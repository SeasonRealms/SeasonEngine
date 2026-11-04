// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Controls;

/// <summary>
/// P2: per-frame cull of the pose *evaluation* of <see cref="InstancedModel"/>, layered on top of the P1 sampling throttle.
///
/// What is culled. Only the CPU replay of the clip. An instance the main camera cannot reach still has its root transform
/// rebuilt and written to every per-primitive stream, still takes part in GPU culling and in the shadow pass, and is still
/// drawn from its cached pose -- so nothing about where it is changes, and nothing pops on the frame it leaves or re-enters.
/// What it stops paying for is the one thing that is proportional to instance count and useless off screen: evaluating a
/// clip nobody can see.
///
/// The test. <see cref="InstanceCull.CpuVisible"/> is the cull kernel's own sphere formula (model box weighted by the three
/// world-matrix rows, radius = |Row0|*ex + |Row1|*ey + |Row2|*ez) plus a guard band on the radius, evaluated against the main
/// view's unjittered frustum -- the same matrix <see cref="InstanceCullEffect"/> writes into the main-view slot. So the CPU
/// can only ever skip an evaluation the kernel would also have skipped, never one it still draws.
///
/// V1 conservative rule. The frustum is the main camera's, and during Update that camera is the only one prepared: a
/// registered secondary view (minimap, rear mirror) may be posed later in the frame. If any registered secondary view still
/// draws opaque geometry, the evaluation cull is switched off wholesale (<see cref="FrameSchedule.AnySecondaryViewDrawsOpaque"/>),
/// because a mirror can legitimately show an instance the main camera cannot. The P1 throttle keeps working in that case.
///
/// History. An instance that was culled and is visible again would otherwise carry motion vectors across the frames it spent
/// culled: the previous-frame transform is several frames old, so the first visible frame would report one huge velocity. On
/// that edge the backend republishes this frame's transform (and morph weights) into its previous-frame history, which makes
/// the velocity zero for exactly that frame. The bone palette is deliberately not republished: on the Web backend it is a
/// single per-skin upload whose previous-frame copy lives on the JS side and cannot be patched per instance, so patching it on
/// the other three backends alone would make the four backends disagree in TAA. What survives there is a pose difference, not
/// a positional jump (the root transform, which is what moves, is zeroed on all four).
///
/// Interplay with the P1 scheduler. A culled instance never reaches <see cref="ShouldSampleInstanceAnimation"/>, so its row's
/// <c>Frame</c> goes stale; the first frame it comes back therefore takes the <c>!stayedEnabled</c> branch and is sampled
/// regardless of tier or stagger. That is the forced re-sample the reset above relies on.
/// </summary>
public partial class InstancedModel
{
    /// <summary>
    /// P2: per-control switch for the pose-evaluation cull. Default true. A control whose instances are cheap to evaluate,
    /// or whose shadows must keep animating while off screen, turns this off and pays the evaluation; the P1 throttle is
    /// unaffected either way. <see cref="RenderQuality.InstanceAnimationCull"/> is a process-wide gate on top of this one.
    /// </summary>
    public bool AnimationCull { get; set; } = true;

    /// <summary>Per-instance root world computed once per submitted frame, so every backend reads the same value the cull
    /// decision was made from instead of recomposing it.</summary>
    Matrix4x4[] _animationWorlds = Array.Empty<Matrix4x4>();

    /// <summary>This frame's verdict per Instances index: true means "skip the pose evaluation". Disabled instances are
    /// false; they are not drawn and their row is re-sampled when they come back.</summary>
    bool[] _animationCulled = Array.Empty<bool>();

    /// <summary>Last frame's verdict, snapshotted at the start of <see cref="BeginInstanceCullFrame"/>. A reset is exactly the
    /// edge from true here to false above.</summary>
    bool[] _animationCulledPrev = Array.Empty<bool>();

    /// <summary>The <c>_animationLodFrame</c> stamp the three arrays above belong to. Backends only consult them through the
    /// accessors below and fall back to on-the-fly computation when the stamp is stale.</summary>
    int _animationCullStamp = -1;

    /// <summary>
    /// P2: starts the cull frame. Called once per submitted update, right after <see cref="BeginAnimationLodFrame"/> and before
    /// the backend replays the batch, because that replay is what reads the verdicts back out.
    ///
    /// The root world is composed for every instance in the list, enabled or not: the backends only visit the enabled ones, and
    /// writing the whole list keeps "world for index i" answerable without a second validity array. A disabled instance's
    /// composition is a few multiplies, against a clip replay for every enabled one.
    /// </summary>
    void BeginInstanceCullFrame()
    {
        _animationCullStamp = _animationLodFrame;

        int count = Instances.Count;

        if (_animationWorlds.Length < count)
            Array.Resize(ref _animationWorlds, count);
        if (_animationCulled.Length < count)
        {
            Array.Resize(ref _animationCulled, count);
            Array.Resize(ref _animationCulledPrev, count);
        }

        // Last frame's verdict becomes the "was culled" side before this frame's is computed, because that edge is the reset.
        Array.Copy(_animationCulled, _animationCulledPrev, count);
        if (count < _animationCulled.Length)
        {
            // Rows past the end of the list describe removed instances. Left set, they would look like a cull history to the
            // next instance that grows back into the slot.
            Array.Clear(_animationCulled, count, _animationCulled.Length - count);
            Array.Clear(_animationCulledPrev, count, _animationCulledPrev.Length - count);
        }

        // V1: a secondary view's camera may be posed after Update, so its opaque geometry cannot be reasoned about from here.
        bool active = AnimationCull && RenderQuality.Current.InstanceAnimationCull
            && !FrameSchedule.AnySecondaryViewDrawsOpaque;

        var view = active ? RenderView.CurrentOrNull : null;
        active &= view != null && view.Prepared;

        var bounds = TemplateCullBounds;
        var center = bounds.Center;
        var extents = bounds.Extents;
        // A zero box has no sphere to test, and testing one would drop every instance that has not finished loading.
        if (active && extents == Vector3.Zero)
            active = false;

        var frustum = active ? view!.Frustum : default;

        for (int i = 0; i < count; i++)
        {
            var instance = Instances[i];
            var world = BuildInstanceMatrix(instance);
            _animationWorlds[i] = world;

            _animationCulled[i] = active && instance.Enable
                && !InstanceCull.CpuVisible(in center, in extents, in world, in frustum);
        }
    }

    /// <summary>
    /// P2: the root instance world for <paramref name="index"/>, as computed by <see cref="BeginInstanceCullFrame"/>. Identical
    /// to <see cref="InstancedMesh3DBase.BuildInstanceMatrix"/> -- it *is* that call, made once per frame instead of once per
    /// backend stage. Falls back to composing on the spot when the frame stamp does not match, which is only reachable from a
    /// caller outside the submitted replay.
    /// </summary>
    internal Matrix4x4 InstanceWorld(int index)
    {
        if (_animationCullStamp == _animationLodFrame && (uint)index < (uint)_animationWorlds.Length)
            return _animationWorlds[index];
        return BuildInstanceMatrix(Instances[index]);
    }

    /// <summary>
    /// P2: whether instance <paramref name="index"/> should skip its pose evaluation this frame. When true the backend must also
    /// not consult <see cref="ShouldSampleInstanceAnimation"/> -- skipping that call is what leaves the row stale and forces a
    /// fresh sample on the first frame the instance is visible again.
    /// </summary>
    internal bool IsInstanceAnimationCulled(int index)
        => _animationCullStamp == _animationLodFrame
           && (uint)index < (uint)_animationCulled.Length && _animationCulled[index];

    /// <summary>
    /// P2: whether this is the first visible frame after one or more culled frames, i.e. whether the backend must republish the
    /// instance's current transform into its previous-frame history so its velocity reads zero for this frame.
    /// </summary>
    internal bool ShouldResetInstanceHistory(int index)
        => _animationCullStamp == _animationLodFrame
           && (uint)index < (uint)_animationCulled.Length
           && _animationCulledPrev[index] && !_animationCulled[index];
}
