// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using UIKit;

namespace Season.Platforms.Shared.Apple;

/// <summary>
/// Hardware-keyboard input for iOS and Mac Catalyst, captured through the
/// MetalViewController's PressesBegan/PressesChanged/PressesEnded/PressesCancelled.
/// The view controller overrides CanBecomeFirstResponder and takes the responder
/// chain in ViewDidAppear, after which UIKit delivers hardware key presses
/// (UIKey values) to it on both platforms.
///
/// Mapping uses UIKey.KeyCode (UIKeyboardHidUsage), the USB HID usage id that
/// identifies physical key positions (KeyboardA(4)..KeyboardZ(29)), mirroring
/// the Windows VirtualKey, Linux SDL_Scancode, Android Keycode, and Web
/// event.code paths. Soft keyboards never reach this path, which is fine
/// because the game keys are hardware-keyboard only.
///
/// OnPress updates a per-key down state and monotonic press/release counters;
/// the key-repeat stream UIKit raises while a key is held (repeated PressesBegan
/// or PressesChanged) is deduplicated by the down state, and the engine-side
/// Season.Storage.KeyboardService pumps these once per frame to derive tap edges
/// and hold durations, so short press vs long press semantics stay identical on
/// every platform.
/// </summary>
public sealed class AppleKeyboardService : Basic.IKeyboardService
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
    /// Handles one UIPress transition from the view controller. Presses without a
    /// Key (touch, gamepad, remote presses) are ignored.
    /// </summary>
    public void OnPress(UIPress press, bool down)
    {
        var key = ToKey(press.Key?.KeyCode);
        if (key == Basic.Key.None)
        {
            return;
        }

        int i = (int)key;

        if (down)
        {
            // UIKit raises repeated PressesBegan/PressesChanged while a key is held;
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
    }

    /// <summary>
    /// Clears every held key. Called from PressesCancelled so keys released while
    /// input was interrupted cannot get stuck down; each cleared key bumps its
    /// release counter so frame-edge consumers see the release as well.
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

    // UIKeyboardHidUsage (USB HID usage) values. Letter, digit, and function keys
    // are contiguous, so they map arithmetically; the rest map through constants.
    static Basic.Key ToKey(UIKeyboardHidUsage? code)
    {
        if (code is null)
        {
            return Basic.Key.None;
        }

        var c = code.Value;
        int v = (int)c;

        if (c >= UIKeyboardHidUsage.KeyboardA && c <= UIKeyboardHidUsage.KeyboardZ)
        {
            return (Basic.Key)((int)Basic.Key.A + v - (int)UIKeyboardHidUsage.KeyboardA); // 4..29
        }

        if (c >= UIKeyboardHidUsage.Keyboard1 && c <= UIKeyboardHidUsage.Keyboard0)
        {
            return (Basic.Key)((int)Basic.Key.D0 + v - (int)UIKeyboardHidUsage.Keyboard1); // 30..39
        }

        if (c >= UIKeyboardHidUsage.KeyboardF1 && c <= UIKeyboardHidUsage.KeyboardF12)
        {
            return (Basic.Key)((int)Basic.Key.F1 + v - (int)UIKeyboardHidUsage.KeyboardF1); // 58..69
        }

        return c switch
        {
            UIKeyboardHidUsage.KeyboardSpacebar => Basic.Key.Space,           // 44
            UIKeyboardHidUsage.KeyboardReturnOrEnter => Basic.Key.Enter,      // 40
            UIKeyboardHidUsage.KeyboardEscape => Basic.Key.Escape,            // 41
            UIKeyboardHidUsage.KeyboardTab => Basic.Key.Tab,                  // 43
            UIKeyboardHidUsage.KeyboardDeleteOrBackspace => Basic.Key.Backspace, // 42
            UIKeyboardHidUsage.KeyboardRightArrow => Basic.Key.Right,         // 79
            UIKeyboardHidUsage.KeyboardLeftArrow => Basic.Key.Left,           // 80
            UIKeyboardHidUsage.KeyboardDownArrow => Basic.Key.Down,           // 81
            UIKeyboardHidUsage.KeyboardUpArrow => Basic.Key.Up,               // 82
            UIKeyboardHidUsage.KeyboardLeftShift => Basic.Key.LeftShift,      // 225
            UIKeyboardHidUsage.KeyboardRightShift => Basic.Key.RightShift,    // 229
            UIKeyboardHidUsage.KeyboardLeftControl => Basic.Key.LeftCtrl,     // 224
            UIKeyboardHidUsage.KeyboardRightControl => Basic.Key.RightCtrl,   // 228
            UIKeyboardHidUsage.KeyboardLeftAlt => Basic.Key.LeftAlt,          // 226
            UIKeyboardHidUsage.KeyboardRightAlt => Basic.Key.RightAlt,        // 230
            _ => Basic.Key.None
        };
    }
}
