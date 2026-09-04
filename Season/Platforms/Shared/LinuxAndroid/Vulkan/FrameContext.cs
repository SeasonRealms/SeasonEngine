// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Vulkan;
using Image = Silk.NET.Vulkan.Image;

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

/// <summary>
/// Vulkan frame context aligned with DX12 FrameContext:
/// each frame owns a CommandPool, the main CommandBuffer, two binary semaphores,
/// and a FenceValue used by the timeline-fence ring.
/// References to the current SwapChain image are injected as RenderTarget and View.
/// </summary>
internal unsafe sealed class FrameContext : IDisposable
{
    readonly Vk _vk;

    readonly Silk.NET.Vulkan.Device _device;

    public CommandPool CommandPool { get; private set; }

    public CommandBuffer CommandList { get; private set; }

    public Image RenderTarget { get; set; }

    public Silk.NET.Vulkan.ImageView RenderTargetView { get; set; }

    public Framebuffer Framebuffer { get; set; }

    /// <summary>Signal that the swapchain image is now writable after acquire completes.</summary>
    public Silk.NET.Vulkan.Semaphore ImageAvailable { get; private set; }

    /// <summary>Signal that rendering finished. Present waits on this semaphore.</summary>
    public Silk.NET.Vulkan.Semaphore RenderFinished { get; private set; }

    /// <summary>Target value for the DirectQueue timeline semaphore used by this frame, equivalent to DX fenceValues[FrameIndex].</summary>
    public ulong FenceValue { get; set; } = 1;

    public FrameContext(Vk vk, Silk.NET.Vulkan.Device device)
    {
        _vk = vk;
        _device = device;
    }

    public void Initialize(uint graphicsQueueFamily)
    {
        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.TransientBit | CommandPoolCreateFlags.ResetCommandBufferBit,
            QueueFamilyIndex = graphicsQueueFamily
        };

        if (_vk.CreateCommandPool(_device, in poolInfo, null, out var pool) != Result.Success)
            throw new Exception("vkCreateCommandPool failed");
        CommandPool = pool;

        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = pool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1
        };

        if (_vk.AllocateCommandBuffers(_device, in allocInfo, out var cb) != Result.Success)
            throw new Exception("vkAllocateCommandBuffers failed");
        CommandList = cb;

        var semInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        _vk.CreateSemaphore(_device, in semInfo, null, out var imgAvail);
        ImageAvailable = imgAvail;
        _vk.CreateSemaphore(_device, in semInfo, null, out var rendDone);
        RenderFinished = rendDone;
    }

    /// <summary>
    /// Equivalent to DX CommandAllocator->Reset plus CommandList->Reset.
    /// Uses vkResetCommandPool to reclaim all command buffers in the pool at once, then begins the main command buffer.
    /// </summary>
    public void Reset()
    {
        _vk.ResetCommandPool(_device, CommandPool, 0);

        var begin = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit
        };
        if (_vk.BeginCommandBuffer(CommandList, in begin) != Result.Success)
            throw new Exception("vkBeginCommandBuffer failed");
    }

    public void End()
    {
        if (_vk.EndCommandBuffer(CommandList) != Result.Success)
            throw new Exception("vkEndCommandBuffer failed");
    }

    public void SetRenderTarget(Image image, Silk.NET.Vulkan.ImageView view, Framebuffer fb)
    {
        RenderTarget = image;
        RenderTargetView = view;
        Framebuffer = fb;
    }

    public void Dispose()
    {
        if (ImageAvailable.Handle != 0)
        {
            _vk.DestroySemaphore(_device, ImageAvailable, null);
            ImageAvailable = default;
        }
        if (RenderFinished.Handle != 0)
        {
            _vk.DestroySemaphore(_device, RenderFinished, null);
            RenderFinished = default;
        }
        if (CommandPool.Handle != 0)
        {
            _vk.DestroyCommandPool(_device, CommandPool, null);
            CommandPool = default;
        }
        // RenderTarget, View, and Framebuffer are owned by SwapChain and Display and are not released here.
    }
}
