// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Models;

/// <summary>
/// The per-instance half of shared-template instancing: a template <see cref="GltfAsset"/> is loaded once, and every
/// instance that plays its own clips needs its own copy of the node tree, the skins and the clips, plus a rest-pose
/// snapshot so that copy can be reset before each instance's pose is evaluated.
///
/// This lived in all three native instanced models — <c>DXInstancedModel</c>, <c>VKInstancedModel</c> and
/// <c>MTLInstancedModel</c> — as <c>CloneAnimationData</c>, <c>SaveRestPoseSnapshot</c> and <c>RestoreNodesToRestPose</c>.
/// The bodies were identical apart from how each backend reached the template asset, and none of them touches a device,
/// a buffer or a pipeline: they build managed nodes, skins, float arrays and <see cref="GLTFAnimation"/> copies. Data
/// that never sees a graphics API belongs next to the data model, not behind three copies of itself.
/// </summary>
internal static class GltfInstanceData
{
    /// <summary>
    /// Deep-copies <paramref name="template"/>'s nodes and skins into <paramref name="target"/>, re-points the clips at
    /// the copies through <paramref name="nodeMap"/>, and leaves the map complete so the caller's primitive pass can
    /// remap <c>OwnerNode</c> with the same keys.
    ///
    /// <c>Mesh</c> and <c>PickMeshes</c> are shared by reference on purpose: both are immutable after loading, and the
    /// pick mesh indices only make sense against the shared copy.
    /// </summary>
    internal static void CloneTemplateForInstance(
        GltfAsset template, GltfAsset target, Dictionary<GltfNodeBase, GltfNodeBase> nodeMap)
    {
        CloneNodes(template, target, nodeMap);
        CloneSkins(template, nodeMap);
        target._animations = AnimationOps.CloneAnimations(template._animations, nodeMap);
    }

    /// <summary>
    /// Copies the transform state of every template node — transforms, initial rest values, morph weights and joint
    /// flags — and rebuilds the parent-child links among the copies. Nothing here walks the mesh data: the copies are
    /// skeletons and poses, not geometry.
    /// </summary>
    static void CloneNodes(
        GltfAsset template, GltfAsset target, Dictionary<GltfNodeBase, GltfNodeBase> nodeMap)
    {
        foreach (var sourceNode in template.gltfNodes)
        {
            var clone = new GltfNodeBase
            {
                Name = sourceNode.Name,
                LogicalIndex = sourceNode.LogicalIndex,
                Mesh = sourceNode.Mesh,
                Translation = sourceNode.Translation,
                Rotation = sourceNode.Rotation,
                Scale = sourceNode.Scale,
                InitialTranslation = sourceNode.InitialTranslation,
                InitialRotation = sourceNode.InitialRotation,
                InitialScale = sourceNode.InitialScale,
                Weights = sourceNode.Weights.ToArray(),
                InitialWeights = sourceNode.InitialWeights.ToArray(),
                WeightsVersion = sourceNode.WeightsVersion,
                IsJoint = sourceNode.IsJoint,
                JointIndex = sourceNode.JointIndex,
                WorldTransform = sourceNode.WorldTransform,
                PickMeshes = sourceNode.PickMeshes,
            };
            nodeMap[sourceNode] = clone;
            target.gltfNodes.Add(clone);
        }

        foreach (var sourceNode in template.gltfNodes)
        {
            var clone = nodeMap[sourceNode];
            foreach (var child in sourceNode.Children)
            {
                if (nodeMap.TryGetValue(child, out var clonedChild))
                    clone.Children.Add(clonedChild);
            }
        }
    }

    /// <summary>
    /// Copies every skin and re-binds its joints to the cloned nodes. The per-instance matrix replaces the mesh node's
    /// world transform (b0 world is identity), yet a joint's world transform still carries shared-ancestor scaling such
    /// as 0.01; the inverse bind node world cancels it. <c>BindNode</c> therefore falls back to the skeleton root, then
    /// to the first joint, so that bone = IBM x joint.WT x inverse(root.WT) = identity at rest.
    /// </summary>
    static void CloneSkins(GltfAsset template, Dictionary<GltfNodeBase, GltfNodeBase> nodeMap)
    {
        var skinMap = new Dictionary<GLTFSkin, GLTFSkin>();
        foreach (var sourceSkin in template.GetAllSkins())
        {
            var clonedSkin = new GLTFSkin
            {
                Name = sourceSkin.Name,
                InverseBindMatrices = new List<Matrix4x4>(sourceSkin.InverseBindMatrices),
                Joints = new List<GltfNodeBase>(),
            };
            skinMap[sourceSkin] = clonedSkin;

            foreach (var joint in sourceSkin.Joints)
            {
                if (nodeMap.TryGetValue(joint, out var clonedJoint))
                {
                    clonedSkin.Joints.Add(clonedJoint);
                    clonedJoint.Skin = clonedSkin;
                }
            }

            if (sourceSkin.SkeletonRoot != null && nodeMap.TryGetValue(sourceSkin.SkeletonRoot, out var clonedRoot))
                clonedSkin.SkeletonRoot = clonedRoot;

            if (sourceSkin.BindNode != null && nodeMap.TryGetValue(sourceSkin.BindNode, out var clonedBindNode))
                clonedSkin.BindNode = clonedBindNode;
            else
                clonedSkin.BindNode = clonedSkin.SkeletonRoot ?? (clonedSkin.Joints.Count > 0 ? clonedSkin.Joints[0] : null);
        }

        foreach (var sourceNode in template.gltfNodes)
        {
            if (sourceNode.Skin != null
                && nodeMap.TryGetValue(sourceNode, out var clonedNode)
                && skinMap.TryGetValue(sourceNode.Skin, out var clonedSkin))
            {
                clonedNode.Skin = clonedSkin;
            }
        }
    }

    /// <summary>
    /// Captures each node's rest pose — the loader's initial TRS plus a private copy of the initial morph weights — so
    /// the instance can be rewound to it before the next pose is evaluated. The weight array is copied rather than
    /// shared because evaluation writes into it in place.
    /// </summary>
    internal static (Vector3, Quaternion, Vector3, float[])[] CaptureRestPose(List<GltfNodeBase> nodes)
    {
        var snapshot = new (Vector3, Quaternion, Vector3, float[])[nodes.Count];
        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            snapshot[i] = (
                node.InitialTranslation,
                node.InitialRotation,
                node.InitialScale,
                node.InitialWeights.Length > 0 ? (float[])node.InitialWeights.Clone() : Array.Empty<float>()
            );
        }

        return snapshot;
    }

    /// <summary>
    /// Rewinds <paramref name="nodes"/> to <paramref name="snapshot"/>. The weight buffer is reused when its length
    /// still matches, which is the common case: morph target counts do not change between two poses of one model.
    /// </summary>
    internal static void RestoreRestPose(
        IReadOnlyList<GltfNodeBase> nodes, (Vector3, Quaternion, Vector3, float[])[] snapshot)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            var snap = snapshot[i];
            node.Translation = snap.Item1;
            node.Rotation = snap.Item2;
            node.Scale = snap.Item3;
            node.WeightsVersion = 0;

            if (snap.Item4.Length == 0)
            {
                node.Weights = Array.Empty<float>();
                continue;
            }

            if (node.Weights.Length != snap.Item4.Length)
                node.Weights = (float[])snap.Item4.Clone();
            else
                Array.Copy(snap.Item4, node.Weights, snap.Item4.Length);
        }
    }
}
