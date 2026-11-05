// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// The world's ambient wind: the one place that turns the two authorable numbers - a bearing in degrees and a
/// strength on the 0~10 ladder - into the metre-per-second vector that a simulation consumer integrates.
///
/// <para>The two settings themselves live on <see cref="WorldSettings"/>, because they are persisted world
/// parameters. This class is deliberately not a settings object: it holds no state, only the mapping. That keeps the
/// ladder in code rather than in Settings.json, so retuning it is not a settings migration, and a file saved at 3级
/// keeps meaning 3级.</para>
///
/// <para><b>Direction</b> follows the convention already used for the sea
/// (<see cref="WaterPlane.WindDirection"/>): the wind blows along (cos θ, sin θ) in world XZ, so 0° pushes toward +X
/// and 90° toward +Z. It is the direction the air moves <i>toward</i>, not the meteorological bearing it comes
/// <i>from</i>: every consumer here is a velocity field, and "blowing toward +X at 2 m/s" is the number it needs,
/// while "from the north" would have to be inverted at every call site.</para>
///
/// <para><b>Strength</b> is a scene-scaled ladder rather than Beaufort's own brackets. Under Beaufort, level 5 is
/// already 9.4 m/s, which would fling the campfire plume thirty metres sideways inside its single 3.4 s lifetime and
/// read as a glitch rather than as wind. Level 1 is pinned to the 0.35 m/s breeze that plume was authored with, and
/// the rungs above it are spaced so that each one is a visible step: the campfire is currently the only consumer, so
/// the ladder was tuned against its plume - at level 3 the smoke drifts about as far sideways as it rises, and at
/// level 10 it is laid almost flat. A ladder spaced purely evenly in m/s would spend its top three rungs on lean
/// angles that are already past 75° and therefore look identical; these numbers avoid that.</para>
/// </summary>
public static class Wind
{
    /// <summary>Top of the strength ladder. Level 0 is dead calm and the only level that leaves particles untouched.</summary>
    public const int MaxLevel = 10;

    /// <summary>Air speed in m/s for each rung, indexed by level. See the class remarks for why these are not Beaufort's.</summary>
    static readonly float[] LevelSpeeds =
    {
        0f,     // 0  calm: the plume rises straight up
        0.35f,  // 1  the breeze the campfire was authored with (atan2(0.10, 0.32) ≈ 17°)
        0.75f,  // 2
        1.20f,  // 3
        1.70f,  // 4
        2.30f,  // 5
        3.00f,  // 6
        3.80f,  // 7
        4.70f,  // 8
        5.70f,  // 9
        7.00f,  // 10
    };

    /// <summary>Current level, 0~<see cref="MaxLevel"/>, read from the persisted world settings.</summary>
    public static int Level => WorldSettings.Current.WindLevel;

    /// <summary>Current bearing in degrees, 0~360, already wrapped by <see cref="WorldSettings.WindDirection"/>.</summary>
    public static float DirectionDegrees => WorldSettings.Current.WindDirection;

    /// <summary>Same bearing in radians, the form the XZ sine and cosine want.</summary>
    public static float DirectionRadians => DirectionDegrees * (MathF.PI / 180f);

    /// <summary>Current air speed in m/s, 0 when calm.</summary>
    public static float Speed => SpeedOfLevel(Level);

    /// <summary>
    /// Air velocity in world space, m/s, on the horizontal plane. Zero when calm, so a consumer can add it
    /// unconditionally instead of branching on the level.
    /// </summary>
    public static Vector3 Velocity
    {
        get
        {
            float speed = Speed;
            if (speed <= 0f)
                return Vector3.Zero;

            float angle = DirectionRadians;
            return new Vector3(MathF.Cos(angle) * speed, 0f, MathF.Sin(angle) * speed);
        }
    }

    /// <summary>Speed of one rung, clamped into the ladder so an out-of-range level cannot index past the table.</summary>
    public static float SpeedOfLevel(int level) => LevelSpeeds[Math.Clamp(level, 0, MaxLevel)];
}
