// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Vulkan;
using Season.Rendering;
using Season.Rendering.Effects;

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

/// <summary>
/// P2 水面材质的 Vulkan 实现。骨架与 <see cref="VKDecalPipeline"/> 同构（Pass 外 Prepare、
/// Pass 内只消费一次），差别集中在三处：
/// <list type="bullet">
/// <item>几何由 <c>gl_VertexIndex</c> 展开（P3 是 6 顶点，P4 起是 tile 内的 subdiv² 网格），
/// <c>gl_InstanceIndex</c> 就是 tile 索引，世界矩阵与分块参数都装在帧缓冲/推送常量里，没有顶点缓冲；</item>
/// <item>本 Pass 不挂深度附件，SceneDepth 保持上一段场景 Pass 发布的 <c>ShaderReadOnlyOptimal</c>，
/// 遮挡靠着色器里的软件深度测试；水面的 render pass 因此与「不写法线」的贴花完全同形；</item>
/// <item>多一张折射快照（开画前的场景色拷贝）。它在 Pass 外由 <see cref="Vk"/> 侧的
/// <c>CopyRenderTarget</c> 拷好并转入采样布局 —— 本 Pass 的颜色附件不能同时被采样。</item>
/// </list>
///
/// 混合走固定功能 <c>SRC_ALPHA / ONE_MINUS_SRC_ALPHA</c>，与原透明几何一致；
/// 双面渲染（<c>CullMode = None</c>）让水面从水下也看得见，掠射角很大时也不会整块消失。
/// </summary>
internal sealed unsafe class VKWaterPipeline : Season.Rendering.WaterPipeline
{
    const int MaxWaters = WaterMath.MaxWaters;
    const ulong FrameBytes = WaterMath.ByteCount;

    readonly Dictionary<string, Texture> _textures;

    // 描述符按帧槽轮转：每个槽各自一条 set 环，槽内游标在帧号盖戳变化时归零。
    readonly List<DescriptorSet>[] _frameSets = new List<DescriptorSet>[(int)Device.frameCount];
    readonly int[] _frameCursors = new int[(int)Device.frameCount];
    readonly ulong[] _stamps = new ulong[(int)Device.frameCount];

    // 逐 draw 的法线贴图解析结果与描述符集。Prepare 一次算好，Draw 只消费。
    readonly Texture?[] _drawNormals = new Texture?[MaxWaters];
    readonly DescriptorSet[] _drawSets = new DescriptorSet[MaxWaters];
    /// <summary>本帧的 Hi-Z 四级金字塔；<see cref="HiZEffect.Ready"/> 为假时整组为空，绑占位。</summary>
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
        if (point.Handle == 0 || linear.Handle == 0) throw new InvalidOperationException("水面采样器尚未初始化。");

        // 九槽挤在一个 set 里：深度、折射、法线、天空与 Hi-Z 四级都是「每帧最多换一次」的资源，
        // 拆成独立 set 只会多一次绑定调用，而且贴花那种「一张贴图一个 set」的写法在这里不适用
        // （一张图一个不可变采样器，几种图却要两种采样约束）。
        var bindings = stackalloc DescriptorSetLayoutBinding[9];
        bindings[0] = new() { Binding = 0, DescriptorCount = 1,
            StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            DescriptorType = DescriptorType.StorageBuffer };
        bindings[1] = new() { Binding = 1, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit,
            DescriptorType = DescriptorType.CombinedImageSampler, PImmutableSamplers = &point };
        // binding 2 起全部是线性过滤的采样纹理：折射、法线、天空 LUT，以及 P3 的 Hi-Z 四级。
        for (int i = 2; i < 9; i++)
            bindings[i] = new() { Binding = (uint)i, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit,
                DescriptorType = DescriptorType.CombinedImageSampler, PImmutableSamplers = &linear };
        var frameInfo = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 9, PBindings = bindings };
        if (vk.CreateDescriptorSetLayout(device, in frameInfo, null, out _frameLayout) != Result.Success)
            throw new InvalidOperationException("创建水面描述符布局失败。");

        // 顶点端要用槽号取世界矩阵，像素端要用槽号取材质与环境参数，所以两段都要看到 push constant。
        var push = new PushConstantRange { StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, Size = 16 };
        var setLayouts = stackalloc DescriptorSetLayout[1] { _frameLayout };
        var layoutInfo = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1, PSetLayouts = setLayouts, PushConstantRangeCount = 1, PPushConstantRanges = &push };
        if (vk.CreatePipelineLayout(device, in layoutInfo, null, out _layout) != Result.Success)
            throw new InvalidOperationException("创建水面管线布局失败。");

        ShaderModule vertex = default, pixel = default;
        try
        {
            // glslang 的简化 C 接口只认 main，所以顶点与片元是两份独立源码，各自拼同一段帧缓冲访问器。
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
            // 双面：水面从水下也应该看得到，俯仰角很大时背面剔除会让水面整块消失。
            // 深度不是附件（遮挡在片元里用软件比较），所以深度测试与写入都不参与管线状态。
            var raster = new PipelineRasterizationStateCreateInfo { SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill, CullMode = CullModeFlags.None, FrontFace = FrontFace.Clockwise,
                LineWidth = 1, DepthClampEnable = false };
            var samples = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit };
            var blends = stackalloc PipelineColorBlendAttachmentState[1];
            // 着色器输出非预乘色，所以用标准 alpha 混合；alpha 槽保留目标值。
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
                throw new InvalidOperationException("创建水面图形管线失败。");
            return pipeline;
        }
    }

    /// <summary>
    /// 校验资源形状。与贴花一样只接受生产者已经写入并转入采样布局的资源，不在 Pass 内补发 barrier：
    /// 颜色附件的布局切换由 render pass 的 initial/finalLayout 完成，深度保持场景 Pass 发布的
    /// <c>ShaderReadOnlyOptimal</c>，折射快照由拷贝那一侧转入采样布局。
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
            // 本 Pass 把颜色目标当作混合底图，共享层因此必须交出同一张目标。
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

        // 逐水面的法线贴图：只查字典，不加载。名字未注册或还没上传完的水面退回程序化波浪。
        Array.Clear(_drawNormals);
        for (int i = 0; i < inputs.Count && i < MaxWaters; i++)
        {
            string? name = inputs.Draws[i].Texture;
            if (name == null) continue;
            Texture? texture;
            lock (_textures) _textures.TryGetValue(name, out texture);
            if (texture == null || !texture.Ready || texture.View.Handle == 0) continue;
            // 贴图首次被用到时把它推进采样布局；Prepare 在 Pass 外，所以这里不会走延迟转换那条路。
            texture.EnsureReadyForRendering(command);
            if (texture.CurrentLayout == ImageLayout.ShaderReadOnlyOptimal) _drawNormals[i] = texture;
        }

        int index = (int)Device.FrameIndex;
        ulong stamp = Device.GetCurrentRetireFenceValue();
        if (_stamps[index] != stamp)
        {
            // 本帧第一次 Prepare 会复用三帧前那一批 set，它们可能仍被 pending 命令缓冲引用。
            Device.WaitForFrameSlot(index);
            _stamps[index] = stamp;
            _frameCursors[index] = 0;
        }
        var ring = _frameSets[index];
        // 法线贴图是逐水面的资源，所以每个水面一份自己的 set（与贴花每贴花一份 set 同理），
        // 换个写法就得把不能共存的贴图塞进同一个 set，到头来只能退化成同屏统一无贴图。
        while (ring.Count < _frameCursors[index] + inputs.Count)
            ring.Add(Device.DescriptorAllocator.AllocateSet(_frameLayout));
        int cursor = _frameCursors[index];

        // 天空 LUT 是 P0 建的那张 compute 纹理：名字没注册（StaticCube 档）时退回 1×1 白，
        // 着色器侧的 misc.w 门控保证它不会被真正采样。
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

        // 占位图必须是合法视图且处在采样布局：白纹理优先，拿不到就用深度视图顶位 ——
        // 后者本来就不是附件，而且着色器的两个门控保证它不会被真正采样。
        var white = Device.White;
        if (white != null && white.Ready && white.View.Handle != 0)
        {
            white.EnsureReadyForRendering(command);
            if (white.CurrentLayout != ImageLayout.ShaderReadOnlyOptimal) white = null;
        }
        var placeholder = white?.View ?? depth.DepthView;

        // P3 Hi-Z：与天空 LUT 同一套拿法，只是四张而不是一张。任一级不成就整组退回，
        // 与着色器侧的 frame[12].x 门控同源（两边都以 HiZEffect.Ready 为准）。
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
        // 着色器侧是 uint4：槽位 + 分块网格参数（tilesX, tilesZ, subdiv），必须按整数位写。
        // 不分块的水面三者是 (1,1,0)，顶点着色器据此退回单 quad。
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
            // P4：分块实例化。tile 索引走 gl_InstanceIndex（firstInstance 保持 0），
            // 槽位仍由推送常量携带 —— 只有 WebGPU 需要把两者挤进同一个实例号。
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
