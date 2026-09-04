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
    Post,       // Optional post-process slot (active in 2-1 FXAA mode: tonemap(+bloom) uber composite outputs LDR PostColor)
    OutlineMask, // Windows DX only: Outline2D object mask pass
    FinalBlit,  // Off-screen -> backbuffer (enabled in Step 2; HDR sources use the tonemap(+bloom) variant, and degrade to LDR resolve when Post is active (FXAA/copy), see RenderQuality 1-4 contract revision 1)
    Overlay,    // UI/debug overlay after final presentation; does not participate in global post-processing
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
    /// <summary>Off-screen color target for the Scene pass; null = direct render to backbuffer (Step 1 shape).</summary>
    public static RenderTarget? SceneColor;

    /// <summary>Explicit depth target for the Scene pass (2-2 contract clause 2: full-size depth-only D32Float, registered by the backend
    /// during initialization in AO mode, replacing the global default depth and forcing StoreDepth=true so the AfterScene compute phase
    /// can consume it through a DepthTexture binding); null = reuse the backend default depth, with zero leftovers in the pipeline.</summary>
    public static RenderTarget? SceneDepth;

    /// <summary>Motion-vector target for the Scene pass (2-3 contract clause 2: full-size MatchBackbufferSize Rg16Float,
    /// created and registered by the backend during initialization in MotionVectors mode as Scene pass MRT slot 1; non-null means the main shader
    /// already baked the VELOCITY_OUTPUT variant); null = single-target shape, with zero leftovers in the pipeline.</summary>
    public static RenderTarget? SceneVelocity;

    // -- Step 3 empty slots: activate only after the target and callback are registered as a pair; otherwise they stay zero-cost. --

    /// <summary>Depth-only target of the Shadow pass (D32Float shadow map); registered by 1-5 CSM.</summary>
    public static RenderTarget? ShadowMap;

    /// <summary>Shadow pass draw content (depth-only draw); registered by 1-5 CSM.</summary>
    public static Action<IGraphics>? RenderShadow;

    /// <summary>Output target of the Post pass (the input is always SceneColor, so this depends on SceneColor being non-null). In 2-1 FXAA mode it is
    /// registered by the backend during initialization (LDR BackbufferCompatible, uber composite output, luma baked into alpha); see RenderQuality 2-1 contract clause 4.</summary>
    public static RenderTarget? PostColor;

    /// <summary>Post pass draw content (fullscreen post-processing, with SceneColor as input; sampling-state transitions of the input are handled by backend binding APIs);
    /// in 2-1 FXAA mode this is the backend uber composite (tonemap+bloom), registered together with PostColor.</summary>
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
    /// (FinalBlit without FXAA, Post uber in FXAA mode) is always replaced with this texture; SceneColor RT itself remains the render target of the Scene pass
    /// (so Execute in this class does not need to change either pass scheduling or blitSource selection, and the override is resolved by name at the backend composite entry).
    /// Written every frame by TaaEffect (the ping-pong writer for the current frame, see clause 11), and set to null when bypassed or disposed;
    /// null = the whole chain falls back to SceneColor, with zero leftovers.</summary>
    public static string? SceneColorOverride;

    /// <summary>2-3 contract clauses 14/15: whether TAA resolve is actually active for this frame (set every frame by TaaEffect).
    /// This gates jitter injection: jitter without resolve is worse than no jitter, so Camera3D.UpdateTemporal only bakes in the subpixel offset
    /// when AaMode.Taa is selected and this value is true; it stays false when registration fails or when resize causes a size mismatch and the effect bypasses itself.</summary>
    public static bool TaaActive;

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
    static readonly List<ComputeEffect> _computeAfterScene = new();

    /// <summary>
    /// Register a compute effect: return false when the backend does not support it or Initialize fails
    /// (missing shader sources / compilation failure on this backend), leaving zero leftovers and no entry in the table; on success it Records automatically every frame according to its phase.
    /// Must be called after backend graphics are ready and before the frame loop starts.
    /// </summary>
    public static bool RegisterCompute(IGraphics g, ComputeEffect effect)
    {
        if (!g.ComputeSupported) return false;
        if (!effect.Initialize(g)) return false;
        (effect.Phase == ComputePhase.FrameStart ? _computeFrameStart : _computeAfterScene).Add(effect);
        return true;
    }

    public static void UnregisterCompute(ComputeEffect effect)
    {
        _computeFrameStart.Remove(effect);
        _computeAfterScene.Remove(effect);
    }

    /// <summary>
    /// Notify all registered compute effects to rebuild size-dependent storage textures after a window resize.
    /// Called by BaseApp.Resize after backend HandleResize (GPU idle) so the old native resources can be safely destroyed and rebuilt in place
    /// while keeping the C# object identity unchanged, which preserves Sprite2D references.
    /// </summary>
    public static void ResizeCompute(IGraphics g)
    {
        for (int i = 0; i < _computeFrameStart.Count; i++)
            _computeFrameStart[i].OnResize(g);
        for (int i = 0; i < _computeAfterScene.Count; i++)
            _computeAfterScene[i].OnResize(g);
    }

    public static void Execute(IGraphics g, BaseApp app, in Vector4 clearColor)
    {
        // FrameStart phase: dispatch before all render passes (strictest-platform contract: VK forbids dispatch inside a pass,
        // and Metal compute/render encoders are mutually exclusive; synchronization is centralized inside backend DispatchCompute)
        for (int i = 0; i < _computeFrameStart.Count; i++)
            _computeFrameStart[i].Record(g);

        // Shadow (before Scene, depth-only; activated after 1-5 registration)
        if (ShadowMap != null && RenderShadow != null)
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

        g.BeginPass(new PassDesc
        {
            Id = RenderPassId.Scene,
            ColorTarget = SceneColor,
            DepthTarget = SceneDepth,
            // 2-3 contract clause 2: non-null means Scene uses three targets (color + velocity + depth), with clear (0,0)
            VelocityTarget = SceneVelocity,
            ClearColor = clearColor,
            ClearColorEnable = true,
            ClearDepthEnable = true,
            // 2-2 contract clause 2: when SceneDepth is explicit, depth must be preserved for AfterScene compute reads (with tiler bandwidth cost);
            // otherwise keep DontCare to save bandwidth
            StoreDepth = SceneDepth != null,
        });

        app.DrawScene();

        g.EndPass();

        // AfterScene phase: dispatch after Scene writes complete and before Post (bloom downsampling/SSAO and similar effects use SceneColor as input)
        for (int i = 0; i < _computeAfterScene.Count; i++)
            _computeAfterScene[i].Record(g);

        g.RenderOutlineMask();

        // Post (after Scene; input SceneColor, output PostColor; activated when later post-processing is registered)
        var blitSource = SceneColor;
        if (SceneColor != null && PostColor != null && RenderPost != null)
        {
            g.BeginPass(new PassDesc
            {
                Id = RenderPassId.Post,
                ColorTarget = PostColor,
                ClearColorEnable = false,
                ClearDepthEnable = false,
                StoreDepth = false,
            });

            RenderPost(g, SceneColor);

            g.EndPass();
            blitSource = PostColor;
        }

        // FinalBlit: present the tail of the off-screen chain
        if (blitSource != null)
        {
            g.BeginPass(new PassDesc
            {
                Id = RenderPassId.FinalBlit,
                ColorTarget = null,
                ClearColorEnable = false,
                ClearDepthEnable = false,
                StoreDepth = false,
            });

            g.BlitToBackbuffer(blitSource);

            g.EndPass();
        }

        g.BeginPass(new PassDesc
        {
            Id = RenderPassId.Overlay,
            ColorTarget = null,
            ClearColorEnable = false,
            ClearDepthEnable = false,
            StoreDepth = false,
        });

        app.DrawOverlay();

        g.EndPass();
    }
}
