// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Microsoft.Xna.Framework;

/// <summary>Byte RGBA value. Construction does not imply premultiplied RGB.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Color : IEquatable<Color>
{
    private uint _packedValue;

    public byte R { readonly get => (byte)_packedValue; set => _packedValue = (_packedValue & 0xffffff00u) | value; }
    public byte G { readonly get => (byte)(_packedValue >> 8); set => _packedValue = (_packedValue & 0xffff00ffu) | ((uint)value << 8); }
    public byte B { readonly get => (byte)(_packedValue >> 16); set => _packedValue = (_packedValue & 0xff00ffffu) | ((uint)value << 16); }
    public byte A { readonly get => (byte)(_packedValue >> 24); set => _packedValue = (_packedValue & 0x00ffffffu) | ((uint)value << 24); }
    public uint PackedValue { readonly get => _packedValue; set => _packedValue = value; }

    public Color(uint packedValue) => _packedValue = packedValue;
    public Color(int r, int g, int b) : this(r, g, b, 255) { }
    public Color(int r, int g, int b, int a) =>
        _packedValue = (uint)(Clamp(r) | Clamp(g) << 8 | Clamp(b) << 16 | Clamp(a) << 24);
    public Color(float r, float g, float b) : this(r, g, b, 1f) { }
    public Color(float r, float g, float b, float a) : this(Channel(r), Channel(g), Channel(b), Channel(a)) { }

    private static int Clamp(int value) => Math.Clamp(value, 0, 255);
    private static int Channel(float value)
    {
        if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value), "Color components must be finite.");
        return (int)(Math.Clamp(value, 0f, 1f) * 255f);
    }

    public static Color FromNonPremultiplied(int r, int g, int b, int a) =>
        new(unchecked(r * a) / 255, unchecked(g * a) / 255, unchecked(b * a) / 255, a);

    public static Color Multiply(Color value, float scale)
    {
        if (!float.IsFinite(scale)) throw new ArgumentOutOfRangeException(nameof(scale), "Color scale must be finite.");
        return new((int)Math.Clamp(value.R * scale, 0f, 255f),
            (int)Math.Clamp(value.G * scale, 0f, 255f),
            (int)Math.Clamp(value.B * scale, 0f, 255f),
            (int)Math.Clamp(value.A * scale, 0f, 255f));
    }

    public static Color operator *(Color value, float scale) => Multiply(value, scale);
    public static Color operator *(float scale, Color value) => Multiply(value, scale);
    public static bool operator ==(Color a, Color b) => a._packedValue == b._packedValue;
    public static bool operator !=(Color a, Color b) => !(a == b);
    public readonly bool Equals(Color other) => this == other;
    public override readonly bool Equals(object? obj) => obj is Color other && Equals(other);
    public override readonly int GetHashCode() => _packedValue.GetHashCode();
    public override readonly string ToString() => $"{{R:{R} G:{G} B:{B} A:{A}}}";

    public static Color Transparent => default;
    public static Color Black => new(0, 0, 0);
    public static Color White => new(255, 255, 255);
    public static Color Red => new(255, 0, 0);
    public static Color Green => new(0, 128, 0);
    public static Color Blue => new(0, 0, 255);
    public static Color Cyan => new(0, 255, 255);
    public static Color Yellow => new(255, 255, 0);
    public static Color Gray => new(128, 128, 128);
    public static Color Silver => new(192, 192, 192);
    public static Color Orange => new(255, 165, 0);
    public static Color OrangeRed => new(255, 69, 0);
    public static Color DarkBlue => new(0, 0, 139);
    public static Color DarkOrange => new(255, 140, 0);
    public static Color DarkRed => new(139, 0, 0);
}
