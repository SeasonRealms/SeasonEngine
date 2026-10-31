// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Season.Rendering;
using Season.Rendering.Effects;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace Season.Platforms.Windows.DirectX;

/// <summary>
/// Dedicated pipeline for FinalBlit (Step 2): draws an offscreen color RT to the
/// screen with a fullscreen triangle.
/// It uses draw instead of CopyTextureRegion so the 1-4 tone-mapping / post chain
/// can reuse the same path later (only the PS / PSO variants need to change while
/// the pass structure stays the same).
/// Uses an independent RootSignature (t0 table + b0 root-constant exposure +
/// s0 point sampling + s1 linear sampling), decoupled from the main pipeline's
/// 12-parameter signature.
/// Step 3 adds a linear-sampling PSO variant for upsampling non-full-size sources
/// (fractional-resolution Post output).
/// 1-4 Step A adds dual tonemap variants (point/linear) for the HDR chain
/// (source RT is Rgba16Float), selected automatically by BlitToBackbuffer
/// from the source format.
/// 1-4 Step B upgrades tonemap variants to exposure (Device.HdrExposure) ->
/// ACES (Narkowicz fit) -> pow(1/2.2) encoding. The main shader outputs true HDR
/// linear values, and tone mapping is unified here.
/// 2-1 Step B adds dual tonemap+bloom variants (point/linear): bloom texture
/// (t1, always sampled linearly for half-resolution chain upsampling) *
/// BloomIntensity (second b0 constant), selected automatically by
/// Device.BlitToBackbuffer from FrameSchedule.BloomTexture
/// (see the RenderQuality 2-1 contract section).
/// 2-1 Step C adds dual uber variants (used by the Post pass:
/// tonemap(+bloom) composed into LDR PostColor with luma baked into alpha) and
/// an FXAA variant (used by FinalBlit: reads PostColor and reuses alpha luma to
/// avoid recomputation; texel size is passed in b0 constants 3/4). For the 1-4
/// contract revision, see the RenderQuality class header.
/// 2-2 Step B adds six AO variants (tonemap +/- bloom x point/linear +
/// uber +/- bloom): multiply AO occlusion before adding bloom in ACES-linear
/// space (scene * lerp(1, ao, AoIntensity) + bloom * BloomIntensity, so AO darkens
/// only the scene and not bloom). The AO texture (t2, always linearly sampled
/// from half-resolution GTAO output r) is scaled by AoIntensity (fifth b0
/// constant) and selected automatically by Device from FrameSchedule.AoTexture
/// (see the RenderQuality 2-2 contract section).
/// 2-3 clause 17 adds an RCAS variant alongside the FXAA one: the same uber output,
/// resolved with FSR1 sharpening instead of edge blending when the TAA tier owns the
/// Post slot. It reuses the FXAA variant's texel-size constants and takes its lobe
/// scale from the seventh b0 constant, which used to be packing padding, so the
/// RootSignature is unchanged (see the RenderQuality 2-3 contract section).
/// </summary>
internal static unsafe class BlitPipeline
{
    internal static ID3D12RootSignature* RootSignature;
    internal static ID3D12PipelineState* PipelineState;
    internal static ID3D12PipelineState* LinearPipelineState;
    internal static ID3D12PipelineState* TonemapPipelineState;
    internal static ID3D12PipelineState* TonemapLinearPipelineState;
    internal static ID3D12PipelineState* TonemapBloomPipelineState;
    internal static ID3D12PipelineState* TonemapBloomLinearPipelineState;
    internal static ID3D12PipelineState* UberPipelineState;
    internal static ID3D12PipelineState* UberBloomPipelineState;
    internal static ID3D12PipelineState* FxaaPipelineState;
    internal static ID3D12PipelineState* RcasPipelineState;
    internal static ID3D12PipelineState* TonemapAoPipelineState;
    internal static ID3D12PipelineState* TonemapAoLinearPipelineState;
    internal static ID3D12PipelineState* TonemapBloomAoPipelineState;
    internal static ID3D12PipelineState* TonemapBloomAoLinearPipelineState;
    internal static ID3D12PipelineState* UberAoPipelineState;
    internal static ID3D12PipelineState* UberBloomAoPipelineState;
    internal static ID3D12PipelineState* OutlineCompositePipelineState;

    public static void Init()
    {
        RootSignature = CreateRootSignature();
        PipelineState = CreatePipelineState("PSMain");
        LinearPipelineState = CreatePipelineState("PSMainLinear");
        TonemapPipelineState = CreatePipelineState("PSMainTonemap");
        TonemapLinearPipelineState = CreatePipelineState("PSMainTonemapLinear");
        TonemapBloomPipelineState = CreatePipelineState("PSMainTonemapBloom");
        TonemapBloomLinearPipelineState = CreatePipelineState("PSMainTonemapBloomLinear");
        UberPipelineState = CreatePipelineState("PSMainUber");
        UberBloomPipelineState = CreatePipelineState("PSMainUberBloom");
        FxaaPipelineState = CreatePipelineState("PSMainFxaa");
        RcasPipelineState = CreatePipelineState("PSMainRcas");
        TonemapAoPipelineState = CreatePipelineState("PSMainTonemapAo");
        TonemapAoLinearPipelineState = CreatePipelineState("PSMainTonemapAoLinear");
        TonemapBloomAoPipelineState = CreatePipelineState("PSMainTonemapBloomAo");
        TonemapBloomAoLinearPipelineState = CreatePipelineState("PSMainTonemapBloomAoLinear");
        UberAoPipelineState = CreatePipelineState("PSMainUberAo");
        UberBloomAoPipelineState = CreatePipelineState("PSMainUberBloomAo");
        OutlineCompositePipelineState = CreatePipelineState("PSMainOutlineComposite");
    }

    static ID3D12RootSignature* CreateRootSignature()
    {
        using ComPtr<ID3D10Blob> signature = null;
        using ComPtr<ID3D10Blob> error = null;

        // t0: source texture (offscreen SceneColor); t1: bloom chain output
        // (2-1 Step B, referenced only by bloom variants);
        // t2: AO output (2-2 Step B, referenced only by AO variants)
        var descriptorRanges = stackalloc DescriptorRange[3]
        {
            new DescriptorRange
            {
                RangeType = DescriptorRangeType.Srv,
                NumDescriptors = 1,
                BaseShaderRegister = 0,
                RegisterSpace = 0
            },
            new DescriptorRange
            {
                RangeType = DescriptorRangeType.Srv,
                NumDescriptors = 1,
                BaseShaderRegister = 1,
                RegisterSpace = 0
            },
            new DescriptorRange
            {
                RangeType = DescriptorRangeType.Srv,
                NumDescriptors = 1,
                BaseShaderRegister = 2,
                RegisterSpace = 0
            }
        };

        var rootParameters = stackalloc RootParameter[4];

        // Parameter 0: t0 source texture table
        rootParameters[0] = new RootParameter
        {
            ParameterType = RootParameterType.TypeDescriptorTable,
            DescriptorTable = new RootDescriptorTable
            {
                NumDescriptorRanges = 1,
                PDescriptorRanges = descriptorRanges
            },
            ShaderVisibility = ShaderVisibility.Pixel
        };

        // Parameter 1: b0 root constants
        // 0=exposure 1=bloom 2-3=texelSize 4=ao 5=outlineWidth 6=sharpness 7=padding
        // (blitPad, for HLSL packing alignment; the outline color is carried per
        // pixel by the mask, so there is no constant color slot and slots 8-11
        // are no longer uploaded)
        rootParameters[1] = new RootParameter
        {
            ParameterType = RootParameterType.Type32BitConstants,
            Constants = new RootConstants
            {
                ShaderRegister = 0,
                RegisterSpace = 0,
                Num32BitValues = 8
            },
            ShaderVisibility = ShaderVisibility.Pixel
        };

        // Parameter 2: t1 bloom texture table (harmless when bound for
        // non-bloom variants because they never reference it)
        rootParameters[2] = new RootParameter
        {
            ParameterType = RootParameterType.TypeDescriptorTable,
            DescriptorTable = new RootDescriptorTable
            {
                NumDescriptorRanges = 1,
                PDescriptorRanges = descriptorRanges + 1
            },
            ShaderVisibility = ShaderVisibility.Pixel
        };

        // Parameter 3: t2 AO texture table (harmless when bound for non-AO
        // variants because they never reference it)
        rootParameters[3] = new RootParameter
        {
            ParameterType = RootParameterType.TypeDescriptorTable,
            DescriptorTable = new RootDescriptorTable
            {
                NumDescriptorRanges = 1,
                PDescriptorRanges = descriptorRanges + 2
            },
            ShaderVisibility = ShaderVisibility.Pixel
        };

        // s0 point sampling (1:1 blit, no resampling needed);
        // s1 linear sampling (upsampling non-full-size sources)
        var staticSamplers = stackalloc StaticSamplerDesc[2]
        {
            new StaticSamplerDesc
            {
                Filter = Filter.MinMagMipPoint,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                ShaderRegister = 0, // s0
                ShaderVisibility = ShaderVisibility.Pixel
            },
            new StaticSamplerDesc
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                ShaderRegister = 1, // s1
                ShaderVisibility = ShaderVisibility.Pixel
            }
        };

        // The fullscreen triangle is generated from SV_VertexID, with no vertex
        // input, so AllowInputAssemblerInputLayout is unnecessary.
        var rootSignatureDesc = new RootSignatureDesc
        {
            Flags = RootSignatureFlags.None,
            NumParameters = 4,
            PParameters = rootParameters,
            NumStaticSamplers = 2,
            PStaticSamplers = staticSamplers
        };

        var result0 = Device.D3D12.SerializeRootSignature
            (
                &rootSignatureDesc, D3DRootSignatureVersion.Version1, signature.GetAddressOf(),
                error.GetAddressOf()
            );
        Device.CheckResult(result0);

        ID3D12RootSignature* rootSignature;

        var iid = ID3D12RootSignature.Guid;
        var result = Device.D3dDevice->CreateRootSignature(nodeMask: 0, signature.Get().GetBufferPointer(), signature.Get().GetBufferSize(), &iid, (void**)&rootSignature);
        Device.CheckResult(result);

        return rootSignature;
    }

    static ID3D12PipelineState* CreatePipelineState(string psEntry)
    {
        var compileFlags = 0u;

#if DEBUG
        // Enable better shader debugging with the graphics debugging tools.
        compileFlags |= (1 << 0) | (1 << 2);
#endif

        var hlsl = @"Texture2D sceneColor : register(t0);
Texture2D bloomColor : register(t1); // 2-1 Step B: bloom chain output (half-resolution, referenced only by bloom variants)
Texture2D aoColor : register(t2);    // 2-2 Step B: GTAO output (half-resolution, r = visibility, referenced only by AO variants)
SamplerState pointSampler : register(s0);
SamplerState linearSampler : register(s1);

cbuffer TonemapParams : register(b0)
{
    float exposure;       // Linear exposure scale (Device.HdrExposure, root constant)
    float bloomIntensity; // 2-1: bloom composition scale (RenderQuality.BloomIntensity, referenced only by bloom/uber variants)
    float texelSizeX;     // 2-1 Step C: FXAA source texel size (referenced only by FXAA variants)
    float texelSizeY;
    float aoIntensity;    // 2-2 Step B: AO occlusion intensity (RenderQuality.AoIntensity, referenced only by AO variants)
    float outlineWidth;   // Outline2D width in pixels
    float sharpness;      // 2-3 clause 17: RCAS lobe scale (RenderQuality.TaaSharpness, referenced only by the RCAS variant)
    float blitPad;        // HLSL packing: float4 values may not cross 16-byte register boundaries (the outline color is carried per pixel by the mask, so there is no constant color slot)
};

struct PSInput
{
    float4 position : SV_POSITION;
    float2 uv : TEXCOORD0;
};

// Fullscreen triangle: 3 vertices cover the entire viewport, with UV
// (0,0)/(2,0)/(0,2); the out-of-range area is clipped.
// Compared with a fullscreen quad, it avoids the diagonal seam and duplicate
// shading from two triangles.
PSInput VSMain(uint vid : SV_VertexID)
{
    PSInput output;
    float2 uv = float2((vid << 1) & 2, vid & 2);
    output.position = float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.0, 1.0);
    output.uv = uv;
    return output;
}

float4 PSMain(PSInput input) : SV_TARGET
{
    return sceneColor.Sample(pointSampler, input.uv);
}

float4 PSMainLinear(PSInput input) : SV_TARGET
{
    return sceneColor.Sample(linearSampler, input.uv);
}

// Tonemap variants (1-4 Step B): HDR linear source (RGBA16F) -> exposure ->
// ACES -> gamma encoding to the screen.
// ACES uses the Narkowicz 2015 fitted curve (RRT+ODT approximation). All four
// backends use the same constants to keep visual output consistent.
float3 AcesFilm(float3 x)
{
    const float a = 2.51;
    const float b = 0.03;
    const float c = 2.43;
    const float d = 0.59;
    const float e = 0.14;
    return saturate((x * (a * x + b)) / (x * (c * x + d) + e));
}

float3 Tonemap(float3 hdr)
{
    float3 mapped = AcesFilm(max(hdr, 0.0) * exposure);
    return pow(mapped, 1.0 / 2.2);
}

float4 PSMainTonemap(PSInput input) : SV_TARGET
{
    float4 c = sceneColor.Sample(pointSampler, input.uv);
    return float4(Tonemap(c.rgb), c.a);
}

float4 PSMainTonemapLinear(PSInput input) : SV_TARGET
{
    float4 c = sceneColor.Sample(linearSampler, input.uv);
    return float4(Tonemap(c.rgb), c.a);
}

// Tonemap+bloom variants (2-1 Step B): add bloom in pre-ACES linear space
// (see the RenderQuality 2-1 contract section).
// Bloom comes from the half-resolution chain output and is always upsampled
// with linear filtering.
float4 PSMainTonemapBloom(PSInput input) : SV_TARGET
{
    float4 c = sceneColor.Sample(pointSampler, input.uv);
    c.rgb += bloomColor.Sample(linearSampler, input.uv).rgb * bloomIntensity;
    return float4(Tonemap(c.rgb), c.a);
}

float4 PSMainTonemapBloomLinear(PSInput input) : SV_TARGET
{
    float4 c = sceneColor.Sample(linearSampler, input.uv);
    c.rgb += bloomColor.Sample(linearSampler, input.uv).rgb * bloomIntensity;
    return float4(Tonemap(c.rgb), c.a);
}

// Uber variants (2-1 Step C, used by the Post pass): tonemap(+bloom) and output
// LDR PostColor, with luma (Rec.601 weights, post-gamma space, shared
// cross-backend constant) baked into alpha so FXAA can reuse it.
// Source and target are always the same size (PostColor MatchBackbufferSize),
// so point sampling is always used.
float Luma(float3 ldr)
{
    return dot(ldr, float3(0.299, 0.587, 0.114));
}

float4 PSMainUber(PSInput input) : SV_TARGET
{
    float4 c = sceneColor.Sample(pointSampler, input.uv);
    float3 ldr = Tonemap(c.rgb);
    return float4(ldr, Luma(ldr));
}

float4 PSMainUberBloom(PSInput input) : SV_TARGET
{
    float4 c = sceneColor.Sample(pointSampler, input.uv);
    c.rgb += bloomColor.Sample(linearSampler, input.uv).rgb * bloomIntensity;
    float3 ldr = Tonemap(c.rgb);
    return float4(ldr, Luma(ldr));
}

// AO variants (2-2 Step B): multiply AO occlusion before adding bloom in
// pre-ACES linear space (AO darkens only the scene, not bloom; see the
// RenderQuality 2-2 contract section). AO comes from half-resolution GTAO
// output, is always linearly upsampled, and uses r as visibility.
float3 ApplyAo(float3 scene, float2 uv)
{
    float ao = aoColor.Sample(linearSampler, uv).r;
    return scene * lerp(1.0, ao, aoIntensity);
}

float4 PSMainTonemapAo(PSInput input) : SV_TARGET
{
    float4 c = sceneColor.Sample(pointSampler, input.uv);
    c.rgb = ApplyAo(c.rgb, input.uv);
    return float4(Tonemap(c.rgb), c.a);
}

float4 PSMainTonemapAoLinear(PSInput input) : SV_TARGET
{
    float4 c = sceneColor.Sample(linearSampler, input.uv);
    c.rgb = ApplyAo(c.rgb, input.uv);
    return float4(Tonemap(c.rgb), c.a);
}

float4 PSMainTonemapBloomAo(PSInput input) : SV_TARGET
{
    float4 c = sceneColor.Sample(pointSampler, input.uv);
    c.rgb = ApplyAo(c.rgb, input.uv);
    c.rgb += bloomColor.Sample(linearSampler, input.uv).rgb * bloomIntensity;
    return float4(Tonemap(c.rgb), c.a);
}

float4 PSMainTonemapBloomAoLinear(PSInput input) : SV_TARGET
{
    float4 c = sceneColor.Sample(linearSampler, input.uv);
    c.rgb = ApplyAo(c.rgb, input.uv);
    c.rgb += bloomColor.Sample(linearSampler, input.uv).rgb * bloomIntensity;
    return float4(Tonemap(c.rgb), c.a);
}

float4 PSMainUberAo(PSInput input) : SV_TARGET
{
    float4 c = sceneColor.Sample(pointSampler, input.uv);
    c.rgb = ApplyAo(c.rgb, input.uv);
    float3 ldr = Tonemap(c.rgb);
    return float4(ldr, Luma(ldr));
}

float4 PSMainUberBloomAo(PSInput input) : SV_TARGET
{
    float4 c = sceneColor.Sample(pointSampler, input.uv);
    c.rgb = ApplyAo(c.rgb, input.uv);
    c.rgb += bloomColor.Sample(linearSampler, input.uv).rgb * bloomIntensity;
    float3 ldr = Tonemap(c.rgb);
    return float4(ldr, Luma(ldr));
}

// FXAA variant (2-1 Step C, used by FinalBlit): FXAA 3.11 reduced-quality
// preset (5-tap direction estimation + 4-tap directional sampling). Luma comes
// from source alpha (baked by the uber pass). REDUCE_MIN/REDUCE_MUL/SPAN_MAX
// and the contrast thresholds are shared cross-backend contract constants and
// should be ported literally. Single-exit writing avoids fxc X4000.
float4 PSMainFxaa(PSInput input) : SV_TARGET
{
    const float FXAA_REDUCE_MIN = 1.0 / 128.0;
    const float FXAA_REDUCE_MUL = 1.0 / 8.0;
    const float FXAA_SPAN_MAX = 8.0;
    const float FXAA_EDGE_THRESHOLD = 1.0 / 8.0;
    const float FXAA_EDGE_THRESHOLD_MIN = 1.0 / 24.0;

    float2 rcpFrame = float2(texelSizeX, texelSizeY);
    float2 uv = input.uv;

    float4 colorM = sceneColor.Sample(pointSampler, uv);
    float lumaM  = colorM.a;
    float lumaNW = sceneColor.Sample(pointSampler, uv + float2(-1.0, -1.0) * rcpFrame).a;
    float lumaNE = sceneColor.Sample(pointSampler, uv + float2( 1.0, -1.0) * rcpFrame).a;
    float lumaSW = sceneColor.Sample(pointSampler, uv + float2(-1.0,  1.0) * rcpFrame).a;
    float lumaSE = sceneColor.Sample(pointSampler, uv + float2( 1.0,  1.0) * rcpFrame).a;

    float lumaMin = min(lumaM, min(min(lumaNW, lumaNE), min(lumaSW, lumaSE)));
    float lumaMax = max(lumaM, max(max(lumaNW, lumaNE), max(lumaSW, lumaSE)));

    float4 result = colorM;

    // Early-out on low contrast: pass non-edge pixels through to save the
    // bandwidth of directional sampling.
    if (lumaMax - lumaMin >= max(FXAA_EDGE_THRESHOLD_MIN, lumaMax * FXAA_EDGE_THRESHOLD))
    {
        // Edge tangent direction (orthogonal to the luma gradient), normalized
        // by local brightness and clamped to the maximum span.
        float2 dir = float2(
            -((lumaNW + lumaNE) - (lumaSW + lumaSE)),
             ((lumaNW + lumaSW) - (lumaNE + lumaSE)));

        float dirReduce = max((lumaNW + lumaNE + lumaSW + lumaSE) * 0.25 * FXAA_REDUCE_MUL, FXAA_REDUCE_MIN);
        float rcpDirMin = 1.0 / (min(abs(dir.x), abs(dir.y)) + dirReduce);
        dir = clamp(dir * rcpDirMin, -FXAA_SPAN_MAX, FXAA_SPAN_MAX) * rcpFrame;

        // Four taps along the tangent: the inner pair (+/-1/6 span) is always
        // trusted, and the outer pair (+/-1/2 span) falls back to the inner pair
        // if it goes out of range.
        float4 rgbA = 0.5 * (
            sceneColor.Sample(linearSampler, uv + dir * (1.0 / 3.0 - 0.5)) +
            sceneColor.Sample(linearSampler, uv + dir * (2.0 / 3.0 - 0.5)));
        float4 rgbB = rgbA * 0.5 + 0.25 * (
            sceneColor.Sample(linearSampler, uv + dir * -0.5) +
            sceneColor.Sample(linearSampler, uv + dir * 0.5));

        result = (rgbB.a < lumaMin || rgbB.a > lumaMax) ? rgbA : rgbB;
    }

    return float4(result.rgb, 1.0);
}

// RCAS variant (2-3 clause 17, used by FinalBlit): FSR1 RCAS, ported from
// FsrRcasF in ffx_fsr1.h. Source is the same LDR uber output the FXAA variant
// reads, and that is a hard requirement rather than convenience: the limiter
// below measures headroom against display white as a literal 1.0, which only
// holds once the uber pass has tonemapped and gamma encoded. Run against
// scene-referred HDR the (1 - mx4) term goes negative and the filter inverts.
float4 PSMainRcas(PSInput input) : SV_TARGET
{
    // FSR1's FSR_RCAS_LIMIT. 0.25 is the lobe at which the 4-tap ring would start
    // to ring; 1/16 is the slack FSR1 keeps below it.
    const float RCAS_LIMIT = 0.25 - 1.0 / 16.0;

    // Guard for the two neighbourhoods where FSR1's reciprocals divide by zero:
    // an all-black ring (mx4 = 0) and an all-white one (mn4 = 1, which also zeroes
    // the second numerator into 0/0). It goes in the denominators only, which
    // yields 0 for both - the same no-sharpening answer FSR1 already gives for
    // every other ring with mn4 = 0 or mx4 = 1, so this is the faithful one-sided
    // limit rather than an invented case. Adding it to the numerators instead
    // would make a black ring report hitMin = 1 and amplify an isolated bright
    // pixel 4x. Away from those two points it only perturbs rings darker than
    // 1e-4, which is far below one 8-bit code value of the target.
    const float RCAS_EPS = 1.0 / 32768.0;

    float2 rcpFrame = float2(texelSizeX, texelSizeY);
    float2 uv = input.uv;

    // Cross neighbourhood, point sampled at 1:1 (the same texel-load-through-uv
    // the FXAA variant relies on).
    //     b
    //   d e f
    //     h
    float3 e = sceneColor.Sample(pointSampler, uv).rgb;
    float3 b = sceneColor.Sample(pointSampler, uv + float2( 0.0, -1.0) * rcpFrame).rgb;
    float3 d = sceneColor.Sample(pointSampler, uv + float2(-1.0,  0.0) * rcpFrame).rgb;
    float3 f = sceneColor.Sample(pointSampler, uv + float2( 1.0,  0.0) * rcpFrame).rgb;
    float3 h = sceneColor.Sample(pointSampler, uv + float2( 0.0,  1.0) * rcpFrame).rgb;

    // The ring only, deliberately excluding the centre: the question the limiter
    // asks is how far e may be pushed away from its neighbours, so letting e into
    // the extents would license its own displacement.
    float3 mn4 = min(min(b, d), min(f, h));
    float3 mx4 = max(max(b, d), max(f, h));

    // Headroom towards black and towards white expressed in units of the lobe
    // weight; both are non-positive after the max, and the tightest of the three
    // channels decides, so no channel can be pushed out of range.
    float3 hitMin = mn4 / (4.0 * mx4 + RCAS_EPS);
    float3 hitMax = (1.0 - mx4) / (4.0 * mn4 - 4.0 - RCAS_EPS);
    float3 lobeRgb = max(-hitMin, hitMax);
    float lobe = max(-RCAS_LIMIT, min(max(lobeRgb.r, max(lobeRgb.g, lobeRgb.b)), 0.0)) * sharpness;

    // FSR_RCAS_DENOISE is deliberately not ported rather than overlooked: it is
    // FSR1's opt-in for noisy input, and this input is a temporally converged TAA
    // resolve. The divide needs no guard because lobe is clamped into
    // [-RCAS_LIMIT, 0], which keeps 4 * lobe + 1 inside [0.25, 1].
    float3 result = (lobe * (b + d + f + h) + e) / (4.0 * lobe + 1.0);

    // RCAS is not bounded by construction; FSR1 leans on the UNORM render target
    // to clamp it. The backbuffer does clamp, but saturating here states the
    // dependency instead of inheriting it from a format.
    return float4(saturate(result), 1.0);
}

float4 PSMainOutlineComposite(PSInput input) : SV_TARGET
{
    float2 texel = float2(texelSizeX, texelSizeY);
    float2 stepUv = texel * max(outlineWidth, 1.0);

    // Inside the mask, alpha is always 1 and RGB stores the outline color of the
    // owning group (multiple colors can appear in the same frame). Edge
    // detection uses alpha so pure black colors still work, and the outline
    // color comes from the first sample in the 8-neighborhood with the maximum
    // alpha, which matches the wrapped object's group color.
    float center = sceneColor.Sample(pointSampler, input.uv).a;
    float neighbor = 0.0;
    float3 color = 0.0;
    const float2 offsets[8] = {
        float2( 1,  0), float2(-1,  0), float2(0,  1), float2(0, -1),
        float2( 1,  1), float2(-1,  1), float2(1, -1), float2(-1, -1) };
    for (int k = 0; k < 8; k++)
    {
        float4 s = sceneColor.Sample(pointSampler, input.uv + offsets[k] * stepUv);
        if (s.a > neighbor)
        {
            neighbor = s.a;
            color = s.rgb;
        }
    }

    float edge = saturate(neighbor - center);
    return float4(color, edge);
}
";

        ID3D10Blob* vertexShaderBlob = ShaderCompiler.CompileShaderFromSource(hlsl, "VSMain", "vs_5_0", compileFlags);
        ID3D10Blob* pixelShaderBlob = ShaderCompiler.CompileShaderFromSource(hlsl, psEntry, "ps_5_0", compileFlags);

        var defaultRenderTargetBlend = new RenderTargetBlendDesc()
        {
            BlendEnable = 0,
            LogicOpEnable = 0,
            SrcBlend = Blend.One,
            DestBlend = Blend.Zero,
            BlendOp = BlendOp.Add,
            SrcBlendAlpha = Blend.One,
            DestBlendAlpha = Blend.Zero,
            BlendOpAlpha = BlendOp.Add,
            LogicOp = LogicOp.Noop,
            RenderTargetWriteMask = (byte)ColorWriteEnable.All
        };

        var alphaBlend = new RenderTargetBlendDesc()
        {
            BlendEnable = 1,
            LogicOpEnable = 0,
            SrcBlend = Blend.SrcAlpha,
            DestBlend = Blend.InvSrcAlpha,
            BlendOp = BlendOp.Add,
            SrcBlendAlpha = Blend.One,
            DestBlendAlpha = Blend.Zero,
            BlendOpAlpha = BlendOp.Add,
            LogicOp = LogicOp.Noop,
            RenderTargetWriteMask = (byte)ColorWriteEnable.All
        };

        var defaultStencilOp = new DepthStencilopDesc
        {
            StencilFailOp = StencilOp.Keep,
            StencilDepthFailOp = StencilOp.Keep,
            StencilPassOp = StencilOp.Keep,
            StencilFunc = ComparisonFunc.Always
        };

        // The target is always the single-sampled backbuffer (MSAA is resolved in
        // the Scene pass), with no DSV bound and no depth reads or writes.
        GraphicsPipelineStateDesc psoDesc = new GraphicsPipelineStateDesc
        {
            // No vertex input: leave InputLayout empty
            PRootSignature = RootSignature,
            VS = new ShaderBytecode(vertexShaderBlob->GetBufferPointer(), vertexShaderBlob->GetBufferSize()),
            PS = new ShaderBytecode(pixelShaderBlob->GetBufferPointer(), pixelShaderBlob->GetBufferSize()),
            RasterizerState = new RasterizerDesc
            {
                FillMode = FillMode.Solid,
                CullMode = CullMode.None,
                FrontCounterClockwise = 0,
                DepthClipEnable = 1,
            },
            BlendState = new BlendDesc
            {
                AlphaToCoverageEnable = 0,
                IndependentBlendEnable = 0,
                RenderTarget = new BlendDesc.RenderTargetBuffer()
                {
                    [0] = psEntry == "PSMainOutlineComposite" ? alphaBlend : defaultRenderTargetBlend,
                    [1] = defaultRenderTargetBlend,
                    [2] = defaultRenderTargetBlend,
                    [3] = defaultRenderTargetBlend,
                    [4] = defaultRenderTargetBlend,
                    [5] = defaultRenderTargetBlend,
                    [6] = defaultRenderTargetBlend,
                    [7] = defaultRenderTargetBlend
                }
            },
            DepthStencilState = new DepthStencilDesc
            {
                DepthEnable = 0,
                DepthWriteMask = DepthWriteMask.Zero,
                DepthFunc = ComparisonFunc.Always,
                StencilEnable = 0,
                StencilReadMask = D3D12.DefaultStencilReadMask,
                StencilWriteMask = D3D12.DefaultStencilWriteMask,
                FrontFace = defaultStencilOp,
                BackFace = defaultStencilOp
            },
            SampleMask = uint.MaxValue,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            NumRenderTargets = 1,
            SampleDesc = new SampleDesc(1, 0),
        };
        psoDesc.RTVFormats[0] = Device.BackBufferFormat;
        psoDesc.DSVFormat = Format.FormatUnknown;

        ID3D12PipelineState* pipelineState;

        var iid = ID3D12PipelineState.Guid;
        var result = Device.D3dDevice->CreateGraphicsPipelineState(&psoDesc, &iid, (void**)&pipelineState);
        Device.CheckResult(result);

        vertexShaderBlob->Release();
        pixelShaderBlob->Release();

        return pipelineState;
    }

    /// <summary>
    /// Draws to the screen with a fullscreen triangle. Preconditions: the
    /// FinalBlit pass has already called BeginPass (the backbuffer is bound as
    /// the RT), and the source texture has already been transitioned to
    /// PixelShaderResource. `linear=true` uses linear sampling (for upsampling
    /// non-full-size sources). `tonemap=true` selects the tonemap variant
    /// (HDR source, Step B: Device.HdrExposure + ACES + gamma encoding).
    /// `bloom=true` (requires tonemap) additionally binds `bloomSrv`
    /// (already in PixelShaderResource state) and uploads
    /// `RenderQuality.BloomIntensity`. `ao=true` (requires tonemap, 2-2 Step B)
    /// additionally binds `aoSrv` (already in PixelShaderResource state) and
    /// uploads `RenderQuality.AoIntensity`.
    /// State leakage is harmless because normal drawing rebinds
    /// RootSignature / PSO / topology in every SetPipeline call.
    /// </summary>
    internal static void Draw(GpuDescriptorHandle srcSrv, bool linear = false, bool tonemap = false,
        GpuDescriptorHandle bloomSrv = default, bool bloom = false,
        GpuDescriptorHandle aoSrv = default, bool ao = false)
    {
        var cmdList = Device.GraphicsCommandList;

        var pso = (ao, bloom, tonemap, linear) switch
        {
            (true, true, _, true) => TonemapBloomAoLinearPipelineState,
            (true, true, _, false) => TonemapBloomAoPipelineState,
            (true, false, _, true) => TonemapAoLinearPipelineState,
            (true, false, _, false) => TonemapAoPipelineState,
            (false, true, _, true) => TonemapBloomLinearPipelineState,
            (false, true, _, false) => TonemapBloomPipelineState,
            (false, false, true, true) => TonemapLinearPipelineState,
            (false, false, true, false) => TonemapPipelineState,
            (false, false, false, true) => LinearPipelineState,
            (false, false, false, false) => PipelineState,
        };

        cmdList->SetGraphicsRootSignature(RootSignature);
        cmdList->SetPipelineState(pso);
        cmdList->IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
        cmdList->SetGraphicsRootDescriptorTable(0, srcSrv);
        if (tonemap)
            cmdList->SetGraphicsRoot32BitConstant(1, BitConverter.SingleToUInt32Bits(Device.HdrExposure), 0);
        if (bloom)
        {
            cmdList->SetGraphicsRoot32BitConstant(1, BitConverter.SingleToUInt32Bits(RenderQuality.Current.BloomIntensity), 1);
            cmdList->SetGraphicsRootDescriptorTable(2, bloomSrv);
        }
        if (ao)
        {
            cmdList->SetGraphicsRoot32BitConstant(1, BitConverter.SingleToUInt32Bits(RenderQuality.Current.AoIntensity), 4);
            cmdList->SetGraphicsRootDescriptorTable(3, aoSrv);
        }
        cmdList->DrawInstanced(3, 1, 0, 0);
    }

    /// <summary>
    /// 2-1 Step C: uber composition inside the Post pass
    /// (tonemap+bloom -> LDR PostColor, with luma baked into alpha).
    /// Preconditions: the Post pass has already called BeginPass (PostColor is
    /// bound as the RT), and the source/bloom textures are already in
    /// PixelShaderResource state.
    /// 2-2 Step B: when `ao=true`, also bind `aoSrv` and upload
    /// `RenderQuality.AoIntensity` (AO variant selection matches Draw).
    /// </summary>
    internal static void DrawUber(GpuDescriptorHandle srcSrv, GpuDescriptorHandle bloomSrv = default, bool bloom = false,
        GpuDescriptorHandle aoSrv = default, bool ao = false)
    {
        var cmdList = Device.GraphicsCommandList;

        var pso = (ao, bloom) switch
        {
            (true, true) => UberBloomAoPipelineState,
            (true, false) => UberAoPipelineState,
            (false, true) => UberBloomPipelineState,
            (false, false) => UberPipelineState,
        };

        cmdList->SetGraphicsRootSignature(RootSignature);
        cmdList->SetPipelineState(pso);
        cmdList->IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
        cmdList->SetGraphicsRootDescriptorTable(0, srcSrv);
        cmdList->SetGraphicsRoot32BitConstant(1, BitConverter.SingleToUInt32Bits(Device.HdrExposure), 0);
        if (bloom)
        {
            cmdList->SetGraphicsRoot32BitConstant(1, BitConverter.SingleToUInt32Bits(RenderQuality.Current.BloomIntensity), 1);
            cmdList->SetGraphicsRootDescriptorTable(2, bloomSrv);
        }
        if (ao)
        {
            cmdList->SetGraphicsRoot32BitConstant(1, BitConverter.SingleToUInt32Bits(RenderQuality.Current.AoIntensity), 4);
            cmdList->SetGraphicsRootDescriptorTable(3, aoSrv);
        }
        cmdList->DrawInstanced(3, 1, 0, 0);
    }

    /// <summary>
    /// 2-1 Step C: present with FXAA inside FinalBlit
    /// (source is the LDR PostColor output from the uber pass, with luma in
    /// alpha). Preconditions are the same as Draw. Texel size is 1/source size
    /// and is uploaded every frame because it changes at runtime with resize.
    /// </summary>
    internal static void DrawFxaa(GpuDescriptorHandle srcSrv, float texelSizeX, float texelSizeY)
    {
        var cmdList = Device.GraphicsCommandList;

        cmdList->SetGraphicsRootSignature(RootSignature);
        cmdList->SetPipelineState(FxaaPipelineState);
        cmdList->IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
        cmdList->SetGraphicsRootDescriptorTable(0, srcSrv);
        cmdList->SetGraphicsRoot32BitConstant(1, BitConverter.SingleToUInt32Bits(texelSizeX), 2);
        cmdList->SetGraphicsRoot32BitConstant(1, BitConverter.SingleToUInt32Bits(texelSizeY), 3);
        cmdList->DrawInstanced(3, 1, 0, 0);
    }

    /// <summary>
    /// 2-3 clause 17: present with RCAS inside FinalBlit. Source is the same LDR
    /// PostColor output as DrawFxaa (alpha luma is simply left unread here), and
    /// the preconditions are the same as Draw. Sharpness is uploaded every frame
    /// because RenderQuality.TaaSharpness is a runtime knob.
    /// </summary>
    internal static void DrawRcas(GpuDescriptorHandle srcSrv, float texelSizeX, float texelSizeY, float sharpness)
    {
        var cmdList = Device.GraphicsCommandList;

        cmdList->SetGraphicsRootSignature(RootSignature);
        cmdList->SetPipelineState(RcasPipelineState);
        cmdList->IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
        cmdList->SetGraphicsRootDescriptorTable(0, srcSrv);
        cmdList->SetGraphicsRoot32BitConstant(1, BitConverter.SingleToUInt32Bits(texelSizeX), 2);
        cmdList->SetGraphicsRoot32BitConstant(1, BitConverter.SingleToUInt32Bits(texelSizeY), 3);
        cmdList->SetGraphicsRoot32BitConstant(1, BitConverter.SingleToUInt32Bits(sharpness), 6);
        cmdList->DrawInstanced(3, 1, 0, 0);
    }

    internal static void DrawOutlineComposite(GpuDescriptorHandle maskSrv, float texelSizeX, float texelSizeY,
        float widthPixels)
    {
        var cmdList = Device.GraphicsCommandList;

        cmdList->SetGraphicsRootSignature(RootSignature);
        cmdList->SetPipelineState(OutlineCompositePipelineState);
        cmdList->IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
        cmdList->SetGraphicsRootDescriptorTable(0, maskSrv);
        cmdList->SetGraphicsRoot32BitConstant(1, BitConverter.SingleToUInt32Bits(texelSizeX), 2);
        cmdList->SetGraphicsRoot32BitConstant(1, BitConverter.SingleToUInt32Bits(texelSizeY), 3);
        cmdList->SetGraphicsRoot32BitConstant(1, BitConverter.SingleToUInt32Bits(widthPixels), 5);
        cmdList->DrawInstanced(3, 1, 0, 0);
    }
}

/// <summary>独立 RGBA16F 雾合成；每次准备均保留独立描述符与原生资源引用。</summary>
internal sealed unsafe class DXFogCompositePipeline : FogCompositePipeline
{
    readonly bool _weather;
    uint FrameBytes => (uint)(_weather ? WeatherCurtainEffect.FrameVectorCount : VolumetricFogEffect.FrameVectorCount) * 16;
    RenderPassId CompositePass => _weather ? RenderPassId.WeatherComposite : RenderPassId.FogComposite;
    readonly Dictionary<string, DXTexture> _textures;
    readonly List<BindingSlot>[] _slots = new List<BindingSlot>[(int)Device.frameCount];
    readonly int[] _cursors = new int[(int)Device.frameCount];
    readonly ulong[] _stamps = new ulong[(int)Device.frameCount];
    ID3D12RootSignature* _signature;
    ID3D12PipelineState* _pipeline;
    ID3D12GraphicsCommandList* _command;
    FogCompositeInputs _inputs;
    DXRenderTarget? _destination;
    BindingSlot? _prepared;
    static DXFogCompositePipeline? _transparentOwner;
    BindingSlot? _transparentSlot;
    FogCompositeInputs _transparentInputs;
    DXRenderTarget? _transparentDestination;
    ulong _stamp;
    uint _frameIndex;
    bool _disposed;

    // 引用保活使 Resize 前后的原生地址不会被复用；描述符不可从共享可见堆原地借用。
    sealed class BindingSlot
    {
        internal readonly int[] Ids = [-1, -1, -1, -1, -1, -1];
        internal readonly nint[] Resources = new nint[7];
        internal readonly ShaderResourceViewDesc[] Views = new ShaderResourceViewDesc[6];

        internal BindingSlot()
        {
            try { for (int i = 0; i < Ids.Length; i++) Ids[i] = Device.DescriptorAllocator.Allocate(); }
            catch { Dispose(); throw; }
        }

        internal void ReleaseResources()
        {
            for (int i = 0; i < Views.Length; i++)
            {
                if (Resources[i] == 0) continue;
                var view = Views[i];
                Device.D3dDevice->CreateShaderResourceView(null, &view, Device.SrvHeapManager.GetCpuHandle(Ids[i]));
            }
            for (int i = 0; i < Resources.Length; i++)
            {
                if (Resources[i] != 0) ((ID3D12Resource*)Resources[i])->Release();
                Resources[i] = 0;
            }
        }

        internal void Capture(ReadOnlySpan<nint> resources, ReadOnlySpan<ShaderResourceViewDesc> views)
        {
            ReleaseResources();
            resources.CopyTo(Resources); views.CopyTo(Views);
            foreach (nint resource in Resources) ((ID3D12Resource*)resource)->AddRef();
            for (int i = 0; i < Views.Length; i++)
            {
                var view = Views[i];
                Device.D3dDevice->CreateShaderResourceView((ID3D12Resource*)Resources[i], &view,
                    Device.SrvHeapManager.GetCpuHandle(Ids[i]));
            }
        }

        internal void Dispose()
        {
            ReleaseResources();
            for (int i = 0; i < Ids.Length; i++)
            {
                if (Ids[i] >= 0) Device.DescriptorAllocator.Free(Ids[i]);
                Ids[i] = -1;
            }
        }
    }

    internal DXFogCompositePipeline(Dictionary<string, DXTexture> textures) : this(textures, false) { }

    internal DXFogCompositePipeline(Dictionary<string, DXTexture> textures, bool weather)
    {
        _weather = weather;
        _textures = textures;
        for (int i = 0; i < _slots.Length; i++) _slots[i] = new();
        try { Initialize(); }
        catch { Dispose(); throw; }
    }

    void Initialize()
    {
        var ranges = stackalloc DescriptorRange[6];
        var parameters = stackalloc RootParameter[7];
        for (int i = 0; i < 6; i++)
        {
            ranges[i] = new() { RangeType = DescriptorRangeType.Srv, NumDescriptors = 1, BaseShaderRegister = (uint)i };
            parameters[i] = new() { ParameterType = RootParameterType.TypeDescriptorTable,
                DescriptorTable = new() { NumDescriptorRanges = 1, PDescriptorRanges = ranges + i },
                ShaderVisibility = ShaderVisibility.Pixel };
        }
        parameters[6] = new() { ParameterType = RootParameterType.Type32BitConstants,
            Constants = new() { ShaderRegister = 0, Num32BitValues = 4 }, ShaderVisibility = ShaderVisibility.Pixel };
        var sampler = new StaticSamplerDesc { Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp,
            MaxLOD = float.MaxValue, ComparisonFunc = ComparisonFunc.Always, ShaderVisibility = ShaderVisibility.Pixel };
        var root = new RootSignatureDesc { NumParameters = 7, PParameters = parameters,
            NumStaticSamplers = 1, PStaticSamplers = &sampler };
        using ComPtr<ID3D10Blob> blob = null;
        using ComPtr<ID3D10Blob> error = null;
        Device.CheckResult(Device.D3D12.SerializeRootSignature(&root, D3DRootSignatureVersion.Version1,
            blob.GetAddressOf(), error.GetAddressOf()));
        ID3D12RootSignature* signature = null;
        var iid = ID3D12RootSignature.Guid;
        Device.CheckResult(Device.D3dDevice->CreateRootSignature(0, blob.Get().GetBufferPointer(),
            blob.Get().GetBufferSize(), &iid, (void**)&signature));
        _signature = signature;
        ID3D10Blob* vertex = null; ID3D10Blob* fragment = null;
        try
        {
            vertex = ShaderCompiler.CompileShaderFromSource(VolumetricFogEffect.CompositeVertexHlsl, "VSMain", "vs_5_0");
            fragment = ShaderCompiler.CompileShaderFromSource(_weather ? WeatherShaders.CompositeHlsl : VolumetricFogEffect.CompositeHlsl, "PSMain", "ps_5_0");
            var stencil = new DepthStencilopDesc { StencilFailOp = StencilOp.Keep, StencilDepthFailOp = StencilOp.Keep,
                StencilPassOp = StencilOp.Keep, StencilFunc = ComparisonFunc.Always };
            var desc = new GraphicsPipelineStateDesc {
                PRootSignature = _signature,
                VS = new(vertex->GetBufferPointer(), vertex->GetBufferSize()),
                PS = new(fragment->GetBufferPointer(), fragment->GetBufferSize()),
                RasterizerState = new() { FillMode = FillMode.Solid, CullMode = CullMode.None, DepthClipEnable = 1 },
                DepthStencilState = new() { DepthFunc = ComparisonFunc.Always, FrontFace = stencil, BackFace = stencil },
                SampleMask = uint.MaxValue, PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                NumRenderTargets = 1, SampleDesc = new(1, 0) };
            desc.BlendState.RenderTarget[0] = new() { SrcBlend = Blend.One, DestBlend = Blend.Zero, BlendOp = BlendOp.Add,
                SrcBlendAlpha = Blend.One, DestBlendAlpha = Blend.Zero, BlendOpAlpha = BlendOp.Add,
                LogicOp = LogicOp.Noop, RenderTargetWriteMask = (byte)ColorWriteEnable.All };
            desc.RTVFormats[0] = Format.FormatR16G16B16A16Float;
            ID3D12PipelineState* pipeline = null;
            iid = ID3D12PipelineState.Guid;
            Device.CheckResult(Device.D3dDevice->CreateGraphicsPipelineState(&desc, &iid, (void**)&pipeline));
            _pipeline = pipeline;
        }
        finally
        {
            if (vertex != null) vertex->Release();
            if (fragment != null) fragment->Release();
        }
    }

    static ShaderResourceViewDesc TextureView(Format format, bool volume = false) => new() {
        Format = format, Shader4ComponentMapping = 0x1688,
        ViewDimension = volume ? SrvDimension.Texture3D : SrvDimension.Texture2D,
        Texture2D = new() { MipLevels = 1 } };

    // Resolve 不改变 GPU 状态，Draw 也可用它核对资源身份；转换仅在 Pass 外执行。
    bool Resolve(in FogCompositeInputs input, DXRenderTarget destination, Span<nint> resources,
        Span<ShaderResourceViewDesc> views, bool transition)
    {
        if (input.Color is not DXRenderTarget color || input.Depth is not DXRenderTarget depth
            || input.FrameBuffer is not DXStorageBuffer frame || frame.Resource == null || frame.SizeInBytes < FrameBytes
            || color.Desc.ColorFormat != RtFormat.Rgba16Float || destination.Desc.ColorFormat != RtFormat.Rgba16Float
            || destination.Desc.DepthFormat != RtFormat.None || destination.HasDepth
            || depth.Color != null || depth.Desc.DepthFormat != RtFormat.D32Float
            || color.Desc.SampleCount != 1 || depth.Desc.SampleCount != 1 || destination.Desc.SampleCount != 1
            || color.Color == null || depth.Depth == null || destination.Color == null
            || destination.RtvIndex == DXRenderTarget.NoRtv || destination.Color == color.Color
            || color.Width == 0 || color.Height == 0 || color.Width != depth.Width || color.Height != depth.Height
            || color.Width != destination.Width || color.Height != destination.Height
            || color.ColorNativeFormat != Format.FormatR16G16B16A16Float
            || destination.ColorNativeFormat != Format.FormatR16G16B16A16Float
            || !float.IsFinite(input.AoIntensity) || input.AoIntensity < 0) return false;
        var integrated = _weather ? null : DXTexture3D.Find(input.IntegratedVolume);
        var local = _weather ? null : DXTexture3D.Find(input.LocalVolume);
        if (!_weather && (integrated == null || local == null || !integrated.Ready || !local.Ready
            || integrated.NativeResource == null || local.NativeResource == null || local.Depth == 0
            || local.Width == 0 || local.Height == 0 || integrated.Width != local.Width || integrated.Height != local.Height
            || integrated.Depth != local.Depth + 1 || integrated.NativeFormat != Format.FormatR16G16B16A16Float
            || local.NativeFormat != Format.FormatR16G16B16A16Float)) return false;
        DXRenderTarget? aoTarget = null; DXTexture? aoTexture = null;
        ID3D12Resource* aoResource = color.Color;
        Format aoFormat = color.ColorNativeFormat;
        if (input.ApplyAo)
        {
            if (input.Ao.Target is DXRenderTarget target)
            {
                if (target.Color == null || target.Desc.SampleCount != 1 || target.Width == 0 || target.Height == 0) return false;
                aoTarget = target; aoResource = target.Color; aoFormat = target.ColorNativeFormat;
            }
            else
            {
                if (input.Ao.TextureName != null)
                    lock (_textures) _textures.TryGetValue(input.Ao.TextureName, out aoTexture);
                if (aoTexture == null || !System.Threading.Volatile.Read(ref aoTexture.Ready)
                    || aoTexture._textureResource == null || aoTexture.Width == 0 || aoTexture.Height == 0) return false;
                aoResource = aoTexture._textureResource;
                aoFormat = aoResource->GetDesc().Format;
            }
            if (aoResource == destination.Color || aoFormat is not (Format.FormatR8Unorm or Format.FormatR16Float
                or Format.FormatR16G16Float or Format.FormatR16G16B16A16Float or Format.FormatR8G8B8A8Unorm)) return false;
        }
        resources[0] = (nint)frame.Resource;
        // 雨幕 shader 不引用 t1/t2；使用输入颜色的合法视图保留描述符快照布局。
        resources[1] = (nint)(_weather ? color.Color : integrated!.NativeResource);
        resources[2] = (nint)(_weather ? color.Color : local!.NativeResource); resources[3] = (nint)color.Color;
        resources[4] = (nint)depth.Depth; resources[5] = (nint)aoResource; resources[6] = (nint)destination.Color;
        views[0] = new() { Format = Format.FormatR32Typeless, Shader4ComponentMapping = 0x1688,
            ViewDimension = SrvDimension.Buffer, Buffer = new() { NumElements = FrameBytes / 4, Flags = BufferSrvFlags.Raw } };
        views[1] = _weather ? TextureView(color.ColorNativeFormat) : TextureView(integrated!.NativeFormat, true);
        views[2] = _weather ? TextureView(color.ColorNativeFormat) : TextureView(local!.NativeFormat, true);
        views[3] = TextureView(color.ColorNativeFormat); views[4] = TextureView(Format.FormatR32Float);
        views[5] = TextureView(aoFormat);
        if (transition)
        {
            var command = Device.GraphicsCommandList;
            frame.TransitionTo(command, ResourceStates.AllShaderResource);
            integrated?.TransitionTo(command, ResourceStates.AllShaderResource);
            local?.TransitionTo(command, ResourceStates.AllShaderResource);
            color.TransitionTo(command, ResourceStates.AllShaderResource);
            depth.TransitionDepthTo(command, ResourceStates.AllShaderResource);
            aoTarget?.TransitionTo(command, ResourceStates.AllShaderResource);
            if (aoTexture != null)
            {
                aoTexture.EnsureReadyForRendering(command);
                aoTexture.TransitionTo(command, ResourceStates.AllShaderResource);
            }
        }
        else
        {
            const ResourceStates read = ResourceStates.PixelShaderResource;
            if ((frame.CurrentState & read) == 0
                || (!_weather && ((integrated!.CurrentState & read) == 0 || (local!.CurrentState & read) == 0))
                || (color.CurrentState & read) == 0 || (depth.DepthCurrentState & read) == 0
                || (aoTarget != null && (aoTarget.CurrentState & read) == 0)
                || (aoTexture != null && (aoTexture.CurrentState & read) == 0)
                || destination.CurrentState != ResourceStates.RenderTarget) return false;
        }
        return true;
    }

    public override bool Prepare(in FogCompositeInputs inputs, RenderTarget destination)
    {
        if (!_weather) ResetTransparent();
        _destination = null; _prepared = null; _inputs = default;
        if (_disposed || Device.ActiveTarget != null || Device.FrameContexts == null
            || Device.FrameIndex >= _slots.Length || Device.GraphicsCommandList == null
            || destination is not DXRenderTarget target) return false;
        int index = (int)Device.FrameIndex;
        ulong stamp = Device.GetCurrentRetireFenceValue();
        if (_stamps[index] != stamp)
        {
            if (_stamps[index] > Device.GetCompletedFenceValue()) return false;
            _stamps[index] = stamp; _cursors[index] = 0;
        }
        Span<nint> resources = stackalloc nint[7];
        Span<ShaderResourceViewDesc> views = stackalloc ShaderResourceViewDesc[6];
        if (!Resolve(inputs, target, resources, views, true)) return false;
        var ring = _slots[index];
        if (_cursors[index] == ring.Count) ring.Add(new BindingSlot());
        var slot = ring[_cursors[index]++];
        slot.Capture(resources, views);
        _inputs = inputs; _destination = target; _prepared = slot;
        _command = Device.GraphicsCommandList; _stamp = stamp; _frameIndex = Device.FrameIndex;
        return true;
    }

    public override FogCompositeResult Draw()
    {
        var destination = _destination; var inputs = _inputs; var slot = _prepared;
        _destination = null; _inputs = default; _prepared = null;
        if (_disposed || destination == null || slot == null || Device.ActivePassId != CompositePass
            || !ReferenceEquals(Device.ActiveTarget, destination) || Device.GraphicsCommandList != _command
            || Device.FrameIndex != _frameIndex || Device.GetCurrentRetireFenceValue() != _stamp) return default;
        Span<nint> resources = stackalloc nint[7];
        Span<ShaderResourceViewDesc> views = stackalloc ShaderResourceViewDesc[6];
        if (!Resolve(inputs, destination, resources, views, false) || !resources.SequenceEqual(slot.Resources)) return default;
        for (int i = 0; i < views.Length; i++) if (views[i].Format != slot.Views[i].Format) return default;
        var heap = Device.SrvHeapManager.Heap;
        _command->SetDescriptorHeaps(1, &heap);
        _command->SetGraphicsRootSignature(_signature);
        _command->SetPipelineState(_pipeline);
        _command->IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
        for (uint i = 0; i < 6; i++)
            _command->SetGraphicsRootDescriptorTable(i, Device.SrvHeapManager.GetGpuHandle(slot.Ids[i]));
        var parameters = new Vector4(inputs.AoIntensity, inputs.ApplyAo ? 1 : 0, 0, 0);
        _command->SetGraphicsRoot32BitConstants(6, 4, &parameters, 0);
        _command->DrawInstanced(3, 1, 0, 0);
        if (!_weather)
        {
            _transparentOwner = this;
            _transparentSlot = slot;
            _transparentInputs = inputs;
            _transparentDestination = destination;
        }
        return new(true, inputs.ApplyAo);
    }

    internal static void ResetTransparent()
    {
        if (_transparentOwner is { } owner)
        {
            owner._transparentSlot = null;
            owner._transparentInputs = default;
            owner._transparentDestination = null;
        }
        _transparentOwner = null;
    }

    // 仅消费成功合成的同帧快照；绑定不转换深度状态，也不改写共享光照参数。
    internal static bool BindTransparent()
    {
        var owner = _transparentOwner;
        if (owner == null || owner._disposed || owner._transparentSlot is not { } slot
            || Device.ActivePassId != RenderPassId.SceneTransparent
            || Device.GraphicsCommandList != owner._command || Device.FrameIndex != owner._frameIndex
            || Device.GetCurrentRetireFenceValue() != owner._stamp
            || !ReferenceEquals(Device.ActiveTarget, owner._transparentDestination)
            || !ReferenceEquals(Device.ActiveDepthTarget, owner._transparentInputs.Depth)) return false;
        var input = owner._transparentInputs;
        var destination = owner._transparentDestination;
        var integrated = DXTexture3D.Find(input.IntegratedVolume);
        var local = DXTexture3D.Find(input.LocalVolume);
        const ResourceStates read = ResourceStates.PixelShaderResource;
        if (destination == null || destination.Color == null || (nint)destination.Color != slot.Resources[6]
            || destination.CurrentState != ResourceStates.RenderTarget
            || input.Depth is not DXRenderTarget depth || depth.Depth == null || (nint)depth.Depth != slot.Resources[4]
            || depth.DepthCurrentState != ResourceStates.DepthWrite
            || destination.Width != depth.Width || destination.Height != depth.Height
            || input.FrameBuffer is not DXStorageBuffer frame || frame.Resource == null
            || (nint)frame.Resource != slot.Resources[0] || (frame.CurrentState & read) == 0
            || integrated == null || local == null || !integrated.Ready || !local.Ready
            || (nint)integrated.NativeResource != slot.Resources[1] || (nint)local.NativeResource != slot.Resources[2]
            || (integrated.CurrentState & read) == 0 || (local.CurrentState & read) == 0) return false;
        var command = Device.GraphicsCommandList;
        command->SetGraphicsRootShaderResourceView(Pipeline.FogFrameRoot, frame.Resource->GetGPUVirtualAddress());
        command->SetGraphicsRootDescriptorTable(Pipeline.FogIntegratedRoot, Device.SrvHeapManager.GetGpuHandle(slot.Ids[1]));
        command->SetGraphicsRootDescriptorTable(Pipeline.FogLocalRoot, Device.SrvHeapManager.GetGpuHandle(slot.Ids[2]));
        var parameters = new Vector4(1, FrameSchedule.WeatherComposited ? 1 : 0, 0, 0);
        command->SetGraphicsRoot32BitConstants(Pipeline.FogParamsRoot, 4, &parameters, 0);
        return true;
    }

    public override void Dispose()
    {
        if (_disposed) return;
        if (ReferenceEquals(_transparentOwner, this)) ResetTransparent();
        _disposed = true; _destination = null; _prepared = null; _inputs = default;
        nint pipeline = (nint)_pipeline, signature = (nint)_signature;
        _pipeline = null; _signature = null;
        Device.EnqueueDeferredRelease(Device.GetCurrentRetireFenceValue(), () =>
        {
            foreach (var ring in _slots) foreach (var slot in ring) slot.Dispose();
            if (pipeline != 0) ((ID3D12PipelineState*)pipeline)->Release();
            if (signature != 0) ((ID3D12RootSignature*)signature)->Release();
        });
    }
}
