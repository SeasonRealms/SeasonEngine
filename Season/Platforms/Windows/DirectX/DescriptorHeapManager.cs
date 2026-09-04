// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Direct3D12;

namespace Season.Platforms.Windows.DirectX;

/// <summary>
/// Manages the D3D12 SRV descriptor heap and its stride, and provides helpers
/// to compute CPU/GPU handles by index.
/// </summary>
internal unsafe sealed class DescriptorHeapManager
{
    private readonly ID3D12Device* _device;

    internal ID3D12DescriptorHeap* Heap { get; private set; }

    internal uint DescriptorSize { get; private set; }

    public DescriptorHeapManager(ID3D12Device* device)
    {
        _device = device;
    }

    /// <summary>
    /// Initializes a shader-visible descriptor heap for SRV/CBV/UAV descriptors.
    /// </summary>
    public void InitializeSrvHeap(uint numDescriptors)
    {
        var desc = new DescriptorHeapDesc
        {
            Type = DescriptorHeapType.CbvSrvUav,
            NumDescriptors = numDescriptors,
            Flags = DescriptorHeapFlags.ShaderVisible,
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
        DescriptorSize = _device->GetDescriptorHandleIncrementSize(DescriptorHeapType.CbvSrvUav);
    }

    internal CpuDescriptorHandle GetCpuHandle(int descriptorIndex)
    {
        var handle = Heap->GetCPUDescriptorHandleForHeapStart();
        handle.Ptr += (uint)(descriptorIndex * DescriptorSize);
        return handle;
    }

    internal GpuDescriptorHandle GetGpuHandle(int descriptorIndex)
    {
        var handle = Heap->GetGPUDescriptorHandleForHeapStart();
        handle.Ptr += (ulong)(descriptorIndex * DescriptorSize);
        return handle;
    }
}
