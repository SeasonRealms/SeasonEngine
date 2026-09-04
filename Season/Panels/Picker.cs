// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Panels;

public class Picker : Panel
{
    float Time { get; set; }

    public string Type { get; set; }

    public bool MultiSelect { get; set; }

    public string Desc { get; set; }

    Sprite2D mask, border, ground, query;

    Input search;

    Texts desc;

    public List<EData> Sources { get; set; }

    List<EData> SourcesView;

    List<Sprite2D> sourcesImages;

    List<Texts> sourcesTitles, sourcesDescs;

    Sprite2D blockSources1, blockSources2;

    Sprite2D gradualSources1, gradualSources2;

    MovePanel movePanelSources;

    float resultScale = 0.8f;

    public List<EData> Results { get; set; }

    List<Sprite2D> resultsImages;

    List<Texts> resultsTitles, resultsDescs;

    List<Sprite2D> resultsRemoves;

    Sprite2D blockResults1, blockResults2;

    Sprite2D gradualResults1, gradualResults2;

    MovePanel movePanelResults;

    public Picker(string type, List<EData> sources, List<EData> results)
    {
        Type = type;

        Sources = sources;

        Results = results;

        mask = new Sprite2D()
        {
            Name = "Square",
            Color = Season.Basic.Colors.Gray
        };
        AddControl(mask);

        border = new Sprite2D()
        {
            Name = "Square",
            Color = Season.Basic.Colors.White
        };
        AddControl(border);

        ground = new Sprite2D()
        {
            Name = "Square",
            Color = Season.Basic.Colors.White
        };
        AddControl(ground);

        search = new Input()
        {

        };
        AddPanel(search);

        query = new Sprite2D()
        {
            Name = "Buttons.png",
            Ext = ".png",
            Color = Season.Basic.Colors.White,
            SourceWidth = 1 / 15f, // 0.4f;
            SourceHeight = 1 / 17f, // 0.3f;
            OnClick = () =>
            {
                BuildSourcesView();
            }
        };
        AddControl(query);

        desc = new Texts()
        {
            Scale = Vector2.One * 0.6f,
            Color = Season.Basic.Colors.DarkRed,
            ShowDot = true
        };
        AddControl(desc);

        BuildSourcesView();

        blockSources1 = new Sprite2D()
        {
            Name = "Square",
            Color = Season.Basic.Colors.White
        };
        AddControl(blockSources1);

        blockSources2 = new Sprite2D()
        {
            Name = "Square",
            Color = Season.Basic.Colors.White
        };
        AddControl(blockSources2);

        gradualSources1 = new Sprite2D()
        {
            Name = "Gradual",
            Ext = "",
            Color = Season.Basic.Colors.White
        };
        AddControl(gradualSources1);

        gradualSources2 = new Sprite2D()
        {
            Name = "Gradual",
            Ext = "",
            Color = Season.Basic.Colors.White
        };
        AddControl(gradualSources2);

        movePanelSources = new MovePanel()
        {
            MoveType = MoveType.Y,
            DisplayLine = true,
            EnableStartMoving = true,
            EnableEndMoving = true
        };
        AddPanel(movePanelSources);

        resultsImages = new List<Sprite2D> { };

        resultsTitles = new List<Texts> { };

        resultsDescs = new List<Texts> { };

        resultsRemoves = new List<Sprite2D> { };

        if (results == null || results.Count == 0)
        {

        }
        else
        {
            for (var i = 0; i < results.Count; i++)
            {
                var result = results[i];

                AddOne(results, result, false);
            }
        }

        blockResults1 = new Sprite2D()
        {
            Name = "Square",
            Color = Season.Basic.Colors.White
        };
        AddControl(blockResults1);

        blockResults2 = new Sprite2D()
        {
            Name = "Square",
            Color = Season.Basic.Colors.White
        };
        AddControl(blockResults2);

        gradualResults1 = new Sprite2D()
        {
            Name = "Gradual",
            Ext = "",
            Color = Season.Basic.Colors.White
        };
        AddControl(gradualResults1);

        gradualResults2 = new Sprite2D()
        {
            Name = "Gradual",
            Ext = "",
            Color = Season.Basic.Colors.White
        };
        AddControl(gradualResults2);

        movePanelResults = new MovePanel()
        {
            MoveType = MoveType.Y,
            DisplayLine = true,
            EnableStartMoving = true,
            EnableEndMoving = true
        };
        AddPanel(movePanelResults);
    }

    void BuildSourcesView()
    {
        sourcesImages = new List<Sprite2D> { };

        sourcesTitles = new List<Texts> { };

        sourcesDescs = new List<Texts> { };

        SourcesView = Sources.Where(so => search.Text.IsNullOrWhiteSpace() || so.Key.Contains(search.Text) || so.Title.Contains(search.Text)).NullToEmptyList();

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
                    Name = source.Image.IsNullOrWhiteSpace() ? "Square" : source.Image,
                    Ext = System.IO.Path.GetExtension(source.Image.NullToString()).ToLower(),
                    Color = (!source.Image.IsNullOrWhiteSpace() && source.Image != "Square" || source.Color == null ? Season.Basic.Colors.White : ((Season.Basic.Color)source.Color)),
                    OnClick = () =>
                    {
                        if (MultiSelect)
                        {
                            var exist = Results.FirstOrDefault(re => re.Key == source.Key);

                            if (exist == null)
                            {
                                Results.Insert(0, source);

                                AddOne(Results, source, true);
                            }
                        }
                        else
                        {
                            Clear();

                            Results = new List<EData> { source };

                            AddOne(Results, source, true);

                            OnClose?.Invoke();
                        }
                    }
                };
                AddControl(image);

                var title = new Texts()
                {
                    Content = source.Title,
                    Scale = Vector2.One * 0.8f,
                    Color = new Season.Basic.Color(65, 105, 225, 255),
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
                    Scale = Vector2.One * 0.7f,
                    Color = Season.Basic.Colors.Gray,
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

        resultsImages = new List<Sprite2D> { };

        resultsTitles = new List<Texts> { };

        resultsDescs = new List<Texts> { };

        resultsRemoves = new List<Sprite2D> { };
    }

    void AddOne(List<EData> results, EData source, bool insert)
    {
        var image = new Sprite2D()
        {
            Name = source.Image.IsNullOrWhiteSpace() ? "Square" : source.Image,
            Ext = System.IO.Path.GetExtension(source.Image.NullToString()).ToLower(),
            Color = (!source.Image.IsNullOrWhiteSpace() && source.Image != "Square" || source.Color == null ? Season.Basic.Colors.White : ((Season.Basic.Color)source.Color))
        };
        Controls.Add(image);

        var title = new Texts()
        {
            Content = source.Title,
            Scale = Vector2.One * 0.9f * resultScale,
            Color = new Season.Basic.Color(65, 105, 225, 255),
            ShowDot = true
        };
        Controls.Add(title);

        var desc = new Texts()
        {
            Content = source.Desc,
            Scale = Vector2.One * 0.7f * resultScale,
            Color = Season.Basic.Colors.Gray,
            ShowDot = true
        };
        Controls.Add(desc);

        var remove = new Sprite2D()
        {
            Name = "Buttons.png",
            Ext = ".png",
            Color = Season.Basic.Colors.White,
            SourceWidth = 1 / 15f, // 0.4f;
            SourceHeight = 1 / 17f, // 0.3f;
            Width = 70,
            Height = 70,
            OnClick = () =>
            {
                var index = results.IndexOf(source);
                results.Remove(source);
                resultsImages.RemoveAt(index);
                resultsTitles.RemoveAt(index);
                resultsDescs.RemoveAt(index);
                resultsRemoves.RemoveAt(index);
            }
        };
        Controls.Add(remove);

        if (insert)
        {
            resultsImages.Insert(0, image);

            resultsTitles.Insert(0, title);

            resultsDescs.Insert(0, desc);

            resultsRemoves.Insert(0, remove);
        }
        else
        {
            resultsImages.Add(image);

            resultsTitles.Add(title);

            resultsDescs.Add(desc);

            resultsRemoves.Add(remove);
        }
    }

    public void SetMode(string mode)
    {
        var color = mode is "Dark" ? Season.Basic.Colors.Gray : Season.Basic.Colors.White;

        for (var i = 0; i < SourcesView.Count; i++)
        {
            if (mode is "Dark")
            {
                sourcesTitles[i].Color = Season.Basic.Colors.White;
                sourcesDescs[i].Color = Season.Basic.Colors.White;
            }
            else
            {
                sourcesTitles[i].Color = Season.Basic.Colors.DarkSlateGray;
                sourcesDescs[i].Color = Season.Basic.Colors.Gray;
            }
        }

        for (var i = 0; i < Results.Count; i++)
        {
            if (mode is "Dark")
            {
                resultsTitles[i].Color = Season.Basic.Colors.White;
                resultsDescs[i].Color = Season.Basic.Colors.White;

            }
            else
            {
                resultsTitles[i].Color = Season.Basic.Colors.DarkSlateGray;
                resultsDescs[i].Color = Season.Basic.Colors.Gray;
            }
        }

        if (DeviceServices.BaseApp.Mode is "Dark")
        {
            var lightBlack = Season.Basic.Colors.LightBlack;

            ground.Color = lightBlack; // Season.Basic.Colors.Black

            border.Color = Season.Basic.Colors.Gray;

            blockSources1.Color = lightBlack;

            blockSources2.Color = lightBlack;

            gradualSources1.Color = lightBlack;

            gradualSources2.Color = lightBlack;

            blockResults1.Color = lightBlack;

            blockResults2.Color = lightBlack;

            gradualResults1.Color = lightBlack;

            gradualResults2.Color = lightBlack;

            movePanelSources.Color = Season.Basic.Colors.White;

            movePanelResults.Color = Season.Basic.Colors.White;

            desc.Color = Season.Basic.Colors.Yellow;
        }
        else
        {
            ground.Color = Season.Basic.Colors.White;

            border.Color = Season.Basic.Colors.Gray;

            blockSources1.Color = Season.Basic.Colors.White;

            blockSources2.Color = Season.Basic.Colors.White;

            gradualSources1.Color = Season.Basic.Colors.White;

            gradualSources2.Color = Season.Basic.Colors.White;

            blockResults1.Color = Season.Basic.Colors.White;

            blockResults2.Color = Season.Basic.Colors.White;

            gradualResults1.Color = Season.Basic.Colors.White;

            gradualResults2.Color = Season.Basic.Colors.White;

            movePanelSources.Color = Season.Basic.Colors.DarkSlateGray;

            movePanelResults.Color = Season.Basic.Colors.DarkSlateGray;

            desc.Color = Season.Basic.Colors.DarkRed;
        }
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        Time += time;

        if (Time <= 0.5f)
        {
            Alpha = Time / 0.5f;
        }
        else
        {
            Alpha = 1f;
        }

        if (TouchService.IsReleased && !ground.MouseOver)
        {
            TouchService.IsReleased = false;

            OnClose?.Invoke();

            return true;
        }

        mask.Update(time, alpha: 0.5f * Alpha, width: (int)DeviceServices.BaseApp.ExtendResolution.X, height: (int)DeviceServices.BaseApp.ExtendResolution.Y);

        ground.Alpha = Alpha;
        ground.Width = 700;
        ground.Height = 700;
        ground.PosX = (int)(DeviceServices.BaseApp.ExtendResolution.X - ground.Width) / 2;
        ground.PosY = (int)(DeviceServices.BaseApp.ExtendResolution.Y - ground.Height) / 2;
        ground.Update(time);

        border.Alpha = Alpha;
        border.Width = 700 + 2;
        border.Height = 700 + 2;
        border.PosX = ground.PosX - 1;
        border.PosY = ground.PosY - 1;
        border.Update(time);

        search.Alpha = Alpha;
        search.Width = 100;
        search.PosX = (int)(ground.PosX + ground.Width) - 200;
        search.PosY = (int)ground.PosY + 30;
        search.Update(time);

        query.Alpha = Alpha;

        if (query.MouseOver)
        {
            query.SourceX = 13 / 15f;
            query.SourceY = 0 / 17f;
        }
        else
        {
            query.SourceX = 12 / 15f;
            query.SourceY = 0 / 17f;
        }

        query.Alpha = Alpha;
        query.SourceX = 0 / 15f;
        query.SourceY = 11 / 17f;
        query.PosX = search.PosX + 120;
        query.PosY = search.PosY - 10;
        query.Width = 60;
        query.Height = 60;
        query.Update(time);

        desc.Content = Desc;
        desc.Alpha = Alpha;
        desc.PosX = ground.PosX + ((ground.Width ?? 0) - (desc.Width ?? 0)) / 2;
        desc.PosY = ground.PosY + (ground.Height ?? 0) - (desc.Height ?? 0) - 10;
        desc.Update(time);

        movePanelSources.Alpha = Alpha;
        movePanelSources.PosX = (int)ground.PosX;
        movePanelSources.PosY = (int)ground.PosY + 20;
        movePanelSources.Width = (int)(ground.Width / 2);
        movePanelSources.Height = (int)(ground.Height - 40);
        movePanelSources.SizeX = movePanelSources.Width ?? 0;
        movePanelSources.SizeY = (SourcesView.Count > 0 ? sourcesImages[SourcesView.Count - 1].PosY - sourcesImages[0].PosY : 0) + 150;
        if (movePanelSources.SizeY < movePanelSources.Height)
        {
            movePanelSources.SizeY = movePanelSources.Height ?? 0;
        }
        movePanelSources.Update(time);

        for (var i = 0; i < SourcesView.Count; i++)
        {
            var source = SourcesView[i];

            var posY0 = (int)movePanelSources.PosY + 30 + 100 * i - (int)movePanelSources.Scroll;

            if (source.Image.IsNullOrWhiteSpace() && source.Color == null)
            {
                sourcesImages[i].Alpha = 0f;
            }
            else
            {
                sourcesImages[i].Alpha = Alpha;
            }
            sourcesImages[i].PosX = ground.PosX + 10;
            sourcesImages[i].PosY = posY0;
            sourcesImages[i].Width = 70;
            sourcesImages[i].Height = 70;

            sourcesTitles[i].Alpha = Alpha;
            sourcesTitles[i].PosX = sourcesImages[i].PosX + 80;
            sourcesTitles[i].PosY = sourcesImages[i].PosY;
            sourcesTitles[i].WidthRequest = (int)movePanelSources.Width - 10 - 80 - 20;
            sourcesTitles[i].HeightRequest = sourcesTitles[i].LineHeight;

            sourcesDescs[i].Alpha = Alpha;
            sourcesDescs[i].PosX = sourcesTitles[i].PosX;
            sourcesDescs[i].PosY = sourcesTitles[i].PosY + 40;
            sourcesDescs[i].WidthRequest = sourcesTitles[i].WidthRequest;
            sourcesDescs[i].HeightRequest = sourcesDescs[i].LineHeight;

            if (posY0 + 70 < movePanelSources.PosY || posY0 > movePanelSources.PosY + movePanelSources.Height)
            {
                sourcesImages[i].Alpha = 0f;
                sourcesTitles[i].Alpha = 0f;
                sourcesDescs[i].Alpha = 0f;
            }
            else
            {
                if (sourcesImages[i].Update(time))
                {
                    //return;
                }

                if (sourcesTitles[i].Update(time))
                {
                    //return;
                }

                if (sourcesDescs[i].Update(time))
                {
                    //return;
                }
            }
        }

        blockSources1.Width = (int)movePanelSources.Width;
        blockSources1.Height = 20;
        blockSources1.PosX = (int)movePanelSources.PosX;
        blockSources1.PosY = (int)movePanelSources.PosY - 20;
        blockSources1.Alpha = Alpha;
        blockSources1.Update(time);

        gradualSources1.Width = (int)movePanelSources.Width;
        gradualSources1.Height = 70;
        gradualSources1.PosX = (int)movePanelSources.PosX;
        gradualSources1.PosY = (int)movePanelSources.PosY;
        gradualSources1.FlipY = true;
        gradualSources1.Alpha = Alpha;
        //gradualSources1.Color = Season.Basic.Colors.White
        gradualSources1.Update(time);

        blockSources2.Width = (int)movePanelSources.Width;
        blockSources2.Height = blockSources1.Height;
        blockSources2.PosX = (int)movePanelSources.PosX;
        blockSources2.PosY = (int)(ground.PosY + ground.Height - blockSources2.Height);
        blockSources2.Alpha = Alpha;
        blockSources2.Update(time);

        gradualSources2.Width = (int)movePanelSources.Width;
        gradualSources2.Height = 70;
        gradualSources2.PosX = (int)movePanelSources.PosX;
        gradualSources2.PosY = blockSources2.PosY - (float)gradualSources2.Height;
        gradualSources2.FlipY = false;
        gradualSources2.Alpha = Alpha;
        //gradualSources2.Color = Season.Basic.Colors.White
        gradualSources2.Update(time);

        for (var i = 0; i < Results.Count; i++)
        {
            var result0 = Results[i];

            var posY0 = (int)(movePanelResults.PosY + 30 * resultScale + 100 * resultScale * i - movePanelResults.Scroll);

            if (result0.Image.IsNullOrWhiteSpace() && result0.Color == null)
            {
                resultsImages[i].Alpha = 0f;
            }
            else
            {
                resultsImages[i].Alpha = Alpha;
            }
            resultsImages[i].PosX = ground.PosX + (float)ground.Width / 2 + 15;
            resultsImages[i].PosY = posY0;
            resultsImages[i].Width = (int)(70 * resultScale);
            resultsImages[i].Height = (int)(70 * resultScale);

            resultsTitles[i].Alpha = Alpha;
            resultsTitles[i].PosX = resultsImages[i].PosX + (int)(80 * resultScale);
            resultsTitles[i].PosY = resultsImages[i].PosY;
            resultsTitles[i].WidthRequest = (int)movePanelResults.Width - 10 - 120 - 10;
            resultsTitles[i].HeightRequest = resultsTitles[i].LineHeight;

            resultsDescs[i].Alpha = Alpha;
            resultsDescs[i].PosX = resultsTitles[i].PosX;
            resultsDescs[i].PosY = resultsTitles[i].PosY + (int)(40 * resultScale);
            resultsDescs[i].WidthRequest = resultsTitles[i].WidthRequest;
            resultsDescs[i].HeightRequest = resultsDescs[i].LineHeight;

            if (resultsRemoves[i].MouseOver)
            {
                resultsRemoves[i].SourceX = 13 / 15f;
                resultsRemoves[i].SourceY = 10 / 17f;
            }
            else
            {
                resultsRemoves[i].SourceX = 12 / 15f;
                resultsRemoves[i].SourceY = 10 / 17f;
            }

            resultsRemoves[i].Alpha = Alpha;
            resultsRemoves[i].PosX = resultsImages[i].PosX + (float)ground.Width / 2 - 75;
            resultsRemoves[i].PosY = resultsImages[i].PosY;
            resultsRemoves[i].Width = 50;
            resultsRemoves[i].Height = 50;

            if (posY0 + 70 < movePanelResults.PosY || posY0 > movePanelResults.PosY + movePanelResults.Height)
            {
                resultsImages[i].Alpha = 0f;
                resultsTitles[i].Alpha = 0f;
                resultsDescs[i].Alpha = 0f;
                resultsRemoves[i].Alpha = 0f;
            }
            else
            {
                resultsImages[i].Update(time);

                resultsTitles[i].Update(time);

                resultsDescs[i].Update(time);

                resultsRemoves[i].Update(time);
            }
        }

        blockResults1.Width = (int)movePanelResults.Width;
        blockResults1.Height = 20;
        blockResults1.PosX = (int)movePanelResults.PosX;
        blockResults1.PosY = (int)movePanelResults.PosY - 20;
        blockResults1.Alpha = Alpha;
        blockResults1.Update(time);

        gradualResults1.Width = (int)movePanelResults.Width;
        gradualResults1.Height = 70;
        gradualResults1.PosX = (int)movePanelResults.PosX;
        gradualResults1.PosY = (int)movePanelResults.PosY;
        gradualResults1.FlipY = true;
        gradualResults1.Alpha = Alpha;
        gradualResults1.Update(time);

        blockResults2.Width = (int)movePanelResults.Width;
        blockResults2.Height = blockSources1.Height;
        blockResults2.PosX = (int)movePanelResults.PosX;
        blockResults2.PosY = (int)(ground.PosY + ground.Height - blockResults2.Height);
        blockResults2.Alpha = Alpha;
        blockResults2.Update(time);

        gradualResults2.Width = (int)movePanelResults.Width;
        gradualResults2.Height = 70;
        gradualResults2.PosX = (int)movePanelResults.PosX;
        gradualResults2.PosY = blockResults2.PosY - (float)gradualResults2.Height;
        gradualResults2.FlipY = false;
        gradualResults2.Alpha = Alpha;
        gradualResults2.Update(time);

        movePanelResults.Alpha = Alpha;
        movePanelResults.PosX = (int)(ground.PosX + ground.Width / 2) + 5;
        movePanelResults.PosY = (int)ground.PosY + 120 + 20;
        movePanelResults.Width = (int)(ground.Width / 2 - 5);
        movePanelResults.Height = (int)(ground.Height - 120 - 20);
        movePanelResults.SizeX = movePanelResults.Width ?? 0;
        if (resultsImages.Count == 0)
        {

        }
        else
        {
            movePanelResults.SizeY = resultsImages[Results.Count - 1].PosY - resultsImages[0].PosY + 150;
        }
        if (movePanelResults.SizeY < movePanelResults.Height)
        {
            movePanelResults.SizeY = movePanelResults.Height ?? 0;
        }
        movePanelResults.Update(time);

        SetMode(DeviceServices.BaseApp.Mode);

        return result;
    }
}
