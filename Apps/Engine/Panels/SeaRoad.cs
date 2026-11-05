// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Engine.Panels;

/// <summary>
/// Seaside stair and road beside the pond's east dike: the same three stone steps the Pond lays on its north bank,
/// turned to face the sea, with an asphalt road of the Ground road's own make running from the stair foot east to the
/// coastal rock line.
///
/// <para><b>Why the flight sits on the east bank.</b> It stands against the pond's east dike, centered on the rim
/// pieces' row at the pond's own authored PosZ: the west face of the top step is sunk a little into the pieces'
/// footprint at <see cref="StairWestX"/>, because their grass slope rounds off before the bounding box edge and
/// standing off from that edge would leave a slit of grass between dike and stone. The top step lands one riser
/// below the rim crest, so the flight reads as the way up onto the east bank from the road, exactly the role the
/// pond's north stair plays on that side.</para>
///
/// <para><b>Same steps as below.</b> The layout is Pond.BuildSteps turned a quarter turn: three solid boxes rising
/// from the grass, the highest against the west so no step floats as a slab over the meadow, one riser per tread and
/// every riser under PlayerCollider.StepLimit. The climb runs westward onto the bank and the road leaves from the
/// bottom step's east face, so the flight keeps the sea straight ahead as it was authored.</para>
///
/// <para><b>The road.</b> From the stair foot the Ground road's own construction runs east instead of north: the same
/// procedural asphalt pixels (Ground.GenerateRoadTexturePixels), the same per-segment UV tiling, the same 5 cm lift
/// above the grass, and the same displayed width. It stops at x = 74, the line the coastal rocks straddle, right at
/// the hand-placed rock at (74, 1, -6), so the walk ends where the rock chain begins. The east corridor is the
/// sightline the rocks and mountains deliberately leave open, and the river only meets the coast far to the south of
/// it, so nothing crosses the road's path.</para>
///
/// Placement follows the Pond convention: every range is authored in panel-local coordinates and Update maps them to
/// world through PosX/PosY/PosZ, so dragging the panel on the property panel moves the stair and the road together.
/// </summary>
internal class SeaRoad : Panel
{
    // -- Steps on the east side of the pond's dike, in panel-local coordinates. Same three-box recipe as Pond: four
    //    equal risers reach the top step, one tread per step, and the transverse half width matches the pond's stair. --
    const int StepCount = 3;
    const float StepHalfWidth = 1.5f;
    const float StepRiser = RiverSegment.DikeTop / (StepCount + 1);
    const float StepTread = 0.5f;

    // Outer face of the pond's east rim row: the pieces stand centered on InnerHalfX + DikeWidth / 2 and are
    // DikeWidth across, so their bounding box east face is one whole piece width past the water line.
    const float DikeEastFace = Pond.InnerHalfX + RiverSegment.DikeWidth;   // 6.55

    // West face of the top step, tucked into the bank pieces' footprint rather than stood off from their box face:
    // the piece is an asymmetric slope whose grass side rounds off well before the bounding box edge, so sitting
    // the stair on 6.55 left a visible slit of grass between dike and stone. Sinking the top step's west end into
    // the slope closes the seam, exactly the way the rim pieces themselves overlap the crest they stand behind.
    const float StairWestX = DikeEastFace - 0.3f;   // 6.25

    // The flight is centered on the east rim row, which runs at the pond's own authored PosZ; the panel starts on
    // the same line so the two stay coaxial until someone drags one of them.
    const float StairCenterZ = Pond.StairFootZ;     // -7.05

    /// <summary>Panel-local X of the flight's bottom face; the road starts here.</summary>
    internal const float StairFootX = StairWestX + StepCount * StepTread;

    // -- Road from the stair foot to the rock line. The rocks straddle the east coast at x = 74~75, so the asphalt
    //    stops at 74 and the walk ends where the rock chain begins. --
    const float RockLineX = 74f;
    const float RoadLength = RockLineX - StairFootX;
    const float RoadWidth = 1.6f;       // Mesh width, displayed at 2 m like the south road.
    const float RoadCellSize = 2.5f;    // Same 2.5 m dash pitch as Ground.BuildRoad.

    // Stone tone and walkable ranges in panel-local coordinates, one-to-one with stairParts like Pond's walkMins/walkMaxs.
    static readonly Vector4 StoneColor = new Vector4(0.55f, 0.53f, 0.50f, 1f);
    readonly Vector3[] stairMins, stairMaxs;

    internal Mesh3D[] stairParts;

    internal Mesh3D road;

    internal SeaRoad()
    {
        var steps = BuildSteps();
        stairParts = new Mesh3D[steps.Length];
        stairMins = new Vector3[steps.Length];
        stairMaxs = new Vector3[steps.Length];
        for (int i = 0; i < steps.Length; i++)
        {
            stairParts[i] = steps[i].Mesh;
            stairMins[i] = steps[i].Min;
            stairMaxs[i] = steps[i].Max;

            // Explicit DDGI albedo matching the visible stone color, as Pond does for its own steps.
            stairParts[i].GiAlbedo = new Vector3(StoneColor.X, StoneColor.Y, StoneColor.Z);
            AddControl(stairParts[i]);
        }

        road = BuildRoad();
        AddControl(road);
    }

    /// <summary>
    /// Walkable height under a movement footprint: the centre plus four corners sampled against the steps, the same
    /// five-point contract Bridge uses so a footprint straddling a riser is lifted to the tread it touches.
    /// </summary>
    internal float FloorHeightUnder(in Vector3 center, in Vector3 half)
    {
        float h = FloorHeightAtWorld(center.X, center.Z);
        h = MathF.Max(h, FloorHeightAtWorld(center.X - half.X, center.Z - half.Z));
        h = MathF.Max(h, FloorHeightAtWorld(center.X - half.X, center.Z + half.Z));
        h = MathF.Max(h, FloorHeightAtWorld(center.X + half.X, center.Z - half.Z));
        h = MathF.Max(h, FloorHeightAtWorld(center.X + half.X, center.Z + half.Z));
        return h;
    }

    /// <summary>
    /// Returns the walkable height in world Y at a world-space XZ point: the top of a step when the point lies on one,
    /// otherwise 0, meaning the grass or the road. Same contract as Pond.FloorHeightAtWorld.
    /// </summary>
    internal float FloorHeightAtWorld(float worldX, float worldZ)
    {
        float x = worldX - PosX, z = worldZ - PosZ;

        float top = 0f;
        for (int i = 0; i < stairMins.Length; i++)
        {
            if (x >= stairMins[i].X && x <= stairMaxs[i].X &&
                z >= stairMins[i].Z && z <= stairMaxs[i].Z)
                top = MathF.Max(top, stairMaxs[i].Y);
        }

        return top <= 0f ? 0f : PosY + top;
    }

    /// <summary>
    /// The three stone steps, descending eastward from the dike's outer face toward the sea. Built like
    /// Pond.BuildSteps: each step is solid from the ground up, with the highest one against the west, so no step is a
    /// slab floating over the meadow. The flight is centered on the rim row at <see cref="StairCenterZ"/>.
    /// </summary>
    static (Mesh3D Mesh, Vector3 Min, Vector3 Max)[] BuildSteps()
    {
        var parts = new List<(Mesh3D Mesh, Vector3 Min, Vector3 Max)>(StepCount);
        for (int i = 0; i < StepCount; i++)
        {
            float near = StairWestX + i * StepTread;
            parts.Add(MakeStairPart($"SeaStair{i + 1}",
                new Vector3(near, 0f, StairCenterZ - StepHalfWidth),
                new Vector3(near + StepTread, (StepCount - i) * StepRiser, StairCenterZ + StepHalfWidth)));
        }

        return parts.ToArray();
    }

    /// <summary>
    /// Creates one stone box part as a unit box centered at the origin, returning its panel-local range for Update.
    /// Same construction and winding as Pond.MakePondPart.
    /// </summary>
    static (Mesh3D Mesh, Vector3 Min, Vector3 Max) MakeStairPart(string name, Vector3 min, Vector3 max)
    {
        var part = new Mesh3D()
        {
            Name = name,
            CullingEnabled = false,
        };
        AddBox(part, new Vector3(-0.5f), new Vector3(0.5f), StoneColor);
        return (part, min, max);
    }

    /// <summary>
    /// The east road in the Ground.BuildRoad mold, turned to run along +X: a 1.6 m wide strip of 2.5 m segments with
    /// the same procedural asphalt pixels. Vertices are generated in local space with x in [0, RoadLength], y = 0 and
    /// z in [-0.8, 0.8], so the anchor is the box center at (RoadLength / 2, 0, 0). The vertex order keeps Ground's
    /// frame exactly: index 1 sits one cell along +X and index 2 one half width along +Z, so the {2,3,1,2,1,0}
    /// winding traces the same clockwise-from-above outline and the front face points upward. Swapping those two
    /// vertices flips the face into the grass and the back-face cull then hides the whole strip. The texture U runs
    /// across the strip so the edge lines follow the road, and V tiles one dash pattern per segment.
    /// CastShadows stays false for the ground-sheet reason Ground documents: an unbroken slab would swallow the
    /// whole SDF volume into one GI proxy.
    /// </summary>
    static Mesh3D BuildRoad()
    {
        int cells = (int)MathF.Ceiling(RoadLength / RoadCellSize);

        var pixels = Ground.GenerateRoadTexturePixels(512);

        var mesh = new Mesh3D()
        {
            Name = "SeaRoad",
            CastShadows = false,
            CullingEnabled = false,
        };

        var verts = new Vertex[cells * 4];
        var indices = new ushort[cells * 6];
        var normal = new Vector3(0, 1, 0);
        int vi = 0, ii = 0;
        for (int j = 0; j < cells; j++)
        {
            float x0 = j * RoadCellSize;                       // West edge of the segment, toward the stair
            float x1 = MathF.Min(x0 + RoadCellSize, RoadLength); // East edge, with the last one closing on the rocks
            verts[vi + 0] = Ground.MakeCubeVertex(new Vector3(x0, 0, -RoadWidth * 0.5f), new Vector2(0, 0), normal);
            verts[vi + 1] = Ground.MakeCubeVertex(new Vector3(x1, 0, -RoadWidth * 0.5f), new Vector2(0, 1), normal);
            verts[vi + 2] = Ground.MakeCubeVertex(new Vector3(x0, 0, RoadWidth * 0.5f), new Vector2(1, 0), normal);
            verts[vi + 3] = Ground.MakeCubeVertex(new Vector3(x1, 0, RoadWidth * 0.5f), new Vector2(1, 1), normal);
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

    /// <summary>Six outward-facing faces of an axis-aligned box, with the same construction and winding as Pond.AddBox.</summary>
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

        // Same per-frame placement as Pond's steps, without scaling: translate each step's center and keep its size
        // in meters.
        for (int i = 0; i < stairParts.Length; i++)
        {
            var part = stairParts[i];
            var center = origin + (stairMins[i] + stairMaxs[i]) * 0.5f;
            var size = stairMaxs[i] - stairMins[i];

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

        // The road starts on the stair's bottom face and runs to the rock line on the flight's own center line,
        // 5 cm above the grass like the south road; Depth carries the displayed 2 m width, so the two strips match.
        road.PosX = origin.X + StairFootX + RoadLength * 0.5f;
        road.PosY = origin.Y + 0.05f;
        road.PosZ = origin.Z + StairCenterZ;
        road.Width = RoadLength;
        road.Height = 0;
        road.Depth = 2;

        if (road.Update(time))
        {
            result = true;
        }

        return result;
    }
}
