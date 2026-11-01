// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

/// <summary>
/// P3 Hi-Z 深度金字塔：把 SceneDepth 逐级 2×2 取最近深度，压成 <see cref="LevelCount"/> 级单通道链，
/// 供屏幕空间反射的射线步进整块跳过空区域。
///
/// 为什么停在 1/16 而不是做到 1×1：金字塔的用途是"一个 texel 覆盖多少屏幕"，不是深度精度。
/// 屏幕空间射线能走多远由它在屏幕上还剩多少距离决定，1/16 的一块已经是一个 1920 宽画面上的 120 像素，
/// 再往下的级别在射线的有效长度里只剩一两步，省下的带宽抵不上多出来的绑定槽。
///
/// 存的值是 NDC 深度的**最小值**（最近表面）：步进时只要射线自身的深度还没越过整块的最小值，
/// 就说明这一块里没有任何表面挡在射线前面，可以整块跳过；一旦越过，才降到更细的一级去逼近交点。
///
/// 每帧重建而不是复用上一帧：金字塔的用途是"这次步进往哪走"，用旧深度会让快速运动的相机把反射
/// 钉在上一帧的物体上。四级的成本加起来不到一次全屏拷贝。
///
/// 注册顺序契约：消费方按名字采样这几张纹理，本效果必须先完成本帧的重建。
/// 顺序反了不会读到脏数据 —— 消费方在入口检查 <see cref="Ready"/>，
/// 表现为"本帧不画反射"，而不是采样上一帧残留的金字塔。
///
/// 只依赖 SceneDepth，不要求 SceneNormal：水面反射用的是自己扰动后的法线（水面根本不写法线目标），
/// 从深度现场重建的法线在轮廓两侧会翻转，反而会在池沿、岸线糊成一片。
/// </summary>
public sealed class HiZEffect : ComputeEffect
{
    public const int LevelCount = 4;

    public const string TextureNamePrefix = "compute://hiz/";

    public static string LevelName(int level) => TextureNamePrefix + level;

    /// <summary>本帧金字塔是否已按当前视图重建完成；水面反射用它做同帧依赖门控。</summary>
    public static bool Ready { get; private set; }

    readonly uint[] _widths = new uint[LevelCount];
    readonly uint[] _heights = new uint[LevelCount];
    IGraphics? _graphics;
    ComputeKernel? _fromDepth;
    ComputeKernel? _reduce;
    ComputeResourceRef[]? _depthResources;
    ComputeResourceRef[]? _reduceResources;
    uint _sourceWidth, _sourceHeight;

    public override string Name => "hiZ";

    /// <summary>AfterOpaque：SceneDepth 在场景不透明段结束后才是一次干净的全帧深度。</summary>
    public override ComputePhase Phase => ComputePhase.AfterOpaque;

    public override ComputeScope Scope => ComputeScope.View;

    public override ViewFeature RequiredFeature => ViewFeature.Reflections;

    /// <summary>第 0 级的尺寸（全分辨率的一半）；任一分量为零表示尚未分配。</summary>
    public uint Level0Width => _widths[0];

    public uint Level0Height => _heights[0];

    public override bool Initialize(IGraphics g)
    {
        if (_graphics != null) throw new InvalidOperationException("Hi-Z 金字塔已经初始化。");
        if (RenderQuality.Current.ScreenSpaceReflections != SsrMode.HiZ || FrameSchedule.SceneDepth == null) return false;
        var depth = FrameSchedule.SceneDepth;
        if (depth.Desc.DepthFormat != RtFormat.D32Float || depth.Desc.Width == 0 || depth.Desc.Height == 0) return false;
        if (!Allocate(g, depth.Desc.Width, depth.Desc.Height)) return false;

        _fromDepth = g.CreateComputeKernel(new ComputeKernelDesc
        {
            Name = "hizFromDepth",
            Source = new ShaderSourceSet
            {
                Hlsl = SourceFromDepthHlsl,
                Glsl = SourceFromDepthGlsl,
                Msl = SourceFromDepthMsl,
                Wgsl = SourceFromDepthWgsl,
                EntryPoint = "CSMain",
            },
            Bindings = new[]
            {
                new ComputeBindingDesc { Type = ComputeBindingType.Params, SizeInBytes = 16 },
                new ComputeBindingDesc { Type = ComputeBindingType.DepthTexture },
                new ComputeBindingDesc { Type = ComputeBindingType.StorageTextureWrite, StorageFormat = ComputeStorageFormat.R16Float },
            },
        });
        _reduce = g.CreateComputeKernel(new ComputeKernelDesc
        {
            Name = "hizReduce",
            Source = new ShaderSourceSet
            {
                Hlsl = SourceReduceHlsl,
                Glsl = SourceReduceGlsl,
                Msl = SourceReduceMsl,
                Wgsl = SourceReduceWgsl,
                EntryPoint = "CSMain",
            },
            Bindings = new[]
            {
                new ComputeBindingDesc { Type = ComputeBindingType.Params, SizeInBytes = 16 },
                new ComputeBindingDesc { Type = ComputeBindingType.SampledTexture },
                new ComputeBindingDesc { Type = ComputeBindingType.StorageTextureWrite, StorageFormat = ComputeStorageFormat.R16Float },
            },
        });
        if (_fromDepth == null || _reduce == null)
        {
            Dispose();
            return false;
        }
        _graphics = g;
        _depthResources = new ComputeResourceRef[] { depth, LevelName(0) };
        _reduceResources = new ComputeResourceRef[] { LevelName(level: 0), LevelName(level: 1) };
        return true;
    }

    bool Allocate(IGraphics g, uint width, uint height)
    {
        _sourceWidth = width;
        _sourceHeight = height;
        uint w = width, h = height;
        for (int level = 0; level < LevelCount; level++)
        {
            // 向上取整：源是奇数时最后一行/列也要被最后一块覆盖到，否则边缘会残留上一帧的深度。
            w = Math.Max(1u, (w + 1u) / 2u);
            h = Math.Max(1u, (h + 1u) / 2u);
            _widths[level] = w;
            _heights[level] = h;
            g.CreateComputeTexture(LevelName(level), w, h, ComputeStorageFormat.R16Float);
        }
        return true;
    }

    public override void OnResize(IGraphics g)
    {
        if (!ReferenceEquals(g, _graphics)) return;
        var depth = FrameSchedule.SceneDepth;
        if (depth == null || depth.Desc.Width == 0 || depth.Desc.Height == 0) return;
        Allocate(g, depth.Desc.Width, depth.Desc.Height);
        _depthResources![0] = depth;
    }

    public override void Record(IGraphics g)
    {
        Ready = false;
        if (!ReferenceEquals(g, _graphics) || _fromDepth == null || _reduce == null) return;
        var depth = FrameSchedule.SceneDepth;
        if (RenderQuality.Current.ScreenSpaceReflections != SsrMode.HiZ || depth == null
            || depth.Desc.Width != _sourceWidth || depth.Desc.Height != _sourceHeight) return;

        Span<float> p = stackalloc float[4];
        p[0] = _sourceWidth;
        p[1] = _sourceHeight;
        p[2] = _widths[0];
        p[3] = _heights[0];
        if (!g.TryDispatchCompute(new ComputeDispatchArgs
        {
            Kernel = _fromDepth,
            Params = MemoryMarshal.AsBytes(p),
            Resources = _depthResources,
            GroupsX = (_widths[0] + 7) / 8,
            GroupsY = (_heights[0] + 7) / 8,
            GroupsZ = 1,
        })) return;

        for (int level = 1; level < LevelCount; level++)
        {
            _reduceResources![0] = LevelName(level - 1);
            _reduceResources[1] = LevelName(level);
            p[0] = _widths[level - 1];
            p[1] = _heights[level - 1];
            p[2] = _widths[level];
            p[3] = _heights[level];
            if (!g.TryDispatchCompute(new ComputeDispatchArgs
            {
                Kernel = _reduce,
                Params = MemoryMarshal.AsBytes(p),
                Resources = _reduceResources,
                GroupsX = (_widths[level] + 7) / 8,
                GroupsY = (_heights[level] + 7) / 8,
                GroupsZ = 1,
            })) return;
        }
        Ready = true;
    }

    public override void OnUnregistered() => Reset();

    void Reset()
    {
        Ready = false;
        _graphics = null;
    }

    public void Dispose()
    {
        _fromDepth?.Dispose();
        _fromDepth = null;
        _reduce?.Dispose();
        _reduce = null;
        _depthResources = null;
        _reduceResources = null;
        Array.Clear(_widths);
        Array.Clear(_heights);
        _sourceWidth = _sourceHeight = 0;
        Reset();
    }

    // Shader sources (single source of truth; slots follow the class-level binding
    // convention; workgroup is fixed at 8x8x1).

    /// <summary>D3D12 cs_5_0 (fxc; single exit avoids X4000). 第 0 级直接读深度附件，四个角取最小值。</summary>
    const string SourceFromDepthHlsl = @"
cbuffer HiZParams : register(b0)
{
    float uSrcWidth;
    float uSrcHeight;
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
        // 源是奇数尺寸时右下角会越界，钳到最后一行/列：重复采样边缘不会把最小值抬高，
        // 而漏掉它就等于让金字塔的边缘比真实深度更远，射线会从缺口里穿过去。
        int2 first = int2(id.xy) * 2;
        int2 last = int2((uint)uSrcWidth - 1u, (uint)uSrcHeight - 1u);
        float d0 = uDepth.Load(int3(min(first, last), 0));
        float d1 = uDepth.Load(int3(min(first + int2(1, 0), last), 0));
        float d2 = uDepth.Load(int3(min(first + int2(0, 1), last), 0));
        float d3 = uDepth.Load(int3(min(first + int2(1, 1), last), 0));
        uOutput[id.xy] = float4(min(min(d0, d1), min(d2, d3)), 0.0, 0.0, 0.0);
    }
}
";

    /// <summary>Vulkan GLSL 450 (glslang -> SPIR-V; Params use push_constant, binding follows declaration order).</summary>
    const string SourceFromDepthGlsl = @"#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(push_constant) uniform HiZParams
{
    float uSrcWidth;
    float uSrcHeight;
    float uDstWidth;
    float uDstHeight;
};

layout(binding = 1) uniform sampler2D uDepth;
layout(binding = 2, r16f) uniform writeonly image2D uOutput;

void main()
{
    uvec2 id = gl_GlobalInvocationID.xy;
    if (id.x < uint(uDstWidth) && id.y < uint(uDstHeight))
    {
        ivec2 first = ivec2(id) * 2;
        ivec2 last = ivec2(int(uint(uSrcWidth)) - 1, int(uint(uSrcHeight)) - 1);
        float d0 = texelFetch(uDepth, min(first, last), 0).r;
        float d1 = texelFetch(uDepth, min(first + ivec2(1, 0), last), 0).r;
        float d2 = texelFetch(uDepth, min(first + ivec2(0, 1), last), 0).r;
        float d3 = texelFetch(uDepth, min(first + ivec2(1, 1), last), 0).r;
        imageStore(uOutput, ivec2(id), vec4(min(min(d0, d1), min(d2, d3)), 0.0, 0.0, 0.0));
    }
}
";

    /// <summary>Metal MSL kernel.</summary>
    const string SourceFromDepthMsl = @"
#include <metal_stdlib>
using namespace metal;

struct HiZParams
{
    float uSrcWidth;
    float uSrcHeight;
    float uDstWidth;
    float uDstHeight;
};

kernel void CSMain(
    constant HiZParams& params [[buffer(0)]],
    depth2d<float, access::read> uDepth [[texture(0)]],
    texture2d<float, access::write> uOutput [[texture(1)]],
    uint2 id [[thread_position_in_grid]])
{
    if (id.x < uint(params.uDstWidth) && id.y < uint(params.uDstHeight))
    {
        int2 first = int2(id) * 2;
        int2 last = int2(int(uint(params.uSrcWidth)) - 1, int(uint(params.uSrcHeight)) - 1);
        float d0 = uDepth.read(uint2(min(first, last)));
        float d1 = uDepth.read(uint2(min(first + int2(1, 0), last)));
        float d2 = uDepth.read(uint2(min(first + int2(0, 1), last)));
        float d3 = uDepth.read(uint2(min(first + int2(1, 1), last)));
        uOutput.write(float4(min(min(d0, d1), min(d2, d3)), 0.0, 0.0, 0.0), id);
    }
}
";

    /// <summary>WebGPU WGSL. 存储格式按引擎的窄格式回退链固定写 rgba16float，读取只取 .x。</summary>
    const string SourceFromDepthWgsl = @"
struct HiZParams
{
    uSrcWidth : f32,
    uSrcHeight : f32,
    uDstWidth : f32,
    uDstHeight : f32,
};

@group(0) @binding(0) var<uniform> params : HiZParams;
@group(0) @binding(1) var uDepth : texture_depth_2d;
@group(0) @binding(2) var uOutput : texture_storage_2d<rgba16float, write>;

@compute @workgroup_size(8, 8, 1)
fn CSMain(@builtin(global_invocation_id) id : vec3<u32>)
{
    if (id.x < u32(params.uDstWidth) && id.y < u32(params.uDstHeight))
    {
        let first = vec2<i32>(i32(id.x), i32(id.y)) * 2;
        let last = vec2<i32>(i32(u32(params.uSrcWidth)) - 1, i32(u32(params.uSrcHeight)) - 1);
        let d0 = textureLoad(uDepth, min(first, last), 0);
        let d1 = textureLoad(uDepth, min(first + vec2<i32>(1, 0), last), 0);
        let d2 = textureLoad(uDepth, min(first + vec2<i32>(0, 1), last), 0);
        let d3 = textureLoad(uDepth, min(first + vec2<i32>(1, 1), last), 0);
        textureStore(uOutput, vec2<i32>(i32(id.x), i32(id.y)), vec4<f32>(min(min(d0, d1), min(d2, d3)), 0.0, 0.0, 0.0));
    }
}
";

    /// <summary>D3D12 cs_5_0 (fxc; single exit avoids X4000). 上一级 2×2 取最小。</summary>
    const string SourceReduceHlsl = @"
cbuffer HiZParams : register(b0)
{
    float uSrcWidth;
    float uSrcHeight;
    float uDstWidth;
    float uDstHeight;
};

Texture2D<float> uSource : register(t0);
SamplerState uLinearClamp : register(s0);
RWTexture2D<float4> uOutput : register(u0);

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x < (uint)uDstWidth && id.y < (uint)uDstHeight)
    {
        // 采样点落在源的两行两列 texel 中心上，线性过滤在中心处等于点采样。
        float2 size = float2(uSrcWidth, uSrcHeight);
        float2 uv = (float2(id.xy) * 2.0 + 0.5) / size;
        float2 step = 1.0 / size;
        float d0 = uSource.SampleLevel(uLinearClamp, uv, 0.0);
        float d1 = uSource.SampleLevel(uLinearClamp, uv + float2(step.x, 0.0), 0.0);
        float d2 = uSource.SampleLevel(uLinearClamp, uv + float2(0.0, step.y), 0.0);
        float d3 = uSource.SampleLevel(uLinearClamp, uv + step, 0.0);
        uOutput[id.xy] = float4(min(min(d0, d1), min(d2, d3)), 0.0, 0.0, 0.0);
    }
}
";

    /// <summary>Vulkan GLSL 450.</summary>
    const string SourceReduceGlsl = @"#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(push_constant) uniform HiZParams
{
    float uSrcWidth;
    float uSrcHeight;
    float uDstWidth;
    float uDstHeight;
};

layout(binding = 1) uniform sampler2D uSource;
layout(binding = 2, r16f) uniform writeonly image2D uOutput;

void main()
{
    uvec2 id = gl_GlobalInvocationID.xy;
    if (id.x < uint(uDstWidth) && id.y < uint(uDstHeight))
    {
        vec2 size = vec2(uSrcWidth, uSrcHeight);
        vec2 uv = (vec2(id) * 2.0 + 0.5) / size;
        vec2 step = 1.0 / size;
        float d0 = textureLod(uSource, uv, 0.0).r;
        float d1 = textureLod(uSource, uv + vec2(step.x, 0.0), 0.0).r;
        float d2 = textureLod(uSource, uv + vec2(0.0, step.y), 0.0).r;
        float d3 = textureLod(uSource, uv + step, 0.0).r;
        imageStore(uOutput, ivec2(id), vec4(min(min(d0, d1), min(d2, d3)), 0.0, 0.0, 0.0));
    }
}
";

    /// <summary>Metal MSL kernel.</summary>
    const string SourceReduceMsl = @"
#include <metal_stdlib>
using namespace metal;

struct HiZParams
{
    float uSrcWidth;
    float uSrcHeight;
    float uDstWidth;
    float uDstHeight;
};

kernel void CSMain(
    constant HiZParams& params [[buffer(0)]],
    texture2d<float, access::sample> uSource [[texture(0)]],
    texture2d<float, access::write> uOutput [[texture(1)]],
    sampler uLinearClamp [[sampler(0)]],
    uint2 id [[thread_position_in_grid]])
{
    if (id.x < uint(params.uDstWidth) && id.y < uint(params.uDstHeight))
    {
        float2 size = float2(params.uSrcWidth, params.uSrcHeight);
        float2 uv = (float2(id) * 2.0 + 0.5) / size;
        float2 step = 1.0 / size;
        float d0 = uSource.sample(uLinearClamp, uv, level(0.0)).r;
        float d1 = uSource.sample(uLinearClamp, uv + float2(step.x, 0.0), level(0.0)).r;
        float d2 = uSource.sample(uLinearClamp, uv + float2(0.0, step.y), level(0.0)).r;
        float d3 = uSource.sample(uLinearClamp, uv + step, level(0.0)).r;
        uOutput.write(float4(min(min(d0, d1), min(d2, d3)), 0.0, 0.0, 0.0), id);
    }
}
";

    /// <summary>WebGPU WGSL.</summary>
    const string SourceReduceWgsl = @"
struct HiZParams
{
    uSrcWidth : f32,
    uSrcHeight : f32,
    uDstWidth : f32,
    uDstHeight : f32,
};

@group(0) @binding(0) var<uniform> params : HiZParams;
@group(0) @binding(1) var uSource : texture_2d<f32>;
@group(0) @binding(2) var uOutput : texture_storage_2d<rgba16float, write>;
@group(0) @binding(15) var uLinearClamp : sampler;

@compute @workgroup_size(8, 8, 1)
fn CSMain(@builtin(global_invocation_id) id : vec3<u32>)
{
    if (id.x < u32(params.uDstWidth) && id.y < u32(params.uDstHeight))
    {
        let size = vec2<f32>(params.uSrcWidth, params.uSrcHeight);
        let uv = (vec2<f32>(f32(id.x), f32(id.y)) * 2.0 + vec2<f32>(0.5)) / size;
        let texel = vec2<f32>(1.0) / size;
        let d0 = textureSampleLevel(uSource, uLinearClamp, uv, 0.0).r;
        let d1 = textureSampleLevel(uSource, uLinearClamp, uv + vec2<f32>(texel.x, 0.0), 0.0).r;
        let d2 = textureSampleLevel(uSource, uLinearClamp, uv + vec2<f32>(0.0, texel.y), 0.0).r;
        let d3 = textureSampleLevel(uSource, uLinearClamp, uv + texel, 0.0).r;
        textureStore(uOutput, vec2<i32>(i32(id.x), i32(id.y)), vec4<f32>(min(min(d0, d1), min(d2, d3)), 0.0, 0.0, 0.0));
    }
}
";
}
