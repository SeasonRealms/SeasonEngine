// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// D1 屏幕空间贴花的 WGSL 变体。四端共用同一套语义与帧缓冲布局（见 <see cref="DecalMath"/>），
/// 这里只负责「读布局 + 重建 + 求值」，不定义布局。
///
/// 与另外三端的差异来自 WebGPU 的两条硬限制：
/// <list type="bullet">
/// <item>没有 push constant：「本 draw 用哪个贴花槽」改由 <c>instance_index</c> 携带
/// （JS 侧 <c>draw(36, 1, 0, slot)</c>），再用 flat 插值把它送进片元阶段 ——
/// 否则每帧都要为 64 个贴花各写一次小 UBO，而写在命令流里的偏移在 WebGPU 里并不存在；</item>
/// <item>深度只能 <c>textureLoad</c>：<c>texture_depth_2d</c> 不支持过滤采样，正好本 Pass 要的是点取样。</item>
/// </list>
///
/// 采样器仍用显式梯度的 <c>textureSampleGrad</c>：盒的背面代理三角形比像素足迹大得多，
/// 隐式导数会在轮廓上取到盒外；而且隐式导数只允许在统一控制流里调用，这里的贴花分支并不统一。
/// </summary>
public static partial class DecalShaders
{
    /// <summary>
    /// 三个入口（<c>VSMain</c> / <c>PSMain</c> / <c>PSMainRoughness</c>）合成一份源码，
    /// 后端只建一个着色器模块，两种附件形状各烘一档 PSO。
    /// </summary>
    public const string Wgsl = """
struct DecalVaryings {
    @builtin(position) position : vec4<f32>,
    // 贴花槽位随实例号进来：WebGPU 没有 push constant，只能靠这个把它送到片元阶段。
    @location(0) @interpolate(flat) slot : u32,
};

struct DecalPixelOut {
    @location(0) color : vec4<f32>,
};

struct DecalRoughnessOut {
    @location(0) color : vec4<f32>,
    @location(1) normal : vec4<f32>,
};

struct DecalEvalResult {
    color : vec4<f32>,
    coverage : f32,
    roughness : f32,
    roughWeight : f32,
};

// 帧缓冲布局见 DecalMath，下标与另外三端逐字相同。
@group(0) @binding(0) var<storage, read> decalFrame : array<vec4<f32>>;
@group(0) @binding(1) var sceneDepth : texture_depth_2d;
@group(0) @binding(2) var decalSampler : sampler;
@group(1) @binding(0) var decalTexture : texture_2d<f32>;

fn decalTransform(p : vec4<f32>, index : u32) -> vec4<f32> {
    return p.x * decalFrame[index] + p.y * decalFrame[index + 1u]
         + p.z * decalFrame[index + 2u] + p.w * decalFrame[index + 3u];
}

fn decalOffset(index : u32) -> u32 { return 16u + index * 12u; }

fn decalSize() -> vec2<f32> { return decalFrame[9].xy; }

// 把投影盒的 36 个顶点按 vertex_index 算出来，不依赖顶点缓冲。
// 六个面统一成「从盒外看逆时针」，配合 cullMode:'front' + frontFace:'cw'（只留背面）
// 让每个像素恰好命中一次 —— 这是整个方案不用模板、不用逐 draw 排序的前提。
@vertex
fn VSMain(@builtin(vertex_index) vid : u32, @builtin(instance_index) slot : u32) -> DecalVaryings {
    // 每面 6 个顶点：两条三角形 (0,1,2)+(0,2,3)，再按 k 还原成四角编号。
    let face = vid / 6u;
    let t = vid - face * 6u;
    let tri = t / 3u;
    let c = t - tri * 3u;
    var k = 0u;
    if (c == 1u) { k = select(2u, 1u, tri == 0u); }
    else if (c == 2u) { k = select(3u, 2u, tri == 0u); }
    // face>>1 = 主轴（0=x,1=y,2=z），face&1 = 正负侧；负侧把 u 取反才能保持「盒外逆时针」。
    let axis = face >> 1u;
    let side = face & 1u;
    let uu = ((k ^ (k >> 1u)) & 1u) ^ (side ^ 1u);
    let vv = (k >> 1u) & 1u;
    let sa = select(-0.5, 0.5, side != 0u);
    let su = select(-0.5, 0.5, uu != 0u);
    let sv = select(-0.5, 0.5, vv != 0u);
    var p : vec3<f32>;
    if (axis == 0u) { p = vec3<f32>(sa, su, sv); }
    else if (axis == 1u) { p = vec3<f32>(sv, sa, su); }
    else { p = vec3<f32>(su, sv, sa); }
    let offset = decalOffset(slot);
    var result : DecalVaryings;
    result.position = decalTransform(decalTransform(vec4<f32>(p, 1.0), offset + 4u), 4u);
    result.slot = slot;
    return result;
}

// 深度重建 + 贴花求值，要点与另外三端一一对应：
// - UV 来自盒内归一化坐标，所以贴花是「沿着盒的局部 Y 投影」的，与表面朝向无关；
// - 导数一律在统一控制流里取（uv 与 world 都先算好），分支里的导数在 quad 边界是未定义行为；
// - 掠射淡出用深度导出的法线，不读 SceneNormal —— 写粗糙度时 SceneNormal 是颜色附件 1，不是采样源；
// - 输出约定：Modulate 输出「乘到目标上的因子」，Additive 输出自发光颜色，两者都靠固定功能混合完成，
//   片元着色器不读 SceneColor，因此 SceneColor 不需要同时是可采样资源。
fn decalEval(pixel : vec2<f32>, slot : u32) -> DecalEvalResult {
    let offset = decalOffset(slot);
    // 本 Pass 没有深度附件，深度只是采样源，所以按整数坐标点取样。
    let depth = textureLoad(sceneDepth, vec2<i32>(pixel), 0);
    let clip = vec4<f32>(pixel / decalSize() * vec2<f32>(2.0, -2.0) + vec2<f32>(-1.0, 1.0), depth, 1.0);
    let w = decalTransform(clip, 0u);
    let world = w.xyz / w.w;
    let local = decalTransform(vec4<f32>(world, 1.0), offset).xyz;
    let tint = decalFrame[offset + 8u];
    let shape = decalFrame[offset + 9u];
    let wrap = decalFrame[offset + 10u];
    let uv = local.xz * 0.5 + vec2<f32>(0.5, 0.5);
    let uvDx = dpdx(uv);
    let uvDy = dpdy(uv);
    let n = normalize(cross(dpdx(world), dpdy(world)) + vec3<f32>(1e-8, 1e-8, 1e-8));
    let axis = normalize(decalFrame[offset + 5u].xyz);
    var shade = 0.0;
    var albedo = tint.rgb;
    if (depth < 1.0 && all(abs(local) <= vec3<f32>(1.0, 1.0, 1.0))) {
        // 羽化：盒边界向内 shape.y 的比例；掠射：法线与投影轴夹角的余弦低于 1-shape.z 就淡出。
        var edge = 1.0;
        if (shape.y > 0.0) {
            edge = clamp((1.0 - max(abs(local.x), abs(local.z))) / max(shape.y, 1e-3), 0.0, 1.0);
        }
        var grazing = 1.0;
        if (shape.z > 0.0) {
            grazing = smoothstep(1.0 - shape.z, 1.0, abs(dot(n, axis)));
        }
        var alpha = 1.0;
        if (wrap.w > 0.5) {
            let texel = textureSampleGrad(decalTexture, decalSampler, uv, uvDx, uvDy);
            albedo = albedo * texel.rgb;
            alpha = texel.a;
        } else {
            // 无纹理时的程序化图案：验证深度重建用，图案跟着地形起伏就说明重建对了。
            // WGSL 没有 fmod：浮点取余只有 `%`（e1 - e2*trunc(e1/e2)，与另外三端 fmod 同号），
            // 写成 fmod(...) 会让整个模块编译失败，连顶点入口一起被判无效。
            let checker = select(0.45, 1.0, ((floor(uv.x * 4.0) + floor(uv.y * 4.0)) % 2.0) < 0.5);
            albedo = albedo * checker * (0.55 + 0.45 * sin(length(local.xz) * 18.0));
        }
        shade = clamp(edge * edge * (3.0 - 2.0 * edge), 0.0, 1.0) * grazing * alpha;
    }
    var result : DecalEvalResult;
    result.coverage = shade * tint.w;
    // 不改粗糙度的贴花必须把附件 1 的源 alpha 置 0：那里的混合是 lerp(dst, src, src.a)，
    // 源 alpha 不为 0 就会把「没要求改粗糙度」的区域白白拉向 src。
    result.roughWeight = select(0.0, result.coverage, wrap.y > 0.5);
    result.roughness = select(0.0, wrap.x, wrap.y > 0.5);
    if (shape.x < 0.5) {
        result.color = vec4<f32>(mix(vec3<f32>(1.0, 1.0, 1.0), albedo, result.coverage), 1.0);
    } else {
        result.color = vec4<f32>(albedo * result.coverage * shape.w, 0.0);
    }
    return result;
}

@fragment
fn PSMain(varyings : DecalVaryings) -> DecalPixelOut {
    var o : DecalPixelOut;
    o.color = decalEval(varyings.position.xy, varyings.slot).color;
    return o;
}

// s9：法线目标只开 B 通道写掩码，所以这里只需要把目标粗糙度送到 .z，
// 八面体法线（.xy）与反射权重（.w）由附件里的原值保留。混合用 roughWeight 在 B 通道上插值。
@fragment
fn PSMainRoughness(varyings : DecalVaryings) -> DecalRoughnessOut {
    var o : DecalRoughnessOut;
    let result = decalEval(varyings.position.xy, varyings.slot);
    o.color = result.color;
    o.normal = vec4<f32>(0.0, 0.0, result.roughness, result.roughWeight);
    return o;
}
""";
}
