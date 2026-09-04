// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Foundation;
using Metal;
using System.Runtime.CompilerServices;

namespace Season.Platforms.Shared.Apple.Metal;

/// <summary>
/// Render-pipeline modes aligned one to one with PipelineMode on the DX and Vulkan backends:
/// - Opaque:      opaque rendering with blending disabled, DepthWrite=true, and DepthCompare=Less
/// - Transparent: true blend translucency with alpha blending, DepthWrite=false, and DepthCompare=LessEqual
/// - Fade:        whole-model fade in or fade out with alpha blending, DepthWrite=true, and DepthCompare=Less
/// </summary>
internal enum PipelineMode
{
    Opaque,
    Transparent,
    Fade,
}

/// <summary>
/// Metal pipeline set equivalent to DX12 DXPipeline and Vulkan Pipeline:
///   - one IMTLLibrary caching compiled MSL
///   - three IMTLRenderPipelineState variants, Opaque, Transparent, and Fade, reusing the same vertex and fragment function pair
///   - when 2-3 MotionVectors is enabled, an additional three velocity MRT PSOs are created with attachment set = SceneColor + Rg16Float + depth,
///     and Scene pass becomes a permanent three-target pass, so SetPipeline always selects the velocity variant because Metal bakes attachment count and format into the PSO
///   - three IMTLDepthStencilState objects, where Opaque and Fade share write+Less, Transparent uses !write+LessEqual,
///     and OpaqueNoDepthState = !write+Less for the GTAO exemption path in contract clause 7 of 2-2
///   - one static IMTLSamplerState using Linear + ClampToEdge
///   - one MTLVertexDescriptor aligned exactly with the 80-byte stride and 6 attributes in src/Controls/Vertex.cs
///
/// Conversion rules from GLSL and HLSL into MSL:
///   1. `layout(std140, binding=N) uniform Block` → `constant Block& blk [[buffer(N)]]`
///   2. `uniform sampler2D` → `texture2d&lt;float&gt; tex [[texture(N)]]` + `sampler s [[sampler(0)]]`
///   3. `mat3(mat4 M)` → `float3x3(M[0].xyz, M[1].xyz, M[2].xyz)`
///   4. `discard` → `discard_fragment()`
///   5. `mix / clamp / pow` map directly by name
///   6. Post-multiplying matrices as `v * M` is mathematically consistent between GLSL and MSL, with the implicit transpose canceling out, so the original order is preserved
///
/// Buffer-slot contract, with Vertex and Fragment spaces independent:
///   VS: buffer(0)=vertex stream ([[stage_in]]), buffer(1)=Matrices(b0), buffer(2)=instance stream ([[stage_in]])
///       buffer(3)=BoneMatrices(b3), buffer(4)=MaterialParams(b2), buffer(5)=MorphDeltas, buffer(6)=InstanceBoneMatrices
///       buffer(7)=TextDrawParams for Text GPU Instancing, matching VK binding 11
///   FS: buffer(1)=SceneLights(b1), buffer(2)=MaterialParams(b2), buffer(3)=TextDrawParams
///       texture(0..4)=BaseColor / Normal / MR / AO / Emissive
///       texture(5)=shadow atlas for 1-5 when SHADOW_ENABLED, texture(6)=environment radiance cube for 1-7
///       sampler(0)=static sampler shared by PBR maps and the environment cube, sampler(1)=shadow comparison sampler
/// </summary>
internal static class Pipeline
{
    public static IMTLLibrary Library = null!;
    public static IMTLFunction VertexFunction = null!;
    public static IMTLFunction FragmentFunction = null!;

    public static IMTLRenderPipelineState OpaquePipelineState = null!;
    public static IMTLRenderPipelineState TransparentPipelineState = null!;
    public static IMTLRenderPipelineState FadePipelineState = null!;

    public static IMTLDepthStencilState OpaqueDepthState = null!;        // write + Less
    public static IMTLDepthStencilState TransparentDepthState = null!;   // !write + LessEqual

    /// <summary>Contract clause 7 of 2-2: DSS used for the GTAO-exempt path with !write + Less.
    /// On Metal, depth-write behavior lives in IMTLDepthStencilState and is bound dynamically on the encoder rather than baked into the PSO,
    /// so the exemption path does not need a new PSO.
    /// Opaque and Fade reuse their existing PSOs and only switch to this DSS, equivalent to the NoDepth PSO variant on DX and VK.</summary>
    public static IMTLDepthStencilState OpaqueNoDepthState = null!;      // !write + Less

    public static IMTLSamplerState StaticSampler = null!;

    /// <summary>Wrap sampler for cloud noise in step C of 2-5, bound as sampler(2), using Repeat + Linear.
    /// The noise is tileable with a fixed period, and wind offsets can push uv coordinates outside [0,1].
    /// Clamp would stretch the outermost texel row into a static band across the sky, matching the same issue avoided by DX s2 and VK bindings[19] immutable wrap sampling.
    /// This is the only wrap sampler in the whole pipeline, while every other texture uses ClampToEdge through StaticSampler.</summary>
    public static IMTLSamplerState WrapSampler = null!;

    // -- 1-5 shadows. Contract details live in the shared RenderQuality summary.
    //    ShadowsEnabled is finalized during quality-tier initialization, and disabling the feature means zero objects are created. --

    /// <summary>Shadow PSO for 1-5, using a depth-only pipeline with no color attachment and no fragment shader.
    /// CullNone and depth bias remain dynamic encoder state. See SetShadowPipeline.</summary>
    public static IMTLRenderPipelineState ShadowPipelineState = null!;

    /// <summary>Library variant compiled with SHADOW_PASS=1.
    /// MTLShaderCompiler caches by full source string, so define injection automatically becomes a new cache key.</summary>
    public static IMTLLibrary ShadowLibrary = null!;

    public static IMTLFunction ShadowVertexFunction = null!;

    /// <summary>Shadow comparison sampler for 1-5, bound as sampler(1), using CompareFunction=LessEqual plus linear filtering.
    /// Hardware 2x2 bilinear comparison is combined with the shader 3x3 PCF grid, matching the visual behavior of DX SampleCmpLevelZero and VK sampler2DShadow.
    /// ClampToEdge prevents leakage across quadrant boundaries.</summary>
    public static IMTLSamplerState ShadowSampler = null!;

    // -- 2-3 motion vectors. Contract details live in the shared RenderQuality 2-3 clauses.
    //    MotionVectors is finalized during quality-tier initialization, and disabling the feature means zero objects are created. --

    /// <summary>Library variant compiled with VELOCITY_OUTPUT=1.
    /// The vertex shader adds prevClip output, and the fragment shader becomes MRT with slot0=color and slot1=velocity.
    /// It is mutually exclusive with SHADOW_PASS because contract clause 3 requires shadow PSOs to have no color targets.
    /// Since Metal bakes color-attachment count and formats into the PSO, the three-target Scene pass must use the dedicated velocity PSOs defined below.</summary>
    public static IMTLLibrary VelocityLibrary = null!;

    public static IMTLFunction VelocityVertexFunction = null!;
    public static IMTLFunction VelocityFragmentFunction = null!;

    /// <summary>Three velocity MRT PSOs for 2-3, mapping one to one to the regular Opaque, Transparent, and Fade variants.
    /// They reuse the same shader source and differ only in attachment set and slot1 write mask.
    /// When MotionVectors is enabled they are created and become the only PSO family valid for Scene pass,
    /// because Scene pass is then a three-target pass and the regular one-attachment PSOs no longer match its attachment set.
    /// When the feature is disabled they remain null and nothing is created.</summary>
    public static IMTLRenderPipelineState VelOpaquePipelineState = null!;
    public static IMTLRenderPipelineState VelTransparentPipelineState = null!;
    public static IMTLRenderPipelineState VelFadePipelineState = null!;

    // -- Phase 4: Outline2D mask variant.
    //    It is compiled a second time with OUTLINE_MASK=1.
    //    The mask RT is always BackbufferCompatible in BGRA8,
    //    which does not match the RGBA16F attachment format of the main Scene PSO in HDR tiers,
    //    so it must be baked independently, mirroring VK EnsureOutlineMaskPipelines. --

    /// <summary>Library variant compiled with OUTLINE_MASK=1.
    /// MTLShaderCompiler caches by source string, so define injection automatically becomes a new cache key.</summary>
    public static IMTLLibrary OutlineMaskLibrary = null!;

    public static IMTLFunction OutlineMaskVertexFunction = null!;
    public static IMTLFunction OutlineMaskFragmentFunction = null!;

    /// <summary>Outline2D mask PSO using Opaque non-blended output with BGRA8 color and D32 depth.
    /// On Metal, face culling is dynamic encoder state, so one PSO covers both double-sided and single-sided cases,
    /// equivalent to the pair of PSOs used on DX and VK.</summary>
    public static IMTLRenderPipelineState OutlineMaskPipelineState = null!;

    /// <summary>Outline2D mask depth state using !write + LessEqual.
    /// This mirrors the mask-PSO depth configuration on DX and VK.
    /// The mask uses the same geometry and matrices as Scene, so their depth values match bit for bit.
    /// A strict Less test would reject the entire surface and produce an empty mask, while real foreground occluders must still reject it.</summary>
    public static IMTLDepthStencilState OutlineMaskDepthState = null!;

    // -- Overlay-pass specific family, mirroring the overlay PSO families on VK, DX, and WebGPU.
    //    Overlay renders directly to the backbuffer.
    //    In HDR tiers its BGRA8 backbuffer target breaks compatibility with the main PSO RGBA16Float attachment format,
    //    for the same reason as OutlineMask, so it needs its own bake.
    //    This pass also bypasses FinalBlit, so HDR_CHAIN is forced to 0 and baked with LDR output semantics in display space plus gamma encoding. --

    /// <summary>Overlay library variant with HDR_CHAIN forced to 0.
    /// MTLShaderCompiler caches by source string, so in LDR tiers it automatically reuses the same cache entry as the main library when the source matches.</summary>
    public static IMTLLibrary OverlayLibrary = null!;

    public static IMTLFunction OverlayVertexFunction = null!;
    public static IMTLFunction OverlayFragmentFunction = null!;

    /// <summary>Three overlay PSOs baked against BackBufferFormat plus D32 depth.
    /// Depth testing and depth writes are disabled through OverlayDepthState.</summary>
    public static IMTLRenderPipelineState OverlayOpaquePipelineState = null!;
    public static IMTLRenderPipelineState OverlayTransparentPipelineState = null!;
    public static IMTLRenderPipelineState OverlayFadePipelineState = null!;

    /// <summary>Overlay depth state using Always with depth writes disabled.
    /// Depth contents in this pass come from the previous backbuffer pass with DontCare semantics, so they are undefined.
    /// Both testing and writing must therefore be fully disabled, mirroring DepthTestEnable=false on the VK overlay family and always plus no-write on WebGPU.</summary>
    public static IMTLDepthStencilState OverlayDepthState = null!;

    /// <summary>Identity instance buffer of 80 bytes, bound to the per-instance slot at buffer(2) for regular non-instanced draws.</summary>
    public static IMTLBuffer IdentityInstanceBuffer = null!;

    /// <summary>Default TextDrawParams buffer of 32 bytes.
    /// MSL statically declares VS buffer(7) and FS buffer(3),
    /// so even non-text draws need valid fallback bindings to satisfy Metal API Validation.
    /// It is bound once whenever a frame encoder is created.</summary>
    public static IMTLBuffer DefaultTextDrawParamsBuffer = null!;

    public static void Init(MTLPixelFormat colorFormat, MTLPixelFormat depthFormat)
    {
        // HDR-chain switch for 1-4 step A, matching rule 7-3 in the Device class header:
        // inject a leading #define at compile time so runtime stays branch-free.
        // When HDR_CHAIN=1, gamma encoding moves to the FinalBlit tonemap variant and Scene output stays in pre-encoding space.
        // MTLShaderCompiler caches by source string, so injection automatically creates a new cache key.
        // The two 1-5 shadow switches follow contract clause 3:
        // SHADOW_ENABLED controls main-FS PCF sampling according to the quality tier, while SHADOW_PASS selects the depth-only VS variant.
        // Contract clause 3 of 2-3 makes VELOCITY_OUTPUT the only new compile-time switch added to the main shader.
        // Regular variants keep it at 0, leaving runtime branch-free.
        var msl = (Device.HdrSceneColor ? "#define HDR_CHAIN 1\n" : "#define HDR_CHAIN 0\n")
            + (RenderQuality.Current.ShadowsEnabled ? "#define SHADOW_ENABLED 1\n" : "#define SHADOW_ENABLED 0\n")
            // Step 6:
            // DDGI tier selection now prefers Settings.RenderQuality so it can be persisted,
            // while null falls back to the static default source.
            // This matches the gate used by DdgiEffect.Initialize,
            // ensuring the main shader variant and atlas resources are created in sync.
            + ((Season.Basic.DeviceServices.BaseApp?.Settings?.RenderQuality?.GlobalIllumination ?? RenderQuality.DefaultGlobalIllumination) == Season.Rendering.GiMode.Ddgi ? "#define DDGI_ENABLED 1\n" : "#define DDGI_ENABLED 0\n")
            + "#define SHADOW_PASS 0\n#define VELOCITY_OUTPUT 0\n" + MetalShaderSource;
        Library = MTLShaderCompiler.Compile(Device.MtlDevice, msl);
        VertexFunction = Library.CreateFunction("vertex_main")
            ?? throw new Exception("MSL function 'vertex_main' not found");
        FragmentFunction = Library.CreateFunction("fragment_main")
            ?? throw new Exception("MSL function 'fragment_main' not found");

        StaticSampler = CreateStaticSampler();
        // Step C of 2-5:
        // create the cloud-noise wrap sampler at sampler(2) unconditionally.
        // MSL fragment shaders always declare sampler(2),
        // and Metal API Validation requires every pass to bind it validly. See Device.BeginPass for the binding point.
        WrapSampler = CreateWrapSampler();

        // Create the identity instance buffer, 80 bytes = four float4 rows plus morph weights.
        IdentityInstanceBuffer = Device.MtlDevice.CreateBuffer((nuint)Unsafe.SizeOf<InstanceTransformData>(), MTLResourceOptions.CpuCacheModeDefault);
        var identityData = new InstanceTransformData
        {
            Row0 = new Vector4(1, 0, 0, 0),
            Row1 = new Vector4(0, 1, 0, 0),
            Row2 = new Vector4(0, 0, 1, 0),
            Row3 = new Vector4(0, 0, 0, 1),
            MorphWeights = Vector4.Zero,
        };
        unsafe
        {
            *(InstanceTransformData*)IdentityInstanceBuffer.Contents = identityData;
        }

        // Create the default TextDrawParams buffer as the fallback binding for VS buffer(7) and FS buffer(3),
        // together with the default font pxRange value.
        DefaultTextDrawParamsBuffer = Device.MtlDevice.CreateBuffer((nuint)Unsafe.SizeOf<MTLTextDrawParams>(), MTLResourceOptions.CpuCacheModeDefault);
        var defaultTdp = new MTLTextDrawParams
        {
            AtlasSize = Vector2.One,
            PxRange = Season.Fonts.Font.PixelRange,
            GlobalAlpha = 1f,
            TextColor = Vector4.One,
        };
        unsafe
        {
            *(MTLTextDrawParams*)DefaultTextDrawParamsBuffer.Contents = defaultTdp;
        }

        OpaqueDepthState = CreateDepthState(write: true, MTLCompareFunction.Less);
        TransparentDepthState = CreateDepthState(write: false, MTLCompareFunction.LessEqual);
        // Contract clause 7 of 2-2:
        // the GTAO exemption state uses the same depth compare function as Opaque and only disables the write mask.
        OpaqueNoDepthState = CreateDepthState(write: false, MTLCompareFunction.Less);

        var vd = CreateVertexDescriptor();
        OpaquePipelineState = CreatePipelineState(PipelineMode.Opaque, vd, colorFormat, depthFormat);
        TransparentPipelineState = CreatePipelineState(PipelineMode.Transparent, vd, colorFormat, depthFormat);
        FadePipelineState = CreatePipelineState(PipelineMode.Fade, vd, colorFormat, depthFormat);

        // Shadow variant for 1-5, following contract clauses 3 and 4:
        // recompile with SHADOW_PASS=1, reusing the main VS deformation path plus light-space projection.
        // The depth-only PSO has no color attachment and no fragment shader.
        // Metal PSOs do not bake a RenderPass object, so they can be created directly during Init
        // with no Vulkan-style delayed-bake ordering issues.
        if (RenderQuality.Current.ShadowsEnabled)
        {
            var shadowMsl = (Device.HdrSceneColor ? "#define HDR_CHAIN 1\n" : "#define HDR_CHAIN 0\n")
                + "#define SHADOW_ENABLED 1\n#define SHADOW_PASS 1\n#define VELOCITY_OUTPUT 0\n#define DDGI_ENABLED 0\n" + MetalShaderSource;
            ShadowLibrary = MTLShaderCompiler.Compile(Device.MtlDevice, shadowMsl);
            ShadowVertexFunction = ShadowLibrary.CreateFunction("vertex_main")
                ?? throw new Exception("MSL function 'vertex_main' (shadow) not found");

            ShadowSampler = CreateShadowSampler();
            ShadowPipelineState = CreateShadowPipelineState(vd, depthFormat);
        }

        // Velocity variant for 2-3, following contract clauses 1 and 3:
        // MotionVectors is finalized during initialization, with AppDelegate deciding it before Pipeline.Init,
        // so only one shape is ever baked within a single process lifetime.
        // This block compiles only the functions, exposing MSL syntax and UBO layout during Init.
        // The actual velocity PSOs, using three-target MRT plus slot1 write masks, are baked later from those functions.
        if (RenderQuality.Current.MotionVectors)
        {
            var velocityMsl = (Device.HdrSceneColor ? "#define HDR_CHAIN 1\n" : "#define HDR_CHAIN 0\n")
                + (RenderQuality.Current.ShadowsEnabled ? "#define SHADOW_ENABLED 1\n" : "#define SHADOW_ENABLED 0\n")
                // Step 6:
                // same rule as above, preferring Settings.RenderQuality and sharing the same gate as DdgiEffect.
                + ((Season.Basic.DeviceServices.BaseApp?.Settings?.RenderQuality?.GlobalIllumination ?? RenderQuality.DefaultGlobalIllumination) == Season.Rendering.GiMode.Ddgi ? "#define DDGI_ENABLED 1\n" : "#define DDGI_ENABLED 0\n")
                + "#define SHADOW_PASS 0\n#define VELOCITY_OUTPUT 1\n" + MetalShaderSource;
            VelocityLibrary = MTLShaderCompiler.Compile(Device.MtlDevice, velocityMsl);
            VelocityVertexFunction = VelocityLibrary.CreateFunction("vertex_main")
                ?? throw new Exception("MSL function 'vertex_main' (velocity) not found");
            VelocityFragmentFunction = VelocityLibrary.CreateFunction("fragment_main")
                ?? throw new Exception("MSL function 'fragment_main' (velocity) not found");

            // Contract clauses 2 and 7:
            // build the three-target MRT PSOs with slot0 using SceneColor format and slot1 using Rg16Float.
            // Slot1 never blends, and only the Opaque variant enables its write mask, mirroring VK and DX one to one.
            VelOpaquePipelineState = CreatePipelineState(PipelineMode.Opaque, vd, colorFormat, depthFormat, velocity: true);
            VelTransparentPipelineState = CreatePipelineState(PipelineMode.Transparent, vd, colorFormat, depthFormat, velocity: true);
            VelFadePipelineState = CreatePipelineState(PipelineMode.Fade, vd, colorFormat, depthFormat, velocity: true);
        }

        // Overlay-pass specific family, mirroring the overlay PSO families on VK, DX, and WebGPU:
        // Overlay renders directly to the backbuffer above FinalBlit output.
        // In HDR tiers the main PSO RGBA16Float attachment format is incompatible with the BGRA8Unorm backbuffer,
        // and that format mismatch is exactly what causes pink-tinted 2D controls or broken blending if reused incorrectly.
        // Overlay also bypasses FinalBlit, so HDR_CHAIN is forced to 0 during baking:
        // Sprite2D outputs gamma-encoded color directly, and text skips inverse-ACES pre-distortion,
        // making output display-space color that is pixel-equivalent to the LDR baseline.
        // The linear direct output and inverse-ACES behavior under HDR_CHAIN=1 are specific to the Scene-to-FinalBlit path
        // and cannot be reused here.
        var overlayMsl = "#define HDR_CHAIN 0\n"
            + (RenderQuality.Current.ShadowsEnabled ? "#define SHADOW_ENABLED 1\n" : "#define SHADOW_ENABLED 0\n")
            + ((Season.Basic.DeviceServices.BaseApp?.Settings?.RenderQuality?.GlobalIllumination ?? RenderQuality.DefaultGlobalIllumination) == Season.Rendering.GiMode.Ddgi ? "#define DDGI_ENABLED 1\n" : "#define DDGI_ENABLED 0\n")
            + "#define SHADOW_PASS 0\n#define VELOCITY_OUTPUT 0\n" + MetalShaderSource;
        OverlayLibrary = MTLShaderCompiler.Compile(Device.MtlDevice, overlayMsl);
        OverlayVertexFunction = OverlayLibrary.CreateFunction("vertex_main")
            ?? throw new Exception("MSL function 'vertex_main' (overlay) not found");
        OverlayFragmentFunction = OverlayLibrary.CreateFunction("fragment_main")
            ?? throw new Exception("MSL function 'fragment_main' (overlay) not found");
        OverlayOpaquePipelineState = CreatePipelineState(PipelineMode.Opaque, vd, Device.BackBufferFormat, depthFormat, overlay: true);
        OverlayTransparentPipelineState = CreatePipelineState(PipelineMode.Transparent, vd, Device.BackBufferFormat, depthFormat, overlay: true);
        OverlayFadePipelineState = CreatePipelineState(PipelineMode.Fade, vd, Device.BackBufferFormat, depthFormat, overlay: true);
        OverlayDepthState = CreateDepthState(write: false, MTLCompareFunction.Always);

        // Phase 4:
        // Outline2D mask variant compiled a second time with OUTLINE_MASK=1.
        // The mask path shares Scene geometry but outputs group-colored outline mask values,
        // and the fragment shader returns early after following the alpha-transparency chain and clip logic.
        // The PSO is always baked as BGRA8 because the mask RT is BackbufferCompatible,
        // which does not match the main PSO attachment format in HDR tiers.
        // Depth comparison LessEqual with depth writes disabled is handled by OutlineMaskDepthState,
        // since on Metal depth write behavior belongs to the DSS and not the PSO.
        var maskMsl = (Device.HdrSceneColor ? "#define HDR_CHAIN 1\n" : "#define HDR_CHAIN 0\n")
            + (RenderQuality.Current.ShadowsEnabled ? "#define SHADOW_ENABLED 1\n" : "#define SHADOW_ENABLED 0\n")
            // Step 6:
            // same rule as above, preferring Settings.RenderQuality and sharing the same gate as DdgiEffect.
            + ((Season.Basic.DeviceServices.BaseApp?.Settings?.RenderQuality?.GlobalIllumination ?? RenderQuality.DefaultGlobalIllumination) == Season.Rendering.GiMode.Ddgi ? "#define DDGI_ENABLED 1\n" : "#define DDGI_ENABLED 0\n")
            + "#define SHADOW_PASS 0\n#define VELOCITY_OUTPUT 0\n#define OUTLINE_MASK 1\n" + MetalShaderSource;
        OutlineMaskLibrary = MTLShaderCompiler.Compile(Device.MtlDevice, maskMsl);
        OutlineMaskVertexFunction = OutlineMaskLibrary.CreateFunction("vertex_main")
            ?? throw new Exception("MSL function 'vertex_main' (outline mask) not found");
        OutlineMaskFragmentFunction = OutlineMaskLibrary.CreateFunction("fragment_main")
            ?? throw new Exception("MSL function 'fragment_main' (outline mask) not found");
        OutlineMaskPipelineState = CreateOutlineMaskPipelineState(vd, Device.BackBufferFormat, depthFormat);
        OutlineMaskDepthState = CreateDepthState(write: false, MTLCompareFunction.LessEqual);
    }

    public static void Shutdown()
    {
        ShadowPipelineState?.Dispose(); ShadowPipelineState = null!;
        ShadowSampler?.Dispose(); ShadowSampler = null!;
        ShadowVertexFunction?.Dispose(); ShadowVertexFunction = null!;
        ShadowLibrary?.Dispose(); ShadowLibrary = null!;
        VelOpaquePipelineState?.Dispose(); VelOpaquePipelineState = null!;
        VelTransparentPipelineState?.Dispose(); VelTransparentPipelineState = null!;
        VelFadePipelineState?.Dispose(); VelFadePipelineState = null!;
        VelocityVertexFunction?.Dispose(); VelocityVertexFunction = null!;
        VelocityFragmentFunction?.Dispose(); VelocityFragmentFunction = null!;
        VelocityLibrary?.Dispose(); VelocityLibrary = null!;
        OverlayOpaquePipelineState?.Dispose(); OverlayOpaquePipelineState = null!;
        OverlayTransparentPipelineState?.Dispose(); OverlayTransparentPipelineState = null!;
        OverlayFadePipelineState?.Dispose(); OverlayFadePipelineState = null!;
        OverlayDepthState?.Dispose(); OverlayDepthState = null!;
        OverlayVertexFunction?.Dispose(); OverlayVertexFunction = null!;
        OverlayFragmentFunction?.Dispose(); OverlayFragmentFunction = null!;
        OverlayLibrary?.Dispose(); OverlayLibrary = null!;
        OutlineMaskPipelineState?.Dispose(); OutlineMaskPipelineState = null!;
        OutlineMaskDepthState?.Dispose(); OutlineMaskDepthState = null!;
        OutlineMaskVertexFunction?.Dispose(); OutlineMaskVertexFunction = null!;
        OutlineMaskFragmentFunction?.Dispose(); OutlineMaskFragmentFunction = null!;
        OutlineMaskLibrary?.Dispose(); OutlineMaskLibrary = null!;
        OpaquePipelineState?.Dispose(); OpaquePipelineState = null!;
        TransparentPipelineState?.Dispose(); TransparentPipelineState = null!;
        FadePipelineState?.Dispose(); FadePipelineState = null!;
        OpaqueDepthState?.Dispose(); OpaqueDepthState = null!;
        TransparentDepthState?.Dispose(); TransparentDepthState = null!;
        OpaqueNoDepthState?.Dispose(); OpaqueNoDepthState = null!;
        StaticSampler?.Dispose(); StaticSampler = null!;
        WrapSampler?.Dispose(); WrapSampler = null!;
        IdentityInstanceBuffer?.Dispose(); IdentityInstanceBuffer = null!;
        DefaultTextDrawParamsBuffer?.Dispose(); DefaultTextDrawParamsBuffer = null!;
        VertexFunction?.Dispose(); VertexFunction = null!;
        FragmentFunction?.Dispose(); FragmentFunction = null!;
        Library?.Dispose(); Library = null!;
    }

    /// <summary>
    /// Set PSO, DSS, static sampler, and triangle face-culling state on the current RenderCommandEncoder.
    /// Equivalent to DX SetPipelineState plus SetGraphicsRootSignature plus IASetPrimitiveTopology plus RSSetState.
    /// When depthWrite=false, contract clause 7 of 2-2, synchronized with Mesh3D.ExcludeFromAo,
    /// OpaqueNoDepthState is rebound while the PSO remains unchanged because Metal stores depth-write state in the DSS.
    /// SceneDepth therefore keeps the clear value, exempting the GTAO sky branch.
    /// </summary>
    public static void SetPipeline(IMTLRenderCommandEncoder enc, PipelineMode mode, bool doubleSided = false, bool depthWrite = true)
    {
        // Overlay-pass routing, mirroring the first ActivePassId==Overlay check on VK:
        // Overlay renders directly to the backbuffer.
        // In HDR tiers the main PSO RGBA16Float attachment format breaks compatibility with the BGRA8Unorm backbuffer,
        // and that format mismatch is the root cause of pink-tinted 2D controls.
        // The dedicated family is baked against BackBufferFormat with HDR_CHAIN=0 for display-space output,
        // and uses always plus no-write depth because the contents are undefined.
        if (Device.ActivePassId == Season.Rendering.RenderPassId.Overlay)
        {
            var overlayPso = mode switch
            {
                PipelineMode.Transparent => OverlayTransparentPipelineState,
                PipelineMode.Fade => OverlayFadePipelineState,
                _ => OverlayOpaquePipelineState,
            };
            enc.SetRenderPipelineState(overlayPso);
            enc.SetDepthStencilState(OverlayDepthState);
            enc.SetFrontFacingWinding(MTLWinding.Clockwise);
            enc.SetCullMode(doubleSided ? MTLCullMode.None : MTLCullMode.Back);
            enc.SetFragmentSamplerState(StaticSampler, 0);
            return;
        }

        // Phase 4:
        // Outline2D mask path routing, mirroring the first ActivePassId check on VK.
        // The mask PSO is always baked as BGRA8 because it cannot reuse the main PSO in HDR tiers.
        // On Metal, face culling is encoder dynamic state, so one PSO covers both double-sided and single-sided cases,
        // equivalent to the pair of PSOs on DX and VK.
        // LessEqual depth compare with depth writes disabled comes from OutlineMaskDepthState.
        // Mask rendering always requests the Opaque variant.
        if (Device.ActivePassId == Season.Rendering.RenderPassId.OutlineMask)
        {
            enc.SetRenderPipelineState(OutlineMaskPipelineState);
            enc.SetDepthStencilState(OutlineMaskDepthState);
            enc.SetFrontFacingWinding(MTLWinding.Clockwise);
            enc.SetCullMode(doubleSided ? MTLCullMode.None : MTLCullMode.Back);
            enc.SetFragmentSamplerState(StaticSampler, 0);
            return;
        }

        // 2-3:
        // when MotionVectors is enabled, Scene pass permanently becomes a three-target pass.
        // All geometry draws, 3D, Sprite2D, and Texts, all issued inside app.Draw in RenderPass.Execute,
        // must use velocity MRT PSOs or the PSO no longer matches the pass attachment set.
        // The tier is finalized during initialization, so this is a constant branch, mirroring VK SetPipeline one to one.
        var pso = RenderQuality.Current.MotionVectors
            ? mode switch
            {
                PipelineMode.Transparent => VelTransparentPipelineState,
                PipelineMode.Fade => VelFadePipelineState,
                _ => VelOpaquePipelineState,
            }
            : mode switch
            {
                PipelineMode.Transparent => TransparentPipelineState,
                PipelineMode.Fade => FadePipelineState,
                _ => OpaquePipelineState,
            };
        enc.SetRenderPipelineState(pso);

        var dss = mode == PipelineMode.Transparent ? TransparentDepthState
            : depthWrite ? OpaqueDepthState : OpaqueNoDepthState;
        enc.SetDepthStencilState(dss);

        // Match DX and VK: CullMode=Back and FrontFace=CW.
        // glTF loading has already converted RH to LH.
        enc.SetFrontFacingWinding(MTLWinding.Clockwise);
        enc.SetCullMode(doubleSided ? MTLCullMode.None : MTLCullMode.Back);

        // Static sampler at fragment slot 0, shared by all PBR maps.
        enc.SetFragmentSamplerState(StaticSampler, 0);
    }

    /// <summary>
    /// Unified draw entry.
    /// Bind the vertex buffer at slot 0 and the instance buffer at slot 2,
    /// then call DrawIndexedPrimitives according to instanceCount, where 1 means non-instanced and N means instanced.
    /// When instanceBuffer is null, IdentityInstanceBuffer is used automatically.
    /// </summary>
    public static void DrawPrimitive(
        IMTLRenderCommandEncoder enc,
        PrimitiveData primitiveData,
        IMTLBuffer primitiveVertexBuffer,
        IMTLBuffer primitiveIndexBuffer,
        IMTLBuffer matrixBuffer,
        IMTLBuffer materialBuffer,
        IMTLBuffer lightBuffer,
        IMTLBuffer boneBuffer,
        IMTLBuffer morphBuffer,
        IMTLBuffer instanceBoneBuffer,
        MTLPrimitiveType primitiveType,
        nuint indexCount,
        MTLIndexType indexType,
        nuint indexBufferOffset,
        IMTLBuffer instanceBuffer,
        nuint instanceBufferOffset,
        nuint instanceCount,
        nuint instanceBoneBufferOffset,
        IMTLBuffer? prevInstanceBuffer = null,
        IMTLBuffer? prevBoneBuffer = null,
        nuint prevBoneBufferOffset = 0)
    {
        // VS: buffer(0)=[[stage_in]] from vertex descriptor
        enc.SetVertexBuffer(primitiveVertexBuffer, 0, 0); // VB slot 0
        enc.SetVertexBuffer(matrixBuffer, 0, 1);          // b1 Matrices
        enc.SetVertexBuffer(boneBuffer, 0, 3);            // b3 BoneMatrices
        enc.SetVertexBuffer(materialBuffer, 0, 4);        // b2 MaterialParams（VS 读取）
        enc.SetVertexBuffer(morphBuffer, 0, 5);           // b4 MorphDeltas
        enc.SetVertexBuffer(instanceBoneBuffer, instanceBoneBufferOffset, 6); // b5 InstanceBoneMatrices

        // FS
        enc.SetFragmentBuffer(lightBuffer, 0, 1);         // b1 SceneLights
        enc.SetFragmentBuffer(materialBuffer, 0, 2);      // b2 MaterialParams

        // Textures
        var fallback = Device.White;
        enc.SetFragmentTexture((primitiveData.BaseColorTexture ?? fallback).Image, 0);
        enc.SetFragmentTexture((primitiveData.NormalTexture ?? fallback).Image, 1);
        enc.SetFragmentTexture((primitiveData.MetallicRoughnessTexture ?? fallback).Image, 2);
        enc.SetFragmentTexture((primitiveData.OcclusionTexture ?? fallback).Image, 3);
        enc.SetFragmentTexture((primitiveData.EmissiveTexture ?? fallback).Image, 4);

        // Instance buffer, falling back to identity when null.
        var instBuf = instanceBuffer ?? IdentityInstanceBuffer;
        enc.SetVertexBuffer(instBuf, instanceBufferOffset, 2);

        // Contract clause 8(b)(c) of 2-3:
        // previous-state sources. Variants with VELOCITY_OUTPUT=0 do not declare buffer(9) or buffer(10),
        // so extra binding is harmless.
        // The default falls back to current-frame data, and the shader will not consume it while the hasPrev sentinels stay 0.
        enc.SetVertexBuffer(prevInstanceBuffer ?? instBuf, instanceBufferOffset, 9);
        enc.SetVertexBuffer(prevBoneBuffer ?? boneBuffer, prevBoneBufferOffset, 10);

        enc.DrawIndexedPrimitives(primitiveType, indexCount, indexType,
            primitiveIndexBuffer, indexBufferOffset, instanceCount);
    }

    // ============================================================
    // 1-5 Shadow pass, contract clauses 3 and 7
    // ============================================================

    /// <summary>
    /// For 1-5, bind the shadow depth-only PSO, equivalent to DX SetShadowPipelineState and VK SetShadowPipeline.
    /// CullNone follows the same convention as DX and VK shadow PSOs so thin-wall back faces still cast shadows.
    /// Depth bias is set dynamically on the encoder on Metal, equivalent to the DepthBias and SlopeScaledDepthBias baked into DX and VK PSOs.
    /// </summary>
    public static void SetShadowPipeline(IMTLRenderCommandEncoder enc)
    {
        enc.SetRenderPipelineState(ShadowPipelineState);
        enc.SetDepthStencilState(OpaqueDepthState);
        enc.SetFrontFacingWinding(MTLWinding.Clockwise);
        enc.SetCullMode(MTLCullMode.None);
        enc.SetDepthBias(RenderQuality.Current.ShadowDepthBias,
            RenderQuality.Current.ShadowSlopeScaledDepthBias, 0f);
    }

    /// <summary>
    /// For 1-5, push the light-space ViewProj matrix per quadrant into VS buffer(8).
    /// SetVertexBytes copies into the command stream, equivalent to DX root constant b5 and VK push constant.
    /// A shared buffer cannot be overwritten per quadrant because the GPU would only read the last written value during execution.
    /// Raw System.Numerics row-major bytes are adapted in MSL through pre-multiplication, following contract clause 1.
    /// </summary>
    public static unsafe void SetShadowViewProj(IMTLRenderCommandEncoder enc, in Matrix4x4 lightViewProj)
    {
        fixed (Matrix4x4* p = &lightViewProj)
            enc.SetVertexBytes((IntPtr)p, 64, 8);
    }

    /// <summary>
    /// Phase 4:
    /// push the outline color per group for the Outline2D mask through FS buffer(0).
    /// SetFragmentBytes copies into the command stream and avoids creating a buffer.
    /// Only the OUTLINE_MASK variant declares and reads it, mirroring DX b6 root constants and VK FS push constant offset 64.
    /// A shared buffer cannot be overwritten per group because the GPU would only read the last written value during execution.
    /// </summary>
    public static unsafe void SetOutlineMaskColor(IMTLRenderCommandEncoder enc, in Vector4 color)
    {
        fixed (Vector4* p = &color)
            enc.SetFragmentBytes((IntPtr)p, 16, 0);
    }

    /// <summary>
    /// For 1-5, this is the shadow-pass draw entry.
    /// It mirrors <see cref="DrawPrimitive"/> but binds only VS-side resources.
    /// The depth-only empty FS path skips the lighting UBO and all FS textures.
    /// The shadow atlas is being written as a depth attachment at that point,
    /// and the BeginPass baseline already skips texture(5) binding for depth-only, so there is no hazard.
    /// </summary>
    /// <param name="bindMaterial">
    /// false means reusing the material buffer at slot 4 and morph buffer at slot 5 already active on the encoder,
    /// skipping those two SetVertexBuffer calls.
    /// The caller must first verify group consistency through <see cref="MTLPrimitiveGroup.CanShareShadowMaterial"/>
    /// and pass true only for the first primitive in the group, making the first bind a group-level bind.
    /// Nothing else writes vertex slots 4 or 5 inside this pass, so the binding survives across following primitives.
    /// This mirrors DX DrawShadowPrimitive.bindMaterial one to one.
    /// </param>
    public static void DrawShadowPrimitive(
        IMTLRenderCommandEncoder enc,
        PrimitiveData primitiveData,
        IMTLBuffer primitiveVertexBuffer,
        IMTLBuffer primitiveIndexBuffer,
        IMTLBuffer matrixBuffer,
        IMTLBuffer materialBuffer,
        IMTLBuffer boneBuffer,
        IMTLBuffer morphBuffer,
        IMTLBuffer instanceBoneBuffer,
        nuint indexCount,
        MTLIndexType indexType,
        IMTLBuffer instanceBuffer,
        nuint instanceBufferOffset,
        nuint instanceCount,
        nuint instanceBoneBufferOffset,
        bool bindMaterial = true)
    {
        enc.SetVertexBuffer(primitiveVertexBuffer, 0, 0); // VB slot 0
        enc.SetVertexBuffer(matrixBuffer, 0, 1);          // b1 Matrices（shadow 不读 view/proj，保留占位）
        enc.SetVertexBuffer(boneBuffer, 0, 3);            // b3 BoneMatrices
        if (bindMaterial)
        {
            enc.SetVertexBuffer(materialBuffer, 0, 4);    // b2 MaterialParams（VS 读变形标志）
            enc.SetVertexBuffer(morphBuffer, 0, 5);       // b4 MorphDeltas
        }
        enc.SetVertexBuffer(instanceBoneBuffer, instanceBoneBufferOffset, 6); // b5 InstanceBoneMatrices

        var instBuf = instanceBuffer ?? IdentityInstanceBuffer;
        enc.SetVertexBuffer(instBuf, instanceBufferOffset, 2);

        enc.DrawIndexedPrimitives(MTLPrimitiveType.Triangle, indexCount, indexType,
            primitiveIndexBuffer, 0, instanceCount);
    }

    static IMTLSamplerState CreateStaticSampler()
    {
        var desc = new MTLSamplerDescriptor
        {
            MinFilter = MTLSamplerMinMagFilter.Linear,
            MagFilter = MTLSamplerMinMagFilter.Linear,
            MipFilter = MTLSamplerMipFilter.Linear,
            SAddressMode = MTLSamplerAddressMode.ClampToEdge,
            TAddressMode = MTLSamplerAddressMode.ClampToEdge,
            RAddressMode = MTLSamplerAddressMode.ClampToEdge,
            CompareFunction = MTLCompareFunction.Always,
            LodMinClamp = 0,
            LodMaxClamp = float.MaxValue,
        };
        return Device.MtlDevice.CreateSamplerState(desc) ?? throw new Exception("CreateSamplerState failed");
    }

    /// <summary>Wrap sampler for cloud noise in step C of 2-5, using Repeat on all three axes plus Linear filtering, with all remaining parameters matching CreateStaticSampler.</summary>
    static IMTLSamplerState CreateWrapSampler()
    {
        var desc = new MTLSamplerDescriptor
        {
            MinFilter = MTLSamplerMinMagFilter.Linear,
            MagFilter = MTLSamplerMinMagFilter.Linear,
            MipFilter = MTLSamplerMipFilter.Linear,
            SAddressMode = MTLSamplerAddressMode.Repeat,
            TAddressMode = MTLSamplerAddressMode.Repeat,
            RAddressMode = MTLSamplerAddressMode.Repeat,
            CompareFunction = MTLCompareFunction.Always,
            LodMinClamp = 0,
            LodMaxClamp = float.MaxValue,
        };
        return Device.MtlDevice.CreateSamplerState(desc) ?? throw new Exception("CreateSamplerState(wrap) failed");
    }

    /// <summary>Shadow comparison sampler for 1-5 using LessEqual hardware comparison plus linear filtering, where D32 comparison sampling with linear filtering is broadly supported on Metal.</summary>
    static IMTLSamplerState CreateShadowSampler()
    {
        var desc = new MTLSamplerDescriptor
        {
            MinFilter = MTLSamplerMinMagFilter.Linear,
            MagFilter = MTLSamplerMinMagFilter.Linear,
            MipFilter = MTLSamplerMipFilter.NotMipmapped,
            SAddressMode = MTLSamplerAddressMode.ClampToEdge,
            TAddressMode = MTLSamplerAddressMode.ClampToEdge,
            RAddressMode = MTLSamplerAddressMode.ClampToEdge,
            CompareFunction = MTLCompareFunction.LessEqual,
            LodMinClamp = 0,
            LodMaxClamp = 0,
        };
        return Device.MtlDevice.CreateSamplerState(desc) ?? throw new Exception("CreateSamplerState(shadow) failed");
    }

    static IMTLDepthStencilState CreateDepthState(bool write, MTLCompareFunction cmp)
    {
        var desc = new MTLDepthStencilDescriptor
        {
            DepthCompareFunction = cmp,
            DepthWriteEnabled = write,
        };
        return Device.MtlDevice.CreateDepthStencilState(desc) ?? throw new Exception("CreateDepthStencilState failed");
    }

    static MTLVertexDescriptor CreateVertexDescriptor()
    {
        var vd = new MTLVertexDescriptor();

        // Eleven vertex attributes, six per-vertex plus five per-instance.
        SetAttr(vd, 0, MTLVertexFormat.Float3, 0,  0); // pos     vec3
        SetAttr(vd, 1, MTLVertexFormat.Float2, 12, 0); // uv      vec2
        SetAttr(vd, 2, MTLVertexFormat.Float3, 20, 0); // normal  vec3
        SetAttr(vd, 3, MTLVertexFormat.Float4, 32, 0); // tangent vec4
        SetAttr(vd, 4, MTLVertexFormat.Float4, 48, 0); // joints  vec4
        SetAttr(vd, 5, MTLVertexFormat.Float4, 64, 0); // weights vec4

        // Four instance-transform rows plus morph weights, buffer index 2, per-instance.
        SetAttr(vd, 6, MTLVertexFormat.Float4, 0,  2); // instanceWorld0
        SetAttr(vd, 7, MTLVertexFormat.Float4, 16, 2); // instanceWorld1
        SetAttr(vd, 8, MTLVertexFormat.Float4, 32, 2); // instanceWorld2
        SetAttr(vd, 9, MTLVertexFormat.Float4, 48, 2); // instanceWorld3
        SetAttr(vd, 10, MTLVertexFormat.Float4, 64, 2); // instanceMorphWeights

        var vertexLayout = vd.Layouts[0];
        vertexLayout.Stride = 80;
        vertexLayout.StepFunction = MTLVertexStepFunction.PerVertex;
        vertexLayout.StepRate = 1;

        var instanceLayout = vd.Layouts[2];
        instanceLayout.Stride = 80;
        instanceLayout.StepFunction = MTLVertexStepFunction.PerInstance;
        instanceLayout.StepRate = 1;

        return vd;

        static void SetAttr(MTLVertexDescriptor vd, int idx, MTLVertexFormat fmt, nuint offset, nuint bufferIndex)
        {
            var a = vd.Attributes[idx];
            a.Format = fmt;
            a.Offset = offset;
            a.BufferIndex = bufferIndex;
        }
    }

    /// <summary>
    /// Bake the PSO shared by both regular and velocity variants.
    /// When velocity=true, switch to the VELOCITY_OUTPUT=1 function pair
    /// and add ColorAttachments[1] in Rg16Float so the attachment set matches the three-target Scene pass, following Metal platform rule 3.
    /// </summary>
    static IMTLRenderPipelineState CreatePipelineState(PipelineMode mode, MTLVertexDescriptor vd,
        MTLPixelFormat colorFormat, MTLPixelFormat depthFormat, bool velocity = false, bool overlay = false)
    {
        var psd = new MTLRenderPipelineDescriptor
        {
            Label = overlay ? $"Season-Overlay-{mode}" : velocity ? $"Season-Vel-{mode}" : $"Season-{mode}",
            VertexFunction = overlay ? OverlayVertexFunction : velocity ? VelocityVertexFunction : VertexFunction,
            FragmentFunction = overlay ? OverlayFragmentFunction : velocity ? VelocityFragmentFunction : FragmentFunction,
            VertexDescriptor = vd,
            DepthAttachmentPixelFormat = depthFormat,
        };

        var att = psd.ColorAttachments[0];
        att.PixelFormat = colorFormat;

        if (mode == PipelineMode.Opaque)
        {
            att.BlendingEnabled = false;
        }
        else
        {
            att.BlendingEnabled = true;
            att.RgbBlendOperation = MTLBlendOperation.Add;
            att.AlphaBlendOperation = MTLBlendOperation.Add;
            att.SourceRgbBlendFactor = MTLBlendFactor.SourceAlpha;
            att.DestinationRgbBlendFactor = MTLBlendFactor.OneMinusSourceAlpha;
            att.SourceAlphaBlendFactor = MTLBlendFactor.One;
            att.DestinationAlphaBlendFactor = MTLBlendFactor.Zero;
        }

        // Contract clause 7 of 2-3:
        // velocity at slot 1 never blends.
        // Transparent and Fade set the write mask to 0 so translucent geometry does not contaminate velocity that does not belong to it,
        // mirroring VK colorWriteMask and DX IndependentBlend one to one.
        if (velocity)
        {
            var vatt = psd.ColorAttachments[1];
            vatt.PixelFormat = MTLPixelFormat.RG16Float;
            vatt.BlendingEnabled = false;
            vatt.WriteMask = mode == PipelineMode.Opaque ? MTLColorWriteMask.All : MTLColorWriteMask.None;
        }

        var pso = Device.MtlDevice.CreateRenderPipelineState(psd, out NSError? err);
        if (pso == null)
            throw new Exception($"CreateRenderPipelineState [{psd.Label}] failed: {err?.LocalizedDescription ?? "(no NSError)"}");
        return pso;
    }

    /// <summary>
    /// Shadow PSO for 1-5.
    /// It is depth-only, with no color attachment and FragmentFunction=null, which is valid,
    /// and its attachment set matches the depth-only pass, following Metal platform rule 3.
    /// The VS uses the SHADOW_PASS=1 variant, with light-space projection and the matrix received at buffer(8).
    /// </summary>
    static IMTLRenderPipelineState CreateShadowPipelineState(MTLVertexDescriptor vd, MTLPixelFormat depthFormat)
    {
        var psd = new MTLRenderPipelineDescriptor
        {
            Label = "Season-Shadow",
            VertexFunction = ShadowVertexFunction,
            FragmentFunction = null,
            VertexDescriptor = vd,
            DepthAttachmentPixelFormat = depthFormat,
        };

        var pso = Device.MtlDevice.CreateRenderPipelineState(psd, out NSError? err);
        if (pso == null)
            throw new Exception($"CreateRenderPipelineState [Shadow] failed: {err?.LocalizedDescription ?? "(no NSError)"}");
        return pso;
    }

    /// <summary>
    /// Phase 4 Outline2D mask PSO.
    /// It uses a single color attachment, always BGRA8 to match the BackbufferCompatible mask RT, plus a D32 depth attachment.
    /// Opaque mode has no blending.
    /// The VS reuses the main vertex_main, with OUTLINE_MASK affecting only the FS, matching VK.
    /// On Metal, both face culling and depth comparison are encoder dynamic states,
    /// so one PSO covers both double-sided and single-sided cases, equivalent to the two PSOs plus baked depth state on DX and VK.
    /// </summary>
    static IMTLRenderPipelineState CreateOutlineMaskPipelineState(MTLVertexDescriptor vd, MTLPixelFormat colorFormat, MTLPixelFormat depthFormat)
    {
        var psd = new MTLRenderPipelineDescriptor
        {
            Label = "Season-OutlineMask",
            VertexFunction = OutlineMaskVertexFunction,
            FragmentFunction = OutlineMaskFragmentFunction,
            VertexDescriptor = vd,
            DepthAttachmentPixelFormat = depthFormat,
        };
        var att = psd.ColorAttachments[0];
        att.PixelFormat = colorFormat;
        att.BlendingEnabled = false;

        var pso = Device.MtlDevice.CreateRenderPipelineState(psd, out NSError? err);
        if (pso == null)
            throw new Exception($"CreateRenderPipelineState [OutlineMask] failed: {err?.LocalizedDescription ?? "(no NSError)"}");
        return pso;
    }

    // ============================================================
    // Embedded MSL source aligned one to one with Vulkan VertexGlsl and FragmentGlsl
    // ============================================================

    const string MetalShaderSource = @"
#include <metal_stdlib>
#include <simd/simd.h>

using namespace metal;

struct Matrices {
    float4x4 world;
    float4x4 view;
    float4x4 projection;
    // Contract clause 6 of 2-3:
    // previous matrices live at offsets 192 and 256 and follow the same transpose contract as world, view, and projection.
    // The CPU uploads Transpose(M), and MSL reads column-major data back as the engine row-major M,
    // so post-multiplication with v * M remains correct everywhere.
    // All-zero means not written, because the C# MatrixBuffer default is zero and Transpose(zero) is still zero.
    // The regular variants also declare these fields because MatrixBuffer is always 320 bytes on the C# side, so there is no out-of-bounds risk.
    float4x4 prevWorld;
    float4x4 prevViewProjection;
};

struct BoneMatrices {
    float4x4 boneMatrices[100];
};

// 1-2 lighting system:
// unified light structure, 64 bytes, byte-aligned with C# GpuLight.
// The all-float4 layout naturally avoids the float3-alignment slip in MSL constant space, the same root cause as the old padding0 issue.
struct GpuLight {
    float4 posRange;        // xyz=世界位置（directional 忽略）, w=衰减半径 range（<=0 退化纯 1/d²）
    float4 colorIntensity;  // xyz=线性色, w=intensity
    float4 dirType;         // xyz=照射方向（spot/directional 用）, w=类型（0=point, 1=spot, 2=directional）
    float4 spotParams;      // x=cosInner, y=cosOuter（CPU 预算）, zw=保留
};

struct SceneLights {
    float4 cameraPos;
    float4 ambientParams;   // xyz=环境光色, w=强度（替代旧硬编码 0.5）
    // x=lightCount, y=hdrExposure from C# SceneLightParams.Params0.Y, with Device.HdrExposure injected every frame by SetLighting,
    // z=directionalIndex, the index of the directional light that casts CSM in the lights array, or -1 when absent,
    // w=spotShadowIndex, the index of the spotlight that casts the 2D shadow map, or -1 when absent
    float4 params0;
    // Directional lights are already merged into this array with dirType.w=2.
    // There is no separate sun field, and the unified lighting loop below dispatches by type.
    GpuLight lights[8];
    // 1-5 shadow matrices and parameters:
    // byte-aligned with the 1152-byte C# SceneLightParams layout.
    // The matrices are uploaded as raw row-major bytes and read by MSL as M transpose,
    // so sampling sites must adapt with pre-multiplication M * v to match CPU-side pos * M.
    float4x4 cascadeViewProj[4];   // offset 560：CSM 级联光空间矩阵（slot 0..2 用）
    float4x4 spotShadowViewProj;   // offset 816：聚光光空间矩阵（slot 3）
    float4 cascadeSplits;          // offset 880：各级联视空间远界（x/y/z 用，w 保留）
    float4 shadowParams0;          // offset 896：x=sunEnabled, y=cascadeCount, z=1/atlasSize, w=保留
    float4 shadowParams1;          // offset 912：x=spotEnabled, y=shadowStrength, zw=保留
    // Contract clause 6 of 2-3:
    // xy=current-frame subpixel jitter in NDC units, z=1/screenWidth, w=1/screenHeight.
    // Injected once per frame by MTLPrimitiveGroup.SetLighting. All-zero means no jitter.
    float4 velocityParams;         // offset 928
    // Contract clause 4 of 1-7:
    // x=specular intensity scale, y=environment diffuse intensity scale,
    // z=diffuse switch, when greater than 0.5 use irradianceSH9, otherwise use ambientParams constant ambient, never both,
    // w=specular switch, when greater than 0.5 enable the envCube LOD0 specular term.
    // All-zero falls back completely to the 1-2 constant ambient path.
    float4 envParams;              // offset 944
    // Contract clause 7 of 1-7:
    // SH9 environment irradiance, xyz=RGB and w reserved.
    // The CPU already pre-multiplies the convolution coefficients A_l,
    // so this code only performs the 9-term linear combination.
    // It is only valid when envParams.z > 0.5.
    // The float4 array in MSL constant space has stride 16 and is byte-aligned with C# SceneLightParams.IrradianceSH9, the InlineArray of 9 float4 values.
    // Using all float4 also avoids the float3-alignment slip noted on GpuLight.
    float4 irradianceSH9[9];       // offset 960，尾部 1104B
    // DDGI clause 10 of 2-4, starting at offset 1104:
    // giParams0=probeGridMin.xyz and spacing,
    // giParams1=gridXYZ as float and GiIntensity,
    // giParams2=normalBias, chebyshev, atlasReady, and an unused slot.
    float4 giParams0;              // offset 1104
    float4 giParams1;              // offset 1120
    float4 giParams2;              // offset 1136，尾部 1152B
    // Step B of 2-5, starting at offset 1152:
    // analytic sun disk, moon disk, and star field.
    // skyParams0.xyz is the sun direction and w is the sun angular radius.
    // All-zero means the StaticCube tier takes the full early-out path.
    float4 skyParams0;             // offset 1152
    float4 skyParams1;             // offset 1168
    float4 skyParams2;             // offset 1184
    float4 skyParams3;             // offset 1200
    float4 skyParams4;             // offset 1216
    // Step C of 2-5, starting at offset 1232:
    // procedural clouds.
    // cloudLayerA stores layerHeightKm, density, coverage, and layerThicknessKm.
    // cloudLayerB stores windOffsetXY, noiseUvScale, and erosionStrength.
    // cloudParams0 stores baseColorRgb and layerCount in w.
    // cloudParams1 stores cloudShadowStrength in x, silverLining g, dark-region brightness, and forward-scattering strength.
    float4 cloudLayerA[3];         // offset 1232
    float4 cloudLayerB[3];         // offset 1280
    float4 cloudParams0;           // offset 1328（w=层数=云消费唯一门控）
    float4 cloudParams1;           // offset 1344
    // Step E of 2-5, starting at offset 1360:
    // aerial-perspective 3D LUT consumption parameters.
    // x=maxDistanceKm, where values greater than 0 enable AP and act as the only gate.
    // y=Intensity, where 0 means identity composition.
    float4 apParams0;              // offset 1360，尾部 1376B
};

struct MaterialParams {
    float4 materialColor;
    float4 emissiveFactor;
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
    uint hasPrevBones;          // 2-3 条款 8(b)：prev bone palette 可用性哨兵
    uint hasPrevInstanceWorld;  // 2-3 条款 8(c)：prev instance world 流可用性哨兵
    uint hasPrevMorph;          // 2-3 条款 8(c)：prev morph 权重可用性哨兵
    float4 morphWeights;
    float4 prevMorphWeights;    // 非 instanced 路径的上一帧权重（CB 内联，源自 CPU 影子副本）
};

struct TextDrawParams {
    float2 textAtlasSize;
    float textPxRange;
    float textGlobalAlpha;
    float4 textBaseColor;
};

struct VertexIn {
    float3 inPos          [[attribute(0)]];
    float2 inUV           [[attribute(1)]];
    float3 inNormal       [[attribute(2)]];
    float4 inTangent      [[attribute(3)]];
    float4 inJoints       [[attribute(4)]];
    float4 inWeights      [[attribute(5)]];
    float4 instanceWorld0 [[attribute(6)]];
    float4 instanceWorld1 [[attribute(7)]];
    float4 instanceWorld2 [[attribute(8)]];
    float4 instanceWorld3 [[attribute(9)]];
    float4 instanceMorphWeights [[attribute(10)]];
};

struct VertexOut {
    float4 position [[position]];
    float3 vWorldPos;
    float2 vUV;
    float3 vNormal;
    float4 vTangent;
    float4 vInstanceColor;
    float vViewDepth;       // 1-5：视空间深度（级联选择用；shadow pass 恒 0）
#if VELOCITY_OUTPUT
    float4 vPrevClip;       // 2-3：上一帧未抖动 clip 空间位置（FS 侧算 velocity 用）
#endif
};

vertex VertexOut vertex_main(
    VertexIn vin [[stage_in]],
    uint vertexId                   [[vertex_id]],
    uint instanceId                 [[instance_id]],
    constant Matrices& mat           [[buffer(1)]],
    constant BoneMatrices& bones     [[buffer(3)]],
    constant MaterialParams& mparams [[buffer(4)]],
    const device float* morphDeltas  [[buffer(5)]],
    const device float4x4* instanceBoneMatrices [[buffer(6)]],
    constant TextDrawParams& textParams [[buffer(7)]]
#if SHADOW_PASS
    // 1-5 light-space matrix:
    // pushed per quadrant with SetVertexBytes.
    // Raw row-major bytes are read by MSL as M transpose, so pre-multiplication adapts the contract.
    , constant float4x4& lightViewProj [[buffer(8)]]
#endif
#if VELOCITY_OUTPUT
    // Contract clause 8(b)(c) of 2-3:
    // previous-instance stream, 5 float4 values per instance, world rows 0 through 3 plus morph weights,
    // byte-aligned with InstanceTransformData, plus the previous bone palette.
    // Only the velocity variant declares them.
    // Regular and shadow variants neither declare nor bind them, so runtime cost stays zero.
    , const device float4* prevInstanceStream    [[buffer(9)]]
    , const device float4x4* prevBoneMatrices    [[buffer(10)]]
#endif
    )
{
    // 2-3 rest-pose local position:
    // the previous local position must restart from rest pose so morph and skinning replay in the same order.
    // That is why the snapshot must happen before any deformation, matching restPos on VK and DX literally.
    float4 restPos = float4(vin.inPos, 1.0);
    float4 localPos = restPos;
    float3 normal = vin.inNormal;
    float3 tangentXYZ = vin.inTangent.xyz;

    float4 curMorphW = mparams.isInstanced == 1u ? vin.instanceMorphWeights : mparams.morphWeights;
    if (mparams.hasMorphTargets != 0u && mparams.morphTargetCount > 0u) {
        float4 morphWeights = curMorphW;
        for (uint t = 0u; t < mparams.morphTargetCount && t < 4u; ++t) {
            float w = morphWeights[int(t)];
            if (fabs(w) < 1e-6f) continue;
            uint off = (t * mparams.morphVertexCount + vertexId) * 9u;
            localPos.xyz += float3(morphDeltas[off + 0u], morphDeltas[off + 1u], morphDeltas[off + 2u]) * w;
            normal += float3(morphDeltas[off + 3u], morphDeltas[off + 4u], morphDeltas[off + 5u]) * w;
            tangentXYZ += float3(morphDeltas[off + 6u], morphDeltas[off + 7u], morphDeltas[off + 8u]) * w;
        }

        normal = normalize(normal);
        tangentXYZ = normalize(tangentXYZ);
    }

    float totalWeight = vin.inWeights.x + vin.inWeights.y + vin.inWeights.z + vin.inWeights.w;
    if (mparams.isSkinned != 0u && totalWeight > 0.0) {
        float4 skinnedPos = float4(0.0);
        float3 skinnedNormal = float3(0.0);
        float3 skinnedTangent = float3(0.0);
        for (int i = 0; i < 4; ++i) {
            float w = vin.inWeights[i];
            if (w > 0.0) {
                int idx = int(vin.inJoints[i]);
                uint boneIndex = mparams.isInstanced == 1u
                    ? instanceId * max(mparams.bonePaletteStride, 1u) + uint(idx)
                    : uint(idx);
                float4x4 B;
                if (mparams.isInstanced == 1u)
                    B = instanceBoneMatrices[boneIndex];
                else
                    B = bones.boneMatrices[idx];
                float3x3 B3 = float3x3(B[0].xyz, B[1].xyz, B[2].xyz);
                skinnedPos    += (localPos * B) * w;
                skinnedNormal += (normal * B3) * w;
                skinnedTangent += (tangentXYZ * B3) * w;
            }
        }
        localPos = skinnedPos;
        normal = normalize(skinnedNormal);
        tangentXYZ = normalize(skinnedTangent);
    }

    float4x4 worldMatrix;
    if (mparams.isInstanced == 1u) {
        worldMatrix = transpose(float4x4(
            vin.instanceWorld0,
            vin.instanceWorld1,
            vin.instanceWorld2,
            vin.instanceWorld3));
    } else {
        worldMatrix = mat.world;
    }

    float4 worldPos = localPos * worldMatrix;

    VertexOut o;
#if SHADOW_PASS
    // Depth-only path, contract clause 3:
    // the deformation section matches the main path literally, with projection replaced only by the light-space matrix.
    // Raw row-major bytes adapt through pre-multiplication.
    // M transpose times v is equivalent to CPU-side pos times M.
    // This differs from view and projection, where the CPU already uploads the transpose and the shader post-multiplies.
    o.position = lightViewProj * worldPos;
    o.vViewDepth = 0.0;
#else
    float4 viewPos  = worldPos * mat.view;
    o.position = viewPos * mat.projection;
    o.vViewDepth = viewPos.z;
#endif

#if VELOCITY_OUTPUT
    // Contract clauses 6 and 8 of 2-3:
    // the sentinel for previous matrices not written is all-zero.
    // A non-zero fourth column in prevViewProjection means valid history.
    // For perspective and orthographic matrices that column is always non-zero.
    float4 prevClip_ = float4(0.0);
    if (any(mat.prevViewProjection[3] != float4(0.0))) {
        // Per-instance history on the instanced path, world rows 0 through 3 plus morph weights,
        // comes uniformly from the previous-instance stream.
        // r3.w == 0 means that slot has not been written yet, such as the first frame or after growth clear,
        // so both world and morph weights fall back to current values.
        bool hasPrevInstance = false;
        float4 pr0 = float4(0.0), pr1 = float4(0.0), pr2 = float4(0.0), pr3 = float4(0.0), prMorphW = float4(0.0);
        if (mparams.isInstanced == 1u && mparams.hasPrevInstanceWorld != 0u) {
            pr0 = prevInstanceStream[instanceId * 5u + 0u];
            pr1 = prevInstanceStream[instanceId * 5u + 1u];
            pr2 = prevInstanceStream[instanceId * 5u + 2u];
            pr3 = prevInstanceStream[instanceId * 5u + 3u];
            prMorphW = prevInstanceStream[instanceId * 5u + 4u];
            hasPrevInstance = pr3.w != 0.0;
        }

        // Previous morph:
        // restart from rest pose and replay the same deformation order as the current frame, only replacing the weight source.
        // Instanced draws use the fifth float4 in the previous-instance stream.
        // Non-instanced draws use prevMorphWeights in the constant buffer.
        // Normals and tangents are not rebuilt for the previous frame because velocity only needs position.
        float4 prevLocalPos = restPos;
        if (mparams.hasMorphTargets != 0u && mparams.morphTargetCount > 0u) {
            float4 prevW = curMorphW;
            if (mparams.isInstanced == 1u) {
                if (hasPrevInstance)
                    prevW = prMorphW;
            } else if (mparams.hasPrevMorph != 0u) {
                prevW = mparams.prevMorphWeights;
            }
            for (uint t = 0u; t < mparams.morphTargetCount && t < 4u; ++t) {
                float w = prevW[int(t)];
                if (fabs(w) < 1e-6f) continue;
                uint off = (t * mparams.morphVertexCount + vertexId) * 9u;
                prevLocalPos.xyz += float3(morphDeltas[off + 0u], morphDeltas[off + 1u], morphDeltas[off + 2u]) * w;
            }
        }

        // Previous skinning:
        // iterate the same joints and weights in the same order as the current frame, replacing only the bone-matrix source.
        // For each joint, B[3][3]==0 falls back to the current matrix, matching the sentinel semantics on VK and DX.
        if (mparams.isSkinned != 0u && totalWeight > 0.0) {
            float4 prevSkinnedPos = float4(0.0);
            for (int i = 0; i < 4; ++i) {
                float w = vin.inWeights[i];
                if (w > 0.0) {
                    int idx = int(vin.inJoints[i]);
                    uint boneIndex = mparams.isInstanced == 1u
                        ? instanceId * max(mparams.bonePaletteStride, 1u) + uint(idx)
                        : uint(idx);
                    float4x4 Bp;
                    if (mparams.isInstanced == 1u)
                        Bp = instanceBoneMatrices[boneIndex];
                    else
                        Bp = bones.boneMatrices[idx];
                    if (mparams.hasPrevBones != 0u) {
                        float4x4 Bh = prevBoneMatrices[boneIndex];
                        if (Bh[3][3] != 0.0)
                            Bp = Bh;
                    }
                    prevSkinnedPos += (prevLocalPos * Bp) * w;
                }
            }
            prevLocalPos = prevSkinnedPos;
        }

        // Previous world:
        // instanced draws use world rows from the previous-instance stream, the previous-frame side of the double buffer.
        // Non-instanced draws use b0 prevWorld.
        // Both paths fall back to the current worldMatrix when [3][3] or r3.w equals 0.
        float4x4 prevWorldMatrix = worldMatrix;
        if (mparams.isInstanced == 1u) {
            if (hasPrevInstance)
                prevWorldMatrix = transpose(float4x4(pr0, pr1, pr2, pr3));
        } else if (mat.prevWorld[3][3] != 0.0) {
            prevWorldMatrix = mat.prevWorld;
        }

        prevClip_ = prevLocalPos * prevWorldMatrix * mat.prevViewProjection;
    }
    o.vPrevClip = prevClip_;
#endif

    o.vWorldPos = worldPos.xyz;

    // Text GPU instancing:
    // remap unit-quad UV into an atlas sub-rectangle, aligned with the VK text branch in the VS.
    // Glyph data reuses buffer(5) morphDeltas.
    // Each instance stores 12 floats, uvRect(4), glyphColor(4), and metrics(4).
    float4 textColor_ = float4(1.0);
    if (mparams.renderMode == 2u && mparams.isInstanced == 1u) {
        uint tBase = instanceId * 12u;
        float4 uvRect = float4(
            morphDeltas[tBase       ], morphDeltas[tBase + 1u],
            morphDeltas[tBase + 2u], morphDeltas[tBase + 3u]);
        float4 glyphColor = float4(
            morphDeltas[tBase + 4u], morphDeltas[tBase + 5u],
            morphDeltas[tBase + 6u], morphDeltas[tBase + 7u]);
        float4 metrics = float4(
            morphDeltas[tBase + 8u], morphDeltas[tBase + 9u],
            morphDeltas[tBase + 10u], morphDeltas[tBase + 11u]);
        float4 baseColor = textParams.textBaseColor;
        textColor_ = metrics.w > 0.5 ? glyphColor : baseColor;
        o.vUV = uvRect.xy + vin.inUV * uvRect.zw;
    } else {
        o.vUV = vin.inUV;
    }
    o.vInstanceColor = textColor_;

    float3x3 W3 = float3x3(worldMatrix[0].xyz, worldMatrix[1].xyz, worldMatrix[2].xyz);
    o.vNormal = normalize(normal * W3);
    o.vTangent = float4(normalize(tangentXYZ * W3), vin.inTangent.w);
    return o;
}

constant float PI = 3.14159265359;

inline float DistributionGGX(float3 N, float3 H, float roughness) {
    float a = roughness * roughness;
    float a2 = a * a;
    float NdotH = max(dot(N, H), 0.0);
    float NdotH2 = NdotH * NdotH;
    float denom = (NdotH2 * (a2 - 1.0) + 1.0);
    denom = PI * denom * denom;
    return a2 / max(denom, 0.0001);
}

inline float GeometrySchlickGGX(float NdotV, float roughness) {
    float r = roughness + 1.0;
    float k = (r * r) / 8.0;
    return NdotV / (NdotV * (1.0 - k) + k);
}

inline float GeometrySmith(float3 N, float3 V, float3 L, float roughness) {
    float NdotV = max(dot(N, V), 0.0);
    float NdotL = max(dot(N, L), 0.0);
    return GeometrySchlickGGX(NdotV, roughness) * GeometrySchlickGGX(NdotL, roughness);
}

inline float3 FresnelSchlick(float cosTheta, float3 F0) {
    cosTheta = clamp(cosTheta, 0.0, 1.0);
    return F0 + (1.0 - F0) * pow(1.0 - cosTheta, 5.0);
}

// Cook-Torrance direct-light contribution for one light source.
// Under the 1-2 contract, the formula is kept literally aligned across all backends.
// Radiance already includes intensity, attenuation, and cone terms.
inline float3 EvaluatePbrLight(float3 N, float3 V, float3 L, float3 albedo, float metallic, float roughness, float3 F0, float3 radiance) {
    float3 H = normalize(V + L);

    float NDF = DistributionGGX(N, H, roughness);
    float G = GeometrySmith(N, V, L, roughness);
    float3 F = FresnelSchlick(max(dot(H, V), 0.0), F0);

    float3 numerator = NDF * G * F;
    float denominator = 4.0 * max(dot(N, V), 0.0) * max(dot(N, L), 0.0);
    float3 specular = numerator / max(denominator, 0.0001);

    float3 kS = F;
    float3 kD = (float3(1.0) - kS) * (1.0 - metallic);

    float NdotL = max(dot(N, L), 0.0);
    return (kD * albedo / PI + specular) * radiance * NdotL;
}

// Contract clause 7 of 1-7:
// SH9 irradiance evaluation, based on Ramamoorthi and Hanrahan 2001.
// The basis functions use the unnormalized polynomial form.
// The CPU has already pre-multiplied the convolution coefficients into irradianceSH9,
// so only the 9-term linear combination remains here.
// The return value is E(n) over pi, with the same units as constant ambient light,
// so it can multiply albedo directly.
// It matches the GLSL and HLSL versions term by term.
// The only difference is that MSL has no global uniform, so lights are passed explicitly.
inline float3 EvaluateIrradianceSH9(constant SceneLights& lights, float3 n) {
    float3 result = lights.irradianceSH9[0].rgb;
    result += lights.irradianceSH9[1].rgb * n.y;
    result += lights.irradianceSH9[2].rgb * n.z;
    result += lights.irradianceSH9[3].rgb * n.x;
    result += lights.irradianceSH9[4].rgb * (n.x * n.y);
    result += lights.irradianceSH9[5].rgb * (n.y * n.z);
    result += lights.irradianceSH9[6].rgb * (3.0 * n.z * n.z - 1.0);
    result += lights.irradianceSH9[7].rgb * (n.x * n.z);
    result += lights.irradianceSH9[8].rgb * (n.x * n.x - n.y * n.y);
    return max(result, 0.0);
}

#if DDGI_ENABLED
// Clauses 9 and 10 of 2-4:
// probe irradiance sampling.
// Octahedral decoding mirrors ddgiProbeUpdate exactly, including OctDecode and tile layout.
// Each tile is 8 by 8, with a 6 by 6 core plus a 1-pixel gutter.
// The absolute center texel is tile times 8 plus 1 plus oct times 6,
// so normalized uv divides directly by atlas size.
// worldPos is biased along the normal by giParams2.x, the normal bias, then locates the 8 neighboring probes.
// Trilinear weights are multiplied by cosine-direction weights.
// texSampler with Linear filtering bilinearly samples each probe octahedral core, and the gutter absorbs seam overflow.
// The result is then multiplied by GiIntensity.
// When giParams2.y is greater than 0.5, each probe also runs a Chebyshev variance test from the depth-moment atlas,
// and the visibility factor multiplies into the weight to suppress wall-gap, contact, and back-face light leaks.
// Starting from step 5, probes with tile alpha below 0.5 are treated as invalid and removed from the weights.
// If all 8 neighbors disappear, the path falls back to SH9 environment irradiance.
// The logic matches the other backends line by line.
// MSL only requires lights to be passed explicitly.
inline float2 DdgiOctEncode(float3 dir) {
    float3 a = abs(dir);
    float2 p = dir.xy / (a.x + a.y + a.z);
    if (dir.z < 0.0)
        p = (1.0 - abs(float2(p.y, p.x))) * float2(p.x >= 0.0 ? 1.0 : -1.0, p.y >= 0.0 ? 1.0 : -1.0);
    return p;
}

// Fallback is the diffuse result that would have been used without DDGI,
// either SH9 or constant ambient.
// Step 5 uses it when invalid probes force a fallback.
inline float3 SampleProbeIrradiance(constant SceneLights& lights, texture2d<float> ddgiAtlas, texture2d<float> ddgiDepth, sampler s, float3 worldPos, float3 N, float3 fallback) {
    float3 gridMin = lights.giParams0.xyz;
    float spacing = lights.giParams0.w;
    float3 dims = lights.giParams1.xyz;
    float2 atlasSize = float2(dims.x * dims.z * 8.0, dims.y * 8.0);
    float2 oct = DdgiOctEncode(N) * 0.5 + 0.5;

    float3 wp = worldPos + N * lights.giParams2.x;
    float3 gc = (wp - gridMin) / spacing - 0.5;
    float3 base = floor(gc);
    float3 f = gc - base;

    float3 sum = float3(0.0);
    float wsum = 0.0;
    float wraw = 0.0;
    for (int i = 0; i < 8; i++) {
        float3 off = float3(float(i & 1), float((i >> 1) & 1), float((i >> 2) & 1));
        float3 tri = mix(1.0 - f, f, off);
        float w = tri.x * tri.y * tri.z;
        float3 pi = clamp(base + off, float3(0.0), dims - 1.0);
        float3 probePos = gridMin + (pi + 0.5) * spacing;
        float wdir = max(dot(normalize(probePos - worldPos), N), 0.0);
        w *= wdir * wdir + 0.01;
        float2 tile = float2(pi.x + pi.z * dims.x, pi.y);
        float2 uv = (tile * 8.0 + 1.0 + oct * 6.0) / atlasSize;
        // Step 5 validity weighting, clause 13:
        // alpha is a tile-wide classification value, so any point inside the tile is enough for sampling.
        // Use continuous weighting instead of a hard step threshold because alpha is a temporal EMA of classification,
        // and hard gating would amplify borderline-probe jitter into visible flicker.
        // wraw accumulates the pure geometric weight without validity,
        // so the tail can compute how much of the current shading point lies on valid probes.
        float valid = saturate(ddgiAtlas.sample(s, (tile * 8.0 + 4.0) / atlasSize, level(0.0)).a);
        if (lights.giParams2.y > 0.5) {
            float3 dirPW = wp - probePos;
            float distPW = length(dirPW);
            float2 octD = DdgiOctEncode(normalize(dirPW)) * 0.5 + 0.5;
            float2 depAtlasSize = float2(dims.x * dims.z * 16.0, dims.y * 16.0);
            float2 uvD = (tile * 16.0 + 1.0 + octD * 14.0) / depAtlasSize;
            float2 m = ddgiDepth.sample(s, uvD, level(0.0)).xy;
            float variance = max(m.y - m.x * m.x, 0.0);
            float d2 = distPW - m.x;
            float cheb = distPW <= m.x ? 1.0 : variance / (variance + d2 * d2);
            float cheb3 = cheb * cheb * cheb;
            // Visibility floor:
            // even full occlusion keeps 20 percent indirect light to prevent AABB proxy over-occlusion,
            // where cheb cubed would otherwise amplify shadowing and turn walls pure black.
            w *= 0.2 + 0.8 * cheb3;
        }
        wraw += w;
        w *= valid;
        sum += ddgiAtlas.sample(s, uv, level(0.0)).rgb * w;
        wsum += w;
    }
    // Step 5:
    // wsum divided by wraw is the fraction of interpolation weight contributed by valid probes at this shading point.
    // That ratio linearly blends between probe irradiance and fallback, the diffuse result without DDGI.
    // When all 8 neighbors are invalid, including the initial zero atlas before the first update,
    // the path naturally falls back to pure fallback with a continuous transition and no threshold pop or flicker.
    float3 probeIrr = wsum > 1e-6 ? sum / wsum : float3(0.0);
    float vfrac = saturate(wsum / max(wraw, 1e-6));
    return mix(fallback, probeIrr * lights.giParams1.w, vfrac);
}
#endif

inline float msdfMedian(float r, float g, float b) {
    return max(min(r, g), min(max(r, g), b));
}

// Step C of 2-5:
// density of a single cloud layer at the given world XZ position in kilometers.
// The value is 0 to 1 and already includes coverage remapping and high-frequency erosion.
// It mirrors DX HLSL CloudDensityAt literally, including lerp to mix and SampleLevel to sample level zero.
// The noise texture must use wrap sampling, as noted on wrapSampler in fragment_main, or the edge stretches into a stripe.
inline float CloudDensityAt(constant SceneLights& lights, texture2d<float> cloudNoise, sampler wrapSampler, float2 posKm, int layer) {
    float2 uv = (posKm + lights.cloudLayerB[layer].xy) * lights.cloudLayerB[layer].z;
    float4 n = cloudNoise.sample(wrapSampler, uv, level(0.0));
    float shape = n.r * mix(1.0, n.a, 0.7);
    float coverage = lights.cloudLayerA[layer].z;
    float d = saturate((shape - (1.0 - coverage)) / max(coverage, 1e-3));
    float erode = lights.cloudLayerB[layer].w * (0.5 * n.g + 0.5 * n.b);
    return saturate(d * saturate(1.0 - erode));
}

// Step C of 2-5:
// step along the light path, accumulate cloud optical depth, and solve cloud-shadow transmittance,
// where 1 means no cloud shadow and 0 means fully blocked.
// The gate matches DX:
// sampling only happens when layerCount is greater than 0, cloudShadowStrength is greater than 0,
// and the light direction is above the horizon.
// Otherwise it returns 1 at zero cost.
inline float ComputeCloudShadow(constant SceneLights& lights, texture2d<float> cloudNoise, sampler wrapSampler, float3 worldPos, float3 toLight) {
    float result = 1.0;
    int count = int(lights.cloudParams0.w);
    if (count > 0 && lights.cloudParams1.x > 0.0 && toLight.y > 0.0)
    {
        float2 originKm = worldPos.xz * 0.001;
        float invY = 1.0 / max(toLight.y, 0.05);
        float tau = 0.0;
        for (int i = 0; i < count; i++)
        {
            float hKm = max(lights.cloudLayerA[i].x - worldPos.y * 0.001, 0.0);
            float2 posKm = originKm + toLight.xz * (hKm * invY);
            tau += CloudDensityAt(lights, cloudNoise, wrapSampler, posKm, i)
                 * lights.cloudLayerA[i].w * lights.cloudLayerA[i].y * invY;
        }
        result = 1.0 - lights.cloudParams1.x * saturate(1.0 - exp(-tau));
    }
    return result;
}

// Step B of 2-5:
// analytic sun disk, moon disk, and procedural stars, mirrored literally from VK GLSL.
// These three terms intentionally stay out of the Sky-View LUT.
// The LUT is about 1.4 degrees per texel, while the sun disk is only 0.53 degrees wide.
// Pushing it into the LUT would produce a bright square whose energy is diluted by about 0.53 over 1.4 squared
// and flickers texel by texel as the celestial body moves.
// All data comes from lights.skyParams0 through lights.skyParams4.
// The disk radiance has already been multiplied on the CPU by the mean transmittance inside the disk,
// using the same evaluation that feeds direct lighting,
// so the sun in the sky and the lighting on the ground fade together.

// Integer bit-mixing hash in an xxhash-finalizer style.
// Avoid fract(sin(...)) because it depends on sine precision at large arguments,
// which would make the star field diverge across drivers and compilers and produce moire striping at high frequencies.
inline uint StarHash(uint3 v)
{
    uint h = v.x * 1597334677u ^ v.y * 3812015801u ^ v.z * 2654435761u;
    h ^= h >> 15u; h *= 2246822519u;
    h ^= h >> 13u; h *= 3266489917u;
    h ^= h >> 16u;
    return h;
}

// Map one 16-bit slice of the hash into the 0 to 1 range.
// Different shift values read non-overlapping bit ranges so each random draw stays independent.
// Multiplying h by a constant and reusing low bits would not work,
// because the low-bit mixing of multiplication is poor and would make jittered x and y visibly correlate into diagonal streaks.
inline float StarSlice(uint h, uint shift)
{
    return float((h >> shift) & 0xFFFFu) * (1.0 / 65536.0);
}

// Convert a direction into a cube-face index plus in-face uv in the 0 to 1 square.
// Use a cube instead of latitude-longitude because the latter degenerates into long thin cells near the poles
// and makes stars form radial artifacts around the zenith and nadir.
inline void StarFaceUv(float3 d, thread uint& face, thread float2& uv)
{
    float3 a = abs(d);
    if (a.x >= a.y && a.x >= a.z)  { uv = float2(d.z, d.y) / a.x; face = d.x > 0.0 ? 0u : 1u; }
    else if (a.y >= a.z)           { uv = float2(d.x, d.z) / a.y; face = d.y > 0.0 ? 2u : 3u; }
    else                           { uv = float2(d.x, d.y) / a.z; face = d.z > 0.0 ? 4u : 5u; }
    uv = clamp(uv * 0.5 + 0.5, 0.0, 1.0);   // Clamp to 0 through 1 because overflow at t = plus or minus 1 could make the floor below fall into cell negative one.
}

// Additive radiance from celestial disks and stars in linear HDR.
// It adds to the Sky-View LUT instead of replacing it because the LUT stores in-scattered radiance along the view ray,
// while this function returns the radiance of bodies and stars that travel through the atmosphere directly to the viewer.
// pxAng is the angular size per pixel in radians and is supplied by the caller.
// Both disk edges and star radii derive from it,
// so features stay about one pixel wide without hardcoding pixel counts or changing blur and aliasing with resolution or FOV.
inline float3 SkyCelestialRadiance(constant SceneLights& lights, float3 dir, float pxAng)
{
    float3 L = float3(0.0);

    // Sun disk:
    // test dot(dir, sunDir) greater than cos of the angular radius.
    // This is the second consumer of Atmosphere.SunAngularRadiusDeg.
    // The anti-alias width maps through the edge slope of cosine,
    // which is negative sin of the radius, so one pixel corresponds to pxAng times sin in cosine space.
    float sunSin = sqrt(max(1.0 - lights.skyParams0.w * lights.skyParams0.w, 1e-12));
    float aaSun = pxAng * sunSin;
    float sunMask = smoothstep(lights.skyParams0.w - aaSun, lights.skyParams0.w + aaSun, dot(dir, lights.skyParams0.xyz));
    L += lights.skyParams1.xyz * sunMask;

    // Moon disk plus lunar phase.
    float cosMoon = dot(dir, lights.skyParams2.xyz);
    float moonSin = sqrt(max(1.0 - lights.skyParams2.w * lights.skyParams2.w, 1e-12));
    float aaMoon = pxAng * moonSin;
    float moonMask = smoothstep(lights.skyParams2.w - aaMoon, lights.skyParams2.w + aaMoon, cosMoon);
    if (moonMask > 0.0)
    {
        // Spherical normal for points inside the disk, which is the zero-parameter source of lunar phase.
        // Normalize the tangential offset from the view ray to the moon center by the disk radius to get s in the 0 to 1 range,
        // where 0 is the disk center and 1 is the rim.
        // The normal is tangential times s minus moonDirection times sqrt of 1 minus s squared.
        // At the center it points directly at the viewer, which is negative moonDirection,
        // and at the rim it becomes perpendicular to the view direction.
        // This is exactly the geometry of a sphere under orthographic projection, with no extra parameter upload needed.
        float3 tangent = dir - lights.skyParams2.xyz * cosMoon;
        float tanLen = length(tangent);
        float s = clamp(tanLen / moonSin, 0.0, 1.0);
        float3 tDir = tanLen > 1e-8 ? tangent / tanLen : float3(1.0, 0.0, 0.0);
        float3 nrm = tDir * s - lights.skyParams2.xyz * sqrt(max(1.0 - s * s, 0.0));

        // Sunlight illuminates the moon, so the incident cosine is the lunar phase and evolves automatically
        // with sunDir and moonDir, with no phase parameter and no artist curve.
        // nrm is the negative outward normal, pointing toward the viewer, while sunDir is the propagation direction,
        // so the two negatives cancel and a positive dot product is correct here.
        // The square root is a cheap approximation of the strong backscatter of lunar regolith.
        // Pure Lambert would darken the edge of a full moon too much,
        // while the real full moon reads more like a nearly uniform bright disk.
        // The 0.015 floor is earthshine, the Earth-lit dark side of the moon,
        // roughly 1.5 percent of full-moon intensity.
        // It causes the visible dark disk in a crescent and is not an artistic boost.
        float lit = max(sqrt(clamp(dot(nrm, lights.skyParams0.xyz), 0.0, 1.0)), 0.015);
        L += lights.skyParams3.xyz * (moonMask * lit);
    }

    // Procedural stars.
    // skyParams1.w already includes twilight visibility derived from StarVisibilityTwilightDeg and stays 0 during daytime.
    if (lights.skyParams1.w > 0.0)
    {
        // Reverse-rotate into the star-fixed frame before randomization so the star map is pinned to that frame.
        // As StarRotation changes, the full sky then moves with diurnal motion
        // instead of re-randomizing every frame and flickering everywhere.
        // The axis is skyParams4.xyz, the celestial-pole axis, rather than world plus Y,
        // because only the pole axis produces correct east-rise west-set motion and circumpolar stars.
        // Rodrigues uses the reverse rotation, angle negative theta, so the cross term is negated.
        // The CPU already normalizes the axis, but a fallback remains:
        // if the axis is zero because it was never injected, fall back to plus Y instead of flipping dir into cos(theta) times dir.
        float3 axis = dot(lights.skyParams4.xyz, lights.skyParams4.xyz) > 1e-8 ? normalize(lights.skyParams4.xyz) : float3(0.0, 1.0, 0.0);
        float ca = cos(lights.skyParams3.w);
        float sa = sin(lights.skyParams3.w);
        float3 sd = dir * ca - cross(axis, dir) * sa + axis * (dot(axis, dir) * (1.0 - ca));

        uint face;
        float2 uv;
        StarFaceUv(sd, face, uv);

        const float gridN = 96.0;        // About 6 times 96 squared, roughly 55 thousand cells.
        const float starDensity = 0.1;   // About 5.5 thousand stars, close to the roughly 6 thousand naked-eye stars across the full sky.
        float2 g = uv * gridN;
        float2 ci = floor(g);
        float2 cf = g - ci;

        uint h = StarHash(uint3(uint(ci.x), uint(ci.y), face));
        if (StarSlice(h, 0u) < starDensity)
        {
            uint hj = StarHash(uint3(h, 0x9E3779B9u, 1u));
            uint hm = StarHash(uint3(h, 0x85EBCA6Bu, 2u));

            // Jitter the position inside the cell while leaving a 0.15 border.
            // This keeps stars inside their own cells so adjacent cells never draw half a star each and expose the grid.
            float2 pos = float2(0.15 + 0.7 * StarSlice(hj, 0u), 0.15 + 0.7 * StarSlice(hj, 16u));

            // Angular size per cell from the analytic form, continuous everywhere so cube edges stay seam-free.
            // Using fwidth(uv) here would explode into bright lines at the cube edges.
            // The in-face tangent coordinate t = uv times 2 minus 1 satisfies tan(theta) = t,
            // so dtheta over dt is approximately 1 over 1 plus absolute t squared,
            // and one cell spans 2 over gridN units of t.
            float2 t = uv * 2.0 - 1.0;
            float radPerCell = (2.0 / gridN) / (1.0 + dot(t, t));
            float distRad = length(cf - pos) * radPerCell;
            float star = 1.0 - smoothstep(pxAng * 0.5, pxAng * 1.8, distRad);

            // Magnitude power law:
            // dim stars vastly outnumber bright stars.
            // Cubing the uniform draw makes the brightest tenth carry most of the luminous flux.
            float mag = StarSlice(hm, 0u);
            float weight = mag * mag * mag;

            // Color-temperature draw:
            // warm K and M types to cool O and B types.
            // The amplitude stays intentionally small because real stars have low color saturation.
            float3 tint = mix(float3(1.0, 0.92, 0.82), float3(0.82, 0.9, 1.0), StarSlice(hm, 16u));

            // Fade out across roughly 3 degrees above the horizon.
            // That region is owned by ground geometry and horizon haze, so drawing stars there would only punch through the ground.
            L += lights.skyParams1.w * weight * star * tint * clamp(dir.y * 20.0, 0.0, 1.0);
        }
    }

    return L;
}

// Step C of 2-5:
// visible-cloud consumption. See CloudDensityAt and ComputeCloudShadow above.

// Distance in kilometers from the view ray to the intersection with cloud layer layer.
// This uses spherical-shell intersection instead of a plane.
// The plane approximation t = h / dir.y diverges near the horizon and stretches clouds into infinite bands.
// The shell solution converges to sqrt of 2Rh as dir.y approaches 0.
// With R=6360 and h=1.6 km, that is about 142 km,
// which is exactly why clouds collapse into a horizon band and why lower clouds move faster than higher ones when looking upward.
// The observer is at 0,R,0 with the planet center at the origin.
// Solve the positive root of absolute p plus t times d equals R plus h.
// R comes from skyParams4.w, which is CPU-side GroundRadiusKm plus ViewAltitudeKm.
// This only makes sense for dir.y greater than 0 because downward rays would hit the far side through the planet,
// so callers must gate that case first.
inline float CloudLayerHitKm(constant SceneLights& lights, float3 dir, float layerAltKm)
{
    float r = max(lights.skyParams4.w, 1.0);
    float b = r * dir.y;
    return -b + sqrt(max(b * b + 2.0 * r * layerAltKm + layerAltKm * layerAltKm, 0.0));
}

// Forward-scattering cloud silver lining, normalized to 0 through 1 where fully forward is 1.
// Use the HG shape instead of pow of cosTheta.
// g is cloudParams1.y, the same control used on the CPU side.
// Self-normalization avoids hardcoding the peak constant as a second source of truth.
inline float CloudSilverLining(float cosTheta, float g)
{
    float g2 = g * g;
    float dn = max(1.0 + g2 - 2.0 * g * cosTheta, 1e-4);
    float p = (1.0 - g2) / (dn * sqrt(dn));
    float dp = max(1.0 + g2 - 2.0 * g, 1e-4);
    float peak = (1.0 - g2) / (dp * sqrt(dp));
    return clamp(p / max(peak, 1e-6), 0.0, 1.0);
}

// Composite clouds onto sky radiance, used only by the renderMode==3 branch.
// Ordering matters:
// clouds live in front of every sky component.
// The Sky-View LUT stores infinitely far in-scattering, and the sun, moon, and stars do too,
// so layers are composed front to back with over, then the accumulated transmittance attenuates the sky behind them.
// That is also why clouds naturally occlude the sun disk and stars.
// Layer order is height order.
// When dir.y is greater than 0, higher layers intersect farther away,
// and the CPU preset fills them in ascending height.
inline float3 CloudComposite(constant SceneLights& lights, texture2d<float> cloudNoise, sampler wrapSampler,
                             float3 skyRadiance, float3 dir, float2 camXZKm)
{
    float3 acc = float3(0.0);
    float trans = 1.0;

    // Only the sun contributes to the forward-scattering highlight.
    // At moonlight levels the silver lining is invisible, so another HG evaluation would be wasted work.
    float fwd = lights.cloudParams1.w * CloudSilverLining(dot(dir, lights.skyParams0.xyz), lights.cloudParams1.y);

    // Fade near the horizon over about 1.4 degrees, following the same clamp(dir.y * 20) pattern as the star field.
    // Otherwise dir.y = 0 would leave a hard edge, especially in scenes without ground geometry.
    float horizonFade = clamp(dir.y * 40.0, 0.0, 1.0);

    int count = int(lights.cloudParams0.w);
    for (int i = 0; i < count; ++i)
    {
        float tKm = CloudLayerHitKm(lights, dir, lights.cloudLayerA[i].x);
        float d = CloudDensityAt(lights, cloudNoise, wrapSampler, camXZKm + dir.xz * tKm, i);

        // Slanted path length:
        // the flatter the view direction, the longer the geometric path through the same layer.
        // The denominator floor 0.05 is about 3 degrees.
        // Below that, the natural convergence of the spherical-shell model should take over,
        // or the horizon would collapse into a hard dark wall.
        float tau = d * lights.cloudLayerA[i].w * lights.cloudLayerA[i].y / max(dir.y, 0.05);
        float alpha = clamp(1.0 - exp(-tau), 0.0, 1.0) * horizonFade;

        // Self-occlusion proxy with zero extra taps:
        // optically thicker cloud centers look darker while edges stay brighter,
        // which matches the appearance of cumulus clouds viewed from below.
        // The true solution would require a few more resampling steps along the light ray,
        // and that cost is left for higher tiers.
        float lit = clamp(1.0 - d, 0.0, 1.0);
        float3 radiance = lights.cloudParams0.rgb * mix(lights.cloudParams1.z, 1.0, lit) * (1.0 + fwd);

        acc += trans * alpha * radiance;
        trans *= 1.0 - alpha;
    }

    return skyRadiance * trans + acc;
}
#if HDR_CHAIN
// Closed-form inverse of ACES, the Narkowicz 2015 fit:
// y = x times 2.51x plus 0.03 over x times 2.43x plus 0.59 plus 0.14,
// solved as a quadratic and taking the positive root.
// Used for text inverse compensation:
// pre-distort into linear scene space so the full FinalBlit exposure plus ACES plus gamma chain reconstructs the design color accurately.
// The curve has an asymptote around y approximately 1.033, so inputs are clamped below 1 to avoid discriminant degeneration.
inline float3 AcesFilmInv(float3 y)
{
    y = min(y, float3(0.999));
    float3 A = 2.51 - 2.43 * y;
    float3 B = 0.03 - 0.59 * y;
    return (-B + sqrt(B * B + 4.0 * A * (0.14 * y))) / (2.0 * A);
}
#endif

#if SHADOW_ENABLED
// 1-5 shadow-atlas comparison sampling using depth2d plus sample_compare with hardware PCF.
// It uses texture(5) plus comparison sampler(1).
// The atlas has four quadrant tiles, slots 0 through 2 for CSM and slot 3 for the spotlight.
// The sampling function mirrors DX and VK one to one with a single exit.
// MSL has no global resources, so atlas, sampler, and lights are passed explicitly through parameters.

// Single-tile 3 by 3 PCF.
// shadowNdc is light-space NDC after dividing by w, and sampling is clamped inside the tile to avoid leakage.
inline float SampleShadowTile(depth2d<float> shadowAtlas, sampler shadowSampler, float texel, int slot, float3 shadowNdc) {
    float result = 1.0;
    float2 uv = float2(shadowNdc.x * 0.5 + 0.5, 0.5 - shadowNdc.y * 0.5);
    if (uv.x >= 0.0 && uv.x <= 1.0 && uv.y >= 0.0 && uv.y <= 1.0 &&
        shadowNdc.z > 0.0 && shadowNdc.z < 1.0) {
        float2 tileOrigin = float2(slot & 1, slot >> 1) * 0.5;
        float2 tileMin = tileOrigin + texel * 1.5;
        float2 tileMax = tileOrigin + 0.5 - texel * 1.5;
        float2 atlasUV = tileOrigin + uv * 0.5;
        float sum = 0.0;
        for (int dy = -1; dy <= 1; ++dy)
            for (int dx = -1; dx <= 1; ++dx) {
                float2 sampleUV = clamp(atlasUV + float2(dx, dy) * texel, tileMin, tileMax);
                sum += shadowAtlas.sample_compare(shadowSampler, sampleUV, shadowNdc.z);
            }
        result = sum / 9.0;
    }
    return result;
}

// Directional light, CSM:
// select the cascade slot by view-space depth, then project into light space, sample it,
// and mix the result into shadowStrength.
inline float ComputeSunShadow(constant SceneLights& lights, depth2d<float> shadowAtlas, sampler shadowSampler, float3 worldPos, float viewDepth) {
    float result = 1.0;
    int cascadeCount = int(lights.shadowParams0.y);
    if (lights.shadowParams0.x >= 0.5 && viewDepth <= lights.cascadeSplits[cascadeCount - 1]) {
        int slot = cascadeCount - 1;
        for (int c = cascadeCount - 1; c >= 0; --c)
            if (viewDepth <= lights.cascadeSplits[c]) slot = c;
        // Pre-multiplication, contract clause 1:
        // raw row-major bytes are read by MSL as M transpose, and M transpose times v is equivalent to CPU-side pos times M.
        float4 lightPos = lights.cascadeViewProj[slot] * float4(worldPos, 1.0);
        float visibility = SampleShadowTile(shadowAtlas, shadowSampler, lights.shadowParams0.z, slot, lightPos.xyz / lightPos.w);
        result = mix(1.0, visibility, lights.shadowParams1.y);
    }
    return result;
}

// Spot light:
// single tile at slot 3, sampled after perspective divide.
inline float ComputeSpotShadow(constant SceneLights& lights, depth2d<float> shadowAtlas, sampler shadowSampler, float3 worldPos) {
    float result = 1.0;
    if (lights.shadowParams1.x >= 0.5) {
        float4 lightPos = lights.spotShadowViewProj * float4(worldPos, 1.0);
        if (lightPos.w > 0.0) {
            float visibility = SampleShadowTile(shadowAtlas, shadowSampler, lights.shadowParams0.z, 3, lightPos.xyz / lightPos.w);
            result = mix(1.0, visibility, lights.shadowParams1.y);
        }
    }
    return result;
}
#endif

#if VELOCITY_OUTPUT
// Contract clauses 2 and 3 of 2-3:
// the Scene-pass fragment shader outputs MRT with slot0=color and slot1=velocity in Rg16Float.
// The main body has multiple early returns, such as the msdf and Sprite2D branches,
// so a macro provides a unified exit.
// Every return path carries the velocity computed unconditionally at function entry,
// which preserves the original control flow and never depends on remembering to write velocity in each branch.
struct FragmentOut {
    float4 color    [[color(0)]];
    float2 velocity [[color(1)]];
};
#define SEASON_FS_OUT FragmentOut
#define SEASON_RETURN_COLOR(col_) { FragmentOut fsOut_; fsOut_.color = (col_); fsOut_.velocity = velocity_; return fsOut_; }
#else
#define SEASON_FS_OUT float4
#define SEASON_RETURN_COLOR(col_) return (col_);
#endif

fragment SEASON_FS_OUT fragment_main(
    VertexOut in [[stage_in]],
#if OUTLINE_MASK
    // Phase 4 group outline color:
    // written per group through SetFragmentBytes at buffer(0).
    // Only the mask variant declares it, so regular variants have zero binding cost.
    constant float4& outlineMaskColor [[buffer(0)]],
#endif
    constant SceneLights& lights [[buffer(1)]],
    constant MaterialParams& mat [[buffer(2)]],
    constant TextDrawParams& textParams [[buffer(3)]],
    texture2d<float> albedoMap            [[texture(0)]],
    texture2d<float> normalMap            [[texture(1)]],
    texture2d<float> metallicRoughnessMap [[texture(2)]],
    texture2d<float> aoMap                [[texture(3)]],
    texture2d<float> emissiveMap          [[texture(4)]],
    sampler texSampler                    [[sampler(0)]],
    // 1-7 environment radiance cube at texture(6), because texture(5) is already occupied by the 1-5 shadow atlas.
    // It is a single-mip cube.
    // Sampling reuses the static texSampler above at sampler(0), Linear plus ClampToEdge.
    // When there is no environment map, Device.BeginPass binds a 1 by 1 black dummy,
    // so this path is always safe to sample and envParams.w remains the actual switch.
    // It intentionally stays outside SHADOW_ENABLED because environment IBL is independent of the shadow tier.
    texturecube<float> envCube            [[texture(6)]]
    // Clause 10 of 2-4:
    // DDGI irradiance probe atlas at texture(7), rgba16float.
    // It is always declared, like envCube, outside SHADOW_ENABLED.
    // Device.BeginPass binds a 1 by 1 white texture before the real atlas is ready,
    // and DDGI_ENABLED performs the real sampling gate.
    , texture2d<float> ddgiAtlas           [[texture(7)]]
    // Step 3 of 2-4:
    // DDGI depth-moment atlas at texture(8), rg16float, where x is mean and y is mean squared.
    // It is always declared for the same reason.
    // Device.BeginPass binds a 1 by 1 white texture until it becomes ready,
    // and giParams2.y gates the actual Chebyshev sampling at runtime.
    , texture2d<float> ddgiDepth           [[texture(8)]]
    // Step C of 2-5:
    // pre-baked cloud noise at texture(9), rgba8unorm.
    // R is low-frequency silhouette FBM, G is Worley fluff, B is high-frequency erosion,
    // and A is ultra-low-frequency coverage modulation.
    // It is always declared.
    // Device.BeginPass binds a 1 by 1 white texture until ready.
    // Real sampling is gated at runtime by cloudParams0.w, the layer count,
    // because the all-white fallback must never be treated as valid noise or density would saturate.
    // The sampler is wrapSampler at sampler(2), Repeat,
    // because the noise is tileable and wind offsets can push uv outside 0 through 1.
    // Clamp would stretch the outermost texel row into a frozen stripe across the sky, same as DX s2 wrapSampler.
    , texture2d<float> cloudNoise          [[texture(9)]]
    // Step E of 2-5:
    // aerial-perspective froxel volume at texture(10), 32 cubed rgba16float.
    // rgb stores accumulated in-scattered radiance from the camera to that distance in linear HDR,
    // and a stores accumulated opacity.
    // It is always declared.
    // Device.BeginPass binds a 1 by 1 by 1 all-zero dummy until ready.
    // Since a stores opacity rather than transmittance, all-zero is exactly the identity element of the compositing formula.
    // apParams0.x only saves a sample when disabled.
    // All three axes use Clamp, and trilinear sampling reuses texSampler at sampler(0).
    , texture3d<float> apLut               [[texture(10)]]
    , sampler wrapSampler                  [[sampler(2)]]
#if SHADOW_ENABLED
    , depth2d<float> shadowAtlas          [[texture(5)]]
    , sampler shadowSampler               [[sampler(1)]]
#endif
    )
{
#if OUTLINE_MASK
    // Phase 4 mask path, mirroring DX PSOutlineMask:
    // alpha follows the material transparency chain, including the albedo alpha.
    // Values below the threshold are discarded.
    // Color passes through as the group color and alpha stays 1,
    // so any outline color, including pure black, remains valid.
    // RGB goes through BGRA8 mask RT quantization and therefore matches final display.
    float maskAlpha = mat.materialColor.a;
    if (mat.useAlbedoMap != 0u)
        maskAlpha *= albedoMap.sample(texSampler, in.vUV).a;
    if (mat.alphaMode == 1u && maskAlpha < mat.alphaCutoff)
        discard_fragment();
    SEASON_RETURN_COLOR(float4(outlineMaskColor.rgb, 1.0));
#endif
#if VELOCITY_OUTPUT
    // Contract clause 5 of 2-3:
    // initialize unconditionally before any early return or discard.
    // w less than or equal to 0 means no history, including all 2D, UI, and text paths
    // where Projection is Identity and previous matrices stay zero, so velocity remains zero.
    float2 velocity_ = float2(0.0);
    if (in.vPrevClip.w > 0.0) {
        // curNdc is reconstructed from [[position]], the fragment screen coordinate equivalent to SV_Position or gl_FragCoord,
        // then current-frame jitter is removed.
        // prevNdc comes from perspective-dividing vPrevClip, whose source matrix prevViewProjection is already unjittered.
        // velocity equals curNdc minus prevNdc, multiplied by 0.5 and negative 0.5,
        // converting into UV space with Y flipped, matching the other backends literally.
        float2 curNdc = in.position.xy * lights.velocityParams.zw * float2(2.0, -2.0) + float2(-1.0, 1.0);
        curNdc -= lights.velocityParams.xy;
        float2 prevNdc = in.vPrevClip.xy / in.vPrevClip.w;
        velocity_ = (curNdc - prevNdc) * float2(0.5, -0.5);
    }
#endif
    float3 albedo = mat.materialColor.rgb;
    float alpha = mat.materialColor.a;
    float3 metallicRoughness = float3(0.0, 0.5, 0.0);
    float ao = 1.0;
    float3 emissive = float3(0.0);

    // renderMode == 2, TextMsdf:
    // multi-channel signed-distance-field rendering with GPU instancing,
    // aligned with the single DX and VK fragment-shader branch.
    // pxRange and atlasSize both come from TextDrawParams, and every pass has DefaultTextDrawParamsBuffer as fallback binding.
    // Color multiplies by vInstanceColor.
    // The VS supplies float4(1) for non-instanced draws, matching DX VS textColor semantics.
    // The old per-glyph path, where mat.padding1 carried pxRange and atlas size was reverse-queried from the texture,
    // has been removed because DX and VK never branch on isInstanced in the fragment shader.
    // The dynamic TextMsdf path in MTLSprite2D, renderMode 2 plus IsInstanced 0, stays fully symmetric with DXSprite2D
    // and remains compatible with the single-branch semantics.
    if (mat.renderMode == 2u) {
        float4 sampledMsdf = albedoMap.sample(texSampler, in.vUV);
        float msdfDist = msdfMedian(sampledMsdf.r, sampledMsdf.g, sampledMsdf.b) - 0.5;
        float trueDist = sampledMsdf.a - 0.5;
        float signedDistance = (msdfDist * trueDist > 0.0) ? msdfDist : trueDist;
        float pxRangeI = max(textParams.textPxRange, 1.0);
        float2 texSize = max(textParams.textAtlasSize, float2(1.0));
        float2 unitRangeI = float2(pxRangeI / max(texSize.x, 1.0), pxRangeI / max(texSize.y, 1.0));
        float2 screenTexSizeI = max(float2(1.0) / max(fwidth(in.vUV), float2(1e-5)), float2(1.0));
        float screenPxRangeI = max(0.5 * dot(unitRangeI, screenTexSizeI), 1.0);
        float coverageI = clamp(screenPxRangeI * signedDistance + 0.5, 0.0, 1.0);
        float3 colorI = albedo * in.vInstanceColor.rgb;
#if HDR_CHAIN
        // Inverse-ACES compensation, step B of 1-4, contract clause 4:
        // pre-distort the text color, a display-space design color, into linear scene space
        // so the full FinalBlit chain of exposure, ACES, and gamma reconstructs the design color exactly.
        // Dividing by exposure makes text exposure-invariant.
        // Fallback to neutral 1.0 if the exposure read is less than or equal to 0 because the UBO was never initialized by SetLighting.
        float3 designColor = saturate(colorI);
        float safeExposure = lights.params0.y > 0.0 ? lights.params0.y : 1.0;
        colorI = AcesFilmInv(pow(designColor, float3(2.2))) / safeExposure;
#endif
        SEASON_RETURN_COLOR(float4(colorI, alpha * in.vInstanceColor.a * textParams.textGlobalAlpha * coverageI));
    }

    // 2-5 procedural sky:
    // reconstruct Sky-View LUT uv from the world view direction and deliberately ignore the vertex uv.
    // That is why this block must run before the useAlbedoMap sampling block below,
    // or albedo would already be contaminated by vUV sampling.
    // The single source of truth for the parameterization lives in the Season.Rendering.Atmosphere class header,
    // and this inverse matches the skyView kernel literally.
    // The seam of u sits on plus Z, north, where celestial arcs never pass, so the Mie spike never hits the seam.
    // v uses sqrt folding to pack more resolution near the horizon, where uniform v would create color banding.
    // The LUT is rgba16float with no mip chain, and implicit derivatives near the seam would produce bogus LOD,
    // so level zero is always explicit.
    // The LUT is bound through the albedoMap slot at texture(0).
    // Sky geometry has no real albedo map, so all backends reuse that slot with zero additions.
    if (mat.renderMode == 3u) {
        float3 skyDir = normalize(in.vWorldPos - lights.cameraPos.xyz);
        float2 skyUv;
        skyUv.x = atan2(skyDir.x, -skyDir.z) * (0.5 / M_PI_F) + 0.5;
        skyUv.y = 0.5 - 0.5 * sign(skyDir.y) * sqrt(abs(skyDir.y));
        float3 skyRadiance = albedoMap.sample(texSampler, skyUv, level(0.0)).rgb * albedo;

        // Step B of 2-5:
        // add the analytic sun disk, moon disk, and stars.
        // The gate is skyParams0.w greater than 0.
        // All four fields being zero means the non-procedural-sky tier,
        // because the true cosine of the angular radius is about 0.99999 and can never be 0.
        // StaticCube therefore leaves no residual here.
        // pxAng is computed outside and passed in because fwidth is a gradient operation
        // and must not live inside the non-uniform disk or star branches.
        // The fallback is 1 over screenHeight from velocityParams.w, injected every frame.
        // When fwidth unexpectedly returns 0, the old 1e-6 rad fallback would collapse stars to subpixel size
        // and make the whole star field disappear, matching the 2026-08 Web-side investigation.
        if (lights.skyParams0.w > 0.0)
        {
            float pxAng = max(length(float3(fwidth(skyDir.x), fwidth(skyDir.y), fwidth(skyDir.z))),
                              max(lights.velocityParams.w, 1e-4));
            skyRadiance += SkyCelestialRadiance(lights, skyDir, pxAng) * albedo;
        }

        // Step C of 2-5:
        // composite procedural clouds.
        // This must run after the celestial disks because clouds are in front of every sky component
        // and must be able to occlude the sun and stars.
        // That occlusion is exactly skyRadiance times trans at the tail of CloudComposite.
        // Two gates apply:
        // cloudParams0.w, the layer count and UBO-side guarantee that cloud noise is ready,
        // plus skyDir.y greater than 0, because downward rays would intersect the far side of the planet and are meaningless here.
        // The body only uses explicit level zero sampling, so this non-uniform branch involves no implicit derivatives.
        if (lights.cloudParams0.w > 0.0 && skyDir.y > 0.0)
            skyRadiance = CloudComposite(lights, cloudNoise, wrapSampler, skyRadiance, skyDir, lights.cameraPos.xz * 0.001);
#if HDR_CHAIN
        // The LUT already stores linear HDR radiance,
        // so output it directly and let the full FinalBlit exposure plus ACES plus gamma chain close the loop, matching the 1-4 contract.
        SEASON_RETURN_COLOR(float4(skyRadiance, alpha));
#else
        // LDR baseline, which overlay always uses:
        // apply gamma in place.
        // max(...,0) is not a quality tweak.
        // Radiance is physically non-negative, but the compiler cannot prove that from sampledValue times materialColor,
        // so the explicit clamp avoids undefined behavior from pow with a negative base.
        SEASON_RETURN_COLOR(float4(pow(max(skyRadiance, float3(0.0)), float3(1.0 / 2.2)), alpha));
#endif
    }

    if (mat.useAlbedoMap != 0u) {
        float4 sampled = albedoMap.sample(texSampler, in.vUV);
        albedo *= sampled.rgb;
        alpha *= sampled.a;
    }

    // alphaMode == 1, MASK:
    // discard below the cutoff.
    if (mat.alphaMode == 1u) {
        if (alpha - mat.alphaCutoff < 0.0) discard_fragment();
    }

    // renderMode == 0, Sprite2D:
    // unlit path with direct gamma output.
    // In HDR tiers it writes linear color directly and FinalBlit closes the chain.
    if (mat.renderMode == 0u) {
#if HDR_CHAIN
        // Sprite2D:
        // unlit path, direct linear output, then the FinalBlit exposure plus ACES plus gamma chain closes it at step B.
        SEASON_RETURN_COLOR(float4(albedo, alpha));
#else
        float3 c = pow(albedo, float3(1.0 / 2.2));
        SEASON_RETURN_COLOR(float4(c, alpha));
#endif
    }

    if (mat.useMetallicRoughnessMap != 0u) {
        metallicRoughness = metallicRoughnessMap.sample(texSampler, in.vUV).rgb;
    } else {
        metallicRoughness.b = mat.metallicFactor;
        metallicRoughness.g = mat.roughnessFactor;
    }

    if (mat.useAoMap != 0u) ao = aoMap.sample(texSampler, in.vUV).r;

    if (mat.useEmissiveMap != 0u) {
        emissive = emissiveMap.sample(texSampler, in.vUV).rgb;
    } else {
        emissive = mat.emissiveFactor.rgb;
    }

    float metallic = metallicRoughness.b;
    float roughness = metallicRoughness.g;

    float3 N = normalize(in.vNormal);
    float3 T = normalize(in.vTangent.xyz);
    T = normalize(T - dot(T, N) * N);
    float3 B = cross(N, T) * in.vTangent.w;
    float3x3 TBN = float3x3(T, B, N);

    if (mat.useNormalMap != 0u) {
        float3 nrm = normalMap.sample(texSampler, in.vUV).rgb * 2.0 - 1.0;
        N = TBN * nrm;
    }

    float3 V = normalize(lights.cameraPos.xyz - in.vWorldPos);
    float3 F0 = float3(0.04);
    F0 = mix(F0, albedo, metallic);

    // Direct-light accumulation.
    // Under contract clause 2 of 1-2, directional, point, and spot lights all live in the same lights array
    // and one loop dispatches by dirType.w.
    float3 Lo = float3(0.0);

    int lightCount = min(int(lights.params0.x), 8);
    int dirShadowIdx = int(lights.params0.z);      // 投 CSM 的方向光下标（-1=无）
    int spotShadowIdx = int(lights.params0.w);     // 投 2D shadowmap 的聚光下标（-1=无）
    for (int i = 0; i < lightCount; ++i) {
        float type = lights.lights[i].dirType.w;
        float3 L;
        float3 radiance;

        if (type >= 1.5) {
            // Directional light, sun or moon:
            // L is constant with no attenuation, and radiance equals color times intensity.
            L = normalize(-lights.lights[i].dirType.xyz);
            radiance = lights.lights[i].colorIntensity.xyz * lights.lights[i].colorIntensity.w;
#if SHADOW_ENABLED
            if (i == dirShadowIdx)
                radiance *= ComputeSunShadow(lights, shadowAtlas, shadowSampler, in.vWorldPos, in.vViewDepth);
#endif
            // Step C of 2-5, cloud shadows:
            // evaluate them separately for every directional light using its own L,
            // so sun and moon cast their own cloud shadows independently.
            // Unlike CSM, which only tracks dirShadowIdx, cloud shadows do not consume atlas quadrants
            // and therefore have no single-light limit.
            // This intentionally stays outside SHADOW_ENABLED because that switch belongs to CSM and the shadow atlas.
            // Cloud shadows are atlas-independent and should remain visible even when CSM is disabled,
            // because sweeping cloud shadow is the dominant lighting cue on overcast days.
            radiance *= ComputeCloudShadow(lights, cloudNoise, wrapSampler, in.vWorldPos, L);
        } else {
            float3 toLight = lights.lights[i].posRange.xyz - in.vWorldPos;
            float dist = length(toLight);
            L = toLight / max(dist, 0.0001);

            // Attenuation, contract clause 3, aligned with KHR_lights_punctual:
            // range greater than 0 applies a windowed cutoff, while range less than or equal to 0 degenerates to pure inverse-distance squared.
            float attenuation = 1.0 / max(dist * dist, 0.0001);
            float range = lights.lights[i].posRange.w;
            if (range > 0.0) {
                float win = saturate(1.0 - pow(dist / range, 4.0));
                attenuation *= win * win;
            }

            // Spot cone, contract clause 4:
            // cosine limits are precomputed on the CPU and the boundary is softened with smoothstep.
            if (type > 0.5) {
                attenuation *= smoothstep(lights.lights[i].spotParams.y, lights.lights[i].spotParams.x,
                                          dot(-L, normalize(lights.lights[i].dirType.xyz)));
            }

            radiance = lights.lights[i].colorIntensity.xyz * lights.lights[i].colorIntensity.w * attenuation;
#if SHADOW_ENABLED
            // Spot shadow at slot 3:
            // only the spot light pointed to by params0.w participates, aligned with DX, VK, and CascadedShadow.ComputeSpot.
            if (i == spotShadowIdx && type > 0.5)
                radiance *= ComputeSpotShadow(lights, shadowAtlas, shadowSampler, in.vWorldPos);
#endif
        }

        Lo += EvaluatePbrLight(N, V, L, albedo, metallic, roughness, F0, radiance);
    }

    // Ambient lighting.
    // Contract clause 6 of 1-2 makes it parameterized, with the default 0.5, 0.5, 0.5 times 1.0
    // matching the old hardcoded appearance.
    // Contract clause 5 of 1-7 makes SH9 environment diffuse and constant ambient mutually exclusive,
    // because they have the same units and summing them would double-count.
    // Both share the same one-minus-metallic gate because metals have no diffuse term.
    // Clause 9 of 2-4 turns the diffuse term into a three-way choice, never additive:
    // when DDGI is ready and GiIntensity is greater than 0, probe irradiance replaces the SH9-versus-constant result;
    // otherwise the path falls back fully to 1-7 or 1-2.
    // The specular term does not move.
    // Clause 13 makes probes blend continuously back toward giDiffuse by validity,
    // so giDiffuse also serves as the step 5 fallback.
    float3 envDiffuse = EvaluateIrradianceSH9(lights, N) * lights.envParams.y;
    float3 constAmbient = lights.ambientParams.xyz * lights.ambientParams.w;
    float3 giDiffuse = mix(constAmbient, envDiffuse, step(0.5, lights.envParams.z));
#if DDGI_ENABLED
    if (lights.giParams2.z > 0.5 && lights.giParams1.w > 0.0)
        giDiffuse = SampleProbeIrradiance(lights, ddgiAtlas, ddgiDepth, texSampler, in.vWorldPos, N, giDiffuse);
#endif
    float3 ambient = giDiffuse * albedo * ao * (1.0 - metallic);

    // Contract clause 6 of 1-7:
    // the specular term uses mirrored reflection from radiance cube LOD0.
    // There is no mip chain and no GGX prefiltering,
    // so it is masked by one-minus-roughness squared and rough-surface environment energy is carried by the SH9 diffuse term above.
    float3 R = reflect(-V, N);
    float3 envSpecular = envCube.sample(texSampler, R, level(0.0)).rgb * lights.envParams.x;
    float specMask = (1.0 - roughness) * (1.0 - roughness);
    ambient += envSpecular * F0 * specMask * ao * step(0.5, lights.envParams.w);

    float3 color = ambient + Lo + emissive;

    // Step E of 2-5:
    // aerial-perspective compositing.
    // This is deliberately placed in linear HDR space before tone mapping,
    // because atmospheric in-scattering is a real radiance contribution.
    // Tone mapping once and then adding it would wash distant blue haze toward gray-white.
    // Only the renderMode == 1 PBR path reaches this point because Sprite2D and TextMsdf return earlier,
    // so the sky itself is not fogged twice.
    // The z axis uses sqrt of distance over maxDistance,
    // which is the inverse of the skyAerial bake slice-center distance formula.
    // That makes near slices dense and far slices sparse, matching the fact that AP gradients are concentrated within the first few kilometers.
    if (lights.apParams0.x > 0.0)
    {
        float2 apUv = in.position.xy * lights.velocityParams.zw;
        float distKm = length(in.vWorldPos - lights.cameraPos.xyz) * 0.001;
        float apW = sqrt(saturate(distKm / lights.apParams0.x));
        float4 ap = apLut.sample(texSampler, float3(apUv, apW), level(0.0));
        color = mix(color, color * (1.0 - ap.a) + ap.rgb, lights.apParams0.y);
    }

#if HDR_CHAIN
    // Step B:
    // output true linear HDR values with no compression and no encoding.
    // Exposure, ACES, and gamma are closed uniformly by the FinalBlit tonemap variant.
#else
    // Tone mapping and gamma correction for the LDR baseline:
    // inline Reinhard plus gamma.
    color = color / (color + float3(1.0));
    color = pow(color, float3(1.0 / 2.2));
#endif

    SEASON_RETURN_COLOR(float4(color, alpha));
}
";
}
