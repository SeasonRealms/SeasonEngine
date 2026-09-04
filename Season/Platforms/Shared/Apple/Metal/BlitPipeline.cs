// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Metal;

namespace Season.Platforms.Shared.Apple.Metal;

/// <summary>
/// Full-screen triangle pipeline for FinalBlit, aligned with VK BlitPipeline and WebGPU _blitPipeline in 1-1 step 2 and 3:
/// - Point variant, where source size equals the backbuffer:
///   the fragment stage uses <c>texture.read(uint2(pos.xy))</c> for exact identity mapping with zero sampling error.
///   Metal framebuffer and texture coordinates both use downward Y,
///   so no direction compensation is required. This is equivalent to VK texelFetch and WebGPU textureLoad.
/// - Linear variant for step 3, used when the source size differs from the backbuffer for fractional-resolution upsampling:
///   the vertex shader outputs additional uv coordinates.
///   NDC Y points upward while texture V points downward, so <c>pos.y = 1 - uv.y * 2</c> performs the flip,
///   using the same formula as WebGPU <c>vs_linear</c>.
///   The fragment shader uses a linear sampler, and Draw selects the variant automatically from source and destination size,
///   following the same contract on all four backends.
/// - The PSO is baked with BackBufferFormat color and Depth32Float depth.
///   The FinalBlit pass reuses the backbuffer depth-attachment shape,
///   which is structurally identical to the Scene pass attachment set,
///   so BeginPass does not need per-pass RPD attachment branching.
/// - Depth state disables writes and uses Always because blit does not participate in depth testing.
///   Initialization is lazy and compiles the PSO on first Draw.
/// - Render-quality 1-4 step B adds paired tonemap point and linear variants that finish the full exposure -> ACES by Narkowicz -> gamma chain.
///   Exposure is pushed every Draw through SetFragmentBytes at buffer 0 using Device.HdrExposure, per contract clause 5.
///   The variant is selected automatically from the source RT format, where Rgba16Float means the HDR path,
///   and the tonemap PSO is still baked against BackBufferFormat because FinalBlit always renders to the backbuffer.
/// - Step D of 2-1, aligned with DX step B and C:
///   tonemap+bloom variants bind bloom-chain output at <c>texture(1)</c> and always upsample it linearly before ACES-space addition.
///   Uber variants are used by the Post pass and output tonemap(+bloom) into LDR PostColor while packing Rec.601 luma into alpha.
///   FXAA variants are used by FinalBlit resolve, and their contract constants are ported literally from the DX reference implementation,
///   using sampler 0 for point neighborhood taps and sampler 1 for linear directional taps.
///   All of them share the 16-byte BlitParams block, containing exposure, bloomIntensity, and texelSize,
///   delivered through SetFragmentBytes at buffer 0.
/// - Step C of 2-2, aligned with DX 2-2 step B:
///   six AO variants exist, covering tonemap with or without bloom, point or linear, plus uber with or without bloom.
///   Before ACES, the linear scene is multiplied by AO and then bloom is added:
///   <c>scene * mix(1, ao, aoIntensity) + bloom * bloomIntensity</c>.
///   AO darkens only the scene and not the bloom contribution.
///   The AO texture is bound at <c>texture(2)</c> and always upsampled linearly from the half-resolution GTAO output r channel,
///   scaled by AoIntensity.
///   This adds the fifth component to BlitParams and expands the block to 20 bytes.
/// - Step D of 2-3, aligned with DX and VK contract clause 12:
///   when <c>sceneTex</c> is non-null and ready, it replaces <c>texture(0)</c> as the scene source.
///   That texture is the TAA resolve storage texture and matches SceneColor in size and rgba16float format,
///   so point and tonemap variants keep identical read and sample semantics.
///   No new PSO variant is needed, because variant selection is still driven by the source RT description and only the bound texture changes.
/// </summary>
internal static class BlitPipeline
{
    const string BlitShaderSource = """
#include <metal_stdlib>
using namespace metal;

vertex float4 blit_vs(uint vid [[vertex_id]])
{
    float2 uv = float2((vid << 1) & 2, vid & 2);
    return float4(uv * 2.0 - 1.0, 0.0, 1.0);
}

fragment float4 blit_fs(float4 pos [[position]],
                        texture2d<float> srcTex [[texture(0)]])
{
    return srcTex.read(uint2(pos.xy));
}

struct BlitVSOut
{
    float4 pos [[position]];
    float2 uv;
};

vertex BlitVSOut blit_vs_linear(uint vid [[vertex_id]])
{
    float2 uv = float2((vid << 1) & 2, vid & 2);
    BlitVSOut o;
    o.pos = float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.0, 1.0);
    o.uv = uv;
    return o;
}

fragment float4 blit_fs_linear(BlitVSOut vsOut [[stage_in]],
                               texture2d<float> srcTex [[texture(0)]],
                               sampler srcSampler [[sampler(0)]])
{
    return srcTex.sample(srcSampler, vsOut.uv);
}

// Tonemap variants for render-quality 1-4 step B:
// HDR linear source in RGBA16Float goes through exposure, ACES, and gamma encoding before presentation.
// ACES uses the Narkowicz 2015 fitted curve, an RRT plus ODT approximation.
// The same constants are used on all four backends so cross-platform appearance stays consistent.
static float3 AcesFilm(float3 x)
{
    const float a = 2.51;
    const float b = 0.03;
    const float c = 2.43;
    const float d = 0.59;
    const float e = 0.14;
    return saturate((x * (a * x + b)) / (x * (c * x + d) + e));
}

static float3 Tonemap(float3 hdr, float exposure)
{
    float3 mapped = AcesFilm(max(hdr, float3(0.0)) * exposure);
    return pow(mapped, float3(1.0 / 2.2));
}

fragment float4 blit_fs_tonemap(float4 pos [[position]],
                                constant float& exposure [[buffer(0)]],
                                texture2d<float> srcTex [[texture(0)]])
{
    float4 c = srcTex.read(uint2(pos.xy));
    return float4(Tonemap(c.rgb, exposure), c.a);
}

fragment float4 blit_fs_linear_tonemap(BlitVSOut vsOut [[stage_in]],
                                       constant float& exposure [[buffer(0)]],
                                       texture2d<float> srcTex [[texture(0)]],
                                       sampler srcSampler [[sampler(0)]])
{
    float4 c = srcTex.sample(srcSampler, vsOut.uv);
    return float4(Tonemap(c.rgb, exposure), c.a);
}

// ---- Step D of 2-1: bloom, uber, and FXAA variants, aligned with DX step B and C ----
// The parameter block is 16 bytes and matches the layout of VK push constants and DX root constants.
// It is delivered through SetFragmentBytes at buffer 0.
// Step C of 2-2 expands it to 20 bytes by appending aoIntensity, without affecting float2 alignment.
struct BlitParams
{
    float exposure;       // Linear exposure multiplier from Device.HdrExposure.
    float bloomIntensity; // Bloom composition coefficient from RenderQuality.BloomIntensity, used only by bloom and uber variants.
    float2 texelSize;     // Source-texture texel size for FXAA, used only by FXAA variants.
    float aoIntensity;    // Step C of 2-2: AO occlusion intensity from RenderQuality.AoIntensity, used only by AO variants.
    float outlineWidth;   // Phase 4: outline width in screen pixels for Outline2D composition, used only by outline_composite.
};

// Tonemap plus bloom:
// bloom is added in linear space before ACES, following the RenderQuality 2-1 contract.
// Bloom comes from the half-resolution chain and is always upsampled linearly through texture(1) and sampler(0).
fragment float4 blit_fs_tonemap_bloom(BlitVSOut vsOut [[stage_in]],
                                      constant BlitParams& params [[buffer(0)]],
                                      texture2d<float> srcTex [[texture(0)]],
                                      texture2d<float> bloomTex [[texture(1)]],
                                      sampler linearSampler [[sampler(0)]])
{
    float4 c = srcTex.read(uint2(vsOut.pos.xy));
    c.rgb += bloomTex.sample(linearSampler, vsOut.uv).rgb * params.bloomIntensity;
    return float4(Tonemap(c.rgb, params.exposure), c.a);
}

fragment float4 blit_fs_linear_tonemap_bloom(BlitVSOut vsOut [[stage_in]],
                                             constant BlitParams& params [[buffer(0)]],
                                             texture2d<float> srcTex [[texture(0)]],
                                             texture2d<float> bloomTex [[texture(1)]],
                                             sampler linearSampler [[sampler(0)]])
{
    float4 c = srcTex.sample(linearSampler, vsOut.uv);
    c.rgb += bloomTex.sample(linearSampler, vsOut.uv).rgb * params.bloomIntensity;
    return float4(Tonemap(c.rgb, params.exposure), c.a);
}

// Uber variant used by the Post pass:
// tonemap with optional bloom is composed into LDR PostColor,
// and luma is baked into alpha using Rec.601 weights in gamma-space with the shared cross-backend constants,
// so FXAA can reuse it without recalculation.
// Source and destination always match in size because PostColor uses MatchBackbufferSize,
// so point reads remain an identity mapping.
static float Luma(float3 ldr)
{
    return dot(ldr, float3(0.299, 0.587, 0.114));
}

fragment float4 blit_fs_uber(float4 pos [[position]],
                             constant BlitParams& params [[buffer(0)]],
                             texture2d<float> srcTex [[texture(0)]])
{
    float4 c = srcTex.read(uint2(pos.xy));
    float3 ldr = Tonemap(c.rgb, params.exposure);
    return float4(ldr, Luma(ldr));
}

fragment float4 blit_fs_uber_bloom(BlitVSOut vsOut [[stage_in]],
                                   constant BlitParams& params [[buffer(0)]],
                                   texture2d<float> srcTex [[texture(0)]],
                                   texture2d<float> bloomTex [[texture(1)]],
                                   sampler linearSampler [[sampler(0)]])
{
    float4 c = srcTex.read(uint2(vsOut.pos.xy));
    c.rgb += bloomTex.sample(linearSampler, vsOut.uv).rgb * params.bloomIntensity;
    float3 ldr = Tonemap(c.rgb, params.exposure);
    return float4(ldr, Luma(ldr));
}

// ---- Step C of 2-2: six AO variants, aligned with DX 2-2 step B ----
// Before ACES, the linear scene is first multiplied by AO and then bloom is added.
// AO darkens only the scene and never darkens bloom.
// AO is the r channel of the half-resolution GTAO output and is always upsampled linearly from texture(2) with sampler(0).
static float3 ApplyAo(float3 scene, float2 uv, texture2d<float> aoTex, sampler linearSampler, float aoIntensity)
{
    float ao = aoTex.sample(linearSampler, uv).r;
    return scene * mix(1.0, ao, aoIntensity);
}

fragment float4 blit_fs_tonemap_ao(BlitVSOut vsOut [[stage_in]],
                                   constant BlitParams& params [[buffer(0)]],
                                   texture2d<float> srcTex [[texture(0)]],
                                   texture2d<float> aoTex [[texture(2)]],
                                   sampler linearSampler [[sampler(0)]])
{
    float4 c = srcTex.read(uint2(vsOut.pos.xy));
    c.rgb = ApplyAo(c.rgb, vsOut.uv, aoTex, linearSampler, params.aoIntensity);
    return float4(Tonemap(c.rgb, params.exposure), c.a);
}

fragment float4 blit_fs_linear_tonemap_ao(BlitVSOut vsOut [[stage_in]],
                                          constant BlitParams& params [[buffer(0)]],
                                          texture2d<float> srcTex [[texture(0)]],
                                          texture2d<float> aoTex [[texture(2)]],
                                          sampler linearSampler [[sampler(0)]])
{
    float4 c = srcTex.sample(linearSampler, vsOut.uv);
    c.rgb = ApplyAo(c.rgb, vsOut.uv, aoTex, linearSampler, params.aoIntensity);
    return float4(Tonemap(c.rgb, params.exposure), c.a);
}

fragment float4 blit_fs_tonemap_bloom_ao(BlitVSOut vsOut [[stage_in]],
                                         constant BlitParams& params [[buffer(0)]],
                                         texture2d<float> srcTex [[texture(0)]],
                                         texture2d<float> bloomTex [[texture(1)]],
                                         texture2d<float> aoTex [[texture(2)]],
                                         sampler linearSampler [[sampler(0)]])
{
    float4 c = srcTex.read(uint2(vsOut.pos.xy));
    c.rgb = ApplyAo(c.rgb, vsOut.uv, aoTex, linearSampler, params.aoIntensity);
    c.rgb += bloomTex.sample(linearSampler, vsOut.uv).rgb * params.bloomIntensity;
    return float4(Tonemap(c.rgb, params.exposure), c.a);
}

fragment float4 blit_fs_linear_tonemap_bloom_ao(BlitVSOut vsOut [[stage_in]],
                                                constant BlitParams& params [[buffer(0)]],
                                                texture2d<float> srcTex [[texture(0)]],
                                                texture2d<float> bloomTex [[texture(1)]],
                                                texture2d<float> aoTex [[texture(2)]],
                                                sampler linearSampler [[sampler(0)]])
{
    float4 c = srcTex.sample(linearSampler, vsOut.uv);
    c.rgb = ApplyAo(c.rgb, vsOut.uv, aoTex, linearSampler, params.aoIntensity);
    c.rgb += bloomTex.sample(linearSampler, vsOut.uv).rgb * params.bloomIntensity;
    return float4(Tonemap(c.rgb, params.exposure), c.a);
}

fragment float4 blit_fs_uber_ao(BlitVSOut vsOut [[stage_in]],
                                constant BlitParams& params [[buffer(0)]],
                                texture2d<float> srcTex [[texture(0)]],
                                texture2d<float> aoTex [[texture(2)]],
                                sampler linearSampler [[sampler(0)]])
{
    float4 c = srcTex.read(uint2(vsOut.pos.xy));
    c.rgb = ApplyAo(c.rgb, vsOut.uv, aoTex, linearSampler, params.aoIntensity);
    float3 ldr = Tonemap(c.rgb, params.exposure);
    return float4(ldr, Luma(ldr));
}

fragment float4 blit_fs_uber_bloom_ao(BlitVSOut vsOut [[stage_in]],
                                      constant BlitParams& params [[buffer(0)]],
                                      texture2d<float> srcTex [[texture(0)]],
                                      texture2d<float> bloomTex [[texture(1)]],
                                      texture2d<float> aoTex [[texture(2)]],
                                      sampler linearSampler [[sampler(0)]])
{
    float4 c = srcTex.read(uint2(vsOut.pos.xy));
    c.rgb = ApplyAo(c.rgb, vsOut.uv, aoTex, linearSampler, params.aoIntensity);
    c.rgb += bloomTex.sample(linearSampler, vsOut.uv).rgb * params.bloomIntensity;
    float3 ldr = Tonemap(c.rgb, params.exposure);
    return float4(ldr, Luma(ldr));
}

// FXAA used by FinalBlit:
// this is the reduced-quality FXAA 3.11 path with five taps for direction estimation
// and four taps for directional sampling.
// Luma is read from source alpha, which the uber pass has already baked in.
// REDUCE_MIN, REDUCE_MUL, SPAN_MAX, and contrast thresholds are shared cross-backend contract constants
// ported literally from the DX reference implementation.
// Sampler 0 is used for point neighborhood taps and sampler 1 for linear directional taps.
fragment float4 blit_fs_fxaa(BlitVSOut vsOut [[stage_in]],
                             constant BlitParams& params [[buffer(0)]],
                             texture2d<float> srcTex [[texture(0)]],
                             sampler pointSampler [[sampler(0)]],
                             sampler linearSampler [[sampler(1)]])
{
    const float FXAA_REDUCE_MIN = 1.0 / 128.0;
    const float FXAA_REDUCE_MUL = 1.0 / 8.0;
    const float FXAA_SPAN_MAX = 8.0;
    const float FXAA_EDGE_THRESHOLD = 1.0 / 8.0;
    const float FXAA_EDGE_THRESHOLD_MIN = 1.0 / 24.0;

    float2 rcpFrame = params.texelSize;
    float2 uv = vsOut.uv;

    float4 colorM = srcTex.sample(pointSampler, uv);
    float lumaM  = colorM.a;
    float lumaNW = srcTex.sample(pointSampler, uv + float2(-1.0, -1.0) * rcpFrame).a;
    float lumaNE = srcTex.sample(pointSampler, uv + float2( 1.0, -1.0) * rcpFrame).a;
    float lumaSW = srcTex.sample(pointSampler, uv + float2(-1.0,  1.0) * rcpFrame).a;
    float lumaSE = srcTex.sample(pointSampler, uv + float2( 1.0,  1.0) * rcpFrame).a;

    float lumaMin = min(lumaM, min(min(lumaNW, lumaNE), min(lumaSW, lumaSE)));
    float lumaMax = max(lumaM, max(max(lumaNW, lumaNE), max(lumaSW, lumaSE)));

    float4 result = colorM;

    // Early out on low contrast so non-edge pixels pass through
    // without paying the bandwidth cost of directional sampling.
    if (lumaMax - lumaMin >= max(FXAA_EDGE_THRESHOLD_MIN, lumaMax * FXAA_EDGE_THRESHOLD))
    {
        // Edge tangent direction, which is orthogonal to the luma gradient.
        // Normalize it against local brightness and clamp the maximum span.
        float2 dir = float2(
            -((lumaNW + lumaNE) - (lumaSW + lumaSE)),
             ((lumaNW + lumaSW) - (lumaNE + lumaSE)));

        float dirReduce = max((lumaNW + lumaNE + lumaSW + lumaSE) * 0.25 * FXAA_REDUCE_MUL, FXAA_REDUCE_MIN);
        float rcpDirMin = 1.0 / (min(abs(dir.x), abs(dir.y)) + dirReduce);
        dir = clamp(dir * rcpDirMin, float2(-FXAA_SPAN_MAX), float2(FXAA_SPAN_MAX)) * rcpFrame;

        // Four taps along the tangent direction:
        // the inner pair at plus or minus one-sixth span is always trusted,
        // while the outer pair at plus or minus one-half span falls back to the inner pair when it goes out of range.
        float4 rgbA = 0.5 * (
            srcTex.sample(linearSampler, uv + dir * (1.0 / 3.0 - 0.5)) +
            srcTex.sample(linearSampler, uv + dir * (2.0 / 3.0 - 0.5)));
        float4 rgbB = rgbA * 0.5 + 0.25 * (
            srcTex.sample(linearSampler, uv + dir * -0.5) +
            srcTex.sample(linearSampler, uv + dir * 0.5));

        result = (rgbB.a < lumaMin || rgbB.a > lumaMax) ? rgbA : rgbB;
    }

    return float4(result.rgb, 1.0);
}

// ---- Phase 4: Outline2D mask composition variant, mirroring DX PSMainOutlineComposite ----
// The mask in texture(0), sampled with point filtering, stores outline pixels as masked = (outlineColor.rgb, 1)
// and cleared pixels as (0, 0, 0, 0).
// It steps by outlineWidth across the eight neighbors, selects the color of the neighbor with the largest alpha,
// and computes edge = saturate(neighborAlpha - centerAlpha).
// The PSO uses alpha blending with SrcAlpha and InvSrcAlpha,
// so edge becomes the outline opacity, while masked interior pixels keep edge = 0 and therefore draw no color.
fragment float4 blit_fs_outline_composite(BlitVSOut vsOut [[stage_in]],
                                          constant BlitParams& params [[buffer(0)]],
                                          texture2d<float> maskTex [[texture(0)]],
                                          sampler pointSampler [[sampler(0)]])
{
    float2 stepUv = params.texelSize * max(params.outlineWidth, 1.0);
    float2 uv = vsOut.uv;
    float center = maskTex.sample(pointSampler, uv).a;

    float bestAlpha = 0.0;
    float3 bestColor = float3(0.0);

    float4 n = maskTex.sample(pointSampler, uv + float2( 1.0,  0.0) * stepUv);
    if (n.a > bestAlpha) { bestAlpha = n.a; bestColor = n.rgb; }
    n = maskTex.sample(pointSampler, uv + float2(-1.0,  0.0) * stepUv);
    if (n.a > bestAlpha) { bestAlpha = n.a; bestColor = n.rgb; }
    n = maskTex.sample(pointSampler, uv + float2( 0.0,  1.0) * stepUv);
    if (n.a > bestAlpha) { bestAlpha = n.a; bestColor = n.rgb; }
    n = maskTex.sample(pointSampler, uv + float2( 0.0, -1.0) * stepUv);
    if (n.a > bestAlpha) { bestAlpha = n.a; bestColor = n.rgb; }
    n = maskTex.sample(pointSampler, uv + float2( 1.0,  1.0) * stepUv);
    if (n.a > bestAlpha) { bestAlpha = n.a; bestColor = n.rgb; }
    n = maskTex.sample(pointSampler, uv + float2(-1.0,  1.0) * stepUv);
    if (n.a > bestAlpha) { bestAlpha = n.a; bestColor = n.rgb; }
    n = maskTex.sample(pointSampler, uv + float2( 1.0, -1.0) * stepUv);
    if (n.a > bestAlpha) { bestAlpha = n.a; bestColor = n.rgb; }
    n = maskTex.sample(pointSampler, uv + float2(-1.0, -1.0) * stepUv);
    if (n.a > bestAlpha) { bestAlpha = n.a; bestColor = n.rgb; }

    float edge = saturate(bestAlpha - center);
    return float4(bestColor, edge);
}
""";

    static IMTLRenderPipelineState? _psoPoint;
    static IMTLRenderPipelineState? _psoLinear;
    static IMTLRenderPipelineState? _psoTonemap;
    static IMTLRenderPipelineState? _psoTonemapLinear;
    static IMTLRenderPipelineState? _psoTonemapBloom;
    static IMTLRenderPipelineState? _psoTonemapBloomLinear;
    static IMTLRenderPipelineState? _psoUber;
    static IMTLRenderPipelineState? _psoUberBloom;
    static IMTLRenderPipelineState? _psoFxaa;
    static IMTLRenderPipelineState? _psoTonemapAo;
    static IMTLRenderPipelineState? _psoTonemapAoLinear;
    static IMTLRenderPipelineState? _psoTonemapBloomAo;
    static IMTLRenderPipelineState? _psoTonemapBloomAoLinear;
    static IMTLRenderPipelineState? _psoUberAo;
    static IMTLRenderPipelineState? _psoUberBloomAo;
    static IMTLRenderPipelineState? _psoOutlineComposite;
    static IMTLDepthStencilState? _depthState;
    static IMTLSamplerState? _linearSampler;
    static IMTLSamplerState? _pointSampler;

    static IMTLRenderPipelineState CreatePso(IMTLLibrary library, string vsName, string fsName, string label, bool alphaBlend = false)
    {
        var vs = library.CreateFunction(vsName) ?? throw new Exception($"MSL function '{vsName}' not found");
        var fs = library.CreateFunction(fsName) ?? throw new Exception($"MSL function '{fsName}' not found");

        var psd = new MTLRenderPipelineDescriptor
        {
            Label = label,
            VertexFunction = vs,
            FragmentFunction = fs,
            DepthAttachmentPixelFormat = Device.DepthBufferFormat,
        };
        psd.ColorAttachments[0].PixelFormat = Device.BackBufferFormat;

        // Phase 4: alpha blending for Outline2D composition.
        // This uses SrcAlpha, InvSrcAlpha, and Add, with alpha-channel One, Zero, and Add,
        // matching the DX and VK outline-composite PSO one to one.
        if (alphaBlend)
        {
            var att = psd.ColorAttachments[0];
            att.BlendingEnabled = true;
            att.RgbBlendOperation = MTLBlendOperation.Add;
            att.AlphaBlendOperation = MTLBlendOperation.Add;
            att.SourceRgbBlendFactor = MTLBlendFactor.SourceAlpha;
            att.DestinationRgbBlendFactor = MTLBlendFactor.OneMinusSourceAlpha;
            att.SourceAlphaBlendFactor = MTLBlendFactor.One;
            att.DestinationAlphaBlendFactor = MTLBlendFactor.Zero;
        }

        return Device.MtlDevice.CreateRenderPipelineState(psd, out Foundation.NSError? err)
            ?? throw new Exception($"CreateRenderPipelineState [{label}] failed: {err?.LocalizedDescription ?? "(no NSError)"}");
    }

    static void EnsureInitialized()
    {
        if (_psoPoint != null) return;

        var library = MTLShaderCompiler.Compile(Device.MtlDevice, BlitShaderSource);
        _psoPoint = CreatePso(library, "blit_vs", "blit_fs", "Season-FinalBlit-Point");
        _psoLinear = CreatePso(library, "blit_vs_linear", "blit_fs_linear", "Season-FinalBlit-Linear");
        _psoTonemap = CreatePso(library, "blit_vs", "blit_fs_tonemap", "Season-FinalBlit-Tonemap");
        _psoTonemapLinear = CreatePso(library, "blit_vs_linear", "blit_fs_linear_tonemap", "Season-FinalBlit-TonemapLinear");
        // Step D of 2-1:
        // bloom, uber, and FXAA variants all use the linear vertex shader where bloom upsampling and FXAA require uv.
        // The point path still performs an identity read through vsOut.pos,
        // so it stays equivalent to the no-uv variant.
        // The uber variant without bloom does not need uv and therefore still uses the point vertex shader.
        _psoTonemapBloom = CreatePso(library, "blit_vs_linear", "blit_fs_tonemap_bloom", "Season-FinalBlit-TonemapBloom");
        _psoTonemapBloomLinear = CreatePso(library, "blit_vs_linear", "blit_fs_linear_tonemap_bloom", "Season-FinalBlit-TonemapBloomLinear");
        _psoUber = CreatePso(library, "blit_vs", "blit_fs_uber", "Season-Post-Uber");
        _psoUberBloom = CreatePso(library, "blit_vs_linear", "blit_fs_uber_bloom", "Season-Post-UberBloom");
        _psoFxaa = CreatePso(library, "blit_vs_linear", "blit_fs_fxaa", "Season-FinalBlit-Fxaa");
        // Step C of 2-2:
        // all AO variants use the linear vertex shader because AO upsampling requires uv.
        // The point path still performs an identity read through vsOut.pos with no behavioral difference.
        _psoTonemapAo = CreatePso(library, "blit_vs_linear", "blit_fs_tonemap_ao", "Season-FinalBlit-TonemapAo");
        _psoTonemapAoLinear = CreatePso(library, "blit_vs_linear", "blit_fs_linear_tonemap_ao", "Season-FinalBlit-TonemapAoLinear");
        _psoTonemapBloomAo = CreatePso(library, "blit_vs_linear", "blit_fs_tonemap_bloom_ao", "Season-FinalBlit-TonemapBloomAo");
        _psoTonemapBloomAoLinear = CreatePso(library, "blit_vs_linear", "blit_fs_linear_tonemap_bloom_ao", "Season-FinalBlit-TonemapBloomAoLinear");
        _psoUberAo = CreatePso(library, "blit_vs_linear", "blit_fs_uber_ao", "Season-Post-UberAo");
        _psoUberBloomAo = CreatePso(library, "blit_vs_linear", "blit_fs_uber_bloom_ao", "Season-Post-UberBloomAo");
        // Phase 4: Outline2D composition variant.
        // It uses alpha blending, binds the mask at texture(0),
        // and samples it with point filtering. See DrawOutlineComposite.
        _psoOutlineComposite = CreatePso(library, "blit_vs_linear", "blit_fs_outline_composite", "Season-FinalBlit-OutlineComposite", alphaBlend: true);

        var dsd = new MTLDepthStencilDescriptor
        {
            DepthCompareFunction = MTLCompareFunction.Always,
            DepthWriteEnabled = false,
        };
        _depthState = Device.MtlDevice.CreateDepthStencilState(dsd)
            ?? throw new Exception("CreateDepthStencilState [FinalBlit] failed");

        var smp = new MTLSamplerDescriptor
        {
            MinFilter = MTLSamplerMinMagFilter.Linear,
            MagFilter = MTLSamplerMinMagFilter.Linear,
            SAddressMode = MTLSamplerAddressMode.ClampToEdge,
            TAddressMode = MTLSamplerAddressMode.ClampToEdge,
        };
        _linearSampler = Device.MtlDevice.CreateSamplerState(smp)
            ?? throw new Exception("CreateSamplerState [FinalBlit linear] failed");

        // Step D of 2-1:
        // FXAA neighborhood taps use point-clamp sampling,
        // matching VK binding 0, which uses an immutable point sampler.
        var smpPoint = new MTLSamplerDescriptor
        {
            MinFilter = MTLSamplerMinMagFilter.Nearest,
            MagFilter = MTLSamplerMinMagFilter.Nearest,
            SAddressMode = MTLSamplerAddressMode.ClampToEdge,
            TAddressMode = MTLSamplerAddressMode.ClampToEdge,
        };
        _pointSampler = Device.MtlDevice.CreateSamplerState(smpPoint)
            ?? throw new Exception("CreateSamplerState [FinalBlit point] failed");
    }

    /// <summary>Pushes the 24-byte BlitParams block through buffer 0 with the same layout as VK push constants and DX root constants.
    /// Sending it every Draw guarantees that runtime parameter changes take effect immediately, per contract clause 5.
    /// Phase 4 appends outlineWidth at the end.</summary>
    static unsafe void SetParams(IMTLRenderCommandEncoder enc, float exposure, float bloomIntensity, float texelSizeX, float texelSizeY, float aoIntensity = 0f, float outlineWidth = 0f)
    {
        float* p = stackalloc float[6] { exposure, bloomIntensity, texelSizeX, texelSizeY, aoIntensity, outlineWidth };
        enc.SetFragmentBytes((IntPtr)p, 6 * sizeof(float), 0);
    }

    /// <summary>Draws the full-screen triangle on the current pass encoder and presents the source color.
    /// It automatically selects point or linear from source and destination size,
    /// and automatically selects tonemap variants from source format, where Rgba16Float means the HDR path.
    /// Tonemap variants push the current-frame exposure through SetFragmentBytes, matching render-quality 1-4 step B.
    /// Step D of 2-1 switches to tonemap-plus-bloom variants when bloomTex is ready, with texture(1) always linearly upsampled.
    /// Step C of 2-2 switches to AO variants when aoTex is ready, using texture(2) and multiplying AO before ACES and before bloom is added.
    /// Contract clause 12 of 2-3 replaces the scene source with sceneTex when it is ready,
    /// while variant selection still follows the source RT description.</summary>
    public static unsafe void Draw(IMTLRenderCommandEncoder enc, MTLRenderTarget src, Texture? bloomTex = null, Texture? aoTex = null,
        Texture? sceneTex = null)
    {
        if (src.ColorTexture == null) return;
        EnsureInitialized();

        bool linear = src.Width != Device.Display.Width || src.Height != Device.Display.Height;
        bool tonemap = src.Desc.ColorFormat == Season.Rendering.RtFormat.Rgba16Float;
        bool bloom = tonemap && bloomTex != null && System.Threading.Volatile.Read(ref bloomTex.Ready);
        bool ao = tonemap && aoTex != null && System.Threading.Volatile.Read(ref aoTex.Ready);
        var pso = (ao, bloom, tonemap, linear) switch
        {
            (true, true, _, true) => _psoTonemapBloomAoLinear!,
            (true, true, _, false) => _psoTonemapBloomAo!,
            (true, false, _, true) => _psoTonemapAoLinear!,
            (true, false, _, false) => _psoTonemapAo!,
            (false, true, _, true) => _psoTonemapBloomLinear!,
            (false, true, _, false) => _psoTonemapBloom!,
            (false, false, true, true) => _psoTonemapLinear!,
            (false, false, true, false) => _psoTonemap!,
            (false, false, false, true) => _psoLinear!,
            (false, false, false, false) => _psoPoint!,
        };
        enc.SetRenderPipelineState(pso);
        enc.SetDepthStencilState(_depthState!);
        enc.SetCullMode(MTLCullMode.None);
        enc.SetFragmentTexture(ResolveSceneTexture(src, sceneTex), 0);
        if (ao)
            enc.SetFragmentTexture(aoTex!.Image, 2);
        if (bloom)
            enc.SetFragmentTexture(bloomTex!.Image, 1);
        if (bloom || ao)
        {
            enc.SetFragmentSamplerState(_linearSampler!, 0);
            SetParams(enc, Device.HdrExposure, RenderQuality.Current.BloomIntensity, 0f, 0f,
                RenderQuality.Current.AoIntensity);
        }
        else
        {
            if (linear)
                enc.SetFragmentSamplerState(_linearSampler!, 0);
            if (tonemap)
            {
                // Push exposure on every Draw, per contract clause 5.
                // Because it is far below 4 KB, SetFragmentBytes avoids creating a buffer
                // and lets runtime exposure changes take effect immediately.
                float exposure = Device.HdrExposure;
                enc.SetFragmentBytes((IntPtr)(&exposure), sizeof(float), 0);
            }
        }
        enc.DrawPrimitives(MTLPrimitiveType.Triangle, 0, 3);
    }

    /// <summary>Contract clause 12 of 2-3 resolves the scene source.
    /// When sceneTex, the TAA resolve storage texture, is non-null and ready, it replaces the source RT color texture.
    /// Otherwise the original RT color texture is used, keeping behavior identical to the pre-2-3 path when there is no override.
    /// That texture matches SceneColor in both size and rgba16float format.
    /// When sizes mismatch, TaaEffect already bypasses itself and does not publish it, per clause 15.
    /// Because of that, the point variant still keeps exact identity mapping through <c>read(uint2(pos.xy))</c>.
    /// Dispatch to sampling requires no extra barrier under Metal rule 2.</summary>
    static IMTLTexture? ResolveSceneTexture(MTLRenderTarget src, Texture? sceneTex)
        => sceneTex != null && System.Threading.Volatile.Read(ref sceneTex.Ready)
            ? sceneTex.Image
            : src.ColorTexture;

    /// <summary>Step D of 2-1 draws the Post-pass uber composition, mapping tonemap with optional bloom into LDR PostColor and baking luma into alpha.
    /// Source and destination sizes always match because both use MatchBackbufferSize, so point reads stay valid.
    /// Step C of 2-2 switches to the uber AO variant when aoTex is ready.
    /// Contract clause 12 of 2-3 applies the same sceneTex override to the scene source.
    /// Taa and Fxaa are currently mutually exclusive in practice, so they do not coexist,
    /// but this path is kept to mirror DX and VK RenderPostUber one to one.</summary>
    public static void DrawUber(IMTLRenderCommandEncoder enc, MTLRenderTarget src, Texture? bloomTex = null, Texture? aoTex = null,
        Texture? sceneTex = null)
    {
        if (src.ColorTexture == null) return;
        EnsureInitialized();

        bool bloom = bloomTex != null && System.Threading.Volatile.Read(ref bloomTex.Ready);
        bool ao = aoTex != null && System.Threading.Volatile.Read(ref aoTex.Ready);
        var pso = (ao, bloom) switch
        {
            (true, true) => _psoUberBloomAo!,
            (true, false) => _psoUberAo!,
            (false, true) => _psoUberBloom!,
            (false, false) => _psoUber!,
        };
        enc.SetRenderPipelineState(pso);
        enc.SetDepthStencilState(_depthState!);
        enc.SetCullMode(MTLCullMode.None);
        enc.SetFragmentTexture(ResolveSceneTexture(src, sceneTex), 0);
        if (ao)
            enc.SetFragmentTexture(aoTex!.Image, 2);
        if (bloom)
            enc.SetFragmentTexture(bloomTex!.Image, 1);
        if (bloom || ao)
            enc.SetFragmentSamplerState(_linearSampler!, 0);
        SetParams(enc, Device.HdrExposure, RenderQuality.Current.BloomIntensity, 0f, 0f,
            RenderQuality.Current.AoIntensity);
        enc.DrawPrimitives(MTLPrimitiveType.Triangle, 0, 3);
    }

    /// <summary>Step D of 2-1 performs FinalBlit FXAA resolve, using PostColor as the source with luma already stored in alpha.
    /// texelSize is the inverse source RT size, and the shader uses sampler 0 as point and sampler 1 as linear.</summary>
    public static void DrawFxaa(IMTLRenderCommandEncoder enc, MTLRenderTarget src)
    {
        if (src.ColorTexture == null) return;
        EnsureInitialized();

        enc.SetRenderPipelineState(_psoFxaa!);
        enc.SetDepthStencilState(_depthState!);
        enc.SetCullMode(MTLCullMode.None);
        enc.SetFragmentTexture(src.ColorTexture, 0);
        enc.SetFragmentSamplerState(_pointSampler!, 0);
        enc.SetFragmentSamplerState(_linearSampler!, 1);
        SetParams(enc, 0f, 0f, 1f / src.Width, 1f / src.Height);
        enc.DrawPrimitives(MTLPrimitiveType.Triangle, 0, 3);
    }

    /// <summary>Phase 4 composes the Outline2D mask inside the FinalBlit pass immediately after scene blit or FXAA.
    /// The mask is bound at texture(0) and sampled with point filtering.
    /// texelSize is the inverse mask size, and widthPixels is the outline width.
    /// The alpha-blend PSO overlays the outline on top of the scene using edge as opacity,
    /// mirroring DX and VK DrawOutlineComposite.</summary>
    public static void DrawOutlineComposite(IMTLRenderCommandEncoder enc, MTLRenderTarget mask, float widthPixels)
    {
        if (mask.ColorTexture == null) return;
        EnsureInitialized();

        enc.SetRenderPipelineState(_psoOutlineComposite!);
        enc.SetDepthStencilState(_depthState!);
        enc.SetCullMode(MTLCullMode.None);
        enc.SetFragmentTexture(mask.ColorTexture, 0);
        enc.SetFragmentSamplerState(_pointSampler!, 0);
        SetParams(enc, 0f, 0f, 1f / mask.Width, 1f / mask.Height, 0f, widthPixels);
        enc.DrawPrimitives(MTLPrimitiveType.Triangle, 0, 3);
    }
}
