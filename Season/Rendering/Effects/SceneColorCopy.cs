// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

/// <summary>
/// Engine built-in compute effect: SceneColor thumbnail copy
/// (2-1 Step A minimal first-pass validation case).
/// It validates three infrastructure gaps / first-use points in one shot:
/// 1. First validation of the AfterScene phase
///    (dispatch after the scene pass finishes, before Post / FinalBlit).
/// 2. ComputeResourceRef.Target wired up
///    (offscreen SceneColor RT used as SampledTexture input).
/// 3. rgba16float storage texture expansion
///    (output preserves linear HDR values, matching the bloom downsample chain format).
///
/// Behavior: each frame linearly samples SceneColor down into a fixed 480x270 storage
/// texture, which Sprite2D displays directly by name without any changes.
/// Expected visual result: the thumbnail contains the previous frame of itself
/// (recursive picture-in-picture, one-frame delay). That is direct evidence that the
/// AfterScene timing is correct, because the scene for the current frame already contains
/// the thumbnail control and the copy happens after it.
///
/// Binding layout (declaration order defines the cross-backend slot convention;
/// see ComputeBindingType summary):
/// [0] Params 16B (dstWidth, dstHeight, _pad x 2) -> HLSL b0
/// [1] SampledTexture (SceneColor target) -> HLSL t0 + s0 linear-clamp
/// [2] StorageTextureWrite rgba16float -> HLSL u0
///
/// Step A ships in HLSL only (the D3D12 first-validation backend). Step D completed the
/// four-source aligned GLSL/MSL/WGSL implementation after Target input and rgba16float
/// support landed across the other backends (same slot convention as Bloom.cs prefilter).
/// </summary>
public sealed class SceneColorCopyEffect : ComputeEffect
{
    /// <summary>Registered output texture name in the platform texture dictionary (used directly by Sprite2D.Name).</summary>
    public const string TextureName = "compute://scenecopy";

    public const uint Width = 480;

    public const uint Height = 270;

    ComputeKernel? _kernel;

    // ComputeResourceRef arrays cannot use stackalloc because they contain reference
    // types (see ComputeDispatchArgs summary). Build once in Initialize and reuse every
    // frame with zero allocations. SceneColor stays alive for the full app lifetime,
    // and resize recreates it in place without changing the reference.
    ComputeResourceRef[]? _resources;

    public override string Name => "sceneColorCopy";

    public override ComputePhase Phase => ComputePhase.AfterScene;

    public override bool Initialize(IGraphics g)
    {
        // AfterScene input depends on offscreen SceneColor.
        // Direct-to-backbuffer rendering has no sampleable Target.
        if (FrameSchedule.SceneColor == null)
            return false;

        _kernel = g.CreateComputeKernel(new ComputeKernelDesc
        {
            Name = "sceneColorCopy",
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
                    StorageFormat = ComputeStorageFormat.Rgba16Float,
                },
            },
        });
        if (_kernel == null) return false;

        g.CreateComputeTexture(TextureName, Width, Height, ComputeStorageFormat.Rgba16Float);
        _resources = new ComputeResourceRef[] { FrameSchedule.SceneColor, TextureName };
        return true;
    }

    public override void Record(IGraphics g)
    {
        Span<float> p = stackalloc float[4];
        p[0] = Width;
        p[1] = Height;

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

    /// <summary>D3D12 cs_5_0 (fxc; single exit avoids X4000). Output keeps the original linear HDR
    /// value without compression or encoding. After Sprite2D samples it, the main HDR
    /// pipeline applies tonemapping uniformly in FinalBlit, so the visual result matches
    /// the main scene.</summary>
    const string SourceHlsl = @"
cbuffer CopyParams : register(b0)
{
    float uDstWidth;
    float uDstHeight;
    float uPad0;
    float uPad1;
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
        uOutput[id.xy] = uScene.SampleLevel(uLinearClamp, uv, 0.0);
    }
}
";

    /// <summary>Vulkan GLSL 450 (glslang -> SPIR-V; Params use push_constant, binding follows declaration order).</summary>
    const string SourceGlsl = @"#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(push_constant) uniform CopyParams
{
    float uDstWidth;
    float uDstHeight;
    float uPad0;
    float uPad1;
};

layout(binding = 1) uniform sampler2D uScene;
layout(binding = 2, rgba16f) uniform writeonly image2D uOutput;

void main()
{
    uvec2 id = gl_GlobalInvocationID.xy;
    if (id.x < uint(uDstWidth) && id.y < uint(uDstHeight))
    {
        vec2 uv = (vec2(id) + 0.5) / vec2(uDstWidth, uDstHeight);
        imageStore(uOutput, ivec2(id), textureLod(uScene, uv, 0.0));
    }
}
";

    /// <summary>Metal MSL kernel.</summary>
    const string SourceMsl = @"
#include <metal_stdlib>
using namespace metal;

struct CopyParams
{
    float uDstWidth;
    float uDstHeight;
    float uPad0;
    float uPad1;
};

kernel void CSMain(
    constant CopyParams& params [[buffer(0)]],
    texture2d<float, access::sample> uScene [[texture(0)]],
    texture2d<float, access::write> uOutput [[texture(1)]],
    sampler uLinearClamp [[sampler(0)]],
    uint2 id [[thread_position_in_grid]])
{
    if (id.x < (uint)params.uDstWidth && id.y < (uint)params.uDstHeight)
    {
        float2 uv = (float2(id) + 0.5) / float2(params.uDstWidth, params.uDstHeight);
        uOutput.write(uScene.sample(uLinearClamp, uv, level(0.0)), id);
    }
}
";

    /// <summary>WebGPU WGSL (delivered through the interop layer; seasonWebGPU.js source is not included).</summary>
    const string SourceWgsl = @"
struct CopyParams
{
    uDstWidth : f32,
    uDstHeight : f32,
    uPad0 : f32,
    uPad1 : f32,
};

@group(0) @binding(0) var<uniform> params : CopyParams;
@group(0) @binding(1) var uScene : texture_2d<f32>;
@group(0) @binding(2) var uOutput : texture_storage_2d<rgba16float, write>;
@group(0) @binding(15) var uLinearClamp : sampler;

@compute @workgroup_size(8, 8, 1)
fn CSMain(@builtin(global_invocation_id) id : vec3<u32>)
{
    if (id.x < u32(params.uDstWidth) && id.y < u32(params.uDstHeight))
    {
        let uv = (vec2<f32>(f32(id.x), f32(id.y)) + vec2<f32>(0.5)) / vec2<f32>(params.uDstWidth, params.uDstHeight);
        textureStore(uOutput, vec2<i32>(i32(id.x), i32(id.y)), textureSampleLevel(uScene, uLinearClamp, uv, 0.0));
    }
}
";
}
