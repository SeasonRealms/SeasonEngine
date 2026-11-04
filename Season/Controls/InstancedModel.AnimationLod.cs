// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Controls;

/// <summary>
/// P1: per-instance animation sampling throttle for <see cref="InstancedModel"/>, plus the pose generation the shadow
/// atlas reuse digest reads instead of the shared animation player's clock.
///
/// The cost being managed: a batch replays its clip once per instance on the CPU, and every instance pays the same price
/// no matter how many pixels it covers. A crowd of a thousand characters spread across a valley spends most of that work on
/// instances that are a few dozen pixels tall, or entirely outside the camera, where a pose sampled every fourth frame is
/// indistinguishable from one sampled every frame.
///
/// Per instance, per frame, in this order:
/// (1) Tier. s = R / (d * tan(fovY/2)) is the instance bounding sphere's radius as a fraction of the view's half-height at
///     its depth, with R the clip-union box's sphere radius scaled by the instance's largest axis scale, and d the view
///     depth of the same center. s >= <see cref="RenderQuality.InstanceAnimationLodFullRateSize"/> samples every frame,
///     s >= <see cref="RenderQuality.InstanceAnimationLodHalfRateSize"/> every second frame, otherwise every fourth. The
///     same sphere against the main frustum answers the shadow-only question: an instance the camera cannot see is pinned
///     to four frames, because only the shadow atlas can show it. An orthographic view replaces the perspective
///     denominator with half the box height.
/// (2) Stagger. An instance is due when (frame + Instances index) % period == 0, so a crowd never lands on one frame.
///     The phase uses the Instances index rather than the compacted slot, so enabling or disabling another instance does
///     not shift every later one onto a different phase.
/// (3) Forced full rate. An instance is sampled regardless of tier when it has never been sampled, when its clip or time
///     offset changed, when it was disabled and re-enabled (or the whole control skipped an update), when the enabled
///     instance count changed, and while it carries a wireframe or outline highlight -- per instance, or batch-wide through
///     the host highlight. The control-level
///     <see cref="AnimationLod"/> switch, or the process-wide <see cref="RenderQuality.InstanceAnimationLod"/> switch,
///     turns the tiering off entirely and samples everything every frame.
/// (4) Static hold. With a zero frame delta or a zero playback speed nothing about the pose can change, so a sampled
///     instance reuses its cache instead of recomputing the identical pose. A paused world therefore costs no sampling at
///     all after the first frame, which is also what lets the shadow atlas stay reused across it.
///
/// Playback time still advances every frame for every instance, and a sampled instance is evaluated at that current time,
/// so throttling changes only how often a pose is refreshed, never the phase of the animation. Everything the pose feeds --
/// node worlds, bone palette, morph weights -- is cached per instance by <see cref="Season.Models.InstancePoseCache"/>, and
/// the instance transform is applied on top every frame, so movement is never throttled.
///
/// P2 sits in front of this scheduler: an instance the main camera cannot reach skips the call entirely, which leaves its
/// row's <c>Frame</c> stale and therefore forces a sample on the first frame it comes back (see
/// <see cref="InstancedModel.AnimationCull"/>). P3a sits behind it: a sampled instance whose tier is two or more frames
/// shares one evaluation per (clip, quantized playback time) pair within the frame, copying the cached rows of the instance
/// that already replayed it.
///
/// The shadow key. The reuse digest used to fold <see cref="Season.Models.GltfAsset.ShadowPoseKey"/>, which is the shared
/// player's clock -- that clock now belongs to whichever instance was evaluated last, and after staggering there is no
/// reliable "last". Backends publish <see cref="AnimationPoseGeneration"/> instead: it increments exactly on frames where at
/// least one instance was evaluated, which is precisely when the set of rendered poses can differ.
/// </summary>
public partial class InstancedModel
{
    /// <summary>
    /// P1: per-control switch for the animation sampling throttle. Default true, which is what a crowd wants; a hero unit that
    /// must never show a pose older than one frame sets this to false and pays full sampling cost. Either this switch or
    /// <see cref="RenderQuality.InstanceAnimationLod"/> being off drops both the size tiers and the stagger, so every instance
    /// samples on every frame; the static hold still applies, because a stopped clock cannot produce a different pose.
    /// </summary>
    public bool AnimationLod { get; set; } = true;

    /// <summary>
    /// P1: pose generation published by the backend. It increments on every frame in which at least one instance was actually
    /// evaluated; frames that only reuse cached poses leave it unchanged. <see cref="ShadowPoseKey"/> reports this value, so the
    /// shadow atlas can be reused across throttled frames and is redrawn as soon as any pose moved. Read-only to callers.
    /// </summary>
    public int AnimationPoseGeneration { get; internal set; }

    /// <summary>Tier for an instance the main camera cannot see but the shadow atlas can; see the class header.</summary>
    const int ShadowOnlySamplePeriod = 4;

    struct AnimationLodState
    {
        /// <summary>The instance this row was last recorded for, so a replaced or reordered entry is re-sampled.</summary>
        public MeshInstanceTransform Instance;

        /// <summary>True once a pose has been sampled into the cache; false means there is nothing to reuse yet.</summary>
        public bool Sampled;

        /// <summary>Last frame index this instance was seen enabled, for the "just enabled" test.</summary>
        public int Frame;

        public int Clip;

        public float TimeOffset;

        /// <summary>P1: last computed tier for this instance (1, 2 or 4); P3a reads it to decide whether the instance
        /// takes part in the quantized dedup at all.</summary>
        public int Period;

        /// <summary>Whether the instance was highlighted on the frame the row was last written. A highlighted instance is
        /// shown to the user, so P3a never lets it reuse another instance's sample and never makes it the source either.</summary>
        public bool Highlighted;
    }

    AnimationLodState[] _animationLodStates = Array.Empty<AnimationLodState>();
    int _animationLodFrame = -1;
    int _animationLodEnabledCount = -1;
    int _animationLodInstanceCount;
    bool _animationLodCountChanged;
    float _animationLodDeltaTime;

    /// <summary>P3a: playback time is quantized to this rate before it becomes a dedup key. Only instances at LOD tier 2 or
    /// slower take part, so a 30 Hz grid is never coarser than the tier that asked for it (tier 2 refreshes at half the frame
    /// rate). Two instances whose sampled times fall inside the same tick are drawn with the same pose -- which they would be,
    /// up to one tick, even without dedup.</summary>
    const float PoseDedupTickRate = 30f;

    /// <summary>P3a: this frame's evaluated (clip, tick) pairs, keyed to the Instances index that evaluated them.</summary>
    readonly Dictionary<(int Clip, long Tick), int> _animationPoseDedup = new();

    /// <summary>
    /// Starts one throttling frame, called once per submitted update immediately before the backend replays the batch.
    /// <paramref name="enabledCount"/> is what makes an enable/disable change force a full-rate frame; <paramref name="deltaTime"/>
    /// is the same frame delta the backend accumulates playback time with, and its zero case is the static hold.
    /// </summary>
    void BeginAnimationLodFrame(int enabledCount, float deltaTime)
    {
        _animationLodFrame++;
        _animationLodCountChanged = enabledCount != _animationLodEnabledCount;
        _animationLodEnabledCount = enabledCount;
        _animationLodDeltaTime = deltaTime;
        // P3a: the dedup table lives for exactly one replay. A key is only comparable between instances replayed in the same
        // frame, because the backend walks Instances in order and every instance's playback clock has already advanced.
        _animationPoseDedup.Clear();

        if (_animationLodStates.Length < Instances.Count)
            Array.Resize(ref _animationLodStates, Instances.Count);
        else if (Instances.Count < _animationLodInstanceCount)
        {
            // The array only ever grows, so a shorter list leaves rows behind. Cleared rows release removed instances
            // (the state holds a reference for the identity check) and make a re-grown index sample from scratch. The
            // previous length is compared rather than the array's, so this runs once per shrink instead of every frame.
            for (int i = Instances.Count; i < _animationLodStates.Length; i++)
                _animationLodStates[i] = default;
        }

        _animationLodInstanceCount = Instances.Count;
    }

    /// <summary>
    /// P1: whether instance <paramref name="index"/> should replay its clip this frame, and, when it should not, that its cached
    /// pose is still valid. <paramref name="clip"/> must be the clip the backend is about to evaluate (already clamped into
    /// range). Called exactly once per enabled instance per frame, in <see cref="InstancedMesh3DBase.Instances"/> order.
    /// </summary>
    internal bool ShouldSampleInstanceAnimation(int index, MeshInstanceTransform instance, int clip)
    {
        ref var state = ref _animationLodStates[index];

        bool stayedEnabled = state.Frame == _animationLodFrame - 1 && ReferenceEquals(state.Instance, instance);
        // A highlighted instance is one the user is being shown, so it never runs on a stale pose. The host-level flags are
        // folded in too: they light the whole batch (the Outline2D mask and the shell pass read them at that level).
        bool highlighted = instance.Highlight.Wireframe || instance.Highlight.Outline
            || Highlight.Wireframe || Highlight.Outline;
        bool force = !state.Sampled
            || !stayedEnabled
            || state.Clip != clip
            || state.TimeOffset != instance.AnimationTimeOffset
            || _animationLodCountChanged
            || highlighted;

        state.Instance = instance;
        state.Frame = _animationLodFrame;
        state.Clip = clip;
        state.TimeOffset = instance.AnimationTimeOffset;

        // The tier is computed here, before any early-out, because P3a reads it back out of the row: dedup has to know whether
        // this instance is at tier 2+ even on a frame where sampling is forced or held. Measuring is cheap next to a replay.
        int period = AnimationSamplePeriod(instance);
        state.Period = period;
        state.Highlighted = highlighted;

        // Static hold: playback time cannot advance, so re-evaluating would recompute the pose already cached. Forced cases
        // fall through, because a clip switch or a seek has to be picked up even with the clock stopped.
        if (!force && (_animationLodDeltaTime <= 0f || instance.AnimationSpeed == 0f))
            return false;

        if (period > 1 && !force && (_animationLodFrame + index) % period != 0)
            return false;

        state.Sampled = true;
        return true;
    }

    /// <summary>
    /// P3a: whether instance <paramref name="index"/> can take its pose from another instance already replayed this frame
    /// instead of replaying its clip. Called only when <see cref="ShouldSampleInstanceAnimation"/> answered true, right before
    /// the backend would evaluate.
    ///
    /// Participation is limited to LOD tier 2 and slower: tier 1 instances sample every frame, and quantizing their evaluated
    /// time to a 30 Hz grid would turn "every frame" into "as if every tick", i.e. it would change what the tier means. At
    /// tier 2+ the instance already accepts a pose that is up to period-1 frames old, so sharing one sample inside a tick is
    /// inside the budget the tier granted. A highlighted instance neither reuses nor sources a sample, because highlighting
    /// means the user is being shown that instance right now.
    ///
    /// On a miss the instance registers itself and the caller must evaluate. On a hit, <paramref name="source"/> is the
    /// Instances index whose rows are this frame's values for the same key, ready to be copied with
    /// <see cref="Season.Models.InstancePoseCache.CopyRow"/>. <paramref name="sampleTime"/> is the time to replay at: the
    /// original time for a non-participant, the tick boundary for a participant, so every instance in a bucket evaluates to
    /// bitwise the same pose no matter which one is the first.
    /// </summary>
    internal bool TryReuseInstancePose(int index, int clip, float playbackTime, out int source, out float sampleTime)
    {
        source = -1;
        sampleTime = playbackTime;

        ref var state = ref _animationLodStates[index];
        if (state.Period < 2 || state.Highlighted || !(playbackTime >= 0f))
            return false;

        long tick = (long)MathF.Round(playbackTime * PoseDedupTickRate);
        sampleTime = tick / PoseDedupTickRate;

        var key = (clip, tick);
        if (_animationPoseDedup.TryGetValue(key, out int registered) && registered != index)
        {
            source = registered;
            return true;
        }

        _animationPoseDedup[key] = index;
        return false;
    }

    /// <summary>
    /// P1: sampling period (1, 2 or 4) for one instance this frame, from its projected size against the current view.
    /// Returns 1 whenever the measurement is unavailable or the feature is off, so a headless host or an unloaded template
    /// degrades to the pre-P1 behavior rather than to the slowest tier.
    /// </summary>
    int AnimationSamplePeriod(MeshInstanceTransform instance)
    {
        if (!AnimationLod || !RenderQuality.Current.InstanceAnimationLod)
            return 1;

        var view = RenderView.CurrentOrNull;
        if (view == null)
            return 1;

        var bounds = TemplateCullBounds;
        float radius = bounds.SphereRadius;
        if (!(radius > 0f) || TemplateLocalSize == Vector3.Zero)
            return 1;

        var scale = InstanceComputedScale(instance);
        float radiusFactor = MathF.Max(MathF.Abs(scale.X), MathF.Max(MathF.Abs(scale.Y), MathF.Abs(scale.Z)));
        float worldRadius = radius * radiusFactor;
        if (!(worldRadius > 0f))
            return 1;

        var center = Vector3.Transform((bounds.Center - TemplateAnchorLocal) * scale, instance.Rotation)
                   + new Vector3(instance.PosX, instance.PosY, instance.PosZ);

        // Only the shadow atlas can show an instance the main camera does not contain; the camera side of its cost is zero,
        // so it takes the slowest tier regardless of the thresholds.
        if (!view.Frustum.IntersectsSphere(in center, worldRadius))
            return ShadowOnlySamplePeriod;

        float halfHeightAtDepth;
        if (view.Camera.Mode == CameraProjectionMode.Orthographic)
        {
            halfHeightAtDepth = view.Camera.OrthographicHeight * 0.5f;
            if (!(halfHeightAtDepth > 0f))
                return 1;
        }
        else
        {
            float tanHalfFov = MathF.Tan(view.Camera.FovY * 0.5f);
            float depth = view.ViewDepth(center);
            if (!(tanHalfFov > 0f) || !(depth > 0f))
                return ShadowOnlySamplePeriod;

            halfHeightAtDepth = depth * tanHalfFov;
        }

        float projectedSize = worldRadius / halfHeightAtDepth;
        var quality = RenderQuality.Current;
        if (projectedSize >= quality.InstanceAnimationLodFullRateSize)
            return 1;
        if (projectedSize >= quality.InstanceAnimationLodHalfRateSize)
            return 2;
        return 4;
    }
}
