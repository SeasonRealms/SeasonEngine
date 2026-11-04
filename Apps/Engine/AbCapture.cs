// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Engine;

/// <summary>
/// 逐像素 A/B 的确定性取证钩子。
///
/// 存在的理由：A/B 只有在"两次运行的第 N 帧画面完全一致"时才有意义，而实时循环的第 N 帧取决于墙上时钟——
/// 同一份二进制跑两次，两处 t=3s 之间隔着几十帧动画。所以这里把两件事钉死：
///
/// 1) 仿真步长固定为 1/60 秒。App.Update 收到的 delta 先经过 <see cref="FrameDelta"/>，于是 BaseApp.Time
///    每帧推进同样的量，第 N 帧的 CPU 侧状态（动画相位、天气累积、TAA 抖动序号）逐帧相同。
/// 2) 帧号从"场景稳定"起算，而不是从进程启动起算。岩石、森林、山体、机器人都走异步加载 + 主线程分批落地，
///    各轮抵达齐全的帧号不同；若按启动帧号取证，三轮会拍到三种不同完整度的场景，
///    差异与 gather 毫无关系。稳定判据用 gather 派发次数：连续一秒不变即认为没有组在增删。
///    这条路径后来又补了一层：稳定时把仿真时间、帧序号、抖动相位一起归零。但它止步于
///    "能归零的状态"，而引擎里还有一批没有对外重置入口的累积器；此时应改用
///    <see cref="AbsoluteFrameVariable"/> 的固定绝对帧号，帧号相同即可让它们全部对齐。
/// 3) 取证发生在固定帧号。到达目标帧时向 Record.CaptureApp 投一次一次性读回请求，它在同一帧的
///    AfterRender 里把 backbuffer 拷出来，落成 PNG 之后直接退出进程——不会有多余帧混进两轮之间。
///
/// 两个开关都走环境变量，默认完全关闭：关闭时 <see cref="FrameDelta"/> 原样返回、<see cref="Tick"/> 立刻返回，
/// 正常运行付出的代价是每帧一次静态布尔判断。
///
/// 用法（同一份二进制跑三次）：
///   SEASON_CULL_INDIRECT=0 SEASON_AB_CAPTURE=a1.png  →  A1
///   SEASON_CULL_INDIRECT=0 SEASON_AB_CAPTURE=a2.png  →  A2
///   SEASON_CULL_INDIRECT=1 SEASON_AB_CAPTURE=b1.png  →  B1
/// A1 与 A2 必须逐位相同，否则确定性不成立，A1 与 B1 的差异也就无从归因。这条对照不是形式主义：
/// 异步加载、粒子、天气都可能让两次运行跑出不同状态，而它们与 gather 的派发方式毫无关系。
/// </summary>
internal static class AbCapture
{
    const string PathVariable = "SEASON_AB_CAPTURE";

    const string FrameVariable = "SEASON_AB_FRAME";

    /// <summary>
    /// 取证时机：设为正数时改用"进程启动后第 N 帧"，而不是"场景稳定后第 N 帧"。
    ///
    /// 两种时机的差别不是精度，而是能不能彻底对齐。稳定后计帧只对齐了少数几个能归零的状态
    /// （仿真时间、帧序号、抖动相位），而引擎里还有一批从进程启动就按帧累加、没有对外重置入口的
    /// 状态：粒子系统的 _phase（它甚至有自己的 Stopwatch）、TAA 的历史纹理、各类缓动。
    /// 实测差异率与两轮的 stableAt 帧号差严格正相关 —— 差 2 帧时 18%，差 9 帧时 60%。
    /// 那就是这些累积器的签名，与 gather 走哪条路完全无关。
    ///
    /// 改用固定绝对帧号后，帧号本身就相同，上面每一个累积器都自然落在同一状态上，
    /// 不需要、也不应该再归零任何东西（归零本身依赖"稳定时刻"，反而会把各轮错开）。
    /// 唯一的代价是取证要等到一个足够大的帧号，保证三轮的场景都已加载完；
    /// 稳定时刻仍然会被观察并写进侧车，用来核对这件事确实成立。
    /// </summary>
    const string AbsoluteFrameVariable = "SEASON_AB_ABS_FRAME";

    /// <summary>
    /// 隔离实验开关：设为 off 时在稳定时刻把抗锯齿降为 <see cref="Season.Rendering.AaMode.Off"/>。
    ///
    /// 它解决的是"差异归因"而不是"跑得多好看"。默认画质下 TAA 的历史反馈、方差裁剪与抖动相位
    /// 全是帧序号驱动的时域状态：只要有一处没对齐，HDR 里的末位差就会被全屏后处理放大成 ±1 的色阶，
    /// 让百分比的差异率失去意义。关掉它之后渲染链退化成纯函数，这时如果 A1/A2 仍然不归零，
    /// 差异就一定在单帧路径里，与时域收敛无关；反之则说明单帧路径等价，残差全部来自时域。
    ///
    /// 改的是 EffectiveAntiAliasing 而不是 AntiAliasing：前者才是各效果 Record 里读的运行时值，
    /// 后者是持久化设置，改了会污染 Settings.json。
    /// </summary>
    const string AntiAliasingVariable = "SEASON_AB_AA";

    /// <summary>固定步长。取 60Hz 只是为了让"帧号 ÷ 60 = 仿真秒数"心算得出来，与缓动常数无关。</summary>
    const float FixedDelta = 1f / 60f;

    /// <summary>隔离实验是否关闭抗锯齿。</summary>
    static readonly bool SterileAntiAliasing =
        string.Equals(Environment.GetEnvironmentVariable(AntiAliasingVariable), "off",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 隔离实验开关：设为 off 时关闭全部粒子提交者（雨、雪、烟、火、闪电）。
    ///
    /// 关掉 TAA 之后残余的差异集中在画面中下部一大片，那正是粒子的落区。粒子相位是
    /// <see cref="Season.Rendering.Effects.ParticleSystemEffect"/> 自己的累积量，而粒子系统是在加载过程中才被创建的：
    /// 各轮创建的时刻不同，之后到取证帧累积的帧数就不同，相位从此永久错开一个偏移 ——
    /// 这类偏移不会随帧数收敛，所以等再久也归不了零。
    /// 这个开关的用途就是把它单独摘出来，确认残余差异确实全部来自它。
    /// </summary>
    const string ParticlesVariable = "SEASON_AB_PARTICLES";

    /// <summary>隔离实验是否关闭粒子。</summary>
    static readonly bool SterileParticles =
        string.Equals(Environment.GetEnvironmentVariable(ParticlesVariable), "off",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 隔离实验开关：设为 off 时关掉实例动画的采样节流（<see cref="Season.Rendering.RenderQuality.InstanceAnimationLod"/>）。
    ///
    /// 它解决的是"跨路径差异到底归谁"。节流按 (帧号 + 实例下标) % 周期 决定这一帧采不采样，而这个帧号从进程启动起算，
    /// 各轮落地时刻不同，取证帧上的相位就不同：一条路径（CPU）可能正处在"保持"帧，显示的是最多三帧前的姿态，
    /// 另一条（GPU）每帧都真实求值。两者的差值不是缺陷，是节流在正常工作的证据 —— 但它与"求值算错"在
    /// 逐像素统计上长得一模一样，都表现为机器人身体的大片区域不同。
    ///
    /// 关掉之后每个实例每帧都求值，两条路径用的是同一个 playbackTime 序列，残差就只剩真正的求值差异
    /// （以及 60Hz 重采样本身）。
    /// </summary>
    const string AnimationLodVariable = "SEASON_AB_LOD";

    /// <summary>隔离实验是否关闭实例动画节流。</summary>
    static readonly bool SterileAnimationLod =
        string.Equals(Environment.GetEnvironmentVariable(AnimationLodVariable), "off",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>输出 PNG 的完整路径；空表示钩子关闭。</summary>
    static readonly string OutputPath = Environment.GetEnvironmentVariable(PathVariable);

    /// <summary>
    /// 取证帧号，默认 400 帧 ≈ 6.7 秒仿真时间。它从"场景稳定"那一刻起算，所以不必为了等加载
    /// 而把数值调得很大；留这么长只是为了让雾、天气这些慢变系统从初始态走出足够远。
    /// </summary>
    static readonly int TargetFrame =
        int.TryParse(Environment.GetEnvironmentVariable(FrameVariable), out int f) && f > 0 ? f : 400;

    /// <summary>进程启动后第几帧取证；0 表示仍用"稳定后计帧"的旧时机。</summary>
    static readonly long AbsoluteFrame =
        long.TryParse(Environment.GetEnvironmentVariable(AbsoluteFrameVariable), out long a) && a > 0 ? a : 0;

    /// <summary>取证前多少帧把粒子相位归零。60fps 下 400 帧 ≈ 6.7 秒，长到足以让粒子分布开。</summary>
    const long ParticleWarmup = 400;

    /// <summary>粒子相位的归零帧号。与取证帧号同为绝对帧号，所以三轮必然落在同一个点上。</summary>
    static readonly long ParticleResetFrame =
        AbsoluteFrame > ParticleWarmup ? AbsoluteFrame - ParticleWarmup : 1;

    /// <summary>
    /// 判定"加载期结束"所需的连续帧数：这段时间里每帧的 gather 派发次数必须完全相同且足够大。
    /// 60 帧不够 —— 加载早期可能只有一两个组有实例，连续一秒都是 1，判据当场成立而实际远未加载完
    /// （实测就是如此：稳定后 400 帧的三轮累计派发数仍相差 5%，说明场景还在长）。
    /// 240 帧（4 秒）且每帧至少 <see cref="StableMinDispatches"/> 次，才足以说明组数停止了增删。
    /// </summary>
    const int StableFrames = 240;

    /// <summary>每帧派发次数的下限。取 4 是因为 20 个组里总有几个长期不可见，但"每帧只有 1 个组"
    /// 与"加载完毕"差得太远，不能算稳定。</summary>
    const long StableMinDispatches = 4;

    /// <summary>
    /// 等稳定的帧数上限（约 50 秒）。没有它，A7 失效时计数永远是 0、稳定永不成立，
    /// 进程会一直跑到外层脚本的 240 秒超时，而超时是"什么都没说"的失败。
    /// 到点写一份侧车再退出，把"卡在哪一步"变成可读的证据。
    /// </summary>
    const int StableTimeoutFrames = 3000;

    static int _waited;

    static long _lastCount;

    static long _lastDelta = -1;

    static int _stableFrames;

    static bool _stable;

    static int _frame;

    /// <summary>稳定时刻的累计派发数，写进侧车用于核对两轮是否从同一复杂度起步。</summary>
    static long _stableCount;

    static bool _fired;

    /// <summary>进程启动后经过的帧数，只在固定绝对帧号模式下使用。</summary>
    static long _absFrame;

    /// <summary>相位归零时实际命中了几套粒子系统；-1 表示还没到归零帧。</summary>
    static int _particleResets = -1;

    internal static bool Enabled => !string.IsNullOrEmpty(OutputPath);

    /// <summary>把本帧 delta 换成固定步长。未启用时原样返回，调用点因此可以无条件写这一行。</summary>
    internal static float FrameDelta(float time) => Enabled ? FixedDelta : time;

    /// <summary>
    /// 应用隔离实验的画质改动。每帧调用而不是只调一次：粒子系统是在加载过程中才逐个注册进来的，
    /// 只在第一帧设会漏掉此后才到的那些，而漏掉一个就等于实验白做。
    /// </summary>
    static void ApplySterile()
    {
        if (SterileAntiAliasing)
            Season.Rendering.RenderQuality.Current.EffectiveAntiAliasing = Season.Rendering.AaMode.Off;

        // Per frame like the other two: this one is a plain property read by the next Update, so setting it once would be enough,
        // but keeping the three switches in one place is what makes the isolation experiment readable.
        if (SterileAnimationLod)
            Season.Rendering.RenderQuality.Current.InstanceAnimationLod = false;

        if (!SterileParticles) return;
        var submitters = Season.Rendering.FrameSchedule.ParticleSubmitters;
        for (int i = 0; i < submitters.Count; i++)
            if (submitters[i] is Season.Rendering.Effects.ParticleSystemEffect particles) particles.Enabled = false;
    }

    /// <summary>把所有粒子提交者的相位归零。闪电不参与：它没有累积相位，触发条件由场景状态决定。</summary>
    static void ResetParticlePhases()
    {
        var submitters = Season.Rendering.FrameSchedule.ParticleSubmitters;
        int reset = 0;
        for (int i = 0; i < submitters.Count; i++)
            if (submitters[i] is Season.Rendering.Effects.ParticleSystemEffect particles)
            {
                particles.ResetPhase();
                reset++;
            }
        // 记录实际重置了几套：为 0 说明归零帧来临时粒子系统还没注册进来，那之后的相位仍然是错的。
        _particleResets = reset;

        // 机器人的动画相位是同一类累积器：它从模型异步加载落地那一帧起按固定步长累加，各轮落地帧不同，
        // 取证时的姿态就永久错开。主视图 A/B 里跨路径的差异全部落在机器人和它们的影子上，就是这个签名。
        // 与粒子在同一个固定帧号上重启，才能让它与绘制路径的差异分开。
        App.Instance?.robots?.RestartAnimations();
    }

    /// <summary>
    /// 观察一帧的派发增量：连续 <see cref="StableFrames"/> 帧完全相同且不低于下限，就认为加载期结束。
    /// 只推进诊断状态（<see cref="_stableFrames"/> 与 <see cref="_stableCount"/>），不碰任何仿真状态 ——
    /// 固定绝对帧号模式要在"绝不归零"的前提下复用它，所以归零动作留在调用方。
    /// </summary>
    static bool Observe(long count)
    {
        if (_stable) return true;

        long delta = count - _lastCount;
        _lastCount = count;
        // delta 小于下限不算稳定：那是"只有一两个组有实例"的加载早期，
        // 它同样会让连续帧的增量保持一致，但场景显然还远未齐全。
        if (delta == _lastDelta && delta >= StableMinDispatches) _stableFrames++;
        else _stableFrames = 0;
        _lastDelta = delta;

        if (_stableFrames < StableFrames) return false;
        _stable = true;
        _stableCount = count;
        return true;
    }

    /// <summary>
    /// 每帧调用一次。先用 gather 派发次数判断加载期是否结束，再从稳定那一刻起累加帧号；
    /// 到目标帧就投出读回请求并从此静默——请求必须在渲染之前提出，
    /// 因为 CaptureApp 的一次性 TCS 是在同一帧的 AfterRender 里被消费的。
    /// </summary>
    internal static void Tick()
    {
        if (!Enabled || _fired) return;

        if (AbsoluteFrame > 0)
        {
            // 固定绝对帧号模式（理由见 <see cref="AbsoluteFrameVariable"/> 的注释）：
            // 帧号相同已经让每一个按帧累加的累积器落在同一状态上，所以这里不归零任何东西 ——
            // 归零的起点只能是"稳定时刻"，而各轮稳定时刻本来就不同，那会把它们重新错开。
            // 稳定观察仍然要跑，但它只产出侧车里的 stableAt，用来核对取证那一刻场景确实已加载完。
            //
            // 隔离实验必须在这里也生效：它不属于"累积器"，而是画质档位，越早设越一致。
            // 放在第一帧而不是等稳定，也让三轮的整条历史都走同一条渲染路径。
            ApplySterile();
            Observe(InstanceCull.FixedGatherDispatches + InstanceCull.IndirectGatherDispatches);

            _absFrame++;

            // 粒子相位必须在固定帧号上归零，否则这一切都没有意义：它的起点是"粒子系统被创建"，
            // 而创建时机取决于异步加载，各轮天然错开几十帧，之后永久保持这个偏移。
            // 归零点取取证前 ParticleWarmup 帧，给粒子留出从零相位走完一小段的时间 ——
            // 取 0 会让所有雨雪重合在同一位置，那一帧的画面对 A/B 没有代表性。
            if (_absFrame == ParticleResetFrame) ResetParticlePhases();

            if (_absFrame < AbsoluteFrame) return;
            _fired = true;
            _ = CaptureAsync();
            return;
        }

        long count = InstanceCull.FixedGatherDispatches + InstanceCull.IndirectGatherDispatches;

        if (!_stable)
        {
            if (!Observe(count))
            {
                if (++_waited < StableTimeoutFrames) return;
                WriteSidecar($"stable=timeout waited={_waited} count={count} "
                    + $"a7=(enabled={InstanceCull.Enabled},targets={InstanceCull.Targets.Count},"
                    + $"sun={CascadedShadow.SunActive},spot={CascadedShadow.SpotActive},"
                    + $"reuse={CascadedShadow.ReuseActive})");
                Environment.Exit(4);
                return;
            }

            // 仿真时间必须与帧号同源。BaseApp.Time 从进程启动就在累加，而各轮的加载耗时不同，
            // 稳定时刻的 Time 因此相差二十几帧；任何读 Time 的效果（曝光自适应、缓慢渐变）
            // 都会把这个偏移变成全屏 ±1 的色阶差，把真正的差异整个淹没。
            // 它是 public 字段，直接归零即可：从此各轮的 Time 都等于"稳定后的帧数 / 60"。
            DeviceServices.BaseApp.Time = 0f;

            // 帧序号同样要从这里重新起算：SSR 的蓝噪声偏移直接拿它当种子，
            // 各轮序号不同就会"同一场景配不同噪声"，逐像素比对永远不归零。
            Season.Rendering.FrameSchedule.ResetFrameIndex();

            // 抖动相位与时间历史同样是从"进程启动起算的帧数"推进的，而各轮的启动帧数取决于加载耗时：
            // 稳定时的 Halton 相位于是各不相同，天空这类低频区域会差出几个色阶。
            // 归零之后各轮从同一相位起步，截图又落在稳定后的同一帧号，相位必然相同。
            // 走 Camera 而不是 RenderView：同一台相机可能被多个视图共享，只重置 Current 会漏掉其余视图。
            Season.Rendering.RenderView.Current.Camera.ResetTemporal();

            // 隔离实验放在所有对齐动作之后：它改的是"这一帧之后用什么画质"，与前面的相位/时间归零无关，
            // 但必须落在稳定这一刻 —— 更早改会影响初始化时按 AA 层级决定的资源与槽位。
            ApplySterile();
            return;
        }

        if (++_frame < TargetFrame) return;
        _fired = true;
        _ = CaptureAsync();
    }

    static async Task CaptureAsync()
    {
        try
        {
            using var image = await DeviceServices.Record.CaptureApp();
            if (image == null)
            {
                WriteSidecar("capture=null");
                Environment.Exit(2);
                return;
            }

            var bytes = DeviceServices.Image.SaveImage(image, Season.Basic.ImageFormat.Png);
            var directory = System.IO.Path.GetDirectoryName(OutputPath);
            if (!string.IsNullOrEmpty(directory))
                System.IO.Directory.CreateDirectory(directory);
            System.IO.File.WriteAllBytes(OutputPath, bytes);

            // 侧车文件而不是 AddLog：AddLog 只在 Debug 且 LogTypes 命中时记账，而"这一轮到底走了哪条
            // gather 派发路径"是判定 A/B 是否成立的前提，不能依赖可能被静默吞掉的通道。
            // a7 那段是早退诊断：gather 计数为 0 时，它把四个前置条件逐个摊开，省得靠二分法找是哪一条没成立。
            WriteSidecar($"frameAfterStable={_frame} absFrame={_absFrame} stableAt={_stableCount} "
                + $"particleResets={_particleResets} "
                + $"time={DeviceServices.BaseApp.Time:F4} "
                + $"aa={Season.Rendering.RenderQuality.Current.EffectiveAntiAliasing} "
                + $"indirect={(InstanceCullEffect.IndirectGather ? 1 : 0)} "
                + $"indirectDispatches={InstanceCull.IndirectGatherDispatches} "
                + $"fixedDispatches={InstanceCull.FixedGatherDispatches} "
                // The main-view slot is only in the mask when some target asked for the indirect draw path, so this is the cull-side
                // evidence that a b-run built the segment its InstancedMesh3D bodies are meant to draw from.
                + $"mainView={(InstanceCull.MainViewCulled ? 1 : 0)} "
                // mainViewDraws is the draw-side half of that evidence: the slot can be culled while every draw still falls back.
                + $"mainViewDraws={InstanceCull.MainViewDraws} "
                + $"a7=(enabled={InstanceCull.Enabled},targets={InstanceCull.Targets.Count},"
                + $"sun={CascadedShadow.SunActive},spot={CascadedShadow.SpotActive},reuse={CascadedShadow.ReuseActive}) "
                // P1: the same evidence for the skinning route. dispatches is what the FrameStart kernel actually served this frame, so a
                // run that asked for SEASON_INSTANCE_SKINNING=gpu but served 0 is a fallback, not a comparison.
                // P3 L2: pooled is how many shared (clip, tick) groups the frame actually collapsed. A frame that pools nothing is
                // supposed to look exactly like the historical path, so without this number a matching capture proves nothing about
                // the pool code having run.
                + $"skinning=(enabled={Season.Rendering.InstanceSkinning.Enabled},"
                + $"mode={(Season.Rendering.InstanceSkinning.Override?.ToString() ?? "auto")},"
                + $"targets={Season.Rendering.InstanceSkinning.Targets.Count},"
                + $"dispatches={Season.Rendering.InstanceSkinning.DispatchedTargets},"
                + $"pooled={Season.Rendering.InstanceSkinning.PooledGroups}) "
                + $"size={image.Width}x{image.Height}");

            // 直接退出而不是走窗口关闭：关闭路径会再跑若干帧清理，那些帧的 TAA 历史与统计会把
            // "取证帧之后发生了什么"混进下一次运行里。
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            WriteSidecar($"error={ex}");
            Environment.Exit(3);
        }
    }

    static void WriteSidecar(string text)
    {
        try
        {
            if (string.IsNullOrEmpty(OutputPath)) return;
            System.IO.File.WriteAllText(OutputPath + ".txt", text + Environment.NewLine);
        }
        catch
        {
            // 取证钩子本身失败不能再抛出：此时进程已经处于"要退出"的状态。
        }
    }
}
