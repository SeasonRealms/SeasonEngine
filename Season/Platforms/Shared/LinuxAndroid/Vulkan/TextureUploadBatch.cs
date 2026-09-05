// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Vulkan;
using System.Runtime.CompilerServices;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

/// <summary>
/// Vulkan batched texture upload aligned 1:1 with DX12 TextureUploadBatch:
///   1. Collect Texture tasks uniformly and allocate a single staging VkBuffer (HostVisible+HostCoherent)
///   2. On the dedicated CommandBuffer of the transfer queue:
///         · ImageBarrier: Undefined → TransferDstOptimal
///         · CmdCopyBufferToImage
///      (Do not transition to ShaderReadOnlyOptimal here because the transfer queue is not aware of the fragment stage)
///   3. Submit the transfer queue and signal the timeline semaphore, then write the fence value back to each Texture
///   4. When first used by the graphics queue, wait for this fence value through SubmitInfo.PWaitSemaphores,
///      and let Texture.EnsureReadyForRendering perform the layout transition on the graphics command buffer
/// </summary>
internal unsafe class TextureUploadBatch : IDisposable
{
    readonly List<Texture> _tasks = new();

    readonly Vk _vk;

    readonly Silk.NET.Vulkan.Device _device;

    CommandPool _pool;
    internal CommandPool Pool => _pool;

    public TextureUploadBatch(Vk vk, Silk.NET.Vulkan.Device device, uint transferQueueFamily)
    {
        _vk = vk;
        _device = device;

        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.TransientBit | CommandPoolCreateFlags.ResetCommandBufferBit,
            QueueFamilyIndex = transferQueueFamily
        };

        if (vk.CreateCommandPool(device, in poolInfo, null, out _pool) != Result.Success)
            throw new Exception("vkCreateCommandPool (upload) failed");
    }

    public List<Texture> GetTasks() => _tasks;

    public void AddTextureUpload(Texture texture) => _tasks.Add(texture);

    /// <summary>Execute one batched upload (synchronously waits for transfer completion; simple and reliable, can be changed to an asynchronous pipeline later).</summary>
    public void Execute()
    {
        if (_tasks.Count == 0) return;

        // 1. Calculate the total size and align to 4 bytes (CmdCopyBufferToImage requires 4-byte alignment by default).
        // 2-6 clause 4: the size must come from ImageData rather than Width*Height*4, because a texture that owns a
        // mip chain carries every level in that same buffer.
        ulong totalSize = 0;
        var offsets = new ulong[_tasks.Count];
        for (int i = 0; i < _tasks.Count; i++)
        {
            offsets[i] = totalSize;
            ulong size = (ulong)(_tasks[i].ImageData?.Length ?? (int)(_tasks[i].Width * _tasks[i].Height * 4));
            totalSize += AlignUp(size, 4);
        }

        // 2. Allocate the staging buffer, map it, and copy pixel data
        var staging = Device.ResourceManager.CreateBuffer(
            totalSize,
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        try
        {
            void* p;
            if (_vk.MapMemory(_device, staging.Memory, 0, totalSize, 0, &p) != Result.Success)
                throw new Exception("vkMapMemory (staging) failed");
            var basePtr = (byte*)p;
            for (int i = 0; i < _tasks.Count; i++)
            {
                var t = _tasks[i];
                if (t.ImageData == null) continue;
                fixed (byte* pSrc = t.ImageData)
                    Unsafe.CopyBlock(basePtr + offsets[i], pSrc, (uint)t.ImageData.Length);
            }
            _vk.UnmapMemory(_device, staging.Memory);

            // 3. Record the transfer command buffer
            var cmd = AllocateCommandBuffer();
            var beginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit
            };
            _vk.BeginCommandBuffer(cmd, in beginInfo);

            for (int i = 0; i < _tasks.Count; i++)
            {
                var t = _tasks[i];

                // Undefined → TransferDstOptimal
                BarrierToTransferDst(cmd, t);

                // CopyBufferToImage, one region per subresource. This degenerates to the pre-2-6 single region
                // whenever MipLevels is 1, since MipInfos then holds exactly level 0 at offset 0.
                var regions = new BufferImageCopy[t.MipLevels];
                for (uint level = 0; level < t.MipLevels; level++)
                {
                    var info = t.MipInfos != null
                        ? t.MipInfos[level]
                        : new MipLevelInfo((int)t.Width, (int)t.Height, 0);
                    regions[level] = new BufferImageCopy
                    {
                        // Per-level offsets are relative to this texture's own block, so the batch offset shifts the
                        // whole layout rather than being folded into each level.
                        BufferOffset = offsets[i] + (ulong)info.ByteOffset,
                        BufferRowLength = 0,        // tightly packed
                        BufferImageHeight = 0,
                        ImageSubresource = new ImageSubresourceLayers
                        {
                            AspectMask = ImageAspectFlags.ColorBit,
                            MipLevel = level,
                            BaseArrayLayer = 0,
                            LayerCount = 1
                        },
                        ImageOffset = new Offset3D(0, 0, 0),
                        ImageExtent = new Extent3D((uint)info.Width, (uint)info.Height, 1)
                    };
                }

                fixed (BufferImageCopy* pRegions = regions)
                {
                    _vk.CmdCopyBufferToImage(cmd, staging.Buffer, t.Image,
                        ImageLayout.TransferDstOptimal, t.MipLevels, pRegions);
                }
            }

            _vk.EndCommandBuffer(cmd);

            // 4. Submit to the transfer queue and signal the timeline semaphore
            var transferCq = Device.TransferCommandQueue;
            var sem = transferCq.TimelineSemaphore;
            ulong signalValue = transferCq.GetCompletedValue() + 1;

            var timelineInfo = new TimelineSemaphoreSubmitInfo
            {
                SType = StructureType.TimelineSemaphoreSubmitInfo,
                SignalSemaphoreValueCount = 1,
                PSignalSemaphoreValues = &signalValue
            };
            var cmdBuf = cmd;
            var signalSem = sem;
            var submit = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                PNext = &timelineInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &cmdBuf,
                SignalSemaphoreCount = 1,
                PSignalSemaphores = &signalSem
            };

            if (_vk.QueueSubmit(transferCq.NativeQueue, 1, in submit, default) != Result.Success)
                throw new Exception("vkQueueSubmit (texture upload) failed");

            // 5. Mark the fence value and state for each texture
            foreach (var t in _tasks)
            {
                t.UploadFenceValue = signalValue;
                t.CurrentLayout = ImageLayout.TransferDstOptimal;
                t.Ready = true;
                t.ImageData = null;
            }

            // 6. Wait for transfer completion on the CPU, then safely release the staging buffer and command buffer
            transferCq.WaitForFence(signalValue);

            _vk.FreeCommandBuffers(_device, _pool, 1, in cmd);
        }
        catch (Exception ex)
        {

        }
        finally
        {
            Device.ResourceManager.DestroyBuffer(staging);
            _tasks.Clear();
        }
    }

    void BarrierToTransferDst(CommandBuffer cmd, Texture t)
    {
        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.Undefined,
            NewLayout = ImageLayout.TransferDstOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = t.Image,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = t.MipLevels,
                BaseArrayLayer = 0,
                LayerCount = 1
            },
            SrcAccessMask = 0,
            DstAccessMask = AccessFlags.TransferWriteBit
        };

        _vk.CmdPipelineBarrier(
            cmd,
            PipelineStageFlags.TopOfPipeBit,
            PipelineStageFlags.TransferBit,
            0, 0, null, 0, null, 1, in barrier);
    }

    CommandBuffer AllocateCommandBuffer()
    {
        var alloc = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _pool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1
        };
        if (_vk.AllocateCommandBuffers(_device, in alloc, out var cmd) != Result.Success)
            throw new Exception("vkAllocateCommandBuffers (upload) failed");
        return cmd;
    }

    static ulong AlignUp(ulong v, ulong align) => (v + align - 1) & ~(align - 1);

    public void Dispose()
    {
        if (_pool.Handle != 0) { _vk.DestroyCommandPool(_device, _pool, null); _pool = default; }
        _tasks.Clear();
    }
}
