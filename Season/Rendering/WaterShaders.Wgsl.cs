// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// P2 WGSL variant of the water surface material. All four backends share the same semantics
/// and frame buffer layout (see <see cref="WaterMath"/>); this file only handles "read the layout
/// + evaluate", it does not define the layout.
///
/// The differences from the other three backends come from two hard limits of WebGPU, the same
/// source as <see cref="DecalShaders.Wgsl"/>:
/// <list type="bullet">
/// <item>No push constant: "which water slot this draw uses" is carried by <c>instance_index</c>
/// (the JS side calls <c>draw(6, 1, 0, slot)</c>), then sent to the fragment stage via flat interpolation;</item>
/// <item>Depth can only be <c>textureLoad</c>: <c>texture_depth_2d</c> does not support filtered
/// sampling, and the software depth test needs point sampling anyway.</item>
/// </list>
///
/// Refraction sampling uses <c>textureSampleLevel</c> rather than the implicit-derivative version:
/// the same water surface has an enormous pixel footprint at grazing angles, so the implicit LOD
/// would fetch blurred neighboring color at the water surface edges, while only LOD 0 is needed here.
/// </summary>
public static partial class WaterShaders
{
    /// <summary>
    /// The vertex and fragment entry points are combined into a single source; the backend only
    /// builds one shader module.
    /// </summary>
    public const string Wgsl = """
struct WaterVaryings {
    @builtin(position) position : vec4<f32>,
    @location(0) world : vec3<f32>,
    // P4: analytic normal after vertex displacement (world space). Constant (0,1,0) when Gerstner is off.
    @location(1) normal : vec3<f32>,
    // The water slot arrives with the instance index: WebGPU has no push constant, so this is the
    // only way to get it to the fragment stage.
    @location(2) @interpolate(flat) slot : u32,
};

// Frame buffer layout is in WaterMath, the indices are textually identical to the other three
// backends. group 0 is all "constant for the whole frame" resources.
@group(0) @binding(0) var<storage, read> waterFrame : array<vec4<f32>>;
@group(0) @binding(1) var sceneDepth : texture_depth_2d;
@group(0) @binding(2) var refractionSource : texture_2d<f32>;
@group(0) @binding(3) var waterSkyView : texture_2d<f32>;
@group(0) @binding(4) var waterLinearClamp : sampler;
// The four levels of the P3 Hi-Z pyramid (1/2 ~ 1/16); none is sampled when frame[12].x is 0.
@group(0) @binding(5) var waterHz0 : texture_2d<f32>;
@group(0) @binding(6) var waterHz1 : texture_2d<f32>;
@group(0) @binding(7) var waterHz2 : texture_2d<f32>;
@group(0) @binding(8) var waterHz3 : texture_2d<f32>;
// The normal map differs per water surface (and may be absent, falling back to procedural waves),
// so it is in its own group: the frame-level binding group is built only once per frame, and
// only this group is swapped per draw -- the same tradeoff as the decal "texture group". The other
// three backends squeeze five slots into one set, where each texture has an immutable sampler, so
// splitting the group would only add an extra bind call.
@group(1) @binding(0) var waterNormalTexture : texture_2d<f32>;

fn waterF(index : u32) -> vec4<f32> { return waterFrame[index]; }
fn saturate(v : f32) -> f32 { return clamp(v, 0.0, 1.0); }
fn waterOffset(index : u32) -> u32 { return 16u + index * 16u; }
fn waterTransform(p : vec4<f32>, index : u32) -> vec4<f32> {
    return p.x * waterFrame[index] + p.y * waterFrame[index + 1u]
         + p.z * waterFrame[index + 2u] + p.w * waterFrame[index + 3u];
}

// Six 2D corner points, arranged as the quad's two triangles. Single exit, same rationale as the other three backends.
fn waterCorner(id : u32) -> vec2<f32> {
    var corner = vec2<f32>(0.0, 0.0);
    if (id == 1u) { corner = vec2<f32>(1.0, 0.0); }
    else if (id == 2u) { corner = vec2<f32>(1.0, 1.0); }
    else if (id == 3u) { corner = vec2<f32>(0.0, 0.0); }
    else if (id == 4u) { corner = vec2<f32>(1.0, 1.0); }
    else if (id == 5u) { corner = vec2<f32>(0.0, 1.0); }
    return corner;
}

// [0,1] grid coordinate within the tile -> square [-0.5,0.5] planar coordinate.
// The divisor is clamped to 1: integer division by zero is undefined in WGSL, and the shader
// cannot prove the value from the frame data is non-zero.
fn waterLocalUv(id : u32, tile : u32, tilesX : u32, tilesZ : u32, subdiv : u32) -> vec2<f32> {
    if (subdiv == 0u) { return waterCorner(id) - vec2<f32>(0.5, 0.5); }
    let nx = max(tilesX, 1u);
    let nz = max(tilesZ, 1u);
    let sd = max(subdiv, 1u);
    let quad = id / 6u;
    let corner = waterCorner(id % 6u);
    let quadUv = (vec2<f32>(f32(quad % sd), f32(quad / sd)) + corner) / f32(sd);
    let tileXy = vec2<f32>(f32(tile % nx), f32(tile / nx));
    return (tileXy + quadUv) / vec2<f32>(f32(nx), f32(nz)) - vec2<f32>(0.5, 0.5);
}

struct GerstnerResult {
    displacement : vec3<f32>,
    tangent : vec3<f32>,
    binormal : vec3<f32>,
};

// Gerstner displacement and tangent vectors, parameters at water + 8 .. + 11 (amplitude, wavenumber, direction.x, direction.z).
// The phase speed is computed on the fly from the deep-water dispersion ω = sqrt(g·k); the frame
// buffer stores only the wavenumber and the speed multiplier at water + 13.x.
// Horizontal displacement contributes k·A·sin (lateral compression), height contributes k·A·cos
// (vertical tilt); mixing them up flips the sign of the normal at crests and troughs.
fn waterGerstner(p : vec2<f32>, t : f32, offset : u32) -> GerstnerResult {
    var result : GerstnerResult;
    result.displacement = vec3<f32>(0.0);
    result.tangent = vec3<f32>(1.0, 0.0, 0.0);
    result.binormal = vec3<f32>(0.0, 0.0, 1.0);
    let choppiness = waterFrame[offset + 12u].w;
    let speedScale = waterFrame[offset + 13u].x;
    for (var i : u32 = 0u; i < 4u; i = i + 1u) {
        let wave = waterFrame[offset + 8u + i];
        let amplitude = wave.x;
        let k = wave.y;
        // All four are 0 when wind waves are off: skip when k is 0, keeping the vertex and normal at identity values.
        if (k > 0.0) {
            let dir = wave.zw;
            let omega = sqrt(9.81 * k) * speedScale;
            let phase = k * dot(dir, p) - omega * t;
            let s = sin(phase);
            let c = cos(phase);
            let q = choppiness * amplitude;
            result.displacement.x += q * dir.x * c;
            result.displacement.y += amplitude * s;
            result.displacement.z += q * dir.y * c;
            let ks = k * amplitude * s;
            let kc = k * amplitude * c;
            result.tangent += vec3<f32>(-choppiness * ks * dir.x * dir.x, kc * dir.x, -choppiness * ks * dir.x * dir.y);
            result.binormal += vec3<f32>(-choppiness * ks * dir.x * dir.y, kc * dir.y, -choppiness * ks * dir.y * dir.y);
        }
    }
    return result;
}

// The unit plane or tile grid is expanded from vertex_index; the instance index encodes both the slot and the tile.
// The low <see cref="WaterMath.TileBits"/> bits are the tile, the high bits are the slot -- the JS side's firstInstance is this product.
@vertex
fn VSMain(@builtin(vertex_index) vertexIndex : u32, @builtin(instance_index) instanceIndex : u32) -> WaterVaryings {
    var out : WaterVaryings;
    let slot = instanceIndex >> 8u;
    let tile = instanceIndex & 255u;
    let offset = waterOffset(slot);
    // No push constant, so the tile grid parameters can only be read from the frame data; the other three backends use root constants.
    let tiling = waterFrame[offset + 12u];
    let uv = waterLocalUv(vertexIndex, tile, u32(tiling.x), u32(tiling.y), u32(tiling.z));
    var world = waterTransform(vec4<f32>(uv.x, 0.0, uv.y, 1.0), offset);

    // Displacement is applied after model->world: Gerstner is a traveling wave on the world XZ plane,
    // so putting it in model space would make it rotate with the rectangle.
    let gerstner = waterGerstner(world.xz, waterFrame[10u].z, offset);
    world = world + vec4<f32>(gerstner.displacement, 0.0);

    out.position = waterTransform(world, 4u);
    out.world = world.xyz;
    // cross(binormal,tangent): (0,0,1)×(1,0,0) = (0,1,0) when undisturbed, pointing straight up.
    out.normal = normalize(cross(gerstner.binormal, gerstner.tangent));
    out.slot = slot;
    return out;
}

fn waterSampleSky(direction : vec3<f32>) -> vec3<f32> {
    // Exact same parameters as the main shader: u = atan2(x,-z)/2pi+0.5, v = 0.5-0.5*sign(y)*sqrt(|y|).
    let uv = vec2<f32>(atan2(direction.x, -direction.z) * 0.15915494309 + 0.5,
        0.5 - 0.5 * sign(direction.y) * sqrt(abs(direction.y)));
    return textureSampleLevel(waterSkyView, waterLinearClamp, uv, 0.0).rgb;
}

// Height field gradient (m/m), coefficient = wavenumber × amplitude, sums to under 0.07, so the normal only tilts 3~4°.
// The units must not be dropped: returning d·cos directly (magnitude ~1) would tilt the normal past 15°, and
// when looking down, half the pixels' normals face away from the view, so facing would be saturated to 0 and
// Fresnel capped at 1, degenerating the water surface into hard-edged specular patches.
fn waterWave(p : vec2<f32>, t : f32, speed : f32) -> vec2<f32> {
    let d1 = vec2<f32>(0.86, 0.51);
    let d2 = vec2<f32>(-0.42, 0.91);
    let k1 = dot(p, d1) * 2.4 + t * speed * 6.2831853;
    let k2 = dot(p, d2) * 3.7 - t * speed * 5.1;
    return d1 * cos(k1) * (2.4 * 0.016) + d2 * cos(k2) * (3.7 * 0.009);
}

// The rain ripples are likewise gradient units: ring frequency 26/m, amplitude 0.0025 m, single-ring gradient about 0.065.
fn waterRipple(p : vec2<f32>, t : f32) -> vec2<f32> {
    var acc = vec2<f32>(0.0);
    for (var i : u32 = 0u; i < 3u; i = i + 1u) {
        let c = vec2<f32>(sin(f32(i) * 12.9898) * 7.0, cos(f32(i) * 78.233) * 7.0);
        let d = p - c;
        let r = length(d);
        let phase = fract(t * 1.7 + f32(i) * 0.37);
        let wave = sin(r * 26.0 - phase * 40.0) * saturate(1.0 - phase);
        acc = acc + d / max(r, 1e-4) * wave;
    }
    return acc * (26.0 * 0.0025);
}

// View-space forward depth (LH + [0,1]), same reconstruction formula as the main shader / GTAO / SsrEffect.
fn waterLinear(d : f32) -> f32 {
    let nearV = waterF(12u).z;
    let farV = waterF(12u).w;
    return nearV * farV / (farV - d * (farV - nearV));
}

fn waterHiZ(uv : vec2<f32>, level : i32) -> f32 {
    if (level <= 0) { return textureSampleLevel(waterHz0, waterLinearClamp, uv, 0.0).r; }
    if (level == 1) { return textureSampleLevel(waterHz1, waterLinearClamp, uv, 0.0).r; }
    if (level == 2) { return textureSampleLevel(waterHz2, waterLinearClamp, uv, 0.0).r; }
    return textureSampleLevel(waterHz3, waterLinearClamp, uv, 0.0).r;
}

// Screen-space reflection: march through screen space along the reflection direction, using the
// Hi-Z pyramid to skip empty regions in whole blocks; on a hit, sample the refraction snapshot
// (the opaque scene color from before this pass). rgb = radiance, a = confidence (0 = no hit).
fn waterTraceReflection(origin : vec3<f32>, direction : vec3<f32>, size : vec2<f32>) -> vec4<f32> {
    let coarseSteps : i32 = 24;
    let refineSteps : i32 = 6;
    let maxDistance = waterF(13u).x;
    let thickness = waterF(13u).y;
    var result = vec4<f32>(0.0);
    let clip0 = waterTransform(vec4<f32>(origin, 1.0), 4u);
    var clip1 = waterTransform(vec4<f32>(origin + direction * maxDistance, 1.0), 4u);

    // When the ray goes behind the camera there is no screen-space information available: shrink along the original direction to just before the camera near plane.
    var endT = 1.0;
    if (clip1.w <= 0.001) {
        let denom = clip1.w - clip0.w;
        endT = select(0.0, saturate(-clip0.w / denom), denom < -0.0001);
        clip1 = waterTransform(vec4<f32>(origin + direction * (maxDistance * endT), 1.0), 4u);
    }

    if (clip0.w > 0.001 && clip1.w > 0.001 && endT > 0.001) {
        let uv0 = clip0.xy / clip0.w * vec2<f32>(0.5, -0.5) + vec2<f32>(0.5);
        let uv1 = clip1.xy / clip1.w * vec2<f32>(0.5, -0.5) + vec2<f32>(0.5);
        let z0 = clip0.z / clip0.w;
        let z1 = clip1.z / clip1.w;
        let delta = uv1 - uv0;

        // How many pixels each step spans decides which pyramid level to query: short rays use fine levels, otherwise the coarse blocks would never hit.
        let pixels = length(delta * size) / f32(coarseSteps);
        var level : i32 = 3;
        if (pixels < 12.0) { level = 2; }
        if (pixels < 6.0) { level = 1; }
        if (pixels < 3.0) { level = 0; }

        var coarse = uv0;
        var coarseZ = z0;
        for (var i : i32 = 1; i <= coarseSteps; i = i + 1) {
            let t = f32(i) / f32(coarseSteps);
            let next = uv0 + delta * t;
            let nextZ = mix(z0, z1, t);
            // Terminate as soon as it goes off-screen: out-of-screen information does not exist, and continuing only samples the clamped edge pixels.
            if (next.x < 0.0 || next.x > 1.0 || next.y < 0.0 || next.y > 1.0) { break; }
            if (nextZ < waterHiZ(next, level)) {
                coarse = next;
                coarseZ = nextZ;
                continue;
            }
            // Overshot: converge point by point toward the true intersection within (previous node, current node).
            for (var j : i32 = 1; j <= refineSteps; j = j + 1) {
                let tj = f32(j) / f32(refineSteps);
                let suv = mix(coarse, next, tj);
                let rayZ = mix(coarseZ, nextZ, tj);
                let rc = clamp(vec2<i32>(suv * size), vec2<i32>(0, 0),
                    vec2<i32>(i32(size.x) - 1, i32(size.y) - 1));
                let sceneD = textureLoad(sceneDepth, rc, 0);
                if (sceneD >= 1.0) { continue; }
                // The thickness test is done in linear depth: NDC depth gets more compressed the farther away, so the same threshold differs by orders of magnitude near and far.
                let rayView = waterLinear(rayZ);
                let sceneView = waterLinear(sceneD);
                if (rayView >= sceneView && rayView - sceneView <= thickness) {
                    let hitUv = clamp(suv, vec2<f32>(0.0), vec2<f32>(1.0));
                    // Fade at the screen edge: the sampling there is extrapolated, and a hard cut would pull a bright edge along the frame border.
                    let edge = min(min(hitUv.x, 1.0 - hitUv.x), min(hitUv.y, 1.0 - hitUv.y));
                    let fade = saturate(edge / 0.06) * saturate(1.0 - t);
                    result = vec4<f32>(textureSampleLevel(refractionSource, waterLinearClamp, hitUv, 0.0).rgb, fade);
                    break;
                }
            }
            break;
        }
    }
    return result;
}

@fragment
fn PSMain(input : WaterVaryings) -> @location(0) vec4<f32> {
    let offset = waterOffset(input.slot);
    let material0 = waterF(offset + 4u);
    let material1 = waterF(offset + 5u);
    let material2 = waterF(offset + 6u);
    let material3 = waterF(offset + 7u);

    let camera = waterF(8u).xyz;
    let screen = waterF(9u);
    let misc = waterF(10u);
    let sun = waterF(11u);
    let time = misc.z;

    let pixel = vec2<i32>(i32(input.position.x), i32(input.position.y));
    let uv = input.position.xy * screen.zw;
    let sceneDepthValue = textureLoad(sceneDepth, pixel, 0);

    // Software depth test: this pass has no depth attachment, occlusion relies entirely on this line.
    if (input.position.z > sceneDepthValue + 1e-6) { discard; }

    let ndc = vec4<f32>(uv * vec2<f32>(2.0, -2.0) + vec2<f32>(-1.0, 1.0), sceneDepthValue, 1.0);
    let scenePosition = ndc.x * waterF(0u) + ndc.y * waterF(1u) + ndc.z * waterF(2u) + ndc.w * waterF(3u);
    let thickness = max(length(scenePosition.xyz / scenePosition.w - input.world), 0.0);

    let planar = input.world.xz;
    var slope = waterWave(planar, time, material2.x);
    if (material3.x > 0.5) {
        let tiled = planar * material1.w;
        let a = textureSampleLevel(waterNormalTexture, waterLinearClamp, tiled + time * material2.x * vec2<f32>(0.7, 0.3), 0.0).rg * 2.0 - 1.0;
        let b = textureSampleLevel(waterNormalTexture, waterLinearClamp, tiled * 1.37 - time * material2.x * vec2<f32>(0.4, 0.8), 0.0).rg * 2.0 - 1.0;
        slope = (a + b) * 0.5;
    }
    if (misc.y > 0.5) { slope = slope + waterRipple(planar * 2.0, time) * material2.y; }
    slope = slope * material1.z;
    // P4: the detail slope is added onto Gerstner's analytic normal. When Gerstner is off,
    // input.normal is constant (0,1,0), and this degenerates into P3's normalize(vec3(slope.x,1,slope.y)).
    let normal = normalize(input.normal + vec3<f32>(slope.x, 0.0, slope.y));

    let view = normalize(camera - input.world);
    let facing = saturate(dot(normal, view));
    let fresnel = material2.z + (1.0 - material2.z) * pow(1.0 - facing, 5.0);

    // The refraction offset is in pixels (multiplied by 1/width and 1/height below to convert to UV); 600 folds the gradient into pixel scale.
    let offsetUv = slope * material1.y * 600.0;
    let refractedUv = clamp(uv + offsetUv * screen.zw, vec2<f32>(0.0), vec2<f32>(1.0));
    let refracted = textureSampleLevel(refractionSource, waterLinearClamp, refractedUv, 0.0).rgb;

    let transmittance = exp(-material1.x * thickness * vec3<f32>(1.35, 0.85, 0.55));
    // The water body is a lit scattering medium: the water color is albedo and must be multiplied by
    // lighting, otherwise the pool would glow on its own at night after the scene goes dark.
    // Use the luminance of the pool bottom snapshot as a proxy for daylight: approaches 0 at night, saturates to 1 by day.
    let daylight = saturate(dot(refracted, vec3<f32>(0.2126, 0.7152, 0.0722)) * 3.0);
    let body = refracted * transmittance + material0.rgb * (1.0 - transmittance) * daylight;

    // Reflection: try screen-space reflection first (Hi-Z accelerated), fall back to the procedural sky on a miss.
    // The ray uses the unclamped direction: when looking down at the water, the reflection direction points toward
    // the bottom, where the sky LUT has no information.
    let reflectDirection = reflect(-view, normal);
    // Before sampling the sky, clamp below the horizon back to the horizon: the lower hemisphere of the
    // LUT is near-ground haze (almost fully black at night), and a slight normal tilt would sample into it.
    let skyDirection = vec3<f32>(reflectDirection.x, max(reflectDirection.y, 0.0), reflectDirection.z);
    // The mix-in base color is also multiplied by daylight: when the sky LUT is unavailable, this constant should not keep glowing at night.
    var reflection = select(vec3<f32>(0.35, 0.45, 0.62) * daylight, waterSampleSky(skyDirection), misc.w > 0.5);
    if (waterF(12u).x > 0.5) {
        let traced = waterTraceReflection(input.world, reflectDirection, screen.xy);
        reflection = mix(reflection, traced.rgb, saturate(traced.a * waterF(12u).y));
    }
    let toSun = normalize(-sun.xyz);
    let halfVector = normalize(view + toSun);
    let shininess = 2.0 / max(material2.w * material2.w, 1e-3);
    reflection = reflection + sun.w * pow(saturate(dot(normal, halfVector)), shininess) * vec3<f32>(1.0, 0.95, 0.85) * 0.5;

    let color = mix(body, reflection, fresnel);
    let thicknessAlpha = 1.0 - exp(-thickness * 0.9);
    let alpha = saturate(max(mix(material0.a, 1.0, fresnel), thicknessAlpha * 0.9));
    return vec4<f32>(color, alpha);
}
""";
}
