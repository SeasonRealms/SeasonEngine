// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Metal;

namespace Season.Platforms.Shared.Apple.Metal;

/// <summary>
/// Metal ResourceManager aligned one to one with the DX12 and Vulkan ResourceManager:
///   - CreateBuffer / CreateVertexBuffer&lt;T&gt; / CreateIndexBuffer / CreateConstantBuffer / CreateTexture2D
///   - UpdateBuffer&lt;T&gt; using Span copies
/// On Metal, vertex, index, and uniform buffers default to MTLResourceOptions.StorageModeShared,
/// equivalent to DX HeapType.Upload and VK HOST_VISIBLE plus HOST_COHERENT.
/// </summary>
internal sealed class ResourceManager
{
    readonly IMTLDevice _device;

    public ResourceManager(IMTLDevice device)
    {
        _device = device;
    }

    /// <summary>Create a generic buffer using StorageModeShared so CPU and GPU share the same memory.</summary>
    public IMTLBuffer CreateBuffer(nuint size, MTLResourceOptions options = MTLResourceOptions.StorageModeShared)
    {
        var buf = _device.CreateBuffer(size, options) ?? throw new Exception("IMTLDevice.CreateBuffer failed");
        return buf;
    }

    public IMTLBuffer CreateVertexBuffer<T>(uint length) where T : unmanaged
    {
        nuint size = (nuint)(Marshal.SizeOf<T>() * (int)length);
        return CreateBuffer(size);
    }

    public IMTLBuffer CreateVertexBuffer<T>(T[] vertices) where T : unmanaged
    {
        var buffer = CreateVertexBuffer<T>((uint)vertices.Length);
        UpdateBuffer(buffer, vertices);
        return buffer;
    }

    public IMTLBuffer CreateIndexBuffer(ushort[] indices)
    {
        nuint size = (nuint)(sizeof(ushort) * indices.Length);
        var buffer = CreateBuffer(size);
        unsafe
        {
            fixed (ushort* pSrc = indices)
            {
                Buffer.MemoryCopy(pSrc, (void*)buffer.Contents, (long)size, (long)size);
            }
        }
        return buffer;
    }

    public IMTLBuffer CreateIndexBuffer(uint[] indices)
    {
        // Match Vulkan ResourceManager.CreateIndexBuffer(uint[]):
        // if all indices are less than or equal to 65535, compress to a 16-bit buffer; otherwise keep 32-bit.
        bool use32Bit = false;
        for (int i = 0; i < indices.Length; i++)
        {
            if (indices[i] > ushort.MaxValue)
            {
                use32Bit = true;
                break;
            }
        }

        if (use32Bit)
        {
            nuint size = (nuint)(sizeof(uint) * indices.Length);
            var buffer = CreateBuffer(size);
            unsafe
            {
                fixed (uint* pSrc = indices)
                {
                    Buffer.MemoryCopy(pSrc, (void*)buffer.Contents, (long)size, (long)size);
                }
            }
            return buffer;
        }
        else
        {
            nuint size = (nuint)(sizeof(ushort) * indices.Length);
            var buffer = CreateBuffer(size);
            var indices16 = new ushort[indices.Length];
            for (int i = 0; i < indices.Length; i++)
                indices16[i] = (ushort)indices[i];
            unsafe
            {
                fixed (ushort* pSrc = indices16)
                {
                    Buffer.MemoryCopy(pSrc, (void*)buffer.Contents, (long)size, (long)size);
                }
            }
            return buffer;
        }
    }

    /// <summary>
    /// Equivalent to DX and VK CreateConstantBuffer:
    /// a persistently mapped buffer aligned to 256 bytes.
    /// Callers write directly into buffer.Contents using per-frame offsets.
    /// </summary>
    public IMTLBuffer CreateConstantBuffer(nuint size)
    {
        nuint aligned = (size + 255) & ~(nuint)255;
        return CreateBuffer(aligned);
    }

    public unsafe void UpdateBuffer<T>(IMTLBuffer buffer, T[] data, nuint offset = 0) where T : unmanaged
    {
        nuint size = (nuint)(sizeof(T) * data.Length);
        byte* dst = (byte*)buffer.Contents + offset;
        fixed (T* pSrc = data)
        {
            Buffer.MemoryCopy(pSrc, dst, (long)size, (long)size);
        }
    }

    public unsafe void UpdateBuffer<T>(IMTLBuffer buffer, ReadOnlySpan<T> data, nuint offset = 0) where T : unmanaged
    {
        nuint size = (nuint)(sizeof(T) * data.Length);
        byte* dst = (byte*)buffer.Contents + offset;
        fixed (T* pSrc = data)
        {
            Buffer.MemoryCopy(pSrc, dst, (long)size, (long)size);
        }
    }

    /// <summary>
    /// Equivalent to DX CreateTexture2D:
    /// default sampled usage with shader read-only access.
    /// Actual pixel copies are performed by TextureUploadBatch through IMTLBlitCommandEncoder.
    /// </summary>
    public IMTLTexture CreateTexture2D(int width, int height, MTLPixelFormat format = MTLPixelFormat.RGBA8Unorm,
        uint mipLevels = 1, MTLTextureUsage usage = MTLTextureUsage.ShaderRead)
    {
        var desc = MTLTextureDescriptor.CreateTexture2DDescriptor(format, (nuint)width, (nuint)height, mipLevels > 1);
        desc.MipmapLevelCount = mipLevels;
        desc.Usage = usage;
        desc.StorageMode = MTLStorageMode.Private; // GPU-only, uploaded through the BlitEncoder.
        var tex = _device.CreateTexture(desc) ?? throw new Exception("IMTLDevice.CreateTexture failed");
        return tex;
    }
}
