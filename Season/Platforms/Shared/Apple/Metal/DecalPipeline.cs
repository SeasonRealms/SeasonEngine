// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Foundation;
using Metal;
using Season.Rendering;

namespace Season.Platforms.Shared.Apple.Metal;

/// <summary>
/// D1 屏幕空间贴花的 Metal 实现。
///
/// 每个贴花一次 36 顶点的 <c>DrawPrimitives</c>：顶点由 <c>[[vertex_id]]</c> 算出投影盒的六个面，
/// 光栅器只留背面（<see cref="MTLCullMode.Front"/>），因此每个像素最多命中一次 —— 混合不会被叠加两遍，
/// 也就不需要模板。像素着色器用本帧深度重建世界坐标，折进盒的局部空间后取 UV；颜色通过固定功能混合写回
/// SceneColor，所以 SceneColor 不需要同时是可采样资源，Pass 里它只是颜色附件。
///
/// 与另外三端的三处差异，都是 Metal 自己的形状：
/// <list type="bullet">
/// <item>没有 render pass 对象：附件由 <see cref="Device.BeginPass"/> 的 <c>MTLRenderPassDescriptor</c> 现建，
/// 所以本类只需在 <see cref="Device"/> 侧确认「无深度附件 + 可选的颜色附件 1」；</item>
/// <item>附件格式烘进 PSO，但贴花的附件形状只有一种（RGBA16Float + 可选第二张 RGBA16Float + 无深度），
/// 因此四个 PSO 在构造期一次烘好，失败由 <c>CreateDecalPipeline</c> 接住并整体退回；</item>
/// <item>没有描述符环也没有 fence：Metal 直接绑定资源，跨帧复用由帧内时序（每帧新建 command buffer）排除。</item>
/// </list>
///
/// 时序与 <see cref="MTLFogCompositePipeline"/> 相同：Prepare 在 Pass 外解析资源，Draw 只能在
/// <see cref="RenderPassId.SceneDecal"/> 内消费一次；任何一项与准备期不符都放弃绘制而不是部分绘制。
/// </summary>
internal sealed class MTLDecalPipeline : DecalPipeline
{
    /// <summary>逐贴花的纹理解析器：只查后端纹理字典，不加载。名字 → 纹理为空时该 draw 被跳过。</summary>
    readonly Dictionary<string, Texture> _textures;

    // 三个函数来自同一份源码、同一个库，整条管线只编译一次。
    IMTLFunction? _vertexFunction, _fragmentFunction, _fragmentRoughnessFunction;

    /// <summary>本 Pass 没有深度附件，比较函数必须是 Always —— 与雾合成、粒子的无深度档同一选择。</summary>
    IMTLDepthStencilState? _noDepthState;

    /// <summary>[模式 0=Modulate/1=Additive, 是否带法线附件]。附件数量必须与 PSO 的颜色附件数一致。</summary>
    readonly IMTLRenderPipelineState?[,] _pipelines = new IMTLRenderPipelineState?[2, 2];

    readonly Texture?[] _drawTextures = new Texture?[DecalMath.MaxDecals];
    readonly bool[] _drawSkip = new bool[DecalMath.MaxDecals];

    // Prepare 的产物，Draw 消费且只消费一次。
    // RT 包装对象也一并持有：纹理引用只是其中的字段，保住 RT 就保住了它不能在本帧中途被重建。
    MTLRenderTarget? _destination;
    MTLRenderTarget? _color, _depth, _normal;
    IMTLTexture? _colorTexture, _depthTexture, _normalTexture;
    MTLStorageBuffer? _frame;
    IMTLCommandBuffer? _command;
    DecalInputs _inputs;
    int _frameIndex;
    bool _writeRoughness;
    bool _disposed;

    internal MTLDecalPipeline(Dictionary<string, Texture> textures)
    {
        _textures = textures;
        try { Initialize(); }
        catch { Dispose(); throw; }
    }

    void Initialize()
    {
        var library = MTLShaderCompiler.Compile(Device.MtlDevice, DecalShaders.Msl);
        _vertexFunction = CreateFunction(library, "VSMain");
        _fragmentFunction = CreateFunction(library, "PSMain");
        _fragmentRoughnessFunction = CreateFunction(library, "PSMainRoughness");

        using var noDepth = new MTLDepthStencilDescriptor
        {
            DepthCompareFunction = MTLCompareFunction.Always, DepthWriteEnabled = false,
        };
        _noDepthState = Device.MtlDevice.CreateDepthStencilState(noDepth)
            ?? throw new InvalidOperationException("贴花深度状态创建失败。");

        for (int roughness = 0; roughness < 2; roughness++)
        {
            var fragment = roughness == 1 ? _fragmentRoughnessFunction : _fragmentFunction;
            _pipelines[0, roughness] = CreatePipeline(fragment!, additive: false, roughness == 1);
            _pipelines[1, roughness] = CreatePipeline(fragment!, additive: true, roughness == 1);
        }
    }

    static IMTLFunction CreateFunction(IMTLLibrary library, string entryPoint)
        // MSL 保留真实入口名（不像 GLSL 恒为 main），找不到就是源码与这里的名字走样了。
        => library.CreateFunction(entryPoint)
            ?? throw new InvalidOperationException($"贴花着色器未找到入口 {entryPoint}。");

    /// <summary>
    /// 两种混合语义共用一套着色器，只是源因子不同：
    /// Modulate 用 <c>dst * src</c>（SourceRgb = DestinationColor, DestinationRgb = Zero）把像素输出当成调制量，
    /// Additive 用 <c>dst + src</c>。alpha 一律保留目标值，贴花不参与后续的 alpha 判定。
    ///
    /// <paramref name="roughness"/> 为 true 时挂第二个颜色附件（SceneNormal）。它只开 B 通道写掩码，
    /// 并用 coverage 在原值上插值，所以八面体法线与金属度不被碰。
    /// </summary>
    IMTLRenderPipelineState CreatePipeline(IMTLFunction fragment, bool additive, bool roughness)
    {
        using var desc = new MTLRenderPipelineDescriptor
        {
            Label = (additive ? "Season-Decal-Add" : "Season-Decal-Mod") + (roughness ? "-Rough" : string.Empty),
            VertexFunction = _vertexFunction,
            FragmentFunction = fragment,
            // 贴花 Pass 特意不挂深度：深度这一帧是采样源，同一资源不能既采样又当附件。
            DepthAttachmentPixelFormat = MTLPixelFormat.Invalid,
            StencilAttachmentPixelFormat = MTLPixelFormat.Invalid,
            RasterSampleCount = 1,
        };
        var color = desc.ColorAttachments[0];
        color.PixelFormat = MTLPixelFormat.RGBA16Float;
        color.BlendingEnabled = true;
        color.RgbBlendOperation = MTLBlendOperation.Add;
        color.AlphaBlendOperation = MTLBlendOperation.Add;
        color.SourceRgbBlendFactor = additive ? MTLBlendFactor.One : MTLBlendFactor.DestinationColor;
        color.DestinationRgbBlendFactor = additive ? MTLBlendFactor.One : MTLBlendFactor.Zero;
        color.SourceAlphaBlendFactor = MTLBlendFactor.Zero;
        color.DestinationAlphaBlendFactor = MTLBlendFactor.One;
        if (roughness)
        {
            // 只写 .z（Blue）：法线的八面体编码在 .xy，金属度在 .w，两者必须保留。
            // 混合用 coverage 做 lerp，coverage 为 0 时结果恰好等于原值，所以「不碰粗糙度」的贴花不会改变目标。
            var normal = desc.ColorAttachments[1];
            normal.PixelFormat = MTLPixelFormat.RGBA16Float;
            normal.BlendingEnabled = true;
            normal.RgbBlendOperation = MTLBlendOperation.Add;
            normal.AlphaBlendOperation = MTLBlendOperation.Add;
            normal.SourceRgbBlendFactor = MTLBlendFactor.SourceAlpha;
            normal.DestinationRgbBlendFactor = MTLBlendFactor.OneMinusSourceAlpha;
            normal.SourceAlphaBlendFactor = MTLBlendFactor.One;
            normal.DestinationAlphaBlendFactor = MTLBlendFactor.Zero;
            normal.WriteMask = MTLColorWriteMask.Blue;
        }
        var pipeline = Device.MtlDevice.CreateRenderPipelineState(desc, out NSError? error);
        return pipeline ?? throw new InvalidOperationException(
            $"贴花 PSO 创建失败：{error?.LocalizedDescription ?? "(no NSError)"}");
    }

    static bool Readable(IMTLTexture? texture, MTLTextureType type) => texture != null
        && texture.Handle != IntPtr.Zero && texture.TextureType == type
        && texture.Width > 0 && texture.Height > 0 && (texture.Usage & MTLTextureUsage.ShaderRead) != 0;

    /// <summary>后端纹理字典里的贴图：包装对象已就绪、底层纹理可采样。纹理尚未上传完的贴花整个跳过。</summary>
    static bool Samplable(Texture? texture) => texture != null && Volatile.Read(ref texture.Ready)
        && Readable(texture.Image, MTLTextureType.k2D);

    /// <summary>
    /// 核对形状并把资源折成引用快照。返回 false 表示本帧放弃：贴花只在「颜色是单采样 HDR、深度与颜色同尺寸」
    /// 这一种形状下成立，其余情况宁可不出图也不出错图。
    ///
    /// 法线目标在 s9 之后是<b>附件</b>而不是采样源：同一个资源不能同时是附件与采样源。
    /// 与 DX 不同的是这一端不回退：<see cref="Device.BeginPass"/> 挂不挂颜色附件 1 只取决于
    /// <see cref="DecalInputs.WriteRoughness"/>，而 Metal 把附件数量烘进了 PSO ——
    /// 若这里降级成单附件 PSO，实际 Pass 却挂着两个附件。宁可整帧不出贴花也不赌一个未定义的附件数。
    /// </summary>
    bool Resolve(in DecalInputs inputs, MTLRenderTarget destination, out bool roughness)
    {
        roughness = false;
        if (inputs.Color is not MTLRenderTarget color || inputs.Depth is not MTLRenderTarget depth
            || inputs.FrameBuffer is not MTLStorageBuffer frame || frame.Buffer == null
            || frame.Buffer.Handle == IntPtr.Zero
            || frame.SizeInBytes < DecalMath.ByteCount || frame.Buffer.Length < (nuint)DecalMath.ByteCount
            || color.Desc.ColorFormat != RtFormat.Rgba16Float || destination.Desc.ColorFormat != RtFormat.Rgba16Float
            || color.Desc.SampleCount > 1 || depth.Desc.SampleCount > 1 || destination.Desc.SampleCount > 1
            || depth.Desc.ColorFormat != RtFormat.None || depth.Desc.DepthFormat != RtFormat.D32Float
            // 本 Pass 把颜色目标同时当作混合底图，共享层因此必须交出同一张目标。
            || !ReferenceEquals(destination, color)
            || color.Width <= 0 || color.Height <= 0 || color.Width != depth.Width || color.Height != depth.Height
            || inputs.Count <= 0 || inputs.Count > DecalMath.MaxDecals) return false;

        var colorTexture = color.ColorTexture;
        var depthTexture = depth.DepthTexture;
        if (colorTexture == null || destination.ColorTexture == null
            || !Readable(colorTexture, MTLTextureType.k2D)) return false;
        if (depthTexture == null || depthTexture.PixelFormat != MTLPixelFormat.Depth32Float
            || !Readable(depthTexture, MTLTextureType.k2D)) return false;

        // 粗糙度写入要求法线目标与颜色同形。Metal 的附件数烘在 PSO 里，所以形状不符时整帧放弃而不是降级，
        // 否则就会出现「两个附件 + 单附件 PSO」的未定义组合。
        MTLRenderTarget? normal = null;
        IMTLTexture? normalTexture = null;
        if (inputs.WriteRoughness)
        {
            if (inputs.Normal is not MTLRenderTarget n || n.Desc.ColorFormat != RtFormat.Rgba16Float
                || n.Desc.SampleCount > 1 || n.Width != color.Width || n.Height != color.Height
                || !Readable(n.ColorTexture, MTLTextureType.k2D)) return false;
            normal = n;
            normalTexture = n.ColorTexture;
        }

        _color = color; _depth = depth; _normal = normal;
        _colorTexture = colorTexture; _depthTexture = depthTexture; _normalTexture = normalTexture;
        _frame = frame;
        roughness = normalTexture != null;
        return true;
    }

    public override bool Prepare(in DecalInputs inputs, RenderTarget destination)
    {
        ClearPrepared();
        var command = Device.GraphicsCommandBuffer;
        // Metal 的 Pass 就是编码器，所以「在 Pass 之外」等价于当前没有渲染编码器。
        if (_disposed || Device.GraphicsEncoder != null || command == null || command.Handle == IntPtr.Zero
            || destination is not MTLRenderTarget target || !Resolve(inputs, target, out bool roughness)) return false;

        _inputs = inputs;
        _destination = target;
        _writeRoughness = roughness;
        _command = command;
        _frameIndex = Device.FrameIndex;

        // s3 逐贴花纹理：只查字典，不在这里加载。名字未注册或还没上传完的贴花整个跳过。
        Array.Clear(_drawTextures);
        Array.Clear(_drawSkip);
        lock (_textures)
        {
            for (int i = 0; i < inputs.Count; i++)
            {
                string? name = inputs.Draws[i].Texture;
                if (name == null) continue;
                if (!_textures.TryGetValue(name, out var texture) || !Samplable(texture))
                {
                    _drawSkip[i] = true;
                    continue;
                }
                _drawTextures[i] = texture;
            }
        }
        return true;
    }

    public override unsafe bool Draw()
    {
        var destination = _destination;
        var command = _command;
        var colorTexture = _colorTexture;
        var depthTexture = _depthTexture;
        var normalTexture = _normalTexture;
        var frame = _frame;
        var inputs = _inputs;
        bool roughness = _writeRoughness;
        int frameIndex = _frameIndex;
        ClearPrepared();
        var encoder = Device.GraphicsEncoder;
        if (_disposed || destination == null || frame == null || colorTexture == null || depthTexture == null
            || encoder == null || encoder.Handle == IntPtr.Zero
            || command == null || command.Handle == IntPtr.Zero || !ReferenceEquals(command, Device.GraphicsCommandBuffer)
            || frameIndex != Device.FrameIndex || Device.ActivePassId != RenderPassId.SceneDecal
            || !ReferenceEquals(destination, Device.ActiveColorTarget)
            || Device.ActiveColorTexture?.Handle != colorTexture.Handle
            // 附件形状只能从实际绑定反推：法线有没有挂成附件决定用哪一档 PSO。
            || roughness != (Device.ActiveNormalTexture != null)
            || (roughness && Device.ActiveNormalTexture!.Handle != normalTexture!.Handle)) return false;

        var pipeline = _pipelines[0, roughness ? 1 : 0];
        if (pipeline == null) return false;

        // 主管线每条绘制都重绑 PSO、深度状态、剔除与顶点槽 0..4，所以这里的覆写不会泄漏到后续绘制。
        encoder.SetDepthStencilState(_noDepthState!);
        // 绕序在 Metal 上是编码器动态状态，不是 PSO 状态：默认的 CounterClockwise 会让
        // 「只留背面」剔错一侧（Metal 的绕序在 y 向下的帧缓冲坐标里判定，与 DX 的默认正好相反），
        // 所以这里显式对齐主管线：顺时针为正面，Front 剔除 = 剔掉 DX 也会剔掉的那一侧。
        encoder.SetFrontFacingWinding(MTLWinding.Clockwise);
        // 只留背面：投影盒是凸体，背面在每个像素上恰好命中一次。
        encoder.SetCullMode(MTLCullMode.Front);
        encoder.SetVertexBuffer(frame.Buffer, 0, 0);
        encoder.SetFragmentBuffer(frame.Buffer, 0, 0);
        // 无贴图时着色器不会采样，但绑定必须合法：与 DX/VK 一样拿深度图占位。
        encoder.SetFragmentTexture(depthTexture, 0);
        encoder.SetFragmentTexture(depthTexture, 1);

        int mode = -1;
        // MSL 侧是 uint4，字节原样拷贝，必须写整数位；写 float 会让第二个贴花起的偏移越界。
        var parameters = stackalloc uint[4];
        for (int i = 0; i < inputs.Count; i++)
        {
            // 贴图没就绪的贴花整个跳过：采样一个占位纹理会把「还没加载完」画成一块错误颜色。
            if (_drawSkip[i]) continue;
            var draw = inputs.Draws[i];
            int next = draw.Mode == DecalMode.Additive ? 1 : 0;
            if (next != mode)
            {
                var selected = _pipelines[next, roughness ? 1 : 0];
                if (selected == null) return false;
                encoder.SetRenderPipelineState(selected);
                mode = next;
            }
            var texture = _drawTextures[i];
            if (texture != null) encoder.SetFragmentTexture(texture.Image, 1);
            // 贴花槽位只到 64，uint4 足够；顶点与像素读的是同一份，所以两个阶段都要推。
            parameters[0] = (uint)draw.Index;
            encoder.SetVertexBytes((IntPtr)parameters, 16, 1);
            encoder.SetFragmentBytes((IntPtr)parameters, 16, 1);
            encoder.DrawPrimitives(MTLPrimitiveType.Triangle, 0, 36);
        }
        return true;
    }

    void ClearPrepared()
    {
        _destination = null;
        _color = null; _depth = null; _normal = null;
        _colorTexture = null; _depthTexture = null; _normalTexture = null;
        _frame = null; _command = null; _inputs = default;
        _frameIndex = 0; _writeRoughness = false;
        // 逐贴花的纹理与跳过表不在这里清：Draw 在 ClearPrepared 之后仍要消费它们，
        // 而 Prepare 一定先于 Draw 发生，所以在 Prepare 入口清才是安全的。
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClearPrepared();
        Array.Clear(_drawTextures);
        Array.Clear(_drawSkip);
        // 已编码资源由 command buffer 持有（Device 规则 5），所以立即释放是安全的；未编码的准备结果已经失效。
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
            {
                _pipelines[i, j]?.Dispose();
                _pipelines[i, j] = null;
            }
        _noDepthState?.Dispose(); _noDepthState = null;
        // 函数是本类持有的，要放掉；库不是 —— 它归 MTLShaderCompiler 的源码缓存所有。
        _vertexFunction?.Dispose(); _vertexFunction = null;
        _fragmentFunction?.Dispose(); _fragmentFunction = null;
        _fragmentRoughnessFunction?.Dispose(); _fragmentRoughnessFunction = null;
    }
}
