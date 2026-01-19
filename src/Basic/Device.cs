// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Basic;

internal static class Device
{
    public static int Width { get; set; }

    public static int Height { get; set; }

    public static float Scale = 1f;

    public static string Language
    {
        get
        {
            return CultureInfo.CurrentCulture.ToString();
        }
    }

    public static bool Debug
    {
        get
        {
            bool debug = false;
#if DEBUG
            debug = true;
#endif
            return debug;
        }
    }

}
