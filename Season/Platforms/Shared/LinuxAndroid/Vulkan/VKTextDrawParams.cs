// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Runtime.InteropServices;

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

/// <summary>
/// Constant buffer for text draw parameters (b4 / binding 11).
/// GLSL std140 layout (reordered to avoid padding, 32 bytes with no waste):
///   vec2 textAtlasSize   @ 0   (8-byte aligned)
///   float textPxRange    @ 8   (4-byte aligned)
///   float textGlobalAlpha @ 12  (4-byte aligned)
///   vec4 textBaseColor   @ 16  (16-byte aligned)
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 32)]
internal struct VKTextDrawParams
{
    /// <summary>Atlas texture resolution (width, height)</summary>
    [FieldOffset(0)] public Vector2 AtlasSize;

    /// <summary>MSDF pixel range (from Font.PixelRange, for example 4)</summary>
    [FieldOffset(8)] public float PxRange;

    /// <summary>Global control alpha (Texts.Alpha)</summary>
    [FieldOffset(12)] public float GlobalAlpha;

    /// <summary>Default text color (Texts.Color, used as fallback when per-glyph color override is absent)</summary>
    [FieldOffset(16)] public Vector4 TextColor;
}
