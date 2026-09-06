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

    Input inputMode, inputMovement, inputFov, inputStep, inputShadowOffset, inputShadowSoftness, inputShadowContact, inputNormalVariance, inputLog;

    Texts textsMode, textsMovement, textsFov, textsStep, textsShadowOffset, textsShadowSoftness, textsShadowContact, textsNormalVariance, textsLog;

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

        // 1-5 clause 13: the normal-offset is the one member of the shadow bias trio that is read from the UBO every frame
        // rather than baked into the shadow PSO, so it is the only one that can honestly be offered as a runtime knob here.
        // ShadowDepthBias and ShadowSlopeScaledDepthBias are deliberately absent: D3D12 and Vulkan fix them at pipeline
        // creation, so a picker for them would appear to work and change nothing until the next launch.
        textsShadowOffset = new Texts()
        {
            Content = "Shadow offset",
            Color = Season.Basic.Colors.Black,
            Scale = Vector2.One * 1f
        };
        AddControl(textsShadowOffset);

        // 1-5 clause 14: radius of the rotated PCF disk, also read from the UBO every frame, so it belongs here for the same
        // reason the offset above does. Unlike the offset this one is visible at a glance - it is the width of the soft edge
        // itself rather than a sub-texel correction - which makes it the row to sweep when judging whether the rotation is
        // resolving into smooth penumbra or leaving visible noise.
        textsShadowSoftness = new Texts()
        {
            Content = "Shadow softness",
            Color = Season.Basic.Colors.Black,
            Scale = Vector2.One * 1f
        };
        AddControl(textsShadowSoftness);

        // 1-5 clause 15: contact hardening, which redefines what the row above means rather than adding to it - softness stops
        // being the edge width everywhere and becomes the width a well separated occluder reaches, with contact edges sharp.
        // The two therefore have to be judged as a pair, which is the reason they sit adjacent. Also read from the UBO every
        // frame, through the sign of the same field, so it is honestly live like the other two shadow rows.
        textsShadowContact = new Texts()
        {
            Content = "Contact harden",
            Color = Season.Basic.Colors.Black,
            Scale = Vector2.One * 1f
        };
        AddControl(textsShadowContact);

        // 2-6 clause 5: Toksvig normal-map variance. Unlike the shadow rows above this one is not read from a UBO - the
        // measurement lives in the alpha channel of the normal map's mip levels - so switching it here re-runs the chains
        // through Graphics.RebuildNormalVarianceTextures. It exists as a row because the effect is narrow enough that
        // judging it needs an A/B in one session rather than two launches and two screenshots.
        textsNormalVariance = new Texts()
        {
            Content = "Normal variance",
            Color = Season.Basic.Colors.Black,
            Scale = Vector2.One * 1f
        };
        AddControl(textsNormalVariance);

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

                current = inputStep;

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

        inputShadowOffset = new Input()
        {
            WidthMin = WidthMin,
            Abbreviate = true,
            OnAction = async () =>
            {
                // Values are in shadow-map texels. 0 turns the offset off entirely, which is what the shader's own branch
                // tests, and is the reference image showing what the two depth biases achieve alone. 1.5 reaches the outer
                // ring of the current 3x3 PCF footprint and is the standing default; the entries above it exist because the
                // right number is whatever just removes acne on a grazing-lit surface without detaching a contact shadow.
                var sources = new List<Season.Entities.EData>();

                foreach (var texels in new[] { "0", "0.5", "1", "1.5", "2", "3", "4" })
                {
                    sources.Add(new Season.Entities.EData()
                    {
                        Key = texels,
                        Title = texels == "0" ? "0 (off)" : texels + " texel"
                    });
                }

                current = inputShadowOffset;

                var result = new List<Season.Entities.EData> { };

                simplePicker = new Season.Panels.SimplePicker(sources, result)
                {
                    OnSelect = () =>
                    {
                        var picked = simplePicker.Results?.Count > 0 ? simplePicker.Results[0] : null;

                        if (picked != null)
                        {
                            // Invariant culture because the keys are authored with a dot, and the value reaches the UBO on the
                            // next Apply with no rebuild: RenderQuality.Current is Settings.RenderQuality itself.
                            RenderQuality.Current.ShadowNormalOffset =
                                float.Parse(picked.Key, System.Globalization.CultureInfo.InvariantCulture);
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
        AddPanel(inputShadowOffset);

        inputShadowSoftness = new Input()
        {
            WidthMin = WidthMin,
            Abbreviate = true,
            OnAction = async () =>
            {
                // Values are the disk radius in shadow-map texels. 0 collapses all eight taps onto one point, which is a
                // single hardware 2x2 comparison fetch and therefore the hard-edge reference. 1 reproduces the reach of the
                // 3x3 kernel this replaced, so it is the like-for-like comparison against the old look. Above that the edge
                // keeps widening on the same eight taps, so the useful ceiling is wherever the rotation stops resolving and
                // the penumbra starts to crawl - which is the thing this row exists to find.
                var sources = new List<Season.Entities.EData>();

                foreach (var texels in new[] { "0", "1", "2", "3", "4", "6", "8" })
                {
                    sources.Add(new Season.Entities.EData()
                    {
                        Key = texels,
                        Title = texels == "0" ? "0 (hard)" : texels + " texel"
                    });
                }

                current = inputShadowSoftness;

                var result = new List<Season.Entities.EData> { };

                simplePicker = new Season.Panels.SimplePicker(sources, result)
                {
                    OnSelect = () =>
                    {
                        var picked = simplePicker.Results?.Count > 0 ? simplePicker.Results[0] : null;

                        if (picked != null)
                        {
                            RenderQuality.Current.ShadowSoftnessTexels =
                                float.Parse(picked.Key, System.Globalization.CultureInfo.InvariantCulture);
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
        AddPanel(inputShadowSoftness);

        inputShadowContact = new Input()
        {
            WidthMin = WidthMin,
            Abbreviate = true,
            OnAction = async () =>
            {
                // Off is the clause 14 behaviour: one constant radius everywhere. On makes the radius per pixel, so the same
                // Shadow softness number now describes only the widest the edge is allowed to get. Expect the scene to look
                // sharper overall when switching it on, which is why the softness above usually wants raising afterwards.
                var sources = new List<Season.Entities.EData>
                {
                    new Season.Entities.EData() { Key = "0", Title = "Off (constant)" },
                    new Season.Entities.EData() { Key = "1", Title = "On (per pixel)" }
                };

                current = inputShadowContact;

                var result = new List<Season.Entities.EData> { };

                simplePicker = new Season.Panels.SimplePicker(sources, result)
                {
                    OnSelect = () =>
                    {
                        var picked = simplePicker.Results?.Count > 0 ? simplePicker.Results[0] : null;

                        if (picked != null)
                        {
                            RenderQuality.Current.ShadowContactHardening = picked.Key == "1";
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
        AddPanel(inputShadowContact);

        inputNormalVariance = new Input()
        {
            WidthMin = WidthMin,
            Abbreviate = true,
            OnAction = async () =>
            {
                var sources = new List<Season.Entities.EData>
                {
                    new Season.Entities.EData() { Key = "0", Title = "Off (neutral alpha)" },
                    new Season.Entities.EData() { Key = "1", Title = "On (Toksvig)" }
                };

                current = inputNormalVariance;

                var result = new List<Season.Entities.EData> { };

                simplePicker = new Season.Panels.SimplePicker(sources, result)
                {
                    OnSelect = () =>
                    {
                        var picked = simplePicker.Results?.Count > 0 ? simplePicker.Results[0] : null;

                        if (picked != null)
                        {
                            RenderQuality.Current.TextureNormalVariance = picked.Key == "1";

                            // The switch is only read while a mip chain is being built, so setting it changes nothing on its
                            // own. Rebuilding blocks on a GPU fence per texture, which is why it happens once here on
                            // selection instead of being checked per frame.
                            var rebuilt = Season.Basic.Graphics.Instance?.RebuildNormalVarianceTextures() ?? 0;

                            App.Instance.AddLog(LogType.Backend,
                                $"{DateTime.UtcNow} [Setting] TextureNormalVariance={RenderQuality.Current.TextureNormalVariance}, " +
                                $"normal maps rebuilt={rebuilt}");
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
        AddPanel(inputNormalVariance);

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
        textsShadowOffset.Update(time, posX: PosX + padding, posY: textsStep.PosY + paddingH);
        textsShadowSoftness.Update(time, posX: PosX + padding, posY: textsShadowOffset.PosY + paddingH);
        textsShadowContact.Update(time, posX: PosX + padding, posY: textsShadowSoftness.PosY + paddingH);
        textsNormalVariance.Update(time, posX: PosX + padding, posY: textsShadowContact.PosY + paddingH);
        textsLog.Update(time, posX: PosX + padding, posY: textsNormalVariance.PosY + paddingH);

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
        inputShadowOffset.Text = RenderQuality.Current.ShadowNormalOffset.ToString("0.##",
            System.Globalization.CultureInfo.InvariantCulture) + " texel";
        inputShadowOffset.Color = inputShadowOffset.MouseOver ? Season.Basic.Colors.Red : Season.Basic.Colors.Black;
        if (inputShadowOffset.Update(time, posX: (int)textsShadowOffset.PosX + inputLeft, posY: (int)textsShadowOffset.PosY, width: width0, height: height0))
        {
            result = true;
        }
        inputShadowSoftness.Text = RenderQuality.Current.ShadowSoftnessTexels.ToString("0.##",
            System.Globalization.CultureInfo.InvariantCulture) + " texel";
        inputShadowSoftness.Color = inputShadowSoftness.MouseOver ? Season.Basic.Colors.Red : Season.Basic.Colors.Black;
        if (inputShadowSoftness.Update(time, posX: (int)textsShadowSoftness.PosX + inputLeft, posY: (int)textsShadowSoftness.PosY, width: width0, height: height0))
        {
            result = true;
        }
        inputShadowContact.Text = RenderQuality.Current.ShadowContactHardening ? "On" : "Off";
        inputShadowContact.Color = inputShadowContact.MouseOver ? Season.Basic.Colors.Red : Season.Basic.Colors.Black;
        if (inputShadowContact.Update(time, posX: (int)textsShadowContact.PosX + inputLeft, posY: (int)textsShadowContact.PosY, width: width0, height: height0))
        {
            result = true;
        }
        inputNormalVariance.Text = RenderQuality.Current.TextureNormalVariance ? "On" : "Off";
        inputNormalVariance.Color = inputNormalVariance.MouseOver ? Season.Basic.Colors.Red : Season.Basic.Colors.Black;
        if (inputNormalVariance.Update(time, posX: (int)textsNormalVariance.PosX + inputLeft, posY: (int)textsNormalVariance.PosY, width: width0, height: height0))
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
