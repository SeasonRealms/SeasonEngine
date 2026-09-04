// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Engine.Panels;

/// <summary>
/// Central celestial-lighting driver, split out of Sky in 2026-08. It owns every lighting responsibility that is
/// independent of the skybox rendering representation: day-night evaluation, persistent sun and moon directional lights,
/// Atmosphere and DayNightCycle parameter feeding, weather and cloud evolution, procedural SH9 environment lighting,
/// fallback cloudtop-cube environment lighting, and global ambient and GI intensity control.
///
/// Sky now keeps only the pure visuals such as the six skybox faces, marker spheres, and fallback tinting. Each frame it reads
/// cached results from this class instead of re-evaluating DayNightCycle. The mode decision follows the same rule as the skybox:
/// a non-null <see cref="Season.Rendering.FrameSchedule.SkyViewTexture"/> means procedural sky mode, while null means the StaticCube fallback.
///
/// Ordering matters. CelestialLighting.Update must run before Sky.Update so the skybox and marker visibility read the newest
/// cached values, and Atmosphere parameters written here are available to the same-frame skyView kernel. The write order inside
/// Update also matters: Atmosphere -> weather clouds -> cloud advance -> SkyLighting.
/// </summary>
internal class CelestialLighting
{
    // Persistent light handles: register once in the constructor, then only mutate pose, intensity, and enable state in Update.
    Season.Rendering.LightSource sunLight;

    Season.Rendering.LightSource moonLight;

    // Day-night parameters. Phase counts elapsed day cycles, and since Step C the sun and moon follow independent full arcs
    // so both bodies can appear in the sky at the same time. Elevation still controls visibility and peak intensity.
    const float DayNightSpeed = 0.02f;      // Phase increment per second, giving an about 50-second day.
    // Shortened synodic cycle for the sample so a full moon-phase loop completes in a few minutes rather than taking far too long to observe.
    const float MoonSynodicDays = 4f;
    // Weather cycle period in seconds. It intentionally differs from the day-night period so weather does not always repeat at the same time of day.
    const float WeatherCycleSeconds = 120f;
    const float SunPeakIntensity = 4f;      // Peak sunlight intensity at high elevation.
    const float MoonPeakIntensity = 0.2f;   // Peak moonlight intensity before applying the moon-phase factor.
    static readonly Vector3 SunLightColor = new Vector3(1f, 0.96f, 0.9f);
    static readonly Vector3 MoonLightColor = new Vector3(0.55f, 0.68f, 1f);

    // Baseline top-of-atmosphere moon irradiance captured at construction time.
    // Each frame it is modulated by moon phase so direct moonlight, moonlit sky scattering, and SH9 environment lighting all dim together.
    readonly float _baseMoonIrradiance = Season.Rendering.Atmosphere.MoonIrradiance;

    // Previous App.Time value used to compute this frame's dt for cloud motion.
    // A negative value means "not initialized yet", so the first frame records time without advancing clouds.
    float _lastCloudTime = -1f;

    // Shared night-brightness knob, used by ambient scaling, fallback sky tinting, and fallback environment dimming.
    internal float NightSkyBrightness = 0.3f;

    // Baseline GI intensity captured from defaults at construction time so later per-frame writes do not accumulate drift.
    readonly float _baseGiIntensity = RenderQuality.DefaultGiIntensity;
    static readonly Vector3 DayAmbientColor = new Vector3(0.13f, 0.12f, 0.10f);
    static readonly Vector3 NightAmbientColor = new Vector3(0.06f, 0.08f, 0.14f);
    // Baseline SH9 diffuse intensity used as the day-night-scaled reference in fallback environment lighting.
    internal float BaseEnvDiffuseIntensity = 0.35f;

    // One-time procedural-sky mode snapshot. App.RegisterEffects runs before this object is constructed, so the value here is already final.
    readonly string? _skyViewTexture = Season.Rendering.FrameSchedule.SkyViewTexture;

    internal bool IsProceduralSky => _skyViewTexture != null;

    internal string? ProceduralSkyTexture => _skyViewTexture;

    // -- Per-frame cached values consumed by Sky for tinting, marker visibility, and moon phase. --
    internal float DayPhase { get; private set; }

    internal bool SunUp { get; private set; }

    internal bool MoonUp { get; private set; }

    internal float MoonPhase { get; private set; }

    public CelestialLighting()
    {
        // Step C gives the moon its own independent phase, which makes moon phases appear naturally instead of locking the moon opposite the sun.
        Season.Rendering.DayNightCycle.SynodicDays = MoonSynodicDays;

        // Register the persistent celestial lights once and reuse them forever. Sun and moon are no longer mutually exclusive,
        // and both cast shadows so the shared CSM cascade always follows whichever directional light is strongest.
        sunLight = App.Instance.Lighting.Add(new Season.Rendering.LightSource
        {
            Name = "Sun",
            Kind = Season.Rendering.LightKind.Directional,
            Color = SunLightColor,
            Intensity = SunPeakIntensity,
            Direction = new Vector3(0f, -1f, 0f),
            CastShadows = true,
            Priority = 100,
        });
        moonLight = App.Instance.Lighting.Add(new Season.Rendering.LightSource
        {
            Name = "Moon",
            Kind = Season.Rendering.LightKind.Directional,
            Color = MoonLightColor,
            Intensity = MoonPeakIntensity,
            Direction = new Vector3(0f, -1f, 0f),
            CastShadows = true,
            Priority = 100,
        });
    }

    public void Load()
    {
        // Procedural mode does not load the fallback cloudtop cube. Instead, SkyLighting projects SH9 from the same
        // Atmosphere state used by the sky rendering, so the lighting and the sky stay source-consistent and startup work is reduced.
        // The environment stays in Diffuse mode rather than DiffuseSpecular because the SkyIntensity control would otherwise try to
        // serve both cube reflections and DDGI sky misses at once. The tradeoff is that procedural mode temporarily loses environment
        // specular on smooth metallic surfaces until the Sky-View LUT is wired in as the proper specular source.
        if (_skyViewTexture != null)
        {
            App.Instance.SceneEnvironment = new Season.Rendering.EnvironmentMap
            {
                Mode = Season.Rendering.EnvironmentLightingMode.Diffuse,
                DiffuseIntensity = 1f,
                SkyIntensity = 1f,
            };
            return;
        }

        // Load the fallback environment cube on a background task. On some backends the async path is effectively synchronous,
        // so doing this inline would block startup for file reads, PNG decoding, cube upload, and SH9 projection.
        // While loading, SceneEnvironment stays null and the render path gracefully falls back to constant ambient lighting.
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                // Rebuild the fallback radiance cube from the six existing cloudtop skybox faces.
                // Face ordering matches the CubeFace convention, so the same PNGs can be reused directly.
                // DiffuseSpecular mode enables both diffuse SH9 and specular reflection, replacing the old constant ambient term.
                var env = await Season.Rendering.EnvironmentMap.LoadFromFacesAsync("Env/Cloudtop", new[]
                {
                    "Assets/cloudtop_rt.png",
                    "Assets/cloudtop_lf.png",
                    "Assets/cloudtop_up.png",
                    "Assets/cloudtop_dn.png",
                    "Assets/cloudtop_bk.png",
                    "Assets/cloudtop_ft.png",
                });
                if (env != null)
                {
                    env.Mode = Season.Rendering.EnvironmentLightingMode.DiffuseSpecular;

                    // Exposure compensation. The fallback cube textures are treated as linear even though the PNG data is sRGB-encoded,
                    // so DiffuseIntensity is reduced to keep the result bright enough to be useful but well below overexposure.
                    env.DiffuseIntensity = BaseEnvDiffuseIntensity;

                    // Assign by reference so the render thread can pick it up on the next frame.
                    App.Instance.SceneEnvironment = env;
                }
            }
            catch (Exception ex)
            {
                // Do not swallow background-task exceptions. Otherwise, a failed environment load would only look like unusually dark metals.
                App.Instance.AddLog(LogType.Error, $"{DateTime.UtcNow} [EnvironmentMap] background load failed err={ex}");
            }
        });
    }

    public void Update(float time)
    {
        // Evaluate the day-night cycle. Phase drives east-to-west motion on arcs tilted toward the south,
        // and since Step C the sun and moon move on independent full circles, allowing both bodies to appear together.
        DayPhase = App.Instance.Time * DayNightSpeed;
        Season.Rendering.DayNightCycle.Evaluate(DayPhase,
            out var sunDir, out float sunElev01, out bool sunUp,
            out var moonDir, out float moonElev01, out bool moonUp);
        // Moon-phase factor, from full moon 1 to new moon 0. It drives direct moonlight, moonlit sky scattering,
        // and fallback sky or environment dimming together so all moon-related contributions stay consistent.
        MoonPhase = Season.Rendering.DayNightCycle.MoonPhaseFactor(sunDir, moonDir);
        SunUp = sunUp;
        MoonUp = moonUp;

        // Feed the continuous sun and moon arcs into the atmospheric model in procedural mode.
        // Both bodies are written every frame instead of switching between them, which keeps transitions continuous
        // and preserves moonlit night color in the sky even while the sun is fading out.
        if (_skyViewTexture != null)
        {
            Season.Rendering.Atmosphere.SunDirection = Season.Rendering.DayNightCycle.BodyPosition(DayPhase, forMoon: false);
            Season.Rendering.Atmosphere.SunColor = SunLightColor;
            Season.Rendering.Atmosphere.MoonDirection = Season.Rendering.DayNightCycle.BodyPosition(DayPhase, forMoon: true);
            Season.Rendering.Atmosphere.MoonColor = MoonLightColor;
            // Moon top-of-atmosphere irradiance follows moon phase. The disk terminator is still handled in the shader,
            // but the moonlit sky and SH9 environment must dim along with it.
            Season.Rendering.Atmosphere.MoonIrradiance = _baseMoonIrradiance * MoonPhase;

            // Drive the nightly starfield around the celestial pole rather than world +Y so stars rise and set correctly.
            // Rotation speed stays synchronized with the sun arc.
            Season.Rendering.Atmosphere.StarPoleAxis = Season.Rendering.DayNightCycle.CelestialPole;
            Season.Rendering.Atmosphere.StarRotation = Season.Rendering.DayNightCycle.StarAngle(DayPhase);

            // Application-side weather driver: cycle through Clear -> Fair -> Overcast -> Storm -> Clear with linear interpolation.
            // SkyState.Lerp handles mismatched cloud-layer counts by fading missing layers in from zero coverage and density.
            float weatherPhase = App.Instance.Time / WeatherCycleSeconds;
            float weatherT = weatherPhase - MathF.Floor(weatherPhase);
            float segment = weatherT * 4f;
            int segIndex = Math.Min((int)segment, 3);
            float segT = segment - segIndex;
            var (weatherFrom, weatherTo) = segIndex switch
            {
                0 => (Season.Rendering.SkyState.Clear, Season.Rendering.SkyState.Fair),
                1 => (Season.Rendering.SkyState.Fair, Season.Rendering.SkyState.Overcast),
                2 => (Season.Rendering.SkyState.Overcast, Season.Rendering.SkyState.Storm),
                _ => (Season.Rendering.SkyState.Storm, Season.Rendering.SkyState.Clear),
            };
            Season.Rendering.Atmosphere.Clouds = Season.Rendering.SkyState.Lerp(weatherFrom, weatherTo, segT);

            // Advance cloud motion after writing the new cloud state. Compute dt explicitly from App.Time because AdvanceClouds
            // integrates frame-to-frame motion and expects a time delta rather than total elapsed time.
            float cloudDt = _lastCloudTime < 0f ? 0f : App.Instance.Time - _lastCloudTime;
            _lastCloudTime = App.Instance.Time;
            Season.Rendering.SkyLighting.AdvanceClouds(cloudDt);

            // Advance the CPU-side sky-lighting model only after Atmosphere, weather, and cloud motion are all current,
            // otherwise the environment light would lag one frame behind the sky.
            Season.Rendering.SkyLighting.Update();
            Season.Rendering.SkyLighting.AccumulateSh9();
            if (App.Instance.SceneEnvironment != null)
                Season.Rendering.SkyLighting.ApplyTo(App.Instance.SceneEnvironment);
        }

        // Bake the lighting state in place without per-frame allocations. The ambient fallback path still lives here,
        // and its color now blends continuously between sun and moon contributions instead of switching abruptly between them.
        float ambientScale = Season.Rendering.DayNightCycle.AmbientScale(DayPhase, NightSkyBrightness);
        float sunAmbientWeight = sunElev01;
        float moonAmbientWeight = moonElev01 * NightSkyBrightness * MoonPhase;
        float ambientWeightSum = sunAmbientWeight + moonAmbientWeight;
        var ambientColor = ambientWeightSum > 1e-4f
            ? (DayAmbientColor * sunAmbientWeight + NightAmbientColor * moonAmbientWeight) / ambientWeightSum
            : NightAmbientColor;
        App.Instance.Lighting.Ambient = new Vector4(ambientColor * ambientScale, 1f);

        App.Instance.Settings.RenderQuality.GiIntensity = _baseGiIntensity;   // Write settings every frame so DDGI sees the current value immediately.
        // In fallback mode, scale the environment lighting with day-night brightness. Procedural mode leaves these controls fixed
        // because SH9 already contains physically scaled day-night radiance.
        if (_skyViewTexture == null && App.Instance.SceneEnvironment != null)
        {
            App.Instance.SceneEnvironment.SkyIntensity = ambientScale;
            App.Instance.SceneEnvironment.DiffuseIntensity = BaseEnvDiffuseIntensity * ambientScale;
        }
        App.Instance.Lighting.Bake(ref App.Instance.SceneLights, App.Instance.CameraPos);

        // Update the persistent celestial lights in place. Procedural mode derives their final color and intensity from
        // atmospheric transmittance, while fallback mode keeps the older geometric elevation mapping for zero-regression behavior.
        if (sunLight != null)
        {
            sunLight.Direction = sunDir;
            if (_skyViewTexture != null)
                ApplyBodyTransmittance(sunLight, sunDir, SunLightColor, SunPeakIntensity,
                    Season.Rendering.Atmosphere.SunAngularRadiusDeg);
            else
                sunLight.Intensity = SunPeakIntensity * sunElev01;
            sunLight.IsOpen = sunUp;
        }
        if (moonLight != null)
        {
            moonLight.Direction = moonDir;
            // Moonlight peak intensity is multiplied by moon phase in both rendering paths, because the visible illuminated fraction
            // of the moon must also control the direct moonlight reaching the ground.
            if (_skyViewTexture != null)
                ApplyBodyTransmittance(moonLight, moonDir, MoonLightColor, MoonPeakIntensity * MoonPhase,
                    Season.Rendering.Atmosphere.MoonAngularRadiusDeg);
            else
                moonLight.Intensity = MoonPeakIntensity * moonElev01 * MoonPhase;
            moonLight.IsOpen = moonUp;
        }
    }

    /// <summary>
    /// Applies atmospheric transmittance to a celestial directional light in procedural mode. The transmittance is evaluated
    /// across the angular radius of the body, not at a single center sample, so sun and moon light fade smoothly at the horizon.
    ///
    /// The final incident spectrum is <paramref name="bodyColor"/> multiplied by the evaluated transmittance. Near the horizon
    /// Rayleigh scattering removes more blue and green, so the surviving light naturally becomes warmer and dimmer. The result is
    /// then normalized by its largest channel because <see cref="Season.Rendering.LightSource.Color"/> is an LDR linear color in
    /// the 0..1 range while intensity is stored separately. If the signal becomes tiny, the code keeps the original body color and
    /// sets intensity to zero to avoid NaNs and let the normal light culling path drop the light.
    ///
    /// This path intentionally does not multiply by the old elevation factor. Surface shading already applies N dot L, so the
    /// elevation-only term would double-apply the cosine and make sunset light unnaturally dark.
    /// </summary>
    /// <param name="light">Persistent light handle registered earlier and updated in place here.</param>
    /// <param name="lightDir">Light propagation direction produced by DayNightCycle, pointing toward the lit surface.</param>
    /// <param name="bodyColor">Top-of-atmosphere body spectrum, matching Atmosphere.SunColor or Atmosphere.MoonColor.</param>
    /// <param name="peakIntensity">Peak intensity in the ideal full-transmittance case.</param>
    /// <param name="angularRadiusDeg">Angular radius of the body in degrees, matching the shader's analytic disk size.</param>
    static void ApplyBodyTransmittance(Season.Rendering.LightSource light, Vector3 lightDir, Vector3 bodyColor,
                                       float peakIntensity, float angularRadiusDeg)
    {
        var t = Season.Rendering.SkyLighting.EvaluateDiskTransmittance(
            Season.Rendering.SkyLighting.ViewRadiusKm, -lightDir.Y, angularRadiusDeg * (MathF.PI / 180f));
        var c = bodyColor * t;
        float peak = MathF.Max(c.X, MathF.Max(c.Y, c.Z));
        if (peak > 1e-6f)
        {
            light.Color = c / peak;
            light.Intensity = peakIntensity * peak;
        }
        else
        {
            light.Color = bodyColor;
            light.Intensity = 0f;
        }
    }
}
