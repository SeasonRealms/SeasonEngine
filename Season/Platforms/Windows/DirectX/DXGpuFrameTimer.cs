// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using Season.Basic;

namespace Season.Platforms.Windows.DirectX;

/// <summary>
/// Contract 3-1: the D3D12 half of frame profiling - a timestamp query heap, a small readback ring and one fence that
/// belongs to nobody else.
///
/// <para><b>Marks, not spans.</b> <see cref="Mark"/> only appends <c>EndQuery(Timestamp)</c> to the frame's command list.
/// The resolve-and-copy is decided at <see cref="EndFrame"/>, once the number of marks is known, and lands in one of four
/// independent READBACK buffers. Nothing here interprets the ticks: the shared layer subtracts them.</para>
///
/// <para><b>A slot is only reused once the GPU has finished with it.</b> <see cref="EndFrame"/> checks the slot's fence
/// before recording the copy. If the slot is still busy the whole frame is dropped and counted - never waited for. That is
/// the opposite trade-off from <c>DXCaptureRing</c>, which may stall because a recording must not lose frames: a profiler
/// that blocks would be measuring itself. A steady state never drops anything, because four slots cover the two frames the
/// compositor keeps in flight.</para>
///
/// <para><b>Its own fence.</b> <see cref="OnSubmitted"/> signals a fence created here, never the swap chain ring
/// (<c>Device.Fence</c>/<c>fenceValues</c>). The ring's completed value is what <c>MoveToNextFrame</c> and the deferred
/// release queue rely on to decide "the GPU is done with frame N"; signalling arbitrary values on it - which is exactly
/// what a per-frame profiling counter does - would make those waits return early. Same reasoning, same separate fence as
/// <c>TransitionFence</c> and <c>UploadFence</c>.</para>
///
/// <para><b>Never a new crash site.</b> Every entry point swallows its failure once and goes <see cref="Available"/>-false.
/// The project has already paid for TDR cascades; a development-time measurement tool has no business being the thing that
/// turns a device removal into an unhandled exception from three call sites deep.</para>
///
/// <para>Threading: render thread only, and every member is called in submission order.</para>
/// </summary>
internal sealed unsafe class DXGpuFrameTimer : IGpuFrameTimer
{
    /// <summary>
    /// Ticks accepted per frame. Two marks per interval plus the frame's own pair, so this covers
    /// <see cref="Season.Rendering.FrameProfiler.MaxIntervals"/> with room to spare; the shared layer stops asking long
    /// before the heap is full, and this is the second line of defence.
    /// </summary>
    internal const int Marks = 512;

    /// <summary>
    /// Readback slots. Four is the shallowest ring that still has a free buffer while the compositor holds two frames, so
    /// a steady state drops nothing; making it deeper would only delay the numbers.
    /// </summary>
    const int SlotCount = 4;

    const int SlotBytes = Marks * 8;

    readonly ID3D12CommandQueue* _queue;

    readonly ID3D12QueryHeap* _heap;

    readonly ID3D12Fence* _fence;

    readonly ID3D12Resource*[] _readbacks = new ID3D12Resource*[SlotCount];

    /// <summary>Fence value guarding each slot; 0 means the slot is free.</summary>
    readonly ulong[] _slotValues = new ulong[SlotCount];

    readonly long[] _slotTokens = new long[SlotCount];

    readonly int[] _slotMarks = new int[SlotCount];

    /// <summary>Managed copy handed out by <see cref="TryTake"/>; valid until the next call, as the interface promises.</summary>
    readonly ulong[] _copy = new ulong[Marks];

    long _fenceValue;

    int _cursor;

    int _marks;

    int _pendingSlot = -1;

    int _pendingMarks;

    long _pendingToken;

    bool _broken;

    bool _disposed;

    double _nanosecondsPerTick;

    internal DXGpuFrameTimer(ID3D12Device* device, ID3D12CommandQueue* queue)
    {
        _queue = queue;

        ulong frequency = 0;

        if (queue->GetTimestampFrequency(&frequency) != 0 || frequency == 0)
            throw new InvalidOperationException("This command queue cannot timestamp: GetTimestampFrequency failed.");

        _nanosecondsPerTick = 1e9 / frequency;

        var heapDesc = new QueryHeapDesc
        {
            Type = QueryHeapType.Timestamp,
            Count = Marks,
            NodeMask = 0,
        };

        ID3D12QueryHeap* heap;
        var iid = ID3D12QueryHeap.Guid;

        int result = device->CreateQueryHeap(&heapDesc, &iid, (void**)&heap);

        if (result != 0 || heap == null)
            throw new InvalidOperationException($"CreateQueryHeap(Timestamp) failed: HRESULT=0x{result:X8}");

        _heap = heap;

        var heapProperties = new HeapProperties(HeapType.Readback);

        var bufferDesc = new ResourceDesc(
            ResourceDimension.Buffer,
            0,
            SlotBytes,
            1, 1, 1,
            Format.FormatUnknown,
            new SampleDesc(1, 0),
            TextureLayout.LayoutRowMajor,
            ResourceFlags.None);

        for (int i = 0; i < SlotCount; i++)
        {
            ID3D12Resource* resource = null;
            var iidResource = ID3D12Resource.Guid;

            // A READBACK buffer is created in CopyDest and never transitions, so ResolveQueryData can target it any frame
            // without a barrier - the same property DXCaptureRing's slots rely on.
            int hr = device->CreateCommittedResource(&heapProperties, HeapFlags.None, &bufferDesc,
                ResourceStates.CopyDest, null, &iidResource, (void**)&resource);

            if (hr != 0 || resource == null)
                throw new InvalidOperationException($"Timestamp readback buffer {i} failed: HRESULT=0x{hr:X8}");

            _readbacks[i] = resource;
        }

        ID3D12Fence* fence;
        var iidFence = ID3D12Fence.Guid;

        result = device->CreateFence(0, FenceFlags.None, &iidFence, (void**)&fence);

        if (result != 0 || fence == null)
            throw new InvalidOperationException($"CreateFence (profiler) failed: HRESULT=0x{result:X8}");

        _fence = fence;
    }

    public bool Available => !_broken && !_disposed;

    public double NanosecondsPerTick => _nanosecondsPerTick;

    public int Capacity => Marks;

    public void BeginFrame(long token)
    {
        if (_broken) return;

        _marks = 0;
        _pendingSlot = -1;
        _pendingToken = token;
    }

    public int Mark()
    {
        if (_broken) return -1;

        if (_marks >= Marks) return -1;

        try
        {
            Device.GraphicsCommandList->EndQuery(_heap, QueryType.Timestamp, (uint)_marks);
        }
        catch (Exception)
        {
            Break();

            return -1;
        }

        return _marks++;
    }

    public void EndFrame()
    {
        _pendingSlot = -1;

        if (_broken || _marks == 0) return;

        try
        {
            ulong completed = _fence->GetCompletedValue();

            // Pick a slot whose previous round has landed. A busy slot means the CPU is four frames ahead of the GPU,
            // which is already a timing artefact worth not measuring: drop the frame rather than change its pacing.
            for (int i = 0; i < SlotCount; i++)
            {
                int slot = (_cursor + i) % SlotCount;

                if (_slotValues[slot] != 0 && completed < _slotValues[slot]) continue;

                _slotValues[slot] = 0;
                _slotMarks[slot] = _marks;
                _cursor = (slot + 1) % SlotCount;

                Device.GraphicsCommandList->ResolveQueryData(_heap, QueryType.Timestamp, 0, (uint)_marks,
                    _readbacks[slot], 0);

                _pendingSlot = slot;
                _pendingMarks = _marks;

                return;
            }
        }
        catch (Exception)
        {
            Break();

            return;
        }

        // Every slot is still in flight: the CPU is four frames ahead of the GPU. That is a stutter, not a failure, so
        // the frame is simply dropped and the next one starts clean - breaking here would end GPU timing for the rest of
        // the process on a single hiccup. No resolve is pending, so no fence value was reserved and the shared layer
        // publishes this frame with its GPU column unknown.
    }

    public void OnSubmitted()
    {
        if (_broken || _pendingSlot < 0) return;

        int slot = _pendingSlot;

        _pendingSlot = -1;

        try
        {
            ulong value = (ulong)Interlocked.Increment(ref _fenceValue);

            int hr = Device.CommandQueue->Signal(_fence, value);

            if (hr != 0)
            {
                Break();

                return;
            }

            _slotValues[slot] = value;
            _slotTokens[slot] = _pendingToken;
            _slotMarks[slot] = _pendingMarks;
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

        int slot = -1;
        ulong best = 0;

        try
        {
            ulong completed = _fence->GetCompletedValue();

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

            void* mapped;

            if (_readbacks[slot]->Map(0, null, &mapped) != 0)
            {
                _slotValues[slot] = 0;

                return false;
            }

            try
            {
                new ReadOnlySpan<ulong>(mapped, marks).CopyTo(_copy);
            }
            finally
            {
                _readbacks[slot]->Unmap(0, null);
            }

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

    void Break()
    {
        _broken = true;
        _pendingSlot = -1;
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _broken = true;
        _pendingSlot = -1;

        for (int i = 0; i < SlotCount; i++)
        {
            var resource = _readbacks[i];

            if (resource == null) continue;

            _readbacks[i] = null;

            try
            {
                // Retire through the deferred queue: a resolve may still be writing into this buffer. The shared layer
                // releases the timer when it stops profiling, which can happen mid-frame.
                var pending = (IntPtr)resource;

                Device.EnqueueDeferredRelease(Device.GetCurrentRetireFenceValue(),
                    () => ((ID3D12Resource*)pending)->Release());
            }
            catch (Exception)
            {
                resource->Release();
            }
        }

        if (_heap != null) _heap->Release();
        if (_fence != null) _fence->Release();
    }
}
