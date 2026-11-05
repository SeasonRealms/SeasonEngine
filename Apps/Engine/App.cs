// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Season.Rendering.Effects;

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

    /// <summary>
    /// Player travel speed multiplier, 1 being the authored baseline. The settings panel offers 0.50x to 3.00x in
    /// quarter steps and writes this field; Direction reads it on every move and scales the step there, so a 0.1 m tap
    /// and a time-scaled held key speed up together instead of disagreeing.
    /// </summary>
    internal float playerSpeed = 1f;

    Sky sky;

    // Central celestial-lighting driver split out of Sky in 2026-08. It owns all lighting work:
    // light sources, environment lighting, weather, and SH9 updates.
    // Update must run it before sky because skybox tinting and marker visibility read its per-frame cache.
    CelestialLighting celestial;

    /// <summary>
    /// Clock hour in [0,24), read straight off the celestial clock so scene props switch on the same sun that lights
    /// them instead of keeping a second timer. The tower beacon opens at 18:00 and closes at 06:00 against this.
    /// Falls back to sunrise, i.e. daylight, before the driver exists.
    /// </summary>
    internal float HourOfDay => Season.Rendering.DayNightCycle.HourFromPhase(celestial?.DayPhase ?? 0f);

    /// <summary>当前昼夜相位（0..1）。WaterfallPool 的潭底焦散与 Pond 焦散同源，每帧按阳光高度重算亮度。</summary>
    internal float DayPhase => celestial?.DayPhase ?? 0f;

    // Scene-medium driver: the three authored fog presets, the local volumes they use, and the colonnade the sun-shaft
    // preset needs as an occluder. Update runs right after celestial because the wind drift shares that clock's frame
    // delta and simulation scale rather than reading App.Time.
    internal Fog fog;

    // The registered volumetric pass, or null when the tier or the backend declined it at registration time. Held only
    // for the diagnostics row in the settings panel (grid size, history weight), which is the one place that needs to
    // report what was actually built rather than what was asked for.
    internal Season.Rendering.Effects.VolumetricFogEffect fogEffect;

    // 后端拒绝 compute/indirect 时为空；晴天不会提交原先无区域约束的演示粒子。
    internal WeatherPrecipitationEffect precipitation;

    internal WeatherController weather;
    readonly WeatherVisualState weatherVisuals = new();
    // 雷电天气的雷声：落雷边沿来自 weatherVisuals，播放的是 Resources/Raw/Musics 里的静态 WAV。
    readonly ThunderAudio thunderAudio = new();
    WeatherCurtainEffect weatherCurtain;
    // 闪电通道 V2：每次落雷把通道几何当 ribbon 带间接绘制一次；注册失败时保持 null，退回 v1 的云内发光。
    LightningEffect lightning;

    // 最后一次同步给天气场的房间内腔；房间被缩放或移动后才会重建遮挡区域记录。
    Vector3 weatherCoverMin, weatherCoverMax;

    internal Ground ground;

    // Procedural sea: a 1400x1400m noisy water surface around the land.
    // Sea.SeaLevel is runtime-adjustable and defaults to y=-1.5 (Sea.DefaultSeaLevel).
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

    internal Forest forest;

    // ── Forest mist patch ──
    //
    // Fog in this sample is authored as a patch, not as weather: the global medium is off (GlobalExtinction = 0), so the box
    // below is the only medium in the world and every pixel outside it composites as identity. That is the shape a general
    // "one fog per place" capability takes - the grove has mist, the beach, the road and the meadow do not.
    //
    // The patch covers the grove (x in [-60, -40], z in [-30, -10], trees 2 m tall) plus 12 m of feather on every side, so
    // its box never reads as a box. Density is set so ~10 m inside it is already half opaque, while a viewer standing
    // outside sees a lit bank between the trunks rather than a wall.
    //
    // Night needs no special case: a patch that nothing lights is invisible, and the player's torch (5 cd, 12 m range,
    // registered in Player) turns it into a visible glow the moment they walk in. That is the intended behaviour.
    //
    // MaxDistance is the froxel depth range, and it has to clear the longest distance the patch is expected to be seen
    // from - which is not a ground-level camera. From directly above the island the grove sits 110-150 m away, so the
    // previous 140 m put the patch inside the final fade band or past the end of the range entirely and it blinked in
    // and out as the camera rose. 400 m covers the overhead view at almost no cost: the same 96 slices are spread
    // exponentially, and a patch 20 m out only gives up about a sixth of its depth resolution.
    // A runtime flag rather than a const so the panel's two paths both stay real code to the compiler: a const would
    // fold the preset branch into unreachable code the moment anyone made this false on purpose.
    internal static bool FixedIslandFog { get; set; } = true;

    // The global medium is off. This is the one number that decides whether the world has weather or only patches.
    const float GlobalExtinction = 0f;

    // Anisotropy, depth range and wind are medium-wide by construction - the phase function and the froxel layout have no
    // per-volume counterpart - so they stay here even though the global extinction is zero.
    const float MistAnisotropy = 0.60f;    // Forward scattering: the sunlit side of the patch glows, the far side greys out.
    const float MistMaxDistance = 400f;
    static readonly Vector3 MistWind = new(0.30f, 0f, 0.10f);

    // Density at the centre of the patch, before the density multiplier. Denser than a scene-wide medium on purpose: it
    // only has 22 m to work with, so the contrast has to come from the near field.
    const float ForestMistExtinction = 0.06f;

    // Sky light the patch gets regardless of what lights it, in the same units as the shader's illumination accumulator:
    // peak 0.55 is the order of the noon ambient, which is enough to lift a canopy-shadowed patch reading as black-grey
    // smoke back to white mist, without outshining the sky. It only opens around dawn and dusk (see MistSkyWeight): at
    // noon the ambient already does the job, and at night a constant floor would turn the patch into a glowing white box.
    const float MistSkyFloorPeak = 0.55f;

    // Slightly cool, matching forestMist.Albedo's blue-white instead of fighting the warm sunrise light.
    static readonly Vector3 MistSkyTint = new(0.97f, 1.00f, 1.06f);

    readonly FogVolume forestMist = new()
    {
        Shape = FogVolumeShape.Box,
        Position = new Vector3(-50f, 2.4f, -20f),   // Grove centre, lifted so the densest layer sits at trunk height.
        Size = new Vector3(44f, 6.5f, 44f),         // 20x20 grove plus 12 m of feather on every side.
        Extinction = ForestMistExtinction,
        EdgeFeather = 0.5f,
        NoiseAmount = 0.5f,
        NoiseScale = 0.05f,                          // ~20 m banks, so the trunks sit inside moving clumps.
        Albedo = new Vector3(0.86f, 0.90f, 0.96f),   // Slightly darker than the sky: a pure-white patch vanishes at noon.
    };

    internal Mountains mountains;

    internal Player player;

    internal StreetLight streetLight;

    internal House house;

    // 风车小屋：站在支线西侧、桥与南脉坡脚之间的草地上，模型自带的单段动画（风车叶片旋转）循环播放。
    // 见 LittleHouse.cs。
    internal LittleHouse littleHouse;

    // Hot air balloon moored just off the tower deck on the house side, 8 player heights tall. See HotAirBalloon.cs.
    internal HotAirBalloon hotAirBalloon;

    Ball ball;

    Sphere sphere;

    internal Room room;

    internal Pond pond;

    internal Tower tower;

    // 池底的焦散贴花。Additive 贴花本身与光照无关，所以由 Update 每帧把阳光倍率写进 Intensity，
    // 否则夜里场景变暗后它会因为对比度反而显得更亮。
    Season.Rendering.DecalVolume pondCaustics;

    internal Campfire campfire;

    // 瀑布潭：水面 + 潭底贴花 + 石块潭沿 + 水花/雾/白沫三组粒子，锚点每帧跟随瀑布落点。见 WaterfallPool.cs。
    internal WaterfallPool waterfallPool;

    // 河流：从瀑布潭一路弯绕到 Rocks 一侧的海。整条河道是一张固定表（见 River.Course），
    // 按“直线段 / 弯道段”切成六个可选取的 Mesh3D；水面由 SceneWaterfall Pass 按 WaterRibbon 数据现算；
    // 两岸的草土堤坝是 RiverBank.glb 的一个批次（River.Dikes），由河段每帧把自身矩阵折进去。
    internal River river;

    // Stone arch bridge over the main river channel, at the spot the player first stood (halfway from the
    // south-branch confluence to the waterfall pool). The visible span is an AI-generated GLB (SeasonImage ->
    // SeasonThreeD, see .dbg/_bridge_assets); the walkable deck and its two grass-to-deck stone step flights
    // are authored in code so the player can climb up and cross. High enough for a boat to pass beneath. See
    // Bridge.cs.
    internal Bridge bridge;

    // 水池东堤旁的海边阶梯与直路：三排石阶与 Pond 台阶同款、面朝大海、沿堤坝行居中，路从阶梯脚下
    // 沿 z = -7.05 铺到 x = 74 的海岸岩石带为止，沥青纹路与南侧的路同出一处。See SeaRoad.cs.
    internal SeaRoad seaRoad;

    // 鬼屋与跑道正中间的圆形旗杆：底座是可行走的圆形平台，杆顶挂一面两面各印一幅海盗图的薄布旗。
    // See Flagpole.cs.
    internal Flagpole flagpole;

    // 维京小船：泊在水池北岸的台阶边，也就是 Player 出生点正下方，模型自带的 morph 动画驱动划桨与风帆。
    // 静止时由 Boat 自己算三个低频摇晃；跳上甲板后 Player 绑定在座位上、方向键驱动 Boat.Steer，
    // 可行水域（水池 / 支流 / 主河道 / 桥下 / 海口 / 海面 / 瀑布潭）见 Boat.cs。
    internal Boat boat;

    internal Robots robots;

    internal Birds birds;

    // 飞机群：十架按 1/2/3/4 四列排成楔形，在岛外的海面上方、与高塔平台同高绕岛作圆周飞行，
    // 每架的螺旋桨循环自转。见 Planes.cs。
    internal Planes planes;

    // 海边跑道上的玩家飞机：停在水池东堤旁那条沥青路上、机头朝海，Player 跳进座舱后整段航程自动完成
    // （3-2-1 倒计时、滑跑、半程起飞、掠过 Rocks 爬升、左转汇入 Planes 的绕岛航线飞三圈、原路俯冲回跑道、
    // 滑回停机位掉头）。见 AeroPlane.cs。
    internal AeroPlane aeroPlane;

    CubeField cubeField;

    internal Logo logo;

    Season.AI.Panels.AIButton aiButton;

    internal Season.AI.Panels.AIPanel aiPanel;

    Skill skill;

    Painting painting;

    internal List<Mesh3D> meshes = new List<Mesh3D>();

    // P1 瀑布：控件只提供代理网格（选取/包围盒/描边）与摆放矩阵，真正的水幕仍由 SceneWaterfall Pass 画。
    // 用列表而不是单个字段，是为了让“多条瀑布”只差一次 Add：同一份 Sheet 可以 Clone 到多个控件，
    // 各自 Place 到不同坐标，注册表上限见 SceneWaterfalls.MaxActiveWaterfalls。
    internal List<Waterfall> waterfalls = new List<Waterfall>();

    // 已交给后端的贴图名（见 RegisterNamedTexture）。贴花与瀑布常常共用一张图，按名字记一份就够。
    readonly HashSet<string> namedTextures = new();

    internal Views views;

    // P3 acceptance: two secondary views over this scene - a rear-view mirror and a minimap - drawn into their own
    // fixed-size attachments and displayed as ordinary overlay sprites. Created once in Create so both view slots and
    // their published textures live for the whole session; a backend that declines them leaves this null and the app
    // keeps its single-view look.
    internal RearViews mirrors;

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

    //internal DebugPanel debugPanel;

    List<INativeImageDecoder> nativeImageDatas;
    int nativeImageDatasIndex = 0;

    // Video playback: frames come from IVideoPlayerService, with Windows using MediaPlayer acceleration.
    // _pendingVideoFrame is written by the VideoFrameAvailable event and consumed in the Update loop.
    INativeImageDecoder? _pendingVideoFrame;
    string? _mp4Path;

    // Ctrl+S screenshot in-flight guard: CaptureApp completes through a single
    // render-thread slot, so overlapping captures would orphan the earlier completion;
    // a new capture only starts once the previous one has finished.
    System.Threading.Tasks.Task screenShotTask = System.Threading.Tasks.Task.CompletedTask;

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
        // Probe traces must be occluded by the same SDF the scene is rasterized from, otherwise a point
        // light shines straight through the stone rim of the pond and the log teepee of the campfire,
        // landing as a warm square on the grass on the wrong side of the obstruction.
        // Cost note: this adds one 24-step SDF march per light per ray, paid only for lights inside their
        // own Range (the range window zeroes the contribution first, and the shader skips the march when
        // the irradiance term is already zero). Flip it back to false to A/B a suspected GPU hang.
        RenderQuality.DefaultGiPunctualShadow = true;
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
        RenderQuality.DefaultRegionalWeather = OperatingSystem.IsAndroid() || OperatingSystem.IsIOS() || OperatingSystem.IsBrowser()
            ? RegionalWeatherQuality.Low : RegionalWeatherQuality.High;

        // Volumetric fog is a tier, so the decision has to happen here: this constructor runs before platform graphics
        // initialization, and RequiresFogSceneDepth is what makes the backends allocate SceneDepth for it and what makes
        // VolumetricFogEffect.Initialize accept the registration. Off cannot be rescued at runtime, because the pass is
        // built once - the settings row that writes this tier therefore says "restart to apply".
        //
        // The sample runs Medium everywhere it can measure it. Low collapses the shadow taps to one, which is what the
        // weaker mobile GPUs and the browser's single-threaded backend want; Medium is the tier the shafts are authored
        // against. A saved setting still wins, because Init reads Settings.json after this and only snapshots these
        // values when the file has no key of its own.
        RenderQuality.DefaultVolumetricFog = OperatingSystem.IsAndroid() || OperatingSystem.IsBrowser()
            ? FogQuality.Low
            : FogQuality.Medium;

        // D1 贴花同样是初始化期档位：它决定后端要不要为贴花把场景打散成不透明/透明两段，也决定
        // SceneDepth 会不会被补出来 —— 两件事都无法在运行期补救，所以在这里开。
        // 没有这个开关时贴花只是数据，不会出现在画面上。
        RenderQuality.DefaultDecals = true;

        // P3: Hi-Z screen-space reflection, consumed only by the water pass. The scene depth it needs is
        // declared through RequiresReflectionSceneDepth, so a backend without compute support keeps the
        // depth-less frame it had before. A backend that can create the pyramid but not this frame simply
        // reports HiZEffect.Ready=false, and the water shader falls back to the sky LUT.
        RenderQuality.DefaultScreenSpaceReflections = SsrMode.HiZ;

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
            // The basket camera is re-pinned to the eyes later in the frame (UpdateBalloonCamera), so the World-mode
            // reset here would only throw away the view the player just dragged: the ride keeps its look instead.
            if (hotAirBalloon == null || !hotAirBalloon.Aboard)
                BindCamera();
            return;
        }

        CameraPos += delta;
        CameraTarget += delta;
    }

    // -- First-person basket camera. While the player stands in the balloon's basket the camera leaves the
    //    over-the-shoulder rig and settles into the eyes, and dragging the view then turns the body inside the
    //    basket instead of the balloon (Direction steers the balloon in the crew's own frame, which never turns).
    //    The blend is published through BalloonEyeBlend so Player can keep the body out of the camera's way
    //    exactly while the camera is close to its head. --
    const float BalloonEyeSeconds = 0.35f;   // full shoulder-to-eye crossing, both ways
    const float BalloonEyeHeight = 0.9f;     // eye height as a fraction of the body, measured up from the feet
    const float BalloonEyeForward = 0.3f;    // eye nudge along the view, so the rim stops cutting off the ground below
    const float ShoulderBack = 6f;           // over-the-shoulder distance behind the view's horizontal heading
    const float ShoulderLift = 3.5f;         // rig height over the player anchor: BindCamera's 3 m target lift + 0.5 m

    float balloonEye;                        // 0 = over-the-shoulder rig, 1 = inside the eyes
    Vector3 balloonEyeFrom;                  // camera spot the entry blend starts from

    /// <summary>
    /// How far the basket camera has moved off the shoulder rig (0) and into the eyes (1). Player reads it to hide
    /// the body once the camera is over halfway in, and to bring it back only once the camera is over halfway out.
    /// </summary>
    internal float BalloonEyeBlend => balloonEye;

    /// <summary>
    /// Drives the basket camera. Called after the balloon has been updated, because the eyes are the seat the player
    /// was bound to earlier in the frame and the balloon is the one the frame will draw. The look vector is carried
    /// through the transition unchanged: only the camera body moves, so the player arrives at the eyes still looking
    /// where the shoulder rig was looking.
    /// </summary>
    void UpdateBalloonCamera(float time)
    {
        var balloon = hotAirBalloon;
        float target = balloon != null && balloon.Aboard ? 1f : 0f;

        bool entering = target > balloonEye;
        if (entering && balloonEye <= 0f)
            balloonEyeFrom = CameraPos;

        float step = time / BalloonEyeSeconds;
        balloonEye = MathF.Abs(target - balloonEye) <= step
            ? target
            : balloonEye + MathF.Sign(target - balloonEye) * step;

        var model = player?.model;
        if (model == null || balloonEye <= 0f)
            return;

        var view = CameraTarget - CameraPos;
        if (view.LengthSquared() < 1e-8f)
            view = Vector3.UnitZ;

        // Horizontal view: the eye nudge below rides along it, and the exit rig backs off along it.
        var horiz = new Vector2(view.X, view.Z);
        horiz = horiz.LengthSquared() > 1e-8f ? Vector2.Normalize(horiz) : new Vector2(0f, -1f);

        // The anchor is the body's bounding-box center, so the feet are half a height below PosY and the eyes sit
        // nine tenths of a (half-size) body above them: just past the basket rim, at the waving crew's eye level.
        float height = model.Height ?? 0f;
        var eye = new Vector3(model.PosX, model.PosY - height * 0.5f + height * BalloonEyeHeight, model.PosZ);

        // The rim in front of the eye cuts a wide wedge out of the lower view and hides the ground below the basket;
        // nudging the eye toward that rim shrinks the wedge. The balloon holds the nudge inside its own wicker, so
        // the camera never leaves the basket.
        if (balloon != null)
            eye = balloon.NudgedEye(eye, horiz, BalloonEyeForward);

        Vector3 from;
        if (entering)
        {
            // Entry leaves from the spot the over-the-shoulder camera was actually holding, whatever distance the
            // player had dragged it out to, so the first blended frame never snaps.
            from = balloonEyeFrom;
        }
        else
        {
            // Exit rebuilds the shoulder rig from the current view every frame instead of remembering one spot, so
            // the camera lands behind a player who has already started walking or jumping away.
            from = new Vector3(
                model.PosX - horiz.X * ShoulderBack,
                model.PosY + ShoulderLift,
                model.PosZ - horiz.Y * ShoulderBack);
        }

        // Smoothstep, so the camera eases out of the rig and into the eyes instead of starting at full speed.
        float t = balloonEye * balloonEye * (3f - 2f * balloonEye);
        CameraPos = Vector3.Lerp(from, eye, t);
        CameraTarget = CameraPos + view;
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
        // A7：逐实例 GPU 剔除。Phase=FrameStart 且 Scope=App，因为派发必须在任何 Pass 之外（Vulkan 在 Pass 内
        // 禁止 dispatch，Metal 的 compute 与 render encoder 互斥），而级联图集整帧只有一张，不该随视图重复剔除。
        // 注册失败（后端无 kernel 源、或缺少间接绘制能力）时 InstanceCull.Enabled 保持 false，
        // 各端 instanced group 于是不分配剔除资源，阴影自然退回 CPU 整批剔除，不会拿空缓冲去画。
        if (Season.Rendering.FrameSchedule.RegisterCompute(
                Season.Basic.Graphics.Instance, new Season.Rendering.InstanceCullEffect()))
        {
        }

        // P1：逐实例 GPU 骨骼求值。Phase=FrameStart 且 Scope=App：调色板必须在场景 Pass 之前写完，而 Vulkan 在 Pass 内
        // 禁止 dispatch、Metal 的 compute 与 render encoder 互斥，所以只能落在 Record 里；整帧只有一份调色板，不该随视图重复。
        // 注册失败（后端无 kernel 源、或不能把 storage buffer 绑进顶点阶段）时 InstanceSkinning.Enabled 保持 false，
        // 各端 InstancedModel 于是整模型退回 CPU 重放，不会去画一份没人写的调色板。
        Season.Rendering.FrameSchedule.RegisterCompute(
            Season.Basic.Graphics.Instance, new Season.Rendering.InstanceSkinningEffect());

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

        // P3: Hi-Z depth pyramid. AfterOpaque, four dispatches that halve SceneDepth four times into an r16f chain,
        // consumed by the water pass's screen-space reflection march. SSR Off means no consumer at all,
        // so the effect is not registered and the four dispatches never happen.
        if (Season.Rendering.RenderQuality.Current.ScreenSpaceReflections != Season.Rendering.SsrMode.Off)
        {
            Season.Rendering.FrameSchedule.RegisterCompute(
                Season.Basic.Graphics.Instance, new Season.Rendering.Effects.HiZEffect());
        }

        // P0: SSR。紧随 Hi-Z —— 它按名字引用那四级金字塔，注册顺序反了会拿到还没建出来的纹理。
        // 反射增量的合成入口由它自己挂到 FrameSchedule.Prepare/RenderSsrComposite 上，
        // 注册失败（后端没有加法合成能力、没有 SceneNormal、天空不是程序化）时两个委托保持 null，
        // 场景 Pass 也就不打散，画面停在主着色器已经算好的天空 + 地面反射。
        if (Season.Rendering.FrameSchedule.RegisterCompute(
                Season.Basic.Graphics.Instance, new Season.Rendering.Effects.SsrEffect()))
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

        // A0.2 first acceptance: register NormalViewEffect.
        // In AfterScene it downsamples full-resolution SceneNormal into a 480x270 world-normal (or roughness/metallic) view.
        // SceneNormals Off, or velocity dropped during initialization (which drops the normals with it), degrades gracefully
        // at registration time. Acceptance criteria are documented in NormalViewEffect.
        if (Season.Rendering.FrameSchedule.RegisterCompute(
                Season.Basic.Graphics.Instance, new Season.Rendering.Effects.NormalViewEffect()))
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

        // 3-x Step A: register VolumetricFogEffect.
        // Last on purpose. It is an AfterOpaque effect, and GTAO is the other one: effects in a phase are recorded in
        // registration order, so the fog composite has to come after GTAO for FrameSchedule.PostAoTexture to already hold
        // this frame's ambient occlusion and for the composite to consume it exactly once and publish that it did.
        //
        // Registration is also where the tier becomes real: with VolumetricFog=Off, missing HDR offscreen color, a
        // backend that is not segmented, or no depth target, Initialize declines and the pass simply does not exist.
        // That is why nothing below or in the panel checks for it - Fog panel keeps writing SceneFog regardless, and the
        // effect is the only thing that decides whether anything consumes it.
fogEffect = new Season.Rendering.Effects.VolumetricFogEffect();
        if (!Season.Rendering.FrameSchedule.RegisterCompute(
                Season.Basic.Graphics.Instance, fogEffect))
        {
            // Declined: no HDR offscreen color, no depth target, no segmented scene, unsupported compute, or the tier is
            // Off. The object is not registered, so dropping the handle is all that is left to do - nothing disposed it
            // and nothing holds it.
            fogEffect = null;
        }

        // 第二阶段：区域雨雪替换无约束演示；仍作为最后一层透明间接绘制，不改动旧天空或树林雾。
        // 每区域桌面预算为雨 8192 / 雪 4096，移动端及 Web 减半，晴天和远离窗口的系统不派发。
        precipitation = new WeatherPrecipitationEffect(weather.Field,
            OperatingSystem.IsAndroid() || OperatingSystem.IsIOS() || OperatingSystem.IsBrowser());
        if (!FrameSchedule.RegisterCompute(Season.Basic.Graphics.Instance, precipitation))
            precipitation = null;
        weatherCurtain = new WeatherCurtainEffect();
        if (!FrameSchedule.RegisterCompute(Season.Basic.Graphics.Instance, weatherCurtain))
            weatherCurtain = null;

        // 闪电通道 V2：几何与 args 都在 FrameStart 里写完，绘制则挂在粒子提交列表上，
        // 与降水共用场景透明阶段的末尾——闪电必须画在雨幕之后、雾合成之前。
        // 没有 compute 或间接绘制的后端在这里失败，通道不出现，云内发光仍由主着色器给出。
        lightning = new LightningEffect();
        if (!FrameSchedule.RegisterCompute(Season.Basic.Graphics.Instance, lightning))
            lightning = null;

        // D1: 屏幕空间贴花。它不在 Compute 相位里，而是一个固定位置的独立 Pass（雾合成之前），
        // 所以注册顺序与上面的相位无关；后端没有实现时这里返回 false 并留下零残留。
        Season.Rendering.FrameSchedule.RegisterDecals(Season.Basic.Graphics.Instance);
    }

    /// <summary>
    /// Writes this frame's medium. Runs after fog.Update on purpose: the panel puts its own numbers in for this frame first,
    /// and this call is what the pass actually reads. Nothing here resets the volumetric history - extinction and wind are
    /// exactly the quantities the temporal filter exists to smooth - so the only history reset still in play is the panel's
    /// own preset switch.
    /// </summary>
    void ApplyMist()
    {
        var scene = Fog;
        if (scene == null) return;

        var world = WorldSettings.Current;
        float density = world.FogDensityScale > 0f && float.IsFinite(world.FogDensityScale) ? world.FogDensityScale : 1f;

        // Fog off and the density multiplier stay live knobs: they are the two settings rows that describe the medium
        // rather than the framing, and the pass itself is what the on/off switch has to reach.
        scene.Enabled = world.FogEnabled;

        // Extinction stays at zero: the world has no weather, only the patch below. Enabled above is what keeps the pass
        // alive, so the volumes are still injected and still composited.
        scene.Extinction = GlobalExtinction;
        scene.Anisotropy = MistAnisotropy;
        scene.MaxDistance = MistMaxDistance;
        scene.WindVelocity = MistWind;

        // The patch is the whole medium, so the density row scales it directly.
        forestMist.Extinction = ForestMistExtinction * density;
        // 树冠在清晨把直射光整根砍掉，雾只剩很低的环境光，于是整片雾被压成黑灰烟；瀑布水汽有自发光下限所以仍是白的。
        // 这里补的就是“雾被头顶天空照亮”的那部分，只在晨昏张开。
        forestMist.SelfIllumination = MistSkyTint * (MistSkyFloorPeak * MistSkyWeight(HourOfDay));
    }

    /// <summary>
    /// 晨昏权重：以日出（6 时）和日落（18 时）为中心的钟形，窗口边界处归零。
    /// 用钟形而不是“日出后固定值”，是因为白天环境光会自己把雾抬亮，而夜里补光会让雾变成一块发光的白盒。
    /// </summary>
    static float MistSkyWeight(float hour)
        => MathF.Max(Bell(hour, 6.3f, 2.6f), Bell(hour, 18.2f, 2.2f));

    /// <summary>1 at <paramref name="centre"/>, 0 beyond the half width, flat at both ends so the patch never pops.</summary>
    static float Bell(float hour, float centre, float halfWidth)
    {
        float t = Math.Clamp(MathF.Abs(hour - centre) / halfWidth, 0f, 1f);
        return 1f - t * t * (3f - 2f * t);
    }

    // Fully synchronous and intentionally free of blocking loads.
    // On Android, InitializeVulkan does not start the render loop until this method returns,
    // so every extra millisecond here directly becomes black or blue startup time before the first frame.
    // Heavy resources must go through Task.Run or the control loading queue.
    public override void Create()
    {
        base.Create();

        weather = new WeatherController(WorldSettings.Current);
        FrameSchedule.WeatherVisuals = weatherVisuals;
        RequestSaveSettings();
        RegisterEffects();

        var musics = new string[] { @"Musics/Cozy.wav", @"Musics/Forest.wav", @"Musics/Sweel.wav" };
        var music = musics[new Random().Next(0, 3)];
        string path = null;

        if (DeviceServices.Core.Platform is Season.Basic.Platform.Web)
        {
            path = music;
        }
        else
        {
            StorageService.CopyToLocal(music);

            path = StorageService.SubPath(StorageService.DirectoryBase, music);
        }

        DeviceServices.Media.PlayMedia("Music", path, "60");

        celestial = new CelestialLighting();
        celestial.Load();
        sky = new Sky(celestial);
        AddPanel(sky);

        // Scene medium right after the sky: it draws the sun-shaft colonnade, which is scenery and has to resolve depth
        // against the ground and the models that follow. Its constructor registers the local volumes on App.Fog, so it
        // must exist before the first frame that renders the volumetric pass.
        fog = new Fog();
        AddPanel(fog);

        // The forest patch joins the same scene medium once, here, so it is present from the first frame and in every
        // preset. Add only registers it: the density it contributes is written by ApplyMist below.
        Fog.Add(forestMist);

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

        forest = new Forest();
        AddPanel(forest);
        DeviceServices.BaseApp?.RequestLoad(forest);   // Queue the whole panel so Load can extract in the background.

        //// Background mountain ring: extract four mountain meshes from background_mountains.glb at runtime,
        //// then assign each one to an InstancedMesh3D for ring instancing. See Mountains.cs.
        mountains = new Mountains();
        AddPanel(mountains);
        DeviceServices.BaseApp?.RequestLoad(mountains);   // Queue the whole panel so Load can extract in the background.

        player = new Player();
        AddPanel(player);

        streetLight = new StreetLight();
        AddPanel(streetLight);

        // Campfire on the south-west meadow: log teepee + ember bed + flame/spark/smoke particles +
        // one flickering point light. See Campfire.cs for why this spot is empty.
        campfire = new Campfire();
        AddPanel(campfire);

        house = new House();
        AddPanel(house);

        littleHouse = new LittleHouse();
        AddPanel(littleHouse);

        hotAirBalloon = new HotAirBalloon();
        AddPanel(hotAirBalloon);

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

        // The pond sits at the head of the south branch, on the grass just south of the spawn: it is the water test bed
        // for the normal target, SSR, refraction and depth absorption, and it is also where the branch starts, so the
        // two waters meet through an opening in its south rim. PosZ is authored so the bottom step's outer face lands
        // exactly on the road's southern end (z = 0, the spawn itself), which is what lets the road run straight up the
        // pond's north bank; the rim is a ring of the river's own bank pieces and the water surface 0.4 m above the
        // grass. See Pond.cs and River.BranchCourse.
        pond = new Pond();
        pond.PosX = 0;
        pond.PosY = 0;
        pond.PosZ = Pond.StairFootZ;
        AddPanel(pond);

        // P2: 水面几何由着色器按 SV_VertexID 现算，所以这里注册的是数据而不是控件 ——
        // 矩形与位置由 Pond 自己维护（位置每帧跟着面板）。
        // 后端建不出水面管线时它只是一份没人读的数据，不会退化成错的画面。
        Waters.Add(pond.Water);

        // P3: 海面同样是一份矩形数据。Sea 的 255×255 起伏网格已经退到水面以下 12m 当水体底面，
        // 水面本身由 SceneWater Pass 画：Fresnel、折射、Hi-Z 屏幕空间反射与深度吸收全在着色器里。
        Waters.Add(sea.Water);

        CreateWaterfall();

        // 瀑布潭必须在 CreateWaterfall 之后：面板要引用刚登记的那条瀑布（落点锚定的唯一数据源，
        // 以及“瀑布是否已经 Load 完”这个门控 —— 首次摆放水汽盒要用到）。
        waterfallPool = new WaterfallPool(waterfalls[^1]);
        AddPanel(waterfallPool);

        // P1 河流。必须在 CreateWaterfall 之后：水面复用的是瀑布那三张同名贴图，
        // 下面再登记一次只是为了“场景里没有瀑布时河也能画”（RegisterNamedTexture 按名字去重）。
        //
        // 每一段都是独立的 Mesh3D：岸坡在绘制时就是世界坐标，Load 完之后 Pos 取自己的锚点，
        // 世界矩阵退化为单位阵，所以面板上的 PosX/PosY/PosZ + Width/Height/Depth + Rotation
        // 改的就是这一段本身；水面挂在 RiverSegment.Water.Placement 上读同一个矩阵，不会落后一帧。
        river = new River(Rivers);
        AddPanel(river);
        foreach (var texture in River.Textures)
        {
            RegisterNamedTexture(texture);
        }

        // Stone arch bridge over the main channel, just upstream of where the player spawns on the bank - the
        // visible span is the AI-generated Bridge.glb, and the walkable deck plus the two grass-to-deck stone
        // step flights (authored like Tower's stair) are built in code so the player can climb up and cross to
        // the far bank. Placed right after the river because its crossing centre and orientation are read off
        // the same course table. See Bridge.cs.
        bridge = new Bridge();
        AddPanel(bridge);

        // 维京小船泊在池北岸的台阶边：构造时从 pond 取池塘矩形与位置，所以水池移动时航道和泊位都自动跟着走。
        // 船头默认朝 +Z，正对 Player 走下来的那排台阶；跳上甲板即绑定位置，划出去后跳回岸上，船留在原地。
        // 海面段还给 sea：船在海里跟着可见浪面起伏与倾斜，而不是浮在恒定海平面上。
        boat = new Boat(pond, sea);
        AddPanel(boat);

        // Square observation tower on the open lawn southeast of the spawn: a 3x3 deck at 20 player heights, with a
        // single 3m flight filling one whole flank. The flight runs east-west, perpendicular to the shoreline, so it
        // keeps a constant distance from the sea and the climber keeps the sea to the side. A slim mast on the deck
        // carries a searchlight that sweeps the rock line offshore every night, so the tower is also the harbor
        // light. See Tower.cs.
        tower = new Tower();
        tower.PosX = 48f;
        tower.PosY = 0f;
        tower.PosZ = -26f;
        AddPanel(tower);

        // Seaside stair and road beside the pond's east dike: the pond's three stone steps turned to face the sea,
        // centered on the rim row, with the south road's asphalt running east from the stair foot to the coastal
        // rock line at x = 74. The flight leans against the bank pieces' outer face without intersecting them.
        // See SeaRoad.cs.
        seaRoad = new SeaRoad();
        AddPanel(seaRoad);

        // Round flagpole on the direct line between the haunted house and the runway, halfway along it: a 0.2 m
        // stone disc, a 10 m column half a player across, and a 2.4 x 1.6 m cloth at the top with a different
        // pirate picture printed on each face. Created before the picker below, which registers the column and the
        // cloth as ordinary selectable entities. See Flagpole.cs.
        // The parts are authored around the pole axis, so the panel position is what puts the pole on the lawn;
        // Flagpole.CenterX / CenterZ are that midpoint and FloorHeightAtWorld reads this same position back.
        flagpole = new Flagpole();
        flagpole.PosX = Flagpole.CenterX;
        flagpole.PosY = 0f;
        flagpole.PosZ = Flagpole.CenterZ;
        AddPanel(flagpole);

        CreatePondDecals();

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

        planes = new Planes();
        AddPanel(planes);

        // The player's aircraft on the seaside road: parked nose-to-sea at x = 14 on the same centre line the
        // asphalt follows, so a leap from the road lands on the fuselage deck and starts the automated trip.
        // Created before the picker below, which registers the airframe as an ordinary selectable entity.
        // See AeroPlane.cs.
        aeroPlane = new AeroPlane();
        AddPanel(aeroPlane);

        // P3 acceptance: the island scene's two extra cameras. Created here because platform initialization has already
        // registered the main view's offscreen attachments, so the mirror can copy its SceneColor format and MRT shape.
        // Its slots therefore exist before the first frame and the sensor textures do not appear a frame late.
        mirrors = new RearViews();
        AddPanel(mirrors);

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

        //debugPanel = new DebugPanel();
        //AddPanel(debugPanel);

        // Hover picking highlight: when the pointer lands on a target's screen projection,
        // pulse Alpha over time and draw a white translucent bounds box.
        // Targets are opt-in only; background ground, walls, and the skybox are excluded.
        // Update must run after all target panels.
        // InstancedTargets registers instanced controls, with hit testing at single-instance granularity for per-instance bounds and property editing.
        picker = new ObjectPicker();
        picker.Targets.Add(ground.grass);
        if (player != null)
        {
            picker.Targets.Add(player.model);
        }
        picker.Targets.Add(ball.model);
        picker.Targets.Add(ball.bee);
        picker.Targets.Add(streetLight.lightsPunctualLamp);
        picker.Targets.Add(house.model);
        picker.Targets.Add(littleHouse.model);
        picker.Targets.Add(hotAirBalloon.model);
        if (campfire != null)
        {
            picker.Targets.Add(campfire.logs);
            picker.Targets.Add(campfire.bed);
        }
        picker.Targets.Add(room.bottle);
        picker.Targets.Add(room.busterDrone);
        picker.Targets.Add(sphere.sphereRow);
        // 瀑布也走同一条路径：代理网格提供三角级拾取，Bounds/Wireframe/Outline 三种高亮与其它实体一致。
        // 这里用列表而不是单个字段，多条瀑布时只需往 waterfalls 里再 Add 一个。
        for (int i = 0; i < waterfalls.Count; i++)
        {
            picker.Targets.Add(waterfalls[i]);
        }
        // 水汽体积也是同一类目标：不可见的盒控件，拾取后可在属性面板里改 Pos/Width/Height/Depth/Rotation，
        // 改完下一帧就作用到雾的出生矩形上（见 WaterfallPool.Update）。
        if (waterfallPool != null)
        {
            picker.Targets.Add(waterfallPool.Mist);
        }
        // 河流的每一段各自是一个选取目标：直线段与弯道段都能单独拖动、改长宽高、转方向。
        // 高亮必须走 Wireframe（长条网格的世界 AABB 一旋转就“变胖”，见 RiverSegment 的类注释）。
        foreach (var segment in river.Segments)
        {
            picker.Targets.Add(segment);
        }
        // 两岸堤坝也可选，但粒度是单块：整条河一百多块共用一个 GLB 模板，按实例命中后选中的就是那一块，
        // 面板上的 Pos/Width/Height/Depth/Rotation 直接写进这个实例（见 ObjectPanel 的实例分支）。
        // 这些改动在 SyncDikes 里被当成相对自动布局的偏移保留，所以挪动整段河道时堤坝不会回到原样。
        picker.InstancedTargets.Add(river.Dikes);
        // 出海口的水汽盒也是同一类目标：不可见的盒子，拾取后在属性面板里改位置/长宽高/朝向，
        // 下一帧就作用到三组粒子的出生矩形上（见 River.UpdateMouthSpray：雾与白沫走盒底面，水花走盒顶面）。
        picker.Targets.Add(river.MouthMist);
        if (robots != null)
        {
            picker.InstancedTargets.Add(robots.robotField);
        }
        picker.InstancedTargets.Add(birds.seagullsModel);
        picker.InstancedTargets.Add(planes.planesModel);
        // 船也是普通可选实体：点击选中后可在属性面板里查看它，与房子/瀑布同一条路径。
        picker.Targets.Add(boat.Hull);
        // The player's aircraft is the same kind of entity: click it to inspect the airframe in the property panel.
        picker.Targets.Add(aeroPlane.body);
        // 旗杆与旗帜同样可选：杆柱和布面各算一个目标，点击后可在属性面板里查看位置与尺寸。
        picker.Targets.Add(flagpole.Pole);
        picker.Targets.Add(flagpole.Flag);
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
        // The pond rim and steps are walkable surfaces rather than obstacles: PlayerCollider lifts the player onto them,
        // refuses rises above one step, and keeps the footprint out of the water. See PlayerCollider.RetractLedge.
        collider.Pond = pond;
        // The tower deck and flight are walkable surfaces as well, so the shaft blocks ground travel by
        // height alone, and only the thin railings and stair parapets are obstacles. See Tower.cs.
        collider.Tower = tower;
        // The river's water blocks entry exactly like the pond's water, while its bank pieces are walkable floors the
        // player can jump onto and walk along - the same treatment the pond's rim gets, at the same height.
        collider.River = river;
        // The bridge deck and step flights are walkable floors the player climbs onto from either bank, and while
        // the feet stand on the deck the river's water below is ignored so the crossing is not blocked. See
        // Bridge.cs.
        collider.Bridge = bridge;
        // The seaside stair's three steps are walkable floors climbed one riser at a time from the east road, the
        // same treatment the pond steps get; the road itself lies 5 cm into the grass and needs no height source.
        collider.SeaRoad = seaRoad;
        // The flagpole's platform is a walkable step rather than an obstacle, so the player steps onto the disc and
        // is stopped by the column standing on it. See Flagpole.cs.
        collider.Flagpole = flagpole;
        // Only the column blocks. The 0.2 m disc is deliberately not an obstacle: a box around it would read as an
        // invisible step at the platform edge, the same reason the campfire's ash bed is skipped below.
        collider.Obstacles.Add(flagpole.Pole);
        foreach (var blocker in tower.Blockers)
        {
            collider.Obstacles.Add(blocker);
        }
        collider.Obstacles.Add(ball.model);
        collider.Obstacles.Add(ball.bee);
        collider.Obstacles.Add(streetLight.lightsPunctualLamp);
        collider.Obstacles.Add(house.model);
        collider.Obstacles.Add(littleHouse.model);
        // Only the teepee blocks; the ash bed is flat enough to walk over, and a 0.9 m disc of
        // collision would feel like an invisible step around the fire.
        if (campfire != null)
        {
            collider.Obstacles.Add(campfire.logs);
        }
        collider.Obstacles.Add(sphere.sphereRow);
        collider.Obstacles.Add(room.bottle);
        collider.Obstacles.Add(room.busterDrone);
        foreach (var part in room.roomParts)
        {
            if (part.Name is "RoomFloor" or "RoomCeiling" or "RoomRoof" || part.Name.StartsWith("RoomStair"))
                continue;

            collider.Obstacles.Add(part);
        }
        if (robots != null)
        {
            collider.InstancedObstacles.Add(robots.robotField);
        }

        // Occlusion-fade registration: start from every movement obstacle such as walls, the house, lamp posts, and the sphere row,
        // then add the roof and ceiling because they may block the camera view even though they do not block movement.
        // The floor is excluded because it sits on the ground and cannot realistically enter the camera sightline.
        // Instanced Robots are not included because there is no per-instance Alpha path and full occlusion by them is rare.
        fade = new OcclusionFade();
        foreach (var obstacle in collider.Obstacles)
            fade.Register(obstacle);
        // Tower structure: not obstacles because it is walkable, but the shaft and the stair mass do stand
        // between the camera and the player. The railings are already covered by the obstacle loop above.
        foreach (var part in tower.Occluders)
            fade.Register(part);
        // Bridge span: walkable, so not an obstacle, but the arch and parapets can stand between the camera and a
        // player crossing the deck, so it fades like the tower shaft.
        fade.Register(bridge.model);
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

    /// <summary>
    /// 把房间内腔交给天气场作为遮挡区域。降水区先消耗覆盖权重，因此室内棱柱内雨雪归零，
    /// 纵向只到天花板，屋顶以上仍按自身区域降水。
    /// </summary>
    void RefreshWeatherCover()
    {
        if (weather == null || room == null) return;
        var (min, max) = room.InteriorBounds;
        if (min == weatherCoverMin && max == weatherCoverMax) return;
        weatherCoverMin = min;
        weatherCoverMax = max;
        float halfX = (max.X - min.X) * 0.5f, halfZ = (max.Z - min.Z) * 0.5f;
        float centerX = (min.X + max.X) * 0.5f, centerZ = (min.Z + max.Z) * 0.5f;
        // 房间是方形、屋顶是双坡，内切椭圆会漏掉四角；这里用外接圆加过渡带，沿对角恰好盖住墙角，
        // 于是墙外只多出半个屋檐宽的干燥带，看上去就是屋檐本身的雨影。
        float corner = MathF.Sqrt(halfX * halfX + halfZ * halfZ);
        float feather = MathF.Max(1f, MathF.Max(halfX, halfZ) * 0.2f);
        weather.Cover = new WeatherRegionSettings
        {
            Id = "room-cover", Weather = WeatherKind.Clear, Shelter = true,
            CenterX = centerX, CenterZ = centerZ, RadiusX = corner + feather, RadiusZ = corner + feather,
            EdgeFeather = feather, Bottom = min.Y, Top = max.Y,
            // 云范围收到亚米级：遮挡区只压制雨雪，不能在天上的云层里开洞。
            CloudRadiusX = 0.001f, CloudRadiusZ = 0.001f, CloudBase = 0,
        };
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? width = null, float? height = null, float? posZ = null, float? depth = null)
    {
        // 逐像素 A/B 的取证钩子（见 AbCapture）：未启用时这两行分别是原样返回与一个静态布尔判断。
        // delta 必须在 base.Update 之前替换，否则 Time 已经按墙上时钟推进过一帧了。
        time = AbCapture.FrameDelta(time);
        AbCapture.Tick();

        base.Update(time, alpha: alpha, posX: posX, posY: posY, width: width, height: height);

        // Ctrl+S screenshot hotkey, polled from the frame keyboard snapshot base.Update just
        // pumped. This is NOT a system-wide hotkey: the platform keyboard service only sees
        // keys routed to this window and clears them on deactivation, so the shortcut works
        // only while the window is active and never blocks other apps' Ctrl+S.
        // Checked before every early return below, so a screenshot also works while the
        // settings or AI panel owns the frame.
        if (IsActive && KeyboardService.IsPressed(Key.S) && KeyboardService.Ctrl)
        {
            SaveScreenShot();
        }

        if (Mode is Mode.Show)
        {
            setting?.Alpha = 0f;
            direction?.Alpha = 0f;
            skill?.Alpha = 0f;
            painting?.Alpha = 0f;
            picker?.Alpha = 0f;
            views?.Alpha = 0f;
            mirrors?.Alpha = 0f;
        }
        else if (Mode is Mode.Play)
        {
            setting?.Alpha = 1f;
            direction?.Alpha = 1f;
            skill?.Alpha = 1f;
            painting?.Alpha = 0f;
            picker?.Alpha = 0f;
            views?.Alpha = 0f;
            mirrors?.Alpha = 1f;
        }
        else if (Mode is Mode.Edit)
        {
            setting?.Alpha = 1f;
            direction?.Alpha = 0f;
            skill?.Alpha = 0f;
            painting?.Alpha = 1f;
            picker?.Alpha = 1f;
            views?.Alpha = 0f;
            mirrors?.Alpha = 1f;

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
            mirrors?.Alpha = 1f;
        }

        // 在所有面板早退前消费请求，打开设置也能预览天气。time 本身是帧秒数，与昼夜倍率无关。
        RefreshWeatherCover();
        if (weather?.Update(WorldSettings.Current, time) == true)
            RequestSaveSettings();
        celestial?.UpdateWeatherBackground();
        bool regionalWeather = WorldSettings.Current.WeatherMode != WeatherMode.Showcase;
        if (weather != null)
        {
            weatherVisuals.Update(weather.Field, time, regionalWeather, Camera.Far);
            // 落雷边沿在本帧已经推进，雷声按声速延迟到闪电之后才响；展示模式没有区域闪电，不发声。
            thunderAudio.Update(time, weatherVisuals, CameraPos, regionalWeather && weather.LightningActive);
        }
        DayNightCycle.Evaluate(celestial?.DayPhase ?? 0, out _, out float daylight, out _, out _, out _, out _);
        if (precipitation != null)
        {
            precipitation.Enabled = regionalWeather;
            precipitation.Advance(time, daylight);
        }
        // 贴花的 Intensity 每帧都要重算：DecalEffect 每帧按当前值重新打包，所以写在这里就够，不需要通知。
        if (pondCaustics != null) pondCaustics.Intensity = CausticsIntensity * SunlightFactor(celestial?.DayPhase ?? 0);

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

        // The flagpole's red button is world geometry rather than an overlay control, so its tap is resolved before
        // the picker: a press that lands on the disc runs the flag and is consumed, while a miss leaves the release
        // for the picker to select the pole behind it. Same bottom-bar guard as the picker below.
        if (painting == null || !painting.MouseOver)
        {
            flagpole?.TryTapButton();
        }

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
        // In the balloon's basket the sync runs regardless of movement mode: the seat there hands over only a position,
        // so the per-frame camera facing is the one and only thing that turns the body inside the basket.
        if ((Movement == Movement.Character && Mode is Mode.Play or Mode.Debug)
            || (hotAirBalloon != null && hotAirBalloon.Aboard))
        {
            SyncPlayerYawToCamera();
        }

        skill?.Update(time);

        views?.Update(time);

        // The two extra cameras read the main camera's pose from the drag block above, so they track a mouse look in the
        // same frame. Their own sprites and picking also run here, before the clickable panels below get a chance to
        // consume the release that a click inside a sensor needs.
        mirrors?.Update(time);

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

        // Scene medium after the lighting driver: the wind drift is integrated from the frame delta that driver just
        // published, and a preset switch previews an hour through the same clock.
        fog?.Update(time);

        // The patch after the panel's own write, so the fixed numbers are the ones the pass reads this frame.
        ApplyMist();

        sky?.Update(time);

        // Ground is static; same pattern, with first-frame geometry upload and draw submission.
        ground?.Update(time);

        // Sea uses static geometry; each frame only synchronizes transforms and materials.
        sea?.Update(time);

        // Beach uses static instances; each frame only synchronizes instance matrices and materials.
        beach?.Update(time);

        // Coastal rocks are statically instanced and early-out internally until extraction finishes.
        rocks?.Update(time);

        forest?.Update(time);

        // Background mountains are statically instanced and early-out internally until extraction finishes.
        mountains?.Update(time);

        // The player's aircraft moves before Player, so the cockpit bind later in this frame reads the seat the
        // aircraft is actually drawn at; it also finishes its own pilot-exit arc there (Player.planeExit keeps the
        // bind off for that stretch). See AeroPlane.cs.
        aeroPlane?.Update(time);

        player?.Update(time);

        streetLight?.Update(time);

        campfire?.Update(time);

        house?.Update(time);

        littleHouse?.Update(time);

        hotAirBalloon?.Update(time);

        // The basket camera re-pins after the balloon has moved this frame: Player.Update above has already bound the
        // body to the seat, so the eyes are at this frame's spot and the first-person view does not lag the ride.
        UpdateBalloonCamera(time);

        ball?.Update(time);

        robots?.Update(time);

        birds?.Update(time);

        planes?.Update(time);

        //cubeField?.Update(time);
        //billboard?.Update(time);

        //bar?.Update(time);

        room?.Update(time);

        seaRoad?.Update(time);

        flagpole?.Update(time);

        pond?.Update(time);

        // 船要排在 pond 之后：它的水面高度与航道都来自池塘，且每帧自己算摇晃与动画状态。
        boat?.Update(time);

        tower?.Update(time);

        sphere?.Update(time);

        // 瀑布控件每帧一同步：把 Pos/Rotation/Scale 算成摆放矩阵交给水幕，并更新高亮的三条通道。
        // 水幕本身由 SceneWaterfall Pass 画，这里只管「对象」那一半。
        for (int i = 0; i < waterfalls.Count; i++)
        {
            waterfalls[i].Update(time);
        }

        // 瀑布潭必须接在瀑布之后：它的锚点是 sheet.Transform × 末控制点，要读本帧刚同步好的摆放矩阵；
        // Panel.Update 不会自动递归（见上方注释），漏掉这一行水面/贴花/石块/粒子就会停在默认值（原点、默认 8m 矩形）。
        waterfallPool?.Update(time);

        // 河流同理：每个河段各自是独立 Mesh3D，Panel 不会递归 tick，漏了这行高亮脉动与 Changed 就停在初值。
        // 真正的水面由 SceneWaterfall Pass 按 River 构造时注册好的 WaterRibbon 数据现算，与这里无关。
        river?.Update(time);

        bridge?.Update(time);

        meshes?.ForEach(me =>
        {
            me.Update(time);
        });

        // Run occlusion fading at the end so both camera and player positions have reached their final values for this frame,
        // keeping ray tests consistent with the rendered image.
        fade?.Update(time);

        //debugPanel?.Update(time);

        return false;
    }

    /// <summary>
    /// Ctrl+S screenshot: capture the current app frame and save it as a quality-90 JPEG at
    /// ScreenShot/yyyyMMddHHmmss.jpg. CaptureApp's completion source is single-slot and the
    /// readback needs a render-thread frame, so only one capture may be in flight; repeat
    /// presses are ignored until it finishes.
    /// </summary>
    void SaveScreenShot()
    {
        if (!screenShotTask.IsCompleted) return;
        screenShotTask = SaveScreenShotAsync();
    }

    async System.Threading.Tasks.Task SaveScreenShotAsync()
    {
        try
        {
            using var file = await DeviceServices.Record.CaptureApp();

            if (file != null)
            {
                var bytes = await DeviceServices.Image.SaveImageAsync(file, Season.Basic.ImageFormat.Jpeg, 90);

                StorageService.SaveFile(StorageService.DirectoryBase, $"ScreenShot/{DateTime.Now.ToDateTimeTicks()}.jpg", bytes);
            }
        }
        catch (Exception ex)
        {
            AddLog(LogType.Error, $"{DateTime.UtcNow} [ScreenShot] capture failed err={ex}");
        }
    }

    public override void Dispose()
    {
        if (IsDisposed) return;
        if (ReferenceEquals(FrameSchedule.WeatherVisuals, weatherVisuals)) FrameSchedule.WeatherVisuals = null;
        if (weatherCurtain != null) FrameSchedule.UnregisterCompute(weatherCurtain);
        if (precipitation != null) FrameSchedule.UnregisterCompute(precipitation);
        Dispose(true);
        base.Dispose();
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

    /// <summary>
    /// 瀑布的通用入口：拿一份形状（<paramref name="template"/>）生成一条独立的水幕控件，可选就地摆放。
    ///
    /// 一次调用做完五件事：拷作者数据（形状/材质/贴图名）、按需 <see cref="Waterfall.Place"/>、
    /// AddControl 排队 Load（到那一刻才建代理网格）、登记进场景瀑布表（真正画水的那一侧）、
    /// 登记进本 App 的 <see cref="waterfalls"/>（每帧 Update 与拾取目标）。
    /// 于是「多条瀑布」只是多写几行：同一份 template 换一个 center 就能再长一条。
    ///
    /// <paramref name="center"/> 为空表示不摆放：控制点里的参考坐标同时就是世界坐标（P1 的旧行为）。
    /// center 与属性面板的 PosX/PosY/PosZ 是同一个量（包围盒中心）；<paramref name="size"/> 是逐轴目标尺寸（米），
    /// 与面板的 Width/Height/Depth 同一套数，null 则各轴沿用 <paramref name="scale"/>。
    /// 视觉微调后把面板上的这几个数抄回来就是启动值。
    ///
    /// 三张 P0 资产也在这里登记：后端只按名字查字典，任一没注册就整条跳过（是「不显示」，
    /// 而不是把白纹理当成满值白沫烧成一片白）。名字相同只加载一次，见 <see cref="RegisterNamedTexture"/>。
    /// </summary>
    internal Waterfall SpawnWaterfall(string name, Season.Rendering.WaterfallSheet template,
        Vector3? center = null, float yawDegrees = 0f, float scale = 1f, Vector3? size = null)
    {
        var waterfall = new Waterfall(name);

        // 拷贝而不是共享：DeepColor/Opacity/SortOrder 这类参数要能逐条调，改一条不会波及整群。
        waterfall.Sheet.CopyFrom(template);

        if (center.HasValue) waterfall.Place(center.Value, yawDegrees, scale, size);

        AddControl(waterfall);
        Waterfalls.Add(waterfall.Sheet);
        waterfalls.Add(waterfall);
        RegisterNamedTexture(waterfall.Sheet.MaskTexture);
        RegisterNamedTexture(waterfall.Sheet.NormalTexture);
        RegisterNamedTexture(waterfall.Sheet.FlowTexture);

        return waterfall;
    }

    /// <summary>
    /// P1 瀑布演示：P0 选址探针定下的 12 行控制点（candidate #5，海岛山脉相对房子/鬼屋那一侧的内侧凹角，
    /// 与东岸 Rocks 保持距离，四条观察视线全部可见）。数据逐行照抄
    /// <c>.dbg/_waterfall_site/out/site_report.txt</c> 的 paste-ready 段。
    ///
    /// outward 恒为 (0.511, 0, 0.860)（背离岩面的水平方向），半宽从 1.30 涨到 2.50 —— 水流在下落中摊开；
    /// 弧长 17.97 m、落差 12.5 m。改形状只改这里：控制点进帧缓冲，着色器不需要重编译。
    ///
    /// 下面这 12 个点是探针给出的<b>原始世界坐标</b>，现在只当「形状模板」用：真正的启动位置与尺寸
    /// 由 tunedCenter / tunedSize 给出（视觉微调的实测读数），见那一块的注释。
    /// 要再挑位置：选中瀑布拖动或改数，把面板上的 PosX/PosY/PosZ 与 Width/Height/Depth 覆盖这两个变量即可。
    /// </summary>
    void CreateWaterfall()
    {
        var sheet = new Season.Rendering.WaterfallSheet
        {
            // 海水蓝绿的水体本色 + 近白泡沫。卡通风靠明度差把两条白边和水体分开，不靠高光。
            DeepColor = new(0.09f, 0.31f, 0.36f),
            FoamColor = new(0.93f, 0.98f, 1.00f),
            Opacity = 0.85f,
            ScrollSpeed = 0.55f,
            NormalStrength = 0.55f,
            NormalTiling = 2.5f,
            FoamAmount = 0.85f,
            // 落差 12.5 m 的长带：唇口/落点白边按比例收窄，否则中段会被两条白带啃没。
            LipFoam = 0.08f,
            FootFoam = 0.10f,
            EdgeFoam = 0.22f,
            EdgeSoftness = 0.14f,
            Erosion = 0.30f,
            Bow = 0.14f,
            // 截面是绕控制点连线的闭合环：Thickness 是总厚度，前后各分一半。
            // 崖面就在控制点连线上，所以后半个截面埋进岩体（被软件深度测试剔除），
            // 前半个截面鼓出来才是能看见的“水体”。1.0 m 对 5 m 宽、12.5 m 高的瀑布大约是一人高的水墙。
            // 想让瀑布更“实”就加这个数，代价是前壁也离崖面更远（远处看会显得往外飘）。
            Thickness = 1.0f,
            // 环向列数（含首尾重合的那一列）：12 段肉眼已看不出棱，两侧的切线过渡也不会太硬。
            Columns = 12,
            Subdiv = 2,
        };
        // 顺序必须从出水口到落点；v=0 是唇口、v=1 是落点，泡沫带按这个方向分配。
        sheet.Points.Add(new(new Vector3( -75.32f,  13.44f,  -74.70f), 1.30f, new Vector3( 0.511f, 0f,  0.860f)));
        sheet.Points.Add(new(new Vector3( -74.92f,  12.10f,  -74.03f), 1.66f, new Vector3( 0.511f, 0f,  0.860f)));
        sheet.Points.Add(new(new Vector3( -74.52f,  10.67f,  -73.36f), 1.81f, new Vector3( 0.511f, 0f,  0.860f)));
        sheet.Points.Add(new(new Vector3( -74.10f,   9.28f,  -72.65f), 1.93f, new Vector3( 0.511f, 0f,  0.860f)));
        sheet.Points.Add(new(new Vector3( -73.65f,   7.93f,  -71.88f), 2.02f, new Vector3( 0.511f, 0f,  0.860f)));
        sheet.Points.Add(new(new Vector3( -73.07f,   6.77f,  -70.92f), 2.11f, new Vector3( 0.511f, 0f,  0.860f)));
        sheet.Points.Add(new(new Vector3( -72.54f,   5.52f,  -70.02f), 2.19f, new Vector3( 0.511f, 0f,  0.860f)));
        sheet.Points.Add(new(new Vector3( -71.94f,   4.42f,  -69.00f), 2.26f, new Vector3( 0.511f, 0f,  0.860f)));
        sheet.Points.Add(new(new Vector3( -71.28f,   3.42f,  -67.90f), 2.32f, new Vector3( 0.511f, 0f,  0.860f)));
        sheet.Points.Add(new(new Vector3( -70.61f,   2.51f,  -66.77f), 2.39f, new Vector3( 0.511f, 0f,  0.860f)));
        sheet.Points.Add(new(new Vector3( -69.90f,   1.71f,  -65.58f), 2.44f, new Vector3( 0.511f, 0f,  0.860f)));
        sheet.Points.Add(new(new Vector3( -69.19f,   0.92f,  -64.38f), 2.50f, new Vector3( 0.511f, 0f,  0.860f)));

        // 视觉微调的实测读数（属性面板原样）：位置是包围盒中心，尺寸是包围盒的边长（米），绕 Y 无旋转。
        //
        // 这些值不能像上一版那样烘进控制点：尺寸是逐轴的（15 × 13 × 13），而控制点只描述位置，
        // 截面的半宽与厚度又是各向同性的标量，吸不掉 X 与 Y/Z 不同的缩放；而且这条水幕斜着横跨 XZ，
        // 只拉宽 X 会连带改变水流的走向 —— 这类各向异性只能由摆放矩阵做，也就是 Width/Height/Depth。
        // 于是控制点保持探针原值，摆放交给 Place：矩阵是 p → (p - 锚点)·缩放 + Pos，
        // 锚点（包围盒中心）正好落在 Pos 上，包围盒边长正好等于 tunedSize，面板读回来的还是这两组数。
        var tunedCenter = new Vector3(-75f, 6.8f, -73f);
        var tunedSize = new Vector3(15f, 14f, 13f);

        // 一条瀑布就是一调。同形状、换坐标再调一次就多一条（不同模板也可以），
        // 上限见 SceneWaterfalls.MaxActiveWaterfalls：
        //     SpawnWaterfall("Waterfall P2", sheet, new Vector3(x, y, z), yaw, scale, new Vector3(w, h, d));
        SpawnWaterfall("Waterfall P1", sheet, tunedCenter, yawDegrees: 0f, size: tunedSize);
    }

    /// <summary>
    /// D1 贴花演示：三块贴花各带一张 SeasonImage 生成的 RGBA 贴图（见 .dbg/_decal_assets）。
    /// 贴图是「乘数」而不是反照率：Modulate 走 dst*src 混合，把一张照片直接当 albedo 会把地面
    /// 压暗好几倍，所以两张地表图的亮度在生成器里已被压进 [0.5,1.0] / [0.62,1.0]，颜色仍留在图里，
    /// Tint 只做轻微偏向。alpha 由生成器画出有机轮廓，取代了原先着色器里的 4x4 调试棋盘格。
    ///
    /// 贴花只按名字查后端纹理字典，不会自己加载文件：名字未注册的那一帧整块贴花会被后端跳过，
    /// 所以要先把同一张图交给 <see cref="RegisterDecalTexture"/> 走既有的命名纹理路径。
    /// </summary>
    void CreatePondDecals()
    {
        // 池外西南侧的湿草：Modulate 且写粗糙度（水洼），这是 s9 的验证点。
        // 投影盒必须以受贴表面为中心、上下各留余量：盒面恰好落在表面上时，深度重建的误差加上 TAA 抖动
        // 会让 |local.y|≤1 的判定逐行翻转，表现为闪烁的条纹。草地在 y=0。
        var wetGrass = new Season.Rendering.DecalVolume
        {
            Mode = Season.Rendering.DecalMode.Modulate,
            Texture = "Assets/DecalWetGrass.png",
            Position = new(-10f, 0f, -15f),
            Size = new(7f, 0.4f, 7f),
            Tint = new(0.94f, 0.97f, 1.00f),
            Opacity = 0.9f,
            AngleFade = 0f,
            // 轮廓已经由贴图 alpha 画好，盒边羽化再压下去就只剩一个小圆点了。
            EdgeFade = 0.15f,
            AffectRoughness = true,
            Roughness = 0.04f,
            SortOrder = 0,
        };
        Decals.Add(wetGrass);
        RegisterNamedTexture(wetGrass.Texture);

        // 北侧池沿顶面的苔藓：只调制颜色。池沿顶面在 Pond.RimTop（堤坝件高 0.86 米埋入 0.10 米后为 0.76），
        // 盒子以它为中心；池沿内外侧壁有一截落进盒里，正好用来看掠射淡出。北带中心在池心 +4.775 米处。
        var moss = new Season.Rendering.DecalVolume
        {
            Mode = Season.Rendering.DecalMode.Modulate,
            Texture = "Assets/DecalMoss.png",
            Position = new(0f, Pond.RimTop, -2.275f),
            Size = new(9f, 0.2f, 1.6f),
            Tint = new(0.88f, 1.00f, 0.86f),
            Opacity = 0.85f,
            AngleFade = 0.25f,
            EdgeFade = 0.2f,
            SortOrder = 1,
        };
        Decals.Add(moss);
        RegisterNamedTexture(moss.Texture);

        //池底的焦散：Additive，透明度 0.35 的池水会盖在它上面，同时验证“贴花在透明之前”。
        // 贴图本身已经是「黑底上的光网」，像素就是光，所以 Tint 只带到接近白，强度靠 Intensity 收。
        // 池底就是 y=0 的草地，水面在 0.4：盒子以池底为中心，顶端 0.15 仍在水面以下。
        // Intensity 这里只是设计值，实际亮度每帧由 Update 按阳光高度重算（见 CausticsIntensity / SunlightFactor）。
        pondCaustics = new Season.Rendering.DecalVolume
        {
            Mode = Season.Rendering.DecalMode.Additive,
            Texture = "Assets/DecalCaustics.png",
            Position = new(0f, 0f, -7.05f),
            Size = new(9f, 0.3f, 7.2f),
            Tint = new(0.80f, 0.95f, 1.00f),
            Opacity = 0.5f,
            AngleFade = 0.1f,
            EdgeFade = 0.25f,
            Intensity = CausticsIntensity,
            SortOrder = 2,
        };
        Decals.Add(pondCaustics);
        RegisterNamedTexture(pondCaustics.Texture);
    }

    /// <summary>正午直射时的焦散强度设计值。</summary>
    const float CausticsIntensity = 0.5f;

    /// <summary>
    /// 直射阳光倍率：色散焦散是阳光经水面折射汇聚出来的亮网，不是自发光，亮度必须跟着太阳走。
    /// 太阳越高越强、接近地平线时几乎全部被水面按菲涅尔反射掉，所以用高次幂而不是线性，
    /// 并带一个起始阈值，避免日没前后在地平线附近拖出一条微亮的贴花。
    /// 月光焦散太弱，不参与。
    /// internal：潭底焦散（WaterfallPool）与池底焦散共用这一条曲线，两处亮度永远同源。
    /// </summary>
    internal static float SunlightFactor(float phase)
    {
        DayNightCycle.Evaluate(phase, out _, out float sunElev01, out _, out _, out _, out _);
        float rise = Math.Clamp((sunElev01 - 0.04f) / 0.30f, 0f, 1f);
        return rise * rise * MathF.Pow(sunElev01, 1.5f);
    }

    /// <summary>
    /// 把贴图名字注册进后端纹理字典。贴花与水幕都只按名字查字典、不会自己加载文件，
    /// 名字没注册就会被整块跳过，所以这里复用既有的命名纹理路径：一张永远不画的 Sprite2D 用同一个名字加载。
    ///
    /// 注册时同步解码一次：AddControl 走的是后台队列（BaseApp.RequestLoad → 消费线程），只排队的话
    /// 贴图要过几秒才进字典，而水幕/河流的第一帧就要按名字取样，期间整条水带按“缺表即跳过”的约定不画。
    /// LoadSprite2D 内部是同步解码，队列里那条稍后会命中同一个缓存键，不会重复解码。
    ///
    /// 浏览器是唯一例外：WASM 单线程运行时没有 Monitor，任何 Task.Wait / GetResult 都会抛
    /// PlatformNotSupportedException（见下方），而浏览器取图本身也只能走异步 fetch，同步等不到任何东西。
    /// 那里只保留 AddControl 排进队列的那一条，由图元队列逐帧解码（BaseApp.ProcessControlQueueFrame）；
    /// 代价是水幕在最初几帧还没图、按缺表约定不画，等字典填上后自然出现。
    ///
    /// 按名字去重：多条瀑布共用同一套三张贴图，不去重就是几个空 Sprite2D 各自加载同一份文件。
    /// internal：WaterfallPool 的潭底两张贴花走同一条命名纹理路径，不再另开加载链。
    /// </summary>
    internal void RegisterNamedTexture(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!namedTextures.Add(name)) return;

        var sprite = new Season.Controls.Sprite2D
        {
            Name = name,
            // LoadSprite2D 内部把 Width/Height 转成 int，null 会被吞成一次“加载失败”，这里给实数。
            Width = 1,
            Height = 1,
            // 纯载体：Alpha 为 0 时 Draw 直接返回，不会出现在屏幕上。
            Alpha = 0f,
            Enable = false,
        };
        AddControl(sprite);

        // 浏览器：AddControl 已经把它排进队列，那一条就是它唯一的解码路径 —— 这里若再直接调一次
        // LoadSprite2D，两次都会走到 AddRef，队列那条稍后落空就是引用计数收支不一致。
        if (OperatingSystem.IsBrowser()) return;

        try
        {
            Season.Basic.Graphics.Instance.LoadSprite2D(sprite).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            AddLog(LogType.Error, $"[Texture] '{name}' LoadSprite2D threw: {ex}");
        }

        // 三张表缺任意一张，瀑布与河流就整段跳过，而后端失败是静默的（只是不画）。
        // 缺图时把“文件在不在”一起写进日志，省得再猜是寻址错还是解码错。
        if (sprite.OriginWidth <= 0 || sprite.OriginHeight <= 0)
        {
            string baseDir = Season.Storage.StorageService.DirectoryBase;
            bool onDisk = Season.Storage.StorageService.FileExist(baseDir, name);
            AddLog(LogType.Error, $"[Texture] '{name}' not decoded: origin={sprite.OriginWidth}x{sprite.OriginHeight} onDisk={onDisk} base='{baseDir}'");
        }
    }

}
