// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// Sun/moon cycle evaluator. This is a pure function evaluator: given a phase value, it outputs the sun/moon directions and intensity coefficients.
///
/// Phase convention: phase = accumulated day count. The fractional part is the progress within the current day, while the integer part advances the lunar phase,
/// so **do not** pre-wrap it with modulo.
///   .00 sunrise in the east, .25 noon passing overhead toward the south, .50 sunset in the west, and .50~1.0 moves the sun along the lower half below the horizon.
/// The sun and moon each run on **independent full-circle orbits and may both be in the sky at once**.
/// Step C removed the old "sun and moon are mutually exclusive" rule: the sun completes one full arc per day,
/// while the moon lags by 1/<see cref="SynodicDays"/> of a turn per day, so the angle between them advances day by day and naturally produces lunar phases.
/// This also naturally reproduces two real phenomena: the moon being visible during the day around first/last quarter, and moonless nights at new moon.
/// Whether a body is in the sky is now decided geometrically by pos.Y &gt; 0 instead of by a phase interval.
///
/// The arc rises in the east (+X = right side of the grass), passes overhead after tilting southward (-Z) around the north-south axis by SouthTilt, and sets in the west (-X = left side of the grass).
/// Azimuth definitions follow the 2026-08 Sample scene convention, where a top-down map uses north at the top, south at the bottom, west to the left, and east to the right:
/// north = +Z (in front of the grass / initial camera facing), south = -Z, east = +X, west = -X.
/// The tilt angle approximates a real northern-hemisphere solar path and was introduced in 2026-08 as an equinox simplification:
/// the sun passes overhead through due south at noon while sunrise and sunset still occur due east and west.
/// This lets sunlight enter through a doorway in the south wall and cast a lit rectangle onto the indoor floor, which is exactly how the Sample Room south wall is laid out.
/// The moon arc adds lunar inclination and ascending-node longitude so the two arcs are neither coplanar nor intersecting exactly at east and west,
/// because otherwise every full moon would land on the node line and every synodic cycle would degenerate into an eclipse cycle.
/// Formula:
/// a_sun = 2π·frac(phase), a_moon = 2π·frac(phase·(1−1/SynodicDays)) + π,
/// pos = (cos a, sin a·cosT, −sin a·sinT), where T is each body's tilt and pos is a unit vector.
/// The moon is then rotated around the Y axis by the ascending-node longitude.
/// The measured angle between the two orbital planes is 10.798° (see the .qoder\cphase verification bench), so at full moon the sun and moon directions are not exactly anti-parallel;
/// even at their closest they still differ by 2.87°.
/// elev01 = max(0, pos.Y) is the sine of the true elevation angle, so its noon peak is cosT rather than 1, and dir = -pos.
/// elev01 is therefore the elevation intensity coefficient: cosT at noon, 0 at the horizon, and 0 below the horizon.
/// The caller only needs to multiply it by the peak intensity. Together with the <see cref="LightSource.Intensity"/>&gt;0 bake filter, falling below the horizon simply means invisible.
///
/// Usage per frame:
/// <code>
/// DayNightCycle.Evaluate(phase, out var sunDir, out var sunElev, out var sunUp,
///                        out var moonDir, out var moonElev, out var moonUp);
/// sun.Direction = sunDir;  sun.Intensity = SunPeak * sunElev;  sun.IsOpen = sunUp;
/// // Moonlight is further multiplied by the lunar phase factor (0 at new moon, 1 at full moon):
/// moon.Intensity = MoonPeak * moonElev * DayNightCycle.MoonPhaseFactor(sunDir, moonDir);
/// </code>
/// </summary>
public static class DayNightCycle
{
    // South tilt of the northern-hemisphere solar path: a unit semicircle tilted southward (-Z) around the east-west axis (X) by this angle remains a unit vector.
    // 35° gives a noon elevation of 55°. Sunlight entering through a south-wall doorway then has dz/dy = tan35° ≈ 0.7,
    // so with a 5.2 m door height the light patch reaches about 3.6 m into the interior, which lands nicely inside the 2026-08 Sample Room layout.
    // A larger angle would move the noon light patch closer to the doorway threshold.
    const float SouthTilt = MathF.PI * 35f / 180f;
    static readonly float SinSouthTilt = MathF.Sin(SouthTilt);
    static readonly float CosSouthTilt = MathF.Cos(SouthTilt);

    /// <summary>
    /// Celestial-pole axis, as a unit vector pointing toward the north celestial pole.
    /// This is the normal of the great circle containing the solar arc and therefore the **axis of diurnal rotation** for the whole sky.
    /// It is determined solely by <c>SouthTilt</c> and has no independent parameter:
    /// the arc point is pos = cos a·(1,0,0) + sin a·(0,cosT,−sinT), and the cross product of the two basis vectors is (0, sinT, cosT),
    /// which tilts by T from zenith toward **north** (+Z), so its horizon elevation is exactly T=35°.
    /// This is just another way of stating "noon elevation 55°": celestial-pole elevation equals observer latitude,
    /// and equinox noon elevation equals 90° minus latitude, so the two always sum to 90°. This model therefore corresponds to latitude 35° north at equinox.
    ///
    /// Used for star-field diurnal motion through <see cref="StarAngle"/>. Rotating the sky sphere around this axis by StarAngle(phase) moves it from its phase=0 orientation to the current time,
    /// and that same rotation applied to (1,0,0) exactly equals <c>BodyPosition(phase, false)</c>.
    /// In other words, the star field and the sun share the same rigid-body motion and cannot drift apart. See Group9 in the .qoder\cphase verification bench.
    /// </summary>
    public static Vector3 CelestialPole { get; } = new Vector3(0f, SinSouthTilt, CosSouthTilt);

    // Lunar inclination, corresponding to the real 5.14° angle between the ecliptic and lunar orbit:
    // this is added on top of the solar tilt so the two arcs are not coplanar.
    // If they were coplanar, full moons would land exactly opposite the sun and new moons would exactly cover it,
    // forcing one lunar eclipse and one solar eclipse every synodic cycle, while also making the full-moon direction strictly anti-parallel to sunlight and degenerating SH9/CSM direction pairs.
    const float MoonInclination = MathF.PI * 5.14f / 180f;
    static readonly float SinMoonTilt = MathF.Sin(SouthTilt + MoonInclination);
    static readonly float CosMoonTilt = MathF.Cos(SouthTilt + MoonInclination);

    // Ascending-node longitude: rotate the lunar-orbit plane around the zenith axis (Y) by this azimuth.
    // Adding inclination alone is not enough: if both tilts are applied around X, the two great circles still always intersect at due east and west, (±1,0,0),
    // and this model's lunar-phase zero point happens to sit right there. That means "full moon" would always occur exactly on a node.
    // When SynodicDays is an integer such as the Sample's 4, every synodic cycle would then reproduce cosα = 1 exactly once.
    // Measurements show that without this term, phase 0/4/8... indeed gives cosα exactly equal to 1.000000.
    // Rotating the node line away removes that known degeneracy.
    // The tradeoff is that the node rotation itself contributes inclination, so the actual plane angle becomes larger than 5.14°, measuring 10.798° in practice.
    // This is harmless for the demo and even makes the height difference between the two arcs more readable, which is not unrealistic because the moon's declination swing is indeed larger than the sun's.
    // A side effect is that full moon no longer lands exactly opposite the sun, so the phase-factor peak drops to 0.983 instead of 1 in the SynodicDays=4 configuration.
    // Real full moons do not reach α=0 either, so this is physically more correct rather than an error.
    const float MoonNodeLongitude = MathF.PI * 12f / 180f;
    static readonly float SinMoonNode = MathF.Sin(MoonNodeLongitude);
    static readonly float CosMoonNode = MathF.Cos(MoonNodeLongitude);

    /// <summary>
    /// Synodic month length in day units, required to be &gt; 1.
    /// The moon lags the sun by 1/SynodicDays of a turn per day, so after SynodicDays days the lunar phase completes one full cycle.
    /// Moonrise is delayed by roughly the same amount per day.
    /// In measurements, the real-world value is 0.03505 versus the simple arc-rate value 0.03386, and the difference comes from lunar inclination shifting the moonrise azimuth.
    /// The physical value is 29.53059 and is used as the default.
    /// Demo scenes may reduce it so a full phase cycle completes within an observable duration, for example 4 in the Sample's 50-seconds-per-day setup, yielding one cycle every 200 seconds and a 90° phase advance per night.
    /// This is a **configuration parameter**, not frame-to-frame state. The class remains a pure-function evaluator, following the same static-parameter style as <see cref="Atmosphere"/>.
    /// </summary>
    public static float SynodicDays = 29.53059f;

    /// <summary>
    /// Evaluates the sun and moon state for a given phase.
    /// </summary>
    /// <param name="phase">Accumulated day count. Do not wrap it ahead of time; the integer part advances the lunar phase.</param>
    /// <param name="sunDir">Sun propagation direction, normalized and pointing toward the lit surface.</param>
    /// <param name="sunElev01">Sun elevation intensity coefficient in [0,1], equal to cosT at noon, 0 at the horizon, and 0 below it.</param>
    /// <param name="sunUp">Whether the sun is above the horizon. This is a geometric test and is equivalent to sunElev01 &gt; 0.</param>
    /// <param name="moonDir">Moon propagation direction, normalized and pointing toward the lit surface.</param>
    /// <param name="moonElev01">Moon elevation intensity coefficient in [0,1], **not** including lunar phase. See <see cref="MoonPhaseFactor"/>.</param>
    /// <param name="moonUp">Whether the moon is above the horizon. This is geometric and may be true at the same time as sunUp.</param>
    public static void Evaluate(
        float phase,
        out Vector3 sunDir, out float sunElev01, out bool sunUp,
        out Vector3 moonDir, out float moonElev01, out bool moonUp)
    {
        // Each arc runs its own full circle, including the lower half below the horizon, with no cross-coupling. Rising and setting are determined solely by the sign of pos.Y.
        EvaluateArc(SunPosition(phase), out sunDir, out sunElev01, out sunUp);
        EvaluateArc(MoonPosition(phase), out moonDir, out moonElev01, out moonUp);
    }

    /// <summary>
    /// Lunar phase factor in [0,1], representing the relative luminous flux reflected from the sunlit portion of the moon toward the ground.
    /// It is 1 at full moon and 0 at new moon.
    /// Multiplying it into moonlight <see cref="LightSource.Intensity"/> produces the expected "no moonlight on a new-moon night" behavior.
    ///
    /// Uses the Lambertian sphere phase function F(α) = (sin α + (π−α)·cos α)/π, where α is the phase angle between sun, moon, and earth.
    /// Under a geocentric approximation, cos α = −dot(sunDir, moonDir), and the negative signs of the two propagation directions cancel out.
    /// The real lunar surface is steeper than Lambert because of roughness and opposition surge. At half moon, observations are about 8% of full moon, while this formula gives 1/π≈32%.
    /// Even so, this code deliberately keeps the standard analytic model and avoids empirical fitting constants, because it only modulates intensity calibration and does not change disk appearance.
    /// The shader side derives the terminator directly from dot(moon surface normal, sun direction), which is related but not the same formula.
    /// </summary>
    /// <param name="sunDir">Sun propagation direction as a unit vector, typically taken from <see cref="Evaluate"/>.</param>
    /// <param name="moonDir">Moon propagation direction as a unit vector.</param>
    public static float MoonPhaseFactor(Vector3 sunDir, Vector3 moonDir)
    {
        float cosAlpha = Math.Clamp(-Vector3.Dot(sunDir, moonDir), -1f, 1f);
        float alpha = MathF.Acos(cosAlpha);
        return MathF.Max(0f, (MathF.Sin(alpha) + (MathF.PI - alpha) * cosAlpha) / MathF.PI);
    }

    /// <summary>
    /// World-space position of the celestial body on the sky sphere, as a unit arc point that already includes the corresponding tilt.
    /// It is also the unit vector pointing toward the body.
    /// It lies on the same arc used by <see cref="Evaluate"/> and is always equal to -dir.
    /// The caller can multiply it by an orbital radius and add a center offset.
    /// Positions below the horizon are returned correctly as well, with y &lt; 0, and visibility can be gated by sunUp/moonUp or directly by the sign of y.
    /// </summary>
    /// <param name="phase">Accumulated day count, same convention as <see cref="Evaluate"/>.</param>
    /// <param name="forMoon">True selects the moon arc, including lunar inclination and synodic slowdown. False selects the sun arc.</param>
    public static Vector3 BodyPosition(float phase, bool forMoon)
    {
        return forMoon ? MoonPosition(phase) : SunPosition(phase);
    }

    /// <summary>
    /// Skybox tinting. Brightness follows sun and moon elevation along the same arcs used by direct lighting, while color temperature emerges naturally from the weighted combination.
    /// tint = daySkyTint × sun elevation + nightSkyTint × moon elevation × nightBrightness, since moonlight is much dimmer than sunlight.
    /// The two terms are **added**, not chosen exclusively. Since Step C removed sun/moon exclusivity, the moon may also be visible during the day.
    /// Each term continuously fades in or out along its arc, so the handoff between sunset and moonrise stays C0 continuous with no popping.
    /// When both are below the horizon, the sky becomes pure black, matching the look of a moonless night.
    /// w stays fixed at 1 and does not touch the alpha pipeline. The result can be assigned directly to Mesh3D.ColorTint.
    /// Backends only synchronize it when the value changes, so steady-state cost is zero.
    /// This is only suitable for the StaticCube fallback mode. Procedural-sky mode must not set ColorTint again; see 2-5 contract clause 4.
    /// </summary>
    /// <param name="phase">Accumulated day count, same convention as <see cref="Evaluate"/>.</param>
    /// <param name="daySkyTint">Tint for the daytime sky, typically a warm white around (1, 0.98, 0.92). Since the texture is already a daytime sky, a noon multiplier of 1 preserves original brightness.</param>
    /// <param name="nightSkyTint">Tint for the night sky, typically a cool blue around (0.5, 0.6, 0.9).</param>
    /// <param name="nightBrightness">Brightness factor for the moonlit sky, in 0~1 relative to daytime noon. Default is 0.3.</param>
    public static Vector4 SkyTint(float phase, Vector3 daySkyTint, Vector3 nightSkyTint, float nightBrightness = 0.3f)
    {
        Evaluate(phase,
            out var sunDir, out float sunElev01, out _,
            out var moonDir, out float moonElev01, out _);

        Vector3 tint = daySkyTint * sunElev01
            + nightSkyTint * (moonElev01 * nightBrightness * MoonPhaseFactor(sunDir, moonDir));
        return new Vector4(tint, 1f);
    }

    /// <summary>
    /// Scale factor for indirect light and baseline brightness. It follows the same elevation arcs as direct lighting:
    /// sun elevation + moon elevation × lunar phase × nightBrightness, with the two terms added together just like <see cref="SkyTint"/>.
    /// Unlike the skybox, it includes a non-zero floor, because the skybox may go black while global indirect light should not;
    /// indoor and outdoor spaces still retain lamps and residual skylight.
    /// Typical usage is Settings.RenderQuality.GiIntensity = baseline × AmbientScale(...), writing into Settings from Step 6 onward as a multiplier consumed by DDGI or SH9.
    /// The intensity component of SceneLighting.Ambient can follow the same driver on degraded or fallback paths, while the RGB of Ambient carries the color temperature separately.
    /// </summary>
    /// <param name="phase">Accumulated day count, same convention as <see cref="Evaluate"/>.</param>
    /// <param name="nightBrightness">Moonlight factor in 0~1 relative to daytime noon. Default is 0.35 and shares the same basis as the skybox path.</param>
    /// <param name="floor">Lower bound, default 0.08, keeping indirect light from reaching zero when both sun and moon are below the horizon so objects under indoor lighting do not turn fully black.</param>
    public static float AmbientScale(float phase, float nightBrightness = 0.35f, float floor = 0.08f)
    {
        Evaluate(phase,
            out var sunDir, out float sunElev01, out _,
            out var moonDir, out float moonElev01, out _);

        float brightness = sunElev01
            + moonElev01 * nightBrightness * MoonPhaseFactor(sunDir, moonDir);
        return Math.Max(floor, brightness);
    }

    /// <summary>Angular position along the sun arc. One full turn per day: frac(phase)=0 rises in the east, .25 passes overhead, .5 sets in the west, and .5~1 runs below the horizon.</summary>
    static float SunAngle(float phase) => Wrap01(phase) * MathF.Tau;

    /// <summary>
    /// Diurnal star-field rotation angle in radians around <see cref="CelestialPole"/>.
    /// It rotates the celestial sphere from its phase=0 orientation to the current moment.
    /// It is **driven by the same source and rate** as the solar arc angle, so the star field and the sun rotate together as one rigid body.
    /// This makes stars rise from the eastern horizon, circle around the north celestial pole, and set in the west, while stars near the pole never set and become circumpolar.
    ///
    /// Known simplification: a true sidereal day is shorter than a solar day by 1/365.25 because of Earth's orbit,
    /// so across one year the star field rotates one extra full turn relative to the sun, producing seasonal constellations.
    /// This model has no calendar, only day and synodic scales in phase, so there is no basis for choosing the zero point of that extra annual rotation.
    /// It therefore intentionally uses the same rate. The tradeoff is that constellations remain fixed relative to the sun, but over demo durations of only a few days the difference is visually negligible.
    /// </summary>
    /// <param name="phase">Accumulated day count, same convention as <see cref="Evaluate"/>.</param>
    public static float StarAngle(float phase) => SunAngle(phase);

    /// <summary>
    /// Angular position along the moon arc. It uses the same base angular speed but lags by 1/<see cref="SynodicDays"/> of a turn per day, with an initial phase offset of π.
    /// At phase=0 this places a near-full moon on the western side, matching the old startup relation from the "sun and moon are mutually exclusive" convention, though node rotation means it is no longer exactly due west.
    /// The implementation multiplies by the rate first and only then applies Wrap01 instead of wrapping phase first.
    /// That lets each arc wrap in its own period, preserving lunar-phase accumulation across days while keeping the trig input bounded to [0,2π), so long-running phase accumulation does not destroy float precision.
    /// When SynodicDays ≤ 1, the rate degenerates and would freeze or reverse the moon. Clamping it to 1 reproduces the old behavior, effectively freezing the moon near full phase,
    /// with measured factor variations around 0.975~0.992 because the two arcs are not coplanar.
    /// </summary>
    static float MoonAngle(float phase)
    {
        float rate = SynodicDays > 1f ? 1f - 1f / SynodicDays : 1f;
        return Wrap01(phase * rate) * MathF.Tau + MathF.PI;
    }

    /// <summary>Evaluates one arc. dir = -pos, with pos always a unit vector, and rising/setting determined from the sign of pos.Y.</summary>
    static void EvaluateArc(Vector3 pos, out Vector3 dir, out float elev01, out bool isUp)
    {
        // dir points from the celestial body toward the scene, i.e. the propagation direction.
        dir = -pos;
        // The elevation intensity coefficient uses the true elevation sine, pos.Y = sin a·cosT, so the noon peak is cosT rather than 1.
        // The arc already covers the full circle, so below-horizon pos.Y < 0 is clamped to 0 and no external rise/set flag is needed.
        elev01 = MathF.Max(0f, pos.Y);
        isUp = pos.Y > 0f;
    }

    /// <summary>Unit position on the sun arc, including only the southward tilt.</summary>
    static Vector3 SunPosition(float phase) => ArcPosition(SunAngle(phase), SinSouthTilt, CosSouthTilt);

    /// <summary>
    /// Unit position on the moon arc.
    /// First tilt around the X axis by "south tilt + lunar inclination", then rotate around the zenith axis (Y) by the ascending-node longitude.
    /// That second step moves the node line away from exact east/west so full moon no longer has to occur on the node, as described by <see cref="MoonNodeLongitude"/>.
    /// </summary>
    static Vector3 MoonPosition(float phase)
    {
        var p = ArcPosition(MoonAngle(phase), SinMoonTilt, CosMoonTilt);

        // Rotate around the Y axis. Length stays unchanged, so the result remains a unit vector.
        return new Vector3(
            p.X * CosMoonNode + p.Z * SinMoonNode,
            p.Y,
            -p.X * SinMoonNode + p.Z * CosMoonNode);
    }

    /// <summary>Takes the point (cos a, sin a, 0) on the unit circle and tilts it southward (-Z) around the east-west axis (X) by the given angle, preserving unit length.</summary>
    static Vector3 ArcPosition(float angle, float sinTilt, float cosTilt)
    {
        float s = MathF.Sin(angle);
        return new Vector3(MathF.Cos(angle), s * cosTilt, -s * sinTilt);
    }

    /// <summary>Wraps any real number into [0,1), including negative inputs, so phase does not jump when Time goes negative or accumulated values overflow.</summary>
    static float Wrap01(float value)
    {
        value -= MathF.Floor(value);
        return value >= 1f ? 0f : value;   // Extremely small negative values can become 1f after floor; clamp them back to 0.
    }
}
