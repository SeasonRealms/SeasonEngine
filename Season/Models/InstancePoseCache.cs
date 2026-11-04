// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Models;

/// <summary>
/// P1: CPU cache of the last sampled animation pose of an instanced model, shared by all four backends.
///
/// Every backend replays one clip per instance per frame into a single node tree, so a frame that skips an instance's
/// evaluation cannot leave that instance's pose in the tree -- the next instance overwrites it. This cache is what makes
/// throttled sampling possible: it keeps the three pose-derived artefacts the per-instance streams are built from, and a
/// frame that skips evaluation rebuilds the same streams from it.
///   NodeWorlds[instance][primitive]    the primitive's owner-node world at the last sample. The final per-instance world
///                                      is still nodeWorld x instanceWorld, so an instance keeps moving every frame even
///                                      when its pose is a few frames old.
///   MorphWeights[instance][primitive]  its morph weights, same source (the owner node's weights).
///   Bones[instance]                    its palette, laid out exactly as the GPU upload expects.
///
/// Rows are keyed by the control's <c>Instances</c> index rather than by the compacted instance slot. The two differ the
/// moment an instance is disabled, and the pose belongs to the instance, not to the slot it currently occupies; a cache
/// addressed by slot would hand an instance its neighbour's pose after every enable/disable change.
///
/// Growth preserves existing rows: growing the instance list only zero-fills the new tail, and the scheduler forces those
/// instances to sample before anything reads them. A different primitive count or bone stride is a load-time layout change,
/// so it rebuilds every row instead.
///
/// P3a: the cache is also what a deduplicated evaluation copies from. Two instances that ask for the same clip at the same
/// quantized playback time would evaluate to the same pose, so the second one takes the first one's finished rows through
/// <see cref="CopyRow"/> instead of replaying the clip. That only ever happens after the source has been sampled in the same
/// frame, so the rows being copied are this frame's values, not the source's previous pose.
/// </summary>
internal sealed class InstancePoseCache
{
    /// <summary>Owner-node world per instance per primitive (see the class header for the indexing contract).</summary>
    public Matrix4x4[][] NodeWorlds = Array.Empty<Matrix4x4[]>();

    /// <summary>Morph weights per instance per primitive.</summary>
    public Vector4[][] MorphWeights = Array.Empty<Vector4[]>();

    /// <summary>Bone palette per instance, null when the model is not skinned.</summary>
    public Matrix4x4[][] Bones = Array.Empty<Matrix4x4[]>();

    int _primitiveCount = -1;
    int _boneStride = -1;

    /// <summary>
    /// Makes the cache cover <paramref name="instanceCount"/> instances with the given per-primitive and per-palette
    /// shapes. Cheap after the first call: one length comparison per dimension.
    /// </summary>
    public void Ensure(int instanceCount, int primitiveCount, int boneStride)
    {
        if (_primitiveCount == primitiveCount && _boneStride == boneStride && NodeWorlds.Length >= instanceCount)
            return;

        bool layoutChanged = _primitiveCount != primitiveCount || _boneStride != boneStride;
        int allocateFrom = layoutChanged ? 0 : NodeWorlds.Length;

        if (layoutChanged)
        {
            NodeWorlds = instanceCount > 0 ? new Matrix4x4[instanceCount][] : Array.Empty<Matrix4x4[]>();
            MorphWeights = instanceCount > 0 ? new Vector4[instanceCount][] : Array.Empty<Vector4[]>();
            Bones = instanceCount > 0 ? new Matrix4x4[instanceCount][] : Array.Empty<Matrix4x4[]>();
        }
        else if (NodeWorlds.Length < instanceCount)
        {
            Array.Resize(ref NodeWorlds, instanceCount);
            Array.Resize(ref MorphWeights, instanceCount);
            Array.Resize(ref Bones, instanceCount);
        }

        for (int i = allocateFrom; i < instanceCount; i++)
        {
            if (primitiveCount > 0)
            {
                NodeWorlds[i] = new Matrix4x4[primitiveCount];
                MorphWeights[i] = new Vector4[primitiveCount];
            }

            if (boneStride > 0)
                Bones[i] = new Matrix4x4[boneStride];
        }

        _primitiveCount = primitiveCount;
        _boneStride = boneStride;
    }

    /// <summary>
    /// P3a: copies every cached row of <paramref name="source"/> onto <paramref name="destination"/>. Used by a deduplicated
    /// evaluation: the destination's clip/time pair matched one already replayed this frame, so its pose is the source's pose.
    /// Rows are keyed by the Instances index like the rest of the cache, and both rows must already exist (the scheduler
    /// registered the source only after <see cref="Ensure"/> raised it to the same shape).
    /// </summary>
    public void CopyRow(int source, int destination)
    {
        if (source == destination) return;
        if ((uint)source >= (uint)NodeWorlds.Length || (uint)destination >= (uint)NodeWorlds.Length) return;

        CopyRow(NodeWorlds, source, destination);
        CopyRow(MorphWeights, source, destination);
        CopyRow(Bones, source, destination);
    }

    static void CopyRow<T>(T[][] rows, int source, int destination)
    {
        if ((uint)source >= (uint)rows.Length || (uint)destination >= (uint)rows.Length) return;
        var from = rows[source];
        var to = rows[destination];
        if (from == null || to == null) return;
        Array.Copy(from, to, Math.Min(from.Length, to.Length));
    }
}
