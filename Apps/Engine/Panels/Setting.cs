// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Engine.Panels;

internal class Setting : Panel
{
    internal Sprite2D sprite2D;

    internal Setting()
    {
        RenderDomain = Season.Controls.RenderDomain.Overlay;

        sprite2D = new Sprite2D()
        {
            Name = "Assets/Setting.png",
            OnClick = () =>
            {
                OnClick?.Invoke();
            }
        };
        AddControl(sprite2D);
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        int size = 80;

        var pos = new Vector2(size, size);

        sprite2D.Color = sprite2D.MouseOver ? Season.Basic.Colors.DarkRed : Season.Basic.Colors.White;
        if (sprite2D.Update(time, alpha: 1f, posX: pos.X, posY: pos.Y, width: size, height: size))
        {
            result = true;
        }

        return result;
    }
}

internal class SettingPanel : BoardPanel
{
    SimplePicker simplePicker;

    Texts title;

    Input inputMode, inputMovement, inputFov, inputStep, inputLog;

    Texts textsMode, textsMovement, textsFov, textsStep, textsLog;

    BaseControl current = null;

    const int WidthMin = 300;

    internal SettingPanel()
        : base()
    {
        FrameColor = Season.Basic.Colors.DarkSlateGray;

        title = new Texts()
        {
            Content = "Settings",
            Color = Season.Basic.Colors.DarkRed,
            Scale = Vector2.One * 1.2f
        };
        AddControl(title);

        textsMode = new Texts()
        {
            Content = "Mode",
            Color = Season.Basic.Colors.Black,
            Scale = Vector2.One * 1f
        };
        AddControl(textsMode);

        textsMovement = new Texts()
        {
            Content = "Movement",
            Color = Season.Basic.Colors.Black,
            Scale = Vector2.One * 1f
        };
        AddControl(textsMovement);

        textsFov = new Texts()
        {
            Content = "Fov",
            Color = Season.Basic.Colors.Black,
            Scale = Vector2.One * 1f
        };
        AddControl(textsFov);

        textsStep = new Texts()
        {
            Content = "Step",
            Color = Season.Basic.Colors.Black,
            Scale = Vector2.One * 1f
        };
        AddControl(textsStep);

        textsLog = new Texts()
        {
            Content = "Log",
            Color = Season.Basic.Colors.Black,
            Scale = Vector2.One * 1f
        };
        AddControl(textsLog);

        inputMode = new Input()
        {
            WidthMin = WidthMin,
            Abbreviate = true,
            OnAction = async () =>
            {
                var sources = new List<Season.Entities.EData>()
                {
                    new Season.Entities.EData()
                    {
                        Key = Mode.Show.ToString(),
                        Title = Mode.Show.ToString()
                    },
                    new Season.Entities.EData()
                    {
                        Key = Mode.Play.ToString(),
                        Title = Mode.Play.ToString()
                    },
                    new Season.Entities.EData()
                    {
                        Key = Mode.Edit.ToString(),
                        Title = Mode.Edit.ToString()
                    },
                    new Season.Entities.EData()
                    {
                        Key = Mode.Debug.ToString(),
                        Title = Mode.Debug.ToString()
                    }
                };

                current = inputMode;

                var result = new List<Season.Entities.EData> { };

                simplePicker = new Season.Panels.SimplePicker(sources, result)
                {
                    OnSelect = () =>
                    {
                        var picked = simplePicker.Results?.Count > 0 ? simplePicker.Results[0] : null;

                        if (picked != null)
                        {
                            App.Instance.Mode = (Mode)Enum.Parse(typeof(Mode), picked.Key);

                            if (App.Instance.Mode is Mode.Show)
                            {

                            }
                            else if (App.Instance.Mode is Mode.Play)
                            {
                                App.Instance.logo.sprite2D.SetTexture(@"Assets/favicon.png");
                            }
                            else if (App.Instance.Mode is Mode.Edit)
                            {
                                string name = null;

                                if (App.Instance.ViewType is ViewType.Ming)
                                {
                                    name = @"Assets/Ming.png";
                                }
                                else
                                {
                                    name = @"Assets/Grid.png";
                                }

                                App.Instance.logo.sprite2D.SetTexture(name);
                            }
                            else if (App.Instance.Mode is Mode.Debug)
                            {
                                if (App.Instance.views == null)
                                {
                                    App.Instance.views = new Views();
                                    App.Instance.AddPanel(App.Instance.views);
                                }
                            }
                            else
                            {
                                if (App.Instance.views != null)
                                {
                                    App.Instance.RemovePanel(App.Instance.views);
                                    App.Instance.views = null;
                                }
                            }
                        }

                        simplePicker.OnClose?.Invoke();
                    },
                    OnClose = () =>
                    {
                        RemovePanel(simplePicker);
                        simplePicker = null;
                    }
                };
                AddPanel(simplePicker);
            }
        };
        AddPanel(inputMode);

        inputMovement = new Input()
        {
            WidthMin = WidthMin,
            Abbreviate = true,
            OnAction = async () =>
            {
                var sources = new List<Season.Entities.EData>()
                {
                    new Season.Entities.EData()
                    {
                        Key = Movement.World.ToString(),
                        Title = Movement.World.ToString()
                    },
                    new Season.Entities.EData()
                    {
                        Key = Movement.Character.ToString(),
                        Title = Movement.Character.ToString()
                    }
                };

                current = inputMovement;

                var result = new List<Season.Entities.EData> { };

                simplePicker = new Season.Panels.SimplePicker(sources, result)
                {
                    OnSelect = () =>
                    {
                        var picked = simplePicker.Results?.Count > 0 ? simplePicker.Results[0] : null;

                        if (picked != null)
                        {
                            App.Instance.Movement = (Movement)Enum.Parse(typeof(Movement), picked.Key);
                        }

                        simplePicker.OnClose?.Invoke();
                    },
                    OnClose = () =>
                    {
                        RemovePanel(simplePicker);
                        simplePicker = null;
                    }
                };
                AddPanel(simplePicker);
            }
        };
        AddPanel(inputMovement);

        inputFov = new Input()
        {
            WidthMin = WidthMin,
            Abbreviate = true,
            OnAction = async () =>
            {
                //MathF.PI * 13 / 36f;

                var sources = new List<Season.Entities.EData>();

                for (var i = 3; i <= 24; i++)
                {
                    sources.Add(new Season.Entities.EData()
                    {
                        Key = i.ToString(),
                        Title = i * 5 + "°",
                        Image = null,
                        Desc = null
                    });
                }

                current = inputFov;

                var result = new List<Season.Entities.EData> { };

                simplePicker = new Season.Panels.SimplePicker(sources, result)
                {
                    OnSelect = () =>
                    {
                        var picked = simplePicker.Results?.Count > 0 ? simplePicker.Results[0] : null;

                        if (picked != null)
                        {
                            App.Instance.Camera.FovY = MathF.PI * int.Parse(picked.Key) / 36f;
                        }

                        simplePicker.OnClose?.Invoke();
                    },
                    OnClose = () =>
                    {
                        RemovePanel(simplePicker);
                        simplePicker = null;
                    }
                };
                AddPanel(simplePicker);
            }
        };
        AddPanel(inputFov);

        inputStep = new Input()
        {
            WidthMin = WidthMin,
            Abbreviate = true,
            OnAction = async () =>
            {
                var sources = new List<Season.Entities.EData>()
                {
                    new Season.Entities.EData()
                    {
                        Key = "0.1",
                        Title = "0.1"
                    },
                    new Season.Entities.EData()
                    {
                        Key = "0.2",
                        Title = "0.2"
                    },
                    new Season.Entities.EData()
                    {
                        Key = "1",
                        Title = "1"
                    },
                    new Season.Entities.EData()
                    {
                        Key = "5",
                        Title = "5"
                    },
                    new Season.Entities.EData()
                    {
                        Key = "10",
                        Title = "10"
                    }
                };

                current = inputFov;

                var result = new List<Season.Entities.EData> { };

                simplePicker = new Season.Panels.SimplePicker(sources, result)
                {
                    OnSelect = () =>
                    {
                        var picked = simplePicker.Results?.Count > 0 ? simplePicker.Results[0] : null;

                        if (picked != null)
                        {
                            App.Instance.step = float.Parse(picked.Key);
                        }

                        simplePicker.OnClose?.Invoke();
                    },
                    OnClose = () =>
                    {
                        RemovePanel(simplePicker);
                        simplePicker = null;
                    }
                };
                AddPanel(simplePicker);
            }
        };
        AddPanel(inputStep);

        inputLog = new Input()
        {
            WidthMin = WidthMin,
            Abbreviate = true,
            OnAction = async () =>
            {
                var logs = String.Join("\r\n", App.Instance.Logs);
                var bytes = Encoding.UTF8.GetBytes(logs);

                DeviceServices.Download.DownloadSave("", $"{DateTime.Now.ToDateTimeTicks()}.txt", bytes, true);

                await Task.CompletedTask;
            }
        };
        AddPanel(inputLog);
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        if (simplePicker != null)
        {
            if (simplePicker.Update(time, alpha: alpha, posX: (int)current.PosX, posY: (int)current.PosY + 50))
            {
                return true;
            }
        }

        var padding = 30;

        title.Update(time, posX: PosX + padding, posY: PosY + padding);

        var paddingH = 100;
        textsMode.Update(time, posX: PosX + padding, posY: title.PosY + padding + paddingH);
        textsMovement.Update(time, posX: PosX + padding, posY: textsMode.PosY + paddingH);
        textsFov.Update(time, posX: PosX + padding, posY: textsMovement.PosY + paddingH);
        textsStep.Update(time, posX: PosX + padding, posY: textsFov.PosY + paddingH);
        textsLog.Update(time, posX: PosX + padding, posY: textsStep.PosY + paddingH);

        var width0 = 180; var inputLeft = 200; var height0 = 70;
        inputMode.Text = App.Instance.Mode.ToString();
        inputMode.Color = inputMode.MouseOver ? Season.Basic.Colors.Red : Season.Basic.Colors.Black;
        if (inputMode.Update(time, posX: (int)textsMode.PosX + inputLeft, posY: (int)textsMode.PosY, width: width0, height: height0))
        {
            result = true;
        }
        inputMovement.Text = App.Instance.Movement.ToString();
        inputMovement.Color = inputMovement.MouseOver ? Season.Basic.Colors.Red : Season.Basic.Colors.Black;
        if (inputMovement.Update(time, posX: (int)textsMovement.PosX + inputLeft, posY: (int)textsMovement.PosY, width: width0, height: height0))
        {
            result = true;
        }
        inputFov.Text = ((int)(App.Instance.Camera.FovY * 36f * 5 / MathF.PI)).ToString() + "°";
        inputFov.Color = inputFov.MouseOver ? Season.Basic.Colors.Red : Season.Basic.Colors.Black;
        if (inputFov.Update(time, posX: (int)textsFov.PosX + inputLeft, posY: (int)textsFov.PosY, width: width0, height: height0))
        {
            result = true;
        }
        inputStep.Text = App.Instance.step.ToString();
        inputStep.Color = inputStep.MouseOver ? Season.Basic.Colors.Red : Season.Basic.Colors.Black;
        if (inputStep.Update(time, posX: (int)textsStep.PosX + inputLeft, posY: (int)textsStep.PosY, width: width0, height: height0))
        {
            result = true;
        }
        inputLog.Text = App.Instance.Logs.Count.ToString();
        inputLog.Color = inputLog.MouseOver ? Season.Basic.Colors.Red : Season.Basic.Colors.Black;
        if (inputLog.Update(time, posX: (int)textsLog.PosX + inputLeft, posY: (int)textsLog.PosY, width: width0, height: height0))
        {
            result = true;
        }

        return result;
    }
}
