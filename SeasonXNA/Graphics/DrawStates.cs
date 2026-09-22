// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Microsoft.Xna.Framework.Graphics;

/// <summary>D01-A tint policy over Season's straight-alpha pipeline, not a mutable GPU state.</summary>
public sealed class BlendState
{
    private BlendState() { }
    public static BlendState AlphaBlend { get; } = new();
}

public sealed class SamplerState
{
    private SamplerState(bool point) => IsPoint = point;
    internal bool IsPoint { get; }
    public static SamplerState LinearClamp { get; } = new(false);
    public static SamplerState PointClamp { get; } = new(true);
}
