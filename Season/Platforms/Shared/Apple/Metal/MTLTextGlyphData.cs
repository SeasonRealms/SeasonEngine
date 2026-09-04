// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Runtime.InteropServices;

namespace Season.Platforms.Shared.Apple.Metal;

/// <summary>
/// Text glyph data for GPU instancing, aligned one to one with DX TextGlyphData and VKTextGlyphData.
/// It is stored in VS buffer(5), reusing the morphDeltas slot as const device float*,
/// with one element laid out as 12 floats, meaning 48 bytes, so float[] can be interpreted as TextGlyphData[].
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MTLTextGlyphData
{
    /// <summary>Normalized atlas sub-rectangle UV values as SourceX, SourceY, SourceWidth, and SourceHeight in the 0..1 range.</summary>
    public Vector4 UvRect;

    /// <summary>Per-instance color in RGBA. When hasColorOverride is set this stores the real color, otherwise it is (1, 1, 1, 1).</summary>
    public Vector4 Color;

    /// <summary>Glyph metrics stored as glyphWidthPx, glyphHeightPx, pxRange, and hasColorOverride as 0 or 1.</summary>
    public Vector4 Metrics;
}
