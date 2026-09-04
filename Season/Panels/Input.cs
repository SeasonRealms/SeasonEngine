// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Panels;

public class Input : Panel
{
    public int? WidthMin;

    public Season.Basic.Color Color;

    Shape Line;

    public Texts Texts, Desc;

    public Sprite2D Remove;

    public string Text { get; set; }

    public string Des { get; set; }

    public Season.Controls.TextAlignment Alignment { get; set; }

    public Action OnAction, OnClear;

    public bool Abbreviate = true;

    public bool Translate = true;

    public bool ShowClear = false;

    public Input()
    {
        Height = 45;

        Line = new Shape()
        {
            Type = ShapeType.Dot,
            Color = Season.Basic.Colors.Gray
        };
        AddControl(Line);

        Texts = new Texts()
        {
            Scale = Vector2.One * 0.8f,
            ShowDot = true
        };
        AddControl(Texts);

        Remove = new Sprite2D()
        {
            Name = "Assets/Clear.png",
            OnClick = () =>
            {
                OnClear?.Invoke();
            }
        };
        AddControl(Remove);

        Desc = new Texts()
        {
            Scale = Vector2.One * 0.7f,
            Color = Season.Basic.Colors.Gray
        };
        AddControl(Desc);
    }

    async Task InvokeAction()
    {
        if (OnAction == null)
        {
            Text = await DeviceServices.Dialog.ShowKeyboard("Input".Translate(), "", new string[] { "OK".Translate(), "Cancel".Translate() }, Text);
            Text = Text.NullToStringTrim();
        }
        else
        {
            OnAction?.Invoke();
        }
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? width = null, float? height = null, float? posZ = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        if (TouchService.Enable && Enable && Alpha > 0)
        {
            MouseOver = PosX < TouchService.PoX && TouchService.PoX < PosX + Line.Width && PosY < TouchService.PoY && TouchService.PoY < PosY + Height;

            if (MouseOver)
            {
                if (TouchService.IsReleased)
                {
                    InvokeAction();

                    result = true;
                }
            }
            else
            {

            }
        }
        else
        {
            MouseOver = false;
        }

        Line.Color = Enable ? Color : Season.Basic.Colors.Gray;
        Texts.Color = Enable ? Color : Season.Basic.Colors.Gray;

        if (WidthMin.HasValue && (Texts.Width is null || Texts.Width == 0 || (int)WidthMin > Texts.Width))
        {
            Line.Width = (int)WidthMin;
        }
        //else if (Width.HasValue)
        //{
        //    Line.Width = (int)(Width ?? 0);
        //}
        else
        {
            Line.Width = Texts.Width;
        }

        Line.Update(time, alpha: Alpha, posX: PosX, posY: PosY + 40, height: 3);

        if (Abbreviate)
        {
            Texts.WidthRequest = (int)(Width ?? 0);
            Texts.HeightRequest = Texts.LineHeight;
        }

        if (Alignment is Season.Controls.TextAlignment.Left)
        {
            Texts.PosX = Line.PosX;
        }
        else if (Alignment is Season.Controls.TextAlignment.Center)
        {
            Texts.PosX = Line.PosX + ((Line.Width ?? 0) - (Texts.Width ?? 0)) / 2;
        }
        else
        {
            Texts.PosX = Line.PosX + (Line.Width ?? 0) - (Texts.Width ?? 0);
        }

        Texts.Translate = Translate;
        Texts.Content = Text;
        Texts.Update(time, alpha: Alpha, posY: Line.PosY - 40);

        if (ShowClear)
        {
            Remove.Alpha = Alpha;
        }
        else
        {
            Remove.Alpha = 0f;
        }
        Remove.Update(time, posX: Line.PosX + Line.Width + 20, posY: Line.PosY - Remove.Height + 5, width: 40, height: 40);

        Width = Line.Width ?? 0;

        if (ShowClear)
        {
            Width = Width + 20 + (int)(Remove.Width ?? 0);
        }

        Desc.Translate = Translate;
        Desc.Content = Des;
        Desc.Update(time, alpha: Alpha, posX: Line.PosX, posY: Line.PosY + 5);

        return result;
    }
}
