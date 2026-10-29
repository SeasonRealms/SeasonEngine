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
///   3. Submit the transfer queue and signal the completion value (timeline semaphore on 1.2 devices,
///      a registered VkFence on the 1.1 fallback), then write the fence value back to each Texture
///   4. When first used by the graphics queue, wait for this fence value through SubmitInfo.PWaitSemaphores,
///      and let Texture.EnsureReadyForRendering perform the layout transition on the graphics command buffer
/// </summary>
internal unsafe class TextureUploadBatch : IDisposable
{
    // Guarded by _tasksLock, never by the GPU gate: producers enqueue from the background loader while a frame may be
    // in flight, and a frame holds the gate while it walks the texture dictionaries the loader already holds.
    List<Texture> _tasks = new();

    readonly object _tasksLock = new();

    readonly Vk _vk;

    readonly Silk.NET.Vulkan.Device _device;

    CommandPool _pool;
    bool _incompleteSubmission;
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

    /// <summary>
    /// Queue one texture for the next <see cref="Execute"/>.
    /// Called from whichever thread created the texture - the background loader for Sprite2D/3D and for model materials -
    /// and deliberately without the GPU gate. A producer reaching here can already hold one of the texture dictionaries
    /// (Graphics.LoadSprite2D/LoadSprite3D/LoadShape/EnsureVKTexture all construct the VkTexture under the lock), while a
    /// frame holds the gate and then looks textures up under those same dictionaries: any producer that waits for the
    /// gate here inverts the order and deadlocks the frame against the loader. The gate is taken by Execute alone.
    /// </summary>
    public void AddTextureUpload(Texture texture)
    {
        lock (_tasksLock) _tasks.Add(texture);
    }

    /// <summary>Take a texture back out of the queue, for a producer whose upload failed before Execute drained it.</summary>
    public void RemoveTask(Texture texture)
    {
        lock (_tasksLock) _tasks.Remove(texture);
    }

    /// <summary>Execute one batched upload (synchronously waits for transfer completion; simple and reliable, can be changed to an asynchronous pipeline later).</summary>
    public void Execute()
    {
        // This batch submits on the shared transfer queue and allocates from the shared upload command pool. Neither may
        // be touched while the render thread is inside a frame: vkQueueSubmit and vkAllocateCommandBuffers are externally
        // synchronized per object, and the validation layer reports the overlap as
        // "THREADING ERROR : object of type VkQueue is simultaneously used". The render thread holds the gate for the
        // whole frame, so a load that gets here mid-frame simply waits for it to end.
        using var gpuAccess = Device.EnterGpuAccessScope();

        // Take the queue under its own lock and leave the producers a fresh list, so a texture queued while this batch
        // runs belongs to the next Execute instead of being dropped by the finally below. Waiting for the gate above
        // before touching the list keeps the only possible order gate -> _tasksLock, which is what lets
        // AddTextureUpload stay gate-free.
        // An empty batch still returns here, before the staging buffer exists: vkCreateBuffer rejects a zero-byte size.
        List<Texture> tasks;
        lock (_tasksLock)
        {
            if (_tasks.Count == 0) return;
            tasks = _tasks;
            _tasks = new List<Texture>();
        }

        // 1. Calculate the total size and align to 4 bytes (CmdCopyBufferToImage requires 4-byte alignment by default).
        // 2-6 clause 4: the size must come from ImageData rather than Width*Height*4, because a texture that owns a
        // mip chain carries every level in that same buffer.
        ulong totalSize = 0;
        var offsets = new ulong[tasks.Count];
        for (int i = 0; i < tasks.Count; i++)
        {
            offsets[i] = totalSize;
            ulong size = (ulong)(tasks[i].ImageData?.Length ?? (int)(tasks[i].Width * tasks[i].Height * 4));
            totalSize += AlignUp(size, 4);
        }

        // 2. Allocate the staging buffer, map it, and copy pixel data
        var staging = Device.ResourceManager.CreateBuffer(
            totalSize,
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        CommandBuffer cmd = default;
        bool submitted = false;
        try
        {
            void* p;
            if (_vk.MapMemory(_device, staging.Memory, 0, totalSize, 0, &p) != Result.Success)
                throw new Exception("vkMapMemory (staging) failed");
            var basePtr = (byte*)p;
            for (int i = 0; i < tasks.Count; i++)
            {
                var t = tasks[i];
                if (t.ImageData == null) continue;
                fixed (byte* pSrc = t.ImageData)
                    Unsafe.CopyBlock(basePtr + offsets[i], pSrc, (uint)t.ImageData.Length);
            }
            _vk.UnmapMemory(_device, staging.Memory);

            // 3. Record the transfer command buffer
            cmd = AllocateCommandBuffer();
            var beginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit
            };
            Device.CheckResult(_vk.BeginCommandBuffer(cmd, in beginInfo));

            for (int i = 0; i < tasks.Count; i++)
            {
                var t = tasks[i];

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

            // Leave every image in ShaderReadOnlyOptimal as part of this very submission, matching UploadPixels.
            // The alternative - letting a later frame's EnsureReadyForRendering do it - is what made a texture
            // uploaded and sampled in the same frame reach the draw still in TransferDstOptimal, which is
            // VUID-vkCmdDraw-None-09600: the in-render-pass deferral can only drain on the next BeforeRender.
            for (int i = 0; i < tasks.Count; i++)
                BarrierToShaderRead(cmd, tasks[i]);

            Device.CheckResult(_vk.EndCommandBuffer(cmd));

            // 4. Submit to the transfer queue. Timeline mode signals the queue's timeline semaphore; the
            //    1.1 fallback registers a VkFence for the same completion value instead. Both keep the
            //    value contract that step 5 and step 6 rely on.
            var transferCq = Device.TransferCommandQueue;
            ulong signalValue = transferCq.GetCompletedValue() + 1;
            var cmdBuf = cmd;

            if (transferCq.TimelineMode)
            {
                var sem = transferCq.TimelineSemaphore;
                var signalSem = sem;
                var timelineInfo = new TimelineSemaphoreSubmitInfo
                {
                    SType = StructureType.TimelineSemaphoreSubmitInfo,
                    SignalSemaphoreValueCount = 1,
                    PSignalSemaphoreValues = &signalValue
                };
                var submit = new SubmitInfo
                {
                    SType = StructureType.SubmitInfo,
                    PNext = &timelineInfo,
                    CommandBufferCount = 1,
                    PCommandBuffers = &cmdBuf,
                    SignalSemaphoreCount = 1,
                    PSignalSemaphores = &signalSem
                };

                transferCq.CheckSubmit(_vk.QueueSubmit(transferCq.NativeQueue, 1, in submit, default));
            }
            else
            {
                var submit = new SubmitInfo
                {
                    SType = StructureType.SubmitInfo,
                    CommandBufferCount = 1,
                    PCommandBuffers = &cmdBuf
                };

                var submitFence = transferCq.PrepareSubmit(signalValue);
                transferCq.CheckSubmit(_vk.QueueSubmit(transferCq.NativeQueue, 1, in submit, submitFence));
            }

            submitted = true;

            // 5. Record the fence value and state for each texture
            //
            // 2-6 clause 5: dropping ImageData here is why IGraphics.RebuildNormalVarianceTextures stays a no-op on this
            // backend - re-running a normal map's chain needs the authored level-0 pixels, and D3D12 can only offer that
            // knob because it keeps them for the resource's lifetime. Retaining them here would cost W*H*4 per normal map
            // permanently on Android, which is not worth paying for a development-time A/B; the switch still works there,
            // it just takes a restart to change. Keep the two in step if this ever changes.
            //
            // Ready is deliberately not published here. It is set in step 6, after the transfer really completed:
            // once a consumer observes Ready it may submit a draw sampling this image, and until the barrier recorded
            // above has executed, the copy is still in flight and the pixels are undefined even though the layout
            // tracker already says ShaderReadOnlyOptimal.
            foreach (var t in tasks)
            {
                t.UploadFenceValue = signalValue;
                t.ImageData = null;
            }

            // 6. Wait for transfer completion on the CPU, then publish readiness and release the staging buffer and
            // command buffer. If the wait throws, Ready stays false: the textures are simply never drawn, which is a
            // far better failure than sampling an image the GPU is still writing.
            transferCq.WaitForFence(signalValue);
            submitted = false;

            foreach (var t in tasks)
            {
                // Advanced only after the copy is confirmed complete, matching UploadPixels: a stale TransferDstOptimal
                // here would make the first consumer re-issue a transition (and, inside a render pass, defer it again)
                // for no reason; a premature ShaderReadOnlyOptimal would claim a layout the GPU has not reached yet.
                t.CurrentLayout = ImageLayout.ShaderReadOnlyOptimal;
                Volatile.Write(ref t.Ready, true);
            }
        }
        finally
        {
            // Do not swallow upload failures or free staging storage still consumed by the transfer queue.
            // Keep resources in transit when waiting for failure; Cannot wait indefinitely or consider timeout as completion in the final stage.
            if (!submitted)
            {
                if (cmd.Handle != 0) _vk.FreeCommandBuffers(_device, _pool, 1, in cmd);
                Device.ResourceManager.DestroyBuffer(staging);
            }
            else _incompleteSubmission = true;

            // tasks was already detached from the queue, so there is nothing to drain on the failure path either: a
            // texture that failed here keeps Ready false and is dropped by its owner.
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

    void BarrierToShaderRead(CommandBuffer cmd, Texture t)
    {
        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.TransferDstOptimal,
            NewLayout = ImageLayout.ShaderReadOnlyOptimal,
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
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit
        };

        // Recorded on the transfer queue, so neither mask may name a graphics stage.
        // AllCommands stands in for FragmentShader the same way it does in Texture.UploadPixels.
        // Cross-queue visibility comes from the timeline semaphore the render submission waits on.
        _vk.CmdPipelineBarrier(
            cmd,
            PipelineStageFlags.TransferBit,
            PipelineStageFlags.AllCommandsBit,
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
        if (!_incompleteSubmission && _pool.Handle != 0) { _vk.DestroyCommandPool(_device, _pool, null); _pool = default; }
        _tasks.Clear();
    }
}
