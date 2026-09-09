// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Android.Views;

namespace Season.Platforms.Android;

/// <summary>
/// Android keyboard input, captured through the SurfaceViewVulkan view's OnKeyDown/OnKeyUp.
/// The surface view is marked Focusable, so once it holds window focus the system routes
/// hardware key events (OTG/Bluetooth keyboards, emulator key presses) to the view.
///
/// Mapping uses Android.Views.Keycode constants, which represent physical key positions
/// (A..Z are contiguous 29..54), mirroring the Windows VirtualKey, Linux SDL_Scancode,
/// and Web event.code paths.
///
/// OnKeyDown/OnKeyUp update a per-key down state and monotonic press/release counters;
/// the key-repeat stream Android sends while a key is held is deduplicated by the down
/// state, and the engine-side Season.Storage.KeyboardService pumps these once per frame
/// to derive tap edges and hold durations, so short press vs long press semantics stay
/// identical on every platform. Soft keyboards do not produce these keycodes, which is
/// fine because the game keys are hardware-keyboard only.
/// </summary>
internal sealed class AndroidKeyboardService : Basic.IKeyboardService
{
    static readonly int KeyCount = Enum.GetValues<Basic.Key>().Length;

    // Physical per-key state, indexed by (int)Key.
    readonly bool[] _down = new bool[KeyCount];
    readonly uint[] _pressed = new uint[KeyCount];
    readonly uint[] _released = new uint[KeyCount];

    public bool IsDown(Basic.Key key) => _down[(int)key];

    public uint PressedCount(Basic.Key key) => _pressed[(int)key];

    public uint ReleasedCount(Basic.Key key) => _released[(int)key];

    /// <summary>
    /// Handles one key transition from the surface view's OnKeyDown or OnKeyUp.
    /// Returns true when the keycode maps to a recognized game key (and is therefore
    /// consumed by the view), false when it should fall through to default handling.
    /// </summary>
    public bool OnKeyEvent(Keycode keyCode, bool down)
    {
        var key = ToKey(keyCode);
        if (key == Basic.Key.None)
        {
            return false;
        }

        int i = (int)key;

        if (down)
        {
            // Android raises OnKeyDown repeatedly (RepeatCount > 0) while a key is held;
            // the down state deduplicates so the press counter only advances on a
            // physical press.
            if (!_down[i])
            {
                _down[i] = true;
                _pressed[i]++;
            }
        }
        else if (_down[i])
        {
            _down[i] = false;
            _released[i]++;
        }

        return true;
    }

    /// <summary>
    /// Clears every held key. Called when the window loses focus so keys released while
    /// the window was inactive cannot get stuck down; each cleared key bumps its release
    /// counter so frame-edge consumers see the release as well.
    /// </summary>
    public void ResetKeys()
    {
        for (int i = 0; i < KeyCount; i++)
        {
            if (_down[i])
            {
                _down[i] = false;
                _released[i]++;
            }
        }
    }

    // Android.Views.Keycode values. Letter, digit, and function keys are contiguous,
    // so they map arithmetically; the rest map through the constants.
    static Basic.Key ToKey(Keycode keyCode)
    {
        int kc = (int)keyCode;

        if (kc >= (int)Keycode.A && kc <= (int)Keycode.Z)
        {
            return (Basic.Key)((int)Basic.Key.A + kc - (int)Keycode.A); // KEYCODE_A(29)..KEYCODE_Z(54)
        }

        if (kc >= (int)Keycode.Num0 && kc <= (int)Keycode.Num9)
        {
            return (Basic.Key)((int)Basic.Key.D0 + kc - (int)Keycode.Num0); // KEYCODE_0(7)..KEYCODE_9(16)
        }

        if (kc >= (int)Keycode.F1 && kc <= (int)Keycode.F12)
        {
            return (Basic.Key)((int)Basic.Key.F1 + kc - (int)Keycode.F1); // KEYCODE_F1(131)..KEYCODE_F12(142)
        }

        return keyCode switch
        {
            Keycode.Space => Basic.Key.Space,                     // KEYCODE_SPACE(62)
            Keycode.Enter => Basic.Key.Enter,                     // KEYCODE_ENTER(66)
            Keycode.Escape => Basic.Key.Escape,                   // KEYCODE_ESCAPE(111)
            Keycode.Tab => Basic.Key.Tab,                         // KEYCODE_TAB(61)
            Keycode.Del => Basic.Key.Backspace,                   // KEYCODE_DEL(67)
            Keycode.DpadLeft => Basic.Key.Left,                   // KEYCODE_DPAD_LEFT(21)
            Keycode.DpadRight => Basic.Key.Right,                 // KEYCODE_DPAD_RIGHT(22)
            Keycode.DpadUp => Basic.Key.Up,                       // KEYCODE_DPAD_UP(19)
            Keycode.DpadDown => Basic.Key.Down,                   // KEYCODE_DPAD_DOWN(20)
            Keycode.ShiftLeft => Basic.Key.LeftShift,             // KEYCODE_SHIFT_LEFT(59)
            Keycode.ShiftRight => Basic.Key.RightShift,           // KEYCODE_SHIFT_RIGHT(60)
            Keycode.CtrlLeft => Basic.Key.LeftCtrl,               // KEYCODE_CTRL_LEFT(113)
            Keycode.CtrlRight => Basic.Key.RightCtrl,             // KEYCODE_CTRL_RIGHT(114)
            Keycode.AltLeft => Basic.Key.LeftAlt,                 // KEYCODE_ALT_LEFT(57)
            Keycode.AltRight => Basic.Key.RightAlt,               // KEYCODE_ALT_RIGHT(58)
            _ => Basic.Key.None
        };
    }
}
