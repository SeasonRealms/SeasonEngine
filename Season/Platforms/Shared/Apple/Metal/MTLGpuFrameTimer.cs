// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Foundation;
using Metal;

namespace Season.Platforms.Shared.Apple.Metal;

/// <summary>
/// Contract 3-1: the Metal half of frame profiling - a four-slot <c>MTLCounterSampleBuffer</c> ring plus the command
/// queue's own completion value. Both iOS and MacCatalyst reach the device through <see cref="Device"/>, so one
/// implementation serves the two targets; what differs between them (whether the GPU can sample counters at a pass
/// boundary at all) is asked of the device at construction instead of compiled per platform.
///
/// <para><b>A slot is chosen at the frame head, not at the frame tail.</b> Unlike D3D12 and Vulkan, Metal writes the
/// samples while the frame is being encoded - the sample is a command inside an encoder - so the buffer that will
/// receive this frame's marks has to be known before the first pass opens. A frame that cannot get a slot is dropped
/// whole and counted; it is never waited for, because a profiler that blocks would be measuring itself. This is the
/// same trade-off as the other two backends, only the decision point moved earlier.</para>
///
/// <para><b>Samples are queued, then flushed into the next encoder.</b> <see cref="Mark"/> is called outside every
/// encoder: the shared layer brackets a pass by <c>g.BeginPass</c> / <c>g.EndPass</c> and a compute effect opens its own
/// encoder inside <c>Record</c>, so at the moment a mark is taken there is nothing to record into. Metal forbids
/// recording outside an encoder, so the mark only appends its index to a queue; <see cref="FlushInto"/> writes the
/// queue as the first commands of the next encoder that opens. The timestamps stay where the shared layer asked for
/// them - the boundary between the two is a single instant either way - and the cost is one sampler call per mark
/// instead of one throwaway blit encoder per mark, which would have changed the CPU pacing the profile is trying to
/// report. The only exception is the frame's own trailing mark, which has no following encoder and closes through a
/// single blit encoder in <see cref="EndFrame"/>.</para>
///
/// <para><b>Never signals anything.</b> The frame's submission already carries a completion value on the shared
/// command queue (<see cref="Device.LastSubmittedFrameValue"/>), and reading it is what <see cref="OnSubmitted"/> does.
/// Registering a value of our own would mean an extra command buffer per frame, and that submission would be the
/// profiler measuring itself - the same reasoning as the Vulkan timer's.</para>
///
/// <para><b>Nanoseconds, not ticks.</b> A timestamp counter sample is already in nanoseconds on Apple GPUs, so
/// <see cref="NanosecondsPerTick"/> is 1.0 and the shared layer's arithmetic needs no per-GPU constant.</para>
///
/// <para><b>Never a new crash site.</b> Every entry point swallows its failure once and goes
/// <see cref="Available"/>-false, exactly like the D3D12 and Vulkan timers: a development-time measurement tool has no
/// business turning a lost device into an unhandled exception from inside the frame loop. A GPU without counter
/// sampling fails the construction instead, which the shared layer reports as "no GPU column" rather than a fake
/// zero.</para>
///
/// <para>Threading: render thread only, and every member is called in submission order.</para>
/// </summary>
internal sealed unsafe class MTLGpuFrameTimer : IGpuFrameTimer
{
    /// <summary>
    /// Samples accepted per frame. Two marks per interval plus the frame's own pair, so this covers
    /// <see cref="Season.Rendering.FrameProfiler.MaxIntervals"/> with room to spare; the shared layer stops asking long
    /// before the buffer is full, and this is the second line of defence. Same value as the other two backends so all
    /// three drop frames under identical pressure.
    /// </summary>
    internal const int Marks = 512;

    /// <summary>
    /// Readback slots. Four is the shallowest ring that still has a free buffer while the compositor holds two frames,
    /// so a steady state drops nothing; making it deeper would only delay the numbers.
    /// </summary>
    const int SlotCount = 4;

    readonly IMTLCounterSampleBuffer?[] _slots = new IMTLCounterSampleBuffer?[SlotCount];

    /// <summary>Frame completion value guarding each slot; 0 means the slot is free.</summary>
    readonly ulong[] _slotValues = new ulong[SlotCount];

    readonly long[] _slotTokens = new long[SlotCount];

    readonly int[] _slotMarks = new int[SlotCount];

    /// <summary>Managed copy handed out by <see cref="TryTake"/>; valid until the next call, as the interface promises.</summary>
    readonly ulong[] _copy = new ulong[Marks];

    /// <summary>Sample indices waiting for an encoder to open; drained by <see cref="FlushPending"/>.</summary>
    readonly int[] _queued = new int[Marks];

    int _cursor;

    int _marks;

    int _queuedCount;

    /// <summary>
    /// Marks actually written into an encoder this frame. Usually equal to <see cref="_marks"/>, but a frame that threw
    /// with a render encoder still open ends without a place to drain the queue, and the counter sample buffer is reused
    /// memory - reading past this point would hand back the values a frame four slots ago wrote. Only this prefix is
    /// published; a mark pair that falls outside it is reported as unmeasured rather than as a wrong number.
    /// </summary>
    int _flushed;

    /// <summary>Slot this frame is writing into, or -1 while the frame is dropped or already published.</summary>
    int _activeSlot = -1;

    long _pendingToken;

    bool _dropFrame;

    bool _broken;

    bool _disposed;

    internal MTLGpuFrameTimer()
    {
        var device = Device.MtlDevice;

        // A pass boundary is the sampling point the shared layer's marks correspond to. DrawBoundary is the fallback:
        // some families refuse the stage-boundary form but still sample once per draw, which is the same instant for a
        // boundary that sits between two draws. Neither being available means this GPU cannot timestamp at all.
        bool stageBoundary = device.SupportsCounterSampling(MTLCounterSamplingPoint.StageBoundary);
        bool drawBoundary = device.SupportsCounterSampling(MTLCounterSamplingPoint.DrawBoundary);

        if (!stageBoundary && !drawBoundary)
            throw new InvalidOperationException("The GPU cannot sample counters at a pass boundary; GPU frame timing is unavailable.");

        string timestampName = MTLCommonCounter.Timestamp.ToString();

        IMTLCounterSet? timestampSet = null;

        foreach (var set in device.CounterSets ?? [])
        {
            // Matched on the constant's own name and, as a second chance, on the literal Apple documents for it. The
            // binding hands the constant back as a wrapped native string, and how that wrapper stringifies is exactly
            // the kind of detail that may differ between SDK revisions - guessing wrong would silently cost the GPU
            // column on every device, so both spellings are accepted.
            if (set.Name == timestampName || string.Equals(set.Name, "timestamp", StringComparison.OrdinalIgnoreCase))
            {
                timestampSet = set;
                break;
            }
        }

        if (timestampSet == null)
            throw new InvalidOperationException("The device exposes no timestamp counter set; GPU frame timing is unavailable.");

        for (int i = 0; i < SlotCount; i++)
        {
            // Shared storage: the CPU reads the resolved samples directly, with no second staging hop. Every Apple GPU
            // that can sample counters supports it, and a device that does not fails the creation below.
            using var desc = new MTLCounterSampleBufferDescriptor
            {
                CounterSet = timestampSet,
                SampleCount = (nuint)Marks,
                StorageMode = MTLStorageMode.Shared,
            };

            var buffer = device.CreateCounterSampleBuffer(desc, out NSError? error);

            if (buffer == null)
            {
                ReleaseNow();
                throw new InvalidOperationException(
                    $"MTLCreateCounterSampleBuffer ({Marks} samples) failed: {error?.LocalizedDescription ?? "(no NSError)"}");
            }

            _slots[i] = buffer;
        }
    }

    public bool Available => !_broken && !_disposed;

    public double NanosecondsPerTick => 1.0;

    public int Capacity => Marks;

    public void BeginFrame(long token)
    {
        if (_broken) return;

        _marks = 0;
        _queuedCount = 0;
        _flushed = 0;
        _activeSlot = -1;
        _pendingToken = token;
        _dropFrame = false;

        try
        {
            ulong completed = Device.GraphicsQueue.GetCompletedValue();

            // Oldest free slot wins, so frames come back in the order they were submitted. A busy slot means the CPU is
            // four frames ahead of the GPU, which is already a timing artefact worth not measuring.
            for (int i = 0; i < SlotCount; i++)
            {
                int slot = (_cursor + i) % SlotCount;

                if (_slotValues[slot] != 0 && completed < _slotValues[slot]) continue;

                _slotValues[slot] = 0;
                _activeSlot = slot;
                _cursor = (slot + 1) % SlotCount;
                return;
            }

            // Every slot is still in flight: that is a stutter, not a failure, so this frame is dropped and the next
            // one starts clean - breaking here would end GPU timing for the rest of the process on a single hiccup.
            _dropFrame = true;
        }
        catch (Exception)
        {
            Break();
        }
    }

    public int Mark()
    {
        if (_broken || _dropFrame) return -1;

        if (_marks >= Marks) return -1;

        _queued[_queuedCount++] = _marks;

        return _marks++;
    }

    public void EndFrame()
    {
        if (_broken || _dropFrame || _marks == 0) return;

        try
        {
            // Nothing opened an encoder after the last mark (the frame ended between passes), so the queue is drained
            // here. A render encoder still open at this point means the frame threw before AfterRender - the backend's
            // own defensive EndPass runs later, so recording a blit now would fail; the frame is simply not measured.
            if (_queuedCount > 0 && Device.GraphicsEncoder == null && Device.GraphicsCommandBuffer != null)
            {
                var blit = Device.GraphicsCommandBuffer.CreateBlitCommandEncoder(new MTLBlitPassDescriptor());

                if (blit != null)
                {
                    try
                    {
                        FlushPending(blit);
                    }
                    finally
                    {
                        blit.EndEncoding();
                        blit.Dispose();
                    }
                }
            }
        }
        catch (Exception)
        {
            Break();
        }
    }

    public void OnSubmitted()
    {
        if (_broken || _dropFrame || _activeSlot < 0) return;

        int slot = _activeSlot;

        _activeSlot = -1;

        try
        {
            // The frame's own submission already carries this value on the shared command queue. Recording it here
            // means "this slot is done once the GPU reaches this frame", with no extra command buffer spent on it.
            ulong value = Device.LastSubmittedFrameValue;

            if (value == 0) return;

            _slotValues[slot] = value;
            _slotTokens[slot] = _pendingToken;
            _slotMarks[slot] = _flushed;
        }
        catch (Exception)
        {
            Break();
        }
    }

    public bool TryTake(out long token, out ReadOnlySpan<ulong> ticks)
    {
        token = 0;
        ticks = default;

        if (_broken) return false;

        try
        {
            ulong completed = Device.GraphicsQueue.GetCompletedValue();

            int slot = -1;
            ulong best = 0;

            // Oldest ready slot wins, so frames come back in the order they were submitted.
            for (int i = 0; i < SlotCount; i++)
            {
                ulong value = _slotValues[i];

                if (value == 0 || completed < value) continue;

                if (slot >= 0 && value >= best) continue;

                slot = i;
                best = value;
            }

            if (slot < 0) return false;

            int marks = _slotMarks[slot];

            var buffer = _slots[slot];

            if (marks <= 0 || buffer == null)
            {
                _slotValues[slot] = 0;
                return false;
            }

            // Resolving is a CPU-side read of a buffer the GPU has finished with; the completion value above is what
            // guarantees that, so this call never waits. The samples are already nanoseconds (see the type summary).
            NSData? data = buffer.ResolveCounterRange(new NSRange(0, (nint)marks));

            if (data == null)
            {
                _slotValues[slot] = 0;
                return false;
            }

            var source = (ulong*)data.Bytes;

            for (int i = 0; i < marks; i++) _copy[i] = source[i];

            _slotValues[slot] = 0;

            token = _slotTokens[slot];
            ticks = _copy.AsSpan(0, marks);

            return true;
        }
        catch (Exception)
        {
            Break();

            return false;
        }
    }

    /// <summary>
    /// Writes every queued mark into the encoder that just opened. The three concrete encoder protocols each declare
    /// their own <c>sampleCounters</c>, which is why this is overloaded instead of taking the shared base interface - and
    /// why each overload spells the loop out rather than taking a delegate, which would allocate on every pass boundary
    /// and show up in the very allocation column the same frame is reporting.
    /// </summary>
    internal void FlushPending(IMTLRenderCommandEncoder encoder)
    {
        if (!TryBeginFlush(out var buffer)) return;

        for (int i = 0; i < _queuedCount; i++)
        {
            // The barrier makes the sample a fixed point the GPU cannot schedule around, which is what a timestamp on
            // a tile-based GPU has to be to be comparable with the other two backends.
            encoder.SampleCounters(buffer, (nuint)_queued[i], true);
        }

        _flushed += _queuedCount;

        _queuedCount = 0;
    }

    internal void FlushPending(IMTLComputeCommandEncoder encoder)
    {
        if (!TryBeginFlush(out var buffer)) return;

        for (int i = 0; i < _queuedCount; i++) encoder.SampleCounters(buffer, (nuint)_queued[i], true);

        _flushed += _queuedCount;

        _queuedCount = 0;
    }

    internal void FlushPending(IMTLBlitCommandEncoder encoder)
    {
        if (!TryBeginFlush(out var buffer)) return;

        for (int i = 0; i < _queuedCount; i++) encoder.SampleCounters(buffer, (nuint)_queued[i], true);

        _flushed += _queuedCount;

        _queuedCount = 0;
    }

    bool TryBeginFlush(out IMTLCounterSampleBuffer buffer)
    {
        buffer = null!;

        if (_broken || _dropFrame || _activeSlot < 0 || _queuedCount == 0) return false;

        var thisFrame = _slots[_activeSlot];

        if (thisFrame == null) return false;

        buffer = thisFrame;

        return true;
    }

    /// <summary>
    /// The backend's encoder-opening sites call this; a no-op when the profiler is off or another backend's timer is
    /// attached.
    /// </summary>
    internal static void FlushInto(IMTLRenderCommandEncoder encoder)
        => (Season.Rendering.FrameProfiler.AttachedTimer as MTLGpuFrameTimer)?.FlushPending(encoder);

    internal static void FlushInto(IMTLComputeCommandEncoder encoder)
        => (Season.Rendering.FrameProfiler.AttachedTimer as MTLGpuFrameTimer)?.FlushPending(encoder);

    internal static void FlushInto(IMTLBlitCommandEncoder encoder)
        => (Season.Rendering.FrameProfiler.AttachedTimer as MTLGpuFrameTimer)?.FlushPending(encoder);

    void Break()
    {
        _broken = true;
        _activeSlot = -1;
        _queuedCount = 0;
        _flushed = 0;
    }

    void ReleaseNow()
    {
        for (int i = 0; i < SlotCount; i++)
        {
            _slots[i]?.Dispose();
            _slots[i] = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _broken = true;
        _activeSlot = -1;

        // Command buffers retain the resources they reference (the queue rejects buffers without retained
        // references), so a slot still in flight keeps its sample buffer alive on its own.
        ReleaseNow();
    }
}
