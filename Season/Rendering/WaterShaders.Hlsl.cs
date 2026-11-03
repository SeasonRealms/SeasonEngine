// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// HLSL variant of the P2 water material. The other three backends (GLSL / MSL / WGSL) each
/// implement the same semantics in backend order; the frame buffer layout is defined by
/// <see cref="WaterMath"/> -- here we only "read the layout + evaluate", never define it.
///
/// Two structural differences from the D1 decal determine the shape of this source:
/// <list type="bullet">
/// <item>The decal is a screen-space volume (a 36-vertex projection box), while water is real
/// geometry (a 6-vertex horizontal rectangle), so the vertex stage must actually compute a set of
/// clip-space coordinates rather than just a proxy triangle;</item>
/// <item>The decal only reads depth and writes color back through fixed-function blending; water
/// must also <b>read</b> the scene color for refraction, so the input has an extra
/// <c>RefractionSource</c> (a snapshot of the scene color before drawing), and this pass's color
/// attachment is a different one.</item>
/// </list>
///
/// Occlusion does not rely on a depth attachment (this pass has none), but on a software depth test
/// inside the shader: when a water fragment's clip-space z is farther than the scene depth it is
/// directly <c>discard</c>ed. This way the depth target can stay in a "read-only sampling" state
/// for the whole frame; half-tile water surfaces hidden by the pool rim, rocks, or the player
/// disappear automatically, and no separate DSV is needed for the water.
/// </summary>
public static partial class WaterShaders
{
    /// <summary>
    /// Accessors for the frame header and the water array, shared by the vertex and pixel shaders.
    /// Concatenating them separately would let the two entry points disagree on the same layout,
    /// consistent with the handling in <see cref="DecalShaders.FrameHlsl"/>.
    /// </summary>
    public const string FrameHlsl = """
ByteAddressBuffer waterFrame : register(t0);
Texture2D<float> sceneDepth : register(t1);
Texture2D<float4> refractionSource : register(t2);
Texture2D<float4> waterNormalTexture : register(t3);
Texture2D<float4> waterSkyView : register(t4);
// The four levels of the P3 Hi-Z pyramid (1/2 ~ 1/16). The names are decoupled from the
// <see cref="WaterEffect"/> switches: when disabled data[12].x is 0, and none of these four
// textures is ever sampled.
Texture2D<float> waterHz0 : register(t5);
Texture2D<float> waterHz1 : register(t6);
Texture2D<float> waterHz2 : register(t7);
Texture2D<float> waterHz3 : register(t8);
SamplerState waterLinearClamp : register(s0);
cbuffer WaterParams : register(b0) { uint4 waterParams; };

float4 WaterF(uint i) { return asfloat(waterFrame.Load4(i * 16)); }
uint WaterOffset(uint index) { return 16u + index * 16u; }

// Verbatim the same shape as WaterTransform in GLSL/MSL/WGSL: p is treated as a row vector and
// dotted per row. This must not be written as mul(M, p): HLSL's mul is "matrix times column
// vector", whereas WaterMath copies the matrix into the buffer in System.Numerics row order.
// Combined, the entire translation row gets dropped, the vertex first falls back near the origin
// and is then placed into clip space by the same wrong multiply -- visually the water spreads out
// into a large triangle extending beyond the pool.
float4 WaterTransform(float4 p, uint offset)
{
    return p.x * WaterF(offset) + p.y * WaterF(offset + 1) + p.z * WaterF(offset + 2) + p.w * WaterF(offset + 3);
}

struct WaterVertex
{
    float4 position : SV_Position;
    float3 world : TEXCOORD0;
    // P4: analytic normal after vertex displacement (world space). When Gerstner is off it is
    // constantly (0,1,0); once the pixel stage combines it with the detail slope it matches the
    // flat P3 water surface per pixel.
    float3 normal : TEXCOORD1;
    nointerpolation uint slot : TEXCOORD2;
};
""";

    /// <summary>
    /// P4 vertex stage: tile grid expansion + Gerstner displacement.
    ///
    /// Both geometries share a single path. When not tiled (<c>subdiv == 0</c>) the vertex count is
    /// 6 and the instance count is 1, expanding to the P3 two-triangle rectangle; when tiled the
    /// vertex count is <c>subdiv²×6</c> and the instance count is the total number of tiles, with
    /// each instance drawing one tile containing subdiv×subdiv quads. The world matrix comes from
    /// the shared layer, and neither path needs a vertex buffer.
    /// </summary>
    public const string VertexHlsl = """
// Six 2D corners, ordered as the quad's two triangles. When not tiled they serve directly as
// unit-plane positions; when tiled they are the relative corners of the (qx,qz) quad.
// Single exit: fxc's flow analysis conservatively flags "functions with multiple returns +
// unknown parameter ranges" as uninitialized (X4000); when called directly it is hidden by
// inlining, but once wrapped by another function layer it surfaces.
float2 WaterCorner(uint id)
{
    float2 corner = float2(0.0f, 0.0f);
    if (id == 1u) corner = float2(1.0f, 0.0f);
    else if (id == 2u) corner = float2(1.0f, 1.0f);
    else if (id == 3u) corner = float2(0.0f, 0.0f);
    else if (id == 4u) corner = float2(1.0f, 1.0f);
    else if (id == 5u) corner = float2(0.0f, 1.0f);
    return corner;
}

// [0,1] grid coordinates within the tile -> square [-0.5,0.5] plane coordinates.
// tilesX/tilesZ/subdiv are bit-identical to the values produced by WaterMath.Tiling, otherwise
// tiles would overlap or leave gaps.
float2 WaterLocalUv(uint id, uint tile, uint tilesX, uint tilesZ, uint subdiv)
{
    if (subdiv == 0u) return WaterCorner(id) - 0.5f;
    // Clamp the divisors to 1: these three values come from the root constants, and flow analysis
    // cannot prove they are non-zero, so dividing directly would be flagged by fxc as "possibly
    // uninitialized"; meanwhile integer division by zero is itself undefined in WGSL, so all four
    // backends need this line. In practice WaterMath.Tiling guarantees all three are >= 1 when this
    // branch is reached, so the clamp merely spells out an already-valid premise for the compiler.
    uint nx = max(tilesX, 1u);
    uint nz = max(tilesZ, 1u);
    uint sd = max(subdiv, 1u);
    uint quad = id / 6u;
    float2 corner = WaterCorner(id % 6u);
    float2 quadUv = (float2(quad % sd, quad / sd) + corner) / (float)sd;
    float2 tileXy = float2(tile % nx, tile / nx);
    return (tileXy + quadUv) / float2(nx, nz) - 0.5f;
}

// Gerstner displacement and tangents. The parameters of four components are packed at
// water + 8 .. + 11 (amplitude, wavenumber, direction.x, direction.z).
//
// Phase speed is not passed separately: deep-water dispersion ω = sqrt(g·k) is uniquely determined
// by the wavenumber, so it is computed here on the fly. Hence "velocity" has only two sources: the
// wavenumber k (derived from wind speed via the wavelength spectrum) and the multiplier at
// water + 13.x, so the authoring layer can speed up or slow down an entire sea by adjusting one number.
//
// Returns the displacement (x/z is the horizontal orbit, y is the height); tangent/binormal are the
// partial derivatives of the displaced surface. Note the two terms use different trigonometric
// functions: the horizontal displacement contributes k·A·sin (lateral compression), the height
// contributes k·A·cos (vertical tilt). Mixing them up flips the normal's sign at wave crests and
// troughs, producing dark streaks along the wave direction on the water.
float3 WaterGerstner(float2 p, float t, uint offset, out float3 tangent, out float3 binormal)
{
    float3 displacement = float3(0.0f, 0.0f, 0.0f);
    tangent = float3(1.0f, 0.0f, 0.0f);
    binormal = float3(0.0f, 0.0f, 1.0f);
    float choppiness = WaterF(offset + 12u).w;
    float speedScale = WaterF(offset + 13u).x;
    [unroll]
    for (uint i = 0u; i < 4u; i++)
    {
        float4 wave = WaterF(offset + 8u + i);
        float amplitude = wave.x;
        float k = wave.y;
        // When wind waves are off BuildWaves zeroes all four slots: k being 0 skips directly, and
        // both position and normal stay at identity values.
        // Not using continue: this loop is fully unrolled by fxc, and an unrolled continue triggers
        // false flow-analysis warnings on some versions.
        if (k > 0.0f)
        {
            float2 dir = float2(wave.z, wave.w);
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

// The instance index is the tile index: when tiled, a single DrawInstanced draws the whole tile
// table. The slot comes from a root constant rather than the instance index, because
// <see cref="WaterMath.MaxTilesPerWater"/> is only needed by WebGPU as an encoding base.
WaterVertex VSMain(uint id : SV_VertexID, uint tile : SV_InstanceID)
{
    WaterVertex o;
    uint slot = waterParams.x;
    uint offset = WaterOffset(slot);
    float2 uv = WaterLocalUv(id, tile, waterParams.y, waterParams.z, waterParams.w);
    float4 world = WaterTransform(float4(uv.x, 0.0f, uv.y, 1.0f), offset);

    // Displacement is applied after model->world: Gerstner is a traveling wave defined on the world
    // XZ plane, so putting it in model space would make the wave direction rotate with the vehicle
    // as soon as the rectangle is rotated.
    float3 tangent, binormal;
    world.xyz += WaterGerstner(world.xz, WaterF(10u).z, offset, tangent, binormal);

    o.position = WaterTransform(world, 4u);
    o.world = world.xyz;
    // cross(binormal, tangent) rather than the other way around: when undisturbed
    // (0,0,1)×(1,0,0) = (0,1,0), pointing straight up.
    o.normal = normalize(cross(binormal, tangent));
    o.slot = slot;
    return o;
}
""";

    /// <summary>
    /// Evaluation of normal perturbation, refraction, absorption, Fresnel, and rain ripples.
    ///
    /// The order cannot be swapped: compensating the software depth test with the (perturbed) normal
    /// is meaningless, so the depth test uses the unperturbed fragment position; refraction and
    /// absorption both depend on the same definition of thickness -- "the distance from the water
    /// surface to the scene surface along the view ray" -- which is reconstructed once from the
    /// inverse VP and evaluated directly; they must not each be linearized again, otherwise the two
    /// attenuations would disagree at grazing angles.
    /// </summary>
    public const string PixelHlsl = """
float3 WaterSampleSky(float3 direction)
{
    // Verbatim the same parameters as SampleSkyViewRadiance in the main shader:
    // u = atan2(x,-z)/2pi+0.5, v = 0.5-0.5*sign(y)*sqrt(|y|).
    // Sampling only at LOD 0: the LUT has no mip, so derivatives across the seam are meaningless anyway.
    float2 uv = float2(atan2(direction.x, -direction.z) * 0.15915494309f + 0.5f,
        0.5f - 0.5f * sign(direction.y) * sqrt(abs(direction.y)));
    return waterSkyView.SampleLevel(waterLinearClamp, uv, 0.0f).rgb;
}

// Procedural water waves: a superposition of two sine waves in different directions. The second
// column has a higher frequency and advances in the opposite phase, so the two columns do not
// translate at the same speed and there is no cheap "whole texture sliding" look.
//
// What is returned is the **gradient** of the height field (m/m), not a normalized direction vector:
// the coefficient = wavenumber × amplitude, with the two columns being 2.4×0.016 and 3.7×0.009
// respectively; the sum is under 0.07 and the normal tilts only 3~4° (a breeze). This dimension must
// not be dropped: an earlier version returned d·cos directly, with a magnitude close to 1, so the
// normal tilted more than 15°; when viewed from above, nearly half the pixels always had the normal
// facing away from the view, facing was saturated to 0 and Fresnel saturated to 1, and the water
// degenerated in that region into a fully opaque specular reflection block (which, after combining
// the beat frequencies of the two waves, is a regular pattern of black circular spots).
float2 WaterWave(float2 p, float t, float speed)
{
    float2 d1 = float2(0.86f, 0.51f);
    float2 d2 = float2(-0.42f, 0.91f);
    float k1 = dot(p, d1) * 2.4f + t * speed * 6.2831853f;
    float k2 = dot(p, d2) * 3.7f - t * speed * 5.1f;
    return d1 * cos(k1) * (2.4f * 0.016f) + d2 * cos(k2) * (3.7f * 0.009f);
}

// Rain ripples: three sets of concentric rings, with the phase offset by index; the ring centers
// are fixed so the water surface does not drift as a whole. Again in gradient units: the ring's
// spatial frequency is 26/m and its amplitude 0.0025m, the single-ring gradient is about 0.065,
// and the three rings sum to the same order as the wind term, so rain does not suddenly jump to a
// different slope scale.
float2 WaterRipple(float2 p, float t)
{
    float2 acc = float2(0.0f, 0.0f);
    for (uint i = 0u; i < 3u; i++)
    {
        float2 c = float2(sin(float(i) * 12.9898f) * 7.0f, cos(float(i) * 78.233f) * 7.0f);
        float2 d = p - c;
        float r = length(d);
        float phase = frac(t * 1.7f + float(i) * 0.37f);
        float wave = sin(r * 26.0f - phase * 40.0f) * saturate(1.0f - phase);
        acc += d / max(r, 1e-4f) * wave;
    }
    return acc * (26.0f * 0.0025f);
}

// View-space forward depth (LH + [0,1]), from the same reconstruction formula as the main shader /
// GTAO / SsrEffect. The variables must not be named near / far: they are reserved words in fxc
// (legacy clip-plane semantics) and flow analysis would falsely report them as uninitialized.
float WaterLinear(float d)
{
    float nearPlane = WaterF(12).z, farPlane = WaterF(12).w;
    return nearPlane * farPlane / (farPlane - d * (farPlane - nearPlane));
}

// Single exit + explicit .x: fxc 4.x's flow analysis falsely reports X4000 (use of potentially
// uninitialized variable) for "templated SampleLevel return value implicitly converted to float +
// multiple returns". Written as a single assignment chain, that path does not exist.
float WaterHiZ(float2 uv, int level)
{
    float value;
    if (level <= 0) value = waterHz0.SampleLevel(waterLinearClamp, uv, 0.0f).x;
    else if (level == 1) value = waterHz1.SampleLevel(waterLinearClamp, uv, 0.0f).x;
    else if (level == 2) value = waterHz2.SampleLevel(waterLinearClamp, uv, 0.0f).x;
    else value = waterHz3.SampleLevel(waterLinearClamp, uv, 0.0f).x;
    return value;
}

// Screen-space reflection: march through screen space from the water surface along the reflection
// direction, using the Hi-Z pyramid to skip whole empty regions; on a hit, sample the refraction
// snapshot (the pre-draw scene color) -- which is exactly "the opaque scene outside the water",
// also avoiding the conflict that the same resource as the color attachment cannot be both read and
// written. Returns rgb = reflected radiance, a = confidence (0 = no hit, the caller falls back to sky).
float4 WaterTraceReflection(float3 origin, float3 direction, float2 size)
{
    const int coarseSteps = 24;
    const int refineSteps = 6;
    float maxDistance = WaterF(13).x;
    float thickness = WaterF(13).y;
    float4 result = float4(0.0f, 0.0f, 0.0f, 0.0f);
    float4 clip0 = WaterTransform(float4(origin, 1.0f), 4u);
    float4 clip1 = WaterTransform(float4(origin + direction * maxDistance, 1.0f), 4u);

    // When the ray goes behind the camera there is no screen-space information available: shorten it
    // along the original direction to just before the camera near plane.
    float endT = 1.0f;
    if (clip1.w <= 0.001f)
    {
        float denom = clip1.w - clip0.w;
        endT = denom < -0.0001f ? saturate(-clip0.w / denom) : 0.0f;
        clip1 = WaterTransform(float4(origin + direction * (maxDistance * endT), 1.0f), 4u);
    }

    if (clip0.w > 0.001f && clip1.w > 0.001f && endT > 0.001f)
    {
        float2 uv0 = clip0.xy / clip0.w * float2(0.5f, -0.5f) + 0.5f;
        float2 uv1 = clip1.xy / clip1.w * float2(0.5f, -0.5f) + 0.5f;
        float z0 = clip0.z / clip0.w;
        float z1 = clip1.z / clip1.w;
        float2 delta = uv1 - uv0;

        // How many pixels each step spans determines which pyramid level to query: short rays use a
        // fine level, otherwise the coarse blocks would never register a hit.
        float pixels = length(delta * size) / (float)coarseSteps;
        int level = 3;
        if (pixels < 12.0f) level = 2;
        if (pixels < 6.0f) level = 1;
        if (pixels < 3.0f) level = 0;

        float2 coarse = uv0;
        float coarseZ = z0;
        [loop]
        for (int i = 1; i <= coarseSteps; i++)
        {
            float t = (float)i / (float)coarseSteps;
            float2 next = uv0 + delta * t;
            float nextZ = lerp(z0, z1, t);
            // Terminate once off-screen: information outside the screen does not exist, and
            // continuing would only sample clamped edge pixels.
            if (next.x < 0.0f || next.x > 1.0f || next.y < 0.0f || next.y > 1.0f) break;
            // The ray depth has not yet crossed this block's nearest surface: skip the whole block.
            if (nextZ < WaterHiZ(next, level))
            {
                coarse = next;
                coarseZ = nextZ;
                continue;
            }
            // Crossed: approach the true intersection point within (previous node, current node).
            [loop]
            for (int j = 1; j <= refineSteps; j++)
            {
                float tj = (float)j / (float)refineSteps;
                float2 suv = lerp(coarse, next, tj);
                float rayZ = lerp(coarseZ, nextZ, tj);
                int2 coord = clamp(int2(suv * size), int2(0, 0), int2((int)size.x - 1, (int)size.y - 1));
                float sceneD = sceneDepth.Load(int3(coord, 0));
                if (sceneD >= 1.0f) continue;
                // The thickness test is done on linear depth: NDC depth is compressed with distance,
                // so the same threshold differs by orders of magnitude between near and far.
                float rayView = WaterLinear(rayZ);
                float sceneView = WaterLinear(sceneD);
                if (rayView >= sceneView && rayView - sceneView <= thickness)
                {
                    float2 hitUv = saturate(suv);
                    // Fade out at the screen edge: the sampling there is extrapolated, and a hard cut
                    // would draw a bright line along the screen edge.
                    float edge = min(min(hitUv.x, 1.0f - hitUv.x), min(hitUv.y, 1.0f - hitUv.y));
                    float fade = saturate(edge / 0.06f) * saturate(1.0f - t);
                    result = float4(refractionSource.SampleLevel(waterLinearClamp, hitUv, 0.0f).rgb, fade);
                    break;
                }
            }
            break;
        }
    }
    return result;
}

float4 PSMain(WaterVertex input) : SV_TARGET
{
    uint offset = WaterOffset(input.slot);
    float4 material0 = WaterF(offset + 4u);   // tint.rgb, baseOpacity
    float4 material1 = WaterF(offset + 5u);   // absorption, refraction, normalStrength, normalTiling
    float4 material2 = WaterF(offset + 6u);   // scrollSpeed, rippleStrength, fresnelF0, roughness
    float4 material3 = WaterF(offset + 7u);   // hasNormalTexture, sortOrder, uvOrigin.x, uvOrigin.z

    float3 camera = WaterF(8).xyz;
    float4 screen = WaterF(9);                // width, height, 1/width, 1/height
    float4 misc = WaterF(10);                 // count, raining, time, skyAvailable
    float4 sun = WaterF(11);                  // sunDirection.xyz, sunIntensity
    float time = misc.z;

    float2 uv = input.position.xy * screen.zw;
    float sceneDepthValue = sceneDepth.Load(int3(input.position.xy, 0));

    // Software depth test: this pass has no depth attachment, so occlusion relies entirely on this
    // line. The bias absorbs jitter when the water surface and pool bottom are coplanar.
    if (input.position.z > sceneDepthValue + 1e-6f) discard;

    // Thickness: the distance from the water surface to the scene surface along the view ray. When
    // depth is at the far plane (sky, no surface) it naturally yields a very large value, so the
    // absorption term saturates to the water color itself and no extra branch is needed.
    float4 ndc = float4(uv * float2(2.0f, -2.0f) + float2(-1.0f, 1.0f), sceneDepthValue, 1.0f);
    float4 scenePosition = ndc.x * WaterF(0) + ndc.y * WaterF(1) + ndc.z * WaterF(2) + ndc.w * WaterF(3);
    float thickness = max(length(scenePosition.xyz / scenePosition.w - input.world), 0.0f);

    // Tangent-space slope -> world normal. The water surface always faces +Y up, so the tangent basis
    // is (1,0,0)/(0,0,1) and there is no TBN matrix.
    float2 planar = input.world.xz;
    float2 slope = WaterWave(planar, time, material2.x);
    if (material3.x > 0.5f)
    {
        float2 tiled = planar * material1.w;
        float2 a = waterNormalTexture.SampleLevel(waterLinearClamp, tiled + time * material2.x * float2(0.7f, 0.3f), 0.0f).rg * 2.0f - 1.0f;
        float2 b = waterNormalTexture.SampleLevel(waterLinearClamp, tiled * 1.37f - time * material2.x * float2(0.4f, 0.8f), 0.0f).rg * 2.0f - 1.0f;
        slope = (a + b) * 0.5f;
    }
    if (misc.y > 0.5f) slope += WaterRipple(planar * 2.0f, time) * material2.y;
    slope *= material1.z;
    // P4: the detail slope is added onto Gerstner's analytic normal rather than rebuilt from scratch.
    // The vertex displacement already turns the large-scale wave shapes into real geometry, and its
    // normal comes from analytic differentiation, more stable than any height-field differencing;
    // the detail term (short-wavelength sines / normal map / rain ripples) accounts for the 3~4°
    // roughness, in the same units as the xz components of the vertex normal, so adding it directly
    // does not skew the normal. When Gerstner is off input.normal is constantly (0,1,0), and this
    // degenerates to the P3 normalize(slope.x,1,slope.y).
    float3 normal = normalize(input.normal + float3(slope.x, 0.0f, slope.y));

    float3 view = normalize(camera - input.world);
    float facing = saturate(dot(normal, view));
    float fresnel = material2.z + (1.0f - material2.z) * pow(1.0f - facing, 5.0f);

    // Refraction: offset the sampling on screen by the slope; the offset unit is pixels (multiplied
    // by 1/width and 1/height later to convert to UV). The 600 is the constant that scales the
    // "gradient" to pixel units: the default gradient 0.07 times RefractionStrength 0.035 is about
    // 1.5 pixels. The name must not be offset: this function's slot offset already takes that name,
    // and an HLSL function body is a single scope.
    float2 refractionOffset = slope * material1.y * 600.0f;
    float2 refractedUv = saturate(uv + refractionOffset * screen.zw);
    float3 refracted = refractionSource.SampleLevel(waterLinearClamp, refractedUv, 0.0f).rgb;

    // Absorption: red light attenuates fastest, so thicker regions shift toward cyan-blue. The three
    // exponential terms correspond to the R/G/B absorption coefficients.
    float3 transmittance = exp(-material1.x * thickness * float3(1.35f, 0.85f, 0.55f));
    // Water is a lit scattering medium, so the water color cannot carry its own absolute brightness:
    // tint is the "water albedo" and must be multiplied by lighting. Previously it was added
    // straight into the HDR image, so once the camera reached night and the scene went fully dark
    // the pool would glow by itself (auto-exposure would then amplify it again). Here the brightness
    // of the pool-bottom snapshot serves as the daylight proxy: it approaches 0 at night and the
    // water color extinguishes accordingly; by day it saturates to 1 and the appearance matches the
    // original. The proxy lives in the snapshot rather than a constant so that it also tracks local
    // lighting (caustics on the pool bottom, torches, and lightning all illuminate the water).
    float daylight = saturate(dot(refracted, float3(0.2126f, 0.7152f, 0.0722f)) * 3.0f);
    float3 body = refracted * transmittance + material0.rgb * (1.0f - transmittance) * daylight;

    // Reflection: try screen-space reflection first (Hi-Z accelerated), and fall back to the
    // procedural sky if there is no hit. The ray uses the unclamped direction: when looking down at
    // the water the reflection direction points toward the bottom, where the sky LUT has no information.
    float3 reflectDirection = reflect(-view, normal);
    // Before sampling the sky, clamp the part below the horizon back to the horizon: the LUT's lower
    // hemisphere stores near-ground haze (almost completely black at night), and when looking down
    // at the water even a slight tilt of the normal would cast the reflection there. Without the
    // clamp, the extrema of the normal perturbation would turn into whole patches of dark spots,
    // appearing as a regular arrangement of black circular dots instead of sky.
    float3 skyDirection = float3(reflectDirection.x, max(reflectDirection.y, 0.0f), reflectDirection.z);
    // The fallback color is likewise multiplied by daylight: when the StaticCube tier has no sky
    // LUT, this constant should not keep glowing at night.
    float3 reflection = misc.w > 0.5f ? WaterSampleSky(skyDirection) : float3(0.35f, 0.45f, 0.62f) * daylight;
    if (WaterF(12).x > 0.5f)
    {
        float4 traced = WaterTraceReflection(input.world, reflectDirection, screen.xy);
        reflection = lerp(reflection, traced.rgb, saturate(traced.a * WaterF(12).y));
    }
    // Sun specular: sun.xyz is the propagation direction "toward the surface", so the vector
    // pointing to the sun must be negated.
    float3 toSun = normalize(-sun.xyz);
    float3 halfVector = normalize(view + toSun);
    float shininess = 2.0f / max(material2.w * material2.w, 1e-3f);
    reflection += sun.w * pow(saturate(dot(normal, halfVector)), shininess) * float3(1.0f, 0.95f, 0.85f) * 0.5f;

    float3 color = lerp(body, reflection, fresnel);
    // Volumetric thickness makes the water opaque on its own: only thin regions show the bottom,
    // while thick regions show only the water color.
    float thicknessAlpha = 1.0f - exp(-thickness * 0.9f);
    float alpha = saturate(max(lerp(material0.a, 1.0f, fresnel), thicknessAlpha * 0.9f));
    return float4(color, alpha);
}
""";
}
