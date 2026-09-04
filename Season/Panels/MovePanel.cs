// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Panels;

public enum MoveType
{
    X,
    Y
}

public class MovePanel : Panel
{
    public MoveType MoveType { get; set; }

    public float Time { get; set; }

    public float Padding { get; set; }

    public float Scroll { get; set; }

    public float SizeX { get; set; }

    public float SizeY { get; set; }

    public string Status { get; set; }

    public bool DisplayLine { get; set; }

    public bool EnableStartMoving { get; set; }

    public bool EnableEndMoving { get; set; }

    public Vector4 Color { get; set; }

    float MoveDistance;

    float ScrollTarget;

    Shape line;

    public MovePanel()
    {
        line = new Shape()
        {
            Type = ShapeType.Dot,
            Color = Season.Basic.Colors.Gray
        };
        AddControl(line);
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? width = null, float? height = null, float? posZ = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        Time += time;

        bool move = false;

        if (TouchService.PoZ != null && TouchService.PoZ != 0)
        {
            if (PosX <= TouchService.PoX && TouchService.PoX <= PosX + Width && PosY <= TouchService.PoY && TouchService.PoY <= PosY + Height)
            {
                Scroll += (float)TouchService.PoZ;

                TouchService.PoZ = 0;

                move = true;
            }
        }

        if (Status is null or "")
        {
            bool inPanel = PosX < TouchService.PoX && TouchService.PoX < PosX + Width && PosY < TouchService.PoY && TouchService.PoY < PosY + Height;

            if (inPanel)
            {
                if (MoveType is MoveType.X)
                {
                    if (TouchService.MoveX != 0)
                    {
                        Scroll -= TouchService.MoveX;
                        move = true;
                    }
                }
                else
                {
                    if (TouchService.MoveY != 0)
                    {
                        Scroll -= TouchService.MoveY;
                        move = true;
                    }
                }
            }

            if (!TouchService.IsDown)
            {
                if (EnableStartMoving)
                {
                    if (Scroll < 0)
                    {
                        Status = "UpMoving";

                        Time = 0f;

                        MoveDistance = Scroll;
                    }
                }
                
                if (EnableEndMoving)
                {
                    if (MoveType is MoveType.X)
                    {
                        if (Scroll > SizeX - Width)  //SizeY > ViewPort.W && 
                        {
                            Status = "DownMoving";

                            Time = 0f;

                            MoveDistance = Scroll - SizeX + (Width ?? 0);

                            ScrollTarget = SizeX - (Width ?? 0);
                        }
                    }
                    else
                    {
                        if (Scroll > SizeY - Height)  //SizeY > ViewPort.W && 
                        {
                            Status = "DownMoving";

                            Time = 0f;

                            MoveDistance = Scroll - SizeY + (Height ?? 0);

                            ScrollTarget = SizeY - (Height ?? 0);
                        }
                    }
                }
            }
        }
        else if (Status is "UpLoading")
        {

        }
        else if (Status is "UpMoving")
        {
            Time += time;

            if (Time >= 1f)
            {
                Time = 1f;
            }

            Scroll = Scroll * (1 - Time);

            if (Scroll >= 0)
            {
                Status = "";
            }
        }
        else if (Status is "DownLoading")
        {

        }
        else if (Status is "DownMoving")
        {
            Time += time;

            if (Time >= 1f)
            {
                Time = 1f;
            }

            var targetMove = 0f;

            if (MoveType is MoveType.X)
            {
                if (Scroll == 0)
                {

                }
                else if (Scroll < SizeX - Width)
                {
                    targetMove = 0;
                }
                else
                {
                    targetMove = SizeX - (Width ?? 0);
                }
            }
            else
            {
                if (Scroll == 0)
                {

                }
                else if (Scroll < SizeY - Height) // Width)
                {
                    targetMove = 0;
                }
                else
                {
                    targetMove = SizeY - (Height ?? 0);
                }
            }

            Scroll = ScrollTarget + (Scroll - ScrollTarget) * (1 - Time);

            //Scroll = Scroll - MoveDistance * Time; // Scroll * (1 - Time) + targetMove * Time;

            if (Scroll <= ScrollTarget)  //SizeY - ViewPort.W)
            {
                Status = "";
            }
        }

        if (DisplayLine)
        {
            if (MoveType is MoveType.X)
            {
                var plus = Scroll * Width / SizeX;
                if (plus < 0)
                {
                    line.PosX = (int)PosX;

                    line.Width = (int)(Width * Width / SizeX + plus);
                }
                else if (plus > SizeX - Width)
                {
                    line.PosX = (int)(PosX + plus);

                    line.Width = (int)(PosX + Width - line.PosX);
                }
                else
                {
                    line.PosX = (int)(PosX + plus);

                    line.Width = (int)(Width * Width / SizeX);

                    if (line.PosX + line.Width > PosX + Width)
                    {
                        line.Width = (int)(PosX + Width - line.PosX);
                    }
                }

                if (line.Width == Width)
                {
                    line.Alpha = 0f;
                }
                else
                {
                    line.Alpha = 0.5f * Alpha; // * ListTime / FadeTime;
                }
                line.PosY = (int)(PosY + Height) - 12;
                line.Height = 4;
            }
            else
            {
                var plus = Scroll * Height / SizeY;
                if (plus < 0)
                {
                    line.PosY = (int)PosY;

                    line.Height = (int)(Height * Height / SizeY + plus);
                }
                else if (plus > SizeY - Height)
                {
                    line.PosY = (int)(PosY + plus);

                    line.Height = (int)(PosY + Height - line.PosY);
                }
                else
                {
                    line.PosY = (int)(PosY + plus);

                    line.Height = (int)(Height * Height / SizeY);

                    if (line.PosY + line.Height > PosY + Height)
                    {
                        line.Height = (int)(PosY + Height - line.PosY);
                    }
                }

                if (line.Height == Height)
                {
                    line.Alpha = 0f;
                }
                else
                {
                    line.Alpha = 0.5f * Alpha; // * ListTime / FadeTime;
                }
                line.PosX = (int)(PosX + (Width ?? 0)) - 12;
                line.Width = 4;
            }

            line.Color = Color;
            line.Update(time);
        }
        else
        {
            line.Alpha = 0f;
        }

        if (move == true)
        {
            result = true;
        }

        return result;
    }
}
