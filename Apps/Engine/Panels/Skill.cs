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

    internal Skill()
    {
        RenderDomain = Season.Controls.RenderDomain.Overlay;

        skill = new Shape()
        {
            Type = ShapeType.Circle,
            // Long-jump skill: starts only when the current animation is not already LongJump.
            OnClick = StartLongJump
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
        jumpForward = YawForward(model.Rotation);
        jumpStartTime = App.Instance.Time;

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

        App.Instance.player.jumping = true;

        model.PlayAnimation("LongJump");
    }

    // Advances the long jump every frame from Update.
    // The arc is based on the current floor height, whether that comes from steps, indoor floors, or grass:
    // PosY = floor + Height/2 + JumpHeight*sin(pi*t), which lets the player clear steps without clipping
    // and land naturally back on the floor at t=1.
    // Horizontal motion follows jumpForward at JumpSpeed, while the camera follows through App.FollowCamera.
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

        // Floor height at the landing point, based only on X/Z and independent of current PosY.
        // Shared by both the landing position and the arc baseline.
        var bounds = model.GetWorldBoundsRaw();
        float groundY = App.Instance.collider.FloorHeightUnder(bounds.Center, bounds.Extents) + (float)model.Height * 0.5f;

        if (t >= 1f)
        {
            // Landing: PosY settles at the floor height of the landing point instead of returning
            // to takeoff height. The camera follows the landing Y displacement as well.
            var beforeLand = new Vector3(model.PosX, model.PosY, model.PosZ);

            model.PosY = groundY;
            App.Instance.player.jumping = false;

            if (model.GetCurrentAnimationName() != jumpResumeAnimation)
                model.PlayAnimation(jumpResumeAnimation);

            App.Instance.FollowCamera(new Vector3(model.PosX, model.PosY, model.PosZ) - beforeLand);
            return;
        }

        // Record the starting point of this frame's displacement, including the arc Y, so the camera
        // can follow the actual movement at the end of the frame.
        var before = new Vector3(model.PosX, model.PosY, model.PosZ);

        // Solve Y first. The arc is based on the current floor height so the jump does not clip into steps,
        // and the airborne height participates in TryMove's Y-overlap test so low obstacles are cleared naturally.
        model.PosY = groundY + JumpHeight * MathF.Sin(MathF.PI * Math.Clamp(t, 0f, 1f));

        // Horizontal displacement still goes through collision resolution: when blocked,
        // it clamps to the contact face and may slide or stop, but never tunnels into obstacle bounds.
        var delta = App.Instance.collider.TryMove(
            model.GetWorldBoundsRaw(),
            jumpForward.X * JumpSpeed * time,
            jumpForward.Z * JumpSpeed * time);

        model.PosX += delta.X;
        model.PosZ += delta.Y;

        App.Instance.FollowCamera(new Vector3(model.PosX, model.PosY, model.PosZ) - before);
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

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
