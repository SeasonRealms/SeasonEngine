// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Season.Rendering.Effects;

namespace Engine.Panels;

/// <summary>
/// Campfire on the south-west meadow: a charred log teepee, an ember bed, three GPU particle
/// systems (flame, sparks, smoke) and one flickering point light, all sharing a single anchor.
///
/// Both meshes were generated offline by the repo's own AI pipeline and ship as self-contained
/// GLBs (Trellis embeds the PBR atlas inside the file), so nothing here references SeasonImage or
/// SeasonThreeD at runtime. The generator that produced them lives in .dbg/_campfire_assets:
///   Assets/CampfireLogs.glb  2966 tris, local 0.85 x 0.78 x 0.99, baked bark and charcoal
///   Assets/EmberBed.glb      1356 tris, local 0.97 x 0.17 x 0.97, baked ash and glowing embers
/// Prompts and seeds are recorded in Resources/Raw/Assets/MODEL_FONT_SOURCES_AND_LICENSES.md.
///
/// Placement is the empty south-west quadrant at (-16, -14). The road runs along x = 0, the pond
/// sits at (14, -12), the grove starts at x = -40, the sphere row is at (-15, 20), the robots line
/// the road at |x| = 2 and the player spawns at the origin, so this spot is at least 12 m from
/// anything solid while still 21 m from the spawn point and well inside the 150x150 grass tile.
///
/// Sizing follows House.cs. The generated meshes come out of Trellis normalized to a unit box, so
/// Width/Height/Depth are settled once from the measured LocalSize after the GLB finishes loading;
/// deriving all three axes from one factor keeps the generated aspect ratio instead of stretching
/// the prop. The anchor is the bounding-box center, so PosY = Height / 2 rests the lower edge on
/// the grass at y = 0.
///
/// The particle systems follow WeatherController's usage pattern: ExternalTime is on so Advance is
/// the only clock, Record is driven by FrameSchedule because they register as ComputeEffects, and
/// each system owns its own budget, instance buffer and indirect argument block. All three are
/// disk-spawned, which is the branch where vertical motion comes from Acceleration alone (the disk
/// starts at zero velocity) and Spread widens or narrows the column with age.
/// </summary>
internal class Campfire : Panel
{
    // -- Anchor ---------------------------------------------------------------

    /// <summary>Fire center on the grass. Also the spawn disk center of every particle system.</summary>
    const float CenterX = -16f;
    const float CenterZ = -14f;

    // -- Meshes ---------------------------------------------------------------

    const string LogsPath = "Assets/CampfireLogs.glb";
    const string BedPath = "Assets/EmberBed.glb";

    // Target world width of each mesh, measured across X. The generated assets are 0.85 m and
    // 0.97 m wide at scale 1, so these are also the uniform scales (0.91 and 0.91).
    const float PileWidth = 0.78f;
    const float BedWidth = 0.88f;

    // -- Fire profile ---------------------------------------------------------

    /// <summary>Height the flame column starts at, just above the ash surface.</summary>
    const float FlameBaseY = 0.10f;

    /// <summary>Half-life of one tongue in seconds; the whole column is 10 of these deep.</summary>
    const float FlameLifetime = 0.85f;

    /// <summary>Upward acceleration of the flame in m/s^2. At FlameLifetime it lifts a tongue 0.87 m.</summary>
    const float FlameAcceleration = 2.4f;

    /// <summary>
    /// Emission height of the point light: two thirds up the flame column, which is where the eye expects the
    /// source and far enough off the grass to stay in the same order of magnitude as the sun. Placing it just
    /// above the ash put eighty times the sun's peak irradiance on the ground directly under the fire.
    /// </summary>
    const float LightHeight = 0.70f;

    /// <summary>
    /// Base intensity, before flicker. Calibrated against the two lights already in the scene: the sun peaks at
    /// 4 and the carried torch is 5 over 12 m, which lights the ground out to about 4 m. A campfire is a little
    /// stronger than a torch and reads to roughly 5 m, so 6 keeps it in family instead of blowing out the grass.
    /// </summary>
    const float LightIntensity = 6f;

    const float LightRange = 12f;

    internal Season.Controls.Model logs;
    internal Season.Controls.Model bed;

    Season.Rendering.LightSource campfireLight;

    // Null when the backend declined compute support; the update loop then has nothing to tick.
    ParticleSystemEffect? flame;
    ParticleSystemEffect? sparks;
    ParticleSystemEffect? smoke;

    /// <summary>Only the systems that actually registered; a backend without compute leaves this empty.</summary>
    readonly List<ParticleSystemEffect> systems = new();

    bool logsSettled;
    bool bedSettled;

    /// <summary>Accumulated frame delta, the argument of the flicker oscillators.</summary>
    float elapsed;

    internal Campfire()
    {
        logs = new Season.Controls.Model()
        {
            Name = LogsPath,
            PosX = CenterX,
            PosZ = CenterZ,
            // CastShadows is off for two separate reasons, and both are about the GI proxy it would emit.
            //
            // 1. The proxy is the whole world AABB of the teepee, and the point light sits at LightHeight
            //    0.70 m inside that box (the settled pile is ~0.716 m tall). Every probe ray that hits the
            //    teepee therefore lands a few centimetres from the light, where 1/d^2 is enormous, and a
            //    2 m probe grid cannot resolve that spike except as one grid-aligned bright square.
            // 2. Now that GiPunctualShadow is on (see App), a solid proxy that encloses the light would
            //    occlude the campfire from every probe in the volume: the SDF is negative everywhere inside
            //    the box, so every shadow ray toward the flame reports blocked and the fire stops lighting
            //    the ground at all.
            //
            // The cost is that the teepee no longer casts a CSM shadow. At 0.78 m wide next to a fire the
            // player stands around, that silhouette was never readable; the grass under the pile is already
            // fully lit by the unshadowed point light.
            CastShadows = false,
        };

        bed = new Season.Controls.Model()
        {
            Name = BedPath,
            PosX = CenterX,
            PosZ = CenterZ,
            // The baked base color already carries the ember glow, so the bed is drawn unlit: at night
            // it must read as self-luminous instead of waiting for the sun. Unlit keeps the albedo
            // texture and multiplies it by MaterialColor (renderMode 0 branch), which here stays at
            // the default white on purpose. Tinting it up to 1.7 pushed the whole 0.9 m disc past the
            // tonemapper and it read as a flat light block rather than ash and coals.
            Unlit = true,
            CastShadows = false,
        };

        AddControl(logs);
        AddControl(bed);

        // Registration is where a backend without compute support quietly drops the effects, so the
        // fields hold null afterwards and the update loop has nothing to tick.
        flame = Register(CreateFlame());
        sparks = Register(CreateSparks());
        smoke = Register(CreateSmoke());

        campfireLight = App.Instance.Lighting.Add(new Season.Rendering.LightSource
        {
            Name = "Campfire",
            Kind = Season.Rendering.LightKind.Point,
            Color = new Vector3(1f, 0.62f, 0.30f),
            Intensity = LightIntensity,
            Position = new Vector3(CenterX, LightHeight, CenterZ),
            Range = LightRange,
            // Point lights do not cast shadows in this engine: cube shadow maps are outside the 1-5
            // scope, so the flag would only raise the Bake trimming weight for a shadow it cannot cast.
            CastShadows = false,
            // Below the street lamp's 50. Bake trims the authoring list down to eight GPU slots at
            // nightfall by priority, and a campfire is a foreground detail next to the lamp.
            Priority = 40,
        });
    }

    /// <summary>
    /// Flame: additive, disk-born, converging tongues. Color runs hot white-yellow at birth through
    /// orange to a dark red tip, while SizeEnd collapses the quad so each tongue ends in a point.
    /// The negative Spread pulls the column back toward the axis as it rises, which is what turns a
    /// cylinder into a cone.
    /// </summary>
    ParticleSystemEffect CreateFlame() => new(768)
    {
        ExternalTime = true,
        WorldOrigin = new Vector3(CenterX, FlameBaseY, CenterZ),
        // World-anchored: a fire that follows the camera window would slide across the grass.
        CameraWindow = false,
        Seed = 0x0F1A0001,
        Density = 0.5f,
        Lifetime = FlameLifetime,
        SpawnDisk = true,
        SpawnRadius = 0.21f,
        Acceleration = new Vector3(0f, FlameAcceleration, 0f),
        Spread = -0.20f,
        Turbulence = 0.30f,
        FadeIn = 0.10f,
        FadeOut = 0.50f,
        ParticleSize = 0.075f,
        SizeEnd = 0.010f,
        Color = new Vector4(2.40f, 1.05f, 0.28f, 0.42f),
        ColorMid = new Vector3(2.80f, 1.85f, 0.62f),
        ColorEnd = new Vector3(0.55f, 0.10f, 0.02f),
        Additive = true,
        CurveMode = true,
        NoiseAmount = 0.85f,
    };

    /// <summary>
    /// Sparks: a tenth of the candidates survive, so roughly 26 embers are alive at any moment out of
    /// 256. They outlive the flame by a factor of two, spread outward instead of converging and take
    /// a strong turbulence term, which is what throws them clear of the fire in a scattered arc.
    /// </summary>
    ParticleSystemEffect CreateSparks() => new(256)
    {
        ExternalTime = true,
        WorldOrigin = new Vector3(CenterX, FlameBaseY, CenterZ),
        CameraWindow = false,
        Seed = 0x0F1A0002,
        Density = 0.10f,
        Lifetime = 1.9f,
        SpawnDisk = true,
        SpawnRadius = 0.16f,
        Acceleration = new Vector3(0.25f, 2.6f, 0.15f),
        Spread = 0.45f,
        Turbulence = 0.85f,
        // A light breeze carries them away from the fire rather than straight up.
        WindVelocity = new Vector3(0.35f, 0f, 0.12f),
        FadeIn = 0.03f,
        FadeOut = 0.55f,
        ParticleSize = 0.014f,
        SizeEnd = 0.004f,
        Color = new Vector4(3.20f, 1.60f, 0.50f, 0.95f),
        ColorMid = new Vector3(2.20f, 0.72f, 0.16f),
        ColorEnd = new Vector3(0.90f, 0.16f, 0.03f),
        Additive = true,
        CurveMode = true,
        NoiseAmount = 0.4f,
    };

    /// <summary>
    /// Smoke: alpha-blended rather than additive, so it darkens the sky instead of adding to it. It
    /// starts as a small tight puff above the fire and grows to five times its size over its life,
    /// which is what gives the column its widening shape; FadeOut dissolves it before it reaches the
    /// top of the plume so nothing pops out of existence.
    /// </summary>
    ParticleSystemEffect CreateSmoke() => new(320)
    {
        ExternalTime = true,
        WorldOrigin = new Vector3(CenterX, FlameBaseY, CenterZ),
        CameraWindow = false,
        Seed = 0x0F1A0003,
        Density = 0.45f,
        Lifetime = 3.4f,
        SpawnDisk = true,
        SpawnRadius = 0.14f,
        Acceleration = new Vector3(0.10f, 0.85f, 0.05f),
        Spread = 0.12f,
        Turbulence = 0.42f,
        // Seeded from the world wind rather than a fixed breeze: Update writes Wind.Velocity here every frame, so
        // this value only covers the frame before the first Update and the case where the effect is not advanced.
        WindVelocity = Wind.Velocity,
        FadeIn = 0.12f,
        FadeOut = 0.45f,
        ParticleSize = 0.11f,
        SizeEnd = 0.52f,
        Color = new Vector4(0.36f, 0.35f, 0.34f, 0.26f),
        ColorMid = new Vector3(0.40f, 0.39f, 0.40f),
        ColorEnd = new Vector3(0.55f, 0.56f, 0.60f),
        Additive = false,
        CurveMode = true,
        NoiseAmount = 0.7f,
    };

    /// <summary>
    /// Registers one particle system and returns it, or disposes it and returns null when the backend
    /// declines. RegisterCompute reports a declined Initialize as false, and Initialize already tears
    /// its own resources down, so a never-initialized system is all that is left to release here.
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

        // Sizing needs the GLB bounds, which arrive asynchronously with the queued load, so retry
        // until each mesh has reported its LocalSize.
        if (!logsSettled) logsSettled = Settle(logs, PileWidth);
        if (!bedSettled) bedSettled = Settle(bed, BedWidth);

        // Advance throws on negative or non-finite seconds, and the same 0.1 s ceiling the weather
        // uses keeps a long stall from teleporting the whole field through one wrap.
        float delta = float.IsFinite(time) ? Math.Clamp(time, 0f, 0.1f) : 0f;
        elapsed += delta;

        float flicker = Flicker(elapsed);

        // The plume is the scene's one airborne material, so it is also where the world wind shows. Wind.Velocity
        // is read rather than cached so the settings rows act on the next frame; the shader integrates it as
        // center += wind * age, which means a level change re-shapes the whole column immediately instead of
        // bending only the particles born afterwards - the field is a closed-form function of age, not a
        // simulation state, so there is nothing to ease in.
        //
        // Only the smoke follows it: the flame and the sparks keep their authored values because a fire's own
        // convection dominates a few metres above the grass, and the brief asked for the smoke first.
        if (smoke != null)
            smoke.WindVelocity = Wind.Velocity;

        for (int i = 0; i < systems.Count; i++)
        {
            var system = systems[i];
            system.Advance(delta);

            // Rising speed is the flame's height, so breathing it here makes the tongues lick higher
            // and lower instead of the whole fire only changing brightness.
            if (ReferenceEquals(system, flame))
                system.Acceleration = new Vector3(0f, FlameAcceleration * (0.9f + 0.1f * flicker), 0f);
        }

        if (campfireLight != null)
            campfireLight.Intensity = LightIntensity * flicker;

        // base.Update only flushes the optional arguments down onto this panel; it does not tick the
        // sub-controls. Their Update result has to be folded into ours, or a resize/anchor change that
        // lands on this frame never marks the panel dirty.
        bool meshesChanged = (logs?.Update(time) ?? false) | (bed?.Update(time) ?? false);

        if (meshesChanged)
            result = true;

        return result;
    }

    /// <summary>
    /// One-shot sizing once a mesh has reported its bounds.
    /// Width/Height/Depth are per-axis target world sizes, so scaling every axis by the same factor
    /// keeps the generated aspect ratio; leaving one of them unset would settle it to the raw local
    /// size and stretch the prop on that axis.
    /// </summary>
    bool Settle(Season.Controls.Model model, float targetWidth)
    {
        var local = model.LocalSize;
        if (local.X <= 1e-6f)
            return false;   // Bounds not established yet; retry on the next frame.

        float scale = targetWidth / local.X;
        float height = local.Y * scale;

        model.Width = targetWidth;
        model.Height = height;
        model.Depth = local.Z * scale;
        model.PosX = CenterX;
        model.PosZ = CenterZ;
        // The anchor is the bounding-box center, so half the settled height lands the lower edge on y = 0.
        model.PosY = height * 0.5f;

        return true;
    }

    /// <summary>
    /// Flame flicker in [0.71, 1.29]. Three fast sines beat against each other inside roughly half a
    /// second while two slow ones add the gusts; the frequencies are mutually incommensurate, so the
    /// pattern does not visibly loop.
    /// </summary>
    static float Flicker(float t)
    {
        float fast = 0.10f * MathF.Sin(t * 7.7f)
                   + 0.06f * MathF.Sin(t * 17.3f + 1.1f)
                   + 0.04f * MathF.Sin(t * 29.1f + 2.3f);

        float gust = 0.06f * MathF.Sin(t * 0.83f + 0.4f)
                   + 0.03f * MathF.Sin(t * 0.31f);

        return 1f + fast + gust;
    }
}
