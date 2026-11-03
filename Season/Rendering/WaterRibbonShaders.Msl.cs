// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// MSL variant of the river ribbon. All four backends share the same semantics and frame buffer layout (see
/// <see cref="WaterRibbonMath"/>); this one is only responsible for "reading the layout + evaluating", not for defining it.
///
/// The differences from the other three backends are only two, both shaped by Metal itself:
/// <list type="bullet">
/// <item>The frame buffer is passed directly as <c>device const float4*</c>, with no bit reinterpretation like a ByteAddressBuffer;</item>
/// <item>The sampler is written into the source (<c>constexpr sampler</c>, repeat addressing only), without taking a binding slot.
/// A ribbon only ever samples with wrapping coordinates, so the clamp sampler the waterfall needs has no counterpart here.</item>
/// </list>
///
/// The depth must be <c>depth2d&lt;float&gt;</c> to be <c>read</c>: the software depth test wants point sampling, and depth2d does
/// not support filtered sampling anyway, which matches the requirement exactly.
/// </summary>
public static partial class WaterRibbonShaders
{
    /// <summary>Accessors shared by both entry points, in the same statement order as the HLSL and GLSL cores.</summary>
    const string CoreMsl = """
#include <metal_stdlib>
using namespace metal;

struct RibbonVertex {
    float4 position [[position]];
    float3 world;
    // Horizontal normal (up after the placement rotation) and the longitudinal tangent, re-orthogonalized in the pixel stage.
    float3 normal;
    float3 tangent;
    // x = lateral [0,1] across the band, y = normalized arc [0,1], z = arc in meters (the scroll source).
    float3 uv;
    // Integer interpolation does not go through linear filtering; the flat qualifier is explicit to avoid relying on compiler default behavior.
    uint slot [[flat]];
};

float4 RbF(const device float4* data, int i) { return data[i]; }
int RbOffset(uint index) { return 16 + int(index) * 48; }
float4 RbTransform(const device float4* data, float4 p, int i) {
    return p.x * data[i] + p.y * data[i + 1] + p.z * data[i + 2] + p.w * data[i + 3];
}

// Per-ribbon placement matrix (slots 38..41): points carry w=1, directions carry w=0, and the translation only affects points.
float3 RbSheetPoint(const device float4* data, int offset, float3 p) {
    return RbTransform(data, float4(p, 1.0f), offset + 38).xyz;
}

float3 RbSheetVector(const device float4* data, int offset, float3 v) {
    return RbTransform(data, float4(v, 0.0f), offset + 38).xyz;
}

// Control point block: two float4 per point, the first is center.xyz + half width, the second is flow.x, 0, flow.z, 0.
// The endpoints clamp rather than wrap, exactly like the waterfall's Catmull-Rom, so the first and last rows stay put.
float4 RbRowCenter(const device float4* data, int offset, uint count, float s) {
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

float4 RbRowFlow(const device float4* data, int offset, uint count, float s) {
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

// A ribbon only ever samples with wrapping coordinates, so one sampler is enough (the waterfall needs a clamp one as well).
constexpr sampler rbWrap(coord::normalized, address::repeat, filter::linear);
""";

    /// <summary>
    /// Vertex side: expand the flat band along the control points. Unlike the waterfall, a column is a straight step across the
    /// band along the lateral axis, so the water surface is horizontal and the two bank columns sit on the waterline.
    /// </summary>
    const string VertexMsl = """

// Six 2D corners, arranged as the two triangles of a quad. Single exit, for the same reason as the other three backends.
float2 RbCorner(uint id) {
    float2 corner = float2(0.0f, 0.0f);
    if (id == 1u) corner = float2(1.0f, 0.0f);
    else if (id == 2u) corner = float2(1.0f, 1.0f);
    else if (id == 3u) corner = float2(0.0f, 0.0f);
    else if (id == 4u) corner = float2(1.0f, 1.0f);
    else if (id == 5u) corner = float2(0.0f, 1.0f);
    return corner;
}

vertex RibbonVertex VSMain(uint id [[vertex_id]], const device float4* data [[buffer(0)]],
    constant uint4& params [[buffer(1)]]) {
    RibbonVertex o;
    uint slot = params.x;
    int offset = RbOffset(slot);
    // count comes from the frame buffer and can't be trusted to be >= 2: clamp it so the later (count-1) does not underflow.
    uint count = max(uint(data[offset + 32].x), 2u);
    uint columns = max(params.y, 2u);
    uint subdiv = max(params.z, 1u);
    uint rows = (count - 1u) * subdiv + 1u;

    uint quad = id / 6u;
    uint corner = id % 6u;
    uint col = quad % (columns - 1u);
    uint row = quad / (columns - 1u);
    float2 c = RbCorner(corner);
    float fu = (float(col) + c.x) / float(columns - 1u);
    float fv = (float(row) + c.y) / float(rows - 1u);

    float s = fv * float(count - 1u);
    float4 centerRow = RbRowCenter(data, offset, count, s);
    float4 flowRow = RbRowFlow(data, offset, count, s);
    // Zero-vector fallback: normalize(0) is NaN and would throw the whole band out of clip space.
    float3 flow = normalize(float3(flowRow.x, 0.0f, flowRow.z) + float3(0.0f, 0.0f, 1e-5f));
    // The lateral axis is the flow turned 90 degrees in the XZ plane. Deriving it from the authored direction rather than from
    // the polyline is what keeps a bend at an even width: a tangent-derived normal pinches the inside of the turn.
    float3 lateral = float3(flow.z, 0.0f, -flow.x);

    float halfWidth = max(centerRow.w, 0.05f);
    // u spans [-1,1] across the band; the surface is flat, so the only vertical contribution is the authored Y.
    float u = fu * 2.0f - 1.0f;
    float3 localPosition = centerRow.xyz + lateral * (u * halfWidth);

    // Longitudinal tangent by central difference of neighbouring rows: cheap, and the pixel stage only needs its direction.
    float ds = 0.05f;
    float4 before = RbRowCenter(data, offset, count, max(s - ds, 0.0f));
    float4 after = RbRowCenter(data, offset, count, min(s + ds, float(count - 1u)));
    float3 localTangent = after.xyz - before.xyz;

    // Placement last, so the shape is built in author space and the world matrix is applied once.
    float3 position = RbSheetPoint(data, offset, localPosition);
    float3 tangent = RbSheetVector(data, offset, localTangent);
    float3 lateralWorld = RbSheetVector(data, offset, lateral);

    // cross(flow, lateral) = +Y in author space, which after the placement rotation is "up" for the water surface.
    // The tiny +Y bias inside the cross product keeps the sign stable when the tangent degenerates at a clamped endpoint.
    float3 normal = normalize(cross(tangent, lateralWorld) + float3(0.0f, 1e-4f, 0.0f));

    o.position = RbTransform(data, float4(position, 1.0f), 0);
    o.world = position;
    o.normal = normal;
    o.tangent = tangent;
    // .z is the arc in meters; the pixel side adds the ribbon's phase to it, which is the whole seam-hiding trick.
    o.uv = float3(fu, fv, fv * RbF(data, offset + 32).y);
    o.slot = slot;
    return o;
}
""";

    /// <summary>
    /// Pixel side: shallow cartoon river water. Order is depth test -> soft shoreline -> sampling -> bank foam -> lighting.
    /// The absorption term is deliberately a screen-space ramp rather than a physical one: the channel is ~13 cm deep, so a
    /// Beer-Lambert term would be numerically indistinguishable from zero, and this pass has no linear depth available.
    /// </summary>
    const string PixelMsl = """

fragment float4 PSMain(RibbonVertex in [[stage_in]], const device float4* data [[buffer(0)]],
    constant uint4& params [[buffer(1)]], depth2d<float> sceneDepth [[texture(0)]],
    texture2d<float> ribbonMask [[texture(1)]], texture2d<float> ribbonNormal [[texture(2)]],
    texture2d<float> ribbonFlow [[texture(3)]]) {
    int offset = RbOffset(in.slot);
    float4 rowHeader = data[offset + 32];  // count, arc meters, maxHalfWidth, flags
    float4 material0 = data[offset + 33];  // water color.rgb, opacity
    float4 material1 = data[offset + 34];  // foam color.rgb, foam amount
    float4 material2 = data[offset + 35];  // flow speed m/s, normal strength, normal tiling, unused
    float4 material3 = data[offset + 36];  // edge foam, edge softness, depth meters, unused
    float4 material4 = data[offset + 37];  // sort order, phase meters, 0, 0
    float4 camera = data[4];
    float4 sun = data[6];                  // propagation direction.xyz, intensity
    float4 sunColor = data[7];
    float4 ambient = data[8];
    float time = camera.w;

    float sceneDepthValue = sceneDepth.read(uint2(in.position.xy));
    float gap = sceneDepthValue - in.position.z;
    // Software depth test: this pass has no depth attachment. The band is drawn after the opaque scene, so anything already
    // closer than the water surface (the bank it is tucked under, a rock standing in it) must win.
    if (gap < 0.0f) discard_fragment();

    // Soft shoreline: the classic soft-particle trick. Dividing by the screen-space depth slope makes the fade one or two
    // pixels wide at every distance, so the waterline dissolves into the bank instead of ending on a stair-stepped edge.
    float depthSlope = max(abs(dfdx(sceneDepthValue)) + abs(dfdy(sceneDepthValue)), 1e-7f);
    float shore = saturate(gap / (depthSlope * 2.5f));
    // A broader ramp from the same quantity, used only as the shallow/deep tint. 24 slopes wide is roughly "half the channel".
    float thickness = saturate(gap / (depthSlope * 24.0f));

    float u = saturate(in.uv.x);   // across the band [0,1]
    float v = saturate(in.uv.y);   // along the band [0,1]
    float lateral = u * 2.0f - 1.0f;

    uint flags = uint(rowHeader.w);
    bool hasMask = (flags & 1u) != 0u;
    bool hasNormal = (flags & 2u) != 0u;
    bool hasFlow = (flags & 4u) != 0u;

    // Scroll coordinate in meters of world travel: phase + arc, minus time * speed. Because each segment carries the phase it
    // starts at, two neighbours with the same speed read the same world distance for every frame - direction and speed both
    // continuous across the joint, which is the property the river layout depends on.
    float travelled = material4.y + in.uv.z - time * max(material2.x, 0.0f);
    float vTiled = travelled / 3.0f;

    float4 mask = float4(0.55f, 0.0f, 0.5f, 0.5f);
    if (hasMask) mask = ribbonMask.sample(rbWrap, float2(u * 2.0f, vTiled * 0.75f), level(0.0f));
    float4 flowSample = float4(0.5f, 0.5f, 0.5f, 1.0f);
    if (hasFlow) flowSample = ribbonFlow.sample(rbWrap, float2(u * 2.0f, vTiled * 1.3f), level(0.0f));

    // Lateral drift so the strands sway as they travel, instead of the whole texture translating like a rigid body.
    float drift = (flowSample.r * 2.0f - 1.0f) * 0.05f;
    float tiling = max(material2.z, 0.25f);
    float2 normalA = float2(0.0f, 0.0f);
    float2 normalB = float2(0.0f, 0.0f);
    if (hasNormal) {
        normalA = ribbonNormal.sample(rbWrap, float2((u + drift) * tiling, vTiled), level(0.0f)).rg * 2.0f - 1.0f;
        // The second layer is overlaid at 1.63x frequency and 0.78x speed: one layer alone reads as a scrolling decal under TAA.
        normalB = ribbonNormal.sample(rbWrap,
            float2((u - drift) * tiling * 1.63f + 0.37f, vTiled * 0.78f - 0.21f), level(0.0f)).rg * 2.0f - 1.0f;
    }
    float2 slope = (normalA + normalB) * (0.5f * material2.y) * float2(0.5f, 1.0f);

    // Foam has two sources: the periodic bands carried by the flow, and the two bank lines. Both take a max rather than a sum,
    // otherwise their overlap blows straight to white and the cartoon tiers disappear.
    float edge = 1.0f - abs(lateral);
    float edgeBand = saturate((edge - (1.0f - material3.x)) / max(material3.x, 1e-3f));
    float bands = saturate(mask.b * 1.35f - 0.35f);
    float foam = saturate(max(bands * material1.w, edgeBand * 0.80f));

    // Bank alpha: the geometry reaches exactly the waterline, so the bite is only the A channel's organic wobble plus a
    // narrow taper. A river that ended on a straight line would read as a decal, not as water.
    float cut = edge - material3.y * (0.45f + 0.65f * mask.a);
    float edgeCut = smoothstep(0.0f, 0.12f, cut);
    float foamCut = smoothstep(-0.05f, 0.05f, cut);
    float density = 0.72f + 0.28f * saturate(mask.r * 1.6f);
    float bodyAlpha = material0.w * density * edgeCut;
    float foamAlpha = foam * 0.85f * foamCut;
    float alpha = saturate(max(bodyAlpha, foamAlpha) * shore);

    // Lighting: the surface normal is mostly up, so the sun term is nearly constant across the band and the interest has to
    // come from the Fresnel rim and the foam. The sky stand-in is the frame's ambient color, since this pass cannot sample the sky.
    float3 view = normalize(camera.xyz - in.world);
    float3 normal = normalize(in.normal);
    float3 tangent = normalize(in.tangent - normal * dot(normal, in.tangent));
    float3 bitangent = cross(normal, tangent);
    float3 shadedNormal = normalize(normal + bitangent * slope.x + tangent * slope.y);

    float3 toSun = -sun.xyz;
    float ndl = saturate(dot(shadedNormal, toSun));
    float3 ambientLight = ambient.rgb * ambient.w;
    float3 direct = sunColor.rgb * (sun.w * ndl);

    // Shallow water is pale and slightly desaturated; deeper water keeps the base color. thickness is the screen-space ramp.
    float3 shallow = mix(material0.rgb, material1.rgb, 0.35f) * 1.35f;
    float3 body = mix(shallow, material0.rgb, thickness) * (ambientLight + direct * 0.85f);

    float fresnel = pow(1.0f - saturate(dot(shadedNormal, view)), 4.0f);
    float3 color = mix(body, ambientLight * 1.4f + sunColor.rgb * (sun.w * 0.15f), fresnel * 0.45f);

    float3 foamLit = material1.rgb * (ambientLight * 1.25f + direct * (0.40f + 0.60f * ndl));
    color = mix(color, foamLit, saturate(foam));

    float3 halfVector = normalize(view + toSun);
    color += sunColor.rgb * (sun.w * pow(saturate(dot(shadedNormal, halfVector)), 64.0f) * 0.45f);
    color += (ambientLight + direct) * pow(1.0f - saturate(dot(shadedNormal, view)), 4.0f) * 0.06f;

    return float4(color, alpha);
}
""";

    /// <summary>The full source for compilation by Metal: the helper functions are written once and shared by both entry points.</summary>
    public const string Msl = CoreMsl + VertexMsl + PixelMsl;
}
