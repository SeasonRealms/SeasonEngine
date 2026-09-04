// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Engine.Panels;

/// <summary>Seagull flight behavior: straight waypoint cruising and circular orbiting around a random center.</summary>
internal enum BirdBehavior
{
    Straight,
    Circle
}

/// <summary>
/// Per-bird runtime state. Each Seagull is the actual instanced object stored in seagullsModel.Instances.
/// Position, size, and animation use the normal placement convention, while flight state is updated every frame by Birds.UpdateFlight.
/// </summary>
internal class Seagull : MeshInstanceTransform
{
    /// <summary>Yaw angle around Y in radians, combined with Pitch and Roll into the final Rotation.</summary>
    internal float Yaw { get; set; }

    /// <summary>Pitch in radians. Positive values tilt upward while climbing.</summary>
    internal float Pitch { get; set; }

    /// <summary>Roll in radians. Positive values bank left as horizontal centripetal acceleration increases.</summary>
    internal float Roll { get; set; }

    /// <summary>Animation clip name. Null, empty, or unmatched values fall back to the default clip.</summary>
    internal string? Animation { get; set; }

    /// <summary>World-space flight velocity in meters per second.</summary>
    internal Vector3 Velocity { get; set; }

    /// <summary>Per-bird cruise speed, randomized on spawn and constant afterward.</summary>
    internal float CruiseSpeed { get; set; }

    // Behavior state

    /// <summary>Current behavior. When it expires, the bird switches to the other behavior.</summary>
    internal BirdBehavior Behavior { get; set; }

    /// <summary>Remaining behavior duration in seconds.</summary>
    internal float BehaviorTimer { get; set; }

    /// <summary>Current waypoint used by straight flight.</summary>
    internal Vector3 TargetPos { get; set; }

    /// <summary>Orbit center in XZ for circular flight.</summary>
    internal Vector2 OrbitCenter { get; set; }

    /// <summary>Orbit radius in meters.</summary>
    internal float OrbitRadius { get; set; }

    /// <summary>Orbit direction, plus or minus one.</summary>
    internal float OrbitDir { get; set; }

    /// <summary>Base orbit altitude in meters, with sinusoidal variation layered on top.</summary>
    internal float OrbitBaseAlt { get; set; }

    /// <summary>Phase of the orbit-altitude sine wave in radians.</summary>
    internal float OrbitPhase { get; set; }

    /// <summary>Elapsed time in the current orbit segment, used to drive altitude oscillation.</summary>
    internal float OrbitTime { get; set; }

    /// <summary>Glide clip used for the current behavior segment.</summary>
    internal string? EpisodeGlide { get; set; }

    /// <summary>Hysteresis flag for climb state during straight flight, used to stabilize flap versus glide switching.</summary>
    internal bool Climbing { get; set; }
}

/// <summary>
/// Seagull flock panel rendered through InstancedModel. All simulation state lives on the Seagull instances themselves.
/// The system covers spawn layout, straight and orbit behaviors, flock separation, obstacle avoidance, pose solving,
/// and animation mapping. Main tuning constants are grouped near the top and count and random seed can be overridden in the constructor.
/// </summary>
internal class Birds : Panel
{
    // Flock parameters
    const int DefaultCount = 20;
    const int DefaultSeed = 2026;

    // Flight zone
    const float FlightRadius = 120f;
    const float MinAltitude = 20f;
    const float MaxAltitude = 45f;
    const float ZoneInner = 0.15f;

    // Motion
    const float MinSpeed = 4f;
    const float MaxSpeed = 10f;
    const float Accel = 4f;
    const float MaxTurnRate = 2.5f;
    const float ArriveTime = 2f;

    // Waypoints
    const float WaypointArriveRadius = 3f;
    const float WaypointMinDistance = 15f;

    // Behavior
    const float BehaviorMinDuration = 6f;
    const float BehaviorMaxDuration = 18f;
    const float EarlySwitchProbability = 0.2f;
    const float OrbitMinRadius = 8f;
    const float OrbitMaxRadius = 22f;
    const float OrbitSpeedScale = 0.7f;
    const float OrbitVerticalGain = 0.8f;
    const float OrbitVerticalMax = 3f;
    const float OrbitAltAmplitude = 6f;
    const float OrbitAltFrequency = 0.3f;
    const float OrbitCenterMargin = 2f;
    const float OrbitCenterMinRadius = 5f;
    const float ClimbEnterAltitude = 5f;
    const float ClimbExitAltitude = 3f;

    // Separation and avoidance
    const float SeparationRadius = 6f;
    const float SeparationStrength = 3.5f;
    const float BirdRadius = 1.5f;
    const float HardSeparationDistance = 3.5f;
    const float ObstacleMargin = 3f;
    const float ObstaclePushStrength = 6f;
    const float LookaheadTime = 1.2f;
    const float LookaheadEscapeStrength = 2f;

    // Pose
    const float Gravity = 9.81f;
    const float PoseMaxPitch = 0.6f;
    const float PoseMaxRoll = 0.7f;
    const float PitchRate = 2.5f;
    const float RollRate = 2f;

    // Spawn
    const float SpawnMinDistance = 8f;
    const int SpawnMaxAttempts = 64;

    /// <summary>Flapping animation clip used for climb and dive effort.</summary>
    const string FlapAnimation = "flap";

    /// <summary>Glide clip pair used for straight gliding and orbiting.</summary>
    static readonly string[] GlideAnimations = { "planer", "planer 2" };

    internal List<Seagull> Seagulls = new List<Seagull>();

    internal InstancedModel seagullsModel;

    readonly Random rng;

    /// <summary>Per-frame cache of obstacle world bounds expanded from App.collider, reused without allocation.</summary>
    readonly List<Season.Rendering.Bounds3D> obstacleBoxes = new();

    internal Birds(int count = DefaultCount, int seed = DefaultSeed)
    {
        rng = new Random(seed);

        seagullsModel = new InstancedModel()
        {
            ModelName = @"Assets/flying_seagull.glb"
        };
        AddControl(seagullsModel);

        for (var i = 0; i < count; i++)
        {
            var seagull = new Seagull()
            {
                Width = 3,
                Height = 0.6f,
                Depth = 1.2f,
            };

            Spawn(seagull);
            Seagulls.Add(seagull);
        }

        SyncInstances();
    }

    /// <summary>
    /// Spawns one bird at a non-overlapping random position, assigns a random initial behavior and parameters,
    /// and randomizes animation offset and speed so the flock does not flap in sync.
    /// </summary>
    void Spawn(Seagull gull)
    {
        for (int attempt = 0; attempt < SpawnMaxAttempts; attempt++)
        {
            // Square-root radius keeps samples uniform over the disk instead of clustering near the center.
            float r = MathF.Sqrt(ZoneInner + rng.NextSingle() * (1f - ZoneInner)) * FlightRadius;
            float angle = rng.NextSingle() * MathF.Tau;
            float x = MathF.Cos(angle) * r;
            float z = MathF.Sin(angle) * r;
            float y = MinAltitude + rng.NextSingle() * (MaxAltitude - MinAltitude);

            bool overlap = false;
            for (int j = 0; j < Seagulls.Count; j++)
            {
                var other = Seagulls[j];
                float dx = x - other.PosX, dy = y - other.PosY, dz = z - other.PosZ;
                if (dx * dx + dy * dy + dz * dz < SpawnMinDistance * SpawnMinDistance)
                {
                    overlap = true;
                    break;
                }
            }

            if (overlap)
                continue;

            gull.PosX = x;
            gull.PosY = y;
            gull.PosZ = z;
            break;
        }

        var pos = new Vector3(gull.PosX, gull.PosY, gull.PosZ);
        gull.CruiseSpeed = MinSpeed + rng.NextSingle() * (MaxSpeed - MinSpeed);

        // Randomize the initial behavior and let UpdateAnimation take over clip selection.
        gull.Behavior = rng.NextSingle() < 0.5f ? BirdBehavior.Circle : BirdBehavior.Straight;
        gull.EpisodeGlide = rng.NextSingle() < 0.5f ? GlideAnimations[0] : GlideAnimations[1];
        AssignBehaviorParams(gull, pos);

        // Initial direction points toward the first waypoint for straight flight, or a random horizontal direction for orbiting.
        Vector3 dir;
        if (gull.Behavior == BirdBehavior.Straight)
        {
            dir = Vector3.Normalize(gull.TargetPos - pos);
        }
        else
        {
            float angle = rng.NextSingle() * MathF.Tau;
            dir = new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle));
        }

        gull.Velocity = dir * gull.CruiseSpeed;
        gull.Yaw = MathF.Atan2(-dir.X, -dir.Z);

        // Seed an initial clip and offset; UpdateAnimation will take over on the first frame.
        gull.Animation = gull.EpisodeGlide;
        gull.AnimationTimeOffset = rng.NextSingle() * 4f;
        gull.AnimationSpeed = 0.9f + rng.NextSingle() * 0.25f;
    }

    /// <summary>
    /// Switches to the other behavior, rotates the glide clip, and randomizes parameters and timer for the new segment.
    /// </summary>
    void ChooseNextBehavior(Seagull gull, in Vector3 pos)
    {
        gull.Behavior = gull.Behavior == BirdBehavior.Circle ? BirdBehavior.Straight : BirdBehavior.Circle;
        gull.EpisodeGlide = PickDifferentGlide(gull.Animation);
        AssignBehaviorParams(gull, pos);
    }

    /// <summary>Assigns randomized parameters for the current behavior and resets its timer.</summary>
    void AssignBehaviorParams(Seagull gull, in Vector3 pos)
    {
        if (gull.Behavior == BirdBehavior.Circle)
        {
            gull.OrbitRadius = OrbitMinRadius + rng.NextSingle() * (OrbitMaxRadius - OrbitMinRadius);
            // Keep the orbit center far enough inside the flight zone for the full circle to remain valid.
            float centerR = MathF.Max(FlightRadius - gull.OrbitRadius - OrbitCenterMargin, OrbitCenterMinRadius)
                          * MathF.Sqrt(rng.NextSingle());
            float angle = rng.NextSingle() * MathF.Tau;
            gull.OrbitCenter = new Vector2(MathF.Cos(angle) * centerR, MathF.Sin(angle) * centerR);
            gull.OrbitDir = rng.NextSingle() < 0.5f ? 1f : -1f;
            // Leave headroom for the altitude sine wave so the whole orbit stays inside the altitude band.
            gull.OrbitBaseAlt = MinAltitude + OrbitAltAmplitude
                              + rng.NextSingle() * (MaxAltitude - MinAltitude - 2f * OrbitAltAmplitude);
            gull.OrbitPhase = rng.NextSingle() * MathF.Tau;
            gull.OrbitTime = 0f;
        }
        else
        {
            gull.TargetPos = PickWaypoint(pos);
            gull.Climbing = false;
        }

        gull.BehaviorTimer = BehaviorMinDuration + rng.NextSingle() * (BehaviorMaxDuration - BehaviorMinDuration);
    }

    /// <summary>Returns a glide clip different from the current one, or a random glide clip if the current clip is not a glide clip.</summary>
    string PickDifferentGlide(string? current)
    {
        if (string.Equals(current, GlideAnimations[0]))
            return GlideAnimations[1];
        if (string.Equals(current, GlideAnimations[1]))
            return GlideAnimations[0];
        return rng.NextSingle() < 0.5f ? GlideAnimations[0] : GlideAnimations[1];
    }

    /// <summary>Picks a random waypoint inside the flight zone and altitude band, at least WaypointMinDistance away from the current position.</summary>
    Vector3 PickWaypoint(in Vector3 from)
    {
        for (int attempt = 0; attempt < 16; attempt++)
        {
            float r = MathF.Sqrt(ZoneInner + rng.NextSingle() * (1f - ZoneInner)) * FlightRadius;
            float angle = rng.NextSingle() * MathF.Tau;
            var wp = new Vector3(
                MathF.Cos(angle) * r,
                MinAltitude + rng.NextSingle() * (MaxAltitude - MinAltitude),
                MathF.Sin(angle) * r);

            if ((wp - from).LengthSquared() >= WaypointMinDistance * WaypointMinDistance)
                return wp;
        }

        // Fallback to the symmetric opposite point, which always stays inside the zone.
        return new Vector3(-from.X, (MinAltitude + MaxAltitude) * 0.5f, -from.Z);
    }

    /// <summary>
    /// Per-bird simulation step: update behavior, build desired motion, blend in separation and obstacle avoidance,
    /// smooth direction and speed, integrate motion, clamp back to the flight zone, solve pose, and update animation.
    /// Selected birds are skipped so editing panels keep write ownership.
    /// </summary>
    void UpdateFlight(float time)
    {
        var dt = MathF.Min(time, 0.05f);   // Clamp frame interval to avoid tunneling and turn overshoot at low frame rates.

        // Collect obstacle bounds once per frame from the shared PlayerCollider registration.
        var collider = App.Instance.collider;
        if (collider != null)
            collider.CollectObstacleBoxes(obstacleBoxes);
        else
            obstacleBoxes.Clear();

        for (int i = 0; i < Seagulls.Count; i++)
        {
            var gull = Seagulls[i];
            if (gull.Selected)
                continue;

            var pos = new Vector3(gull.PosX, gull.PosY, gull.PosZ);
            var prevVel = gull.Velocity;   // Previous-frame velocity, used for banking acceleration.

            gull.BehaviorTimer -= dt;
            if (gull.BehaviorTimer <= 0f)
                ChooseNextBehavior(gull, pos);

            Vector3 desiredDir;
            float desiredSpeed;
            if (gull.Behavior == BirdBehavior.Straight)
                SteerStraight(gull, pos, out desiredDir, out desiredSpeed);
            else
                SteerOrbit(gull, pos, dt, out desiredDir, out desiredSpeed);

            // Phase 3: combine base steering with flock separation and obstacle avoidance.
            var desired = desiredDir * desiredSpeed + Separation(gull, pos, i) + ObstacleAvoidance(gull, pos);
            if (desired.LengthSquared() > 1e-6f)
            {
                desiredDir = Vector3.Normalize(desired);
                desiredSpeed = Math.Clamp(desired.Length(), MinSpeed, MaxSpeed);
            }
            // Rare case: if avoidance cancels steering almost exactly, keep the base steering for this frame.

            // Shared smoothing: turn-rate limiting and speed convergence.
            var velLen = gull.Velocity.Length();
            Vector3 dir;
            if (velLen < 1e-3f)
            {
                dir = desiredDir;
            }
            else
            {
                // If the angular change exceeds MaxTurnRate*dt, scale it back with normalized lerp.
                var curDir = gull.Velocity / velLen;
                float angle = AngleBetween(curDir, desiredDir);
                float maxTurn = MaxTurnRate * dt;
                dir = angle <= maxTurn
                    ? desiredDir
                    : Vector3.Normalize(Vector3.Lerp(curDir, desiredDir, maxTurn / angle));
            }

            gull.Velocity = dir * MoveTowards(velLen, desiredSpeed, Accel * dt);

            gull.PosX = pos.X + gull.Velocity.X * dt;
            gull.PosY = pos.Y + gull.Velocity.Y * dt;
            gull.PosZ = pos.Z + gull.Velocity.Z * dt;

            ClampToZone(gull);

            // Recover yaw from velocity using the engine forward convention in Skill.YawForward.
            gull.Yaw = MathF.Atan2(-gull.Velocity.X, -gull.Velocity.Z);

            UpdatePose(gull, prevVel, dt);

            UpdateAnimation(gull, pos);
        }

        HardSeparate();   // Final hard separation in XZ keeps nearby bird pairs from overlapping.
    }

    /// <summary>
    /// Straight-flight steering. On arrival, the bird either switches behavior early or chooses a new waypoint,
    /// and decelerates into the approach segment.
    /// </summary>
    void SteerStraight(Seagull gull, in Vector3 pos, out Vector3 dir, out float speed)
    {
        var toTarget = gull.TargetPos - pos;
        if (toTarget.LengthSquared() <= WaypointArriveRadius * WaypointArriveRadius)
        {
            if (rng.NextSingle() < EarlySwitchProbability)
            {
                ChooseNextBehavior(gull, pos);
                SteerOrbit(gull, pos, 0f, out dir, out speed);
                return;
            }

            gull.TargetPos = PickWaypoint(pos);
            toTarget = gull.TargetPos - pos;
        }

        float dist = toTarget.Length();
        if (dist < 1e-3f)
        {
            // Fallback if the waypoint lands exactly on the current position.
            dir = Vector3.UnitZ;
            speed = gull.CruiseSpeed;
            return;
        }

        // Cruise at distance, then linearly slow down over the last ArriveTime seconds of travel.
        dir = toTarget / dist;
        speed = MathF.Max(MathF.Min(gull.CruiseSpeed, dist / ArriveTime), MinSpeed);
    }

    /// <summary>
    /// Orbit steering. Horizontal motion blends tangent direction with radial correction,
    /// while altitude tracks a sine wave around the base orbit height.
    /// The returned vector is later smoothed together with the rest of the motion pipeline.
    /// </summary>
    void SteerOrbit(Seagull gull, in Vector3 pos, float dt, out Vector3 dir, out float speed)
    {
        gull.OrbitTime += dt;

        float rx = pos.X - gull.OrbitCenter.X;
        float rz = pos.Z - gull.OrbitCenter.Y;
        float dist = MathF.Sqrt(rx * rx + rz * rz);
        Vector2 radialDir;
        if (dist < 1e-3f)
        {
            radialDir = new Vector2(1f, 0f);
            dist = 1e-3f;
        }
        else
        {
            radialDir = new Vector2(rx / dist, rz / dist);
        }

        // Tangent direction in the XZ plane, signed by orbit direction.
        var tangent = new Vector2(-radialDir.Y, radialDir.X) * gull.OrbitDir;
        float blend = Math.Clamp((dist - gull.OrbitRadius) / gull.OrbitRadius, -1f, 1f);
        var horiz = Vector2.Normalize(tangent + radialDir * blend * 0.8f);

        float alt = gull.OrbitBaseAlt + MathF.Sin(gull.OrbitTime * OrbitAltFrequency + gull.OrbitPhase) * OrbitAltAmplitude;

        float orbitSpeed = MathF.Max(gull.CruiseSpeed * OrbitSpeedScale, MinSpeed);
        var desired = new Vector3(
            horiz.X * orbitSpeed,
            Math.Clamp((alt - pos.Y) * OrbitVerticalGain, -OrbitVerticalMax, OrbitVerticalMax),
            horiz.Y * orbitSpeed);

        dir = Vector3.Normalize(desired);
        speed = Math.Clamp(desired.Length(), MinSpeed, MaxSpeed);
    }

    /// <summary>
    /// Flock separation. Nearby birds inside SeparationRadius push away with inverse falloff,
    /// with half weight on Y so separation stays mostly horizontal. Selected birds still participate as static obstacles.
    /// </summary>
    Vector3 Separation(Seagull gull, in Vector3 pos, int selfIndex)
    {
        var push = Vector3.Zero;

        for (int j = 0; j < Seagulls.Count; j++)
        {
            if (j == selfIndex)
                continue;

            var other = Seagulls[j];
            float dx = pos.X - other.PosX;
            float dy = pos.Y - other.PosY;
            float dz = pos.Z - other.PosZ;
            float distSq = dx * dx + dy * dy + dz * dz;
            if (distSq >= SeparationRadius * SeparationRadius || distSq < 1e-6f)
                continue;

            float dist = MathF.Sqrt(distSq);
            float w = (1f - dist / SeparationRadius) * SeparationStrength;
            push.X += dx / dist * w;
            push.Y += dy / dist * w * 0.5f;
            push.Z += dz / dist * w;
        }

        return push;
    }

    /// <summary>
    /// Obstacle avoidance against mountains and buildings.
    /// First apply a nearest-point push on the current bird sphere against obstacle AABBs,
    /// then apply a stronger lookahead escape if the future probe sphere intersects an obstacle.
    /// Low obstacles are rejected cheaply when both current and lookahead positions stay above the box top.
    /// </summary>
    Vector3 ObstacleAvoidance(Seagull gull, in Vector3 pos)
    {
        var push = Vector3.Zero;
        var probe = pos + gull.Velocity * LookaheadTime;
        float floor = MathF.Min(pos.Y, probe.Y) - BirdRadius;

        for (int i = 0; i < obstacleBoxes.Count; i++)
        {
            var box = obstacleBoxes[i];
            if (floor > box.Center.Y + box.Extents.Y)
                continue;   // Both current and lookahead positions are above the box top, so no intersection is possible.

            var min = box.Center - box.Extents;
            var max = box.Center + box.Extents;

            // Current-sphere push
            var closest = Vector3.Clamp(pos, min, max);
            var d = pos - closest;
            float dist = d.Length();
            float reach = BirdRadius + ObstacleMargin;
            if (dist < reach)
            {
                var dir = dist > 1e-3f ? d / dist : Vector3.UnitY;   // Defensive fallback if the center is inside the box.
                push += dir * (1f - dist / reach) * ObstaclePushStrength;
            }

            // Lookahead-sphere escape
            closest = Vector3.Clamp(probe, min, max);
            d = probe - closest;
            dist = d.Length();
            if (dist < BirdRadius)
            {
                var dir = dist > 1e-3f ? d / dist : Vector3.UnitY;
                push += dir * ObstaclePushStrength * LookaheadEscapeStrength;
            }
        }

        return push;
    }

    /// <summary>
    /// Hard-separation fallback for close bird pairs that soft separation did not resolve in time.
    /// Overlap is pushed apart in XZ, while selected frozen birds stay still and moving birds absorb the whole correction when needed.
    /// </summary>
    void HardSeparate()
    {
        for (int i = 0; i < Seagulls.Count; i++)
        {
            var a = Seagulls[i];
            bool aFree = !a.Selected;

            for (int j = i + 1; j < Seagulls.Count; j++)
            {
                var b = Seagulls[j];
                bool bFree = !b.Selected;
                if (!aFree && !bFree)
                    continue;

                float dx = b.PosX - a.PosX;
                float dy = b.PosY - a.PosY;
                float dz = b.PosZ - a.PosZ;
                float dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                if (dist >= HardSeparationDistance)
                    continue;

                float horiz = MathF.Sqrt(dx * dx + dz * dz);
                if (horiz < 1e-3f)
                    continue;

                float overlap = HardSeparationDistance - dist;
                float divisor = aFree && bFree ? 2f : 1f;
                float step = overlap / divisor / horiz;
                dx *= step;
                dz *= step;

                if (aFree)
                {
                    a.PosX -= dx;
                    a.PosZ -= dz;
                }
                if (bFree)
                {
                    b.PosX += dx;
                    b.PosZ += dz;
                }
            }
        }
    }

    /// <summary>
    /// Pose solving. Pitch follows climb and dive angle, while roll follows horizontal centripetal acceleration.
    /// Both are clamped and smoothed so separation and avoidance impulses do not turn into visible pose jitter.
    /// </summary>
    void UpdatePose(Seagull gull, in Vector3 prevVel, float dt)
    {
        var vel = gull.Velocity;
        float horiz = MathF.Sqrt(vel.X * vel.X + vel.Z * vel.Z);

        // Pitch from velocity angle relative to the horizontal plane.
        float targetPitch = 0f;
        if (horiz > 0.5f)
            targetPitch = Math.Clamp(MathF.Atan2(vel.Y, horiz), -PoseMaxPitch, PoseMaxPitch);
        gull.Pitch = MoveTowards(gull.Pitch, targetPitch, PitchRate * dt);

        // Roll from horizontal centripetal acceleration projected onto the current right direction.
        float targetRoll = 0f;
        float prevHoriz = MathF.Sqrt(prevVel.X * prevVel.X + prevVel.Z * prevVel.Z);
        if (prevHoriz > 0.5f && horiz > 0.5f)
        {
            float fx = prevVel.X / prevHoriz;
            float fz = prevVel.Z / prevHoriz;
            float aLat = (-fz * (vel.X - prevVel.X) + fx * (vel.Z - prevVel.Z)) / dt;
            targetRoll = Math.Clamp(-MathF.Atan(aLat / Gravity), -PoseMaxRoll, PoseMaxRoll);
        }
        gull.Roll = MoveTowards(gull.Roll, targetRoll, RollRate * dt);
    }

    /// <summary>
    /// Animation mapping. Orbiting always uses glide clips, while straight flight switches between flap and glide
    /// through altitude-difference hysteresis. Clip changes randomize AnimationTimeOffset again to avoid synchrony.
    /// </summary>
    void UpdateAnimation(Seagull gull, in Vector3 pos)
    {
        string next;

        if (gull.Behavior == BirdBehavior.Circle)
        {
            gull.Climbing = false;
            next = gull.EpisodeGlide ?? GlideAnimations[0];
        }
        else
        {
            float deltaAlt = MathF.Abs(gull.TargetPos.Y - pos.Y);
            if (!gull.Climbing && deltaAlt > ClimbEnterAltitude)
                gull.Climbing = true;
            else if (gull.Climbing && deltaAlt < ClimbExitAltitude)
                gull.Climbing = false;

            next = gull.Climbing ? FlapAnimation : (gull.EpisodeGlide ?? GlideAnimations[0]);
        }

        if (!string.Equals(gull.Animation, next))
        {
            gull.Animation = next;
            gull.AnimationTimeOffset = rng.NextSingle() * 4f;   // Re-randomize offset on clip changes to keep the flock unsynchronized.
        }
    }

    /// <summary>Clamps birds back into the flight zone by enforcing altitude bounds and removing outward radial velocity when they hit the horizontal radius limit.</summary>
    void ClampToZone(Seagull gull)
    {
        // Velocity is a property returning a struct, so modify a local copy and write it back once.
        var vel = gull.Velocity;

        if (gull.PosY < MinAltitude)
        {
            gull.PosY = MinAltitude;
            if (vel.Y < 0f)
                vel.Y = 0f;
        }
        else if (gull.PosY > MaxAltitude)
        {
            gull.PosY = MaxAltitude;
            if (vel.Y > 0f)
                vel.Y = 0f;
        }

        float r = MathF.Sqrt(gull.PosX * gull.PosX + gull.PosZ * gull.PosZ);
        if (r > FlightRadius)
        {
            float scale = FlightRadius / r;
            gull.PosX *= scale;
            gull.PosZ *= scale;

            float radial = vel.X * gull.PosX + vel.Z * gull.PosZ;   // After snapping back to the ring, positive means outward.
            if (radial > 0f)
            {
                vel.X -= radial * gull.PosX / (FlightRadius * FlightRadius);
                vel.Z -= radial * gull.PosZ / (FlightRadius * FlightRadius);
            }
        }

        gull.Velocity = vel;
    }

    static float AngleBetween(Vector3 a, Vector3 b) =>
        MathF.Acos(Math.Clamp(Vector3.Dot(a, b), -1f, 1f));

    static float MoveTowards(float current, float target, float maxDelta) =>
        current + Math.Clamp(target - current, -maxDelta, maxDelta);

    void SyncInstances()
    {
        var instances = seagullsModel.Instances;

        while (instances.Count < Seagulls.Count)
            instances.Add(Seagulls[instances.Count]);

        while (instances.Count > Seagulls.Count)
            instances.RemoveAt(instances.Count - 1);

        for (int i = 0; i < Seagulls.Count; i++)
        {
            if (!ReferenceEquals(instances[i], Seagulls[i]))
                instances[i] = Seagulls[i];
        }
    }

    void ApplySeagulls()
    {
        for (int i = 0; i < Seagulls.Count; i++)
        {
            var person = Seagulls[i];

            if (person.Selected)
            {
                var names = seagullsModel.AnimationNames;
                person.Animation = person.AnimationClip >= 0 && person.AnimationClip < names.Count
                    ? names[person.AnimationClip]
                    : null;
                continue;
            }

            person.Rotation = Quaternion.CreateFromYawPitchRoll(person.Yaw, person.Pitch, person.Roll);
            person.AnimationClip = ResolveAnimationClip(person.Animation);
        }
    }

    int ResolveAnimationClip(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return 0;

        var names = seagullsModel.AnimationNames;
        for (int i = 0; i < names.Count; i++)
        {
            if (names[i] == name)
                return i;
        }

        return 0;
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        SyncInstances();
        UpdateFlight(time);
        ApplySeagulls();
        seagullsModel.Update(time);

        return false;
    }
}
