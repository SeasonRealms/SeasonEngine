// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Controls;

/// <summary>
/// Paints a rectangle with a transparent window punched through it, using four axis-aligned bands
/// that never overlap.
///
/// Overlay panels are drawn after the scene pass, so a 3D control that renders into that pass is
/// only visible where no overlay pixel covers it. A panel that wants to show a live model therefore
/// cannot simply lower its background alpha (the model would show through everywhere as a ghost):
/// it has to leave the viewport rect unpainted. Same-color bands abut exactly, which makes the
/// result pixel-identical to the single quad the plain background draws.
///
/// An empty hole stays zero-cost: every band is switched off and the owner paints its
/// rectangle with the single quad it already had, so panels that never show a model pay
/// nothing for this helper.
/// </summary>
public sealed class HoleBands
{
    readonly Shape[] bands;

    public HoleBands(int capacity = 4)
    {
        if (capacity < 4)
            capacity = 4;

        bands = new Shape[capacity];

        for (int i = 0; i < bands.Length; i++)
        {
            bands[i] = new Shape() { Type = ShapeType.Dot };

            // An unused band stays hidden instead of painting a stray dot at the origin.
            bands[i].Alpha = 0f;
        }
    }

    /// <summary>
    /// Registers every band with the owning panel. The bands are plain controls of that panel, so
    /// they are drawn after the controls registered before them; add them once, in the constructor,
    /// after the surface this helper carves.
    /// </summary>
    public void AddTo(Panel panel)
    {
        for (int i = 0; i < bands.Length; i++)
        {
            panel?.AddControl(bands[i]);
        }
    }

    /// <summary>
    /// Lays the bands out over the rectangle of <paramref name="width"/> x <paramref name="height"/>
    /// at (<paramref name="posX"/>, <paramref name="posY"/>), leaving <paramref name="hole"/>
    /// unpainted. The hole shares the logical pixel space and is clipped to the rectangle, so a
    /// scrolled-out or oversized rect degrades to "carve what is inside".
    /// </summary>
    public void Update(float time, float posX, float posY, float width, float height, Season.Utils.Rect? hole, Season.Basic.Color color, float alpha)
    {
        int left = (int)MathF.Round(posX);
        int top = (int)MathF.Round(posY);
        int right = left + (int)MathF.Round(width);
        int bottom = top + (int)MathF.Round(height);

        int used = 0;

        bool carved = false;

        int holeLeft = 0, holeTop = 0, holeRight = 0, holeBottom = 0;

        if (hole is Season.Utils.Rect h && right > left && bottom > top)
        {
            holeLeft = Math.Clamp(h.X, left, right);
            holeTop = Math.Clamp(h.Y, top, bottom);
            holeRight = Math.Clamp(h.X + h.Width, left, right);
            holeBottom = Math.Clamp(h.Y + h.Height, top, bottom);

            carved = holeRight > holeLeft && holeBottom > holeTop;
        }

        if (carved)
        {
            used = Emit(used, time, left, top, right - left, holeTop - top, color, alpha);

            used = Emit(used, time, left, holeBottom, right - left, bottom - holeBottom, color, alpha);

            used = Emit(used, time, left, holeTop, holeLeft - left, holeBottom - holeTop, color, alpha);

            used = Emit(used, time, holeRight, holeTop, right - holeRight, holeBottom - holeTop, color, alpha);
        }

        // Without a hole the owner's own quad still covers the rectangle, so the bands stay dark
        // instead of painting the same surface twice.

        for (; used < bands.Length; used++)
        {
            bands[used].Update(time, alpha: 0f, posX: 0f, posY: 0f, width: 0f, height: 0f);
        }
    }

    /// <summary>Writes one band and returns the next free slot; degenerate bands are switched off
    /// instead of inflating the quad count.</summary>
    int Emit(int index, float time, int posX, int posY, int width, int height, Season.Basic.Color color, float alpha)
    {
        if (index >= bands.Length)
        {
            return index;
        }

        var band = bands[index];

        if (width <= 0 || height <= 0 || alpha <= 0f)
        {
            band.Update(time, alpha: 0f, posX: 0f, posY: 0f, width: 0f, height: 0f);
        }
        else
        {
            band.Color = color;

            band.Update(time, alpha: alpha, posX: posX, posY: posY, width: width, height: height);
        }

        return index + 1;
    }
}
