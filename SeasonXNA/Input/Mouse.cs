// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Season.Basic;
using Season.Storage;

namespace Microsoft.Xna.Framework.Input;

/// <summary>
/// XNA facade over the engine pointer state. The engine reports pointer coordinates in
/// back-buffer pixels divided by BaseApp.Scale, so they are multiplied back to physical
/// pixels and pushed through the frame canvas transform (Draw2D.ToCanvas) to yield the
/// canvas coordinates ported titles consume. The wheel sign is inverted: the engine
/// accumulates wheel-up as a negative delta, XNA as a positive one.
/// </summary>
public static class Mouse
{
    public static MouseState GetState()
    {
        int x = TouchService.PoX ?? 0;
        int y = TouchService.PoY ?? 0;

        var app = DeviceServices.BaseApp;

        if (app != null)
        {
            float scale = app.Scale > 0f ? app.Scale : 1f;

            var canvas = app.Canvas2D.ToCanvas(new System.Numerics.Vector2(x * scale, y * scale));

            x = (int)MathF.Round(canvas.X);
            y = (int)MathF.Round(canvas.Y);
        }

        return new MouseState(
            x,
            y,
            -(TouchService.PoZ ?? 0),
            TouchService.IsDown ? ButtonState.Pressed : ButtonState.Released,
            ButtonState.Released,
            TouchService.IsRightDown ? ButtonState.Pressed : ButtonState.Released,
            ButtonState.Released,
            ButtonState.Released);
    }
}
