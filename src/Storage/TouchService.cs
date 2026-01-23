// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Storage;

internal static class TouchService
{
    internal static int? PoXPre;

    internal static int? PoYPre;

    internal static int? PoZPre;

    public static int? PoX { get; internal set; }

    public static int? PoY { get; internal set; }

    public static int? PoZ { get; internal set; }

    public static float MoveX { get; internal set; }

    public static float MoveY { get; internal set; }

    internal static bool isDownPre;

    internal static bool isDown;

    public static bool IsDown { get; internal set; }

    public static bool IsReleased = false;

    public static bool IsMoved { get; internal set; }

    public static Vector2? LastDownPos = null;

    public static void Update(float gameTime, float scale)
    {
        IsDown = IsReleased = IsMoved = false;

        MoveX = 0; MoveY = 0;

        if (isDown)
        {
            if (PoX != null && PoXPre != null && PoY != null && PoYPre != null)
            {
                MoveX = (int)PoX - (int)PoXPre;

                MoveY = (int)PoY - (int)PoYPre;

                if (MoveY != 0)
                {

                }
            }

            if (isDownPre || PoX is null || PoY is null)
            {

            }
            else
            {
                LastDownPos = new Vector2((float)PoX, (float)PoY);
            }

            PoXPre = PoX;

            PoYPre = PoY;

            IsDown = true;

            isDownPre = true;
        }
        else
        {
            if (isDownPre && LastDownPos != null)
            {
                var lastDownPos = (Vector2)LastDownPos;

                if (Math.Abs((float)PoX - lastDownPos.X) < 20 && Math.Abs((float)PoY - lastDownPos.Y) < 20)
                {
                    IsReleased = true;

                    LastDownPos = null;
                }
                else
                {
                    IsMoved = true;
                }

                isDownPre = false;
            }

            PoXPre = null;

            PoYPre = null;
        }
    }
}
