// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Engine.Panels;

/// <summary>
/// Square stone observation tower on the open lawn southeast of the spawn.
///
/// Every size is measured in player models, and the player model is exactly 1m wide, 1m tall, and 0.5m deep:
/// the deck is 3x3 players (3m x 3m) at 20 player heights (20m), the single flight is 3 players wide (3m) so it
/// fills one whole deck flank, and the railing is half a player tall (0.5m).
///
/// That leaves one simple mass: a square shaft with three plain vertical faces and one straight flight on the
/// fourth. The flight runs east-west, perpendicular to the north-south shoreline, so it keeps a constant distance
/// from the sea, and the climber keeps the sea to the side instead of having the shaft in front of them.
///
/// Layout follows Room and Pond: everything is described in tower-local coordinates whose origin is the deck
/// center on the ground (north = +Z, east = +X), and Update maps it to world space through PosX/PosY/PosZ.
/// Nothing is scaled, because every dimension is measured against the player.
///
/// Draw calls: each part is one Mesh3D with exactly one Surface, and that Surface is a merge of every primitive in
/// the part. A flight is 80 steps; adding one Surface per face the way Pond does would cost hundreds of draw calls,
/// so <see cref="BoxBuilder"/> accumulates the faces of each primitive into a single indexed buffer.
///
/// Beacon: a slim mast stands on the deck center, half a player wide and two player heights tall, carrying a
/// searchlight. It burns around the clock and throws a shallow beam out onto open water, swinging symmetrically
/// about due east, so the tower doubles as a navigation light. Two pieces make it read: a spot light whose pool is
/// the ellipse the cone cuts out of the sea, and a translucent cone along the same axis standing in for the light
/// the air scatters. Both are driven off one pair of angles, so the drawn beam and the light hiding behind it can
/// never drift apart.
///
/// The lit patch is the part worth understanding, because it is not a disc. A cone meeting a plane cuts a conic
/// section, and which one depends on how the beam's dip compares with its half angle: dip steeper than the half
/// angle and the section closes into an ellipse, equal and it is a parabola, shallower and the beam never leaves the
/// water and the section is a hyperbola running to the horizon. Here the axis dips eight degrees and the cone half
/// angle is six, so the pool is a long narrow ellipse whose far end sits where the beam's *upper* edge finally
/// grazes the water. It is exactly as wide where the axis lands as the round pool it replaces, and what changes is
/// that it carries on for some six hundred more metres instead of stopping there. See the Beacon constants for the
/// numbers.
///
/// Nothing in the beacon reads the time of day any more: it burns continuously, and the swing runs on its own phase
/// integrated from the frame delta. One external dependency is worth knowing before tuning: the lit pool needs the
/// sea on its ordinary PBR path, which <c>SeaMode.Static</c> and <c>Swell</c> both are, because the dynamic water
/// pass lights itself from the sun alone and would simply ignore the searchlight. The haze cone does not care either
/// way, and it is the sea's own depth that hides whatever part of the cone dips below the waterline.
///
/// Collision split, the same one Pond uses for its rim and Room for its stairs:
/// - The shaft, deck slab, and flight are floor heights through <see cref="FloorHeightAtWorld"/>.
///   The deck height also makes the shaft unwalkable from the grass: a move whose target floor rises more than
///   PlayerCollider.StepLimit is retracted, so a player on the grass stops at the shaft face. The same rule keeps
///   a player from climbing the side of the flight, whose stone mass rises far more than one step.
/// - Railings and stair parapets are thin world obstacles and never floor boxes. FloorHeightUnder takes the
///   maximum of five samples across the footprint, so a railing in the floor data would lift a player who merely
///   turns next to it onto the railing top. As obstacles they block the crossing instead.
/// </summary>
internal class Tower : Panel
{
    // Deck: the walkable area is exactly 3x3 players, and the railing rim sits on the slab outside it.
    const float DeckTop = 20f;
    const float SlabThickness = 0.35f;
    const float UsableHalf = 1.5f;
    const float RailThick = 0.15f;
    const float RailHeight = 0.5f;
    const float DeckHalf = UsableHalf + RailThick;      // 1.65, slab edge; the railings are flush with it.

    // Shaft. It shares the slab footprint, so the flight can be exactly as wide as the flank it hangs on.
    const float ShaftHalf = DeckHalf;                   // 1.65
    const float CorniceHalf = ShaftHalf + 0.12f;        // 1.77
    const float CorniceHeight = 0.15f;
    const float CorniceTop = DeckTop - SlabThickness;   // 19.65, underside of the deck slab
    const float ShaftTop = CorniceTop - CorniceHeight;  // 19.5
    const float BeltBottom = 9.6f;
    const float BeltTop = 9.8f;
    const float BeltHalf = ShaftHalf + 0.06f;

    // Flight. One straight run on the west face: a 3m tread plus a 0.15m shoulder on each side for the parapets,
    // so the whole assembly is exactly as wide as the shaft. The riser stays under PlayerCollider.StepLimit
    // (0.3m), so the player walks it up and down without jumping.
    const int StepCount = 80;
    const float Riser = DeckTop / StepCount;            // 0.25
    const float Tread = 0.35f;
    const float FlightLength = StepCount * Tread;       // 28
    const float RunEndX = -DeckHalf;                    // -1.65, the deck slab face
    const float RunStartX = RunEndX - FlightLength;     // -29.65
    const float TreadHalfZ = UsableHalf;                // 1.5, walkable tread between the shoulders
    const float StairHalfZ = DeckHalf;                  // 1.65, shoulder outer face, flush with the shaft

    // The flight is split into several controls so that one GI proxy never spans the whole 28 x 20m mass.
    const int FlightChunkCount = 4;
    const int StepsPerChunk = StepCount / FlightChunkCount;

    // -- Beacon: slim mast on the deck center plus the searchlight it carries. --
    // Half a player wide and two player heights tall, as asked. The widest ring, the collar under the housing, is
    // 0.58m across, which still leaves 1.2m of deck to each railing for the player to walk the rim.
    const float PostRadius = 0.25f;
    const float PostHeight = 2f;
    const float PostTop = DeckTop + PostHeight;         // 22, top of the mast and mouth of the beam
    const float LampRadius = 0.34f;
    const float LampHeight = 0.3f;
    const int PostSegments = 20;

    // The sea sits at y=-3 (see Sea.cs). A shallow 8 degree dip off the lamp axis travels 181m down the beam and
    // lands it on open water 179m out, against the 100m a 14 degree dip reached. That is the whole point of the
    // angle: far enough out to read as a lighthouse reaching toward the horizon instead of lighting the rocks at
    // its foot. It is also what sets the shape of the pool, since the dip has to stay steeper than the cone.
    const float SeaLevel = -3f;
    const float BeamPitch = 8f * MathF.PI / 180f;
    const float LampAxis = PostTop + LampHeight * 0.5f;                                 // 22.15, beam origin
    static readonly float LampDrop = LampAxis - SeaLevel;                               // 25.15, lamp above water

    // Half angle of the cone, and here the angle itself is a hard limit rather than a taste setting. A cone meeting
    // a plane cuts a conic section: with the dip steeper than the half angle the section closes into an ellipse,
    // equal makes it a parabola, and shallower makes it a hyperbola, which is lit water running all the way to the
    // horizon. Six degrees against an eight degree dip is the widest cone that still closes, and the two degrees of
    // margin left over are exactly what stretches the pool into a long narrow ellipse instead of a round disc.
    const float BeamConeHalf = 6f * MathF.PI / 180f;
    const float BeamConeInner = 3.5f * MathF.PI / 180f;

    // The two stations where the cone's own surface crosses the water, measured from the lamp along the two edge
    // generators. The lower one, dipping at pitch plus half angle, dives in first and marks the near edge of the
    // pool; the upper one, grazing at pitch minus half angle, is what finally lifts out of the water and marks the
    // far edge. Everything between them is lit, and their ratio is the shape: 104m to 721m from a lamp whose axis
    // only touches the sea at 181m. So the pool is a band about 619m long and 57m wide through its middle.
    //
    // Its width at the station where the axis lands is worth pausing on, because it is exactly the diameter of the
    // circle the cone cuts perpendicular to itself there, and for a plain reason: the sea and that cross-section
    // plane share the cross-track direction, and the sea runs through the circle's centre. So a round-pool model
    // gets that one chord right to the centimetre and is wrong about everything past it, which is how a 38m disc
    // stood in for a 619m band for two revisions without anything in the numbers objecting.
    static readonly float BeamSkim = LampDrop / MathF.Sin(BeamPitch - BeamConeHalf);

    // The drawn haze runs the full length of the lit ellipse, stopping at whichever comes first: the far edge of the
    // pool, or the edge of the world's water, which is a 1400m plate centred on the origin (see Sea.cs) and so runs
    // out about 652m horizontally from the tower. Ending the cone on the waterline is the whole trick: the sea is
    // opaque and writes depth, so the submerged half of the cone is clipped by the sea itself and the beam appears
    // to bury itself in the water rather than being cut by a disc standing in mid air.
    static readonly float BeamHaze = MathF.Min(BeamSkim, 640f);
    static readonly float BeamEndRadius = BeamHaze * MathF.Tan(BeamConeHalf);
    const float BeamOriginRadius = LampRadius * 0.9f;
    const int BeamSegments = 24;

    // Symmetric sweep, plus or minus 30 degrees about due east, so the beam turns around at equal angles either
    // side of head-on. It deliberately no longer tracks the rock layout: matching that put the turnarounds at
    // -19 and +64 degrees, which reads as a beam that swings wide going north and snaps back going south.
    const float BeaconSweepHalf = 30f * MathF.PI / 180f;

    // One full back-and-forth takes this long. Cosine easing makes the beam linger at both ends and hurry across
    // the middle, which is what reads as a searchlight rather than a metronome.
    const float BeaconSweepSeconds = 16f;

    // Spot intensity follows the engine convention radiance = colour * intensity / d^2, divided by a cutoff window
    // that saturates away at Range. The range is the far edge of the pool rather than twice the axis throw, because
    // it is the ellipse and not the axis that the light has to cover: a window closing at 361m cut the lit band in
    // half at the waterline and left the far two thirds of the beam dark water. Stretching it to 721m barely moves
    // the middle of the pool, since the window is still 0.99 where the axis lands, so intensity carries the
    // brightness outright: 9.1 at the axis against the 5.4 of the previous tuning and the 2.5 before that.
    const float BeaconIntensity = 300000f;
    static readonly float BeaconRange = BeamSkim;

    // Peak opacity of the scattered haze, at the lamp end. It feeds a ramp that falls to nothing at the far rim
    // (see BeamRampExponent), because a cone this long has to thin out: at the far end it is 135m across, and a
    // uniform opacity there would put a milky wedge over the whole coast. Even at the peak the shell is doubled on
    // any view straight across it, so what the eye gets is roughly twice this number near the lamp.
    const float BeamAlpha = 0.06f;

    // S1: analytic in-scatter gain used only by secondary views (rear mirror / minimap). The volumetric fog that
    // gives the beam its brightness in the main view is frame-global and indexed by the primary camera screen,
    // so it is deliberately disabled on any non-main view (see Pipeline.BindSceneFog / DXSceneDrawCollection.ViewHasFog).
    // Without this fallback the mirror sees a bare 6 % albedo film instead of a light shaft, which is what read as
    // "black beam at night, refraction window by day". The value is emissiveFactor.rgb in the shader; the unlit
    // branch on the secondary view adds it once per pass, so at BeamAlpha = 0.06 a peak of BeamColor × gain lands
    // as ~0.06 × gain HDR above the background. Sixty puts the near-lamp core around sRGB 235 by night and about
    // 30 sRGB above the sky by day, which matches the volumetric look closely enough that the two views agree.
    const float BeamScatterGain = 60f;

    // How fast the haze thins with distance, as the exponent of a straight fall from one at the lamp to zero at the
    // far rim. One would be a linear taper and leave the middle of the beam as visible as the near end; real
    // scattering drops faster than that against a dark sky, and it is the drop that stops this reading as a solid.
    const float BeamRampExponent = 1.5f;
    const int BeamRampSize = 64;

    static readonly Vector4 ShaftColor = new Vector4(0.62f, 0.57f, 0.49f, 1f);
    static readonly Vector4 CorniceColor = new Vector4(0.50f, 0.46f, 0.40f, 1f);
    static readonly Vector4 DeckColor = new Vector4(0.72f, 0.68f, 0.60f, 1f);
    static readonly Vector4 RailColor = new Vector4(0.56f, 0.52f, 0.46f, 1f);
    static readonly Vector4 MastColor = new Vector4(0.34f, 0.35f, 0.37f, 1f);

    // The housing is painted sheet metal, not a bulb: an ordinary opaque surface lit by whatever is around it, so
    // at night it goes dark and the lamp inside never shows. Only its light and the haze it scatters are visible.
    static readonly Vector4 LampColor = new Vector4(0.68f, 0.66f, 0.62f, 1f);

    // Strong cold white, xenon rather than sodium. A warm beam reads as firelight; the point here is a navigation
    // searchlight, so both the light and the haze it scatters sit at the cool end and the intensity carries the
    // punch instead of the colour.
    static readonly Vector4 BeamColor = new Vector4(0.88f, 0.93f, 1f, 1f);
    static readonly Vector3 BeaconLightColor = new Vector3(0.96f, 0.98f, 1f);

    // Tower-local box of every part, remapped to world space by Update.
    readonly List<(Mesh3D Part, Vector3 Center, Vector3 Size)> parts = new();

    /// <summary>
    /// Thin railings and stair parapets. App registers them as movement obstacles: they block the crossing but are
    /// never floor heights, see the class remarks.
    /// </summary>
    internal readonly List<Mesh3D> Blockers = new();

    /// <summary>Large visible parts for OcclusionFade. Blockers are already faded through the obstacle list.</summary>
    internal readonly List<Mesh3D> Occluders = new();

    // Beacon rig. The mast and the housing are ordinary parts, so the Update loop places them with everything else;
    // the housing is plain sheet metal with no light of its own, because the lamp inside it is meant to stay
    // invisible. The haze cone is driven directly instead, since its transform is rebuilt every frame. Neither the
    // housing nor the cone is an obstacle or belongs to the occlusion-fade set.
    Mesh3D beaconBeam;
    Season.Rendering.LightSource beaconLight;

    // Sweep phase, integrated from the frame delta. See UpdateBeacon for why it is not read off App.Time.
    float beaconClock;

    internal Tower()
    {
        AddPart("TowerShaft", BuildShaft(), ShaftColor, blocks: false);
        AddPart("TowerCornice", BuildCornice(), CorniceColor, blocks: false);
        AddPart("TowerDeck", BuildDeck(), DeckColor, blocks: false);

        for (int i = 0; i < FlightChunkCount; i++)
        {
            AddPart($"TowerStair{i}", BuildFlight(i), ShaftColor, blocks: false);
        }

        // Parapets on the two stair shoulders. Both run the whole flight and stop at the deck slab face, where the
        // deck railing takes over, so the guard line is continuous from the grass to the deck.
        AddPart("TowerParapetPosZ", BuildParapet(TreadHalfZ, StairHalfZ), RailColor, blocks: true);
        AddPart("TowerParapetNegZ", BuildParapet(-StairHalfZ, -TreadHalfZ), RailColor, blocks: true);

        // Deck railing on the slab rim: closed on the three plain faces and wide open on the west face where the
        // flight arrives. That single 3m opening is the entire stair mouth.
        AddPart("TowerRailPosZ", Rail(-DeckHalf, DeckHalf, UsableHalf, DeckHalf), RailColor, blocks: true);
        AddPart("TowerRailNegZ", Rail(-DeckHalf, DeckHalf, -DeckHalf, -UsableHalf), RailColor, blocks: true);
        AddPart("TowerRailPosX", Rail(UsableHalf, DeckHalf, -UsableHalf, UsableHalf), RailColor, blocks: true);

        // Beacon in the middle of the deck. The mast is solid, so the player walks around it rather than through it;
        // the housing is an ordinary opaque shell that holds nothing visible, so it neither blocks movement nor fades.
        AddPart("TowerBeaconMast", BuildBeaconMast(), MastColor, blocks: true);
        AddPart("TowerBeaconLamp", BuildBeaconLamp(), LampColor, blocks: false, occludes: false);
        beaconBeam = BuildBeaconBeam();

        // Registered once and then only retuned each frame, the same lifecycle the torch and the street lamp use.
        // Priority sits above the shadow-casting interior lamps because the GPU takes eight lights and this one is
        // the whole point of the rig; without it the beacon would be trimmed away. It starts shut so a frame where
        // the panel has not been placed yet cannot put a lamp at the world origin, and UpdateBeacon opens it.
        beaconLight = App.Instance.Lighting.Add(new Season.Rendering.LightSource
        {
            Name = "TowerBeacon",
            Kind = Season.Rendering.LightKind.Spot,
            Color = BeaconLightColor,
            Intensity = 0f,
            Range = BeaconRange,
            InnerConeAngle = BeamConeInner,
            OuterConeAngle = BeamConeHalf,
            CastShadows = false,
            IsOpen = false,
            Priority = 600,
        });
    }

    /// <summary>
    /// Walkable height in world Y at a world-space XZ point: the deck or a step top, otherwise 0 for the grass.
    /// The flight is solved from the run coordinate instead of searching 80 boxes. Same contract as
    /// Room.FloorHeightAtWorld and Pond.FloorHeightAtWorld.
    /// </summary>
    internal float FloorHeightAtWorld(float worldX, float worldZ)
    {
        float x = worldX - PosX, z = worldZ - PosZ;
        float ax = MathF.Abs(x), az = MathF.Abs(z);

        // Deck slab, which also carries the railing rim.
        if (ax <= DeckHalf && az <= DeckHalf)
            return PosY + DeckTop;

        // Flight on the west face, including the parapet shoulders so the stone mass cannot be walked into from
        // the side; the parapets themselves stand on those shoulders as obstacles.
        if (az <= StairHalfZ && x >= RunStartX && x <= RunEndX)
        {
            int step = (int)((x - RunStartX) / Tread);
            step = Math.Clamp(step, 0, StepCount - 1);
            return PosY + (step + 1) * Riser;
        }

        return 0f;
    }

    /// <summary>
    /// Height above which a fall off the tower would hurt, so PlayerCollider arms <see cref="SupportRatio"/>.
    /// Below it, stepping off a low step is harmless and must stay free.
    /// </summary>
    internal const float DangerHeight = 3f;

    /// <summary>
    /// Share of nine footprint samples, the center plus a ring, that stand on a tower surface, deck or step.
    ///
    /// PlayerCollider compares this before and after a move and rejects any move that lowers it while the feet are
    /// above <see cref="DangerHeight"/>. The railings are thin obstacles rather than floor boxes, and Sweep never pushes
    /// an already overlapping footprint back, so turning next to a railing can leave the footprint embedded in it.
    /// Without this guard that turns into a 20m drop the moment the embedded footprint walks out past the edge;
    /// with it, the player can always back away but never slide further out. Samples only need to be on some part of
    /// the tower, not above the danger height, otherwise walking down the lower steps would read as losing support.
    /// </summary>
    internal float SupportRatio(in Vector3 center, in Vector3 half)
    {
        int hits = 0;

        for (int i = -1; i <= 1; i++)
        {
            for (int j = -1; j <= 1; j++)
            {
                float x = center.X + i * half.X * 0.9f;
                float z = center.Z + j * half.Z * 0.9f;

                if (FloorHeightAtWorld(x, z) > 0f)
                    hits++;
            }
        }

        return hits / 9f;
    }

    /// <summary>
    /// Plain shaft with a shallow belt course at mid height, which gives the 19.5m face a sense of scale.
    /// </summary>
    static BoxBuilder BuildShaft()
    {
        var box = new BoxBuilder();
        box.Add(new Vector3(-ShaftHalf, 0f, -ShaftHalf), new Vector3(ShaftHalf, ShaftTop, ShaftHalf));
        box.Add(new Vector3(-BeltHalf, BeltBottom, -BeltHalf), new Vector3(BeltHalf, BeltTop, BeltHalf));
        return box;
    }

    /// <summary>Overhanging band just under the deck, one continuous ring around the three plain faces and the flight.</summary>
    static BoxBuilder BuildCornice()
    {
        var box = new BoxBuilder();
        box.Add(new Vector3(-CorniceHalf, ShaftTop, -CorniceHalf), new Vector3(CorniceHalf, CorniceTop, CorniceHalf));
        return box;
    }

    /// <summary>Deck slab. Its footprint is the shaft footprint, so the slab edge is directly above the wall face.</summary>
    static BoxBuilder BuildDeck()
    {
        var box = new BoxBuilder();
        box.Add(new Vector3(-DeckHalf, CorniceTop, -DeckHalf), new Vector3(DeckHalf, DeckTop, DeckHalf));
        return box;
    }

    /// <summary>
    /// One chunk of the flight. Each step is solid from the grass up, like Room.BuildStairParts and the Pond steps,
    /// so the flight reads as a stone mass against the shaft rather than floating treads. The chunk stays tight
    /// enough to be a usable GI proxy while the shared surface keeps it at one draw call.
    /// </summary>
    static BoxBuilder BuildFlight(int chunk)
    {
        var box = new BoxBuilder();
        int first = chunk * StepsPerChunk;

        for (int i = first; i < first + StepsPerChunk; i++)
        {
            float a = RunStartX + i * Tread;
            float top = (i + 1) * Riser;

            box.Add(new Vector3(a, 0f, -StairHalfZ), new Vector3(a + Tread, top, StairHalfZ));
        }

        return box;
    }

    /// <summary>
    /// Stepped parapet standing on a stair shoulder. Each step gets its own segment so the top follows the slope at
    /// a constant 0.5m above the tread; the last segment ends flush with the deck slab face.
    /// </summary>
    static BoxBuilder BuildParapet(float z0, float z1)
    {
        var box = new BoxBuilder();

        for (int i = 0; i < StepCount; i++)
        {
            float a = RunStartX + i * Tread;
            float top = (i + 1) * Riser;

            box.Add(new Vector3(a, top, z0), new Vector3(a + Tread, top + RailHeight, z1));
        }

        return box;
    }

    /// <summary>One straight railing run at the deck surface, from which each deck side is cut.</summary>
    static BoxBuilder Rail(float x0, float x1, float z0, float z1)
    {
        var box = new BoxBuilder();
        box.Add(new Vector3(x0, DeckTop, z0), new Vector3(x1, DeckTop + RailHeight, z1));
        return box;
    }

    /// <summary>
    /// Mast on the deck center: a plain round column with a collar under the housing, so the searchlight reads as
    /// mounted rather than balanced on the column. Round rather than square because at half a player across a box
    /// silhouette still looks like a post, while the cylinder does not.
    /// </summary>
    static BoxBuilder BuildBeaconMast()
    {
        var box = new BoxBuilder();
        box.AddCylinder(0f, 0f, DeckTop, PostTop, PostRadius, PostSegments);
        box.AddCylinder(0f, 0f, PostTop - 0.12f, PostTop, LampRadius * 0.85f, PostSegments);
        return box;
    }

    /// <summary>
    /// Housing at the mast top. It is opaque painted sheet metal on the same PBR path as the rest of the tower, so
    /// it carries no light of its own and goes dark with the night: the lamp inside is deliberately invisible. It is
    /// a normal part for placement only, and is excluded from occlusion fading because it is the light's own shell.
    /// </summary>
    static BoxBuilder BuildBeaconLamp()
    {
        var box = new BoxBuilder();
        box.AddCylinder(0f, 0f, PostTop, PostTop + LampHeight, LampRadius, PostSegments);
        return box;
    }

    /// <summary>
    /// The scattered haze: an open cone from the lamp mouth out to the far edge of the lit ellipse, drawn unlit and
    /// blended so it reads as light hanging in the air rather than as an object. Two things keep it off reading as a
    /// solid: opacity that falls to nothing along its length, so the beam dissolves instead of being cut by a rim,
    /// and the sea itself, which is opaque, writes depth, and hides the part of the cone that has already dipped
    /// under the waterline. It starts at Alpha 0 and is faded in by <see cref="UpdateBeacon"/>, and it is never an
    /// obstacle, a shadow caster, or a GI proxy.
    /// </summary>
    Mesh3D BuildBeaconBeam()
    {
        var box = new BoxBuilder();
        box.AddFrustumX(0f, BeamHaze, BeamOriginRadius, BeamEndRadius, BeamSegments);

        var beam = box.Build("TowerBeaconBeam", BeamColor, out _, out var size,
            unlit: true, mode: SurfaceBlendMode.Blend, doubleSided: true);

        // The lengthwise fade goes in through the base color slot. The blend path multiplies the sampled texel's
        // alpha into the mesh opacity and its rgb into the base color, so white texels leave the colour alone and
        // this only ever shapes how much air the light is passing through. The cone's u runs along its axis and v
        // around it, so one horizontal band of the ramp stretches over the whole length of the beam.
        beam.Surfaces[0].TextureOverride = TextureUpdateSource.FromImage(BuildBeamRamp());

        // S1: the analytic in-scatter colour the shader adds on secondary views, gated by emissiveFactor.w > 0.
        // The unlit path (renderMode==0) in every backend normally ignores emissiveFactor entirely; the secondary-
        // view branch in Pipeline.ApplySceneFog-adjacent code is the sole consumer. The primary view keeps reading
        // its brightness from the volumetric fog pass unchanged, so this has zero effect on the primary view.
        beam.Surfaces[0].EmissiveFactor = new Vector4(
            BeamColor.X * BeamScatterGain, BeamColor.Y * BeamScatterGain, BeamColor.Z * BeamScatterGain, 1f);

        beam.CastShadows = false;
        beam.ExcludeFromAo = true;
        beam.Alpha = 0f;

        // Pinned to the built size rather than left for the default settle, so the cone is already the right length
        // on the frame it first appears instead of waiting for Load to backfill Width/Height/Depth.
        beam.Width = size.X;
        beam.Height = size.Y;
        beam.Depth = size.Z;

        AddControl(beam);
        return beam;
    }

    /// <summary>
    /// The haze ramp as in-memory pixels: white at full opacity by the lamp, transparent at the far rim, falling as
    /// one minus u to BeamRampExponent in between. Square and a power of two because the same texture is read by four
    /// backends and none of them is asked to special case a one pixel tall image; the rows are duplicates, which
    /// costs 16KB and buys a texture every sampler handles without a special path.
    /// </summary>
    static NativeImageData BuildBeamRamp()
    {
        int size = BeamRampSize;
        var pixels = new byte[size * size * 4];

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                // Texel centres rather than texel edges: with clamped filtering the column nearest the rim still
                // reads faint, and it is the rim itself that has to be gone, since that is where the cone is widest
                // and a leftover opacity there would show as the edge of an object.
                float u = (x + 0.5f) / size;
                byte a = (byte)Math.Round(255f * MathF.Pow(1f - u, BeamRampExponent));

                int i = (y * size + x) * 4;
                pixels[i] = 255;
                pixels[i + 1] = 255;
                pixels[i + 2] = 255;
                pixels[i + 3] = a;
            }
        }

        return new NativeImageData(size, size, pixels);
    }

    /// <summary>
    /// Drives the beacon every frame: sweep heading, the haze cone, and the spot light behind it. The cone is only
    /// the visible half of the rig, so its axis and the light direction come off the same two angles and can never
    /// disagree with each other.
    /// </summary>
    bool UpdateBeacon(float time)
    {
        // The value handed to Update is a frame delta, not a running clock: BaseApp accumulates it into Time one
        // level up. Dividing the delta itself by the sweep period held the beam at its start heading forever, which
        // is the "the light never moves" symptom. Integrating the delta into a local phase fixes that, and unlike
        // reading App.Time it also survives a retuned period without teleporting the beam.
        beaconClock += time;

        // Wrapping is one remainder per frame and keeps the phase inside the range where a single-precision float
        // still resolves it: a week of accumulated seconds would otherwise start quantizing the heading, the same
        // reason Mesh3D wraps its swell phase.
        if (beaconClock >= BeaconSweepSeconds)
            beaconClock %= BeaconSweepSeconds;

        // Cosine easing over one full cycle: the beam slows at each turnaround, where a real searchlight dwells,
        // and crosses the middle at its fastest.
        float t = 0.5f - 0.5f * MathF.Cos(beaconClock / BeaconSweepSeconds * MathF.Tau);

        // Symmetric about due east, so the two turnarounds sit at equal angles either side of the head-on view.
        float yaw = (t * 2f - 1f) * BeaconSweepHalf;

        // Down-pitch around Z, then heading around Y. Nothing is scaled, so the matrix is a pure rotation and the
        // cone's local +X lands on the beam axis. The cone's anchor is its bounds center, halfway down that axis,
        // so Pos = mouth + axis * halfLength keeps the mouth pinned to the lamp.
        var rotation = Matrix4x4.CreateRotationZ(-BeamPitch) * Matrix4x4.CreateRotationY(-yaw);
        var axis = Vector3.TransformNormal(Vector3.UnitX, rotation);
        var mouth = new Vector3(PosX, PosY + LampAxis, PosZ);

        beaconBeam.Rotation = Quaternion.CreateFromRotationMatrix(rotation);
        beaconBeam.PosX = mouth.X + axis.X * BeamHaze * 0.5f;
        beaconBeam.PosY = mouth.Y + axis.Y * BeamHaze * 0.5f;
        beaconBeam.PosZ = mouth.Z + axis.Z * BeamHaze * 0.5f;

        // Haze density. The mesh opacity is a flat peak and the ramp texture carries the fall to nothing down the
        // beam, so the number here is only what the lamp end is worth; the 640m shell past it is already thin air.
        beaconBeam.Alpha = BeamAlpha;

        // Spot light riding the same axis from the same mouth, burning continuously. Priority sits above the
        // shadow-casting interior lamps so the navigation light keeps a GPU slot; the eight slot budget means it
        // displaces one of them for the whole day now, not just overnight.
        if (beaconLight != null)
        {
            beaconLight.IsOpen = true;
            beaconLight.Intensity = BeaconIntensity;
            beaconLight.Position = mouth;
            beaconLight.Direction = axis;
        }

        // The housing is an ordinary part, so the loop in Update already places and updates it; only the cone needs
        // its transform rebuilt here.
        return beaconBeam.Update(time);
    }

    /// <summary>
    /// Builds one part, registers it for per-frame placement, and files it under the obstacle or fade list the
    /// caller asks for. Every part on the tower is an ordinary opaque lit surface, so there is no per-part material
    /// choice beyond the colour.
    /// </summary>
    void AddPart(string name, BoxBuilder boxes, Vector4 color, bool blocks, bool occludes = true)
    {
        var part = boxes.Build(name, color, out var center, out var size);

        // Explicit DDGI albedo matching the visible stone color, as Room and Pond do for their parts.
        part.GiAlbedo = new Vector3(color.X, color.Y, color.Z);

        AddControl(part);
        parts.Add((part, center, size));

        if (blocks)
            Blockers.Add(part);
        else if (occludes)
            Occluders.Add(part);
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        // Beacon first: it writes the cone transform and the haze opacity, and the part loop below is what pushes
        // the housing onto the GPU, so keeping it ahead of the loop makes those two land in the same frame.
        if (UpdateBeacon(time))
        {
            result = true;
        }

        // Same per-frame placement as Room and Pond, without scaling: translate each part center and keep its size in meters.
        for (int i = 0; i < parts.Count; i++)
        {
            var (part, center, size) = parts[i];

            part.PosX = PosX + center.X;
            part.PosY = PosY + center.Y;
            part.PosZ = PosZ + center.Z;
            part.Width = size.X;
            part.Height = size.Y;
            part.Depth = size.Z;

            if (part.Update(time))
            {
                result = true;
            }
        }

        return result;
    }

    /// <summary>
    /// Accumulates axis-aligned boxes, upright cylinders and an open frustum into a single indexed triangle list, so
    /// a whole part costs one Surface and one draw call. Geometry is fed in tower-local meters;
    /// <see cref="Build"/> then recenters it on its own bounds, which puts the control anchor at the local origin
    /// and leaves Update with nothing but a translation to apply. The haze cone is the exception: its anchor lands
    /// on the frustum center, halfway down its axis, which is why UpdateBeacon offsets Pos by half the haze length.
    /// </summary>
    sealed class BoxBuilder
    {
        readonly List<Vertex> vertices = new();
        readonly List<ushort> indices = new();
        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);

        public void Add(Vector3 boxMin, Vector3 boxMax)
        {
            Track(boxMin, boxMax);

            var s = boxMax - boxMin;
            var sx = new Vector3(s.X, 0f, 0f);
            var sy = new Vector3(0f, s.Y, 0f);
            var sz = new Vector3(0f, 0f, s.Z);

            // Six outward faces, same construction and winding as Pond.AddBox and Room.AddRoomBox.
            AddFace(new Vector3(boxMin.X, boxMin.Y, boxMin.Z), sx, sy, new Vector3(0f, 0f, -1f));
            AddFace(new Vector3(boxMax.X, boxMin.Y, boxMax.Z), -sx, sy, new Vector3(0f, 0f, 1f));
            AddFace(new Vector3(boxMin.X, boxMin.Y, boxMax.Z), -sz, sy, new Vector3(-1f, 0f, 0f));
            AddFace(new Vector3(boxMax.X, boxMin.Y, boxMin.Z), sz, sy, new Vector3(1f, 0f, 0f));
            AddFace(new Vector3(boxMin.X, boxMax.Y, boxMin.Z), sx, sz, new Vector3(0f, 1f, 0f));
            AddFace(new Vector3(boxMin.X, boxMin.Y, boxMax.Z), sx, -sz, new Vector3(0f, -1f, 0f));
        }

        /// <summary>
        /// Upright cylinder around (cx, cz) with a closed top. Used for the beacon mast and its housing, where a box
        /// silhouette would still read as a post at half a player across. No bottom cap: both sit on something.
        /// </summary>
        public void AddCylinder(float cx, float cz, float y0, float y1, float radius, int segments)
        {
            Track(new Vector3(cx - radius, y0, cz - radius), new Vector3(cx + radius, y1, cz + radius));

            var center = new Vector3(cx, y1, cz);

            for (int i = 0; i < segments; i++)
            {
                float a0 = i * MathF.Tau / segments;
                float a1 = (i + 1) * MathF.Tau / segments;

                var b0 = Polar(cx, y0, cz, radius, a0);
                var b1 = Polar(cx, y0, cz, radius, a1);
                var t0 = Polar(cx, y1, cz, radius, a0);
                var t1 = Polar(cx, y1, cz, radius, a1);

                // One normal per quad, taken at the mid angle: flat shading is indistinguishable at this radius.
                var normal = new Vector3(MathF.Cos((a0 + a1) * 0.5f), 0f, MathF.Sin((a0 + a1) * 0.5f));
                AddTriangle(b0, t0, t1, normal);
                AddTriangle(b0, t1, b1, normal);

                // Top cap, wound so the fan faces +Y.
                AddTriangle(center, Polar(cx, y1, cz, radius, a1), Polar(cx, y1, cz, radius, a0), Vector3.UnitY);
            }
        }

        /// <summary>
        /// Open frustum along +X, from (x0, r0) to (x1, r1). No end caps: the mouth would show as a hard disc
        /// through the blended surface, and the shell is drawn double-sided. UVs are laid out for the haze ramp, u
        /// running the length of the axis and v running around it, so a texture bound to this surface reads as a
        /// fall-off with distance rather than as decoration.
        /// </summary>
        public void AddFrustumX(float x0, float x1, float r0, float r1, int segments)
        {
            float r = MathF.Max(r0, r1);
            Track(new Vector3(x0, -r, -r), new Vector3(x1, r, r));

            for (int i = 0; i < segments; i++)
            {
                float a0 = i * MathF.Tau / segments;
                float a1 = (i + 1) * MathF.Tau / segments;

                var m0 = new Vector3(x0, r0 * MathF.Cos(a0), r0 * MathF.Sin(a0));
                var m1 = new Vector3(x0, r0 * MathF.Cos(a1), r0 * MathF.Sin(a1));
                var e0 = new Vector3(x1, r1 * MathF.Cos(a0), r1 * MathF.Sin(a0));
                var e1 = new Vector3(x1, r1 * MathF.Cos(a1), r1 * MathF.Sin(a1));

                // Surface normal of the cone. The (x, angle) parameterisation gives (-dr, length * cos, length * sin)
                // up to a positive radial factor, which normalisation removes.
                var normal = ConeNormal(x1 - x0, r1 - r0, (a0 + a1) * 0.5f);

                // Four corners on one quad, two per ring, so the interpolation across it is linear in the axial
                // fraction: exactly the quantity the ramp texture was drawn against.
                ushort b = (ushort)vertices.Count;
                float v0 = i / (float)segments;
                float v1 = (i + 1) / (float)segments;

                vertices.Add(MakeVertex(m0, new Vector2(0f, v0), normal));
                vertices.Add(MakeVertex(m1, new Vector2(0f, v1), normal));
                vertices.Add(MakeVertex(e1, new Vector2(1f, v1), normal));
                vertices.Add(MakeVertex(e0, new Vector2(1f, v0), normal));

                indices.Add(b);
                indices.Add((ushort)(b + 1));
                indices.Add((ushort)(b + 2));
                indices.Add(b);
                indices.Add((ushort)(b + 2));
                indices.Add((ushort)(b + 3));
            }
        }

        static Vector3 Polar(float cx, float y, float cz, float radius, float angle)
            => new Vector3(cx + radius * MathF.Cos(angle), y, cz + radius * MathF.Sin(angle));

        static Vector3 ConeNormal(float length, float deltaRadius, float angle)
            => Vector3.Normalize(new Vector3(-deltaRadius, length * MathF.Cos(angle), length * MathF.Sin(angle)));

        void Track(Vector3 boxMin, Vector3 boxMax)
        {
            min = Vector3.Min(min, boxMin);
            max = Vector3.Max(max, boxMax);
        }

        void AddTriangle(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 normal)
        {
            ushort b = (ushort)vertices.Count;

            vertices.Add(MakeVertex(p0, new Vector2(0f, 0f), normal));
            vertices.Add(MakeVertex(p1, new Vector2(1f, 0f), normal));
            vertices.Add(MakeVertex(p2, new Vector2(0f, 1f), normal));

            indices.Add(b);
            indices.Add((ushort)(b + 1));
            indices.Add((ushort)(b + 2));
        }

        void AddFace(Vector3 origin, Vector3 u, Vector3 v, Vector3 normal)
        {
            ushort b = (ushort)vertices.Count;

            vertices.Add(MakeVertex(origin, new Vector2(0f, 1f), normal));
            vertices.Add(MakeVertex(origin + u, new Vector2(1f, 1f), normal));
            vertices.Add(MakeVertex(origin + v, new Vector2(0f, 0f), normal));
            vertices.Add(MakeVertex(origin + u + v, new Vector2(1f, 0f), normal));

            indices.Add((ushort)(b + 2));
            indices.Add((ushort)(b + 3));
            indices.Add((ushort)(b + 1));
            indices.Add((ushort)(b + 2));
            indices.Add((ushort)(b + 1));
            indices.Add(b);
        }

        public Mesh3D Build(string name, Vector4 color, out Vector3 center, out Vector3 size,
            bool unlit = false, SurfaceBlendMode mode = SurfaceBlendMode.Opaque, bool doubleSided = false)
        {
            center = (min + max) * 0.5f;
            size = max - min;

            var verts = vertices.ToArray();
            for (int i = 0; i < verts.Length; i++)
                verts[i].Position -= center;

            var part = new Mesh3D()
            {
                Name = name,
                CullingEnabled = false,
            };

            part.Surfaces.Add(new Surface
            {
                Vertices = verts,
                Indices = indices.ToArray(),
                BaseColor = color,
                MetallicFactor = 0f,
                RoughnessFactor = 0.85f,
                // Written out because Surface.Unlit defaults to true, which would flatten every stone part to its
                // base color; only the haze cone asks for it.
                Unlit = unlit,
                Mode = mode,
                DoubleSided = doubleSided,
            });

            return part;
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
    }
}
