// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Engine.Management;

/// <summary>
/// Simple collision detection for Player movement. Registered obstacles are queried using
/// their current world bounds: Mesh3DBase uses GetWorldBoundsRaw to stay aligned with the
/// rendered geometry, while instanced controls expand one box per enabled instance by
/// transforming the template's local bounds with the instance matrix.
/// The requested horizontal displacement (dx, dz) is clamped axis by axis with a sweep test,
/// stopping just outside the obstacle contact face with a ContactEpsilon margin.
/// Diagonal input can slide along obstacle faces, and a fully blocked move returns a zero vector.
/// Mountains are handled separately through the terrain height map (Terrain), which stores the
/// true mesh XZ coverage and per-cell surface heights baked by Mountains. After the box sweep,
/// motion is retracted per axis when the footprint height exceeds the current floor baseline by
/// more than TerrainStepLimit. This matches visible mountains accurately even for rotated,
/// concave, or intersecting shapes where rotated AABB tests would be too conservative
/// (see Mountains.cs). If Terrain is null, terrain blocking is not yet ready and is skipped.
/// The Y axis is not solved independently because movement is locked to the ground plane,
/// but Y overlap still participates in blocking decisions. Overhead pieces such as lintels
/// or roofs do not block ground movement, and whether a long jump clears a low obstacle is
/// determined naturally by the jump arc height.
/// This class also provides step and floor height queries through FloorHeightUnder, using the
/// layout of the registered Room. When the player's footprint touches a step or indoor floor,
/// the player is lifted to that top surface and steps back down level by level when leaving.
/// Consumed by Direction.MovePlayer and UpdateLongJump.
/// </summary>
internal sealed class PlayerCollider
{
    /// <summary>Registered single-instance obstacles. Mesh3D and Model both derive from Mesh3DBase, and world bounds come from GetWorldBoundsRaw.</summary>
    internal List<Mesh3DBase> Obstacles { get; } = new();

    /// <summary>Registered instanced obstacles such as Robots. One box is expanded for each enabled instance using template bounds transformed by the instance matrix.</summary>
    internal List<InstancedMesh3DBase> InstancedObstacles { get; } = new();

    /// <summary>Registered mountain terrain height map, written back after Mountains baking completes. Null means terrain blocking is not ready yet.</summary>
    internal MountainTerrain Terrain;

    /// <summary>Registered room used as the data source for step lifting and indoor floor height. Null means all grassland and a floor height of zero.</summary>
    internal Room Room;

    /// <summary>Contact margin: stop 1 mm before intersection to avoid jitter from floating-point boundary ambiguity.</summary>
    const float ContactEpsilon = 1e-3f;

    /// <summary>Terrain blocking threshold: movement is blocked when the footprint cell height exceeds the current floor baseline by more than this value. Flat ground cells are zero, and cells on the mountain's outer shell that stay below ground level do not block.</summary>
    const float TerrainStepLimit = 0.5f;

    // Reusable cache of obstacle world boxes for the current query. TryMove clears and reuses it
    // each time, which keeps queries allocation-free for scenes with only dozens of boxes.
    readonly List<Season.Rendering.Bounds3D> boxes = new();

    /// <summary>
    /// Attempts to move the player bounds horizontally by (dx, dz) and returns the actual
    /// displacement that can be applied, where Vector2.X is the X axis and Vector2.Y is the Z axis.
    /// <paramref name="player"/> should be the player's current world bounds, typically read
    /// after orientation has settled because the footprint changes with Rotation, via GetWorldBoundsRaw.
    /// Obstacles that are not loaded yet, have zero-size bounds, or are disabled are skipped automatically,
    /// so registration may happen before asset loading finishes.
    /// </summary>
    internal Vector2 TryMove(in Season.Rendering.Bounds3D player, float dx, float dz)
    {
        CollectObstacleBoxes(boxes);

        // Solve per axis: X first, then Z based on the center after X is applied,
        // which allows diagonal motion to slide along obstacle faces.
        // Each axis uses two constraint stages: obstacle sweep, then terrain retraction.
        var center = player.Center;

        float moveX = Sweep(player.Extents, ref center, dx, alongX: true);
        moveX = RetractTerrain(player.Extents, ref center, moveX, alongX: true);

        float moveZ = Sweep(player.Extents, ref center, dz, alongX: false);
        moveZ = RetractTerrain(player.Extents, ref center, moveZ, alongX: false);

        return new Vector2(moveX, moveZ);
    }

    /// <summary>
    /// Clamps motion along a single axis. For each obstacle, first require overlap on the other
    /// two axes so the obstacle is actually on the movement path, then clamp against the nearest
    /// contact face on the moving axis. If the player has already passed the contact face and is
    /// embedded in the obstacle, no push-back is applied: the method only guarantees that movement
    /// does not continue deeper into the overlap. The retained motion is applied to center at the end
    /// so the next axis can use the updated position.
    /// </summary>
    float Sweep(Vector3 half, ref Vector3 center, float delta, bool alongX)
    {
        if (MathF.Abs(delta) < 1e-6f)
            return 0f;

        float allowed = delta;

        for (int i = 0; i < boxes.Count; i++)
        {
            var box = boxes[i];

            // The two non-moving axes must overlap; if either axis is separated,
            // the obstacle is not on the current movement path.
            if (!Overlaps(alongX ? center.Z : center.X, alongX ? half.Z : half.X,
                          alongX ? box.Center.Z : box.Center.X, alongX ? box.Extents.Z : box.Extents.X))
                continue;

            if (!Overlaps(center.Y, half.Y, box.Center.Y, box.Extents.Y))
                continue;

            float c = alongX ? center.X : center.Z;
            float h = alongX ? half.X : half.Z;
            float boxCenter = alongX ? box.Center.X : box.Center.Z;
            float boxHalf = alongX ? box.Extents.X : box.Extents.Z;

            if (delta > 0f)
            {
                // Positive direction: contact face = obstacle min face - player half width - margin.
                // Clamp only while still outside that contact face.
                float contact = boxCenter - boxHalf - h - ContactEpsilon;
                if (c <= contact)
                    allowed = MathF.Min(allowed, contact - c);
            }
            else
            {
                // Negative direction: contact face = obstacle max face + player half width + margin.
                float contact = boxCenter + boxHalf + h + ContactEpsilon;
                if (c >= contact)
                    allowed = MathF.Max(allowed, contact - c);
            }
        }

        if (alongX) center.X += allowed;
        else center.Z += allowed;

        return allowed;
    }

    /// <summary>One-dimensional interval overlap test using center plus half-length. Touching edges still count as potential overlap and are resolved by Sweep.</summary>
    static bool Overlaps(float c1, float h1, float c2, float h2) => MathF.Abs(c1 - c2) <= h1 + h2;

    /// <summary>
    /// Retracts motion against the terrain height map after Sweep has already applied the clamped
    /// target position to center. If the target footprint height exceeds the starting floor baseline
    /// by more than the threshold, motion is binary-searched back along this axis to the farthest
    /// reachable position. When multiple blocking segments are crossed, it stops before the first wall
    /// instead of tunneling through. If the starting footprint is already embedded, no retraction is
    /// applied, consistent with Sweep's no-push-back policy. Returns the retained signed displacement.
    /// </summary>
    float RetractTerrain(Vector3 half, ref Vector3 center, float allowed, bool alongX)
    {
        if (Terrain == null || MathF.Abs(allowed) < 1e-6f)
            return allowed;

        var start = center;
        if (alongX) start.X -= allowed;
        else start.Z -= allowed;

        // Starting floor baseline: grassland zero, step top, or indoor floor.
        // Terrain cells block only by height relative to this baseline.
        float baseH = FloorHeightUnder(start, half);

        if (Terrain.FootprintMaxHeight(start, half) - baseH > TerrainStepLimit)
            return allowed;   // Already embedded, so do not retract.

        if (Terrain.FootprintMaxHeight(center, half) - baseH <= TerrainStepLimit)
            return allowed;   // Target is reachable, so no retraction is needed.

        // Target is too high: binary-search the farthest reachable point along the movement direction.
        // lo always stays reachable, hi always stays blocked.
        float dist = MathF.Abs(allowed);
        float dir = MathF.Sign(allowed);
        float lo = 0f, hi = dist;

        for (int i = 0; i < 10; i++)
        {
            float mid = (lo + hi) * 0.5f;

            var probe = start;
            if (alongX) probe.X += dir * mid;
            else probe.Z += dir * mid;

            if (Terrain.FootprintMaxHeight(probe, half) - baseH <= TerrainStepLimit)
                lo = mid;
            else
                hi = mid;
        }

        float retained = MathF.Max(0f, lo - ContactEpsilon) * dir;

        if (alongX) center.X = start.X + retained;
        else center.Z = start.Z + retained;

        return retained;
    }

    /// <summary>
    /// Floor height under the footprint: sample the center plus the four corners and take the maximum.
    /// As soon as the footprint touches a step or indoor floor, the player is lifted to that top surface,
    /// preventing feet from sinking into a higher step ahead. Stepping off lowers symmetrically level by level.
    /// Without a registered Room, the floor height is always zero.
    /// </summary>
    internal float FloorHeightUnder(in Vector3 center, in Vector3 half)
    {
        if (Room == null)
            return 0f;

        float h = Room.FloorHeightAtWorld(center.X, center.Z);
        h = MathF.Max(h, Room.FloorHeightAtWorld(center.X - half.X, center.Z - half.Z));
        h = MathF.Max(h, Room.FloorHeightAtWorld(center.X - half.X, center.Z + half.Z));
        h = MathF.Max(h, Room.FloorHeightAtWorld(center.X + half.X, center.Z - half.Z));
        h = MathF.Max(h, Room.FloorHeightAtWorld(center.X + half.X, center.Z + half.Z));
        return h;
    }

    /// <summary>
    /// Collects all obstacle world boxes. Objects that are not ready, disabled, or degenerate
    /// with zero-size bounds are skipped. This is shared by TryMove and external avoidance logic
    /// such as the seagull flock. <paramref name="into"/> is cleared before writing so each query stays allocation-free.
    /// </summary>
    internal void CollectObstacleBoxes(List<Season.Rendering.Bounds3D> into)
    {
        into.Clear();

        for (int i = 0; i < Obstacles.Count; i++)
        {
            var obstacle = Obstacles[i];

            if (!obstacle.Ready || !obstacle.Enable || obstacle.LocalBoundsRaw.Extents == Vector3.Zero)
                continue;

            into.Add(obstacle.GetWorldBoundsRaw());
        }

        for (int i = 0; i < InstancedObstacles.Count; i++)
        {
            var host = InstancedObstacles[i];

            if (!host.Ready || !host.Enable || host.TemplateLocalBoundsRaw.Extents == Vector3.Zero)
                continue;

            var instances = host.Instances;
            for (int j = 0; j < instances.Count; j++)
            {
                var instance = instances[j];
                if (!instance.Enable)
                    continue;

                var box = host.TemplateLocalBoundsRaw.Transform(host.BuildInstanceMatrix(instance));
                if (box.Extents == Vector3.Zero)
                    continue; // Size is not finalized yet, such as scale zero, so degenerate boxes are ignored.

                into.Add(box);
            }
        }
    }
}
