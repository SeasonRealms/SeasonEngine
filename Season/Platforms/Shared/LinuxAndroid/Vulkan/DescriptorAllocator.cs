// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Vulkan;

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

/// <summary>
/// Vulkan descriptor allocator aligned with the combined role of DX12 DescriptorAllocator and DescriptorHeapManager:
/// one large VkDescriptorPool dynamically allocates and frees VkDescriptorSet objects by DescriptorSetLayout.
///
/// Capacity, default 2048, reserves UBO and CombinedImageSampler descriptors proportionally:
///   - per set: 4 UBOs, matrix, light, material, and bone, plus 1 storage buffer for instanced bones
///     plus 7 combined image samplers, albedo, normal, mr, ao, emissive, shadow atlas, and the 1-7 environment cube
///   - pool total: capacity * 4 UBO + capacity * 1 storage + capacity * 7 image
///
/// Vulkan does not need RTV or DSV heaps. The equivalent concept is carried by RenderPass plus Framebuffer, see Display.
/// </summary>
internal unsafe sealed class DescriptorAllocator : IDisposable
{
    readonly Vk _vk;

    readonly Silk.NET.Vulkan.Device _device;

    public int Capacity { get; }

    public DescriptorPool Pool { get; private set; }

    /// <summary>Number of UBOs per set, aligned with PipelineLayout and including binding 11 for TextDrawParams.</summary>
    public const uint UniformsPerSet = 5;

    /// <summary>
    /// Number of CombinedImageSampler bindings per set, aligned with PipelineLayout:
    /// binding 4 through 8 for the 5 material textures,
    /// binding 12 for the shadow atlas,
    /// binding 16 for the 1-7 environment radiance cube,
    /// binding 17 for the 2-4 DDGI irradiance atlas,
    /// binding 18 for the 2-4 step-3 DDGI depth atlas,
    /// binding 19 for the 2-5 step-C cloud noise,
    /// and binding 20 for the 2-5 step-E AP 3D LUT.
    /// Note:
    /// binding 12 had previously been omitted, leaving the pool undersized without actually exhausting it.
    /// It is now included together with 1-7.
    /// 2-4 adds 17 and 18 to reach 9, and 2-5 adds 19 and 20 to reach 11.
    /// </summary>
    public const uint SampledImagesPerSet = 11;

    /// <summary>Number of storage buffers per set, aligned with PipelineLayout.</summary>
    public const uint StorageBuffersPerSet = 2;

    /// <summary>1-6 Compute: storage-image pool capacity for per-kernel StorageTextureWrite bindings.
    /// The number of kernels is far smaller than the number of sprites, so capacity times 1 is already well beyond demand.</summary>
    public const uint StorageImagesPerSet = 1;

    public DescriptorAllocator(Vk vk, Silk.NET.Vulkan.Device device, int capacity = 2048)
    {
        _vk = vk;
        _device = device;
        Capacity = capacity;

        var poolSizes = stackalloc DescriptorPoolSize[4]
        {
            new DescriptorPoolSize
            {
                Type = DescriptorType.UniformBuffer,
                DescriptorCount = (uint)capacity * UniformsPerSet
            },
            new DescriptorPoolSize
            {
                Type = DescriptorType.StorageBuffer,
                DescriptorCount = (uint)capacity * StorageBuffersPerSet
            },
            new DescriptorPoolSize
            {
                Type = DescriptorType.CombinedImageSampler,
                DescriptorCount = (uint)capacity * SampledImagesPerSet
            },
            new DescriptorPoolSize
            {
                Type = DescriptorType.StorageImage,
                DescriptorCount = (uint)capacity * StorageImagesPerSet
            }
        };

        var info = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit,
            MaxSets = (uint)capacity,
            PoolSizeCount = 4,
            PPoolSizes = poolSizes
        };

        if (vk.CreateDescriptorPool(device, in info, null, out var pool) != Result.Success)
            throw new Exception("vkCreateDescriptorPool failed");
        Pool = pool;
    }

    /// <summary>Allocate one VkDescriptorSet from the pool using the specified layout. Equivalent to DX DescriptorAllocator.Allocate.</summary>
    public DescriptorSet AllocateSet(DescriptorSetLayout layout)
    {
        var setLayout = layout;
        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = Pool,
            DescriptorSetCount = 1,
            PSetLayouts = &setLayout
        };

        if (_vk.AllocateDescriptorSets(_device, in allocInfo, out var set) != Result.Success)
            throw new Exception("vkAllocateDescriptorSets failed (pool may be exhausted)");
        return set;
    }

    /// <summary>Return a set to the pool. Equivalent to DX DescriptorAllocator.Free.</summary>
    public void FreeSet(DescriptorSet set)
    {
        if (set.Handle == 0) return;
        _vk.FreeDescriptorSets(_device, Pool, 1, in set);
    }

    public void Dispose()
    {
        if (Pool.Handle != 0)
        {
            _vk.DestroyDescriptorPool(_device, Pool, null);
            Pool = default;
        }
    }
}
