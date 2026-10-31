// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// Authored fog setups the app can switch between. This is a preset name rather than a bag of numbers for the same
/// reason RenderQuality keeps tiers rather than raw factors: the numbers themselves belong to whoever builds the medium,
/// and only the choice of setup is a world parameter worth persisting. A preset that a given app does not implement is
/// simply never selected, so the enum stays a naming scheme rather than a capability declaration.
/// </summary>
public enum FogPreset
{
    /// <summary>Low morning mist hugging the ground.</summary>
    Morning,

    /// <summary>Low sun through a colonnade, for visible light shafts.</summary>
    SunShafts,

    /// <summary>Night haze around the street lamp and the room lights.</summary>
    NightLamps,
}

/// <summary>
/// World-simulation settings: the parameters that say how the world itself behaves over time, as opposed to how it
/// is rendered. Persisted in Settings.json next to <see cref="RenderQuality"/> and editable while the frame loop runs.
///
/// This is a type of its own rather than more fields on RenderQuality because the two answer different questions.
/// RenderQuality is a tier: most of it is read once and locked before graphics initialization, and a backend is
/// allowed to ignore a value it cannot support. Nothing here is a tier - these are simulation inputs that every
/// backend reads identically, that are meant to change at runtime, and that have no notion of being unsupported.
/// Filing a clock rate under a tier object would invite it to be baked at startup along with the values that
/// legitimately are, and would make the tier's "must not change after the frame loop starts" rule a lie.
///
/// The shape deliberately matches RenderQuality so the two are read and persisted the same way: static Default*
/// fields are the default sources an app may override in its constructor, instance properties are what round-trips
/// through Settings.json, and <see cref="Current"/> is the single runtime read entry point. New world parameters
/// belong here rather than in another new type.
/// </summary>
public class WorldSettings
{
    // -- Default-value sources (static Default* fields; apps may override them in the constructor, and BaseApp.Init() snapshots them into Settings.World). --

    /// <summary>Default value for DayNightSpeed (overrideable in the app constructor and captured by Init()).</summary>
    public static float DefaultDayNightSpeed = 0.01f;

    /// <summary>Default value for StartHour (overrideable in the app constructor and captured by Init()).</summary>
    public static float DefaultStartHour = DayNightCycle.SunriseHour;

    /// <summary>
    /// Day-night phase increment per second. Phase counts whole day cycles, so the 0.005 default is an about
    /// 100-second day, and 0 holds the sky still - which is the point of exposing it at all, since a frozen sky is
    /// what makes the lighting comparable between two captures.
    ///
    /// Freezing is not a special case: the phase is integrated from this rate rather than computed as elapsed time
    /// times it, so changing the rate only affects where the phase goes next and stopping leaves it where it was.
    ///
    /// Assigning this property is honoured but arrives as a step in the rate. The owner of the integration is
    /// expected to ease toward it instead; in this engine that owner is the app's CelestialLighting, whose
    /// SetDayNightSpeed is the intended entry point and also the one that persists the change.
    /// </summary>
    public float DayNightSpeed { get; set; } = DefaultDayNightSpeed;

    /// <summary>
    /// Clock hour the world starts the day at, in 0~24, where 0 and 24 are the same instant. The default is
    /// <see cref="DayNightCycle.SunriseHour"/>, which is the hour phase 0 already stood for, so leaving it alone reproduces
    /// the sunrise start the sample had before this existed.
    ///
    /// This is a time of day, not a phase: the phase convention counts whole day cycles and its integer part drives the
    /// lunar cycle, so an hour is the part of it a person can actually name. <see cref="DayNightCycle.PhaseFromHour"/> is the
    /// conversion, and it deliberately returns the fractional part alone.
    ///
    /// Unlike <see cref="DayNightSpeed"/> this one is not integrated from - it seeds the clock rather than driving it, so
    /// once the day is running the value here is history and no longer says what time it is. Changing it therefore has to
    /// move the clock explicitly, which is a jump rather than an ease: setting the time is a discontinuity by definition,
    /// where changing the rate is not. CelestialLighting.SetStartHour is the entry point that does both that and the save.
    /// </summary>
    public float StartHour { get; set; } = DefaultStartHour;

    /// <summary>Default value for FogPreset (overrideable in the app constructor and captured by Init()).</summary>
    public static FogPreset DefaultFogPreset = FogPreset.Morning;

    /// <summary>Default value for FogEnabled (overrideable in the app constructor and captured by Init()).</summary>
    public static bool DefaultFogEnabled = true;

    /// <summary>Default value for FogDensityScale (overrideable in the app constructor and captured by Init()).</summary>
    public static float DefaultFogDensityScale = 1f;

    /// <summary>Default value for FogWeatherLink (overrideable in the app constructor and captured by Init()).</summary>
    public static bool DefaultFogWeatherLink = true;

    /// <summary>
    /// Which authored fog setup the world is running. The preset also names a time of day and a camera in the sample
    /// app, but only the name is persisted: the numbers behind it live next to the code that builds the medium, so
    /// retuning one is not a settings migration, and a world saved under a preset keeps meaning the same setup.
    ///
    /// Changing it is a step rather than an ease. Unlike the day-night rate there is no continuous quantity underneath
    /// to integrate towards: the medium is replaced outright, so the volumetric history has to be dropped instead - see
    /// SceneFog.ResetHistory. The owner of that reset is the panel that writes this property.
    /// </summary>
    public FogPreset FogPreset { get; set; } = DefaultFogPreset;

    /// <summary>
    /// Whether the scene medium participates at all. Separate from <see cref="RenderQuality.VolumetricFog"/> on purpose:
    /// that one is a tier and decides whether the pass is built, which cannot change once the frame loop has started,
    /// while this one is a switch every backend reads identically each frame and that has no notion of being
    /// unsupported. Turning it off leaves the pass registered and costs nothing extra.
    /// </summary>
    public bool FogEnabled { get; set; } = DefaultFogEnabled;

    /// <summary>
    /// Multiplier on the preset's own extinction. This is the one fog number that is a world parameter rather than a
    /// rendering tier, because it is the quantity a person actually reasons about - "thicker than yesterday" - and it
    /// has the same meaning under every preset.
    /// </summary>
    public float FogDensityScale { get; set; } = DefaultFogDensityScale;

    /// <summary>
    /// Whether the medium follows the weather cycle. On, the extinction is scaled by how much cloud cover the current
    /// weather segment carries, so the same preset reads thin on a clear day and thick under an overcast; off, the
    /// preset runs at its authored density regardless of the sky.
    /// </summary>
    public bool FogWeatherLink { get; set; } = DefaultFogWeatherLink;

    /// <summary>空间天气配置。旧设置缺失此项时为空，由应用补齐场景默认区域。</summary>
    public WeatherRegionSettings[] WeatherRegions { get; set; } = [];

    /// <summary>区域模式不再让背景天空自动进入全局风暴；展示模式保留原循环。</summary>
    public WeatherMode WeatherMode { get; set; } = WeatherMode.Regional;

    static WorldSettings? _currentFallback;

    /// <summary>Runtime access entry point. Consumers should always read this property instead of the static Default* fields.</summary>
    public static WorldSettings Current
    {
        get
        {
            var world = DeviceServices.BaseApp?.Settings?.World;
            if (world != null)
                return world;
            return _currentFallback ??= new WorldSettings();
        }
    }
}
