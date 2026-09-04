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
/// The material is solid-color PBR with no textures at all. Sea height can be adjusted at runtime through <see cref="SeaLevel"/>,
/// with a default of y=-3, three units below grass at y=0. With Mountains using Sink=10, their lower edges remain submerged,
/// making the ring read as island chains in the sea.
///
/// Geometry budget: Surface.Indices uses ushort, so the hard vertex limit is 65535, matching the rule in Ground.BuildRoad.
/// The sea uses 255x255 = 65025 vertices, right up against that limit, with grid spacing 1400/254 about 5.512m,
/// and 254x254x6 = 387096 ushort indices. The mesh is static geometry uploaded once during loading,
/// because the engine currently has no cross-backend dynamic vertex path and UpdateMesh3D only synchronizes transforms and materials.
/// Waves are therefore static undulation from baked noise rather than per-frame animation.
///
/// Area contract: land is the 150x150 grass tile, 22500m², while the sea was expanded three times in 2026-08 to 1960000m²,
/// ending up as a 1400m square centered at the world origin and extending to +/-700m.
/// That reaches the visual horizon and works with the east-west openings in Mountains so the sun and moon can rise from the sea
/// and set at the line where water meets sky. Since the skybox uses a NoDepth PSO from rule 2-2 onward,
/// drawing first without writing depth, the sea can extend beyond the box faces without being hard-clipped by skybox textures.
/// The only real constraint is the far plane, which App.ResetCamera sets to 1300 to cover the farthest sea-corner rays.
/// The plane fills the whole area rather than cutting a hole for land; land naturally occludes the sea by depth testing because y=0 is above sea level.
///
/// Near shore, wave damping reduces FBM amplitude to zero within ShoreDampRange=60m using smoothstep over the distance from shore,
/// defined as the L-infinity distance to the land rectangle. Shore water therefore stays calm and wave crests never rise above
/// the beach top or grass seam, even if sea level is raised. Offshore, the sea keeps a swell envelope of about +/-4.5m.
/// The useful SeaLevel range is roughly [-7,-1]. Beach tiles are static instances and do not track SeaLevel,
/// with their top pinned near y=-0.4 and bottoms around y=-11 to -17, so raising the sea too much will flood over the beach top onto the grass seam.
/// </summary>
internal class Sea : Panel
{
    const int GridSize = 255;                       // Vertex count per side: 255x255 = 65025, below the ushort index hard limit.
    const float Size = 1400f;                       // Sea side length in meters after the three-stage expansion to 1400x1400.
    const float DefaultSeaLevel = -3f;              // Default sea height in world Y, three units below the ground.
    const uint Seed = 0x0CEA2026u;                  // Fixed seed so sea shape stays reproducible across launches.

    // Shoreline parameters
    const float LandHalf = 75f;                     // Half-width of the land square in meters: the 150x150 grass tile becomes +/-75.
    const float ShoreDampRange = 60f;               // Near-shore damping zone where FBM amplitude fades out with smoothstep.

    // FBM octave table with wavelengths 160/80/40/20m and amplitudes 2.4/1.2/0.6/0.3m.
    // This yields open-ocean swell within about +/-4.5m offshore, while near shore the amplitude is damped by ShoreDampRange.
    // Given grid spacing of about 5.512m, the shortest resolvable wavelength by Nyquist is about 11m,
    // so the finest 20m octave still has about 3.6 samples per wavelength.
    static readonly float[] Wavelengths = { 160f, 80f, 40f, 20f };
    static readonly float[] Amplitudes = { 2.4f, 1.2f, 0.6f, 0.3f };

    internal Mesh3D sea;

    /// <summary>
    /// Sea height in world Y, defaulting to -3. This writes directly to Mesh3D.PosY.
    /// Under the unified placement convention, the anchor is the geometric center of the bounds,
    /// and vertices are centered by the midpoint of the bounds in Y, so PosY equals the average sea height.
    /// The change takes effect on the next Update. The practical range is about [-7,-1],
    /// since higher values flood over the beach top and onto the grass seam while beach tiles remain static.
    /// </summary>
    internal float SeaLevel
    {
        get => sea.PosY;
        set => sea.PosY = value;
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
            PosY = DefaultSeaLevel,
            PosZ = 0f,
            // Leave Width/Height/Depth unset so OnBoundsEstablished keeps the native size with a scale of 1.
        };

        sea.Surfaces.Add(BuildSurface());
        AddControl(sea);
    }

    /// <summary>
    /// Builds the sea Surface in local space with x/z in [-Size/2, Size/2], where the local origin is the plane center.
    /// Height comes from FBM noise with shoreline damping. The midpoint of the bounds in Y is then subtracted so the anchor,
    /// which is the geometric center of the box, lands on the average sea level. That makes PosY equal the sea height.
    /// Normals are computed from central differences on the height grid, and all PBR specular highlights come from those normal variations.
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
            // Solid-color PBR with no textures: a deep ocean teal base.
            // Low roughness lets sun and moon produce bright glints across the noisy normals.
            BaseColor = new Vector4(0.06f, 0.28f, 0.38f, 1f),
            MetallicFactor = 0f,                          // Water is a dielectric; specular strength comes from low-roughness F0.
            RoughnessFactor = 0.15f,
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

        // Sea geometry is static. Update only synchronizes the world transform and material,
        // and SeaLevel changes take effect here through PosY.
        if (sea.Update(time))
        {
            result = true;
        }

        return result;
    }
}
