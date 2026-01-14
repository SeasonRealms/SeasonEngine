// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.

namespace Season.Utils;

public static class Extensions
{
    public static float NullToZero(this float? o)
    {
        return o == null ? 0 : (float)o;
    }

    public static string NullToString(this object o)
    {
        return o.NullToString("");
    }

    public static string NullToString(this object o, string words)
    {
        return o == null || string.IsNullOrEmpty(o.ToString()) ? words : o.ToString();
    }

    public static string NullToStringTrim(this object o)
    {
        return o.NullToString("").Trim();
    }

    public static bool IsNullOrWhiteSpace(this object obj)
    {
        return string.IsNullOrWhiteSpace(obj.NullToString());
    }

    public static bool IsNullOrWhiteSpace(this string str)
    {
        return string.IsNullOrWhiteSpace(str);
    }

    public static float? ToFloat(this string s)
    {
        float? result = null;

        if (float.TryParse(s.NullToStringTrim(), out float res))
        {
            result = res;
        }
        else
        {

        }

        return result;
    }

    public static int? ToInt(this string s)
    {
        int? result = null;

        if (int.TryParse(s.NullToStringTrim(), out int res))
        {
            result = res;
        }
        else
        {

        }

        return result;
    }

    public static byte[] StreamToBytes(this Stream stream)
    {
        using (stream)
        {
            byte[] bytes = new byte[stream.Length];
            stream.Read(bytes, 0, bytes.Length);
            stream.Seek(0, SeekOrigin.Begin);
            return bytes;
        }
    }

}
