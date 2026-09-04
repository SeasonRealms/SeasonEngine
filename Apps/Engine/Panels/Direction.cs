// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Engine.Panels;

internal class Direction : Panel
{
    public override bool MouseOver
    {
        get
        {
            return left.MouseOver || front.MouseOver || right.MouseOver || back.MouseOver;
        }
    }

    Sprite2D left, front, right, back;

    // Step length in meters for each click when translating along the horizontal view direction.
    // Direction comes from the XZ projection of CameraTarget - CameraPos:
    // camera and target move together so the view vector stays unchanged, and Y is always zero,
    // which keeps camera height fixed relative to the grass.
    // Motion is clamped to the grassland range, X +/-120 and Z in [-120,230], matching
    // Ground.cs halfExtentX, zMin, and zMax, so the camera does not leave the land area.
    // Since 2026-08, areas beyond the grass are ocean and a camera above the sea would still be acceptable,
    // but the clamp preserves the established framing baseline.
    const float Step = 5f;

    // Character facing direction via Model.Rotation in radians around Y.
    // The glTF asset faces back by default, which matches the initial Rotation=pi convention.
    // World directions are front=+Z, back=-Z, left=-X, and right=+X.
    const float YawFront = MathF.PI;
    const float YawLeft = MathF.PI / 2f;
    const float YawRight = -MathF.PI / 2f;
    const float YawBack = 0f;

    // Movement and stop animation switching:
    // clicking a direction key refreshes the last-move time and switches to Run-loop;
    // if no new input arrives after StopDelay, the player is considered stopped and switches back to Idle-loop.
    // Current animation names are checked to avoid redundant switches.
    const float StopDelay = 0.3f;

    float lastMoveTime = float.MinValue;

    const float RunSpeed = 1f;          // Baseline running speed in meters per second.
                                        // It corresponds to the nominal speed implied by 0.1m directional taps
                                        // and is used as the reference for the jump's 2x speed.

    internal Direction()
    {
        RenderDomain = Season.Controls.RenderDomain.Overlay;

        left = new Sprite2D()
        {
            Name = "Assets/Arrow.png",
            Clock = 90,
            OnTouch = async () => MovePlayer(-0.1f, 0f, YawLeft)
        };
        AddControl(left);

        front = new Sprite2D()
        {
            Name = "Assets/Arrow.png",
            Clock = 180,
            OnTouch = async () => MovePlayer(0f, 0.1f, YawFront)
        };
        AddControl(front);

        right = new Sprite2D()
        {
            Name = "Assets/Arrow.png",
            Clock = 270,
            OnTouch = async () => MovePlayer(0.1f, 0f, YawRight)
        };
        AddControl(right);

        back = new Sprite2D()
        {
            Name = "Assets/Arrow.png",
            Clock = 0,
            OnTouch = async () => MovePlayer(0f, -0.1f, YawBack)
        };
        AddControl(back);
    }

    // Moves horizontally straight forward or backward relative to the camera view.
    // Only X and Z change; Y remains fixed.
    void MoveCamera(bool forward)
    {
        var app = App.Instance;

        var dir = app.CameraTarget - app.CameraPos;
        dir.Y = 0f; // Horizontal projection: movement uses only the ground-plane component of the view vector.
        if (dir.LengthSquared() < 1e-8f)
            return; // View direction is almost vertical, so there is no usable horizontal direction.

        var unit = Vector3.Normalize(dir) * Step * (forward ? 1f : -1f);

        var pos = app.CameraPos + unit;
        pos.X = Math.Clamp(pos.X, -120f, 120f);
        pos.Z = Math.Clamp(pos.Z, -120f, 230f);

        // Actual displacement may be smaller than Step after clamping.
        // CameraTarget moves by the same amount so the viewing direction remains unchanged.
        var actual = pos - app.CameraPos;
        if (actual.LengthSquared() < 1e-8f)
            return;

        app.CameraPos = pos;
        app.CameraTarget += actual;
    }

    // Direction-key input applies facing direction, collision-resolved step movement, camera follow,
    // and refreshes the last-move time, switching to Run-loop only when actual movement occurs.
    // The meaning of dx and dz depends on movement mode:
    //   World mode: dx and dz are direct world-space X and Z displacements of +/-0.1, with fixed four-way yaw.
    //   Character mode: dx and dz contribute only their signs as input identity.
    //   Forward, backward, and strafing directions are derived from the XZ projection of the camera view,
    //   with the same 0.1m step length as World mode, and App.FollowCamera keeps the over-the-shoulder framing by following real displacement.
    void MovePlayer(float dx, float dz, float yaw)
    {
        var app = App.Instance;

        // Locked during a long jump: facing is already frozen to the takeoff direction and movement is handled by UpdateLongJump.
        if (app.player.jumping)
            return;

        var model = app.player.model;

        if (app.Movement == Movement.Character)
        {
            var dir = app.CameraTarget - app.CameraPos;
            var horiz = new Vector2(dir.X, dir.Z);

            // Convert camera horizontal facing to character yaw, which is the inverse of Skill.YawForward:
            // forward=(-sin yaw,0,-cos yaw) implies yaw=Atan2(-fx,-fz).
            // If the view is vertically top-down and there is no horizontal direction, fall back to the current character facing.
            float yawCam;
            if (horiz.LengthSquared() < 1e-8f)
            {
                yawCam = model.Rotation;
                horiz = new Vector2(-MathF.Sin(yawCam), -MathF.Cos(yawCam));
            }
            else
            {
                horiz = Vector2.Normalize(horiz);
                yawCam = MathF.Atan2(-horiz.X, -horiz.Y);
            }

            // World left is camera forward rotated by -90 degrees around +Y in the left-handed system: (x,z)->(-z,x).
            // Right is the negation of left.
            // Input signs mean dz>0 follows forward, dz<0 goes backward, dx<0 moves left, and dx>0 moves right.
            var left = new Vector2(-horiz.Y, horiz.X);
            var move = horiz * MathF.Sign(dz) + left * -MathF.Sign(dx);

            dx = move.X * 0.1f;
            dz = move.Y * 0.1f;
            yaw = yawCam;
        }

        // Turn first, then move. Facing always takes effect, even when blocked,
        // so the player can rotate in place. Collision uses the AABB under the new facing direction.
        model.Rotation = yaw;

        // Collision resolution returns the actual displacement that can be applied.
        // It clamps to the obstacle contact face, stopping before overlap. A zero vector means fully blocked.
        var delta = app.collider.TryMove(model.GetWorldBoundsRaw(), dx, dz);

        // Fully blocked: do not write position, do not move the camera, and do not refresh lastMoveTime.
        // CheckPlayerStopped will switch back to Idle-loop on timeout.
        if (delta == Vector2.Zero)
            return;

        var before = new Vector3(model.PosX, model.PosY, model.PosZ);

        model.PosX += delta.X;
        model.PosZ += delta.Y;

        // Step lifting and indoor floor offset:
        // compute the floor height under the new footprint, whether that is grass, a step top, or the indoor floor.
        // PosY keeps its bounding-box-center meaning, so the feet stay at PosY - Height/2.
        // FollowCamera then tracks the new position in the appropriate mode.
        var settled = model.GetWorldBoundsRaw();
        model.PosY = app.collider.FloorHeightUnder(settled.Center, settled.Extents) + (float)model.Height * 0.5f;

        app.FollowCamera(new Vector3(model.PosX, model.PosY, model.PosZ) - before);

        lastMoveTime = app.Time;

        if (model.GetCurrentAnimationName() != "Run-loop")
            model.PlayAnimation("Run-loop");
    }

    // Stop detection: when time since the most recent direction-key input exceeds StopDelay,
    // the character is considered stopped and switches to Idle-loop if needed.
    // This runs every frame from Update, and resets its sentinel after switching so the next direction-key input starts timing again.
    void CheckPlayerStopped()
    {
        // Do not interrupt a long jump, or StopDelay would switch LongJump to Idle-loop midair.
        if (App.Instance.player.jumping)
            return;

        if (lastMoveTime == float.MinValue)
            return;

        var model = App.Instance.player?.model;
        if (model == null || App.Instance.Time - lastMoveTime < StopDelay)
            return;

        lastMoveTime = float.MinValue;

        if (model.GetCurrentAnimationName() != "Idle-loop")
            model.PlayAnimation("Idle-loop");
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        CheckPlayerStopped();

        int size = 60;

        var pos = new Vector2(size, App.Instance.ExtendResolution.Y - size * 4);

        left.Color = left.MouseOver ? Season.Basic.Colors.DarkRed : Season.Basic.Colors.White;
        if (left.Update(time, alpha: Alpha > 0 ? 0.7f : Alpha, posX: pos.X, posY: pos.Y + size, width: size, height: size))
        {
            result = true;
        }

        front.Color = front.MouseOver ? Season.Basic.Colors.DarkRed : Season.Basic.Colors.White;
        if (front.Update(time, alpha: Alpha > 0 ? 0.7f : Alpha, posX: pos.X + size, posY: pos.Y, width: size, height: size))
        {
            result = true;
        }

        right.Color = right.MouseOver ? Season.Basic.Colors.DarkRed : Season.Basic.Colors.White;
        if (right.Update(time, alpha: Alpha > 0 ? 0.7f : Alpha, posX: pos.X + size * 2, posY: pos.Y + size, width: size, height: size))
        {
            result = true;
        }

        back.Color = back.MouseOver ? Season.Basic.Colors.DarkRed : Season.Basic.Colors.White;
        if (back.Update(time, alpha: Alpha > 0 ? 0.7f : Alpha, posX: pos.X + size, posY: pos.Y + size * 2, width: size, height: size))
        {
            result = true;
        }

        return result;
    }
}
