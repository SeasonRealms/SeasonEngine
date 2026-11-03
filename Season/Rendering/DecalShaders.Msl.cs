// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// MSL variant of the D1 screen-space decal. All four backends share the same semantics and frame buffer layout (see <see cref="DecalMath"/>);
/// this file is only responsible for "reading the layout + reconstructing + evaluating", and does not define the layout.
///
/// There are only two differences from the other three backends, both of them quirks of Metal itself:
/// <list type="bullet">
/// <item>The frame buffer is passed directly as <c>device const float4*</c>, with no need for the bit reinterpretation that ByteAddressBuffer requires;</item>
/// <item>The sampler is declared in the source (<c>constexpr sampler</c>) and takes up no binding slot -- the same approach the main pipeline uses for the shadow map.</item>
/// </list>
///
/// The entire source is compiled only once: the vertex and the two fragment entry points share the same helper functions, and Metal's shader compiler
/// caches libraries by source string, so a given decal pipeline pays the compilation cost only once.
/// </summary>
public static partial class DecalShaders
{
    /// <summary>
    /// Accessors for the frame header and the decal array, shared by the three entry points.
    ///
    /// The vertex stage needs both the local->world and world->clip matrices, and the pixel stage needs the unprojection and pixel size;
    /// splitting them apart would make the two entry points disagree on how to read the same layout -- the same trade-off as on the GLSL/HLSL sides.
    /// </summary>
    const string CoreMsl = """
#include <metal_stdlib>
using namespace metal;

// Frame buffer layout is in DecalMath: one float4 per item, with indices identical, word for word, to the other three backends.
float4 DecalF(device const float4* f, uint i) { return f[i]; }
float4 DecalTransform(device const float4* f, float4 p, uint i)
{
    return p.x * f[i] + p.y * f[i + 1u] + p.z * f[i + 2u] + p.w * f[i + 3u];
}
uint DecalOffset(uint index) { return 16u + index * 12u; }
float2 DecalSize(device const float4* f) { return f[9].xy; }

// The texture uses linear sampling with explicit gradients: the box's back-face proxy triangle is far larger than the pixel footprint, so implicit derivatives would sample outside the box along the outline.
constexpr sampler decalLinearSampler(filter::linear, mip_filter::linear, address::clamp_to_edge);
""";

    /// <summary>
    /// Computes the 36 vertices of the projection box from <c>[[vertex_id]]</c>, without relying on a vertex buffer.
    ///
    /// All six faces are unified as "counter-clockwise when viewed from outside the box", combined with <c>MTLCullMode.Front</c> (keeping only back faces) so that each pixel is hit exactly once --
    /// this is the premise of the whole approach requiring neither the stencil nor per-draw sorting. The bit decomposition and winding derivation correspond line by line to the HLSL side.
    /// </summary>
    const string VertexMsl = """

vertex float4 VSMain(uint vid [[vertex_id]],
                     device const float4* decalFrame [[buffer(0)]],
                     constant uint4& decalParams [[buffer(1)]])
{
    // 6 vertices per face: two triangles (0,1,2)+(0,2,3), then k is mapped back to the corner index.
    uint face = vid / 6u, t = vid - face * 6u;
    uint tri = t / 3u, c = t - tri * 3u;
    uint k = c == 0u ? 0u : (c == 1u ? (tri == 0u ? 1u : 2u) : (tri == 0u ? 2u : 3u));
    // face>>1 = major axis (0=x,1=y,2=z), face&1 = positive/negative side; the negative side must flip u to preserve "counter-clockwise from outside the box".
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
    /// Depth reconstruction + decal evaluation, with key points corresponding one-to-one to the HLSL side:
    /// <list type="bullet">
    /// <item>UV comes from the box's normalized coordinates, so the decal is "projected along the box's local Y" regardless of surface orientation;</item>
    /// <item>Derivatives are always taken in uniform control flow (both uv and world are computed up front); derivatives inside branches are undefined behavior at quad boundaries;</item>
    /// <item>Grazing fade-out uses the normal derived from depth and does not read SceneNormal -- when writing roughness, SceneNormal is color attachment 1, not a sampling source;</item>
    /// <item>Output convention: Modulate outputs "the factor multiplied into the target", Additive outputs an emissive color, and both rely on fixed-function blending to complete,
    /// the fragment shader does not read SceneColor, so SceneColor does not need to be a samplable resource at the same time.</item>
    /// </list>
    ///
    /// Frame buffer fields: <c>wrap = (target roughness, whether to rewrite roughness, sort key, whether there is a texture)</c>,
    /// <c>tint.w</c> is opacity, and <c>shape.zw</c> are the feather and grazing thresholds.
    /// </summary>
    const string PixelMsl = """

struct DecalPixelOut { float4 color [[color(0)]]; };
struct DecalRoughnessOut { float4 color [[color(0)]]; float4 normal [[color(1)]]; };

void DecalEval(device const float4* decalFrame, constant uint4& decalParams,
               texture2d<float> sceneDepth, texture2d<float> decalTexture, float2 pixel,
               thread float4& outColor, thread float& coverage, thread float& roughness, thread float& roughWeight)
{
    uint index = decalParams.x, offset = DecalOffset(index);
    // This pass has no depth attachment; depth is only a sampling source, so it is point-sampled at integer coordinates, unaffected by sampler filtering.
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
        // Feathering: the fraction of shape.y inward from the box edge; grazing: fade out when the cosine of the angle between the normal and the projection axis drops below 1-shape.z.
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
            // Procedural pattern when there is no texture: used to verify depth reconstruction; if the pattern follows the terrain relief, the reconstruction is correct.
            float checker = fmod(floor(uv.x * 4.0f) + floor(uv.y * 4.0f), 2.0f) < 0.5f ? 1.0f : 0.45f;
            albedo = albedo * checker * (0.55f + 0.45f * sin(length(local.xz) * 18.0f));
        }
        shade = saturate(edge * edge * (3.0f - 2.0f * edge)) * grazing * alpha;
    }
    coverage = shade * tint.w;
    // A decal that does not modify roughness must set the source alpha of attachment 1 to 0: the blending there is lerp(dst, src, src.a),
    // so a non-zero source alpha would needlessly pull regions that "did not request a roughness change" toward src.
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

// s9: the normal target only enables the B channel in its write mask, so here we only need to send the target roughness to .z,
// while the octahedral normal (.xy) and reflection weight (.w) keep their original values from the attachment. Blending interpolates on the B channel using roughWeight.
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

    /// <summary>The vertex and the two fragment entry points are combined into a single source: the backend caches by source string, so the whole pipeline compiles only once.</summary>
    public const string Msl = CoreMsl + VertexMsl + PixelMsl;
}
