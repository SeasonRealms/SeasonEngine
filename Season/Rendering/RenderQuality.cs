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
/// - 2-1 post stack: bloom is an AfterScene compute chain and FXAA is a post-tonemap raster pass. Both use the final HDR->LDR composite point and degrade cleanly when unavailable.
/// - 2-2 AO: GTAO-lite uses explicit SceneDepth, depth-texture compute input, half-resolution kernels, and AO composition before ACES. Mesh-level AO exclusion remains supported.
/// - 2-3 motion vectors + TAA: velocity is an independent tier, SceneVelocity is explicit, jitter is injected from a single Camera3D path,
///   history data rides existing constant buffers, transparent geometry does not write velocity, and TAA uses ping-pong history with controlled degradation.
/// - 1-7 cubemap + IBL: TextureCube is a minimal cross-platform type, SH9 irradiance/radiance ride the lighting UBO, diffuse picks either SH9 or constant ambient,
///   and the entire path falls back cleanly to the old ambient-only baseline.
/// - 2-4 DDGI + SDF: GI uses box/sphere proxies, accepts one-frame latency, stores all runtime parameters in the existing lighting UBO tail,
///   and keeps graceful fallback to the pre-GI image. Probe validity classification and Chebyshev visibility are runtime-tunable.
/// - 2-5 procedural sky: the sky path uses SkyAtmosphereEffect LUTs, a dual-light sun/moon model, optional procedural clouds,
///   and optional aerial perspective. Procedural sky, clouds, and AP all use explicit readiness gates and clean fallback paths.
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
