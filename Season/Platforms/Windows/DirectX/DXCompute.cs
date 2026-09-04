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

    internal DXComputeKernel(Season.Rendering.ComputeKernelDesc desc)
    {
        Desc = desc;
        LabelZ = System.Text.Encoding.ASCII.GetBytes($"Compute {desc.Name}\0");

        var bindings = desc.Bindings;
        RootParamIndex = new int[bindings.Length];
        ResolvedScratch = new object?[bindings.Length];

        // ── Root signature: constants + one single-descriptor table per resource ──
        var ranges = stackalloc DescriptorRange[bindings.Length == 0 ? 1 : bindings.Length];
        var rootParams = stackalloc RootParameter[bindings.Length == 0 ? 1 : bindings.Length];
        uint paramCount = 0, srvReg = 0, uavReg = 0;
        bool hasSampled = false;

        for (int i = 0; i < bindings.Length; i++)
        {
            if (bindings[i].Type == Season.Rendering.ComputeBindingType.Params)
            {
                ParamsNum32Bit = bindings[i].SizeInBytes / 4;
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
                RootParamIndex[i] = (int)paramCount++;
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
            RootParamIndex[i] = (int)paramCount++;
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
        var result0 = Device.D3D12.SerializeRootSignature(
            &rootSignatureDesc, D3DRootSignatureVersion.Version1, signature.GetAddressOf(), error.GetAddressOf());
        Device.CheckResult(result0);

        ID3D12RootSignature* rootSignature;
        var iid = ID3D12RootSignature.Guid;
        var result = Device.D3dDevice->CreateRootSignature(
            nodeMask: 0, signature.Get().GetBufferPointer(), signature.Get().GetBufferSize(), &iid, (void**)&rootSignature);
        Device.CheckResult(result);
        RootSignature = rootSignature;

        // ── CS compilation + compute PSO ──
        ID3D10Blob* csBlob = ShaderCompiler.CompileShaderFromSource(desc.Source.Hlsl!, desc.Source.EntryPoint, "cs_5_0");
        try
        {
            var psoDesc = new ComputePipelineStateDesc
            {
                PRootSignature = RootSignature,
                CS = new ShaderBytecode(csBlob->GetBufferPointer(), csBlob->GetBufferSize()),
            };
            ID3D12PipelineState* pso;
            iid = ID3D12PipelineState.Guid;
            var psoResult = Device.D3dDevice->CreateComputePipelineState(&psoDesc, &iid, (void**)&pso);
            Device.CheckResult(psoResult);
            PipelineState = pso;
        }
        finally
        {
            csBlob->Release();
        }
    }

    public override void Dispose()
    {
        // Unregister path (called after UnregisterCompute): the caller must
        // guarantee the kernel is no longer referenced by any in-flight frame,
        // matching the direct-release semantics of DXTexture.Dispose.
        if (PipelineState != null)
        {
            PipelineState->Release();
            PipelineState = null;
        }
        if (RootSignature != null)
        {
            RootSignature->Release();
            RootSignature = null;
        }
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
/// </summary>
internal sealed unsafe class DXStorageBuffer : Season.Rendering.StorageBuffer
{
    internal ID3D12Resource* Resource;

    internal readonly int SrvDescriptorID;

    internal readonly int UavDescriptorID;

    internal GpuDescriptorHandle SrvGpuHandle;

    internal GpuDescriptorHandle UavGpuHandle;

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

    internal DXStorageBuffer(uint sizeInBytes)
    {
        // Raw views are addressed in 4-byte elements, so round capacity up to
        // 16 bytes to match Params alignment granularity and avoid tail overruns.
        uint alignedSize = (sizeInBytes + 15u) & ~15u;
        SizeInBytes = sizeInBytes;
        _alignedSize = alignedSize;

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

        SrvDescriptorID = Device.DescriptorAllocator.Allocate();
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

        UavDescriptorID = Device.DescriptorAllocator.Allocate();
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
        TransitionTo(cmd, ResourceStates.NonPixelShaderResource);
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
        if (CurrentState == newState)
            return;

        var barrier = Device.InitTransition(Resource, CurrentState, newState);
        cmd->ResourceBarrier(1, &barrier);
        CurrentState = newState;
    }

    public override void Dispose()
    {
        if (Resource != null)
        {
            Resource->Release();
            Resource = null;
        }
        for (int i = 0; i < _uploadHeaps.Length; i++)
        {
            var heap = (ID3D12Resource*)_uploadHeaps[i];
            if (heap == null)
                continue;
            heap->Release();
            _uploadHeaps[i] = IntPtr.Zero;
            _uploadHeapCapacities[i] = 0;
        }
        Device.DescriptorAllocator.Free(SrvDescriptorID);
        Device.DescriptorAllocator.Free(UavDescriptorID);
    }
}
