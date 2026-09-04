// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

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
        // 0=exposure 1=bloom 2-3=texelSize 4=ao 5=outlineWidth 6-7=padding
        // (outlinePad, for HLSL packing alignment; color is already carried per
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
    float2 outlinePad;    // HLSL packing: float4 values may not cross 16-byte register boundaries (color is carried per pixel by the mask, so there is no constant color slot)
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
