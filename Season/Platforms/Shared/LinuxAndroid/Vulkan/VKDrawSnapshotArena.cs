// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Collections.Generic;

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

/// <summary>
/// Vulkan scene snapshot page, aligned with the responsibilities of DX <c>FrameContext.DrawSnapshots</c>: retirement validation and the host-visible page cursor.
///
/// One essential difference from DX is written here, so that later readers do not assume a deep copy was missed:
/// every DX draw writes matrix/material/skeleton/instance data into a shared write-combined upload page,
/// so the collection must copy that block of bytes out wholesale, otherwise the next draw would overwrite it;
/// VK's rotation ring is exclusively owned by "owner + frame slot" (<c>PrimitiveData.MatrixBuffers[fi]</c>,
/// <c>VKSpriteQuad._matrixBuffers[fi]</c>, <c>_instanceBuffer</c>, etc.),
/// and the direct-draw path originally only hands handles to the command buffer, so no one overwrites another's slot within a frame.
/// So no byte copying is done here, only lifetime pinning: the controls/textures/buffers used during replay must not be released before this frame's GPU retirement.
/// </summary>
internal sealed class VKDrawSnapshotArena : IDisposable
{
    readonly List<object> _retained = new();

    // Reference equality: pinning the same object repeatedly should not re-enter the table; Texture/PrimitiveData have no usable value-equality semantics.
    readonly HashSet<object> _retainedSet = new(ReferenceEqualityComparer.Instance);

    ulong _stamp;

    bool _disposed;

    /// <summary>Number of objects pinned this frame, used by tests and logs to observe the collection scale.</summary>
    internal int RetainedCount => _retained.Count;

    /// <summary>
    /// Enters a new frame: validates that the retirement stamp is monotonic and reclaims the previous frame's pins.
    /// Shaped the same as DX's <c>DXDrawSnapshotArena.BeginFrame</c>, except that the VK queue's retirement is guaranteed by the device timeline semaphore,
    /// so here it only needs to reject calls with a "timeline regression".
    /// </summary>
    internal void BeginFrame(ulong stamp)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (stamp == 0 || stamp < _stamp)
            throw new InvalidOperationException("The scene snapshot's retirement stamp must be monotonically increasing.");
        if (stamp == _stamp) return;

        // During collection/replay the current frame slot's descriptor sets are rewritten (all slots are rewritten together when the skeleton or the previous frame's SSBO capacity changes),
        // while the previous frame's submission on the same slot may still be on the GPU: a set that does not declare UPDATE_AFTER_BIND,
        // when rewritten while referenced by a pending command buffer, invalidates that entire buffer.
        // This is the same set of constraints as VKCompute.AcquireSet and VKPrimitiveGroup.WriteDescriptorSet;
        // we wait only once per frame when entering collection, and the submission on the same slot has usually long since completed.
        Device.WaitForFrameSlot((int)Device.FrameIndex);

        ReleaseRetained();
        _stamp = stamp;
    }

    /// <summary>Pins an object that will still be referenced by the command buffer during replay; pinning it repeatedly within the same frame is a no-op.</summary>
    internal void Retain(object? resource)
    {
        if (_disposed || resource == null || !_retainedSet.Add(resource)) return;
        _retained.Add(resource);
    }

    /// <summary>Pins a group of objects, convenient for registering them all at once.</summary>
    internal void Retain<T>(T[]? resources)
    {
        if (resources == null) return;
        for (int i = 0; i < resources.Length; i++) Retain(resources[i]);
    }

    void ReleaseRetained()
    {
        _retained.Clear();
        _retainedSet.Clear();
    }

    /// <summary>In-frame exception path: undoes the pins, leaving no cross-frame residue.</summary>
    internal void Reset()
    {
        ReleaseRetained();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseRetained();
    }
}
