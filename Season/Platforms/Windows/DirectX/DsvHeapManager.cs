// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Direct3D12;

namespace Season.Platforms.Windows.DirectX;

internal unsafe sealed class DsvHeapManager
{
    private readonly ID3D12Device* _device;

    internal ID3D12DescriptorHeap* Heap { get; private set; }

    internal uint DescriptorSize { get; private set; }

    public DsvHeapManager(ID3D12Device* device)
    {
        _device = device;
    }

    /// <summary>
    /// Initializes the DSV descriptor heap.
    /// </summary>
    /// <param name="numDescriptors">Number of descriptors, usually 1 for the
    /// depth-stencil buffer.</param>
    public void InitializeHeap(uint numDescriptors = 1)
    {
        var desc = new DescriptorHeapDesc
        {
            Type = DescriptorHeapType.Dsv,
            NumDescriptors = numDescriptors,
            Flags = DescriptorHeapFlags.None,
            NodeMask = 0
        };

        ID3D12DescriptorHeap* heap;
        var iid = ID3D12DescriptorHeap.Guid;
        var hr = _device->CreateDescriptorHeap(&desc, &iid, (void**)&heap);
        var ex = Marshal.GetExceptionForHR(hr);
        if (ex != null)
        {
            throw ex;
        }

        Heap = heap;
        DescriptorSize = _device->GetDescriptorHandleIncrementSize(DescriptorHeapType.Dsv);
    }

    /// <summary>
    /// Gets the CPU descriptor handle at the start of the heap
    /// (DSV heaps usually contain only one descriptor).
    /// </summary>
    internal CpuDescriptorHandle GetCpuHandle()
    {
        return Heap->GetCPUDescriptorHandleForHeapStart();
    }

    /// <summary>
    /// Gets the CPU descriptor handle at the specified index.
    /// </summary>
    internal CpuDescriptorHandle GetCpuHandle(uint index)
    {
        var handle = Heap->GetCPUDescriptorHandleForHeapStart();
        handle.Ptr += index * DescriptorSize;
        return handle;
    }
}
