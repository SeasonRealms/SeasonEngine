// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// D1 屏幕空间贴花的 MSL 变体。四端共用同一套语义与帧缓冲布局（见 <see cref="DecalMath"/>），
/// 这里只负责「读布局 + 重建 + 求值」，不定义布局。
///
/// 与另外三端的差异只有两处，都是 Metal 自己的形状：
/// <list type="bullet">
/// <item>帧缓冲直接以 <c>device const float4*</c> 传入，不需要 ByteAddressBuffer 那样的位重解释；</item>
/// <item>采样器写在源码里（<c>constexpr sampler</c>），不占绑定槽 —— 与主管线对阴影图的做法一致。</item>
/// </list>
///
/// 整份源码只编译一次：顶点与两个片元入口共享同一份辅助函数，Metal 的着色器编译器按源码字符串
/// 缓存库，所以同一条贴花管线只付一次编译代价。
/// </summary>
public static partial class DecalShaders
{
    /// <summary>
    /// 帧头与贴花数组的访问器，三个入口共用。
    ///
    /// 顶点端要局部→世界与世界→裁剪两套矩阵，像素端要反投影与像素尺寸，
    /// 拆开拼会让两个入口对同一份布局的解读出现分歧 —— 与 GLSL/HLSL 端的取舍相同。
    /// </summary>
    const string CoreMsl = """
#include <metal_stdlib>
using namespace metal;

// 帧缓冲布局见 DecalMath：每项一个 float4，下标与另外三端逐字相同。
float4 DecalF(device const float4* f, uint i) { return f[i]; }
float4 DecalTransform(device const float4* f, float4 p, uint i)
{
    return p.x * f[i] + p.y * f[i + 1u] + p.z * f[i + 2u] + p.w * f[i + 3u];
}
uint DecalOffset(uint index) { return 16u + index * 12u; }
float2 DecalSize(device const float4* f) { return f[9].xy; }

// 贴图用显式梯度的线性采样：盒的背面代理三角形比像素足迹大得多，隐式导数会在轮廓上取到盒外。
constexpr sampler decalLinearSampler(filter::linear, mip_filter::linear, address::clamp_to_edge);
""";

    /// <summary>
    /// 把投影盒的 36 个顶点按 <c>[[vertex_id]]</c> 算出来，不依赖顶点缓冲。
    ///
    /// 六个面统一成「从盒外看逆时针」，配合 <c>MTLCullMode.Front</c>（只留背面）让每个像素恰好命中一次 ——
    /// 这是整个方案不用模板、不用逐 draw 排序的前提。位拆分与绕序推导与 HLSL 端逐行对应。
    /// </summary>
    const string VertexMsl = """

vertex float4 VSMain(uint vid [[vertex_id]],
                     device const float4* decalFrame [[buffer(0)]],
                     constant uint4& decalParams [[buffer(1)]])
{
    // 每面 6 个顶点：两条三角形 (0,1,2)+(0,2,3)，再按 k 还原成四角编号。
    uint face = vid / 6u, t = vid - face * 6u;
    uint tri = t / 3u, c = t - tri * 3u;
    uint k = c == 0u ? 0u : (c == 1u ? (tri == 0u ? 1u : 2u) : (tri == 0u ? 2u : 3u));
    // face>>1 = 主轴（0=x,1=y,2=z），face&1 = 正负侧；负侧把 u 取反才能保持「盒外逆时针」。
    uint axis = face >> 1u, side = face & 1u;
    uint u = ((k ^ (k >> 1u)) & 1u) ^ (side ^ 1u);
    uint v = (k >> 1u) & 1u;
    float sa = side != 0u ? 0.5f : -0.5f, su = u != 0u ? 0.5f : -0.5f, sv = v != 0u ? 0.5f : -0.5f;
    float3 p = axis == 0u ? float3(sa, su, sv) : (axis == 1u ? float3(sv, sa, su) : float3(su, sv, sa));
    uint offset = DecalOffset(decalParams.x);
    return DecalTransform(decalFrame, DecalTransform(decalFrame, float4(p, 1.0f), offset + 4u), 4u);
}
""";

    /// <summary>
    /// 深度重建 + 贴花求值，要点与 HLSL 端一一对应：
    /// <list type="bullet">
    /// <item>UV 来自盒内归一化坐标，所以贴花是「沿着盒的局部 Y 投影」的，与表面朝向无关；</item>
    /// <item>导数一律在统一控制流里取（uv 与 world 都先算好），分支里的导数在 quad 边界是未定义行为；</item>
    /// <item>掠射淡出用深度导出的法线，不读 SceneNormal —— 写粗糙度时 SceneNormal 是颜色附件 1，不是采样源；</item>
    /// <item>输出约定：Modulate 输出「乘到目标上的因子」，Additive 输出自发光颜色，两者都靠固定功能混合完成，
    /// 片元着色器不读 SceneColor，因此 SceneColor 不需要同时是可采样资源。</item>
    /// </list>
    ///
    /// 帧缓冲字段：<c>wrap = (目标粗糙度, 是否改写粗糙度, 排序键, 是否有贴图)</c>，
    /// <c>tint.w</c> 是不透明度，<c>shape.zw</c> 是羽化与掠射阈值。
    /// </summary>
    const string PixelMsl = """

struct DecalPixelOut { float4 color [[color(0)]]; };
struct DecalRoughnessOut { float4 color [[color(0)]]; float4 normal [[color(1)]]; };

void DecalEval(device const float4* decalFrame, constant uint4& decalParams,
               texture2d<float> sceneDepth, texture2d<float> decalTexture, float2 pixel,
               thread float4& outColor, thread float& coverage, thread float& roughness, thread float& roughWeight)
{
    uint index = decalParams.x, offset = DecalOffset(index);
    // 本 Pass 没有深度附件，深度只是采样源，所以按整数坐标点采样，不受采样器过滤影响。
    float depth = sceneDepth.read(uint2(pixel), 0).x;
    float4 clip = float4(pixel / DecalSize(decalFrame) * float2(2.0f, -2.0f) + float2(-1.0f, 1.0f), depth, 1.0f);
    float4 w = DecalTransform(decalFrame, clip, 0u);
    float3 world = w.xyz / w.w;
    float3 local = DecalTransform(decalFrame, float4(world, 1.0f), offset).xyz;
    float4 tint = decalFrame[offset + 8u], shape = decalFrame[offset + 9u], wrap = decalFrame[offset + 10u];
    float2 uv = local.xz * 0.5f + 0.5f;
    float2 uvDx = dfdx(uv), uvDy = dfdy(uv);
    float3 n = normalize(cross(dfdx(world), dfdy(world)) + float3(1e-8f));
    float3 axis = normalize(decalFrame[offset + 5u].xyz);
    float shade = 0.0f;
    float3 albedo = tint.rgb;
    if (depth < 1.0f && all(abs(local) <= float3(1.0f)))
    {
        // 羽化：盒边界向内 shape.y 的比例；掠射：法线与投影轴夹角的余弦低于 1-shape.z 就淡出。
        float edge = shape.y > 0.0f
            ? saturate((1.0f - max(abs(local.x), abs(local.z))) / max(shape.y, 1e-3f)) : 1.0f;
        float grazing = shape.z > 0.0f ? smoothstep(1.0f - shape.z, 1.0f, abs(dot(n, axis))) : 1.0f;
        float alpha = 1.0f;
        if (wrap.w > 0.5f)
        {
            float4 texel = decalTexture.sample(decalLinearSampler, uv, gradient2d(uvDx, uvDy));
            albedo = albedo * texel.rgb;
            alpha = texel.a;
        }
        else
        {
            // 无纹理时的程序化图案：验证深度重建用，图案跟着地形起伏就说明重建对了。
            float checker = fmod(floor(uv.x * 4.0f) + floor(uv.y * 4.0f), 2.0f) < 0.5f ? 1.0f : 0.45f;
            albedo = albedo * checker * (0.55f + 0.45f * sin(length(local.xz) * 18.0f));
        }
        shade = saturate(edge * edge * (3.0f - 2.0f * edge)) * grazing * alpha;
    }
    coverage = shade * tint.w;
    // 不改粗糙度的贴花必须把附件 1 的源 alpha 置 0：那里的混合是 lerp(dst, src, src.a)，
    // 源 alpha 不为 0 就会把「没要求改粗糙度」的区域白白拉向 src。
    roughWeight = wrap.y > 0.5f ? coverage : 0.0f;
    roughness = wrap.y > 0.5f ? wrap.x : 0.0f;
    outColor = shape.x < 0.5f
        ? float4(mix(float3(1.0f), albedo, coverage), 1.0f)
        : float4(albedo * coverage * shape.w, 0.0f);
}

fragment DecalPixelOut PSMain(float4 position [[position]],
                              device const float4* decalFrame [[buffer(0)]],
                              constant uint4& decalParams [[buffer(1)]],
                              texture2d<float> sceneDepth [[texture(0)]],
                              texture2d<float> decalTexture [[texture(1)]])
{
    DecalPixelOut o;
    float coverage, roughness, roughWeight;
    DecalEval(decalFrame, decalParams, sceneDepth, decalTexture, position.xy,
        o.color, coverage, roughness, roughWeight);
    return o;
}

// s9：法线目标只开 B 通道写掩码，所以这里只需要把目标粗糙度送到 .z，
// 八面体法线（.xy）与金属度（.w）由附件里的原值保留。混合用 roughWeight 在 B 通道上插值。
fragment DecalRoughnessOut PSMainRoughness(float4 position [[position]],
                                           device const float4* decalFrame [[buffer(0)]],
                                           constant uint4& decalParams [[buffer(1)]],
                                           texture2d<float> sceneDepth [[texture(0)]],
                                           texture2d<float> decalTexture [[texture(1)]])
{
    DecalRoughnessOut o;
    float coverage, roughness, roughWeight;
    DecalEval(decalFrame, decalParams, sceneDepth, decalTexture, position.xy,
        o.color, coverage, roughness, roughWeight);
    o.normal = float4(0.0f, 0.0f, roughness, roughWeight);
    return o;
}
""";

    /// <summary>顶点与两个片元入口合成一份源码：后端按源码字符串缓存，整条管线只编译一次。</summary>
    public const string Msl = CoreMsl + VertexMsl + PixelMsl;
}
