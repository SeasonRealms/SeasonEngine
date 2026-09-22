// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

[StructLayout(LayoutKind.Sequential)]
internal struct Draw2DConstants
{
    public Vector4 OriginXAxis, YAxis, Uv, Color, Clip, Parameters, UvClamp;
}

/// <summary>Overlay-only quads. Immutable descriptors and per-draw push constants protect in-flight frames.</summary>
internal sealed unsafe class Draw2DPipeline : IDisposable
{
    DescriptorAllocator? _allocator;
    DescriptorSetLayout _setLayout;
    PipelineLayout _layout;
    VkPipeline _pipeline;
    Sampler _linear, _point;
    readonly Dictionary<Texture, (ulong Version, DescriptorSet Set)> _sets = new();
    readonly HashSet<Texture> _used = new();

    const string Parameters = """
        layout(push_constant) uniform DrawParameters {
            vec4 originXAxis; vec4 yAxis; vec4 uvRect; vec4 tint;
            vec4 clipRect; vec4 parameters; vec4 uvClamp;
        } p;
        """;
    internal static readonly string VertexSource = "#version 450\n" + Parameters + """

        layout(location=0) out vec2 vUv;
        void main() {
            vec2 corner = vec2(gl_VertexIndex & 1, gl_VertexIndex >> 1);
            gl_Position = vec4(p.originXAxis.xy + corner.x * p.originXAxis.zw + corner.y * p.yAxis.xy, 0, 1);
            vUv = p.uvRect.xy + corner * p.uvRect.zw;
        }
        """;
    internal static readonly string FragmentSource = "#version 450\n" + Parameters + """

        layout(set=0, binding=0) uniform sampler2D linearImage;
        layout(set=0, binding=1) uniform sampler2D pointImage;
        layout(location=0) in vec2 vUv;
        layout(location=0) out vec4 outputColor;
        void main() {
            vec2 screenTextureSize = 1.0 / max(fwidth(vUv), vec2(1e-5));
            vec2 uv = clamp(vUv, p.uvClamp.xy, p.uvClamp.zw);
            vec4 sampled = p.parameters.x > 0.5 ? texture(pointImage, uv) : texture(linearImage, uv);
            if (any(lessThan(gl_FragCoord.xy, p.clipRect.xy)) ||
                any(greaterThanEqual(gl_FragCoord.xy, p.clipRect.zw))) discard;
            if (p.parameters.y > 0) {
                float median = max(min(sampled.r, sampled.g), min(max(sampled.r, sampled.g), sampled.b));
                float msdfDistance = median - 0.5;
                float trueDistance = sampled.a - 0.5;
                float distance = msdfDistance * trueDistance > 0 ? msdfDistance : trueDistance;
                float range = max(0.5 * dot(p.parameters.y * p.parameters.zw, screenTextureSize), 1.0);
                outputColor = vec4(p.tint.rgb, p.tint.a * clamp(range * distance + 0.5, 0, 1));
            } else outputColor = sampled * p.tint;
        }
        """;

    internal Draw2DPipeline()
    {
        try
        {
            Device.Vk.GetPhysicalDeviceProperties(Device.PhysicalDevice, out var properties);
            if (sizeof(Draw2DConstants) != 112 || properties.Limits.MaxPushConstantsSize < 112)
                throw new NotSupportedException("Immediate 2D requires 112 bytes of push constants.");
            _allocator = new DescriptorAllocator(Device.Vk, Device.LogicalDevice);
            _linear = CreateSampler(Filter.Linear);
            _point = CreateSampler(Filter.Nearest);
            var linear = _linear;
            var point = _point;
            var bindings = stackalloc DescriptorSetLayoutBinding[2];
            bindings[0] = new(0, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit, &linear);
            bindings[1] = new(1, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit, &point);
            var setInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 2, PBindings = bindings
            };
            Device.CheckResult(Device.Vk.CreateDescriptorSetLayout(Device.LogicalDevice, in setInfo, null, out _setLayout));
            var setLayout = _setLayout;
            var range = new PushConstantRange(ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, 112);
            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &setLayout,
                PushConstantRangeCount = 1, PPushConstantRanges = &range
            };
            Device.CheckResult(Device.Vk.CreatePipelineLayout(Device.LogicalDevice, in layoutInfo, null, out _layout));
            CreatePipeline();
        }
        catch { Dispose(); throw; }
    }

    static Sampler CreateSampler(Filter filter)
    {
        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo, MinFilter = filter, MagFilter = filter,
            MipmapMode = SamplerMipmapMode.Nearest, AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge, AddressModeW = SamplerAddressMode.ClampToEdge,
            MaxLod = 0
        };
        Device.CheckResult(Device.Vk.CreateSampler(Device.LogicalDevice, in info, null, out var sampler));
        return sampler;
    }

    void CreatePipeline()
    {
        ShaderModule vs = default, fs = default;
        var entry = SilkMarshal.StringToPtr("main");
        try
        {
            vs = ShaderCompiler.CreateShaderModule(Device.Vk, Device.LogicalDevice, VertexSource,
                ShaderStageFlags.VertexBit, "main", "immediate2d.vert", false);
            fs = ShaderCompiler.CreateShaderModule(Device.Vk, Device.LogicalDevice, FragmentSource,
                ShaderStageFlags.FragmentBit, "main", "immediate2d.frag", false);
            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new() { SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit, Module = vs, PName = (byte*)entry };
            stages[1] = new() { SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit, Module = fs, PName = (byte*)entry };
            var vertex = new PipelineVertexInputStateCreateInfo { SType = StructureType.PipelineVertexInputStateCreateInfo };
            var assembly = new PipelineInputAssemblyStateCreateInfo
                { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleStrip };
            var viewport = new PipelineViewportStateCreateInfo
                { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
            var raster = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo, PolygonMode = PolygonMode.Fill,
                CullMode = CullModeFlags.None, FrontFace = FrontFace.Clockwise, LineWidth = 1
            };
            var multisample = new PipelineMultisampleStateCreateInfo
                { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit };
            var depth = new PipelineDepthStencilStateCreateInfo
                { SType = StructureType.PipelineDepthStencilStateCreateInfo, DepthCompareOp = CompareOp.Always };
            var attachment = new PipelineColorBlendAttachmentState
            {
                BlendEnable = true, SrcColorBlendFactor = BlendFactor.SrcAlpha,
                DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha, ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One, DstAlphaBlendFactor = BlendFactor.Zero, AlphaBlendOp = BlendOp.Add,
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit
            };
            var blend = new PipelineColorBlendStateCreateInfo
                { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &attachment };
            var states = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
            var dynamic = new PipelineDynamicStateCreateInfo
                { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = states };
            var info = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo, StageCount = 2, PStages = stages,
                PVertexInputState = &vertex, PInputAssemblyState = &assembly, PViewportState = &viewport,
                PRasterizationState = &raster, PMultisampleState = &multisample, PDepthStencilState = &depth,
                PColorBlendState = &blend, PDynamicState = &dynamic, Layout = _layout,
                RenderPass = Device.Display.RenderPass, Subpass = 0
            };
            Device.CheckResult(Device.Vk.CreateGraphicsPipelines(Device.LogicalDevice, default, 1, in info, null, out _pipeline));
        }
        finally
        {
            if (vs.Handle != 0) Device.Vk.DestroyShaderModule(Device.LogicalDevice, vs, null);
            if (fs.Handle != 0) Device.Vk.DestroyShaderModule(Device.LogicalDevice, fs, null);
            SilkMarshal.Free(entry);
        }
    }

    internal DescriptorSet Prepare(Texture texture)
    {
        _used.Add(texture);
        if (_sets.TryGetValue(texture, out var existing))
        {
            if (existing.Version == texture.ViewVersion) return existing.Set;
            Retire(existing.Set);
            _sets.Remove(texture);
        }
        var set = _allocator!.AllocateSet(_setLayout);
        var image = new DescriptorImageInfo { ImageView = texture.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        var writes = stackalloc WriteDescriptorSet[2];
        for (uint i = 0; i < 2; i++)
            writes[i] = new() { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = i,
                DescriptorCount = 1, DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &image };
        Device.Vk.UpdateDescriptorSets(Device.LogicalDevice, 2, writes, 0, null);
        _sets.Add(texture, (texture.ViewVersion, set));
        return set;
    }

    internal void Draw(DescriptorSet set, Draw2DConstants constants)
    {
        var cmd = Device.GraphicsCommandBuffer;
        Device.Vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipeline);
        Device.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _layout, 0, 1, in set, 0, null);
        Device.Vk.CmdPushConstants(cmd, _layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, 112, &constants);
        Device.Vk.CmdDraw(cmd, 4, 1, 0, 0);
    }

    internal void CompleteFrame()
    {
        foreach (var texture in _sets.Keys.Where(t => !_used.Contains(t)).ToArray())
        {
            Retire(_sets[texture].Set);
            _sets.Remove(texture);
        }
        _used.Clear();
    }

    void Retire(DescriptorSet set)
    {
        var allocator = _allocator!;
        Device.EnqueueDeferredRelease(() => allocator.FreeSet(set));
    }

    /// <summary>Only after GPU completion and earlier descriptor retire callbacks.</summary>
    public void Dispose()
    {
        _sets.Clear();
        _used.Clear();
        _allocator?.Dispose();
        _allocator = null;
        if (_pipeline.Handle != 0) Device.Vk.DestroyPipeline(Device.LogicalDevice, _pipeline, null);
        if (_layout.Handle != 0) Device.Vk.DestroyPipelineLayout(Device.LogicalDevice, _layout, null);
        if (_setLayout.Handle != 0) Device.Vk.DestroyDescriptorSetLayout(Device.LogicalDevice, _setLayout, null);
        if (_linear.Handle != 0) Device.Vk.DestroySampler(Device.LogicalDevice, _linear, null);
        if (_point.Handle != 0) Device.Vk.DestroySampler(Device.LogicalDevice, _point, null);
        _pipeline = default; _layout = default; _setLayout = default; _linear = _point = default;
    }
}
