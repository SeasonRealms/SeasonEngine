// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Season.Rendering;
using Season.Rendering.Effects;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace Season.Platforms.Windows.DirectX;

/// <summary>
/// D3D12 implementation of the waterfall water curtain.
///
/// Shares the same skeleton as <see cref="DXWaterPipeline"/> (descriptor snapshot + Prepare/Draw timing contract), with the differences concentrated in three places:
/// <list type="bullet">
/// <item>Geometry is expanded by the VS along the control points in the frame buffer (Catmull-Rom + per-row lateral tangents), with no vertex buffer and no instances;
/// slot, column count and subdivision count go through root constants;</item>
/// <item>No refraction snapshot: the water curtain does not sample scene color, the color target is the blend base map, so each frame only adds one depth SRV;</item>
/// <item>Three textures (Mask/Normal/Flow) are resolved per waterfall. Any waterfall with an author-declared texture that fails to resolve is skipped entirely
/// - binding a white texture and letting the shader sample it treats the G channel as full-value foam, burning the whole waterfall to a sheet of white, harder to diagnose than not drawing at all.</item>
/// </list>
///
/// Blending is standard alpha blending (SrcAlpha / InvSrcAlpha), double-sided rendering (<c>CullMode.None</c>):
/// the water curtain's cross-section is a closed ring around the control-point line, both the front wall and the back wall enter the pixel stage (the back wall is dimmed by the shader according to normal orientation),
/// culling either face would make the side wall disappear entirely at grazing angles.
/// </summary>
internal sealed unsafe class DXWaterfallPipeline : WaterfallPipeline
{
    // Root parameter indices, one-to-one with t0..t4 and b0 in WaterfallShaders.Hlsl.
    const uint FrameRoot = 0;
    const uint DepthRoot = 1;
    const uint MaskRoot = 2;
    const uint NormalRoot = 3;
    const uint FlowRoot = 4;
    const uint ParamsRoot = 5;

    readonly List<BindingSlot>[] _slots = new List<BindingSlot>[(int)Device.frameCount];
    readonly int[] _cursors = new int[(int)Device.frameCount];
    readonly ulong[] _stamps = new ulong[(int)Device.frameCount];
    ID3D12RootSignature* _signature;
    ID3D12PipelineState* _pipeline;
    /// <summary>Per-waterfall texture resolver: only queries the backend texture dictionary, does not load.</summary>
    readonly Func<string, DXTexture?> _resolve;
    readonly DXTexture?[] _masks = new DXTexture?[WaterfallMath.MaxSheets];
    readonly DXTexture?[] _normals = new DXTexture?[WaterfallMath.MaxSheets];
    readonly DXTexture?[] _flows = new DXTexture?[WaterfallMath.MaxSheets];
    readonly bool[] _skip = new bool[WaterfallMath.MaxSheets];
    ID3D12GraphicsCommandList* _command;
    WaterfallInputs _inputs;
    DXRenderTarget? _destination;
    DXStorageBuffer? _frame;
    BindingSlot? _prepared;
    ulong _stamp;
    uint _frameIndex;
    bool _disposed;

    /// <summary>
    /// Depth descriptor snapshot. Reference keep-alive ensures that native addresses before and after a window Resize are not reused;
    /// the three textures directly borrow the SRV handles already created in the dictionary and do not occupy a slot here.
    /// </summary>
    sealed class BindingSlot
    {
        internal readonly int Id = -1;
        internal nint Resource;
        internal ShaderResourceViewDesc View;

        internal BindingSlot()
        {
            try { Id = Device.DescriptorAllocator.Allocate(); }
            catch { Dispose(); throw; }
        }

        internal void ReleaseResources()
        {
            if (Resource == 0) return;
            var view = View;
            Device.D3dDevice->CreateShaderResourceView(null, &view, Device.SrvHeapManager.GetCpuHandle(Id));
            ((ID3D12Resource*)Resource)->Release();
            Resource = 0;
        }

        internal void Capture(nint resource, in ShaderResourceViewDesc view)
        {
            ReleaseResources();
            Resource = resource;
            View = view;
            ((ID3D12Resource*)resource)->AddRef();
            var captured = View;
            Device.D3dDevice->CreateShaderResourceView((ID3D12Resource*)resource, &captured,
                Device.SrvHeapManager.GetCpuHandle(Id));
        }

        internal void Dispose()
        {
            ReleaseResources();
            if (Id >= 0) Device.DescriptorAllocator.Free(Id);
        }
    }

    /// <summary>
    /// <paramref name="ribbon"/> switches the shader source only: a river ribbon is a flat horizontal band whose current runs
    /// in meters per second, a waterfall curtain is a closed section ring that accelerates from lip to landing point. Everything
    /// around the shader - root signature, descriptor ring, blending, software depth test, pass identity - is identical, so this is
    /// a variant rather than a second class. The frame buffer layouts are slot-for-slot equal (see <see cref="WaterRibbonMath"/>).
    /// </summary>
    internal DXWaterfallPipeline(Func<string, DXTexture?> resolve, bool ribbon = false)
    {
        _resolve = resolve;
        _ribbon = ribbon;
        for (int i = 0; i < _slots.Length; i++) _slots[i] = new();
        try { Initialize(); }
        catch { Dispose(); throw; }
    }

    /// <summary>Whether this instance draws river ribbons instead of waterfall curtains.</summary>
    readonly bool _ribbon;

    void Initialize()
    {
        var parameters = stackalloc RootParameter[6];
        // The frame buffer is a root SRV: there is only one per frame, and both the vertex and pixel stages need to read it, so there is no need to occupy a descriptor for it separately.
        parameters[FrameRoot] = new() { ParameterType = RootParameterType.TypeSrv,
            Descriptor = new RootDescriptor { ShaderRegister = 0, RegisterSpace = 0 },
            ShaderVisibility = ShaderVisibility.All };
        var ranges = stackalloc DescriptorRange[4];
        for (int i = 0; i < 4; i++)
        {
            ranges[i] = new() { RangeType = DescriptorRangeType.Srv, NumDescriptors = 1, BaseShaderRegister = (uint)(i + 1) };
            parameters[DepthRoot + i] = new() { ParameterType = RootParameterType.TypeDescriptorTable,
                DescriptorTable = new() { NumDescriptorRanges = 1, PDescriptorRanges = ranges + i },
                ShaderVisibility = ShaderVisibility.Pixel };
        }
        parameters[ParamsRoot] = new() { ParameterType = RootParameterType.Type32BitConstants,
            Constants = new() { ShaderRegister = 0, Num32BitValues = 4 }, ShaderVisibility = ShaderVisibility.All };

        // s0 clamps (R/G are mapped only once), s1 repeats (B/A, normal and Flow wrap).
        // Both static samplers are visible only to the pixel stage: the vertex stage does not sample any texture.
        // ShaderRegister must be written explicitly: StaticSamplerDesc's default value is 0, and omitting both would land both on s0.
        var samplers = stackalloc StaticSamplerDesc[2];
        for (int i = 0; i < 2; i++)
            samplers[i] = new StaticSamplerDesc { Filter = Filter.MinMagMipLinear,
                AddressU = i == 0 ? TextureAddressMode.Clamp : TextureAddressMode.Wrap,
                AddressV = i == 0 ? TextureAddressMode.Clamp : TextureAddressMode.Wrap,
                AddressW = i == 0 ? TextureAddressMode.Clamp : TextureAddressMode.Wrap,
                MaxLOD = float.MaxValue, ComparisonFunc = ComparisonFunc.Always,
                ShaderRegister = (uint)i, RegisterSpace = 0, ShaderVisibility = ShaderVisibility.Pixel };
        var root = new RootSignatureDesc { NumParameters = 6, PParameters = parameters,
            NumStaticSamplers = 2, PStaticSamplers = samplers };
        using ComPtr<ID3D10Blob> blob = null;
        using ComPtr<ID3D10Blob> error = null;
        Device.CheckResult(Device.D3D12.SerializeRootSignature(&root, D3DRootSignatureVersion.Version1,
            blob.GetAddressOf(), error.GetAddressOf()));
        ID3D12RootSignature* signature = null;
        var iid = ID3D12RootSignature.Guid;
        Device.CheckResult(Device.D3dDevice->CreateRootSignature(0, blob.Get().GetBufferPointer(),
            blob.Get().GetBufferSize(), &iid, (void**)&signature));
        _signature = signature;

        // The compiled artifact is kept alive by a reference held by the ShaderCompiler cache; the copy the caller receives must be released by the caller once used.
        // The two variants keep the same entry point names on purpose: every Draw path below is shared, and the root signature
        // and descriptor layout are the same, so only these two strings decide which body runs.
        string common = _ribbon ? WaterRibbonShaders.FrameHlsl : WaterfallShaders.FrameHlsl;
        string vertexSource = _ribbon ? WaterRibbonShaders.VertexHlsl : WaterfallShaders.VertexHlsl;
        string pixelSource = _ribbon ? WaterRibbonShaders.PixelHlsl : WaterfallShaders.PixelHlsl;
        ID3D10Blob* vertex = ShaderCompiler.CompileShaderFromSource(common + vertexSource, "VSMain", "vs_5_0");
        try
        {
            ID3D10Blob* fragment = ShaderCompiler.CompileShaderFromSource(common + pixelSource, "PSMain", "ps_5_0");
            try { _pipeline = CreatePipeline(vertex, fragment); }
            finally { fragment->Release(); }
        }
        finally { vertex->Release(); }
    }

    ID3D12PipelineState* CreatePipeline(ID3D10Blob* vertex, ID3D10Blob* fragment)
    {
        var stencil = new DepthStencilopDesc { StencilFailOp = StencilOp.Keep, StencilDepthFailOp = StencilOp.Keep,
            StencilPassOp = StencilOp.Keep, StencilFunc = ComparisonFunc.Always };
        var desc = new GraphicsPipelineStateDesc {
            PRootSignature = _signature,
            VS = new(vertex->GetBufferPointer(), vertex->GetBufferSize()),
            PS = new(fragment->GetBufferPointer(), fragment->GetBufferSize()),
            // Double-sided: the cross-section is a closed ring, both inner and outer faces must be drawn - the inner side is the half that gives the water body thickness.
            RasterizerState = new() { FillMode = FillMode.Solid, CullMode = CullMode.None, DepthClipEnable = 1 },
            // There is no depth attachment, so depth comparison must be explicitly disabled; occlusion is handled by the software depth test in the shader.
            DepthStencilState = new() { DepthEnable = 0, DepthWriteMask = DepthWriteMask.Zero,
                DepthFunc = ComparisonFunc.Always, FrontFace = stencil, BackFace = stencil },
            SampleMask = uint.MaxValue, PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            NumRenderTargets = 1u, SampleDesc = new(1, 0) };
        // The shader outputs non-premultiplied color, so standard alpha blending is used; the alpha slot keeps the destination value, and the water curtain does not participate in later alpha decisions.
        desc.BlendState.RenderTarget[0] = new() { BlendEnable = 1, SrcBlend = Blend.SrcAlpha,
            DestBlend = Blend.InvSrcAlpha, BlendOp = BlendOp.Add,
            SrcBlendAlpha = Blend.Zero, DestBlendAlpha = Blend.One, BlendOpAlpha = BlendOp.Add,
            LogicOp = LogicOp.Noop, RenderTargetWriteMask = (byte)ColorWriteEnable.All };
        desc.RTVFormats[0] = Format.FormatR16G16B16A16Float;
        ID3D12PipelineState* pipeline = null;
        var iid = ID3D12PipelineState.Guid;
        Device.CheckResult(Device.D3dDevice->CreateGraphicsPipelineState(&desc, &iid, (void**)&pipeline));
        return pipeline;
    }

    static ShaderResourceViewDesc TextureView(Format format) => new() {
        Format = format, Shader4ComponentMapping = 0x1688, ViewDimension = SrvDimension.Texture2D,
        Texture2D = new() { MipLevels = 1 } };

    /// <summary>
    /// Validates the shape and transitions resource state. Returning false means this frame is abandoned: waterfalls only support the single shape of "color is single-sample HDR
    /// and depth matches color in size"; in all other cases it prefers not to produce an image rather than produce a wrong one.
    /// </summary>
    bool Resolve(in WaterfallInputs inputs, DXRenderTarget destination, Span<nint> resources,
        Span<ShaderResourceViewDesc> views, bool transition)
    {
        if (inputs.Color is not DXRenderTarget color || inputs.Depth is not DXRenderTarget depth
            || inputs.FrameBuffer is not DXStorageBuffer buffer || buffer.Resource == null
            || buffer.SizeInBytes < WaterfallMath.ByteCount
            || color.Desc.ColorFormat != RtFormat.Rgba16Float || destination.Desc.ColorFormat != RtFormat.Rgba16Float
            || color.Desc.SampleCount != 1 || depth.Desc.SampleCount != 1 || destination.Desc.SampleCount != 1
            || color.Color == null || depth.Depth == null || destination.Color == null
            || color.ColorNativeFormat != Format.FormatR16G16B16A16Float
            || destination.ColorNativeFormat != Format.FormatR16G16B16A16Float
            || depth.Desc.DepthFormat != RtFormat.D32Float
            // This Pass treats the color target as the blend base map, so the shared layer must hand over the very same target.
            || !ReferenceEquals(destination, color)
            || color.Width == 0 || color.Height == 0 || color.Width != depth.Width || color.Height != depth.Height
            || inputs.Count <= 0 || inputs.Count > WaterfallMath.MaxSheets) return false;
        resources[0] = (nint)depth.Depth;
        views[0] = TextureView(Format.FormatR32Float);
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
                || destination.CurrentState != ResourceStates.RenderTarget) return false;
        }
        return true;
    }

    /// <summary>Resolves one per-waterfall texture. A null name means "do not sample", returning true and clearing the slot.</summary>
    bool ResolveTexture(string? name, DXTexture?[] target, int index)
    {
        target[index] = null;
        if (name == null) return true;
        var texture = _resolve(name);
        if (texture == null || !texture.Ready || texture._textureResource == null) return false;
        target[index] = texture;
        return true;
    }

    public override bool Prepare(in WaterfallInputs inputs, RenderTarget destination)
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
        Span<nint> resources = stackalloc nint[1];
        Span<ShaderResourceViewDesc> views = stackalloc ShaderResourceViewDesc[1];
        if (!Resolve(inputs, target, resources, views, true)) return false;
        if (inputs.FrameBuffer is not DXStorageBuffer frame) return false;
        var ring = _slots[index];
        if (_cursors[index] == ring.Count) ring.Add(new BindingSlot());
        var slot = ring[_cursors[index]++];
        slot.Capture(resources[0], views[0]);
        _inputs = inputs; _destination = target; _frame = frame; _prepared = slot;
        _command = Device.GraphicsCommandList; _stamp = stamp; _frameIndex = Device.FrameIndex;

        Array.Clear(_masks); Array.Clear(_normals); Array.Clear(_flows); Array.Clear(_skip);
        for (int i = 0; i < inputs.Count; i++)
        {
            var draw = inputs.Draws[i];
            // If any of the three tables is missing the whole strip is skipped: binding a white texture and letting the shader sample it treats foam/streaks as full value,
            // so the picture is "a whitish water curtain" rather than "a missing texture", harder to diagnose than simply not drawing.
            _skip[i] = !ResolveTexture(draw.Mask, _masks, i)
                || !ResolveTexture(draw.Normal, _normals, i)
                || !ResolveTexture(draw.Flow, _flows, i);
        }
        return true;
    }

    public override bool Draw()
    {
        var destination = _destination; var inputs = _inputs; var slot = _prepared; var frame = _frame;
        var masks = _masks; var normals = _normals; var flows = _flows; var skip = _skip;
        _destination = null; _prepared = null; _inputs = default; _frame = null;
        if (_disposed || destination == null || slot == null || frame == null
            || Device.ActivePassId != RenderPassId.SceneWaterfall
            || !ReferenceEquals(Device.ActiveTarget, destination)
            || Device.GraphicsCommandList != _command || Device.FrameIndex != _frameIndex
            || Device.GetCurrentRetireFenceValue() != _stamp) return false;
        Span<nint> resources = stackalloc nint[1];
        Span<ShaderResourceViewDesc> views = stackalloc ShaderResourceViewDesc[1];
        if (!Resolve(inputs, destination, resources, views, false)
            || resources[0] != slot.Resource || views[0].Format != slot.View.Format) return false;

        var command = _command;
        var heap = Device.SrvHeapManager.Heap;
        command->SetDescriptorHeaps(1, &heap);
        command->SetGraphicsRootSignature(_signature);
        command->SetPipelineState(_pipeline);
        command->IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
        command->SetGraphicsRootShaderResourceView(FrameRoot, frame.Resource->GetGPUVirtualAddress());
        command->SetGraphicsRootDescriptorTable(DepthRoot, Device.SrvHeapManager.GetGpuHandle(slot.Id));
        // On the shader side this is a uint4 waterfallParams, and root constants are copied bit-for-bit: the integer bits must be written.
        // The last three components are mesh parameters, from which the vertex shader expands the subdivision rows and lateral columns beyond the control points.
        var white = Device.White;
        var parameters = stackalloc uint[4];
        int drawn = 0;
        for (int i = 0; i < inputs.Count; i++)
        {
            if (skip[i]) continue;
            var draw = inputs.Draws[i];
            if (draw.VertexCount <= 0) continue;
            parameters[0] = (uint)draw.Index;
            parameters[1] = (uint)draw.Columns;
            parameters[2] = (uint)draw.Subdiv;
            parameters[3] = 0u;
            command->SetGraphicsRootDescriptorTable(MaskRoot, (masks[i] ?? white).GpuDescriptorHandle);
            command->SetGraphicsRootDescriptorTable(NormalRoot, (normals[i] ?? white).GpuDescriptorHandle);
            command->SetGraphicsRootDescriptorTable(FlowRoot, (flows[i] ?? white).GpuDescriptorHandle);
            command->SetGraphicsRoot32BitConstants(ParamsRoot, 4, parameters, 0);
            command->DrawInstanced((uint)draw.VertexCount, 1, 0, 0);
            drawn++;
        }
        // Returning false when every waterfall was skipped: the same semantics as drawn > 0 on VK/Metal,
        // by which the caller marks this frame as "nothing was drawn" rather than "drew 0 but succeeded".
        return drawn > 0;
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _destination = null; _prepared = null; _inputs = default; _frame = null;
        nint pipeline = (nint)_pipeline;
        nint signature = (nint)_signature;
        _pipeline = null;
        _signature = null;
        Array.Clear(_masks);
        Array.Clear(_normals);
        Array.Clear(_flows);
        Device.EnqueueDeferredRelease(Device.GetCurrentRetireFenceValue(), () =>
        {
            foreach (var ring in _slots) foreach (var slot in ring) slot.Dispose();
            if (pipeline != 0) ((ID3D12PipelineState*)pipeline)->Release();
            if (signature != 0) ((ID3D12RootSignature*)signature)->Release();
        });
    }
}
