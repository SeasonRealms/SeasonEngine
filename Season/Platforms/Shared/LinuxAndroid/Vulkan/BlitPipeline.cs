// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Vulkan;
using Silk.NET.Core.Native;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

/// <summary>
/// Full-screen triangle pipeline for FinalBlit: offscreen RT to backbuffer.
/// Aligned with the DX-side BlitPipeline: no vertex input, no depth testing, and a full-screen triangle driven by SV_VertexID.
/// Two sampling variants exist for step 3: point, the default 1:1 path, and linear for fractional-resolution upsampling.
/// In step B of 1-4, the two tonemap variants, point and linear, close the full exposure to ACES, Narkowicz, to gamma chain.
/// The HDR path, where the source RT is Rgba16Float, is selected automatically by Device.BlitToBackbuffer from the source format.
/// Exposure is pushed every frame through push constants from Device.HdrExposure, matching the same chain on DX, Metal, and WebGPU.
/// Step D of 2-1, aligned with DX 2-1 step B and C:
/// - tonemap plus bloom has two variants. Bloom is added in linear space before ACES from a bloom texture on set=1,
///   reusing the second set layout and always sampled linearly for upsampling from the half-resolution chain output,
///   then scaled by BloomIntensity, the second push-constant component.
/// - the two uber variants, used by the Post pass, compose tonemap plus optional bloom into LDR PostColor and bake luma into alpha.
/// - the FXAA variant, used by FinalBlit, reads PostColor, reuses alpha as luma to avoid recomputation,
///   and takes texel size from the third and fourth push-constant components.
///   Contract constants are ported literally from the DX reference implementation. See the 1-4 contract-1 revision in the RenderQuality class header.
/// Step C of 2-2, aligned with DX 2-2 step B:
/// - six AO variants exist, tonemap with and without bloom times point and linear, plus uber with and without bloom.
///   AO is multiplied in linear space before ACES and bloom is added afterward,
///   scene times mix(1, ao, aoIntensity) plus bloom times bloomIntensity,
///   so AO darkens only the scene and not bloom.
///   The AO texture lives on set=2, reusing the third set layout and always sampled linearly from the half-resolution GTAO r-channel output,
///   scaled by AoIntensity, the fifth push-constant component, expanding the block to 20 bytes.
///
/// PSOs are baked against Display.RenderPass, the backbuffer render pass, which includes a depth attachment even though this pipeline disables both depth test and depth write.
/// The FinalBlit pass reuses the backbuffer render pass and framebuffer directly, with no need for a separate color-only render pass.
/// On tilers, Clear is the optimal load op, so extra clears add no bandwidth cost.
/// The uber variants render to the PostColor offscreen render pass in BackbufferCompatible format,
/// and that render pass is render-compatible with the backbuffer render pass because the format and structure match,
/// following the same precedent as Pipeline.Init, so the PSO can be shared.
///
/// Y-flip and sampling precision:
/// both the source image and the backbuffer are written under the same negative-height viewport convention,
/// so framebuffer content layout is identical and identity mapping stays correct.
/// - the point variant uses gl_FragCoord plus texelFetch for identity mapping in integer framebuffer coordinates, giving 1:1 zero sampling error.
/// - the linear variant outputs normalized uv from the VS using the NDC-to-framebuffer mapping of the negative-height viewport,
///   v = (1 - ndc.y) / 2, so normalized-coordinate semantics match on both sides and texture filtering can upsample directly.
///
/// The descriptor layout also serves as the generic sampling layout for offscreen RTs, reused by 1-4 Post input and 1-5 shadow sampling:
/// binding 0 is nearest and binding 1 is linear, both using immutable samplers.
/// Depth-only RTs write only binding 0 because linear filtering for D32 is optional in VK, so no depth view is bound to the linear sampler.
/// </summary>
internal static unsafe class BlitPipeline
{
    public static DescriptorSetLayout SetLayout;

    public static PipelineLayout PipelineLayout;

    static Sampler _samplerPoint;

    static Sampler _samplerLinear;

    static VkPipeline _pipelinePoint;

    static VkPipeline _pipelineLinear;

    static VkPipeline _pipelineTonemapPoint;

    static VkPipeline _pipelineTonemapLinear;

    static VkPipeline _pipelineTonemapBloomPoint;

    static VkPipeline _pipelineTonemapBloomLinear;

    static VkPipeline _pipelineUber;

    static VkPipeline _pipelineUberBloom;

    static VkPipeline _pipelineFxaa;

    static VkPipeline _pipelineTonemapAoPoint;

    static VkPipeline _pipelineTonemapAoLinear;

    static VkPipeline _pipelineTonemapBloomAoPoint;

    static VkPipeline _pipelineTonemapBloomAoLinear;

    static VkPipeline _pipelineUberAo;

    static VkPipeline _pipelineUberBloomAo;

    static VkPipeline _pipelineOutlineComposite;

    static bool _initialized;

    /// <summary>Lazy initialization. Called by the first CreateRenderTarget, after Display.RenderPass is already ready.</summary>
    public static void EnsureInitialized()
    {
        if (_initialized) return;

        _samplerPoint = CreateSampler(Filter.Nearest);
        _samplerLinear = CreateSampler(Filter.Linear);
        SetLayout = CreateDescriptorSetLayout();
        PipelineLayout = CreatePipelineLayout();
        _pipelinePoint = CreatePipelineState(FragmentPointGlsl, "blit_point.frag");
        _pipelineLinear = CreatePipelineState(FragmentLinearGlsl, "blit_linear.frag");
        _pipelineTonemapPoint = CreatePipelineState(FragmentTonemapPointGlsl, "blit_tonemap_point.frag");
        _pipelineTonemapLinear = CreatePipelineState(FragmentTonemapLinearGlsl, "blit_tonemap_linear.frag");
        _pipelineTonemapBloomPoint = CreatePipelineState(FragmentTonemapBloomPointGlsl, "blit_tonemap_bloom_point.frag");
        _pipelineTonemapBloomLinear = CreatePipelineState(FragmentTonemapBloomLinearGlsl, "blit_tonemap_bloom_linear.frag");
        _pipelineUber = CreatePipelineState(FragmentUberGlsl, "blit_uber.frag");
        _pipelineUberBloom = CreatePipelineState(FragmentUberBloomGlsl, "blit_uber_bloom.frag");
        _pipelineFxaa = CreatePipelineState(FragmentFxaaGlsl, "blit_fxaa.frag");
        _pipelineTonemapAoPoint = CreatePipelineState(FragmentTonemapAoPointGlsl, "blit_tonemap_ao_point.frag");
        _pipelineTonemapAoLinear = CreatePipelineState(FragmentTonemapAoLinearGlsl, "blit_tonemap_ao_linear.frag");
        _pipelineTonemapBloomAoPoint = CreatePipelineState(FragmentTonemapBloomAoPointGlsl, "blit_tonemap_bloom_ao_point.frag");
        _pipelineTonemapBloomAoLinear = CreatePipelineState(FragmentTonemapBloomAoLinearGlsl, "blit_tonemap_bloom_ao_linear.frag");
        _pipelineUberAo = CreatePipelineState(FragmentUberAoGlsl, "blit_uber_ao.frag");
        _pipelineUberBloomAo = CreatePipelineState(FragmentUberBloomAoGlsl, "blit_uber_bloom_ao.frag");
        _pipelineOutlineComposite = CreatePipelineState(FragmentOutlineCompositeGlsl, "blit_outline_composite.frag", alphaBlend: true);
        _initialized = true;
    }

    /// <summary>Allocate and write the sampling descriptor set for the output view of an offscreen RT.</summary>
    public static DescriptorSet CreateSourceDescriptor(Silk.NET.Vulkan.ImageView view, bool linearBinding)
    {
        EnsureInitialized();
        var set = Device.DescriptorAllocator.AllocateSet(SetLayout);
        UpdateSourceDescriptor(set, view, linearBinding);
        return set;
    }

    /// <summary>Overwrite a descriptor set to point at the new view. Called after in-place RT recreation during resize, with the GPU already idle.</summary>
    public static void UpdateSourceDescriptor(DescriptorSet set, Silk.NET.Vulkan.ImageView view, bool linearBinding)
    {
        // The output-plane render pass has already transitioned finalLayout into sampling state.
        var imageInfo = new DescriptorImageInfo
        {
            Sampler = _samplerPoint,
            ImageView = view,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal
        };
        var imageInfoLinear = new DescriptorImageInfo
        {
            Sampler = _samplerLinear,
            ImageView = view,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal
        };

        var writes = stackalloc WriteDescriptorSet[2]
        {
            new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = 0,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                PImageInfo = &imageInfo
            },
            new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                PImageInfo = &imageInfoLinear
            }
        };
        Device.Vk.UpdateDescriptorSets(Device.LogicalDevice, linearBinding ? 2u : 1u, writes, 0, null);
    }

    /// <summary>Descriptor-set cache for compute storage textures, bloom, AO, and TAA resolve outputs, which are not RTs, when used as full-screen sampling sources.
    /// After dispatch they have already transitioned to ShaderReadOnlyOptimal with zero explicit barrier.
    ///
    /// Why this is cached in an instance dictionary rather than a single slot:
    /// TAA ping-pong makes SceneColorOverride alternate frame by frame between taa0 and taa1.
    /// A single-slot cache that allocates whenever the instance changes would call vkAllocateDescriptorSets every frame without reclaiming,
    /// exhausting the pool after roughly 2048 frames.
    ///
    /// Why the cache also compares the view version:
    /// after resize, chain textures are recreated in place through RecreateComputeStorage, so the C# object identity stays the same
    /// while the old VkImageView has already been destroyed.
    /// Matching only by instance would leave the descriptor baked from the old view inside the set,
    /// and the GPU would read freed memory, causing a native crash.
    /// This code must compare Texture.ViewVersion rather than View.Handle.
    /// The handle is a heap pointer and often remains exactly equal after recreation, so comparing handles would silently miss the change.
    /// Version changes happen only after DeviceWaitIdle in HandleResize, with no in-flight frame still referencing the set, so in-place overwrite is safe.</summary>
    static DescriptorSet GetSampleDescriptor(Texture tex)
    {
        var version = tex.ViewVersion;
        if (_sampleSets.TryGetValue(tex, out var entry))
        {
            if (entry.ViewVersion != version)
            {
                UpdateSourceDescriptor(entry.Set, tex.View, linearBinding: true);
                _sampleSets[tex] = (entry.Set, version);
            }
            return entry.Set;
        }

        var set = CreateSourceDescriptor(tex.View, linearBinding: true);
        _sampleSets[tex] = (set, version);
        return set;
    }

    /// <summary>Sampling-set cache keyed by Texture instances. Texture does not override Equals, so keys use reference semantics.
    /// The expected size is bloom chain plus AO plus taa0 and taa1, independent of resolution and not growing with resize.</summary>
    static readonly Dictionary<Texture, (DescriptorSet Set, ulong ViewVersion)> _sampleSets = new();

    /// <summary>Step D of 2-1: sampling descriptor set for the bloom-chain output texture.</summary>
    public static DescriptorSet GetBloomDescriptor(Texture bloomTex) => GetSampleDescriptor(bloomTex);

    /// <summary>Step C of 2-2: sampling descriptor set for the AO output texture.</summary>
    public static DescriptorSet GetAoDescriptor(Texture aoTex) => GetSampleDescriptor(aoTex);

    /// <summary>Contract clause 12 of 2-3: sampling descriptor set used when the TAA resolve output becomes the scene source.
    /// This texture matches SceneColor in size and rgba16float format, so the linear binding is written as well,
    /// while variant selection is still driven by the srcRT description.</summary>
    public static DescriptorSet GetSceneDescriptor(Texture sceneTex) => GetSampleDescriptor(sceneTex);

    /// <summary>Record a blit by binding the PSO, choosing point or linear from source size,
    /// tonemap from source format, and bloom or ao from chain-output readiness,
    /// then binding the descriptor set and issuing a full-screen triangle draw(3).
    /// Tonemap variants push a 20-byte constant block, exposure plus BloomIntensity plus AoIntensity.
    /// When bloom=true, which requires tonemap, bloomSet is additionally bound at set=1.
    /// When ao=true, which also requires tonemap, aoSet is additionally bound at set=2.
    /// Must be called inside the FinalBlit pass.</summary>
    public static void Record(CommandBuffer cmd, DescriptorSet sourceSet, bool linear, bool tonemap = false,
        DescriptorSet bloomSet = default, bool bloom = false, DescriptorSet aoSet = default, bool ao = false)
    {
        var pso = (ao, bloom, tonemap, linear) switch
        {
            (true, true, _, true) => _pipelineTonemapBloomAoLinear,
            (true, true, _, false) => _pipelineTonemapBloomAoPoint,
            (true, false, _, true) => _pipelineTonemapAoLinear,
            (true, false, _, false) => _pipelineTonemapAoPoint,
            (false, true, _, true) => _pipelineTonemapBloomLinear,
            (false, true, _, false) => _pipelineTonemapBloomPoint,
            (false, false, true, true) => _pipelineTonemapLinear,
            (false, false, true, false) => _pipelineTonemapPoint,
            (false, false, false, true) => _pipelineLinear,
            (false, false, false, false) => _pipelinePoint,
        };
        Device.Vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pso);
        Device.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, PipelineLayout, 0, 1, in sourceSet, 0, null);
        if (bloom)
            Device.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, PipelineLayout, 1, 1, in bloomSet, 0, null);
        if (ao)
            Device.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, PipelineLayout, 2, 1, in aoSet, 0, null);
        if (tonemap || bloom)
            PushParams(cmd, Device.HdrExposure, RenderQuality.Current.BloomIntensity, 0f, 0f,
                RenderQuality.Current.AoIntensity);
        Device.Vk.CmdDraw(cmd, 3, 1, 0, 0);
    }

    /// <summary>Step D of 2-1: uber composition inside the Post pass, tonemap plus bloom into LDR PostColor with luma baked into alpha.
    /// Prerequisite: the Post pass has already called BeginPass and PostColor is bound as the RT.
    /// Mirrors DX BlitPipeline.DrawUber.</summary>
    public static void RecordUber(CommandBuffer cmd, DescriptorSet sourceSet,
        DescriptorSet bloomSet = default, bool bloom = false, DescriptorSet aoSet = default, bool ao = false)
    {
        var pso = (ao, bloom) switch
        {
            (true, true) => _pipelineUberBloomAo,
            (true, false) => _pipelineUberAo,
            (false, true) => _pipelineUberBloom,
            (false, false) => _pipelineUber,
        };
        Device.Vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pso);
        Device.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, PipelineLayout, 0, 1, in sourceSet, 0, null);
        if (bloom)
            Device.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, PipelineLayout, 1, 1, in bloomSet, 0, null);
        if (ao)
            Device.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, PipelineLayout, 2, 1, in aoSet, 0, null);
        PushParams(cmd, Device.HdrExposure, RenderQuality.Current.BloomIntensity, 0f, 0f,
            RenderQuality.Current.AoIntensity);
        Device.Vk.CmdDraw(cmd, 3, 1, 0, 0);
    }

    /// <summary>Step D of 2-1: present FXAA inside FinalBlit, with the source being the LDR PostColor output of the Post uber pass and luma stored in alpha.
    /// Texel size equals 1 over source size, changes with resize at runtime, and is pushed every frame.
    /// Mirrors DX BlitPipeline.DrawFxaa.</summary>
    public static void RecordFxaa(CommandBuffer cmd, DescriptorSet sourceSet, float texelSizeX, float texelSizeY)
    {
        Device.Vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipelineFxaa);
        Device.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, PipelineLayout, 0, 1, in sourceSet, 0, null);
        PushParams(cmd, 0f, 0f, texelSizeX, texelSizeY, 0f);
        Device.Vk.CmdDraw(cmd, 3, 1, 0, 0);
    }

    /// <summary>Phase 4: Outline2D composition, appended after scene blit inside the same backbuffer pass.
    /// The mask is bound on set=0 using point sampling, where pixels inside the mask keep alpha at 1 and RGB as the outline color of the owning group, allowing multiple colors in the same frame.
    /// Step equals texel times max(width, 1.0), and the RGB of the sample with the largest alpha among the 8 neighbors becomes the outline color.
    /// Alpha is computed as saturate(neighbor - center), then blended to screen with SrcAlpha and InvSrcAlpha, mirroring DX BlitPipeline.DrawOutlineComposite.
    /// Must be called inside the FinalBlit pass after scene blit.</summary>
    public static void RecordOutlineComposite(CommandBuffer cmd, DescriptorSet maskSet, float texelSizeX, float texelSizeY,
        float widthPixels)
    {
        Device.Vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipelineOutlineComposite);
        Device.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, PipelineLayout, 0, 1, in maskSet, 0, null);
        PushParams(cmd, 0f, 0f, texelSizeX, texelSizeY, 0f, widthPixels);
        Device.Vk.CmdDraw(cmd, 3, 1, 0, 0);
    }

    /// <summary>Push the full 24-byte constant block, mirroring the six constants of DX b0.
    /// Variants that declare the block must receive the full push to satisfy validation layers.</summary>
    static void PushParams(CommandBuffer cmd, float exposure, float bloomIntensity, float texelSizeX, float texelSizeY,
        float aoIntensity, float outlineWidth = 0f)
    {
        var p = stackalloc float[6] { exposure, bloomIntensity, texelSizeX, texelSizeY, aoIntensity, outlineWidth };
        Device.Vk.CmdPushConstants(cmd, PipelineLayout, ShaderStageFlags.FragmentBit, 0, 6 * sizeof(float), p);
    }

    static Sampler CreateSampler(Filter filter)
    {
        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = filter,
            MinFilter = filter,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            AnisotropyEnable = false,
            BorderColor = BorderColor.FloatOpaqueBlack,
            CompareEnable = false,
            CompareOp = CompareOp.Always,
            MinLod = 0,
            MaxLod = 0
        };
        if (Device.Vk.CreateSampler(Device.LogicalDevice, in info, null, out var s) != Result.Success)
            throw new Exception("vkCreateSampler (blit) failed");
        return s;
    }

    static DescriptorSetLayout CreateDescriptorSetLayout()
    {
        var samplerPoint = _samplerPoint;
        var samplerLinear = _samplerLinear;
        var bindings = stackalloc DescriptorSetLayoutBinding[2]
        {
            new DescriptorSetLayoutBinding
            {
                Binding = 0,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit,
                PImmutableSamplers = &samplerPoint
            },
            new DescriptorSetLayoutBinding
            {
                Binding = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit,
                PImmutableSamplers = &samplerLinear
            }
        };

        var info = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 2,
            PBindings = bindings
        };

        if (Device.Vk.CreateDescriptorSetLayout(Device.LogicalDevice, in info, null, out var layout) != Result.Success)
            throw new Exception("vkCreateDescriptorSetLayout (blit) failed");
        return layout;
    }

    static PipelineLayout CreatePipelineLayout()
    {
        // set 0 = source RT with both point and linear bindings.
        // set 1 = bloom texture.
        // set 2 = AO texture for step C of 2-2.
        // All three use the same layout, and only the variants that statically reference them bind them, which is valid, mirroring the three t0/t1/t2 tables in the DX root signature.
        var setLayouts = stackalloc DescriptorSetLayout[3] { SetLayout, SetLayout, SetLayout };
        // The 24-byte push constant block contains exposure, BloomIntensity, texelSizeX/Y, AoIntensity, and outlineWidth, mirroring the six constants of DX b0.
        // It is consumed by tonemap, bloom, ao, uber, FXAA, and composite variants.
        // Ordinary variants declare nothing and receive no push, and the shared layout range remains harmless to them.
        var pushRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.FragmentBit,
            Offset = 0,
            Size = 6 * sizeof(float)
        };
        var info = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 3,
            PSetLayouts = setLayouts,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushRange
        };
        if (Device.Vk.CreatePipelineLayout(Device.LogicalDevice, in info, null, out var layout) != Result.Success)
            throw new Exception("vkCreatePipelineLayout (blit) failed");
        return layout;
    }

    static VkPipeline CreatePipelineState(string fragmentGlsl, string fragmentFileName, bool alphaBlend = false)
    {
        bool debug =
#if DEBUG
            true;
#else
            false;
#endif

        var vsModule = ShaderCompiler.CreateShaderModule(
            Device.Vk, Device.LogicalDevice, VertexGlsl, ShaderStageFlags.VertexBit, "main", "blit.vert", debug);
        var fsModule = ShaderCompiler.CreateShaderModule(
            Device.Vk, Device.LogicalDevice, fragmentGlsl, ShaderStageFlags.FragmentBit, "main", fragmentFileName, debug);

        var entryPtr = SilkMarshal.StringToPtr("main", NativeStringEncoding.UTF8);

        try
        {
            var stages = stackalloc PipelineShaderStageCreateInfo[2]
            {
                new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.VertexBit,
                    Module = vsModule,
                    PName = (byte*)entryPtr
                },
                new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.FragmentBit,
                    Module = fsModule,
                    PName = (byte*)entryPtr
                }
            };

            // Full-screen triangle with no vertex input.
            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 0,
                VertexAttributeDescriptionCount = 0
            };

            var inputAsm = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList,
                PrimitiveRestartEnable = false
            };

            var viewportState = new PipelineViewportStateCreateInfo
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                ScissorCount = 1
            };

            var rasterizer = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                DepthClampEnable = false,
                RasterizerDiscardEnable = false,
                PolygonMode = PolygonMode.Fill,
                // A negative-height viewport flips winding direction, so disabling culling is the safest choice for the full-screen triangle.
                CullMode = CullModeFlags.None,
                FrontFace = FrontFace.Clockwise,
                DepthBiasEnable = false,
                LineWidth = 1.0f
            };

            var multisample = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit,
                SampleShadingEnable = false
            };

            // The backbuffer render pass includes a depth attachment, but blit neither tests nor writes depth.
            var depthStencil = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = false,
                DepthWriteEnable = false,
                DepthCompareOp = CompareOp.Always,
                DepthBoundsTestEnable = false,
                StencilTestEnable = false
            };

            var colorAttachment = new PipelineColorBlendAttachmentState
            {
                // Phase 4:
                // the Outline composite variant enables SrcAlpha and InvSrcAlpha blending so the outline edge fades in with edge,
                // while all other variants stay opaque, mirroring the two DX states alphaBlend and defaultRenderTargetBlend.
                BlendEnable = alphaBlend,
                SrcColorBlendFactor = alphaBlend ? BlendFactor.SrcAlpha : BlendFactor.One,
                DstColorBlendFactor = alphaBlend ? BlendFactor.OneMinusSrcAlpha : BlendFactor.Zero,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = alphaBlend ? BlendFactor.One : BlendFactor.One,
                DstAlphaBlendFactor = alphaBlend ? BlendFactor.Zero : BlendFactor.Zero,
                AlphaBlendOp = BlendOp.Add,
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                                 ColorComponentFlags.BBit | ColorComponentFlags.ABit
            };

            var colorBlend = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                LogicOpEnable = false,
                AttachmentCount = 1,
                PAttachments = &colorAttachment
            };

            var dynStates = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
            var dynamicState = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = 2,
                PDynamicStates = dynStates
            };

            var info = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                StageCount = 2,
                PStages = stages,
                PVertexInputState = &vertexInput,
                PInputAssemblyState = &inputAsm,
                PViewportState = &viewportState,
                PRasterizationState = &rasterizer,
                PMultisampleState = &multisample,
                PDepthStencilState = &depthStencil,
                PColorBlendState = &colorBlend,
                PDynamicState = &dynamicState,
                Layout = PipelineLayout,
                // Bake against the backbuffer render pass because FinalBlit always renders to the backbuffer, following VK PSO and render-pass compatibility rules.
                RenderPass = Device.Display.RenderPass,
                Subpass = 0
            };

            if (Device.Vk.CreateGraphicsPipelines(Device.LogicalDevice, default, 1, in info, null, out var pso) != Result.Success)
                throw new Exception("vkCreateGraphicsPipelines (blit) failed");
            return pso;
        }
        finally
        {
            SilkMarshal.Free(entryPtr);
            Device.Vk.DestroyShaderModule(Device.LogicalDevice, vsModule, null);
            Device.Vk.DestroyShaderModule(Device.LogicalDevice, fsModule, null);
        }
    }

    // gl_VertexIndex 0, 1, and 2 generate a large triangle covering the full screen, with NDC coordinates (-1,-1), (3,-1), and (-1,3).
    // uv is output as normalized framebuffer coordinates using the NDC-to-framebuffer mapping of the negative-height viewport,
    // v = (1 - ndc.y) / 2, for the linear variants to sample.
    // The point variant does not consume it, and it is valid for the VS to output more than the FS consumes.
    const string VertexGlsl = @"#version 460

layout(location = 0) out vec2 vUv;

void main()
{
    vec2 pos = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
    gl_Position = vec4(pos * 2.0 - 1.0, 0.0, 1.0);
    vUv = vec2(pos.x, 1.0 - pos.y);
}
";

    // Point variant:
    // texelFetch with gl_FragCoord gives identity mapping in integer framebuffer coordinates, with 1:1 zero sampling error and no dependence on Y-flip direction.
    const string FragmentPointGlsl = @"#version 460

layout(binding = 0) uniform sampler2D srcTex;

layout(location = 0) out vec4 outColor;

void main()
{
    outColor = texelFetch(srcTex, ivec2(gl_FragCoord.xy), 0);
}
";

    // Linear variant:
    // filtered sampling from normalized uv for upsampling fractional-resolution Post output, with matching normalized-coordinate semantics on both sides.
    const string FragmentLinearGlsl = @"#version 460

layout(binding = 1) uniform sampler2D srcTexLinear;

layout(location = 0) in vec2 vUv;

layout(location = 0) out vec4 outColor;

void main()
{
    outColor = texture(srcTexLinear, vUv);
}
";

    // Shared push-constant block:
    // expanded to 16 bytes in step D of 2-1 and to 20 bytes in step C of 2-2, mirroring the five constants of DX b0.
    // Tonemap variants read exposure, bloom variants additionally read bloomIntensity,
    // FXAA variants read texelSizeX/Y, and AO variants additionally read aoIntensity.
    const string PushParamsGlsl = @"
layout(push_constant) uniform BlitParams {
    float exposure;       // Linear exposure multiplier, Device.HdrExposure.
    float bloomIntensity; // 2-1: bloom composition factor, RenderQuality.BloomIntensity, referenced only by bloom and uber variants.
    float texelSizeX;     // Step D of 2-1: FXAA source-texture texel size, referenced only by FXAA variants.
    float texelSizeY;
    float aoIntensity;    // Step C of 2-2: AO occlusion strength, RenderQuality.AoIntensity, referenced only by AO variants.
    float outlineWidth;   // Phase 4: outline-composite step size in pixels, referenced only by composite variants.
};
";

    // Tonemap variants, step B of 1-4:
    // HDR linear source, RGBA16F, then exposure from push constants, then ACES, then gamma encoding to screen.
    // ACES uses the Narkowicz 2015 fit, an RRT plus ODT approximation, with the same constant set shared across all four backends to keep visuals consistent.
    static readonly string TonemapCommonGlsl = PushParamsGlsl + @"
vec3 AcesFilm(vec3 x)
{
    const float a = 2.51;
    const float b = 0.03;
    const float c = 2.43;
    const float d = 0.59;
    const float e = 0.14;
    return clamp((x * (a * x + b)) / (x * (c * x + d) + e), 0.0, 1.0);
}

vec3 Tonemap(vec3 hdr)
{
    vec3 mapped = AcesFilm(max(hdr, vec3(0.0)) * exposure);
    return pow(mapped, vec3(1.0 / 2.2));
}
";

    static readonly string FragmentTonemapPointGlsl = @"#version 460

layout(binding = 0) uniform sampler2D srcTex;

layout(location = 0) out vec4 outColor;
" + TonemapCommonGlsl + @"
void main()
{
    vec4 c = texelFetch(srcTex, ivec2(gl_FragCoord.xy), 0);
    outColor = vec4(Tonemap(c.rgb), c.a);
}
";

    static readonly string FragmentTonemapLinearGlsl = @"#version 460

layout(binding = 1) uniform sampler2D srcTexLinear;

layout(location = 0) in vec2 vUv;

layout(location = 0) out vec4 outColor;
" + TonemapCommonGlsl + @"
void main()
{
    vec4 c = texture(srcTexLinear, vUv);
    outColor = vec4(Tonemap(c.rgb), c.a);
}
";

    // Tonemap plus bloom variants, step D of 2-1 and aligned with DX step B:
    // bloom is added in linear space before ACES, see the 2-1 section in RenderQuality for the contract.
    // Bloom comes from the half-resolution chain output and is always upsampled linearly through the linear binding of the second shared set on set=1.
    static readonly string FragmentTonemapBloomPointGlsl = @"#version 460

layout(binding = 0) uniform sampler2D srcTex;

layout(set = 1, binding = 1) uniform sampler2D bloomTexLinear;

layout(location = 0) in vec2 vUv;

layout(location = 0) out vec4 outColor;
" + TonemapCommonGlsl + @"
void main()
{
    vec4 c = texelFetch(srcTex, ivec2(gl_FragCoord.xy), 0);
    c.rgb += texture(bloomTexLinear, vUv).rgb * bloomIntensity;
    outColor = vec4(Tonemap(c.rgb), c.a);
}
";

    static readonly string FragmentTonemapBloomLinearGlsl = @"#version 460

layout(binding = 1) uniform sampler2D srcTexLinear;

layout(set = 1, binding = 1) uniform sampler2D bloomTexLinear;

layout(location = 0) in vec2 vUv;

layout(location = 0) out vec4 outColor;
" + TonemapCommonGlsl + @"
void main()
{
    vec4 c = texture(srcTexLinear, vUv);
    c.rgb += texture(bloomTexLinear, vUv).rgb * bloomIntensity;
    outColor = vec4(Tonemap(c.rgb), c.a);
}
";

    // Uber variants, step D of 2-1 and aligned with DX step C, used by the Post pass:
    // tonemap plus optional bloom composes into LDR PostColor,
    // and luma, using Rec.601 weights in gamma space and shared cross-backend contract constants, is baked into alpha so FXAA can avoid recomputation.
    // Source and destination share the same size because PostColor uses MatchBackbufferSize, so point sampling with texelFetch identity mapping is always used.
    const string LumaGlsl = @"
float Luma(vec3 ldr)
{
    return dot(ldr, vec3(0.299, 0.587, 0.114));
}
";

    static readonly string FragmentUberGlsl = @"#version 460

layout(binding = 0) uniform sampler2D srcTex;

layout(location = 0) out vec4 outColor;
" + TonemapCommonGlsl + LumaGlsl + @"
void main()
{
    vec4 c = texelFetch(srcTex, ivec2(gl_FragCoord.xy), 0);
    vec3 ldr = Tonemap(c.rgb);
    outColor = vec4(ldr, Luma(ldr));
}
";

    static readonly string FragmentUberBloomGlsl = @"#version 460

layout(binding = 0) uniform sampler2D srcTex;

layout(set = 1, binding = 1) uniform sampler2D bloomTexLinear;

layout(location = 0) in vec2 vUv;

layout(location = 0) out vec4 outColor;
" + TonemapCommonGlsl + LumaGlsl + @"
void main()
{
    vec4 c = texelFetch(srcTex, ivec2(gl_FragCoord.xy), 0);
    c.rgb += texture(bloomTexLinear, vUv).rgb * bloomIntensity;
    vec3 ldr = Tonemap(c.rgb);
    outColor = vec4(ldr, Luma(ldr));
}
";

    // Six AO variants, step C of 2-2 and aligned with DX 2-2 step B:
    // AO is applied in linear space before ACES and bloom is added afterward,
    // scene times mix(1, ao, aoIntensity) plus bloom times bloomIntensity, so AO darkens only the scene and not bloom.
    // AO comes from the r-channel of the half-resolution GTAO output and is always upsampled linearly through the linear binding of the third shared set on set=2.
    // Point variants still declare vUv input for AO sampling, while the source image itself still uses texelFetch identity mapping.
    const string AoSamplerGlsl = @"
layout(set = 2, binding = 1) uniform sampler2D aoTexLinear;

vec3 ApplyAo(vec3 scene, vec2 uv)
{
    float ao = texture(aoTexLinear, uv).r;
    return scene * mix(vec3(1.0), vec3(ao), aoIntensity);
}
";

    static readonly string FragmentTonemapAoPointGlsl = @"#version 460

layout(binding = 0) uniform sampler2D srcTex;

layout(location = 0) in vec2 vUv;

layout(location = 0) out vec4 outColor;
" + TonemapCommonGlsl + AoSamplerGlsl + @"
void main()
{
    vec4 c = texelFetch(srcTex, ivec2(gl_FragCoord.xy), 0);
    c.rgb = ApplyAo(c.rgb, vUv);
    outColor = vec4(Tonemap(c.rgb), c.a);
}
";

    static readonly string FragmentTonemapAoLinearGlsl = @"#version 460

layout(binding = 1) uniform sampler2D srcTexLinear;

layout(location = 0) in vec2 vUv;

layout(location = 0) out vec4 outColor;
" + TonemapCommonGlsl + AoSamplerGlsl + @"
void main()
{
    vec4 c = texture(srcTexLinear, vUv);
    c.rgb = ApplyAo(c.rgb, vUv);
    outColor = vec4(Tonemap(c.rgb), c.a);
}
";

    static readonly string FragmentTonemapBloomAoPointGlsl = @"#version 460

layout(binding = 0) uniform sampler2D srcTex;

layout(set = 1, binding = 1) uniform sampler2D bloomTexLinear;

layout(location = 0) in vec2 vUv;

layout(location = 0) out vec4 outColor;
" + TonemapCommonGlsl + AoSamplerGlsl + @"
void main()
{
    vec4 c = texelFetch(srcTex, ivec2(gl_FragCoord.xy), 0);
    c.rgb = ApplyAo(c.rgb, vUv);
    c.rgb += texture(bloomTexLinear, vUv).rgb * bloomIntensity;
    outColor = vec4(Tonemap(c.rgb), c.a);
}
";

    static readonly string FragmentTonemapBloomAoLinearGlsl = @"#version 460

layout(binding = 1) uniform sampler2D srcTexLinear;

layout(set = 1, binding = 1) uniform sampler2D bloomTexLinear;

layout(location = 0) in vec2 vUv;

layout(location = 0) out vec4 outColor;
" + TonemapCommonGlsl + AoSamplerGlsl + @"
void main()
{
    vec4 c = texture(srcTexLinear, vUv);
    c.rgb = ApplyAo(c.rgb, vUv);
    c.rgb += texture(bloomTexLinear, vUv).rgb * bloomIntensity;
    outColor = vec4(Tonemap(c.rgb), c.a);
}
";

    static readonly string FragmentUberAoGlsl = @"#version 460

layout(binding = 0) uniform sampler2D srcTex;

layout(location = 0) in vec2 vUv;

layout(location = 0) out vec4 outColor;
" + TonemapCommonGlsl + LumaGlsl + AoSamplerGlsl + @"
void main()
{
    vec4 c = texelFetch(srcTex, ivec2(gl_FragCoord.xy), 0);
    c.rgb = ApplyAo(c.rgb, vUv);
    vec3 ldr = Tonemap(c.rgb);
    outColor = vec4(ldr, Luma(ldr));
}
";

    static readonly string FragmentUberBloomAoGlsl = @"#version 460

layout(binding = 0) uniform sampler2D srcTex;

layout(set = 1, binding = 1) uniform sampler2D bloomTexLinear;

layout(location = 0) in vec2 vUv;

layout(location = 0) out vec4 outColor;
" + TonemapCommonGlsl + LumaGlsl + AoSamplerGlsl + @"
void main()
{
    vec4 c = texelFetch(srcTex, ivec2(gl_FragCoord.xy), 0);
    c.rgb = ApplyAo(c.rgb, vUv);
    c.rgb += texture(bloomTexLinear, vUv).rgb * bloomIntensity;
    vec3 ldr = Tonemap(c.rgb);
    outColor = vec4(ldr, Luma(ldr));
}
";

    // Outline composite variant, phase 4 and mirrored with DX PSMainOutlineComposite:
    // pixels inside the mask always keep alpha at 1 and RGB as the outline color of the owning group, allowing multiple colors in the same frame.
    // Edge detection uses alpha so even colors such as pure black remain valid.
    // The outline color comes from the RGB of the first sample with the maximum alpha among the 8 neighbors,
    // so the outline band inherits the group color of the enclosed object.
    // step equals texel times max(outlineWidth, 1.0), edge equals saturate(neighbor - center), and blending uses SrcAlpha.
    static readonly string FragmentOutlineCompositeGlsl = @"#version 460

layout(binding = 0) uniform sampler2D maskTex;

layout(location = 0) in vec2 vUv;

layout(location = 0) out vec4 outColor;
" + PushParamsGlsl + @"
void main()
{
    vec2 stepUv = vec2(texelSizeX, texelSizeY) * max(outlineWidth, 1.0);

    float center = texture(maskTex, vUv).a;
    float neighbor = 0.0;
    vec3 color = vec3(0.0);
    const vec2 offsets[8] = vec2[8](
        vec2( 1,  0), vec2(-1,  0), vec2(0,  1), vec2(0, -1),
        vec2( 1,  1), vec2(-1,  1), vec2(1, -1), vec2(-1, -1));
    for (int k = 0; k < 8; k++)
    {
        vec4 s = texture(maskTex, vUv + offsets[k] * stepUv);
        if (s.a > neighbor)
        {
            neighbor = s.a;
            color = s.rgb;
        }
    }

    float edge = clamp(neighbor - center, 0.0, 1.0);
    outColor = vec4(color, edge);
}
";

    // FXAA variant, step D of 2-1 and used by FinalBlit:
    // simplified FXAA 3.11 quality mode, with 5 taps for direction estimation and 4 taps for directional sampling.
    // Luma comes from source alpha, already baked by the uber pass.
    // REDUCE_MIN, REDUCE_MUL, SPAN_MAX, and the contrast thresholds are shared four-backend contract constants ported literally from the DX reference implementation.
    // Binding 0 uses point sampling for neighborhood taps and binding 1 uses linear sampling for directional taps, both bound to the same PostColor output view.
    static readonly string FragmentFxaaGlsl = @"#version 460

layout(binding = 0) uniform sampler2D srcTex;

layout(binding = 1) uniform sampler2D srcTexLinear;

layout(location = 0) in vec2 vUv;

layout(location = 0) out vec4 outColor;
" + PushParamsGlsl + @"
void main()
{
    const float FXAA_REDUCE_MIN = 1.0 / 128.0;
    const float FXAA_REDUCE_MUL = 1.0 / 8.0;
    const float FXAA_SPAN_MAX = 8.0;
    const float FXAA_EDGE_THRESHOLD = 1.0 / 8.0;
    const float FXAA_EDGE_THRESHOLD_MIN = 1.0 / 24.0;

    vec2 rcpFrame = vec2(texelSizeX, texelSizeY);
    vec2 uv = vUv;

    vec4 colorM = texture(srcTex, uv);
    float lumaM  = colorM.a;
    float lumaNW = texture(srcTex, uv + vec2(-1.0, -1.0) * rcpFrame).a;
    float lumaNE = texture(srcTex, uv + vec2( 1.0, -1.0) * rcpFrame).a;
    float lumaSW = texture(srcTex, uv + vec2(-1.0,  1.0) * rcpFrame).a;
    float lumaSE = texture(srcTex, uv + vec2( 1.0,  1.0) * rcpFrame).a;

    float lumaMin = min(lumaM, min(min(lumaNW, lumaNE), min(lumaSW, lumaSE)));
    float lumaMax = max(lumaM, max(max(lumaNW, lumaNE), max(lumaSW, lumaSE)));

    vec4 result = colorM;

    // Early out for low contrast:
    // pass through non-edge pixels directly to save the bandwidth of directional sampling.
    if (lumaMax - lumaMin >= max(FXAA_EDGE_THRESHOLD_MIN, lumaMax * FXAA_EDGE_THRESHOLD))
    {
        // Edge tangent direction, the direction orthogonal to the luma gradient,
        // normalized by local brightness and clamped by the maximum span.
        vec2 dir = vec2(
            -((lumaNW + lumaNE) - (lumaSW + lumaSE)),
             ((lumaNW + lumaSW) - (lumaNE + lumaSE)));

        float dirReduce = max((lumaNW + lumaNE + lumaSW + lumaSE) * 0.25 * FXAA_REDUCE_MUL, FXAA_REDUCE_MIN);
        float rcpDirMin = 1.0 / (min(abs(dir.x), abs(dir.y)) + dirReduce);
        dir = clamp(dir * rcpDirMin, vec2(-FXAA_SPAN_MAX), vec2(FXAA_SPAN_MAX)) * rcpFrame;

        // Four taps along the tangent direction:
        // the inner pair, plus or minus 1/6 span, is always trusted,
        // while the outer pair, plus or minus 1/2 span, falls back to the inner pair when it goes out of bounds.
        vec4 rgbA = 0.5 * (
            texture(srcTexLinear, uv + dir * (1.0 / 3.0 - 0.5)) +
            texture(srcTexLinear, uv + dir * (2.0 / 3.0 - 0.5)));
        vec4 rgbB = rgbA * 0.5 + 0.25 * (
            texture(srcTexLinear, uv + dir * -0.5) +
            texture(srcTexLinear, uv + dir * 0.5));

        result = (rgbB.a < lumaMin || rgbB.a > lumaMax) ? rgbA : rgbB;
    }

    outColor = vec4(result.rgb, 1.0);
}
";
}
