// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Utils;

/// <summary>
/// Wireframe edge-index generator used as a shared pure function for highlight-shell geometry.
/// It takes triangle indices and outputs line-segment index pairs, with three edges per triangle for six indices total.
/// The lines share the same vertex buffer as the surface, so skinning, deformation, and instancing stay exactly aligned with the surface and remain tightly matched during animation.
/// </summary>
internal static class WireframeBuilder
{
    /// <summary>
    /// Build wireframe line indices.
    /// </summary>
    /// <param name="triangleIndices">Triangle index array. The length should be a multiple of 3; any incomplete tail is discarded automatically.</param>
    /// <param name="deduplicate">
    /// True removes interior edges shared by two or more triangles, keeping only one line segment for each (min, max) vertex pair,
    /// so only the mesh outline and non-manifold boundaries are drawn.
    /// False, the default, emits all three edges per triangle, matching mainstream engine wireframe debug views where quad faces show the triangulation diagonal.
    /// </param>
    /// <returns>Line indices. Returns an empty array for null or invalid input.</returns>
    public static uint[] BuildLineIndices(uint[]? triangleIndices, bool deduplicate = false)
    {
        if (triangleIndices == null || triangleIndices.Length < 3)
            return Array.Empty<uint>();

        if (!deduplicate)
        {
            var lines = new uint[triangleIndices.Length * 2];
            int w = 0;
            for (int i = 0; i + 2 < triangleIndices.Length; i += 3)
            {
                lines[w++] = triangleIndices[i];
                lines[w++] = triangleIndices[i + 1];
                lines[w++] = triangleIndices[i + 1];
                lines[w++] = triangleIndices[i + 2];
                lines[w++] = triangleIndices[i + 2];
                lines[w++] = triangleIndices[i];
            }
            return lines;
        }

        var seen = new HashSet<(uint, uint)>(triangleIndices.Length);
        var dedup = new List<uint>(triangleIndices.Length);
        void AddEdge(uint a, uint b)
        {
            var key = a < b ? (a, b) : (b, a);
            if (seen.Add(key))
            {
                dedup.Add(a);
                dedup.Add(b);
            }
        }

        for (int i = 0; i + 2 < triangleIndices.Length; i += 3)
        {
            AddEdge(triangleIndices[i], triangleIndices[i + 1]);
            AddEdge(triangleIndices[i + 1], triangleIndices[i + 2]);
            AddEdge(triangleIndices[i + 2], triangleIndices[i]);
        }
        return dedup.ToArray();
    }
}
