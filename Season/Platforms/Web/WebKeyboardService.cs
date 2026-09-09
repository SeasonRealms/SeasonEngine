// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace Season.Platforms.Web;

/// <summary>
/// Web keyboard input, pulled once per frame from seasonWebGPU.js.
///
/// Keyboard events land on the JS side (window-level keydown/keyup/blur listeners
/// inside seasonWebGPU.js), which maintains a per-code down state and monotonic
/// press/release counters using the browser's physical <c>event.code</c> (layout
/// independent, mirroring the Windows VirtualKey and Linux SDL_Scancode paths).
/// This service overrides <see cref="Basic.IKeyboardService.Update"/> to fetch the
/// full snapshot through one [JSImport] call per frame, then serves IsDown and the
/// counters from memory, so the engine-side pump sees a state machine identical to
/// the other platforms without per-key JS interop overhead.
///
/// The key list and its order must match the JS KEY_CODES list exactly; the snapshot
/// layout is [down(0/1)×N, pressed×N, released×N].
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed class WebKeyboardService : Basic.IKeyboardService
{
    // Fixed key list, order-matched with the JS KEY_CODES list in seasonWebGPU.js.
    static readonly Basic.Key[] MappedKeys =
    {
        Basic.Key.A, Basic.Key.B, Basic.Key.C, Basic.Key.D, Basic.Key.E, Basic.Key.F,
        Basic.Key.G, Basic.Key.H, Basic.Key.I, Basic.Key.J, Basic.Key.K, Basic.Key.L,
        Basic.Key.M, Basic.Key.N, Basic.Key.O, Basic.Key.P, Basic.Key.Q, Basic.Key.R,
        Basic.Key.S, Basic.Key.T, Basic.Key.U, Basic.Key.V, Basic.Key.W, Basic.Key.X,
        Basic.Key.Y, Basic.Key.Z,
        Basic.Key.D0, Basic.Key.D1, Basic.Key.D2, Basic.Key.D3, Basic.Key.D4,
        Basic.Key.D5, Basic.Key.D6, Basic.Key.D7, Basic.Key.D8, Basic.Key.D9,
        Basic.Key.Space, Basic.Key.Enter, Basic.Key.Escape, Basic.Key.Tab, Basic.Key.Backspace,
        Basic.Key.Left, Basic.Key.Right, Basic.Key.Up, Basic.Key.Down,
        Basic.Key.LeftShift, Basic.Key.RightShift,
        Basic.Key.LeftCtrl, Basic.Key.RightCtrl,
        Basic.Key.LeftAlt, Basic.Key.RightAlt,
        Basic.Key.F1, Basic.Key.F2, Basic.Key.F3, Basic.Key.F4, Basic.Key.F5, Basic.Key.F6,
        Basic.Key.F7, Basic.Key.F8, Basic.Key.F9, Basic.Key.F10, Basic.Key.F11, Basic.Key.F12,
    };

    // (int)Key -> index into the per-key arrays, -1 when the key is not mapped on Web.
    static readonly int[] IndexOfKey = BuildKeyIndex();

    readonly int _count = MappedKeys.Length;
    readonly bool[] _down = new bool[MappedKeys.Length];
    readonly uint[] _pressed = new uint[MappedKeys.Length];
    readonly uint[] _released = new uint[MappedKeys.Length];

    static int[] BuildKeyIndex()
    {
        var index = new int[Enum.GetValues<Basic.Key>().Length];
        Array.Fill(index, -1);

        for (int i = 0; i < MappedKeys.Length; i++)
        {
            index[(int)MappedKeys[i]] = i;
        }

        return index;
    }

    public bool IsDown(Basic.Key key)
    {
        int i = IndexOfKey[(int)key];
        return i >= 0 && _down[i];
    }

    public uint PressedCount(Basic.Key key)
    {
        int i = IndexOfKey[(int)key];
        return i >= 0 ? _pressed[i] : 0u;
    }

    public uint ReleasedCount(Basic.Key key)
    {
        int i = IndexOfKey[(int)key];
        return i >= 0 ? _released[i] : 0u;
    }

    /// <summary>
    /// Fetches the full keyboard snapshot from JS once per frame (called by the
    /// engine-side KeyboardService pump). JS owns the event listeners, the down
    /// state, and the monotonic counters; this only copies the snapshot.
    /// </summary>
    public void Update()
    {
        var snap = WebGPUInterop.PollKeyboard();
        int n = _count;

        for (int i = 0; i < n; i++)
        {
            _down[i] = snap[i] != 0;
            _pressed[i] = (uint)snap[n + i];
            _released[i] = (uint)snap[2 * n + i];
        }
    }
}
