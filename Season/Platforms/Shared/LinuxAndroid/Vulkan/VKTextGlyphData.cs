// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Runtime.InteropServices;

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

/// <summary>
/// GPU-instanced text glyph data, aligned 1:1 with DX TextGlyphData.
/// Stored in the binding 10 StorageBuffer as one element per 12 floats (48 bytes),
/// meaning `float[]` is interpreted as `TextGlyphData[]`.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct VKTextGlyphData
{
    /// <summary>Normalized 0..1 UV sub-rectangle in the atlas
    /// (SourceX, SourceY, SourceWidth, SourceHeight)</summary>
    public Vector4 UvRect;

    /// <summary>Per-instance color (R, G, B, A).
    /// Uses the real color when hasColorOverride is set, otherwise (1, 1, 1, 1).</summary>
    public Vector4 Color;

    /// <summary>Glyph metrics (glyphWidthPx, glyphHeightPx, pxRange, hasColorOverride 0/1)</summary>
    public Vector4 Metrics;
}
