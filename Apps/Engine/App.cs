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

    /// <summary>Current day/night phase in (0..1). WaterfallPool's pool-floor caustics share the same source as the Pond caustics, and their brightness is recomputed every frame from the sunlight height.</summary>
    internal float DayPhase => celestial?.DayPhase ?? 0f;

    // Scene-medium driver: the three authored fog presets, the local volumes they use, and the colonnade the sun-shaft
    // preset needs as an occluder. Update runs right after celestial because the wind drift shares that clock's frame
    // delta and simulation scale rather than reading App.Time.
    internal Fog fog;

    // The registered volumetric pass, or null when the tier or the backend declined it at registration time. Held only
    // for the diagnostics row in the settings panel (grid size, history weight), which is the one place that needs to
    // report what was actually built rather than what was asked for.
    internal Season.Rendering.Effects.VolumetricFogEffect fogEffect;

    // Null when the backend declines compute/indirect; clear weather does not submit the previously unconstrained demo particles.
    internal WeatherPrecipitationEffect precipitation;

    internal WeatherController weather;
    readonly WeatherVisualState weatherVisuals = new();
    // Thunder sound for stormy weather: the strike edge comes from weatherVisuals, and the sound played is a static WAV in Resources/Raw/Musics.
    readonly ThunderAudio thunderAudio = new();
    WeatherCurtainEffect weatherCurtain;
    // Lightning channel V2: each strike indirect-draws the channel geometry once as a ribbon strip; keeps null on registration failure, falling back to v1's in-cloud glow.
    LightningEffect lightning;

    // The room interior last synced to the weather field; the occlusion region record is rebuilt only after the room is scaled or moved.
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
    // The patch covers the grove (x in [-60, -40], z in [-30, -10], trees 2 m tall) with about 33 m of clearance on every
    // side, so its box never reads as a box. Density is set so ~7 m inside it is already half opaque, while a viewer
    // standing outside sees a lit bank between the trunks rather than a wall.
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

    // Depth range and wind are medium-wide by construction - the froxel layout carries neither per volume - so they stay
    // here even though the global extinction is zero. Anisotropy is no longer one of them: FogVolume.Anisotropy overrides
    // it per patch, and this value is only what the volumes that do not author one inherit.
    //
    // 0.75 rather than 0.60: a shaft only reads as a shaft when the phase function strongly favours looking into the sun.
    // The two-lobe FogMath.Phase at g = 0.75 evaluates to 21.1x isotropic looking into the sun and 0.94x looking away, a
    // forward-to-back ratio of 22:1. The single lobe it replaced gave 28x and 0.082x, i.e. 343:1 - punchier by a quarter,
    // but a patch under it collapsed to a flat sheet whenever the sun was behind the camera, which is half of the orbit
    // and the half where mist is actually looked at. 22:1 keeps the beam and leaves the other side lit. Clamped at 0.9 by
    // both SceneFog.Anisotropy and FogVolume.Anisotropy.
    const float MistAnisotropy = 0.75f;
    const float MistMaxDistance = 400f;
    static readonly Vector3 MistWind = new(0.30f, 0f, 0.10f);

    // Density at the centre of the patch, before the density multiplier. Denser than a scene-wide medium on purpose: it
    // only has 22 m to work with, so the contrast has to come from the near field.
    //
    // 0.10 rather than 0.06: in-scattering is sigma_s * illumination, so this number scales the beam as well as the
    // extinction. At 0.06 the single-scattering term came out at the same magnitude as the ambient term and the floor
    // below, which is why the patch read as one evenly lit volume with no structure in it. 0.10 puts ten metres at about
    // two thirds opaque (1 - exp(-1)) and the direct term clearly above both of the light-independent ones.
    const float ForestMistExtinction = 0.10f;

    // Multiple-scattering gain on the sky term, not a fixed emission floor. The shader only ever gives the patch one
    // bounce of ambient, while real water vapour bounces the sky many times; multiplying the ambient by this number is
    // that approximation, which is why the coefficient sits next to the extinction below rather than next to a clock.
    //
    // It replaces a dawn/dusk-only bell window, which left exactly the worst hours uncovered: at 09:00 the window has
    // just closed and the 2 m trees already throw 2 m shadows across a 4 m grid, so the whole patch sat in canopy shadow
    // with ambient 0.09 and read as black smoke at 44:1 against the direct term. Scaling with ambient instead of with
    // the hour also keeps night dark for free - ambient falls to its floor, so this term does too, and the patch never
    // turns into a glowing white box.
    //
    // 10 puts a shaded patch near 0.9 in HDR while the sunlit beam stays around 8, so the shaft keeps roughly 9:1.
    // Raise it for whiter vapour, lower it for a denser beam.
    const float MistScatterBoost = 10f;

    // Slightly cool, matching forestMist.Albedo's blue-white instead of fighting the warm ambient, which is what the
    // sky term above is now derived from.
    static readonly Vector3 MistSkyTint = new(0.97f, 1.00f, 1.06f);

    readonly FogVolume forestMist = new()
    {
        Shape = FogVolumeShape.Box,
        // Doubled horizontally around the same centre, and grown upward only: the floor stays at -0.2, so all of the extra
        // height sits above the trees rather than half of it under the ground. Position is the centre, hence 4.0 (floor
        // -0.2 plus half of 8.4) instead of 1.9.
        Position = new Vector3(-50f, 4.0f, -20f),
        // 88 x 8.4 x 88, twice the previous extent on every axis. Extinction is untouched: density per metre does not change
        // when the box grows, only the reach. EdgeFeather is a fraction of the half-extent rather than a distance, so the fade
        // now spans 22 m instead of 11 and the fully dense core is still the original 44 m grove, with the new ring entirely
        // inside the fade. The cost is vertical: with a 4.2 m half-height the ground under the trunks falls from 9.5% to
        // 2.5% of full density (smoothstep, not linear) and only recovers to full at y=2, which is exactly the band the
        // shafts are read in. Dropping EdgeFeather to 0.25 restores the old 11 m fade if that band reads too thin.
        Size = new Vector3(88f, 8.4f, 88f),
        Extinction = ForestMistExtinction,
        EdgeFeather = 0.5f,
        // 0.35 rather than 0.5, and 0.03 rather than 0.05: the noise modulates sigma before the light is integrated, so it
        // multiplies the beam too. Strong fine-grained noise shreds a shaft into speckle; larger, weaker banks let the beam
        // stay continuous while the patch still moves.
        NoiseAmount = 0.35f,
        NoiseScale = 0.03f,
        Albedo = new Vector3(0.86f, 0.90f, 0.96f),   // Slightly darker than the sky: a pure-white patch vanishes at noon.
    };

    internal Mountains mountains;

    internal Player player;

    internal StreetLight streetLight;

    internal House house;

    // Windmill cottage: standing on the grass west of the branch, between the bridge and the foot of the southern ridge; the model's single built-in animation (windmill blades rotating) plays on a loop.
    // See LittleHouse.cs.
    internal LittleHouse littleHouse;

    // Hot air balloon moored just off the tower deck on the house side, 8 player heights tall. See HotAirBalloon.cs.
    internal HotAirBalloon hotAirBalloon;

    Ball ball;

    Sphere sphere;

    internal Room room;

    internal Pond pond;

    internal Tower tower;

    // Pond-floor caustics decal. An Additive decal is itself independent of lighting, so Update writes the sunlight factor into Intensity every frame;
    // otherwise, once the scene darkens at night, it would only look brighter because of the contrast.
    Season.Rendering.DecalVolume pondCaustics;

    internal Campfire campfire;

    // Waterfall pool: water surface + pool-floor decal + stone rim + three particle groups (spray/mist/foam), with the anchor following the waterfall's landing point every frame. See WaterfallPool.cs.
    internal WaterfallPool waterfallPool;

    // River: winds all the way from the waterfall pool to the sea on the Rocks side. The entire course is a fixed table (see River.Course),
    // split into six selectable Mesh3D segments by "straight / curve"; the water surface is computed on the fly by the SceneWaterfall Pass from WaterRibbon data;
    // the grassy earth dikes on both banks are one batch from RiverBank.glb (River.Dikes), with each segment folding its own matrix in every frame.
    internal River river;

    // Stone arch bridge over the main river channel, at the spot the player first stood (halfway from the
    // south-branch confluence to the waterfall pool). The visible span is an AI-generated GLB (SeasonImage ->
    // SeasonThreeD, see .dbg/_bridge_assets); the walkable deck and its two grass-to-deck stone step flights
    // are authored in code so the player can climb up and cross. High enough for a boat to pass beneath. See
    // Bridge.cs.
    internal Bridge bridge;

    // Seaside stair and straight road beside the pond's east dike: three flights of stone steps matching the Pond steps, facing the sea, centered on the dike row; the road runs from the foot of the stair
    // along z = -7.05 to the coastal rock band at x = 74, with asphalt texture from the same source as the south road. See SeaRoad.cs.
    internal SeaRoad seaRoad;

    // Round flagpole midway between the haunted house and the runway: the base is a walkable circular platform, and a thin cloth flag with a different pirate picture on each face hangs at the top of the pole.
    // See Flagpole.cs.
    internal Flagpole flagpole;

    // Viking boat: moored by the steps on the pond's north bank, directly below the Player spawn point; the model's built-in morph animation drives the oars and the sail.
    // At rest, Boat computes three low-frequency rockings itself; after jumping onto the deck the Player is bound to the seat and the arrow keys drive Boat.Steer.
    // The navigable waters (pond / branch / main channel / under the bridge / river mouth / sea surface / waterfall pool) are listed in Boat.cs.
    internal Boat boat;

    // Sailboat beyond the east-shore rocks: a single Model whose height follows Sea.SurfaceY; the sail colour changes every 5 seconds (red, orange, yellow, green, cyan, blue, violet; a 35-second cycle).
    // It is the first use case of "changing base colour per part", see SailingBoats.cs.
    internal SailingBoats sailingBoats;

    internal Robots robots;

    internal Birds birds;

    // Plane squadron: ten planes in a wedge of four columns (1/2/3/4), flying in a circle around the island above the sea outside it, at the same height as the tower deck,
    // with each plane's propeller spinning continuously. See Planes.cs.
    internal Planes planes;

    // The player's aircraft on the seaside runway: parked on the asphalt road beside the pond's east dike, nose toward the sea; after the Player jumps into the cockpit the whole flight completes automatically
    // (3-2-1 countdown, taxi, takeoff at mid-field, climb past Rocks, bank left to join the Planes' island orbit for three laps, dive back to the runway along the same path,
    // taxi back to the parking spot and turn around). See AeroPlane.cs.
    internal AeroPlane aeroPlane;

    CubeField cubeField;

    internal Logo logo;

    Skill skill;

    Painting painting;

    internal List<Mesh3D> meshes = new List<Mesh3D>();

    // P1 waterfall: the control provides only the proxy mesh (picking/bounds/outline) and the placement matrix, while the actual water curtain is still drawn by the SceneWaterfall Pass.
    // A list rather than a single field, so that "multiple waterfalls" differ by only one Add: the same Sheet can be cloned to several controls,
    // each Placed at a different coordinate; for the registry limit see SceneWaterfalls.MaxActiveWaterfalls.
    internal List<Waterfall> waterfalls = new List<Waterfall>();

    // Texture names already handed to the backend (see RegisterNamedTexture). Decals and waterfalls often share one texture, so one entry per name suffices.
    readonly HashSet<string> namedTextures = new();

    internal Views views;

    // P3 acceptance: two secondary views over this scene - a rear-view mirror and a minimap - drawn into their own
    // fixed-size attachments and displayed as ordinary overlay sprites. Created once in Create so both view slots and
    // their published textures live for the whole session; a backend that declines them leaves this null and the app
    // keeps its single-view look.
    internal RearViews mirrors;

    Setting setting;
    internal SettingPanel settingPanel;

    // Day/night dial between the Settings button and the rear-view mirror. See Clock.cs.
    Clock clock;

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

        // Contract 3-1: hand the island to the cost runner. Guarded so an ordinary launch does not even build the
        // scenario table: without SEASON_PROFILE the runner is disabled and this is the entire cost of the feature.
        if (CostProfile.Enabled) CostProfile.RegisterScene(new CostProfileScene());

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
        // The desktop sample runs High; mobile and the browser stay on Low. Low collapses the shadow taps to one, which is
        // what the weaker mobile GPUs and the browser's single-threaded backend want. High is the tier the shafts are
        // authored against, and the reason is depth rather than width: at this sample's 400 m range the exponential slice
        // layout puts a boundary every 2.0 m at 20 m out with Medium's 64 slices, and every 1.35 m with High's 96. A trunk's
        // shadow crossing the view ray spans roughly that much depth, so 64 slices average it back into the surrounding
        // mist while 96 keep it. Columns going from 16 to 12 pixels is the secondary gain. A saved setting still wins,
        // because Init reads Settings.json after this and only snapshots these values when the file has no key of its own.
        RenderQuality.DefaultVolumetricFog = OperatingSystem.IsAndroid() || OperatingSystem.IsBrowser()
            ? FogQuality.Low
            : FogQuality.High;

        // The D1 decal is likewise an initialization-time tier: it decides whether the backend splits the scene into opaque/transparent sections for decals, and whether
        // SceneDepth gets synthesized — neither can be rescued at runtime, so it is enabled here.
        // Without this switch decals are only data and never appear on screen.
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
        // A7: per-instance GPU culling. Phase=FrameStart and Scope=App, because the dispatch must lie outside any Pass (Vulkan forbids
        // dispatch inside a Pass, and Metal's compute and render encoders are mutually exclusive), and the cascade atlas is a single copy per frame that should not be culled repeatedly per view.
        // On registration failure (backend has no kernel source, or lacks indirect-draw capability) InstanceCull.Enabled stays false,
        // so each backend's instanced group allocates no culling resources and shadows naturally fall back to whole-batch CPU culling rather than drawing from an empty buffer.
        if (Season.Rendering.FrameSchedule.RegisterCompute(
                Season.Basic.Graphics.Instance, new Season.Rendering.InstanceCullEffect()))
        {
        }

        // P1: per-instance GPU skeletal evaluation. Phase=FrameStart and Scope=App: the palette must be fully written before the scene Pass, while Vulkan forbids
        // dispatch inside a Pass and Metal's compute and render encoders are mutually exclusive, so it can only go in Record; there is one palette per frame and it should not repeat per view.
        // On registration failure (backend has no kernel source, or cannot bind the storage buffer into the vertex stage) InstanceSkinning.Enabled stays false,
        // so each backend's InstancedModel falls back to whole-model CPU replay rather than drawing a palette nobody wrote.
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

        // A1: register the motion-blur gather.
        // It has to sit between the two effects above and below, for the same registration-order reason TAA does:
        // after TaaEffect, so it blurs the resolved image instead of feeding a smeared image into TAA's history and
        // smearing the subpixel jitter along every streak; and before BloomEffect, so the highlights the bloom chain
        // extracts are the streaked ones. It is the second publisher of SceneColorOverride, which is a per-frame
        // chain rather than a slot - MotionBlurEffect documents the rule it follows when it stands down.
        // Missing velocity, missing SceneDepth, a missing offscreen SceneColor, or the tier being off all degrade to
        // a registration-time bypass with zero residue.
        if (Season.Rendering.FrameSchedule.RegisterCompute(
                Season.Basic.Graphics.Instance, new Season.Rendering.Effects.MotionBlurEffect()))
        {

        }

        // F1 Step B: register the depth-of-field gather.
        // It sits between the two effects above and below for the same registration-order reason: after
        // MotionBlurEffect, so the lens blurs the exposure-correct image rather than the velocity field (and so the
        // streak is gathered once instead of once per circle-of-confusion tap); and before BloomEffect, so the bloom
        // chain sees the final, defocused highlights and the other order cannot smear a glow into a soft halo.
        // It is the third publisher of SceneColorOverride, which is a per-frame chain rather than a slot -
        // DepthOfFieldEffect documents the rule it follows when it stands down.
        // The tier being Off, a missing explicit SceneDepth, a missing offscreen SceneColor, or a backend without
        // compute all degrade to a registration-time bypass with zero residue.
        if (Season.Rendering.FrameSchedule.RegisterCompute(
                Season.Basic.Graphics.Instance, new Season.Rendering.Effects.DepthOfFieldEffect()))
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

        // L2.1: occlusion pyramid. Same four-level downsample as Hi-Z, but into two ping-ponged layers and for the main view
        // only, so a later culler can test against last frame's pyramid while this frame's is rebuilt. It has no consumer yet,
        // so it is registered only when SEASON_OCCLUSION_PYRAMID asked for it - otherwise the four dispatches are pure cost.
        // It is deliberately independent of SSR and therefore sits outside the block above: it neither requires nor feeds HiZ.
        if (Season.Rendering.Effects.OcclusionPyramidEffect.Requested)
        {
            Season.Rendering.FrameSchedule.RegisterCompute(
                Season.Basic.Graphics.Instance, new Season.Rendering.Effects.OcclusionPyramidEffect());
        }

        // P0: SSR. Right after Hi-Z — it references that four-level pyramid by name, and a reversed registration order would fetch a texture not yet built.
        // Its reflection-delta composite entry point is attached by itself to FrameSchedule.Prepare/RenderSsrComposite;
        // on registration failure (the backend has no additive-composite capability, no SceneNormal, or the sky is not procedural) both delegates stay null,
        // and the scene Pass is not split either, so the image stops at the sky + ground reflection the main shader already computed.
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

        // Second phase: regional rain/snow replaces the unconstrained demo; still the last transparent indirect draw, without touching the old sky or forest mist.
        // The per-region desktop budget is 8192 rain / 4096 snow, halved on mobile and Web; systems that are clear or far from the window are not dispatched.
        precipitation = new WeatherPrecipitationEffect(weather.Field,
            OperatingSystem.IsAndroid() || OperatingSystem.IsIOS() || OperatingSystem.IsBrowser());
        if (!FrameSchedule.RegisterCompute(Season.Basic.Graphics.Instance, precipitation))
            precipitation = null;
        weatherCurtain = new WeatherCurtainEffect();
        if (!FrameSchedule.RegisterCompute(Season.Basic.Graphics.Instance, weatherCurtain))
            weatherCurtain = null;

        // Lightning channel V2: the geometry and args are fully written in FrameStart, while the draw is attached to the particle submission list,
        // sharing the end of the scene's transparent phase with precipitation — lightning must be drawn after the rain curtain and before the fog composite.
        // Backends without compute or indirect draw fail here, the channel does not appear, and the in-cloud glow is still produced by the main shader.
        lightning = new LightningEffect();
        if (!FrameSchedule.RegisterCompute(Season.Basic.Graphics.Instance, lightning))
            lightning = null;

        // D1: screen-space decals. They are not in a Compute phase, but a standalone Pass at a fixed position (before the fog composite),
        // so their registration order is independent of the phases above; when the backend does not implement them, this returns false and leaves zero residue.
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
        float sigma = ForestMistExtinction * density;
        forestMist.Extinction = sigma;
        // Emission shares sigma with the scattering term, so it is already per metre and the density multiplier scales
        // both together: a thicker patch reads brighter but also more opaque, exactly as the single-scatter term does.
        // Ambient is read before sky?.Update runs this frame, so this lags one frame - it moves over tens of seconds,
        // which makes the lag invisible.
        forestMist.SelfIllumination = MistSkyTint * (sigma * SkyLuminance(Lighting.Ambient) * (MistScatterBoost - 1f));
    }

    /// <summary>
    /// Grey level of the ambient term, Rec.709 weights. The sky contribution is a colour in the lighting state but the
    /// multiple-scattering coefficient is a single number, so it is collapsed here once instead of per component.
    /// </summary>
    static float SkyLuminance(Vector4 ambient)
    {
        float value = ambient.X * 0.2126f + ambient.Y * 0.7152f + ambient.Z * 0.0722f;
        return float.IsFinite(value) ? MathF.Max(value, 0f) : 0f;
    }

    // Fully synchronous and intentionally free of blocking loads.
    // On Android, InitializeVulkan does not start the render loop until this method returns,
    // so every extra millisecond here directly becomes black or blue startup time before the first frame.
    // Heavy resources must go through Task.Run or the control loading queue.
    public override void Create()
    {
        base.Create();

        // F1: seed the main lens from the persisted tier defaults. The effect reads Camera3D every frame and never these
        // fields, so this is a one-time handoff and a later scene write to Camera.Aperture/FocusDistance simply wins.
        // Done here rather than in the constructor because RenderQuality is only populated once the settings are loaded.
        Camera.Aperture = Season.Rendering.RenderQuality.Current.DofAperture;
        Camera.FocusDistance = Season.Rendering.RenderQuality.Current.DofFocusDistance;

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

        // Sailboat beyond the rocks: ordered after sea and rocks; its waterline is taken directly from Sea.SurfaceY.
        sailingBoats = new SailingBoats(sea);
        AddPanel(sailingBoats);

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

        // P2: the water surface geometry is computed on the fly by the shader from SV_VertexID, so what is registered here is data rather than a control —
        // the rectangle and position are maintained by Pond itself (the position follows the panel every frame).
        // When the backend cannot build the water pipeline it is just data nobody reads, and does not degrade into a wrong image.
        Waters.Add(pond.Water);

        // P3: the sea surface is likewise rectangle data. Sea's 255×255 undulating mesh has already dropped 12m below the water surface to act as the water body floor,
        // while the surface itself is drawn by the SceneWater Pass: Fresnel, refraction, Hi-Z screen-space reflection, and depth absorption are all in the shader.
        Waters.Add(sea.Water);

        CreateWaterfall();

        // The waterfall pool must come after CreateWaterfall: the panel needs to reference the waterfall just registered (the sole data source for anchoring the landing point,
        // as well as the "has the waterfall finished loading" gate — needed for the first placement of the mist box).
        waterfallPool = new WaterfallPool(waterfalls[^1]);
        AddPanel(waterfallPool);

        // P1 river. Must come after CreateWaterfall: the water surface reuses the waterfall's three same-named textures,
        // and registering them again below is only so that "the river can be drawn even when the scene has no waterfall" (RegisterNamedTexture deduplicates by name).
        //
        // Each segment is an independent Mesh3D: the bank slope is already in world coordinates when drawn, and after Load the Pos takes its own anchor,
        // the world matrix degenerating to identity, so the panel's PosX/PosY/PosZ + Width/Height/Depth + Rotation
        // changes that very segment; the water surface hangs off RiverSegment.Water.Placement reading the same matrix, so it never lags a frame behind.
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

        // The Viking boat is moored by the steps on the pond's north bank: at construction it takes the pond rectangle and position from pond, so when the pond moves both the channel and the berth follow automatically.
        // The bow faces +Z by default, straight at the flight of steps the Player walks down; jumping onto the deck binds the position, and after rowing out and jumping back ashore the boat stays put.
        // The sea segment is also handed to sea: in the sea the boat follows the visible wave surface's rise and tilt instead of floating on a constant sea level.
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

        skill = new Skill();
        AddPanel(skill);

        painting = new Painting();
        AddPanel(painting);

        // Day/night dial: sits in the free gap between the Settings button and the mirror stack, so it
        // is added before neither makes a difference to drawing order - the two never overlap.
        clock = new Clock();
        AddPanel(clock);

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
        // Waterfalls take the same path: the proxy mesh provides triangle-level picking, and the Bounds/Wireframe/Outline highlights match other entities.
        // A list rather than a single field, so multiple waterfalls just need one more Add into waterfalls.
        for (int i = 0; i < waterfalls.Count; i++)
        {
            picker.Targets.Add(waterfalls[i]);
        }
        // The mist volume is the same kind of target: an invisible box control whose Pos/Width/Height/Depth/Rotation can be changed in the property panel after picking,
        // and the change takes effect on the mist's spawn rectangle the next frame (see WaterfallPool.Update).
        if (waterfallPool != null)
        {
            picker.Targets.Add(waterfallPool.Mist);
        }
        // Each river segment is its own pick target: straight and curve segments can be dragged, resized, and rotated individually.
        // The highlight must use Wireframe (a long mesh's world AABB "fattens" as soon as it rotates, see RiverSegment's class comment).
        foreach (var segment in river.Segments)
        {
            picker.Targets.Add(segment);
        }
        // The dikes on both banks are also selectable, but at single-block granularity: the whole river's hundred-plus blocks share one GLB template, and an instance hit selects that one block,
        // with the panel's Pos/Width/Height/Depth/Rotation written directly into that instance (see ObjectPanel's instance branch).
        // These changes are kept in SyncDikes as offsets relative to the automatic layout, so the dikes do not snap back when the whole river course is moved.
        picker.InstancedTargets.Add(river.Dikes);
        // The river mouth's mist box is the same kind of target: an invisible box whose position/size/orientation can be changed in the property panel after picking,
        // and the change takes effect on the three particle groups' spawn rectangles the next frame (see River.UpdateMouthSpray: mist and foam use the box's bottom face, spray the top face).
        picker.Targets.Add(river.MouthMist);
        if (robots != null)
        {
            picker.InstancedTargets.Add(robots.robotField);
        }
        picker.InstancedTargets.Add(birds.seagullsModel);
        picker.InstancedTargets.Add(planes.planesModel);
        // The boat is an ordinary selectable entity too: click to select it and inspect it in the property panel, the same path as the house/waterfalls.
        picker.Targets.Add(boat.Hull);
        // The player's aircraft is the same kind of entity: click it to inspect the airframe in the property panel.
        picker.Targets.Add(aeroPlane.body);
        // The flagpole and flag are selectable as well: the column and the cloth each count as a target, and clicking them shows position and size in the property panel.
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
                        // Contract 3-2: fonts load on a background task in parallel with the first frames, so their
                        // duration belongs to the "everything is usable" milestone rather than to the white screen.
                        long fontStart = StartupProfiler.Enabled ? StartupProfiler.Ticks() : 0;

                        var fontInstance = await Season.Fonts.Font.CreateAsync(font.File, font.Size);

                        if (fontStart != 0)
                        {
                            StartupProfiler.Item("font", string.IsNullOrEmpty(font.Name) ? font.File : font.Name,
                                StartupProfiler.ElapsedMs(fontStart), 0, font.File);

                            StartupProfiler.TouchActivity();
                        }

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
    /// Hands the room interior to the weather field as a shelter region. The precipitation region consumes
    /// the cover weight first, so rain and snow are zeroed inside the interior prism, which extends vertically
    /// only up to the ceiling; above the roof the own region still drives precipitation.
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
        // The room is rectangular and the roof is a gable, so an inscribed ellipse would miss the corners; here we use
        // the circumscribed circle plus a feather band, which along the diagonal exactly covers the wall corners,
        // leaving only a half-eave-wide dry band outside the walls that reads as the eave's own rain shadow.
        float corner = MathF.Sqrt(halfX * halfX + halfZ * halfZ);
        float feather = MathF.Max(1f, MathF.Max(halfX, halfZ) * 0.2f);
        weather.Cover = new WeatherRegionSettings
        {
            Id = "room-cover", Weather = WeatherKind.Clear, Shelter = true,
            CenterX = centerX, CenterZ = centerZ, RadiusX = corner + feather, RadiusZ = corner + feather,
            EdgeFeather = feather, Bottom = min.Y, Top = max.Y,
            // The cloud radius is shrunk to sub-meter: the shelter only suppresses rain and snow, it must not punch a hole in the sky's cloud deck.
            CloudRadiusX = 0.001f, CloudRadiusZ = 0.001f, CloudBase = 0,
        };
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? width = null, float? height = null, float? posZ = null, float? depth = null)
    {
        // Per-pixel A/B forensic hooks (see AbCapture): when disabled, these two lines are a pass-through and a
        // static boolean check respectively. delta must be substituted before base.Update, otherwise Time has already
        // been advanced by one frame on wall-clock time. Contract 3-1's fixed timestep uses the same switch:
        // the cost table requires t to be a pure function of the frame number, and with both hooks off it is just one static boolean check.
        time = FrameProfiler.FixedDelta(AbCapture.FrameDelta(time));
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
            clock?.Alpha = 0f;
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
            clock?.Alpha = 1f;
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
            clock?.Alpha = 1f;

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
            clock?.Alpha = 1f;
        }

        // Consume the request before any panel's early return, so weather can be previewed even with settings open. time is frame seconds, independent of the day/night rate.
        RefreshWeatherCover();
        if (weather?.Update(WorldSettings.Current, time) == true)
            RequestSaveSettings();
        celestial?.UpdateWeatherBackground();
        bool regionalWeather = WorldSettings.Current.WeatherMode != WeatherMode.Showcase;
        if (weather != null)
        {
            weatherVisuals.Update(weather.Field, time, regionalWeather, Camera.Far);
            // The lightning edge has already advanced this frame; thunder is delayed behind the flash by the speed of sound, and Showcase mode has no regional lightning, so it stays silent.
            thunderAudio.Update(time, weatherVisuals, CameraPos, regionalWeather && weather.LightningActive);
        }
        DayNightCycle.Evaluate(celestial?.DayPhase ?? 0, out _, out float daylight, out _, out _, out _, out _);
        if (precipitation != null)
        {
            precipitation.Enabled = regionalWeather;
            precipitation.Advance(time, daylight);
        }
        // The decal Intensity must be recomputed every frame: DecalEffect repacks from the current value each frame, so writing it here is enough and no notification is needed.
        if (pondCaustics != null) pondCaustics.Intensity = CausticsIntensity * SunlightFactor(celestial?.DayPhase ?? 0);

        if (settingPanel != null)
        {
            settingPanel.Update(time, alpha: 0.8f, posX: 100, posY: 100, width: (int)App.Instance.ExtendResolution.X - 200, height: (int)App.Instance.ExtendResolution.Y - 200);

            return false;
        }

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

        clock?.Update(time);

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

        // Sailboats must come after sea: their waterline reads this frame's sea-surface sample (Sea.SurfaceY).
        sailingBoats?.Update(time);

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

        // The boat must come after pond: its water height and channel both come from the pond, and it computes its own rocking and animation state each frame.
        boat?.Update(time);

        tower?.Update(time);

        sphere?.Update(time);

        // The waterfall control syncs every frame: it turns Pos/Rotation/Scale into a placement matrix for the sheet
        // and updates the three highlighted channels. The sheet itself is drawn by the SceneWaterfall pass; this handles only the "object" half.
        for (int i = 0; i < waterfalls.Count; i++)
        {
            waterfalls[i].Update(time);
        }

        // The waterfall pool must follow the waterfalls: its anchor is sheet.Transform x the last control point, so it
        // reads the placement matrix synced just this frame. Panel.Update does not recurse automatically (see the comment above),
        // so missing this line would leave the water surface/decals/rocks/particles at their defaults (origin, default 8m rectangle).
        waterfallPool?.Update(time);

        // Same for the river: each segment is its own separate Mesh3D and Panel does not tick recursively, so missing
        // this line would leave the highlight pulse and Changed at their initial values. The actual water surface is
        // computed on the fly by the SceneWaterfall pass from the WaterRibbon data registered when the River was built, unrelated to this.
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

        // Contract 3-1: last, so every panel and every camera rig above has already written this frame's pose and the
        // measurement either overwrites it or leaves it alone. Disabled runs pay one static bool read.
        CostProfile.Tick(this);

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
    /// Generic entry point for waterfalls: takes a shape (<paramref name="template"/>) and generates an independent
    /// waterfall control, optionally placing it in the scene.
    ///
    /// One call does five things: copies the author data (shape/material/texture names), calls
    /// <see cref="Waterfall.Place"/> as needed, queues a Load via AddControl (the proxy mesh is built only at that
    /// moment), registers into the scene waterfall table (the side that actually draws the water), and registers into
    /// this App's <see cref="waterfalls"/> (per-frame Update and pick targets). So "multiple waterfalls" is just a few
    /// more lines: the same template with a different center grows one more.
    ///
    /// An empty <paramref name="center"/> means no placement: the reference coordinates in the control points are also
    /// the world coordinates (P1's old behavior). center is the same quantity as the property panel's PosX/PosY/PosZ
    /// (the bounding-box center); <paramref name="size"/> is the per-axis target size (meters), the same set of numbers
    /// as the panel's Width/Height/Depth, and null keeps <paramref name="scale"/> for each axis. After visual tweaks,
    /// copying these numbers from the panel back gives the startup values.
    ///
    /// The three P0 assets are also registered here: the backend only looks textures up by name in a dictionary, and
    /// any unregistered one makes the whole thing be skipped (it's "not shown", rather than burning the white texture
    /// into full-value white foam across the sheet). The same name is loaded only once, see <see cref="RegisterNamedTexture"/>.
    /// </summary>
    internal Waterfall SpawnWaterfall(string name, Season.Rendering.WaterfallSheet template,
        Vector3? center = null, float yawDegrees = 0f, float scale = 1f, Vector3? size = null)
    {
        var waterfall = new Waterfall(name);

        // Copy rather than share: parameters like DeepColor/Opacity/SortOrder must be tweakable per instance, so changing one does not affect the whole group.
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
    /// P1 waterfall demo: the 12 rows of control points fixed by the P0 site-selection probe (candidate #5, the inner
    /// concave corner of the island mountains on the side facing the house/haunted house, kept clear of the east-shore
    /// Rocks, visible from all four observation lines). The data is copied row by row from the paste-ready section of
    /// <c>.dbg/_waterfall_site/out/site_report.txt</c>.
    ///
    /// outward is always (0.511, 0, 0.860) (the horizontal direction away from the rock face), and the half-width grows
    /// from 1.30 to 2.50 -- the flow spreads out as it falls; arc length 17.97 m, drop 12.5 m. To change the shape, edit
    /// only here: the control points go into a frame buffer, so the shader needs no recompile.
    ///
    /// The 12 points below are the <b>raw world coordinates</b> given by the probe, now used only as a "shape template":
    /// the actual startup position and size are given by tunedCenter / tunedSize (the measured readings from visual
    /// tweaks), see the comment on that block. To pick another location: select the waterfall and drag or edit the
    /// numbers, then overwrite these two variables with the panel's PosX/PosY/PosZ and Width/Height/Depth.
    /// </summary>
    void CreateWaterfall()
    {
        var sheet = new Season.Rendering.WaterfallSheet
        {
            // The water body's own blue-green sea tone + near-white foam. The cartoon look separates the two white edges from the water body by brightness difference, not by specular highlights.
            DeepColor = new(0.09f, 0.31f, 0.36f),
            FoamColor = new(0.93f, 0.98f, 1.00f),
            Opacity = 0.85f,
            ScrollSpeed = 0.55f,
            NormalStrength = 0.55f,
            NormalTiling = 2.5f,
            FoamAmount = 0.85f,
            // A long 12.5 m drop: the lip/impact white edges are narrowed proportionally, otherwise the middle would be eaten away by the two white bands.
            LipFoam = 0.08f,
            FootFoam = 0.10f,
            EdgeFoam = 0.22f,
            EdgeSoftness = 0.14f,
            Erosion = 0.30f,
            Bow = 0.14f,
            // The cross-section is a closed ring around the control-point polyline: Thickness is the total thickness, split evenly front and back.
            // The cliff face lies on the control-point polyline, so the rear half of the section sinks into the rock (culled by the software depth test),
            // and only the front half bulges out as the visible "water body". 1.0 m for a 5 m wide, 12.5 m high waterfall is roughly a person-high wall of water.
            // To make the waterfall look more "solid", raise this number, at the cost of the front wall moving further from the cliff face (it will appear to float outward from a distance).
            Thickness = 1.0f,
            // Number of columns around the ring (including the coincident first/last column): at 12 segments the eye can no longer see the facets, and the tangent transition between the two sides is not too hard either.
            Columns = 12,
            Subdiv = 2,
        };
        // The order must go from the outlet to the impact point; v=0 is the lip and v=1 is the impact point, and the foam band is assigned along this direction.
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

        // Measured readings from visual tweaks (as-is from the property panel): position is the bounding-box center, size is the bounding-box side lengths (meters), no rotation about Y.
        //
        // Unlike the previous version, these values cannot be baked into the control points: the size is per-axis (15 x 13 x 13),
        // while the control points describe only position, and the section's half-width and thickness are isotropic scalars that
        // cannot absorb the different X and Y/Z scaling; also, this sheet spans XZ diagonally, so widening only X would change
        // the flow's direction -- such anisotropy can only be done by the placement matrix, i.e. Width/Height/Depth.
        // So the control points keep the probe's original values and placement is delegated to Place: the matrix is p -> (p - anchor)·scale + Pos,
        // the anchor (bounding-box center) lands exactly on Pos, and the bounding-box side lengths equal tunedSize exactly, so the panel reads back the same two sets of numbers.
        var tunedCenter = new Vector3(-75f, 6.8f, -73f);
        var tunedSize = new Vector3(15f, 14f, 13f);

        // Each waterfall is one call. The same shape with different coordinates called again grows one more (different templates work too);
        // the cap is SceneWaterfalls.MaxActiveWaterfalls:
        //     SpawnWaterfall("Waterfall P2", sheet, new Vector3(x, y, z), yaw, scale, new Vector3(w, h, d));
        SpawnWaterfall("Waterfall P1", sheet, tunedCenter, yawDegrees: 0f, size: tunedSize);
    }

    /// <summary>
    /// D1 decal demo: three decals each carry an RGBA texture generated by SeasonImage (see .dbg/_decal_assets).
    /// The textures are "multipliers" rather than albedo: Modulate blends as dst*src, and using a photo directly as
    /// albedo would darken the ground several times over, so the brightness of the two ground textures has already been
    /// squeezed into [0.5,1.0] / [0.62,1.0] in the generator while the color stays in the image; Tint only applies a
    /// slight bias. The alpha is drawn by the generator as an organic outline, replacing the earlier 4x4 debug checkerboard
    /// in the shader.
    ///
    /// Decals only look up textures by name in the backend dictionary and do not load files themselves: on the frame a name
    /// is unregistered the whole decal is skipped by the backend, so the same image must first be handed to
    /// <see cref="RegisterDecalTexture"/> to go through the existing named-texture path.
    /// </summary>
    void CreatePondDecals()
    {
        // Wet grass on the southwest side outside the pond: Modulate and writes roughness (puddles); this is the s9 verification point.
        // The projection box must be centered on the surface being decaled with slack above and below: if the box face lands exactly
        // on the surface, the depth-reconstruction error plus TAA jitter makes the |local.y|<=1 test flip row by row, showing up as
        // flickering stripes. The grass is at y=0.
        var wetGrass = new Season.Rendering.DecalVolume
        {
            Mode = Season.Rendering.DecalMode.Modulate,
            Texture = "Assets/DecalWetGrass.png",
            Position = new(-10f, 0f, -15f),
            Size = new(7f, 0.4f, 7f),
            Tint = new(0.94f, 0.97f, 1.00f),
            Opacity = 0.9f,
            AngleFade = 0f,
            // The outline is already drawn by the texture alpha; pushing it down further with box-edge feathering would leave only a small dot.
            EdgeFade = 0.15f,
            AffectRoughness = true,
            Roughness = 0.04f,
            SortOrder = 0,
        };
        Decals.Add(wetGrass);
        RegisterNamedTexture(wetGrass.Texture);

        // Moss on the top face of the north pond rim: modulates color only. The rim top face is at Pond.RimTop (0.76 after a
        // 0.86 m dam piece sunk 0.10 m), and the box is centered on it; a stretch of the rim's inner and outer side walls falls
        // into the box, which is exactly what shows the grazing-angle fadeout. The north band's center is at pond center + 4.775 m.
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

        //Caustics on the pond floor: Additive, and the 0.35-opacity pond water draws over it, which also verifies "decals before transparent".
        // The texture itself is already a "light mesh on a black background", so the pixels are the light; Tint only brings it near white and the strength is set by Intensity.
        // The pond floor is the grass at y=0 and the water surface is at 0.4: the box is centered on the pond floor, and its top at 0.15 is still below the water surface.
        // Intensity here is only a design value; the actual brightness is recomputed each frame by Update from the sun height (see CausticsIntensity / SunlightFactor).
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

    /// <summary>Design value for the caustics intensity under direct noon sunlight.</summary>
    const float CausticsIntensity = 0.5f;

    /// <summary>
    /// Direct-sunlight multiplier: dispersive caustics are the bright mesh of sunlight refracted and focused through the
    /// water surface, not self-emission, so the brightness must follow the sun. The higher the sun the stronger, and near
    /// the horizon almost all of it is Fresnel-reflected off the water surface, hence the high-power curve rather than a
    /// linear one, plus an onset threshold to avoid a faint decal trailing near the horizon around sunset. Moonlight caustics
    /// are too weak and do not participate. internal: the pool-floor caustics (WaterfallPool) and the pond-floor caustics
    /// share this one curve, so the two brightnesses always come from the same source.
    /// </summary>
    internal static float SunlightFactor(float phase)
    {
        DayNightCycle.Evaluate(phase, out _, out float sunElev01, out _, out _, out _, out _);
        float rise = Math.Clamp((sunElev01 - 0.04f) / 0.30f, 0f, 1f);
        return rise * rise * MathF.Pow(sunElev01, 1.5f);
    }

    /// <summary>
    /// Registers a texture name into the backend texture dictionary. Decals and waterfall sheets only look names up in
    /// the dictionary and never load files themselves, so an unregistered name means the whole thing is skipped; that's why
    /// this reuses the existing named-texture path: a Sprite2D that is never drawn loads under the same name.
    ///
    /// It decodes once synchronously on registration: AddControl goes through a background queue (BaseApp.RequestLoad -> consumer
    /// thread), and merely queuing would let a texture take a few seconds to reach the dictionary, while the waterfall/river needs
    /// to sample by name on its very first frame, and during that time the whole water band is not drawn per the "missing entry means
    /// skip" rule. LoadSprite2D decodes synchronously internally, and the queued entry later hits the same cache key, so it won't
    /// decode twice.
    ///
    /// The browser is the only exception: the single-threaded WASM runtime has no Monitor, and any Task.Wait / GetResult throws
    /// PlatformNotSupportedException (see below), while fetching images in the browser itself can only go through an async fetch,
    /// so a synchronous wait gets nothing. There only the AddControl-queued entry is kept, decoded frame by frame by the primitive
    /// queue (BaseApp.ProcessControlQueueFrame); the cost is that the waterfall sheet has no texture for the first few frames and
    /// is not drawn per the missing-entry rule, appearing naturally once the dictionary is filled.
    ///
    /// Deduplicated by name: multiple waterfalls share the same set of three textures, and without dedup that would be several
    /// empty Sprite2D instances each loading the same file. internal: WaterfallPool's two pool-floor decals go through the same
    /// named-texture path instead of opening their own load chain.
    /// </summary>
    internal void RegisterNamedTexture(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!namedTextures.Add(name)) return;

        var sprite = new Season.Controls.Sprite2D
        {
            Name = name,
            // LoadSprite2D internally converts Width/Height to int, and null gets swallowed as a "load failure", so give real numbers here.
            Width = 1,
            Height = 1,
            // Pure carrier: with Alpha 0 Draw returns immediately and it never appears on screen.
            Alpha = 0f,
            Enable = false,
        };
        AddControl(sprite);

        // Browser: AddControl has already queued it, and that entry is its only decode path -- calling
        // LoadSprite2D directly again here would make both reach AddRef, and the queue entry later failing
        // would leave the reference count unbalanced.
        if (OperatingSystem.IsBrowser()) return;

        try
        {
            Season.Basic.Graphics.Instance.LoadSprite2D(sprite).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            AddLog(LogType.Error, $"[Texture] '{name}' LoadSprite2D threw: {ex}");
        }

        // If any one of the three maps is missing, the waterfall and river are skipped entirely, and backend failures are silent (just not drawn).
        // When a texture is missing, log "whether the file exists" as well, to save guessing whether it's an addressing error or a decode error.
        if (sprite.OriginWidth <= 0 || sprite.OriginHeight <= 0)
        {
            string baseDir = Season.Storage.StorageService.DirectoryBase;
            bool onDisk = Season.Storage.StorageService.FileExist(baseDir, name);
            AddLog(LogType.Error, $"[Texture] '{name}' not decoded: origin={sprite.OriginWidth}x{sprite.OriginHeight} onDisk={onDisk} base='{baseDir}'");
        }
    }

}
