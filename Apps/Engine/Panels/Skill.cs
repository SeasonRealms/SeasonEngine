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

    // -- Boat deck. The hull's plan rectangle is the only deck there is, and its rim is gunwale rather than floor, so
    //    the boarding test shrinks the rectangle by this much before a touchdown counts as a landing on deck. --
    const float DeckLandingMargin = 0.1f;

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
        var boat = App.Instance.boat;
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
        // popping when it lands. A leap off the deck grows back, and only a leap whose flight reaches the hull heads
        // for the deck pose; Land re-aims this at whatever the touchdown turned out to be, so a wrong guess costs a
        // short walk back to the size the body actually needs.
        App.Instance.player.SetDeckPose(boat != null && !boat.Aboard && FlightReachesDeck(boat));

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

        // Floor height at the landing point, based only on X/Z and independent of current PosY.
        // Shared by both the landing position and the arc baseline.
        var bounds = model.GetWorldBoundsRaw();
        float groundY = App.Instance.collider.FloorHeightUnder(bounds.Center, bounds.Extents) + (float)model.Height * 0.5f;

        // The deck is a floor PlayerCollider knows nothing about, so the arc adds it by hand: a takeoff from the boat
        // starts at deck height instead of the water's zero, and a jump aimed at the boat rides over the deck instead
        // of dipping to the water surface underneath it.
        if (boat != null && (boat.Aboard || boat.ContainsDeck(model.PosX, model.PosZ, DeckLandingMargin)))
            groundY = MathF.Max(groundY, boat.DeckY + (float)model.Height * 0.5f);

        if (t >= 1f)
        {
            Land(boat, model, groundY, bounds);
            return;
        }

        // Contact boards. The flight is longer than the hull, so waiting for the feet to come down on the deck made
        // a leap that clears the stern land in the water and get refused; touching the deck anywhere overhead takes
        // the helm instead. Gated on not already being aboard, or a jump off the deck would pull the player straight
        // back on at takeoff.
        if (boat != null && !boat.Aboard && boat.ContainsDeck(model.PosX, model.PosZ, DeckLandingMargin))
        {
            Land(boat, model, groundY, bounds);
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
    /// Resolves where the jump came down. Three outcomes, in this order: the deck takes the player aboard and walks
    /// the body toward the deck pose, open water refuses the landing and restores the takeoff pose, and dry ground
    /// ends the trip with the boat left exactly where it floats. The deck case is also what a mid-flight touch
    /// arrives through, so the arc can end the moment the body meets the hull.
    /// </summary>
    void Land(Boat? boat, Season.Controls.Model model, float groundY, in Season.Rendering.Bounds3D bounds)
    {
        var app = App.Instance;
        var beforeLand = new Vector3(model.PosX, model.PosY, model.PosZ);

        bool onDeck = boat != null && boat.ContainsDeck(model.PosX, model.PosZ, DeckLandingMargin);
        bool wet = app.collider.InWater(bounds)
            || (boat != null && boat.Waterway.Contains(model.PosX, model.PosZ));

        if (onDeck)
        {
            // On the deck: take the helm and sit at the oars. The pose is aimed at the end of this method, so a
            // touchdown and a mid-flight touch follow the same path.
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
            // Dry ground: the player gets off and the boat stays where it floats.
            if (boat != null && boat.Aboard)
            {
                boat.Disembark();

                // The body is still at deck size at this instant and grows back over the next few frames, with the
                // feet as the fixed end (Player.Update re-reads the floor on every frame of that ramp). This read
                // only has to put the feet on the ground for the landing frame itself.
                var landing = model.GetWorldBoundsRaw();
                groundY = app.collider.FloorHeightUnder(landing.Center, landing.Extents) + (float)model.Height * 0.5f;
            }

            model.PosY = groundY;
        }

        // The pose follows what happened, not the takeoff guess: a jump refused over water and a leap that missed
        // the deck both walk the body back to the size their landing actually calls for.
        app.player.SetDeckPose(boat != null && boat.Aboard);

        app.player.jumping = false;

        // A body at the oars sits still; the walking clips belong to the ground. Everything else restores what the
        // player was doing before takeoff, matching the pre-boat behaviour.
        string resume = onDeck ? "Idle-loop" : jumpResumeAnimation;
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
