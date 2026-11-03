// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Vulkan;
using Season.Rendering;
using Season.Rendering.Effects;

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

/// <summary>
/// P1 waterfall curtain's Vulkan implementation. The skeleton is structurally identical to <see cref="VKWaterPipeline"/>
/// (prepare outside the pass, consume once inside the pass), with the differences concentrated in three places:
/// <list type="bullet">
/// <item>Geometry is expanded from <c>gl_VertexIndex</c> along the control points stored in the frame buffer
/// (Catmull-Rom rows + a closed section ring per row); there is no vertex buffer and no instance,
/// the slot / column count / subdivision come in through push constants;</item>
/// <item>No refraction snapshot: the curtain never samples the scene color, so the color attachment is itself the blend
/// base and the only extra resource per frame is the depth SRV;</item>
/// <item>Three textures (Mask / Normal / Flow) are resolved per curtain. Any curtain that declared a texture which
/// failed to resolve is skipped whole — binding the white texture would turn foam into a solid white sheet.</item>
/// </list>
///
/// Descriptor shape: set 0 is frame-level (frame buffer + depth), set 1 is the per-curtain texture table. The mask is
/// bound twice (bindings 0 and 3) because Vulkan bakes one sampler per binding, and the R/G channels must clamp while
/// the B/A channels must wrap — the same split DX expresses with two static samplers.
///
/// Blending uses the fixed-function <c>SRC_ALPHA / ONE_MINUS_SRC_ALPHA</c>; double-sided rendering
/// (<c>CullMode = None</c>) keeps the far wall of the section ring visible, which is the half that carries the thickness.
/// </summary>
internal sealed unsafe class VKWaterfallPipeline : Season.Rendering.WaterfallPipeline
{
    const int MaxSheets = WaterfallMath.MaxSheets;
    const ulong FrameBytes = WaterfallMath.ByteCount;

    readonly Dictionary<string, Texture> _textures;

    // Descriptors rotate by frame slot. Set 0 (frame buffer + depth) and set 1 (per-curtain textures) have separate rings
    // because their lifetimes differ: one set 0 per Prepare, one set 1 per curtain. Mixing them into a single ring would
    // make the layout of a slot depend on the previous frame's curtain count.
    readonly List<DescriptorSet>[] _frameSets = new List<DescriptorSet>[(int)Device.frameCount];
    readonly List<DescriptorSet>[] _textureSets = new List<DescriptorSet>[(int)Device.frameCount];
    readonly int[] _frameCursors = new int[(int)Device.frameCount];
    readonly int[] _textureCursors = new int[(int)Device.frameCount];
    readonly ulong[] _stamps = new ulong[(int)Device.frameCount];

    // Per-curtain texture resolution result and descriptor set. Computed once in Prepare and only consumed in Draw.
    readonly Texture?[] _masks = new Texture?[MaxSheets];
    readonly Texture?[] _normals = new Texture?[MaxSheets];
    readonly Texture?[] _flows = new Texture?[MaxSheets];
    readonly bool[] _skip = new bool[MaxSheets];
    readonly DescriptorSet[] _drawSets = new DescriptorSet[MaxSheets];

    DescriptorSetLayout _frameLayout;
    DescriptorSetLayout _textureLayout;
    PipelineLayout _layout;
    Silk.NET.Vulkan.Pipeline _pipeline;

    WaterfallInputs _inputs;
    VKRenderTarget? _destination;
    DescriptorSet _frameSet;
    Silk.NET.Vulkan.Buffer _buffer;
    CommandBuffer _command;
    int _drawCount;
    ulong _stamp;
    uint _frameIndex;
    bool _disposed;

    /// <summary>
    /// <paramref name="ribbon"/> switches the shader source only: a river ribbon is a flat horizontal band whose current runs in
    /// meters per second, a waterfall curtain is a closed section ring that accelerates from lip to landing point. The descriptor
    /// layouts, push constant, blending and pass identity are the same, and the frame buffers are slot-for-slot equal (see
    /// <see cref="WaterRibbonMath"/>), so this is a variant rather than a second class.
    /// </summary>
    internal VKWaterfallPipeline(Dictionary<string, Texture> textures, bool ribbon = false)
    {
        _textures = textures;
        _ribbon = ribbon;
        for (int i = 0; i < _frameSets.Length; i++) { _frameSets[i] = new(); _textureSets[i] = new(); }
        try { Initialize(); }
        catch { Dispose(); throw; }
    }

    /// <summary>Whether this instance draws river ribbons instead of waterfall curtains.</summary>
    readonly bool _ribbon;

    void Initialize()
    {
        var vk = Device.Vk;
        var device = Device.LogicalDevice;
        var point = Pipeline.StaticPointSampler;
        var linear = Pipeline.StaticSampler;
        var wrap = Pipeline.WrapSampler;
        if (point.Handle == 0 || linear.Handle == 0 || wrap.Handle == 0)
            throw new InvalidOperationException("The waterfall samplers are not yet initialized.");

        // Set 0 is everything that stays fixed for the whole frame: the frame buffer and the depth sampling source.
        var frameBindings = stackalloc DescriptorSetLayoutBinding[2];
        frameBindings[0] = new() { Binding = 0, DescriptorCount = 1,
            StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            DescriptorType = DescriptorType.StorageBuffer };
        // Depth is a point sample by definition (the software depth test is per pixel); D32 with a linear sampler is an
        // optional feature, so the immutable sampler must be the nearest one.
        frameBindings[1] = new() { Binding = 1, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit,
            DescriptorType = DescriptorType.CombinedImageSampler, PImmutableSamplers = &point };
        var frameInfo = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 2, PBindings = frameBindings };
        if (vk.CreateDescriptorSetLayout(device, in frameInfo, null, out _frameLayout) != Result.Success)
            throw new InvalidOperationException("Failed to create the waterfall descriptor layout.");

        // Set 1 is the per-curtain texture table: mask (clamp), normal (wrap), flow (wrap), mask again (wrap).
        var textureBindings = stackalloc DescriptorSetLayoutBinding[4];
        textureBindings[0] = new() { Binding = 0, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit,
            DescriptorType = DescriptorType.CombinedImageSampler, PImmutableSamplers = &linear };
        for (int i = 1; i < 4; i++)
            textureBindings[i] = new() { Binding = (uint)i, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit,
                DescriptorType = DescriptorType.CombinedImageSampler, PImmutableSamplers = &wrap };
        var textureInfo = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 4, PBindings = textureBindings };
        if (vk.CreateDescriptorSetLayout(device, in textureInfo, null, out _textureLayout) != Result.Success)
            throw new InvalidOperationException("Failed to create the waterfall texture layout.");

        // Both stages read the push constant: the vertex side needs the slot plus the mesh parameters, the pixel side
        // needs the slot to fetch material and environment rows.
        var push = new PushConstantRange { StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, Size = 16 };
        var setLayouts = stackalloc DescriptorSetLayout[2] { _frameLayout, _textureLayout };
        var layoutInfo = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 2, PSetLayouts = setLayouts, PushConstantRangeCount = 1, PPushConstantRanges = &push };
        if (vk.CreatePipelineLayout(device, in layoutInfo, null, out _layout) != Result.Success)
            throw new InvalidOperationException("Failed to create the waterfall pipeline layout.");

        ShaderModule vertex = default, pixel = default;
        try
        {
            // glslang's simplified C interface only recognizes main, so vertex and fragment are two independent sources,
            // each assembling the same frame buffer accessor. The ribbon variant keeps the same entry point and binding shape.
            string vertexSource = _ribbon ? WaterRibbonShaders.VertexGlsl : WaterfallShaders.VertexGlsl;
            string pixelSource = _ribbon ? WaterRibbonShaders.PixelGlsl : WaterfallShaders.PixelGlsl;
            vertex = ShaderCompiler.CreateShaderModule(vk, device, vertexSource,
                ShaderStageFlags.VertexBit, "main", "waterfall.vert", false);
            pixel = ShaderCompiler.CreateShaderModule(vk, device, pixelSource,
                ShaderStageFlags.FragmentBit, "main", "waterfall.frag", false);
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
            // Double-sided: the section is a closed ring, and its inner wall is exactly the half that reads as thickness.
            // Depth is not an attachment (occlusion uses a software comparison in the fragment stage).
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
            // The waterfall pass is shaped exactly like the water pass (single HDR color attachment, no depth), so it is
            // baked against the same render pass object — Device.BeginPass routes SceneWaterfall there too.
            var info = new GraphicsPipelineCreateInfo { SType = StructureType.GraphicsPipelineCreateInfo,
                StageCount = 2, PStages = stages, PVertexInputState = &input, PInputAssemblyState = &assembly,
                PViewportState = &viewport, PRasterizationState = &raster, PMultisampleState = &samples,
                PColorBlendState = &blend, PDynamicState = &dynamic, Layout = _layout,
                RenderPass = Device.GetWaterRenderPass() };
            if (vk.CreateGraphicsPipelines(device, default, 1, in info, null, out var pipeline) != Result.Success)
                throw new InvalidOperationException("Failed to create the waterfall graphics pipeline.");
            return pipeline;
        }
    }

    /// <summary>
    /// Validates the shapes. Like the water pass it only accepts resources the producer has already written and
    /// transitioned into the sampling layout, and does not issue a barrier inside the pass: the color attachment's
    /// transition is handled by the render pass, and the depth keeps the <c>ShaderReadOnlyOptimal</c> published by the
    /// scene pass. There is no refraction snapshot — the curtain never reads the scene color.
    /// </summary>
    bool Resolve(in WaterfallInputs inputs, VKRenderTarget destination, out VKRenderTarget depth, out BufferResource buffer)
    {
        depth = null!;
        buffer = default;
        if (inputs.Color is not VKRenderTarget color || inputs.Depth is not VKRenderTarget inputDepth
            || color.ColorFormat != Format.R16G16B16A16Sfloat
            || color.Desc.SampleCount != 1 || !color.HasColor
            || inputDepth.HasColor || inputDepth.Desc.DepthFormat != Season.Rendering.RtFormat.D32Float
            || inputDepth.Desc.SampleCount != 1
            || color.ColorView.Handle == 0 || inputDepth.DepthView.Handle == 0 || destination.ColorView.Handle == 0
            // This pass treats the color target as the blend base, so the shared layer must hand over the same target.
            || !ReferenceEquals(color, destination)
            || color.Width == 0 || color.Height == 0
            || color.Width != inputDepth.Width || color.Height != inputDepth.Height
            || inputs.FrameBuffer is not VKStorageBuffer frame
            || frame.Buffer.Buffer.Handle == 0 || frame.Buffer.Size < FrameBytes
            || inputs.Count <= 0 || inputs.Count > MaxSheets) return false;
        depth = inputDepth;
        buffer = frame.Buffer;
        return true;
    }

    /// <summary>Resolves one per-curtain texture; a null name means "declared absent" and succeeds.</summary>
    bool ResolveTexture(string? name, Texture?[] target, int index)
    {
        target[index] = null;
        if (name == null) return true;
        Texture? texture;
        lock (_textures) _textures.TryGetValue(name, out texture);
        if (texture == null || !texture.Ready || texture.View.Handle == 0) return false;
        texture.EnsureReadyForRendering(Device.GraphicsCommandBuffer);
        if (texture.CurrentLayout != ImageLayout.ShaderReadOnlyOptimal) return false;
        target[index] = texture;
        return true;
    }

    public override bool Prepare(in WaterfallInputs inputs, RenderTarget destination)
    {
        _destination = null; _inputs = default;
        if (_disposed || Device.InRenderPass || Device.GraphicsCommandBuffer.Handle == 0
            || destination is not VKRenderTarget target
            || !Resolve(inputs, target, out var depth, out var buffer)) return false;

        var command = Device.GraphicsCommandBuffer;
        Array.Clear(_masks); Array.Clear(_normals); Array.Clear(_flows); Array.Clear(_skip);
        for (int i = 0; i < inputs.Count; i++)
        {
            var draw = inputs.Draws[i];
            // Any declared-but-missing texture skips the whole curtain: sampling a white placeholder would turn the foam
            // channel into a solid value, which reads as "a white sheet" rather than "a missing texture".
            _skip[i] = !ResolveTexture(draw.Mask, _masks, i)
                || !ResolveTexture(draw.Normal, _normals, i)
                || !ResolveTexture(draw.Flow, _flows, i);
        }

        int index = (int)Device.FrameIndex;
        ulong stamp = Device.GetCurrentRetireFenceValue();
        if (_stamps[index] != stamp)
        {
            // The first Prepare of this frame reuses the batch of sets from three frames ago, which may still be referenced by pending command buffers.
            Device.WaitForFrameSlot(index);
            _stamps[index] = stamp;
            _frameCursors[index] = 0;
            _textureCursors[index] = 0;
        }
        var frameRing = _frameSets[index];
        var textureRing = _textureSets[index];
        // The texture set is per curtain because the three maps differ per curtain; cramming them together would degrade
        // to a uniform "no texture" across the whole screen.
        while (frameRing.Count < _frameCursors[index] + 1)
            frameRing.Add(Device.DescriptorAllocator.AllocateSet(_frameLayout));
        var frameSet = frameRing[_frameCursors[index]++];
        while (textureRing.Count < _textureCursors[index] + inputs.Count)
            textureRing.Add(Device.DescriptorAllocator.AllocateSet(_textureLayout));
        int cursor = _textureCursors[index];

        // The placeholder image must be a valid view in the sampling layout: the white texture is preferred, and if
        // unavailable the depth view is used as a stand-in — the shader's three flags ensure it is never sampled.
        var white = Device.White;
        if (white != null && white.Ready && white.View.Handle != 0)
        {
            white.EnsureReadyForRendering(command);
            if (white.CurrentLayout != ImageLayout.ShaderReadOnlyOptimal) white = null;
        }
        var placeholder = white?.View ?? depth.DepthView;

        var bufferInfo = new DescriptorBufferInfo { Buffer = buffer.Buffer, Range = FrameBytes };
        var depthImage = new DescriptorImageInfo { ImageView = depth.DepthView, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        var writes = stackalloc WriteDescriptorSet[2];
        writes[0] = new() { SType = StructureType.WriteDescriptorSet, DstSet = frameSet, DstBinding = 0,
            DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &bufferInfo };
        writes[1] = new() { SType = StructureType.WriteDescriptorSet, DstSet = frameSet, DstBinding = 1,
            DescriptorCount = 1, DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &depthImage };
        Device.Vk.UpdateDescriptorSets(Device.LogicalDevice, 2, writes, 0, null);

        var textureWrites = stackalloc WriteDescriptorSet[4];
        for (int i = 0; i < inputs.Count && i < MaxSheets; i++)
        {
            var set = textureRing[cursor++];
            var maskImage = new DescriptorImageInfo { ImageView = _masks[i]?.View ?? placeholder,
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
            var normalImage = new DescriptorImageInfo { ImageView = _normals[i]?.View ?? placeholder,
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
            var flowImage = new DescriptorImageInfo { ImageView = _flows[i]?.View ?? placeholder,
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
            textureWrites[0] = new() { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 0,
                DescriptorCount = 1, DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &maskImage };
            textureWrites[1] = new() { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 1,
                DescriptorCount = 1, DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &normalImage };
            textureWrites[2] = new() { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 2,
                DescriptorCount = 1, DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &flowImage };
            // Binding 3 is the same mask image under the wrap sampler: the tiled B/A channels need repeat addressing.
            textureWrites[3] = new() { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 3,
                DescriptorCount = 1, DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &maskImage };
            Device.Vk.UpdateDescriptorSets(Device.LogicalDevice, 4, textureWrites, 0, null);
            _drawSets[i] = set;
        }
        _textureCursors[index] = cursor;

        _inputs = inputs; _destination = target; _frameSet = frameSet;
        _drawCount = Math.Min(inputs.Count, MaxSheets);
        _buffer = buffer.Buffer; _command = command; _stamp = stamp; _frameIndex = Device.FrameIndex;
        return true;
    }

    public override bool Draw()
    {
        var destination = _destination;
        var inputs = _inputs;
        _destination = null; _inputs = default;
        if (_disposed || destination == null || !Device.InRenderPass
            || Device.ActivePassId != Season.Rendering.RenderPassId.SceneWaterfall
            || !ReferenceEquals(Device.ActiveColorTarget, destination)
            || Device.GraphicsCommandBuffer.Handle != _command.Handle || Device.FrameIndex != _frameIndex
            || Device.GetCurrentRetireFenceValue() != _stamp) return false;
        var vk = Device.Vk;
        var command = _command;
        var layout = _layout;
        if (!Resolve(inputs, destination, out _, out var buffer)
            || buffer.Buffer.Handle != _buffer.Handle) return false;

        vk.CmdBindPipeline(command, PipelineBindPoint.Graphics, _pipeline);
        // The shader side is a uint4: slot plus the mesh parameters (columns, subdiv). They must be written as integer
        // bits, and the vertex stage derives the same grid the shared layer used to compute the vertex count.
        var constants = stackalloc uint[4];
        var frameSet = _frameSet;
        vk.CmdBindDescriptorSets(command, PipelineBindPoint.Graphics, layout, 0, 1, &frameSet, 0, null);
        int drawn = 0;
        for (int i = 0; i < _drawCount; i++)
        {
            if (_skip[i]) continue;
            var draw = inputs.Draws[i];
            if (draw.VertexCount <= 0) continue;
            var set = _drawSets[i];
            vk.CmdBindDescriptorSets(command, PipelineBindPoint.Graphics, layout, 1, 1, &set, 0, null);
            constants[0] = (uint)draw.Index;
            constants[1] = (uint)draw.Columns;
            constants[2] = (uint)draw.Subdiv;
            constants[3] = 0u;
            vk.CmdPushConstants(command, layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, 16, constants);
            vk.CmdDraw(command, (uint)draw.VertexCount, 1, 0, 0);
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
        var textureLayout = _textureLayout; _textureLayout = default;
        var pipeline = _pipeline; _pipeline = default;
        Array.Clear(_masks);
        Array.Clear(_normals);
        Array.Clear(_flows);
        Array.Clear(_skip);
        Array.Clear(_drawSets);
        Device.EnqueueDeferredRelease(() =>
        {
            foreach (var ring in _frameSets) foreach (var set in ring) allocator.FreeSet(set);
            foreach (var ring in _textureSets) foreach (var set in ring) allocator.FreeSet(set);
            if (pipeline.Handle != 0) vk.DestroyPipeline(device, pipeline, null);
            if (layout.Handle != 0) vk.DestroyPipelineLayout(device, layout, null);
            if (frameLayout.Handle != 0) vk.DestroyDescriptorSetLayout(device, frameLayout, null);
            if (textureLayout.Handle != 0) vk.DestroyDescriptorSetLayout(device, textureLayout, null);
        });
    }
}
