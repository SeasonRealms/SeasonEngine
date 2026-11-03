// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Engine.Panels;

/// <summary>
/// One selectable stretch of the river: the procedural bed and levee, the flowing water band on top of it, and the
/// textured bank pieces instanced along both sides of it.
///
/// <para><b>The channel is a raised levee, not a cut.</b> Eight points per row, from the left toe to the right toe,
/// described in the row's lateral axis L (<see cref="WaterRibbonMath.Lateral"/>) and its height relative to that row's
/// water surface:</para>
/// <code>
///   toe       crest    crest   bedEdge    bed    bedEdge   crest   crest      toe
///  (y)   -0.49   +0.32    +0.32    -0.22   -0.22    -0.22    +0.32   +0.32    -0.49
///  (L)  -(hw+1.25) -(hw+0.40) -hw  -(hw-0.35)  0  +(hw-0.35)  +hw  +(hw+0.40) +(hw+1.25)
/// </code>
/// <para>Seven bands come out of it: outer slope, crest, inner face, bed, inner face, crest, outer slope. The inner faces
/// and the bed are the wet channel and share one <see cref="Surface"/> (gravel); the crests and outer slopes are the dry
/// levee and share the other (grass over soil). Both carry a procedural noise texture generated here (the same in-memory
/// path <see cref="Ground.BuildRoad"/> uses) so the river needs no authored texture on disk.</para>
///
/// <para><b>Why an instanced model sits on top of that:</b> the procedural levee is deliberately narrow and exact - it is
/// the only geometry that has to stay watertight along a curve - while the visible mass of the bank is one SeasonThreeD
/// piece from <c>Assets/RiverBank.glb</c>, repeated along both sides by <see cref="AddDikes"/>. The piece is mirrored to
/// the far side by turning it half a turn about Y (never a negative scale: that flips the winding and turns it inside
/// out), which is why only one bank exists on disk. Its inner edge tucks behind the procedural crest, so the two never
/// have to agree on a silhouette and a gap between neighbouring pieces cannot leak water.</para>
///
/// <para><b>Why the waterline needs no authoring:</b> the water band spans exactly ±hw, i.e. exactly the crest shoulder
/// on the water side. The inner face then rises from the bed to +0.32 at ±hw, so the water plane cuts that face a few
/// centimetres inside the shoulder. Everything between that intersection and ±hw lies under the face and is removed by
/// the ribbon's software depth test, so the visible shoreline is the intersection of the water plane with the terrain -
/// not the edge of a polygon. That is what keeps it from reading as a decal with a hard cut.</para>
///
/// <para><b>Why this is a <see cref="Mesh3D"/>:</b> the whole shape is authored in world coordinates, so
/// <see cref="Mesh3DBase.Load"/> establishes the bounding box and <see cref="Load"/> then sets Pos to the anchor, which
/// makes <see cref="Mesh3DBase.BuildWorldMatrix"/> the identity matrix. From that point the property panel's
/// PosX/PosY/PosZ, Width/Height/Depth and Rotation move the banks, and <see cref="Water"/> follows through its
/// <see cref="WaterRibbon.Placement"/> delegate - one matrix, evaluated per frame, so the water can never lag a frame behind
/// its own channel. Segments are picked, dragged and outlined exactly like a house or the waterfall's proxy.</para>
///
/// <para>Highlight must be <see cref="Season.Panels.HighlightStyle.Wireframe"/>: a segment is a long strip, so the world
/// axis-aligned bounds box would balloon out to a rectangle covering the whole course as soon as the segment is rotated.</para>
/// </summary>
internal sealed class RiverSegment : Mesh3D
{
    // -- Cross-section, in meters, relative to the row's water surface. --

    /// <summary>Horizontal run of the levee crest, from the water-side shoulder out to the outer shoulder.</summary>
    const float CrestWidth = 0.40f;

    /// <summary>Crest height above the water surface: 32 cm of freeboard over a 22 cm channel.</summary>
    const float CrestRise = 0.32f;

    /// <summary>Horizontal run of the outer slope, from the crest shoulder down to the buried toe.</summary>
    const float SkirtWidth = 0.85f;

    /// <summary>How far below the grass the outer toe is buried, so the levee has no floating edge.</summary>
    const float BuriedDepth = 0.15f;

    /// <summary>Horizontal run of the steep inner face, from the crest down to the edge of the bed.</summary>
    const float WallWidth = 0.35f;

    /// <summary>
    /// Water depth over the bed. Together with the course height this is what puts the bed 12 cm above the grass, i.e.
    /// the raised bed the levee plan asks for, instead of the 2 cm skim it was sitting at before.
    /// </summary>
    const float WaterDepth = 0.22f;

    /// <summary>
    /// A row whose water surface is below this is past the shore lip: the bank and bed stop there and only the water
    /// ribbon continues. Two centimetres of slack absorbs the interpolation between the last on-land point and the first
    /// falling one.
    /// </summary>
    const float LandLimit = -0.02f;

    // -- Bank pieces (Assets/RiverBank.glb), one pair per DikeSpacing metres. --

    /// <summary>World size of one piece: across the bank, up, and along the flow. Stretched from the asset's own aspect.</summary>
    const float DikeWidth = 1.55f;
    const float DikeHeight = 0.86f;
    const float DikeLength = 4.0f;

    /// <summary>
    /// Center-to-center spacing along the course, shorter than <see cref="DikeLength"/> on purpose: neighbours overlap, so
    /// the outside of a bend never opens a wedge between two pieces.
    /// </summary>
    const float DikeSpacing = 3.20f;

    /// <summary>How far a piece is buried, measured from the grass to its anchor (the template-box center).</summary>
    const float DikeSink = 0.10f;

    /// <summary>How far the piece's inner edge sits behind the crest shoulder, i.e. how much it overlaps the levee.</summary>
    const float DikeInset = 0.06f;

    /// <summary>
    /// Half-turn (0 or pi) that decides which face of the piece looks at the water. The asset is asymmetric - one long
    /// grass slope against one cut soil face - and which of the two ends up on the water side depends on how it was
    /// modelled, so this is the single knob to turn if a regenerated piece comes back with its cut face to the river.
    /// </summary>
    const float DikeFacing = 0f;

    // -- The road crossing. --

    /// <summary>Half width of the flat apron the levee opens into where the road crosses, measured along the course.</summary>
    const float FordHalf = 3.0f;

    /// <summary>Length of the ramp from that apron back to the full levee on each side.</summary>
    const float FordFeather = 2.0f;

    const int BandCount = 7;
    const int SectionPoints = 8;
    const int TextureSize = 512;

    // Generated once and shared by every segment: the noise is deterministic, and one identical 1 MB upload per stretch
    // would otherwise be built from one identical byte array each time.
    static byte[] bankPixels;
    static byte[] bedPixels;

    readonly WaterRibbonPoint[] _points;

    // One axis-aligned rectangle per row pair, in author space, for the movement footprint test.
    readonly List<WaterSlab> _slabs = new();

    // One entry per bank piece this stretch owns, in course order: filled by BuildSurface, instantiated by AddDikes.
    readonly List<DikeSlot> _slots = new();

    // The pieces' transforms on the shared batch, parallel to _slots. Null until the template bounds have arrived.
    MeshInstanceTransform[] _dikeInstances;

    // Correction that brings the asset's long axis onto local +Z, decided once from the template's aspect.
    float _dikeYaw;

    // Target sizes along the template's own X and Z. Width and Depth scale template axes, not world ones, so these two
    // swap with the asset's long axis in AddDikes.
    float _dikeSpanX = DikeWidth;
    float _dikeSpanZ = DikeLength;

    // The segment matrix the instances were last placed with, so an untouched stretch costs one matrix compare per frame.
    Matrix4x4 _dikeWorld;

    // What SyncDikes placed per piece, parallel to _slots. The property panel writes into instances in place, so the
    // difference between an instance and its snapshot here is the hand-made nudge - see SyncDikes for how it survives.
    DikePose[] _dikePoses;

    /// <summary>
    /// The water actually drawn for this stretch. Registered into the scene by <see cref="River"/>, not by the control:
    /// the backend only knows about <see cref="Season.Rendering.WaterRibbon"/> data.
    /// </summary>
    internal WaterRibbon Water { get; }

    /// <param name="name">Control name, also the row label in the property panel.</param>
    /// <param name="points">This stretch's control points, shared verbatim with its neighbours at both joints.</param>
    /// <param name="phase">Arc length in meters from the river source to <paramref name="points"/>[0], see <see cref="WaterRibbon.Phase"/>.</param>
    /// <param name="sortOrder">Draw order among ribbons; steady and increasing downstream so the blend order never flips.</param>
    internal RiverSegment(string name, WaterRibbonPoint[] points, float phase, float sortOrder)
    {
        Name = name;
        _points = points;

        // See the class header: a long strip must not use the world bounds box.
        Highlight = new Highlight { Style = HighlightStyle.Wireframe, EdgeWidth = 0.004f };

        // The banks are terrain: they receive shadows but never cast them. A 13 m wide, 100 m long sheet would otherwise
        // become one GI proxy enclosing a large part of the SDF volume, exactly the reason Ground clears this on the grass
        // and the road.
        CastShadows = false;
        CullingEnabled = false;

        Water = new WaterRibbon
        {
            Phase = phase,
            SortOrder = sortOrder,

            // Every segment shares the speed. Together with the accumulated phase this is the whole trick that keeps the
            // current continuous across a joint: the shader samples at "phase + arc - time * speed", so two neighbours
            // read the same world distance on every frame and the texture never jumps at the seam.
            FlowSpeed = 0.75f,

            // The band is flat, so columns only interpolate the texture; three is plenty for a channel this wide.
            Columns = 3,

            // Three rows per control point keeps a bend's shoreline off its own chord.
            Subdiv = 3,
        };
        Water.Points.AddRange(points);

        // The texture names stay at their defaults ("Assets/WaterfallMask.png" and friends), which the waterfall already
        // registers; River.Textures hands the same three names to the App so a scene without a waterfall still works.
        Water.Placement = () => BuildWorldMatrix();
        Water.EnabledProbe = () => Enable && Alpha > 0f;

        BuildSurface();
    }

    /// <summary>
    /// Builds the mesh now, then lets the base class aggregate the bounding box and finally pins Pos onto the anchor so the
    /// world matrix collapses to the identity. The water ribbon needs no separate setup: its Placement delegate evaluates
    /// <see cref="Mesh3DBase.BuildWorldMatrix"/> every frame, and that method returns the identity until the panel moves
    /// this segment.
    /// </summary>
    public override async Task<bool> Load()
    {
        bool result = await base.Load();

        // OnBoundsEstablished has already settled Width/Height/Depth to the local size, so ComputedScale is 1 and the
        // matrix is identity; only the translation has to be pinned.
        if (LocalBoundsRaw.Extents != Vector3.Zero)
        {
            PosX = AnchorLocal.X;
            PosY = AnchorLocal.Y;
            PosZ = AnchorLocal.Z;
        }

        return result;
    }

    /// <summary>
    /// Overlap between a footprint (world center plus half extents) and this stretch's water, measured as "how many of the
    /// four footprint corners are over water" (0..4).
    ///
    /// <para>Same contract as <see cref="Pond.WaterOverlap"/>, and deliberately a coarse count rather than an exact area:
    /// <see cref="Management.PlayerCollider"/> only compares the target against the starting overlap, so what it needs is
    /// something that grows monotonically as the player walks toward the water and shrinks as they back out. A count does
    /// that with a footprint test per corner and no polygon clipping, and the channel is at least 3.8 m wide, far wider than
    /// the player, so the count cannot be fooled by slipping between samples.</para>
    ///
    /// <para>The corners are transformed into this segment's author space, which is what makes a moved or rotated segment
    /// keep blocking the right patch of ground.</para>
    /// </summary>
    internal float WaterOverlap(in Vector3 center, in Vector3 half)
    {
        if (_slabs.Count == 0) return 0f;
        if (!Matrix4x4.Invert(BuildWorldMatrix(), out var inverse)) return 0f;

        float overlap = 0f;
        for (int corner = 0; corner < 4; corner++)
        {
            float sx = (corner & 1) == 0 ? -1f : 1f;
            float sz = (corner & 2) == 0 ? -1f : 1f;
            var point = Vector3.Transform(
                new Vector3(center.X + sx * half.X, center.Y, center.Z + sz * half.Z), inverse);

            for (int i = 0; i < _slabs.Count; i++)
            {
                var slab = _slabs[i];
                var delta = point - slab.Center;
                if (MathF.Abs(Vector3.Dot(delta, slab.Lateral)) <= slab.HalfWidth
                    && MathF.Abs(Vector3.Dot(delta, slab.Flow)) <= slab.HalfLength)
                {
                    overlap += 1f;
                    break;
                }
            }
        }

        return overlap;
    }

    /// <summary>
    /// Expands the control points into rows with <see cref="WaterRibbonMath.SampleRow"/> - the same CPU-side curve the
    /// ribbon's vertex shader evaluates - and sweeps the eight-point section along them. Using the shared sampler rather than
    /// the raw control points is what keeps the banks on the water: a bank built from chords would let the band poke out of
    /// the outside of every bend, and so would a collision footprint built from them.
    /// </summary>
    void BuildSurface()
    {
        Surfaces.Clear();
        _slabs.Clear();

        int count = Math.Clamp(_points.Length, 2, WaterRibbonMath.MaxControlPoints);
        int subdiv = Math.Clamp(Water.Subdiv, 1, WaterRibbonMath.MaxSubdiv);
        int rows = (count - 1) * subdiv + 1;

        var sampled = new WaterRibbonRow[rows];
        for (int row = 0; row < rows; row++)
            sampled[row] = WaterRibbonMath.SampleRow(_points, row / (float)subdiv);

        // The channel only exists where the water is still on land. Past the shore lip the ground ends and the water
        // leaves it, so the bank and bed mesh stops at the last on-land row and only the ribbon continues. That is what
        // makes the mouth a cascade instead of a flume hanging off the cliff, and it self-hides: the rows just past the
        // lip are already below the grass, so the opaque ground covers them until the water is past the island edge.
        int landRows = rows;
        while (landRows > 1 && sampled[landRows - 1].Center.Y < LandLimit)
            landRows--;

        // Eight section points per row plus one normal per band per row: built once, then the cell loop is a pure copy.
        var section = new Vector3[rows * SectionPoints];
        var normals = new Vector3[rows * BandCount];
        var lateral = new float[SectionPoints];
        var height = new float[SectionPoints];

        for (int row = 0; row < landRows; row++)
        {
            var line = sampled[row];
            var side = WaterRibbonMath.Lateral(line.Flow);
            float halfWidth = line.HalfWidth;
            float bedEdge = MathF.Max(halfWidth - WallWidth, 0.05f);

            // The road crosses the channel at x = 0. Standing a 66 cm berm across the asphalt would read as a wall
            // rather than a crossing, so the levee opens into a flat apron there: the crest drops below the water and
            // the outer slope flattens out, and the water spills across the road as a widened ford. The same factor
            // skips the bank pieces and widens the collision footprint further down.
            float ford = FordFactor(line.Center.X);
            float crest = CrestRise - ford * (CrestRise + WaterDepth + 0.06f);
            float skirt = SkirtWidth + ford * 1.6f;

            // The toe always ends the same distance under the grass, whatever this row's water height is, so the last
            // rows before the lip cannot leave the skirt hanging in the air.
            float toe = -(line.Center.Y + BuriedDepth);

            lateral[0] = -(halfWidth + CrestWidth + skirt); lateral[1] = -(halfWidth + CrestWidth);
            lateral[2] = -halfWidth;                       lateral[3] = -bedEdge;
            lateral[4] = bedEdge;                          lateral[5] = halfWidth;
            lateral[6] = halfWidth + CrestWidth;           lateral[7] = halfWidth + CrestWidth + skirt;

            height[0] = toe;           height[1] = crest;
            height[2] = crest;         height[3] = -WaterDepth;
            height[4] = -WaterDepth;   height[5] = crest;
            height[6] = crest;         height[7] = toe;

            for (int point = 0; point < SectionPoints; point++)
                section[row * SectionPoints + point] = line.Center + side * lateral[point] + Vector3.UnitY * height[point];

            for (int band = 0; band < BandCount; band++)
            {
                // Profile tangent (dl, dy) rotated 90 degrees in the (L, Y) plane gives (-dy, dl). That single expression
                // covers every band with the correct sign: +Y on the bed, up-and-outward on the banks, up-and-inward on
                // the two inner walls.
                float dl = lateral[band + 1] - lateral[band];
                float dy = height[band + 1] - height[band];
                normals[row * BandCount + band] = Vector3.Normalize(side * -dy + Vector3.UnitY * dl);
            }
        }

        var bankVertices = new List<Vertex>();
        var bankIndices = new List<ushort>();
        var bedVertices = new List<Vertex>();
        var bedIndices = new List<ushort>();

        for (int row = 0; row < landRows - 1; row++)
        {
            for (int band = 0; band < BandCount; band++)
            {
                // Outer slopes and crests vs. inner faces and bed: two materials, so two Surfaces.
                bool isBank = band <= 1 || band >= BandCount - 2;
                var vertices = isBank ? bankVertices : bedVertices;
                var indices = isBank ? bankIndices : bedIndices;

                int first = vertices.Count;
                var low = normals[row * BandCount + band];
                var high = normals[(row + 1) * BandCount + band];

                int a = row * SectionPoints + band;
                int b = (row + 1) * SectionPoints + band;

                // Same four-vertex cell as Ground.BuildRoad: u runs across the band, v along the flow, and the
                // {2,3,1,2,1,0} winding points the front face up. Each cell owns its own four vertices so the UVs can tile
                // in [0,1] per cell - the PBR base color sampler is Clamp, so UV outside [0,1] would smear the edge
                // texels instead of repeating.
                vertices.Add(MakeVertex(section[a], new Vector2(0f, 0f), low));
                vertices.Add(MakeVertex(section[a + 1], new Vector2(1f, 0f), low));
                vertices.Add(MakeVertex(section[b], new Vector2(0f, 1f), high));
                vertices.Add(MakeVertex(section[b + 1], new Vector2(1f, 1f), high));

                indices.Add((ushort)(first + 2));
                indices.Add((ushort)(first + 3));
                indices.Add((ushort)(first + 1));
                indices.Add((ushort)(first + 2));
                indices.Add((ushort)(first + 1));
                indices.Add((ushort)first);
            }
        }

        if (bankVertices.Count > 0)
        {
            bankPixels ??= GenerateBankPixels(TextureSize);
            Surfaces.Add(new Surface
            {
                Vertices = bankVertices.ToArray(),
                Indices = bankIndices.ToArray(),
                BaseColor = Vector4.One,
                MetallicFactor = 0f,
                RoughnessFactor = 0.95f,
                Unlit = false,
                Mode = SurfaceBlendMode.Opaque,
                DoubleSided = true,
                TextureOverride = TextureUpdateSource.FromImage(new NativeImageData(TextureSize, TextureSize, bankPixels)),
            });
        }

        if (bedVertices.Count > 0)
        {
            bedPixels ??= GenerateBedPixels(TextureSize);
            Surfaces.Add(new Surface
            {
                Vertices = bedVertices.ToArray(),
                Indices = bedIndices.ToArray(),
                BaseColor = Vector4.One,
                MetallicFactor = 0f,
                // Wet gravel: rougher than the pond's polished stone but visibly smoother than the grass bank.
                RoughnessFactor = 0.72f,
                Unlit = false,
                Mode = SurfaceBlendMode.Opaque,
                DoubleSided = true,
                TextureOverride = TextureUpdateSource.FromImage(new NativeImageData(TextureSize, TextureSize, bedPixels)),
            });
        }

        // Approximate albedo for the DDGI proxy, so the banks do not bounce back as neutral grey.
        GiAlbedo = new Vector3(0.22f, 0.26f, 0.16f);

        BuildSlabs(sampled, landRows);
        BuildDikeSlots(sampled, landRows);
    }

    /// <summary>
    /// One rectangle per row pair, covering the water band's <b>maximum</b> half width of the two rows plus whatever
    /// stands on the bank - the levee's toe, or the outer edge of the bank pieces where they are laid - and half the
    /// chord length along the flow. Widening past the water is deliberate: the levee is a 66 cm physical barrier and the
    /// pieces are another 76 cm of solid geometry, so the player stops outside both instead of walking into them and only
    /// then meeting the water. Overshooting by a few centimetres is harmless, because the corners this samples are
    /// already a footprint radius away from the body.
    /// </summary>
    void BuildSlabs(WaterRibbonRow[] sampled, int rows)
    {
        for (int row = 0; row < rows - 1; row++)
        {
            var start = sampled[row];
            var end = sampled[row + 1];

            var along = end.Center - start.Center;
            along.Y = 0f;
            float length = along.Length();
            if (length < 1e-4f) continue;

            var flow = along / length;

            // How far out the stretch blocks the player. Two things have to be inside the footprint: the levee's outer
            // toe, and the bank pieces standing on the outer slope - they are visual only, so a footprint that stopped
            // at the toe would let the player walk straight into a 76 cm bank. At the ford neither exists: there the
            // levee is flat and the road crossing has to stay walkable, which is what the extra ramp width is for.
            float ford = FordFactor(start.Center.X);
            float levee = CrestWidth + SkirtWidth + ford * 1.6f;
            float dike = (1f - ford) * (CrestWidth - DikeInset + DikeWidth);

            _slabs.Add(new WaterSlab(
                (start.Center + end.Center) * 0.5f,
                WaterRibbonMath.Lateral(flow),
                flow,
                MathF.Max(start.HalfWidth, end.HalfWidth) + MathF.Max(levee, dike),
                length * 0.5f));
        }
    }

    /// <summary>
    /// Walks the sampled rows by arc length and drops one bank piece every <see cref="DikeSpacing"/> metres on each side.
    /// Walking the curve rather than stepping control points is what keeps the spacing even through a bend and puts every
    /// piece's heading on the local tangent - that per-piece rotation is the whole reason the bank can follow the river at
    /// all.
    ///
    /// The slots stay in author space until <see cref="AddDikes"/> runs, because the shared template's bounds are what
    /// decide which way the asset's long axis points.
    /// </summary>
    void BuildDikeSlots(WaterRibbonRow[] sampled, int rows)
    {
        _slots.Clear();

        float placement = DikeSpacing * 0.5f;
        float travelled = 0f;
        float placed = float.NegativeInfinity;

        for (int row = 0; row < rows - 1; row++)
        {
            var start = sampled[row];
            var end = sampled[row + 1];

            float chord = Vector3.Distance(start.Center, end.Center);
            if (chord < 1e-4f)
                continue;

            while (placement <= travelled + chord)
            {
                float t = (placement - travelled) / chord;
                var center = Vector3.Lerp(start.Center, end.Center, t);
                float halfWidth = start.HalfWidth + (end.HalfWidth - start.HalfWidth) * t;
                var flow = Flatten(Vector3.Lerp(start.Flow, end.Flow, t));

                if (CanStand(center))
                {
                    AddDikePair(center, flow, halfWidth);
                    placed = placement;
                }

                placement += DikeSpacing;
            }

            travelled += chord;
        }

        // The split table decides where a stretch ends, and that rarely lands on the spacing: the last piece can sit a
        // whole spacing short of the joint, which is far enough to open a gap wider than nothing between two 4 m
        // pieces. Pin one pair on the last row. It cannot coincide with the next stretch's first piece, because that
        // one starts half a spacing in, so the two overlap instead of z-fighting.
        if (travelled - placed > DikeSpacing * 0.5f)
        {
            var tail = sampled[rows - 1];
            if (CanStand(tail.Center))
                AddDikePair(tail.Center, Flatten(tail.Flow), tail.HalfWidth);
        }
    }

    /// <summary>A pair of slots, one per bank, sharing the row's frame. The side sign is the only difference.</summary>
    void AddDikePair(Vector3 center, Vector3 flow, float halfWidth)
    {
        var lateral = WaterRibbonMath.Lateral(flow);
        _slots.Add(new DikeSlot(center, lateral, flow, halfWidth, 1));
        _slots.Add(new DikeSlot(center, lateral, flow, halfWidth, -1));
    }

    /// <summary>No bank past the lip, and none across the ford: there the pieces would stand in the road crossing.</summary>
    static bool CanStand(Vector3 center) => center.Y >= LandLimit && FordFactor(center.X) < 0.001f;

    /// <summary>A row's flow with the vertical part dropped; the sampled tangents carry a slope on the falling rows.</summary>
    static Vector3 Flatten(Vector3 flow)
    {
        var flat = new Vector3(flow.X, 0f, flow.Z);
        return flat.LengthSquared() > 1e-6f ? Vector3.Normalize(flat) : Vector3.UnitZ;
    }

    /// <summary>
    /// Creates this stretch's pieces on the river-wide batch. Nothing is deferred beyond this point except the heading:
    /// across, up and along are fixed constants, so the only thing that has to wait for the template is which of the
    /// asset's axes is the long one.
    /// </summary>
    /// <param name="host">The shared batch. Instance transforms are world-space, so every stretch lands in one draw.</param>
    /// <param name="longAxisIsX">
    /// Which of the asset's horizontal axes carries its length, see <see cref="River.SettleDikes"/>. It decides the
    /// heading correction and, because Width and Depth scale the template's own X and Z, which of the two target sizes
    /// goes where. <c>RiverBank.glb</c> comes back 0.99 x 0.20 x 0.33 m, so its long axis is X.
    /// </param>
    internal void AddDikes(InstancedModel host, bool longAxisIsX)
    {
        _dikeYaw = longAxisIsX ? -MathF.PI * 0.5f : 0f;
        _dikeSpanX = longAxisIsX ? DikeLength : DikeWidth;
        _dikeSpanZ = longAxisIsX ? DikeWidth : DikeLength;
        _dikeInstances = new MeshInstanceTransform[_slots.Count];
        _dikePoses = new DikePose[_slots.Count];

        for (int i = 0; i < _slots.Count; i++)
        {
            // Sizes start at the template-axis targets and are rewritten with the stretch's own scale by SyncDikes
            // below; the names are the part that has to be right here.
            var instance = new MeshInstanceTransform
            {
                Name = $"{Name}.bank{i + 1}",
                Width = _dikeSpanX,
                Height = DikeHeight,
                Depth = _dikeSpanZ,
            };

            _dikeInstances[i] = instance;

            // The starting snapshot mirrors the instance, so the first layout pass reads a zero nudge instead of
            // treating the untouched instance as one the handler dragged.
            _dikePoses[i] = new DikePose(Vector3.Zero, new Vector3(_dikeSpanX, DikeHeight, _dikeSpanZ), 0f);

            host.Instances.Add(instance);
        }

        SyncDikes(force: true);
    }

    /// <summary>
    /// Folds this stretch's current matrix into every piece of it. The stretch is what the property panel moves, scales
    /// and turns, so without this the bank would stay behind while the levee mesh and the water moved under it.
    ///
    /// <see cref="Mesh3DBase.BuildWorldMatrix"/> is the same matrix the levee mesh and the water ribbon read, so there is
    /// one source of truth and nothing to keep in sync. A scaled stretch multiplies the piece sizes rather than skewing
    /// them, which is the one thing this decomposition cannot express - and the panel's Width/Height/Depth are exactly
    /// the "make the banks further apart or the water wider" control the plan asked for.
    ///
    /// <para><b>Hand-made edits win.</b> The pieces are separate pick targets, so the property panel writes straight
    /// into them. Rather than flattening that on the next layout pass, each run compares the instance against the pose
    /// it wrote last time and adds the difference back on top of the freshly derived pose. A piece someone raised,
    /// widened or turned therefore keeps its offset when its stretch is moved - and a stretch nobody touches costs one
    /// matrix compare per frame, because this whole method returns early in that case.</para>
    /// </summary>
    void SyncDikes(bool force = false)
    {
        if (_dikeInstances == null)
            return;

        var world = BuildWorldMatrix();
        if (!force && world == _dikeWorld)
            return;

        if (!Matrix4x4.Decompose(world, out var scale, out var rotation, out _))
            return;

        _dikeWorld = world;

        for (int i = 0; i < _slots.Count; i++)
        {
            var slot = _slots[i];
            var instance = _dikeInstances[i];
            var pose = _dikePoses[i];

            // Everything the instance now holds beyond the pose written last time was put there by the handler.
            var shift = new Vector3(
                instance.PosX - pose.Position.X,
                instance.PosY - pose.Position.Y,
                instance.PosZ - pose.Position.Z);
            var stretch = new Vector3(
                instance.Width - pose.Size.X,
                instance.Height - pose.Size.Y,
                instance.Depth - pose.Size.Z);
            float turn = YawOf(instance.Rotation) - pose.Yaw;

            // Distance from the water line to the piece's center: the row's own half width (it grows from 1.9 to 4.3 m
            // down the course, which is what makes the channel widen), the crest, and then half the piece - less the
            // inset that buries its inner edge in the crest shoulder so the two never have to agree on a silhouette.
            float offset = slot.HalfWidth + CrestWidth - DikeInset + DikeWidth * 0.5f;

            var anchor = new Vector3(
                slot.Center.X + slot.Lateral.X * slot.Side * offset,
                DikeHeight * 0.5f - DikeSink,
                slot.Center.Z + slot.Lateral.Z * slot.Side * offset);

            var position = Vector3.Transform(anchor, world) + shift;

            instance.PosX = position.X;
            instance.PosY = position.Y;
            instance.PosZ = position.Z;

            // Floored above zero on purpose: zero means "use the template size" to InstanceComputedScale, and a negative
            // target would mirror the geometry, which flips the winding and turns the piece inside out.
            instance.Width = MathF.Max(0.01f, _dikeSpanX * scale.X + stretch.X);
            instance.Height = MathF.Max(0.01f, DikeHeight * scale.Y + stretch.Y);
            instance.Depth = MathF.Max(0.01f, _dikeSpanZ * scale.Z + stretch.Z);

            // Half a turn about Y is the mirror onto the far bank: the engine never takes a negative scale, which would
            // flip the winding and turn the piece inside out. It swaps the piece's two ends along the flow, which an
            // organic bank segment hides completely because the neighbours overlap either way.
            float heading = MathF.Atan2(slot.Flow.X, slot.Flow.Z) + _dikeYaw + DikeFacing
                          + (slot.Side > 0 ? 0f : MathF.PI) + turn;
            instance.Rotation = Quaternion.Multiply(Quaternion.CreateFromYawPitchRoll(heading, 0f, 0f), rotation);

            // Snapshot is read back from the instance rather than from the values above, so the next comparison is exact
            // even when the stretch carries a pitch or roll that the yaw extraction can only approximate.
            _dikePoses[i] = new DikePose(position, new Vector3(instance.Width, instance.Height, instance.Depth), YawOf(instance.Rotation));
        }
    }

    /// <summary>
    /// Per-frame driver for one stretch: ticks the procedural mesh, then lets the bank pieces follow the matrix the panel
    /// edits. A control does not tick its children, so <see cref="River"/> calls this once per stretch per frame.
    /// </summary>
    internal bool Tick(float time)
    {
        bool result = Update(time);
        SyncDikes();
        return result;
    }

    static Vertex MakeVertex(Vector3 position, Vector2 uv, Vector3 normal) => new()
    {
        Position = position,
        TexCoord = uv,
        Normal = normal,
        // Unused by renderMode 0, but the vertex layout has a slot for it.
        Tangent = new Vector4(1f, 0f, 0f, 1f),
        Joints = Vector4.Zero,
        Weights = Vector4.Zero,
    };

    /// <summary>One collision footprint in author space: the midpoint of a row pair plus the frame its rectangle is built in.</summary>
    readonly record struct WaterSlab(Vector3 Center, Vector3 Lateral, Vector3 Flow, float HalfWidth, float HalfLength);

    /// <summary>
    /// A piece's transform as the course asked for it: anchor, template-axis size and heading. Compared against the live
    /// instance it tells the hand-made part from the derived part; see <see cref="SyncDikes"/>.
    /// </summary>
    readonly record struct DikePose(Vector3 Position, Vector3 Size, float Yaw);

    /// <summary>
    /// Yaw of a rotation that may also carry a pitch or roll, read the same way the property panel's Rotation row does:
    /// turn local +X and take atan2(-z, x). Exact for the pure Y turns the panel writes.
    /// </summary>
    static float YawOf(Quaternion rotation)
    {
        var x = Vector3.TransformNormal(Vector3.UnitX, Matrix4x4.CreateFromQuaternion(rotation));
        return MathF.Atan2(-x.Z, x.X);
    }

    /// <summary>
    /// Where one bank piece goes, in author space: the row's center point on the water line, the frame the piece stands
    /// in, the row's half width, and which bank (<see cref="Side"/> = +1 is the +Lateral side). Kept in author space because
    /// the piece's heading has to wait for the template's bounds; see <see cref="SyncDikes"/>.
    /// </summary>
    readonly record struct DikeSlot(Vector3 Center, Vector3 Lateral, Vector3 Flow, float HalfWidth, int Side);

    /// <summary>
    /// How far the levee has opened into a ford at this world X: 1 across the road corridor, 0 on the full levee, and a
    /// smooth ramp in between. The road runs along Z at x = 0, so the factor depends on X alone.
    /// </summary>
    static float FordFactor(float x)
    {
        float distance = MathF.Abs(x);
        if (distance <= FordHalf) return 1f;
        if (distance >= FordHalf + FordFeather) return 0f;

        float t = (distance - FordHalf) / FordFeather;
        return 1f - t * t * (3f - 2f * t);
    }

    // ── Procedural ground textures ───────────────────────────────────────────────────────────────────────────────────
    //
    // Both are deterministic value-noise images generated in memory and handed to the backend as pixels, the same path
    // Ground.BuildRoad uses for the asphalt. They are colours, not lighting: the surfaces stay Unlit = false and get their
    // shading from the sun, the CSM shadows and the ambient term like any other PBR mesh.

    /// <summary>Damp grass bank: a dark green base with moss and bare-earth patches, plus fine grain so it does not look flat.</summary>
    static byte[] GenerateBankPixels(int size)
    {
        var pixels = new byte[size * size * 4];
        const uint seed = 0x52495645u;   // "RIVE"

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float broad = 0.5f * Noise(x, y, 64, seed) + 0.3f * Noise(x, y, 24, seed ^ 0x9E3779B9u)
                            + 0.2f * Noise(x, y, 10, seed ^ 0x85EBCA77u);
                float grain = Noise(x, y, 3, seed ^ 0xC2B2AE35u);

                // Grass to bare earth: the low-frequency term alone decides the patch mix.
                float earth = Saturate((broad - 0.45f) * 2.2f);
                var grass = new Vector3(0.19f, 0.29f, 0.13f) * (0.85f + 0.5f * grain);
                var dirt = new Vector3(0.34f, 0.28f, 0.19f) * (0.85f + 0.4f * grain);
                var color = Vector3.Lerp(grass, dirt, earth);

                // A few lighter pebbles and a darker wet line right at the water side keep the slope readable up close.
                float pebble = Noise(x, y, 6, seed ^ 0x27D4EB2Du);
                if (pebble > 0.80f) color = Vector3.Lerp(color, new Vector3(0.52f, 0.50f, 0.45f), (pebble - 0.80f) * 4f);

                int o = (y * size + x) * 4;
                pixels[o] = ToByte(color.X);
                pixels[o + 1] = ToByte(color.Y);
                pixels[o + 2] = ToByte(color.Z);
                pixels[o + 3] = 255;
            }
        }

        return pixels;
    }

    /// <summary>Wet gravel bed: grey-brown sand with darker wet stones and a scatter of bright pebbles showing through the film.</summary>
    static byte[] GenerateBedPixels(int size)
    {
        var pixels = new byte[size * size * 4];
        const uint seed = 0x42454421u;   // "BED!"

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float broad = 0.6f * Noise(x, y, 48, seed) + 0.4f * Noise(x, y, 16, seed ^ 0x9E3779B9u);
                float grain = Noise(x, y, 4, seed ^ 0x85EBCA77u);

                // Silt at the two banks, coarser gravel down the middle: the gravel is what reads as a bed under water.
                float silt = Noise(x, y, 128, seed ^ 0xC2B2AE35u);
                var sand = new Vector3(0.32f, 0.29f, 0.24f) * (0.82f + 0.45f * broad);
                var gravel = new Vector3(0.24f, 0.23f, 0.20f) * (0.80f + 0.55f * grain);
                var color = Vector3.Lerp(gravel, sand, silt);

                // Stones: a second noise at a stone-sized lattice, raised only where it crosses a threshold.
                float stone = Noise(x, y, 11, seed ^ 0x165667B1u);
                if (stone > 0.72f) color = Vector3.Lerp(color, new Vector3(0.46f, 0.45f, 0.42f), (stone - 0.72f) * 3f);

                int o = (y * size + x) * 4;
                pixels[o] = ToByte(color.X);
                pixels[o + 1] = ToByte(color.Y);
                pixels[o + 2] = ToByte(color.Z);
                pixels[o + 3] = 255;
            }
        }

        return pixels;
    }

    static byte ToByte(float value) => (byte)MathF.Round(Saturate(value) * 255f);

    static float Saturate(float value) => value < 0f ? 0f : value > 1f ? 1f : value;

    /// <summary>Deterministic integer-hash value noise with a smoothstep bilinear blend, same family as Ground's.</summary>
    static float Noise(int px, int py, int lattice, uint seed)
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
}

/// <summary>
/// The island's river: one fixed course from the waterfall pool down to the sea on the Rocks side, cut into six
/// selectable stretches (straight runs and the bends between them) so the layout can still be edited from the property
/// panel without regenerating anything.
///
/// <para><b>Why the course is a constant table:</b> the plan asks for a course fixed once rather than recomputed per launch,
/// because the interesting edits happen in the panel afterwards - drag a stretch, change its width or height. The table
/// below is the whole authoring surface: edit a point or a half width, and the flow directions, the phases, the bends inside
/// each stretch and the collision footprint all follow.</para>
///
/// <para><b>Flow continuity is derived, not authored.</b> Every control point gets one flow direction from the central
/// difference of the <b>global</b> centerline, so a point shared by two stretches carries the same direction on both sides.
/// Each stretch then starts at the arc length its predecessors covered, and all of them share one
/// <see cref="WaterRibbon.FlowSpeed"/>; that is the complete set of conditions the shader needs to make the current pass a
/// joint without a jump in direction, speed or phase.</para>
///
/// <para><b>Geography:</b> the course leaves the pool at the mountain foot in the south-west, runs east while steadily
/// pulling away from the southern range so it never hugs the rock face, stays well south of the pond and the observation
/// tower, and turns south-east over the last stretches to reach the shore in the one gap the east rock line has. The two
/// places it deliberately touches existing geometry are the road at x = 0 (the river is shallower than the asphalt lip,
/// so the crossing reads as a ford) and the shore rocks flanking the mouth (water lapping at a boulder's base is what a
/// river mouth looks like).</para>
/// </summary>
internal sealed class River : Panel
{
    /// <summary>
    /// Water surface height of the channel proper, i.e. how far the raised bed and its levee lift the river above the
    /// grass. The section is built around it: bed 22 cm below, crest 32 cm above, and the levee toe buried 15 cm under
    /// the grass, so the whole channel sits about half a metre proud of the meadow instead of being a painted strip.
    /// </summary>
    internal const float WaterY = 0.34f;

    /// <summary>
    /// Water surface of the waterfall pool, which the first control point matches. The pool is a raised basin, so the
    /// channel takes its first twenty metres to climb the 29 cm to <see cref="WaterY"/>; a step here would be visible as
    /// a shelf across the whole outlet.
    /// </summary>
    const float PoolWaterY = 0.05f;

    /// <summary>
    /// The one generated bank piece, mirrored to the other side and repeated along both banks by the segment instances.
    /// Produced by <c>.dbg/_river_assets</c> (SeasonImage -&gt; SeasonThreeD) and committed with its atlas embedded.
    /// </summary>
    internal const string DikeGlbPath = "Assets/RiverBank.glb";

    /// <summary>
    /// The three ribbon textures, in the order the backend looks them up. Same names as the waterfall uses, so a scene with
    /// both registers them once; <see cref="App"/> still registers them for the river, so a river without a waterfall draws.
    /// </summary>
    internal static readonly string[] Textures =
    {
        "Assets/WaterfallMask.png",
        "Assets/WaterfallNormal.png",
        "Assets/WaterfallFlow.png",
    };

    /// <summary>One point of the fixed course, in world meters.</summary>
    /// <param name="X">Centerline X.</param>
    /// <param name="Z">Centerline Z.</param>
    /// <param name="Y">Water surface at this point; the channel section is built around it.</param>
    /// <param name="HalfWidth">Water half width. The banks add <c>BankWidth</c> on each side.</param>
    readonly record struct CoursePoint(float X, float Z, float Y, float HalfWidth);

    /// <summary>
    /// The fixed course: alternate straight runs and bends, then the mouth. Straight stretches carry a collinear middle
    /// point so the chord between their two ends really is straight; a bend's vertex is the point its two runs share, and
    /// the ribbon's Catmull-Rom rounds it.
    ///
    /// <para>Fifteen of the sixteen control points the shader can hold, six stretches, one ribbon slot each - the whole
    /// course fits inside the per-scene ribbon table with two slots to spare.</para>
    ///
    /// <para><b>The two ends are special.</b> Point 0 sits in the pool's outflow at the pool's own surface, so the two
    /// waters meet flush. Points 13 and 14 are past the island edge (x = 75) and drop below the sea surface: the bank
    /// mesh stops at the lip ("still on land") and only the water continues, so the mouth ends as a cascade into the sea
    /// instead of a cut-off polygon. Points 12-14 also aim the mouth between the shore rocks at z = -38 and z = -54,
    /// which is the only gap in the east rock line wide enough for a river.</para>
    /// </summary>
    static readonly CoursePoint[] Course =
    {
        // Straight 1 - away from the pool. Point 1 is the exact midpoint of 0 and 2.
        new(-64.00f, -63.60f, PoolWaterY, 1.90f),   // 0: in the pool's outflow
        new(-54.00f, -60.15f, 0.195f, 2.00f),       // 1
        new(-44.00f, -56.70f, WaterY, 2.10f),       // 2: bend 1 vertex, the first turn away from the range

        // Bend 1 - the heading eases from 19 to 8 degrees, opening the angle against the southern range.
        new(-26.00f, -51.00f, WaterY, 2.20f),       // 3
        new(-8.00f, -48.60f, WaterY, 2.30f),        // 4: straight 2 begins

        // Straight 2 - crosses the road at x = 0, which reads as a ford. Point 5 is the exact midpoint of 4 and 6.
        new(3.00f, -47.10f, WaterY, 2.40f),         // 5
        new(14.00f, -45.60f, WaterY, 2.50f),        // 6

        // Bend 2 - the meadow bend, straightening the long run toward the east shore.
        new(30.00f, -43.40f, WaterY, 2.80f),        // 7
        new(46.00f, -40.60f, WaterY, 3.10f),        // 8: straight 3 begins

        // Straight 3 - past the observation tower, still more than ten metres off its footprint.
        new(53.00f, -39.60f, WaterY, 3.30f),        // 9: exact midpoint of 8 and 10
        new(60.00f, -38.60f, WaterY, 3.50f),        // 10

        // Bend 3 - the estuary turn, south-east into the gap in the shore rocks.
        new(68.50f, -42.40f, WaterY, 3.90f),        // 11
        new(74.00f, -45.80f, WaterY, 4.30f),        // 12: the lip, just inside the island edge

        // Mouth - over the edge and into the sea. No banks here; see the class header.
        new(76.55f, -47.38f, -1.05f, 4.20f),        // 13
        new(79.10f, -49.00f, -3.25f, 4.00f),        // 14: below the sea surface
    };

    /// <summary>
    /// Split table: first and last course index of each selectable stretch. Neighbouring stretches share their joint
    /// point, which is what makes the water meet the water instead of meeting a gap.
    ///
    /// The mouth is part of the last stretch rather than its own: only one course point of it is on land, so a separate
    /// segment would have a single row of bank - a pick target too small to hit. Keeping the estuary turn in the same
    /// stretch gives it six metres of bank to select and drag while the bank still stops at the lip, because the cut is
    /// per row, not per segment.
    /// </summary>
    static readonly (int First, int Last, string Name)[] Splits =
    {
        (0, 2, "RiverStraightPool"),      // straight: the run away from the waterfall
        (2, 4, "RiverBendWest"),          // bend: the first turn away from the range
        (4, 6, "RiverStraightFord"),      // straight: the ford at x = 0
        (6, 8, "RiverBendMeadow"),        // bend
        (8, 10, "RiverStraightEast"),     // straight: the long run past the tower
        (10, 14, "RiverEstuary"),         // bend into the shore gap, then over the lip into the sea
    };

    /// <summary>Selectable stretches in flow order. Add them all to the picker to edit the course in the property panel.</summary>
    internal RiverSegment[] Segments { get; }

    /// <summary>
    /// The shared bank batch: every piece on both banks of every stretch is one instance here, so the whole river costs
    /// one instanced draw. Filled by <see cref="SettleDikes"/> once the template's bounds are known.
    ///
    /// <para>Registered by <see cref="App"/> as an <see cref="Season.Panels.ObjectPicker.InstancedTargets"/> entry, which
    /// makes each piece its own pick target: clicking a bank selects that piece and the property panel edits its anchor,
    /// size and yaw directly. <see cref="RiverSegment.SyncDikes"/> treats such edits as an offset from the derived
    /// layout, so they survive the next layout pass.</para>
    /// </summary>
    internal InstancedModel Dikes { get; }

    /// <summary>One-shot flag: the template bounds arrive asynchronously with the queued load.</summary>
    bool dikesSettled;

    /// <param name="registry">
    /// Scene ribbon registry the water is registered into. Registration happens here rather than in <see cref="App"/> so the
    /// segment and its water can never end up on different sides of the list.
    /// </param>
    internal River(SceneWaterRibbons registry)
    {
        int points = Course.Length;
        var flowPoints = new WaterRibbonPoint[points];

        for (int i = 0; i < points; i++)
        {
            // Central difference of the global course: endpoints fall back to the one-sided difference. Computing the
            // direction here, once, for the whole course is what makes two neighbours agree at a shared point.
            var before = Course[Math.Max(i - 1, 0)];
            var after = Course[Math.Min(i + 1, points - 1)];
            var direction = new Vector3(after.X - before.X, 0f, after.Z - before.Z);

            flowPoints[i] = new WaterRibbonPoint(
                new Vector3(Course[i].X, Course[i].Y, Course[i].Z),
                Course[i].HalfWidth,
                direction);
        }

        Segments = new RiverSegment[Splits.Length];

        // AddControl queues the template load and takes ownership of the instance list, so the batch is created before
        // the stretches, which only ever append to it later.
        Dikes = new InstancedModel
        {
            Name = "RiverBanks",
            ModelName = DikeGlbPath,
            // These are solid ridges standing half a metre proud of the meadow, so they cast like any other prop.

            // A piece is a 4 m long strip, so its world AABB goes fat as soon as it turns - the same reason the levee
            // stretch uses Wireframe. Instances added later keep their default style and inherit this one through the
            // fallback in ObjectPicker.SetHighlight.
            Highlight = new Highlight { Style = HighlightStyle.Wireframe, EdgeWidth = 0.004f },
        };
        AddControl(Dikes);

        float phase = 0f;
        for (int i = 0; i < Splits.Length; i++)
        {
            var (first, last, name) = Splits[i];

            var slice = new WaterRibbonPoint[last - first + 1];
            Array.Copy(flowPoints, first, slice, 0, slice.Length);

            var segment = new RiverSegment(name, slice, phase, i * 0.01f);
            Segments[i] = segment;
            AddControl(segment);
            registry.Add(segment.Water);

            phase += PolylineLength(slice);
        }
    }

    /// <summary>
    /// One-shot layout of the bank pieces once the shared template reports its bounds. Which of the asset's horizontal
    /// axes is the long one decides the base yaw: a piece is always laid out as "long axis along the flow, short axis
    /// across the bank", and a regenerated asset is free to come back the other way round.
    /// </summary>
    bool SettleDikes()
    {
        var local = Dikes.TemplateLocalSize;
        if (local.X <= 1e-6f || local.Z <= 1e-6f)
            return false;

        // Reaching +Z from the long axis: yaw 0 already points +Z there, -90 degrees turns +X into +Z. Either sign is
        // a valid layout; what matters is that the long side runs downstream and the short side runs across. The asset
        // as committed measures 0.99 x 0.20 x 0.33 m, so the quarter turn is the usual answer.
        bool longAxisIsX = local.X >= local.Z;

        for (int i = 0; i < Segments.Length; i++)
            Segments[i].AddDikes(Dikes, longAxisIsX);

        return true;
    }

    /// <summary>
    /// Overlap between a movement footprint and any stretch's water, summed. The collider only compares the total against the
    /// value at the start of the move, so adding the stretches keeps the blocking behaviour of a single channel.
    /// </summary>
    internal float WaterOverlap(in Vector3 center, in Vector3 half)
    {
        float overlap = 0f;
        for (int i = 0; i < Segments.Length; i++)
            overlap += Segments[i].WaterOverlap(center, half);
        return overlap;
    }

    /// <summary>Control points only flush the optional arguments onto the panel; the stretches have to be ticked explicitly.</summary>
    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        // The template bounds arrive asynchronously with the InstancedModel load, so the piece layout runs on the first
        // frame that reports them.
        if (!dikesSettled)
            dikesSettled = SettleDikes();

        for (int i = 0; i < Segments.Length; i++)
        {
            if (Segments[i].Tick(time))
                result = true;
        }

        // After the stretches, never before: their Tick is what wrote the instance transforms this update consumes.
        if (Dikes.Update(time))
            result = true;

        return result;
    }

    /// <summary>Arc length of a control point polyline, i.e. the phase advance a stretch contributes to its successor.</summary>
    static float PolylineLength(ReadOnlySpan<WaterRibbonPoint> points)
    {
        float total = 0f;
        for (int i = 1; i < points.Length; i++)
            total += Vector3.Distance(points[i - 1].Position, points[i].Position);
        return total;
    }
}
