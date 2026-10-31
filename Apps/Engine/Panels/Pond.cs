// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Engine.Panels;

/// <summary>
/// Raised pond used as the water test bed for the normal target, SSR, refraction, and depth absorption.
/// A stone rim of four solid boxes stands on the grass, the rim top sits at 0.5m, and the water surface is a flat plane
/// at 0.4m inside it. The pond bottom is the grass itself, so the water has a real 0.4m depth below the surface,
/// enough for later refraction and depth-based absorption to read a meaningful thickness from SceneDepth.
/// Three stone steps on the north face, toward the player spawn, lead up to the rim. The rim is two player widths wide
/// so the player can walk all the way around and look down at the water from close range.
///
/// Placement follows the same convention as Room: all layout is described in pond-local coordinates whose origin is the pond
/// center on the ground, and Update maps it to world space through PosX/PosY/PosZ every frame. There is no scaling here,
/// because rim width, step height, and water depth are all measured against the player and should stay in meters.
/// Rim and steps are separate unit-box Mesh3D parts, matching the Room reasoning for GI proxies: one Mesh3D for the whole rim
/// would produce a single 14 x 0.5 x 12 solid proxy and fill the water volume in the SDF.
/// </summary>
internal class Pond : Panel
{
    // Rim and step parts in the same layout as Room.roomParts, with pond-local ranges one-to-one in partMins and partMaxs.
    internal Mesh3D[] pondParts;

    Vector3[] partMins, partMaxs;

    // Water surface: a flat plane spanning the pond interior. It is kept separate from pondParts because it is neither walkable nor a GI proxy.
    internal Mesh3D water;

    // Pond-local layout. Orientation convention matches Room: north = +Z, south = -Z, east = +X, west = -X.
    const float InnerHalfX = 5f;        // Water extends 10m along X.
    const float InnerHalfZ = 4f;        // Water extends 8m along Z.
    const float RimWidth = 2f;          // Two player widths, since the player model is 1m wide.
    const float RimTop = 0.5f;          // Rim top above the grass.
    const float WaterY = 0.4f;          // Water surface 0.1m below the rim top, leaving a visible lip.

    // Front steps on the north face. Four equal risers of 0.125m reach the rim top: three step boxes plus the rim itself.
    // Each riser stays well under PlayerCollider.StepLimit, while the rim face itself is too high to climb directly.
    const int StepCount = 3;
    const float StepHalfWidth = 1.5f;
    const float StepRiser = RimTop / (StepCount + 1);
    const float StepTread = 0.5f;

    static readonly Vector4 StoneColor = new Vector4(0.55f, 0.53f, 0.50f, 1f);

    // Water tint with partial coverage so the grass bottom stays visible through the surface.
    static readonly Vector4 WaterColor = new Vector4(0.10f, 0.32f, 0.38f, 0.7f);

    internal Pond()
    {
        var layout = BuildPondParts();
        pondParts = new Mesh3D[layout.Length];
        partMins = new Vector3[layout.Length];
        partMaxs = new Vector3[layout.Length];
        for (int i = 0; i < layout.Length; i++)
        {
            pondParts[i] = layout[i].Mesh;
            partMins[i] = layout[i].Min;
            partMaxs[i] = layout[i].Max;

            // Explicit DDGI albedo matching the visible stone color, as Room does for its walls.
            pondParts[i].GiAlbedo = new Vector3(StoneColor.X, StoneColor.Y, StoneColor.Z);
            AddControl(pondParts[i]);
        }

        // CastShadows=false keeps the water out of shadow casting and GI proxies: a solid proxy over the water would fill
        // the pond in the SDF, and a transparent sheet should not darken the grass bottom it is meant to reveal.
        water = new Mesh3D()
        {
            Name = "PondWater",
            CastShadows = false,
            CullingEnabled = false,
            // Leave Width/Height/Depth unset so OnBoundsEstablished keeps native size with a scale of 1.
        };
        water.Surfaces.Add(BuildWaterSurface());
        AddControl(water);
    }

    /// <summary>
    /// World-space water surface height, for later water effects that need the plane without reading the mesh.
    /// </summary>
    internal float WaterLevel => PosY + WaterY;

    /// <summary>
    /// Returns the walkable height in world Y at a world-space XZ point: the rim top or a step top when the point lies on them,
    /// otherwise 0, meaning the grass, including the grass that forms the pond bottom. Same contract as Room.FloorHeightAtWorld.
    /// </summary>
    internal float FloorHeightAtWorld(float worldX, float worldZ)
    {
        float x = worldX - PosX, z = worldZ - PosZ;

        float top = 0f;
        for (int i = 0; i < pondParts.Length; i++)
        {
            var min = partMins[i];
            var max = partMaxs[i];

            if (x >= min.X && x <= max.X && z >= min.Z && z <= max.Z)
                top = MathF.Max(top, max.Y);
        }

        return top <= 0f ? 0f : PosY + top;
    }

    /// <summary>
    /// Overlap area between a footprint, given as world center and half extents, and the water area inside the rim.
    /// PlayerCollider uses it to keep the player on the rim: entering the water is blocked, and a footprint that already
    /// overlaps, for example after turning in place at the water line, may only move so the overlap shrinks.
    /// Touching the inner rim edge gives zero, so the player can stand right at the water line.
    /// </summary>
    internal float WaterOverlap(in Vector3 center, in Vector3 half)
    {
        float x = center.X - PosX, z = center.Z - PosZ;
        float ox = MathF.Min(x + half.X, InnerHalfX) - MathF.Max(x - half.X, -InnerHalfX);
        float oz = MathF.Min(z + half.Z, InnerHalfZ) - MathF.Max(z - half.Z, -InnerHalfZ);
        return ox > 0f && oz > 0f ? ox * oz : 0f;
    }

    /// <summary>
    /// Builds the rim from four solid boxes around the water plus the north steps. North and south rims span the full X range,
    /// and the west and east rims fill between them, so the four boxes meet without overlapping and form a closed ring.
    /// Inner faces of the rim boxes become the pond walls, and the grass between them is the pond bottom.
    /// </summary>
    static (Mesh3D Mesh, Vector3 Min, Vector3 Max)[] BuildPondParts()
    {
        const float x0 = -InnerHalfX, x1 = InnerHalfX, z0 = -InnerHalfZ, z1 = InnerHalfZ, w = RimWidth;

        var parts = new List<(Mesh3D Mesh, Vector3 Min, Vector3 Max)>
        {
            MakePondPart("PondRimPosZ", new Vector3(x0 - w, 0f, z1), new Vector3(x1 + w, RimTop, z1 + w)),
            MakePondPart("PondRimNegZ", new Vector3(x0 - w, 0f, z0 - w), new Vector3(x1 + w, RimTop, z0)),
            MakePondPart("PondRimNegX", new Vector3(x0 - w, 0f, z0), new Vector3(x0, RimTop, z1)),
            MakePondPart("PondRimPosX", new Vector3(x1, 0f, z0), new Vector3(x1 + w, RimTop, z1)),
        };

        // Steps descend northward from the outer face of the north rim, built like Room.BuildStairParts:
        // each step is solid from the ground up, with the highest step against the rim.
        const float rimFaceZ = z1 + w;
        for (int i = 0; i < StepCount; i++)
        {
            float near = rimFaceZ + i * StepTread;
            parts.Add(MakePondPart($"PondStair{StepCount - i}",
                new Vector3(-StepHalfWidth, 0f, near),
                new Vector3(StepHalfWidth, (StepCount - i) * StepRiser, near + StepTread)));
        }

        return parts.ToArray();
    }

    /// <summary>
    /// Creates one stone box part as a unit box centered at the origin, returning its pond-local range for Update.
    /// Names stay unique because graphics backends key GPU resources by (Name, ID).
    /// </summary>
    static (Mesh3D Mesh, Vector3 Min, Vector3 Max) MakePondPart(string name, Vector3 min, Vector3 max)
    {
        var part = new Mesh3D()
        {
            Name = name,
            CullingEnabled = false,
        };
        AddBox(part, new Vector3(-0.5f), new Vector3(0.5f), StoneColor);
        return (part, min, max);
    }

    /// <summary>Six outward-facing faces of an axis-aligned box, with the same construction and winding as Room.AddRoomBox.</summary>
    static void AddBox(Mesh3D mesh, Vector3 min, Vector3 max, Vector4 color)
    {
        var s = max - min;
        var sx = new Vector3(s.X, 0, 0);
        var sy = new Vector3(0, s.Y, 0);
        var sz = new Vector3(0, 0, s.Z);

        AddFace(mesh, origin: new Vector3(min.X, min.Y, min.Z), u: sx, v: sy, normal: new Vector3(0, 0, -1), color: color);
        AddFace(mesh, origin: new Vector3(max.X, min.Y, max.Z), u: -sx, v: sy, normal: new Vector3(0, 0, +1), color: color);
        AddFace(mesh, origin: new Vector3(min.X, min.Y, max.Z), u: -sz, v: sy, normal: new Vector3(-1, 0, 0), color: color);
        AddFace(mesh, origin: new Vector3(max.X, min.Y, min.Z), u: sz, v: sy, normal: new Vector3(+1, 0, 0), color: color);
        AddFace(mesh, origin: new Vector3(min.X, max.Y, min.Z), u: sx, v: sz, normal: new Vector3(0, +1, 0), color: color);
        AddFace(mesh, origin: new Vector3(min.X, min.Y, max.Z), u: sx, v: -sz, normal: new Vector3(0, -1, 0), color: color);
    }

    /// <summary>One solid-color stone quad: dielectric with roughness 0.85, a little smoother than the Room plaster.</summary>
    static void AddFace(Mesh3D mesh, Vector3 origin, Vector3 u, Vector3 v, Vector3 normal, Vector4 color)
    {
        var verts = new Vertex[4];
        verts[0] = MakeVertex(origin, new Vector2(0, 1), normal);
        verts[1] = MakeVertex(origin + u, new Vector2(1, 1), normal);
        verts[2] = MakeVertex(origin + v, new Vector2(0, 0), normal);
        verts[3] = MakeVertex(origin + u + v, new Vector2(1, 0), normal);

        mesh.Surfaces.Add(new Surface
        {
            Vertices = verts,
            Indices = new ushort[] { 2, 3, 1, 2, 1, 0 },
            BaseColor = color,
            MetallicFactor = 0f,
            RoughnessFactor = 0.85f,
            Unlit = false,
            Mode = SurfaceBlendMode.Opaque,
        });
    }

    /// <summary>
    /// Water plane centered at the local origin, covering the pond interior plus 1cm under the rim on every side
    /// so no seam opens against the inner rim faces. Winding follows Ground.BuildRoad with u=+X and v=+Z, so the front face points up.
    /// The plane is flat for now; normals come from the mesh until a dedicated water pass provides surface detail.
    /// </summary>
    static Surface BuildWaterSurface()
    {
        const float hx = InnerHalfX + 0.01f, hz = InnerHalfZ + 0.01f;
        var up = new Vector3(0, 1, 0);

        return new Surface
        {
            Vertices = new[]
            {
                MakeVertex(new Vector3(-hx, 0f, -hz), new Vector2(0, 1), up),
                MakeVertex(new Vector3(hx, 0f, -hz), new Vector2(1, 1), up),
                MakeVertex(new Vector3(-hx, 0f, hz), new Vector2(0, 0), up),
                MakeVertex(new Vector3(hx, 0f, hz), new Vector2(1, 0), up),
            },
            Indices = new ushort[] { 2, 3, 1, 2, 1, 0 },
            BaseColor = WaterColor,
            MetallicFactor = 0f,                          // Water is a dielectric.
            RoughnessFactor = 0.05f,                      // Near-mirror so sun and sky highlights read clearly on the flat plane.
            Unlit = false,
            Mode = SurfaceBlendMode.Blend,
        };
    }

    static Vertex MakeVertex(Vector3 pos, Vector2 uv, Vector3 normal)
    {
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

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        var origin = new Vector3(PosX, PosY, PosZ);

        // Same per-frame placement as Room, without scaling: translate each part center and keep its size in meters.
        for (int i = 0; i < pondParts.Length; i++)
        {
            var part = pondParts[i];
            var min = partMins[i];
            var max = partMaxs[i];

            var center = origin + (min + max) * 0.5f;
            var size = max - min;

            part.PosX = center.X;
            part.PosY = center.Y;
            part.PosZ = center.Z;
            part.Width = size.X;
            part.Height = size.Y;
            part.Depth = size.Z;

            if (part.Update(time))
            {
                result = true;
            }
        }

        water.PosX = origin.X;
        water.PosY = WaterLevel;
        water.PosZ = origin.Z;
        if (water.Update(time))
        {
            result = true;
        }

        return result;
    }
}
