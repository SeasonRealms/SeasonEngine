// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Season.Rendering;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace Season.Platforms.Windows.DirectX;

/// <summary>
/// D3D12 implementation of the D1 screen-space decal.
///
/// One 36-vertex DrawInstanced per decal: the vertex shader computes the six faces of the projection box from SV_VertexID, and the rasterizer keeps only the back faces
/// (<see cref="CullMode.Front"/>), so each pixel is hit at most once - blending is not applied twice, and no stencil is needed.
/// The pixel shader reconstructs the world position from this frame's depth, folds it into the box's local space and takes the UV; the color is written back to SceneColor through fixed-function blending,
/// so SceneColor does not need to be a samplable resource at the same time - inside the Pass it is only a color attachment.
///
/// The timing is the same as <see cref="DXFogCompositePipeline"/>: Prepare performs the depth/normal state transitions and
/// descriptor snapshot outside the Pass, and Draw may consume it only once inside <see cref="RenderPassId.SceneDecal"/>; if anything differs from the prepare phase
/// it gives up the draw rather than drawing partially.
/// </summary>
internal sealed unsafe class DXDecalPipeline : DecalPipeline
{
    // Root parameter indices, corresponding one-to-one with t0/t1/t2 and b0 in DecalShaders.Hlsl.
    const uint FrameRoot = 0;
    const uint DepthRoot = 1;
    const uint TextureRoot = 2;
    const uint ParamsRoot = 3;

    readonly List<BindingSlot>[] _slots = new List<BindingSlot>[(int)Device.frameCount];
    readonly int[] _cursors = new int[(int)Device.frameCount];
    readonly ulong[] _stamps = new ulong[(int)Device.frameCount];
    ID3D12RootSignature* _signature;
    // [mode 0=Modulate/1=Additive, whether it has a normal attachment]. The roughness write of s9 needs a second color attachment,
    // and the number of attachments must match the RT count of the PSO, so one copy is kept here for each of the two attachment shapes.
    readonly ID3D12PipelineState*[,] _pipelines = new ID3D12PipelineState*[2, 2];
    /// <summary>Per-decal texture resolver: it only looks up the backend texture dictionary, it does not load. name -> when the texture is null this draw is skipped.</summary>
    readonly Func<string, DXTexture?> _resolve;
    readonly DXTexture?[] _textures = new DXTexture?[DecalMath.MaxDecals];
    readonly bool[] _skip = new bool[DecalMath.MaxDecals];
    ID3D12GraphicsCommandList* _command;
    DecalInputs _inputs;
    DXRenderTarget? _destination;
    DXStorageBuffer? _frame;
    BindingSlot? _prepared;
    ulong _stamp;
    uint _frameIndex;
    bool _writeRoughness;
    bool _disposed;

    /// <summary>
    /// Descriptor snapshot for depth and normal. Keeping the references alive prevents the native address from being reused across a window Resize,
    /// and the descriptor also cannot be borrowed in place from the shared visible heap - exactly the same reason as for fog composite.
    /// </summary>
    sealed class BindingSlot
    {
        internal readonly int[] Ids = [-1, -1];
        internal readonly nint[] Resources = new nint[2];
        internal readonly ShaderResourceViewDesc[] Views = new ShaderResourceViewDesc[2];

        internal BindingSlot()
        {
            try { for (int i = 0; i < Ids.Length; i++) Ids[i] = Device.DescriptorAllocator.Allocate(); }
            catch { Dispose(); throw; }
        }

        internal void ReleaseResources()
        {
            for (int i = 0; i < Views.Length; i++)
            {
                if (Resources[i] == 0) continue;
                var view = Views[i];
                Device.D3dDevice->CreateShaderResourceView(null, &view, Device.SrvHeapManager.GetCpuHandle(Ids[i]));
            }
            for (int i = 0; i < Resources.Length; i++)
            {
                if (Resources[i] != 0) ((ID3D12Resource*)Resources[i])->Release();
                Resources[i] = 0;
            }
        }

        internal void Capture(ReadOnlySpan<nint> resources, ReadOnlySpan<ShaderResourceViewDesc> views)
        {
            ReleaseResources();
            resources.CopyTo(Resources); views.CopyTo(Views);
            foreach (nint resource in Resources) ((ID3D12Resource*)resource)->AddRef();
            for (int i = 0; i < Views.Length; i++)
            {
                var view = Views[i];
                Device.D3dDevice->CreateShaderResourceView((ID3D12Resource*)Resources[i], &view,
                    Device.SrvHeapManager.GetCpuHandle(Ids[i]));
            }
        }

        internal void Dispose()
        {
            ReleaseResources();
            for (int i = 0; i < Ids.Length; i++)
            {
                if (Ids[i] >= 0) Device.DescriptorAllocator.Free(Ids[i]);
                Ids[i] = -1;
            }
        }
    }

    internal DXDecalPipeline(Func<string, DXTexture?> resolve)
    {
        _resolve = resolve;
        for (int i = 0; i < _slots.Length; i++) _slots[i] = new();
        try { Initialize(); }
        catch { Dispose(); throw; }
    }

    void Initialize()
    {
        var parameters = stackalloc RootParameter[4];
        // The frame buffer is a root SRV: there is only one per frame, and both the vertex and pixel stages need to read it, so there is no need to occupy a descriptor for it separately.
        parameters[FrameRoot] = new() { ParameterType = RootParameterType.TypeSrv,
            Descriptor = new RootDescriptor { ShaderRegister = 0, RegisterSpace = 0 },
            ShaderVisibility = ShaderVisibility.All };
        var ranges = stackalloc DescriptorRange[2];
        for (int i = 0; i < 2; i++)
        {
            ranges[i] = new() { RangeType = DescriptorRangeType.Srv, NumDescriptors = 1, BaseShaderRegister = (uint)(i + 1) };
            parameters[DepthRoot + i] = new() { ParameterType = RootParameterType.TypeDescriptorTable,
                DescriptorTable = new() { NumDescriptorRanges = 1, PDescriptorRanges = ranges + i },
                ShaderVisibility = ShaderVisibility.Pixel };
        }
        parameters[ParamsRoot] = new() { ParameterType = RootParameterType.Type32BitConstants,
            Constants = new() { ShaderRegister = 0, Num32BitValues = 4 }, ShaderVisibility = ShaderVisibility.All };
        var sampler = new StaticSamplerDesc { Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp,
            MaxLOD = float.MaxValue, ComparisonFunc = ComparisonFunc.Always, ShaderVisibility = ShaderVisibility.Pixel };
        var root = new RootSignatureDesc { NumParameters = 4, PParameters = parameters,
            NumStaticSamplers = 1, PStaticSamplers = &sampler };
        using ComPtr<ID3D10Blob> blob = null;
        using ComPtr<ID3D10Blob> error = null;
        Device.CheckResult(Device.D3D12.SerializeRootSignature(&root, D3DRootSignatureVersion.Version1,
            blob.GetAddressOf(), error.GetAddressOf()));
        ID3D12RootSignature* signature = null;
        var iid = ID3D12RootSignature.Guid;
        Device.CheckResult(Device.D3dDevice->CreateRootSignature(0, blob.Get().GetBufferPointer(),
            blob.Get().GetBufferSize(), &iid, (void**)&signature));
        _signature = signature;

        ID3D10Blob* vertex = null;
        try
        {
            string common = DecalShaders.FrameHlsl;
            vertex = ShaderCompiler.CompileShaderFromSource(common + DecalShaders.VertexHlsl, "VSMain", "vs_5_0");
            for (int roughness = 0; roughness < 2; roughness++)
            {
                ID3D10Blob* fragment = ShaderCompiler.CompileShaderFromSource(common + DecalShaders.PixelHlsl,
                    roughness == 0 ? "PSMain" : "PSMainRoughness", "ps_5_0");
                try
                {
                    _pipelines[0, roughness] = CreatePipeline(vertex, fragment, Blend.DestColor, roughness == 1);
                    _pipelines[1, roughness] = CreatePipeline(vertex, fragment, Blend.One, roughness == 1);
                }
                finally { fragment->Release(); }
            }
        }
        finally
        {
            if (vertex != null) vertex->Release();
        }
    }

    /// <summary>
    /// The two blend semantics share one shader, only the source factors differ:
    /// Modulate uses <c>dst * src</c> (SrcBlend = DestColor, DestBlend = Zero) treating the pixel output as a modulation amount,
    /// Additive uses <c>dst + src</c>. The alpha always keeps the destination value, and the decal does not participate in the subsequent alpha test.
    ///
    /// When <paramref name="roughness"/> is true a second color attachment (SceneNormal) is attached. It only enables the B channel write mask,
    /// and interpolates on the original value with coverage, so the octahedral normal and the metalness are not touched.
    /// </summary>
    ID3D12PipelineState* CreatePipeline(ID3D10Blob* vertex, ID3D10Blob* fragment, Blend source, bool roughness)
    {
        var stencil = new DepthStencilopDesc { StencilFailOp = StencilOp.Keep, StencilDepthFailOp = StencilOp.Keep,
            StencilPassOp = StencilOp.Keep, StencilFunc = ComparisonFunc.Always };
        var desc = new GraphicsPipelineStateDesc {
            PRootSignature = _signature,
            VS = new(vertex->GetBufferPointer(), vertex->GetBufferSize()),
            PS = new(fragment->GetBufferPointer(), fragment->GetBufferSize()),
            // Keep only the back faces: the projection box is a convex body, and the back face is hit exactly once per pixel.
            RasterizerState = new() { FillMode = FillMode.Solid, CullMode = CullMode.Front, DepthClipEnable = 1 },
            DepthStencilState = new() { DepthFunc = ComparisonFunc.Always, FrontFace = stencil, BackFace = stencil },
            SampleMask = uint.MaxValue, PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            NumRenderTargets = roughness ? 2u : 1u, SampleDesc = new(1, 0) };
        // BlendEnable must be explicitly enabled: when it is 0 the blend factors are all ignored and the PS output directly overwrites SceneColor -
        // Modulate writes 1.0 where coverage=0 inside the box (a white block, even the characters behind the box are covered), Additive writes 0 (a black block).
        // The two attachments blend differently, so IndependentBlendEnable is also needed, otherwise RT1 would apply RT0's settings and overwrite the normal entirely.
        desc.BlendState.IndependentBlendEnable = roughness ? 1u : 0u;
        desc.BlendState.RenderTarget[0] = new() { BlendEnable = 1, SrcBlend = source,
            DestBlend = source == Blend.One ? Blend.One : Blend.Zero, BlendOp = BlendOp.Add,
            SrcBlendAlpha = Blend.Zero, DestBlendAlpha = Blend.One, BlendOpAlpha = BlendOp.Add,
            LogicOp = LogicOp.Noop, RenderTargetWriteMask = (byte)ColorWriteEnable.All };
        desc.RTVFormats[0] = Format.FormatR16G16B16A16Float;
        if (roughness)
        {
            // Write only .z (Blue): the octahedral encoding of the normal is in .xy and the reflection weight is in .w, both must be preserved.
            // The blend lerps with coverage; when coverage is 0 the result is exactly the original value, so a decal that "does not touch roughness" does not change the destination.
            desc.BlendState.RenderTarget[1] = new() { BlendEnable = 1, SrcBlend = Blend.SrcAlpha,
                DestBlend = Blend.InvSrcAlpha, BlendOp = BlendOp.Add,
                SrcBlendAlpha = Blend.One, DestBlendAlpha = Blend.Zero, BlendOpAlpha = BlendOp.Add,
                LogicOp = LogicOp.Noop, RenderTargetWriteMask = (byte)ColorWriteEnable.Blue };
            desc.RTVFormats[1] = Format.FormatR16G16B16A16Float;
        }
        ID3D12PipelineState* pipeline = null;
        var iid = ID3D12PipelineState.Guid;
        Device.CheckResult(Device.D3dDevice->CreateGraphicsPipelineState(&desc, &iid, (void**)&pipeline));
        return pipeline;
    }

    static ShaderResourceViewDesc TextureView(Format format) => new() {
        Format = format, Shader4ComponentMapping = 0x1688, ViewDimension = SrvDimension.Texture2D,
        Texture2D = new() { MipLevels = 1 } };

    /// <summary>
    /// Verify the shape and fold the resources into a descriptor snapshot. Returning false means giving up this frame: the decal only
    /// holds for the shape "color is single-sample HDR, depth and color are the same size"; in other cases it will produce no image rather than a wrong image.
    ///
    /// After s9 the normal target is an <b>attachment</b> rather than a sampling source: the same resource cannot be bound as both RTV and SRV.
    /// So both descriptor slots point at the depth, and the second slot is left for the per-decal texture of s3.
    /// </summary>
    bool Resolve(in DecalInputs inputs, DXRenderTarget destination, Span<nint> resources,
        Span<ShaderResourceViewDesc> views, bool transition, out DXStorageBuffer? frame, out bool roughness)
    {
        frame = null;
        roughness = false;
        if (inputs.Color is not DXRenderTarget color || inputs.Depth is not DXRenderTarget depth
            || inputs.FrameBuffer is not DXStorageBuffer buffer || buffer.Resource == null
            || buffer.SizeInBytes < DecalMath.ByteCount
            || color.Desc.ColorFormat != RtFormat.Rgba16Float || destination.Desc.ColorFormat != RtFormat.Rgba16Float
            || color.Desc.SampleCount != 1 || depth.Desc.SampleCount != 1 || destination.Desc.SampleCount != 1
            || color.Color == null || depth.Depth == null || destination.Color == null
            || color.ColorNativeFormat != Format.FormatR16G16B16A16Float
            || destination.ColorNativeFormat != Format.FormatR16G16B16A16Float
            || depth.Desc.DepthFormat != RtFormat.D32Float
            // This Pass uses the color target also as the blend base image, so the shared layer must hand out the same target; with another RT it would no longer be "decaled onto the scene".
            || !ReferenceEquals(destination, color)
            || color.Width == 0 || color.Height == 0 || color.Width != depth.Width || color.Height != depth.Height
            || inputs.Count <= 0 || inputs.Count > DecalMath.MaxDecals) return false;
        // The roughness write requires the normal target to have the same shape as the color; if not satisfied only the roughness write is skipped, and it should not make the color decal disappear as well.
        DXRenderTarget? normal = inputs.Normal as DXRenderTarget;
        if (normal != null && (normal.Color == null || normal.Desc.SampleCount != 1
            || normal.Desc.ColorFormat != RtFormat.Rgba16Float
            || normal.Width != color.Width || normal.Height != color.Height)) normal = null;
        roughness = inputs.WriteRoughness && normal != null;
        resources[0] = (nint)depth.Depth;
        resources[1] = (nint)depth.Depth;
        views[0] = TextureView(Format.FormatR32Float);
        views[1] = views[0];
        if (transition)
        {
            var command = Device.GraphicsCommandList;
            buffer.TransitionTo(command, ResourceStates.AllShaderResource);
            depth.TransitionDepthTo(command, ResourceStates.AllShaderResource);
        }
        else
        {
            const ResourceStates read = ResourceStates.PixelShaderResource;
            if ((buffer.CurrentState & read) == 0 || (depth.DepthCurrentState & read) == 0
                || destination.CurrentState != ResourceStates.RenderTarget
                // The attachment was bound by BeginPass, here we only verify it is indeed still in the render target state.
                || (roughness && !ReferenceEquals(Device.ActiveNormalTarget, normal))) return false;
        }
        frame = buffer;
        return true;
    }

    public override bool Prepare(in DecalInputs inputs, RenderTarget destination)
    {
        _destination = null; _prepared = null; _inputs = default; _frame = null;
        if (_disposed || Device.ActiveTarget != null || Device.FrameContexts == null
            || Device.FrameIndex >= _slots.Length || Device.GraphicsCommandList == null
            || destination is not DXRenderTarget target) return false;
        int index = (int)Device.FrameIndex;
        ulong stamp = Device.GetCurrentRetireFenceValue();
        if (_stamps[index] != stamp)
        {
            if (_stamps[index] > Device.GetCompletedFenceValue()) return false;
            _stamps[index] = stamp; _cursors[index] = 0;
        }
        Span<nint> resources = stackalloc nint[2];
        Span<ShaderResourceViewDesc> views = stackalloc ShaderResourceViewDesc[2];
        if (!Resolve(inputs, target, resources, views, true, out var frame, out bool roughness) || frame == null) return false;
        var ring = _slots[index];
        if (_cursors[index] == ring.Count) ring.Add(new BindingSlot());
        var slot = ring[_cursors[index]++];
        slot.Capture(resources, views);
        _inputs = inputs; _destination = target; _frame = frame; _prepared = slot; _writeRoughness = roughness;
        _command = Device.GraphicsCommandList; _stamp = stamp; _frameIndex = Device.FrameIndex;

        // s3 per-decal texture: only look up the dictionary, do not load here. A decal whose name is unregistered or not yet fully uploaded is skipped entirely.
        Array.Clear(_textures); Array.Clear(_skip);
        for (int i = 0; i < inputs.Count; i++)
        {
            string? name = inputs.Draws[i].Texture;
            if (name == null) continue;
            var texture = _resolve(name);
            if (texture == null || !texture.Ready || texture._textureResource == null)
            {
                _skip[i] = true;
                continue;
            }
            _textures[i] = texture;
        }
        return true;
    }

    public override bool Draw()
    {
        var destination = _destination; var inputs = _inputs; var slot = _prepared; var frame = _frame;
        bool roughness = _writeRoughness;
        var textures = _textures; var skip = _skip;
        _destination = null; _prepared = null; _inputs = default; _frame = null; _writeRoughness = false;
        if (_disposed || destination == null || slot == null || frame == null
            || Device.ActivePassId != RenderPassId.SceneDecal
            || !ReferenceEquals(Device.ActiveTarget, destination)
            || Device.GraphicsCommandList != _command || Device.FrameIndex != _frameIndex
            || Device.GetCurrentRetireFenceValue() != _stamp) return false;
        Span<nint> resources = stackalloc nint[2];
        Span<ShaderResourceViewDesc> views = stackalloc ShaderResourceViewDesc[2];
        if (!Resolve(inputs, destination, resources, views, false, out var resolved, out bool roughnessNow)
            || !ReferenceEquals(resolved, frame) || roughnessNow != roughness
            || !resources.SequenceEqual(slot.Resources)) return false;
        for (int i = 0; i < views.Length; i++) if (views[i].Format != slot.Views[i].Format) return false;

        var command = _command;
        var heap = Device.SrvHeapManager.Heap;
        command->SetDescriptorHeaps(1, &heap);
        command->SetGraphicsRootSignature(_signature);
        command->IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
        command->SetGraphicsRootShaderResourceView(FrameRoot, frame.Resource->GetGPUVirtualAddress());
        command->SetGraphicsRootDescriptorTable(DepthRoot, Device.SrvHeapManager.GetGpuHandle(slot.Ids[0]));
        command->SetGraphicsRootDescriptorTable(TextureRoot, Device.SrvHeapManager.GetGpuHandle(slot.Ids[1]));
        // Consecutive decals of the same mode share one PSO switch; the ordering has already been stably sorted by the shared layer according to SortOrder.
        int mode = -1;
        int shape = roughness ? 1 : 0;
        // On the shader side it is uint4 decalParams; the root constants are copied bit-for-bit: you must write the integer bits. If written as float, 1.0f would be read as
        // 0x3F800000, and DecalOffset would go out of bounds beyond the root SRV (root descriptors have no bounds check), hanging the GPU from the second decal onward.
        var parameters = stackalloc uint[4];
        for (int i = 0; i < inputs.Count; i++)
        {
            // A decal whose texture is not ready is skipped entirely: sampling a placeholder texture would render "not yet loaded" as a block of wrong color.
            if (skip[i]) continue;
            var draw = inputs.Draws[i];
            int next = draw.Mode == DecalMode.Additive ? 1 : 0;
            if (next != mode)
            {
                command->SetPipelineState(_pipelines[next, shape]);
                mode = next;
            }
            // Per-decal texture: directly borrow the already-built SRV handle from the dictionary, no need to allocate a descriptor again.
            if (textures[i] != null) command->SetGraphicsRootDescriptorTable(TextureRoot, textures[i]!.GpuDescriptorHandle);
            parameters[0] = (uint)draw.Index;
            command->SetGraphicsRoot32BitConstants(ParamsRoot, 4, parameters, 0);
            command->DrawInstanced(36, 1, 0, 0);
        }
        return true;
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _destination = null; _prepared = null; _inputs = default; _frame = null;
        var pipelines = new nint[4];
        int count = 0;
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
            {
                pipelines[count++] = (nint)_pipelines[i, j];
                _pipelines[i, j] = null;
            }
        nint signature = (nint)_signature;
        _signature = null;
        Array.Clear(_textures); Array.Clear(_skip);
        Device.EnqueueDeferredRelease(Device.GetCurrentRetireFenceValue(), () =>
        {
            foreach (var ring in _slots) foreach (var slot in ring) slot.Dispose();
            foreach (nint pipeline in pipelines)
                if (pipeline != 0) ((ID3D12PipelineState*)pipeline)->Release();
            if (signature != 0) ((ID3D12RootSignature*)signature)->Release();
        });
    }
}
