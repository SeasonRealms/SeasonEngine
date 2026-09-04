// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

/// <summary>
/// Engine built-in compute effect: TAA resolve
/// (2-3 Step B implementation body; see clauses 10-15 in section 2-3 of the RenderQuality class header).
///
/// Behavior: one kernel in the AfterScene phase, running at full resolution in linear HDR
/// space before tonemapping. It reads the current frame's SceneColor + SceneVelocity +
/// previous-frame output (history), writes the current output, and publishes that output
/// name to FrameSchedule.SceneColorOverride so bloom and the composition entry point can
/// use it as their source (clause 12).
///
/// Why this uses a storage texture instead of an RT-UAV: ComputeBindingType.StorageTextureWrite
/// is always write-only and only accepts textures created by CreateComputeTexture. Compute
/// cannot write directly to a RenderTarget UAV under the four-backend contract. So TAA writes
/// into a storage texture, and downstream stages read it by name through SceneColorOverride.
/// SceneColor RT itself remains the render target for the scene pass, leaving the pipeline unchanged.
///
/// Ping-pong (clause 11): history and output cannot be the same resource. Reading through SRV
/// and writing through UAV inside the same dispatch is a race, and WebGPU core does not support
/// rgba read-write storage. Therefore `taa0` and `taa1` alternate roles each frame: this frame
/// reads `_p^1` and writes `_p`, then publishes `_p` as the downstream source for the current frame.
/// Both resources are created once in Initialize, and runtime only swaps references with zero allocations.
///
/// Binding layout (declaration order defines the cross-backend slot convention;
/// see ComputeBindingType summary):
/// [0] Params 32B (width, height, 1/width, 1/height, feedback, clipGamma, historyValid, _pad) -> HLSL b0
/// [1] SampledTexture (SceneColor target, rgba16float) -> HLSL t0
/// [2] SampledTexture (SceneVelocity target, rg16float) -> HLSL t1
/// [3] SampledTexture (history storage texture) -> HLSL t2
/// [4] StorageTextureWrite rgba16float (current-frame output) -> HLSL u0
/// Sampler s0 (linear-clamp) is provided statically by the engine and is only needed for
/// history reprojection filtering. Scene and velocity always use texel loads.
///
/// Step B was shaped on D3D12 only (HLSL is the validated backend). The other three shader
/// sources are already provided in the aligned four-backend form and will be validated one by one in Step D.
/// </summary>
public sealed class TaaEffect : ComputeEffect
{
    /// <summary>Registered names of the ping-pong textures in the platform texture dictionary. Downstream stages resolve them by name, and Sprite2D can also sample them directly for debugging.</summary>
    public const string TextureName0 = "compute://taa0";

    public const string TextureName1 = "compute://taa1";

    ComputeKernel? _kernel;

    // Two resource arrays that alternate every frame. They are built once in Initialize
    // and then reused. ComputeResourceRef arrays containing strings cannot use stackalloc.
    // _res[p] points its history slot to [p^1] and its output slot to [p].
    ComputeResourceRef[][]? _res;

    readonly string[] _names = { TextureName0, TextureName1 };

    /// <summary>Index of the writer for the current frame (0/1). Record flips it before dispatch.</summary>
    int _p;

    /// <summary>Creation size of the ping-pong textures (clause 15: resize should be handled in place; on mismatch the effect bypasses itself).</summary>
    uint _width, _height;

    /// <summary>Whether the history contains valid data. It stays false on the first frame, when texture contents are undefined, and after any bypass; feedback is forced to zero based on this.</summary>
    bool _historyValid;

    public override string Name => "taa";

    public override ComputePhase Phase => ComputePhase.AfterScene;

    public override bool Initialize(IGraphics g)
    {
        // Quality mode and dependencies (clauses 1/10): TAA requires both velocity and
        // offscreen SceneColor. If either is missing, the whole effect stays inactive.
        if (RenderQuality.Current.AntiAliasing != AaMode.Taa || !RenderQuality.Current.MotionVectors)
            return false;
        if (FrameSchedule.SceneColor == null || FrameSchedule.SceneVelocity == null)
            return false;

        _kernel = g.CreateComputeKernel(new ComputeKernelDesc
        {
            Name = "taaResolve",
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
                new ComputeBindingDesc { Type = ComputeBindingType.Params, SizeInBytes = 32 },
                new ComputeBindingDesc { Type = ComputeBindingType.SampledTexture },
                new ComputeBindingDesc { Type = ComputeBindingType.SampledTexture },
                new ComputeBindingDesc { Type = ComputeBindingType.SampledTexture },
                new ComputeBindingDesc
                {
                    Type = ComputeBindingType.StorageTextureWrite,
                    StorageFormat = ComputeStorageFormat.Rgba16Float,
                },
            },
        });
        if (_kernel == null) return false;

        // Clause 15: create the ping-pong textures at the current full backbuffer resolution.
        // If size mismatches later, the effect falls back frame by frame to the original SceneColor.
        var res = DeviceServices.BaseApp.DeviceResolution;
        _width = Math.Max(8u, (uint)res.X);
        _height = Math.Max(8u, (uint)res.Y);
        g.CreateComputeTexture(TextureName0, _width, _height, ComputeStorageFormat.Rgba16Float);
        g.CreateComputeTexture(TextureName1, _width, _height, ComputeStorageFormat.Rgba16Float);

        _res = new ComputeResourceRef[2][];
        for (int p = 0; p < 2; p++)
        {
            _res[p] = new ComputeResourceRef[]
            {
                FrameSchedule.SceneColor,
                FrameSchedule.SceneVelocity,
                _names[p ^ 1],  // History = previous frame's writer
                _names[p],      // Output = current frame's writer
            };
        }

        _p = 0;
        _historyValid = false;
        return true;
    }

    public override void OnResize(IGraphics g)
    {
        // Clause 15 revision: recreate the ping-pong storage textures in place after resize
        // while keeping the name and C# object identity unchanged. Update the captured size
        // and invalidate history so convergence restarts on the next frame. The kernel itself
        // does not need rebuilding. CreateComputeTexture handles the size-match guard.
        var res = DeviceServices.BaseApp.DeviceResolution;
        uint w = Math.Max(8u, (uint)res.X);
        uint h = Math.Max(8u, (uint)res.Y);
        g.CreateComputeTexture(TextureName0, w, h, ComputeStorageFormat.Rgba16Float);
        g.CreateComputeTexture(TextureName1, w, h, ComputeStorageFormat.Rgba16Float);
        _width = w;
        _height = h;
        _historyValid = false;
    }

    public override void Record(IGraphics g)
    {
        // Clause 15: OnResize should already have recreated the textures in place, so sizes
        // normally match. This guard only handles degraded paths where OnResize was not called
        // (for example, a platform path that missed the resize callback). In that case, downstream
        // stages fall back to the original SceneColor, history is invalidated, and convergence
        // restarts automatically once the size matches again.
        var res = DeviceServices.BaseApp.DeviceResolution;
        if ((uint)res.X != _width || (uint)res.Y != _height)
        {
            FrameSchedule.SceneColorOverride = null;
            FrameSchedule.TaaActive = false;
            _historyValid = false;
            return;
        }

        _p ^= 1;

        Span<float> p = stackalloc float[8];
        p[0] = _width;
        p[1] = _height;
        p[2] = 1f / _width;
        p[3] = 1f / _height;
        p[4] = _historyValid ? RenderQuality.Current.TaaFeedback : 0f;
        p[5] = RenderQuality.Current.TaaVarianceClipGamma;
        p[6] = _historyValid ? 1f : 0f;
        p[7] = 0f;

        g.DispatchCompute(new ComputeDispatchArgs
        {
            Kernel = _kernel!,
            Params = MemoryMarshal.AsBytes(p),
            Resources = _res![_p],
            GroupsX = (_width + 7) / 8,
            GroupsY = (_height + 7) / 8,
            GroupsZ = 1,
        });

        // Clauses 12/13: publish the current frame's writer for bloom and the composition
        // entry point after dispatch, so downstream stages always read fully written content.
        // Clause 14: this flag also enables jitter injection.
        FrameSchedule.SceneColorOverride = _names[_p];
        FrameSchedule.TaaActive = true;
        _historyValid = true;
    }

    public void Dispose()
    {
        // Clause 12: clear the override only when it still points to one of our own outputs,
        // so we do not accidentally clear another producer's override. After reset, the whole chain falls back to SceneColor.
        if (FrameSchedule.SceneColorOverride == TextureName0 || FrameSchedule.SceneColorOverride == TextureName1)
            FrameSchedule.SceneColorOverride = null;
        FrameSchedule.TaaActive = false;
        _historyValid = false;
        _kernel?.Dispose();
        _kernel = null;
    }

    // Shader sources (single source of truth; slots follow the class-level binding convention;
    // workgroup is fixed at 8x8x1; single exit avoids fxc X4000; the resolve formula is a
    // cross-backend contract constant and should be ported literally when aligned).
    //
    // Algorithm (clause 10):
    //   1. Reprojection: prevUV = uv - velocity. Clause 5 defines velocity as
    //      "current pointing to history", so it can be used directly.
    //   2. History sampling: bilinear. This is the only sample that needs filtering.
    //      Scene and velocity always use texel loads: filtering velocity would mix motion
    //      from adjacent objects, and filtering scene at the same resolution is meaningless.
    //   3. Neighborhood clamping: one 3x3 pass computes first/second moments plus min/max.
    //      Clamp range = [mean-gamma*sigma, mean+gamma*sigma] intersected with [min, max].
    //      When gamma <= 0, this degenerates into a pure min/max bounding box.
    //      The operation stays in linear RGB per channel, without converting to YCoCg,
    //      prioritizing consistency across backends.
    //   4. Blending: lerp(cur, clamp(hist), feedback). If reprojection lands outside the
    //      screen or history is invalid, CPU-side logic has already forced feedback to zero.

    /// <summary>D3D12 cs_5_0 (fxc; single exit avoids X4000). When Texture2D&lt;float4&gt; reads an
    /// rg16float SRV, missing components are filled as (0,1); taking .xy yields velocity,
    /// matching the VelocityView precedent.</summary>
    const string SourceHlsl = @"
cbuffer TaaParams : register(b0)
{
    float uWidth;
    float uHeight;
    float uTexelX;
    float uTexelY;
    float uFeedback;
    float uClipGamma;
    float uHistoryValid;
    float uPad0;
};

Texture2D<float4> uScene : register(t0);
Texture2D<float4> uVelocity : register(t1);
Texture2D<float4> uHistory : register(t2);
SamplerState uLinearClamp : register(s0);
RWTexture2D<float4> uOutput : register(u0);

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x < (uint)uWidth && id.y < (uint)uHeight)
    {
        int2 maxCoord = int2((int)uWidth - 1, (int)uHeight - 1);
        float4 cur = uScene.Load(int3(id.xy, 0));

        // 3x3 neighborhood statistics: compute first/second moments and the min/max bounding box in one pass.
        float3 m1 = 0.0;
        float3 m2 = 0.0;
        float3 nmin = cur.rgb;
        float3 nmax = cur.rgb;
        [unroll] for (int y = -1; y <= 1; ++y)
        {
            [unroll] for (int x = -1; x <= 1; ++x)
            {
                int2 c = clamp(int2(id.xy) + int2(x, y), int2(0, 0), maxCoord);
                float3 s = uScene.Load(int3(c, 0)).rgb;
                m1 += s;
                m2 += s * s;
                nmin = min(nmin, s);
                nmax = max(nmax, s);
            }
        }
        float3 mean = m1 / 9.0;
        float3 sigma = sqrt(max(m2 / 9.0 - mean * mean, 0.0));
        float3 ext = uClipGamma * sigma;
        float3 lo = (uClipGamma > 0.0) ? max(mean - ext, nmin) : nmin;
        float3 hi = (uClipGamma > 0.0) ? min(mean + ext, nmax) : nmax;

        float2 uv = (float2(id.xy) + 0.5) * float2(uTexelX, uTexelY);
        float2 v = uVelocity.Load(int3(id.xy, 0)).xy;
        float2 prevUv = uv - v;

        float3 hist = uHistory.SampleLevel(uLinearClamp, prevUv, 0.0).rgb;
        float3 clamped = clamp(hist, lo, hi);

        // Reprojection outside the screen means no valid history.
        // Clamping out-of-range history to the border would be wrong and leaves ghosts along the frame edge.
        bool inside = all(prevUv > 0.0) && all(prevUv < 1.0);
        float w = (uHistoryValid > 0.5 && inside) ? uFeedback : 0.0;

        uOutput[id.xy] = float4(max(lerp(cur.rgb, clamped, w), 0.0), cur.a);
    }
}
";

    /// <summary>Vulkan GLSL 450 (glslang -> SPIR-V; Params use push_constant, binding follows declaration order, and binding 0 remains empty).</summary>
    const string SourceGlsl = @"#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(push_constant) uniform TaaParams
{
    float uWidth;
    float uHeight;
    float uTexelX;
    float uTexelY;
    float uFeedback;
    float uClipGamma;
    float uHistoryValid;
    float uPad0;
};

layout(binding = 1) uniform sampler2D uScene;
layout(binding = 2) uniform sampler2D uVelocity;
layout(binding = 3) uniform sampler2D uHistory;
layout(binding = 4, rgba16f) uniform writeonly image2D uOutput;

void main()
{
    uvec2 id = gl_GlobalInvocationID.xy;
    if (id.x < uint(uWidth) && id.y < uint(uHeight))
    {
        ivec2 maxCoord = ivec2(int(uWidth) - 1, int(uHeight) - 1);
        vec4 cur = texelFetch(uScene, ivec2(id), 0);

        vec3 m1 = vec3(0.0);
        vec3 m2 = vec3(0.0);
        vec3 nmin = cur.rgb;
        vec3 nmax = cur.rgb;
        for (int y = -1; y <= 1; ++y)
        {
            for (int x = -1; x <= 1; ++x)
            {
                ivec2 c = clamp(ivec2(id) + ivec2(x, y), ivec2(0), maxCoord);
                vec3 s = texelFetch(uScene, c, 0).rgb;
                m1 += s;
                m2 += s * s;
                nmin = min(nmin, s);
                nmax = max(nmax, s);
            }
        }
        vec3 mean = m1 / 9.0;
        vec3 sigma = sqrt(max(m2 / 9.0 - mean * mean, vec3(0.0)));
        vec3 ext = uClipGamma * sigma;
        vec3 lo = (uClipGamma > 0.0) ? max(mean - ext, nmin) : nmin;
        vec3 hi = (uClipGamma > 0.0) ? min(mean + ext, nmax) : nmax;

        vec2 uv = (vec2(id) + 0.5) * vec2(uTexelX, uTexelY);
        vec2 v = texelFetch(uVelocity, ivec2(id), 0).xy;
        vec2 prevUv = uv - v;

        vec3 hist = textureLod(uHistory, prevUv, 0.0).rgb;
        vec3 clamped = clamp(hist, lo, hi);

        bool inside = all(greaterThan(prevUv, vec2(0.0))) && all(lessThan(prevUv, vec2(1.0)));
        float w = (uHistoryValid > 0.5 && inside) ? uFeedback : 0.0;

        imageStore(uOutput, ivec2(id), vec4(max(mix(cur.rgb, clamped, w), vec3(0.0)), cur.a));
    }
}
";

    /// <summary>Metal MSL kernel (textures map to texture(i) by declaration order, Params go through buffer(0)).</summary>
    const string SourceMsl = @"
#include <metal_stdlib>
using namespace metal;

struct TaaParams
{
    float uWidth;
    float uHeight;
    float uTexelX;
    float uTexelY;
    float uFeedback;
    float uClipGamma;
    float uHistoryValid;
    float uPad0;
};

kernel void CSMain(
    constant TaaParams& params [[buffer(0)]],
    texture2d<float, access::sample> uScene [[texture(0)]],
    texture2d<float, access::sample> uVelocity [[texture(1)]],
    texture2d<float, access::sample> uHistory [[texture(2)]],
    texture2d<float, access::write> uOutput [[texture(3)]],
    sampler uLinearClamp [[sampler(0)]],
    uint2 id [[thread_position_in_grid]])
{
    if (id.x < (uint)params.uWidth && id.y < (uint)params.uHeight)
    {
        int2 maxCoord = int2((int)params.uWidth - 1, (int)params.uHeight - 1);
        float4 cur = uScene.read(id);

        float3 m1 = float3(0.0);
        float3 m2 = float3(0.0);
        float3 nmin = cur.rgb;
        float3 nmax = cur.rgb;
        for (int y = -1; y <= 1; ++y)
        {
            for (int x = -1; x <= 1; ++x)
            {
                int2 c = clamp(int2(id) + int2(x, y), int2(0), maxCoord);
                float3 s = uScene.read(uint2(c)).rgb;
                m1 += s;
                m2 += s * s;
                nmin = min(nmin, s);
                nmax = max(nmax, s);
            }
        }
        float3 mean = m1 / 9.0;
        float3 sigma = sqrt(max(m2 / 9.0 - mean * mean, float3(0.0)));
        float3 ext = params.uClipGamma * sigma;
        float3 lo = (params.uClipGamma > 0.0) ? max(mean - ext, nmin) : nmin;
        float3 hi = (params.uClipGamma > 0.0) ? min(mean + ext, nmax) : nmax;

        float2 uv = (float2(id) + 0.5) * float2(params.uTexelX, params.uTexelY);
        float2 v = uVelocity.read(id).xy;
        float2 prevUv = uv - v;

        float3 hist = uHistory.sample(uLinearClamp, prevUv, level(0.0)).rgb;
        float3 clamped = clamp(hist, lo, hi);

        bool inside = all(prevUv > float2(0.0)) && all(prevUv < float2(1.0));
        float w = (params.uHistoryValid > 0.5 && inside) ? params.uFeedback : 0.0;

        uOutput.write(float4(max(mix(cur.rgb, clamped, w), float3(0.0)), cur.a), id);
    }
}
";

    /// <summary>WebGPU WGSL (delivered through the interop layer; seasonWebGPU.js source is not included).</summary>
    const string SourceWgsl = @"
struct TaaParams
{
    uWidth : f32,
    uHeight : f32,
    uTexelX : f32,
    uTexelY : f32,
    uFeedback : f32,
    uClipGamma : f32,
    uHistoryValid : f32,
    uPad0 : f32,
};

@group(0) @binding(0) var<uniform> params : TaaParams;
@group(0) @binding(1) var uScene : texture_2d<f32>;
@group(0) @binding(2) var uVelocity : texture_2d<f32>;
@group(0) @binding(3) var uHistory : texture_2d<f32>;
@group(0) @binding(4) var uOutput : texture_storage_2d<rgba16float, write>;
@group(0) @binding(15) var uLinearClamp : sampler;

@compute @workgroup_size(8, 8, 1)
fn CSMain(@builtin(global_invocation_id) id : vec3<u32>)
{
    if (id.x < u32(params.uWidth) && id.y < u32(params.uHeight))
    {
        let coord = vec2<i32>(i32(id.x), i32(id.y));
        let maxCoord = vec2<i32>(i32(params.uWidth) - 1, i32(params.uHeight) - 1);
        let cur = textureLoad(uScene, coord, 0);

        var m1 = vec3<f32>(0.0);
        var m2 = vec3<f32>(0.0);
        var nmin = cur.rgb;
        var nmax = cur.rgb;
        for (var y : i32 = -1; y <= 1; y = y + 1)
        {
            for (var x : i32 = -1; x <= 1; x = x + 1)
            {
                let c = clamp(coord + vec2<i32>(x, y), vec2<i32>(0, 0), maxCoord);
                let s = textureLoad(uScene, c, 0).rgb;
                m1 = m1 + s;
                m2 = m2 + s * s;
                nmin = min(nmin, s);
                nmax = max(nmax, s);
            }
        }
        let mean = m1 / 9.0;
        let sigma = sqrt(max(m2 / 9.0 - mean * mean, vec3<f32>(0.0)));
        let ext = params.uClipGamma * sigma;
        var lo = nmin;
        var hi = nmax;
        if (params.uClipGamma > 0.0)
        {
            lo = max(mean - ext, nmin);
            hi = min(mean + ext, nmax);
        }

        let uv = (vec2<f32>(f32(id.x), f32(id.y)) + vec2<f32>(0.5)) * vec2<f32>(params.uTexelX, params.uTexelY);
        let v = textureLoad(uVelocity, coord, 0).xy;
        let prevUv = uv - v;

        let hist = textureSampleLevel(uHistory, uLinearClamp, prevUv, 0.0).rgb;
        let clamped = clamp(hist, lo, hi);

        let inside = all(prevUv > vec2<f32>(0.0)) && all(prevUv < vec2<f32>(1.0));
        var w = 0.0;
        if (params.uHistoryValid > 0.5 && inside)
        {
            w = params.uFeedback;
        }

        textureStore(uOutput, coord, vec4<f32>(max(mix(cur.rgb, clamped, w), vec3<f32>(0.0)), cur.a));
    }
}
";
}
