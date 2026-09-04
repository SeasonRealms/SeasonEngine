// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

/// <summary>
/// Engine built-in compute effect: motion-vector visualization
/// (2-3 Step A infrastructure first-pass validation case, contract clause 9).
/// It validates the entire velocity path in one shot:
/// 1. RtFormat.Rg16Float creation / clear / RTV / SRV are all wired through
///    (Device.ToNativeColorFormat + zero-optimized clear value).
/// 2. PassDesc.VelocityTarget turns the scene pass into a three-target setup
///    (OMSetRenderTargets with a non-contiguous RTV handle array).
/// 3. Main shader VELOCITY_OUTPUT variant
///    (VS outputs prevClip, PS writes PSOutput MRT).
/// 4. Jitter injection and de-jittering are exact inverses.
///    A static image must stay per-pixel zero, otherwise one side of the jitter/de-jitter path is wrong.
///
/// Behavior: each AfterScene phase downsamples the full-resolution SceneVelocity into a
/// fixed 480x270 storage texture with nearest sampling. Direction is mapped to hue and
/// magnitude is mapped to brightness. Sprite2D consumes it directly by name without any changes.
///
/// Acceptance criteria (thumbnail appearance; each one is direct evidence that one part of the
/// pipeline is correct):
/// - Camera and objects completely static -> fully black.
///   Any non-black value means jitter was not canceled by de-jittering
///   (velocityParams.xy or the PS reconstruction is wrong).
/// - Camera translation / rotation only -> all geometry has the same direction and color,
///   while sky and uncovered regions remain black (clear = (0,0) is working).
/// - Object motion only -> only that object is colored, with a black background
///   (the PrevWorld shadow copy is working).
/// - Transparent geometry -> not colored
///   (slot 1 write mask is 0, per contract clause 7).
///
/// Binding layout (declaration order defines the cross-backend slot convention;
/// see ComputeBindingType summary):
/// [0] Params 16B (dstWidth, dstHeight, scale, _pad) -> HLSL b0
/// [1] SampledTexture (SceneVelocity target) -> HLSL t0 + s0 linear-clamp
///     (this kernel uses Load, not sampling)
/// [2] StorageTextureWrite rgba8unorm -> HLSL u0
///
/// Step A was validated on D3D12. GLSL/MSL/WGSL sources live in this file as well with
/// literally identical formulas. They will become active once Step D wires up Rg16Float and
/// VelocityTarget on those three backends. Before that, they do not create SceneVelocity, so
/// Initialize returns false directly and leaves no residue in the pipeline.
/// </summary>
public sealed class VelocityViewEffect : ComputeEffect
{
    /// <summary>Registered output texture name in the platform texture dictionary (used directly by Sprite2D.Name).</summary>
    public const string TextureName = "compute://velocityview";

    public const uint Width = 480;

    public const uint Height = 270;

    /// <summary>
    /// Visualization gain: velocity is measured in UV per frame, with a typical magnitude
    /// around 1e-3, so it must be amplified to become visible.
    /// Magnitude multiplied by this value is clamped to [0,1] for brightness.
    /// This affects only the thumbnail appearance and does not participate in rendering.
    /// </summary>
    public static float Scale = 100f;

    ComputeKernel? _kernel;

    // ComputeResourceRef arrays cannot use stackalloc because they contain reference
    // types (see ComputeDispatchArgs summary). Build once in Initialize and reuse every
    // frame with zero allocations. SceneVelocity stays alive for the full app lifetime,
    // and resize recreates it in place without changing the reference.
    ComputeResourceRef[]? _resources;

    public override string Name => "velocityView";

    public override ComputePhase Phase => ComputePhase.AfterScene;

    public override bool Initialize(IGraphics g)
    {
        // Requires MotionVectors mode to be active and SceneVelocity to exist.
        // If the feature is disabled or falls back, SceneVelocity stays null and the whole effect remains inactive.
        if (!RenderQuality.Current.MotionVectors || FrameSchedule.SceneVelocity == null)
            return false;

        _kernel = g.CreateComputeKernel(new ComputeKernelDesc
        {
            Name = "velocityView",
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
        _resources = new ComputeResourceRef[] { FrameSchedule.SceneVelocity, TextureName };
        return true;
    }

    public override void Record(IGraphics g)
    {
        Span<float> p = stackalloc float[4];
        p[0] = Width;
        p[1] = Height;
        p[2] = Scale;
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

    // Shader sources (single source of truth; slots follow the class-level binding
    // convention; workgroup is fixed at 8x8x1).
    //
    // Direction-to-hue mapping is identical across all four backends:
    // hue = atan2(v.y, v.x) / 2PI + 0.5.
    // That makes +U (right) cyan, -U (left) red, +V (down) purple, and -V (up) yellow.
    // Brightness = saturate(|v| * scale).
    // Reads use per-texel Load / texelFetch with nearest downsampling so linear filtering
    // does not mix velocities from adjacent objects.

    /// <summary>D3D12 cs_5_0 (fxc; single exit avoids X4000). When Texture2D&lt;float4&gt; reads an
    /// rg16float SRV, missing components are filled as (0,1); taking .xy yields velocity.</summary>
    const string SourceHlsl = @"
cbuffer VelocityViewParams : register(b0)
{
    float uDstWidth;
    float uDstHeight;
    float uScale;
    float uPad;
};

Texture2D<float4> uVelocity : register(t0);
SamplerState uVelocitySampler : register(s0);
RWTexture2D<float4> uOutput : register(u0);

float3 HueToRgb(float h)
{
    float r = abs(h * 6.0 - 3.0) - 1.0;
    float g = 2.0 - abs(h * 6.0 - 2.0);
    float b = 2.0 - abs(h * 6.0 - 4.0);
    return saturate(float3(r, g, b));
}

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x < (uint)uDstWidth && id.y < (uint)uDstHeight)
    {
        uint w, h;
        uVelocity.GetDimensions(w, h);
        uint2 src = uint2((float2(id.xy) + 0.5) / float2(uDstWidth, uDstHeight) * float2(w, h));
        float2 v = uVelocity.Load(int3(src, 0)).xy;
        float mag = saturate(length(v) * uScale);
        float hue = atan2(v.y, v.x) / 6.28318531 + 0.5;
        uOutput[id.xy] = float4(HueToRgb(hue) * mag, 1.0);
    }
}
";

    /// <summary>Vulkan GLSL 450 (glslang compiles to SPIR-V at runtime; entry point is always main).
    /// The SampledTexture slot is bound as a CombinedImageSampler, and texelFetch reads per texel without filtering.</summary>
    const string SourceGlsl = @"#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(push_constant) uniform VelocityViewParams
{
    float uDstWidth;
    float uDstHeight;
    float uScale;
    float uPad;
};

layout(binding = 1) uniform sampler2D uVelocity;
layout(binding = 2, rgba8) uniform writeonly image2D uOutput;

vec3 HueToRgb(float h)
{
    float r = abs(h * 6.0 - 3.0) - 1.0;
    float g = 2.0 - abs(h * 6.0 - 2.0);
    float b = 2.0 - abs(h * 6.0 - 4.0);
    return clamp(vec3(r, g, b), 0.0, 1.0);
}

void main()
{
    uvec2 id = gl_GlobalInvocationID.xy;
    if (id.x < uint(uDstWidth) && id.y < uint(uDstHeight))
    {
        ivec2 dim = textureSize(uVelocity, 0);
        ivec2 src = ivec2((vec2(id) + 0.5) / vec2(uDstWidth, uDstHeight) * vec2(dim));
        vec2 v = texelFetch(uVelocity, src, 0).xy;
        float mag = clamp(length(v) * uScale, 0.0, 1.0);
        float hue = atan(v.y, v.x) / 6.28318531 + 0.5;
        imageStore(uOutput, ivec2(id), vec4(HueToRgb(hue) * mag, 1.0));
    }
}
";

    /// <summary>Metal MSL kernel. The SampledTexture slot convention provides sampler(0), but this kernel uses read for per-texel access.</summary>
    const string SourceMsl = @"
#include <metal_stdlib>
using namespace metal;

struct VelocityViewParams
{
    float uDstWidth;
    float uDstHeight;
    float uScale;
    float uPad;
};

static inline float3 HueToRgb(float h)
{
    float r = abs(h * 6.0 - 3.0) - 1.0;
    float g = 2.0 - abs(h * 6.0 - 2.0);
    float b = 2.0 - abs(h * 6.0 - 4.0);
    return saturate(float3(r, g, b));
}

kernel void CSMain(
    constant VelocityViewParams& params [[buffer(0)]],
    texture2d<float, access::read> uVelocity [[texture(0)]],
    texture2d<float, access::write> uOutput [[texture(1)]],
    uint2 id [[thread_position_in_grid]])
{
    if (id.x < (uint)params.uDstWidth && id.y < (uint)params.uDstHeight)
    {
        uint2 dim = uint2(uVelocity.get_width(), uVelocity.get_height());
        uint2 src = uint2((float2(id) + 0.5) / float2(params.uDstWidth, params.uDstHeight) * float2(dim));
        float2 v = uVelocity.read(src).xy;
        float mag = saturate(length(v) * params.uScale);
        float hue = atan2(v.y, v.x) / 6.28318531 + 0.5;
        uOutput.write(float4(HueToRgb(hue) * mag, 1.0), id);
    }
}
";

    /// <summary>WebGPU WGSL (delivered through the interop layer; seasonWebGPU.js source is not included).
    /// The SampledTexture slot automatically carries a sampler at @binding(15) as a layout placeholder,
    /// but this kernel uses textureLoad rather than sampling.</summary>
    const string SourceWgsl = @"
struct VelocityViewParams
{
    uDstWidth : f32,
    uDstHeight : f32,
    uScale : f32,
    uPad : f32,
};

@group(0) @binding(0) var<uniform> params : VelocityViewParams;
@group(0) @binding(1) var uVelocity : texture_2d<f32>;
@group(0) @binding(2) var uOutput : texture_storage_2d<rgba8unorm, write>;

fn HueToRgb(h : f32) -> vec3<f32>
{
    let r = abs(h * 6.0 - 3.0) - 1.0;
    let g = 2.0 - abs(h * 6.0 - 2.0);
    let b = 2.0 - abs(h * 6.0 - 4.0);
    return clamp(vec3<f32>(r, g, b), vec3<f32>(0.0), vec3<f32>(1.0));
}

@compute @workgroup_size(8, 8, 1)
fn CSMain(@builtin(global_invocation_id) id : vec3<u32>)
{
    if (id.x < u32(params.uDstWidth) && id.y < u32(params.uDstHeight))
    {
        let dim = vec2<f32>(textureDimensions(uVelocity));
        let src = vec2<i32>((vec2<f32>(f32(id.x), f32(id.y)) + vec2<f32>(0.5)) / vec2<f32>(params.uDstWidth, params.uDstHeight) * dim);
        let v = textureLoad(uVelocity, src, 0).xy;
        let mag = clamp(length(v) * params.uScale, 0.0, 1.0);
        let hue = atan2(v.y, v.x) / 6.28318531 + 0.5;
        textureStore(uOutput, vec2<i32>(i32(id.x), i32(id.y)), vec4<f32>(HueToRgb(hue) * mag, 1.0));
    }
}
";
}
