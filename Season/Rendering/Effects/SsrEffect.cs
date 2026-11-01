// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

/// <summary>
/// P3 屏幕空间反射：从每个不透明像素的着色法线出发，沿反射方向在屏幕空间步进，
/// 用 <see cref="HiZEffect"/> 的金字塔整块跳过空区域，命中后取该点的场景色作为反射辐射。
///
/// 为什么是独立 compute 而不是主着色器里的一个采样：反射要用到"这一帧已经画完的不透明场景"，
/// 而主着色器正在写这张图，同一资源不能既当附件又当采样源。放到场景之后单独算，
/// 输入是一张完整的、可采样的不透明色，也就不需要为反射再拷一份全屏快照。
///
/// 输出是**辐射值**而不是最终颜色：rgb 是命中点的线性 HDR 场景色，a 是命中置信度（0 = 没命中）。
/// 加权交给 <see cref="SsrCompositePipeline"/> —— 场景色的合成与水面的直接采样用的是同一套权重
/// （F0 × (1-roughness)^4 × Fresnel），两边分开算会立刻在衔接处露出色差。
///
/// 步进分两段：粗段按屏幕像素步长自动挑一级金字塔（步长大就查更粗的级，一个 texel 覆盖一整片），
/// 射线深度还没越过该块的最近深度就整块跳过；越过之后在该块内用全分辨率深度细化，找第一个交点。
/// 粗段挑级而不是固定用最粗一级，是因为近处的短射线每步只有几个像素，查 1/16 的块等于什么都没跳过。
///
/// 依赖：<see cref="HiZEffect"/> 必须先注册且本帧已重建（<see cref="HiZEffect.Ready"/>），
/// 同时 SceneNormal 必须存在 —— 从深度现场重建的法线在轮廓两侧会翻转，反射会顺着轮廓糊成一片。
/// </summary>
public sealed class SsrEffect : ComputeEffect
{
    /// <summary>反射辐射输出：rgb = 线性 HDR 反射色，a = 命中置信度。</summary>
    public const string TextureName = "compute://ssr/reflection";

    /// <summary>帧缓冲只含 float4；矩阵按 System.Numerics 原始行序存放。</summary>
    public const int FrameVectorCount = 16;

    /// <summary>粗段的屏幕步数。步长由射线在屏幕上的总位移除以它得到，所以远处细长的射线同样是这么多步。</summary>
    const int CoarseSteps = 40;

    /// <summary>命中块内的细化步数。</summary>
    const int RefineSteps = 8;

    static SsrEffect? _active;

    readonly Vector4[] _frame = new Vector4[FrameVectorCount];
    readonly ComputeResourceRef[] _resources = new ComputeResourceRef[9];
    IGraphics? _graphics;
    StorageBuffer? _frameBuffer;
    ComputeKernel? _kernel;
    uint _width, _height;
    bool _ready;
    bool _unregistering;

    public override string Name => "ssr";

    /// <summary>AfterOpaque：只反射不透明场景，且必须在雾/水面之前把结果出版给后面的消费者。</summary>
    public override ComputePhase Phase => ComputePhase.AfterOpaque;

    public override ComputeScope Scope => ComputeScope.View;

    public override ViewFeature RequiredFeature => ViewFeature.Reflections;

    /// <summary>本帧是否出版了一份可用的反射纹理；合成与水面的采样都看它。</summary>
    public bool Ready => _ready;

    public static bool AnyReady => _active is { _ready: true };

    /// <summary>当前出版的反射纹理名；未就绪时为 null，调用方据此退回天空反射。</summary>
    public static string? PublishedTexture => _active is { _ready: true } ? TextureName : null;

    public override bool Initialize(IGraphics g)
    {
        if (_graphics != null) throw new InvalidOperationException("SSR 已经初始化。");
        if (_active != null) return false;
        if (RenderQuality.Current.ScreenSpaceReflections != SsrMode.HiZ) return false;
        var color = FrameSchedule.SceneColor;
        if (color == null || color.Desc.ColorFormat != RtFormat.Rgba16Float || color.Desc.SampleCount > 1) return false;
        if (FrameSchedule.SceneDepth == null || FrameSchedule.SceneNormal == null) return false;
        if (color.Desc.Width == 0 || color.Desc.Height == 0) return false;
        _kernel = g.CreateComputeKernel(new ComputeKernelDesc
        {
            Name = "ssrTrace",
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
                new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferRead },
                new ComputeBindingDesc { Type = ComputeBindingType.SampledTexture },
                new ComputeBindingDesc { Type = ComputeBindingType.DepthTexture },
                new ComputeBindingDesc { Type = ComputeBindingType.SampledTexture },
                new ComputeBindingDesc { Type = ComputeBindingType.SampledTexture },
                new ComputeBindingDesc { Type = ComputeBindingType.SampledTexture },
                new ComputeBindingDesc { Type = ComputeBindingType.SampledTexture },
                new ComputeBindingDesc { Type = ComputeBindingType.SampledTexture },
                new ComputeBindingDesc { Type = ComputeBindingType.StorageTextureWrite, StorageFormat = ComputeStorageFormat.Rgba16Float },
            },
        });
        if (_kernel == null) return false;
        _frameBuffer = g.CreateStorageBuffer(FrameVectorCount * 16);
        if (_frameBuffer == null)
        {
            Dispose();
            return false;
        }
        _graphics = g;
        _active = this;
        Allocate(g, color.Desc.Width, color.Desc.Height);
        return true;
    }

    void Allocate(IGraphics g, uint width, uint height)
    {
        _width = width;
        _height = height;
        _params = new Vector4(width, height, 0f, 0f);
        g.CreateComputeTexture(TextureName, width, height, ComputeStorageFormat.Rgba16Float);
        // 槽位表与绑定声明一一对应（Params 之后从 0 数）：0 帧缓冲、1 场景色、2 深度、
        // 3 法线、4..7 金字塔、8 输出。只建一次：纹理名是常量，尺寸变化时
        // CreateComputeTexture 会就地重建，引用同一名字的绑定不需要跟着换。
        _resources[0] = _frameBuffer!;
        _resources[1] = FrameSchedule.SceneColor!;
        _resources[2] = FrameSchedule.SceneDepth!;
        _resources[3] = FrameSchedule.SceneNormal!;
        for (int level = 0; level < HiZEffect.LevelCount; level++)
            _resources[4 + level] = HiZEffect.LevelName(level);
        _resources[8] = TextureName;
    }

    public override void OnResize(IGraphics g)
    {
        if (!ReferenceEquals(g, _graphics)) return;
        var color = FrameSchedule.SceneColor;
        if (color == null || color.Desc.Width == 0 || color.Desc.Height == 0) return;
        Allocate(g, color.Desc.Width, color.Desc.Height);
    }

    public override void Record(IGraphics g)
    {
        _ready = false;
        if (!ReferenceEquals(g, _graphics) || _kernel == null || _frameBuffer == null) return;
        if (!HiZEffect.Ready || RenderQuality.Current.ScreenSpaceReflections != SsrMode.HiZ) return;
        var color = FrameSchedule.SceneColor;
        var depth = FrameSchedule.SceneDepth;
        var normal = FrameSchedule.SceneNormal;
        if (color == null || depth == null || normal == null) return;
        if (color.Desc.Width != _width || color.Desc.Height != _height) return;
        var vp = FrameSchedule.UnjitteredRenderViewProjection;
        if (!Matrix4x4.Invert(vp, out var inverse)) return;
        var camera = RenderView.Current.Camera;
        float tanHalf = MathF.Tan(camera.FovY * 0.5f);
        float aspect = camera.Aspect > 0f ? camera.Aspect : (float)_width / _height;
        if (!(tanHalf > 0f) || !(aspect > 0f) || !(camera.Far > camera.Near)) return;

        // 行序写入：0..3 VP、4..7 逆 VP、8..11 相机与重建参数、12 步进参数。
        MemoryMarshal.Cast<Matrix4x4, Vector4>(MemoryMarshal.CreateReadOnlySpan(ref vp, 1)).CopyTo(_frame);
        MemoryMarshal.Cast<Matrix4x4, Vector4>(MemoryMarshal.CreateReadOnlySpan(ref inverse, 1)).CopyTo(_frame.AsSpan(4));
        _frame[8] = new Vector4(camera.Position, 1f);
        _frame[9] = new Vector4(_width, _height, 1f / _width, 1f / _height);
        _frame[10] = new Vector4(camera.Near, camera.Far, tanHalf, aspect);
        _frame[11] = new Vector4(MathF.Max(0.01f, RenderQuality.Current.SsrMaxDistance),
            MathF.Max(0.01f, RenderQuality.Current.SsrThickness), RenderQuality.Current.SsrIntensity,
            FrameSchedule.SkyViewTexture != null ? 1f : 0f);
        _frame[12] = new Vector4((float)(FrameSchedule.FrameIndex & 0xFFFFUL), CoarseSteps, RefineSteps, 0f);
        if (!g.TryUpdateStorageBuffer(_frameBuffer, MemoryMarshal.AsBytes(_frame.AsSpan()))) return;
        if (!g.TryDispatchCompute(new ComputeDispatchArgs
        {
            Kernel = _kernel,
            Params = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref _params, 1)),
            Resources = _resources,
            GroupsX = (_width + 7) / 8,
            GroupsY = (_height + 7) / 8,
            GroupsZ = 1,
        })) return;
        _ready = true;
    }

    Vector4 _params;

    public override void OnUnregistered() => Dispose();

    public void Dispose()
    {
        if (_unregistering) return;
        _unregistering = true;
        var graphics = _graphics;
        _graphics = null;
        _ready = false;
        if (graphics != null) FrameSchedule.UnregisterCompute(this);
        _kernel?.Dispose();
        _kernel = null;
        _frameBuffer?.Dispose();
        _frameBuffer = null;
        Array.Clear(_resources);
        if (ReferenceEquals(_active, this)) _active = null;
        _unregistering = false;
    }

    // Shader sources (single source of truth; slots follow the class-level binding
    // convention; workgroup is fixed at 8x8x1).

    /// <summary>D3D12 cs_5_0 (fxc; single exit avoids X4000).</summary>
    /// <remarks>
    /// 不能写成 const：源码里的步数是把 int 常量拼进字符串，而「字符串 + int」不是编译期常量。
    /// </remarks>
    static readonly string SourceHlsl = @"
cbuffer SsrParams : register(b0)
{
    float uDstWidth;
    float uDstHeight;
    float uPad0;
    float uPad1;
};

ByteAddressBuffer uFrame : register(t0);
Texture2D<float4> uScene : register(t1);
SamplerState uLinearClamp : register(s0);
Texture2D<float> uDepth : register(t2);
Texture2D<float4> uNormal : register(t3);
Texture2D<float> uHz0 : register(t4);
Texture2D<float> uHz1 : register(t5);
Texture2D<float> uHz2 : register(t6);
Texture2D<float> uHz3 : register(t7);
RWTexture2D<float4> uOutput : register(u0);

// 帧缓冲走 ByteAddressBuffer：Load4 的偏移是字节，i * 16 就是 float4 的下标 i。
float4 FrameF(uint i)
{
    return asfloat(uFrame.Load4(i * 16u));
}

// 行序矩阵：与其余三端同形，绝不能写成 mul(M, p)——System.Numerics 是按行存放的。
float4 Transform(float4 p, uint offset)
{
    return p.x * FrameF(offset) + p.y * FrameF(offset + 1) + p.z * FrameF(offset + 2) + p.w * FrameF(offset + 3);
}

float3 OctDecode(float2 e)
{
    float3 n = float3(e, 1.0 - abs(e.x) - abs(e.y));
    if (n.z < 0.0) n.xy = (1.0 - abs(n.yx)) * float2(n.x >= 0.0 ? 1.0 : -1.0, n.y >= 0.0 ? 1.0 : -1.0);
    return normalize(n);
}

// 视空间前向深度（LH + [0,1]），与 GTAO 的重建公式同源。
float Linearize(float d)
{
    float near = FrameF(10u).x, far = FrameF(10u).y;
    return near * far / (far - d * (far - near));
}

// 单一出口 + 显式 .x：fxc 4.x 对「模板化 SampleLevel 返回值隐式转 float + 多 return」的
// 流分析会错报 X4000（use of potentially uninitialized variable）。
float HiZ(float2 uv, int level)
{
    float value;
    if (level <= 0) value = uHz0.SampleLevel(uLinearClamp, uv, 0.0).x;
    else if (level == 1) value = uHz1.SampleLevel(uLinearClamp, uv, 0.0).x;
    else if (level == 2) value = uHz2.SampleLevel(uLinearClamp, uv, 0.0).x;
    else value = uHz3.SampleLevel(uLinearClamp, uv, 0.0).x;
    return value;
}

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x < (uint)uDstWidth && id.y < (uint)uDstHeight)
    {
        float2 size = float2(uDstWidth, uDstHeight);
        float2 uv = (float2(id.xy) + 0.5) / size;
        float4 result = float4(0.0, 0.0, 0.0, 0.0);
        float depth = uDepth.Load(int3(id.xy, 0));

        // 天空与背景不产生反射：深度停在远平面，法线目标里也没有可用的面。
        if (depth < 1.0)
        {
            float4 packed = uNormal.SampleLevel(uLinearClamp, uv, 0.0);
            float roughness = saturate(packed.z);
            float3 N = OctDecode(packed.xy);

            // 粗糙面上屏幕空间反射本来就会糊成一片，还容易被单像素的误命中放大成噪点；
            // 粗糙面的环境能量由 SH9 漫反射项承担，这里直接不出反射。
            if (roughness < 0.6)
            {
                // 世界坐标重建：UV 的 Y 向下，所以 NDC 的 Y 要翻回来。
                float2 ndc = float2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
                float4 world = Transform(float4(ndc, depth, 1.0), 4u);
                float3 P = world.xyz / world.w;
                float3 V = normalize(FrameF(8u).xyz - P);
                float3 R = reflect(-V, N);

                // 沿法线偏移：不偏就直接命中自己所在的平面，整屏会反射成同一个颜色。
                float bias = max(0.02, length(FrameF(8u).xyz - P) * 0.002);
                float3 origin = P + N * bias + R * bias;
                float maxDistance = FrameF(11u).x;
                float4 clip0 = Transform(float4(origin, 1.0), 0u);
                float4 clip1 = Transform(float4(origin + R * maxDistance, 1.0), 0u);

                // 射线走不到的地方（相机背后）没有屏幕信息可采，缩短到近平面之前。
                float endT = 1.0;
                if (clip1.w <= 0.001)
                {
                    float denom = clip1.w - clip0.w;
                    endT = denom < -0.0001 ? saturate(-clip0.w / denom) : 0.0;
                    clip1 = Transform(float4(origin + R * (maxDistance * endT), 1.0), 0u);
                }

                if (clip0.w > 0.001 && clip1.w > 0.001 && endT > 0.001)
                {
                    float2 uv0 = clip0.xy / clip0.w * float2(0.5, -0.5) + 0.5;
                    float2 uv1 = clip1.xy / clip1.w * float2(0.5, -0.5) + 0.5;
                    float z0 = clip0.z / clip0.w;
                    float z1 = clip1.z / clip1.w;
                    float2 delta = uv1 - uv0;

                    // 每步跨多少像素决定查哪一级金字塔：短射线用细级，否则粗块永远命中不了。
                    float pixels = length(delta * size) / (float)" + CoarseSteps + @";
                    int level = 3;
                    if (pixels < 12.0) level = 2;
                    if (pixels < 6.0) level = 1;
                    if (pixels < 3.0) level = 0;

                    float2 stepUv = delta / (float)" + CoarseSteps + @";
                    float2 coarse = uv0;
                    float coarseZ = z0;
                    [loop]
                    for (int i = 1; i <= " + CoarseSteps + @"; i++)
                    {
                        float2 next = uv0 + delta * ((float)i / (float)" + CoarseSteps + @");
                        float nextZ = lerp(z0, z1, (float)i / (float)" + CoarseSteps + @");
                        // 出屏即终止：屏幕外的信息不存在，继续走只会采到钳制后的边缘像素。
                        if (next.x < 0.0 || next.x > 1.0 || next.y < 0.0 || next.y > 1.0) break;
                        float tile = HiZ(next, level);
                        if (nextZ < tile)
                        {
                            coarse = next;
                            coarseZ = nextZ;
                            continue;
                        }
                        // 射线越过了这一块的最近表面：在 (上一节点, 当前节点) 内逐点逼近真实交点。
                        float2 from = coarse;
                        float fromZ = coarseZ;
                        float2 to = next;
                        float toZ = nextZ;
                        [loop]
                        for (int j = 1; j <= " + RefineSteps + @"; j++)
                        {
                            float t = (float)j / (float)" + RefineSteps + @";
                            float2 suv = lerp(from, to, t);
                            float rayZ = lerp(fromZ, toZ, t);
                            int2 coord = clamp(int2(suv * size), int2(0, 0), int2((int)uDstWidth - 1, (int)uDstHeight - 1));
                            float sceneD = uDepth.Load(int3(coord, 0));
                            if (sceneD >= 1.0) continue;
                            // 厚度判定放在线性深度上：NDC 深度越远越挤，同一个阈值在近处和远处差出几个数量级。
                            float rayView = Linearize(rayZ);
                            float sceneView = Linearize(sceneD);
                            if (rayView >= sceneView && rayView - sceneView <= FrameF(11u).y)
                            {
                                float2 hitUv = saturate(suv);
                                float edge = min(min(hitUv.x, 1.0 - hitUv.x), min(hitUv.y, 1.0 - hitUv.y));
                                float fade = saturate(edge / 0.06);
                                fade *= saturate(1.0 - (float)i / (float)" + CoarseSteps + @");
                                result = float4(uScene.SampleLevel(uLinearClamp, hitUv, 0.0).rgb, fade);
                                break;
                            }
                        }
                        break;
                    }
                }
            }
        }
        uOutput[id.xy] = result;
    }
}
";

    /// <summary>Vulkan GLSL 450 (glslang -> SPIR-V; Params use push_constant, binding follows declaration order).</summary>
    static readonly string SourceGlsl = @"#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(push_constant) uniform SsrParams
{
    float uDstWidth;
    float uDstHeight;
    float uPad0;
    float uPad1;
};

layout(binding = 1, std430) readonly buffer Frame { vec4 data[]; } uFrame;
layout(binding = 2) uniform sampler2D uScene;
layout(binding = 3) uniform sampler2D uDepth;
layout(binding = 4) uniform sampler2D uNormal;
layout(binding = 5) uniform sampler2D uHz0;
layout(binding = 6) uniform sampler2D uHz1;
layout(binding = 7) uniform sampler2D uHz2;
layout(binding = 8) uniform sampler2D uHz3;
layout(binding = 9, rgba16f) uniform writeonly image2D uOutput;

vec4 Transform(vec4 p, int offset)
{
    return p.x * uFrame.data[offset] + p.y * uFrame.data[offset + 1] + p.z * uFrame.data[offset + 2] + p.w * uFrame.data[offset + 3];
}

vec3 OctDecode(vec2 e)
{
    vec3 n = vec3(e, 1.0 - abs(e.x) - abs(e.y));
    if (n.z < 0.0) n.xy = (1.0 - abs(n.yx)) * vec2(n.x >= 0.0 ? 1.0 : -1.0, n.y >= 0.0 ? 1.0 : -1.0);
    return normalize(n);
}

float Linearize(float d)
{
    float near = uFrame.data[10].x, far = uFrame.data[10].y;
    return near * far / (far - d * (far - near));
}

float HiZ(vec2 uv, int level)
{
    if (level <= 0) return textureLod(uHz0, uv, 0.0).r;
    if (level == 1) return textureLod(uHz1, uv, 0.0).r;
    if (level == 2) return textureLod(uHz2, uv, 0.0).r;
    return textureLod(uHz3, uv, 0.0).r;
}

void main()
{
    uvec2 id = gl_GlobalInvocationID.xy;
    if (id.x < uint(uDstWidth) && id.y < uint(uDstHeight))
    {
        vec2 size = vec2(uDstWidth, uDstHeight);
        vec2 uv = (vec2(id) + 0.5) / size;
        vec4 result = vec4(0.0);
        float depth = texelFetch(uDepth, ivec2(id), 0).r;

        if (depth < 1.0)
        {
            vec4 packed = textureLod(uNormal, uv, 0.0);
            float roughness = clamp(packed.z, 0.0, 1.0);
            vec3 N = OctDecode(packed.xy);

            if (roughness < 0.6)
            {
                vec2 ndc = vec2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
                vec4 world = Transform(vec4(ndc, depth, 1.0), 4);
                vec3 P = world.xyz / world.w;
                vec3 V = normalize(uFrame.data[8].xyz - P);
                vec3 R = reflect(-V, N);

                float bias = max(0.02, length(uFrame.data[8].xyz - P) * 0.002);
                vec3 origin = P + N * bias + R * bias;
                float maxDistance = uFrame.data[11].x;
                vec4 clip0 = Transform(vec4(origin, 1.0), 0);
                vec4 clip1 = Transform(vec4(origin + R * maxDistance, 1.0), 0);

                float endT = 1.0;
                if (clip1.w <= 0.001)
                {
                    float denom = clip1.w - clip0.w;
                    endT = denom < -0.0001 ? clamp(-clip0.w / denom, 0.0, 1.0) : 0.0;
                    clip1 = Transform(vec4(origin + R * (maxDistance * endT), 1.0), 0);
                }

                if (clip0.w > 0.001 && clip1.w > 0.001 && endT > 0.001)
                {
                    vec2 uv0 = clip0.xy / clip0.w * vec2(0.5, -0.5) + 0.5;
                    vec2 uv1 = clip1.xy / clip1.w * vec2(0.5, -0.5) + 0.5;
                    float z0 = clip0.z / clip0.w;
                    float z1 = clip1.z / clip1.w;
                    vec2 delta = uv1 - uv0;

                    float pixels = length(delta * size) / float(" + CoarseSteps + @");
                    int level = 3;
                    if (pixels < 12.0) level = 2;
                    if (pixels < 6.0) level = 1;
                    if (pixels < 3.0) level = 0;

                    vec2 coarse = uv0;
                    float coarseZ = z0;
                    for (int i = 1; i <= " + CoarseSteps + @"; i++)
                    {
                        float t = float(i) / float(" + CoarseSteps + @");
                        vec2 next = uv0 + delta * t;
                        float nextZ = mix(z0, z1, t);
                        if (next.x < 0.0 || next.x > 1.0 || next.y < 0.0 || next.y > 1.0) break;
                        float tile = HiZ(next, level);
                        if (nextZ < tile)
                        {
                            coarse = next;
                            coarseZ = nextZ;
                            continue;
                        }
                        vec2 from = coarse;
                        float fromZ = coarseZ;
                        for (int j = 1; j <= " + RefineSteps + @"; j++)
                        {
                            float tj = float(j) / float(" + RefineSteps + @");
                            vec2 suv = mix(from, next, tj);
                            float rayZ = mix(fromZ, nextZ, tj);
                            ivec2 coord = clamp(ivec2(suv * size), ivec2(0), ivec2(int(uint(uDstWidth)) - 1, int(uint(uDstHeight)) - 1));
                            float sceneD = texelFetch(uDepth, coord, 0).r;
                            if (sceneD >= 1.0) continue;
                            float rayView = Linearize(rayZ);
                            float sceneView = Linearize(sceneD);
                            if (rayView >= sceneView && rayView - sceneView <= uFrame.data[11].y)
                            {
                                vec2 hitUv = clamp(suv, vec2(0.0), vec2(1.0));
                                float edge = min(min(hitUv.x, 1.0 - hitUv.x), min(hitUv.y, 1.0 - hitUv.y));
                                float fade = clamp(edge / 0.06, 0.0, 1.0);
                                fade *= clamp(1.0 - float(i) / float(" + CoarseSteps + @"), 0.0, 1.0);
                                result = vec4(textureLod(uScene, hitUv, 0.0).rgb, fade);
                                break;
                            }
                        }
                        break;
                    }
                }
            }
        }
        imageStore(uOutput, ivec2(id), result);
    }
}
";

    /// <summary>Metal MSL kernel.</summary>
    static readonly string SourceMsl = @"
#include <metal_stdlib>
using namespace metal;

struct SsrParams
{
    float uDstWidth;
    float uDstHeight;
    float uPad0;
    float uPad1;
};

float4 Transform(const device float4* frame, float4 p, int offset)
{
    return p.x * frame[offset] + p.y * frame[offset + 1] + p.z * frame[offset + 2] + p.w * frame[offset + 3];
}

float3 OctDecode(float2 e)
{
    float3 n = float3(e, 1.0 - abs(e.x) - abs(e.y));
    if (n.z < 0.0) n.xy = (1.0 - abs(n.yx)) * float2(n.x >= 0.0 ? 1.0 : -1.0, n.y >= 0.0 ? 1.0 : -1.0);
    return normalize(n);
}

float Linearize(const device float4* frame, float d)
{
    float near = frame[10].x, far = frame[10].y;
    return near * far / (far - d * (far - near));
}

float HiZ(texture2d<float, access::sample> hz0, texture2d<float, access::sample> hz1,
    texture2d<float, access::sample> hz2, texture2d<float, access::sample> hz3,
    sampler sm, float2 uv, int level)
{
    if (level <= 0) return hz0.sample(sm, uv, level(0.0)).r;
    if (level == 1) return hz1.sample(sm, uv, level(0.0)).r;
    if (level == 2) return hz2.sample(sm, uv, level(0.0)).r;
    return hz3.sample(sm, uv, level(0.0)).r;
}

kernel void CSMain(
    constant SsrParams& params [[buffer(0)]],
    const device float4* frame [[buffer(1)]],
    texture2d<float, access::sample> uScene [[texture(0)]],
    depth2d<float, access::read> uDepth [[texture(1)]],
    texture2d<float, access::sample> uNormal [[texture(2)]],
    texture2d<float, access::sample> uHz0 [[texture(3)]],
    texture2d<float, access::sample> uHz1 [[texture(4)]],
    texture2d<float, access::sample> uHz2 [[texture(5)]],
    texture2d<float, access::sample> uHz3 [[texture(6)]],
    texture2d<float, access::write> uOutput [[texture(7)]],
    sampler uLinearClamp [[sampler(0)]],
    uint2 id [[thread_position_in_grid]])
{
    if (id.x < uint(params.uDstWidth) && id.y < uint(params.uDstHeight))
    {
        float2 size = float2(params.uDstWidth, params.uDstHeight);
        float2 uv = (float2(id) + 0.5) / size;
        float4 result = float4(0.0);
        float depth = uDepth.read(id);

        if (depth < 1.0)
        {
            float4 packed = uNormal.sample(uLinearClamp, uv, level(0.0));
            float roughness = saturate(packed.z);
            float3 N = OctDecode(packed.xy);

            if (roughness < 0.6)
            {
                float2 ndc = float2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
                float4 world = Transform(frame, float4(ndc, depth, 1.0), 4);
                float3 P = world.xyz / world.w;
                float3 V = normalize(frame[8].xyz - P);
                float3 R = reflect(-V, N);

                float bias = max(0.02, length(frame[8].xyz - P) * 0.002);
                float3 origin = P + N * bias + R * bias;
                float maxDistance = frame[11].x;
                float4 clip0 = Transform(frame, float4(origin, 1.0), 0);
                float4 clip1 = Transform(frame, float4(origin + R * maxDistance, 1.0), 0);

                float endT = 1.0;
                if (clip1.w <= 0.001)
                {
                    float denom = clip1.w - clip0.w;
                    endT = denom < -0.0001 ? saturate(-clip0.w / denom) : 0.0;
                    clip1 = Transform(frame, float4(origin + R * (maxDistance * endT), 1.0), 0);
                }

                if (clip0.w > 0.001 && clip1.w > 0.001 && endT > 0.001)
                {
                    float2 uv0 = clip0.xy / clip0.w * float2(0.5, -0.5) + 0.5;
                    float2 uv1 = clip1.xy / clip1.w * float2(0.5, -0.5) + 0.5;
                    float z0 = clip0.z / clip0.w;
                    float z1 = clip1.z / clip1.w;
                    float2 delta = uv1 - uv0;

                    float pixels = length(delta * size) / float(" + CoarseSteps + @");
                    int level = 3;
                    if (pixels < 12.0) level = 2;
                    if (pixels < 6.0) level = 1;
                    if (pixels < 3.0) level = 0;

                    float2 coarse = uv0;
                    float coarseZ = z0;
                    for (int i = 1; i <= " + CoarseSteps + @"; i++)
                    {
                        float t = float(i) / float(" + CoarseSteps + @");
                        float2 next = uv0 + delta * t;
                        float nextZ = mix(z0, z1, t);
                        if (next.x < 0.0 || next.x > 1.0 || next.y < 0.0 || next.y > 1.0) break;
                        float tile = HiZ(uHz0, uHz1, uHz2, uHz3, uLinearClamp, next, level);
                        if (nextZ < tile)
                        {
                            coarse = next;
                            coarseZ = nextZ;
                            continue;
                        }
                        float2 from = coarse;
                        float fromZ = coarseZ;
                        for (int j = 1; j <= " + RefineSteps + @"; j++)
                        {
                            float tj = float(j) / float(" + RefineSteps + @");
                            float2 suv = mix(from, next, tj);
                            float rayZ = mix(fromZ, nextZ, tj);
                            int2 coord = clamp(int2(suv * size), int2(0, 0), int2(int(params.uDstWidth) - 1, int(params.uDstHeight) - 1));
                            float sceneD = uDepth.read(uint2(coord));
                            if (sceneD >= 1.0) continue;
                            float rayView = Linearize(frame, rayZ);
                            float sceneView = Linearize(frame, sceneD);
                            if (rayView >= sceneView && rayView - sceneView <= frame[11].y)
                            {
                                float2 hitUv = saturate(suv);
                                float edge = min(min(hitUv.x, 1.0 - hitUv.x), min(hitUv.y, 1.0 - hitUv.y));
                                float fade = saturate(edge / 0.06);
                                fade *= saturate(1.0 - float(i) / float(" + CoarseSteps + @"));
                                result = float4(uScene.sample(uLinearClamp, hitUv, level(0.0)).rgb, fade);
                                break;
                            }
                        }
                        break;
                    }
                }
            }
        }
        uOutput.write(result, id);
    }
}
";

    /// <summary>WebGPU WGSL. 存储格式按引擎的窄格式回退链固定写 rgba16float。</summary>
    static readonly string SourceWgsl = @"
struct SsrParams
{
    uDstWidth : f32,
    uDstHeight : f32,
    uPad0 : f32,
    uPad1 : f32,
};

@group(0) @binding(0) var<uniform> params : SsrParams;
@group(0) @binding(1) var<storage, read> uFrame : array<vec4<f32>>;
@group(0) @binding(2) var uScene : texture_2d<f32>;
@group(0) @binding(3) var uDepth : texture_depth_2d;
@group(0) @binding(4) var uNormal : texture_2d<f32>;
@group(0) @binding(5) var uHz0 : texture_2d<f32>;
@group(0) @binding(6) var uHz1 : texture_2d<f32>;
@group(0) @binding(7) var uHz2 : texture_2d<f32>;
@group(0) @binding(8) var uHz3 : texture_2d<f32>;
@group(0) @binding(9) var uOutput : texture_storage_2d<rgba16float, write>;
@group(0) @binding(15) var uLinearClamp : sampler;

fn Transform(p : vec4<f32>, offset : i32) -> vec4<f32>
{
    return p.x * uFrame[offset] + p.y * uFrame[offset + 1] + p.z * uFrame[offset + 2] + p.w * uFrame[offset + 3];
}

fn OctDecode(e : vec2<f32>) -> vec3<f32>
{
    var n = vec3<f32>(e, 1.0 - abs(e.x) - abs(e.y));
    if (n.z < 0.0) {
        n = vec3<f32>((1.0 - abs(n.yx)) * vec2<f32>(select(-1.0, 1.0, n.x >= 0.0), select(-1.0, 1.0, n.y >= 0.0)), n.z);
    }
    return normalize(n);
}

fn Linearize(d : f32) -> f32
{
    let near = uFrame[10].x;
    let far = uFrame[10].y;
    return near * far / (far - d * (far - near));
}

fn HiZ(uv : vec2<f32>, level : i32) -> f32
{
    if (level <= 0) { return textureSampleLevel(uHz0, uLinearClamp, uv, 0.0).r; }
    if (level == 1) { return textureSampleLevel(uHz1, uLinearClamp, uv, 0.0).r; }
    if (level == 2) { return textureSampleLevel(uHz2, uLinearClamp, uv, 0.0).r; }
    return textureSampleLevel(uHz3, uLinearClamp, uv, 0.0).r;
}

@compute @workgroup_size(8, 8, 1)
fn CSMain(@builtin(global_invocation_id) id : vec3<u32>)
{
    if (id.x < u32(params.uDstWidth) && id.y < u32(params.uDstHeight))
    {
        let size = vec2<f32>(params.uDstWidth, params.uDstHeight);
        let uv = (vec2<f32>(f32(id.x), f32(id.y)) + vec2<f32>(0.5)) / size;
        var result = vec4<f32>(0.0, 0.0, 0.0, 0.0);
        let coord = vec2<i32>(i32(id.x), i32(id.y));
        let depth = textureLoad(uDepth, coord, 0);

        if (depth < 1.0)
        {
            let packed = textureSampleLevel(uNormal, uLinearClamp, uv, 0.0);
            let roughness = saturate(packed.z);
            let N = OctDecode(packed.xy);

            if (roughness < 0.6)
            {
                let ndc = vec2<f32>(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
                let world = Transform(vec4<f32>(ndc, depth, 1.0), 4);
                let P = world.xyz / world.w;
                let V = normalize(uFrame[8].xyz - P);
                let R = reflect(-V, N);

                let bias = max(0.02, length(uFrame[8].xyz - P) * 0.002);
                let origin = P + N * bias + R * bias;
                let maxDistance = uFrame[11].x;
                let clip0 = Transform(vec4<f32>(origin, 1.0), 0);
                var clip1 = Transform(vec4<f32>(origin + R * maxDistance, 1.0), 0);

                var endT = 1.0;
                if (clip1.w <= 0.001)
                {
                    let denom = clip1.w - clip0.w;
                    endT = select(0.0, saturate(-clip0.w / denom), denom < -0.0001);
                    clip1 = Transform(vec4<f32>(origin + R * (maxDistance * endT), 1.0), 0);
                }

                if (clip0.w > 0.001 && clip1.w > 0.001 && endT > 0.001)
                {
                    let uv0 = clip0.xy / clip0.w * vec2<f32>(0.5, -0.5) + vec2<f32>(0.5);
                    let uv1 = clip1.xy / clip1.w * vec2<f32>(0.5, -0.5) + vec2<f32>(0.5);
                    let z0 = clip0.z / clip0.w;
                    let z1 = clip1.z / clip1.w;
                    let delta = uv1 - uv0;

                    let pixels = length(delta * size) / f32(" + CoarseSteps + @");
                    var level : i32 = 3;
                    if (pixels < 12.0) { level = 2; }
                    if (pixels < 6.0) { level = 1; }
                    if (pixels < 3.0) { level = 0; }

                    var coarse = uv0;
                    var coarseZ = z0;
                    for (var i : i32 = 1; i <= " + CoarseSteps + @"; i = i + 1)
                    {
                        let t = f32(i) / f32(" + CoarseSteps + @");
                        let next = uv0 + delta * t;
                        let nextZ = mix(z0, z1, t);
                        if (next.x < 0.0 || next.x > 1.0 || next.y < 0.0 || next.y > 1.0) { break; }
                        let tile = HiZ(next, level);
                        if (nextZ < tile)
                        {
                            coarse = next;
                            coarseZ = nextZ;
                            continue;
                        }
                        let from = coarse;
                        let fromZ = coarseZ;
                        for (var j : i32 = 1; j <= " + RefineSteps + @"; j = j + 1)
                        {
                            let tj = f32(j) / f32(" + RefineSteps + @");
                            let suv = mix(from, next, tj);
                            let rayZ = mix(fromZ, nextZ, tj);
                            let rc = clamp(vec2<i32>(suv * size), vec2<i32>(0, 0),
                                vec2<i32>(i32(u32(params.uDstWidth)) - 1, i32(u32(params.uDstHeight)) - 1));
                            let sceneD = textureLoad(uDepth, rc, 0);
                            if (sceneD >= 1.0) { continue; }
                            let rayView = Linearize(rayZ);
                            let sceneView = Linearize(sceneD);
                            if (rayView >= sceneView && rayView - sceneView <= uFrame[11].y)
                            {
                                let hitUv = clamp(suv, vec2<f32>(0.0), vec2<f32>(1.0));
                                let edge = min(min(hitUv.x, 1.0 - hitUv.x), min(hitUv.y, 1.0 - hitUv.y));
                                var fade = saturate(edge / 0.06);
                                fade = fade * saturate(1.0 - f32(i) / f32(" + CoarseSteps + @"));
                                result = vec4<f32>(textureSampleLevel(uScene, uLinearClamp, hitUv, 0.0).rgb, fade);
                                break;
                            }
                        }
                        break;
                    }
                }
            }
        }
        textureStore(uOutput, coord, result);
    }
}
";
}
