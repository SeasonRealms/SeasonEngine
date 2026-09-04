// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Vulkan;
using System.Runtime.CompilerServices;
using Image = Silk.NET.Vulkan.Image;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

/// <summary>
/// Minimal wrapper for a Vulkan buffer object.
/// It is equivalent to the single-value external interface of DX ID3D12Resource*,
/// but Vulkan must explicitly keep DeviceMemory to support Map, Bind, and destruction.
/// </summary>
internal struct BufferResource
{
    public VkBuffer Buffer;

    public DeviceMemory Memory;

    public ulong Size;
}

/// <summary>
/// One-to-one counterpart of DX12 ResourceManager:
///   - CreateBuffer / CreateVertexBuffer&lt;T&gt; / CreateIndexBuffer / CreateConstantBuffer / CreateTexture2D
///   - UpdateBuffer&lt;T&gt; (Map+Memcpy+Unmap)
/// Vulkan uses HOST_VISIBLE | HOST_COHERENT by default for vertex, index, and uniform buffers,
/// equivalent to DX HeapType.Upload. This is sufficient for samples and small data,
/// while hot data can later move to DEVICE_LOCAL plus staging.
/// </summary>
internal unsafe sealed class ResourceManager
{
    readonly Vk _vk;

    readonly PhysicalDevice _physical;

    readonly Silk.NET.Vulkan.Device _device;

    public ResourceManager(Vk vk, PhysicalDevice physical, Silk.NET.Vulkan.Device device)
    {
        _vk = vk;
        _physical = physical;
        _device = device;
    }

    /// <summary>Generic Buffer + Memory + Bind trio.</summary>
    public BufferResource CreateBuffer(ulong size, BufferUsageFlags usage, MemoryPropertyFlags memProps)
    {
        var bufInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive
        };

        if (_vk.CreateBuffer(_device, in bufInfo, null, out var buffer) != Result.Success)
            throw new Exception("vkCreateBuffer failed");

        _vk.GetBufferMemoryRequirements(_device, buffer, out var memReq);

        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = FindMemoryType(memReq.MemoryTypeBits, memProps)
        };

        if (_vk.AllocateMemory(_device, in allocInfo, null, out var memory) != Result.Success)
            throw new Exception("vkAllocateMemory failed");

        if (_vk.BindBufferMemory(_device, buffer, memory, 0) != Result.Success)
            throw new Exception("vkBindBufferMemory failed");

        return new BufferResource { Buffer = buffer, Memory = memory, Size = size };
    }

    public BufferResource CreateVertexBuffer<T>(uint length) where T : unmanaged
    {
        ulong size = (ulong)sizeof(T) * length;
        return CreateBuffer(
            size,
            BufferUsageFlags.VertexBufferBit | BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
    }

    public BufferResource CreateVertexBuffer<T>(T[] vertices) where T : unmanaged
    {
        var buffer = CreateVertexBuffer<T>((uint)vertices.Length);
        UpdateBuffer(buffer, vertices);
        return buffer;
    }

    public BufferResource CreateIndexBuffer(uint[] indices)
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

        ulong size = use32Bit
            ? (ulong)(sizeof(uint) * indices.Length)
            : (ulong)(sizeof(ushort) * indices.Length);
        var buffer = CreateBuffer(
            size,
            BufferUsageFlags.IndexBufferBit | BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        void* p;
        if (_vk.MapMemory(_device, buffer.Memory, 0, size, 0, &p) != Result.Success)
            throw new Exception("vkMapMemory (CreateIndexBuffer) failed");
        if (use32Bit)
        {
            fixed (uint* pSrc = indices)
                Unsafe.CopyBlock(p, pSrc, (uint)size);
        }
        else
        {
            var indices16 = new ushort[indices.Length];
            for (int i = 0; i < indices.Length; i++)
                indices16[i] = (ushort)indices[i];
            fixed (ushort* pSrc = indices16)
                Unsafe.CopyBlock(p, pSrc, (uint)size);
        }
        _vk.UnmapMemory(_device, buffer.Memory);

        return buffer;
    }

    /// <summary>
    /// Equivalent to DX CreateConstantBuffer:
    /// 256-byte alignment plus a persistently mapped handle returned to the caller,
    /// which writes directly by frame offset.
    /// </summary>
    public BufferResource CreateConstantBuffer(ulong size, out byte* mapped)
    {
        // Vulkan UBO alignment:
        // use minUniformBufferOffsetAlignment, usually 64 or 256 on drivers, and align uniformly upward to 256.
        ulong aligned = (size + 255UL) & ~255UL;
        var buffer = CreateBuffer(
            aligned,
            BufferUsageFlags.UniformBufferBit | BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        void* p;
        if (_vk.MapMemory(_device, buffer.Memory, 0, aligned, 0, &p) != Result.Success)
            throw new Exception("vkMapMemory (CreateConstantBuffer) failed");
        mapped = (byte*)p;
        return buffer;
    }

    public void UpdateBuffer<T>(BufferResource buffer, T[] data) where T : unmanaged
    {
        ulong size = (ulong)(sizeof(T) * data.Length);
        if (size > buffer.Size) throw new ArgumentException("Update size exceeds buffer size");

        void* p;
        if (_vk.MapMemory(_device, buffer.Memory, 0, size, 0, &p) != Result.Success)
            throw new Exception("vkMapMemory (UpdateBuffer) failed");
        fixed (T* pSrc = data)
            Unsafe.CopyBlock(p, pSrc, (uint)size);
        _vk.UnmapMemory(_device, buffer.Memory);
    }

    public void DestroyBuffer(BufferResource buffer)
    {
        if (buffer.Buffer.Handle != 0)
            _vk.DestroyBuffer(_device, buffer.Buffer, null);
        if (buffer.Memory.Handle != 0)
            _vk.FreeMemory(_device, buffer.Memory, null);
    }

    /// <summary>
    /// Equivalent to DX CreateTexture2D:
    /// default heap plus Sampled usage.
    /// Returns the Image and its dedicated DeviceMemory.
    /// Upload is handled by TextureUploadBatch on the transfer queue.
    /// </summary>
    public (Image image, DeviceMemory memory) CreateTexture2D(
        int width, int height, Format format,
        ImageUsageFlags usage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit,
        uint mipLevels = 1, uint sampleCount = 1)
    {
        var imgInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D((uint)width, (uint)height, 1),
            MipLevels = mipLevels,
            ArrayLayers = 1,
            Samples = (SampleCountFlags)sampleCount,
            Tiling = ImageTiling.Optimal,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined
        };

        if (_vk.CreateImage(_device, in imgInfo, null, out var image) != Result.Success)
            throw new Exception("vkCreateImage failed");

        _vk.GetImageMemoryRequirements(_device, image, out var memReq);

        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = FindMemoryType(memReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit)
        };

        if (_vk.AllocateMemory(_device, in allocInfo, null, out var memory) != Result.Success)
            throw new Exception("vkAllocateMemory (image) failed");

        _vk.BindImageMemory(_device, image, memory, 0);
        return (image, memory);
    }

    /// <summary>Scan the PhysicalDevice memory types and return the first index satisfying both typeBits and required.</summary>
    public uint FindMemoryType(uint typeBits, MemoryPropertyFlags required)
    {
        _vk.GetPhysicalDeviceMemoryProperties(_physical, out var memProps);
        for (uint i = 0; i < memProps.MemoryTypeCount; i++)
        {
            if ((typeBits & (1u << (int)i)) != 0 &&
                (memProps.MemoryTypes[(int)i].PropertyFlags & required) == required)
                return i;
        }
        throw new Exception($"Vulkan memory type not found (typeBits=0x{typeBits:X}, required={required})");
    }
}
