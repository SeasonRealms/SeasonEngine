// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// WGSL variant of the D1 screen-space decal. All four backends share the same semantics and frame buffer layout (see <see cref="DecalMath"/>);
/// this file is only responsible for "reading the layout + reconstructing + evaluating", and does not define the layout.
///
/// The differences from the other three backends come from two hard limits of WebGPU:
/// <list type="bullet">
/// <item>There is no push constant: "which decal slot this draw uses" is instead carried by <c>instance_index</c>
/// (on the JS side, <c>draw(36, 1, 0, slot)</c>), and then passed into the fragment stage via flat interpolation --
/// otherwise every frame would have to write a small UBO once per decal for all 64 decals, and offsets written into the command stream do not exist in WebGPU;</item>
/// <item>Depth can only use <c>textureLoad</c>: <c>texture_depth_2d</c> does not support filtered sampling, and point sampling is exactly what this pass needs.</item>
/// </list>
///
/// The sampler still uses explicit gradients via <c>textureSampleGrad</c>: the box's back-face proxy triangle is far larger than the pixel footprint,
/// so implicit derivatives would sample outside the box along the outline; moreover, implicit derivatives may only be called in uniform control flow, and the decal branch here is not uniform.
/// </summary>
public static partial class DecalShaders
{
    /// <summary>
    /// The three entry points (<c>VSMain</c> / <c>PSMain</c> / <c>PSMainRoughness</c>) are combined into a single source;
    /// the backend builds just one shader module and bakes one PSO for each of the two attachment shapes.
    /// </summary>
    public const string Wgsl = """
struct DecalVaryings {
    @builtin(position) position : vec4<f32>,
    // The decal slot comes in with the instance index: WebGPU has no push constant, so this is the only way to pass it to the fragment stage.
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

// Frame buffer layout is in DecalMath, with indices identical, word for word, to the other three backends.
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

// Computes the 36 vertices of the projection box from vertex_index, without relying on a vertex buffer.
// All six faces are unified as "counter-clockwise when viewed from outside the box", combined with cullMode:'front' + frontFace:'cw' (keeping only back faces)
// so that each pixel is hit exactly once -- this is the premise of the whole approach requiring neither the stencil nor per-draw sorting.
@vertex
fn VSMain(@builtin(vertex_index) vid : u32, @builtin(instance_index) slot : u32) -> DecalVaryings {
    // 6 vertices per face: two triangles (0,1,2)+(0,2,3), then k is mapped back to the corner index.
    let face = vid / 6u;
    let t = vid - face * 6u;
    let tri = t / 3u;
    let c = t - tri * 3u;
    var k = 0u;
    if (c == 1u) { k = select(2u, 1u, tri == 0u); }
    else if (c == 2u) { k = select(3u, 2u, tri == 0u); }
    // face>>1 = major axis (0=x,1=y,2=z), face&1 = positive/negative side; the negative side must flip u to preserve "counter-clockwise from outside the box".
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

// Depth reconstruction + decal evaluation, with key points corresponding one-to-one to the other three backends:
// - UV comes from the box's normalized coordinates, so the decal is "projected along the box's local Y" regardless of surface orientation;
// - Derivatives are always taken in uniform control flow (both uv and world are computed up front); derivatives inside branches are undefined behavior at quad boundaries;
// - Grazing fade-out uses the normal derived from depth and does not read SceneNormal -- when writing roughness, SceneNormal is color attachment 1, not a sampling source;
// - Output convention: Modulate outputs "the factor multiplied into the target", Additive outputs an emissive color, and both rely on fixed-function blending to complete,
//   the fragment shader does not read SceneColor, so SceneColor does not need to be a samplable resource at the same time.
fn decalEval(pixel : vec2<f32>, slot : u32) -> DecalEvalResult {
    let offset = decalOffset(slot);
    // This pass has no depth attachment; depth is only a sampling source, so it is point-sampled at integer coordinates.
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
        // Feathering: the fraction of shape.y inward from the box edge; grazing: fade out when the cosine of the angle between the normal and the projection axis drops below 1-shape.z.
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
            // Procedural pattern when there is no texture: used to verify depth reconstruction; if the pattern follows the terrain relief, the reconstruction is correct.
            // WGSL has no fmod: the only floating-point remainder is `%` (e1 - e2*trunc(e1/e2), the same sign as fmod on the other three backends),
            // and writing fmod(...) would make the whole module fail to compile, invalidating even the vertex entry point.
            let checker = select(0.45, 1.0, ((floor(uv.x * 4.0) + floor(uv.y * 4.0)) % 2.0) < 0.5);
            albedo = albedo * checker * (0.55 + 0.45 * sin(length(local.xz) * 18.0));
        }
        shade = clamp(edge * edge * (3.0 - 2.0 * edge), 0.0, 1.0) * grazing * alpha;
    }
    var result : DecalEvalResult;
    result.coverage = shade * tint.w;
    // A decal that does not modify roughness must set the source alpha of attachment 1 to 0: the blending there is lerp(dst, src, src.a),
    // so a non-zero source alpha would needlessly pull regions that "did not request a roughness change" toward src.
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

// s9: the normal target only enables the B channel in its write mask, so here we only need to send the target roughness to .z,
// while the octahedral normal (.xy) and reflection weight (.w) keep their original values from the attachment. Blending interpolates on the B channel using roughWeight.
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
