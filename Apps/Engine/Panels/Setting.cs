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

    Texts textsMode, textsMovement, textsFov, textsDayNightSpeed, textsStartHour, textsStep, textsAntiAliasing, textsGi, textsShadows, textsAo, textsShadowOffset, textsShadowSoftness, textsShadowContact, textsNormalVariance, textsLog;

    Input inputMode, inputMovement, inputFov, inputDayNightSpeed, inputStartHour, inputStep, inputAntiAliasing, inputGi, inputShadows, inputAo, inputShadowOffset, inputShadowSoftness, inputShadowContact, inputNormalVariance, inputLog;

    BaseControl current = null;

    const int WidthMin = 300;

    // The AA tier that is actually in force for this process, captured the first time the panel is built. Static rather than
    // per-instance because the panel is constructed fresh on every open (see App.setting.OnClick): an instance field would be
    // re-read after the user had already picked a new tier, and the pending marker in the value box would vanish on reopen.
    //
    // Reading it here is enough to be the session truth, and no earlier hook is needed: the backends resolve the tier during
    // graphics initialization and write the downgrade back into this same property, so by the time any panel exists the value
    // is final. That write-back is also why this snapshot is the only way to tell "running TAA" from "asked for TAA" - the
    // authored choice and the effective one share one field.
    static Season.Rendering.AaMode _sessionAntiAliasing;
    static bool _sessionAntiAliasingSeeded;

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

        textsDayNightSpeed = new Texts()
        {
            Content = "DayNightSpeed",
            Color = Season.Basic.Colors.Black,
            Scale = Vector2.One * 1f
        };
        AddControl(textsDayNightSpeed);

        // Hour of day rather than a phase, because the phase counts whole day cycles and its integer part drives the lunar
        // cycle, so it is not a quantity anyone can read as a time. The unit lives in the label: the value box is fed back
        // to the keyboard as its initial text, so it has to stay a bare number.
        textsStartHour = new Texts()
        {
            Content = "Start hour",
            Color = Season.Basic.Colors.Black,
            Scale = Vector2.One * 1f
        };
        AddControl(textsStartHour);

        textsStep = new Texts()
        {
            Content = "Step",
            Color = Season.Basic.Colors.Black,
            Scale = Vector2.One * 1f
        };
        AddControl(textsStep);

        if (!_sessionAntiAliasingSeeded)
        {
            _sessionAntiAliasingSeeded = true;
            _sessionAntiAliasing = RenderQuality.Current.AntiAliasing;
        }

        // 2-1 clause 5. The one row here that cannot be live, and it is not close: every arm of AaMode is decided while
        // resources are being built. Msaa4x fixes the sample count of every render target and PSO, Fxaa and Taa are what make
        // the backend register FrameSchedule.PostColor and the Post uber pass at all, and Taa additionally forces the
        // motion-vector target and bakes the VELOCITY_OUTPUT shader variant. So this row writes the tier and persists it, and
        // the value box marks itself with * until the process it applies to is the one running - see AaText below.
        textsAntiAliasing = new Texts()
        {
            Content = "Anti-alias",
            Color = Season.Basic.Colors.Black,
            Scale = Vector2.One * 1f
        };
        AddControl(textsAntiAliasing);

        // 2-4. Live in both directions, through the tier rather than around it: GlobalIllumination is read only at
        // initialization (Ddgi.Initialize and the DDGI_ENABLED shader define), so flipping it now cannot free the probe chain,
        // but CelestialLighting derives GiIntensity from it every frame and a zero intensity is what the consumer side treats
        // as "no DDGI". Off therefore removes the contribution on the next frame and keeps paying for the compute.
        textsGi = new Texts()
        {
            Content = "DDGI",
            Color = Season.Basic.Colors.Black,
            Scale = Vector2.One * 1f
        };
        AddControl(textsGi);

        // 1-5. Backed by ShadowStrength, not by the ShadowsEnabled tier, and that is deliberate. ShadowsEnabled is read every
        // frame - by RenderShadowPass and by SetLighting - but only as a guard that skips CascadedShadow.Apply, and Bake does
        // not clear the shadow tail of SceneLightParams (it is documented as reinjected from one point per frame). Clearing it
        // mid-run would therefore leave the last frame's matrices and atlas standing, so the scene would freeze its shadows
        // rather than lose them. Strength reaches ShadowParams1.Y every frame and 0 makes full occlusion cost no light, which
        // is the honest "off", at the price of still running the pass.
        textsShadows = new Texts()
        {
            Content = "CSM shadow",
            Color = Season.Basic.Colors.Black,
            Scale = Vector2.One * 1f
        };
        AddControl(textsShadows);

        // 2-2. AoIntensity is the live half, exactly as clause 5 describes it, and the AmbientOcclusion tier is written
        // alongside it because that one is safe to change late - nothing reads it after GtaoEffect.Initialize - and without it
        // a session that started with AO off could never be turned on at all, not even on the next launch.
        textsAo = new Texts()
        {
            Content = "GTAO",
            Color = Season.Basic.Colors.Black,
            Scale = Vector2.One * 1f
        };
        AddControl(textsAo);

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

        inputDayNightSpeed = new Input()
        {
            WidthMin = WidthMin,
            Abbreviate = true,
            OnAction = async () =>
            {
                var result = await DeviceServices.Dialog.ShowKeyboard("DayNightSpeed".Translate(), "", new string[] { "OK".Translate(), "Cancel".Translate() }, inputDayNightSpeed.Text);

                // Invariant culture so the box round-trips what Update printed into it: that text is formatted
                // invariantly, and parsing it back under a comma-decimal locale would read 0.005 as 5.
                if (result is not null && float.TryParse(result, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float speed))
                {
                    // Day cycles per second, so 0 holds the sky still. It goes through CelestialLighting rather than
                    // being assigned to Settings.World here, because the phase is integrated from this rate: a bare
                    // assignment would step the rate and snap every clock derived from the phase. SetDayNightSpeed eases
                    // the change, owns that invariant, and is also what persists the new value.
                    CelestialLighting.SetDayNightSpeed(speed);
                }
            }
        };
        AddPanel(inputDayNightSpeed);

        inputStartHour = new Input()
        {
            WidthMin = WidthMin,
            Abbreviate = true,
            OnAction = async () =>
            {
                var result = await DeviceServices.Dialog.ShowKeyboard("StartHour".Translate(), "", new string[] { "OK".Translate(), "Cancel".Translate() }, inputStartHour.Text);

                // Invariant culture for the same reason as the rate above: the box holds what Update printed there, and a
                // comma-decimal locale would read 6.5 as 65.
                if (result is not null && float.TryParse(result, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float hour))
                {
                    // Clock hour in 0~24. Setting it both persists the value and moves the running clock to it, which is
                    // why this goes through CelestialLighting rather than writing Settings.World here - the phase is the
                    // clock, and only CelestialLighting can move it without also rewinding the lunar cycle. Clamping to
                    // range happens there too, so every caller gets it rather than just this one.
                    CelestialLighting.SetStartHour(hour);
                }
            }
        };
        AddPanel(inputStartHour);

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

        inputAntiAliasing = new Input()
        {
            WidthMin = WidthMin,
            Abbreviate = true,
            OnAction = async () =>
            {
                // Keys are the AaMode member names so the parse below stays correct if the enum is reordered; the titles are
                // the spellings the modes are known by. Off is the honest name for the first arm - it is no AA at all, not a
                // cheaper one - and MSAA carries its sample count because that is the part that costs memory.
                var sources = new List<Season.Entities.EData>
                {
                    new Season.Entities.EData() { Key = "Off", Title = "Off (none)" },
                    new Season.Entities.EData() { Key = "Msaa4x", Title = "MSAA 4x" },
                    new Season.Entities.EData() { Key = "Fxaa", Title = "FXAA" },
                    new Season.Entities.EData() { Key = "Taa", Title = "TAA" }
                };

                current = inputAntiAliasing;

                var result = new List<Season.Entities.EData> { };

                simplePicker = new Season.Panels.SimplePicker(sources, result)
                {
                    OnSelect = () =>
                    {
                        var picked = simplePicker.Results?.Count > 0 ? simplePicker.Results[0] : null;

                        if (picked != null && Enum.TryParse<Season.Rendering.AaMode>(picked.Key, out var mode))
                        {
                            RenderQuality.Current.AntiAliasing = mode;

                            // The only row that is authored-for-next-launch rather than live, so it has to be persisted here
                            // or the choice would be lost before the launch it applies to. The log says so explicitly because
                            // the value box has room for a marker but not for a sentence.
                            DeviceServices.BaseApp?.RequestSaveSettings();

                            App.Instance.AddLog(LogType.Backend,
                                $"{DateTime.UtcNow} [Setting] AntiAliasing={mode} saved, in force this session={_sessionAntiAliasing}" +
                                (mode == _sessionAntiAliasing ? "" : " (restart to apply)"));
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
        AddPanel(inputAntiAliasing);

        inputGi = new Input()
        {
            WidthMin = WidthMin,
            Abbreviate = true,
            OnAction = async () =>
            {
                var sources = new List<Season.Entities.EData>
                {
                    new Season.Entities.EData() { Key = "0", Title = "Off (no indirect)" },
                    new Season.Entities.EData() { Key = "1", Title = "On (DDGI probes)" }
                };

                current = inputGi;

                var result = new List<Season.Entities.EData> { };

                simplePicker = new Season.Panels.SimplePicker(sources, result)
                {
                    OnSelect = () =>
                    {
                        var picked = simplePicker.Results?.Count > 0 ? simplePicker.Results[0] : null;

                        if (picked != null)
                        {
                            // Writing the tier alone is enough to be live, because CelestialLighting reads it every frame to
                            // derive GiIntensity - see the comment on the label. Turning it on mid-session cannot build the
                            // probe chain, though, so it only takes effect now if this session already initialized DDGI.
                            RenderQuality.Current.GlobalIllumination = picked.Key == "1"
                                ? Season.Rendering.GiMode.Ddgi
                                : Season.Rendering.GiMode.Off;

                            DeviceServices.BaseApp?.RequestSaveSettings();
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
        AddPanel(inputGi);

        inputShadows = new Input()
        {
            WidthMin = WidthMin,
            Abbreviate = true,
            OnAction = async () =>
            {
                var sources = new List<Season.Entities.EData>
                {
                    new Season.Entities.EData() { Key = "0", Title = "Off (unlit occluders)" },
                    new Season.Entities.EData() { Key = "1", Title = "On (cascaded)" }
                };

                current = inputShadows;

                var result = new List<Season.Entities.EData> { };

                simplePicker = new Season.Panels.SimplePicker(sources, result)
                {
                    OnSelect = () =>
                    {
                        var picked = simplePicker.Results?.Count > 0 ? simplePicker.Results[0] : null;

                        if (picked != null)
                        {
                            // Strength, not the ShadowsEnabled tier - see the label for why touching the tier mid-run would
                            // freeze the shadows instead of removing them. Restoring goes back to the default rather than to
                            // whatever was there before, since 0 is the only value this row can have written.
                            RenderQuality.Current.ShadowStrength = picked.Key == "1"
                                ? RenderQuality.DefaultShadowStrength
                                : 0f;

                            DeviceServices.BaseApp?.RequestSaveSettings();
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
        AddPanel(inputShadows);

        inputAo = new Input()
        {
            WidthMin = WidthMin,
            Abbreviate = true,
            OnAction = async () =>
            {
                var sources = new List<Season.Entities.EData>
                {
                    new Season.Entities.EData() { Key = "0", Title = "Off (no occlusion)" },
                    new Season.Entities.EData() { Key = "1", Title = "On (GTAO)" }
                };

                current = inputAo;

                var result = new List<Season.Entities.EData> { };

                simplePicker = new Season.Panels.SimplePicker(sources, result)
                {
                    OnSelect = () =>
                    {
                        var picked = simplePicker.Results?.Count > 0 ? simplePicker.Results[0] : null;

                        if (picked != null)
                        {
                            var on = picked.Key == "1";

                            // Both halves, for the two horizons this row has to serve: the intensity is what the composite
                            // reads every frame, so it is what makes the change visible now, and the tier is what decides
                            // whether the next launch builds the pass at all.
                            RenderQuality.Current.AmbientOcclusion = on
                                ? Season.Rendering.AoMode.Gtao
                                : Season.Rendering.AoMode.Off;
                            RenderQuality.Current.AoIntensity = on ? RenderQuality.DefaultAoIntensity : 0f;

                            DeviceServices.BaseApp?.RequestSaveSettings();
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
        AddPanel(inputAo);

        inputShadowOffset = new Input()
        {
            WidthMin = WidthMin,
            Abbreviate = true,
            OnAction = async () =>
            {
                // Values are in shadow-map texels. 0 turns the offset off entirely, which is what the shader's own branch
                // tests, and is the reference image showing what the two depth biases achieve alone. Everything above 0 is a
                // floor rather than the whole offset: the shader raises it to the reach of the clause 14 sample disk whenever
                // that is larger, so the low entries here differ from each other only while the disk is narrow. The entries
                // above the default exist because the right number is whatever just removes acne on a grazing-lit surface
                // without detaching a contact shadow.
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
        var width0 = 180; var inputLeft = 350; var height0 = 70;

        // Rows wrap into columns rather than running straight down. Fifteen of them at this pitch want about 1560px, and a
        // 1080p window leaves the panel 880, so a single column would simply hide everything past the shadow rows - the
        // failure would be silent, since a control positioned off the panel still updates and still answers hit tests.
        //
        // The column count is derived from the space rather than fixed, because the caller sizes this panel from the window
        // (see App.settingPanel.Update): one column on a tall window, three on a short one, with no size hardcoded here.
        const int rowCount = 15;
        var rowTop = (int)(title.PosY + padding + paddingH);
        var rowWidth = inputLeft + width0 + 100;
        var panelWidth = (int)(width ?? App.Instance.ExtendResolution.X - 200);
        var panelHeight = (int)(height ?? App.Instance.ExtendResolution.Y - 200);
        var usableW = panelWidth - padding * 2;
        var usableH = panelHeight - (rowTop - (int)PosY) - padding;

        var rowsPerColumn = Math.Max(1, usableH / paddingH);
        var columns = Math.Max(1, (rowCount + rowsPerColumn - 1) / rowsPerColumn);
        // Re-divide once the column count is known so the last column is not left holding a single row.
        rowsPerColumn = (rowCount + columns - 1) / columns;
        var columnStride = columns > 1
            ? rowWidth + Math.Clamp((usableW - columns * rowWidth) / (columns - 1), 0, 120)
            : 0;

        var row = 0;

        void Place(Texts label, Input input, string text)
        {
            var x = (int)PosX + padding + row / rowsPerColumn * columnStride;
            var y = rowTop + row % rowsPerColumn * paddingH;
            row++;

            label.Update(time, posX: x, posY: y);

            input.Text = text;
            input.Color = input.MouseOver ? Season.Basic.Colors.Red : Season.Basic.Colors.Black;
            if (input.Update(time, posX: x + inputLeft, posY: y, width: width0, height: height0))
            {
                result = true;
            }
        }

        Place(textsMode, inputMode, App.Instance.Mode.ToString());
        Place(textsMovement, inputMovement, App.Instance.Movement.ToString());
        Place(textsFov, inputFov, ((int)(App.Instance.Camera.FovY * 36f * 5 / MathF.PI)).ToString() + "°");
        // The authored rate, not the eased one, so the box does not scroll while a change ramps in. Printed as a bare
        // invariant number with no unit suffix, unlike the row above: this one is fed straight back to ShowKeyboard as
        // its initial value, so anything decorative here would have to be parsed off again on the way in.
        Place(textsDayNightSpeed, inputDayNightSpeed, CelestialLighting.DayNightSpeed.ToString("0.#####",
            System.Globalization.CultureInfo.InvariantCulture));
        // The authored start hour, not the hour the clock is currently at. Showing the live time would make this box the
        // one row that scrolls, and worse, re-confirming the keyboard would then set the start hour to whatever time it
        // happened to be - so the field would quietly rewrite itself just by being opened.
        Place(textsStartHour, inputStartHour, Season.Rendering.WorldSettings.Current.StartHour.ToString("0.##",
            System.Globalization.CultureInfo.InvariantCulture));
        Place(textsStep, inputStep, App.Instance.step.ToString());

        // The trailing * on the four rows below means "this is what will be used, but not what is running". Each of them
        // reads a different witness for that, because each is prevented by something different: the AA tier is compared
        // against the snapshot of what the backend resolved at startup, while the other three ask whether the resource
        // their feature needs actually exists this session - a null there is the one signal that survives the backends
        // silently declining a tier they cannot support.
        Place(textsAntiAliasing, inputAntiAliasing, AaText(RenderQuality.Current.AntiAliasing)
            + (RenderQuality.Current.AntiAliasing == _sessionAntiAliasing ? "" : " *"));
        Place(textsGi, inputGi, RenderQuality.Current.GlobalIllumination == Season.Rendering.GiMode.Ddgi
            ? Season.Rendering.Effects.DdgiEffect.Ready ? "On" : "On *"
            : "Off");
        Place(textsShadows, inputShadows, RenderQuality.Current.ShadowStrength > 0f
            ? Season.Rendering.FrameSchedule.ShadowMap != null ? "On" : "On *"
            : "Off");
        Place(textsAo, inputAo, RenderQuality.Current.AmbientOcclusion == Season.Rendering.AoMode.Gtao
            && RenderQuality.Current.AoIntensity > 0f
            ? Season.Rendering.FrameSchedule.AoTexture != null ? "On" : "On *"
            : "Off");

        Place(textsShadowOffset, inputShadowOffset, RenderQuality.Current.ShadowNormalOffset.ToString("0.##",
            System.Globalization.CultureInfo.InvariantCulture) + " texel");
        Place(textsShadowSoftness, inputShadowSoftness, RenderQuality.Current.ShadowSoftnessTexels.ToString("0.##",
            System.Globalization.CultureInfo.InvariantCulture) + " texel");
        Place(textsShadowContact, inputShadowContact, RenderQuality.Current.ShadowContactHardening ? "On" : "Off");
        Place(textsNormalVariance, inputNormalVariance, RenderQuality.Current.TextureNormalVariance ? "On" : "Off");
        Place(textsLog, inputLog, App.Instance.Logs.Count.ToString());

        return result;
    }

    // Display spellings for the AA tier. Kept apart from the picker's own titles because those carry a parenthetical that
    // explains the choice, which is fine in a list and too long for a 180px box.
    static string AaText(Season.Rendering.AaMode mode) => mode switch
    {
        Season.Rendering.AaMode.Off => "Off",
        Season.Rendering.AaMode.Msaa4x => "MSAA 4x",
        Season.Rendering.AaMode.Fxaa => "FXAA",
        _ => "TAA",
    };
}
