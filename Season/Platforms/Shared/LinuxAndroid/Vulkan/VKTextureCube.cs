// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Vulkan;
using Image = Silk.NET.Vulkan.Image;
using System.Runtime.CompilerServices;

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

/// <summary>
/// 1-7 Cubemap texture (Vulkan side, aligned with D3D12 <c>DXTextureCube</c>):
/// a single-mip image created with <c>ImageCreateFlags.CubeCompatibleBit</c>
/// and <c>ArrayLayers=6</c>,
/// exposed through one <c>ImageViewType.TypeCube</c> view
/// (<c>layerCount=6</c>, so the whole cube owns exactly one <see cref="ViewVersion"/>).
/// It intentionally stays separate from <see cref="Texture"/> instead of extending it:
/// the latter assumes single-layer Type2D across the whole pipeline
/// (view dimension, copy region, UAV, sub-rect updates),
/// and mixing cubemaps in would add branches to every path.
///
/// Lifetime:
/// CreateFromDecoders synchronously completes
/// "create image -> create view -> upload six faces -> publish Ready",
/// so the returned object is immediately usable.
/// It is then registered in this class's static dictionary by <see cref="Name"/>
/// (name-as-handle, matching the 1-6 storage-texture convention and the D3D12 side),
/// and later 2-4 DDGI resolves sky radiance by name through <see cref="Find"/>.
///
/// All layout transitions are completed inside the transfer command buffer used for upload
/// (the barrier subresourceRange always uses <c>baseArrayLayer=0, layerCount=6</c>,
/// so all six layers are transitioned at once), followed by QueueWaitIdle.
/// Therefore the render thread never needs any barriers.
/// Under the Android tiler restriction
/// (no layout transitions inside render passes, see the comment on <see cref="Texture.EnsureReadyForRendering"/>),
/// this is the simplest form:
/// there is no equivalent of the D3D12-side per-draw EnsureReadyForRendering here.
/// The transfer queue must not reference FragmentShader stages/access,
/// so barriers use the queue-agnostic AllCommands stage.
/// Visibility to the graphics queue is guaranteed by QueueWaitIdle plus the implicit dependency
/// of later submissions
/// (same reasoning as <see cref="Texture.UploadPixels"/>);
/// cross-queue-family ownership is avoided through SharingMode.Concurrent.
/// </summary>
internal unsafe sealed class VKTextureCube : IDisposable
{
    /// <summary>Number of cube faces (always 6, face order follows Season.Rendering.CubeFace).</summary>
    public const int FaceCount = 6;

    public string Name = string.Empty;

    /// <summary>Native resources are created, all six faces are uploaded,
    /// and layout is already ShaderReadOnlyOptimal.</summary>
    public bool Ready;

    /// <summary>Edge length of one face (all six faces are equal squares).</summary>
    public uint Size;

    public Image Image;

    public DeviceMemory Memory;

    /// <summary>TypeCube view (covers all 6 layers and is sampled directly as `samplerCube` in shaders).</summary>
    public Silk.NET.Vulkan.ImageView View;

    /// <summary>
    /// Monotonically increasing identity number of the View, with the exact same semantics as
    /// <see cref="Texture.ViewVersion"/>:
    /// downstream descriptor caches must use it to decide whether the underlying view changed,
    /// not View.Handle
    /// (handles are heap pointers, and after destroy/recreate they are very likely to reuse the same address,
    /// which would silently miss invalidation).
    /// </summary>
    public ulong ViewVersion;

    Format _format = Silk.NET.Vulkan.Format.R8G8B8A8Unorm;

    ImageLayout _currentLayout = ImageLayout.Undefined;

    /// <summary>Name-based registry (name-as-handle). All access is protected by this lock.</summary>
    static readonly Dictionary<string, VKTextureCube> _registry = new();

    /// <summary>
    /// Environment radiance cube active for this frame
    /// (resolved once per frame by VKPrimitiveGroup.SetLighting;
    /// null means there is no environment map, so binding 16 uses <see cref="DummyBlack"/>).
    /// </summary>
    internal static VKTextureCube? Active;

    static VKTextureCube? _dummyBlack;

    /// <summary>
    /// 1x1 all-black fallback cube:
    /// binding 16 always requires a valid descriptor
    /// (the shader statically references `envCube`,
    /// and stackalloc-backed writes are not zero-initialized - leaving it empty means stack garbage,
    /// which makes the WSL driver abort on an invalid descriptor).
    /// This cube is bound when there is no environment map.
    /// Unlike on the D3D12 side, Vulkan device memory is not guaranteed to be zero-initialized,
    /// so all six faces must upload explicit black pixels instead of assuming fresh allocations start at zero.
    /// </summary>
    internal static VKTextureCube DummyBlack
    {
        get
        {
            if (_dummyBlack == null)
            {
                var faces = new byte[FaceCount][];
                for (int f = 0; f < FaceCount; f++)
                    faces[f] = new byte[] { 0, 0, 0, 255 };
                _dummyBlack = CreateAndUpload("__EnvCubeDummyBlack", 1, faces, register: false);
            }
            return _dummyBlack;
        }
    }

    /// <summary>Cube that should be bound to binding 16 this frame
    /// (prefer Active, otherwise use the all-black fallback). Never null.</summary>
    internal static VKTextureCube Bound => Active ?? DummyBlack;

    /// <summary>Lookup by name (returns null if not registered).</summary>
    internal static VKTextureCube? Find(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        lock (_registry)
        {
            return _registry.TryGetValue(name, out var cube) ? cube : null;
        }
    }

    /// <summary>
    /// Create and register a cube from six decoded RGBA8 face images
    /// (face order follows the declaration order of Season.Rendering.CubeFace).
    /// If the same name already exists, reuse it directly
    /// (1-7 does not support runtime cubemap swapping; see the simplified EnvironmentMap boundary).
    /// The shared layer already validates that all six faces are equal-size squares;
    /// this method only keeps defensive assertions.
    /// </summary>
    internal static VKTextureCube CreateFromDecoders(string name, int size,
        Season.Rendering.TextureCubeFormat format, INativeImageDecoder[] faces)
    {
        lock (_registry)
        {
            if (_registry.TryGetValue(name, out var existing))
                return existing;
        }

        if (format != Season.Rendering.TextureCubeFormat.Rgba8Unorm)
            throw new NotSupportedException(
                $"[VKTextureCube] '{name}': 1-7 currently supports only Rgba8Unorm (got {format}).");

        if (faces == null || faces.Length != FaceCount)
            throw new ArgumentException($"[VKTextureCube] '{name}': exactly {FaceCount} face textures are required.", nameof(faces));

        // The decoder contract requires tightly packed RGBA8,
        // but end-of-row padding is allowed (Stride > size * 4),
        // so copy row by row into a tightly packed buffer.
        // Note: this path only handles padding and does not expand RGB -> RGBA -
        // channel normalization is the decoder's own responsibility.
        // LinuxImageDecoder once overflowed here because it exposed Gdk.Pixbuf's 3-channel buffer as-is,
        // and that was fixed internally.
        // If another decoder violates the contract again,
        // the explicit checks below report exactly which one failed instead of throwing an unclear out-of-range error.
        int dstStride = size * 4;
        var faceData = new byte[FaceCount][];
        for (int f = 0; f < FaceCount; f++)
        {
            var decoder = faces[f];
            if (decoder == null || decoder.Width != size || decoder.Height != size)
                throw new ArgumentException(
                    $"[VKTextureCube] '{name}': face {(Season.Rendering.CubeFace)f} has mismatched dimensions (expected {size}x{size}).");

            if (decoder.Stride < dstStride)
                throw new ArgumentException(
                    $"[VKTextureCube] '{name}': decoder for face {(Season.Rendering.CubeFace)f} violates " +
                    $"the INativeImageDecoder RGBA8 contract (Stride={decoder.Stride} < {dstStride}, " +
                    $"likely unexpanded RGB three-channel data).");

            var data = new byte[size * dstStride];
            var src = decoder.PixelSpan;
            int srcStride = decoder.Stride;
            for (int y = 0; y < size; y++)
                src.Slice(y * srcStride, dstStride).CopyTo(new Span<byte>(data, y * dstStride, dstStride));
            faceData[f] = data;
        }

        var cube = CreateAndUpload(name, (uint)size, faceData, register: true);
        return cube;
    }

    static VKTextureCube CreateAndUpload(string name, uint size, byte[][] faceData, bool register)
    {
        var cube = new VKTextureCube { Name = name, Size = size };
        cube.CreateImageResource();
        cube.CreateImageView();
        cube.UploadFaces(faceData);
        cube.Ready = true;

        if (register)
        {
            lock (_registry)
            {
                if (_registry.TryGetValue(name, out var raced))
                {
                    cube.Dispose();
                    return raced;
                }
                _registry.Add(name, cube);
            }
        }

        return cube;
    }

    void CreateImageResource()
    {
        // Use Concurrent when graphics and transfer belong to different families
        // to avoid ownership barriers (same as Texture)
        bool concurrent = Device.GraphicsQueueFamily != Device.TransferQueueFamily;
        var families = stackalloc uint[2] { Device.GraphicsQueueFamily, Device.TransferQueueFamily };

        var info = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            // CubeCompatibleBit is a prerequisite for ImageViewType.TypeCube
            // (without it, vkCreateImageView fails immediately)
            Flags = ImageCreateFlags.CreateCubeCompatibleBit,
            ImageType = ImageType.Type2D,
            Format = _format,
            Extent = new Extent3D(Size, Size, 1),
            MipLevels = 1,
            ArrayLayers = FaceCount,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit,
            SharingMode = concurrent ? SharingMode.Concurrent : SharingMode.Exclusive,
            QueueFamilyIndexCount = concurrent ? 2u : 0u,
            PQueueFamilyIndices = concurrent ? families : null,
            InitialLayout = ImageLayout.Undefined
        };

        if (Device.Vk.CreateImage(Device.LogicalDevice, in info, null, out Image) != Result.Success)
            throw new Exception($"vkCreateImage (cube '{Name}') failed");

        Device.Vk.GetImageMemoryRequirements(Device.LogicalDevice, Image, out var memReq);

        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = Device.ResourceManager.FindMemoryType(
                memReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit)
        };

        if (Device.Vk.AllocateMemory(Device.LogicalDevice, in allocInfo, null, out Memory) != Result.Success)
            throw new Exception($"vkAllocateMemory (cube '{Name}') failed");

        Device.Vk.BindImageMemory(Device.LogicalDevice, Image, Memory, 0);
        _currentLayout = ImageLayout.Undefined;
    }

    void CreateImageView()
    {
        var info = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = Image,
            ViewType = ImageViewType.TypeCube,
            Format = _format,
            Components = new ComponentMapping(
                ComponentSwizzle.Identity, ComponentSwizzle.Identity,
                ComponentSwizzle.Identity, ComponentSwizzle.Identity),
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                // One view covers all 6 layers:
                // to shaders, the six faces form a single samplerCube,
                // so no per-face views are created and the whole cube has only one ViewVersion
                // for downstream caches to compare
                LayerCount = FaceCount
            }
        };

        if (Device.Vk.CreateImageView(Device.LogicalDevice, in info, null, out View) != Result.Success)
            throw new Exception($"vkCreateImageView (cube '{Name}') failed");

        ViewVersion = Device.NextViewVersion();
    }

    /// <summary>
    /// Upload all six faces in one shot:
    /// one staging buffer (all faces tightly packed) + six BufferImageCopy regions
    /// (with baseArrayLayer=f per face), and both pre/post barriers use layerCount=6.
    /// The upload runs on the transfer queue and finishes with QueueWaitIdle,
    /// so the texture is already ShaderReadOnlyOptimal when this method returns.
    /// </summary>
    void UploadFaces(byte[][] faceData)
    {
        var vk = Device.Vk;
        var d = Device.LogicalDevice;
        var rm = Device.ResourceManager;

        uint faceBytes = Size * Size * 4;
        ulong totalBytes = (ulong)faceBytes * FaceCount;

        var staging = rm.CreateBuffer(totalBytes,
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        try
        {
            void* mapped;
            if (vk.MapMemory(d, staging.Memory, 0, totalBytes, 0, &mapped) != Result.Success)
                throw new Exception($"vkMapMemory (cube '{Name}' staging) failed");

            var basePtr = (byte*)mapped;
            for (int f = 0; f < FaceCount; f++)
            {
                var data = faceData[f];
                if (data == null || data.Length != faceBytes)
                    throw new ArgumentException(
                        $"[VKTextureCube] '{Name}': face {(Season.Rendering.CubeFace)f} has invalid pixel byte count " +
                        $"(expected {faceBytes}, got {data?.Length ?? 0}).");
                fixed (byte* src = data)
                    Unsafe.CopyBlock(basePtr + (nuint)((ulong)f * faceBytes), src, faceBytes);
            }
            vk.UnmapMemory(d, staging.Memory);

            var allocInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = Device.TextureUploadBatch.Pool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1
            };
            if (vk.AllocateCommandBuffers(d, in allocInfo, out var cmd) != Result.Success)
                throw new Exception($"vkAllocateCommandBuffers (cube '{Name}') failed");

            var beginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit
            };
            vk.BeginCommandBuffer(cmd, in beginInfo);

            // Undefined -> TransferDstOptimal (all 6 layers)
            TransitionAllLayers(cmd, ImageLayout.TransferDstOptimal,
                PipelineStageFlags.AllCommandsBit, PipelineStageFlags.TransferBit,
                0, AccessFlags.TransferWriteBit);

            var regions = stackalloc BufferImageCopy[FaceCount];
            for (int f = 0; f < FaceCount; f++)
            {
                regions[f] = new BufferImageCopy
                {
                    BufferOffset = (ulong)f * faceBytes,
                    BufferRowLength = 0,        // tightly packed
                    BufferImageHeight = 0,
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        MipLevel = 0,
                        BaseArrayLayer = (uint)f,   // Face index is the array layer (single-mip cube)
                        LayerCount = 1
                    },
                    ImageOffset = new Offset3D(0, 0, 0),
                    ImageExtent = new Extent3D(Size, Size, 1)
                };
            }
            vk.CmdCopyBufferToImage(cmd, staging.Buffer, Image,
                ImageLayout.TransferDstOptimal, FaceCount, regions);

            // TransferDstOptimal -> ShaderReadOnlyOptimal (all 6 layers).
            // The transfer queue must not reference FragmentShader stage/access,
            // so dstStage uses the queue-agnostic AllCommands
            // (same as Texture.UploadPixels)
            TransitionAllLayers(cmd, ImageLayout.ShaderReadOnlyOptimal,
                PipelineStageFlags.TransferBit, PipelineStageFlags.AllCommandsBit,
                AccessFlags.TransferWriteBit, 0);

            vk.EndCommandBuffer(cmd);

            var submitInfo = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &cmd
            };
            if (vk.QueueSubmit(Device.TransferCommandQueue.NativeQueue, 1, in submitInfo, default) != Result.Success)
                throw new Exception($"vkQueueSubmit (cube '{Name}' upload) failed");
            vk.QueueWaitIdle(Device.TransferCommandQueue.NativeQueue);
            vk.FreeCommandBuffers(d, Device.TextureUploadBatch.Pool, 1, in cmd);
        }
        finally
        {
            rm.DestroyBuffer(staging);
        }
    }

    /// <summary>Layout transition for the entire cube
    /// (all 6 layers, single mip).
    /// The six faces always advance together, with no per-layer tracking.</summary>
    void TransitionAllLayers(CommandBuffer cmd, ImageLayout newLayout,
        PipelineStageFlags srcStage, PipelineStageFlags dstStage,
        AccessFlags srcAccess, AccessFlags dstAccess)
    {
        if (_currentLayout == newLayout) return;

        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = _currentLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = Image,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = FaceCount
            },
            SrcAccessMask = srcAccess,
            DstAccessMask = dstAccess
        };

        Device.Vk.CmdPipelineBarrier(cmd, srcStage, dstStage, 0, 0, null, 0, null, 1, in barrier);
        _currentLayout = newLayout;
    }

    /// <summary>
    /// Refresh binding 16 of <paramref name="set"/> to the current <see cref="Bound"/> view.
    /// This updates only when ViewVersion changes
    /// (compare version rather than handle; see <see cref="ViewVersion"/>).
    ///
    /// Only this single binding is updated and the other 16 descriptors remain untouched.
    /// Environment maps load asynchronously, so they inevitably become ready after early models
    /// have already had their descriptor sets written.
    /// A full rewrite would require reassembling all buffer handles again,
    /// which adds unnecessary cost and risk.
    ///
    /// <paramref name="cachedVersion"/> must be cached per frame slot:
    /// only the set for the current FrameIndex is updated,
    /// and the remaining slots are refreshed when their own frame arrives.
    /// The previous submission for this slot is already guaranteed complete
    /// by the same-slot fence waited at the end of AfterRender,
    /// so vkUpdateDescriptorSets is safe against in-flight command buffers here.
    /// </summary>
    internal static void RefreshBinding(DescriptorSet set, ref ulong cachedVersion)
    {
        var cube = Bound;
        if (cube.ViewVersion == cachedVersion)
            return;

        var info = new DescriptorImageInfo
        { ImageView = cube.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = EnvCubeBinding,
            DstArrayElement = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = &info
        };
        Device.Vk.UpdateDescriptorSets(Device.LogicalDevice, 1, &write, 0, null);
        cachedVersion = cube.ViewVersion;
    }

    /// <summary>1-7: descriptor binding index of the environment radiance cube
    /// (GLSL <c>samplerCube envCube</c>).</summary>
    internal const uint EnvCubeBinding = 16;

    public void Dispose()
    {
        lock (_registry)
        {
            if (!string.IsNullOrEmpty(Name) && _registry.TryGetValue(Name, out var registered) && registered == this)
                _registry.Remove(Name);
        }

        if (Active == this)
            Active = null;

        // In-flight command buffers may still hold descriptors pointing at this view,
        // so destruction must wait until the timeline passes the retire value
        // (same as Texture.Dispose / VKRenderTarget.Recreate).
        // Destroying the image releases all 6 layers at once, so no per-layer handling is needed.
        // Downstream caches that compare ViewVersion invalidate correctly
        // because Bound switches to DummyBlack.
        var vk = Device.Vk;
        var d = Device.LogicalDevice;
        var oldView = View; View = default;
        var oldImage = Image; Image = default;
        var oldMemory = Memory; Memory = default;
        Device.EnqueueDeferredRelease(() =>
        {
            if (oldView.Handle != 0) vk.DestroyImageView(d, oldView, null);
            if (oldImage.Handle != 0) vk.DestroyImage(d, oldImage, null);
            if (oldMemory.Handle != 0) vk.FreeMemory(d, oldMemory, null);
        });

        Ready = false;
    }
}
