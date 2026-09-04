// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.AI.Panels;

public class AIButton : Panel
{
    Shape shape;

    Texts texts;

    public AIButton()
    {
        RenderDomain = Season.Controls.RenderDomain.Overlay;

        shape = new Shape()
        {
            Type = ShapeType.Circle,
            OnClick = () =>
            {
                OnClick?.Invoke();
            }
        };
        AddControl(shape);

        texts = new Texts()
        {
            Content = "AI",
            Scale = Vector2.One * 2f
        };
        AddControl(texts);
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        int size = 60;

        shape.Color = shape.MouseOver ? Season.Basic.Colors.DarkRed : Season.Basic.Colors.White;
        if (shape.Update(time, alpha: Alpha > 0 ? 0.6f : Alpha, posX: DeviceServices.BaseApp.ExtendResolution.X - size - size * 2, posY: DeviceServices.BaseApp.ExtendResolution.Y / 2 - size / 2, width: size * 2, height: size * 2))
        {
            result = true;
        }

        texts.Color = shape.MouseOver ? Season.Basic.Colors.White : Season.Basic.Colors.Black;
        if (texts.Update(time, alpha: Alpha, posX: shape.PosX + (shape.Width - texts.Width) / 2, posY: shape.PosY + 15))
        {
            result = true;
        }

        return result;
    }
}
