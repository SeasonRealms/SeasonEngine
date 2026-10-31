// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Season.Rendering;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace Season.Platforms.Windows.DirectX;

/// <summary>
/// D1 屏幕空间贴花的 D3D12 实现。
///
/// 每个贴花一次 36 顶点的 DrawInstanced：顶点由 SV_VertexID 算出投影盒的六个面，光栅器只留背面
/// （<see cref="CullMode.Front"/>），因此每个像素最多命中一次 —— 混合不会被叠加两遍，也就不需要模板。
/// 像素着色器用本帧深度重建世界坐标，折进盒的局部空间后取 UV；颜色通过固定功能混合写回 SceneColor，
/// 所以 SceneColor 不需要同时是可采样资源，Pass 里它只是颜色附件。
///
/// 时序与 <see cref="DXFogCompositePipeline"/> 相同：Prepare 在 Pass 外完成深度/法线的状态转换与
/// 描述符快照，Draw 只能在 <see cref="RenderPassId.SceneDecal"/> 内消费一次；任何一项与准备期不符
/// 都放弃绘制而不是部分绘制。
/// </summary>
internal sealed unsafe class DXDecalPipeline : DecalPipeline
{
    // 根参数序号，与 DecalShaders.Hlsl 的 t0/t1/t2、b0 一一对应。
    const uint FrameRoot = 0;
    const uint DepthRoot = 1;
    const uint TextureRoot = 2;
    const uint ParamsRoot = 3;

    readonly List<BindingSlot>[] _slots = new List<BindingSlot>[(int)Device.frameCount];
    readonly int[] _cursors = new int[(int)Device.frameCount];
    readonly ulong[] _stamps = new ulong[(int)Device.frameCount];
    ID3D12RootSignature* _signature;
    // [模式 0=Modulate/1=Additive, 是否带法线附件]。s9 的粗糙度写入要第二个颜色附件，
    // 而附件数量必须与 PSO 的 RT 数量一致，所以这里按两种附件形状各留一份。
    readonly ID3D12PipelineState*[,] _pipelines = new ID3D12PipelineState*[2, 2];
    /// <summary>逐贴花的纹理解析器：只查后端纹理字典，不加载。名字 → 纹理为空时该 draw 被跳过。</summary>
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
    /// 深度与法线的描述符快照。引用保活使窗口 Resize 前后的原生地址不会被复用，
    /// 描述符也不能从共享可见堆里原地借用 —— 与雾合成完全相同的原因。
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
        // 帧缓冲是根 SRV：每帧只有一个，且顶点与像素阶段都要读，没必要为它单独占描述符。
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
    /// 两种混合语义共用一套着色器，只是源因子不同：
    /// Modulate 用 <c>dst * src</c>（SrcBlend = DestColor, DestBlend = Zero）把像素输出当成调制量，
    /// Additive 用 <c>dst + src</c>。alpha 一律保留目标值，贴花不参与后续的 alpha 判定。
    ///
    /// <paramref name="roughness"/> 为 true 时挂第二个颜色附件（SceneNormal）。它只开 B 通道写掩码，
    /// 并用 coverage 在原值上插值，所以八面体法线与金属度不被碰。
    /// </summary>
    ID3D12PipelineState* CreatePipeline(ID3D10Blob* vertex, ID3D10Blob* fragment, Blend source, bool roughness)
    {
        var stencil = new DepthStencilopDesc { StencilFailOp = StencilOp.Keep, StencilDepthFailOp = StencilOp.Keep,
            StencilPassOp = StencilOp.Keep, StencilFunc = ComparisonFunc.Always };
        var desc = new GraphicsPipelineStateDesc {
            PRootSignature = _signature,
            VS = new(vertex->GetBufferPointer(), vertex->GetBufferSize()),
            PS = new(fragment->GetBufferPointer(), fragment->GetBufferSize()),
            // 只留背面：投影盒是凸体，背面在每个像素上恰好命中一次。
            RasterizerState = new() { FillMode = FillMode.Solid, CullMode = CullMode.Front, DepthClipEnable = 1 },
            DepthStencilState = new() { DepthFunc = ComparisonFunc.Always, FrontFace = stencil, BackFace = stencil },
            SampleMask = uint.MaxValue, PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            NumRenderTargets = roughness ? 2u : 1u, SampleDesc = new(1, 0) };
        desc.BlendState.RenderTarget[0] = new() { SrcBlend = source,
            DestBlend = source == Blend.One ? Blend.One : Blend.Zero, BlendOp = BlendOp.Add,
            SrcBlendAlpha = Blend.Zero, DestBlendAlpha = Blend.One, BlendOpAlpha = BlendOp.Add,
            LogicOp = LogicOp.Noop, RenderTargetWriteMask = (byte)ColorWriteEnable.All };
        desc.RTVFormats[0] = Format.FormatR16G16B16A16Float;
        if (roughness)
        {
            // 只写 .z（Blue）：法线的八面体编码在 .xy，金属度在 .w，两者必须保留。
            // 混合用 coverage 做 lerp，coverage 为 0 时结果恰好等于原值，所以“不碰粗糙度”的贴花不会改变目标。
            desc.BlendState.RenderTarget[1] = new() { SrcBlend = Blend.SrcAlpha,
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
    /// 核对形状并把资源折成描述符快照。返回 false 表示本帧放弃：贴花只在「颜色是单采样 HDR、
    /// 深度与颜色同尺寸」这一种形状下成立，其余情况宁可不出图也不出错图。
    ///
    /// 法线目标在 s9 之后是<b>附件</b>而不是采样源：同一个资源不能同时挂 RTV 与 SRV。
    /// 所以两个描述符槽都指向深度，第二个槽留给 s3 的逐贴花纹理。
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
            // 本 Pass 把颜色目标同时当作混合底图，共享层因此必须交出同一张目标；换成别的 RT 就不是「贴到场景上」了。
            || !ReferenceEquals(destination, color)
            || color.Width == 0 || color.Height == 0 || color.Width != depth.Width || color.Height != depth.Height
            || inputs.Count <= 0 || inputs.Count > DecalMath.MaxDecals) return false;
        // 粗糙度写入要求法线目标与颜色同形；不满足时只是不写粗糙度，不应该让颜色贴花一起消失。
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
                // 附件由 BeginPass 挂好，这里只核对它确实还在渲染目标状态。
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

        // s3 逐贴花纹理：只查字典，不在这里加载。名字未注册或还没上传完的贴花整个跳过。
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
        // 同一模式的连续贴花共用一次 PSO 切换；排序已由共享层按 SortOrder 稳定排好。
        int mode = -1;
        int shape = roughness ? 1 : 0;
        for (int i = 0; i < inputs.Count; i++)
        {
            // 贴图没就绪的贴花整个跳过：采样一个占位纹理会把“还没加载完”画成一块错误颜色。
            if (skip[i]) continue;
            var draw = inputs.Draws[i];
            int next = draw.Mode == DecalMode.Additive ? 1 : 0;
            if (next != mode)
            {
                command->SetPipelineState(_pipelines[next, shape]);
                mode = next;
            }
            // 逐贴花纹理：直接借字典里已经建好的 SRV 句柄，不再重新分配描述符。
            if (textures[i] != null) command->SetGraphicsRootDescriptorTable(TextureRoot, textures[i]!.GpuDescriptorHandle);
            var parameters = new Vector4(draw.Index, 0, 0, 0);
            command->SetGraphicsRoot32BitConstants(ParamsRoot, 4, &parameters, 0);
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
