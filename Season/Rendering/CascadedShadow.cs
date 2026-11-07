// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// 1-5: CPU-side matrix computation for CSM cascades and spot shadow maps, shared across all four backends.
/// See RenderQuality 1-5 clause 9 for the contract.
///
/// Responsibilities: called every frame by the backend frame loop after the camera is updated:
///   BeginFrame() → ComputeSun(camera, sunDir) when directional light is enabled → ComputeSpot(light) when slot 0 is a spot
///   → Apply(ref sceneLights) to write matrices, splits, and enable flags into the lighting UBO mirror.
/// During shadow-pass rendering, each atlas quadrant calls <see cref="BeginSlot"/> to get that slot's view×projection
/// as a replacement for the main-pass camera matrix, while the world matrix remains unchanged, and <see cref="EndPass"/> is called after the slot loop ends.
/// BeginSlot also publishes <see cref="ActiveFrustum"/> so shared-layer controls can cull shadow casters according to contract clause 7.
/// The culling frustum and uploaded matrix must originate from the same call, so reading CascadeViewProj directly instead of going through BeginSlot is forbidden.
///
/// Algorithm notes (zero allocations per frame, no virtual dispatch):
/// - Practical split: lambda-blended log/uniform partitioning over [Near, min(Far, ShadowDistance)];
/// - For each cascade, orthographic projection is built from the bounding sphere of the 8 corners of the frustum slice
///   (a sphere is invariant to camera rotation, so shadow-map coverage stays stable and does not shimmer under rotation);
/// - Texel snapping: align the full light-space translation to the texel grid, with tile resolution = atlas/2, to remove shimmer from camera translation;
/// - Light-direction quantization: snap the sun direction onto a fixed angular grid before deriving anything from it, because the
///   snapping grid above is the light basis and would otherwise rotate along with the sun, leaving snapping quantizing into a
///   moving frame of reference. Together these two make the cascade matrix bitwise stable rather than merely similar;
/// - Move the light-space eye back along the light so caster geometry outside the slice but on the light side, such as room walls,
///   still fits into the depth range. The distance is derived from <see cref="RenderQuality.ShadowCasterHeight"/> and the sun's
///   elevation rather than being a fixed multiple of the radius, so the depth range is not mostly empty space;
/// - Matrix conventions stay aligned with the engine: LH + [0,1] depth + row vectors via the System.Numerics Create*LeftHanded family.
///
/// Atlas quadrant convention (contract clause 2): slot i uses tile origin = ((i%2)·half, (i/2)·half), where half = atlas/2.
/// Slots 0..2 are cascades and slot 3 is the spotlight. The shader maps light-space UV into the corresponding quadrant using the same convention.
/// </summary>
public static class CascadedShadow
{
    /// <summary>Maximum number of CSM cascades. In the four atlas quadrants, three belong to cascades and the fourth belongs to the spotlight.</summary>
    public const int MaxCascades = 3;

    /// <summary>Atlas slot used by the spotlight shadow map.</summary>
    public const int SpotSlot = 3;

    /// <summary>Near clip plane for the spotlight shadow map. This is a perspective projection, so setting it too small harms depth precision.</summary>
    const float SpotNear = 0.05f;

    /// <summary>Fallback far clip when spotlight range&lt;=0, matching the KHR "infinite range" semantic.</summary>
    const float SpotFarFallback = 20f;

    /// <summary>Active cascade light-space ViewProj matrices for the current frame. Preallocated; only the first <see cref="ActiveCascadeCount"/> entries are valid.</summary>
    public static readonly Matrix4x4[] CascadeViewProj = new Matrix4x4[MaxCascades];

    /// <summary>View-space far bounds of the current-frame cascade splits. x/y/z are the far bounds of cascades 0/1/2, and w is the shadow maximum distance.</summary>
    public static Vector4 CascadeSplits;

    /// <summary>Number of active cascades for the current frame, valid after ComputeSun.</summary>
    public static int ActiveCascadeCount;

    /// <summary>Whether directional-light shadows are active this frame. Cleared in BeginFrame and set by ComputeSun.</summary>
    public static bool SunActive;

    /// <summary>Spotlight light-space ViewProj for the current frame, valid after ComputeSpot.</summary>
    public static Matrix4x4 SpotViewProj = Matrix4x4.Identity;

    /// <summary>Whether spotlight shadows are active this frame. Cleared in BeginFrame and set by ComputeSpot.</summary>
    public static bool SpotActive;

    /// <summary>
    /// Light-space ViewProj of the fog's far shadow map for this frame, valid when <see cref="FarActive"/>.
    ///
    /// Unlike the cascades this one is anchored to the medium, not to the camera: the centre is the union of the enabled
    /// fog volumes' world bounds, so walking the camera around leaves the matrix bit-identical and no temporal filter has
    /// to chase a map that moved underneath it. The cascades have to follow the camera because they cover the camera's
    /// frustum; the far map covers the fog, and the fog does not move.
    /// </summary>
    public static Matrix4x4 FarViewProj = Matrix4x4.Identity;

    /// <summary>Whether the fog's far shadow map is valid this frame. Cleared in <see cref="BeginFrame"/> and set by
    /// <see cref="ComputeFar"/> only when a directional light casts into a live fog volume.</summary>
    public static bool FarActive;

    /// <summary>Half extent the far map spans on each axis, in metres. Zero while <see cref="FarActive"/> is false.</summary>
    public static float FarExtent;

    /// <summary>
    /// Generation counter for the shadow pass. It is incremented monotonically by <see cref="BeginFrame"/> and stays constant within a frame.
    /// Backends use it to cache "the caster primitive list for this pass": within one shadow pass, the same list is replayed for all four atlas quadrants
    /// (3 cascades + 1 spotlight), with only the viewport and light-space ViewProj changing per slot.
    /// The primitive set itself is unchanged, so CollectPrimitives does not need to run per quadrant.
    /// Monotonicity is the only contract. Extra increments only cause an extra collection pass and affect performance only, not correctness.
    /// </summary>
    public static int Epoch;

    /// <summary>Atlas slot index used by the fog's far shadow map. It is not a quadrant: the far map has a texture of
    /// its own, so this index only names its diagnostics row and its culling frustum.</summary>
    public const int FarSlot = MaxCascades + 1;

    /// <summary>Total number of shadow slots: the three cascade quadrants, the spotlight quadrant, and
    /// <see cref="FarSlot"/>. Only the first four share the atlas, per contract clause 2.</summary>
    public const int SlotCount = MaxCascades + 2;

    /// <summary>Edge length of the fog's own shadow map. 1024 across a medium-sized fog volume is roughly a decimetre
    /// per texel - coarse next to a cascade, which is the point: this map exists to keep a shaft readable at a distance
    /// the cascades no longer reach, not to resolve the trunk that makes it.</summary>
    public const int FarSize = 1024;

    /// <summary>Depth bias in light-space NDC. The far map's texel is ~0.13 m across a ~260 m depth range, so a surface
    /// leaning away from the light can shift by well over a tenth of a metre between neighbouring texels.</summary>
    public const float FarBias = 0.0015f;

    /// <summary>Smallest half extent the far map is allowed to cover, in metres: 120 m across, which is what a fog
    /// volume of this project's size asks for and what the shafts were tuned against.</summary>
    public const float FarMinExtent = 60f;

    /// <summary>Largest half extent. A scene whose fog volumes are spread across the whole map would otherwise pull the
    /// texel size up until the tree that casts a shaft is smaller than one texel and the shaft disappears into a blur.</summary>
    public const float FarMaxExtent = 200f;

    /// <summary>
    /// Number of local (point-light) shadow slots the fog may sample. Two because that is the count the medium actually
    /// asks for in practice - a fire and the nearest lamp - and because each slot costs six full shadow traversals.
    ///
    /// The slots are selected against the fog volumes, never against the camera, for the same reason
    /// <see cref="ComputeFar"/> centres its map on the medium: a matrix that moves with the eye re-samples every stored
    /// depth at a new phase every frame, and the fog's own history would chase a shadow map that never holds still.
    /// </summary>
    public const int LocalShadowLights = 2;

    /// <summary>Cube faces per local light. The order is +X, -X, +Y, -Y, +Z, -Z and the injection kernel picks a face
    /// by the dominant axis of (froxel - light), so the two sides must keep this order.</summary>
    public const int LocalShadowFaces = 6;

    /// <summary>Edge length of one cube face. 128 across a lamp's whole sphere is a decimetre per texel at 8 m, which is
    /// still finer than the froxel grid the result feeds; the point of the map is the silhouette of the lamp housing and
    /// of whatever stands between it and the mist, not a resolved leaf.</summary>
    public const int LocalShadowFaceSize = 128;

    /// <summary>Faces packed per atlas row. Four keeps the atlas as wide as it is with three rows, which is the shape
    /// most backends like best for a depth attachment.</summary>
    public const int LocalShadowTilesPerRow = 4;

    /// <summary>Width of the local atlas in pixels.</summary>
    public const int LocalShadowWidth = LocalShadowTilesPerRow * LocalShadowFaceSize;

    /// <summary>Height of the local atlas in pixels: every light's six faces, laid out four to a row.</summary>
    public const int LocalShadowHeight = LocalShadowLights * LocalShadowFaces / LocalShadowTilesPerRow * LocalShadowFaceSize;

    /// <summary>
    /// Depth margin for the local faces, measured in texels of the face's own footprint at the sampled distance. Not an
    /// NDC constant like <see cref="FarBias"/>, because a cube face is perspective: one NDC unit of depth is worth
    /// <c>near * far / (far - near) / z^2</c> metres, so a fixed NDC margin buys the opposite of what a margin is for.
    /// The previous 0.003 NDC over a 12 m range was a 0.24 m dead zone at 2 m and a 3.8 m one at 8 m - a lamp behind a
    /// nearby wall still lit the mist through it, while a far silhouette had to be metres in front of the froxel before
    /// its shadow showed at all. In texels the dead zone stays one texel of the face wide at every distance, which is
    /// exactly as coarse as the shadow it is protecting, and it is <see cref="LocalBiasCoefficient"/> that converts it.
    /// </summary>
    public const float LocalShadowBias = 1.5f;

    /// <summary>
    /// Strength of the receiver-plane tilt, 0..1, carried in the meta row's .w. Offset along a surface normal is not
    /// available here: a froxel is a point in the medium and has no normal, and offsetting along the lamp-to-froxel
    /// radius leaves ndc.xy untouched on a cube face - that projection is a ratio of the two smaller axes to the largest,
    /// so moving outward along the largest changes neither. What a normal offset buys a rasterised surface - sample where
    /// the receiver would be at this tap's texel instead of where the occluder happens to be - the kernel gets by fitting
    /// a plane through its eight taps and evaluating that plane at each tap's offset. At 0 each tap is read raw, which is
    /// the plain eight-tap percentage-closer filter.
    /// </summary>
    public const float LocalShadowNormalOffset = 1f;

    /// <summary>Near plane of a local cube face. Perspective, so a smaller value only spends depth precision.</summary>
    const float LocalNear = 0.05f;

    /// <summary>
    /// Converts <see cref="LocalShadowBias"/> from texels of footprint into the per-metre coefficient the injection
    /// kernel divides by clip.w. A face spans two NDC units across <see cref="LocalShadowFaceSize"/> texels whatever the
    /// distance, so one texel is <c>2 * w / faceSize</c> metres wide at w; the projection turns a metre at w into
    /// <c>B / w^2</c> NDC with <c>B = near * far / (far - near)</c>, and the two w factors cancel. What is left depends on
    /// the lamp's range alone, which is why this is a constant per light rather than per froxel: clip.w is the distance
    /// along the face's own axis, so the kernel's division is the whole of the reconstruction.
    /// </summary>
    public static float LocalBiasCoefficient(float range)
    {
        if (!(range > LocalNear)) return 0f; // a lamp nearer than the near plane has no depth range to bias across
        float depthScale = LocalNear * range / (range - LocalNear);
        return depthScale * LocalShadowBias * 2f / LocalShadowFaceSize;
    }

    /// <summary>First diagnostics slot used by a local light; local slots follow the atlas and far slots so their
    /// indices never collide with <see cref="ActiveSlot"/> values a backend passes to <see cref="BeginSlot"/>.
    ///
    /// The value is numerically equal to <see cref="InstanceCull.MainViewSlot"/>. The two numbering spaces overlap here and mean different
    /// things -- a lamp-anchored cube face versus the main camera's frustum -- which is exactly why a shadow draw asks
    /// <see cref="InstanceCull.ShadowSlotActive"/> rather than comparing a slot number against a range: only the mask knows which slots the
    /// cull chain wrote this frame. Local slots are never among them, so a local pass always takes the whole-batch draw.</summary>
    public const int LocalSlotBase = SlotCount;

    /// <summary>Total number of slots any shadow pass may enter, atlas, far and local together. Only used to size the
    /// per-slot diagnostics counters; it deliberately does not feed the web view-slot layout, which is why
    /// <see cref="SlotCount"/> stays what it was.</summary>
    public const int TotalSlotCount = SlotCount + LocalShadowLights;

    /// <summary>Whether at least one local light is being shadowed this frame. Cleared in <see cref="BeginFrame"/> and
    /// set by <see cref="ComputeLocal"/>.</summary>
    public static bool LocalActive;

    /// <summary>Index into <see cref="SceneLightParams.Lights"/> of each local slot, or -1 when the slot is unused.
    /// Indexed by slot; written by <see cref="ComputeLocal"/>.</summary>
    public static readonly int[] LocalLightIndex = CreateLocalLightIndices();

    static int[] CreateLocalLightIndices()
    {
        var indices = new int[LocalShadowLights];
        for (int i = 0; i < indices.Length; i++) indices[i] = -1;
        return indices;
    }

    /// <summary>Light-space ViewProj per local slot per face, indexed <c>slot * LocalShadowFaces + face</c>.
    /// Only the slots whose <see cref="LocalLightIndex"/> is non-negative are valid.</summary>
    public static readonly Matrix4x4[] LocalViewProj = new Matrix4x4[LocalShadowLights * LocalShadowFaces];

    /// <summary>Number of local slots in use this frame, valid after <see cref="ComputeLocal"/>.</summary>
    public static int LocalCount;

    /// <summary>The six cube directions, in the order <see cref="LocalViewProj"/> stores them and the order the injection
    /// kernel's dominant-axis test reproduces.</summary>
    static readonly Vector3[] LocalFaceDirections =
    {
        Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ,
    };

    /// <summary>
    /// Light-space culling frustum for the current quadrant of this pass, written by <see cref="BeginSlot"/> and read-only during DrawShadow traversal.
    /// It is derived from the exact ViewProj uploaded for that quadrant; see <see cref="BeginSlot"/> for the no-drift guarantee.
    /// </summary>
    public static Frustum ActiveFrustum;

    /// <summary>
    /// Current active slot. -1 means execution is outside the shadow pass, in which case culling is always disabled to avoid false rejection.
    /// Reset both by <see cref="BeginFrame"/> and <see cref="EndPass"/>.
    /// </summary>
    public static int ActiveSlot = -1;

    /// <summary>Whether culling is active for the current frame and slot, gated by the global switch and whether execution is inside a shadow-pass slot.</summary>
    public static bool CullingActive
        => ActiveSlot >= 0 && RenderQuality.Current.ShadowCulling;

    /// <summary>Per-slot group-level submitted/culled counters used for diagnostics. Cleared at the start of each frame; index = slot.</summary>
    static readonly int[] _submitted = new int[TotalSlotCount];
    static readonly int[] _culled = new int[TotalSlotCount];

    /// <summary>
    /// 1-5 clause 10: whether every active slot's light-space ViewProj is bit-for-bit unchanged from the previous frame.
    /// Written once per frame by <see cref="Apply"/> and read-only afterwards; false until the first pair of frames exists.
    ///
    /// Why this is exactly equivalent to "the atlas would come out identical": the atlas is the rasterization of a fixed
    /// caster set under these matrices, the depth-only pass reduces to a per-texel minimum and is therefore independent of
    /// submission order, and the shadow PSO is baked once. Same matrices plus same casters therefore give the same image,
    /// not merely a similar one. That makes this flag half of the clause 12 reuse predicate; the caster half is
    /// <see cref="CasterFingerprint"/>.
    ///
    /// Float equality is used rather than a byte comparison, and it is not a shortcut. The only values it treats as equal
    /// while their bits differ are +0 and -0, and a matrix is consumed exclusively by multiply-add, where the two are
    /// interchangeable. In the other direction a NaN anywhere in the matrix compares unequal to itself, so a malformed
    /// frame reports unstable and forces a redraw, which is the safe answer.
    /// </summary>
    public static bool MatricesStable;

    // -- Previous-frame matrix mirror backing MatricesStable. Not the same data as the public arrays: those are
    // overwritten in place by ComputeSun before anything can compare against them. --
    static readonly Matrix4x4[] _prevCascadeViewProj = new Matrix4x4[MaxCascades];
    static Matrix4x4 _prevSpotViewProj;
    static int _prevCascadeCount;
    static bool _prevSunActive;
    static bool _prevSpotActive;

    /// <summary>Whether the mirror above holds a usable previous frame. Cleared whenever shadows were skipped entirely,
    /// because <see cref="FrameSchedule"/> clears the atlas on entry even when the pass body returns immediately, so the
    /// stored matrices no longer describe any surviving atlas content.</summary>
    static bool _prevValid;

    // -- Stability statistics over the current diagnostic window. Only frames with SunActive are folded in; see
    // EvaluateStability for why, and ReportStability for how to read what comes out. --
    static int _stableFrames;
    static int _evaluatedFrames;
    static int _stableRun;
    static int _runSum;
    static int _runCount;

    /// <summary>How many intervals in the current window were longer than <see cref="DiagFrameInterval"/> and so entered the
    /// average clamped to it. Reported alongside the average, because a clamped average is a lower bound, and a mean that
    /// silently understates is the same class of misleading number the clamp exists to prevent.</summary>
    static int _cappedRuns;

    /// <summary>Longest stable interval relevant to the current diagnostic window, reset with the other window counters.
    /// An interval spanning the window boundary carries its full accumulated length into the next window's figure rather than
    /// being truncated, for the same reason <see cref="_stableRun"/> is not reset there: it is one interval, still running.
    /// It is raised both as a run grows and as one closes, so it always bounds every interval this window's average reports;
    /// updating it only on growth let a run that grew in the previous window and ended on this window's first frame enter the
    /// average while the peak still read 1, a contradiction on the face of the line.</summary>
    static int _windowLongestRun;

    /// <summary>Longest stable interval seen since process start, kept across diagnostic windows because the peak is what
    /// tells you whether the angular step is sized for the light's actual speed.</summary>
    static int _longestRun;

    /// <summary>
    /// 1-5 clause 11: the angular quantization step in degrees actually applied this frame, after the ladder in
    /// <see cref="UpdateAngularStep"/> scaled <see cref="RenderQuality.ShadowLightAngleStep"/> by a power of two.
    /// 0 means quantization is bypassed. Read-only outside this class; exposed for diagnostics and control panels.
    /// </summary>
    public static float EffectiveLightAngleStep;

    // -- Angular-speed estimator backing the clause 11 ladder. The direction stored here is the raw one handed to
    // ComputeSun, never the quantized result: differencing quantized directions would only ever see whole cells and could
    // not measure a speed finer than the step it is supposed to choose. --
    static Vector3 _prevRawSunDir;
    static bool _prevRawValid;
    static float _smoothedDeltaDegrees;

    // -- Held ladder position. The rung is kept rather than recomputed from scratch each frame because the hysteresis band
    // and the confirmation count below are both statements about history, and a stateless choice has none. _rungBaseStep
    // records which base the exponent was measured against, since the exponent alone does not describe a step without it. --
    static int _rung;
    static bool _rungValid;
    static int _rungPendingWant;
    static int _rungPending;
    static float _rungBaseStep;

    /// <summary>How many powers of two the step may travel either side of the configured base, giving a 64x range.
    /// Bounded rather than open so a malformed frame time or a light that teleports cannot pick a step large enough to
    /// quantize the sun into a different hemisphere.</summary>
    const int StepLadderRange = 3;

    /// <summary>Smoothing rates for the per-frame angular delta, deliberately asymmetric: the estimate rises quickly and
    /// falls slowly. Rising fast matters because an underestimate means the step is too small for the light's real speed,
    /// which is the churn this whole mechanism exists to prevent. Falling slowly matters because every change of ladder
    /// rung is itself a discontinuity, so a light that briefly slows should not trigger one. The asymmetry is only safe
    /// alongside <see cref="DeltaOutlierRatio"/>: rising fast on an unbounded sample and then falling slowly is precisely
    /// how one stalled frame captures the estimate for hundreds of frames.</summary>
    const float DeltaRiseAlpha = 0.5f;
    const float DeltaFallAlpha = 0.02f;

    /// <summary>Extra distance in log2 units, beyond the half-rung that rounding already gives, that the estimate must travel
    /// before a rung is abandoned without waiting for confirmation. Rounding compares the estimate against a boundary; this
    /// compares it against the rung actually held, which is the whole of what gives the choice a memory.
    ///
    /// 0.25 puts the thresholds at 1.68x either side of a rung centre. Sized against measurement, not taste: a sun observed at
    /// 0.09 deg/frame jittered across a log2 span of 0.105 between diagnostic windows, so the band carries roughly a 5x margin
    /// over the noise it exists to reject. It is deliberately wider than the half-rung rounding uses, which leaves a zone where
    /// two adjacent rungs are both defensible answers for one light speed; <see cref="RungConfirmFrames"/> settles that zone.
    /// Outside the band the disagreement is too large to be noise, so the move is taken at once.</summary>
    const float RungHysteresis = 0.25f;

    /// <summary>How many consecutive frames must agree on the same alternative rung, while the estimate sits inside the band
    /// above, before the change is committed.
    ///
    /// This is what distinguishes an estimate that is drifting from one that is merely straddling a boundary, and the two cannot
    /// be told apart from a single frame. Noise around a boundary flips the rounded answer back and forth, so the count keeps
    /// resetting and nothing is committed; a light whose speed genuinely leans towards the neighbouring rung produces the same
    /// answer every frame and eventually gets it. Counting time since the last change instead would not work: under boundary
    /// noise every change resets that clock, which then permits the next one, and the measured flip rate barely improves.
    ///
    /// The count also repairs a bad acquisition. The smoothed delta needs a handful of frames to reach the light's real speed,
    /// and a rung chosen on the way up can land inside the band of the correct one, where the band alone would hold it for good -
    /// on a clean ramp to 0.09 deg/frame that cost half the step and half the stable interval, permanently.
    ///
    /// 240 frames is about four seconds. It has to exceed the longest run of same-direction noise, which for a boundary straddle
    /// is a few tens of frames, and the cost of erring long is only a step one rung finer than ideal for those seconds.</summary>
    const int RungConfirmFrames = 240;

    /// <summary>How many times the current estimate a single frame's measured delta may exceed before it is capped.
    ///
    /// A frame is not a fixed slice of the day-night cycle. A stalled or hitching frame advances the sun by the whole stall,
    /// and differencing across it reports a per-frame speed the light never had: one load stall produced a 14 deg/frame sample
    /// against a true 0.09, which pinned the step at the coarse end of the ladder for roughly 420 frames because
    /// <see cref="DeltaFallAlpha"/> then needs seconds to unwind it. Capping bounds the damage of any one frame to less than a
    /// rung while still tracking a genuine speed change within a few frames.</summary>
    const float DeltaOutlierRatio = 3f;

    /// <summary>The smoothed per-frame delta below which the measurement is treated as unusable rather than as a slow light,
    /// pinning the ladder to rung 0 instead of letting it run to the fine end.
    ///
    /// The delta is recovered with acos(dot(dir, prevDir)), and acos loses almost all of its precision as the dot approaches
    /// one: near dot = 1-e the result is about sqrt(2e), so a float's last bit of dot - e around 1e-7 - already spreads into
    /// roughly 4.5e-4 rad, which is 0.026 deg. Anything the estimator reports below that is the arccosine's own quantization,
    /// not the light, and it is reported *smaller* than the truth because the dot saturates at exactly one.
    ///
    /// Believing it costs real stability. A stopped or near-stopped sun drives the estimate to zero and the ladder to its
    /// finest rung, which the clause-11 documentation calls free precision on the grounds that a direction which does not
    /// change is stable at any step. That reasoning holds for the direction and fails for the grid: a finer step packs the
    /// angular cell boundaries eight times more densely, so the sun does not have to move to be near one, and any residual
    /// jitter in it then crosses cells that a coarser grid would have absorbed. Rung 0 is the honest answer when the
    /// measurement has run out of resolution - it is the step the base was calibrated for.</summary>
    const float DeltaNoiseFloorDegrees = 0.026f;

    /// <summary>Floor on sin(elevation) when converting a caster height into the light-side depth margin, so a sun at or below
    /// the horizon asks for a bounded margin instead of dividing by zero. 0.05 is about 2.9 deg of elevation, past which the
    /// margin the geometry asks for exceeds a cascade radius anyway and the cap takes over.</summary>
    const float MinElevationSine = 0.05f;

    /// <summary>The band of |sunDir.Y| over which the light basis' reference axis is carried from world up to world forward.
    ///
    /// The axis exists only to disambiguate roll about the light, and any axis not parallel to sunDir does that job, so the
    /// choice is free everywhere except within a few degrees of vertical. What is not free is switching between two choices at
    /// a threshold: world up and world forward are 90 deg apart, so a hard swap rotates the entire light basis by 90 deg in
    /// one frame, which relocates every texel of the cascade and every angular cell boundary with it. Clause 9's whole premise
    /// is that the basis is reproducible frame to frame, and one frame of a quarter turn discards it - a sun tracking through
    /// the threshold would do it twice a day, and a sun parked near it would do it whenever the quantized direction jittered
    /// across.
    ///
    /// Interpolating the axis across a band instead makes the basis a continuous function of the direction, so no frame ever
    /// pays more than the direction actually moved. The band ends at the old threshold and starts far enough below it that the
    /// blended axis is nowhere near parallel to the sun: at the midpoint the sun's vertical component is 0.945, bounding its
    /// forward component to 0.327 against a reference axis whose two components are equal, so the cross product stays well
    /// conditioned throughout. Below the band the result is exactly world up, bit for bit, which is what the sun spends almost
    /// all of its day at.</summary>
    const float UpBlendStart = 0.90f;
    const float UpBlendEnd = 0.99f;

    /// <summary>
    /// 1-5 clause 12: FNV-1a digest of the caster set as it stood during this frame's fingerprint walk, folding every
    /// enabled caster's world bounds, instance transforms and animation pose in traversal order. Together with
    /// <see cref="MatricesStable"/> it forms the reuse predicate evaluated by <see cref="EvaluateReuse"/>.
    ///
    /// Order-sensitive on purpose. Reordering the control tree while keeping the same set cannot change the atlas, because
    /// a depth-only pass reduces to a per-texel minimum, so an order-sensitive digest can only ever report a change that
    /// did not happen. Being wrong in that direction costs one redundant redraw; being wrong in the other direction would
    /// leave a stale shadow on screen, so every ambiguity here is resolved towards redrawing.
    ///
    /// What it deliberately does not cover: geometry replaced in place under unchanged bounds, and vertex-stage motion that
    /// no CPU-side value reflects. Neither exists in this engine today; both would need their own contribution here.
    /// </summary>
    public static ulong CasterFingerprint;

    // -- FNV-1a parameters. Chosen over a hash-code combiner so the digest is reproducible run to run, which is what makes
    // it worth printing when a reuse decision has to be explained after the fact. --
    const ulong FnvOffsetBasis = 14695981039346656037UL;
    const ulong FnvPrime = 1099511628211UL;

    static ulong _fingerprintAccum;
    static ulong _prevCasterFingerprint;
    static bool _prevFingerprintValid;
    static bool _fingerprintWalk;

    // -- Reuse statistics over the current diagnostic window. --
    static int _reuseSkipped;
    static int _reuseEvaluated;

    /// <summary>Print interval in frames for diagnostic summaries. Logging every frame would flood the log, and these group-level counts are stable enough that per-frame output is unnecessary.</summary>
    const int DiagFrameInterval = 60;

    /// <summary>
    /// Clears activation flags at the beginning of the frame, so any slot not recomputed this frame has no shadows, and advances <see cref="Epoch"/>.
    /// Also resets slot state and culling counters, after first reporting the previous frame.
    /// </summary>
    public static void BeginFrame()
    {
        ReportCulling();
        ReportStability();
        ReportReuse();

        Epoch++;
        SunActive = false;
        SpotActive = false;
        FarActive = false;
        FarExtent = 0f;
        LocalActive = false;
        LocalCount = 0;
        for (int i = 0; i < LocalLightIndex.Length; i++)
            LocalLightIndex[i] = -1;

        // Cleared pessimistically rather than left to Apply: when shadows are disabled or no light is active, Apply
        // returns before evaluating, and a stale true from an earlier frame would let clause 12 reuse an atlas that was
        // cleared in the meantime.
        MatricesStable = false;

        ActiveSlot = -1;
        Array.Clear(_submitted);
        Array.Clear(_culled);
    }

    /// <summary>
    /// Enters one atlas quadrant, as defined by contract clause 7: publishes the culling frustum derived from this slot's light-space ViewProj,
    /// and returns that same matrix for backend upload.
    ///
    /// This method is the **only** derivation point for both the culling volume and the rendering matrix.
    /// The backend receives the exact matrix whose planes were extracted, so the two cannot drift structurally.
    /// Constructing another culling volume elsewhere, for example by recomputing a bounding sphere, is forbidden.
    /// Doing so would make CPU culling and GPU clipping disagree, typically showing up as shadow flicker or popping as the camera moves.
    /// </summary>
    public static Matrix4x4 BeginSlot(int slot)
    {
        var vp = slot == SpotSlot ? SpotViewProj : CascadeViewProj[slot];
        ActiveSlot = slot;
        Frustum.FromViewProjection(in vp, out ActiveFrustum);
        return vp;
    }

    /// <summary>
    /// Enters <see cref="FarSlot"/>: publishes the culling frustum derived from <see cref="FarViewProj"/> and returns
    /// that same matrix for the backend to upload. Same no-drift contract as <see cref="BeginSlot"/> - the frustum and the
    /// matrix come from one value, so CPU culling and GPU clipping cannot disagree.
    /// </summary>
    public static Matrix4x4 BeginFarSlot()
    {
        ActiveSlot = FarSlot;
        Frustum.FromViewProjection(in FarViewProj, out ActiveFrustum);
        return FarViewProj;
    }

    /// <summary>
    /// Enters one cube face of a local light: publishes the culling frustum derived from that face's ViewProj and returns
    /// the same matrix for the backend to upload. Same no-drift contract as <see cref="BeginSlot"/>.
    ///
    /// The 90 degree pyramid of a face is what makes the six traversals affordable: a lamp inside a clearing rejects
    /// almost everything on any one face, so the pass pays for six frustum walks and a fraction of six draw lists.
    /// </summary>
    public static Matrix4x4 BeginLocalSlot(int slot, int face)
    {
        var vp = LocalViewProj[slot * LocalShadowFaces + face];
        ActiveSlot = LocalSlotBase + slot;
        Frustum.FromViewProjection(in vp, out ActiveFrustum);
        return vp;
    }

    /// <summary>
    /// Maps a local slot and cube face to its atlas viewport. Tile order is slot-major, four tiles to a row, matching
    /// <see cref="LocalShadowHeight"/>; the injection kernel reproduces the same arithmetic from literals.
    /// </summary>
    public static void GetLocalViewport(int slot, int face, out int x, out int y, out int size)
    {
        int tile = slot * LocalShadowFaces + face;
        size = LocalShadowFaceSize;
        x = tile % LocalShadowTilesPerRow * LocalShadowFaceSize;
        y = tile / LocalShadowTilesPerRow * LocalShadowFaceSize;
    }

    /// <summary>Leaves the shadow pass. Called after the quadrant loop ends; <see cref="BeginFrame"/> also provides a fallback reset.</summary>
    public static void EndPass() => ActiveSlot = -1;

    /// <summary>
    /// Slot-level shadow-caster culling test using control-level world-space AABBs, per contract clause 7.
    ///
    /// Correctness: the depth image of slot c is the rasterization result of geometry transformed by <see cref="CascadeViewProj"/>[c].
    /// Triangles outside clip-space [-w,w]×[-w,w]×[0,w] are clipped by the GPU and cannot write into that tile.
    /// If the AABB does not intersect the frustum, then none of the object's triangles, which are contained by that AABB, can be inside it.
    /// Skipping submission therefore leaves atlas contents bit-identical. This is not a quality tradeoff: A/B results must match pixel for pixel.
    ///
    /// The usual concern that "objects outside the slice but on the light side can still cast shadows into it" does not apply here.
    /// <see cref="ComputeSun"/> already moves the box back along the light direction by the margin a caster of
    /// <see cref="RenderQuality.ShadowCasterHeight"/> needs at the current elevation; any caster farther on the light side has already been clipped by the orthographic near plane for the current setup,
    /// so its shadow is already absent today and this culling introduces no new loss.
    ///
    /// Empty boxes with extents=0, typically while resources are still loading, do not participate in culling, following 1-3 clause 6 to avoid false rejection.
    /// </summary>
    public static bool IsCulled(in Bounds3D worldBounds)
    {
        if (!CullingActive || worldBounds.Extents == Vector3.Zero)
            return false;

        return Register(!ActiveFrustum.Intersects(in worldBounds));
    }

    /// <summary>
    /// Records one group-level culling decision and returns it. This is the only entry point for diagnostic counting.
    /// Used by callers that already evaluated against <see cref="ActiveFrustum"/> themselves,
    /// such as InstancedMesh3DBase doing per-instance sphere rejection, where the test must iterate instances and cannot be moved into this class.
    /// </summary>
    public static bool Register(bool culled)
    {
        if (!CullingActive)
            return false;

        if (culled)
            _culled[ActiveSlot]++;
        else
            _submitted[ActiveSlot]++;

        return culled;
    }

    /// <summary>
    /// Every <see cref="DiagFrameInterval"/> frames, reports previous-frame per-slot group-level submitted/culled counts
    /// in the form x/y = culled/total tested. Nothing is printed if no tests occurred, either because shadows were disabled or because the scene had no casters.
    /// </summary>
    static void ReportCulling()
    {
        if (Epoch % DiagFrameInterval != 0)
            return;

        int culled = 0, total = 0;
        for (int i = 0; i < TotalSlotCount; i++)
        {
            culled += _culled[i];
            total += _culled[i] + _submitted[i];
        }

        if (total == 0)
            return;

        int localCulled = 0, localTotal = 0;
        for (int i = LocalSlotBase; i < TotalSlotCount; i++)
        {
            localCulled += _culled[i];
            localTotal += _culled[i] + _submitted[i];
        }

        DeviceServices.BaseApp?.AddLog(LogType.Backend,
            $"{DateTime.UtcNow} [ShadowCull] " +
            $"c0 {_culled[0]}/{_culled[0] + _submitted[0]}  " +
            $"c1 {_culled[1]}/{_culled[1] + _submitted[1]}  " +
            $"c2 {_culled[2]}/{_culled[2] + _submitted[2]}  " +
            $"spot {_culled[SpotSlot]}/{_culled[SpotSlot] + _submitted[SpotSlot]}  " +
            $"local {localCulled}/{localTotal}  " +
            $"total {culled}/{total} ({(total > 0 ? culled * 100 / total : 0)}% of group submissions skipped)");
    }

    /// <summary>
    /// 1-5 clause 10: compares this frame's active slot matrices against the previous frame, publishes
    /// <see cref="MatricesStable"/>, folds the result into the diagnostic window, and then rotates the mirror.
    ///
    /// Called from <see cref="Apply"/> rather than exposed as its own entry point. Apply is already the single place where
    /// every backend closes out the frame's shadow state - after every Compute call and before any consumer - which is
    /// exactly the window this evaluation has to land in. A separate public method would be a fifth call site for four
    /// backends to forget.
    ///
    /// The cascade count and both active flags participate in the comparison because a change in either reshapes which
    /// quadrants hold meaning, which invalidates the atlas just as thoroughly as a matrix change.
    /// </summary>
    static void EvaluateStability()
    {
        bool stable = _prevValid
            && _prevSunActive == SunActive
            && _prevSpotActive == SpotActive
            && _prevCascadeCount == ActiveCascadeCount;

        if (stable && SunActive)
        {
            for (int i = 0; i < ActiveCascadeCount; i++)
            {
                if (CascadeViewProj[i] != _prevCascadeViewProj[i])
                {
                    stable = false;
                    break;
                }
            }
        }

        if (stable && SpotActive && SpotViewProj != _prevSpotViewProj)
            stable = false;

        MatricesStable = stable;

        // The statistics fold only frames with the sun up, while MatricesStable above stays unconditional. That flag is a
        // clause 12 precondition and a spot-only frame whose matrix held still is genuinely reusable, but the window below
        // exists to size the clause 11 angular step, which steers the sun and nothing else. Counting sun-down frames there
        // scored them as hits - cascade matrices are trivially unchanged when no cascade is being computed - so a single
        // night pulled the reported rate towards 100% while saying nothing at all about the step. With the sun down the
        // window now receives nothing, ReportStability prints no line, and the gap in the log is unambiguous.
        //
        // The pending interval is closed on the way down rather than left dangling: an interval interrupted by sunset
        // belongs to the window where the sun set, and carrying it across the night made it surface hundreds of frames
        // later, in a window it had nothing to do with, large enough to swamp that window's average on its own.
        if (!SunActive)
            CloseStableRun();
        else
        {
            _evaluatedFrames++;
            if (stable)
            {
                _stableFrames++;
                _stableRun++;
                if (_stableRun > _windowLongestRun)
                    _windowLongestRun = _stableRun;
                if (_stableRun > _longestRun)
                    _longestRun = _stableRun;
            }
            else
                CloseStableRun();
        }

        if (SunActive)
        {
            for (int i = 0; i < ActiveCascadeCount; i++)
                _prevCascadeViewProj[i] = CascadeViewProj[i];
        }
        if (SpotActive)
            _prevSpotViewProj = SpotViewProj;

        _prevCascadeCount = ActiveCascadeCount;
        _prevSunActive = SunActive;
        _prevSpotActive = SpotActive;
        _prevValid = true;
    }

    /// <summary>
    /// 1-5 clause 10: folds the pending stable interval into the window average and clears it. A no-op when no interval is
    /// pending, so every path that ends one may call it unconditionally.
    ///
    /// A run of k stable frames means k+1 consecutive frames shared one matrix set; the reported interval adds that first
    /// frame back so the number can be compared directly against the target in clause 11.
    ///
    /// The clamp to <see cref="DiagFrameInterval"/> keeps one outlier from taking over the mean. Without it a single interval
    /// longer than the window dominates a sample of two or three and yields an average larger than the window itself, which
    /// is not a number anything can be tuned against. Intervals that long stay visible through the capped count and through
    /// the all-time peak, so the clamp loses no information; it only stops the mean from claiming to carry some.
    ///
    /// Both peaks are raised from the unclamped length, so they stay the honest record the clamped mean points back to.
    /// </summary>
    static void CloseStableRun()
    {
        if (_stableRun == 0)
            return;

        if (_stableRun > _windowLongestRun)
            _windowLongestRun = _stableRun;
        if (_stableRun > _longestRun)
            _longestRun = _stableRun;

        int interval = _stableRun + 1;
        if (interval > DiagFrameInterval)
        {
            interval = DiagFrameInterval;
            _cappedRuns++;
        }

        _runSum += interval;
        _runCount++;
        _stableRun = 0;
    }

    /// <summary>
    /// 1-5 clause 10: every <see cref="DiagFrameInterval"/> frames, reports how often the cascade matrices held still.
    ///
    /// Only frames with the sun up are counted, so no line appears at all while it is down; see
    /// <see cref="EvaluateStability"/>. A window straddling sunrise reports a denominator smaller than the interval, which is
    /// why <c>hit</c> is printed as a fraction and not only as a percentage.
    ///
    /// How to read it: <c>hit</c> is the fraction of frames whose matrices matched the frame before, which is the upper
    /// bound on what clause 12 can skip. <c>avg</c> is the mean length of a completed stable interval in frames and is the
    /// number clause 11 is steering towards <see cref="RenderQuality.ShadowTargetStableFrames"/>; a trailing <c>capped</c>
    /// count means at least one interval outran the window and entered the mean clamped, making the mean a lower bound.
    /// <c>peak</c> gives the longest interval for this window first and since process start second. A low average with a high
    /// window peak means the light is fine and the camera is what keeps breaking the interval, since translation past one
    /// texel invalidates the snap just as a new angular cell does. A low average with a low window peak but a high all-time
    /// peak only means the scene was quieter earlier, and the figure to compare against the target is the window one.
    ///
    /// The window counters reset here but <c>_stableRun</c> deliberately does not: an interval spanning the window
    /// boundary is still one interval, and truncating it would bias the average downwards exactly when it is longest.
    /// </summary>
    static void ReportStability()
    {
        if (Epoch % DiagFrameInterval != 0 || _evaluatedFrames == 0)
            return;

        float avgRun = _runCount > 0 ? _runSum / (float)_runCount : 0f;

        DeviceServices.BaseApp?.AddLog(LogType.Backend,
            $"{DateTime.UtcNow} [ShadowStable] " +
            $"hit {_stableFrames}/{_evaluatedFrames} ({_stableFrames * 100 / _evaluatedFrames}%)  " +
            $"avg {avgRun:F1} frames over {_runCount} intervals" +
            (_cappedRuns > 0 ? $" ({_cappedRuns} capped)" : "") + "  " +
            $"peak {_windowLongestRun + 1} window / {_longestRun + 1} all-time  " +
            $"step {EffectiveLightAngleStep:F4} deg for {_smoothedDeltaDegrees:F4} deg/frame  " +
            $"target {RenderQuality.Current.ShadowTargetStableFrames}");

        _stableFrames = 0;
        _evaluatedFrames = 0;
        _runSum = 0;
        _runCount = 0;
        _cappedRuns = 0;
        _windowLongestRun = 0;
    }

    /// <summary>
    /// 1-5 clause 12: true while the control tree is being walked purely to build <see cref="CasterFingerprint"/>, with no
    /// pass open and no backend pipeline state set. A control overriding DrawShadow must, in this mode, fold its caster
    /// state through <see cref="MixCaster(float)"/> and return without submitting anything.
    ///
    /// Where the check belongs in an override: after the CastShadows and readiness gating, because a control that would not
    /// draw must not contribute, and before per-cascade culling, which has no meaning here. No slot is active during the
    /// walk, and a caster invisible to one cascade may be exactly the one that matters to another - fingerprinting only what
    /// the current slot can see would miss a distant object's motion on precisely the frames it was going to be skipped.
    /// </summary>
    public static bool AccumulatingCasters => _fingerprintWalk;

    /// <summary>
    /// Whether this frame's shadow map reuses the previous frame's result; written by the caller of
    /// <see cref="EvaluateReuse"/> before the FrameStart phase.
    ///
    /// It exists because of ordering: A7 instance culling runs in the FrameStart phase, earlier than the shadow Pass,
    /// so it can only read this flag during Record. When reuse hits, the entire shadow Pass is skipped, the culling
    /// and gathering outputs have no consumer, and that frame's three dispatches are pure waste. Hoisting the
    /// decision before RecordPhase is feasible because EvaluateReuse only walks the tree once and submits no draw
    /// commands -- see <see cref="AccumulatingCasters"/>.
    ///
    /// Frames that do not call EvaluateReuse (reuse tier off, no shadows, no lights) have the caller explicitly write
    /// back false rather than relying on this property to reset itself, otherwise the flag would linger forever after
    /// a single hit.
    /// </summary>
    public static bool ReuseActive { get; internal set; }

    /// <summary>1-5 clause 12: folds one float into the running caster digest, by its bit pattern rather than its value.
    /// Bits rather than value because +0 and -0 must be distinguished here: they are interchangeable inside a matrix, but a
    /// coordinate that flipped sign is a coordinate that was written by different code, and treating that as "no change" is
    /// the one class of mistake this digest cannot afford.</summary>
    public static void MixCaster(float value) => MixCaster(BitConverter.SingleToInt32Bits(value));

    /// <summary>1-5 clause 12: folds one 32-bit value into the running caster digest, byte by byte per FNV-1a.
    /// A no-op outside the fingerprint walk, so a control may call it unconditionally.</summary>
    public static void MixCaster(int value)
    {
        if (!_fingerprintWalk)
            return;

        ulong h = _fingerprintAccum;
        for (int i = 0; i < 4; i++)
        {
            h ^= (byte)(value >> (i * 8));
            h *= FnvPrime;
        }
        _fingerprintAccum = h;
    }

    /// <summary>1-5 clause 12: folds a vector into the running caster digest.</summary>
    public static void MixCaster(in Vector3 value)
    {
        MixCaster(value.X);
        MixCaster(value.Y);
        MixCaster(value.Z);
    }

    /// <summary>1-5 clause 12: folds a rotation into the running caster digest.</summary>
    public static void MixCaster(in Quaternion value)
    {
        MixCaster(value.X);
        MixCaster(value.Y);
        MixCaster(value.Z);
        MixCaster(value.W);
    }

    /// <summary>1-5 clause 12: folds a world bounding box into the running caster digest. This is the whole of what a rigid
    /// caster contributes: the box is derived from the same world matrix the shadow pass would rasterize with, so any change
    /// of position, rotation or scale reaches the digest through it.</summary>
    public static void MixCaster(in Bounds3D value)
    {
        MixCaster(value.Center);
        MixCaster(value.Extents);
    }

    /// <summary>
    /// 1-5 clause 12: decides whether this frame's shadow pass can be skipped entirely, and returns true when it can.
    ///
    /// Two conditions, both necessary: the cascade matrices are bitwise unchanged (<see cref="MatricesStable"/>) and the
    /// caster digest is unchanged. Under both, a redraw would reproduce the existing atlas exactly rather than approximately,
    /// so this buys cost and changes no pixel. Reuse is also impossible on the first eligible frame of a run, since there is
    /// nothing yet to compare the digest against.
    ///
    /// Why the digest is built by walking the tree here instead of being collected during the pass, which would be free: a
    /// digest collected during the pass is only refreshed on frames the pass actually ran, so during a skip run it would go
    /// stale and a caster that started moving mid-run would keep its old shadow until the run ended. Walking here costs one
    /// extra traversal on redraw frames and replaces four with one on skipped frames - the pass replays the tree once per atlas
    /// quadrant - so it is a net reduction in CPU traversals whenever anything is being skipped at all, and a flat surcharge of
    /// one traversal per frame whenever nothing is. Which of the two applies is what <see cref="ReportReuse"/> is for; the
    /// surcharge is why <see cref="RenderQuality.ShadowAtlasReuse"/> defaults off.
    ///
    /// The digest is one value for the whole atlas, not one per quadrant, so a caster moving anywhere disqualifies every slot
    /// including those it is culled from. Per-quadrant digests would be cheap to accumulate, but skipping a single quadrant is
    /// not expressible today: the pass clears the entire atlas on entry, so partial reuse needs a scissored clear in all four
    /// backends before it could mean anything.
    ///
    /// The walk is deliberately not culled against any slot; see <see cref="AccumulatingCasters"/>.
    ///
    /// Safety of reusing the previous frame's atlas contents: the atlas render target is created once during backend startup
    /// at ShadowAtlasSize, which is a locked tier value, and is never resized or recreated, so nothing else can invalidate it
    /// between frames. It is the pass itself that destroys the contents, by clearing on entry, which is why the caller must
    /// bypass BeginPass and not merely return early from the pass body.
    /// </summary>
    public static bool EvaluateReuse(BaseApp app)
    {
        if (app == null || !RenderQuality.Current.ShadowAtlasReuse
            || !RenderQuality.Current.ShadowsEnabled || (!SunActive && !SpotActive))
        {
            // Nothing was fingerprinted, so the stored digest no longer describes a known frame.
            _prevFingerprintValid = false;
            ReuseActive = false;
            return false;
        }

        _fingerprintAccum = FnvOffsetBasis;
        _fingerprintWalk = true;
        app.DrawShadow();
        _fingerprintWalk = false;

        CasterFingerprint = _fingerprintAccum;
        bool reuse = MatricesStable && _prevFingerprintValid && CasterFingerprint == _prevCasterFingerprint;
        _prevCasterFingerprint = CasterFingerprint;
        _prevFingerprintValid = true;

        _reuseEvaluated++;
        if (reuse)
            _reuseSkipped++;

        ReuseActive = reuse;
        return reuse;
    }

    /// <summary>
    /// 1-5 clause 12: every <see cref="DiagFrameInterval"/> frames, reports the fraction of shadow passes actually avoided.
    /// This is the number to pair with <see cref="RenderQuality.ShadowTargetStableFrames"/> when tuning: a target of N frames
    /// caps this at (N-1)/N, and falling well short of that cap means something in the scene is breaking the interval rather
    /// than the step being sized wrongly - compare against the avg reported by <see cref="ReportStability"/> to tell which.
    ///
    /// Note that skipped frames contribute nothing to the <c>[ShadowCull]</c> counters, since no slot is entered, so that
    /// diagnostic naturally reports fewer tests once reuse is achieving anything.
    /// </summary>
    static void ReportReuse()
    {
        if (Epoch % DiagFrameInterval != 0 || _reuseEvaluated == 0)
            return;

        DeviceServices.BaseApp?.AddLog(LogType.Backend,
            $"{DateTime.UtcNow} [ShadowReuse] " +
            $"skipped {_reuseSkipped}/{_reuseEvaluated} " +
            $"({_reuseSkipped * 100 / _reuseEvaluated}% of shadow passes avoided)");

        _reuseSkipped = 0;
        _reuseEvaluated = 0;
    }

    /// <summary>
    /// Computes the directional-light CSM cascade matrices. The camera must already have completed UpdateIfChanged for this frame so aspect and frustum parameters are ready.
    /// sunDir is the world-space propagation direction pointing toward the lit surface, matching the semantics of directional-light DirType.xyz, and is normalized internally.
    /// </summary>
    public static void ComputeSun(Camera3D camera, Vector3 sunDir)
    {
        if (sunDir.LengthSquared() < 1e-12f)
            return;
        sunDir = Vector3.Normalize(sunDir);

        // Clause 9: quantize before anything else derives from the direction. Everything below - the light basis, the
        // snapping grid, the eye position - is a function of sunDir, so this single substitution is what makes the whole
        // cascade matrix reproducible frame to frame. See QuantizeLightDirection for why snapping alone is not enough.
        // Clause 11 chooses the step first, from the raw direction, because the estimator has to see motion finer than one cell.
        float angleStep = UpdateAngularStep(sunDir);
        sunDir = QuantizeLightDirection(sunDir, angleStep);

        int count = Math.Clamp(RenderQuality.Current.ShadowCascadeCount, 2, MaxCascades);
        float near = camera.Near;
        float far = MathF.Max(MathF.Min(camera.Far, RenderQuality.Current.ShadowDistance), near + 1e-3f);
        float lambda = Math.Clamp(RenderQuality.Current.CascadeSplitLambda, 0f, 1f);

        // Practical split: blend logarithmic and uniform partitioning by lambda, per contract clause 9.
        Span<float> splits = stackalloc float[MaxCascades];
        for (int i = 0; i < count; i++)
        {
            float p = (i + 1) / (float)count;
            float logSplit = near * MathF.Pow(far / near, p);
            float uniSplit = near + (far - near) * p;
            splits[i] = lambda * logSplit + (1f - lambda) * uniSplit;
        }

        // Camera basis for LH space: right = up × forward.
        var forward = camera.Target - camera.Position;
        if (forward.LengthSquared() < 1e-12f)
            forward = Vector3.UnitZ;
        forward = Vector3.Normalize(forward);
        var right = Vector3.Cross(camera.Up, forward);
        if (right.LengthSquared() < 1e-12f)
            right = Vector3.UnitX;
        right = Vector3.Normalize(right);
        var up = Vector3.Cross(forward, right);

        float tanHalfFov = MathF.Tan(camera.FovY * 0.5f);
        float aspect = camera.Aspect > 0f ? camera.Aspect : 1f;
        float tileRes = RenderQuality.Current.ShadowAtlasSize * 0.5f;
        // Clause 9: the reference axis has to be a continuous function of the quantized direction, not a two-way choice at a
        // threshold, or the basis takes a 90 degree step whenever the sun crosses it. See UpBlendStart for why that step is
        // more expensive than the degenerate case it was avoiding. Below the band this is exactly Vector3.UnitY.
        float verticality = MathF.Abs(sunDir.Y);
        float upTilt = Math.Clamp((verticality - UpBlendStart) / (UpBlendEnd - UpBlendStart), 0f, 1f);
        var lightUp = Vector3.Lerp(Vector3.UnitY, Vector3.UnitZ, upTilt);
        lightUp = Vector3.Normalize(lightUp);

        Span<Vector3> corners = stackalloc Vector3[8];
        float sliceNear = near;
        for (int c = 0; c < count; c++)
        {
            float sliceFar = splits[c];

            // The 8 corners of the current frustum slice.
            int k = 0;
            for (int e = 0; e < 2; e++)
            {
                float d = e == 0 ? sliceNear : sliceFar;
                float halfH = d * tanHalfFov;
                float halfW = halfH * aspect;
                var center = camera.Position + forward * d;
                corners[k++] = center - right * halfW - up * halfH;
                corners[k++] = center + right * halfW - up * halfH;
                corners[k++] = center - right * halfW + up * halfH;
                corners[k++] = center + right * halfW + up * halfH;
            }

            // Bounding sphere: centroid + max distance. Stable under rotation, with radius quantization further suppressing tiny FOV/aspect jitter.
            var sphereCenter = Vector3.Zero;
            for (int i = 0; i < 8; i++)
                sphereCenter += corners[i];
            sphereCenter *= 1f / 8f;

            float radius = 0f;
            for (int i = 0; i < 8; i++)
                radius = MathF.Max(radius, Vector3.Distance(corners[i], sphereCenter));
            radius = MathF.Ceiling(radius * 16f) / 16f;

            // Light-space view: move the eye back along -sunDir far enough that casters standing between the slice and the sun
            // are still in front of the near plane. Only the light side needs the margin - nothing behind the slice can cast
            // into it - and the margin a caster of height h needs, measured along the light, is h/sin(elevation), the distance
            // from its top down to the ground it shadows. The old code spent a full radius on each side unconditionally, giving
            // a depth range of four radii where the light side alone needs one margin, and those wasted bits came out of the
            // same mantissa the stored depths use, which is what acne resolution is made of. Capped at one radius so this can
            // only ever reclaim precision the old formula wasted, never remove coverage it had; quantized to the same 1/16 grid
            // as the radius so a drifting elevation cannot make the matrix change every frame and undo clause 9 - sunDir is
            // already quantized above, so this is quantization of an already-quantized input.
            float casterHeight = RenderQuality.Current.ShadowCasterHeight;
            // Inverted comparison so a NaN from a malformed settings file collapses the margin rather than poisoning the matrix.
            if (!(casterHeight > 0f))
                casterHeight = 0f;
            float lightPad = MathF.Min(casterHeight / MathF.Max(verticality, MinElevationSine), radius);
            lightPad = MathF.Ceiling(lightPad * 16f) / 16f;

            var eye = sphereCenter - sunDir * (radius + lightPad);
            var view = Matrix4x4.CreateLookAtLeftHanded(eye, sphereCenter, lightUp);

            // Texel snapping: align the translation terms of the view matrix, which are the light-space offsets, to the texel
            // grid per contract clause 9. All three components are snapped, not just XY: an unsnapped Z lets every stored
            // depth drift continuously as the camera moves, so surfaces sitting near the bias margin cross the depth
            // comparison at different moments and produce acne that churns. With the direction quantized, the radius
            // quantized and the full translation snapped, view*proj is bitwise identical while the camera moves less than a
            // texel inside one angular cell, which makes the atlas itself bitwise identical rather than merely similar.
            float texelSize = radius * 2f / tileRes;
            view.M41 = MathF.Floor(view.M41 / texelSize) * texelSize;
            view.M42 = MathF.Floor(view.M42 / texelSize) * texelSize;
            view.M43 = MathF.Floor(view.M43 / texelSize) * texelSize;

            // Far plane just past the back of the sphere: eye-to-sphere-far is (radius + lightPad) + radius.
            var proj = Matrix4x4.CreateOrthographicLeftHanded(radius * 2f, radius * 2f, 0f, radius * 2f + lightPad);
            CascadeViewProj[c] = view * proj;

            sliceNear = sliceFar;
        }

        CascadeSplits = new Vector4(
            splits[0],
            count > 1 ? splits[1] : far,
            count > 2 ? splits[2] : far,
            far);
        ActiveCascadeCount = count;
        SunActive = true;
        ComputeFar(sunDir);
    }

    /// <summary>
    /// Builds the fog's own shadow view: one orthographic map that covers the medium rather than the camera.
    ///
    /// Why a second map at all: the cascades stop at <see cref="RenderQuality.ShadowDistance"/> because every metre they
    /// add is paid for by the near cascade, where the acne that matters lives. A fog volume tens of metres across can be
    /// viewed from outside it - from a ridge, from across a clearing - and the froxels that make up a shaft then sit past
    /// the last cascade, where the fog's own visibility test has nothing to say and every shaft flattens into a slab.
    /// Widening the cascades would trade the near field's precision for this; a second map costs one 1024^2 depth pass
    /// and is sampled by exactly one consumer, the fog injection kernel.
    ///
    /// Centre and extent come from the medium, not the camera. That choice is what makes the map stable: with the sun
    /// direction already quantized by the caller, the matrix only changes when that direction moves, so the texture is
    /// reused across camera motion without any of the temporal bookkeeping the cascades need.
    ///
    /// Returns without activating when there is no medium to cover. Global fog alone is not a reason to build this:
    /// its extinction is low by construction and it never resolves into individual shafts, which is the only thing the
    /// extra pass buys.
    /// </summary>
    static void ComputeFar(Vector3 sunDir)
    {
        var fog = DeviceServices.BaseApp?.Fog;
        if (fog == null || !fog.Enabled) return;

        // Union of the world AABBs of the volumes that actually add density. Exclude volumes subtract light rather than
        // occlude it, so a corridor carved out of a basin must not stretch the map to its own far corner.
        var minimum = new Vector3(float.MaxValue);
        var maximum = new Vector3(float.MinValue);
        foreach (var volume in fog.Volumes)
        {
            if (!volume.Enabled || volume.Mode != FogVolumeMode.Add || volume.Extinction <= 0f) continue;
            // A rotated box's world AABB: take the absolute rotation, because a rotation can only ever move a corner
            // outwards in each axis. The same formula bounds a sphere volume's ellipsoid, so both shapes share one path.
            var rotation = Matrix4x4.CreateFromQuaternion(volume.Rotation);
            var half = volume.Size * 0.5f;
            var box = new Vector3(
                MathF.Abs(rotation.M11) * half.X + MathF.Abs(rotation.M21) * half.Y + MathF.Abs(rotation.M31) * half.Z,
                MathF.Abs(rotation.M12) * half.X + MathF.Abs(rotation.M22) * half.Y + MathF.Abs(rotation.M32) * half.Z,
                MathF.Abs(rotation.M13) * half.X + MathF.Abs(rotation.M23) * half.Y + MathF.Abs(rotation.M33) * half.Z);
            minimum = Vector3.Min(minimum, volume.Position - box);
            maximum = Vector3.Max(maximum, volume.Position + box);
        }
        if (minimum.X > maximum.X) return;

        var center = (minimum + maximum) * 0.5f;
        // The bounding sphere rather than the box, because the light direction is arbitrary: a box covering the AABB
        // exactly along the light's own axes would still clip its corners under a raking sun. 1.05 leaves a margin for
        // the noise offset the injection kernel adds on top of a volume's position.
        float radius = ((maximum - minimum) * 0.5f).Length() * 1.05f;
        float extent = Math.Clamp(radius, FarMinExtent, FarMaxExtent);

        // Same light basis as the cascades, so the two maps agree on which way is up around the sun and a shadow edge
        // crossing from one to the other does not rotate.
        float verticality = MathF.Abs(sunDir.Y);
        if (verticality < 1e-4f) return; // the sun is in the horizon plane; no orthographic view can be built from it
        float upTilt = Math.Clamp((verticality - UpBlendStart) / (UpBlendEnd - UpBlendStart), 0f, 1f);
        var lightUp = Vector3.Normalize(Vector3.Lerp(Vector3.UnitY, Vector3.UnitZ, upTilt));

        float casterHeight = RenderQuality.Current.ShadowCasterHeight;
        if (!(casterHeight > 0f)) casterHeight = 0f;
        float lightPad = MathF.Min(casterHeight / MathF.Max(verticality, MinElevationSine), extent);
        lightPad = MathF.Ceiling(lightPad * 16f) / 16f;

        var eye = center - sunDir * (extent + lightPad);
        var view = Matrix4x4.CreateLookAtLeftHanded(eye, center, lightUp);

        // Same texel snap as the cascades, for the same reason: a matrix that only differs in its sub-texel translation
        // re-samples every stored depth at a new phase, which shows up as crawling edges on a shadow that is otherwise
        // static. Here it also keeps the map reusable across frames that moved nothing but the camera.
        float texelSize = extent * 2f / FarSize;
        view.M41 = MathF.Floor(view.M41 / texelSize) * texelSize;
        view.M42 = MathF.Floor(view.M42 / texelSize) * texelSize;
        view.M43 = MathF.Floor(view.M43 / texelSize) * texelSize;

        var proj = Matrix4x4.CreateOrthographicLeftHanded(extent * 2f, extent * 2f, 0f, extent * 2f + lightPad);
        FarViewProj = view * proj;
        FarExtent = extent;
        FarActive = true;
    }

    /// <summary>
    /// Selects up to <see cref="LocalShadowLights"/> point lights and builds a cube of light-space matrices for each, so the
    /// fog injection kernel can test whether geometry stands between a froxel and the lamp warming it.
    ///
    /// Why the fog needs this at all: without a depth map a punctual light reaches a froxel at 1/d² and nothing else, so a
    /// torch inside a barn lights the mist through the wall. The cascades cannot help - they belong to the sun - and the
    /// spotlight quadrant belongs to a spot light, which has a cone the point light does not. A lamp's occlusion is a
    /// sphere of directions, which is what a cube is.
    ///
    /// Why selection ignores the camera: the resulting matrices must be bit-identical while the eye moves, or the fog's
    /// temporal filter re-converges on a shadow that shimmers every frame. The predicate here is "the light's range sphere
    /// touches a volume that adds density" and the ranking is the light's own luminance-weighted intensity, neither of
    /// which mentions the camera. This is the same reasoning that anchors <see cref="ComputeFar"/> to the medium.
    ///
    /// Only point lights participate. A spot light already owns atlas slot 3 and is reached through it, and a directional
    /// light has no position to build a cube around.
    ///
    /// Cost is bounded and paid only when it buys something: nothing is computed, no pass is opened and no matrix is
    /// uploaded when no enabled Add volume overlaps a point light.
    /// </summary>
    public static void ComputeLocal(in SceneLightParams scene)
    {
        var fog = DeviceServices.BaseApp?.Fog;
        if (fog == null || !fog.Enabled) return;

        // Union of the world AABBs of the volumes that add density, by the same formula ComputeFar uses: a rotated box's
        // world AABB takes the absolute rotation, and the same expression bounds a sphere volume's ellipsoid.
        var minimum = new Vector3(float.MaxValue);
        var maximum = new Vector3(float.MinValue);
        foreach (var volume in fog.Volumes)
        {
            if (!volume.Enabled || volume.Mode != FogVolumeMode.Add || volume.Extinction <= 0f) continue;
            var rotation = Matrix4x4.CreateFromQuaternion(volume.Rotation);
            var half = volume.Size * 0.5f;
            var box = new Vector3(
                MathF.Abs(rotation.M11) * half.X + MathF.Abs(rotation.M21) * half.Y + MathF.Abs(rotation.M31) * half.Z,
                MathF.Abs(rotation.M12) * half.X + MathF.Abs(rotation.M22) * half.Y + MathF.Abs(rotation.M32) * half.Z,
                MathF.Abs(rotation.M13) * half.X + MathF.Abs(rotation.M23) * half.Y + MathF.Abs(rotation.M33) * half.Z);
            minimum = Vector3.Min(minimum, volume.Position - box);
            maximum = Vector3.Max(maximum, volume.Position + box);
        }
        if (minimum.X > maximum.X) return;

        int lightCount = Math.Clamp(scene.LightCount, 0, SceneLightParams.MaxLights);
        Span<int> chosen = stackalloc int[LocalShadowLights];
        Span<float> chosenScore = stackalloc float[LocalShadowLights];
        for (int s = 0; s < LocalShadowLights; s++)
        {
            chosen[s] = -1;
            chosenScore[s] = 0f;
        }

        // Insertion by descending score. A tie leaves the lower light index in front, because a light only displaces one
        // already held on a strictly greater score - which is what keeps the ranking stable against a flame whose
        // intensity is being modulated every frame.
        for (int i = 0; i < lightCount; i++)
        {
            var light = scene.Lights[i];
            if (light.DirType.W >= 0.5f) continue; // spots own atlas slot 3, directional lights have no position
            float range = light.PosRange.W;
            if (!(range > 0f)) continue; // an unbounded light has no cube to build; the fog leaves it at plain 1/d²
            var position = new Vector3(light.PosRange.X, light.PosRange.Y, light.PosRange.Z);
            if (!SphereTouches(position, range, minimum, maximum)) continue;
            float score = light.ColorIntensity.W * MathF.Max(
                light.ColorIntensity.X * 0.2126f + light.ColorIntensity.Y * 0.7152f + light.ColorIntensity.Z * 0.0722f, 0f);
            if (!(score > 0f)) continue;
            for (int s = 0; s < LocalShadowLights; s++)
            {
                if (chosen[s] >= 0 && score <= chosenScore[s]) continue;
                for (int k = LocalShadowLights - 1; k > s; k--)
                {
                    chosen[k] = chosen[k - 1];
                    chosenScore[k] = chosenScore[k - 1];
                }
                chosen[s] = i;
                chosenScore[s] = score;
                break;
            }
        }

        // The six faces share one projection: a square 90 degree frustum per axis is exactly a cube face, and 90 rather
        // than a little more because at exactly 90 the faces tile the sphere with no overlap and no gap, so the kernel's
        // dominant-axis test can never land on a face that does not cover the direction.
        float fovY = MathF.PI * 0.5f;
        for (int s = 0; s < LocalShadowLights; s++)
        {
            LocalLightIndex[s] = chosen[s];
            if (chosen[s] < 0) continue;

            var light = scene.Lights[chosen[s]];
            var position = new Vector3(light.PosRange.X, light.PosRange.Y, light.PosRange.Z);
            // Selection above rejected every light whose range is not positive, so the far plane is always authored.
            float range = light.PosRange.W;
            var proj = Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(fovY, 1f, LocalNear, range);
            for (int face = 0; face < LocalShadowFaces; face++)
            {
                var direction = LocalFaceDirections[face];
                // A face pointing along Y cannot look up at Y; the spot path makes the same substitution.
                var up = MathF.Abs(direction.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
                var view = Matrix4x4.CreateLookAtLeftHanded(position, position + direction, up);
                LocalViewProj[s * LocalShadowFaces + face] = view * proj;
            }
            LocalCount = s + 1;
            LocalActive = true;
        }
    }

    /// <summary>Whether a sphere overlaps an axis-aligned box. Used to skip lamps whose light cannot reach any of the
    /// medium, which is the only reason a local cube would be built for nothing.</summary>
    static bool SphereTouches(Vector3 center, float radius, Vector3 minimum, Vector3 maximum)
    {
        var closest = Vector3.Clamp(center, minimum, maximum);
        return Vector3.DistanceSquared(center, closest) < radius * radius;
    }

    /// <summary>
    /// Quantizes the directional-light direction onto a fixed spherical grid whose step is <paramref name="stepDegrees"/>,
    /// as chosen by <see cref="UpdateAngularStep"/>. Returns the input unchanged when the step is not positive.
    ///
    /// Why snapping alone is insufficient: texel snapping aligns the light-space translation to the texel grid, but that grid
    /// *is* the light basis. When the sun rotates continuously, the grid rotates with it, so snapping quantizes into a frame of
    /// reference that is itself moving and the guarantee it was supposed to provide - "sub-texel camera motion changes nothing"
    /// - no longer holds. Every texel in the atlas re-samples the depth surface at a new sub-texel phase each frame, which
    /// shows up as shadow-edge crawling and as a self-shadowing speckle pattern that churns even with the camera and all
    /// casters perfectly still.
    ///
    /// What quantization buys: inside one angular cell the light basis is fixed, so the rotation part of the view matrix is
    /// bit-identical, the per-cascade radius is already quantized to 1/16 and depends only on constant projection parameters,
    /// and the snapped translation is bit-identical while the camera moves less than a texel. The cascade matrix is therefore
    /// bitwise unchanged, the atlas is bitwise unchanged, and temporal accumulation finally has a stable signal to converge on.
    /// When the cell does change, the shadow advances in one coherent step instead of dissolving pixel by pixel.
    ///
    /// What it costs, stated plainly: the shadow direction lags the shading direction by at most half a step, and the
    /// accumulated light motion is batched rather than removed. A larger step buys a longer stable interval and pays with a
    /// proportionally larger jump, so the step should stay small enough that one jump is on the order of a texel for typical
    /// caster heights. Only shadow matrices consume the quantized value; N-dot-L shading keeps the exact direction from the
    /// lighting UBO, so this never changes how surfaces are lit.
    ///
    /// Angles are quantized rather than components: cell boundaries then stay fixed in world space and the result stays unit
    /// length by construction, which is what makes the basis reproducible instead of merely close.
    /// </summary>
    static Vector3 QuantizeLightDirection(Vector3 dir, float stepDegrees)
    {
        // Inverted rather than <=0 so a NaN coming out of a malformed settings file also takes the bypass.
        // A NaN would otherwise propagate into the cascade matrix and silently remove every shadow in the scene.
        if (!(stepDegrees > 0f))
            return dir;

        float step = stepDegrees * (MathF.PI / 180f);
        float azimuth = MathF.Atan2(dir.X, dir.Z);
        float elevation = MathF.Asin(Math.Clamp(dir.Y, -1f, 1f));

        azimuth = MathF.Round(azimuth / step) * step;
        elevation = MathF.Round(elevation / step) * step;

        float cosElevation = MathF.Cos(elevation);
        return new Vector3(
            MathF.Sin(azimuth) * cosElevation,
            MathF.Sin(elevation),
            MathF.Cos(azimuth) * cosElevation);
    }

    /// <summary>
    /// 1-5 clause 11: sizes the angular quantization step from the light's own measured speed, so that one cell lasts about
    /// <see cref="RenderQuality.ShadowTargetStableFrames"/> frames instead of however long a fixed step happens to buy.
    /// Publishes and returns <see cref="EffectiveLightAngleStep"/>.
    ///
    /// Why speed is measured here rather than read from whatever drives the sun: the app owns the day-night rate and the
    /// engine cannot see it, and even if it could, the rate is not the only thing that moves the light - a script, a dragged
    /// slider, or the sun/moon ownership handover in SceneLighting.Bake all change the direction too. Differencing the
    /// direction the shadow system was actually handed covers every one of those without a dependency, and it needs no frame
    /// time because the target is expressed in frames: the ideal step is simply the per-frame angular delta times the target.
    ///
    /// The rungs are powers of two of the configured base, and that is load-bearing rather than tidy. An adaptive step would
    /// otherwise defeat itself: a step that drifts every frame moves the cell boundaries every frame, so the direction would
    /// never be reproducible and clause 9 would buy nothing. A discrete ladder makes the step constant over long stretches.
    /// Powers of two specifically because those grids nest: every boundary of a coarser rung is also a boundary of a finer one,
    /// so stepping down the ladder cannot move the current direction at all, and stepping up moves it by at most half of the
    /// new step. Any other ratio would relocate every boundary and discard the current cell.
    ///
    /// Rounding in log space places the boundary between rungs but supplies no hysteresis, having no memory of the rung it is
    /// currently on: an estimate sitting near a boundary flips on noise alone. Measured in this engine, a sun at 0.09 deg/frame
    /// against a target of 8 lands within a percent of the 1x/2x boundary, and the rung changed six times across eight
    /// diagnostic windows, every time on a delta jitter under 6%. Since each upward change relocates the grid by up to half the
    /// new step, every flip truncated the very interval it was trying to lengthen - the same scene averaged 4.8 stable frames
    /// while the rung oscillated and 8.4 once it settled, at a slightly *higher* light speed. Left unguarded, the mechanism is
    /// its own dominant source of churn, so <see cref="RungHysteresis"/>, <see cref="RungConfirmFrames"/> and
    /// <see cref="DeltaOutlierRatio"/> each close one route by which it could be.
    ///
    /// The scale factor is built by integer shifting rather than a pow call because the step feeds the cascade matrix, whose
    /// whole value here is being bit-identical across frames and backends. Multiplying and dividing by an exact power of two
    /// is exact by construction and leaves no room for a library's rounding to differ.
    ///
    /// A motionless light does not land on the bottom rung, though an earlier version of this claimed it should. The argument
    /// was that a direction which never changes is stable at any step, so the finest grid is free precision - true of the
    /// direction, false of the grid, because the finest rung also packs the cell boundaries eight times more densely and a
    /// parked sun is then eight times more likely to be sitting on one, where the smallest residual jitter crosses it. The
    /// measurement cannot tell a stopped sun from a slow one either, since acos runs out of precision first; see
    /// <see cref="DeltaNoiseFloorDegrees"/>. Below that floor the ladder holds rung 0.
    /// </summary>
    static float UpdateAngularStep(Vector3 rawDir)
    {
        float baseStep = RenderQuality.Current.ShadowLightAngleStep;

        // Inverted comparison so a NaN from a malformed settings file also disables quantization rather than poisoning the matrix.
        if (!(baseStep > 0f))
        {
            _prevRawValid = false;
            _rungValid = false;
            EffectiveLightAngleStep = 0f;
            return 0f;
        }

        int target = RenderQuality.Current.ShadowTargetStableFrames;
        if (target < 2)
        {
            // Adaptation off: behave exactly as clause 9 did before the ladder existed. The estimator state is dropped so
            // re-enabling it later starts from measurement rather than from a stale delta.
            _prevRawValid = false;
            _smoothedDeltaDegrees = 0f;
            _rungValid = false;
            EffectiveLightAngleStep = baseStep;
            return baseStep;
        }

        // The held rung is an exponent relative to the base, so a base retuned from a control panel would silently
        // reinterpret it into a different step. Dropping it there costs one re-acquisition and keeps it meaning what it says.
        if (_rungBaseStep != baseStep)
            _rungValid = false;
        _rungBaseStep = baseStep;

        float deltaDegrees = 0f;
        if (_prevRawValid)
        {
            float cos = Math.Clamp(Vector3.Dot(rawDir, _prevRawSunDir), -1f, 1f);
            deltaDegrees = MathF.Acos(cos) * (180f / MathF.PI);
        }
        _prevRawSunDir = rawDir;
        _prevRawValid = true;

        // Seeded at the delta that asks for the base step exactly, so acquisition begins at rung 0 rather than letting the
        // first sample become the whole estimate - at startup that sample is a load stall more often than not.
        if (!(_smoothedDeltaDegrees > 0f))
            _smoothedDeltaDegrees = baseStep / target;

        float ceiling = _smoothedDeltaDegrees * DeltaOutlierRatio;
        if (deltaDegrees > ceiling)
            deltaDegrees = ceiling;

        float alpha = deltaDegrees > _smoothedDeltaDegrees ? DeltaRiseAlpha : DeltaFallAlpha;
        _smoothedDeltaDegrees += (deltaDegrees - _smoothedDeltaDegrees) * alpha;

        // exact is the rung the estimate asks for as a real number; want is the nearest reachable one. Keeping both is what
        // lets the band be measured from the rung actually held instead of from the rounded answer, which is where the
        // hysteresis lives: rounding compares against a boundary, this compares against a position.
        float exact;
        int want;
        if (_smoothedDeltaDegrees < DeltaNoiseFloorDegrees)
        {
            // The estimate has fallen below what acos can resolve, so it is no longer a measurement of the light. Hold rung 0
            // rather than following it down the ladder. exact is set to 0 as well, not just want: the hysteresis band is
            // measured from exact, so leaving it at the noisy value would keep re-arming a move away from the rung being held.
            exact = 0f;
            want = 0;
        }
        else
        {
            float ideal = _smoothedDeltaDegrees * target;
            exact = ideal > 0f ? MathF.Log2(ideal / baseStep) : -StepLadderRange;
            want = Math.Clamp((int)MathF.Round(exact), -StepLadderRange, StepLadderRange);
        }

        if (!_rungValid)
        {
            _rung = want;
            _rungValid = true;
            _rungPendingWant = want;
            _rungPending = 0;
        }
        else if (MathF.Abs(exact - _rung) > 0.5f + RungHysteresis)
        {
            // Too far out to be noise about a boundary, so this is the light's speed having actually changed.
            _rung = want;
            _rungPendingWant = want;
            _rungPending = 0;
        }
        else if (want != _rungPendingWant)
        {
            // The rounded answer moved, so whatever was accumulating was noise rather than a lean. Start over.
            _rungPendingWant = want;
            _rungPending = 0;
        }
        else if (want != _rung && ++_rungPending >= RungConfirmFrames)
        {
            _rung = want;
            _rungPending = 0;
        }

        EffectiveLightAngleStep = _rung >= 0
            ? baseStep * (1 << _rung)
            : baseStep / (1 << -_rung);
        return EffectiveLightAngleStep;
    }

    /// <summary>
    /// Computes the spotlight shadow-map matrix. Contract clause 8 states that it only applies to the light designated by Params0.W and only when that light is a spot; the caller is responsible for that check.
    /// fov = 2×outerConeAngle to fully cover the cone, and far = range, or <see cref="SpotFarFallback"/> when range&lt;=0.
    /// </summary>
    public static void ComputeSpot(in GpuLight spot)
    {
        var dir = new Vector3(spot.DirType.X, spot.DirType.Y, spot.DirType.Z);
        if (dir.LengthSquared() < 1e-12f)
            return;
        dir = Vector3.Normalize(dir);

        var pos = new Vector3(spot.PosRange.X, spot.PosRange.Y, spot.PosRange.Z);
        float range = spot.PosRange.W > 0f ? spot.PosRange.W : SpotFarFallback;

        // Convert cosOuter to the full cone angle. Clamp to avoid acos domain errors and projection degeneration when the cone gets too wide and approaches a hemisphere.
        float cosOuter = Math.Clamp(spot.SpotParams.Y, 0.05f, 0.995f);
        float fovY = MathF.Min(2f * MathF.Acos(cosOuter), MathF.PI * 0.9f);

        var lightUp = MathF.Abs(dir.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        var view = Matrix4x4.CreateLookAtLeftHanded(pos, pos + dir, lightUp);
        var proj = Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(fovY, 1f, SpotNear, range);

        SpotViewProj = view * proj;
        SpotActive = true;
    }

    /// <summary>
    /// Writes this frame's shadow results into the lighting UBO mirror. This is the single write entry point for shadow fields under contract clause 1.
    /// When ShadowsEnabled=false or no light is active this frame, zeros are written so shader-side ShadowParams all become zero and shadows are fully disabled.
    ///
    /// Clause 10 additionally makes this the frame's shadow-state closing point: it evaluates <see cref="MatricesStable"/>
    /// here, because this runs after every Compute call and before any consumer, and it is already called by all four
    /// backends. The disabled arm invalidates the previous-frame mirror instead of evaluating, since
    /// <see cref="FrameSchedule"/> clears the atlas whenever the pass is entered at all - even with a body that returns
    /// immediately - so nothing survives for a later frame to be compared against.
    /// </summary>
    public static void Apply(ref SceneLightParams scene)
    {
        if (!RenderQuality.Current.ShadowsEnabled || (!SunActive && !SpotActive))
        {
            scene.ShadowParams0 = default;
            scene.ShadowParams1 = default;
            _prevValid = false;
            // Folded rather than dropped: the interval up to here happened, and discarding it silently biased the average
            // downwards every time shadows were toggled off or the last light left the frame.
            CloseStableRun();
            return;
        }

        EvaluateStability();

        if (SunActive)
        {
            for (int i = 0; i < ActiveCascadeCount; i++)
                scene.CascadeViewProj[i] = CascadeViewProj[i];
            scene.CascadeSplits = CascadeSplits;
        }

        if (SpotActive)
            scene.SpotShadowViewProj = SpotViewProj;

        // Clause 15: contact hardening rides on the sign of the same slot rather than on a field of its own. The shader needs
        // exactly one more bit here - constant radius or occluder-derived radius - and the magnitude means the same thing
        // either way, a ceiling in texels. Spending a new vec4 on one bit would have grown SceneLightParams, and its size is
        // mirrored by hand in SCENE_LIGHT_BYTES across three copies of the web JS, so a layout change is the most expensive
        // way this codebase has of expressing a boolean. Negative is the opt-in so the untouched default stays positive.
        float softnessTexels = MathF.Max(RenderQuality.Current.ShadowSoftnessTexels, 0f);
        if (RenderQuality.Current.ShadowContactHardening)
            softnessTexels = -softnessTexels;

        scene.ShadowParams0 = new Vector4(
            SunActive ? 1f : 0f,
            SunActive ? ActiveCascadeCount : 0f,
            1f / RenderQuality.Current.ShadowAtlasSize,
            // Clause 14: w carries the rotated-disk radius in texels of the cascade tile, on the same footing as the
            // normal-offset in ShadowParams1.z - the shader turns texels into a tile-NDC step itself from z above.
            // Clause 15: a negative w means the same magnitude is a ceiling and the radius is derived per pixel instead.
            softnessTexels);
        // Clause 13: z carries the normal-offset in texels of the cascade tile, and is the whole of what that clause costs
        // the UBO - the shader converts texels to an NDC displacement itself from shadowParams0.z, so no per-cascade world
        // scale has to be uploaded. Zero disables the offset entirely, which is what the shader's own branch tests.
        //
        // Clause 14: w is the per-frame rotation seed for the PCF disk. The shader gets its spatial variation from
        // interleaved gradient noise over pixel coordinates, which is fixed for a given pixel, so without a term that moves
        // every frame TAA would average a constant pattern and bake the noise in permanently instead of resolving it. The
        // sequence is the golden-ratio additive one because it is the low-discrepancy choice for a one-dimensional series:
        // any short run of frames - which is all a TAA history is - lands close to evenly spread over the circle. The index
        // is masked to 1024 so the multiply stays exact; restarting the sequence that rarely is invisible against a history
        // an order of magnitude shorter.
        float rotationSeed = (float)((Epoch & 1023) * 0.61803398874989485 % 1.0);
        scene.ShadowParams1 = new Vector4(
            SpotActive ? 1f : 0f,
            Math.Clamp(RenderQuality.Current.ShadowStrength, 0f, 1f),
            MathF.Max(RenderQuality.Current.ShadowNormalOffset, 0f),
            rotationSeed);
    }

    /// <summary>
    /// Maps an atlas slot to its quadrant viewport in pixels. Contract clause 2 defines origin = ((slot%2)·half, (slot/2)·half).
    /// Called by backend shadow-pass rendering per quadrant, together with the controlled SetViewport path from contract clause 6.
    /// </summary>
    public static void GetAtlasViewport(int slot, out int x, out int y, out int size)
    {
        size = RenderQuality.Current.ShadowAtlasSize / 2;
        x = (slot % 2) * size;
        y = (slot / 2) * size;
    }
}
