// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Foundation;
using Metal;
using Season.Rendering;
using Season.Rendering.Effects;

namespace Season.Platforms.Shared.Apple.Metal;

/// <summary>
/// P2 水面材质的 Metal 实现。每个水面一次 <c>DrawPrimitives</c>：顶点由 <c>[[vertex_id]]</c>
/// 展开（P3 是 6 顶点的单位平面，P4 起是 tile 内的 subdiv² 网格，<c>[[instance_id]]</c> 就是 tile 索引），
/// 世界矩阵与分块参数都在帧缓冲与 buffer(1) 里，没有顶点缓冲也没有实例缓冲。
///
/// 与另外三端的差异只有 Metal 自己的形状：
/// <list type="bullet">
/// <item>没有 render pass 对象：附件由 <see cref="Device.BeginPass"/> 的 <c>MTLRenderPassDescriptor</c> 现建，
/// 本类只需保证附件形状与烘好的 PSO 一致 —— 单颜色附件、无深度、单采样、RGBA16Float；</item>
/// <item>没有描述符环也没有 fence：Metal 直接绑定资源，跨帧复用由「每帧新建 command buffer」排除；</item>
/// <item>深度是<b>采样源</b>而不是附件：遮挡靠着色器里的软件深度测试，这与贴花同理，
/// 也是 SceneDepth 能整帧保持可采样的前提。</item>
/// </list>
///
/// 折射源是开画前的场景色快照，由 <see cref="Device.CopyRenderTarget"/> 用 blit 编码器在 Pass 外拷好；
/// 本 Pass 的颜色附件不能同时被采样，这也是快照存在的唯一原因。
/// 混合是标准 alpha 混合（SourceAlpha / OneMinusSourceAlpha），与另外三端逐位对齐。
/// </summary>
internal sealed class MTLWaterPipeline : Season.Rendering.WaterPipeline
{
    /// <summary>逐水面的法线纹理解析器：只查后端纹理字典，不加载。</summary>
    readonly Dictionary<string, Texture> _textures;

    IMTLFunction? _vertexFunction, _fragmentFunction;

    /// <summary>本 Pass 没有深度附件，比较函数必须是 Always —— 与贴花、雾合成同一档。</summary>
    IMTLDepthStencilState? _noDepthState;

    IMTLRenderPipelineState? _pipeline;

    readonly Texture?[] _drawNormals = new Texture?[WaterMath.MaxWaters];
    /// <summary>本帧的 Hi-Z 四级金字塔；<see cref="HiZEffect.Ready"/> 为假时整组为空，退回深度图占位。</summary>
    readonly Texture?[] _hz = new Texture?[HiZEffect.LevelCount];

    // Prepare 的产物，Draw 消费且只消费一次。
    // RT 包装对象也一并持有：纹理引用只是其中的字段，保住 RT 就保住了它不能在本帧中途被重建。
    MTLRenderTarget? _destination;
    MTLRenderTarget? _color, _depth, _refraction;
    IMTLTexture? _colorTexture, _depthTexture, _refractionTexture, _skyTexture;
    MTLStorageBuffer? _frame;
    IMTLCommandBuffer? _command;
    WaterInputs _inputs;
    int _frameIndex;
    bool _disposed;

    internal MTLWaterPipeline(Dictionary<string, Texture> textures)
    {
        _textures = textures;
        try { Initialize(); }
        catch { Dispose(); throw; }
    }

    void Initialize()
    {
        var library = MTLShaderCompiler.Compile(Device.MtlDevice, WaterShaders.Msl);
        // MSL 保留真实入口名（不像 GLSL 恒为 main），找不到就是源码与这里的名字走样了。
        _vertexFunction = library.CreateFunction("VSMain")
            ?? throw new InvalidOperationException("水面着色器未找到入口 VSMain。");
        _fragmentFunction = library.CreateFunction("PSMain")
            ?? throw new InvalidOperationException("水面着色器未找到入口 PSMain。");

        using var noDepth = new MTLDepthStencilDescriptor
        {
            DepthCompareFunction = MTLCompareFunction.Always, DepthWriteEnabled = false,
        };
        _noDepthState = Device.MtlDevice.CreateDepthStencilState(noDepth)
            ?? throw new InvalidOperationException("水面深度状态创建失败。");

        _pipeline = CreatePipeline();
    }

    IMTLRenderPipelineState CreatePipeline()
    {
        using var desc = new MTLRenderPipelineDescriptor
        {
            Label = "Season-Water",
            VertexFunction = _vertexFunction,
            FragmentFunction = _fragmentFunction,
            // 水面 Pass 特意不挂深度：深度这一帧是采样源，同一资源不能既采样又当附件。
            DepthAttachmentPixelFormat = MTLPixelFormat.Invalid,
            StencilAttachmentPixelFormat = MTLPixelFormat.Invalid,
            RasterSampleCount = 1,
        };
        var color = desc.ColorAttachments[0];
        color.PixelFormat = MTLPixelFormat.RGBA16Float;
        color.BlendingEnabled = true;
        color.RgbBlendOperation = MTLBlendOperation.Add;
        color.AlphaBlendOperation = MTLBlendOperation.Add;
        // 着色器输出非预乘色；alpha 槽保留目标值，水面不参与后续的 alpha 判定。
        color.SourceRgbBlendFactor = MTLBlendFactor.SourceAlpha;
        color.DestinationRgbBlendFactor = MTLBlendFactor.OneMinusSourceAlpha;
        color.SourceAlphaBlendFactor = MTLBlendFactor.Zero;
        color.DestinationAlphaBlendFactor = MTLBlendFactor.One;
        var pipeline = Device.MtlDevice.CreateRenderPipelineState(desc, out NSError? error);
        return pipeline ?? throw new InvalidOperationException(
            $"水面 PSO 创建失败：{error?.LocalizedDescription ?? "(no NSError)"}");
    }

    static bool Readable(IMTLTexture? texture, MTLTextureType type) => texture != null
        && texture.Handle != IntPtr.Zero && texture.TextureType == type
        && texture.Width > 0 && texture.Height > 0 && (texture.Usage & MTLTextureUsage.ShaderRead) != 0;

    /// <summary>后端纹理字典里的贴图：包装对象已就绪、底层纹理可采样。</summary>
    static bool Samplable(Texture? texture) => texture != null && Volatile.Read(ref texture.Ready)
        && Readable(texture.Image, MTLTextureType.k2D);

    /// <summary>
    /// 核对形状并把资源折成引用快照。返回 false 表示本帧放弃：水面只在「颜色是单采样 HDR、
    /// 深度与颜色同尺寸、折射快照另是一张同形纹理」这一种形状下成立，其余情况宁可不出图也不出错图。
    /// </summary>
    bool Resolve(in WaterInputs inputs, MTLRenderTarget destination)
    {
        if (inputs.Color is not MTLRenderTarget color || inputs.Depth is not MTLRenderTarget depth
            || inputs.RefractionSource is not MTLRenderTarget refraction
            || inputs.FrameBuffer is not MTLStorageBuffer frame || frame.Buffer == null
            || frame.Buffer.Handle == IntPtr.Zero
            || frame.SizeInBytes < WaterMath.ByteCount || frame.Buffer.Length < (nuint)WaterMath.ByteCount
            || color.Desc.ColorFormat != RtFormat.Rgba16Float || destination.Desc.ColorFormat != RtFormat.Rgba16Float
            || refraction.Desc.ColorFormat != RtFormat.Rgba16Float
            || color.Desc.SampleCount > 1 || depth.Desc.SampleCount > 1 || destination.Desc.SampleCount > 1
            || refraction.Desc.SampleCount > 1
            || depth.Desc.ColorFormat != RtFormat.None || depth.Desc.DepthFormat != RtFormat.D32Float
            || ReferenceEquals(refraction, color)
            // 本 Pass 把颜色目标同时当作混合底图，共享层因此必须交出同一张目标。
            || !ReferenceEquals(destination, color)
            || color.Width <= 0 || color.Height <= 0 || color.Width != depth.Width || color.Height != depth.Height
            || color.Width != refraction.Width || color.Height != refraction.Height
            || inputs.Count <= 0 || inputs.Count > WaterMath.MaxWaters) return false;

        var colorTexture = color.ColorTexture;
        var depthTexture = depth.DepthTexture;
        var refractionTexture = refraction.ColorTexture;
        if (colorTexture == null || destination.ColorTexture == null || refractionTexture == null
            || !Readable(colorTexture, MTLTextureType.k2D) || !Readable(refractionTexture, MTLTextureType.k2D)) return false;
        if (depthTexture == null || depthTexture.PixelFormat != MTLPixelFormat.Depth32Float
            || !Readable(depthTexture, MTLTextureType.k2D)) return false;

        _color = color; _depth = depth; _refraction = refraction;
        _colorTexture = colorTexture; _depthTexture = depthTexture; _refractionTexture = refractionTexture;
        _frame = frame;
        return true;
    }

    public override bool Prepare(in WaterInputs inputs, RenderTarget destination)
    {
        ClearPrepared();
        var command = Device.GraphicsCommandBuffer;
        // Metal 的 Pass 就是编码器，所以「在 Pass 之外」等价于当前没有渲染编码器。
        if (_disposed || Device.GraphicsEncoder != null || command == null || command.Handle == IntPtr.Zero
            || destination is not MTLRenderTarget target || !Resolve(inputs, target)) return false;

        _inputs = inputs;
        _destination = target;
        _command = command;
        _frameIndex = Device.FrameIndex;

        // 逐水面法线贴图：只查字典，不在这里加载。名字未注册或还没上传完的水面退回程序化波浪。
        Array.Clear(_drawNormals);
        lock (_textures)
        {
            for (int i = 0; i < inputs.Count; i++)
            {
                string? name = inputs.Draws[i].Texture;
                if (name == null) continue;
                if (!_textures.TryGetValue(name, out var texture) || !Samplable(texture)) continue;
                _drawNormals[i] = texture;
            }
        }

        // 天空 LUT 是 P0 建的那张 compute 纹理：名字没注册（StaticCube 档）时退回深度图占位，
        // 着色器侧的 misc.w 门控保证它不会被真正采样。
        _skyTexture = _depthTexture;
        if (inputs.SkyView != null)
        {
            lock (_textures)
            {
                if (_textures.TryGetValue(inputs.SkyView, out var sky) && Samplable(sky))
                    _skyTexture = sky.Image;
            }
        }

        // P3 Hi-Z：逐级解析本帧的金字塔。任一级缺席就整组退回，与着色器侧的
        // frame[12].x 门控同源（两边都以 HiZEffect.Ready 为准），不会出现半套金字塔。
        Array.Clear(_hz);
        if (HiZEffect.Ready)
        {
            lock (_textures)
            {
                for (int level = 0; level < _hz.Length; level++)
                {
                    if (!_textures.TryGetValue(HiZEffect.LevelName(level), out var texture) || !Samplable(texture))
                    {
                        Array.Clear(_hz);
                        break;
                    }
                    _hz[level] = texture;
                }
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
        var refractionTexture = _refractionTexture;
        var skyTexture = _skyTexture;
        var hz = _hz;
        var frame = _frame;
        var inputs = _inputs;
        int frameIndex = _frameIndex;
        ClearPrepared();
        var encoder = Device.GraphicsEncoder;
        if (_disposed || destination == null || frame == null || colorTexture == null || depthTexture == null
            || refractionTexture == null || skyTexture == null
            || encoder == null || encoder.Handle == IntPtr.Zero
            || command == null || command.Handle == IntPtr.Zero || !ReferenceEquals(command, Device.GraphicsCommandBuffer)
            || frameIndex != Device.FrameIndex || Device.ActivePassId != RenderPassId.SceneWater
            || !ReferenceEquals(destination, Device.ActiveColorTarget)
            || Device.ActiveColorTexture?.Handle != colorTexture.Handle
            // 附件形状只能从实际绑定反推：水面 Pass 的深度必须是采样源而不是附件。
            || Device.ActiveDepthTexture != null) return false;
        var pipeline = _pipeline;
        if (pipeline == null) return false;

        // 主管线每条绘制都重绑 PSO、深度状态、剔除与顶点槽，所以这里的覆写不会泄漏到后续绘制。
        encoder.SetRenderPipelineState(pipeline);
        encoder.SetDepthStencilState(_noDepthState!);
        encoder.SetFrontFacingWinding(MTLWinding.Clockwise);
        // 双面：水面从水下也应该看得到，俯仰角很大时背面剔除会让水面整块消失。
        encoder.SetCullMode(MTLCullMode.None);
        encoder.SetVertexBuffer(frame.Buffer, 0, 0);
        encoder.SetFragmentBuffer(frame.Buffer, 0, 0);
        encoder.SetFragmentTexture(depthTexture, 0);
        encoder.SetFragmentTexture(refractionTexture, 1);
        encoder.SetFragmentTexture(skyTexture, 3);
        // P3：Hi-Z 的四级金字塔占 texture(4..7)；缺席时逐级退回深度图占位。
        for (int level = 0; level < hz.Length; level++)
            encoder.SetFragmentTexture(hz[level]?.Image ?? depthTexture, (nuint)(4 + level));

        // MSL 侧是 uint4，字节原样拷贝，必须写整数位；写 float 会让第二个水面起的偏移越界。
        // P4 起后三个分量是分块网格参数（tilesX, tilesZ, subdiv），不分块时是 (1,1,0)。
        var parameters = stackalloc uint[4];
        int drawn = 0;
        for (int i = 0; i < inputs.Count; i++)
        {
            var draw = inputs.Draws[i];
            // 无贴图的水面仍然绑一个合法纹理：着色器用 material3.x 门控，不会真的采样。
            encoder.SetFragmentTexture(_drawNormals[i]?.Image ?? depthTexture, 2);
            parameters[0] = (uint)draw.Index;
            parameters[1] = (uint)draw.TilesX;
            parameters[2] = (uint)draw.TilesZ;
            parameters[3] = (uint)draw.Subdiv;
            encoder.SetVertexBytes((IntPtr)parameters, 16, 1);
            encoder.SetFragmentBytes((IntPtr)parameters, 16, 1);
            // P4：分块实例化。tile 索引走 [[instance_id]]，槽位仍由 buffer(1) 携带。
            encoder.DrawPrimitives(MTLPrimitiveType.Triangle, 0, (nuint)draw.VertexCount, (nuint)draw.InstanceCount);
            drawn++;
        }
        return drawn > 0;
    }

    void ClearPrepared()
    {
        _destination = null;
        _color = null; _depth = null; _refraction = null;
        _colorTexture = null; _depthTexture = null; _refractionTexture = null;
        _skyTexture = null;
        _frame = null; _command = null; _inputs = default;
        _frameIndex = 0;
        // 逐水面的法线表不在这里清：Draw 在 ClearPrepared 之后仍要消费它，
        // 而 Prepare 一定先于 Draw 发生，所以在 Prepare 入口清才是安全的。
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClearPrepared();
        Array.Clear(_drawNormals);
        Array.Clear(_hz);
        // 已编码资源由 command buffer 持有（Device 规则 5），所以立即释放是安全的。
        _pipeline?.Dispose(); _pipeline = null;
        _noDepthState?.Dispose(); _noDepthState = null;
        // 函数是本类持有的，要放掉；库不是 —— 它归 MTLShaderCompiler 的源码缓存所有。
        _vertexFunction?.Dispose(); _vertexFunction = null;
        _fragmentFunction?.Dispose(); _fragmentFunction = null;
    }
}
