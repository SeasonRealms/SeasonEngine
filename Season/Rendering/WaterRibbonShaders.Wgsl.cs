// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// WGSL variant of the river ribbon. All four backends share the same semantics and frame buffer layout (see
/// <see cref="WaterRibbonMath"/>); this one is only responsible for "reading the layout + evaluating", not for defining it.
///
/// The differences from the other three backends come from WebGPU's two hard limits, sharing the same origin as
/// <see cref="WaterfallShaders.Wgsl"/>:
/// <list type="bullet">
/// <item>No push constant: "which ribbon slot this draw uses" and the mesh parameters (column count, subdivision count) are
/// packed into the same <c>instance_index</c> (the JS side pushes it down as <c>first_instance</c> unchanged), then the slot is
/// handed to the fragment stage via flat interpolation;</item>
/// <item>Depth can only be read with <c>textureLoad</c>: <c>texture_depth_2d</c> does not support filtered sampling, and the
/// software depth test needs point sampling anyway.</item>
/// </list>
///
/// Sampling always uses <c>textureSampleLevel</c>: the scrolling coordinates come from frame data, the implicit-derivative
/// version simply fails to compile in non-uniform control flow, and only LOD 0 is needed. The two depth derivatives are taken
/// before the discard so the uniformity analysis never has to look past a may-discard statement.
/// </summary>
public static partial class WaterRibbonShaders
{
    /// <summary>
    /// The vertex and fragment entry points are combined into a single source, so the backend only builds one shader module.
    /// </summary>
    public const string Wgsl = """
struct RibbonVaryings {
    @builtin(position) position : vec4<f32>,
    @location(0) world : vec3<f32>,
    // Horizontal normal (up after the placement rotation) and the longitudinal tangent, re-orthogonalized in the pixel stage.
    @location(1) normal : vec3<f32>,
    @location(2) tangent : vec3<f32>,
    // x = lateral [0,1] across the band, y = normalized arc [0,1], z = arc in meters (the scroll source).
    @location(3) uv : vec3<f32>,
    // The ribbon slot arrives with the instance index: WebGPU has no push constant, so this is the only way to hand it down.
    @location(4) @interpolate(flat) slot : u32,
};

// The frame buffer layout is in WaterRibbonMath, and the indices are identical with the other three backends. group 0 is
// entirely "constant per frame"; a ribbon only ever samples with wrapping coordinates, so binding 2 (the clamp sampler that the
// waterfall needs) is left bound-but-unused, which the explicit pipeline layout on the JS side allows.
@group(0) @binding(0) var<storage, read> ribbonFrame : array<vec4<f32>>;
@group(0) @binding(1) var sceneDepth : texture_depth_2d;
@group(0) @binding(3) var ribbonWrap : sampler;
// The three textures differ per ribbon, so they form a separate group: the frame-level bind group is built once per frame.
@group(1) @binding(0) var ribbonMask : texture_2d<f32>;
@group(1) @binding(1) var ribbonNormal : texture_2d<f32>;
@group(1) @binding(2) var ribbonFlow : texture_2d<f32>;

fn rbF(index : u32) -> vec4<f32> { return ribbonFrame[index]; }
fn saturate(v : f32) -> f32 { return clamp(v, 0.0, 1.0); }
fn rbOffset(index : u32) -> u32 { return 16u + index * 48u; }
fn rbTransform(p : vec4<f32>, index : u32) -> vec4<f32> {
    return p.x * ribbonFrame[index] + p.y * ribbonFrame[index + 1u]
         + p.z * ribbonFrame[index + 2u] + p.w * ribbonFrame[index + 3u];
}

// Per-ribbon placement matrix (slots 38..41): points carry w=1, directions carry w=0, and the translation only affects points.
fn rbSheetPoint(offset : u32, p : vec3<f32>) -> vec3<f32> {
    return rbTransform(vec4<f32>(p, 1.0), offset + 38u).xyz;
}

fn rbSheetVector(offset : u32, v : vec3<f32>) -> vec3<f32> {
    return rbTransform(vec4<f32>(v, 0.0), offset + 38u).xyz;
}

// Control point block: two float4 per point, the first is center.xyz + half width, the second is flow.x, 0, flow.z, 0.
// The endpoints clamp rather than wrap, exactly like the waterfall's Catmull-Rom, so the first and last rows stay put.
fn rbRowCenter(offset : u32, count : u32, s : f32) -> vec4<f32> {
    let clamped = clamp(s, 0.0, f32(count - 1u));
    let i1 = u32(floor(clamped));
    let f = clamped - f32(i1);
    let i0 = select(0u, i1 - 1u, i1 > 0u);
    let i2 = min(i1 + 1u, count - 1u);
    let i3 = min(i1 + 2u, count - 1u);
    let p0 = rbF(offset + i0 * 2u);
    let p1 = rbF(offset + i1 * 2u);
    let p2 = rbF(offset + i2 * 2u);
    let p3 = rbF(offset + i3 * 2u);
    return 0.5 * (2.0 * p1 + (p2 - p0) * f
        + (2.0 * p0 - 5.0 * p1 + 4.0 * p2 - p3) * f * f
        + (3.0 * p1 - p0 - 3.0 * p2 + p3) * f * f * f);
}

fn rbRowFlow(offset : u32, count : u32, s : f32) -> vec4<f32> {
    let clamped = clamp(s, 0.0, f32(count - 1u));
    let i1 = u32(floor(clamped));
    let f = clamped - f32(i1);
    let i0 = select(0u, i1 - 1u, i1 > 0u);
    let i2 = min(i1 + 1u, count - 1u);
    let i3 = min(i1 + 2u, count - 1u);
    let p0 = rbF(offset + i0 * 2u + 1u);
    let p1 = rbF(offset + i1 * 2u + 1u);
    let p2 = rbF(offset + i2 * 2u + 1u);
    let p3 = rbF(offset + i3 * 2u + 1u);
    return 0.5 * (2.0 * p1 + (p2 - p0) * f
        + (2.0 * p0 - 5.0 * p1 + 4.0 * p2 - p3) * f * f
        + (3.0 * p1 - p0 - 3.0 * p2 + p3) * f * f * f);
}

// Six 2D corners, arranged as the two triangles of a quad. Single exit, for the same reason as the other three backends.
fn rbCorner(id : u32) -> vec2<f32> {
    var corner = vec2<f32>(0.0, 0.0);
    if (id == 1u) { corner = vec2<f32>(1.0, 0.0); }
    else if (id == 2u) { corner = vec2<f32>(1.0, 1.0); }
    else if (id == 3u) { corner = vec2<f32>(0.0, 0.0); }
    else if (id == 4u) { corner = vec2<f32>(1.0, 1.0); }
    else if (id == 5u) { corner = vec2<f32>(0.0, 1.0); }
    return corner;
}

// Vertices are expanded from vertex_index into a (rows-1)x(columns-1) quad grid; the instance index encodes both the slot and
// the mesh parameters: bits 0..7 the slot, 8..15 the column count, 16..23 the subdivision count -- bit-for-bit identical to the
// packing in the C# BuildDrawsJson / JS draw.
@vertex
fn VSMain(@builtin(vertex_index) id : u32, @builtin(instance_index) encoded : u32) -> RibbonVaryings {
    var o : RibbonVaryings;
    let slot = encoded & 255u;
    let offset = rbOffset(slot);
    // count comes from the frame buffer and can't be trusted to be >= 2: clamp it so the later (count-1) does not underflow.
    let count = max(u32(rbF(offset + 32u).x), 2u);
    let columns = max((encoded >> 8u) & 255u, 2u);
    let subdiv = max((encoded >> 16u) & 255u, 1u);
    let rows = (count - 1u) * subdiv + 1u;

    let quad = id / 6u;
    let corner = id % 6u;
    let col = quad % (columns - 1u);
    let row = quad / (columns - 1u);
    let c = rbCorner(corner);
    let fu = (f32(col) + c.x) / f32(columns - 1u);
    let fv = (f32(row) + c.y) / f32(rows - 1u);

    let s = fv * f32(count - 1u);
    let centerRow = rbRowCenter(offset, count, s);
    let flowRow = rbRowFlow(offset, count, s);
    // Zero-vector fallback: normalize(0) is NaN and would throw the whole band out of clip space.
    let flow = normalize(vec3<f32>(flowRow.x, 0.0, flowRow.z) + vec3<f32>(0.0, 0.0, 1e-5));
    // The lateral axis is the flow turned 90 degrees in the XZ plane. Deriving it from the authored direction rather than from
    // the polyline is what keeps a bend at an even width: a tangent-derived normal pinches the inside of the turn.
    let lateral = vec3<f32>(flow.z, 0.0, -flow.x);

    let halfWidth = max(centerRow.w, 0.05);
    // u spans [-1,1] across the band; the surface is flat, so the only vertical contribution is the authored Y.
    let u = fu * 2.0 - 1.0;
    let localPosition = centerRow.xyz + lateral * (u * halfWidth);

    // Longitudinal tangent by central difference of neighbouring rows: cheap, and the pixel stage only needs its direction.
    let ds = 0.05;
    let before = rbRowCenter(offset, count, max(s - ds, 0.0));
    let after = rbRowCenter(offset, count, min(s + ds, f32(count - 1u)));
    let localTangent = after.xyz - before.xyz;

    // Placement last, so the shape is built in author space and the world matrix is applied once.
    let position = rbSheetPoint(offset, localPosition);
    let tangent = rbSheetVector(offset, localTangent);
    let lateralWorld = rbSheetVector(offset, lateral);

    // cross(flow, lateral) = +Y in author space, which after the placement rotation is "up" for the water surface.
    // The tiny +Y bias inside the cross product keeps the sign stable when the tangent degenerates at a clamped endpoint.
    let normal = normalize(cross(tangent, lateralWorld) + vec3<f32>(0.0, 1e-4, 0.0));

    o.position = rbTransform(vec4<f32>(position, 1.0), 0u);
    o.world = position;
    o.normal = normal;
    o.tangent = tangent;
    // .z is the arc in meters; the fragment stage adds the ribbon's phase to it, which is the whole seam-hiding trick.
    o.uv = vec3<f32>(fu, fv, fv * rbF(offset + 32u).y);
    o.slot = slot;
    return o;
}

@fragment
fn PSMain(input : RibbonVaryings) -> @location(0) vec4<f32> {
    let offset = rbOffset(input.slot);
    let rowHeader = rbF(offset + 32u);  // count, arc meters, maxHalfWidth, flags
    let material0 = rbF(offset + 33u);  // water color.rgb, opacity
    let material1 = rbF(offset + 34u);  // foam color.rgb, foam amount
    let material2 = rbF(offset + 35u);  // flow speed m/s, normal strength, normal tiling, unused
    let material3 = rbF(offset + 36u);  // edge foam, edge softness, depth meters, unused
    let material4 = rbF(offset + 37u);  // sort order, phase meters, 0, 0
    let camera = rbF(4u);
    let sun = rbF(6u);                  // propagation direction.xyz, intensity
    let sunColor = rbF(7u);
    let ambient = rbF(8u);
    let time = camera.w;

    let pixel = vec2<i32>(i32(input.position.x), i32(input.position.y));
    let sceneDepthValue = textureLoad(sceneDepth, pixel, 0);
    // Both screen-space derivatives are taken before the discard: WGSL's uniformity analysis would otherwise have to look past
    // a may-discard statement, and the value itself is unaffected either way.
    let depthSlope = max(abs(dpdx(sceneDepthValue)) + abs(dpdy(sceneDepthValue)), 1e-7);
    let gap = sceneDepthValue - input.position.z;
    // Software depth test: this pass has no depth attachment. The band is drawn after the opaque scene, so anything already
    // closer than the water surface (the bank it is tucked under, a rock standing in it) must win.
    if (gap < 0.0) { discard; }

    // Soft shoreline: the classic soft-particle trick. Dividing by the screen-space depth slope makes the fade one or two
    // pixels wide at every distance, so the waterline dissolves into the bank instead of ending on a stair-stepped edge.
    let shore = saturate(gap / (depthSlope * 2.5));
    // A broader ramp from the same quantity, used only as the shallow/deep tint. 24 slopes wide is roughly "half the channel".
    let thickness = saturate(gap / (depthSlope * 24.0));

    let u = saturate(input.uv.x);   // across the band [0,1]
    let v = saturate(input.uv.y);   // along the band [0,1]
    let lateral = u * 2.0 - 1.0;

    let flags = u32(rowHeader.w);
    let hasMask = (flags & 1u) != 0u;
    let hasNormal = (flags & 2u) != 0u;
    let hasFlow = (flags & 4u) != 0u;

    // Scroll coordinate in meters of world travel: phase + arc, minus time * speed. Because each segment carries the phase it
    // starts at, two neighbours with the same speed read the same world distance for every frame - direction and speed both
    // continuous across the joint, which is the property the river layout depends on.
    let travelled = material4.y + input.uv.z - time * max(material2.x, 0.0);
    let vTiled = travelled / 3.0;

    var mask = vec4<f32>(0.55, 0.0, 0.5, 0.5);
    if (hasMask) { mask = textureSampleLevel(ribbonMask, ribbonWrap, vec2<f32>(u * 2.0, vTiled * 0.75), 0.0); }
    var flowSample = vec4<f32>(0.5, 0.5, 0.5, 1.0);
    if (hasFlow) { flowSample = textureSampleLevel(ribbonFlow, ribbonWrap, vec2<f32>(u * 2.0, vTiled * 1.3), 0.0); }

    // Lateral drift so the strands sway as they travel, instead of the whole texture translating like a rigid body.
    let drift = (flowSample.r * 2.0 - 1.0) * 0.05;
    let tiling = max(material2.z, 0.25);
    var normalA = vec2<f32>(0.0, 0.0);
    var normalB = vec2<f32>(0.0, 0.0);
    if (hasNormal) {
        normalA = textureSampleLevel(ribbonNormal, ribbonWrap,
            vec2<f32>((u + drift) * tiling, vTiled), 0.0).rg * 2.0 - 1.0;
        // The second layer is overlaid at 1.63x frequency and 0.78x speed: one layer alone reads as a scrolling decal under TAA.
        normalB = textureSampleLevel(ribbonNormal, ribbonWrap,
            vec2<f32>((u - drift) * tiling * 1.63 + 0.37, vTiled * 0.78 - 0.21), 0.0).rg * 2.0 - 1.0;
    }
    let slope = (normalA + normalB) * (0.5 * material2.y) * vec2<f32>(0.5, 1.0);

    // Foam has two sources: the periodic bands carried by the flow, and the two bank lines. Both take a max rather than a sum,
    // otherwise their overlap blows straight to white and the cartoon tiers disappear.
    let edge = 1.0 - abs(lateral);
    let edgeBand = saturate((edge - (1.0 - material3.x)) / max(material3.x, 1e-3));
    let bands = saturate(mask.b * 1.35 - 0.35);
    let foam = saturate(max(bands * material1.w, edgeBand * 0.80));

    // Bank alpha: the geometry reaches exactly the waterline, so the bite is only the A channel's organic wobble plus a
    // narrow taper. A river that ended on a straight line would read as a decal, not as water.
    let cut = edge - material3.y * (0.45 + 0.65 * mask.a);
    let edgeCut = smoothstep(0.0, 0.12, cut);
    let foamCut = smoothstep(-0.05, 0.05, cut);
    let density = 0.72 + 0.28 * saturate(mask.r * 1.6);
    let bodyAlpha = material0.w * density * edgeCut;
    let foamAlpha = foam * 0.85 * foamCut;
    let alpha = saturate(max(bodyAlpha, foamAlpha) * shore);

    // Lighting: the surface normal is mostly up, so the sun term is nearly constant across the band and the interest has to
    // come from the Fresnel rim and the foam. The sky stand-in is the frame's ambient color, since this pass cannot sample the sky.
    let view = normalize(camera.xyz - input.world);
    let normal = normalize(input.normal);
    let tangent = normalize(input.tangent - normal * dot(normal, input.tangent));
    let bitangent = cross(normal, tangent);
    let shadedNormal = normalize(normal + bitangent * slope.x + tangent * slope.y);

    let toSun = -sun.xyz;
    let ndl = saturate(dot(shadedNormal, toSun));
    let ambientLight = ambient.rgb * ambient.w;
    let direct = sunColor.rgb * (sun.w * ndl);

    // Shallow water is pale and slightly desaturated; deeper water keeps the base color. thickness is the screen-space ramp.
    let shallow = mix(material0.rgb, material1.rgb, 0.35) * 1.35;
    let body = mix(shallow, material0.rgb, thickness) * (ambientLight + direct * 0.85);

    let fresnel = pow(1.0 - saturate(dot(shadedNormal, view)), 4.0);
    var color = mix(body, ambientLight * 1.4 + sunColor.rgb * (sun.w * 0.15), fresnel * 0.45);

    let foamLit = material1.rgb * (ambientLight * 1.25 + direct * (0.40 + 0.60 * ndl));
    color = mix(color, foamLit, saturate(foam));

    let halfVector = normalize(view + toSun);
    color = color + sunColor.rgb * (sun.w * pow(saturate(dot(shadedNormal, halfVector)), 64.0) * 0.45);
    color = color
        + (ambientLight + direct) * pow(1.0 - saturate(dot(shadedNormal, view)), 4.0) * 0.06;

    return vec4<f32>(color, alpha);
}
""";
}
