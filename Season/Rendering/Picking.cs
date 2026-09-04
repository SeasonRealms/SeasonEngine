// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// Picking math toolbox in the shared layer, with zero backend forks: screen point → world-space picking ray.
/// Conventions are kept exactly consistent with Camera3D and 1-3 culling:
/// LH + [0,1] depth + row vectors in the form pos·M.
/// Unprojection uses <see cref="Camera3D.RenderViewProjection"/>, which means non-jittered projection plus desktop DPI compensation,
/// matching the actual rendered pixel mapping exactly.
/// Using the uncompensated ViewProjection from culling and CSM would misalign results under high DPI.
/// Screen coordinates use logical pixels, matching TouchService.PoX and PoY after division by BaseApp.Scale.
/// </summary>
public static class Picking
{
    /// <summary>
    /// Screen point → world-space picking ray, where origin is the camera position and direction is the normalized outgoing direction through that pixel.
    /// Returns false when width or height is non-positive, the matrix has not been built yet, meaning the identity sentinel before the first rendered frame,
    /// or the matrix is not invertible. Callers should treat that case as "no ray".
    /// </summary>
    public static bool ScreenPointToRay(float px, float py, Camera3D camera, float width, float height,
        out Vector3 origin, out Vector3 direction)
    {
        origin = default;
        direction = default;

        if (camera == null || width <= 0f || height <= 0f)
            return false;

        var viewProjection = camera.RenderViewProjection;

        // Before the first rendered frame, the matrix has not yet been rebuilt because UpdateIfChanged runs during backend rendering.
        // Under the identity sentinel, unprojection degenerates into a constant +Z ray through the camera position and causes false hits, so reject it outright.
        if (viewProjection == Matrix4x4.Identity)
            return false;

        if (!Matrix4x4.Invert(viewProjection, out var invViewProjection))
            return false;

        px = px / DeviceServices.BaseApp.CompositionScale.X;
        py = py / DeviceServices.BaseApp.CompositionScale.Y;

        // Screen space, top-left origin with y downward, → NDC, center origin with y upward in [-1,1].
        float ndcX = 2f * px / width - 1f;
        float ndcY = 1f - 2f * py / height;

        // LH + [0,1] depth means z=0 at the near plane and z=1 at the far plane, so both points are unprojected through the inverse ViewProjection.
        // This must use Vector4 plus a manual w divide.
        // Vector3.Transform(Vector3, Matrix4x4) is purely affine and performs no perspective divide, so far-near would collapse into the constant third row of the inverse matrix.
        // The result would be a ray permanently stuck to the camera-forward center bundle and independent of the pixel, a historically observed defect.
        // The row-vector pos·M convention applies to the inverse matrix as well.
        var near = Unproject(ndcX, ndcY, 0f, invViewProjection);
        var far = Unproject(ndcX, ndcY, 1f, invViewProjection);

        var delta = far - near;
        if (delta.LengthSquared() < 1e-12f)
            return false;

        origin = camera.Position;
        direction = Vector3.Normalize(delta);

        return true;
    }

    /// <summary>Unprojects an NDC point into world space through a 4×4 matrix using Vector4.Transform plus an explicit w divide, with |w| clamped from below to avoid division by zero.</summary>
    static Vector3 Unproject(float ndcX, float ndcY, float ndcZ, Matrix4x4 matrix)
    {
        var clip = Vector4.Transform(new Vector4(ndcX, ndcY, ndcZ, 1f), matrix);

        float w = MathF.Abs(clip.W) < 1e-8f ? (clip.W < 0f ? -1e-8f : 1e-8f) : clip.W;

        return new Vector3(clip.X / w, clip.Y / w, clip.Z / w);
    }

    /// <summary>
    /// Intersects a world-space ray with an OBB, shared by Mesh3DBase.TryPick and InstancedMesh3DBase.TryPickInstance.
    /// The ray is transformed into local space by the inverse of world, then tested against localBounds min/max with slab tests.
    /// This is equivalent to testing against the world-space OBB and matches the object's screen projection exactly under rotation and non-uniform scaling,
    /// which is better than a coarse world-AABB test.
    /// If the camera starts inside the box, meaning tMin&lt;0≤tMax, that still counts as a hit with distance=0.
    /// Distance is returned in world space so values remain comparable across controls and instances.
    /// Returns false if world is not invertible.
    /// </summary>
    internal static bool RayIntersectsObb(Vector3 rayOrigin, Vector3 rayDirection, in Matrix4x4 world, in Bounds3D localBounds, out float distance)
    {
        distance = 0f;

        if (!Matrix4x4.Invert(world, out var invWorld))
            return false;

        // Transform the ray into local space: points use the full inverse transform, including translation, while directions use TransformNormal.
        // Under non-uniform scale the direction is no longer unit length, but it still represents the same line, so slab interval testing remains valid.
        var localOrigin = Vector3.Transform(rayOrigin, invWorld);
        var localDir = Vector3.TransformNormal(rayDirection, invWorld);

        var min = localBounds.Center - localBounds.Extents;
        var max = localBounds.Center + localBounds.Extents;

        float tMin = 0f, tMax = float.MaxValue;

        if (!SlabAxis(localOrigin.X, localDir.X, min.X, max.X, ref tMin, ref tMax)) return false;
        if (!SlabAxis(localOrigin.Y, localDir.Y, min.Y, max.Y, ref tMin, ref tMax)) return false;
        if (!SlabAxis(localOrigin.Z, localDir.Z, min.Z, max.Z, ref tMin, ref tMax)) return false;

        if (tMax < 0f)
            return false;

        // Transform the hit point back to world space and convert to world distance.
        // Local-space t values are not comparable across controls, so the metric is normalized here into world space.
        var localHit = localOrigin + localDir * MathF.Max(tMin, 0f);
        var worldHit = Vector3.Transform(localHit, world);
        distance = (worldHit - rayOrigin).Length();

        return true;
    }

    /// <summary>Single-axis slab interval test. When the direction component is approximately zero, the origin must already lie inside the slab; otherwise the method clips the [tMin,tMax] range.</summary>
    internal static bool SlabAxis(float origin, float direction, float min, float max, ref float tMin, ref float tMax)
    {
        if (MathF.Abs(direction) < 1e-8f)
            return origin >= min && origin <= max;

        float t1 = (min - origin) / direction;
        float t2 = (max - origin) / direction;

        if (t1 > t2)
            (t1, t2) = (t2, t1);

        if (t1 > tMin) tMin = t1;
        if (t2 < tMax) tMax = t2;

        return tMin <= tMax;
    }

    /// <summary>
    /// Precise ray-triangle test for the v2 narrow picking phase using Moller-Trumbore with no back-face culling.
    /// The ray is transformed into local space through the inverse world matrix, then tested against every triangle, keeping the nearest hit.
    /// The hit point is transformed back to world space and converted into a world-space distance, following the same convention as <see cref="RayIntersectsObb"/>, so results are comparable across controls.
    /// This is mathematically equivalent to point-in-triangle testing after 2D projection, but better:
    /// one pass in local 3D space yields the exact surface depth t directly, with no per-vertex projection, camera clipping, or depth re-interpolation.
    /// Overlapping objects naturally resolve to the nearest surface, that is, the one closest to the screen.
    /// positions and indices must come from the same rendering source after RH→LH conversion.
    /// Degenerate geometry with fewer than 3 vertices or indices, or a non-invertible world matrix, returns false.
    /// </summary>
    internal static bool RayIntersectsTriangles(Vector3 rayOrigin, Vector3 rayDirection, in Matrix4x4 world,
        ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices, out float distance)
    {
        distance = 0f;

        if (positions.Length < 3 || indices.Length < 3)
            return false;

        if (!Matrix4x4.Invert(world, out var invWorld))
            return false;

        var localOrigin = Vector3.Transform(rayOrigin, invWorld);
        var localDir = Vector3.TransformNormal(rayDirection, invWorld);

        if (!RayTrianglesCore(localOrigin, localDir, positions, indices, out float bestT))
            return false;

        var localHit = localOrigin + localDir * bestT;
        var worldHit = Vector3.Transform(localHit, world);
        distance = (worldHit - rayOrigin).Length();

        return true;
    }

    /// <summary>
    /// Surface overload of <see cref="RayIntersectsTriangles"/>, directly reusing shared-layer Vertex data with zero copy.
    /// Indices use ushort, matching <see cref="Surface.Indices"/>.
    /// </summary>
    internal static bool RayIntersectsTriangles(Vector3 rayOrigin, Vector3 rayDirection, in Matrix4x4 world,
        ReadOnlySpan<Vertex> vertices, ReadOnlySpan<ushort> indices, out float distance)
    {
        distance = 0f;

        if (vertices.Length < 3 || indices.Length < 3)
            return false;

        if (!Matrix4x4.Invert(world, out var invWorld))
            return false;

        var localOrigin = Vector3.Transform(rayOrigin, invWorld);
        var localDir = Vector3.TransformNormal(rayDirection, invWorld);

        float bestT = float.MaxValue;
        bool hit = false;
        int triCount = indices.Length / 3; // Drop remainder, matching rendering behavior.
        for (int tri = 0; tri < triCount; tri++)
        {
            int i0 = indices[tri * 3];
            int i1 = indices[tri * 3 + 1];
            int i2 = indices[tri * 3 + 2];
            if ((uint)i0 >= (uint)vertices.Length || (uint)i1 >= (uint)vertices.Length || (uint)i2 >= (uint)vertices.Length)
                continue;

            if (MollerTrumbore(vertices[i0].Position, vertices[i1].Position, vertices[i2].Position,
                    localOrigin, localDir, out float t) && t < bestT)
            {
                bestT = t;
                hit = true;
            }
        }

        if (!hit)
            return false;

        var localHit = localOrigin + localDir * bestT;
        var worldHit = Vector3.Transform(localHit, world);
        distance = (worldHit - rayOrigin).Length();

        return true;
    }

    /// <summary>Core triangle traversal for uint indices, keeping the nearest hit parameter t across triangles.</summary>
    static bool RayTrianglesCore(Vector3 localOrigin, Vector3 localDir,
        ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices, out float bestT)
    {
        bestT = float.MaxValue;
        bool hit = false;
        int triCount = indices.Length / 3; // Drop remainder, matching rendering behavior.
        for (int tri = 0; tri < triCount; tri++)
        {
            int i0 = (int)indices[tri * 3];
            int i1 = (int)indices[tri * 3 + 1];
            int i2 = (int)indices[tri * 3 + 2];
            if ((uint)i0 >= (uint)positions.Length || (uint)i1 >= (uint)positions.Length || (uint)i2 >= (uint)positions.Length)
                continue;

            if (MollerTrumbore(positions[i0], positions[i1], positions[i2], localOrigin, localDir, out float t) && t < bestT)
            {
                bestT = t;
                hit = true;
            }
        }

        return hit;
    }

    /// <summary>
    /// Moller-Trumbore intersection against one triangle in local space, with no back-face culling.
    /// Returns hit parameter t, where t≥0 means the hit lies in the forward ray direction.
    /// Cases with det≈0 are skipped, covering degenerate or collinear triangles as well as numerically thin faces.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool MollerTrumbore(Vector3 a, Vector3 b, Vector3 c, Vector3 rayOrigin, Vector3 rayDirection, out float t)
    {
        t = 0f;

        var edge1 = b - a;
        var edge2 = c - a;
        var pvec = Vector3.Cross(rayDirection, edge2);
        float det = Vector3.Dot(edge1, pvec);
        if (MathF.Abs(det) < 1e-12f)
            return false;

        float invDet = 1f / det;
        var tvec = rayOrigin - a;
        float u = Vector3.Dot(tvec, pvec) * invDet;
        if (u < 0f || u > 1f)
            return false;

        var qvec = Vector3.Cross(tvec, edge1);
        float v = Vector3.Dot(rayDirection, qvec) * invDet;
        if (v < 0f || u + v > 1f)
            return false;

        t = Vector3.Dot(edge2, qvec) * invDet;
        return t >= 0f;
    }

    /// <summary>
    /// Performs immediate skinning of vertices, used only in the narrow picking phase and only for hit candidates:
    /// p' = Σ w·Transform(p, bones[paletteOffset + ji]).
    /// bones is the current-frame palette from <see cref="GLTFAnimationPlayer.GetBoneMatricesArray"/>, already transposed under UpdateBoneMatrices semantics.
    /// Row-vector Transform is therefore bit-identical to the VS expression mul(localPosition, boneMatrix).
    /// The result stays in the skinned mesh's local space, the same domain as the shader intermediate value before the mesh world matrix is applied.
    /// paletteOffset comes from <see cref="GltfAsset.GetSkinPaletteOffset"/>, the prefix sum over GetAllSkins order.
    /// Out-of-range joint indices are skipped safely and contribute 0.
    /// dest must be at least positions.Length long.
    /// </summary>
    internal static void SkinPositions(ReadOnlySpan<Vector3> positions, ReadOnlySpan<Vector4> joints, ReadOnlySpan<Vector4> weights,
        ReadOnlySpan<Matrix4x4> bones, int paletteOffset, Span<Vector3> dest)
    {
        for (int i = 0; i < positions.Length; i++)
        {
            var p = Vector3.Zero;
            var ji = joints[i];
            var wt = weights[i];

            int bx = paletteOffset + (int)ji.X;
            if (wt.X > 0f && (uint)bx < (uint)bones.Length)
                p += wt.X * Vector3.Transform(positions[i], bones[bx]);
            bx = paletteOffset + (int)ji.Y;
            if (wt.Y > 0f && (uint)bx < (uint)bones.Length)
                p += wt.Y * Vector3.Transform(positions[i], bones[bx]);
            bx = paletteOffset + (int)ji.Z;
            if (wt.Z > 0f && (uint)bx < (uint)bones.Length)
                p += wt.Z * Vector3.Transform(positions[i], bones[bx]);
            bx = paletteOffset + (int)ji.W;
            if (wt.W > 0f && (uint)bx < (uint)bones.Length)
                p += wt.W * Vector3.Transform(positions[i], bones[bx]);

            dest[i] = p;
        }
    }

    /// <summary>
    /// World point → screen logical pixels, serving as the inverse of <see cref="ScreenPointToRay"/> and using the same RenderViewProjection, so round-tripping remains self-consistent.
    /// Useful for picking diagnostics and screen annotations.
    /// Returns false for points behind the camera, meaning clip.w≤0.
    /// </summary>
    public static bool ProjectToScreen(Vector3 worldPoint, Camera3D camera, float width, float height,
        out float screenX, out float screenY)
    {
        screenX = default;
        screenY = default;

        if (camera == null || width <= 0f || height <= 0f)
            return false;

        var viewProjection = camera.RenderViewProjection;
        if (viewProjection == Matrix4x4.Identity)
            return false;

        var clip = Vector4.Transform(new Vector4(worldPoint, 1f), viewProjection);
        if (clip.W <= 1e-6f)
            return false;

        float ndcX = clip.X / clip.W;
        float ndcY = clip.Y / clip.W;

        screenX = (ndcX + 1f) * 0.5f * width;
        screenY = (1f - ndcY) * 0.5f * height;

        screenX = screenX * DeviceServices.BaseApp.CompositionScale.X;
        screenY = screenY * DeviceServices.BaseApp.CompositionScale.Y;

        return true;
    }
}
