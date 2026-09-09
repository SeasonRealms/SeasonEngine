// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Storage;

/// <summary>
/// Engine-side keyboard input pump and the single query point for keyboard state.
/// <see cref="Basic.BaseApp.Update"/> pumps it once per frame right after TouchService,
/// diffing the platform facts from <see cref="Basic.DeviceServices.Keyboard"/> into
/// frame-level semantics: tap edges, release edges, hold durations, modifiers and combos.
///
/// A platform service only reports physical facts (IsDown and press/release counters),
/// so short press vs long press behavior stays identical on every platform, and a tap
/// that both presses and releases between two frames is still seen as an edge thanks
/// to the counters. When no platform service is registered, every query returns false.
/// </summary>
public static class KeyboardService
{
    // All keys except None, cached once for the per-frame pump.
    static readonly Basic.Key[] Keys = Enum.GetValues<Basic.Key>().Where(key => key != Basic.Key.None).ToArray();

    // Edge sets, recomputed at the start of every Update and valid until the next one.
    static readonly HashSet<Basic.Key> Pressed = new();
    static readonly HashSet<Basic.Key> Released = new();

    // Hold bookkeeping keyed by Key.
    static readonly Dictionary<Basic.Key, float> HoldTimes = new();
    static readonly Dictionary<Basic.Key, uint> LastPressedCounts = new();
    static readonly Dictionary<Basic.Key, uint> LastReleasedCounts = new();

    static bool _countsInitialized;

    /// <summary>True while the key is physically held down.</summary>
    public static bool IsDown(Basic.Key key) => DeviceServices.Keyboard?.IsDown(key) ?? false;

    /// <summary>True only on the frame the key went down, i.e. a short tap edge.</summary>
    public static bool IsPressed(Basic.Key key) => Pressed.Contains(key);

    /// <summary>True only on the frame the key went up.</summary>
    public static bool IsReleased(Basic.Key key) => Released.Contains(key);

    /// <summary>Seconds the key has been held; 0 while it is up.</summary>
    public static float HoldTime(Basic.Key key) => HoldTimes.TryGetValue(key, out var held) ? held : 0f;

    /// <summary>True while the key has been held at least the given number of seconds.</summary>
    public static bool HeldLong(Basic.Key key, float seconds) => HoldTime(key) >= seconds;

    public static bool Ctrl => IsDown(Basic.Key.LeftCtrl) || IsDown(Basic.Key.RightCtrl);

    public static bool Shift => IsDown(Basic.Key.LeftShift) || IsDown(Basic.Key.RightShift);

    public static bool Alt => IsDown(Basic.Key.LeftAlt) || IsDown(Basic.Key.RightAlt);

    /// <summary>
    /// Combination edge, e.g. PressedCombo(Key.C, Key.LeftCtrl) matches Ctrl+C on the
    /// frame C goes down while Ctrl is held. True only on that single frame.
    /// </summary>
    public static bool PressedCombo(Basic.Key key, params Basic.Key[] modifiers)
    {
        if (!IsPressed(key))
        {
            return false;
        }

        foreach (var modifier in modifiers)
        {
            if (!IsDown(modifier))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Recomputes the frame-level state. Called once per frame from BaseApp.Update
    /// before panels run, so panel Update code sees a consistent snapshot.
    /// </summary>
    internal static void Update(float time)
    {
        Pressed.Clear();
        Released.Clear();

        var keyboard = DeviceServices.Keyboard;

        if (keyboard == null)
        {
            HoldTimes.Clear();
            _countsInitialized = false;
            return;
        }

        // Pull-based services (Web) refresh their platform state here, once per frame.
        keyboard.Update();

        foreach (var key in Keys)
        {
            uint pressed = keyboard.PressedCount(key);
            uint released = keyboard.ReleasedCount(key);

            // Edge detection uses the platform counters, so events that happen entirely
            // between two frames are never lost. The first Update only records baselines.
            if (_countsInitialized)
            {
                if (pressed != LastPressedCounts[key])
                {
                    Pressed.Add(key);
                }

                if (released != LastReleasedCounts[key])
                {
                    Released.Add(key);
                }
            }

            LastPressedCounts[key] = pressed;
            LastReleasedCounts[key] = released;

            if (keyboard.IsDown(key))
            {
                HoldTimes[key] = HoldTime(key) + time;
            }
            else
            {
                HoldTimes.Remove(key);
            }
        }

        _countsInitialized = true;
    }
}
