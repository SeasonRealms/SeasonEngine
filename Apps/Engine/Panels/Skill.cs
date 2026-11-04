// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Engine.Panels;

internal class Skill : Panel
{
    Shape skill;

    Texts skillTexts;

    float jumpStartTime;        // Jump start time in App.Instance.Time.
    float jumpDuration = FallbackLongJumpDuration; // Animation duration for this jump, resolved from asset metadata when the jump starts.
    Vector3 jumpForward;        // Locked movement direction, taken from the facing direction at takeoff.
    Vector3 jumpOrigin;         // Takeoff position, restored when the jump comes down on water that has nothing to stand on.
    float jumpBaseline;         // Y the arc is measured from; eased toward the surface under the flight every frame.
    string jumpResumeAnimation = "Idle-loop"; // Animation restored after landing, usually Idle-loop or Run-loop from before takeoff.

    // Facing angle to horizontal forward direction, matching MovePlayer's four-way movement convention:
    // yaw=pi means +Z forward, 0 means -Z backward, pi/2 means -X left, and -pi/2 means +X right.
    // Therefore forward = (-sin yaw, 0, -cos yaw).
    static Vector3 YawForward(float yaw) => new Vector3(-MathF.Sin(yaw), 0f, -MathF.Cos(yaw));

    // Long-jump skill, added in 2026-08 and triggered by the Skill button OnClick.
    // It starts only when the current animation is not already LongJump.
    // The button switches to LongJump, locks movement direction to the facing direction at takeoff,
    // ignores movement input during the jump, moves PosY along a sine arc, advances forward
    // at roughly twice running speed, and restores the pre-jump animation on landing.
    // The animation duration is read from Model.GetAnimations() metadata at takeoff.
    // If unavailable, it falls back to FallbackLongJumpDuration, which matches the measured
    // LongJump sampler input max of about 2.083s in 3DGodotRobot.glb.
    const float FallbackLongJumpDuration = 25f / 12f;
    const float JumpHeight = 1f;        // Peak height of the PosY arc, in meters.

    const float JumpSpeed = 2f; // RunSpeed * 2f;   // Forward speed during long jump, about 2x running speed.

    // How far one full leap carries the body, in metres. This is the reach that decides whether the basket is
    // close enough to the tower top, or to the ground below it, to be stepped out of: the balloon stays parked in
    // the air while the player is away, so leaving it has to happen somewhere the player can board it again.
    const float JumpReach = JumpSpeed * FallbackLongJumpDuration;

    // -- Boat deck. The hull's plan rectangle is the only deck there is, and its rim is gunwale rather than floor, so
    //    the boarding test shrinks the rectangle by this much before a touchdown counts as a landing on deck. --
    const float DeckLandingMargin = 0.1f;

    // -- Balloon basket. The wicker rectangle is a quarter of the hull's, and its rim is a wall the player can be
    //    stopped a step short of by the tower railing, so the boarding test grows the rectangle instead of shrinking
    //    it: a body that has reached the railing has reached the basket. --
    const float BasketBoardReach = 0.5f;

    // The basket rides some 20 m up, so contact alone would let a jump from the grass underneath board it. The feet
    // must already be at seat height, give or take the jump's own 1 m arc.
    const float BasketAltitudeMargin = 1.6f;

    // How far outside the basket's plan the flight still treats the seat as the ground under it. The gap
    // between the tower railing and the basket is only 0.4 m while the baseline eases at 10 m/s, so without this the
    // arc would start dropping toward the lawn for a frame or two while crossing it.
    const float BasketArcReach = 1.2f;

    // -- Boarding is contact, not touchdown. The leap covers JumpSpeed * jumpDuration = 4.17 m against a 3.4 m hull,
    //    so a jump aimed at the boat routinely comes down past the stern, which made getting aboard a precision
    //    exercise; any point of the flight that reaches the deck rectangle takes the helm instead. --

    /// <summary>Points sampled along the flight to tell at takeoff whether this jump is heading for the deck.</summary>
    const int DeckReachSteps = 8;

    // How fast the arc's baseline follows the surface it flies over. The rim, the deck and the water are 0.4-0.7 m
    // apart, so a single-frame snap would step the trajectory where the existing code simply re-read the floor.
    const float BaselineFollowPerSecond = 10f;

    internal Skill()
    {
        RenderDomain = Season.Controls.RenderDomain.Overlay;

        skill = new Shape()
        {
            Type = ShapeType.Circle,
            // Long-jump skill: starts only when the current animation is not already LongJump.
            OnClick = () =>
            {
                //App.Instance.player?.model.PlayAnimation("Idle-loop");
                StartLongJump();
            }
        };
        AddControl(skill);

        skillTexts = new Texts()
        {
            Content = "Jump",
            Scale = Vector2.One * 1.5f
        };
        AddControl(skillTexts);
    }

    // Starts a long jump from the Skill button OnClick.
    // If the player is already jumping or already playing LongJump, the request is ignored.
    // Otherwise it records the animation to restore on landing and the takeoff facing direction,
    // then switches to LongJump. Rotation is left unchanged and jumpForward is derived from the current facing direction.
    void StartLongJump()
    {
        if (App.Instance.player.jumping)
            return;

        var model = App.Instance.player?.model;
        if (model == null || !model.Ready)
            return;

        var current = model.GetCurrentAnimationName();
        if (current == "LongJump")
            return;

        jumpResumeAnimation = current ?? "Idle-loop";

        // At the oars the hull's bow decides, and for two reasons: the body is bound to the boat's heading rather than
        // to the camera, and in Character mode the per-frame facing sync has already written the camera's direction into
        // model.Rotation by the time this runs. Taking the heading keeps the takeoff where the oarsman is pointing.
        // In the basket there is no hull to point the body: it already faces wherever the camera looks, and the leap
        // simply follows that facing, exactly like a jump from open ground.
        var boat = App.Instance.boat;
        var balloon = App.Instance.hotAirBalloon;

        // A leap out of the basket is only allowed while the balloon would stay reachable from where the player comes
        // down; anywhere else the press is a no-op and the player keeps standing in the basket.
        if (balloon != null && balloon.Aboard && !CanLeaveBasket(balloon, model))
            return;

        if (boat != null && boat.Aboard)
        {
            model.Rotation = boat.Heading;
            jumpForward = YawForward(boat.Heading);
        }
        else
        {
            jumpForward = YawForward(model.Rotation);
        }

        jumpStartTime = App.Instance.Time;
        jumpOrigin = new Vector3(model.PosX, model.PosY, model.PosZ);
        jumpBaseline = model.PosY;

        // Resolve animation duration from asset metadata via Model.GetAnimations().
        // If the clip is missing or contains no keyframes, fall back to the default duration.
        jumpDuration = FallbackLongJumpDuration;
        foreach (var info in model.GetAnimations())
        {
            if (info.Name == "LongJump" && info.Duration > 0f)
            {
                jumpDuration = info.Duration;
                break;
            }
        }

        // The size change is aimed here, at takeoff, rather than left to the touchdown: the body starts shrinking
        // on the way to the deck and starts growing on the way off it, so the pose arrives with the jump instead of
        // popping when it lands. A leap off the deck grows back, and only a leap whose flight reaches the hull or the
        // basket heads for the deck pose; Land re-aims this at whatever the touchdown turned out to be, so a wrong
        // guess costs a short walk back to the size the body actually needs.
        bool aimingForDeck = boat != null && !boat.Aboard && FlightReachesDeck(boat);
        bool aimingForBasket = balloon != null && !balloon.Aboard && AtBasketAltitude(model, balloon)
            && FlightReachesBasket(balloon);
        App.Instance.player.SetDeckPose(aimingForDeck || aimingForBasket);

        App.Instance.player.jumping = true;

        model.PlayAnimation("LongJump");
    }

    /// <summary>
    /// Whether the straight flight this jump is about to fly passes over the hull's plan rectangle. Both the real
    /// path and this prediction start at the same origin with the same locked heading, so only a collision clamping
    /// the flight can make them differ - and the landing re-aims the pose when that happens.
    /// </summary>
    bool FlightReachesDeck(Boat boat)
    {
        float reach = JumpSpeed * jumpDuration;

        for (int i = 1; i <= DeckReachSteps; i++)
        {
            float distance = reach * i / DeckReachSteps;
            if (boat.ContainsDeck(jumpOrigin.X + jumpForward.X * distance, jumpOrigin.Z + jumpForward.Z * distance, DeckLandingMargin))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether the straight flight this jump is about to fly passes over the basket. The basket is a quarter of the
    /// hull's size, so the same eight samples may straddle it; the reach used here is the same grown rectangle the
    /// contact test uses, which also makes a leap that the tower railing stops just short of the wicker count.
    /// </summary>
    bool FlightReachesBasket(HotAirBalloon balloon)
    {
        float reach = JumpSpeed * jumpDuration;

        for (int i = 1; i <= DeckReachSteps; i++)
        {
            float distance = reach * i / DeckReachSteps;
            if (balloon.ContainsBasket(jumpOrigin.X + jumpForward.X * distance, jumpOrigin.Z + jumpForward.Z * distance, -BasketBoardReach))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether the body is standing at seat height, give or take the jump's own arc. The horizontal test alone
    /// would match a jump from the grass 20 m underneath the balloon long before the feet could reach it, so
    /// every basket test - prediction, contact and landing - is gated on this.
    /// </summary>
    static bool AtBasketAltitude(Season.Controls.Model model, HotAirBalloon balloon)
        => MathF.Abs(model.PosY - (model.Height ?? 0f) * 0.5f - balloon.SeatFootY) <= BasketAltitudeMargin;

    /// <summary>
    /// Whether a leap out of the basket may start at all. The balloon stays parked in the air while the player is
    /// away, so a leap that comes down on open ground away from the tower would leave it hanging out of reach and
    /// impossible to board again. The leap is only allowed while the player would come down somewhere that can be
    /// jumped back from: a walkable stretch of the tower about seat height within one leap of the wicker, or ground
    /// low enough under the basket that the seat is still inside the boarding altitude gate. Anywhere else the
    /// press is a no-op and the player keeps standing in the basket.
    /// </summary>
    bool CanLeaveBasket(HotAirBalloon balloon, Season.Controls.Model model)
    {
        // Low over the ground: a leap from below rides the arc up a metre, so the seat can sit that much above the
        // boarding gate and still be met.
        var bounds = model.GetWorldBoundsRaw();
        float ground = App.Instance.collider.FloorHeightUnder(bounds.Center, bounds.Extents);
        if (balloon.SeatFootY - ground <= BasketAltitudeMargin + JumpHeight)
            return true;

        var tower = App.Instance.tower;
        if (tower == null)
            return false;

        // The tower only publishes its walkable tops through FloorHeightAtWorld, so walk the reach in a coarse grid
        // around the seat. The deck is wider than the step, and every top a leap could start from is within this
        // radius of the seat, so the grid cannot slip between two samples of a surface.
        var seat = balloon.SeatPosition;
        float radius = JumpReach + BasketBoardReach + 1f;
        const float step = 0.5f;

        for (float x = -radius; x <= radius; x += step)
        {
            for (float z = -radius; z <= radius; z += step)
            {
                // Within one leap of the wicker: the leap's reach plus the grown rectangle the contact test uses,
                // so a leap from that top meets the basket before the flight ends.
                if (balloon.PlanDistance(seat.X + x, seat.Z + z) > JumpReach + BasketBoardReach)
                    continue;

                float top = tower.FloorHeightAtWorld(seat.X + x, seat.Z + z);
                if (top <= 0f)
                    continue;

                // About seat height: a leap from that top meets the basket inside the altitude gate.
                if (MathF.Abs(top - balloon.SeatFootY) <= BasketAltitudeMargin + JumpHeight)
                    return true;
            }
        }

        return false;
    }

    // Advances the long jump every frame from Update.
    // The arc is based on the surface under the flight, whether that comes from steps, indoor floors, grass or the
    // boat's deck: PosY = baseline + JumpHeight*sin(pi*t), with the baseline eased toward that height so surfaces of
    // different heights join without a step in the trajectory. Horizontal motion follows jumpForward at JumpSpeed,
    // while the camera follows through App.FollowCamera. Landing is decided in Land.
    void UpdateLongJump(float time)
    {
        if (!App.Instance.player.jumping)
            return;

        var model = App.Instance.player?.model;
        if (model == null)
        {
            App.Instance.player.jumping = false;
            return;
        }

        float t = (App.Instance.Time - jumpStartTime) / jumpDuration;

        var boat = App.Instance.boat;
        var balloon = App.Instance.hotAirBalloon;

        // Floor height at the landing point, based only on X/Z and independent of current PosY.
        // Shared by both the landing position and the arc baseline.
        var bounds = model.GetWorldBoundsRaw();
        float groundY = App.Instance.collider.FloorHeightUnder(bounds.Center, bounds.Extents) + (float)model.Height * 0.5f;

        // The deck is a floor PlayerCollider knows nothing about, so the arc adds it by hand: a takeoff from the boat
        // starts at deck height instead of the water's zero, and a jump aimed at the boat rides over the deck instead
        // of dipping to the water surface underneath it.
        if (boat != null && (boat.Aboard || boat.ContainsDeck(model.PosX, model.PosZ, DeckLandingMargin)))
            groundY = MathF.Max(groundY, boat.DeckY + (float)model.Height * 0.5f);

        // The basket is the same kind of floor, 20 m in the air. It reaches out past its own rim by BasketArcReach so
        // the few frames spent crossing the gap between the tower railing and the wicker keep the arc at basket
        // height instead of starting to fall toward the lawn. The altitude gate is what keeps a jump from the grass
        // underneath from being lifted up here.
        if (balloon != null && AtBasketAltitude(model, balloon)
            && balloon.ContainsBasket(model.PosX, model.PosZ, -BasketArcReach))
            groundY = MathF.Max(groundY, balloon.SeatFootY + (float)model.Height * 0.5f);

        if (t >= 1f)
        {
            Land(boat, balloon, model, groundY, bounds);
            return;
        }

        // Contact boards. The flight is longer than the hull, so waiting for the feet to come down on the deck made
        // a leap that clears the stern land in the water and get refused; touching the deck anywhere overhead takes
        // the helm instead. Gated on not already being aboard, or a jump off the deck would pull the player straight
        // back on at takeoff. The basket is boarded the same way, with the grown rectangle and the altitude gate that
        // its 20 m height needs.
        if (boat != null && !boat.Aboard && boat.ContainsDeck(model.PosX, model.PosZ, DeckLandingMargin))
        {
            Land(boat, balloon, model, groundY, bounds);
            return;
        }

        if (balloon != null && !balloon.Aboard && AtBasketAltitude(model, balloon)
            && balloon.ContainsBasket(model.PosX, model.PosZ, -BasketBoardReach))
        {
            Land(boat, balloon, model, groundY, bounds);
            return;
        }

        // Record the starting point of this frame's displacement, including the arc Y, so the camera
        // can follow the actual movement at the end of the frame.
        var before = new Vector3(model.PosX, model.PosY, model.PosZ);

        // Solve Y first. The arc is based on the floor under the flight so the jump does not clip into steps,
        // and the airborne height participates in TryMove's Y-overlap test so low obstacles are cleared naturally.
        // The baseline is eased rather than copied: following the rim, the deck and the water in one frame would
        // step the trajectory wherever two surfaces of different height meet.
        jumpBaseline += (groundY - jumpBaseline) * MathF.Min(1f, BaselineFollowPerSecond * time);
        model.PosY = jumpBaseline + JumpHeight * MathF.Sin(MathF.PI * Math.Clamp(t, 0f, 1f));

        // Horizontal displacement still goes through collision resolution: when blocked,
        // it clamps to the contact face and may slide or stop, but never tunnels into obstacle bounds.
        // Airborne is what lets the flight cross the water line at all: the walking ledge retraction would stop it
        // at the bank, and the landing test below owns what happens where it comes down.
        var delta = App.Instance.collider.TryMove(
            model.GetWorldBoundsRaw(),
            jumpForward.X * JumpSpeed * time,
            jumpForward.Z * JumpSpeed * time,
            airborne: true);

        model.PosX += delta.X;
        model.PosZ += delta.Y;

        App.Instance.FollowCamera(new Vector3(model.PosX, model.PosY, model.PosZ) - before);
    }

    /// <summary>
    /// Resolves where the jump came down. Four outcomes, in this order: the basket takes the player aboard and binds
    /// the body to the seat in front of the waving crew, the deck takes the player aboard and sits the body at the
    /// oars, open water refuses the landing and restores the takeoff pose, and dry ground ends the trip with the
    /// boat and the balloon left exactly where they are. Boarding is also what a mid-flight touch arrives through, so
    /// the arc can end the moment the body meets the hull or the wicker.
    /// </summary>
    void Land(Boat? boat, HotAirBalloon? balloon, Season.Controls.Model model, float groundY, in Season.Rendering.Bounds3D bounds)
    {
        var app = App.Instance;
        var beforeLand = new Vector3(model.PosX, model.PosY, model.PosZ);

        bool onBasket = balloon != null
            && AtBasketAltitude(model, balloon)
            && balloon.ContainsBasket(model.PosX, model.PosZ, -BasketBoardReach);
        bool onDeck = boat != null && boat.ContainsDeck(model.PosX, model.PosZ, DeckLandingMargin);
        bool wet = app.collider.InWater(bounds)
            || (boat != null && boat.Waterway.Contains(model.PosX, model.PosZ));

        if (onBasket)
        {
            // In the basket: stand on the lifted seat in front of the waving crew. Only the position is bound here;
            // the heading stays with the camera, so the body turns on the spot while the basket keeps its course.
            // The pose is aimed at the end of this method, so a touchdown and a mid-flight touch follow the same
            // path. The seats are exclusive: a body that leaves one for the other must not stay flagged aboard the
            // one it left, or the next frame would bind it straight back.
            if (boat != null && boat.Aboard)
                boat.Disembark();

            balloon!.Board();

            var seat = balloon.SeatPosition;
            model.PosX = seat.X;
            model.PosZ = seat.Z;
            model.PosY = seat.Y + (float)model.Height * 0.5f;
        }
        else if (onDeck)
        {
            // On the deck: take the helm and sit at the oars. The pose is aimed at the end of this method, so a
            // touchdown and a mid-flight touch follow the same path.
            if (balloon != null && balloon.Aboard)
                balloon.Disembark();

            boat!.Board();

            var seat = boat.SeatPosition;
            model.PosX = seat.X;
            model.PosZ = seat.Z;
            model.PosY = seat.Y + (float)model.Height * 0.5f;
            model.Rotation = boat.Heading;
        }
        else if (wet)
        {
            // Nothing to stand on: the jump is refused and the takeoff pose comes back, boat trip included.
            model.PosX = jumpOrigin.X;
            model.PosY = jumpOrigin.Y;
            model.PosZ = jumpOrigin.Z;
        }
        else
        {
            // Dry ground: the player gets off and the boat and the balloon stay where they are.
            if (boat != null && boat.Aboard)
            {
                boat.Disembark();

                // The body is still at deck size at this instant and grows back over the next few frames, with the
                // feet as the fixed end (Player.Update re-reads the floor on every frame of that ramp). This read
                // only has to put the feet on the ground for the landing frame itself.
                var landing = model.GetWorldBoundsRaw();
                groundY = app.collider.FloorHeightUnder(landing.Center, landing.Extents) + (float)model.Height * 0.5f;
            }

            if (balloon != null && balloon.Aboard)
                balloon.Disembark();

            model.PosY = groundY;
        }

        // The pose follows what happened, not the takeoff guess: a jump refused over water and a leap that missed
        // the deck or the basket both walk the body back to the size their landing actually calls for.
        app.player.SetDeckPose((boat != null && boat.Aboard) || (balloon != null && balloon.Aboard));

        app.player.jumping = false;

        // A body at the oars or in the basket sits still; the walking clips belong to the ground. Everything else
        // restores what the player was doing before takeoff, matching the pre-boat behaviour.
        string resume = onBasket || onDeck ? "Idle-loop" : jumpResumeAnimation;
        if (model.GetCurrentAnimationName() != resume)
            model.PlayAnimation(resume);

        app.FollowCamera(new Vector3(model.PosX, model.PosY, model.PosZ) - beforeLand);
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        // Space mirrors the skill button: an edge press starts one long jump.
        // The edge semantics deduplicate naturally, so holding Space does not re-trigger.
        if (Season.Storage.KeyboardService.IsPressed(Season.Basic.Key.Space))
        {
            StartLongJump();
        }

        UpdateLongJump(time);

        int size = 60;

        skill.Color = skill.MouseOver ? Season.Basic.Colors.DarkRed : Season.Basic.Colors.White;
        if (skill.Update(time, alpha: Alpha > 0 ? 0.6f : Alpha, posX: App.Instance.ExtendResolution.X - size - size * 2, posY: App.Instance.ExtendResolution.Y - size - size * 2, width: size * 2, height: size * 2))
        {
            result = true;
        }

        skillTexts.Color = skill.MouseOver ? Season.Basic.Colors.White : Season.Basic.Colors.Black;
        if (skillTexts.Update(time, alpha: Alpha, posX: skill.PosX + (skill.Width - skillTexts.Width) / 2, posY: skill.PosY + (skill.Height - skillTexts.Height - skillTexts.VisualOffsetTop) / 2))
        {
            result = true;
        }

        return result;
    }
}
