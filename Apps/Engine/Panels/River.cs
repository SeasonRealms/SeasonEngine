// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Season.Rendering.Effects;

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
/// <para><b>Both ends of the course are cut, per row.</b> Downstream the water runs a few metres past the coast line so
/// the mouth's diagonal cut lands out over the sea, while the bed and the levee stop on the line itself; the estuary's
/// spray covers the seam - it used to carry on over the edge as a 3.7 m cascade into the sea. Upstream - the "mouth" of
/// the first stretch, which starts inside the waterfall pool - the same
/// cut runs the other way: over the first <see cref="MouthReach"/> metres only the ribbon draws, because the pool's
/// water is a five centimetre film lying on the very grass the ribbon then lies on, so the bed has nothing to add there
/// and would only butt a 22 cm step into the pool. The bed starts where that film ends, and the levee grows back to its
/// full section over <see cref="MouthRamp"/>. Tapering the section matters for a second reason: the pool's rim rocks
/// stand along the film's edge, and a 66 cm levee running out of the pool would stand up in front of them.</para>
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
    /// How far inside the nominal water line ±hw the visible shoreline actually runs: the water plane cuts the inner face
    /// between its bed edge and its shoulder at that much past the shoulder, because the face is <see cref="WallWidth"/>
    /// across while rising <see cref="CrestRise"/> over a <see cref="WaterDepth"/>-deep channel. The movement block stops on
    /// this line rather than on ±hw, which is what leaves the bank pieces standing just outside it walkable.
    /// </summary>
    const float WaterlineDepth = WallWidth * CrestRise / (CrestRise + WaterDepth);

    /// <summary>
    /// Water depth over the bed. Together with the course height this is what puts the bed 12 cm above the grass, i.e.
    /// the raised bed the levee plan asks for, instead of the 2 cm skim it was sitting at before.
    /// </summary>
    const float WaterDepth = 0.22f;

    /// <summary>
    /// A row whose water surface is below this is past the shore lip: the bank and bed stop there and only the water
    /// ribbon continues. Two centimetres of slack absorbs the interpolation between the last on-land point and the first
    /// falling one. The main course never falls - it runs out over the coast at a constant height - so the only thing
    /// still using the height cut is the south branch, whose tail sinks into the foot of the range, see
    /// <see cref="BranchCourse"/>; the estuary stretch is cut by the coast line instead, see
    /// <see cref="RiverSegment.BuildSurface"/>.
    /// </summary>
    const float LandLimit = -0.02f;

    // -- Bank pieces (Assets/RiverBank.glb), one pair per DikeSpacing metres. --

    /// <summary>
    /// World size of one piece: across the bank, up, and along the flow. Stretched from the asset's own aspect.
    /// Internal because <see cref="Pond"/> rings its basin with the very same piece, and a rim that did not stand at
    /// exactly the height and width a river bank does would read as a different material.
    /// </summary>
    internal const float DikeWidth = 1.55f;
    internal const float DikeHeight = 0.86f;
    internal const float DikeLength = 4.0f;

    /// <summary>
    /// Center-to-center spacing along the course, shorter than <see cref="DikeLength"/> on purpose: neighbours overlap, so
    /// the outside of a bend never opens a wedge between two pieces.
    /// </summary>
    internal const float DikeSpacing = 3.20f;

    /// <summary>
    /// How far a piece is buried, measured from the grass to its anchor (the template-box center). Internal because
    /// <see cref="Pond"/> derives the top of its rim from it: the rim is the top of a piece standing on the grass.
    /// </summary>
    internal const float DikeSink = 0.10f;

    /// <summary>
    /// The top of a standing piece, above the grass: its full height less the buried part. The anchor in SyncDikes is
    /// placed at half this box regardless of the row's water surface, so this is absolute - <see cref="Pond"/> derives
    /// <c>RimTop</c> from the very same pair of constants, which is what lets the pond's rim carry straight onto the
    /// bank pieces at one height. The walkable <see cref="DikeSlab"/> tops must use this, not a water-referenced height.
    /// </summary>
    internal const float DikeTop = DikeHeight - DikeSink;

    /// <summary>How far the piece's inner edge sits behind the crest shoulder, i.e. how much it overlaps the levee.</summary>
    const float DikeInset = 0.06f;

    /// <summary>
    /// How far a piece may reach into the other channel's water before it is refused, in meters. Not zero: the search in
    /// <see cref="BuildDikeSlots"/> steps by <see cref="PinStep"/>, so a piece landing exactly on that waterline would be
    /// luck rather than rule, and the centimetres it holds back are a gap between two bank ends. Four is small enough that
    /// the rounded, soil-sided corner of a piece over the waterline still reads as the corner of the bank.
    /// </summary>
    const float DikeOverhang = 0.04f;

    /// <summary>
    /// Half-turn (0 or pi) that decides which face of the piece looks at the water. The asset is asymmetric - one long
    /// grass slope against one cut soil face - and which of the two ends up on the water side depends on how it was
    /// modelled, so this is the single knob to turn if a regenerated piece comes back with its cut face to the river.
    /// <see cref="Pond"/> turns its own pieces by the same number, so the two lines of bank always face the same way.
    /// </summary>
    internal const float DikeFacing = 0f;

    /// <summary>Where the island's east edge runs, i.e. where the grass tile stops and the sea begins - the tile is 150 m across
    /// (see <c>Rocks.CoastHalf</c>), pulled in 15 cm so a piece's foot lands on the grass and not on its last blade. Only
    /// the stretch that reaches the sea tests against it: bank pieces are dropped one by one where their footprint would
    /// cross the line (<see cref="AddDikePair"/>), and the bed and the levee stop at the last row before it
    /// (<see cref="RiverSegment.BuildSurface"/>). The boat's waterway uses it as the coast line: everything past it is the
    /// open sea, and the estuary corridor is what carries a hull across it.</summary>
    internal const float CoastEdge = 74.85f;

    // -- Confluences, where the south branch crosses the main course. --
    //
    // Both channels stand on their own levee, so a crossing is not two water planes laid over each other: each river's
    // berm physically crosses the other's channel. Each one therefore opens along its own course where the other passes -
    // the branch over its last metres before the main channel and its first after, the main course on both its banks.

    /// <summary>
    /// Half length of the branch's own opening, measured along its course from the end that meets the main channel. Just
    /// long enough to hold the flat apron under the main channel's water film: the five metres this used to take pulled
    /// the branch's berms back that far from the crossing and left a bare meadow bowl where the two channels meet.
    /// </summary>
    internal const float JunctionHalf = 1.20f;

    /// <summary>
    /// Length of the ramp from that opening back out to the full branch levee. It ends roughly a metre outside the main
    /// course's outer toe, so each branch berm rises straight off the main bank and the two levee lines meet in a corner
    /// rather than stopping in mid-meadow.
    /// </summary>
    internal const float JunctionFeather = 1.80f;

    /// <summary>
    /// Half length of the main course's opening on each bank. The branch's channel is 4.5 m of water plus a 0.35 m inner
    /// face on each side, so this clears it with about a metre of apron either side for the two waters to read as one.
    ///
    /// <para>This shapes the berm only. Where the line of bank pieces stops is a different question with a different
    /// answer: not this far from the crossing, but wherever a piece's footprint first reaches the branch's water. The two
    /// courses cross at an angle and both lines of pieces stand a lateral offset away from their own centerline, so that
    /// point is about a metre short of this on one bank and a metre past it on the opposite one - which is why one fixed
    /// distance cannot close all four corners. See <see cref="WaterBand"/>.</para>
    /// </summary>
    internal const float CrossingHalf = 3.00f;

    /// <summary>
    /// Length of the ramp from the crossing opening back out to the full levee. Kept near <see cref="JunctionFeather"/>:
    /// the two ramps face each other across the confluence, and one much longer than the other leaves the wider side's
    /// bank looking cut back.
    /// </summary>
    internal const float CrossingFeather = 1.80f;

    /// <summary>
    /// Step of the search that places the last pair on each side of an opening, in meters. A piece's inner corner has to
    /// land on the other channel's waterline, and the spacing walk never lands there, so the search walks outward from the
    /// opening's edge until a piece fits - this is how finely that landing resolves.
    /// </summary>
    const float PinStep = 0.05f;

    /// <summary>
    /// How far the bed rises at a junction, above the course's usual <see cref="WaterDepth"/>. The branch bed and the main
    /// bed would otherwise land on the same absolute height and z-fight across the whole crossing; lifting the branch a few
    /// centimetres keeps the difference a step under the water instead of a shimmer on it. Only the branch lifts - the
    /// main bed is the lower of the two by construction.
    /// </summary>
    internal const float JunctionBedLift = 0.08f;

    // -- The mouth, where the first stretch meets the waterfall pool. --

    /// <summary>
    /// Arc length from the start of the course that only the ribbon covers. On the main course everything before it
    /// lies inside the waterfall pool's water film, which the pool already draws, so the bed and the levee begin where
    /// that film ends; on the branch the same metres are the film lying on the grass at the source. Either way the bed
    /// would add nothing there and a 22 cm step would show, so it waits.
    ///
    /// <para>The pool's film edge measures 1.2 m along the first stretch and its sampled rows land at 1.18 m and
    /// 2.94 m, so this is the smallest value that still starts the mesh on the edge instead of inside the pool. The
    /// branch's rows are denser, so it would cut a little earlier there - but the branch is not a mouth at all, see
    /// <see cref="RiverSegment"/>: it leaves the pond through the opening in the pond's rim, at the same level, and
    /// keeps that bank.</para>
    /// </summary>
    internal const float MouthReach = 1.10f;

    /// <summary>
    /// Length of the ramp from the flat mouth apron back out to the full levee section. Deliberately shorter than the
    /// rocks: the pool's rim stands from 0.3 m to 3.3 m along the course, so the levee is already at full height among
    /// them. The four metres this used to take left the bank visibly cut off two to three metres short of the rim.
    /// </summary>
    const float MouthRamp = 1.20f;

    /// <summary>
    /// Crest height over the water where the channel leaves the pool. Not an opening's drop onto the bed: down here the
    /// water is only five centimetres above the grass, so a crest at the bed would leave the band's edge hanging in the
    /// air. Two centimetres of lip instead puts the shoreline back where the rest of the course keeps it - the intersection
    /// of the water plane with the section - while standing far too low to hide the pool's rim rocks.
    /// </summary>
    const float MouthLip = 0.02f;

    const int BandCount = 7;
    const int SectionPoints = 8;
    const int TextureSize = 512;

    // Generated once and shared by every segment: the noise is deterministic, and one identical 1 MB upload per stretch
    // would otherwise be built from one identical byte array each time.
    static byte[] bankPixels;
    static byte[] bedPixels;

    readonly WaterRibbonPoint[] _points;

    /// <summary>
    /// One place where a stretch opens its levee out because another channel crosses it: arc metres from the stretch's
    /// first point, the half length of the flat apron, and the ramp back to the full section.
    /// </summary>
    internal readonly record struct Opening(float Along, float Half, float Feather);

    // True on a stretch that starts at a source - the waterfall pool on the main course.
    // Only such a stretch opens its levee out over its first metres, see MouthReach and MouthFactor. The branch does not:
    // it leaves the pond through the opening in the pond's own rim, which is the bank it should keep, and a mouth lip
    // there would lay a grass shelf across the outlet instead of meeting the pond's water plain.
    readonly bool _mouth;

    // Where this stretch opens its levee out; empty on a stretch that no other channel meets.
    readonly Opening[] _openings;

    // The other route's channel, which this stretch's bank pieces have to stop on rather than cross. Every stretch carries
    // it, including the ones far from the junction where it never binds.
    readonly WaterBand _counter;

    // How far this stretch's bed rises inside an opening, see JunctionBedLift. Zero on the main course.
    readonly float _bedLift;

    // True on the stretch that runs to the island edge: its bank pieces are dropped wherever one's footprint would
    // cross the coast, see AddDikePair, and its bed and levee stop on the line while the water runs on past it, see
    // BuildSurface.
    readonly bool _shore;

    // One axis-aligned rectangle per row pair, in author space, for the movement footprint test.
    readonly List<WaterSlab> _slabs = new();

    // One rectangle per bank piece, in author space, parallel to the piece slots: the top of a piece is a walkable floor,
    // the same lift PlayerCollider derives from the pond's rim bands.
    readonly List<DikeSlab> _dikeSlabs = new();

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
    /// <param name="flowSpeed">
    /// Current speed in m/s, see <see cref="WaterRibbon.FlowSpeed"/>. Zero draws standing water: the shader samples at
    /// <c>phase + arc - time * speed</c>, so a speed of zero freezes the pattern, while each point's flow direction keeps
    /// its other job - it is still the band's lateral axis.
    /// </param>
    /// <param name="sortOrder">Draw order among ribbons; steady and increasing downstream so the blend order never flips.</param>
    /// <param name="mouth">
    /// Whether this stretch starts in the waterfall pool rather than at the tail of another stretch. Only the first
    /// stretch of the main course does, and it is the only one whose levee is opened out at its own start.
    /// </param>
    /// <param name="openings">
    /// Where this stretch opens its levee out because another channel crosses it, in arc metres from its first point. Empty
    /// when nothing does; see <see cref="JunctionHalf"/> and <see cref="CrossingHalf"/> for how wide each opening is.
    /// </param>
    /// <param name="counter">
    /// The other route's water. Every bank piece is tested against it, so the line of pieces on this stretch ends on that
    /// channel's waterline instead of at a fixed distance from the crossing; see <see cref="CanStand"/>.
    /// </param>
    /// <param name="bedLift">How far the bed rises inside an opening, or zero to leave it at <see cref="WaterDepth"/>.</param>
    /// <param name="shore">
    /// Whether this stretch runs to the island edge. Only then does the bank test each piece against the coast line,
    /// see <see cref="CoastEdge"/>, so the line of pieces ends at the edge instead of stepping off it.
    /// </param>
    internal RiverSegment(
        string name, WaterRibbonPoint[] points, float phase, float flowSpeed, float sortOrder, bool mouth,
        Opening[] openings, WaterBand counter, float bedLift, bool shore)
    {
        Name = name;
        _points = points;
        _mouth = mouth;
        _openings = openings;
        _counter = counter;
        _bedLift = bedLift;
        _shore = shore;

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

            // One speed for a whole route, which is the first of the three numbers (speed, phase, shared control point) the
            // shader needs to make the current pass a joint without a jump, and the reason a stretch with no source can
            // simply be given zero and read as standing water. See <see cref="River.FlowSpeed"/> and
            // <see cref="River.StillSpeed"/>.
            FlowSpeed = flowSpeed,

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
    /// Walkable floor height under a footprint - world center plus half extents - from the tops of this stretch's bank
    /// pieces: the same five samples <see cref="Management.PlayerCollider.FloorHeightUnder"/> takes over the pond's rim
    /// bands, tested against each piece's footprint rectangle in author space. A piece's top stands at
    /// <see cref="DikeTop"/> above the grass, the same height the pond derives as <c>RimTop</c>.
    ///
    /// <para>This is what lets the player jump onto the bank and walk along it: the block below the pieces only covers the
    /// water, so once the feet are on a piece the two tests agree. Like the pond's rim, the top is more than one
    /// <see cref="Management.PlayerCollider.StepLimit"/> above the grass, so it is reached by a jump, never by walking up.
    /// Hand-edited pieces move with their instance because the query reads the stretch's live matrix.</para>
    /// </summary>
    internal float FloorHeightUnder(in Vector3 center, in Vector3 half)
    {
        if (_dikeSlabs.Count == 0) return 0f;
        if (!Matrix4x4.Invert(BuildWorldMatrix(), out var inverse)) return 0f;

        float top = 0f;
        for (int sample = 0; sample < 5; sample++)
        {
            float sx = sample == 0 ? 0f : (sample & 1) == 0 ? -1f : 1f;
            float sz = sample == 0 ? 0f : (sample & 2) == 0 ? -1f : 1f;
            var point = Vector3.Transform(
                new Vector3(center.X + sx * half.X, center.Y, center.Z + sz * half.Z), inverse);

            for (int i = 0; i < _dikeSlabs.Count; i++)
            {
                var slab = _dikeSlabs[i];
                var delta = point - slab.Center;
                if (MathF.Abs(Vector3.Dot(delta, slab.Lateral)) <= slab.HalfWidth
                    && MathF.Abs(Vector3.Dot(delta, slab.Flow)) <= slab.HalfLength)
                {
                    top = MathF.Max(top, slab.Top);
                    break;
                }
            }
        }

        return top;
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

        // The channel only exists where the water is still on land, so a course that falls ends its bank and bed at the
        // last on-land row and lets only the ribbon continue - the south branch's tail, which sinks into the range. The
        // estuary stretch is cut in the same spirit at the coast: its course now runs a stretch past the line so the
        // mouth's diagonal cut lands out over the water, but earth carried that far would be a slab of island hanging
        // over the sea, so the bed and the levee stop at the last row before CoastEdge and the ribbon alone goes on.
        int landRows = rows;
        while (landRows > 1 && (sampled[landRows - 1].Center.Y < LandLimit
            || (_shore && sampled[landRows - 1].Center.X > CoastEdge)))
            landRows--;

        // Arc length per row, measured on the sampled curve. The mouth taper needs metres, and the control point index
        // says nothing about them - the runs between two points are not equally long.
        var along = new float[rows];
        for (int row = 1; row < rows; row++)
            along[row] = along[row - 1] + Vector3.Distance(sampled[row - 1].Center, sampled[row].Center);

        // The upstream cut described in the class header: skip the rows the pool's water film already covers, so the
        // bed begins at the film's edge instead of inside the pool. At least two rows stay, otherwise there is no mesh.
        int firstRow = 0;
        while (_mouth && firstRow < landRows - 2 && along[firstRow] < MouthReach)
            firstRow++;

        // Eight section points per row plus one normal per band per row: built once, then the cell loop is a pure copy.
        var section = new Vector3[rows * SectionPoints];
        var normals = new Vector3[rows * BandCount];
        var lateral = new float[SectionPoints];
        var height = new float[SectionPoints];

        for (int row = firstRow; row < landRows; row++)
        {
            var line = sampled[row];
            var side = WaterRibbonMath.Lateral(line.Flow);
            float halfWidth = line.HalfWidth;
            float bedEdge = MathF.Max(halfWidth - WallWidth, 0.05f);

            // An opening is where the other channel crosses this one, and it opens the levee out along the course rather
            // than across it: a 66 cm berm standing in the other channel's water would read as a dam. The crest drops all
            // the way onto the bed there, so crest, inner faces and bed become one flat shelf a full water depth down.
            //
            // Stopping that drop at the waterline, as it used to, left the berm standing two centimetres under the other
            // channel's surface: a shelf you look straight through the water at, which reads as the dam the opening was
            // supposed to remove. On the bed there is nothing left to see but bed, and the shelf is deep enough that the
            // water hides its own edge. The outer slope keeps its shape and leans out to the meadow, which is what keeps
            // the shelf from ending in a step.
            //
            // The pool mouth is the second such opening, but it drops the crest only to a low lip above the water, see
            // MouthLip: down there the water is a film on the grass and a crest at the surface would hang in the air.
            float junction = OpeningFactor(along[row]);
            float mouth = MouthFactor(along[row]);
            float skirt = SkirtWidth + junction * 1.6f;

            // A junction row lifts its bed clear of the other channel's: both beds otherwise land on exactly
            // water - WaterDepth and z-fight across the whole crossing.
            float bed = -WaterDepth + junction * _bedLift;

            float crest = CrestRise + junction * (bed - CrestRise) + mouth * (MouthLip - CrestRise);

            // The toe always ends the same distance under the grass, whatever this row's water height is, so the last
            // rows before the lip cannot leave the skirt hanging in the air.
            float toe = -(line.Center.Y + BuriedDepth);

            lateral[0] = -(halfWidth + CrestWidth + skirt); lateral[1] = -(halfWidth + CrestWidth);
            lateral[2] = -halfWidth;                       lateral[3] = -bedEdge;
            lateral[4] = bedEdge;                          lateral[5] = halfWidth;
            lateral[6] = halfWidth + CrestWidth;           lateral[7] = halfWidth + CrestWidth + skirt;

            height[0] = toe;           height[1] = crest;
            height[2] = crest;         height[3] = bed;
            height[4] = bed;           height[5] = crest;
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

        for (int row = firstRow; row < landRows - 1; row++)
        {
            // Inside a crossing the crest has dropped onto the bed, so its two bands belong to the bed's material: kept
            // on the bank's they drew two grass-green stripes running across the other channel's water, which is the
            // same berm the geometry no longer builds, only painted.
            bool opened = OpeningFactor((along[row] + along[row + 1]) * 0.5f) > 0.5f;

            for (int band = 0; band < BandCount; band++)
            {
                // Outer slopes and crests vs. inner faces and bed: two materials, so two Surfaces.
                bool crestBand = band == 1 || band == BandCount - 2;
                bool isBank = band == 0 || band == BandCount - 1 || (crestBand && !opened);
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

        BuildSlabs(sampled, along, landRows);
        BuildDikeSlots(sampled, along, landRows);
    }

    /// <summary>
    /// One rectangle per row pair, in author space, covering the water the movement footprint test blocks entry to: the
    /// <b>maximum</b> visible shoreline of the two rows - ±hw less <see cref="WaterlineDepth"/>, because the water plane
    /// cuts the inner face a little inside the shoulder. The bank pieces and the levee toe stand outside this band on
    /// purpose: a piece's top is a walkable floor (<see cref="FloorHeightUnder"/>) the player can jump onto and walk
    /// along, exactly like the pond's rim, and only stepping into the water itself is refused. Inside an opening the crest
    /// has dropped onto the bed, so the water does reach its full ±hw and the band widens to it - which also keeps the
    /// junction, where no pieces stand, closed to movement.
    /// </summary>
    void BuildSlabs(WaterRibbonRow[] sampled, float[] arc, int rows)
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

            // Where the levee is fully stood up the shoreline sits inside ±hw; where an opening has dropped the crest
            // onto the bed the water spreads to ±hw itself. Smoothstepped with the opening, like everything else here.
            float junction = OpeningFactor((arc[row] + arc[row + 1]) * 0.5f);
            float shoulder = MathF.Max(
                MathF.Max(start.HalfWidth, end.HalfWidth) - WaterlineDepth * (1f - junction), 0.05f);

            _slabs.Add(new WaterSlab(
                (start.Center + end.Center) * 0.5f,
                WaterRibbonMath.Lateral(flow),
                flow,
                shoulder,
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
    ///
    /// <para><b>Nothing hangs over the shore lip.</b> A piece is <see cref="DikeLength"/> long and anchored at its center, so
    /// the last one allowed on land would reach two metres past the cliff edge and stand in the sea. On the stretch that ends
    /// at the sea the walk therefore stops half a piece short of the lip and one pair is pinned right there, so the bank line
    /// reads as ending at the edge instead of stepping off it. That cut runs <i>along</i> the course; the coast runs across
    /// it, so <see cref="AddDikePair"/> also drops the individual piece whose own footprint would cross
    /// <see cref="CoastEdge"/>.</para>
    /// </summary>
    void BuildDikeSlots(WaterRibbonRow[] sampled, float[] arc, int rows)
    {
        _slots.Clear();
        _dikeSlabs.Clear();

        // Arc metres of every pair laid down so far. The opening pins below read it to tell a gap from a piece they would
        // only land on top of.
        var standing = new List<float>();

        // How far along the course a piece may still be anchored: its far end lands exactly on the last on-land row. On a
        // stretch that stays on land the lip never happens and the walk is only bounded by the rows themselves.
        float limit = _shore && rows < arc.Length ? arc[rows - 1] - DikeLength * 0.5f : float.PositiveInfinity;

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

            while (placement <= travelled + chord && placement <= limit)
            {
                if (PlaceAt(sampled, rows, placement))
                {
                    placed = placement;
                    standing.Add(placement);
                }

                placement += DikeSpacing;
            }

            travelled += chord;
        }

        // The split table decides where a stretch ends, and that rarely lands on the spacing: the last piece can sit a
        // whole spacing short of the joint, which is far enough to open a gap wider than nothing between two 4 m
        // pieces. Pin one pair on the last row. It cannot coincide with the next stretch's first piece, because that
        // one starts half a spacing in, so the two overlap instead of z-fighting.
        //
        // At the shore the same pin runs at the limit instead of the last row, which is what keeps the bank short of
        // the edge: the pin's far end is the lip row itself.
        float tail = MathF.Min(travelled, limit);
        if (tail - placed > DikeSpacing * 0.5f && PlaceAt(sampled, rows, tail))
            standing.Add(tail);

        // An opening's two edges rarely land on the spacing either, and what the walk leaves out there is a whole piece of
        // missing bank. Nor sits the missing piece at a fixed distance from the edge: a piece fits up to the other
        // channel's water, and how far along the course that is depends on where that water runs, not on the opening. On
        // one bank of the confluence it is a metre short of the edge - which left the corner bare - and on the opposite
        // one a metre past it - which let the piece overshoot the other water. So each edge is walked outward in
        // <see cref="PinStep"/> steps for the first spot a piece still fits, i.e. its inner corner on that waterline, and
        // one pair goes there.
        for (int i = 0; i < _openings.Length; i++)
        {
            var opening = _openings[i];

            for (int side = -1; side <= 1; side += 2)
            {
                float edge = Math.Clamp(opening.Along + side * opening.Half, 0f, tail);

                for (float step = 0f; step <= DikeLength * 2f; step += PinStep)
                {
                    float anchor = edge + side * step;
                    if (anchor < 0f || anchor > tail)
                        break;

                    if (!Locate(sampled, rows, anchor, out var center, out var flow, out float halfWidth))
                        break;

                    if (Taken(standing, anchor))
                        break;

                    if (!AddDikePair(center, flow, halfWidth, anchor))
                        continue;

                    standing.Add(anchor);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Whether one of the pairs already laid down sits within a hand's width of <paramref name="along"/>. Overlapping
    /// neighbours are the normal state of this bank; what this rejects is a second piece on almost the same spot, which
    /// shimmers instead of reading as one bank.
    /// </summary>
    static bool Taken(List<float> standing, float along)
    {
        for (int i = 0; i < standing.Count; i++)
        {
            if (MathF.Abs(standing[i] - along) < DikeLength * 0.1f)
                return true;
        }

        return false;
    }

    /// <summary>
    /// One pair at an exact arc length: <see cref="Locate"/> the row, lay down whichever banks fit. False when no row pair
    /// covers that length, or when neither bank fits.
    /// </summary>
    bool PlaceAt(WaterRibbonRow[] sampled, int rows, float along)
    {
        if (!Locate(sampled, rows, along, out var center, out var flow, out float halfWidth))
            return false;

        return AddDikePair(center, flow, halfWidth, along);
    }

    /// <summary>
    /// The row pair that covers <paramref name="along"/>, interpolated: the center point, the heading and the half width a
    /// piece at that exact arc length stands on.
    /// </summary>
    static bool Locate(
        WaterRibbonRow[] sampled, int rows, float along, out Vector3 center, out Vector3 flow, out float halfWidth)
    {
        float walk = 0f;

        for (int row = 0; row < rows - 1; row++)
        {
            var start = sampled[row];
            var end = sampled[row + 1];

            float chord = Vector3.Distance(start.Center, end.Center);
            if (chord < 1e-4f)
                continue;

            if (along < walk || along > walk + chord)
            {
                walk += chord;
                continue;
            }

            float t = (along - walk) / chord;
            center = Vector3.Lerp(start.Center, end.Center, t);
            flow = Flatten(Vector3.Lerp(start.Flow, end.Flow, t));
            halfWidth = start.HalfWidth + (end.HalfWidth - start.HalfWidth) * t;
            return true;
        }

        center = default;
        flow = Vector3.UnitZ;
        halfWidth = 0f;
        return false;
    }

    /// <summary>
    /// A pair of slots, one per bank, sharing the row's frame - each bank decided on its own. The two sides are not
    /// equivalent near an opening: the other channel crosses this course at an angle, so its water reaches a metre further
    /// along the course on one bank than on the other, and a test that asked both together would stop both on the stricter
    /// one's spot - which is a whole piece of missing bank on the looser side. The same holds near the sea, where the coast
    /// crosses the course at an angle: the piece on the seaward bank reaches past <see cref="CoastEdge"/> while its mirror
    /// stays well inside it. Such a piece is dropped rather than added, which is what keeps the bank from ending in a block
    /// standing over open water.
    ///
    /// <para>Returns whether anything was added, so a caller walking the course can tell "spacing says place here" from
    /// "this spot is refused".</para>
    ///
    /// <para>Every accepted piece also gets a <see cref="DikeSlab"/>: its top is the walkable floor
    /// <see cref="FloorHeightUnder"/> reports, so a player who jumps onto the bank can stand and walk along it while the
    /// narrower <see cref="BuildSlabs"/> band keeps the water itself refused.</para>
    /// </summary>
    bool AddDikePair(Vector3 center, Vector3 flow, float halfWidth, float along)
    {
        var lateral = WaterRibbonMath.Lateral(flow);

        // From the row's center out to the piece's outer edge: the water line, the crest, then the whole piece.
        float reach = halfWidth + CrestWidth - DikeInset + DikeWidth;

        // Half the piece projected onto X: it is laid along the flow, so this is how far its far end runs ahead of the
        // anchor. Only the seaward direction matters, and the footprint is symmetric, so the sign is dropped.
        float run = MathF.Abs(flow.X) * DikeLength * 0.5f;

        bool added = false;

        for (int side = -1; side <= 1; side += 2)
        {
            if (_shore && center.X + lateral.X * side * reach + run > CoastEdge)
                continue;

            if (!CanStand(center, flow, halfWidth, lateral, side, along))
                continue;

            _slots.Add(new DikeSlot(center, lateral, flow, halfWidth, side));

            // The piece's own footprint rectangle, same offset formula as SyncDikes: its center sits half a piece out
            // from the water line plus the crest shoulder, and its top is the box top SyncDikes places - a full piece
            // high less the buried part, above the grass. Not above the row's water surface: the anchor is placed at
            // DikeHeight * 0.5 - DikeSink whatever this row's water height is, so adding center.Y here would float the
            // player 34 cm over the visible surface on every raised-levee row.
            float offset = halfWidth + CrestWidth - DikeInset + DikeWidth * 0.5f;
            _dikeSlabs.Add(new DikeSlab(
                center + lateral * (side * offset), lateral, flow,
                DikeWidth * 0.5f, DikeLength * 0.5f,
                DikeTop));

            added = true;
        }

        return added;
    }

    /// <summary>
    /// Whether the piece on one bank, <paramref name="side"/>, fits at <paramref name="center"/>. Two things rule a spot
    /// out: it is past the shore lip (<see cref="LandLimit" />) or in the mouth apron, or the piece would stand in the other
    /// channel's water.
    ///
    /// <para>The last one tests the piece's <b>whole footprint</b> - the 4 x 1.55 m rectangle it stands in - against that
    /// channel's water, see <see cref="WaterBand"/>. That is what puts both routes' lines of pieces on one another's
    /// waterline: a distance along the course cannot express where an obliquely crossing channel's edge runs, which is why
    /// the opening's half length alone left two corners of the confluence bare and let the opposite two overshoot.</para>
    /// </summary>
    bool CanStand(Vector3 center, Vector3 flow, float halfWidth, Vector3 lateral, int side, float along)
    {
        if (center.Y < LandLimit || MouthFactor(along) >= 0.001f)
            return false;

        float offset = halfWidth + CrestWidth - DikeInset + DikeWidth * 0.5f;
        var middle = center + lateral * (side * offset);

        // Nine points - corners, edge midpoints and the center - because a piece is four metres long and a channel four and
        // a half wide, so a channel cutting across a piece at an angle can miss all four corners.
        for (int a = -1; a <= 1; a++)
        {
            for (int b = -1; b <= 1; b++)
            {
                var point = middle + flow * (DikeLength * 0.5f * a) + lateral * (DikeWidth * 0.5f * b);
                if (_counter.Clearance(point) < -DikeOverhang)
                    return false;
            }
        }

        return true;
    }

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
    /// One piece's walkable footprint in author space: its center plus the frame its rectangle is built in, the half
    /// extents themselves, and the world top its surface stands at. Built beside <see cref="DikeSlot"/>s so a piece that
    /// was refused by <see cref="CanStand"/> is neither drawn nor floor.
    /// </summary>
    readonly record struct DikeSlab(Vector3 Center, Vector3 Lateral, Vector3 Flow, float HalfWidth, float HalfLength, float Top);

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
    /// The other route's channel, as the bank pieces have to see it: its control points with the half width each one
    /// carries, i.e. the swept strip of water. Not the route's <see cref="RiverSegment"/>s on purpose - a line of pieces
    /// has to stop on the <i>water</i> of the channel it meets, and where that water runs is the only thing about it they
    /// need to know.
    /// </summary>
    internal readonly record struct WaterBand(WaterRibbonPoint[] Points)
    {
        /// <summary>
        /// Distance from <paramref name="point"/> to this band's water, measured in the ground plane: positive outside,
        /// negative inside, in meters. Nearest point of the swept strip rather than of the centerline, so a bend reads as
        /// the union of its own quads; each edge is clamped, which makes the ends of the band rounded instead of open.
        /// </summary>
        internal float Clearance(Vector3 point)
        {
            float best = float.MaxValue;

            for (int i = 1; i < Points.Length; i++)
            {
                var start = Points[i - 1];
                var end = Points[i];

                float dx = end.Position.X - start.Position.X;
                float dz = end.Position.Z - start.Position.Z;
                float length = MathF.Sqrt(dx * dx + dz * dz);
                if (length < 1e-4f)
                    continue;

                // How far along the edge the point's own projection lands, clamped to the edge's ends.
                float reach = Math.Clamp(
                    ((point.X - start.Position.X) * dx + (point.Z - start.Position.Z) * dz) / length, 0f, length);

                float half = start.HalfWidth + (end.HalfWidth - start.HalfWidth) * (reach / length);

                float ox = point.X - (start.Position.X + dx / length * reach);
                float oz = point.Z - (start.Position.Z + dz / length * reach);
                float clearance = MathF.Sqrt(ox * ox + oz * oz) - half;

                if (clearance < best)
                    best = clearance;
            }

            return best;
        }
    }

    /// <summary>
    /// How far the levee is still opened out at arc length <paramref name="along"/>: 1 over the first
    /// <see cref="MouthReach"/> metres, then a smoothstep back to the full section over <see cref="MouthRamp"/>, and
    /// always 0 on a stretch that does not start at a source. Same contract as <see cref="JunctionFactor"/>.
    /// </summary>
    float MouthFactor(float along)
    {
        if (!_mouth || along >= MouthReach + MouthRamp) return 0f;
        if (along <= MouthReach) return 1f;

        float t = (along - MouthReach) / MouthRamp;
        return 1f - t * t * (3f - 2f * t);
    }

    /// <summary>
    /// How far the levee is still opened out at arc length <paramref name="along"/>: 1 inside an opening's half length,
    /// then a smoothstep back to the full section over its feather, and 0 when no opening is near. Same contract as
    /// <see cref="MouthFactor"/>, which does the same for a stretch that starts at a source.
    /// </summary>
    float OpeningFactor(float along)
    {
        float factor = 0f;

        for (int i = 0; i < _openings.Length; i++)
        {
            var opening = _openings[i];
            float distance = MathF.Abs(along - opening.Along);
            if (distance >= opening.Half + opening.Feather) continue;
            if (distance <= opening.Half) return 1f;

            float t = (distance - opening.Half) / opening.Feather;
            float value = 1f - t * t * (3f - 2f * t);
            if (value > factor) factor = value;
        }

        return factor;
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
/// Each stretch then starts at the arc length its predecessors covered, and all stretches of a route share one
/// <see cref="WaterRibbon.FlowSpeed"/>; that is the complete set of conditions the shader needs to make the current pass a
/// joint without a jump in direction, speed or phase. The speed is the one number that differs between the two routes:
/// the main course runs at <see cref="FlowSpeed"/> because the waterfall feeds it, the branch at <see cref="StillSpeed"/>
/// because nothing does.</para>
///
/// <para><b>Geography:</b> the course leaves the pool at the mountain foot in the south-west, runs east while steadily
/// pulling away from the southern range so it never hugs the rock face, stays well south of the pond and the observation
/// tower, and turns south-east over the last stretches to reach the shore in the one gap the east rock line has. The
/// place it deliberately touches existing geometry is the shore rocks flanking the mouth (water lapping at a boulder's
/// base is what a river mouth looks like).</para>
///
/// <para><b>The branch is the second route.</b> It takes over the corridor the road used to run through south of the
/// player's spawn, from the meadow down to the foot of the southern range, and crosses the main course at z = -47.5.
/// At that crossing the two berms have to give way to each other - each river is a raised section, so each one would
/// otherwise stand a 66 cm bank across the other's channel - which is what <see cref="RiverSegment.OpeningFactor"/> is
/// for. There the berm does not merely drop to the waterline: it drops onto its own bed, so both channels read as one
/// wide shallow shelf instead of one crossing the other on a submerged shelf. The two lines of bank pieces close the four
/// corners for the same reason: each route's pieces are tested against the other's water and stop on it, so neither line
/// ends short of the corner nor runs into the other channel. The two routes share only the bank batch and
/// the ribbon table - six stretches plus two, which is exactly
/// <see cref="Season.Rendering.SceneWaterRibbons.MaxActiveRibbons"/> - so the whole river still costs one instanced
/// draw.</para>
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
    /// Current speed of the main course, in metres per second. The waterfall is the source: the pool drains through the
    /// channel and out over the coast, so every stretch of that route carries the same speed - the speed is also the first
    /// of the three numbers that keeps the current from jumping at the seams between stretches.
    /// </summary>
    internal const float FlowSpeed = 0.75f;

    /// <summary>
    /// Current speed of the south branch: zero, i.e. standing water. Nothing feeds that route through a fall - it is the
    /// pond's own level that stands at its head and the meadow that swallows its tail beyond the main course - so a
    /// scrolling current on it would be a current with no source behind it. Only the scroll stops: its control points
    /// still carry a flow direction, which is what lays the band's lateral axis and its bank pieces.
    /// </summary>
    internal const float StillSpeed = 0f;

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
    /// <para>Six stretches, one ribbon slot each, out of the sixteen the shader can hold.</para>
    ///
    /// <para><b>The two ends are special.</b> Points 0 and 1 sit at the pool's own surface, so the two waters meet flush:
    /// point 0 is inside the pool's water film and point 1 is the outlet just clear of it, which is where the first
    /// stretch starts. Over the metres between them only the ribbon draws - the bed starts at the film's edge, see
    /// <see cref="RiverSegment"/> - so the channel leaves the pool as open water instead of ending in a levee wall that
    /// would stand in front of the pool's rim rocks.</para>
    ///
    /// <para><b>Point 14 is the mouth, and the course ends there.</b> Point 13 sits on the coast line (x = 74.85, see
    /// <see cref="CoastEdge"/>) and the water runs about three metres past it, because the mouth crosses that line at 32
    /// degrees: a course stopping on the line ends on a diagonal, and the inland half of that cut left a triangular wedge
    /// of dry bed where the water never arrived. Carrying the last point out over the water puts the whole cut past the
    /// coast, where the estuary's cloud stands over it. Only the water goes that far - the bed and the levee still stop on
    /// the line, see <see cref="RiverSegment.BuildSurface"/>, or the island would end in a slab of earth hanging over the
    /// sea. Points 12 to 14 aim the mouth between the shore rocks at z = -38 and z = -54, which is the only gap in the
    /// east rock line wide enough for a river.</para>
    /// </summary>
    static readonly CoursePoint[] Course =
    {
        // The pool outlet - water only for the first couple of metres; see RiverSegment.MouthReach.
        new(-68.20f, -66.70f, PoolWaterY, 2.00f),   // 0: inside the waterfall pool's water film
        new(-64.00f, -63.60f, PoolWaterY, 1.90f),   // 1: the outlet, just clear of that film

        // Straight 1 - away from the pool. Point 2 is the exact midpoint of 1 and 3.
        new(-54.00f, -60.15f, 0.195f, 2.00f),       // 2
        new(-44.00f, -56.70f, WaterY, 2.10f),       // 3: bend 1 vertex, the first turn away from the range

        // Bend 1 - the heading eases from 19 to 8 degrees, opening the angle against the southern range.
        new(-26.00f, -51.00f, WaterY, 2.20f),       // 4
        new(-8.00f, -48.60f, WaterY, 2.30f),        // 5: straight 2 begins

        // Straight 2 - crosses the x = 0 axis, which is where the south branch joins. Point 6 is the exact midpoint
        // of 5 and 7.
        new(3.00f, -47.10f, WaterY, 2.40f),         // 6
        new(14.00f, -45.60f, WaterY, 2.50f),        // 7

        // Bend 2 - the meadow bend, straightening the long run toward the east shore.
        new(30.00f, -43.40f, WaterY, 2.80f),        // 8
        new(46.00f, -40.60f, WaterY, 3.10f),        // 9: straight 3 begins

        // Straight 3 - past the observation tower, still more than ten metres off its footprint.
        new(53.00f, -39.60f, WaterY, 3.30f),        // 10: exact midpoint of 9 and 11
        new(60.00f, -38.60f, WaterY, 3.50f),        // 11

        // Bend 3 - the estuary turn, south-east into the gap in the shore rocks and out past the coast line.
        new(68.50f, -42.40f, WaterY, 3.90f),        // 12
        new(74.85f, -46.32f, WaterY, 4.30f),        // 13: the coast edge
        new(77.41f, -47.90f, WaterY, 4.30f),        // 14: the mouth, out over the water
    };

    /// <summary>
    /// Split table: first and last course index of each selectable stretch. Neighbouring stretches share their joint
    /// point, which is what makes the water meet the water instead of meeting a gap.
    ///
    /// The mouth is part of the last stretch rather than its own: a separate segment would be one bend, a pick target
    /// too small to hit and too short to drag with any precision. Keeping the estuary turn in the same stretch gives it
    /// ten metres of bank to select, while the bank pieces still stop at the coast - that cut is per piece against
    /// <see cref="CoastEdge"/>, not per segment - and the bed ends there too even though the water runs on.
    /// </summary>
    static readonly (int First, int Last, string Name)[] Splits =
    {
        (0, 3, "RiverStraightPool"),      // straight: out of the pool and away from the waterfall
        (3, 5, "RiverBendWest"),          // bend: the first turn away from the range
        (5, 7, "RiverStraightFord"),      // straight: the junction with the branch at x = 0
        (7, 9, "RiverBendMeadow"),        // bend
        (9, 11, "RiverStraightEast"),     // straight: the long run past the tower
        (11, 14, "RiverEstuary"),         // bend into the shore gap and out to the mouth
    };

    /// <summary>
    /// The south branch: the channel that drains the pond and runs due south along the x = 0 axis. It leaves the pond's
    /// south rim through the opening cut in it, crosses the main course at z = -47.5 and sinks into the range at its
    /// foot, where the ground rises and only the water ribbon continues into the hillside.
    ///
    /// <para><b>Two stretches and no shared point.</b> The main channel is 4.7 m wide across the crossing and this
    /// one's band is 4.5 m, so the branch simply stops inside it and resumes on the far side: the main water covers the
    /// gap, and the main course cuts its own bank across the branch with <see cref="CrossingHalf"/>. A shared control point
    /// would force one tangent through a right angle, which would bend both stretches at their joint.</para>
    ///
    /// <para><b>The upstream end is the pond, not the meadow.</b> Point 0 lies a hand's width inside the pond's own
    /// water, at <see cref="Pond.WaterY"/> less five millimetres: the two surfaces overlap there instead of meeting at a
    /// seam, and that gap keeps them from z-fighting across the overlap. The route is not a mouth (see
    /// <see cref="RiverSegment"/>), so its bank starts at full height on the very first row and the two berms simply
    /// continue the pond's rim out of the opening in it. The first three points keep a slow fall instead of a step, so
    /// the pond's surface reaches the channel's own <see cref="WaterY"/> over four metres.</para>
    ///
    /// <para><b>The downstream end is cut per row.</b> The last points drop below the ground, so the levee stops and the
    /// ribbon disappears under the meadow at the foot of the range.</para>
    /// </summary>
    static readonly CoursePoint[] BranchCourse =
    {
        // The outlet, inside the pond's water; see Pond.SouthEdgeZ. Only the ribbon covers its first hand's width.
        new(0f, Pond.SouthEdgeZ + 0.10f, Pond.WaterY - 0.005f, BranchHalfWidth),  // 0
        new(0f, -12.60f, 0.375f, 1.70f),    // 1: the channel proper resumes below the pond's rim
        new(0f, -13.80f, 0.355f, 1.80f),    // 2
        new(0f, -15.00f, WaterY, 1.95f),    // 3: full depth from here on
        new(0f, -25.00f, WaterY, 2.05f),    // 4
        new(0f, -35.00f, WaterY, 2.15f),    // 5
        new(0f, -45.90f, WaterY, 2.25f),    // 6: stops inside the main channel's water film
        new(0f, -49.35f, WaterY, 2.25f),    // 7: resumes on the far side of it
        new(0f, -56.00f, WaterY, 2.15f),    // 8
        new(0f, -63.00f, 0.30f, 2.05f),     // 9: the ground starts to rise toward the range
        new(0f, -69.50f, -0.10f, 2.00f),    // 10: crossing grass level here
        new(0f, -72.00f, -1.80f, 2.00f),    // 11: below the ground, inside the hillside
    };

    /// <summary>
    /// Half the branch's water width where it leaves the pond, in meters. <see cref="Pond"/> opens its rim by this much
    /// plus a hand's width either side, so the two waters meet through the opening instead of the rim pinching the
    /// channel; the same number is the half width of <see cref="BranchCourse"/>[0].
    /// </summary>
    internal const float BranchHalfWidth = 1.60f;

    /// <summary>
    /// Split table of the branch. The two stretches deliberately do not share a point: the gap between them is the
    /// main channel, and the main water is what joins the two halves.
    /// </summary>
    static readonly (int First, int Last, string Name)[] BranchSplits =
    {
        (0, 6, "RiverBranchSpring"),      // from the source down to the main course
        (7, 11, "RiverBranchSink"),       // out of the main course and under the range
    };

    /// <summary>
    /// Where the branch passes through the main course: the middle of the gap between its two stretches, which sits on the
    /// main channel's centerline. The main course reads it to cut its own bank there, see <see cref="CrossingHalf"/>.
    /// </summary>
    static readonly Vector3 BranchCrossing = new(0f, WaterY, (BranchCourse[6].Z + BranchCourse[7].Z) * 0.5f);

    /// <summary>
    /// Number of branch control points a hull can use: the pond outlet down to <c>BranchCourse[9]</c>, the last row
    /// whose surface still stands above the shore lip (0.30 m at z = -63). The points that follow it sink under the
    /// meadow at the foot of the range - the water surface is below the grass there and only the ribbon is left - so
    /// the corridor ends on the last row that still has a bed and banks under it. Its end cap reaches about two metres
    /// further, which is where the drawn water meets the grass.
    /// </summary>
    const int BranchNavigablePoints = 10;

    static (Vector3 Center, float HalfWidth)[]? navigableMain, navigableBranch;

    /// <summary>
    /// The two courses as the boat's navigable centerline: world-space water-surface points together with the water half
    /// width at each, in flow order. The main route runs from inside the waterfall pool (index 0) through the bridge to the
    /// estuary (index 14); the branch runs from the pond, across the main channel, and on up to the foot of the range
    /// (see <see cref="BranchNavigablePoints"/>).
    ///
    /// <para>These are read off the same tables the channel geometry is built from, so a course edit moves the corridor and
    /// the water it describes together - there is no second copy of the route to keep in sync. The pond and the open sea are
    /// not part of this table: they are rectangles, not ribbons, and <see cref="BoatWaterway"/> adds them by name.</para>
    ///
    /// <para>Cached once because both tables are static. Callers must not mutate the returned array.</para>
    /// </summary>
    internal static (Vector3 Center, float HalfWidth)[] NavigableCenterline(bool branch)
    {
        if (branch)
            return navigableBranch ??= BuildNavigableCenterline(BranchCourse, BranchNavigablePoints);

        return navigableMain ??= BuildNavigableCenterline(Course, Course.Length);
    }

    static (Vector3 Center, float HalfWidth)[] BuildNavigableCenterline(CoursePoint[] course, int count)
    {
        var result = new (Vector3 Center, float HalfWidth)[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = (new Vector3(course[i].X, course[i].Y, course[i].Z), course[i].HalfWidth);
        }

        return result;
    }

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

    // -- The mouth spray, where the river meets the sea. --

    /// <summary>
    /// Spray at the estuary: the waterfall pool's three particle systems copied onto the river's last metres - mist,
    /// splash and foam. Same device as the pool's mist: an invisible, pickable
    /// <see cref="Season.Controls.MistVolume"/> whose center, yaw and size <i>are</i> the parameters, so the whole effect
    /// can be dragged, turned and resized in the property panel like any other object.
    ///
    /// <para>Why it is needed at all: the bed is cut off on the coast line 1.8 m above the sea and the water
    /// breaks off one stretch further out, and a flat water surface meeting a flat plane with nothing in between
    /// reads as two pictures side by side. The cloud joins them: its foot sits on the water and its body swallows
    /// both cuts.</para>
    ///
    /// <para><b>The box is the only source.</b> The mouth crosses a straight coast at an angle, so the frame the bank
    /// stops at is not where the haze has to sit: measured off the lip it came out barely 2 m tall and could not cover the
    /// seam. The three numbers below are what the property panel shows - Pos, Rotation and Width/Height/Depth - hand tuned
    /// into a 12 x 5 x 5 cloud standing over the estuary, and they are what <see cref="MouthMist"/> is placed with. All
    /// three systems read that box and nothing else, so dragging, turning or resizing it moves the whole spray, and the
    /// course geometry no longer feeds the emitters at all.</para>
    /// </summary>
    internal readonly Season.Controls.MistVolume MouthMist = new("RiverMouthMist");

    /// <summary>Authored center of the spray box, i.e. the panel's PosX/PosY/PosZ.</summary>
    static readonly Vector3 SprayPos = new(75f, 2f, -46f);

    /// <summary>Authored yaw of the spray box, in degrees about Y: the panel's Rotation.</summary>
    const float SprayYawDegrees = 100f;

    /// <summary>Authored size of the spray box: the panel's Width/Height/Depth, in meters.</summary>
    static readonly Vector3 SpraySize = new(12f, 3f, 6f);

    /// <summary>
    /// Sea surface height, taken from <see cref="Sea.DefaultSeaLevel"/> rather than repeated: one number moves the water
    /// plane and the droplets' floor together. The splash no longer starts there - it starts on the lid of the box - but
    /// it is still the plane that kills a droplet, so spray that falls out of the cloud ends in the water rather than
    /// sinking through it.
    /// </summary>
    const float SeaY = Sea.DefaultSeaLevel;

    /// <summary>
    /// Where the splash sits in the box, as a fraction of the box's height measured down from the top face, and how far it
    /// climbs, as a fraction of the same height. Both are the waterfall's own proportions: the pool's splash is born 8 cm
    /// over its cloud's floor and crests at 0.71 m in a 2.6 m box, i.e. 3% and 27% of the height. Scaled by this box - 5 m
    /// against the pool's 2.6 - that puts the band at 38% of the height (0.43 m, just over the river's own surface at
    /// 0.34) and its crest at 1.78 m, in the body of the vapour. Floating over the lid, where it was, it read as a band of
    /// its own with nothing to do with the cloud underneath.
    /// </summary>
    const float SplashDrop = 0.615f;

    /// <summary>The climb, same units: 1.35 m on the authored 5 m box, the pool's 27%.</summary>
    const float SplashRise = 0.27f;

    // Null when the backend declined compute support; the update loop then has nothing to tick.
    ParticleSystemEffect? mist;
    ParticleSystemEffect? splash;
    ParticleSystemEffect? foam;

    /// <summary>Only the systems that actually registered; a backend without compute leaves this empty.</summary>
    readonly List<ParticleSystemEffect> systems = new();

    /// <summary>
    /// One puff: alpha 0.10, up from 0.075. The authored box covers a 12 x 5 m footprint, and a thin puff over that reads
    /// as haze rather than the solid white cloud an estuary wants; together with the density in
    /// <see cref="CreateMouthMist"/> that is what makes it read as one body.
    /// </summary>
    static readonly Vector4 SprayColor = new(0.92f, 0.96f, 1.00f, 0.10f);
    static readonly Vector3 SprayMid = new(0.95f, 0.97f, 1.00f);
    static readonly Vector3 SprayEnd = new(1.00f, 1.00f, 1.00f);

    /// <summary>Same wind as the scene fog and the waterfall's spray, so one wind field covers the whole picture.</summary>
    static readonly Vector3 SprayWind = new(0.30f, 0f, 0.10f);

    /// <param name="registry">
    /// Scene ribbon registry the water is registered into. Registration happens here rather than in <see cref="App"/> so the
    /// segment and its water can never end up on different sides of the list.
    /// </param>
    internal River(SceneWaterRibbons registry)
    {
        // AddControl queues the template load and takes ownership of the instance list, so the batch is created before
        // the stretches, which only ever append to it later.
        Dikes = new InstancedModel
        {
            Name = "RiverBanks",
            ModelName = DikeGlbPath,
            // Two long channels of 4 m bank pieces: the far reaches are off screen from anywhere the player can stand.
            DrawPath = InstanceDrawPath.Indirect,
            // These are solid ridges standing half a metre proud of the meadow, so they cast like any other prop.

            // A piece is a 4 m long strip, so its world AABB goes fat as soon as it turns - the same reason the levee
            // stretch uses Wireframe. Instances added later keep their default style and inherit this one through the
            // fallback in ObjectPicker.SetHighlight.
            Highlight = new Highlight { Style = HighlightStyle.Wireframe, EdgeWidth = 0.004f },
        };
        AddControl(Dikes);

        // Both routes are resolved to ribbon points first, so each can be handed to the other as the water its bank pieces
        // have to stop on: a line of pieces has to end on the other channel's waterline, and only that channel's own
        // polyline can say where that runs. See RiverSegment.WaterBand.
        var main = FlowPoints(Course);
        var branch = FlowPoints(BranchCourse);

        // One flat array for both routes: the picker, the collider and the per-frame update only ever walk Segments,
        // so a branch that is built the same way as the main course needs no plumbing of its own.
        var segments = new List<RiverSegment>(Splits.Length + BranchSplits.Length);
        AddRoute(main, branch, Splits, FlowSpeed, 0f, BranchCrossing, bedLift: 0f, shore: true, mouth: true, registry, segments);
        AddRoute(branch, main, BranchSplits, StillSpeed, 0.5f, null, RiverSegment.JunctionBedLift, shore: false, mouth: false, registry, segments);
        Segments = segments.ToArray();

        // The spray where the river meets the sea: the same invisible, pickable box the waterfall pool drives its mist and
        // foam from, placed with the authored numbers above, and all three particle systems spawned off its faces.
        AddControl(MouthMist);
        MouthMist.Place(SprayPos, SprayYawDegrees, SpraySize);

        mist = Register(CreateMouthMist());
        splash = Register(CreateMouthSplash());
        foam = Register(CreateMouthFoam());
    }

    /// <summary>
    /// A route's control points as ribbon points: position, half width, and one flow direction per point taken as the
    /// central difference of the <b>whole</b> course - computing it here, once, is what makes two neighbours agree at a
    /// shared point.
    ///
    /// <para>Both routes are resolved before either is split, because each one is handed to the other as the water its
    /// bank pieces have to stop against; see <see cref="RiverSegment.WaterBand"/>.</para>
    /// </summary>
    static WaterRibbonPoint[] FlowPoints(CoursePoint[] course)
    {
        var flowPoints = new WaterRibbonPoint[course.Length];

        for (int i = 0; i < course.Length; i++)
        {
            // Endpoints fall back to the one-sided difference.
            var before = course[Math.Max(i - 1, 0)];
            var after = course[Math.Min(i + 1, course.Length - 1)];
            var direction = new Vector3(after.X - before.X, 0f, after.Z - before.Z);

            flowPoints[i] = new WaterRibbonPoint(
                new Vector3(course[i].X, course[i].Y, course[i].Z),
                course[i].HalfWidth,
                direction);
        }

        return flowPoints;
    }

    /// <summary>
    /// Splits one route's ribbon points into its selectable stretches and registers their water. Runs once per route from
    /// the constructor, so both channels are built and phased the same way.
    /// </summary>
    /// <param name="flowPoints">This route's control points, see <see cref="FlowPoints"/>.</param>
    /// <param name="other">
    /// The other route's control points, handed to every stretch as the water its bank pieces stop against.
    /// </param>
    /// <param name="flowSpeed">
    /// Current speed of the whole route, see <see cref="FlowSpeed"/> and <see cref="StillSpeed"/>. It is a route-wide
    /// number, not a per-stretch one: two neighbours reading different speeds would put a visible jump in the current at
    /// their joint, which is the one thing the split must never do.
    /// </param>
    /// <param name="sortBase">
    /// First ribbon blend order of this route. The two routes are drawn independently - nothing crosses between them
    /// except at the junction, where both waters are the same height - so the only requirement is that one route's own
    /// order keeps increasing downstream.
    /// </param>
    /// <param name="crossing">
    /// Optional world point where the other route crosses this one. The stretch that contains it opens its levee there on
    /// both banks, so the other channel is not walled off by a berm it never asked for.
    /// </param>
    /// <param name="bedLift">How far this route's bed rises inside its openings, see <see cref="JunctionBedLift"/>.</param>
    /// <param name="mouth">
    /// Whether this route starts in a source's own water rather than at the tail of another stretch - true only for the
    /// main course, whose first metres lie inside the waterfall pool's film. The branch passes false: it leaves the pond
    /// through the opening in the pond's rim, level with the pond's surface, so there is nothing to open out.
    /// </param>
    /// <param name="shore">Whether this route's last stretch leaves the island edge and runs into the sea.</param>
    void AddRoute(
        WaterRibbonPoint[] flowPoints,
        WaterRibbonPoint[] other,
        (int First, int Last, string Name)[] splits,
        float flowSpeed,
        float sortBase,
        Vector3? crossing,
        float bedLift,
        bool shore,
        bool mouth,
        SceneWaterRibbons registry,
        List<RiverSegment> into)
    {
        // Arc position of the other route's channel, resolved against the whole course before it is split, so no stretch
        // has to know how far along it starts.
        float? crossingAlong = crossing is Vector3 point ? ArcOfNearest(flowPoints, point) : null;

        float phase = 0f;
        for (int i = 0; i < splits.Length; i++)
        {
            var (first, last, name) = splits[i];

            var slice = new WaterRibbonPoint[last - first + 1];
            Array.Copy(flowPoints, first, slice, 0, slice.Length);

            float length = PolylineLength(slice);

            // Where the split table skips a point, the two neighbours face each other across another channel: that gap
            // is the junction. A stretch ending at one opens out at its own tail, one starting at it at its head.
            var openings = new List<RiverSegment.Opening>(2);
            if (i + 1 < splits.Length && splits[i + 1].First > last)
                openings.Add(new RiverSegment.Opening(length, RiverSegment.JunctionHalf, RiverSegment.JunctionFeather));
            else if (i > 0 && splits[i - 1].Last < first)
                openings.Add(new RiverSegment.Opening(0f, RiverSegment.JunctionHalf, RiverSegment.JunctionFeather));

            // The other route's channel, when it crosses inside this stretch: the berm has to give way there, or it would
            // stand in the middle of that channel.
            if (crossingAlong is float at && at >= phase && at <= phase + length)
                openings.Add(new RiverSegment.Opening(at - phase, RiverSegment.CrossingHalf, RiverSegment.CrossingFeather));

            var segment = new RiverSegment(
                name, slice, phase, flowSpeed, sortBase + i * 0.01f, mouth: i == 0 && mouth,
                openings.ToArray(), new RiverSegment.WaterBand(other), bedLift, shore: shore && i == splits.Length - 1);
            into.Add(segment);
            AddControl(segment);
            registry.Add(segment.Water);

            phase += length;
        }
    }

    /// <summary>
    /// Arc length of the point on a route's polyline closest to <paramref name="target"/>, measured the same way the split
    /// lengths are. Null when the route has no usable edge.
    /// </summary>
    static float? ArcOfNearest(WaterRibbonPoint[] points, Vector3 target)
    {
        float? result = null;
        float arc = 0f;
        float best = float.MaxValue;

        for (int i = 1; i < points.Length; i++)
        {
            var start = points[i - 1].Position;
            var edge = points[i].Position - start;
            float length = edge.Length();
            if (length < 1e-4f)
                continue;

            var direction = edge / length;
            float reach = Math.Clamp(Vector3.Dot(target - start, direction), 0f, length);
            float distance = (start + direction * reach - target).LengthSquared();

            if (distance < best)
            {
                best = distance;
                result = arc + reach;
            }

            arc += length;
        }

        return result;
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

    /// <summary>
    /// Walkable floor height under a movement footprint: the top of any bank piece it touches, across every stretch. This
    /// is what turns the line of banks into ground the player can jump onto and walk along - the same lift the pond's rim
    /// bands give, at the same height, so the pond's rim carries straight on onto the branch's banks through the opening.
    /// </summary>
    internal float FloorHeightUnder(in Vector3 center, in Vector3 half)
    {
        float top = 0f;
        for (int i = 0; i < Segments.Length; i++)
            top = MathF.Max(top, Segments[i].FloorHeightUnder(center, half));
        return top;
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

        if (UpdateMouthSpray(time))
            result = true;

        return result;
    }

    /// <summary>
    /// Drives the estuary's three particle systems. Every one of them reads the authored box and nothing else - its center,
    /// its yaw, its size - so whatever the property panel shows is what the emitters use and a change lands on the next
    /// frame. Deriving the splash from the course instead was the bug: the box moved and the droplets stayed put.
    ///
    /// <para>The three are stacked inside the box rather than all on one face: the foam rides the bottom as the whitest
    /// band, the mist is born on the same face and rises to the lid, and the splash - the only system that visibly goes up
    /// and comes back down - is born low in the cloud, at the same fraction of the way up as the pool's, so it fizzes
    /// inside the vapour instead of drifting over its top.</para>
    /// </summary>
    bool UpdateMouthSpray(float time)
    {
        if (MouthMist.Placed)
        {
            // The box's local +X is the long side, across the mouth, and its local +Z is the direction the mouth runs, out
            // to sea: both come off the yaw, so turning the box turns the whole spray with it. Width is how far the cloud
            // reaches across the mouth and Depth how far it reaches out to sea.
            var axis = MouthMist.SpawnAxis;
            var boxAxis = new Vector2(axis.X, axis.Z);
            var downstream = new Vector3(-axis.Z, 0f, axis.X);
            var bottom = new Vector3(MouthMist.PosX, MouthMist.BottomY, MouthMist.PosZ);
            var seat = new Vector3(
                MouthMist.PosX, MouthMist.PosY + MouthMist.WorldHeight * (0.5f - SplashDrop), MouthMist.PosZ);

            if (mist != null)
            {
                mist.WorldOrigin = bottom;
                mist.SpawnAxis = boxAxis;
                mist.SpawnHalfLength = MouthMist.HalfLength;
                mist.SpawnHalfDepth = MouthMist.HalfDepth;

                // Rise speed solved from the box height, so pulling the box up is what makes the spray taller.
                mist.InitialVelocity = new Vector3(
                    0f,
                    WaterfallPool.RiseSpeed(MouthMist.WorldHeight, mist.Lifetime, mist.Acceleration.Y),
                    0f) + SprayWind * 0.1f;
            }

            if (foam != null)
            {
                // The whitest stroke in the picture, spread over the same rect and only floated up rather than climbed -
                // the pool's foam, and the reason one box moves both of them.
                foam.WorldOrigin = new Vector3(bottom.X, bottom.Y + 0.02f, bottom.Z);
                foam.SpawnAxis = boxAxis;
                foam.SpawnHalfLength = MouthMist.HalfLength;
                foam.SpawnHalfDepth = MouthMist.HalfDepth;
                foam.InitialVelocity = new Vector3(downstream.X * 0.8f, 0.3f, downstream.Z * 0.8f);
            }

            if (splash != null)
            {
                // Born low in the cloud and thrown up and downstream at the speed that buys SplashRise of climb: the same
                // fractions of the box the pool uses, so the crest lands in the vapour instead of over it. FloorY still
                // buries any droplet that reaches the sea.
                splash.WorldOrigin = seat;
                splash.SpawnAxis = boxAxis;
                splash.SpawnHalfLength = MouthMist.HalfLength;

                // Capped, unlike the other two: the droplet budget is the pool's, and spread over the whole lid it thins
                // to confetti. A metre and a bit of depth keeps it a crest of spray - and a narrower box still narrows it.
                splash.SpawnHalfDepth = MathF.Min(MouthMist.HalfDepth, 0.6f);
                splash.InitialVelocity = new Vector3(
                    downstream.X * 1.5f,
                    MathF.Sqrt(2f * -splash.Acceleration.Y * SplashRise * MathF.Max(MouthMist.WorldHeight, 0.2f)),
                    downstream.Z * 1.5f);
                splash.FloorY = SeaY;
            }
        }

        // Particles are self-lit and never sample the scene, so the authored colours are scaled by the sun here or the
        // spray would stay noon-white at night. Splash and foam reuse the pool's colours outright: same material, two
        // places.
        float light = WaterfallPool.ParticleLighting(App.Instance.DayPhase);
        if (mist != null)
            WaterfallPool.ApplyParticlesLighting(mist, SprayColor, SprayMid, SprayEnd, light);
        if (splash != null)
            WaterfallPool.ApplyParticlesLighting(
                splash, WaterfallPool.SplashColor, WaterfallPool.SplashMid, WaterfallPool.SplashEnd, light);
        if (foam != null)
            WaterfallPool.ApplyParticlesLighting(
                foam, WaterfallPool.FoamColor, WaterfallPool.FoamMid, WaterfallPool.FoamEnd, light);

        bool result = false;

        // Frame delta clamped exactly like the pool's: a long stall must not jump the whole phase field past one lifetime.
        float delta = float.IsFinite(time) ? Math.Clamp(time, 0f, 0.1f) : 0f;

        // All three systems spawn off the box, so one gate covers them. An unplaced box has no usable spawn face, and the
        // alternative is the old "no origin means follow the camera" behaviour - a cloud bursting at the world origin or
        // under the player's feet while the course is still being built.
        bool on = Enable && Alpha > 0f && MouthMist.Placed;
        for (int i = 0; i < systems.Count; i++)
        {
            systems[i].Enabled = on;
            if (on)
                systems[i].Advance(delta);
        }

        // The box is a control in its own right, and Panel.Update does not recurse: without this its world matrix never
        // reaches the backend and selecting it would draw no highlight.
        if (MouthMist.Update(time))
            result = true;

        return result;
    }

    /// <summary>
    /// Registers one particle system, keeps it in the tick list and returns it, or disposes it and returns null when the
    /// backend declines. Same contract as <see cref="WaterfallPool"/>: an Initialize the backend rejected has already torn
    /// its own resources down, so there is nothing left to do but drop the reference.
    /// </summary>
    ParticleSystemEffect? Register(ParticleSystemEffect effect)
    {
        if (Season.Rendering.FrameSchedule.RegisterCompute(Season.Basic.Graphics.Instance, effect))
        {
            systems.Add(effect);
            return effect;
        }

        effect.Dispose();
        return null;
    }

    /// <summary>
    /// The estuary's mist: a solid white cloud, not a wisp. The authored box is 12 x 5 m at its foot, so the density had to
    /// come up with it - 1024 x 0.95 is roughly 970 puffs, one every 0.25 m of that footprint, while a puff is 0.45 m across
    /// at birth and 1.25 m at death. Any point in the cloud is therefore behind five or six puffs, which is what "solid" means
    /// here: the body reads as one mass of spray rather than a countable row of balls.
    ///
    /// <para>Noise is down to 0.35 for the same reason the waterfall's foam keeps its own low: more than that shreds every
    /// edge and puts the point cloud back. Alpha sits at 0.10 (<see cref="SprayColor"/>) because it now stacks that deep.</para>
    ///
    /// <para>Lifetime 3.4 s is the time a puff needs to climb the height its box gives it, which is what ties the vertical
    /// speed to the cascade's own height; see <see cref="WaterfallPool.RiseSpeed"/>.</para>
    /// </summary>
    ParticleSystemEffect CreateMouthMist() => new(1024)
    {
        ExternalTime = true,
        CameraWindow = false,
        Seed = 0x52495601,   // "RIV\u0001"
        Density = 1, // 0.95f,
        SpawnRect = true,
        Lifetime = 3.4f,
        Acceleration = new Vector3(0f, 0.16f, 0f),
        WindVelocity = SprayWind,
        ConeJitter = 0.22f,
        ParticleSize = 0.45f,
        SizeEnd = 1.25f,
        Color = SprayColor,
        ColorMid = SprayMid,
        ColorEnd = SprayEnd,
        FadeIn = 0.15f,
        FadeOut = 0.45f,
        Additive = false,
        CurveMode = true,
        NoiseAmount = 0.35f,
        Turbulence = 0.14f,
        ViewDistance = 0f,
    };

    /// <summary>
    /// The estuary's splash: the waterfall pool's numbers copied over, with its own seed so the two sprays never burst in
    /// lockstep. Thrown up and out along the mouth at 3.5 m/s against -9.8 gravity over 0.9 s, stretched along its own
    /// motion, and killed by DieOnFloor if it ever falls back through the sea.
    ///
    /// <para>Capacity is a third higher than the pool's because the spray is a crest across a 12 m mouth rather than a
    /// line at one foot, and the droplets are spread over the lid's whole length.</para>
    /// </summary>
    ParticleSystemEffect CreateMouthSplash() => new(2048)
    {
        ExternalTime = true,
        CameraWindow = false,
        Seed = 0x52495602,
        Density = 0.8f,
        SpawnRect = true,
        Lifetime = 0.9f,
        Acceleration = new Vector3(0f, -9.8f, 0f),
        ConeJitter = 0.42f,
        SpeedJitter = 0.20f,
        DieOnFloor = true,
        Stretch = 0.04f,
        ParticleSize = 0.05f,
        SizeEnd = 0.02f,
        Color = WaterfallPool.SplashColor,
        ColorMid = WaterfallPool.SplashMid,
        ColorEnd = WaterfallPool.SplashEnd,
        FadeIn = 0.05f,
        FadeOut = 0.30f,
        Additive = false,
        CurveMode = true,
        NoiseAmount = 0.3f,
        Turbulence = 0.15f,
        ViewDistance = 0f,
    };

    /// <summary>
    /// The estuary's foam: the whitest stroke in the picture, born on the mist box's bottom face so one object moves both
    /// clouds - the same coupling the pool has, where pulling the box widens the band and moving it drags the foam along.
    /// 768 x 0.80 is about 610 puffs on that face, 0.36 m at birth and 0.92 m at death, so they press into one continuous
    /// band instead of a row of dots, and ToonEdge 0.30 clips the radial falloff to a clean round edge - soap bubbles rather
    /// than grey smoke.
    /// </summary>
    ParticleSystemEffect CreateMouthFoam() => new(768)
    {
        ExternalTime = true,
        CameraWindow = false,
        Seed = 0x52495603,
        Density = 0.80f,
        SpawnRect = true,
        Lifetime = 1.6f,
        Acceleration = new Vector3(0f, 0.08f, 0f),
        SpeedJitter = 0.3f,
        ParticleSize = 0.36f,
        SizeEnd = 0.92f,
        Color = WaterfallPool.FoamColor,
        ColorMid = WaterfallPool.FoamMid,
        ColorEnd = WaterfallPool.FoamEnd,
        FadeIn = 0.08f,
        FadeOut = 0.32f,
        Additive = false,
        CurveMode = true,
        NoiseAmount = 0.35f,
        Turbulence = 0.25f,
        ToonEdge = 0.30f,
        ViewDistance = 0f,
    };

    /// <summary>Arc length of a control point polyline, i.e. the phase advance a stretch contributes to its successor.</summary>
    static float PolylineLength(ReadOnlySpan<WaterRibbonPoint> points)
    {
        float total = 0f;
        for (int i = 1; i < points.Length; i++)
            total += Vector3.Distance(points[i - 1].Position, points[i].Position);
        return total;
    }
}
