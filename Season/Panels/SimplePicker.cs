// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Panels;

public class SimplePicker : Panel
{
    public int LineHeight { get; set; } = 80;

    float Time { get; set; }

    Shape border, ground;

    MovePanel movePanel;

    public Action OnSelect;

    public List<EData> Sources { get; set; }

    public List<EData> Results { get; set; }

    public Season.Basic.Color Color { get; set; } = Season.Basic.Colors.LightBlack;

    public Season.Basic.Color ColorHover { get; set; } = Season.Basic.Colors.Red;

    List<EData> SourcesView;

    List<Sprite2D> sourcesImages;

    List<Texts> sourcesTitles, sourcesDescs;

    public SimplePicker(List<EData> sources, List<EData> results)
    {
        RenderDomain = RenderDomain.Overlay;

        Sources = sources;

        Results = results;

        border = new Shape()
        {
            Type = ShapeType.Dot,
            Color = Season.Basic.Colors.Gray
        };
        AddControl(border);

        ground = new Shape()
        {
            Type = ShapeType.Dot,
            Color = new Season.Basic.Color(200, 200, 200, 255)
        };
        AddControl(ground);

        movePanel = new MovePanel()
        {
            MoveType = MoveType.Y,
            Color = Season.Basic.Colors.LightBlack,
            DisplayLine = true,
            EnableStartMoving = true,
            EnableEndMoving = true
        };
        AddPanel(movePanel);

        BuildSourcesView();
    }

    void BuildSourcesView()
    {
        sourcesImages = new List<Sprite2D> { };

        sourcesTitles = new List<Texts> { };

        sourcesDescs = new List<Texts> { };

        SourcesView = Sources;

        if (SourcesView == null || SourcesView.Count == 0)
        {

        }
        else
        {
            for (var i = 0; i < SourcesView.Count; i++)
            {
                var source = SourcesView[i];

                var image = new Sprite2D()
                {
                    Name = source.Image,
                    Color = source.Color == null ? Season.Basic.Colors.White : (Season.Basic.Color)source.Color,
                    OnClick = () =>
                    {
                        if (source.Enable)
                        {
                            Clear();

                            Results = new List<EData> { source };

                            OnSelect?.Invoke();
                        }
                    }
                };
                AddControl(image);

                var title = new Texts()
                {
                    Content = source.Title,
                    Scale = Vector2.One * 1.0f,
                    Color = Season.Basic.Colors.Black, //.LightBlack, // new Season.Basic.Color(65, 105, 225, 255),
                    ShowDot = true,
                    OnClick = () =>
                    {
                        image.OnClick?.Invoke();
                    }
                };
                AddControl(title);

                var desc = new Texts()
                {
                    Content = source.Desc,
                    Scale = Vector2.One * 0.85f,
                    Color = Season.Basic.Colors.LightBlack,
                    ShowDot = true,
                    OnClick = () =>
                    {
                        image.OnClick?.Invoke();
                    }
                };
                AddControl(desc);

                sourcesImages.Add(image);

                sourcesTitles.Add(title);

                sourcesDescs.Add(desc);
            }
        }
    }

    void Clear()
    {
        Results.Clear();
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        Time += time;

        if (Width is null or < 500)
        {
            Width = 500;
        }

        var stMax = sourcesTitles.Max(st => st.Width);

        if (stMax > Width)
        {
            Width = stMax;
        }

        Height = SourcesView.Count * LineHeight + 120;

        if (posY + Height > DeviceServices.BaseApp.ExtendResolution.Y)
        {
            if (Height > DeviceServices.BaseApp.ExtendResolution.Y)
            {
                posY = 0;

                Height = (int)DeviceServices.BaseApp.ExtendResolution.Y;
            }
            else
            {
                posY = (int)DeviceServices.BaseApp.ExtendResolution.Y - (int)Height;
            }
        }

        if (posX + Width > DeviceServices.BaseApp.ExtendResolution.X)
        {
            posX = (int)DeviceServices.BaseApp.ExtendResolution.X - (int)Width;
        }

        border.Update(time, alpha: 1f, posX: posX, posY: posY, width: Width, height: Height);

        ground.Update(time, alpha: 1f, posX: posX + 2, posY: posY + 2, width: Width - 4, height: Height - 4);

        movePanel.Alpha = Alpha;
        movePanel.PosX = (int)ground.PosX;
        movePanel.PosY = (int)ground.PosY;
        movePanel.Width = (int)ground.Width;
        movePanel.Height = (int)ground.Height;
        movePanel.SizeX = movePanel.Width ?? 0;
        movePanel.SizeY = (SourcesView.Count > 0 ? sourcesImages[SourcesView.Count - 1].PosY - sourcesImages[0].PosY : 0) + 150;
        if (movePanel.SizeY < movePanel.Height)
        {
            movePanel.SizeY = movePanel.Height ?? 0;
        }
        if (movePanel.Update(time))
        {
            //return true;
        }

        for (var i = 0; i < SourcesView.Count; i++)
        {
            var source = SourcesView[i];

            var posY0 = (int)movePanel.PosY + 10 + LineHeight * i - (int)movePanel.Scroll;

            if (source.Image.IsNullOrWhiteSpace() && source.Color == null)
            {
                sourcesImages[i].Alpha = 0f;
            }
            else
            {
                sourcesImages[i].Alpha = 1f;
            }
            if (sourcesImages[i].Update(time, posX: ground.PosX + 10, posY: posY0 + 10, width: 60, height: 60))
            {
                result = true;
            }

            sourcesTitles[i].Color = Sources[i].Enable ? (sourcesTitles[i].MouseOver || sourcesDescs[i].MouseOver ? ColorHover : Color) : Season.Basic.Colors.Gray;
            sourcesTitles[i].WidthRequest = (int)movePanel.Width - 10 - 80 - 20;
            sourcesTitles[i].HeightRequest = sourcesTitles[i].LineHeight;
            if (sourcesTitles[i].Update(time, alpha: 1f, posX: sourcesImages[i].PosX + 80, sourcesImages[i].PosY))
            {
                result = true;
            }

            sourcesDescs[i].Color = sourcesTitles[i].MouseOver || sourcesDescs[i].MouseOver ? ColorHover : Color;
            sourcesDescs[i].WidthRequest = sourcesTitles[i].WidthRequest;
            sourcesDescs[i].HeightRequest = sourcesDescs[i].LineHeight;
            if (sourcesDescs[i].Update(time, alpha: 1f, posX: sourcesTitles[i].PosX, posY: sourcesTitles[i].PosY + 45))
            {
                result = true;
            }

            if (posY0 + 70 < movePanel.PosY || posY0 > movePanel.PosY + movePanel.Height)
            {
                //sourcesImages[i].Alpha = 0f;
                //sourcesTitles[i].Alpha = 0f;
                //sourcesDescs[i].Alpha = 0f;
            }
            else
            {
                //if (sourcesImages[i].Update(time))
                //{
                //    //return;
                //}

                //if (sourcesTitles[i].Update(time))
                //{
                //    //return;
                //}

                //if (sourcesDescs[i].Update(time))
                //{
                //    //return;
                //}
            }
        }

        if (border.MouseOver)
        {
            result = true;
        }
        else
        {
            if (TouchService.IsDown)
            {
                OnClose?.Invoke();

                result = true;
            }
        }

        return result;
    }
}
