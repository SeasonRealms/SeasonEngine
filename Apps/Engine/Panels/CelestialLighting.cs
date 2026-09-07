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
    //
    // The authored rate itself lives in Settings.World so it survives a restart. This is a read-through rather than a
    // local copy on purpose: a copy would need a rule for which of the two wins once the settings file has been read,
    // and any such rule is a place for the panel and the sky to disagree about what the clock is doing.
    internal static float DayNightSpeed => Season.Rendering.WorldSettings.Current.DayNightSpeed;

    // Rate that is actually integrated, eased toward DayNightSpeed rather than snapped to it, plus the ramp state that
    // drives the easing. DayNightSpeed stays the authored value - what the clock is being asked to run at - so reading it
    // still means what it always did; this is the value that gets there over DayNightRampSeconds.
    //
    // None of it is persisted, and deliberately so: it describes where the rate is on its way from, which is a fact about
    // this session rather than about the world. Seeding is explicit (see EnsureClockSeeded) instead of done in these
    // initializers, because static initialization is not ordered against BaseApp.Init reading Settings.json - the
    // initializers could capture the default rate and then have the first frame ramp away from the saved one.
    static float _speedCurrent;
    static float _speedFrom;
    static float _speedRampT = 1f;
    // Speed to restore when flow is resumed, so a stop/start pair returns to the rate that was running rather than to a
    // hardcoded one.
    static float _speedResume;
    // Whether the persisted rate and start hour have been read into the state above and into _dayPhase. Distinct from
    // _clockReady below, which is about having a previous frame to difference against: this one is about having read the
    // world's opening state at all, and it is set once for the process rather than once per clock.
    static bool _clockSeeded;

    // Ease duration for a speed change. Long enough that the sun visibly accelerates and coasts to a halt instead of
    // stepping, short enough that a click still feels like it did something.
    const float DayNightRampSeconds = 1.5f;

    // Upper bound on the frame delta fed to the phase integration. A hitch, a breakpoint, or a minimised window would
    // otherwise hand over one enormous delta and teleport the sun - the exact discontinuity this integration exists to
    // avoid. The cost is that the day clock falls slightly behind wall time across a hitch, which is the right trade:
    // nothing here needs to agree with wall time, everything here needs to be continuous.
    const float MaxFrameSeconds = 0.25f;

    // Integrated day phase. Static so the panel, the sky, and the settings row all read one clock rather than each
    // holding a copy that could disagree about what time it is.
    static float _dayPhase;

    // Wall-clock time at the previous integration step, with an explicit ready flag rather than a sentinel: phase and
    // time both legitimately start at 0, so no value could stand for "not started".
    static float _lastClockTime;
    static bool _clockReady;

    // Phase rate that the weather and cloud clocks are expressed against. Dividing DayPhase by it recovers a
    // "seconds at the original speed" clock, so WeatherCycleSeconds below keeps its literal meaning at this rate
    // while still stretching, or stopping outright, together with DayNightSpeed.
    const float ReferenceDayNightSpeed = 0.01f;
    // Shortened synodic cycle for the sample so a full moon-phase loop completes in a few minutes rather than taking far too long to observe.
    const float MoonSynodicDays = 4f;
    // Weather cycle period, in seconds at ReferenceDayNightSpeed. It intentionally differs from the day-night period so weather does not always repeat at the same time of day.
    const float WeatherCycleSeconds = 120f;

    const float SunPeakIntensity = 4f;      // Peak sunlight intensity at high elevation.
    const float MoonPeakIntensity = 0.2f;   // Peak moonlight intensity before applying the moon-phase factor.
    static readonly Vector3 SunLightColor = new Vector3(1f, 0.96f, 0.9f);
    static readonly Vector3 MoonLightColor = new Vector3(0.55f, 0.68f, 1f);

    // Baseline top-of-atmosphere moon irradiance captured at construction time.
    // Each frame it is modulated by moon phase so direct moonlight, moonlit sky scattering, and SH9 environment lighting all dim together.
    readonly float _baseMoonIrradiance = Season.Rendering.Atmosphere.MoonIrradiance;

    // Previous value of the DayPhase-derived weather clock, used to compute this frame's dt for cloud motion.
    // A separate ready flag carries the "not initialized yet" state instead of a negative sentinel, because that
    // clock is pinned to 0 whenever DayNightSpeed is 0 and a value-based sentinel could not tell the two apart.
    float _lastCloudTime;
    bool _cloudClockReady;

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
    // Backed by the static integrated phase rather than being its own storage, so the value Sky reads and the value the
    // diagnostic sampler reports cannot drift apart.
    internal float DayPhase => _dayPhase;

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

    /// <summary>
    /// Seeds the clock from the persisted world settings the first time anything needs it: the easing state starts at the
    /// saved rate instead of ramping onto it, and the phase starts at the saved hour instead of at sunrise. Split out of the
    /// field initializers because those run at static initialization time, which is not ordered against BaseApp.Init reading
    /// Settings.json; called from Update and from every rate or time entry point, so whichever happens first is the one that
    /// pays for it.
    /// </summary>
    static void EnsureClockSeeded()
    {
        if (_clockSeeded)
            return;

        _clockSeeded = true;
        _speedCurrent = DayNightSpeed;
        _speedFrom = DayNightSpeed;
        // A saved rate of 0 is a sky that was deliberately left frozen. Resuming still has to go somewhere, so fall back
        // to the default rate rather than to 0, which would make the resume look like it did nothing at all.
        _speedResume = DayNightSpeed != 0f ? DayNightSpeed : Season.Rendering.WorldSettings.DefaultDayNightSpeed;
        // The phase is still 0 here, so this only chooses the time of day the first frame opens on. It is the same call the
        // panel makes later, which is what keeps "the hour the world starts at" and "the hour the setting moves it to" from
        // being two different mappings.
        JumpToHour(Season.Rendering.WorldSettings.Current.StartHour);
    }

    /// <summary>
    /// Moves the day phase to a clock hour, keeping the accumulated day count and replacing only the time of day.
    ///
    /// Zeroing the whole phase instead would rewind the lunar cycle to its phase-0 near-full moon every time the clock was
    /// set, so setting the hour would silently set the moon phase too - two unrelated things out of one input.
    ///
    /// This is the one place that moves the phase by anything other than the frame delta, and it does so as a step rather
    /// than an ease, unlike every rate change. That is not the discontinuity the integration exists to avoid: that one was
    /// an unasked-for jump produced by a rate change, whereas naming a time of day is a request to be at a different time,
    /// and there is no reading of it that leaves the phase where it was. The clocks derived from the phase absorb it on
    /// their own - the cloud delta is clamped to a second and resynchronises on the next frame, and the weather segment is
    /// a pure function of the phase - so nothing needs to be reset here.
    /// </summary>
    static void JumpToHour(float hour)
    {
        _dayPhase = MathF.Floor(_dayPhase) + Season.Rendering.DayNightCycle.PhaseFromHour(hour);
    }

    /// <summary>
    /// Stops the day-night flow if it is running, or resumes it at the rate it was last running at. Both directions ease
    /// rather than switch, and because the phase is integrated the transition is continuous no matter how long the sample
    /// has been up. Callers used to assign DayNightSpeed directly, which moved the phase by elapsed time times the change.
    ///
    /// The settings panel now sets a rate outright rather than toggling, so this is the programmatic stop/resume entry
    /// point: it is the only thing that remembers what rate to come back to.
    /// </summary>
    internal static void ToggleDayNightFlow()
    {
        EnsureClockSeeded();

        if (DayNightSpeed != 0f)
        {
            _speedResume = DayNightSpeed;
            SetDayNightSpeed(0f);
        }
        else
        {
            // Seeding guarantees a non-zero resume rate, so there is nothing left to guard against here.
            SetDayNightSpeed(_speedResume);
        }
    }

    /// <summary>
    /// Requests a new day-night rate, eased in from whatever the current rate happens to be. Ramping from the current
    /// rate rather than from the previous target is what keeps a change made mid-ramp smooth instead of snapping back.
    ///
    /// The rate is the authored value, so it is written to Settings and persisted from here - this is the one place that
    /// changes it, which is what lets callers set a rate without also having to know about saving. The save is the
    /// debounced request rather than a direct write, because a keyboard or a picker can produce several of these in a row.
    /// </summary>
    internal static void SetDayNightSpeed(float speed)
    {
        EnsureClockSeeded();

        // A non-finite rate is refused outright. MathF.Max passes NaN through, and NaN is unrecoverable once it reaches the
        // phase, because every later frame adds to it: one mistyped character would leave the sky permanently blank with
        // nothing in the log to say why.
        if (!float.IsFinite(speed))
            return;

        // A negative rate is refused rather than run backwards. Nothing downstream is written for it: the weather and
        // cloud clocks are derived from the phase and both ignore non-positive deltas, so the sun would reverse while the
        // weather and the wind offset sat still. Clamping here rather than at the panel keeps that true of every caller.
        speed = MathF.Max(0f, speed);

        if (speed == DayNightSpeed)
            return;

        _speedFrom = _speedCurrent;
        _speedRampT = 0f;
        Season.Rendering.WorldSettings.Current.DayNightSpeed = speed;
        DeviceServices.BaseApp?.RequestSaveSettings();
    }

    /// <summary>
    /// Sets the hour of day the world starts at, persists it, and moves the running clock to it, which is what makes the
    /// setting observable rather than something that only takes effect on the next launch.
    ///
    /// Moving the clock is deliberately a jump; see <see cref="JumpToHour"/> for why that does not contradict the
    /// integrated phase. The rate is left alone, so setting the hour on a frozen sky repositions the sun and leaves it
    /// frozen there, which is the useful behaviour for comparing two captures at a chosen time.
    /// </summary>
    /// <param name="hour">Clock hour in 0~24, where 0 and 24 are both midnight. Out-of-range values clamp to the nearest end.</param>
    internal static void SetStartHour(float hour)
    {
        EnsureClockSeeded();

        // Refused for the same reason a non-finite rate is, and more directly: this value reaches the phase without being
        // integrated first, so a NaN here breaks the sky on the very next frame.
        if (!float.IsFinite(hour))
            return;

        // Clamped rather than wrapped, so a typo lands at midnight rather than at some unrelated hour of the caller's
        // arithmetic. Both ends are the same instant, so the clamp has no discontinuity of its own.
        hour = Math.Clamp(hour, 0f, 24f);

        Season.Rendering.WorldSettings.Current.StartHour = hour;
        DeviceServices.BaseApp?.RequestSaveSettings();

        JumpToHour(hour);
    }

    public void Update(float time)
    {
        // Advance the day-night clock. Phase is integrated from the frame delta rather than recomputed as elapsed time
        // times the current rate, and that distinction is the whole reason a speed change is watchable. Under the old
        // form the phase was a function of the rate, so changing the rate moved the phase by the elapsed time times the
        // change - toggling flow off after five minutes at 0.005 snapped the phase by 1.5 whole days, teleporting the sun
        // and every clock derived from it. Integrating makes the rate affect only where the phase goes next, so the phase
        // is continuous across any rate change by construction, however long the sample has been running.
        float now = App.Instance.Time;
        float dt = _clockReady ? Math.Clamp(now - _lastClockTime, 0f, MaxFrameSeconds) : 0f;
        _lastClockTime = now;
        _clockReady = true;

        // Read the persisted rate and start hour into the clock state before anything below uses either of them. It has to
        // happen here rather than in a field initializer, and it has to happen before the integration below rather than
        // after it; see EnsureClockSeeded.
        EnsureClockSeeded();

        // Ease the rate itself on a smoothstep, which has zero slope at both ends: the sun accelerates from rest and
        // coasts to a stop instead of starting and stopping at full speed. A plain exponential approach would be simpler
        // but starts at maximum acceleration and never quite arrives, so a "stopped" sky would keep creeping.
        if (_speedRampT < 1f)
        {
            _speedRampT = DayNightRampSeconds > 0f ? MathF.Min(1f, _speedRampT + dt / DayNightRampSeconds) : 1f;
            float s = _speedRampT * _speedRampT * (3f - 2f * _speedRampT);
            _speedCurrent = _speedFrom + (DayNightSpeed - _speedFrom) * s;
        }
        else
        {
            _speedCurrent = DayNightSpeed;
        }

        // Evaluate the day-night cycle. Phase drives east-to-west motion on arcs tilted toward the south,
        // and since Step C the sun and moon move on independent full circles, allowing both bodies to appear together.
        _dayPhase += dt * _speedCurrent;

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
            // The clock is derived from DayPhase rather than from App.Time, so weather, cloud drift, and the sun all
            // stop or stretch together with DayNightSpeed. Driving it from App.Time instead made DayNightSpeed=0 leave
            // the weather cycling and the wind offset integrating, which is precisely the case a frozen sky is meant to
            // rule out. Dividing by ReferenceDayNightSpeed keeps WeatherCycleSeconds readable as seconds at that rate.
            float cycleTime = DayPhase / ReferenceDayNightSpeed;
            float weatherPhase = cycleTime / WeatherCycleSeconds;
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

            // Advance cloud motion after writing the new cloud state, on the same derived clock. AdvanceClouds
            // integrates frame-to-frame motion and expects a delta rather than total elapsed time, and it already
            // ignores non-positive deltas, so lowering DayNightSpeed at runtime simply pauses the drift. Raising it
            // would otherwise hand over one huge delta and teleport the offsets, hence the upper clamp; one second of
            // reference time is far more than any real frame and keeps the integration continuous.
            float cloudDt = _cloudClockReady ? MathF.Min(cycleTime - _lastCloudTime, 1f) : 0f;
            _lastCloudTime = cycleTime;
            _cloudClockReady = true;
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

        // Write settings every frame so DDGI sees the current value immediately. Derived from the tier rather than assigned
        // flat, because this line is the last writer each frame and would otherwise overwrite anything the settings panel put
        // there - which also means the tier is what the panel has to switch to turn the contribution off mid-run, since the
        // probe chain itself is only built at initialization.
        App.Instance.Settings.RenderQuality.GiIntensity =
            App.Instance.Settings.RenderQuality.GlobalIllumination == Season.Rendering.GiMode.Ddgi ? _baseGiIntensity : 0f;
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
