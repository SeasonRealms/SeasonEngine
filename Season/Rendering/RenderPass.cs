// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// Fixed pass slots. The enum order is the execution order (no Frame Graph).
/// Scene is always active; Shadow/Post are optional slots (wired in Step 3, populated by 1-5/later post effects), and empty slots are zero-cost.
/// </summary>
public enum RenderPassId
{
    Shadow,     // Enabled in 1-5 (depth-only)
    Scene,      // Main scene (all content for now; outputs linear HDR RT in 1-4 HDR mode, see RenderQuality)
    Post,       // Optional post-process slot (2-1 clause 4 / 2-3 clause 17: the tonemap(+bloom+AO) uber composite outputs LDR PostColor)
    OutlineMask, // Windows DX only: Outline2D object mask pass
    FinalBlit,  // Off-screen -> backbuffer (enabled in Step 2; HDR sources use the tonemap(+bloom) variant, and degrade to an LDR resolve when Post is active - see PostResolve and the RenderQuality 1-4 contract revision 1)
    Overlay,    // UI/debug overlay after final presentation; does not participate in global post-processing

    // Appended numbers: keep Web interop and existing pass values stable; execution order is decided by the scheduler.
    SceneOpaque = 6,
    FogComposite = 7,
    SceneTransparent = 8,
    WeatherComposite = 9,

    /// <summary>D1 screen-space decals: after the opaque draws finish, before the fog/rain-curtain composites and the transparent draws; writes SceneColor only.
    /// This pass has no color clear and no depth attachment (SceneDepth is a sampling source at this point), so the backend BeginPass must special-case it accordingly.</summary>
    SceneDecal = 10,

    /// <summary>P2 water surface: after decals, before transparent geometry. It likewise has no depth attachment (occlusion relies on a software depth test in the shader),
    /// but adds a pre-draw scene-color snapshot as the refraction source - this pass's color attachment cannot be sampled at the same time.</summary>
    SceneWater = 11,

    /// <summary>P0 screen-space reflection composite: after the opaque segment and decals, before the fog/rain-curtain composites.
    /// Unlike the fog composite, it opens no separate target: the delta is added directly into SceneColor, so there is no clear, no depth attachment,
    /// and no PublishSceneComposite - the later fog/rain-curtain stages read the scene color that already contains reflections.</summary>
    SsrComposite = 12,

    /// <summary>P1 waterfall curtain: after the water surface, before transparent geometry. Like the water surface it has no depth attachment (occlusion relies on a software depth test),
    /// but it needs no refraction snapshot - the curtain does not sample scene color, and its color attachment is itself the blend base.
    /// It is ordered after the water surface so the curtain at the impact point covers the pool edge; transparent primitives are still overlaid last.</summary>
    SceneWaterfall = 13,
}

/// <summary>RenderTarget pixel format (cross-platform abstraction mapped to native formats per backend).</summary>
public enum RtFormat
{
    None,
    BackbufferCompatible,
    Rgba16Float,
    D32Float,

    /// <summary>2-3: dual-channel half-float (rg16float), dedicated to motion vectors (UV-space velocity, including negative values).</summary>
    Rg16Float,
}

/// <summary>Off-screen RenderTarget descriptor. Unused in Step 1 (backbuffer only), effective from Step 2 onward.</summary>
public struct RenderTargetDesc
{
    public RtFormat ColorFormat;

    public RtFormat DepthFormat;

    /// <summary>The color target does not create the platform-compatible implicit depth; default false keeps the old attachment shape, and an explicit DepthFormat is unaffected.</summary>
    public bool NoImplicitDepth;

    /// <summary>When true, rebuild automatically with backbuffer resize; when false, use fixed Width/Height (shadowmap-style).</summary>
    public bool MatchBackbufferSize;

    public uint Width;

    public uint Height;

    /// <summary>MSAA sample count (1 = off), private to the Scene pass configuration.</summary>
    public uint SampleCount;

    /// <summary>
    /// Optional optimized clear value (cross-platform contract: used to bake D3D12 OptimizedClearValue at resource creation time).
    /// null = platform default (scene background color). RTs cleared with a non-background color
    /// (for example, OutlineMask always cleared to zero) must specify this explicitly,
    /// otherwise the D3D12 debug layer reports CLEARRENDERTARGETVIEW_MISMATCHINGCLEARVALUE every frame and falls back to a slow clear.
    /// </summary>
    public Vector4? ClearColor;
}

/// <summary>
/// Cross-platform opaque RenderTarget handle. Backend implementations own the real GPU resource; the shared layer only keeps the reference and Desc.
/// </summary>
public abstract class RenderTarget
{
    public RenderTargetDesc Desc;

    public abstract void Dispose();
}

/// <summary>
/// Begin parameters for a single render pass. Load/store intent must be explicit (critical for tiler GPU bandwidth).
///
/// Per-pass state contract (must be honored by all four backends and by pass authors, aligned to the strictest platform):
/// - No resource state switches inside the pass body: barrier/layout transitions may only happen in BeginPass/EndPass/backend binding APIs
///   (barriers are forbidden inside a Vulkan render-pass instance; D3D12/Metal are looser but follow the same rule here);
/// - No bindings are preserved across passes: each pass starts from a fresh encoding state, and the first draw inside the pass must rebind
///   pipeline/descriptors/vertex streams (Metal naturally resets bindings with a new encoder each pass; D3D12 command lists retain state but code must not depend on that);
/// - viewport/scissor are set by BeginPass from the target size, and pass contents may not override them;
///   the only exception (1-5 contract clause 6) is the Shadow pass, whose content may call controlled SetViewport per atlas quadrant slot
///   (the quadrant rect comes from CascadedShadow.GetAtlasViewport); all other passes keep the same restriction;
/// - Sampling-state transitions for pass input resources (upstream RTs) are handled by backend binding APIs (such as BlitToBackbuffer),
///   and content code does not touch barriers.
/// </summary>
public struct PassDesc
{
    public RenderPassId Id;

    /// <summary>Color target; null = backbuffer.</summary>
    public RenderTarget? ColorTarget;

    /// <summary>Depth target; null = the default depth paired with the color target.</summary>
    public RenderTarget? DepthTarget;

    /// <summary>2-3 contract clause 2: the second color target of the Scene pass (MRT slot 1, Rg16Float motion vector);
    /// null = single-target shape (VELOCITY_OUTPUT variant not baked), with zero leftovers in the pipeline. Only meaningful for the Scene pass.</summary>
    public RenderTarget? VelocityTarget;

    /// <summary>A0.2 contract clause 2: the third color target of the Scene pass (MRT slot 2, Rgba16Float octahedral normal +
    /// roughness + metallic); null = no NORMAL_OUTPUT variant baked. Only valid together with <see cref="VelocityTarget"/>, because the
    /// normal output exists only as an extension of the velocity shape; backends never bake a color+normal shape without velocity.
    /// Follows VelocityTarget's load/store and clear behavior exactly (cleared to zero with ClearColorEnable, loaded otherwise).</summary>
    public RenderTarget? NormalTarget;

    public Vector4 ClearColor;

    /// <summary>false = Load (preserve existing content).</summary>
    public bool ClearColorEnable;

    public bool ClearDepthEnable;

    /// <summary>Whether depth must be preserved after the pass ends (false maps to DontCare on tiler GPUs to save bandwidth).</summary>
    public bool StoreDepth;
}

/// <summary>
/// Fixed pass-chain scheduler: pass order and content belong to the shared layer, while pass Begin/End belong to the backend.
/// Step 1: Scene pass only (SceneColor=null -> render directly to the backbuffer, matching the legacy single-pass behavior).
/// Step 2: once SceneColor points to an off-screen RT, append FinalBlit automatically.
/// Step 3: Shadow/Post are optional slots; a pass activates only when both target and callback are non-null, and empty slots are zero-cost.
///
/// Lifecycle and resize conventions (1-4/1-5 integration guide):
/// - Slot RTs live for the whole app lifetime: create and register them during initialization
///   (after the backend graphics system is ready and before the frame loop); do not destroy or replace them at runtime;
/// - resize: RTs with MatchBackbufferSize=true are rebuilt in place by the backend during resize
///   (same wrapper object, same descriptor slot), so references held by this class remain valid; fixed-size RTs (shadow maps) are not affected by resize;
/// - callbacks run on the frame-loop thread; cross-thread registration/unregistration is forbidden; callbacks only draw pass content,
///   and must not open/close passes or touch barriers (see the per-pass state contract in PassDesc).
/// </summary>
public static class FrameSchedule
{
    /// <summary>Target set of the main view. Backends write the scene targets onto the static members below
    /// during initialization, and those members forward here, so every existing registration site keeps working.</summary>
    public static ViewTargets PrimaryTargets { get; } = new(0, "main");

    /// <summary>The view currently being executed. Every static target member in this class forwards onto it,
    /// which is what lets the same pass chain run twice in one frame without touching the ~250 existing readers.</summary>
    public static ViewTargets ActiveTargets { get; private set; } = PrimaryTargets;

    /// <summary>Views rendered after the main view (minimap, rear mirror, XR second eye). Empty by default, i.e. the
    /// single-view shape: the backend prepares the main view before the frame and <see cref="PrimaryTargets"/> is the
    /// only target set. Registering a view here is what turns the frame loop multi-view.</summary>
    public static readonly List<ViewSlot> Views = new();

    /// <summary>Hard limit on view slots, main view included. Backends size their per-view constant-buffer pools from
    /// this number, so it is the single source of truth: raising it here is what raises the limit everywhere.</summary>
    public const int MaxViews = 8;

    /// <summary>
    /// Lowest view index not used by <see cref="PrimaryTargets"/> or any registered view, or -1 when every slot is
    /// taken. Callers must hold the slot for the lifetime of their view: the slot indexes both the per-view constant
    /// buffers and the per-view texture scope suffix, so reusing one for two live views would alias them.
    /// </summary>
    public static int AllocateViewIndex()
    {
        for (int index = 1; index < MaxViews; index++)
        {
            bool used = false;
            for (int i = 0; i < Views.Count; i++)
            {
                if (Views[i].View.Index != index) continue;
                used = true;
                break;
            }
            if (!used) return index;
        }
        return -1;
    }

    /// <summary>Frames rendered so far. Owned by the scheduler because per-view pacing
    /// (<see cref="RenderView.UpdateInterval"/>) has to agree on what "this frame" means; backends only read it.</summary>
    public static ulong FrameIndex { get; private set; }

    /// <summary>
    /// Resets the frame index to zero. The only caller is the pixel-by-pixel A/B forensics hook.
    ///
    /// It is needed because this counter has been incrementing since process start, and the two runs take different
    /// times to load: even when the simulation time is aligned, the screenshot frame indices still differ by dozens.
    /// Anywhere the index is used as a noise seed (the SSR blue-noise offset is the typical case) the same scene is
    /// then drawn with a different noise pattern, and the pixel-by-pixel comparison is drowned in this "meaningless
    /// difference". The normal path never calls it.
    /// </summary>
    public static void ResetFrameIndex() => FrameIndex = 0;

    /// <summary>Feature flags of the view being executed right now. Read by <see cref="RecordPhase"/> so a compute
    /// effect that a view switched off is never even recorded.</summary>
    public static ViewFeature ActiveFeatures { get; private set; } = ViewFeature.All;

    /// <summary>Shadow policy of the view being executed right now. Read by the backends at shadow-binding time, which
    /// happens while the view's draws are collected, i.e. after the scheduler set this.</summary>
    public static ViewShadowPolicy ActiveShadowPolicy { get; private set; } = ViewShadowPolicy.Shared;

    /// <summary>Invoked once after the last view of the frame, before the Overlay pass. Backends use it to restore
    /// frame-wide state that the per-view loop left bound (on DX: which view's constant buffer is active).</summary>
    public static Action<IGraphics>? AfterViews;

    /// <summary>View preparation hook: the backend uploads that view's constants (uniform buffer slot, TAA state, ...)
    /// right before its pass chain starts. Invoked for every view, the main one included, so a backend never has to
    /// duplicate the upload outside the loop; leaving it unregistered is the zero-cost single-view shape.</summary>
    public static Action<IGraphics, RenderView>? PrepareView;

    /// <summary>Attach a view to its target set and render it after the main view. Registering the same view again
    /// replaces its target set in place, keeping the render order stable.</summary>
    public static ViewTargets AddView(RenderView view, ViewTargets targets)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(targets);
        for (int i = 0; i < Views.Count; i++)
        {
            if (!ReferenceEquals(Views[i].View, view)) continue;
            Views[i] = new ViewSlot(view, targets);
            view.Targets = targets;
            return targets;
        }
        Views.Add(new ViewSlot(view, targets));
        view.Targets = targets;
        return targets;
    }

    public static bool RemoveView(RenderView view)
    {
        for (int i = 0; i < Views.Count; i++)
        {
            if (!ReferenceEquals(Views[i].View, view)) continue;
            var targets = Views[i].Targets;
            Views.RemoveAt(i);
            if (ReferenceEquals(view.Targets, targets)) view.Targets = null;
            // The view's transients die with it: nothing else walks a slot that has left the list.
            ReleaseViewTransients(targets);
            return true;
        }
        return false;
    }

    /// <summary>Drop every additional view (back to the single-view shape).</summary>
    public static void ClearViews()
    {
        for (int i = 0; i < Views.Count; i++)
        {
            Views[i].View.Targets = null;
            ReleaseViewTransients(Views[i].Targets);
        }
        Views.Clear();
    }

    /// <summary>Target set registered for <paramref name="view"/>, or null when the view is not registered.</summary>
    public static ViewTargets? TargetsOf(RenderView view)
    {
        for (int i = 0; i < Views.Count; i++)
            if (ReferenceEquals(Views[i].View, view)) return Views[i].Targets;
        return null;
    }

    /// <summary>Runs <paramref name="action"/> on the main view's targets and then on every registered view's targets.
    /// For effects that have to revoke their per-view publications (an override name, a ready flag) on retirement.</summary>
    internal static void ForEachTargets(Action<ViewTargets> action)
    {
        action(PrimaryTargets);
        for (int i = 0; i < Views.Count; i++) action(Views[i].Targets);
    }

    /// <summary>Off-screen color target for the Scene pass; null = direct render to backbuffer (Step 1 shape).</summary>
    public static RenderTarget? SceneColor
    {
        get => ActiveTargets.SceneColor;
        set => ActiveTargets.SceneColor = value;
    }

    public static WeatherVisualState? WeatherVisuals;

    public static RenderTarget? WeatherCompositeTarget
    {
        get => ActiveTargets.WeatherCompositeTarget;
        set => ActiveTargets.WeatherCompositeTarget = value;
    }
    public static Func<IGraphics, bool>? PrepareWeatherComposite;
    public static Func<IGraphics, RenderTarget, FogCompositeResult>? RenderWeatherComposite;

    public static bool WeatherOutputReady
    {
        get => ActiveTargets.WeatherOutputReady;
        set => ActiveTargets.WeatherOutputReady = value;
    }

    /// <summary>Transparent materials sample weather transmission only after this frame's rain-curtain composite has succeeded.</summary>
    public static bool WeatherComposited => ActiveTargets.WeatherComposited;

    /// <summary>Complete scene color for this frame (including transparency and fog); falls back to the original SceneColor when not published.</summary>
    public static RenderTarget? CompleteSceneColor => ActiveTargets.CompleteSceneColor;

    public static RenderTarget? EffectiveSceneColor => ActiveTargets.EffectiveSceneColor;

    /// <summary>The independent HDR composite target and backend draw entry registered by the fog effect; when either is missing the single Scene route is kept.</summary>
    public static RenderTarget? FogCompositeTarget
    {
        get => ActiveTargets.FogCompositeTarget;
        set => ActiveTargets.FogCompositeTarget = value;
    }

    public static Func<IGraphics, RenderTarget, FogCompositeResult>? RenderFogComposite;
    /// <summary>Prepares read-only resources after all AfterOpaque effects have finished and before the composite pass opens; false skips the composite.</summary>
    public static Func<IGraphics, bool>? PrepareFogComposite;

    /// <summary>
    /// The composite entry for P0 screen-space reflections, attached by <see cref="Effects.SsrEffect"/> after a successful
    /// registration and detached on retirement. The scene pass is split only when both delegates are non-null - unlike the
    /// fog composite, SSR can only rewrite the opaque segment, and transparent primitives must be drawn after the
    /// reflections (their color should neither be treated as a reflection source nor be added in a second time).
    /// null means this backend has no additive composite capability; SSR then does not register at all and the image falls back to sky reflections.
    /// </summary>
    public static Func<IGraphics, bool>? PrepareSsrComposite;
    public static Func<IGraphics, bool>? RenderSsrComposite;

    /// <summary>Set to true only when this frame's reflection delta has been published; cleared automatically at the start of each frame.</summary>
    public static bool SsrOutputReady
    {
        get => ActiveTargets.SsrOutputReady;
        set => ActiveTargets.SsrOutputReady = value;
    }

    /// <summary>Set to true only after this frame's injection, filtering and integration have all completed; cleared automatically at the start of each frame.</summary>
    public static bool FogOutputReady
    {
        get => ActiveTargets.FogOutputReady;
        set => ActiveTargets.FogOutputReady = value;
    }

    /// <summary>
    /// The D1 screen-space decal entry. Registration happens during initialization (the backend creates the PSO and root
    /// signature at that point); thereafter the scheduler calls Prepare/Draw once each, before and after
    /// <see cref="RenderPassId.SceneDecal"/>, every frame. null means this backend does not implement it, in which case the
    /// scene pass is not split even when decals are registered - decals with no renderer mean "not displayed", not "drawn in the wrong place".
    /// </summary>
    public static DecalEffect? DecalRenderer { get; private set; }

    /// <summary>
    /// The backend entry for the P2 water-surface material. The contract matches <see cref="DecalRenderer"/>: null means this
    /// backend does not implement it, in which case the scene pass is not split even when water surfaces are registered -
    /// water with no renderer means "not displayed", not "drawn in the wrong place".
    /// </summary>
    public static WaterEffect? WaterRenderer { get; private set; }

    /// <summary>
    /// The scene-color snapshot used for water refraction. Its lifetime follows <see cref="RegisterWaters"/> /
    /// <see cref="UnregisterWaters"/>, it is rebuilt in sync with SceneColor inside <see cref="ResizeCompute"/>, and
    /// it is per view (see <see cref="ViewTargets.WaterRefractionTarget"/>): the snapshot has to match the shape of
    /// the scene color it refracts, and a minimap is not full-screen.
    /// It is strictly a separate resource from SceneColor: in the water pass SceneColor is the color attachment, and
    /// the same resource cannot be sampled at the same time.
    /// </summary>
    public static RenderTarget? WaterRefractionTarget => ActiveTargets.WaterRefractionTarget;

    /// <summary>
    /// The backend entry for the P1 waterfall curtain. The contract matches <see cref="WaterRenderer"/>: null means this
    /// backend does not implement it, in which case the scene pass is not split even when curtains are registered. Unlike
    /// the water surface it has no refraction snapshot, so there is no extra "snapshot not built" failure path.
    /// </summary>
    public static WaterfallEffect? WaterfallRenderer { get; private set; }

    /// <summary>
    /// The backend entry for the P1 river ribbon. Its contract is exactly <see cref="WaterfallRenderer"/>'s - it is the same
    /// Pass, the same resources and the same frame buffer layout, only the shader differs - so a null here simply means the river
    /// is data with nothing drawing it.
    /// </summary>
    public static WaterRibbonEffect? RiverRenderer { get; private set; }

    /// <summary>A0.1 particle indirect-draw entry. Prepare hands over the parameters and resolves resources outside the scene
    /// pass, and returning false means this frame gives up submission; Submit submits once at the end of the scene's transparent
    /// stage (right after the full Scene draw when not segmented), adding neither a pass nor an attachment.
    /// Particles therefore always land after all transparent primitives, an accepted sorting compromise: interleaving with
    /// SceneDrawQueue would require reading back the GPU-determined instance count in advance.
    ///
    /// This is a table rather than a single slot: rain, snow and smoke are each an independent particle system holding its
    /// own buffers and indirect parameter block, submitted in registration order when they coexist on screen - their relative
    /// order is therefore deterministic, though the whole group still follows the transparent primitives.</summary>
    public static readonly List<IParticleSubmitter> ParticleSubmitters = new();

    // The subset that prepared successfully this frame. Prepare is outside the pass and Submit is inside it, with
    // BeginPass in between, so "who handed over successfully" has to be recorded: asking again inside the pass would
    // break Prepare's one-shot semantics.
    static readonly List<IParticleSubmitter> _preparedParticles = new();

    /// <summary>Hands over the registered particle systems one by one, returning whether at least one must submit inside this
    /// pass. Called by the frame loop before BeginPass; the acceptance project uses it to drive a single frame.</summary>
    public static bool PrepareParticles(IGraphics g, RenderTarget? color)
    {
        _preparedParticles.Clear();
        if (color == null) return false;
        for (int i = 0; i < ParticleSubmitters.Count; i++)
            if (ParticleSubmitters[i].Prepare(g, color)) _preparedParticles.Add(ParticleSubmitters[i]);
        return _preparedParticles.Count > 0;
    }

    /// <summary>Submits the particle systems handed over successfully this frame and clears the table immediately, so each
    /// Prepare honors at most one submission. Returns the number of systems actually drawn. Failure is not an exception:
    /// BeginPass sits between Prepare and Draw, and the backend may re-verify identity inside the pass; each system logs
    /// its own state transitions on failure, and this only aggregates.</summary>
    public static int SubmitParticles(IGraphics g)
    {
        int drawn = 0;
        for (int i = 0; i < _preparedParticles.Count; i++)
            if (_preparedParticles[i].Submit(g)) drawn++;
        _preparedParticles.Clear();
        return drawn;
    }

    public static bool SceneCollectionActive { get; private set; }
    /// <summary>Hierarchical sort context for segmented collection; primitive commands capture Current and must not keep control references.</summary>
    public static SceneDrawOrderContext SceneDrawOrder { get; } = new();

    /// <summary>This frame has chosen the opaque/transparent split; the fog compute publishes its output only on this route.</summary>
    public static bool SceneSegmented => ActiveTargets.SceneSegmented;

    /// <summary>Consumers after TAA use this entry; TAA itself only reads EffectiveSceneColor.</summary>
    public static ComputeResourceRef EffectiveSceneSource => ActiveTargets.EffectiveSceneSource;

    /// <summary>AO enters final post-processing only when it was generated this frame and has not yet been consumed in the HDR composite.</summary>
    public static bool AoReady { get; internal set; }
    public static bool AoComposited { get; private set; }
    public static string? PostAoTexture => AoReady && !AoComposited ? AoTexture : null;

    /// <summary>Must be published after the independent HDR target has finished writing; the input target must not be used as the composite output.</summary>
    public static void PublishSceneComposite(RenderTarget target, bool consumedAo)
    {
        ArgumentNullException.ThrowIfNull(target);
        var source = SceneColor ?? throw new InvalidOperationException("Scene composite is missing SceneColor.");
        if (ReferenceEquals(source, target) || ReferenceEquals(CompleteSceneColor, target)
            || target.Desc.ColorFormat != RtFormat.Rgba16Float
            || target.Desc.SampleCount > 1 || target.Desc.Width != source.Desc.Width
            || target.Desc.Height != source.Desc.Height)
            throw new ArgumentException("The scene composite must use an independent single-sampled HDR target with matching dimensions.", nameof(target));
        if (consumedAo && !AoReady)
            throw new InvalidOperationException("Cannot consume AO that has not been generated yet this frame.");
        ActiveTargets.CompleteSceneColor = target;
        AoComposited |= consumedAo;
    }

    /// <summary>When an effect retires or rebuilds its target, immediately revoke the effective color source pointing at the old target (every view must be cleared).</summary>
    internal static void ForgetSceneComposite(RenderTarget target)
    {
        bool live = Retire(PrimaryTargets);
        for (int i = 0; i < Views.Count; i++) live |= Retire(Views[i].Targets);
        if (live) AoComposited = false;

        bool Retire(ViewTargets targets)
        {
            bool hadComposite = ReferenceEquals(targets.CompleteSceneColor, target);
            targets.ForgetSceneComposite(target);
            return hadComposite;
        }
    }

    static SceneLightParams _pendingLights, _frameLights;
    static Matrix4x4 _pendingView, _pendingProjection, _pendingUnjitteredViewProjection;
    static BaseApp? _pendingLightingApp;
    static bool _lightingPending;

    /// <summary>The lighting copy finally uploaded by the backend, containing this frame's light order, CSM, spot, ambient and cloud parameters.</summary>
    public static ref readonly SceneLightParams LightingSnapshot => ref _frameLights;
    public static bool LightingSnapshotValid { get; private set; }
    public static Matrix4x4 RenderView { get; private set; }
    public static Matrix4x4 RenderProjection { get; private set; }
    public static Matrix4x4 UnjitteredRenderViewProjection { get; private set; }

    /// <summary>Called by the backend only at the final lighting upload point; does not modify the shared light UBO.</summary>
    public static void CaptureLighting(in SceneLightParams lights, in Matrix4x4 view,
        in Matrix4x4 projection, in Matrix4x4 unjitteredViewProjection)
    {
        _pendingLights = lights;
        _pendingView = view;
        _pendingProjection = projection;
        _pendingUnjitteredViewProjection = unjitteredViewProjection;
        _pendingLightingApp = DeviceServices.BaseApp;
        _lightingPending = true;
    }

    static void BeginSceneFrame(BaseApp app)
    {
        // Per-view state is reset by ViewTargets.BeginFrame inside ExecuteView; here only the frame-level (view-independent) state is reset.
        AoReady = false;
        AoComposited = false;
        SceneCollectionActive = false;
        ActiveTargets = PrimaryTargets;
        // Outside a view the whole layer set is visible: the shadow atlas is frame-wide, and diagnostics/tests that
        // draw without entering a view must never be culled by whichever mask ran last.
        Season.Rendering.RenderView.CurrentLayerMask = uint.MaxValue;
        ActiveShadowPolicy = ViewShadowPolicy.Shared;
        LightingSnapshotValid = _lightingPending && ReferenceEquals(_pendingLightingApp, app);
        _frameLights = LightingSnapshotValid ? _pendingLights : default;
        RenderView = LightingSnapshotValid ? _pendingView : default;
        RenderProjection = LightingSnapshotValid ? _pendingProjection : default;
        UnjitteredRenderViewProjection = LightingSnapshotValid ? _pendingUnjitteredViewProjection : default;
        _lightingPending = false;
        _pendingLightingApp = null;
    }

    /// <summary>Explicit depth target for the Scene pass (2-2 contract clause 2: full-size depth-only D32Float, registered by the backend
    /// during initialization in AO mode, replacing the global default depth and forcing StoreDepth=true so the AfterScene compute phase
    /// can consume it through a DepthTexture binding); null = reuse the backend default depth, with zero leftovers in the pipeline.</summary>
    public static RenderTarget? SceneDepth
    {
        get => ActiveTargets.SceneDepth;
        set => ActiveTargets.SceneDepth = value;
    }

    /// <summary>Motion-vector target for the Scene pass (2-3 contract clause 2: full-size MatchBackbufferSize Rg16Float,
    /// created and registered by the backend during initialization in MotionVectors mode as Scene pass MRT slot 1; non-null means the main shader
    /// already baked the VELOCITY_OUTPUT variant); null = single-target shape, with zero leftovers in the pipeline.</summary>
    public static RenderTarget? SceneVelocity
    {
        get => ActiveTargets.SceneVelocity;
        set => ActiveTargets.SceneVelocity = value;
    }

    /// <summary>Scene-normal target for the Scene pass (A0.2 contract clause 2: full-size MatchBackbufferSize Rgba16Float with a zero
    /// optimized clear, created by the backend during initialization when RenderQuality.EffectiveSceneNormals holds, as Scene pass MRT slot 2).
    /// Non-null implies <see cref="SceneVelocity"/> is non-null and the main shader baked NORMAL_OUTPUT; null = no residue in the pipeline.
    /// Layout: xy = octahedral-encoded world normal in [-1,1], z = Toksvig-corrected roughness,
    /// w = the scalar reflection weight the SSR delta is built from (Rec.709 luminance of F0 times ao, and 0 on
    /// the envCube path where SSR has no base term to replace). Texels with no opaque surface
    /// (sky, background, transparent-only pixels) keep the zero clear, but zero xy is also a legal encoding (+Z), so consumers must tell
    /// "no surface" apart through SceneDepth at the far plane, never through the normal itself.</summary>
    public static RenderTarget? SceneNormal
    {
        get => ActiveTargets.SceneNormal;
        set => ActiveTargets.SceneNormal = value;
    }

    // -- Step 3 empty slots: activate only after the target and callback are registered as a pair; otherwise they stay zero-cost. --

    /// <summary>Depth-only target of the Shadow pass (D32Float shadow map); registered by 1-5 CSM.</summary>
    public static RenderTarget? ShadowMap;

    /// <summary>Shadow pass draw content (depth-only draw); registered by 1-5 CSM.</summary>
    public static Action<IGraphics>? RenderShadow;

    /// <summary>Output target of the Post pass (the input is always SceneColor, so this depends on SceneColor being non-null).
    /// Registered by the backend during initialization as an LDR BackbufferCompatible target holding the finished uber composite
    /// with luma baked into alpha. Two tiers register it and they are mutually exclusive: 2-1 clause 4 (FXAA) and 2-3 clause 17
    /// (TAA with TaaSharpness above zero). In both cases the reason is the same - moving the HDR-&gt;LDR composite upstream of
    /// FinalBlit is what gives a full-screen filter something display-referred to read, which neither FXAA nor RCAS can do
    /// against scene-referred HDR. Which filter FinalBlit then applies is decided by <see cref="PostResolve"/>.</summary>
    public static RenderTarget? PostColor
    {
        get => ActiveTargets.PostColor;
        set => ActiveTargets.PostColor = value;
    }

    /// <summary>Post pass draw content (fullscreen post-processing, with SceneColor as input; sampling-state transitions of the input are handled by backend binding APIs);
    /// this is the backend uber composite (tonemap + bloom + AO), registered together with PostColor by whichever tier owns the slot.
    /// It resolves <see cref="SceneColorOverride"/> itself, so the TAA tier reaches it correctly: the Post pass runs after the
    /// AfterScene phase, by which point TaaEffect.Record has already published the current frame's resolve output.</summary>
    public static Action<IGraphics, RenderTarget>? RenderPost;

    /// <summary>2-1 Step B: backend texture-dictionary registration name of the bloom-chain output texture (null = no bloom, so FinalBlit uses the existing variant with zero leftovers).
    /// Written after BloomEffect.Initialize succeeds and cleared on Dispose; backend FinalBlit (the tonemap variant side) resolves it by name and adds it in linear space before ACES
    /// (scene + bloom x RenderQuality.BloomIntensity, see the RenderQuality 2-1 contract).</summary>
    public static string? BloomTexture;

    /// <summary>2-2 Step B (contract clause 5): backend texture-dictionary registration name of the GTAO-chain output texture (null = no AO,
    /// so the composite path uses the existing variant with zero leftovers, same shape as BloomTexture). Written after GtaoEffect.Initialize succeeds
    /// and cleared on Dispose; the backend resolves it by name at the final HDR->LDR composite point before presentation and multiplies it in linear space before ACES
    /// (scene x lerp(1, ao, RenderQuality.AoIntensity), see the RenderQuality 2-2 contract).</summary>
    public static string? AoTexture;

    /// <summary>2-3 contract clause 12: downstream scene-source override (backend texture-dictionary registration name, same shape as BloomTexture).
    /// When non-null, the scene source for both bloom input and the final HDR->LDR composite point before presentation
    /// is always replaced with this texture, wherever that point currently lives: FinalBlit when the Post slot is unused, or the Post uber pass when a tier
    /// registered it (2-1 clause 4 FXAA, 2-3 clause 17 sharpening). SceneColor RT itself remains the render target of the Scene pass
    /// (so Execute in this class does not need to change either pass scheduling or blitSource selection, and the override is resolved by name at the backend composite entry).
    /// Written every frame by TaaEffect (the ping-pong writer for the current frame, see clause 11), and set to null when bypassed or disposed;
    /// null = the whole chain falls back to SceneColor, with zero leftovers.
    /// Per-view: stored on <see cref="ViewTargets"/>, so two views in one frame keep separate resolved sources.</summary>
    public static string? SceneColorOverride
    {
        get => ActiveTargets.SceneColorOverride;
        set => ActiveTargets.SceneColorOverride = value;
    }

    /// <summary>2-3 contract clauses 14/15: whether TAA resolve is actually active for this frame (set every frame by TaaEffect).
    /// This gates jitter injection: jitter without resolve is worse than no jitter, so RenderView.Prepare only bakes in the subpixel offset
    /// when AaMode.Taa is selected and this value is true; it stays false when registration fails or when resize causes a size mismatch and the effect bypasses itself.
    /// Per-view: a view reads its own registration through <see cref="RenderView.Targets"/>.</summary>
    public static bool TaaActive
    {
        get => ActiveTargets.TaaActive;
        set => ActiveTargets.TaaActive = value;
    }

    /// <summary>The view whose matrices the scene is currently being prepared / drawn with. Set by each backend right
    /// after it prepares the view at its per-frame camera update point; read through <see cref="Season.Rendering.RenderView.Current"/>.
    /// Null before the first prepared frame (Current then falls back to the main view).</summary>
    public static Season.Rendering.RenderView? CurrentView { get; set; }

    /// <summary>2-5 Step A: backend texture-dictionary registration name of the procedural-sky Sky-View LUT (null = the procedural tier is not active,
    /// so the app-side skybox falls back to static cube textures, with zero leftovers; same shape as BloomTexture). Written after SkyAtmosphereEffect.Initialize
    /// succeeds and cleared on Dispose; this is the **only criterion** for whether procedural sky is available, so the app layer uses it during construction to decide
    /// whether the skybox faces should use the LUT (Surface.ProceduralSky=true, PS renderMode==3 reconstructs LUT uv from world view direction)
    /// or static face textures. Therefore the backend degrades automatically when shader sources are missing, compute is unsupported, or the quality tier is StaticCube.</summary>
    public static string? SkyViewTexture;

    /// <summary>2-5 Step C: backend texture-dictionary registration name of the prebaked cloud-noise texture (same shape as <see cref="SkyViewTexture"/>).
    /// This is the **only criterion** for whether procedural clouds are available: <c>SkyLighting.Apply</c> only uploads cloud parameters when it is non-null;
    /// otherwise <c>CloudParams0.w</c> (layer count) stays 0 so the consumer side skips everything.
    /// This gate is required and cannot rely on layer count alone: DX must bind t14 on every draw, and the fallback texture before readiness is 1x1 white;
    /// feeding white noise into density remapping is equivalent to maxing out density, which produces a solid dead-gray fake overcast sky.</summary>
    public static string? CloudNoiseTexture;

    /// <summary>2-5 Step E: backend **3D** texture-dictionary registration name of the aerial-perspective 3D LUT (same shape as the two entries above,
    /// but it points into the 3D dictionary, so backends must not resolve it from the 2D dictionary; see 1-8).
    /// This is the **only criterion** for whether aerial perspective is available: <c>SkyLighting.ApplyAerial</c> only uploads <c>ApParams0</c> when it is non-null;
    /// otherwise it explicitly writes zeros so the consumer side skips everything.
    /// Unlike cloud noise, this gate is **not required for correctness** (the DX fallback is a 1x1x1 zero-filled volume, which is the identity element in the composite formula);
    /// it only saves one meaningless trilinear sample.</summary>
    public static string? AerialLutTexture;

    // -- 1-6 compute-phase registry: kernel registration model (no "main shader", all kernels are peers), and effects attach to fixed points in the frame;
    // an empty table is zero-cost (for iteration with no enumerator allocation); registration/unregistration is restricted to the frame-loop thread, matching the callback contract in this class. --

    static readonly List<ComputeEffect> _computeFrameStart = new();
    static readonly List<ComputeEffect> _computeAfterOpaque = new();
    static readonly List<ComputeEffect> _computeAfterScene = new();

    static List<ComputeEffect> EffectsFor(ComputePhase phase) => phase switch
    {
        ComputePhase.FrameStart => _computeFrameStart,
        ComputePhase.AfterOpaque => _computeAfterOpaque,
        ComputePhase.AfterScene => _computeAfterScene,
        _ => throw new ArgumentOutOfRangeException(nameof(phase)),
    };

    /// <summary>
    /// Register a compute effect: return false when the backend does not support it or Initialize fails
    /// (missing shader sources / compilation failure on this backend), leaving zero leftovers and no entry in the table; on success it Records automatically every frame according to its phase.
    /// Must be called after backend graphics are ready and before the frame loop starts.
    /// </summary>
    public static bool RegisterCompute(IGraphics g, ComputeEffect effect)
    {
        var effects = EffectsFor(effect.Phase);
        if (_computeFrameStart.Contains(effect) || _computeAfterOpaque.Contains(effect)
            || _computeAfterScene.Contains(effect))
            return true;
        if (!g.ComputeSupported) return false;
        if (!effect.Initialize(g)) return false;
        effects.Add(effect);
        return true;
    }

    public static void UnregisterCompute(ComputeEffect effect)
    {
        bool removed = _computeFrameStart.Remove(effect) | _computeAfterOpaque.Remove(effect) | _computeAfterScene.Remove(effect);
        if (removed) effect.OnUnregistered();
    }

    /// <summary>
    /// D1: establishes the decal render entry. The preconditions are that <see cref="RenderQuality"/> declares decals, the
    /// scene color is single-sampled HDR, and an explicit SceneDepth exists - the last one also guarantees the depth can be
    /// read as a sampling source during the AfterOpaque phase. If any precondition is missing, returns false leaving zero
    /// leftovers, consistent with the <see cref="RegisterCompute"/> contract.
    /// </summary>
    public static bool RegisterDecals(IGraphics g)
    {
        if (DecalRenderer != null) return true;
        if (!RenderQuality.Current.Decals || Immediate2DMode.Enabled) return false;
        if (SceneColor is not { Desc.ColorFormat: RtFormat.Rgba16Float } color
            || color.Desc.SampleCount > 1 || SceneDepth == null) return false;
        var pipeline = g.CreateDecalPipeline();
        if (pipeline == null) return false;
        DecalRenderer = new DecalEffect(g, pipeline);
        return true;
    }

    public static void UnregisterDecals()
    {
        var effect = DecalRenderer;
        if (effect == null) return;
        DecalRenderer = null;
        effect.Dispose();
        // Secondary views own their own frame blocks, which nothing would reach once the renderer is gone.
        for (int i = 0; i < Views.Count; i++) ReleaseViewTransients(Views[i].Targets);
    }

    /// <summary>
    /// P2: establishes the water-surface render entry and the main view's refraction snapshot. The preconditions match decals: the scene
    /// color is single-sampled HDR and an explicit SceneDepth exists; it additionally requires that a snapshot target with
    /// the same shape as SceneColor can be created. If any precondition is missing, returns false leaving zero leftovers.
    /// </summary>
    public static bool RegisterWaters(IGraphics g)
    {
        if (WaterRenderer != null) return true;
        if (Immediate2DMode.Enabled) return false;
        if (SceneColor is not { Desc.ColorFormat: RtFormat.Rgba16Float } color
            || color.Desc.SampleCount > 1 || SceneDepth == null) return false;
        var pipeline = g.CreateWaterPipeline();
        if (pipeline == null) return false;
        var effect = new WaterEffect(g, pipeline);
        bool ready;
        try { ready = EnsureWaterRefractionTarget(g, PrimaryTargets, color); }
        catch (Exception ex)
        {
            DeviceServices.BaseApp.AddLog(LogType.Error, $"[Water] Failed to create snapshot target: {ex.Message}");
            ready = false;
        }
        if (!ready) { effect.Dispose(); ReleaseWaterRefractionTarget(PrimaryTargets); return false; }
        WaterRenderer = effect;
        return true;
    }

    public static void UnregisterWaters()
    {
        var effect = WaterRenderer;
        if (effect == null) return;
        WaterRenderer = null;
        effect.Dispose();
        ReleaseWaterRefractionTarget(PrimaryTargets);
        // Secondary views own their own snapshots and frame blocks, which nothing would reach once the renderer is gone.
        for (int i = 0; i < Views.Count; i++) ReleaseViewTransients(Views[i].Targets);
    }

    /// <summary>
    /// Releases the per-view transients the shared pass chain allocated on a view's behalf: the water refraction
    /// snapshot, the water / waterfall / river / decal frame blocks, and every particle transient this view holds on
    /// behalf of the submitters that drew into it. Idempotent, so every owner can call it on the way out.
    /// </summary>
    internal static void ReleaseViewTransients(ViewTargets targets)
    {
        ReleaseWaterRefractionTarget(targets);
        targets.WaterFrameBuffer?.Dispose();
        targets.WaterFrameBuffer = null;
        targets.WaterfallFrameBuffer?.Dispose();
        targets.WaterfallFrameBuffer = null;
        targets.RiverFrameBuffer?.Dispose();
        targets.RiverFrameBuffer = null;
        targets.DecalFrameBuffer?.Dispose();
        targets.DecalFrameBuffer = null;
        targets.ReleaseParticleBuffers();
    }

    /// <summary>
    /// Ensures <paramref name="targets"/> owns a refraction snapshot matching the shape of <paramref name="color"/>,
    /// rebuilding it when the shape moved (resize) and allocating it on first use (a secondary view's first water frame).
    /// Must be called outside a pass: creating a target mid-chain is only legal between passes.
    /// </summary>
    internal static bool EnsureWaterRefractionTarget(IGraphics g, ViewTargets targets, RenderTarget color)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(color);
        var current = targets.WaterRefractionTarget;
        // Compared against the format this path creates (HDR) rather than the source's: a scene color in any other
        // format would otherwise mismatch on every frame and rebuild the snapshot each time. A non-HDR source simply
        // fails the pipeline's shape check and draws no water, which is the same outcome as no snapshot.
        if (current != null && current.Desc.Width == color.Desc.Width && current.Desc.Height == color.Desc.Height
            && current.Desc.ColorFormat == RtFormat.Rgba16Float) return true;
        ReleaseWaterRefractionTarget(targets);
        var target = g.CreateRenderTarget(new RenderTargetDesc {
            ColorFormat = RtFormat.Rgba16Float, DepthFormat = RtFormat.None, SampleCount = 1, NoImplicitDepth = true,
            Width = color.Desc.Width, Height = color.Desc.Height,
            MatchBackbufferSize = color.Desc.MatchBackbufferSize, ClearColor = Vector4.Zero });
        if (target == null) return false;
        targets.WaterRefractionTarget = target;
        return true;
    }

    /// <summary>
    /// <see cref="EnsureWaterRefractionTarget"/> for the render path: a failure is logged and the frame draws no water
    /// rather than unwinding the whole view, which is the same outcome as "this view has no snapshot".
    /// </summary>
    static bool TryEnsureWaterRefractionTarget(IGraphics g, ViewTargets targets, RenderTarget color)
    {
        try { return EnsureWaterRefractionTarget(g, targets, color); }
        catch (Exception ex)
        {
            DeviceServices.BaseApp.AddLog(LogType.Error, $"[Water] Failed to create snapshot for {targets}: {ex.Message}");
            ReleaseWaterRefractionTarget(targets);
            return false;
        }
    }

    static void ReleaseWaterRefractionTarget(ViewTargets targets)
    {
        targets.WaterRefractionTarget?.Dispose();
        targets.WaterRefractionTarget = null;
    }

    /// <summary>
    /// P1: establishes the waterfall-curtain render entry. The preconditions match decals (single-sampled HDR scene color +
    /// explicit SceneDepth), but there is no refraction snapshot, so it has one fewer failure path than the water surface.
    /// If any precondition is missing, returns false leaving zero leftovers.
    /// </summary>
    public static bool RegisterWaterfalls(IGraphics g)
    {
        if (WaterfallRenderer != null) return true;
        if (Immediate2DMode.Enabled) return false;
        if (SceneColor is not { Desc.ColorFormat: RtFormat.Rgba16Float } color
            || color.Desc.SampleCount > 1 || SceneDepth == null) return false;
        var pipeline = g.CreateWaterfallPipeline();
        if (pipeline == null) return false;
        WaterfallRenderer = new WaterfallEffect(g, pipeline);
        return true;
    }

    /// <summary>Detaches the curtain entry and releases the backend pipeline; a no-op when it was never registered.</summary>
    public static void UnregisterWaterfalls()
    {
        var effect = WaterfallRenderer;
        if (effect == null) return;
        WaterfallRenderer = null;
        effect.Dispose();
    }

    /// <summary>
    /// P1: establishes the river-ribbon render entry. The preconditions are the curtain's, because the two share the Pass and the
    /// resource shape; if any of them is missing this returns false and leaves zero leftovers.
    /// </summary>
    public static bool RegisterWaterRivers(IGraphics g)
    {
        if (RiverRenderer != null) return true;
        if (Immediate2DMode.Enabled) return false;
        if (SceneColor is not { Desc.ColorFormat: RtFormat.Rgba16Float } color
            || color.Desc.SampleCount > 1 || SceneDepth == null) return false;
        var pipeline = g.CreateWaterRibbonPipeline();
        if (pipeline == null) return false;
        RiverRenderer = new WaterRibbonEffect(g, pipeline);
        return true;
    }

    /// <summary>Detaches the river entry and releases the backend pipeline; a no-op when it was never registered.</summary>
    public static void UnregisterWaterRivers()
    {
        var effect = RiverRenderer;
        if (effect == null) return;
        RiverRenderer = null;
        effect.Dispose();
    }

    /// <summary>
    /// Notify all registered compute effects to rebuild size-dependent storage textures after a window resize.
    /// Called by BaseApp.Resize after backend HandleResize (GPU idle) so the old native resources can be safely destroyed and rebuilt in place
    /// while keeping the C# object identity unchanged, which preserves Sprite2D references.
    ///
    /// Scope decides which notifications each effect gets: every effect still gets one <see cref="ComputeEffect.OnResize"/>
    /// for its app-global storage (sized from the device resolution), and a <see cref="ComputeScope.View"/> effect
    /// additionally gets one <see cref="ComputeEffect.OnViewResize"/> per rendered view (main view first) so it can size
    /// that view's own resources from that view's targets (a minimap is not full-screen).
    /// </summary>
    public static void ResizeCompute(IGraphics g)
    {
        g.SynchronizeRenderTargetSizes();
        ResizeList(_computeFrameStart);
        ResizeList(_computeAfterOpaque);
        ResizeList(_computeAfterScene);

        // P2: the main view's water snapshot must be rebuilt along with SceneColor, and before any frame starts -
        // creating resources mid-render is not allowed during topology changes. Secondary views keep fixed-size
        // targets, so their snapshots never leave the shape they were allocated with.
        if (WaterRenderer != null && PrimaryTargets.SceneColor is { } waterColor)
        {
            try { EnsureWaterRefractionTarget(g, PrimaryTargets, waterColor); }
            catch (Exception ex)
            {
                DeviceServices.BaseApp.AddLog(LogType.Error, $"[Water] Failed to rebuild snapshot: {ex.Message}");
                UnregisterWaters();
            }
        }

        void ResizeList(List<ComputeEffect> effects)
        {
            for (int i = 0; i < effects.Count; i++)
            {
                var effect = effects[i];
                effect.OnResize(g);
                if (effect.Scope != ComputeScope.View) continue;
                effect.OnViewResize(g, PrimaryTargets);
                for (int v = 0; v < Views.Count; v++) effect.OnViewResize(g, Views[v].Targets);
            }
        }
    }

    public static void Execute(IGraphics g, BaseApp app, in Vector4 clearColor)
    {
        BeginSceneFrame(app);
        FrameIndex++;
        var canvas = app.Canvas2D;
        var backend = g.Immediate2D;
        try
        {
            canvas.BeginFrame(app.DesignResolution, backend?.OutputSize ?? app.DeviceResolution);
            app.Draw2D(canvas);
            canvas.EndFrame();
            if (canvas.CommandCount != 0)
            {
                if (backend == null)
                    throw new PlatformNotSupportedException("The current platform does not implement the immediate 2D backend.");
                // Resource preparation and upload occur before all passes and are never initiated within Control.Draw.
                backend.Prepare(canvas);
            }
            ExecutePasses(g, app, clearColor, canvas.CommandCount == 0 ? null : backend);
        }
        finally
        {
            try { backend?.CompleteFrame(); }
            finally { canvas.Clear(); }
        }
    }

    static void ExecutePasses(IGraphics g, BaseApp app, in Vector4 clearColor, IImmediate2DBackend? immediate)
    {
        // Immediate-2D compatibility mode (Immediate2DMode): the platform skipped the entire 3D
        // initialization, so the pass chain reduces to a single Overlay pass that renders the 2D
        // canvas straight into the backbuffer. It is the first and only backbuffer pass of the
        // frame, so BeginPass selects the clearing variant and the frame starts from the app
        // background color. No offscreen target, effect, or compute phase exists to run here.
        if (Immediate2DMode.Enabled)
        {
            g.BeginPass(new PassDesc
            {
                Id = RenderPassId.Overlay,
                ColorTarget = null,
                ClearColor = clearColor,
                ClearColorEnable = true,
                ClearDepthEnable = true,
                StoreDepth = false,
            });

            try
            {
                app.DrawOverlay();
                immediate?.Submit(app.Canvas2D);
            }
            finally
            {
                g.EndPass();
            }
            return;
        }

        // Shadow reuse has to be decided before the FrameStart phase, not at the pass: A7's instance cull runs
        // inside that phase and must know whether its output will have a consumer. Entering the shadow pass at all
        // destroys the atlas contents being reused (the pass clears on entry), so this decision also gates BeginPass.
        // The test only walks the caster tree and submits nothing, so running it here costs nothing extra.
        bool shadowReuse = ShadowMap != null && RenderShadow != null && CascadedShadow.EvaluateReuse(app);
        CascadedShadow.ReuseActive = shadowReuse;

        // FrameStart phase, App scope: dispatch before all render passes (strictest-platform contract: VK forbids dispatch
        // inside a pass, and Metal compute/render encoders are mutually exclusive; synchronization is centralized inside
        // backend DispatchCompute). Effects that produce something a single view needs (per-view culling, per-view
        // procedural input) declare Scope=View and run at the start of their own view instead.
        RecordPhase(g, ComputePhase.FrameStart, ComputeScope.App);

        // Shadow (before Scene, depth-only; activated after 1-5 registration). It stays outside the view loop because the
        // cascaded atlas is one texture for the whole frame.
        // Clause 12: the reuse result decided above bypasses BeginPass rather than merely returning early from the pass
        // body - the pass clears the atlas on entry, so entering it at all destroys the very contents being reused.
        if (ShadowMap != null && RenderShadow != null && !shadowReuse)
        {
            g.BeginPass(new PassDesc
            {
                Id = RenderPassId.Shadow,
                DepthTarget = ShadowMap,
                ClearDepthEnable = true,
                StoreDepth = true,
            });

            RenderShadow(g);

            g.EndPass();
        }

        // Views. The main view always runs first and closes the frame for App-scope effects, exactly as in the
        // single-view shape; additional views follow with lastView=false so an App-scope effect still runs once per
        // frame. That order is what keeps "one execution per frame" true while the pass chain itself runs N times.
        // A view is skipped when its UpdateInterval says so, and then its targets keep the previous frame's contents,
        // which is the visible result for a slow minimap.
        ExecuteView(g, app, null, PrimaryTargets, clearColor, true);

        for (int i = 0; i < Views.Count; i++)
        {
            var slot = Views[i];
            if (!slot.View.ShouldRender(FrameIndex)) continue;
            ExecuteView(g, app, slot.View, slot.Targets, clearColor, false);
        }

        // Restore the default so post-frame readers (diagnostics, tests, the next frame's setup) see the main view.
        ActiveTargets = PrimaryTargets;
        CurrentView = app.MainView;
        Season.Rendering.RenderView.CurrentLayerMask = uint.MaxValue;
        AfterViews?.Invoke(g);

        // Overlay: UI/debug drawn once, on top of whichever view presented to the backbuffer.
        g.BeginPass(new PassDesc
        {
            Id = RenderPassId.Overlay,
            ColorTarget = null,
            ClearColorEnable = false,
            ClearDepthEnable = false,
            StoreDepth = false,
        });

        try
        {
            app.DrawOverlay();
            immediate?.Submit(app.Canvas2D);
        }
        finally
        {
            g.EndPass();
        }
    }

    /// <summary>
    /// Executes the complete pass chain of one view: scene (possibly split into opaque/transparent with the fog and
    /// weather composites in between), the per-view compute phases, the Post slot and finally the present blit.
    ///
    /// <para>Every static target member read below is routed to <paramref name="targets"/> through
    /// <see cref="ActiveTargets"/>, so the same code path serves the main view and any additional view.</para>
    ///
    /// <para><paramref name="view"/> is null only in the single-view shape; the view whose constants are active is then
    /// <see cref="CurrentView"/>, which <see cref="PrepareView"/> receives as well, so both shapes upload exactly once.
    /// <paramref name="lastView"/> tells App-scope compute effects that this is the view whose chain closes the frame,
    /// which is where effects that do not depend on a view run (they see one execution per frame, as before).</para>
    /// </summary>
    public static void ExecuteView(IGraphics g, BaseApp app, RenderView? view, ViewTargets targets,
        in Vector4 clearColor, bool lastView = true)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (view != null)
        {
            view.Targets = targets;
            CurrentView = view;
        }
        ActiveTargets = targets;
        targets.BeginFrame();

        // P3 per-view policy. The main view keeps the pre-P3 defaults, so nothing changes unless an app opts out.
        var activeView = view ?? Season.Rendering.RenderView.Current;
        var features = activeView.Features;
        ActiveFeatures = features;
        ActiveShadowPolicy = activeView.ShadowPolicy;
        Season.Rendering.RenderView.CurrentLayerMask = activeView.LayerMask;

        PrepareView?.Invoke(g, activeView);

        // View-scope effects that prepare inputs for this view only (per-view culling, per-view procedural input).
        RecordPhase(g, ComputePhase.FrameStart, ComputeScope.View);

        var segmented = g as ISegmentedSceneGraphics;
        bool opaque = (features & ViewFeature.Opaque) != 0;
        bool transparent = (features & ViewFeature.Transparent) != 0;
        bool particleSubmit = (features & ViewFeature.Particles) != 0;
        // Per-view fog split: for the primary view, FogCompositeTarget is pre-allocated at Initialize;
        // for secondary views, Record() lazily allocates it, so we permit the route when the target is null
        // and the view has a valid SceneColor (the allocation will happen inside the CS phase).
        bool fogTargetReady = FogCompositeTarget is { Desc.ColorFormat: RtFormat.Rgba16Float }
            && FogCompositeTarget.Desc.SampleCount <= 1
            && FogCompositeTarget.Desc.Width == SceneColor?.Desc.Width
            && FogCompositeTarget.Desc.Height == SceneColor?.Desc.Height
            && !ReferenceEquals(FogCompositeTarget, SceneColor);
        bool fogLazyAllowed = ActiveTargets.Index != 0 && SceneColor is { Desc.ColorFormat: RtFormat.Rgba16Float, Desc.SampleCount: <= 1 };
        bool fogSplit = segmented != null && (features & ViewFeature.Fog) != 0 && app.Fog.Enabled && g.ComputeSupported
            && RenderQuality.Current.VolumetricFog != FogQuality.Off
            && SceneColor is { Desc.ColorFormat: RtFormat.Rgba16Float }
            && SceneColor.Desc.SampleCount <= 1 && SceneDepth != null
            && (fogTargetReady || fogLazyAllowed) && RenderFogComposite != null;
        bool weatherSplit = segmented != null && (features & ViewFeature.Weather) != 0
            && LightingSnapshotValid && _frameLights.WeatherParams1.W > 0
            && SceneColor is { Desc.ColorFormat: RtFormat.Rgba16Float, Desc.SampleCount: <= 1 }
            && SceneDepth != null && WeatherCompositeTarget is { Desc.ColorFormat: RtFormat.Rgba16Float, Desc.SampleCount: <= 1 }
            && SceneColor.Desc.Width == WeatherCompositeTarget.Desc.Width && SceneColor.Desc.Height == WeatherCompositeTarget.Desc.Height
            && !ReferenceEquals(SceneColor, WeatherCompositeTarget) && RenderWeatherComposite != null;
        // D1: decals must use the segmented route. Without segmentation the full Scene pass already drew the transparent
        // primitives, and decals would land right on top of water and particles; decals have no "this frame's output not
        // ready" fallback like fog, so whenever the view has opacity on and the scene has active decals, split unconditionally.
        bool decalSplit = segmented != null && DecalRenderer != null && opaque && (features & ViewFeature.Decals) != 0
            && SceneColor != null && SceneDepth != null && app.Decals.ActiveCount > 0;
        // P2: the water surface must likewise be segmented - without segmentation the full Scene pass already drew the
        // transparent primitives, and the water would cover transparent objects that should be above it. The refraction
        // snapshot must have the same shape as the scene color, otherwise it is better not to draw at all. The snapshot
        // lives on the view, so a secondary view has none until its first water frame and allocates it just before the
        // pass (the same lazy rule the fog composite follows); the route is therefore also allowed while it is missing.
        bool waterSnapshotReady = WaterRefractionTarget is { } waterSnapshot
            && !ReferenceEquals(waterSnapshot, SceneColor)
            && waterSnapshot.Desc.Width == SceneColor?.Desc.Width
            && waterSnapshot.Desc.Height == SceneColor?.Desc.Height;
        bool waterLazyAllowed = ActiveTargets.Index != 0
            && SceneColor is { Desc.ColorFormat: RtFormat.Rgba16Float, Desc.SampleCount: <= 1 };
        bool waterSplit = segmented != null && WaterRenderer != null && opaque && (features & ViewFeature.Water) != 0
            && SceneColor is not null && SceneDepth != null && app.Waters.ActiveCount > 0
            && (waterSnapshotReady || waterLazyAllowed);
        // P1 curtain: like the water surface it must be segmented (otherwise the full Scene draw has already drawn the
        // transparent primitives and the curtain would cover them). It does not sample a scene-color snapshot, so there are
        // only two gates, shape and data: single-sampled HDR color + active curtains in the scene.
        bool waterfallSplit = segmented != null && WaterfallRenderer != null && opaque && (features & ViewFeature.Waterfalls) != 0
            && SceneColor is { Desc.ColorFormat: RtFormat.Rgba16Float, Desc.SampleCount: 1 }
            && SceneDepth != null && app.Waterfalls.ActiveCount > 0;
        // P1 river ribbon: identical gates to the curtain, because it is the same Pass with a flat band shader. Ordered after the
        // curtain in the frame, so at the mouth the sea/pool water and the foam near the impact point are already in place.
        bool riverSplit = segmented != null && RiverRenderer != null && opaque && (features & ViewFeature.Waterfalls) != 0
            && SceneColor is { Desc.ColorFormat: RtFormat.Rgba16Float, Desc.SampleCount: 1 }
            && SceneDepth != null && app.Rivers.ActiveCount > 0;
        // P0 SSR: reflections need to read the "already-drawn opaque scene color", but that rendering passes it as the color
        // attachment, and the same resource cannot be both attachment and sampling source; the composite must also land
        // before the transparent primitives, so we split whenever this frame might publish reflections. The criterion only
        // looks at capability (delegates attached), not whether this frame is Ready - the split is pass structure and must not
        // be inferred from a single frame's output, otherwise the same backend would switch routes frame by frame.
        bool ssrSplit = PrepareSsrComposite != null && RenderSsrComposite != null
            && opaque && (features & ViewFeature.Reflections) != 0
            && SceneColor is { Desc.ColorFormat: RtFormat.Rgba16Float } && SceneDepth != null;
        bool split = fogSplit || weatherSplit || decalSplit || waterSplit || waterfallSplit || riverSplit || ssrSplit;
        // S4: a non-segmented view must also run the two stages "collect -> opaque -> transparent". Without segmentation the
        // full Scene pass mixes the transparent primitives into the opaque ones in scene order, so ViewFeature.Transparent is
        // effectively a no-op (a minimap that requests only Opaque still draws light beams) and transparent geometry loses the
        // "depth already finished" precondition. The backend hard-binds its queue to the two pass ids SceneOpaque /
        // SceneTransparent, so we cannot keep using the Scene pass here and can only align the structure with the segmented route.
        bool deferredScene = !split && segmented != null && opaque;
        // SceneSegmented keeps its original meaning (whether the fog/weather-style full-screen composite segmentation was done):
        // both VolumetricFog and WeatherCurtain require it to publish output, whereas deferredScene merely splits the geometry
        // draws into two stages without introducing those two composite stages.
        targets.SceneSegmented = split;

        try
        {
            // Without segmentation particles submit along with the full Scene draw, and the handover must happen before BeginPass; with segmentation it is deferred until just before the transparent stage opens.
            bool particles = false;
            if (split || deferredScene)
            {
                SceneDrawOrder.BeginCollection();
                SceneCollectionActive = true;
                segmented!.BeginSceneCollection();
                app.DrawScene();
                SceneDrawOrder.Seal();
                segmented.SealSceneCollection();
                SceneCollectionActive = false;
            }
            else if (particleSubmit && SceneColor != null)
                particles = PrepareParticles(g, SceneColor);

            g.BeginPass(new PassDesc
            {
                Id = split || deferredScene ? RenderPassId.SceneOpaque : RenderPassId.Scene,
                ColorTarget = SceneColor,
                DepthTarget = SceneDepth,
                VelocityTarget = SceneVelocity,
                NormalTarget = SceneNormal,
                ClearColor = clearColor,
                ClearColorEnable = true,
                ClearDepthEnable = true,
                StoreDepth = SceneDepth != null,
            });
            try
            {
                if (split || deferredScene) segmented!.DrawCollectedOpaque();
                else if (opaque)
                {
                    app.DrawScene();
                    if (particles) SubmitParticles(g);
                }
            }
            finally { g.EndPass(); }

            // D1: decals write SceneColor only, and the depth here is a sampling source rather than an attachment. They must be
            // ordered before the fog/rain-curtain composites, otherwise EffectiveSceneColor already points at the post-composite
            // copy and the decals would be overwritten by the composite result published afterwards.
            // Prepare performs culling, sorting and state transitions outside the pass; when it returns false not even BeginPass is issued.
            // s9: SceneNormal is attached as the second color attachment only when roughness actually has to be rewritten (it therefore cannot be a sampling source at the same time).
            if (decalSplit && DecalRenderer!.Prepare(g, app.Decals, SceneColor, SceneDepth, SceneNormal))
            {
                g.BeginPass(new PassDesc
                {
                    Id = RenderPassId.SceneDecal,
                    ColorTarget = SceneColor,
                    NormalTarget = DecalRenderer!.WriteRoughness ? SceneNormal : null,
                    ClearColorEnable = false,
                    ClearDepthEnable = false,
                    StoreDepth = true,
                });
                try { DecalRenderer!.Draw(g); }
                finally { g.EndPass(); }
            }

            // AfterOpaque: the fog pass integrates its volume here and the weather curtain prepares its frame data,
            // so both scopes run before the composites. App-scope effects only run on the view that closes the frame.
            RecordPhase(g, ComputePhase.AfterOpaque, ComputeScope.View);
            RecordPhase(g, ComputePhase.AfterOpaque, ComputeScope.App, lastView);

            // P0 SSR: decals have already finished rewriting roughness and reflections should see the final surface; the delta
            // is added into SceneColor, so this step must be ordered before the fog/rain-curtain stages - those two composites copy
            // the result into their own targets and publish the color source, and once past them it can never be written back to
            // the final image. Transparent primitives come later and are naturally never treated as a reflection source.
            if (ssrSplit && SsrOutputReady && PrepareSsrComposite!(g))
            {
                g.BeginPass(new PassDesc
                {
                    Id = RenderPassId.SsrComposite,
                    ColorTarget = SceneColor,
                    ClearColorEnable = false,
                    ClearDepthEnable = false,
                    StoreDepth = false,
                });
                try { RenderSsrComposite!(g); }
                finally { g.EndPass(); }
            }

            if (split)
            {
                bool composited = false;
                if (weatherSplit && WeatherOutputReady && (PrepareWeatherComposite?.Invoke(g) ?? false))
                {
                    g.BeginPass(new PassDesc { Id = RenderPassId.WeatherComposite, ColorTarget = WeatherCompositeTarget,
                        ClearColorEnable = true, ClearDepthEnable = false, StoreDepth = false });
                    FogCompositeResult weatherResult;
                    try { weatherResult = RenderWeatherComposite!(g, EffectiveSceneColor!); }
                    finally { g.EndPass(); }
                    if (weatherResult.Success)
                    {
                        PublishSceneComposite(WeatherCompositeTarget!, weatherResult.AoApplied);
                        targets.WeatherComposited = true;
                    }
                }
                if (fogSplit && FogOutputReady && (PrepareFogComposite?.Invoke(g) ?? true))
                {
                    g.BeginPass(new PassDesc
                    {
                        Id = RenderPassId.FogComposite,
                        ColorTarget = FogCompositeTarget,
                        ClearColorEnable = true,
                        ClearDepthEnable = false,
                        StoreDepth = false,
                    });
                    FogCompositeResult result;
                    try { result = RenderFogComposite!(g, EffectiveSceneColor!); }
                    finally { g.EndPass(); }
                    if (result.Success)
                    {
                        PublishSceneComposite(FogCompositeTarget!, result.AoApplied);
                        composited = true;
                    }
                }
                // P2 water surface: after decals and the fog/rain-curtain stages, before transparent geometry.
                // The snapshot must be copied before BeginPass - in the water pass SceneColor is the color attachment, and the same
                // resource cannot be sampled at the same time. When the copy fails, the whole frame skips water: using an uninitialized
                // snapshot as the refraction source corrupts the image outright rather than just missing one effect.
                // A secondary view allocates its snapshot here on its first water frame (the shape gate above lets it through).
                if (waterSplit && EffectiveSceneColor is { } waterColor
                    && TryEnsureWaterRefractionTarget(g, targets, waterColor)
                    && WaterRefractionTarget is { } snapshot
                    && g.CopyRenderTarget(waterColor, snapshot)
                    && WaterRenderer!.Prepare(g, app.Waters, waterColor, snapshot, SceneDepth, app.Time))
                {
                    g.BeginPass(new PassDesc
                    {
                        Id = RenderPassId.SceneWater,
                        ColorTarget = waterColor,
                        ClearColorEnable = false,
                        ClearDepthEnable = false,
                        StoreDepth = false,
                    });
                    try { WaterRenderer!.Draw(g); }
                    finally { g.EndPass(); }
                }

                // P1 waterfall curtain: after the water surface, before transparent geometry. There is no snapshot to make, and
                // Prepare takes the final scene color directly; being ordered after the water surface lets the curtain at the impact
                // point cover the pool edge (water writes no depth, so the order is decided entirely by pass order).
                if (waterfallSplit && EffectiveSceneColor is { } waterfallColor
                    && WaterfallRenderer!.Prepare(g, app.Waterfalls, waterfallColor, SceneDepth, app.Time))
                {
                    g.BeginPass(new PassDesc
                    {
                        Id = RenderPassId.SceneWaterfall,
                        ColorTarget = waterfallColor,
                        ClearColorEnable = false,
                        ClearDepthEnable = false,
                        StoreDepth = false,
                    });
                    try { WaterfallRenderer!.Draw(g); }
                    finally { g.EndPass(); }
                }

                // P1 river ribbon: after the curtain, before transparent geometry. Same pass id (the pipeline checks
                // SceneWaterfall), same reason for the ordering: water writes no depth, so only pass order decides who covers whom.
                if (riverSplit && EffectiveSceneColor is { } riverColor
                    && RiverRenderer!.Prepare(g, app.Rivers, riverColor, SceneDepth, app.Time))
                {
                    g.BeginPass(new PassDesc
                    {
                        Id = RenderPassId.SceneWaterfall,
                        ColorTarget = riverColor,
                        ClearColorEnable = false,
                        ClearDepthEnable = false,
                        StoreDepth = false,
                    });
                    try { RiverRenderer!.Draw(g); }
                    finally { g.EndPass(); }
                }

                // Transparent geometry is the second half of the scene; a view that switched it off still gets the
                // fog/weather composites above, because those blend over the whole image rather than over geometry.
                if (transparent)
                {
                    var transparentColor = EffectiveSceneColor;
                    particles = particleSubmit && PrepareParticles(g, transparentColor);
                    g.BeginPass(new PassDesc
                    {
                        Id = RenderPassId.SceneTransparent,
                        ColorTarget = EffectiveSceneColor,
                        DepthTarget = SceneDepth,
                        VelocityTarget = SceneVelocity,
                        NormalTarget = SceneNormal,
                        ClearColorEnable = false,
                        ClearDepthEnable = false,
                        StoreDepth = true,
                    });
                    try
                    {
                        segmented!.DrawCollectedTransparent(composited);
                        if (particles) SubmitParticles(g);
                    }
                    finally { g.EndPass(); }
                }
            }
            else if (deferredScene && transparent)
            {
                // S4: the transparent stage of a non-segmented view. There is no fog/weather composite here, so EffectiveSceneColor
                // is still SceneColor; sampleFog is passed false - the view did not request Fog, and the backend uses this to keep the
                // volumetric trio at zero gating rather than the previous frame's leftovers.
                particles = particleSubmit && PrepareParticles(g, EffectiveSceneColor);
                g.BeginPass(new PassDesc
                {
                    Id = RenderPassId.SceneTransparent,
                    ColorTarget = EffectiveSceneColor,
                    DepthTarget = SceneDepth,
                    VelocityTarget = SceneVelocity,
                    NormalTarget = SceneNormal,
                    ClearColorEnable = false,
                    ClearDepthEnable = false,
                    StoreDepth = true,
                });
                try
                {
                    segmented!.DrawCollectedTransparent(false);
                    if (particles) SubmitParticles(g);
                }
                finally { g.EndPass(); }
            }
        }
        finally
        {
            SceneCollectionActive = false;
            // An exception thrown inside the pass body skips SubmitParticles, so the handover record must be dropped here:
            // carrying it into the next frame would not misdraw (Prepare clears the table at the start) but would let residue persist across frames.
            _preparedParticles.Clear();
            if (split || deferredScene)
            {
                try { segmented!.ReleaseSceneCollection(); }
                finally { SceneDrawOrder.Reset(); }
            }
        }

        // AfterScene phase: dispatch after Scene writes complete and before Post (bloom downsampling/SSAO and similar
        // effects use SceneColor as input). View-scope effects see this view's targets; App-scope ones close the frame.
        RecordPhase(g, ComputePhase.AfterScene, ComputeScope.View);
        RecordPhase(g, ComputePhase.AfterScene, ComputeScope.App, lastView);

        // The outline mask is a single frame-wide texture consumed by the main view's FinalBlit, so only the
        // presenting view may overwrite it.
        if (targets.Present) g.RenderOutlineMask();

        // Post (after Scene; input SceneColor, output PostColor; activated when later post-processing is registered)
        var blitSource = EffectiveSceneColor;
        if (blitSource != null && PostColor != null && RenderPost != null && (features & ViewFeature.Post) != 0)
        {
            g.BeginPass(new PassDesc
            {
                Id = RenderPassId.Post,
                ColorTarget = PostColor,
                ClearColorEnable = false,
                ClearDepthEnable = false,
                StoreDepth = false,
            });

            RenderPost(g, blitSource);

            g.EndPass();
            blitSource = PostColor;
        }

        // FinalBlit: present the tail of the off-screen chain. Only the presenting view touches the backbuffer;
        // a secondary view writes into its UiColor surface instead, which is the LDR image a 2D control samples
        // later in the Overlay pass (tonemapped exactly like the backbuffer would have been).
        var presentTarget = targets.Present ? null : targets.UiColor;
        if (blitSource != null && (targets.Present || presentTarget != null))
        {
            g.BeginPass(new PassDesc
            {
                Id = RenderPassId.FinalBlit,
                ColorTarget = presentTarget,
                ClearColorEnable = false,
                ClearDepthEnable = false,
                StoreDepth = false,
            });

            g.BlitToBackbuffer(blitSource);

            g.EndPass();
        }
    }

    /// <summary>
    /// Records every effect of <paramref name="phase"/> whose scope matches, in registration order.
    /// <paramref name="enabled"/> is false for App-scope effects on views that do not close the frame: those effects
    /// never read per-view state by definition, so they run exactly once and keep the single-view ordering intact.
    /// </summary>
    static void RecordPhase(IGraphics g, ComputePhase phase, ComputeScope scope, bool enabled = true)
    {
        if (!enabled) return;
        var effects = EffectsFor(phase);
        for (int i = 0; i < effects.Count; i++)
        {
            var effect = effects[i];
            if (effect.Scope != scope) continue;
            // P3: a view that switched a feature off must not even record the matching effect, so the work (and its
            // per-view scratch targets) disappears instead of being dispatched and ignored.
            var required = effect.RequiredFeature;
            if (required != ViewFeature.None && (ActiveFeatures & required) == 0) continue;
            effect.Record(g);
        }
    }
}
