// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

/// <summary>
/// Engine built-in compute effect: SceneDepth linearization visualization
/// (2-2 Step A infrastructure first-pass validation case).
/// This validates the full path for using depth as a compute input in one shot:
/// 1. Explicit FrameSchedule.SceneDepth depth RT
///    (private DSV writes in the scene pass + StoreDepth).
/// 2. ComputeBindingType.DepthTexture wired up
///    (depth SRV from a depth-only RT as texel-load input).
/// 3. Depth aspect state tracking
///    (DepthWrite -> NonPixelShaderResource -> PixelShaderResource).
///
/// Behavior: each AfterScene phase downsamples the full-resolution SceneDepth into a
/// fixed 480x270 storage texture with nearest filtering, then shows the linearized
/// depth in grayscale after reprojection (near = black, far = white). Sprite2D
/// consumes it directly by name without any changes.
/// Expected visual result: 3D geometry shows a grayscale depth gradient, while the
/// sky/clear region (clear = 1.0) is pure white. That is direct evidence that depth
/// writes, StoreDepth, and SRV reads are all working together correctly.
///
/// Binding layout (declaration order defines the cross-backend slot convention;
/// see ComputeBindingType summary):
/// [0] Params 16B (near, far, dstWidth, dstHeight) -> HLSL b0
/// [1] DepthTexture (SceneDepth target, texel load without sampler) -> HLSL t0
/// [2] StorageTextureWrite rgba8unorm -> HLSL u0
///
/// The linearization exactly matches the engine projection convention
/// (LH + [0,1] depth, see Camera3D summary):
/// z = near*far / (far - d*(far - near)), with
/// (z - near)/(far - near) normalized as grayscale.
///
/// Step A ships in HLSL only (the D3D12 first-validation backend). Step C completed
/// the four-source aligned implementation after DepthTexture support landed on all
/// backends (GLSL/MSL/WGSL match HLSL numerically, verified on all four backends).
/// </summary>
public sealed class DepthViewEffect : ComputeEffect
{
    /// <summary>Registered output texture name in the platform texture dictionary (used directly by Sprite2D.Name).</summary>
    public const string TextureName = "compute://depthview";

    public const uint Width = 480;

    public const uint Height = 270;

    ComputeKernel? _kernel;

    // ComputeResourceRef arrays cannot use stackalloc because they contain reference
    // types (see ComputeDispatchArgs summary). Build once in Initialize and reuse every
    // frame with zero allocations. SceneDepth stays alive for the full app lifetime,
    // and resize recreates it in place without changing the reference.
    ComputeResourceRef[]? _resources;

    public override string Name => "depthView";

    public override ComputePhase Phase => ComputePhase.AfterScene;

    public override bool Initialize(IGraphics g)
    {
        // Requires AO mode to be settled and SceneDepth to exist.
        // When AO is Off or falls back, SceneDepth remains null and this effect stays inactive.
        if (RenderQuality.Current.AmbientOcclusion == AoMode.Off || FrameSchedule.SceneDepth == null)
            return false;

        _kernel = g.CreateComputeKernel(new ComputeKernelDesc
        {
            Name = "depthView",
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
                new ComputeBindingDesc { Type = ComputeBindingType.DepthTexture },
                new ComputeBindingDesc
                {
                    Type = ComputeBindingType.StorageTextureWrite,
                    StorageFormat = ComputeStorageFormat.Rgba8Unorm,
                },
            },
        });
        if (_kernel == null) return false;

        g.CreateComputeTexture(TextureName, Width, Height);
        _resources = new ComputeResourceRef[] { FrameSchedule.SceneDepth, TextureName };
        return true;
    }

    public override void Record(IGraphics g)
    {
        var camera = DeviceServices.BaseApp.Camera;

        Span<float> p = stackalloc float[4];
        p[0] = camera.Near;
        p[1] = camera.Far;
        p[2] = Width;
        p[3] = Height;

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

    /// <summary>D3D12 cs_5_0 (fxc; single exit avoids X4000). Depth is read per texel with Load
    /// (depth32float is not filterable, matching the strictest cross-backend constraint).
    /// GetDimensions fetches the source size without passing extra parameters.</summary>
    const string SourceHlsl = @"
cbuffer DepthViewParams : register(b0)
{
    float uNear;
    float uFar;
    float uDstWidth;
    float uDstHeight;
};

Texture2D<float> uDepth : register(t0);
RWTexture2D<float4> uOutput : register(u0);

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x < (uint)uDstWidth && id.y < (uint)uDstHeight)
    {
        uint w, h;
        uDepth.GetDimensions(w, h);
        uint2 src = uint2((float2(id.xy) + 0.5) / float2(uDstWidth, uDstHeight) * float2(w, h));
        float d = uDepth.Load(int3(src, 0));
        float z = uNear * uFar / (uFar - d * (uFar - uNear));
        float g = saturate((z - uNear) / (uFar - uNear));
        uOutput[id.xy] = float4(g, g, g, 1.0);
    }
}
";

    /// <summary>Vulkan GLSL 450 (glslang compiles to SPIR-V at runtime; entry point is always main).
    /// Depth is bound as CombinedImageSampler (immutable point sampler, see VKComputeKernel),
    /// and read per texel with texelFetch without filtering.</summary>
    const string SourceGlsl = @"#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(push_constant) uniform DepthViewParams
{
    float uNear;
    float uFar;
    float uDstWidth;
    float uDstHeight;
};

layout(binding = 1) uniform sampler2D uDepth;
layout(binding = 2, rgba8) uniform writeonly image2D uOutput;

void main()
{
    uvec2 id = gl_GlobalInvocationID.xy;
    if (id.x < uint(uDstWidth) && id.y < uint(uDstHeight))
    {
        ivec2 dim = textureSize(uDepth, 0);
        ivec2 src = ivec2((vec2(id) + 0.5) / vec2(uDstWidth, uDstHeight) * vec2(dim));
        float d = texelFetch(uDepth, src, 0).r;
        float z = uNear * uFar / (uFar - d * (uFar - uNear));
        float g = clamp((z - uNear) / (uFar - uNear), 0.0, 1.0);
        imageStore(uOutput, ivec2(id), vec4(g, g, g, 1.0));
    }
}
";

    /// <summary>Metal MSL kernel. Depth uses depth2d + access::read for per-texel reads without
    /// a sampler; declaring a depth-format texture as texture2d is undefined behavior and
    /// will be rejected by the validation layer.</summary>
    const string SourceMsl = @"
#include <metal_stdlib>
using namespace metal;

struct DepthViewParams
{
    float uNear;
    float uFar;
    float uDstWidth;
    float uDstHeight;
};

kernel void CSMain(
    constant DepthViewParams& params [[buffer(0)]],
    depth2d<float, access::read> uDepth [[texture(0)]],
    texture2d<float, access::write> uOutput [[texture(1)]],
    uint2 id [[thread_position_in_grid]])
{
    if (id.x < (uint)params.uDstWidth && id.y < (uint)params.uDstHeight)
    {
        uint2 dim = uint2(uDepth.get_width(), uDepth.get_height());
        uint2 src = uint2((float2(id) + 0.5) / float2(params.uDstWidth, params.uDstHeight) * float2(dim));
        float d = uDepth.read(src);
        float z = params.uNear * params.uFar / (params.uFar - d * (params.uFar - params.uNear));
        float g = saturate((z - params.uNear) / (params.uFar - params.uNear));
        uOutput.write(float4(g, g, g, 1.0), id);
    }
}
";

    /// <summary>WebGPU WGSL (delivered through the interop layer; seasonWebGPU.js source is not included).
    /// Depth uses texture_depth_2d + textureLoad (returns f32, no sampler; the depth24plus
    /// form is valid, see web-side formatKind 3).</summary>
    const string SourceWgsl = @"
struct DepthViewParams
{
    uNear : f32,
    uFar : f32,
    uDstWidth : f32,
    uDstHeight : f32,
};

@group(0) @binding(0) var<uniform> params : DepthViewParams;
@group(0) @binding(1) var uDepth : texture_depth_2d;
@group(0) @binding(2) var uOutput : texture_storage_2d<rgba8unorm, write>;

@compute @workgroup_size(8, 8, 1)
fn CSMain(@builtin(global_invocation_id) id : vec3<u32>)
{
    if (id.x < u32(params.uDstWidth) && id.y < u32(params.uDstHeight))
    {
        let dim = vec2<f32>(textureDimensions(uDepth));
        let src = vec2<i32>((vec2<f32>(f32(id.x), f32(id.y)) + vec2<f32>(0.5)) / vec2<f32>(params.uDstWidth, params.uDstHeight) * dim);
        let d = textureLoad(uDepth, src, 0);
        let z = params.uNear * params.uFar / (params.uFar - d * (params.uFar - params.uNear));
        let g = saturate((z - params.uNear) / (params.uFar - params.uNear));
        textureStore(uOutput, vec2<i32>(i32(id.x), i32(id.y)), vec4<f32>(g, g, g, 1.0));
    }
}
";
}
