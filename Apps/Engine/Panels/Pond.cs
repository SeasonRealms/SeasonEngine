// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Engine.Panels;

/// <summary>
/// Raised pond at the head of the south branch, and the water test bed for the normal target, SSR, refraction, and depth
/// absorption. The water surface is a flat plane at <see cref="WaterY"/> drawn by the dedicated P2 water pass from
/// <see cref="Season.Rendering.WaterPlane"/> data, the grass is its bottom, and the basin is held by a closed ring of the
/// same bank piece the river lays along its own banks (<see cref="River.DikeGlbPath"/>). The stone rim the panel used to
/// build is gone: the pond now reads as the first stretch of the same water system as the channel it feeds, rather than
/// as a concrete basin standing next to it.
///
/// <para><b>The south side is open.</b> That is where the branch leaves, so the ring stops either side of an opening
/// <see cref="GapHalf"/> wide - the branch's own half width plus a hand's width - and the pond's plane and the branch's
/// water meet across it at the same height. The two levels are not independent: the branch's first control point takes
/// <see cref="WaterY"/> and <see cref="SouthEdgeZ"/> from here, so moving or re-levelling the pond carries the river with
/// it instead of opening a step at the outlet.</para>
///
/// <para><b>The north side is the way in.</b> Three stone steps climb from the grass to the top of the ring, on the same
/// axis as the road, and <see cref="StairFootZ"/> puts their bottom face exactly on the road's southern end - which is
/// also the player's spawn point - so the road runs straight up the pond's north bank and there is no gap for the player
/// to fall through between the two.</para>
///
/// Placement follows the same convention as Room: all layout is described in pond-local coordinates whose origin is the
/// pond center on the ground, and Update maps it to world space through PosX/PosY/PosZ every frame. There is no scaling
/// here, because the ring's width, the step heights and the water depth are all measured against the player and should
/// stay in meters.
/// </summary>
internal class Pond : Panel
{
    // The stone steps, in the same layout as Room.roomParts, with pond-local ranges one-to-one in partMins and partMaxs.
    internal Mesh3D[] pondParts;

    // Every walkable surface of the pond in pond-local coordinates: the ring's four bands and the three steps. Kept apart
    // from pondParts because the ring is instanced geometry with no Mesh3D to derive a range from.
    readonly Vector3[] walkMins, walkMaxs;

    /// <summary>
    /// The rim ring: one instance of the river's bank piece per piece, one draw. Registered by <see cref="App"/> through
    /// the panel like any other control, so it follows Pos without a line of bookkeeping.
    /// </summary>
    internal InstancedModel Rim { get; }

    // Water surface: a flat rectangle spanning the pond interior. It is not walkable and not a GI proxy, so it is not in
    // pondParts. What is stored here is data, not a mesh: P2's water Pass computes the 6 vertices of the rectangle on the
    // fly, and the normals, refraction and absorption all live in the shader.
    internal Season.Rendering.WaterPlane Water { get; }

    // -- Pond-local layout. Orientation convention matches Room: north = +Z, south = -Z, east = +X, west = -X. --

    /// <summary>Half the water's extent along X; the water is <c>2 * InnerHalfX</c> across.</summary>
    internal const float InnerHalfX = 5f;

    /// <summary>Half the water's extent along Z. Internal because the branch starts on this line, see <see cref="SouthEdgeZ"/>.</summary>
    internal const float InnerHalfZ = 4f;

    /// <summary>
    /// How far outside the water line the ring's pieces stand, center to center: half a piece, so their inner face is the
    /// pond wall. The river sets its own pieces back by <c>CrestWidth - DikeInset</c> further out than this, because on a
    /// channel there is a levee crest for them to stand behind; the pond has no crest, so the piece itself is the edge.
    /// </summary>
    const float RimReach = RiverSegment.DikeWidth * 0.5f;

    /// <summary>
    /// Top of the rim: the top of a bank piece standing on the grass, i.e. its height less the part that is buried. This is
    /// what the steps climb to, and the height <see cref="FloorHeightAtWorld"/> reports along the ring.
    /// </summary>
    internal const float RimTop = RiverSegment.DikeTop;

    /// <summary>
    /// Water surface above the grass: deep enough for refraction and depth-based absorption to read a real thickness, and
    /// low enough that the ring stands well proud of it.
    /// </summary>
    internal const float WaterY = 0.4f;

    /// <summary>
    /// Half the width of the opening in the south rim. The branch leaves through it, so it is the branch's own half width
    /// plus a hand's width either side: narrower and the rim would pinch the river's water, wider and the two ends of the
    /// ring would stand back from the water they are supposed to hold.
    /// </summary>
    const float GapHalf = River.BranchHalfWidth + 0.10f;

    // -- Steps on the north face. Four equal risers reach the rim top: three step boxes plus the ring itself. Each riser
    //    stays well under PlayerCollider.StepLimit, while the ring's face is too high to climb directly. --
    const int StepCount = 3;
    const float StepHalfWidth = 1.5f;
    const float StepRiser = RimTop / (StepCount + 1);
    const float StepTread = 0.5f;

    /// <summary>
    /// How far each of the four rows runs past its corner. The rows have to overlap the way neighbours along a river do,
    /// or four straight lines meeting end to end would leave a notch at each of the four corners.
    /// </summary>
    const float CornerReach = 0.9f;

    /// <summary>
    /// The panel's PosZ that puts the bottom step's outer face exactly on the road's southern end, z = 0, which is also
    /// where the player spawns. The panel's Pos stays free; this is only the authored value <see cref="App"/> starts from.
    /// </summary>
    internal const float StairFootZ = -(InnerHalfZ + RiverSegment.DikeWidth + StepCount * StepTread);

    /// <summary>
    /// World Z of the water's south edge when the panel sits at <see cref="StairFootZ"/>. The branch begins on this line,
    /// which is what makes the pond's plane and the branch's ribbon one surface across the opening instead of two with a
    /// seam between them; see <see cref="River.BranchCourse"/>.
    /// </summary>
    internal const float SouthEdgeZ = StairFootZ - InnerHalfZ;

    static readonly Vector4 StoneColor = new Vector4(0.55f, 0.53f, 0.50f, 1f);

    // Water tint with partial coverage so the grass bottom stays visible through the surface.
    static readonly Vector4 WaterColor = new Vector4(0.10f, 0.32f, 0.38f, 1f);

    // Opacity when viewed straight on: 0.35 still lets the pond-bottom grass and caustics show through, and at grazing
    // angles Fresnel pushes it towards 1.
    const float WaterOpacity = 0.35f;

    /// <summary>One piece of the ring: where its anchor goes, which way its length runs, and which face looks at the water.</summary>
    readonly record struct RimPiece(Vector3 Center, Vector3 Along, float Flip);

    // The ring, in pond-local coordinates, resolved once. The instances are created on the first frame the shared
    // template's bounds are known, because only then is it clear which of the asset's axes carries its length.
    readonly List<RimPiece> ring = new();
    bool rimSettled, rimPlaced;
    float rimYaw, rimSpanX, rimSpanZ;
    Vector3 rimOrigin;

    internal Pond()
    {
        var steps = BuildSteps();
        pondParts = new Mesh3D[steps.Length];
        for (int i = 0; i < steps.Length; i++)
        {
            pondParts[i] = steps[i].Mesh;

            // Explicit DDGI albedo matching the visible stone color, as Room does for its walls.
            pondParts[i].GiAlbedo = new Vector3(StoneColor.X, StoneColor.Y, StoneColor.Z);
            AddControl(pondParts[i]);
        }

        var (mins, maxs) = BuildWalkable(steps);
        walkMins = mins;
        walkMaxs = maxs;

        Rim = new InstancedModel
        {
            Name = "PondRim",
            ModelName = River.DikeGlbPath,

            // A piece is a 4 m strip, so its world AABB goes fat as soon as it turns - the same reason the river's own
            // bank batch uses Wireframe.
            Highlight = new Highlight { Style = HighlightStyle.Wireframe, EdgeWidth = 0.004f },
        };
        AddControl(Rim);

        ring.AddRange(BuildRing());

        // The surface has retired the old transparent Mesh3D: it could only be a material of the main shader, and the main
        // shader cannot sample this frame's SceneColor/SceneDepth (in the transparent pass both are attachments). Only P2's
        // own Pass can read (snapshot + depth) and write (scene colour) at once, and occlusion is now handled by a software
        // depth test in the shader. Registration is done by App, and the position follows the panel every frame.
        Water = new Season.Rendering.WaterPlane
        {
            // 1 cm beyond the inner edge: covers the seam against the inner wall. The inner wall is now the inner face of the
            // dyke pieces, so this 1 cm tucks exactly under the pieces.
            Size = new Vector2(2f * InnerHalfX + 0.02f, 2f * InnerHalfZ + 0.02f),
            Tint = new Vector4(WaterColor.X, WaterColor.Y, WaterColor.Z, 1f),
            Opacity = WaterOpacity,
        };
    }

    /// <summary>
    /// World-space water surface height, for later water effects that need the plane without reading the mesh.
    /// </summary>
    internal float WaterLevel => PosY + WaterY;

    /// <summary>
    /// Returns the walkable height in world Y at a world-space XZ point: the top of the ring or of a step when the point
    /// lies on one, otherwise 0, meaning the grass, including the grass that forms the pond bottom. Same contract as
    /// Room.FloorHeightAtWorld.
    /// </summary>
    internal float FloorHeightAtWorld(float worldX, float worldZ)
    {
        float x = worldX - PosX, z = worldZ - PosZ;

        float top = 0f;
        for (int i = 0; i < walkMins.Length; i++)
        {
            var min = walkMins[i];
            var max = walkMaxs[i];

            if (x >= min.X && x <= max.X && z >= min.Z && z <= max.Z)
                top = MathF.Max(top, max.Y);
        }

        return top <= 0f ? 0f : PosY + top;
    }

    /// <summary>
    /// Overlap area between a footprint, given as world center and half extents, and the water area inside the ring.
    /// PlayerCollider uses it to keep the player on the bank: entering the water is blocked, and a footprint that already
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
    /// The three stone steps, descending northward from the ring's outer face. Built like Room.BuildStairParts: each step is
    /// solid from the ground up, with the highest one against the rim, so no step is a slab floating over the meadow.
    /// </summary>
    static (Mesh3D Mesh, Vector3 Min, Vector3 Max)[] BuildSteps()
    {
        const float rimFaceZ = InnerHalfZ + RiverSegment.DikeWidth;

        var parts = new List<(Mesh3D Mesh, Vector3 Min, Vector3 Max)>(StepCount);
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
    /// The walkable surfaces in pond-local coordinates: four rectangles for the ring's bands and one per step. The bands
    /// meet at the corners exactly as the old stone ring's four boxes did; the opening in the south band is deliberately
    /// not cut out, because what lies below it is water and the collider already refuses that footprint.
    /// </summary>
    static (Vector3[] Mins, Vector3[] Maxs) BuildWalkable((Mesh3D Mesh, Vector3 Min, Vector3 Max)[] steps)
    {
        const float ex = InnerHalfX + RimReach, ez = InnerHalfZ + RimReach, far = RiverSegment.DikeWidth * 0.5f;

        var mins = new List<Vector3>(4 + steps.Length);
        var maxs = new List<Vector3>(4 + steps.Length);

        // North and south bands run along X, east and west along Z - the same two axes their row of pieces is laid on.
        AddBand(mins, maxs, new Vector3(-ex - CornerReach, 0f, ez - far), new Vector3(ex + CornerReach, RimTop, ez + far));
        AddBand(mins, maxs, new Vector3(-ex - CornerReach, 0f, -ez - far), new Vector3(ex + CornerReach, RimTop, -ez + far));
        AddBand(mins, maxs, new Vector3(ex - far, 0f, -ez - CornerReach), new Vector3(ex + far, RimTop, ez + CornerReach));
        AddBand(mins, maxs, new Vector3(-ex - far, 0f, -ez - CornerReach), new Vector3(-ex + far, RimTop, ez + CornerReach));

        for (int i = 0; i < steps.Length; i++)
        {
            mins.Add(steps[i].Min);
            maxs.Add(steps[i].Max);
        }

        return (mins.ToArray(), maxs.ToArray());
    }

    static void AddBand(List<Vector3> mins, List<Vector3> maxs, Vector3 min, Vector3 max)
    {
        mins.Add(min);
        maxs.Add(max);
    }

    /// <summary>
    /// Four rows of bank pieces around the water, the south one open where the branch leaves. Each row is laid like the
    /// river's own line - neighbours overlapping by the same amount - and every row runs <see cref="CornerReach"/> past its
    /// two corners, so the four lines close on each other instead of meeting end to end.
    /// </summary>
    static List<RimPiece> BuildRing()
    {
        const float ex = InnerHalfX + RimReach, ez = InnerHalfZ + RimReach;

        var pieces = new List<RimPiece>(16);

        AddRun(pieces, new Vector3(-ex - CornerReach, 0f, ez), new Vector3(ex + CornerReach, 0f, ez), new Vector3(0f, 0f, 1f), outlet: false);
        AddRun(pieces, new Vector3(-ex - CornerReach, 0f, -ez), new Vector3(ex + CornerReach, 0f, -ez), new Vector3(0f, 0f, -1f), outlet: true);
        AddRun(pieces, new Vector3(ex, 0f, -ez - CornerReach), new Vector3(ex, 0f, ez + CornerReach), new Vector3(1f, 0f, 0f), outlet: false);
        AddRun(pieces, new Vector3(-ex, 0f, -ez - CornerReach), new Vector3(-ex, 0f, ez + CornerReach), new Vector3(-1f, 0f, 0f), outlet: false);

        return pieces;
    }

    /// <summary>One side of the ring, split around the opening when this is the south side the branch leaves through.</summary>
    static void AddRun(List<RimPiece> pieces, Vector3 from, Vector3 to, Vector3 outward, bool outlet)
    {
        if (!outlet)
        {
            AddSpan(pieces, from, to, outward);
            return;
        }

        // The axis through the pond center is where the branch runs, so the opening is centred on it and the two stubs run
        // from there out to the corners.
        var middle = (from + to) * 0.5f;
        AddSpan(pieces, from, middle + new Vector3(-GapHalf, 0f, 0f), outward);
        AddSpan(pieces, middle + new Vector3(GapHalf, 0f, 0f), to, outward);
    }

    /// <summary>
    /// One straight row of pieces. Centers are spread over the length that is left once a whole piece is on the run, so the
    /// two end pieces land on the run's own ends rather than overhanging the corner or the opening.
    /// </summary>
    static void AddSpan(List<RimPiece> pieces, Vector3 from, Vector3 to, Vector3 outward)
    {
        var span = to - from;
        float run = span.Length();
        if (run < 1e-4f)
            return;

        var along = span / run;

        float slack = run - RiverSegment.DikeLength;
        int count = slack <= 0f ? 1 : (int)MathF.Ceiling(slack / RiverSegment.DikeSpacing) + 1;
        float step = count > 1 ? slack / (count - 1) : 0f;

        for (int i = 0; i < count; i++)
        {
            var center = from + along * (RiverSegment.DikeLength * 0.5f + step * i);

            // The asset is asymmetric - one grass slope against one cut soil face - and the soil face has to look at the
            // water. Which of the piece's two long faces local +Z is was settled for the river's own line, so the ring only
            // has to match it; the heading below turns local +Z to (-along.Z, 0, along.X), and half the rows therefore
            // need a half turn.
            var face = new Vector3(-along.Z, 0f, along.X);
            float flip = Vector3.Dot(face, outward) > 0f ? MathF.PI : 0f;

            pieces.Add(new RimPiece(center, along, flip));
        }
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

    /// <summary>
    /// Creates the ring's instances once the shared template's bounds have arrived, and decides which of the asset's
    /// horizontal axes carries its length - the same two questions the river's own bank batch asks, see
    /// <see cref="River.SettleDikes"/>.
    /// </summary>
    void SettleRim()
    {
        var local = Rim.TemplateLocalSize;
        if (local.X <= 1e-6f || local.Z <= 1e-6f)
            return;

        // Reaching +Z from the long axis: yaw 0 already points +Z there, -90 degrees turns +X into +Z.
        bool longAxisIsX = local.X >= local.Z;
        rimYaw = longAxisIsX ? -MathF.PI * 0.5f : 0f;
        rimSpanX = longAxisIsX ? RiverSegment.DikeLength : RiverSegment.DikeWidth;
        rimSpanZ = longAxisIsX ? RiverSegment.DikeWidth : RiverSegment.DikeLength;

        for (int i = 0; i < ring.Count; i++)
        {
            Rim.Instances.Add(new MeshInstanceTransform
            {
                Name = $"PondRim{i + 1}",
                Width = rimSpanX,
                Height = RiverSegment.DikeHeight,
                Depth = rimSpanZ,
            });
        }

        rimSettled = true;
    }

    /// <summary>
    /// Folds the panel's current position into every piece of the ring. The ring is authored in pond-local coordinates, so
    /// the whole bank follows the pond when the property panel drags it - the same one-matrix rule the stairs follow.
    /// </summary>
    void PlaceRim()
    {
        rimOrigin = new Vector3(PosX, PosY, PosZ);
        rimPlaced = true;

        // The anchor is the piece's own box center, so the top of the rim is half a piece above it, less the buried part.
        float lift = RimTop - RiverSegment.DikeHeight * 0.5f;

        for (int i = 0; i < ring.Count; i++)
        {
            var piece = ring[i];
            var instance = Rim.Instances[i];

            instance.PosX = rimOrigin.X + piece.Center.X;
            instance.PosY = rimOrigin.Y + lift;
            instance.PosZ = rimOrigin.Z + piece.Center.Z;

            float heading = MathF.Atan2(piece.Along.X, piece.Along.Z) + rimYaw + RiverSegment.DikeFacing + piece.Flip;
            instance.Rotation = Quaternion.CreateFromYawPitchRoll(heading, 0f, 0f);
        }
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        var origin = new Vector3(PosX, PosY, PosZ);

        // Same per-frame placement as Room, without scaling: translate each step's center and keep its size in meters.
        for (int i = 0; i < pondParts.Length; i++)
        {
            var part = pondParts[i];
            var min = walkMins[walkMins.Length - pondParts.Length + i];
            var max = walkMaxs[walkMaxs.Length - pondParts.Length + i];

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

        // The template's bounds arrive asynchronously with the queued load, so the ring is laid out on the first frame
        // that reports them; after that it only moves when the panel does.
        if (!rimSettled)
            SettleRim();

        if (rimSettled)
        {
            if (!rimPlaced || origin != rimOrigin)
                PlaceRim();

            if (Rim.Update(time))
                result = true;
        }

        // The water rectangle is a piece of world-space data (no mesh and no child control); it simply follows the panel.
        Water.Position = new Vector3(origin.X, WaterLevel, origin.Z);

        return result;
    }
}
