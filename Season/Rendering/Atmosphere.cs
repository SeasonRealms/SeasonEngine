// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// Runtime state and physical parameters for procedural atmosphere rendering (2-5 Step A; see sections 2-5 in the <see cref="RenderQuality"/> header for contract terms).
/// Positioned like <see cref="SceneLighting"/>: the application writes every frame, and <c>SkyAtmosphereEffect</c> reads every frame.
/// The engine does not decide which arc the sun should follow on behalf of the application (the Sample delegates this to <see cref="DayNightCycle"/>,
/// while real projects can hook up TOD curves or astronomical ephemerides). All fields are runtime knobs and require no restart.
///
/// ── Units and coordinate conventions (shared across all four backends and the CPU side; any change must keep shader constant semantics in sync) ──
/// - All length units are **kilometers** (float precision is friendlier at atmospheric scale; the engine world uses meters, and the two are never converted into each other:
///   the atmosphere is an "infinitely distant" background, and observer altitude is supplied separately by <see cref="ViewAltitudeKm"/>, independent of camera Y).
/// - Scattering/extinction coefficients use units of 1/km and represent sea-level values; they decay exponentially with height according to their own scale heights.
/// - <see cref="SunDirection"/> / <see cref="MoonDirection"/> are unit vectors **from the observer toward the celestial body**
///   (opposite to the "light travel direction" used by <see cref="LightSource.Direction"/>); this makes
///   <c>dot(viewDir, SunDirection)</c> directly equal to the cosine of the scattering angle in the phase function.
/// - The planet center always sits directly below the observer at <see cref="GroundRadiusKm"/> + <see cref="ViewAltitudeKm"/>,
///   and the local up direction is always world +Y.
///
/// ── Sky-View LUT parameterization (**single source of truth**: the main shader PS and the skyView kernel must stay textually identical) ──
/// Forward mapping (PS: world view direction d → LUT uv):
/// <code>
///   u = atan2(d.x, -d.z) / (2π) + 0.5      // u=0.5 points to -Z (south), u=0/1 points to +Z (north) = seam
///   v = 0.5 - 0.5 * sign(d.y) * sqrt(abs(d.y))   // v=0 zenith, 0.5 horizon, 1 nadir
/// </code>
/// Inverse mapping (kernel: texel-center uv → world direction):
/// <code>
///   phi = (u - 0.5) * 2π
///   s = 1 - 2v;  cosZ = sign(s) * s * s;  sinZ = sqrt(max(0, 1 - cosZ*cosZ))
///   dir = float3(sinZ * sin(phi), cosZ, -sinZ * cos(phi))
/// </code>
/// Design rationale in two points:
/// 1. **Use absolute world azimuth and place the seam at +Z (north)** instead of Hillaire's original "sun-relative azimuth symmetry" parameterization.
///    The latter saves half the width, but it requires the PS to know the light azimuth at sampling time. On the PS side,
///    SceneLights.directionalIndex can become -1 during sun/moon handoff when intensity falls to zero and gets filtered out, and extending the UBO for that would touch all four backends.
///    With absolute azimuth, the PS only needs d itself. The seam is placed at north because the celestial arc from <see cref="DayNightCycle"/> runs from 0° (+X east)
///    through -90° (-Z south) to 180° (-X west), and **never crosses north**, so the Mie forward-scattering spike never lands on the seam.
///    At the seam, only one texel's worth of interpolation is lost (256 columns ≈ 1.4°), and the azimuth gradient is already gentle there.
/// 2. **Fold v with sqrt** to concentrate sampling density near the horizon (around v=0.5), which is the main cause of "horizon color banding":
///    uniformly spaced v does not provide enough angular resolution near the horizon, where optical depth changes abruptly along the ray and produces bands.
/// Half-texel convention: the kernel uses <c>u=(id.x+0.5)/W</c> and <c>v=(id.y+0.5)/H</c>, while the PS computes continuous values directly,
/// so bilinear sampling lands exactly on texel centers without a half-pixel offset.
///
/// ── Transmittance LUT parameterization ──
/// <c>u = (mu + 1) / 2</c> (mu = cosine between the view ray and local up), <c>v = (r - Rg) / (Rt - Rg)</c>.
/// **Whenever the ray hits the ground, record transmittance = 0**: planetary self-occlusion is baked in as well, so the consumer side needs no shadow ray.
/// Celestial transmittance is then available from a single lookup (as Hillaire intended), and "the sun drops below the horizon → the sky automatically becomes night"
/// naturally falls out of the integration result rather than from branching.
///
/// ── Dual-light symmetric model (Step B) ──
/// The sun and moon **simultaneously** occupy one scattering term each (<c>Sun*</c> / <c>Moon*</c>, two isomorphic field groups), instead of "feeding only one at a time".
/// This is the formal solution to the Step A warning of "do not switch to the moon at the sunset instant": the pop happens because, in a single-light model,
/// irradiance must jump across two orders of magnitude in one frame. With both terms present, each <c>elev01</c> continuously fades out or in,
/// and sky luminance becomes the sum of two continuous curves, yielding C0 continuity at the handoff with no jump.
/// It also preserves Rayleigh-blue moonlight after sunset, instead of leaving only the full-screen constant floor from <see cref="NightAirglow"/>.
/// The cost is one extra Transmittance sample per step in the skyView kernel (16 steps × 1 tap, measured cost is negligible).
/// </summary>
public static class Atmosphere
{
    // ── Runtime state (written every frame by the application; read by SkyAtmosphereEffect.Record) ──

    /// <summary>Unit vector **from the observer toward the sun** (opposite to LightSource.Direction; normalized inside Record if non-unit).
    /// The Sample feeds <c>DayNightCycle.BodyPosition(phase, forMoon: false)</c>: when phase &gt; 0.5, that arc naturally drops below
    /// the horizon, and the Transmittance LUT records 0 when it hits the ground, so nightfall happens automatically without day/night branching.
    /// The moon follows <see cref="MoonDirection"/> independently in parallel; see the class header section "dual-light symmetric model".</summary>
    public static Vector3 SunDirection = Vector3.UnitY;

    /// <summary>Sun linear color (multiplied onto the scattering result; daylight is roughly (1, 0.96, 0.9)).</summary>
    public static Vector3 SunColor = Vector3.One;

    /// <summary>Top-of-atmosphere solar irradiance scale (dimensionless, in the same units as the engine's linear light intensity).
    /// Why the default is 12 (derived from the unit-irradiance response of a CPU recomputation of the skyView integral, not chosen arbitrarily):
    /// at a 60° solar elevation, zenith blue-channel luminance = 0.0209×E; with E=12, this gives L_B≈0.25.
    /// After the full FinalBlit chain of exposure 1.0 + ACES + gamma 2.2, that lands around sRGB (74, 115, 166), a typical sky blue.
    /// Meanwhile horizon glow is about 0.6 (below BloomThreshold=1.0, so it does not trigger bloom during the day), while at a low 5° solar elevation
    /// the sunrise horizon reaches 3.7 and intentionally crosses the threshold, so the glow band blooms naturally.
    /// Raising this too far blows through the ACES shoulder and whitens the entire day sky (E=120 already pushes the zenith to 2.5, turning the whole image pure white).
    /// This is the only knob for "overall sky brightness"; adjusting it does not change hue.</summary>
    public static float SunIrradiance = 12f;

    /// <summary>Sun **angular radius** in degrees. 0.2665 is the real value (0.533° angular diameter).
    /// It must stay physically correct rather than becoming an art knob at both consumption sites:
    /// 1. <c>SkyLighting.EvaluateDiskTransmittance</c> averages over the solar disk, so direct-light intensity reaches zero continuously at sunset
    ///    as "the solar disk is gradually swallowed by the horizon", instead of abruptly turning off in one frame (see the calibration notes in that method summary).
    /// 2. The analytic sun disk in the main shader uses the test <c>dot(viewDir, SunDirection) &gt; cos(angularRadius)</c>.
    /// **Not part of the static LUT rebake criteria**: Transmittance and MultiScatter are pure functions of (mu, r) and do not depend on the body's angular size.
    /// Increasing it only makes the sun disk larger and lengthens sunset transitions; it does not alter energy calibration anywhere (radiance is normalized inversely by solid angle).</summary>
    public static float SunAngularRadiusDeg = 0.2665f;

    /// <summary>Unit vector **from the observer toward the moon** (same convention as <see cref="SunDirection"/>).
    /// Defaults to -Y (below the horizon, meaning this term stays at 0), so applications that do not feed moon data behave bit-for-bit like Step A.</summary>
    public static Vector3 MoonDirection = -Vector3.UnitY;

    /// <summary>Moon linear color. (0.55, 0.68, 1) is used here: moonlight is physically reflected sunlight and is spectrally near-neutral with a slight warmth,
    /// while this cool blue tint is a perceptual compensation for the Purkinje effect (dark vision being more sensitive to shorter wavelengths), so it is an art convention rather than a physical value.
    /// It must stay in sync with the Sample's <c>MoonLightColor</c>.</summary>
    public static Vector3 MoonColor = new Vector3(0.55f, 0.68f, 1f);

    /// <summary>Top-of-atmosphere moon irradiance scale. 0.6 = <see cref="SunIrradiance"/> × 5% (matching the Sample-side
    /// ratio <c>MoonPeakIntensity/SunPeakIntensity = 0.2/4</c>; a real full moon is roughly 1/400000 of daylight,
    /// and that scale is pitch black under ACES with exposure 1.0, so this uses a perceptually visible dark-adapted value instead).
    /// Setting this to 0 reverts to the Step A single-light night sky with only the airglow floor.</summary>
    public static float MoonIrradiance = 0.6f;

    /// <summary>Moon angular radius in degrees. 0.259 is the real value (0.518° angular diameter, almost the same apparent size as the sun, which is why total solar eclipses are possible).
    /// Conventions and consumption points are the same as <see cref="SunAngularRadiusDeg"/>.</summary>
    public static float MoonAngularRadiusDeg = 0.259f;

    /// <summary>Night-sky airglow floor radiance. It is tinted using the channel ratios of <see cref="RayleighScattering"/>
    /// (about (0.175, 0.41, 1.0), giving a naturally cool blue), so the night sky is not pure black but a readable deep blue.
    /// The default 0.004 corresponds to radiance (0.0007, 0.0016, 0.004), more than two orders of magnitude below the daytime zenith (~0.25),
    /// so it is completely invisible during the day. **It is a constant term independent of view direction**:
    /// once the dual-light model is active, directional night-sky gradients come from moonlight scattering, while this term only provides fallback visibility after the moon has set as well.</summary>
    public static float NightAirglow = 0.004f;

    // ── Analytic celestial disks and star field (b11). Both are evaluated **per pixel**
    // by the renderMode==3 branch in the main shader rather than going through the Sky-View LUT.
    // Each LUT texel spans about 1.4°, while the sun disk is only 0.53° across, so placing it in the LUT would only yield
    // a bright block with energy diluted by roughly (0.53/1.4)^2 and flickering from texel to texel as the body moves.
    // Upload path: SkyParams0..4 at the tail of SceneLightParams (injected from a single point by SkyLighting.ApplyTo(ref ...)).

    /// <summary>Linear radiance at the center of the solar disk (outside the atmosphere; the consumer side multiplies by mean in-disk transmittance and <see cref="SunColor"/>).
    /// **This is intentionally a perceptual value rather than a physical one**. The physical value would be <see cref="SunIrradiance"/> divided by the solar-disk solid angle
    /// Ω = 2π(1−cos 0.2665°) = 6.798e-5 sr, which gives 12 / 6.798e-5 ≈ **176500**. But under this engine's exposure 1.0 + ACES pipeline,
    /// inputs above roughly 16 are already saturated to pure white, so **on the disk itself** 176500 and 30 look no different; the only visible difference is Bloom.
    /// With BloomThreshold=1.0, 176500 turns half the screen into white fog. So 30 is chosen: it stays far above horizon glow (about 3.7 at 5° solar elevation),
    /// keeping the sun disk the brightest thing in the frame and intentionally driving bloom, while avoiding runaway glare.
    /// Adjusting this only changes the sun disk and its bloom, and **does not affect sky scattering or direct-light illumination** (those use <see cref="SunIrradiance"/>).</summary>
    public static float SunDiskRadiance = 30f;

    /// <summary>Linear radiance at the center of the lunar disk (same convention as <see cref="SunDiskRadiance"/>, then multiplied by the phase mask and transmittance).
    /// The physical value would be 0.6 / 6.420e-5 ≈ 9350, but this also uses a perceptual value instead: 3 is about 750 times the night-sky background
    /// (airglow is roughly 0.004), enough to keep the moon the brightest object in a night scene and to nudge it slightly above BloomThreshold for a faint halo
    /// that matches the visual impression of a real moon glow, while still being dim enough to preserve the light/dark phase boundary across the disk.
    /// Raising it into the same order of magnitude as the sun disk would wash the whole disk white and make the phase invisible.</summary>
    public static float MoonDiskRadiance = 3f;

    /// <summary>Baseline star radiance (the shader further multiplies this by each star's power-law brightness weight and by visibility derived from <see cref="StarVisibilityTwilightDeg"/>).
    /// 0.15 makes the brightest stars about 37 times the airglow background (blue channel 0.004), landing around 127/255 after ACES tone mapping:
    /// prominent at night with distinguishable warm/cool color variation, yet still far below the lunar disk (3).
    /// [2026-08 tuning] The previous value 0.03 landed at only about 43/255 after tone mapping, and dark-region contrast compression by human vision made stars read as "dim gray dots", so it was increased by 5x.</summary>
    public static float StarRadiance = 0.15f;

    /// <summary>Required **negative solar elevation** in degrees for the star field to become fully visible. 18 is not an artistic guess but the astronomical
    /// "end of astronomical twilight": once the sun drops 18° below the horizon, sky scattering has fallen below the dimmest visible stars, so the full star field appears
    /// (civil 6° and nautical 12° are the other standard twilight levels).
    /// Why an explicit visibility term is needed instead of assuming "additive blending will naturally drown them out": HDR addition does not drown out high-frequency bright points.
    /// If star intensity is set to a night-visible 0.15, then adding it over a daytime zenith of 0.25 still leaves a 60% difference, which would fill the blue sky with visible specks.
    /// In reality, "you cannot see stars in daylight" comes from eye adaptation and contrast thresholds, which are outside this engine's linear light transport model, so it must be modeled explicitly.</summary>
    public static float StarVisibilityTwilightDeg = 18f;

    /// <summary>Rotation angle of the star field around <see cref="StarPoleAxis"/>, in radians. Written every frame by the application
    /// (the Sample uses <c>DayNightCycle.StarAngle(phase)</c>, driven by the same source and speed as the solar arc angle).
    /// A constant 0 means the star field stays fixed in world space, with no diurnal motion even though the star map is still present.</summary>
    public static float StarRotation = 0f;

    /// <summary>Rotation axis for the star field's diurnal motion (**celestial pole axis**; world-space unit vector; normalized before upload, falling back to +Y for zero vectors).
    /// Written every frame by the application (the Sample uses <c>DayNightCycle.CelestialPole</c>, the normal of the great circle containing the sun/moon arc;
    /// in this repository that arc tilts 35° toward the south, so the celestial pole tilts 35° from zenith toward the north).
    ///
    /// The default +Y means rotation around the zenith axis, equivalent to placing the observer at the north pole:
    /// stars only translate along their altitude circles and never rise or set.
    /// This was a bookkeeping placeholder written down in 2-5 Step B when there was no upload path for the axis yet, and Step C fulfills it by extending SkyParams4.
    /// Visually, using the celestial-pole axis makes stars rise **diagonally** over the eastern horizon (at 90°−35°=55° relative to the horizon) and trace concentric circles around the north celestial pole,
    /// while stars near the pole become circumpolar and never set, matching the look of long-exposure star-trail photography.</summary>
    public static Vector3 StarPoleAxis = Vector3.UnitY;

    // ── Procedural clouds (Step C). Like the analytic celestial disks, they are evaluated **per pixel**
    // by the main shader and do not go through the Sky-View LUT.
    // Upload path: CloudLayerA/B[3] + CloudParams0/1 at the tail of SceneLightParams (injected from a single point by SkyLighting.Apply).
    // Consumption requires a pre-baked tileable noise texture (compute://sky/cloudnoise), so the gate is
    // FrameSchedule.CloudNoiseTexture != null; see SkyLighting.Apply and clause 11 in 2-5 for details.

    /// <summary>Current cloud state (layer shapes + global optical parameters). Written every frame by the application, usually as
    /// an interpolated state between presets such as <see cref="SkyState.Clear"/>/<c>Fair</c>/<c>Overcast</c>/<c>Storm</c>
    /// via <c>SkyState.Lerp</c> along a weather curve.
    ///
    /// The default is <see cref="SkyState.Clear"/> (LayerCount=0), so applications that do not feed clouds remain bit-for-bit identical to Step B.
    /// This default is not just for convenience: when the noise texture is missing, the DX side binds a 1×1 white fallback texture,
    /// and feeding solid white into the density remap effectively means "max density", turning the entire sky into a flat dead-gray overcast.
    /// That is why both protections are needed: a zero-layer default and texture gating.</summary>
    public static SkyState Clouds = SkyState.Clear;

    /// <summary>Accumulated horizontal wind offset for each cloud layer (km, world XZ; indices align with <c>Clouds.Layers</c>).
    /// Integrated frame by frame by <c>SkyLighting.AdvanceClouds(dt)</c>; applications usually do not write this directly
    /// unless they need to "jump" to a particular cloud pattern, such as when restoring a save.</summary>
    public static readonly Vector2[] CloudWindOffsetKm = new Vector2[SkyState.MaxLayers];

    // ── Physical parameters (static parameters: changes trigger a Transmittance LUT rebake; see SkyAtmosphereEffect.Record) ──

    /// <summary>Planet surface radius in km. 6360 is the commonly used simplified Earth value in the Hillaire/Bruneton line of work.</summary>
    public static float GroundRadiusKm = 6360f;

    /// <summary>Top-of-atmosphere radius in km. 6460 means a 100 km atmospheric thickness, roughly at the Karman-line scale.</summary>
    public static float AtmosphereRadiusKm = 6460f;

    /// <summary>Observer altitude in km above the surface. 0.2 = 200 m, matching the ground-elevation scale used by the Sample in this repository.
    /// It stays constant instead of following camera Y because camera movement of a few dozen meters inside the scene affects atmospheric optical depth far less than LUT quantization error does,
    /// and updating it every frame would only make the LUT wobble. Change it only for high-altitude or space viewpoints, where it will automatically trigger a rebake.</summary>
    public static float ViewAltitudeKm = 0.2f;

    /// <summary>Rayleigh scattering coefficients (1/km, sea level, separate RGB channels; Rayleigh has no absorption, so scattering = extinction).
    /// (0.005802, 0.013558, 0.033100) are Bruneton fitted values; the blue/red ratio is about 5.7, which is exactly why the sky is blue.</summary>
    public static Vector3 RayleighScattering = new Vector3(0.005802f, 0.013558f, 0.033100f);

    /// <summary>Rayleigh density scale height in km. 8 is the atmospheric scale height.</summary>
    public static float RayleighHeightKm = 8f;

    /// <summary>Mie scattering coefficient (1/km, sea level, grayscale). This is the scattering side of the aerosol-density control.</summary>
    public static float MieScattering = 0.003996f;

    /// <summary>Mie extinction coefficient (1/km, sea level). The amount by which it exceeds <see cref="MieScattering"/> is aerosol absorption,
    /// producing a single-scattering albedo of about 0.9. When tuning "haze vs. clarity", this pair should move proportionally; otherwise you change albedo instead.</summary>
    public static float MieExtinction = 0.004440f;

    /// <summary>Mie density scale height in km. 1.2 means aerosols are concentrated in the lower atmosphere, which is why the forward-scattering peak hugs the horizon at sunset.</summary>
    public static float MieHeightKm = 1.2f;

    /// <summary>Henyey-Greenstein anisotropy factor (0 = isotropic, approaching 1 = more forward-scattering). 0.8 sets the width of the halo around the sun.
    /// **Not part of the static LUT criteria**: Transmittance uses only extinction, and the multiple-scattering LUT follows Hillaire with an isotropic phase,
    /// so neither depends on g. Changing g at runtime therefore does not trigger a rebake and only affects the per-frame single-scattering phase in skyView.</summary>
    public static float MiePhaseG = 0.8f;

    /// <summary>Ground albedo (grayscale, Lambertian). 0.1 is in the range of dark vegetation or soil.
    /// It participates in two places: the ground bounce term in the multiple-scattering LUT (the <c>L_f</c> term from Hillaire 2020 §4),
    /// and the lower hemisphere of the CPU-side SH9 projection in <c>SkyLighting</c>. Increasing it brightens both the bottom of the sky and shadowed surfaces together, as in snowfields or deserts.
    /// **This is a static parameter** and belongs to the MS LUT rebake criteria.</summary>
    public static float GroundAlbedo = 0.1f;

    /// <summary>Multiple-scattering energy gain (1 = physical value, 0 = off, reverting to the pure single-scattering result from Step A).
    /// This is a runtime knob: the MS LUT itself is normalized as "unit irradiance + white light", so it is light-source independent and statically baked once.
    /// This gain is applied only on the skyView consumption side, so changing it does not trigger a rebake.
    /// Physically, it restores energy missing from single scattering alone, which is the reason for dark zeniths, gray horizons, and twilight fading out too early.</summary>
    public static float MultiScatterGain = 1f;
}
