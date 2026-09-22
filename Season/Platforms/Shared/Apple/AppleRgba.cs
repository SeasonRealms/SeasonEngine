// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Platforms.Shared.Apple;

internal static class AppleRgba
{
    // CoreGraphics bitmap contexts require premultiplied pixels; Season textures use straight alpha.
    internal static void Unpremultiply(Span<byte> pixels)
    {
        if (pixels.Length % 4 != 0) throw new ArgumentException("Expected packed RGBA8 pixels.", nameof(pixels));
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int alpha = pixels[i + 3];
            for (int channel = 0; channel < 3; channel++)
                pixels[i + channel] = alpha == 0 ? (byte)0
                    : (byte)Math.Min(255, (pixels[i + channel] * 255 + alpha / 2) / alpha);
        }
    }
}
