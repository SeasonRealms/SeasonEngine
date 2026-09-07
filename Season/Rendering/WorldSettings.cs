// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

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
