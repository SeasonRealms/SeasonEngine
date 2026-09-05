// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Vulkan;
using Image = Silk.NET.Vulkan.Image;
using System.Runtime.CompilerServices;
using Season.Fonts;

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

/// <summary>
/// Vulkan texture aligned one to one with DX12 DXTexture:
///   - creates a VkImage plus DeviceMemory plus VkImageView, using R8G8B8A8 Unorm and mip=1
///   - actual pixel copying is submitted once by TextureUploadBatch on the transfer queue
///   - UploadFenceValue stores the transfer-queue timeline semaphore value; before first use on the graphics queue,
///     submit must add it to PWaitSemaphores and then call EnsureReadyForRendering for layout transition
///   - cross-queue-family resource ownership is avoided through SharingMode.Concurrent, so no explicit acquire or release barrier is needed
/// </summary>
internal unsafe class Texture : IDisposable
{
    public string Name = string.Empty;

    public bool Ready;

    /// <summary>Value reached by the transfer-queue timeline semaphore when upload completes. 0 means no wait is needed anymore.</summary>
    public ulong UploadFenceValue;

    public Image Image;

    public DeviceMemory Memory;

    public Silk.NET.Vulkan.ImageView View;

    /// <summary>
    /// Monotonically increasing identity number for the View, incremented after each successful vkCreateImageView.
    /// Downstream caches, descriptor sets and framebuffers, must use it to determine whether the underlying view changed.
    /// They must not compare View.Handle:
    /// the handle is just a heap pointer, and destroy plus recreate often returns the same address.
    /// The numeric value may match while the image is already different, so comparing handles would silently miss the change.
    /// </summary>
    public ulong ViewVersion;

    public uint Width;

    public uint Height;

    public Format Format = Silk.NET.Vulkan.Format.R8G8B8A8Unorm;

    /// <summary>Current image layout. Starts at Undefined, stays at TransferDstOptimal after transfer completes, and transitions to ShaderReadOnlyOptimal on first graphics use.</summary>
    public ImageLayout CurrentLayout = ImageLayout.Undefined;

    /// <summary>Raw pixel data, RGBA8. It can be discarded after TextureUploadBatch copies it into the staging buffer.</summary>
    public byte[]? ImageData;

    /// <summary>
    /// 2-6 clause 4: mip level count of this image. Stays 1 unless MipChain.ShouldGenerate approved a chain for the
    /// requested policy, so every pre-2-6 caller keeps the single-level layout untouched.
    /// </summary>
    public uint MipLevels = 1;

    /// <summary>
    /// Geometry of each level inside <see cref="ImageData"/>, tightly packed with no row padding. Vulkan needs no
    /// per-level pitch conversion - BufferRowLength 0 means "derive from ImageExtent" - so the byte offset carried
    /// here is directly usable as BufferOffset, which is why no upload-heap counterpart is needed as on D3D12.
    /// </summary>
    public MipLevelInfo[]? MipInfos;

    /// <summary>
    /// The policy this texture was created with, retained so in-place replacement through <see cref="UploadPixels"/>
    /// can regenerate the chain instead of leaving levels 1..N holding stale content.
    /// </summary>
    TextureMipPolicy _mipPolicy = TextureMipPolicy.None;

    /// <summary>
    /// Key this texture occupies in Device.DictionaryTexture, which is not always <see cref="Name"/>: once a mip
    /// policy participates in cache identity the key carries a suffix. Dispose must remove that exact key, otherwise
    /// the dictionary would keep handing a released texture to the next GetOrCreate with the same policy.
    /// Empty for textures registered by other paths, which key on Name.
    /// </summary>
    string _cacheKey = string.Empty;

    int _refCount = 1;

    public int RefCount => _refCount;

    public void AddRef() => Interlocked.Increment(ref _refCount);

    public void Release()
    {
        if (Interlocked.Decrement(ref _refCount) == 0) Dispose();
    }

    void ProcessImageResult(INativeImageDecoder imageResult, TextureMipPolicy mipPolicy)
    {
        Width = (uint)imageResult.Width;
        Height = (uint)imageResult.Height;

        // Normalize to tightly-packed RGBA.
        // Source may be RGB (3 bytes/pixel, e.g. Gdk.Pixbuf) or RGBA with/without row padding.
        int srcStride = imageResult.Stride;
        int tightStride = (int)Width * 4;
        var src = imageResult.PixelSpan;

        if (srcStride == tightStride)
        {
            // RGBA, tightly-packed — fast path.
            ImageData = src.ToArray();
        }
        else if (srcStride >= tightStride)
        {
            // RGBA with row-alignment padding — copy only the pixel data per row.
            ImageData = new byte[tightStride * (int)Height];
            for (int y = 0; y < Height; y++)
                src.Slice(y * srcStride, tightStride).CopyTo(ImageData.AsSpan(y * tightStride));
        }
        else
        {
            // Source has fewer bytes per row — typically RGB (e.g. Gdk.Pixbuf default).
            // Convert RGB → RGBA pixel by pixel, adding opaque alpha.
            ImageData = new byte[tightStride * (int)Height];
            // Approximate source bytes-per-pixel from stride (e.g. 3 for RGB).
            int srcBpp = srcStride / (int)Width;
            for (int y = 0; y < Height; y++)
            {
                int srcOffset = y * srcStride;
                int dstOffset = y * tightStride;
                for (int x = 0; x < Width; x++)
                {
                    int si = srcOffset + x * srcBpp;
                    int di = dstOffset + x * 4;
                    ImageData[di]     = src[si];       // R
                    ImageData[di + 1] = src[si + 1];   // G
                    ImageData[di + 2] = src[si + 2];   // B
                    ImageData[di + 3] = 255;            // A (fully opaque)
                }
            }
        }

        BuildMipChain(mipPolicy);

        CreateImageResource();
        CreateImageView();

        Device.TextureUploadBatch.AddTextureUpload(this);
    }

    /// <summary>
    /// 2-6 clause 3: the de-strided buffer produced above is exactly the tightly packed input MipChain.Build requires,
    /// which is why generation belongs here - this is the first point where the pixels are in a known-dense layout and
    /// the last point before the image is created with a level count baked into it.
    /// </summary>
    void BuildMipChain(TextureMipPolicy mipPolicy)
    {
        if (ImageData != null && MipChain.ShouldGenerate(mipPolicy, (int)Width, (int)Height))
        {
            ImageData = MipChain.Build(ImageData, (int)Width, (int)Height, mipPolicy, out var infos);
            MipInfos = infos;
            MipLevels = (uint)infos.Length;
            _mipPolicy = mipPolicy;
        }
        else
        {
            MipInfos = [new MipLevelInfo((int)Width, (int)Height, 0)];
            MipLevels = 1;
        }
    }

    internal Texture(INativeImageDecoder imageResult, TextureMipPolicy mipPolicy = TextureMipPolicy.None)
    {
        ProcessImageResult(imageResult, mipPolicy);
    }

    internal Texture(string name, SharpGLTF.Schema2.Image? image, TextureMipPolicy mipPolicy = TextureMipPolicy.None)
    {
        Name = name;
        INativeImageDecoder imageResult;

        if (name is "White")
        {
            imageResult = new NativeImageData(1, 1, new byte[] { 255, 255, 255, 255 });
        }
        else if (image != null)
        {
            using Stream stream = image.Content.Open();
            imageResult = ImageUtils.GetImageFromStream(stream, null);
        }
        else
        {
            using Stream stream = File.Open(name, FileMode.Open);
            imageResult = ImageUtils.GetImageFromStream(stream, null);
        }

        ProcessImageResult(imageResult, mipPolicy);
    }

    internal static Texture GetOrCreate(string name, SharpGLTF.Schema2.Image? image,
        TextureMipPolicy mipPolicy = TextureMipPolicy.None)
    {
        // 2-6 clause 4: the policy is part of the cache identity. The same image can legitimately be bound as base
        // colour in one material and as a normal map in another, and those two need different chains (one box
        // filtered, one renormalized). The suffix is only appended for non-default policies so every pre-2-6 key
        // stays byte-identical.
        string key = mipPolicy == TextureMipPolicy.None ? name : $"{name}#mip{mipPolicy}";

        if (Device.DictionaryTexture.TryGetValue(key, out var texture))
        {
            texture.AddRef();
            return texture;
        }

        texture = new Texture(name, image, mipPolicy);
        texture._cacheKey = key;
        Device.DictionaryTexture.Add(key, texture);
        return texture;
    }

    /// <summary>Create a new texture directly from decoded pixels. It is not added to the global cache and its lifetime is managed by the caller.</summary>
    internal static Texture CreateFromDecoder(INativeImageDecoder decoder,
        TextureMipPolicy mipPolicy = TextureMipPolicy.None)
    {
        return new Texture(decoder, mipPolicy);
    }

    /// <summary>
    /// Update texture pixel content in place, with the size required to match the current GPU texture.
    /// Implemented through staging buffer to vkCmdCopyBufferToImage, while Image and ImageView stay unchanged.
    /// </summary>
    public void UploadPixels(ReadOnlySpan<byte> rgbaPixels)
    {
        int expectedSize = (int)(Width * Height * 4);
        if (rgbaPixels.Length != expectedSize)
            throw new ArgumentException(
                $"Pixel data size mismatch. Expected {expectedSize} bytes for {Width}×{Height}, got {rgbaPixels.Length}.");

        var vk = Device.Vk;
        var d = Device.LogicalDevice;
        var rm = Device.ResourceManager;

        // 2-6 clause 4: in-place replacement has to refresh the whole chain. The incoming span only describes level 0,
        // so if this texture owns a chain the lower levels are regenerated here - otherwise they would keep showing the
        // previous content at distance, which is far harder to diagnose than no mipmaps at all.
        byte[]? chain = null;
        MipLevelInfo[]? chainInfos = null;
        if (MipLevels > 1)
        {
            chain = MipChain.Build(rgbaPixels, (int)Width, (int)Height, _mipPolicy, out chainInfos);
        }

        ulong size = (ulong)(chain?.Length ?? expectedSize);

        // 1. Create the staging buffer, HOST_VISIBLE | HOST_COHERENT.
        var staging = rm.CreateBuffer(size,
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        try
        {
            void* mapped;
            vk.MapMemory(d, staging.Memory, 0, size, 0, &mapped);
            if (chain != null)
            {
                fixed (byte* src = chain)
                    Unsafe.CopyBlock(mapped, src, (uint)chain.Length);
            }
            else
            {
                fixed (byte* src = rgbaPixels)
                    Unsafe.CopyBlock(mapped, src, (uint)expectedSize);
            }
            vk.UnmapMemory(d, staging.Memory);

            // 2. Allocate a one-time transfer command buffer.
            var allocInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1
            };
            // Get the transfer command pool from Device, using the TextureUploadBatch pool.
            allocInfo.CommandPool = Device.TextureUploadBatch.Pool;
            CommandBuffer cmd;
            vk.AllocateCommandBuffers(d, in allocInfo, out cmd);

            var beginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit
            };
            vk.BeginCommandBuffer(cmd, in beginInfo);

            // 3. Barrier: CurrentLayout → TransferDst
            // Note:
            // this command buffer executes on the transfer queue.
            // A dedicated transfer family does not support the FragmentShader stage,
            // so queue-independent stages, AllCommands, must be used.
            // Cross-queue visibility is guaranteed by QueueWaitIdle after submission.
            TransitionTo(cmd, ImageLayout.TransferDstOptimal,
                PipelineStageFlags.AllCommandsBit, PipelineStageFlags.TransferBit,
                0, AccessFlags.TransferWriteBit);

            // 4. CopyBufferToImage, one region per subresource. Every level is tightly packed, so its byte offset in
            // the staging buffer is already a multiple of the 4-byte RGBA8 texel size that vkCmdCopyBufferToImage
            // demands, and BufferRowLength 0 lets the driver derive the pitch from ImageExtent.
            var copies = new BufferImageCopy[MipLevels];
            for (uint level = 0; level < MipLevels; level++)
            {
                var info = chainInfos != null
                    ? chainInfos[level]
                    : new MipLevelInfo((int)Width, (int)Height, 0);
                copies[level] = new BufferImageCopy
                {
                    BufferOffset = (ulong)info.ByteOffset,
                    BufferRowLength = 0,
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
            fixed (BufferImageCopy* pCopies = copies)
            {
                vk.CmdCopyBufferToImage(cmd, staging.Buffer, Image,
                    ImageLayout.TransferDstOptimal, MipLevels, pCopies);
            }

            // 5. Barrier: TransferDst to ShaderReadOnly.
            // The transfer queue must not reference FragmentShader stages or accesses.
            // Visibility to the graphics queue is guaranteed by QueueWaitIdle plus the implicit dependency of the later submission.
            TransitionTo(cmd, ImageLayout.ShaderReadOnlyOptimal,
                PipelineStageFlags.TransferBit, PipelineStageFlags.AllCommandsBit,
                AccessFlags.TransferWriteBit, 0);

            vk.EndCommandBuffer(cmd);

            // 6. Submit to the transfer queue and wait.
            var submitInfo = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &cmd
            };
            vk.QueueSubmit(Device.TransferCommandQueue.NativeQueue, 1, in submitInfo, default);
            vk.QueueWaitIdle(Device.TransferCommandQueue.NativeQueue);
            vk.FreeCommandBuffers(d, Device.TextureUploadBatch.Pool, 1, in cmd);

            CurrentLayout = ImageLayout.ShaderReadOnlyOptimal;
            UploadFenceValue = 0;
        }
        finally
        {
            rm.DestroyBuffer(staging);
        }
    }

    void CreateImageResource(ImageUsageFlags usage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit)
    {
        // Use Concurrent when graphics and transfer belong to different families to avoid ownership barriers.
        bool concurrent = Device.GraphicsQueueFamily != Device.TransferQueueFamily;
        var families = stackalloc uint[2] { Device.GraphicsQueueFamily, Device.TransferQueueFamily };

        var info = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = Format,
            Extent = new Extent3D(Width, Height, 1),
            MipLevels = MipLevels,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = usage,
            SharingMode = concurrent ? SharingMode.Concurrent : SharingMode.Exclusive,
            QueueFamilyIndexCount = concurrent ? 2u : 0u,
            PQueueFamilyIndices = concurrent ? families : null,
            InitialLayout = ImageLayout.Undefined
        };

        if (Device.Vk.CreateImage(Device.LogicalDevice, in info, null, out Image) != Result.Success)
            throw new Exception("vkCreateImage failed");

        Device.Vk.GetImageMemoryRequirements(Device.LogicalDevice, Image, out var memReq);

        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = Device.ResourceManager.FindMemoryType(memReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit)
        };

        if (Device.Vk.AllocateMemory(Device.LogicalDevice, in allocInfo, null, out Memory) != Result.Success)
            throw new Exception("vkAllocateMemory (image) failed");

        Device.Vk.BindImageMemory(Device.LogicalDevice, Image, Memory, 0);
        CurrentLayout = ImageLayout.Undefined;
    }

    void CreateImageView()
    {
        var info = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = Image,
            ViewType = ImageViewType.Type2D,
            Format = Format,
            Components = new ComponentMapping(
                ComponentSwizzle.Identity, ComponentSwizzle.Identity,
                ComponentSwizzle.Identity, ComponentSwizzle.Identity),
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = MipLevels,
                BaseArrayLayer = 0,
                LayerCount = 1
            }
        };

        if (Device.Vk.CreateImageView(Device.LogicalDevice, in info, null, out View) != Result.Success)
            throw new Exception("vkCreateImageView failed");

        ViewVersion = Device.NextViewVersion();
    }

    /// <summary>
    /// Perform the image-layout transition on the graphics command buffer.
    /// </summary>
    public void TransitionTo(CommandBuffer cmd, ImageLayout newLayout,
        PipelineStageFlags srcStage, PipelineStageFlags dstStage,
        AccessFlags srcAccess, AccessFlags dstAccess)
    {
        if (CurrentLayout == newLayout) return;

        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = CurrentLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = Image,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                // 2-6 clause 4: the whole chain transitions as one unit. Levels are never in different layouts here,
                // because every level is written by the same upload command buffer and read by the same sampler.
                LevelCount = MipLevels,
                BaseArrayLayer = 0,
                LayerCount = 1
            },
            SrcAccessMask = srcAccess,
            DstAccessMask = dstAccess
        };

        Device.Vk.CmdPipelineBarrier(
            cmd, srcStage, dstStage,
            0, 0, null, 0, null, 1, in barrier);

        CurrentLayout = newLayout;
    }

    /// <summary>
    /// Aligned with the DX-side EnsureReadyForRendering:
    ///   - UploadFenceValue must be expressed by the caller through PWaitSemaphores during graphics submit, because Vulkan does not support inserting cross-queue waits during recording
    ///   - this method is responsible only for transitioning the layout to ShaderReadOnlyOptimal
    /// </summary>
    public void EnsureReadyForRendering(CommandBuffer cmd)
    {
        if (CurrentLayout == ImageLayout.ShaderReadOnlyOptimal) return;

        // Vulkan forbids recording layout-transition barriers inside a render pass, except self-dependencies.
        // Desktop drivers are tolerant, but Android tilers, Adreno and Mali, can break tile rendering and produce black screens or artifacts.
        // When requested inside a pass, delay the transition to the out-of-pass stage of the next BeforeRender.
        if (Device.InRenderPass)
        {
            Device.DeferTextureTransition(this);
            return;
        }

        // The source may be either Undefined, upload not completed yet, or TransferDstOptimal, upload completed.
        // Visibility of transfer writes is guaranteed by the timeline semaphore signal plus the CPU-side wait chain.
        TransitionTo(cmd,
            ImageLayout.ShaderReadOnlyOptimal,
            srcStage: PipelineStageFlags.TransferBit,
            dstStage: PipelineStageFlags.FragmentShaderBit,
            srcAccess: AccessFlags.TransferWriteBit,
            dstAccess: AccessFlags.ShaderReadBit);

        UploadFenceValue = 0;
    }

    /// <summary>
    /// Create an empty atlas texture with no initial pixel data.
    /// Used for dynamic-atlas scenarios such as GlyphAtlasManager.
    /// </summary>
    internal static Texture CreateEmpty(uint width, uint height, string name)
    {
        var tex = new Texture
        {
            Width = width,
            Height = height,
            Format = Silk.NET.Vulkan.Format.R8G8B8A8Unorm,
            Name = name
        };
        tex.CreateImageResource();
        tex.CreateImageView();
        return tex;
    }

    /// <summary>
    /// 1-6 Compute: create a storage texture, the first introduction of StorageBit, for dual use as compute write target and sampled texture with no upload chain.
    /// Equivalent to DXTexture.CreateComputeStorage:
    /// the layout starts at Undefined, transitions to General before the first DispatchCompute,
    /// and transitions to ShaderReadOnlyOptimal after dispatch, where the draw-path EnsureReadyForRendering observes it as a no-op.
    /// Once Ready is set, it can be consumed by Sprite2D and DispatchCompute with no UploadFenceValue wait.
    /// Step D of 2-1: the format is parameterized, for example bloom-chain R16G16B16A16Sfloat, and mapping is closed in Graphics.CreateComputeTexture.
    /// </summary>
    internal static Texture CreateComputeStorage(string name, uint width, uint height,
        Silk.NET.Vulkan.Format format = Silk.NET.Vulkan.Format.R8G8B8A8Unorm)
    {
        var tex = new Texture
        {
            Width = width,
            Height = height,
            Format = format,
            Name = name
        };
        tex.CreateImageResource(ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit);
        tex.CreateImageView();
        // A newly created Vulkan image has undefined contents, while D3D12 committed resources and WebGPU textures guarantee zero initialization.
        // DDGI probe-atlas hysteresis and bounce feedback read the surface written in the previous frame.
        // Dirty memory would feed back into that loop and preserve itself indefinitely,
        // observed in practice as full walls turning green or smeared with random colors.
        // The image therefore must be cleared to zero on creation to match the zero-start semantics of the other backends.
        tex.ClearToZero();
        tex.Ready = true;
        return tex;
    }

    /// <summary>
    /// Clear to zero immediately after creation:
    /// on a one-time transfer command buffer, transition Undefined to TransferDst, then CmdClearColorImage(0), then General.
    /// Leave the image in General after the clear:
    /// the first DispatchCompute write-surface transition, any-to-General, then short-circuits because CurrentLayout already matches,
    /// which is equivalent to the existing first-frame Undefined-to-General path.
    /// Visibility is guaranteed by the host-side QueueWaitIdle, following the same precedent as the upload chain.
    /// </summary>
    internal unsafe void ClearToZero()
    {
        var vk = Device.Vk;
        var d = Device.LogicalDevice;

        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
            CommandPool = Device.TextureUploadBatch.Pool
        };
        CommandBuffer cmd;
        vk.AllocateCommandBuffers(d, in allocInfo, out cmd);

        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit
        };
        vk.BeginCommandBuffer(cmd, in beginInfo);

        // Undefined to TransferDst for a newly created image:
        // old contents may be discarded and no source access is needed.
        TransitionTo(cmd, ImageLayout.TransferDstOptimal,
            PipelineStageFlags.AllCommandsBit, PipelineStageFlags.TransferBit,
            0, AccessFlags.TransferWriteBit);

        var clearColor = new ClearColorValue(0f, 0f, 0f, 0f);
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
        vk.CmdClearColorImage(cmd, Image, ImageLayout.TransferDstOptimal, in clearColor, 1, in range);

        // Leave it in General for the first compute write.
        // Destination access includes both read, for SampleBouncePrev linear sampling, and write.
        TransitionTo(cmd, ImageLayout.General,
            PipelineStageFlags.TransferBit, PipelineStageFlags.ComputeShaderBit,
            AccessFlags.TransferWriteBit, AccessFlags.ShaderWriteBit | AccessFlags.ShaderReadBit);

        vk.EndCommandBuffer(cmd);

        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &cmd
        };
        vk.QueueSubmit(Device.TransferCommandQueue.NativeQueue, 1, in submitInfo, default);
        vk.QueueWaitIdle(Device.TransferCommandQueue.NativeQueue);
        vk.FreeCommandBuffers(d, Device.TextureUploadBatch.Pool, 1, in cmd);
    }

    /// <summary>
    /// 1-8 format intent to Vulkan concrete format, the single source of truth shared by both the 2D and 3D creation paths,
    /// preventing the same intent from mapping to different concrete formats in two places.
    /// The three new 1-8 formats, R16_SFLOAT, R8_UNORM, and R16G16_SFLOAT, are not part of the mandatory Vulkan STORAGE_IMAGE support table.
    /// Availability must therefore be validated by the caller through vkGetPhysicalDeviceFormatProperties,
    /// closed in Graphics.CheckComputeFormatSupport.
    /// This function performs only the mechanical mapping.
    /// </summary>
    internal static Format MapComputeFormat(Season.Rendering.ComputeStorageFormat format) => format switch
    {
        Season.Rendering.ComputeStorageFormat.Rgba16Float => Silk.NET.Vulkan.Format.R16G16B16A16Sfloat,
        Season.Rendering.ComputeStorageFormat.R16Float => Silk.NET.Vulkan.Format.R16Sfloat,
        Season.Rendering.ComputeStorageFormat.R8Unorm => Silk.NET.Vulkan.Format.R8Unorm,
        Season.Rendering.ComputeStorageFormat.Rg16Float => Silk.NET.Vulkan.Format.R16G16Sfloat,
        _ => Silk.NET.Vulkan.Format.R8G8B8A8Unorm,
    };

    /// <summary>
    /// Recreate the native resources of a storage texture, Image, Memory, and View, in place to match a new size.
    /// Keep the same C# object identity so Sprite2D AddRef references and DictionaryVKTexture keys remain unchanged.
    ///
    /// Old resources must use deferred release, following the same pattern as VKRenderTarget.Recreate, and must not be destroyed immediately:
    /// on mainstream implementations, VkImageView and VkImage handles are heap pointers.
    /// Destroying and then creating again often makes the allocator return the same block unchanged,
    /// leaving old and new handle values numerically identical.
    /// Under lavapipe, 16 out of 17 chain textures hit this case.
    /// Any downstream cache that checks liveness by handle comparison would therefore hit silently,
    /// leaving descriptors baked from destroyed views behind.
    /// Vulkan descriptors snapshot the internal state of the view at write time and do not dereference the handle again later.
    /// The GPU would then read freed memory during execution and crash inside vkQueuePresentKHR.
    /// </summary>
    internal void RecreateComputeStorage(uint width, uint height)
    {
        var vk = Device.Vk;
        var d = Device.LogicalDevice;
        Device.CancelTextureTransition(this);

        var oldView = View; View = default;
        var oldImage = Image; Image = default;
        var oldMemory = Memory; Memory = default;
        Device.EnqueueDeferredRelease(() =>
        {
            if (oldView.Handle != 0) vk.DestroyImageView(d, oldView, null);
            if (oldImage.Handle != 0) vk.DestroyImage(d, oldImage, null);
            if (oldMemory.Handle != 0) vk.FreeMemory(d, oldMemory, null);
        });

        Width = width;
        Height = height;
        CurrentLayout = ImageLayout.Undefined;
        // TransferDstBit is required by spec for CmdClearColorImage, matching the creation-path usage set.
        CreateImageResource(ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit);
        CreateImageView();
        // The recreated image also starts with undefined contents.
        // Clear it to zero just like CreateComputeStorage to prevent dirty memory from re-entering the feedback loop.
        ClearToZero();
        Ready = true;
    }

    /// <summary>
    /// Incremental sub-rectangle upload:
    /// copy the specified dirty rectangles of atlas pixels into the GPU texture.
    /// The texture must have been created by CreateEmpty and match sourceWidth and sourceHeight in size.
    /// </summary>
    public void UploadSubRects(byte[] rgbaPixels, int sourceWidth, int sourceHeight, AtlasUploadRect[] dirtyRects)
    {
        if (dirtyRects == null || dirtyRects.Length == 0)
            return;

        int expectedSize = (int)(Width * Height * 4);
        if (Width != (uint)sourceWidth || Height != (uint)sourceHeight)
            throw new ArgumentException(
                $"Atlas size mismatch. Expected {Width}×{Height}, got {sourceWidth}×{sourceHeight}.");
        if (rgbaPixels.Length != expectedSize)
            throw new ArgumentException(
                $"Pixel data size mismatch. Expected {expectedSize} bytes, got {rgbaPixels.Length}.");

        var vk = Device.Vk;
        var d = Device.LogicalDevice;
        var rm = Device.ResourceManager;
        ulong size = (ulong)expectedSize;

        // 1. Create the staging buffer, HOST_VISIBLE | HOST_COHERENT, and copy the full atlas pixel payload.
        var staging = rm.CreateBuffer(size,
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        try
        {
            void* mapped;
            vk.MapMemory(d, staging.Memory, 0, size, 0, &mapped);
            fixed (byte* src = rgbaPixels)
                Unsafe.CopyBlock(mapped, src, (uint)expectedSize);
            vk.UnmapMemory(d, staging.Memory);

            // 2. Allocate a one-time transfer command buffer.
            var allocInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1
            };
            allocInfo.CommandPool = Device.TextureUploadBatch.Pool;
            CommandBuffer cmd;
            vk.AllocateCommandBuffers(d, in allocInfo, out cmd);

            var beginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit
            };
            vk.BeginCommandBuffer(cmd, in beginInfo);

            // 3. Barrier: CurrentLayout to TransferDstOptimal.
            // Note:
            // this command buffer executes on the transfer queue.
            // A dedicated transfer family does not support the FragmentShader stage,
            // so queue-independent stages, AllCommands, must be used.
            // Cross-queue visibility is guaranteed by QueueWaitIdle after submission.
            TransitionTo(cmd, ImageLayout.TransferDstOptimal,
                PipelineStageFlags.AllCommandsBit,
                PipelineStageFlags.TransferBit,
                0, AccessFlags.TransferWriteBit);

            // 4. Generate one VkBufferImageCopy per dirty rectangle and submit them as a batch.
            int fullRowStride = (int)(Width * 4);
            var copies = new BufferImageCopy[dirtyRects.Length];
            for (int i = 0; i < dirtyRects.Length; i++)
            {
                var rect = dirtyRects[i];
                copies[i] = new BufferImageCopy
                {
                    BufferOffset = (ulong)(rect.Y * fullRowStride + rect.X * 4),
                    BufferRowLength = (uint)sourceWidth,
                    BufferImageHeight = (uint)sourceHeight,
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        MipLevel = 0,
                        BaseArrayLayer = 0,
                        LayerCount = 1
                    },
                    ImageOffset = new Offset3D(rect.X, rect.Y, 0),
                    ImageExtent = new Extent3D((uint)rect.Width, (uint)rect.Height, 1)
                };
            }

            fixed (BufferImageCopy* pCopies = copies)
            {
                vk.CmdCopyBufferToImage(cmd, staging.Buffer, Image,
                    ImageLayout.TransferDstOptimal, (uint)copies.Length, pCopies);
            }

            // 5. Barrier: TransferDst to ShaderReadOnly, again using queue-independent stages on the transfer queue.
            TransitionTo(cmd, ImageLayout.ShaderReadOnlyOptimal,
                PipelineStageFlags.TransferBit, PipelineStageFlags.AllCommandsBit,
                AccessFlags.TransferWriteBit, 0);

            vk.EndCommandBuffer(cmd);

            // 6. Submit to the transfer queue and wait.
            var submitInfo = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &cmd
            };
            vk.QueueSubmit(Device.TransferCommandQueue.NativeQueue, 1, in submitInfo, default);
            vk.QueueWaitIdle(Device.TransferCommandQueue.NativeQueue);
            vk.FreeCommandBuffers(d, Device.TextureUploadBatch.Pool, 1, in cmd);

            CurrentLayout = ImageLayout.ShaderReadOnlyOptimal;
            UploadFenceValue = 0;
        }
        finally
        {
            rm.DestroyBuffer(staging);
        }
    }

    /// <summary>
    /// Parameterless constructor used by CreateEmpty to skip the texture-creation logic inside ProcessImageResult.
    /// </summary>
    Texture() { }

    public void Dispose()
    {
        var vk = Device.Vk;
        var d = Device.LogicalDevice;
        Device.CancelTextureTransition(this);

        // Same rule as RecreateComputeStorage:
        // in-flight command buffers may still reference this texture, so destruction must wait until the timeline passes the retire value.
        var oldView = View; View = default;
        var oldImage = Image; Image = default;
        var oldMemory = Memory; Memory = default;
        Device.EnqueueDeferredRelease(() =>
        {
            if (oldView.Handle != 0) vk.DestroyImageView(d, oldView, null);
            if (oldImage.Handle != 0) vk.DestroyImage(d, oldImage, null);
            if (oldMemory.Handle != 0) vk.FreeMemory(d, oldMemory, null);
        });

        Device.DictionaryTexture.Remove(string.IsNullOrEmpty(_cacheKey) ? Name : _cacheKey);
    }
}
