// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Engine.Panels;

/// <summary>
/// Procedural sea surface: a single 1400x1400m subdivided plane with a 255x255 vertex grid,
/// extending outward from the land in all directions.
/// Land-to-sea transition is handled by the Beach panel through GPU-instanced procedural beach tiles.
/// The earlier monolithic procedural beach slopes, four 128x33 grids merged into the sea, were retired in 2026-08
/// because the large mesh was too complex and hard to compose, and small instanced tiles work better.
/// Heights and normals are baked once on the CPU from deterministic noise, using a fixed seed that stays stable across launches.
/// Sea height can be adjusted at runtime through <see cref="SeaLevel"/>,
/// with a default of y=-1.5, a metre and a half below grass at y=0. With Mountains using Sink=10, their lower edges remain
/// submerged, making the ring read as island chains in the sea.
///
/// Three presentations, chosen by <see cref="Season.Rendering.SeaMode"/>. The geometry below serves all of them; only
/// the material, the Y offset, and the two swell scalars differ, and both variants are built once at startup, so
/// switching is an Enable flip plus a Position write with no upload and no pipeline rebuild.
///
/// <see cref="Season.Rendering.SeaMode.Static"/> (the default) is the pre-P3 look, restored: this mesh is the visible
/// surface, sitting at <see cref="SeaLevel"/>, with the baked noise normals and a low-roughness teal material, so sun
/// and moon draw specular glints across it through ordinary PBR. There is no refraction and no screen-space
/// reflection on that path, and nothing is evaluated per frame beyond the world matrix.
///
/// <see cref="Season.Rendering.SeaMode.Swell"/> is the middle option: the same mesh, the same material, the same baked
/// relief, plus a travelling wave evaluated in the vertex stage of the ordinary PBR shader. It keeps the flat-shaded
/// cartoon read of the static path - three radial sines instead of Gerstner, so no crest sharpening and no ripple
/// layer - and it costs nothing on the CPU: the only per-frame work is a phase accumulation of two float adds. The
/// displacement is gated to the water near the camera (see Mesh3D.WaveFadeRadius, 750m), so a far horizon and a high
/// altitude both fall back to the baked relief on a single compare per vertex; 750m is what makes the sea read as
/// moving water from the 20m tower deck rather than only from the shore.
///
/// <see cref="Season.Rendering.SeaMode.Dynamic"/> is P3/P4: the visible surface is a
/// <see cref="Season.Rendering.WaterPlane"/> drawn by the SceneWater pass - the only stage that can both sample this
/// frame's scene color (through the refraction snapshot) and depth, and write the scene color - and this mesh sinks
/// <see cref="SeaDepth"/> below it to act as the <b>sea floor</b>, giving the shader's depth-absorption term real
/// geometry to read a thickness from. Fresnel, refraction, sky reflection, and the Hi-Z screen-space reflection live
/// in the water shader.
///
/// Geometry budget: Surface.Indices uses ushort, so the hard vertex limit is 65535, matching the rule in Ground.BuildRoad.
/// The sea uses 255x255 = 65025 vertices, right up against that limit, with grid spacing 1400/254 about 5.512m,
/// and 254x254x6 = 387096 ushort indices. The mesh is static geometry uploaded once during loading,
/// because the engine currently has no cross-backend dynamic vertex path and UpdateMesh3D only synchronizes transforms and materials.
/// That is fine on both paths: the static one never animates geometry by design, and the dynamic one moves the waves in
/// the water shader (P4) instead. Holding both variants costs one extra VB/IB pair in VRAM (about 6MB) and nothing per
/// frame; the CPU-side vertex and index arrays are shared between them.
///
/// Area contract: land is the 150x150 grass tile, 22500m², while the sea was expanded three times in 2026-08 to 1960000m²,
/// ending up as a 1400m square centered at the world origin and extending to +/-700m.
/// That reaches the visual horizon and works with the east-west openings in Mountains so the sun and moon can rise from the sea
/// and set at the line where water meets sky. Since the skybox uses a NoDepth PSO from rule 2-2 onward,
/// drawing first without writing depth, the sea can extend beyond the box faces without being hard-clipped by skybox textures.
/// The only real constraint is the far plane, which App.ResetCamera sets to 1300 to cover the farthest sea-corner rays.
/// The plane fills the whole area rather than cutting a hole for land; land naturally occludes the sea by depth testing because y=0 is above sea level.
///
/// Near shore, the FBM relief fades to zero within ShoreDampRange=60m of the land square (L-infinity distance): on the
/// dynamic path that keeps the 12m water column even so the absorption term reads a flat tint instead of noise shadows,
/// and on the static path it is what keeps the waterline calm against the beach.
/// The vertex wave uses a much narrower band of its own (SwellMaskStart=6m, ramping to full amplitude by 24m): the
/// relief never has to be visible from the shore, the wave does. Sharing the 60m band held the first crest 135m out
/// and left the water around the east-coast rocks perfectly still.
/// The useful SeaLevel range is roughly [-6,-1]. Beach tiles are static instances and do not track SeaLevel, with their
/// top pinned at -0.4 and bottoms around -11 to -17, so a level above about -1 floods the sand up to the grass seam.
/// The default sits at -1.5 for three reasons: it leaves 1.1 m of dry sand, it buries the shore rocks to the knee, and
/// it is exactly the bottom face of the estuary's spray box (River.SprayPos.Y 1 minus SpraySize.Y 5 / 2), so the
/// river's foam band lands on the water instead of hovering over it or sinking under it.
///
/// P4 (dynamic path only): the swell is not baked into geometry. The visible surface is a Gerstner water plane whose displacement,
/// analytic normals, and phase speed all come from <see cref="Water"/>; the 1420m rectangle is cut into 8x8 tiles of
/// 20x20 quads so the vertex grid can carry the 155m..42m wave components from <see cref="WaterMath.BuildWaves"/>.
/// Surface amplitude stays at 2.4m peak-to-trough on purpose: the beach slopes about 1:4, so every extra meter of
/// crest sweeps the waterline roughly four meters further up the sand.
/// </summary>
internal class Sea : Panel
{
    const int GridSize = 255;                       // Vertex count per side: 255x255 = 65025, below the ushort index hard limit.
    const float Size = 1400f;                       // Sea side length in meters after the three-stage expansion to 1400x1400.
    /// <summary>
    /// Default sea height in world Y: 1.5 m below the grass, against the 3 m it used to be. Not a taste setting - it is
    /// the level where the beach's top edge, the shore rocks and the estuary's spray box all agree, see the class header.
    /// Shared rather than repeated so one number moves the water plane, the droplets' floor (River.SeaY) and the
    /// lighthouse's pool together.
    /// </summary>
    internal const float DefaultSeaLevel = -1.5f;
    const float SeaDepth = 12f;                     // Distance from the water surface down to the baked floor mesh, in meters.
    const uint Seed = 0x0CEA2026u;                  // Fixed seed so sea shape stays reproducible across launches.

    // Shoreline parameters
    const float LandHalf = 75f;                     // Half-width of the land square in meters: the 150x150 grass tile becomes +/-75.
    const float ShoreDampRange = 60f;               // Near-shore damping zone where FBM amplitude fades out with smoothstep.

    // Swell shoaling band, kept separate from ShoreDampRange on purpose.
    // The baked relief has to stay flat 60m out so the static path's waterline holds still against the beach, but the
    // travelling wave is the one thing that has to be visible from the shore. Sharing that 60m band pushed the swell's
    // first crest out to 135m from the island centre - a thin strip near the horizon - and left the water around the
    // east-coast rocks (x about 75..90) perfectly still, which reads as "the wave is on the other side of the island".
    // Beach tiles pin their top edge at -0.4 and reach about -13 at the bottom, so the shoreline sits within a few
    // metres of the land square at SeaLevel -3. Starting at 6m keeps every crest off that band, and the short ramp means
    // the swell grows to full height over roughly three grid cells (3 x 5.512m) instead of fading in forever.
    const float SwellMaskStart = 6f;                // Distance from the land square where the swell is still fully damped.
    const float SwellMaskRange = 18f;               // Ramp length; full amplitude at SwellMaskStart + SwellMaskRange (24m).

    // Material constants for the two variants. They are fixed at Load time rather than switched at runtime because
    // Mesh3D uploads its material once during Load and UpdateMesh3D never re-reads Surface afterwards; that is why the
    // two variants exist as two meshes, each with its own Surface, instead of one mesh with two materials.
    // Static, the pre-P3 sea as it was authored: a deep ocean teal with low roughness is what lets sun and moon draw
    // bright glints across the noise normals, and that specular band is the effect worth bringing back.
    static readonly Vector4 StaticSeaColor = new(0.06f, 0.28f, 0.38f, 1f);
    const float StaticSeaRoughness = 0.15f;
    // Dynamic, the floor SeaDepth down: dark silt with very high roughness, so the only thing that reaches the camera
    // through the water is the absorption tint. A glossy teal here would read as a second, wrongly lit water surface.
    static readonly Vector4 FloorColor = new(0.022f, 0.055f, 0.062f, 1f);
    const float FloorRoughness = 0.96f;

    // P4 sea state. The swell lives in the water shader now, so the sea is described by wind instead of geometry.
    // 11 m/s puts the PM peak wavelength at 2*pi*U^2/g = 78m, doubled to about 155m by WaterMath.BuildWaves,
    // with the remaining three components stepping down by 1.55x to 100m, 64m, and 42m.
    const float WaveWindSpeed = 11f;                // m/s; 0 would switch the Gerstner displacement off entirely.
    const float WaveWindDirection = 0.55f;          // Radians in world XZ: 0=+X, pi/2=+Z, so the swell crosses the shore at an angle.
    const float WaveSwellHeight = 2.4f;             // Peak-to-trough in meters, i.e. 1.2m crests over a 12m water column.
    const float WaveChoppiness = 1.2f;              // Slightly sharper crests than a plain sine; still far below self-intersection.
    const float WaveTileSize = 180f;                // 1420/180 rounds up to 8 tiles per axis, so 64 tiles in total.
    const int WaveTileSubdiv = 20;                  // 20x20 quads per tile: 8.9m spacing, 153600 vertices for the whole sea.

    // FBM octave table with wavelengths 160/80/40/20m and amplitudes 2.4/1.2/0.6/0.3m.
    // On the static path this is the relief of the visible surface: open-ocean swell within about +/-4.5m offshore,
    // damped near shore so the waterline stays calm. On the dynamic path it is only the relief of the sea floor,
    // where it varies the water thickness the absorption term reads.
    // Grid spacing is about 5.512m, and the shortest 20m octave still has about 3.6 samples per wavelength.
    static readonly float[] Wavelengths = { 160f, 80f, 40f, 20f };
    static readonly float[] Amplitudes = { 2.4f, 1.2f, 0.6f, 0.3f };

    /// <summary>
    /// Static variant: the visible sea surface at <see cref="SeaLevel"/>. Disabled whenever the dynamic path is in
    /// force, which is what keeps it out of every draw list without unloading its geometry.
    /// </summary>
    internal Mesh3D sea;

    /// <summary>
    /// Dynamic variant's sea floor, <see cref="SeaDepth"/> below the water plane. Same geometry as <see cref="sea"/>,
    /// different material; disabled whenever the static path is in force.
    /// </summary>
    Mesh3D floor;

    /// <summary>
    /// Which presentation was last applied, so <see cref="ApplySeaMode"/> runs only when the setting actually changed.
    /// The constructor seeds it, so the first Update never has to guess.
    /// </summary>
    Season.Rendering.SeaMode _mode;

    /// <summary>
    /// The dynamic path's visible sea surface: a 1400x1400m rectangle at <see cref="SeaLevel"/>. Registered by App
    /// through <see cref="Season.Basic.BaseApp.Waters"/>; the SceneWater pass expands it into 6 vertices and evaluates
    /// Fresnel, refraction, Hi-Z screen-space reflection, and depth absorption in the shader. It is disabled on the
    /// static path, where the mesh itself is the visible surface, so an empty visible table makes that pass a no-op.
    /// </summary>
    internal Season.Rendering.WaterPlane Water { get; }

    /// <summary>
    /// Sea height in world Y, defaulting to -1.5. The water plane sits at this height on the dynamic path, and on both
    /// paths it is the reference the mesh offset is measured from: the floor follows <see cref="SeaDepth"/> below it,
    /// the static surface sits exactly on it.
    /// The change takes effect on the next Update. The practical range is about [-6,-1],
    /// since higher values flood over the beach top at -0.4 and onto the grass seam while beach tiles remain static.
    /// </summary>
    internal float SeaLevel
    {
        get => Water.Position.Y;
        // Position 是 struct：只能整体回写，改不动 Water.Position.Y 本身。
        set => Water.Position = new Vector3(Water.Position.X, value, Water.Position.Z);
    }

    internal Sea()
    {
        // One geometry, two materials. BuildSurfaces bakes the FBM grid once and hands the same vertex and index
        // arrays to both variants, so the only extra cost of keeping both alive is the second GPU copy of VB/IB.
        var (staticSurface, floorSurface) = BuildSurfaces();

        sea = new Mesh3D()
        {
            Name = "Sea",
            // CastShadows=false follows the same "never cast geometry" rule as grass and road.
            // A 1400m sheet would otherwise create a GI proxy that encloses the whole SDF volume
            // and collapses the distance field to a constant negative value.
            CastShadows = false,
            // CullingEnabled=false keeps the sea on the always-visible baseline, exempt from frustum culling.
            CullingEnabled = false,
            PosX = 0f,
            PosY = DefaultSeaLevel,
            PosZ = 0f,
            // Leave Width/Height/Depth unset so OnBoundsEstablished keeps the native size with a scale of 1.
        };

        sea.Surfaces.Add(staticSurface);
        AddControl(sea);

        // The floor is the same sheet under the other material. It only ever shows on the dynamic path, but it is
        // registered here rather than lazily because AddControl is what queues the GPU upload: building it on the
        // first settings change would stall that frame on a 65025-vertex upload.
        floor = new Mesh3D()
        {
            Name = "SeaFloor",
            CastShadows = false,
            CullingEnabled = false,
            PosX = 0f,
            PosY = DefaultSeaLevel - SeaDepth,
            PosZ = 0f,
        };

        floor.Surfaces.Add(floorSurface);
        AddControl(floor);

        // P3: the surface itself carries no mesh. The shader derives normals, refraction, and absorption from the
        // world position, so the data here is just the rectangle plus its material constants.
        Water = new Season.Rendering.WaterPlane
        {
            Position = new Vector3(0f, DefaultSeaLevel, 0f),
            // The mountains and beach hide the rim, and a little overshoot keeps the horizon seam from showing.
            Size = new Vector2(Size + 20f, Size + 20f),
            Tint = new Vector4(0.020f, 0.075f, 0.100f, 1f),
            // Deep water is nearly opaque even at normal incidence; Fresnel pushes it the rest of the way.
            Opacity = 0.72f,
            // 1.6/m turns the 12m floor into a faint silhouette and leaves open water a flat blue-green.
            Absorption = 1.6f,
            // Large swells need a longer wavelength than the pond, or the near field reads as static noise.
            NormalTiling = 0.075f,
            NormalStrength = 1.6f,
            ScrollSpeed = 0.05f,
            RippleStrength = 1.4f,
            // Open ocean has no nearby shoreline to sample, so refraction is mostly a depth-cue here.
            RefractionStrength = 0.012f,
            Roughness = 0.08f,
            // P4: Gerstner swell plus the tile grid that gives it vertices. The four wave components are built
            // on the CPU from the wind settings; the shader only evaluates them, so phase speed stays physical
            // (deep-water dispersion, sqrt(g*k)) and WaveSpeed is a pure time multiplier.
            WindSpeed = WaveWindSpeed,
            WindDirection = WaveWindDirection,
            SwellHeight = WaveSwellHeight,
            Choppiness = WaveChoppiness,
            WaveSpeed = 1f,
            TileSize = WaveTileSize,
            TileSubdiv = WaveTileSubdiv,
        };

        // Seed the gating from the persisted setting so the very first frame already shows exactly one variant;
        // both meshes default to Enable=true, and leaving it to Update would show both for one frame.
        ApplyWaveSettings(Season.Rendering.WorldSettings.Current.SeaMode);
        ApplySeaMode(Season.Rendering.WorldSettings.Current.SeaMode);
    }

    /// <summary>
    /// Puts exactly one presentation on screen. Enable is the right gate rather than Alpha=0: an alpha below 1 still
    /// reports EnableTransparentSort=true and would drag a 1400m sheet through the transparent sort every frame,
    /// while a disabled control is skipped by Draw, DrawShadow, picking, and UpdateMesh3D alike.
    /// </summary>
    void ApplySeaMode(Season.Rendering.SeaMode mode)
    {
        _mode = mode;
        bool dynamicMode = mode == Season.Rendering.SeaMode.Dynamic;
        sea.Enable = !dynamicMode;
        floor.Enable = dynamicMode;
        // The water plane is the visible surface on the dynamic path only. Hiding it is what makes the SceneWater
        // pass a no-op for this panel: WaterEffect.Prepare bails out on an empty visible table.
        Water.Enabled = dynamicMode;
    }

    /// <summary>
    /// Hands the swell scalars to the visible mesh. Kept separate from <see cref="ApplySeaMode"/> because these two can
    /// move without the mode changing, and writing them is two float stores - cheaper than tracking a dirty flag.
    ///
    /// Amplitude is zero on every mode but Swell, and zero amplitude is what makes the vertex shader skip the whole
    /// displacement on a single compare: the static and dynamic paths pay nothing, and so does a sea whose panel
    /// slider sits at its bottom stop.
    /// </summary>
    void ApplyWaveSettings(Season.Rendering.SeaMode mode)
    {
        var settings = Season.Rendering.WorldSettings.Current;
        sea.WaveAmplitude = mode == Season.Rendering.SeaMode.Swell ? settings.SeaWaveHeight : 0f;
        sea.WaveSpeed = settings.SeaWaveSpeed;
        // The floor is only ever seen through 12m of absorbing water, where a vertical displacement of a meter is not
        // measurable, and on the dynamic path the visible motion already comes from the water shader.
        floor.WaveAmplitude = 0f;
    }

    /// <summary>
    /// Builds both Surfaces in local space with x/z in [-Size/2, Size/2], where the local origin is the plane center.
    /// Height comes from FBM noise with shoreline damping. The midpoint of the bounds in Y is then subtracted so the
    /// anchor, which is the geometric center of the box, lands on the average surface height. Both variants want that:
    /// on the static path PosY is the sea level itself, on the dynamic path it is the floor level, SeaDepth below the
    /// water plane.
    ///
    /// Normals are computed from central differences on the height grid and are shared by both variants: on the static
    /// path they are what carries the sun and moon glints, and on the dynamic path they only matter for the little CSM
    /// and GI shading that survives the water tint above the matte silt floor.
    ///
    /// The two returned Surfaces reference the same vertex and index arrays. The backends read those during Load and
    /// never write them back, so sharing is safe and keeps the CPU-side cost of the second variant at one allocation.
    /// </summary>
    static (Surface surface, Surface floor) BuildSurfaces()
    {
        const int cells = GridSize - 1;                 // Cell count per side.
        float step = Size / cells;                      // Grid spacing, about 5.512m.
        float half = Size * 0.5f;

        // 1. Height field: bake FBM value noise per vertex. The result is deterministic and costs O(GridSize^2) once.
        var heights = new float[GridSize * GridSize];
        float minY = float.MaxValue, maxY = float.MinValue;
        for (int j = 0; j < GridSize; j++)
        {
            float z = -half + j * step;
            int row = j * GridSize;
            for (int i = 0; i < GridSize; i++)
            {
                float h = Fbm(-half + i * step, z);
                heights[row + i] = h;
                if (h < minY) minY = h;
                if (h > maxY) maxY = h;
            }
        }
        float midY = (minY + maxY) * 0.5f;              // Midpoint of the bounds in Y, subtracted so the anchor sits on average sea level.

        // 2. Vertices: filled the same way as the road, with any reasonable tangent because renderMode=0 ignores TBN.
        var verts = new Vertex[GridSize * GridSize];
        for (int j = 0; j < GridSize; j++)
        {
            float z = -half + j * step;
            int row = j * GridSize;
            for (int i = 0; i < GridSize; i++)
            {
                // Normals from central differences dh/dx and dh/dz, falling back to one-sided differences on edges.
                int i0 = i > 0 ? i - 1 : i, i1 = i < cells ? i + 1 : i;
                int j0 = j > 0 ? j - 1 : j, j1 = j < cells ? j + 1 : j;
                float dhx = (heights[row + i1] - heights[j * GridSize + i0]) / ((i1 - i0) * step);
                float dhz = (heights[j1 * GridSize + i] - heights[j0 * GridSize + i]) / ((j1 - j0) * step);
                var normal = Vector3.Normalize(new Vector3(-dhx, 1f, -dhz));

                verts[row + i] = new Vertex
                {
                    Position = new Vector3(-half + i * step, heights[row + i] - midY, z),
                    // UV0.x carries the swell mask, not a texture coordinate: the sea has no textures on any path, so
                    // the channel is free, and the swell shader reads it to attenuate the wave before it reaches the
                    // beach. Deliberately not ShoreMask - that one is 60m wide because it also flattens the baked
                    // relief, which would hold the first crest 135m out and leave the east-coast rocks in still water.
                    // Both masks reach zero before the waterline, which is what keeps the shoreline from walking.
                    TexCoord = new Vector2(SwellMask(-half + i * step, z), 0f),
                    Normal = normal,
                    Tangent = new Vector4(1, 0, 0, 1),
                    Joints = Vector4.Zero,
                    Weights = Vector4.Zero,
                };
            }
        }

        // 3. Indices: same winding as Ground.BuildRoad, {2,3,1, 2,1,0}, so with u=+X and v=+Z the front face points upward.
        //    The grid shares vertices, with cell corners a=(i,j), b=(i+1,j), c=(i,j+1), and d=(i+1,j+1).
        var indices = new ushort[cells * cells * 6];
        int ii = 0;
        for (int j = 0; j < cells; j++)
        {
            int row = j * GridSize;
            for (int i = 0; i < cells; i++)
            {
                int a = row + i, b = row + i + 1;
                int c = row + GridSize + i, d = row + GridSize + i + 1;
                indices[ii + 0] = (ushort)c;
                indices[ii + 1] = (ushort)d;
                indices[ii + 2] = (ushort)b;
                indices[ii + 3] = (ushort)c;
                indices[ii + 4] = (ushort)b;
                indices[ii + 5] = (ushort)a;
                ii += 6;
            }
        }

        // Static variant, the pre-P3 sea: solid-color PBR with no textures, deep ocean teal, and low roughness so sun
        // and moon produce bright glints across the noise normals.
        var surface = new Surface
        {
            Vertices = verts,
            Indices = indices,
            BaseColor = StaticSeaColor,
            MetallicFactor = 0f,                          // Water is a dielectric; specular strength comes from low-roughness F0.
            RoughnessFactor = StaticSeaRoughness,
            Unlit = false,                                // Uses PBR lighting and receives CSM shadows and GI.
            Mode = SurfaceBlendMode.Opaque,
        };

        // Dynamic variant, the same sheet serving as the floor: dark silt at very high roughness, so the only thing
        // that reaches the camera through the water is the depth-absorption tint.
        var floor = new Surface
        {
            Vertices = verts,
            Indices = indices,
            BaseColor = FloorColor,
            MetallicFactor = 0f,                          // Silt is a dielectric; the floor must not add specular.
            RoughnessFactor = FloorRoughness,
            Unlit = false,
            Mode = SurfaceBlendMode.Opaque,
        };

        return (surface, floor);
    }

    /// <summary>
    /// FBM using four octaves of value noise, with wavelengths and amplitudes defined in Wavelengths and Amplitudes.
    /// Offshore, the envelope is about [-4.5,4.5]. Near shore, amplitude fades to zero within ShoreDampRange
    /// based on the L-infinity distance to the land rectangle.
    /// </summary>
    static float Fbm(float x, float z)
    {
        float damp = ShoreMask(x, z);

        float sum = 0f;
        for (int o = 0; o < Wavelengths.Length; o++)
            sum += Amplitudes[o] * (ValueNoise(x / Wavelengths[o], z / Wavelengths[o], Seed ^ (uint)(o * 0x9E3779B9u)) * 2f - 1f);
        return sum * damp;
    }

    /// <summary>
    /// Relief mask in [0,1] for the baked shoreline: 0 over the land square and rising to 1 at
    /// <see cref="ShoreDampRange"/> offshore, measured as the L-infinity distance to the land rectangle so the band
    /// follows the square coastline rather than the circle. Only <see cref="Fbm"/> consumes it. The vertex wave uses
    /// the narrower <see cref="SwellMask"/> instead; the two are independent on purpose, see the constants above.
    /// </summary>
    static float ShoreMask(float x, float z)
    {
        // Land and boundary points clamp to zero so the shoreline stays flat under the grass edge.
        float d = MathF.Max(MathF.Max(MathF.Abs(x), MathF.Abs(z)) - LandHalf, 0f);
        return Smoothstep01(d / ShoreDampRange);
    }

    /// <summary>
    /// Swell mask in [0,1], written into vertex UV0.x and read by the vertex wave in all four backends.
    /// Same L-infinity shape as <see cref="ShoreMask"/> but with a much shorter start and ramp, so crests exist from
    /// roughly 24m offshore instead of 135m. It still reaches zero 6m from the land square, which is past the
    /// waterline, so a crest can never climb the beach and the shoreline stays where the static path put it.
    /// </summary>
    static float SwellMask(float x, float z)
    {
        float d = MathF.Max(MathF.Max(MathF.Abs(x), MathF.Abs(z)) - LandHalf, 0f);
        return Smoothstep01((d - SwellMaskStart) / SwellMaskRange);
    }

    /// <summary>Deterministic value noise with smoothstep bilinear interpolation, where (x,z) are continuous coordinates in lattice space.</summary>
    static float ValueNoise(float x, float z, uint seed)
    {
        int x0 = (int)MathF.Floor(x), z0 = (int)MathF.Floor(z);
        float fx = x - x0, fz = z - z0;
        fx = fx * fx * (3f - 2f * fx);
        fz = fz * fz * (3f - 2f * fz);

        float v00 = Hash01(x0, z0, seed);
        float v10 = Hash01(x0 + 1, z0, seed);
        float v01 = Hash01(x0, z0 + 1, seed);
        float v11 = Hash01(x0 + 1, z0 + 1, seed);
        float top = v00 + (v10 - v00) * fx;
        float bottom = v01 + (v11 - v01) * fx;
        return top + (bottom - top) * fz;
    }

    static float Hash01(int x, int z, uint seed)
    {
        uint h = seed;
        h ^= (uint)x * 0x27d4eb2du;
        h ^= (uint)z * 0x165667b1u;
        h ^= h >> 15;
        h *= 0x85ebca6bu;
        h ^= h >> 13;
        return h / 4294967295f;
    }

    /// <summary>smoothstep(0,1,x): clamp first, then evaluate 3t^2-2t^3, returning 0 or 1 outside the range.</summary>
    static float Smoothstep01(float x)
    {
        x = Math.Clamp(x, 0f, 1f);
        return x * x * (3f - 2f * x);
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        // Read once per frame and act only when it actually changed, so the steady state is one comparison.
        // The mode flip touches Enable on three objects, so it stays gated; the two swell scalars are written
        // unconditionally because the panel can move them without touching the mode.
        var seaMode = Season.Rendering.WorldSettings.Current.SeaMode;
        if (seaMode != _mode) ApplySeaMode(seaMode);
        ApplyWaveSettings(seaMode);

        // The water rectangle is world-space data with no mesh and no child control, so it follows the panel directly.
        // On the dynamic path the floor sits SeaDepth below it; that gap is what the shader reads as water thickness.
        // On the static path the rectangle is hidden and only the mesh position matters.
        float level = Water.Position.Y;
        Water.Position = new Vector3(PosX, level, PosZ);

        sea.PosX = PosX;
        sea.PosY = level;
        sea.PosZ = PosZ;

        floor.PosX = PosX;
        floor.PosY = level - SeaDepth;
        floor.PosZ = PosZ;

        // Only the visible variant is handed to the renderer. The hidden one gets a fresh world matrix on the first
        // frame after it is re-enabled, because UpdateMesh3D rewrites it unconditionally.
        if (seaMode == Season.Rendering.SeaMode.Dynamic ? floor.Update(time) : sea.Update(time))
        {
            result = true;
        }

        return result;
    }
}
