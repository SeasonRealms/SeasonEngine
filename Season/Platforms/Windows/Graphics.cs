// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Season.Platforms.Windows.DirectX;
using Silk.NET.Direct3D12;

namespace Season.Platforms.Windows;

internal unsafe class Graphics : IGraphics
{
    // ── Text GPU Instancing: lightweight ITextureHolder (no GPU resources) ──
    internal sealed class TextGlyphHolder : ITextureHolder
    {
        public Texture Texture { get; set; } = new Texture();
    }

    /// <summary>
    /// GPU instancing state for a single Texts control.
    /// Each Texts owns an independent glyph buffer (t5) and per-frame instance buffer (slot 1).
    /// </summary>
    internal unsafe struct TextInstanceState
    {
        public TextGlyphBufferLease GlyphLease;
        public ID3D12Resource* GlyphBuffer;
        public byte* GlyphMappedPtr;
        public GpuDescriptorHandle GlyphSrv;
        public int GlyphDescriptorId;
        public int GlyphCapacity;
        public int GlyphAtlasVersionBuilt;
        public bool GlyphDirty;
        public bool CanDraw;
        public ID3D12Resource*[] InstanceBuffers;     // slot 1: per-frame InstanceTransformData[]
        public VertexBufferView[] InstanceBufferViews;
        public uint InstanceFrameMask;
        public int InstanceCount;
        public int InstanceCapacity;                  // Allocated capacity (grown geometrically), >= InstanceCount
    }

    internal unsafe struct TextDrawParamsArena
    {
        const uint DrawParamsStride = 256;

        public ID3D12Resource* Buffer;
        public byte* MappedPtr;
        public int Capacity;
        public int Count;

        public void Init(int capacity)
        {
            Capacity = Math.Max(1, capacity);
            Buffer = DirectX.Device.ResourceManager.CreateBuffer(
                HeapType.Upload,
                (ulong)(DrawParamsStride * Capacity),
                ResourceStates.GenericRead);

            void* pMapped;
            Buffer->Map(0, null, &pMapped);
            MappedPtr = (byte*)pMapped;
            Count = 0;
        }

        public void Reset()
        {
            Count = 0;
        }

        public ulong Allocate(in TextDrawParams value)
        {
            if (Buffer == null || Count >= Capacity)
                return 0;

            var dst = MappedPtr + Count * DrawParamsStride;
            Unsafe.Write(dst, value);

            ulong gpuAddress = Buffer->GetGPUVirtualAddress() + (ulong)(Count * DrawParamsStride);
            Count++;
            return gpuAddress;
        }
    }

    internal unsafe struct TextFrameResources
    {
        public TextDrawParamsArena DrawParamsArena;

        public void Init(int drawParamsCapacity)
        {
            DrawParamsArena.Init(drawParamsCapacity);
        }

        public void Reset()
        {
            DrawParamsArena.Reset();
        }
    }

    readonly Dictionary<Texts, TextInstanceState> _textInstances = new();
    // _textInstances is accessed concurrently by the background loading thread (LoadTexts),
    // the UI thread (DisposeTexts), and the render thread (UpdateTexts/DrawTexts).
    // All reads and writes must hold the lock, otherwise the dictionary may be corrupted
    // or resources may be released twice.
    readonly object _textInstancesLock = new();
    readonly TextGlyphBufferPool _textGlyphBufferPool = new();

    // ── Shared draw resources for Text GPU Instancing ──
    ID3D12Resource* _textMatrixBuffer;       // b0: identity matrix (instanced text does not use b0 world, but it must still be bound)
    byte* _mappedTextMatrixBuffer;
    TextFrameResources[] _textFrameResources;

    readonly GlyphAtlasManager<DXTexture> _glyphAtlas = new(
        2048, 2048,
        createAtlasTexture: (w, h) => DXTexture.CreateEmpty(w, h, "TextAtlas"),
        uploadFullPixels: (tex, pixels) => tex.UploadPixels(pixels),
        uploadSubRects: (tex, pixels, atlasW, atlasH, rects) =>
        {
            var dxRects = new TextureUploadRect[rects.Length];
            for (int i = 0; i < rects.Length; i++)
                dxRects[i] = new TextureUploadRect(rects[i].X, rects[i].Y, rects[i].Width, rects[i].Height);
            DirectX.Device.textureUploadBatch.ExecuteSubRectUpload(
                tex, pixels, atlasW, atlasH, dxRects,
                DirectX.Device.CopyGraphicsCommandList, DirectX.Device.CopyCommandQueue);
        },
        getCurrentFrameIndex: () => DirectX.Device.FrameIndex);

    Dictionary<string, DXTexture> DictionaryDXTexture = new Dictionary<string, DXTexture>();

    Dictionary<(string, long), DXSprite2D> DictionarySprite = new Dictionary<(string, long), DXSprite2D>();

    Dictionary<(string, long), DXSprite3D> DictionarySprite3D = new Dictionary<(string, long), DXSprite3D>();

    Dictionary<string, Task<DXModel>> DictionaryModelResource = new Dictionary<string, Task<DXModel>>();

    Dictionary<(string, long), DXModel> DictionaryModel = new Dictionary<(string, long), DXModel>();
    Dictionary<(string, long), DXInstancedModel> DictionaryInstancedModel = new Dictionary<(string, long), DXInstancedModel>();

    Dictionary<(string, long), DXMesh3D> DictionaryMesh3D = new Dictionary<(string, long), DXMesh3D>();
    Dictionary<(string, long), DXInstancedMesh3D> DictionaryInstancedMesh3D = new Dictionary<(string, long), DXInstancedMesh3D>();

    Season.Rendering.RenderTarget _outlineMaskTarget;
    bool _outline2DFrameActive;
    float _outline2DFrameWidth;

    // ── Shape (procedural geometry) ──
    Dictionary<(Season.Controls.ShapeType, int, int, int), DXTexture> DictionaryShapeTexture = new();
    Dictionary<(Season.Controls.ShapeType, long), DXSprite2D> DictionaryShape = new();

    public void Init()
    {
        DirectX.Device.White = DXTexture.GetOrCreate("White", null);
        ExecuteUpload();

        // ── Shared draw resources for Text GPU Instancing ──
        _textMatrixBuffer = DirectX.Device.ResourceManager.CreateConstantBuffer(
            (uint)Unsafe.SizeOf<MatrixBuffer>(), out _mappedTextMatrixBuffer);
        var identityMatrix = new MatrixBuffer
        {
            World = Matrix4x4.Transpose(Matrix4x4.Identity),
            View = Matrix4x4.Identity,
            Projection = Matrix4x4.Identity
        };
        Unsafe.Write(_mappedTextMatrixBuffer, identityMatrix);

        _textFrameResources = new TextFrameResources[(int)DirectX.Device.frameCount];
        for (int i = 0; i < _textFrameResources.Length; i++)
        {
            _textFrameResources[i].Init(drawParamsCapacity: 2048);
        }
    }

    internal void BeginTextFrame()
    {
        if (_textFrameResources == null || _textFrameResources.Length == 0)
            return;

        _textFrameResources[(int)DirectX.Device.FrameIndex].Reset();
    }

    static TextGlyphData CreateHiddenGlyphData()
    {
        return new TextGlyphData
        {
            UvRect = Vector4.Zero,
            Color = Vector4.One,
            Metrics = Vector4.Zero,
        };
    }

    static InstanceTransformData CreateHiddenInstanceData()
    {
        return new InstanceTransformData
        {
            Row0 = Vector4.Zero,
            Row1 = Vector4.Zero,
            Row2 = Vector4.Zero,
            Row3 = Vector4.Zero,
            MorphWeights = Vector4.Zero,
        };
    }

    bool EnsureGlyphBufferCapacity(ref TextInstanceState state, int requiredCount)
    {
        requiredCount = Math.Max(requiredCount, 1);
        if (state.GlyphLease != null && state.GlyphBuffer != null && state.GlyphMappedPtr != null && state.GlyphCapacity >= requiredCount && state.GlyphSrv.Ptr != 0)
            return true;

        ulong fence = DirectX.Device.GetCurrentRetireFenceValue();
        if (state.GlyphLease != null)
        {
            var oldLease = state.GlyphLease;
            EnqueueDeferredRelease(fence, () => _textGlyphBufferPool.Return(oldLease));
        }

        var lease = _textGlyphBufferPool.Rent(requiredCount);
        state.GlyphLease = lease;
        state.GlyphBuffer = lease.Buffer;
        state.GlyphMappedPtr = lease.MappedPtr;
        state.GlyphDescriptorId = lease.DescriptorId;
        state.GlyphSrv = lease.SrvHandle;
        state.GlyphCapacity = lease.Capacity;
        return true;
    }

    /// <summary>Ensures per-frame instance buffer capacity with geometric growth so incremental
    /// appends amortize buffer creation to O(1).
    /// New buffers do not copy old contents: the caller must clear InstanceFrameMask and set
    /// GlyphDirty so the next UpdateTexts rebuilds all instances with a full write across all
    /// frames (the anti-flicker invariant).</summary>
    bool EnsureInstanceBufferCapacity(ref TextInstanceState state, int requiredCount)
    {
        requiredCount = Math.Max(requiredCount, 1);
        if (state.InstanceBuffers != null && state.InstanceBufferViews != null && state.InstanceCapacity >= requiredCount)
            return true;

        int frameCount = state.InstanceBuffers?.Length ?? (int)DirectX.Device.frameCount;
        if (frameCount <= 0)
            return false;

        int capacity = Math.Max(requiredCount, Math.Max(state.InstanceCapacity * 2, 64));

        var seed = new InstanceTransformData[capacity];
        var hidden = CreateHiddenInstanceData();
        for (int i = 0; i < capacity; i++)
            seed[i] = hidden;

        var buffers = new ID3D12Resource*[frameCount];
        var views = new VertexBufferView[frameCount];
        for (int fi = 0; fi < frameCount; fi++)
        {
            buffers[fi] = DirectX.Device.CreateVertexBuffer(seed, out views[fi]);
            if (buffers[fi] == null)
            {
                // Partial creation failed: reclaim the created buffers, keep the old capacity,
                // and let the caller fall back to a full rebuild.
                ReleaseInstanceBuffersDeferred(buffers, DirectX.Device.GetCurrentRetireFenceValue());
                return false;
            }
        }

        var previousBuffers = state.InstanceBuffers;
        state.InstanceBuffers = buffers;
        state.InstanceBufferViews = views;
        state.InstanceCapacity = capacity;
        state.InstanceFrameMask = 0;

        // Old buffers may still be referenced by in-flight frames and must be released later.
        ReleaseInstanceBuffersDeferred(previousBuffers, DirectX.Device.GetCurrentRetireFenceValue());
        return true;
    }

    /// <summary>The InstanceBuffers array is shared by all struct copies of the same Texts.
    /// Clear each slot immediately after enqueueing so duplicate releases (for example,
    /// a race between LoadTexts rebuild and DisposeTexts) automatically become no-ops.</summary>
    void ReleaseInstanceBuffersDeferred(ID3D12Resource*[] buffers, ulong fence)
    {
        if (buffers == null)
            return;

        for (int i = 0; i < buffers.Length; i++)
        {
            var instanceBuffer = buffers[i];
            buffers[i] = null;
            if (instanceBuffer != null)
            {
                var captured = instanceBuffer;
                EnqueueDeferredRelease(fence, () => captured->Release());
            }
        }
    }

    void ReleaseTextInstanceResources(in TextInstanceState state, ulong fence)
    {
        ReleaseInstanceBuffersDeferred(state.InstanceBuffers, fence);

        if (state.GlyphLease != null)
        {
            var glyphLease = state.GlyphLease;
            EnqueueDeferredRelease(fence, () => _textGlyphBufferPool.Return(glyphLease));
        }
    }

    bool TryGetTextInstanceState(Texts texts, out TextInstanceState state)
    {
        lock (_textInstancesLock)
            return _textInstances.TryGetValue(texts, out state);
    }

    void StoreTextInstanceState(Texts texts, in TextInstanceState state)
    {
        lock (_textInstancesLock)
        {
            // Do not write back after DisposeTexts; otherwise resources already queued for
            // release may be "revived" into the dictionary and leave dangling pointers.
            if (_textInstances.ContainsKey(texts))
                _textInstances[texts] = state;
        }
    }

    /// <summary>The deferred release queue has moved to <c>DirectX.Device</c> (the same mechanism
    /// as VK Device.EnqueueDeferredRelease). This local helper is a private convenience forwarder
    /// used by Graphics runtime destruction paths; the DirectX layer can call
    /// <c>Device.EnqueueDeferredRelease</c> directly (for example, when reclaiming old primitives
    /// after wireframe shell geometry grows in capacity).</summary>
    void EnqueueDeferredRelease(ulong fenceValue, Action releaseAction)
        => DirectX.Device.EnqueueDeferredRelease(fenceValue, releaseAction);

    /// <summary>Executes deferred releases whose GPU fence has already passed; when force=true,
    /// executes all of them after the GPU has been made idle first.
    /// Call sites (mirroring VK Device.PumpDeferredReleases):
    ///   - each frame in the WindowsApp frame loop after AfterRender with force disabled
    ///     (see the main render loop in WindowsApp.cs);
    ///   - the WindowsApp shutdown path after <c>DirectX.Device.WaitForGpu()</c> +
    ///     <c>ResetAllAllocatorsForShutdown()</c> with force=true
    ///     (see the shutdown flow in WindowsApp.cs).
    /// The queue itself and its implementation live in DirectX.Device.</summary>
    public void PumpDeferredReleases(bool force = false)
        => DirectX.Device.PumpDeferredReleases(force);

    // ── 1-6 Compute foundation (kernel registration model, contract in IGraphics/Compute.cs) ──
    // Dispatch is recorded into the per-frame GraphicsCommandList (BeforeRender has already
    // opened it and bound the shared SRV heap). The FrameStart phase happens inside
    // FrameSchedule.Execute before the first render pass. All synchronization is contained
    // inside DispatchCompute (transition + UAV barrier; queue order provides synchronization).

    public bool ComputeSupported => DirectX.Device.D3dDevice != null;

    /// <summary>Parameter-level validation is centralized here (same rules on all backends):
    /// missing HLSL source returns null for graceful fallback; invalid binding declarations
    /// throw exceptions (programming error); fxc compilation or PSO creation failures are
    /// logged and return null (graceful degradation during registration, with no platform residue).</summary>
    public Season.Rendering.ComputeKernel CreateComputeKernel(Season.Rendering.ComputeKernelDesc desc)
    {
        if (!ComputeSupported || string.IsNullOrEmpty(desc.Source.Hlsl))
            return null;

        var bindings = desc.Bindings;
        desc.ValidateWorkgroupSize();
        for (int i = 0; i < bindings.Length; i++)
        {
            if (bindings[i].Type != Season.Rendering.ComputeBindingType.Params)
                continue;
            if (i != 0)
                throw new ArgumentException($"[CreateComputeKernel] '{desc.Name}': Params must be located at Bindings[0].");
            var size = bindings[i].SizeInBytes;
            if (size == 0 || size % 16 != 0 || size > 128)
                throw new ArgumentException($"[CreateComputeKernel] '{desc.Name}': Params must be 16B-aligned and <= 128B (got {size}).");
        }

        try
        {
            return new DXComputeKernel(desc);
        }
        catch (Exception ex)
        {
            DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} [CreateComputeKernel] '{desc.Name}' compilation/creation failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Storage textures are registered into DictionaryDXTexture by name
    /// (LoadSprite2D hits them, AddRefs, and skips file loading). Sprite2D consumes them by
    /// name without changes, matching the dual-end registration semantics of DictionaryWGPUTexture
    /// on the Web backend.
    /// 2-1 Step A: supports rgba16float (HDR intermediate data, used by the bloom downsample chain).</summary>
    public void CreateComputeTexture(string name, uint width, uint height,
        Season.Rendering.ComputeStorageFormat format = Season.Rendering.ComputeStorageFormat.Rgba8Unorm)
    {
        lock (DictionaryDXTexture)
        {
            if (DictionaryDXTexture.TryGetValue(name, out var existing))
            {
                // Rebuild in place when the size does not match (reuse the descriptor slot and keep the C# object identity).
                if (existing.Width != width || existing.Height != height)
                    existing.RecreateComputeStorage(width, height);
                return;
            }
            DictionaryDXTexture.Add(name, DXTexture.CreateComputeStorage(name, width, height, format));
        }
    }

    /// <summary>Looks up a registered texture by name (the same dictionary used by CreateComputeTexture,
    /// which is the only registration point for storage textures).
    /// The DDGI consumer side (DXPrimitiveGroup.SetLighting) must resolve the atlas from this table
    /// every frame; otherwise compute://* textures will not be found and White will always be bound
    /// as fallback, which would diverge from VK's single-registry behavior.</summary>
    internal bool TryGetTexture(string name, out DXTexture tex)
    {
        lock (DictionaryDXTexture)
            return DictionaryDXTexture.TryGetValue(name, out tex);
    }

    public Season.Rendering.StorageBuffer CreateStorageBuffer(uint sizeInBytes)
        => new DXStorageBuffer(sizeInBytes);

    // ── 1-8 Compute 3D resource expansion (contract in IGraphics / Compute.cs) ──
    // 3D textures are registered by name in DXTexture3D's own static dictionary
    // (not merged into DictionaryDXTexture, for the same reason noted in the cube comments below).
    // UpdateStorageBuffer opens a CPU write path for StorageBufferRead/ReadWrite
    // (an escape hatch when Params exceeds 128B).

    /// <summary>3D storage texture (dual-use for compute writes and trilinear sampling; clamp on
    /// all three axes is guaranteed by the static sampler s0).
    /// Rebuild in place when size or format does not match (reuse the descriptor slot and keep the
    /// C# object identity); creation or rebuild failures are logged instead of thrown
    /// (graceful degradation during registration, because 3D format support is easier to miss than
    /// in CreateComputeTexture).</summary>
    public void CreateComputeTexture3D(string name, uint width, uint height, uint depth,
        Season.Rendering.ComputeStorageFormat format = Season.Rendering.ComputeStorageFormat.Rgba8Unorm)
    {
        try
        {
            DirectX.DXTexture3D.CreateOrUpdate(name, width, height, depth, format);
        }
        catch (Exception ex)
        {
            DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} [CreateComputeTexture3D] '{name}' "
                + $"{width}x{height}x{depth} {format} creation failed: {ex.Message}");
        }
    }

    /// <summary>CPU-to-GPU constant block upload: staged through the upload heap and copied with
    /// CopyBufferRegion into the current frame command list
    /// (BeforeRender has already opened it; the caller must invoke this outside render/compute
    /// passes, see the IGraphics contract).</summary>
    public void UpdateStorageBuffer(Season.Rendering.StorageBuffer buffer, ReadOnlySpan<byte> data)
    {
        var cmd = DirectX.Device.GraphicsCommandList;
        if (cmd == null || buffer is not DXStorageBuffer dxBuffer)
            return;

        dxBuffer.Upload(cmd, data);
    }

    // ── 1-7 Cubemap type + environment IBL (contract in IGraphics / Season.Rendering.Environment.cs) ──
    // Cubes are registered by name in DXTextureCube's own static dictionary
    // (not merged into DictionaryDXTexture, whose elements have Texture2D semantics and are
    // consumed by Sprite2D/material paths by name; mixing cubes into that table would let those
    // paths retrieve an unsampleable dimension).

    public bool TextureCubeSupported => DirectX.Device.D3dDevice != null;

    /// <summary>Six already-decoded RGBA8 faces (ordered +X, -X, +Y, -Y, +Z, -Z), single mip;
    /// creates the resource synchronously, uploads it, and returns it ready to use.
    /// Any failure is logged and returns null (the shared layer then falls back to constant ambient
    /// light; see the graceful degradation contract of EnvironmentMap).</summary>
    public Season.Rendering.TextureCube CreateTextureCube(string name, int size,
        Season.Rendering.TextureCubeFormat format, INativeImageDecoder[] faces)
    {
        if (!TextureCubeSupported)
            return null;

        try
        {
            var cube = DirectX.DXTextureCube.CreateFromDecoders(name, size, format, faces);
            if (cube == null)
                return null;

            return new Season.Rendering.TextureCube
            {
                Name = name,
                Size = size,
                Format = format,
                Ready = true,
            };
        }
        catch (Exception ex)
        {
            DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} [CreateTextureCube] '{name}' creation failed: {ex.Message}");
            return null;
        }
    }

    public void DispatchCompute(in Season.Rendering.ComputeDispatchArgs args)
    {
        var cmd = DirectX.Device.GraphicsCommandList;
        if (cmd == null)
            return;

        var kernel = (DXComputeKernel)args.Kernel;
        var bindings = kernel.Desc.Bindings;

        DirectX.Device.PushDebugGroup(kernel.LabelZ);

        cmd->SetComputeRootSignature(kernel.RootSignature);
        cmd->SetPipelineState(kernel.PipelineState);

        if (kernel.ParamsNum32Bit > 0)
        {
            fixed (byte* pParams = args.Params)
            {
                cmd->SetComputeRoot32BitConstants(0, kernel.ParamsNum32Bit, pParams, 0);
            }
        }

        // Per binding: resolve resource -> pre-transition -> bind descriptor table; store the
        // resolution result into kernel slots for post-processing (zero allocation).
        // If a resource is not ready (name not registered/upload not finished), skip this frame's
        // dispatch (already-recorded barriers are harmless).
        int r = 0;
        for (int i = 0; i < bindings.Length; i++)
        {
            kernel.ResolvedScratch[i] = null;
            if (bindings[i].Type == Season.Rendering.ComputeBindingType.Params)
                continue;

            ref readonly var res = ref args.Resources[r++];

            if (res.Buffer is DXStorageBuffer buffer)
            {
                bool write = bindings[i].Type == Season.Rendering.ComputeBindingType.StorageBufferReadWrite;
                buffer.TransitionTo(cmd, write ? ResourceStates.UnorderedAccess : ResourceStates.NonPixelShaderResource);
                cmd->SetComputeRootDescriptorTable((uint)kernel.RootParamIndex[i], write ? buffer.UavGpuHandle : buffer.SrvGpuHandle);
                kernel.ResolvedScratch[i] = buffer;
                continue;
            }

            // 2-1 Step A: offscreen RT used as SampledTexture input (SceneColor is the source in
            // the AfterScene phase).
            // Wrapper forms without an SRV (backbuffer/MSAA target) skip this frame's dispatch
            // under the same "texture not ready" semantics.
            // Sampling-state transitions go through DXRenderTarget state tracking
            // (from ResolveDest in the MSAA path, or from RenderTarget in the non-MSAA path).
            // 2-2 Step A: depth-only RT (SceneDepth) used as DepthTexture input; transitions follow
            // the depth-surface state tracker (DepthWrite -> NonPixelShaderResource), and the SRV
            // is an R32Float alias view.
            if (res.Target is DXRenderTarget rt)
            {
                if (rt.SrvIndex < 0)
                {
                    DirectX.Device.PopDebugGroup();
                    return;
                }
                if (rt.Color == null && rt.Depth != null)
                    rt.TransitionDepthTo(cmd, ResourceStates.NonPixelShaderResource);
                else
                    rt.TransitionTo(cmd, ResourceStates.NonPixelShaderResource);
                cmd->SetComputeRootDescriptorTable((uint)kernel.RootParamIndex[i], rt.GpuSrvHandle);
                kernel.ResolvedScratch[i] = rt;
                continue;
            }

            // 1-8: 3D bindings use the dedicated DXTexture3D dictionary
            // (the 2D dictionary has Texture2D semantics and must never be queried for 3D).
            // The state machine mirrors 2D (write slot -> UnorderedAccess), with two differences:
            // 3D has no upload chain and therefore does not need to wait on a copy fence;
            // the sampled slot uses AllShaderResource instead of NonPixelShaderResource.
            // Since 2-5 Step E, 3D textures already have a draw consumer (the main shader's t15
            // aerial-perspective LUT), so using the union state of "non-pixel + pixel" lets the
            // compute sample slot and graphics sample slot remain idempotent without extra
            // transitions; otherwise the same volume would flip between two states every frame.
            if (bindings[i].Type is Season.Rendering.ComputeBindingType.SampledTexture3D
                or Season.Rendering.ComputeBindingType.StorageTexture3DWrite)
            {
                var tex3d = res.TextureName != null ? DirectX.DXTexture3D.Find(res.TextureName) : null;
                if (tex3d == null || !tex3d.Ready)
                {
                    DirectX.Device.PopDebugGroup();
                    return;
                }

                if (bindings[i].Type == Season.Rendering.ComputeBindingType.StorageTexture3DWrite)
                {
                    tex3d.TransitionTo(cmd, ResourceStates.UnorderedAccess);
                    cmd->SetComputeRootDescriptorTable((uint)kernel.RootParamIndex[i], tex3d.UavGpuDescriptorHandle);
                }
                else
                {
                    tex3d.TransitionTo(cmd, ResourceStates.AllShaderResource);
                    cmd->SetComputeRootDescriptorTable((uint)kernel.RootParamIndex[i], tex3d.GpuDescriptorHandle);
                }
                kernel.ResolvedScratch[i] = tex3d;
                continue;
            }

            DXTexture tex = null;
            if (res.TextureName != null)
            {
                lock (DictionaryDXTexture)
                {
                    DictionaryDXTexture.TryGetValue(res.TextureName, out tex);
                }
            }
            if (tex == null || !System.Threading.Volatile.Read(ref tex.Ready))
            {
                DirectX.Device.PopDebugGroup();
                return;
            }

            if (bindings[i].Type == Season.Rendering.ComputeBindingType.StorageTextureWrite)
            {
                tex.TransitionTo(cmd, ResourceStates.UnorderedAccess);
                cmd->SetComputeRootDescriptorTable((uint)kernel.RootParamIndex[i], tex.UavGpuDescriptorHandle);
            }
            else // SampledTexture
            {
                // Upload-chain texture: wait for the Copy Queue fence first (same semantics as
                // EnsureReadyForRendering), then transition to the compute SRV state.
                if (tex.UploadFenceValue > 0)
                {
                    DirectX.Device.DirectQueueWaitCopyFence(tex.UploadFenceValue);
                    tex.UploadFenceValue = 0;
                }
                tex.TransitionTo(cmd, ResourceStates.NonPixelShaderResource);
                cmd->SetComputeRootDescriptorTable((uint)kernel.RootParamIndex[i], tex.GpuDescriptorHandle);
            }
            kernel.ResolvedScratch[i] = tex;
        }

        cmd->Dispatch(args.GroupsX, args.GroupsY, args.GroupsZ);

        // Post-dispatch synchronization: storage/sampled textures return to PixelShaderResource
        // (draw sampling can consume them directly, and the transition barrier itself provides
        // write->read synchronization). Offscreen RTs also return to PixelShaderResource
        // (TransitionTo in downstream Post/FinalBlit is idempotent and avoids redundant
        // transitions). RW buffers get an extra UAV barrier for same-frame kernel-chain dependencies.
        for (int i = 0; i < bindings.Length; i++)
        {
            switch (kernel.ResolvedScratch[i])
            {
                case DXTexture tex:
                    tex.TransitionTo(cmd, ResourceStates.PixelShaderResource);
                    break;
                // Since 2-5 Step E, 3D textures have a draw consumer (the main shader's t15
                // aerial-perspective LUT), so they are no longer restored to
                // NonPixelShaderResource. Use the union state of "non-pixel + pixel" instead:
                // both graphics-side sampling and downstream kernel sample slots in the same chain
                // remain idempotent without extra transitions, avoiding a per-frame barrier flip.
                // The transition barrier itself already provides write->read synchronization, so
                // no extra UAV barrier is needed.
                case DirectX.DXTexture3D tex3d:
                    tex3d.TransitionTo(cmd, ResourceStates.AllShaderResource);
                    break;
                case DXRenderTarget rt:
                    if (rt.Color == null && rt.Depth != null)
                        rt.TransitionDepthTo(cmd, ResourceStates.PixelShaderResource);
                    else
                        rt.TransitionTo(cmd, ResourceStates.PixelShaderResource);
                    break;
                case DXStorageBuffer buffer when bindings[i].Type == Season.Rendering.ComputeBindingType.StorageBufferReadWrite:
                    ResourceBarrier uavBarrier = default;
                    uavBarrier.Type = ResourceBarrierType.Uav;
                    uavBarrier.Anonymous.UAV.PResource = buffer.Resource;
                    cmd->ResourceBarrier(1, &uavBarrier);
                    break;
            }
            kernel.ResolvedScratch[i] = null;
        }

        DirectX.Device.PopDebugGroup();
    }

    public async Task<bool> LoadSprite2D(Sprite2D sprite2D)
    {
        DXSprite2D dxSprite2D = null;

        lock (DictionarySprite)
        {
            if (sprite2D.IsDisposed) return false;

            if (DictionarySprite.TryGetValue((sprite2D.Name, sprite2D.ID), out dxSprite2D))
            {
                if (dxSprite2D == null || dxSprite2D.DXTexture == null)
                {

                }
                else
                {
                    sprite2D.OriginWidth = (int)dxSprite2D.DXTexture.Width;
                    sprite2D.OriginHeight = (int)dxSprite2D.DXTexture.Height;
                }
            }
            else
            {
                try
                {
                    DXTexture view = null;

                    lock (DictionaryDXTexture)
                    {
                        if (DictionaryDXTexture.TryGetValue(sprite2D.Name, out view))
                        {
                            view.AddRef();
                        }
                        else
                        {
                            INativeImageDecoder iNativeImageDecoder = null;

                            if (ImageUtils.CreateImageExist(sprite2D.Name))
                            {
                                iNativeImageDecoder = ImageUtils.CreateImage(sprite2D.Name, (int)sprite2D.Width, (int)sprite2D.Height);
                            }
                            else
                            {
                                if (StorageService.FileExist(StorageService.DirectoryBase, sprite2D.Name))
                                {

                                }
                                else
                                {
                                    StorageService.CopyToLocal(sprite2D.Name);
                                }

                                StorageService.TryGetStream(StorageService.DirectoryBase, sprite2D.Name, out Stream stream, out string errMsg);

                                if (stream == null)
                                {

                                }
                                else
                                {
                                    using (stream)
                                    {
                                        iNativeImageDecoder = new WindowsImageDecoder(stream);
                                    }
                                }
                            }

                            if (iNativeImageDecoder == null)
                            {

                            }
                            else
                            {
                                view = new DXTexture(iNativeImageDecoder);
                            }

                            if (view == null)
                            {
                                DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} {sprite2D.ToString()} LoadTextureAsync GetTexture....");
                            }
                            else
                            {
                                ExecuteUpload();
                            }

                            DictionaryDXTexture.Add(sprite2D.Name, view);
                        }
                    }

                    try
                    {
                        // Use Sprite instead of Texture2D.
                        dxSprite2D = new DXSprite2D(view);

                        sprite2D.OriginWidth = (int)dxSprite2D.DXTexture.Width;
                        sprite2D.OriginHeight = (int)dxSprite2D.DXTexture.Height;
                    }
                    catch (Exception ex)
                    {
                        DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} {sprite2D.ToString()} LoadTextureAsync new Sprite....{ex}");
                    }

                    lock (DictionarySprite)
                    {
                        if (DictionarySprite.ContainsKey((sprite2D.Name, sprite2D.ID)))
                        {
                            //impossible
                        }
                        else
                        {
                            DictionarySprite.Add((sprite2D.Name, sprite2D.ID), dxSprite2D);
                        }
                    }
                }
                catch (Exception ex)
                {
                    DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} {sprite2D.ToString()} LoadTextureAsync {ex}");
                }
            }
        }

        return true;
    }

    public void UpdateSprite2D(Sprite2D sprite)
    {
        DXSprite2D dxSprite = null;

        lock (DictionarySprite)
        {
            if (DictionarySprite.TryGetValue((sprite.Name, sprite.ID), out dxSprite))
            {
                if (dxSprite == null || dxSprite.DXTexture == null || dxSprite.DXTexture._textureResource == null)
                {

                }
                else
                {
                    sprite.Ready = true;

                    // ── Texture replacement (new) ──
                    if (sprite.TextureOverride.HasValue)
                    {
                        var source = sprite.TextureOverride;
                        sprite.TextureOverride = default; // Clear first.
                        ReplaceSpriteTexture(dxSprite, source);
                    }

                    if (sprite.Changed)
                    {
                        sprite.Changed = false;

                        // Zero-copy: reference the control directly and let Update sync it automatically.
                        dxSprite.SpriteRef = sprite;

                        dxSprite.Update();
                    }
                }
            }
        }
    }

    /// <summary>Resolves TextureUpdateSource into an INativeImageDecoder. Image takes priority over Path.</summary>
    static INativeImageDecoder? ResolveDecoder(TextureUpdateSource source)
    {
        if (source.Image != null) return source.Image;
        if (source.Path != null) return DecodeImageFromPath(source.Path);
        return null;
    }

    static INativeImageDecoder? DecodeImageFromPath(string path)
    {
        if (ImageUtils.CreateImageExist(path))
            return ImageUtils.CreateImage(path);

        if (!StorageService.FileExist(StorageService.DirectoryBase, path))
            StorageService.CopyToLocal(path);

        StorageService.TryGetStream(StorageService.DirectoryBase, path, out Stream stream, out _);
        if (stream == null) return null;

        using (stream)
            return new WindowsImageDecoder(stream);
    }

    /// <summary>Replaces the single texture of a Sprite.</summary>
    void ReplaceSpriteTexture(DXSpriteQuad dxSprite, TextureUpdateSource source)
    {
        var decoder = ResolveDecoder(source);
        if (decoder == null) return;

        var oldTex = dxSprite.DXTexture;

        if ((uint)decoder.Width == oldTex.Width
            && (uint)decoder.Height == oldTex.Height
            && oldTex.RefCount == 1)
        {
            // Fast path: update pixels in place (zero GPU allocation, SRV unchanged).
            oldTex.UploadPixels(decoder.PixelSpan);
        }
        else
        {
            // Recreate path: size changed or the texture is shared.
            var newTex = DXTexture.CreateFromDecoder(decoder);
            ExecuteUpload();
            dxSprite.DXTexture = newTex;
        }

        decoder.Dispose();
    }

    public void DrawSprite2D(Sprite2D sprite)
    {
        DXSprite2D dxSprite = null;

        lock (DictionarySprite)
        {
            if (DictionarySprite.TryGetValue((sprite.Name, sprite.ID), out dxSprite))
            {

            }
            else
            {
                //sprite.Changed = true;
            }
        }

        if (dxSprite == null || dxSprite.DXTexture == null || dxSprite.DXTexture._textureResource == null)
        {

        }
        else
        {
            dxSprite.Draw();
        }
    }

    public async Task<bool> LoadTexts(Texts texts)
    {
        if (texts?.TexsLoading?.Length == 0)
            return false;

        var texsLoading = texts.TexsLoading;
        int totalCount = texsLoading.Length + (texts.ShowDot ? 1 : 0);

        // Stage 1: count valid characters and ensure every glyph has been added to the atlas.
        var validIndices = new int[totalCount];
        int validCount = 0;

        for (int i = 0; i < texsLoading.Length; i++)
        {
            ref var tex = ref texsLoading[i];
            if (tex.TexType is TexType.NewLine or TexType.Space or TexType.Missing)
                continue;
            if (!TryEnsureGlyphEntry(ref tex, out var entry))
                continue;
            validIndices[validCount++] = i;
        }

        // Handle dot.
        bool hasDot = false;
        if (texts.ShowDot && TryEnsureGlyphEntry(ref texts._dotRef, out var dotEntry))
        {
            validIndices[validCount] = -1;  // -1 means dot.
            hasDot = true;
            validCount++;
        }

        if (validCount == 0)
            return false;

        // Stage 2: create instance buffers and the per-text glyph buffer.
        // The initial transform must stay hidden to avoid a one-frame flash of huge glyphs with
        // the identity world matrix after background loading finishes but before the first
        // Position/UpdateTexts call.
        var instanceData = new InstanceTransformData[validCount];
        var holders = new ITextureHolder[totalCount];

        int instanceIdx = 0;
        for (int v = 0; v < validCount; v++)
        {
            int srcIdx = validIndices[v];
            bool isDot = srcIdx < 0;
            ref var tex = ref isDot ? ref texts._dotRef : ref texsLoading[srcIdx];

            if (!TryEnsureGlyphEntry(ref tex, out var entry))
                continue;

            tex.AtlasVersion = entry.AtlasVersion;
            tex.GlyphMetrics = entry.GlyphMetrics;
            tex.Factor = entry.PixelRange;

            // slot 1: write zero matrices initially to hide instances; the real positions are
            // computed and overwritten in UpdateTexts.
            instanceData[instanceIdx] = CreateHiddenInstanceData();

            // Create the TextGlyphHolder and record the instance index.
            var holder = new TextGlyphHolder();
            holder.Texture.TextureType = TextureType.TextMsdf;
            holder.Texture.SourceX = entry.SourceX;
            holder.Texture.SourceY = entry.SourceY;
            holder.Texture.SourceWidth = entry.SourceWidth;
            holder.Texture.SourceHeight = entry.SourceHeight;
            holder.Texture.OriginWidth = entry.Width;
            holder.Texture.OriginHeight = entry.Height;
            holder.Texture.Factor = entry.PixelRange;
            holder.Texture.Ready = true;

            int storeIdx = isDot ? texsLoading.Length : srcIdx;
            if (isDot)
                texts.dotTextureHolderLoading = holder;
            else
                holders[storeIdx] = holder;

            instanceIdx++;
        }

        // Create GPU instance buffers (ring-buffer glyph data is written per frame in UpdateTexts).
        var state = new TextInstanceState
        {
            InstanceCount = instanceIdx,
            GlyphDescriptorId = -1,
            GlyphCapacity = 0,
            GlyphAtlasVersionBuilt = -1,
            GlyphDirty = true,
            CanDraw = false,
            InstanceFrameMask = 0,
            InstanceBuffers = new ID3D12Resource*[(int)DirectX.Device.frameCount],
            InstanceBufferViews = new VertexBufferView[(int)DirectX.Device.frameCount],
            InstanceCapacity = instanceData.Length,
        };

        for (int fi = 0; fi < state.InstanceBuffers.Length; fi++)
        {
            state.InstanceBuffers[fi] = DirectX.Device.CreateVertexBuffer(instanceData, out state.InstanceBufferViews[fi]);
        }

        if (!EnsureGlyphBufferCapacity(ref state, Math.Max(instanceIdx, 1)))
        {
            ReleaseTextInstanceResources(state, DirectX.Device.GetCurrentRetireFenceValue());
            return false;
        }

        var hiddenGlyph = CreateHiddenGlyphData();
        var glyphPtr = (TextGlyphData*)state.GlyphMappedPtr;
        for (int i = 0; i < Math.Max(instanceIdx, 1); i++)
            glyphPtr[i] = hiddenGlyph;

        lock (_textInstancesLock)
        {
            // Texts was disposed during LoadTexts: reclaim the newly created resources immediately
            // and do not write back into the dictionary, otherwise they would leak.
            if (texts.IsDisposed)
            {
                ReleaseTextInstanceResources(state, DirectX.Device.GetCurrentRetireFenceValue());
                return false;
            }

            if (_textInstances.TryGetValue(texts, out var previousState))
                ReleaseTextInstanceResources(previousState, DirectX.Device.GetCurrentRetireFenceValue());

            _textInstances[texts] = state;
        }

        texts.textureHoldersLoading = holders;

        return true;
    }

    /// <summary>Incremental append (contract in IGraphics.AppendTexts). Only creates atlas entries
    /// and holders for newly added glyphs.
    /// Buffers grow geometrically without rebuilding per-text state, so existing resources do not
    /// need to be released or recreated.
    /// GlyphDirty must be set to true because the instance index of dot shifts after appends,
    /// requiring the glyph data to be recomputed as a whole.</summary>
    public Task<bool> AppendTexts(Texts texts, Tex[] appendTexs, ITextureHolder[] appendHolders)
    {
        if (texts == null || appendTexs == null || appendHolders == null
            || appendTexs.Length == 0 || appendHolders.Length != appendTexs.Length)
            return Task.FromResult(false);

        if (!TryGetTextInstanceState(texts, out var state) || state.InstanceBuffers == null || state.InstanceCount <= 0)
            return Task.FromResult(false);

        int added = 0;
        for (int i = 0; i < appendTexs.Length; i++)
        {
            ref var tex = ref appendTexs[i];
            if (tex.TexType is TexType.NewLine or TexType.Space or TexType.Missing)
                continue;
            if (!TryEnsureGlyphEntry(ref tex, out var entry))
                continue;

            tex.AtlasVersion = entry.AtlasVersion;
            tex.GlyphMetrics = entry.GlyphMetrics;
            tex.Factor = entry.PixelRange;

            var holder = new TextGlyphHolder();
            holder.Texture.TextureType = TextureType.TextMsdf;
            holder.Texture.SourceX = entry.SourceX;
            holder.Texture.SourceY = entry.SourceY;
            holder.Texture.SourceWidth = entry.SourceWidth;
            holder.Texture.SourceHeight = entry.SourceHeight;
            holder.Texture.OriginWidth = entry.Width;
            holder.Texture.OriginHeight = entry.Height;
            holder.Texture.Factor = entry.PixelRange;
            holder.Texture.Ready = true;

            appendHolders[i] = holder;
            added++;
        }

        // Whitespace-only append (for example spaces/newlines): instance count stays unchanged,
        // only layout progression is needed at the upper layer.
        if (added == 0)
            return Task.FromResult(true);

        int required = state.InstanceCount + added;

        if (!EnsureInstanceBufferCapacity(ref state, required) || !EnsureGlyphBufferCapacity(ref state, required))
            return Task.FromResult(false);

        state.InstanceCount = required;
        state.GlyphDirty = true;
        state.InstanceFrameMask = 0;
        state.CanDraw = false;

        lock (_textInstancesLock)
        {
            // Do not write back after Dispose; otherwise resources already queued for release may
            // be "revived" into the dictionary and leave dangling pointers.
            if (texts.IsDisposed || !_textInstances.ContainsKey(texts))
                return Task.FromResult(false);

            _textInstances[texts] = state;
        }

        return Task.FromResult(true);
    }

    public void UpdateTexts(Texts texts)
    {
        if (texts?.Texs?.Length <= 0)
        {
            if (TryGetTextInstanceState(texts, out var emptyState))
            {
                emptyState.CanDraw = false;
                StoreTextInstanceState(texts, emptyState);
            }

            return;
        }

        // GPU instancing path.
        if (TryGetTextInstanceState(texts, out var state))
        {
            var texs = texts.Texs;
            var holders = texts.textureHolders;
            int instanceCount = state.InstanceCount;
            if (instanceCount <= 0 || state.GlyphMappedPtr == null || !EnsureGlyphBufferCapacity(ref state, instanceCount))
            {
                state.CanDraw = false;
                StoreTextInstanceState(texts, state);
                return;
            }

            int frameIndex = (int)DirectX.Device.FrameIndex;
            var glyphPtr = (TextGlyphData*)state.GlyphMappedPtr;
            uint frameBit = 1u << frameIndex;
            bool uploadGlyphData = state.GlyphDirty || state.GlyphAtlasVersionBuilt != _glyphAtlas.Version;

            // Check whether the layout changed: holder.Texture.Changed is set by
            // Position() -> ApplyLayoutToHolder.
            bool layoutChanged = uploadGlyphData;
            if (!layoutChanged)
            {
                if (holders != null)
                {
                    for (int i = 0; i < holders.Length; i++)
                    {
                        if (holders[i] is TextGlyphHolder h && h.Texture.Changed)
                        {
                            layoutChanged = true;
                            break;
                        }
                    }
                }
                if (!layoutChanged && texts.dotTextureHolder is TextGlyphHolder dh && dh.Texture.Changed)
                    layoutChanged = true;
            }

            if (layoutChanged)
                state.InstanceFrameMask = 0;

            bool writeInstanceData = layoutChanged;

            if (!uploadGlyphData && !writeInstanceData)
            {
                state.CanDraw = true;
                StoreTextInstanceState(texts, state);
                return;
            }

            // When layout changes, compute instance data into a temporary array and write it into
            // all frame buffers (Sprite2D mode).
            var instanceData = writeInstanceData ? new InstanceTransformData[instanceCount] : null;

            float n = 1f / DeviceServices.BaseApp.CompositionScale.X;
            var extendRes = DeviceServices.BaseApp.ExtendResolution;
            var deviceRes = DeviceServices.BaseApp.DeviceResolution;
            var globalScale = DeviceServices.BaseApp.Scale;
            float atlasW = _glyphAtlas.AtlasTexture?.Width ?? 1f;
            float atlasH = _glyphAtlas.AtlasTexture?.Height ?? 1f;

            int instIdx = 0;
            state.CanDraw = false;

            for (int i = 0; i < texs.Length; i++)
            {
                ref var tex = ref texs[i];
                if (tex.TexType is TexType.NewLine or TexType.Space or TexType.Missing)
                    continue;

                if (holders == null || i >= holders.Length || holders[i] is not TextGlyphHolder holder)
                    continue;

                var t = holder.Texture;
                if (t.Changed)
                    t.Changed = false;

                GlyphAtlasEntry entry = default;
                bool hasValidEntry = true;
                if (uploadGlyphData)
                {
                    // Check the atlas version: refresh the local cache if the glyph was rasterized again.
                    hasValidEntry = TryEnsureGlyphEntry(ref tex, out entry);
                    if (hasValidEntry && tex.AtlasVersion != entry.AtlasVersion)
                    {
                        tex.AtlasVersion = entry.AtlasVersion;
                        tex.Factor = entry.PixelRange;
                        t.SourceX = entry.SourceX;
                        t.SourceY = entry.SourceY;
                        t.SourceWidth = entry.SourceWidth;
                        t.SourceHeight = entry.SourceHeight;
                        t.OriginWidth = entry.Width;
                        t.OriginHeight = entry.Height;
                        t.Factor = entry.PixelRange;
                    }

                    float sx = hasValidEntry ? entry.SourceX : t.SourceX;
                    float sy = hasValidEntry ? entry.SourceY : t.SourceY;
                    float sw = hasValidEntry ? entry.SourceWidth : t.SourceWidth;
                    float sh = hasValidEntry ? entry.SourceHeight : t.SourceHeight;
                    float gw = hasValidEntry ? entry.Width : (float)t.OriginWidth;
                    float gh = hasValidEntry ? entry.Height : (float)t.OriginHeight;
                    float pr = hasValidEntry ? entry.PixelRange : (float)t.Factor;
                    bool hasColorOverride = tex.Color.HasValue;
                    var glyphColor = hasColorOverride ? tex.Color.Value.AsVector4 : Vector4.One;

                    glyphPtr[instIdx] = new TextGlyphData
                    {
                        UvRect = new Vector4(sx / atlasW, sy / atlasH, sw / atlasW, sh / atlasH),
                        Color = glyphColor,
                        Metrics = new Vector4(gw, gh, pr, hasColorOverride ? 1f : 0f),
                    };
                }

                if (writeInstanceData)
                {
                    float glyphAlpha = Math.Clamp(t.Alpha, 0f, 1f);
                    float posX = t.PosX;
                    float posY = t.PosY;
                    float width = t.Width;
                    float height = t.Height;

                    if (glyphAlpha <= 0f || width <= 0f || height <= 0f)
                    {
                        instanceData[instIdx] = CreateHiddenInstanceData();
                        instIdx++;
                        continue;
                    }

                    float ndcPosX = posX * n;
                    float ndcPosY = posY * n;
                    float ndcWidth = width * n;
                    float ndcHeight = height * n;
                    float ndcX = (ndcPosX - extendRes.X / 2) / (extendRes.X / 2);
                    float ndcY = (extendRes.Y / 2 - ndcPosY) / (extendRes.Y / 2);
                    float ndcScaledWidth = ndcWidth * globalScale / (deviceRes.X / 2);
                    float ndcScaledHeight = ndcHeight * globalScale / (deviceRes.Y / 2);

                    var pos = new Vector3(ndcX + ndcScaledWidth / 2, ndcY - ndcScaledHeight / 2, 0);
                    var scl = new Vector3(ndcScaledWidth, ndcScaledHeight, 1);
                    var world = Matrix4x4.CreateScale(scl) * Matrix4x4.CreateTranslation(pos);

                    var itd = new InstanceTransformData
                    {
                        Row0 = new Vector4(world.M11, world.M12, world.M13, world.M14),
                        Row1 = new Vector4(world.M21, world.M22, world.M23, world.M24),
                        Row2 = new Vector4(world.M31, world.M32, world.M33, world.M34),
                        Row3 = new Vector4(world.M41, world.M42, world.M43, world.M44),
                        MorphWeights = Vector4.Zero,
                    };
                    instanceData[instIdx] = itd;
                }

                instIdx++;
            }

            // Handle dot: Changed must be cleared unconditionally, otherwise a leftover dirty flag
            // after LastPos becomes null would cause a full rewrite every frame.
            if (texts.dotTextureHolder is TextGlyphHolder dotHolder)
            {
                var dt = dotHolder.Texture;
                if (dt.Changed)
                    dt.Changed = false;

                if (texts.LastPos != null)
                {
                    if (uploadGlyphData)
                    {
                        GlyphAtlasEntry dotEntry = default;
                        bool hasDotEntry = TryEnsureGlyphEntry(ref texts._dotRef, out dotEntry);
                        if (hasDotEntry && texts._dotRef.AtlasVersion != dotEntry.AtlasVersion)
                        {
                            texts._dotRef.AtlasVersion = dotEntry.AtlasVersion;
                            dt.SourceX = dotEntry.SourceX;
                            dt.SourceY = dotEntry.SourceY;
                            dt.SourceWidth = dotEntry.SourceWidth;
                            dt.SourceHeight = dotEntry.SourceHeight;
                            dt.OriginWidth = dotEntry.Width;
                            dt.OriginHeight = dotEntry.Height;
                            dt.Factor = dotEntry.PixelRange;
                        }

                        float dsx = hasDotEntry ? dotEntry.SourceX : dt.SourceX;
                        float dsy = hasDotEntry ? dotEntry.SourceY : dt.SourceY;
                        float dsw2 = hasDotEntry ? dotEntry.SourceWidth : dt.SourceWidth;
                        float dsh2 = hasDotEntry ? dotEntry.SourceHeight : dt.SourceHeight;
                        float dgw = hasDotEntry ? dotEntry.Width : (float)dt.OriginWidth;
                        float dgh = hasDotEntry ? dotEntry.Height : (float)dt.OriginHeight;
                        float dpr = hasDotEntry ? dotEntry.PixelRange : (float)dt.Factor;
                        bool hasDotColorOverride = texts._dotRef.Color.HasValue;
                        var dotGlyphColor = hasDotColorOverride ? texts._dotRef.Color.Value.AsVector4 : Vector4.One;

                        glyphPtr[instIdx] = new TextGlyphData
                        {
                            UvRect = new Vector4(dsx / atlasW, dsy / atlasH, dsw2 / atlasW, dsh2 / atlasH),
                            Color = dotGlyphColor,
                            Metrics = new Vector4(dgw, dgh, dpr, hasDotColorOverride ? 1f : 0f),
                        };
                    }

                    if (writeInstanceData)
                    {
                        float dotAlpha = Math.Clamp(dt.Alpha, 0f, 1f);
                        float dpx = dt.PosX * n;
                        float dpy = dt.PosY * n;
                        float dw = dt.Width * n;
                        float dh = dt.Height * n;

                        if (dotAlpha <= 0f || dw <= 0f || dh <= 0f)
                        {
                            instanceData[instIdx] = CreateHiddenInstanceData();
                            instIdx++;
                            goto AfterDot;
                        }

                        float dnx = (dpx - extendRes.X / 2) / (extendRes.X / 2);
                        float dny = (extendRes.Y / 2 - dpy) / (extendRes.Y / 2);
                        float dsw = dw * globalScale / (deviceRes.X / 2);
                        float dsh = dh * globalScale / (deviceRes.Y / 2);

                        var dpos = new Vector3(dnx + dsw / 2, dny - dsh / 2, 0);
                        var dscl = new Vector3(dsw, dsh, 1);
                        var dworld = Matrix4x4.CreateScale(dscl) * Matrix4x4.CreateTranslation(dpos);

                        var ditd = new InstanceTransformData
                        {
                            Row0 = new Vector4(dworld.M11, dworld.M12, dworld.M13, dworld.M14),
                            Row1 = new Vector4(dworld.M21, dworld.M22, dworld.M23, dworld.M24),
                            Row2 = new Vector4(dworld.M31, dworld.M32, dworld.M33, dworld.M34),
                            Row3 = new Vector4(dworld.M41, dworld.M42, dworld.M43, dworld.M44),
                            MorphWeights = Vector4.Zero,
                        };
                        instanceData[instIdx] = ditd;
                    }
                    instIdx++;
                }
            }

        AfterDot:

            for (; instIdx < instanceCount; instIdx++)
            {
                if (uploadGlyphData)
                    glyphPtr[instIdx] = CreateHiddenGlyphData();
                if (writeInstanceData)
                    instanceData[instIdx] = CreateHiddenInstanceData();
            }

            // Sprite2D multi-frame sync mode: write to all frame buffers when layout changes to
            // avoid alternating flicker as different in-flight frames read old and new layouts.
            if (writeInstanceData)
            {
                for (int fi = 0; fi < state.InstanceBuffers.Length; fi++)
                {
                    var ib = state.InstanceBuffers[fi];
                    if (ib == null)
                        continue;

                    void* p;
                    ib->Map(0, null, &p);
                    for (int j = 0; j < instanceCount; j++)
                        Unsafe.Write((byte*)p + j * sizeof(InstanceTransformData), instanceData[j]);
                    ib->Unmap(0, null);
                    state.InstanceFrameMask |= (1u << fi);
                }
            }
            if (uploadGlyphData)
            {
                state.GlyphAtlasVersionBuilt = _glyphAtlas.Version;
                state.GlyphDirty = false;
            }
            state.CanDraw = true;
            // Write back into the dictionary: TextInstanceState is a struct, and TryGetValue
            // returns a copy.
            StoreTextInstanceState(texts, state);
            return;
        }

    }

    public void DrawTexts(Texts texts)
    {
        if (texts?.Texs?.Length == 0)
        {
            if (TryGetTextInstanceState(texts, out var emptyState))
            {
                emptyState.CanDraw = false;
                StoreTextInstanceState(texts, emptyState);
            }

            return;
        }

        // GPU instancing path: single DrawIndexedInstanced call.
        if (TryGetTextInstanceState(texts, out var state) && state.InstanceCount > 0)
        {
            var cmdList = DirectX.Device.GraphicsCommandList;
            int fi = (int)DirectX.Device.FrameIndex;
            if (!state.CanDraw || state.GlyphBuffer == null || state.GlyphSrv.Ptr == 0 || state.InstanceBufferViews == null || fi >= state.InstanceBufferViews.Length)
                return;

            // Ensure the atlas texture is ready.
            _glyphAtlas.AtlasTexture?.EnsureReadyForRendering(cmdList);

            // Set the pipeline (Transparent + DoubleSided -> no back-face culling).
            Pipeline.SetPipeline(PipelineMode.Transparent, doubleSided: true);

            // Bind VB slot 0 (unit quad) + slot 1 (instance transforms).
            var vertexViews = stackalloc VertexBufferView[2];
            vertexViews[0] = Pipeline.UnitQuadVertexBufferView;
            vertexViews[1] = state.InstanceBufferViews[fi];
            cmdList->IASetVertexBuffers(0, 2, vertexViews);

            // Bind the IB (unit quad).
            fixed (IndexBufferView* ibv = &Pipeline.UnitQuadIndexBufferView)
                cmdList->IASetIndexBuffer(ibv);

            // Write material parameters.
            var texSize = new Vector2(
                _glyphAtlas.AtlasTexture?.Width ?? 1f,
                _glyphAtlas.AtlasTexture?.Height ?? 1f);
            var textColor = texts.Color.AsVector4;
            var drawParams = new TextDrawParams
            {
                PxRange = Season.Fonts.Font.PixelRange,
                AtlasSize = texSize,
                GlobalAlpha = Math.Clamp(texts.Alpha, 0f, 1f),
                TextColor = textColor,
            };
            ulong drawParamsGpuAddress = _textFrameResources[fi].DrawParamsArena.Allocate(drawParams);
            if (drawParamsGpuAddress == 0)
                return;


            // b0: matrix CB
            cmdList->SetGraphicsRootConstantBufferView(0, _textMatrixBuffer->GetGPUVirtualAddress());
            // b1: lighting CB
            cmdList->SetGraphicsRootConstantBufferView(1, DXPrimitiveGroup.lightConstantBuffers[fi]->GetGPUVirtualAddress());
            // b2: material CB
            cmdList->SetGraphicsRootConstantBufferView(2, Pipeline.DefaultTextMaterialGpuAddress);
            // b4: text draw parameter CB
            cmdList->SetGraphicsRootConstantBufferView(11, drawParamsGpuAddress);

            // t0: atlas texture
            cmdList->SetGraphicsRootDescriptorTable(3, _glyphAtlas.AtlasTexture?.GpuDescriptorHandle ?? DirectX.Device.White.GpuDescriptorHandle);
            // t1-t4: placeholder textures
            var whiteHandle = DirectX.Device.White.GpuDescriptorHandle;
            cmdList->SetGraphicsRootDescriptorTable(4, whiteHandle);
            cmdList->SetGraphicsRootDescriptorTable(5, whiteHandle);
            cmdList->SetGraphicsRootDescriptorTable(6, whiteHandle);
            cmdList->SetGraphicsRootDescriptorTable(7, whiteHandle);

            // t5: the glyph-data SRV owned by the current Texts
            cmdList->SetGraphicsRootDescriptorTable(9, state.GlyphSrv);

            // t6: default instance bones
            cmdList->SetGraphicsRootDescriptorTable(10, Pipeline.DefaultInstanceBoneSrvHandle);

            // b3: bone CBV (text does not use it, but it must still be bound to satisfy D3D12 validation)
            cmdList->SetGraphicsRootConstantBufferView(8, _textMatrixBuffer->GetGPUVirtualAddress());

            // Single instanced draw: glyph data is indexed directly from the current Texts t5 SRV by instanceID.
            cmdList->DrawIndexedInstanced(6, (uint)state.InstanceCount, 0, 0, 0);
            return;
        }

    }

    public void DisposeTexts(Texts texts)
    {
        ulong fence = DirectX.Device.GetCurrentRetireFenceValue();

        // Release GPU instancing resources (per-text glyph buffer + per-frame instance buffers).
        lock (_textInstancesLock)
        {
            if (_textInstances.TryGetValue(texts, out var state))
            {
                ReleaseTextInstanceResources(state, fence);
                _textInstances.Remove(texts);
            }
        }

        // Release holder references (TextGlyphHolder has no GPU resources; only clear the references).
        if (texts.textureHoldersLoading != null)
        {
            foreach (var holder in texts.textureHoldersLoading)
            {
                if (holder is IDisposable d)
                    EnqueueDeferredRelease(fence, d.Dispose);
            }
        }
        if (texts.textureHolders != null)
        {
            foreach (var holder in texts.textureHolders)
            {
                if (holder is IDisposable d)
                    EnqueueDeferredRelease(fence, d.Dispose);
            }
        }
        if (texts.dotTextureHolderLoading is IDisposable ddl)
            EnqueueDeferredRelease(fence, ddl.Dispose);
        if (texts.dotTextureHolder is IDisposable dd)
            EnqueueDeferredRelease(fence, dd.Dispose);

        texts.textureHoldersLoading = null;
        texts.textureHolders = null;
        texts.dotTextureHolderLoading = null;
        texts.dotTextureHolder = null;
    }

    public void FlushTextAtlas()
    {
        _glyphAtlas.FlushPendingUploadsOnRenderThread();
    }

    public void DisposeTextureHolders(ITextureHolder[] holders)
    {
        if (holders == null || holders.Length == 0)
            return;

        ulong fence = DirectX.Device.GetCurrentRetireFenceValue();

        foreach (var holder in holders)
        {
            if (holder is IDisposable d)
                EnqueueDeferredRelease(fence, d.Dispose);
        }
    }

    bool TryEnsureGlyphEntry(ref Tex tex, out GlyphAtlasEntry entry)
    {
        entry = default;

        if (tex.TexType is TexType.NewLine or TexType.Space or TexType.Missing)
        {
            return false;
        }

        int size = (int)DeviceServices.BaseApp.FontSize;

        try
        {
            if (!_glyphAtlas.TryEnsureGlyph(size, tex.Value, out entry))
            {
                tex.TexType = TexType.Missing;
                return false;
            }
        }
        catch (Exception ex)
        {
            tex.TexType = TexType.Missing;
            DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} LoadTexTexture EnsureGlyph {ex}");
            return false;
        }

        tex.GlyphMetrics = entry.GlyphMetrics;
        tex.Factor = entry.PixelRange;
        return true;
    }

    public async Task<bool> LoadModel(Season.Controls.Model model)
    {
        lock (DictionaryModel)
        {
            if (DictionaryModel.ContainsKey((model.Name, model.ID)))
            {
                return true;
            }
        }

        GetOrCreateSharedModelAsync(model.Name).ContinueWith(task =>
        {
            DXModel dxModel;
            try
            {
                var template = task.GetAwaiter().GetResult();
                dxModel = template.CreateInstance(model, DXPrimitiveGroup.Camera);
            }
            catch
            {
                dxModel = new DXModel(model.Name);
                dxModel.Load(model, DXPrimitiveGroup.Camera);
            }

            lock (DictionaryModel)
            {
                if (!DictionaryModel.ContainsKey((model.Name, model.ID)))
                    DictionaryModel.Add((model.Name, model.ID), dxModel);
                else
                    dxModel.Dispose();
            }
        });

        return true;
    }

    Task<DXModel> GetOrCreateSharedModelAsync(string modelName)
    {
        Task<DXModel> sharedTask;
        lock (DictionaryModelResource)
        {
            if (!DictionaryModelResource.TryGetValue(modelName, out sharedTask))
            {
                sharedTask = CreateSharedModelAsync(modelName);
                DictionaryModelResource[modelName] = sharedTask;
            }
        }

        return sharedTask.ContinueWith(task =>
        {
            if (task.IsFaulted || task.IsCanceled)
            {
                lock (DictionaryModelResource)
                {
                    if (DictionaryModelResource.TryGetValue(modelName, out var cachedTask) && cachedTask == sharedTask)
                        DictionaryModelResource.Remove(modelName);
                }
            }

            return task.GetAwaiter().GetResult();
        });
    }

    Task<DXModel> CreateSharedModelAsync(string modelName)
    {
        var templateModel = new Model
        {
            Name = modelName,
            Alpha = 1f
        };

        var template = new DXModel(modelName);
        template.Load(templateModel, DXPrimitiveGroup.Camera);
        ExecuteUpload();
        return Task.FromResult(template);
    }

    public void UpdateModel(Model model, float time)
    {
        DXModel dxModel = null;

        lock (DictionaryModel)
        {
            if (DictionaryModel.TryGetValue((model.Name, model.ID), out dxModel))
            {
                // ── Material overrides (new: handled before animation updates) ──
                ProcessModelOverrides(model, dxModel);

                dxModel.Update(model, time);
            }
        }
    }

    /// <summary>Consumes all material override properties on Model and resets them to null/default after processing.</summary>
    void ProcessModelOverrides(Model model, DXPrimitiveGroup dxGroup)
    {
        // Texture overrides.
        TryReplaceModelTexture(model, dxGroup, model.BaseColorOverride, TextureSlot.BaseColor, () => model.BaseColorOverride = default);
        TryReplaceModelTexture(model, dxGroup, model.NormalOverride, TextureSlot.Normal, () => model.NormalOverride = default);
        TryReplaceModelTexture(model, dxGroup, model.MetallicRoughnessOverride, TextureSlot.MetallicRoughness, () => model.MetallicRoughnessOverride = default);
        TryReplaceModelTexture(model, dxGroup, model.OcclusionOverride, TextureSlot.Occlusion, () => model.OcclusionOverride = default);
        TryReplaceModelTexture(model, dxGroup, model.EmissiveTextureOverride, TextureSlot.Emissive, () => model.EmissiveTextureOverride = default);

        // Parameter overrides.
        bool hasParamOverride = model.MetallicOverride.HasValue
                             || model.RoughnessOverride.HasValue
                             || model.EmissiveFactorOverride.HasValue;
        if (hasParamOverride)
        {
            dxGroup.SyncMaterialParams(
                model.MetallicOverride, model.RoughnessOverride, model.EmissiveFactorOverride);
            model.MetallicOverride = null;
            model.RoughnessOverride = null;
            model.EmissiveFactorOverride = null;
        }
    }

    void TryReplaceModelTexture(Model model, DXPrimitiveGroup dxGroup,
        TextureUpdateSource source, TextureSlot slot, Action clearSource)
    {
        if (!source.HasValue) return;
        clearSource();
        var decoder = ResolveDecoder(source);
        if (decoder == null) return;
        dxGroup.ReplaceTextureBySlot(slot, decoder);
        ExecuteUpload();
        decoder.Dispose();
    }

    public void DrawModel(Model model)
    {
        if (model.Name.IsNullOrWhiteSpace() || model.Alpha == 0)
        {

        }
        else
        {
            DXModel dxModel3D = null;

            lock (DictionaryModel)
            {
                if (DictionaryModel.TryGetValue((model.Name, model.ID), out dxModel3D))
                {

                }
                else
                {
                    //texture.Changed = true;
                }
            }

            if (dxModel3D == null)
            {

            }
            else
            {
                dxModel3D.Draw();
            }
        }
    }

    // ============================================================
    // 1-5 Shadow pass: per-control shadow dispatch + pass scheduling entry point.
    // ============================================================

    public void DrawModelShadow(Model model)
    {
        DXModel dxModel = null;
        lock (DictionaryModel)
        {
            DictionaryModel.TryGetValue((model.Name, model.ID), out dxModel);
        }
        dxModel?.DrawShadow();
    }

    public void DrawMesh3DShadow(Season.Controls.Mesh3D mesh)
    {
        DXMesh3D dxMesh = null;
        lock (DictionaryMesh3D)
        {
            DictionaryMesh3D.TryGetValue((mesh.Name, mesh.ID), out dxMesh);
        }
        dxMesh?.DrawShadow();
    }

    public void DrawInstancedModelShadow(InstancedModel model)
    {
        DXInstancedModel dxModel = null;
        lock (DictionaryInstancedModel)
        {
            DictionaryInstancedModel.TryGetValue((model.ModelName, model.ID), out dxModel);
        }
        dxModel?.DrawShadow();
    }

    public void DrawInstancedMesh3DShadow(InstancedMesh3D mesh)
    {
        DXInstancedMesh3D dxMesh = null;
        lock (DictionaryInstancedMesh3D)
        {
            DictionaryInstancedMesh3D.TryGetValue((mesh.Name, mesh.ID), out dxMesh);
        }
        dxMesh?.DrawShadow();
    }

    /// <summary>
    /// 1-5 Shadow pass body (FrameSchedule.RenderShadow callback): after switching to the shadow PSO,
    /// set a controlled viewport and light-space matrix (root constant b5) for each atlas quadrant,
    /// then replay the shared-layer DrawShadow traversal once per cascade/spot light.
    /// The atlas has already been fully cleared by BeginPass; when no light is active, return
    /// immediately (shadowParams stays all zero on the shader side, so nothing is sampled).
    /// </summary>
    internal void RenderShadowPass(Season.Basic.IGraphics g)
    {
        if (!RenderQuality.Current.ShadowsEnabled)
            return;
        if (!CascadedShadow.SunActive && !CascadedShadow.SpotActive)
            return;

        var app = DeviceServices.BaseApp;
        if (app == null)
            return;

        Pipeline.SetShadowPipeline();

        if (CascadedShadow.SunActive)
        {
            for (int slot = 0; slot < CascadedShadow.ActiveCascadeCount; slot++)
            {
                CascadedShadow.GetAtlasViewport(slot, out int x, out int y, out int size);
                DirectX.Device.SetShadowViewport(x, y, size);
                // Clause 7: BeginSlot must publish both the matrix and the culling frustum together
                // because they share the same source and cannot be bypassed independently.
                Pipeline.SetShadowViewProj(CascadedShadow.BeginSlot(slot));
                app.DrawShadow();
            }
        }

        if (CascadedShadow.SpotActive)
        {
            CascadedShadow.GetAtlasViewport(CascadedShadow.SpotSlot, out int sx, out int sy, out int ssize);
            DirectX.Device.SetShadowViewport(sx, sy, ssize);
            Pipeline.SetShadowViewProj(CascadedShadow.BeginSlot(CascadedShadow.SpotSlot));
            app.DrawShadow();
        }

        CascadedShadow.EndPass();
    }

    public void DisposeModel(Model model)
    {
        DXModel dxModel = null;
        lock (DictionaryModel)
        {
            var key = (model.Name, model.ID);
            if (DictionaryModel.TryGetValue(key, out dxModel))
                DictionaryModel.Remove(key);
        }

        // Same contract as DisposeMesh3D: in-flight frames may still reference these resources,
        // so release must be deferred behind a retire fence.
        ulong retireFence = DirectX.Device.GetCurrentRetireFenceValue();
        if (dxModel != null)
            EnqueueDeferredRelease(retireFence, dxModel.Dispose);

        // The shared template cache (DictionaryModelResource) is shared by Model controls with
        // the same name and is not part of per-control release
        // (same contract shape as DisposeInstancedModel).
        model.Ready = false;
    }

    public async Task<bool> LoadSprite3D(Sprite3D sprite)
    {
        lock (DictionarySprite3D)
        {
            if (DictionarySprite3D.ContainsKey((sprite.Name, sprite.ID)))
                return true;
        }

        DXTexture view = null;
        lock (DictionaryDXTexture)
        {
            if (!DictionaryDXTexture.TryGetValue(sprite.Name, out view))
            {
                INativeImageDecoder iNativeImageDecoder = null;

                if (ImageUtils.CreateImageExist(sprite.Name))
                {
                    iNativeImageDecoder = ImageUtils.CreateImage(sprite.Name);
                }
                else
                {
                    if (!StorageService.FileExist(StorageService.DirectoryBase, sprite.Name))
                        StorageService.CopyToLocal(sprite.Name);
                    StorageService.TryGetStream(StorageService.DirectoryBase, sprite.Name, out Stream stream, out string errMsg);
                    using (stream)
                    {
                        if (stream != null)
                        {
                            iNativeImageDecoder = new WindowsImageDecoder(stream);
                            //imageResult = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
                        }
                    }
                }
                if (iNativeImageDecoder != null)
                {
                    view = new DXTexture(iNativeImageDecoder);
                }
                if (view != null)
                    DictionaryDXTexture.Add(sprite.Name, view);
            }
        }

        var dxSprite3D = new DXSprite3D(view);

        lock (DictionarySprite3D)
        {
            if (!DictionarySprite3D.ContainsKey((sprite.Name, sprite.ID)))
                DictionarySprite3D.Add((sprite.Name, sprite.ID), dxSprite3D);
        }

        return true;
    }

    public void UpdateSprite3D(Sprite3D sprite, float time)
    {
        DXSprite3D dxSprite3D = null;
        lock (DictionarySprite3D)
        {
            if (DictionarySprite3D.TryGetValue((sprite.Name, sprite.ID), out dxSprite3D))
            {
                // ── Texture replacement (new) ──
                if (sprite.TextureOverride.HasValue)
                {
                    var source = sprite.TextureOverride;
                    sprite.TextureOverride = default; // Clear first.
                    ReplaceSpriteTexture(dxSprite3D, source);
                }

                dxSprite3D.Update(
                    new Vector3(sprite.PosX, sprite.PosY, sprite.PosZ),
                    new Vector2(sprite.Width ?? 1f, sprite.Height ?? 1f),
                    sprite.Rotation,
                    DXPrimitiveGroup.Camera.View,
                    DXPrimitiveGroup.Camera.Projection,
                    sprite.Mode,
                    sprite.Color,
                    sprite.Alpha);
            }
        }
    }

    public void DrawSprite3D(Sprite3D sprite)
    {
        if (sprite.Name.IsNullOrWhiteSpace() || sprite.Alpha == 0)
            return;

        DXSprite3D dxSprite3D = null;
        lock (DictionarySprite3D)
        {
            DictionarySprite3D.TryGetValue((sprite.Name, sprite.ID), out dxSprite3D);
        }
        dxSprite3D?.Draw();
    }

    public void DisposeSprite3D(Sprite3D sprite)
    {
        DXSprite3D dxSprite3D = null;
        lock (DictionarySprite3D)
        {
            var key = (sprite.Name, sprite.ID);
            if (DictionarySprite3D.TryGetValue(key, out dxSprite3D))
                DictionarySprite3D.Remove(key);
        }
        dxSprite3D?.Dispose();

        lock (DictionaryDXTexture)
        {
            if (DictionaryDXTexture.TryGetValue(sprite.Name, out var dxTexture) && dxTexture != null)
            {
                dxTexture.Release();
                if (dxTexture.RefCount == 0)
                    DictionaryDXTexture.Remove(sprite.Name);
            }
        }
        sprite.Ready = false;
    }

    /// <summary>
    /// Loads a single texture into DictionaryDXTexture on demand and returns the DXTexture.
    /// Reuses the LoadSprite3D loading chain: StorageService -> ImageResult ->
    /// new DXTexture(imageResult) + ExecuteUpload.
    /// </summary>
    DXTexture EnsureDXTexture(string name)
    {
        if (name.IsNullOrWhiteSpace())
            return null;

        DXTexture view = null;
        lock (DictionaryDXTexture)
        {
            if (DictionaryDXTexture.TryGetValue(name, out view))
                return view;

            INativeImageDecoder iNativeImageDecoder = null;

            if (ImageUtils.CreateImageExist(name))
            {
                iNativeImageDecoder = ImageUtils.CreateImage(name);
            }
            else
            {
                if (!StorageService.FileExist(StorageService.DirectoryBase, name))
                    StorageService.CopyToLocal(name);
                StorageService.TryGetStream(StorageService.DirectoryBase, name, out Stream stream, out string errMsg);

                using (stream)
                {
                    if (stream != null)
                    {
                        iNativeImageDecoder = new WindowsImageDecoder(stream);
                    }
                }
            }

            if (iNativeImageDecoder != null)
            {
                view = new DXTexture(iNativeImageDecoder);
            }

            if (view != null)
                DictionaryDXTexture.Add(name, view);

            return view;
        }
    }

    /// <summary>
    /// Synthetic name for procedural textures: pixel-source textures have no file path, so they are
    /// registered into DictionaryDXTexture under a deterministic name that matches the glTF
    /// "{Model.Name}-baseColor-{index}" name-based cache pattern.
    /// </summary>
    static string ProcTextureName(string meshName, long meshId, int surfaceIndex, SurfaceTextureSlot slot)
        => $"proc:{meshName}:{meshId}:{surfaceIndex}:{slot}";

    /// <summary>
    /// Resolves the texture source of a single Surface slot into a DXTexture registered in
    /// DictionaryDXTexture:
    /// - Image branch (procedural pixels): CreateFromDecoder uploads directly to the GPU without
    ///   any file I/O, with the decoder consumed and disposed inside the DXTexture constructor,
    ///   then registers it under the synthetic name;
    /// - Path branch: reuses the existing EnsureDXTexture loading chain.
    /// Note: Override is not cleared here because ProcessMaterial still needs to query it through
    /// GetTextureSource/HasTexture; the caller clears it uniformly with ClearTextureOverride after
    /// loading completes (single-consumption contract).
    /// </summary>
    DXTexture EnsureSurfaceTexture(string meshName, long meshId, int surfaceIndex, Surface surface, SurfaceTextureSlot slot)
    {
        var source = surface.GetTextureSource(slot);
        if (!source.HasValue)
            return null;

        if (source.Image != null)
        {
            var name = ProcTextureName(meshName, meshId, surfaceIndex, slot);
            lock (DictionaryDXTexture)
            {
                if (DictionaryDXTexture.TryGetValue(name, out var cached))
                {
                    source.Image.Dispose();   // Already registered, so skip re-upload and only dispose the decoder to avoid leaks.
                    return cached;
                }
            }

            var tex = DXTexture.CreateFromDecoder(source.Image);
            tex.Name = name;

            lock (DictionaryDXTexture)
                DictionaryDXTexture[name] = tex;

            return tex;
        }

        return EnsureDXTexture(source.Path);
    }

    /// <summary>Pre-resolves all five texture slots of a single Surface (empty sources are skipped automatically).</summary>
    void EnsureSurfaceTextures(string meshName, long meshId, int surfaceIndex, Surface surface)
    {
        EnsureSurfaceTexture(meshName, meshId, surfaceIndex, surface, SurfaceTextureSlot.BaseColor);
        EnsureSurfaceTexture(meshName, meshId, surfaceIndex, surface, SurfaceTextureSlot.Normal);
        EnsureSurfaceTexture(meshName, meshId, surfaceIndex, surface, SurfaceTextureSlot.MetallicRoughness);
        EnsureSurfaceTexture(meshName, meshId, surfaceIndex, surface, SurfaceTextureSlot.Occlusion);
        EnsureSurfaceTexture(meshName, meshId, surfaceIndex, surface, SurfaceTextureSlot.Emissive);
    }

    /// <summary>Clears TextureOverride for all Surface slots after Load completes (single-consumption contract).</summary>
    static void ClearSurfaceOverrides(Surface surface)
    {
        surface.ClearTextureOverride(SurfaceTextureSlot.BaseColor);
        surface.ClearTextureOverride(SurfaceTextureSlot.Normal);
        surface.ClearTextureOverride(SurfaceTextureSlot.MetallicRoughness);
        surface.ClearTextureOverride(SurfaceTextureSlot.Occlusion);
        surface.ClearTextureOverride(SurfaceTextureSlot.Emissive);
    }

    /// <summary>
    /// Builds the per-slot resolver used by *Mesh3D.ProcessMaterial: pixel sources are resolved by
    /// synthetic name, path sources by path name. Both hit DXTextures already registered in
    /// DictionaryDXTexture before Load; missing entries return null and fall back to White.
    /// </summary>
    Func<Surface, TextureSlot, DXTexture> BuildSurfaceTextureResolver(string meshName, long meshId, IList<Surface> surfaces)
    {
        return (surface, slot) =>
        {
            var source = surface.GetTextureSource((SurfaceTextureSlot)slot);
            if (!source.HasValue)
                return null;

            var name = source.Image != null
                ? ProcTextureName(meshName, meshId, surfaces.IndexOf(surface), (SurfaceTextureSlot)slot)
                : source.Path;

            lock (DictionaryDXTexture)
            {
                DictionaryDXTexture.TryGetValue(name, out var tex);
                return tex;
            }
        };
    }

    /// <summary>Releases procedural textures registered under the five synthetic names of a single
    /// Surface (the caller must hold the DictionaryDXTexture lock).
    /// Resources whose reference count drops to zero are moved to the deferred release queue to
    /// avoid competing with in-flight frames (same contract as DisposeSprite2D).</summary>
    void ReleaseProcSurfaceTextures(string meshName, long meshId, int surfaceIndex, ulong retireFence)
    {
        ReleaseProcTexture(ProcTextureName(meshName, meshId, surfaceIndex, SurfaceTextureSlot.BaseColor));
        ReleaseProcTexture(ProcTextureName(meshName, meshId, surfaceIndex, SurfaceTextureSlot.Normal));
        ReleaseProcTexture(ProcTextureName(meshName, meshId, surfaceIndex, SurfaceTextureSlot.MetallicRoughness));
        ReleaseProcTexture(ProcTextureName(meshName, meshId, surfaceIndex, SurfaceTextureSlot.Occlusion));
        ReleaseProcTexture(ProcTextureName(meshName, meshId, surfaceIndex, SurfaceTextureSlot.Emissive));

        void ReleaseProcTexture(string name)
        {
            if (DictionaryDXTexture.TryGetValue(name, out var tex) && tex != null)
            {
                if (tex.RefCount <= 1)
                {
                    DictionaryDXTexture.Remove(name);
                    EnqueueDeferredRelease(retireFence, tex.Release);
                }
                else
                {
                    tex.Release();
                }
            }
        }
    }

    public async Task<bool> LoadMesh3D(Season.Controls.Mesh3D mesh)
    {
        lock (DictionaryMesh3D)
        {
            if (DictionaryMesh3D.ContainsKey((mesh.Name, mesh.ID)))
                return true;
        }

        // 1. Pre-resolve all texture sources referenced by Surface: pixel sources upload directly
        //    to the GPU through CreateFromDecoder (no disk round-trip), and path sources reuse
        //    EnsureDXTexture (empty sources are skipped automatically).
        for (int i = 0; i < mesh.Surfaces.Count; i++)
            EnsureSurfaceTextures(mesh.Name, mesh.ID, i, mesh.Surfaces[i]);

        ExecuteUpload();

        // 2. Construct DXMesh3D: resolve cached DXTexture per slot (fall back to solid color when missing).
        var dxMesh = new DXMesh3D(mesh.Name);
        dxMesh.Load(mesh, DXPrimitiveGroup.Camera, BuildSurfaceTextureResolver(mesh.Name, mesh.ID, mesh.Surfaces));

        // 3. Pixel sources have been consumed: clear each slot's Override (single-consumption contract).
        foreach (var surface in mesh.Surfaces)
            ClearSurfaceOverrides(surface);

        lock (DictionaryMesh3D)
        {
            if (!DictionaryMesh3D.ContainsKey((mesh.Name, mesh.ID)))
                DictionaryMesh3D.Add((mesh.Name, mesh.ID), dxMesh);
        }

        return true;
    }

    public void UpdateMesh3D(Season.Controls.Mesh3D mesh, float time)
    {
        DXMesh3D dxMesh = null;
        lock (DictionaryMesh3D)
        {
            DictionaryMesh3D.TryGetValue((mesh.Name, mesh.ID), out dxMesh);
        }
        dxMesh?.Update(mesh, time);
    }

    public void DrawMesh3D(Season.Controls.Mesh3D mesh)
    {
        if (mesh.Alpha == 0f)
            return;

        DXMesh3D dxMesh = null;
        lock (DictionaryMesh3D)
        {
            DictionaryMesh3D.TryGetValue((mesh.Name, mesh.ID), out dxMesh);
        }
        dxMesh?.Draw();
    }

    public void DisposeMesh3D(Season.Controls.Mesh3D mesh)
    {
        DXMesh3D dxMesh = null;
        lock (DictionaryMesh3D)
        {
            var key = (mesh.Name, mesh.ID);
            if (DictionaryMesh3D.TryGetValue(key, out dxMesh))
                DictionaryMesh3D.Remove(key);
        }

        // GPU resources must be released behind a retire fence (same contract as
        // DisposeSprite2D/DisposeShape): in-flight frames may still reference VB/IB/CB during
        // runtime removal, and releasing immediately would race the render thread and trigger SEHException.
        ulong retireFence = DirectX.Device.GetCurrentRetireFenceValue();
        if (dxMesh != null)
            EnqueueDeferredRelease(retireFence, dxMesh.Dispose);

        // Release textures referenced by Surface according to the DXTexture reference count:
        // path sources follow the old contract and release only the BaseColor path, while
        // procedural pixel sources release all five synthetic names (private to this mesh).
        lock (DictionaryDXTexture)
        {
            for (int i = 0; i < mesh.Surfaces.Count; i++)
            {
                var path = mesh.Surfaces[i].BaseColorTexturePath;
                if (!string.IsNullOrEmpty(path)
                    && DictionaryDXTexture.TryGetValue(path, out var dxTexture) && dxTexture != null)
                {
                    if (dxTexture.RefCount <= 1)
                    {
                        DictionaryDXTexture.Remove(path);
                        EnqueueDeferredRelease(retireFence, dxTexture.Release);
                    }
                    else
                    {
                        dxTexture.Release();
                    }
                }

                ReleaseProcSurfaceTextures(mesh.Name, mesh.ID, i, retireFence);
            }
        }

        mesh.Ready = false;
    }

    public async Task<bool> LoadInstancedMesh3D(InstancedMesh3D mesh)
    {
        lock (DictionaryInstancedMesh3D)
        {
            if (DictionaryInstancedMesh3D.ContainsKey((mesh.Name, mesh.ID)))
                return true;
        }

        // 1. Pre-resolve all texture sources referenced by Surface: pixel sources upload directly
        //    to the GPU through CreateFromDecoder (no disk round-trip), and path sources reuse
        //    EnsureDXTexture (empty sources are skipped automatically).
        for (int i = 0; i < mesh.Surfaces.Count; i++)
            EnsureSurfaceTextures(mesh.Name, mesh.ID, i, mesh.Surfaces[i]);

        ExecuteUpload();

        var dxMesh = new DXInstancedMesh3D(mesh.Name);
        dxMesh.Load(mesh, DXPrimitiveGroup.Camera, BuildSurfaceTextureResolver(mesh.Name, mesh.ID, mesh.Surfaces));

        // Pixel sources have been consumed: clear each slot's Override (single-consumption contract).
        foreach (var surface in mesh.Surfaces)
            ClearSurfaceOverrides(surface);

        lock (DictionaryInstancedMesh3D)
        {
            if (!DictionaryInstancedMesh3D.ContainsKey((mesh.Name, mesh.ID)))
                DictionaryInstancedMesh3D.Add((mesh.Name, mesh.ID), dxMesh);
            else
                dxMesh.Dispose();
        }

        return true;
    }

    public void UpdateInstancedMesh3D(InstancedMesh3D mesh, float time)
    {
        DXInstancedMesh3D dxMesh = null;
        lock (DictionaryInstancedMesh3D)
        {
            DictionaryInstancedMesh3D.TryGetValue((mesh.Name, mesh.ID), out dxMesh);
        }
        dxMesh?.Update(mesh, time);
    }

    public void DrawInstancedMesh3D(InstancedMesh3D mesh)
    {
        if (mesh.Alpha == 0f)
            return;

        DXInstancedMesh3D dxMesh = null;
        lock (DictionaryInstancedMesh3D)
        {
            DictionaryInstancedMesh3D.TryGetValue((mesh.Name, mesh.ID), out dxMesh);
        }
        dxMesh?.Draw();
    }

    public void DisposeInstancedMesh3D(InstancedMesh3D mesh)
    {
        DXInstancedMesh3D dxMesh = null;
        lock (DictionaryInstancedMesh3D)
        {
            var key = (mesh.Name, mesh.ID);
            if (DictionaryInstancedMesh3D.TryGetValue(key, out dxMesh))
                DictionaryInstancedMesh3D.Remove(key);
        }

        // Same contract as DisposeMesh3D: in-flight frames may still reference these resources,
        // so release must be deferred behind a retire fence.
        ulong retireFence = DirectX.Device.GetCurrentRetireFenceValue();
        if (dxMesh != null)
            EnqueueDeferredRelease(retireFence, dxMesh.Dispose);

        // Release textures referenced by Surface according to the DXTexture reference count:
        // path sources follow the old contract and release only the BaseColor path, while
        // procedural pixel sources release all five synthetic names (private to this mesh).
        lock (DictionaryDXTexture)
        {
            for (int i = 0; i < mesh.Surfaces.Count; i++)
            {
                var path = mesh.Surfaces[i].BaseColorTexturePath;
                if (!string.IsNullOrEmpty(path)
                    && DictionaryDXTexture.TryGetValue(path, out var dxTexture) && dxTexture != null)
                {
                    if (dxTexture.RefCount <= 1)
                    {
                        DictionaryDXTexture.Remove(path);
                        EnqueueDeferredRelease(retireFence, dxTexture.Release);
                    }
                    else
                    {
                        dxTexture.Release();
                    }
                }

                ReleaseProcSurfaceTextures(mesh.Name, mesh.ID, i, retireFence);
            }
        }

        mesh.Ready = false;
    }

    // ============================================================
    // InstancedModel（GLB GPU Instancing）
    // ============================================================

    public async Task<bool> LoadInstancedModel(InstancedModel model)
    {
        lock (DictionaryInstancedModel)
        {
            if (DictionaryInstancedModel.ContainsKey((model.ModelName, model.ID)))
            {
                return true;
            }
        }

        GetOrCreateSharedModelAsync(model.ModelName).ContinueWith(task =>
        {
            var template = task.GetAwaiter().GetResult();

            var wrapperModel = new Season.Controls.Model
            {
                Name = model.ModelName,
                Alpha = model.Alpha,
                MaterialColor = null,
                Unlit = false
            };

            var dxInstancedModel = new DXInstancedModel(model.ModelName);
            dxInstancedModel.Load(template, wrapperModel, DXPrimitiveGroup.Camera);

            // v2 picking: inject the instantiated GltfAsset so the node tree, animation, and bone
            // palette share the same source as instanced rendering.
            model.Asset = dxInstancedModel._asset;

            // 1-3: backfill the control with the shared template local box
            // (instance-level sphere quick-reject data, once at load time).
            model.TemplateLocalBounds = template._asset.Model.LocalBounds;
            // Unified placement convention: likewise backfill the original box
            // (data source for instance anchors and per-axis scaling, before animation expansion).
            model.TemplateLocalBoundsRaw = template._asset.Model.LocalBoundsRaw;

            // Fill animation metadata.
            var animNames = dxInstancedModel.GetAnimationNames();
            model.AnimationClipCount = animNames.Count;
            model.AnimationNames = animNames;

            lock (DictionaryInstancedModel)
            {
                if (!DictionaryInstancedModel.ContainsKey((model.ModelName, model.ID)))
                    DictionaryInstancedModel.Add((model.ModelName, model.ID), dxInstancedModel);
                else
                    // Same contract as DisposeInstancedModel: newly created GPU resources may
                    // still be in flight, so defer release behind a retire fence.
                    EnqueueDeferredRelease(DirectX.Device.GetCurrentRetireFenceValue(), dxInstancedModel.Dispose);
            }
        });

        return true;
    }

    public void UpdateInstancedModel(InstancedModel model, float time)
    {
        DXInstancedModel dxModel = null;
        lock (DictionaryInstancedModel)
        {
            DictionaryInstancedModel.TryGetValue((model.ModelName, model.ID), out dxModel);
        }
        dxModel?.Update(model, time);
    }

    public void DrawInstancedModel(InstancedModel model)
    {
        if (model.Alpha == 0f)
            return;

        DXInstancedModel dxModel = null;
        lock (DictionaryInstancedModel)
        {
            DictionaryInstancedModel.TryGetValue((model.ModelName, model.ID), out dxModel);
        }
        dxModel?.Draw();
    }

    public void DisposeInstancedModel(InstancedModel model)
    {
        DXInstancedModel dxModel = null;
        lock (DictionaryInstancedModel)
        {
            var key = (model.ModelName, model.ID);
            if (DictionaryInstancedModel.TryGetValue(key, out dxModel))
                DictionaryInstancedModel.Remove(key);
        }

        // Same contract as DisposeMesh3D: in-flight frames may still reference these resources,
        // so release must be deferred behind a retire fence.
        ulong retireFence = DirectX.Device.GetCurrentRetireFenceValue();
        if (dxModel != null)
            EnqueueDeferredRelease(retireFence, dxModel.Dispose);
        model.Ready = false;
    }

    public void DisposeSprite2D(Sprite2D sprite)
    {
        DXSprite2D dxSprite2D = null;
        ulong retireFence = DirectX.Device.GetCurrentRetireFenceValue();

        lock (DictionarySprite)
        {
            var key = (sprite.Name, sprite.ID);
            if (DictionarySprite.TryGetValue(key, out dxSprite2D))
            {
                DictionarySprite.Remove(key);
            }
        }

        if (dxSprite2D != null)
        {
            dxSprite2D.SpriteRef = null; // Clear the reference.
            EnqueueDeferredRelease(retireFence, dxSprite2D.Dispose);

            // Release the shared texture only if the Sprite was actually loaded and holds a texture reference.
            lock (DictionaryDXTexture)
            {
                if (DictionaryDXTexture.TryGetValue(sprite.Name, out var dxTexture) && dxTexture != null)
                {
                    if (dxTexture.RefCount <= 1)
                    {
                        DictionaryDXTexture.Remove(sprite.Name);
                        EnqueueDeferredRelease(retireFence, dxTexture.Release);
                    }
                    else
                    {
                        dxTexture.Release();
                    }
                }
            }
        }

        sprite.Ready = false;
    }

    // ============================================================
    // Shape (procedural geometry)
    // ============================================================
    public async Task<bool> LoadShape(Season.Controls.Shape shape)
    {
        if (shape.Type != Season.Controls.ShapeType.Dot && (shape.Width <= 0 || shape.Height <= 0))
        {
            return false;
        }

        // Width/Height may still be unset (null) when AddControl runs: (int)(float?)null throws
        // InvalidOperationException, and this code path sits outside the try/catch, so Ready would
        // never be set after Load fails.
        // Normalize null -> 1 as fallback here as well (CreateShapeImage also clamps with Max(1, ...)).
        int shapeW = Math.Max(1, (int)(shape.Width ?? 1f));
        int shapeH = Math.Max(1, (int)(shape.Height ?? 1f));

        // RectFrame textures are determined by the tuple (Type, W, H, Border), while Border stays
        // 0 for other types.
        // Apply the same [1, min(W, H) / 2] clamp as CreateImageRectFrame to avoid multiple copies
        // of the same texture under different keys.
        int shapeBorder = shape.Type == Season.Controls.ShapeType.RectFrame
            ? Math.Clamp((int)shape.Border, 1, Math.Min(shapeW, shapeH) / 2)
            : 0;

        var textureKey = shape.Type == Season.Controls.ShapeType.Dot
            ? (shape.Type, 1, 1, 0)
            : (shape.Type, shapeW, shapeH, shapeBorder);
        var instanceKey = (shape.Type, shape.ID);

        DXSprite2D dxSprite2D = null;

        lock (DictionaryShape)
        {
            if (shape.IsDisposed) return false;

            // A previous failure may have cached a null entry: treat it as missing, remove it, and rebuild.
            if (DictionaryShape.TryGetValue(instanceKey, out dxSprite2D)
                && (dxSprite2D == null || dxSprite2D.DXTexture == null))
            {
                DictionaryShape.Remove(instanceKey);
                dxSprite2D = null;
            }

            if (dxSprite2D != null)
            {
                shape.OriginWidth = (int)dxSprite2D.DXTexture.Width;
                shape.OriginHeight = (int)dxSprite2D.DXTexture.Height;
            }
            else
            {
                // Get or create the shared shape texture (cached by Type + Width + Height).
                DXTexture dxTexture = null;

                lock (DictionaryShapeTexture)
                {
                    if (DictionaryShapeTexture.TryGetValue(textureKey, out dxTexture!))
                    {

                    }
                    else
                    {
                        var iNativeImageDecoder = Season.Models.ImageUtils.CreateShapeImage(shape.Type, shapeW, shapeH, shapeBorder);

                        if (iNativeImageDecoder != null)
                        {
                            dxTexture = new DXTexture(iNativeImageDecoder);
                        }

                        if (dxTexture == null)
                        {
                            DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} dxTexture == null {shape.Type}");
                        }
                        else
                        {
                            ExecuteUpload();

                            // Cache only on success to avoid polluting later requests for the same key with null.
                            DictionaryShapeTexture[textureKey] = dxTexture;
                        }

                        //decoder.Dispose();
                    }
                }

                if (dxTexture == null)
                {
                    // Shared texture creation failed: do not register an empty entry; return false
                    // from Load so the upper layer can pinpoint it in logs.
                    return false;
                }

                try
                {
                    dxSprite2D = new DXSprite2D(dxTexture);

                    shape.OriginWidth = (int)dxSprite2D.DXTexture.Width;
                    shape.OriginHeight = (int)dxSprite2D.DXTexture.Height;
                }
                catch (Exception ex)
                {
                    DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} LoadShape new DXSprite2D {shape.Type} {ex}");

                    return false;
                }

                lock (DictionaryShape)
                {
                    if (DictionaryShape.ContainsKey(instanceKey))
                    {

                    }
                    else
                    {
                        DictionaryShape.Add(instanceKey, dxSprite2D);
                    }
                }
            }
        }

        return true;
    }

    public void UpdateShape(Season.Controls.Shape shape)
    {
        DXSprite2D? dxSprite = null;

        lock (DictionaryShape)
        {
            DictionaryShape.TryGetValue((shape.Type, shape.ID), out dxSprite);
        }

        if (dxSprite == null || dxSprite.DXTexture == null)
            return;

        shape.Ready = true;

        // Texture replacement.
        if (shape.TextureOverride.HasValue)
        {
            var source = shape.TextureOverride;
            shape.TextureOverride = default;
            ReplaceSpriteTexture(dxSprite, source);
        }

        if (shape.Changed)
        {
            shape.Changed = false;
            dxSprite.SpriteRef = shape;
            dxSprite.Update();
        }
    }

    public void DrawShape(Season.Controls.Shape shape)
    {
        DXSprite2D? dxSprite = null;

        lock (DictionaryShape)
        {
            DictionaryShape.TryGetValue((shape.Type, shape.ID), out dxSprite);
        }

        if (dxSprite == null || dxSprite.DXTexture == null || dxSprite.DXTexture._textureResource == null)
            return;

        dxSprite.Draw();
    }

    public void DisposeShape(Season.Controls.Shape shape)
    {
        DXSprite2D? dxSprite = null;
        ulong retireFence = DirectX.Device.GetCurrentRetireFenceValue();

        lock (DictionaryShape)
        {
            var key = (shape.Type, shape.ID);
            if (DictionaryShape.TryGetValue(key, out dxSprite))
                DictionaryShape.Remove(key);
        }

        if (dxSprite != null)
            EnqueueDeferredRelease(retireFence, dxSprite.Dispose);

        // Shape textures are managed centrally by DictionaryShapeTexture; releasing by reference
        // count can be added later if needed.

        shape.Ready = false;
    }

    public void ExecuteUpload()
    {
        DirectX.Device.textureUploadBatch.ExecuteFullUploads(DirectX.Device.CopyGraphicsCommandList, DirectX.Device.CopyCommandQueue);
    }

    // ── Pass scheduling (Step 1) / offscreen rendering (Step 2): delegated to DirectX.Device ──
    public Season.Rendering.RenderTarget CreateRenderTarget(in Season.Rendering.RenderTargetDesc desc) => DirectX.Device.CreateRenderTarget(desc);

    public void BeginPass(in PassDesc desc) => DirectX.Device.BeginPass(desc);

    public void EndPass() => DirectX.Device.EndPass();

    Season.Rendering.RenderTarget EnsureOutlineMaskTarget()
    {
        if (_outlineMaskTarget != null)
            return _outlineMaskTarget;

        _outlineMaskTarget = CreateRenderTarget(new Season.Rendering.RenderTargetDesc
        {
            ColorFormat = Season.Rendering.RtFormat.BackbufferCompatible,
            MatchBackbufferSize = true,
            SampleCount = 1,
            // The mask pass is always cleared with Vector4.Zero (see RenderOutlineMask), so any
            // optimized clear value must also be zero; otherwise it will not match the background
            // color baked at creation time and will trigger CLEARRENDERTARGETVIEW_MISMATCHINGCLEARVALUE every frame.
            ClearColor = Vector4.Zero,
        });
        return _outlineMaskTarget;
    }

    bool TryAccumulateOutline2D(DXPrimitiveGroup group)
    {
        if (group == null || !group.Outline2DActive)
            return false;

        // Each group carries its color per pixel inside the mask
        // (multiple colors can coexist in one frame; see PSOutlineMask/PSMainOutlineComposite).
        // At the frame level only width is accumulated, taking the maximum so the widest outline
        // remains fully visible.
        _outline2DFrameActive = true;
        _outline2DFrameWidth = MathF.Max(_outline2DFrameWidth, group.Outline2DMaskWidth);

        return true;
    }

    public void RenderOutlineMask()
    {
        _outline2DFrameActive = false;
        _outline2DFrameWidth = 0f;

        var drawGroups = new List<DXPrimitiveGroup>();

        lock (DictionaryModel)
        {
            foreach (var pair in DictionaryModel)
            {
                if (TryAccumulateOutline2D(pair.Value))
                    drawGroups.Add(pair.Value);
            }
        }

        lock (DictionaryMesh3D)
        {
            foreach (var pair in DictionaryMesh3D)
            {
                if (TryAccumulateOutline2D(pair.Value))
                    drawGroups.Add(pair.Value);
            }
        }

        // Instanced controls (InstancedMesh3D / InstancedModel): Outline2D also supports
        // per-instance masks, with the active state aggregated during the platform Update stage
        // from each instance/host Highlight.Outline2D (DXInstancedPrimitiveGroup).
        lock (DictionaryInstancedMesh3D)
        {
            foreach (var pair in DictionaryInstancedMesh3D)
            {
                if (TryAccumulateOutline2D(pair.Value))
                    drawGroups.Add(pair.Value);
            }
        }

        lock (DictionaryInstancedModel)
        {
            foreach (var pair in DictionaryInstancedModel)
            {
                if (TryAccumulateOutline2D(pair.Value))
                    drawGroups.Add(pair.Value);
            }
        }

        if (!_outline2DFrameActive || drawGroups.Count == 0)
            return;

        BeginPass(new PassDesc
        {
            Id = RenderPassId.OutlineMask,
            ColorTarget = EnsureOutlineMaskTarget(),
            DepthTarget = Season.Rendering.FrameSchedule.SceneDepth,
            ClearColor = Vector4.Zero,
            ClearColorEnable = true,
            ClearDepthEnable = false,
            StoreDepth = false,
        });

        for (int i = 0; i < drawGroups.Count; i++)
        {
            drawGroups[i].DrawOutlineMask();
        }

        EndPass();

        // Fallback reset for b6 boneBase: the slot base written by per-slot drawing in the mask
        // pass must not leak into the main/shadow passes of later frames.
        // The main/shadow passes already reset it in SetPipeline/SetShadowPipeline respectively;
        // this is one extra guard.
        Pipeline.ResetOutlineMaskBoneBase();
    }

    /// <summary>2-1 Step B: bloom-chain output is registered in the instance dictionary
    /// (the compute texture registry). Resolve it by name here and hand it to the static Device
    /// for composition; if it is unregistered or not ready, the Device falls back to the existing
    /// variant with no residue.
    /// 2-1 Step C: when the source is PostColor (the FXAA-tier Post uber pass has already
    /// completed composition), switch to the FXAA variant for presentation and skip bloom lookup.
    /// 2-2 Step B: AO output is forwarded the same way through FrameSchedule.AoTexture
    /// (null = no AO, with no residue).
    /// 2-3 Contract clause 12: scene source is forwarded the same way through
    /// FrameSchedule.SceneColorOverride (the TAA tier uses the resolve output). In the FXAA tier,
    /// this entry point has already degenerated into FXAA resolve because composition finished in
    /// Post, so overrides only take effect in RenderPostPass.</summary>
    public void BlitToBackbuffer(Season.Rendering.RenderTarget src)
    {
        if (ReferenceEquals(src, Season.Rendering.FrameSchedule.PostColor))
        {
            DirectX.Device.BlitToBackbuffer(src, null, fxaa: true,
                outlineMask: _outline2DFrameActive ? _outlineMaskTarget : null,
                outlineWidth: _outline2DFrameWidth);
            return;
        }

        DirectX.Device.BlitToBackbuffer(src, ResolveBloomTexture(), aoTex: ResolveAoTexture(),
            sceneTex: ResolveSceneOverrideTexture(),
            outlineMask: _outline2DFrameActive ? _outlineMaskTarget : null,
            outlineWidth: _outline2DFrameWidth);
    }

    /// <summary>2-1 Step C: post-pass body (FrameSchedule.RenderPost callback, registered as a pair
    /// with PostColor in the FXAA tier): the uber pass composes tonemap(+bloom) into LDR PostColor
    /// and bakes luma into alpha. After composition moved downstream, FinalBlit degenerated into
    /// FXAA resolve; see the RenderQuality 1-4 contract 1 revision.
    /// 2-2 Step B: AO is forwarded at the same point.
    /// 2-3 Clause 12: scene overrides are also forwarded at the same point.</summary>
    internal void RenderPostPass(Season.Basic.IGraphics g, Season.Rendering.RenderTarget sceneColor)
        => DirectX.Device.RenderPostUber(sceneColor, ResolveBloomTexture(), ResolveAoTexture(),
            ResolveSceneOverrideTexture());

    /// <summary>Resolves bloom-chain output from the instance dictionary through FrameSchedule.BloomTexture (null = no bloom).</summary>
    DXTexture ResolveBloomTexture()
    {
        var bloomName = Season.Rendering.FrameSchedule.BloomTexture;
        if (bloomName == null)
            return null;
        lock (DictionaryDXTexture)
        {
            DictionaryDXTexture.TryGetValue(bloomName, out var bloom);
            return bloom;
        }
    }

    /// <summary>2-2 Step B: resolves GTAO output from the instance dictionary through FrameSchedule.AoTexture (null = no AO).</summary>
    DXTexture ResolveAoTexture()
    {
        var aoName = Season.Rendering.FrameSchedule.AoTexture;
        if (aoName == null)
            return null;
        lock (DictionaryDXTexture)
        {
            DictionaryDXTexture.TryGetValue(aoName, out var ao);
            return ao;
        }
    }

    /// <summary>2-3 Contract clause 12: resolves TAA resolve output from the instance dictionary
    /// through FrameSchedule.SceneColorOverride
    /// (null = no override, and the Device falls back to the SceneColor RT with no residue).</summary>
    DXTexture ResolveSceneOverrideTexture()
    {
        var sceneName = Season.Rendering.FrameSchedule.SceneColorOverride;
        if (sceneName == null)
            return null;
        lock (DictionaryDXTexture)
        {
            DictionaryDXTexture.TryGetValue(sceneName, out var scene);
            return scene;
        }
    }
}
