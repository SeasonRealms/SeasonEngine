// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Engine.Panels;

/// <summary>
/// One aircraft of the <see cref="Planes"/> formation. A Plane is the actual instanced object stored in
/// planesModel.Instances, so PosX/PosY/PosZ, Width/Height/Depth, Rotation and the animation fields follow the same
/// placement contract as every other MeshInstanceTransform. Yaw/Pitch/Roll are the orbit attitude that Planes resolves
/// into Rotation every frame, exactly the way Seagull does for the bird flock.
/// </summary>
internal class Plane : MeshInstanceTransform
{
    /// <summary>
    /// Yaw around Y in radians, taken from the orbit tangent. The asset's nose sits at glTF -Z, which the loader mirrors
    /// into the engine's left-handed world as +Z, so a yaw of zero points the aircraft along +Z.
    /// </summary>
    internal float Yaw { get; set; }

    /// <summary>Pitch in radians. The formation holds a constant altitude, so this stays at zero.</summary>
    internal float Pitch { get; set; }

    /// <summary>Roll in radians. Positive lifts the starboard wing, banking left toward the orbit center.</summary>
    internal float Roll { get; set; }

    /// <summary>Distance this aircraft trails the lead column along the orbit, in meters.</summary>
    internal float Behind { get; set; }

    /// <summary>Offset from the formation center line in meters, positive toward the lead aircraft's starboard side.</summary>
    internal float Lateral { get; set; }
}

/// <summary>
/// Aircraft formation panel: ten planes in the 1 / 2 / 3 / 4 column wedge, orbiting the island over the open sea at
/// the observation tower's deck height. The whole panel is instanced through one InstancedModel, and the only clip in
/// the asset is the propeller spin, so the propellers keep turning while the formation itself is placed in code.
///
/// <para><b>Formation.</b> Four columns, each one aircraft wider than the column in front of it: the lead column holds
/// a single aircraft on the center line, and the three columns behind it hold two, three and four. Every column is
/// centered on the center line, which is what the odd columns' single center aircraft and the even columns' symmetric
/// pair produce. Columns are spaced along the direction of travel and neighbours inside a column are spaced laterally;
/// both gaps exceed the 8m wingspan so the wedge flies as ten separate silhouettes rather than one overlapped blob.</para>
///
/// <para><b>Orbit.</b> The formation turns as one rigid body about the island center. Each aircraft is placed by its
/// own orbit angle and radius: trailing distance becomes an angle offset, lateral offset becomes a radius offset, and
/// the bank angle follows the centripetal acceleration of that radius. The flight direction is the tangent of increasing
/// orbit angle, and the island center therefore sits on the aircraft's port side, which is why the roll is positive --
/// the same sign convention Birds uses for a left-hand turn. Note that the asset's nose is at glTF -Z, but the loader
/// negates Position.Z to reach the engine's left-handed world, so in engine space a yaw of zero points along +Z. The
/// altitude is the tower's walkable deck top (Tower.DeckTop, tower.PosY is 0), copied here the same way HotAirBalloon
/// copies it.</para>
///
/// <para><b>Animation.</b> plane.glb was rebuilt from the original orbiting export: the flight joint sits at rest and
/// the single remaining clip is the propeller rotation, so the instance transform is the only thing that moves the
/// airframe. The clip is assigned by index (the exporter's take name is not a stable contract) and the player loops it.
/// Per-instance time offsets desynchronize the ten propellers, so the wedge does not read as one mechanical unit.</para>
/// </summary>
internal class Planes : Panel
{
    // Asset: the origin-centered airframe whose only clip is the propeller spin. Path is relative to Raw/.
    internal const string PlaneGlbPath = @"Assets/plane.glb";

    // Formation: four columns of 1, 2, 3 and 4 aircraft. Both spacings clear the 8m wingspan twice over, and the column
    // spacing also clears the ~6m fuselage length, so no two aircraft overlap along either axis of the wedge.
    const int Columns = 4;
    const float ColumnSpacing = 20f;     // meters between columns, along the orbit
    const float LateralSpacing = 22f;    // meters between neighbours inside one column

    // Orbit: the sea reaches +/-700m, the land square is +/-75m and the outermost mountain ridge sits around 110m.
    // Doubling the formation spacing widens the wedge to +/-33m laterally, so the radius grew with it: the innermost
    // aircraft still orbits at 113m, outside the ridge line, and the whole wedge stays over open water. The bank angle
    // comes out between roughly 17 and 26 degrees across the wedge.
    internal const float OrbitRadius = 146f;
    internal const float OrbitSpeed = 24f;        // ground speed at OrbitRadius, ~86 km/h
    const float Gravity = 9.81f;
    const float OrbitStartAngle = MathF.PI * 0.5f;   // start north of the island, inside the initial camera's view

    // Flight level: the tower deck top (matches Tower.DeckTop; the tower sits at PosY = 0). The anchor is the
    // bounding-box center, so the fuselage centerline rides exactly at the platform height and the whole formation
    // sits level with a viewer standing on the deck.
    internal const float FlightAltitude = 20f;

    // Model size: 8m wingspan. Height and depth follow the asset's own 1.16944 x 0.41724 x 0.88113 local bounds, so
    // the airframe keeps its proportions instead of being stretched to a cube.
    const float ModelWidth = 8f;
    const float ModelHeight = ModelWidth * 0.41724f / 1.16944f;   // ~2.855m
    const float ModelDepth = ModelWidth * 0.88113f / 1.16944f;    // ~6.028m

    internal List<Plane> Aircraft = new List<Plane>();

    // Exposed as internal so App.Create can register it into the picker's instanced target list, like Birds does.
    internal InstancedModel planesModel;

    // The lead aircraft's angle on the orbit, advanced by the frame delta in UpdateOrbit and wrapped to one turn.
    float orbitAngle = OrbitStartAngle;

    internal Planes()
    {
        planesModel = new InstancedModel()
        {
            ModelName = PlaneGlbPath,
            // A closed orbit around the island: by construction the half behind the camera is never visible.
            DrawPath = InstanceDrawPath.Indirect
        };
        AddControl(planesModel);

        for (var column = 0; column < Columns; column++)
        {
            var count = column + 1;

            for (var k = 0; k < count; k++)
            {
                var plane = new Plane()
                {
                    Width = ModelWidth,
                    Height = ModelHeight,
                    Depth = ModelDepth,

                    // Trailing columns step back along the orbit, while each column is centered on the center line:
                    // (k - (n - 1) / 2) * spacing is -5.5/+5.5 for the two-aircraft column, -11/0/+11 for three, and
                    // so on. The single lead aircraft lands at zero on both axes, which is the "centered" first column.
                    Behind = column * ColumnSpacing,
                    Lateral = (k - (count - 1) * 0.5f) * LateralSpacing,

                    // Single propeller-spin clip, looped by the player. Offsets phase the ten propellers apart; the
                    // spin rate stays at the authored 1x.
                    AnimationClip = 0,
                    AnimationSpeed = 1f,
                    AnimationTimeOffset = Aircraft.Count * 0.4f,
                };

                Aircraft.Add(plane);
            }
        }

        SyncInstances();
        UpdateOrbit(0f);   // Place the wedge before the first frame, so it never shows up stacked at the origin.
    }

    /// <summary>
    /// Advances the formation around the island and rewrites every aircraft's polar placement and attitude.
    /// <paramref name="time"/> is the frame delta, the same convention Birds uses inside its flight step; it is clamped
    /// so a long stall cannot jump the wedge across a chunk of the orbit in one step.
    /// </summary>
    void UpdateOrbit(float time)
    {
        var dt = MathF.Min(time, 0.05f);

        // Every aircraft shares the angular velocity because the formation turns as one rigid body. Its own radius is
        // what sets its ground speed and bank angle.
        var omega = OrbitSpeed / OrbitRadius;
        orbitAngle += omega * dt;

        if (orbitAngle >= MathF.Tau)
            orbitAngle -= MathF.Tau;
        else if (orbitAngle < 0f)
            orbitAngle += MathF.Tau;

        var bankGain = omega * omega / Gravity;

        for (var i = 0; i < Aircraft.Count; i++)
        {
            var plane = Aircraft[i];

            // A selected aircraft belongs to the property panel, exactly like a selected seagull: leaving it alone
            // while Selected keeps the edits, and the orbit resumes once the selection clears.
            if (plane.Selected)
                continue;

            // Trailing aircraft sit at a smaller orbit angle. The center is on the port side of the left-hand orbit, so
            // the starboard direction points away from it: a positive (starboard) offset flies a wider radius.
            var theta = orbitAngle - plane.Behind / OrbitRadius;
            var radius = OrbitRadius + plane.Lateral;

            var cos = MathF.Cos(theta);
            var sin = MathF.Sin(theta);

            plane.PosX = cos * radius;
            plane.PosY = FlightAltitude;
            plane.PosZ = sin * radius;

            // Forward is the orbit tangent in the direction of increasing angle, i.e. (-sin, 0, cos). The model's nose
            // is +Z in engine space (its glTF -Z was mirrored by the loader), so yaw is atan2(forward.X, forward.Z).
            plane.Yaw = MathF.Atan2(-sin, cos);
            plane.Pitch = 0f;
            plane.Roll = MathF.Atan(bankGain * radius);

            plane.Rotation = Quaternion.CreateFromYawPitchRoll(plane.Yaw, plane.Pitch, plane.Roll);
        }
    }

    /// <summary>Keeps planesModel.Instances in step with Aircraft, preserving order like Birds.SyncInstances.</summary>
    void SyncInstances()
    {
        var instances = planesModel.Instances;

        while (instances.Count < Aircraft.Count)
            instances.Add(Aircraft[instances.Count]);

        while (instances.Count > Aircraft.Count)
            instances.RemoveAt(instances.Count - 1);

        for (var i = 0; i < Aircraft.Count; i++)
        {
            if (!ReferenceEquals(instances[i], Aircraft[i]))
                instances[i] = Aircraft[i];
        }
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        SyncInstances();
        UpdateOrbit(time);
        planesModel.Update(time);

        return false;
    }
}
