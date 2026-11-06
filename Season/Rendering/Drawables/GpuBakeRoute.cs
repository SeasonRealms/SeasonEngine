// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Drawables;

/// <summary>
/// The stepper for the GPU skinning route, which every backend runs identically once it has latched the route through
/// <c>BeginSkinningFrame</c>.
///
/// On this route the kernel samples the bake, so no curve is evaluated and no node transform is touched: the clock and
/// one 16-byte parameter row are the whole per-frame cost. <c>WorldTransform</c> stays at rest, which is exactly what
/// the palette is relative to - the vertex stage multiplies each primitive by <c>restWorld * userTransform</c> while the
/// palette carries <c>inverse(restWorld)</c>, so the two cancel and only the user transform survives.
///
/// The stale CPU pose is the price: picking cannot read a storage buffer, so it asks for one on demand
/// (see <c>GltfAsset.EnsureCpuPose</c>) and pays for it only when it actually needs the pose that frame.
///
/// This lived as four copies that were already one word apart, which is the drift this file exists to stop.
/// </summary>
internal static class GpuBakeRoute
{
    /// <summary>Advances the clock and publishes the instance row the kernel reads. Call only when the route actually
    /// resolved to a bake; the CPU route steps the player through the tree instead.</summary>
    internal static void Advance(GltfAsset asset, InstanceSkinningTarget skinning, InstanceSkinBake bake, float time)
    {
        var player = asset._animationPlayer;
        player.Advance(time);
        int clip = player.CurrentAnimationIndex;
        skinning.SetInstance(0, (uint)clip, bake.NormalizeTime(clip, player.AnimationTime, player.Loop));

        asset.CpuPoseStale = true;
    }
}
