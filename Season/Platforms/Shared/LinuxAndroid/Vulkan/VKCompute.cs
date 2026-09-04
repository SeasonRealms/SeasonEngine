// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Vulkan;
using Silk.NET.Core.Native;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

/// <summary>
/// 1-6 Compute kernel handle (Vulkan side): generated once from ComputeKernelDesc,
/// with a dedicated descriptor set layout + pipeline layout + compute pipeline
/// (kernel registration model: each kernel carries its own layout).
///
/// Binding layout (mechanically generated from Bindings, aligned with the GLSL slot
/// conventions described in the ComputeBindingType summary):
/// - Params (if present, must be Bindings[0]) -> push_constant (ComputeBit range, <= 128B;
///   the strictest Vulkan minimum limit is treated as the contract maximum),
///   so binding slot 0 is left empty and omitted from the set layout;
/// - Every remaining binding -> binding = declared index i:
///   SampledTexture = CombinedImageSampler (immutable linear-clamp sampler, reusing
///   Pipeline.StaticSampler, exposed as sampler2D in GLSL),
///   DepthTexture = CombinedImageSampler (immutable nearest sampler Pipeline.StaticPointSampler,
///   exposed as sampler2D + texelFetch in GLSL; per contract 2-2 clause 3, D32 linear filtering
///   is an optional Vulkan feature),
///   StorageTextureWrite=StorageImage（rgba8 writeonly image2D）、StorageBuffer*=StorageBuffer。
///
/// 1-8: SampledTexture3D = CombinedImageSampler (same immutable StaticSampler; its AddressModeW
/// is also ClampToEdge + Linear, so GLSL sampler3D naturally gets trilinear filtering with clamped
/// end faces), StorageTexture3DWrite = StorageImage (writeonly image3D).
/// The descriptor type is the same as the 2D case; dimensionality only differs in the ImageView
/// written during binding.
///
/// Descriptor sets are N-buffered by in-flight frame, and each frame slot grows as a ring based on
/// the number of dispatches within that frame (AcquireSet):
/// vkUpdateDescriptorSets takes effect immediately, so if multiple dispatches in the same frame
/// reuse a single set, earlier recorded dispatches would read the last overwritten bindings
/// (this is triggered by a bloom chain firing kernel 5 multiple times).
/// The ring fence at the end of AfterRender guarantees the entire ring for the current FrameIndex
/// slot has retired, so it is safe to overwrite within the current frame
/// (aligned with the per-frame pattern used by the glyph buffer).
///
/// Compilation goes through ShaderCompiler (glslang GLSL 450 -> SPIR-V, entry point always `main`);
/// compile/create failures are caught in Graphics.CreateComputeKernel and return null
/// (graceful degradation during registration).
/// </summary>
internal sealed unsafe class VKComputeKernel : Season.Rendering.ComputeKernel
{
    internal DescriptorSetLayout SetLayout;

    internal PipelineLayout PipelineLayout;

    internal VkPipeline PipelineState;

    /// <summary>Descriptor set ring for in-flight frames x multiple dispatches in the same frame
    /// (outer index = Device.FrameIndex).
    /// The ring grows lazily to the maximum dispatch count for this kernel within a frame,
    /// and the cursor resets when the frame stamp changes (see AcquireSet).</summary>
    internal readonly List<DescriptorSet>[] SetRings;

    readonly int[] _setCursors;

    readonly ulong[] _setStamps;

    /// <summary>Byte size of the Params block (0 when there is no Params binding).</summary>
    internal readonly uint ParamsSize;

    /// <summary>Debug label (prebaked as UTF-8 + NUL, aligned with the Device._passLabels pattern).</summary>
    internal readonly byte[] LabelZ;

    /// <summary>Resolved resource slots reused during the post-dispatch stage
    /// (aligned with Bindings, zero-allocation).</summary>
    internal readonly object?[] ResolvedScratch;

    internal VKComputeKernel(Season.Rendering.ComputeKernelDesc desc)
    {
        Desc = desc;
        LabelZ = System.Text.Encoding.UTF8.GetBytes($"Compute {desc.Name}\0");

        var vk = Device.Vk;
        var device = Device.LogicalDevice;
        var bindings = desc.Bindings;
        ResolvedScratch = new object?[bindings.Length];

        // Descriptor set layout: binding = declared index i (Params slot left empty)
        var sampler = Pipeline.StaticSampler;
        var pointSampler = Pipeline.StaticPointSampler;
        var layoutBindings = stackalloc DescriptorSetLayoutBinding[bindings.Length == 0 ? 1 : bindings.Length];
        uint bindingCount = 0;

        for (int i = 0; i < bindings.Length; i++)
        {
            if (bindings[i].Type == Season.Rendering.ComputeBindingType.Params)
            {
                ParamsSize = bindings[i].SizeInBytes;
                continue;
            }

            bool sampled = bindings[i].Type is Season.Rendering.ComputeBindingType.SampledTexture
                or Season.Rendering.ComputeBindingType.SampledTexture3D;
            bool depth = bindings[i].Type == Season.Rendering.ComputeBindingType.DepthTexture;
            layoutBindings[bindingCount++] = new DescriptorSetLayoutBinding
            {
                Binding = (uint)i,
                DescriptorType = bindings[i].Type switch
                {
                    Season.Rendering.ComputeBindingType.SampledTexture => DescriptorType.CombinedImageSampler,
                    Season.Rendering.ComputeBindingType.DepthTexture => DescriptorType.CombinedImageSampler,
                    Season.Rendering.ComputeBindingType.StorageTextureWrite => DescriptorType.StorageImage,
                    // 1-8: 3D descriptor types are identical to their 2D counterparts;
                    // the only difference is the dimension of the image view.
                    // VkDescriptorSetLayoutBinding carries no dimension information,
                    // so no new enum is needed here.
                    Season.Rendering.ComputeBindingType.SampledTexture3D => DescriptorType.CombinedImageSampler,
                    Season.Rendering.ComputeBindingType.StorageTexture3DWrite => DescriptorType.StorageImage,
                    _ => DescriptorType.StorageBuffer,
                },
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.ComputeBit,
                PImmutableSamplers = sampled ? &sampler : depth ? &pointSampler : null,
            };
        }

        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = bindingCount,
            PBindings = layoutBindings,
        };
        if (vk.CreateDescriptorSetLayout(device, in layoutInfo, null, out SetLayout) != Result.Success)
            throw new Exception($"vkCreateDescriptorSetLayout (compute '{desc.Name}') failed");

        // Pipeline layout: set 0 + optional push constant range
        var setLayout = SetLayout;
        var pushRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.ComputeBit,
            Offset = 0,
            Size = ParamsSize,
        };
        var pipelineLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &setLayout,
            PushConstantRangeCount = ParamsSize > 0 ? 1u : 0u,
            PPushConstantRanges = ParamsSize > 0 ? &pushRange : null,
        };
        if (vk.CreatePipelineLayout(device, in pipelineLayoutInfo, null, out PipelineLayout) != Result.Success)
            throw new Exception($"vkCreatePipelineLayout (compute '{desc.Name}') failed");

        // CS compilation (glslang -> SPIR-V) + compute pipeline
        bool debug =
#if DEBUG
            true;
#else
            false;
#endif
        var csModule = ShaderCompiler.CreateShaderModule(
            vk, device, desc.Source.Glsl!, ShaderStageFlags.ComputeBit, "main", $"{desc.Name}.comp", debug);
        var entryPtr = SilkMarshal.StringToPtr("main", NativeStringEncoding.UTF8);
        try
        {
            var info = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = csModule,
                    PName = (byte*)entryPtr,
                },
                Layout = PipelineLayout,
            };
            if (vk.CreateComputePipelines(device, default, 1, in info, null, out PipelineState) != Result.Success)
                throw new Exception($"vkCreateComputePipelines ('{desc.Name}') failed");
        }
        finally
        {
            SilkMarshal.Free(entryPtr);
            vk.DestroyShaderModule(device, csModule, null);
        }

        // In-flight frame descriptor set ring (allocated from DescriptorAllocator's single large pool;
        // the first set is created up front)
        SetRings = new List<DescriptorSet>[(int)Device.frameCount];
        _setCursors = new int[SetRings.Length];
        _setStamps = new ulong[SetRings.Length];
        for (int f = 0; f < SetRings.Length; f++)
            SetRings[f] = new List<DescriptorSet>(1) { Device.DescriptorAllocator.AllocateSet(SetLayout) };
    }

    /// <summary>Acquire the dedicated descriptor set for this dispatch:
    /// when the frame stamp (retire fence value, constant during recording and monotonic per frame)
    /// changes, reset the cursor to zero; subsequent dispatches in the same frame advance one by one,
    /// and the ring grows lazily if the cursor exceeds its current capacity.</summary>
    internal DescriptorSet AcquireSet()
    {
        int f = (int)Device.FrameIndex;
        ulong stamp = Device.GetCurrentRetireFenceValue();
        if (_setStamps[f] != stamp)
        {
            _setStamps[f] = stamp;
            _setCursors[f] = 0;
        }
        var ring = SetRings[f];
        if (_setCursors[f] == ring.Count)
            ring.Add(Device.DescriptorAllocator.AllocateSet(SetLayout));
        return ring[_setCursors[f]++];
    }

    public override void Dispose()
    {
        // Disposal path: in-flight command buffers may still reference the pipeline/set,
        // so release is deferred behind the timeline gate.
        // Android tilers must not destroy in-flight resources immediately;
        // see the Device.EnqueueDeferredRelease contract.
        var vk = Device.Vk;
        var device = Device.LogicalDevice;
        var pso = PipelineState;
        var pipelineLayout = PipelineLayout;
        var setLayout = SetLayout;
        var sets = SetRings;
        Device.EnqueueDeferredRelease(() =>
        {
            foreach (var ring in sets)
                foreach (var s in ring)
                    Device.DescriptorAllocator.FreeSet(s);
            if (pso.Handle != 0) vk.DestroyPipeline(device, pso, null);
            if (pipelineLayout.Handle != 0) vk.DestroyPipelineLayout(device, pipelineLayout, null);
            if (setLayout.Handle != 0) vk.DestroyDescriptorSetLayout(device, setLayout, null);
        });
        PipelineState = default;
        PipelineLayout = default;
        SetLayout = default;
    }
}

/// <summary>
/// 1-6 Compute storage buffer (Vulkan side): a DEVICE_LOCAL storage buffer
/// (GPU-resident, with no CPU mapping).
/// Vulkan buffers have no layout concept, so descriptor writes directly reference BufferResource.
/// Synchronization is closed in DispatchCompute: a buffer memory barrier is added after dispatch
/// for ReadWrite bindings (for same-frame kernel chain dependencies).
/// 1-8: TransferDstBit is added to support Graphics.UpdateStorageBuffer staging -> device-local copies
/// (constant-block path; the buffer remains DEVICE_LOCAL and unmapped on the CPU side,
/// and all writes go through vkCmdCopyBuffer).
/// </summary>
internal sealed unsafe class VKStorageBuffer : Season.Rendering.StorageBuffer
{
    internal BufferResource Buffer;

    /// <summary>2-4 Step 0: staging ring used for CPU -> GPU uploads
    /// (one slot per in-flight frame, created on first upload and kept resident).
    /// Previously, each UpdateStorageBuffer call created a new staging buffer and queued deferred
    /// release (with extra closure allocation), which is harmless for infrequent use,
    /// but DDGI parameter blocks are written every frame, which would mean
    /// vkCreateBuffer/vkAllocateMemory every frame (violating zero per-frame allocation).
    /// Slotting also avoids write/read races between reusing a single staging buffer and in-flight
    /// frame vkCmdCopyBuffer operations.
    /// Slot safety is guaranteed by the ring fence at the frame-loop entry
    /// (matching the engine's other per-frame buffers).
    /// Capacity always matches the target buffer, so no rebuild is needed after creation.</summary>
    BufferResource[]? _staging;

    internal VKStorageBuffer(uint sizeInBytes)
    {
        // Round capacity up to 16 bytes
        // to match the D3D12 raw-view alignment granularity and keep cross-backend behavior consistent
        uint alignedSize = (sizeInBytes + 15u) & ~15u;
        SizeInBytes = sizeInBytes;
        Buffer = Device.ResourceManager.CreateBuffer(
            alignedSize,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.DeviceLocalBit);
    }

    /// <summary>Get the staging buffer for the current frame slot (created on demand).
    /// If creation fails, return default (Buffer.Handle == 0) so the caller can degrade gracefully.</summary>
    internal BufferResource TryGetStagingForCurrentFrame()
    {
        _staging ??= new BufferResource[Device.frameCount];
        int slot = (int)(Device.FrameIndex % (uint)_staging.Length);
        if (_staging[slot].Buffer.Handle == 0)
        {
            try
            {
                _staging[slot] = Device.ResourceManager.CreateBuffer(
                    Buffer.Size,
                    BufferUsageFlags.TransferSrcBit,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
            }
            catch (Exception ex)
            {
                DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} [VKStorageBuffer] Failed to create staging buffer: {ex.Message}");
                return default;
            }
        }
        return _staging[slot];
    }

    public override void Dispose()
    {
        var buf = Buffer;
        Device.EnqueueDeferredRelease(() => Device.ResourceManager.DestroyBuffer(buf));
        Buffer = default;

        if (_staging != null)
        {
            for (int i = 0; i < _staging.Length; i++)
            {
                if (_staging[i].Buffer.Handle == 0)
                    continue;
                var staging = _staging[i];
                Device.EnqueueDeferredRelease(() => Device.ResourceManager.DestroyBuffer(staging));
                _staging[i] = default;
            }
            _staging = null;
        }
    }
}
