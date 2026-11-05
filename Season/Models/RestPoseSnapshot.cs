// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Numerics;
using SharpGLTF.Runtime;

namespace Season.Models;

/// <summary>
/// The pose a skin's inverse bind matrices were built against, captured once per model and restored whenever the CPU route hands
/// the tree to the GPU one.
///
/// <para>
/// This exists because the single-model GPU route multiplies two matrices that have to cancel: the palette the kernel writes
/// carries <c>inverse(rest world)</c>, and the per-primitive factor the vertex stage applies is that same rest world. They cancel
/// only when both sides are the same value, so the snapshot has to be computed the way the bake computes it - local TRS from the
/// initial values, walked up the parent chain - rather than read off the live transform. Reading the live transform would make the
/// answer depend on when this runs and on whether something already evaluated the tree; following the bake's own walk makes it
/// depend on nothing.
/// </para>
///
/// <para>
/// Shared by all four backends for the same reason as the bake itself: getting it subtly wrong renders as a plausible wrong pose
/// rather than as an error, so there should be exactly one copy.
/// </para>
/// </summary>
internal sealed class RestPoseSnapshot
{
    /// <summary>Rest world of every node, indexed exactly like <c>asset.gltfNodes</c>. Empty until <see cref="Capture"/> runs, and
    /// empty again if the asset has no nodes.</summary>
    public Matrix4x4[] Worlds { get; private set; } = Array.Empty<Matrix4x4>();

    /// <summary>
    /// Snapshots every node's rest world. Cheap and allocation-only, so it runs at load and wherever the route is latched for the
    /// first time; the result never changes for the life of the asset.
    /// </summary>
    public bool Capture(GltfAsset asset)
    {
        var nodes = asset?.gltfNodes;
        if (nodes == null)
        {
            Worlds = Array.Empty<Matrix4x4>();
            return false;
        }

        var rest = new Matrix4x4[nodes.Count];
        Worlds = rest;
        if (nodes.Count == 0) return true;

        // Parent links come from the child lists; glTF has no parent pointer. Same construction as the bake's, because the walk
        // below assumes it.
        var parentOf = new Dictionary<GltfNodeBase, GltfNodeBase>(nodes.Count);
        for (int i = 0; i < nodes.Count; i++)
        {
            var children = nodes[i]?.Children;
            if (children == null) continue;
            for (int c = 0; c < children.Count; c++)
                if (children[c] != null) parentOf[children[c]] = nodes[i];
        }

        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (node == null) continue;

            // Up the parent chain, not forward over the list: glTF does not require a parent to precede its children, and a
            // forward pass would silently drop the ancestor chain of every node listed before its parent.
            var world = RestLocalOf(node);
            var cursor = parentOf.TryGetValue(node, out var parent) ? parent : null;
            while (cursor != null)
            {
                world = world * RestLocalOf(cursor);
                cursor = parentOf.TryGetValue(cursor, out var up) ? up : null;
            }
            rest[i] = world;
        }

        return true;
    }

    /// <summary>
    /// Puts every node back to the snapshot. The GPU route's palette carries inverse(rest world), so this is the matrix the vertex
    /// stage has to multiply by; a tree left in any other pose applies the skin on top of the pose it is already in, which reads as
    /// a limb attached at the wrong place. The TRS triple is written alongside the world matrix because rest is a property of the
    /// triple, not of the cached product: leaving an evaluated triple behind would read back as animated the first time anything
    /// recomposes the tree.
    ///
    /// Returns false without touching anything when no snapshot lines up with the asset, which is the state a model that never
    /// entered the GPU route stays in.
    /// </summary>
    public bool Restore(GltfAsset asset)
    {
        var rest = Worlds;
        var nodes = asset?.gltfNodes;
        if (nodes == null || rest.Length != nodes.Count) return false;

        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (node == null) continue;
            node.Translation = node.InitialTranslation;
            node.Rotation = node.InitialRotation;
            node.Scale = node.InitialScale;
            node.WorldTransform = rest[i];
        }

        return true;
    }

    /// <summary>The local matrix the rest pose's TRS triple composes to, in the same multiplication order the animation player
    /// uses for an animated frame.</summary>
    public static Matrix4x4 RestLocalOf(GltfNodeBase node)
        => Matrix4x4.CreateScale(node.InitialScale)
         * Matrix4x4.CreateFromQuaternion(node.InitialRotation)
         * Matrix4x4.CreateTranslation(node.InitialTranslation);
}
