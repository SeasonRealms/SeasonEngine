// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Platforms.Windows.DirectX;

[StructLayout(LayoutKind.Sequential)]
internal struct TextGlyphData
{
    public Vector4 UvRect;
    public Vector4 Color;
    public Vector4 Metrics;
}
