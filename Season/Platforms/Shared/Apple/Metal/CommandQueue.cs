// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Metal;

namespace Season.Platforms.Shared.Apple.Metal;

/// <summary>
/// Aligns with the DX12 and Vulkan CommandQueue implementations by wrapping IMTLCommandQueue.
/// It provides command-buffer allocation plus a monotonically increasing completed counter,
/// accumulated through IMTLCommandBuffer.AddCompletedHandler.
/// Frame-concurrency throttling is handled by Device.InFlight via SemaphoreSlim,
/// and GetCompletedValue is used only for lightweight waits such as upload completion sync.
/// </summary>
internal sealed class CommandQueue : IDisposable
{
    public IMTLCommandQueue NativeQueue { get; }

    long _completedValue;

    long _signalCounter;

    public CommandQueue(IMTLDevice device)
    {
        NativeQueue = device.CreateCommandQueue() ?? throw new Exception("IMTLDevice.CreateCommandQueue returned null");
    }

    /// <summary>Allocates a new IMTLCommandBuffer.</summary>
    public IMTLCommandBuffer CreateCommandBuffer()
    {
        var cmd = NativeQueue.CommandBuffer() ?? throw new Exception("IMTLCommandQueue.CommandBuffer returned null");
        return cmd;
    }

    /// <summary>Reads the cumulative count of completed command buffers, matching ID3D12Fence.GetCompletedValue and VkSemaphoreCounterValue.</summary>
    public ulong GetCompletedValue() => (ulong)Interlocked.Read(ref _completedValue);

    /// <summary>
    /// Registers a completion callback on cmdBuffer.
    /// It allocates the next monotonic value and advances the completed value when the GPU finishes.
    /// CAS is used to prevent completedValue from moving backward when multiple frames complete out of order.
    /// </summary>
    public ulong RegisterSignal(IMTLCommandBuffer cmd, Action? onSignal = null)
    {
        ulong target = (ulong)Interlocked.Increment(ref _signalCounter);
        cmd.AddCompletedHandler(_ =>
        {
            long cur;
            do { cur = Interlocked.Read(ref _completedValue); }
            while (cur < (long)target && Interlocked.CompareExchange(ref _completedValue, (long)target, cur) != cur);
            onSignal?.Invoke();
        });
        return target;
    }

    /// <summary>Spin-waits on the CPU until completedValue is greater than or equal to value. Intended only for lightweight waits such as upload synchronization.</summary>
    public void WaitForFence(ulong value)
    {
        var sw = new SpinWait();
        while (GetCompletedValue() < value) sw.SpinOnce();
    }

    public void Dispose()
    {
        NativeQueue?.Dispose();
    }
}
