// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// GLSL variant of the waterfall sheet (Vulkan / Linux / Android), semantically identical to the HLSL in <see cref="WaterfallShaders"/>.
///
/// The differences from the original are syntactic only; four points are kept per Vulkan's rules:
/// <list type="bullet">
/// <item>The frame buffer is read as a <c>vec4</c> array (<c>std430</c>), the same memory as HLSL's <c>Load4(i*16)</c>;</item>
/// <item>glslang's simplified C interface only accepts <c>main</c>, so the vertex and fragment stages are two separate sources, each concatenating the same <c>CoreGlsl</c>;</item>
/// <item>Depth uses <c>texelFetch</c> rather than <c>texture</c>: the software depth test is a per-pixel comparison, and any filtering would mix in neighboring pixels'
/// depth, producing a ring of incorrect occlusion around the sheet's edges;</item>
/// <item>Samplers are <b>per-binding immutable</b>, and since R/G needs clamping while B/A and the normal need repeating, the same Mask must be bound twice
/// (binding 0 and binding 3 of <c>set=1</c>), the same semantics as DX's two static samplers.</item>
/// </list>
/// </summary>
public static partial class WaterfallShaders
{
    /// <summary>
    /// Accessors for the frame buffer and sampling sources, shared by the vertex and fragment stages. <c>WfTransform</c> keeps HLSL's row-vector form
    /// (<c>p.x*WfF(i)+...</c>), because <see cref="WaterfallMath"/> copies <see cref="System.Numerics.Matrix4x4"/>
    /// into the buffer by row, and the rows fetched by <c>vec4</c> index in GLSL are exactly the same rows.
    /// </summary>
    const string CoreGlsl = """
layout(std430,set=0,binding=0) readonly buffer WaterfallFrame { vec4 wfF[]; };
layout(set=0,binding=1) uniform sampler2D sceneDepth;
// set=1 is the per-sheet texture table: 0 is clamp sampling (R/G maps only once), 1/2 are repeat sampling (normal, Flow),
// 3 is a repeat-sampling copy of the same Mask (B/A needs wrap); Vulkan cannot attach two samplers to one image.
layout(set=1,binding=0) uniform sampler2D waterfallMask;
layout(set=1,binding=1) uniform sampler2D waterfallNormal;
layout(set=1,binding=2) uniform sampler2D waterfallFlow;
layout(set=1,binding=3) uniform sampler2D waterfallMaskTiled;
layout(push_constant) uniform WaterfallParams { uvec4 waterfallParams; };

vec4 WfF(int i) { return wfF[i]; }
int WfOffset(uint index) { return 16+int(index)*48; }
vec4 WfTransform(vec4 p,int i) { return p.x*WfF(i)+p.y*WfF(i+1)+p.z*WfF(i+2)+p.w*WfF(i+3); }
// Analytic mist for the sheet itself; see WaterfallShaders.Hlsl for why this is an exponential rather than a volume
// sample. Slot 37.w is the extinction averaged along this sheet's eye ray, zero when no medium covers the path.
float WfFogTransmittance(vec3 world,vec3 eye,float sigma) {
    if(sigma<=0.0) return 1.0;
    return exp(-sigma*distance(world,eye));
}

// Per-sheet placement matrix (slots 38..41). Points carry w=1 and directions carry w=0: translation applies only to points,
// and the normal follows rotation and non-uniform scale automatically via the cross of the two tangents, the same derivation as the other three backends.
vec3 WfSheetPoint(int offset,vec3 p) { return WfTransform(vec4(p,1.0),offset+38).xyz; }
vec3 WfSheetVector(int offset,vec3 v) { return WfTransform(vec4(v,0.0),offset+38).xyz; }

// Control point section: two float4 per point, the first is midpoint.xyz + half width, the second is outward direction.x, 0, .z, 0.
// The ends clamp rather than wrap: Catmull-Rom needs p0/p3, and repeating the endpoint on itself keeps the curve from tapering at the lip and landing point.
vec4 WfRowCenter(int offset,uint count,float s) {
    float clamped = clamp(s,0.0,float(count-1u));
    uint i1 = uint(floor(clamped));
    float f = clamped-float(i1);
    uint i0 = i1>0u?i1-1u:0u;
    uint i2 = min(i1+1u,count-1u);
    uint i3 = min(i1+2u,count-1u);
    vec4 p0 = WfF(offset+int(i0)*2);
    vec4 p1 = WfF(offset+int(i1)*2);
    vec4 p2 = WfF(offset+int(i2)*2);
    vec4 p3 = WfF(offset+int(i3)*2);
    return 0.5*(2.0*p1+(p2-p0)*f
        +(2.0*p0-5.0*p1+4.0*p2-p3)*f*f
        +(3.0*p1-p0-3.0*p2+p3)*f*f*f);
}

vec4 WfRowOutward(int offset,uint count,float s) {
    float clamped = clamp(s,0.0,float(count-1u));
    uint i1 = uint(floor(clamped));
    float f = clamped-float(i1);
    uint i0 = i1>0u?i1-1u:0u;
    uint i2 = min(i1+1u,count-1u);
    uint i3 = min(i1+2u,count-1u);
    vec4 p0 = WfF(offset+int(i0)*2+1);
    vec4 p1 = WfF(offset+int(i1)*2+1);
    vec4 p2 = WfF(offset+int(i2)*2+1);
    vec4 p3 = WfF(offset+int(i3)*2+1);
    return 0.5*(2.0*p1+(p2-p0)*f
        +(2.0*p0-5.0*p1+4.0*p2-p3)*f*f
        +(3.0*p1-p0-3.0*p2+p3)*f*f*f);
}
""";

    /// <summary>
    /// Vertex stage: unwrap the water band along the control points. Same statement order as the HLSL version; the only syntactic differences are <c>gl_VertexIndex</c>
    /// instead of <c>SV_VertexID</c>, and <c>out</c> variables instead of an output struct.
    ///
    /// The mesh is (rows-1)×(columns-1) quads, 6 vertices per quad, with no vertex buffer.
    /// The columns are not a straight line across the width, but a closed section ring around the control point polyline (the first and last columns coincide in both position and texture).
    /// </summary>
    public const string VertexGlsl = "#version 450\n" + CoreGlsl + """

layout(location=0) out vec3 vWorld;
layout(location=1) out vec3 vNormal;
layout(location=2) out vec3 vTangent;
// x = lateral [0,1], y = arc length [0,1], z = advection-mapped longitudinal parameter (used for scrolling).
layout(location=3) out vec3 vUv;
layout(location=4) flat out uint vSlot;

// Six 2D corners, arranged as the two triangles of the quad. Single exit, same reasoning as the HLSL version.
vec2 WfCorner(uint id) {
    vec2 corner = vec2(0.0,0.0);
    if (id == 1u) corner = vec2(1.0,0.0);
    else if (id == 2u) corner = vec2(1.0,1.0);
    else if (id == 3u) corner = vec2(0.0,0.0);
    else if (id == 4u) corner = vec2(1.0,1.0);
    else if (id == 5u) corner = vec2(0.0,1.0);
    return corner;
}

// Arc length -> advection time remap. A falling body's speed grows with the drop (approximately v ∝ sqrt(v)), and scrolling directly with v would give
// the lip and the landing point the same texture speed, reading like a conveyor belt. Here we integrate 1/(0.35+1.65√v) and normalize:
// The antiderivative (x=√v) = 1.2121·(x − c·ln(1+x/c)), c=0.35/1.65=0.2121; its value at x=1 is 0.6303,
// so the normalization constant is 1/0.6303 = 1.5866 -- it also absorbs the 1.2121 coefficient above.
// This guarantees WfAdvection(1)=1: the landing point's sampling coordinate equals exactly repeat, aligned with the texture tiling count.
float WfAdvection(float v) {
    float x = sqrt(clamp(v,0.0,1.0));
    const float c = 0.2121212;
    float value = x-c*log(1.0+x/c);
    return clamp(value*1.5866,0.0,1.0);
}

void main() {
    uint slot = waterfallParams.x;
    int offset = WfOffset(slot);
    // count comes from the frame buffer and cannot be trusted to be >= 2: clamp it so the later (count-1) does not underflow.
    uint count = max(uint(WfF(offset+32).x),2u);
    uint columns = max(waterfallParams.y,2u);
    uint subdiv = max(waterfallParams.z,1u);
    uint rows = (count-1u)*subdiv+1u;

    uint id = uint(gl_VertexIndex);
    uint quad = id/6u;
    uint corner = id%6u;
    uint col = quad%(columns-1u);
    uint row = quad/(columns-1u);
    vec2 c = WfCorner(corner);
    float fu = (float(col)+c.x)/float(columns-1u);
    float fv = (float(row)+c.y)/float(rows-1u);

    float s = fv*float(count-1u);
    vec4 centerRow = WfRowCenter(offset,count,s);
    vec4 outRow = WfRowOutward(offset,count,s);
    // Zero-vector fallback: normalize(0) is NaN and would throw the whole sheet outside clip space (harder to locate than a wrong orientation).
    vec3 outward = normalize(vec3(outRow.x,0.0,outRow.z)+vec3(0.0,0.0,1e-5));
    vec3 lateral = vec3(outward.z,0.0,-outward.x);

    float halfWidth = max(centerRow.w,0.02);
    float bow = WfF(offset+37).y;

    // Section: a closed ring opened around the control point polyline. fu=0 is the front center, fu=0.5 is the back center against the rock,
    // and the two sides (0.25 / 0.75) fall exactly on the control point polyline -- the cliff face is right there, so the sides cannot pull back,
    // otherwise the whole sheet would be culled by the software depth test as being "inside the rock".
    // u (the texture horizontal coordinate) is taken from the lateral position rather than the ring parameter: the front half is therefore pixel-for-pixel identical to the old single sheet,
    // the two sides are 0 / 1 (the texture has already been bitten to transparent by alpha) and the back is its mirror, with both position and texture continuous at the seam.
    float theta = fu*6.28318530718;
    float sinT = sin(theta);
    float cosT = cos(theta);
    float halfDepth = max(WfF(offset+37).z,0.04)*0.5;

    // The front (cosT>0) gets an extra outward bulge: that is where the old single sheet's bow came from, so the front arc coincides with the old geometry.
    float front = max(cosT,0.0);
    float slope = halfDepth+2.0*bow*front;
    vec3 localPosition = centerRow.xyz
        +lateral*(sinT*halfWidth)
        +outward*(cosT*halfDepth+bow*front*front);
    float u = sinT*0.5+0.5;

    // The normal uses a central difference of neighboring rows, and the ring tangent uses the analytic section derivative:
    // a difference would skew the tangent where the half width changes sharply, whereas the analytic form's direction always matches the section.
    float ds = 0.04;
    vec4 before = WfRowCenter(offset,count,max(s-ds,0.0));
    vec4 after = WfRowCenter(offset,count,min(s+ds,float(count-1u)));
    vec3 localTangentV = after.xyz-before.xyz;
    vec3 localTangentU = lateral*(cosT*halfWidth)+outward*(-sinT*slope);

    // Placement: compute the local shape first, then move it to world in one go. The tangents are transformed along with it, so the normal automatically follows rotation and non-uniform scaling.
    vec3 position = WfSheetPoint(offset,localPosition);
    vec3 tangentV = WfSheetVector(offset,localTangentV);
    vec3 tangentU = WfSheetVector(offset,localTangentU);
    vec3 sectionNormal = WfSheetVector(offset,lateral*(sinT*slope)+outward*(cosT*halfWidth));

    vec3 normal = normalize(cross(tangentV,tangentU)+vec3(0.0,1e-5,0.0));
    if (dot(normal,sectionNormal) < 0.0) normal = -normal;

    gl_Position = WfTransform(vec4(position,1.0),0);
    vWorld = position;
    vNormal = normal;
    vTangent = normalize(tangentV);
    vUv = vec3(u,fv,WfAdvection(fv));
    vSlot = slot;
}
""";

    /// <summary>
    /// Pixel side: cartoon water body + foam. Same statement order as the HLSL version; the only
    /// syntax differences are <c>gl_FragCoord</c> and <c>texelFetch</c>/<c>textureLod</c>.
    ///
    /// The geometry is a closed section ring, so a single sheet occludes itself: this pass has no
    /// depth attachment, so both the back wall and the front wall pass the software depth test
    /// (both are in front of the scene depth). The back wall is darkened and faded by the
    /// orientation of the geometric normal against the view direction, whereas the front wall's
    /// backFace is 0 and is completely unaffected.
    /// </summary>
    public const string PixelGlsl = "#version 450\n" + CoreGlsl + """

layout(location=0) in vec3 vWorld;
layout(location=1) in vec3 vNormal;
layout(location=2) in vec3 vTangent;
layout(location=3) in vec3 vUv;
layout(location=4) flat in uint vSlot;
layout(location=0) out vec4 outColor;

void main() {
    int offset = WfOffset(vSlot);
    vec4 rowHeader = WfF(offset+32);  // count, arc, maxHalfWidth, flags
    vec4 material0 = WfF(offset+33);  // deep.rgb, opacity
    vec4 material1 = WfF(offset+34);  // foam.rgb, foamAmount
    vec4 material2 = WfF(offset+35);  // scroll, normalStrength, normalTiling, erosion
    vec4 material3 = WfF(offset+36);  // lipFoam, footFoam, edgeFoam, edgeSoftness
    vec4 camera = WfF(4);
    vec4 sun = WfF(6);                // propagation direction.xyz, intensity
    vec4 sunColor = WfF(7);
    vec4 ambient = WfF(8);
    float time = camera.w;

    float sceneDepthValue = texelFetch(sceneDepth,ivec2(gl_FragCoord.xy),0).r;
    // Software depth test: this pass has no depth attachment. The bias absorbs the reconstruction error between the sheet and the rock surface at grazing angles.
    if (gl_FragCoord.z > sceneDepthValue+1e-6) discard;

    float u = clamp(vUv.x,0.0,1.0);
    float v = clamp(vUv.y,0.0,1.0);
    float flowV = clamp(vUv.z,0.0,1.0);
    float lateral = u*2.0-1.0;

    uint flags = uint(rowHeader.w);
    bool hasMask = (flags&1u) != 0u;
    bool hasNormal = (flags&2u) != 0u;
    bool hasFlow = (flags&4u) != 0u;

    // The arc length determines how many times the texture repeats along V: counting 6 meters per segment, an 18-meter waterfall is exactly three segments.
    float repeat = clamp(max(rowHeader.y,1.0)/6.0,1.0,6.0);
    float vTiled = flowV*repeat-time*material2.x*repeat;

    // R/G are mapped once only: they draw the whole structure "from the lip to the landing point", and tiling would copy the outlet halfway down the slope.
    vec4 maskStatic = vec4(0.55,0.0,0.5,0.5);
    if (hasMask) maskStatic = textureLod(waterfallMask,vec2(u,v),0.0);
    // B/A are periodic noise, switched to wrap sampling for the scrolling foam and erosion, without seams.
    vec4 maskTiled = vec4(0.55,0.0,0.5,0.5);
    if (hasMask) maskTiled = textureLod(waterfallMaskTiled,vec2(u*2.0,vTiled*0.75),0.0);

    vec4 flowSample = vec4(0.5,0.5,0.5,1.0);
    if (hasFlow) flowSample = textureLod(waterfallFlow,vec2(u*2.0,vTiled),0.0);

    // Flow.R is the lateral drift: it makes the strands sway left and right while moving down, avoiding the whole texture translating like a rigid body.
    float drift = (flowSample.r*2.0-1.0)*0.06;
    float tiling = max(material2.z,0.25);
    vec2 normalA = vec2(0.0,0.0);
    vec2 normalB = vec2(0.0,0.0);
    if (hasNormal) {
        normalA = textureLod(waterfallNormal,
            vec2((u+drift)*tiling,vTiled*tiling),0.0).rg*2.0-1.0;
        // The second layer is overlaid in reverse at 1.63x frequency and 0.78x speed: a single-layer scroll turns into uniform translational bands under TAA.
        normalB = textureLod(waterfallNormal,
            vec2((u-drift)*tiling*1.63+0.37,vTiled*tiling*0.78-0.21),0.0).rg*2.0-1.0;
    }
    vec2 slope = (normalA+normalB)*(0.5*material2.y);

    // Foam splits into four channels: the lip, the landing point, both side edges, and the B-channel scrolling foam bands.
    // The four are max'd rather than summed; otherwise their overlap would blow out to pure white and the cartoon shading hierarchy would be lost.
    float lip = 1.0-clamp(v/max(material3.x,1e-3),0.0,1.0);
    float foot = clamp((v-(1.0-max(material3.y,1e-3)))/max(material3.y,1e-3),0.0,1.0);
    float edge = 1.0-abs(lateral);
    float edgeBand = clamp((edge-(1.0-material3.z))/max(material3.z,1e-3),0.0,1.0);
    float bands = clamp(maskTiled.b*1.35-0.35,0.0,1.0);
    float sheetFoam = clamp(maskStatic.g*1.25-0.15,0.0,1.0);
    float foam = clamp(max(max(sheetFoam*material1.w,bands*0.45),
        max(max(lip*1.10,foot*0.85),edgeBand*0.55)),0.0,1.0);

    // Edge: the geometry is wider than the visible water, and the alpha is bitten out into an organic silhouette by the A-channel noise.
    // A negative cut means it is already out of bounds; the foam uses a wider threshold so the white edge always pokes out a little further than the water body.
    float warp = maskTiled.a;
    float cut = edge-material3.w*(0.55+0.75*warp);
    float edgeCut = smoothstep(0.0,0.10,cut);
    float foamCut = smoothstep(-0.05,0.05,cut);
    float holes = 1.0-material2.w*smoothstep(0.62,0.95,warp);

    // The R-channel strands also modulate thickness: the 0.60 lower bound ensures that even the thinnest strands are not fully transparent,
    // otherwise the 47% of zeros in the texture would tear the sheet into a sieve.
    float density = 0.60+0.40*clamp(maskStatic.r*1.6,0.0,1.0);
    // Back wall test: the half whose geometric normal points away from the view direction is the far side of the water body.
    // Multiplying by 2.5 puts the transition within about 66 degrees -- it fades a little earlier when grazing the side, and is still 0 when facing head-on.
    // Reduced by only 70% rather than zeroed: seen from the side, this back wall is precisely the half that says "the water body has thickness".
    vec3 view = normalize(camera.xyz-vWorld);
    float backFace = clamp(-dot(normalize(vNormal),view)*2.5,0.0,1.0);
    float bodyAlpha = material0.w*density*edgeCut*holes;
    float foamAlpha = foam*0.92*foamCut;
    float alpha = max(bodyAlpha,foamAlpha);
    alpha *= 1.0-0.70*backFace;
    // Leave a 1.5% soft transition at the top and bottom edges: the lip/landing point is where the geometry is truncated, and a hard cut would flash a bright line under TAA.
    alpha *= smoothstep(0.0,0.015,v)*smoothstep(1.0,0.985,v);
    alpha = clamp(alpha,0.0,1.0);

    // Normal: geometric normal + texture slope. First re-orthogonalize from the interpolated normal, to avoid the elongated quads skewing the tangent.
    vec3 normal = normalize(vNormal);
    vec3 tangent = normalize(vTangent-normal*dot(normal,vTangent));
    vec3 bitangent = cross(normal,tangent);
    vec3 shadedNormal = normalize(normal+bitangent*slope.x+tangent*slope.y);

    vec3 toSun = -sun.xyz;
    float ndl = clamp(dot(shadedNormal,toSun),0.0,1.0);
    vec3 ambientLight = ambient.rgb*ambient.w;
    vec3 direct = sunColor.rgb*(sun.w*ndl);
    vec3 body = material0.rgb*(ambientLight+direct*0.85);
    vec3 foamLit = material1.rgb*(ambientLight*1.25+direct*(0.40+0.60*ndl));
    // The back wall is also darkened: when two layers hugging the same texture coordinate overlap, reducing only the alpha makes the color look grey and bright,
    // reducing the brightness a little is what makes it read as "the water behind" rather than "fog in front".
    vec3 color = mix(body,foamLit,clamp(foam,0.0,1.0));
    color *= 1.0-0.30*backFace;

    vec3 halfVector = normalize(view+toSun);
    color += sunColor.rgb*(sun.w*pow(clamp(dot(shadedNormal,halfVector),0.0,1.0),48.0)*0.55);
    // A very light rim light: a cartoon-style sheet needs a bright edge when backlit, otherwise the whole thing smears into the background rocks.
    color += (ambientLight+direct)*pow(1.0-clamp(dot(shadedNormal,view),0.0,1.0),4.0)*0.08;

    // Fog last: alpha, not colour, because the fraction the sheet loses is exactly the fraction the already-fogged
    // background shows through.
    alpha *= WfFogTransmittance(vWorld,camera.xyz,WfF(offset+37).w);
    outColor = vec4(color,alpha);
}
""";
}
