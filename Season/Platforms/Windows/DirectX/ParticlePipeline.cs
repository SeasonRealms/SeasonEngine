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
/// A0.1 的 D3D12 提交端：一次 ExecuteIndirect 画出 compute 决定数量的公告板。
///
/// 和 DXFogCompositePipeline 比，这里刻意少了一整套机制——没有描述符堆、没有按帧绑定环、
/// 没有原生资源 AddRef 保活。原因是粒子只消费缓冲不消费纹理，而 root SRV 可以直接吃 GPU 虚拟地址，
/// 所以根本不存在"描述符可能被在飞帧复用"这个问题。
///
/// PSO 不在构造期烘焙而是在 Draw 里按需烘焙：决定 OM 形状的三件事（采样数、是否绑定速度槽、
/// 颜色格式）全部只在进入 Pass 之后才确定，而 Prepare 必须在 Pass 之外执行。创建 PSO 是设备调用
/// 不是命令录制，在 Pass 内做是合法的，且按 key 缓存后每种形状只付一次代价。
///
/// 混合与深度状态照抄主管线的 Scene 透明分支，但 OM 的格式与采样数直接问绑定的资源要，
/// 而不是读 Device 的全局档位。两者在正常运行时一致，可一旦不一致（MSAA 档位下绑的是
/// MsaaSceneRT、离屏或无窗口路径下又可能是单采样目标），全局量就会烘出一个与附件不匹配的 PSO，
/// 而这种错配只能在运行期由调试层报出来。资源描述本身就是 OM 的事实来源，所以以它为准。
/// </summary>
internal sealed unsafe class DXParticleDrawPipeline : ParticleDrawPipeline
{
    /// <summary>根参数序号，与 ParticleSystemEffect.DrawHlsl 的 t0/t1 一一对应。</summary>
    const uint InstancesRoot = 0;
    const uint FrameRoot = 1;

    /// <summary>PSO 的 OM 签名。Velocity 为 Unknown 表示本 Pass 只绑了一个渲染目标，
    /// 于是"是否有速度槽"与"速度槽格式"用同一个字段表达，不会出现两者矛盾的 key。
    /// Normal 同理（A0.2 的 slot 2），且只会在 Velocity 非 Unknown 时出现。</summary>
    readonly record struct PipelineKey(Format Color, Format Velocity, Format Normal, uint SampleCount, uint SampleQuality);

    readonly Dictionary<PipelineKey, nint> _pipelines = [];
    ID3D12RootSignature* _signature;
    ID3D12CommandSignature* _commands;
    ID3D10Blob* _vertex;
    ID3D10Blob* _pixel;
    ID3D10Blob* _pixelVelocity;

    // Prepare 的产物，Draw 消费且只消费一次。
    ID3D12GraphicsCommandList* _command;
    DXRenderTarget? _color;
    RenderTarget? _depth;
    DXStorageBuffer? _instances;
    DXStorageBuffer? _args;
    DXStorageBuffer? _frame;
    ulong _stamp;
    uint _frameIndex;
    bool _prepared;
    bool _disposed;

    internal DXParticleDrawPipeline()
    {
        try { Initialize(); }
        catch { Dispose(); throw; }
    }

    void Initialize()
    {
        // 两个 root SRV，仅顶点阶段可见。没有输入布局，所以不能声明
        // AllowInputAssemblerInputLayout——该标志要求 PSO 真的带输入元素。
        var parameters = stackalloc RootParameter[2];
        parameters[0] = new()
        {
            ParameterType = RootParameterType.TypeSrv,
            Descriptor = new RootDescriptor { ShaderRegister = 0, RegisterSpace = 0 },
            ShaderVisibility = ShaderVisibility.Vertex,
        };
        parameters[1] = new()
        {
            ParameterType = RootParameterType.TypeSrv,
            Descriptor = new RootDescriptor { ShaderRegister = 1, RegisterSpace = 0 },
            ShaderVisibility = ShaderVisibility.Vertex,
        };
        var root = new RootSignatureDesc { NumParameters = 2, PParameters = parameters };
        using ComPtr<ID3D10Blob> blob = null;
        using ComPtr<ID3D10Blob> error = null;
        Device.CheckResult(Device.D3D12.SerializeRootSignature(&root, D3DRootSignatureVersion.Version1,
            blob.GetAddressOf(), error.GetAddressOf()));
        ID3D12RootSignature* signature = null;
        var iid = ID3D12RootSignature.Guid;
        Device.CheckResult(Device.D3dDevice->CreateRootSignature(0, blob.Get().GetBufferPointer(),
            blob.Get().GetBufferSize(), &iid, (void**)&signature));
        _signature = signature;

        // 命令签名只描述一条 DrawIndexed 记录，不改任何根参数，所以 root signature 必须传 null；
        // ByteStride 与共享层的 IndirectDrawIndexedArgs 同一个常量，布局漂移会在这里暴露。
        var argument = new IndirectArgumentDesc { Type = IndirectArgumentType.DrawIndexed };
        var commandDesc = new CommandSignatureDesc
        {
            ByteStride = IndirectDrawIndexedArgs.SizeInBytes,
            NumArgumentDescs = 1,
            PArgumentDescs = &argument,
            NodeMask = 0,
        };
        ID3D12CommandSignature* commands = null;
        iid = ID3D12CommandSignature.Guid;
        Device.CheckResult(Device.D3dDevice->CreateCommandSignature(&commandDesc, null, &iid, (void**)&commands));
        _commands = commands;

        _vertex = ShaderCompiler.CompileShaderFromSource(ParticleSystemEffect.DrawHlsl, "VSMain", "vs_5_0");
        _pixel = ShaderCompiler.CompileShaderFromSource(ParticleSystemEffect.DrawHlsl, "PSMain", "ps_5_0");
        _pixelVelocity = ShaderCompiler.CompileShaderFromSource(ParticleSystemEffect.DrawHlsl, "PSMainVelocity", "ps_5_0");
    }

    ID3D12PipelineState* GetOrCreatePipeline(in PipelineKey key)
    {
        if (_pipelines.TryGetValue(key, out nint cached)) return (ID3D12PipelineState*)cached;
        bool velocity = key.Velocity != Format.FormatUnknown;
        bool normal = velocity && key.Normal != Format.FormatUnknown;

        var stencil = new DepthStencilopDesc
        {
            StencilFailOp = StencilOp.Keep,
            StencilDepthFailOp = StencilOp.Keep,
            StencilPassOp = StencilOp.Keep,
            StencilFunc = ComparisonFunc.Always,
        };
        // 像素着色器输出预乘 alpha，所以源因子是 One 而不是 SrcAlpha。
        var blend = new RenderTargetBlendDesc
        {
            BlendEnable = 1,
            SrcBlend = Blend.One,
            DestBlend = Blend.InvSrcAlpha,
            BlendOp = BlendOp.Add,
            SrcBlendAlpha = Blend.One,
            DestBlendAlpha = Blend.InvSrcAlpha,
            BlendOpAlpha = BlendOp.Add,
            LogicOp = LogicOp.Noop,
            RenderTargetWriteMask = (byte)ColorWriteEnable.All,
        };
        // 速度槽写掩码置零，与主管线对透明几何的既有选择一致：保留下方不透明层的速度，
        // 比写 0 更好，后者会让 TAA 把运动的粒子当成静止背景去累积。
        var velocityBlend = blend;
        velocityBlend.BlendEnable = 0;
        velocityBlend.DestBlend = Blend.Zero;
        velocityBlend.DestBlendAlpha = Blend.Zero;
        velocityBlend.RenderTargetWriteMask = 0;

        var desc = new GraphicsPipelineStateDesc
        {
            PRootSignature = _signature,
            VS = new(_vertex->GetBufferPointer(), _vertex->GetBufferSize()),
            PS = velocity
                ? new(_pixelVelocity->GetBufferPointer(), _pixelVelocity->GetBufferSize())
                : new(_pixel->GetBufferPointer(), _pixel->GetBufferSize()),
            RasterizerState = new()
            {
                FillMode = FillMode.Solid,
                // 公告板的绕序取决于相机基与索引顺序的组合，关掉剔除比在着色器里翻正负更可靠。
                CullMode = CullMode.None,
                FrontCounterClockwise = 0,
                DepthClipEnable = 1,
                MultisampleEnable = key.SampleCount > 1 ? 1u : 0u,
            },
            // 深度测试开、深度写关：粒子互相之间不排序，写深度只会让后画的被先画的挡掉。
            DepthStencilState = new()
            {
                DepthEnable = 1,
                DepthWriteMask = DepthWriteMask.Zero,
                DepthFunc = ComparisonFunc.LessEqual,
                StencilEnable = 0,
                StencilReadMask = D3D12.DefaultStencilReadMask,
                StencilWriteMask = D3D12.DefaultStencilWriteMask,
                FrontFace = stencil,
                BackFace = stencil,
            },
            SampleMask = uint.MaxValue,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            NumRenderTargets = normal ? 3u : (velocity ? 2u : 1u),
            SampleDesc = new(key.SampleCount, key.SampleQuality),
            DSVFormat = Device.DepthBufferFormat,
        };
        desc.BlendState.IndependentBlendEnable = velocity ? 1u : 0u;
        desc.BlendState.RenderTarget[0] = blend;
        desc.BlendState.RenderTarget[1] = velocity ? velocityBlend : blend;
        desc.RTVFormats[0] = key.Color;
        if (velocity) desc.RTVFormats[1] = key.Velocity;
        // 法线槽与速度槽同一条规则：写掩码为零，保留下方不透明面的法线。着色器不必输出 SV_Target2，
        // 写掩码为零的槽不要求有对应输出，所以沿用速度变体的像素着色器。
        if (normal)
        {
            desc.BlendState.RenderTarget[2] = velocityBlend;
            desc.RTVFormats[2] = key.Normal;
        }

        ID3D12PipelineState* pipeline = null;
        var iid = ID3D12PipelineState.Guid;
        Device.CheckResult(Device.D3dDevice->CreateGraphicsPipelineState(&desc, &iid, (void**)&pipeline));
        _pipelines[key] = (nint)pipeline;
        return pipeline;
    }

    public override bool Prepare(in ParticleDrawInputs inputs)
    {
        _prepared = false;
        _color = null; _depth = null; _instances = null; _args = null; _frame = null;
        var cmd = Device.GraphicsCommandList;
        // ActiveTarget 非空即处于 Pass 内；此时录制 barrier 违反 PassDesc 契约，宁可放弃本帧提交。
        if (_disposed || cmd == null || Device.ActiveTarget != null
            || Pipeline.UnitQuadIndexBuffer == null
            || inputs.Color is not DXRenderTarget color || color.Color == null
            || color.RtvIndex == DXRenderTarget.NoRtv
            || inputs.Instances is not DXStorageBuffer instances || instances.Resource == null
            || instances.SizeInBytes < ParticleInstance.SizeInBytes
            || inputs.IndirectArgs is not DXStorageBuffer args || args.Resource == null
            || args.Usage != StorageBufferUsage.ComputeAndIndirectArgs
            || args.SizeInBytes < IndirectDrawIndexedArgs.SizeInBytes
            || inputs.Frame is not DXStorageBuffer frame || frame.Resource == null
            || frame.SizeInBytes < ParticleDraw.FrameBytes) return false;
        // 参数缓冲的转换由 IGraphics.TryPrepareIndirectArgs 负责，它必须已经先行；
        // 这里只核对落点，避免把一个仍停在 UnorderedAccess 的缓冲喂给 ExecuteIndirect。
        if (args.CurrentState != ResourceStates.IndirectArgument) return false;

        // 实例缓冲刚被 compute 写完，停在 UnorderedAccess；顶点阶段要读它。
        // 帧缓冲本就停在 AllShaderResource，门控转换因此是空操作。
        instances.TransitionTo(cmd, ResourceStates.AllShaderResource);
        frame.TransitionTo(cmd, ResourceStates.AllShaderResource);

        _color = color; _depth = inputs.Depth;
        _instances = instances; _args = args; _frame = frame;
        _command = cmd; _stamp = Device.GetCurrentRetireFenceValue(); _frameIndex = Device.FrameIndex;
        _prepared = true;
        return true;
    }

    public override bool Draw()
    {
        bool prepared = _prepared;
        var color = _color; var depth = _depth;
        var instances = _instances; var args = _args; var frame = _frame;
        _prepared = false;
        _color = null; _depth = null; _instances = null; _args = null; _frame = null;
        if (!prepared || _disposed || color == null || instances == null || args == null || frame == null) return false;

        var cmd = Device.GraphicsCommandList;
        if (cmd == null || cmd != _command || Device.FrameIndex != _frameIndex
            || Device.GetCurrentRetireFenceValue() != _stamp
            || Device.ActivePassId is not (RenderPassId.Scene or RenderPassId.SceneTransparent)) return false;

        var target = Device.ActiveTarget;
        if (target == null || target.Color == null || target.CurrentState != ResourceStates.RenderTarget) return false;
        // MSAA 档位下 BeginPass 把 Scene 的颜色目标换成 MsaaSceneRT，共享层交出的那张退为解析目标。
        // 不接受这一形式，粒子会在整个 MSAA 档位静默消失。
        if (!ReferenceEquals(target, color)
            && !(ReferenceEquals(target, Device.MsaaSceneRT) && ReferenceEquals(target.ResolveDest, color)))
            return false;
        // null == 使用后端默认深度，这同样要求准备期看到的是 null，否则深度语义已经变了。
        if (!ReferenceEquals(Device.ActiveDepthTarget, depth)) return false;
        if (instances.Resource == null || (instances.CurrentState & ResourceStates.NonPixelShaderResource) == 0
            || frame.Resource == null || (frame.CurrentState & ResourceStates.NonPixelShaderResource) == 0
            || args.Resource == null || args.CurrentState != ResourceStates.IndirectArgument) return false;

        // 向绑定的资源要 OM 签名。PSO 的 SampleDesc 必须与附件逐位相同，而附件的采样数和
        // 采样质量只有资源自己知道得准；颜色格式同理，包装器上的 ColorNativeFormat 对
        // 后备缓冲包装形式是留空的，反推资源则永远有值。
        var colorDesc = target.Color->GetDesc();
        var velocityTarget = Device.ActiveVelocityTarget;
        var velocityFormat = velocityTarget != null && velocityTarget.Color != null
            ? velocityTarget.Color->GetDesc().Format
            : Format.FormatUnknown;
        var normalTarget = Device.ActiveNormalTarget;
        var normalFormat = normalTarget != null && normalTarget.Color != null
            ? normalTarget.Color->GetDesc().Format
            : Format.FormatUnknown;
        var key = new PipelineKey(colorDesc.Format, velocityFormat, normalFormat,
            colorDesc.SampleDesc.Count, colorDesc.SampleDesc.Quality);
        var pipeline = GetOrCreatePipeline(key);
        if (pipeline == null) return false;

        // 主管线每次 SetPipeline 都会重设根签名、PSO 和拓扑，所以这里的覆写不会泄漏到后续绘制。
        cmd->SetGraphicsRootSignature(_signature);
        cmd->SetPipelineState(pipeline);
        cmd->IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
        var indexView = Pipeline.UnitQuadIndexBufferView;
        cmd->IASetIndexBuffer(&indexView);
        cmd->SetGraphicsRootShaderResourceView(InstancesRoot, instances.Resource->GetGPUVirtualAddress());
        cmd->SetGraphicsRootShaderResourceView(FrameRoot, frame.Resource->GetGPUVirtualAddress());
        // 实例数来自参数缓冲的第二个字段，CPU 这一侧从未知道过它的值。
        cmd->ExecuteIndirect(_commands, 1, args.Resource, 0, null, 0);
        return true;
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _prepared = false;
        _color = null; _depth = null; _instances = null; _args = null; _frame = null;
        // Blob 只是 CPU 侧字节，不被 GPU 引用，可以立即释放。
        if (_vertex != null) { _vertex->Release(); _vertex = null; }
        if (_pixel != null) { _pixel->Release(); _pixel = null; }
        if (_pixelVelocity != null) { _pixelVelocity->Release(); _pixelVelocity = null; }
        var pipelines = _pipelines.Values.ToArray();
        _pipelines.Clear();
        nint commands = (nint)_commands, signature = (nint)_signature;
        _commands = null; _signature = null;
        Device.EnqueueDeferredRelease(Device.GetCurrentRetireFenceValue(), () =>
        {
            foreach (nint pipeline in pipelines)
                if (pipeline != 0) ((ID3D12PipelineState*)pipeline)->Release();
            if (commands != 0) ((ID3D12CommandSignature*)commands)->Release();
            if (signature != 0) ((ID3D12RootSignature*)signature)->Release();
        });
    }
}
