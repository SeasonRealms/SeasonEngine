// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.

using System.Numerics;
using System.Runtime.InteropServices;
using Season.Rendering;

namespace Season.Platforms.Shared.Apple.Metal;

[StructLayout(LayoutKind.Sequential)]
internal struct Draw2DConstants
{
    internal Vector4 OriginXAxis, YAxis, Uv, Color, Clip, Parameters, UvClamp;

    internal static Draw2DConstants Build(Draw2DCommand command, Rect2D destination, Rect2D source,
        uint width, uint height, Vector2 outputSize, float pixelRange)
    {
        var origin = Vector2.Transform(destination.Position, command.Transform);
        var axisX = Vector2.TransformNormal(new(destination.Width, 0), command.Transform);
        var axisY = Vector2.TransformNormal(new(0, destination.Height), command.Transform);
        // Metal NDC is Y-up, while the drawable and clip coordinates are physical Y-down pixels.
        var toNdc = new Vector2(2 / outputSize.X, -2 / outputSize.Y);
        origin = origin * toNdc + new Vector2(-1, 1);
        axisX *= toNdc;
        axisY *= toNdc;
        float invW = 1f / width, invH = 1f / height;
        var uv = new Vector4(source.X * invW, source.Y * invH, source.Width * invW, source.Height * invH);
        if ((command.Flip & ImageFlip2D.Horizontal) != 0) { uv.X += uv.Z; uv.Z = -uv.Z; }
        if ((command.Flip & ImageFlip2D.Vertical) != 0) { uv.Y += uv.W; uv.W = -uv.W; }
        float insetX = Math.Min(0.5f, source.Width * 0.5f), insetY = Math.Min(0.5f, source.Height * 0.5f);
        return new()
        {
            OriginXAxis = new(origin, axisX.X, axisX.Y), YAxis = new(axisY, 0, 0),
            Uv = uv, Color = command.Color,
            Clip = new(command.Clip.X, command.Clip.Y, command.Clip.Right, command.Clip.Bottom),
            Parameters = new(command.Sampling == Sampling2D.Point ? 1 : 0, pixelRange, invW, invH),
            UvClamp = new((source.X + insetX) * invW, (source.Y + insetY) * invH,
                (source.Right - insetX) * invW, (source.Bottom - insetY) * invH)
        };
    }
}
