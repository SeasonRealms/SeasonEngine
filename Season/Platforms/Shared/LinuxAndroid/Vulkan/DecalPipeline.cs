// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Numerics;
using Silk.NET.Vulkan;
using Season.Rendering;

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

/// <summary>
/// Vulkan implementation of the D1 screen-space decal, structurally isomorphic to <see cref="VKFogCompositePipeline"/>:
/// Prepare resolves resources outside the Pass and leaves depth in a sample layout, and Draw can only
/// consume it once within <see cref="RenderPassId.SceneDecal"/>.
///
/// <para>Three points aligned item-by-item with the DX version:</para>
/// <list type="bullet">
/// <item>one 36-vertex <c>vkCmdDraw</c> per decal, vertices reconstructed from <c>gl_VertexIndex</c>, with no vertex buffer at all;</item>
/// <item>rasterization keeps only back faces (<c>CullMode = Front</c>). The negative-height viewport and DX's screen Y flip cancel each other out,
/// so both backends reach the same "front face" decision for the same box face;</item>
/// <item>blending uses fixed-function: Modulate is <c>DST_COLOR / ZERO</c>, Additive is <c>ONE / ONE</c>,
/// and the alpha for both is <c>ZERO / ONE</c> (preserving the destination alpha).</item>
/// </list>
///
/// <para>Roughness write (s9) uses a second color attachment, with the write mask enabling only the B channel, and blending is <c>SRC_ALPHA / ONE_MINUS_SRC_ALPHA</c>,
/// so the octahedral normal (.xy) and reflection weight (.w) in the attachment keep their original values. This form's render pass and the form that does not write roughness
/// have a different attachment count and are mutually incompatible, so a separate PSO is baked for each.</para>
/// </summary>
internal sealed unsafe class VKDecalPipeline : Season.Rendering.DecalPipeline
{
    const int MaxDecals = Season.Rendering.DecalMath.MaxDecals;
    const ulong FrameBytes = Season.Rendering.DecalMath.ByteCount;

    readonly Dictionary<string, Texture> _textures;

    // Descriptors rotate by frame slot: each slot has its own set ring, and the in-slot cursor resets to zero when the frame-number stamp changes.
    readonly List<DescriptorSet>[] _frameSets = new List<DescriptorSet>[(int)Device.frameCount];
    readonly int[] _frameCursors = new int[(int)Device.frameCount];
    readonly List<DescriptorSet>[] _textureSets = new List<DescriptorSet>[(int)Device.frameCount];
    readonly int[] _textureCursors = new int[(int)Device.frameCount];
    readonly ulong[] _stamps = new ulong[(int)Device.frameCount];

    // Per-draw texture resolution results. Prepare computes them once, and Draw only consumes them.
    readonly Texture?[] _drawTextures = new Texture?[MaxDecals];
    readonly bool[] _drawSkip = new bool[MaxDecals];
    readonly DescriptorSet[] _drawSets = new DescriptorSet[MaxDecals];

    readonly Silk.NET.Vulkan.Pipeline[,] _pipelines = new Silk.NET.Vulkan.Pipeline[2, 2];
    DescriptorSetLayout _frameLayout;
    DescriptorSetLayout _textureLayout;
    PipelineLayout _layout;

    DecalInputs _inputs;
    VKRenderTarget? _destination;
    Silk.NET.Vulkan.Buffer _buffer;
    CommandBuffer _command;
    DescriptorSet _frameSet;
    DescriptorSet _fallbackSet;
    ulong _stamp;
    uint _frameIndex;
    bool _withNormal;
    bool _disposed;

    internal VKDecalPipeline(Dictionary<string, Texture> textures)
    {
        _textures = textures;
        for (int i = 0; i < _frameSets.Length; i++)
        {
            _frameSets[i] = new();
            _textureSets[i] = new();
        }
        try { Initialize(); }
        catch { Dispose(); throw; }
    }

    void Initialize()
    {
        var vk = Device.Vk;
        var device = Device.LogicalDevice;
        var point = Pipeline.StaticPointSampler;
        var linear = Pipeline.StaticSampler;
        if (point.Handle == 0 || linear.Handle == 0) throw new InvalidOperationException("Decal sampler not yet initialized.");

        var bindings = stackalloc DescriptorSetLayoutBinding[2];
        // Both stages read DecalFrame: the vertex stage fetches the view and projection rows to place the box, the pixel stage
        // fetches the decal block. Declaring it as FRAGMENT-only makes vkCreateGraphicsPipelines fail
        // (VUID-VkGraphicsPipelineCreateInfo-layout-07988: the SPIR-V vertex stage uses descriptor [Set 0, Binding 0]).
        bindings[0] = new() { Binding = 0, DescriptorCount = 1, StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            DescriptorType = DescriptorType.StorageBuffer };
        bindings[1] = new() { Binding = 1, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit,
            DescriptorType = DescriptorType.CombinedImageSampler, PImmutableSamplers = &point };
        var frameInfo = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 2, PBindings = bindings };
        if (vk.CreateDescriptorSetLayout(device, in frameInfo, null, out _frameLayout) != Result.Success)
            throw new InvalidOperationException("Failed to create decal descriptor layout.");

        var textureBinding = new DescriptorSetLayoutBinding { Binding = 0, DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit, DescriptorType = DescriptorType.CombinedImageSampler,
            PImmutableSamplers = &linear };
        var textureInfo = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1, PBindings = &textureBinding };
        if (vk.CreateDescriptorSetLayout(device, in textureInfo, null, out _textureLayout) != Result.Success)
            throw new InvalidOperationException("Failed to create decal texture descriptor layout.");

        var setLayouts = stackalloc DescriptorSetLayout[2] { _frameLayout, _textureLayout };
        // The vertex stage uses the decal index to fetch a matrix, and the pixel stage uses the index to fetch the whole block of data, so both stages must see it.
        var push = new PushConstantRange { StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, Size = 16 };
        var layoutInfo = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 2, PSetLayouts = setLayouts, PushConstantRangeCount = 1, PPushConstantRanges = &push };
        if (vk.CreatePipelineLayout(device, in layoutInfo, null, out _layout) != Result.Success)
            throw new InvalidOperationException("Failed to create decal pipeline layout.");

        ShaderModule vertex = default, pixel = default, pixelRoughness = default;
        try
        {
            vertex = ShaderCompiler.CreateShaderModule(vk, device, Season.Rendering.DecalShaders.VertexGlsl,
                ShaderStageFlags.VertexBit, "main", "decal.vert", false);
            pixel = ShaderCompiler.CreateShaderModule(vk, device, Season.Rendering.DecalShaders.PixelGlsl,
                ShaderStageFlags.FragmentBit, "main", "decal.frag", false);
            pixelRoughness = ShaderCompiler.CreateShaderModule(vk, device, Season.Rendering.DecalShaders.PixelRoughnessGlsl,
                ShaderStageFlags.FragmentBit, "main", "decal_roughness.frag", false);
            for (int normal = 0; normal < 2; normal++)
            {
                for (int mode = 0; mode < 2; mode++)
                {
                    var fragment = normal != 0 ? pixelRoughness : pixel;
                    _pipelines[mode, normal] = CreatePipeline(vk, device, vertex, fragment,
                        mode != 0, normal != 0);
                }
            }
        }
        finally
        {
            if (vertex.Handle != 0) vk.DestroyShaderModule(device, vertex, null);
            if (pixel.Handle != 0) vk.DestroyShaderModule(device, pixel, null);
            if (pixelRoughness.Handle != 0) vk.DestroyShaderModule(device, pixelRoughness, null);
        }
    }

    Silk.NET.Vulkan.Pipeline CreatePipeline(Vk vk, Silk.NET.Vulkan.Device device, ShaderModule vertex, ShaderModule fragment,
        bool additive, bool withNormal)
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
            // CullMode.Front keeps only back faces: the box's six faces are counter-clockwise when viewed from outside the box, and the back face is exactly the side that "hits each pixel once".
            // Depth is not an attachment, so the depth test and write must be turned off.
            var raster = new PipelineRasterizationStateCreateInfo { SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill, CullMode = CullModeFlags.FrontBit, FrontFace = FrontFace.Clockwise, LineWidth = 1 };
            var samples = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit };

            var blends = stackalloc PipelineColorBlendAttachmentState[2];
            // RT0: Modulate = dst * src, Additive = dst + src; for alpha, both modes preserve the destination value.
            blends[0] = new PipelineColorBlendAttachmentState
            {
                BlendEnable = true,
                SrcColorBlendFactor = additive ? BlendFactor.One : BlendFactor.DstColor,
                DstColorBlendFactor = additive ? BlendFactor.One : BlendFactor.Zero,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.Zero,
                DstAlphaBlendFactor = BlendFactor.One,
                AlphaBlendOp = BlendOp.Add,
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit
            };
            // RT1 (roughness-write form only): enables only the B channel, interpolates with the source alpha, and preserves the original value bit-for-bit when the source alpha is 0.
            blends[1] = new PipelineColorBlendAttachmentState
            {
                BlendEnable = true,
                SrcColorBlendFactor = BlendFactor.SrcAlpha,
                DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One,
                DstAlphaBlendFactor = BlendFactor.Zero,
                AlphaBlendOp = BlendOp.Add,
                ColorWriteMask = ColorComponentFlags.BBit
            };
            var blend = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = withNormal ? 2u : 1u, PAttachments = blends };
            var states = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
            var dynamic = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = 2, PDynamicStates = states };
            var info = new GraphicsPipelineCreateInfo { SType = StructureType.GraphicsPipelineCreateInfo,
                StageCount = 2, PStages = stages, PVertexInputState = &input, PInputAssemblyState = &assembly,
                PViewportState = &viewport, PRasterizationState = &raster, PMultisampleState = &samples,
                PColorBlendState = &blend, PDynamicState = &dynamic, Layout = _layout,
                RenderPass = Device.GetDecalRenderPass(withNormal) };
            if (vk.CreateGraphicsPipelines(device, default, 1, in info, null, out var pipeline) != Result.Success)
                throw new InvalidOperationException("Failed to create decal graphics pipeline.");
            return pipeline;
        }
    }

    /// <summary>
    /// Validates the resource shapes and fixes this frame's attachment form. Like the fog composite, it only accepts resources that the producer has written and transitioned into a sample layout,
    /// and does not re-issue a barrier inside the Pass: the layout switching of color and normal is done by the render pass's initial/final layout,
    /// and depth keeps the ShaderReadOnlyOptimal published by the previous scene Pass.
    /// </summary>
    bool Resolve(in DecalInputs inputs, VKRenderTarget destination, out VKRenderTarget color,
        out VKRenderTarget depth, out VKRenderTarget? normal, out BufferResource buffer)
    {
        color = destination;
        depth = null!;
        normal = null;
        buffer = default;
        if (inputs.Color is not VKRenderTarget inputColor || inputs.Depth is not VKRenderTarget inputDepth
            || inputColor.ColorFormat != Format.R16G16B16A16Sfloat
            || inputColor.Desc.SampleCount != 1 || !inputColor.HasColor
            || inputDepth.HasColor || inputDepth.Desc.DepthFormat != Season.Rendering.RtFormat.D32Float
            || inputDepth.Desc.SampleCount != 1
            || inputColor.ColorView.Handle == 0 || inputDepth.DepthView.Handle == 0 || destination.ColorView.Handle == 0
            || !ReferenceEquals(inputColor, destination)
            || inputColor.Width == 0 || inputColor.Height == 0
            || inputColor.Width != inputDepth.Width || inputColor.Height != inputDepth.Height
            || inputs.FrameBuffer is not VKStorageBuffer frame
            || frame.Buffer.Buffer.Handle == 0 || frame.Buffer.Size < FrameBytes) return false;
        depth = inputDepth;
        if (inputs.WriteRoughness && inputs.Normal is VKRenderTarget inputNormal)
        {
            if (!ReferenceEquals(inputNormal, inputColor) && inputNormal.ColorView.Handle != 0
                && inputNormal.ColorFormat == Format.R16G16B16A16Sfloat
                && inputNormal.Width == inputColor.Width && inputNormal.Height == inputColor.Height)
                normal = inputNormal;
        }
        buffer = frame.Buffer;
        return true;
    }

    public override bool Prepare(in Season.Rendering.DecalInputs inputs, Season.Rendering.RenderTarget destination)
    {
        _destination = null; _inputs = default; _frameSet = default; _fallbackSet = default;
        _withNormal = false;
        if (_disposed || Device.InRenderPass || Device.GraphicsCommandBuffer.Handle == 0
            || destination is not VKRenderTarget target
            || !Resolve(inputs, target, out _, out var depth, out var normal, out var buffer)) return false;

        Array.Clear(_drawTextures);
        Array.Clear(_drawSkip);
        int drawn = 0;
        var command = Device.GraphicsCommandBuffer;
        for (int i = 0; i < inputs.Count && i < MaxDecals; i++)
        {
            var draw = inputs.Draws[i];
            if (draw.Texture == null) continue;
            Texture? texture = null;
            lock (_textures) _textures.TryGetValue(draw.Texture, out texture);
            if (texture == null || !texture.Ready || texture.View.Handle == 0) { _drawSkip[i] = true; continue; }
            // The first time a texture is used, push it into a sample layout; Prepare is outside the Pass, so this does not take the deferred-transition path.
            texture.EnsureReadyForRendering(command);
            if (texture.CurrentLayout != ImageLayout.ShaderReadOnlyOptimal) { _drawSkip[i] = true; continue; }
            _drawTextures[i] = texture;
            drawn++;
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
        if (_frameCursors[index] == frameRing.Count) frameRing.Add(Device.DescriptorAllocator.AllocateSet(_frameLayout));
        var frameSet = frameRing[_frameCursors[index]++];

        // Texture ring: this frame needs drawn independent sets, plus one valid placeholder for "textureless decals".
        var textureRing = _textureSets[index];
        while (textureRing.Count < _textureCursors[index] + drawn + 1)
            textureRing.Add(Device.DescriptorAllocator.AllocateSet(_textureLayout));
        int cursor = _textureCursors[index];

        var bufferInfo = new DescriptorBufferInfo { Buffer = buffer.Buffer, Range = FrameBytes };
        var depthImage = new DescriptorImageInfo { ImageView = depth.DepthView, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        var writes = stackalloc WriteDescriptorSet[2];
        writes[0] = new() { SType = StructureType.WriteDescriptorSet, DstSet = frameSet, DstBinding = 0,
            DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &bufferInfo };
        writes[1] = new() { SType = StructureType.WriteDescriptorSet, DstSet = frameSet, DstBinding = 1,
            DescriptorCount = 1, DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &depthImage };
        Device.Vk.UpdateDescriptorSets(Device.LogicalDevice, 2, writes, 0, null);

        // A textureless decal still binds a valid view to the texture slot: wrap.w is a per-draw constant, so the shader never actually samples it,
        // but Vulkan requires statically used descriptors to be valid. The depth view is a free valid placeholder here (it was never an attachment anyway).
        _fallbackSet = textureRing[cursor++];
        var fallbackWrite = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = _fallbackSet,
            DstBinding = 0, DescriptorCount = 1, DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &depthImage };
        Device.Vk.UpdateDescriptorSets(Device.LogicalDevice, 1, &fallbackWrite, 0, null);

        for (int i = 0; i < inputs.Count && i < MaxDecals; i++)
        {
            var texture = _drawTextures[i];
            if (texture == null) { _drawSets[i] = _fallbackSet; continue; }
            var set = textureRing[cursor++];
            var image = new DescriptorImageInfo { ImageView = texture.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
            var write = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 0,
                DescriptorCount = 1, DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &image };
            Device.Vk.UpdateDescriptorSets(Device.LogicalDevice, 1, &write, 0, null);
            _drawSets[i] = set;
        }
        _textureCursors[index] = cursor;

        _inputs = inputs; _destination = target; _frameSet = frameSet;
        _buffer = buffer.Buffer; _command = command; _stamp = stamp; _frameIndex = Device.FrameIndex;
        _withNormal = normal != null;
        return true;
    }

    public override bool Draw()
    {
        var destination = _destination;
        var inputs = _inputs;
        _destination = null; _inputs = default;
        if (_disposed || destination == null || !Device.InRenderPass
            || Device.ActivePassId != Season.Rendering.RenderPassId.SceneDecal
            || !ReferenceEquals(Device.ActiveColorTarget, destination)
            || _withNormal != (Device.ActiveNormalTarget != null)
            || Device.GraphicsCommandBuffer.Handle != _command.Handle || Device.FrameIndex != _frameIndex
            || Device.GetCurrentRetireFenceValue() != _stamp) return false;
        var vk = Device.Vk;
        var command = _command;
        var layout = _layout;
        var frameSet = _frameSet;
        // Only accepts a matching attachment form: when roughness is not written, the normal must not be an attachment, and when it is written it must be the same RT.
        if (!Resolve(inputs, destination, out _, out _, out var normal, out var buffer)
            || buffer.Buffer.Handle != _buffer.Handle || (normal != null) != _withNormal) return false;

        vk.CmdBindDescriptorSets(command, PipelineBindPoint.Graphics, layout, 0, 1, &frameSet, 0, null);
        uint drawIndex = 0;
        var constants = stackalloc uint[4];
        constants[1] = 0u; constants[2] = 0u; constants[3] = 0u;
        for (int i = 0; i < inputs.Count && i < MaxDecals; i++)
        {
            if (_drawSkip[i]) continue;
            var draw = inputs.Draws[i];
            int mode = draw.Mode == DecalMode.Additive ? 1 : 0;
            vk.CmdBindPipeline(command, PipelineBindPoint.Graphics, _pipelines[mode, _withNormal ? 1 : 0]);
            var textureSet = _drawSets[i];
            vk.CmdBindDescriptorSets(command, PipelineBindPoint.Graphics, layout, 1, 1, &textureSet, 0, null);
            constants[0] = (uint)draw.Index;
            vk.CmdPushConstants(command, layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, 16, constants);
            vk.CmdDraw(command, 36, 1, 0, 0);
            drawIndex++;
        }
        return drawIndex > 0;
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
        var pipelines = new Silk.NET.Vulkan.Pipeline[4];
        int n = 0;
        for (int normal = 0; normal < 2; normal++)
            for (int mode = 0; mode < 2; mode++) pipelines[n++] = _pipelines[mode, normal];
        Array.Clear(_pipelines);
        Array.Clear(_drawTextures);
        Array.Clear(_drawSkip);
        Array.Clear(_drawSets);
        Device.EnqueueDeferredRelease(() =>
        {
            foreach (var ring in _frameSets) foreach (var set in ring) allocator.FreeSet(set);
            foreach (var ring in _textureSets) foreach (var set in ring) allocator.FreeSet(set);
            foreach (var pipeline in pipelines)
                if (pipeline.Handle != 0) vk.DestroyPipeline(device, pipeline, null);
            if (layout.Handle != 0) vk.DestroyPipelineLayout(device, layout, null);
            if (frameLayout.Handle != 0) vk.DestroyDescriptorSetLayout(device, frameLayout, null);
            if (textureLayout.Handle != 0) vk.DestroyDescriptorSetLayout(device, textureLayout, null);
        });
    }
}
