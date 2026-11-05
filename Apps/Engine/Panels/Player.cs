// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Engine.Panels;

internal class Player : Panel
{
    // Long-jump state: starts on click, advances in UpdateLongJump, and resets on landing.
    internal bool jumping;

    /// <summary>
    /// True while the automated plane is lifting the pilot back out of the cockpit. The plane drives the body for
    /// the length of that arc, so the seat bind and the floor pin below must both stand down; it is deliberately
    /// not <see cref="jumping"/>, which would hand the body to Skill's long-jump step.
    /// </summary>
    internal bool planeExit;

    bool modelFirst = true;

    bool modelSettled;

    /// <summary>
    /// The character. It carries a full attitude rather than yaw alone (see <see cref="AttitudeBody"/>) because the
    /// cockpit of a banking aircraft takes the pilot over with it.
    /// </summary>
    internal AttitudeBody model;

    internal Season.Controls.Model torch;

    // -- Torch rig: a carried torch welded to the front of the character, plus the point light it burns with. --
    const string TorchGlbPath = @"Assets/Torch.glb";

    // The flame marker reuses the unlit sphere the street lamp uses for its bulb. Torch.glb has no emissive
    // material, so without a marker the light would look like it comes out of bare wood once the sun is down.
    const string FlameGlbPath = @"Assets/Sun.glb";

    // The character stands 1 m tall, so a 0.7 m torch reads as something carried rather than a floor stand.
    const float TorchHeight = 0.7f;

    // Anchor-to-anchor distance in front of the character. The player box is 0.5 m deep and the scaled shaft is
    // about 0.2 m across, so 0.5 m keeps a clear gap instead of letting the torch intersect the body.
    const float TorchForward = 1f;

    // Bottom edge above the feet plane. The whole rig moves with the character, jump arc included, so a small
    // lift is what keeps it reading as carried instead of planted in the grass.
    const float TorchLift = 0.12f;

    const float FlameSize = 0.16f;

    /// <summary>
    /// Scale applied to the whole character while it stands on the boat. The hull is 3.4 m long and the character
    /// 1 m tall, so at full size it would fill the boat; half size reads as a person at the oars instead. Everything
    /// that hangs off the body - the carried torch and its flame - is scaled by the same factor in
    /// <see cref="PlaceTorch"/> and <see cref="Update"/>, so the rig stays proportional in both states.
    /// </summary>
    const float DeckScale = 0.5f;

    // -- Body box at full scale, in meters. The constructor pins these values and Update rewrites the box every frame
    //    at the pose scale below, so the deck pose is a size the body walks toward, not a second set of numbers. --
    const float BodyWidth = 1f;
    const float BodyHeight = 1f;
    const float BodyDepth = 0.5f;

    /// <summary>
    /// Seconds the body takes to cross the whole full-size to <see cref="DeckScale"/> span, linearly. The jump aims
    /// this at takeoff rather than at the touchdown (see <see cref="Skill"/>), so most of the crossing happens while
    /// the body is still in the air; short enough that a leap meeting the hull early still arrives at deck size.
    /// </summary>
    const float PoseShiftSeconds = 0.6f;

    /// <summary>Size change per second: the slope of that linear crossing.</summary>
    const float PoseRate = (1f - DeckScale) / PoseShiftSeconds;

    /// <summary>Size the body is heading for: 1 walking, <see cref="DeckScale"/> at the oars.</summary>
    float poseTarget = 1f;

    /// <summary>Size in force this frame, walked toward <see cref="poseTarget"/> at <see cref="PoseRate"/>.</summary>
    float poseScale = 1f;

    // Warm flame colour. The torch is a carried light rather than a dusk-only lamp, so it keeps burning and keeps
    // warming the character at midday too, as asked, instead of switching itself off with the sun.
    static readonly Vector3 TorchLightColor = new Vector3(1f, 0.7f, 0.35f);

    // Point-light intensity follows the engine convention radiance = colour * intensity / d^2 with a window cutoff
    // at Range. 5 lights the ground out to roughly 4 m at night, which is what makes the surroundings readable,
    // while staying well below the sun by day; the 12 m range keeps the pool edge from ending on a hard circle.
    const float TorchLightIntensity = 5f;
    const float TorchLightRange = 12f;

    /// <summary>Flame marker at the torch tip: unlit, casts no shadows, never picked.</summary>
    Season.Controls.Model torchFlame;

    /// <summary>Always-on point light at the flame, moved every frame with the torch.</summary>
    Season.Rendering.LightSource torchLight;

    // Unified placement convention: (PosX, PosY, PosZ) is the world position of the
    // bounding-box anchor, namely the geometric center. This sample still thinks in terms
    // of "placing the model by its local origin", so writes are converted through
    // AnchorWorldOffset.
    // The local origin of 3DGodotRobot.glb is approximately at the center of the feet:
    // when the origin is at (0,0,0), the model stands on the origin, with the body in
    // y in [0,1] and the bottom resting on the y=0 plane, which matches grass level.
    /// <summary>
    /// Aims the body at the walking size or the deck pose. The size itself is walked there by <see cref="Update"/>,
    /// so nothing pops: a boarding jump starts shrinking at takeoff and a jump off the deck starts growing there.
    /// Called at takeoff with the landing the jump expects, and again on touchdown with the landing it got.
    /// </summary>
    internal void SetDeckPose(bool onDeck) => poseTarget = onDeck ? DeckScale : 1f;

    internal Player()
    {
        model = new AttitudeBody()
        {
            Name = @"Assets/3DGodotRobot.glb",
            Highlight =
            {
                Style = HighlightStyle.Wireframe
            },
            PosX = 0,
            PosY = 0.5f,
            PosZ = 0.5f,
            Width = BodyWidth,
            Height = BodyHeight,
            Depth = BodyDepth,
            Rotation = (float)Math.PI
        };
        AddControl(model);

        // Size and position are settled from the character pose every frame, so only the asset is fixed here.
        // CastShadows keeps the default true: a carried torch is near-field geometry, not a background prop.
        torch = new Season.Controls.Model()
        {
            Name = TorchGlbPath,
        };
        AddControl(torch);

        torchFlame = new Season.Controls.Model()
        {
            Name = FlameGlbPath,
            Unlit = true,
            MaterialColor = new System.Numerics.Vector4(1.9f, 1.15f, 0.4f, 1f),
            CastShadows = false,
            Width = FlameSize,
            Height = FlameSize,
            Depth = FlameSize,
        };
        AddControl(torchFlame);

        // Registered once and then only ever moved: the light takes part in every frame's Bake as is, so it needs
        // no per-frame rebuild and stays lit day and night.
        // Point lights do not cast shadows in this engine, so the flame may emit from inside the torch head
        // without the mesh self-occluding the pool it throws.
        torchLight = App.Instance.Lighting.Add(new Season.Rendering.LightSource
        {
            Name = "Torch",
            Kind = Season.Rendering.LightKind.Point,
            Color = TorchLightColor,
            Intensity = TorchLightIntensity,
            Range = TorchLightRange,
            CastShadows = false,
            // Bake trims to eight GPU slots by priority, adding 1000 for directional lights and 500 for shadow
            // casters. 40 keeps the torch below every existing caster, so neither sun, moon, nor the lamps can
            // be displaced by it, while it still outranks plain unlit filler lights.
            Priority = 40,
        });

        // Place once at construction so the very first frame already lights the ground under the torch instead of
        // flashing at the world origin. Player.Update keeps it there from then on.
        PlaceTorch();
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        // The pose is walked, not switched, and written to the box every frame: the torch rig, its light and the
        // collision box all read this one scale, so none of them can disagree half way through the change.
        if (poseScale != poseTarget)
        {
            float limit = PoseRate * time;
            poseScale = MathF.Abs(poseTarget - poseScale) <= limit
                ? poseTarget
                : poseScale + MathF.Sign(poseTarget - poseScale) * limit;
        }

        model.Width = BodyWidth * poseScale;
        model.Height = BodyHeight * poseScale;
        model.Depth = BodyDepth * poseScale;

        // The flame marker is a fixed-size sphere, so it follows the body here rather than in SetDeckPose.
        torchFlame.Width = FlameSize * poseScale;
        torchFlame.Height = FlameSize * poseScale;
        torchFlame.Depth = FlameSize * poseScale;

        // Model defaults set explicitly in the constructor take priority.
        // Size values are assigned only when still zero, using normalized 0.2x scaling
        // (world meters = local size * OriginalScale * 0.2).
        // Position is assigned only when all components are zero, pinning the local origin to (0,0,0).
        if (!modelSettled && model.LocalSize != Vector3.Zero)
        {
            if (model.Width == 0) model.Width = model.LocalSize.X * model.OriginalScale * 0.2f;
            if (model.Height == 0) model.Height = model.LocalSize.Y * model.OriginalScale * 0.2f;
            if (model.Depth == 0) model.Depth = model.LocalSize.Z * model.OriginalScale * 0.2f;

            if (model.PosX == 0 && model.PosY == 0 && model.PosZ == 0)
            {
                var pos = model.AnchorWorldOffset;
                model.PosX = pos.X;
                model.PosY = pos.Y;
                model.PosZ = pos.Z;
            }
            modelSettled = true;
        }

        // Torch proportions come from the asset rather than from constants: Torch.glb is a thin shaft of unit
        // height, and a uniform scale derived from its bounds keeps that shape instead of squaring it off.
        // The deck pose halves the body, so the shaft is resized with it on every frame of the pose change.
        if (torch.LocalSize.Y > 1e-6f)
        {
            float shaftHeight = TorchHeight * poseScale;
            float scale = shaftHeight / torch.LocalSize.Y;

            torch.Width = torch.LocalSize.X * scale;
            torch.Height = shaftHeight;
            torch.Depth = torch.LocalSize.Z * scale;
        }

        // While the player is on the boat the deck owns the pose: the seat position, the deck height and the boat's
        // heading are copied in before the rig is placed, so the torch reads the deck on the same frame. The long jump
        // is exempt because it is steering the body itself, and the landing test decides whether the player stayed
        // aboard or stepped off. The balloon's basket works the same way, one branch over, except that its seat only
        // hands over the position: the heading stays with the camera (App.SyncPlayerYawToCamera), so the body turns
        // freely inside the basket while the balloon keeps its own course. The aircraft's cockpit is the third seat:
        // the plane owns the whole ride, so it hands over both the position and the heading.
        var boat = App.Instance.boat;
        var balloon = App.Instance.hotAirBalloon;
        var plane = App.Instance.aeroPlane;

        // Upright by default: the two extra axes belong to the cockpit alone, so a body that steps back out of a
        // banking aircraft, or hangs under a canopy, is not left leaning. Every branch below that wants a tilt
        // writes it again after this.
        model.Pitch = 0f;
        model.Roll = 0f;

        if (plane != null && plane.Aboard && !jumping && !planeExit)
        {
            // In the cockpit: the seat is on the fuselage centre line and the body faces the way the nose points,
            // so the pilot rides the climb with the airframe instead of standing level in a pitching aircraft. The
            // pitch and the bank come across as well: the pilot is strapped into a seat that tilts with the wings,
            // and a body left upright while the cockpit rolls forty degrees is what reads as standing in the wrong
            // place in the cabin.
            //
            // Both angles are negated, because the pilot's yaw is half a turn off the airframe's (PilotYaw): the
            // character's own +X and +Z both end up pointing opposite the aircraft's, so an angle copied straight
            // across turns the body the other way in the world - the seat would lean out of the wing it is tucked
            // into, and the pilot would pitch nose-down as the jet climbs.
            //
            // The half-height is measured along the airframe's up (SeatUp), not the world's: the body turns about its
            // own centre, so lifting the centre straight up would leave the feet hanging off the floor sideways by
            // half the body times the sine of the bank. Along SeatUp the feet sit exactly on the seat at any attitude.
            var seat = plane.SeatPosition;
            var up = plane.SeatUp;
            float half = (model.Height ?? 0f) * 0.5f;

            model.PosX = seat.X + up.X * half;
            model.PosY = seat.Y + up.Y * half;
            model.PosZ = seat.Z + up.Z * half;
            model.Rotation = plane.PilotYaw;
            model.Pitch = -plane.PilotPitch;
            model.Roll = -plane.PilotRoll;
        }
        else if (balloon != null && balloon.Aboard && !jumping)
        {
            var seat = balloon.SeatPosition;
            model.PosX = seat.X;
            model.PosZ = seat.Z;
            model.PosY = seat.Y + (model.Height ?? 0f) * 0.5f;
        }
        else if (boat != null && boat.Aboard && !jumping)
        {
            var seat = boat.SeatPosition;
            model.PosX = seat.X;
            model.PosZ = seat.Z;
            model.PosY = seat.Y + (model.Height ?? 0f) * 0.5f;
            model.Rotation = boat.Heading;
        }
        else if (!jumping && !planeExit && poseScale != poseTarget)
        {
            // The feet are the fixed end of a body still changing size, so the centre has to be re-read from the
            // floor and the height it has right now. Without this a body growing back to full size sinks half of
            // what it just gained into the grass until the next step settles it again.
            var planted = model.GetWorldBoundsRaw();
            model.PosY = App.Instance.collider.FloorHeightUnder(planted.Center, planted.Extents) + (model.Height ?? 0f) * 0.5f;
        }

        // While the eye camera is up, the rig has to step out of its own view: alpha zero skips the draw and
        // cast-shadows off keeps the invisible body from leaving a shadow in the wicker. The torch and its flame
        // go with it because they hang right in front of the face; only the light stays lit, since it is what
        // warms the basket around the player. Hiding starts as the camera passes the halfway mark toward the eyes
        // and ends once it is back past it, so the camera never travels through a body that is still drawn.
        bool eyeView = App.Instance.BalloonEyeBlend > 0.5f;
        float rigAlpha = eyeView ? 0f : 1f;
        model.Alpha = rigAlpha;
        model.CastShadows = !eyeView;
        torch.Alpha = rigAlpha;
        torch.CastShadows = !eyeView;
        torchFlame.Alpha = rigAlpha;

        // Direction and Skill have already moved and turned the character by the time App.Update reaches this
        // panel, so the rig follows this frame's final pose rather than a frame-old one.
        PlaceTorch();

        // Ready is set by the load queue before the shared-model continuation injects
        // Asset, and PlayAnimation silently no-ops while Asset is null. Retry every frame
        // until the switch actually lands, and only then consume the first-time flag.
        if (model.Ready && modelFirst)
        {
            if (model.PlayAnimation("Idle-loop") != null)
                modelFirst = false;
        }

        if (model.Update(time))
        {
            result = true;
        }

        // Panel.Update does not recurse into children, so the torch rig is driven explicitly here.
        if (torch.Update(time))
        {
            result = true;
        }

        if (torchFlame.Update(time))
        {
            result = true;
        }

        return result;
    }

    /// <summary>
    /// Welds the torch rig to the front of the character: the anchor is offset along the facing direction on the XZ
    /// plane, and every height is measured from the feet plane so steps and the long-jump arc carry the torch along
    /// instead of leaving it behind.
    ///
    /// The flame marker and the light share one point, so the illumination always comes out of the visible flame.
    /// SceneLighting.Bake has already run for this frame by the time this panel updates, so a new light position lands
    /// on the next frame's bake: a sub-frame offset, far below one frame of motion even at jump speed.
    /// </summary>
    void PlaceTorch()
    {
        float yaw = model.Rotation;

        // The deck pose halves the body and the rig with it; everything below is authored for the 1 m character.
        float k = poseScale;
        float forward = TorchForward * k;
        float shaftHeight = TorchHeight * k;
        float lift = TorchLift * k;
        float flameSize = FlameSize * k;

        // Character facing convention shared with Direction and Skill: forward = (-sin yaw, 0, -cos yaw).
        float forwardX = -MathF.Sin(yaw);
        float forwardZ = -MathF.Cos(yaw);

        // The anchor is the bounding-box center, so the feet plane sits half a height below PosY.
        float feetY = model.PosY - (model.Height ?? 0f) * 0.5f;

        torch.PosX = model.PosX + forwardX * forward;
        torch.PosZ = model.PosZ + forwardZ * forward;
        torch.PosY = feetY + lift + shaftHeight * 0.5f;   // Anchor is the torch box center.
        torch.Rotation = yaw;   // Same yaw as the character, so the shaft turns with it instead of staying put.

        // Flame sits just above the tip of the shaft; the light emits from that same point.
        float flameY = feetY + lift + shaftHeight + flameSize * 0.15f;

        torchFlame.PosX = torch.PosX;
        torchFlame.PosY = flameY;
        torchFlame.PosZ = torch.PosZ;

        // Point lights are the ones that read Position; Direction is ignored by the shader for this kind.
        torchLight.Position = new Vector3(torch.PosX, flameY, torch.PosZ);

        // The deck pose carries the flame half as high off the boards, so the same intensity lights the hull around
        // it about twice as brightly. Tying the intensity to the body scale keeps the pool at the strength the body
        // calls for at every distance, and it moves with the pose instead of stepping at the ends of it; the range
        // stays fixed, because shortening it would cut the lit circle off instead of dimming it.
        torchLight.Intensity = TorchLightIntensity * k;
    }
}

/// <summary>
/// A model with a full three-axis attitude instead of the single yaw <see cref="Season.Controls.Model"/> turns about.
/// Two things in this sample need one: the airframe (<see cref="PlaneBody"/>), whose nose rises on the climb and whose
/// wings bank into the turns, and the pilot, who is strapped into that airframe and therefore has to tilt with it.
///
/// <para>The matrix is built from the body axes rather than from three chained <c>CreateRotation*</c> calls: yaw sets
/// the nose and the starboard axis, pitch swings both about the starboard axis, and bank swings the wing about the
/// nose. Writing the result as rows means <c>p * R</c> maps a local point onto the world, which is the layout
/// <c>BuildWorldMatrix</c> expects, so the drawn pose, the anchor offset, the picking box and the shadow box all read
/// the same attitude.</para>
/// </summary>
internal class AttitudeBody : Season.Controls.Model
{
    /// <summary>Nose-up angle about the starboard axis, radians. Positive raises the nose.</summary>
    internal float Pitch;

    /// <summary>Bank angle about the nose, radians. Positive lifts the starboard side.</summary>
    internal float Roll;

    protected override Matrix4x4 GetRotationMatrix()
    {
        float cy = MathF.Cos(Rotation), sy = MathF.Sin(Rotation);

        // Engine space: the asset's nose is its local +Z (the loader mirrors the glTF -Z), +Y is up, +X starboard.
        var right = new Vector3(cy, 0f, -sy);
        var up = Vector3.UnitY;
        var noseFlat = new Vector3(sy, 0f, cy);

        float cp = MathF.Cos(Pitch), sp = MathF.Sin(Pitch);
        var nose = noseFlat * cp + up * sp;
        var pitchedUp = up * cp - noseFlat * sp;

        float cr = MathF.Cos(Roll), sr = MathF.Sin(Roll);
        var bankedRight = right * cr + pitchedUp * sr;
        var bankedUp = pitchedUp * cr - right * sr;

        return new Matrix4x4(
            bankedRight.X, bankedRight.Y, bankedRight.Z, 0f,
            bankedUp.X, bankedUp.Y, bankedUp.Z, 0f,
            nose.X, nose.Y, nose.Z, 0f,
            0f, 0f, 0f, 1f);
    }
}
