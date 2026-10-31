// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Foundation;
using Metal;

namespace Season.Platforms.Shared.Apple.Metal;

/// <summary>
/// A0.1 的 Metal 提交端：一次间接索引绘制画出 compute 决定数量的公告板。
///
/// 这是四端里最轻的一端，相对 VKParticleDrawPipeline 少掉三样东西。
/// 一是描述符：Metal 直接 SetVertexBuffer，没有 set 也没有按帧描述符环，于是连 Device.WaitForFrameSlot
/// 那条"三帧前的 set 可能还被 pending 命令缓冲引用"的推理都不存在。
/// 二是屏障：写后读的依赖由驱动 hazard tracking 兑现，所以 Prepare 不录任何东西，
/// IGraphics.TryPrepareIndirectArgs 在这一端也只是做准入校验。
/// 三是延迟释放：已编码资源由 command buffer 的 retained references 持有，Dispose 可以立即放手。
///
/// PSO 不在构造期烘焙而是在 Draw 里按需烘焙，理由与另外两端一致：决定附件形状的事实只在进入 Pass
/// 之后才成立，而 Prepare 必须在 Pass 之外。创建 PSO 是设备调用不是编码器调用，在 Pass 内合法。
///
/// "附件形状"在 Metal 就是字面意义上的三个像素格式——颜色、深度、速度——因为 Metal 把附件格式直接
/// 烘进 PSO，不匹配即创建失败或绘制被丢弃。所以这里的 key 不像 Vulkan 那样是个枚举，而就是格式三元组：
/// 深度为 Invalid 表示本 Pass 没有深度附件（NoImplicitDepth 的目标），此时开深度测试是非法的；
/// 速度为 Invalid 表示单目标形状，PSO 只有一个颜色附件。三者都只能从 Device 记录的实际绑定反推。
/// </summary>
internal sealed class MTLParticleDrawPipeline : Season.Rendering.ParticleDrawPipeline
{
    /// <summary>单位四边形的 6 个索引，绕序与另外三端共用的 0,1,2,1,3,2 一致；角点由 [[vertex_id]] 的低两位展开。</summary>
    static readonly ushort[] QuadIndices = [0, 1, 2, 1, 3, 2];

    /// <summary>PSO 签名。Metal 把附件格式烘进 PSO，所以签名就是实际附件的格式组合；采样数不进 key，见类注释。</summary>
    readonly record struct PipelineKey(MTLPixelFormat Color, MTLPixelFormat Depth, MTLPixelFormat Velocity,
        MTLPixelFormat Normal);

    readonly Dictionary<PipelineKey, IMTLRenderPipelineState> _pipelines = [];

    IMTLBuffer? _quadIndices;

    // 着色器函数在构造期就编译好，与 DX 端同一选择。这是整条链里唯一没有任何离线编译手段的环节
    // （Windows 上没有 xcrun metal），把它的失败留到 Draw 就是 Pass 内抛异常；放在构造期则由
    // CreateParticleDrawPipeline 接住并返回 null，粒子效果整体不注册，画面退回改动前。
    IMTLFunction? _vertexFunction, _fragmentFunction, _fragmentVelocityFunction;

    /// <summary>深度测试开、深度写关：粒子互相之间不排序，写深度只会让后画的被先画的挡掉。
    /// 场景深度是正向的（主管线用 Less/LessEqual），所以这里是 LessEqual，与 Pipeline.TransparentDepthState 同形。</summary>
    IMTLDepthStencilState? _depthState;

    /// <summary>没有深度附件那一档。Metal 要求此时比较函数必须是 Always，否则是未定义行为，
    /// 与 MTLFogCompositePipeline 的无深度全屏管线同一选择。</summary>
    IMTLDepthStencilState? _noDepthState;

    // Prepare 的产物，Draw 消费且只消费一次。
    MTLRenderTarget? _color;
    Season.Rendering.RenderTarget? _depth;
    MTLStorageBuffer? _instances, _args, _frame;
    IMTLTexture? _colorTexture;
    IMTLCommandBuffer? _command;
    int _frameIndex;
    bool _prepared;
    bool _disposed;

    internal MTLParticleDrawPipeline()
    {
        try
        {
            _quadIndices = Device.ResourceManager.CreateIndexBuffer(QuadIndices);
            _quadIndices.Label = "Season-ParticleQuadIndices";

            // MTLShaderCompiler 按源码字符串缓存 IMTLLibrary，所以两档片元共用同一份顶点库，
            // 每份源码也只编译一次，哪怕后面因颜色格式不同而烘了多个 PSO。
            _vertexFunction = CreateFunction(Season.Rendering.Effects.ParticleSystemEffect.DrawVertexMsl,
                "VSMain");
            _fragmentFunction = CreateFunction(Season.Rendering.Effects.ParticleSystemEffect.DrawFragmentMsl,
                "PSMain");
            _fragmentVelocityFunction = CreateFunction(
                Season.Rendering.Effects.ParticleSystemEffect.DrawFragmentVelocityMsl, "PSMain");

            using var depth = new MTLDepthStencilDescriptor
            {
                DepthCompareFunction = MTLCompareFunction.LessEqual, DepthWriteEnabled = false,
            };
            _depthState = Device.MtlDevice.CreateDepthStencilState(depth)
                ?? throw new InvalidOperationException("粒子深度状态创建失败。");
            using var noDepth = new MTLDepthStencilDescriptor
            {
                DepthCompareFunction = MTLCompareFunction.Always, DepthWriteEnabled = false,
            };
            _noDepthState = Device.MtlDevice.CreateDepthStencilState(noDepth)
                ?? throw new InvalidOperationException("粒子无深度状态创建失败。");
        }
        catch { Dispose(); throw; }
    }

    static IMTLFunction CreateFunction(string source, string entryPoint)
    {
        var library = MTLShaderCompiler.Compile(Device.MtlDevice, source);
        // MSL 保留真实入口名（不像 GLSL 恒为 main），所以找不到就是源码与这里的名字走样了。
        return library.CreateFunction(entryPoint)
            ?? throw new InvalidOperationException($"粒子着色器未找到入口 {entryPoint}。");
    }

    IMTLRenderPipelineState? GetOrCreatePipeline(in PipelineKey key)
    {
        if (_pipelines.TryGetValue(key, out var cached)) return cached;
        bool velocity = key.Velocity != MTLPixelFormat.Invalid;

        // 函数已在构造期编译，此处只剩烘 PSO——因为只有它需要附件格式，而格式要到进了 Pass 才成立。
        // 注意：函数不能在这里 dispose，它们还要给后续颜色格式的 PSO 用。
        // 没有 VertexDescriptor：角点来自 [[vertex_id]]，实例值来自顶点阶段的只读缓冲，
        // 所以顶点槽 0/1 在这条管线里不是 [[stage_in]] 流，不与主管线的顶点布局冲突。
        using var desc = new MTLRenderPipelineDescriptor
        {
            Label = velocity ? "Season-ParticleDraw-Vel" : "Season-ParticleDraw",
            VertexFunction = _vertexFunction,
            FragmentFunction = velocity ? _fragmentVelocityFunction : _fragmentFunction,
            DepthAttachmentPixelFormat = key.Depth,
            StencilAttachmentPixelFormat = MTLPixelFormat.Invalid, RasterSampleCount = 1,
        };
        var color = desc.ColorAttachments[0];
        color.PixelFormat = key.Color;
        // 片元着色器输出预乘 alpha，所以源因子是 One 而不是 SourceAlpha。
        color.BlendingEnabled = true;
        color.RgbBlendOperation = MTLBlendOperation.Add;
        color.AlphaBlendOperation = MTLBlendOperation.Add;
        color.SourceRgbBlendFactor = MTLBlendFactor.One;
        color.DestinationRgbBlendFactor = MTLBlendFactor.OneMinusSourceAlpha;
        color.SourceAlphaBlendFactor = MTLBlendFactor.One;
        color.DestinationAlphaBlendFactor = MTLBlendFactor.OneMinusSourceAlpha;
        if (velocity)
        {
            // 速度槽写掩码置零，与主管线对透明几何的既有选择一致（Pipeline.CreatePipelineState 的
            // slot 1）：保留下方不透明层的速度，比写 0 更好，后者会让 TAA 把运动的粒子当成静止背景去累积。
            var motion = desc.ColorAttachments[1];
            motion.PixelFormat = key.Velocity;
            motion.BlendingEnabled = false;
            motion.WriteMask = MTLColorWriteMask.None;

            // A0.2 法线槽同理：粒子不是 SSR/贴花要看到的表面。写掩码为零的附件不要求片元函数有
            // 对应的 [[color(2)]] 输出，所以沿用速度版本的片元函数。
            if (key.Normal != MTLPixelFormat.Invalid)
            {
                var normal = desc.ColorAttachments[2];
                normal.PixelFormat = key.Normal;
                normal.BlendingEnabled = false;
                normal.WriteMask = MTLColorWriteMask.None;
            }
        }

        var pipeline = Device.MtlDevice.CreateRenderPipelineState(desc, out NSError? error);
        if (pipeline == null)
            throw new InvalidOperationException($"粒子 PSO 创建失败：{error?.LocalizedDescription ?? "(no NSError)"}");
        _pipelines[key] = pipeline;
        return pipeline;
    }

    public override bool Prepare(in Season.Rendering.ParticleDrawInputs inputs)
    {
        ClearPrepared();
        var command = Device.GraphicsCommandBuffer;
        // Metal 的 Pass 就是编码器，所以"在 Pass 之外"等价于当前没有渲染编码器；
        // 有编码器时录制任何东西都违反 PassDesc 契约，宁可放弃本帧提交。
        if (_disposed || Device.GraphicsEncoder != null || command == null || command.Handle == IntPtr.Zero
            || _quadIndices == null || _depthState == null || _noDepthState == null
            || _vertexFunction == null || _fragmentFunction == null || _fragmentVelocityFunction == null
            || inputs.Color is not MTLRenderTarget color || color.ColorTexture == null
            || color.Desc.SampleCount != 1
            || inputs.Instances is not MTLStorageBuffer instances || instances.Buffer == null
            || instances.SizeInBytes < Season.Rendering.ParticleInstance.SizeInBytes
            || inputs.IndirectArgs is not MTLStorageBuffer args || args.Buffer == null
            || args.Usage != Season.Rendering.StorageBufferUsage.ComputeAndIndirectArgs
            || args.SizeInBytes < Season.Rendering.IndirectDrawIndexedArgs.SizeInBytes
            || inputs.Frame is not MTLStorageBuffer frame || frame.Buffer == null
            || frame.SizeInBytes < Season.Rendering.ParticleDraw.FrameBytes) return false;

        _color = color; _depth = inputs.Depth; _colorTexture = color.ColorTexture;
        _instances = instances; _args = args; _frame = frame;
        _command = command; _frameIndex = Device.FrameIndex;
        _prepared = true;
        return true;
    }

    public override bool Draw()
    {
        bool prepared = _prepared;
        var color = _color; var depth = _depth;
        var instances = _instances; var args = _args; var frame = _frame;
        var colorTexture = _colorTexture; var command = _command;
        int frameIndex = _frameIndex;
        ClearPrepared();
        if (!prepared || _disposed || color == null || colorTexture == null || command == null
            || instances == null || args == null || frame == null) return false;

        var encoder = Device.GraphicsEncoder;
        // Metal 没有 fence 退休值可比，身份核对因此是"同一条 command buffer 的托管引用 + 同一个
        // FrameIndex"：command buffer 每帧新建，所以两者同时成立就排除了跨帧复用准备结果。
        if (encoder == null || encoder.Handle == IntPtr.Zero
            || command.Handle == IntPtr.Zero || !ReferenceEquals(command, Device.GraphicsCommandBuffer)
            || frameIndex != Device.FrameIndex
            || Device.ActivePassId is not (Season.Rendering.RenderPassId.Scene
                or Season.Rendering.RenderPassId.SceneTransparent)
            || !ReferenceEquals(color, Device.ActiveColorTarget)
            || Device.ActiveColorTexture?.Handle != colorTexture.Handle) return false;
        // null == 使用该目标自带的深度，这同样要求准备期看到的是 null，否则深度语义已经变了。
        if (!ReferenceEquals(Device.ActiveDepthTarget, depth)) return false;

        // 附件形状只能从实际绑定反推：显式深度缺席不代表没有深度附件，目标自带的那张同样算；
        // 速度槽只在三目标形状下存在，而准备期持有的 PassDesc 推不出这一点。
        var velocityTexture = Device.ActiveVelocityTexture;
        var velocityFormat = MTLPixelFormat.Invalid;
        var normalFormat = MTLPixelFormat.Invalid;
        if (velocityTexture != null)
        {
            if (velocityTexture.PixelFormat != MTLPixelFormat.RG16Float) return false;
            velocityFormat = velocityTexture.PixelFormat;
            normalFormat = Device.ActiveNormalTexture?.PixelFormat ?? MTLPixelFormat.Invalid;
        }
        var depthFormat = Device.ActiveDepthTexture?.PixelFormat ?? MTLPixelFormat.Invalid;
        var pipeline = GetOrCreatePipeline(new PipelineKey(colorTexture.PixelFormat, depthFormat, velocityFormat,
            normalFormat));
        if (pipeline == null) return false;

        // 主管线每条绘制都重绑 PSO、深度状态、剔除与顶点槽 0..4（见 Pipeline.DrawPrimitive），
        // 所以这里的覆写不会泄漏到后续绘制。
        encoder.SetRenderPipelineState(pipeline);
        encoder.SetDepthStencilState(depthFormat == MTLPixelFormat.Invalid ? _noDepthState! : _depthState!);
        // 公告板的绕序取决于相机基与索引顺序的组合，关掉剔除比在着色器里翻正负更可靠。
        encoder.SetCullMode(MTLCullMode.None);
        encoder.SetVertexBuffer(instances.Buffer, 0, 0);
        encoder.SetVertexBuffer(frame.Buffer, 0, 1);
        // 实例数来自参数缓冲的第二个字段，CPU 这一侧从未知道过它的值。
        // 重载是 drawIndexedPrimitives:indexType:indexBuffer:indexBufferOffset:indirectBuffer:indirectBufferOffset:
        // —— 索引缓冲在前、参数缓冲在后，两个偏移都是 nuint。
        encoder.DrawIndexedPrimitives(MTLPrimitiveType.Triangle, MTLIndexType.UInt16,
            _quadIndices!, 0, args.Buffer, 0);
        return true;
    }

    void ClearPrepared()
    {
        _prepared = false;
        _color = null; _depth = null; _colorTexture = null;
        _instances = null; _args = null; _frame = null;
        _command = null; _frameIndex = 0;
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClearPrepared();
        // 已编码资源由 command buffer 持有（Device 规则 5），所以立即释放包装对象是安全的，
        // 不需要 DX 与 VK 那种按 fence 放行的延迟释放队列；未编码的准备结果已经失效。
        foreach (var pipeline in _pipelines.Values) pipeline?.Dispose();
        _pipelines.Clear();
        _depthState?.Dispose(); _depthState = null;
        _noDepthState?.Dispose(); _noDepthState = null;
        // 函数是本类持有的，要放掉；库不是——它归 MTLShaderCompiler 的源码缓存所有。
        _vertexFunction?.Dispose(); _vertexFunction = null;
        _fragmentFunction?.Dispose(); _fragmentFunction = null;
        _fragmentVelocityFunction?.Dispose(); _fragmentVelocityFunction = null;
        _quadIndices?.Dispose(); _quadIndices = null;
    }
}
