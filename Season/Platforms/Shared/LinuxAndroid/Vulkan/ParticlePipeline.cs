// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Vulkan;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

/// <summary>
/// A0.1 的 Vulkan 提交端：一次 vkCmdDrawIndexedIndirect 画出 compute 决定数量的公告板。
///
/// 和 DXParticleDrawPipeline 的差别只有两处，都是 Vulkan 固有的：
/// 一是需要描述符，于是照 VKFogCompositePipeline 的按帧描述符环管理 set，而 D3D12 用 root SRV 直吃
/// GPU 虚拟地址，连堆都不需要；二是 Prepare 里一道 barrier 都不录，因为 TryDispatchCompute 尾部已经
/// 为每个 ReadWrite 绑定发过 ShaderWrite -> ShaderRead（dstStage 含 VertexShaderBit），参数缓冲的
/// IndirectCommandRead 又由 IGraphics.TryPrepareIndirectArgs 单独补过。Prepare 因此只做解析与校验。
///
/// PSO 不在构造期烘焙而是在 Draw 里按需烘焙，理由与 D3D12 一致：决定附件形状的事实只在进入 Pass
/// 之后才成立，而 Prepare 必须在 Pass 之外。创建 PSO 是设备调用不是命令录制，在 Pass 内合法。
///
/// Vulkan 这边"附件形状"比 D3D12 多一维。D3D12 只需区分有没有速度槽，而这里 Scene Pass 在
/// Device.BeginPass 下有三种真实形状：颜色+速度+深度（三目标）、颜色+深度（双目标或 RT 自带 RP）、
/// 以及只有颜色（RT 声明了 NoImplicitDepth，自带 RP 就是 colorOnly）。最后一种下开深度测试是非法的，
/// 所以形状必须从实际绑定反推而不是假定。采样数不进 key：上述三条 RP 全部硬编码 Count1Bit，
/// 于是非单采样目标一律拒绝提交，而不是烘一个必然不匹配的 PSO。
/// </summary>
internal sealed unsafe class VKParticleDrawPipeline : Season.Rendering.ParticleDrawPipeline
{
    /// <summary>描述符槽序，与 ParticleSystemEffect.DrawVertexGlsl 的 binding 0/1 一一对应。</summary>
    const uint InstancesBinding = 0;
    const uint FrameBinding = 1;

    /// <summary>单位四边形索引只有 0..3，ResourceManager.CreateIndexBuffer 因此把它存成 16 位。</summary>
    const ulong QuadIndexBytes = Season.Rendering.ParticleDraw.QuadIndexCount * sizeof(ushort);

    /// <summary>当前 Pass 的真实附件形状，决定 PSO 的附件数与深度状态。</summary>
    enum Attachments { ColorOnly, ColorDepth, ColorVelocityDepth, ColorVelocityNormalDepth }

    /// <summary>PSO 签名。采样数不在其中，见类注释。</summary>
    readonly record struct PipelineKey(Format Color, Attachments Shape);

    readonly Dictionary<PipelineKey, (VkPipeline Pipeline, RenderPass Anchor)> _pipelines = [];
    readonly List<DescriptorSet>[] _sets = new List<DescriptorSet>[(int)Device.frameCount];
    readonly int[] _cursors = new int[(int)Device.frameCount];
    readonly ulong[] _stamps = new ulong[(int)Device.frameCount];
    DescriptorSetLayout _setLayout;
    PipelineLayout _layout;

    // Prepare 的产物，Draw 消费且只消费一次。
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
        // 两个只读 storage buffer，仅顶点阶段可见；没有 push constant，所以 binding 0 是空闲的，
        // 而 compute 侧那套 binding 从 1 起算是因为 0 号位被 Params 的 push constant 占掉了语义。
        var bindings = stackalloc DescriptorSetLayoutBinding[2];
        bindings[0] = new() { Binding = InstancesBinding, DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageBuffer, StageFlags = ShaderStageFlags.VertexBit };
        bindings[1] = new() { Binding = FrameBinding, DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageBuffer, StageFlags = ShaderStageFlags.VertexBit };
        var setInfo = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 2, PBindings = bindings };
        if (vk.CreateDescriptorSetLayout(device, in setInfo, null, out _setLayout) != Result.Success)
            throw new InvalidOperationException("创建粒子描述符布局失败。");
        var setLayout = _setLayout;
        var layoutInfo = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1, PSetLayouts = &setLayout };
        if (vk.CreatePipelineLayout(device, in layoutInfo, null, out _layout) != Result.Success)
            throw new InvalidOperationException("创建粒子管线布局失败。");
    }

    VkPipeline GetOrCreatePipeline(in PipelineKey key)
    {
        if (_pipelines.TryGetValue(key, out var cached)) return cached.Pipeline;
        var vk = Device.Vk; var device = Device.LogicalDevice;
        bool normal = key.Shape == Attachments.ColorVelocityNormalDepth;
        bool velocity = normal || key.Shape == Attachments.ColorVelocityDepth;
        bool depth = key.Shape != Attachments.ColorOnly;

        // PSO 只要求与实际 render pass "兼容"，也就是附件的格式、采样数和 subpass 结构一致；
        // LoadOp/StoreOp/layout 不参与判定。所以这里自造同构的锚点 RP 来烘焙，与 VKFogCompositePipeline
        // 和 Pipeline.Init 的 HDR 锚点同一手法，不需要去借 Pass 正在用的那个句柄。
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
                // 没有顶点缓冲也没有输入布局：角点来自 gl_VertexIndex，实例值来自 storage buffer。
                var input = new PipelineVertexInputStateCreateInfo { SType = StructureType.PipelineVertexInputStateCreateInfo };
                var assembly = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                    Topology = PrimitiveTopology.TriangleList };
                var viewportState = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo,
                    ViewportCount = 1, ScissorCount = 1 };
                var raster = new PipelineRasterizationStateCreateInfo { SType = StructureType.PipelineRasterizationStateCreateInfo,
                    PolygonMode = PolygonMode.Fill,
                    // 公告板的绕序取决于相机基与索引顺序的组合，关掉剔除比在着色器里翻正负更可靠。
                    CullMode = CullModeFlags.None, FrontFace = FrontFace.Clockwise, LineWidth = 1 };
                var samples = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo,
                    RasterizationSamples = SampleCountFlags.Count1Bit };
                // 深度测试开、深度写关：粒子互相之间不排序，写深度只会让后画的被先画的挡掉。
                // 场景深度是正向的（Pipeline 主管线用 Less/LessOrEqual），所以这里是 LessOrEqual。
                var depthState = new PipelineDepthStencilStateCreateInfo { SType = StructureType.PipelineDepthStencilStateCreateInfo,
                    DepthTestEnable = true, DepthWriteEnable = false, DepthCompareOp = CompareOp.LessOrEqual };
                var all = ColorComponentFlags.RBit | ColorComponentFlags.GBit
                    | ColorComponentFlags.BBit | ColorComponentFlags.ABit;
                var attachmentBlends = stackalloc PipelineColorBlendAttachmentState[3];
                // 片元着色器输出预乘 alpha，所以源因子是 One 而不是 SrcAlpha。
                attachmentBlends[0] = new() { BlendEnable = true,
                    SrcColorBlendFactor = BlendFactor.One, DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
                    ColorBlendOp = BlendOp.Add,
                    SrcAlphaBlendFactor = BlendFactor.One, DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
                    AlphaBlendOp = BlendOp.Add, ColorWriteMask = all };
                // 速度槽写掩码置零，与主管线对透明几何的既有选择一致：保留下方不透明层的速度，
                // 比写 0 更好，后者会让 TAA 把运动的粒子当成静止背景去累积。
                attachmentBlends[1] = new() { BlendEnable = false, ColorWriteMask = 0 };
                // 法线槽同理：粒子不是 SSR/贴花要看到的表面，保留下方不透明层的法线。
                // 写掩码为零的槽不要求片元着色器有对应输出，所以着色器沿用速度版本。
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
                    throw new InvalidOperationException("创建粒子图形管线失败。");
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
        // InRenderPass 为真时录制任何东西都违反 PassDesc 契约，宁可放弃本帧提交。
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
            // 本帧第一次 Prepare 会复用 ring[0]，也就是同一槽三帧前用过的那个 set。
            // 那批提交可能仍在 GPU 上，而未声明 UPDATE_AFTER_BIND 的 set 被 pending
            // 命令缓冲引用时改写会让那条缓冲整体失效；见 Device.WaitForFrameSlot。
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
        // Vulkan 的 Scene Pass 没有 MSAA 替身，所以身份核对可以是严格的引用相等。
        if (!ReferenceEquals(Device.ActiveColorTarget, color)
            || color.ColorViewVersion != _colorViewVersion) return false;
        // null == 使用该目标自带的深度，这同样要求准备期看到的是 null，否则深度语义已经变了。
        if (!ReferenceEquals(Device.ActiveDepthTarget, depth)) return false;

        // 附件形状只能从实际绑定反推：速度槽存在即三目标（法线也在则四目标）；否则有显式深度就是双目标；
        // 两者都没有时用的是目标自带的 RP，它是否带深度由 NoImplicitDepth 决定。
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
        // 主管线每次 SetPipeline 都会重绑 PSO、描述符和索引缓冲，所以这里的覆写不会泄漏到后续绘制。
        vk.CmdBindPipeline(_command, PipelineBindPoint.Graphics, pipeline);
        vk.CmdBindDescriptorSets(_command, PipelineBindPoint.Graphics, _layout, 0, 1, &set, 0, null);
        vk.CmdBindIndexBuffer(_command, Pipeline.UnitQuadIndexBuffer.Buffer, 0, IndexType.Uint16);
        // 实例数来自参数缓冲的第二个字段，CPU 这一侧从未知道过它的值。
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
