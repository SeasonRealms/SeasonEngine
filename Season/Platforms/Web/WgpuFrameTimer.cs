// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Runtime.Versioning;

namespace Season.Platforms.Web;

/// <summary>
/// Contract 3-1 for the WebGPU backend: a four-slot ring of timestamp query sets, written by each pass's
/// <c>timestampWrites</c> descriptor and read back asynchronously through <c>takeGpuTimestamps</c>.
///
/// <para><b>A timestamp only exists at a pass boundary.</b> WebGPU has no command-encoder timestamp, and the
/// per-pass <c>writeTimestamp</c> method is behind <c>chromium-experimental-timestamp-query-inside-passes</c> (measured:
/// a stable browser rejects it even when the adapter advertises the feature, unless it was requested at device creation
/// and the page is Chromium). The portable form is the pass descriptor's <c>timestampWrites</c>, which means both indices
/// of a pass have to be known when the pass opens - a different shape from the other three backends, where a mark is
/// written whenever it is taken.</para>
///
/// <para><b>How the shared layer's marks map onto that.</b> A pass opens with <c>FrameProfiler.BeginPass</c> (which takes
/// a mark) immediately followed by <c>g.BeginPass</c>, so the opening index is in hand. The closing mark is taken by
/// <c>FrameProfiler.EndPass</c> right after <c>g.EndPass()</c> - measured at every call site in <c>RenderPass.cs</c> - so
/// it is always the next index to be handed out, and the descriptor can reserve it in advance. The frame's own two marks
/// and a compute interval's closing mark have no pass to ride on; those are written by a one-line marker pass emitted at
/// the moment the mark is taken, which the encoder is still open to accept.</para>
///
/// <para><b>An index that never finds a pass is reported as unmeasured.</b> A mark whose pass never opened cannot be
/// written anywhere, and a query set keeps whatever an earlier frame left in that index. <see cref="Mark"/> therefore
/// answers -1 for the interval that would have spanned it, so the shared layer publishes "unknown" for that row instead
/// of a number read from four frames ago.</para>
///
/// <para><b>Slot chosen at the frame head.</b> The JS side owns one query set per slot and refuses a slot whose readback
/// is still mapped; asking at the head is what turns "four frames of CPU lead" into a dropped frame instead of a
/// sample buffer overwritten under a pending map.</para>
///
/// <para><b>Nanoseconds, not ticks.</b> The WebGPU specification defines timestamp queries as nanoseconds, so
/// <see cref="NanosecondsPerTick"/> is one and the shared layer does no conversion.</para>
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed class WgpuFrameTimer : IGpuFrameTimer
{
    /// <summary>Marks accepted per frame. Matches the other three backends: 250 intervals need two marks each.</summary>
    internal const int Marks = 512;

    const int SlotCount = 4;

    /// <summary>Managed copy handed out by <see cref="TryTake"/>; valid until the next call, as the interface promises.</summary>
    readonly ulong[] _copy = new ulong[Marks];

    /// <summary>Frame token each slot is waiting on; 0 means the slot is free.</summary>
    readonly long[] _slotTokens = new long[SlotCount];

    /// <summary>Samples each slot's pending readback holds, so a take cannot hand out a longer span than was resolved.</summary>
    readonly int[] _slotMarks = new int[SlotCount];

    int _cursor;

    /// <summary>Next index to hand out, which is also the number of samples this frame will resolve.</summary>
    int _marks;

    /// <summary>The frame's opening mark, held until the first pass of the frame can carry it (or the frame closes).</summary>
    int _headMark = -1;

    /// <summary>True once the frame's opening mark has been handed out; the ring is per frame, so this resets with it.</summary>
    bool _headSeen;

    /// <summary>Most recent mark, claimable as the opening index of the pass that follows it.</summary>
    int _freshMark = -1;

    /// <summary>Index reserved for the closing mark of the pass that is currently open, or -1.</summary>
    int _reservedEnd = -1;

    /// <summary>True when a compute pass claimed an interval's opening mark and its closing mark is still owed.</summary>
    bool _computeOpen;

    /// <summary>Slot this frame is writing into, or -1 while the frame is dropped or already handed over.</summary>
    int _activeSlot = -1;

    long _pendingToken;

    bool _dropFrame;

    bool _broken;

    bool _disposed;

    /// <summary>
    /// Creates the timer, or returns null when this browser cannot timestamp at all: no <c>timestamp-query</c> feature on
    /// the adapter, or a device that refuses the query set. Returning null is the same answer the other backends give
    /// when their timestamp support is missing - the profiler keeps its CPU columns and the GPU column reads "unknown"
    /// instead of zero.
    /// </summary>
    internal static WgpuFrameTimer? TryCreate()
    {
        try
        {
            return WebGPUInterop.CreateGpuTimer(Marks) ? new WgpuFrameTimer() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public bool Available => !_broken && !_disposed;

    public double NanosecondsPerTick => 1.0;

    public int Capacity => Marks;

    public void BeginFrame(long token)
    {
        if (_broken) return;

        _marks = 0;
        _headMark = -1;
        _headSeen = false;
        _freshMark = -1;
        _reservedEnd = -1;
        _computeOpen = false;
        _activeSlot = -1;
        _pendingToken = token;
        _dropFrame = false;

        // Oldest free slot wins, so frames come back in the order they were submitted. "Free" is the JS side's answer:
        // it knows whether a slot's readback is still mapped, which is the only thing that makes a query set unsafe to
        // reuse here - there is no completion value to compare against in WebGPU.
        for (int i = 0; i < SlotCount; i++)
        {
            int slot = (_cursor + i) % SlotCount;

            bool taken;

            try
            {
                taken = WebGPUInterop.BeginGpuTimestamps(slot, (double)token);
            }
            catch (Exception)
            {
                Break();

                return;
            }

            if (!taken) continue;

            _activeSlot = slot;
            _cursor = (slot + 1) % SlotCount;

            return;
        }

        // Every slot is still mapped: that is a stutter, not a failure, so this frame is dropped and the next one starts
        // clean - breaking here would end GPU timing for the rest of the process on a single hiccup.
        _dropFrame = true;
    }

    public int Mark()
    {
        if (_broken || _dropFrame || _activeSlot < 0) return -1;

        // The reserved index closes the pass that is ending: FrameProfiler.EndPass runs immediately after g.EndPass().
        if (_reservedEnd >= 0)
        {
            int end = _reservedEnd;
            _reservedEnd = -1;

            return end;
        }

        // The previous mark never found a pass, so the interval closing now opened on an index nothing was written
        // into. Answering -1 keeps that row out of the table instead of reporting whatever the slot last held.
        if (_freshMark >= 0)
        {
            _freshMark = -1;
            _computeOpen = false;

            return -1;
        }

        if (_marks >= Marks) return -1;

        int index = _marks++;

        // A compute interval may span several passes, so its closing mark cannot ride on any single pass's descriptor.
        // The encoder is still open at this point, so a marker pass is the one place it can go.
        if (_computeOpen)
        {
            _computeOpen = false;
            Place(index);

            return index;
        }

        // The first mark of the frame is its start. It is handed to the module with the first pass, which writes it as a
        // marker pass ahead of itself; a frame with no passes at all writes it when the frame closes.
        if (!_headSeen)
        {
            _headSeen = true;
            _headMark = index;

            return index;
        }

        _freshMark = index;

        return index;
    }

    /// <summary>
    /// Called by the backend immediately before a pass opens. <paramref name="hasEnd"/> is true for a render pass, whose
    /// closing mark is predictable, and false for a compute pass, whose interval may contain more than one dispatch.
    /// </summary>
    void DeclarePass(bool hasEnd)
    {
        if (_broken || _dropFrame || _activeSlot < 0) return;

        int begin = _freshMark;
        _freshMark = -1;

        int end = -1;

        if (begin >= 0 && hasEnd && _marks < Marks)
        {
            end = _marks++;
            _reservedEnd = end;
        }

        // The opening mark of a compute interval belongs to its first pass; the closing mark is placed when it is taken.
        if (begin >= 0 && !hasEnd) _computeOpen = true;

        int head = _headMark;
        _headMark = -1;

        if (head < 0 && begin < 0 && end < 0) return;

        try
        {
            WebGPUInterop.SetPassTimestamps(head, begin, end);
        }
        catch (Exception)
        {
            Break();
        }
    }

    public void EndFrame()
    {
        if (_broken || _dropFrame || _activeSlot < 0) return;

        // FrameProfiler.EndFrame takes the frame's closing mark immediately before calling this, so it is the last index
        // handed out and nothing was claimed for it. The module writes it in a marker pass at the end of the encoder.
        int tail = _freshMark;

        _freshMark = -1;
        _reservedEnd = -1;
        _computeOpen = false;

        int marks = _marks;

        // Nothing was allocated this frame, so there is nothing to resolve and the slot stays free.
        if (marks == 0)
        {
            _activeSlot = -1;

            return;
        }

        int slot = _activeSlot;

        try
        {
            // A frame that recorded no pass never handed the opening mark over, and the module can only write it once
            // it knows. Without this the frame's total would subtract the closing mark from an index nothing wrote.
            if (_headMark >= 0)
            {
                WebGPUInterop.SetPassTimestamps(_headMark, -1, -1);

                _headMark = -1;
            }

            WebGPUInterop.EndGpuTimestamps(marks, tail);
        }
        catch (Exception)
        {
            Break();

            return;
        }

        _slotTokens[slot] = _pendingToken;
        _slotMarks[slot] = marks;
        _activeSlot = -1;
    }

    /// <summary>
    /// Nothing to do: the readback was already armed by <see cref="EndFrame"/>, and the JS side starts the map itself
    /// once the command buffer is submitted. Kept as a no-op rather than omitted because the interface requires it.
    /// </summary>
    public void OnSubmitted()
    {
    }

    public bool TryTake(out long token, out ReadOnlySpan<ulong> ticks)
    {
        token = 0;
        ticks = default;

        if (_broken) return false;

        double[] result;

        try
        {
            result = WebGPUInterop.TakeGpuTimestamps();
        }
        catch (Exception)
        {
            Break();

            return false;
        }

        // Element 0 is the frame token, the rest are that frame's timestamps.
        if (result == null || result.Length < 2) return false;

        long tag = (long)result[0];

        int slot = -1;

        for (int i = 0; i < SlotCount; i++)
        {
            if (_slotTokens[i] != 0 && _slotTokens[i] == tag)
            {
                slot = i;
                break;
            }
        }

        // A token nobody is waiting for means the frame was already dropped and aged out; take it off the queue anyway
        // so it cannot be handed out twice, and ask again next frame.
        if (slot < 0) return false;

        int count = result.Length - 1;

        if (count > _slotMarks[slot]) count = _slotMarks[slot];

        if (count > Marks) count = Marks;

        for (int i = 0; i < count; i++) _copy[i] = (ulong)result[i + 1];

        _slotTokens[slot] = 0;
        _slotMarks[slot] = 0;

        token = tag;
        ticks = _copy.AsSpan(0, count);

        return true;
    }

    /// <summary>Writes one index in a marker pass of its own; the caller has already checked that an encoder exists.</summary>
    void Place(int index)
    {
        try
        {
            WebGPUInterop.PlaceGpuTimestamp(index);
        }
        catch (Exception)
        {
            Break();
        }
    }

    /// <summary>
    /// The backend calls this immediately before opening a pass; a no-op when the profiler is off or another backend's
    /// timer is attached.
    /// </summary>
    internal static void PreparePass(bool hasEnd)
        => (Season.Rendering.FrameProfiler.AttachedTimer as WgpuFrameTimer)?.DeclarePass(hasEnd);

    void Break()
    {
        _broken = true;
        _activeSlot = -1;
        _freshMark = -1;
        _reservedEnd = -1;
        _headMark = -1;
        _computeOpen = false;

        for (int i = 0; i < SlotCount; i++)
        {
            _slotTokens[i] = 0;
            _slotMarks[i] = 0;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _broken = true;

        try
        {
            WebGPUInterop.DisposeGpuTimer();
        }
        catch (Exception)
        {
            // A page that is already tearing the device down cannot be asked to release anything; the query sets die
            // with the context, and the profiler never reads them again.
        }
    }
}
