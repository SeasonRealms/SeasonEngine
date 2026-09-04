// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Vulkan;
using Image = Silk.NET.Vulkan.Image;

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

/// <summary>
/// Vulkan display object aligned with DX12 Display:
/// owns the depth image and view, render pass, framebuffer array, and viewport and scissor.
/// The current implementation uses 1x MSAA with no MSAA resolve path,
/// so it does not yet match the DX default of 4x and can be extended later if needed.
/// The viewport uses negative height to implement Y flip on Vulkan 1.1 and later,
/// keeping NDC consistent with the DX left-handed convention.
///
/// Pass-orchestration position, fixed in step 4 of 1-1:
/// the RenderPass in this class is the only render pass for backbuffer-target passes,
/// Scene direct rendering and FinalBlit,
/// and it is also the baking anchor for all main PSOs.
/// Offscreen passes use the render pass owned by VKRenderTarget.
/// BackbufferCompatible offscreen formats remain render-pass compatible with this one,
/// so PSOs can be reused across them.
/// The backbuffer attachment uses finalLayout=PresentSrcKhr,
/// and cross-frame depth WAW ordering is handled by the EXTERNAL to 0 subpass dependency,
/// all with zero explicit barriers.
/// See the Device class header for the full catalog of Vulkan-specific rules.
/// </summary>
internal unsafe sealed class Display : IDisposable
{
    readonly Vk _vk;

    readonly PhysicalDevice _physical;

    readonly Silk.NET.Vulkan.Device _device;

    public Format BackBufferFormat { get; }

    public Format DepthBufferFormat { get; }

    public uint MsaaSampleCount => 1;

    public Image DepthImage { get; private set; }

    public DeviceMemory DepthMemory { get; private set; }

    public Silk.NET.Vulkan.ImageView DepthView { get; private set; }

    public RenderPass RenderPass { get; private set; }

    /// <summary>
    /// Preserve-style backbuffer render pass using color LoadOp=Load.
    /// The second and later backbuffer passes in the same frame, such as Overlay, must use it.
    /// Otherwise the <see cref="RenderPass"/> baked with LoadOp=Clear would erase the 3D content that FinalBlit already presented when CmdBeginRenderPass starts.
    /// That was the root cause of WSL and Vulkan showing only 2D content.
    /// It differs from <see cref="RenderPass"/> only in load op and initialLayout.
    /// Attachment formats and sample counts stay identical, so render-pass compatibility holds and the same framebuffer and PSO set can be shared.
    /// </summary>
    public RenderPass RenderPassLoad { get; private set; }

    public Framebuffer[] Framebuffers { get; private set; } = [];

    public Silk.NET.Vulkan.Viewport Viewport { get; private set; }

    public Rect2D ScissorRect { get; private set; }

    Vector4 _clearColor;

    public Vector4 ClearColor => _clearColor;

    public Display(Vk vk, PhysicalDevice physical, Silk.NET.Vulkan.Device device, Format backFmt, Format depthFmt)
    {
        _vk = vk;
        _physical = physical;
        _device = device;
        BackBufferFormat = backFmt;
        DepthBufferFormat = depthFmt;
        _clearColor = new Vector4(1f, 1f, 1f, 1f);
    }

    public void SetClearColor(Vector4 color) => _clearColor = color;

    public void Initialize(int width, int height, Silk.NET.Vulkan.ImageView[] swapchainImageViews)
    {
        CreateDepth(width, height);
        CreateRenderPass();
        CreateFramebuffers(width, height, swapchainImageViews);
        UpdateViewportAndScissor(width, height);
    }

    public void Resize(int width, int height, Silk.NET.Vulkan.ImageView[] swapchainImageViews)
    {
        ReleaseFramebuffers();
        ReleaseDepth();

        CreateDepth(width, height);
        CreateFramebuffers(width, height, swapchainImageViews);
        UpdateViewportAndScissor(width, height);
    }

    void CreateDepth(int width, int height)
    {
        var imgInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = DepthBufferFormat,
            Extent = new Extent3D((uint)width, (uint)height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.DepthStencilAttachmentBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined
        };
        if (_vk.CreateImage(_device, in imgInfo, null, out var img) != Result.Success)
            throw new Exception("vkCreateImage (depth) failed");
        DepthImage = img;

        _vk.GetImageMemoryRequirements(_device, img, out var memReq);

        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = FindMemoryType(memReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit)
        };
        if (_vk.AllocateMemory(_device, in allocInfo, null, out var mem) != Result.Success)
            throw new Exception("vkAllocateMemory (depth) failed");
        DepthMemory = mem;
        _vk.BindImageMemory(_device, img, mem, 0);

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = img,
            ViewType = ImageViewType.Type2D,
            Format = DepthBufferFormat,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.DepthBit, 0, 1, 0, 1)
        };
        if (_vk.CreateImageView(_device, in viewInfo, null, out var view) != Result.Success)
            throw new Exception("vkCreateImageView (depth) failed");
        DepthView = view;
    }

    void CreateRenderPass()
    {
        if (RenderPass.Handle != 0) return; // Create only once. Resize does not rebuild the render pass.
        RenderPass = CreateBackbufferRenderPass(preserve: false);
        RenderPassLoad = CreateBackbufferRenderPass(preserve: true);
    }

    RenderPass CreateBackbufferRenderPass(bool preserve)
    {
        // preserve=false means the first pass, Scene direct rendering or FinalBlit.
        // It fully covers the screen, so Clear is the optimal load op.
        // preserve=true means later passes such as Overlay, which must Load to preserve backbuffer contents written by the previous pass.
        // The initialLayout of a preserve-style color attachment must be PresentSrcKhr.
        // The previous backbuffer pass already ended in finalLayout=PresentSrcKhr, so the driver has transitioned the image there.
        // Declaring ColorAttachmentOptimal here would violate the spec because it would not match the actual layout.
        // lavapipe may tolerate the mismatch, but Android tilers are strict and would produce Overlay color corruption and instability.
        // The driver performs the legal PresentSrcKhr to ColorAttachmentOptimal transition inside BeginRenderPass.
        var colorAttachment = new AttachmentDescription
        {
            Format = BackBufferFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = preserve ? AttachmentLoadOp.Load : AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = preserve ? ImageLayout.PresentSrcKhr : ImageLayout.Undefined,
            FinalLayout = ImageLayout.PresentSrcKhr
        };

        var depthAttachment = new AttachmentDescription
        {
            Format = DepthBufferFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = preserve ? AttachmentLoadOp.Load : AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.DontCare,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = preserve ? ImageLayout.DepthStencilAttachmentOptimal : ImageLayout.Undefined,
            FinalLayout = ImageLayout.DepthStencilAttachmentOptimal
        };

        var attachments = stackalloc AttachmentDescription[2] { colorAttachment, depthAttachment };

        var colorRef = new AttachmentReference(0, ImageLayout.ColorAttachmentOptimal);
        var depthRef = new AttachmentReference(1, ImageLayout.DepthStencilAttachmentOptimal);

        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorRef,
            PDepthStencilAttachment = &depthRef
        };

        // Subpass dependency from EXTERNAL to 0.
        // Color waits until the swapchain image is writable, while visibility is already guaranteed by the ImageAvailable semaphore,
        // so srcAccess needs no additional bits.
        // Depth is more subtle:
        // every framebuffer shares the same depth image, and adjacent-frame render passes can truly overlap on tiler GPUs such as Adreno and Mali.
        // A WAW memory dependency must therefore be established against the previous frame's depth writes,
        // both Early and Late stages.
        // Otherwise this frame's depth clear and writes can race against the previous frame's depth tests,
        // leaving depth contents undefined and causing intermittent depth-test failures across the whole screen,
        // the black and white flashing seen on Android.
        // Desktop IMR GPUs serialize passes in practice, so the issue does not appear there.
        // Preserve-mode also adds a RAW dependency for color writes from the previous backbuffer pass so loaded contents stay valid.
        var dependency = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
            SrcAccessMask = (preserve ? AccessFlags.ColorAttachmentWriteBit : 0) | AccessFlags.DepthStencilAttachmentWriteBit,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit | AccessFlags.DepthStencilAttachmentReadBit
        };

        var rpInfo = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 2,
            PAttachments = attachments,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 1,
            PDependencies = &dependency
        };

        if (_vk.CreateRenderPass(_device, in rpInfo, null, out var rp) != Result.Success)
            throw new Exception("vkCreateRenderPass failed");
        return rp;
    }

    void CreateFramebuffers(int width, int height, Silk.NET.Vulkan.ImageView[] swapchainImageViews)
    {
        Framebuffers = new Framebuffer[swapchainImageViews.Length];
        for (int i = 0; i < swapchainImageViews.Length; i++)
        {
            var attachments = stackalloc Silk.NET.Vulkan.ImageView[2] { swapchainImageViews[i], DepthView };
            var fbInfo = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = RenderPass,
                AttachmentCount = 2,
                PAttachments = attachments,
                Width = (uint)width,
                Height = (uint)height,
                Layers = 1
            };
            if (_vk.CreateFramebuffer(_device, in fbInfo, null, out var fb) != Result.Success)
                throw new Exception($"vkCreateFramebuffer failed for index {i}");
            Framebuffers[i] = fb;
        }
    }

    void UpdateViewportAndScissor(int width, int height)
    {
        // Negative height plus Y offset to the bottom implements Y flip on Vulkan 1.1 and later,
        // while shader output keeps following the DX and GLM left-handed convention.
        Viewport = new Silk.NET.Vulkan.Viewport
        {
            X = 0,
            Y = height,
            Width = width,
            Height = -height,
            MinDepth = 0f,
            MaxDepth = 1f
        };
        ScissorRect = new Rect2D(default, new Extent2D((uint)width, (uint)height));
    }

    uint FindMemoryType(uint typeBits, MemoryPropertyFlags required)
    {
        _vk.GetPhysicalDeviceMemoryProperties(_physical, out var memProps);
        for (uint i = 0; i < memProps.MemoryTypeCount; i++)
        {
            if ((typeBits & (1u << (int)i)) != 0 &&
                (memProps.MemoryTypes[(int)i].PropertyFlags & required) == required)
                return i;
        }
        throw new Exception("Failed to find suitable Vulkan memory type");
    }

    void ReleaseFramebuffers()
    {
        for (int i = 0; i < Framebuffers.Length; i++)
        {
            if (Framebuffers[i].Handle != 0)
                _vk.DestroyFramebuffer(_device, Framebuffers[i], null);
        }
        Framebuffers = [];
    }

    void ReleaseDepth()
    {
        if (DepthView.Handle != 0)
        {
            _vk.DestroyImageView(_device, DepthView, null);
            DepthView = default;
        }
        if (DepthImage.Handle != 0)
        {
            _vk.DestroyImage(_device, DepthImage, null);
            DepthImage = default;
        }
        if (DepthMemory.Handle != 0)
        {
            _vk.FreeMemory(_device, DepthMemory, null);
            DepthMemory = default;
        }
    }

    public void Dispose()
    {
        ReleaseFramebuffers();
        ReleaseDepth();
        if (RenderPass.Handle != 0)
        {
            _vk.DestroyRenderPass(_device, RenderPass, null);
            RenderPass = default;
        }
        if (RenderPassLoad.Handle != 0)
        {
            _vk.DestroyRenderPass(_device, RenderPassLoad, null);
            RenderPassLoad = default;
        }
    }
}
