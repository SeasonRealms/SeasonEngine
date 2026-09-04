// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

/// <summary>
/// Built-in engine compute effect: Bloom downsample chain (2-1 Step B, the first real AfterScene effect).
/// See section 2-1 in the RenderQuality class header for the contract. The effect uses a
/// three-kernel, dual-chain layout shaped by WebGPU's write-only storage constraint:
/// 1. prefilter: extracts soft-threshold highlights from SceneColor (Target input) and downsamples
///    them into half-resolution down[0];
///    2-3 contract clauses 12/13: the input is re-resolved every frame from
///    FrameSchedule.SceneColorOverride (in the TAA tier, use the resolved image; otherwise
///    per-frame highlight aliasing would be amplified into bloom flicker). When null, fall back to
///    SceneColor. This effect does not need to know whether TAA exists.
/// 2. downsample: down[i] -> down[i+1], halving resolution each level
///    (4-tap box, each tap is bilinear and covers about 16 texels);
/// 3. upsample: up[i] = tent3x3(up[i+1]) + down[i]
///    (in-place accumulation is forbidden, so the up chain stays separate from the down chain;
///    the deepest level uses down[N-1] directly as the tent source, and the up chain contains N-1 textures).
/// Each frame records 2N-1 dispatches (11 when N=6), all in the AfterScene phase
/// (after Scene finishes writing, before Post/FinalBlit).
///
/// Output: up[0] (half-resolution rgba16float linear HDR highlights). After successful Initialize,
/// it is written into FrameSchedule.BloomTexture, and the FinalBlit tonemap+bloom variant composes
/// it by name (added in linear space before ACES).
/// Chain textures are created during initialization using the then-current half-resolution
/// backbuffer size, and are not rebuilt on resize (contract clause 2).
///
/// Binding layout (declaration order defines the cross-backend slot contract; see the
/// ComputeBindingType summary; all Params blocks are 16B):
/// prefilter  [0] Params(dstW,dstH,threshold,knee) [1] SampledTexture(SceneColor Target) [2] StorageWrite rgba16f
/// downsample [0] Params(dstW,dstH,srcTexelX,srcTexelY) [1] SampledTexture(down[i]) [2] StorageWrite rgba16f
/// upsample   [0] Params(dstW,dstH,smallTexelX,smallTexelY) [1] SampledTexture(small-chain source) [2] SampledTexture(down[i]) [3] StorageWrite rgba16f
///
/// Step B was visually tuned on D3D12 (HLSL only). Since Step D, full source coverage exists on
/// all four backends (HLSL/GLSL/MSL/WGSL) in this file as a single source of truth, with
/// numerically identical filter weights and soft-threshold formulas.
/// </summary>
public sealed class BloomEffect : ComputeEffect
{
    /// <summary>Registration name prefix for chain textures; each down{i}/up{i} level can be hooked
    /// directly to Sprite2D for debug visualization (following the Plasma precedent).</summary>
    public const string TextureNamePrefix = "compute://bloom/";

    ComputeKernel? _prefilter;
    ComputeKernel? _downsample;
    ComputeKernel? _upsample;

    // Level count and per-level sizes (level i = half resolution >> i), fixed after Initialize
    // and not rebuilt on resize.
    int _levels;
    uint[]? _w;
    uint[]? _h;

    // Resource reference arrays for each dispatch, built during Initialize and reused every frame
    // with zero allocation. They contain reference types, so stackalloc is not allowed;
    // see the ComputeDispatchArgs summary.
    ComputeResourceRef[]? _prefilterRes;
    ComputeResourceRef[][]? _downRes;
    ComputeResourceRef[][]? _upRes;

    string? _outputName;

    public override string Name => "bloom";

    public override ComputePhase Phase => ComputePhase.AfterScene;

    static string DownName(int i) => TextureNamePrefix + "down" + i;

    static string UpName(int i) => TextureNamePrefix + "up" + i;

    public override bool Initialize(IGraphics g)
    {
        // Tier gating + shape dependency: requires the HDR offscreen path
        // (threshold semantics do not hold in LDR/direct-render mode, so registration degrades cleanly).
        if (!RenderQuality.Current.BloomEnabled)
            return false;
        if (FrameSchedule.SceneColor == null || FrameSchedule.SceneColor.Desc.ColorFormat != RtFormat.Rgba16Float)
            return false;

        // Continue only if all three kernels compile successfully
        // (any failure reclaims the created handles and leaves no residue).
        _prefilter = g.CreateComputeKernel(new ComputeKernelDesc
        {
            Name = "bloomPrefilter",
            Source = new ShaderSourceSet
            {
                Hlsl = SourcePrefilterHlsl,
                Glsl = SourcePrefilterGlsl,
                Msl = SourcePrefilterMsl,
                Wgsl = SourcePrefilterWgsl,
                EntryPoint = "CSMain",
            },
            Bindings = new[]
            {
                new ComputeBindingDesc { Type = ComputeBindingType.Params, SizeInBytes = 16 },
                new ComputeBindingDesc { Type = ComputeBindingType.SampledTexture },
                new ComputeBindingDesc { Type = ComputeBindingType.StorageTextureWrite, StorageFormat = ComputeStorageFormat.Rgba16Float },
            },
        });
        _downsample = g.CreateComputeKernel(new ComputeKernelDesc
        {
            Name = "bloomDownsample",
            Source = new ShaderSourceSet
            {
                Hlsl = SourceDownsampleHlsl,
                Glsl = SourceDownsampleGlsl,
                Msl = SourceDownsampleMsl,
                Wgsl = SourceDownsampleWgsl,
                EntryPoint = "CSMain",
            },
            Bindings = new[]
            {
                new ComputeBindingDesc { Type = ComputeBindingType.Params, SizeInBytes = 16 },
                new ComputeBindingDesc { Type = ComputeBindingType.SampledTexture },
                new ComputeBindingDesc { Type = ComputeBindingType.StorageTextureWrite, StorageFormat = ComputeStorageFormat.Rgba16Float },
            },
        });
        _upsample = g.CreateComputeKernel(new ComputeKernelDesc
        {
            Name = "bloomUpsample",
            Source = new ShaderSourceSet
            {
                Hlsl = SourceUpsampleHlsl,
                Glsl = SourceUpsampleGlsl,
                Msl = SourceUpsampleMsl,
                Wgsl = SourceUpsampleWgsl,
                EntryPoint = "CSMain",
            },
            Bindings = new[]
            {
                new ComputeBindingDesc { Type = ComputeBindingType.Params, SizeInBytes = 16 },
                new ComputeBindingDesc { Type = ComputeBindingType.SampledTexture },
                new ComputeBindingDesc { Type = ComputeBindingType.SampledTexture },
                new ComputeBindingDesc { Type = ComputeBindingType.StorageTextureWrite, StorageFormat = ComputeStorageFormat.Rgba16Float },
            },
        });
        if (_prefilter == null || _downsample == null || _upsample == null)
        {
            Dispose();
            return false;
        }

        // Chain sizes: level 0 is half of the current backbuffer resolution, then halves each level;
        // clamp the level count so the minimum dimension stays >= 8 (contract clause 2).
        var res = DeviceServices.BaseApp.DeviceResolution;
        uint w0 = Math.Max(8u, (uint)res.X / 2);
        uint h0 = Math.Max(8u, (uint)res.Y / 2);
        _levels = 1;
        while (_levels < RenderQuality.Current.BloomMipCount && Math.Min(w0 >> _levels, h0 >> _levels) >= 8)
            _levels++;

        _w = new uint[_levels];
        _h = new uint[_levels];
        for (int i = 0; i < _levels; i++)
        {
            _w[i] = w0 >> i;
            _h[i] = h0 >> i;
            g.CreateComputeTexture(DownName(i), _w[i], _h[i], ComputeStorageFormat.Rgba16Float);
            if (i < _levels - 1)
                g.CreateComputeTexture(UpName(i), _w[i], _h[i], ComputeStorageFormat.Rgba16Float);
        }

        // Slot 0 is re-resolved each frame by Record (2-3 clause 13); initialize it with a seed value here.
        _prefilterRes = new ComputeResourceRef[] { FrameSchedule.SceneColor, DownName(0) };

        _downRes = new ComputeResourceRef[_levels - 1][];
        _upRes = new ComputeResourceRef[_levels - 1][];
        for (int i = 0; i < _levels - 1; i++)
        {
            _downRes[i] = new ComputeResourceRef[] { DownName(i), DownName(i + 1) };
            // Tent source for up[i]: use down[N-1] at the deepest level, otherwise use up[i+1].
            string small = i == _levels - 2 ? DownName(_levels - 1) : UpName(i + 1);
            _upRes[i] = new ComputeResourceRef[] { small, DownName(i), UpName(i) };
        }

        // Single-level shape (extremely small backbuffer): no up chain, composition reads down[0] directly.
        _outputName = _levels >= 2 ? UpName(0) : DownName(0);
        FrameSchedule.BloomTexture = _outputName;
        return true;
    }

    public override void OnResize(IGraphics g)
    {
        // Recompute the chain level count and per-level sizes
        // (level 0 = half resolution, then halve per level; keep minimum dimension >= 8).
        var res = DeviceServices.BaseApp.DeviceResolution;
        uint w0 = Math.Max(8u, (uint)res.X / 2);
        uint h0 = Math.Max(8u, (uint)res.Y / 2);
        int newLevels = 1;
        while (newLevels < RenderQuality.Current.BloomMipCount && Math.Min(w0 >> newLevels, h0 >> newLevels) >= 8)
            newLevels++;

        _levels = newLevels;
        _w = new uint[_levels];
        _h = new uint[_levels];
        for (int i = 0; i < _levels; i++)
        {
            _w[i] = w0 >> i;
            _h[i] = h0 >> i;
            g.CreateComputeTexture(DownName(i), _w[i], _h[i], ComputeStorageFormat.Rgba16Float);
            if (i < _levels - 1)
                g.CreateComputeTexture(UpName(i), _w[i], _h[i], ComputeStorageFormat.Rgba16Float);
        }

        // Rebuild resource reference arrays because the number of levels may have changed.
        _prefilterRes = new ComputeResourceRef[] { FrameSchedule.SceneColor!, DownName(0) };
        _downRes = new ComputeResourceRef[_levels - 1][];
        _upRes = new ComputeResourceRef[_levels - 1][];
        for (int i = 0; i < _levels - 1; i++)
        {
            _downRes[i] = new ComputeResourceRef[] { DownName(i), DownName(i + 1) };
            string small = i == _levels - 2 ? DownName(_levels - 1) : UpName(i + 1);
            _upRes[i] = new ComputeResourceRef[] { small, DownName(i), UpName(i) };
        }
        _outputName = _levels >= 2 ? UpName(0) : DownName(0);
        FrameSchedule.BloomTexture = _outputName;
    }

    public override void Record(IGraphics g)
    {
        Span<float> p = stackalloc float[4];

        // 2-3 contract clause 13: rewrite the cached resource slot in place
        // (ComputeResourceRef is a value type, so this stays allocation-free).
        // TaaEffect registers earlier than this effect, so SceneColorOverride for the current frame
        // has already been published by its Record call.
        if (FrameSchedule.SceneColorOverride is string sceneOverride)
            _prefilterRes![0] = sceneOverride;
        else
            _prefilterRes![0] = FrameSchedule.SceneColor!;

        // 1) prefilter: scene source -> down[0]
        // (soft-threshold highlights, with threshold/knee runtime knobs uploaded every frame).
        p[0] = _w![0];
        p[1] = _h![0];
        p[2] = RenderQuality.Current.BloomThreshold;
        p[3] = RenderQuality.Current.BloomKnee;
        g.DispatchCompute(new ComputeDispatchArgs
        {
            Kernel = _prefilter!,
            Params = MemoryMarshal.AsBytes(p),
            Resources = _prefilterRes,
            GroupsX = (_w[0] + 7) / 8,
            GroupsY = (_h[0] + 7) / 8,
            GroupsZ = 1,
        });

        // 2) down chain: down[i] -> down[i+1] (srcTexel provides offsets for the 4-tap box filter).
        for (int i = 0; i < _levels - 1; i++)
        {
            p[0] = _w[i + 1];
            p[1] = _h[i + 1];
            p[2] = 1f / _w[i];
            p[3] = 1f / _h[i];
            g.DispatchCompute(new ComputeDispatchArgs
            {
                Kernel = _downsample!,
                Params = MemoryMarshal.AsBytes(p),
                Resources = _downRes![i],
                GroupsX = (_w[i + 1] + 7) / 8,
                GroupsY = (_h[i + 1] + 7) / 8,
                GroupsZ = 1,
            });
        }

        // 3) up chain (deep -> shallow): up[i] = tent3x3(small-chain source) + down[i]
        // (smallTexel provides offsets for the tent filter).
        for (int i = _levels - 2; i >= 0; i--)
        {
            p[0] = _w[i];
            p[1] = _h[i];
            p[2] = 1f / _w[i + 1];
            p[3] = 1f / _h[i + 1];
            g.DispatchCompute(new ComputeDispatchArgs
            {
                Kernel = _upsample!,
                Params = MemoryMarshal.AsBytes(p),
                Resources = _upRes![i],
                GroupsX = (_w[i] + 7) / 8,
                GroupsY = (_h[i] + 7) / 8,
                GroupsZ = 1,
            });
        }
    }

    public void Dispose()
    {
        if (_outputName != null && FrameSchedule.BloomTexture == _outputName)
            FrameSchedule.BloomTexture = null;
        _outputName = null;
        _prefilter?.Dispose();
        _prefilter = null;
        _downsample?.Dispose();
        _downsample = null;
        _upsample?.Dispose();
        _upsample = null;
    }

    // ── Shader sources (single source of truth; slots follow the binding layout contract in the
    //    class header; workgroup is fixed at 8x8x1; all shaders keep a single exit to avoid
    //    fxc X4000; filter weights and the soft-threshold formula are contract constants shared
    //    across all four backends and ported verbatim for alignment) ──

    /// <summary>prefilter: single-tap bilinear sampling (exactly a 2x2 box at half resolution)
    /// plus a soft threshold (threshold+knee knee-shaped transition).
    /// The output preserves linear HDR highlights without compression or encoding, and brightness is
    /// scaled together with the main path exposure in FinalBlit.</summary>
    const string SourcePrefilterHlsl = @"
cbuffer BloomParams : register(b0)
{
    float uDstWidth;
    float uDstHeight;
    float uThreshold;
    float uKnee;
};

Texture2D<float4> uScene : register(t0);
SamplerState uLinearClamp : register(s0);
RWTexture2D<float4> uOutput : register(u0);

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x < (uint)uDstWidth && id.y < (uint)uDstHeight)
    {
        float2 uv = (float2(id.xy) + 0.5) / float2(uDstWidth, uDstHeight);
        float3 c = uScene.SampleLevel(uLinearClamp, uv, 0.0).rgb;

        // Soft threshold (Unity/Karis style): quadratic transition in the knee region,
        // linearly fading to 0 outside the knee.
        float br = max(c.r, max(c.g, c.b));
        float soft = clamp(br - uThreshold + uKnee, 0.0, 2.0 * uKnee);
        soft = soft * soft / (4.0 * uKnee + 1e-4);
        float contribution = max(soft, br - uThreshold) / max(br, 1e-4);

        uOutput[id.xy] = float4(c * max(contribution, 0.0), 1.0);
    }
}
";

    /// <summary>Vulkan GLSL 450 (compiled to SPIR-V at runtime by glslang; entry point is always main).</summary>
    const string SourcePrefilterGlsl = @"#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(push_constant) uniform BloomParams
{
    float uDstWidth;
    float uDstHeight;
    float uThreshold;
    float uKnee;
};

layout(binding = 1) uniform sampler2D uScene;
layout(binding = 2, rgba16f) uniform writeonly image2D uOutput;

void main()
{
    uvec2 id = gl_GlobalInvocationID.xy;
    if (id.x < uint(uDstWidth) && id.y < uint(uDstHeight))
    {
        vec2 uv = (vec2(id) + 0.5) / vec2(uDstWidth, uDstHeight);
        vec3 c = textureLod(uScene, uv, 0.0).rgb;

        float br = max(c.r, max(c.g, c.b));
        float soft = clamp(br - uThreshold + uKnee, 0.0, 2.0 * uKnee);
        soft = soft * soft / (4.0 * uKnee + 1e-4);
        float contribution = max(soft, br - uThreshold) / max(br, 1e-4);

        imageStore(uOutput, ivec2(id), vec4(c * max(contribution, 0.0), 1.0));
    }
}
";

    /// <summary>Metal MSL kernel。</summary>
    const string SourcePrefilterMsl = @"
#include <metal_stdlib>
using namespace metal;

struct BloomParams
{
    float uDstWidth;
    float uDstHeight;
    float uThreshold;
    float uKnee;
};

kernel void CSMain(
    constant BloomParams& params [[buffer(0)]],
    texture2d<float, access::sample> uScene [[texture(0)]],
    texture2d<float, access::write> uOutput [[texture(1)]],
    sampler uLinearClamp [[sampler(0)]],
    uint2 id [[thread_position_in_grid]])
{
    if (id.x < (uint)params.uDstWidth && id.y < (uint)params.uDstHeight)
    {
        float2 uv = (float2(id) + 0.5) / float2(params.uDstWidth, params.uDstHeight);
        float3 c = uScene.sample(uLinearClamp, uv, level(0.0)).rgb;

        float br = max(c.r, max(c.g, c.b));
        float soft = clamp(br - params.uThreshold + params.uKnee, 0.0, 2.0 * params.uKnee);
        soft = soft * soft / (4.0 * params.uKnee + 1e-4);
        float contribution = max(soft, br - params.uThreshold) / max(br, 1e-4);

        uOutput.write(float4(c * max(contribution, 0.0), 1.0), id);
    }
}
";

    /// <summary>WebGPU WGSL (submitted through interop; seasonWebGPU.js does not embed the source).</summary>
    const string SourcePrefilterWgsl = @"
struct BloomParams
{
    uDstWidth : f32,
    uDstHeight : f32,
    uThreshold : f32,
    uKnee : f32,
};

@group(0) @binding(0) var<uniform> params : BloomParams;
@group(0) @binding(1) var uScene : texture_2d<f32>;
@group(0) @binding(2) var uOutput : texture_storage_2d<rgba16float, write>;
@group(0) @binding(15) var uLinearClamp : sampler;

@compute @workgroup_size(8, 8, 1)
fn CSMain(@builtin(global_invocation_id) id : vec3<u32>)
{
    if (id.x < u32(params.uDstWidth) && id.y < u32(params.uDstHeight))
    {
        let uv = (vec2<f32>(f32(id.x), f32(id.y)) + vec2<f32>(0.5)) / vec2<f32>(params.uDstWidth, params.uDstHeight);
        let c = textureSampleLevel(uScene, uLinearClamp, uv, 0.0).rgb;

        let br = max(c.r, max(c.g, c.b));
        var soft = clamp(br - params.uThreshold + params.uKnee, 0.0, 2.0 * params.uKnee);
        soft = soft * soft / (4.0 * params.uKnee + 1e-4);
        let contribution = max(soft, br - params.uThreshold) / max(br, 1e-4);

        textureStore(uOutput, vec2<i32>(i32(id.x), i32(id.y)), vec4<f32>(c * max(contribution, 0.0), 1.0));
    }
}
";

    /// <summary>downsample: 4-tap box filter (taps sit on source diagonals at +-1 texel; each tap is
    /// bilinear and covers about 16 texels), giving better anti-flicker behavior than a single tap
    /// in a bloom chain that amplifies high-frequency shimmer level by level.</summary>
    const string SourceDownsampleHlsl = @"
cbuffer BloomParams : register(b0)
{
    float uDstWidth;
    float uDstHeight;
    float uSrcTexelX;
    float uSrcTexelY;
};

Texture2D<float4> uSrc : register(t0);
SamplerState uLinearClamp : register(s0);
RWTexture2D<float4> uOutput : register(u0);

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x < (uint)uDstWidth && id.y < (uint)uDstHeight)
    {
        float2 uv = (float2(id.xy) + 0.5) / float2(uDstWidth, uDstHeight);
        float2 o = float2(uSrcTexelX, uSrcTexelY);

        float3 c = uSrc.SampleLevel(uLinearClamp, uv + float2(-o.x, -o.y), 0.0).rgb
                 + uSrc.SampleLevel(uLinearClamp, uv + float2( o.x, -o.y), 0.0).rgb
                 + uSrc.SampleLevel(uLinearClamp, uv + float2(-o.x,  o.y), 0.0).rgb
                 + uSrc.SampleLevel(uLinearClamp, uv + float2( o.x,  o.y), 0.0).rgb;

        uOutput[id.xy] = float4(c * 0.25, 1.0);
    }
}
";

    /// <summary>Vulkan GLSL 450 (compiled to SPIR-V at runtime by glslang; entry point is always main).</summary>
    const string SourceDownsampleGlsl = @"#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(push_constant) uniform BloomParams
{
    float uDstWidth;
    float uDstHeight;
    float uSrcTexelX;
    float uSrcTexelY;
};

layout(binding = 1) uniform sampler2D uSrc;
layout(binding = 2, rgba16f) uniform writeonly image2D uOutput;

void main()
{
    uvec2 id = gl_GlobalInvocationID.xy;
    if (id.x < uint(uDstWidth) && id.y < uint(uDstHeight))
    {
        vec2 uv = (vec2(id) + 0.5) / vec2(uDstWidth, uDstHeight);
        vec2 o = vec2(uSrcTexelX, uSrcTexelY);

        vec3 c = textureLod(uSrc, uv + vec2(-o.x, -o.y), 0.0).rgb
               + textureLod(uSrc, uv + vec2( o.x, -o.y), 0.0).rgb
               + textureLod(uSrc, uv + vec2(-o.x,  o.y), 0.0).rgb
               + textureLod(uSrc, uv + vec2( o.x,  o.y), 0.0).rgb;

        imageStore(uOutput, ivec2(id), vec4(c * 0.25, 1.0));
    }
}
";

    /// <summary>Metal MSL kernel。</summary>
    const string SourceDownsampleMsl = @"
#include <metal_stdlib>
using namespace metal;

struct BloomParams
{
    float uDstWidth;
    float uDstHeight;
    float uSrcTexelX;
    float uSrcTexelY;
};

kernel void CSMain(
    constant BloomParams& params [[buffer(0)]],
    texture2d<float, access::sample> uSrc [[texture(0)]],
    texture2d<float, access::write> uOutput [[texture(1)]],
    sampler uLinearClamp [[sampler(0)]],
    uint2 id [[thread_position_in_grid]])
{
    if (id.x < (uint)params.uDstWidth && id.y < (uint)params.uDstHeight)
    {
        float2 uv = (float2(id) + 0.5) / float2(params.uDstWidth, params.uDstHeight);
        float2 o = float2(params.uSrcTexelX, params.uSrcTexelY);

        float3 c = uSrc.sample(uLinearClamp, uv + float2(-o.x, -o.y), level(0.0)).rgb
                 + uSrc.sample(uLinearClamp, uv + float2( o.x, -o.y), level(0.0)).rgb
                 + uSrc.sample(uLinearClamp, uv + float2(-o.x,  o.y), level(0.0)).rgb
                 + uSrc.sample(uLinearClamp, uv + float2( o.x,  o.y), level(0.0)).rgb;

        uOutput.write(float4(c * 0.25, 1.0), id);
    }
}
";

    /// <summary>WebGPU WGSL (submitted through interop; seasonWebGPU.js does not embed the source).</summary>
    const string SourceDownsampleWgsl = @"
struct BloomParams
{
    uDstWidth : f32,
    uDstHeight : f32,
    uSrcTexelX : f32,
    uSrcTexelY : f32,
};

@group(0) @binding(0) var<uniform> params : BloomParams;
@group(0) @binding(1) var uSrc : texture_2d<f32>;
@group(0) @binding(2) var uOutput : texture_storage_2d<rgba16float, write>;
@group(0) @binding(15) var uLinearClamp : sampler;

@compute @workgroup_size(8, 8, 1)
fn CSMain(@builtin(global_invocation_id) id : vec3<u32>)
{
    if (id.x < u32(params.uDstWidth) && id.y < u32(params.uDstHeight))
    {
        let uv = (vec2<f32>(f32(id.x), f32(id.y)) + vec2<f32>(0.5)) / vec2<f32>(params.uDstWidth, params.uDstHeight);
        let o = vec2<f32>(params.uSrcTexelX, params.uSrcTexelY);

        let c = textureSampleLevel(uSrc, uLinearClamp, uv + vec2<f32>(-o.x, -o.y), 0.0).rgb
              + textureSampleLevel(uSrc, uLinearClamp, uv + vec2<f32>( o.x, -o.y), 0.0).rgb
              + textureSampleLevel(uSrc, uLinearClamp, uv + vec2<f32>(-o.x,  o.y), 0.0).rgb
              + textureSampleLevel(uSrc, uLinearClamp, uv + vec2<f32>( o.x,  o.y), 0.0).rgb;

        textureStore(uOutput, vec2<i32>(i32(id.x), i32(id.y)), vec4<f32>(c * 0.25, 1.0));
    }
}
";

    /// <summary>upsample: upsample the small-chain source with a 3x3 tent filter
    /// (1/2/1 weights, offsets taken from the small-texture texel size), then add the matching down level.
    /// The dual-chain design keeps the write target up[i] different from both sampled inputs,
    /// satisfying WebGPU's write-only storage constraint.</summary>
    const string SourceUpsampleHlsl = @"
cbuffer BloomParams : register(b0)
{
    float uDstWidth;
    float uDstHeight;
    float uSmallTexelX;
    float uSmallTexelY;
};

Texture2D<float4> uSmall : register(t0);
Texture2D<float4> uSame : register(t1);
SamplerState uLinearClamp : register(s0);
RWTexture2D<float4> uOutput : register(u0);

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x < (uint)uDstWidth && id.y < (uint)uDstHeight)
    {
        float2 uv = (float2(id.xy) + 0.5) / float2(uDstWidth, uDstHeight);
        float2 o = float2(uSmallTexelX, uSmallTexelY);

        float3 tent =
              uSmall.SampleLevel(uLinearClamp, uv + float2(-o.x, -o.y), 0.0).rgb
            + uSmall.SampleLevel(uLinearClamp, uv + float2( 0.0, -o.y), 0.0).rgb * 2.0
            + uSmall.SampleLevel(uLinearClamp, uv + float2( o.x, -o.y), 0.0).rgb
            + uSmall.SampleLevel(uLinearClamp, uv + float2(-o.x,  0.0), 0.0).rgb * 2.0
            + uSmall.SampleLevel(uLinearClamp, uv, 0.0).rgb * 4.0
            + uSmall.SampleLevel(uLinearClamp, uv + float2( o.x,  0.0), 0.0).rgb * 2.0
            + uSmall.SampleLevel(uLinearClamp, uv + float2(-o.x,  o.y), 0.0).rgb
            + uSmall.SampleLevel(uLinearClamp, uv + float2( 0.0,  o.y), 0.0).rgb * 2.0
            + uSmall.SampleLevel(uLinearClamp, uv + float2( o.x,  o.y), 0.0).rgb;

        float3 c = tent * (1.0 / 16.0) + uSame.SampleLevel(uLinearClamp, uv, 0.0).rgb;

        uOutput[id.xy] = float4(c, 1.0);
    }
}
";

    /// <summary>Vulkan GLSL 450 (compiled to SPIR-V at runtime by glslang; entry point is always main).</summary>
    const string SourceUpsampleGlsl = @"#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(push_constant) uniform BloomParams
{
    float uDstWidth;
    float uDstHeight;
    float uSmallTexelX;
    float uSmallTexelY;
};

layout(binding = 1) uniform sampler2D uSmall;
layout(binding = 2) uniform sampler2D uSame;
layout(binding = 3, rgba16f) uniform writeonly image2D uOutput;

void main()
{
    uvec2 id = gl_GlobalInvocationID.xy;
    if (id.x < uint(uDstWidth) && id.y < uint(uDstHeight))
    {
        vec2 uv = (vec2(id) + 0.5) / vec2(uDstWidth, uDstHeight);
        vec2 o = vec2(uSmallTexelX, uSmallTexelY);

        vec3 tent =
              textureLod(uSmall, uv + vec2(-o.x, -o.y), 0.0).rgb
            + textureLod(uSmall, uv + vec2( 0.0, -o.y), 0.0).rgb * 2.0
            + textureLod(uSmall, uv + vec2( o.x, -o.y), 0.0).rgb
            + textureLod(uSmall, uv + vec2(-o.x,  0.0), 0.0).rgb * 2.0
            + textureLod(uSmall, uv, 0.0).rgb * 4.0
            + textureLod(uSmall, uv + vec2( o.x,  0.0), 0.0).rgb * 2.0
            + textureLod(uSmall, uv + vec2(-o.x,  o.y), 0.0).rgb
            + textureLod(uSmall, uv + vec2( 0.0,  o.y), 0.0).rgb * 2.0
            + textureLod(uSmall, uv + vec2( o.x,  o.y), 0.0).rgb;

        vec3 c = tent * (1.0 / 16.0) + textureLod(uSame, uv, 0.0).rgb;

        imageStore(uOutput, ivec2(id), vec4(c, 1.0));
    }
}
";

    /// <summary>Metal MSL kernel。</summary>
    const string SourceUpsampleMsl = @"
#include <metal_stdlib>
using namespace metal;

struct BloomParams
{
    float uDstWidth;
    float uDstHeight;
    float uSmallTexelX;
    float uSmallTexelY;
};

kernel void CSMain(
    constant BloomParams& params [[buffer(0)]],
    texture2d<float, access::sample> uSmall [[texture(0)]],
    texture2d<float, access::sample> uSame [[texture(1)]],
    texture2d<float, access::write> uOutput [[texture(2)]],
    sampler uLinearClamp [[sampler(0)]],
    uint2 id [[thread_position_in_grid]])
{
    if (id.x < (uint)params.uDstWidth && id.y < (uint)params.uDstHeight)
    {
        float2 uv = (float2(id) + 0.5) / float2(params.uDstWidth, params.uDstHeight);
        float2 o = float2(params.uSmallTexelX, params.uSmallTexelY);

        float3 tent =
              uSmall.sample(uLinearClamp, uv + float2(-o.x, -o.y), level(0.0)).rgb
            + uSmall.sample(uLinearClamp, uv + float2( 0.0, -o.y), level(0.0)).rgb * 2.0
            + uSmall.sample(uLinearClamp, uv + float2( o.x, -o.y), level(0.0)).rgb
            + uSmall.sample(uLinearClamp, uv + float2(-o.x,  0.0), level(0.0)).rgb * 2.0
            + uSmall.sample(uLinearClamp, uv, level(0.0)).rgb * 4.0
            + uSmall.sample(uLinearClamp, uv + float2( o.x,  0.0), level(0.0)).rgb * 2.0
            + uSmall.sample(uLinearClamp, uv + float2(-o.x,  o.y), level(0.0)).rgb
            + uSmall.sample(uLinearClamp, uv + float2( 0.0,  o.y), level(0.0)).rgb * 2.0
            + uSmall.sample(uLinearClamp, uv + float2( o.x,  o.y), level(0.0)).rgb;

        float3 c = tent * (1.0 / 16.0) + uSame.sample(uLinearClamp, uv, level(0.0)).rgb;

        uOutput.write(float4(c, 1.0), id);
    }
}
";

    /// <summary>WebGPU WGSL (submitted through interop; seasonWebGPU.js does not embed the source).</summary>
    const string SourceUpsampleWgsl = @"
struct BloomParams
{
    uDstWidth : f32,
    uDstHeight : f32,
    uSmallTexelX : f32,
    uSmallTexelY : f32,
};

@group(0) @binding(0) var<uniform> params : BloomParams;
@group(0) @binding(1) var uSmall : texture_2d<f32>;
@group(0) @binding(2) var uSame : texture_2d<f32>;
@group(0) @binding(3) var uOutput : texture_storage_2d<rgba16float, write>;
@group(0) @binding(15) var uLinearClamp : sampler;

@compute @workgroup_size(8, 8, 1)
fn CSMain(@builtin(global_invocation_id) id : vec3<u32>)
{
    if (id.x < u32(params.uDstWidth) && id.y < u32(params.uDstHeight))
    {
        let uv = (vec2<f32>(f32(id.x), f32(id.y)) + vec2<f32>(0.5)) / vec2<f32>(params.uDstWidth, params.uDstHeight);
        let o = vec2<f32>(params.uSmallTexelX, params.uSmallTexelY);

        let tent =
              textureSampleLevel(uSmall, uLinearClamp, uv + vec2<f32>(-o.x, -o.y), 0.0).rgb
            + textureSampleLevel(uSmall, uLinearClamp, uv + vec2<f32>( 0.0, -o.y), 0.0).rgb * 2.0
            + textureSampleLevel(uSmall, uLinearClamp, uv + vec2<f32>( o.x, -o.y), 0.0).rgb
            + textureSampleLevel(uSmall, uLinearClamp, uv + vec2<f32>(-o.x,  0.0), 0.0).rgb * 2.0
            + textureSampleLevel(uSmall, uLinearClamp, uv, 0.0).rgb * 4.0
            + textureSampleLevel(uSmall, uLinearClamp, uv + vec2<f32>( o.x,  0.0), 0.0).rgb * 2.0
            + textureSampleLevel(uSmall, uLinearClamp, uv + vec2<f32>(-o.x,  o.y), 0.0).rgb
            + textureSampleLevel(uSmall, uLinearClamp, uv + vec2<f32>( 0.0,  o.y), 0.0).rgb * 2.0
            + textureSampleLevel(uSmall, uLinearClamp, uv + vec2<f32>( o.x,  o.y), 0.0).rgb;

        let c = tent * (1.0 / 16.0) + textureSampleLevel(uSame, uLinearClamp, uv, 0.0).rgb;

        textureStore(uOutput, vec2<i32>(i32(id.x), i32(id.y)), vec4<f32>(c, 1.0));
    }
}
";
}
