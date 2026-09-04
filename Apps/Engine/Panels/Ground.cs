// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Engine.Panels;

internal class Ground : Panel
{
    // Grass ground: the PBR asset warcraft_3_style_pbr_grass.glb, introduced in 2026-08 to replace
    // the procedural noise grass for better surface realism. The tile center is pinned at the world origin
    // and the ground height remains y=0. See the constructor comments.
    internal Season.Controls.Model grass;

    // Procedural road: starts at the south-wall doorway of the room, follows the X=0 axis from the ground origin,
    // and extends south to the southern edge of the grass. See BuildRoad.
    internal Mesh3D road;

    internal Ground()
    {
        // Grass ground: a PBR model introduced in 2026-08 to replace the old 240x350m procedural noise plane
        // for better surface realism.
        // After converting the glTF root from Z-up to Y-up, the asset is a single 60x60m plane tile
        // centered at the local origin, with x/z in [-30,30] and the surface at about local y=0.
        // Size and placement are set directly by the initializer.
        // A 2026-08 fix removed a one-shot overwrite block in Update that had reset size back to native LocalSize
        // and position back to AnchorWorldOffset, which made initializer values ineffective.
        // OnBoundsEstablished now fills only dimensions that remain zero, so explicit Width and Depth are preserved.
        // ComputedScale is target over local size.
        // Height is intentionally left unset because this asset is effectively a zero-thickness plane.
        // Its Y thickness is just export noise, about 5e-5, and explicitly writing Height would amplify that
        // into huge Y scaling and tear the plane into visible vertical fragments.
        // Leaving Height at zero lets OnBoundsEstablished settle it to native thickness, and the axis-scale
        // degeneracy guard keeps Y scale at 1.
        // Under the unified placement convention, (PosX, PosY, PosZ) is the world position of the bounding-box anchor.
        // For a zero-thickness plane the anchor lies on the surface itself, so PosY=0 keeps ground height at zero
        // and matches the road top at y=0.05.
        // Unlit stays false so the model uses PBR lighting through its own baseColor, metallicRoughness, and normal textures.
        // CastShadows=false follows the same "never cast geometry" rule as the skybox:
        // shadow casting also gates GI-proxy inclusion, and a 60m ground sheet would create a proxy that encloses
        // the whole SDF volume and collapses the distance field to a constant negative value.
        // The grass only needs to receive CSM shadows from the room.
        // CullingEnabled=false keeps the ground always visible and avoids frustum-edge popping.
        grass = new Season.Controls.Model()
        {
            Name = @"Assets/warcraft_3_style_pbr_grass.glb",
            Highlight = new Highlight() { Style = HighlightStyle.None },
            CastShadows = false,
            CullingEnabled = false,
            PosX = 0,
            PosY = 0,
            PosZ = 0,
            Width = 150,
            Depth = 150
        };
        AddControl(grass);

        // The procedural road is added right after the grass so it draws later and wins by stable depth testing
        // while remaining embedded in the grass surface.
        road = BuildRoad();
        road.PosX = 0;
        road.PosY = 0.05f;
        road.PosZ = 0;
        road.Width = 2;
        road.Height = 0;
        road.Depth = 150;
        AddControl(road);

    }

    /// <summary>
    /// Procedural road: a 1.6m wide and 276m long subdivided plane with 111 segments of 2.5m
    /// and a final 1m closing segment. It starts aligned with the south-wall doorway of the room,
    /// where x is in [-0.8,0.8], runs along X=0 through the ground origin, and extends south until
    /// it reaches the grass's southern edge at z=-120.
    /// The old procedural grass kept that south edge unchanged. The grass GLB tile only covers +/-30m around the origin,
    /// so the southern road segment beyond z=-30 lies outside the tile and the skybox closes the far view.
    /// Height aligns with the grass: the road top is y=0.05, which is 5cm above the grass at y=0,
    /// preventing z-fighting while remaining visually embedded with no visible step.
    /// Under the unified placement convention, vertices are generated in local space with the anchor at the bounding-box center,
    /// located at local (0.8, 0, 138). Coordinates span x in [0,1.6], y=0, and z in [0,276], which maps to world z from -120 to 156.
    /// World placement is set once in the constructor with Pos=(0, 0.05, 18), while Width, Height, and Depth
    /// stay at local size so scale remains 1.
    /// With 111x4 = 444 vertices, the geometry stays far below the ushort limit of 65535 for Surface.Indices.
    /// The surface texture is procedurally generated asphalt noise, using a dark gray base with white edge lines
    /// and a dashed center line, tiled per segment in UV [0,1].
    /// CastShadows=false uses the same rationale as the grass: a 276m sheet would otherwise create a GI proxy
    /// that covers the whole SDF volume and collapses the distance field to a constant negative value.
    /// The road only needs to receive CSM shadows from the room.
    /// </summary>
    static Mesh3D BuildRoad()
    {
        const float width = 1.6f;       // Road width equals the doorway width on the room's south wall.
        const float length = 276f;      // Road length from the outer south wall of the room to the grass's south edge.
        const float cellSize = 2.5f;    // Each segment spans 2.5m and tiles UVs in [0,1].
        int cells = (int)MathF.Ceiling(length / cellSize); // 276m becomes 111 segments, with the last one closing at the south edge.

        // 1. Generate the procedural noise texture with deterministic value noise.
        var pixels = GenerateRoadTexturePixels(512);

        var mesh = new Mesh3D()
        {
            Name = "Road",
            CastShadows = false,
            CullingEnabled = false,
            // Unified placement convention: Pos is the world position of the anchor, namely the bounding-box center.
            // The resulting world span is x in [-0.8,0.8], y=0.05, and z in [-120,156].
            PosX = 0f,
            PosY = 0.05f,
            PosZ = 18f,
        };

        // 2. Subdivided plane in local space, with x in [0,1.6], y=0, and z in [0,276].
        //    The anchor is the box center at (0.8,0,138). Each segment owns four vertices,
        //    UVs tile in [0,1], and winding uses the same {2,3,1,2,1,0} order as AddWallFace,
        //    where u=+X and v=+Z so -cross(u,v)=+Y and the front face points upward.
        var verts = new Vertex[cells * 4];
        var indices = new ushort[cells * 6];
        var normal = new Vector3(0, 1, 0);
        int vi = 0, ii = 0;
        for (int j = 0; j < cells; j++)
        {
            float z1 = length - j * cellSize;              // North edge of the segment, toward the room
            float z0 = MathF.Max(z1 - cellSize, 0f);       // South edge of the segment, with the last one closing at the local south edge
            verts[vi + 0] = MakeCubeVertex(new Vector3(0f, 0, z0), new Vector2(0, 0), normal);
            verts[vi + 1] = MakeCubeVertex(new Vector3(width, 0, z0), new Vector2(1, 0), normal);
            verts[vi + 2] = MakeCubeVertex(new Vector3(0f, 0, z1), new Vector2(0, 1), normal);
            verts[vi + 3] = MakeCubeVertex(new Vector3(width, 0, z1), new Vector2(1, 1), normal);
            indices[ii + 0] = (ushort)(vi + 2);
            indices[ii + 1] = (ushort)(vi + 3);
            indices[ii + 2] = (ushort)(vi + 1);
            indices[ii + 3] = (ushort)(vi + 2);
            indices[ii + 4] = (ushort)(vi + 1);
            indices[ii + 5] = (ushort)(vi + 0);
            vi += 4; ii += 6;
        }

        var surface = new Surface
        {
            Vertices = verts,
            Indices = indices,
            BaseColor = new Vector4(1f, 1f, 1f, 1f),   // The texture already contains the full color, including asphalt gray and white markings.
            MetallicFactor = 0f,
            RoughnessFactor = 0.9f,                    // Rough asphalt surface
            Unlit = false,                             // Enable PBR lighting and receive CSM shadows
            Mode = SurfaceBlendMode.Opaque,
            // Pixel source is sent straight to the GPU and consumed once during loading, with no disk output.
            TextureOverride = TextureUpdateSource.FromImage(new NativeImageData(512, 512, pixels)),
        };
        mesh.Surfaces.Add(surface);

        return mesh;
    }

    static Vertex MakeCubeVertex(Vector3 pos, Vector2 uv, Vector3 normal)
    {
        // Any reasonable tangent is fine because Mesh3D v1 renderMode=0 does not use TBN in the pixel shader.
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
    /// Procedural road texture pixels in RGBA8. The pattern is built from deterministic integer-hash value noise,
    /// so it stays stable across launches. A dark-gray asphalt base uses three octaves of low-frequency brightness variation,
    /// then white markings are layered on top: edge lines occupy u in [0,0.05] and [0.95,1], which is about 8cm
    /// on a 1.6m road width and remains continuous across tiles, while the dashed center line sits around the middle
    /// 6% of u and occupies v in [0.35,0.65], giving one 0.75m dash plus a 1.75m gap per 2.5m segment.
    /// </summary>
    static byte[] GenerateRoadTexturePixels(int size)
    {
        var pixels = new byte[size * size * 4];
        const uint seed = 0x7AAD2026u;
        int edgeLine = size / 20;       // 5% edge white line
        int dashHalf = size / 32;       // Half-width of the 3% center dashed line
        int half = size / 2;
        for (int py = 0; py < size; py++)
        {
            float v = py / (float)size;
            bool dashRow = v >= 0.35f && v <= 0.65f;
            for (int px = 0; px < size; px++)
            {
                float n = 0.5f * ValueNoise(px, py, 32, seed)
                        + 0.3f * ValueNoise(px, py, 16, seed ^ 0x9E3779B9u)
                        + 0.2f * ValueNoise(px, py, 8, seed ^ 0x85EBCA77u);

                // Asphalt base color, dark gray with slight brightness variation.
                var c = new Vector3(0.31f, 0.31f, 0.32f) * (0.80f + 0.4f * n);
                // White markings: continuous edge lines and a dashed center line inside the v range.
                bool line = px < edgeLine || px >= size - edgeLine
                         || (dashRow && Math.Abs(px - half) < dashHalf);
                if (line)
                    c = new Vector3(0.88f, 0.88f, 0.88f);

                int o = (py * size + px) * 4;
                pixels[o] = (byte)MathF.Round(Math.Clamp(c.X, 0f, 1f) * 255f);
                pixels[o + 1] = (byte)MathF.Round(Math.Clamp(c.Y, 0f, 1f) * 255f);
                pixels[o + 2] = (byte)MathF.Round(Math.Clamp(c.Z, 0f, 1f) * 255f);
                pixels[o + 3] = 255;
            }
        }
        return pixels;
    }

    /// <summary>Deterministic integer-hash value noise with smoothstep bilinear interpolation and O(1) cost per sample.</summary>
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
        // MathF provides no Lerp in .NET, so interpolation is written out manually.
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

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        if (grass.Update(time))
        {
            result = true;
        }

        if (road.Update(time))
        {
            result = true;
        }

        return result;
    }
}
