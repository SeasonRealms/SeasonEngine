// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Engine.Panels;

/// <summary>
/// Scene-medium driver for the sample world: it owns what the fog looks like in each of the three authored presets,
/// the only local volumes registered on the scene, and the colonnade that the sun-shaft preset needs as an occluder.
///
/// This is presentation, not simulation. WorldSettings owns which preset is running, whether the medium is on, how much
/// thicker than authored it is, and whether it follows the weather; everything else here - the extinction of a preset,
/// where its volumes sit, the camera and hour that make it readable - is authored next to the medium it describes, so
/// retuning one is an edit here rather than a settings migration. The settings panel therefore only ever writes
/// WorldSettings, and this panel reads it back every frame. Nothing flows the other way except the preset's own preview
/// hour and camera, which are deliberately not persisted: see CelestialLighting.PreviewHour.
///
/// Ordering. Update must run after CelestialLighting.Update, because the wind drift shares that clock's frame delta and
/// simulation scale rather than reading App.Time itself, and because the preset preview hour has to be applied after the
/// driver has published this frame's lighting. It must also run before the panels that draw the world, since the volume
/// list feeds the volumetric pass that runs later in the same frame.
/// </summary>
internal sealed class Fog : Panel
{
    // ── Authored media ──
    //
    // One profile per preset, holding only the global height-fog parameters. Local volumes are laid out separately
    // because they are geometry - a position and a size - rather than a handful of numbers that can sit in a table.
    sealed class Profile
    {
        public readonly float Extinction;
        public readonly float HeightFalloff;
        public readonly float Anisotropy;
        public readonly float NoiseAmount;
        public readonly float NoiseScale;
        public readonly float MaxDistance;
        public readonly Vector3 Albedo;
        public readonly Vector3 Wind;

        public Profile(float extinction, Vector3 albedo, float heightFalloff, float anisotropy,
            float noiseAmount, float noiseScale, Vector3 wind, float maxDistance)
        {
            Extinction = extinction;
            Albedo = albedo;
            HeightFalloff = heightFalloff;
            Anisotropy = anisotropy;
            NoiseAmount = noiseAmount;
            NoiseScale = noiseScale;
            Wind = wind;
            MaxDistance = maxDistance;
        }
    }

    // Weak, cool, and almost isotropic: morning mist is lit from everywhere and reads as a veil rather than as a beam.
    static readonly Profile MorningProfile = new(0.011f, new Vector3(0.90f, 0.93f, 0.98f),
        heightFalloff: 0.10f, anisotropy: 0.30f, noiseAmount: 0.32f, noiseScale: 0.055f,
        wind: new Vector3(0.40f, 0f, 0.16f), maxDistance: 240f);

    // Warm and strongly forward-scattering, which is what turns the low sun into visible beams: the phase function is
    // what decides whether light survives along the view ray or only when looking into the sun.
    static readonly Profile ShaftProfile = new(0.030f, new Vector3(1f, 0.95f, 0.86f),
        heightFalloff: 0.04f, anisotropy: 0.78f, noiseAmount: 0.18f, noiseScale: 0.075f,
        wind: new Vector3(0.15f, 0f, 0.05f), maxDistance: 220f);

    // Cold and fairly forward-scattering so the lamp halo has a bright core without smearing across the whole street.
    static readonly Profile NightProfile = new(0.016f, new Vector3(0.78f, 0.84f, 0.96f),
        heightFalloff: 0.05f, anisotropy: 0.55f, noiseAmount: 0.14f, noiseScale: 0.08f,
        wind: new Vector3(0.10f, 0f, 0.04f), maxDistance: 160f);

    // Weather factor bounds. Clear sky keeps the preset at 60% of its authored density instead of collapsing to
    // nothing, so switching the weather link on and off stays a comparison rather than a disappearance.
    const float ClearWeatherWeight = 0.6f;
    const float WeatherWeightRange = 1.4f;

    // ── Local volumes ──
    //
    // All five are registered once and toggled per preset. Registering them up front rather than adding and removing them
    // on every preset switch keeps SceneFog.HistoryRevision stable across a switch on its own, so the only thing that
    // invalidates the volumetric history is the explicit reset below - which is the point of that reset.
    readonly FogVolume groundMist = new()
    {
        Shape = FogVolumeShape.Box,
        EdgeFeather = 0.85f,
        NoiseAmount = 0.35f,
        NoiseScale = 0.06f,
        Albedo = new Vector3(0.92f, 0.95f, 1f),
    };

    readonly FogVolume farBank = new()
    {
        Shape = FogVolumeShape.Box,
        EdgeFeather = 0.9f,
        NoiseAmount = 0.4f,
        NoiseScale = 0.05f,
        Albedo = new Vector3(0.92f, 0.95f, 1f),
    };

    readonly FogVolume shaftAir = new()
    {
        Shape = FogVolumeShape.Box,
        EdgeFeather = 0.45f,
        NoiseAmount = 0.15f,
        NoiseScale = 0.07f,
        Albedo = new Vector3(1f, 0.96f, 0.88f),
    };

    readonly FogVolume lampHalo = new()
    {
        Shape = FogVolumeShape.Sphere,
        EdgeFeather = 0.9f,
        NoiseAmount = 0.1f,
        NoiseScale = 0.09f,
        Albedo = new Vector3(0.85f, 0.9f, 1f),
    };

    readonly FogVolume roomHaze = new()
    {
        Shape = FogVolumeShape.Box,
        EdgeFeather = 0.35f,
        NoiseAmount = 0.08f,
        NoiseScale = 0.12f,
        Albedo = new Vector3(0.88f, 0.92f, 1f),
    };

    // ── Sun-shaft occluders ──
    //
    // A colonnade, because a colonnade is what a light shaft is: the beams are the gaps, and they only exist because
    // something solid blocks the rest. It is honest geometry that also casts into the cascaded shadow map, which is the
    // source the volumetric pass samples - an invisible occluder would have to be excluded from the camera pass while
    // still being submitted to the shadow pass, and this control type gates both on Alpha.
    const float ColonnadeX = 14f;
    const int ColonnadeColumns = 7;
    const float ColonnadeSpacing = 3.2f;
    const float ColumnHalfWidth = 0.4f;
    const float ColumnHeight = 7f;

    readonly InstancedMesh3D colonnade = new() { Name = "FogShaftColonnade" };

    // Where each preset wants the camera and the clock. Camera moves are a jump for the same reason the hour is: a preset
    // is a framing, not a transition, and easing between two framings would only make the switch slower to read.
    static readonly Vector3 MorningCamera = new(0f, 4f, -6f);
    static readonly Vector3 MorningTarget = new(0f, 3.5f, -2.5f);
    static readonly Vector3 ShaftCamera = new(-24f, 5.5f, 0f);
    static readonly Vector3 ShaftTarget = new(4f, 4.5f, 0f);
    static readonly Vector3 NightCamera = new(5f, 3.4f, 12f);
    static readonly Vector3 NightTarget = new(5f, 4f, 20f);

    // Preview hours. 7 puts the sun about 12 degrees up in the east, which is the window where beams are long, and 22
    // puts it well below the horizon so the street lamp is the only thing lighting the medium.
    const float ShaftHour = 7f;
    const float NightHour = 22f;

    // Null until the first Update, which applies the saved preset without retargeting: the startup camera and the saved
    // hour are already what the morning preset wants, and moving them on frame one would fight the world's own start.
    FogPreset? _applied;

    internal Fog()
    {
        var scene = App.Instance.Fog;

        scene.Add(groundMist);
        scene.Add(farBank);
        scene.Add(shaftAir);
        scene.Add(lampHalo);
        scene.Add(roomHaze);

        BuildColonnade();
        AddControl(colonnade);
    }

    static Profile Of(FogPreset preset) => preset switch
    {
        FogPreset.SunShafts => ShaftProfile,
        FogPreset.NightLamps => NightProfile,
        _ => MorningProfile,
    };

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null,
        float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, width: width, height: height);

        var world = WorldSettings.Current;

        if (_applied != world.FogPreset)
        {
            // The very first application adopts the saved preset; only a later change is a switch and therefore worth
            // retargeting the camera and the clock for.
            ApplyPreset(world.FogPreset, switchView: _applied != null);
            _applied = world.FogPreset;
        }

        // App pins the medium when it owns a fixed island fog. In that mode this panel keeps only what a preset is for:
        // the volume layout, the colonnade and the wind clock. Writing the numbers here as well would fight App's fixed
        // values every frame, and the one value that must not oscillate is MaxDistance - its setter resets the fog
        // history, so two writers with different ideas of it would disable the temporal filter outright.
        if (App.FixedIslandFog)
        {
            AdvanceWind();
        }
        else
        {
            ApplyMedium(world);
        }

        if (colonnade != null)
        {
            // Visible only in the preset that needs it, and Alpha is the switch that also drops it from the shadow pass -
            // see the comment on the field. CastShadows stays on in every preset so the two passes never disagree.
            colonnade.Alpha = world.FogPreset == FogPreset.SunShafts ? 1f : 0f;
            if (colonnade.Update(time))
            {
                result = true;
            }
        }

        return result;
    }

    /// <summary>
    /// Writes this frame's numbers: the preset's own global medium, scaled by the two world-facing knobs (density and
    /// the weather link), plus the wind integration.
    ///
    /// These are written every frame rather than on change so the weather factor can keep up with the sky without this
    /// panel having to know when a weather segment turned over. None of the setters here reset the volumetric history -
    /// extinction, density and wind are the quantities the temporal filter exists to smooth - so a slow weather drift
    /// accumulates across frames instead of restarting the filter each time.
    /// </summary>
    void ApplyMedium(WorldSettings world)
    {
        var scene = App.Instance.Fog;
        var profile = Of(world.FogPreset);

        float density = Math.Max(0f, float.IsFinite(world.FogDensityScale) ? world.FogDensityScale : 1f);
        float weather = world.FogWeatherLink
            ? ClearWeatherWeight + WeatherWeightRange * CelestialLighting.CloudCoverage
            : 1f;

        scene.Enabled = world.FogEnabled;
        scene.Extinction = profile.Extinction * density * weather;
        scene.Albedo = profile.Albedo;
        scene.HeightFalloff = profile.HeightFalloff;
        scene.Anisotropy = profile.Anisotropy;
        scene.NoiseAmount = profile.NoiseAmount;
        scene.NoiseScale = profile.NoiseScale;
        scene.WindVelocity = profile.Wind;
        scene.MaxDistance = profile.MaxDistance;

        // Shared clock, not App.Time: the drift stops when the sky is frozen and stretches with the day-night rate, so
        // the medium and the sun never disagree about how fast the world is moving. The delta is never negative, so the
        // one thing SceneFog.Advance refuses is the one thing this cannot hand it.
        AdvanceWind();
    }

    /// <summary>
    /// Advances the noise clock by this frame's share of the day-night rate. Split out of ApplyMedium because the two
    /// medium owners differ: the preset path calls it after writing its numbers, and App.FixedIslandFog calls it directly
    /// when App owns the numbers instead.
    /// </summary>
    static void AdvanceWind()
    {
        float delta = CelestialLighting.FrameDelta * CelestialLighting.SimulationScale;
        if (delta > 0f && float.IsFinite(delta))
            App.Instance.Fog.Advance(delta);
    }

    /// <summary>
    /// Replaces the medium outright: global numbers, local volume layout, and - when the caller is a user-driven switch -
    /// the camera and the clock that make the preset readable.
    ///
    /// The history reset is the same one a jump in time needs and for the same reason: every volume moved and the
    /// extinction changed by a factor, so last frame's radiance describes a different medium and reprojecting it would
    /// smear the old preset across the first frames of the new one.
    /// </summary>
    void ApplyPreset(FogPreset preset, bool switchView)
    {
        var scene = App.Instance.Fog;

        // The two generic atmosphere boxes exist only to fake a global medium for the camera they were authored for. With
        // App owning that medium they would stack on top of it and roughly double the density where the player starts, so
        // they sit out the pinned mode entirely. The three effect-specific volumes below are not atmosphere - beams need
        // air, a lamp needs a halo, the room needs haze - and stay in place in every mode.
        bool pinned = App.FixedIslandFog;
        groundMist.Enabled = !pinned && preset == FogPreset.Morning;
        if (preset == FogPreset.Morning)
        {
            // Mist hugging the ground around the player, feathered wide enough that its box never reads as a box, plus a
            // far bank to give the view depth. Both sit above y=0 so the ground plane itself stays clear of the medium.
            groundMist.Position = new Vector3(0f, 1.4f, 10f);
            groundMist.Size = new Vector3(70f, 4f, 60f);
            groundMist.Extinction = 0.022f;

            farBank.Position = new Vector3(0f, 5f, 48f);
            farBank.Size = new Vector3(90f, 12f, 26f);
            farBank.Extinction = 0.010f;
        }
        farBank.Enabled = !pinned && preset == FogPreset.Morning;

        shaftAir.Enabled = preset == FogPreset.SunShafts;
        if (preset == FogPreset.SunShafts)
        {
            // Covers the whole run from the camera to the colonnade and a little past it, so the beams are read inside
            // the medium rather than only where they land.
            shaftAir.Position = new Vector3(-8f, 4.5f, 0f);
            shaftAir.Size = new Vector3(46f, 12f, 30f);
            shaftAir.Extinction = 0.035f;
        }

        lampHalo.Enabled = preset == FogPreset.NightLamps;
        if (preset == FogPreset.NightLamps)
        {
            // Sphere at the bulb, not around the post: the point light sits at y=4.03 and the halo has to be centred on
            // the scattering source or the near edge dominates.
            lampHalo.Position = new Vector3(5f, 4.03f, 20f);
            lampHalo.Size = new Vector3(11f);
            lampHalo.Extinction = 0.030f;
        }

        roomHaze.Enabled = preset == FogPreset.NightLamps;
        if (preset == FogPreset.NightLamps)
        {
            roomHaze.Position = new Vector3(6f, 3f, 60f);
            roomHaze.Size = new Vector3(12.4f, 6.4f, 12.4f);
            roomHaze.Extinction = 0.012f;
        }

        scene.ResetHistory();

        if (!switchView)
            return;

        var app = App.Instance;
        switch (preset)
        {
            case FogPreset.SunShafts:
                app.CameraPos = ShaftCamera;
                app.CameraTarget = ShaftTarget;
                CelestialLighting.PreviewHour(ShaftHour);
                break;
            case FogPreset.NightLamps:
                app.CameraPos = NightCamera;
                app.CameraTarget = NightTarget;
                CelestialLighting.PreviewHour(NightHour);
                break;
            default:
                app.CameraPos = MorningCamera;
                app.CameraTarget = MorningTarget;
                // Back to the hour the world was authored to start at, not to whatever hour the previous preview left.
                CelestialLighting.PreviewHour(WorldSettings.Current.StartHour);
                break;
        }
    }

    /// <summary>
    /// One unit cube as shared template, projected once per column. Template stays a unit cube because per-instance
    /// Width/Height/Depth are what scale it - see CubeField for the same convention - so the columns and the lintel can
    /// share one geometry.
    /// </summary>
    void BuildColonnade()
    {
        BuildCube();

        colonnade.Instances.Clear();

        float start = -((ColonnadeColumns - 1) * ColonnadeSpacing) * 0.5f;
        for (int i = 0; i < ColonnadeColumns; i++)
        {
            colonnade.Instances.Add(new MeshInstanceTransform
            {
                // Columns run ground to head height. Pos is the box centre, which for a column based at y=0 is half its
                // height.
                PosX = ColonnadeX,
                PosY = ColumnHeight * 0.5f,
                PosZ = start + i * ColonnadeSpacing,
                Width = ColumnHalfWidth * 2f,
                Height = ColumnHeight,
                Depth = ColumnHalfWidth * 2f,
            });
        }

        // Lintel: ties the columns together so they read as architecture instead of a row of separate blocks.
        colonnade.Instances.Add(new MeshInstanceTransform
        {
            PosX = ColonnadeX,
            PosY = ColumnHeight + 0.3f,
            PosZ = 0f,
            Width = 0.9f,
            Height = 0.6f,
            Depth = (ColonnadeColumns - 1) * ColonnadeSpacing + 0.9f,
        });
    }

    void BuildCube()
    {
        const float h = 0.5f;

        // Same face winding as CubeField: the engine is left-handed with FrontCounterClockwise=0, so clockwise triangles
        // seen from outside are front-facing.
        AddFace(new Vector3(-h, -h, -h), new Vector3(+1, 0, 0), new Vector3(0, +1, 0), new Vector3(0, 0, -1));
        AddFace(new Vector3(+h, -h, +h), new Vector3(-1, 0, 0), new Vector3(0, +1, 0), new Vector3(0, 0, +1));
        AddFace(new Vector3(-h, -h, +h), new Vector3(0, 0, -1), new Vector3(0, +1, 0), new Vector3(-1, 0, 0));
        AddFace(new Vector3(+h, -h, -h), new Vector3(0, 0, +1), new Vector3(0, +1, 0), new Vector3(+1, 0, 0));
        AddFace(new Vector3(-h, +h, -h), new Vector3(+1, 0, 0), new Vector3(0, 0, +1), new Vector3(0, +1, 0));
        AddFace(new Vector3(-h, -h, +h), new Vector3(+1, 0, 0), new Vector3(0, 0, -1), new Vector3(0, -1, 0));
    }

    void AddFace(Vector3 origin, Vector3 u, Vector3 v, Vector3 normal)
    {
        var verts = new Vertex[4];
        verts[0] = MakeVertex(origin, new Vector2(0, 1), normal);
        verts[1] = MakeVertex(origin + u, new Vector2(1, 1), normal);
        verts[2] = MakeVertex(origin + v, new Vector2(0, 0), normal);
        verts[3] = MakeVertex(origin + u + v, new Vector2(1, 0), normal);

        var indices = new ushort[] { 2, 3, 1, 2, 1, 0 };

        colonnade.Surfaces.Add(new Surface
        {
            Vertices = verts,
            Indices = indices,
            // Empty path is the documented pure-color mode: no texture, no material file, just the tint below. The
            // colonnade is meant to be read as a silhouette, and a texture would only add noise to the beams.
            BaseColorTexturePath = "",
            BaseColor = new Vector4(0.26f, 0.25f, 0.23f, 1f),
            Mode = SurfaceBlendMode.Opaque,
        });
    }

    static Vertex MakeVertex(Vector3 pos, Vector2 uv, Vector3 normal) => new()
    {
        Position = pos,
        TexCoord = uv,
        Normal = normal,
        Tangent = new Vector4(1, 0, 0, 1),
        Joints = Vector4.Zero,
        Weights = Vector4.Zero,
    };
}
