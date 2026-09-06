// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// Step 6 consolidation: render-quality settings have been merged into Season.Basic.RenderQuality (BaseApp.cs).
/// That class now owns the static Default* sources and the runtime instance properties persisted in Settings.json;
/// Season.Rendering only keeps the enums defined in this file.
/// </summary>
public enum AaMode
{
    Off,

    /// <summary>4x MSAA (legacy tier: currently D3D12-only; HDR resolve quality is compromised and bandwidth cost is high, but it remains as a VR fallback).</summary>
    Msaa4x,

    /// <summary>FXAA 3.11 (introduced in 2-1; raster variant in post-tonemap LDR, using Post uber composite + FinalBlit FXAA presentation).</summary>
    Fxaa,

    /// <summary>TAA (introduced in 2-3; runs in HDR before tonemap and requires velocity). Selecting it forces MotionVectors=true during initialization;
    /// it depends on the HDR off-screen chain and compute, and falls back to Fxaa when unavailable.
    /// Implemented and stabilized on D3D12, Vulkan, Metal, and WebGPU.</summary>
    Taa,
}

/// <summary>
/// Which filter FinalBlit applies when its source is <see cref="FrameSchedule.PostColor"/>, i.e. when the Post pass
/// already finished the HDR-&gt;LDR composite and the remaining work is a full-screen filter in display-referred space.
///
/// This is derived from <see cref="AaMode"/> at the blit entry rather than stored alongside PostColor on purpose:
/// the AA tier is what decided to create PostColor in the first place, so a second copy of the same decision could
/// only ever disagree with it. Backends therefore map the tier here instead of reading a registered field.
/// </summary>
public enum PostResolve
{
    /// <summary>Straight presentation of the composited LDR image. This is the fallback when a tier registers the Post
    /// slot without asking for a filter; it is not currently reachable, and exists so that such a tier presents a
    /// correct image rather than an accidental FXAA or RCAS pass.</summary>
    Copy,

    /// <summary>2-1 clause 4: FXAA 3.11 on the composited image, reusing the luma the uber pass baked into alpha.</summary>
    Fxaa,

    /// <summary>2-3 clause 17: FSR1 RCAS sharpening on the composited image, scaled by RenderQuality.TaaSharpness.</summary>
    Rcas,
}

/// <summary>Ambient occlusion mode (2-2 contract clause 1: mutually exclusive and fixed at initialization). See the RenderQuality class header for fallback rules.
/// No classic SSAO tier is kept in advance: quality scaling changes parameters (direction count / step count) rather than the algorithm; more modes can be added later if needed.</summary>
public enum AoMode
{
    Off,

    /// <summary>GTAO-lite (introduced in 2-2: half-resolution horizon-based AO + depth-reconstructed normals + IGN noise + depth-aware spatial blur).</summary>
    Gtao,
}

/// <summary>Global illumination mode (2-4 contract clause 1: mutually exclusive and fixed at initialization). See the RenderQuality class header for fallback rules.
/// No side branches such as screen-space GI are pre-added: quality scaling changes parameters (SDF resolution / probe count / ray count / amortization divisor, see clause 11)
/// rather than switching algorithms, matching the AoMode policy.</summary>
public enum GiMode
{
    Off,

    /// <summary>DDGI (Majercik 2019) + box/sphere proxy SDF tracing (introduced in 2-4: three-kernel AfterScene compute chain,
    /// one-frame-lagged probe atlas, octahedral irradiance, and depth-moment ping-pong hysteresis).</summary>
    Ddgi,
}

/// <summary>Sky mode (2-5 contract clause 1: mutually exclusive and fixed at initialization). See the RenderQuality class header for fallback rules.
/// As with AoMode and GiMode, quality scaling changes parameters (LUT resolution / step count) rather than the algorithm.</summary>
public enum SkyMode
{
    /// <summary>Static skybox (app-provided cube faces with tint interpolated across day and night). This was the only form before 2-5,
    /// and remains the fallback target when any part of the procedural path fails (no compute support / missing shader sources on this backend).</summary>
    StaticCube,

    /// <summary>Procedural atmosphere (introduced in 2-5: Hillaire-style reduced single scattering, two FrameStart compute LUTs,
    /// and main shader renderMode=3 sampling the Sky-View LUT by world view direction).</summary>
    Procedural,
}

/// <summary>
/// 2-6 clause 1: per-texture mipmap policy. This is deliberately opt-in per texture rather than a global
/// "give every RGBA8 texture a chain", because the main pixel shader reaches several very different kinds of
/// texture through the same albedo slot and the same static sampler s0.
///
/// The distinction that matters is implicit versus explicit LOD. Material maps are read with Sample(), so the
/// hardware derives LOD from screen-space derivatives and a chain takes effect automatically with no shader
/// change. Everything that must not be affected already reads with SampleLevel(..., 0): cloud noise, the
/// sky-view LUT, the environment cube, the aerial-perspective LUT and the DDGI atlas. The one implicit-LOD
/// consumer that must stay single-level is the MSDF glyph atlas, where neighbouring glyphs are unrelated and
/// downsampling would bleed across their boundaries while the text itself is drawn near 1:1 and never minifies.
/// Hence <see cref="None"/> is the default and callers opt in explicitly.
/// </summary>
public enum TextureMipPolicy
{
    /// <summary>Single level. Identical behaviour to the pre-2-6 engine, and the default for every texture that does not ask otherwise.</summary>
    None,

    /// <summary>
    /// Box-filtered chain for colour data (base colour, emissive). Filtering happens directly on the stored bytes:
    /// the engine has no sRGB decode anywhere (textures are created as plain UNORM and the shader consumes the
    /// sampled value as linear), so the byte space *is* the space the shader interprets, and averaging there is
    /// self-consistent. Decoding to physical linear first would be correct in the absolute sense but inconsistent
    /// with how the value is used, which is the property that actually matters for filtering.
    /// </summary>
    Color,

    /// <summary>
    /// Box-filtered chain plus per-level renormalization, for tangent-space normal maps. Averaging two normals
    /// that differ in direction always produces a vector shorter than unit length, so without this step the
    /// shading normal shrinks with distance and both N dot L and the Fresnel term drift.
    ///
    /// 2-6 clause 5: the length discarded by that renormalization is measured first and stored in the alpha channel,
    /// so this policy takes ownership of alpha in the normal slot - an authored normal map that packed something else
    /// there will have it overwritten. See <see cref="RenderQuality.TextureNormalVariance"/> and MipChain.Build.
    /// </summary>
    Normal,

    /// <summary>
    /// Box-filtered chain with no renormalization, for scalar material data packed per channel
    /// (metallic-roughness, ambient occlusion). Plain averaging of roughness is not the physically correct answer for
    /// specular antialiasing - that requires folding normal variance into roughness (Toksvig / LEAN) - so this tier is
    /// deliberately the naive filter and stays that way. 2-6 clause 5 does the variance-aware part elsewhere, in the
    /// normal map's alpha channel and in the shader, precisely so that it does not depend on a material having one of
    /// these textures at all: most materials in this engine pair a normal map with a scalar roughness factor.
    /// </summary>
    Linear,
}

/// <summary>
/// Render quality configuration (introduced in 1-4). Step 6 merged the old static Season.Rendering.RenderQuality
/// into this runtime class: static fields became the Default* sources below, and the old properties became runtime instance properties.
/// BaseApp.Init() snapshots Default* into <see cref="Settings.RenderQuality"/> for new or empty settings, after which rendering always consumes that instance.
/// Apps may override Default* in their constructor to customize defaults. Runtime access is unified through <see cref="Current"/>.
///
/// Tier rules: each backend reads and locks these values before graphics initialization (PSO baking / RT and swapchain format derivation).
/// They must not change after the frame loop starts; unsupported features are ignored by a backend as an effective false.
///
/// Cross-platform contract summary:
/// - 1-4 HDR + tone mapping: SceneColor becomes Rgba16Float when enabled, output stays linear HDR until the final HDR->LDR composite point,
///   ACES constants are identical across backends, clear colors are linearized on entry, text uses inverse ACES compensation, and HdrExposure is injected from a single SetLighting path.
/// - 1-2 lighting: SceneLightParams keeps an all-vec4 layout, supports one directional light plus eight punctual lights, matches KHR_lights_punctual attenuation,
///   decouples color from intensity, and uses the same ambient/light-intensity conventions on all backends.
/// - 1-3 camera + frustum culling: frustum extraction and AABB tests stay in the shared CPU layer, animated bounds use a conservative scale,
///   culling never skips the fixed pass chain, and the whole path is allocation-free and runtime-toggleable.
/// - 1-5 shadows: CSM plus spot shadow maps use one atlas, controlled per-slot viewports, depth-only shadow shaders, hardware comparison sampling with fixed PCF,
///   and CPU-side cascade setup with texel snapping. Shadow ownership is chosen every frame by SceneLighting.Bake.
///   Clauses 10-12 form one chain built on the fact that clause 9 makes the cascade matrix bitwise stable rather than merely similar:
///   clause 10 measures how long that stability actually lasts, clause 11 sizes the angular step from the light's measured speed so the
///   interval lands on a target instead of being whatever 0.25 degrees happens to buy, and clause 12 cashes the interval in by skipping
///   the shadow pass on frames where the atlas would be reproduced exactly. Clause 12 changes no pixel; what it buys is the headroom to
///   raise atlas resolution. Clause 13 is independent of that chain: a normal-offset bias that removes acne by moving the sample along
///   the receiver's surface instead of pushing stored depth away from the light, which is what lets the depth biases - and the
///   peter-panning they cause - come back down.
/// - 2-1 post stack: bloom is an AfterScene compute chain and FXAA is a post-tonemap raster pass. Both use the final HDR->LDR composite point and degrade cleanly when unavailable.
/// - 2-2 AO: GTAO-lite uses explicit SceneDepth, depth-texture compute input, half-resolution kernels, and AO composition before ACES. Mesh-level AO exclusion remains supported.
/// - 2-3 motion vectors + TAA: velocity is an independent tier, SceneVelocity is explicit, jitter is injected from a single Camera3D path,
///   history data rides existing constant buffers, transparent geometry does not write velocity, and TAA uses ping-pong history with controlled degradation.
///   Clause 16 resamples reprojected history through a renormalized 5-tap Catmull-Rom filter instead of one bilinear fetch, because the per-frame
///   softening of a single fetch compounds across the whole feedback window rather than being paid once. Clause 17 then borrows the Post slot that
///   2-1 built for FXAA and resolves with FSR1 RCAS, which is the only place a display-referred sharpener can legally run in this engine;
///   it is off by default because owning that slot costs a whole extra pass, and TaaSharpness above zero is the switch that buys it.
/// - 1-7 cubemap + IBL: TextureCube is a minimal cross-platform type, SH9 irradiance/radiance ride the lighting UBO, diffuse picks either SH9 or constant ambient,
///   and the entire path falls back cleanly to the old ambient-only baseline.
/// - 2-4 DDGI + SDF: GI uses box/sphere proxies, accepts one-frame latency, stores all runtime parameters in the existing lighting UBO tail,
///   and keeps graceful fallback to the pre-GI image. Probe validity classification and Chebyshev visibility are runtime-tunable.
/// - 2-5 procedural sky: the sky path uses SkyAtmosphereEffect LUTs, a dual-light sun/moon model, optional procedural clouds,
///   and optional aerial perspective. Procedural sky, clouds, and AP all use explicit readiness gates and clean fallback paths.
/// - 2-6 material texture filtering: mip chains are opt-in per texture slot, generated once on the CPU and keyed into the shared
///   texture dictionary by policy, filtered with anisotropy, variance-corrected through Toksvig, and sharpened by a
///   TAA-only LOD bias. Every gate degrades to the pre-2-6 single-level image rather than to a different one.
/// </summary>
public class RenderQuality
{
    // -- Default-value sources (static Default* fields; apps may override them in the constructor, and BaseApp.Init() snapshots them into Settings.RenderQuality). --

    /// <summary>Default value for HdrSceneColor (overrideable in the app constructor and captured by Init()).</summary>
    public static bool DefaultHdrSceneColor = true;

    /// <summary>Default value for HdrExposure (overrideable in the app constructor and captured by Init()).</summary>
    public static float DefaultHdrExposure = 1.0f;

    /// <summary>Default value for KhrLightIntensityScale (overrideable in the app constructor and captured by Init()).</summary>
    public static float DefaultKhrLightIntensityScale = 0.05f;

    /// <summary>Default value for FrustumCulling (overrideable in the app constructor and captured by Init()).</summary>
    public static bool DefaultFrustumCulling = true;

    /// <summary>Default value for ShadowCulling (overrideable in the app constructor and captured by Init()).</summary>
    public static bool DefaultShadowCulling = true;

    /// <summary>Default value for AnimatedBoundsScale (overrideable in the app constructor and captured by Init()).</summary>
    public static float DefaultAnimatedBoundsScale = 1.5f;

    /// <summary>Default value for ShadowsEnabled (overrideable in the app constructor and captured by Init()).</summary>
    public static bool DefaultShadowsEnabled = true;

    /// <summary>Default value for ShadowAtlasSize (overrideable in the app constructor and captured by Init()).</summary>
    public static int DefaultShadowAtlasSize = 2048;

    /// <summary>Default value for ShadowCascadeCount (overrideable in the app constructor and captured by Init()).</summary>
    public static int DefaultShadowCascadeCount = 3;

    /// <summary>Default value for ShadowDistance (overrideable in the app constructor and captured by Init()).</summary>
    public static float DefaultShadowDistance = 40f;

    /// <summary>Default value for CascadeSplitLambda (overrideable in the app constructor and captured by Init()).</summary>
    public static float DefaultCascadeSplitLambda = 0.6f;

    /// <summary>Default value for ShadowLightAngleStep (overrideable in the app constructor and captured by Init()).</summary>
    public static float DefaultShadowLightAngleStep = 0.25f;

    /// <summary>Default value for ShadowTargetStableFrames (overrideable in the app constructor and captured by Init()).
    /// 8 rather than something longer: at the sample scene's day length the ladder settles on 1 degree - measured, with the
    /// resulting average interval coming out at 8.4 frames against this target of 8 - and the jump paid when a cell changes
    /// stays around a few texels in the near cascade, which TAA absorbs in one or two frames. Doubling the target doubles
    /// both the interval and that jump.</summary>
    public static int DefaultShadowTargetStableFrames = 8;

    /// <summary>Default value for ShadowAtlasReuse (overrideable in the app constructor and captured by Init()).
    /// False because the fingerprint is a single digest over the whole atlas, so one animating caster disqualifies every
    /// quadrant, and scenes here tend to keep animations running; see <see cref="RenderQuality.ShadowAtlasReuse"/> for the
    /// measurements behind that and for when turning it on is worth it.</summary>
    public static bool DefaultShadowAtlasReuse = false;

    /// <summary>Default value for ShadowNormalOffset (overrideable in the app constructor and captured by Init()).
    /// Expressed in shadow-map texels, so it keeps meaning when the atlas is resized. 1.5 covers the 3x3 PCF footprint's
    /// outer ring, which is the actual distance a receiver can be misjudged by, and is low enough not to visibly detach
    /// contact shadows on the near cascade.</summary>
    public static float DefaultShadowNormalOffset = 1.5f;

    /// <summary>Default value for ShadowSoftnessTexels (overrideable in the app constructor and captured by Init()).
    /// 2 texels puts the outermost of the eight taps at about 1.9 texels, which with the hardware 2x2 comparison filter on
    /// each tap covers slightly more than the 3x3 grid it replaces, so the default is a softer edge at the same tap count
    /// rather than a change of scale.</summary>
    public static float DefaultShadowSoftnessTexels = 2.0f;

    /// <summary>Default value for ShadowContactHardening (overrideable in the app constructor and captured by Init()).
    /// Off, because turning it on redefines <see cref="DefaultShadowSoftnessTexels"/> from "the edge width" into "the width a
    /// well separated occluder reaches", so the two have to be calibrated as a pair and the constant-radius look is the
    /// reference the pair is judged against.</summary>
    public static bool DefaultShadowContactHardening = false;

    /// <summary>Default value for ShadowDepthBias (overrideable in the app constructor and captured by Init()).</summary>
    public static int DefaultShadowDepthBias = 4;

    /// <summary>Default value for ShadowSlopeScaledDepthBias (overrideable in the app constructor and captured by Init()).</summary>
    public static float DefaultShadowSlopeScaledDepthBias = 2.0f;

    /// <summary>Default value for ShadowStrength (overrideable in the app constructor and captured by Init()).</summary>
    public static float DefaultShadowStrength = 1.0f;

    /// <summary>Default value for BloomEnabled (overrideable in the app constructor and captured by Init()).</summary>
    public static bool DefaultBloomEnabled = true;

    /// <summary>Default value for BloomThreshold (overrideable in the app constructor and captured by Init()).</summary>
    public static float DefaultBloomThreshold = 1.0f;

    /// <summary>Default value for BloomKnee (overrideable in the app constructor and captured by Init()).</summary>
    public static float DefaultBloomKnee = 0.5f;

    /// <summary>Default value for BloomIntensity (overrideable in the app constructor and captured by Init()).</summary>
    public static float DefaultBloomIntensity = 0.3f;

    /// <summary>Default value for BloomMipCount (overrideable in the app constructor and captured by Init()).</summary>
    public static int DefaultBloomMipCount = 6;

    /// <summary>Default value for AntiAliasing (overrideable in the app constructor and captured by Init()).</summary>
    public static AaMode DefaultAntiAliasing = AaMode.Taa;

    /// <summary>Default value for AmbientOcclusion (overrideable in the app constructor and captured by Init()).</summary>
    public static AoMode DefaultAmbientOcclusion = AoMode.Gtao;

    /// <summary>Default value for AoRadius (overrideable in the app constructor and captured by Init()).</summary>
    public static float DefaultAoRadius = 0.5f;

    /// <summary>Default value for AoIntensity (overrideable in the app constructor and captured by Init()).</summary>
    public static float DefaultAoIntensity = 1.0f;

    /// <summary>Default value for MotionVectors (overrideable in the app constructor and captured by Init()).</summary>
    public static bool DefaultMotionVectors = true;

    /// <summary>Default value for JitterScale (overrideable in the app constructor and captured by Init()).</summary>
    public static float DefaultJitterScale = 1.0f;

    /// <summary>Default value for JitterPhaseCount (overrideable in the app constructor and captured by Init()).</summary>
    public static int DefaultJitterPhaseCount = 8;

    /// <summary>Default value for TaaFeedback (overrideable in the app constructor and captured by Init()).</summary>
    public static float DefaultTaaFeedback = 0.9f;

    /// <summary>Default value for TaaVarianceClipGamma (overrideable in the app constructor and captured by Init()).</summary>
    public static float DefaultTaaVarianceClipGamma = 1.0f;

    /// <summary>Default value for TaaStaticFeedback (overrideable in the app constructor and captured by Init()).</summary>
    public static float DefaultTaaStaticFeedback = 0.97f;

    /// <summary>Default value for TaaSharpness (overrideable in the app constructor and captured by Init()).
    ///
    /// <b>Zero by default, and that choice is about cost, not about taste.</b> This value is the whole-feature gate for
    /// clause 17: a value above zero at initialization is what makes the TAA tier register PostColor and RenderPost,
    /// which costs one extra full-screen Post pass (the HDR-&gt;LDR uber composite moves out of FinalBlit into its own
    /// pass, writing an extra full-size LDR target) plus one RCAS blit. At zero, both slots stay null and the TAA tier
    /// keeps exactly the pre-clause-17 pipeline with no residue - which is why zero is the default: the tier should not
    /// silently buy a pass that not every scene needs.
    ///
    /// <b>To enable sharpening, set this above zero before Init(), for example in the app constructor.</b> A reasonable
    /// starting point is 0.5f, one stop below FSR1's maximum (its "stops" parameter maps here as exp2(-stops), so 0.5
    /// is one stop and 0.25 is two). One stop rather than full strength because clause 16 already removed the
    /// resampling loss that used to be the main reason a resolved frame looked soft; what is left for clause 17 to hide
    /// is the jitter footprint itself, which does not need an aggressive lobe.
    ///
    /// Changing it at runtime through <see cref="TaaSharpness"/> only scales the lobe and can never create or remove
    /// the pass, so a process that started at zero cannot gain sharpening later. See that property for the full rule.</summary>
    public static float DefaultTaaSharpness = 0f;

    /// <summary>Default value for GlobalIllumination (overrideable in the app constructor and captured by Init()).</summary>
    public static GiMode DefaultGlobalIllumination = GiMode.Off;

    /// <summary>Default value for GiSdfResolution (overrideable in the app constructor and captured by Init()).</summary>
    public static int DefaultGiSdfResolution = 64;

    /// <summary>Default value for GiVolumeSize (overrideable in the app constructor and captured by Init()).</summary>
    public static float DefaultGiVolumeSize = 32f;

    /// <summary>Default value for GiProbeGridX (overrideable in the app constructor and captured by Init()).</summary>
    public static int DefaultGiProbeGridX = 16;

    /// <summary>Default value for GiProbeGridY (overrideable in the app constructor and captured by Init()).</summary>
    public static int DefaultGiProbeGridY = 8;

    /// <summary>Default value for GiProbeGridZ (overrideable in the app constructor and captured by Init()).</summary>
    public static int DefaultGiProbeGridZ = 16;

    /// <summary>Default value for GiRaysPerProbe (overrideable in the app constructor and captured by Init()).</summary>
    public static int DefaultGiRaysPerProbe = 128;

    /// <summary>Default value for GiProbeUpdateDivisor (overrideable in the app constructor and captured by Init()).</summary>
    public static int DefaultGiProbeUpdateDivisor = 2;

    /// <summary>Default value for GiTraceMaxSteps (overrideable in the app constructor and captured by Init()).</summary>
    public static int DefaultGiTraceMaxSteps = 64;

    /// <summary>Default value for GiChebyshevOcclusion (overrideable in the app constructor and captured by Init()).</summary>
    public static bool DefaultGiChebyshevOcclusion = true;

    /// <summary>Default value for GiIntensity (overrideable in the app constructor and captured by Init()).</summary>
    public static float DefaultGiIntensity = 0.4f;

    /// <summary>Default value for GiHysteresis (overrideable in the app constructor and captured by Init()).</summary>
    public static float DefaultGiHysteresis = 0.97f;

    /// <summary>Default value for GiBackfaceHysteresis (overrideable in the app constructor and captured by Init()).</summary>
    public static float DefaultGiBackfaceHysteresis = 0.99f;

    /// <summary>Default value for GiProbeValidity (overrideable in the app constructor and captured by Init()).</summary>
    public static bool DefaultGiProbeValidity = true;

    /// <summary>Default value for GiNormalBias (overrideable in the app constructor and captured by Init()).</summary>
    public static float DefaultGiNormalBias = 0.25f;

    /// <summary>Default value for GiShadowSteps (overrideable in the app constructor and captured by Init()).</summary>
    public static int DefaultGiShadowSteps = 24;

    /// <summary>Default value for GiBounceGain (overrideable in the app constructor and captured by Init()).</summary>
    public static float DefaultGiBounceGain = 1f;

    /// <summary>Default value for GiPunctualShadow (overrideable in the app constructor and captured by Init()).</summary>
    public static bool DefaultGiPunctualShadow = false;

    /// <summary>Default value for GiBackfaceThreshold (overrideable in the app constructor and captured by Init()).</summary>
    public static float DefaultGiBackfaceThreshold = 0.5f;

    /// <summary>Default value for GiLogIntervalFrames (overrideable in the app constructor and captured by Init()).</summary>
    public static int DefaultGiLogIntervalFrames = 600;

    /// <summary>Default value for Sky (overrideable in the app constructor and captured by Init()).</summary>
    public static SkyMode DefaultSky = SkyMode.Procedural;

    /// <summary>Default value for SkyViewLutWidth (overrideable in the app constructor and captured by Init()).</summary>
    public static int DefaultSkyViewLutWidth = 256;

    /// <summary>Default value for SkyViewLutHeight (overrideable in the app constructor and captured by Init()).</summary>
    public static int DefaultSkyViewLutHeight = 128;

    /// <summary>Default value for SkyRayMarchSteps (overrideable in the app constructor and captured by Init()).</summary>
    public static int DefaultSkyRayMarchSteps = 16;

    /// <summary>Default value for CloudNoiseSize (overrideable in the app constructor and captured by Init()).</summary>
    public static int DefaultCloudNoiseSize = 512;

    /// <summary>Default value for AerialPerspective (overrideable in the app constructor and captured by Init()).</summary>
    public static bool DefaultAerialPerspective = true;

    /// <summary>Default value for AerialMaxDistanceKm (overrideable in the app constructor and captured by Init()).</summary>
    public static float DefaultAerialMaxDistanceKm = 32f;

    /// <summary>Default value for AerialIntensity (overrideable in the app constructor and captured by Init()).</summary>
    public static float DefaultAerialIntensity = 1f;

    /// <summary>Default value for TextureMipmaps (overrideable in the app constructor and captured by Init()).</summary>
    public static bool DefaultTextureMipmaps = true;

    /// <summary>Default value for TextureMipMinSize (overrideable in the app constructor and captured by Init()).</summary>
    public static int DefaultTextureMipMinSize = 64;

    /// <summary>Default value for TextureMaxAnisotropy (overrideable in the app constructor and captured by Init()).</summary>
    public static int DefaultTextureMaxAnisotropy = 16;

    /// <summary>Default value for TextureNormalVariance (overrideable in the app constructor and captured by Init()).</summary>
    public static bool DefaultTextureNormalVariance = true;

    /// <summary>Default value for TextureLodBias (overrideable in the app constructor and captured by Init()).</summary>
    public static float DefaultTextureLodBias = -0.5f;

    // -- Runtime properties (snapshot from Default* in the constructor; editable at runtime and persisted through Settings). --

    /// <summary>1-4 tier: HDR SceneColor (Rgba16Float) plus FinalBlit tonemap. Fixed at initialization and not meant to change at runtime.</summary>
    public bool HdrSceneColor { get; set; } = DefaultHdrSceneColor;

    /// <summary>Exposure multiplier for the HDR chain. Runtime knob, adjustable every frame. 1.0 = neutral exposure.</summary>
    public float HdrExposure { get; set; } = DefaultHdrExposure;

    /// <summary>
    /// 1-2 contract clause 5: KHR_lights_punctual intensity conversion knob (candela -> engine linear intensity).
    /// glTF point/spot light intensity is specified in candela, while the engine uses radiance = color x intensity x attenuation,
    /// so this runtime scale normalizes imported lights to a range comparable to hand-authored point lights. The default 0.05 provides a neutral brightness starting point.
    /// </summary>
    public float KhrLightIntensityScale { get; set; } = DefaultKhrLightIntensityScale;

    /// <summary>1-3 global switch for CPU frustum culling. Runtime-toggleable for A/B validation.</summary>
    public bool FrustumCulling { get; set; } = DefaultFrustumCulling;

    /// <summary>
    /// 1-5 clause 7 global switch for per-quadrant light-space caster culling in the shadow pass.
    /// The contract requires atlas contents to stay bit-identical with the switch on or off, which is why this remains runtime-toggleable for A/B verification.
    /// </summary>
    public bool ShadowCulling { get; set; } = DefaultShadowCulling;

    /// <summary>
    /// 1-3 contract clause 2: conservative bounds scale for animated models (skinning/morph).
    /// Runtime culling uses rest-pose AABB x this value to avoid false culling when animation extends outside the static box. Higher values trade culling efficiency for safety.
    /// </summary>
    public float AnimatedBoundsScale { get; set; } = DefaultAnimatedBoundsScale;

    /// <summary>1-5 tier: global shadow switch (CSM + spot shadowmap). Locked at initialization and ignored by unsupported backends.</summary>
    public bool ShadowsEnabled { get; set; } = DefaultShadowsEnabled;

    /// <summary>1-5 clause 2: shadow atlas size (square D32Float; each quadrant tile is half the side length). Fixed at initialization.</summary>
    public int ShadowAtlasSize { get; set; } = DefaultShadowAtlasSize;

    /// <summary>1-5 clause 2: CSM cascade count (atlas slots 0..N-1, clamped to [2,3]; slot 3 is reserved for the spot light). Fixed at initialization.</summary>
    public int ShadowCascadeCount { get; set; } = DefaultShadowCascadeCount;

    /// <summary>1-5 clause 9: farthest distance for directional-light shadows, clamped by camera Far and used as the cascade coverage range. Runtime-tunable.</summary>
    public float ShadowDistance { get; set; } = DefaultShadowDistance;

    /// <summary>1-5 clause 9: practical-split blend factor (0=pure uniform, 1=pure logarithmic). Default 0.6 gives denser near cascades.</summary>
    public float CascadeSplitLambda { get; set; } = DefaultCascadeSplitLambda;

    /// <summary>1-5 clause 9: <b>base</b> angular grid step in degrees for quantizing the directional-light direction before the
    /// cascade matrices are derived; 0 disables quantization entirely. Texel snapping aligns the light-space translation to the
    /// texel grid, but that grid is the light basis itself, so a continuously rotating sun makes snapping quantize into a moving
    /// frame of reference and the whole atlas re-samples at a new sub-texel phase every frame. Freezing the direction onto a
    /// fixed grid makes the cascade matrix bitwise identical within one cell, so the atlas is bitwise identical and temporal
    /// accumulation has something stable to converge on. The cost is that the shadow direction lags the shading direction by up
    /// to half a step, and that light motion is batched rather than removed: a larger step buys a longer stable interval and pays
    /// with a proportionally larger jump when the cell changes.
    ///
    /// Clause 11 turned this from the final step into the centre of a ladder: the step actually used is this value times a power
    /// of two, picked each frame from the light's measured angular speed so that one cell lasts about
    /// <see cref="ShadowTargetStableFrames"/> frames. Raising this raises the whole ladder. Read
    /// <see cref="CascadedShadow.EffectiveLightAngleStep"/> to see what is actually in force. Runtime-tunable.</summary>
    public float ShadowLightAngleStep { get; set; } = DefaultShadowLightAngleStep;

    /// <summary>1-5 clause 11: how many consecutive frames the cascade matrices should ideally stay bitwise identical. The
    /// adaptive angular step solves for this - it is sized so one angular cell lasts roughly this long at the light's current
    /// speed. Values below 2 disable adaptation and pin the step at <see cref="ShadowLightAngleStep"/>.
    ///
    /// This is the one knob that states the actual tradeoff, which is why it is the one exposed: raising it lengthens the interval
    /// TAA gets to converge over and proportionally enlarges the jump when the cell finally changes, because the accumulated
    /// light motion is batched rather than removed. It is also the ceiling on what clause 12 can skip, so a target of N frames
    /// means at most (N-1)/N of shadow passes avoided.
    ///
    /// Runtime-tunable, and meant to be surfaced in the control panel for tuning next to the [ShadowStable] diagnostic, whose
    /// reported average interval is the direct feedback signal for this value.</summary>
    public int ShadowTargetStableFrames { get; set; } = DefaultShadowTargetStableFrames;

    /// <summary>1-5 clause 12: whether the shadow pass may be skipped entirely on frames where the atlas would come out
    /// identical. Disabled by default; see below. This is purely a cost optimization and changes no pixel: the reuse predicate
    /// demands bitwise-identical cascade matrices <b>and</b> an unchanged caster set, and under those two conditions a redraw
    /// would reproduce the existing atlas exactly, since a depth-only pass reduces to a per-texel minimum and cannot depend on
    /// submission order.
    ///
    /// The caster half of the predicate is a fingerprint built by walking the caster tree once immediately before the pass,
    /// not collected during it, so detection never lags: an object that starts moving is caught on that very frame. The
    /// arrangement also pays for itself in CPU terms, since the pass replays the tree once per atlas quadrant - a redraw frame
    /// costs one extra traversal and a skipped frame saves four.
    ///
    /// Off by default on measured grounds rather than caution. That fingerprint is a single digest covering the whole atlas, so
    /// any one caster in motion disqualifies all four quadrants even where it is not visible, and scenes in this engine keep
    /// animations running continuously - a looping idle on the player, gulls flapping, instanced robots each on their own clip.
    /// Across sixty diagnostic windows of a populated scene the skip rate was 0% in every single one. Fifteen seconds of that
    /// was night, with the sun down and only a static spot light left, which isolates the cause: the matrices were provably
    /// still and the fingerprint was the only half of the predicate failing. In that state the mechanism is a net loss, one
    /// extra caster traversal per frame in exchange for nothing. Camera motion closes the other half independently - while the
    /// camera moved, matrix stability measured 0%, not merely reduced, since translation past a single texel invalidates the
    /// snap.
    ///
    /// A startup trace bounds both ends of that on one run. In the window before the animated models finished loading, the skip
    /// rate and the matrix stability rate came out identical to the frame - 49/59, 83% - because with a still caster set the
    /// predicate reduces to its matrix half, and 83% is therefore the whole of what this mechanism can ever deliver at a target
    /// of 8 frames. Four animated models loaded over the next three seconds; the next window read 1/60 against a matrix
    /// stability of 93%, and every window for the following forty-five seconds read 0/60. The entire gap between 83% and 0% is
    /// attributable to the digest being global, which is what makes per-quadrant digests the only change that would move this
    /// number - not a wider tolerance or a longer target.
    ///
    /// Worth enabling for a static or near-static scene, where it is the difference between running the pass every frame and
    /// running it once. Read the [ShadowReuse] diagnostic to confirm rather than assuming: a non-zero skip rate there is the
    /// only evidence that it is paying for itself. Turning it off also rules the mechanism out while diagnosing a stale-shadow
    /// artifact.
    ///
    /// The other reason to care about it, where it does pay: with the pass running on roughly
    /// 1/<see cref="ShadowTargetStableFrames"/> of frames, raising <see cref="ShadowAtlasSize"/> costs proportionally less, and
    /// texel density is the most direct lever there is on shadow-edge detail. Runtime-tunable, and intended for the control
    /// panel next to that diagnostic.</summary>
    public bool ShadowAtlasReuse { get; set; } = DefaultShadowAtlasReuse;

    /// <summary>1-5 clause 13: normal-offset shadow bias, in shadow-map texels of the cascade being sampled; 0 disables it.
    /// The lookup is displaced along the receiver's geometric normal - the one interpolated from the mesh, before any normal
    /// map perturbs it, since the offset has to follow the surface the depth buffer actually holds.
    ///
    /// The unit is texels rather than world distance, and that is what makes one uploaded value serve all three cascades: the
    /// shader applies the displacement in the cascade's tile NDC, where one texel is a fixed fraction of the tile no matter how
    /// much world space that tile covers. A texel count therefore lands on the right world distance in each cascade with no
    /// per-cascade constant being uploaded, and it also survives a change of <see cref="ShadowAtlasSize"/> unchanged.
    ///
    /// The offset is additionally scaled by sqrt(1 - NdotL^2), the length of the normal's projection onto the plane
    /// perpendicular to the light, so it vanishes on surfaces facing the light and peaks on grazing ones - which is exactly
    /// where a texel's worth of depth quantization spans the most world distance and self-shadowing acne appears.
    ///
    /// Why this exists next to the depth biases rather than instead of them: <see cref="ShadowDepthBias"/> and
    /// <see cref="ShadowSlopeScaledDepthBias"/> fight acne by pushing stored depth away from the light, which also detaches
    /// the shadow from the object's contact point - peter-panning. Offsetting along the surface instead moves the sample
    /// sideways into the receiver's own texel without touching depth at all, so acne can be removed without buying
    /// peter-panning. With this active the depth biases can usually come down; they are baked into the PSO at initialization
    /// and so remain the only defence for casters whose normals are unusable.
    ///
    /// Applies to the cascades only. The spotlight projects perspectively, so a texel covers a world distance that grows with
    /// depth and a single texel count cannot describe the offset there; the spot keeps the depth biases alone.
    ///
    /// Runtime-tunable, and intended for the control panel: it and the two depth biases are one tuning group, best adjusted
    /// together while watching a grazing-lit surface for acne and a contact edge for detachment.</summary>
    public float ShadowNormalOffset { get; set; } = DefaultShadowNormalOffset;

    /// <summary>1-5 clause 14: radius of the rotated PCF disk, in shadow-map texels, uploaded to ShadowParams0.W.
    ///
    /// The kernel is eight taps on a Vogel disk whose orientation is chosen per pixel, replacing the fixed 3x3 grid. A fixed
    /// grid can only be widened by adding taps, because widening the spacing alone turns the penumbra into visible steps at
    /// the grid pitch. Rotating instead converts that structured banding into noise, and noise is what TAA is able to remove:
    /// each frame lands a different orientation on the same pixel, so the history average converges on the true coverage that
    /// far more taps would have cost. This is why the clause was not worth attempting before the atlas stopped churning -
    /// clause 11 had to make the shadow matrices hold still for several frames first, or the history TAA accumulates would be
    /// of a moving shadow map and there would be nothing coherent to average.
    ///
    /// The unit is texels for the same reason as <see cref="ShadowNormalOffset"/>: the shader scales by shadowParams0.z, so
    /// one uploaded number means the same edge width in all three cascades and survives a change of <see cref="ShadowAtlasSize"/>.
    ///
    /// Raising this widens the penumbra at no extra sample cost, and the ceiling is set by noise rather than by performance:
    /// eight taps spread over a wide disk leave residual variance that TAA can only partly absorb, and it shows up as a faint
    /// crawl along the edge under camera motion. Note also that the quadrant is shrunk by this radius plus half a texel to
    /// keep taps inside their own tile, so a very large radius starts clamping taps at the tile border.
    ///
    /// Runtime-tunable, and one tuning group with <see cref="ShadowNormalOffset"/>: the offset is expressed in terms of the
    /// footprint this radius defines, so changing one invalidates the other's calibration.</summary>
    public float ShadowSoftnessTexels { get; set; } = DefaultShadowSoftnessTexels;

    /// <summary>1-5 clause 15: contact hardening. When on, the PCF disk radius is chosen per pixel from how far the occluder
    /// stands above the receiver instead of being the constant <see cref="ShadowSoftnessTexels"/>, so an edge is sharp where
    /// the two touch and widens as they separate. That is the single strongest cue that a shadow belongs to its object, and its
    /// absence is what makes a uniformly soft edge read as a decal.
    ///
    /// It reuses the clause 14 disk twice: once with the raw depth of the atlas to average the depths that are in front of the
    /// receiver, then again as the actual comparison with the radius that average implies. The first pass is the reason this
    /// clause costs a binding change on every backend - the atlas had only ever been reachable through a comparison sampler,
    /// which returns a pass/fail and not a depth.
    ///
    /// On the mapping from separation to width, stated plainly because it is not the textbook one: a physically sized sun
    /// (0.53 degrees across) produces a penumbra that is almost exactly a constant 0.6 to 0.8 texels wide in every cascade.
    /// The reason is that this projection setup makes depth range and texel footprint scale together - the cascade's bounding
    /// sphere radius cancels out of their ratio exactly - so a physical light angle buys a width below one texel and therefore
    /// no visible softening at any atlas size worth shipping. What the shader uses instead is the separation as a fraction of
    /// the cascade's own depth range, reaching <see cref="ShadowSoftnessTexels"/> at one quarter of it. That keeps every
    /// property this clause is actually for - hard on contact, softer with distance, consistent across cascades - and leaves
    /// the absolute width where it can be judged by eye, which is the softness knob above.
    ///
    /// The spotlight shares the code and gets a weaker version of the effect: its projection is perspective, so stored depth is
    /// not linear in distance and the same fraction means a different world separation near the light than far from it. The
    /// ordering still holds - touching is still hard, separated is still soft - so it is left on rather than special-cased.
    ///
    /// Has no effect when <see cref="ShadowSoftnessTexels"/> is zero, since the ceiling it would scale is zero; the sign that
    /// carries this flag to the shader is the sign of that same number, and a signed zero compares equal to zero.
    ///
    /// Off by default: it makes <see cref="ShadowSoftnessTexels"/> a ceiling reached only by well separated occluders rather
    /// than the width everywhere, so the two have to be calibrated together and the constant-radius look stays the reference.</summary>
    public bool ShadowContactHardening { get; set; } = DefaultShadowContactHardening;

    /// <summary>1-5 clause 4: constant depth bias for the shadow PSO. Baked into the PSO at initialization.</summary>
    public int ShadowDepthBias { get; set; } = DefaultShadowDepthBias;

    /// <summary>1-5 clause 4: slope-scaled depth bias for the shadow PSO. Baked into the PSO at initialization.</summary>
    public float ShadowSlopeScaledDepthBias { get; set; } = DefaultShadowSlopeScaledDepthBias;

    /// <summary>1-5: shadow strength (0-1, uploaded to ShadowParams1.Y; 1 means direct light falls to zero under full occlusion). Runtime-tunable.</summary>
    public float ShadowStrength { get; set; } = DefaultShadowStrength;

    /// <summary>2-1 tier: global bloom switch. Fixed at initialization and automatically degraded away when the HDR off-screen path is unavailable.</summary>
    public bool BloomEnabled { get; set; } = DefaultBloomEnabled;

    /// <summary>2-1: bright-pass threshold in linear HDR before exposure. Runtime knob.</summary>
    public float BloomThreshold { get; set; } = DefaultBloomThreshold;

    /// <summary>2-1: soft-threshold knee width. 0 = hard threshold; larger values make the transition softer.</summary>
    public float BloomKnee { get; set; } = DefaultBloomKnee;

    /// <summary>2-1: bloom composite intensity added in linear space before ACES. 0 visually disables bloom without rebuilding resources.</summary>
    public float BloomIntensity { get; set; } = DefaultBloomIntensity;

    /// <summary>2-1: downsample-chain mip count, including the half-resolution first level. Fixed at initialization.</summary>
    public int BloomMipCount { get; set; } = DefaultBloomMipCount;

    /// <summary>2-1 contract clause 5: anti-aliasing mode. Mutually exclusive, fixed at initialization, and downgraded with logging when unsupported. Default is Taa.</summary>
    public AaMode AntiAliasing { get; set; } = DefaultAntiAliasing;

    /// <summary>2-2 contract clause 1: ambient-occlusion tier. Mutually exclusive, fixed at initialization, and downgraded to Off when unsupported.</summary>
    public AoMode AmbientOcclusion { get; set; } = DefaultAmbientOcclusion;

    /// <summary>2-2: AO sampling radius in world space, uploaded to gtaoMain parameters. Runtime knob.</summary>
    public float AoRadius { get; set; } = DefaultAoRadius;

    /// <summary>2-2 contract clause 5: AO composite strength applied before ACES. 0 visually disables AO without rebuilding resources.</summary>
    public float AoIntensity { get; set; } = DefaultAoIntensity;

    /// <summary>2-3 contract clause 1: global motion-vector switch. Independent from AaMode, fixed at initialization, forced on by TAA, and ignored by unsupported backends.</summary>
    public bool MotionVectors { get; set; } = DefaultMotionVectors;

    /// <summary>2-3 contract clauses 4/14: TAA jitter amplitude in NDC subpixel units. Effective only when TAA is active.</summary>
    public float JitterScale { get; set; } = DefaultJitterScale;

    /// <summary>2-3 contract clause 4: TAA jitter phase count for the Halton sequence. Runtime knob.</summary>
    public int JitterPhaseCount { get; set; } = DefaultJitterPhaseCount;

    /// <summary>2-3 contract clause 10: TAA history feedback weight in lerp(cur, clampedHist, fb). This is the value used
    /// once a pixel reprojects by a full pixel or more per frame; static pixels use TaaStaticFeedback instead. Runtime knob.</summary>
    public float TaaFeedback { get; set; } = DefaultTaaFeedback;

    /// <summary>2-3 contract clause 10: TAA history feedback weight for pixels with zero reprojection. The resolve kernel
    /// interpolates from this value to TaaFeedback over the first pixel of per-frame motion, which gives a still camera the
    /// long accumulation window that jitter convergence needs (1/(1-fb) frames) without adding ghosting to moving content.
    /// Lower it toward TaaFeedback if lighting that changes very fast starts to smear; setting the two equal restores
    /// uniform blending. Runtime knob.</summary>
    public float TaaStaticFeedback { get; set; } = DefaultTaaStaticFeedback;

    /// <summary>2-3 contract clause 10: TAA neighborhood variance-clipping range. Runtime knob.</summary>
    public float TaaVarianceClipGamma { get; set; } = DefaultTaaVarianceClipGamma;

    /// <summary>2-3 contract clause 17: RCAS lobe attenuation for the post-tonemap sharpening pass, in [0, 1],
    /// where 1 matches FSR1's maximum sharpness (its "stops" parameter maps here as exp2(-stops), so 0.5 is one stop
    /// and 0.25 is two). Runtime knob for the strength, but <b>not</b> for whether the pass exists: a value above zero
    /// at initialization is what makes the TAA tier register PostColor and RenderPost, and that decision is fixed for
    /// the process. Lowering it to 0 later leaves the extra Post pass in place and merely zeroes the lobe, which makes
    /// the filter an identity - the same shape as BloomIntensity, which disables bloom visually without freeing anything.
    /// Symmetrically, raising it above zero in a process that started at zero does nothing at all, because the slots
    /// were never registered. <b>The default is 0, so sharpening is off unless
    /// <see cref="DefaultTaaSharpness"/> is raised before Init(); see there for the cost this gate is guarding.</b>
    /// Only effective under AaMode.Taa; the Fxaa tier owns the same slot and resolves with FXAA instead.</summary>
    public float TaaSharpness { get; set; } = DefaultTaaSharpness;

    /// <summary>Which filter FinalBlit must apply when its source is <see cref="FrameSchedule.PostColor"/>, derived from
    /// the AA tier that registered the slot. Shared by all four backends rather than reimplemented in each, since the
    /// mapping is a property of the contract and not of any one API. Safe to call at blit time because the AA tier is
    /// finalized during initialization and fixed for the process, so this cannot disagree with the tier that decided to
    /// create PostColor. Reachable only when PostColor exists; the Off/Msaa4x arms are unreachable in practice and return
    /// Copy so that a future tier registering the slot presents a correct image instead of an accidental filter.</summary>
    public static PostResolve PostResolveFilter() => Current.AntiAliasing switch
    {
        AaMode.Fxaa => PostResolve.Fxaa,
        AaMode.Taa => PostResolve.Rcas,
        _ => PostResolve.Copy,
    };

    /// <summary>2-4 contract clause 1: global-illumination tier. Mutually exclusive, fixed at initialization, and downgraded to Off when unsupported.</summary>
    public GiMode GlobalIllumination { get; set; } = DefaultGlobalIllumination;

    /// <summary>2-4 clause 4: proxy SDF volume resolution. Fixed at initialization.</summary>
    public int GiSdfResolution { get; set; } = DefaultGiSdfResolution;

    /// <summary>2-4 clause 4: world-space horizontal size jointly covered by the SDF volume and probe grid. Fixed at initialization.</summary>
    public float GiVolumeSize { get; set; } = DefaultGiVolumeSize;

    /// <summary>2-4 clauses 7/11: probe-grid X resolution. Fixed at initialization.</summary>
    public int GiProbeGridX { get; set; } = DefaultGiProbeGridX;

    /// <summary>2-4 clauses 7/11: probe-grid Y resolution. Fixed at initialization.</summary>
    public int GiProbeGridY { get; set; } = DefaultGiProbeGridY;

    /// <summary>2-4 clauses 7/11: probe-grid Z resolution. Fixed at initialization.</summary>
    public int GiProbeGridZ { get; set; } = DefaultGiProbeGridZ;

    /// <summary>2-4 clauses 8/11: rays traced per probe update. Fixed at initialization.</summary>
    public int GiRaysPerProbe { get; set; } = DefaultGiRaysPerProbe;

    /// <summary>2-4 clause 8: probe-update amortization divisor. 1 means full update each frame. Fixed at initialization.</summary>
    public int GiProbeUpdateDivisor { get; set; } = DefaultGiProbeUpdateDivisor;

    /// <summary>2-4 clauses 4/5: maximum SDF ray-march steps for probe rays. Fixed at initialization.</summary>
    public int GiTraceMaxSteps { get; set; } = DefaultGiTraceMaxSteps;

    /// <summary>2-4 clause 7: runtime toggle for Chebyshev visibility weighting during probe sampling.</summary>
    public bool GiChebyshevOcclusion { get; set; } = DefaultGiChebyshevOcclusion;

    /// <summary>2-4 clauses 9/12: indirect-diffuse composite strength. 0 visually disables GI without rebuilding resources.</summary>
    public float GiIntensity { get; set; } = DefaultGiIntensity;

    /// <summary>2-4 clause 7: probe temporal hysteresis weight. Higher values are steadier but converge more slowly.</summary>
    public float GiHysteresis { get; set; } = DefaultGiHysteresis;

    /// <summary>2-4 clause 13 / Step 5: independent EMA weight for probe-validity classification. Runtime knob.</summary>
    public float GiBackfaceHysteresis { get; set; } = DefaultGiBackfaceHysteresis;

    /// <summary>2-4 clause 13 / Step 5: global switch for probe-validity classification. Runtime-toggleable.</summary>
    public bool GiProbeValidity { get; set; } = DefaultGiProbeValidity;

    /// <summary>2-4 clause 7: probe normal bias in world units, used to reduce self-occlusion and thin-surface light leaks. Runtime knob.</summary>
    public float GiNormalBias { get; set; } = DefaultGiNormalBias;

    /// <summary>2-4 clause 5 / Step 2c: maximum sphere-tracing steps for a single GI shadow ray. Runtime knob.</summary>
    public int GiShadowSteps { get; set; } = DefaultGiShadowSteps;

    /// <summary>2-4 clause 5 / Step 2c: multi-bounce feedback gain for the previous-frame atlas sample. Runtime knob.</summary>
    public float GiBounceGain { get; set; } = DefaultGiBounceGain;

    /// <summary>2-4 clause 5 / Step 2c: whether point and spot lights also use SDF shadow marching. Disabled by default. Runtime knob.</summary>
    public bool GiPunctualShadow { get; set; } = DefaultGiPunctualShadow;

    /// <summary>2-4 clause 13 / Step 5: backface-hit threshold used by probe-validity classification. Runtime knob.</summary>
    public float GiBackfaceThreshold { get; set; } = DefaultGiBackfaceThreshold;

    /// <summary>2-4: log interval in frames for DDGI+SDF runtime telemetry. 0 disables the heartbeat.</summary>
    public int GiLogIntervalFrames { get; set; } = DefaultGiLogIntervalFrames;

    /// <summary>2-5 clause 1: sky mode. Fixed at initialization because the sky resources and skybox material are chosen during registration/construction.</summary>
    public SkyMode Sky { get; set; } = DefaultSky;

    /// <summary>2-5 clause 7: Sky-View LUT width in the azimuth direction. Fixed at initialization.</summary>
    public int SkyViewLutWidth { get; set; } = DefaultSkyViewLutWidth;

    /// <summary>2-5 clause 7: Sky-View LUT height from zenith to nadir. Fixed at initialization.</summary>
    public int SkyViewLutHeight { get; set; } = DefaultSkyViewLutHeight;

    /// <summary>2-5 clause 7: single-scattering march step count for the Sky-View LUT. Runtime knob.</summary>
    public int SkyRayMarchSteps { get; set; } = DefaultSkyRayMarchSteps;

    /// <summary>2-5 clause 11: side length of the prebaked cloud-noise texture. Fixed at initialization.</summary>
    public int CloudNoiseSize { get; set; } = DefaultCloudNoiseSize;

    /// <summary>2-5 clause 12: global switch for the aerial-perspective 3D LUT path. Fixed at initialization.</summary>
    public bool AerialPerspective { get; set; } = DefaultAerialPerspective;

    /// <summary>2-5 clause 12: farthest distance of the aerial-perspective froxel volume, in kilometers. Runtime knob.</summary>
    public float AerialMaxDistanceKm { get; set; } = DefaultAerialMaxDistanceKm;

    /// <summary>2-5 clause 12: aerial-perspective intensity scale. Runtime knob; values above 1 are allowed for artistic amplification on small-scale scenes.</summary>
    public float AerialIntensity { get; set; } = DefaultAerialIntensity;

    /// <summary>
    /// 2-6 clause 2: master switch for material-texture mip chains. Unlike most tiers this is locked at texture creation
    /// rather than per frame: a chain is either baked into the resource at upload time or it is not, so flipping this at
    /// runtime only affects textures created afterwards. Turning it off restores the pre-2-6 single-level behaviour exactly.
    /// </summary>
    public bool TextureMipmaps { get; set; } = DefaultTextureMipmaps;

    /// <summary>
    /// 2-6 clause 2: textures whose larger dimension is below this many pixels keep a single level even when their policy
    /// asks for a chain. Small maps are rarely minified enough to alias, while a chain still costs an extra resource
    /// footprint and, on D3D12, an extra 256-byte-aligned row-pitch block per level - which for tiny textures is a
    /// larger relative overhead than the 33% the geometric series suggests.
    /// </summary>
    public int TextureMipMinSize { get; set; } = DefaultTextureMipMinSize;

    /// <summary>
    /// 2-6 clause 2: anisotropic sample count for the material sampler; 1 means isotropic trilinear filtering.
    /// This is the knob that trades blur against aliasing. Trilinear alone picks its level from the longer screen-space
    /// axis, so surfaces seen at a grazing angle - distant hillsides, ground receding to the horizon - are filtered as if
    /// they were minified equally in both directions and come out over-blurred. Anisotropy is the actual fix for that,
    /// not a refinement of it.
    ///
    /// 2-6 clause 6: the default is 16, chosen from a measured A/B rather than convention. Enabling mip chains alone
    /// costs about 7.5% of the spatial high-frequency energy on distant grazing rock while cutting frame-to-frame
    /// difference by 9-24% - it trades visible sharpness for stability. Anisotropy buys that sharpness back: the
    /// high-frequency energy returns to within 2% of the un-mipped image at a count of 4 and does not improve further
    /// at 8 or 16. What continues to improve past 4 is temporal stability, and only 16 keeps the frame-to-frame
    /// difference clearly below the un-mipped baseline while holding the restored sharpness. 4 and 8 recover the
    /// sharpness but let the shimmer return to roughly the un-mipped level, which would defeat the reason mip chains
    /// were added. Note that the A/B measured image quality only; the GPU cost of the count was not measured, so a
    /// platform that finds 16 too expensive should lower this rather than assume it is free.
    ///
    /// Values above 1 are ignored by a backend that does not support them, and are harmless on single-level textures
    /// because anisotropic filtering degenerates to bilinear when there is only one level to choose from.
    /// </summary>
    public int TextureMaxAnisotropy { get; set; } = DefaultTextureMaxAnisotropy;

    /// <summary>
    /// 2-6 clause 5: whether normal-map mip generation measures the variance it filters away and folds it into
    /// roughness in the shader (Toksvig). Mip filtering plus renormalization gives a coarse texel one unit normal and
    /// leaves its authored roughness alone, so a footprint that really covers many differing normals keeps a narrow
    /// specular lobe it has no right to - which is what makes distant specular highlights flicker as the footprint
    /// shifts between frames. Anisotropy and more mip levels cannot fix this; the lost lobe width has to be returned
    /// as roughness.
    ///
    /// Like <see cref="TextureMipmaps"/> this is locked at texture creation, because the measurement is baked into the
    /// normal map's alpha channel at upload time. Switching it off writes the neutral value into that channel instead,
    /// which makes the shader's mapping an exact no-op - so the off state costs no shader branch and no extra
    /// constant, and only affects textures created afterwards.
    ///
    /// Unlike TextureMipmaps it can nevertheless be redone in place, which is what IGraphics.RebuildNormalVarianceTextures
    /// exists for: only the alpha of the levels below zero changes, so the level count and the GPU resource survive and an
    /// in-place pixel upload from the retained level-0 pixels carries it. That path is D3D12-only, because Vulkan and Metal
    /// release those pixels once the upload batch has run; elsewhere this still needs a restart to compare.
    ///
    /// Expect the effect to be narrow rather than global. It does nothing for a material without a normal map, and
    /// almost nothing for one that is already near-fully rough, since the perturbed roughness saturates. The materials
    /// it visibly changes are those pairing a detailed normal map with low roughness.
    /// </summary>
    public bool TextureNormalVariance { get; set; } = DefaultTextureNormalVariance;

    /// <summary>
    /// 2-6 clause 7: mip level-of-detail bias applied to the material texture fetches in the main pixel shader.
    /// Negative sharpens by selecting a finer level than the screen-space derivatives ask for.
    ///
    /// This only has a defensible meaning under TAA, which is why <see cref="EffectiveTextureLodBias"/> zeroes it in
    /// every other tier rather than leaving that gate to each backend. Jitter is subpixel supersampling: with the
    /// default Halton(2,3) sequence of <see cref="JitterPhaseCount"/> phases a converged pixel is the mean of eight
    /// distinct subpixel positions, so it carries roughly 2.8x the linear sampling density of a single sample and can
    /// reconstruct detail that one sample per pixel would alias. Mip selection, however, is computed per fetch from
    /// derivatives that know nothing about jitter, so it keeps picking the level appropriate to one sample - and the
    /// extra density is spent re-resolving detail that was already filtered away. The bias is what hands that
    /// headroom back. Without accumulation the same bias is simply undersampling, which is why it is tier-gated and
    /// not a free sharpening knob.
    ///
    /// The default -0.5 is half a level, or 1.41x linear resolution, which sits well inside the 2.8x budget. -1.0 is
    /// the point where the bias consumes the whole budget and leaves nothing for the aliasing that jitter was
    /// supposed to absorb, so it is the ceiling of what is reasonable rather than the next step up.
    ///
    /// Interaction with <see cref="TextureNormalVariance"/> is self-consistent rather than conflicting: a finer normal
    /// level carries a mean resultant length nearer 1, so Toksvig folds less variance into roughness. That is the
    /// correct answer, not a cancellation - a sharper footprint really does average fewer differing normals.
    ///
    /// Textures with a single level are unaffected: a negative bias clamps at level 0. That is what makes the
    /// shared albedo slot safe, since sprites and MSDF font atlases ask for <see cref="TextureMipPolicy.None"/>.
    /// </summary>
    public float TextureLodBias { get; set; } = DefaultTextureLodBias;

    /// <summary>
    /// 2-6 clause 7: <see cref="TextureLodBias"/> resolved against the anti-aliasing tier and clamped to a range every
    /// backend can honour. Zero outside <see cref="AaMode.Taa"/>, for the reason given on that property.
    ///
    /// The bound is 2 because Vulkan only guarantees maxSamplerLodBias >= 2.0 and WGSL requires the bias operand to
    /// stay within [-16, 15.99]; a request past that would be silently clamped on one backend and honoured on another,
    /// which is exactly the kind of per-backend divergence a shared tier value exists to prevent.
    ///
    /// Read at shader-assembly time, which happens after each platform app has finished rewriting the tier, so a
    /// backend that downgraded TAA is seen as downgraded here. A per-frame TAA bypass (clause 15, size mismatch during
    /// resize) cannot be seen, since the value is baked: those frames render slightly sharper without accumulation,
    /// which is a transient, not the frame-to-frame shaking that made jitter itself worth gating per frame.
    ///
    /// Get-only on purpose, and not only for immutability: this type is persisted through JsonUtils.Serialize, whose
    /// options set IgnoreReadOnlyProperties, so this and <see cref="TextureLodBiasLiteral"/> stay out of Settings.json.
    /// Giving either one a setter would start writing a derived value into the file, where it would then be read back as
    /// though it were the authored one - <see cref="TextureLodBias"/> is the only member here that should round-trip.
    /// </summary>
    public float EffectiveTextureLodBias
        => AntiAliasing == AaMode.Taa ? Math.Clamp(TextureLodBias, -2f, 2f) : 0f;

    /// <summary>
    /// 2-6 clause 7: <see cref="EffectiveTextureLodBias"/> formatted as a shader-source literal, wrapped in
    /// parentheses so it stays one token wherever the four backends paste it - HLSL and GLSL and MSL take it through a
    /// #define, WGSL through the const substitution it uses for HDR_CHAIN.
    ///
    /// It lives here rather than at the four injection sites because the formatting is culture-sensitive: on a locale
    /// whose decimal separator is a comma, the default ToString would emit (-0,5) and every shader in the process
    /// would fail to compile. One invariant formatter is what keeps that from being four separate latent bugs.
    /// </summary>
    public string TextureLodBiasLiteral
        => "(" + EffectiveTextureLodBias.ToString("0.0#####", System.Globalization.CultureInfo.InvariantCulture) + ")";

    /// <summary>
    /// 2-6 clause 6: resolves <see cref="TextureMaxAnisotropy"/> against a backend's own ceiling. The value is shared
    /// by all four backends but their ceilings are not: D3D12 and Metal both cap the count at 16 by specification,
    /// WebGPU leaves it implementation-defined and silently clamps, and Vulkan exposes it per device as
    /// limits.maxSamplerAnisotropy - which is why the cap is a parameter rather than a constant. Clamping here rather
    /// than at each call site keeps an out-of-range setting from becoming a device-creation failure on one backend and
    /// a silent no-op on another; 1 is returned for any request at or below 1, which is the isotropic case.
    /// </summary>
    public static int ClampAnisotropy(int requested, int backendMax = 16)
        => Math.Clamp(requested, 1, Math.Max(1, backendMax));

    static RenderQuality? _currentFallback;

    /// <summary>Runtime access entry point. Consumers should always read this property instead of static Default* fields.</summary>
    public static RenderQuality Current
    {
        get
        {
            var rq = DeviceServices.BaseApp?.Settings?.RenderQuality;
            if (rq != null)
                return rq;
            return _currentFallback ??= new RenderQuality();
        }
    }
}
