// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Panels;

public class BoardPanel : Panel
{
    public Season.Basic.Color BoardColor = new Season.Basic.Color(200, 200, 200, 255);

    public Season.Basic.Color FrameColor = Season.Basic.Colors.White;

    /// <summary>
    /// Viewport left unpainted on the board surface, in logical pixels. A 3D control drawn in the
    /// scene pass is hidden by the overlay board, so a page that shows a live model publishes the
    /// rect it renders into here; everything else keeps the opaque background.
    /// </summary>
    public Season.Utils.Rect? Hole;

    Shape board;

    Shape lineLeft, lineTop, lineRight, lineDown;

    HoleBands holeBands;

    public BoardPanel()
        : base()
    {
        RenderDomain = Season.Controls.RenderDomain.Overlay;

        board = new Shape()
        {
            Type = ShapeType.Dot
        };
        AddControl(board);

        // Registered after the board so the carved bands cover it in the same frame.
        holeBands = new HoleBands();
        holeBands.AddTo(this);

        lineLeft = new Shape()
        {
            Type = ShapeType.Dot,
            Color = FrameColor
        };
        AddControl(lineLeft);

        lineTop = new Shape()
        {
            Type = ShapeType.Dot,
            Color = FrameColor
        };
        AddControl(lineTop);

        lineRight = new Shape()
        {
            Type = ShapeType.Dot,
            Color = FrameColor
        };
        AddControl(lineRight);

        lineDown = new Shape()
        {
            Type = ShapeType.Dot,
            Color = FrameColor
        };
        AddControl(lineDown);
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        LayoutSurface(time);

        if (TouchService.Enable && TouchService.IsReleased && !board.IsHit)
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

    /// <summary>
    /// Lays out the background surface, its carved hole and therefore the frame lines' basis.
    /// <see cref="Update"/> calls it first, and a page that only learns its hole rect after its
    /// own controls were updated calls it again at the end of that frame, so the window and the
    /// 3D control it reveals land on the same pixels instead of the window trailing by a frame.
    /// </summary>
    protected void LayoutSurface(float time)
    {
        // With a hole the single quad is switched off by alpha rather than transparency, because
        // board.IsHit must keep covering the full rect (a tap inside the hole must not close the
        // board) and the frame lines reuse board.Alpha.
        board.Color = Hole is null ? BoardColor : new Season.Basic.Color(BoardColor, 0);
        board.Update(time, alpha: Alpha, posX: PosX, posY: PosY, width: Width, height: Height);

        holeBands.Update(time, board.PosX, board.PosY, board.Width ?? 0f, board.Height ?? 0f, Hole, BoardColor, board.Alpha);
    }
}
