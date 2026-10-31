// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// Render-target set and in-frame publish state owned by exactly one view.
///
/// The single-view era parked every one of these on static fields of <see cref="FrameSchedule"/>:
/// SceneColor / SceneDepth / SceneVelocity / SceneNormal, the fog and weather composite targets, PostColor,
/// plus "where did the scene composite land this frame" and "is the color source overridden by TAA".
/// That made "a frame contains one scene" an implicit assumption. Multiple cameras (main view + minimap /
/// rear mirror) and XR stereo need the whole pass chain to run more than once per frame, each with its own
/// attachments and publish state, so that state now lives here:
///
/// - Targets are written at registration time by each backend. The static members on
///   <see cref="FrameSchedule"/> are forwarders onto <see cref="FrameSchedule.ActiveTargets"/>, so backend
///   initialization code and every existing reader keep working unchanged.
/// - Frame-local state is reset by <see cref="BeginFrame"/> when <see cref="FrameSchedule.ExecuteView"/>
///   enters the view.
/// - Named compute resources are global in the backend texture dictionaries, so anything a view registers
///   under a fixed name must go through <see cref="Scope"/>. The main view gets no suffix, which keeps
///   every single-view registration name byte-identical to before.
/// </summary>
public sealed class ViewTargets
{
    public ViewTargets(int index, string name)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        Index = index;
        Name = string.IsNullOrEmpty(name) ? $"view{index}" : name;
    }

    /// <summary>View slot index; 0 is the main view. Matches <see cref="RenderView.Index"/>.</summary>
    public int Index { get; }

    /// <summary>Diagnostic name, used in logs and backend debug markers.</summary>
    public string Name { get; }

    /// <summary>
    /// Whether the view ends by presenting to the backbuffer. True for the main view only: secondary views
    /// (minimap, rear mirror, the second XR eye) keep their result in <see cref="SceneColor"/> so 2D controls
    /// and later composite passes can sample it. A non-presenting view requires an off-screen SceneColor.
    /// </summary>
    public bool Present { get; set; } = true;

    // -- Registration-time targets: created by the backend during initialization, never replaced at runtime.
    //    MatchBackbufferSize targets are rebuilt in place on resize, so these references stay valid. --

    public RenderTarget? SceneColor;
    public RenderTarget? SceneDepth;
    public RenderTarget? SceneVelocity;
    public RenderTarget? SceneNormal;

    /// <summary>Independent HDR target for the fog composite pass; null = single Scene route.</summary>
    public RenderTarget? FogCompositeTarget;

    /// <summary>Independent HDR target for the weather composite pass; null = single Scene route.</summary>
    public RenderTarget? WeatherCompositeTarget;

    /// <summary>LDR BackbufferCompatible output of the Post pass; null = the Post slot is unused for this view.</summary>
    public RenderTarget? PostColor;

    /// <summary>
    /// P3: presentation target of a non-presenting view. When <see cref="Present"/> is false and this is set, the
    /// FinalBlit of the view writes here instead of the backbuffer (tonemapped to the display range, exactly like the
    /// backbuffer would have been), which is what lets a 2D control sample the finished view as a texture.
    /// Null on the main view - it presents to the backbuffer.
    /// </summary>
    public RenderTarget? UiColor;

    // -- Frame-local publish state, reset by BeginFrame. --

    /// <summary>Complete scene color for this frame (after transparency and fog/weather); null = use SceneColor.</summary>
    public RenderTarget? CompleteSceneColor;

    /// <summary>Texture-dictionary name that replaces the scene source downstream (TAA resolve output).</summary>
    public string? SceneColorOverride;

    /// <summary>Set once this frame's fog injection, filtering and integration all completed.</summary>
    public bool FogOutputReady;

    /// <summary>Set once this frame's weather curtain produced its output.</summary>
    public bool WeatherOutputReady;

    /// <summary>Only after this frame's rain composite succeeded do transparent materials sample weather transmission.</summary>
    public bool WeatherComposited;

    /// <summary>This frame chose the opaque/transparent split route for this view.</summary>
    public bool SceneSegmented;

    /// <summary>
    /// Whether TAA resolve is actually active for this view's frame. Deliberately NOT cleared by
    /// <see cref="BeginFrame"/>: jitter is decided in <see cref="RenderView.Prepare"/> before the view
    /// executes, so the flag has to survive from the previous frame's resolve (one frame of lag, which is
    /// what the single-view path always did).
    /// </summary>
    public bool TaaActive;

    /// <summary>Downstream consumers read this; TAA itself only reads <see cref="SceneColor"/>.</summary>
    public RenderTarget? EffectiveSceneColor => CompleteSceneColor ?? SceneColor;

    /// <summary>Scene source as a compute reference, resolving the TAA override by name.</summary>
    public ComputeResourceRef EffectiveSceneSource => SceneColorOverride is string name
        ? new ComputeResourceRef { TextureName = name }
        : new ComputeResourceRef { Target = EffectiveSceneColor };

    /// <summary>Registered scene color width, or 0 when the view has no off-screen target.</summary>
    public uint Width => SceneColor?.Desc.Width ?? 0;

    /// <summary>Registered scene color height, or 0 when the view has no off-screen target.</summary>
    public uint Height => SceneColor?.Desc.Height ?? 0;

    /// <summary>
    /// Per-view name for a backend texture-dictionary resource. The main view returns the name unchanged so
    /// single-view registrations and their consumers keep byte-identical keys; every other view appends a
    /// suffix, letting two views own separate TAA histories (or any other named intermediate) in one frame.
    /// </summary>
    public string Scope(string name) => Index == 0 ? name : string.Concat(name, "#v", Index.ToString());

    /// <summary>Reset the frame-local publish state. Targets are untouched.</summary>
    public void BeginFrame()
    {
        CompleteSceneColor = null;
        SceneColorOverride = null;
        FogOutputReady = false;
        WeatherOutputReady = false;
        WeatherComposited = false;
        SceneSegmented = false;
    }

    /// <summary>Drop the published composite when it points at a target that is being retired or rebuilt.</summary>
    internal void ForgetSceneComposite(RenderTarget target)
    {
        if (ReferenceEquals(WeatherCompositeTarget, target)) WeatherComposited = false;
        if (!ReferenceEquals(CompleteSceneColor, target)) return;
        CompleteSceneColor = null;
        SceneColorOverride = null;
    }

    public override string ToString() => $"ViewTargets[{Index}:{Name}]";
}

/// <summary>
/// One entry of <see cref="FrameSchedule.Views"/>: the view to render, paired with the targets it renders into.
/// The list is empty by default, which is the single-view shape (the backend prepares the main view before the
/// frame and <see cref="FrameSchedule.PrimaryTargets"/> is the only target set).
/// </summary>
public sealed class ViewSlot
{
    public ViewSlot(RenderView view, ViewTargets targets)
    {
        View = view ?? throw new ArgumentNullException(nameof(view));
        Targets = targets ?? throw new ArgumentNullException(nameof(targets));
    }

    public RenderView View { get; }

    public ViewTargets Targets { get; }
}
