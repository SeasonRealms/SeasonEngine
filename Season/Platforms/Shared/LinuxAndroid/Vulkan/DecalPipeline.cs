// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Numerics;
using Silk.NET.Vulkan;
using Season.Rendering;

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

/// <summary>
/// D1 屏幕空间贴花的 Vulkan 实现，结构与 <see cref="VKFogCompositePipeline"/> 同构：
/// Prepare 在 Pass 外解析资源并把深度留在采样布局，Draw 只能在
/// <see cref="RenderPassId.SceneDecal"/> 内消费一次。
///
/// <para>与 DX 版逐条对齐的三处：</para>
/// <list type="bullet">
/// <item>每贴花一次 36 顶点 <c>vkCmdDraw</c>，顶点由 <c>gl_VertexIndex</c> 还原，没有任何顶点缓冲；</item>
/// <item>光栅只留背面（<c>CullMode = Front</c>）。负高度视口与 DX 的屏幕 Y 翻转互相抵消，
/// 所以两端对同一个盒面得到同一个「正面」判断；</item>
/// <item>混合走固定功能：Modulate 是 <c>DST_COLOR / ZERO</c>，Additive 是 <c>ONE / ONE</c>，
/// 两者的 alpha 都是 <c>ZERO / ONE</c>（保留目标 alpha）。</item>
/// </list>
///
/// <para>粗糙度写入（s9）用第二个颜色附件，写掩码只开 B 通道，混合为 <c>SRC_ALPHA / ONE_MINUS_SRC_ALPHA</c>，
/// 因此附件里的八面体法线（.xy）与反射权重（.w）保持原值。该形态的 render pass 与不写粗糙度的形态
/// 附件数量不同、互不兼容，所以各烘一套 PSO。</para>
/// </summary>
internal sealed unsafe class VKDecalPipeline : Season.Rendering.DecalPipeline
{
    const int MaxDecals = Season.Rendering.DecalMath.MaxDecals;
    const ulong FrameBytes = Season.Rendering.DecalMath.ByteCount;

    readonly Dictionary<string, Texture> _textures;

    // 描述符按帧槽轮转：每个槽各自一条 set 环，槽内游标在帧号盖戳变化时归零。
    readonly List<DescriptorSet>[] _frameSets = new List<DescriptorSet>[(int)Device.frameCount];
    readonly int[] _frameCursors = new int[(int)Device.frameCount];
    readonly List<DescriptorSet>[] _textureSets = new List<DescriptorSet>[(int)Device.frameCount];
    readonly int[] _textureCursors = new int[(int)Device.frameCount];
    readonly ulong[] _stamps = new ulong[(int)Device.frameCount];

    // 逐 draw 的贴图解析结果。Prepare 一次算好，Draw 只消费。
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
        if (point.Handle == 0 || linear.Handle == 0) throw new InvalidOperationException("贴花采样器尚未初始化。");

        var bindings = stackalloc DescriptorSetLayoutBinding[2];
        bindings[0] = new() { Binding = 0, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit,
            DescriptorType = DescriptorType.StorageBuffer };
        bindings[1] = new() { Binding = 1, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit,
            DescriptorType = DescriptorType.CombinedImageSampler, PImmutableSamplers = &point };
        var frameInfo = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 2, PBindings = bindings };
        if (vk.CreateDescriptorSetLayout(device, in frameInfo, null, out _frameLayout) != Result.Success)
            throw new InvalidOperationException("创建贴花描述符布局失败。");

        var textureBinding = new DescriptorSetLayoutBinding { Binding = 0, DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit, DescriptorType = DescriptorType.CombinedImageSampler,
            PImmutableSamplers = &linear };
        var textureInfo = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1, PBindings = &textureBinding };
        if (vk.CreateDescriptorSetLayout(device, in textureInfo, null, out _textureLayout) != Result.Success)
            throw new InvalidOperationException("创建贴花贴图描述符布局失败。");

        var setLayouts = stackalloc DescriptorSetLayout[2] { _frameLayout, _textureLayout };
        // 顶点端要用贴花索引取矩阵，像素端要用索引取整块数据，所以两段都要可见。
        var push = new PushConstantRange { StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, Size = 16 };
        var layoutInfo = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 2, PSetLayouts = setLayouts, PushConstantRangeCount = 1, PPushConstantRanges = &push };
        if (vk.CreatePipelineLayout(device, in layoutInfo, null, out _layout) != Result.Success)
            throw new InvalidOperationException("创建贴花管线布局失败。");

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
            // CullMode.Front 只留背面：盒的六个面从盒外看逆时针，背面恰好是「每个像素命中一次」的那一侧。
            // 深度不是附件，所以深度测试与写入必须关掉。
            var raster = new PipelineRasterizationStateCreateInfo { SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill, CullMode = CullModeFlags.FrontBit, FrontFace = FrontFace.Clockwise, LineWidth = 1 };
            var samples = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit };

            var blends = stackalloc PipelineColorBlendAttachmentState[2];
            // RT0：Modulate = dst * src，Additive = dst + src；alpha 两个模式都保留目标值。
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
            // RT1（仅写粗糙度形态）：只开 B 通道，用源 alpha 插值，源 alpha 为 0 时逐位保持原值。
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
                throw new InvalidOperationException("创建贴花图形管线失败。");
            return pipeline;
        }
    }

    /// <summary>
    /// 校验资源形状并定下本帧的附件形态。与雾合成一样只接受生产者已经写入并转入采样布局的资源，
    /// 不在 Pass 内补发 barrier：颜色与法线的布局切换由 render pass 的 initial/final layout 完成，
    /// 深度保持上一段场景 Pass 发布的 ShaderReadOnlyOptimal。
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
            // 贴图首次被用到时把它推进采样布局；Prepare 在 Pass 外，所以这里不会走延迟转换那条路。
            texture.EnsureReadyForRendering(command);
            if (texture.CurrentLayout != ImageLayout.ShaderReadOnlyOptimal) { _drawSkip[i] = true; continue; }
            _drawTextures[i] = texture;
            drawn++;
        }

        int index = (int)Device.FrameIndex;
        ulong stamp = Device.GetCurrentRetireFenceValue();
        if (_stamps[index] != stamp)
        {
            // 本帧第一次 Prepare 会复用三帧前那一批 set，它们可能仍被 pending 命令缓冲引用。
            Device.WaitForFrameSlot(index);
            _stamps[index] = stamp;
            _frameCursors[index] = 0;
            _textureCursors[index] = 0;
        }

        var frameRing = _frameSets[index];
        if (_frameCursors[index] == frameRing.Count) frameRing.Add(Device.DescriptorAllocator.AllocateSet(_frameLayout));
        var frameSet = frameRing[_frameCursors[index]++];

        // 贴图环：本帧需要 drawn 个独立 set，外加一个给「无纹理贴花」的合法占位。
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

        // 无纹理贴花仍然会把贴图槽绑上一个合法视图：wrap.w 是逐 draw 常量，着色器不会真的采样，
        // 但 Vulkan 要求静态使用的描述符必须有效。深度视图在这里是免费的合法占位（它本来就不是附件）。
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
        // 只接受匹配的附件形态：不写粗糙度时法线不能是附件，写粗糙度时必须是同一个 RT。
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
