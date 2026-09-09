// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Platforms.Linux;

/// <summary>
/// Linux keyboard input, captured from the SDL3 event loop that already drives the
/// window, mouse, and touch events. Key events arrive on the same thread as the
/// render loop, so no cross-thread bridging is needed unlike the WinUI 3 backend.
///
/// Mapping uses SDL_Scancode (the physical key position) instead of SDL_Keycode
/// (the layout-dependent key value), mirroring the Windows backend's VirtualKey
/// path so WASD keeps its positional meaning on any keyboard layout.
///
/// KEY_DOWN/KEY_UP update a per-key down state and monotonic press/release counters;
/// auto-repeat KEY_DOWN events carry repeat=1 and are deduplicated by the down state,
/// and the engine-side Season.Storage.KeyboardService pumps these once per frame to
/// derive tap edges and hold durations, so short press vs long press semantics stay
/// identical on every platform.
/// </summary>
internal sealed class LinuxKeyboardService : Basic.IKeyboardService
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
    /// Handles one SDL_EVENT_KEY_DOWN or SDL_EVENT_KEY_UP event from the SDL3 event loop.
    /// </summary>
    public void OnKeyEvent(in SDL_KeyboardEvent keyEvent)
    {
        var key = ToKey(keyEvent.scancode);
        if (key == Basic.Key.None)
        {
            return;
        }

        int i = (int)key;

        if (keyEvent.down != 0)
        {
            // SDL marks auto-repeat KEY_DOWN with repeat=1; the down state deduplicates
            // so the press counter only advances on a physical press.
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

    // SDL3 SDL_Scancode values, from SDL3/SDL_scancode.h.
    static Basic.Key ToKey(short scancode)
    {
        if (scancode >= 4 && scancode <= 29)
        {
            return (Basic.Key)(Basic.Key.A + scancode - 4); // SDL_SCANCODE_A..Z
        }

        if (scancode >= 30 && scancode <= 38)
        {
            return (Basic.Key)(Basic.Key.D1 + scancode - 30); // SDL_SCANCODE_1..9
        }

        return scancode switch
        {
            39 => Basic.Key.D0,                               // SDL_SCANCODE_0
            40 => Basic.Key.Enter,                            // SDL_SCANCODE_RETURN
            41 => Basic.Key.Escape,                           // SDL_SCANCODE_ESCAPE
            42 => Basic.Key.Backspace,                        // SDL_SCANCODE_BACKSPACE
            43 => Basic.Key.Tab,                              // SDL_SCANCODE_TAB
            44 => Basic.Key.Space,                            // SDL_SCANCODE_SPACE
            >= 58 and <= 69 => (Basic.Key)(Basic.Key.F1 + scancode - 58), // SDL_SCANCODE_F1..F12
            79 => Basic.Key.Right,                            // SDL_SCANCODE_RIGHT
            80 => Basic.Key.Left,                             // SDL_SCANCODE_LEFT
            81 => Basic.Key.Down,                             // SDL_SCANCODE_DOWN
            82 => Basic.Key.Up,                               // SDL_SCANCODE_UP
            224 => Basic.Key.LeftCtrl,                        // SDL_SCANCODE_LCTRL
            225 => Basic.Key.LeftShift,                       // SDL_SCANCODE_LSHIFT
            226 => Basic.Key.LeftAlt,                         // SDL_SCANCODE_LALT
            228 => Basic.Key.RightCtrl,                       // SDL_SCANCODE_RCTRL
            229 => Basic.Key.RightShift,                      // SDL_SCANCODE_RSHIFT
            230 => Basic.Key.RightAlt,                        // SDL_SCANCODE_RALT
            _ => Basic.Key.None
        };
    }
}
