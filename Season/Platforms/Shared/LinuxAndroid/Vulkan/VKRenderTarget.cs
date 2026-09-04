// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Vulkan;
using Image = Silk.NET.Vulkan.Image;

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

/// <summary>
/// Offscreen Vulkan RenderTarget. Supports two shapes
/// (Step 3, aligned with DX Device.CreateRenderTarget):
/// - color shape (Step 2): color image (used both as attachment and sampled texture)
///   plus its own depth buffer
///   (Vulkan framebuffer attachments must match in size and cannot share Display depth;
///   even RTs such as Post outputs that do not need depth still carry one for now,
///   and a depth-free RP variant can be added later if bandwidth becomes a concern after 1-4 lands);
/// - depth-only shape (Step 3, shadow map): depth image only
///   (again used both as attachment and sampled texture),
///   with D32Sfloat sampled through a direct view
///   (Vulkan has no DX-style typeless indirection requirement).
/// Each offscreen RT owns a dedicated RenderPass/Framebuffer + sampled descriptor set
/// (the Vulkan structure cost is higher than DX).
///
/// All layout transitions are handled by the RP's initial/finalLayout + subpass dependencies
/// (zero explicit barriers):
/// - output plane (color or shadow depth): Undefined -> attachment layout (inside the pass)
///   -> ShaderReadOnlyOptimal (finalLayout for downstream pass sampling);
///   the next frame uses initialLayout=Undefined and discards old contents anyway because the pass clears;
/// - EXTERNAL -> 0 dependency: waits for the previous frame's downstream sampling read of this image (WAR)
///   plus cross-frame WAW
///   (same rationale as the Android tiler dependency in Display.CreateRenderPass);
/// - 0 -> EXTERNAL dependency: this pass writes -> this frame's downstream fragment sampling reads (RAW).
///
/// Lifetime (FrameSchedule contract):
/// - RTs with MatchBackbufferSize are rebuilt in place through Recreate by
///   Device.HandleResize/RecreateSurfaceAndSwapChain after DeviceWaitIdle
///   (same object, same descriptor set, so external references remain valid);
///   fixed-size RTs such as shadow maps are not affected by resize;
/// - runtime destruction must go through Dispose
///   (EnqueueDeferredRelease; Android tilers must not destroy in-flight resources immediately).
/// </summary>
internal unsafe sealed class VKRenderTarget : Season.Rendering.RenderTarget
{
    public uint Width { get; private set; }

    public uint Height { get; private set; }

    /// <summary>`false` means the depth-only shape (shadow map),
    /// and all color-related fields stay at default values.</summary>
    public bool HasColor => Desc.ColorFormat != Season.Rendering.RtFormat.None;

    /// <summary>Native format of the color plane
    /// (used to keep RP/image/view consistent; valid only when HasColor is true).</summary>
    readonly Format _colorFormat;

    /// <summary>Phase 4: read-only exposure of the native color format
    /// (used to bake the OutlineMask-specific RP against the mask RT format).</summary>
    internal Format ColorFormat => _colorFormat;

    public Image ColorImage { get; private set; }

    DeviceMemory _colorMemory;

    public Silk.NET.Vulkan.ImageView ColorView { get; private set; }

    /// <summary>Version number of ColorView, incremented monotonically after each vkCreateImageView
    /// (see Device.NextViewVersion).
    /// Downstream framebuffer caches use this to detect whether the underlying view changed;
    /// it is more reliable than View.Handle because handles are heap pointers
    /// and often reuse the same value after Recreate.</summary>
    public ulong ColorViewVersion { get; private set; }

    public Image DepthImage { get; private set; }

    DeviceMemory _depthMemory;

    public Silk.NET.Vulkan.ImageView DepthView { get; private set; }

    /// <summary>Version number of DepthView (same semantics as ColorViewVersion).</summary>
    public ulong DepthViewVersion { get; private set; }

    /// <summary>Dedicated offscreen RenderPass.
    /// It is preserved across Recreate because the format does not change
    /// and it serves as the PSO compatibility anchor.</summary>
    public RenderPass RenderPass { get; private set; }

    public Framebuffer Framebuffer { get; private set; }

    /// <summary>
    /// Sample descriptor set for the output plane (BlitPipeline.SetLayout), rewritten in place on Recreate.
    /// The color shape points to ColorView (FinalBlit / Post input);
    /// the depth-only shape points to DepthView
    /// (1-5 shadow sampling uses only the nearest binding because linear filtering on D32 is optional).
    /// </summary>
    public DescriptorSet SampleDescriptorSet { get; private set; }

    public VKRenderTarget(in Season.Rendering.RenderTargetDesc desc, uint width, uint height)
    {
        Desc = desc;
        if (HasColor)
            _colorFormat = ToNativeColorFormat(desc.ColorFormat);

        if (HasColor)
            CreateColorRenderPass();
        else
            CreateDepthOnlyRenderPass();

        CreateSizeDependentResources(width, height);
        SampleDescriptorSet = BlitPipeline.CreateSourceDescriptor(
            HasColor ? ColorView : DepthView, linearBinding: HasColor);
    }

    static Format ToNativeColorFormat(Season.Rendering.RtFormat format) => format switch
    {
        Season.Rendering.RtFormat.BackbufferCompatible => Device.BackBufferFormat,
        Season.Rendering.RtFormat.Rgba16Float => Format.R16G16B16A16Sfloat,
        Season.Rendering.RtFormat.Rg16Float => Format.R16G16Sfloat,
        _ => throw new NotSupportedException($"[VKRenderTarget] Unsupported color format {format}."),
    };

    /// <summary>
    /// In-place rebuild during resize (called only from the MatchBackbufferSize path).
    /// Preconditions: the caller has already performed DeviceWaitIdle
    /// (both HandleResize and RecreateSurfaceAndSwapChain satisfy this),
    /// there are no in-flight GPU references, old resources can be destroyed immediately,
    /// and the descriptor set is rewritten in place so FinalBlit bindings stay valid.
    /// </summary>
    public void Recreate(uint width, uint height)
    {
        ReleaseSizeDependentResources();
        CreateSizeDependentResources(width, height);
        BlitPipeline.UpdateSourceDescriptor(SampleDescriptorSet,
            HasColor ? ColorView : DepthView, linearBinding: HasColor);
    }

    void CreateColorRenderPass()
    {
        RenderPass = CreateColorRenderPassForFormat(_colorFormat);
    }

    /// <summary>
    /// Create the offscreen color RP for a given color format
    /// (attachment structure and subpass dependencies are fully isomorphic to the instance RP).
    /// 1-4 HDR: Pipeline.Init uses this method in HDR mode to build an RGBA16F-compatible RP
    /// as the main PSO bake anchor.
    /// It is created by the same construction path as the SceneColor instance RP,
    /// so render-pass compatibility
    /// (same attachment formats/count/sample counts) is guaranteed by the code path.
    /// </summary>
    internal static RenderPass CreateColorRenderPassForFormat(Format colorFormat)
    {
        var colorAttachment = new AttachmentDescription
        {
            Format = colorFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            // Automatically transition to sampled layout at the end of rendering;
            // FinalBlit can sample directly with no explicit barrier
            FinalLayout = ImageLayout.ShaderReadOnlyOptimal
        };

        var depthAttachment = new AttachmentDescription
        {
            Format = Device.DepthBufferFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.DontCare,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
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

        var dependencies = stackalloc SubpassDependency[2]
        {
            // EXTERNAL -> 0: sampling read of this color image by the previous frame's FinalBlit (WAR)
            // plus cross-frame WAW on the owned depth image
            // (tiler GPUs can run adjacent-frame passes truly in parallel; see Display.CreateRenderPass notes)
            new SubpassDependency
            {
                SrcSubpass = Vk.SubpassExternal,
                DstSubpass = 0,
                SrcStageMask = PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
                SrcAccessMask = AccessFlags.ShaderReadBit | AccessFlags.DepthStencilAttachmentWriteBit,
                DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
                DstAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit | AccessFlags.DepthStencilAttachmentReadBit
            },
            // 0 -> EXTERNAL: color writes complete -> fragment sampling read by this frame's FinalBlit (RAW)
            new SubpassDependency
            {
                SrcSubpass = 0,
                DstSubpass = Vk.SubpassExternal,
                SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
                SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
                DstStageMask = PipelineStageFlags.FragmentShaderBit,
                DstAccessMask = AccessFlags.ShaderReadBit
            }
        };

        var rpInfo = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 2,
            PAttachments = attachments,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 2,
            PDependencies = dependencies
        };

        if (Device.Vk.CreateRenderPass(Device.LogicalDevice, in rpInfo, null, out var rp) != Result.Success)
            throw new Exception("vkCreateRenderPass (offscreen) failed");
        return rp;
    }

    /// <summary>
    /// 2-3 Step D: three-target Scene RP
    /// (SceneColor + SceneVelocity R16G16Float + explicit SceneDepth).
    /// Isomorphic to CreateColorRenderPassForFormat, so render-pass compatibility holds.
    /// Differences:
    /// - adds an R16G16Float velocity attachment (slot 1) with Store + finalLayout=ShaderReadOnlyOptimal
    ///   (for AfterScene velocity readback / TAA consumption, with zero explicit barriers);
    /// - depth uses Store + finalLayout=ShaderReadOnlyOptimal
    ///   (same as DualTarget, for DepthTexture consumption);
    /// - the subpass has 2 color attachments (slot 0 = color, slot 1 = velocity).
    /// </summary>
    internal static RenderPass CreateVelocityRenderPassForFormat(Format colorFormat)
    {
        var colorAttachment = new AttachmentDescription
        {
            Format = colorFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.ShaderReadOnlyOptimal
        };

        var velocityAttachment = new AttachmentDescription
        {
            Format = Format.R16G16Sfloat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.ShaderReadOnlyOptimal
        };

        var depthAttachment = new AttachmentDescription
        {
            Format = Device.DepthBufferFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.ShaderReadOnlyOptimal
        };

        var attachments = stackalloc AttachmentDescription[3]
            { colorAttachment, velocityAttachment, depthAttachment };

        var colorRefs = stackalloc AttachmentReference[2]
        {
            new(0, ImageLayout.ColorAttachmentOptimal),
            new(1, ImageLayout.ColorAttachmentOptimal)
        };
        var depthRef = new AttachmentReference(2, ImageLayout.DepthStencilAttachmentOptimal);

        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 2,
            PColorAttachments = colorRefs,
            PDepthStencilAttachment = &depthRef
        };

        var dependencies = stackalloc SubpassDependency[2]
        {
            new SubpassDependency
            {
                SrcSubpass = Vk.SubpassExternal,
                DstSubpass = 0,
                SrcStageMask = PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
                SrcAccessMask = AccessFlags.ShaderReadBit | AccessFlags.DepthStencilAttachmentWriteBit,
                DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
                DstAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit | AccessFlags.DepthStencilAttachmentReadBit
            },
            new SubpassDependency
            {
                SrcSubpass = 0,
                DstSubpass = Vk.SubpassExternal,
                SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.LateFragmentTestsBit,
                SrcAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit,
                DstStageMask = PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
                DstAccessMask = AccessFlags.ShaderReadBit
            }
        };

        var rpInfo = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 3,
            PAttachments = attachments,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 2,
            PDependencies = dependencies
        };

        if (Device.Vk.CreateRenderPass(Device.LogicalDevice, in rpInfo, null, out var rp) != Result.Success)
            throw new Exception("vkCreateRenderPass (velocity scene) failed");
        return rp;
    }

    /// <summary>
    /// 2-2 dual-target Scene RP
    /// (SceneColor + explicit SceneDepth, contract clause 2):
    /// the attachment structure/format/sample count are fully isomorphic to
    /// CreateColorRenderPassForFormat
    /// (render-pass compatibility holds, so Scene PSOs need no extra variants).
    /// Differences only:
    /// - the depth attachment uses Store + finalLayout=ShaderReadOnlyOptimal
    ///   (StoreDepth semantics, consumed by AfterScene compute through DepthTexture binding,
    ///   with zero explicit barriers);
    /// - both dependencies include ComputeShaderBit:
    ///   EXTERNAL -> 0 waits for the previous frame's compute sampling reads of color/depth (WAR),
    ///   and 0 -> EXTERNAL covers this frame's compute reads
    ///   (gtaoMain reads depth, bloom prefilter reads color).
    /// The matching framebuffer is created by Device from SceneColor.ColorView + SceneDepth.DepthView.
    /// </summary>
    internal static RenderPass CreateDualTargetRenderPassForFormat(Format colorFormat)
    {
        var colorAttachment = new AttachmentDescription
        {
            Format = colorFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.ShaderReadOnlyOptimal
        };

        var depthAttachment = new AttachmentDescription
        {
            Format = Device.DepthBufferFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.ShaderReadOnlyOptimal
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

        var dependencies = stackalloc SubpassDependency[2]
        {
            // EXTERNAL -> 0: previous-frame FinalBlit / compute sampling reads of color/depth (WAR)
            // plus cross-frame WAW
            new SubpassDependency
            {
                SrcSubpass = Vk.SubpassExternal,
                DstSubpass = 0,
                SrcStageMask = PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
                SrcAccessMask = AccessFlags.ShaderReadBit | AccessFlags.DepthStencilAttachmentWriteBit,
                DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
                DstAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit | AccessFlags.DepthStencilAttachmentReadBit
            },
            // 0 -> EXTERNAL: color/depth writes complete -> this frame's compute sampling reads
            // (gtaoMain / bloom) + FinalBlit fragment reads (RAW)
            new SubpassDependency
            {
                SrcSubpass = 0,
                DstSubpass = Vk.SubpassExternal,
                SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.LateFragmentTestsBit,
                SrcAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit,
                DstStageMask = PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
                DstAccessMask = AccessFlags.ShaderReadBit
            }
        };

        var rpInfo = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 2,
            PAttachments = attachments,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 2,
            PDependencies = dependencies
        };

        if (Device.Vk.CreateRenderPass(Device.LogicalDevice, in rpInfo, null, out var rp) != Result.Success)
            throw new Exception("vkCreateRenderPass (dual-target scene) failed");
        return rp;
    }

    /// <summary>
    /// Outline2D mask RP (color = BackbufferCompatible + depth = DepthBufferFormat):
    /// - color: Clear/Store
    ///   (the mask is fully rewritten every frame), finalLayout=ShaderReadOnlyOptimal
    ///   for FinalBlit compositing;
    /// - depth: LoadOp=Load
    ///   (keep scene depth for occlusion testing - the depth source is either SceneColor's owned depth
    ///   or SceneDepth, both already written by the Scene pass),
    ///   InitialLayout=DepthStencilAttachmentOptimal
    ///   (SceneColor depth is naturally in this layout under the standard tier;
    ///   SceneDepth is in ShaderReadOnlyOptimal under the AO tier and is explicitly transitioned by
    ///   Device.BeginPass),
    ///   finalLayout=ShaderReadOnlyOptimal
    ///   (SceneDepth is later consumed in sampled layout under the AO tier);
    /// - mask primitives use depthWrite=false, so they do not write depth,
    ///   and StoreOp=DontCare saves bandwidth.
    /// The attachment structure is isomorphic to CreateColorRenderPassForFormat
    /// (render-pass compatibility holds), with differences only in depth load/layout.
    /// </summary>
    internal static RenderPass CreateOutlineMaskRenderPassForFormat(Format colorFormat)
    {
        var colorAttachment = new AttachmentDescription
        {
            Format = colorFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.ShaderReadOnlyOptimal
        };

        var depthAttachment = new AttachmentDescription
        {
            Format = Device.DepthBufferFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Load,
            StoreOp = AttachmentStoreOp.DontCare,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.DepthStencilAttachmentOptimal,
            FinalLayout = ImageLayout.ShaderReadOnlyOptimal
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

        var dependencies = stackalloc SubpassDependency[2]
        {
            // EXTERNAL -> 0: previous-frame FinalBlit sampling read of the color image (WAR)
            // plus cross-frame WAW;
            // depth uses Load, so we must also wait for previous-frame / Scene-pass depth read-write completion
            // (DS attachment access)
            new SubpassDependency
            {
                SrcSubpass = Vk.SubpassExternal,
                DstSubpass = 0,
                SrcStageMask = PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
                SrcAccessMask = AccessFlags.ShaderReadBit | AccessFlags.DepthStencilAttachmentWriteBit | AccessFlags.DepthStencilAttachmentReadBit,
                DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
                DstAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentReadBit
            },
            // 0 -> EXTERNAL: color writes complete -> this frame's FinalBlit fragment sampling reads (RAW)
            new SubpassDependency
            {
                SrcSubpass = 0,
                DstSubpass = Vk.SubpassExternal,
                SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
                SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
                DstStageMask = PipelineStageFlags.FragmentShaderBit,
                DstAccessMask = AccessFlags.ShaderReadBit
            }
        };

        var rpInfo = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 2,
            PAttachments = attachments,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 2,
            PDependencies = dependencies
        };

        if (Device.Vk.CreateRenderPass(Device.LogicalDevice, in rpInfo, null, out var rp) != Result.Success)
            throw new Exception("vkCreateRenderPass (outline mask) failed");
        return rp;
    }

    /// <summary>
    /// Depth-only RP (shadow map): a single D32 attachment with Clear/Store
    /// (StoreDepth=true, so depth is itself the output),
    /// and finalLayout=ShaderReadOnlyOptimal for direct sampling by later passes.
    /// </summary>
    void CreateDepthOnlyRenderPass()
    {
        var depthAttachment = new AttachmentDescription
        {
            Format = Device.DepthBufferFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            // Automatically transition to sampled layout at the end of rendering;
            // later passes can sample directly with no explicit barrier
            FinalLayout = ImageLayout.ShaderReadOnlyOptimal
        };

        var depthRef = new AttachmentReference(0, ImageLayout.DepthStencilAttachmentOptimal);

        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 0,
            PDepthStencilAttachment = &depthRef
        };

        var dependencies = stackalloc SubpassDependency[2]
        {
            // EXTERNAL -> 0: previous-frame sampling read of this depth image (WAR)
            // plus cross-frame depth WAW
            new SubpassDependency
            {
                SrcSubpass = Vk.SubpassExternal,
                DstSubpass = 0,
                SrcStageMask = PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
                SrcAccessMask = AccessFlags.ShaderReadBit | AccessFlags.DepthStencilAttachmentWriteBit,
                DstStageMask = PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
                DstAccessMask = AccessFlags.DepthStencilAttachmentWriteBit | AccessFlags.DepthStencilAttachmentReadBit
            },
            // 0 -> EXTERNAL: depth writes complete -> this frame's later fragment sampling reads (RAW)
            new SubpassDependency
            {
                SrcSubpass = 0,
                DstSubpass = Vk.SubpassExternal,
                SrcStageMask = PipelineStageFlags.LateFragmentTestsBit,
                SrcAccessMask = AccessFlags.DepthStencilAttachmentWriteBit,
                DstStageMask = PipelineStageFlags.FragmentShaderBit,
                DstAccessMask = AccessFlags.ShaderReadBit
            }
        };

        var rpInfo = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &depthAttachment,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 2,
            PDependencies = dependencies
        };

        if (Device.Vk.CreateRenderPass(Device.LogicalDevice, in rpInfo, null, out var rp) != Result.Success)
            throw new Exception("vkCreateRenderPass (depth-only) failed");
        RenderPass = rp;
    }

    void CreateSizeDependentResources(uint width, uint height)
    {
        Width = width;
        Height = height;

        if (HasColor)
        {
            // color: used both as attachment and sampled texture
            (ColorImage, _colorMemory, ColorView) = CreateImage(
                width, height, _colorFormat,
                ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit,
                ImageAspectFlags.ColorBit);
            ColorViewVersion = Device.NextViewVersion();

            // depth: owned by the offscreen target
            // (Vulkan framebuffer attachment sizes must match, so it cannot share Display depth)
            (DepthImage, _depthMemory, DepthView) = CreateImage(
                width, height, Device.DepthBufferFormat,
                ImageUsageFlags.DepthStencilAttachmentBit,
                ImageAspectFlags.DepthBit);
            DepthViewVersion = Device.NextViewVersion();
        }
        else
        {
            // depth-only: used both as attachment and sampled texture (shadow map)
            (DepthImage, _depthMemory, DepthView) = CreateImage(
                width, height, Device.DepthBufferFormat,
                ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit,
                ImageAspectFlags.DepthBit);
            DepthViewVersion = Device.NextViewVersion();
        }

        var attachments = stackalloc Silk.NET.Vulkan.ImageView[2] { HasColor ? ColorView : DepthView, DepthView };
        var fbInfo = new FramebufferCreateInfo
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = RenderPass,
            AttachmentCount = HasColor ? 2u : 1u,
            PAttachments = attachments,
            Width = width,
            Height = height,
            Layers = 1
        };
        if (Device.Vk.CreateFramebuffer(Device.LogicalDevice, in fbInfo, null, out var fb) != Result.Success)
            throw new Exception("vkCreateFramebuffer (offscreen) failed");
        Framebuffer = fb;
    }

    static (Image, DeviceMemory, Silk.NET.Vulkan.ImageView) CreateImage(uint width, uint height, Format format, ImageUsageFlags usage, ImageAspectFlags aspect)
    {
        var imgInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D(width, height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined
        };
        if (Device.Vk.CreateImage(Device.LogicalDevice, in imgInfo, null, out var img) != Result.Success)
            throw new Exception("vkCreateImage (offscreen RT) failed");

        Device.Vk.GetImageMemoryRequirements(Device.LogicalDevice, img, out var memReq);
        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = Device.ResourceManager.FindMemoryType(memReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit)
        };
        if (Device.Vk.AllocateMemory(Device.LogicalDevice, in allocInfo, null, out var mem) != Result.Success)
            throw new Exception("vkAllocateMemory (offscreen RT) failed");
        Device.Vk.BindImageMemory(Device.LogicalDevice, img, mem, 0);

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = img,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange(aspect, 0, 1, 0, 1)
        };
        if (Device.Vk.CreateImageView(Device.LogicalDevice, in viewInfo, null, out var view) != Result.Success)
            throw new Exception("vkCreateImageView (offscreen RT) failed");

        return (img, mem, view);
    }

    void ReleaseSizeDependentResources()
    {
        var vk = Device.Vk;
        var device = Device.LogicalDevice;

        if (Framebuffer.Handle != 0) { vk.DestroyFramebuffer(device, Framebuffer, null); Framebuffer = default; }
        if (ColorView.Handle != 0) { vk.DestroyImageView(device, ColorView, null); ColorView = default; }
        if (ColorImage.Handle != 0) { vk.DestroyImage(device, ColorImage, null); ColorImage = default; }
        if (_colorMemory.Handle != 0) { vk.FreeMemory(device, _colorMemory, null); _colorMemory = default; }
        if (DepthView.Handle != 0) { vk.DestroyImageView(device, DepthView, null); DepthView = default; }
        if (DepthImage.Handle != 0) { vk.DestroyImage(device, DepthImage, null); DepthImage = default; }
        if (_depthMemory.Handle != 0) { vk.FreeMemory(device, _depthMemory, null); _depthMemory = default; }
    }

    /// <summary>Runtime destruction: all GPU objects go through deferred release via
    /// <see cref="Device.EnqueueDeferredRelease(Action)"/>
    /// (see the deferred-release contract in Device.cs).
    /// In-flight command buffers may still reference this RT's view/image/framebuffer
    /// (AfterScene compute, downstream pass sampling, descriptor-set cache hits),
    /// so it follows the same pattern as Texture.RecreateComputeStorage:
    /// capture handle snapshots, clear fields immediately, and destroy the snapshots in the deferred action
    /// after the timeline fence passes the retire value.
    /// Semantically equivalent to the D3D12-side
    /// <c>Graphics.EnqueueDeferredRelease(DirectX.Device.GetCurrentRetireFenceValue(), ...)</c>.</summary>
    public override void Dispose()
    {
        Device.OffscreenTargets.Remove(this);

        // Capture handle snapshots, clear fields immediately,
        // and destroy the snapshots in the deferred action
        // (the object itself can be discarded right away)
        var fb = Framebuffer; Framebuffer = default;
        var colorView = ColorView; ColorView = default;
        var colorImage = ColorImage; ColorImage = default;
        var colorMemory = _colorMemory; _colorMemory = default;
        var depthView = DepthView; DepthView = default;
        var depthImage = DepthImage; DepthImage = default;
        var depthMemory = _depthMemory; _depthMemory = default;
        var rp = RenderPass; RenderPass = default;
        var blitSet = SampleDescriptorSet; SampleDescriptorSet = default;

        Device.EnqueueDeferredRelease(() =>
        {
            var vk = Device.Vk;
            var device = Device.LogicalDevice;
            if (blitSet.Handle != 0) Device.DescriptorAllocator.FreeSet(blitSet);
            if (fb.Handle != 0) vk.DestroyFramebuffer(device, fb, null);
            if (colorView.Handle != 0) vk.DestroyImageView(device, colorView, null);
            if (colorImage.Handle != 0) vk.DestroyImage(device, colorImage, null);
            if (colorMemory.Handle != 0) vk.FreeMemory(device, colorMemory, null);
            if (depthView.Handle != 0) vk.DestroyImageView(device, depthView, null);
            if (depthImage.Handle != 0) vk.DestroyImage(device, depthImage, null);
            if (depthMemory.Handle != 0) vk.FreeMemory(device, depthMemory, null);
            if (rp.Handle != 0) vk.DestroyRenderPass(device, rp, null);
        });
    }
}
