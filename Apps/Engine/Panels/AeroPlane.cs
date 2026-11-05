// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Engine.Panels;

/// <summary>
/// The player's aircraft: parked on the seaside road with its nose to the sea, and the whole automated trip it
/// flies once the pilot is in the cockpit.
///
/// <para><b>Why it is parked on that road.</b> <see cref="SeaRoad"/> runs from the stair foot at x = 7.75 east to
/// the coastal rock line at x = 74 on the pond's own centre line, z = -7.05. That strip is the only piece of flat,
/// hand-laid ground on the island long enough to read as a runway, and it points straight at the open water, which
/// is where the climb goes. The aircraft waits at <see cref="ParkX"/>, a few metres past the stair, nose at yaw
/// pi/2, i.e. +X: the airframe's nose is its local +Z (the loader mirrors the glTF -Z), so yaw pi/2 is due east.</para>
///
/// <para><b>Boarding follows the boat and the balloon.</b> <see cref="Skill"/> predicts the cockpit at takeoff and
/// starts shrinking the body there, contact anywhere over <see cref="ContainsCockpit"/> binds the player to
/// <see cref="SeatPosition"/>, and <see cref="Player"/> keeps the body parked there while <see cref="Aboard"/>.
/// The one difference is that the plane is a scripted ride, not a vehicle: there is no steering entry point, and
/// <see cref="Direction"/> ignores the movement keys for the whole voyage.</para>
///
/// <para><b>The trip.</b> Boarding starts a three-second countdown drawn in the air ahead of the nose, then the
/// aircraft runs the strip, lifts off at its halfway mark, climbs on a constant slope that clears the coastal rocks,
/// turns left into the formation's ring (<see cref="Planes.OrbitRadius"/> at <see cref="Planes.FlightAltitude"/>),
/// circles the island <see cref="OrbitLaps"/> times, swings back down onto the runway centre line, lands where it
/// took off, rolls back to its parking spot, and pivots to face the sea again. The pilot is lifted out of the
/// cockpit back to the spot the boarding leap started from while the aircraft turns around.</para>
///
/// <para><b>The path is geometry, not physics.</b> Each leg is evaluated in closed form from the elapsed time of
/// that leg, so a stalled frame cannot integrate the aircraft into the sea, and every leg starts exactly where the
/// previous one ended: the climb's constant slope is the tangent of the turn's entry heading, the turn circle is
/// internally tangent to the ring by construction (see <see cref="TurnRadius"/>), and the return swing is a cubic
/// Bezier whose end tangents are the orbit tangent and the runway heading.</para>
///
/// <para><b>Bailing out.</b> The jump key works in the air: a press puts a rainbow canopy over the pilot's head and
/// glides them back to the spot the boarding leap started from, which is the same point the finished ride would
/// have put them down on. Because the aircraft is a scripted ride and not a piloted one, an empty cockpit changes
/// nothing about the trip - it keeps the ring, lands on its own and parks, with the propeller still turning until
/// it is down. The canopy owns the body for the descent, so nothing can move it but the glide itself. See
/// <see cref="BailOut"/>.</para>
/// </summary>
internal sealed class AeroPlane : Panel
{
    // -- Runway, read off SeaRoad so the asphalt and the aircraft cannot drift apart. --
    const float RunwayZ = Pond.StairFootZ;        // -7.05, the road's centre line
    const float StairFootX = SeaRoad.StairFootX;  // 7.75, west end of the asphalt
    const float RockLineX = 74f;                  // east end, where the rock chain begins
    const float ParkX = 14f;                      // where the aircraft waits for the pilot
    const float TakeoffX = (StairFootX + RockLineX) * 0.5f;   // 40.875, halfway along the strip
    const float RoadTopY = 0.05f;                 // the road's 5 cm lift over the grass

    /// <summary>Yaw with the nose pointing east, along the runway and out to sea.</summary>
    const float ParkYaw = MathF.PI * 0.5f;

    /// <summary>Yaw after the landing roll, with the nose pointing west, back at the parking spot.</summary>
    const float LandingYaw = -MathF.PI * 0.5f;

    // -- Airframe: the same 8 m wingspan the formation flies, so the jet reads as one of the ten it joins. The
    //    height and depth follow the asset's own 1.16944 x 0.41724 x 0.88113 local bounds. --
    const float ModelWidth = 8f;
    const float ModelHeight = ModelWidth * 0.41724f / 1.16944f;   // ~2.855 m
    const float ModelDepth = ModelWidth * 0.88113f / 1.16944f;    // ~6.028 m

    /// <summary>Fuselage-centre height while the wheels rest on the asphalt (the anchor is the box centre).</summary>
    const float GroundY = RoadTopY + ModelHeight * 0.5f;

    // -- Climb. The slope eases in over ClimbRamp metres and then holds a constant angle, which is the "uniform
    //    climb" the trip asks for; 0.344 is tan(19 degrees). ClimbDistance is the run needed to reach the ring's
    //    altitude exactly, so TurnStartX is derived rather than authored. --
    const float FlightAltitude = Planes.FlightAltitude;   // 20 m, the formation's level
    const float ClimbSlope = 0.344f;
    const float ClimbRamp = 20f;
    const float ClimbDistance = (FlightAltitude - GroundY) / ClimbSlope + ClimbRamp * 0.5f;   // ~63.8 m
    const float TurnStartX = TakeoffX + ClimbDistance;                                       // ~104.7 m

    // -- Turn-in and orbit. --
    const float OrbitRadius = Planes.OrbitRadius;   // 146 m
    const float OrbitSpeed = Planes.OrbitSpeed;     // 24 m/s, the formation's ground speed
    const float Gravity = 9.81f;

    /// <summary>
    /// Radius of the horizontal turn that joins the ring. Solved so the turn circle is internally tangent to the
    /// orbit circle: the centre sits at (TurnStartX, RunwayZ + r) and must be exactly <c>OrbitRadius - r</c> from the
    /// island centre. Expanding that constraint gives this closed form, so the merge has no kink to tune away.
    /// </summary>
    const float TurnRadius = (OrbitRadius * OrbitRadius - TurnStartX * TurnStartX - RunwayZ * RunwayZ)
                           / (2f * (OrbitRadius + RunwayZ));   // ~37.06 m

    /// <summary>Angle of the tangency point on the ring, and therefore the heading the turn ends on.</summary>
    static readonly float OrbitEntryAngle = MathF.Atan2(RunwayZ + TurnRadius, TurnStartX);   // ~16 degrees

    /// <summary>How far the turn sweeps: from due east (pi/2) round to the ring's tangent.</summary>
    static readonly float TurnSweep = MathF.PI * 0.5f + OrbitEntryAngle;

    // The turn is also where the aircraft changes speed: it leaves the climb at LaunchSpeed and has to be doing the
    // ring's own speed by the time it touches the ring, or the merge would be a visible step. Constant acceleration,
    // so the arc length is the closed form below and the bank eases from the climb's to the ring's along with it.
    static readonly float TurnLength = TurnRadius * TurnSweep;
    static readonly float TurnSeconds = 2f * TurnLength / (LaunchSpeed + OrbitSpeed);
    static readonly float TurnAccel = (OrbitSpeed - LaunchSpeed) / TurnSeconds;

    /// <summary>Laps flown before the aircraft leaves the ring and comes home.</summary>
    const float OrbitLaps = 3f;

    // -- Speeds and timings. The taxi accelerates from rest to LaunchSpeed over the whole run up to TakeoffX, and
    //    the landing roll is that same profile run backwards, so both end exactly on the two ends of the strip. --
    const float LaunchSpeed = 16f;
    const float TaxiAccel = LaunchSpeed * LaunchSpeed / (2f * (TakeoffX - ParkX));
    const float CountdownSeconds = 3f;

    /// <summary>
    /// The spoken countdown, generated offline with the qwen3-tts model (see .dbg/qwen3_probe) and sitting beside
    /// the thunder clips under Raw/Musics. 2.16 s, so it fits inside <see cref="CountdownSeconds"/>.
    /// </summary>
    const string CountdownClip = "Musics/ThreeTwoOneStart.wav";

    /// <summary>
    /// How long the roll out lasts. The speed reaches zero after exactly this time, so the leg ends on the clock:
    /// the distance ramp below peaks at RunwayLength where its two roots are, and evaluating "has run reached the
    /// end" against a value that is only ever reached asymptotically would leave the jet rolling on past the stop
    /// point (and then reversing), which is what kept it from ever parking.
    /// </summary>
    const float RolloutSeconds = 2f * (TakeoffX - ParkX) / LaunchSpeed;

    // Return swing: a cubic Bezier from the ring exit to the runway centre line, entering it on the landing
    // heading. The two handles are set to the speed each end is already flying - the ring's 24 going out, the
    // descent's 16 coming in - so the aircraft neither leaps off the ring nor stalls onto the approach; the 70 m
    // of curve that falls out of them is ~22 m/s average and stays outside x = 100, clear of the rocks.
    const float SwingSeconds = 3.2f;
    static readonly float SwingExitHandle = OrbitSpeed * SwingSeconds;
    static readonly float SwingApproachHandle = LaunchSpeed * SwingSeconds;

    const float TurnAroundSeconds = 4f;

    // -- Pilot exit: the same arc shape the long jump uses, aimed at the spot the boarding leap started from. --
    const float ExitSeconds = 1.1f;
    const float ExitArc = 0.8f;

    // -- Ride camera. The over-the-shoulder rig sits 3.5 m behind the pilot, i.e. inside an 8 m airframe, which is
    //    why the aircraft filled and clipped the view; for the length of the trip the camera backs off to a chase
    //    station that frames the whole aircraft, looking a little ahead of the nose so there is room to see where
    //    the jet is going. The pull-back is eased over the first second of the countdown instead of cutting.
    //    Once it is there the station is carried rigidly with the airframe, so dragging to look around and scrolling
    //    to move in or out keep working for the rest of the ride.
    //    On the way out the camera eases back onto the walking rig so the pilot lands in the usual view. --
    const float ChaseBack = 13f;
    const float ChaseLift = 5f;
    const float ChaseAhead = 6f;
    const float ChaseLookLift = 1f;
    const float ChaseBlendSeconds = 0.8f;
    const float ExitCameraFollowPerSecond = 4.5f;

    /// <summary>How fast the airframe's pitch and bank chase the leg's target angles, per second.</summary>
    const float PostureFollowPerSecond = 4f;

    // -- Cockpit, as fractions of the bounding box. The pilot keeps their walking size in the cockpit - the
    //    aircraft is not scaled down, so neither is the body - which makes the seat a far smaller lift than the
    //    deck poses need: half the box height is 1.43 m and the canopy rim sits ~1.9 m over the road, so 0.03
    //    puts the feet on the cockpit floor with the chest and head standing clear above the glass. Forward,
    //    the seat sits a metre ahead of the bounding-box centre, in the front half of the canopy, so the pilot
    //    reads as sitting behind the windscreen rather than standing in the middle of the fuselage. --
    const float SeatLiftFraction = 0.03f;
    const float SeatForwardFraction = 0.16f;
    const float CockpitHalfWidth = 1.2f;
    const float CockpitHalfLength = 2.6f;

    // -- Bail-out. The canopy hangs over the pilot and glides them to the takeoff spot of the boarding leap, i.e.
    //    the spot the finished ride would have put them down on. The asset is 10.4 x 6.7 x 4.5 m with its wire knot
    //    at the bottom of the box, so scaling it to ChuteSpan across hangs the canopy a proportional 3.5 m over the
    //    harness without a second set of numbers. The glide is flown at a steady horizontal speed, which is what
    //    makes the descent take as long as the distance asks for: a hop off the end of the strip is over in a
    //    couple of seconds, a jump from the far side of the ring is a long float home. --
    const string ChuteGlbPath = @"Assets/parachute_rainbow.glb";
    const float ChuteSpan = 5.5f;
    const float ChuteHang = 0.1f;
    const float GlideSpeed = 14f;
    const float DropMinSeconds = 1.5f;
    const float DropMaxSeconds = 26f;

    /// <summary>How fast the pilot turns to face the glide they stepped into, per second.</summary>
    const float DropTurnPerSecond = 4f;

    /// <summary>Pendulum swing across the track, in metres, and how many full swings it makes over the drop.</summary>
    const float DropSway = 0.35f;
    const float DropSways = 2f;

    /// <summary>How long the view takes to ease off the descent and back onto the walking rig once the feet are down.</summary>
    const float DropSettleSeconds = 0.7f;

    // -- Formation separation. The jet joins the very ring the ten aircraft fly, so it would otherwise pass through
    //    them. Near a formation aircraft the jet rides up on a smooth bump; 4.5 m clears two 2.86 m tall airframes
    //    and fades out 26 m away, which is a slope under 0.2 - a gentle detour, not a hop. --
    const float AvoidRadius = 26f;
    const float AvoidLift = 4.5f;

    /// <summary>Ring distance over which the avoidance bump eases in at the entry and out at the exit, in metres.</summary>
    const float LiftRamp = 60f;

    /// <summary>Stages of the scripted flight, in the order the aircraft flies them.</summary>
    enum Stage
    {
        Parked,
        Countdown,
        Taxi,
        Climb,
        TurnIn,
        Orbit,
        Swing,
        Descent,
        Rollout,
        TurnAround,
    }

    internal PlaneBody body;

    Stage stage;

    /// <summary>Seconds spent in the current stage; every leg's geometry is a closed form of this.</summary>
    float stageTime;

    /// <summary>Pitch and bank the current leg asks for; the body eases onto them.</summary>
    float targetPitch, targetRoll;

    /// <summary>Eased pitch and bank, i.e. the attitude every leg settles on. Published to the body each frame.</summary>
    float pitch, roll;

    /// <summary>Pose this update computed, held back so the next frame draws it (see <see cref="PublishPose"/>).</summary>
    AirframePose poseNext;
    bool poseNextReady;

    /// <summary>Where the boarding leap started from, i.e. where the pilot is put back down at the end.</summary>
    Vector3 exitSpot;

    Vector3 exitFrom, exitTo;
    float exitTime;
    bool exiting;

    /// <summary>True while the propeller clip is running, i.e. for the ground run and the flight, not for the wait.</summary>
    bool spinning;

    /// <summary>
    /// True once the blades have been laid to rest. The loader plays the asset's first clip the moment it lands, so
    /// "not spinning" is a state this panel has to establish once rather than one the aircraft starts in; the flag
    /// keeps that from re-evaluating the node tree on every parked frame.
    /// </summary>
    bool propellerParked;

    /// <summary>How far the view has been handed over to the chase station: 0 at the shoulder rig, 1 fully astern.</summary>
    float chaseBlend;

    /// <summary>Where the camera was pointing when the pilot climbed in; the pull-back blends out of it.</summary>
    Vector3 chaseFromPos, chaseFromTarget;

    /// <summary>Aircraft pose the camera rig was last carried with, so the follow is a rigid move, not a re-aim.</summary>
    Vector3 chaseAnchor;
    float chaseYaw;

    /// <summary>Canopy over the pilot's head while the parachute owns the body; Alpha 0 keeps it out of the frame.</summary>
    Season.Controls.Model chute;

    /// <summary>Endpoints of the glide: the cockpit the pilot stepped out of, and the spot they land on.</summary>
    Vector3 dropFrom, dropTo;

    /// <summary>Glide point of the previous update - the pose the current frame is drawn at (see <see cref="StepDrop"/>).</summary>
    Vector3 dropAnchor;

    float dropTime, dropSeconds;

    /// <summary>Facing the canopy settles on: the direction of the glide, eased out of the cockpit's heading.</summary>
    float dropYaw, dropYawCurrent;

    bool dropping;        // the canopy owns the body
    bool dropSettling;    // feet are down, the view is easing back onto the walking rig
    float settleTime;

    /// <summary>Countdown drawn ahead of the nose, one digit per second.</summary>
    Texts countdown;

    /// <summary>
    /// True while the player stands in the cockpit. Entered only through <see cref="Board"/>, which the long-jump
    /// landing test calls once it knows where the feet came down; left when the automated trip has brought the
    /// aircraft home and the pilot has been lifted back out.
    /// </summary>
    internal bool Aboard { get; private set; }

    /// <summary>
    /// Whether the aircraft will take a pilot. Only the parked state does: a jet running the strip or circling the
    /// island cannot be boarded, which is also what keeps a jump from re-boarding mid-flight.
    /// </summary>
    internal bool CanBoard => stage == Stage.Parked;

    /// <summary>
    /// Yaw the pilot faces while seated. The character convention is forward = (-sin yaw, 0, -cos yaw) while the
    /// airframe's nose is (sin yaw, 0, cos yaw), so the two differ by half a turn - the same relation the boat's
    /// heading has with its own bow, only the airframe's nose points the other way in model space.
    /// </summary>
    internal float PilotYaw => body.Rotation + MathF.PI;

    /// <summary>
    /// Whether the pilot may step out. Only an aircraft that is off the ground and still carrying its pilot can be
    /// left: on the strip there is nothing to parachute from, and once the cockpit is empty a press changes nothing.
    /// The landing itself is excluded too - the wheels are on the asphalt by then, and the ride puts the pilot down
    /// on the road a second later anyway.
    /// </summary>
    internal bool CanBailOut => Aboard
        && stage is Stage.Climb or Stage.TurnIn or Stage.Orbit or Stage.Swing or Stage.Descent;

    /// <summary>
    /// True from the moment the pilot steps out over the side until the canopy has put them on the ground and the
    /// view has eased back onto the walking rig. While it is set the canopy owns the body: no walking, no jumping,
    /// and the only thing a key or a drag can do is move the camera around the descent.
    /// </summary>
    internal bool Dropping => dropping;

    internal AeroPlane()
    {
        body = new PlaneBody
        {
            Name = Planes.PlaneGlbPath,
            Highlight =
            {
                Style = HighlightStyle.Wireframe
            },
            Width = ModelWidth,
            Height = ModelHeight,
            Depth = ModelDepth,
            Rotation = ParkYaw,
        };
        AddControl(body);

        // The countdown is a scene panel's overlay child: the digits are anchored to a world point ahead of the
        // nose by ProjectToViewFraction, but drawn with the UI pass so they stay crisp and always face the camera.
        countdown = new Texts
        {
            RenderDomain = Season.Controls.RenderDomain.Overlay,
            Content = string.Empty,
            Color = Season.Basic.Colors.Blue,
            Scale = Vector2.One * 5f,
        };
        AddControl(countdown);

        // The canopy is a child of this panel for the whole session but stays invisible until somebody jumps: a
        // zero alpha skips it in the draw path, so a parked aircraft carries no extra geometry into the frame. It is
        // built here rather than on the first jump so the asset is already on the GPU by the time the pilot needs it.
        chute = new Season.Controls.Model
        {
            Name = ChuteGlbPath,
            Alpha = 0f,
        };
        AddControl(chute);

        Enter(Stage.Parked);
    }

    /// <summary>
    /// The airframe's own up axis, i.e. vertical as the seat feels it. Pitch swings it forward with the nose and the
    /// bank swings it out into the turn, in exactly the order <see cref="PlaneBody"/> builds them, so this is the one
    /// line of a cockpit that is never the world's +Y once the aircraft is flying.
    /// </summary>
    internal Vector3 SeatUp
    {
        get
        {
            float yaw = body.Rotation;
            float cy = MathF.Cos(yaw), sy = MathF.Sin(yaw);
            var noseFlat = new Vector3(sy, 0f, cy);
            var rightFlat = new Vector3(cy, 0f, -sy);

            float cp = MathF.Cos(body.Pitch), sp = MathF.Sin(body.Pitch);
            var pitchedUp = Vector3.UnitY * cp - noseFlat * sp;

            float cr = MathF.Cos(body.Roll), sr = MathF.Sin(body.Roll);
            return pitchedUp * cr - rightFlat * sr;
        }
    }

    /// <summary>
    /// World position the pilot stands on: the cockpit floor on the fuselage centre line. The lift and the forward
    /// offset ride the airframe's own attitude, so a climbing jet carries the pilot with it instead of leaving them
    /// level while the nose rises, and a banking jet tips the seat with the wings - the lift is measured along
    /// <see cref="SeatUp"/>, which the bank swings off the vertical.
    /// </summary>
    internal Vector3 SeatPosition
    {
        get
        {
            float yaw = body.Rotation;
            float cy = MathF.Cos(yaw), sy = MathF.Sin(yaw);

            float cp = MathF.Cos(body.Pitch), sp = MathF.Sin(body.Pitch);
            var nose = new Vector3(sy * cp, sp, cy * cp);

            float lift = SeatLiftFraction * ModelHeight;
            float forward = SeatForwardFraction * ModelDepth;

            return new Vector3(body.PosX, body.PosY, body.PosZ) + SeatUp * lift + nose * forward;
        }
    }

    /// <summary>
    /// Pitch of the pose being drawn, radians: the pilot's body takes it so a climbing jet is not stood in. It is
    /// the airframe's own angle, in the airframe's own frame; the pilot turns it over, because their yaw is half a
    /// turn from the nose (see <see cref="PilotYaw"/> and the cockpit branch of <c>Player.Update</c>).
    /// </summary>
    internal float PilotPitch => body.Pitch;

    /// <summary>
    /// Bank of the pose being drawn, radians: the pilot's body takes it so the wings roll the seat too. Negated by
    /// the same half-turn as <see cref="PilotPitch"/> when it is handed to the body.
    /// </summary>
    internal float PilotRoll => body.Roll;

    /// <summary>
    /// Whether a world XZ point lies over the fuselage deck. The boarding test uses it; <paramref name="margin"/>
    /// shrinks the rectangle so the wing roots and the tail do not count as cockpit floor.
    /// </summary>
    internal bool ContainsCockpit(float x, float z, float margin = 0f)
    {
        float halfX = CockpitHalfWidth - margin;
        float halfZ = CockpitHalfLength - margin;
        if (halfX <= 0f || halfZ <= 0f)
            return false;

        float dx = x - body.PosX;
        float dz = z - body.PosZ;
        float c = MathF.Cos(body.Rotation);
        float s = MathF.Sin(body.Rotation);

        // World offset in the airframe's own axes: the inverse of the yaw applied to the mesh.
        float lx = dx * c - dz * s;
        float lz = dx * s + dz * c;

        return MathF.Abs(lx) <= halfX
            && MathF.Abs(lz) <= halfZ;
    }

    /// <summary>
    /// Takes the pilot. <paramref name="spot"/> is the takeoff position of the boarding leap; the trip ends by
    /// putting the player back down there, so the ride always returns them where they came from.
    /// </summary>
    internal void Board(Vector3 spot)
    {
        Aboard = true;
        exitSpot = spot;

        // Remember the rig the boarding leap left the view in, so the chase pull-back eases out of the live view
        // instead of cutting to the station on the first frame.
        chaseFromPos = App.Instance.CameraPos;
        chaseFromTarget = App.Instance.CameraTarget;
        chaseBlend = 0f;

        Enter(Stage.Countdown);

        // The call is voiced over the digits: 2.16 s of "three two one start" against the three-second hold, so the
        // last word lands just before the aircraft starts rolling.
        PlayCountdownCall();
    }

    /// <summary>
    /// Plays the pre-generated countdown call. The WAV is a static asset made offline with SeasonAudio and the
    /// qwen3-tts model (see .dbg/qwen3_probe), so nothing loads a model at runtime: it goes through the same media
    /// channel as the thunder clips and the background music, straight out of Raw/Musics.
    /// </summary>
    static void PlayCountdownCall()
    {
        var path = DeviceServices.Core?.LoadFilePath(CountdownClip);

        if (!string.IsNullOrEmpty(path))
        {
            DeviceServices.Media?.PlayMedia("Sound", path, "90");
        }
    }

    /// <summary>
    /// Steps out of the cockpit under a canopy. The pilot glides to the spot the boarding leap started from - the
    /// same point the finished ride would have put them down on - while the aircraft flies the rest of its script
    /// unattended, since none of it was ever flown by hand.
    ///
    /// <para>The glide is a line, not a simulation: the landing point and the flight time are both read once here,
    /// so every frame of the descent is a closed form of the drop clock and a stalled frame cannot drop the pilot
    /// into the sea. The time comes from the horizontal distance at a steady canopy speed, which is also what makes
    /// a jump over the strip quick and a jump from the far side of the ring a long float home.</para>
    /// </summary>
    internal void BailOut()
    {
        if (!CanBailOut)
            return;

        var model = App.Instance.player?.model;
        if (model == null)
            return;

        Aboard = false;
        dropping = true;
        dropSettling = false;
        dropTime = 0f;
        settleTime = 0f;

        dropFrom = new Vector3(model.PosX, model.PosY, model.PosZ);

        // The frame after the drop is drawn at the cockpit, because that is the pose the frame's camera was locked
        // to; see StepDrop.
        dropAnchor = dropFrom;

        // The landing point keeps the boarding XZ and takes the floor's height, exactly the way the pilot-exit arc
        // does, so the canopy puts the player back on the spot they jumped from in the first place.
        float ground = App.Instance.collider.FloorHeightUnder(
            new Vector3(exitSpot.X, GroundY, exitSpot.Z),
            new Vector3(0.25f, 0.5f, 0.125f));

        dropTo = new Vector3(exitSpot.X, ground + (model.Height ?? 0f) * 0.5f, exitSpot.Z);

        float dx = dropTo.X - dropFrom.X;
        float dz = dropTo.Z - dropFrom.Z;
        float reach = MathF.Sqrt(dx * dx + dz * dz);

        dropSeconds = Math.Clamp(reach / GlideSpeed, DropMinSeconds, DropMaxSeconds);

        // Face the way the canopy is going, not the way the nose was pointing: the two differ by up to half a turn
        // when the drop starts on the far side of the ring.
        dropYaw = reach > 1e-3f ? MathF.Atan2(-dx, -dz) : model.Rotation;
        dropYawCurrent = model.Rotation;

        PlaceChute(dropAnchor, model);

        if (model.GetCurrentAnimationName() != "Idle-loop")
            model.PlayAnimation("Idle-loop");
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        // Same frame-delta contract the boat uses: a stalled frame must not jump the aircraft across a leg.
        float delta = float.IsFinite(time) ? Math.Clamp(time, 0f, 0.1f) : 0f;

        stageTime += delta;
        Advance();

        // Posture is eased rather than switched so the roll does not pop when the climb hands over to the turn,
        // and the pitch settles as the descent levels off for touchdown. The eased angles live in their own
        // fields because the pose the body carries is published one update behind (see PublishPose).
        float posture = MathF.Min(1f, PostureFollowPerSecond * delta);
        pitch += (targetPitch - pitch) * posture;
        roll += (targetRoll - roll) * posture;
        body.Pitch = pitch;
        body.Roll = roll;

        if (exiting)
            StepExit(delta);

        // A pilot who stepped out over the side is flown home by the canopy; the aircraft carries on with the same
        // script either way, because nothing in it was ever flown by hand.
        if (dropping)
            StepDrop(delta);

        // The aircraft flies itself, so the panel is the only thing that moves the view with it. The shoulder rig
        // sits 3.5 m behind the seat - inside the hull and the tail - so the ride hands the camera to a chase station
        // astern, eased in over the first second of the countdown. From then on the rig is carried along rigidly, so
        // the player can still drag the view around and scroll in or out for the rest of the trip.
        if (Aboard && !exiting)
            ChaseCamera(delta);

        SpinPropeller();

        PublishPose();

        if (body.Update(time))
        {
            result = true;
        }

        if (dropping && chute.Update(time))
        {
            result = true;
        }

        if (UpdateCountdown(time))
        {
            result = true;
        }

        return result;
    }

    void Enter(Stage next)
    {
        stage = next;
        stageTime = 0f;
    }

    /// <summary>
    /// Poses the camera astern of the aircraft. The first second is a hand-over: the rig the boarding leap left
    /// the view in is blended onto the chase station, so nothing cuts on the first frame. After that the station is
    /// carried rigidly - yaw with the airframe, translate with it - which keeps whatever the player has dragged and
    /// scrolled the view into, instead of re-aiming every frame and taking the controls away.
    /// </summary>
    void ChaseCamera(float delta)
    {
        var app = App.Instance;
        var anchor = new Vector3(body.PosX, body.PosY, body.PosZ);

        if (chaseBlend < 1f)
        {
            chaseBlend = MathF.Min(1f, chaseBlend + delta / ChaseBlendSeconds);

            var nose = new Vector3(MathF.Sin(body.Rotation), 0f, MathF.Cos(body.Rotation));
            var stationPos = anchor - nose * ChaseBack + Vector3.UnitY * ChaseLift;
            var stationLook = anchor + nose * ChaseAhead + Vector3.UnitY * ChaseLookLift;

            float t = chaseBlend * chaseBlend * (3f - 2f * chaseBlend);
            app.CameraPos = Vector3.Lerp(chaseFromPos, stationPos, t);
            app.CameraTarget = Vector3.Lerp(chaseFromTarget, stationLook, t);
        }
        else
        {
            // Rigid follow: rotate the rig about the aircraft by the yaw it turned this frame, then carry it along.
            float dyaw = WrapAngle(body.Rotation - chaseYaw);
            app.CameraPos = RotateY(app.CameraPos - chaseAnchor, dyaw) + anchor;
            app.CameraTarget = RotateY(app.CameraTarget - chaseAnchor, dyaw) + anchor;
        }

        chaseAnchor = anchor;
        chaseYaw = body.Rotation;
    }

    /// <summary>Rotates a vector about world +Y, the same handedness the camera drag uses.</summary>
    static Vector3 RotateY(Vector3 v, float angle)
    {
        if (angle == 0f)
            return v;

        float c = MathF.Cos(angle), s = MathF.Sin(angle);
        return new Vector3(v.X * c + v.Z * s, v.Y, -v.X * s + v.Z * c);
    }

    /// <summary>
    /// Flies the canopy home. The descent is a closed form of the drop clock - one eased parameter drives all three
    /// axes, plus a small sway across the track that dies out at both ends, so the touchdown stays exactly on the
    /// target - and the view is carried with the pilot the same way <see cref="ChaseCamera"/> carries it with the
    /// airframe: translate the rig by this update's step, and draw the body at the previous step, which is the pose
    /// the frame's camera was locked to. Without that handshake the canopy would judder for the same reason the
    /// chase station did before <see cref="PublishPose"/>.
    /// </summary>
    void StepDrop(float delta)
    {
        var app = App.Instance;
        var model = App.Instance.player?.model;
        if (model == null)
        {
            EndDrop();
            return;
        }

        if (dropSettling)
        {
            // Feet down: ease the view off the descent and back onto the walking rig, the same hand-back the
            // pilot-exit arc makes, so the player is left looking at the island from the shoulder camera.
            settleTime += delta;

            var rigTarget = new Vector3(model.PosX, model.PosY + 3f, model.PosZ - 2.5f);
            var rigPos = rigTarget + new Vector3(0f, 0.5f, -3.5f);
            float ease = MathF.Min(1f, ExitCameraFollowPerSecond * delta);
            app.CameraPos = Vector3.Lerp(app.CameraPos, rigPos, ease);
            app.CameraTarget = Vector3.Lerp(app.CameraTarget, rigTarget, ease);

            if (settleTime >= DropSettleSeconds)
            {
                dropSettling = false;
                dropping = false;
            }

            return;
        }

        dropTime += delta;
        float t = MathF.Min(dropTime / dropSeconds, 1f);
        var next = GlidePoint(t);

        var carry = next - dropAnchor;
        app.CameraPos += carry;
        app.CameraTarget += carry;

        // The body is drawn one update behind, matching the camera this frame is drawn with - see PublishPose for
        // why every frame pairs the older pose with the older camera.
        model.PosX = dropAnchor.X;
        model.PosY = dropAnchor.Y;
        model.PosZ = dropAnchor.Z;

        // Turn out of the cockpit's heading onto the glide, rather than snapping round on the first frame. The
        // facing is written from its own state so the camera-facing sync earlier in the frame cannot ripple through
        // the easing and make the canopy twitch.
        dropYawCurrent += WrapAngle(dropYaw - dropYawCurrent) * MathF.Min(1f, DropTurnPerSecond * delta);
        model.Rotation = dropYawCurrent;

        PlaceChute(dropAnchor, model);

        dropAnchor = next;

        if (t >= 1f)
            TouchDown(model);
    }

    /// <summary>
    /// Where the canopy is <paramref name="t"/> of the way home: an eased line from the cockpit to the landing spot
    /// with a gentle pendulum swing across the track. The easing makes the drop leave the aircraft and meet the
    /// ground gently instead of at speed, and the sway fades to nothing at both ends, so the first frame continues
    /// the cockpit and the last frame is exactly the landing point.
    /// </summary>
    Vector3 GlidePoint(float t)
    {
        float eased = t * t * (3f - 2f * t);
        var point = Vector3.Lerp(dropFrom, dropTo, eased);

        float dx = dropTo.X - dropFrom.X;
        float dz = dropTo.Z - dropFrom.Z;
        float reach = MathF.Sqrt(dx * dx + dz * dz);

        if (reach > 1e-3f)
        {
            // Across the track, in the ground plane: the canopy hangs off the lines and swings a little.
            var across = new Vector3(dz / reach, 0f, -dx / reach);
            float swing = DropSway * MathF.Sin(MathF.PI * t) * MathF.Sin(t * DropSways * MathF.Tau);
            point += across * swing;
        }

        return point;
    }

    /// <summary>
    /// Ends the glide: the pilot goes down exactly on the target, the canopy disappears, and the view starts easing
    /// back onto the walking rig. The aircraft is not touched - it has been flying its own script all the way down.
    /// </summary>
    void TouchDown(Season.Controls.Model model)
    {
        model.PosX = dropTo.X;
        model.PosY = dropTo.Y;
        model.PosZ = dropTo.Z;
        model.Rotation = dropYaw;

        dropAnchor = dropTo;
        dropSettling = true;
        settleTime = 0f;

        HideChute();

        if (model.GetCurrentAnimationName() != "Idle-loop")
            model.PlayAnimation("Idle-loop");
    }

    /// <summary>Drops the canopy state without a landing - the body disappeared mid-descent.</summary>
    void EndDrop()
    {
        dropping = false;
        dropSettling = false;
        HideChute();
    }

    /// <summary>
    /// Hangs the canopy over the pilot. The asset's wire knot is the bottom of its bounding box, so scaling the box
    /// to <see cref="ChuteSpan"/> across and setting its bottom just over the pilot's head hangs the canopy the
    /// height the asset itself describes, without a second set of numbers to keep in step.
    /// </summary>
    void PlaceChute(Vector3 at, Season.Controls.Model pilot)
    {
        var local = chute.LocalSize;
        if (local.X > 1e-4f)
        {
            float scale = ChuteSpan / local.X;

            chute.Width = local.X * scale;
            chute.Height = local.Y * scale;
            chute.Depth = local.Z * scale;
        }

        float head = at.Y + (pilot.Height ?? 1f) * 0.5f;

        chute.PosX = at.X;
        chute.PosY = head + ChuteHang + (chute.Height ?? 0f) * 0.5f;
        chute.PosZ = at.Z;
        chute.Rotation = pilot.Rotation;
        chute.Alpha = 1f;
    }

    /// <summary>Takes the canopy out of the frame. A zero alpha also keeps it out of the shadow atlas.</summary>
    void HideChute() => chute.Alpha = 0f;

    /// <summary>
    /// Publishes this update's pose one update late.
    ///
    /// <para><b>Why.</b> The frame loop rebuilds the main view - and therefore the camera the passes draw
    /// with - at the top of the frame, before <c>App.Update</c> runs (see the platform loop: the camera
    /// constant buffer is written, then the app updates, then the passes are recorded). Every frame is
    /// therefore drawn with this update's airframe but with the previous update's camera. A walking body does
    /// not care, because its camera lags by one step of ten centimetres; a chase station that is locked to
    /// the airframe does, because the skew is one frame of travel - 0.8 m at the ring's 24 m/s - so the
    /// aircraft pulses between 13.0 m and 13.8 m from the lens every frame, which reads as the airframe
    /// juddering fore and aft. The formation hides the same skew because 0.8 m at 150 m is a fifth of a
    /// degree, and its screen motion is small next to ours.</para>
    ///
    /// <para><b>The fix.</b> Holding the pose back by exactly one update cancels the skew: the pose drawn on
    /// a frame is the one the camera drawn with it was locked to. The picture becomes a consistent snapshot
    /// one update old - invisible on a scripted ride - instead of two halves of the frame a whole update
    /// apart, which everyone sees.</para>
    /// </summary>
    void PublishPose()
    {
        var pose = new AirframePose(body.PosX, body.PosY, body.PosZ, body.Rotation, body.Pitch, body.Roll);

        if (poseNextReady)
        {
            var held = poseNext;

            body.PosX = held.X;
            body.PosY = held.Y;
            body.PosZ = held.Z;
            body.Rotation = held.Yaw;
            body.Pitch = held.Pitch;
            body.Roll = held.Roll;
        }

        poseNext = pose;
        poseNextReady = true;
    }

    /// <summary>
    /// Writes this frame's pose for the current leg. Every case is a closed form of <see cref="stageTime"/>, and
    /// each leg's first frame reproduces the previous leg's last frame, so the aircraft never jumps between legs.
    /// </summary>
    void Advance()
    {
        switch (stage)
        {
            case Stage.Parked:
                body.Rotation = ParkYaw;
                PlaceOnRunway(ParkX, GroundY);
                targetPitch = 0f;
                targetRoll = 0f;
                break;

            case Stage.Countdown:
                body.Rotation = ParkYaw;
                PlaceOnRunway(ParkX, GroundY);
                targetPitch = 0f;
                targetRoll = 0f;
                if (stageTime >= CountdownSeconds)
                    Enter(Stage.Taxi);
                break;

            case Stage.Taxi:
            {
                float run = MathF.Min(0.5f * TaxiAccel * stageTime * stageTime, RunwayLength);

                body.Rotation = ParkYaw;
                PlaceOnRunway(ParkX + run, GroundY);
                targetPitch = 0f;
                targetRoll = 0f;

                if (run >= RunwayLength)
                    Enter(Stage.Climb);
                break;
            }

            case Stage.Climb:
            {
                float run = LaunchSpeed * stageTime;
                if (run >= ClimbDistance)
                {
                    run = ClimbDistance;
                    Enter(Stage.TurnIn);
                }

                body.Rotation = ParkYaw;
                PlaceOnRunway(TakeoffX + run, GroundY + ClimbSlope * ClimbRise(run));
                targetPitch = MathF.Atan(ClimbSlope * ClimbRampWeight(run));
                targetRoll = 0f;
                break;
            }

            case Stage.TurnIn:
            {
                // Arc length under constant acceleration, so the jet arrives on the ring already flying the
                // formation's 24 m/s instead of stepping up to it at the tangency point.
                float speed = LaunchSpeed + TurnAccel * stageTime;
                float run = LaunchSpeed * stageTime + 0.5f * TurnAccel * stageTime * stageTime;
                if (run >= TurnLength)
                {
                    run = TurnLength;
                    speed = OrbitSpeed;
                    Enter(Stage.Orbit);
                }

                float sweep = run / TurnRadius;

                body.PosX = TurnStartX + TurnRadius * MathF.Sin(sweep);
                body.PosZ = RunwayZ + TurnRadius * (1f - MathF.Cos(sweep));
                body.PosY = FlightAltitude;
                body.Rotation = ParkYaw - sweep;
                targetPitch = 0f;
                targetRoll = MathF.Atan(speed * speed / (TurnRadius * Gravity));
                break;
            }

            case Stage.Orbit:
            {
                float arc = OrbitSpeed * stageTime;
                float lap = MathF.Tau * OrbitRadius;
                if (arc >= OrbitLaps * lap)
                {
                    arc = OrbitLaps * lap;
                    Enter(Stage.Swing);
                }

                float theta = OrbitEntryAngle + arc / OrbitRadius;
                float x = MathF.Cos(theta) * OrbitRadius;
                float z = MathF.Sin(theta) * OrbitRadius;

                body.PosX = x;
                body.PosZ = z;
                body.PosY = FlightAltitude + FormationLift(x, z) * LiftFade(arc, OrbitLaps * lap);
                body.Rotation = -theta;   // tangent (-sin, cos) in aircraft yaw terms
                targetPitch = 0f;
                targetRoll = MathF.Atan(OrbitSpeed * OrbitSpeed / (OrbitRadius * Gravity));
                break;
            }

            case Stage.Swing:
            {
                float t = Math.Clamp(stageTime / SwingSeconds, 0f, 1f);
                SwingPose(t, out var position, out float yaw, out float bank);

                body.PosX = position.X;
                body.PosY = FlightAltitude;
                body.PosZ = position.Y;
                body.Rotation = yaw;
                targetPitch = 0f;
                targetRoll = bank;

                if (t >= 1f)
                    Enter(Stage.Descent);
                break;
            }

            case Stage.Descent:
            {
                float run = LaunchSpeed * stageTime;
                if (run >= ClimbDistance)
                {
                    run = ClimbDistance;
                    Enter(Stage.Rollout);
                }

                // The mirror of the climb: the same profile read from the top down, which is what makes the
                // touchdown happen exactly on the takeoff point with the flare the ramp already provides.
                float left = ClimbDistance - run;

                body.Rotation = LandingYaw;
                PlaceOnRunway(TurnStartX - run, GroundY + ClimbSlope * ClimbRise(left));
                targetPitch = -MathF.Atan(ClimbSlope * ClimbRampWeight(left));
                targetRoll = 0f;
                break;
            }

            case Stage.Rollout:
            {
                // Same brake curve the taxi accelerates on, read backwards: 16 m/s at touchdown, 0 over the 27 m
                // to the parking spot. The leg ends on the clock rather than on the ramp, because the ramp only
                // grazes RunwayLength at its apex and a float comparison there would never fire.
                float run = LaunchSpeed * stageTime - 0.5f * TaxiAccel * stageTime * stageTime;
                if (stageTime >= RolloutSeconds)
                {
                    run = RunwayLength;
                    Enter(Stage.TurnAround);

                    // An empty cockpit has nobody to lift out: a pilot who bailed out over the island stays where
                    // the canopy put them while the aircraft finishes its approach and turns around on its own.
                    if (Aboard)
                        StartExit();
                }

                body.Rotation = LandingYaw;
                PlaceOnRunway(TakeoffX - run, GroundY);
                targetPitch = 0f;
                targetRoll = 0f;
                break;
            }

            case Stage.TurnAround:
            {
                float turn = MathF.PI * Math.Clamp(stageTime / TurnAroundSeconds, 0f, 1f);

                // A left pivot on the spot: -pi/2 - pi ends on -3pi/2, the same heading as +pi/2 with the nose
                // back out to sea. Turning left sweeps the wings over the grass south of the road rather than
                // over the pond's east dike.
                body.Rotation = LandingYaw - turn;
                PlaceOnRunway(ParkX, GroundY);
                targetPitch = 0f;
                targetRoll = 0f;

                if (stageTime >= TurnAroundSeconds)
                    Enter(Stage.Parked);
                break;
            }
        }
    }

    /// <summary>Distance from the parking spot to the takeoff point, i.e. the length of the ground run.</summary>
    const float RunwayLength = TakeoffX - ParkX;

    void PlaceOnRunway(float x, float y)
    {
        body.PosX = x;
        body.PosY = y;
        body.PosZ = RunwayZ;
    }

    /// <summary>
    /// Height gained after <paramref name="distance"/> metres of climb: the integral of
    /// <see cref="ClimbSlope"/> times the smoothstep ramp, which is slope-at-full-rate beyond the ramp.
    /// </summary>
    static float ClimbRise(float distance)
    {
        if (distance <= 0f)
            return 0f;

        if (distance >= ClimbRamp)
            return distance - ClimbRamp * 0.5f;

        float t = distance / ClimbRamp;

        // Integral of the smoothstep 3t^2 - 2t^3, in metres: Ramp * (t^3 - t^4 / 2).
        return ClimbRamp * (t * t * t - 0.5f * t * t * t * t);
    }

    /// <summary>Share of the climb slope in force at <paramref name="distance"/>: 0 at the start, 1 past the ramp.</summary>
    static float ClimbRampWeight(float distance)
    {
        if (distance <= 0f)
            return 0f;

        if (distance >= ClimbRamp)
            return 1f;

        float t = distance / ClimbRamp;
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// The return swing: a cubic Bezier in the XZ plane from the ring exit to the runway centre line, leaving on
    /// the orbit tangent and arriving on the landing heading. The bank comes from the turn rate of the tangent -
    /// the coordinated-turn relation tan(roll) = omega * v / g - so the wings follow the curve without a table of
    /// keyframes.
    /// </summary>
    void SwingPose(float t, out Vector2 position, out float yaw, out float bank)
    {
        // Ring exit: the tangency point, arriving along the orbit.
        var exit = new Vector2(
            MathF.Cos(OrbitEntryAngle) * OrbitRadius,
            MathF.Sin(OrbitEntryAngle) * OrbitRadius);

        var approach = new Vector2(TurnStartX, RunwayZ);

        var orbitTangent = new Vector2(-MathF.Sin(OrbitEntryAngle), MathF.Cos(OrbitEntryAngle));
        var landingTangent = new Vector2(-1f, 0f);

        var c1 = exit + orbitTangent * (SwingExitHandle / 3f);
        var c2 = approach - landingTangent * (SwingApproachHandle / 3f);

        position = Bezier(exit, c1, c2, approach, t);
        yaw = BezierHeading(exit, c1, c2, approach, t);

        // A short look ahead gives the turn rate. The step deliberately runs past t = 1 on the last frame: the
        // cubic is just a polynomial, so its derivative there is still the landing tangent, while clamping the
        // step to 1 would divide by zero and slam the bank against its limit for the last frame of the leg.
        const float step = 0.02f;
        float ahead = t + step;

        var aheadPosition = Bezier(exit, c1, c2, approach, ahead);
        float yawAhead = BezierHeading(exit, c1, c2, approach, ahead);
        float omega = WrapAngle(yawAhead - yaw) / (step * SwingSeconds);

        // Coordinated turn, tan(roll) = omega * v / g, with v taken from the curve itself: the swing leaves the
        // ring at 24 m/s and arrives at the descent's 16, so a fixed speed would bank the approach far too hard.
        float speed = (aheadPosition - position).Length() / (step * SwingSeconds);

        bank = MathF.Atan(-omega * speed / Gravity);
        bank = Math.Clamp(bank, -0.7f, 0.7f);
    }

    static Vector2 Bezier(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        float u = 1f - t;
        return u * u * u * p0
             + 3f * u * u * t * p1
             + 3f * u * t * t * p2
             + t * t * t * p3;
    }

    static float BezierHeading(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        float u = 1f - t;
        var d = 3f * u * u * (p1 - p0)
              + 6f * u * t * (p2 - p1)
              + 3f * t * t * (p3 - p2);

        // Aircraft forward is (sin yaw, cos yaw) in (X, Z).
        return MathF.Atan2(d.X, d.Y);
    }

    /// <summary>Wraps an angle difference into (-pi, pi] so a turn across the tangent seam is not read as a spin.</summary>
    static float WrapAngle(float angle)
    {
        while (angle > MathF.PI)
            angle -= MathF.Tau;

        while (angle <= -MathF.PI)
            angle += MathF.Tau;

        return angle;
    }

    /// <summary>
    /// Height added to the ring to keep the jet clear of the formation it just joined. The bump is squared at the
    /// edge, so the aircraft eases up and back down; with ten aircraft on the same ring a fixed offset would just
    /// move the collision, this makes the two layers miss each other.
    /// </summary>
    float FormationLift(float x, float z)
    {
        var formation = App.Instance.planes;
        if (formation == null)
            return 0f;

        float lift = 0f;
        var aircraft = formation.Aircraft;

        for (int i = 0; i < aircraft.Count; i++)
        {
            var other = aircraft[i];
            float dx = other.PosX - x;
            float dz = other.PosZ - z;
            float distanceSquared = dx * dx + dz * dz;

            if (distanceSquared >= AvoidRadius * AvoidRadius)
                continue;

            float close = 1f - MathF.Sqrt(distanceSquared) / AvoidRadius;
            lift = MathF.Max(lift, AvoidLift * close * close);
        }

        return lift;
    }

    /// <summary>
    /// How much of the avoidance bump the ring is allowed to use, as a smoothstep over the first and last
    /// <see cref="LiftRamp"/> metres of the lap. Both ends of the orbit enter and leave at
    /// <see cref="FlightAltitude"/> exactly - the turn-in holds that height and the return swing holds it too - so
    /// without this fade the jet would skip up by the bump's full height on the first frame of the ring.
    /// </summary>
    static float LiftFade(float arc, float total)
    {
        float edge = MathF.Min(arc, total - arc) / LiftRamp;
        if (edge <= 0f)
            return 0f;

        if (edge >= 1f)
            return 1f;

        return edge * edge * (3f - 2f * edge);
    }

    /// <summary>
    /// Spins the propeller for the legs that are actually moving: the ground run, the flight and the landing roll -
    /// with the pilot in it, and equally with an empty cockpit after a bail-out, because the jet flies the whole
    /// script on its own. The parked aircraft sits with still blades, the countdown does not spin them up (nothing
    /// has started moving yet), and they stop the moment the wheels are back on the parking spot, so the pivot to
    /// face the sea again happens with a stopped propeller. The asset loads asynchronously, so a start that arrives
    /// before the clip does is retried on the next frame.
    /// </summary>
    void SpinPropeller()
    {
        bool wanted = stage is Stage.Taxi or Stage.Climb or Stage.TurnIn or Stage.Orbit
            or Stage.Swing or Stage.Descent or Stage.Rollout;

        if (wanted)
        {
            if (spinning || !body.Ready)
                return;

            var names = body.GetAnimationNames();
            if (names.Count > 0)
            {
                spinning = body.PlayAnimation(names[0]) != null;
                propellerParked = false;
            }

            return;
        }

        // Not wanted: still blades. This has to run once even though nothing was ever started - the loader plays the
        // asset's first, and only, clip as soon as it lands, so a parked aircraft would otherwise idle at the
        // threshold with its propeller turning before the trip has begun. Ready turns true a frame before the
        // shared-model continuation injects the asset, and StopAnimationAtRest declines while that asset is still
        // null, so the parked flag is only consumed once the stop has actually landed - the same retry Boat's
        // SyncPlayback and Player's idle switch use, and the reason a plain "Ready, therefore parked" does not hold.
        // The flag then keeps the rest from re-evaluating the node tree on every parked frame.
        if (spinning || !propellerParked)
        {
            spinning = false;

            if (body.StopAnimationAtRest())
                propellerParked = true;
        }
    }

    /// <summary>
    /// Lifts the pilot out of the cockpit, back to the spot the boarding leap started from. The plane owns the body
    /// for the length of the arc (Player.planeExit keeps the seat bind and the floor pin off). The cockpit never
    /// changes the body's size, so unlike the boat's deck there is nothing to grow back on the way out.
    /// </summary>
    void StartExit()
    {
        var player = App.Instance.player;
        var model = player?.model;
        if (model == null)
        {
            Aboard = false;
            return;
        }

        exitFrom = new Vector3(model.PosX, model.PosY, model.PosZ);

        // The landing point keeps the boarding XZ and takes the floor's height, so a leap that started on the
        // grass beside the road comes back down on that same grass rather than at the cockpit's altitude.
        float ground = App.Instance.collider.FloorHeightUnder(
            new Vector3(exitSpot.X, GroundY, exitSpot.Z),
            new Vector3(0.25f, 0.5f, 0.125f));

        exitTo = new Vector3(exitSpot.X, ground + (model.Height ?? 0f) * 0.5f, exitSpot.Z);

        exitTime = 0f;
        exiting = true;
        player.planeExit = true;
        model.PlayAnimation("LongJump");
    }

    void StepExit(float delta)
    {
        var player = App.Instance.player;
        var model = player?.model;
        if (model == null)
        {
            exiting = false;
            Aboard = false;
            return;
        }

        exitTime += delta;
        float t = Math.Clamp(exitTime / ExitSeconds, 0f, 1f);

        model.PosX = exitFrom.X + (exitTo.X - exitFrom.X) * t;
        model.PosZ = exitFrom.Z + (exitTo.Z - exitFrom.Z) * t;
        model.PosY = exitFrom.Y + (exitTo.Y - exitFrom.Y) * t + ExitArc * MathF.Sin(MathF.PI * t);

        // Ease the view off the chase station and back onto the walking rig while the pilot arcs down, so the
        // landing is watched from the shoulder camera the rest of the session uses rather than 17 m astern.
        var rigTarget = new Vector3(model.PosX, model.PosY + 3f, model.PosZ - 2.5f);
        var rigPos = rigTarget + new Vector3(0f, 0.5f, -3.5f);
        float ease = MathF.Min(1f, ExitCameraFollowPerSecond * delta);
        var app = App.Instance;
        app.CameraPos = Vector3.Lerp(app.CameraPos, rigPos, ease);
        app.CameraTarget = Vector3.Lerp(app.CameraTarget, rigTarget, ease);

        float forwardX = exitTo.X - exitFrom.X;
        float forwardZ = exitTo.Z - exitFrom.Z;
        if (forwardX * forwardX + forwardZ * forwardZ > 1e-6f)
            model.Rotation = MathF.Atan2(-forwardX, -forwardZ);

        if (t < 1f)
            return;

        exiting = false;
        Aboard = false;
        player.planeExit = false;

        if (model.GetCurrentAnimationName() != "Idle-loop")
            model.PlayAnimation("Idle-loop");
    }

    /// <summary>
    /// Draws the three-second countdown ahead of the nose. The digits are a Texts child on the overlay domain, but
    /// their position comes from projecting a world point just past the nose cone, so they hang in the air right
    /// where the aircraft is pointed instead of sitting on a fixed corner of the screen.
    /// This is the countdown's only Update call: it hides the digits and places them itself, so there is nothing
    /// left to do for them in the panel's own update.
    /// </summary>
    bool UpdateCountdown(float time)
    {
        if (stage != Stage.Countdown)
        {
            countdown.Alpha = 0f;
            return countdown.Update(time, alpha: 0f);
        }

        int digit = Math.Clamp(3 - (int)MathF.Floor(MathF.Min(stageTime, CountdownSeconds - 0.001f)), 1, 3);
        string text = digit.ToString();
        if (countdown.ContentOrigin != text)
            countdown.Content = text;

        countdown.Alpha = 1f;

        // Anchor: just past the nose cone and above the propeller disc, so the digits hang right where the
        // aircraft is pointed instead of floating off in the distance, and no blade sweeps through them.
        var forward = new Vector3(MathF.Sin(body.Rotation), 0f, MathF.Cos(body.Rotation));
        var anchor = new Vector3(body.PosX, body.PosY, body.PosZ)
                   + forward * (ModelDepth * 0.5f + 1.2f)
                   + Vector3.UnitY * 2.8f;

        var resolution = App.Instance.ExtendResolution;
        float x = resolution.X * 0.5f;
        float y = resolution.Y * 0.3f;

        var view = Season.Rendering.RenderView.CurrentOrNull;
        if (view != null && view.ProjectToViewFraction(anchor, out float u, out float v))
        {
            x = u * resolution.X;
            y = v * resolution.Y;
        }

        // Centre the glyph box on that point; the size is the one the previous frame's layout produced, so the
        // first frame of the countdown is off by half a glyph and every frame after it is exact.
        x -= (countdown.Width ?? 0f) * 0.5f;
        y -= (countdown.Height ?? 0f) * 0.5f;

        // While the digits are counting the aircraft is standing still, so the only dirty frame is the one where
        // the glyphs themselves changed; the position is the same point in the air for all three seconds.
        return countdown.Update(time, alpha: 1f, posX: x, posY: y);
    }

    /// <summary>
    /// One update's worth of airframe pose, held between the update that computes it and the frame that draws
    /// it. See <see cref="PublishPose"/> for why a frame is always drawn from the previous update's pose.
    /// </summary>
    readonly struct AirframePose
    {
        internal readonly float X, Y, Z, Yaw, Pitch, Roll;

        internal AirframePose(float x, float y, float z, float yaw, float pitch, float roll)
        {
            X = x;
            Y = y;
            Z = z;
            Yaw = yaw;
            Pitch = pitch;
            Roll = roll;
        }
    }
}

/// <summary>
/// The airframe: an <see cref="AttitudeBody"/> under the name this panel reads it by. The asset has no flight
/// animation at all - its single clip is the propeller spin - so pitch and bank are code, exactly the way
/// <see cref="BoatHull"/> adds the boat's rocking; the matrix that turns those two angles into a pose is shared with
/// the pilot's own body, who has to tilt with the seat they are strapped into.
/// </summary>
internal sealed class PlaneBody : AttitudeBody
{
}
