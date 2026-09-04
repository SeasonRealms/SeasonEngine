// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Vulkan;
using Silk.NET.Core.Native;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

/// <summary>
/// Render-pipeline modes aligned one to one with the DX-side PipelineMode:
/// - Opaque:      opaque rendering with blending disabled, DepthWrite=true, and DepthCompare=Less
/// - Transparent: true BLEND translucency with alpha blending, DepthWrite=false, and DepthCompare=LessOrEqual
/// - Fade:        full-model fade in or fade out with alpha blending, DepthWrite=true, and DepthCompare=Less
/// </summary>
internal enum PipelineMode
{
    Opaque,
    Transparent,
    Fade,
}

/// <summary>
/// Vulkan pipeline set equivalent to DX12 DXPipeline:
///   - one PipelineLayout, the DX RootSignature equivalent, with 4 UBOs, b0 through b3, plus 5 sampled images, t0 through t4
///   - one immutable Sampler, the DX static sampler s0 equivalent, using Linear plus Clamp
///   - three VkPipeline variants reusing the same GLSL source, with inline VS and FS
///
/// GLSL and HLSL translation conventions:
///   1. cbuffer → layout(std140, binding = N) uniform
///   2. Texture2D + SamplerState → uniform sampler2D（CombinedImageSampler）
///   3. mul(v, M) in HLSL, row-vector post-multiply, becomes v * M in GLSL, where the implicit std140 column-major transpose cancels out and keeps the math identical
///   4. SV_POSITION → gl_Position；SV_TARGET → out vec4 outColor
///   5. clip(x) → if (x &lt; 0) discard；saturate → clamp(x,0,1)；lerp → mix
/// </summary>
internal static unsafe class Pipeline
{
    public static DescriptorSetLayout SetLayout;

    public static PipelineLayout PipelineLayout;

    public static Sampler StaticSampler;

    /// <summary>2-2: nearest-clamp static sampler, the immutable sampler bound by compute DepthTexture.
    /// GLSL texelFetch does not filter, and linear filtering for D32 is an optional Vulkan feature,
    /// so this follows the rule of not binding depth views to linear samplers. See the BlitPipeline class header.</summary>
    public static Sampler StaticPointSampler;

    public static VkPipeline OpaquePipelineState;
    public static VkPipeline OpaqueDoubleSidedPipelineState;

    public static VkPipeline TransparentPipelineState;
    public static VkPipeline TransparentDoubleSidedPipelineState;
public static VkPipeline TransparentBackFacePipelineState;

    public static VkPipeline FadePipelineState;
    public static VkPipeline FadeDoubleSidedPipelineState;

    /// <summary>Contract clause 7 of 2-2: AoExempt NoDepth variants.
    /// Opaque and Fade only change depth write to zero and keep all other states identical.
    /// When Mesh3D.ExcludeFromAo is true, draw buckets select them through PrimitiveData.AoExempt.
    /// The velocity table mirrors the same setup.</summary>
    public static VkPipeline OpaqueNoDepthPipelineState;
    public static VkPipeline OpaqueNoDepthDoubleSidedPipelineState;
    public static VkPipeline FadeNoDepthPipelineState;
    public static VkPipeline FadeNoDepthDoubleSidedPipelineState;
    public static VkPipeline VelOpaqueNoDepthPipelineState;
    public static VkPipeline VelOpaqueNoDepthDoubleSidedPipelineState;
    public static VkPipeline VelFadeNoDepthPipelineState;
    public static VkPipeline VelFadeNoDepthDoubleSidedPipelineState;

    /// <summary>1-5 shadow-pass depth-only PSO, using CullNone plus slope bias, baked against the shadow-atlas depth-only render pass with an empty FS.
    /// The shadow-atlas RT is created after Init, so baking is deferred through <see cref="EnsureShadowPipeline"/>.</summary>
    public static VkPipeline ShadowPipelineState;

    /// <summary>Dedicated PSO for the Outline2D mask pass:
    /// outputs the group outline-color mask, reads depth, and does not write depth, mirroring DX OutlineMaskPipelineState.
    /// The mask RT and render pass are created after Init, lazily on the first enabled Graphics frame,
    /// so baking is deferred through <see cref="EnsureOutlineMaskPipelines"/>.</summary>
    public static VkPipeline OutlineMaskPipelineState;
    public static VkPipeline OutlineMaskDoubleSidedPipelineState;

    // Step D of 2-3:
    // velocity MRT PSO variants, created when MotionVectors=1 and mapped one to one with the regular 8 variants.
    // The only differences from the regular PS are one extra R16G16Float velocity attachment in the render pass,
    // IndependentBlend enabled, and slot-1 write masks split by Opaque versus non-Opaque.
    public static VkPipeline VelOpaquePipelineState;
    public static VkPipeline VelOpaqueDoubleSidedPipelineState;
    public static VkPipeline VelTransparentPipelineState;
    public static VkPipeline VelTransparentDoubleSidedPipelineState;
    public static VkPipeline VelTransparentBackFacePipelineState;
    public static VkPipeline VelFadePipelineState;
    public static VkPipeline VelFadeDoubleSidedPipelineState;

    // Dedicated PSO family for the Overlay pass, baked against the backbuffer render pass:
    // Overlay renders directly to the backbuffer.
    // It is render-pass incompatible with the HDR-tier _renderPass, RGBA16F,
    // and Overlay depth is loaded from the DontCare content of the previous pass, which is undefined.
    // Depth testing must therefore be disabled, or strict Android tiler drivers hit undefined behavior.
    // lavapipe and desktop IMR drivers are more tolerant, so the problem does not show on Linux there.
    // See the Overlay route in SetPipeline.
    public static VkPipeline OverlayOpaquePipelineState;
    public static VkPipeline OverlayOpaqueDoubleSidedPipelineState;
    public static VkPipeline OverlayTransparentPipelineState;
    public static VkPipeline OverlayTransparentDoubleSidedPipelineState;
    public static VkPipeline OverlayTransparentBackFacePipelineState;
    public static VkPipeline OverlayFadePipelineState;
    public static VkPipeline OverlayFadeDoubleSidedPipelineState;

    static RenderPass _renderPass;

    /// <summary>Overlay PSO bake anchor, always the backbuffer render pass. The Init parameter is Display.RenderPass.</summary>
    static RenderPass _overlayRenderPass;

    /// <summary>Shadow PSO bake anchor, the shadow-atlas depth-only render pass, finalized by EnsureShadowPipeline.</summary>
    static RenderPass _shadowRenderPass;

    /// <summary>OutlineMask PSO bake anchor, the mask render pass with color=BackbufferCompatible and depth=DepthBufferFormat,
    /// where depth LoadOp=Load preserves scene depth. Finalized by EnsureOutlineMaskPipelines.</summary>
    static RenderPass _outlineMaskRenderPass;

    /// <summary>Step D of 2-3: bake anchor for velocity MRT PSOs, using a 3-attachment render pass with color plus velocity R16G16Float plus depth.
    /// Created when MotionVectors=1, with the same color format as _renderPass to preserve render-pass compatibility.</summary>
    static RenderPass _velocityRenderPass;

    /// <summary>1-5 shadow comparison sampler, the immutable GLSL sampler2DShadow on binding 12, with CompareEnable plus LessOrEqual plus linear hardware PCF.</summary>
    public static Sampler ShadowComparisonSampler;

    /// <summary>Step C of 2-5: dedicated linear-<b>wrap</b> sampler for cloud noise, immutable at binding 19.
    /// This is the only sampling path in the whole pipeline that needs Wrap:
    /// the noise tiles periodically, while wind offsets can push uv outside [0,1].
    /// Using Clamp would stretch the outermost column into a motionless stripe across the sky, mirroring DX s2 wrapSampler.</summary>
    public static Sampler WrapSampler;

    /// <summary>Identity instance buffer, 64 bytes, bound to slot 1 during regular drawing.</summary>
    public static BufferResource IdentityInstanceBuffer;

    /// <summary>Identity instanced-bone storage buffer, containing one identity matrix, used as the placeholder for non-instanced skinning paths.</summary>
    public static BufferResource[] IdentityInstanceBoneBuffers = null!;

    static byte*[] _mappedIdentityInstanceBoneBuffers = null!;

    /// <summary>Default zero morph-delta storage buffer, used as the placeholder for primitives without morph data.</summary>
    public static BufferResource[] DefaultMorphDeltasBuffers = null!;

    static byte*[] _mappedDefaultMorphDeltasBuffers = null!;

    // Step C of 2-3:
    // default zero SSBOs for previous-frame data, with sentinel semantics:
    // matrix _m33==0 or all-zero weights make shader code fall back to current data.
    // Bindings 13, 14, and 15 all bind the matching default buffer when prev data is unavailable.
    public static BufferResource[] DefaultPrevBoneBuffers = null!;
    public static BufferResource[] DefaultPrevInstanceWorldBuffers = null!;
    public static BufferResource[] DefaultPrevMorphWeightsBuffers = null!;

    // -- Shared resources for Text GPU Instancing --
    /// <summary>Unit-quad VB shared by all Texts controls, with 4 vertices, positions at +/-0.5 and UVs in 0..1.</summary>
    public static BufferResource UnitQuadVertexBuffer;

    /// <summary>Unit-quad IB shared by all Texts controls, with 6 indices forming 2 triangles.</summary>
    public static BufferResource UnitQuadIndexBuffer;

    /// <summary>Default TextDrawParams UBO, placeholder for b4 and binding 11, including the font pxRange.</summary>
    public static BufferResource DefaultTextDrawParamsBuffer;

    static byte* _mappedDefaultTextDrawParams;

    public static void Init(RenderPass renderPass)
    {
        // Step A of 1-4:
        // in the HDR tier, SceneColor is RGBA16F and becomes render-pass incompatible with the backbuffer render pass,
        // as previewed in the Device class header.
        // The main PSO is therefore baked against an RGBA16F-compatible render pass.
        // That render pass is created through the same VKRenderTarget path as the SceneColor instance render pass,
        // so compatibility is guaranteed by the code path itself.
        // In the HDR tier, the Scene pass always renders offscreen,
        // since HdrSceneColor already implies UseOffscreenSceneColor,
        // so the main PSO is never used to render the backbuffer.
        // The LDR tier keeps baking against the backbuffer render pass,
        // because BackbufferCompatible offscreen render passes stay compatible with it and PSOs remain reusable.
        _renderPass = Device.HdrSceneColor
            ? VKRenderTarget.CreateColorRenderPassForFormat(Device.SceneColorFormat)
            : renderPass;

        // The Overlay bake anchor is always the backbuffer render pass:
        // the Overlay pass renders directly to the backbuffer, B8G8R8A8_SRGB.
        // In the HDR tier it is render-pass incompatible with _renderPass, RGBA16F,
        // for the same reason as OutlineMask, so it needs its own dedicated PSO family.
        _overlayRenderPass = renderPass;

        // Create the identity instance buffer, 64 bytes = InstanceTransformData.
        IdentityInstanceBuffer = Device.ResourceManager.CreateConstantBuffer(64, out var mapped);
        Unsafe.InitBlock(mapped, 0, 64); // All fields are zero, which produces the float4 rows of the identity matrix.

        CreateIdentityInstanceBoneBuffers();
        CreateDefaultMorphDeltasBuffers();
        CreateDefaultPrevSSBOs();

        // -- Text GPU Instancing: shared unit-quad VB plus IB for all Texts --
        CreateUnitQuad();

        // -- Default TextDrawParams, placeholder for binding 11 plus the default font pxRange --
        DefaultTextDrawParamsBuffer = Device.ResourceManager.CreateConstantBuffer(
            (uint)Unsafe.SizeOf<VKTextDrawParams>(), out _mappedDefaultTextDrawParams);
        var defaultTdp = new VKTextDrawParams
        {
            AtlasSize = Vector2.One,
            PxRange = Season.Fonts.Font.PixelRange,
            GlobalAlpha = 1f,
            TextColor = Vector4.One,
        };
        Unsafe.Write(_mappedDefaultTextDrawParams, defaultTdp);

        StaticSampler = CreateImmutableSampler();
        StaticPointSampler = CreateImmutablePointSampler();
        // 1-5:
        // the immutable comparison sampler for binding 12 must be created before SetLayout because PImmutableSamplers references it.
        ShadowComparisonSampler = CreateShadowComparisonSampler();
        // Step C of 2-5:
        // the immutable wrap sampler for binding 19 must also be created before SetLayout.
        WrapSampler = CreateWrapSampler();
        SetLayout = CreateDescriptorSetLayout();
        PipelineLayout = CreatePipelineLayout();

        OpaquePipelineState = CreatePipelineState(PipelineMode.Opaque);
        OpaqueDoubleSidedPipelineState = CreatePipelineState(PipelineMode.Opaque, true);
        TransparentPipelineState = CreatePipelineState(PipelineMode.Transparent);
        TransparentDoubleSidedPipelineState = CreatePipelineState(PipelineMode.Transparent, true);
        TransparentBackFacePipelineState = CreatePipelineState(PipelineMode.Transparent, CullModeFlags.FrontBit);
        FadePipelineState = CreatePipelineState(PipelineMode.Fade);
        FadeDoubleSidedPipelineState = CreatePipelineState(PipelineMode.Fade, true);

        // Contract clause 7 of 2-2:
        // AoExempt NoDepth variants are baked eagerly.
        // Only depth write is forced to zero, and shader bytecode stays literally identical to the regular variants.
        OpaqueNoDepthPipelineState = CreatePipelineState(PipelineMode.Opaque, CullModeFlags.BackBit, depthWriteOverride: false);
        OpaqueNoDepthDoubleSidedPipelineState = CreatePipelineState(PipelineMode.Opaque, CullModeFlags.None, depthWriteOverride: false);
        FadeNoDepthPipelineState = CreatePipelineState(PipelineMode.Fade, CullModeFlags.BackBit, depthWriteOverride: false);
        FadeNoDepthDoubleSidedPipelineState = CreatePipelineState(PipelineMode.Fade, CullModeFlags.None, depthWriteOverride: false);

        // Dedicated Overlay family, mirroring the DX route for ActivePassId==Overlay:
        // baked against the backbuffer render pass, with DepthTest disabled because Overlay depth is undefined.
        // Sprite2D, Shape, and Texts all use this family.
        OverlayOpaquePipelineState = CreatePipelineState(PipelineMode.Opaque, CullModeFlags.BackBit, overlay: true);
        OverlayOpaqueDoubleSidedPipelineState = CreatePipelineState(PipelineMode.Opaque, CullModeFlags.None, overlay: true);
        OverlayTransparentPipelineState = CreatePipelineState(PipelineMode.Transparent, CullModeFlags.BackBit, overlay: true);
        OverlayTransparentDoubleSidedPipelineState = CreatePipelineState(PipelineMode.Transparent, CullModeFlags.None, overlay: true);
        OverlayTransparentBackFacePipelineState = CreatePipelineState(PipelineMode.Transparent, CullModeFlags.FrontBit, overlay: true);
        OverlayFadePipelineState = CreatePipelineState(PipelineMode.Fade, CullModeFlags.BackBit, overlay: true);
        OverlayFadeDoubleSidedPipelineState = CreatePipelineState(PipelineMode.Fade, CullModeFlags.None, overlay: true);

        // Step D of 2-3:
        // when MotionVectors=1, create velocity MRT PSOs, 8 variants plus one velocity render pass.
        // The velocity render pass uses the same color format as the main render pass,
        // with render-pass compatibility guaranteed by the code path.
        if (RenderQuality.Current.MotionVectors)
        {
            _velocityRenderPass = VKRenderTarget.CreateVelocityRenderPassForFormat(
                Device.SceneColorFormat);
            VelOpaquePipelineState = CreatePipelineState(PipelineMode.Opaque, CullModeFlags.BackBit, velocity: true);
            VelOpaqueDoubleSidedPipelineState = CreatePipelineState(PipelineMode.Opaque, CullModeFlags.None, velocity: true);
            VelTransparentPipelineState = CreatePipelineState(PipelineMode.Transparent, CullModeFlags.BackBit, velocity: true);
            VelTransparentDoubleSidedPipelineState = CreatePipelineState(PipelineMode.Transparent, CullModeFlags.None, velocity: true);
            VelTransparentBackFacePipelineState = CreatePipelineState(PipelineMode.Transparent, CullModeFlags.FrontBit, velocity: true);
            VelFadePipelineState = CreatePipelineState(PipelineMode.Fade, CullModeFlags.BackBit, velocity: true);
            VelFadeDoubleSidedPipelineState = CreatePipelineState(PipelineMode.Fade, CullModeFlags.None, velocity: true);
            // Mirror of clause 7 in 2-2:
            // NoDepth variants for the velocity table, mapped one to one with the regular table.
            VelOpaqueNoDepthPipelineState = CreatePipelineState(PipelineMode.Opaque, CullModeFlags.BackBit, velocity: true, depthWriteOverride: false);
            VelOpaqueNoDepthDoubleSidedPipelineState = CreatePipelineState(PipelineMode.Opaque, CullModeFlags.None, velocity: true, depthWriteOverride: false);
            VelFadeNoDepthPipelineState = CreatePipelineState(PipelineMode.Fade, CullModeFlags.BackBit, velocity: true, depthWriteOverride: false);
            VelFadeNoDepthDoubleSidedPipelineState = CreatePipelineState(PipelineMode.Fade, CullModeFlags.None, velocity: true, depthWriteOverride: false);
        }

        // 1-7:
        // create the 1x1 all-black fallback cube ahead of time.
        // Binding 16 always needs a valid descriptor.
        // The writes array is stackalloc memory and is not zero-initialized, so leaving it empty would write stack garbage.
        // The very first descriptor-set write already needs this fallback, so it cannot wait for the first environment texture to load.
        // TextureUploadBatch and TransferCommandQueue are already initialized in CreateSwapChain at this point.
        _ = VKTextureCube.DummyBlack;
    }

    public static void Shutdown()
    {
        var vk = Device.Vk;
        var d = Device.LogicalDevice;
        var rm = Device.ResourceManager;
        if (OpaquePipelineState.Handle != 0) vk.DestroyPipeline(d, OpaquePipelineState, null);
        if (OpaqueDoubleSidedPipelineState.Handle != 0) vk.DestroyPipeline(d, OpaqueDoubleSidedPipelineState, null);
        if (TransparentPipelineState.Handle != 0) vk.DestroyPipeline(d, TransparentPipelineState, null);
        if (TransparentDoubleSidedPipelineState.Handle != 0) vk.DestroyPipeline(d, TransparentDoubleSidedPipelineState, null);
        if (TransparentBackFacePipelineState.Handle != 0) vk.DestroyPipeline(d, TransparentBackFacePipelineState, null);
        if (FadePipelineState.Handle != 0) vk.DestroyPipeline(d, FadePipelineState, null);
        if (FadeDoubleSidedPipelineState.Handle != 0) vk.DestroyPipeline(d, FadeDoubleSidedPipelineState, null);
        // Clause 7 of 2-2: cleanup for AoExempt NoDepth variants.
        if (OpaqueNoDepthPipelineState.Handle != 0) vk.DestroyPipeline(d, OpaqueNoDepthPipelineState, null);
        if (OpaqueNoDepthDoubleSidedPipelineState.Handle != 0) vk.DestroyPipeline(d, OpaqueNoDepthDoubleSidedPipelineState, null);
        if (FadeNoDepthPipelineState.Handle != 0) vk.DestroyPipeline(d, FadeNoDepthPipelineState, null);
        if (FadeNoDepthDoubleSidedPipelineState.Handle != 0) vk.DestroyPipeline(d, FadeNoDepthDoubleSidedPipelineState, null);
        // Cleanup for the dedicated Overlay family.
        if (OverlayOpaquePipelineState.Handle != 0) vk.DestroyPipeline(d, OverlayOpaquePipelineState, null);
        if (OverlayOpaqueDoubleSidedPipelineState.Handle != 0) vk.DestroyPipeline(d, OverlayOpaqueDoubleSidedPipelineState, null);
        if (OverlayTransparentPipelineState.Handle != 0) vk.DestroyPipeline(d, OverlayTransparentPipelineState, null);
        if (OverlayTransparentDoubleSidedPipelineState.Handle != 0) vk.DestroyPipeline(d, OverlayTransparentDoubleSidedPipelineState, null);
        if (OverlayTransparentBackFacePipelineState.Handle != 0) vk.DestroyPipeline(d, OverlayTransparentBackFacePipelineState, null);
        if (OverlayFadePipelineState.Handle != 0) vk.DestroyPipeline(d, OverlayFadePipelineState, null);
        if (OverlayFadeDoubleSidedPipelineState.Handle != 0) vk.DestroyPipeline(d, OverlayFadeDoubleSidedPipelineState, null);
        if (ShadowPipelineState.Handle != 0) vk.DestroyPipeline(d, ShadowPipelineState, null);
        // Cleanup for the dedicated Outline2D mask PSOs.
        if (OutlineMaskPipelineState.Handle != 0) vk.DestroyPipeline(d, OutlineMaskPipelineState, null);
        if (OutlineMaskDoubleSidedPipelineState.Handle != 0) vk.DestroyPipeline(d, OutlineMaskDoubleSidedPipelineState, null);
        // Step D of 2-3: cleanup for velocity MRT PSOs.
        if (VelOpaquePipelineState.Handle != 0) vk.DestroyPipeline(d, VelOpaquePipelineState, null);
        if (VelOpaqueDoubleSidedPipelineState.Handle != 0) vk.DestroyPipeline(d, VelOpaqueDoubleSidedPipelineState, null);
        if (VelTransparentPipelineState.Handle != 0) vk.DestroyPipeline(d, VelTransparentPipelineState, null);
        if (VelTransparentDoubleSidedPipelineState.Handle != 0) vk.DestroyPipeline(d, VelTransparentDoubleSidedPipelineState, null);
        if (VelTransparentBackFacePipelineState.Handle != 0) vk.DestroyPipeline(d, VelTransparentBackFacePipelineState, null);
        if (VelFadePipelineState.Handle != 0) vk.DestroyPipeline(d, VelFadePipelineState, null);
        if (VelFadeDoubleSidedPipelineState.Handle != 0) vk.DestroyPipeline(d, VelFadeDoubleSidedPipelineState, null);
        if (VelOpaqueNoDepthPipelineState.Handle != 0) vk.DestroyPipeline(d, VelOpaqueNoDepthPipelineState, null);
        if (VelOpaqueNoDepthDoubleSidedPipelineState.Handle != 0) vk.DestroyPipeline(d, VelOpaqueNoDepthDoubleSidedPipelineState, null);
        if (VelFadeNoDepthPipelineState.Handle != 0) vk.DestroyPipeline(d, VelFadeNoDepthPipelineState, null);
        if (VelFadeNoDepthDoubleSidedPipelineState.Handle != 0) vk.DestroyPipeline(d, VelFadeNoDepthDoubleSidedPipelineState, null);
        if (PipelineLayout.Handle != 0) vk.DestroyPipelineLayout(d, PipelineLayout, null);
        if (SetLayout.Handle != 0) vk.DestroyDescriptorSetLayout(d, SetLayout, null);
        if (StaticSampler.Handle != 0) vk.DestroySampler(d, StaticSampler, null);
        if (StaticPointSampler.Handle != 0) vk.DestroySampler(d, StaticPointSampler, null);
        if (ShadowComparisonSampler.Handle != 0) vk.DestroySampler(d, ShadowComparisonSampler, null);
        if (WrapSampler.Handle != 0) vk.DestroySampler(d, WrapSampler, null);
        OpaquePipelineState = OpaqueDoubleSidedPipelineState = default;
        TransparentPipelineState = TransparentDoubleSidedPipelineState = TransparentBackFacePipelineState = default;
        FadePipelineState = FadeDoubleSidedPipelineState = default;
        OpaqueNoDepthPipelineState = OpaqueNoDepthDoubleSidedPipelineState = default;
        FadeNoDepthPipelineState = FadeNoDepthDoubleSidedPipelineState = default;
        OverlayOpaquePipelineState = OverlayOpaqueDoubleSidedPipelineState = default;
        OverlayTransparentPipelineState = OverlayTransparentDoubleSidedPipelineState = OverlayTransparentBackFacePipelineState = default;
        OverlayFadePipelineState = OverlayFadeDoubleSidedPipelineState = default;
        ShadowPipelineState = default;
        VelOpaquePipelineState = VelOpaqueDoubleSidedPipelineState = default;
        VelTransparentPipelineState = VelTransparentDoubleSidedPipelineState = VelTransparentBackFacePipelineState = default;
        VelFadePipelineState = VelFadeDoubleSidedPipelineState = default;
        VelOpaqueNoDepthPipelineState = VelOpaqueNoDepthDoubleSidedPipelineState = default;
        VelFadeNoDepthPipelineState = VelFadeNoDepthDoubleSidedPipelineState = default;
        ShadowComparisonSampler = default;
        _shadowRenderPass = default;
        _overlayRenderPass = default;
        if (_velocityRenderPass.Handle != 0) vk.DestroyRenderPass(d, _velocityRenderPass, null);
        _velocityRenderPass = default;
        PipelineLayout = default;
        SetLayout = default;
        StaticSampler = default;
        StaticPointSampler = default;
        if (IdentityInstanceBuffer.Memory.Handle != 0) rm.DestroyBuffer(IdentityInstanceBuffer);
        IdentityInstanceBuffer = default;

        if (UnitQuadVertexBuffer.Memory.Handle != 0) rm.DestroyBuffer(UnitQuadVertexBuffer);
        UnitQuadVertexBuffer = default;
        if (UnitQuadIndexBuffer.Memory.Handle != 0) rm.DestroyBuffer(UnitQuadIndexBuffer);
        UnitQuadIndexBuffer = default;
        if (DefaultTextDrawParamsBuffer.Memory.Handle != 0)
        {
            if (_mappedDefaultTextDrawParams != null)
                vk.UnmapMemory(d, DefaultTextDrawParamsBuffer.Memory);
            rm.DestroyBuffer(DefaultTextDrawParamsBuffer);
            DefaultTextDrawParamsBuffer = default;
            _mappedDefaultTextDrawParams = null;
        }
        if (IdentityInstanceBoneBuffers != null)
        {
            for (int i = 0; i < IdentityInstanceBoneBuffers.Length; i++)
            {
                if (_mappedIdentityInstanceBoneBuffers != null
                    && i < _mappedIdentityInstanceBoneBuffers.Length
                    && _mappedIdentityInstanceBoneBuffers[i] != null
                    && IdentityInstanceBoneBuffers[i].Memory.Handle != 0)
                {
                    vk.UnmapMemory(d, IdentityInstanceBoneBuffers[i].Memory);
                }

                if (IdentityInstanceBoneBuffers[i].Memory.Handle != 0)
                    rm.DestroyBuffer(IdentityInstanceBoneBuffers[i]);
            }

            IdentityInstanceBoneBuffers = null!;
            _mappedIdentityInstanceBoneBuffers = null!;
        }

        if (DefaultMorphDeltasBuffers != null)
        {
            for (int i = 0; i < DefaultMorphDeltasBuffers.Length; i++)
            {
                if (_mappedDefaultMorphDeltasBuffers != null
                    && i < _mappedDefaultMorphDeltasBuffers.Length
                    && _mappedDefaultMorphDeltasBuffers[i] != null
                    && DefaultMorphDeltasBuffers[i].Memory.Handle != 0)
                {
                    vk.UnmapMemory(d, DefaultMorphDeltasBuffers[i].Memory);
                }

                if (DefaultMorphDeltasBuffers[i].Memory.Handle != 0)
                    rm.DestroyBuffer(DefaultMorphDeltasBuffers[i]);
            }

            DefaultMorphDeltasBuffers = null!;
            _mappedDefaultMorphDeltasBuffers = null!;
        }

        // Step C of 2-3: cleanup for default zero SSBOs holding previous-frame data.
        if (DefaultPrevBoneBuffers != null)
        {
            for (int i = 0; i < DefaultPrevBoneBuffers.Length; i++)
                if (DefaultPrevBoneBuffers[i].Memory.Handle != 0)
                    rm.DestroyBuffer(DefaultPrevBoneBuffers[i]);
            DefaultPrevBoneBuffers = null!;
        }
        if (DefaultPrevInstanceWorldBuffers != null)
        {
            for (int i = 0; i < DefaultPrevInstanceWorldBuffers.Length; i++)
                if (DefaultPrevInstanceWorldBuffers[i].Memory.Handle != 0)
                    rm.DestroyBuffer(DefaultPrevInstanceWorldBuffers[i]);
            DefaultPrevInstanceWorldBuffers = null!;
        }
        if (DefaultPrevMorphWeightsBuffers != null)
        {
            for (int i = 0; i < DefaultPrevMorphWeightsBuffers.Length; i++)
                if (DefaultPrevMorphWeightsBuffers[i].Memory.Handle != 0)
                    rm.DestroyBuffer(DefaultPrevMorphWeightsBuffers[i]);
            DefaultPrevMorphWeightsBuffers = null!;
        }
    }

    static void CreateIdentityInstanceBoneBuffers()
    {
        int n = (int)Device.frameCount;
        IdentityInstanceBoneBuffers = new BufferResource[n];
        _mappedIdentityInstanceBoneBuffers = new byte*[n];

        ulong size = (ulong)Unsafe.SizeOf<Matrix4x4>();
        var identity = Matrix4x4.Identity;

        for (int i = 0; i < n; i++)
        {
            IdentityInstanceBoneBuffers[i] = Device.ResourceManager.CreateBuffer(
                size,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

            void* mapped;
            if (Device.Vk.MapMemory(Device.LogicalDevice, IdentityInstanceBoneBuffers[i].Memory, 0, size, 0, &mapped) != Result.Success)
                throw new Exception("vkMapMemory (IdentityInstanceBoneBuffers) failed");

            _mappedIdentityInstanceBoneBuffers[i] = (byte*)mapped;
            Unsafe.Write(_mappedIdentityInstanceBoneBuffers[i], identity);
        }
    }

    static void CreateDefaultMorphDeltasBuffers()
    {
        int n = (int)Device.frameCount;
        DefaultMorphDeltasBuffers = new BufferResource[n];
        _mappedDefaultMorphDeltasBuffers = new byte*[n];

        ulong size = sizeof(float);
        for (int i = 0; i < n; i++)
        {
            DefaultMorphDeltasBuffers[i] = Device.ResourceManager.CreateBuffer(
                size,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

            void* mapped;
            if (Device.Vk.MapMemory(Device.LogicalDevice, DefaultMorphDeltasBuffers[i].Memory, 0, size, 0, &mapped) != Result.Success)
                throw new Exception("vkMapMemory (DefaultMorphDeltasBuffers) failed");

            _mappedDefaultMorphDeltasBuffers[i] = (byte*)mapped;
            Unsafe.Write(_mappedDefaultMorphDeltasBuffers[i], 0.0f);
        }
    }

    /// <summary>Step C of 2-3: create three default zero SSBOs for previous-frame data, each with 1 zero-filled entry using sentinel semantics.
    /// binding 13: prev bone, one zero mat4, where shader-side _m33==0 triggers fallback
    /// binding 14: prev instanceWorld, one zero mat4
    /// binding 15: prev morphWeights, one zero vec4</summary>
    static void CreateDefaultPrevSSBOs()
    {
        int n = (int)Device.frameCount;
        var usageFlags = BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit;
        var memFlags = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;

        // binding 13: prev bone, one zero mat4 = 64 bytes.
        DefaultPrevBoneBuffers = new BufferResource[n];
        for (int i = 0; i < n; i++)
        {
            ulong size = (ulong)Unsafe.SizeOf<Matrix4x4>();
            DefaultPrevBoneBuffers[i] = Device.ResourceManager.CreateBuffer(size, usageFlags, memFlags);
            void* mapped;
            if (Device.Vk.MapMemory(Device.LogicalDevice, DefaultPrevBoneBuffers[i].Memory, 0, size, 0, &mapped) != Result.Success)
                throw new Exception("vkMapMemory (DefaultPrevBoneBuffers) failed");
            new Span<byte>(mapped, (int)size).Clear();
            Device.Vk.UnmapMemory(Device.LogicalDevice, DefaultPrevBoneBuffers[i].Memory);
        }

        // binding 14: prev instanceWorld, one zero mat4 = 64 bytes.
        DefaultPrevInstanceWorldBuffers = new BufferResource[n];
        for (int i = 0; i < n; i++)
        {
            ulong size = (ulong)Unsafe.SizeOf<Matrix4x4>();
            DefaultPrevInstanceWorldBuffers[i] = Device.ResourceManager.CreateBuffer(size, usageFlags, memFlags);
            void* mapped;
            if (Device.Vk.MapMemory(Device.LogicalDevice, DefaultPrevInstanceWorldBuffers[i].Memory, 0, size, 0, &mapped) != Result.Success)
                throw new Exception("vkMapMemory (DefaultPrevInstanceWorldBuffers) failed");
            new Span<byte>(mapped, (int)size).Clear();
            Device.Vk.UnmapMemory(Device.LogicalDevice, DefaultPrevInstanceWorldBuffers[i].Memory);
        }

        // binding 15: prev morphWeights, one zero vec4 = 16 bytes.
        DefaultPrevMorphWeightsBuffers = new BufferResource[n];
        for (int i = 0; i < n; i++)
        {
            ulong size = (ulong)Unsafe.SizeOf<Vector4>();
            DefaultPrevMorphWeightsBuffers[i] = Device.ResourceManager.CreateBuffer(size, usageFlags, memFlags);
            void* mapped;
            if (Device.Vk.MapMemory(Device.LogicalDevice, DefaultPrevMorphWeightsBuffers[i].Memory, 0, size, 0, &mapped) != Result.Success)
                throw new Exception("vkMapMemory (DefaultPrevMorphWeightsBuffers) failed");
            new Span<byte>(mapped, (int)size).Clear();
            Device.Vk.UnmapMemory(Device.LogicalDevice, DefaultPrevMorphWeightsBuffers[i].Memory);
        }
    }

    /// <summary>
    /// Create the shared unit quad for Text GPU Instancing:
    /// 4 vertices, positions at +/-0.5 with UVs in 0..1, plus 6 indices.
    /// All Texts controls share the same VB and IB, bound at slot 0.
    /// </summary>
    static void CreateUnitQuad()
    {
        var quadVertices = new Vertex[]
        {
            new() { Position = new Vector3(-0.5f, -0.5f, 0), TexCoord = new Vector2(0, 1), Normal = Vector3.UnitZ, Tangent = new Vector4(1, 0, 0, 1), Joints = Vector4.Zero, Weights = Vector4.Zero },
            new() { Position = new Vector3( 0.5f, -0.5f, 0), TexCoord = new Vector2(1, 1), Normal = Vector3.UnitZ, Tangent = new Vector4(1, 0, 0, 1), Joints = Vector4.Zero, Weights = Vector4.Zero },
            new() { Position = new Vector3(-0.5f,  0.5f, 0), TexCoord = new Vector2(0, 0), Normal = Vector3.UnitZ, Tangent = new Vector4(1, 0, 0, 1), Joints = Vector4.Zero, Weights = Vector4.Zero },
            new() { Position = new Vector3( 0.5f,  0.5f, 0), TexCoord = new Vector2(1, 0), Normal = Vector3.UnitZ, Tangent = new Vector4(1, 0, 0, 1), Joints = Vector4.Zero, Weights = Vector4.Zero },
        };
        var quadIndices = new uint[] { 0, 1, 2, 1, 3, 2 };

        UnitQuadVertexBuffer = Device.ResourceManager.CreateVertexBuffer(quadVertices);
        UnitQuadIndexBuffer = Device.ResourceManager.CreateIndexBuffer(quadIndices);
    }

    /// <summary>Set the target pipeline, equivalent to DX SetPipelineState plus SetGraphicsRootSignature plus IASetPrimitiveTopology.</summary>
    public static void SetPipeline(CommandBuffer cmd, PipelineMode mode, bool doubleSided = false)
    {
        SetPipeline(cmd, mode, doubleSided ? CullModeFlags.None : CullModeFlags.BackBit);
    }

    public static void SetPipeline(CommandBuffer cmd, PipelineMode mode, CullModeFlags cullMode)
    {
        SetPipeline(cmd, mode, cullMode, depthWrite: true);
    }

    /// <summary>Contract clause 7 of 2-2: depthWrite=false routes to NoDepth variants.
    /// Only Opaque and Fade have matching PSOs.
    /// Transparent already skips depth writes, so it falls back to the regular variant.</summary>
    public static void SetPipeline(CommandBuffer cmd, PipelineMode mode, CullModeFlags cullMode, bool depthWrite)
    {
        // Outline2D mask pass:
        // route to the dedicated PSO, with mask FS plus LessEqual plus no depth writes.
        // This mirrors the DX ActivePassId route.
        // Mask variants are requested only inside this pass, so the depth-write switch does not participate in selection.
        if (Device.ActivePassId == Season.Rendering.RenderPassId.OutlineMask)
        {
            var maskPso = cullMode == CullModeFlags.None ? OutlineMaskDoubleSidedPipelineState : OutlineMaskPipelineState;
            Device.Vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, maskPso);
            return;
        }

        // Overlay pass:
        // route to the dedicated family baked against the backbuffer render pass, mirroring DX ActivePassId==Overlay.
        // This must run before the MotionVectors route.
        // Velocity PSOs are baked for a 3-attachment render pass and would be render-pass incompatible inside the 2-attachment backbuffer render pass.
        // Android tilers are strict here, while lavapipe and desktop IMR drivers are more tolerant.
        // Overlay depth content is also undefined, so this family disables DepthTest.
        // Sprite2D, Shape, and Texts all go through this path.
        if (Device.ActivePassId == Season.Rendering.RenderPassId.Overlay)
        {
            var overlayPso = GetOverlayPipelineState(mode, cullMode);
            Device.Vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, overlayPso);
            return;
        }

        // Step D of 2-3:
        // when MotionVectors=1, automatically select the velocity MRT PSO, mirroring the DX CreatePipelineState velocityOutput path.
        // Velocity PSOs map one to one with regular PSOs and differ only in render pass and blend state.
        // Shader source stays shared, with VELOCITY_OUTPUT injected at compile time.
        var pso = RenderQuality.Current.MotionVectors
            ? GetVelocityPipelineState(mode, cullMode, depthWrite)
            : GetRegularPipelineState(mode, cullMode, depthWrite);
        Device.Vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pso);
        // PrimitiveTopology is already baked into the PSO as TriangleList.
    }

    static VkPipeline GetRegularPipelineState(PipelineMode mode, CullModeFlags cullMode, bool depthWrite = true) => mode switch
    {
        PipelineMode.Transparent when cullMode == CullModeFlags.None => TransparentDoubleSidedPipelineState,
        PipelineMode.Transparent when cullMode == CullModeFlags.FrontBit => TransparentBackFacePipelineState,
        PipelineMode.Transparent => TransparentPipelineState,
        PipelineMode.Fade when !depthWrite => cullMode == CullModeFlags.None ? FadeNoDepthDoubleSidedPipelineState : FadeNoDepthPipelineState,
        PipelineMode.Fade => cullMode == CullModeFlags.None ? FadeDoubleSidedPipelineState : FadePipelineState,
        _ when !depthWrite => cullMode == CullModeFlags.None ? OpaqueNoDepthDoubleSidedPipelineState : OpaqueNoDepthPipelineState,
        _ => cullMode == CullModeFlags.None ? OpaqueDoubleSidedPipelineState : OpaquePipelineState,
    };

    // Overlay family route:
    // baked against the backbuffer render pass with DepthTest disabled. See the Overlay branch in SetPipeline.
    static VkPipeline GetOverlayPipelineState(PipelineMode mode, CullModeFlags cullMode) => mode switch
    {
        PipelineMode.Transparent when cullMode == CullModeFlags.None => OverlayTransparentDoubleSidedPipelineState,
        PipelineMode.Transparent when cullMode == CullModeFlags.FrontBit => OverlayTransparentBackFacePipelineState,
        PipelineMode.Transparent => OverlayTransparentPipelineState,
        PipelineMode.Fade => cullMode == CullModeFlags.None ? OverlayFadeDoubleSidedPipelineState : OverlayFadePipelineState,
        _ => cullMode == CullModeFlags.None ? OverlayOpaqueDoubleSidedPipelineState : OverlayOpaquePipelineState,
    };

    static VkPipeline GetVelocityPipelineState(PipelineMode mode, CullModeFlags cullMode, bool depthWrite = true) => mode switch
    {
        PipelineMode.Transparent when cullMode == CullModeFlags.None => VelTransparentDoubleSidedPipelineState,
        PipelineMode.Transparent when cullMode == CullModeFlags.FrontBit => VelTransparentBackFacePipelineState,
        PipelineMode.Transparent => VelTransparentPipelineState,
        PipelineMode.Fade when !depthWrite => cullMode == CullModeFlags.None ? VelFadeNoDepthDoubleSidedPipelineState : VelFadeNoDepthPipelineState,
        PipelineMode.Fade => cullMode == CullModeFlags.None ? VelFadeDoubleSidedPipelineState : VelFadePipelineState,
        _ when !depthWrite => cullMode == CullModeFlags.None ? VelOpaqueNoDepthDoubleSidedPipelineState : VelOpaqueNoDepthPipelineState,
        _ => cullMode == CullModeFlags.None ? VelOpaqueDoubleSidedPipelineState : VelOpaquePipelineState,
    };

    /// <summary>
    /// Unified draw entry:
    /// bind the vertex buffer at slot 0 and the instance buffer at slot 1,
    /// then call vkCmdDrawIndexed according to instanceCount, where 1 versus N means instanced drawing.
    /// When instanceBuffer is null, IdentityInstanceBuffer is used automatically.
    /// </summary>
    public static void DrawPrimitive(
        CommandBuffer cmd,
        PrimitiveData primitiveData,
        VkBuffer primitiveVertexBuffer,
        VkBuffer primitiveIndexBuffer,
        DescriptorSet descriptorSet,
        uint indexCount,
        VkBuffer instanceBuffer,
        uint instanceCount,
        uint firstInstance)
    {
        var vk = Device.Vk;

        // Ensure textures are ready for rendering.
        primitiveData.BaseColorTexture?.EnsureReadyForRendering(cmd);
        primitiveData.NormalTexture?.EnsureReadyForRendering(cmd);
        primitiveData.MetallicRoughnessTexture?.EnsureReadyForRendering(cmd);
        primitiveData.OcclusionTexture?.EnsureReadyForRendering(cmd);
        primitiveData.EmissiveTexture?.EnsureReadyForRendering(cmd);

        // 1-7:
        // refresh binding 16 to the environment radiance cube active for the current frame.
        // Environment maps load asynchronously, so they inevitably become ready after the first descriptor-set write of early primitives.
        // This path therefore refreshes by ViewVersion rather than by handle.
        // Only the set for the current frame slot is touched.
        // Its previous submission is already guaranteed retired by the same-slot fence waited at the end of AfterRender,
        // while other slots may still be in flight and will be refreshed when their own frame arrives.
        // The cube layout was already transitioned during upload, so this introduces no barrier.
        // Layout transitions remain forbidden inside a render pass.
        VKTextureCube.RefreshBinding(descriptorSet,
            ref primitiveData.EnvCubeViewVersions[(int)Device.FrameIndex]);

        // Clause 10 of 2-4:
        // refresh binding 17 to the DDGI atlas for the current frame.
        // The atlas ping-pongs every frame, irr0 and irr1, and compute already transitions it to ShaderReadOnlyOptimal after writing.
        // As with envCube, there is no barrier inside the render pass,
        // so only the current frame-slot set is refreshed by ViewVersion under the same timing and fence guarantee.
        VKPrimitiveGroup.RefreshDdgiBinding(descriptorSet,
            ref primitiveData.DdgiAtlasViewVersions[(int)Device.FrameIndex]);

        // Step 3 of 2-4:
        // refresh binding 18 in the same way to the current-frame DDGI depth atlas, with dep0 and dep1 ping-ponging in sync.
        VKPrimitiveGroup.RefreshDdgiDepthBinding(descriptorSet,
            ref primitiveData.DdgiDepthViewVersions[(int)Device.FrameIndex]);

        // Step C of 2-5:
        // refresh binding 19 to the current-frame cloud noise.
        // The noise is baked only once in its lifetime, so the version converges after the first frame and stops changing.
        VKPrimitiveGroup.RefreshCloudNoiseBinding(descriptorSet,
            ref primitiveData.CloudNoiseViewVersions[(int)Device.FrameIndex]);

        // Step E of 2-5:
        // refresh binding 20 in the same way to the current-frame AP volume, which is also baked only once.
        VKPrimitiveGroup.RefreshAerialLutBinding(descriptorSet,
            ref primitiveData.AerialLutViewVersions[(int)Device.FrameIndex]);

        // Bind vertex buffer slot 0 for geometry data.
        ulong vbOffset = 0;
        vk.CmdBindVertexBuffers(cmd, 0, 1, &primitiveVertexBuffer, &vbOffset);

        // Bind instance buffer slot 1, either identity or real instance data.
        var instBuf = instanceBuffer.Handle != 0 ? instanceBuffer : IdentityInstanceBuffer.Buffer;
        ulong instOffset = 0;
        vk.CmdBindVertexBuffers(cmd, 1, 1, &instBuf, &instOffset);

        // Bind the index buffer.
        vk.CmdBindIndexBuffer(cmd, primitiveIndexBuffer, 0, primitiveData.Use32BitIndices ? IndexType.Uint32 : IndexType.Uint16);

        // Bind the descriptor set, containing b0, b1, b2, plus the 5 textures.
        var set = descriptorSet;
        var pipelineLayout = PipelineLayout;
        vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, pipelineLayout, 0, 1, &set, 0, null);

        vk.CmdDrawIndexed(cmd, indexCount, instanceCount, 0, 0, firstInstance);
    }

    /// <summary>
    /// 1-5:
    /// lazily bake the shadow depth-only PSO.
    /// The shadow-atlas RT, which provides the depth-only render pass, is created after Init,
    /// so the app passes its RenderPass on the first call after creating the shadow RT.
    /// The operation is idempotent and bakes only once.
    /// Pipeline creation is not command recording, so it may be called at any time after RT creation.
    /// </summary>
    public static void EnsureShadowPipeline(RenderPass shadowRenderPass)
    {
        if (ShadowPipelineState.Handle != 0) return;
        _shadowRenderPass = shadowRenderPass;
        ShadowPipelineState = CreatePipelineState(PipelineMode.Opaque, CullModeFlags.None, shadowPass: true);
    }

    /// <summary>1-5: bind the shadow depth-only PSO, equivalent to DX SetShadowPipelineState.</summary>
    public static void SetShadowPipeline(CommandBuffer cmd)
    {
        Device.Vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, ShadowPipelineState);
    }

    /// <summary>
    /// Lazily bake OutlineMask PSOs.
    /// The mask RT, BackbufferCompatible, and its render pass are created after Init,
    /// so the first call comes from Device.BeginPass(OutlineMask) passing that render pass.
    /// The operation is idempotent and bakes only once.
    /// The bake anchor for mask PSOs differs from the Scene main PSO because the color format is BackbufferCompatible.
    /// _renderPass cannot be reused, since in the HDR tier it is RGBA16F and render-pass incompatible.
    /// There are two call sites:
    /// shared SceneDepth, in AO tiers, passes the dedicated render pass through EnsureOutlineMaskPass with depth LoadOp=Load;
    /// the degraded path without shared depth passes maskRT.RenderPass directly from BeginPass with depth LoadOp=Clear.
    /// Whether SceneDepth exists is fixed for the session during init-tier selection,
    /// so the first bake anchor becomes the only shape used in that session and the two render passes are never mixed.
    /// </summary>
    public static void EnsureOutlineMaskPipelines(RenderPass outlineMaskRenderPass)
    {
        if (OutlineMaskPipelineState.Handle != 0)
            return;
        _outlineMaskRenderPass = outlineMaskRenderPass;
        OutlineMaskPipelineState = CreatePipelineState(PipelineMode.Opaque, CullModeFlags.BackBit,
            outlineMask: true, depthWriteOverride: false);
        OutlineMaskDoubleSidedPipelineState = CreatePipelineState(PipelineMode.Opaque, CullModeFlags.None,
            outlineMask: true, depthWriteOverride: false);
    }

    /// <summary>1-5: write the light-space ViewProj for each quadrant into the VS push constant, using raw System.Numerics row-major bytes and adapting with GLSL-side pre-multiplication.</summary>
    public static void SetShadowViewProj(CommandBuffer cmd, in Matrix4x4 lightViewProj)
    {
        fixed (Matrix4x4* p = &lightViewProj)
            Device.Vk.CmdPushConstants(cmd, PipelineLayout, ShaderStageFlags.VertexBit, 0, 64, p);
    }

    /// <summary>Write the Outline2D mask outline color group by group, at FS push constant offset 0 as a float4, mirroring the DX b6 root constant.</summary>
    public static void SetOutlineMaskColor(CommandBuffer cmd, in Vector4 color)
    {
        fixed (Vector4* p = &color)
            Device.Vk.CmdPushConstants(cmd, PipelineLayout, ShaderStageFlags.FragmentBit, 0, 16, p);
    }

    /// <summary>
    /// 1-5: draw entry for the shadow pass.
    /// Mirrors <see cref="DrawPrimitive"/> but skips texture EnsureReadyForRendering,
    /// because the depth-only path has no FS sampling and barriers are forbidden inside the pass.
    /// Binding 12, the shadow atlas, is still referenced by the descriptor set,
    /// but the shadow variant uses an empty FS and does not statically consume it,
    /// so there is no hazard even if it points to the atlas currently being written.
    /// </summary>
    public static void DrawShadowPrimitive(
        CommandBuffer cmd,
        PrimitiveData primitiveData,
        VkBuffer primitiveVertexBuffer,
        VkBuffer primitiveIndexBuffer,
        DescriptorSet descriptorSet,
        uint indexCount,
        VkBuffer instanceBuffer,
        uint instanceCount,
        uint firstInstance)
    {
        var vk = Device.Vk;

        ulong vbOffset = 0;
        vk.CmdBindVertexBuffers(cmd, 0, 1, &primitiveVertexBuffer, &vbOffset);

        var instBuf = instanceBuffer.Handle != 0 ? instanceBuffer : IdentityInstanceBuffer.Buffer;
        ulong instOffset = 0;
        vk.CmdBindVertexBuffers(cmd, 1, 1, &instBuf, &instOffset);

        vk.CmdBindIndexBuffer(cmd, primitiveIndexBuffer, 0, primitiveData.Use32BitIndices ? IndexType.Uint32 : IndexType.Uint16);

        var set = descriptorSet;
        vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, PipelineLayout, 0, 1, &set, 0, null);

        vk.CmdDrawIndexed(cmd, indexCount, instanceCount, 0, 0, firstInstance);
    }

    static Sampler CreateImmutableSampler()
    {
        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Linear,
            // Match DX behavior: Clamp avoids 1-pixel seams at the borders of standalone textures such as skyboxes.
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            AnisotropyEnable = false,
            BorderColor = BorderColor.FloatOpaqueBlack,
            CompareEnable = false,
            CompareOp = CompareOp.Always,
            MinLod = 0,
            MaxLod = float.MaxValue
        };
        if (Device.Vk.CreateSampler(Device.LogicalDevice, in info, null, out var s) != Result.Success)
            throw new Exception("vkCreateSampler failed");
        return s;
    }

    static Sampler CreateImmutablePointSampler()
    {
        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Nearest,
            MinFilter = Filter.Nearest,
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
            throw new Exception("vkCreateSampler (point) failed");
        return s;
    }

    /// <summary>
    /// 1-5 shadow comparison sampler, sampler2DShadow and immutable at binding 12:
    /// CompareEnable plus LessOrEqual plus linear,
    /// giving hardware comparison plus 2x2 bilinear PCF.
    /// Combined with the shader-side 3x3 grid, this matches the visual result of DX SampleCmpLevelZero.
    /// ClampToEdge prevents leakage outside quadrant boundaries, with BorderColor as fallback.
    /// Linear comparison sampling for D32 is a separate feature and is widely supported on both desktop and mobile.
    /// </summary>
    static Sampler CreateShadowComparisonSampler()
    {
        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            AnisotropyEnable = false,
            BorderColor = BorderColor.FloatOpaqueWhite,
            CompareEnable = true,
            CompareOp = CompareOp.LessOrEqual,
            MinLod = 0,
            MaxLod = 0
        };
        if (Device.Vk.CreateSampler(Device.LogicalDevice, in info, null, out var s) != Result.Success)
            throw new Exception("vkCreateSampler (shadow comparison) failed");
        return s;
    }

    /// <summary>
    /// Step C of 2-5: linear-wrap sampler for cloud noise, immutable at binding 19.
    /// Uses Repeat on all three axes plus linear filtering.
    /// The noise tiles at a fixed period, with integer lattice hashes wrapped by octave grid size first,
    /// while wind offsets can push uv outside [0,1].
    /// Clamp would stretch the outermost column into a motionless stripe across the sky,
    /// matching the semantics avoided by DX s2 wrapSampler.
    /// </summary>
    static Sampler CreateWrapSampler()
    {
        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.Repeat,
            AddressModeV = SamplerAddressMode.Repeat,
            AddressModeW = SamplerAddressMode.Repeat,
            AnisotropyEnable = false,
            BorderColor = BorderColor.FloatOpaqueBlack,
            CompareEnable = false,
            CompareOp = CompareOp.Always,
            MinLod = 0,
            MaxLod = 0
        };
        if (Device.Vk.CreateSampler(Device.LogicalDevice, in info, null, out var s) != Result.Success)
            throw new Exception("vkCreateSampler (wrap) failed");
        return s;
    }

    static DescriptorSetLayout CreateDescriptorSetLayout()
    {
        // binding 0..3: 4 UBOs.
        // binding 11: TextDrawParams UBO.
        // binding 4..8: 5 CombinedImageSamplers.
        // binding 9 and 10: storage buffers for instance bones and morph deltas.
        // binding 12, 1-5: shadow-atlas CombinedImageSampler with an immutable comparison sampler.
        // binding 13, 14, and 15, step C of 2-3: previous-frame data SSBOs, prev bones, prev instanceWorld, and prev morphWeights.
        // binding 16, 1-7: environment radiance cube CombinedImageSampler with the immutable StaticSampler.
        // binding 17, clause 10 of 2-4: DDGI irradiance atlas.
        // binding 18, step 3 of 2-4: DDGI depth-moment atlas.
        // binding 19, step C of 2-5: cloud noise with the immutable wrap sampler.
        // binding 20, step E of 2-5: AP 3D LUT with linear clamp.
        var sampler = StaticSampler;
        var shadowSampler = ShadowComparisonSampler;
        var wrapSampler = WrapSampler;
        var bindings = stackalloc DescriptorSetLayoutBinding[21];

        bindings[0] = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.UniformBuffer,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.VertexBit
        };
        bindings[1] = new DescriptorSetLayoutBinding
        {
            Binding = 1,
            DescriptorType = DescriptorType.UniformBuffer,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit
        };
        bindings[2] = new DescriptorSetLayoutBinding
        {
            Binding = 2,
            DescriptorType = DescriptorType.UniformBuffer,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit
        };
        bindings[3] = new DescriptorSetLayoutBinding
        {
            Binding = 3,
            DescriptorType = DescriptorType.UniformBuffer,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.VertexBit
        };
        bindings[9] = new DescriptorSetLayoutBinding
        {
            Binding = 9,
            DescriptorType = DescriptorType.StorageBuffer,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.VertexBit
        };
        bindings[10] = new DescriptorSetLayoutBinding
        {
            Binding = 10,
            DescriptorType = DescriptorType.StorageBuffer,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.VertexBit
        };
        bindings[11] = new DescriptorSetLayoutBinding
        {
            Binding = 11,
            DescriptorType = DescriptorType.UniformBuffer,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit
        };
        for (int i = 0; i < 5; i++)
        {
            bindings[4 + i] = new DescriptorSetLayoutBinding
            {
                Binding = (uint)(4 + i),
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit,
                PImmutableSamplers = &sampler
            };
        }
        // 1-5: shadow atlas on binding 12, using an immutable comparison sampler. The main PS consumes it through PCF sampling.
        bindings[12] = new DescriptorSetLayoutBinding
        {
            Binding = 12,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
            PImmutableSamplers = &shadowSampler
        };
        // Step C of 2-3:
        // previous-frame data SSBOs on binding 13, 14, and 15, read by the VS to reconstruct prevClip.
        // These mirror binding 9 and 10, StorageBuffer in VS,
        // and bind the default zero-value buffers when prev data does not exist.
        bindings[13] = new DescriptorSetLayoutBinding
        {
            Binding = 13,
            DescriptorType = DescriptorType.StorageBuffer,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.VertexBit
        };
        bindings[14] = new DescriptorSetLayoutBinding
        {
            Binding = 14,
            DescriptorType = DescriptorType.StorageBuffer,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.VertexBit
        };
        bindings[15] = new DescriptorSetLayoutBinding
        {
            Binding = 15,
            DescriptorType = DescriptorType.StorageBuffer,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.VertexBit
        };
        // 1-7:
        // environment radiance cube on binding 16, reusing the immutable StaticSampler from binding 4..8,
        // linear plus repeat.
        // Seamless cross-face cube sampling is handled by the driver, so addressMode does not participate.
        // When no environment map exists, bind VKTextureCube.DummyBlack, so this descriptor always stays valid.
        bindings[16] = new DescriptorSetLayoutBinding
        {
            Binding = VKTextureCube.EnvCubeBinding,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
            PImmutableSamplers = &sampler
        };

        // Clause 10 of 2-4:
        // DDGI irradiance atlas on binding 17, reusing the immutable linear StaticSampler from binding 4..8 and 16.
        // Octahedral tile UVs are computed in shader, while boundaries rely on the 1-pixel gutter and bilinear overflow.
        // When not ready, VKPrimitiveGroup.DdgiAtlasBound falls back to Device.White, so this descriptor always stays valid.
        bindings[17] = new DescriptorSetLayoutBinding
        {
            Binding = VKPrimitiveGroup.DdgiAtlasBinding,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
            PImmutableSamplers = &sampler
        };

        // Step 3 of 2-4:
        // DDGI depth-moment atlas on binding 18, rg16float, reusing the same immutable linear StaticSampler.
        // This follows the same pattern as binding 17:
        // when not ready, VKPrimitiveGroup.DdgiDepthBound falls back to Device.White, so the descriptor always remains valid.
        bindings[18] = new DescriptorSetLayoutBinding
        {
            Binding = VKPrimitiveGroup.DdgiDepthBinding,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
            PImmutableSamplers = &sampler
        };

        // Step C of 2-5:
        // cloud noise on binding 19, rgba8unorm.
        // The sampler must wrap, see the WrapSampler note,
        // so the clamp StaticSampler cannot be reused.
        // When not ready, VKPrimitiveGroup.CloudNoiseBound falls back to Device.White.
        // Actual sampling is gated at runtime by cloudParams0.w, the layer count,
        // so this descriptor always remains valid.
        bindings[19] = new DescriptorSetLayoutBinding
        {
            Binding = VKPrimitiveGroup.CloudNoiseBinding,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
            PImmutableSamplers = &wrapSampler
        };

        // Step E of 2-5:
        // aerial-perspective 3D LUT on binding 20, rgba16float.
        // Three-axis Clamp plus trilinear sampling reuse the StaticSampler, linear clamp, just as DX uses s0 everywhere.
        // When not ready, VKPrimitiveGroup.AerialLutBound falls back to a 1x1x1 all-zero dummy,
        // which is the identity element of the composition formula.
        // Actual sampling is gated at runtime by apParams0.x.
        bindings[20] = new DescriptorSetLayoutBinding
        {
            Binding = VKPrimitiveGroup.AerialLutBinding,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
            PImmutableSamplers = &sampler
        };

        var info = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 21,
            PBindings = bindings
        };

        if (Device.Vk.CreateDescriptorSetLayout(Device.LogicalDevice, in info, null, out var layout) != Result.Success)
            throw new Exception("vkCreateDescriptorSetLayout failed");
        return layout;
    }

    static PipelineLayout CreatePipelineLayout()
    {
        var setLayout = SetLayout;

        // 1-5:
        // VS push constant, 64 bytes, holding light-space ViewProj and written per quadrant in the shadow pass.
        // The main PSO and shadow PSO share the same layout.
        // The main VS simply does not reference it, which is legal.
        // Outline2D mask:
        // FS push constant, 16 bytes, for outline color at offset 0, consumed only by the mask FS variant.
        // Overlap with the VS range [0,64) is legal because cross-stage overlap is allowed and vkCmdPushConstants updates by stageFlags intersection.
        // A non-zero range base is intentionally forbidden:
        // when the FS block sits at base offset 64, some drivers, observed with lavapipe, fail to read data pushed at offset 64
        // and instead read uninitialized storage, producing white outlines. That is the root cause of DX red versus VK white outlines.
        var pushRangeVS = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.VertexBit,
            Offset = 0,
            Size = 64
        };
        var pushRangeFS = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.FragmentBit,
            Offset = 0,
            Size = 16
        };
        var pushRanges = stackalloc PushConstantRange[2] { pushRangeVS, pushRangeFS };
        var info = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &setLayout,
            PushConstantRangeCount = 2,
            PPushConstantRanges = pushRanges
        };
        if (Device.Vk.CreatePipelineLayout(Device.LogicalDevice, in info, null, out var layout) != Result.Success)
            throw new Exception("vkCreatePipelineLayout failed");
        return layout;
    }

    static VkPipeline CreatePipelineState(PipelineMode mode, bool doubleSided = false)
    {
        return CreatePipelineState(mode, doubleSided ? CullModeFlags.None : CullModeFlags.BackBit);
    }

    static VkPipeline CreatePipelineState(PipelineMode mode, CullModeFlags cullMode, bool shadowPass = false, bool velocity = false, bool depthWriteOverride = true, bool outlineMask = false, bool overlay = false)
    {
        bool debug =
#if DEBUG
            true;
#else
            false;
#endif

        // 1-5 shadow dual switches, contract clause 3 and mirrored with the DX injection path:
        // SHADOW_ENABLED enables PCF sampling in the main FS, following quality-tier selection.
        // SHADOW_PASS enables the depth-only VS variant, reusing all deformation stages of the main VS plus light-space projection, with an empty FS.
        // Both variants are injected into the VS, though only SHADOW_PASS takes effect there.
        // Contract clause 3 of 2-3:
        // VELOCITY_OUTPUT=1 makes the VS append prevClip computation and turns the FS into MRT output, slot0=color and slot1=velocity.
        // MotionVectors is fixed during initialization, so only one shape is baked during a process lifetime.
        // Outline2D mask uses a single color attachment plus the mask FS variant, OUTLINE_MASK,
        // so velocityOutput always stays 0 even when MotionVectors=1.
        bool velocityOutput = RenderQuality.Current.MotionVectors && !shadowPass && !outlineMask && !overlay;
        string shadowDefs = (RenderQuality.Current.ShadowsEnabled ? "#define SHADOW_ENABLED 1\n" : "#define SHADOW_ENABLED 0\n")
            + (shadowPass ? "#define SHADOW_PASS 1\n" : "#define SHADOW_PASS 0\n")
            + (velocityOutput ? "#define VELOCITY_OUTPUT 1\n" : "#define VELOCITY_OUTPUT 0\n");
        var vertexSrc = VertexGlsl.Replace("#version 460", "#version 460\n" + shadowDefs);
        var vsModule = ShaderCompiler.CreateShaderModule(
            Device.Vk, Device.LogicalDevice, vertexSrc, ShaderStageFlags.VertexBit, "main", "pipeline.vert", debug);

        // HDR-chain switch, step A of 1-4 and mirrored with the DX HDR_CHAIN injection path:
        // injected at compile time, with zero runtime branching.
        // GLSL requires #version to stay first, so macros are injected immediately after it.
        // The shadow pass is depth-only and does not compile an FS.
        // Overlay renders directly to the backbuffer and does not pass through FinalBlit,
        // so HDR_CHAIN is forcibly baked as 0, matching the Metal overlay library.
        // Sprite2D then outputs gamma-encoded color and text skips inverse ACES pre-distortion,
        // giving display-space output that is pixel-identical to the LDR baseline.
        // The linear direct output and inverse ACES under HDR_CHAIN=1 belong only to the Scene-to-FinalBlit encoding-space path and cannot be reused here.
        // Outline2D mask:
        // OUTLINE_MASK=1 makes the FS take the direct outline-color path, with the alpha transparency chain plus clip,
        // sharing the same semantics as DX PSOutlineMask.
        ShaderModule fsModule = default;
        if (!shadowPass)
        {
            var fragmentSrc = FragmentGlsl.Replace("#version 460",
                "#version 460\n" + ((!overlay && Device.HdrSceneColor) ? "#define HDR_CHAIN 1\n" : "#define HDR_CHAIN 0\n")
                // Step 6:
                // DDGI tier selection now prefers Settings.RenderQuality, which can be persisted, and falls back to the static default source when null.
                // This shares the same gate as DdgiEffect.Initialize, ensuring that the main shader variant and atlas resources are created in sync.
                + ((Season.Basic.DeviceServices.BaseApp?.Settings?.RenderQuality?.GlobalIllumination ?? RenderQuality.DefaultGlobalIllumination) == Season.Rendering.GiMode.Ddgi ? "#define DDGI_ENABLED 1\n" : "#define DDGI_ENABLED 0\n") + shadowDefs
                + (outlineMask ? "#define OUTLINE_MASK 1\n" : "#define OUTLINE_MASK 0\n"));
            fsModule = ShaderCompiler.CreateShaderModule(
                Device.Vk, Device.LogicalDevice, fragmentSrc, ShaderStageFlags.FragmentBit, "main", "pipeline.frag", debug);
        }

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

            var bindings = stackalloc VertexInputBindingDescription[2]
            {
                new VertexInputBindingDescription
                {
                    Binding = 0,
                    Stride = 80, // Vertex.Size：3+2+3+4+4+4 floats
                    InputRate = VertexInputRate.Vertex
                },
                new VertexInputBindingDescription
                {
                    Binding = 1,
                    Stride = 80, // InstanceTransformData + MorphWeights
                    InputRate = VertexInputRate.Instance
                }
            };

            var attrs = stackalloc VertexInputAttributeDescription[11]
            {
                new() { Binding = 0, Location = 0, Format = Format.R32G32B32Sfloat,    Offset = 0  },
                new() { Binding = 0, Location = 1, Format = Format.R32G32Sfloat,       Offset = 12 },
                new() { Binding = 0, Location = 2, Format = Format.R32G32B32Sfloat,    Offset = 20 },
                new() { Binding = 0, Location = 3, Format = Format.R32G32B32A32Sfloat, Offset = 32 },
                new() { Binding = 0, Location = 4, Format = Format.R32G32B32A32Sfloat, Offset = 48 },
                new() { Binding = 0, Location = 5, Format = Format.R32G32B32A32Sfloat, Offset = 64 },
                new() { Binding = 1, Location = 6, Format = Format.R32G32B32A32Sfloat, Offset = 0  },
                new() { Binding = 1, Location = 7, Format = Format.R32G32B32A32Sfloat, Offset = 16 },
                new() { Binding = 1, Location = 8, Format = Format.R32G32B32A32Sfloat, Offset = 32 },
                new() { Binding = 1, Location = 9, Format = Format.R32G32B32A32Sfloat, Offset = 48 },
                new() { Binding = 1, Location = 10, Format = Format.R32G32B32A32Sfloat, Offset = 64 },
            };

            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 2,
                PVertexBindingDescriptions = bindings,
                VertexAttributeDescriptionCount = 11,
                PVertexAttributeDescriptions = attrs
            };

            var inputAsm = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList,
                PrimitiveRestartEnable = false
            };

            // Viewport and Scissor use dynamic state and are set by Display after BeginRenderPass.
            var viewportState = new PipelineViewportStateCreateInfo
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                ScissorCount = 1
            };

            // Disable back-face culling when glTF doubleSided=true.
            var rasterizer = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                DepthClampEnable = false,
                RasterizerDiscardEnable = false,
                PolygonMode = PolygonMode.Fill,
                CullMode = shadowPass ? CullModeFlags.None : cullMode,
                FrontFace = FrontFace.Clockwise,
                // Contract clause 4 of 1-5:
                // the shadow pass bakes in constant plus slope-scaled depth bias to remove acne.
                // DX DepthBias uses integer semantics, so here a Vulkan constant factor is used as the closest visual approximation.
                DepthBiasEnable = shadowPass,
                DepthBiasConstantFactor = shadowPass ? RenderQuality.Current.ShadowDepthBias : 0f,
                DepthBiasSlopeFactor = shadowPass ? RenderQuality.Current.ShadowSlopeScaledDepthBias : 0f,
                LineWidth = 1.0f
            };

            var multisample = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit,
                SampleShadingEnable = false
            };

            // Depth:
            // Opaque and Fade write depth with Less, while Transparent skips depth writes and uses LessOrEqual.
            var depthWrite = mode != PipelineMode.Transparent;
            // Contract clause 7 of 2-2:
            // AoExempt NoDepth variants only force depth write to zero and keep all other states literally identical.
            // SceneDepth then keeps the cleared value 1.0 so the GTAO sky branch stays exempt.
            // The shadow pass always writes depth and is unaffected.
            // Outline2D mask does not write depth because the mask itself has no depth semantics and must preserve scene depth for occlusion testing.
            // Overlay loads depth from the DontCare content of the previous backbuffer pass, which is undefined,
            // so both depth testing and writing must be disabled.
            // Android tilers are strict here, while lavapipe and desktop IMR drivers are more tolerant.
            if (!depthWriteOverride || outlineMask || overlay)
                depthWrite = false;
            // Outline2D mask:
            // the mask uses the same geometry and matrices as Scene, so depth values match bit for bit.
            // Less would reject its own surface pixels completely, producing an empty mask and therefore no composite output or visible off-screen outline.
            // LessEqual fixes this:
            // pixels on the same surface pass, while closer foreground occluders still reject, preserving correct occlusion semantics and matching DX outlineMask depth behavior.
            var depthCompare = mode == PipelineMode.Transparent || outlineMask ? CompareOp.LessOrEqual : CompareOp.Less;
            var depthStencil = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = !overlay,
                DepthWriteEnable = depthWrite,
                DepthCompareOp = depthCompare,
                DepthBoundsTestEnable = false,
                StencilTestEnable = false
            };

            // Blend:
            // Opaque disables blending, while Transparent and Fade enable alpha blending.
            bool blendEnable = mode != PipelineMode.Opaque;
            var colorAttachment = new PipelineColorBlendAttachmentState
            {
                BlendEnable = blendEnable,
                SrcColorBlendFactor = BlendFactor.SrcAlpha,
                DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One,
                DstAlphaBlendFactor = BlendFactor.Zero,
                AlphaBlendOp = BlendOp.Add,
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                                 ColorComponentFlags.BBit | ColorComponentFlags.ABit
            };

            // Contract clause 7 of 2-3:
            // velocity, slot 1, never blends.
            // Transparent and Fade also set its write mask to 0,
            // preventing translucent geometry from polluting velocity that does not belong to it,
            // in a 1:1 mirror of the DX IndependentBlend logic.
            var velocityAttachment = new PipelineColorBlendAttachmentState
            {
                ColorWriteMask = mode == PipelineMode.Opaque
                    ? ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                      ColorComponentFlags.BBit | ColorComponentFlags.ABit
                    : 0
            };

            uint blendAttachmentCount;
            PipelineColorBlendAttachmentState* blendAttachments;
            if (shadowPass)
            {
                blendAttachmentCount = 0;
                blendAttachments = null;
            }
            else if (velocity)
            {
                blendAttachmentCount = 2;
                var arr = stackalloc PipelineColorBlendAttachmentState[2] { colorAttachment, velocityAttachment };
                blendAttachments = arr;
            }
            else
            {
                blendAttachmentCount = 1;
                blendAttachments = &colorAttachment;
            }

            var colorBlend = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                LogicOpEnable = false,
                AttachmentCount = blendAttachmentCount,
                PAttachments = blendAttachments
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
                StageCount = shadowPass ? 1u : 2u,
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
                // Bake anchors:
                // shadow goes to the shadow-atlas render pass.
                // velocity goes to the triple-target render pass.
                // OutlineMask goes to the mask render pass, BackbufferCompatible plus depth Load, and must stay dedicated because its format differs from the Scene main render pass.
                // Overlay goes to the backbuffer render pass, renders directly to the backbuffer, and must also stay dedicated because its format differs from the HDR-tier Scene main render pass.
                // Everything else goes to the main render pass.
                RenderPass = shadowPass ? _shadowRenderPass
                    : (overlay ? _overlayRenderPass
                    : (velocity ? _velocityRenderPass
                    : (outlineMask ? _outlineMaskRenderPass : _renderPass))),
                Subpass = 0
            };

            if (Device.Vk.CreateGraphicsPipelines(Device.LogicalDevice, default, 1, in info, null, out var pso) != Result.Success)
                throw new Exception($"vkCreateGraphicsPipelines failed [{mode}]");
            return pso;
        }
        finally
        {
            SilkMarshal.Free(entryPtr);
            Device.Vk.DestroyShaderModule(Device.LogicalDevice, vsModule, null);
            if (fsModule.Handle != 0)
                Device.Vk.DestroyShaderModule(Device.LogicalDevice, fsModule, null);
        }
    }

    // ============================================================
    // Inline GLSL source, matching DX HLSL one to one and reused by the three variants.
    // ============================================================

    const string VertexGlsl = @"#version 460

layout(location = 0) in vec3 inPos;
layout(location = 1) in vec2 inUV;
layout(location = 2) in vec3 inNormal;
layout(location = 3) in vec4 inTangent;
layout(location = 4) in vec4 inJoints;
layout(location = 5) in vec4 inWeights;

layout(location = 6) in vec4 instanceWorld0;
layout(location = 7) in vec4 instanceWorld1;
layout(location = 8) in vec4 instanceWorld2;
layout(location = 9) in vec4 instanceWorld3;
layout(location = 10) in vec4 instanceMorphWeights;

layout(location = 0) out vec3 vWorldPos;
layout(location = 1) out vec2 vUV;
layout(location = 2) out vec3 vNormal;
layout(location = 3) out vec4 vTangent;
layout(location = 4) out vec4 vInstanceColor;  // per-instance text color (renderMode==2)
layout(location = 5) out float vViewDepth;     // 1-5: view-space depth, used for cascade selection, always 0 in the shadow pass
#if VELOCITY_OUTPUT
layout(location = 6) out vec4 vPrevClip;       // 2-3: previous-frame non-jittered clip-space position, used by the PS to compute velocity
#endif

layout(std140, binding = 0) uniform Matrices {
    mat4 world;
    mat4 view;
    mat4 projection;
    // Contract clause 6 of 2-3:
    // history matrices follow the same transpose contract as world, view, and projection.
    // All-zero means not written yet, and the VS falls back from that sentinel.
    mat4 prevWorld;
    mat4 prevViewProjection;
};

layout(std140, binding = 2) uniform MaterialParams {
    vec4 materialColor;
    vec4 emissiveFactor;
    float metallicFactor;
    float roughnessFactor;
    uint useAlbedoMap;
    uint useNormalMap;
    uint useMetallicRoughnessMap;
    uint useAoMap;
    uint useEmissiveMap;
    float alphaCutoff;
    uint alphaMode;
    uint renderMode;
    float padding1;
    uint isInstanced;
    uint isSkinned;
    uint bonePaletteStride;
    uint hasMorphTargets;
    uint morphTargetCount;
    uint morphVertexCount;
    uint hasPrevBones;
    uint hasPrevInstanceWorld;
    uint hasPrevMorph;
    vec4 morphWeights;
};

layout(std140, binding = 3) uniform BoneMatrices {
    mat4 boneMatrices[100];
};

layout(std430, binding = 9) readonly buffer InstanceBoneMatrices {
    mat4 instanceBoneMatrices[];
};

layout(std430, binding = 10) readonly buffer MorphDeltas {
    float morphDeltas[];
};

// Step C of 2-3:
// previous-frame data SSBOs, read by the VS to reconstruct prevClip
layout(std430, binding = 13) readonly buffer PrevBoneMatrices {
    mat4 prevBoneMatrices[];
};
layout(std430, binding = 14) readonly buffer PrevInstanceWorlds {
    mat4 prevInstanceWorlds[];
};
layout(std430, binding = 15) readonly buffer PrevMorphWeights {
    vec4 prevMorphWeights[];
};

layout(std140, binding = 11) uniform TextDrawParams {
    vec2 textAtlasSize;
    float textPxRange;
    float textGlobalAlpha;
    vec4 textBaseColor;
};

#if SHADOW_PASS
// 1-5:
// light-space matrix push constant.
// Raw System.Numerics row-major bytes are pushed directly, contract clause 1, with no chance to transpose.
// GLSL reads them as column-major M^T, so M*v pre-multiplication is required.
// This differs from view and projection, which the CPU already uploads after Transpose and shader code post-multiplies.
layout(push_constant) uniform ShadowPassParams {
    mat4 lightViewProj;
};
#endif

void main() {
    vec4 restPos = vec4(inPos, 1.0);   // 2-3: rest-pose local position, the starting point for prev reconstruction below
    vec4 localPos = restPos;
    vec3 normal = inNormal;

    // Current-frame morph weights.
    // When prev data is not ready, the velocity path reuses them so prev == cur and velocity becomes zero.
    vec4 curMorphW = isInstanced == 1u ? instanceMorphWeights : morphWeights;

    if (hasMorphTargets != 0u && morphTargetCount > 0u) {
        vec3 morphPosDelta = vec3(0.0);
        vec3 morphNormalDelta = vec3(0.0);

        for (uint t = 0u; t < morphTargetCount && t < 4u; ++t) {
            float w = curMorphW[t];
            if (abs(w) < 1e-6)
                continue;

            uint baseIdx = (t * morphVertexCount + uint(gl_VertexIndex)) * 9u;
            morphPosDelta += vec3(morphDeltas[baseIdx], morphDeltas[baseIdx + 1u], morphDeltas[baseIdx + 2u]) * w;
            morphNormalDelta += vec3(morphDeltas[baseIdx + 3u], morphDeltas[baseIdx + 4u], morphDeltas[baseIdx + 5u]) * w;
        }

        localPos.xyz += morphPosDelta;
        normal = normalize(normal + morphNormalDelta);
    }

    float totalWeight = inWeights.x + inWeights.y + inWeights.z + inWeights.w;
    if (isSkinned != 0u && totalWeight > 0.0) {
        vec4 skinnedPos = vec4(0.0);
        vec3 skinnedNormal = vec3(0.0);
        for (int i = 0; i < 4; ++i) {
            float w = inWeights[i];
            if (w > 0.0) {
                int idx = int(inJoints[i]);
                int baseIdx = isInstanced == 1u
                    ? int(gl_InstanceIndex) * int(max(bonePaletteStride, 1u)) + idx
                    : idx;
                mat4 boneMatrix = instanceBoneMatrices[baseIdx];

                skinnedPos    += (localPos * boneMatrix) * w;
                skinnedNormal += (normal   * mat3(boneMatrix)) * w;
            }
        }
        localPos = skinnedPos;
        normal = normalize(skinnedNormal);
    }

    // World matrix:
    // use the per-instance matrix when isInstanced=1, otherwise use b0 world.
    mat4 worldMatrix;
    if (isInstanced == 1u) {
        // DX-style row-vector post-multiply:
        // row vectors are uploaded, so GLSL must transpose back into column-major form.
        worldMatrix = transpose(mat4(instanceWorld0, instanceWorld1, instanceWorld2, instanceWorld3));
    } else {
        worldMatrix = world;
    }

    vec4 worldPos = localPos * worldMatrix;
    vWorldPos = worldPos.xyz;
#if SHADOW_PASS
    // Depth-only path, contract clause 3:
    // the deformation stages remain literally identical to the main path, and only projection is replaced by the light-space matrix.
    // Raw row-major bytes are adapted by pre-multiplication, M^T * v, equivalent to CPU-side pos * M.
    gl_Position = lightViewProj * worldPos;
    vViewDepth = 0.0;
#else
    vec4 viewPos = worldPos * view;
    gl_Position = viewPos * projection;
    vViewDepth = viewPos.z;
#endif

#if VELOCITY_OUTPUT
    // Contract clauses 6 and 8 of 2-3:
    // the sentinel for an unwritten history matrix is all zeros, matching C# default, and Transpose(all-zero) is still all-zero.
    // The fourth column of prevViewProjection, column-major in GLSL, equals the fourth row after C# transpose,
    // which equals the fourth column in the original row-major bytes.
    // A non-zero test on that column determines whether valid history exists.
    // All-zero means no history, while perspective and orthographic matrices always keep that column non-zero.
    vec4 prevClip_ = vec4(0.0);
    if (any(notEqual(prevViewProjection[3], vec4(0.0)))) {
        // 1) prev morph:
        // start again from rest pose rather than adding onto already morphed or skinned localPos.
        // Previous-frame position must be rest + sum(prevW * delta), strictly symmetric with the current path rest + sum(curW * delta).
        // Starting from localPos would produce rest + sum(curW * d) + sum(prevW * d), double-applying morph deformation and breaking velocity.
        // When prev morph is not ready, hasPrevMorph==0, reuse current weights so prev position degrades to current position and morph contributes no velocity.
        vec4 prevLocalPos = restPos;
        if (hasMorphTargets != 0u && morphTargetCount > 0u) {
            vec4 prevW = hasPrevMorph != 0u
                ? (isInstanced == 1u ? prevMorphWeights[gl_InstanceIndex] : prevMorphWeights[0])
                : curMorphW;
            for (uint t = 0u; t < morphTargetCount && t < 4u; ++t) {
                float w = prevW[t];
                if (abs(w) < 1e-6)
                    continue;
                uint baseIdx = (t * morphVertexCount + uint(gl_VertexIndex)) * 9u;
                prevLocalPos.xyz += vec3(morphDeltas[baseIdx], morphDeltas[baseIdx + 1u], morphDeltas[baseIdx + 2u]) * w;
            }
        }

        // 2) prev skinning:
        // follows the same order as current skinning, acting on the same unskinned rest-plus-morph position,
        // while only swapping in prevBoneMatrices.
        // Sentinel rule:
        // prev bone _m33==0 means that entry was not written, so it falls back to the current bone matrix with per-joint tolerance.
        float prevTotalWeight = inWeights.x + inWeights.y + inWeights.z + inWeights.w;
        if (prevTotalWeight > 0.0 && isSkinned != 0u) {
            vec4 prevSkinnedPos = vec4(0.0);
            for (int i = 0; i < 4; ++i) {
                float w = inWeights[i];
                if (w <= 0.0) continue;
                int idx = int(inJoints[i]);
                int baseIdx = isInstanced == 1u
                    ? int(gl_InstanceIndex) * int(max(bonePaletteStride, 1u)) + idx
                    : idx;
                mat4 bm;
                if (hasPrevBones != 0u) {
                    bm = prevBoneMatrices[baseIdx];
                    if (bm[3][3] == 0.0)
                        bm = isInstanced == 1u ? instanceBoneMatrices[baseIdx] : boneMatrices[idx];
                } else {
                    bm = isInstanced == 1u ? instanceBoneMatrices[baseIdx] : boneMatrices[idx];
                }
                prevSkinnedPos += (prevLocalPos * bm) * w;
            }
            prevLocalPos = prevSkinnedPos;
        }

        // 3) prev world:
        // instanced paths use the prevInstanceWorlds storage buffer, while non-instanced paths use b0 prevWorld.
        // Sentinel rule:
        // _m33==0 means not written and falls back to the current worldMatrix.
        mat4 prevWorldMatrix;
        if (isInstanced == 1u && hasPrevInstanceWorld != 0u) {
            prevWorldMatrix = prevInstanceWorlds[gl_InstanceIndex];
            if (prevWorldMatrix[3][3] == 0.0)
                prevWorldMatrix = worldMatrix;
        } else {
            prevWorldMatrix = (prevWorld[3][3] != 0.0) ? prevWorld : worldMatrix;
        }

        prevClip_ = prevLocalPos * prevWorldMatrix * prevViewProjection;
    }
    vPrevClip = prevClip_;
#endif

    // ── Text GPU Instancing: remap unit quad UV → atlas sub-rect ──
    vec4 textColor_ = vec4(1.0);
    if (renderMode == 2u && isInstanced == 1u) {
        uint tBase = gl_InstanceIndex * 12u;
        vec4 uvRect = vec4(
            morphDeltas[tBase       ], morphDeltas[tBase + 1u],
            morphDeltas[tBase + 2u], morphDeltas[tBase + 3u]);
        vec4 glyphColor = vec4(
            morphDeltas[tBase + 4u], morphDeltas[tBase + 5u],
            morphDeltas[tBase + 6u], morphDeltas[tBase + 7u]);
        vec4 metrics = vec4(
            morphDeltas[tBase + 8u], morphDeltas[tBase + 9u],
            morphDeltas[tBase + 10u], morphDeltas[tBase + 11u]);
        textColor_ = metrics.w > 0.5 ? glyphColor : textBaseColor;
        vUV = uvRect.xy + inUV * uvRect.zw;
    } else {
        vUV = inUV;
    }
    vInstanceColor = textColor_;
    vNormal = normalize(normal * mat3(worldMatrix));
    vTangent = inTangent;
}
";

    internal const string FragmentGlsl = @"#version 460

layout(location = 0) in vec3 vWorldPos;
layout(location = 1) in vec2 vUV;
layout(location = 2) in vec3 vNormal;
layout(location = 3) in vec4 vTangent;
layout(location = 4) in vec4 vInstanceColor;  // per-instance text color
layout(location = 5) in float vViewDepth;     // 1-5: view-space depth, used for cascade selection
#if VELOCITY_OUTPUT
layout(location = 6) in vec4 vPrevClip;       // 2-3: previous-frame non-jittered clip-space position
#endif

layout(location = 0) out vec4 outColor;
#if VELOCITY_OUTPUT
layout(location = 1) out vec2 outVelocity;    // 2-3: MRT slot 1 = velocity in UV space
#endif

// 1-2 lighting system:
// unified light structure, 64 bytes, matching C# GpuLight byte for byte.
// See the RenderQuality 1-2 contract.
struct GpuLight {
    vec4 posRange;        // xyz = world position, ignored for directional, w = attenuation radius range, <=0 degenerates to pure 1/d^2
    vec4 colorIntensity;  // xyz = linear color, w = intensity
    vec4 dirType;         // xyz = light direction for spot and directional, w = type, 0=point, 1=spot, 2=directional
    vec4 spotParams;      // x = cosInner, y = cosOuter, precomputed on CPU, z and w reserved
};

layout(std140, binding = 1) uniform SceneLights {
    vec4 cameraPos;
    vec4 ambientParams;   // xyz = ambient color, w = intensity, replacing the old hardcoded 0.5
    // x = lightCount, y = hdrExposure, C# SceneLightParams.Params0.Y, injected every frame by SetLighting from Device.HdrExposure,
    // z = directionalIndex, index of the directional light in lights that casts CSM, -1 when none,
    // w = spotShadowIndex, index of the spotlight in lights that casts the 2D shadowmap, -1 when none
    vec4 params0;
    // Directional lights are already merged into this array, dirType.w=2, with no separate sun field.
    // The lighting loop below dispatches all light types through one unified path.
    GpuLight lights[8];
    // 1-5:
    // shadow matrices and parameters, aligned byte for byte with the 1152-byte C# SceneLightParams layout.
    // Matrices are raw row-major bytes, read by GLSL as M^T, so sampling code must pre-multiply M*v to match CPU-side pos*M.
    mat4 cascadeViewProj[4];   // offset 560: CSM cascade light-space matrices, used by slots 0..2
    mat4 spotShadowViewProj;   // offset 816: spotlight light-space matrix, used by slot 3
    vec4 cascadeSplits;        // offset 880: far view-space boundaries of each cascade, x/y/z used, w reserved
    vec4 shadowParams0;        // offset 896: x=sunEnabled, y=cascadeCount, z=1/atlasSize, w reserved
    vec4 shadowParams1;        // offset 912: x=spotEnabled, y=shadowStrength, z and w reserved
    // Contract clause 6 of 2-3:
    // xy = current-frame subpixel jitter in NDC units, z = 1/screenWidth, w = 1/screenHeight.
    // Injected every frame through the single SetLighting entry point.
    // All-zero means no jitter.
    vec4 velocityParams;       // offset 928
    // Contract clause 4 of 1-7:
    // x = specular intensity multiplier, y = ambient diffuse intensity multiplier,
    // z = diffuse switch, >0.5 uses irradianceSH9, otherwise uses the constant ambient light in ambientParams, strictly one or the other and never both,
    // w = specular switch, >0.5 enables the envCube LOD0 specular term.
    // All-zero means complete fallback to the 1-2 constant ambient-light path.
    vec4 envParams;            // offset 944
    // Contract clause 7 of 1-7:
    // SH9 environment irradiance, xyz = RGB, w reserved.
    // The CPU already pre-multiplies the convolution coefficients A_l, and shader code only performs the 9-term linear combination.
    // Active only when envParams.z > 0.5.
    // Under std140, the vec4 array stride is 16, matching C# SceneLightParams.IrradianceSH9, [InlineArray(9)] Sh9Array, byte for byte.
    vec4 irradianceSH9[9];     // offset 960, ends at 1104 bytes
    // Clause 10 of 2-4, starting at offset 1104:
    // giParams0 = probeGridMin.xyz and spacing,
    // giParams1 = gridXYZ as float and GiIntensity,
    // giParams2 = normalBias, chebyshev, atlasReady, and unused.
    vec4 giParams0;            // offset 1104
    vec4 giParams1;            // offset 1120
    vec4 giParams2;            // offset 1136, ends at 1152 bytes
    // Step B of 2-5, starting at offset 1152:
    // analytic sun and moon disks plus starfield.
    // skyParams0.xyz = sun direction and w = sun disk angular radius.
    // All-zero means the whole StaticCube tier early-outs.
    vec4 skyParams0;           // offset 1152
    vec4 skyParams1;           // offset 1168
    vec4 skyParams2;           // offset 1184
    vec4 skyParams3;           // offset 1200
    vec4 skyParams4;           // offset 1216
    // Step C of 2-5, starting at offset 1232:
    // procedural clouds.
    // cloudLayerA = layer height km, density, coverage, layer thickness km.
    // cloudLayerB = wind offset xy, noise uv scale, and erosion strength.
    // cloudParams0 = base color rgb and layer count w.
    // cloudParams1 = cloud-shadow strength x, silver-lining g, dark-side brightness, and forward-scattering strength.
    vec4 cloudLayerA[3];       // offset 1232
    vec4 cloudLayerB[3];       // offset 1280
    vec4 cloudParams0;         // offset 1328, where w = layer count and is the sole gate for cloud consumption
    vec4 cloudParams1;         // offset 1344
    // Step E of 2-5, starting at offset 1360:
    // aerial-perspective 3D LUT consumption parameters.
    // x = farthest distance in km, >0 enables AP and is the only gate.
    // y = Intensity, where 0 means identity composition.
    vec4 apParams0;            // offset 1360, ends at 1376 bytes
};

layout(std140, binding = 2) uniform MaterialParams {
    vec4 materialColor;
    vec4 emissiveFactor;
    float metallicFactor;
    float roughnessFactor;
    uint useAlbedoMap;
    uint useNormalMap;
    uint useMetallicRoughnessMap;
    uint useAoMap;
    uint useEmissiveMap;
    float alphaCutoff;
    uint alphaMode;
    uint renderMode;
    float padding1;
    uint isInstanced;
    uint isSkinned;
    uint bonePaletteStride;
    uint hasMorphTargets;
    uint morphTargetCount;
    uint morphVertexCount;
    uint hasPrevBones;
    uint hasPrevInstanceWorld;
    uint hasPrevMorph;
    vec4 morphWeights;
};

layout(binding = 4) uniform sampler2D albedoMap;
layout(binding = 5) uniform sampler2D normalMap;
layout(binding = 6) uniform sampler2D metallicRoughnessMap;
layout(binding = 7) uniform sampler2D aoMap;
layout(binding = 8) uniform sampler2D emissiveMap;

// 1-7:
// environment radiance cube on binding 16, single mip.
// When no environment texture exists, bind a 1x1 all-black dummy,
// so this path is always sampleable and envParams.w acts as the switch.
layout(binding = 16) uniform samplerCube envCube;

// Clause 10 of 2-4:
// DDGI irradiance probe atlas on binding 17, rgba16float.
// When not ready, bind 1x1 White.
// Actual sampling is gated by DDGI_ENABLED plus giParams, and compile-time stripping removes it entirely when disabled.
layout(binding = 17) uniform sampler2D ddgiAtlas;

// Step 3 of 2-4:
// DDGI depth-moment atlas on binding 18, rg16float, where x=mean and y=mean^2.
// When not ready, bind 1x1 White.
// Chebyshev visibility testing is gated at runtime by giParams2.y and does not sample when disabled.
layout(binding = 18) uniform sampler2D ddgiDepth;

// Step C of 2-5:
// pre-baked cloud noise on binding 19, rgba8unorm:
// R = low-frequency silhouette FBM, G = Worley puffs, B = high-frequency erosion, A = ultra-low-frequency coverage modulation.
// It is always declared, since layout always includes this slot, and binds 1x1 White when not ready.
// Actual sampling is gated at runtime by cloudParams0.w, the layer count.
// The all-white fallback cannot be treated as usable noise because it would drive density to the maximum.
// The sampler is immutable wrap:
// noise tiles at a fixed period, while wind offsets can push uv outside [0,1].
// Clamp would stretch the outermost column into a motionless stripe across the sky, just like DX s2 wrapSampler avoids.
layout(binding = 19) uniform sampler2D cloudNoise;

// Step E of 2-5:
// aerial-perspective froxel volume on binding 20, 32^3 rgba16float.
// rgb stores accumulated in-scattered radiance from the camera to that distance in linear HDR, and a stores accumulated opacity.
// This is the only 3D slot in the entire pipeline.
// It is always declared and binds a 1x1x1 all-zero dummy when not ready.
// Since a stores opacity rather than transmittance, all-zero is exactly the identity element of the composition formula.
// The apParams0.x gate only saves the sampling cost.
// Three-axis Clamp plus trilinear filtering are provided by the immutable linear-clamp sampler.
layout(binding = 20) uniform sampler3D aerialLut;

layout(std140, binding = 11) uniform TextDrawParams {
    vec2 textAtlasSize;
    float textPxRange;
    float textGlobalAlpha;
    vec4 textBaseColor;
};

const float PI = 3.14159265359;

// Contract clause 7 of 1-7:
// SH9 irradiance evaluation, Ramamoorthi and Hanrahan 2001.
// Basis functions use the unnormalized polynomial form.
// The CPU already pre-multiplies the convolution coefficients A_l * k_i^2 / pi into irradianceSH9,
// so shader code performs only the 9-term linear combination here.
// The return value is E(n)/pi, with the same units as the constant ambient-light path, and can be multiplied directly by albedo.
vec3 EvaluateIrradianceSH9(vec3 n) {
    vec3 result = irradianceSH9[0].rgb;
    result += irradianceSH9[1].rgb * n.y;
    result += irradianceSH9[2].rgb * n.z;
    result += irradianceSH9[3].rgb * n.x;
    result += irradianceSH9[4].rgb * (n.x * n.y);
    result += irradianceSH9[5].rgb * (n.y * n.z);
    result += irradianceSH9[6].rgb * (3.0 * n.z * n.z - 1.0);
    result += irradianceSH9[7].rgb * (n.x * n.z);
    result += irradianceSH9[8].rgb * (n.x * n.x - n.y * n.y);
    return max(result, 0.0);
}

#if DDGI_ENABLED
// Clauses 9 and 10 of 2-4:
// probe irradiance sampling.
// Octahedral decoding strictly mirrors the OctDecode and tile layout in ddgiProbeUpdate.
// Each tile is 8^2, with a 6^2 inner core plus a 1-pixel gutter.
// The absolute center texel is tile*8 + 1 + oct*6, so normalized uv divides directly by atlas size.
// worldPos is offset along the normal by giParams2.x, normalBias, then the 8 neighboring probes are gathered and blended by trilinear weights times cosine-direction weights.
// StaticSampler bilinearly samples each probe's octahedral inner core, with the gutter absorbing seam overflow.
// The result is multiplied by GiIntensity.
// Starting from step 3, giParams2.y > 0.5 enables a Chebyshev variance test per probe by sampling the depth-moment atlas.
// The resulting visibility factor multiplies the weight to suppress light leaks through wall seams, contact regions, and back faces.
// Starting from step 5, invalid probes, tile alpha < 0.5 and back-face hit rate above the threshold in clause 13, are removed from the weighting.
// When all 8 neighbors are rejected, the code falls back to SH9 environment irradiance.
// The implementation matches line by line across all four backends.
vec2 DdgiOctEncode(vec3 dir) {
    vec3 a = abs(dir);
    vec2 p = dir.xy / (a.x + a.y + a.z);
    if (dir.z < 0.0)
        p = (1.0 - abs(vec2(p.y, p.x))) * vec2(p.x >= 0.0 ? 1.0 : -1.0, p.y >= 0.0 ? 1.0 : -1.0);
    return p;
}

// fallback = the diffuse term that would have been used without DDGI,
// the result of choosing between SH9 and constant ambient.
// Step 5 uses it as the fallback for invalid probes.
vec3 SampleProbeIrradiance(vec3 worldPos, vec3 N, vec3 fallback) {
    vec3 gridMin = giParams0.xyz;
    float spacing = giParams0.w;
    vec3 dims = giParams1.xyz;
    vec2 atlasSize = vec2(dims.x * dims.z * 8.0, dims.y * 8.0);
    vec2 oct = DdgiOctEncode(N) * 0.5 + 0.5;

    vec3 wp = worldPos + N * giParams2.x;
    vec3 gc = (wp - gridMin) / spacing - 0.5;
    vec3 base = floor(gc);
    vec3 f = gc - base;

    vec3 sum = vec3(0.0);
    float wsum = 0.0;
    float wraw = 0.0;
    for (int i = 0; i < 8; i++) {
        vec3 off = vec3(float(i & 1), float((i >> 1) & 1), float((i >> 2) & 1));
        vec3 tri = mix(1.0 - f, f, off);
        float w = tri.x * tri.y * tri.z;
        vec3 pi = clamp(base + off, vec3(0.0), dims - 1.0);
        vec3 probePos = gridMin + (pi + 0.5) * spacing;
        float wdir = max(dot(normalize(probePos - worldPos), N), 0.0);
        w *= wdir * wdir + 0.01;
        vec2 tile = vec2(pi.x + pi.z * dims.x, pi.y);
        vec2 uv = (tile * 8.0 + 1.0 + oct * 6.0) / atlasSize;
        // Step 5 validity weighting, clause 13:
        // tile alpha is constant inside each tile as the classification value, so sampling any point inside the tile is enough.
        // Use continuous weighting rather than a hard step threshold.
        // Alpha is the temporal EMA of the classification, and hard gating would amplify borderline probe jitter into visible flicker.
        // wraw accumulates the pure geometric weight before validity so the end of the function can measure how much of this shaded point falls onto valid probes.
        float valid = clamp(textureLod(ddgiAtlas, (tile * 8.0 + 4.0) / atlasSize, 0.0).a, 0.0, 1.0);
        if (giParams2.y > 0.5) {
            vec3 dirPW = wp - probePos;
            float distPW = length(dirPW);
            vec2 octD = DdgiOctEncode(normalize(dirPW)) * 0.5 + 0.5;
            vec2 depAtlasSize = vec2(dims.x * dims.z * 16.0, dims.y * 16.0);
            vec2 uvD = (tile * 16.0 + 1.0 + octD * 14.0) / depAtlasSize;
            vec2 m = textureLod(ddgiDepth, uvD, 0.0).xy;
            float variance = max(m.y - m.x * m.x, 0.0);
            float d2 = distPW - m.x;
            float cheb = distPW <= m.x ? 1.0 : variance / (variance + d2 * d2);
            float cheb3 = cheb * cheb * cheb;
            // Visibility floor:
            // keep 20% indirect light even under full occlusion, preventing AABB proxy over-occlusion,
            // where cheb^3 exaggerates the occlusion volume and could black out walls completely.
            w *= 0.2 + 0.8 * cheb3;
        }
        wraw += w;
        w *= valid;
        sum += textureLod(ddgiAtlas, uv, 0.0).rgb * w;
        wsum += w;
    }
    // Step 5:
    // wsum / wraw is the proportion of this shaded point's interpolated weight that lands on valid probes.
    // It is used to linearly blend between probe irradiance and fallback,
    // which is the diffuse result that would have been used without DDGI.
    // If all 8 neighbors are invalid, including the initial zero-initialized atlas before the first update, the code naturally lands on pure fallback.
    // The transition stays continuous, with no threshold pop or flicker.
    vec3 probeIrr = wsum > 1e-6 ? sum / wsum : vec3(0.0);
    float vfrac = clamp(wsum / max(wraw, 1e-6), 0.0, 1.0);
    return mix(fallback, probeIrr * giParams1.w, vfrac);
}
#endif

// -- Step C of 2-5: procedural clouds, using pre-baked noise plus multi-layer parallax composition, mirrored line by line with DX HLSL --
// Each cloud layer is a horizontal slab at height h.
// Sample points index noise with world XZ in kilometers, because engine world units are meters, hence *0.001.
// Visible clouds and cloud shadows share the same indexing, the same noise, and the same coverage remapping,
// so the cloud you see is the cloud that casts the shadow.

// Density of a single cloud layer at a given world XZ position in km, in the range 0 to 1,
// already including coverage remapping and high-frequency erosion.
float CloudDensityAt(vec2 posKm, int layer)
{
    vec2 uv = (posKm + cloudLayerB[layer].xy) * cloudLayerB[layer].z;
    vec4 n = textureLod(cloudNoise, uv, 0.0);

    // The A channel, ultra-low frequency, modulates the silhouette so cloud-rich regions form large masses instead of being spread uniformly.
    // Real skies cluster clouds into large groups.
    float shape = n.r * mix(1.0, n.a, 0.7);

    // Coverage remapping:
    // linearly remap the values above the threshold back into 0 to 1.
    // Divide by coverage rather than using a fixed slope,
    // so density approaches saturation when coverage approaches 1, overcast skies,
    // and clouds disappear altogether when coverage approaches 0 instead of only thinning out.
    float coverage = cloudLayerA[layer].z;
    float d = clamp((shape - (1.0 - coverage)) / max(coverage, 1e-3), 0.0, 1.0);

    // High-frequency erosion:
    // only carve the edges without thickening the cloud body, so use multiplication.
    // Additive blending would also thicken cloud cores and make them look like cotton balls.
    // Use half Worley puffiness and half high-frequency FBM:
    // Worley shapes cumulus-like clumps, while FBM produces wispy cirrus-style tearing.
    float erode = cloudLayerB[layer].w * (0.5 * n.g + 0.5 * n.b);
    return clamp(d * clamp(1.0 - erode, 0.0, 1.0), 0.0, 1.0);
}

// Cloud shadow:
// visibility of a world point after cloud occlusion along the given light direction, 1 = fully lit, with a lower bound of 1 - cloud shadow strength.
// It shares the same noise, the same coverage remapping, and the same wind offset as visible clouds,
// so the cloud you see is the cloud that casts the shadow.
//
// Use plane intersection rather than spherical-shell intersection, leaving CloudLayerHitKm to handle the visible-cloud path.
// Here the ray starts near the ground and ends at cloud-layer height, so travel distance is only a few kilometers.
// Curvature correction is far smaller than one texel of the noise.
// The plane solution is one division, while the spherical-shell solution needs a square root.
// This function runs for every directional light and every pixel, so the savings are real.
// Keep a single exit and preinitialize the result so behavior stays isomorphic with DX, where fxc treats multiple exits in called functions as potentially uninitialized.
float ComputeCloudShadow(vec3 worldPos, vec3 toLight)
{
    float result = 1.0;
    int count = int(cloudParams0.w);
    if (count > 0 && cloudParams1.x > 0.0 && toLight.y > 0.0)
    {
        vec2 originKm = worldPos.xz * 0.001;

        // Clamp the lower bound of toLight.y to 0.05, about 3 degrees.
        // When a celestial body hugs the horizon, the light ray becomes nearly horizontal and a plane solution would push the sample point hundreds of kilometers away into unrelated clouds.
        // At that point direct light has already been reduced close to zero by atmospheric transmittance,
        // using the mean transmittance over the disk, see SkyLighting.EvaluateDiskTransmittance,
        // so this clamp introduces no visible error.
        float invY = 1.0 / max(toLight.y, 0.05);

        float tau = 0.0;
        for (int i = 0; i < count; i++)
        {
            // Layer height is measured relative to the observer, while observer altitude is already folded into Atmosphere.ViewAltitudeKm.
            // This therefore uses world y=0 as the observer plane, with engine world units converted from meters by *0.001.
            // Scene height differences of a few dozen meters affect the projected position of clouds a few kilometers high by less than one noise texel,
            // but the subtraction is still kept so cloud shadows disappear naturally when flying or climbing through the cloud layer instead of following the camera into the sky.
            float hKm = max(cloudLayerA[i].x - worldPos.y * 0.001, 0.0);
            vec2 posKm = originKm + toLight.xz * (hKm * invY);
            tau += CloudDensityAt(posKm, i) * cloudLayerA[i].w * cloudLayerA[i].y * invY;
        }

        // The intensity knob follows the same shape as shadowParams1.y in 1-5:
        // interpolate between fully lit and physical transmittance.
        // cloudParams1.x=1 gives the physical value, and =0 turns cloud shadows completely off with zero residual.
        result = 1.0 - cloudParams1.x * clamp(1.0 - exp(-tau), 0.0, 1.0);
    }
    return result;
}

// -- Step B of 2-5: analytic sun and moon disks plus procedural starfield, mirrored line by line with DX HLSL --
// These three features are intentionally excluded from the Sky-View LUT.
// Each LUT texel spans about 1.4 degrees, while the sun disk is only 0.53 degrees across.
// Putting the disk into the LUT would only produce a bright square whose energy is diluted by about (0.53/1.4)^2 and flickers one texel at a time as the body moves.
// All data comes from skyParams0 through skyParams4, see the SceneLightParams header.
// The CPU already multiplies disk radiance by the mean transmittance over the disk,
// the same evaluation used for direct lighting,
// so the disk in the sky and the lighting on the ground fade together at the same rate.

// Integer bit-mixing hash in the style of an xxhash finalizer.
// Do not use fract(sin(...)) style hashes:
// they depend on sine precision at large arguments, produce inconsistent star maps across drivers and compilers,
// and generate moire striping in high-frequency regions.
uint StarHash(uvec3 v)
{
    uint h = v.x * 1597334677u ^ v.y * 3812015801u ^ v.z * 2654435761u;
    h ^= h >> 15u; h *= 2246822519u;
    h ^= h >> 13u; h *= 3266489917u;
    h ^= h >> 16u;
    return h;
}

// Map one 16-bit slice of the hash into [0,1).
// Different shifts pull disjoint bit ranges so the random draws stay independent.
// Multiplying h by a constant and then taking the low bits would be wrong:
// multiplication mixes low bits poorly and makes the jittered x and y visibly correlated into diagonal streaks.
float StarSlice(uint h, uint shift)
{
    return float((h >> shift) & 0xFFFFu) * (1.0 / 65536.0);
}

// Convert a direction into a cube-face index plus in-face uv in [0,1]^2.
// Use a cube instead of a latitude-longitude grid:
// the latter degenerates into thin strips at the zenith and nadir and would align stars into radial artifacts around the poles.
void StarFaceUv(vec3 d, out uint face, out vec2 uv)
{
    vec3 a = abs(d);
    if (a.x >= a.y && a.x >= a.z)  { uv = vec2(d.z, d.y) / a.x; face = d.x > 0.0 ? 0u : 1u; }
    else if (a.y >= a.z)           { uv = vec2(d.x, d.z) / a.y; face = d.y > 0.0 ? 2u : 3u; }
    else                           { uv = vec2(d.x, d.y) / a.z; face = d.z > 0.0 ? 4u : 5u; }
    uv = clamp(uv * 0.5 + 0.5, 0.0, 1.0);   // Clamp into [0,1]: floating-point overflow at t = +/-1 could otherwise make the floor below pick cell -1
}

// Additional radiance from celestial disks plus the starfield, in linear HDR.
// It is added on top of the Sky-View LUT rather than replacing it.
// The LUT represents in-scattering along the view ray, while this function represents direct radiance from celestial bodies and stars reaching the observer through the atmosphere.
// Physically, the two are additive.
// pxAng is the per-pixel angular size in radians, passed by the caller.
// Disk edges and star radii both derive from it, so features remain about one pixel wide
// without hardcoding pixel counts or blurring and aliasing as resolution and FOV change.
vec3 SkyCelestialRadiance(vec3 dir, float pxAng)
{
    vec3 L = vec3(0.0);

    // -- Sun disk:
    // the test is dot(dir, sunDir) > cos(angular radius), which is the second consumption site of Atmosphere.SunAngularRadiusDeg.
    // Anti-alias width conversion:
    // the slope of cos at the disk edge is -sin(angular radius), so the cos increment for 1 pixel is pxAng * sin.
    float sunSin = sqrt(max(1.0 - skyParams0.w * skyParams0.w, 1e-12));
    float aaSun = pxAng * sunSin;
    float sunMask = smoothstep(skyParams0.w - aaSun, skyParams0.w + aaSun, dot(dir, skyParams0.xyz));
    L += skyParams1.xyz * sunMask;

    // -- Moon disk plus phase --
    float cosMoon = dot(dir, skyParams2.xyz);
    float moonSin = sqrt(max(1.0 - skyParams2.w * skyParams2.w, 1e-12));
    float aaMoon = pxAng * moonSin;
    float moonMask = smoothstep(skyParams2.w - aaMoon, skyParams2.w + aaMoon, cosMoon);
    if (moonMask > 0.0)
    {
        // Spherical normal for points inside the disk, the zero-parameter source of moon phase:
        // normalize the tangential offset of the view ray relative to the moon center by the disk radius to get s in [0,1],
        // where 0 is the disk center and 1 is the rim.
        // The normal is tangential * s minus moon-center direction * sqrt(1 - s^2).
        // At the center, the normal faces the observer, which is -moon-center direction.
        // At the rim, the normal is perpendicular to the view direction.
        // This is exactly the geometry of the orthographic projection of a sphere, with no extra parameters needed.
        vec3 tangent = dir - skyParams2.xyz * cosMoon;
        float tanLen = length(tangent);
        float s = clamp(tanLen / moonSin, 0.0, 1.0);
        vec3 tDir = tanLen > 1e-8 ? tangent / tanLen : vec3(1.0, 0.0, 0.0);
        vec3 nrm = tDir * s - skyParams2.xyz * sqrt(max(1.0 - s * s, 0.0));

        // The moon surface is lit by the sun, so the cosine of incidence becomes the lunar phase and evolves automatically with sunDir and moonDir.
        // No explicit phase parameter or art curve is needed.
        // nrm is the negative outward normal, pointing toward the observer, while sunDir is the propagation direction,
        // so the two negatives cancel and the dot product is taken positive here.
        // The square root is a cheap approximation of strong lunar backscatter, the opposition surge.
        // Pure Lambert would make the full moon noticeably darken toward the edge, while the real full moon looks close to a uniformly bright disk.
        // The lower bound of 0.015 models earthshine, Earth light reflected onto the dark side of the moon,
        // at about 1.5 percent of the full-moon brightness.
        // That is what produces the visible dark disk around a thin crescent, not an artistic light boost.
        float lit = max(sqrt(clamp(dot(nrm, skyParams0.xyz), 0.0, 1.0)), 0.015);
        L += skyParams3.xyz * (moonMask * lit);
    }

    // -- Procedural starfield:
    // skyParams1.w already contains twilight visibility derived from StarVisibilityTwilightDeg and is always 0 during daytime. --
    if (skyParams1.w > 0.0)
    {
        // Rotate backward into the starfield-fixed frame before drawing randoms.
        // The star map is pinned in that frame, so StarRotation produces whole-sky sidereal motion
        // instead of rerolling stars every frame and making the sky flicker.
        // Use skyParams4.xyz as the celestial-pole axis rather than world +Y:
        // only rotation around the celestial pole produces real east-rise west-set motion and circumpolar stars.
        // This is an inverse Rodrigues rotation, angle -theta so the cross term is negated.
        // The CPU already normalizes the axis, but a final length fallback is still applied:
        // if the axis is zero, meaning not injected, fall back to +Y instead of collapsing dir into cos(theta)*dir and flipping it.
        vec3 axis = dot(skyParams4.xyz, skyParams4.xyz) > 1e-8 ? normalize(skyParams4.xyz) : vec3(0.0, 1.0, 0.0);
        float ca = cos(skyParams3.w);
        float sa = sin(skyParams3.w);
        vec3 sd = dir * ca - cross(axis, dir) * sa + axis * (dot(axis, dir) * (1.0 - ca));

        uint face;
        vec2 uv;
        StarFaceUv(sd, face, uv);

        const float gridN = 96.0;        // 6 * 96^2 is about 55k cells
        const float starDensity = 0.1;   // about 5.5k stars, comparable to the roughly 6k naked-eye stars in the full sky
        vec2 g = uv * gridN;
        vec2 ci = floor(g);
        vec2 cf = g - ci;

        uint h = StarHash(uvec3(uint(ci.x), uint(ci.y), face));
        if (StarSlice(h, 0u) < starDensity)
        {
            uint hj = StarHash(uvec3(h, 0x9E3779B9u, 1u));
            uint hm = StarHash(uvec3(h, 0x85EBCA6Bu, 2u));

            // Jitter the star position inside the cell while leaving a 0.15 margin.
            // This keeps stars from crossing cell boundaries and avoids adjacent cells each drawing half a star, which would reveal the grid.
            vec2 pos = vec2(0.15 + 0.7 * StarSlice(hj, 0u), 0.15 + 0.7 * StarSlice(hj, 16u));

            // Angular size per cell:
            // the analytic form stays continuous everywhere, so cube edges stay seam-free.
            // Using fwidth(uv) here would explode into bright lines on the edges.
            // The in-face tangential coordinate t = uv*2 - 1 satisfies tan(theta)=t,
            // so dtheta/dt is about 1/(1+|t|^2), and one cell spans 2/gridN units in t.
            vec2 t = uv * 2.0 - 1.0;
            float radPerCell = (2.0 / gridN) / (1.0 + dot(t, t));
            float distRad = length(cf - pos) * radPerCell;
            float star = 1.0 - smoothstep(pxAng * 0.5, pxAng * 1.8, distRad);

            // Magnitude power law:
            // dim stars vastly outnumber bright ones, and cubing a uniform random makes the brightest tenth carry most of the flux.
            float mag = StarSlice(hm, 0u);
            float weight = mag * mag * mag;

            // Color-temperature randomization:
            // warm K/M types versus cool O/B types.
            // The range is intentionally small because real stars have low saturation.
            vec3 tint = mix(vec3(1.0, 0.92, 0.82), vec3(0.82, 0.9, 1.0), StarSlice(hm, 16u));

            // Fade near the horizon over about 3 degrees.
            // Ground geometry and horizon glow own that region, and drawing stars there would only make them clip through the ground.
            L += skyParams1.w * weight * star * tint * clamp(dir.y * 20.0, 0.0, 1.0);
        }
    }

    return L;
}

// -- Visible-cloud consumption for step C of 2-5, mirrored line by line with DX HLSL.
// See CloudDensityAt and ComputeCloudShadow above. --

// Distance in km from the view ray to the intersection with cloud layer number layer.
// Use spherical-shell intersection rather than a plane:
// the plane approximation t = h / dir.y diverges near the horizon and stretches clouds into infinitely long streaks.
// The spherical-shell solution converges to sqrt(2Rh) as dir.y approaches 0.
// With R=6360 and h=1.6 km, that is about 142 km,
// which is exactly why horizon clouds collapse into a band and why lower clouds move faster than higher ones when looking upward, the parallax effect.
// The observer is at (0,R,0) with the planet center at the origin.
// Solve the positive root of |p + t*d| = R + h.
// R comes from skyParams4.w, the CPU-side GroundRadiusKm plus ViewAltitudeKm.
// The result is meaningful only for dir.y > 0, since downward rays hit the far side through the planet, so callers must gate beforehand.
float CloudLayerHitKm(vec3 dir, float layerAltKm)
{
    float r = max(skyParams4.w, 1.0);
    float b = r * dir.y;
    return -b + sqrt(max(b * b + 2.0 * r * layerAltKm + layerAltKm * layerAltKm, 0.0));
}

// Forward-scattering silver lining for clouds, normalized into 0 to 1 where straight forward = 1.
// Use the Henyey-Greenstein shape rather than pow(cos):
// g is cloudParams1.y, the same knob used on the CPU side,
// and self-normalization avoids duplicating the peak constant as a second source of truth.
float CloudSilverLining(float cosTheta, float g)
{
    float g2 = g * g;
    float dn = max(1.0 + g2 - 2.0 * g * cosTheta, 1e-4);
    float p = (1.0 - g2) / (dn * sqrt(dn));
    float dp = max(1.0 + g2 - 2.0 * g, 1e-4);
    float peak = (1.0 - g2) / (dp * sqrt(dp));
    return clamp(p / max(peak, 1e-6), 0.0, 1.0);
}

// Composite clouds into sky radiance, used only by the renderMode==3 branch.
// Ordering matters:
// clouds live in front of all sky components.
// The Sky-View LUT is in-scattering at effectively infinite distance, and the sun and moon disks and stars are as well.
// So the code performs per-layer over compositing first and then attenuates the sky behind it by the accumulated transmittance.
// That is what makes clouds naturally occlude the sun disk and stars.
// Layer order is height order:
// when dir.y > 0, higher layers intersect farther away, and the CPU fills them in ascending height order.
vec3 CloudComposite(vec3 skyRadiance, vec3 dir, vec2 camXZKm)
{
    vec3 acc = vec3(0.0);
    float trans = 1.0;

    // Forward scattering is computed only against the sun.
    // At moonlight levels the silver lining is invisible, so evaluating another HG term would just waste work.
    float fwd = cloudParams1.w * CloudSilverLining(dot(dir, skyParams0.xyz), cloudParams1.y);

    // Fade near the horizon over about 1.4 degrees, following the same clamp(dir.y*20) pattern as the starfield.
    // Without it, dir.y=0 would leave a hard edge that is especially obvious in scenes without ground geometry.
    float horizonFade = clamp(dir.y * 40.0, 0.0, 1.0);

    int count = int(cloudParams0.w);
    for (int i = 0; i < count; ++i)
    {
        float tKm = CloudLayerHitKm(dir, cloudLayerA[i].x);
        float d = CloudDensityAt(camXZKm + dir.xz * tKm, i);

        // Oblique path length:
        // the flatter the view ray, the longer the geometric travel distance through the same layer.
        // The denominator floor of 0.05, about 3 degrees, hands off to the natural spherical-shell convergence below that.
        // Without it, the horizon band would harden into a black wall.
        float tau = d * cloudLayerA[i].w * cloudLayerA[i].y / max(dir.y, 0.05);
        float alpha = clamp(1.0 - exp(-tau), 0.0, 1.0) * horizonFade;

        // Self-occlusion proxy with zero extra taps:
        // optically thicker cloud centers get darker while edges get brighter, matching the appearance of cumulus clouds seen from below.
        // The true solution would need several extra resampling steps along the light ray, and that cost is left for a later quality tier.
        float lit = clamp(1.0 - d, 0.0, 1.0);
        vec3 radiance = cloudParams0.rgb * mix(cloudParams1.z, 1.0, lit) * (1.0 + fwd);

        acc += trans * alpha * radiance;
        trans *= 1.0 - alpha;
    }

    return skyRadiance * trans + acc;
}

float msdfMedian(float r, float g, float b) {
    return max(min(r, g), min(max(r, g), b));
}

#if HDR_CHAIN
// Closed-form inverse of ACES, the Narkowicz 2015 fit:
// y = x(2.51x+0.03) / (x(2.43x+0.59)+0.14), solved as a quadratic and taking the positive root.
// Used for inverse compensation of text:
// pre-distort into linear scene space so that the full FinalBlit chain, exposure plus ACES plus gamma,
// reconstructs the design color exactly.
// The curve has an asymptote near y ~= 1.033, so input is clamped below 1 first to keep the discriminant well-behaved.
vec3 AcesFilmInv(vec3 y)
{
    y = min(y, vec3(0.999));
    vec3 A = 2.51 - 2.43 * y;
    vec3 B = 0.03 - 0.59 * y;
    return (-B + sqrt(B * B + 4.0 * A * (0.14 * y))) / (2.0 * A);
}
#endif

float DistributionGGX(vec3 N, vec3 H, float roughness) {
    float a = roughness * roughness;
    float a2 = a * a;
    float NdotH = max(dot(N, H), 0.0);
    float NdotH2 = NdotH * NdotH;
    float denom = (NdotH2 * (a2 - 1.0) + 1.0);
    denom = PI * denom * denom;
    return a2 / max(denom, 0.0001);
}

float GeometrySchlickGGX(float NdotV, float roughness) {
    float r = (roughness + 1.0);
    float k = (r * r) / 8.0;
    return NdotV / (NdotV * (1.0 - k) + k);
}

float GeometrySmith(vec3 N, vec3 V, vec3 L, float roughness) {
    float NdotV = max(dot(N, V), 0.0);
    float NdotL = max(dot(N, L), 0.0);
    return GeometrySchlickGGX(NdotV, roughness) * GeometrySchlickGGX(NdotL, roughness);
}

vec3 FresnelSchlick(float cosTheta, vec3 F0) {
    cosTheta = clamp(cosTheta, 0.0, 1.0);
    return F0 + (1.0 - F0) * pow(1.0 - cosTheta, 5.0);
}

// Cook-Torrance direct-light contribution for a single light.
// Under the 1-2 contract, the formula is kept literally identical across all four backends.
// radiance already includes intensity times attenuation times spotlight cone shaping.
vec3 EvaluatePbrLight(vec3 N, vec3 V, vec3 L, vec3 albedo, float metallic, float roughness, vec3 F0, vec3 radiance) {
    vec3 H = normalize(V + L);

    float NDF = DistributionGGX(N, H, roughness);
    float G = GeometrySmith(N, V, L, roughness);
    vec3 F = FresnelSchlick(max(dot(H, V), 0.0), F0);

    vec3 numerator = NDF * G * F;
    float denominator = 4.0 * max(dot(N, V), 0.0) * max(dot(N, L), 0.0);
    vec3 specular = numerator / max(denominator, 0.0001);

    vec3 kS = F;
    vec3 kD = (vec3(1.0) - kS) * (1.0 - metallic);

    float NdotL = max(dot(N, L), 0.0);
    return (kD * albedo / PI + specular) * radiance * NdotL;
}

#if SHADOW_ENABLED
// 1-5:
// comparison sampling from the shadow atlas, sampler2DShadow using hardware PCF.
// binding 12 uses the immutable comparison sampler.
// The atlas has four quadrant tiles, slot 0..2 for CSM and slot 3 for the spotlight.
// This mirrors the DX sampling function one to one and keeps a single exit.
layout(binding = 12) uniform sampler2DShadow shadowAtlas;

// Single-tile 3x3 PCF:
// shadowNdc is light-space NDC after dividing by w, and sampling is clamped inside the tile to prevent leakage.
float SampleShadowTile(int slot, vec3 shadowNdc) {
    float result = 1.0;
    vec2 uv = vec2(shadowNdc.x * 0.5 + 0.5, 0.5 - shadowNdc.y * 0.5);
    if (uv.x >= 0.0 && uv.x <= 1.0 && uv.y >= 0.0 && uv.y <= 1.0 &&
        shadowNdc.z > 0.0 && shadowNdc.z < 1.0) {
        float texel = shadowParams0.z;
        vec2 tileOrigin = vec2(slot & 1, slot >> 1) * 0.5;
        vec2 tileMin = tileOrigin + texel * 1.5;
        vec2 tileMax = tileOrigin + 0.5 - texel * 1.5;
        vec2 atlasUV = tileOrigin + uv * 0.5;
        float sum = 0.0;
        for (int dy = -1; dy <= 1; ++dy)
            for (int dx = -1; dx <= 1; ++dx) {
                vec2 sampleUV = clamp(atlasUV + vec2(dx, dy) * texel, tileMin, tileMax);
                sum += texture(shadowAtlas, vec3(sampleUV, shadowNdc.z));
            }
        result = sum / 9.0;
    }
    return result;
}

// Directional light, CSM:
// select the cascade slot by view-space depth, sample after light-space projection, and mix into shadowStrength.
float ComputeSunShadow(vec3 worldPos, float viewDepth) {
    float result = 1.0;
    int cascadeCount = int(shadowParams0.y);
    if (shadowParams0.x >= 0.5 && viewDepth <= cascadeSplits[cascadeCount - 1]) {
        int slot = cascadeCount - 1;
        for (int c = cascadeCount - 1; c >= 0; --c)
            if (viewDepth <= cascadeSplits[c]) slot = c;
        vec4 lightPos = cascadeViewProj[slot] * vec4(worldPos, 1.0);
        float visibility = SampleShadowTile(slot, lightPos.xyz / lightPos.w);
        result = mix(1.0, visibility, shadowParams1.y);
    }
    return result;
}

// Spotlight:
// use slot 3 as a single tile and sample after perspective divide.
float ComputeSpotShadow(vec3 worldPos) {
    float result = 1.0;
    if (shadowParams1.x >= 0.5) {
        vec4 lightPos = spotShadowViewProj * vec4(worldPos, 1.0);
        if (lightPos.w > 0.0) {
            float visibility = SampleShadowTile(3, lightPos.xyz / lightPos.w);
            result = mix(1.0, visibility, shadowParams1.y);
        }
    }
    return result;
}
#endif

#if OUTLINE_MASK
// Outline2D mask:
// outline color comes from the FS push constant at offset 0, written group by group and mirroring the DX b6 root constant.
layout(push_constant) uniform OutlineMaskParams {
    vec4 outlineMaskColor;
};
#endif

void main() {
#if OUTLINE_MASK
    // Mask path, mirroring DX PSOutlineMask:
    // alpha follows the material-transparency chain, including albedo alpha.
    // Values below the threshold are discarded.
    // Color passes through as the group color while alpha stays fixed at 1,
    // so any outline color, including pure black, remains valid.
    // RGB is quantized through the RGBA8 mask RT, matching the final display path.
    float maskAlpha = materialColor.a;
    if (useAlbedoMap != 0u) {
        vec4 maskSample = texture(albedoMap, vUV);
        maskAlpha *= maskSample.a;
    }
    if (alphaMode == 1u && maskAlpha < alphaCutoff)
        discard;
    outColor = vec4(outlineMaskColor.rgb, 1.0);
    return;
#endif
#if VELOCITY_OUTPUT
    // Contract clause 5 of 2-3:
    // this must be initialized unconditionally before any early return.
    // w <= 0 means no history, including all 2D, UI, and text paths, so velocity stays zero.
    outVelocity = vec2(0.0);
    if (vPrevClip.w > 0.0) {
        // curNdc:
        // reconstruct from SV_Position, gl_FragCoord.xy, back into NDC and subtract the current-frame jitter to de-jitter it.
        // prevNdc:
        // obtained by perspective divide of vPrevClip, whose source matrix prevViewProjection is itself non-jittered.
        // velocity = (curNdc - prevNdc) * (0.5, -0.5), converting into UV space with the Y axis flipped.
        vec2 curNdc = gl_FragCoord.xy * velocityParams.zw * vec2(2.0, -2.0) + vec2(-1.0, 1.0);
        curNdc -= velocityParams.xy;
        vec2 prevNdc = vPrevClip.xy / vPrevClip.w;
        outVelocity = (curNdc - prevNdc) * vec2(0.5, -0.5);
    }
#endif
    vec3 albedo = materialColor.rgb;
    float alpha = materialColor.a;
    vec3 metallicRoughness = vec3(0.0, 0.5, 0.0);
    float ao = 1.0;
    vec3 emissive = vec3(0.0);

    // renderMode == 2, TextMsdf:
    // multi-channel signed-distance-field rendering, GPU-instanced version.
    if (renderMode == 2u) {
        vec4 sampledMsdf = texture(albedoMap, vUV);
        float msdfDist = msdfMedian(sampledMsdf.r, sampledMsdf.g, sampledMsdf.b) - 0.5;
        float trueDist = sampledMsdf.a - 0.5;
        float signedDistance = (msdfDist * trueDist > 0.0) ? msdfDist : trueDist;
        float pxRange = max(textPxRange, 1.0);
        vec2 texSize = max(textAtlasSize, vec2(1.0));
        vec2 unitRange = vec2(pxRange / max(texSize.x, 1.0), pxRange / max(texSize.y, 1.0));
        vec2 screenTexSize = max(vec2(1.0) / max(fwidth(vUV), vec2(1e-5)), vec2(1.0));
        float screenPxRange = max(0.5 * dot(unitRange, screenTexSize), 1.0);
        float coverage = clamp(screenPxRange * signedDistance + 0.5, 0.0, 1.0);
#if HDR_CHAIN
        // Inverse ACES compensation, step B of 1-4 and mirrored with DX:
        // pre-distort text color, which is a display-space design color, into a linear scene-space value,
        // so the full FinalBlit chain, times exposure, then ACES, then pow(1/2.2), reconstructs the design color exactly, pixel by pixel inside the glyph.
        // Dividing by exposure makes text exposure-invariant:
        // changing HdrExposure brightens or darkens the scene while text remains unchanged.
        // Fallback:
        // if exposure is read as 0 because the UBO has not been injected by SetLighting, fall back to the neutral exposure 1.0 and avoid blowing up from division by tiny numbers.
        vec3 target = clamp(albedo * vInstanceColor.rgb, 0.0, 1.0);
        float safeExposure = params0.y > 0.0 ? params0.y : 1.0;
        vec3 color = AcesFilmInv(pow(target, vec3(2.2))) / safeExposure;
#else
        vec3 color = albedo * vInstanceColor.rgb;
#endif
        outColor = vec4(color, alpha * vInstanceColor.a * textGlobalAlpha * coverage);
        return;
    }

    // Procedural sky for 2-5:
    // reconstruct Sky-View LUT uv from the world view direction, explicitly ignoring vertex uv.
    // This block must run before the useAlbedoMap sampling block below,
    // or albedo would already be polluted by a vUV lookup.
    // The single source of truth for the parameterization lives in the Season.Rendering.Atmosphere header
    // and is line-for-line identical to the inverse used by the skyView kernel.
    // The seam in u lies on +Z, north, and celestial arcs never pass through north, so the Mie peak never hits the seam.
    // v uses a sqrt fold that concentrates resolution toward the horizon.
    // Uniform v would interpolate color banding there.
    // The LUT is rgba16float with no mip chain, and implicit derivatives at the seam would compute invalid LOD,
    // so this code always uses textureLod(..., 0.0).
    if (renderMode == 3u) {
        vec3 skyDir = normalize(vWorldPos - cameraPos.xyz);
        vec2 skyUv;
        skyUv.x = atan(skyDir.x, -skyDir.z) * (0.5 / PI) + 0.5;
        skyUv.y = 0.5 - 0.5 * sign(skyDir.y) * sqrt(abs(skyDir.y));
        vec3 skyRadiance = textureLod(albedoMap, skyUv, 0.0).rgb * materialColor.rgb;

        // Step B of 2-5:
        // add analytic sun and moon disks plus the starfield.
        // Gate on skyParams0.w > 0.
        // All four fields being zero means this is not the procedural-sky tier.
        // Under the real angular radius, cos is about 0.99999 and never 0,
        // so the StaticCube tier leaves zero residual here.
        // pxAng is computed outside the function and passed in because fwidth is a gradient operation and cannot be buried inside the two non-uniform disk and star branches.
        // The two branch conditions here, renderMode and skyParams0.w, are both UBO constants and therefore uniform to the compiler,
        // just like the existing texture sampling inside the useAlbedoMap branch below.
        if (skyParams0.w > 0.0)
        {
            float pxAng = max(length(fwidth(skyDir)), 1e-6);
            skyRadiance += SkyCelestialRadiance(skyDir, pxAng) * materialColor.rgb;
        }

        // Step C of 2-5:
        // procedural cloud composition.
        // This must happen after celestial disks are added:
        // clouds sit in front of every sky component, so they must be able to occlude the sun disk and stars.
        // The skyRadiance * trans term at the end of CloudComposite is exactly that occlusion.
        // There are two gates:
        // cloudParams0.w, the layer count, which is a UBO constant and also implies that the noise texture is ready,
        // plus dir.y > 0, which is per pixel because downward rays would intersect the far side of the planet and are meaningless here.
        // The code inside uses only textureLod, explicit LOD 0, so this non-uniform branch does not involve implicit derivatives.
        if (cloudParams0.w > 0.0 && skyDir.y > 0.0)
            skyRadiance = CloudComposite(skyRadiance, skyDir, cameraPos.xz * 0.001);
#if HDR_CHAIN
        // The LUT already stores linear HDR radiance.
        // Output it directly and let the full FinalBlit chain, exposure plus ACES plus gamma, close the path under the 1-4 contract.
        outColor = vec4(skyRadiance, alpha);
        return;
#else
        // LDR baseline, and Overlay always takes this branch:
        // gamma-encode in place.
        // max(..., 0) is not a quality hack.
        // Radiance is physically non-negative, but the compiler cannot infer that from sampled value times material color.
        // The explicit clamp prevents undefined pow behavior on negative bases.
        outColor = vec4(pow(max(skyRadiance, vec3(0.0)), vec3(1.0 / 2.2)), alpha);
        return;
#endif
    }

    if (useAlbedoMap != 0u) {
        vec4 sampled = texture(albedoMap, vUV);
        albedo *= sampled.rgb;
        alpha *= sampled.a;
    }

    // alphaMode == 1, MASK:
    // discard when below the threshold.
    if (alphaMode == 1u) {
        if (alpha - alphaCutoff < 0.0) discard;
    }

    // renderMode == 0, Sprite2D:
    // unlit path.
    if (renderMode == 0u) {
#if HDR_CHAIN
        // Step A of 1-4:
        // output directly in pre-encoding space and move gamma into the FinalBlit tonemap variant.
        // This is a pure transport move and stays pixel-identical.
        outColor = vec4(albedo, alpha);
#else
        // Direct gamma output.
        vec3 c = pow(albedo, vec3(1.0 / 2.2));
        outColor = vec4(c, alpha);
#endif
        return;
    }

    if (useMetallicRoughnessMap != 0u) {
        metallicRoughness = texture(metallicRoughnessMap, vUV).rgb;
    } else {
        metallicRoughness.b = metallicFactor;
        metallicRoughness.g = roughnessFactor;
    }

    if (useAoMap != 0u) ao = texture(aoMap, vUV).r;

    if (useEmissiveMap != 0u) {
        emissive = texture(emissiveMap, vUV).rgb;
    } else {
        emissive = emissiveFactor.rgb;
    }

    float metallic = metallicRoughness.b;
    float roughness = metallicRoughness.g;

    vec3 N = normalize(vNormal);
    vec3 T = normalize(vTangent.xyz);
    T = normalize(T - dot(T, N) * N);
    vec3 B = cross(N, T) * vTangent.w;
    mat3 TBN = mat3(T, B, N);

    if (useNormalMap != 0u) {
        vec3 nrm = texture(normalMap, vUV).rgb * 2.0 - 1.0;
        N = TBN * nrm;
    }

    vec3 V = normalize(cameraPos.xyz - vWorldPos);
    vec3 F0 = vec3(0.04);
    F0 = mix(F0, albedo, metallic);

    // Accumulate direct lighting.
    // Under contract 2 of 1-2, directional lights, point lights, and spotlights all live in the same lights array,
    // and one loop dispatches by dirType.w.
    vec3 Lo = vec3(0.0);

    int lightCount = min(int(params0.x), 8);
    int dirShadowIdx = int(params0.z);      // index of the directional light casting CSM, -1 when none
    int spotShadowIdx = int(params0.w);     // index of the spotlight casting the 2D shadowmap, -1 when none
    for (int i = 0; i < lightCount; ++i) {
        float type = lights[i].dirType.w;
        vec3 L;
        vec3 radiance;

        if (type >= 1.5) {
            // Directional light, sun or moon:
            // L is constant with no attenuation, and radiance = color * intensity.
            L = normalize(-lights[i].dirType.xyz);
            radiance = lights[i].colorIntensity.xyz * lights[i].colorIntensity.w;
#if SHADOW_ENABLED
            if (i == dirShadowIdx)
                radiance *= ComputeSunShadow(vWorldPos, vViewDepth);
#endif
            // Step C of 2-5: cloud shadow.
            // Evaluate it independently for every directional light using its own L, so sun and moon cast their own cloud shadows.
            // This differs from CSM, which is limited to the single dirShadowIdx light.
            // Cloud shadows do not consume atlas quadrants and have no one-light limit.
            // They are intentionally not gated by SHADOW_ENABLED:
            // that switch belongs to CSM and the shadow atlas, while cloud shadows are independent of the atlas.
            // Disabling CSM should still let cloud shadows sweep across the ground, which is one of the main lighting features of an overcast sky.
            radiance *= ComputeCloudShadow(vWorldPos, L);
        } else {
            vec3 toLight = lights[i].posRange.xyz - vWorldPos;
            float dist = length(toLight);
            L = toLight / max(dist, 0.0001);

            // Attenuation, contract 3 and aligned with KHR_lights_punctual:
            // when range>0, apply the window-function cutoff;
            // when range<=0, degenerate to pure 1/d^2.
            float attenuation = 1.0 / max(dist * dist, 0.0001);
            float range = lights[i].posRange.w;
            if (range > 0.0) {
                float win = clamp(1.0 - pow(dist / range, 4.0), 0.0, 1.0);
                attenuation *= win * win;
            }

            // Spotlight cone, contract 4:
            // cosine values are precomputed on CPU and the edge is softened with smoothstep.
            if (type > 0.5) {
                attenuation *= smoothstep(lights[i].spotParams.y, lights[i].spotParams.x,
                                          dot(-L, normalize(lights[i].dirType.xyz)));
            }

            radiance = lights[i].colorIntensity.xyz * lights[i].colorIntensity.w * attenuation;
#if SHADOW_ENABLED
            // Spotlight shadow, slot 3:
            // only the spotlight referenced by params0.w participates, aligned with DX and CascadedShadow.ComputeSpot.
            if (i == spotShadowIdx && type > 0.5)
                radiance *= ComputeSpotShadow(vWorldPos);
#endif
        }

        Lo += EvaluatePbrLight(N, V, L, albedo, metallic, roughness, F0, radiance);
    }

    // Ambient light, contract 6 of 1-2:
    // parameterized, with default (0.5, 0.5, 0.5) * 1.0 matching the visual feel of the old hardcoded value.
    // Contract clause 5 of 1-7:
    // choose exactly one of SH9 ambient diffuse or constant ambient light.
    // They share the same units, so adding them would double count.
    // Both paths are gated by (1 - metallic), since metals have no diffuse term.
    // Contract clause 9 of 2-4:
    // choose exactly one diffuse source out of three and never accumulate them.
    // When DDGI is ready and GiIntensity > 0, probe irradiance replaces the SH9-or-constant result.
    // Otherwise the shader fully falls back to the 1-7 and 1-2 path.
    // The specular term is untouched.
    // Clause 13:
    // the probe path continuously blends back to giDiffuse by validity, so giDiffuse also acts as the step-5 fallback.
    vec3 envDiffuse = EvaluateIrradianceSH9(N) * envParams.y;
    vec3 constAmbient = ambientParams.xyz * ambientParams.w;
    vec3 giDiffuse = mix(constAmbient, envDiffuse, step(0.5, envParams.z));
#if DDGI_ENABLED
    if (giParams2.z > 0.5 && giParams1.w > 0.0)
        giDiffuse = SampleProbeIrradiance(vWorldPos, N, giDiffuse);
#endif
    vec3 ambient = giDiffuse * albedo * ao * (1.0 - metallic);

    // Contract clause 6 of 1-7:
    // the specular term samples the radiance cube at LOD0 along the reflected direction.
    // There is no mip chain and no GGX prefiltering,
    // so the term is masked by (1 - roughness)^2, while rough-surface environment energy is carried by the SH9 diffuse term above.
    vec3 R = reflect(-V, N);
    vec3 envSpecular = textureLod(envCube, R, 0.0).rgb * envParams.x;
    float specMask = (1.0 - roughness) * (1.0 - roughness);
    ambient += envSpecular * F0 * specMask * ao * step(0.5, envParams.w);

    vec3 color = ambient + Lo + emissive;

    // Step E of 2-5: aerial-perspective composition.
    // It is intentionally placed in linear HDR space before tonemapping.
    // Atmospheric in-scattering is a real radiance contribution, and compositing it after a tone curve would wash distant blue haze into gray-white.
    // Only the renderMode==1 PBR path reaches here.
    // Sprite2D and TextMsdf both return earlier, so the sky itself is never fogged twice.
    // The z axis uses sqrt(distance / farthestDistance), the inverse of the slicing rule used when baking skyAerial,
    // where slice-center distance = maxDist * ((k+0.5)/N)^2.
    // That keeps slices dense nearby and sparse far away, matching the fact that AP gradients are concentrated within the first few kilometers.
    if (apParams0.x > 0.0)
    {
        vec2 apUv = gl_FragCoord.xy * velocityParams.zw;
        float distKm = length(vWorldPos - cameraPos.xyz) * 0.001;
        float apW = sqrt(clamp(distKm / apParams0.x, 0.0, 1.0));
        vec4 ap = textureLod(aerialLut, vec3(apUv, apW), 0.0);
        color = mix(color, color * (1.0 - ap.a) + ap.rgb, apParams0.y);
    }

#if HDR_CHAIN
    // Step B:
    // output true linear HDR values, with no compression and no encoding.
    // exposure plus ACES plus gamma are closed later in the FinalBlit tonemap variant.
#else
    // Tone mapping and gamma correction, with the LDR baseline using inline Reinhard plus gamma.
    color = color / (color + vec3(1.0));
    color = pow(color, vec3(1.0 / 2.2));
#endif

    outColor = vec4(color, alpha);
}
";
}
