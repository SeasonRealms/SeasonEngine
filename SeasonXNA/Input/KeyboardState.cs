// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Microsoft.Xna.Framework.Input;

/// <summary>
/// XNA keyboard snapshot: an immutable set of pressed keys captured at
/// <see cref="Keyboard.GetState"/> time. Ported titles compare consecutive snapshots
/// with == to detect whether any key changed between frames, so equality compares the
/// pressed-key sets, not the live services behind them.
/// </summary>
public struct KeyboardState : IEquatable<KeyboardState>
{
    // One bit per XNA Keys value (0..255), four 64-bit words.
    readonly ulong _word0;
    readonly ulong _word1;
    readonly ulong _word2;
    readonly ulong _word3;

    /// <summary>Builds a state in which exactly the given keys are down (XNA constructor shape).</summary>
    public KeyboardState(params Keys[] keys)
    {
        ulong word0 = 0, word1 = 0, word2 = 0, word3 = 0;

        if (keys != null)
        {
            foreach (var key in keys)
            {
                SetBit(ref word0, ref word1, ref word2, ref word3, key);
            }
        }

        _word0 = word0;
        _word1 = word1;
        _word2 = word2;
        _word3 = word3;
    }

    internal KeyboardState(ulong word0, ulong word1, ulong word2, ulong word3)
    {
        _word0 = word0;
        _word1 = word1;
        _word2 = word2;
        _word3 = word3;
    }

    /// <summary>Sets the bit of one key; None and out-of-range values are ignored.</summary>
    internal static void SetBit(ref ulong word0, ref ulong word1, ref ulong word2, ref ulong word3, Keys key)
    {
        int i = (int)key;

        if (i <= 0 || i > 255)
        {
            return;
        }

        ulong mask = 1UL << (i & 63);

        switch (i >> 6)
        {
            case 0: word0 |= mask; break;
            case 1: word1 |= mask; break;
            case 2: word2 |= mask; break;
            default: word3 |= mask; break;
        }
    }

    public readonly bool IsKeyDown(Keys key)
    {
        int i = (int)key;

        if (i <= 0 || i > 255)
        {
            return false;
        }

        ulong mask = 1UL << (i & 63);

        return (i >> 6) switch
        {
            0 => (_word0 & mask) != 0,
            1 => (_word1 & mask) != 0,
            2 => (_word2 & mask) != 0,
            _ => (_word3 & mask) != 0,
        };
    }

    public readonly bool IsKeyUp(Keys key) => !IsKeyDown(key);

    public readonly Keys[] GetPressedKeys()
    {
        var keys = new List<Keys>(8);

        Collect(_word0, 0, keys);
        Collect(_word1, 64, keys);
        Collect(_word2, 128, keys);
        Collect(_word3, 192, keys);

        return keys.ToArray();
    }

    static void Collect(ulong word, int baseBit, List<Keys> keys)
    {
        for (int bit = 0; bit < 64; bit++)
        {
            if ((word & (1UL << bit)) != 0)
            {
                keys.Add((Keys)(baseBit + bit));
            }
        }
    }

    public static bool operator ==(KeyboardState left, KeyboardState right) => left.Equals(right);

    public static bool operator !=(KeyboardState left, KeyboardState right) => !left.Equals(right);

    public readonly bool Equals(KeyboardState other) =>
        _word0 == other._word0 &&
        _word1 == other._word1 &&
        _word2 == other._word2 &&
        _word3 == other._word3;

    public override readonly bool Equals(object? obj) => obj is KeyboardState other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(_word0, _word1, _word2, _word3);

    public override readonly string ToString() => $"{{Keys: {string.Join(", ", GetPressedKeys())}}}";
}
