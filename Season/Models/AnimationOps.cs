// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Models;

/// <summary>
/// Animation-data operations that have nothing to do with a graphics API, so all four backends can share them.
///
/// <see cref="CloneAnimations"/> lived six times before this: once in <c>DXModel</c>, <c>VKModel</c>, <c>MTLModel</c>,
/// <c>VKInstancedModel</c>, <c>MTLInstancedModel</c> and <c>WGPUModel</c>. The six bodies were byte-identical — they
/// only differed in whether the method was spelled <c>static</c> or <c>internal static</c>, and whether the type names
/// carried a <c>Season.Models.</c> prefix — which is the signature of code that was copied because there was nowhere to
/// put it, not because the backends wanted different behaviour. A clone is pure managed data: lists of floats, a node
/// map, and a new <see cref="GLTFAnimation"/> per source clip. There is no handle, no upload and no per-backend layout
/// in it.
///
/// The clone exists for instances: a <c>CreateInstance</c> call duplicates the asset's node tree so two instances can
/// play different clips, and the animation channels have to be re-pointed at the cloned nodes or every instance would
/// drive the template's skeleton.
/// </summary>
internal static class AnimationOps
{
    /// <summary>
    /// Deep-copies animation clips so a new instance owns its own playback state, remapping every channel target
    /// through <paramref name="nodeMap"/> (template node to clone node). A channel whose target node is absent from the
    /// map becomes a null target and is dropped: it pointed at a node the clone does not have, so there is nothing for
    /// it to drive. Samplers travel with the channel that uses them.
    /// </summary>
    internal static List<GLTFAnimation> CloneAnimations(
        List<GLTFAnimation> sourceAnimations, Dictionary<GltfNodeBase, GltfNodeBase> nodeMap)
    {
        var result = new List<GLTFAnimation>(sourceAnimations.Count);
        foreach (var sourceAnimation in sourceAnimations)
        {
            var clonedAnimation = new GLTFAnimation
            {
                Name = sourceAnimation.Name,
            };

            foreach (var sourceChannel in sourceAnimation.Channels)
            {
                var clonedSampler = new AnimationSampler
                {
                    Inputs = new List<float>(sourceChannel.Sampler?.Inputs ?? new List<float>()),
                    Values = new List<float>(sourceChannel.Sampler?.Values ?? new List<float>()),
                    InTangents = sourceChannel.Sampler?.InTangents != null ? new List<float>(sourceChannel.Sampler.InTangents) : null,
                    OutTangents = sourceChannel.Sampler?.OutTangents != null ? new List<float>(sourceChannel.Sampler.OutTangents) : null,
                    OutputElementCount = sourceChannel.Sampler?.OutputElementCount ?? 4,
                    Interpolation = sourceChannel.Sampler?.Interpolation ?? AnimationInterpolationMode.Linear,
                };

                var clonedChannel = new AnimationChannel
                {
                    Sampler = clonedSampler,
                    Target = sourceChannel.Target == null
                        ? null
                        : new AnimationChannelTarget
                        {
                            Node = sourceChannel.Target.Node != null ? nodeMap[sourceChannel.Target.Node] : null,
                            Path = sourceChannel.Target.Path,
                        }
                };

                clonedAnimation.Samplers.Add(clonedSampler);
                if (clonedChannel.Target != null)
                    clonedAnimation.Channels.Add(clonedChannel);
            }

            result.Add(clonedAnimation);
        }

        return result;
    }
}
