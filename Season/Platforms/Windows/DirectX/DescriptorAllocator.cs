// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Text;

namespace Season.Platforms.Windows.DirectX;

/// <summary>
/// Which subsystem owns a descriptor. The pool is shared by everything the platform binds, so a report that only
/// says "exhausted" cannot answer whether the scene simply grew more textures or a per-frame capture path is
/// holding slots alive for the life of the process. These buckets make that question answerable from one log line.
/// </summary>
internal enum DescriptorKind : byte
{
    Other = 0,
    /// <summary>A draw-snapshot view owned by a FrameContext's DXDrawSnapshotArena; reclaimed only when that frame dies.</summary>
    Snapshot = 1,
    /// <summary>A long-lived texture SRV (DXTexture.DescriptorID / UAV).</summary>
    Texture = 2,
    /// <summary>Per-model bone / previous-bone / previous-morph palettes and per-primitive morph deltas.</summary>
    ModelData = 3,
    /// <summary>A per-instance ring (bone / morph / world / extras), one descriptor per frame slot.</summary>
    InstanceRing = 4,
    /// <summary>An offscreen render-target SRV.</summary>
    RenderTarget = 5,
    /// <summary>A compute storage buffer or a text glyph buffer.</summary>
    Compute = 6,
    /// <summary>A device-level placeholder created once during startup and never released.</summary>
    Placeholder = 7,
    Count = 8,
}

/// <summary>
/// Allocates SRV/UAV descriptor indices. Called concurrently by the loading thread
/// (TextGlyphBufferPool.Rent from LoadTexts/AppendTexts, DXTexture creation) and the
/// render thread (runtime texture replacement such as video-frame TextureOverride,
/// deferred-release Free execution). Without the lock, two threads can receive the
/// same index (two CreateShaderResourceView calls overwrite one descriptor slot) or
/// corrupt the free-list, producing garbage indices whose CPU handles write past the
/// descriptor heap and damage D3D12 internals; the damage then surfaces as a random
/// SEHException in an unrelated command-list record call (for example
/// IASetVertexBuffers inside DrawTexts). Same cross-thread class as the deferred
/// release queue / TransitionCommandList separation.
/// </summary>
internal class DescriptorAllocator
{
    readonly object _sync = new();
    readonly Stack<int> _freeList = new();
    readonly int _capacity;
    // Per-slot bookkeeping, indexed by descriptor index. `_live` is what rejects a repeated Free: without it the
    // same index is pushed twice, two owners then share one slot, and the second Create*View silently overwrites
    // the first owner's binding. It also rejects a -1 (an owner field cleared before a second teardown), which
    // would otherwise come back out of Allocate and make GetCpuHandle compute a handle far outside the heap.
    readonly bool[] _live;
    readonly DescriptorKind[] _kinds;
    readonly int[] _liveByKind = new int[(int)DescriptorKind.Count];
    int _nextIndex;
    int _liveCount;
    int _highWater;
    int _invalidFreeCount;

    public DescriptorAllocator() : this(2048) { }

    public DescriptorAllocator(int capacity)
    {
        _capacity = capacity;
        _live = new bool[capacity];
        _kinds = new DescriptorKind[capacity];
    }

    public int Capacity => _capacity;

    /// <summary>Descriptors currently handed out. Diagnostics only.</summary>
    public int LiveCount { get { lock (_sync) return _liveCount; } }

    /// <summary>Largest <see cref="LiveCount"/> ever observed, i.e. where the capacity has to reach. Diagnostics only.</summary>
    public int HighWater { get { lock (_sync) return _highWater; } }

    /// <summary>
    /// Releases rejected because the index was already free or out of range. A non-zero value means an owner
    /// released its slot twice (a teardown path ran again after clearing its id); it is counted instead of
    /// thrown because that always happens during shutdown, where a second exception only hides the first one.
    /// </summary>
    public int InvalidFreeCount { get { lock (_sync) return _invalidFreeCount; } }

    /// <summary>
    /// Appended to the exhaustion message. The pool cannot see frame state on its own, and the caller that needs
    /// this (Device) owns the snapshot arenas, whose per-frame high-water marks are the number that actually
    /// explains a mid-draw exhaustion. The delegate must be side-effect free: it runs while the pool lock is held.
    /// </summary>
    internal static Func<string>? ExtraDiagnostics { get; set; }

    public int Allocate(DescriptorKind kind = DescriptorKind.Other)
    {
        lock (_sync)
        {
            int index;
            if (_freeList.Count > 0)
            {
                index = _freeList.Pop();
            }
            else if (_nextIndex < _capacity)
            {
                index = _nextIndex++;
            }
            else
            {
                throw new System.Exception(DescribeExhaustion());
            }

            _kinds[index] = kind;
            _live[index] = true;
            _liveByKind[(int)kind]++;
            if (++_liveCount > _highWater) _highWater = _liveCount;
            return index;
        }
    }

    public void Free(int index)
    {
        lock (_sync)
        {
            if ((uint)index >= (uint)_capacity || !_live[index])
            {
                _invalidFreeCount++;
                return;
            }

            _live[index] = false;
            _liveCount--;
            _liveByKind[(int)_kinds[index]]--;
            _freeList.Push(index);
        }
    }

    /// <summary>
    /// Builds the exhaustion report. Called under <see cref="_sync"/>: it reads the counters directly and must
    /// not reach back into <see cref="Allocate"/>/<see cref="Free"/> through the diagnostic delegate.
    /// </summary>
    string DescribeExhaustion()
    {
        var report = new StringBuilder(160);
        report.Append($"Descriptor heap exhausted (capacity: {_capacity}, live: {_liveCount}, high-water: {_highWater})");
        report.Append(" | live by kind:");
        bool any = false;
        for (int k = 0; k < _liveByKind.Length; k++)
        {
            if (_liveByKind[k] == 0) continue;
            report.Append(' ').Append(((DescriptorKind)k).ToString()).Append('=').Append(_liveByKind[k]);
            any = true;
        }
        if (!any) report.Append(" none");
        if (_invalidFreeCount != 0)
            report.Append(" | rejected frees (double release or out of range): ").Append(_invalidFreeCount);

        var extra = ExtraDiagnostics;
        if (extra != null)
        {
            try
            {
                string detail = extra();
                if (!string.IsNullOrEmpty(detail)) report.Append(" | ").Append(detail);
            }
            catch (Exception exception)
            {
                // A diagnostic must never replace the diagnosis.
                report.Append(" | extra diagnostics failed: ").Append(exception.GetType().Name);
            }
        }

        return report.ToString();
    }
}
