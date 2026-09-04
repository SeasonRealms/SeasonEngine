// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// 1-3: Axis-aligned bounding box in center/extents form, used as the shared culling primitive across all four backends.
/// It uses center+extents instead of min/max because frustum-plane testing only needs one projected radius via dot(|N|, e),
/// and RH→LH conversion reduces to negating Center.Z (extents remain symmetric, matching the rule that imported glTF vertex Z is negated).
/// The struct is always passed by value or embedded in object fields, with no heap allocation.
/// </summary>
public struct Bounds3D
{
    /// <summary>Box center, in either local or world space depending on the caller's semantics.</summary>
    public Vector3 Center;

    /// <summary>Half-size along each axis, always non-negative. Zero means a degenerate point, used as the fallback for empty geometry.</summary>
    public Vector3 Extents;

    public Bounds3D(Vector3 center, Vector3 extents)
    {
        Center = center;
        Extents = extents;
    }

    /// <summary>Constructs from min/max: center=(min+max)/2, extents=(max-min)/2.</summary>
    public static Bounds3D FromMinMax(Vector3 min, Vector3 max)
    {
        return new Bounds3D((min + max) * 0.5f, (max - min) * 0.5f);
    }

    /// <summary>Computes a local AABB from a vertex array. Empty input returns the zero box. Used during loading, not per frame.</summary>
    public static Bounds3D FromVertices(ReadOnlySpan<Season.Basic.Vertex> vertices)
    {
        if (vertices.Length == 0)
            return default;

        var min = vertices[0].Position;
        var max = min;
        for (int i = 1; i < vertices.Length; i++)
        {
            var p = vertices[i].Position;
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        return FromMinMax(min, max);
    }

    /// <summary>List overload: converts to Span through CollectionsMarshal with zero copy. Used during loading.</summary>
    public static Bounds3D FromVertices(List<Season.Basic.Vertex> vertices)
    {
        if (vertices == null || vertices.Count == 0)
            return default;

        return FromVertices((ReadOnlySpan<Season.Basic.Vertex>)
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(vertices));
    }

    /// <summary>Array overload, used during loading.</summary>
    public static Bounds3D FromVertices(Season.Basic.Vertex[] vertices)
    {
        if (vertices == null)
            return default;

        return FromVertices((ReadOnlySpan<Season.Basic.Vertex>)vertices);
    }

    /// <summary>
    /// Local AABB → world AABB using the absolute-matrix method |M|, without transforming all 8 corners.
    /// Under the System.Numerics row-vector convention: worldCenter = center·M;
    /// worldExtents = |row0|·e.X + |row1|·e.Y + |row2|·e.Z, where row_i is the 3D part of row i in M.
    /// The result is the tightest axis-aligned bound of the box after applying M, slightly conservative under rotation, which is appropriate for culling.
    /// </summary>
    public readonly Bounds3D Transform(in Matrix4x4 m)
    {
        var worldCenter = Vector3.Transform(Center, m);
        var absRow0 = Vector3.Abs(new Vector3(m.M11, m.M12, m.M13));
        var absRow1 = Vector3.Abs(new Vector3(m.M21, m.M22, m.M23));
        var absRow2 = Vector3.Abs(new Vector3(m.M31, m.M32, m.M33));
        var worldExtents = absRow0 * Extents.X + absRow1 * Extents.Y + absRow2 * Extents.Z;
        return new Bounds3D(worldCenter, worldExtents);
    }

    /// <summary>Keeps the center fixed and scales extents uniformly, used for conservative bounds on animated models via RenderQuality.AnimatedBoundsScale.</summary>
    public readonly Bounds3D Scaled(float factor)
    {
        return new Bounds3D(Center, Extents * factor);
    }

    /// <summary>Union of two boxes, used for aggregating control-level bounds across multiple surfaces or primitives.</summary>
    public static Bounds3D Union(in Bounds3D a, in Bounds3D b)
    {
        var min = Vector3.Min(a.Center - a.Extents, b.Center - b.Extents);
        var max = Vector3.Max(a.Center + a.Extents, b.Center + b.Extents);
        return FromMinMax(min, max);
    }

    /// <summary>Radius of the enclosing sphere, useful for quick sphere-based rejection tests.</summary>
    public readonly float SphereRadius => Extents.Length();
}
