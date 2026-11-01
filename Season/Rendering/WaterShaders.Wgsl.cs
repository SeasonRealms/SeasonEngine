// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// P2 水面材质的 WGSL 变体。四端共用同一套语义与帧缓冲布局（见 <see cref="WaterMath"/>），
/// 这里只负责「读布局 + 求值」，不定义布局。
///
/// 与另外三端的差异来自 WebGPU 的两条硬限制，与 <see cref="DecalShaders.Wgsl"/> 同源：
/// <list type="bullet">
/// <item>没有 push constant：「本 draw 用哪个水面槽」由 <c>instance_index</c> 携带
/// （JS 侧 <c>draw(6, 1, 0, slot)</c>），再用 flat 插值送进片元阶段；</item>
/// <item>深度只能 <c>textureLoad</c>：<c>texture_depth_2d</c> 不支持过滤采样，而软件深度测试本来就要点取样。</item>
/// </list>
///
/// 折射采样用 <c>textureSampleLevel</c> 而不是隐式导数版本：同一个水面在掠射角下的像素足迹极大，
/// 隐式 LOD 会在水面边缘取到模糊的邻域颜色，而这里只需要 LOD 0。
/// </summary>
public static partial class WaterShaders
{
    /// <summary>
    /// 顶点与片元两个入口合成一份源码，后端只建一个着色器模块。
    /// </summary>
    public const string Wgsl = """
struct WaterVaryings {
    @builtin(position) position : vec4<f32>,
    @location(0) world : vec3<f32>,
    // P4：顶点位移后的解析法线（世界空间）。Gerstner 关闭时恒为 (0,1,0)。
    @location(1) normal : vec3<f32>,
    // 水面槽位随实例号进来：WebGPU 没有 push constant，只能靠这个把它送到片元阶段。
    @location(2) @interpolate(flat) slot : u32,
};

// 帧缓冲布局见 WaterMath，下标与另外三端逐字相同。group 0 全是「整帧恒定」的资源。
@group(0) @binding(0) var<storage, read> waterFrame : array<vec4<f32>>;
@group(0) @binding(1) var sceneDepth : texture_depth_2d;
@group(0) @binding(2) var refractionSource : texture_2d<f32>;
@group(0) @binding(3) var waterSkyView : texture_2d<f32>;
@group(0) @binding(4) var waterLinearClamp : sampler;
// P3 Hi-Z 金字塔的四个级别（1/2 ~ 1/16）；frame[12].x 为 0 时一张都不会被采样。
@group(0) @binding(5) var waterHz0 : texture_2d<f32>;
@group(0) @binding(6) var waterHz1 : texture_2d<f32>;
@group(0) @binding(7) var waterHz2 : texture_2d<f32>;
@group(0) @binding(8) var waterHz3 : texture_2d<f32>;
// 法线贴图逐水面不同（也允许缺席走程序化波浪），所以单独成组：帧级绑定组每帧只建一次，
// 逐 draw 只换这一组 —— 与贴花「纹理组」的取舍相同。另外三端把五个槽挤在一个 set 里，
// 那边一张贴图一个不可变采样器，拆组只会多一次绑定调用。
@group(1) @binding(0) var waterNormalTexture : texture_2d<f32>;

fn waterF(index : u32) -> vec4<f32> { return waterFrame[index]; }
fn saturate(v : f32) -> f32 { return clamp(v, 0.0, 1.0); }
fn waterOffset(index : u32) -> u32 { return 16u + index * 16u; }
fn waterTransform(p : vec4<f32>, index : u32) -> vec4<f32> {
    return p.x * waterFrame[index] + p.y * waterFrame[index + 1u]
         + p.z * waterFrame[index + 2u] + p.w * waterFrame[index + 3u];
}

// 六个二维角点，按 quad 的两个三角形排列。单一出口，与另外三端同理。
fn waterCorner(id : u32) -> vec2<f32> {
    var corner = vec2<f32>(0.0, 0.0);
    if (id == 1u) { corner = vec2<f32>(1.0, 0.0); }
    else if (id == 2u) { corner = vec2<f32>(1.0, 1.0); }
    else if (id == 3u) { corner = vec2<f32>(0.0, 0.0); }
    else if (id == 4u) { corner = vec2<f32>(1.0, 1.0); }
    else if (id == 5u) { corner = vec2<f32>(0.0, 1.0); }
    return corner;
}

// tile 内的 [0,1] 网格坐标 → 方形的 [-0.5,0.5] 平面坐标。
// 除数钳到 1：WGSL 里整数除零是未定义值，且着色器无法证明帧数据里的值非零。
fn waterLocalUv(id : u32, tile : u32, tilesX : u32, tilesZ : u32, subdiv : u32) -> vec2<f32> {
    if (subdiv == 0u) { return waterCorner(id) - vec2<f32>(0.5, 0.5); }
    let nx = max(tilesX, 1u);
    let nz = max(tilesZ, 1u);
    let sd = max(subdiv, 1u);
    let quad = id / 6u;
    let corner = waterCorner(id % 6u);
    let quadUv = (vec2<f32>(f32(quad % sd), f32(quad / sd)) + corner) / f32(sd);
    let tileXy = vec2<f32>(f32(tile % nx), f32(tile / nx));
    return (tileXy + quadUv) / vec2<f32>(f32(nx), f32(nz)) - vec2<f32>(0.5, 0.5);
}

struct GerstnerResult {
    displacement : vec3<f32>,
    tangent : vec3<f32>,
    binormal : vec3<f32>,
};

// Gerstner 位移与切向量，参数在 water + 8 .. + 11（振幅, 波数, 方向.x, 方向.z）。
// 相速度由深水色散 ω = sqrt(g·k) 现算，帧缓冲里只存波数与 water + 13.x 的速度倍率。
// 水平位移贡献 k·A·sin（横向压缩），高度贡献 k·A·cos（竖直倾斜）；混用会让波峰与波谷的法线反号。
fn waterGerstner(p : vec2<f32>, t : f32, offset : u32) -> GerstnerResult {
    var result : GerstnerResult;
    result.displacement = vec3<f32>(0.0);
    result.tangent = vec3<f32>(1.0, 0.0, 0.0);
    result.binormal = vec3<f32>(0.0, 0.0, 1.0);
    let choppiness = waterFrame[offset + 12u].w;
    let speedScale = waterFrame[offset + 13u].x;
    for (var i : u32 = 0u; i < 4u; i = i + 1u) {
        let wave = waterFrame[offset + 8u + i];
        let amplitude = wave.x;
        let k = wave.y;
        // 风浪关闭时四位全 0：k 为 0 跳过，顶点与法线保持恒等值。
        if (k > 0.0) {
            let dir = wave.zw;
            let omega = sqrt(9.81 * k) * speedScale;
            let phase = k * dot(dir, p) - omega * t;
            let s = sin(phase);
            let c = cos(phase);
            let q = choppiness * amplitude;
            result.displacement.x += q * dir.x * c;
            result.displacement.y += amplitude * s;
            result.displacement.z += q * dir.y * c;
            let ks = k * amplitude * s;
            let kc = k * amplitude * c;
            result.tangent += vec3<f32>(-choppiness * ks * dir.x * dir.x, kc * dir.x, -choppiness * ks * dir.x * dir.y);
            result.binormal += vec3<f32>(-choppiness * ks * dir.x * dir.y, kc * dir.y, -choppiness * ks * dir.y * dir.y);
        }
    }
    return result;
}

// 单位平面或 tile 网格按 vertex_index 展开；实例号同时编码槽位与 tile。
// 低 <see cref="WaterMath.TileBits"/> 位是 tile，高位是槽位 —— JS 侧的 firstInstance 是这个乘积。
@vertex
fn VSMain(@builtin(vertex_index) vertexIndex : u32, @builtin(instance_index) instanceIndex : u32) -> WaterVaryings {
    var out : WaterVaryings;
    let slot = instanceIndex >> 8u;
    let tile = instanceIndex & 255u;
    let offset = waterOffset(slot);
    // 没有 push constant，tile 网格参数只能从帧数据读；另外三端走根常量。
    let tiling = waterFrame[offset + 12u];
    let uv = waterLocalUv(vertexIndex, tile, u32(tiling.x), u32(tiling.y), u32(tiling.z));
    var world = waterTransform(vec4<f32>(uv.x, 0.0, uv.y, 1.0), offset);

    // 位移在模型→世界之后做：Gerstner 是世界 XZ 平面上的行波，放进模型空间会跟着矩形转。
    let gerstner = waterGerstner(world.xz, waterFrame[10u].z, offset);
    world = world + vec4<f32>(gerstner.displacement, 0.0);

    out.position = waterTransform(world, 4u);
    out.world = world.xyz;
    // cross(binormal,tangent)：未扰动时 (0,0,1)×(1,0,0) = (0,1,0)，正好朝上。
    out.normal = normalize(cross(gerstner.binormal, gerstner.tangent));
    out.slot = slot;
    return out;
}

fn waterSampleSky(direction : vec3<f32>) -> vec3<f32> {
    // 与主着色器逐字同参：u = atan2(x,-z)/2pi+0.5，v = 0.5-0.5*sign(y)*sqrt(|y|)。
    let uv = vec2<f32>(atan2(direction.x, -direction.z) * 0.15915494309 + 0.5,
        0.5 - 0.5 * sign(direction.y) * sqrt(abs(direction.y)));
    return textureSampleLevel(waterSkyView, waterLinearClamp, uv, 0.0).rgb;
}

// 高度场梯度（米/米），系数 = 波数 × 振幅，叠加不到 0.07，法线只倾 3~4°。
// 量纲不能省：直接返回 d·cos（幅值 ~1）会让法线倾到 15° 以上，俯视时一半像素的
// 法线背对视线，facing 被 saturate 到 0、Fresnel 饱和到 1，水面退化成硬边镜面斑块。
fn waterWave(p : vec2<f32>, t : f32, speed : f32) -> vec2<f32> {
    let d1 = vec2<f32>(0.86, 0.51);
    let d2 = vec2<f32>(-0.42, 0.91);
    let k1 = dot(p, d1) * 2.4 + t * speed * 6.2831853;
    let k2 = dot(p, d2) * 3.7 - t * speed * 5.1;
    return d1 * cos(k1) * (2.4 * 0.016) + d2 * cos(k2) * (3.7 * 0.009);
}

// 雨涟漪同样是梯度量纲：环频率 26/m、振幅 0.0025m，单环梯度约 0.065。
fn waterRipple(p : vec2<f32>, t : f32) -> vec2<f32> {
    var acc = vec2<f32>(0.0);
    for (var i : u32 = 0u; i < 3u; i = i + 1u) {
        let c = vec2<f32>(sin(f32(i) * 12.9898) * 7.0, cos(f32(i) * 78.233) * 7.0);
        let d = p - c;
        let r = length(d);
        let phase = fract(t * 1.7 + f32(i) * 0.37);
        let wave = sin(r * 26.0 - phase * 40.0) * saturate(1.0 - phase);
        acc = acc + d / max(r, 1e-4) * wave;
    }
    return acc * (26.0 * 0.0025);
}

// 视空间前向深度（LH + [0,1]），与主着色器 / GTAO / SsrEffect 的重建公式同源。
fn waterLinear(d : f32) -> f32 {
    let nearV = waterF(12u).z;
    let farV = waterF(12u).w;
    return nearV * farV / (farV - d * (farV - nearV));
}

fn waterHiZ(uv : vec2<f32>, level : i32) -> f32 {
    if (level <= 0) { return textureSampleLevel(waterHz0, waterLinearClamp, uv, 0.0).r; }
    if (level == 1) { return textureSampleLevel(waterHz1, waterLinearClamp, uv, 0.0).r; }
    if (level == 2) { return textureSampleLevel(waterHz2, waterLinearClamp, uv, 0.0).r; }
    return textureSampleLevel(waterHz3, waterLinearClamp, uv, 0.0).r;
}

// 屏幕空间反射：沿反射方向在屏幕空间步进，用 Hi-Z 金字塔整块跳过空区域，
// 命中点取折射快照（开画前的不透明场景色）。rgb = 辐射，a = 置信度（0 = 没命中）。
fn waterTraceReflection(origin : vec3<f32>, direction : vec3<f32>, size : vec2<f32>) -> vec4<f32> {
    let coarseSteps : i32 = 24;
    let refineSteps : i32 = 6;
    let maxDistance = waterF(13u).x;
    let thickness = waterF(13u).y;
    var result = vec4<f32>(0.0);
    let clip0 = waterTransform(vec4<f32>(origin, 1.0), 4u);
    var clip1 = waterTransform(vec4<f32>(origin + direction * maxDistance, 1.0), 4u);

    // 射线走到相机背后时屏幕空间没有信息可用：沿原方向缩到相机近平面之前。
    var endT = 1.0;
    if (clip1.w <= 0.001) {
        let denom = clip1.w - clip0.w;
        endT = select(0.0, saturate(-clip0.w / denom), denom < -0.0001);
        clip1 = waterTransform(vec4<f32>(origin + direction * (maxDistance * endT), 1.0), 4u);
    }

    if (clip0.w > 0.001 && clip1.w > 0.001 && endT > 0.001) {
        let uv0 = clip0.xy / clip0.w * vec2<f32>(0.5, -0.5) + vec2<f32>(0.5);
        let uv1 = clip1.xy / clip1.w * vec2<f32>(0.5, -0.5) + vec2<f32>(0.5);
        let z0 = clip0.z / clip0.w;
        let z1 = clip1.z / clip1.w;
        let delta = uv1 - uv0;

        // 每步跨多少像素决定查哪一级金字塔：短射线用细级，否则粗块永远命中不了。
        let pixels = length(delta * size) / f32(coarseSteps);
        var level : i32 = 3;
        if (pixels < 12.0) { level = 2; }
        if (pixels < 6.0) { level = 1; }
        if (pixels < 3.0) { level = 0; }

        var coarse = uv0;
        var coarseZ = z0;
        for (var i : i32 = 1; i <= coarseSteps; i = i + 1) {
            let t = f32(i) / f32(coarseSteps);
            let next = uv0 + delta * t;
            let nextZ = mix(z0, z1, t);
            // 出屏即终止：屏幕外的信息不存在，继续走只会采到钳制后的边缘像素。
            if (next.x < 0.0 || next.x > 1.0 || next.y < 0.0 || next.y > 1.0) { break; }
            if (nextZ < waterHiZ(next, level)) {
                coarse = next;
                coarseZ = nextZ;
                continue;
            }
            // 越过了：在（上一节点, 当前节点）内逐点逼近真实交点。
            for (var j : i32 = 1; j <= refineSteps; j = j + 1) {
                let tj = f32(j) / f32(refineSteps);
                let suv = mix(coarse, next, tj);
                let rayZ = mix(coarseZ, nextZ, tj);
                let rc = clamp(vec2<i32>(suv * size), vec2<i32>(0, 0),
                    vec2<i32>(i32(size.x) - 1, i32(size.y) - 1));
                let sceneD = textureLoad(sceneDepth, rc, 0);
                if (sceneD >= 1.0) { continue; }
                // 厚度判定放在线性深度上：NDC 深度越远越挤，同一阈值在近处和远处差出几个数量级。
                let rayView = waterLinear(rayZ);
                let sceneView = waterLinear(sceneD);
                if (rayView >= sceneView && rayView - sceneView <= thickness) {
                    let hitUv = clamp(suv, vec2<f32>(0.0), vec2<f32>(1.0));
                    // 屏幕边缘淡出：那里的采样是外推出来的，硬切会在画面边缘拉出一条亮边。
                    let edge = min(min(hitUv.x, 1.0 - hitUv.x), min(hitUv.y, 1.0 - hitUv.y));
                    let fade = saturate(edge / 0.06) * saturate(1.0 - t);
                    result = vec4<f32>(textureSampleLevel(refractionSource, waterLinearClamp, hitUv, 0.0).rgb, fade);
                    break;
                }
            }
            break;
        }
    }
    return result;
}

@fragment
fn PSMain(input : WaterVaryings) -> @location(0) vec4<f32> {
    let offset = waterOffset(input.slot);
    let material0 = waterF(offset + 4u);
    let material1 = waterF(offset + 5u);
    let material2 = waterF(offset + 6u);
    let material3 = waterF(offset + 7u);

    let camera = waterF(8u).xyz;
    let screen = waterF(9u);
    let misc = waterF(10u);
    let sun = waterF(11u);
    let time = misc.z;

    let pixel = vec2<i32>(i32(input.position.x), i32(input.position.y));
    let uv = input.position.xy * screen.zw;
    let sceneDepthValue = textureLoad(sceneDepth, pixel, 0);

    // 软件深度测试：本 Pass 没有深度附件，遮挡完全靠这一句。
    if (input.position.z > sceneDepthValue + 1e-6) { discard; }

    let ndc = vec4<f32>(uv * vec2<f32>(2.0, -2.0) + vec2<f32>(-1.0, 1.0), sceneDepthValue, 1.0);
    let scenePosition = ndc.x * waterF(0u) + ndc.y * waterF(1u) + ndc.z * waterF(2u) + ndc.w * waterF(3u);
    let thickness = max(length(scenePosition.xyz / scenePosition.w - input.world), 0.0);

    let planar = input.world.xz;
    var slope = waterWave(planar, time, material2.x);
    if (material3.x > 0.5) {
        let tiled = planar * material1.w;
        let a = textureSampleLevel(waterNormalTexture, waterLinearClamp, tiled + time * material2.x * vec2<f32>(0.7, 0.3), 0.0).rg * 2.0 - 1.0;
        let b = textureSampleLevel(waterNormalTexture, waterLinearClamp, tiled * 1.37 - time * material2.x * vec2<f32>(0.4, 0.8), 0.0).rg * 2.0 - 1.0;
        slope = (a + b) * 0.5;
    }
    if (misc.y > 0.5) { slope = slope + waterRipple(planar * 2.0, time) * material2.y; }
    slope = slope * material1.z;
    // P4：细节斜率叠加到 Gerstner 的解析法线上。Gerstner 关闭时 input.normal 恒为 (0,1,0)，
    // 这里退化成 P3 的 normalize(vec3(slope.x,1,slope.y))。
    let normal = normalize(input.normal + vec3<f32>(slope.x, 0.0, slope.y));

    let view = normalize(camera - input.world);
    let facing = saturate(dot(normal, view));
    let fresnel = material2.z + (1.0 - material2.z) * pow(1.0 - facing, 5.0);

    // 折射偏移的单位是像素（后面乘 1/宽高换成 UV），600 把梯度折成像素尺度。
    let offsetUv = slope * material1.y * 600.0;
    let refractedUv = clamp(uv + offsetUv * screen.zw, vec2<f32>(0.0), vec2<f32>(1.0));
    let refracted = textureSampleLevel(refractionSource, waterLinearClamp, refractedUv, 0.0).rgb;

    let transmittance = exp(-material1.x * thickness * vec3<f32>(1.35, 0.85, 0.55));
    // 水体是被照亮的散射介质：水色是反照率，必须乘光照，否则夜里场景全暗之后水池会自己发光。
    // 用池底快照的亮度当日光代理：夜里趋近 0，白天饱和到 1。
    let daylight = saturate(dot(refracted, vec3<f32>(0.2126, 0.7152, 0.0722)) * 3.0);
    let body = refracted * transmittance + material0.rgb * (1.0 - transmittance) * daylight;

    // 反射：先试屏幕空间反射（Hi-Z 加速），没命中再退回程序化天空。
    // 射线用未钳制的方向：水面往下看时反射方向指向水底，天空 LUT 那里没有信息。
    let reflectDirection = reflect(-view, normal);
    // 天空采样前把地平线以下钳回地平线：LUT 下半球是近地雾霾（夜里几乎全黑），法线稍一倾斜就会打进去。
    let skyDirection = vec3<f32>(reflectDirection.x, max(reflectDirection.y, 0.0), reflectDirection.z);
    // 兑底色同样乘 daylight：拿不到天空 LUT 时，这个常量不该在夜里继续发光。
    var reflection = select(vec3<f32>(0.35, 0.45, 0.62) * daylight, waterSampleSky(skyDirection), misc.w > 0.5);
    if (waterF(12u).x > 0.5) {
        let traced = waterTraceReflection(input.world, reflectDirection, screen.xy);
        reflection = mix(reflection, traced.rgb, saturate(traced.a * waterF(12u).y));
    }
    let toSun = normalize(-sun.xyz);
    let halfVector = normalize(view + toSun);
    let shininess = 2.0 / max(material2.w * material2.w, 1e-3);
    reflection = reflection + sun.w * pow(saturate(dot(normal, halfVector)), shininess) * vec3<f32>(1.0, 0.95, 0.85) * 0.5;

    let color = mix(body, reflection, fresnel);
    let thicknessAlpha = 1.0 - exp(-thickness * 0.9);
    let alpha = saturate(max(mix(material0.a, 1.0, fresnel), thicknessAlpha * 0.9));
    return vec4<f32>(color, alpha);
}
""";
}
