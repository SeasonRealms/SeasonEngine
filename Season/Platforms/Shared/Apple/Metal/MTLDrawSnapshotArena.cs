// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Collections.Generic;

namespace Season.Platforms.Shared.Apple.Metal;

/// <summary>
/// Metal scene snapshot page, sharing its responsibility with DX <c>FrameContext.DrawSnapshots</c> and VK <c>VKDrawSnapshotArena</c>:
/// retirement validation + pinning resources during replay.
///
/// One essential difference from the other two backends is recorded here, so that latecomers do not think the deep copy was missed:
/// DX must write matrix/material/bone/instance data into a shared write-combined upload page on every draw,
/// so during collection that block of bytes has to be copied away wholesale, otherwise the next draw would overwrite it;
/// Metal's rotation ring is likewise exclusively owned per "owner + frame slot" (<c>PrimitiveData.MatrixBuffers[fi]</c>,
/// <c>MTLSpriteQuad._matrixBuffers[fi]</c>, and text with its own per-frame-slot instance/parameter buffers),
/// and the direct-draw path only ever hands the <c>IMTLBuffer</c> handle to the encoder, so nobody within a frame overwrites another's slot.
/// Therefore no byte copy is done here either, only lifetime pinning.
///
/// The difference from VK is that there is no descriptor page cursor here:
/// Metal bindings are <c>SetVertexBuffer</c> / <c>SetFragmentTexture</c> carrying the object reference and offset directly,
/// with no "a descriptor set cannot be rewritten once bound" constraint, so no descriptor write page and commit cursor are needed.
/// </summary>
internal sealed class MTLDrawSnapshotArena : IDisposable
{
    readonly List<object> _retained = new();

    // Reference equality: pinning the same object repeatedly must not re-enter the table; Texture/PrimitiveData have no usable value-equality semantics.
    readonly HashSet<object> _retainedSet = new(ReferenceEqualityComparer.Instance);

    ulong _stamp;

    bool _disposed;

    /// <summary>Number of objects pinned for the current frame, for tests and logs to observe the collection scale.</summary>
    internal int RetainedCount => _retained.Count;

    /// <summary>Current retirement stamp; the collector uses it to reject cross-frame consumption.</summary>
    internal ulong Stamp => _stamp;

    /// <summary>
    /// Enter a new frame: validate that the retirement stamp is monotonic, and reclaim the previous frame's pins.
    /// Metal's frame-slot retirement is guaranteed by <c>Device</c>'s in-flight semaphore (BeginFrame has already waited on that slot),
    /// so here it only needs to reject a "timeline going backwards" call.
    /// </summary>
    internal void BeginFrame(ulong stamp)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (stamp == 0 || stamp < _stamp)
            throw new InvalidOperationException("The scene snapshot retirement stamp must be monotonically increasing.");
        if (stamp == _stamp) return;

        ReleaseRetained();
        _stamp = stamp;
    }

    /// <summary>Pin an object that will still be referenced by an encoder during replay; pinning it repeatedly within the same frame is a no-op.</summary>
    internal void Retain(object? resource)
    {
        if (_disposed || resource == null || !_retainedSet.Add(resource)) return;
        _retained.Add(resource);
    }

    /// <summary>Pin a group of objects, for convenient one-shot registration.</summary>
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

    /// <summary>In-frame exception path: undo the pins, leaving no cross-frame residue.</summary>
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
