// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// Everything one frame owns: the view slots and their target sets, the current-view cursor, the scene draw list,
/// the particle hand-off table, and the lighting/camera snapshot the backend captured for each view.
///
/// Why this is an instance and not more static fields on <see cref="FrameSchedule"/>: the pass chain runs once per
/// view and the backend captures the lighting snapshot once per view, at its own upload point
/// (<see cref="FrameSchedule.PrepareView"/>). A single static snapshot could only ever hold the last capture, so the
/// second view of a frame read the first view's matrices and light set. Every view slot owns a snapshot here, and the
/// static members on <see cref="FrameSchedule"/> forward onto <see cref="Active"/> / <see cref="ActiveSnapshot"/>,
/// which keeps every existing reader unchanged while the data underneath becomes per-view.
/// </summary>
public sealed class FrameState
{
    /// <summary>Target set of the main view (slot 0).</summary>
    public ViewTargets Main { get; } = new(0, "main");

    /// <summary>
    /// Frame-global slot table: entries that are genuinely camera-independent (SkyView LUT, cloud noise,
    /// the aerial-perspective LUT, the DDGI ping-pong read surfaces). Per-view entries live on each
    /// <see cref="ViewTargets.Textures"/> instead; <see cref="FrameTextureScopeRules.IsGlobal"/> is the
    /// single place that decides which is which.
    /// </summary>
    public FrameTextureTable Textures { get; } = new(-1);

    /// <summary>Views rendered after the main view. Empty is the single-view shape.</summary>
    public List<ViewSlot> Views { get; } = new();

    /// <summary>The view currently being executed, or <see cref="Main"/> outside a view.</summary>
    public ViewTargets Active { get; private set; } = null!;

    /// <summary>Hierarchical sort context of the view currently being collected. Handles expire on <see cref="SceneDrawOrderContext.Reset"/>.</summary>
    public SceneDrawOrderContext DrawOrder { get; } = new();

    /// <summary>Particle systems that handed over successfully in the view being prepared; cleared by Submit and by the view's exception path.</summary>
    public List<IParticleSubmitter> PreparedParticles { get; } = new();

    /// <summary>The <see cref="RenderView"/> whose constants are active right now; null outside a view before the first frame.</summary>
    public RenderView? CurrentView { get; set; }

    /// <summary>Feature flags of the view being executed.</summary>
    public ViewFeature ActiveFeatures { get; internal set; } = ViewFeature.All;

    /// <summary>Shadow policy of the view being executed.</summary>
    public ViewShadowPolicy ActiveShadowPolicy { get; internal set; } = ViewShadowPolicy.Shared;

    /// <summary>True between <see cref="FrameSchedule"/>'s collection begin and its release, for the view being executed.</summary>
    public bool CollectionActive { get; internal set; }

    /// <summary>Frames rendered so far; paced per view by <see cref="RenderView.UpdateInterval"/>.</summary>
    public ulong Index { get; internal set; }

    public FrameState() => Active = Main;

    /// <summary>
    /// The lighting/camera snapshot of one view, taken at the backend's final lighting upload point.
    /// Matrices are the ones the view actually rendered with (jitter and backend NDC adjustment baked in);
    /// <see cref="Lights"/> is the light set uploaded alongside them.
    /// </summary>
    public struct ViewSnapshot
    {
        public SceneLightParams Lights;
        public Matrix4x4 View;
        public Matrix4x4 Projection;
        public Matrix4x4 UnjitteredViewProjection;

        /// <summary>False when neither a backend capture nor a view-derived snapshot covered this view this frame.</summary>
        public bool Valid;
    }

    // One slot per view index. MaxViews is a compile-time constant on the scheduler, so the array size is fixed and
    // no view can ever index outside it (Snapshot throws instead of aliasing two views).
    readonly ViewSnapshot[] _snapshots = new ViewSnapshot[FrameSchedule.MaxViews];

    // Capture made during PrepareView, waiting for the scheduler to promote it. Kept separate from the slots so a
    // backend that captures before the view's chain starts cannot publish half-updated state.
    ViewSnapshot _pending;
    int _pendingIndex = -1;
    bool _pendingCaptured;
    BaseApp? _pendingApp;

    /// <summary>
    /// Starts a frame: every view's snapshot expires with the previous frame, and so does the hand-off table.
    ///
    /// The pending capture is deliberately NOT cleared here: the main view's lighting upload happens at the camera
    /// update point, i.e. before this runs, and clearing it would discard exactly the capture that is about to be
    /// promoted. Expiry is enforced by <see cref="ViewSnapshot.Valid"/>, which is what every reader checks first.
    /// </summary>
    internal void BeginFrame()
    {
        CollectionActive = false;
        PreparedParticles.Clear();
        // Only Valid is cleared: the payload stays in place and is never read without the flag, so a frame does not
        // memset ~21KB of light parameters that no consumer will look at.
        for (int i = 0; i < _snapshots.Length; i++) _snapshots[i].Valid = false;
    }

    internal void AdvanceFrame() => Index++;

    internal void Activate(ViewTargets targets) => Active = targets ?? throw new ArgumentNullException(nameof(targets));

    /// <summary>Records the backend's final lighting upload for the view that is currently active.</summary>
    internal void Capture(in SceneLightParams lights, in Matrix4x4 view, in Matrix4x4 projection,
        in Matrix4x4 unjitteredViewProjection)
    {
        _pending.Lights = lights;
        _pending.View = view;
        _pending.Projection = projection;
        _pending.UnjitteredViewProjection = unjitteredViewProjection;
        _pending.Valid = true;
        _pendingIndex = Active.Index;
        _pendingCaptured = true;
        _pendingApp = DeviceServices.BaseApp;
    }

    /// <summary>
    /// Moves the capture made for <paramref name="viewIndex"/> into that view's slot. Returns false when the backend
    /// captured nothing for this view, or when the capture belongs to another app (the snapshot must not survive an
    /// app switch, which is the same contract the previous single-slot implementation had).
    /// </summary>
    internal bool Promote(int viewIndex, BaseApp app)
    {
        if (!_pendingCaptured || _pendingIndex != viewIndex) return false;
        _pendingCaptured = false;
        if (!ReferenceEquals(_pendingApp, app)) return false;
        ref var slot = ref Snapshot(viewIndex);
        slot = _pending;
        return true;
    }

    /// <summary>
    /// Fills a view's snapshot from the view itself, for backends that upload the main view's lighting before the frame
    /// (so the main view never reaches PrepareView's capture) and for secondary views whose backend has no per-view
    /// upload point. The camera terms come from the view being rendered; the light set is scene-wide and is inherited
    /// from the main view's snapshot.
    /// </summary>
    internal void Derive(int viewIndex, RenderView view)
    {
        ref var slot = ref Snapshot(viewIndex);
        slot.View = view.View;
        slot.Projection = view.RenderProjection;
        slot.UnjitteredViewProjection = view.RenderViewProjection;
        slot.Lights = _snapshots[0].Lights;
        slot.Valid = view.Prepared && _snapshots[0].Valid;
    }

    /// <summary>Snapshot slot of one view. Out-of-range indexes throw rather than aliasing another view's state.</summary>
    public ref ViewSnapshot Snapshot(int viewIndex)
    {
        if ((uint)viewIndex >= (uint)_snapshots.Length)
            throw new ArgumentOutOfRangeException(nameof(viewIndex), viewIndex,
                $"View index exceeds FrameSchedule.MaxViews ({_snapshots.Length}).");
        return ref _snapshots[viewIndex];
    }

    /// <summary>Snapshot of the view being executed; every FrameSchedule lighting forwarder reads this.</summary>
    public ref ViewSnapshot ActiveSnapshot => ref Snapshot(Active.Index);
}
