// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// WGSL variant of the waterfall sheet. All four backends share the same semantics and frame buffer layout (see <see cref="WaterfallMath"/>);
/// this one is only responsible for "reading the layout + evaluating", not for defining the layout.
///
/// The differences from the other three backends come from WebGPU's two hard limits, sharing the same origin as <see cref="WaterShaders.Wgsl"/>:
/// <list type="bullet">
/// <item>No push constant: "which sheet slot this draw uses" and the mesh parameters (column count, subdivision count) must be packed into the same
/// <c>instance_index</c> (on the JS side, <c>draw(vc, 1, 0, slot | columns &lt;&lt; 8 | subdiv &lt;&lt; 16)</c>),
/// then the slot is handed to the fragment stage via flat interpolation;</item>
/// <item>Depth can only be read with <c>textureLoad</c>: <c>texture_depth_2d</c> does not support filtered sampling, and the software depth test needs point sampling anyway.</item>
/// </list>
///
/// Sampling always uses <c>textureSampleLevel</c>: the texture's scrolling coordinates come from frame data, the implicit-derivative version simply
/// fails to compile in non-uniform control flow (the hasMask / hasNormal gates), and only LOD 0 is needed here.
/// </summary>
public static partial class WaterfallShaders
{
    /// <summary>
    /// The vertex and fragment entry points are combined into a single source, so the backend only builds one shader module.
    /// </summary>
    public const string Wgsl = """
struct WaterfallVaryings {
    @builtin(position) position : vec4<f32>,
    @location(0) world : vec3<f32>,
    // Interpolated geometric normal and longitudinal tangent; the pixel side re-orthogonalizes, so there is no need to normalize each vertex perfectly here.
    @location(1) normal : vec3<f32>,
    @location(2) tangent : vec3<f32>,
    // x = lateral [0,1], y = arc length [0,1], z = the longitudinal parameter after the acceleration remap (used for scrolling).
    @location(3) uv : vec3<f32>,
    // The sheet slot arrives with the instance index: WebGPU has no push constant, so this is the only way to hand it to the fragment stage.
    @location(4) @interpolate(flat) slot : u32,
};

// The frame buffer layout is in WaterfallMath, and the indices are identical character-for-character with the other three backends. group 0 is entirely "constant per frame" resources;
// the two samplers are separate because the same Mask needs R/G clamped and B/A repeated; WebGPU samplers are explicit arguments, so there is no need to bind twice as in Vulkan.
@group(0) @binding(0) var<storage, read> waterfallFrame : array<vec4<f32>>;
@group(0) @binding(1) var sceneDepth : texture_depth_2d;
@group(0) @binding(2) var waterfallClamp : sampler;
@group(0) @binding(3) var waterfallWrap : sampler;
// The three textures differ per sheet, so they form a separate group: the frame-level bind group is built only once per frame, and only this group is swapped per draw.
@group(1) @binding(0) var waterfallMask : texture_2d<f32>;
@group(1) @binding(1) var waterfallNormal : texture_2d<f32>;
@group(1) @binding(2) var waterfallFlow : texture_2d<f32>;

fn wfF(index : u32) -> vec4<f32> { return waterfallFrame[index]; }
fn saturate(v : f32) -> f32 { return clamp(v, 0.0, 1.0); }
fn wfOffset(index : u32) -> u32 { return 16u + index * 48u; }
fn wfTransform(p : vec4<f32>, index : u32) -> vec4<f32> {
    return p.x * waterfallFrame[index] + p.y * waterfallFrame[index + 1u]
         + p.z * waterfallFrame[index + 2u] + p.w * waterfallFrame[index + 3u];
}

// Analytic mist for the sheet itself; see WaterfallShaders.Hlsl for why this is an exponential rather than a volume
// sample. Slot 37.w is the extinction averaged along this sheet's eye ray, zero when no medium covers the path.
fn wfFogTransmittance(world : vec3<f32>, eye : vec3<f32>, sigma : f32) -> f32 {
    if (sigma <= 0.0) { return 1.0; }
    return exp(-sigma * distance(world, eye));
}

// Per-sheet placement matrix (slots 38..41): points carry w=1, directions carry w=0, and the translation only affects points.
// The normal follows rotation and non-uniform scaling automatically via the cross product of the two tangents, using the same derivation as the other three backends.
fn wfSheetPoint(offset : u32, p : vec3<f32>) -> vec3<f32> {
    return wfTransform(vec4<f32>(p, 1.0), offset + 38u).xyz;
}

fn wfSheetVector(offset : u32, v : vec3<f32>) -> vec3<f32> {
    return wfTransform(vec4<f32>(v, 0.0), offset + 38u).xyz;
}

// Control point segment: two float4 per point, the first is midpoint.xyz + half width, the second is the outward direction .x, 0, .z, 0.
// The ends are clamped rather than wrapped: Catmull-Rom needs p0/p3, and repeating the endpoint on itself keeps the curve from pinching to a point at the lip and the landing point.
fn wfRowCenter(offset : u32, count : u32, s : f32) -> vec4<f32> {
    let clamped = clamp(s, 0.0, f32(count - 1u));
    let i1 = u32(floor(clamped));
    let f = clamped - f32(i1);
    let i0 = select(0u, i1 - 1u, i1 > 0u);
    let i2 = min(i1 + 1u, count - 1u);
    let i3 = min(i1 + 2u, count - 1u);
    let p0 = wfF(offset + i0 * 2u);
    let p1 = wfF(offset + i1 * 2u);
    let p2 = wfF(offset + i2 * 2u);
    let p3 = wfF(offset + i3 * 2u);
    return 0.5 * (2.0 * p1 + (p2 - p0) * f
        + (2.0 * p0 - 5.0 * p1 + 4.0 * p2 - p3) * f * f
        + (3.0 * p1 - p0 - 3.0 * p2 + p3) * f * f * f);
}

fn wfRowOutward(offset : u32, count : u32, s : f32) -> vec4<f32> {
    let clamped = clamp(s, 0.0, f32(count - 1u));
    let i1 = u32(floor(clamped));
    let f = clamped - f32(i1);
    let i0 = select(0u, i1 - 1u, i1 > 0u);
    let i2 = min(i1 + 1u, count - 1u);
    let i3 = min(i1 + 2u, count - 1u);
    let p0 = wfF(offset + i0 * 2u + 1u);
    let p1 = wfF(offset + i1 * 2u + 1u);
    let p2 = wfF(offset + i2 * 2u + 1u);
    let p3 = wfF(offset + i3 * 2u + 1u);
    return 0.5 * (2.0 * p1 + (p2 - p0) * f
        + (2.0 * p0 - 5.0 * p1 + 4.0 * p2 - p3) * f * f
        + (3.0 * p1 - p0 - 3.0 * p2 + p3) * f * f * f);
}

// Six 2D corners, arranged as the two triangles of a quad. Single exit, for the same reason as the other three backends.
fn wfCorner(id : u32) -> vec2<f32> {
    var corner = vec2<f32>(0.0, 0.0);
    if (id == 1u) { corner = vec2<f32>(1.0, 0.0); }
    else if (id == 2u) { corner = vec2<f32>(1.0, 1.0); }
    else if (id == 3u) { corner = vec2<f32>(0.0, 0.0); }
    else if (id == 4u) { corner = vec2<f32>(1.0, 1.0); }
    else if (id == 5u) { corner = vec2<f32>(0.0, 1.0); }
    return corner;
}

// Remap from arc length to advection time. Falling speed grows with the drop (approximately v proportional to sqrt(v)), so scrolling directly with
// v would give the lip and the landing point the same texture speed, reading like a conveyor belt. Here we integrate 1/(0.35+1.65*sqrt(v)) and normalize:
// the antiderivative (x=sqrt(v)) = 1.2121*(x - c*ln(1+x/c)), c=0.35/1.65=0.2121; its value at x=1 is 0.6303,
// so the normalization constant is 1/0.6303 = 1.5866 -- which also absorbs the 1.2121 coefficient of the formula above.
// This guarantees wfAdvection(1)=1: the landing point's sample coordinate equals exactly repeat, aligning with the texture tiling count.
fn wfAdvection(v : f32) -> f32 {
    let x = sqrt(saturate(v));
    let c = 0.2121212;
    let value = x - c * log(1.0 + x / c);
    return saturate(value * 1.5866);
}

// Vertices are expanded from vertex_index into a (rows-1)x(columns-1) quad grid; the instance index encodes both the slot and the mesh parameters:
// bits 0..7 the slot, 8..15 the column count, 16..23 the subdivision count -- bit-for-bit identical to the packing in the C# BuildDrawsJson / JS draw.
@vertex
fn VSMain(@builtin(vertex_index) id : u32, @builtin(instance_index) encoded : u32) -> WaterfallVaryings {
    var o : WaterfallVaryings;
    let slot = encoded & 255u;
    let offset = wfOffset(slot);
    // count comes from the frame buffer and can't be trusted to be >= 2: clamp it so the later (count-1) does not underflow.
    let count = max(u32(wfF(offset + 32u).x), 2u);
    let columns = max((encoded >> 8u) & 255u, 2u);
    let subdiv = max((encoded >> 16u) & 255u, 1u);
    let rows = (count - 1u) * subdiv + 1u;

    let quad = id / 6u;
    let corner = id % 6u;
    let col = quad % (columns - 1u);
    let row = quad / (columns - 1u);
    let c = wfCorner(corner);
    let fu = (f32(col) + c.x) / f32(columns - 1u);
    let fv = (f32(row) + c.y) / f32(rows - 1u);

    let s = fv * f32(count - 1u);
    let centerRow = wfRowCenter(offset, count, s);
    let outRow = wfRowOutward(offset, count, s);
    // Zero-vector fallback: normalize(0) is NaN and would fling the whole sheet out of clip space (harder to track down than a wrong orientation).
    let outward = normalize(vec3<f32>(outRow.x, 0.0, outRow.z) + vec3<f32>(0.0, 0.0, 1e-5));
    let lateral = vec3<f32>(outward.z, 0.0, -outward.x);

    let halfWidth = max(centerRow.w, 0.02);
    let bow = wfF(offset + 37u).y;

    // Section: a closed ring that opens around the control point polyline. fu=0 is the front center, fu=0.5 is the back center hugging the rock,
    // the two sides (0.25 / 0.75) land exactly on the control point polyline -- the cliff face is right there, and the sides must not recede,
    // otherwise the software depth test would cull the whole sheet as being "inside the rock".
    // u (the texture horizontal coordinate) is taken from the lateral position rather than the ring parameter: the front half is therefore identical
    // pixel-for-pixel to the old single sheet, the sides are 0 / 1 (already bitten to transparent by alpha), the back is mirrored, and position and texture are continuous at the seam.
    let theta = fu * 6.28318530718;
    let sinT = sin(theta);
    let cosT = cos(theta);
    let halfDepth = max(wfF(offset + 37u).z, 0.04) * 0.5;

    // The front (cosT>0) gets an extra outward bulge: this is where the old single sheet's bow came from, so the front arc coincides with the old geometry.
    let front = max(cosT, 0.0);
    let slope = halfDepth + 2.0 * bow * front;
    let localPosition = centerRow.xyz
        + lateral * (sinT * halfWidth)
        + outward * (cosT * halfDepth + bow * front * front);
    let u = sinT * 0.5 + 0.5;

    // The normal uses a central difference of neighboring rows, and the ring tangent uses the analytic section derivative:
    // a difference would skew the tangent where the half width changes sharply, whereas the analytic form's direction always matches the section.
    let ds = 0.04;
    let before = wfRowCenter(offset, count, max(s - ds, 0.0));
    let after = wfRowCenter(offset, count, min(s + ds, f32(count - 1u)));
    let localTangentV = after.xyz - before.xyz;
    let localTangentU = lateral * (cosT * halfWidth) + outward * (-sinT * slope);

    // Placement: compute the local shape first, then move it to world in one go. The tangents are transformed along with it, so the normal automatically follows rotation and non-uniform scaling.
    let position = wfSheetPoint(offset, localPosition);
    let tangentV = wfSheetVector(offset, localTangentV);
    let tangentU = wfSheetVector(offset, localTangentU);
    let sectionNormal = wfSheetVector(offset, lateral * (sinT * slope) + outward * (cosT * halfWidth));

    var normal = normalize(cross(tangentV, tangentU) + vec3<f32>(0.0, 1e-5, 0.0));
    if (dot(normal, sectionNormal) < 0.0) { normal = -normal; }

    o.position = wfTransform(vec4<f32>(position, 1.0), 0u);
    o.world = position;
    o.normal = normal;
    o.tangent = normalize(tangentV);
    o.uv = vec3<f32>(u, fv, wfAdvection(fv));
    o.slot = slot;
    return o;
}

@fragment
fn PSMain(input : WaterfallVaryings) -> @location(0) vec4<f32> {
    let offset = wfOffset(input.slot);
    let rowHeader = wfF(offset + 32u);  // count, arc, maxHalfWidth, flags
    let material0 = wfF(offset + 33u);  // deep.rgb, opacity
    let material1 = wfF(offset + 34u);  // foam.rgb, foamAmount
    let material2 = wfF(offset + 35u);  // scroll, normalStrength, normalTiling, erosion
    let material3 = wfF(offset + 36u);  // lipFoam, footFoam, edgeFoam, edgeSoftness
    let camera = wfF(4u);
    let sun = wfF(6u);                  // propagation direction.xyz, intensity
    let sunColor = wfF(7u);
    let ambient = wfF(8u);
    let time = camera.w;

    let pixel = vec2<i32>(i32(input.position.x), i32(input.position.y));
    let sceneDepthValue = textureLoad(sceneDepth, pixel, 0);
    // Software depth test: this pass has no depth attachment. The bias absorbs the reconstruction error between the sheet and the rock surface at grazing angles.
    if (input.position.z > sceneDepthValue + 1e-6) { discard; }

    let u = saturate(input.uv.x);
    let v = saturate(input.uv.y);
    let flowV = saturate(input.uv.z);
    let lateral = u * 2.0 - 1.0;

    let flags = u32(rowHeader.w);
    let hasMask = (flags & 1u) != 0u;
    let hasNormal = (flags & 2u) != 0u;
    let hasFlow = (flags & 4u) != 0u;

    // The arc length determines how many times the texture repeats along V: counting 6 meters per segment, an 18-meter waterfall is exactly three segments.
    // The variable name avoids repeat: it skirts the reserved words in some WGSL implementations, and renaming is more reliable than betting on compiler leniency.
    let repeatCount = clamp(max(rowHeader.y, 1.0) / 6.0, 1.0, 6.0);
    let vTiled = flowV * repeatCount - time * material2.x * repeatCount;

    // R/G are mapped once only: they draw the whole structure "from the lip to the landing point", and tiling would copy the outlet halfway down the slope.
    var maskStatic = vec4<f32>(0.55, 0.0, 0.5, 0.5);
    if (hasMask) { maskStatic = textureSampleLevel(waterfallMask, waterfallClamp, vec2<f32>(u, v), 0.0); }
    // B/A are periodic noise, switched to wrap sampling for the scrolling foam and erosion, without seams.
    var maskTiled = vec4<f32>(0.55, 0.0, 0.5, 0.5);
    if (hasMask) { maskTiled = textureSampleLevel(waterfallMask, waterfallWrap, vec2<f32>(u * 2.0, vTiled * 0.75), 0.0); }

    var flowSample = vec4<f32>(0.5, 0.5, 0.5, 1.0);
    if (hasFlow) { flowSample = textureSampleLevel(waterfallFlow, waterfallWrap, vec2<f32>(u * 2.0, vTiled), 0.0); }

    // Flow.R is the lateral drift: it makes the strands sway left and right while moving down, avoiding the whole texture translating like a rigid body.
    let drift = (flowSample.r * 2.0 - 1.0) * 0.06;
    let tiling = max(material2.z, 0.25);
    var normalA = vec2<f32>(0.0, 0.0);
    var normalB = vec2<f32>(0.0, 0.0);
    if (hasNormal) {
        normalA = textureSampleLevel(waterfallNormal, waterfallWrap,
            vec2<f32>((u + drift) * tiling, vTiled * tiling), 0.0).rg * 2.0 - 1.0;
        // The second layer is overlaid in reverse at 1.63x frequency and 0.78x speed: a single-layer scroll turns into uniform translational bands under TAA.
        normalB = textureSampleLevel(waterfallNormal, waterfallWrap,
            vec2<f32>((u - drift) * tiling * 1.63 + 0.37, vTiled * tiling * 0.78 - 0.21), 0.0).rg * 2.0 - 1.0;
    }
    let slope = (normalA + normalB) * (0.5 * material2.y);

    // Foam splits into four channels: the lip, the landing point, both side edges, and the B-channel scrolling foam bands.
    // The four are max'd rather than summed; otherwise their overlap would blow out to pure white and the cartoon shading hierarchy would be lost.
    let lip = 1.0 - saturate(v / max(material3.x, 1e-3));
    let foot = saturate((v - (1.0 - max(material3.y, 1e-3))) / max(material3.y, 1e-3));
    let edge = 1.0 - abs(lateral);
    let edgeBand = saturate((edge - (1.0 - material3.z)) / max(material3.z, 1e-3));
    let bands = saturate(maskTiled.b * 1.35 - 0.35);
    let sheetFoam = saturate(maskStatic.g * 1.25 - 0.15);
    let foam = saturate(max(max(sheetFoam * material1.w, bands * 0.45),
        max(max(lip * 1.10, foot * 0.85), edgeBand * 0.55)));

    // Edge: the geometry is wider than the visible water, and the alpha is bitten out into an organic silhouette by the A-channel noise.
    // A negative cut means it is already out of bounds; the foam uses a wider threshold so the white edge always pokes out a little further than the water body.
    let warp = maskTiled.a;
    let cut = edge - material3.w * (0.55 + 0.75 * warp);
    let edgeCut = smoothstep(0.0, 0.10, cut);
    let foamCut = smoothstep(-0.05, 0.05, cut);
    let holes = 1.0 - material2.w * smoothstep(0.62, 0.95, warp);

    // The R-channel strands also modulate thickness: the 0.60 lower bound ensures that even the thinnest strands are not fully transparent,
    // otherwise the 47% of zeros in the texture would tear the sheet into a sieve.
    let density = 0.60 + 0.40 * saturate(maskStatic.r * 1.6);
    // Back wall test: the half whose geometric normal points away from the view direction is the far side of the water body.
    // Multiplying by 2.5 puts the transition within about 66 degrees -- it fades a little earlier when grazing the side, and is still 0 when facing head-on.
    // Reduced by only 70% rather than zeroed: seen from the side, this back wall is precisely the half that says "the water body has thickness".
    let view = normalize(camera.xyz - input.world);
    let backFace = saturate(-dot(normalize(input.normal), view) * 2.5);
    let bodyAlpha = material0.w * density * edgeCut * holes;
    let foamAlpha = foam * 0.92 * foamCut;
    var alpha = max(bodyAlpha, foamAlpha);
    alpha = alpha * (1.0 - 0.70 * backFace);
    // Leave a 1.5% soft transition at the top and bottom edges: the lip/landing point is where the geometry is truncated, and a hard cut would flash a bright line under TAA.
    alpha = alpha * smoothstep(0.0, 0.015, v) * smoothstep(1.0, 0.985, v);
    alpha = saturate(alpha);

    // Normal: geometric normal + texture slope. First re-orthogonalize from the interpolated normal, to avoid the elongated quads skewing the tangent.
    let normal = normalize(input.normal);
    let tangent = normalize(input.tangent - normal * dot(normal, input.tangent));
    let bitangent = cross(normal, tangent);
    let shadedNormal = normalize(normal + bitangent * slope.x + tangent * slope.y);

    let toSun = -sun.xyz;
    let ndl = saturate(dot(shadedNormal, toSun));
    let ambientLight = ambient.rgb * ambient.w;
    let direct = sunColor.rgb * (sun.w * ndl);
    let body = material0.rgb * (ambientLight + direct * 0.85);
    let foamLit = material1.rgb * (ambientLight * 1.25 + direct * (0.40 + 0.60 * ndl));
    // The back wall is also darkened: when two layers hugging the same texture coordinate overlap, reducing only the alpha makes the color look grey and bright,
    // reducing the brightness a little is what makes it read as "the water behind" rather than "fog in front".
    var color = mix(body, foamLit, saturate(foam));
    color = color * (1.0 - 0.30 * backFace);

    let halfVector = normalize(view + toSun);
    color = color + sunColor.rgb * (sun.w * pow(saturate(dot(shadedNormal, halfVector)), 48.0) * 0.55);
    // A very light rim light: a cartoon-style sheet needs a bright edge when backlit, otherwise the whole thing smears into the background fog and rock.
    color = color + (ambientLight + direct) * pow(1.0 - saturate(dot(shadedNormal, view)), 4.0) * 0.08;

    // Fog last: alpha, not colour, because the fraction the sheet loses is exactly the fraction the already-fogged
    // background shows through.
    alpha = alpha * wfFogTransmittance(input.world, camera.xyz, wfF(offset + 37u).w);
    return vec4<f32>(color, alpha);
}
""";
}
