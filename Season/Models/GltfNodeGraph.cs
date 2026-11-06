// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Models;

/// <summary>
/// The shared-template clone of a node graph: one template asset is loaded once, and every control that asks for its own
/// instance gets a copy of the nodes, skins and clips, so two controls can be posed independently while still sharing
/// the template's geometry, textures and bake tables.
///
/// This body lived four times - in <c>DXModel</c>, <c>VKModel</c>, <c>MTLModel</c> and <c>WGPUModel</c> - and the copies
/// had already drifted: two of them never copied <see cref="GltfNodeBase.PickMeshes"/>, which quietly downgraded picking
/// on those platforms from triangle-exact to the bounding-volume fallback. Nothing here touches a device; the only
/// backend-specific step is "build this node's clone and the primitives it owns", which is what <c>createNode</c>
/// supplies. That keeps the walk next to the data model, the same argument that placed
/// <see cref="GltfInstanceData"/> here.
/// </summary>
internal static class GltfNodeGraph
{
    /// <summary>
    /// Builds <paramref name="target"/>'s own copy of <paramref name="template"/>'s node tree, skins and clips.
    /// <paramref name="createNode"/> creates one node clone - the loader identity plus its primitives, with the
    /// requesting control's overrides folded in - and is called at most once per source node.
    ///
    /// The pass order is load-bearing. Every node a skin or a channel names has to exist before the children pass can
    /// link it, and the skin links are rewired last, once every skin copy exists. A primitive is created together with
    /// its node, so the <c>IsSkinned</c> it carries comes from a node whose skin is still null; every backend recomputes
    /// that from the finished graph right after this call.
    ///
    /// The player is initialized on the copies and left on clip 0, which is the state the direct-load path hands a
    /// control: an instance starts out playing the default clip without anyone asking for it.
    /// </summary>
    internal static void Clone(
        GltfAsset template,
        GltfAsset target,
        Season.Controls.Model model,
        Func<GltfNodeBase, Season.Controls.Model, GltfNodeBase> createNode)
    {
        var nodeMap = new Dictionary<GltfNodeBase, GltfNodeBase>();

        GltfNodeBase Ensure(GltfNodeBase sourceNode)
        {
            if (nodeMap.TryGetValue(sourceNode, out var existing))
                return existing;

            var cloned = createNode(sourceNode, model);
            nodeMap[sourceNode] = cloned;
            return cloned;
        }

        foreach (var nodeBase in template.gltfNodes)
            Ensure(nodeBase);

        var skinMap = new Dictionary<GLTFSkin, GLTFSkin>();
        foreach (var sourceSkin in template.GetAllSkins())
        {
            skinMap[sourceSkin] = new GLTFSkin
            {
                Name = sourceSkin.Name,
                InverseBindMatrices = new List<Matrix4x4>(sourceSkin.InverseBindMatrices),
                Joints = new List<GltfNodeBase>(),
            };

            foreach (var joint in sourceSkin.Joints)
                Ensure(joint);

            if (sourceSkin.SkeletonRoot != null)
                Ensure(sourceSkin.SkeletonRoot);

            if (sourceSkin.BindNode != null)
                Ensure(sourceSkin.BindNode);
        }

        foreach (var animation in template._animations)
        {
            foreach (var channel in animation.Channels)
            {
                if (channel.Target?.Node != null)
                    Ensure(channel.Target.Node);
            }
        }

        foreach (var sourceNode in template.gltfNodes)
        {
            var clonedNode = nodeMap[sourceNode];
            clonedNode.Children = sourceNode.Children.Select(Ensure).ToList();

            if (sourceNode.Skin != null && skinMap.TryGetValue(sourceNode.Skin, out var clonedSkin))
                clonedNode.Skin = clonedSkin;
        }

        foreach (var pair in skinMap)
        {
            var sourceSkin = pair.Key;
            var clonedSkin = pair.Value;
            clonedSkin.Joints = sourceSkin.Joints.Select(Ensure).ToList();
            clonedSkin.SkeletonRoot = sourceSkin.SkeletonRoot != null ? Ensure(sourceSkin.SkeletonRoot) : null;
            clonedSkin.BindNode = sourceSkin.BindNode != null ? Ensure(sourceSkin.BindNode) : null;
        }

        target.gltfNodes = template.gltfNodes.Select(node => nodeMap[node]).ToList();
        // The logical-index map and the clips are remapped through the same node map, or a clip would animate the
        // template's nodes and drag every other instance of it along.
        target._nodeMap = template._nodeMap.ToDictionary(kvp => kvp.Key, kvp => nodeMap[kvp.Value]);
        target._animations = AnimationOps.CloneAnimations(template._animations, nodeMap);
        target._animationPlayer = new GLTFAnimationPlayer();
        target._animationPlayer.Initialize(target._animations);
        // A fresh player already sits on clip 0 and playing, so this states the intent rather than starting anything; it
        // is here because this is the one place all four backends agree on what a cloned instance is.
        target.PlayAnimation();
    }
}
