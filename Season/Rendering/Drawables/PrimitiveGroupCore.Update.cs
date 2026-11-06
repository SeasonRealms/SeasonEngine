// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Drawables;

/// <summary>
/// The two pieces of an instanced Update that read the same on all three backends: the CPU skinning palette
/// upload and the previous-frame publication.
///
/// The pass over the instances stays backend code, and deliberately so. That loop is where the three actually
/// differ — a mapped ring on D3D12, a host-visible <c>BufferResource</c> on Vulkan, a double buffer advanced on
/// Metal — so hoisting it would only turn each per-instance difference into a hook per iteration. The two segments
/// hoisted here are the ones with no such difference.
///
/// Both are contracts rather than conveniences. A palette is keyed twice: the pose cache by the Instances index,
/// because a disabled entry leaves a hole, and the bone ring by the compacted write index, because that is the slot
/// the vertex stage reads. The two only line up while one place says which is which. The previous-frame flags are
/// consumed by three shader paths that have to agree, so publishing them in one place is what keeps a backend from
/// publishing half of them.
/// </summary>
internal abstract partial class PrimitiveGroupCore<TPrim, TMat, TTex>
    where TPrim : PrimitiveCpuData<TMat, TTex>, IDisposable
    where TMat : unmanaged, IMaterialFields
    where TTex : class
{
    /// <summary>
    /// CPU route: resolves this instance's palette row — replayed into it when the clock asked for a fresh sample —
    /// and uploads it into the instance's compacted slot.
    ///
    /// A throttled frame still uploads. The row it kept is the pose the instance is drawn with, so skipping the
    /// upload would draw whatever an earlier frame left in the ring slot.
    /// </summary>
    /// <param name="samplePose">Whether the animation clock asked for a fresh replay this frame.</param>
    /// <param name="cacheRow">Pose-cache row, indexed by the Instances index.</param>
    /// <param name="writeIndex">Compacted slot in the bone ring, the one the draw's instance order addresses.</param>
    protected void UploadInstancePalette(bool samplePose, int cacheRow, int writeIndex)
        => UploadInstanceBoneMatrices(writeIndex, ResolveInstancePalette(cacheRow, samplePose));

    /// <summary>
    /// Publishes the end-of-frame history state. The two parts travel together because the Draw passes gate on the
    /// first and the shaders gate on the second: a group that is transform-initialized while still flagged as
    /// having no history would draw a plausible but wrong previous frame instead of skipping it.
    /// </summary>
    /// <param name="historyValid">Whether the compacted order still lines up with the previous frame's slots.</param>
    /// <param name="hadPreviousFrame">False on the first update after load, when there is no previous frame at all.</param>
    protected void EndInstanceUpdateFrame(bool historyValid, bool hadPreviousFrame)
    {
        _transformInitialized = true;
        UpdatePrevStateFlags(historyValid);
        OnPrevStatePublished(hadPreviousFrame);
    }

    /// <summary>
    /// Resolves the row to upload: the cached one on a throttled frame, a freshly replayed one otherwise.
    /// </summary>
    protected virtual Matrix4x4[] ResolveInstancePalette(int cacheRow, bool samplePose)
        => throw MissingUpdateHooks();

    /// <summary>Uploads one instance's palette into the slot the vertex stage reads.</summary>
    protected virtual void UploadInstanceBoneMatrices(int writeIndex, Matrix4x4[] bones)
        => throw MissingUpdateHooks();

    /// <summary>
    /// Runs once per update, after the previous-frame flags have reached the primitives and the highlight shells.
    /// Vulkan republishes its descriptor sets here: they hold buffer handles by value, so the switch away from the
    /// placeholder SSBOs has to be reflected in the bound sets. The other two bind by GPU address or through the
    /// encoder and have nothing to redo.
    /// </summary>
    protected virtual void OnPrevStatePublished(bool hadPreviousFrame) { }

    private NotSupportedException MissingUpdateHooks()
        => new($"{GetType().Name} took the shared Update path without implementing its backend half.");
}
