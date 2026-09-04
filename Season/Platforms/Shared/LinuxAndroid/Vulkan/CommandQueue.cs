// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Vulkan;

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

/// <summary>
/// Vulkan command queue aligned with DX12 CommandQueue:
/// wraps a VkQueue plus one timeline VkSemaphore,
/// providing monotonic fence semantics through Signal(value) and WaitForFence(value).
/// It does not own a Fence or FenceEvent, and the single timeline semaphore serves as the equivalent object.
/// </summary>
internal unsafe sealed class CommandQueue : IDisposable
{
    readonly Vk _vk;

    readonly Silk.NET.Vulkan.Device _device;

    public Silk.NET.Vulkan.Queue NativeQueue { get; }

    public uint QueueFamily { get; }

    public Silk.NET.Vulkan.Semaphore TimelineSemaphore => _semaphore;

    Silk.NET.Vulkan.Semaphore _semaphore;

    public CommandQueue(Vk vk, Silk.NET.Vulkan.Device device, Silk.NET.Vulkan.Queue queue, uint family)
    {
        _vk = vk;
        _device = device;
        NativeQueue = queue;
        QueueFamily = family;

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
    /// GPU-side signal:
    /// submit an empty SubmitInfo and notify through the timeline semaphore only when the GPU advances to the target value.
    /// </summary>
    public void Signal(ulong value)
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
    }

    /// <summary>Block on the CPU until the timeline semaphore reaches a value greater than or equal to the target.</summary>
    public void WaitForFence(ulong value)
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
    }

    /// <summary>Read the current GPU progress value of the timeline semaphore, equivalent to ID3D12Fence::GetCompletedValue.</summary>
    public ulong GetCompletedValue()
    {
        _vk.GetSemaphoreCounterValue(_device, _semaphore, out ulong v);
        return v;
    }

    public void WaitIdle() => _vk.QueueWaitIdle(NativeQueue);

    public void Dispose()
    {
        if (_semaphore.Handle != 0)
        {
            _vk.DestroySemaphore(_device, _semaphore, null);
            _semaphore = default;
        }
    }
}
