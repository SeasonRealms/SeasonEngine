// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

/// <summary>
/// 闪电通道 V2 的绘制端：把 <see cref="LightningChannel"/> 的折线喂给粒子 ribbon 管线，一条间接绘制画完整道雷。
///
/// 为什么复用粒子管线而不是新建一条：ribbon 需要的全部能力——每实例数据来自 storage buffer、
/// 实例数来自 GPU 侧的间接参数、预乘 alpha 的透明混合、深度测试开而写关——粒子管线已经具备。
/// 顶点阶段的 ribbon 分支（见 <see cref="ParticleDraw"/> 第 12/13 行的契约）只在帧缓冲的 ribbon 标志
/// 置位时执行，所以粒子路径的每个字节都与扩容前一致，两条路径共用一条 PSO 而不互相干扰。
///
/// 每帧的链路，全部在 FrameStart 相位里、任何 Pass 之外：
/// 1. 落雷边沿：把通道的每一段写成一条 <see cref="ParticleInstance"/> 并上传一次；
/// 2. args kernel（单线程）写入 indexCount=6、instanceCount=段数、其余为 0；
/// 3. Prepare（BeginPass 之前）：按本帧视口写帧缓冲，移交 args 到间接参数状态，解析资源；
/// 4. 同一处（Prepare）：把通道投影成屏幕 AABB 写进 <see cref="ViewTargets.ReactiveRect"/>，
///    TAA 在矩形内关掉历史复用——通道只活一两帧，0.97 的静态反馈会把它整条吃掉。
/// 之后由 <see cref="FrameSchedule.SubmitParticles"/> 在场景透明阶段末尾提交一次。
///
/// args 走 GPU 而不是 CPU 上传：compute-write → indirect-read 这条同步链已被粒子路径在四端验证过，
/// 而 CPU 上传在 Vulkan 上要一道 copy→drawIndirect 的屏障，那条链路从未被验证。
///
/// 亮度分两级：实例的 Color.a 是每段**静态**的权重（顶部在云里渐隐、支路比主通道暗），
/// 帧缓冲 row 13 的强度是**逐帧**的包络（回击的四段衰减）。两者相乘才是最终覆盖度，
/// 于是包络变化不需要重建实例。
/// </summary>
public sealed class LightningEffect : ComputeEffect, IParticleSubmitter, IDisposable
{
    /// <summary>闪电的线性 RGB。冷白略偏蓝：真实通道的核心是白炽的，边缘才偏蓝紫。</summary>
    public Vector3 Tint = new(0.78f, 0.85f, 1.0f);

    /// <summary>包络满值时的覆盖度倍数。闪电是光源而不是表面，亮度必须远超 1 才会在 HDR 里烧成白色。</summary>
    public float Intensity = 3.2f;

    /// <summary>
    /// 雾消光系数（每米）。刻意不用雨幕那条 0.006：那条描述的是"雨滴幕对背景的散射"，
    /// 而闪电是自己的辐射源，它的光在暴雨里能穿透的距离远大于背景可见度。
    /// 取 0.0012 时 500 m 处保留 55%、1500 m 处保留 16%，远处仍看得见一条亮线。
    /// </summary>
    public float FogExtinction = 0.0012f;

    /// <summary>
    /// 最小半宽（像素）。远端通道的世界半宽会算到亚像素，光栅化在那种宽度上时有时无：
    /// 有些帧产生片元、有些帧整段消失，表现为整条通道断续闪烁。
    /// 反应矩形（<see cref="ViewTargets.ReactiveRect"/>）只保护已经画出来的像素不被历史拖住，
    /// 补不上一个根本没产生的片元，所以这一档必须留着。
    /// </summary>
    public float MinHalfWidthPixels = 1.4f;

    /// <summary>关闭时不派发也不提交，用于面板或质量档。</summary>
    public bool Enabled = true;

    readonly ParticleInstance[] _instances = new ParticleInstance[LightningChannel.MaxSegments];
    readonly Vector4[] _frame = new Vector4[ParticleDraw.FrameVectorCount];
    readonly ComputeResourceRef[] _argsResources = new ComputeResourceRef[1];

    IGraphics? _graphics;
    ComputeKernel? _argsKernel;
    StorageBuffer? _instancesBuffer, _args, _frameBuffer;
    ParticleDrawPipeline? _pipeline;
    // 已经上传过实例的落雷序号。同一道雷的几何不会变，所以只在边沿上传一次。
    int _builtSerial = int.MinValue;
    bool _ready;
    bool _routed;
    bool _submitFailed;

    public override string Name => "lightning";

    public override ComputePhase Phase => ComputePhase.FrameStart;

    public override bool Initialize(IGraphics g)
    {
        if (_graphics != null) throw new InvalidOperationException("闪电效果已经初始化。");
        if (!g.ComputeSupported || !g.IndirectDrawSupported) return false;
        _graphics = g;
        try
        {
            _pipeline = g.CreateParticleDrawPipeline();
            if (_pipeline == null) { Dispose(); return false; }
            _argsKernel = g.CreateComputeKernel(ParticleSystemEffect.CreateArgsKernel("lightningArgs"));
            if (_argsKernel == null) { Dispose(); return false; }
            _frameBuffer = g.CreateStorageBuffer(ParticleDraw.FrameBytes);
            _instancesBuffer = g.CreateStorageBuffer(LightningChannel.MaxSegments * ParticleInstance.SizeInBytes);
            // usage 必须在创建期声明：这条缓冲同时被 compute 写、被间接绘制读。
            _args = g.CreateStorageBuffer(IndirectDrawIndexedArgs.SizeInBytes,
                StorageBufferUsage.ComputeAndIndirectArgs);
            if (_frameBuffer == null || _instancesBuffer == null || _args == null) { Dispose(); return false; }
            _argsResources[0] = _args;
            FrameSchedule.ParticleSubmitters.Add(this);
            _routed = true;
            return true;
        }
        catch (Exception ex)
        {
            Dispose();
            DeviceServices.BaseApp.AddLog(LogType.Error, $"[Lightning] 初始化失败：{ex.Message}");
            return false;
        }
    }

    public override void Record(IGraphics g)
    {
        _ready = false;
        if (!Enabled || !ReferenceEquals(g, _graphics) || _argsKernel == null
            || _instancesBuffer == null || _args == null || _frameBuffer == null) return;

        var visuals = FrameSchedule.WeatherVisuals;
        if (visuals == null) return;
        var channel = visuals.Channel;
        if (!channel.Visible) return;

        var flash = visuals.Flash;
        if (!(flash.W > 0)) return;

        // 本帧最亮的那一路未必属于这条通道：两块云各自落雷时 <see cref="WeatherVisualState.Flash"/>
        // 取的是较亮的那一路。坐标对不上就明确不画，而不是把 A 云的通道点亮成 B 云的亮度。
        Vector3 source = new(flash.X, flash.Y, flash.Z);
        if (Vector3.DistanceSquared(source, channel.CloudPoint) > 1e-4f) return;

        int count = channel.Count;
        if (count == 0 || count > _instances.Length) return;

        if (visuals.StrikeSerial != _builtSerial)
        {
            var segments = channel.Segments;
            for (int i = 0; i < count; i++)
            {
                var segment = segments[i];
                _instances[i] = new ParticleInstance
                {
                    // 段中点，配合 Velocity = 半段向量，就是 ribbon 顶点阶段认定的两个端点。
                    Position = (segment.A + segment.B) * 0.5f,
                    Size = segment.HalfWidth,
                    Color = new Vector4(Tint.X, Tint.Y, Tint.Z, segment.Brightness),
                    Velocity = (segment.B - segment.A) * 0.5f,
                    Age = 0f,
                };
            }

            if (!g.TryUpdateStorageBuffer(_instancesBuffer,
                MemoryMarshal.AsBytes(_instances.AsSpan(0, count)))) return;
            _builtSerial = visuals.StrikeSerial;
        }

        // 段数是 CPU 已知的常量，但仍然让 GPU 写参数：这条 compute-write → indirect-read 的链
        // 是粒子路径已经验证过的那一条，CPU 上传在 Vulkan 上还缺一道可见性保证。
        Span<float> args = stackalloc float[4];
        args[0] = ParticleDraw.QuadIndexCount;
        args[1] = count;
        args[2] = 0;
        args[3] = 0;
        g.DispatchCompute(new ComputeDispatchArgs
        {
            Kernel = _argsKernel,
            Params = MemoryMarshal.AsBytes(args),
            Resources = _argsResources,
            GroupsX = 1,
            GroupsY = 1,
            GroupsZ = 1,
        });
        _ready = true;
    }

    /// <summary>
    /// Pass 之外的参数交接。帧缓冲在这里写而不是在 Record 里写，因为最小屏宽需要一个**像素**尺寸，
    /// 而 RenderTarget 只有在 Prepare 才到手；顺带整块帧缓冲只上传一次。
    /// </summary>
    bool IParticleSubmitter.Prepare(IGraphics g, RenderTarget color)
    {
        if (!_ready) return false;
        _ready = false;
        if (!ReferenceEquals(g, _graphics) || _pipeline == null || _frameBuffer == null) return false;
        if (color.Desc.Width == 0 || color.Desc.Height == 0) return false;

        if (!Matrix4x4.Invert(FrameSchedule.RenderView, out var inverseView)) return false;
        var position = inverseView.Translation;
        var right = new Vector3(inverseView.M11, inverseView.M12, inverseView.M13);
        var up = new Vector3(inverseView.M21, inverseView.M22, inverseView.M23);
        if (right.LengthSquared() < 1e-12f || up.LengthSquared() < 1e-12f) return false;
        right = Vector3.Normalize(right);
        up = Vector3.Normalize(up);

        var vp = FrameSchedule.UnjitteredRenderViewProjection;
        _frame[0] = new Vector4(vp.M11, vp.M12, vp.M13, vp.M14);
        _frame[1] = new Vector4(vp.M21, vp.M22, vp.M23, vp.M24);
        _frame[2] = new Vector4(vp.M31, vp.M32, vp.M33, vp.M34);
        _frame[3] = new Vector4(vp.M41, vp.M42, vp.M43, vp.M44);
        _frame[4] = new Vector4(right, 0);
        _frame[5] = new Vector4(up, 0);
        _frame[6] = new Vector4(position, 0);
        // row 7 的寿命淡入淡出与拉伸由公告板路径消费；ribbon 不读，保持零以免有人以为它生效。
        _frame[7] = default;
        _frame[8] = new Vector4(Tint.X, Tint.Y, Tint.Z, 1f);
        _frame[9] = default;
        _frame[10] = default;
        _frame[11] = default;
        // row 12 = 视口宽 / 视口高 / 最小半宽像素 / ribbon 标志。
        _frame[12] = new Vector4(color.Desc.Width, color.Desc.Height, MathF.Max(MinHalfWidthPixels, 0.05f), 1f);
        // row 13 = 强度（包络 × 常量）/ 雾消光系数 / 保留 / 保留。
        float envelope = FrameSchedule.WeatherVisuals?.Flash.W ?? 0f;
        _frame[13] = new Vector4(MathF.Max(Intensity, 0f) * envelope, MathF.Max(FogExtinction, 0f), 0f, 0f);
        _frame[14] = default;
        _frame[15] = default;
        if (!g.TryUpdateStorageBuffer(_frameBuffer, MemoryMarshal.AsBytes(_frame.AsSpan()))) return false;

        if (!g.TryPrepareIndirectArgs(_args!)) return false;
        if (!_pipeline.Prepare(new ParticleDrawInputs(color, FrameSchedule.SceneDepth,
            _instancesBuffer!, _args!, _frameBuffer!))) return false;

        // 绘制真的会发生，才让 TAA 在这一帧的反应矩形里关掉历史复用。放在所有失败路径之后：
        // 没有画出来的帧不该让任何像素失去时间累积。
        PublishReactiveRect(vp, color);
        return true;
    }

    /// <summary>
    /// 把整条通道（主通道 + 全部分叉）投影到归一化屏幕空间，求一个 AABB 交给 TAA。
    ///
    /// 为什么需要：通道只存活 1～3 帧，而 TAA 的静态反馈是 0.97——新出现的亮线第一帧只拿到 3% 权重，
    /// 等历史积到看得见的亮度事件已经结束；反过来它熄灭时又要三十多帧才褪干净。
    /// 矩形内的像素直接不吃历史，通道因此帧帧都是「第一次出现」。
    ///
    /// 为什么是 AABB 而不是逐像素遮罩：CPU 手里只有折线的世界坐标，要精确覆盖得再加一道光栅化 Pass
    /// 和一张全屏附件；而通道的屏幕足迹本来就是一条细折线，包围盒浪费的面积有限。
    /// 代价写在明处：矩形内的背景像素在通道存活的这几帧失去时间累积，会短暂回到逐帧噪声。
    ///
    /// 每段按自己的米制半宽膨胀，而不是整个矩形加一个常数像素：近处的通道半宽可达十几像素，
    /// 只按最小屏宽膨胀会切掉线的两侧，而被切掉的那部分恰好落在 TAA 邻域裁剪的视野里。
    /// </summary>
    void PublishReactiveRect(in Matrix4x4 viewProjection, RenderTarget color)
    {
        var visuals = FrameSchedule.WeatherVisuals;
        if (visuals == null) return;
        var channel = visuals.Channel;
        if (!channel.Visible) return;

        float width = color.Desc.Width;
        float height = color.Desc.Height;
        if (width < 1f || height < 1f) return;

        // 单位方向的世界偏移对 clip.x / clip.y 的影响分别由这两行的三个缩放项决定，取绝对值之和就是上界。
        // 有了它，米制半宽换成 uv 偏移只是一次乘法，不必反解 fov 或 aspect，也不必假设投影是对称的。
        float scaleX = MathF.Abs(viewProjection.M11) + MathF.Abs(viewProjection.M21) + MathF.Abs(viewProjection.M31);
        float scaleY = MathF.Abs(viewProjection.M12) + MathF.Abs(viewProjection.M22) + MathF.Abs(viewProjection.M32);
        // 最小屏宽由顶点阶段兜底；再加 2 像素是 TAA 3x3 邻域半径——邻域里只要有一个像素看见通道，
        // 裁剪就把整带一起拉过去，所以反应区必须比线本身更宽。
        float minHalf = MathF.Max(MinHalfWidthPixels, 0.05f) + 2f;
        float minPadX = minHalf / width;
        float minPadY = minHalf / height;

        float minX = 1f, minY = 1f, maxX = 0f, maxY = 0f;
        bool any = false;

        var segments = channel.Segments;
        for (int i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            Vector4 a = Vector4.Transform(new Vector4(segment.A, 1f), viewProjection);
            Vector4 b = Vector4.Transform(new Vector4(segment.B, 1f), viewProjection);
            // 任一端点落在相机平面之后，包围盒就失去意义（会把通道切掉一半）。此时退化成整屏：
            // 这一帧的 TAA 实际上不再累积，但画面是对的；算一个错的矩形则会留下半条通道的残影。
            if (a.W <= 1e-4f || b.W <= 1e-4f)
            {
                FrameSchedule.ActiveTargets.ReactiveRect = new Vector4(0f, 0f, 1f, 1f);
                return;
            }

            // uv.y 必须翻转：引擎的共享投影约定是 NDC 的 +1 在屏幕顶部（见 Picking.ProjectToScreen 的
            // screenY = (1 - ndcY) * 0.5 * height），而 TAA 的纹理第一行 id.y = 0 也在顶部。
            // 照着 +0.5 写会让反应区上下颠倒——闪电那一带不受保护，屏幕另一侧的天空反而失去历史。
            Vector2 ua = new(a.X / a.W * 0.5f + 0.5f, 0.5f - a.Y / a.W * 0.5f);
            Vector2 ub = new(b.X / b.W * 0.5f + 0.5f, 0.5f - b.Y / b.W * 0.5f);
            // 段中点的视深度。两端取平均已经足够：带子的宽度远小于它到相机的距离。
            float depth = (a.W + b.W) * 0.5f;
            float padX = MathF.Max(segment.HalfWidth * scaleX / depth * 0.5f, minPadX);
            float padY = MathF.Max(segment.HalfWidth * scaleY / depth * 0.5f, minPadY);

            minX = MathF.Min(minX, MathF.Min(ua.X, ub.X) - padX);
            minY = MathF.Min(minY, MathF.Min(ua.Y, ub.Y) - padY);
            maxX = MathF.Max(maxX, MathF.Max(ua.X, ub.X) + padX);
            maxY = MathF.Max(maxY, MathF.Max(ua.Y, ub.Y) + padY);
            any = true;
        }

        if (!any) return;
        minX = Math.Clamp(minX, 0f, 1f);
        minY = Math.Clamp(minY, 0f, 1f);
        maxX = Math.Clamp(maxX, 0f, 1f);
        maxY = Math.Clamp(maxY, 0f, 1f);
        // 整条通道都在视口外时两个分量会被夹到相等；此时留空矩形，让 TAA 走原路径。
        if (minX >= maxX || minY >= maxY) return;

        FrameSchedule.ActiveTargets.ReactiveRect = new Vector4(minX, minY, maxX, maxY);
    }

    bool IParticleSubmitter.Submit(IGraphics g)
    {
        if (!ReferenceEquals(g, _graphics)) return false;
        bool drawn = _pipeline?.Draw() == true;
        if (drawn == _submitFailed)
        {
            _submitFailed = !drawn;
            DeviceServices.BaseApp.AddLog(drawn ? LogType.Backend : LogType.Error, drawn
                ? "[Lightning] 间接绘制恢复提交。"
                : "[Lightning] 间接绘制连续未提交：本帧管线状态与 Prepare 不一致，通道在整帧缺失。");
        }
        return drawn;
    }

    public override void OnUnregistered() => Dispose();

    public void Dispose()
    {
        _ready = false;
        _builtSerial = int.MinValue;
        if (_routed)
        {
            FrameSchedule.ParticleSubmitters.Remove(this);
            _routed = false;
        }
        _pipeline?.Dispose();
        _pipeline = null;
        _argsKernel?.Dispose();
        _argsKernel = null;
        _instancesBuffer?.Dispose();
        _instancesBuffer = null;
        _args?.Dispose();
        _args = null;
        _frameBuffer?.Dispose();
        _frameBuffer = null;
        _graphics = null;
    }
}
