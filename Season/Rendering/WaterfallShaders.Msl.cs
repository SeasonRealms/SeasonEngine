// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// MSL variant of the waterfall sheet. All four backends share the same semantics and frame buffer layout (see <see cref="WaterfallMath"/>);
/// this one is only responsible for "reading the layout + evaluating", not for defining the layout.
///
/// The differences from the other three backends are only two, both shaped by Metal itself:
/// <list type="bullet">
/// <item>The frame buffer is passed directly as <c>device const float4*</c>, with no bit reinterpretation like a ByteAddressBuffer;</item>
/// <item>The samplers are written into the source (<c>constexpr sampler</c>), one clamp and one repeat, without taking a binding slot -- the same approach as the decals and the water surface.
/// The semantics that the other three backends express with two static samplers (DX) or two image bindings (Vulkan) are just one sampler argument here.</item>
/// </list>
///
/// The depth must be <c>depth2d&lt;float&gt;</c> to be <c>read</c>: the software depth test wants point sampling,
/// and depth2d does not support filtered sampling anyway, which matches the requirement exactly.
/// </summary>
public static partial class WaterfallShaders
{
    /// <summary>Accessors shared by the vertex output and both entry points, in the same statement order as the GLSL CoreGlsl.</summary>
    const string CoreMsl = """
#include <metal_stdlib>
using namespace metal;

struct WaterfallVertex {
    float4 position [[position]];
    float3 world;
    // Interpolated geometric normal and longitudinal tangent; the pixel side re-orthogonalizes, so there is no need to normalize each vertex perfectly here.
    float3 normal;
    float3 tangent;
    // x = lateral [0,1], y = arc length [0,1], z = the longitudinal parameter after the acceleration remap (used for scrolling).
    float3 uv;
    // Integer interpolation does not go through linear filtering; the flat qualifier is explicit to avoid relying on compiler default behavior.
    uint slot [[flat]];
};

float4 WfF(const device float4* data, int i) { return data[i]; }
int WfOffset(uint index) { return 16 + int(index) * 48; }
float4 WfTransform(const device float4* data, float4 p, int i) {
    return p.x * data[i] + p.y * data[i + 1] + p.z * data[i + 2] + p.w * data[i + 3];
}

// Analytic mist for the sheet itself; see WaterfallShaders.Hlsl for why this is an exponential rather than a volume
// sample. Slot 37.w is the extinction averaged along this sheet's eye ray, zero when no medium covers the path.
float WfFogTransmittance(float3 world, float3 eye, float sigma) {
    if (sigma <= 0.0f) return 1.0f;
    return exp(-sigma * distance(world, eye));
}

// Per-sheet placement matrix (slots 38..41): points carry w=1, directions carry w=0, and the translation only affects points.
// The normal follows rotation and non-uniform scaling automatically via the cross product of the two tangents, using the same derivation as the other three backends.
float3 WfSheetPoint(const device float4* data, int offset, float3 p) {
    return WfTransform(data, float4(p, 1.0f), offset + 38).xyz;
}

float3 WfSheetVector(const device float4* data, int offset, float3 v) {
    return WfTransform(data, float4(v, 0.0f), offset + 38).xyz;
}

// Control point segment: two float4 per point, the first is midpoint.xyz + half width, the second is the outward direction .x, 0, .z, 0.
// The ends are clamped rather than wrapped: Catmull-Rom needs p0/p3, and repeating the endpoint on itself keeps the curve from pinching to a point at the lip and the landing point.
float4 WfRowCenter(const device float4* data, int offset, uint count, float s) {
    float clamped = clamp(s, 0.0f, float(count - 1u));
    uint i1 = uint(floor(clamped));
    float f = clamped - float(i1);
    uint i0 = i1 > 0u ? i1 - 1u : 0u;
    uint i2 = min(i1 + 1u, count - 1u);
    uint i3 = min(i1 + 2u, count - 1u);
    float4 p0 = data[offset + int(i0) * 2];
    float4 p1 = data[offset + int(i1) * 2];
    float4 p2 = data[offset + int(i2) * 2];
    float4 p3 = data[offset + int(i3) * 2];
    return 0.5f * (2.0f * p1 + (p2 - p0) * f
        + (2.0f * p0 - 5.0f * p1 + 4.0f * p2 - p3) * f * f
        + (3.0f * p1 - p0 - 3.0f * p2 + p3) * f * f * f);
}

float4 WfRowOutward(const device float4* data, int offset, uint count, float s) {
    float clamped = clamp(s, 0.0f, float(count - 1u));
    uint i1 = uint(floor(clamped));
    float f = clamped - float(i1);
    uint i0 = i1 > 0u ? i1 - 1u : 0u;
    uint i2 = min(i1 + 1u, count - 1u);
    uint i3 = min(i1 + 2u, count - 1u);
    float4 p0 = data[offset + int(i0) * 2 + 1];
    float4 p1 = data[offset + int(i1) * 2 + 1];
    float4 p2 = data[offset + int(i2) * 2 + 1];
    float4 p3 = data[offset + int(i3) * 2 + 1];
    return 0.5f * (2.0f * p1 + (p2 - p0) * f
        + (2.0f * p0 - 5.0f * p1 + 4.0f * p2 - p3) * f * f
        + (3.0f * p1 - p0 - 3.0f * p2 + p3) * f * f * f);
}

// R/G are mapped once only and must be clamped; B/A, along with the normal and Flow, wrap and must repeat.
constexpr sampler wfClamp(coord::normalized, address::clamp_to_edge, filter::linear);
constexpr sampler wfWrap(coord::normalized, address::repeat, filter::linear);
""";

    /// <summary>
    /// Vertex side: expand the water band along the control points. Same statement order as the HLSL version; the only syntax differences are <c>[[vertex_id]]</c>
    /// instead of <c>SV_VertexID</c>, and a parameter list instead of assigning to an output struct.
    /// </summary>
    const string VertexMsl = """

// Six 2D corners, arranged as the two triangles of a quad. Single exit, for the same reason as the HLSL version.
float2 WfCorner(uint id) {
    float2 corner = float2(0.0f, 0.0f);
    if (id == 1u) corner = float2(1.0f, 0.0f);
    else if (id == 2u) corner = float2(1.0f, 1.0f);
    else if (id == 3u) corner = float2(0.0f, 0.0f);
    else if (id == 4u) corner = float2(1.0f, 1.0f);
    else if (id == 5u) corner = float2(0.0f, 1.0f);
    return corner;
}

// Remap from arc length to advection time. Falling speed grows with the drop (approximately v proportional to sqrt(v)), so scrolling directly with
// v would give the lip and the landing point the same texture speed, reading like a conveyor belt. Here we integrate 1/(0.35+1.65*sqrt(v)) and normalize:
// the antiderivative (x=sqrt(v)) = 1.2121*(x - c*ln(1+x/c)), c=0.35/1.65=0.2121; its value at x=1 is 0.6303,
// so the normalization constant is 1/0.6303 = 1.5866 -- which also absorbs the 1.2121 coefficient of the formula above.
// This guarantees WfAdvection(1)=1: the landing point's sample coordinate equals exactly repeat, aligning with the texture tiling count.
float WfAdvection(float v) {
    float x = sqrt(saturate(v));
    const float c = 0.2121212f;
    float value = x - c * log(1.0f + x / c);
    return saturate(value * 1.5866f);
}

vertex WaterfallVertex VSMain(uint id [[vertex_id]], const device float4* data [[buffer(0)]],
    constant uint4& params [[buffer(1)]]) {
    WaterfallVertex o;
    uint slot = params.x;
    int offset = WfOffset(slot);
    // count comes from the frame buffer and can't be trusted to be >= 2: clamp it so the later (count-1) does not underflow.
    uint count = max(uint(data[offset + 32].x), 2u);
    uint columns = max(params.y, 2u);
    uint subdiv = max(params.z, 1u);
    uint rows = (count - 1u) * subdiv + 1u;

    uint quad = id / 6u;
    uint corner = id % 6u;
    uint col = quad % (columns - 1u);
    uint row = quad / (columns - 1u);
    float2 c = WfCorner(corner);
    float fu = (float(col) + c.x) / float(columns - 1u);
    float fv = (float(row) + c.y) / float(rows - 1u);

    float s = fv * float(count - 1u);
    float4 centerRow = WfRowCenter(data, offset, count, s);
    float4 outRow = WfRowOutward(data, offset, count, s);
    // Zero-vector fallback: normalize(0) is NaN and would fling the whole sheet out of clip space (harder to track down than a wrong orientation).
    float3 outward = normalize(float3(outRow.x, 0.0f, outRow.z) + float3(0.0f, 0.0f, 1e-5f));
    float3 lateral = float3(outward.z, 0.0f, -outward.x);

    float halfWidth = max(centerRow.w, 0.02f);
    float bow = data[offset + 37].y;

    // Section: a closed ring that opens around the control point polyline. fu=0 is the front center, fu=0.5 is the back center hugging the rock,
    // the two sides (0.25 / 0.75) land exactly on the control point polyline -- the cliff face is right there, and the sides must not recede,
    // otherwise the software depth test would cull the whole sheet as being "inside the rock".
    // u (the texture horizontal coordinate) is taken from the lateral position rather than the ring parameter: the front half is therefore identical
    // pixel-for-pixel to the old single sheet, the sides are 0 / 1 (already bitten to transparent by alpha), the back is mirrored, and position and texture are continuous at the seam.
    float theta = fu * 6.28318530718f;
    float sinT = sin(theta);
    float cosT = cos(theta);
    float halfDepth = max(data[offset + 37].z, 0.04f) * 0.5f;

    // The front (cosT>0) gets an extra outward bulge: this is where the old single sheet's bow came from, so the front arc coincides with the old geometry.
    float front = max(cosT, 0.0f);
    float slope = halfDepth + 2.0f * bow * front;
    float3 localPosition = centerRow.xyz
        + lateral * (sinT * halfWidth)
        + outward * (cosT * halfDepth + bow * front * front);
    float u = sinT * 0.5f + 0.5f;

    // The normal uses a central difference of neighboring rows, and the ring tangent uses the analytic section derivative:
    // a difference would skew the tangent where the half width changes sharply, whereas the analytic form's direction always matches the section.
    float ds = 0.04f;
    float4 before = WfRowCenter(data, offset, count, max(s - ds, 0.0f));
    float4 after = WfRowCenter(data, offset, count, min(s + ds, float(count - 1u)));
    float3 localTangentV = after.xyz - before.xyz;
    float3 localTangentU = lateral * (cosT * halfWidth) + outward * (-sinT * slope);

    // Placement: compute the local shape first, then move it to world in one go. The tangents are transformed along with it, so the normal automatically follows rotation and non-uniform scaling.
    float3 position = WfSheetPoint(data, offset, localPosition);
    float3 tangentV = WfSheetVector(data, offset, localTangentV);
    float3 tangentU = WfSheetVector(data, offset, localTangentU);
    float3 sectionNormal = WfSheetVector(data, offset, lateral * (sinT * slope) + outward * (cosT * halfWidth));

    float3 normal = normalize(cross(tangentV, tangentU) + float3(0.0f, 1e-5f, 0.0f));
    if (dot(normal, sectionNormal) < 0.0f) normal = -normal;

    o.position = WfTransform(data, float4(position, 1.0f), 0);
    o.world = position;
    o.normal = normal;
    o.tangent = normalize(tangentV);
    o.uv = float3(u, fv, WfAdvection(fv));
    o.slot = slot;
    return o;
}
""";

    /// <summary>
    /// Pixel side: cartoon water body + foam. Same statement order as the HLSL version; the only syntax differences are <c>[[stage_in]]</c> and explicit texture arguments.
    ///
    /// The geometry is a closed section ring, so a single sheet occludes itself: this pass has no depth attachment, so both the back wall and the front wall pass the software depth test
    /// (both are in front of the scene depth). The back wall is darkened and faded by the orientation of the geometric normal against the view direction, whereas the front wall's
    /// backFace is 0 and is completely unaffected.
    /// </summary>
    const string PixelMsl = """

fragment float4 PSMain(WaterfallVertex in [[stage_in]], const device float4* data [[buffer(0)]],
    constant uint4& params [[buffer(1)]], depth2d<float> sceneDepth [[texture(0)]],
    texture2d<float> waterfallMask [[texture(1)]], texture2d<float> waterfallNormal [[texture(2)]],
    texture2d<float> waterfallFlow [[texture(3)]]) {
    int offset = WfOffset(in.slot);
    float4 rowHeader = data[offset + 32];  // count, arc, maxHalfWidth, flags
    float4 material0 = data[offset + 33];  // deep.rgb, opacity
    float4 material1 = data[offset + 34];  // foam.rgb, foamAmount
    float4 material2 = data[offset + 35];  // scroll, normalStrength, normalTiling, erosion
    float4 material3 = data[offset + 36];  // lipFoam, footFoam, edgeFoam, edgeSoftness
    float4 camera = data[4];
    float4 sun = data[6];                  // propagation direction.xyz, intensity
    float4 sunColor = data[7];
    float4 ambient = data[8];
    float time = camera.w;

    float sceneDepthValue = sceneDepth.read(uint2(in.position.xy));
    // Software depth test: this pass has no depth attachment. The bias absorbs the reconstruction error between the sheet and the rock surface at grazing angles.
    if (in.position.z > sceneDepthValue + 1e-6f) discard_fragment();

    float u = saturate(in.uv.x);
    float v = saturate(in.uv.y);
    float flowV = saturate(in.uv.z);
    float lateral = u * 2.0f - 1.0f;

    uint flags = uint(rowHeader.w);
    bool hasMask = (flags & 1u) != 0u;
    bool hasNormal = (flags & 2u) != 0u;
    bool hasFlow = (flags & 4u) != 0u;

    // The arc length determines how many times the texture repeats along V: counting 6 meters per segment, an 18-meter waterfall is exactly three segments.
    float repeat = clamp(max(rowHeader.y, 1.0f) / 6.0f, 1.0f, 6.0f);
    float vTiled = flowV * repeat - time * material2.x * repeat;

    // R/G are mapped once only: they draw the whole structure "from the lip to the landing point", and tiling would copy the outlet halfway down the slope.
    float4 maskStatic = float4(0.55f, 0.0f, 0.5f, 0.5f);
    if (hasMask) maskStatic = waterfallMask.sample(wfClamp, float2(u, v), level(0.0f));
    // B/A are periodic noise, switched to wrap sampling for the scrolling foam and erosion, without seams.
    float4 maskTiled = float4(0.55f, 0.0f, 0.5f, 0.5f);
    if (hasMask) maskTiled = waterfallMask.sample(wfWrap, float2(u * 2.0f, vTiled * 0.75f), level(0.0f));

    float4 flowSample = float4(0.5f, 0.5f, 0.5f, 1.0f);
    if (hasFlow) flowSample = waterfallFlow.sample(wfWrap, float2(u * 2.0f, vTiled), level(0.0f));

    // Flow.R is the lateral drift: it makes the strands sway left and right while moving down, avoiding the whole texture translating like a rigid body.
    float drift = (flowSample.r * 2.0f - 1.0f) * 0.06f;
    float tiling = max(material2.z, 0.25f);
    float2 normalA = float2(0.0f, 0.0f);
    float2 normalB = float2(0.0f, 0.0f);
    if (hasNormal) {
        normalA = waterfallNormal.sample(wfWrap,
            float2((u + drift) * tiling, vTiled * tiling), level(0.0f)).rg * 2.0f - 1.0f;
        // The second layer is overlaid in reverse at 1.63x frequency and 0.78x speed: a single-layer scroll turns into uniform translational bands under TAA.
        normalB = waterfallNormal.sample(wfWrap,
            float2((u - drift) * tiling * 1.63f + 0.37f, vTiled * tiling * 0.78f - 0.21f), level(0.0f)).rg * 2.0f - 1.0f;
    }
    float2 slope = (normalA + normalB) * (0.5f * material2.y);

    // Foam splits into four channels: the lip, the landing point, both side edges, and the B-channel scrolling foam bands.
    // The four are max'd rather than summed; otherwise their overlap would blow out to pure white and the cartoon shading hierarchy would be lost.
    float lip = 1.0f - saturate(v / max(material3.x, 1e-3f));
    float foot = saturate((v - (1.0f - max(material3.y, 1e-3f))) / max(material3.y, 1e-3f));
    float edge = 1.0f - abs(lateral);
    float edgeBand = saturate((edge - (1.0f - material3.z)) / max(material3.z, 1e-3f));
    float bands = saturate(maskTiled.b * 1.35f - 0.35f);
    float sheetFoam = saturate(maskStatic.g * 1.25f - 0.15f);
    float foam = saturate(max(max(sheetFoam * material1.w, bands * 0.45f),
        max(max(lip * 1.10f, foot * 0.85f), edgeBand * 0.55f)));

    // Edge: the geometry is wider than the visible water, and the alpha is bitten out into an organic silhouette by the A-channel noise.
    // A negative cut means it is already out of bounds; the foam uses a wider threshold so the white edge always pokes out a little further than the water body.
    float warp = maskTiled.a;
    float cut = edge - material3.w * (0.55f + 0.75f * warp);
    float edgeCut = smoothstep(0.0f, 0.10f, cut);
    float foamCut = smoothstep(-0.05f, 0.05f, cut);
    float holes = 1.0f - material2.w * smoothstep(0.62f, 0.95f, warp);

    // The R-channel strands also modulate thickness: the 0.60 lower bound ensures that even the thinnest strands are not fully transparent,
    // otherwise the 47% of zeros in the texture would tear the sheet into a sieve.
    float density = 0.60f + 0.40f * saturate(maskStatic.r * 1.6f);
    // Back wall test: the half whose geometric normal points away from the view direction is the far side of the water body.
    // Multiplying by 2.5 puts the transition within about 66 degrees -- it fades a little earlier when grazing the side, and is still 0 when facing head-on.
    // Reduced by only 70% rather than zeroed: seen from the side, this back wall is precisely the half that says "the water body has thickness".
    float3 view = normalize(camera.xyz - in.world);
    float backFace = saturate(-dot(normalize(in.normal), view) * 2.5f);
    float bodyAlpha = material0.w * density * edgeCut * holes;
    float foamAlpha = foam * 0.92f * foamCut;
    float alpha = max(bodyAlpha, foamAlpha);
    alpha *= 1.0f - 0.70f * backFace;
    // Leave a 1.5% soft transition at the top and bottom edges: the lip/landing point is where the geometry is truncated, and a hard cut would flash a bright line under TAA.
    alpha *= smoothstep(0.0f, 0.015f, v) * smoothstep(1.0f, 0.985f, v);
    alpha = saturate(alpha);

    // Normal: geometric normal + texture slope. First re-orthogonalize from the interpolated normal, to avoid the elongated quads skewing the tangent.
    float3 normal = normalize(in.normal);
    float3 tangent = normalize(in.tangent - normal * dot(normal, in.tangent));
    float3 bitangent = cross(normal, tangent);
    float3 shadedNormal = normalize(normal + bitangent * slope.x + tangent * slope.y);

    float3 toSun = -sun.xyz;
    float ndl = saturate(dot(shadedNormal, toSun));
    float3 ambientLight = ambient.rgb * ambient.w;
    float3 direct = sunColor.rgb * (sun.w * ndl);
    float3 body = material0.rgb * (ambientLight + direct * 0.85f);
    float3 foamLit = material1.rgb * (ambientLight * 1.25f + direct * (0.40f + 0.60f * ndl));
    // The back wall is also darkened: when two layers hugging the same texture coordinate overlap, reducing only the alpha makes the color look grey and bright,
    // reducing the brightness a little is what makes it read as "the water behind" rather than "fog in front".
    float3 color = mix(body, foamLit, saturate(foam));
    color *= 1.0f - 0.30f * backFace;

    float3 halfVector = normalize(view + toSun);
    color += sunColor.rgb * (sun.w * pow(saturate(dot(shadedNormal, halfVector)), 48.0f) * 0.55f);
    // A very light rim light: a cartoon-style sheet needs a bright edge when backlit, otherwise the whole thing smears into the background rocks.
    color += (ambientLight + direct) * pow(1.0f - saturate(dot(shadedNormal, view)), 4.0f) * 0.08f;

    // Fog last: alpha, not colour, because the fraction the sheet loses is exactly the fraction the already-fogged
    // background shows through.
    alpha *= WfFogTransmittance(in.world, camera.xyz, data[offset + 37].w);
    return float4(color, alpha);
}
""";

    /// <summary>The full source for compilation by Metal: the helper functions are written once and shared by both entry points.</summary>
    public const string Msl = CoreMsl + VertexMsl + PixelMsl;
}
