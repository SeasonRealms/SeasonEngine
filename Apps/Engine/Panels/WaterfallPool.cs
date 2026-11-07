// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Season.Rendering.Effects;

namespace Engine.Panels;

/// <summary>
/// Waterfall pool: the water surface below the fall point (<see cref="Season.Rendering.WaterPlane"/>) + two decals on the pool bed +
/// a ring of rock rim (reusing the instanced rocks from Rocks.glb) + three groups of GPU particles (splash Splash / mist Mist / foam Foam).
/// All anchors except the mist are recomputed every frame from the waterfall's landing control point,
/// so when the waterfall is dragged (Place changes the placement matrix) the pool and effects follow along, with no need to maintain coordinates in two places.
///
/// The landing anchor shares its source with the waterfall: take the last control point of <see cref="WaterfallSheet.Points"/> (the v=1 landing point),
/// multiply by <see cref="WaterfallSheet.Transform"/> into world space. The half width is derived by "transforming the two endpoints and taking half the distance",
/// so Place's anisotropic scaling (15×14×13) and yaw are both absorbed automatically, without mirroring Place's formula.
///
/// Why the rectangle that extends into the mountain does not need to be clipped: the water Pass's pixel shader has a software depth test
/// (discard when input.position.z is greater than SceneDepth, see WaterShaders), and the part of the pool rectangle facing into the mountain sits behind the rock body in depth,
/// so it is naturally culled; the remaining problem is the rectangle's four straight edges — the front/side two are sealed by the rock rim and the foam particles,
/// and the back edge is entirely sunk into the rock body, so that edge does not exist. The rocks are laid along the edges of the "world axis-aligned bounding box of the landing ellipse",
/// because the water surface is itself that axis-aligned rectangle, and rocks only seal the straight seams by hugging the rectangle's edges; hugging the ellipse's edges would instead leave gaps.
///
/// All three particle groups go through ParticleSystemEffect's rectangle spawn branch (SpawnRect, contract at lines 16..19),
/// with registration and driving copied verbatim from Campfire: ExternalTime + Advance is the only clock,
/// Dispose leaves null when RegisterCompute declines, and Update advances each frame and gates on the waterfall's Enabled.
/// Landing die-off uses DieOnFloor + FloorY=pool surface: the frame at which the splash arc falls through the water surface stops emitting from that frame onward, i.e. "extinguished on entering the water".
///
/// The mist is the only exception: its spawn rectangle is no longer derived from the landing point, but comes from the pickable invisible box control <see cref="Season.Controls.MistVolume"/>,
/// the same approach as <see cref="Season.Controls.Waterfall"/> — the box center/yaw/width-depth is the spawn plane and the mist column height,
/// so the mist's parameters become an object that can be dragged, rotated, and scaled in the properties panel (changes take effect the next frame).
/// The foam is driven by the same box: it is that whitest band in the scene (the box's Width = band length, Depth = band thickness),
/// so when the box moves, mist and foam move together; the splash stays bound to the landing point.
/// Only the first frame lays out a default set relative to the pool surface; afterward the box's planar position follows the pool center, until the user moves Pos in the panel, at which point it permanently detaches and is handed to the user.
///
/// None of the three particle groups set a near-view visible distance (ViewDistance = 0, i.e. infinite): that is a fixed radius centered on the camera beyond which the whole cluster fades out,
/// and the former 50 / 55 m was only enough to watch from the pool's edge. When looking down at the whole island from the sky the distance from the camera to the pool is over a hundred meters, and the fixed radius would wipe the splash, foam, and
/// mist all down to 0, leaving only a curtain of water with no landing point in the scene — that is not "can't see clearly from afar", but a landmark vanishing into thin air.
/// Distant particles, like the geometry elsewhere, are left to frustum culling and lifetime fade-out; a second camera-centered gate is not needed.
///
/// Particles do not take scene lighting (Particles' Shade only computes Color.rgb * alpha, and Color is the albedo):
/// so Update scales a copy of the author color by the sun height and writes it back each frame (see <see cref="ApplyParticlesLighting"/>),
/// and only then do the mist have a brightness difference between day and night. The waterfall curtain side does not need this patch: WaterfallEffect already packs
/// FrameSchedule.LightingSnapshot's sun and ambient light into the frame buffer (rows 6..8), and the fragment multiplies by ambient/direct.
///
/// The rocks do not build their own boxes: the geometry and four textures come from small variants of Assets/Rocks.glb extracted in the background by the Rocks panel,
/// and this panel only appends instances to those InstancedMesh3D and writes the matrix each frame (the rock ring follows when the waterfall is dragged),
/// so the rim and the east bank share one copy of the geometry and GPU instancing, and incidentally inherit Rocks' picking and collision registration.
/// </summary>
internal class WaterfallPool : Panel
{
    /// <summary>Pool water surface height. The waterfall's landing point is pushed about 0.2 m below the grass by tunedSize, and the water film sits 5 cm above the y=0 grass.</summary>
    const float WaterY = 0.05f;

    /// <summary>Wind sourced from the same place as the scene mist (same value as App.MistWind), so the pool mist drifts the same way and the wind field looks consistent in the scene.</summary>
    static readonly Vector3 MistWind = new(0.30f, 0f, 0.10f);

    /// <summary>Design value of the pool-bed caustics intensity at direct noon light (slightly lower than Pond: deeper pool, and the water curtain overhead also blocks light).</summary>
    const float CausticsIntensity = 0.38f;

    // Author colors (design values at full noon brightness, never changed at any time): the particle shader treats Color directly as the albedo,
    // taking no part in scene lighting (see Particles' Shade, only Color.rgb * alpha), so the three systems each frame scale a copy by the sun height
    // and write it back (see ApplyParticlesLighting), otherwise day and night would look exactly the same.
    // The author colors are reused as shared source (internal) by River's river-mouth splash and foam: both are the same kind of impact effect, so the color should have only one design value.
    internal static readonly Vector4 SplashColor = new(0.82f, 0.93f, 0.98f, 0.75f);
    internal static readonly Vector3 SplashMid = new(0.90f, 0.97f, 1.00f);
    internal static readonly Vector3 SplashEnd = new(0.50f, 0.62f, 0.70f);

    // Mist alpha is 0.07: 0.05 is too faint, the Haze would be nearly invisible and the Height knob would lose its meaning.
    static readonly Vector4 MistColor = new(0.92f, 0.96f, 1.00f, 0.07f);
    static readonly Vector3 MistMid = new(0.95f, 0.97f, 1.00f);
    static readonly Vector3 MistEnd = new(1.00f, 1.00f, 1.00f);

    // Foam alpha raised from 0.42 to 0.52: this band is the whitest stroke in the scene, and too much transparency would reveal the water and rocks behind it,
    // looking "hollow rather than solid".
    internal static readonly Vector4 FoamColor = new(0.95f, 0.98f, 1.00f, 0.52f);
    internal static readonly Vector3 FoamMid = new(0.92f, 0.96f, 0.99f);
    internal static readonly Vector3 FoamEnd = new(0.75f, 0.82f, 0.88f);

    /// <summary>Night-time brightness floor: the mist must still show its outline under moonlight, rather than going straight to a black silhouette.</summary>
    const float NightParticleLight = 0.32f;

    /// <summary>The anchored waterfall control. The control's own side (Place/proxy mesh) is handled by App's waterfalls; here we only read the shape and ready state.</summary>
    readonly Season.Controls.Waterfall waterfall;

    /// <summary>Waterfall author data, i.e. waterfall.Sheet; the single source of truth for the shape and landing point.</summary>
    readonly Season.Rendering.WaterfallSheet sheet;

    internal readonly Season.Rendering.WaterPlane Water = new()
    {
        // A mountain pool is darker and murkier than a sea pool: deeper base color, stronger absorption, and the grazing angle is brightened by Fresnel itself.
        Tint = new Vector4(0.045f, 0.16f, 0.20f, 1f),
        Opacity = 0.45f,
        Absorption = 2.6f,
        NormalStrength = 0.7f,
        ScrollSpeed = 0.02f,
        // Calm pool water: WindSpeed stays 0 and the whole Gerstner vertex displacement is turned off, leaving only the pixel-side normal perturbation.
        RippleStrength = 0.4f,
        Roughness = 0.05f,
    };

    Season.Rendering.DecalVolume silt;
    Season.Rendering.DecalVolume caustics;

    // Rim rocks: we do not build our own boxes, but attach instances of Assets/Rocks.glb — the geometry/textures are extracted once in the background by the Rocks panel
    // (15 variants, one InstancedMesh3D each); here we only append instances to those templates, so the same geometry goes through GPU instancing
    // and the same four-texture PBR, and incidentally enters Rocks' picking and collision table. The parameters live in the table below.
    readonly RimRock[] rimLayout;
    readonly MeshInstanceTransform?[] rimInstances;   // null before registration (template ready)
    readonly InstancedMesh3D?[] rimFields;            // the variant template each rock belongs to, used to read the template's local size
    bool rimRegistered;

    // Null when the backend declined compute support; the update loop then has nothing to tick.
    ParticleSystemEffect? splash;
    ParticleSystemEffect? mist;
    ParticleSystemEffect? foam;

    /// <summary>
    /// Water mist's placeable volume: an invisible box control (same approach as Waterfall). The box center and orientation, width/height/depth are the spawn rectangle,
    /// and Height determines how high the particles climb over their lifetime — so "the mist's parameters" is just this one box that can be dragged, rotated, and scaled in the panel.
    /// It registers as an ordinary Mesh3D into picker.Targets, so it has triangle-level picking and three highlight modes: Bounds/Wireframe/Outline;
    /// the control's own default highlight was changed to Wireframe (see MistVolume's constructor): Bounds is the world axis-aligned bounding box,
    /// and rotating this 8.8 × 2.4 m long box only fattens the AABB (about 8 × 8 m at 45°), so "rotating" looks like "scaling up".
    /// </summary>
    internal readonly Season.Controls.MistVolume Mist = new("WaterfallMist");

    /// <summary>Whether the mist box has already been placed: on the first frame (after the waterfall has Loaded) a default set is laid out from the pool geometry.</summary>
    bool mistPlaced;

    /// <summary>Whether the user has changed the mist box's position in the panel: once changed it permanently detaches, and is no longer overwritten by the per-frame follow write-back.</summary>
    bool mistDetached;

    /// <summary>The center point last written to the mist box; the next frame it is compared against the box's Pos, and any difference means the user moved it.</summary>
    Vector3 lastMistCenter;

    /// <summary>Only the systems that actually registered; a backend without compute leaves this empty.</summary>
    readonly List<ParticleSystemEffect> systems = new();

    /// <summary>Author parameters of a single rim rock: polar angle (0=straight ahead, i.e. the outward direction), world height (m), yaw, push-out distance, sink amount.</summary>
    readonly record struct RimRock(float AngleDegrees, float Height, float YawDegrees, float PushOut, float Sink);

    /// <summary>LeftPull: the amount (m) by which the left-half ring of rocks is shifted laterally to the right. The left side of the pool rectangle is eaten away by the rock wall, so laying rocks flush along it would bury whole rocks in the mountain body; hence the left half-ring is pushed a little further right.</summary>
    const float LeftPull = 0.9f;

    internal WaterfallPool(Season.Controls.Waterfall waterfall)
    {
        this.waterfall = waterfall;
        this.sheet = waterfall.Sheet;

        // Rock layout: from facing ±114° one rock every 12°, 20 in total; the rock height and yaw are jittered with a fixed seed (same technique as the Rocks panel: the layout is reproducible,
        // changing the seed reshapes the whole ring). The height is set directly in world meters, and the scale is back-computed from the template's bounding box, so no matter how large the rocks in the asset are, they are never laid out too small.
        var rng = new Random(0x5A11BEEF);
        rimLayout = new RimRock[20];
        for (int i = 0; i < rimLayout.Length; i++)
        {
            float angle = -114f + i * 12f;
            // The middle few are a bit shorter so they do not block the view of the curtain's landing point; the large rocks on both sides cover the corners and plug the gap in the ring.
            bool center = MathF.Abs(angle) < 40f;
            float height = (center ? 0.95f : 1.45f) + (float)rng.NextDouble() * (center ? 0.35f : 0.60f);
            float yaw = (float)rng.NextDouble() * 360f;
            rimLayout[i] = new RimRock(angle, height, yaw, 0.30f, 0.35f);
        }

        rimInstances = new MeshInstanceTransform[rimLayout.Length];
        rimFields = new InstancedMesh3D[rimLayout.Length];

        // Mist box: the control itself is invisible and only provides the matrix and highlight; App adds it to picker.Targets (triangle-level picking + properties panel).
        AddControl(Mist);

        // The water surface and decals are "data registrations" rather than controls: same path as Pond/Sea, collected each frame from App's registry.
        App.Instance.Waters.Add(Water);

        // Pool-bed silt: Modulate presses the grass into dark brown-green. The projection box is centered on the y=0 grass surface with margin above and below (same rule as Pond's decals:
        // a box face landing exactly on the surface would flip row by row due to depth-reconstruction error + TAA jitter, appearing as shimmering stripes).
        silt = new Season.Rendering.DecalVolume
        {
            Mode = Season.Rendering.DecalMode.Modulate,
            Texture = "Assets/DecalWetGrass.png",
            Position = new Vector3(0f, 0f, 0f),
            Size = new Vector3(10f, 0.3f, 10f),
            Tint = new Vector3(0.42f, 0.50f, 0.44f),
            Opacity = 0.9f,
            AngleFade = 0.15f,
            EdgeFade = 0.25f,
            AffectRoughness = true,
            Roughness = 0.15f,
            SortOrder = 0,
        };
        App.Instance.Decals.Add(silt);
        App.Instance.RegisterNamedTexture(silt.Texture);

        // Pool-bed caustics: Additive, pressed below the 0.05 water surface. The box's full height is only 0.08: the grass surface is at 0, and the top face at 0.04 stays below the pool surface,
        // so the caustics do not float above the water and get drawn in the air, nor incidentally light up the lower half of the rocks' sides (same constraint as Pond: the box top must be submerged).
        // The brightness shares its source with Pond's caustics: recomputed each frame by the sun height, so that once the scene darkens at night it does not appear brighter due to contrast.
        caustics = new Season.Rendering.DecalVolume
        {
            Mode = Season.Rendering.DecalMode.Additive,
            Texture = "Assets/DecalCaustics.png",
            Position = new Vector3(0f, 0f, 0f),
            Size = new Vector3(6f, 0.08f, 6f),
            Tint = new Vector3(0.80f, 0.95f, 1.00f),
            Opacity = 0.5f,
            AngleFade = 0.1f,
            EdgeFade = 0.3f,
            Intensity = CausticsIntensity,
            SortOrder = 1,
        };
        App.Instance.Decals.Add(caustics);
        App.Instance.RegisterNamedTexture(caustics.Texture);

        splash = Register(CreateSplash());
        mist = Register(CreateMist());
        foam = Register(CreateFoam());
    }

    /// <summary>
    /// Splash: thrown diagonally upward in a whole row from the landing banner, cone jitter 0.5, gravity -9.8, lifetime 0.9 s,
    /// and DieOnFloor makes the frame at which it falls through the water surface stop emitting from that instant (extinguished on entering the water).
    /// SizeEnd needs CurveMode to take effect (the vertex-side size lerp is inside the curve.w gate), so the white-cyan color is made a
    /// two-stage gradient born near-white and fading to pale cyan; ToonEdge is independent of CurveMode and always on.
    /// </summary>
    ParticleSystemEffect CreateSplash() => new(1536)
    {
        ExternalTime = true,
        CameraWindow = false,
        Seed = 0x5A110001,
        Density = 0.6f,
        SpawnRect = true,
        Lifetime = 0.9f,
        Acceleration = new Vector3(0f, -9.8f, 0f),
        ConeJitter = 0.42f,
        SpeedJitter = 0.20f,
        DieOnFloor = true,
        Stretch = 0.04f,
        ParticleSize = 0.05f,
        SizeEnd = 0.02f,
        Color = SplashColor,
        ColorMid = SplashMid,
        ColorEnd = SplashEnd,
        FadeIn = 0.05f,
        FadeOut = 0.30f,
        Additive = false,
        CurveMode = true,
        NoiseAmount = 0.3f,
        Turbulence = 0.15f,
        ViewDistance = 0f,
    };

    /// <summary>
    /// Mist: the spawn rectangle and height are entirely determined by <see cref="Mist"/> (the pickable invisible box) — the particles grow from the box's bottom face,
    /// and the initial rise speed is back-solved from the box height, so raising the box raises the mist. Upward acceleration 0.15, drifting with the scene wind direction, lifetime 3.2 s.
    /// A cluster of thin mist: a low-coverage soft puff with alpha 0.05, spreading from 0.45 to a final 1.25 via CurveMode's SizeEnd interpolation,
    /// and noise 0.6 makes each puff's edge irregular. Both density and volume are kept small; the mist only serves as the damp air at the foot of the water curtain and must not cover the waterfall itself.
    /// </summary>
    ParticleSystemEffect CreateMist() => new(256)
    {
        ExternalTime = true,
        CameraWindow = false,
        Seed = 0x5A110002,
        Density = 0.40f,
        SpawnRect = true,
        Lifetime = 3.2f,
        Acceleration = new Vector3(0f, 0.15f, 0f),
        WindVelocity = MistWind,
        ConeJitter = 0.25f,
        ParticleSize = 0.45f,
        SizeEnd = 1.25f,
        Color = MistColor,
        ColorMid = MistMid,
        ColorEnd = MistEnd,
        FadeIn = 0.20f,
        FadeOut = 0.55f,
        Additive = false,
        CurveMode = true,
        NoiseAmount = 0.6f,
        Turbulence = 0.15f,
        ViewDistance = 0f,
    };

    /// <summary>
    /// Foam: spawns as a shallow band along the front of the landing point, moving horizontally outward 0.8 and rising 0.3, lifetime 1.6 s.
    /// Toon clouds: white with alpha 0.52, size 0.36→0.92, and ToonEdge 0.30 presses the radial falloff into a crisp round edge,
    /// so they look like squeezed-out soap bubbles rather than grey smoke.
    ///
    /// Density: Density is the GPU-side alive quota (capacity × Density), so "more solid" is raised directly here, without adding draw calls.
    /// 768 × 0.80 ≈ 610 particles spread over the box's bottom face (default 8.8 × 2.4 m ≈ 21 m²), a spacing of about 0.19 m,
    /// while the particles themselves are 0.36 ~ 0.92 m, so they overlap into a continuous white band; the former 512 × 0.50 ≈ 256 particles at 0.29 m spacing
    /// overlapped too little, showing separate white dots.
    /// NoiseAmount is also lowered from 0.5 to 0.35: the larger the noise, the more fragmented the edges, and the white band would scatter back into a "point cloud", contrary to the goal of "more solid".
    /// </summary>
    ParticleSystemEffect CreateFoam() => new(768)
    {
        ExternalTime = true,
        CameraWindow = false,
        Seed = 0x5A110003,
        Density = 0.80f,
        SpawnRect = true,
        Lifetime = 1.6f,
        Acceleration = new Vector3(0f, 0.08f, 0f),
        SpeedJitter = 0.3f,
        ParticleSize = 0.36f,
        SizeEnd = 0.92f,
        Color = FoamColor,
        ColorMid = FoamMid,
        ColorEnd = FoamEnd,
        FadeIn = 0.08f,
        FadeOut = 0.32f,
        Additive = false,
        CurveMode = true,
        NoiseAmount = 0.35f,
        Turbulence = 0.25f,
        ToonEdge = 0.30f,
        ViewDistance = 0f,
    };

    /// <summary>
    /// Registers one particle system and returns it, or disposes it and returns null when the backend
    /// declines. Same contract as Campfire: RegisterCompute reports a declined Initialize as false and
    /// Initialize already tears its own resources down.
    /// </summary>
    ParticleSystemEffect? Register(ParticleSystemEffect effect)
    {
        if (Season.Rendering.FrameSchedule.RegisterCompute(Season.Basic.Graphics.Instance, effect))
        {
            systems.Add(effect);
            return effect;
        }

        effect.Dispose();
        return null;
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        if (sheet.Points.Count < 2)
            return result;

        // While the waterfall control has not finished Loading, Transform is the identity matrix and the landing point is the author coordinate (about 2 m off the final position);
        // from the frame Load completes, all anchors below align automatically, with no need to wait on a gate or run an extra initialization.
        var m = sheet.Transform;
        var last = sheet.Points[^1];
        var foot = Vector3.Transform(last.Position, m);

        // outward / lateral must each be computed in world coordinates: Place is per-axis scaling (15×14×13),
        // and anisotropy twists the two basis vectors that are perpendicular in author space into an oblique pair (here off by about 20°),
        // so you cannot rotate outward by 90° to derive lateral, or the spawn rectangle would cross the real curtain banner obliquely,
        // and the pool surface would also be blown up by an order of magnitude. Only passing each through the transform and then normalizing gives the true geometry.
        var rawOut = last.Outward;
        float rawLen = MathF.Sqrt(rawOut.X * rawOut.X + rawOut.Z * rawOut.Z);
        var outLocal = rawLen > 1e-6f
            ? new Vector3(rawOut.X / rawLen, 0f, rawOut.Z / rawLen)
            : new Vector3(0f, 0f, 1f);
        var latLocal = new Vector3(outLocal.Z, 0f, -outLocal.X);
        var outward = Horizontal(Vector3.TransformNormal(outLocal, m), new Vector3(0f, 0f, 1f));
        var lateral = Horizontal(Vector3.TransformNormal(latLocal, m), new Vector3(1f, 0f, 0f));

        // World value of the banner half width: transform the two endpoints on either side of the landing point in author coordinates and take half the distance, absorbing all per-axis scaling.
        float hw = MathF.Max(last.HalfWidth, 0.5f);
        float halfWidthWorld = 0.5f * Vector3.Distance(
            Vector3.Transform(last.Position + latLocal * hw, m),
            Vector3.Transform(last.Position - latLocal * hw, m));

        // Pool surface: lateral = banner half width + foam margin; longitudinally forward (splash landing point) takes 1.15× half depth,
        // and backward it cuts only 1 m into the cliff face — just enough to bury the rectangle's back edge in the rock body and hand it to the software depth test for culling, taking no extra room.
        float a = halfWidthWorld + 0.6f;
        float b = MathF.Max(2.5f, a * 0.55f);
        const float back = 1.0f;
        float front = b * 1.15f;
        var center = foot + outward * ((front - back) * 0.5f);

        // Exact axis-aligned bounding box of the parallelogram spanned by the oblique basis: half width = the projection lengths of the two sides onto the axis added together.
        // (Cannot use sqrt(u²+v²): that formula only holds for an orthogonal basis.)
        float hx = a * MathF.Abs(lateral.X) + back * MathF.Abs(outward.X);
        float hz = a * MathF.Abs(lateral.Z) + back * MathF.Abs(outward.Z);

        Water.Position = new Vector3(center.X, WaterY, center.Z);
        Water.Size = new Vector2(2f * hx + 0.02f, 2f * hz + 0.02f);

        // The decal boxes follow the pool surface: the silt covers the whole pool bed, and the caustics pave only the front half (brightest directly in front of the curtain's landing point, and hidden invisibly on the mountain-facing side).
        silt.Position = new Vector3(center.X, 0f, center.Z);
        silt.Size = new Vector3(2f * hx - 1.2f, 0.3f, 2f * hz - 1.2f);
        caustics.Position = new Vector3(center.X + outward.X * front * 0.35f, 0f, center.Z + outward.Z * front * 0.35f);
        caustics.Size = new Vector3(
            MathF.Max(2f, 2f * hx * 0.55f), 0.08f,
            MathF.Max(2f, 2f * hz * 0.55f));
        caustics.Intensity = CausticsIntensity * App.SunlightFactor(App.Instance.DayPhase);

        // Mist box: on the first frame (after the waterfall has Loaded) a default set is laid out from the pool surface, and afterward the planar position follows the pool center and is written back each frame;
        // but once the box is ready, if its Pos is found to differ from the value we wrote last frame, it means the user moved it in the panel,
        // and from then on it permanently detaches — so "the mist follows when the waterfall is dragged" and "the mist can be freely placed" both hold, and the user's changes are never overwritten.
        if (!mistPlaced)
        {
            if (waterfall.Ready)
            {
                const float mistHeight = 2.6f;
                // By default only a band is laid out at the foot of the fall point (2.4 m thick), not covering the whole water surface: the box is that white mist band itself,
                // so to cover the whole pool surface increase Depth, and to keep only a thin veil set Depth to 0.5.
                const float mistDepth = 2.4f;
                float yawDegrees = MathF.Atan2(-lateral.Z, lateral.X) * 180f / MathF.PI;
                PlaceMist(
                    new Vector3(center.X, WaterY + mistHeight * 0.5f, center.Z),
                    yawDegrees,
                    new Vector3(2f * a + 0.6f, mistHeight, mistDepth));
            }
        }
        else if (!mistDetached && Mist.Ready)
        {
            if (Mist.Center != lastMistCenter)
            {
                mistDetached = true;
            }
            else
            {
                // Follow only the planar position: vertically, keep the bottom face on the pool surface along the user-given Height; yaw and width/height/depth all belong to the user.
                float mistHeightWorld = Mist.WorldHeight > 0f ? Mist.WorldHeight : 2.6f;
                lastMistCenter = new Vector3(center.X, WaterY + mistHeightWorld * 0.5f, center.Z);
                Mist.PosX = lastMistCenter.X;
                Mist.PosY = lastMistCenter.Y;
                Mist.PosZ = lastMistCenter.Z;
            }
        }

        // Rocks are laid along the visible edges of the axis-aligned rectangle: cast a ray from the center at the polar angle, find the parameter of its intersection with the rectangle edge,
        // then push out a little along the same direction to seal the water-land seam. The water surface is an axis-aligned rectangle, so rocks hug the rectangle's edges rather than the banner.
        RegisterRimInstances();
        for (int i = 0; i < rimLayout.Length; i++)
        {
            var instance = rimInstances[i];
            if (instance == null)
                continue;   // Background extraction has not yielded the template yet: skip laying rocks this frame, and they are filled in automatically once ready.

            var layout = rimLayout[i];
            float rad = layout.AngleDegrees * MathF.PI / 180f;
            float side = MathF.Sin(rad);                    // -1 = screen left (-lateral), +1 = screen right (+lateral)
            var dir = lateral * side + outward * MathF.Cos(rad);
            float dirLen = dir.Length();
            if (dirLen < 1e-5f) dirLen = 1f;
            dir /= dirLen;
            float tX = MathF.Abs(dir.X) > 1e-4f ? hx / MathF.Abs(dir.X) : float.MaxValue;
            float tZ = MathF.Abs(dir.Z) > 1e-4f ? hz / MathF.Abs(dir.Z) : float.MaxValue;
            var edge = center + dir * MathF.Min(tX, tZ);

            // The left half-ring is additionally pushed a little toward +lateral (screen right): laying the left side flush along the rectangle edge would bury whole rocks in the rock wall,
            // while the right half-ring is not pushed, so "the left shifts right, the right stays put".
            float pull = side < 0f ? -side * LeftPull : 0f;

            instance.PosX = edge.X + dir.X * layout.PushOut + lateral.X * pull;
            instance.PosZ = edge.Z + dir.Z * layout.PushOut + lateral.Z * pull;
            instance.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, layout.YawDegrees * MathF.PI / 180f);
            instance.Enable = sheet.Enabled;

            // Scale by world height (rather than a fixed scale): no matter how large the rock at this tier originally is in Rocks.glb, it is laid out at layout.Height;
            // width/depth follow proportionally by the template's aspect ratio, and the anchor is the bounding box center, so when the bottom edge is pressed to -Sink the center is at half height minus Sink.
            var local = rimFields[i]!.TemplateLocalSize;
            if (local == Vector3.Zero)
                continue;
            float scale = local.Y > 1e-4f ? layout.Height / local.Y : 1f;
            instance.Width = local.X * scale;
            instance.Height = local.Y * scale;
            instance.Depth = local.Z * scale;
            instance.PosY = instance.Height * 0.5f - layout.Sink;
        }

        // Particle anchors follow each frame: the spawn rectangles all lie across the lateral axis, and WorldOrigin drops directly into world coordinates.
        // boxReady is also the on/off gate for the two systems (see the systems loop below): the mist and foam spawn rectangles both come from MistVolume,
        // and without a placed box there is no usable spawn plane, so it is better to turn the whole group off than to fall back to the old behavior of "bursting a puff at the origin" or "misting at the camera's feet".
        // Note it only looks at Placed, not Ready: Place has already written Pos/Width/Height/Depth into the fields (independent of the bounding box),
        // so even if the box's GPU mesh is still in the loading queue, the particles emit by it immediately — parameter edits need not wait for loading.
        bool boxPlaced = Mist.Placed;
        var axis2 = new Vector2(lateral.X, lateral.Z);
        if (splash != null)
        {
            splash.WorldOrigin = new Vector3(foot.X + outward.X * 0.25f, WaterY + 0.08f, foot.Z + outward.Z * 0.25f);
            splash.SpawnAxis = axis2;
            splash.SpawnHalfLength = a;
            splash.SpawnHalfDepth = 0.6f;
            splash.InitialVelocity = new Vector3(outward.X * 1.5f, 3.5f, outward.Z * 1.5f);
            splash.FloorY = WaterY;
        }

        if (boxPlaced)
        {
            // The mist and foam follow MistVolume entirely: the box center (xz) and bottom face are the spawn plane, the box's +X axis is the banner direction,
            // and Width/Depth are directly the length and depth of the spawn rectangle — so the position, size, and orientation of "that thing you could call the white stuff"
            // can all be edited live in the properties panel, taking effect on the particles the next frame (compact rebuilds all particle positions from the box parameters each frame).
            var boxAxis = Mist.SpawnAxis;
            var boxAxis2 = new Vector2(boxAxis.X, boxAxis.Z);
            var boxOrigin = new Vector3(Mist.PosX, Mist.BottomY, Mist.PosZ);

            if (mist != null)
            {
                // Mist: grows from the box's bottom face; the rise speed is back-solved from Height so the mist climbs to the box top by the end of its lifetime.
                mist.WorldOrigin = boxOrigin;
                mist.SpawnAxis = boxAxis2;
                mist.SpawnHalfLength = Mist.HalfLength;
                mist.SpawnHalfDepth = Mist.HalfDepth;
                mist.InitialVelocity = new Vector3(
                    outward.X * 0.25f,
                    RiseSpeed(Mist.WorldHeight, mist.Lifetime, mist.Acceleration.Y),
                    outward.Z * 0.25f);
            }

            if (foam != null)
            {
                // Foam: it is that whitest mist band in the scene. It covers the box's bottom face, so Depth is its thickness ("not thick enough" → adjust Depth directly),
                // and Width is its length; it only floats up a tiny bit, not climbing with the box.
                foam.WorldOrigin = new Vector3(boxOrigin.X, boxOrigin.Y + 0.02f, boxOrigin.Z);
                foam.SpawnAxis = boxAxis2;
                foam.SpawnHalfLength = Mist.HalfLength;
                foam.SpawnHalfDepth = Mist.HalfDepth;
                foam.InitialVelocity = new Vector3(outward.X * 0.8f, 0.3f, outward.Z * 0.8f);
            }
        }

        // Particles do not take scene lighting (PS only computes Color.rgb * alpha, see Particles' Shade), so each frame scale a copy of the author color by the sun height
        // and write it back: full brightness at noon, clamped to 32% at night, sharing the same sun curve as the pool-bed caustics,
        // so in the scene the mist and caustics are bright and dark together. Only RGB is multiplied; alpha (coverage) does not change with the lighting, so the mist's volume stays stable.
        float light = ParticleLighting(App.Instance.DayPhase);
        if (splash != null) ApplyParticlesLighting(splash, SplashColor, SplashMid, SplashEnd, light);
        if (mist != null) ApplyParticlesLighting(mist, MistColor, MistMid, MistEnd, light);
        if (foam != null) ApplyParticlesLighting(foam, FoamColor, FoamMid, FoamEnd, light);

        // The mist box is itself a control: Panel.Update does not recurse, so it must be driven explicitly once here,
        // otherwise its world matrix is never uploaded and no highlight frame is drawn even when selected.
        if (Mist.Update(time))
            result = true;

        // Same driving as Campfire: the frame delta is clamped to [0, 0.1], so a long hitch does not teleport the whole phase field through an entire wrap.
        float delta = float.IsFinite(time) ? Math.Clamp(time, 0f, 0.1f) : 0f;
        bool on = sheet.Enabled;
        for (int i = 0; i < systems.Count; i++)
        {
            var system = systems[i];
            // The mist and foam spawn rectangles both come from MistVolume: from the frame the box is not ready (not finished Loading), turn the whole group off first,
            // otherwise they would fall back to the old behavior of "following the camera without a WorldOrigin" (a puff at the world origin or at the camera's feet).
            bool boxDriven = system == mist || system == foam;
            bool systemOn = on && (boxPlaced || !boxDriven);
            system.Enabled = systemOn;
            if (systemOn)
                system.Advance(delta);
        }

        return result;
    }

    /// <summary>
    /// Initialization entry point: explicitly place the mist box in App, with semantics identical to <see cref="Season.Controls.MistVolume.Place"/>
    /// (<paramref name="center"/> is exactly the panel's Pos, and <paramref name="size"/> is exactly Width/Height/Depth).
    /// When not called, this panel automatically lays out a default set from the pool surface on the frame the waterfall finishes Loading.
    /// </summary>
    internal void PlaceMist(Vector3 center, float yawDegrees = 0f, Vector3? size = null)
    {
        Mist.Place(center, yawDegrees, size);
        lastMistCenter = center;
        mistDetached = false;
        mistPlaced = true;
    }

    /// <summary>
    /// Append the 20 rim rocks to the Rocks panel's instance set: that is the small-block variant extracted from Assets/Rocks.glb, sharing one copy of the geometry
    /// and four textures on both sides, while the rock matrices are still written by this panel each frame (the rock ring follows when the waterfall is dragged). The extraction is a background task,
    /// and <see cref="Rocks.rockFields"/> is null before it completes, so here we try once per frame and, once it succeeds, set a flag and do not retry.
    /// </summary>
    void RegisterRimInstances()
    {
        if (rimRegistered)
            return;

        var rocks = App.Instance.rocks;
        if (rocks == null || rocks.rockFields == null || rocks.rockFields.Length == 0)
            return;   // Background extraction incomplete or failed: skip laying rocks this frame.

        var fields = rocks.rockFields;
        for (int i = 0; i < rimInstances.Length; i++)
        {
            // Rocks' Mesh.LogicalIndex ordering is 5 large + 5 medium + 5 small, and the rim takes the smallest tier;
            // clamped to the existing variant count so it does not go out of bounds when the asset version has fewer.
            var field = fields[Math.Min(fields.Length - 1, 10 + i % 5)];
            var instance = new MeshInstanceTransform
            {
                Name = $"WaterfallPoolRock{i}",
                Enable = false,   // Turned on in Update according to the waterfall's Enabled.
            };
            field.Instances.Add(instance);
            rimInstances[i] = instance;
            rimFields[i] = field;
        }

        rimRegistered = true;
    }

    /// <summary>
    /// Make the mist climb to near the box top within its lifetime: displacement = v·T + ½·a·T², back-solve v (then clamp to a practical range as a safeguard).
    /// So Height is simply "how tall the mist column is", and the initial speed no longer needs separate tuning.
    /// </summary>
    internal static float RiseSpeed(float height, float lifetime, float accelY)
    {
        float t = MathF.Max(lifetime, 0.1f);
        return Math.Clamp((height - 0.5f * accelY * t * t) / t, 0.05f, 3f);
    }

    /// <summary>
    /// The particles' "lighting": the particles are self-emissive colors that do not sample scene lighting, so they can only scale their author colors by the sun height themselves.
    /// It shares App.SunlightFactor, the sun curve, with the pool-bed caustics: noon => 1.10, night => NightParticleLight.
    /// A floor is used rather than zeroing out because fully black mist looks like a dropped-frame black block, not like white mist under moonlight.
    /// </summary>
    internal static float ParticleLighting(float dayPhase)
    {
        float sun = App.SunlightFactor(dayPhase);
        return NightParticleLight + (1.10f - NightParticleLight) * sun;
    }

    /// <summary>Multiply the author color by the lighting and write it back to the system; only RGB is multiplied, alpha is unchanged (coverage must not flicker with daylight).</summary>
    internal static void ApplyParticlesLighting(ParticleSystemEffect system, Vector4 color, Vector3 mid, Vector3 end, float light)
    {
        system.Color = new Vector4(color.X * light, color.Y * light, color.Z * light, color.W);
        system.ColorMid = mid * light;
        system.ColorEnd = end * light;
    }

    /// <summary>Horizontal normalization: drop the vertical component of the anisotropically scaled basis vector first, falling back to a default direction when degenerate.</summary>
    static Vector3 Horizontal(Vector3 v, Vector3 fallback)
    {
        v.Y = 0f;
        float length = v.Length();
        return length > 1e-6f ? v / length : fallback;
    }
}
