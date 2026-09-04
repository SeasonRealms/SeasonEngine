// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// Shared geometry helpers for unified highlighting, used across all four backends with no platform dependency and only static methods.
/// It covers shell construction through <see cref="AppendShellFace"/>, outline extraction through <see cref="AppendShellEdges"/>
/// by turning deduplicated triangle-mesh edges into flat quad strips, bounds-box geometry through <see cref="BuildBoxFaceVertices"/> and <see cref="BuildBoxEdgesVertices"/>,
/// and shell-thickness conversion through <see cref="NodeScaleOf"/> and <see cref="ComputeShellThickness"/>.
/// The original native implementation lived in DXPrimitiveGroup. DX now calls this class instead, preserving bit-identical behavior,
/// and VK/Metal/Web can reuse it directly.
/// Backend code is responsible only for GPU resource creation and draw orchestration, such as box and shell PrimitiveData, lazy construction, and WriteHighlightBox/DrawHighlightBox.
/// </summary>
internal static class HighlightGeometry
{
    /// <summary>Node scale used during shell baking, converting node-local space into the engine's local space.
    /// Under the B1 semantic, world edge width = baked local thickness × node scale × user scale.
    /// With uniform scale, the maximum length of the three columns of WorldTransform is exactly the scale s, since rotation does not change column lengths.
    /// With non-uniform scale, the maximum axis is used as a documented approximation.
    /// When there is no node, such as for procedural Mesh3D primitives, this always returns 1.</summary>
    internal static float NodeScaleOf(GltfNodeBase? node)
    {
        if (node == null)
            return 1f;
        var m = node.WorldTransform;
        float sx = MathF.Sqrt(m.M11 * m.M11 + m.M21 * m.M21 + m.M31 * m.M31);
        float sy = MathF.Sqrt(m.M12 * m.M12 + m.M22 * m.M22 + m.M32 * m.M32);
        float sz = MathF.Sqrt(m.M13 * m.M13 + m.M23 * m.M23 + m.M33 * m.M33);
        return MathF.Max(sx, MathF.Max(sy, sz));
    }

    /// <summary>Baked local thickness h = Highlight.EdgeWidth × the model's maximum local dimension / node scale, see <see cref="NodeScaleOf"/>.
    /// This makes world edge width approximately equal to edgeWidth × the model's maximum world dimension, consistent across assets.
    /// Total strip width is 2× h, and the shell's outward expansion thickness uses the same value.</summary>
    internal static float ComputeShellThickness(float edgeWidth, float localSizeMax, GltfNodeBase? node)
        => edgeWidth * localSizeMax / NodeScaleOf(node);

    /// <summary>Meaning of shell thickness:
    /// vertices are expanded **outward** along the normal by h.
    /// This creates a shell hugging the outside of the surface: the near-side shell sits closer to the camera than the model surface, passes the depth test,
    /// and appears as a translucent surface tint, while the far-side shell lies behind the back side of the model and is naturally hidden by the model's own depth.
    /// Insetting by -hN would place the shell entirely inside the model, where it would be completely occluded by the already rendered opaque surface, so outward expansion is mandatory.
    /// Concave regions visible from outside naturally reduce to edges only.
    /// Edge strips are flat quads attached to the expanded shell surface at +hN, with tangential width ±h and outward-facing normals, so they remain visible from outside.
    /// Here h is the baked local thickness, see <see cref="ComputeShellThickness"/>.
    /// Appending shell faces copies source triangles directly and expands their vertices outward by h, with indices offset by baseIndex.
    /// Full vertices are copied, including skin indices and weights, so skinned models stay perfectly attached under the same VS skinning.
    /// When sourceMap is not null, each output vertex records its source vertex index for expanding morph shell deltas in shell-vertex layout order.</summary>
    internal static void AppendShellFace(List<Vertex> vertices, List<uint> indices,
        IReadOnlyList<Vertex> sourceVertices, uint[] sourceIndices, float thickness, List<int>? sourceMap = null)
    {
        uint baseIndex = (uint)vertices.Count;
        for (int i = 0; i < sourceVertices.Count; i++)
        {
            var v = sourceVertices[i];
            v.Position += v.Normal * thickness;
            vertices.Add(v);
            sourceMap?.Add(i);
        }
        for (int i = 0; i < sourceIndices.Length; i++)
            indices.Add(baseIndex + sourceIndices[i]);
    }

    /// <summary>Appends shell edge strips.
    /// Each line segment, deduplicated by WireframeBuilder.BuildLineIndices, becomes a flat quad with 4 vertices:
    /// A and B are both expanded outward by h along their own normals, then widened by ±h along the tangent, producing 6 indices.
    /// Full source vertices are copied and carry skinning data with them.
    /// The tangent is cross(sum of endpoint normals, edge direction). Degenerate cases, such as zero normals, zero-length edges, or normals parallel to the edge, are skipped.
    /// When sourceMap is not null, source vertex indices are recorded in output order, A0/A1 → ia and B0/B1 → ib, for expanding morph shell deltas in shell-vertex layout order.</summary>
    internal static void AppendShellEdges(List<Vertex> vertices, List<uint> indices,
        IReadOnlyList<Vertex> sourceVertices, uint[] sourceIndices, float thickness, List<int>? sourceMap = null)
    {
        var lineIndices = WireframeBuilder.BuildLineIndices(sourceIndices, deduplicate: true);
        for (int i = 0; i < lineIndices.Length - 1; i += 2)
        {
            uint ia = lineIndices[i];
            uint ib = lineIndices[i + 1];
            if (ia >= (uint)sourceVertices.Count || ib >= (uint)sourceVertices.Count)
                continue;
            var va = sourceVertices[(int)ia];
            var vb = sourceVertices[(int)ib];

            var tangent = Vector3.Cross(va.Normal + vb.Normal, vb.Position - va.Position);
            if (tangent.LengthSquared() < 1e-12f)
                continue;
            tangent = Vector3.Normalize(tangent) * thickness;

            uint b = (uint)vertices.Count;
            va.Position += va.Normal * thickness;
            vb.Position += vb.Normal * thickness;
            var va0 = va;
            va0.Position -= tangent; vertices.Add(va0);   // A + hN - hT
            va.Position += tangent; vertices.Add(va);     // A + hN + hT
            vb.Position += tangent; vertices.Add(vb);     // B + hN + hT
            vb.Position -= tangent * 2f; vertices.Add(vb); // B + hN - hT
            if (sourceMap != null)
            {
                sourceMap.Add((int)ia);
                sourceMap.Add((int)ia);
                sourceMap.Add((int)ib);
                sourceMap.Add((int)ib);
            }
            indices.Add(b); indices.Add(b + 1); indices.Add(b + 2);
            indices.Add(b); indices.Add(b + 2); indices.Add(b + 3);
        }
    }

    /// <summary>The 8 corner vertices of the bounds-box faces in [-0.5,0.5]^3, with normals pointing radially from the corners.
    /// Unlit does not consume them, but they complete the vertex format.</summary>
    internal static List<Vertex> BuildBoxFaceVertices()
    {
        var vertices = new List<Vertex>(8);
        for (int i = 0; i < 8; i++)
        {
            var position = new Vector3((i & 1) - 0.5f, ((i >> 1) & 1) - 0.5f, ((i >> 2) & 1) - 0.5f);
            vertices.Add(new Vertex { Position = position, Normal = Vector3.Normalize(position) });
        }
        return vertices;
    }

    /// <summary>The 36 indices for the bounds-box faces, organized as six 4-corner rings with bit-encoded corner indices.
    /// Under DoubleSided rendering, winding order does not affect visibility.</summary>
    internal static List<uint> BuildBoxFaceIndices()
    {
        int[][] faces =
        {
            new[] { 0, 1, 3, 2 }, // z=-0.5
            new[] { 4, 6, 7, 5 }, // z=+0.5
            new[] { 0, 4, 5, 1 }, // y=-0.5
            new[] { 2, 3, 7, 6 }, // y=+0.5
            new[] { 0, 2, 6, 4 }, // x=-0.5
            new[] { 1, 5, 7, 3 }, // x=+0.5
        };

        var indices = new List<uint>(36);
        for (int f = 0; f < 6; f++)
        {
            indices.Add((uint)faces[f][0]);
            indices.Add((uint)faces[f][1]);
            indices.Add((uint)faces[f][2]);
            indices.Add((uint)faces[f][0]);
            indices.Add((uint)faces[f][2]);
            indices.Add((uint)faces[f][3]);
        }
        return indices;
    }

    /// <summary>Thin-box vertices for the 12 edges of the bounds box, 96 vertices total.
    /// Each strip extends one thickness beyond the corner along the edge direction so all 8 corners join seamlessly.</summary>
    internal static List<Vertex> BuildBoxEdgesVertices(List<uint> indices)
    {
        const float h = 0.015f; // Half thickness in the box-local [-0.5,0.5]^3 space; world thickness = h × the corresponding axis size.

        var vertices = new List<Vertex>(12 * 24);
        for (int axis = 0; axis < 3; axis++)
            for (int u = 0; u < 2; u++)
                for (int v = 0; v < 2; v++)
                {
                    var mn = new float[3];
                    var mx = new float[3];
                    for (int a = 0; a < 3; a++)
                    {
                        if (a == axis)
                        {
                            mn[a] = -0.5f - h;
                            mx[a] = 0.5f + h;
                        }
                        else
                        {
                            float c = (a == (axis + 1) % 3 ? u : v) - 0.5f;
                            mn[a] = c - h;
                            mx[a] = c + h;
                        }
                    }

                    AppendBoundsEdgeBox(vertices, indices, mn, mx);
                }

        return vertices;
    }

    /// <summary>Appends one axis-aligned thin box, using 6 faces × 4 independent vertices, with face-axis normals.
    /// Under DoubleSided rendering, winding order does not affect visibility.</summary>
    static void AppendBoundsEdgeBox(List<Vertex> vertices, List<uint> indices, float[] mn, float[] mx)
    {
        for (int f = 0; f < 3; f++)
        {
            int a1 = (f + 1) % 3, a2 = (f + 2) % 3;

            for (int s = 0; s < 2; s++)
            {
                var normal = new Vector3(
                    f == 0 ? (s == 0 ? -1f : 1f) : 0f,
                    f == 1 ? (s == 0 ? -1f : 1f) : 0f,
                    f == 2 ? (s == 0 ? -1f : 1f) : 0f);

                Vector3 Corner(int b1, int b2)
                {
                    var p = new float[3];
                    p[f] = s == 0 ? mn[f] : mx[f];
                    p[a1] = b1 == 0 ? mn[a1] : mx[a1];
                    p[a2] = b2 == 0 ? mn[a2] : mx[a2];
                    return new Vector3(p[0], p[1], p[2]);
                }

                uint b = (uint)vertices.Count;
                vertices.Add(new Vertex { Position = Corner(0, 0), Normal = normal });
                vertices.Add(new Vertex { Position = Corner(1, 0), Normal = normal });
                vertices.Add(new Vertex { Position = Corner(1, 1), Normal = normal });
                vertices.Add(new Vertex { Position = Corner(0, 1), Normal = normal });

                indices.Add(b);
                indices.Add(b + 1);
                indices.Add(b + 2);
                indices.Add(b);
                indices.Add(b + 2);
                indices.Add(b + 3);
            }
        }
    }
}
