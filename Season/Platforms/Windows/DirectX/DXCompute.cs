// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace Season.Platforms.Windows.DirectX;

/// <summary>
/// 1-6 Compute kernel handle on the D3D12 backend: creates a dedicated compute
/// root signature + PSO once from ComputeKernelDesc
/// (kernel-registration model: no main shader, each kernel carries its own layout).
///
/// Root-parameter layout (mechanically generated from Bindings, aligned with the
/// HLSL slot convention described in ComputeBindingType summary):
/// - Params (if present, it must be Bindings[0]) -> root parameter 0:
///   b0 root constants (size/4 32-bit values, with zero upload buffers and zero
///   descriptors, which is the shortest parameter-upload path on D3D12);
/// - every remaining binding -> one single-descriptor table
///   (SRV or UAV range, register indices increment in declaration order).
///   Per-resource single tables are used because descriptor slots come from the
///   DescriptorAllocator free list and are not contiguous; all descriptors live
///   in the shared shader-visible heap (bound once per frame in BeforeRender and
///   reused directly by compute);
/// - if SampledTexture is present, append the s0 linear-clamp static sampler to
///   match the engine sampler semantics of WGSL @binding(15).
///
/// Compilation goes through ShaderCompiler (fxc cs_5_0, with zero warning
/// tolerance, so kernel source must use a single exit to avoid X4000);
/// compile / creation failures are caught by Graphics.CreateComputeKernel, which
/// returns null for graceful degradation during registration.
/// </summary>
internal sealed unsafe class DXComputeKernel : Season.Rendering.ComputeKernel
{
    internal ID3D12RootSignature* RootSignature;

    internal ID3D12PipelineState* PipelineState;

    /// <summary>Bindings[i] -> root-parameter index (Params is always 0; table
    /// parameters increase sequentially).</summary>
    internal readonly int[] RootParamIndex;

    /// <summary>Number of 32-bit values in the Params block (0 when there is no
    /// Params binding).</summary>
    internal readonly uint ParamsNum32Bit;

    /// <summary>PIX event label (pre-baked ANSI + NUL, matching the
    /// Device._passLabels pattern).</summary>
    internal readonly byte[] LabelZ;

    /// <summary>Resolved resource slots reused by the post-dispatch phase,
    /// aligned with Bindings and allocated once.</summary>
    internal readonly object?[] ResolvedScratch;

    /// <summary>Set once Build has thrown. A kernel whose dispatch runs every frame must not re-enter fxc every frame;
    /// the field is the difference between "one bad shader costs one compile" and "one bad shader costs a frame".</summary>
    bool _buildFailed;

    /// <summary>
    /// Step 2: constructor cost, split from creation cost.
    ///
    /// Everything left here is derived from the descriptor alone - label bytes, slot arithmetic, scratch arrays. The
    /// root signature, the fxc pass and the PSO moved into <see cref="Build"/>, so a registration can construct every
    /// kernel it needs in microseconds and let the frame boundary pay for them in one parallel wave. This is what makes
    /// the split legal: <see cref="Season.Rendering.ComputeKernel"/> exposes only Desc and Dispose to effects, so no
    /// effect can observe that its kernel has no pipeline yet.
    /// </summary>
    internal DXComputeKernel(Season.Rendering.ComputeKernelDesc desc)
    {
        Desc = desc;
        LabelZ = System.Text.Encoding.ASCII.GetBytes($"Compute {desc.Name}\0");

        var bindings = desc.Bindings;
        RootParamIndex = new int[bindings.Length];
        ResolvedScratch = new object?[bindings.Length];

        uint paramCount = 0;

        for (int i = 0; i < bindings.Length; i++)
        {
            if (bindings[i].Type == Season.Rendering.ComputeBindingType.Params)
                ParamsNum32Bit = bindings[i].SizeInBytes / 4;

            RootParamIndex[i] = (int)paramCount++;
        }
    }

    /// <summary>
    /// Step 2: true once the PSO exists. The first call builds it, and a failure is remembered.
    ///
    /// <para>Called from the frame-boundary drain after the shader wave has already put the blob in the compiler cache,
    /// and from <c>TryDispatchCompute</c> as the fallback for a kernel registered mid-frame. That fallback is what makes
    /// this whole change a pure optimisation: if a kernel is ever dispatched before any drain has seen it, it simply
    /// pays its own build at the point of use, exactly as it would have before - unless it is <see cref="Season.Rendering.ComputeKernelDesc.Deferred"/>,
    /// whose shader a background lane owns and which therefore declines instead of waiting.</para>
    ///
    /// <para>Returning false is the same observable outcome as pre-Step-2 <c>CreateComputeKernel</c> returning null -
    /// the dispatch is skipped - with one difference worth naming: the effect stays registered and keeps recording
    /// instead of declining at registration. A bad shader therefore looks like "this effect never draws" rather than
    /// "this effect does not exist", and the log line below is what tells the two apart.</para>
    /// </summary>
    internal bool EnsureBuilt()
    {
        if (PipelineState != null) return true;

        if (_buildFailed) return false;

        // Step 5: a Deferred kernel is finished by the background lane, and this method must not become the place that
        // blocks on it. On a cache miss the answer is "not yet": the dispatch is skipped and the frame draws without
        // the feature, which is the state the effect already knows how to present (fog publishes FogOutputReady = false,
        // cloth keeps its static vertices). Building on a miss would move the 19.9 s fxc call into a frame, i.e. undo
        // the one thing the deferral bought.
        if (Desc.Deferred && Desc.Source.Hlsl is { Length: > 0 } source
            && !ShaderCompiler.IsCached(source, Desc.Source.EntryPoint, "cs_5_0", label: Desc.Name))
            return false;

        try
        {
            Build();

            if (PipelineState != null) return true;
        }
        catch (Exception ex)
        {
            DeviceServices.BaseApp.AddLog(LogType.Error,
                $"{DateTime.UtcNow} [CreateComputeKernel] '{Desc.Name}' compilation/creation failed: {ex.Message}");
        }

        _buildFailed = true;

        return false;
    }

    /// <summary>
    /// Creates the root signature and the compute PSO. Both are GPU-side and therefore main-thread work; the one part
    /// of the original constructor that was moved to a worker pool is the fxc call, which now resolves from the cache
    /// the prewarm filled.
    ///
    /// The root-parameter layout mirrors the constructor's slot arithmetic exactly. It is rebuilt here instead of
    /// cached because the descriptor lists live on the stack only for the duration of the call, and re-walking a
    /// handful of bindings is cheaper than owning an array to avoid it.
    /// </summary>
    void Build()
    {
        var bindings = Desc.Bindings;

        // ── Root signature: constants + one single-descriptor table per resource ──
        var ranges = stackalloc DescriptorRange[bindings.Length == 0 ? 1 : bindings.Length];
        var rootParams = stackalloc RootParameter[bindings.Length == 0 ? 1 : bindings.Length];
        uint paramCount = 0, srvReg = 0, uavReg = 0;
        bool hasSampled = false;

        for (int i = 0; i < bindings.Length; i++)
        {
            if (bindings[i].Type == Season.Rendering.ComputeBindingType.Params)
            {
                rootParams[paramCount] = new RootParameter
                {
                    ParameterType = RootParameterType.Type32BitConstants,
                    Constants = new RootConstants
                    {
                        ShaderRegister = 0, // b0
                        RegisterSpace = 0,
                        Num32BitValues = ParamsNum32Bit,
                    },
                    ShaderVisibility = ShaderVisibility.All,
                };
                paramCount++;
                continue;
            }

            // 1-8: 3D types do not open a new register-count domain.
            // RWTexture3D shares the u counter with RWTexture2D, and Texture3D
            // shares the t counter with Texture2D, so the rest of the root
            // signature stays unchanged.
            bool isUav = bindings[i].Type is Season.Rendering.ComputeBindingType.StorageTextureWrite
                or Season.Rendering.ComputeBindingType.StorageBufferReadWrite
                or Season.Rendering.ComputeBindingType.StorageTexture3DWrite;
            hasSampled |= bindings[i].Type is Season.Rendering.ComputeBindingType.SampledTexture
                or Season.Rendering.ComputeBindingType.SampledTexture3D;

            ranges[i] = new DescriptorRange
            {
                RangeType = isUav ? DescriptorRangeType.Uav : DescriptorRangeType.Srv,
                NumDescriptors = 1,
                BaseShaderRegister = isUav ? uavReg++ : srvReg++,
                RegisterSpace = 0,
            };
            rootParams[paramCount] = new RootParameter
            {
                ParameterType = RootParameterType.TypeDescriptorTable,
                DescriptorTable = new RootDescriptorTable
                {
                    NumDescriptorRanges = 1,
                    PDescriptorRanges = ranges + i,
                },
                ShaderVisibility = ShaderVisibility.All,
            };
            paramCount++;
        }

        // s0: engine linear-clamp sampler, provided when SampledTexture exists
        // to match the cross-backend contract
        var staticSampler = new StaticSamplerDesc
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ShaderRegister = 0, // s0
            ShaderVisibility = ShaderVisibility.All,
        };

        var rootSignatureDesc = new RootSignatureDesc
        {
            Flags = RootSignatureFlags.None,
            NumParameters = paramCount,
            PParameters = rootParams,
            NumStaticSamplers = hasSampled ? 1u : 0u,
            PStaticSamplers = hasSampled ? &staticSampler : null,
        };

        using ComPtr<ID3D10Blob> signature = null;
        using ComPtr<ID3D10Blob> error = null;
        // Contract 3-2: this ctor pays three bills and only the middle one (fxc, reported by ShaderCompiler) was on the
        // startup report. Root signature and PSO creation are measured here so a deferral decision sees the whole cost.
        long signatureStart = StartupProfiler.Enabled ? StartupProfiler.Ticks() : 0;

        var result0 = Device.D3D12.SerializeRootSignature(
            &rootSignatureDesc, D3DRootSignatureVersion.Version1, signature.GetAddressOf(), error.GetAddressOf());
        Device.CheckResult(result0);

        ID3D12RootSignature* rootSignature;
        var iid = ID3D12RootSignature.Guid;
        var result = Device.D3dDevice->CreateRootSignature(
            nodeMask: 0, signature.Get().GetBufferPointer(), signature.Get().GetBufferSize(), &iid, (void**)&rootSignature);
        Device.CheckResult(result);
        RootSignature = rootSignature;

        double signatureMs = 0;

        if (signatureStart != 0)
        {
            signatureMs = StartupProfiler.ElapsedMs(signatureStart);

            StartupProfiler.Add("compute.signature", signatureMs);
        }

        // ── CS compilation + compute PSO ──
        ID3D10Blob* csBlob = null;
        try
        {
            long compileStart = StartupProfiler.Enabled ? StartupProfiler.Ticks() : 0;
            csBlob = ShaderCompiler.CompileShaderFromSource(Desc.Source.Hlsl!, Desc.Source.EntryPoint, "cs_5_0", label: Desc.Name);
            double compileMs = compileStart != 0 ? StartupProfiler.ElapsedMs(compileStart) : 0;
            var psoDesc = new ComputePipelineStateDesc
            {
                PRootSignature = RootSignature,
                CS = new ShaderBytecode(csBlob->GetBufferPointer(), csBlob->GetBufferSize()),
            };
            ID3D12PipelineState* pso;
            iid = ID3D12PipelineState.Guid;
            long psoStart = StartupProfiler.Enabled ? StartupProfiler.Ticks() : 0;
            var psoResult = Device.D3dDevice->CreateComputePipelineState(&psoDesc, &iid, (void**)&pso);
            Device.CheckResult(psoResult);
            PipelineState = pso;

            // Contract 3-2: one row per kernel, named after the kernel, so a slow row is traceable instead of every row
            // reading "cs_5_0 CSMain". The detail carries the other two bills of this ctor: the row plus them is the cost.
            if (psoStart != 0)
            {
                double psoMs = StartupProfiler.ElapsedMs(psoStart);

                StartupProfiler.Item("pso", $"compute {Desc.Name}", psoMs,
                    csBlob != null ? (long)csBlob->GetBufferSize() : 0,
                    $"cs_ms={compileMs:0.###} sig_ms={signatureMs:0.###}");

                StartupProfiler.Add("compute.pso", psoMs);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            if (csBlob != null) csBlob->Release();
        }
    }

    public override void Dispose()
    {
        // Unregistration prevents further scheduling; already-recorded commands still hold the native handle, so it is released after this frame's fence completes.
        nint pipeline = (nint)PipelineState, signature = (nint)RootSignature;
        PipelineState = null;
        RootSignature = null;
        if (pipeline == 0 && signature == 0) return;
        Device.EnqueueDeferredRelease(Device.GetCurrentRetireFenceValue(), () =>
        {
            if (pipeline != 0) ((ID3D12PipelineState*)pipeline)->Release();
            if (signature != 0) ((ID3D12RootSignature*)signature)->Release();
        });
    }
}

/// <summary>
/// 1-6 Compute storage buffer on the D3D12 backend: default-heap buffer with
/// AllowUnorderedAccess, plus paired SRV/UAV raw views
/// (ByteAddressBuffer semantics, see the ComputeBindingType contract).
/// State tracking is centralized in DispatchCompute: transition to
/// NonPixelShaderResource before Read binding, to UnorderedAccess before
/// ReadWrite binding, and insert a UAV barrier after dispatch
/// for same-frame kernel-chain dependencies.
///
/// A7c adds a second lifetime mode next to "owns a resource": the borrowed read-only view (<see cref="CreateReadOnlyView"/>
/// and the alias constructor). It owns no resource, uploads nothing and records no barrier, because what it points at is an upload-heap
/// vertex buffer that the engine maps directly and that the D3D12 runtime keeps in GenericRead for the resource's whole life. All three
/// omissions are mandatory, not an optimization: a barrier on an upload heap is an invalid transition, there is no UAV descriptor behind
/// a read-only alias so a ReadWrite binding would be a lie the heap cannot back, and releasing the resource here would leave the IA
/// binding dangling while the instance buffer still uses it. The instance buffer's owner releases it; this object retires only its SRV
/// descriptor, together with the resource's fence value, so no later allocation can overwrite a descriptor an in-flight frame still reads.
/// </summary>
internal sealed unsafe class DXStorageBuffer : Season.Rendering.StorageBuffer
{
    internal ID3D12Resource* Resource;

    internal int SrvDescriptorID = -1;

    internal int UavDescriptorID = -1;

    internal GpuDescriptorHandle SrvGpuHandle;

    internal GpuDescriptorHandle UavGpuHandle;

    /// <summary>P0: the structured view a compute-written, vertex-read block needs. The compute path binds the raw SRV/UAV pair below,
    /// because a kernel addresses the block as bytes; the vertex shader declares the palette as <c>StructuredBuffer&lt;float4x4&gt;</c>,
    /// and D3D12 types a buffer view by its element stride, so the raw view cannot be reused for it. Owned only by buffers created with
    /// <see cref="Season.Rendering.StorageBufferUsage.ComputeAndVertexShaderRead"/>; -1 everywhere else.</summary>
    internal int VertexSrvDescriptorID = -1;

    internal GpuDescriptorHandle VertexSrvGpuHandle;

    internal ResourceStates CurrentState = ResourceStates.Common;

    /// <summary>
    /// 1-8: upload-heap staging for CPU -> GPU writes. Created on demand during
    /// the first UpdateStorageBuffer call and kept alive to avoid creating and
    /// destroying a committed resource on every update. GPU-only default heaps
    /// cannot be mapped, so staging + copy is required.
    /// 2-4 Step 0: N-buffered by in-flight frame count. A single persistent heap
    /// is valid only for low-frequency updates. With per-frame uploads, CPU Map
    /// writes for the current frame would contend for the same memory with
    /// CopyBufferRegion commands from still-in-flight frames, with no
    /// synchronization, causing the GPU to read next-frame data. Slots are
    /// selected by Device.FrameIndex, and safety comes from the ring fence at the
    /// frame-loop entry: before re-entering slot i, the engine has already waited
    /// for the previous GPU work that used that slot to finish. This matches the
    /// convention used by the engine's other per-frame buffers
    /// (instance / bone / light CB).
    /// </summary>
    readonly IntPtr[] _uploadHeaps = new IntPtr[Device.frameCount];

    readonly ulong[] _uploadHeapCapacities = new ulong[Device.frameCount];

    readonly uint _alignedSize;

    /// <summary>
    /// A7c: this handle only aliases somebody else's resource (the IA instance stream) instead of owning one. It then skips every
    /// owning behavior: no resource release, no state barrier, no upload, and no bound-check against a capacity it does not have.
    /// </summary>
    readonly bool _borrowed;

    /// <summary>
    /// A7c: wraps an existing buffer as a read-only storage view so the culling kernel can read the stream the engine already
    /// uploads for instanced drawing. Before this, every frame copied the very same 80-bytes-per-instance bytes into a second,
    /// dedicated storage buffer: the cull input and the IA instance stream are bit-identical by contract, so the copy moved no new
    /// information to the GPU and only cost an extra staging write plus a copy command per group (per primitive stream for a glTF model).
    ///
    /// The source is a vertex buffer that the engine writes through Map/Unmap and that the D3D12 runtime keeps in GenericRead for its
    /// whole lifetime (an upload heap cannot leave that state). GenericRead already includes NON_PIXEL_SHADER_RESOURCE, so binding the
    /// raw SRV needs neither a transition nor a state tracked here -- which is exactly why the alias must never record a barrier itself.
    /// </summary>
    internal static DXStorageBuffer CreateReadOnlyView(ID3D12Resource* resource, uint sizeInBytes)
        => new(resource, sizeInBytes);

    /// <summary>
    /// A7c: the alias constructor. <paramref name="sizeInBytes"/> is the real resource size, never a rounded-up capacity:
    /// a raw SRV's NumElements must stay inside the resource the engine created for vertex input.
    /// </summary>
    internal DXStorageBuffer(ID3D12Resource* resource, uint sizeInBytes)
    {
        SizeInBytes = sizeInBytes;
        // Read-only by construction: there is no UAV descriptor and the owner keeps writing the resource through Map, so anything
        // above a plain read binding would be a lie the descriptor heap could not back anyway.
        Usage = Season.Rendering.StorageBufferUsage.Compute;
        _borrowed = true;
        _alignedSize = (sizeInBytes + 15u) & ~15u;
        Resource = resource;
        CurrentState = ResourceStates.GenericRead;
        CreateSrv(sizeInBytes / 4);
    }

    internal DXStorageBuffer(uint sizeInBytes, Season.Rendering.StorageBufferUsage usage)
    {
        // Raw views are addressed in 4-byte elements, so round capacity up to
        // 16 bytes to match Params alignment granularity and avoid tail overruns.
        uint alignedSize = (sizeInBytes + 15u) & ~15u;
        SizeInBytes = sizeInBytes;
        Usage = usage;
        _alignedSize = alignedSize;

        // A0.1: D3D12 needs no extra creation flags. Any buffer can be transitioned into INDIRECT_ARGUMENT,
        // and AllowUnorderedAccess already allows compute writes, so on this backend usage only affects
        // the admission test of TryPrepareIndirectArgs, not the resource description.
        var heapProps = new HeapProperties(HeapType.Default);
        var bufferDesc = new ResourceDesc
        {
            Dimension = ResourceDimension.Buffer,
            Width = alignedSize,
            Height = 1,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = Format.FormatUnknown,
            SampleDesc = new SampleDesc(1, 0),
            Layout = TextureLayout.LayoutRowMajor,
            Flags = ResourceFlags.AllowUnorderedAccess,
        };

        ID3D12Resource* resource;
        var iid = ID3D12Resource.Guid;
        var hr = Device.D3dDevice->CreateCommittedResource(
            &heapProps, HeapFlags.None, &bufferDesc,
            ResourceStates.Common, null, &iid, (void**)&resource);
        Device.CheckResult(hr);
        Resource = resource;

        uint numElements = alignedSize / 4;

        CreateSrv(numElements);

        // P0: only the palette usage also gets a structured view (see VertexSrvDescriptorID). Every other usage stops at the raw pair,
        // because no shader indexes them as typed elements.
        if (usage == Season.Rendering.StorageBufferUsage.ComputeAndVertexShaderRead)
            CreateVertexSrv(Math.Max(1u, alignedSize / (uint)Season.Rendering.StorageBuffer.VertexShaderReadElementBytes));

        UavDescriptorID = Device.DescriptorAllocator.Allocate(DescriptorKind.Compute);
        UavGpuHandle = Device.SrvHeapManager.GetGpuHandle(UavDescriptorID);
        var uavDesc = new UnorderedAccessViewDesc
        {
            Format = Format.FormatR32Typeless,
            ViewDimension = UavDimension.Buffer,
            Buffer = new BufferUav
            {
                FirstElement = 0,
                NumElements = numElements,
                StructureByteStride = 0,
                CounterOffsetInBytes = 0,
                Flags = BufferUavFlags.Raw,
            },
        };
        Device.D3dDevice->CreateUnorderedAccessView(Resource, null, &uavDesc, Device.SrvHeapManager.GetCpuHandle(UavDescriptorID));
    }

    /// <summary>Raw (ByteAddressBuffer) SRV over <see cref="Resource"/>, shared by the owning and the alias constructor: the two differ in
    /// who owns the resource and how many elements are addressable, not in the view type the kernels expect.</summary>
    void CreateSrv(uint numElements)
    {
        SrvDescriptorID = Device.DescriptorAllocator.Allocate(DescriptorKind.Compute);
        SrvGpuHandle = Device.SrvHeapManager.GetGpuHandle(SrvDescriptorID);
        const uint DefaultShader4ComponentMapping = 0x00001688;
        var srvDesc = new ShaderResourceViewDesc
        {
            Format = Format.FormatR32Typeless,
            ViewDimension = SrvDimension.Buffer,
            Shader4ComponentMapping = DefaultShader4ComponentMapping,
            Buffer = new BufferSrv
            {
                FirstElement = 0,
                NumElements = numElements,
                StructureByteStride = 0,
                Flags = BufferSrvFlags.Raw,
            },
        };
        Device.D3dDevice->CreateShaderResourceView(Resource, &srvDesc, Device.SrvHeapManager.GetCpuHandle(SrvDescriptorID));
    }

    /// <summary>
    /// P0: typed view over the same resource, stride <see cref="Season.Rendering.StorageBuffer.VertexShaderReadElementBytes"/>.
    /// Format must stay UNKNOWN and the stride non-zero: that pair is exactly what makes a buffer SRV a StructuredBuffer descriptor
    /// rather than a ByteAddressBuffer one, and mismatching it against the shader declaration is a debug-layer error rather than a
    /// silent reinterpreting.
    /// </summary>
    void CreateVertexSrv(uint numElements)
    {
        VertexSrvDescriptorID = Device.DescriptorAllocator.Allocate(DescriptorKind.Compute);
        VertexSrvGpuHandle = Device.SrvHeapManager.GetGpuHandle(VertexSrvDescriptorID);
        const uint DefaultShader4ComponentMapping = 0x00001688;
        var srvDesc = new ShaderResourceViewDesc
        {
            Format = Format.FormatUnknown,
            ViewDimension = SrvDimension.Buffer,
            Shader4ComponentMapping = DefaultShader4ComponentMapping,
            Buffer = new BufferSrv
            {
                FirstElement = 0,
                NumElements = numElements,
                StructureByteStride = (uint)Season.Rendering.StorageBuffer.VertexShaderReadElementBytes,
                Flags = BufferSrvFlags.None,
            },
        };
        Device.D3dDevice->CreateShaderResourceView(Resource, &srvDesc, Device.SrvHeapManager.GetCpuHandle(VertexSrvDescriptorID));
    }

    /// <summary>
    /// 1-8: CPU data upload path. This is the escape hatch for the 128-byte
    /// Params limit and carries large DDGI-scale constant blocks.
    /// Records into the provided command list:
    /// current state -> CopyDest -> CopyBufferRegion -> NonPixelShaderResource.
    /// The last state is the steady state for StorageBufferRead. If the buffer is
    /// later bound for ReadWrite, the gated transition in DispatchCompute moves it
    /// to UnorderedAccess again, so the state machine remains self-consistent.
    /// The caller (Graphics.UpdateStorageBuffer) must record this outside
    /// render/compute passes.
    /// From 2-4 Step 0 onward, this supports per-frame calls: staging heaps are
    /// slotted by FrameIndex, as described in the _uploadHeaps comment.
    /// </summary>
    internal void Upload(ID3D12GraphicsCommandList* cmd, ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
            return;
        // A7c: an alias has neither a destination state of its own nor an upload heap to write from; the owner (the instance buffer)
        // is the one that publishes these bytes. Silently ignoring the call is deliberate: it keeps a mistaken caller from recording a
        // CopyBufferRegion into an upload-heap vertex buffer, which the debug layer would only report after the fact.
        if (_borrowed)
            return;
        if ((uint)data.Length > _alignedSize)
            throw new ArgumentException(
                $"UpdateStorageBuffer: data size {data.Length}B exceeds buffer capacity {_alignedSize}B.");

        int slot = (int)(Device.FrameIndex % (uint)_uploadHeaps.Length);
        var uploadHeap = EnsureUploadHeap(slot, _alignedSize);

        void* mapped = null;
        Device.CheckResult(uploadHeap->Map(0, null, &mapped));
        fixed (byte* src = data)
        {
            System.Runtime.CompilerServices.Unsafe.CopyBlock(mapped, src, (uint)data.Length);
        }
        uploadHeap->Unmap(0, null);

        TransitionTo(cmd, ResourceStates.CopyDest);
        cmd->CopyBufferRegion(Resource, 0, uploadHeap, 0, (ulong)data.Length);
        TransitionTo(cmd, ResourceStates.AllShaderResource);
    }

    ID3D12Resource* EnsureUploadHeap(int slot, ulong requiredBytes)
    {
        var existing = (ID3D12Resource*)_uploadHeaps[slot];
        if (existing != null && _uploadHeapCapacities[slot] >= requiredBytes)
            return existing;

        if (existing != null)
        {
            existing->Release();
            _uploadHeaps[slot] = IntPtr.Zero;
            _uploadHeapCapacities[slot] = 0;
        }

        var heapProps = new HeapProperties(HeapType.Upload);
        var bufferDesc = new ResourceDesc
        {
            Dimension = ResourceDimension.Buffer,
            Width = requiredBytes,
            Height = 1,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = Format.FormatUnknown,
            SampleDesc = new SampleDesc(1, 0),
            Layout = TextureLayout.LayoutRowMajor,
            Flags = ResourceFlags.None,
        };

        ID3D12Resource* upload;
        var iid = ID3D12Resource.Guid;
        Device.CheckResult(Device.D3dDevice->CreateCommittedResource(
            &heapProps, HeapFlags.None, &bufferDesc,
            ResourceStates.GenericRead, null, &iid, (void**)&upload));

        _uploadHeaps[slot] = (IntPtr)upload;
        _uploadHeapCapacities[slot] = requiredBytes;
        return upload;
    }

    /// <summary>Gated state transition used by DispatchCompute; zero cost when the
    /// state is unchanged.</summary>
    internal void TransitionTo(ID3D12GraphicsCommandList* cmd, ResourceStates newState)
    {
        // A7c: the aliased vertex buffer never changes state -- an upload heap must stay in GenericRead for its whole life, and that
        // state already covers compute SRV reads. Recording a barrier here would be an invalid transition on an upload heap.
        if (_borrowed || CurrentState == newState)
            return;

        var barrier = Device.InitTransition(Resource, CurrentState, newState);
        cmd->ResourceBarrier(1, &barrier);
        CurrentState = newState;
    }

    public override void Dispose()
    {
        if (Resource == null && SrvDescriptorID < 0 && UavDescriptorID < 0 && VertexSrvDescriptorID < 0) return;
        // A7c: the alias does not own the resource (the instance buffer releases it), so only the descriptor it allocated is ours.
        nint resource = _borrowed ? 0 : (nint)Resource;
        var uploads = (IntPtr[])_uploadHeaps.Clone();
        int srv = SrvDescriptorID, uav = UavDescriptorID, vertexSrv = VertexSrvDescriptorID;
        var allocator = Device.DescriptorAllocator;
        Resource = null;
        SrvDescriptorID = UavDescriptorID = VertexSrvDescriptorID = -1;
        SrvGpuHandle = UavGpuHandle = VertexSrvGpuHandle = default;
        Array.Clear(_uploadHeaps);
        Array.Clear(_uploadHeapCapacities);
        // The resource and the descriptors retire together, to prevent a later allocation from overwriting the SRV/UAV bound by an in-flight frame.
        Device.EnqueueDeferredRelease(Device.GetCurrentRetireFenceValue(), () =>
        {
            if (resource != 0) ((ID3D12Resource*)resource)->Release();
            foreach (var upload in uploads)
                if (upload != IntPtr.Zero) ((ID3D12Resource*)upload)->Release();
            if (srv >= 0) allocator.Free(srv);
            if (uav >= 0) allocator.Free(uav);
            if (vertexSrv >= 0) allocator.Free(vertexSrv);
        });
    }
}
