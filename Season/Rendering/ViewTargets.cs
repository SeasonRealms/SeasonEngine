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
        Textures = new FrameTextureTable(index);
    }

    /// <summary>View slot index; 0 is the main view. Matches <see cref="RenderView.Index"/>.</summary>
    public int Index { get; }

    /// <summary>
    /// Slot table of this view's published intermediates (bloom / AO / TAA resolve / fog volumes).
    /// Declared in stage 2B: producers and consumers name a <see cref="FrameTextureSlot"/>, never a raw
    /// string, and a rebuild advances the generation instead of leaving a byte-identical name behind.
    /// </summary>
    public FrameTextureTable Textures { get; }

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

    // -- Per-view fog volume resources (allocated lazily when ViewFeature.Fog is active for this view). --

    /// <summary>Per-view fog frame constant buffer (448×float4). Allocated on first fog Record for this view;
    /// null = this view does not have volumetric fog.</summary>
    public StorageBuffer? FogFrameBuffer;

    /// <summary>Scoped name of this view's integrated 3D volume; null = not yet allocated.</summary>
    public string? FogIntegratedName
    {
        get => Textures.NameOf(FrameTextureSlot.FogIntegrated);
        set => Textures.Publish(FrameTextureSlot.FogIntegrated, value);
    }

    /// <summary>Scoped name of this view's local (filtered) 3D volume (current history-write slot); null = not yet allocated.</summary>
    public string? FogLocalName
    {
        get => Textures.NameOf(FrameTextureSlot.FogLocal);
        set => Textures.Publish(FrameTextureSlot.FogLocal, value);
    }

    /// <summary>Scoped name of this view's injection 3D volume.</summary>
    public string? FogInjectionName
    {
        get => Textures.NameOf(FrameTextureSlot.FogInjection);
        set => Textures.Publish(FrameTextureSlot.FogInjection, value);
    }

    /// <summary>Scoped name of this view's temporal history ping slot 0.</summary>
    public string? FogHistory0Name
    {
        get => Textures.NameOf(FrameTextureSlot.FogHistory0);
        set => Textures.Publish(FrameTextureSlot.FogHistory0, value);
    }

    /// <summary>Scoped name of this view's temporal history ping slot 1.</summary>
    public string? FogHistory1Name
    {
        get => Textures.NameOf(FrameTextureSlot.FogHistory1);
        set => Textures.Publish(FrameTextureSlot.FogHistory1, value);
    }

    /// <summary>Per-view ping index (0 or 1); swaps after each successful fog Record.</summary>
    public int FogPing;

    /// <summary>Per-view temporal history validity; false after reset (camera jump, quality change).</summary>
    public bool FogHistoryValid;

    /// <summary>True when this view has fully allocated fog resources (frame buffer + both volumes).</summary>
    public bool HasFogVolumes => FogFrameBuffer != null && FogIntegratedName != null && FogLocalName != null;

    /// <summary>
    /// P2: scene-color snapshot the water surface samples as its refraction source. Per view, because the snapshot
    /// must have exactly this view's SceneColor shape and the water pass cannot sample the attachment it writes.
    /// The main view's snapshot is built at registration / resize; a secondary view allocates its own lazily on the
    /// first frame that draws water. Null = this view has no snapshot yet.
    /// </summary>
    public RenderTarget? WaterRefractionTarget;

    /// <summary>
    /// Per-view water frame constant block. It lives on the view rather than on the effect because a view uploads it
    /// between its own passes, and the staging of some backends only keeps a frame's last upload (Metal keeps one
    /// staging buffer per frame, WebGPU's writeBuffer lands before the encoded commands run): two views sharing one
    /// buffer would leave the main view drawing with the secondary view's matrices. Null = not yet allocated here.
    /// </summary>
    public StorageBuffer? WaterFrameBuffer;

    /// <summary>Per-view waterfall frame constant block; same reasoning as <see cref="WaterFrameBuffer"/>.</summary>
    public StorageBuffer? WaterfallFrameBuffer;

    /// <summary>Per-view river-ribbon frame constant block; same reasoning as <see cref="WaterFrameBuffer"/>, and kept
    /// separate from <see cref="WaterfallFrameBuffer"/> because the curtain and the ribbon are two uploads in one view.</summary>
    public StorageBuffer? RiverFrameBuffer;

    /// <summary>Per-view decal frame constant block; same reasoning as <see cref="WaterFrameBuffer"/>. Allocated on the
    /// first decal frame of this view.</summary>
    public StorageBuffer? DecalFrameBuffer;

    /// <summary>
    /// Particle transients owned by one (view, submitter) pair.
    ///
    /// Particle systems are the only multi-instance per-view effect in the engine: rain, snow, the campfire's
    /// flame / sparks / smoke and the waterfall mist routinely coexist, and each of them compacts its own instance
    /// table against this view's frustum and uploads its own frame block. The camera terms cannot be shared between
    /// views (see <see cref="WaterFrameBuffer"/>), and the tables cannot be shared between submitters either: the
    /// pass chain records every submitter first and only then issues the indirect draws, so a single table per view
    /// holds nothing but the last submitter's result and every earlier draw would render that submitter's particles.
    /// Allocation is lazy, on the first frame each pair is actually drawn.
    /// </summary>
    public sealed class ParticleBuffers
    {
        /// <summary>Instance table the compaction kernel appends survivors to. Recycled every frame.</summary>
        public StorageBuffer? Instances;

        /// <summary>Indirect argument block whose instance count this submitter's compaction raises.</summary>
        public StorageBuffer? Args;

        /// <summary>Frame constant block: VP, camera basis, spawn shape and appearance curve.</summary>
        public StorageBuffer? Frame;
    }

    /// <summary>Per-(view, submitter) particle transients. See <see cref="ParticleBuffers"/>.</summary>
    readonly Dictionary<object, ParticleBuffers> _particleBuffers = [];

    /// <summary>
    /// Transients for <paramref name="owner"/> in this view, allocated empty on first use. The owner is the
    /// submitting effect, so two particle systems rendered by the same view never share a table.
    /// </summary>
    internal ParticleBuffers GetParticleBuffers(object owner)
    {
        if (_particleBuffers.TryGetValue(owner, out var buffers)) return buffers;
        buffers = new ParticleBuffers();
        _particleBuffers[owner] = buffers;
        return buffers;
    }

    /// <summary>Drops <paramref name="owner"/>'s transients, e.g. when that particle system is disposed while the view lives on.</summary>
    internal void ReleaseParticleBuffers(object owner)
    {
        if (!_particleBuffers.Remove(owner, out var buffers)) return;
        buffers.Instances?.Dispose();
        buffers.Args?.Dispose();
        buffers.Frame?.Dispose();
    }

    /// <summary>Drops every particle transient this view allocated; called when the view retires.</summary>
    internal void ReleaseParticleBuffers()
    {
        foreach (var buffers in _particleBuffers.Values)
        {
            buffers.Instances?.Dispose();
            buffers.Args?.Dispose();
            buffers.Frame?.Dispose();
        }
        _particleBuffers.Clear();
    }

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
    public string? SceneColorOverride
    {
        get => Textures.NameOf(FrameTextureSlot.SceneColorResolve);
        set => Textures.Publish(FrameTextureSlot.SceneColorResolve, value);
    }

    /// <summary>
    /// Scoped texture-dictionary name of this view's bloom-chain output; null = this view records no bloom.
    /// Bloom is a <see cref="ComputeScope.View"/> effect built from this view's resolved scene color, so the name
    /// goes through <see cref="Scope"/> and two views in one frame own separate chains and outputs. Written when the
    /// effect first allocates this view's chain, revoked when the effect is disposed.
    /// </summary>
    public string? BloomOutput
    {
        get => Textures.NameOf(FrameTextureSlot.Bloom);
        set => Textures.Publish(FrameTextureSlot.Bloom, value);
    }

    /// <summary>Scoped texture-dictionary name of this view's GTAO output; null = this view records no AO.</summary>
    public string? AoOutput
    {
        get => Textures.NameOf(FrameTextureSlot.Ao);
        set => Textures.Publish(FrameTextureSlot.Ao, value);
    }

    /// <summary>This view's GTAO chain produced its output this frame.</summary>
    public bool AoReady;

    /// <summary>This view's AO already entered its HDR composite this frame, so its final blit must not multiply it again.</summary>
    public bool AoComposited;

    /// <summary>Set once this frame's fog injection, filtering and integration all completed.</summary>
    public bool FogOutputReady;

    /// <summary>Set once this frame's weather curtain produced its output.</summary>
    public bool WeatherOutputReady;

    /// <summary>P0: this frame's screen-space reflection delta texture has been published, so the composite pass immediately after it is issued.</summary>
    public bool SsrOutputReady;

    /// <summary>Only after this frame's rain composite succeeded do transparent materials sample weather transmission.</summary>
    public bool WeatherComposited;

    /// <summary>This frame inserted the fog/weather-style full-screen composites between the opaque and transparent stages for
    /// this view. The geometry split itself is unconditional; this flag gates only the effects that publish through a composite.</summary>
    public bool SceneSegmented;

    /// <summary>
    /// Whether TAA resolve is actually active for this view's frame. Deliberately NOT cleared by
    /// <see cref="BeginFrame"/>: jitter is decided in <see cref="RenderView.Prepare"/> before the view
    /// executes, so the flag has to survive from the previous frame's resolve (one frame of lag, which is
    /// what the single-view path always did).
    /// </summary>
    public bool TaaActive;

    /// <summary>
    /// This frame's TAA reactive rect, as (minX, minY, maxX, maxY) in normalized uv space. Pixels inside the rect do not reuse history.
    ///
    /// Why it exists: a bright event that lasts only one or two frames (a lightning channel) receives only 3% weight on the first frame
    /// under a static feedback of 0.97, so by the time the history accumulates to a visible brightness the event is already over; after it
    /// dies down it takes another thirty-odd frames to fade out completely.
    ///
    /// The default value is the **empty rect** (1,1,0,0), which is also the identity element of the union operation: when merging, x/y take
    /// min and z/w take max, and the empty rect happens to equal the neutral value in both directions, so multiple producers can merge
    /// unconditionally. With no producer, no uv falls inside the rect, so that shader branch is always false and ordinary frames cost nothing.
    /// </summary>
    public Vector4 ReactiveRect = new(1f, 1f, 0f, 0f);

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
        AoReady = false;
        AoComposited = false;
        FogOutputReady = false;
        WeatherOutputReady = false;
        SsrOutputReady = false;
        WeatherComposited = false;
        SceneSegmented = false;
        ReactiveRect = new Vector4(1f, 1f, 0f, 0f);
    }

    /// <summary>Drop the published composite when it points at a target that is being retired or rebuilt.</summary>
    internal void ForgetSceneComposite(RenderTarget target)
    {
        if (ReferenceEquals(WeatherCompositeTarget, target)) WeatherComposited = false;
        if (!ReferenceEquals(CompleteSceneColor, target)) return;
        CompleteSceneColor = null;
        SceneColorOverride = null;
        AoComposited = false;
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
