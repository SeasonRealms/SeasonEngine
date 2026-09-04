// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Engine;

internal enum Mode
{
    Show,
    Play,
    Edit,
    Debug
}

internal enum ViewType
{
    Ming,
    Grid
}

internal class App : BaseApp
{
    internal static App Instance => DeviceServices.BaseApp as App;

    internal Mode Mode = Mode.Play;

    internal ViewType ViewType = ViewType.Ming;

    internal float step = 0.1f;

    Sky sky;

    // Central celestial-lighting driver split out of Sky in 2026-08. It owns all lighting work:
    // light sources, environment lighting, weather, and SH9 updates.
    // Update must run it before sky because skybox tinting and marker visibility read its per-frame cache.
    CelestialLighting celestial;

    internal Ground ground;

    // Procedural sea: a 1400x1400m noisy water surface around the land.
    // Sea.SeaLevel is runtime-adjustable and defaults to y=-3.
    // Together with the widened horizon and the mountain ring's east-west corridor,
    // this lets the sun and moon rise from the sea and set at the water-sky horizon.
    Sea sea;

    // Land-sea transition beach: small procedural 3D tiles with a top-to-bottom slope and dune noise.
    // They are instantiated only inside the east and west openings of the mountain ring,
    // and all faces share one procedural noisy sand texture. See Beach.cs.
    Beach beach;

    // Coastal rocks: extract 15 rock meshes from Rocks.glb at runtime,
    // with 5 large, 5 medium, and 5 small variants.
    // Each variant gets one InstancedMesh3D and is scattered along the east shoreline. See Rocks.cs.
    internal Rocks rocks;

    internal Mountains mountains;

    internal Player player;

    internal StreetLight streetLight;

    internal House house;

    Ball ball;

    Sphere sphere;

    internal Room room;

    internal Robots robots;

    internal Birds birds;

    CubeField cubeField;

    internal Logo logo;

    Season.AI.Panels.AIButton aiButton;

    internal Season.AI.Panels.AIPanel aiPanel;

    Skill skill;

    Painting painting;

    internal List<Mesh3D> meshes = new List<Mesh3D>();

    internal Views views;

    Setting setting;
    internal SettingPanel settingPanel;

    Direction direction;

    internal ObjectPicker picker;

    // Player movement collision: obstacle-bounds registration and displacement solving,
    // consumed by Direction.MovePlayer and UpdateLongJump.
    internal PlayerCollider collider;

    // Camera occlusion fading: fades occluders to reveal the Player behind them,
    // driven near the end of App.Update.
    internal OcclusionFade fade;

    List<INativeImageDecoder> nativeImageDatas;
    int nativeImageDatasIndex = 0;

    // Video playback: frames come from IVideoPlayerService, with Windows using MediaPlayer acceleration.
    // _pendingVideoFrame is written by the VideoFrameAvailable event and consumed in the Update loop.
    INativeImageDecoder? _pendingVideoFrame;
    string? _mp4Path;

    // 1-3: FovY preset cycle (30/45/60 degrees), demonstrating runtime-adjustable camera projection settings.
    //internal readonly float[] fovPresets = { MathF.PI / 6f, MathF.PI / 4f, MathF.PI / 3f };
    //internal int fovPresetIndex = 1;

    internal App()
    {
        Title = "SeasonEngine";

        StorageService.DirectoryBase = "SeasonEngine";

        BackgroundColor = Season.Basic.Colors.White;

        BasicResolution = new Vector2(1920, 1080);

        // 2-4 Step 1 acceptance: enable the DDGI mode here.
        // The field defaults to Off, so it must be set during initialization.
        // App construction runs before platform graphics initialization, which lets dependent resources and variants bake correctly.
        // Step 6: static RenderQuality values have converged to the Default* source of truth.
        // Overriding Default* here is snapshotted into Settings.RenderQuality when BaseApp.Init reads Settings,
        // after which rendering always consumes Settings.RenderQuality.
        RenderQuality.DefaultGlobalIllumination = Season.Rendering.GiMode.Ddgi;
        // Measurement note: GiBounceGain=3 was tested and then rolled back.
        // A feedback gain of alb*3 > 1 makes probe irradiance diverge frame by frame,
        // saturating the fp16 atlas into solid white. The bounce chain itself works correctly;
        // future amplification must stay below 1 / (alb * directional overlap), for example 1.5.

        // 2-5 Step E: raise aerial perspective intensity to 12.
        // This is for artistic visibility, not a physics correction. The engine default value 1 is already the physical one,
        // but this sample world is only on the scale of hundreds of meters, while real aerial perspective is a kilometer-scale effect.
        // Measurement note from 2026-08 with a frozen day-night phase: at 1, the distant mountains gain only +2.2/255 blue,
        // which is visually negligible. At 12, it rises to +22.2/255 while keeping the same qualitative behavior:
        // b > g > r from Rayleigh scattering, blue growth increasing with distance,
        // and no change in the sky tiles because renderMode==3 bypasses the AP branch.
        // Tuning note: this control is a lerp weight, not a multiplier.
        // 0 bypasses it, 1 is the physical value, and >1 extrapolates for emphasis.
        // Scenes on the scale of hundreds of meters usually want about 8 to 16, so this sample uses 12.
        // Kilometer-scale scenes should move back toward 1 or 2.
        // It can also be adjusted at runtime through Settings.RenderQuality.AerialIntensity without rebuilding,
        // then copied back here once the final default is chosen.
        // Only the sample default is overridden; the engine-level DefaultAerialIntensity remains 1.
        RenderQuality.DefaultAerialIntensity = 12f;

        ResetCamera();
    }

    void ResetCamera()
    {
        // The engine uses a left-handed system and the standard setup where CameraPos.Z < 0 looks toward the origin.
        // Inside LookAtLeftHanded, xaxis=(+1,0,0) keeps X unflipped, so world +X maps to the right side of the screen
        // and text on model surfaces is not mirrored.

        CameraTarget = new System.Numerics.Vector3(0, 0.5f + 3f, -2.5f);

        CameraPos = CameraTarget + new Vector3(0, 0.5f, -3.5f);

        // 1-3: projection parameters are now carried by the Camera object,
        // so changing them here updates all platforms.
        // The setter has Changed gating: identical values do not mark matrices dirty,
        // which avoids unnecessary rebuilds for a stationary camera.
        // Far=1300 must cover two kinds of farthest points:
        // 1) the farthest skybox exit point. With the camera at the box center and half extent h=450,
        //    the maximum ray distance toward a corner is h*sqrt(3) ~= 779m.
        //    A smaller far plane cuts a long strip out of the skybox top.
        // 2) the farthest sea corner. After expanding the sea to 1400x1400 (±700m),
        //    the farthest clamped camera position in Direction to the most distant sea corner is about 1240m.
        //    A smaller far plane clips the diagonal far sea and reveals the seam at the bottom of the skybox.
        // 1300 covers both cases.
        Camera.FovY = MathF.PI * 13 / 36f; // fovPresets[fovPresetIndex];
        Camera.Near = 0.1f;
        Camera.Far = 1300f;
    }

    internal void BindCamera()
    {
        CameraTarget = new Vector3(player.model.PosX, player.model.PosY + 3f, player.model.PosZ - 2.5f);

        CameraPos = CameraTarget + new Vector3(0, 0.5f, -3.5f);
    }

    /// <summary>
    /// Camera follow after player movement, used by both movement modes and consumed by
    /// Direction.MovePlayer and Skill.UpdateLongJump.
    /// World mode keeps the legacy top-down behavior by resetting to a fixed camera position behind and above the player through BindCamera.
    /// Character mode keeps the dragged orientation and distance, then translates the whole camera rig by <paramref name="delta"/>
    /// so the target point moves with the player while the look vector stays unchanged.
    /// The Y component moves together with steps or jumps so pitch is preserved.
    /// </summary>
    internal void FollowCamera(Vector3 delta)
    {
        if (Movement == Movement.World)
        {
            BindCamera();
            return;
        }

        CameraPos += delta;
        CameraTarget += delta;
    }

    /// <summary>
    /// Per-frame facing sync for Character mode: each frame the player's yaw is aligned with the camera view
    /// projected onto the XZ plane, so the character faces where the over-the-shoulder camera looks.
    /// The Y component is ignored, meaning pitch only affects camera elevation and does not affect yaw.
    /// This is the inverse of Skill.YawForward: forward=(-sin yaw, 0, -cos yaw) -> yaw=Atan2(-fx, -fz).
    /// Pure vertical views with almost no horizontal component are skipped, and long jumps keep the locked jumpForward direction.
    /// Synchronizing every frame, rather than only while dragging, lets mode switches, landings, and camera changes converge naturally on the next frame.
    /// </summary>
    void SyncPlayerYawToCamera()
    {
        var model = player?.model;
        if (model == null || !model.Ready || player.jumping)
            return;

        var dir = CameraTarget - CameraPos;
        if (dir.X * dir.X + dir.Z * dir.Z < 1e-8f)
            return;

        model.Rotation = MathF.Atan2(-dir.X, -dir.Z);
    }

    void RegisterEffects()
    {
        // 1-6 compute-baseline validation: register the built-in PlasmaEffect.
        // It dispatches every frame in the FrameStart phase and writes a 256x256 storage texture.
        // When registration succeeds, Sprite2D can display it directly by TextureName without extra changes.
        // If the platform lacks compute support or this backend has no shader source, registration returns false and the control is skipped gracefully.
        if (Season.Rendering.FrameSchedule.RegisterCompute(
                Season.Basic.Graphics.Instance, new Season.Rendering.Effects.PlasmaEffect()))
        {

        }

        // 2-1 Step A first acceptance: register SceneColorCopyEffect.
        // In the AfterScene phase it downsamples offscreen SceneColor into a 480x270 rgba16float storage texture.
        // At the moment only D3D12 provides HLSL source, and other backends or direct-render paths degrade gracefully at registration time.
        if (Season.Rendering.FrameSchedule.RegisterCompute(
                Season.Basic.Graphics.Instance, new Season.Rendering.Effects.SceneColorCopyEffect()))
        {

        }

        // 2-3 Step B: register TaaEffect.
        // It performs a full-resolution resolve in the AfterScene phase with a ping-pong pair of textures.
        // Contract rule 13 requires it to be registered before BloomEffect because AfterScene effects are recorded in registration order,
        // and bloom must consume the image after TAA resolve. Otherwise shimmering highlights would be amplified into visible flicker.
        // Non-TAA mode, missing velocity, or missing offscreen SceneColor all cause a graceful registration-time fallback to SceneColor.
        if (Season.Rendering.FrameSchedule.RegisterCompute(
                Season.Basic.Graphics.Instance, new Season.Rendering.Effects.TaaEffect()))
        {

        }

        // 2-1 Step B: register BloomEffect.
        // It runs a three-kernel dual-chain pass in AfterScene and writes FrameSchedule.BloomTexture for FinalBlit composition.
        // Threshold and intensity are controlled by RenderQuality.
        // Only D3D12 currently provides HLSL source, and other backends or LDR direct-render modes fall back gracefully.
        if (Season.Rendering.FrameSchedule.RegisterCompute(
                Season.Basic.Graphics.Instance, new Season.Rendering.Effects.BloomEffect()))
        {

        }

        // 2-2 Step A first acceptance: register DepthViewEffect.
        // In AfterScene it linearizes full-resolution SceneDepth into a 480x270 grayscale storage texture.
        // AO Off, AO fallback, or a missing SceneDepth all degrade gracefully at registration time.
        // After Step C, all four backends have shader coverage.
        if (Season.Rendering.FrameSchedule.RegisterCompute(
                Season.Basic.Graphics.Instance, new Season.Rendering.Effects.DepthViewEffect()))
        {

        }

        // 2-2 Step B: register GtaoEffect.
        // In AfterScene it runs gtaoMain plus a bidirectional separable blur in three dispatches,
        // then writes FrameSchedule.AoTexture for FinalBlit and uber-shader AO composition.
        // Radius and intensity are driven by RenderQuality.AoRadius and AoIntensity.
        // AO Off, AO fallback, or a missing SceneDepth all degrade gracefully at registration time.
        if (Season.Rendering.FrameSchedule.RegisterCompute(
                Season.Basic.Graphics.Instance, new Season.Rendering.Effects.GtaoEffect()))
        {

        }

        // 2-3 Step A first acceptance: register VelocityViewEffect.
        // In AfterScene it downsamples full-resolution SceneVelocity into a 480x270 direction-and-magnitude visualization texture.
        // MotionVectors Off or a missing SceneVelocity both degrade gracefully at registration time.
        // Acceptance criteria are documented in VelocityViewEffect; this step does not perform TAA resolve.
        if (Season.Rendering.FrameSchedule.RegisterCompute(
                Season.Basic.Graphics.Instance, new Season.Rendering.Effects.VelocityViewEffect()))
        {

        }

        // 1-8 acceptance: register Sdf3DViewEffect.
        // In FrameStart it runs a three-dispatch chain that covers 3D texture read/write,
        // the R16Float format, 4x4x4 and 64x1x1 workgroups, and four constant-buffer expansions in UpdateStorageBuffer.
        // What gets displayed is the final 3D-to-2D slice kernel output at 256x256,
        // since Sprite2D cannot consume the 3D texture directly.
        // Missing shader source or unsupported 3D formats degrade gracefully at registration time.
        if (Season.Rendering.FrameSchedule.RegisterCompute(
                Season.Basic.Graphics.Instance, new Season.Rendering.Effects.Sdf3DViewEffect()))
        {

        }

        // 2-4 Step 1 acceptance: register DdgiEffect.
        // In AfterScene it runs two dispatches: upload the proxy list every frame,
        // then gather the minimum distance per voxel into an R16Float volume of size res^3,
        // followed by a 256x256 debug slice.
        // Non-DDGI GiMode, missing shader source, or unsupported 3D formats degrade gracefully at registration time.
        if (Season.Rendering.FrameSchedule.RegisterCompute(
                Season.Basic.Graphics.Instance, new Season.Rendering.Effects.DdgiEffect()))
        {

        }

        // 2-5 Step A: register SkyAtmosphereEffect.
        // In FrameStart it maintains two LUTs: transmittance on demand and Sky-View every frame.
        // This must run before new Sky() because the Sky constructor reads FrameSchedule.SkyViewTexture
        // to choose materials for the six skybox faces, either procedural LUTs or static cloudtop textures.
        // RegisterEffects is called in Create before sky is created, and that order must not be reversed.
        // Non-procedural SkyMode, missing shader source, or unsupported compute degrade gracefully at registration time,
        // leaving SkyViewTexture null so the skybox falls back to static textures face by face.
        if (Season.Rendering.FrameSchedule.RegisterCompute(
                Season.Basic.Graphics.Instance, new Season.Rendering.Effects.SkyAtmosphereEffect()))
        {

        }
    }

    // Fully synchronous and intentionally free of blocking loads.
    // On Android, InitializeVulkan does not start the render loop until this method returns,
    // so every extra millisecond here directly becomes black or blue startup time before the first frame.
    // Heavy resources must go through Task.Run or the control loading queue.
    public override void Create()
    {
        base.Create();

        RegisterEffects();

        var musics = new string[] { @"Musics/Cozy.wav", @"Musics/Forest.wav", @"Musics/Sweel.wav" };

        var music = musics[new Random().Next(0, 3)];

        StorageService.CopyToLocal(music);

        var path = StorageService.SubPath(StorageService.DirectoryBase, music);

        DeviceServices.Media.PlayMedia("Music", path, "60");

        celestial = new CelestialLighting();
        celestial.Load();
        sky = new Sky(celestial);
        AddPanel(sky);

        ground = new Ground();
        AddPanel(ground);

        // Procedural sea: add it right after the ground.
        // The sea sits at y=-3 below the land, so depth testing naturally hides the part under the terrain.
        sea = new Sea();
        AddPanel(sea);

        // Procedural beach: add it right after the sea.
        // Tile top edges are pinned around y=-0.4 and buried under the grass, while bottom edges stay deep underwater,
        // bridging the land edge to the sea through instanced 3D tiles. See Beach.cs.
        //beach = new Beach();
        //AddPanel(beach);

        // Coastal rocks: add them right after the beach.
        // It is expected that no rocks appear until background extraction builds the Rocks.glb templates.
        // Instances straddle the east shoreline, partly buried in sand or grass and partly entering the water. See Rocks.cs.
        rocks = new Rocks();
        AddPanel(rocks);
        DeviceServices.BaseApp?.RequestLoad(rocks);   // Queue the whole panel so Load can extract in the background.

        //// Background mountain ring: extract four mountain meshes from background_mountains.glb at runtime,
        //// then assign each one to an InstancedMesh3D for ring instancing. See Mountains.cs.
        mountains = new Mountains();
        AddPanel(mountains);
        DeviceServices.BaseApp?.RequestLoad(mountains);   // Queue the whole panel so Load can extract in the background.

        player = new Player();
        AddPanel(player);

        streetLight = new StreetLight();
        AddPanel(streetLight);

        house = new House();
        AddPanel(house);

        sphere = new Sphere();
        AddPanel(sphere);

        ball = new Ball();
        AddPanel(ball);

        room = new Room();
        room.PosX = 6;
        room.PosY = 3f;
        room.PosZ = 60;
        room.Width = 12;
        room.Height = 6;
        room.Depth = 12;
        AddPanel(room);

        robots = new Robots();
        // Robots contains Sprite3D controls that use the Transparent pipeline and do not write depth,
        // so correct occlusion depends on draw order.
        // Raising Layer by one would place the whole panel after opaque panels such as Sphere.sphereRow,
        // allowing the sprites to appear correctly in front through depth testing,
        // while the other opaque controls inside the panel would keep resolving depth on their own.
        //robots.Layer = 1;
        AddPanel(robots);

        birds = new Birds();
        AddPanel(birds);

        //billboard = new Billboard();
        //AddPanel(billboard);

        //cubeField = new CubeField();
        //AddPanel(cubeField);

        //bar = new Bar();
        //AddPanel(bar);

        logo = new Logo();
        AddPanel(logo);

        aiButton = new Season.AI.Panels.AIButton()
        {
            OnClick = () =>
            {
                if (aiPanel is null)
                {
                    aiPanel = new Season.AI.Panels.AIPanel()
                    {
                        OnClose = () =>
                        {
                            Season.AI.Panels.AIPanel.Instance = null;
                            App.Instance.RemovePanel(aiPanel);
                            aiPanel = null;
                        }
                    };
                    App.Instance.AddPanel(aiPanel);
                }
            }
        };
        AddPanel(aiButton);

        skill = new Skill();
        AddPanel(skill);

        painting = new Painting();
        AddPanel(painting);

        setting = new Setting()
        {
            OnClick = () =>
            {
                settingPanel = new SettingPanel()
                {
                    OnClose = () =>
                    {
                        RemovePanel(settingPanel);
                        settingPanel = null;
                    }
                };
                AddPanel(settingPanel);
            }
        };
        AddPanel(setting);

        direction = new Direction();
        AddPanel(direction);

        // Hover picking highlight: when the pointer lands on a target's screen projection,
        // pulse Alpha over time and draw a white translucent bounds box.
        // Targets are opt-in only; background ground, walls, and the skybox are excluded.
        // Update must run after all target panels.
        // InstancedTargets registers instanced controls, with hit testing at single-instance granularity for per-instance bounds and property editing.
        picker = new ObjectPicker();
        picker.Targets.Add(ground.grass);
        picker.Targets.Add(player.model);
        picker.Targets.Add(ball.model);
        picker.Targets.Add(ball.bee);
        picker.Targets.Add(streetLight.lightsPunctualLamp);
        picker.Targets.Add(house.model);
        picker.Targets.Add(room.bottle);
        picker.Targets.Add(room.busterDrone);
        picker.Targets.Add(sphere.sphereRow);
        picker.InstancedTargets.Add(robots.robotField);
        picker.InstancedTargets.Add(birds.seagullsModel);
        AddPanel(picker);

        // Movement-collision registration: only register solid obstacles that block ground travel.
        // Room floor, ceiling, roof, and stairs are not added because they are walkable or overhead pieces.
        // A full XZ floor would also block the doorway path entirely.
        // Stairs and floor height instead use the stepwise elevation logic through collider.Room; see PlayerCollider.FloorHeightUnder.
        // Walls, including lintels and door posts, are registered box by box so Y-overlap participates in blocking tests:
        // the player cannot reach lintel height, but the doorway still remains traversable.
        // Small items such as the ceiling-light marker sphere and the bee do not block movement and are skipped.
        // Unloaded controls with zero boxes are skipped automatically, so registration can happen before assets are ready.
        collider = new PlayerCollider();
        collider.Room = room;
        collider.Obstacles.Add(ball.model);
        collider.Obstacles.Add(ball.bee);
        collider.Obstacles.Add(streetLight.lightsPunctualLamp);
        collider.Obstacles.Add(house.model);
        collider.Obstacles.Add(sphere.sphereRow);
        collider.Obstacles.Add(room.bottle);
        collider.Obstacles.Add(room.busterDrone);
        foreach (var part in room.roomParts)
        {
            if (part.Name is "RoomFloor" or "RoomCeiling" or "RoomRoof" || part.Name.StartsWith("RoomStair"))
                continue;

            collider.Obstacles.Add(part);
        }
        collider.InstancedObstacles.Add(robots.robotField);

        // Occlusion-fade registration: start from every movement obstacle such as walls, the house, lamp posts, and the sphere row,
        // then add the roof and ceiling because they may block the camera view even though they do not block movement.
        // The floor is excluded because it sits on the ground and cannot realistically enter the camera sightline.
        // Instanced Robots are not included because there is no per-instance Alpha path and full occlusion by them is rare.
        fade = new OcclusionFade();
        foreach (var obstacle in collider.Obstacles)
            fade.Register(obstacle);
        foreach (var part in room.roomParts)
        {
            if (part.Name is "RoomRoof" or "RoomCeiling")
                fade.Register(part);
        }

        //StorageService.DirectoryDel(StorageService.DirectoryBase, "");

        // Load fonts asynchronously in the background so UI construction and first-frame rendering stay unblocked,
        // while Shape and Sprite content can appear immediately.
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            var fonts = new List<Season.Basic.Font>()
                    {
                        new Season.Basic.Font()
                        {
                            //https://fonts.google.com/noto/specimen/Noto+Sans+Mono
                            File = "Assets/NotoSansMono-VariableFont.ttf",
                            Name = "SansMono",
                            Language = "",
                            Size = FontSize,
                            ReadOnly = true,
                            Time = DateTime.Now.ToDateTimeMilliseconds()
                        },
                        new Season.Basic.Font()
                        {
                            //https://fonts.google.com/noto/specimen/Noto+Sans+SC?preview.script=Hans
                            File = "Assets/NotoSansSC-VariableFont_wght.ttf",
                            Name = "SansSC",
                            Language = "",
                            Size = FontSize,
                            ReadOnly = true,
                            Time = DateTime.Now.ToDateTimeMilliseconds()
                        },
                        new Season.Basic.Font()
                        {
                            //https://fonts.google.com/noto/specimen/Noto+Sans+TC?preview.script=Hant
                            File = "Assets/NotoSansTC-VariableFont_wght.ttf",
                            Name = "NotoSansTC",
                            Language = "",
                            Size = FontSize,
                            ReadOnly = true,
                            Time = DateTime.Now.ToDateTimeMilliseconds()
                        },
                        new Season.Basic.Font()
                        {
                            //https://github.com/mozilla/twemoji-colr
                            File = "Assets/Twemoji.ttf",
                            Name = "Twemoji",
                            Language = "",
                            Size = FontSize,
                            ReadOnly = true,
                            Time = DateTime.Now.ToDateTimeMilliseconds()
                        }
                    };

            for (var i = 0; i < fonts.Count; i++)
            {
                var font = fonts[i];

                if (font.File.IsNullOrWhiteSpace())
                {

                }
                else
                {
                    try
                    {
                        var fontInstance = await Season.Fonts.Font.CreateAsync(font.File, font.Size);

                        Season.Fonts.Font.Instance.Add(fontInstance);
                    }
                    catch (Exception ex)
                    {
                        AddLog(LogType.Error, $"{DateTime.UtcNow} [Font.CreateAsync] file={font.File} failed err={ex}");
                    }
                }
            }

            FontsCreated = true;
        });
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? width = null, float? height = null, float? posZ = null, float? depth = null)
    {
        base.Update(time, alpha: alpha, posX: posX, posY: posY, width: width, height: height);

        if (Mode is Mode.Show)
        {
            setting?.Alpha = 0f;
            direction?.Alpha = 0f;
            skill?.Alpha = 0f;
            painting?.Alpha = 0f;
            picker?.Alpha = 0f;
            views?.Alpha = 0f;
        }
        else if (Mode is Mode.Play)
        {
            setting?.Alpha = 1f;
            direction?.Alpha = 1f;
            skill?.Alpha = 1f;
            painting?.Alpha = 0f;
            picker?.Alpha = 0f;
            views?.Alpha = 0f;
        }
        else if (Mode is Mode.Edit)
        {
            setting?.Alpha = 1f;
            direction?.Alpha = 0f;
            skill?.Alpha = 0f;
            painting?.Alpha = 1f;
            picker?.Alpha = 1f;
            views?.Alpha = 0f;

            Time -= time;
        }
        else if (Mode is Mode.Debug)
        {
            setting?.Alpha = 1f;
            direction?.Alpha = 1f;
            skill?.Alpha = 1f;
            painting?.Alpha = 0f;
            picker?.Alpha = 1f;
            views?.Alpha = 1f;
        }

        if (settingPanel != null)
        {
            settingPanel.Update(time, alpha: 0.8f, posX: 100, posY: 100, width: (int)App.Instance.ExtendResolution.X - 200, height: (int)App.Instance.ExtendResolution.Y - 200);

            return false;
        }

        if (aiPanel != null)
        {
            aiPanel.Update(time, alpha: 0.8f, posX: 100, posY: 0, width: (int)App.Instance.ExtendResolution.X - 200, height: (int)App.Instance.ExtendResolution.Y);

            return false;
        }

        aiButton?.Update(time);

        logo?.Update(time);

        // Run hover picking last so picking and bounds matching read the final world matrices for this frame,
        // after every panel has finished updating.
        if ((painting == null || !painting.MouseOver) && picker != null && picker.Alpha > 0 && picker.Update(time))
        {
            return true;
        }

        if (painting != null && painting.Update(time))
        {
            return true;
        }

        if ((painting == null || !painting.MouseOver) && !direction.MouseOver)
        {
            if (TouchService.PoZ != null && TouchService.PoZ != 0)
            {
                var poZ = (float)TouchService.PoZ / 1000;

                var direction = CameraTarget - CameraPos;

                var length = direction.Length();

                var unit = direction / length * step;

                if (poZ < 0)
                {
                    unit = -unit;
                }

                CameraPos += unit;

                CameraTarget += unit;

                TouchService.PoZ = 0;
            }

            // Drag to rotate the view.
            // Character mode rotates around the Player anchor in an over-the-shoulder style,
            // moving both camera and target around the character while keeping camera distance.
            // World mode keeps camera position fixed and only rotates the look vector (Target - CameraPos).
            // Horizontal drag maps to yaw around world +Y in the left-handed system,
            // vertical drag maps to pitch around the camera right axis,
            // and the sensitivity stays consistent with the original implementation at about pi radians across the full screen.
            if (TouchService.MoveX != 0 || TouchService.MoveY != 0)
            {
                // Rotation pivot: Player anchor in Character mode and camera position in World mode, preserving legacy behavior.
                var pivot = CameraPos;
                var orbitPlayer = false;
                if (Movement == Movement.Character && player?.model != null)
                {
                    pivot = new Vector3(player.model.PosX, player.model.PosY, player.model.PosZ);
                    orbitPlayer = true;
                }

                // Two vectors are rotated together: the view vector used for clamping, matching the original behavior,
                // and the camera offset from the pivot, which only matters in Character mode.
                var view = CameraTarget - CameraPos;
                var posDir = CameraPos - pivot;

                float yaw = -TouchService.MoveX / ExtendResolution.X * MathF.PI;
                float pitch = -TouchService.MoveY / ExtendResolution.Y * MathF.PI;

                // Yaw rotates around world +Y in the left-handed system.
                // Dragging right makes yaw positive, pushes view.X positive, and turns the camera to the right.
                // Both vectors use the same rotation, preserving the relation between camera, target, and pivot.
                float cy = MathF.Cos(yaw);
                float sy = MathF.Sin(yaw);
                view = new Vector3(
                    view.X * cy + view.Z * sy,
                    view.Y,
                   -view.X * sy + view.Z * cy);
                posDir = new Vector3(
                    posDir.X * cy + posDir.Z * sy,
                    posDir.Y,
                   -posDir.X * sy + posDir.Z * cy);

                // Pitch rotates around the camera right axis, normalize(cross(+Y, view)).
                // Dragging downward makes view.Y more negative and rotates the view downward.
                var right = Vector3.Cross(Vector3.UnitY, view);
                if (right.LengthSquared() > 1e-6f)
                {
                    right = Vector3.Normalize(right);
                    var pitchRot = Matrix4x4.CreateFromAxisAngle(right, pitch);
                    view = Vector3.Transform(view, pitchRot);
                    posDir = Vector3.Transform(posDir, pitchRot);
                }

                // Clamp pitch to avoid becoming collinear with Up=+Y, which would make LookAt singular and cause flipping.
                // The limit is about plus or minus 79 degrees.
                float radius = view.Length();
                if (radius < 1e-5f) { radius = 1f; view = new Vector3(0, 0, 1); }
                view = Vector3.Normalize(view) * radius;
                float maxY = radius * 0.98f;
                if (MathF.Abs(view.Y) > maxY)
                {
                    float newY = MathF.Sign(view.Y) * maxY;
                    float horizLen = MathF.Sqrt(MathF.Max(0f, radius * radius - newY * newY));
                    var horiz = new Vector2(view.X, view.Z);
                    if (horiz.LengthSquared() > 1e-6f)
                    {
                        horiz = Vector2.Normalize(horiz) * horizLen;
                        view = new Vector3(horiz.X, newY, horiz.Y);
                    }
                }

                if (orbitPlayer)
                    CameraPos = pivot + posDir;

                CameraTarget = CameraPos + view;
            }
        }

        // Character movement mode uses an over-the-shoulder convention:
        // each frame the character aligns with the camera's horizontal facing,
        // so dragging the camera also turns the character and forward motion always matches the view projected onto the ground.
        // Per-frame synchronization makes mode switches and long-jump landings converge naturally.
        if (Movement == Movement.Character && Mode is Mode.Play or Mode.Debug)
        {
            SyncPlayerYawToCamera();
        }

        skill?.Update(time);

        views?.Update(time);

        setting?.Update(time);

        direction?.Update(time);

        // Panel.Update does not recurse automatically and only synchronizes layout parameters,
        // so every panel must be driven explicitly here.
        // Each panel should be updated exactly once per frame.
        // Repeating Update would repeat animation sampling and skeleton uploads,
        // and because GLTFAnimationPlayer accumulates time, a second Update in the same frame would double-advance animated models such as house.
        // The lighting driver must run before the sky panel because Sky.Update reads the celestial per-frame cache
        // for DayPhase, SunUp, MoonUp, and MoonPhase when applying tint and marker visibility.
        celestial?.Update(time);
        sky?.Update(time);

        // Ground is static; same pattern, with first-frame geometry upload and draw submission.
        ground?.Update(time);

        // Sea uses static geometry; each frame only synchronizes transforms and materials.
        sea?.Update(time);

        // Beach uses static instances; each frame only synchronizes instance matrices and materials.
        beach?.Update(time);

        // Coastal rocks are statically instanced and early-out internally until extraction finishes.
        rocks?.Update(time);

        // Background mountains are statically instanced and early-out internally until extraction finishes.
        mountains?.Update(time);

        player?.Update(time);

        streetLight?.Update(time);

        house?.Update(time);

        ball?.Update(time);

        robots?.Update(time);

        birds?.Update(time);

        //cubeField?.Update(time);
        //billboard?.Update(time);

        //bar?.Update(time);

        room?.Update(time);

        sphere?.Update(time);

        meshes?.ForEach(me =>
        {
            me.Update(time);
        });

        // Run occlusion fading at the end so both camera and player positions have reached their final values for this frame,
        // keeping ray tests consistent with the rendered image.
        fade?.Update(time);

        return false;
    }

    protected void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (DeviceServices.Video != null)
            {
                DeviceServices.Video.VideoFrameAvailable -= OnVideoFrameAvailable;
                DeviceServices.Video.PlaybackEnded -= OnVideoPlaybackEnded;
                DeviceServices.Video.Stop();
            }
            // Dispose pending frame if any
            var pending = Interlocked.Exchange(ref _pendingVideoFrame, null);
            pending?.Dispose();
        }
        //base.Dispose(disposing);
    }

    // -- VideoPlayer event handlers --

    /// <summary>VideoFrameAvailable callback from any thread. Atomically swaps in the frame waiting to be displayed.</summary>
    void OnVideoFrameAvailable(INativeImageDecoder frame)
    {
        var old = Interlocked.Exchange(ref _pendingVideoFrame, frame);
        old?.Dispose(); // Release the previous frame if Update has not consumed it yet.
    }

    /// <summary>Playback-ended callback from any thread. Looping or stop logic can be handled here.</summary>
    void OnVideoPlaybackEnded()
    {
        System.Diagnostics.Debug.WriteLine("Video playback ended.");
    }

}
