// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Controls;

/// <summary>
/// Procedural geometry shape types.
/// Dot and Square are 1x1 solid-color filled primitives.
/// Circle, RoundRect, Gradual, and GradualCircle are size-dependent procedural textures.
/// </summary>
public enum ShapeType
{
    /// <summary>1x1 pixel solid-color dot, equivalent to a 1x1 Square.</summary>
    Dot,

    /// <summary>Solid-color filled rectangle. At the default 1x1 size it matches Dot, but any width and height may be specified.</summary>
    Square,

    /// <summary>Antialiased filled ellipse or circle. It becomes a circle when width and height are equal.</summary>
    Circle,

    /// <summary>Antialiased rounded rectangle.</summary>
    RoundRect,

    /// <summary>Rectangular frame with a fully transparent center. Border thickness is controlled by Shape.Border.</summary>
    RectFrame,

    /// <summary>Rectangle with top-to-bottom alpha gradient.</summary>
    Gradual,

    /// <summary>Circle with radial gradient.</summary>
    GradualCircle,
}

/// <summary>
/// Procedural geometry shape control.
/// Replaces Sprite2D for Dot, Square, Circle, RoundRect, Gradual, and GradualCircle.
/// Inherits ID, Name, Color, Alpha, PosX, PosY, Width, Height, and related properties from SpriteBase.
///
/// Key differences from Sprite2D:
/// - Texture content is determined by ShapeType + Width + Height and does not depend on external files.
/// - GPU resources are cached in Graphics by (ShapeType, Width, Height), so shapes of the same size share textures.
/// - Size changes automatically trigger texture rebuilding, without external hard-coded special handling.
/// </summary>
public class Shape : SpriteBase, IRenderOrder
{
    public int Layer { get; set; }

    public int Order { get; set; }

    /// <summary>Shape type: Dot, Square, Circle, RoundRect, RectFrame, Gradual, or GradualCircle.</summary>
    public ShapeType Type { get; init; }

    private float _border = 1f;

    /// <summary>Rectangle frame border thickness, used only by RectFrame. Changes mark ContentDirty and trigger texture rebuilding.</summary>
    public float Border
    {
        get => _border;
        set
        {
            if (_border != value)
            {
                _border = value;
                ContentDirty = true;
            }
        }
    }

    public override string ToString()
    {
        return Type.ToString() + "-" + Width + "-" + Height;
    }

    public override async Task<bool> Load()
    {
        bool result = false;

        if (Type == ShapeType.Dot || (Width > 0 && Height > 0))
        {
            await Graphics.Instance.LoadShape(this);

            result = true;
        }

        return result;
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? width = null, float? height = null, float? posZ = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, width: width, height: height, posZ: posZ, depth: depth);

        if (ContentDirty)
        {
            if (width.HasValue && width.Value > 0 && height.HasValue && height.Value > 0)
            {
                
            }
            else if (Width > 0 && Height > 0)
            {
                
            }
            else
            {
                ContentDirty = false;
            }
        }
        else
        {
            // Width and Height may be null on the first AddControl frame.
            // The Load gate `(Width > 0)` evaluates null as false, so first load would fail and must be retried
            // after Update provides size values. The older condition `(Width == 0)` also evaluates null as false
            // and would never fire again. This path therefore handles null explicitly.
            if (Type != ShapeType.Dot && (Width is null || Width == 0 || Height is null || Height == 0)
                && width.HasValue && width.Value > 0 && height.HasValue && height.Value > 0)
            {
                ContentDirty = true;
            }
        }

        if (ContentDirty)
        {
            ContentDirty = false;

            Ready = false;
            Changed = true;

            // RectFrame texture generation depends on Border, but the instance cache key (Type, ID) does not include Border.
            // If the old entry is not removed first, LoadShape will hit the stale texture and Border changes will not take effect.
            if (Type == ShapeType.RectFrame)
            {
                Graphics.Instance.DisposeShape(this);
            }

            DeviceServices.BaseApp?.RequestLoad(this);
        }

        // Input hit test.
        if (TouchService.Enable)
        {
            MouseOver = PosX < TouchService.PoX && TouchService.PoX < PosX + Width && PosY < TouchService.PoY && TouchService.PoY < PosY + Height;
        }
        else
        {
            MouseOver = false;
        }

        Graphics.Instance.UpdateShape(this);

        return result;
    }

    public override bool Draw()
    {
        var result = false;

        if (base.Draw())
        {
            if (Ready && Width > 0 && Height > 0 && Alpha > 0f)
            {
                Graphics.Instance.DrawShape(this);

                result = true;
            }
        }

        return result;
    }

    public override void Dispose()
    {
        base.Dispose();

        Graphics.Instance.DisposeShape(this);
    }
}
