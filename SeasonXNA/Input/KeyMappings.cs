// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Season.Basic;

namespace Microsoft.Xna.Framework.Input;

/// <summary>
/// Bidirectional mapping between the XNA <see cref="Keys"/> identity used by ported
/// titles and the platform-neutral <see cref="Key"/> identity of the engine keyboard
/// service. Numpad digits collapse onto the top-row digits and the numpad +/- aliases
/// collapse onto the OEM +/- pair, mirroring the engine's own platform mappings.
/// </summary>
internal static class KeyMappings
{
    /// <summary>Every engine key, cached once for the per-frame Keyboard.GetState() scan.</summary>
    public static readonly Key[] EngineKeys = Enum.GetValues<Key>();

    public static Key ToEngine(Keys key)
    {
        int k = (int)key;

        if (k >= (int)Keys.A && k <= (int)Keys.Z)
        {
            return (Key)((int)Key.A + k - (int)Keys.A);
        }

        if (k >= (int)Keys.D0 && k <= (int)Keys.D9)
        {
            return (Key)((int)Key.D0 + k - (int)Keys.D0);
        }

        if (k >= (int)Keys.NumPad0 && k <= (int)Keys.NumPad9)
        {
            return (Key)((int)Key.D0 + k - (int)Keys.NumPad0); // numpad digits collapse onto the top row
        }

        if (k >= (int)Keys.F1 && k <= (int)Keys.F12)
        {
            return (Key)((int)Key.F1 + k - (int)Keys.F1);
        }

        return key switch
        {
            Keys.Back => Key.Backspace,
            Keys.Tab => Key.Tab,
            Keys.Enter => Key.Enter,
            Keys.Escape => Key.Escape,
            Keys.Space => Key.Space,
            Keys.Left => Key.Left,
            Keys.Right => Key.Right,
            Keys.Up => Key.Up,
            Keys.Down => Key.Down,
            Keys.LeftShift => Key.LeftShift,
            Keys.RightShift => Key.RightShift,
            Keys.LeftControl => Key.LeftCtrl,
            Keys.RightControl => Key.RightCtrl,
            Keys.LeftAlt => Key.LeftAlt,
            Keys.RightAlt => Key.RightAlt,
            Keys.OemPlus => Key.OemPlus,
            Keys.Add => Key.OemPlus,       // numpad + alias
            Keys.OemMinus => Key.OemMinus,
            Keys.Subtract => Key.OemMinus, // numpad - alias
            Keys.Delete => Key.Delete,
            _ => Key.None,
        };
    }

    /// <summary>Reverse mapping, producing the canonical XNA key for a pressed engine key.</summary>
    public static bool TryToKeys(Key key, out Keys result)
    {
        int k = (int)key;

        if (k >= (int)Key.A && k <= (int)Key.Z)
        {
            result = (Keys)((int)Keys.A + k - (int)Key.A);
            return true;
        }

        if (k >= (int)Key.D0 && k <= (int)Key.D9)
        {
            result = (Keys)((int)Keys.D0 + k - (int)Key.D0);
            return true;
        }

        if (k >= (int)Key.F1 && k <= (int)Key.F12)
        {
            result = (Keys)((int)Keys.F1 + k - (int)Key.F1);
            return true;
        }

        result = key switch
        {
            Key.Backspace => Keys.Back,
            Key.Tab => Keys.Tab,
            Key.Enter => Keys.Enter,
            Key.Escape => Keys.Escape,
            Key.Space => Keys.Space,
            Key.Left => Keys.Left,
            Key.Right => Keys.Right,
            Key.Up => Keys.Up,
            Key.Down => Keys.Down,
            Key.LeftShift => Keys.LeftShift,
            Key.RightShift => Keys.RightShift,
            Key.LeftCtrl => Keys.LeftControl,
            Key.RightCtrl => Keys.RightControl,
            Key.LeftAlt => Keys.LeftAlt,
            Key.RightAlt => Keys.RightAlt,
            Key.OemPlus => Keys.OemPlus,
            Key.OemMinus => Keys.OemMinus,
            Key.Delete => Keys.Delete,
            _ => Keys.None,
        };

        return result != Keys.None;
    }
}
