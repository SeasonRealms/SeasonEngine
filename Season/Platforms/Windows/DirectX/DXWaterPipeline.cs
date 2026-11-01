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
/// P2 水面材质的 D3D12 实现。
///
/// 与 <see cref="DXDecalPipeline"/> 的骨架相同（描述符快照 + Prepare/Draw 时序契约），差别集中在三处：
/// <list type="bullet">
/// <item>几何由 VS 按 <c>SV_VertexID</c> 展开（P3 是 6 顶点的单位平面，P4 起是 tile 内的 subdiv² 网格），
/// 世界矩阵与分块参数都装在帧缓冲/根常量里，所以没有顶点缓冲；<c>SV_InstanceID</c> 就是 tile 索引；</item>
/// <item>本 Pass <b>不挂深度附件</b>：SceneDepth 是纯采样源（<c>AllShaderResource</c>），
/// 遮挡靠着色器里的软件深度测试解决。这与贴花同理，但水面还需要深度来算厚度，所以两者共用同一次转换；</item>
/// <item>多一张 <c>RefractionSource</c>（开画前的场景色快照）：颜色附件与采样源不能是同一资源，
/// 快照由 <see cref="Device.CopyRenderTarget"/> 在 Pass 外拷好。</item>
/// </list>
///
/// 混合是标准 alpha 混合（SrcAlpha / InvSrcAlpha）：着色器输出非预乘色，与原透明几何保持一致。
/// 双面渲染（<c>CullMode.None</c>）：水面从水下也应该看得到，而且俯仰角很大时背面剔除会让水面整块消失。
/// </summary>
internal sealed unsafe class DXWaterPipeline : WaterPipeline
{
    // 根参数序号，与 WaterShaders.Hlsl 的 t0..t8、b0 一一对应。
    // 八个采样槽连成一段：t1..t4 是深度/折射/法线/天空，t5..t8 是 P3 的 Hi-Z 四级金字塔。
    const uint FrameRoot = 0;
    const uint DepthRoot = 1;
    const uint RefractionRoot = 2;
    const uint NormalRoot = 3;
    const uint SkyRoot = 4;
    // P3：Hi-Z 金字塔的四个级别紧跟天空 LUT，t5..t8。
    const uint HzRoot0 = 5;
    const uint ParamsRoot = 9;

    readonly List<BindingSlot>[] _slots = new List<BindingSlot>[(int)Device.frameCount];
    readonly int[] _cursors = new int[(int)Device.frameCount];
    readonly ulong[] _stamps = new ulong[(int)Device.frameCount];
    ID3D12RootSignature* _signature;
    ID3D12PipelineState* _pipeline;
    /// <summary>逐水面的法线纹理解析器：只查后端纹理字典，不加载。</summary>
    readonly Func<string, DXTexture?> _resolve;
    readonly DXTexture?[] _normals = new DXTexture?[WaterMath.MaxWaters];
    /// <summary>本帧的 Hi-Z 四级金字塔；<see cref="HiZEffect.Ready"/> 为假时整组为空，绑白纹理。</summary>
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
    /// 深度与折射快照的描述符快照。引用保活使窗口 Resize 前后的原生地址不会被复用；
    /// 法线贴图与天空 LUT 直接借后端字典里已经建好的 SRV 句柄，不占这里的槽。
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
        // 帧缓冲是根 SRV：每帧只有一个，且顶点与像素阶段都要读，没必要为它单独占描述符。
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

        // 编译产物由 ShaderCompiler 缓存持有一份引用，调用方拿到的这一份用完全必须自己释放。
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
            // 双面：水面从水下看得到，俯仰角很大时也不会因背面剔除整块消失。
            RasterizerState = new() { FillMode = FillMode.Solid, CullMode = CullMode.None, DepthClipEnable = 1 },
            // 没有深度附件，深度比较必须显式关闭；遮挡由着色器里的软件深度测试负责。
            DepthStencilState = new() { DepthEnable = 0, DepthWriteMask = DepthWriteMask.Zero,
                DepthFunc = ComparisonFunc.Always, FrontFace = stencil, BackFace = stencil },
            SampleMask = uint.MaxValue, PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            NumRenderTargets = 1u, SampleDesc = new(1, 0) };
        // 着色器输出非预乘色，所以用标准 alpha 混合；alpha 槽保留目标值，水面不参与后续的 alpha 判定。
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
    /// 核对形状并把资源折成描述符快照。返回 false 表示本帧放弃：水面只支持「颜色是单采样 HDR、
    /// 深度与颜色同尺寸、快照与颜色不同形」这一种形状，其余情况宁可不出图也不出错图。
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
            // 本 Pass 把颜色目标当作混合底图，共享层因此必须交出同一张目标。
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
        // 天空 LUT 是 P0 建的那张 compute 纹理：名字没注册（StaticCube 档）时退回 1×1 白，
        // 着色器侧的 misc.w 门控保证它不会被真正采样。
        _skyView = inputs.SkyView != null && _resolve(inputs.SkyView) is { Ready: true } sky && sky._textureResource != null
            ? sky : null;
        // P3 Hi-Z：逐级解析本帧的金字塔。任何一级缺席就整组退回，与着色器侧的
        // frame[12].x 门控同源（两边都以 HiZEffect.Ready 为准），不会出现半套金字塔。
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

        // 逐水面的法线贴图：只查字典，不加载。名字未注册或还没上传完的水面退回程序化波浪。
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
        // 法线贴图与天空 LUT 直接借字典里已经建好的 SRV 句柄，不再重新分配描述符。
        var white = Device.White;
        command->SetGraphicsRootDescriptorTable(SkyRoot, (skyView ?? white).GpuDescriptorHandle);
        // Hi-Z 是 compute 写入的纹理，dispatch 结束就停在 PixelShaderResource，这里不再补 barrier。
        for (int i = 0; i < hz.Length; i++)
            command->SetGraphicsRootDescriptorTable(HzRoot0 + (uint)i, (hz[i] ?? white).GpuDescriptorHandle);
        // 着色器侧是 uint4 waterParams，根常量按位原样拷贝：必须写整数位。
        // P4 起后三个分量是 tile 网格参数，顶点着色器据此展开分块网格；不分块时全为 (1,1,0)。
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
            // P4：分块实例化。tile 索引走 SV_InstanceID（StartInstanceLocation 保持 0），
            // 槽位仍由根常量携带 —— 只有 WebGPU 需要把两者挤进同一个实例号。
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
