// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Runtime.InteropServices;

namespace Season.Platforms.Shared.Apple.Metal;

/// <summary>
/// Constant buffer for text drawing parameters at VS buffer(7) and FS buffer(3), with a layout fully matching VKTextDrawParams.
/// MSL constant-address-space layout, identical to GLSL std140 with no wasted bytes in this 32-byte block:
///   float2 textAtlasSize    @ 0   (8-byte aligned)
///   float textPxRange       @ 8   (4-byte aligned)
///   float textGlobalAlpha   @ 12  (4-byte aligned)
///   float4 textBaseColor    @ 16  (16-byte aligned)
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 32)]
internal struct MTLTextDrawParams
{
    /// <summary>Atlas texture resolution as width and height.</summary>
    [FieldOffset(0)] public Vector2 AtlasSize;

    /// <summary>MSDF pixel range from Font.PixelRange, for example 4.</summary>
    [FieldOffset(8)] public float PxRange;

    /// <summary>Global control alpha from Texts.Alpha.</summary>
    [FieldOffset(12)] public float GlobalAlpha;

    /// <summary>Default text color from Texts.Color, used as the fallback when per-glyph color overrides are absent.</summary>
    [FieldOffset(16)] public Vector4 TextColor;
}
