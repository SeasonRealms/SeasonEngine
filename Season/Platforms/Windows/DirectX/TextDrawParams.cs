// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Platforms.Windows.DirectX;

[StructLayout(LayoutKind.Explicit, Size = 32)]
internal struct TextDrawParams
{
    [FieldOffset(0)] public float PxRange;
    [FieldOffset(4)] public Vector2 AtlasSize;
    [FieldOffset(12)] public float GlobalAlpha;
    [FieldOffset(16)] public Vector4 TextColor;
}
