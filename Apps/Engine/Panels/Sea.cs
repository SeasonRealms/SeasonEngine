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
/// with a default of y=-3, three units below grass at y=0. With Mountains using Sink=10, their lower edges remain submerged,
/// making the ring read as island chains in the sea.
///
/// P3: the visible surface is no longer this mesh. It is a <see cref="Season.Rendering.WaterPlane"/> drawn by the
/// SceneWater pass, which is the only stage that can both sample this frame's scene color (through the refraction
/// snapshot) and depth, and write the scene color. This mesh is kept as the <b>sea floor</b>: Update keeps it
/// <see cref="SeaDepth"/> below the water plane, so the shader's depth-absorption term has real geometry to read a
/// thickness from, and a backend that cannot create a water pipeline still shows a dark seabed instead of a hole.
/// Fresnel, refraction, sky reflection, and the Hi-Z screen-space reflection all live in the water shader.
///
/// Geometry budget: Surface.Indices uses ushort, so the hard vertex limit is 65535, matching the rule in Ground.BuildRoad.
/// The sea uses 255x255 = 65025 vertices, right up against that limit, with grid spacing 1400/254 about 5.512m,
/// and 254x254x6 = 387096 ushort indices. The mesh is static geometry uploaded once during loading,
/// because the engine currently has no cross-backend dynamic vertex path and UpdateMesh3D only synchronizes transforms and materials.
/// That is fine now that it is only the floor: the waves that move are evaluated per frame in the water shader (P4).
///
/// Area contract: land is the 150x150 grass tile, 22500m², while the sea was expanded three times in 2026-08 to 1960000m²,
/// ending up as a 1400m square centered at the world origin and extending to +/-700m.
/// That reaches the visual horizon and works with the east-west openings in Mountains so the sun and moon can rise from the sea
/// and set at the line where water meets sky. Since the skybox uses a NoDepth PSO from rule 2-2 onward,
/// drawing first without writing depth, the sea can extend beyond the box faces without being hard-clipped by skybox textures.
/// The only real constraint is the far plane, which App.ResetCamera sets to 1300 to cover the farthest sea-corner rays.
/// The plane fills the whole area rather than cutting a hole for land; land naturally occludes the sea by depth testing because y=0 is above sea level.
///
/// Near shore, the floor relief fades to zero within ShoreDampRange=60m of the land square (L-infinity distance), so the
/// 12m water column stays even and the absorption term reads as a flat tint instead of noise shadows.
/// The useful SeaLevel range is roughly [-7,-1]. Beach tiles are static instances and do not track SeaLevel,
/// with their top pinned near y=-0.4 and bottoms around y=-11 to -17, so raising the sea too much will flood over the beach top onto the grass seam.
///
/// P4: the swell is no longer baked into geometry. The visible surface is a Gerstner water plane whose displacement,
/// analytic normals, and phase speed all come from <see cref="Water"/>; the 1420m rectangle is cut into 8x8 tiles of
/// 20x20 quads so the vertex grid can carry the 155m..42m wave components from <see cref="WaterMath.BuildWaves"/>.
/// Surface amplitude stays at 2.4m peak-to-trough on purpose: the beach slopes about 1:4, so every extra meter of
/// crest sweeps the waterline roughly four meters further up the sand.
/// </summary>
internal class Sea : Panel
{
    const int GridSize = 255;                       // Vertex count per side: 255x255 = 65025, below the ushort index hard limit.
    const float Size = 1400f;                       // Sea side length in meters after the three-stage expansion to 1400x1400.
    const float DefaultSeaLevel = -3f;              // Default sea height in world Y, three units below the ground.
    const float SeaDepth = 12f;                     // Distance from the water surface down to the baked floor mesh, in meters.
    const uint Seed = 0x0CEA2026u;                  // Fixed seed so sea shape stays reproducible across launches.

    // Shoreline parameters
    const float LandHalf = 75f;                     // Half-width of the land square in meters: the 150x150 grass tile becomes +/-75.
    const float ShoreDampRange = 60f;               // Near-shore damping zone where FBM amplitude fades out with smoothstep.

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
    // Since P3 this is the relief of the sea floor, not of the visible surface: offshore it spans about +/-4.5m,
    // which varies the water thickness the absorption term reads, and near shore it fades out by ShoreDampRange.
    // Grid spacing is about 5.512m, and the shortest 20m octave still has about 3.6 samples per wavelength.
    static readonly float[] Wavelengths = { 160f, 80f, 40f, 20f };
    static readonly float[] Amplitudes = { 2.4f, 1.2f, 0.6f, 0.3f };

    internal Mesh3D sea;

    /// <summary>
    /// The visible sea surface: a 1400x1400m rectangle at <see cref="SeaLevel"/>. Registered by App through
    /// <see cref="Season.Basic.BaseApp.Waters"/>; the SceneWater pass expands it into 6 vertices and evaluates
    /// Fresnel, refraction, Hi-Z screen-space reflection, and depth absorption in the shader.
    /// </summary>
    internal Season.Rendering.WaterPlane Water { get; }

    /// <summary>
    /// Sea height in world Y, defaulting to -3. This is the water plane's height; the baked floor mesh follows
    /// <see cref="SeaDepth"/> below it, so moving the level moves the whole water body.
    /// The change takes effect on the next Update. The practical range is about [-7,-1],
    /// since higher values flood over the beach top and onto the grass seam while beach tiles remain static.
    /// </summary>
    internal float SeaLevel
    {
        get => Water.Position.Y;
        // Position 是 struct：只能整体回写，改不动 Water.Position.Y 本身。
        set => Water.Position = new Vector3(Water.Position.X, value, Water.Position.Z);
    }

    internal Sea()
    {
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
            PosY = DefaultSeaLevel - SeaDepth,
            PosZ = 0f,
            // Leave Width/Height/Depth unset so OnBoundsEstablished keeps the native size with a scale of 1.
        };

        sea.Surfaces.Add(BuildSurface());
        AddControl(sea);

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
    }

    /// <summary>
    /// Builds the sea floor Surface in local space with x/z in [-Size/2, Size/2], where the local origin is the plane center.
    /// Height comes from FBM noise with shoreline damping. The midpoint of the bounds in Y is then subtracted so the anchor,
    /// which is the geometric center of the box, lands on the average floor height. That makes PosY the floor height.
    /// Normals are computed from central differences on the height grid; the floor is matte silt, so they only matter
    /// for the little CSM and GI shading that survives the water tint above it.
    /// </summary>
    static Surface BuildSurface()
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
                    TexCoord = new Vector2(i / (float)cells, j / (float)cells),
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

        return new Surface
        {
            Vertices = verts,
            Indices = indices,
            // P3: this mesh is the sea floor now, not the visible surface. It sits 12m down and is painted as dark silt
            // with very high roughness, so the only thing that reaches the camera through the water is the
            // depth-absorption tint. A glossy teal here would read as a second, wrongly lit water surface.
            BaseColor = new Vector4(0.022f, 0.055f, 0.062f, 1f),
            MetallicFactor = 0f,                          // Silt is a dielectric; the floor must not add specular.
            RoughnessFactor = 0.96f,
            Unlit = false,                                // Uses PBR lighting and receives CSM shadows and GI.
            Mode = SurfaceBlendMode.Opaque,
        };
    }

    /// <summary>
    /// FBM using four octaves of value noise, with wavelengths and amplitudes defined in Wavelengths and Amplitudes.
    /// Offshore, the envelope is about [-4.5,4.5]. Near shore, amplitude fades to zero within ShoreDampRange
    /// based on the L-infinity distance to the land rectangle.
    /// </summary>
    static float Fbm(float x, float z)
    {
        // Distance from shore, measured as the L-infinity distance to the land rectangle.
        // Land and boundary points clamp to zero so the shoreline stays flat under the grass edge.
        float d = MathF.Max(MathF.Max(MathF.Abs(x), MathF.Abs(z)) - LandHalf, 0f);
        float damp = Smoothstep01(d / ShoreDampRange);

        float sum = 0f;
        for (int o = 0; o < Wavelengths.Length; o++)
            sum += Amplitudes[o] * (ValueNoise(x / Wavelengths[o], z / Wavelengths[o], Seed ^ (uint)(o * 0x9E3779B9u)) * 2f - 1f);
        return sum * damp;
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

        // The water rectangle is world-space data with no mesh and no child control, so it follows the panel directly.
        // The baked floor mesh sits SeaDepth below it; that gap is what the shader reads as water thickness.
        float level = Water.Position.Y;
        Water.Position = new Vector3(PosX, level, PosZ);
        sea.PosX = PosX;
        sea.PosY = level - SeaDepth;
        sea.PosZ = PosZ;

        // The floor mesh is static geometry: Update only synchronizes its transform and material.
        if (sea.Update(time))
        {
            result = true;
        }

        return result;
    }
}
