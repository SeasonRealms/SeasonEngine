// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Engine;

/// <summary>
/// Deterministic forensics hook for per-pixel A/B.
///
/// Rationale: A/B is only meaningful when "frame N of two runs is pixel-identical", and frame N of the
/// realtime loop depends on the wall clock — running the same binary twice puts dozens of frames of animation
/// between the two t=3s points. So this pins two things down:
///
/// 1) The simulation step is fixed at 1/60 second. The delta that App.Update receives first passes through
///    <see cref="FrameDelta"/>, so BaseApp.Time advances by the same amount each frame, and the CPU-side state
///    of frame N (animation phase, weather accumulation, TAA jitter index) is identical frame by frame.
/// 2) The frame number is counted from "scene stable", not from process start. Rocks, forests, terrain, and
///    robots all load asynchronously and land on the main thread in batches, so the frame at which each run is
///    fully populated differs; capturing by startup frame number would photograph three scenes of differing
///    completeness, with a difference unrelated to gather. The stability criterion uses the gather dispatch
///    count: unchanged for a full second means no group is being added or removed.
///    This path later gained a layer: on stability the simulation time, frame index, and jitter phase are all
///    reset to zero. But it stops at "states that can be zeroed", and there remains a batch of accumulators in
///    the engine with no external reset entry point; for those, switch to the fixed absolute frame number of
///    <see cref="AbsoluteFrameVariable"/>, where equal frame numbers align them all.
/// 3) The capture happens at a fixed frame number. On reaching the target frame, a one-shot readback request is
///    submitted to Record.CaptureApp, which copies out the backbuffer in the same frame's AfterRender, saves it
///    as a PNG, then exits the process directly — no extra frames slip in between the two runs.
///
/// Both switches go through environment variables and default to fully off: when off, <see cref="FrameDelta"/>
/// returns its input unchanged and <see cref="Tick"/> returns immediately, so the cost to normal operation is
/// one static boolean check per frame.
///
/// Usage (running the same binary three times):
///   SEASON_CULL_INDIRECT=0 SEASON_AB_CAPTURE=a1.png  →  A1
///   SEASON_CULL_INDIRECT=0 SEASON_AB_CAPTURE=a2.png  →  A2
///   SEASON_CULL_INDIRECT=1 SEASON_AB_CAPTURE=b1.png  →  B1
/// A1 and A2 must be bit-identical, otherwise determinism does not hold and the difference between A1 and B1
/// cannot be attributed. This control is not formalism: asynchronous loading, particles, and weather can all
/// make two runs diverge, and none of them has anything to do with how gather dispatches.
/// </summary>
internal static class AbCapture
{
    const string PathVariable = "SEASON_AB_CAPTURE";

    const string FrameVariable = "SEASON_AB_FRAME";

    /// <summary>
    /// Capture timing: when set to a positive number, use "frame N after process start" instead of "frame N
    /// after the scene is stable".
    ///
    /// The difference between the two timings is not precision, but whether everything can be fully aligned.
    /// Counting frames after stability only aligns the few states that can be zeroed (simulation time, frame
    /// index, jitter phase), while the engine still has a batch of states that accumulate by frame from process
    /// start and have no external reset entry point: the particle system's _phase (which even has its own
    /// Stopwatch), TAA's history textures, and the various easing accumulators. The measured difference rate is
    /// strictly positively correlated with the stableAt frame gap between the two runs — 18% at a gap of 2
    /// frames, 60% at a gap of 9. That is the signature of these accumulators, entirely unrelated to which path
    /// gather takes.
    ///
    /// With the fixed absolute frame number, the frame number itself is identical, so every accumulator above
    /// naturally lands on the same state, and nothing needs or should be zeroed (zeroing itself depends on the
    /// "moment of stability", which would actually stagger the runs). The only cost is that the capture must
    /// wait for a large enough frame number to ensure all three runs have finished loading the scene; the moment
    /// of stability is still observed and written to the sidecar to verify that this is indeed the case.
    /// </summary>
    const string AbsoluteFrameVariable = "SEASON_AB_ABS_FRAME";

    /// <summary>
    /// Isolation experiment switch: when set to off, the anti-aliasing is lowered to
    /// <see cref="Season.Rendering.AaMode.Off"/> at the moment of stability.
    ///
    /// It addresses "attributing the difference", not "how good it looks". At the default quality, TAA's
    /// history feedback, variance clipping, and jitter phase are all temporal states driven by the frame index:
    /// a single misalignment makes the last-bit difference in HDR get amplified by full-screen post-processing
    /// into a ±1 color step, rendering the percentage difference rate meaningless. Once it is off, the render
    /// chain degenerates into a pure function; if A1/A2 then still fail to zero out, the difference must lie in
    /// the single-frame path and be unrelated to temporal convergence; conversely, it means the single-frame
    /// paths are equivalent and the residual all comes from the temporal domain.
    ///
    /// It modifies EffectiveAntiAliasing rather than AntiAliasing: the former is the runtime value read in each
    /// effect's Record, while the latter is a persisted setting, and changing it would pollute Settings.json.
    /// </summary>
    const string AntiAliasingVariable = "SEASON_AB_AA";

    /// <summary>Fixed step. 60Hz is used only so that "frame number ÷ 60 = simulated seconds" can be done in your head; it is unrelated to the easing constants.</summary>
    const float FixedDelta = 1f / 60f;

    /// <summary>Whether the isolation experiment disables anti-aliasing.</summary>
    static readonly bool SterileAntiAliasing =
        string.Equals(Environment.GetEnvironmentVariable(AntiAliasingVariable), "off",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Isolation experiment switch: when set to off, all particle submitters (rain, snow, smoke, fire, lightning)
    /// are disabled.
    ///
    /// After TAA is turned off, the residual difference concentrates in a large patch in the lower middle of the
    /// image, which is exactly where the particles fall. The particle phase is an accumulation owned by
    /// <see cref="Season.Rendering.Effects.ParticleSystemEffect"/> itself, and the particle systems are only created during
    /// loading: the moment of creation differs between runs, so the number of frames accumulated from then to the
    /// capture frame differs, and the phase is permanently offset from that point on — this kind of offset does
    /// not converge with the frame count, so no amount of waiting will zero it out. The purpose of this switch is
    /// to isolate it and confirm that the residual difference indeed all comes from it.
    /// </summary>
    const string ParticlesVariable = "SEASON_AB_PARTICLES";

    /// <summary>Whether the isolation experiment disables particles.</summary>
    static readonly bool SterileParticles =
        string.Equals(Environment.GetEnvironmentVariable(ParticlesVariable), "off",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Isolation experiment switch: when set to off, the sampling throttle of instance animation is disabled
    /// (<see cref="Season.Rendering.RenderQuality.InstanceAnimationLod"/>).
    ///
    /// It addresses "who is actually to blame for the cross-path difference". The throttle uses
    /// (frame number + instance index) % period to decide whether this frame is sampled, and this frame number is
    /// counted from process start, so the moment of landing differs between runs and the phase at the capture
    /// frame differs: one path (CPU) may be on a "hold" frame, showing a pose up to three frames old, while the
    /// other (GPU) truly evaluates every frame. The gap between them is not a defect but evidence that the
    /// throttle is working correctly — yet in per-pixel statistics it looks exactly like "the evaluation is
    /// computed wrong", both appearing as a large region of the robot body differing.
    ///
    /// After it is off, every instance is evaluated every frame, and both paths use the same playbackTime
    /// sequence, leaving only the real evaluation difference (plus the 60Hz resampling itself) as residual.
    /// </summary>
    const string AnimationLodVariable = "SEASON_AB_LOD";

    /// <summary>Whether the isolation experiment disables the instance animation throttle.</summary>
    static readonly bool SterileAnimationLod =
        string.Equals(Environment.GetEnvironmentVariable(AnimationLodVariable), "off",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Full path of the output PNG; empty means the hook is off.</summary>
    static readonly string OutputPath = Environment.GetEnvironmentVariable(PathVariable);

    /// <summary>
    /// Capture frame number, default 400 frames ≈ 6.7 seconds of simulation time. It is counted from the moment
    /// "the scene is stable", so there is no need to set a very large value to wait for loading; keeping it this
    /// long is only to let slow-changing systems like fog and weather move far enough from their initial state.
    /// </summary>
    static readonly int TargetFrame =
        int.TryParse(Environment.GetEnvironmentVariable(FrameVariable), out int f) && f > 0 ? f : 400;

    /// <summary>Which frame after process start to capture; 0 means still use the old "count frames after stability" timing.</summary>
    static readonly long AbsoluteFrame =
        long.TryParse(Environment.GetEnvironmentVariable(AbsoluteFrameVariable), out long a) && a > 0 ? a : 0;

    /// <summary>How many frames before capture to zero the particle phase. At 60fps, 400 frames ≈ 6.7 seconds, long enough for the particles to spread out.</summary>
    const long ParticleWarmup = 400;

    /// <summary>Frame number at which the particle phase is zeroed. It is an absolute frame number like the capture frame number, so all three runs necessarily land on the same point.</summary>
    static readonly long ParticleResetFrame =
        AbsoluteFrame > ParticleWarmup ? AbsoluteFrame - ParticleWarmup : 1;

    /// <summary>
    /// The number of consecutive frames required to declare "loading phase over": during this span the gather
    /// dispatch count must be exactly identical every frame and sufficiently large. 60 frames is not enough —
    /// early in loading only one or two groups may have instances, so a full second of 1s satisfies the criterion
    /// on the spot while loading is still far from done (this is exactly what was measured: after stability, the
    /// cumulative dispatch count of three 400-frame runs still differed by 5%, showing the scene was still
    /// growing). Only 240 frames (4 seconds) with at least <see cref="StableMinDispatches"/> per frame is enough
    /// to show that the number of groups has stopped changing.
    /// </summary>
    const int StableFrames = 240;

    /// <summary>Lower bound on the dispatch count per frame. It is 4 because among 20 groups a few are always
    /// invisible for a long time, but "only 1 group per frame" is far from "fully loaded" and cannot count as stable.</summary>
    const long StableMinDispatches = 4;

    /// <summary>
    /// Upper limit on the number of frames waited for stability (about 50 seconds). Without it, if A7 fails the
    /// count is always 0 and stability never holds, so the process would run until the outer script's 240-second
    /// timeout, and a timeout is a failure that "says nothing". At the limit it writes a sidecar and exits,
    /// turning "where it got stuck" into readable evidence.
    /// </summary>
    const int StableTimeoutFrames = 3000;

    static int _waited;

    static long _lastCount;

    static long _lastDelta = -1;

    static int _stableFrames;

    static bool _stable;

    static int _frame;

    /// <summary>Cumulative dispatch count at the moment of stability, written to the sidecar to verify that both runs started from the same complexity.</summary>
    static long _stableCount;

    static bool _fired;

    /// <summary>Number of frames elapsed since process start; only used in the fixed absolute frame number mode.</summary>
    static long _absFrame;

    /// <summary>How many particle systems were actually hit when the phase was zeroed; -1 means the reset frame has not been reached yet.</summary>
    static int _particleResets = -1;

    /// <summary>How many single-model animation clocks were pinned in the most recent frame, written to the sidecar to verify that the action was actually hit.</summary>
    static int _pinnedModels;

    internal static bool Enabled => !string.IsNullOrEmpty(OutputPath);

    /// <summary>Replace this frame's delta with the fixed step. Returns it unchanged when disabled, so call sites can write this line unconditionally.</summary>
    internal static float FrameDelta(float time) => Enabled ? FixedDelta : time;

    /// <summary>
    /// Apply the quality changes of the isolation experiment. Called every frame rather than just once: the
    /// particle systems are registered one by one during loading, so setting it only on the first frame would
    /// miss those that arrive later, and missing even one makes the experiment worthless.
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

    /// <summary>Zero the phase of all particle submitters. Lightning does not participate: it has no accumulated phase, and its trigger condition is determined by scene state.</summary>
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
        // Record how many were actually reset: 0 means the particle systems had not been registered when the reset
        // frame arrived, so the phase after that is still wrong.
        _particleResets = reset;

        // The robot animation phase is the same kind of accumulator: it accumulates by the fixed step from the frame
        // the model lands from asynchronous loading, and since the landing frame differs between runs, the pose at
        // capture is permanently offset. In the main-view A/B, all cross-path differences fall on the robots and
        // their shadows, which is this signature. Restarting it at the same fixed frame number as the particles is
        // what separates it from differences in the draw path.
        App.Instance?.robots?.RestartAnimations();
    }

    /// <summary>
    /// Pin each single-model animation's playback clock in the scene to "how many frames have passed since the
    /// reset frame", once per frame.
    ///
    /// It is the same kind of accumulator as the particle and robot phases; the only difference is that it has no
    /// reset entry point: a single model's clock accumulates from the frame that asset lands from asynchronous
    /// loading, and since the landing frame differs between runs, the phase at capture is permanently off by
    /// dozens of frames. What it pollutes is not the edges but the criterion for the whole image — the measured
    /// MorphStressTest (morph deformation, with the wireframe shell following it) differs by 211~292 at world
    /// coordinates x≈540..660 / y≈950..1045, and the self-rotating busterDrone at the doorway differs by 356.
    /// Both take the default CPU route and have nothing to do with the skinning route under test, yet they put a
    /// difference into every cell outside the "rectangle containing the probe", which is how the earlier 2+2 split
    /// of the full-frame A/B came about.
    ///
    /// Re-pinning every frame rather than pinning only once has the same reason as the hold mode of
    /// <c>Robots.UpdateSingleProbe</c>: pinning only once requires the model to already be Ready on the pinning
    /// frame, and the Ready frame number itself is a quantity that differs between runs, so the late-loading ones
    /// would be staggered again. Re-pinning is idempotent, and the phase of frame N is from then on a pure
    /// function of the frame number. It only writes the clock, not the pose, and does not touch IsAnimating —
    /// a stopped model keeps sitting in its own pose.
    /// </summary>
    static void PinModelPhases(long elapsedFrames)
    {
        if (App.Instance == null) return;

        float time = elapsedFrames * FixedDelta;
        _pinnedModels = 0;
        PinModelPhases(App.Instance, time);
    }

    /// <summary>Recursively traverse the panel tree: a panel holds both controls and child panels, and both must be visited, otherwise models hung under a child panel would be missed.</summary>
    static void PinModelPhases(Season.Panels.Panel panel, float time)
    {
        var controls = panel.Controls;
        for (int i = 0; i < controls.Count; i++)
            if (controls[i] is Season.Controls.Model model && model.PinAnimationTime(time)) _pinnedModels++;

        var panels = panel.Panels;
        for (int i = 0; i < panels.Count; i++)
            PinModelPhases(panels[i], time);
    }

    /// <summary>
    /// Observe one frame's dispatch delta: when it is exactly identical for <see cref="StableFrames"/> consecutive
    /// frames and not below the lower bound, the loading phase is considered over. It only advances diagnostic
    /// state (<see cref="_stableFrames"/> and <see cref="_stableCount"/>) and touches no simulation state — the
    /// fixed absolute frame number mode needs to reuse it under the premise of "never zeroing anything", so the
    /// zeroing action stays with the caller.
    /// </summary>
    static bool Observe(long count)
    {
        if (_stable) return true;

        long delta = count - _lastCount;
        _lastCount = count;
        // A delta below the lower bound does not count as stable: that is the early loading phase of "only one or
        // two groups have instances", which also keeps the consecutive-frame delta consistent, yet the scene is
        // clearly far from complete.
        if (delta == _lastDelta && delta >= StableMinDispatches) _stableFrames++;
        else _stableFrames = 0;
        _lastDelta = delta;

        if (_stableFrames < StableFrames) return false;
        _stable = true;
        _stableCount = count;
        return true;
    }

    /// <summary>
    /// Called once per frame. It first uses the gather dispatch count to decide whether the loading phase is over,
    /// then counts frames from the moment of stability; on the target frame it submits the readback request and
    /// falls silent from then on — the request must be made before rendering, because CaptureApp's one-shot TCS is
    /// consumed in the same frame's AfterRender.
    /// </summary>
    internal static void Tick()
    {
        if (!Enabled || _fired) return;

        if (AbsoluteFrame > 0)
        {
            // Fixed absolute frame number mode (for the rationale see the comment on <see cref="AbsoluteFrameVariable"/>):
            // equal frame numbers already put every frame-accumulating accumulator on the same state, so nothing is
            // zeroed here — the starting point of zeroing can only be "the moment of stability", and since the moment
            // of stability inherently differs between runs, that would stagger them again. The stability observation
            // still runs, but it only produces the stableAt in the sidecar, to verify that the scene was indeed fully
            // loaded at the moment of capture.
            //
            // The isolation experiment must take effect here too: it is not an "accumulator" but a quality tier, and
            // the earlier it is set the more consistent it is. Putting it on the first frame instead of waiting for
            // stability also makes the entire history of all three runs follow the same render path.
            ApplySterile();
            Observe(InstanceCull.FixedGatherDispatches + InstanceCull.IndirectGatherDispatches);

            _absFrame++;

            // The particle phase must be zeroed at a fixed frame number, otherwise all of this is meaningless: its
            // starting point is "the particle system being created", and the moment of creation depends on
            // asynchronous loading, so the runs naturally stagger by dozens of frames and then hold that offset
            // forever. The zero point is ParticleWarmup frames before capture, giving the particles time to run
            // through a short stretch from zero phase — choosing 0 would make all the rain and snow overlap at the
            // same position, and that frame's image would not be representative for A/B.
            if (_absFrame == ParticleResetFrame) ResetParticlePhases();

            // The single-model animation clocks are re-pinned frame by frame starting from the reset frame (for the
            // rationale see PinModelPhases). It starts on the same frame as the particle reset, so >= is used here:
            // the phase of the reset frame itself should also be zero, rather than "wherever the last accumulation
            // had gotten to".
            if (_absFrame >= ParticleResetFrame) PinModelPhases(_absFrame - ParticleResetFrame);

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

            // The simulation time must share its origin with the frame number. BaseApp.Time has been accumulating
            // since process start, and since each run's loading time differs, the Time at the moment of stability
            // differs by twenty-odd frames; any effect that reads Time (exposure adaptation, slow fades) would turn
            // this offset into a full-screen ±1 color step and completely drown the real difference. It is a public
            // field, so simply zeroing it works: from then on every run's Time equals "frames after stability / 60".
            DeviceServices.BaseApp.Time = 0f;

            // The frame index must also restart from here: SSR's blue-noise offset uses it directly as a seed, so if
            // the indices differ between runs, "the same scene gets different noise" and the per-pixel comparison
            // never zeroes out.
            Season.Rendering.FrameSchedule.ResetFrameIndex();

            // The jitter phase, like the time history, is advanced from "the frame count since process start", and
            // each run's startup frame count depends on the loading time: the Halton phase at stability therefore
            // differs, and low-frequency regions like the sky differ by several color steps. After zeroing, all runs
            // start from the same phase, and since the screenshot also lands on the same frame after stability, the
            // phase is necessarily identical. Use Camera rather than RenderView: the same camera may be shared by
            // multiple views, so resetting only Current would miss the rest.
            Season.Rendering.RenderView.Current.Camera.ResetTemporal();

            // The isolation experiment comes after all the alignment actions: what it changes is "which quality to use
            // after this frame", unrelated to the earlier phase/time zeroing, but it must land on this moment of
            // stability — changing it earlier would affect the resources and slots determined by the AA tier during
            // initialization.
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

            // Sidecar file rather than AddLog: AddLog only records when in Debug and the LogTypes match, but "which
            // gather dispatch path this run actually took" is a prerequisite for judging whether A/B holds, and must
            // not depend on a channel that might be silently swallowed. The a7 section is an early-exit diagnostic:
            // when the gather count is 0, it lays out the four preconditions one by one, saving a binary search for
            // which one failed.
            WriteSidecar($"frameAfterStable={_frame} absFrame={_absFrame} stableAt={_stableCount} "
                + $"particleResets={_particleResets} pinnedModels={_pinnedModels} "
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
                // slots is the only machine-readable answer to "did the far map join the chain this frame": the far shadow map is a
                // fog-side target with no debug view, so no pixel comparison can tell a culled far map from one that never joined.
                // Local cube maps are absent from it by design (they keep the direct path); local= says whether the frame had one.
                + $"a7=(enabled={InstanceCull.Enabled},targets={InstanceCull.Targets.Count},"
                + $"slots={SlotNames(InstanceCull.ShadowSlotMask)},local={CascadedShadow.LocalActive},"
                + $"sun={CascadedShadow.SunActive},spot={CascadedShadow.SpotActive},reuse={CascadedShadow.ReuseActive},"
                // L1.2: the sub-texel threshold in texels, invariant-formatted because the sidecar is a space-separated key=value
                // line and a culture that writes "1,5" would split one field into two. 0 means the kernel's test is inactive.
                + $"texelCull={InstanceCull.ShadowTexelCull.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}) "
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

            // Exit directly rather than going through window close: the close path runs several more frames of
            // cleanup, and those frames' TAA history and statistics would mix "what happened after the capture frame"
            // into the next run.
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            WriteSidecar($"error={ex}");
            Environment.Exit(3);
        }
    }

    /// <summary>
    /// Names the shadow slots the cull chain wrote this frame, in slot order, so a sidecar can be read without decoding a bitmask.
    /// The far map is the reason this exists: the other slots are already legible through their own consumers, but a far map that
    /// joined the chain and one that stayed out produce the same capture unless the fog happens to show a silhouette.
    /// </summary>
    static string SlotNames(int mask)
    {
        var names = new System.Text.StringBuilder();

        for (int slot = 0; slot < InstanceCull.MaxSlots; slot++)
        {
            if ((mask & (1 << slot)) == 0) continue;
            if (names.Length > 0) names.Append(',');
            names.Append(slot == CascadedShadow.FarSlot ? "far"
                : slot == CascadedShadow.SpotSlot ? "spot"
                : $"c{slot}");
        }

        return names.Length > 0 ? names.ToString() : "none";
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
            // A failure in the capture hook itself must not be thrown again: the process is already in a "about to exit" state.
        }
    }
}
