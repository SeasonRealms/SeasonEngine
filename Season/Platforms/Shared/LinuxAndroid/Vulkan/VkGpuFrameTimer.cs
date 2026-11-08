// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Vulkan;

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

/// <summary>
/// Contract 3-1: the Vulkan half of frame profiling - one timestamp query pool, a four-slot host-visible readback ring
/// and the swap chain's own completion value. One implementation serves both Linux and Android: the two targets reach
/// the device through the same <see cref="Device"/> and differ only in driver-reported limits, which are read from
/// <see cref="PhysicalDevice"/> at construction rather than compiled per platform.
///
/// <para><b>Marks, not spans.</b> <see cref="Mark"/> only appends <c>vkCmdWriteTimestamp</c> to the frame's command
/// buffer. The copy is decided at <see cref="EndFrame"/>, once the number of marks is known, and resolves into one of
/// four independent host-visible buffers. Nothing here interprets the ticks: the shared layer subtracts them.</para>
///
/// <para><b>One reset per frame, never per query.</b> <c>vkCmdWriteTimestamp</c> requires the query to be unavailable
/// first (<c>VUID-vkCmdWriteTimestamp-None-00830</c>), which is the one place Vulkan differs from D3D12's implicit
/// EndQuery reset. A single <c>vkCmdResetQueryPool</c> at the frame head covers the whole pool, and every index a frame
/// copies is one the same frame just wrote - so a dropped frame is harmless: nothing reads an index it did not write.</para>
///
/// <para><b>Never signals anything.</b> Unlike D3D12 there is nothing to signal here: the frame's submission already
/// carries a timeline value (<see cref="Device.LastSubmittedFrameValue"/>), and reading it is what
/// <see cref="OnSubmitted"/> does. Signalling a value of our own would mean an extra <c>vkQueueSubmit</c> per frame, and
/// that submission would be the profiler measuring itself - a driver flush on every frame for a number nobody reads
/// sooner.</para>
///
/// <para><b>A slot is only reused once the GPU has finished with it.</b> <see cref="EndFrame"/> checks the slot's
/// completion value before recording the copy. If the slot is still busy the whole frame is dropped and counted - never
/// waited for. That is the opposite trade-off from the capture ring, which may stall because a recording must not lose
/// frames: a profiler that blocks would be measuring itself.</para>
///
/// <para><b>timestampValidBits.</b> A family may report fewer than 64 valid bits (32 is common on mobile). Ticks are
/// masked to that width on readback, so the shared layer's monotonicity check is comparing real values instead of
/// high-order garbage. A wrap inside one frame is not handled - at 32 bits it happens once every few seconds, and the
/// shared layer already drops a non-monotonic pair rather than reporting a bogus span.</para>
///
/// <para><b>Never a new crash site.</b> Every entry point swallows its failure once and goes <see cref="Available"/>-false,
/// exactly like the D3D12 timer. A development-time measurement tool has no business turning a lost device into an
/// unhandled exception from inside the frame loop.</para>
///
/// <para>Threading: render thread only, and every member is called in submission order.</para>
/// </summary>
internal sealed unsafe class VkGpuFrameTimer : IGpuFrameTimer
{
    /// <summary>
    /// Ticks accepted per frame. Two marks per interval plus the frame's own pair, so this covers
    /// <see cref="Season.Rendering.FrameProfiler.MaxIntervals"/> with room to spare; the shared layer stops asking long
    /// before the pool is full, and this is the second line of defence. Same value as the D3D12 timer so the two
    /// backends drop frames under identical pressure.
    /// </summary>
    internal const int Marks = 512;

    /// <summary>
    /// Readback slots. Four is the shallowest ring that still has a free buffer while the compositor holds two frames, so
    /// a steady state drops nothing; making it deeper would only delay the numbers.
    /// </summary>
    const int SlotCount = 4;

    const ulong SlotBytes = Marks * 8;

    readonly QueryPool _pool;

    /// <summary>One host-visible buffer per slot; the copy targets a different buffer each round.</summary>
    readonly BufferResource[] _slots = new BufferResource[SlotCount];

    /// <summary>Persistent maps, one per slot. Mapping once keeps the readback path to a span copy and no Vk call.</summary>
    readonly nint[] _mapped = new nint[SlotCount];

    /// <summary>Frame completion value guarding each slot; 0 means the slot is free.</summary>
    readonly ulong[] _slotValues = new ulong[SlotCount];

    readonly long[] _slotTokens = new long[SlotCount];

    readonly int[] _slotMarks = new int[SlotCount];

    /// <summary>Managed copy handed out by <see cref="TryTake"/>; valid until the next call, as the interface promises.</summary>
    readonly ulong[] _copy = new ulong[Marks];

    /// <summary>Mask for the family's timestampValidBits; 0 when the full 64 bits are valid.</summary>
    readonly ulong _tickMask;

    readonly double _nanosecondsPerTick;

    int _cursor;

    int _marks;

    int _pendingSlot = -1;

    int _pendingMarks;

    long _pendingToken;

    bool _broken;

    bool _disposed;

    internal VkGpuFrameTimer()
    {
        var vk = Device.Vk;
        var device = Device.LogicalDevice;
        var physical = Device.PhysicalDevice;

        vk.GetPhysicalDeviceProperties(physical, out var props);

        // Vulkan reports the period directly in nanoseconds per tick, unlike D3D12's frequency in Hz.
        float period = props.Limits.TimestampPeriod;

        if (period <= 0f)
            throw new InvalidOperationException("The device reports no timestamp period; GPU frame timing is unavailable.");

        _nanosecondsPerTick = period;

        uint validBits = ReadTimestampValidBits(physical, Device.GraphicsQueueFamily);

        // 0 means this family cannot write timestamps at all. Asking the shared layer to publish a GPU column it will
        // never receive is worse than answering null here: the profiler then reports "--" for every GPU number, which
        // is a measurement rather than a fallback zero.
        if (validBits == 0)
            throw new InvalidOperationException("The graphics queue family cannot write timestamps; GPU frame timing is unavailable.");

        _tickMask = validBits >= 64 ? 0UL : (1UL << (int)validBits) - 1;

        try
        {
            var poolInfo = new QueryPoolCreateInfo
            {
                SType = StructureType.QueryPoolCreateInfo,
                QueryType = QueryType.Timestamp,
                QueryCount = Marks
            };

            if (vk.CreateQueryPool(device, in poolInfo, null, out QueryPool pool) != Result.Success)
                throw new InvalidOperationException("vkCreateQueryPool(Timestamp) failed.");

            _pool = pool;

            for (int i = 0; i < SlotCount; i++)
            {
                // TRANSFER_DST because the copy is the only writer, HOST_VISIBLE so the result can be read without a
                // second staging hop. HOST_COHERENT is requested as well: every implementation in the field exposes
                // one such type, and requiring it keeps the readback free of a manual invalidate.
                _slots[i] = Device.ResourceManager.CreateBuffer(SlotBytes,
                    BufferUsageFlags.TransferDstBit,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

                void* mapped;

                if (vk.MapMemory(device, _slots[i].Memory, 0, SlotBytes, 0, &mapped) != Result.Success)
                    throw new InvalidOperationException($"vkMapMemory (timestamp readback {i}) failed.");

                _mapped[i] = (nint)mapped;
            }
        }
        catch
        {
            // Nothing has been submitted against these objects yet, so they can go immediately.
            ReleaseNow();
            throw;
        }
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

        // VUID-vkCmdWriteTimestamp-None-00830: every query a write touches must be unavailable first. D3D12's EndQuery
        // resets implicitly; Vulkan's does not, so the pool is reset once per frame at the frame head. This has to sit
        // outside a render pass - the frame boundary already is - and the previous frame's copy has consumed the old
        // values before this reset executes, because both are recorded into the same queue in order.
        if (!Device.IsRecording) return;

        try
        {
            Device.Vk.CmdResetQueryPool(Device.GraphicsCommandBuffer, _pool, 0, Marks);
        }
        catch (Exception)
        {
            Break();
        }
    }

    public int Mark()
    {
        if (_broken) return -1;

        if (_marks >= Marks) return -1;

        // Writing into a command buffer between vkEndCommandBuffer and the next vkBeginCommandBuffer is undefined
        // behaviour, and Device.IsRecording is the one flag that knows the difference (a handle check cannot).
        if (!Device.IsRecording) return -1;

        try
        {
            // Bottom-of-pipe: the timestamp lands after everything recorded so far, which is the same point a D3D12
            // EndQuery(Timestamp) resolves to. Every mark the shared layer writes sits outside a render pass - pass and
            // compute boundaries are scheduled around it - so no in-pass stage restriction applies.
            Device.Vk.CmdWriteTimestamp(Device.GraphicsCommandBuffer, PipelineStageFlags.BottomOfPipeBit, _pool, (uint)_marks);
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

        // The frame buffer already closed (the frame threw before AfterRender): there is nowhere left to record the
        // copy, so this frame is dropped and the next one starts clean.
        if (!Device.IsRecording) return;

        try
        {
            ulong completed = Device.GraphicsCommandQueue.GetCompletedValue();

            // Pick a slot whose previous round has landed. A busy slot means the CPU is four frames ahead of the GPU,
            // which is already a timing artefact worth not measuring: drop the frame rather than change its pacing.
            for (int i = 0; i < SlotCount; i++)
            {
                int slot = (_cursor + i) % SlotCount;

                if (_slotValues[slot] != 0 && completed < _slotValues[slot]) continue;

                _slotValues[slot] = 0;
                _slotMarks[slot] = _marks;
                _cursor = (slot + 1) % SlotCount;

                // RESULT_WAIT is a GPU-side wait, not a CPU one: it tells the copy to hold until the timestamps ahead of
                // it in this same buffer have landed, which the driver is already sequencing anyway.
                Device.Vk.CmdCopyQueryPoolResults(Device.GraphicsCommandBuffer, _pool, 0, (uint)_marks,
                    _slots[slot].Buffer, 0, 8,
                    QueryResultFlags.Result64Bit | QueryResultFlags.ResultWaitBit);

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
        // the process on a single hiccup.
    }

    public void OnSubmitted()
    {
        if (_broken || _pendingSlot < 0) return;

        int slot = _pendingSlot;

        _pendingSlot = -1;

        try
        {
            // The frame's own submission already signals this value on the swap-chain timeline. Recording it here means
            // "this timer is done with slot N once the GPU reaches this frame", with no extra submission spent on it.
            ulong value = Device.LastSubmittedFrameValue;

            if (value == 0) return;

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
            ulong completed = Device.GraphicsCommandQueue.GetCompletedValue();

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

            new ReadOnlySpan<ulong>((void*)_mapped[slot], marks).CopyTo(_copy);

            if (_tickMask != 0)
            {
                for (int i = 0; i < marks; i++) _copy[i] &= _tickMask;
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

    /// <summary>Immediate destruction, valid only when nothing was ever submitted against these objects.</summary>
    void ReleaseNow()
    {
        var vk = Device.Vk;
        var device = Device.LogicalDevice;

        for (int i = 0; i < SlotCount; i++)
        {
            if (_mapped[i] != 0)
            {
                try { vk.UnmapMemory(device, _slots[i].Memory); } catch { /* already unusable */ }
                _mapped[i] = 0;
            }

            if (_slots[i].Buffer.Handle != 0)
            {
                try { vk.DestroyBuffer(device, _slots[i].Buffer, null); } catch { /* already unusable */ }
                _slots[i].Buffer = default;
            }

            if (_slots[i].Memory.Handle != 0)
            {
                try { vk.FreeMemory(device, _slots[i].Memory, null); } catch { /* already unusable */ }
                _slots[i].Memory = default;
            }
        }

        if (_pool.Handle != 0)
        {
            try { vk.DestroyQueryPool(device, _pool, null); } catch { /* already unusable */ }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _broken = true;
        _pendingSlot = -1;

        if (_pool.Handle == 0) return;

        var vk = Device.Vk;
        var device = Device.LogicalDevice;

        for (int i = 0; i < SlotCount; i++)
        {
            if (_mapped[i] == 0) continue;

            // Unmapping is CPU-side bookkeeping; the GPU's view of the memory is unaffected.
            try { vk.UnmapMemory(device, _slots[i].Memory); } catch { /* the device is already gone */ }
            _mapped[i] = 0;
        }

        var slots = (BufferResource[])_slots.Clone();
        var pool = _pool;

        try
        {
            // The last submitted frame may still have a copy in flight writing into these buffers. Retire behind the
            // timeline, the same rule every other runtime destruction follows on this backend (see Device.cs).
            Device.EnqueueDeferredRelease(() =>
            {
                foreach (var slot in slots)
                {
                    if (slot.Buffer.Handle != 0) vk.DestroyBuffer(device, slot.Buffer, null);
                    if (slot.Memory.Handle != 0) vk.FreeMemory(device, slot.Memory, null);
                }

                if (pool.Handle != 0) vk.DestroyQueryPool(device, pool, null);
            });
        }
        catch (Exception)
        {
            // The device is already torn down and there is no timeline left to wait on.
            ReleaseNow();
        }
    }

    /// <summary>
    /// Reads the graphics family's timestampValidBits. This is a queue-family property rather than a device limit, so it
    /// cannot ride on the same <c>GetPhysicalDeviceProperties</c> call as the timestamp period above.
    /// </summary>
    static uint ReadTimestampValidBits(PhysicalDevice physical, uint family)
    {
        var vk = Device.Vk;

        uint count = 0;
        vk.GetPhysicalDeviceQueueFamilyProperties(physical, ref count, null);

        if (family >= count) return 0;

        var families = new QueueFamilyProperties[count];

        fixed (QueueFamilyProperties* p = families)
            vk.GetPhysicalDeviceQueueFamilyProperties(physical, ref count, p);

        return families[(int)family].TimestampValidBits;
    }
}
