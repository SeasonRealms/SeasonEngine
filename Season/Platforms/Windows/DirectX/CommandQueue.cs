// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

namespace Season.Platforms.Windows.DirectX;

internal unsafe sealed class CommandQueue : IDisposable
{
    private readonly ID3D12Device* _device;

    private readonly ID3D12CommandQueue* _queue;

    private readonly ID3D12Fence* _fence;

    private readonly IntPtr _fenceEvent;

    public ID3D12CommandQueue* NativeQueue => _queue;

    public ID3D12Fence* Fence => _fence;

    public IntPtr FenceEvent => _fenceEvent;

    public CommandQueue(ID3D12Device* device, CommandListType type, CommandQueueFlags flags = CommandQueueFlags.None)
    {
        _device = device;

        var queueDesc = new CommandQueueDesc
        {
            Type = type,
            Priority = (int)CommandQueuePriority.Normal,
            Flags = flags,
            NodeMask = 0
        };

        ID3D12CommandQueue* queue;
        var iid = ID3D12CommandQueue.Guid;
        var hr = device->CreateCommandQueue(&queueDesc, &iid, (void**)&queue);
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);
        _queue = queue;

        ID3D12Fence* fence;
        iid = ID3D12Fence.Guid;
        hr = device->CreateFence(0, FenceFlags.None, &iid, (void**)&fence);
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);
        _fence = fence;

        _fenceEvent = SilkMarshal.CreateWindowsEvent(null, false, false, null);
        if (_fenceEvent == IntPtr.Zero)
        {
            Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
        }
    }

    public void Signal(ulong fenceValue)
    {
        var hr = _queue->Signal(_fence, fenceValue);
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);
    }

    public void WaitForFence(ulong fenceValue)
    {
        // Use a poll + short-wait loop instead of a single WaitWindowsObjects call.
        // A single wait can observe a spurious wakeup if SetEventOnCompletion is
        // overwritten by another thread, causing callers (for example
        // HandleResize -> WaitForGpu) to assume the GPU is done too early and touch
        // command allocators prematurely, which then triggers SEHException.
        while (_fence->GetCompletedValue() < fenceValue)
        {
            var hr = _fence->SetEventOnCompletion(fenceValue, _fenceEvent.ToPointer());
            if (hr != 0) Marshal.ThrowExceptionForHR(hr);
            SilkMarshal.WaitWindowsObjects(_fenceEvent);
        }
    }

    public void Dispose()
    {
        if (_queue != null) _queue->Release();
        if (_fence != null) _fence->Release();
        // Note: SilkMarshal doesn't have a CloseEvent, usually CloseHandle is used via PInvoke if needed.
    }
}
