// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// HLSL variant of the river ribbon. The other three backends (GLSL / MSL / WGSL) implement the same semantics in their own
/// backend order; the frame buffer layout is the one described on <see cref="WaterRibbonMath"/> and is <b>slot-for-slot identical</b>
/// to <see cref="WaterfallMath"/>, which is why the four backends can draw a ribbon by swapping only the shader source.
///
/// Three structural differences from <see cref="WaterfallShaders"/>:
/// <list type="bullet">
/// <item>The cross-section is <b>flat and horizontal</b>, not a closed ring around the flow: the band is the centerline swept by
/// the lateral axis, so there is no thickness, no bow, no lip and no landing point;</item>
/// <item>The current runs <b>along</b> the band in meters per second. The scroll coordinate is <c>phase + arc - time * speed</c>
/// divided by a fixed 3 m repeat, so two neighbouring segments with the same speed are seamless in both direction and phase,
/// whereas a waterfall's v is normalized "lip to landing" and deliberately accelerates;</item>
/// <item>Occlusion uses the software depth test plus a soft shoreline: the alpha fades over the screen-space derivative of the
/// scene depth, so where the band meets the bank it dissolves instead of ending on a hard aliased line. The channel is only about
/// 13 cm deep, so there is no meaningful absorption to model - the tint is a screen-space shallow-water ramp.</item>
/// </list>
/// </summary>
public static partial class WaterRibbonShaders
{
    /// <summary>
    /// Frame header and ribbon slot accessors, shared by the vertex and pixel stages. Kept in one string because two copies
    /// would be two chances for the entry points to disagree about the same bytes.
    /// </summary>
    public const string FrameHlsl = """
ByteAddressBuffer ribbonFrame : register(t0);
Texture2D<float> sceneDepth : register(t1);
Texture2D<float4> ribbonMask : register(t2);
Texture2D<float4> ribbonNormal : register(t3);
Texture2D<float4> ribbonFlow : register(t4);
// A ribbon only ever samples with wrapping coordinates (the flow carries everything along), so the clamp sampler s0 that the
// waterfall needs for its once-mapped R/G is deliberately unused here.
SamplerState ribbonWrap : register(s1);
cbuffer RibbonParams : register(b0) { uint4 ribbonParams; };

float4 RbF(uint i) { return asfloat(ribbonFrame.Load4(i * 16)); }
uint RbOffset(uint index) { return 16u + index * 48u; }

// Row-vector transform, verbatim the same expression as WfTransform: the frame buffer is copied in System.Numerics row order,
// so this cannot be written as mul(M, p).
float4 RbTransform(float4 p, uint offset)
{
    return p.x * RbF(offset) + p.y * RbF(offset + 1) + p.z * RbF(offset + 2) + p.w * RbF(offset + 3);
}

// Per-ribbon placement (slots 38..41). Points carry w=1 and directions w=0, so translation applies only to points.
float3 RbPoint(uint offset, float3 p) { return RbTransform(float4(p, 1.0f), offset + 38u).xyz; }
float3 RbVector(uint offset, float3 v) { return RbTransform(float4(v, 0.0f), offset + 38u).xyz; }

// Control point block: two float4 per point, the first is center.xyz + half width, the second is flow.x, 0, flow.z, 0.
// The endpoints clamp rather than wrap, exactly like the waterfall's Catmull-Rom, so the first and last rows stay put.
float4 RbRowCenter(uint offset, uint count, float s)
{
    float clamped = clamp(s, 0.0f, (float)(count - 1u));
    uint i1 = (uint)floor(clamped);
    float f = clamped - (float)i1;
    uint i0 = i1 > 0u ? i1 - 1u : 0u;
    uint i2 = min(i1 + 1u, count - 1u);
    uint i3 = min(i1 + 2u, count - 1u);
    float4 p0 = RbF(offset + i0 * 2u);
    float4 p1 = RbF(offset + i1 * 2u);
    float4 p2 = RbF(offset + i2 * 2u);
    float4 p3 = RbF(offset + i3 * 2u);
    return 0.5f * (2.0f * p1 + (p2 - p0) * f
        + (2.0f * p0 - 5.0f * p1 + 4.0f * p2 - p3) * f * f
        + (3.0f * p1 - p0 - 3.0f * p2 + p3) * f * f * f);
}

float4 RbRowFlow(uint offset, uint count, float s)
{
    float clamped = clamp(s, 0.0f, (float)(count - 1u));
    uint i1 = (uint)floor(clamped);
    float f = clamped - (float)i1;
    uint i0 = i1 > 0u ? i1 - 1u : 0u;
    uint i2 = min(i1 + 1u, count - 1u);
    uint i3 = min(i1 + 2u, count - 1u);
    float4 p0 = RbF(offset + i0 * 2u + 1u);
    float4 p1 = RbF(offset + i1 * 2u + 1u);
    float4 p2 = RbF(offset + i2 * 2u + 1u);
    float4 p3 = RbF(offset + i3 * 2u + 1u);
    return 0.5f * (2.0f * p1 + (p2 - p0) * f
        + (2.0f * p0 - 5.0f * p1 + 4.0f * p2 - p3) * f * f
        + (3.0f * p1 - p0 - 3.0f * p2 + p3) * f * f * f);
}

struct RibbonVertex
{
    float4 position : SV_Position;
    float3 world : TEXCOORD0;
    // Horizontal normal (up after the placement rotation) and the longitudinal tangent, re-orthogonalized in the pixel stage.
    float3 normal : TEXCOORD1;
    float3 tangent : TEXCOORD2;
    // x = lateral [0,1] across the band, y = normalized arc [0,1], z = arc in meters (the scroll source).
    float3 uv : TEXCOORD3;
    nointerpolation uint slot : TEXCOORD4;
};
""";

    /// <summary>
    /// Vertex stage: expand the flat band along the control points.
    ///
    /// The mesh is (rows-1)×(columns-1) quads with 6 vertices each and no vertex buffer; row and column indices are decoded from
    /// <c>SV_VertexID</c>, exactly like the waterfall. The difference is what a column means: here it is a straight step across
    /// the band along the lateral axis, so the water surface is flat and the two bank columns sit exactly on the waterline.
    /// </summary>
    public const string VertexHlsl = """
// Six 2D corners of the quad. Single exit: fxc's flow analysis reports multiple-return functions as uninitialized (X4000).
float2 RbCorner(uint id)
{
    float2 corner = float2(0.0f, 0.0f);
    if (id == 1u) corner = float2(1.0f, 0.0f);
    else if (id == 2u) corner = float2(1.0f, 1.0f);
    else if (id == 3u) corner = float2(0.0f, 0.0f);
    else if (id == 4u) corner = float2(1.0f, 1.0f);
    else if (id == 5u) corner = float2(0.0f, 1.0f);
    return corner;
}

RibbonVertex VSMain(uint id : SV_VertexID)
{
    RibbonVertex o;
    uint slot = ribbonParams.x;
    uint offset = RbOffset(slot);
    // count comes from the frame buffer and cannot be trusted to be >= 2: clamp it so (count-1) does not underflow.
    uint count = max((uint)RbF(offset + 32u).x, 2u);
    uint columns = max(ribbonParams.y, 2u);
    uint subdiv = max(ribbonParams.z, 1u);
    uint rows = (count - 1u) * subdiv + 1u;

    uint quad = id / 6u;
    uint corner = id % 6u;
    uint col = quad % (columns - 1u);
    uint row = quad / (columns - 1u);
    float2 c = RbCorner(corner);
    float fu = ((float)col + c.x) / (float)(columns - 1u);
    float fv = ((float)row + c.y) / (float)(rows - 1u);

    float s = fv * (float)(count - 1u);
    float4 centerRow = RbRowCenter(offset, count, s);
    float4 flowRow = RbRowFlow(offset, count, s);
    // Zero-vector fallback: normalize(0) is NaN and would throw the whole band outside clip space.
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
    float4 before = RbRowCenter(offset, count, max(s - ds, 0.0f));
    float4 after = RbRowCenter(offset, count, min(s + ds, (float)(count - 1u)));
    float3 localTangent = after.xyz - before.xyz;

    // Placement last, so the shape is built in author space and the world matrix is applied once - the same order as the waterfall.
    float3 position = RbPoint(offset, localPosition);
    float3 tangent = RbVector(offset, localTangent);
    float3 lateralWorld = RbVector(offset, lateral);

    // cross(flow, lateral) = +Y in author space, which after the placement rotation is "up" for the water surface.
    // The tiny +Y bias inside the cross product keeps the sign stable when the tangent degenerates at a clamped endpoint.
    float3 normal = normalize(cross(tangent, lateralWorld) + float3(0.0f, 1e-4f, 0.0f));

    o.position = RbTransform(float4(position, 1.0f), 0u);
    o.world = position;
    o.normal = normal;
    o.tangent = tangent;
    // .z is the arc in meters; the shader adds the ribbon's phase to it, which is the whole seam-hiding trick.
    o.uv = float3(fu, fv, fv * RbF(offset + 32u).y);
    o.slot = slot;
    return o;
}
""";

    /// <summary>
    /// Pixel stage: shallow cartoon river water.
    ///
    /// Order: depth test -> soft shoreline -> sampling -> bank foam -> lighting. The depth test uses the unperturbed fragment
    /// position (a normal perturbation must not change occlusion), and it is the only thing keeping the band from painting over
    /// the bank it is tucked under.
    ///
    /// The absorption term is deliberately a screen-space ramp rather than a physical one: the channel is ~13 cm deep, so a
    /// Beer-Lambert term would be numerically indistinguishable from zero, and the depth buffer here is device depth with no
    /// linearization available in this pass. What the ramp buys is a tint that darkens slightly where the bed is far behind the
    /// surface, which reads as depth without pretending to be accurate.
    /// </summary>
    public const string PixelHlsl = """
float4 PSMain(RibbonVertex input) : SV_TARGET
{
    uint offset = RbOffset(input.slot);
    float4 rowHeader = RbF(offset + 32u);  // count, arc meters, maxHalfWidth, flags
    float4 material0 = RbF(offset + 33u);  // water color.rgb, opacity
    float4 material1 = RbF(offset + 34u);  // foam color.rgb, foam amount
    float4 material2 = RbF(offset + 35u);  // flow speed m/s, normal strength, normal tiling, unused
    float4 material3 = RbF(offset + 36u);  // edge foam, edge softness, depth meters, unused
    float4 material4 = RbF(offset + 37u);  // sort order, phase meters, 0, 0
    float4 camera = RbF(4u);
    float4 sun = RbF(6u);
    float4 sunColor = RbF(7u);
    float4 ambient = RbF(8u);
    float time = camera.w;

    float sceneDepthValue = sceneDepth.Load(int3((int2)input.position.xy, 0));
    float gap = sceneDepthValue - input.position.z;
    // Software depth test: this Pass has no depth attachment. The band is drawn after the opaque scene, so anything already
    // closer than the water surface (the bank it is tucked under, a rock standing in it) must win.
    if (gap < 0.0f) discard;

    // Soft shoreline: the classic soft-particle trick. Dividing by the screen-space depth slope makes the fade one or two
    // pixels wide at every distance, so the waterline dissolves into the bank instead of ending on a stair-stepped edge, and it
    // needs no linear depth to do it.
    float depthSlope = max(abs(ddx(sceneDepthValue)) + abs(ddy(sceneDepthValue)), 1e-7f);
    float shore = saturate(gap / (depthSlope * 2.5f));
    // A broader ramp from the same quantity, used only as the shallow/deep tint. 24 slopes wide is roughly "half the channel".
    float thickness = saturate(gap / (depthSlope * 24.0f));

    float u = saturate(input.uv.x);        // across the band [0,1]
    float v = saturate(input.uv.y);        // along the band [0,1]
    float lateral = u * 2.0f - 1.0f;

    uint flags = (uint)rowHeader.w;
    bool hasMask = (flags & 1u) != 0u;
    bool hasNormal = (flags & 2u) != 0u;
    bool hasFlow = (flags & 4u) != 0u;

    // Scroll coordinate in meters of world travel: phase + arc, minus time * speed. Because each segment carries the phase it
    // starts at, two neighbours with the same speed read the same world distance for every frame - direction and speed both
    // continuous across the joint, which is the property the river layout depends on.
    float travelled = material4.y + input.uv.z - time * max(material2.x, 0.0f);
    float vTiled = travelled / 3.0f;

    float4 mask = float4(0.55f, 0.0f, 0.5f, 0.5f);
    if (hasMask) mask = ribbonMask.SampleLevel(ribbonWrap, float2(u * 2.0f, vTiled * 0.75f), 0.0f);
    float4 flowSample = float4(0.5f, 0.5f, 0.5f, 1.0f);
    if (hasFlow) flowSample = ribbonFlow.SampleLevel(ribbonWrap, float2(u * 2.0f, vTiled * 1.3f), 0.0f);

    // Lateral drift so the strands sway as they travel, instead of the whole texture translating like a rigid body.
    float drift = (flowSample.r * 2.0f - 1.0f) * 0.05f;
    float tiling = max(material2.z, 0.25f);
    float2 normalA = float2(0.0f, 0.0f);
    float2 normalB = float2(0.0f, 0.0f);
    if (hasNormal)
    {
        normalA = ribbonNormal.SampleLevel(ribbonWrap,
            float2((u + drift) * tiling, vTiled), 0.0f).rg * 2.0f - 1.0f;
        // The second layer is overlaid at 1.63x frequency and 0.78x speed: one layer alone reads as a scrolling decal under TAA.
        normalB = ribbonNormal.SampleLevel(ribbonWrap,
            float2((u - drift) * tiling * 1.63f + 0.37f, vTiled * 0.78f - 0.21f), 0.0f).rg * 2.0f - 1.0f;
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

    // Lighting: the surface normal is mostly up, so the sun term is nearly constant across the band and the interest has to come
    // from the Fresnel rim and the foam. The sky stand-in is the frame's ambient color, since this Pass cannot sample the sky.
    float3 view = normalize(camera.xyz - input.world);
    float3 normal = normalize(input.normal);
    float3 tangent = normalize(input.tangent - normal * dot(normal, input.tangent));
    float3 bitangent = cross(normal, tangent);
    float3 shadedNormal = normalize(normal + bitangent * slope.x + tangent * slope.y);

    float3 toSun = -sun.xyz;
    float ndl = saturate(dot(shadedNormal, toSun));
    float3 ambientLight = ambient.rgb * ambient.w;
    float3 direct = sunColor.rgb * (sun.w * ndl);

    // Shallow water is pale and slightly desaturated; deeper water keeps the base color. thickness is the screen-space ramp.
    float3 shallow = lerp(material0.rgb, material1.rgb, 0.35f) * 1.35f;
    float3 body = lerp(shallow, material0.rgb, thickness) * (ambientLight + direct * 0.85f);

    float fresnel = pow(1.0f - saturate(dot(shadedNormal, view)), 4.0f);
    float3 color = lerp(body, ambientLight * 1.4f + sunColor.rgb * (sun.w * 0.15f), fresnel * 0.45f);

    float3 foamLit = material1.rgb * (ambientLight * 1.25f + direct * (0.40f + 0.60f * ndl));
    color = lerp(color, foamLit, saturate(foam));

    float3 halfVector = normalize(view + toSun);
    color += sunColor.rgb * (sun.w * pow(saturate(dot(shadedNormal, halfVector)), 64.0f) * 0.45f);
    color += (ambientLight + direct) * pow(1.0f - saturate(dot(shadedNormal, view)), 4.0f) * 0.06f;

    return float4(color, alpha);
}
""";
}
