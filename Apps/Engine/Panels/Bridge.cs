// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Engine.Panels;

/// <summary>
/// Stone arch bridge over the main river course, at the spot the player was standing - roughly halfway
/// between the confluence with the south branch (x = 0) and the waterfall pool (x = -64).
///
/// <para><b>Two sources of truth, deliberately split.</b> The visible span is an AI-generated GLB
/// (<c>Assets/Bridge.glb</c>, produced by <c>.dbg/_bridge_assets</c> - SeasonImage then SeasonThreeD) laid
/// across the channel. The walkable surface is NOT read from that mesh: like <see cref="Tower"/>, the deck
/// height is authored in code through <see cref="FloorHeightUnder"/>, because an AI mesh has no clean
/// collision. The two agree only approximately, which is fine - the collider is authoritative for movement
/// and the GLB is authoritative for the look.</para>
///
/// <para><b>Why there are stone steps.</b> The generated bridge has a flat deck carried high on a single arch -
/// high so a boat can later pass under it. A flat deck 2.6 m above the banks cannot be walked onto directly
/// (PlayerCollider.StepLimit refuses a rise of more than 0.3 m per move), so two straight-run stone flights
/// are built here in code, one against each abutment, and the deck between them is a flat walkable floor -
/// the same stepped treatment the <see cref="Tower"/>"s flight and Room's stairs use, solid boxes from the
/// ground up to each tread, every riser comfortably under the step limit. The player walks up a flight, along
/// the deck, and down the other flight onto the opposite bank.</para>
///
/// <para><b>Why there are no railings.</b> The deck edges over the channel are kept safe by the water test
/// itself, not by geometry: <see cref="Management.PlayerCollider"/> only ignores the river's water while the
/// feet are on the bridge deck, so stepping off the side toward the water is rejected exactly as walking into
/// the river from a bank is. The only exits are the two flight bottoms on the banks, which is the intended path.</para>
///
/// <para>Layout is authored in bridge-local meters: the span axis (the direction the player walks across) is
/// <see cref="SpanX"/>/<see cref="SpanZ"/>, perpendicular to the channel flow, and the deck width runs along
/// the flow. The whole panel is placed at the crossing centre with no scaling, so the same numbers drive both
/// the GLB and the step flights.</para>
/// </summary>
internal class Bridge : Panel
{
    /// <summary>The generated span, produced by <c>.dbg/_bridge_assets</c> (SeasonImage -&gt; SeasonThreeD).</summary>
    internal const string BridgeGlbPath = @"Assets/Bridge.glb";

    // -- Crossing location and orientation, read off River.Course. --
    //
    // The channel here is the RiverBendWest stretch between control points 3 (-44, -56.7) and 5 (-8, -48.6).
    // The chord 3->5 is the local flow direction; the bridge stands perpendicular to it, centred on the
    // course at x = -32 (halfway from the confluence to the pool).

    const float CenterX = -32f;
    const float CenterZ = -52.9f;

    // Flow (downstream) unit vector in the XZ plane: normalize((-8 - -44), (-48.6 - -56.7)) = normalize(36, 8.1).
    const float FlowX = 0.9746f;
    const float FlowZ = 0.2193f;

    // Span (walk-across) unit vector: the flow turned by ninety degrees, (x, z) -> (-z, x).
    const float SpanX = -FlowZ;   // -0.2193
    const float SpanZ = FlowX;    //  0.9746

    // -- Walkable profile, in world meters above the grass (which is y = 0 at this spot). --

    /// <summary>Half the GLB span along the walk axis; the deck is flat between -HalfSpan and +HalfSpan.</summary>
    const float HalfSpan = 6.0f;

    // -- Approach flights, authored like Tower's stair: a straight run of solid step boxes, every riser
    //    comfortably under PlayerCollider.StepLimit (0.3 m). 13 risers of 0.2 m cover the 2.6 m to the deck. --

    const int StepCount = 13;
    const float Riser = DeckTop / StepCount;   // 0.2
    const float Tread = 0.3f;

    /// <summary>Horizontal run of one approach flight: StepCount treads, 3.9 m.</summary>
    const float FlightRun = StepCount * Tread;

    /// <summary>Half the deck width across the flow. The player is 1 m wide, so 2.4 m leaves room to pass a boat-watcher.</summary>
    const float HalfWidth = 1.2f;

    /// <summary>Top surface of the deck. High enough that the arch keeps clear headroom over the water for a boat.</summary>
    const float DeckTop = 2.6f;

    // -- Visual box of the generated span (meters). Length is authored along the GLB's local X. --

    const float ModelLength = 12.0f;   // 2 * HalfSpan
    const float ModelWidth = 2.4f;     // 2 * HalfWidth
    const float ModelHeight = 3.2f;    // the arch is tall; the walkable deck sits a little under the parapet top

    /// <summary>
    /// Height of the walkable deck surface inside the scaled model's bounding box, measured off Bridge.glb's
    /// vertex profile (.dbg/_bridge_assets/inspect_deck.py): the central-band plateau sits at 90.8% of the
    /// bbox height and the top nine percent is parapet walls and end posts. The first placement guessed this
    /// as "deck near the top minus half a metre", which left the visual surface ~0.2 m above the collision
    /// deck and the player's feet sunk into it (20261104112851.jpg).
    /// </summary>
    const float DeckFrac = 0.908f;

    // The Model anchor is its bounding-box centre and the generated bbox is centred on zero, so the deck
    // surface lands (2*DeckFrac - 1) * ModelHeight/2 above the anchor. Solve the anchor height so the visual
    // deck sits exactly on the collision DeckTop; the footings then dip just below the water surface at the
    // springing, which is what a real arch abutment does.
    const float ModelCenterY = DeckTop - (2f * DeckFrac - 1f) * ModelHeight * 0.5f;

    // Stone tone for the procedural step flights, matched to the masonry of the generated span.
    static readonly Vector4 StepColor = new Vector4(0.60f, 0.58f, 0.55f, 1f);

    internal Season.Controls.Model model;

    Mesh3D stairs;

    // Local +X of the placed controls maps onto the span direction, matching the Model convention
    // (CreateRotationY sends local +X to (cos, 0, -sin)), so the walk axis lines up with the arch.
    readonly float yaw = MathF.Atan2(-SpanZ, SpanX);

    internal Bridge()
    {
        model = new Season.Controls.Model()
        {
            Name = BridgeGlbPath,
            PosX = CenterX,
            PosY = ModelCenterY,
            PosZ = CenterZ,
            Width = ModelLength,
            Height = ModelHeight,
            Depth = ModelWidth,
            Rotation = yaw,
        };
        AddControl(model);

        stairs = BuildStairs();
        AddControl(stairs);
    }

    /// <summary>
    /// Walkable height (world Y) under a movement footprint: the higher of the flat deck and the two step
    /// flights, sampled at the centre plus four corners. Zero off the bridge. Same five-point contract as
    /// <see cref="River.FloorHeightUnder"/> and <see cref="Tower.FloorHeightAtWorld"/>.
    /// </summary>
    internal float FloorHeightUnder(in Vector3 center, in Vector3 half)
    {
        float h = HeightAt(center.X, center.Z);
        h = MathF.Max(h, HeightAt(center.X - half.X, center.Z - half.Z));
        h = MathF.Max(h, HeightAt(center.X - half.X, center.Z + half.Z));
        h = MathF.Max(h, HeightAt(center.X + half.X, center.Z - half.Z));
        h = MathF.Max(h, HeightAt(center.X + half.X, center.Z + half.Z));
        return h;
    }

    /// <summary>
    /// Deck or step-top height at one world XZ point. The across-deck coordinate must stay within the walkable
    /// width; along the span the profile is flat over the deck and steps down through the flight, the top of
    /// step i being (i + 1) * Riser, exactly like Tower.FloorHeightAtWorld solves its flight from the run
    /// coordinate instead of searching boxes.
    /// </summary>
    float HeightAt(float worldX, float worldZ)
    {
        float dx = worldX - CenterX, dz = worldZ - CenterZ;
        float u = dx * SpanX + dz * SpanZ;   // along the walk axis
        float v = dx * FlowX + dz * FlowZ;   // across the deck

        if (MathF.Abs(v) > HalfWidth)
            return 0f;

        float au = MathF.Abs(u);
        if (au <= HalfSpan)
            return DeckTop;

        if (au <= HalfSpan + FlightRun)
        {
            // j treads out from the deck face: the flight climbs as the player approaches the abutment,
            // so the tread nearest the deck stands at full deck height and the bottom one at a single riser.
            int j = (int)((au - HalfSpan) / Tread);
            return (StepCount - j) * Riser;
        }

        return 0f;
    }

    /// <summary>
    /// Two straight-run stone flights, one leaning against each abutment, that carry the player from the grass
    /// up to the deck. Built in bridge-local meters (X along the span, Y up, Z across) with the crossing centre
    /// at the origin, every step a solid box from the ground up to its tread like Tower.BuildFlight, so the
    /// flight reads as a stone mass against the abutment rather than floating treads. A 13-step flight is 13
    /// boxes = 156 verts, so one batched Surface is enough to keep it a single draw call. Never registered as
    /// an obstacle: they are walkable floors, and the deck edges are policed by the water test instead of
    /// geometry.
    /// </summary>
    Mesh3D BuildStairs()
    {
        var verts = new List<Vertex>();
        var inds = new List<int>();

        AddFlight(verts, inds, +1f);
        AddFlight(verts, inds, -1f);

        // The pair is symmetric in X and Z and its Y bounds are already [0, DeckTop], so the bounding-box
        // centre - the placement anchor - is exactly the crossing centre shifted up by half the deck height.
        float cy = DeckTop * 0.5f;
        var mesh = new Mesh3D
        {
            Name = "BridgeStairs",
            CullingEnabled = false,
            PosX = CenterX,
            PosY = cy,
            PosZ = CenterZ,
            Width = 2f * (HalfSpan + FlightRun),
            Height = DeckTop,
            Depth = 2f * HalfWidth,
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw),
            GiAlbedo = new Vector3(StepColor.X, StepColor.Y, StepColor.Z),
        };

        mesh.Surfaces.Add(new Surface
        {
            Vertices = verts.ToArray(),
            Indices = inds.ConvertAll(x => (ushort)x).ToArray(),
            BaseColor = StepColor,
            MetallicFactor = 0f,
            RoughnessFactor = 0.85f,
            Unlit = false,
        });

        return mesh;
    }

    /// <summary>
    /// Emit one straight-run step flight on the side given by <paramref name="side"/> (+1 toward +span, -1
    /// toward -span). Step i has its tread top at (i + 1) * Riser and sits i treads short of the grass end,
    /// so the tread against the abutment face is the full deck height and the bottom step's riser is the
    /// mouth - the same climbing order as Tower.BuildFlight, whose steps grow toward the deck.
    /// </summary>
    static void AddFlight(List<Vertex> verts, List<int> inds, float side)
    {
        float wl = -HalfWidth, wr = HalfWidth;
        for (int i = 0; i < StepCount; i++)
        {
            // Band of this step along the span axis: measured outward from the deck face, the tallest step
            // (i = StepCount - 1) occupies the first tread, the bottom step the last one.
            float a = side * (HalfSpan + (StepCount - 1 - i) * Tread);
            float b = side * (HalfSpan + (StepCount - i) * Tread);
            float top = (i + 1) * Riser;

            // Solid box from the grass up to the tread, spanning the deck width.
            Box(verts, inds, MathF.Min(a, b), 0f, wl, MathF.Max(a, b), top, wr);
        }
    }

    /// <summary>Emit one axis-aligned box as six shaded quads with outward normals.</summary>
    static void Box(List<Vertex> verts, List<int> inds, float x0, float y0, float z0, float x1, float y1, float z1)
    {
        Quad(verts, inds, new Vector3(x0, y1, z0), new Vector3(x0, y1, z1), new Vector3(x1, y1, z1), new Vector3(x1, y1, z0), Vector3.UnitY);   // tread top
        Quad(verts, inds, new Vector3(x0, y0, z0), new Vector3(x1, y0, z0), new Vector3(x1, y0, z1), new Vector3(x0, y0, z1), -Vector3.UnitY);   // underside
        Quad(verts, inds, new Vector3(x1, y0, z0), new Vector3(x1, y1, z0), new Vector3(x1, y1, z1), new Vector3(x1, y0, z1), Vector3.UnitX);    // +X riser face
        Quad(verts, inds, new Vector3(x0, y0, z0), new Vector3(x0, y0, z1), new Vector3(x0, y1, z1), new Vector3(x0, y1, z0), -Vector3.UnitX);   // -X face
        Quad(verts, inds, new Vector3(x0, y0, z1), new Vector3(x1, y0, z1), new Vector3(x1, y1, z1), new Vector3(x0, y1, z1), Vector3.UnitZ);    // +Z cheek
        Quad(verts, inds, new Vector3(x0, y0, z0), new Vector3(x0, y1, z0), new Vector3(x1, y1, z0), new Vector3(x1, y0, z0), -Vector3.UnitZ);   // -Z cheek
    }

    static void Quad(List<Vertex> verts, List<int> inds, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n)
    {
        int i = verts.Count;
        verts.Add(MakeVertex(a, n));
        verts.Add(MakeVertex(b, n));
        verts.Add(MakeVertex(c, n));
        verts.Add(MakeVertex(d, n));
        inds.Add(i); inds.Add(i + 1); inds.Add(i + 2);
        inds.Add(i); inds.Add(i + 2); inds.Add(i + 3);
    }

    static Vertex MakeVertex(Vector3 pos, Vector3 normal)
    {
        return new Vertex
        {
            Position = pos,
            TexCoord = new Vector2(pos.X, pos.Z),
            Normal = normal,
            Tangent = new Vector4(1, 0, 0, 1),
            Joints = Vector4.Zero,
            Weights = Vector4.Zero,
        };
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        if (model.Update(time))
            result = true;

        if (stairs.Update(time))
            result = true;

        return result;
    }
}
