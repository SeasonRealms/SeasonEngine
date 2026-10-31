// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

/// <summary>
/// A0.1 end-to-end validation case and the seed of the A6 GPU particle system: a GPU-driven
/// billboard field submitted with one indirect indexed draw.
///
/// Per-frame chain, all of it in the FrameStart phase so nothing touches a render pass:
/// 1. reset kernel, one thread, writes the five indirect arguments with InstanceCount = 0;
/// 2. compaction kernel, one thread per capacity slot, derives a particle position, rejects it
///    against the density gate and the camera frustum, and for survivors claims a slot with an
///    atomic add on InstanceCount and appends a <see cref="ParticleInstance"/>.
/// The instance count the rasterizer consumes is therefore produced entirely on the GPU and never
/// travels back to the CPU. That is the whole point of the exercise: with a CPU-sourced count the
/// cull would have to be a readback and would cost a full frame of latency.
///
/// Motion is stateless by design at this stage. A particle's position is a pure function of its
/// index hash plus one shared normalized phase, so there is no persistent state buffer, no spawn
/// list, no free list and nothing to reset on resize or on camera teleport. The phase is accumulated
/// from delta time rather than computed as elapsed * speed, because the latter makes every speed
/// change jump the phase of the entire field. Phase wraps at 1 and the shader only ever uses it
/// inside frac() or a 2*pi sine, so the wrap is exactly continuous.
///
/// Acceleration and lifetime fit inside that stateless model rather than breaking it: the phase is
/// normalized by the lifetime, so a particle's age in seconds is age * lifetime and the acceleration
/// integrates in closed form to 0.5 * a * t^2 on the position and a * t on the velocity. Nothing has
/// to be remembered between frames, which is what keeps rain, snow, smoke and fire sharing one
/// kernel. What this cannot express is anything path-dependent -- collision response, event-driven
/// bursts, trails -- and that is where a real state buffer will have to arrive later.
///
/// Several instances coexist on screen. Each owns its own instance buffer, indirect argument block
/// and backend pipeline, and registers itself into <see cref="FrameSchedule.ParticleSubmitters"/>,
/// so rain, snow and smoke are separate systems rather than modes of one.
/// 每套系统按构造时的 ParticleBudget 分配和派发，Capacity 仅为单系统预算上限；
/// 降低 Density 只减少存活数，不减少候选线程数。
///
/// The shader single source of truth for all stages lives beside this file, one partial per backend
/// language (Particles.Hlsl.cs and friends), including the draw-side vertex and pixel shaders consumed
/// by the backend pipeline, per the engine's shader externalization rule. A backend whose language is
/// absent returns null from CreateComputeKernel / CreateParticleDrawPipeline and registration declines
/// cleanly.
/// </summary>
public sealed partial class ParticleSystemEffect : ComputeEffect, IParticleSubmitter, IDisposable
{
    /// <summary>Upper bound on simultaneously simulated particles. One compaction thread per slot, so this is also the dispatch width.</summary>
    public const uint Capacity = 32768;

    /// <summary>实际分配和派发预算，构造后不可改变；默认保持旧容量。</summary>
    public uint ParticleBudget { get; }

    public ParticleSystemEffect() : this(Capacity) { }

    public ParticleSystemEffect(uint particleBudget)
    {
        if (particleBudget == 0 || particleBudget > Capacity) throw new ArgumentOutOfRangeException(nameof(particleBudget));
        ParticleBudget = particleBudget;
    }

    /// <summary>关闭时不派发也不提交旧间接参数；默认开启以保持旧调用行为。</summary>
    public bool Enabled = true;
    Vector3? _worldOrigin;
    /// <summary>非空时生成盒固定在世界位置；为空时保持旧的相机跟随行为。</summary>
    public Vector3? WorldOrigin
    {
        get => _worldOrigin;
        set
        {
            if (value is { } origin) ValidateVector(origin, nameof(WorldOrigin));
            _worldOrigin = value;
        }
    }
    /// <summary>世界锚定的周期场在相机周围选取窗口；只搬运边缘粒子，不平移整个场。</summary>
    public bool CameraWindow;
    float _viewDistance;
    /// <summary>近景可见距离（米）；最后四分之一距离平滑淡出。0 保持原来的无限距离。</summary>
    public float ViewDistance
    {
        get => _viewDistance;
        set
        {
            if (!float.IsFinite(value) || value < 0) throw new ArgumentOutOfRangeException(nameof(ViewDistance));
            _viewDistance = value;
        }
    }
    /// <summary>密度门槛前的 0.02 区间渐显，避免天气过渡时整颗粒子突然出现；默认关闭。</summary>
    public bool SoftDensity;
    Vector3 _windVelocity;
    /// <summary>恒定漂移速度（米/秒），不同于随年龄积分的加速度。</summary>
    public Vector3 WindVelocity
    {
        get => _windVelocity;
        set { ValidateVector(value, nameof(WindVelocity)); _windVelocity = value; }
    }

    static void ValidateVector(Vector3 value, string name)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
            throw new ArgumentOutOfRangeException(name, "粒子空间参数必须有限。");
    }
    /// <summary>独立种子，仅低 24 位参与，保证四端 float 常量可精确传递。</summary>
    public uint Seed;
    /// <summary>区域约束，null 表示无约束；空场表示无降水。</summary>
    public WeatherField? Weather;
    /// <summary>区域场的降水通道：1=雨，2=雪。</summary>
    public int WeatherChannel = 1;
    /// <summary>启用外部帧时间，未 Advance 时冻结；不会重复消费上一帧的时间。</summary>
    public bool ExternalTime;
    float _pendingDelta;

    public void Advance(float seconds)
    {
        if (!float.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        _pendingDelta = MathF.Min(0.1f, _pendingDelta + seconds);
    }

    const uint CompactGroupSize = 64;

    readonly Vector4[] _frame = new Vector4[ParticleDraw.FrameVectorCount];
    readonly ComputeResourceRef[] _resetResources = new ComputeResourceRef[1];
    readonly ComputeResourceRef[] _compactResources = new ComputeResourceRef[4];
    readonly Vector4[] _regionData = new Vector4[WeatherField.MaxRegions * WeatherField.RegionVectorCount];
    StorageBuffer? _regionBuffer;
    readonly Stopwatch _clock = Stopwatch.StartNew();

    IGraphics? _graphics;
    ComputeKernel? _reset, _compact;
    StorageBuffer? _instances, _args, _frameBuffer;
    ParticleDrawPipeline? _pipeline;
    double _lastSeconds;
    float _phase;
    bool _ready;
    // 是否已进入 FrameSchedule 的提交表。Initialize 可能在加表之前就失败回退，
    // 那时 Dispose 不能去移除一个从未加入的条目。
    bool _routed;

    /// <summary>Fraction of the capacity allowed to survive the density gate, in [0,1]. The rejection happens on the GPU, so lowering it costs no draw calls.</summary>
    public float Density = 1f;

    /// <summary>Half-extent of the horizontal spawn box around the camera, in world units.</summary>
    public float BoxRadius = 28f;

    /// <summary>Vertical span of the fall cycle, in world units. Particles wrap from the bottom back to the top.</summary>
    public float BoxHeight = 30f;

    /// <summary>Fall speed in world units per second.</summary>
    public float FallSpeed = 2.4f;

    /// <summary>Horizontal sway amplitude in world units.</summary>
    public float Sway = 0.35f;

    /// <summary>Billboard half-extent in world units.</summary>
    public float ParticleSize = 0.055f;

    /// <summary>Linear RGB plus peak coverage of a single billboard.</summary>
    public Vector4 Color = new(1.6f, 1.7f, 1.85f, 0.75f);

    /// <summary>
    /// Lifetime of one fall cycle in seconds. Zero means derive it from <see cref="BoxHeight"/> and
    /// <see cref="FallSpeed"/>, which is what rain and snow want; smoke and fire set it explicitly
    /// because their rise speed is governed by <see cref="Acceleration"/> rather than by the box.
    /// A lifetime of zero with a zero fall speed freezes the field, by design.
    /// </summary>
    public float Lifetime;

    /// <summary>
    /// Constant world-space acceleration in units per second squared. Integrated in closed form
    /// against the particle's own age, so it stays a pure function of the index hash: negative Y is
    /// heavier rain, positive Y is rising smoke, and a horizontal component is wind.
    /// </summary>
    public Vector3 Acceleration;

    /// <summary>Fraction of the lifetime spent fading in, in [0,1]. Zero means appear at full coverage.</summary>
    public float FadeIn;

    /// <summary>Fraction of the lifetime spent fading out, in [0,1]. Zero means disappear at full coverage.</summary>
    public float FadeOut;

    /// <summary>
    /// Seconds of travel the billboard is sheared along its velocity, turning the quad into a motion
    /// streak whose total length is |velocity| * this. Zero leaves the quad exactly square, which is
    /// what snow and smoke want; rain sets a small positive value to get falling threads.
    /// </summary>
    public float Stretch;

    /// <summary>
    /// 曲线模式：外观随归一化年龄变化，而不是整个系统共用一个常量。
    /// 打开后压缩阶段把实例记录的 rgb 改写成两枚稳定随机数（顶点阶段据此选两段颜色梯度与初始转角），
    /// 所以 <see cref="Color"/> 的 rgb 变成梯度起点，只有 alpha 仍是覆盖度。
    /// 关掉时两端都走原来的路径，一帧的输出与扩容前逐位相同。
    /// </summary>
    public bool CurveMode;

    /// <summary>出生点落在水平面上均匀分布的圆盘里，而不是方盒里。与曲线模式相互独立。</summary>
    public bool SpawnDisk;

    /// <summary>圆盘半径（米），仅在 <see cref="SpawnDisk"/> 打开时参与压缩。</summary>
    public float SpawnRadius = 1f;

    /// <summary>
    /// 径向速度（米/秒），沿出生点到圆心的连线方向持续外扩：烟柱因此越升越宽，
    /// 火焰用负值让火舌向中轴收拢。与风速一样是恒定速度，不是加速度。
    /// </summary>
    public float Spread;

    /// <summary>
    /// 低频摆动的幅度（米/秒）；两轴各一条不同频率的正弦，位移随年龄线性增长。
    /// 仍然是对年龄的闭式函数，不引入任何跨帧状态。
    /// </summary>
    public float Turbulence;

    /// <summary>寿命结束时的公告板半尺寸（米）；起点仍是 <see cref="ParticleSize"/>。</summary>
    public float SizeEnd;

    /// <summary>寿命中点的颜色，与 <see cref="Color"/>（起点）和 <see cref="ColorEnd"/> 组成两段线性梯度。</summary>
    public Vector3 ColorMid = Vector3.One;

    /// <summary>寿命终点的颜色。</summary>
    public Vector3 ColorEnd = Vector3.One;

    /// <summary>
    /// 加法混合：保留 rgb 而把 alpha 压到 0，在既有的 One/InvSrcAlpha PSO 下就是纯加法。
    /// 火焰与火星用它在亮部堆出饱和的白心，不需要第二套 PSO。
    /// </summary>
    public bool Additive;

    /// <summary>亚像素噪声强度，0 表示平滑的径向衰减；仅在曲线模式下参与着色。</summary>
    public float NoiseAmount;

    public override string Name => "particles";

    public override ComputePhase Phase => ComputePhase.FrameStart;

    public override bool Initialize(IGraphics g)
    {
        if (_graphics != null) throw new InvalidOperationException("粒子系统已经初始化。");
        if (!g.ComputeSupported || !g.IndirectDrawSupported) return false;
        _graphics = g;
        try
        {
            _pipeline = g.CreateParticleDrawPipeline();
            if (_pipeline == null) { Dispose(); return false; }
            _reset = g.CreateComputeKernel(CreateResetKernel());
            _compact = g.CreateComputeKernel(CreateCompactKernel());
            if (_reset == null || _compact == null) { Dispose(); return false; }
            _frameBuffer = g.CreateStorageBuffer(ParticleDraw.FrameBytes);
            _instances = g.CreateStorageBuffer(ParticleBudget * ParticleInstance.SizeInBytes);
            _regionBuffer = g.CreateStorageBuffer((uint)_regionData.Length * 16);
            // 这是全工程第一个间接参数缓冲；usage 必须在创建期声明，之后无法加宽。
            _args = g.CreateStorageBuffer(IndirectDrawIndexedArgs.SizeInBytes,
                StorageBufferUsage.ComputeAndIndirectArgs);
            _resetResources[0] = _args;
            _compactResources[0] = _frameBuffer;
            _compactResources[1] = _instances;
            _compactResources[2] = _args;
            _compactResources[3] = _regionBuffer;
            FrameSchedule.ParticleSubmitters.Add(this);
            _routed = true;
            return true;
        }
        catch (Exception ex)
        {
            Dispose();
            DeviceServices.BaseApp.AddLog(LogType.Error, $"[Particles] 初始化失败：{ex.Message}");
            return false;
        }
    }

    public override void Record(IGraphics g)
    {
        _ready = false;
        if (!ReferenceEquals(g, _graphics)
            || _reset == null || _compact == null || _args == null || _instances == null
            || _frameBuffer == null) return;

        double now = _clock.Elapsed.TotalSeconds;
        // 首帧与长卡顿都会给出异常 dt；钳位后相位推进最多等于一个落差周期，不会出现整场跳帧。
        float delta = ExternalTime ? _pendingDelta : (float)Math.Clamp(now - _lastSeconds, 0, 0.1);
        _pendingDelta = 0;
        _lastSeconds = now;
        if (!Enabled || _regionBuffer == null) return;
        if (Weather != null)
        {
            if (WeatherChannel is not (1 or 2)) throw new InvalidOperationException("天气通道必须为雨或雪。");
            bool active = false;
            foreach (var region in Weather.Regions)
                active |= WeatherChannel == 1 ? region.State.Rain > 0 : region.State.Snow > 0;
            if (!active) return;
            int vectors = Weather.WriteParticleData(_regionData);
            if (!g.TryUpdateStorageBuffer(_regionBuffer, MemoryMarshal.AsBytes(_regionData.AsSpan(0, vectors)))) return;
        }
        float height = MathF.Max(BoxHeight, 0.01f);
        // 寿命是相位的唯一时标：显式 Lifetime 优先，否则由落差与落速反推，两者都缺则相位冻结。
        // 这样 shader 侧能把归一化年龄乘回秒数做闭式积分，而相位本身仍然只是 [0,1) 的纯量。
        float fallSpeed = MathF.Max(FallSpeed, 0);
        float life = Lifetime > 0 ? Lifetime : (fallSpeed > 0 ? height / fallSpeed : 0);
        // 相位按归一化寿命累积并在 1 处回绕；shader 只在 frac() 与 2*pi 正弦内使用它，回绕因此精确连续。
        _phase += life > 0 ? delta / life : 0;
        if (_phase >= 1f) _phase -= MathF.Floor(_phase);

        if (!Matrix4x4.Invert(FrameSchedule.RenderView, out var inverseView)) return;
        var position = inverseView.Translation;
        var right = new Vector3(inverseView.M11, inverseView.M12, inverseView.M13);
        var up = new Vector3(inverseView.M21, inverseView.M22, inverseView.M23);
        if (right.LengthSquared() < 1e-12f || up.LengthSquared() < 1e-12f) return;
        right = Vector3.Normalize(right);
        up = Vector3.Normalize(up);

        // 用未抖动 VP 做剔除与投影：抖动只属于 TAA，粒子位置本身不应随抖动晃动。
        var vp = FrameSchedule.UnjitteredRenderViewProjection;
        _frame[0] = new Vector4(vp.M11, vp.M12, vp.M13, vp.M14);
        _frame[1] = new Vector4(vp.M21, vp.M22, vp.M23, vp.M24);
        _frame[2] = new Vector4(vp.M31, vp.M32, vp.M33, vp.M34);
        _frame[3] = new Vector4(vp.M41, vp.M42, vp.M43, vp.M44);
        _frame[4] = new Vector4(right, 0);
        _frame[5] = new Vector4(up, 0);
        _frame[6] = new Vector4(position, 0);
        // row 7：寿命淡入/淡出、拉伸秒数、近景距离；后两项也参与 compaction。
        _frame[7] = new Vector4(Math.Clamp(FadeIn, 0, 1), Math.Clamp(FadeOut, 0, 1),
            MathF.Max(Stretch, 0), ViewDistance);
        // rows 8..11：外观曲线与出生形状，见 ParticleDraw.FrameVectorCount 的行义表。
        // 默认值下这四行里的标志位全为 0，顶点与片元两端都不会进入新分支。
        _frame[8] = new Vector4(Color.X, Color.Y, Color.Z, Additive ? 1f : 0f);
        _frame[9] = new Vector4(ColorMid.X, ColorMid.Y, ColorMid.Z, Math.Clamp(NoiseAmount, 0f, 1f));
        _frame[10] = new Vector4(ColorEnd.X, ColorEnd.Y, ColorEnd.Z,
            float.IsFinite(SizeEnd) ? SizeEnd : 0f);
        _frame[11] = new Vector4(MathF.Max(SpawnRadius, 0f), MathF.Max(Spread, -100f),
            MathF.Max(Turbulence, 0f), CurveMode ? 1f : 0f);
        if (!g.TryUpdateStorageBuffer(_frameBuffer, MemoryMarshal.AsBytes(_frame.AsSpan()))) return;

        Span<float> reset = stackalloc float[4];
        reset.Clear();
        reset[0] = ParticleDraw.QuadIndexCount;
        g.DispatchCompute(new ComputeDispatchArgs
        {
            Kernel = _reset,
            Params = MemoryMarshal.AsBytes(reset),
            Resources = _resetResources,
            GroupsX = 1,
            GroupsY = 1,
            GroupsZ = 1,
        });

        float size = MathF.Max(ParticleSize, 1e-4f);
        var origin = WorldOrigin ?? position;
        Span<float> p = stackalloc float[24];
        p.Clear();
        p[0] = _phase;
        p[1] = MathF.Max(BoxRadius, 0.01f);
        p[2] = height;
        p[3] = ParticleBudget;
        p[4] = origin.X;
        p[5] = origin.Y;
        p[6] = origin.Z;
        p[7] = size;
        p[8] = Color.X;
        p[9] = Color.Y;
        p[10] = Color.Z;
        p[11] = Color.W;
        p[12] = Math.Clamp(Density, 0, 1);
        p[13] = MathF.Max(Sway, 0);
        // 寿命以秒送进 shader，让 kernel 把 [0,1) 的年龄换算回真实时间做闭式积分；
        // 0 表示相位冻结，shader 会把时间项整条取零，于是位置与速度都退回不扩展前的结果。
        p[14] = life;
        p[15] = Seed & 0x00FFFFFFu;
        p[16] = Acceleration.X;
        p[17] = Acceleration.Y;
        p[18] = Acceleration.Z;
        p[19] = Weather?.Count ?? 0;
        p[20] = WindVelocity.X; p[21] = WindVelocity.Y; p[22] = WindVelocity.Z;
        // 通道低两位：0=无约束，1=雨，2=雪；+4 世界锚定窗口，+8 密度渐显，+16 圆盘出生。
        p[23] = (Weather == null ? 0 : WeatherChannel) + (CameraWindow && WorldOrigin.HasValue ? 4 : 0)
            + (SoftDensity ? 8 : 0) + (SpawnDisk ? 16 : 0);
        g.DispatchCompute(new ComputeDispatchArgs
        {
            Kernel = _compact,
            Params = MemoryMarshal.AsBytes(p),
            Resources = _compactResources,
            GroupsX = (ParticleBudget + CompactGroupSize - 1) / CompactGroupSize,
            GroupsY = 1,
            GroupsZ = 1,
        });
        _ready = true;
    }

    /// <summary>Pass 之外的参数交接，由 FrameSchedule 在开启场景绘制目标前调用；每帧只接受一次。</summary>
    bool IParticleSubmitter.Prepare(IGraphics g, RenderTarget color)
    {
        if (!_ready) return false;
        _ready = false;
        if (!ReferenceEquals(g, _graphics) || _pipeline == null) return false;
        if (!g.TryPrepareIndirectArgs(_args!)) return false;
        return _pipeline.Prepare(new ParticleDrawInputs(color, FrameSchedule.SceneDepth,
            _instances!, _args!, _frameBuffer!));
    }

    void IParticleSubmitter.Submit(IGraphics g)
    {
        if (!ReferenceEquals(g, _graphics)) return;
        _pipeline?.Draw();
    }

    public override void OnUnregistered() => Dispose();

    public void Dispose()
    {
        _ready = false;
        if (_routed)
        {
            FrameSchedule.ParticleSubmitters.Remove(this);
            _routed = false;
        }
        _pipeline?.Dispose();
        _pipeline = null;
        _reset?.Dispose();
        _reset = null;
        _compact?.Dispose();
        _compact = null;
        _instances?.Dispose();
        _instances = null;
        _args?.Dispose();
        _args = null;
        _frameBuffer?.Dispose();
        _frameBuffer = null;
        _regionBuffer?.Dispose();
        _regionBuffer = null;
        _pendingDelta = 0;
        _graphics = null;
    }

    // ── Kernel declarations ──
    // Binding order is the cross-backend slot contract; see the ComputeBindingType summary.
    // reset:   [0] Params 16B -> b0 / [1] StorageBufferReadWrite args -> u0
    // compact: [0] Params 96B -> b0 / [1] StorageBufferRead frame -> t0
    //          [2] StorageBufferReadWrite instances -> u0 / [3] StorageBufferReadWrite args -> u1
    //          [4] StorageBufferRead regions -> t1
    // 96B 低于 Vulkan push constant / WebGPU uniform 的跨端 128B 上限。

    static ComputeKernelDesc CreateResetKernel() => new()
    {
        Name = "particleReset",
        Source = new ShaderSourceSet
        {
            Hlsl = ResetHlsl, Glsl = ResetGlsl, Msl = ResetMsl, Wgsl = ResetWgsl, EntryPoint = "CSMain",
        },
        Bindings = new[]
        {
            new ComputeBindingDesc { Type = ComputeBindingType.Params, SizeInBytes = 16 },
            new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferReadWrite },
        },
        WorkgroupX = 1,
        WorkgroupY = 1,
        WorkgroupZ = 1,
    };

    static ComputeKernelDesc CreateCompactKernel() => new()
    {
        Name = "particleCompact",
        Source = new ShaderSourceSet
        {
            Hlsl = CompactHlsl, Glsl = CompactGlsl, Msl = CompactMsl, Wgsl = CompactWgsl, EntryPoint = "CSMain",
        },
        Bindings = new[]
        {
            new ComputeBindingDesc { Type = ComputeBindingType.Params, SizeInBytes = 96 },
            new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferRead },
            new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferReadWrite },
            new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferReadWrite },
            new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferRead },
        },
        WorkgroupX = CompactGroupSize,
        WorkgroupY = 1,
        WorkgroupZ = 1,
    };

}
