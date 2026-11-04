// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Engine.Panels;

/// <summary>
/// The viking boat (<c>Assets/low_poly_viking_boat_2.glb</c>), berthed at the pond's north edge bow-first against
/// the stair the player comes down from, and the two states the game drives it through. It has no controller of
/// its own on purpose: the hull only moves while the player is aboard, so this panel owns the pose, the clip, and
/// the water it is allowed on, while <see cref="Steer"/> is the single entry the input path calls.
///
/// <para><b>Two states, one model.</b> Moored, the boat does not play its clip: the hull only breathes on
/// the water through three low-frequency oscillators - heave on Y, pitch about the boat's own X, and a
/// smaller roll about its Z. Rowing, the glTF clip loops (its two channels are morph-target weights that
/// swing the oars and fill the sail) and the same oscillators keep running at a reduced amplitude, because
/// a moving hull still sits on the same water. Stopping rewinds the pose to the clip's first sampled frame
/// through <see cref="Season.Models.GltfAsset.StopAndRest"/> instead of freezing it mid-stroke.</para>
///
/// <para><b>Why the hull is a subclass.</b> <see cref="Season.Controls.Model"/> rotates about Y only: its
/// <c>Rotation</c> is a yaw and <c>GetRotationMatrix</c> is <c>CreateRotationY</c>. Listing is two more
/// axes, and this model has no animation that produces them (the clip only drives morph weights, not
/// nodes), so the rocking has to be code. <see cref="BoatHull"/> overrides the same injection point with
/// yaw x pitch x roll; the anchor, the world matrix, picking and the shadow box all read that one matrix,
/// so what is drawn and what is tested cannot disagree.</para>
///
/// <para><b>The size is a bridge constraint, not a taste call.</b> The bridge's arch soffit sits at 1.96 m
/// in world space (the central band's lowest vertex in Bridge.glb is 0.0507 local, carried through the
/// 3.2 m tall span, see Bridge.ModelCenterY), i.e. 1.62 m above the river's 0.34 m surface. The hull is
/// scaled so its highest point rides 1.40 m above the water line, leaving about 0.2 m under the arch even
/// at the top of a heave. Lengthening past the authored 3.4 m, or removing the vertical compression
/// (<see cref="Height"/> 1.70 m against the 2.21 m that an even scale from <see cref="Length"/> would
/// give), puts the sail into the masonry.</para>
///
/// <para><b>Travel range.</b> <see cref="Waterway"/> holds the water the boat is allowed on: the pond
/// rectangle, the branch and main courses as a centerline corridor, and the open sea past the coast. The
/// corridor is built from <see cref="River.NavigableCenterline"/>, i.e. the very control points the
/// channel is drawn from, so it cannot drift from the water it describes. Driving enters through
/// <see cref="Board"/> and <see cref="Steer"/>: the player is bound to <see cref="SeatPosition"/> and every
/// steering request is checked against that water before the hull commits to it.</para>
///
/// <para><b>The open sea is not a plane.</b> Past the coast the level comes from <see cref="Sea.SurfaceY"/>, i.e. the
/// surface the sea actually draws, and <see cref="Update"/> samples that same surface under the bow, the stern and
/// both beams to ride its slope. Without it a hull pinned to the mean level sits buried under every crest offshore,
/// where the baked relief runs to several metres.</para>
/// </summary>
internal sealed class Boat : Panel
{
    /// <summary>The viking boat model: one morph-target clip that swings the oars and fills the sail.</summary>
    internal const string GlbPath = @"Assets/low_poly_viking_boat_2.glb";

    // -- Hull box, in meters. The asset's raw box is 6.98 x 7.58 x 11.67 local units on X / Y / Z, and
    //    its bow-stern axis is Z. Height is deliberately compressed; see the class summary. --

    /// <summary>Bounding-box length along the model's Z axis, the bow-stern line.</summary>
    const float Length = 3.4f;

    /// <summary>Bounding-box width along X, kept at the even scale: <c>6.98 * (3.4 / 11.67)</c>.</summary>
    const float Beam = 2.03f;

    /// <summary>Bounding-box height including the mast. An even scale would give 2.21 m and hit the bridge.</summary>
    const float Height = 1.70f;

    /// <summary>
    /// Height of the bounding-box centre above the water line. At <see cref="Height"/> the box reaches
    /// +/-0.85 m from that centre, so the hull's lower band (local y = -1.5) floats 14 cm under the surface
    /// and the oar tips, the lowest points of the model, sit 30 cm down. That is inside the pond's 40 cm of
    /// water; in the river's 22 cm channel the tips graze the bed by a few centimetres, where the clip's own
    /// motion is what the eye reads anyway.
    /// </summary>
    const float RideHeight = 0.55f;

    // -- Moored rocking: two frequencies per axis so the cycle cannot read as a metronome, all amplitudes
    //    small enough that the gunwale never dips. Radians for the two rotations. --
    const float HeaveAmpA = 0.040f, HeaveAmpB = 0.014f;
    const float PitchAmpA = 0.022f, PitchAmpB = 0.009f;
    const float RollAmpA = 0.030f, RollAmpB = 0.012f;

    /// <summary>A few tenths of a degree of heading wander, the same water moving the bow around.</summary>
    const float YawWobble = 0.004f;

    /// <summary>Seconds for the moored/rowing blend to cross; long enough to cover the clip switch.</summary>
    const float SailBlendSeconds = 1.2f;

    /// <summary>Share of the rocking that survives at full row: the water does not stop when the oars bite.</summary>
    const float RowingRockScale = 0.45f;

    /// <summary>
    /// Slack left between the bow and the pond's north water line. The berth is the stair the player walks down
    /// from the road, so the boat is pulled up to it as close as the rim allows: this much short of the water's
    /// edge keeps the hull off the bank piece while still putting the bow within a step of the rim's walk face.
    /// </summary>
    const float BerthGap = 0.2f;

    /// <summary>
    /// Standing height of the hull's inner floor above its bounding-box bottom. The box bottom sits 0.10 m above
    /// the water line (deck 0.30 m below the hull's top), so this puts the player's feet a shade under the pond
    /// surface, inside the hull and in front of the gunwale - where a rower's feet actually are.
    /// </summary>
    const float DeckOffset = 0.25f;

    /// <summary>
    /// Where the player stands along the hull, as a local Z offset: positive is toward the stern, so the oarsman
    /// sits behind the mast instead of inside it. World XZ is this rotated by <see cref="Heading"/>.
    /// </summary>
    const float DeckSeatZ = 0.45f;

    /// <summary>
    /// How far the hull's centre must stay inside a navigable corridor. The hull is 2.03 m wide and the narrowest
    /// channel (the branch) is 3.20 m across, so a full half-beam margin would leave 0.5 m of steering room and the
    /// branch would read as a rail; this keeps the centre off the bank while still letting the boat turn.
    /// </summary>
    const float SteerMargin = 0.25f;

    /// <summary>Seconds without a steering request before the oars stop and the boat goes back to riding the water.</summary>
    const float OarStopDelay = 0.25f;

    /// <summary>How fast the hull follows the water level it is entering; the estuary alone drops 1.84 m.</summary>
    const float LevelLerpPerSecond = 1.6f;

    /// <summary>How fast the hull's pose eases onto the sea's slope, per second.</summary>
    const float SeaTiltFollowPerSecond = 3f;

    /// <summary>
    /// Largest pitch or roll the sea may impose, radians. The relief's steepest gradients sit around this, and a taller
    /// angle would put a gunwale under; it is also the safety net that keeps a sample on a wave face from flipping the
    /// hull.
    /// </summary>
    const float MaxSeaTilt = 0.16f;

    internal readonly BoatHull hull;

    /// <summary>
    /// The sea panel the open-water legs are read from. Held here as well as in <see cref="Waterway"/> because the
    /// hull's tilt needs the surface under four points, not just the level under the centre.
    /// </summary>
    readonly Sea sea;

    /// <summary>
    /// The model itself, exposed for the object picker: a boat is a scene entity like a house or a
    /// waterfall, so it can be clicked, outlined and inspected in the property panel.
    /// </summary>
    internal BoatHull Hull => hull;

    /// <summary>
    /// Water the boat may travel. See <see cref="BoatWaterway"/>; the driving code must ask before it
    /// commits a move, and read the surface height it returns to keep the hull on the water it enters.
    /// </summary>
    internal BoatWaterway Waterway { get; }

    /// <summary>Accumulated frame delta, the argument of the rocking oscillators.</summary>
    float elapsed;

    bool sailing;

    /// <summary>0 = moored, 1 = rowing, crossed over <see cref="SailBlendSeconds"/>.</summary>
    float rowBlend;

    /// <summary>Name of the clip to loop, resolved once the asset is loaded (the boat has exactly one).</summary>
    string? clipName;

    bool playing;

    /// <summary>The moored boat's pose has already been pinned this voyage.</summary>
    bool rested;

    /// <summary>Water level the hull is heading for; <see cref="Update"/> walks PosY toward it at <see cref="LevelLerpPerSecond"/>.</summary>
    float targetLevel;

    /// <summary>Pitch and roll taken from the sea's slope, eased toward the sampled target at <see cref="SeaTiltFollowPerSecond"/>.</summary>
    float seaPitch, seaRoll;

    /// <summary>Boat-local clock, advanced by the same clamped delta the oscillators use.</summary>
    float clock;

    /// <summary>Value of <see cref="clock"/> at the last accepted steering request.</summary>
    float lastSteerClock = float.MinValue;

    /// <param name="pond">
    /// The pond the boat is built in: its panel position anchors the pond rectangle inside the waterway and
    /// the boat's own start pose. Passed in rather than duplicated so moving the pond moves the boat's water.
    /// </param>
    /// <param name="sea">
    /// The sea panel. The open-water leg reads its drawn surface instead of a constant, so the hull rises with the
    /// relief and settles when the sea level is changed at runtime.
    /// </param>
    internal Boat(Pond pond, Sea sea)
    {
        this.sea = sea;
        Waterway = new BoatWaterway(new Vector3(pond.PosX, Pond.WaterY, pond.PosZ), sea);

        // The panel's Pos is the water line under the hull, not the model's anchor: the rocking and the
        // ride height are applied on top of it in Update, and the driving code only has to write PosX/PosZ
        // (and PosY when the water level changes) to move the boat.
        //
        // The berth is the north rim rather than the pond centre. The water's north edge is one InnerHalfZ
        // ahead of the panel, and the stair that lands the player from the road (z = 0) comes down onto the
        // rim just outside it, so the hull is set half a length plus the berth gap inside that edge and kept
        // on the stair's own x. See Heading for which way that points the bow.
        PosX = pond.PosX;
        PosY = Pond.WaterY;
        PosZ = pond.PosZ + Pond.InnerHalfZ - Length / 2f - BerthGap;
        targetLevel = PosY;

        hull = new BoatHull
        {
            Name = GlbPath,
            Width = Beam,
            Height = Height,
            Depth = Length,
        };
        AddControl(hull);
    }

    /// <summary>
    /// Puts the boat in either state. The driving code calls this when the player takes the helm; Idle is
    /// the state the boat is built in, so a scene with no controller simply leaves it moored.
    /// </summary>
    internal void SetSailing(bool value)
    {
        if (sailing == value)
            return;

        sailing = value;

        // A new voyage starts from the clip's own first frame even if the last one was stopped there.
        if (value)
            rested = false;
    }

    /// <summary>The state the boat is in, as last requested through <see cref="SetSailing"/>.</summary>
    internal bool Sailing => sailing;

    /// <summary>
    /// Bow direction as a yaw around world Y, in radians. The model's bow is its -Z end, so a yaw of 0 points the
    /// bow to -Z - the pond's outlet - and a yaw of pi points it to +Z. The boat is built at that second value:
    /// at the berth +Z is the stair the player steps down from, so the bow is the end that opens for boarding,
    /// and the stern faces the outlet the boat backs out toward when the voyage starts. The driving code writes
    /// this every frame; the rocking adds a few tenths of a degree on top and never replaces it.
    /// </summary>
    internal float Heading { get; set; } = MathF.PI;

    /// <summary>
    /// True while the player stands on the deck. Entered and left only through <see cref="Board"/> and
    /// <see cref="Disembark"/>, which the long-jump landing test calls once it knows where the feet came down; that
    /// test also owns the player's deck pose, so this flag stays a plain statement about the hull.
    /// </summary>
    internal bool Aboard { get; private set; }

    /// <summary>
    /// World Y of the deck under the player's feet. Follows the heave the hull is riding, so a moored boat lifts the
    /// character with it; pitch and roll are left out because the character model has yaw only.
    /// </summary>
    internal float DeckY => hull.PosY - Height * 0.5f + DeckOffset;

    /// <summary>World position the player stands on: the deck point one seat back from the mast.</summary>
    internal Vector3 SeatPosition
    {
        get
        {
            float c = MathF.Cos(Heading);
            float s = MathF.Sin(Heading);

            // Local (0, 0, DeckSeatZ) through the same row-vector yaw the hull uses: (x cos + z sin, -x sin + z cos).
            return new Vector3(PosX + DeckSeatZ * s, DeckY, PosZ + DeckSeatZ * c);
        }
    }

    /// <summary>Takes the helm. From the next frame on the player is bound to the seat and steering drives the hull.</summary>
    internal void Board() => Aboard = true;

    /// <summary>Leaves the helm where the hull stands: the boat keeps its position and goes back to riding the water.</summary>
    internal void Disembark()
    {
        Aboard = false;
        SetSailing(false);
    }

    /// <summary>
    /// Whether a world XZ point lies over the hull's plan rectangle. The landing test uses it to tell a jump that came
    /// down on the deck from one that came down beside it; <paramref name="margin"/> shrinks the rectangle so the rim of
    /// the box - where there is no floor - does not count as deck.
    /// </summary>
    internal bool ContainsDeck(float x, float z, float margin = 0f)
    {
        float dx = x - PosX;
        float dz = z - PosZ;
        float c = MathF.Cos(Heading);
        float s = MathF.Sin(Heading);

        // World offset in boat-local axes: the inverse of the yaw applied above.
        float lx = dx * c - dz * s;
        float lz = dx * s + dz * c;

        return MathF.Abs(lx) <= Beam * 0.5f - margin
            && MathF.Abs(lz) <= Length * 0.5f - margin;
    }

    /// <summary>
    /// One steering request, in world metres. The bow turns to the requested direction first, then the hull moves as
    /// far as the waterway allows: a request whose target centre falls outside every corridor is rejected outright, so
    /// the boat never climbs a bank. Returns whether the hull actually moved.
    ///
    /// <para>Only the player's own input calls this, which is what keeps the boat from drifting on its own; the oars
    /// stop <see cref="OarStopDelay"/> seconds after the last accepted request.</para>
    /// </summary>
    internal bool Steer(float dx, float dz)
    {
        float length = MathF.Sqrt(dx * dx + dz * dz);
        if (length < 1e-5f)
            return false;

        // forward = (-sin yaw, -cos yaw), so yaw = atan2(-fx, -fz).
        Heading = MathF.Atan2(-dx / length, -dz / length);

        float nx = PosX + dx;
        float nz = PosZ + dz;

        if (!Waterway.SurfaceY(nx, nz, out float level, SteerMargin))
            return false;

        PosX = nx;
        PosZ = nz;
        targetLevel = level;

        lastSteerClock = clock;
        SetSailing(true);
        return true;
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        // Same frame-delta contract the campfire uses: a stalled frame must not teleport the oscillators.
        float delta = float.IsFinite(time) ? Math.Clamp(time, 0f, 0.1f) : 0f;
        elapsed += delta;
        clock += delta;

        // The oars follow the steering, not the hull's speed: a request that was rejected by the bank still counts
        // as rowing, because the crew is pulling even though the boat is not going anywhere.
        if (sailing && clock - lastSteerClock > OarStopDelay)
            SetSailing(false);

        // Re-read the water under the hull every frame rather than only when a steering request lands: a channel's
        // level depends on where along the course the hull sits, and the sea's surface rises and falls by metres as it
        // crosses a crest. Steer still writes the level it just checked, so the same frame's lerp already sees it.
        if (Waterway.SurfaceY(PosX, PosZ, out float surfaceY))
            targetLevel = surfaceY;

        // Level changes are walked rather than snapped: the estuary drops 1.84 m between the channel and the sea.
        if (PosY != targetLevel)
        {
            float limit = LevelLerpPerSecond * delta;
            PosY = MathF.Abs(targetLevel - PosY) <= limit
                ? targetLevel
                : PosY + MathF.Sign(targetLevel - PosY) * limit;
        }

        float target = sailing ? 1f : 0f;
        float step = delta / SailBlendSeconds;
        rowBlend = target > rowBlend ? MathF.Min(target, rowBlend + step) : MathF.Max(target, rowBlend - step);

        SyncPlayback();

        float rock = 1f - (1f - RowingRockScale) * rowBlend;

        float heave = (HeaveAmpA * MathF.Sin(elapsed * 1.05f) + HeaveAmpB * MathF.Sin(elapsed * 1.73f + 1.3f)) * rock;
        float pitch = (PitchAmpA * MathF.Sin(elapsed * 0.83f + 0.7f) + PitchAmpB * MathF.Sin(elapsed * 1.37f)) * rock;
        float roll = (RollAmpA * MathF.Sin(elapsed * 0.71f + 2.1f) + RollAmpB * MathF.Sin(elapsed * 1.19f + 0.4f)) * rock;
        float yaw = YawWobble * MathF.Sin(elapsed * 0.61f + 1.9f) * rock;

        // Open water is a slope, so the hull takes its bow and list angles from the surface under bow, stern and both
        // beams. Channels are flat by construction, so this stays off there and the moored rocking is all there is.
        float seaPitchTarget = 0f, seaRollTarget = 0f;
        if (Waterway.IsOpenSea(PosX, PosZ))
        {
            float c = MathF.Cos(Heading), s = MathF.Sin(Heading);
            float halfLength = Length * 0.5f, halfBeam = Beam * 0.5f;

            // Local +Z is the stern (the bow is the model's -Z end, see Heading), local +X is one beam.
            float bow = sea.SurfaceY(PosX - s * halfLength, PosZ - c * halfLength);
            float stern = sea.SurfaceY(PosX + s * halfLength, PosZ + c * halfLength);
            float starboard = sea.SurfaceY(PosX + c * halfBeam, PosZ - s * halfBeam);
            float port = sea.SurfaceY(PosX - c * halfBeam, PosZ + s * halfBeam);

            // BoatHull's positive pitch tips the bow down and its positive roll tips +X down, so whichever end sits
            // higher than its opposite pulls the angle negative.
            seaPitchTarget = Math.Clamp(-MathF.Atan2(bow - stern, Length), -MaxSeaTilt, MaxSeaTilt);
            seaRollTarget = Math.Clamp(-MathF.Atan2(starboard - port, Beam), -MaxSeaTilt, MaxSeaTilt);
        }

        // Eased, not snapped: a sample taken a step across a crest would otherwise flick the mast.
        float tiltBlend = MathF.Min(1f, SeaTiltFollowPerSecond * delta);
        seaPitch += (seaPitchTarget - seaPitch) * tiltBlend;
        seaRoll += (seaRollTarget - seaRoll) * tiltBlend;

        hull.PosX = PosX;
        hull.PosY = PosY + RideHeight + heave;
        hull.PosZ = PosZ;
        hull.Rotation = Heading + yaw;
        hull.Pitch = pitch + seaPitch;
        hull.Roll = roll + seaRoll;

        if (hull.Update(time))
            result = true;

        return result;
    }

    /// <summary>
    /// Keeps the clip in sync with the requested state. Two details make this a per-frame sync rather than
    /// an event: the asset arrives asynchronously with the queued load, and the direct-load paths on DX and
    /// VK start the first clip on their own (<c>DXModel.Load</c> calls PlayAnimation), so a boat that was
    /// never asked to row still has to be pulled back to its moored pose once the asset appears.
    /// </summary>
    void SyncPlayback()
    {
        if (!hull.Ready)
            return;

        if (sailing)
        {
            clipName ??= PickClip();
            if (clipName != null)
                playing = hull.PlayAnimation(clipName) != null;
        }
        else if (!rested || playing)
        {
            rested = hull.StopAnimationAtRest();
            playing = false;
        }
    }

    /// <summary>The boat has one clip; play the first one rather than hard-coding the exporter's name.</summary>
    string? PickClip()
    {
        var names = hull.GetAnimationNames();
        return names.Count > 0 ? names[0] : null;
    }
}

/// <summary>
/// The boat's hull model with the two extra rotation axes a floating body needs. See
/// <see cref="Boat"/> for why the rocking cannot come from the asset's clip.
/// </summary>
internal sealed class BoatHull : Model
{
    /// <summary>Bow-down / bow-up angle about the hull's own X axis, radians.</summary>
    internal float Pitch;

    /// <summary>Port-starboard list about the hull's own Z axis, radians.</summary>
    internal float Roll;

    /// <summary>
    /// Row-vector convention: <c>p * Ry * Rx * Rz</c> applies the heading first and then the two rocking
    /// angles, so pitch and roll are expressed in the boat's own frame. Building them as one matrix keeps
    /// <see cref="Mesh3DBase.AnchorWorldOffset"/> and every consumer of the world matrix on the same pose.
    /// </summary>
    protected override Matrix4x4 GetRotationMatrix()
        => Matrix4x4.CreateRotationY(Rotation)
         * Matrix4x4.CreateRotationX(Pitch)
         * Matrix4x4.CreateRotationZ(Roll);
}

/// <summary>
/// Where the boat may float: the pond rectangle, the branch and main courses as a centerline corridor, and
/// the open sea past the coast. The corridor comes from <see cref="River.NavigableCenterline"/> - the same
/// control points the channel is built from - so the water and the route cannot disagree.
///
/// <para>The main route runs from inside the waterfall pool, under the bridge (its arch is at the crossing,
/// see <see cref="Bridge"/>), past the estuary and out to sea. The branch runs from the pond down across the
/// main course and on to the foot of the range, where its channel sinks under the meadow; the corridor stops on the
/// last row that still has a bed under it, see <see cref="River.NavigableCenterline"/>.</para>
///
/// <para><b>The estuary is a step, not a blend.</b> The river runs out at 0.34 m and the sea stands at
/// <see cref="Sea.DefaultSeaLevel"/> (-1.5 m), so a hull that crosses the coast line drops 1.84 m. That is
/// the scene's actual geometry; driving code has to carry the boat over it (a short fall) instead of
/// pretending the two surfaces meet.</para>
/// </summary>
internal sealed class BoatWaterway
{
    readonly Vector3 pondCenter;
    readonly Sea sea;
    readonly (Vector3 Center, float HalfWidth)[] main;
    readonly (Vector3 Center, float HalfWidth)[] branch;

    internal BoatWaterway(Vector3 pondCenter, Sea sea)
    {
        this.pondCenter = pondCenter;
        this.sea = sea;
        main = River.NavigableCenterline(branch: false);
        branch = River.NavigableCenterline(branch: true);
    }

    /// <summary>Whether a point lies on water the boat may occupy, ignoring the hull's own footprint.</summary>
    internal bool Contains(float x, float z) => SurfaceY(x, z, out _);

    /// <summary>
    /// Water level at a navigable point, or false when the point is outside the boat's water.
    ///
    /// <para><paramref name="margin"/> shrinks every corridor by that much, so passing half the hull's beam
    /// keeps the whole hull on the water rather than just its anchor. The pond and the courses use it; the
    /// open sea ignores it, because there is no far bank to keep off. The sea's level is not the constant
    /// <see cref="Sea.DefaultSeaLevel"/> but the surface the panel draws, which follows the sea's own relief.</para>
    /// </summary>
    internal bool SurfaceY(float x, float z, out float y, float margin = 0f)
    {
        if (MathF.Abs(x - pondCenter.X) <= Pond.InnerHalfX - margin
            && MathF.Abs(z - pondCenter.Z) <= Pond.InnerHalfZ - margin)
        {
            y = Pond.WaterY;
            return true;
        }

        if (TryCourse(main, x, z, margin, out y) || TryCourse(branch, x, z, margin, out y))
            return true;

        if (x >= RiverSegment.CoastEdge + margin)
        {
            y = sea.SurfaceY(x, z);
            return true;
        }

        y = 0f;
        return false;
    }

    /// <summary>
    /// Whether a point's level comes from the open sea rather than a channel, using the same precedence as
    /// <see cref="SurfaceY"/>. Only the sea has a slope, so the hull asks this before it takes its tilt from the water;
    /// the estuary's last metres also sit past the coast line but are still channel, which is why the corridor is
    /// tested first here too.
    /// </summary>
    internal bool IsOpenSea(float x, float z)
    {
        if (MathF.Abs(x - pondCenter.X) <= Pond.InnerHalfX && MathF.Abs(z - pondCenter.Z) <= Pond.InnerHalfZ)
            return false;

        if (TryCourse(main, x, z, 0f, out _) || TryCourse(branch, x, z, 0f, out _))
            return false;

        return x >= RiverSegment.CoastEdge;
    }

    /// <summary>
    /// One polyline's span test. A point inside the corridor takes the level of whichever span it sits
    /// deepest inside (distance over half width), so the junction where the branch meets the main course
    /// resolves to the channel the point is actually centered on instead of to whichever edge is nearer.
    /// </summary>
    static bool TryCourse((Vector3 Center, float HalfWidth)[] points, float x, float z, float margin, out float y)
    {
        y = 0f;
        float best = float.MaxValue;

        for (int i = 0; i + 1 < points.Length; i++)
        {
            var a = points[i];
            var b = points[i + 1];

            float dx = b.Center.X - a.Center.X;
            float dz = b.Center.Z - a.Center.Z;
            float lengthSquared = dx * dx + dz * dz;

            float px = x - a.Center.X;
            float pz = z - a.Center.Z;
            float t = lengthSquared > 1e-8f
                ? Math.Clamp((px * dx + pz * dz) / lengthSquared, 0f, 1f)
                : 0f;

            float cx = px - dx * t;
            float cz = pz - dz * t;
            float distance = MathF.Sqrt(cx * cx + cz * cz);

            float halfWidth = a.HalfWidth + (b.HalfWidth - a.HalfWidth) * t - margin;
            if (halfWidth <= 0f || distance > halfWidth)
                continue;

            float score = distance / halfWidth;
            if (score >= best)
                continue;

            best = score;
            y = a.Center.Y + (b.Center.Y - a.Center.Y) * t;
        }

        return best < float.MaxValue;
    }
}
