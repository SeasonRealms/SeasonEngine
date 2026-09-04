// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Panels;

public class BoardPanel : Panel
{
    public Season.Basic.Color FrameColor = Season.Basic.Colors.White;
    Shape board;

    Shape lineLeft, lineTop, lineRight, lineDown;

    public BoardPanel()
        : base()
    {
        RenderDomain = Season.Controls.RenderDomain.Overlay;

        board = new Shape()
        {
            Type = ShapeType.Dot,
            Color = new Season.Basic.Color(200, 200, 200, 255)
        };
        AddControl(board);

        lineLeft = new Shape()
        {
            Type = ShapeType.Dot,
            Color = Season.Basic.Colors.DarkRed
        };
        AddControl(lineLeft);

        lineTop = new Shape()
        {
            Type = ShapeType.Dot,
            Color = Season.Basic.Colors.DarkRed
        };
        AddControl(lineTop);

        lineRight = new Shape()
        {
            Type = ShapeType.Dot,
            Color = Season.Basic.Colors.DarkRed
        };
        AddControl(lineRight);

        lineDown = new Shape()
        {
            Type = ShapeType.Dot,
            Color = Season.Basic.Colors.DarkRed
        };
        AddControl(lineDown);
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        board.Update(time, alpha: Alpha, posX: PosX, posY: PosY, width: Width, height: Height);

        if (TouchService.Enable && TouchService.IsReleased && !board.MouseOver)
        {
            OnClose?.Invoke();

            result = true;
        }

        var thick = 2;
        lineLeft.Color = FrameColor;
        lineLeft.Update(time, alpha: board.Alpha, board.PosX - thick, board.PosY, thick, board.Height);
        lineTop.Color = FrameColor;
        lineTop.Update(time, alpha: board.Alpha, board.PosX - thick, board.PosY - thick, board.Width + thick * 2, thick);
        lineRight.Color = FrameColor;
        lineRight.Update(time, alpha: board.Alpha, board.PosX + board.Width, board.PosY - thick, 2, 2 + board.Height);
        lineDown.Color = FrameColor;
        lineDown.Update(time, alpha: board.Alpha, board.PosX - thick, board.PosY + board.Height, board.Width + thick * 2, thick);

        return result;
    }
}
