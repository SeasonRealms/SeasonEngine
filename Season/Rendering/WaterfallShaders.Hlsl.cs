// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// HLSL variant of the waterfall sheet. The other three backends (GLSL / MSL / WGSL) each implement the same semantics in their own backend order;
/// the frame buffer layout follows <see cref="WaterfallMath"/> -- this file only "reads the layout and evaluates", it no longer defines the layout.
///
/// Two structural differences from <see cref="WaterShaders"/>:
/// <list type="bullet">
/// <item>The geometry is not a horizontal rectangle, but a water body unwrapped along the control points and closed in a ring around the section: the vertex shader must do
/// Catmull-Rom interpolation, ring unwrapping around the section, analytic tangents and accelerated UV, so the whole control point range is placed in the frame buffer rather than a vertex buffer;</item>
/// <item>There is no refraction snapshot and it does not sample the scene color either: a sheet is a translucent water film, and the blend base is the color attachment itself.
/// Occlusion still relies on the software depth test (this Pass attaches no depth attachment), so SceneDepth is only a sampling source.</item>
/// </list>
///
/// The R/G channels are deliberately non-tileable: they describe the whole waterfall's structure, u maps the sheet width only once and v maps the arc length only once.
/// Scrolling therefore happens only in B/A (periodic Worley / fbm) and the normal texture, so wrapping never exposes a seam.
/// </summary>
public static partial class WaterfallShaders
{
    /// <summary>
    /// Accessors for the frame header and sheet array, shared by the vertex and pixel shaders. Splitting them would make the two entry points disagree on reading the same layout.
    /// </summary>
    public const string FrameHlsl = """
ByteAddressBuffer waterfallFrame : register(t0);
Texture2D<float> sceneDepth : register(t1);
Texture2D<float4> waterfallMask : register(t2);
Texture2D<float4> waterfallNormal : register(t3);
Texture2D<float4> waterfallFlow : register(t4);
// s0 is for the once-mapped R/G (clamp), s1 is for the wrapping B/A, normal and Flow (repeat).
SamplerState waterfallClamp : register(s0);
SamplerState waterfallWrap : register(s1);
cbuffer WaterfallParams : register(b0) { uint4 waterfallParams; };

float4 WfF(uint i) { return asfloat(waterfallFrame.Load4(i * 16)); }
uint WfOffset(uint index) { return 16u + index * 48u; }

// Verbatim identical to the transform in WaterShaders / GLSL / MSL / WGSL: treat p as a row vector and dot with each row.
// It cannot be written as mul(M, p): HLSL's mul multiplies a matrix by a column vector, whereas the frame buffer is copied in System.Numerics row order.
float4 WfTransform(float4 p, uint offset)
{
    return p.x * WfF(offset) + p.y * WfF(offset + 1) + p.z * WfF(offset + 2) + p.w * WfF(offset + 3);
}

// Per-sheet placement matrix (slots 38..41): a local-to-world step after the control points are written.
// The control points themselves keep author coordinates, so when the same shape is copied into multiple waterfalls their control point arrays can be identical
// and only this differs -- position, orientation and scale all follow it.
//
// Two entry points rather than one, because points have w=1 and directions have w=0: translation should apply only to points.
// Normals do not need an inverse-transpose matrix: just cross the two transformed tangents,
// cross(Ja, Jb) = det(J)·J⁻ᵀ(a×b), which after normalization is parallel to the inverse-transpose normal (the sign is corrected by the section normal).
float3 WfSheetPoint(uint offset, float3 p)
{
    return WfTransform(float4(p, 1.0f), offset + 38u).xyz;
}

float3 WfSheetVector(uint offset, float3 v)
{
    return WfTransform(float4(v, 0.0f), offset + 38u).xyz;
}

// Control point section: two float4 per point, the first is midpoint.xyz + half width, the second is outward direction.x, 0, .z, 0.
// The ends clamp rather than wrap: Catmull-Rom needs p0/p3, and repeating the endpoint on itself keeps the curve from tapering at the lip and landing point.
float4 WfRowCenter(uint offset, uint count, float s)
{
    float clamped = clamp(s, 0.0f, (float)(count - 1u));
    uint i1 = (uint)floor(clamped);
    float f = clamped - (float)i1;
    uint i0 = i1 > 0u ? i1 - 1u : 0u;
    uint i2 = min(i1 + 1u, count - 1u);
    uint i3 = min(i1 + 2u, count - 1u);
    float4 p0 = WfF(offset + i0 * 2u);
    float4 p1 = WfF(offset + i1 * 2u);
    float4 p2 = WfF(offset + i2 * 2u);
    float4 p3 = WfF(offset + i3 * 2u);
    return 0.5f * (2.0f * p1 + (p2 - p0) * f
        + (2.0f * p0 - 5.0f * p1 + 4.0f * p2 - p3) * f * f
        + (3.0f * p1 - p0 - 3.0f * p2 + p3) * f * f * f);
}

float4 WfRowOutward(uint offset, uint count, float s)
{
    float clamped = clamp(s, 0.0f, (float)(count - 1u));
    uint i1 = (uint)floor(clamped);
    float f = clamped - (float)i1;
    uint i0 = i1 > 0u ? i1 - 1u : 0u;
    uint i2 = min(i1 + 1u, count - 1u);
    uint i3 = min(i1 + 2u, count - 1u);
    float4 p0 = WfF(offset + i0 * 2u + 1u);
    float4 p1 = WfF(offset + i1 * 2u + 1u);
    float4 p2 = WfF(offset + i2 * 2u + 1u);
    float4 p3 = WfF(offset + i3 * 2u + 1u);
    return 0.5f * (2.0f * p1 + (p2 - p0) * f
        + (2.0f * p0 - 5.0f * p1 + 4.0f * p2 - p3) * f * f
        + (3.0f * p1 - p0 - 3.0f * p2 + p3) * f * f * f);
}

struct WaterfallVertex
{
    float4 position : SV_Position;
    float3 world : TEXCOORD0;
    // Interpolated geometric normal and longitudinal tangent; the pixel stage re-orthogonalizes, so there is no need to normalize perfectly per vertex here.
    float3 normal : TEXCOORD1;
    float3 tangent : TEXCOORD2;
    // x = lateral [0,1], y = arc length [0,1], z = advection-mapped longitudinal parameter (used for scrolling).
    float3 uv : TEXCOORD3;
    nointerpolation uint slot : TEXCOORD4;
};
""";

    /// <summary>
    /// Vertex stage: unwrap the water band along the control points.
    ///
    /// The mesh is (rows-1)×(columns-1) quads, 6 vertices per quad, with no vertex buffer -- the row and column indices are all
    /// decoded from <c>SV_VertexID</c>. The row parameter s does Catmull-Rom over the control point array, so even sparse control points produce no polyline;
    /// the columns are not a straight line across the width, but a closed section ring around the control point polyline (the first and last columns coincide in both position and texture),
    /// so head-on it is a water band and from the side it is a water body with thickness; the section orientation is recomputed from that row's outward,
    /// and when several control points have different outward directions the sheet bends along the cliff face.
    /// </summary>
    public const string VertexHlsl = """
// Six 2D corners, arranged as the two triangles of the quad. Single exit: fxc's flow analysis conservatively reports functions with
// multiple returns as uninitialized (X4000), which is masked by inlining on a direct call but surfaces once wrapped by another layer of function.
float2 WfCorner(uint id)
{
    float2 corner = float2(0.0f, 0.0f);
    if (id == 1u) corner = float2(1.0f, 0.0f);
    else if (id == 2u) corner = float2(1.0f, 1.0f);
    else if (id == 3u) corner = float2(0.0f, 0.0f);
    else if (id == 4u) corner = float2(1.0f, 1.0f);
    else if (id == 5u) corner = float2(0.0f, 1.0f);
    return corner;
}

// Arc length -> advection time remap. A falling body's speed grows with the drop (approximately v ∝ sqrt(v)), and scrolling directly with v would give
// the lip and the landing point the same texture speed, reading like a conveyor belt. Here we integrate 1/(0.35+1.65√v) and normalize:
// the lip gets more texture and the landing point less, so as the sampling coordinate advances at a constant rate the world-space speed automatically accelerates.
// The antiderivative (x=√v) = 1.2121·(x − c·ln(1+x/c)), c=0.35/1.65=0.2121; its value at x=1 is 0.6303,
// so the normalization constant is 1/0.6303 = 1.5866 -- it also absorbs the 1.2121 coefficient above.
// This guarantees WfAdvection(1)=1: the landing point's sampling coordinate equals exactly repeat, aligned with the texture tiling count.
float WfAdvection(float v)
{
    float x = sqrt(saturate(v));
    const float c = 0.2121212f;
    float value = x - c * log(1.0f + x / c);
    return saturate(value * 1.5866f);
}


WaterfallVertex VSMain(uint id : SV_VertexID)
{
    WaterfallVertex o;
    uint slot = waterfallParams.x;
    uint offset = WfOffset(slot);
    // count comes from the frame buffer and cannot be trusted to be >= 2: clamp it so the later (count-1) does not underflow to 0xFFFFFFFF.
    uint count = max((uint)WfF(offset + 32u).x, 2u);
    uint columns = max(waterfallParams.y, 2u);
    uint subdiv = max(waterfallParams.z, 1u);
    uint rows = (count - 1u) * subdiv + 1u;

    uint quad = id / 6u;
    uint corner = id % 6u;
    uint col = quad % (columns - 1u);
    uint row = quad / (columns - 1u);
    float2 c = WfCorner(corner);
    float fu = ((float)col + c.x) / (float)(columns - 1u);
    float fv = ((float)row + c.y) / (float)(rows - 1u);

    float s = fv * (float)(count - 1u);
    float4 centerRow = WfRowCenter(offset, count, s);
    float4 outRow = WfRowOutward(offset, count, s);
    // Zero-vector fallback: normalize(0) is NaN and would throw the whole sheet outside clip space (harder to locate than a wrong orientation).
    float3 outward = normalize(float3(outRow.x, 0.0f, outRow.z) + float3(0.0f, 0.0f, 1e-5f));
    float3 lateral = float3(outward.z, 0.0f, -outward.x);

    float u = fu * 2.0f - 1.0f;
    float halfWidth = max(centerRow.w, 0.02f);
    float bow = WfF(offset + 37u).y;

    // Section: a closed ring opened around the control point polyline. fu=0 is the front center, fu=0.5 is the back center against the rock,
    // and the two sides (0.25 / 0.75) fall exactly on the control point polyline -- the cliff face is right there, so the sides cannot pull back,
    // otherwise the whole sheet would be culled by the software depth test as being "inside the rock".
    // u (the texture horizontal coordinate) is taken from the lateral position rather than the ring parameter: the front half is therefore pixel-for-pixel identical to the old single sheet,
    // the two sides are 0 / 1 (the texture has already been bitten to transparent by alpha) and the back is its mirror, with both position and texture continuous at the seam.
    float theta = fu * 6.28318530718f;
    float sinT = sin(theta);
    float cosT = cos(theta);
    float halfDepth = max(WfF(offset + 37u).z, 0.04f) * 0.5f;

    // The front (cosT>0) gets an extra outward bulge: that is where the old single sheet's bow came from, so the front arc coincides with the old geometry.
    float front = max(cosT, 0.0f);
    float slope = halfDepth + 2.0f * bow * front;
    float3 localPosition = centerRow.xyz
        + lateral * (sinT * halfWidth)
        + outward * (cosT * halfDepth + bow * front * front);
    u = sinT * 0.5f + 0.5f;

    // The normal uses a central difference of neighboring rows, and the ring tangent uses the analytic section derivative:
    // a difference would skew the tangent where the half width changes sharply, whereas the analytic form's direction always matches the section.
    float ds = 0.04f;
    float4 before = WfRowCenter(offset, count, max(s - ds, 0.0f));
    float4 after = WfRowCenter(offset, count, min(s + ds, (float)(count - 1u)));
    float3 localTangentV = after.xyz - before.xyz;
    float3 localTangentU = lateral * (cosT * halfWidth) + outward * (-sinT * slope);

    // Placement: compute the local shape first, then move it to the world in one step. The tangents are transformed too, so the normal automatically follows rotation and non-uniform scale.
    float3 position = WfSheetPoint(offset, localPosition);
    float3 tangentV = WfSheetVector(offset, localTangentV);
    float3 tangentU = WfSheetVector(offset, localTangentU);
    float3 sectionNormal = WfSheetVector(offset, lateral * (sinT * slope) + outward * (cosT * halfWidth));

    float3 normal = normalize(cross(tangentV, tangentU) + float3(0.0f, 1e-5f, 0.0f));
    if (dot(normal, sectionNormal) < 0.0f) normal = -normal;

    o.position = WfTransform(float4(position, 1.0f), 0u);
    o.world = position;
    o.normal = normal;
    o.tangent = normalize(tangentV);
    o.uv = float3(u, fv, WfAdvection(fv));
    o.slot = slot;
    return o;
}
""";

    /// <summary>
    /// Pixel stage: cartoon water body + foam.
    ///
    /// Order: depth test -> sampling -> foam/edges -> back-face attenuation -> lighting. The depth test uses the unperturbed fragment position (normal perturbation should not change occlusion),
    /// consistent with the water surface; occlusion relies entirely on this line, so this Pass's color target never draws trees on the hillside in front of the sheet.
    ///
    /// The geometry is a closed section ring, so the same sheet occludes itself: this Pass has no depth attachment, and both the back wall and the front wall pass the
    /// software depth test (both are in front of the scene depth). The back wall is darkened and faded using the geometric normal's orientation against the view direction,
    /// while the front wall's backFace is 0 and is completely unaffected.
    /// </summary>
    /// <summary>
    /// Analytic mist for the sheet itself. This Pass is ordered after FogComposite, so the color target already holds the
    /// fogged background; without applying the same medium here the curtain composites at full contrast on top of it and
    /// reads as floating in front of the fog rather than inside it.
    ///
    /// Analytic instead of a sample of fogIntegrated on purpose: the sheet only needs the medium's transmittance at one
    /// point, and an exponential with the volume's extinction gets that without giving this pipeline its own 3D-texture
    /// descriptor set on four backends. In-scatter is not added because it is already in the target - attenuating alpha
    /// lets it show through - and the sheet's own radiance loses exactly the fraction the fog has replaced.
    ///
    /// Header slot 37.w carries that sheet's own extinction, averaged along the eye ray rather than sampled at the sheet:
    /// a curtain standing beyond a fog bank is covered by the medium in between, and there is none at the curtain itself.
    /// Per sheet rather than per frame because it is an integral along that sheet's own ray.
    /// </summary>
    public const string FogHlsl = """
float WfFogTransmittance(float3 world, float3 eye, float sigma)
{
    if (sigma <= 0.0f) return 1.0f;
    return exp(-sigma * distance(world, eye));
}
""";

    public const string PixelHlsl = FogHlsl + """
float4 PSMain(WaterfallVertex input) : SV_TARGET
{
    uint offset = WfOffset(input.slot);
    float4 rowHeader = WfF(offset + 32u);  // count, arc, maxHalfWidth, flags
    float4 material0 = WfF(offset + 33u);  // deep.rgb, opacity
    float4 material1 = WfF(offset + 34u);  // foam.rgb, foamAmount
    float4 material2 = WfF(offset + 35u);  // scroll, normalStrength, normalTiling, erosion
    float4 material3 = WfF(offset + 36u);  // lipFoam, footFoam, edgeFoam, edgeSoftness
    float4 camera = WfF(4u);
    float4 sun = WfF(6u);                  // propagation direction.xyz, intensity
    float4 sunColor = WfF(7u);
    float4 ambient = WfF(8u);
    float time = camera.w;

    float sceneDepthValue = sceneDepth.Load(int3((int2)input.position.xy, 0));
    // Software depth test: this Pass has no depth attachment. A bias absorbs the reconstruction error between the sheet and the rock surface at grazing angles.
    if (input.position.z > sceneDepthValue + 1e-6f) discard;

    float u = saturate(input.uv.x);
    float v = saturate(input.uv.y);
    float flowV = saturate(input.uv.z);
    float lateral = u * 2.0f - 1.0f;

    uint flags = (uint)rowHeader.w;
    bool hasMask = (flags & 1u) != 0u;
    bool hasNormal = (flags & 2u) != 0u;
    bool hasFlow = (flags & 4u) != 0u;

    // Arc length decides how many times the texture repeats longitudinally: counted per 6-meter segment, an 18-meter waterfall is exactly three segments.
    // It no longer samples directly by world meters because the sheet's length is something the author changes at any time and the repeat count must follow it.
    float repeat = clamp(max(rowHeader.y, 1.0f) / 6.0f, 1.0f, 6.0f);
    float vTiled = flowV * repeat - time * material2.x * repeat;

    // R/G maps only once: it draws the whole structure "from lip to landing point", and tiling would duplicate the outlet halfway down the slope.
    float4 maskStatic = float4(0.55f, 0.0f, 0.5f, 0.5f);
    if (hasMask) maskStatic = waterfallMask.SampleLevel(waterfallClamp, float2(u, v), 0.0f);
    // B/A is periodic noise and uses wrap sampling for the scrolling foam and erosion, so no seam is exposed.
    float4 maskTiled = float4(0.55f, 0.0f, 0.5f, 0.5f);
    if (hasMask) maskTiled = waterfallMask.SampleLevel(waterfallWrap, float2(u * 2.0f, vTiled * 0.75f), 0.0f);

    float4 flowSample = float4(0.5f, 0.5f, 0.5f, 1.0f);
    if (hasFlow) flowSample = waterfallFlow.SampleLevel(waterfallWrap, float2(u * 2.0f, vTiled), 0.0f);

    // Flow.R is the lateral drift: it makes the strands sway left and right as they travel down, avoiding the whole texture translating like a rigid body.
    float drift = (flowSample.r * 2.0f - 1.0f) * 0.06f;
    float tiling = max(material2.z, 0.25f);
    float2 normalA = float2(0.0f, 0.0f);
    float2 normalB = float2(0.0f, 0.0f);
    if (hasNormal)
    {
        normalA = waterfallNormal.SampleLevel(waterfallWrap,
            float2((u + drift) * tiling, vTiled * tiling), 0.0f).rg * 2.0f - 1.0f;
        // The second layer overlaps in reverse at 1.63× frequency and 0.78× speed: a single layer would scroll into uniformly translating bands under TAA.
        normalB = waterfallNormal.SampleLevel(waterfallWrap,
            float2((u - drift) * tiling * 1.63f + 0.37f, vTiled * tiling * 0.78f - 0.21f), 0.0f).rg * 2.0f - 1.0f;
    }
    float2 slope = (normalA + normalB) * (0.5f * material2.y);

    // Foam comes in four paths: lip, landing point, two side edges, and the B channel's scrolling foam bands.
    // The four take the max rather than summing, otherwise where they overlap it would blow straight to pure white and the cartoon shading tiers would be lost.
    float lip = 1.0f - saturate(v / max(material3.x, 1e-3f));
    float foot = saturate((v - (1.0f - max(material3.y, 1e-3f))) / max(material3.y, 1e-3f));
    float edge = 1.0f - abs(lateral);
    float edgeBand = saturate((edge - (1.0f - material3.z)) / max(material3.z, 1e-3f));
    float bands = saturate(maskTiled.b * 1.35f - 0.35f);
    float sheetFoam = saturate(maskStatic.g * 1.25f - 0.15f);
    float foam = saturate(max(max(sheetFoam * material1.w, bands * 0.45f),
        max(max(lip * 1.10f, foot * 0.85f), edgeBand * 0.55f)));

    // Edges: the geometry is wider than the visible water band and alpha is bitten into an organic outline by the A channel noise.
    // A negative cut means it is already out of bounds; foam uses a wider threshold so the white edge always protrudes a little beyond the water body.
    float warp = maskTiled.a;
    float cut = edge - material3.w * (0.55f + 0.75f * warp);
    float edgeCut = smoothstep(0.0f, 0.10f, cut);
    float foamCut = smoothstep(-0.05f, 0.05f, cut);
    float holes = 1.0f - material2.w * smoothstep(0.62f, 0.95f, warp);

    // The R channel strands also modulate thickness: a 0.60 floor guarantees that even the thinnest strands are not fully transparent,
    // otherwise the zero values making up 47% of the texture would tear the sheet into a sieve.
    float density = 0.60f + 0.40f * saturate(maskStatic.r * 1.6f);
    // Back-wall test: a geometric normal facing against the view marks the far half of the water body.
    // Multiplying by 2.5 puts the turnoff within about 66° -- it fades a little earlier when grazing the side and is still 0 face-on.
    // Only 70% is attenuated rather than zeroed: seen from the side, this back wall is exactly the half that says "the water body has thickness",
    // and zeroing it would turn the waterfall back into a sheet of paper.
    float3 view = normalize(camera.xyz - input.world);
    float backFace = saturate(-dot(normalize(input.normal), view) * 2.5f);
    float bodyAlpha = material0.w * density * edgeCut * holes;
    float foamAlpha = foam * 0.92f * foamCut;
    float alpha = max(bodyAlpha, foamAlpha);
    alpha *= 1.0f - 0.70f * backFace;
    // Leave a 1.5% soft transition at the top and bottom edges: the lip/landing point is where the geometry is truncated, and a hard cut would flicker a bright line under TAA.
    alpha *= smoothstep(0.0f, 0.015f, v) * smoothstep(1.0f, 0.985f, v);
    alpha = saturate(alpha);

    // Normal: geometric normal + texture slope. Re-orthogonalize from the interpolated normal first to avoid long quad strips skewing the tangent.
    float3 normal = normalize(input.normal);
    float3 tangent = normalize(input.tangent - normal * dot(normal, input.tangent));
    float3 bitangent = cross(normal, tangent);
    float3 shadedNormal = normalize(normal + bitangent * slope.x + tangent * slope.y);

    float3 toSun = -sun.xyz;
    float ndl = saturate(dot(shadedNormal, toSun));
    float3 ambientLight = ambient.rgb * ambient.w;
    float3 direct = sunColor.rgb * (sun.w * ndl);
    float3 body = material0.rgb * (ambientLight + direct * 0.85f);
    float3 foamLit = material1.rgb * (ambientLight * 1.25f + direct * (0.40f + 0.60f * ndl));
    // The back wall is darkened too: when two layers sharing the same texture coordinate are composited, attenuating only alpha looks gray and bright;
    // attenuating the brightness a little is what reads as "water behind" rather than "fog in front".
    float3 color = lerp(body, foamLit, saturate(foam));
    color *= 1.0f - 0.30f * backFace;

    float3 halfVector = normalize(view + toSun);
    color += sunColor.rgb * (sun.w * pow(saturate(dot(shadedNormal, halfVector)), 48.0f) * 0.55f);
    // A very light rim light: a cartoon-style sheet needs a bright edge when backlit, otherwise the whole thing smears into the background rock.
    color += (ambientLight + direct) * pow(1.0f - saturate(dot(shadedNormal, view)), 4.0f) * 0.08f;

    // Fog last: everything above is the sheet's own radiance, and the medium only decides how much of it reaches the
    // eye. Attenuating alpha rather than colour is what puts the sheet *inside* the fog: the fraction it loses is
    // exactly the fraction the already-fogged background shows through.
    alpha *= WfFogTransmittance(input.world, camera.xyz, WfF(offset + 37u).w);
    return float4(color, alpha);
}
""";
}
