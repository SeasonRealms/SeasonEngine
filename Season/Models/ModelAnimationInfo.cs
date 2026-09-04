// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Models;

/// <summary>
/// glTF animation clip metadata.
/// This belongs to the model-parsing domain, namely GltfAsset and GLTFAnimationPlayer,
/// and is independent of the graphics backend, so it is shared across all backends.
/// It currently carries Name and Duration and can be extended later with fields such as
/// loop flags or event markers without changing the caller-facing signature.
/// </summary>
public readonly struct ModelAnimationInfo
{
    /// <summary>Animation name, taken from <c>glTF animations[].name</c> and falling back to <c>Animation_{index}</c> during loading when missing.</summary>
    public readonly string Name;

    /// <summary>Clip duration in seconds, defined as the maximum final-keyframe time across all channel samplers. Returns 0 when no keyframes exist.</summary>
    public readonly float Duration;

    public ModelAnimationInfo(string name, float duration)
    {
        Name = name;
        Duration = duration;
    }
}
