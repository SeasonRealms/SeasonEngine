// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Drawables;

/// <summary>
/// The CPU half of GPU morph-target blending. Both functions here are pure: they read a glTF morph target list
/// and produce bytes, nothing else. Uploading those bytes is the backend's job, because the buffer type and the
/// upload path differ (DX needs an upload heap plus an SRV, VK needs host-visible memory, Metal writes Contents).
///
/// The layout produced by <see cref="Pack"/> is read by the vertex shader in all four backends:
/// <c>[targetIndex * vertexCount + vertexIndex] * 9 + {0..2}</c> is the position delta,
/// <c>+ {3..5}</c> the normal delta, and <c>+ {6..8}</c> the tangent delta.
/// </summary>
internal static class MorphDelta
{
    /// <summary>Position, normal, and tangent, three floats each.</summary>
    internal const int FloatsPerVertex = 9;

    /// <summary>
    /// Packs every target's deltas into the single flat buffer the shaders index into.
    ///
    /// <paramref name="vertexMap"/> selects the destination vertex layout: when it is non-null the output is
    /// expanded through the map, so destination vertex v takes its delta from source vertex
    /// <c>vertexMap[v]</c> and the output vertex count is <c>vertexMap.Count</c>. That is how a shell primitive
    /// reuses the source primitive's deltas under its own vertex order. When it is null the mapping is identity
    /// and the output vertex count is <paramref name="baseVertices"/>.Length.
    ///
    /// A vertex whose source index falls outside a target's delta array contributes zeros for that channel
    /// rather than throwing: glTF targets are sparse by design, and dropping them would shift every later
    /// vertex. <paramref name="baseVertices"/> is only read for its length and may be null when a map is given.
    /// </summary>
    internal static float[] Pack(Vertex[]? baseVertices, List<GLTFMorphTarget> morphTargets,
        IReadOnlyList<int>? vertexMap = null)
    {
        int vertexCount = vertexMap != null ? vertexMap.Count : baseVertices!.Length;
        int targetCount = morphTargets.Count;
        var deltaData = new float[targetCount * vertexCount * FloatsPerVertex];

        for (int t = 0; t < targetCount; t++)
        {
            var target = morphTargets[t];
            int targetBase = t * vertexCount * FloatsPerVertex;
            for (int v = 0; v < vertexCount; v++)
            {
                int srcIdx = vertexMap != null ? vertexMap[v] : v;
                int baseIdx = targetBase + v * FloatsPerVertex;
                if (srcIdx < target.PositionDeltas.Length)
                {
                    deltaData[baseIdx] = target.PositionDeltas[srcIdx].X;
                    deltaData[baseIdx + 1] = target.PositionDeltas[srcIdx].Y;
                    deltaData[baseIdx + 2] = target.PositionDeltas[srcIdx].Z;
                }
                if (srcIdx < target.NormalDeltas.Length)
                {
                    deltaData[baseIdx + 3] = target.NormalDeltas[srcIdx].X;
                    deltaData[baseIdx + 4] = target.NormalDeltas[srcIdx].Y;
                    deltaData[baseIdx + 5] = target.NormalDeltas[srcIdx].Z;
                }
                if (srcIdx < target.TangentDeltas.Length)
                {
                    deltaData[baseIdx + 6] = target.TangentDeltas[srcIdx].X;
                    deltaData[baseIdx + 7] = target.TangentDeltas[srcIdx].Y;
                    deltaData[baseIdx + 8] = target.TangentDeltas[srcIdx].Z;
                }
            }
        }

        return deltaData;
    }

    /// <summary>
    /// Takes the first four node weights, which is the limit the vertex shaders accumulate over
    /// (<c>morphTargetCount &lt; 4u</c>). A node with no weights, or no node at all, yields zero.
    /// </summary>
    internal static Vector4 ExtractWeights(GltfNodeBase? node)
    {
        if (node == null || node.Weights.Length == 0)
            return Vector4.Zero;

        var weights = node.Weights;
        return new Vector4(
            weights.Length > 0 ? weights[0] : 0f,
            weights.Length > 1 ? weights[1] : 0f,
            weights.Length > 2 ? weights[2] : 0f,
            weights.Length > 3 ? weights[3] : 0f);
    }
}
