// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// P2 GLSL variant of the water surface material (Vulkan / Linux / Android), semantically identical
/// to the HLSL in <see cref="WaterShaders"/>.
///
/// The differences from the original are purely syntactic; three are kept as required by Vulkan's rules:
/// <list type="bullet">
/// <item>The frame buffer is read as a <c>vec4</c> array (<c>std430</c>), the same memory as the HLSL <c>Load4(i*16)</c>;</item>
/// <item>glslang's simplified C interface only recognizes <c>main</c>, so the vertex and fragment are two
/// separate sources, each concatenating the same <c>CoreGlsl</c>;</item>
/// <item>Depth uses <c>texelFetch</c> rather than <c>texture</c>: the software depth test is a per-pixel
/// comparison, and any filtering would blend in neighboring pixels' depth, producing an erroneous ring of
/// occlusion around the water surface edges.</item>
/// </list>
/// </summary>
public static partial class WaterShaders
{
    /// <summary>
    /// Accessors for the frame buffer and sampling sources, shared by the vertex and fragment stages.
    /// <c>WaterTransform</c> keeps the HLSL row-vector form (<c>p.x*F(i)+...</c>), because
    /// <see cref="WaterMath"/> copies <see cref="System.Numerics.Matrix4x4"/> into the buffer row by row,
    /// and indexing by <c>vec4</c> in GLSL fetches exactly those same rows.
    /// </summary>
    const string CoreGlsl = """
layout(std430,set=0,binding=0) readonly buffer WaterFrame { vec4 waterF[]; };
layout(set=0,binding=1) uniform sampler2D sceneDepth;
layout(set=0,binding=2) uniform sampler2D refractionSource;
layout(set=0,binding=3) uniform sampler2D waterNormalTexture;
layout(set=0,binding=4) uniform sampler2D waterSkyView;
// The four levels of the P3 Hi-Z pyramid (1/2 ~ 1/16); none is sampled when data[12].x is 0.
layout(set=0,binding=5) uniform sampler2D waterHz0;
layout(set=0,binding=6) uniform sampler2D waterHz1;
layout(set=0,binding=7) uniform sampler2D waterHz2;
layout(set=0,binding=8) uniform sampler2D waterHz3;
layout(push_constant) uniform WaterParams { uvec4 waterParams; };

vec4 WaterF(int i) { return waterF[i]; }
vec4 WaterTransform(vec4 p,int i) { return p.x*WaterF(i)+p.y*WaterF(i+1)+p.z*WaterF(i+2)+p.w*WaterF(i+3); }
int WaterOffset(uint index) { return 16+int(index)*16; }
""";

    /// <summary>
    /// P4 vertex stage: tile grid expansion + Gerstner displacement. Statement-for-statement in the
    /// same order as the HLSL version; the only syntax differences are <c>gl_InstanceIndex</c> instead
    /// of <c>SV_InstanceID</c>, and <c>out</c> parameters written in the parameter list.
    /// </summary>
    public const string VertexGlsl = "#version 450\n" + CoreGlsl + """

layout(location=0) out vec3 vWorld;
layout(location=1) flat out uint vSlot;
layout(location=2) out vec3 vNormal;

// Six 2D corner points, arranged as the quad's two triangles. Single exit, same rationale as the HLSL version.
vec2 WaterCorner(uint id) {
    vec2 corner = vec2(0.0,0.0);
    if (id == 1u) corner = vec2(1.0,0.0);
    else if (id == 2u) corner = vec2(1.0,1.0);
    else if (id == 3u) corner = vec2(0.0,0.0);
    else if (id == 4u) corner = vec2(1.0,1.0);
    else if (id == 5u) corner = vec2(0.0,1.0);
    return corner;
}

// [0,1] grid coordinate within the tile -> square [-0.5,0.5] planar coordinate.
// The divisor is clamped to 1: integer division by zero is undefined in both GLSL and WGSL, and the driver cannot prove the root constant is non-zero.
vec2 WaterLocalUv(uint id,uint tile,uint tilesX,uint tilesZ,uint subdiv) {
    if (subdiv == 0u) return WaterCorner(id)-0.5;
    uint nx = max(tilesX,1u);
    uint nz = max(tilesZ,1u);
    uint sd = max(subdiv,1u);
    uint quad = id/6u;
    vec2 corner = WaterCorner(id%6u);
    vec2 quadUv = (vec2(float(quad%sd),float(quad/sd))+corner)/float(sd);
    vec2 tileXy = vec2(float(tile%nx),float(tile/nx));
    return (tileXy+quadUv)/vec2(float(nx),float(nz))-0.5;
}

// Gerstner displacement and tangent vectors, parameters at water + 8 .. + 11 (amplitude, wavenumber, direction.x, direction.z).
// The phase speed is computed on the fly from the deep-water dispersion ω = sqrt(g·k); the frame
// buffer stores only the wavenumber and the speed multiplier at water + 13.x.
// Horizontal displacement contributes k·A·sin (lateral compression), height contributes k·A·cos
// (vertical tilt); mixing them up flips the sign of the normal at crests and troughs.
vec3 WaterGerstner(vec2 p,float t,int offset,out vec3 tangent,out vec3 binormal) {
    vec3 displacement = vec3(0.0);
    tangent = vec3(1.0,0.0,0.0);
    binormal = vec3(0.0,0.0,1.0);
    float choppiness = WaterF(offset+12).w;
    float speedScale = WaterF(offset+13).x;
    for (uint i=0u;i<4u;i++) {
        vec4 wave = WaterF(offset+8+int(i));
        float amplitude = wave.x;
        float k = wave.y;
        // All four are 0 when wind waves are off: skip when k is 0, keeping the vertex and normal at identity values.
        if (k > 0.0) {
            vec2 dir = wave.zw;
            float omega = sqrt(9.81*k)*speedScale;
            float phase = k*dot(dir,p)-omega*t;
            float s = sin(phase);
            float c = cos(phase);
            float q = choppiness*amplitude;
            displacement.x += q*dir.x*c;
            displacement.y += amplitude*s;
            displacement.z += q*dir.y*c;
            float ks = k*amplitude*s;
            float kc = k*amplitude*c;
            tangent += vec3(-choppiness*ks*dir.x*dir.x,kc*dir.x,-choppiness*ks*dir.x*dir.y);
            binormal += vec3(-choppiness*ks*dir.x*dir.y,kc*dir.y,-choppiness*ks*dir.y*dir.y);
        }
    }
    return displacement;
}

// The instance index is the tile index; the slot still comes from the push constant (WebGPU lacks this, so it must squeeze it into the instance index).
void main() {
    uint slot = waterParams.x;
    int offset = WaterOffset(slot);
    vec2 uv = WaterLocalUv(uint(gl_VertexIndex),uint(gl_InstanceIndex),
        waterParams.y,waterParams.z,waterParams.w);
    vec4 world = WaterTransform(vec4(uv.x,0.0,uv.y,1.0),offset);

    // Displacement is applied after model->world: Gerstner is a traveling wave on the world XZ plane,
    // so putting it in model space would make it rotate with the rectangle.
    vec3 tangent, binormal;
    world.xyz += WaterGerstner(world.xz,WaterF(10).z,offset,tangent,binormal);

    gl_Position = WaterTransform(world,4);
    vWorld = world.xyz;
    // cross(binormal,tangent): (0,0,1)×(1,0,0) = (0,1,0) when undisturbed, pointing straight up.
    vNormal = normalize(cross(binormal,tangent));
    vSlot = slot;
}
""";

    /// <summary>
    /// Normal perturbation, refraction, absorption, Fresnel and rain ripple evaluation. Statement-for-statement
    /// in the same order as the HLSL version; the only syntax differences are <c>atan2(y,x)</c> written as
    /// <c>atan(y,x)</c> in GLSL, and <c>frac</c> corresponding to <c>fract</c>.
    /// </summary>
    public const string PixelGlsl = "#version 450\n" + CoreGlsl + """

layout(location=0) in vec3 vWorld;
layout(location=1) flat in uint vSlot;
layout(location=2) in vec3 vNormal;
layout(location=0) out vec4 outColor;

vec3 WaterSampleSky(vec3 direction) {
    vec2 uv = vec2(atan(direction.x,-direction.z)*0.15915494309+0.5,
        0.5-0.5*sign(direction.y)*sqrt(abs(direction.y)));
    return textureLod(waterSkyView,uv,0.0).rgb;
}

// Height field gradient (m/m), coefficient = wavenumber × amplitude, sums to under 0.07, so the normal only tilts 3~4°.
// The units must not be dropped: returning d·cos directly (magnitude ~1) would tilt the normal past 15°, and
// when looking down, half the pixels' normals face away from the view, so facing would be clamped to 0 and
// Fresnel capped at 1, degenerating the water surface into hard-edged specular patches.
vec2 WaterWave(vec2 p,float t,float speed) {
    vec2 d1 = vec2(0.86,0.51);
    vec2 d2 = vec2(-0.42,0.91);
    float k1 = dot(p,d1)*2.4+t*speed*6.2831853;
    float k2 = dot(p,d2)*3.7-t*speed*5.1;
    return d1*cos(k1)*(2.4*0.016)+d2*cos(k2)*(3.7*0.009);
}

// The rain ripples are likewise gradient units: ring frequency 26/m, amplitude 0.0025 m, single-ring gradient about 0.065.
vec2 WaterRipple(vec2 p,float t) {
    vec2 acc = vec2(0.0);
    for (uint i=0u;i<3u;i++) {
        vec2 c = vec2(sin(float(i)*12.9898)*7.0,cos(float(i)*78.233)*7.0);
        vec2 d = p-c;
        float r = length(d);
        float phase = fract(t*1.7+float(i)*0.37);
        float wave = sin(r*26.0-phase*40.0)*clamp(1.0-phase,0.0,1.0);
        acc += d/max(r,1e-4)*wave;
    }
    return acc*(26.0*0.0025);
}

// View-space forward depth (LH + [0,1]), same reconstruction formula as the main shader / GTAO / SsrEffect.
float WaterLinear(float d) {
    float nearV = WaterF(12).z, farV = WaterF(12).w;
    return nearV*farV/(farV-d*(farV-nearV));
}

float WaterHiZ(vec2 uv,int level) {
    if (level <= 0) return textureLod(waterHz0,uv,0.0).r;
    if (level == 1) return textureLod(waterHz1,uv,0.0).r;
    if (level == 2) return textureLod(waterHz2,uv,0.0).r;
    return textureLod(waterHz3,uv,0.0).r;
}

// Screen-space reflection: march through screen space along the reflection direction, using the
// Hi-Z pyramid to skip empty regions in whole blocks; on a hit, sample the refraction snapshot
// (the opaque scene color from before this pass). rgb = radiance, a = confidence (0 = no hit).
vec4 WaterTraceReflection(vec3 origin,vec3 direction,vec2 size) {
    const int coarseSteps = 24;
    const int refineSteps = 6;
    float maxDistance = WaterF(13).x;
    float thickness = WaterF(13).y;
    vec4 result = vec4(0.0);
    vec4 clip0 = WaterTransform(vec4(origin,1.0),4);
    vec4 clip1 = WaterTransform(vec4(origin+direction*maxDistance,1.0),4);

    // When the ray goes behind the camera there is no screen-space information available: shrink along the original direction to just before the camera near plane.
    float endT = 1.0;
    if (clip1.w <= 0.001) {
        float denom = clip1.w-clip0.w;
        endT = denom < -0.0001 ? clamp(-clip0.w/denom,0.0,1.0) : 0.0;
        clip1 = WaterTransform(vec4(origin+direction*(maxDistance*endT),1.0),4);
    }

    if (clip0.w > 0.001 && clip1.w > 0.001 && endT > 0.001) {
        vec2 uv0 = clip0.xy/clip0.w*vec2(0.5,-0.5)+0.5;
        vec2 uv1 = clip1.xy/clip1.w*vec2(0.5,-0.5)+0.5;
        float z0 = clip0.z/clip0.w;
        float z1 = clip1.z/clip1.w;
        vec2 delta = uv1-uv0;

        // How many pixels each step spans decides which pyramid level to query: short rays use fine levels, otherwise the coarse blocks would never hit.
        float pixels = length(delta*size)/float(coarseSteps);
        int level = 3;
        if (pixels < 12.0) level = 2;
        if (pixels < 6.0) level = 1;
        if (pixels < 3.0) level = 0;

        vec2 coarse = uv0;
        float coarseZ = z0;
        for (int i=1;i<=coarseSteps;i++) {
            float t = float(i)/float(coarseSteps);
            vec2 next = uv0+delta*t;
            float nextZ = mix(z0,z1,t);
            // Terminate as soon as it goes off-screen: out-of-screen information does not exist, and continuing only samples the clamped edge pixels.
            if (next.x < 0.0 || next.x > 1.0 || next.y < 0.0 || next.y > 1.0) break;
            if (nextZ < WaterHiZ(next,level)) {
                coarse = next;
                coarseZ = nextZ;
                continue;
            }
            // Overshot: converge point by point toward the true intersection within (previous node, current node).
            for (int j=1;j<=refineSteps;j++) {
                float tj = float(j)/float(refineSteps);
                vec2 suv = mix(coarse,next,tj);
                float rayZ = mix(coarseZ,nextZ,tj);
                ivec2 coord = clamp(ivec2(suv*size),ivec2(0),ivec2(int(uint(size.x))-1,int(uint(size.y))-1));
                float sceneD = texelFetch(sceneDepth,coord,0).r;
                if (sceneD >= 1.0) continue;
                // The thickness test is done in linear depth: NDC depth gets more compressed the farther away, so the same threshold differs by orders of magnitude near and far.
                float rayView = WaterLinear(rayZ);
                float sceneView = WaterLinear(sceneD);
                if (rayView >= sceneView && rayView-sceneView <= thickness) {
                    vec2 hitUv = clamp(suv,vec2(0.0),vec2(1.0));
                    // Fade at the screen edge: the sampling there is extrapolated, and a hard cut would pull a bright edge along the frame border.
                    float edge = min(min(hitUv.x,1.0-hitUv.x),min(hitUv.y,1.0-hitUv.y));
                    float fade = clamp(edge/0.06,0.0,1.0)*clamp(1.0-t,0.0,1.0);
                    result = vec4(textureLod(refractionSource,hitUv,0.0).rgb,fade);
                    break;
                }
            }
            break;
        }
    }
    return result;
}

void main() {
    int offset = WaterOffset(vSlot);
    vec4 material0 = WaterF(offset+4);
    vec4 material1 = WaterF(offset+5);
    vec4 material2 = WaterF(offset+6);
    vec4 material3 = WaterF(offset+7);

    vec3 camera = WaterF(8).xyz;
    vec4 screen = WaterF(9);
    vec4 misc = WaterF(10);
    vec4 sun = WaterF(11);
    float time = misc.z;

    vec2 uv = gl_FragCoord.xy*screen.zw;
    ivec2 pixel = ivec2(gl_FragCoord.xy);
    float sceneDepthValue = texelFetch(sceneDepth,pixel,0).r;

    if (gl_FragCoord.z > sceneDepthValue+1e-6) discard;

    vec4 ndc = vec4(uv*vec2(2,-2)+vec2(-1,1),sceneDepthValue,1.0);
    vec4 scenePosition = ndc.x*WaterF(0)+ndc.y*WaterF(1)+ndc.z*WaterF(2)+ndc.w*WaterF(3);
    float thickness = max(length(scenePosition.xyz/scenePosition.w-vWorld),0.0);

    vec2 planar = vWorld.xz;
    vec2 slope = WaterWave(planar,time,material2.x);
    if (material3.x > 0.5) {
        vec2 tiled = planar*material1.w;
        vec2 a = textureLod(waterNormalTexture,tiled+time*material2.x*vec2(0.7,0.3),0.0).rg*2.0-1.0;
        vec2 b = textureLod(waterNormalTexture,tiled*1.37-time*material2.x*vec2(0.4,0.8),0.0).rg*2.0-1.0;
        slope = (a+b)*0.5;
    }
    if (misc.y > 0.5) slope += WaterRipple(planar*2.0,time)*material2.y;
    slope *= material1.z;
    // P4: the detail slope is added onto Gerstner's analytic normal. When Gerstner is off,
    // vNormal is constant (0,1,0), and this degenerates into P3's normalize(vec3(slope.x,1,slope.y)).
    vec3 normal = normalize(vNormal+vec3(slope.x,0.0,slope.y));

    vec3 view = normalize(camera-vWorld);
    float facing = clamp(dot(normal,view),0.0,1.0);
    float fresnel = material2.z+(1.0-material2.z)*pow(1.0-facing,5.0);

    // The refraction offset is in pixels (multiplied by 1/width and 1/height below to convert to UV); 600 folds the gradient into pixel scale.
    vec2 offsetUv = slope*material1.y*600.0;
    vec2 refractedUv = clamp(uv+offsetUv*screen.zw,vec2(0.0),vec2(1.0));
    vec3 refracted = textureLod(refractionSource,refractedUv,0.0).rgb;

    vec3 transmittance = exp(-material1.x*thickness*vec3(1.35,0.85,0.55));
    // The water body is a lit scattering medium: the water color is albedo and must be multiplied by
    // lighting, otherwise the pool would glow on its own at night after the scene goes dark.
    // Use the luminance of the pool bottom snapshot as a proxy for daylight: approaches 0 at night, saturates to 1 by day.
    float daylight = clamp(dot(refracted,vec3(0.2126,0.7152,0.0722))*3.0,0.0,1.0);
    vec3 body = refracted*transmittance+material0.rgb*(1.0-transmittance)*daylight;

    // Reflection: try screen-space reflection first (Hi-Z accelerated), fall back to the procedural sky on a miss.
    // The ray uses the unclamped direction: when looking down at the water, the reflection direction points toward
    // the bottom, where the sky LUT has no information.
    vec3 reflectDirection = reflect(-view,normal);
    // Before sampling the sky, clamp below the horizon back to the horizon: the lower hemisphere of the
    // LUT is near-ground haze (almost fully black at night), and a slight normal tilt would sample into it.
    vec3 skyDirection = vec3(reflectDirection.x,max(reflectDirection.y,0.0),reflectDirection.z);
    // The mix-in base color is also multiplied by daylight: when the sky LUT is unavailable, this constant should not keep glowing at night.
    vec3 reflection = misc.w > 0.5 ? WaterSampleSky(skyDirection) : vec3(0.35,0.45,0.62)*daylight;
    if (WaterF(12).x > 0.5) {
        vec4 traced = WaterTraceReflection(vWorld,reflectDirection,screen.xy);
        reflection = mix(reflection,traced.rgb,clamp(traced.a*WaterF(12).y,0.0,1.0));
    }
    vec3 toSun = normalize(-sun.xyz);
    vec3 halfVector = normalize(view+toSun);
    float shininess = 2.0/max(material2.w*material2.w,1e-3);
    reflection += sun.w*pow(clamp(dot(normal,halfVector),0.0,1.0),shininess)*vec3(1.0,0.95,0.85)*0.5;

    vec3 color = mix(body,reflection,fresnel);
    float thicknessAlpha = 1.0-exp(-thickness*0.9);
    float alpha = clamp(max(mix(material0.a,1.0,fresnel),thicknessAlpha*0.9),0.0,1.0);
    outColor = vec4(color,alpha);
}
""";
}
