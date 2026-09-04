// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Utils;

public static class CompressExtensions
{
    public static byte[] CompressBytes(this string rawString)
    {
        if (string.IsNullOrEmpty(rawString) || rawString.Length == 0)
        {
            return null;
        }
        else
        {
            byte[] rawData = System.Text.Encoding.UTF8.GetBytes(rawString.ToString());
            byte[] zippedData = Compress(rawData);
            return zippedData;
        }
    }

    public static string DecompressBytes(this byte[] zippedBytes)
    {
        if (zippedBytes == null || zippedBytes.Length == 0)
        {
            return "";
        }
        else
        {
            byte[] decompressData = Decompress(zippedBytes);

            var json = System.Text.Encoding.UTF8.GetString(decompressData, 0, decompressData.Length);

            return json;
        }
    }

    public static string CompressString(this string rawString)
    {
        if (string.IsNullOrEmpty(rawString) || rawString.Length == 0)
        {
            return "";
        }
        else
        {
            byte[] rawData = System.Text.Encoding.UTF8.GetBytes(rawString.ToString());
            byte[] zippedData = Compress(rawData);
            return (string)(Convert.ToBase64String(zippedData));
        }
    }

    public static string DecompressString(this string zippedString)
    {
        zippedString = zippedString.Trim();
        if (string.IsNullOrEmpty(zippedString) || zippedString.Length == 0)
        {
            return "";
        }
        else
        {
            byte[] zippedData = Convert.FromBase64String(zippedString.ToString());
            byte[] decompressData = Decompress(zippedData);

            var json = System.Text.Encoding.UTF8.GetString(decompressData, 0, decompressData.Length);

            return json;
        }
    }

    public static byte[] Compress(this byte[] rawData)
    {
        using (var ms = new MemoryStream())
        {
            using (var compressedzipStream = new GZipStream(ms, CompressionMode.Compress, true))
            {
                compressedzipStream.Write(rawData, 0, rawData.Length);
                compressedzipStream.Dispose();
                return ms.ToArray();
            }
        }
    }

    public static byte[] Decompress(this byte[] zippedData)
    {
        using (var ms = new MemoryStream(zippedData))
        {
            using (var compressedzipStream = new GZipStream(ms, CompressionMode.Decompress))
            {
                using (var outBuffer = new MemoryStream())
                {
                    byte[] block = new byte[1024];
                    while (true)
                    {
                        int bytesRead = compressedzipStream.Read(block, 0, block.Length);
                        if (bytesRead <= 0)
                            break;
                        else
                            outBuffer.Write(block, 0, bytesRead);
                    }
                    compressedzipStream.Dispose();
                    return outBuffer.ToArray();
                }
            }
        }
    }
}
