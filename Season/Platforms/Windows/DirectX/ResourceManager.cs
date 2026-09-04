// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using Silk.NET.Maths;

namespace Season.Platforms.Windows.DirectX;

internal unsafe sealed class ResourceManager
{
    private readonly ID3D12Device* _device;

    public ResourceManager(ID3D12Device* device)
    {
        _device = device;
    }

    public ID3D12Resource* CreateBuffer(HeapType heapType, ulong size, ResourceStates initialState, ResourceFlags flags = ResourceFlags.None)
    {
        var heapProps = new HeapProperties(heapType);
        var desc = new ResourceDesc(
            ResourceDimension.Buffer,
            0,
            size,
            1, 1, 1,
            Format.FormatUnknown,
            new SampleDesc(1, 0),
            TextureLayout.LayoutRowMajor,
            flags);

        ID3D12Resource* resource;
        var iid = ID3D12Resource.Guid;
        var hr = _device->CreateCommittedResource(&heapProps, HeapFlags.None, &desc, initialState, null, &iid, (void**)&resource);
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);
        return resource;
    }

    public ID3D12Resource* CreateVertexBuffer<T>(uint length, out VertexBufferView view) where T : unmanaged
    {
        uint size = (uint)sizeof(T) * length;
        var buffer = CreateBuffer(HeapType.Upload, size, ResourceStates.GenericRead);

        view = new VertexBufferView
        {
            BufferLocation = buffer->GetGPUVirtualAddress(),
            StrideInBytes = (uint)sizeof(T),
            SizeInBytes = size
        };
        return buffer;
    }

    public ID3D12Resource* CreateIndexBuffer(uint[] indices, out IndexBufferView view)
    {
        bool use32Bit = false;
        for (int i = 0; i < indices.Length; i++)
        {
            if (indices[i] > ushort.MaxValue)
            {
                use32Bit = true;
                break;
            }
        }

        uint size = use32Bit
            ? (uint)(sizeof(uint) * indices.Length)
            : (uint)(sizeof(ushort) * indices.Length);
        var buffer = CreateBuffer(HeapType.Upload, size, ResourceStates.GenericRead);

        void* pData;
        var hr = buffer->Map(0, null, &pData);
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);
        if (use32Bit)
        {
            fixed (uint* pSrc = indices)
            {
                Unsafe.CopyBlock(pData, pSrc, size);
            }
        }
        else
        {
            var index16 = new ushort[indices.Length];
            for (int i = 0; i < indices.Length; i++)
                index16[i] = (ushort)indices[i];

            fixed (ushort* pSrc = index16)
            {
                Unsafe.CopyBlock(pData, pSrc, size);
            }
        }
        buffer->Unmap(0, null);

        view = new IndexBufferView
        {
            BufferLocation = buffer->GetGPUVirtualAddress(),
            Format = use32Bit ? Format.FormatR32Uint : Format.FormatR16Uint,
            SizeInBytes = size
        };
        return buffer;
    }

    public void UpdateBuffer<T>(ID3D12Resource* buffer, uint size, T[] data) where T : unmanaged
    {
        void* pData;
        var hr = buffer->Map(0, null, &pData);
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);
        fixed (T* pSrc = data)
        {
            Unsafe.CopyBlock(pData, pSrc, size);
        }
        buffer->Unmap(0, null);
    }

    public ID3D12Resource* CreateConstantBuffer(uint size, out byte* pMappedData)
    {
        // Constant buffers must be aligned to 256 bytes.
        uint alignedSize = (size + 255u) & ~255u;
        var buffer = CreateBuffer(HeapType.Upload, alignedSize, ResourceStates.GenericRead);

        void* pData;
        var hr = buffer->Map(0, null, &pData);
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);
        pMappedData = (byte*)pData;

        return buffer;
    }

    public ID3D12Resource* CreateTexture2D(Vector2D<int> size, Format format, ResourceFlags flags, ResourceStates initialState, ClearValue* clearValue = null, uint sampleCount = 1, uint sampleQuality = 0)
    {
        var heapProps = new HeapProperties(HeapType.Default);
        var desc = new ResourceDesc(
            ResourceDimension.Texture2D,
            0,
            (ulong)size.X,
            (uint)size.Y,
            1,
            1,
            format,
            new SampleDesc(sampleCount, sampleQuality),
            TextureLayout.LayoutUnknown,
            flags);

        ID3D12Resource* resource;
        var iid = ID3D12Resource.Guid;
        var hr = _device->CreateCommittedResource(&heapProps, HeapFlags.None, &desc, initialState, clearValue, &iid, (void**)&resource);
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);
        return resource;
    }
}
