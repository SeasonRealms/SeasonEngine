// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

/// <summary>
/// Engine built-in compute effect: scene-normal visualization (A0.2 first acceptance case, the SceneNormal counterpart of
/// <see cref="VelocityViewEffect"/>). It validates the normal path in one shot:
/// 1. RtFormat.Rgba16Float SceneNormal is created next to SceneVelocity and becomes MRT slot 2 of the Scene pass.
/// 2. The main shader NORMAL_OUTPUT variant writes the octahedral world normal after the normal map and Toksvig.
/// 3. Transparent geometry and particles leave slot 2 untouched (write mask 0).
///
/// Behavior: each AfterScene phase downsamples the full-resolution SceneNormal into a fixed 480x270 storage texture with
/// nearest sampling. <see cref="Mode"/> 0 shows the decoded world normal as n * 0.5 + 0.5; 1 shows roughness in red and
/// metallic in green. Sprite2D consumes it directly by name.
///
/// Acceptance criteria (thumbnail appearance):
/// - Flat ground -> one uniform color (+Y up reads as (0.5, 1, 0.5), light green).
/// - Rotating the camera -> colors stay attached to surfaces; they are world-space, not view-space.
/// - Normal-mapped surfaces -> fine detail on top of the face color, not a flat face color.
/// - Sky / uncovered pixels -> black. The target clears to zero and xy = 0 is a legal code (+Z), so this view treats an
///   all-zero texel as no surface; a real +Z normal with roughness 0 and metallic 0 is shown black as well, which is
///   acceptable for a debug view. Consumers that must tell the two apart test SceneDepth instead.
/// - Transparent geometry -> invisible here (slot 2 write mask is 0).
///
/// Binding layout (same convention as VelocityViewEffect):
/// [0] Params 16B (dstWidth, dstHeight, mode, _pad) -> HLSL b0
/// [1] SampledTexture (SceneNormal target) -> HLSL t0 + s0 (this kernel uses Load, not sampling)
/// [2] StorageTextureWrite rgba8unorm -> HLSL u0
/// </summary>
public sealed class NormalViewEffect : ComputeEffect
{
    /// <summary>Registered output texture name in the platform texture dictionary (used directly by Sprite2D.Name).</summary>
    public const string TextureName = "compute://normalview";

    public const uint Width = 480;

    public const uint Height = 270;

    /// <summary>0 = decoded world normal, 1 = roughness (R) / metallic (G). Read every frame.</summary>
    public static int Mode;

    ComputeKernel? _kernel;

    // Same reasoning as VelocityViewEffect: built once, reused every frame, and SceneNormal is recreated in place on resize.
    ComputeResourceRef[]? _resources;

    public override string Name => "normalView";

    public override ComputePhase Phase => ComputePhase.AfterScene;

    public override bool Initialize(IGraphics g)
    {
        // SceneNormal only exists when EffectiveSceneNormals survived initialization on this backend; otherwise stay inactive.
        if (!RenderQuality.Current.EffectiveSceneNormals || FrameSchedule.SceneNormal == null)
            return false;

        _kernel = g.CreateComputeKernel(new ComputeKernelDesc
        {
            Name = "normalView",
            Source = new ShaderSourceSet
            {
                Hlsl = SourceHlsl,
                Glsl = SourceGlsl,
                Msl = SourceMsl,
                Wgsl = SourceWgsl,
                EntryPoint = "CSMain",
            },
            Bindings = new[]
            {
                new ComputeBindingDesc { Type = ComputeBindingType.Params, SizeInBytes = 16 },
                new ComputeBindingDesc { Type = ComputeBindingType.SampledTexture },
                new ComputeBindingDesc
                {
                    Type = ComputeBindingType.StorageTextureWrite,
                    StorageFormat = ComputeStorageFormat.Rgba8Unorm,
                },
            },
        });
        if (_kernel == null) return false;

        g.CreateComputeTexture(TextureName, Width, Height);
        _resources = new ComputeResourceRef[] { FrameSchedule.SceneNormal, TextureName };
        return true;
    }

    public override void Record(IGraphics g)
    {
        Span<float> p = stackalloc float[4];
        p[0] = Width;
        p[1] = Height;
        p[2] = Mode;
        p[3] = 0f;

        g.DispatchCompute(new ComputeDispatchArgs
        {
            Kernel = _kernel!,
            Params = MemoryMarshal.AsBytes(p),
            Resources = _resources,
            GroupsX = (Width + 7) / 8,
            GroupsY = (Height + 7) / 8,
            GroupsZ = 1,
        });
    }

    public void Dispose()
    {
        _kernel?.Dispose();
        _kernel = null;
    }

    // Shader sources (single source of truth; slots follow the class-level binding convention; workgroup is fixed at 8x8x1).
    //
    // OctDecodeNormal is the exact inverse of OctEncodeNormal in the four main shaders:
    // n = (e, 1 - |e.x| - |e.y|); t = saturate(-n.z); n.xy += (n.xy >= 0 ? -t : t); normalize(n).
    // Reads use per-texel Load / texelFetch so the downsample never blends normals of adjacent surfaces.

    /// <summary>D3D12 cs_5_0 (fxc; single exit avoids X4000).</summary>
    const string SourceHlsl = @"
cbuffer NormalViewParams : register(b0)
{
    float uDstWidth;
    float uDstHeight;
    float uMode;
    float uPad;
};

Texture2D<float4> uNormal : register(t0);
SamplerState uNormalSampler : register(s0);
RWTexture2D<float4> uOutput : register(u0);

float3 OctDecodeNormal(float2 e)
{
    float3 n = float3(e, 1.0 - abs(e.x) - abs(e.y));
    float t = saturate(-n.z);
    n.xy += (n.xy >= 0.0) ? float2(-t, -t) : float2(t, t);
    return normalize(n);
}

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x < (uint)uDstWidth && id.y < (uint)uDstHeight)
    {
        uint w, h;
        uNormal.GetDimensions(w, h);
        uint2 src = uint2((float2(id.xy) + 0.5) / float2(uDstWidth, uDstHeight) * float2(w, h));
        float4 e = uNormal.Load(int3(src, 0));
        float3 c = float3(0.0, 0.0, 0.0);
        if (dot(abs(e), float4(1.0, 1.0, 1.0, 1.0)) > 0.0)
            c = uMode < 0.5 ? OctDecodeNormal(e.xy) * 0.5 + 0.5 : float3(saturate(e.z), saturate(e.w), 0.0);
        uOutput[id.xy] = float4(c, 1.0);
    }
}
";

    /// <summary>Vulkan GLSL 450 (glslang compiles to SPIR-V at runtime; entry point is always main).</summary>
    const string SourceGlsl = @"#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(push_constant) uniform NormalViewParams
{
    float uDstWidth;
    float uDstHeight;
    float uMode;
    float uPad;
};

layout(binding = 1) uniform sampler2D uNormal;
layout(binding = 2, rgba8) uniform writeonly image2D uOutput;

vec3 OctDecodeNormal(vec2 e)
{
    vec3 n = vec3(e, 1.0 - abs(e.x) - abs(e.y));
    float t = clamp(-n.z, 0.0, 1.0);
    n.xy += mix(vec2(t), vec2(-t), greaterThanEqual(n.xy, vec2(0.0)));
    return normalize(n);
}

void main()
{
    uvec2 id = gl_GlobalInvocationID.xy;
    if (id.x < uint(uDstWidth) && id.y < uint(uDstHeight))
    {
        ivec2 dim = textureSize(uNormal, 0);
        ivec2 src = ivec2((vec2(id) + 0.5) / vec2(uDstWidth, uDstHeight) * vec2(dim));
        vec4 e = texelFetch(uNormal, src, 0);
        vec3 c = vec3(0.0);
        if (dot(abs(e), vec4(1.0)) > 0.0)
            c = uMode < 0.5 ? OctDecodeNormal(e.xy) * 0.5 + 0.5 : vec3(clamp(e.z, 0.0, 1.0), clamp(e.w, 0.0, 1.0), 0.0);
        imageStore(uOutput, ivec2(id), vec4(c, 1.0));
    }
}
";

    /// <summary>Metal MSL kernel. The SampledTexture slot convention provides sampler(0), but this kernel uses read.</summary>
    const string SourceMsl = @"
#include <metal_stdlib>
using namespace metal;

struct NormalViewParams
{
    float uDstWidth;
    float uDstHeight;
    float uMode;
    float uPad;
};

static inline float3 OctDecodeNormal(float2 e)
{
    float3 n = float3(e, 1.0 - fabs(e.x) - fabs(e.y));
    float t = saturate(-n.z);
    n.xy += select(float2(t), float2(-t), n.xy >= 0.0);
    return normalize(n);
}

kernel void CSMain(
    constant NormalViewParams& params [[buffer(0)]],
    texture2d<float, access::read> uNormal [[texture(0)]],
    texture2d<float, access::write> uOutput [[texture(1)]],
    uint2 id [[thread_position_in_grid]])
{
    if (id.x < (uint)params.uDstWidth && id.y < (uint)params.uDstHeight)
    {
        uint2 dim = uint2(uNormal.get_width(), uNormal.get_height());
        uint2 src = uint2((float2(id) + 0.5) / float2(params.uDstWidth, params.uDstHeight) * float2(dim));
        float4 e = uNormal.read(src);
        float3 c = float3(0.0);
        if (dot(fabs(e), float4(1.0)) > 0.0)
            c = params.uMode < 0.5 ? OctDecodeNormal(e.xy) * 0.5 + 0.5 : float3(saturate(e.z), saturate(e.w), 0.0);
        uOutput.write(float4(c, 1.0), id);
    }
}
";

    /// <summary>WebGPU WGSL. WGSL cannot assign a multi-component swizzle, so the decode rebuilds the vector instead of
    /// the += on n.xy the other three use.</summary>
    const string SourceWgsl = @"
struct NormalViewParams
{
    uDstWidth : f32,
    uDstHeight : f32,
    uMode : f32,
    uPad : f32,
};

@group(0) @binding(0) var<uniform> params : NormalViewParams;
@group(0) @binding(1) var uNormal : texture_2d<f32>;
@group(0) @binding(2) var uOutput : texture_storage_2d<rgba8unorm, write>;

fn OctDecodeNormal(e : vec2<f32>) -> vec3<f32>
{
    let z = 1.0 - abs(e.x) - abs(e.y);
    let t = clamp(-z, 0.0, 1.0);
    let xy = e + select(vec2<f32>(t), vec2<f32>(-t), e >= vec2<f32>(0.0));
    return normalize(vec3<f32>(xy, z));
}

@compute @workgroup_size(8, 8, 1)
fn CSMain(@builtin(global_invocation_id) id : vec3<u32>)
{
    if (id.x < u32(params.uDstWidth) && id.y < u32(params.uDstHeight))
    {
        let dim = vec2<f32>(textureDimensions(uNormal));
        let src = vec2<i32>((vec2<f32>(f32(id.x), f32(id.y)) + vec2<f32>(0.5)) / vec2<f32>(params.uDstWidth, params.uDstHeight) * dim);
        let e = textureLoad(uNormal, src, 0);
        var c = vec3<f32>(0.0);
        if (dot(abs(e), vec4<f32>(1.0)) > 0.0) {
            if (params.uMode < 0.5) {
                c = OctDecodeNormal(e.xy) * 0.5 + vec3<f32>(0.5);
            } else {
                c = vec3<f32>(clamp(e.z, 0.0, 1.0), clamp(e.w, 0.0, 1.0), 0.0);
            }
        }
        textureStore(uOutput, vec2<i32>(i32(id.x), i32(id.y)), vec4<f32>(c, 1.0));
    }
}
";
}
