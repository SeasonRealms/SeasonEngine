// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// P2 MSL variant of the water surface material. All four backends share the same semantics
/// and frame buffer layout (see <see cref="WaterMath"/>); this file only handles "read the layout
/// + evaluate", it does not define the layout.
///
/// There are only two differences from the other three backends, both specific to Metal's shape:
/// <list type="bullet">
/// <item>The frame buffer is passed directly as <c>device const float4*</c>, with no bit
/// reinterpretation like ByteAddressBuffer requires;</item>
/// <item>The sampler is written in the source (<c>constexpr sampler</c>) and does not occupy a
/// binding slot -- the same approach as decals and the main pipeline for the shadow map.</item>
/// </list>
///
/// Depth must be <c>depth2d&lt;float&gt;</c> to be <c>read</c>: the software depth test wants point
/// sampling, and depth2d does not support filtered sampling anyway, which matches the requirement exactly.
/// </summary>
public static partial class WaterShaders
{
    /// <summary>Accessors for the frame header and the water array, shared by both entry points.</summary>
    const string CoreMsl = """
#include <metal_stdlib>
using namespace metal;

struct WaterVertex {
    float4 position [[position]];
    float3 world;
    // P4: analytic normal after vertex displacement (world space). Constant (0,1,0) when Gerstner is off.
    float3 normal;
    // Integer interpolation does not go through linear filtering; make flat explicit rather than rely on the compiler default.
    uint slot [[flat]];
};

float4 WaterF(const device float4* data, int i) { return data[i]; }
int WaterOffset(uint index) { return 16 + int(index) * 16; }
float4 WaterTransform(const device float4* data, float4 p, int i) {
    return p.x * data[i] + p.y * data[i+1] + p.z * data[i+2] + p.w * data[i+3];
}
""";

    /// <summary>
    /// P4 vertex stage: tile grid expansion + Gerstner displacement. Statement-for-statement in the
    /// same order as the HLSL version; the only syntax differences are <c>[[instance_id]]</c> instead
    /// of <c>SV_InstanceID</c>, and <c>thread</c> references acting as out parameters.
    /// </summary>
    const string VertexMsl = """

// Six 2D corner points, arranged as the quad's two triangles. Single exit, same rationale as the HLSL version.
float2 WaterCorner(uint id) {
    float2 corner = float2(0.0f, 0.0f);
    if (id == 1u) corner = float2(1.0f, 0.0f);
    else if (id == 2u) corner = float2(1.0f, 1.0f);
    else if (id == 3u) corner = float2(0.0f, 0.0f);
    else if (id == 4u) corner = float2(1.0f, 1.0f);
    else if (id == 5u) corner = float2(0.0f, 1.0f);
    return corner;
}

// [0,1] grid coordinate within the tile -> square [-0.5,0.5] planar coordinate. The divisor is clamped to 1, same rationale as the other two backends.
float2 WaterLocalUv(uint id, uint tile, uint tilesX, uint tilesZ, uint subdiv) {
    if (subdiv == 0u) return WaterCorner(id) - 0.5f;
    uint nx = max(tilesX, 1u);
    uint nz = max(tilesZ, 1u);
    uint sd = max(subdiv, 1u);
    uint quad = id / 6u;
    float2 corner = WaterCorner(id % 6u);
    float2 quadUv = (float2(float(quad % sd), float(quad / sd)) + corner) / float(sd);
    float2 tileXy = float2(float(tile % nx), float(tile / nx));
    return (tileXy + quadUv) / float2(float(nx), float(nz)) - 0.5f;
}

// Gerstner displacement and tangent vectors, parameters at water + 8 .. + 11 (amplitude, wavenumber, direction.x, direction.z).
// The phase speed is computed on the fly from the deep-water dispersion ω = sqrt(g·k); the frame
// buffer stores only the wavenumber and the speed multiplier at water + 13.x.
// Horizontal displacement contributes k·A·sin (lateral compression), height contributes k·A·cos
// (vertical tilt); mixing them up flips the sign of the normal at crests and troughs.
float3 WaterGerstner(const device float4* data, float2 p, float t, int offset,
    thread float3& tangent, thread float3& binormal) {
    float3 displacement = float3(0.0f, 0.0f, 0.0f);
    tangent = float3(1.0f, 0.0f, 0.0f);
    binormal = float3(0.0f, 0.0f, 1.0f);
    float choppiness = data[offset + 12].w;
    float speedScale = data[offset + 13].x;
    for (uint i = 0u; i < 4u; i++) {
        float4 wave = data[offset + 8 + int(i)];
        float amplitude = wave.x;
        float k = wave.y;
        // All four are 0 when wind waves are off: skip when k is 0, keeping the vertex and normal at identity values.
        if (k > 0.0f) {
            float2 dir = wave.zw;
            float omega = sqrt(9.81f * k) * speedScale;
            float phase = k * dot(dir, p) - omega * t;
            float s = sin(phase);
            float c = cos(phase);
            float q = choppiness * amplitude;
            displacement.x += q * dir.x * c;
            displacement.y += amplitude * s;
            displacement.z += q * dir.y * c;
            float ks = k * amplitude * s;
            float kc = k * amplitude * c;
            tangent += float3(-choppiness * ks * dir.x * dir.x, kc * dir.x, -choppiness * ks * dir.x * dir.y);
            binormal += float3(-choppiness * ks * dir.x * dir.y, kc * dir.y, -choppiness * ks * dir.y * dir.y);
        }
    }
    return displacement;
}

// The instance index is the tile index; the slot still comes from the buffer(1) constant (WebGPU lacks this, so it must squeeze it into the instance index).
vertex WaterVertex VSMain(uint id [[vertex_id]], uint tile [[instance_id]], const device float4* data [[buffer(0)]],
    constant uint4& params [[buffer(1)]]) {
    WaterVertex o;
    uint slot = params.x;
    int offset = WaterOffset(slot);
    float2 uv = WaterLocalUv(id, tile, params.y, params.z, params.w);
    float4 world = WaterTransform(data, float4(uv.x, 0.0f, uv.y, 1.0f), offset);

    // Displacement is applied after model->world: Gerstner is a traveling wave on the world XZ plane,
    // so putting it in model space would make it rotate with the rectangle.
    float3 tangent, binormal;
    world.xyz += WaterGerstner(data, world.xz, data[10].z, offset, tangent, binormal);

    o.position = WaterTransform(data, world, 4);
    o.world = world.xyz;
    // cross(binormal,tangent): (0,0,1)×(1,0,0) = (0,1,0) when undisturbed, pointing straight up.
    o.normal = normalize(cross(binormal, tangent));
    o.slot = slot;
    return o;
}
""";

    /// <summary>
    /// Normal perturbation, refraction, absorption, Fresnel and rain ripple evaluation. Statement-for-statement
    /// in the same order as the HLSL version; all math function names match across the two backends except
    /// <c>fmod</c>, and <c>frac</c> corresponds to <c>fract</c>.
    /// </summary>
    const string PixelMsl = """

float3 WaterSampleSky(texture2d<float> skyView, float3 direction) {
    constexpr sampler linearClamp(coord::normalized, address::clamp_to_edge, filter::linear);
    float2 uv = float2(atan2(direction.x, -direction.z) * 0.15915494309f + 0.5f,
        0.5f - 0.5f * sign(direction.y) * sqrt(abs(direction.y)));
    return skyView.sample(linearClamp, uv, level(0.0)).rgb;
}

// Height field gradient (m/m), coefficient = wavenumber × amplitude, sums to under 0.07, so the normal only tilts 3~4°.
// The units must not be dropped: returning d·cos directly (magnitude ~1) would tilt the normal past 15°, and
// when looking down, half the pixels' normals face away from the view, so facing would be saturated to 0 and
// Fresnel capped at 1, degenerating the water surface into hard-edged specular patches.
float2 WaterWave(float2 p, float t, float speed) {
    float2 d1 = float2(0.86f, 0.51f);
    float2 d2 = float2(-0.42f, 0.91f);
    float k1 = dot(p, d1) * 2.4f + t * speed * 6.2831853f;
    float k2 = dot(p, d2) * 3.7f - t * speed * 5.1f;
    return d1 * cos(k1) * (2.4f * 0.016f) + d2 * cos(k2) * (3.7f * 0.009f);
}

// The rain ripples are likewise gradient units: ring frequency 26/m, amplitude 0.0025 m, single-ring gradient about 0.065.
float2 WaterRipple(float2 p, float t) {
    float2 acc = float2(0.0f);
    for (uint i = 0u; i < 3u; i++) {
        float2 c = float2(sin(float(i) * 12.9898f) * 7.0f, cos(float(i) * 78.233f) * 7.0f);
        float2 d = p - c;
        float r = length(d);
        float phase = fract(t * 1.7f + float(i) * 0.37f);
        float wave = sin(r * 26.0f - phase * 40.0f) * saturate(1.0f - phase);
        acc += d / max(r, 1e-4f) * wave;
    }
    return acc * (26.0f * 0.0025f);
}

// View-space forward depth (LH + [0,1]), same reconstruction formula as the main shader / GTAO / SsrEffect.
float WaterLinear(const device float4* data, float d) {
    float nearV = data[12].z, farV = data[12].w;
    return nearV * farV / (farV - d * (farV - nearV));
}

float WaterHiZ(texture2d<float> hz0, texture2d<float> hz1, texture2d<float> hz2, texture2d<float> hz3,
    sampler sm, float2 uv, int level) {
    if (level <= 0) return hz0.sample(sm, uv, level(0.0)).r;
    if (level == 1) return hz1.sample(sm, uv, level(0.0)).r;
    if (level == 2) return hz2.sample(sm, uv, level(0.0)).r;
    return hz3.sample(sm, uv, level(0.0)).r;
}

// Screen-space reflection: march through screen space along the reflection direction, using the
// Hi-Z pyramid to skip empty regions in whole blocks; on a hit, sample the refraction snapshot
// (the opaque scene color from before this pass). rgb = radiance, a = confidence (0 = no hit).
// MSL has no shader globals, so all seven textures and the sampler are explicit parameters.
float4 WaterTraceReflection(const device float4* data, depth2d<float> depthTexture,
    texture2d<float> refraction, texture2d<float> hz0, texture2d<float> hz1,
    texture2d<float> hz2, texture2d<float> hz3, sampler sm,
    float3 origin, float3 direction, float2 size) {
    const int coarseSteps = 24;
    const int refineSteps = 6;
    float maxDistance = data[13].x;
    float thickness = data[13].y;
    float4 result = float4(0.0f);
    float4 clip0 = WaterTransform(data, float4(origin, 1.0f), 4);
    float4 clip1 = WaterTransform(data, float4(origin + direction * maxDistance, 1.0f), 4);

    // When the ray goes behind the camera there is no screen-space information available: shrink along the original direction to just before the camera near plane.
    float endT = 1.0f;
    if (clip1.w <= 0.001f) {
        float denom = clip1.w - clip0.w;
        endT = denom < -0.0001f ? saturate(-clip0.w / denom) : 0.0f;
        clip1 = WaterTransform(data, float4(origin + direction * (maxDistance * endT), 1.0f), 4);
    }

    if (clip0.w > 0.001f && clip1.w > 0.001f && endT > 0.001f) {
        float2 uv0 = clip0.xy / clip0.w * float2(0.5f, -0.5f) + 0.5f;
        float2 uv1 = clip1.xy / clip1.w * float2(0.5f, -0.5f) + 0.5f;
        float z0 = clip0.z / clip0.w;
        float z1 = clip1.z / clip1.w;
        float2 delta = uv1 - uv0;

        // How many pixels each step spans decides which pyramid level to query: short rays use fine levels, otherwise the coarse blocks would never hit.
        float pixels = length(delta * size) / float(coarseSteps);
        int level = 3;
        if (pixels < 12.0f) level = 2;
        if (pixels < 6.0f) level = 1;
        if (pixels < 3.0f) level = 0;

        float2 coarse = uv0;
        float coarseZ = z0;
        for (int i = 1; i <= coarseSteps; i++) {
            float t = float(i) / float(coarseSteps);
            float2 next = uv0 + delta * t;
            float nextZ = mix(z0, z1, t);
            // Terminate as soon as it goes off-screen: out-of-screen information does not exist, and continuing only samples the clamped edge pixels.
            if (next.x < 0.0f || next.x > 1.0f || next.y < 0.0f || next.y > 1.0f) break;
            if (nextZ < WaterHiZ(hz0, hz1, hz2, hz3, sm, next, level)) {
                coarse = next;
                coarseZ = nextZ;
                continue;
            }
            // Overshot: converge point by point toward the true intersection within (previous node, current node).
            for (int j = 1; j <= refineSteps; j++) {
                float tj = float(j) / float(refineSteps);
                float2 suv = mix(coarse, next, tj);
                float rayZ = mix(coarseZ, nextZ, tj);
                int2 coord = clamp(int2(suv * size), int2(0, 0), int2(int(size.x) - 1, int(size.y) - 1));
                float sceneD = depthTexture.read(uint2(coord));
                if (sceneD >= 1.0f) continue;
                // The thickness test is done in linear depth: NDC depth gets more compressed the farther away, so the same threshold differs by orders of magnitude near and far.
                float rayView = WaterLinear(data, rayZ);
                float sceneView = WaterLinear(data, sceneD);
                if (rayView >= sceneView && rayView - sceneView <= thickness) {
                    float2 hitUv = saturate(suv);
                    // Fade at the screen edge: the sampling there is extrapolated, and a hard cut would pull a bright edge along the frame border.
                    float edge = min(min(hitUv.x, 1.0f - hitUv.x), min(hitUv.y, 1.0f - hitUv.y));
                    float fade = saturate(edge / 0.06f) * saturate(1.0f - t);
                    result = float4(refraction.sample(sm, hitUv, level(0.0)).rgb, fade);
                    break;
                }
            }
            break;
        }
    }
    return result;
}

fragment float4 PSMain(WaterVertex in [[stage_in]], const device float4* data [[buffer(0)]],
    constant uint4& params [[buffer(1)]], depth2d<float> depthTexture [[texture(0)]],
    texture2d<float> refractionSource [[texture(1)]], texture2d<float> normalTexture [[texture(2)]],
    texture2d<float> skyView [[texture(3)]], texture2d<float> hz0 [[texture(4)]],
    texture2d<float> hz1 [[texture(5)]], texture2d<float> hz2 [[texture(6)]],
    texture2d<float> hz3 [[texture(7)]]) {
    constexpr sampler linearClamp(coord::normalized, address::clamp_to_edge, filter::linear);
    int offset = WaterOffset(in.slot);
    float4 material0 = WaterF(data, offset + 4);
    float4 material1 = WaterF(data, offset + 5);
    float4 material2 = WaterF(data, offset + 6);
    float4 material3 = WaterF(data, offset + 7);

    float3 camera = data[8].xyz;
    float4 screen = data[9];
    float4 misc = data[10];
    float4 sun = data[11];
    float time = misc.z;

    float2 uv = in.position.xy * screen.zw;
    uint2 pixel = uint2(in.position.xy);
    float sceneDepthValue = depthTexture.read(pixel);

    if (in.position.z > sceneDepthValue + 1e-6f) discard_fragment();

    float4 ndc = float4(uv * float2(2.0f, -2.0f) + float2(-1.0f, 1.0f), sceneDepthValue, 1.0f);
    float4 scenePosition = ndc.x * data[0] + ndc.y * data[1] + ndc.z * data[2] + ndc.w * data[3];
    float thickness = max(length(scenePosition.xyz / scenePosition.w - in.world), 0.0f);

    float2 planar = in.world.xz;
    float2 slope = WaterWave(planar, time, material2.x);
    if (material3.x > 0.5f) {
        float2 tiled = planar * material1.w;
        float2 a = normalTexture.sample(linearClamp, tiled + time * material2.x * float2(0.7f, 0.3f), level(0.0)).rg * 2.0f - 1.0f;
        float2 b = normalTexture.sample(linearClamp, tiled * 1.37f - time * material2.x * float2(0.4f, 0.8f), level(0.0)).rg * 2.0f - 1.0f;
        slope = (a + b) * 0.5f;
    }
    if (misc.y > 0.5f) slope += WaterRipple(planar * 2.0f, time) * material2.y;
    slope *= material1.z;
    // P4: the detail slope is added onto Gerstner's analytic normal. When Gerstner is off,
    // in.normal is constant (0,1,0), and this degenerates into P3's normalize(float3(slope.x,1,slope.y)).
    float3 normal = normalize(in.normal + float3(slope.x, 0.0f, slope.y));

    float3 view = normalize(camera - in.world);
    float facing = saturate(dot(normal, view));
    float fresnel = material2.z + (1.0f - material2.z) * pow(1.0f - facing, 5.0f);

    // The refraction offset is in pixels (multiplied by 1/width and 1/height below to convert to UV); 600 folds the gradient into pixel scale.
    float2 offsetUv = slope * material1.y * 600.0f;
    float2 refractedUv = clamp(uv + offsetUv * screen.zw, float2(0.0f), float2(1.0f));
    float3 refracted = refractionSource.sample(linearClamp, refractedUv, level(0.0)).rgb;

    float3 transmittance = exp(-material1.x * thickness * float3(1.35f, 0.85f, 0.55f));
    // The water body is a lit scattering medium: the water color is albedo and must be multiplied by
    // lighting, otherwise the pool would glow on its own at night after the scene goes dark.
    // Use the luminance of the pool bottom snapshot as a proxy for daylight: approaches 0 at night, saturates to 1 by day.
    float daylight = saturate(dot(refracted, float3(0.2126f, 0.7152f, 0.0722f)) * 3.0f);
    float3 body = refracted * transmittance + material0.rgb * (1.0f - transmittance) * daylight;

    // Reflection: try screen-space reflection first (Hi-Z accelerated), fall back to the procedural sky on a miss.
    // The ray uses the unclamped direction: when looking down at the water, the reflection direction points toward
    // the bottom, where the sky LUT has no information.
    float3 reflectDirection = reflect(-view, normal);
    // Before sampling the sky, clamp below the horizon back to the horizon: the lower hemisphere of the
    // LUT is near-ground haze (almost fully black at night), and a slight normal tilt would sample into it.
    float3 skyDirection = float3(reflectDirection.x, max(reflectDirection.y, 0.0f), reflectDirection.z);
    // The mix-in base color is also multiplied by daylight: when the sky LUT is unavailable, this constant should not keep glowing at night.
    float3 reflection = misc.w > 0.5f ? WaterSampleSky(skyView, skyDirection) : float3(0.35f, 0.45f, 0.62f) * daylight;
    if (data[12].x > 0.5f) {
        float4 traced = WaterTraceReflection(data, depthTexture, refractionSource, hz0, hz1, hz2, hz3,
            linearClamp, in.world, reflectDirection, screen.xy);
        reflection = mix(reflection, traced.rgb, saturate(traced.a * data[12].y));
    }
    float3 toSun = normalize(-sun.xyz);
    float3 halfVector = normalize(view + toSun);
    float shininess = 2.0f / max(material2.w * material2.w, 1e-3f);
    reflection += sun.w * pow(saturate(dot(normal, halfVector)), shininess) * float3(1.0f, 0.95f, 0.85f) * 0.5f;

    float3 color = mix(body, reflection, fresnel);
    float thicknessAlpha = 1.0f - exp(-thickness * 0.9f);
    float alpha = saturate(max(mix(material0.a, 1.0f, fresnel), thicknessAlpha * 0.9f));
    return float4(color, alpha);
}
""";

    /// <summary>The vertex and fragment entry points are combined into a single source: the backend caches by source string, so the whole pipeline compiles only once.</summary>
    public const string Msl = CoreMsl + VertexMsl + PixelMsl;
}
