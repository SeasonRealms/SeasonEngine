// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Vulkan;

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

/// <summary>
/// Vulkan command queue aligned with DX12 CommandQueue:
/// wraps a VkQueue and provides monotonic fence semantics through Signal(value), WaitForFence(value)
/// and GetCompletedValue(). Two synchronization backends keep that contract identical:
/// - Timeline mode (core 1.2 device): a single timeline VkSemaphore carries the values, exactly the
///   DX fence pattern, and doubles as the queue's fence object.
/// - Fence mode (1.1 fallback, driven by Device.TimelineSemaphoreEnabled): each value is carried by a
///   VkFence registered through PrepareSubmit(value) before the submission, then recycled once observed
///   signaled.
/// Threading contract: waits hold the internal lock while blocking, so a fence being waited on can
/// never be retired and reused by another thread. For the same reason vkQueueSubmit must stay outside
/// the lock - callers obtain the fence from PrepareSubmit first and submit afterwards - otherwise a
/// concurrent WaitForFence holding the lock would deadlock against a submitter blocked on it.
/// </summary>
internal unsafe sealed class CommandQueue : IDisposable
{
    readonly Vk _vk;

    readonly Silk.NET.Vulkan.Device _device;

    public Silk.NET.Vulkan.Queue NativeQueue { get; }

    public uint QueueFamily { get; }

    /// <summary>Whether completion values are tracked by the timeline semaphore (true) or by VkFence pairs (false). Set once in the constructor from Device.TimelineSemaphoreEnabled.</summary>
    public bool TimelineMode { get; }

    public Silk.NET.Vulkan.Semaphore TimelineSemaphore => _semaphore;

    Silk.NET.Vulkan.Semaphore _semaphore;

    // ===== Fence-mode state, all guarded by _sync =====

    readonly object _sync = new();

    /// <summary>Submitted values whose owning fence has not been observed as signaled yet, in submission order (lowest value first).</summary>
    readonly List<(ulong Value, Fence Fence)> _pending = new();

    /// <summary>Fences whose submission has already been observed as complete, available for reuse by a later PrepareSubmit.</summary>
    readonly Stack<Fence> _recycled = new();

    /// <summary>Highest completion value observed so far. Advances monotonically as pending entries retire.</summary>
    ulong _completed;

    public CommandQueue(Vk vk, Silk.NET.Vulkan.Device device, Silk.NET.Vulkan.Queue queue, uint family)
    {
        _vk = vk;
        _device = device;
        NativeQueue = queue;
        QueueFamily = family;

        // The device-level probe decides once for the whole process; see Device.TimelineSemaphoreEnabled.
        TimelineMode = Device.TimelineSemaphoreEnabled;
        if (!TimelineMode)
            return;

        var typeInfo = new SemaphoreTypeCreateInfo
        {
            SType = StructureType.SemaphoreTypeCreateInfo,
            SemaphoreType = SemaphoreType.Timeline,
            InitialValue = 0
        };

        var info = new SemaphoreCreateInfo
        {
            SType = StructureType.SemaphoreCreateInfo,
            PNext = &typeInfo
        };

        if (vk.CreateSemaphore(device, in info, null, out _semaphore) != Result.Success)
            throw new Exception("vkCreateSemaphore (timeline) failed");
    }

    /// <summary>
    /// Fence-mode only: reserve the VkFence that will carry the submit's completion value and register
    /// (value, fence), then return the fence so the caller passes it to vkQueueSubmit. Registering
    /// before the submission is what keeps a concurrent WaitForFence(value) from observing a submission
    /// it cannot yet wait on. In timeline mode this is unnecessary and returns default.
    /// </summary>
    public Fence PrepareSubmit(ulong value)
    {
        if (TimelineMode)
            return default;

        lock (_sync)
        {
            Fence fence;
            if (_recycled.Count > 0)
            {
                fence = _recycled.Pop();
                // The recycled fence was observed signaled, so it must be reset first: vkQueueSubmit
                // requires the fence to be unsignaled at submission time.
                if (_vk.ResetFences(_device, 1, &fence) != Result.Success)
                    throw new Exception("vkResetFences failed");
            }
            else
            {
                var info = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
                if (_vk.CreateFence(_device, in info, null, out fence) != Result.Success)
                    throw new Exception("vkCreateFence failed");
            }

            _pending.Add((value, fence));
            return fence;
        }
    }

    /// <summary>
    /// GPU-side signal:
    /// submit an empty SubmitInfo and notify through the queue's fence object only when the GPU advances
    /// to the target value - the timeline semaphore on 1.2 devices, a registered VkFence on the fallback.
    /// </summary>
    public void Signal(ulong value)
    {
        if (TimelineMode)
        {
            var sem = _semaphore;
            var timelineInfo = new TimelineSemaphoreSubmitInfo
            {
                SType = StructureType.TimelineSemaphoreSubmitInfo,
                SignalSemaphoreValueCount = 1,
                PSignalSemaphoreValues = &value
            };

            var submit = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                PNext = &timelineInfo,
                SignalSemaphoreCount = 1,
                PSignalSemaphores = &sem
            };

            if (_vk.QueueSubmit(NativeQueue, 1, in submit, default) != Result.Success)
                throw new Exception("vkQueueSubmit (signal) failed");
            return;
        }

        var emptySubmit = new SubmitInfo { SType = StructureType.SubmitInfo };
        var fence = PrepareSubmit(value);
        if (_vk.QueueSubmit(NativeQueue, 1, in emptySubmit, fence) != Result.Success)
            throw new Exception("vkQueueSubmit (signal) failed");
    }

    /// <summary>
    /// Block the calling thread until the queue's completion value reaches the target.
    /// Timeline mode waits on vkWaitSemaphores. Fence mode blocks on the oldest outstanding fence and
    /// then re-checks the watermark, so completion is observed strictly in submission order and an
    /// out-of-order completion further down the list can never satisfy the wait early. A target that no
    /// pending entry covers means everything issued is already complete or it was never issued, and the
    /// wait returns instead of blocking forever.
    /// </summary>
    public void WaitForFence(ulong value)
    {
        if (TimelineMode)
        {
            var sem = _semaphore;
            var waitInfo = new SemaphoreWaitInfo
            {
                SType = StructureType.SemaphoreWaitInfo,
                SemaphoreCount = 1,
                PSemaphores = &sem,
                PValues = &value
            };
            _vk.WaitSemaphores(_device, in waitInfo, ulong.MaxValue);
            return;
        }

        lock (_sync)
        {
            while (true)
            {
                DrainCompletedLocked();
                if (_completed >= value || _pending.Count == 0)
                    return;

                var target = _pending[0].Fence;
                _vk.WaitForFences(_device, 1, &target, Vk.True, ulong.MaxValue);
            }
        }
    }

    /// <summary>Read the queue's current GPU progress value, equivalent to ID3D12Fence::GetCompletedValue.</summary>
    public ulong GetCompletedValue()
    {
        if (TimelineMode)
        {
            _vk.GetSemaphoreCounterValue(_device, _semaphore, out ulong v);
            return v;
        }

        lock (_sync)
        {
            DrainCompletedLocked();
            return _completed;
        }
    }

    /// <summary>
    /// Fence-mode bookkeeping, called with _sync held: retire the head of _pending while its fence is
    /// signaled. Entries are registered in submission order, so the watermark advances monotonically
    /// and never skips over a submission that is still running.
    /// </summary>
    void DrainCompletedLocked()
    {
        while (_pending.Count > 0)
        {
            var head = _pending[0];
            if (_vk.GetFenceStatus(_device, head.Fence) != Result.Success)
                break;

            if (head.Value > _completed)
                _completed = head.Value;
            _recycled.Push(head.Fence);
            _pending.RemoveAt(0);
        }
    }

    public void WaitIdle() => _vk.QueueWaitIdle(NativeQueue);

    public void Dispose()
    {
        if (TimelineMode)
        {
            if (_semaphore.Handle != 0)
            {
                _vk.DestroySemaphore(_device, _semaphore, null);
                _semaphore = default;
            }
            return;
        }

        lock (_sync)
        {
            foreach (var entry in _pending)
                _vk.DestroyFence(_device, entry.Fence, null);
            _pending.Clear();

            while (_recycled.Count > 0)
                _vk.DestroyFence(_device, _recycled.Pop(), null);
        }
    }
}
