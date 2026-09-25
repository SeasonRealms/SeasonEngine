// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Microsoft.Xna.Framework;

/// <summary>
/// Carries elapsed and total time to game logic, mirroring the XNA contract.
/// Hosts convert their frame delta into an instance before dispatching updates.
/// </summary>
public class GameTime
{
    public GameTime() { }

    public GameTime(TimeSpan totalGameTime, TimeSpan elapsedGameTime)
        : this(totalGameTime, elapsedGameTime, false) { }

    public GameTime(TimeSpan totalGameTime, TimeSpan elapsedGameTime, bool isRunningSlowly)
    {
        TotalGameTime = totalGameTime;
        ElapsedGameTime = elapsedGameTime;
        IsRunningSlowly = isRunningSlowly;
    }

    public TimeSpan TotalGameTime { get; set; }
    public TimeSpan ElapsedGameTime { get; set; }
    public bool IsRunningSlowly { get; set; }
}
