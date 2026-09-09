// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Platforms.Windows;

/// <summary>
/// Windows keyboard input, captured from the window's ContentIsland keyboard source.
///
/// The WinUI 3 desktop window hosts its XAML content in a ContentIsland whose own child
/// HWND holds the keyboard focus, so WM_KEYDOWN is delivered to that child window and a
/// window-procedure subclass on the top-level HWND never sees keyboard messages.
/// InputKeyboardSource.GetForIsland attaches below the XAML input stack instead and
/// reports every key regardless of which window or element has focus.
///
/// KeyDown/KeyUp update a per-key down state and monotonic press/release counters;
/// auto-repeat KeyDown events are deduplicated by the down state, and the engine-side
/// Season.Storage.KeyboardService pumps these once per frame to derive tap edges and
/// hold durations, so short press vs long press semantics stay identical on every platform.
/// </summary>
internal sealed class WindowsKeyboardService : Basic.IKeyboardService
{
    static readonly int KeyCount = Enum.GetValues<Basic.Key>().Length;

    // Physical per-key state, indexed by (int)Key.
    readonly bool[] _down = new bool[KeyCount];
    readonly uint[] _pressed = new uint[KeyCount];
    readonly uint[] _released = new uint[KeyCount];

    Microsoft.UI.Input.InputKeyboardSource _source;

    public bool IsDown(Basic.Key key) => _down[(int)key];

    public uint PressedCount(Basic.Key key) => _pressed[(int)key];

    public uint ReleasedCount(Basic.Key key) => _released[(int)key];

    /// <summary>
    /// Attaches to the window's ContentIsland keyboard source. Called from the content's
    /// Loaded event, once the XamlRoot and its ContentIsland are guaranteed to exist.
    /// Idempotent: Loaded can fire again on content reload without double-subscribing.
    /// </summary>
    public void Attach(Microsoft.UI.Xaml.Window window)
    {
        if (_source != null)
        {
            return;
        }

        var island = window.Content?.XamlRoot?.ContentIsland;

        if (island == null)
        {
            Debug.WriteLine("[Keyboard] Attach skipped: no ContentIsland on the window content");
            return;
        }

        _source = Microsoft.UI.Input.InputKeyboardSource.GetForIsland(island);
        _source.KeyDown += OnKeyDown;
        _source.KeyUp += OnKeyUp;
    }

    /// <summary>
    /// Detaches from the island keyboard source. Called when the window closes.
    /// </summary>
    public void Detach()
    {
        if (_source == null)
        {
            return;
        }

        _source.KeyDown -= OnKeyDown;
        _source.KeyUp -= OnKeyUp;
        _source = null;
    }

    /// <summary>
    /// Clears every held key. Called when the window deactivates so keys released while
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

    void OnKeyDown(Microsoft.UI.Input.InputKeyboardSource sender, Microsoft.UI.Input.KeyEventArgs args)
    {
        var key = ToKey((int)args.VirtualKey);
        if (key == Basic.Key.None)
        {
            return;
        }

        int i = (int)key;

        // Auto-repeat raises KeyDown repeatedly while a key is held; the down state
        // deduplicates so the press counter only advances on a physical press.
        if (!_down[i])
        {
            _down[i] = true;
            _pressed[i]++;
        }
    }

    void OnKeyUp(Microsoft.UI.Input.InputKeyboardSource sender, Microsoft.UI.Input.KeyEventArgs args)
    {
        var key = ToKey((int)args.VirtualKey);
        if (key == Basic.Key.None)
        {
            return;
        }

        int i = (int)key;

        if (_down[i])
        {
            _down[i] = false;
            _released[i]++;
        }
    }

    static Basic.Key ToKey(int vk)
    {
        if (vk >= 'A' && vk <= 'Z')
        {
            return (Basic.Key)(Basic.Key.A + vk - 'A');
        }

        if (vk >= '0' && vk <= '9')
        {
            return (Basic.Key)(Basic.Key.D0 + vk - '0');
        }

        return vk switch
        {
            0x20 => Basic.Key.Space,                       // VK_SPACE
            0x0D => Basic.Key.Enter,                       // VK_RETURN
            0x1B => Basic.Key.Escape,                      // VK_ESCAPE
            0x09 => Basic.Key.Tab,                         // VK_TAB
            0x08 => Basic.Key.Backspace,                   // VK_BACK
            0x25 => Basic.Key.Left,                        // VK_LEFT
            0x27 => Basic.Key.Right,                       // VK_RIGHT
            0x26 => Basic.Key.Up,                          // VK_UP
            0x28 => Basic.Key.Down,                        // VK_DOWN
            0xA0 => Basic.Key.LeftShift,                   // VK_LSHIFT
            0xA1 => Basic.Key.RightShift,                  // VK_RSHIFT
            0x10 => Basic.Key.LeftShift,                   // VK_SHIFT
            0xA2 => Basic.Key.LeftCtrl,                    // VK_LCONTROL
            0xA3 => Basic.Key.RightCtrl,                   // VK_RCONTROL
            0x11 => Basic.Key.LeftCtrl,                    // VK_CONTROL
            0xA4 => Basic.Key.LeftAlt,                     // VK_LMENU
            0xA5 => Basic.Key.RightAlt,                    // VK_RMENU
            0x12 => Basic.Key.LeftAlt,                     // VK_MENU
            >= 0x70 and <= 0x7B => (Basic.Key)(Basic.Key.F1 + vk - 0x70), // VK_F1..VK_F12
            _ => Basic.Key.None
        };
    }
}
