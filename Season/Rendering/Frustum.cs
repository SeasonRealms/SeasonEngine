// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// 1-3: Frustum, represented by 6 planes extracted from View×Projection using the Gribb-Hartmann method, shared across all four backends.
///
/// Preconditions, see RenderQuality 1-3 clause 1:
/// all four backends build View/Projection on the shared C# side through System.Numerics using LH + [0,1] depth matrices.
/// Differences such as Vulkan Y-flip happen only after clip space, on the shader or viewport side,
/// so one CPU-side frustum extraction and plane-test implementation works across all four backends.
///
/// Under the row-vector convention, clip = v·VP, planes are assembled from matrix "columns". Let c_i be column i of VP:
///   left = c4+c1, right = c4-c1, bottom = c4+c2, top = c4-c2,
///   near = c3 for [0,1] depth, where z≥0 means the point is inside the near plane, and far = c4-c3.
/// Each plane stores normalized (Normal, D), with the inside test dot(N,p)+D ≥ 0, and also caches |Normal| for AABB projected-radius tests.
/// The struct inlines 24 float fields with no heap allocation, and it is rebuilt only when the camera is dirty under Camera3D gating.
/// </summary>
public struct Frustum
{
    // 6 planes × (Normal, D, AbsNormal), ordered as near, far, left, right, bottom, top.
    // Near/far reject most often during camera translation, so putting them first improves early-out hits.
    Vector3 _n0; float _d0; Vector3 _a0;
    Vector3 _n1; float _d1; Vector3 _a1;
    Vector3 _n2; float _d2; Vector3 _a2;
    Vector3 _n3; float _d3; Vector3 _a3;
    Vector3 _n4; float _d4; Vector3 _a4;
    Vector3 _n5; float _d5; Vector3 _a5;

    /// <summary>
    /// Extracts the 6 planes from a view×projection matrix under the row-vector convention with LH + [0,1] depth.
    /// The out form avoids copying a large struct through a temporary return value.
    /// </summary>
    public static void FromViewProjection(in Matrix4x4 vp, out Frustum frustum)
    {
        frustum = default;

        // Column vectors. Under the row-vector convention, plane coefficients come from matrix columns.
        var c1 = new Vector4(vp.M11, vp.M21, vp.M31, vp.M41);
        var c2 = new Vector4(vp.M12, vp.M22, vp.M32, vp.M42);
        var c3 = new Vector4(vp.M13, vp.M23, vp.M33, vp.M43);
        var c4 = new Vector4(vp.M14, vp.M24, vp.M34, vp.M44);

        SetPlane(ref frustum._n0, ref frustum._d0, ref frustum._a0, c3);       // near: z ≥ 0
        SetPlane(ref frustum._n1, ref frustum._d1, ref frustum._a1, c4 - c3);  // far:  w-z ≥ 0
        SetPlane(ref frustum._n2, ref frustum._d2, ref frustum._a2, c4 + c1);  // left
        SetPlane(ref frustum._n3, ref frustum._d3, ref frustum._a3, c4 - c1);  // right
        SetPlane(ref frustum._n4, ref frustum._d4, ref frustum._a4, c4 + c2);  // bottom
        SetPlane(ref frustum._n5, ref frustum._d5, ref frustum._a5, c4 - c2);  // top
    }

    static void SetPlane(ref Vector3 n, ref float d, ref Vector3 abs, Vector4 coeffs)
    {
        var normal = new Vector3(coeffs.X, coeffs.Y, coeffs.Z);
        float invLen = 1f / normal.Length();
        n = normal * invLen;
        d = coeffs.W * invLen;
        abs = Vector3.Abs(n);
    }

    /// <summary>
    /// Intersects test between a world-space AABB in center/extents form and the frustum.
    /// Per plane: signed distance = dot(N,c)+D, projected radius r = dot(|N|,e).
    /// If dist + r &lt; 0, the whole box lies outside that plane and can be rejected with false.
    /// This is a conservative test: returning true may include a few false positives outside the corners, which is harmless for culling.
    /// Zero allocation, no loop, and per-plane early-out.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Intersects(in Vector3 center, in Vector3 extents)
    {
        if (Vector3.Dot(_n0, center) + _d0 + Vector3.Dot(_a0, extents) < 0) return false;
        if (Vector3.Dot(_n1, center) + _d1 + Vector3.Dot(_a1, extents) < 0) return false;
        if (Vector3.Dot(_n2, center) + _d2 + Vector3.Dot(_a2, extents) < 0) return false;
        if (Vector3.Dot(_n3, center) + _d3 + Vector3.Dot(_a3, extents) < 0) return false;
        if (Vector3.Dot(_n4, center) + _d4 + Vector3.Dot(_a4, extents) < 0) return false;
        if (Vector3.Dot(_n5, center) + _d5 + Vector3.Dot(_a5, extents) < 0) return false;
        return true;
    }

    /// <summary>Convenience overload for Bounds3D.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Intersects(in Bounds3D bounds)
    {
        return Intersects(in bounds.Center, in bounds.Extents);
    }

    /// <summary>Intersects test between a world-space sphere and the frustum, useful as a fast instance-level filter with radius = Bounds3D.SphereRadius.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IntersectsSphere(in Vector3 center, float radius)
    {
        if (Vector3.Dot(_n0, center) + _d0 + radius < 0) return false;
        if (Vector3.Dot(_n1, center) + _d1 + radius < 0) return false;
        if (Vector3.Dot(_n2, center) + _d2 + radius < 0) return false;
        if (Vector3.Dot(_n3, center) + _d3 + radius < 0) return false;
        if (Vector3.Dot(_n4, center) + _d4 + radius < 0) return false;
        if (Vector3.Dot(_n5, center) + _d5 + radius < 0) return false;
        return true;
    }
}
