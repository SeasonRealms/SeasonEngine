// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Vulkan;
using Season.Rendering;
using Season.Rendering.Effects;

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

/// <summary>
/// P2 water surface material's Vulkan implementation. The skeleton is structurally identical to <see cref="VKDecalPipeline"/> (Prepare outside the pass,
/// consume once inside the pass), with the differences concentrated in three places:
/// <list type="bullet">
/// <item>Geometry is expanded from <c>gl_VertexIndex</c> (6 vertices for P3; from P4 onward a subdiv² mesh within a tile),
/// <c>gl_InstanceIndex</c> is the tile index, and the world matrix and tiling parameters are carried in the frame buffer/push constants, with no vertex buffer;</item>
/// <item>This pass has no depth attachment; SceneDepth keeps the <c>ShaderReadOnlyOptimal</c> published by the previous scene pass,
/// and occlusion relies on the software depth test in the shader; the water surface render pass is therefore completely identical in shape to a "no-normal" decal;</item>
/// <item>One extra refraction snapshot (a copy of the scene color before drawing begins). Outside the pass it is copied by
/// <c>CopyRenderTarget</c> on the <see cref="Vk"/> side and transitioned into the sampling layout — this pass's color attachment cannot be sampled at the same time.</item>
/// </list>
///
/// Blending uses the fixed-function <c>SRC_ALPHA / ONE_MINUS_SRC_ALPHA</c>, consistent with the original transparent geometry;
/// double-sided rendering (<c>CullMode = None</c>) lets the water surface be visible from underwater too, and it will not disappear entirely at very large grazing angles.
/// </summary>
internal sealed unsafe class VKWaterPipeline : Season.Rendering.WaterPipeline
{
    const int MaxWaters = WaterMath.MaxWaters;
    const ulong FrameBytes = WaterMath.ByteCount;

    readonly Dictionary<string, Texture> _textures;

    // Descriptors rotate by frame slot: each slot has its own set ring, and the cursor within a slot is reset to zero when the frame stamp changes.
    readonly List<DescriptorSet>[] _frameSets = new List<DescriptorSet>[(int)Device.frameCount];
    readonly int[] _frameCursors = new int[(int)Device.frameCount];
    readonly ulong[] _stamps = new ulong[(int)Device.frameCount];

    // Per-draw normal texture resolution result and descriptor set. Computed once in Prepare and only consumed in Draw.
    readonly Texture?[] _drawNormals = new Texture?[MaxWaters];
    readonly DescriptorSet[] _drawSets = new DescriptorSet[MaxWaters];
    /// <summary>This frame's Hi-Z four-level pyramid; when <see cref="HiZEffect.Ready"/> is false the whole group is empty and a placeholder is bound.</summary>
    readonly Texture?[] _hz = new Texture?[HiZEffect.LevelCount];

    DescriptorSetLayout _frameLayout;
    PipelineLayout _layout;
    Silk.NET.Vulkan.Pipeline _pipeline;

    WaterInputs _inputs;
    VKRenderTarget? _destination;
    Silk.NET.Vulkan.Buffer _buffer;
    CommandBuffer _command;
    int _drawCount;
    ulong _stamp;
    uint _frameIndex;
    bool _disposed;

    internal VKWaterPipeline(Dictionary<string, Texture> textures)
    {
        _textures = textures;
        for (int i = 0; i < _frameSets.Length; i++) _frameSets[i] = new();
        try { Initialize(); }
        catch { Dispose(); throw; }
    }

    void Initialize()
    {
        var vk = Device.Vk;
        var device = Device.LogicalDevice;
        var point = Pipeline.StaticPointSampler;
        var linear = Pipeline.StaticSampler;
        if (point.Handle == 0 || linear.Handle == 0) throw new InvalidOperationException("The water surface samplers are not yet initialized.");

        // Nine slots are packed into one set: depth, refraction, normal, sky, and the four Hi-Z levels are all resources that are swapped at most once per frame.
        // Splitting them into separate sets would only add one more binding call, and the decal style of "one set per texture" does not apply here
        // (one image per immutable sampler, yet several kinds of image require two sampling constraints).
        var bindings = stackalloc DescriptorSetLayoutBinding[9];
        bindings[0] = new() { Binding = 0, DescriptorCount = 1,
            StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            DescriptorType = DescriptorType.StorageBuffer };
        bindings[1] = new() { Binding = 1, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit,
            DescriptorType = DescriptorType.CombinedImageSampler, PImmutableSamplers = &point };
        // From binding 2 onward everything is a linearly filtered sampled texture: refraction, normal, sky LUT, and the four Hi-Z levels of P3.
        for (int i = 2; i < 9; i++)
            bindings[i] = new() { Binding = (uint)i, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit,
                DescriptorType = DescriptorType.CombinedImageSampler, PImmutableSamplers = &linear };
        var frameInfo = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 9, PBindings = bindings };
        if (vk.CreateDescriptorSetLayout(device, in frameInfo, null, out _frameLayout) != Result.Success)
            throw new InvalidOperationException("Failed to create the water surface descriptor layout.");

        // The vertex side needs the slot number to fetch the world matrix, and the pixel side needs it to fetch material and environment parameters, so both stages must see the push constant.
        var push = new PushConstantRange { StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, Size = 16 };
        var setLayouts = stackalloc DescriptorSetLayout[1] { _frameLayout };
        var layoutInfo = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1, PSetLayouts = setLayouts, PushConstantRangeCount = 1, PPushConstantRanges = &push };
        if (vk.CreatePipelineLayout(device, in layoutInfo, null, out _layout) != Result.Success)
            throw new InvalidOperationException("Failed to create the water surface pipeline layout.");

        ShaderModule vertex = default, pixel = default;
        try
        {
            // glslang's simplified C interface only recognizes main, so vertex and fragment are two independent sources, each assembling the same frame buffer accessor.
            vertex = ShaderCompiler.CreateShaderModule(vk, device, WaterShaders.VertexGlsl,
                ShaderStageFlags.VertexBit, "main", "water.vert", false);
            pixel = ShaderCompiler.CreateShaderModule(vk, device, WaterShaders.PixelGlsl,
                ShaderStageFlags.FragmentBit, "main", "water.frag", false);
            _pipeline = CreatePipeline(vk, device, vertex, pixel);
        }
        finally
        {
            if (vertex.Handle != 0) vk.DestroyShaderModule(device, vertex, null);
            if (pixel.Handle != 0) vk.DestroyShaderModule(device, pixel, null);
        }
    }

    Silk.NET.Vulkan.Pipeline CreatePipeline(Vk vk, Silk.NET.Vulkan.Device device, ShaderModule vertex, ShaderModule fragment)
    {
        ReadOnlySpan<byte> entry = "main\0"u8;
        var stages = stackalloc PipelineShaderStageCreateInfo[2];
        fixed (byte* name = entry)
        {
            stages[0] = new() { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = vertex, PName = name };
            stages[1] = new() { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = fragment, PName = name };
            var input = new PipelineVertexInputStateCreateInfo { SType = StructureType.PipelineVertexInputStateCreateInfo };
            var assembly = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList };
            var viewport = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1, ScissorCount = 1 };
            // Double-sided: the water surface should be visible from underwater too; with a very large pitch angle, back-face culling would make the whole water surface disappear.
            // Depth is not an attachment (occlusion uses a software comparison in the fragment stage), so neither depth test nor depth write is part of the pipeline state.
            var raster = new PipelineRasterizationStateCreateInfo { SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill, CullMode = CullModeFlags.None, FrontFace = FrontFace.Clockwise,
                LineWidth = 1, DepthClampEnable = false };
            var samples = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit };
            var blends = stackalloc PipelineColorBlendAttachmentState[1];
            // The shader outputs non-premultiplied color, so standard alpha blending is used; the alpha slot preserves the destination value.
            blends[0] = new PipelineColorBlendAttachmentState
            {
                BlendEnable = true,
                SrcColorBlendFactor = BlendFactor.SrcAlpha,
                DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.Zero,
                DstAlphaBlendFactor = BlendFactor.One,
                AlphaBlendOp = BlendOp.Add,
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit
            };
            var blend = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = 1, PAttachments = blends };
            var states = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
            var dynamic = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = 2, PDynamicStates = states };
            var info = new GraphicsPipelineCreateInfo { SType = StructureType.GraphicsPipelineCreateInfo,
                StageCount = 2, PStages = stages, PVertexInputState = &input, PInputAssemblyState = &assembly,
                PViewportState = &viewport, PRasterizationState = &raster, PMultisampleState = &samples,
                PColorBlendState = &blend, PDynamicState = &dynamic, Layout = _layout,
                RenderPass = Device.GetWaterRenderPass() };
            if (vk.CreateGraphicsPipelines(device, default, 1, in info, null, out var pipeline) != Result.Success)
                throw new InvalidOperationException("Failed to create the water surface graphics pipeline.");
            return pipeline;
        }
    }

    /// <summary>
    /// Validates the resource shapes. Like the decal, it only accepts resources the producer has already written and transitioned into the sampling layout, and does not issue a barrier inside the pass:
    /// the color attachment's layout transition is handled by the render pass's initial/finalLayout, depth keeps the <c>ShaderReadOnlyOptimal</c> published by the scene pass,
    /// and the refraction snapshot is transitioned into the sampling layout on the copy side.
    /// </summary>
    bool Resolve(in WaterInputs inputs, VKRenderTarget destination, out VKRenderTarget color,
        out VKRenderTarget depth, out VKRenderTarget refraction, out BufferResource buffer)
    {
        color = destination;
        depth = null!;
        refraction = null!;
        buffer = default;
        if (inputs.Color is not VKRenderTarget inputColor || inputs.Depth is not VKRenderTarget inputDepth
            || inputs.RefractionSource is not VKRenderTarget inputRefraction
            || inputColor.ColorFormat != Format.R16G16B16A16Sfloat
            || inputColor.Desc.SampleCount != 1 || !inputColor.HasColor
            || inputDepth.HasColor || inputDepth.Desc.DepthFormat != Season.Rendering.RtFormat.D32Float
            || inputDepth.Desc.SampleCount != 1
            || inputRefraction.ColorFormat != Format.R16G16B16A16Sfloat || !inputRefraction.HasColor
            || ReferenceEquals(inputRefraction, inputColor)
            || inputColor.ColorView.Handle == 0 || inputDepth.DepthView.Handle == 0
            || inputRefraction.ColorView.Handle == 0 || destination.ColorView.Handle == 0
            // This pass treats the color target as the blending base, so the shared layer must hand over the same target.
            || !ReferenceEquals(inputColor, destination)
            || inputColor.Width == 0 || inputColor.Height == 0
            || inputColor.Width != inputDepth.Width || inputColor.Height != inputDepth.Height
            || inputColor.Width != inputRefraction.Width || inputColor.Height != inputRefraction.Height
            || inputs.FrameBuffer is not VKStorageBuffer frame
            || frame.Buffer.Buffer.Handle == 0 || frame.Buffer.Size < FrameBytes
            || inputs.Count <= 0 || inputs.Count > MaxWaters) return false;
        depth = inputDepth;
        refraction = inputRefraction;
        buffer = frame.Buffer;
        return true;
    }

    public override bool Prepare(in WaterInputs inputs, RenderTarget destination)
    {
        _destination = null; _inputs = default;
        if (_disposed || Device.InRenderPass || Device.GraphicsCommandBuffer.Handle == 0
            || destination is not VKRenderTarget target
            || !Resolve(inputs, target, out _, out var depth, out var refraction, out var buffer)) return false;

        var command = Device.GraphicsCommandBuffer;

        // Per-water-surface normal texture: dictionary lookup only, no loading. Water surfaces whose name is unregistered or not yet fully uploaded fall back to procedural waves.
        Array.Clear(_drawNormals);
        for (int i = 0; i < inputs.Count && i < MaxWaters; i++)
        {
            string? name = inputs.Draws[i].Texture;
            if (name == null) continue;
            Texture? texture;
            lock (_textures) _textures.TryGetValue(name, out texture);
            if (texture == null || !texture.Ready || texture.View.Handle == 0) continue;
            // Transition the texture into the sampling layout the first time it is used; Prepare runs outside the pass, so the deferred-transition path is not taken here.
            texture.EnsureReadyForRendering(command);
            if (texture.CurrentLayout == ImageLayout.ShaderReadOnlyOptimal) _drawNormals[i] = texture;
        }

        int index = (int)Device.FrameIndex;
        ulong stamp = Device.GetCurrentRetireFenceValue();
        if (_stamps[index] != stamp)
        {
            // The first Prepare of this frame reuses the batch of sets from three frames ago, which may still be referenced by pending command buffers.
            Device.WaitForFrameSlot(index);
            _stamps[index] = stamp;
            _frameCursors[index] = 0;
        }
        var ring = _frameSets[index];
        // The normal texture is a per-water-surface resource, so each water surface gets its own set (analogous to one set per decal for the decal),
        // any other approach would have to cram mutually incompatible textures into the same set, ultimately degrading to a uniform "no texture" across the whole screen.
        while (ring.Count < _frameCursors[index] + inputs.Count)
            ring.Add(Device.DescriptorAllocator.AllocateSet(_frameLayout));
        int cursor = _frameCursors[index];

        // The sky LUT is the compute texture created by P0: when the name is unregistered (StaticCube tier) it falls back to a 1×1 white,
        // and the misc.w gate on the shader side ensures it is never actually sampled.
        Texture? sky = null;
        if (inputs.SkyView != null)
        {
            lock (_textures) _textures.TryGetValue(inputs.SkyView, out sky);
            if (sky != null && (!sky.Ready || sky.View.Handle == 0)) sky = null;
        }
        if (sky != null)
        {
            sky.EnsureReadyForRendering(command);
            if (sky.CurrentLayout != ImageLayout.ShaderReadOnlyOptimal) sky = null;
        }

        // The placeholder image must be a valid view and in the sampling layout: the white texture is preferred, and if unavailable the depth view is used as a stand-in —
        // the latter was never an attachment anyway, and the shader's two gates ensure it is never actually sampled.
        var white = Device.White;
        if (white != null && white.Ready && white.View.Handle != 0)
        {
            white.EnsureReadyForRendering(command);
            if (white.CurrentLayout != ImageLayout.ShaderReadOnlyOptimal) white = null;
        }
        var placeholder = white?.View ?? depth.DepthView;

        // P3 Hi-Z: fetched the same way as the sky LUT, just four images instead of one. If any level fails, the whole group falls back,
        // sharing its origin with the frame[12].x gate on the shader side (both defer to HiZEffect.Ready).
        Array.Clear(_hz);
        if (HiZEffect.Ready)
        {
            for (int level = 0; level < _hz.Length; level++)
            {
                Texture? texture;
                lock (_textures) _textures.TryGetValue(HiZEffect.LevelName(level), out texture);
                if (texture == null || !texture.Ready || texture.View.Handle == 0) { Array.Clear(_hz); break; }
                texture.EnsureReadyForRendering(command);
                if (texture.CurrentLayout != ImageLayout.ShaderReadOnlyOptimal) { Array.Clear(_hz); break; }
                _hz[level] = texture;
            }
        }

        var bufferInfo = new DescriptorBufferInfo { Buffer = buffer.Buffer, Range = FrameBytes };
        var depthImage = new DescriptorImageInfo { ImageView = depth.DepthView, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        var refractionImage = new DescriptorImageInfo { ImageView = refraction.ColorView, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        var skyImage = new DescriptorImageInfo { ImageView = sky?.View ?? placeholder, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        var hzImages = stackalloc DescriptorImageInfo[HiZEffect.LevelCount];
        for (int level = 0; level < HiZEffect.LevelCount; level++)
            hzImages[level] = new DescriptorImageInfo { ImageView = _hz[level]?.View ?? placeholder,
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        var writes = stackalloc WriteDescriptorSet[9];
        for (int i = 0; i < inputs.Count && i < MaxWaters; i++)
        {
            var set = ring[cursor++];
            var normalImage = new DescriptorImageInfo { ImageView = _drawNormals[i]?.View ?? placeholder,
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
            writes[0] = new() { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 0,
                DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &bufferInfo };
            writes[1] = new() { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 1,
                DescriptorCount = 1, DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &depthImage };
            writes[2] = new() { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 2,
                DescriptorCount = 1, DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &refractionImage };
            writes[3] = new() { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 3,
                DescriptorCount = 1, DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &normalImage };
            writes[4] = new() { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 4,
                DescriptorCount = 1, DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &skyImage };
            for (int level = 0; level < HiZEffect.LevelCount; level++)
                writes[5 + level] = new() { SType = StructureType.WriteDescriptorSet, DstSet = set,
                    DstBinding = (uint)(5 + level), DescriptorCount = 1,
                    DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &hzImages[level] };
            Device.Vk.UpdateDescriptorSets(Device.LogicalDevice, 9, writes, 0, null);
            _drawSets[i] = set;
        }
        _frameCursors[index] = cursor;

        _inputs = inputs; _destination = target;
        _drawCount = Math.Min(inputs.Count, MaxWaters);
        _buffer = buffer.Buffer; _command = command; _stamp = stamp; _frameIndex = Device.FrameIndex;
        return true;
    }

    public override bool Draw()
    {
        var destination = _destination;
        var inputs = _inputs;
        _destination = null; _inputs = default;
        if (_disposed || destination == null || !Device.InRenderPass
            || Device.ActivePassId != Season.Rendering.RenderPassId.SceneWater
            || !ReferenceEquals(Device.ActiveColorTarget, destination)
            || Device.GraphicsCommandBuffer.Handle != _command.Handle || Device.FrameIndex != _frameIndex
            || Device.GetCurrentRetireFenceValue() != _stamp) return false;
        var vk = Device.Vk;
        var command = _command;
        var layout = _layout;
        if (!Resolve(inputs, destination, out _, out _, out _, out var buffer)
            || buffer.Buffer.Handle != _buffer.Handle) return false;

        vk.CmdBindPipeline(command, PipelineBindPoint.Graphics, _pipeline);
        // The shader side is a uint4: slot + tiled mesh parameters (tilesX, tilesZ, subdiv), which must be written as integer bits.
        // For an untiled water surface the three are (1,1,0), and the vertex shader falls back to a single quad accordingly.
        var constants = stackalloc uint[4];
        int drawn = 0;
        for (int i = 0; i < _drawCount; i++)
        {
            var set = _drawSets[i];
            var draw = inputs.Draws[i];
            vk.CmdBindDescriptorSets(command, PipelineBindPoint.Graphics, layout, 0, 1, &set, 0, null);
            constants[0] = (uint)draw.Index;
            constants[1] = (uint)draw.TilesX;
            constants[2] = (uint)draw.TilesZ;
            constants[3] = (uint)draw.Subdiv;
            vk.CmdPushConstants(command, layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, 16, constants);
            // P4: tiled instancing. The tile index goes through gl_InstanceIndex (firstInstance stays 0),
            // and the slot is still carried by the push constant — only WebGPU needs to squeeze both into the same instance number.
            vk.CmdDraw(command, (uint)draw.VertexCount, (uint)draw.InstanceCount, 0, 0);
            drawn++;
        }
        return drawn > 0;
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _destination = null; _inputs = default;
        var vk = Device.Vk;
        var device = Device.LogicalDevice;
        var allocator = Device.DescriptorAllocator;
        var layout = _layout; _layout = default;
        var frameLayout = _frameLayout; _frameLayout = default;
        var pipeline = _pipeline; _pipeline = default;
        Array.Clear(_drawNormals);
        Array.Clear(_drawSets);
        Array.Clear(_hz);
        Device.EnqueueDeferredRelease(() =>
        {
            foreach (var ring in _frameSets) foreach (var set in ring) allocator.FreeSet(set);
            if (pipeline.Handle != 0) vk.DestroyPipeline(device, pipeline, null);
            if (layout.Handle != 0) vk.DestroyPipelineLayout(device, layout, null);
            if (frameLayout.Handle != 0) vk.DestroyDescriptorSetLayout(device, frameLayout, null);
        });
    }
}
