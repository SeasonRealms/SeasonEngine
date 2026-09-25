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

    // XNA 4.0 / MonoGame full named-color bank (packed 0xAABBGGRR, same semantics as the uint constructor).
    public static Color AliceBlue => new(0xfffff8f0u);
    public static Color AntiqueWhite => new(0xffd7ebfau);
    public static Color Aqua => new(0xffffff00u);
    public static Color Aquamarine => new(0xffd4ff7fu);
    public static Color Azure => new(0xfffffff0u);
    public static Color Beige => new(0xffdcf5f5u);
    public static Color Bisque => new(0xffc4e4ffu);
    public static Color Black => new(0xff000000u);
    public static Color BlanchedAlmond => new(0xffcdebffu);
    public static Color Blue => new(0xffff0000u);
    public static Color BlueViolet => new(0xffe22b8au);
    public static Color Brown => new(0xff2a2aa5u);
    public static Color BurlyWood => new(0xff87b8deu);
    public static Color CadetBlue => new(0xffa09e5fu);
    public static Color Chartreuse => new(0xff00ff7fu);
    public static Color Chocolate => new(0xff1e69d2u);
    public static Color Coral => new(0xff507fffu);
    public static Color CornflowerBlue => new(0xffed9564u);
    public static Color Cornsilk => new(0xffdcf8ffu);
    public static Color Crimson => new(0xff3c14dcu);
    public static Color Cyan => new(0xffffff00u);
    public static Color DarkBlue => new(0xff8b0000u);
    public static Color DarkCyan => new(0xff8b8b00u);
    public static Color DarkGoldenrod => new(0xff0b86b8u);
    public static Color DarkGray => new(0xffa9a9a9u);
    public static Color DarkGreen => new(0xff006400u);
    public static Color DarkKhaki => new(0xff6bb7bdu);
    public static Color DarkMagenta => new(0xff8b008bu);
    public static Color DarkOliveGreen => new(0xff2f6b55u);
    public static Color DarkOrange => new(0xff008cffu);
    public static Color DarkOrchid => new(0xffcc3299u);
    public static Color DarkRed => new(0xff00008bu);
    public static Color DarkSalmon => new(0xff7a96e9u);
    public static Color DarkSeaGreen => new(0xff8bbc8fu);
    public static Color DarkSlateBlue => new(0xff8b3d48u);
    public static Color DarkSlateGray => new(0xff4f4f2fu);
    public static Color DarkTurquoise => new(0xffd1ce00u);
    public static Color DarkViolet => new(0xffd30094u);
    public static Color DeepPink => new(0xff9314ffu);
    public static Color DeepSkyBlue => new(0xffffbf00u);
    public static Color DimGray => new(0xff696969u);
    public static Color DodgerBlue => new(0xffff901eu);
    public static Color Firebrick => new(0xff2222b2u);
    public static Color FloralWhite => new(0xfff0faffu);
    public static Color ForestGreen => new(0xff228b22u);
    public static Color Fuchsia => new(0xffff00ffu);
    public static Color Gainsboro => new(0xffdcdcdcu);
    public static Color GhostWhite => new(0xfffff8f8u);
    public static Color Gold => new(0xff00d7ffu);
    public static Color Goldenrod => new(0xff20a5dau);
    public static Color Gray => new(0xff808080u);
    public static Color Green => new(0xff008000u);
    public static Color GreenYellow => new(0xff2fffadu);
    public static Color Honeydew => new(0xfff0fff0u);
    public static Color HotPink => new(0xffb469ffu);
    public static Color IndianRed => new(0xff5c5ccdu);
    public static Color Indigo => new(0xff82004bu);
    public static Color Ivory => new(0xfff0ffffu);
    public static Color Khaki => new(0xff8ce6f0u);
    public static Color Lavender => new(0xfffae6e6u);
    public static Color LavenderBlush => new(0xfff5f0ffu);
    public static Color LawnGreen => new(0xff00fc7cu);
    public static Color LemonChiffon => new(0xffcdfaffu);
    public static Color LightBlue => new(0xffe6d8adu);
    public static Color LightCoral => new(0xff8080f0u);
    public static Color LightCyan => new(0xffffffe0u);
    public static Color LightGoldenrodYellow => new(0xffd2fafau);
    public static Color LightGray => new(0xffd3d3d3u);
    public static Color LightGreen => new(0xff90ee90u);
    public static Color LightPink => new(0xffc1b6ffu);
    public static Color LightSalmon => new(0xff7aa0ffu);
    public static Color LightSeaGreen => new(0xffaab220u);
    public static Color LightSkyBlue => new(0xffface87u);
    public static Color LightSlateGray => new(0xff998877u);
    public static Color LightSteelBlue => new(0xffdec4b0u);
    public static Color LightYellow => new(0xffe0ffffu);
    public static Color Lime => new(0xff00ff00u);
    public static Color LimeGreen => new(0xff32cd32u);
    public static Color Linen => new(0xffe6f0fau);
    public static Color Magenta => new(0xffff00ffu);
    public static Color Maroon => new(0xff000080u);
    public static Color MediumAquamarine => new(0xffaacd66u);
    public static Color MediumBlue => new(0xffcd0000u);
    public static Color MediumOrchid => new(0xffd355bau);
    public static Color MediumPurple => new(0xffdb7093u);
    public static Color MediumSeaGreen => new(0xff71b33cu);
    public static Color MediumSlateBlue => new(0xffee687bu);
    public static Color MediumSpringGreen => new(0xff9afa00u);
    public static Color MediumTurquoise => new(0xffccd148u);
    public static Color MediumVioletRed => new(0xff8515c7u);
    public static Color MidnightBlue => new(0xff701919u);
    public static Color MintCream => new(0xfffafff5u);
    public static Color MistyRose => new(0xffe1e4ffu);
    public static Color Moccasin => new(0xffb5e4ffu);
    public static Color MonoGameOrange => new(0xff003ce7u);
    public static Color NavajoWhite => new(0xffaddeffu);
    public static Color Navy => new(0xff800000u);
    public static Color OldLace => new(0xffe6f5fdu);
    public static Color Olive => new(0xff008080u);
    public static Color OliveDrab => new(0xff238e6bu);
    public static Color Orange => new(0xff00a5ffu);
    public static Color OrangeRed => new(0xff0045ffu);
    public static Color Orchid => new(0xffd670dau);
    public static Color PaleGoldenrod => new(0xffaae8eeu);
    public static Color PaleGreen => new(0xff98fb98u);
    public static Color PaleTurquoise => new(0xffeeeeafu);
    public static Color PaleVioletRed => new(0xff9370dbu);
    public static Color PapayaWhip => new(0xffd5efffu);
    public static Color PeachPuff => new(0xffb9daffu);
    public static Color Peru => new(0xff3f85cdu);
    public static Color Pink => new(0xffcbc0ffu);
    public static Color Plum => new(0xffdda0ddu);
    public static Color PowderBlue => new(0xffe6e0b0u);
    public static Color Purple => new(0xff800080u);
    public static Color Red => new(0xff0000ffu);
    public static Color RosyBrown => new(0xff8f8fbcu);
    public static Color RoyalBlue => new(0xffe16941u);
    public static Color SaddleBrown => new(0xff13458bu);
    public static Color Salmon => new(0xff7280fau);
    public static Color SandyBrown => new(0xff60a4f4u);
    public static Color SeaGreen => new(0xff578b2eu);
    public static Color SeaShell => new(0xffeef5ffu);
    public static Color Sienna => new(0xff2d52a0u);
    public static Color Silver => new(0xffc0c0c0u);
    public static Color SkyBlue => new(0xffebce87u);
    public static Color SlateBlue => new(0xffcd5a6au);
    public static Color SlateGray => new(0xff908070u);
    public static Color Snow => new(0xfffafaffu);
    public static Color SpringGreen => new(0xff7fff00u);
    public static Color SteelBlue => new(0xffb48246u);
    public static Color Tan => new(0xff8cb4d2u);
    public static Color Teal => new(0xff808000u);
    public static Color Thistle => new(0xffd8bfd8u);
    public static Color Tomato => new(0xff4763ffu);
    public static Color Turquoise => new(0xffd0e040u);
    public static Color Violet => new(0xffee82eeu);
    public static Color Wheat => new(0xffb3def5u);
    public static Color White => new(0xffffffffu);
    public static Color WhiteSmoke => new(0xfff5f5f5u);
    public static Color Yellow => new(0xff00ffffu);
    public static Color YellowGreen => new(0xff32cd9au);

    // MonoGame extension colors used by ported assets (color-name parsing tables).
    public static Color TransparentBlack => new(0x00000000u);
    public static Color TransparentWhite => new(0x00ffffffu);
}
