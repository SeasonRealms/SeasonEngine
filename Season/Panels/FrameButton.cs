// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Panels;

public class FrameButton : Panel
{
    public string Text { get; set; }

    public Vector2 TextSize
    {
        get
        {
            return buttonText.Scale;
        }
        set
        {
            buttonText.Scale = value;
        }
    }

    public Season.Basic.Color NormalGround = Season.Basic.Colors.DarkSlateGray;
    
    public Season.Basic.Color NormalText = Season.Basic.Colors.White;

    public Season.Basic.Color HoverGround = Season.Basic.Colors.DarkRed;

    public Season.Basic.Color HoverText = Season.Basic.Colors.White;

    public override bool MouseOver
    {
        get
        {
            return buttonGround.MouseOver;
        }
        set
        {
            buttonGround.MouseOver = value;
        }
    }

    Shape buttonGround, buttonFrame;
    Texts buttonText;

    public FrameButton()
        : base()
    {
        buttonGround = new Shape()
        {
            Type = ShapeType.Dot,
            OnClick = () =>
            {
                if (Enable)
                {
                    OnClick?.Invoke();
                }
            }
        };
        AddControl(buttonGround);

        buttonFrame = new Shape()
        {
            Type = ShapeType.RectFrame,
            Width = buttonGround.Width,
            Height = buttonGround.Height,
            Color = Season.Basic.Colors.LightBlack,
            Border = 3
        };
        AddControl(buttonFrame);

        buttonText = new Texts()
        {
            Scale = Vector2.One
        };
        AddControl(buttonText);
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        buttonGround.Color = Enable ? (Selected || buttonGround.MouseOver ? HoverGround : NormalGround) : Season.Basic.Colors.Gray;
        buttonGround.Update(time, posX: PosX, posY: PosY, width: Width, height: Height);

        buttonFrame.Update(time, posX: buttonGround.PosX, posY: buttonGround.PosY);

        buttonText.Content = Text;
        buttonText.Color = Enable ? (Selected || buttonGround.MouseOver ? HoverText : NormalText) : Season.Basic.Colors.Gray;
        buttonText.Update(time, posX: buttonGround.PosX + (buttonGround.Width - buttonText.Width) / 2, posY: buttonGround.PosY + 10);

        return result;
    }
}
