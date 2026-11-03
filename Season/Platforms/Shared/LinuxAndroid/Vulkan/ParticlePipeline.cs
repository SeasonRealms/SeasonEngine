// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Vulkan;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

/// <summary>
/// A0.1's Vulkan submit side: one vkCmdDrawIndexedIndirect draws however many billboards the compute pass decided.
///
/// It differs from DXParticleDrawPipeline in only two ways, both inherent to Vulkan:
/// first, descriptors are required, so sets are managed through a per-frame descriptor ring like VKFogCompositePipeline, whereas D3D12 consumes
/// the GPU virtual address directly through a root SRV and needs no heap at all; second, Prepare records not a single barrier, because the tail of TryDispatchCompute already
/// emits ShaderWrite -> ShaderRead for every ReadWrite binding (dstStage includes VertexShaderBit), and the IndirectCommandRead of the argument buffer
/// is patched in separately by IGraphics.TryPrepareIndirectArgs. Prepare therefore only parses and validates.
///
/// The PSO is baked on demand in Draw rather than at construction time, for the same reason as D3D12: the facts that determine the attachment shape only
/// hold after entering the Pass, while Prepare must run outside it. Creating a PSO is a device call, not command recording, so it is legal inside a Pass.
///
/// On the Vulkan side, "attachment shape" has one more dimension than in D3D12. D3D12 only needs to distinguish whether a velocity slot is present, whereas here the Scene Pass
/// has three real shapes under Device.BeginPass: color + velocity + depth (three targets), color + depth (two targets or an RT with its own RP),
/// and color only (the RT declares NoImplicitDepth, so its own RP is colorOnly). Enabling depth testing in the last case is illegal,
/// so the shape must be inferred from the actual bindings rather than assumed. The sample count does not go into the key: all three RPs above hard-code Count1Bit,
/// so any non-single-sampled target is rejected outright instead of baking a PSO that is guaranteed not to match.
/// </summary>
internal sealed unsafe class VKParticleDrawPipeline : Season.Rendering.ParticleDrawPipeline
{
    /// <summary>Descriptor slot order, mapping one-to-one to binding 0/1 of ParticleSystemEffect.DrawVertexGlsl.</summary>
    const uint InstancesBinding = 0;
    const uint FrameBinding = 1;

    /// <summary>The unit-quad indices only range over 0..3, so ResourceManager.CreateIndexBuffer stores them as 16-bit.</summary>
    const ulong QuadIndexBytes = Season.Rendering.ParticleDraw.QuadIndexCount * sizeof(ushort);

    /// <summary>The real attachment shape of the current Pass, which determines the PSO's attachment count and depth state.</summary>
    enum Attachments { ColorOnly, ColorDepth, ColorVelocityDepth, ColorVelocityNormalDepth }

    /// <summary>PSO signature. The sample count is not part of it; see the class comment.</summary>
    readonly record struct PipelineKey(Format Color, Attachments Shape);

    readonly Dictionary<PipelineKey, (VkPipeline Pipeline, RenderPass Anchor)> _pipelines = [];
    readonly List<DescriptorSet>[] _sets = new List<DescriptorSet>[(int)Device.frameCount];
    readonly int[] _cursors = new int[(int)Device.frameCount];
    readonly ulong[] _stamps = new ulong[(int)Device.frameCount];
    DescriptorSetLayout _setLayout;
    PipelineLayout _layout;

    // Produced by Prepare and consumed by Draw exactly once.
    DescriptorSet _preparedSet;
    VKRenderTarget? _color;
    Season.Rendering.RenderTarget? _depth;
    VKStorageBuffer? _instances;
    VKStorageBuffer? _args;
    VKStorageBuffer? _frame;
    ulong _colorViewVersion;
    CommandBuffer _command;
    ulong _stamp;
    uint _frameIndex;
    bool _prepared;
    bool _disposed;

    internal VKParticleDrawPipeline()
    {
        for (int i = 0; i < _sets.Length; i++) _sets[i] = new();
        try { Initialize(); }
        catch { Dispose(); throw; }
    }

    void Initialize()
    {
        var vk = Device.Vk; var device = Device.LogicalDevice;
        // Two read-only storage buffers, visible only to the vertex stage; there is no push constant, so binding 0 is free,
        // and the compute-side binding set starts at 1 because slot 0 is semantically occupied by Params' push constant.
        var bindings = stackalloc DescriptorSetLayoutBinding[2];
        bindings[0] = new() { Binding = InstancesBinding, DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageBuffer, StageFlags = ShaderStageFlags.VertexBit };
        bindings[1] = new() { Binding = FrameBinding, DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageBuffer, StageFlags = ShaderStageFlags.VertexBit };
        var setInfo = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 2, PBindings = bindings };
        if (vk.CreateDescriptorSetLayout(device, in setInfo, null, out _setLayout) != Result.Success)
            throw new InvalidOperationException("Failed to create particle descriptor layout.");
        var setLayout = _setLayout;
        var layoutInfo = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1, PSetLayouts = &setLayout };
        if (vk.CreatePipelineLayout(device, in layoutInfo, null, out _layout) != Result.Success)
            throw new InvalidOperationException("Failed to create particle pipeline layout.");
    }

    VkPipeline GetOrCreatePipeline(in PipelineKey key)
    {
        if (_pipelines.TryGetValue(key, out var cached)) return cached.Pipeline;
        var vk = Device.Vk; var device = Device.LogicalDevice;
        bool normal = key.Shape == Attachments.ColorVelocityNormalDepth;
        bool velocity = normal || key.Shape == Attachments.ColorVelocityDepth;
        bool depth = key.Shape != Attachments.ColorOnly;

        // A PSO only needs to be "compatible" with the actual render pass, i.e. the attachment formats, sample counts, and subpass structure must match;
        // LoadOp/StoreOp/layout do not participate in the check. So an isomorphic anchor RP is synthesized here for baking, the same technique used by VKFogCompositePipeline
        // and the HDR anchor in Pipeline.Init, without borrowing the handle the Pass is currently using.
        RenderPass anchor = velocity
            ? VKRenderTarget.CreateVelocityRenderPassForFormat(key.Color, withNormal: normal)
            : VKRenderTarget.CreateColorRenderPassForFormat(key.Color, colorOnly: !depth);
        ShaderModule vertex = default, fragment = default;
        VkPipeline pipeline = default;
        try
        {
            vertex = ShaderCompiler.CreateShaderModule(vk, device,
                Season.Rendering.Effects.ParticleSystemEffect.DrawVertexGlsl,
                ShaderStageFlags.VertexBit, "main", "particle.vert", false);
            fragment = ShaderCompiler.CreateShaderModule(vk, device, velocity
                    ? Season.Rendering.Effects.ParticleSystemEffect.DrawFragmentVelocityGlsl
                    : Season.Rendering.Effects.ParticleSystemEffect.DrawFragmentGlsl,
                ShaderStageFlags.FragmentBit, "main", "particle.frag", false);
            ReadOnlySpan<byte> entry = "main\0"u8;
            fixed (byte* name = entry)
            {
                var stages = stackalloc PipelineShaderStageCreateInfo[2];
                stages[0] = new() { SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.VertexBit, Module = vertex, PName = name };
                stages[1] = new() { SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.FragmentBit, Module = fragment, PName = name };
                // No vertex buffer and no input layout: corners come from gl_VertexIndex and instance values from a storage buffer.
                var input = new PipelineVertexInputStateCreateInfo { SType = StructureType.PipelineVertexInputStateCreateInfo };
                var assembly = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                    Topology = PrimitiveTopology.TriangleList };
                var viewportState = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo,
                    ViewportCount = 1, ScissorCount = 1 };
                var raster = new PipelineRasterizationStateCreateInfo { SType = StructureType.PipelineRasterizationStateCreateInfo,
                    PolygonMode = PolygonMode.Fill,
                    // The billboard's winding order depends on the combination of the camera basis and index order, so disabling culling is more reliable than flipping signs in the shader.
                    CullMode = CullModeFlags.None, FrontFace = FrontFace.Clockwise, LineWidth = 1 };
                var samples = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo,
                    RasterizationSamples = SampleCountFlags.Count1Bit };
                // Depth test on, depth write off: particles are not sorted against one another, and writing depth would only let later draws be occluded by earlier ones.
                // Scene depth is forward (the main Pipeline uses Less/LessOrEqual), so LessOrEqual is used here.
                var depthState = new PipelineDepthStencilStateCreateInfo { SType = StructureType.PipelineDepthStencilStateCreateInfo,
                    DepthTestEnable = true, DepthWriteEnable = false, DepthCompareOp = CompareOp.LessOrEqual };
                var all = ColorComponentFlags.RBit | ColorComponentFlags.GBit
                    | ColorComponentFlags.BBit | ColorComponentFlags.ABit;
                var attachmentBlends = stackalloc PipelineColorBlendAttachmentState[3];
                // The fragment shader outputs premultiplied alpha, so the source factor is One rather than SrcAlpha.
                attachmentBlends[0] = new() { BlendEnable = true,
                    SrcColorBlendFactor = BlendFactor.One, DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
                    ColorBlendOp = BlendOp.Add,
                    SrcAlphaBlendFactor = BlendFactor.One, DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
                    AlphaBlendOp = BlendOp.Add, ColorWriteMask = all };
                // The velocity slot's write mask is zeroed, consistent with the main pipeline's existing choice for transparent geometry: keeping the velocity of the opaque layer below
                // is better than writing 0, which would make TAA accumulate moving particles as if they were a static background.
                attachmentBlends[1] = new() { BlendEnable = false, ColorWriteMask = 0 };
                // Same for the normal slot: particles are not a surface that SSR/decals should see, so keep the normals of the opaque layer below.
                // A slot with a zero write mask does not require a matching fragment shader output, so the shader reuses the velocity variant.
                attachmentBlends[2] = attachmentBlends[1];
                var blend = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo,
                    AttachmentCount = normal ? 3u : (velocity ? 2u : 1u), PAttachments = attachmentBlends };
                var states = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
                var dynamic = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo,
                    DynamicStateCount = 2, PDynamicStates = states };
                var info = new GraphicsPipelineCreateInfo { SType = StructureType.GraphicsPipelineCreateInfo,
                    StageCount = 2, PStages = stages, PVertexInputState = &input, PInputAssemblyState = &assembly,
                    PViewportState = &viewportState, PRasterizationState = &raster, PMultisampleState = &samples,
                    PDepthStencilState = depth ? &depthState : null,
                    PColorBlendState = &blend, PDynamicState = &dynamic, Layout = _layout, RenderPass = anchor };
                if (vk.CreateGraphicsPipelines(device, default, 1, in info, null, out pipeline) != Result.Success)
                    throw new InvalidOperationException("Failed to create particle graphics pipeline.");
            }
        }
        catch
        {
            if (anchor.Handle != 0) vk.DestroyRenderPass(device, anchor, null);
            throw;
        }
        finally
        {
            if (vertex.Handle != 0) vk.DestroyShaderModule(device, vertex, null);
            if (fragment.Handle != 0) vk.DestroyShaderModule(device, fragment, null);
        }
        _pipelines[key] = (pipeline, anchor);
        return pipeline;
    }

    public override bool Prepare(in Season.Rendering.ParticleDrawInputs inputs)
    {
        _prepared = false;
        _color = null; _depth = null; _instances = null; _args = null; _frame = null; _preparedSet = default;
        // Recording anything while InRenderPass is true violates the PassDesc contract, so it is better to skip this frame's submission.
        if (_disposed || Device.InRenderPass || Device.GraphicsCommandBuffer.Handle == 0
            || Pipeline.UnitQuadIndexBuffer.Buffer.Handle == 0
            || Pipeline.UnitQuadIndexBuffer.Size < QuadIndexBytes
            || inputs.Color is not VKRenderTarget color || !color.HasColor
            || color.ColorView.Handle == 0 || color.Desc.SampleCount != 1
            || inputs.Instances is not VKStorageBuffer instances || instances.Buffer.Buffer.Handle == 0
            || instances.SizeInBytes < Season.Rendering.ParticleInstance.SizeInBytes
            || inputs.IndirectArgs is not VKStorageBuffer args || args.Buffer.Buffer.Handle == 0
            || args.Usage != Season.Rendering.StorageBufferUsage.ComputeAndIndirectArgs
            || args.SizeInBytes < Season.Rendering.IndirectDrawIndexedArgs.SizeInBytes
            || inputs.Frame is not VKStorageBuffer frame || frame.Buffer.Buffer.Handle == 0
            || frame.SizeInBytes < Season.Rendering.ParticleDraw.FrameBytes) return false;

        int index = (int)Device.FrameIndex;
        ulong stamp = Device.GetCurrentRetireFenceValue();
        if (_stamps[index] != stamp)
        {
            // The first Prepare of a frame reuses ring[0], i.e. the set used by the same slot three frames ago.
            // That batch of submissions may still be on the GPU, and rewriting a set that has not declared UPDATE_AFTER_BIND
            // while it is referenced by a pending command buffer invalidates that whole buffer; see Device.WaitForFrameSlot.
            Device.WaitForFrameSlot(index);
            _stamps[index] = stamp; _cursors[index] = 0;
        }
        var ring = _sets[index];
        if (_cursors[index] == ring.Count) ring.Add(Device.DescriptorAllocator.AllocateSet(_setLayout));
        var set = ring[_cursors[index]++];
        var buffers = stackalloc DescriptorBufferInfo[2];
        buffers[0] = new() { Buffer = instances.Buffer.Buffer, Range = instances.Buffer.Size };
        buffers[1] = new() { Buffer = frame.Buffer.Buffer, Range = frame.Buffer.Size };
        var writes = stackalloc WriteDescriptorSet[2];
        for (int i = 0; i < 2; i++)
            writes[i] = new() { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = (uint)i,
                DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &buffers[i] };
        Device.Vk.UpdateDescriptorSets(Device.LogicalDevice, 2, writes, 0, null);

        _color = color; _depth = inputs.Depth; _colorViewVersion = color.ColorViewVersion;
        _instances = instances; _args = args; _frame = frame; _preparedSet = set;
        _command = Device.GraphicsCommandBuffer; _stamp = stamp; _frameIndex = Device.FrameIndex;
        _prepared = true;
        return true;
    }

    public override bool Draw()
    {
        bool prepared = _prepared;
        var color = _color; var depth = _depth; var args = _args; var set = _preparedSet;
        _prepared = false;
        _color = null; _depth = null; _instances = null; _args = null; _frame = null; _preparedSet = default;
        if (!prepared || _disposed || color == null || args == null || set.Handle == 0) return false;

        if (!Device.InRenderPass
            || Device.ActivePassId is not (Season.Rendering.RenderPassId.Scene
                or Season.Rendering.RenderPassId.SceneTransparent)
            || Device.GraphicsCommandBuffer.Handle != _command.Handle
            || Device.FrameIndex != _frameIndex
            || Device.GetCurrentRetireFenceValue() != _stamp) return false;
        // Vulkan's Scene Pass has no MSAA variant, so the identity check can be strict reference equality.
        if (!ReferenceEquals(Device.ActiveColorTarget, color)
            || color.ColorViewVersion != _colorViewVersion) return false;
        // null == use the depth the target ships with, which likewise requires that Prepare saw null, otherwise the depth semantics have already changed.
        if (!ReferenceEquals(Device.ActiveDepthTarget, depth)) return false;

        // The attachment shape can only be inferred from the actual bindings: a velocity slot means three targets (four if the normal slot is also present); otherwise an explicit depth target means two;
        // when neither is present the target's own RP is used, and whether it carries depth is decided by NoImplicitDepth.
        var velocity = Device.ActiveVelocityTarget;
        var normalTarget = Device.ActiveNormalTarget;
        Attachments shape;
        if (velocity != null)
        {
            if (!velocity.HasColor || velocity.ColorFormat != Format.R16G16Sfloat
                || velocity.Desc.SampleCount != 1) return false;
            if (normalTarget != null && normalTarget.ColorFormat != Format.R16G16B16A16Sfloat) return false;
            shape = normalTarget != null ? Attachments.ColorVelocityNormalDepth : Attachments.ColorVelocityDepth;
        }
        else if (Device.ActiveDepthTarget != null) shape = Attachments.ColorDepth;
        else shape = color.Desc.NoImplicitDepth ? Attachments.ColorOnly : Attachments.ColorDepth;

        var pipeline = GetOrCreatePipeline(new PipelineKey(color.ColorFormat, shape));
        if (pipeline.Handle == 0) return false;

        var vk = Device.Vk;
        // The main pipeline rebinds the PSO, descriptors, and index buffer on every SetPipeline, so the overrides here do not leak into subsequent draws.
        vk.CmdBindPipeline(_command, PipelineBindPoint.Graphics, pipeline);
        vk.CmdBindDescriptorSets(_command, PipelineBindPoint.Graphics, _layout, 0, 1, &set, 0, null);
        vk.CmdBindIndexBuffer(_command, Pipeline.UnitQuadIndexBuffer.Buffer, 0, IndexType.Uint16);
        // The instance count comes from the second field of the argument buffer; the CPU side never knows its value.
        vk.CmdDrawIndexedIndirect(_command, args.Buffer.Buffer, 0, 1,
            Season.Rendering.IndirectDrawIndexedArgs.SizeInBytes);
        return true;
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _prepared = false;
        _color = null; _depth = null; _instances = null; _args = null; _frame = null; _preparedSet = default;
        var vk = Device.Vk; var device = Device.LogicalDevice; var allocator = Device.DescriptorAllocator;
        var baked = _pipelines.Values.ToArray();
        _pipelines.Clear();
        var layout = _layout; var setLayout = _setLayout;
        _layout = default; _setLayout = default;
        Device.EnqueueDeferredRelease(() =>
        {
            foreach (var ring in _sets) foreach (var set in ring) allocator.FreeSet(set);
            foreach (var (pipeline, anchor) in baked)
            {
                if (pipeline.Handle != 0) vk.DestroyPipeline(device, pipeline, null);
                if (anchor.Handle != 0) vk.DestroyRenderPass(device, anchor, null);
            }
            if (layout.Handle != 0) vk.DestroyPipelineLayout(device, layout, null);
            if (setLayout.Handle != 0) vk.DestroyDescriptorSetLayout(device, setLayout, null);
        });
    }
}
