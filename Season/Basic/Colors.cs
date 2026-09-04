// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Basic;

[System.AttributeUsage(System.AttributeTargets.Field | System.AttributeTargets.Enum)]
public class PropertiesDesc : System.Attribute
{
    public string Desc { get; set; }

    public PropertiesDesc(string desc)
    {
        Desc = desc;
    }
}

//internal class ColorInfo
//{
//    internal Color Color { get; set; }
//    internal string Name { get; set; }
//}

//internal static readonly Dictionary<string, ColorInfo> Colors = new Dictionary<string, ColorInfo>();

public static class ColorsHelper
{
    public static string GetDescByProperties(this Color p)
    {
        Type type = p.GetType();
        var fields = type.GetFields();
        foreach (var field in fields)
        {
            if (field.Name.Equals(p.ToString()))
            {
                object[] objs = field.GetCustomAttributes(typeof(PropertiesDesc), true);
                if (objs != null && objs.Length > 0)
                {
                    return ((PropertiesDesc)objs[0]).Desc;
                }
                else
                {
                    return p.ToString() + " has no attached PropertiesDesc metadata.";
                }
            }
        }
        return "No Such field : " + p;
    }

    internal static string ToHexString(this Color c)
    {
        return string.Format("#{0}{1}{2}{3}",
            c.R.ToString("X2"),
            c.G.ToString("X2"),
            c.B.ToString("X2"),
            c.A.ToString("X2"));
    }
}

public static class Colors
{
    public static string[] AllNames = null;
    public static Color[] AllColors = null;

    public static void Init()
    {
        AllNames = new string[]
        {
            "AliceBlue", "AntiqueWhite", "Aqua", "Aquamarine", "Azure",
            "Beige", "Bisque", "Black","BlanchedAlmond", "Blue", "BlueViolet", "Brown", "BurlyWood",
            "CadetBlue", "Chartreuse", "Chocolate", "Coral", "CornflowerBlue", "Cornsilk", "Crimson", "Cyan",
            "DarkBlue", "DarkCyan", "DarkGoldenrod", "DarkGray", "DarkGreen", "DarkGrey", "DarkKhaki", "DarkMagenta", "DarkOliveGreen", "DarkOrange", "DarkOrchid", "DarkRed", "DarkSalmon", "DarkSeaGreen", "DarkSlateBlue", "DarkSlateGray", "DarkTurquoise", "DarkViolet", "DeepPink", "DeepSkyBlue", "DimGray", "DodgerBlue",
            "Firebrick", "FloralWhite", "ForestGreen", "Fuchsia",
            "Gainsboro", "GhostWhite", "Gold", "Goldenrod", "Gray", "Green", "GreenYellow",
            "Honeydew", "HotPink",
            "IndianRed", "Indigo", "Ivory",
            "Khaki",
            "Lavender", "LavenderBlush", "LawnGreen", "LemonChiffon", "LightBlue", "LightCoral", "LightCyan", "LightGoldenrodYellow", "LightGray", "LightGreen", "LightGrey", "LightPink", "LightSalmon", "LightSeaGreen", "LightSkyBlue", "LightSlateGray", "LightSteelBlue", "LightYellow", "Lime", "LimeGreen", "Linen",
            "Magenta", "Maroon", "MediumAquamarine", "MediumBlue", "MediumOrchid", "MediumPurple", "MediumSeaGreen", "MediumSlateBlue", "MediumSpringGreen", "MediumTurquoise", "MediumVioletRed", "MidnightBlue", "MintCream", "MistyRose", "Moccasin",
            "NavajoWhite", "Navy",
            "OldLace", "Olive", "OliveDrab", "Orange", "OrangeRed", "Orchid",
            "PaleGoldenrod", "PaleGreen", "PaleTurquoise", "PaleVioletRed", "PapayaWhip", "PeachPuff", "Peru", "Pink", "Plum", "PowderBlue", "Purple",
            "RebeccaPurple", "Red", "RosyBrown", "RoyalBlue",
            "SaddleBrown", "Salmon", "SandyBrown", "SeaGreen", "SeaShell", "Sienna", "Silver", "SkyBlue", "SlateBlue", "SlateGray", "Snow", "SpringGreen", "SteelBlue",
            "Tan", "Teal", "Thistle", "Tomato", "Transparent", "Turquoise",
            "Violet",
            "Wheat", "White", "WhiteSmoke",
            "Yellow", "YellowGreen",
            "GroundBlue", "MapBlue", "CellBlue", "LightWhite", "LightBlack"
        };

        AllColors = new Color[]
        {
            AliceBlue, AntiqueWhite, Aqua, Aquamarine, Azure,
            Beige, Bisque, Black, BlanchedAlmond, Blue, BlueViolet, Brown, BurlyWood,
            CadetBlue, Chartreuse, Chocolate, Coral, CornflowerBlue, Cornsilk, Crimson, Cyan,
            DarkBlue, DarkCyan, DarkGoldenrod, DarkGray, DarkGreen, DarkGrey, DarkKhaki, DarkMagenta, DarkOliveGreen, DarkOrange, DarkOrchid, DarkRed, DarkSalmon, DarkSeaGreen, DarkSlateBlue, DarkSlateGray, DarkTurquoise, DarkViolet, DeepPink, DeepSkyBlue, DimGray, DodgerBlue,
            Firebrick, FloralWhite, ForestGreen, Fuchsia,
            Gainsboro, GhostWhite, Gold, Goldenrod, Gray, Green, GreenYellow,
            Honeydew, HotPink,
            IndianRed, Indigo, Ivory,
            Khaki,
            Lavender, LavenderBlush, LawnGreen, LemonChiffon, LightBlue, LightCoral, LightCyan, LightGoldenrodYellow, LightGray, LightGreen, LightGrey, LightPink, LightSalmon, LightSeaGreen, LightSkyBlue, LightSlateGray, LightSteelBlue, LightYellow, Lime, LimeGreen, Linen,
            Magenta, Maroon, MediumAquamarine, MediumBlue, MediumOrchid, MediumPurple, MediumSeaGreen, MediumSlateBlue, MediumSpringGreen, MediumTurquoise, MediumVioletRed, MidnightBlue, MintCream, MistyRose, Moccasin,
            NavajoWhite, Navy,
            OldLace, Olive, OliveDrab, Orange, OrangeRed, Orchid,
            PaleGoldenrod, PaleGreen, PaleTurquoise, PaleVioletRed, PapayaWhip, PeachPuff, Peru, Pink, Plum, PowderBlue, Purple,
            RebeccaPurple, Red, RosyBrown, RoyalBlue,
            SaddleBrown, Salmon, SandyBrown, SeaGreen, SeaShell, Sienna, Silver, SkyBlue, SlateBlue, SlateGray, Snow, SpringGreen, SteelBlue,
            Tan, Teal, Thistle, Tomato, Transparent, Turquoise,
            Violet,
            Wheat, White, WhiteSmoke,
            Yellow, YellowGreen,
            GroundBlue, MapBlue, CellBlue, LightWhite, LightBlack
        };
    }

    public static Color? GetColor(string name)
    {
        if (AllNames is null)
        {
            Init();
        }

        var index = AllNames.IndexOf(name);

        if (index >= 0)
        {
            var color = AllColors[index];

            return color;
        }

        return null;
    }

    public static string GetColorName(this Color color)
    {
        if (AllColors is null)
        {
            Init();
        }

        var index = AllColors.IndexOf(color);
        if (index >= 0)
        {
            return AllNames[index];
        }
        return "";

        //foreach (var c in AllColors)
        //{
        //    if (c == color)
        //    {
        //        var index = AllColors.IndexOf(color);

        //        if (index >= 0)
        //        {
        //            return AllNames[AllColors[]]
        //        }

        //        return c.GetDescByProperties();
        //    }
        //}

        //return null;
    }

    public static Color? FromName(string name)
    {
        if (name.StartsWith("#"))
        {
            name = name.Substring(1);
            uint u;
            if (uint.TryParse(name, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out u))
            {
                // Parsed value contains color in RGBA form
                // Extract color components

                byte r = 0, g = 0, b = 0, a = 0;

                unchecked
                {
                    if (name.Length == 6)
                    {
                        r = (byte)(u >> 16);
                        g = (byte)(u >> 8);
                        b = (byte)u;
                        a = 255;
                    }
                    else if (name.Length == 8)
                    {
                        r = (byte)(u >> 24);
                        g = (byte)(u >> 16);
                        b = (byte)(u >> 8);
                        a = (byte)u;
                    }
                }

                return new Color(r, g, b, a);
            }
        }
        else
        {
            var index = AllNames.IndexOf(name);

            if (index >= 0)
            {
                return AllColors[index];
            }
            //return All.FirstOrDefault(co => co.GetDescByProperties() == name);
        }

        return null;
    }

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #F0F8FF.
    [PropertiesDesc("AliceBlue")]
    public static readonly Color AliceBlue = new Color((byte)240, (byte)248, byte.MaxValue, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #FAEBD7.
    [PropertiesDesc("AntiqueWhite")]
    public static readonly Color AntiqueWhite = new Color((byte)250, (byte)235, (byte)215, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #00FFFF.
    [PropertiesDesc("Aqua")]
    public static readonly Color Aqua = new Color((byte)0, byte.MaxValue, byte.MaxValue, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #7FFFD4.
    [PropertiesDesc("Aquamarine")]
    public static readonly Color Aquamarine = new Color((byte)127, byte.MaxValue, (byte)212, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #F0FFFF.
    public static readonly Color Azure = new Color((byte)240, byte.MaxValue, byte.MaxValue, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #F5F5DC.
    public static readonly Color Beige = new Color((byte)245, (byte)245, (byte)220, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #FFE4C4.
    public static readonly Color Bisque = new Color(byte.MaxValue, (byte)228, (byte)196, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #000000.
    public static readonly Color Black = new Color((byte)0, (byte)0, (byte)0, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #FFEBCD.
    public static readonly Color BlanchedAlmond = new Color(byte.MaxValue, (byte)235, (byte)205, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #0000FF.
    public static readonly Color Blue = new Color((byte)0, (byte)0, byte.MaxValue, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #8A2BE2.
    public static readonly Color BlueViolet = new Color((byte)138, (byte)43, (byte)226, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #A52A2A.
    public static readonly Color Brown = new Color((byte)165, (byte)42, (byte)42, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #DEB887.
    public static readonly Color BurlyWood = new Color((byte)222, (byte)184, (byte)135, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #5F9EA0.
    public static readonly Color CadetBlue = new Color((byte)95, (byte)158, (byte)160, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #7FFF00.
    public static readonly Color Chartreuse = new Color((byte)127, byte.MaxValue, (byte)0, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #D2691E.
    public static readonly Color Chocolate = new Color((byte)210, (byte)105, (byte)30, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #FF7F50.
    public static readonly Color Coral = new Color(byte.MaxValue, (byte)127, (byte)80, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #6495ED.
    public static readonly Color CornflowerBlue = new Color((byte)100, (byte)149, (byte)237, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #FFF8DC.
    public static readonly Color Cornsilk = new Color(byte.MaxValue, (byte)248, (byte)220, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #DC143C.
    public static readonly Color Crimson = new Color((byte)220, (byte)20, (byte)60, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #00FFFF.
    public static readonly Color Cyan = Aqua;

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #00008B.
    public static readonly Color DarkBlue = new Color((byte)0, (byte)0, (byte)139, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #008B8B.
    public static readonly Color DarkCyan = new Color((byte)0, (byte)139, (byte)139, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #B8860B.
    public static readonly Color DarkGoldenrod = new Color((byte)184, (byte)134, (byte)11, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #A9A9A9.
    public static readonly Color DarkGray = new Color((byte)169, (byte)169, (byte)169, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #006400.
    public static readonly Color DarkGreen = new Color((byte)0, (byte)100, (byte)0, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #A9A9A9.
    public static readonly Color DarkGrey = DarkGray;

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #BDB76B.
    public static readonly Color DarkKhaki = new Color((byte)189, (byte)183, (byte)107, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #8B008B.
    public static readonly Color DarkMagenta = new Color((byte)139, (byte)0, (byte)139, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #556B2F.
    public static readonly Color DarkOliveGreen = new Color((byte)85, (byte)107, (byte)47, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #FF8C00.
    public static readonly Color DarkOrange = new Color(byte.MaxValue, (byte)140, (byte)0, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #9932CC.
    public static readonly Color DarkOrchid = new Color((byte)153, (byte)50, (byte)204, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #8B0000.
    public static readonly Color DarkRed = new Color((byte)139, (byte)0, (byte)0, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #E9967A.
    public static readonly Color DarkSalmon = new Color((byte)233, (byte)150, (byte)122, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #8FBC8F.
    public static readonly Color DarkSeaGreen = new Color((byte)143, (byte)188, (byte)143, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #483D8B.
    public static readonly Color DarkSlateBlue = new Color((byte)72, (byte)61, (byte)139, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #2F4F4F.
    public static readonly Color DarkSlateGray = new Color((byte)47, (byte)79, (byte)79, byte.MaxValue);

    //
    // Summary:
    ////     Represents a Color matching the W3C definition that has an hex value of #2F4F4F.
    //public static readonly Color DarkSlateGrey = DarkSlateGray;

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #00CED1.
    public static readonly Color DarkTurquoise = new Color((byte)0, (byte)206, (byte)209, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #9400D3.
    public static readonly Color DarkViolet = new Color((byte)148, (byte)0, (byte)211, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #FF1493.
    public static readonly Color DeepPink = new Color(byte.MaxValue, (byte)20, (byte)147, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #00BFFF.
    public static readonly Color DeepSkyBlue = new Color((byte)0, (byte)191, byte.MaxValue, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #696969.
    public static readonly Color DimGray = new Color((byte)105, (byte)105, (byte)105, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #696969.
    //public static readonly Color DimGrey = DimGray;

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #1E90FF.
    public static readonly Color DodgerBlue = new Color((byte)30, (byte)144, byte.MaxValue, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #B22222.
    public static readonly Color Firebrick = new Color((byte)178, (byte)34, (byte)34, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #FFFAF0.
    public static readonly Color FloralWhite = new Color(byte.MaxValue, (byte)250, (byte)240, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #228B22.
    public static readonly Color ForestGreen = new Color((byte)34, (byte)139, (byte)34, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #FF00FF.
    public static readonly Color Fuchsia = new Color(byte.MaxValue, (byte)0, byte.MaxValue, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #DCDCDC.
    public static readonly Color Gainsboro = new Color((byte)220, (byte)220, (byte)220, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #F8F8FF.
    public static readonly Color GhostWhite = new Color((byte)248, (byte)248, byte.MaxValue, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #FFD700.
    public static readonly Color Gold = new Color(byte.MaxValue, (byte)215, (byte)0, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #DAA520.
    public static readonly Color Goldenrod = new Color((byte)218, (byte)165, (byte)32, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #808080.
    public static readonly Color Gray = new Color((byte)128, (byte)128, (byte)128, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #008000.
    public static readonly Color Green = new Color((byte)0, (byte)128, (byte)0, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #ADFF2F.
    public static readonly Color GreenYellow = new Color((byte)173, byte.MaxValue, (byte)47, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #808080.
    //public static readonly Color Grey = Gray;

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #F0FFF0.
    public static readonly Color Honeydew = new Color((byte)240, byte.MaxValue, (byte)240, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #FF69B4.
    public static readonly Color HotPink = new Color(byte.MaxValue, (byte)105, (byte)180, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #CD5C5C.
    public static readonly Color IndianRed = new Color((byte)205, (byte)92, (byte)92, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #4B0082.
    public static readonly Color Indigo = new Color((byte)75, (byte)0, (byte)130, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #FFFFF0.
    public static readonly Color Ivory = new Color(byte.MaxValue, byte.MaxValue, (byte)240, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #F0E68C.
    public static readonly Color Khaki = new Color((byte)240, (byte)230, (byte)140, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #E6E6FA.
    public static readonly Color Lavender = new Color((byte)230, (byte)230, (byte)250, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #FFF0F5.
    public static readonly Color LavenderBlush = new Color(byte.MaxValue, (byte)240, (byte)245, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #7CFC00.
    public static readonly Color LawnGreen = new Color((byte)124, (byte)252, (byte)0, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #FFFACD.
    public static readonly Color LemonChiffon = new Color(byte.MaxValue, (byte)250, (byte)205, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #ADD8E6.
    public static readonly Color LightBlue = new Color((byte)173, (byte)216, (byte)230, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #F08080.
    public static readonly Color LightCoral = new Color((byte)240, (byte)128, (byte)128, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #E0FFFF.
    public static readonly Color LightCyan = new Color((byte)224, byte.MaxValue, byte.MaxValue, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #FAFAD2.
    public static readonly Color LightGoldenrodYellow = new Color((byte)250, (byte)250, (byte)210, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #D3D3D3.
    public static readonly Color LightGray = new Color((byte)211, (byte)211, (byte)211, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #90EE90.
    public static readonly Color LightGreen = new Color((byte)144, (byte)238, (byte)144, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #D3D3D3.
    public static readonly Color LightGrey = LightGray;

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #FFB6C1.
    public static readonly Color LightPink = new Color(byte.MaxValue, (byte)182, (byte)193, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #FFA07A.
    public static readonly Color LightSalmon = new Color(byte.MaxValue, (byte)160, (byte)122, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #20B2AA.
    public static readonly Color LightSeaGreen = new Color((byte)32, (byte)178, (byte)170, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #87CEFA.
    public static readonly Color LightSkyBlue = new Color((byte)135, (byte)206, (byte)250, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #778899.
    public static readonly Color LightSlateGray = new Color((byte)119, (byte)136, (byte)153, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #778899.
    //public static readonly Color LightSlateGrey = LightSlateGray;

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #B0C4DE.
    public static readonly Color LightSteelBlue = new Color((byte)176, (byte)196, (byte)222, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #FFFFE0.
    public static readonly Color LightYellow = new Color(byte.MaxValue, byte.MaxValue, (byte)224, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #00FF00.
    public static readonly Color Lime = new Color((byte)0, byte.MaxValue, (byte)0, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #32CD32.
    public static readonly Color LimeGreen = new Color((byte)50, (byte)205, (byte)50, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #FAF0E6.
    public static readonly Color Linen = new Color((byte)250, (byte)240, (byte)230, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #FF00FF.
    public static readonly Color Magenta = Fuchsia;

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #800000.
    public static readonly Color Maroon = new Color((byte)128, (byte)0, (byte)0, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #66CDAA.
    public static readonly Color MediumAquamarine = new Color((byte)102, (byte)205, (byte)170, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #0000CD.
    public static readonly Color MediumBlue = new Color((byte)0, (byte)0, (byte)205, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #BA55D3.
    public static readonly Color MediumOrchid = new Color((byte)186, (byte)85, (byte)211, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #9370DB.
    public static readonly Color MediumPurple = new Color((byte)147, (byte)112, (byte)219, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #3CB371.
    public static readonly Color MediumSeaGreen = new Color((byte)60, (byte)179, (byte)113, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #7B68EE.
    public static readonly Color MediumSlateBlue = new Color((byte)123, (byte)104, (byte)238, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #00FA9A.
    public static readonly Color MediumSpringGreen = new Color((byte)0, (byte)250, (byte)154, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #48D1CC.
    public static readonly Color MediumTurquoise = new Color((byte)72, (byte)209, (byte)204, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #C71585.
    public static readonly Color MediumVioletRed = new Color((byte)199, (byte)21, (byte)133, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #191970.
    public static readonly Color MidnightBlue = new Color((byte)25, (byte)25, (byte)112, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #F5FFFA.
    public static readonly Color MintCream = new Color((byte)245, byte.MaxValue, (byte)250, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #FFE4E1.
    public static readonly Color MistyRose = new Color(byte.MaxValue, (byte)228, (byte)225, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #FFE4B5.
    public static readonly Color Moccasin = new Color(byte.MaxValue, (byte)228, (byte)181, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #FFDEAD.
    public static readonly Color NavajoWhite = new Color(byte.MaxValue, (byte)222, (byte)173, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #000080.
    public static readonly Color Navy = new Color((byte)0, (byte)0, (byte)128, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #FDF5E6.
    public static readonly Color OldLace = new Color((byte)253, (byte)245, (byte)230, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #808000.
    public static readonly Color Olive = new Color((byte)128, (byte)128, (byte)0, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #6B8E23.
    public static readonly Color OliveDrab = new Color((byte)107, (byte)142, (byte)35, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #FFA500.
    public static readonly Color Orange = new Color(byte.MaxValue, (byte)165, (byte)0, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #FF4500.
    public static readonly Color OrangeRed = new Color(byte.MaxValue, (byte)69, (byte)0, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #DA70D6.
    public static readonly Color Orchid = new Color((byte)218, (byte)112, (byte)214, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #EEE8AA.
    public static readonly Color PaleGoldenrod = new Color((byte)238, (byte)232, (byte)170, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #98FB98.
    public static readonly Color PaleGreen = new Color((byte)152, (byte)251, (byte)152, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #AFEEEE.
    public static readonly Color PaleTurquoise = new Color((byte)175, (byte)238, (byte)238, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #DB7093.
    public static readonly Color PaleVioletRed = new Color((byte)219, (byte)112, (byte)147, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #FFEFD5.
    public static readonly Color PapayaWhip = new Color(byte.MaxValue, (byte)239, (byte)213, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #FFDAB9.
    public static readonly Color PeachPuff = new Color(byte.MaxValue, (byte)218, (byte)185, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #CD853F.
    public static readonly Color Peru = new Color((byte)205, (byte)133, (byte)63, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #FFC0CB.
    public static readonly Color Pink = new Color(byte.MaxValue, (byte)192, (byte)203, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #DDA0DD.
    public static readonly Color Plum = new Color((byte)221, (byte)160, (byte)221, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #B0E0E6.
    public static readonly Color PowderBlue = new Color((byte)176, (byte)224, (byte)230, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #800080.
    public static readonly Color Purple = new Color((byte)128, (byte)0, (byte)128, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #663399.
    public static readonly Color RebeccaPurple = new Color((byte)102, (byte)51, (byte)153, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #FF0000.
    public static readonly Color Red = new Color(byte.MaxValue, (byte)0, (byte)0, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #BC8F8F.
    public static readonly Color RosyBrown = new Color((byte)188, (byte)143, (byte)143, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #4169E1.
    public static readonly Color RoyalBlue = new Color((byte)65, (byte)105, (byte)225, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #8B4513.
    public static readonly Color SaddleBrown = new Color((byte)139, (byte)69, (byte)19, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #FA8072.
    public static readonly Color Salmon = new Color((byte)250, (byte)128, (byte)114, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #F4A460.
    public static readonly Color SandyBrown = new Color((byte)244, (byte)164, (byte)96, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #2E8B57.
    public static readonly Color SeaGreen = new Color((byte)46, (byte)139, (byte)87, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #FFF5EE.
    public static readonly Color SeaShell = new Color(byte.MaxValue, (byte)245, (byte)238, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #A0522D.
    public static readonly Color Sienna = new Color((byte)160, (byte)82, (byte)45, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #C0C0C0.
    public static readonly Color Silver = new Color((byte)192, (byte)192, (byte)192, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #87CEEB.
    public static readonly Color SkyBlue = new Color((byte)135, (byte)206, (byte)235, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #6A5ACD.
    public static readonly Color SlateBlue = new Color((byte)106, (byte)90, (byte)205, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #708090.
    public static readonly Color SlateGray = new Color((byte)112, (byte)128, (byte)144, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #708090.
    //public static readonly Color SlateGrey = SlateGray;

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #FFFAFA.
    public static readonly Color Snow = new Color(byte.MaxValue, (byte)250, (byte)250, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #00FF7F.
    public static readonly Color SpringGreen = new Color((byte)0, byte.MaxValue, (byte)127, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #4682B4.
    public static readonly Color SteelBlue = new Color((byte)70, (byte)130, (byte)180, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #D2B48C.
    public static readonly Color Tan = new Color((byte)210, (byte)180, (byte)140, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #008080.
    public static readonly Color Teal = new Color((byte)0, (byte)128, (byte)128, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #D8BFD8.
    public static readonly Color Thistle = new Color((byte)216, (byte)191, (byte)216, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #FF6347.
    public static readonly Color Tomato = new Color(byte.MaxValue, (byte)99, (byte)71, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #00000000.
    public static readonly Color Transparent = new Color(0, 0, 0, 0);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #40E0D0.
    public static readonly Color Turquoise = new Color((byte)64, (byte)224, (byte)208, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #EE82EE.
    public static readonly Color Violet = new Color((byte)238, (byte)130, (byte)238, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #F5DEB3.
    public static readonly Color Wheat = new Color((byte)245, (byte)222, (byte)179, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #FFFFFF.
    public static readonly Color White = new Color(byte.MaxValue, byte.MaxValue, byte.MaxValue, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #F5F5F5.
    public static readonly Color WhiteSmoke = new Color((byte)245, (byte)245, (byte)245, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #FFFF00.
    public static readonly Color Yellow = new Color(byte.MaxValue, byte.MaxValue, (byte)0, byte.MaxValue);

    //
    // Summary:
    //     Represents a Color matching the W3C definition that has an hex value of #9ACD32.
    public static readonly Color YellowGreen = new Color((byte)154, (byte)205, (byte)50, byte.MaxValue);

    public static readonly Color GroundBlue = new Color((byte)222, (byte)240, (byte)254, byte.MaxValue);

    public static readonly Color MapBlue = new Color((byte)77, (byte)106, (byte)176, byte.MaxValue);

    public static readonly Color CellBlue = new Color((byte)133, (byte)148, (byte)189, byte.MaxValue);

    public static readonly Color LightWhite = new Color((byte)242, (byte)242, (byte)242, byte.MaxValue);

    public static readonly Color LightBlack = new Color((byte)59, (byte)64, (byte)68, byte.MaxValue);

    public static readonly Color MiddleBlack = new Color((byte)38, (byte)18, (byte)8, byte.MaxValue);
}
