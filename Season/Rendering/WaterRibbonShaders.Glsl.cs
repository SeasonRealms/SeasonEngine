// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// GLSL variant of the river ribbon (Vulkan / Linux / Android), semantically identical to the HLSL in
/// <see cref="WaterRibbonShaders"/>. Every statement order matches the HLSL version; only the syntax changes.
///
/// Three points are kept per Vulkan's rules:
/// <list type="bullet">
/// <item>The frame buffer is read as a <c>vec4</c> array (<c>std430</c>), the same memory as HLSL's <c>Load4(i*16)</c>;</item>
/// <item>glslang's simplified C interface only accepts <c>main</c>, so the vertex and fragment stages are two separate sources,
/// each concatenating the same <c>CoreGlsl</c>;</item>
/// <item>The ribbon only ever samples with wrapping coordinates, so it uses binding 3 (the wrap-sampling copy of the mask) and
/// never binding 0. The binding is still declared so the source reads like the shared descriptor set it is compiled against;
/// the clamp sampler is simply unused, and the descriptor is still written by the pipeline.</item>
/// </list>
/// </summary>
public static partial class WaterRibbonShaders
{
    /// <summary>
    /// Accessors shared by the vertex and fragment stages. <c>RbTransform</c> keeps HLSL's row-vector form
    /// (<c>p.x*RbF(i)+...</c>) because <see cref="WaterRibbonMath"/> copies <see cref="System.Numerics.Matrix4x4"/>
    /// into the buffer by row.
    /// </summary>
    const string CoreGlsl = """
layout(std430,set=0,binding=0) readonly buffer RibbonFrame { vec4 rbF[]; };
layout(set=0,binding=1) uniform sampler2D sceneDepth;
// set=1 is the same per-draw texture table the waterfall uses (0 clamp, 1/2 wrap, 3 a wrap-sampling copy of the mask).
// A river never needs the clamped copy, but the layout is shared, so the binding is declared and left unused.
layout(set=1,binding=0) uniform sampler2D ribbonMask;
layout(set=1,binding=1) uniform sampler2D ribbonNormal;
layout(set=1,binding=2) uniform sampler2D ribbonFlow;
layout(set=1,binding=3) uniform sampler2D ribbonMaskTiled;
layout(push_constant) uniform RibbonParams { uvec4 ribbonParams; };

vec4 RbF(int i) { return rbF[i]; }
int RbOffset(uint index) { return 16+int(index)*48; }
vec4 RbTransform(vec4 p,int i) { return p.x*RbF(i)+p.y*RbF(i+1)+p.z*RbF(i+2)+p.w*RbF(i+3); }

// Per-ribbon placement matrix (slots 38..41). Points carry w=1 and directions carry w=0, so translation applies only to points.
vec3 RbSheetPoint(int offset,vec3 p) { return RbTransform(vec4(p,1.0),offset+38).xyz; }
vec3 RbSheetVector(int offset,vec3 v) { return RbTransform(vec4(v,0.0),offset+38).xyz; }

// Control point block: two vec4 per point, the first is center.xyz + half width, the second is flow.x, 0, flow.z, 0.
// The endpoints clamp rather than wrap, exactly like the waterfall's Catmull-Rom, so the first and last rows stay put.
vec4 RbRowCenter(int offset,uint count,float s) {
    float clamped = clamp(s,0.0,float(count-1u));
    uint i1 = uint(floor(clamped));
    float f = clamped-float(i1);
    uint i0 = i1>0u?i1-1u:0u;
    uint i2 = min(i1+1u,count-1u);
    uint i3 = min(i1+2u,count-1u);
    vec4 p0 = RbF(offset+int(i0)*2);
    vec4 p1 = RbF(offset+int(i1)*2);
    vec4 p2 = RbF(offset+int(i2)*2);
    vec4 p3 = RbF(offset+int(i3)*2);
    return 0.5*(2.0*p1+(p2-p0)*f
        +(2.0*p0-5.0*p1+4.0*p2-p3)*f*f
        +(3.0*p1-p0-3.0*p2+p3)*f*f*f);
}

vec4 RbRowFlow(int offset,uint count,float s) {
    float clamped = clamp(s,0.0,float(count-1u));
    uint i1 = uint(floor(clamped));
    float f = clamped-float(i1);
    uint i0 = i1>0u?i1-1u:0u;
    uint i2 = min(i1+1u,count-1u);
    uint i3 = min(i1+2u,count-1u);
    vec4 p0 = RbF(offset+int(i0)*2+1);
    vec4 p1 = RbF(offset+int(i1)*2+1);
    vec4 p2 = RbF(offset+int(i2)*2+1);
    vec4 p3 = RbF(offset+int(i3)*2+1);
    return 0.5*(2.0*p1+(p2-p0)*f
        +(2.0*p0-5.0*p1+4.0*p2-p3)*f*f
        +(3.0*p1-p0-3.0*p2+p3)*f*f*f);
}
""";

    /// <summary>
    /// Vertex stage: expand the flat band along the control points. Unlike the waterfall, a column is a straight step across
    /// the band along the lateral axis, so the water surface is horizontal and the two bank columns sit on the waterline.
    /// </summary>
    public const string VertexGlsl = "#version 450\n" + CoreGlsl + """

layout(location=0) out vec3 vWorld;
layout(location=1) out vec3 vNormal;
layout(location=2) out vec3 vTangent;
// x = lateral [0,1] across the band, y = normalized arc [0,1], z = arc in meters (the scroll source).
layout(location=3) out vec3 vUv;
layout(location=4) flat out uint vSlot;

vec2 RbCorner(uint id) {
    vec2 corner = vec2(0.0,0.0);
    if (id == 1u) corner = vec2(1.0,0.0);
    else if (id == 2u) corner = vec2(1.0,1.0);
    else if (id == 3u) corner = vec2(0.0,0.0);
    else if (id == 4u) corner = vec2(1.0,1.0);
    else if (id == 5u) corner = vec2(0.0,1.0);
    return corner;
}

void main() {
    uint slot = ribbonParams.x;
    int offset = RbOffset(slot);
    // count comes from the frame buffer and cannot be trusted to be >= 2: clamp it so the later (count-1) does not underflow.
    uint count = max(uint(RbF(offset+32).x),2u);
    uint columns = max(ribbonParams.y,2u);
    uint subdiv = max(ribbonParams.z,1u);
    uint rows = (count-1u)*subdiv+1u;

    uint id = uint(gl_VertexIndex);
    uint quad = id/6u;
    uint corner = id%6u;
    uint col = quad%(columns-1u);
    uint row = quad/(columns-1u);
    vec2 c = RbCorner(corner);
    float fu = (float(col)+c.x)/float(columns-1u);
    float fv = (float(row)+c.y)/float(rows-1u);

    float s = fv*float(count-1u);
    vec4 centerRow = RbRowCenter(offset,count,s);
    vec4 flowRow = RbRowFlow(offset,count,s);
    // Zero-vector fallback: normalize(0) is NaN and would throw the whole band outside clip space.
    vec3 flow = normalize(vec3(flowRow.x,0.0,flowRow.z)+vec3(0.0,0.0,1e-5));
    // The lateral axis is the flow turned 90 degrees in the XZ plane. Deriving it from the authored direction rather than
    // from the polyline is what keeps a bend at an even width: a tangent-derived normal pinches the inside of the turn.
    vec3 lateral = vec3(flow.z,0.0,-flow.x);

    float halfWidth = max(centerRow.w,0.05);
    // u spans [-1,1] across the band; the surface is flat, so the only vertical contribution is the authored Y.
    float u = fu*2.0-1.0;
    vec3 localPosition = centerRow.xyz+lateral*(u*halfWidth);

    // Longitudinal tangent by central difference of neighbouring rows: cheap, and the pixel stage only needs its direction.
    float ds = 0.05;
    vec4 before = RbRowCenter(offset,count,max(s-ds,0.0));
    vec4 after = RbRowCenter(offset,count,min(s+ds,float(count-1u)));
    vec3 localTangent = after.xyz-before.xyz;

    // Placement last, so the shape is built in author space and the world matrix is applied once.
    vec3 position = RbSheetPoint(offset,localPosition);
    vec3 tangent = RbSheetVector(offset,localTangent);
    vec3 lateralWorld = RbSheetVector(offset,lateral);

    // cross(flow, lateral) = +Y in author space, which after the placement rotation is "up" for the water surface.
    // The tiny +Y bias inside the cross product keeps the sign stable when the tangent degenerates at a clamped endpoint.
    vec3 normal = normalize(cross(tangent,lateralWorld)+vec3(0.0,1e-4,0.0));

    gl_Position = RbTransform(vec4(position,1.0),0);
    vWorld = position;
    vNormal = normal;
    vTangent = tangent;
    // .z is the arc in meters; the fragment stage adds the ribbon's phase to it, which is the whole seam-hiding trick.
    vUv = vec3(fu,fv,fv*RbF(offset+32).y);
    vSlot = slot;
}
""";

    /// <summary>
    /// Fragment stage: shallow cartoon river water. Order is depth test -> soft shoreline -> sampling -> bank foam -> lighting.
    /// The absorption term is deliberately a screen-space ramp rather than a physical one: the channel is ~13 cm deep, so a
    /// Beer-Lambert term would be numerically indistinguishable from zero, and this pass has no linear depth available.
    /// </summary>
    public const string PixelGlsl = "#version 450\n" + CoreGlsl + """

layout(location=0) in vec3 vWorld;
layout(location=1) in vec3 vNormal;
layout(location=2) in vec3 vTangent;
layout(location=3) in vec3 vUv;
layout(location=4) flat in uint vSlot;
layout(location=0) out vec4 outColor;

void main() {
    int offset = RbOffset(vSlot);
    vec4 rowHeader = RbF(offset+32);  // count, arc meters, maxHalfWidth, flags
    vec4 material0 = RbF(offset+33);  // water color.rgb, opacity
    vec4 material1 = RbF(offset+34);  // foam color.rgb, foam amount
    vec4 material2 = RbF(offset+35);  // flow speed m/s, normal strength, normal tiling, unused
    vec4 material3 = RbF(offset+36);  // edge foam, edge softness, depth meters, unused
    vec4 material4 = RbF(offset+37);  // sort order, phase meters, 0, 0
    vec4 camera = RbF(4);
    vec4 sun = RbF(6);                // propagation direction.xyz, intensity
    vec4 sunColor = RbF(7);
    vec4 ambient = RbF(8);
    float time = camera.w;

    float sceneDepthValue = texelFetch(sceneDepth,ivec2(gl_FragCoord.xy),0).r;
    float gap = sceneDepthValue-gl_FragCoord.z;
    // Software depth test: this pass has no depth attachment. The band is drawn after the opaque scene, so anything already
    // closer than the water surface (the bank it is tucked under, a rock standing in it) must win.
    if (gap < 0.0) discard;

    // Soft shoreline: the classic soft-particle trick. Dividing by the screen-space depth slope makes the fade one or two
    // pixels wide at every distance, so the waterline dissolves into the bank instead of ending on a stair-stepped edge.
    float depthSlope = max(abs(dFdx(sceneDepthValue))+abs(dFdy(sceneDepthValue)),1e-7);
    float shore = clamp(gap/(depthSlope*2.5),0.0,1.0);
    // A broader ramp from the same quantity, used only as the shallow/deep tint. 24 slopes wide is roughly "half the channel".
    float thickness = clamp(gap/(depthSlope*24.0),0.0,1.0);

    float u = clamp(vUv.x,0.0,1.0);   // across the band [0,1]
    float v = clamp(vUv.y,0.0,1.0);   // along the band [0,1]
    float lateral = u*2.0-1.0;

    uint flags = uint(rowHeader.w);
    bool hasMask = (flags&1u) != 0u;
    bool hasNormal = (flags&2u) != 0u;
    bool hasFlow = (flags&4u) != 0u;

    // Scroll coordinate in meters of world travel: phase + arc, minus time * speed. Because each segment carries the phase it
    // starts at, two neighbours with the same speed read the same world distance for every frame - direction and speed both
    // continuous across the joint, which is the property the river layout depends on.
    float travelled = material4.y+vUv.z-time*max(material2.x,0.0);
    float vTiled = travelled/3.0;

    vec4 mask = vec4(0.55,0.0,0.5,0.5);
    if (hasMask) mask = textureLod(ribbonMaskTiled,vec2(u*2.0,vTiled*0.75),0.0);
    vec4 flowSample = vec4(0.5,0.5,0.5,1.0);
    if (hasFlow) flowSample = textureLod(ribbonFlow,vec2(u*2.0,vTiled*1.3),0.0);

    // Lateral drift so the strands sway as they travel, instead of the whole texture translating like a rigid body.
    float drift = (flowSample.r*2.0-1.0)*0.05;
    float tiling = max(material2.z,0.25);
    vec2 normalA = vec2(0.0,0.0);
    vec2 normalB = vec2(0.0,0.0);
    if (hasNormal) {
        normalA = textureLod(ribbonNormal,vec2((u+drift)*tiling,vTiled),0.0).rg*2.0-1.0;
        // The second layer is overlaid at 1.63x frequency and 0.78x speed: one layer alone reads as a scrolling decal under TAA.
        normalB = textureLod(ribbonNormal,vec2((u-drift)*tiling*1.63+0.37,vTiled*0.78-0.21),0.0).rg*2.0-1.0;
    }
    vec2 slope = (normalA+normalB)*(0.5*material2.y)*vec2(0.5,1.0);

    // Foam has two sources: the periodic bands carried by the flow, and the two bank lines. Both take a max rather than a sum,
    // otherwise their overlap blows straight to white and the cartoon tiers disappear.
    float edge = 1.0-abs(lateral);
    float edgeBand = clamp((edge-(1.0-material3.x))/max(material3.x,1e-3),0.0,1.0);
    float bands = clamp(mask.b*1.35-0.35,0.0,1.0);
    float foam = clamp(max(bands*material1.w,edgeBand*0.80),0.0,1.0);

    // Bank alpha: the geometry reaches exactly the waterline, so the bite is only the A channel's organic wobble plus a
    // narrow taper. A river that ended on a straight line would read as a decal, not as water.
    float cut = edge-material3.y*(0.45+0.65*mask.a);
    float edgeCut = smoothstep(0.0,0.12,cut);
    float foamCut = smoothstep(-0.05,0.05,cut);
    float density = 0.72+0.28*clamp(mask.r*1.6,0.0,1.0);
    float bodyAlpha = material0.w*density*edgeCut;
    float foamAlpha = foam*0.85*foamCut;
    float alpha = clamp(max(bodyAlpha,foamAlpha)*shore,0.0,1.0);

    // Lighting: the surface normal is mostly up, so the sun term is nearly constant across the band and the interest has to
    // come from the Fresnel rim and the foam. The sky stand-in is the frame's ambient color, since this pass cannot sample the sky.
    vec3 view = normalize(camera.xyz-vWorld);
    vec3 normal = normalize(vNormal);
    vec3 tangent = normalize(vTangent-normal*dot(normal,vTangent));
    vec3 bitangent = cross(normal,tangent);
    vec3 shadedNormal = normalize(normal+bitangent*slope.x+tangent*slope.y);

    vec3 toSun = -sun.xyz;
    float ndl = clamp(dot(shadedNormal,toSun),0.0,1.0);
    vec3 ambientLight = ambient.rgb*ambient.w;
    vec3 direct = sunColor.rgb*(sun.w*ndl);

    // Shallow water is pale and slightly desaturated; deeper water keeps the base color. thickness is the screen-space ramp.
    vec3 shallow = mix(material0.rgb,material1.rgb,0.35)*1.35;
    vec3 body = mix(shallow,material0.rgb,thickness)*(ambientLight+direct*0.85);

    float fresnel = pow(1.0-clamp(dot(shadedNormal,view),0.0,1.0),4.0);
    vec3 color = mix(body,ambientLight*1.4+sunColor.rgb*(sun.w*0.15),fresnel*0.45);

    vec3 foamLit = material1.rgb*(ambientLight*1.25+direct*(0.40+0.60*ndl));
    color = mix(color,foamLit,clamp(foam,0.0,1.0));

    vec3 halfVector = normalize(view+toSun);
    color += sunColor.rgb*(sun.w*pow(clamp(dot(shadedNormal,halfVector),0.0,1.0),64.0)*0.45);
    color += (ambientLight+direct)*pow(1.0-clamp(dot(shadedNormal,view),0.0,1.0),4.0)*0.06;

    outColor = vec4(color,alpha);
}
""";
}
