// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// P2 水面材质的 MSL 变体。四端共用同一套语义与帧缓冲布局（见 <see cref="WaterMath"/>），
/// 这里只负责「读布局 + 求值」，不定义布局。
///
/// 与另外三端的差异只有两处，都是 Metal 自己的形状：
/// <list type="bullet">
/// <item>帧缓冲直接以 <c>device const float4*</c> 传入，不需要 ByteAddressBuffer 那样的位重解释；</item>
/// <item>采样器写在源码里（<c>constexpr sampler</c>），不占绑定槽 —— 与贴花和主管线对阴影图的做法一致。</item>
/// </list>
///
/// 深度必须是 <c>depth2d&lt;float&gt;</c> 才能 <c>read</c>：软件深度测试要的是点取样，
/// 而 depth2d 本来就不支持过滤采样，与需求正好吻合。
/// </summary>
public static partial class WaterShaders
{
    /// <summary>帧头与水面数组的访问器，两个入口共用。</summary>
    const string CoreMsl = """
#include <metal_stdlib>
using namespace metal;

struct WaterVertex {
    float4 position [[position]];
    float3 world;
    // P4：顶点位移后的解析法线（世界空间）。Gerstner 关闭时恒为 (0,1,0)。
    float3 normal;
    // 整数插值不经过线性滤波，显式标 flat 免得依赖编译器默认行为。
    uint slot [[flat]];
};

float4 WaterF(const device float4* data, int i) { return data[i]; }
int WaterOffset(uint index) { return 16 + int(index) * 16; }
float4 WaterTransform(const device float4* data, float4 p, int i) {
    return p.x * data[i] + p.y * data[i+1] + p.z * data[i+2] + p.w * data[i+3];
}
""";

    /// <summary>
    /// P4 顶点端：分块网格展开 + Gerstner 位移。与 HLSL 版逐句同序；唯一的语法差异是
    /// <c>[[instance_id]]</c> 而不是 <c>SV_InstanceID</c>，以及 <c>thread</c> 引用充当 out 参数。
    /// </summary>
    const string VertexMsl = """

// 六个二维角点，按 quad 的两个三角形排列。单一出口，与 HLSL 版同理。
float2 WaterCorner(uint id) {
    float2 corner = float2(0.0f, 0.0f);
    if (id == 1u) corner = float2(1.0f, 0.0f);
    else if (id == 2u) corner = float2(1.0f, 1.0f);
    else if (id == 3u) corner = float2(0.0f, 0.0f);
    else if (id == 4u) corner = float2(1.0f, 1.0f);
    else if (id == 5u) corner = float2(0.0f, 1.0f);
    return corner;
}

// tile 内的 [0,1] 网格坐标 → 方形的 [-0.5,0.5] 平面坐标。除数钳到 1，与另外两端同理。
float2 WaterLocalUv(uint id, uint tile, uint tilesX, uint tilesZ, uint subdiv) {
    if (subdiv == 0u) return WaterCorner(id) - 0.5f;
    uint nx = max(tilesX, 1u);
    uint nz = max(tilesZ, 1u);
    uint sd = max(subdiv, 1u);
    uint quad = id / 6u;
    float2 corner = WaterCorner(id % 6u);
    float2 quadUv = (float2(float(quad % sd), float(quad / sd)) + corner) / float(sd);
    float2 tileXy = float2(float(tile % nx), float(tile / nx));
    return (tileXy + quadUv) / float2(float(nx), float(nz)) - 0.5f;
}

// Gerstner 位移与切向量，参数在 water + 8 .. + 11（振幅, 波数, 方向.x, 方向.z）。
// 相速度由深水色散 ω = sqrt(g·k) 现算，帧缓冲里只存波数与 water + 13.x 的速度倍率。
// 水平位移贡献 k·A·sin（横向压缩），高度贡献 k·A·cos（竖直倾斜）；混用会让波峰与波谷的法线反号。
float3 WaterGerstner(const device float4* data, float2 p, float t, int offset,
    thread float3& tangent, thread float3& binormal) {
    float3 displacement = float3(0.0f, 0.0f, 0.0f);
    tangent = float3(1.0f, 0.0f, 0.0f);
    binormal = float3(0.0f, 0.0f, 1.0f);
    float choppiness = data[offset + 12].w;
    float speedScale = data[offset + 13].x;
    for (uint i = 0u; i < 4u; i++) {
        float4 wave = data[offset + 8 + int(i)];
        float amplitude = wave.x;
        float k = wave.y;
        // 风浪关闭时四位全 0：k 为 0 跳过，顶点与法线保持恒等值。
        if (k > 0.0f) {
            float2 dir = wave.zw;
            float omega = sqrt(9.81f * k) * speedScale;
            float phase = k * dot(dir, p) - omega * t;
            float s = sin(phase);
            float c = cos(phase);
            float q = choppiness * amplitude;
            displacement.x += q * dir.x * c;
            displacement.y += amplitude * s;
            displacement.z += q * dir.y * c;
            float ks = k * amplitude * s;
            float kc = k * amplitude * c;
            tangent += float3(-choppiness * ks * dir.x * dir.x, kc * dir.x, -choppiness * ks * dir.x * dir.y);
            binormal += float3(-choppiness * ks * dir.x * dir.y, kc * dir.y, -choppiness * ks * dir.y * dir.y);
        }
    }
    return displacement;
}

// 实例号即 tile 索引；槽位仍走 buffer(1) 的常量（WebGPU 没有，那边只能挤进实例号）。
vertex WaterVertex VSMain(uint id [[vertex_id]], uint tile [[instance_id]], const device float4* data [[buffer(0)]],
    constant uint4& params [[buffer(1)]]) {
    WaterVertex o;
    uint slot = params.x;
    int offset = WaterOffset(slot);
    float2 uv = WaterLocalUv(id, tile, params.y, params.z, params.w);
    float4 world = WaterTransform(data, float4(uv.x, 0.0f, uv.y, 1.0f), offset);

    // 位移在模型→世界之后做：Gerstner 是世界 XZ 平面上的行波，放进模型空间会跟着矩形转。
    float3 tangent, binormal;
    world.xyz += WaterGerstner(data, world.xz, data[10].z, offset, tangent, binormal);

    o.position = WaterTransform(data, world, 4);
    o.world = world.xyz;
    // cross(binormal,tangent)：未扰动时 (0,0,1)×(1,0,0) = (0,1,0)，正好朝上。
    o.normal = normalize(cross(binormal, tangent));
    o.slot = slot;
    return o;
}
""";

    /// <summary>
    /// 法线扰动、折射、吸收、Fresnel 与雨涟漪的求值。与 HLSL 版逐句同序；
    /// <c>fmod</c> 之外的数学函数名两端一致，<c>frac</c> 对应 <c>fract</c>。
    /// </summary>
    const string PixelMsl = """

float3 WaterSampleSky(texture2d<float> skyView, float3 direction) {
    constexpr sampler linearClamp(coord::normalized, address::clamp_to_edge, filter::linear);
    float2 uv = float2(atan2(direction.x, -direction.z) * 0.15915494309f + 0.5f,
        0.5f - 0.5f * sign(direction.y) * sqrt(abs(direction.y)));
    return skyView.sample(linearClamp, uv, level(0.0)).rgb;
}

// 高度场梯度（米/米），系数 = 波数 × 振幅，叠加不到 0.07，法线只倾 3~4°。
// 量纲不能省：直接返回 d·cos（幅值 ~1）会让法线倾到 15° 以上，俯视时一半像素的
// 法线背对视线，facing 被 saturate 到 0、Fresnel 饱和到 1，水面退化成硬边镜面斑块。
float2 WaterWave(float2 p, float t, float speed) {
    float2 d1 = float2(0.86f, 0.51f);
    float2 d2 = float2(-0.42f, 0.91f);
    float k1 = dot(p, d1) * 2.4f + t * speed * 6.2831853f;
    float k2 = dot(p, d2) * 3.7f - t * speed * 5.1f;
    return d1 * cos(k1) * (2.4f * 0.016f) + d2 * cos(k2) * (3.7f * 0.009f);
}

// 雨涟漪同样是梯度量纲：环频率 26/m、振幅 0.0025m，单环梯度约 0.065。
float2 WaterRipple(float2 p, float t) {
    float2 acc = float2(0.0f);
    for (uint i = 0u; i < 3u; i++) {
        float2 c = float2(sin(float(i) * 12.9898f) * 7.0f, cos(float(i) * 78.233f) * 7.0f);
        float2 d = p - c;
        float r = length(d);
        float phase = fract(t * 1.7f + float(i) * 0.37f);
        float wave = sin(r * 26.0f - phase * 40.0f) * saturate(1.0f - phase);
        acc += d / max(r, 1e-4f) * wave;
    }
    return acc * (26.0f * 0.0025f);
}

// 视空间前向深度（LH + [0,1]），与主着色器 / GTAO / SsrEffect 的重建公式同源。
float WaterLinear(const device float4* data, float d) {
    float nearV = data[12].z, farV = data[12].w;
    return nearV * farV / (farV - d * (farV - nearV));
}

float WaterHiZ(texture2d<float> hz0, texture2d<float> hz1, texture2d<float> hz2, texture2d<float> hz3,
    sampler sm, float2 uv, int level) {
    if (level <= 0) return hz0.sample(sm, uv, level(0.0)).r;
    if (level == 1) return hz1.sample(sm, uv, level(0.0)).r;
    if (level == 2) return hz2.sample(sm, uv, level(0.0)).r;
    return hz3.sample(sm, uv, level(0.0)).r;
}

// 屏幕空间反射：沿反射方向在屏幕空间步进，用 Hi-Z 金字塔整块跳过空区域，
// 命中点取折射快照（开画前的不透明场景色）。rgb = 辐射，a = 置信度（0 = 没命中）。
// MSL 没有着色器全局，七张纹理与采样器全部显式入参。
float4 WaterTraceReflection(const device float4* data, depth2d<float> depthTexture,
    texture2d<float> refraction, texture2d<float> hz0, texture2d<float> hz1,
    texture2d<float> hz2, texture2d<float> hz3, sampler sm,
    float3 origin, float3 direction, float2 size) {
    const int coarseSteps = 24;
    const int refineSteps = 6;
    float maxDistance = data[13].x;
    float thickness = data[13].y;
    float4 result = float4(0.0f);
    float4 clip0 = WaterTransform(data, float4(origin, 1.0f), 4);
    float4 clip1 = WaterTransform(data, float4(origin + direction * maxDistance, 1.0f), 4);

    // 射线走到相机背后时屏幕空间没有信息可用：沿原方向缩到相机近平面之前。
    float endT = 1.0f;
    if (clip1.w <= 0.001f) {
        float denom = clip1.w - clip0.w;
        endT = denom < -0.0001f ? saturate(-clip0.w / denom) : 0.0f;
        clip1 = WaterTransform(data, float4(origin + direction * (maxDistance * endT), 1.0f), 4);
    }

    if (clip0.w > 0.001f && clip1.w > 0.001f && endT > 0.001f) {
        float2 uv0 = clip0.xy / clip0.w * float2(0.5f, -0.5f) + 0.5f;
        float2 uv1 = clip1.xy / clip1.w * float2(0.5f, -0.5f) + 0.5f;
        float z0 = clip0.z / clip0.w;
        float z1 = clip1.z / clip1.w;
        float2 delta = uv1 - uv0;

        // 每步跨多少像素决定查哪一级金字塔：短射线用细级，否则粗块永远命中不了。
        float pixels = length(delta * size) / float(coarseSteps);
        int level = 3;
        if (pixels < 12.0f) level = 2;
        if (pixels < 6.0f) level = 1;
        if (pixels < 3.0f) level = 0;

        float2 coarse = uv0;
        float coarseZ = z0;
        for (int i = 1; i <= coarseSteps; i++) {
            float t = float(i) / float(coarseSteps);
            float2 next = uv0 + delta * t;
            float nextZ = mix(z0, z1, t);
            // 出屏即终止：屏幕外的信息不存在，继续走只会采到钳制后的边缘像素。
            if (next.x < 0.0f || next.x > 1.0f || next.y < 0.0f || next.y > 1.0f) break;
            if (nextZ < WaterHiZ(hz0, hz1, hz2, hz3, sm, next, level)) {
                coarse = next;
                coarseZ = nextZ;
                continue;
            }
            // 越过了：在（上一节点, 当前节点）内逐点逼近真实交点。
            for (int j = 1; j <= refineSteps; j++) {
                float tj = float(j) / float(refineSteps);
                float2 suv = mix(coarse, next, tj);
                float rayZ = mix(coarseZ, nextZ, tj);
                int2 coord = clamp(int2(suv * size), int2(0, 0), int2(int(size.x) - 1, int(size.y) - 1));
                float sceneD = depthTexture.read(uint2(coord));
                if (sceneD >= 1.0f) continue;
                // 厚度判定放在线性深度上：NDC 深度越远越挤，同一阈值在近处和远处差出几个数量级。
                float rayView = WaterLinear(data, rayZ);
                float sceneView = WaterLinear(data, sceneD);
                if (rayView >= sceneView && rayView - sceneView <= thickness) {
                    float2 hitUv = saturate(suv);
                    // 屏幕边缘淡出：那里的采样是外推出来的，硬切会在画面边缘拉出一条亮边。
                    float edge = min(min(hitUv.x, 1.0f - hitUv.x), min(hitUv.y, 1.0f - hitUv.y));
                    float fade = saturate(edge / 0.06f) * saturate(1.0f - t);
                    result = float4(refraction.sample(sm, hitUv, level(0.0)).rgb, fade);
                    break;
                }
            }
            break;
        }
    }
    return result;
}

fragment float4 PSMain(WaterVertex in [[stage_in]], const device float4* data [[buffer(0)]],
    constant uint4& params [[buffer(1)]], depth2d<float> depthTexture [[texture(0)]],
    texture2d<float> refractionSource [[texture(1)]], texture2d<float> normalTexture [[texture(2)]],
    texture2d<float> skyView [[texture(3)]], texture2d<float> hz0 [[texture(4)]],
    texture2d<float> hz1 [[texture(5)]], texture2d<float> hz2 [[texture(6)]],
    texture2d<float> hz3 [[texture(7)]]) {
    constexpr sampler linearClamp(coord::normalized, address::clamp_to_edge, filter::linear);
    int offset = WaterOffset(in.slot);
    float4 material0 = WaterF(data, offset + 4);
    float4 material1 = WaterF(data, offset + 5);
    float4 material2 = WaterF(data, offset + 6);
    float4 material3 = WaterF(data, offset + 7);

    float3 camera = data[8].xyz;
    float4 screen = data[9];
    float4 misc = data[10];
    float4 sun = data[11];
    float time = misc.z;

    float2 uv = in.position.xy * screen.zw;
    uint2 pixel = uint2(in.position.xy);
    float sceneDepthValue = depthTexture.read(pixel);

    if (in.position.z > sceneDepthValue + 1e-6f) discard_fragment();

    float4 ndc = float4(uv * float2(2.0f, -2.0f) + float2(-1.0f, 1.0f), sceneDepthValue, 1.0f);
    float4 scenePosition = ndc.x * data[0] + ndc.y * data[1] + ndc.z * data[2] + ndc.w * data[3];
    float thickness = max(length(scenePosition.xyz / scenePosition.w - in.world), 0.0f);

    float2 planar = in.world.xz;
    float2 slope = WaterWave(planar, time, material2.x);
    if (material3.x > 0.5f) {
        float2 tiled = planar * material1.w;
        float2 a = normalTexture.sample(linearClamp, tiled + time * material2.x * float2(0.7f, 0.3f), level(0.0)).rg * 2.0f - 1.0f;
        float2 b = normalTexture.sample(linearClamp, tiled * 1.37f - time * material2.x * float2(0.4f, 0.8f), level(0.0)).rg * 2.0f - 1.0f;
        slope = (a + b) * 0.5f;
    }
    if (misc.y > 0.5f) slope += WaterRipple(planar * 2.0f, time) * material2.y;
    slope *= material1.z;
    // P4：细节斜率叠加到 Gerstner 的解析法线上。Gerstner 关闭时 in.normal 恒为 (0,1,0)，
    // 这里退化成 P3 的 normalize(float3(slope.x,1,slope.y))。
    float3 normal = normalize(in.normal + float3(slope.x, 0.0f, slope.y));

    float3 view = normalize(camera - in.world);
    float facing = saturate(dot(normal, view));
    float fresnel = material2.z + (1.0f - material2.z) * pow(1.0f - facing, 5.0f);

    // 折射偏移的单位是像素（后面乘 1/宽高换成 UV），600 把梯度折成像素尺度。
    float2 offsetUv = slope * material1.y * 600.0f;
    float2 refractedUv = clamp(uv + offsetUv * screen.zw, float2(0.0f), float2(1.0f));
    float3 refracted = refractionSource.sample(linearClamp, refractedUv, level(0.0)).rgb;

    float3 transmittance = exp(-material1.x * thickness * float3(1.35f, 0.85f, 0.55f));
    // 水体是被照亮的散射介质：水色是反照率，必须乘光照，否则夜里场景全暗之后水池会自己发光。
    // 用池底快照的亮度当日光代理：夜里趋近 0，白天饱和到 1。
    float daylight = saturate(dot(refracted, float3(0.2126f, 0.7152f, 0.0722f)) * 3.0f);
    float3 body = refracted * transmittance + material0.rgb * (1.0f - transmittance) * daylight;

    // 反射：先试屏幕空间反射（Hi-Z 加速），没命中再退回程序化天空。
    // 射线用未钳制的方向：水面往下看时反射方向指向水底，天空 LUT 那里没有信息。
    float3 reflectDirection = reflect(-view, normal);
    // 天空采样前把地平线以下钳回地平线：LUT 下半球是近地雾霾（夜里几乎全黑），法线稍一倾斜就会打进去。
    float3 skyDirection = float3(reflectDirection.x, max(reflectDirection.y, 0.0f), reflectDirection.z);
    // 兑底色同样乘 daylight：拿不到天空 LUT 时，这个常量不该在夜里继续发光。
    float3 reflection = misc.w > 0.5f ? WaterSampleSky(skyView, skyDirection) : float3(0.35f, 0.45f, 0.62f) * daylight;
    if (data[12].x > 0.5f) {
        float4 traced = WaterTraceReflection(data, depthTexture, refractionSource, hz0, hz1, hz2, hz3,
            linearClamp, in.world, reflectDirection, screen.xy);
        reflection = mix(reflection, traced.rgb, saturate(traced.a * data[12].y));
    }
    float3 toSun = normalize(-sun.xyz);
    float3 halfVector = normalize(view + toSun);
    float shininess = 2.0f / max(material2.w * material2.w, 1e-3f);
    reflection += sun.w * pow(saturate(dot(normal, halfVector)), shininess) * float3(1.0f, 0.95f, 0.85f) * 0.5f;

    float3 color = mix(body, reflection, fresnel);
    float thicknessAlpha = 1.0f - exp(-thickness * 0.9f);
    float alpha = saturate(max(mix(material0.a, 1.0f, fresnel), thicknessAlpha * 0.9f));
    return float4(color, alpha);
}
""";

    /// <summary>顶点与片元两个入口合成一份源码：后端按源码字符串缓存，整条管线只编译一次。</summary>
    public const string Msl = CoreMsl + VertexMsl + PixelMsl;
}
