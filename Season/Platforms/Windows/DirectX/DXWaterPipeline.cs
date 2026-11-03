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
/// D3D12 implementation of the P2 water surface material.
///
/// The skeleton is the same as <see cref="DXDecalPipeline"/> (descriptor snapshot + Prepare/Draw timing contract); the differences are concentrated in three places:
/// <list type="bullet">
/// <item>The geometry is expanded by the VS from <c>SV_VertexID</c> (P3 is a 6-vertex unit plane, P4 onward is a subdiv² mesh inside a tile),
/// the world matrix and tiling parameters are all packed in the frame buffer/root constants, so there is no vertex buffer; <c>SV_InstanceID</c> is the tile index;</item>
/// <item>This Pass does <b>not attach a depth attachment</b>: SceneDepth is a pure sampling source (<c>AllShaderResource</c>),
/// occlusion is solved by the software depth test in the shader. This is the same reasoning as for the decal, but the water surface also needs the depth to compute thickness, so the two share the same transition;</item>
/// <item>There is an extra <c>RefractionSource</c> (a snapshot of the scene color before drawing): the color attachment and the sampling source cannot be the same resource,
/// the snapshot is copied outside the Pass by <see cref="Device.CopyRenderTarget"/>.</item>
/// </list>
///
/// Blending is standard alpha blending (SrcAlpha / InvSrcAlpha): the shader outputs non-premultiplied color, consistent with the original transparent geometry.
/// Double-sided rendering (<c>CullMode.None</c>): the water surface should be visible from underwater too, and at a very large pitch angle back-face culling would make the water surface disappear entirely.
/// </summary>
internal sealed unsafe class DXWaterPipeline : WaterPipeline
{
    // Root parameter indices, corresponding one-to-one with t0..t8 and b0 in WaterShaders.Hlsl.
    // The eight sampling slots form one run: t1..t4 are depth/refraction/normal/sky, t5..t8 are the four-level Hi-Z pyramid of P3.
    const uint FrameRoot = 0;
    const uint DepthRoot = 1;
    const uint RefractionRoot = 2;
    const uint NormalRoot = 3;
    const uint SkyRoot = 4;
    // P3: the four levels of the Hi-Z pyramid immediately follow the sky LUT, t5..t8.
    const uint HzRoot0 = 5;
    const uint ParamsRoot = 9;

    readonly List<BindingSlot>[] _slots = new List<BindingSlot>[(int)Device.frameCount];
    readonly int[] _cursors = new int[(int)Device.frameCount];
    readonly ulong[] _stamps = new ulong[(int)Device.frameCount];
    ID3D12RootSignature* _signature;
    ID3D12PipelineState* _pipeline;
    /// <summary>Per-water-surface normal texture resolver: it only looks up the backend texture dictionary, it does not load.</summary>
    readonly Func<string, DXTexture?> _resolve;
    readonly DXTexture?[] _normals = new DXTexture?[WaterMath.MaxWaters];
    /// <summary>This frame's four-level Hi-Z pyramid; when <see cref="HiZEffect.Ready"/> is false the whole set is null and a white texture is bound.</summary>
    readonly DXTexture?[] _hz = new DXTexture?[HiZEffect.LevelCount];
    ID3D12GraphicsCommandList* _command;
    WaterInputs _inputs;
    DXRenderTarget? _destination;
    DXStorageBuffer? _frame;
    DXTexture? _skyView;
    BindingSlot? _prepared;
    ulong _stamp;
    uint _frameIndex;
    bool _disposed;

    /// <summary>
    /// Descriptor snapshot for depth and refraction. Keeping the references alive prevents the native address from being reused across a window Resize;
    /// the normal map and the sky LUT directly borrow the already-built SRV handles from the backend dictionary and do not occupy a slot here.
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

    internal DXWaterPipeline(Func<string, DXTexture?> resolve)
    {
        _resolve = resolve;
        for (int i = 0; i < _slots.Length; i++) _slots[i] = new();
        try { Initialize(); }
        catch { Dispose(); throw; }
    }

    void Initialize()
    {
        var parameters = stackalloc RootParameter[10];
        // The frame buffer is a root SRV: there is only one per frame, and both the vertex and pixel stages need to read it, so there is no need to occupy a descriptor for it separately.
        parameters[FrameRoot] = new() { ParameterType = RootParameterType.TypeSrv,
            Descriptor = new RootDescriptor { ShaderRegister = 0, RegisterSpace = 0 },
            ShaderVisibility = ShaderVisibility.All };
        var ranges = stackalloc DescriptorRange[8];
        for (int i = 0; i < 8; i++)
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
        var root = new RootSignatureDesc { NumParameters = 10, PParameters = parameters,
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

        // The compile result is kept as one reference by the ShaderCompiler cache; the copy the caller gets must be released by itself once done.
        string common = WaterShaders.FrameHlsl;
        ID3D10Blob* vertex = ShaderCompiler.CompileShaderFromSource(common + WaterShaders.VertexHlsl, "VSMain", "vs_5_0");
        try
        {
            ID3D10Blob* fragment = ShaderCompiler.CompileShaderFromSource(common + WaterShaders.PixelHlsl, "PSMain", "ps_5_0");
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
            // Double-sided: the water surface is visible from underwater, and at a very large pitch angle it will not disappear entirely due to back-face culling.
            RasterizerState = new() { FillMode = FillMode.Solid, CullMode = CullMode.None, DepthClipEnable = 1 },
            // There is no depth attachment, so the depth comparison must be explicitly disabled; occlusion is handled by the software depth test in the shader.
            DepthStencilState = new() { DepthEnable = 0, DepthWriteMask = DepthWriteMask.Zero,
                DepthFunc = ComparisonFunc.Always, FrontFace = stencil, BackFace = stencil },
            SampleMask = uint.MaxValue, PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            NumRenderTargets = 1u, SampleDesc = new(1, 0) };
        // The shader outputs non-premultiplied color, so standard alpha blending is used; the alpha slot keeps the destination value, and the water surface does not participate in the subsequent alpha test.
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
    /// Verify the shape and fold the resources into a descriptor snapshot. Returning false means giving up this frame: the water surface only supports the one shape
    /// "color is single-sample HDR, depth and color are the same size, snapshot and color are not the same shape"; in other cases it will produce no image rather than a wrong image.
    /// </summary>
    bool Resolve(in WaterInputs inputs, DXRenderTarget destination, Span<nint> resources,
        Span<ShaderResourceViewDesc> views, bool transition)
    {
        if (inputs.Color is not DXRenderTarget color || inputs.Depth is not DXRenderTarget depth
            || inputs.RefractionSource is not DXRenderTarget refraction || inputs.FrameBuffer is not DXStorageBuffer buffer
            || buffer.Resource == null || buffer.SizeInBytes < WaterMath.ByteCount
            || color.Desc.ColorFormat != RtFormat.Rgba16Float || destination.Desc.ColorFormat != RtFormat.Rgba16Float
            || color.Desc.SampleCount != 1 || depth.Desc.SampleCount != 1 || destination.Desc.SampleCount != 1
            || color.Color == null || depth.Depth == null || refraction.Color == null || destination.Color == null
            || color.ColorNativeFormat != Format.FormatR16G16B16A16Float
            || destination.ColorNativeFormat != Format.FormatR16G16B16A16Float
            || refraction.ColorNativeFormat != Format.FormatR16G16B16A16Float
            || depth.Desc.DepthFormat != RtFormat.D32Float
            || ReferenceEquals(refraction, color)
            // This Pass uses the color target as the blend base image, so the shared layer must hand out the same target.
            || !ReferenceEquals(destination, color)
            || color.Width == 0 || color.Height == 0 || color.Width != depth.Width || color.Height != depth.Height
            || color.Width != refraction.Width || color.Height != refraction.Height
            || inputs.Count <= 0 || inputs.Count > WaterMath.MaxWaters) return false;
        resources[0] = (nint)depth.Depth;
        resources[1] = (nint)refraction.Color;
        views[0] = TextureView(Format.FormatR32Float);
        views[1] = TextureView(Format.FormatR16G16B16A16Float);
        if (transition)
        {
            var command = Device.GraphicsCommandList;
            buffer.TransitionTo(command, ResourceStates.AllShaderResource);
            depth.TransitionDepthTo(command, ResourceStates.AllShaderResource);
            refraction.TransitionTo(command, ResourceStates.PixelShaderResource);
        }
        else
        {
            const ResourceStates read = ResourceStates.PixelShaderResource;
            if ((buffer.CurrentState & read) == 0 || (depth.DepthCurrentState & read) == 0
                || (refraction.CurrentState & read) == 0
                || destination.CurrentState != ResourceStates.RenderTarget) return false;
        }
        return true;
    }

    public override bool Prepare(in WaterInputs inputs, RenderTarget destination)
    {
        _destination = null; _prepared = null; _inputs = default; _frame = null; _skyView = null;
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
        if (!Resolve(inputs, target, resources, views, true)) return false;
        if (inputs.FrameBuffer is not DXStorageBuffer frame) return false;
        var ring = _slots[index];
        if (_cursors[index] == ring.Count) ring.Add(new BindingSlot());
        var slot = ring[_cursors[index]++];
        slot.Capture(resources, views);
        // The sky LUT is the compute texture created by P0: when the name is unregistered (the StaticCube tier) it falls back to 1x1 white,
        // and the misc.w gate on the shader side guarantees it is never actually sampled.
        _skyView = inputs.SkyView != null && _resolve(inputs.SkyView) is { Ready: true } sky && sky._textureResource != null
            ? sky : null;
        // P3 Hi-Z: resolve this frame's pyramid level by level. If any level is missing the whole set falls back, sharing the same source as the
        // frame[12].x gate on the shader side (both are governed by HiZEffect.Ready), so a half set of the pyramid cannot occur.
        Array.Clear(_hz);
        if (HiZEffect.Ready)
        {
            for (int level = 0; level < _hz.Length; level++)
            {
                var texture = _resolve(HiZEffect.LevelName(level));
                if (texture == null || !texture.Ready || texture._textureResource == null)
                {
                    Array.Clear(_hz);
                    break;
                }
                _hz[level] = texture;
            }
        }
        _inputs = inputs; _destination = target; _frame = frame; _prepared = slot;
        _command = Device.GraphicsCommandList; _stamp = stamp; _frameIndex = Device.FrameIndex;

        // Per-water-surface normal map: only look up the dictionary, do not load. A water surface whose name is unregistered or not yet fully uploaded falls back to procedural waves.
        Array.Clear(_normals);
        for (int i = 0; i < inputs.Count; i++)
        {
            string? name = inputs.Draws[i].Texture;
            if (name == null) continue;
            var texture = _resolve(name);
            if (texture == null || !texture.Ready || texture._textureResource == null) continue;
            _normals[i] = texture;
        }
        return true;
    }

    public override bool Draw()
    {
        var destination = _destination; var inputs = _inputs; var slot = _prepared; var frame = _frame;
        var normals = _normals; var skyView = _skyView; var hz = _hz;
        _destination = null; _prepared = null; _inputs = default; _frame = null; _skyView = null;
        if (_disposed || destination == null || slot == null || frame == null
            || Device.ActivePassId != RenderPassId.SceneWater
            || !ReferenceEquals(Device.ActiveTarget, destination)
            || Device.GraphicsCommandList != _command || Device.FrameIndex != _frameIndex
            || Device.GetCurrentRetireFenceValue() != _stamp) return false;
        Span<nint> resources = stackalloc nint[2];
        Span<ShaderResourceViewDesc> views = stackalloc ShaderResourceViewDesc[2];
        if (!Resolve(inputs, destination, resources, views, false)
            || !resources.SequenceEqual(slot.Resources)) return false;
        for (int i = 0; i < views.Length; i++) if (views[i].Format != slot.Views[i].Format) return false;

        var command = _command;
        var heap = Device.SrvHeapManager.Heap;
        command->SetDescriptorHeaps(1, &heap);
        command->SetGraphicsRootSignature(_signature);
        command->SetPipelineState(_pipeline);
        command->IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
        command->SetGraphicsRootShaderResourceView(FrameRoot, frame.Resource->GetGPUVirtualAddress());
        command->SetGraphicsRootDescriptorTable(DepthRoot, Device.SrvHeapManager.GetGpuHandle(slot.Ids[0]));
        command->SetGraphicsRootDescriptorTable(RefractionRoot, Device.SrvHeapManager.GetGpuHandle(slot.Ids[1]));
        // The normal map and the sky LUT directly borrow the already-built SRV handles from the dictionary, no need to allocate a descriptor again.
        var white = Device.White;
        command->SetGraphicsRootDescriptorTable(SkyRoot, (skyView ?? white).GpuDescriptorHandle);
        // Hi-Z is a texture written by compute; when the dispatch ends it stays in PixelShaderResource, so no barrier is added here.
        for (int i = 0; i < hz.Length; i++)
            command->SetGraphicsRootDescriptorTable(HzRoot0 + (uint)i, (hz[i] ?? white).GpuDescriptorHandle);
        // On the shader side it is uint4 waterParams; the root constants are copied bit-for-bit: you must write the integer bits.
        // From P4 onward the last three components are the tile mesh parameters, and the vertex shader expands the tiled mesh from them; when not tiling they are all (1,1,0).
        var parameters = stackalloc uint[4];
        for (int i = 0; i < inputs.Count; i++)
        {
            var draw = inputs.Draws[i];
            parameters[0] = (uint)draw.Index;
            parameters[1] = (uint)draw.TilesX;
            parameters[2] = (uint)draw.TilesZ;
            parameters[3] = (uint)draw.Subdiv;
            command->SetGraphicsRootDescriptorTable(NormalRoot, (normals[i] ?? white).GpuDescriptorHandle);
            command->SetGraphicsRoot32BitConstants(ParamsRoot, 4, parameters, 0);
            // P4: tiled instancing. The tile index goes through SV_InstanceID (StartInstanceLocation stays 0),
            // and the slot is still carried by the root constants - only WebGPU needs to squeeze the two into the same instance number.
            command->DrawInstanced((uint)draw.VertexCount, (uint)draw.InstanceCount, 0, 0);
        }
        return true;
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _destination = null; _prepared = null; _inputs = default; _frame = null; _skyView = null;
        nint pipeline = (nint)_pipeline;
        nint signature = (nint)_signature;
        _pipeline = null;
        _signature = null;
        Array.Clear(_normals);
        Array.Clear(_hz);
        Device.EnqueueDeferredRelease(Device.GetCurrentRetireFenceValue(), () =>
        {
            foreach (var ring in _slots) foreach (var slot in ring) slot.Dispose();
            if (pipeline != 0) ((ID3D12PipelineState*)pipeline)->Release();
            if (signature != 0) ((ID3D12RootSignature*)signature)->Release();
        });
    }
}
