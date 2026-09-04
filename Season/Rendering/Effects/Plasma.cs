// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

/// <summary>
/// Engine built-in compute effect: procedural animated plasma texture
/// (1-6 Compute infrastructure minimal visual validation case).
/// It also serves as the standard template for third-party custom compute effects:
/// the shader single source of truth lives in this file, with no embedded platform code.
///
/// Behavior: each FrameStart phase dispatches one kernel with an 8x8 workgroup to write
/// time-varying three-phase plasma colors into a 256x256 rgba8unorm storage texture.
/// The texture is registered into the platform texture dictionary under TextureName, and
/// Sprite2D consumes it directly by setting Name = TextureName.
///
/// Binding layout (declaration order defines the cross-backend slot convention;
/// see ComputeBindingType summary):
/// [0] Params 16B (time, _pad, width, height)
///     -> HLSL b0 / GLSL push_constant / MSL buffer(0) / WGSL @binding(0)
/// [1] StorageTextureWrite
///     -> HLSL u0 / GLSL binding=1 rgba8 / MSL texture(0) / WGSL @binding(1)
///
/// Shared formula across all four shader backends (numerically identical):
/// v = sin(uv.x*2PI+t) + sin((uv.y*2PI+t)*0.7)
///   + sin((uv.x+uv.y)*PI+t*0.8) + sin(d*4PI-t*1.5)
/// RGB uses three phases with 120-degree offsets (2.0944 / 4.1888), where d is the
/// distance to the center.
/// </summary>
public sealed class PlasmaEffect : ComputeEffect
{
    /// <summary>Registered output texture name in the platform texture dictionary (used directly by Sprite2D.Name).</summary>
    public const string TextureName = "compute://plasma";

    public const uint Size = 256;

    ComputeKernel? _kernel;

    readonly Stopwatch _clock = Stopwatch.StartNew();

    // This array cannot use stackalloc because it contains a string reference
    // (see ComputeDispatchArgs summary). Reuse the cached array every frame with zero allocations.
    readonly ComputeResourceRef[] _resources = { TextureName };

    public override string Name => "plasma";

    public override ComputePhase Phase => ComputePhase.FrameStart;

    public override bool Initialize(IGraphics g)
    {
        _kernel = g.CreateComputeKernel(new ComputeKernelDesc
        {
            Name = "plasma",
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
                new ComputeBindingDesc { Type = ComputeBindingType.StorageTextureWrite },
            },
        });
        if (_kernel == null) return false;

        g.CreateComputeTexture(TextureName, Size, Size);
        return true;
    }

    public override void Record(IGraphics g)
    {
        Span<float> p = stackalloc float[4];
        p[0] = (float)_clock.Elapsed.TotalSeconds;
        p[2] = Size;
        p[3] = Size;

        g.DispatchCompute(new ComputeDispatchArgs
        {
            Kernel = _kernel!,
            Params = MemoryMarshal.AsBytes(p),
            Resources = _resources,
            GroupsX = (Size + 7) / 8,
            GroupsY = (Size + 7) / 8,
            GroupsZ = 1,
        });
    }

    public void Dispose()
    {
        _kernel?.Dispose();
        _kernel = null;
    }

    // Shader sources for all four backends (single source of truth; slots follow the
    // class-level binding convention; workgroup is fixed at 8x8x1).

    /// <summary>D3D12 cs_5_0 (fxc; single exit avoids X4000).</summary>
    const string SourceHlsl = @"
cbuffer PlasmaParams : register(b0)
{
    float uTime;
    float uPad;
    float uWidth;
    float uHeight;
};

RWTexture2D<float4> uOutput : register(u0);

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x < (uint)uWidth && id.y < (uint)uHeight)
    {
        float2 uv = float2(id.xy) / float2(uWidth, uHeight);
        float t = uTime;
        float d = distance(uv, float2(0.5, 0.5));
        float v = sin(uv.x * 6.2832 + t)
                + sin((uv.y * 6.2832 + t) * 0.7)
                + sin((uv.x + uv.y) * 3.1416 + t * 0.8)
                + sin(d * 12.566 - t * 1.5);
        float3 rgb = 0.5 + 0.5 * float3(sin(v * 3.1416), sin(v * 3.1416 + 2.0944), sin(v * 3.1416 + 4.1888));
        uOutput[id.xy] = float4(rgb, 1.0);
    }
}
";

    /// <summary>Vulkan GLSL 450 (glslang compiles to SPIR-V at runtime; entry point is always main).</summary>
    const string SourceGlsl = @"#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(push_constant) uniform PlasmaParams
{
    float uTime;
    float uPad;
    float uWidth;
    float uHeight;
};

layout(binding = 1, rgba8) uniform writeonly image2D uOutput;

void main()
{
    uvec2 id = gl_GlobalInvocationID.xy;
    if (id.x < uint(uWidth) && id.y < uint(uHeight))
    {
        vec2 uv = vec2(id) / vec2(uWidth, uHeight);
        float t = uTime;
        float d = distance(uv, vec2(0.5, 0.5));
        float v = sin(uv.x * 6.2832 + t)
                + sin((uv.y * 6.2832 + t) * 0.7)
                + sin((uv.x + uv.y) * 3.1416 + t * 0.8)
                + sin(d * 12.566 - t * 1.5);
        vec3 rgb = 0.5 + 0.5 * vec3(sin(v * 3.1416), sin(v * 3.1416 + 2.0944), sin(v * 3.1416 + 4.1888));
        imageStore(uOutput, ivec2(id), vec4(rgb, 1.0));
    }
}
";

    /// <summary>Metal MSL kernel.</summary>
    const string SourceMsl = @"
#include <metal_stdlib>
using namespace metal;

struct PlasmaParams
{
    float uTime;
    float uPad;
    float uWidth;
    float uHeight;
};

kernel void CSMain(
    constant PlasmaParams& params [[buffer(0)]],
    texture2d<float, access::write> uOutput [[texture(0)]],
    uint2 id [[thread_position_in_grid]])
{
    if (id.x < (uint)params.uWidth && id.y < (uint)params.uHeight)
    {
        float2 uv = float2(id) / float2(params.uWidth, params.uHeight);
        float t = params.uTime;
        float d = distance(uv, float2(0.5, 0.5));
        float v = sin(uv.x * 6.2832 + t)
                + sin((uv.y * 6.2832 + t) * 0.7)
                + sin((uv.x + uv.y) * 3.1416 + t * 0.8)
                + sin(d * 12.566 - t * 1.5);
        float3 rgb = 0.5 + 0.5 * float3(sin(v * 3.1416), sin(v * 3.1416 + 2.0944), sin(v * 3.1416 + 4.1888));
        uOutput.write(float4(rgb, 1.0), id);
    }
}
";

    /// <summary>WebGPU WGSL (delivered through the interop layer; seasonWebGPU.js source is not included).</summary>
    const string SourceWgsl = @"
struct PlasmaParams
{
    uTime : f32,
    uPad : f32,
    uWidth : f32,
    uHeight : f32,
};

@group(0) @binding(0) var<uniform> params : PlasmaParams;
@group(0) @binding(1) var uOutput : texture_storage_2d<rgba8unorm, write>;

@compute @workgroup_size(8, 8, 1)
fn CSMain(@builtin(global_invocation_id) id : vec3<u32>)
{
    if (id.x < u32(params.uWidth) && id.y < u32(params.uHeight))
    {
        let uv = vec2<f32>(f32(id.x), f32(id.y)) / vec2<f32>(params.uWidth, params.uHeight);
        let t = params.uTime;
        let d = distance(uv, vec2<f32>(0.5, 0.5));
        let v = sin(uv.x * 6.2832 + t)
              + sin((uv.y * 6.2832 + t) * 0.7)
              + sin((uv.x + uv.y) * 3.1416 + t * 0.8)
              + sin(d * 12.566 - t * 1.5);
        let rgb = vec3<f32>(0.5) + 0.5 * vec3<f32>(sin(v * 3.1416), sin(v * 3.1416 + 2.0944), sin(v * 3.1416 + 4.1888));
        textureStore(uOutput, vec2<i32>(i32(id.x), i32(id.y)), vec4<f32>(rgb, 1.0));
    }
}
";
}
