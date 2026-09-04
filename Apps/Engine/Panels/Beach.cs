// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Engine.Panels;

/// <summary>
/// Beach transition panel between land and sea. It procedurally builds a small 3D beach tile mesh
/// with a top-to-bottom slope and dune noise, then scatters those tiles through GPU instancing along
/// the coastline. This replaced the old monolithic procedural beach inside Sea.cs because the large
/// mesh was harder to compose and much heavier than reusable tiles.
///
/// Each tile is about 20x36m with about 1.9K vertices and 1.4K triangles. Local x runs along the coast,
/// local z runs downslope toward the sea, and height blends from the buried top edge to the submerged
/// bottom edge with extra dune displacement. Noise fades to zero at the top edge and both side edges so
/// neighboring tiles join cleanly. All instances share one procedural sand texture uploaded directly from memory.
///
/// Placement is limited to the east and west shoreline openings left by Mountains. Tiles are skipped inside
/// the central corridor so the sun and moon still rise and set over open water. Yaw aligns each tile's local +z
/// with the local outward normal, width and depth scale almost proportionally to preserve the slope profile,
/// and PosY is solved from a fixed top-edge target height under the unified anchor convention.
/// CastShadows stays false so these thin shoreline shells do not enter CSM or GI proxy generation, and
/// CullingEnabled stays false so the coastline remains part of the always-visible baseline.
/// </summary>
internal class Beach : Panel
{
    // -- Tile template geometry in local space: x along the coast, z down the slope toward the sea, y as height. --
    const float TileW = 20f;          // Tile width in meters along the coastline.
    const float TileD = 36f;          // Tile depth in meters, half buried under grass and half extending into water.
    const int Nu = 21;                // Vertex count along the coastline, using a 1.0m grid step.
    const int Nv = 25;                // Vertex count along the slope, using a 1.5m grid step.
    const float TopY = 10f;           // Top-edge height, buried slightly under the grass to hide seams and avoid z-fighting.
    const float BottomY = -2f;        // Bottom-edge height, deep enough to stay below the near-shore low-water line.
    const float RampVRange = 6f;      // Slopewise fade-in distance for dune noise.
    const float RampSRange = 2.5f;    // Coastwise fade-in distance so side seams stay smooth.
    const uint NoiseSeed = 0x0BEA2026u;   // Fixed seed so tile shape stays reproducible across launches.

    // Dune-noise octave table with wavelengths of 12/6/4m and a total amplitude of about 0.56m.
    // With a 1.5m slopewise step, the Nyquist limit is 3m, so the finest 4m octave still has about 2.7 samples per wavelength.
    static readonly float[] DuneWavelengths = { 12f, 6f, 4f };
    static readonly float[] DuneAmplitudes = { 0.30f, 0.18f, 0.08f };

    // -- Coast-placement parameters for the east and west openings only. --
    const int Count = 26;              // Total tile count before corridor filtering.
    const float CoastHalf = 75f;       // Half-width of the grass tile, so east and west shorelines sit at x=+/-75 with z in [-75,75].
    const float BaseWidth = 28f;       // Base tile width before randomization, producing about 24-35m overlaps.
    const float DepthAspect = 1.8f;    // Depth-to-width aspect ratio, matching TileD/TileW.
    const float HeightAspect = 0.46f;  // Height-to-width ratio that keeps the slope shape approximately similar under scaling.
    const float StraddleJitter = 5f;   // Cross-shore random offset in meters along the outward normal.
    const float TopTargetY = -0.4f;    // Fixed top-edge height; PosY is solved back from Height / 2.
    const int Seed = 0x0BEA2026;       // Fixed seed so layout stays reproducible.

    // Mountain-foot boundary shared with Mountains.CorridorClearance=55.
    // Tiles are only placed where |PosZ| >= 55 so they do not intersect the mountain ring,
    // while the center corridor remains open water for sunrise and sunset sightlines.
    const float CorridorClearance = 55f;

    /// <summary>Beach tile template control with one Surface using a procedural sand texture and about 19 instances across the east and west shoreline openings.</summary>
    internal InstancedMesh3D beachField;

    internal Beach()
    {
        beachField = new InstancedMesh3D()
        {
            Name = "Beach",
            // Keep CastShadows false for this thin shoreline shell so it does not create a GI proxy
            // that encloses the SDF volume and collapses the distance field to a constant negative value.
            CastShadows = false,
            // Keep the coastline on the always-visible baseline, exempt from frustum culling.
            CullingEnabled = false,
        };
        beachField.Surfaces.Add(BuildTileSurface());
        AddControl(beachField);

        var rng = new Random(Seed);
        float perimeter = CoastHalf * 4f;   // East and west edges only: 2 x 150 = 300m.

        for (int i = 0; i < Count; i++)
        {
            // Spread instances by arc length with random jitter so they do not look evenly queued.
            // Width, height, yaw, and cross-shore offsets are randomized independently.
            // Draw all random values before corridor filtering so later random sequences stay identical.
            float arc = (i + rng.NextSingle()) / Count * perimeter;
            float width = BaseWidth * (0.85f + rng.NextSingle() * 0.40f);
            float heightJitter = 0.90f + rng.NextSingle() * 0.15f;
            float yawJitter = (rng.NextSingle() * 2f - 1f) * 0.08f;
            float off = (rng.NextSingle() * 2f - 1f) * StraddleJitter;
            float posYJitter = (rng.NextSingle() * 2f - 1f) * 0.15f;

            var (edgeX, edgeZ, outX, outZ, baseYaw) = GapCoastPoint(arc);
            float posX = edgeX + outX * off;
            float posZ = edgeZ + outZ * off;

            // Skip the mountain-foot corridor so the center section remains open water.
            if (MathF.Abs(posZ) < CorridorClearance)
                continue;

            // Pin the top edge by solving PosY = TopTargetY - Height/2 under the geometric-center anchor convention.
            // This keeps the top edge buried under grass and the bottom edge underwater at any scale.
            float height = width * HeightAspect * heightJitter;
            beachField.Instances.Add(new MeshInstanceTransform
            {
                PosX = posX,
                PosY = TopTargetY - height * 0.5f + posYJitter,
                PosZ = posZ,
                Width = width,
                Height = height,
                Depth = width * DepthAspect,
                Rotation = Quaternion.CreateFromYawPitchRoll(baseYaw + yawJitter, 0f, 0f),
            });
        }
    }

    /// <summary>
    /// Maps gap-coast arc length to an east or west shoreline point, the outward unit normal,
    /// and a baseline yaw. Arc length starts at the south end of the east edge, then runs
    /// east edge south-to-north and west edge north-to-south for a total of 300m.
    /// BaseYaw aligns the tile's local +z downslope direction with the local outward normal.
    /// </summary>
    static (float X, float Z, float OutX, float OutZ, float BaseYaw) GapCoastPoint(float arc)
    {
        float side = CoastHalf * 2f;   // 150
        float s = arc % (side * 2f);

        if (s < side)            // East edge (+X): south to north, outward +X, yaw = pi/2.
            return (CoastHalf, -CoastHalf + s, 1f, 0f, MathF.PI * 0.5f);
        return (-CoastHalf, CoastHalf - (s - side), -1f, 0f, -MathF.PI * 0.5f);   // West edge (-X): north to south.
    }

    /// <summary>
    /// Builds the tile Surface from a shared Nu x Nv grid of positions, noise displacement, and normals,
    /// then duplicates four vertices per cell with tiled [0,1] UVs. Winding matches Room.AddRoofFace and
    /// Ground.BuildRoad, {c,d,b, c,b,a}, so with u=+x along the coast and v=+z downslope the front face points upward.
    /// The final 20 x 24 x 4 = 1920 vertices remain well below the ushort index limit.
    /// </summary>
    static Surface BuildTileSurface()
    {
        int cu = Nu - 1, cv = Nv - 1;

        // 1. Shared grid positions: a linear base slope from top edge to bottom edge plus dune-noise displacement in +Y.
        //    Noise amplitude ramps in from both the top edge and the side edges so every tile border stays flat
        //    and seams remain hidden when instances are combined.
        var positions = new Vector3[Nu * Nv];
        for (int j = 0; j < Nv; j++)
        {
            float sv = j / (float)cv;
            float z = -TileD * 0.5f + TileD * sv;
            float dTop = TileD * sv;                              // Downslope distance from the top edge.
            float rampV = Smoothstep01(dTop / RampVRange);
            int row = j * Nu;
            for (int i = 0; i < Nu; i++)
            {
                float x = -TileW * 0.5f + TileW * (i / (float)cu);
                float dSide = MathF.Min(x + TileW * 0.5f, TileW * 0.5f - x);
                float ramp = rampV * Smoothstep01(dSide / RampSRange);
                float y = TopY + (BottomY - TopY) * sv;
                if (ramp > 0f)
                    y += ramp * DuneNoise(x, z);
                positions[row + i] = new Vector3(x, y, z);
            }
        }

        // 2. Shared grid normals from normalize(cross(dv, du)).
        //    With u=x along the coast and v=z downslope, +Y points upward.
        //    Edge samples fall back to one-sided differences.
        var normals = new Vector3[Nu * Nv];
        for (int j = 0; j < Nv; j++)
        {
            int row = j * Nu;
            int j0 = j > 0 ? j - 1 : j, j1 = j < cv ? j + 1 : j;
            for (int i = 0; i < Nu; i++)
            {
                int i0 = i > 0 ? i - 1 : i, i1 = i < cu ? i + 1 : i;
                var du = positions[row + i1] - positions[row + i0];                 // dP/dsu along the coast.
                var dv = positions[j1 * Nu + i] - positions[j0 * Nu + i];           // dP/dsv down the slope.
                normals[row + i] = Vector3.Normalize(Vector3.Cross(dv, du));
            }
        }

        // 3. Duplicate four vertices per cell using shared positions and normals, with per-cell tiled UVs and matching winding.
        var verts = new Vertex[cu * cv * 4];
        var indices = new ushort[cu * cv * 6];
        int vi = 0, ii = 0;
        for (int j = 0; j < cv; j++)
        {
            int row = j * Nu;
            for (int i = 0; i < cu; i++)
            {
                int a = row + i, b = row + i + 1;
                int c = row + Nu + i, d = row + Nu + i + 1;
                verts[vi + 0] = MakeVertex(positions[a], new Vector2(0, 0), normals[a]);
                verts[vi + 1] = MakeVertex(positions[b], new Vector2(1, 0), normals[b]);
                verts[vi + 2] = MakeVertex(positions[c], new Vector2(0, 1), normals[c]);
                verts[vi + 3] = MakeVertex(positions[d], new Vector2(1, 1), normals[d]);
                indices[ii + 0] = (ushort)(vi + 2);
                indices[ii + 1] = (ushort)(vi + 3);
                indices[ii + 2] = (ushort)(vi + 1);
                indices[ii + 3] = (ushort)(vi + 2);
                indices[ii + 4] = (ushort)(vi + 1);
                indices[ii + 5] = (ushort)(vi + 0);
                vi += 4; ii += 6;
            }
        }

        return new Surface
        {
            Vertices = verts,
            Indices = indices,
            BaseColor = Vector4.One,          // The texture carries the full sand color without extra tint.
            MetallicFactor = 0f,
            RoughnessFactor = 0.95f,          // Matte sand surface.
            Unlit = false,                    // Uses PBR lighting and receives CSM shadows and GI.
            Mode = SurfaceBlendMode.Opaque,
            // Upload the procedural sand texture directly from in-memory pixels during loading.
            // All instances share this single Surface and therefore the same texture.
            TextureOverride = TextureUpdateSource.FromImage(new NativeImageData(512, 512, GenerateSandTexturePixels(512))),
        };
    }

    static Vertex MakeVertex(Vector3 pos, Vector2 uv, Vector3 normal)
    {
        // Any reasonable tangent works here because renderMode=0 does not read the TBN basis.
        return new Vertex
        {
            Position = pos,
            TexCoord = uv,
            Normal = normal,
            Tangent = new Vector4(1, 0, 0, 1),
            Joints = Vector4.Zero,
            Weights = Vector4.Zero,
        };
    }

    /// <summary>
    /// Generates the procedural sand texture as RGBA8 using deterministic integer-hash value noise.
    /// A warm sand base color is modulated by three octaves of brightness variation to create broad patches,
    /// then per-pixel grain adds occasional darker and brighter specks. Since UVs tile per cell, the texture
    /// repeats across tiles, so the noise is kept directionless to make repetition look natural.
    /// </summary>
    static byte[] GenerateSandTexturePixels(int size)
    {
        var pixels = new byte[size * size * 4];
        const uint seed = 0x5A2D2026u;
        for (int py = 0; py < size; py++)
        {
            for (int px = 0; px < size; px++)
            {
                float n = 0.5f * ValueNoise(px, py, 32, seed)
                        + 0.3f * ValueNoise(px, py, 16, seed ^ 0x9E3779B9u)
                        + 0.2f * ValueNoise(px, py, 8, seed ^ 0x85EBCA77u);

                var c = new Vector3(0.82f, 0.74f, 0.56f) * (0.78f + 0.44f * n);

                // Fine sand grain from per-pixel hashing, with sparse darker and brighter specks.
                float grain = Hash01(px, py, seed ^ 0x51AB3A17u);
                if (grain < 0.12f) c *= 0.85f;
                else if (grain > 0.92f) c *= 1.10f;

                int o = (py * size + px) * 4;
                pixels[o] = (byte)MathF.Round(Math.Clamp(c.X, 0f, 1f) * 255f);
                pixels[o + 1] = (byte)MathF.Round(Math.Clamp(c.Y, 0f, 1f) * 255f);
                pixels[o + 2] = (byte)MathF.Round(Math.Clamp(c.Z, 0f, 1f) * 255f);
                pixels[o + 3] = 255;
            }
        }
        return pixels;
    }

    /// <summary>Dune noise using three octaves of value noise with wavelengths and amplitudes from DuneWavelengths and DuneAmplitudes, returning about [-0.56, 0.56].</summary>
    static float DuneNoise(float x, float z)
    {
        float sum = 0f;
        for (int o = 0; o < DuneWavelengths.Length; o++)
            sum += DuneAmplitudes[o] * (ValueNoise(x / DuneWavelengths[o], z / DuneWavelengths[o], NoiseSeed ^ (uint)(o * 0x9E3779B9u)) * 2f - 1f);
        return sum;
    }

    /// <summary>Deterministic value noise with smoothstep bilinear interpolation, where (x,z) are continuous lattice-space coordinates sampled in world meters.</summary>
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

    /// <summary>Deterministic integer-hash value noise in texture-pixel space, O(1) per sample, matching the Ground helper style.</summary>
    static float ValueNoise(int px, int py, int lattice, uint seed)
    {
        int lx = px / lattice, ly = py / lattice;
        float fx = px % lattice / (float)lattice;
        float fy = py % lattice / (float)lattice;
        fx = fx * fx * (3f - 2f * fx);
        fy = fy * fy * (3f - 2f * fy);

        float v00 = Hash01(lx, ly, seed);
        float v10 = Hash01(lx + 1, ly, seed);
        float v01 = Hash01(lx, ly + 1, seed);
        float v11 = Hash01(lx + 1, ly + 1, seed);
        float top = v00 + (v10 - v00) * fx;
        float bottom = v01 + (v11 - v01) * fx;
        return top + (bottom - top) * fy;
    }

    static float Hash01(int x, int y, uint seed)
    {
        uint h = seed;
        h ^= (uint)x * 0x27d4eb2du;
        h ^= (uint)y * 0x165667b1u;
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

        // Beach tiles use static geometry, so each frame only synchronizes instance transforms and materials.
        beachField.Update(time);

        return result;
    }
}
