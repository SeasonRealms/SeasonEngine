// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Image = Silk.NET.Vulkan.Image;

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

/// <summary>
/// SwapChain aligned with DX12:
/// wraps VkSwapchainKHR plus the backing Image[] and ImageView[] arrays,
/// and exposes Create, Resize, AcquireNextImage, and Present.
/// It does not directly own RenderPass, Framebuffer, or Depth, which belong to Display.
/// </summary>
internal unsafe sealed class SwapChain : IDisposable
{
    readonly Vk _vk;

    readonly Instance _instance;

    readonly PhysicalDevice _physical;

    readonly Silk.NET.Vulkan.Device _device;

    readonly KhrSurface _surfaceExt;

    readonly SurfaceKHR _surface;

    public KhrSwapchain KhrSwapchain { get; private set; } = null!;

    public SwapchainKHR Native { get; private set; }

    public uint FrameCount { get; private set; }

    public Format BackBufferFormat { get; private set; }

    public ColorSpaceKHR ColorSpace { get; private set; }

    public Extent2D Extent { get; private set; }

    public PresentModeKHR PresentMode { get; private set; }

    public Image[] Images { get; private set; } = [];

    public Silk.NET.Vulkan.ImageView[] ImageViews { get; private set; } = [];

    public uint CurrentImageIndex { get; private set; }

    public SwapChain(
        Vk vk,
        Instance instance,
        PhysicalDevice physical,
        Silk.NET.Vulkan.Device device,
        KhrSurface surfaceExt,
        SurfaceKHR surface,
        uint preferredFrameCount,
        Format preferredFormat)
    {
        _vk = vk;
        _instance = instance;
        _physical = physical;
        _device = device;
        _surfaceExt = surfaceExt;
        _surface = surface;
        FrameCount = preferredFrameCount;
        BackBufferFormat = preferredFormat;

        if (!_vk.TryGetDeviceExtension(instance, device, out KhrSwapchain swapExt))
            throw new Exception("VK_KHR_swapchain extension not available");
        KhrSwapchain = swapExt;
    }

    /// <summary>
    /// First creation, where oldSwapchain=null, or recreation, passing the old handle to accelerate Resize.
    /// </summary>
    public void Create(int width, int height, uint graphicsFamily, uint presentFamily)
    {
        Recreate(width, height, graphicsFamily, presentFamily);
    }

    public void Resize(int width, int height, uint graphicsFamily, uint presentFamily)
    {
        Recreate(width, height, graphicsFamily, presentFamily);
    }

    void Recreate(int width, int height, uint graphicsFamily, uint presentFamily)
    {
        // 1) Query surface capabilities.
        _surfaceExt.GetPhysicalDeviceSurfaceCapabilities(_physical, _surface, out var caps);

        // 2) Choose the surface format, preferring BackBufferFormat plus SrgbNonlinear.
        var (chosenFormat, chosenSpace) = ChooseSurfaceFormat(BackBufferFormat);
        BackBufferFormat = chosenFormat;
        ColorSpace = chosenSpace;

        // 3) Choose the present mode, preferring Mailbox and falling back to FIFO.
        PresentMode = ChoosePresentMode();

        // 4) Choose the extent.
        Extent = ChooseExtent(caps, width, height);

        // 5) Decide the image count, clamped between caps.MinImageCount and caps.MaxImageCount and rounded up from FrameCount.
        uint imageCount = Math.Max(FrameCount, caps.MinImageCount);
        if (caps.MaxImageCount > 0 && imageCount > caps.MaxImageCount)
            imageCount = caps.MaxImageCount;

        var oldHandle = Native;

        var createInfo = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _surface,
            MinImageCount = imageCount,
            ImageFormat = BackBufferFormat,
            ImageColorSpace = ColorSpace,
            ImageExtent = Extent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit,
            // Force Identity so the compositor handles screen rotation.
            // If caps.CurrentTransform were used instead, for example Rotate90,
            // the engine would have to multiply the projection matrix by a rotation matrix in shader code,
            // or the rendered orientation would be wrong.
            // The Identity path is simpler and more general.
            PreTransform = SurfaceTransformFlagsKHR.IdentityBitKhr,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = PresentMode,
            Clipped = true,
            OldSwapchain = oldHandle
        };

        // Cross-family sharing handling.
        if (graphicsFamily != presentFamily)
        {
            var families = stackalloc uint[2] { graphicsFamily, presentFamily };
            createInfo.ImageSharingMode = SharingMode.Concurrent;
            createInfo.QueueFamilyIndexCount = 2;
            createInfo.PQueueFamilyIndices = families;
        }
        else
        {
            createInfo.ImageSharingMode = SharingMode.Exclusive;
        }

        if (KhrSwapchain.CreateSwapchain(_device, in createInfo, null, out var newSwapchain) != Result.Success)
            throw new Exception("vkCreateSwapchainKHR failed");

        // Release old ImageViews and the old swapchain.
        ReleaseImageViews();
        if (oldHandle.Handle != 0)
            KhrSwapchain.DestroySwapchain(_device, oldHandle, null);

        Native = newSwapchain;

        // 6) Retrieve the actual image count, which may be greater than or equal to minImageCount.
        uint actualCount = 0;
        KhrSwapchain.GetSwapchainImages(_device, newSwapchain, ref actualCount, null);
        Images = new Image[actualCount];
        fixed (Image* p = Images)
            KhrSwapchain.GetSwapchainImages(_device, newSwapchain, ref actualCount, p);
        FrameCount = actualCount;

        // 7) Create an ImageView for each image.
        ImageViews = new Silk.NET.Vulkan.ImageView[actualCount];
        for (uint i = 0; i < actualCount; i++)
        {
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = Images[i],
                ViewType = ImageViewType.Type2D,
                Format = BackBufferFormat,
                Components = new ComponentMapping(
                    ComponentSwizzle.Identity, ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity, ComponentSwizzle.Identity),
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1)
            };
            if (_vk.CreateImageView(_device, in viewInfo, null, out var view) != Result.Success)
                throw new Exception($"vkCreateImageView failed for swapchain image {i}");
            ImageViews[i] = view;
        }
    }

    public Result AcquireNextImage(Silk.NET.Vulkan.Semaphore imageAvailable, out uint index)
    {
        index = 0;
        var r = KhrSwapchain.AcquireNextImage(_device, Native, ulong.MaxValue, imageAvailable, default, ref index);
        CurrentImageIndex = index;
        return r;
    }

    public Result Present(Silk.NET.Vulkan.Queue presentQueue, Silk.NET.Vulkan.Semaphore wait, uint imageIndex)
    {
        var sw = Native;
        var sem = wait;
        var info = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &sem,
            SwapchainCount = 1,
            PSwapchains = &sw,
            PImageIndices = &imageIndex
        };
        return KhrSwapchain.QueuePresent(presentQueue, in info);
    }

    (Format format, ColorSpaceKHR space) ChooseSurfaceFormat(Format preferred)
    {
        uint count = 0;
        _surfaceExt.GetPhysicalDeviceSurfaceFormats(_physical, _surface, ref count, null);
        if (count == 0) throw new Exception("No surface formats");
        var formats = new SurfaceFormatKHR[count];
        fixed (SurfaceFormatKHR* p = formats)
            _surfaceExt.GetPhysicalDeviceSurfaceFormats(_physical, _surface, ref count, p);

        // Preferred: match preferred plus SrgbNonlinear.
        for (int i = 0; i < count; i++)
            if (formats[i].Format == preferred && formats[i].ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
                return (formats[i].Format, formats[i].ColorSpace);

        // Second choice: any Srgb suffix.
        for (int i = 0; i < count; i++)
            if (formats[i].ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
                return (formats[i].Format, formats[i].ColorSpace);

        return (formats[0].Format, formats[0].ColorSpace);
    }

    PresentModeKHR ChoosePresentMode()
    {
        uint count = 0;
        _surfaceExt.GetPhysicalDeviceSurfacePresentModes(_physical, _surface, ref count, null);
        var modes = new PresentModeKHR[count];
        fixed (PresentModeKHR* p = modes)
            _surfaceExt.GetPhysicalDeviceSurfacePresentModes(_physical, _surface, ref count, p);

        for (int i = 0; i < count; i++)
            if (modes[i] == PresentModeKHR.MailboxKhr)
                return PresentModeKHR.MailboxKhr;
        return PresentModeKHR.FifoKhr; // Guaranteed to be supported.
    }

    Extent2D ChooseExtent(SurfaceCapabilitiesKHR caps, int width, int height)
    {
        if (caps.CurrentExtent.Width != uint.MaxValue) return caps.CurrentExtent;
        return new Extent2D
        {
            Width = (uint)Math.Clamp(width, (int)caps.MinImageExtent.Width, (int)caps.MaxImageExtent.Width),
            Height = (uint)Math.Clamp(height, (int)caps.MinImageExtent.Height, (int)caps.MaxImageExtent.Height)
        };
    }

    void ReleaseImageViews()
    {
        for (int i = 0; i < ImageViews.Length; i++)
        {
            if (ImageViews[i].Handle != 0)
                _vk.DestroyImageView(_device, ImageViews[i], null);
        }
        ImageViews = [];
    }

    public void Dispose()
    {
        ReleaseImageViews();
        if (Native.Handle != 0 && KhrSwapchain != null)
        {
            KhrSwapchain.DestroySwapchain(_device, Native, null);
            Native = default;
        }
    }
}
