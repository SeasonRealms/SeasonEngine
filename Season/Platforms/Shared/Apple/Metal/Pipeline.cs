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
///       buffer(11)=ViewConstants, matching DX b8 and VK set 2
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
    /// Hardware 2x2 bilinear comparison is combined with the shader's clause 14 rotated sample disk, matching the visual behavior of DX SampleCmpLevelZero and VK sampler2DShadow.
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

    // -- Work package D: transparent-stage volumetric fog sampling.
    //    Metal has no descriptor sets and no root signature; everything lands on the encoder and resets to zero after a Pass switch;
    //    therefore this trio must be static pushed state carried by every command and fully rebound on replay,
    //    corresponding one-to-one with VK's set 1 binding 0/1/2 + push constant and DX's FogFrameRoot/FogIntegratedRoot/FogLocalRoot. --

    /// <summary>The current frame's fog constant array (<see cref="Season.Rendering.Effects.VolumetricFogEffect.FrameVectorCount"/> float4s) is in FS buffer(4).</summary>
    internal const int SceneFogFrameSlot = 4;

    /// <summary>The runtime gating float4 is in FS buffer(5), pushed per command with SetFragmentBytes; sampling actually happens only when x &gt; 0.5.</summary>
    internal const int SceneFogParamsSlot = 5;

    /// <summary>The integrated column volume (read-only 3D) is in texture(11), right after the AP LUT in texture(10).</summary>
    internal const int SceneFogIntegratedSlot = 11;

    /// <summary>The local column volume (read-only 3D) is in texture(12).</summary>
    internal const int SceneFogLocalSlot = 12;

    /// <summary>Only the transparent stage sets it to (1,0,0,0); it stays zero at all other times, so the MSL gate is always closed.</summary>
    internal static Vector4 SceneFogParams;

    /// <summary>The current frame's fog frame constant buffer; when empty, falls back to the all-zero page of <see cref="DefaultFogFrameBuffer"/>.</summary>
    internal static IMTLBuffer? SceneFogFrame;

    internal static IMTLTexture? SceneFogIntegrated;

    internal static IMTLTexture? SceneFogLocal;

    /// <summary>Same origin as VK SceneFogSamplingBaked / DX _fogSamplingEnabled:
    /// the tier is already locked before initialization and never flips afterwards, so this is a capability switch evaluated once.</summary>
    internal static bool SceneFogSamplingBaked;

    /// <summary>The placeholder frame constant page when fog sampling is off: all zero, with both <c>F(8).w</c> and <c>F(10).xyz</c> equal to 0,
    /// so even an accidental sample only yields a degenerate zero accumulation, and the gate guarantees the sampling branch is never entered.</summary>
    public static IMTLBuffer DefaultFogFrameBuffer = null!;

    // -- Per-view constants (ViewConstants, 256 bytes) are in VS buffer(11), corresponding one-to-one with DX b8 and VK set 2.
    //    MatrixBuffer retains only the per-object World/PrevWorld; View/Projection/PrevViewProjection are provided by the view.
    //    The main view is buffered per frame slot N and written once per frame by MTLPrimitiveGroup.Update; the screen-space view (identity matrix) is resident read-only,
    //    used by Sprite2D and text, so they no longer need to write the identity View/Projection into their respective MatrixBuffers. --

    /// <summary>The VS buffer slot where ViewConstants lives.</summary>
    internal const int ViewConstantsSlot = 11;

    /// <summary>The view slot limit, same origin as the shared scheduler's <see cref="Season.Rendering.FrameSchedule.MaxViews"/>.</summary>
    internal const int MaxViews = Season.Rendering.FrameSchedule.MaxViews;

    /// <summary>
    /// Per-view constants (view slot × frame slot, a 2D array). Slot 0 is the main view, written once per frame by MTLPrimitiveGroup.Update;
    /// the remaining slots are written by the FrameSchedule.PrepareView hook before that view's draw collection.
    /// View constants are inherently a single independent buffer; the same draw command just needs to carry a different buffer to be reused across multiple views.
    /// </summary>
    static IMTLBuffer[][] _viewBuffers = [];

    /// <summary>Screen-space view: View/Projection are the identity matrix, with no history.</summary>
    public static IMTLBuffer ScreenViewBuffer = null!;

    /// <summary>The main view constant buffer for the current frame slot.</summary>
    internal static IMTLBuffer MainViewBuffer => _viewBuffers[0][Device.FrameIndex];

    /// <summary>
    /// The view slot currently being collected/replayed. Set by <see cref="SetActiveView"/> before that view's draw collection,
    /// and restored to the main view by <see cref="ResetActiveView"/> after the view loop ends,
    /// so the Overlay and the direct-draw path still bind to the main view constants.
    /// </summary>
    internal static int ActiveViewIndex;

    /// <summary>The constant buffer for the current view slot and current frame slot: this is the buffer recorded by draw items at collection time.</summary>
    internal static IMTLBuffer ActiveViewBuffer => _viewBuffers[ActiveViewIndex][Device.FrameIndex];

    /// <summary>Select the view slot for subsequent draws; out of range falls back to the main view.</summary>
    internal static void SetActiveView(int index)
        => ActiveViewIndex = (uint)index < MaxViews ? index : 0;

    internal static void ResetActiveView() => ActiveViewIndex = 0;

    /// <summary>Write the main view constants for the current frame slot; the transpose convention is the same as MatrixBuffer.</summary>
    internal static void WriteMainView(in ViewConstants constants) => WriteViewConstants(0, constants);

    /// <summary>Write the constants for a given view slot and current frame slot; an out-of-range slot is ignored, and the caller need not pre-validate.</summary>
    internal static unsafe void WriteViewConstants(int index, in ViewConstants constants)
    {
        if ((uint)index >= MaxViews) return;
        *(ViewConstants*)_viewBuffers[index][Device.FrameIndex].Contents = constants.Transposed();
    }

    /// <summary>
    /// Push the view constants onto buffer(11) of both VS and FS. Binding resets to zero after a Metal Pass switch, so every draw must carry it itself.
    /// The FS-side reference only appears in the S1 branch (<c>viewc.cameraPos.w</c> as the view slot gate); the shadow channel's PSO has no FS function,
    /// and Metal allows unconsumed fragment state to be cached on that encoder, so no branching is done here.
    /// </summary>
    internal static void BindView(IMTLRenderCommandEncoder enc, IMTLBuffer view)
    {
        enc.SetVertexBuffer(view, 0, ViewConstantsSlot);
        enc.SetFragmentBuffer(view, 0, ViewConstantsSlot);
    }

    static unsafe IMTLBuffer CreateViewBuffer()
    {
        var buffer = Device.MtlDevice.CreateBuffer((nuint)Unsafe.SizeOf<ViewConstants>(), MTLResourceOptions.CpuCacheModeDefault);
        // Even frame slots never written (e.g. immediate 2D mode never calls Update) must be a usable identity view rather than an all-zero projection.
        *(ViewConstants*)buffer.Contents = ViewConstants.ScreenSpace.Transposed();
        return buffer;
    }

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
        //
        // 2-6 clause 7: material-fetch LOD bias, injected as a literal rather than carried in the material buffer.
        // The value is fixed for the process (RenderQuality is locked before graphics initialization), so a buffer field
        // would cost a per-draw write to express something the Metal compiler can fold. It is bound to a local because
        // every library variant below needs it, including the shadow one: MetalShaderSource is a single translation unit
        // holding fragment_main, so an undefined macro would break a compile that only asks for vertex_main.
        var lodBiasDefine = "#define TEXTURE_LOD_BIAS " + RenderQuality.Current.TextureLodBiasLiteral + "\n";
        var msl = (Device.HdrSceneColor ? "#define HDR_CHAIN 1\n" : "#define HDR_CHAIN 0\n")
            + (RenderQuality.Current.ShadowsEnabled ? "#define SHADOW_ENABLED 1\n" : "#define SHADOW_ENABLED 0\n")
            // Step 6:
            // DDGI tier selection now prefers Settings.RenderQuality so it can be persisted,
            // while null falls back to the static default source.
            // This matches the gate used by DdgiEffect.Initialize,
            // ensuring the main shader variant and atlas resources are created in sync.
            + ((Season.Basic.DeviceServices.BaseApp?.Settings?.RenderQuality?.GlobalIllumination ?? RenderQuality.DefaultGlobalIllumination) == Season.Rendering.GiMode.Ddgi ? "#define DDGI_ENABLED 1\n" : "#define DDGI_ENABLED 0\n")
            + "#define SHADOW_PASS 0\n#define VELOCITY_OUTPUT 0\n#define NORMAL_OUTPUT 0\n" + lodBiasDefine + MetalShaderSource;
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

        // Work package D: the capability switch and placeholder frame constant page for transparent-stage volumetric fog sampling.
        // Same origin as VK CreateDefaultFogFrameBuffers + SceneFogSamplingBaked:
        // the tier is locked before graphics initialization and never flips afterwards, so it is evaluated only once.
        SceneFogSamplingBaked = RenderQuality.Current.RequiresAtmosphereSceneDepth(
            Device.HdrSceneColor, true, Season.Rendering.Immediate2DMode.Enabled);
        DefaultFogFrameBuffer = Device.MtlDevice.CreateBuffer(
            (nuint)(Season.Rendering.Effects.VolumetricFogEffect.FrameVectorCount * 16), MTLResourceOptions.CpuCacheModeDefault);
        unsafe
        {
            new Span<byte>((void*)DefaultFogFrameBuffer.Contents,
                Season.Rendering.Effects.VolumetricFogEffect.FrameVectorCount * 16).Clear();
        }
        SceneFogParams = default;
        SceneFogFrame = null;
        SceneFogIntegrated = null;
        SceneFogLocal = null;

        // The view slot pool is created fully in one go: each view has only 256B of constants, and the first appearance of a view (minimap coming online) should not trigger an allocation mid-frame.
        _viewBuffers = new IMTLBuffer[MaxViews][];
        for (int v = 0; v < MaxViews; v++)
        {
            _viewBuffers[v] = new IMTLBuffer[Device.frameCount];
            for (int i = 0; i < _viewBuffers[v].Length; i++)
                _viewBuffers[v][i] = CreateViewBuffer();
        }
        ScreenViewBuffer = CreateViewBuffer();

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
                + "#define SHADOW_ENABLED 1\n#define SHADOW_PASS 1\n#define VELOCITY_OUTPUT 0\n#define NORMAL_OUTPUT 0\n#define DDGI_ENABLED 0\n" + lodBiasDefine + MetalShaderSource;
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
                // A0.2: the normal slot only ever rides on the velocity library, so the library count stays at five.
                + (RenderQuality.Current.EffectiveSceneNormals ? "#define NORMAL_OUTPUT 1\n" : "#define NORMAL_OUTPUT 0\n")
                + "#define SHADOW_PASS 0\n#define VELOCITY_OUTPUT 1\n" + lodBiasDefine + MetalShaderSource;
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
            + "#define SHADOW_PASS 0\n#define VELOCITY_OUTPUT 0\n#define NORMAL_OUTPUT 0\n" + lodBiasDefine + MetalShaderSource;
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
            + "#define SHADOW_PASS 0\n#define VELOCITY_OUTPUT 0\n#define NORMAL_OUTPUT 0\n#define OUTLINE_MASK 1\n" + lodBiasDefine + MetalShaderSource;
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
        DefaultFogFrameBuffer?.Dispose(); DefaultFogFrameBuffer = null!;
        foreach (var viewSlot in _viewBuffers)
            foreach (var view in viewSlot) view?.Dispose();
        _viewBuffers = [];
        ActiveViewIndex = 0;
        ScreenViewBuffer?.Dispose(); ScreenViewBuffer = null!;
        SceneFogParams = default;
        SceneFogFrame = null;
        SceneFogIntegrated = null;
        SceneFogLocal = null;
        SceneFogSamplingBaked = false;
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
        // Work package D: during collection no encoder exists at all (Metal only has an encoder after BeginPass),
        // so only the state is recorded here; the PSO and DSS are determined by the Pass where replay happens (main scene / velocity MRT / Overlay / OutlineMask).
        if (MTLSceneDrawCollection.Active is { } collecting)
        {
            collecting.RecordPipeline(mode, doubleSided, depthWrite);
            return;
        }

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
    /// Metal's face culling is encoder dynamic state, and <see cref="SetPipeline"/> can only express None and Back,
    /// whereas the front-face pass of the two-pass double-sided rendering needs Front set separately, so there must be a wrapper that the collection phase can intercept.
    /// The collection phase only records, and it is applied to the encoder at replay time.
    /// </summary>
    internal static void SetCullMode(IMTLRenderCommandEncoder enc, MTLCullMode cull)
    {
        if (MTLSceneDrawCollection.Active is { } collecting)
        {
            collecting.RecordCull(cull);
            return;
        }
        enc.SetCullMode(cull);
    }

    /// <summary>
    /// Push the current frame's transparent-stage fog sampling trio onto the encoder:
    /// the frame constant array is in FS buffer(4), the gating float4 is in FS buffer(5), and the two read-only volumes are in texture(11)/texture(12).
    ///
    /// When off, it writes a zero gate plus placeholder resources, so the <c>if (fogParams.x &gt; 0.5)</c> in MSL is never entered,
    /// and it therefore never reads the not-yet-composited volume textures (although the placeholder volume is a 1x1x1 black body, the code path does not sample at all).
    ///
    /// It must be called once per command: all bindings reset to zero after a Metal Pass switch,
    /// and there is no descriptor set reusable across commands, which is exactly why replay needs no "re-write" discipline.
    /// </summary>
    internal static unsafe void BindSceneFog(IMTLRenderCommandEncoder enc)
    {
        bool sampling = SceneFogParams.X > 0.5f && SceneFogFrame != null
            && SceneFogIntegrated != null && SceneFogLocal != null;
        // Stage one copy on the stack and then push it: SetFragmentBytes copies the bytes into the command stream immediately, so the stack page's lifetime is sufficient.
        // S5: weather follows sampling ("whether this view has fog"), no longer independently checking ActivePassId==SceneTransparent
        // — the deferredScene view is also in that pass, but does not composite fog or weather.
        Vector4* p = stackalloc Vector4[1];
        *p = new Vector4(sampling ? 1 : 0, sampling && FrameSchedule.WeatherComposited ? 1 : 0, 0, 0);
        enc.SetFragmentBytes((IntPtr)p, 16, SceneFogParamsSlot);
        enc.SetFragmentBuffer(sampling ? SceneFogFrame! : DefaultFogFrameBuffer, 0, SceneFogFrameSlot);
        var dummy = MTLTexture3D.DummyBlack.Image;
        enc.SetFragmentTexture(sampling ? SceneFogIntegrated! : dummy, SceneFogIntegratedSlot);
        enc.SetFragmentTexture(sampling ? SceneFogLocal! : dummy, SceneFogLocalSlot);
    }

    /// <summary>
    /// Unified draw entry.
    /// Bind the vertex buffer at slot 0 and the instance buffer at slot 2,
    /// then call DrawIndexedPrimitives according to instanceCount, where 1 means non-instanced and N means instanced.
    /// When instanceBuffer is null, IdentityInstanceBuffer is used automatically.
    /// </summary>
    /// <param name="indirectBuffer">
    /// A7 main view: when set, the index count, instance count and baseVertex come from one 20-byte record at
    /// <paramref name="indirectOffset"/>, and indexCount/instanceCount are ignored. instanceBufferOffset then selects the
    /// main-view segment of the reordered stream, for the reason given on <see cref="DrawShadowPrimitiveIndirect"/>.
    /// </param>
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
        nuint prevBoneBufferOffset = 0,
        IMTLBuffer? viewBuffer = null,
        IMTLBuffer? indirectBuffer = null,
        nuint indirectOffset = 0)
    {
        // Work package D: this is the single exit for all 3D paths (model / instanced model / instanced mesh / highlight shell / sprite shell box),
        // so intercepting once covers all PrimitiveData draws. The collection phase submits no draw; it only hands immutable handles to the replay queue.
        if (MTLSceneDrawCollection.Active is { } collecting)
        {
            collecting.CapturePrimitive(primitiveData, primitiveVertexBuffer, primitiveIndexBuffer,
                matrixBuffer, materialBuffer, lightBuffer, boneBuffer, morphBuffer, instanceBoneBuffer,
                indexType, indexCount, indexBufferOffset, instanceBuffer, instanceBufferOffset, instanceCount,
                instanceBoneBufferOffset, prevInstanceBuffer, prevBoneBuffer, prevBoneBufferOffset,
                viewBuffer ?? ActiveViewBuffer, indirectBuffer, indirectOffset);
            return;
        }

        // VS: buffer(0)=[[stage_in]] from vertex descriptor
        enc.SetVertexBuffer(primitiveVertexBuffer, 0, 0); // VB slot 0
        enc.SetVertexBuffer(matrixBuffer, 0, 1);          // b1 Matrices
        BindView(enc, viewBuffer ?? ActiveViewBuffer);     // b11 ViewConstants
        enc.SetVertexBuffer(boneBuffer, 0, 3);            // b3 BoneMatrices
        enc.SetVertexBuffer(materialBuffer, 0, 4);        // b2 MaterialParams (read by VS)
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
        // An indirect draw's reordered stream is offset to its segment, but the prev stream is still the CPU-ordered one the
        // VS indexes by the decoded original instance, so it starts at 0.
        enc.SetVertexBuffer(prevInstanceBuffer ?? instBuf, indirectBuffer != null ? 0 : instanceBufferOffset, 9);
        enc.SetVertexBuffer(prevBoneBuffer ?? boneBuffer, prevBoneBufferOffset, 10);

        if (indirectBuffer != null)
        {
            enc.DrawIndexedPrimitives(primitiveType, indexType,
                primitiveIndexBuffer, indexBufferOffset, indirectBuffer, indirectOffset);
            return;
        }

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
        enc.SetVertexBuffer(matrixBuffer, 0, 1);          // b1 Matrices
        BindView(enc, MainViewBuffer);                    // b11 ViewConstants (shadow does not read view/proj, placeholder kept)
        enc.SetVertexBuffer(boneBuffer, 0, 3);            // b3 BoneMatrices
        if (bindMaterial)
        {
            enc.SetVertexBuffer(materialBuffer, 0, 4);    // b2 MaterialParams (VS reads the deformation flag)
            enc.SetVertexBuffer(morphBuffer, 0, 5);       // b4 MorphDeltas
        }
        enc.SetVertexBuffer(instanceBoneBuffer, instanceBoneBufferOffset, 6); // b5 InstanceBoneMatrices

        var instBuf = instanceBuffer ?? IdentityInstanceBuffer;
        enc.SetVertexBuffer(instBuf, instanceBufferOffset, 2);

        enc.DrawIndexedPrimitives(MTLPrimitiveType.Triangle, indexCount, indexType,
            primitiveIndexBuffer, 0, instanceCount);
    }

    /// <summary>
    /// A7: the shadow indirect draw exit, structurally identical item by item to <see cref="DrawShadowPrimitive"/>, differing only in that "how many to draw" is given by the GPU-side argument buffer.
    /// The index count, instance count and baseVertex all read the same 20-byte record (whose layout matches D3D12_DRAW_INDEXED_ARGUMENTS),
    /// so indexCount and instanceCount are no longer taken here — passing them in would only clash with the values in the argument buffer.
    /// The overload is drawIndexedPrimitives:indexType:indexBuffer:indexBufferOffset:indirectBuffer:indirectBufferOffset:,
    /// with the index buffer first and the argument buffer second.
    /// </summary>
    /// <param name="instanceBufferOffset">
    /// The start of the slot segment in the reordered stream after culling. An offset is placed here rather than the firstInstance in the argument buffer,
    /// because both Metal and Vulkan count baseInstance into gl_InstanceIndex, which would shift the instance stream indices as a whole and misalign them.
    /// </param>
    /// <param name="indirectOffset">The byte offset of this primitive in the argument table.</param>
    public static void DrawShadowPrimitiveIndirect(
        IMTLRenderCommandEncoder enc,
        PrimitiveData primitiveData,
        IMTLBuffer primitiveVertexBuffer,
        IMTLBuffer primitiveIndexBuffer,
        IMTLBuffer matrixBuffer,
        IMTLBuffer materialBuffer,
        IMTLBuffer boneBuffer,
        IMTLBuffer morphBuffer,
        IMTLBuffer instanceBoneBuffer,
        MTLIndexType indexType,
        IMTLBuffer instanceBuffer,
        nuint instanceBufferOffset,
        IMTLBuffer indirectBuffer,
        nuint indirectOffset,
        bool bindMaterial = true)
    {
        enc.SetVertexBuffer(primitiveVertexBuffer, 0, 0); // VB slot 0
        enc.SetVertexBuffer(matrixBuffer, 0, 1);          // b1 Matrices
        BindView(enc, MainViewBuffer);                    // b11 ViewConstants (shadow does not read view/proj, placeholder kept)
        enc.SetVertexBuffer(boneBuffer, 0, 3);            // b3 BoneMatrices
        if (bindMaterial)
        {
            enc.SetVertexBuffer(materialBuffer, 0, 4);    // b2 MaterialParams (VS reads the deformation flag)
            enc.SetVertexBuffer(morphBuffer, 0, 5);       // b4 MorphDeltas
        }
        enc.SetVertexBuffer(instanceBoneBuffer, 0, 6);    // b5 InstanceBoneMatrices

        enc.SetVertexBuffer(instanceBuffer, instanceBufferOffset, 2);

        enc.DrawIndexedPrimitives(MTLPrimitiveType.Triangle, indexType,
            primitiveIndexBuffer, 0, indirectBuffer, indirectOffset);
    }

    static IMTLSamplerState CreateStaticSampler()
    {
        // 2-6 clause 6: anisotropy goes on the material sampler only. Metal caps the count at 16 and rejects 0, which
        // is exactly what ClampAnisotropy guarantees, so no Metal-specific ceiling is needed here. The sampler state is
        // created once during Pipeline initialization, so a change to the knob takes a restart as on the other backends.
        int anisotropy = RenderQuality.ClampAnisotropy(RenderQuality.Current.TextureMaxAnisotropy);
        var desc = new MTLSamplerDescriptor
        {
            MinFilter = MTLSamplerMinMagFilter.Linear,
            MagFilter = MTLSamplerMinMagFilter.Linear,
            MipFilter = MTLSamplerMipFilter.Linear,
            SAddressMode = MTLSamplerAddressMode.ClampToEdge,
            TAddressMode = MTLSamplerAddressMode.ClampToEdge,
            RAddressMode = MTLSamplerAddressMode.ClampToEdge,
            CompareFunction = MTLCompareFunction.Always,
            // 1 is Metal's own default and means isotropic, so the isotropic path stays identical to the pre-2-6 state.
            MaxAnisotropy = (nuint)anisotropy,
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
            // Alpha accumulates coverage with "over" instead of replacing it, so a near-invisible picking proxy cannot
            // zero the coverage of a secondary view that is composited through its alpha (see DX Pipeline).
            att.SourceAlphaBlendFactor = MTLBlendFactor.One;
            att.DestinationAlphaBlendFactor = MTLBlendFactor.OneMinusSourceAlpha;
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

            // A0.2: SceneNormal at slot 2 follows the velocity rules exactly (no blend, Opaque-only writes).
            if (RenderQuality.Current.EffectiveSceneNormals)
            {
                var natt = psd.ColorAttachments[2];
                natt.PixelFormat = MTLPixelFormat.RGBA16Float;
                natt.BlendingEnabled = false;
                natt.WriteMask = vatt.WriteMask;
            }
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

    /// <summary>
    /// Main-scene MSL. Work package D prepends the fog sampling glue to the top of the file,
    /// so the four exits of fragment_main (text / sky / Sprite2D / PBR) share the same ApplySceneFog shape with VK and DX,
    /// and FogSampler's structure and math come directly from <see cref="Season.Rendering.Effects.VolumetricFogEffect.SamplingMsl"/>, with no second copy.
    /// Because it must be concatenated, this was changed from const to static readonly; all use sites are just runtime string concatenation, so the semantics are unchanged.
    /// </summary>
    static readonly string MetalShaderSource = @"
#include <metal_stdlib>
#include <simd/simd.h>

using namespace metal;

" + Season.Rendering.Effects.WeatherShaders.ContextMsl
  + Season.Rendering.Effects.VolumetricFogEffect.SceneSamplingMsl + @"
struct Matrices {
    float4x4 world;
    // Contract clause 6 of 2-3:
    // the previous world matrix lives at offset 64 and follows the same transpose contract as world.
    // The CPU uploads Transpose(M), and MSL reads column-major data back as the engine row-major M,
    // so post-multiplication with v * M remains correct everywhere.
    // All-zero means not written, because the C# MatrixBuffer default is zero and Transpose(zero) is still zero.
    float4x4 prevWorld;
};

// Per-view constants (256 bytes, byte-aligned with C# ViewConstants), VS buffer(11).
// Same transpose contract as Matrices; an all-zero prevViewProjection means no history.
struct ViewConstants {
    float4x4 view;
    float4x4 projection;
    float4x4 prevViewProjection;
    float4 cameraPos;
    float4 velocityParams;
    float4 params0;
    float4 reserved0;
};

struct BoneMatrices {
    float4x4 boneMatrices[100];
};

// 1-2 lighting system:
// unified light structure, 64 bytes, byte-aligned with C# GpuLight.
// The all-float4 layout naturally avoids the float3-alignment slip in MSL constant space, the same root cause as the old padding0 issue.
struct GpuLight {
    float4 posRange;        // xyz=world position (ignored for directional), w=attenuation radius range (<=0 degenerates to pure 1/d²)
    float4 colorIntensity;  // xyz=linear color, w=intensity
    float4 dirType;         // xyz=light direction (used for spot/directional), w=type (0=point, 1=spot, 2=directional)
    float4 spotParams;      // x=cosInner, y=cosOuter (precomputed on CPU), zw=reserved
};

struct SceneLights {
    float4 cameraPos;
    float4 ambientParams;   // xyz=ambient color, w=intensity (replacing the old hardcoded 0.5)
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
    float4x4 cascadeViewProj[4];   // offset 560: CSM cascade light-space matrices (used by slot 0..2)
    float4x4 spotShadowViewProj;   // offset 816: spot light-space matrix (slot 3)
    float4 cascadeSplits;          // offset 880: per-cascade view-space far bounds (x/y/z used, w reserved)
    float4 shadowParams0;          // offset 896: x=sunEnabled, y=cascadeCount, z=1/atlasSize, w=reserved
    float4 shadowParams1;          // offset 912: x=spotEnabled, y=shadowStrength, zw=reserved
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
    float4 irradianceSH9[9];       // offset 960, tail 1104B
    // DDGI clause 10 of 2-4, starting at offset 1104:
    // giParams0=probeGridMin.xyz and spacing,
    // giParams1=gridXYZ as float and GiIntensity (blend weight, never a scale on probeIrr),
    // giParams2=normalBias, chebyshev, atlasReady, and fadeBandCells.
    float4 giParams0;              // offset 1104
    float4 giParams1;              // offset 1120
    float4 giParams2;              // offset 1136, tail 1152B
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
    float4 cloudParams0;           // offset 1328 (w=layer count=the sole gate for cloud consumption)
    float4 cloudParams1;           // offset 1344
    // Step E of 2-5, starting at offset 1360:
    // aerial-perspective 3D LUT consumption parameters.
    // x=maxDistanceKm, where values greater than 0 enable AP and act as the only gate.
    // y=Intensity, where 0 means identity composition.
    float4 apParams0;                      // offset 1360
    // Regional weather: two header rows followed by eight rows per region; the 1376B prefix is preserved, total length 2448B.
    float4 weatherParams0;                 // offset 1376
    float4 weatherParams1;                 // offset 1392
    float4 weatherRegions[64];             // offset 1408, trailing 2432B
    // 2-7 lightning (offset 2432): xyz = world coordinates of the current dominant strike inside the cloud / w = return-stroke envelope brightness 0..1.
    // w is the sole master switch; under non-thunderstorm weather, RegionalQuality Off, or Showcase mode the whole field is zero.
    float4 weatherFlash;                   // offset 2432, trailing 2448B
    // 2-8 virtual ground for ambient specular (offset 2448): xyz = ground radiance (linear HDR, including albedo/direct light/sky SH9/1pi),
    // w = world Y where the ground lies. Injected only by SkyLighting.Apply, read only in the procedural sky branch.
    float4 reflectionParams0;              // offset 2448, trailing 2464B
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
    uint hasPrevBones;          // Clause 8(b) of 2-3: prev bone palette availability sentinel
    uint hasPrevInstanceWorld;  // Clause 8(c) of 2-3: prev instance world stream availability sentinel
    uint hasPrevMorph;          // Clause 8(c) of 2-3: prev morph weight availability sentinel
    float4 morphWeights;
    float4 prevMorphWeights;    // previous-frame weights for the non-instanced path (inlined in the CB, sourced from the CPU shadow copy)
    // Vertex swell (currently only the water surface writes it): x=half peak-to-trough amplitude (meters, 0=off), y=current phase, z=previous-frame phase, w=falloff radius (meters).
    // See SwellEvaluate below.
    float4 vertexWave;
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

// -- Vertex swell: the stylized moving sea, evaluated entirely in the vertex stage --
//
// Three radial sines centered on the island rather than Gerstner: no crest sharpening and no high-frequency
// ripple layer, so the silhouette stays round and reads as the same flat-shaded cartoon water the static path
// already has, only in motion. Three slightly offset centers keep the wavefronts from reading as perfect
// concentric circles, which is what a single radial wave would look like.
//
// The components advance at 1x / 2x / 3x of the shared phase. The integer ratios are what make wrapping the phase
// at 2*PI seamless: sin(k*r + n*PHI) is exactly periodic in PHI with period 2*PI/n, so a wrap is invisible. They
// also put the shorter components slightly faster, which is where deep-water dispersion goes anyway (c = sqrt(g/k)).
//
// The mask is read from the UV channel. Sea.BuildSurfaces bakes a smoothstep distance to the island square into it -
// the swell's own band, 6m to 24m offshore, deliberately narrower than the 60m band that damps the baked relief.
// It reaches zero before the waterline, so a crest can never climb the beach, while the first visible crest sits
// close enough to the shore (and to the east-coast rocks) to actually be seen.
//
// Written as three named components rather than a loop over constant arrays: dynamic indexing into a const array
// is the one construct where the four backends' compilers disagree about what is legal, and three explicit terms
// are what all four of them fold identically anyway.
float SwellEvaluate(float2 p, float phase, float amp, thread float2& grad)
{
    float2 d0 = p;                            // 90m component, weight 0.55
    float2 d1 = p - float2(37.0, -24.0);      // 55m component, weight 0.30
    float2 d2 = p - float2(-61.0, 43.0);      // 34m component, weight 0.15
    float r0 = max(length(d0), 1.0);
    float r1 = max(length(d1), 1.0);
    float r2 = max(length(d2), 1.0);
    // The plus sign is the travel direction. A crest sits where k*r + ph = 2*PI*n + PI/2, so r shrinks as the phase
    // grows: the swell runs inward, toward the island, which is the only direction this ocean needs. A minus would
    // radiate the rings outward instead, like a stone dropped in a pond.
    float p0 = 0.06981317 * r0 + phase;
    float p1 = 0.11423740 * r1 + phase * 2.0;
    float p2 = 0.18478989 * r2 + phase * 3.0;
    float c0 = cos(p0), c1 = cos(p1), c2 = cos(p2);
    float h = amp * (0.55 * sin(p0) + 0.30 * sin(p1) + 0.15 * sin(p2));
    // d(a*sin(k*r + ph))/dp = a*k*cos(k*r + ph) * d/r
    grad = amp * (0.55 * 0.06981317 * c0 / r0 * d0
                + 0.30 * 0.11423740 * c1 / r1 * d1
                + 0.15 * 0.18478989 * c2 / r2 * d2);
    return h;
}

struct VertexOut {
    float4 position [[position]];
    float3 vWorldPos;
    float2 vUV;
    float3 vNormal;
    float4 vTangent;
    float4 vInstanceColor;
    float vViewDepth;       // 1-5: view-space depth (used for cascade selection; always 0 in the shadow pass)
#if VELOCITY_OUTPUT
    float4 vPrevClip;       // 2-3: previous-frame un-jittered clip-space position (used by the FS to compute velocity)
#endif
};

vertex VertexOut vertex_main(
    VertexIn vin [[stage_in]],
    uint vertexId                   [[vertex_id]],
    uint instanceId                 [[instance_id]],
    constant Matrices& mat           [[buffer(1)]],
    constant ViewConstants& viewc    [[buffer(11)]],
    constant BoneMatrices& bones     [[buffer(3)]],
    constant MaterialParams& mparams [[buffer(4)]],
    const device float* morphDeltas  [[buffer(5)]],
    // P2 L1: row instanceSlot doubles as that instance's pose-pool tag (see the decode below), so a pooled palette addresses
    // joints from the row the tag names instead of from the stride product. Row count is only ever Capacity * Stride, and
    // instanceSlot < Capacity, so the tag fetch stays in bounds whatever the pool does.
    // Declared unconditionally, unlike prevBoneMatrices: the tag is not a velocity feature, it selects the address the
    // current frame reads too.
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

    // Original instance index: what every per-instance buffer (glyphs, bone palettes, the prev instance stream) is laid out by.
    // Direct draws reach it as [[instance_id]], which already includes the base instance. A7 indirect draws (main view and shadow slots alike)
    // are compacted, so [[instance_id]] is only the position in the visible list there; their gather stamps float(original index + 1) into M14
    // (instanceWorld0.w) instead, which is otherwise exactly 0 because every instance transform is affine. Decoding restores M14 to 0,
    // so the world matrix below is bit-identical on both paths, and an untagged record -- every direct draw -- keeps the old index.
    // The SHADOW_PASS variant compiles this same block, so a skinned group's shadow segment reads its own palette too. Mirrors the DX VS.
    uint instanceSlot = instanceId;
    float4 instanceRow0 = vin.instanceWorld0;
    if (mparams.isInstanced == 1u && instanceRow0.w >= 1.0) {
        instanceSlot = uint(instanceRow0.w) - 1u;
        instanceRow0.w = 0.0;
    }

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

    // P2 L1 / P3 L2: pose-pool tag (mirrors the DX and VK VS). Each instance's palette block ends with one row that holds nothing
    // but the tag: the block is bonePaletteStride rows, the first (bonePaletteStride - 1) of which are joints. Bytes 48 and 52 of
    // that last row carry the current- and previous-frame block this instance draws its joints from.
    //
    // The tag row is not the row whose index equals the instance slot. instanceBoneMatrices[i] resolves to row i of the flat
    // palette, which belongs to instance i / bonePaletteStride at joint i % bonePaletteStride - a live joint of a neighbour. That
    // address stayed harmless only while every one of those rows held zero in both lanes, which is why this lookup now goes
    // through the stride product instead.
    //
    // The two lanes are M41 and M42, the last row of the matrix. An affine palette guarantees they are zero in a joint row - it
    // holds transpose(IBM * world * inverseMesh), every factor is affine so the product's last column is (0, 0, 0, 1), and the
    // transpose moves that column into the last row. Nothing multiplies by the tag row, so its two lanes are plain storage. MSL
    // indexes float4x4 by column, so that row's first two lanes are [3].x and [3].y of the same row-major upload the other three
    // backends read.
    //
    // An unpooled palette decodes to 0 and keeps the historical instanceSlot * stride address below. The decode only ever selects
    // an address and never rewrites a matrix, so both branches are bit-exact. Gated on isSkinned as well: a plain instanced draw
    // binds no palette, and its block would be an out-of-bounds fetch rather than a tag row.
    uint stride = max(mparams.bonePaletteStride, 1u);
    uint boneBase = mparams.isInstanced == 1u ? instanceSlot * stride : 0u;
    uint prevBoneBase = boneBase;
    if (mparams.isInstanced == 1u && mparams.isSkinned != 0u) {
        uint tagRow = boneBase + stride - 1u;
        uint pooledRow = as_type<uint>(instanceBoneMatrices[tagRow][3].x);      // PoseTagCurrentOffset = 48
        uint pooledPrevRow = as_type<uint>(instanceBoneMatrices[tagRow][3].y);  // PoseTagPreviousOffset = 52
        if (pooledRow != 0u)
            boneBase = pooledRow;
        if (pooledPrevRow != 0u)
            prevBoneBase = pooledPrevRow;
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
                uint boneIndex = mparams.isInstanced == 1u ? boneBase + uint(idx) : uint(idx);
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
            instanceRow0,
            vin.instanceWorld1,
            vin.instanceWorld2,
            vin.instanceWorld3));
    } else {
        worldMatrix = mat.world;
    }

    float4 worldPos = localPos * worldMatrix;

#if !SHADOW_PASS
    // Vertex swell. vertexWave.x is zero for every mesh except the sea, so the whole block is skipped by a single
    // compare. Only the water near the camera is displaced: past vertexWave.w the mesh keeps its baked relief, which
    // is all the eye resolves at that range, and the six trig calls never run on the far half of a 65025-vertex sheet.
    //
    // The distance is the full 3D distance to the camera, not the horizontal one. That single choice covers all three
    // cases the sea has to survive: standing on the island (dy is a few meters, so it is the same as horizontal),
    // sailing (camera at the water surface, same again), and flying over it (dy is the altitude, so the water directly
    // below is already past the radius and the whole sheet stops displacing without needing a second altitude rule).
    //
    // The gate reads the per-view camera position rather than a per-draw copy, so it stays correct for every view
    // of the frame. It is deliberately not evaluated in the shadow pass: the sea never casts (CastShadows=false),
    // and the shadow view slots carry no camera position, so the gate would fade the whole sheet against the origin.
    float2 swellGrad = float2(0.0, 0.0);
    float prevSwellHeight = 0.0;
    if (mparams.vertexWave.x > 0.0) {
        float fadeEnd = mparams.vertexWave.w;
        float rangeFade = fadeEnd > 0.0
            ? 1.0 - smoothstep(fadeEnd * 0.62, fadeEnd, length(worldPos.xyz - viewc.cameraPos.xyz))
            : 1.0;
        float swellAmp = mparams.vertexWave.x * rangeFade * vin.inUV.x;
        if (swellAmp > 0.0) {
            worldPos.y += SwellEvaluate(worldPos.xz, mparams.vertexWave.y, swellAmp, swellGrad);
            float2 gradUnused;
            prevSwellHeight = SwellEvaluate(worldPos.xz, mparams.vertexWave.z, swellAmp, gradUnused);
        }
    }
#endif

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
    float4 viewPos  = worldPos * viewc.view;
    o.position = viewPos * viewc.projection;
    o.vViewDepth = viewPos.z;
#endif

#if VELOCITY_OUTPUT
    // Contract clauses 6 and 8 of 2-3:
    // the sentinel for previous matrices not written is all-zero.
    // A non-zero fourth column in prevViewProjection means valid history.
    // For perspective and orthographic matrices that column is always non-zero.
    float4 prevClip_ = float4(0.0);
    if (any(viewc.prevViewProjection[3] != float4(0.0))) {
        // Per-instance history on the instanced path, world rows 0 through 3 plus morph weights,
        // comes uniformly from the previous-instance stream.
        // r3.w == 0 means that slot has not been written yet, such as the first frame or after growth clear,
        // so both world and morph weights fall back to current values.
        bool hasPrevInstance = false;
        float4 pr0 = float4(0.0), pr1 = float4(0.0), pr2 = float4(0.0), pr3 = float4(0.0), prMorphW = float4(0.0);
        if (mparams.isInstanced == 1u && mparams.hasPrevInstanceWorld != 0u) {
            pr0 = prevInstanceStream[instanceSlot * 5u + 0u];
            pr1 = prevInstanceStream[instanceSlot * 5u + 1u];
            pr2 = prevInstanceStream[instanceSlot * 5u + 2u];
            pr3 = prevInstanceStream[instanceSlot * 5u + 3u];
            prMorphW = prevInstanceStream[instanceSlot * 5u + 4u];
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
                    uint boneIndex = mparams.isInstanced == 1u ? prevBoneBase + uint(idx) : uint(idx);
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

        // The swell is time-varying geometry, so the previous-frame position needs the previous-frame phase.
        // Reusing the current height would cancel the wave's own motion out of the velocity buffer and leave the
        // one surface that should be streaking perfectly clean.
        float4 prevWorldPos = prevLocalPos * prevWorldMatrix;
        prevWorldPos.y += prevSwellHeight;
        prevClip_ = prevWorldPos * viewc.prevViewProjection;
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
        uint tBase = instanceSlot * 12u;
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
#if !SHADOW_PASS
    // The swell is a height field added on top of the baked relief, and over that relief the normal is always
    // close to +Y, so n' = normalize(n - (dh/dx, 0, dh/dz)) is the small-slope form of the perturbed normal.
    // Without it the sun glints would stay frozen in place while the geometry moved underneath them, which is
    // exactly the part of the static sea worth keeping.
    o.vNormal = normalize(o.vNormal - float3(swellGrad.x, 0.0, swellGrad.y));
#endif
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
// The result replaces the SH9/ambient term as a blend, and giParams1.w is the weight of that blend rather than a scale on the probe value: see the return statement for why the difference matters.
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

    // 2-4 clause 9: fade the probe contribution out beyond the last probe row. gc is in probe-index units, so trilinear
    // interpolation is only defined over [0, dims-1]; past that the clamp below collapses all eight corners onto a
    // single boundary probe, which hands a distant surface one near-camera probe's irradiance as if it were its own.
    // That reads as a whole surface lit uniformly by an unrelated probe, wobbling with that probe's amortized R2 ray
    // rotation and hysteresis rather than with anything in the scene, and it is the only lighting a face turned away
    // from every light has. The grid is camera-centred and re-snapped every frame in Ddgi.Record, so this covers
    // everything past GiVolumeSize/2, not just distant scenery.
    // Three decisions here, about the seam rather than its silhouette:
    // 1. The band is giParams2.w cells wide, not one. One cell of linear ramp is already a hard-edged ring: it breaks the
    //    gradient at both ends, and once the interior and the exterior differ by any amount that ring is what the eye
    //    locks onto while the volume slides with the camera. Widen it and curve it - s(t)=t*t*(3-2t) is C1 at both ends.
    // 2. The metric stays the axis-aligned max of the per-axis excess, not a radial length. A round band would only
    //    redraw the same seam as a circle, because the shape is not what makes it visible - the step in the blend at the
    //    end of this function is. Clauses 9/13 remove that step, so the band has nothing left to outline.
    // 3. The fade never moves the result to a *different* amount of light: both ends settle on fallback, and an
    //    unoccluded interior produces fallback term for term as well, so inside the band the two sides agree exactly.
    float3 outside = max(max(-gc, gc - (dims - 1.0)), float3(0.0));
    float band = max(lights.giParams2.w, 1.0);
    float boundsFade = saturate(1.0 - max(max(outside.x, outside.y), outside.z) / band);
    boundsFade = boundsFade * boundsFade * (3.0 - 2.0 * boundsFade);

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
    // Step 5 + clause 9 energy contract. wsum/wraw is the fraction of this shading point's interpolation weight that
    // lands on valid probes; boundsFade is the coverage envelope; giParams1.w is how much of the probe field's deviation
    // from the environment the application wants to show. All three scale the *blend*, never the probe value:
    //     result = mix(fallback, probeIrr, valid * boundsFade * GiIntensity)
    // That distinction is the whole of clause 9. A probe stores irradiance in the same units as EvaluateIrradianceSH9:
    // a ray that misses multiplies the same SH radiance coefficients by the same cosine kernel the surface samples them
    // with, and the update divides by the accumulated cosine weight, so the quotient is E(n)/pi. In an unoccluded, empty
    // volume probeIrr therefore equals fallback term for term. Scaling probeIrr would turn any gain into a uniform
    // brightness step between the inside and the outside of a camera-following box - at GiIntensity 0.4 the interior
    // sits at 40% of the exterior, which in an ambient-only frame is the entire visible difference. Scaling the blend
    // instead leaves that case identical for every gain, applies the same gain to real occlusion (probeIrr < fallback)
    // and to real bounce (probeIrr > fallback), and still returns pure fallback at gain 0.
    // If all 8 neighbors are invalid, including the zero-initialized atlas before the first update, wsum is zero and the
    // result is pure fallback with a continuous transition and no threshold jumps or flicker.
    float3 probeIrr = wsum > 1e-6 ? sum / wsum : float3(0.0);
    float vfrac = saturate(wsum / max(wraw, 1e-6)) * boundsFade;
    return mix(fallback, probeIrr, vfrac * lights.giParams1.w);
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
float4 ApplySceneWeather(float4 color,float3 worldPos,constant SceneLights& lights,float4 fogParams) {
    if(fogParams.y>0.5) {
        WeatherRainContext<constant float4*> weather{lights.weatherParams0,lights.weatherParams1,lights.weatherRegions};
        float3 ray=worldPos-lights.cameraPos.xyz; float distance=length(ray);
        float4 rain=weather.WeatherRain(lights.cameraPos.xyz,ray/max(distance,1e-8f),distance);
        color.rgb=color.rgb*rain.w+rain.rgb;
    }
    return color;
}
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
    WeatherCloudContext<constant float4*> weather{lights.weatherParams0,lights.weatherParams1,lights.weatherRegions,cloudNoise,lights.weatherFlash};
    return result*weather.WeatherCloudShadow(worldPos,toLight);
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
// Uses hardware comparison sampling plus the clause 14 rotated eight-tap disk.

// Clause 14: per-pixel orientation for the PCF disk, returned as a unit vector so the sampling loop needs no trigonometry.
// The spatial term is interleaved gradient noise, which is constructed to decorrelate immediate neighbours - the property
// that matters here, because TAA resolves a pixel from its own history and from the pixels next to it. The temporal term
// arrives in shadowParams1.w already spread over the circle by the golden-ratio sequence, and adding it before the turn
// spins the whole kernel frame to frame, which is what turns the sampling error into something a history can average away
// rather than a fixed pattern it would preserve.
inline float2 ShadowKernelRotation(constant SceneLights& lights, float2 pixel) {
    float ign = fract(52.9829189 * fract(dot(pixel, float2(0.06711056, 0.00583715))));
    float angle = (ign + lights.shadowParams1.w) * 6.28318531;
    return float2(cos(angle), sin(angle));
}

// Clause 14: eight taps on a Vogel disk, oriented by rot and scaled to radiusTexels. A Vogel spiral rather than a ring
// because the golden angle fills the disk evenly at any tap count, so this one loop keeps working whatever radius it is
// handed - which is what lets a variable penumbra reuse it later instead of forcing a second sampler into all four
// backends. Each tap advances by one complex multiply by the golden angle, and that stays unit length across all eight.
//
// Clause 15: a negative radiusTexels means its magnitude is a ceiling rather than the radius, and the radius is derived
// per pixel from a blocker search run first on the very same disk. Two properties of this projection setup are what keep
// that search cheap. The separation is measured in stored-depth units, which are the cascade's own depth range normalized
// to [0,1], and CascadedShadow builds every cascade with a depth range of exactly four times its bounding radius against a
// texel footprint of two radii over the tile resolution - so radius cancels out of their ratio and one mapping from
// separation to width is correct in all three cascades with nothing per-cascade uploaded. The mapping is deliberately not
// the textbook light-angle one: a physically sized sun puts the penumbra at a near-constant 0.6 to 0.8 texels in every
// cascade, for that same reason of scales growing together, and 0.7 texels of softening is invisible. Reaching the ceiling
// at a quarter of the depth range keeps every property that matters - hard on contact, widening with separation,
// consistent across cascades - and leaves the absolute width as the artistic knob it has to be to be worth having.
//
// The blocker search reads the atlas as plain depth, which the comparison sampler cannot do - it answers pass/fail. On
// Metal that costs no binding at all: MSL allows a sampler to be declared inside the shader, so unlike the other three
// backends nothing is added to the pipeline's resource layout here. Filtering must be nearest - averaging two depths across
// a silhouette yields a value lying on no surface, and a blocker distance derived from it would be fiction.
constexpr sampler shadowPointSampler(filter::nearest, address::clamp_to_edge);

// shadowNdc is light-space NDC after dividing by w, and sampling is clamped inside the tile to avoid leakage.
inline float SampleShadowTile(depth2d<float> shadowAtlas, sampler shadowSampler, float texel, int slot, float3 shadowNdc, float2 rot, float radiusTexels) {
    float result = 1.0;
    float2 uv = float2(shadowNdc.x * 0.5 + 0.5, 0.5 - shadowNdc.y * 0.5);
    if (uv.x >= 0.0 && uv.x <= 1.0 && uv.y >= 0.0 && uv.y <= 1.0 &&
        shadowNdc.z > 0.0 && shadowNdc.z < 1.0) {
        float2 tileOrigin = float2(slot & 1, slot >> 1) * 0.5;
        float2 atlasUV = tileOrigin + uv * 0.5;

        float radius = abs(radiusTexels);
        // Clause 15: set when the blocker search finds nothing above the receiver, which lets the comparison pass be
        // skipped outright - the answer is already known to be fully lit, and that is the common case over open ground.
        bool unoccluded = false;
        if (radiusTexels < 0.0) {
            // Search at the ceiling radius, because the radius being solved for is not known yet and a search narrower
            // than the final kernel would miss occluders the kernel then samples, which reads as an edge that snaps.
            float2 searchInset = float2(min(texel * (radius + 0.5), 0.2499));
            float2 searchMin = tileOrigin + searchInset;
            float2 searchMax = tileOrigin + 0.5 - searchInset;
            float2 searchDir = rot;
            float searchStepUV = radius * texel;
            float depthSum = 0.0;
            float depthCount = 0.0;
            for (int j = 0; j < 8; ++j) {
                float rj = sqrt((float(j) + 0.5) * 0.125);
                float2 searchUV = clamp(atlasUV + searchDir * (rj * searchStepUV), searchMin, searchMax);
                float stored = shadowAtlas.sample(shadowPointSampler, searchUV);
                // Only depths in front of the receiver are occluders; averaging the rest in would pull the estimate
                // towards the receiver's own surface and collapse the penumbra wherever the disk straddles a silhouette.
                float isBlocker = stored < shadowNdc.z ? 1.0 : 0.0;
                depthSum += stored * isBlocker;
                depthCount += isBlocker;
                searchDir = float2(searchDir.x * -0.7373689 - searchDir.y * 0.6754903,
                                   searchDir.x * 0.6754903 + searchDir.y * -0.7373689);
            }
            if (depthCount < 0.5) {
                unoccluded = true;
            } else {
                // Floor at half a texel: that is the reach of the hardware 2x2 comparison filter, so a smaller disk
                // would collapse all eight taps onto one bilinear footprint and cost eight samples for one result.
                float separation = shadowNdc.z - depthSum / depthCount;
                radius = clamp(radius * separation * 4.0, 0.5, radius);
            }
        }

        if (!unoccluded) {
            // Shrink the quadrant inward by the disk radius plus the half texel the hardware 2x2 comparison filter reaches, so
            // no tap can bleed into a neighbouring quadrant however wide the disk is. Capped just under the half-quadrant so a
            // pathological radius degenerates into sampling the tile centre instead of inverting the clamp bounds.
            float2 inset = float2(min(texel * (radius + 0.5), 0.2499));
            float2 tileMin = tileOrigin + inset;
            float2 tileMax = tileOrigin + 0.5 - inset;

            float2 dir = rot;
            float stepUV = radius * texel;
            float sum = 0.0;
            for (int k = 0; k < 8; ++k) {
                float r = sqrt((float(k) + 0.5) * 0.125);
                float2 sampleUV = clamp(atlasUV + dir * (r * stepUV), tileMin, tileMax);
                sum += shadowAtlas.sample_compare(shadowSampler, sampleUV, shadowNdc.z);
                // Advance by the golden angle 2.39996323 rad, whose (cos, sin) is (-0.7373689, 0.6754903).
                dir = float2(dir.x * -0.7373689 - dir.y * 0.6754903, dir.x * 0.6754903 + dir.y * -0.7373689);
            }
            result = sum * 0.125;
        }
    }
    return result;
}

// Directional light, CSM:
// select the cascade slot by view-space depth, then project into light space, sample it,
// and mix the result into shadowStrength.
//
// Clause 13 normal-offset: geoNormal is the interpolated mesh normal, taken before any normal map perturbs it, because the
// offset has to follow the surface the depth buffer actually holds rather than the shading normal. lightDir points towards
// the light and is only used for the foreshortening factor, so its sign does not matter.
inline float ComputeSunShadow(constant SceneLights& lights, depth2d<float> shadowAtlas, sampler shadowSampler, float3 worldPos, float3 geoNormal, float3 lightDir, float viewDepth, float2 shadowRot) {
    float result = 1.0;
    int cascadeCount = int(lights.shadowParams0.y);
    if (lights.shadowParams0.x >= 0.5 && viewDepth <= lights.cascadeSplits[cascadeCount - 1]) {
        int slot = cascadeCount - 1;
        for (int c = cascadeCount - 1; c >= 0; --c)
            if (viewDepth <= lights.cascadeSplits[c]) slot = c;
        // Pre-multiplication, contract clause 1:
        // raw row-major bytes are read by MSL as M transpose, and M transpose times v is equivalent to CPU-side pos times M.
        float4 lightPos = lights.cascadeViewProj[slot] * float4(worldPos, 1.0);
        float3 ndc = lightPos.xyz / lightPos.w;

        // Clause 13: displace the lookup along the surface normal, in this cascade's tile NDC rather than in world space.
        // One texel is a fixed 2/(atlasSize/2) = 4*shadowParams0.z of tile NDC however wide the cascade is, so a single
        // uploaded texel count is right for all three cascades and no per-cascade world scale has to reach the shader.
        //
        // The direction is the normal put through the same matrix the position went through, as a direction (w=0), then
        // normalized. Writing the multiply in the same order as lightPos above means the transposed read described there
        // applies to the normal identically, which keeps this a transcription of the other three backends. Normalizing is the
        // load-bearing step: an orthographic projection scales every direction by 2/cascadeWidth, so the raw projected length
        // carries the cascade's extent and using it unnormalized would make the offset inversely proportional to cascade
        // width - far too large near, far too small far out. That length also carried the sqrt(1 - NdotL^2) foreshortening,
        // which is why it has to be reapplied by hand once the scale is gone. Its effect is that the offset vanishes on
        // surfaces facing the light and peaks on grazing ones, which is exactly where a texel's worth of depth quantization
        // spans the most world distance.
        //
        // Only xy moves. Displacing z would push stored depth away from the light again, which is the peter-panning this
        // clause exists to avoid buying. The length guard costs nothing real: the projected normal only collapses when the
        // surface faces the light dead on, and that is precisely where the foreshortening would have zeroed the offset too.
        //
        // The texel count applied is not shadowParams1.z alone but the larger of it and the reach of clause 14's disk, which is
        // the disk radius in texels plus the half texel the hardware 2x2 comparison filter widens every tap by. The two knobs
        // are one calibration: an offset shorter than the disk leaves the disk's outer taps sampling the receiver's own depth on
        // grazing surfaces, and that reads as the whole face losing a fraction of its taps - a uniform darkening rather than
        // speckle - with the fraction changing every frame because clause 14 rotates the disk per frame. Taking the max makes
        // that an invariant of the code instead of a note asking whoever retunes the radius to remember the offset.
        // shadowParams1.z remains the floor, so it still decides the single-tap case where the radius is zero, and raising it
        // past the disk's reach still works and still buys nothing but peter-panning.
        if (lights.shadowParams1.z > 0.0) {
            float2 ndcN = (lights.cascadeViewProj[slot] * float4(geoNormal, 0.0)).xy;
            float ndcLen = length(ndcN);
            if (ndcLen > 1e-8) {
                float ndl = dot(geoNormal, lightDir);
                float sinTheta = sqrt(saturate(1.0 - ndl * ndl));
                float offsetTexels = max(lights.shadowParams1.z, abs(lights.shadowParams0.w) + 0.5);
                ndc.xy += (ndcN / ndcLen) * (sinTheta * offsetTexels * 4.0 * lights.shadowParams0.z);
            }
        }

        float visibility = SampleShadowTile(shadowAtlas, shadowSampler, lights.shadowParams0.z, slot, ndc, shadowRot, lights.shadowParams0.w);

        // Taper the contribution out across the last tenth of shadow range rather than ending the range at a comparison. At the
        // boundary the branch above admits one pixel and rejects its neighbour, so the two differ by the full shadow strength
        // across a single pixel; TAA's jitter then walks individual pixels back and forth over it every frame, which turns a
        // static boundary into a flickering one wherever geometry straddles it - and geometry at the edge of shadow range is
        // usually large and distant, so a whole face flickers together. The taper reaches exactly 0 at the boundary, where the
        // mix yields 1.0, which is what the rejected side already returns, so the two sides now meet continuously and the
        // branch is left as the pure optimization it was meant to be. Read from cascadeSplits inside the branch rather than
        // hoisted above it, because cascadeCount is only known to be at least one here.
        float shadowFar = lights.cascadeSplits[cascadeCount - 1];
        float fade = saturate((shadowFar - viewDepth) / max(shadowFar * 0.1, 1e-6));
        result = mix(1.0, visibility, lights.shadowParams1.y * fade);
    }
    return result;
}

// Spot light:
// single tile at slot 3, sampled after perspective divide.
// No normal-offset here (clause 13 covers the cascades only): under a perspective projection a texel covers a world
// distance that grows with depth, so a single texel count cannot describe the offset. The spot keeps the PSO depth biases.
inline float ComputeSpotShadow(constant SceneLights& lights, depth2d<float> shadowAtlas, sampler shadowSampler, float3 worldPos, float2 shadowRot) {
    float result = 1.0;
    if (lights.shadowParams1.x >= 0.5) {
        float4 lightPos = lights.spotShadowViewProj * float4(worldPos, 1.0);
        if (lightPos.w > 0.0) {
            float visibility = SampleShadowTile(shadowAtlas, shadowSampler, lights.shadowParams0.z, 3, lightPos.xyz / lightPos.w, shadowRot, lights.shadowParams0.w);
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
#if NORMAL_OUTPUT
// A0.2: octahedral encoding of the unit world normal into [-1,1]^2, literally the same as OctEncodeNormal on DX / VK / WebGPU.
float2 OctEncodeNormal(float3 n) {
    n /= (fabs(n.x) + fabs(n.y) + fabs(n.z));
    float2 s = float2(n.x >= 0.0 ? 1.0 : -1.0, n.y >= 0.0 ? 1.0 : -1.0);
    return n.z >= 0.0 ? n.xy : (1.0 - fabs(n.yx)) * s;
}
#endif
struct FragmentOut {
    float4 color    [[color(0)]];
    float2 velocity [[color(1)]];
#if NORMAL_OUTPUT
    float4 normal   [[color(2)]];   // A0.2: octahedral world normal xy, roughness, P0 reflection weight
#endif
};
#define SEASON_FS_OUT FragmentOut
#if NORMAL_OUTPUT
#define SEASON_WRITE_NORMAL_ fsOut_.normal = normal_;
#else
#define SEASON_WRITE_NORMAL_
#endif
#define SEASON_RETURN_COLOR(col_) { FragmentOut fsOut_; fsOut_.color = ApplySceneFog(ApplySceneWeather((col_), in.vWorldPos, lights, fogParams), in.vWorldPos, sceneFog, fogParams); fsOut_.velocity = velocity_; SEASON_WRITE_NORMAL_ return fsOut_; }
#else
#define SEASON_FS_OUT float4
#define SEASON_RETURN_COLOR(col_) return ApplySceneFog(ApplySceneWeather((col_), in.vWorldPos, lights, fogParams), in.vWorldPos, sceneFog, fogParams);
#endif

// 2-7 P0: sample the Sky-View LUT for radiance along the reflection direction.
// The parameterization is byte-for-byte aligned with the renderMode == 3 sky sphere and with the inverse
// solve in the skyView compute kernel:
// the u seam is at +Z, and v folds with sqrt to concentrate resolution toward the horizon. The LUT has no mip, so
// only level 0 can be sampled - the implicit derivatives across the seam would select the wrong level.
inline float3 SampleSkyViewRadiance(texture2d<float> skyView, sampler s, float3 dir)
{
    // 1/(2*PI) is written as a literal to avoid a duplicate definition with the macro name in the shared constant block.
    float2 uv;
    uv.x = atan2(dir.x, -dir.z) * 0.15915494309 + 0.5;
    uv.y = 0.5 - 0.5 * sign(dir.y) * sqrt(abs(dir.y));
    return skyView.sample(s, uv, level(0.0)).rgb;
}

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
    // Work package D: the fog sampling trio for the transparent stage, one-to-one with VK's set 1 binding 0/1/2 and
    // DX's FogFrameRoot/FogIntegratedRoot/FogLocalRoot.
    // All three are always declared, not behind compile-time macro variants, so the number of PSO families does not change because of them;
    // what actually decides whether to sample is fogParams.x, pushed per command.
    const device float4* fogFrameData      [[buffer(4)]],
    constant float4& fogParams             [[buffer(5)]],
    // S1: per-view constants mirrored from VS buffer(11) onto FS buffer(11).
    // Only cameraPos.w (view slot index, 0 = primary) is read, and only inside the renderMode==0 branch.
    // BindView above binds both stages, so no other call-site change is needed here.
    constant ViewConstants& viewc          [[buffer(11)]],
    texture2d<float> albedoMap            [[texture(0)]],
    texture2d<float> normalMap            [[texture(1)]],
    texture2d<float> metallicRoughnessMap [[texture(2)]],
    texture2d<float> aoMap                [[texture(3)]],
    texture2d<float> emissiveMap          [[texture(4)]],
    // 2-6 clause 7: every material fetch in this function passes bias(TEXTURE_LOD_BIAS), which is the literal 0.0
    // outside the TAA tier, so the level selected there is the one an unbiased fetch would pick. Whether the zero is
    // folded away or kept as an operand is the Metal compiler's business; fxc keeps it, so do not assume the neutral
    // tier is free of a bias-capable fetch here either. The uniform call form is deliberate regardless - WGSL has no
    // preprocessor to switch forms with, so branching here would buy a saving on one backend at the price of four
    // structurally different shader sources.
    //
    // The bias is not applied to the MSDF text fetch or the
    // outline-mask fetch that share albedoMap: a distance field is not colour, and its coverage maths derives screen
    // pixel range from uv derivatives rather than from the level actually sampled, so a bias would desynchronize the
    // two. Both of those atlases carry TextureMipPolicy.None today, which makes the distinction free rather than a
    // cost - but it is written out so that giving one a chain later cannot silently change glyph coverage.
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
    // Work package D: two read-only volume textures immediately after the AP LUT at texture(10).
    // A texture3d in the fragment stage defaults to access::sample, so it can be handed straight to FogSampler's parameters.
    , texture3d<float> fogIntegratedVolume [[texture(11)]]
    , texture3d<float> fogLocalVolume      [[texture(12)]]
    // 2-7 P0: the procedural sky's Sky-View LUT (texture(13)), the ambient specular source for PBR.
    // It runs in parallel with the envCube at texture(6) rather than replacing it: the two are mutually exclusive, chosen
    // by skyParams0.w in the uniform branch, so both images must have a valid binding (Device.BeginPass binds 1x1 white before they are ready).
    // The white dummy will never be sampled: the precondition for writing skyParams0.w is precisely that FrameSchedule.SkyViewTexture is non-null.
    , texture2d<float> skyView             [[texture(13)]]
#if SHADOW_ENABLED
    , depth2d<float> shadowAtlas          [[texture(5)]]
    , sampler shadowSampler               [[sampler(1)]]
#endif
    )
{
    // Work package D: assemble read-only handles only, reading no data.
    // The gate lives inside ApplySceneFog; when it is off, fogFrameData and the two volume textures are never accessed even once.
    FogSampler sceneFog{fogFrameData, fogIntegratedVolume, fogLocalVolume};
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
#if NORMAL_OUTPUT
    // A0.2: zero for every non-PBR exit; the PBR path overwrites it once N and roughness are final.
    float4 normal_ = float4(0.0);
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
        WeatherCloudContext<constant float4*> weather{lights.weatherParams0,lights.weatherParams1,lights.weatherRegions,cloudNoise,lights.weatherFlash};
        skyRadiance=weather.WeatherCloudComposite(skyRadiance,lights.cameraPos.xyz,skyDir,lights.skyParams0.xyz);
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
        float4 sampled = albedoMap.sample(texSampler, in.vUV, bias(TEXTURE_LOD_BIAS));
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
        // S1: analytic in-scatter for unlit geometry in secondary views. viewc.cameraPos.w is the view slot
        // (0 = primary). The primary view still draws its brightness entirely from ApplySceneFog inside
        // SEASON_RETURN_COLOR, so this branch is pixel-identical to the pre-change behavior on the primary view.
        // On secondary views DrawTransparent(sampleFog=false) → sampling=false → fogParams=(0,0,0,0),
        // so scattering-fed geometry such as the beacon beam would
        // otherwise collapse to a bare 6% albedo film and decouple from the main render.
        // emissiveFactor is repurposed as the HDR gain on this path (.rgb = gain color, .w = gate);
        // the unlit path originally reads none of it, so only primitives that explicitly opt in (the beacon
        // beam in Tower.cs) light up; every other primitive keeps emissiveFactor at zero and stays unchanged.
        if (viewc.cameraPos.w > 0.5 && mat.emissiveFactor.w > 0.0) {
            albedo = albedo + mat.emissiveFactor.rgb * alpha;
        }
        // Sprite2D:
        // unlit path, direct linear output, then the FinalBlit exposure plus ACES plus gamma chain closes it at step B.
        SEASON_RETURN_COLOR(float4(albedo, alpha));
#else
        float3 c = pow(albedo, float3(1.0 / 2.2));
        SEASON_RETURN_COLOR(float4(c, alpha));
#endif
    }

    // glTF defines the effective values as factor * texture channel, so the scalar factors apply on both
    // paths. Sampling the map without the factors used to force metallicFactor 0 materials (grass, most
    // props) to whatever the packed ORM blue channel held, i.e. fully metallic.
    if (mat.useMetallicRoughnessMap != 0u) {
        float3 metallicRoughnessSample = metallicRoughnessMap.sample(texSampler, in.vUV, bias(TEXTURE_LOD_BIAS)).rgb;
        metallicRoughness.b = mat.metallicFactor * metallicRoughnessSample.b;
        metallicRoughness.g = mat.roughnessFactor * metallicRoughnessSample.g;
    } else {
        metallicRoughness.b = mat.metallicFactor;
        metallicRoughness.g = mat.roughnessFactor;
    }

    if (mat.useAoMap != 0u) ao = aoMap.sample(texSampler, in.vUV, bias(TEXTURE_LOD_BIAS)).r;

    if (mat.useEmissiveMap != 0u) {
        emissive = emissiveMap.sample(texSampler, in.vUV, bias(TEXTURE_LOD_BIAS)).rgb;
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

    // 1-5 clause 13: keep the geometric normal before the normal map overwrites N below. Shadow lookups are displaced along
    // this one, not the shading normal: the depth buffer holds the interpolated surface, so a perturbed normal would push
    // the sample off that surface and reintroduce acne exactly where the map is strongest.
    float3 Ngeo = N;

    if (mat.useNormalMap != 0u) {
        float4 nrmSample = normalMap.sample(texSampler, in.vUV, bias(TEXTURE_LOD_BIAS));
        float3 nrm = nrmSample.rgb * 2.0 - 1.0;
        // 2-6 clause 5: normalize the world-space result. Any filtered fetch of a normal map returns a vector
        // shorter than unit length, and with a mip chain the shortening grows with distance.
        N = normalize(TBN * nrm);

        // 2-6 clause 5, Toksvig: alpha encodes the mean resultant length of the normals this texel averaged, written
        // by MipChain - see MipChain.Renormalize for why it stores 1 - sqrt(1 - length) rather than the length. The
        // normalize above restores the direction but not the lobe width, so it is folded into roughness here. The
        // combination is in GGX alpha-squared space because slope variances are what add (a_new^2 = a_old^2 + 2/kappa,
        // with a = roughness*roughness as DistributionGGX above defines it); the D3D12 shader carries the full
        // derivation, including why 1 - sigma^2 is reconstructed from the stored complement instead of from sigma. A
        // no-op at 1 up to rounding, which is how RenderQuality.TextureNormalVariance switches this off from the CPU
        // with no branch here; the denominator is guarded so that 0 saturates roughness instead of dividing by zero.
        float t = 1.0 - nrmSample.a;
        float tSq = t * t;
        float oneMinusSigmaSq = tSq * (2.0 - tSq);
        float sigma = 1.0 - tSq;
        float twoOverKappa = 2.0 * oneMinusSigmaSq / max(sigma * (2.0 + oneMinusSigmaSq), 1e-4);
        float ggxAlpha = roughness * roughness;
        roughness = min(1.0, sqrt(sqrt(ggxAlpha * ggxAlpha + twoOverKappa)));
    }

#if NORMAL_OUTPUT
    // As of P0 the 4th channel is no longer metallic: it is the **reflection weight** the SSR difference needs, which
    // can only be written once both F0 and ao are computed,
    // so the write point has moved below the reflection term (see the `ambient += reflectionRadiance` line).
#endif

    float3 V = normalize(lights.cameraPos.xyz - in.vWorldPos);
    float3 F0 = float3(0.04);
    F0 = mix(F0, albedo, metallic);

    // Direct-light accumulation.
    // Under contract clause 2 of 1-2, directional, point, and spot lights all live in the same lights array
    // and one loop dispatches by dirType.w.
    float3 Lo = float3(0.0);

    int lightCount = min(int(lights.params0.x), 8);
    int dirShadowIdx = int(lights.params0.z);      // index of the directional light casting CSM (-1=none)
    int spotShadowIdx = int(lights.params0.w);     // index of the spot light casting a 2D shadowmap (-1=none)
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
                radiance *= ComputeSunShadow(lights, shadowAtlas, shadowSampler, in.vWorldPos, Ngeo, L, in.vViewDepth, ShadowKernelRotation(lights, in.position.xy));
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
                radiance *= ComputeSpotShadow(lights, shadowAtlas, shadowSampler, in.vWorldPos, ShadowKernelRotation(lights, in.position.xy));
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
    // when DDGI is ready and GiIntensity is greater than 0, probe irradiance is blended over the SH9-versus-constant result by that weight;
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
    // The two corrections of 2-8 both happen only inside the procedural sky branch; the else branch (envCube) keeps
    // the 1-7 formula unchanged,
    // so presets that enable only a texture cube are still zero-regression:
    //   A. When R.y < 0 the Sky-View LUT side is almost black (a downward ray only crosses those few hundred meters of
    //      air from the view point to the ground, and the radiance of the ground itself after being lit is not added in
    //      at all), so it switches to the ground radiance precomputed on the CPU,
    //      blended with a smoothstep across the horizon. The lower half of a mirror ball should reflect grass, not black.
    //   B. The rough end no longer crushes the specular to zero by stacking another (1-roughness)^2: a metal's diffuse
    //      term is multiplied by (1-metallic), so crushing to zero means a metal completely loses ambient energy once rough.
    //      Instead it interpolates by (1-roughness)^2 between the specular sample and
    //      giDiffuse (hemisphere average, already E/pi and including the DDGI ground bounce). When rough, R is already
    //      close to N, so using the irradiance on N to approximate the average incoming radiance within the reflection
    //      lobe avoids another probe fetch.
    float3 R = reflect(-V, N);
    float mirrorW = (1.0 - roughness) * (1.0 - roughness);
    float3 reflectionRadiance; float specGate;
    if (lights.skyParams0.w > 0.0)
    {
        float3 skyRadiance = SampleSkyViewRadiance(skyView, texSampler, R);
        // The view point must be above the ground for the ground term to be meaningful; otherwise (a camera at or under the ground) it always falls back to the sky.
        float aboveGround = step(lights.reflectionParams0.w, in.vWorldPos.y);
        float horizonW = max(smoothstep(-0.09, 0.09, R.y), 1.0 - aboveGround);
        skyRadiance = mix(lights.reflectionParams0.xyz, skyRadiance, horizonW);
        reflectionRadiance = mix(giDiffuse, skyRadiance * lights.envParams.x, mirrorW);
        specGate = 1.0;
    }
    else
    {
        // Original 1-7 formula: LOD0 specular x envParams.x x (1-r)^4.
        reflectionRadiance = envCube.sample(texSampler, R, level(0.0)).rgb * lights.envParams.x * mirrorW * mirrorW;
        specGate = step(0.5, lights.envParams.w);
    }
    ambient += reflectionRadiance * F0 * ao * specGate;

#if NORMAL_OUTPUT
    // The F0 channel for P0 SSR. What the compositing side must do is replace the sky term with the screen-space hit,
    // and the identity is delta = conf*(1-r)^2*(F0*ao)*(ssr - reflSky): giDiffuse cancels in the difference,
    // so all that is needed here is a scalar weight, squeezing F0 to one dimension with Rec.709 luminance.
    // What is lost is only the color cast (gold ball vs silver ball), because delta is a replacement amount, not the
    // color itself; in exchange there is no need to open another MRT target for SSR.
    // The envCube branch writes 0: that path's base term is the texture cube, which the SSR kernel cannot access, so replacing it would only compute wrong.
    normal_ = float4(OctEncodeNormal(N), roughness,
        lights.skyParams0.w > 0.0 ? dot(F0, float3(0.2126, 0.7152, 0.0722)) * ao : 0.0);
#endif

    // 2-7: lightning illumination. v1 folded it into the ambient term, so every orientation brightened uniformly - the
    // back of a ridge lit up too,
    // which does not match the lightning is over there. v2 gives it a direction, done byte-for-byte the same as DX12:
    //   the light source is taken at the ground projection of the strike point, with the height clamped to [ground, strike point]; attenuation is 1/(1+d^2/R^2), R=400 m;
    //   the Lambert term keeps a 25% floor, because the channel has multiple branches so some branch can always light the back face;
    //   the factor 1.6 makes it land exactly back at v1's 0.8 at 400 m, leaving distant brightness unchanged.
    if (lights.weatherFlash.w > 0.0)
    {
        float flashGround = min(lights.reflectionParams0.w, lights.weatherFlash.y);
        float3 flashTo = float3(lights.weatherFlash.x,
            clamp(in.vWorldPos.y, flashGround, lights.weatherFlash.y), lights.weatherFlash.z) - in.vWorldPos;
        float flashDist2 = dot(flashTo, flashTo);
        float3 flashL = flashTo * rsqrt(max(flashDist2, 1e-4));
        float flashFace = clamp(dot(N, flashL) * 0.75 + 0.25, 0.0, 1.0);
        float flashFall = 1.0 / (1.0 + flashDist2 * (1.0 / 160000.0));
        ambient += albedo * ao * (1.0 - metallic) * lights.weatherFlash.w * flashFace * flashFall
            * float3(0.55, 0.62, 0.85) * 1.6;
    }

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
