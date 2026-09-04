// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Platforms.Web;

/// <summary>
/// Web-side WebGPU rendering pipeline definitions.
/// Contains the main shader sources and a small set of layout constants used directly by the C# runtime.
/// The shader covers all rendering paths in a single compilation through bit flags in <c>flags.w</c>:
///   - bit 4 (16): GPU instancing
///   - bit 5 (32): skeletal skinning
///   - bit 6 (64): morph targets
/// </summary>
public static class WebGPUPipeline
{
    /// <summary>
    /// Fixed 20-float vertex-input layout:
    ///   pos(3) + uv(2) + normal(3) + tangent(4) + joints(4) + weights(4)
    /// Each float is 4 bytes, for a total stride of 80 bytes.
    /// </summary>
    public const int VertexStrideFloats = 20;
    public const int VertexStrideBytes = VertexStrideFloats * 4;

    /// <summary>
    /// GPU-instancing instance-stream stride (16 floats for world + 4 floats for morphWeights = 80 bytes).
    /// </summary>
    public const int InstanceStrideFloats = 20;
    public const int InstanceStrideBytes = InstanceStrideFloats * 4;

    /// <summary>
    /// Per-frame uniform-buffer size (108 floats = 432 bytes, with hdrParams vec4 added at the end in 1-4 Step B).
    /// Precisely aligned with <see cref="WebGPUUniformLayout"/> and WGSL Uniforms.
    /// </summary>
    public const int UniformBytes = WebGPUUniformLayout.TotalBytes;

    // ── Unified WGSL shader source ──

    /// <summary>
    /// Main rendering-pipeline shader (WGSL).
    /// Includes full PBR lighting, MSDF text rendering, alpha testing, skeletal skinning,
    /// morph targets, and GPU instancing.
    /// On Web, it is passed to <c>device.createShaderModule()</c> through JSInterop.
    /// </summary>
    public const string Mesh3DShader = """
// ── HDR-chain switch (1-4 Step A, mirroring compile-time HDR_CHAIN injection on DX/VK):
// WGSL has no preprocessor, so use a foldable const. C# replaces it with true by string substitution
// before InitializeAsync based on Graphics.HdrSceneColor.
const HDR_CHAIN : bool = false;

// ── 2-3 Contract Clause 3 (the only compile-time switch for velocity, mirroring VELOCITY_OUTPUT on DX/VK):
// when true, vs_main computes prevClip. When disabled, constant folding removes the entire block with zero runtime cost.
// The FS side uses two entry points instead of conditional output because WGSL has no preprocessor and
// @location declarations cannot be conditional:
// fs_main = single target, fs_main_mrt = color + velocity. The JS side builds the extra MRT pipeline variant only when enabled.
// Mutually exclusive with SHADOW_PASS, since shadow uses a vertex-only pipeline with no fragment stage.
const VELOCITY_OUTPUT : bool = false;

// ── 2-4 Clause 10 (the only compile-time switch on the DDGI consumer side, mirroring DDGI_ENABLED on DX/VK/Metal):
// when true, the fragment shader replaces ambient diffuse with probe-atlas sampling.
// When disabled, constant folding removes the path with zero runtime cost.
// C# replaces it with true by string substitution before InitializeAsync when RenderQuality.GlobalIllumination==Ddgi.
const DDGI_ENABLED : bool = false;

struct VertexInput {
    @location(0) position: vec3f,
    @location(1) uv: vec2f,
    @location(2) normal: vec3f,
    @location(3) tangent: vec4f,
    @location(4) joints: vec4f,
    @location(5) weights: vec4f,
    @location(6) instanceWorld0: vec4f,
    @location(7) instanceWorld1: vec4f,
    @location(8) instanceWorld2: vec4f,
    @location(9) instanceWorld3: vec4f,
    @location(10) instanceMorphWeights: vec4f,
};

struct VertexOutput {
    @builtin(position) position: vec4f,
    @location(0) uv: vec2f,
    @location(1) normal: vec3f,
    @location(2) worldPos: vec3f,
    @location(3) tangent: vec4f,
    @location(4) instanceColor: vec4f,
    @location(5) viewDepth: f32,  // 1-5: view-space depth (used for CSM cascade selection, equivalent to Metal vViewDepth)
    @location(6) prevClip: vec4f, // 2-3: previous-frame non-jittered clip-space position (used by FS to compute velocity; w<=0 = no history)
};

struct Uniforms {
    world: mat4x4f,
    view: mat4x4f,
    projection: mat4x4f,
    // ── 2-3 Contract Clause 6: history-data region (reuses the 9 retired vec4 slots from 1-2; see WebGPUUniformLayout.PrevWorld).
    // Follows the same transpose convention as world/view/projection (uploaded row-major, read column-major -> pre-multiply).
    // All zero means not written, and vs_main gracefully falls back through the sentinel path (Clause 9).
    prevWorld: mat4x4f,          // float 48: previous-frame world matrix ([3][3]==C# M44==0 -> fall back to current-frame world)
    prevViewProjection: mat4x4f, // float 64: previous-frame non-jittered View×Projection (all-zero block -> prevClip stays 0)
    prevMorphWeights: vec4f,     // float 80: previous-frame morph weights (non-instanced path; driven by hasPrevMorph bit)
    baseColor: vec4f,
    emissive: vec4f,
    material: vec4f,    // x=metallic, y=roughness, z=alpha, w=alphaCutoff
    // x = sentinel bitfield for prev data in 2-3 Step C
    // (bit0=hasPrevBones, bit1=hasPrevInstanceWorld, bit2=hasPrevMorph;
    // aligned with the three uints in native MaterialParams. On this backend, the 432B UBO is already full,
    // so the bits are packed into the retired lightCount slot from 1-2.)
    flags: vec4<i32>,   // y=renderMode, z=alphaMode, w=textureFlags
    morphWeights: vec4f, // Morph weights for the non-instanced path (aligned with native MaterialParams.MorphWeights)
    hdrParams: vec4f,   // Reserved retired slot from 1-2 (exposure now reads uLights.params0.y, Contract 7); reused in Phase 4 as the per-draw outline color carrier for OutlineMask
};

// ── 1-2 lighting system (Contract 8): dedicated shared lighting UBO uploaded once per frame (updateSceneLights),
// byte-aligned with C# SceneLightParams (1152B). A real array in uniform address space can be indexed at runtime,
// replacing the old switch(i)-expansion hack that existed because field-by-field vec4 data could not be dynamically addressed.
struct GpuLight {
    posRange: vec4f,        // xyz=world position, w=attenuation radius range (<=0 falls back to pure 1/d^2)
    colorIntensity: vec4f,  // xyz=linear color, w=intensity
    dirType: vec4f,         // xyz=lighting direction (used by spot/directional), w=type (0=point, 1=spot, 2=directional)
    spotParams: vec4f,      // x=cosInner, y=cosOuter (precomputed on CPU), zw=reserved
};

struct SceneLights {
    cameraPos: vec4f,
    ambientParams: vec4f,   // xyz=ambient-light color, w=intensity (replaces the old hardcoded 0.5)
    // x=lightCount, y=hdrExposure (C# SceneLightParams.Params0.Y, injected every frame by UpdateCamera3D),
    // z=directionalIndex (index of the directional light in lights that casts CSM shadows, -1=none),
    // w=spotShadowIndex (index of the spotlight that casts the 2D shadow map, -1=none)
    params0: vec4f,
    // Directional lights are already included in this array (dirType.w=2), so there is no separate sun field.
    // The unified lighting loop below dispatches by type.
    lights: array<GpuLight, 8>,
    // ── 1-5 shadows (1152B contract across all backends): matrices are uploaded row-major as-is, then read column-major as M^T,
    // so pre-multiplying M*v is equivalent to CPU-side pos·M.
    cascadeViewProj: array<mat4x4f, 4>,  // offset 560: CSM cascade light-space VP (slot 0..2)
    spotShadowViewProj: mat4x4f,         // offset 816: spotlight light-space VP (slot 3)
    cascadeSplits: vec4f,                // offset 880: cascade view-depth split distances
    shadowParams0: vec4f,                // offset 896: x=sunEnabled, y=cascadeCount, z=1/atlasSize, w=reserved
    shadowParams1: vec4f,                // offset 912: x=spotEnabled, y=shadowStrength, zw=reserved
    // ── 2-3 Contract Clause 6 (offset 928, expanding to 1152B): JS-side SCENE_LIGHT_BYTES must stay synchronized
    velocityParams: vec4f,               // xy=current-frame subpixel jitter (NDC, used by FS for de-jittering), z=1/screenWidth, w=1/screenHeight
    // 1-7 Contract Clause 4: x=specular intensity multiplier, y=ambient diffuse intensity multiplier,
    // z=diffuse switch (>0.5 uses irradianceSH9, otherwise uses constant ambient from ambientParams; never add both),
    // w=specular switch (>0.5 enables the uEnvCube LOD0 specular term). All zero = full fallback to 1-2 constant ambient light.
    envParams: vec4f,                    // offset 944
    // 1-7 Contract Clause 7: SH9 ambient irradiance (xyz=RGB, w reserved). The CPU has already pre-multiplied
    // the convolution coefficients A_l, so only the 9-term linear combination is evaluated here.
    // Effective only when envParams.z > 0.5. The vec4f array in uniform address space has stride 16,
    // byte-aligned with C# SceneLightParams.IrradianceSH9 ([InlineArray(9)] Sh9Array).
    irradianceSH9: array<vec4f, 9>,      // offset 960
    // 2-4 DDGI Clause 10 (starting at offset 1104, expanding to 1152B): JS-side SCENE_LIGHT_BYTES must stay synchronized.
    // giParams0=probeGridMin.xyz/spacing，giParams1=gridXYZ(as float)/GiIntensity，
    // giParams2=normalBias/chebyshev/atlasReady/_。
    giParams0: vec4f,                    // offset 1104
    giParams1: vec4f,                    // offset 1120
    giParams2: vec4f,                    // offset 1136, end of 1152B block
    // 2-5 Step B (starting at offset 1152): analytical sun/moon disks + starfield
    // (skyParams0.xyz=sun direction, w=sun angular radius; all zero = full early-out for the StaticCube tier).
    skyParams0: vec4f,                   // offset 1152
    skyParams1: vec4f,                   // offset 1168
    skyParams2: vec4f,                   // offset 1184
    skyParams3: vec4f,                   // offset 1200
    skyParams4: vec4f,                   // offset 1216
    // 2-5 Step C (starting at offset 1232): procedural clouds.
    // cloudLayerA=layer height in km / density / coverage / thickness in km,
    // cloudLayerB=wind offset xy / noise-uv scale / erosion strength,
    // cloudParams0=base color rgb / layer count w,
    // cloudParams1=cloud-shadow intensity x / silver-lining g / dark-side brightness / forward-scattering intensity.
    cloudLayerA: array<vec4f, 3>,        // offset 1232
    cloudLayerB: array<vec4f, 3>,        // offset 1280
    cloudParams0: vec4f,                 // offset 1328 (w=layer count = the only gate for cloud consumption)
    cloudParams1: vec4f,                 // offset 1344
    // 2-5 Step E (starting at offset 1360): consumption parameters for the aerial-perspective 3D LUT.
    // x=max distance in km (>0 enables AP; the only gate), y=Intensity (0 = identity blend).
    // JS-side SCENE_LIGHT_BYTES=1376 is already synchronized.
    apParams0: vec4f,                    // offset 1360, end of 1376B block
};

@group(0) @binding(0) var<uniform> u: Uniforms;
@group(0) @binding(1) var uSampler: sampler;
@group(0) @binding(2) var uTexture: texture_2d<f32>;
@group(0) @binding(3) var uNormalTexture: texture_2d<f32>;
@group(0) @binding(4) var uMetallicRoughnessTexture: texture_2d<f32>;
@group(0) @binding(5) var uAoTexture: texture_2d<f32>;
@group(0) @binding(6) var uEmissiveTexture: texture_2d<f32>;
@group(0) @binding(7) var<storage, read> uBones: array<mat4x4f>;
@group(0) @binding(8) var<storage, read> uMorphMeta: array<u32>;
@group(0) @binding(9) var<storage, read> uMorphValues: array<f32>;
@group(0) @binding(10) var<uniform> uLights: SceneLights;
// 1-5: shadow atlas (D32, four quadrants) + comparison sampler, referenced statically only by fs_main.
// The shadow-pass vertex-only pipeline uses a separate layout (0/7/8/9) and does not include this binding group.
@group(0) @binding(11) var uShadowAtlas: texture_depth_2d;
@group(0) @binding(12) var uShadowSampler: sampler_comparison;
// ── 2-3 Step C (structural fallback for Contract Clause 8(b)(c)): previous deformation data ──
// 13 = previous bone palette (same layout and capacity as binding 7, rolled forward each frame by the JS-side shadow copy);
// 14 = previous instance byte stream (5 vec4 per instance: the first 4 = previous-frame world in row-major form,
// the 5th = previous-frame morph weights, structurally matching the other half of the double-buffered instance stream on Metal, Clause 8(d)).
// Both are referenced statically only by vs_main, but VELOCITY_OUTPUT is a globally injected const in a single shader module,
// and WGSL static-reference analysis does not constant-fold it. Therefore the shadow-pass vertex-only layout must still declare 13/14,
// or createRenderPipeline reports layout incompatibility. JS-side _shadowBindGroupLayout has been expanded accordingly and binds defaults.
@group(0) @binding(13) var<storage, read> uPrevBones: array<mat4x4f>;
@group(0) @binding(14) var<storage, read> uPrevInstanceData: array<vec4f>;
// ── 1-7: environment radiance cube (six-layer texture + viewDimension:'cube', single mip).
// Referenced statically only by fs_main, so the shadow-pass vertex-only layout does not need to grow
// unlike bindings 13/14, which are referenced from vs_main.
// Sampling reuses binding 1 uSampler (Linear+ClampToEdge), so no extra sampler slot is needed.
// Always use textureSampleLevel(..., 0.0): this avoids WGSL uniformity issues
// (textureSample's implicit derivatives are legal only in uniform control flow, while this path is wrapped in conditionals inside shade()),
// and it also matches the semantics of a single-mip texture that always samples LOD0.
// When there is no environment texture, JS-side _getEnvCubeView() falls back to a 1x1 all-black cube, so sampling is always valid here.
// The actual switch is carried by envParams.w.
@group(0) @binding(15) var uEnvCube: texture_cube<f32>;
// 2-4 Clause 10: DDGI irradiance probe atlas (binding 16, rgba16float). Like uEnvCube, it is referenced
// statically only by fs_main, so the shadow-pass vertex-only layout does not need to grow.
// Sampling reuses binding 1 uSampler (Linear+Clamp).
// When not ready, JS falls back to a 1x1 White texture, so sampling is always valid; actual use is gated by DDGI_ENABLED + giParams.
@group(0) @binding(16) var uDdgiAtlas: texture_2d<f32>;

// 2-4 Step 3: DDGI depth-moment atlas (binding 17, rg16float with a core fallback to rgba16float, .x=mean/.y=mean^2).
// Same as uDdgiAtlas: referenced statically only by fs_main, sampling reuses uSampler, and JS falls back to a 1x1 White texture when not ready.
// The Chebyshev variance test is gated at runtime by giParams2.y and does not sample when disabled.
@group(0) @binding(17) var uDdgiDepth: texture_2d<f32>;
// 2-5 Step C: precomputed cloud noise (binding 18, rgba8unorm: R=low-frequency silhouette FBM,
// G=Worley fluff, B=high-frequency erosion, A=very-low-frequency coverage modulation).
// Always declared, same as uDdgiAtlas. When not ready, JS falls back to a 1x1 White texture.
// Actual sampling is gated at runtime by cloudParams0.w (layer count), because a white fallback cannot serve as real noise
// and would drive density to full scale.
// Sampler uWrapSampler (binding 20, Repeat): the noise is tileable with a fixed period, and wind offsets can push UV outside [0,1].
// Clamp would stretch the outermost texel into a fixed horizontal stripe across the sky, matching the same rationale as DX s2 wrapSampler.
@group(0) @binding(18) var uCloudNoise: texture_2d<f32>;
// 2-5 Step E: aerial-perspective froxel volume (binding 19, 32^3 rgba16float:
// rgb=accumulated in-scattered radiance from the camera to that distance in linear HDR, a=accumulated opacity).
// Always declared; when not ready, JS falls back to a 1x1x1 all-zero texture.
// Since a stores opacity rather than transmittance, all-zero is exactly the additive identity for the blend equation.
// apParams0.x gating only avoids an unnecessary sample.
// Three-axis Clamp + trilinear filtering reuse binding 1 uSampler, so no extra slot is needed.
@group(0) @binding(19) var uAerialLut: texture_3d<f32>;
@group(0) @binding(20) var uWrapSampler: sampler;

fn getMorphWeight(weights: vec4f, index: u32) -> f32 {
    switch (index) {
        case 0u: { return weights.x; }
        case 1u: { return weights.y; }
        case 2u: { return weights.z; }
        case 3u: { return weights.w; }
        default: { return 0.0; }
    }
}

// ── 2-3 Step C: rebuild previous local position (restart from rest pose, then morph -> skin, aligned with the three-step fallback on VK).
// Entered only by the velocity variant (the call site is wrapped in if (VELOCITY_OUTPUT), so constant folding removes the whole path when disabled).
// Per-branch sentinels: if the corresponding hasPrev* bit is not set, reuse the current-frame source data
// so that branch contributes no velocity. Do not fall back to zero weights or an identity matrix,
// because that would fabricate large false motion. With all three bits zero on the first frame, behavior matches pre-Step-C logic.
// Position only: normal/tangent do not participate in velocity.
fn computePrevLocalPosition(input: VertexInput, instanceIndex: u32, vertexIndex: u32) -> vec4f {
    var localPosition = vec4f(input.position, 1.0);
    let prevFlags = u.flags.x;
    let instanced = (u.flags.w & 16) != 0;

    // 1) Morph: use u.prevMorphWeights for the non-instanced path, and the 5th vec4 from the previous instance stream for the instanced path.
    if ((u.flags.w & 64) != 0) {
        let morphTargetCount = min(uMorphMeta[0], 4u);
        let morphVertexCount = uMorphMeta[1];
        if (morphTargetCount > 0u && morphVertexCount > 0u) {
            var prevWeights = select(u.morphWeights, input.instanceMorphWeights, instanced);
            if ((prevFlags & 4) != 0) {
                if (instanced) {
                    prevWeights = uPrevInstanceData[instanceIndex * 5u + 4u];
                } else {
                    prevWeights = u.prevMorphWeights;
                }
            }
            for (var t: u32 = 0u; t < morphTargetCount; t = t + 1u) {
                let weight = getMorphWeight(prevWeights, t);
                if (abs(weight) > 0.000001) {
                    let off = (t * morphVertexCount + vertexIndex) * 9u;
                    let positionDelta = vec3f(uMorphValues[off], uMorphValues[off + 1u], uMorphValues[off + 2u]);
                    localPosition = vec4f(localPosition.xyz + positionDelta * weight, localPosition.w);
                }
            }
        }
    }

    // 2) Skeletal deformation: when hasPrevBones is set, read uPrevBones per joint; otherwise fall back to the current-frame uBones.
    //    The bone base index follows the same rule as the current frame (100 matrices per instance for instancing),
    //    and the previous shadow copy shares the same layout as binding 7.
    if ((u.flags.w & 32) != 0) {
        let totalWeight = input.weights.x + input.weights.y + input.weights.z + input.weights.w;
        if (totalWeight > 0.0) {
            var skinnedPosition = vec4f(0.0);
            let boneBaseIndex: u32 = select(0u, instanceIndex * 100u, instanced);
            let usePrevBones = (prevFlags & 1) != 0;
            for (var i: i32 = 0; i < 4; i = i + 1) {
                let weight = input.weights[i];
                if (weight > 0.0) {
                    let boneIndex = boneBaseIndex + u32(input.joints[i]);
                    var boneMatrix = uBones[boneIndex];
                    if (usePrevBones) {
                        boneMatrix = uPrevBones[boneIndex];
                    }
                    skinnedPosition += (localPosition * boneMatrix) * weight;
                }
            }
            localPosition = skinnedPosition;
        }
    }

    return localPosition;
}

@vertex
fn vs_main(input: VertexInput, @builtin(instance_index) instanceIndex: u32, @builtin(vertex_index) vertexIndex: u32) -> VertexOutput {
    var output: VertexOutput;

    var localPosition = vec4f(input.position, 1.0);
    var localNormal = input.normal;
    var localTangent = input.tangent;
    if ((u.flags.w & 64) != 0) {
        let morphTargetCount = min(uMorphMeta[0], 4u);
        let morphVertexCount = uMorphMeta[1];
        if (morphTargetCount > 0u && morphVertexCount > 0u) {
            let morphWeights = select(u.morphWeights, input.instanceMorphWeights, (u.flags.w & 16) != 0);
            for (var t: u32 = 0u; t < morphTargetCount; t = t + 1u) {
                let weight = getMorphWeight(morphWeights, t);
                if (abs(weight) > 0.000001) {
                    let off = (t * morphVertexCount + vertexIndex) * 9u;
                    let positionDelta = vec3f(uMorphValues[off], uMorphValues[off + 1u], uMorphValues[off + 2u]);
                    let normalDelta = vec3f(uMorphValues[off + 3u], uMorphValues[off + 4u], uMorphValues[off + 5u]);
                    let tangentDelta = vec3f(uMorphValues[off + 6u], uMorphValues[off + 7u], uMorphValues[off + 8u]);
                    localPosition = vec4f(localPosition.xyz + positionDelta * weight, localPosition.w);
                    localNormal = localNormal + normalDelta * weight;
                    localTangent = vec4f(localTangent.xyz + tangentDelta * weight, localTangent.w);
                }
            }
            localNormal = normalize(localNormal);
            localTangent = vec4f(normalize(localTangent.xyz), localTangent.w);
        }
    }

    // Skeletal skinning (bit 5=32: vertex data contains bone weights)
    if ((u.flags.w & 32) != 0) {
        let totalWeight = input.weights.x + input.weights.y + input.weights.z + input.weights.w;
        if (totalWeight > 0.0) {
            var skinnedPosition = vec4f(0.0);
            var skinnedNormal = vec3f(0.0);
            var skinnedTangent = vec3f(0.0);
            let boneBaseIndex: u32 = select(0u, instanceIndex * 100u, (u.flags.w & 16) != 0);
            for (var i: i32 = 0; i < 4; i = i + 1) {
                let weight = input.weights[i];
                if (weight > 0.0) {
                    let jointIndex = u32(input.joints[i]);
                    let boneIndex = boneBaseIndex + jointIndex;
                    let boneMatrix = uBones[boneIndex];
                    skinnedPosition += (localPosition * boneMatrix) * weight;
                    let boneMat = mat3x3f(
                        boneMatrix[0].xyz,
                        boneMatrix[1].xyz,
                        boneMatrix[2].xyz);
                    skinnedNormal += (localNormal * boneMat) * weight;
                    skinnedTangent += (localTangent.xyz * boneMat) * weight;
                }
            }
            localPosition = skinnedPosition;
            localNormal = normalize(skinnedNormal);
            localTangent = vec4f(normalize(skinnedTangent), input.tangent.w);
        }
    }

    // World matrix (bit 4=16: GPU-instancing path)
    var worldMatrix: mat4x4f;
    if ((u.flags.w & 16) != 0) {
        worldMatrix = mat4x4f(input.instanceWorld0, input.instanceWorld1, input.instanceWorld2, input.instanceWorld3);
    } else {
        worldMatrix = u.world;
    }
    let worldPos = worldMatrix * localPosition;
    output.position = u.projection * u.view * worldPos;
    output.viewDepth = (u.view * worldPos).z;  // 1-5: V^T·v is the view-space coordinate, equivalent to Metal viewPos.z

    // 2-3 Contract Clauses 6/9: rebuild prevClip (constant folding removes the whole block when VELOCITY_OUTPUT is disabled).
    // Outer sentinel: WGSL reads row-major bytes as column-major, so prevViewProjection[3] corresponds to row 4 in C#.
    // That row is never zero for a perspective or orthographic VP matrix, so all zero means no history.
    // In that case prevClip stays 0, and the FS side outputs zero velocity when w<=0.
    // This naturally covers all 2D/UI/text paths, because those call sites never write prev slots.
    // 2-3 Step C: prev world has three sources. Instanced paths first prefer per-instance world from the previous instance stream
    // (filling Clause 8(b), where u.prevWorld stays all zero and is ignored). Otherwise use the prevWorld sentinel,
    // and finally fall back to the current-frame worldMatrix, which yields only camera-motion velocity.
    // Local position is rebuilt from rest pose by computePrevLocalPosition
    // (previous morph + previous bones, filling Clause 8(c)) instead of reusing current-frame localPosition.
    if (VELOCITY_OUTPUT) {
        if (any(u.prevViewProjection[3] != vec4f(0.0))) {
            var prevWorldMatrix = worldMatrix;
            if ((u.flags.x & 2) != 0) {
                let pb = instanceIndex * 5u;
                prevWorldMatrix = mat4x4f(uPrevInstanceData[pb], uPrevInstanceData[pb + 1u],
                                          uPrevInstanceData[pb + 2u], uPrevInstanceData[pb + 3u]);
            } else if (u.prevWorld[3][3] != 0.0) {
                prevWorldMatrix = u.prevWorld;
            }
            let prevLocalPosition = computePrevLocalPosition(input, instanceIndex, vertexIndex);
            output.prevClip = u.prevViewProjection * (prevWorldMatrix * prevLocalPosition);
        }
    }

    output.uv = input.uv;
    output.normal = normalize((worldMatrix * vec4f(localNormal, 0.0)).xyz);
    output.worldPos = worldPos.xyz;
    output.tangent = vec4f(normalize((worldMatrix * vec4f(localTangent.xyz, 0.0)).xyz), input.tangent.w);

    // Text GPU instancing (renderMode==2 and instancing bit set):
    // reuse uMorphValues as glyph data (12 floats per glyph: uvRect(4)+color(4)+metrics(4)),
    // aligned with the reuse strategy of DX t5 / VK binding 10.
    // The text path never sets the morph bit (64), so the two modes are mutually exclusive.
    var instanceColor = u.baseColor;
    if (u.flags.y == 2 && (u.flags.w & 16) != 0) {
        let gBase = instanceIndex * 12u;
        let uvRect = vec4f(uMorphValues[gBase], uMorphValues[gBase + 1u],
                           uMorphValues[gBase + 2u], uMorphValues[gBase + 3u]);
        output.uv = uvRect.xy + input.uv * uvRect.zw;
        // metrics.w > 0.5: per-glyph color override (aligned with DX LoadTextGlyph semantics)
        if (uMorphValues[gBase + 11u] > 0.5) {
            instanceColor = vec4f(uMorphValues[gBase + 4u], uMorphValues[gBase + 5u],
                                  uMorphValues[gBase + 6u], uMorphValues[gBase + 7u]);
        }
    }
    output.instanceColor = instanceColor;
    return output;
}

const PI: f32 = 3.14159265359;

fn DistributionGGX(N: vec3f, H: vec3f, roughness: f32) -> f32 {
    let a = roughness * roughness;
    let a2 = a * a;
    let NdotH = max(dot(N, H), 0.0);
    let NdotH2 = NdotH * NdotH;
    let denomInner = NdotH2 * (a2 - 1.0) + 1.0;
    let denom = PI * denomInner * denomInner;
    return a2 / max(denom, 0.0001);
}

fn GeometrySchlickGGX(NdotV: f32, roughness: f32) -> f32 {
    let r = roughness + 1.0;
    let k = (r * r) / 8.0;
    return NdotV / (NdotV * (1.0 - k) + k);
}

fn GeometrySmith(N: vec3f, V: vec3f, L: vec3f, roughness: f32) -> f32 {
    let NdotV = max(dot(N, V), 0.0);
    let NdotL = max(dot(N, L), 0.0);
    let ggx1 = GeometrySchlickGGX(NdotV, roughness);
    let ggx2 = GeometrySchlickGGX(NdotL, roughness);
    return ggx1 * ggx2;
}

fn FresnelSchlick(cosTheta: f32, F0: vec3f) -> vec3f {
    let ct = clamp(cosTheta, 0.0, 1.0);
    return F0 + (vec3f(1.0) - F0) * pow(1.0 - ct, 5.0);
}

// Cook-Torrance direct-light contribution for a single light source
// (1-2 contract: the formula is textually identical across all four backends; radiance already contains intensity * attenuation * cone factor)
fn EvaluatePbrLight(N: vec3f, V: vec3f, L: vec3f, albedo: vec3f, metallic: f32, roughness: f32, F0: vec3f, radiance: vec3f) -> vec3f {
    let H = normalize(V + L);

    let NDF = DistributionGGX(N, H, roughness);
    let G = GeometrySmith(N, V, L, roughness);
    let F = FresnelSchlick(max(dot(H, V), 0.0), F0);

    let numerator = NDF * G * F;
    let denominator = 4.0 * max(dot(N, V), 0.0) * max(dot(N, L), 0.0);
    let specular = numerator / max(denominator, 0.0001);

    let kS = F;
    let kD = (vec3f(1.0) - kS) * (1.0 - metallic);

    let NdotL = max(dot(N, L), 0.0);
    return (kD * albedo / PI + specular) * radiance * NdotL;
}

// ── 2-5 Step B: hash-based lottery for the procedural starfield
// (textually mirrors the function of the same name on DX/VK, except WGSL returns a struct instead of using out parameters) ──
fn StarHash(v: vec3<u32>) -> u32 {
    // WGSL requires explicit parentheses when mixing bitwise operators (^ & |) with arithmetic operators; HLSL does not.
    var h = (v.x * 1597334677u) ^ (v.y * 3812015801u) ^ (v.z * 2654435761u);
    h ^= h >> 15u; h *= 2246822519u;
    h ^= h >> 13u; h *= 3266489917u;
    h ^= h >> 16u;
    return h;
}

// Map a 16-bit slice of the hash to [0,1). Different shifts use non-overlapping bit ranges, so the lottery values stay independent.
// Simply multiplying h by a constant and taking the low bits does not work: multiplication mixes low bits poorly,
// which makes jittered x/y visibly correlated and form diagonal streaks.
fn StarSlice(h: u32, shift: u32) -> f32 {
    return f32((h >> shift) & 0xFFFFu) * (1.0 / 65536.0);
}

// Convert a direction into a cube-face index + face-local uv in [0,1]^2.
// A cube is used instead of a latitude/longitude grid because the latter degenerates into thin strips near the poles,
// making stars align into radial artifacts there. WGSL has no out parameters, so return a struct instead.
struct StarFaceResult {
    face: u32,
    uv: vec2f,
};

fn StarFaceUv(d: vec3f) -> StarFaceResult {
    var result: StarFaceResult;
    let a = abs(d);
    if (a.x >= a.y && a.x >= a.z) {
        result.uv = vec2f(d.z, d.y) / a.x;
        result.face = select(1u, 0u, d.x > 0.0);
    } else if (a.y >= a.z) {
        result.uv = vec2f(d.x, d.z) / a.y;
        result.face = select(3u, 2u, d.y > 0.0);
    } else {
        result.uv = vec2f(d.x, d.y) / a.z;
        result.face = select(5u, 4u, d.z > 0.0);
    }
    result.uv = clamp(result.uv * 0.5 + 0.5, vec2f(0.0), vec2f(1.0));   // Clamp to [0,1]: floating-point overflow at t=±1 would otherwise make the floor below land in cell -1
    return result;
}

// Additional radiance from celestial disks + starfield in linear HDR.
// This is added to the Sky-View LUT instead of replacing it:
// the LUT contains in-scattering along the view ray, while this function provides direct celestial-body / stellar radiance reaching the observer through the atmosphere,
// so the two are physically additive.
// pxAng = per-pixel angular size in radians, provided by the caller. Both disk edges and star radii derive from it,
// so features stay about one pixel wide without hardcoded pixel sizes and without blurring or aliasing across resolution/FOV changes.
fn SkyCelestialRadiance(dir: vec3f, pxAng: f32) -> vec3f {
    var L = vec3f(0.0);

    // ── Sun disk: the test is dot(dir, sunDir) > cos(angular radius)
    // (the second consumer of Atmosphere.SunAngularRadiusDeg) ──
    // AA width conversion: the slope of cos at the disk edge is -sin(angular radius),
    // so the corresponding delta in cos for one pixel is pxAng * sin.
    let sunSin = sqrt(max(1.0 - uLights.skyParams0.w * uLights.skyParams0.w, 1e-12));
    let aaSun = pxAng * sunSin;
    let sunMask = smoothstep(uLights.skyParams0.w - aaSun, uLights.skyParams0.w + aaSun, dot(dir, uLights.skyParams0.xyz));
    L += uLights.skyParams1.xyz * sunMask;

    // ── Moon disk + phase ──
    let cosMoon = dot(dir, uLights.skyParams2.xyz);
    let moonSin = sqrt(max(1.0 - uLights.skyParams2.w * uLights.skyParams2.w, 1e-12));
    let aaMoon = pxAng * moonSin;
    let moonMask = smoothstep(uLights.skyParams2.w - aaMoon, uLights.skyParams2.w + aaMoon, cosMoon);
    if (moonMask > 0.0) {
        // Spherical normal for a point inside the disk, which is the zero-parameter source of the moon phase:
        // normalize the tangential offset of the view ray relative to the moon center by the disk radius to get s in [0,1]
        // (0 = disk center, 1 = disk edge). Then normal = tangent*s - moonCenterDir*sqrt(1-s^2).
        // At the disk center, the normal points straight at the observer (= -moonCenterDir), and at the edge it becomes perpendicular to the view ray.
        // This is exactly the geometry of an orthographic sphere projection, with no extra parameters required.
        let tangent = dir - uLights.skyParams2.xyz * cosMoon;
        let tanLen = length(tangent);
        let s = clamp(tanLen / moonSin, 0.0, 1.0);
        let tDir = select(vec3f(1.0, 0.0, 0.0), tangent / tanLen, tanLen > 1e-8);
        let nrm = tDir * s - uLights.skyParams2.xyz * sqrt(max(1.0 - s * s, 0.0));

        // The moon surface is lit by the sun, so the incident cosine directly becomes the phase,
        // evolving automatically with sunDir/moonDir and needing no phase parameter or artist curve.
        // nrm is the negative outward normal pointing toward the observer, while sunDir is the propagation direction,
        // so the two negatives cancel and a positive dot product is correct here.
        // The square root is a cheap approximation of strong lunar back-scattering near opposition,
        // and the lower bound 0.015 models earthshine.
        let lit = max(sqrt(clamp(dot(nrm, uLights.skyParams0.xyz), 0.0, 1.0)), 0.015);
        L += uLights.skyParams3.xyz * (moonMask * lit);
    }

    // ── Procedural starfield (skyParams1.w already contains twilight visibility derived from StarVisibilityTwilightDeg, and stays 0 during daytime) ──
    if (uLights.skyParams1.w > 0.0) {
        // Reverse-rotate into the starfield's fixed frame before doing the lottery:
        // the star map is pinned to that frame, so StarRotation produces a coherent sidereal sweep
        // instead of a full-sky flicker from re-rolling every frame.
        // The rotation axis is skyParams4.xyz, the celestial-pole axis, rather than world +Y,
        // because rotating around the pole is what produces true east-rise / west-set motion and circumpolar stars.
        // This is Rodrigues inverse rotation (angle = -theta, so the cross term is negated).
        // The CPU already normalizes the axis, but a final fallback is still applied: if the axis is zero, fall back to +Y.
        let axis = select(vec3f(0.0, 1.0, 0.0), normalize(uLights.skyParams4.xyz),
                          dot(uLights.skyParams4.xyz, uLights.skyParams4.xyz) > 1e-8);
        let ca = cos(uLights.skyParams3.w);
        let sa = sin(uLights.skyParams3.w);
        let sd = dir * ca - cross(axis, dir) * sa + axis * (dot(axis, dir) * (1.0 - ca));

        let sf = StarFaceUv(sd);

        let gridN = 96.0;        // 6×96² ≈ 55k cells
        let starDensity = 0.1;   // about 5.5k stars, close to the roughly 6k naked-eye stars across the full sky
        let g = sf.uv * gridN;
        let ci = floor(g);
        let cf = g - ci;

        let h = StarHash(vec3<u32>(u32(ci.x), u32(ci.y), sf.face));
        if (StarSlice(h, 0u) < starDensity) {
            let hj = StarHash(vec3<u32>(h, 0x9E3779B9u, 1u));
            let hm = StarHash(vec3<u32>(h, 0x85EBCA6Bu, 2u));

            // Jitter the star position inside the cell with a 0.15 margin so the star never crosses a cell boundary.
            // That avoids adjacent cells each drawing half a star, which would reveal the grid.
            let pos = vec2f(0.15 + 0.7 * StarSlice(hj, 0u), 0.15 + 0.7 * StarSlice(hj, 16u));

            // Angular size per cell, from a fully analytic expression. It stays continuous everywhere,
            // so cube edges remain seam-free. Using fwidth(uv) here would explode into bright lines along cube edges.
            // The face-local tangential coordinate is t=uv*2-1 with tan(theta)=t,
            // so dtheta/dt≈1/(1+|t|²), and one cell spans 2/gridN units in t.
            let t = sf.uv * 2.0 - 1.0;
            let radPerCell = (2.0 / gridN) / (1.0 + dot(t, t));
            let distRad = length(cf - pos) * radPerCell;
            let star = 1.0 - smoothstep(pxAng * 0.5, pxAng * 1.8, distRad);

            // Magnitude power law: dim stars far outnumber bright stars.
            // Cubing the uniform random value makes the brightest tenth carry most of the luminous flux.
            let mag = StarSlice(hm, 0u);
            let weight = mag * mag * mag;

            // Color-temperature lottery: warm (K/M) to cool (O/B). The range is deliberately subtle
            // because real stars have very low color saturation.
            let tint = mix(vec3f(1.0, 0.92, 0.82), vec3f(0.82, 0.9, 1.0), StarSlice(hm, 16u));

            // Fade out close to the horizon (about 3 degrees), where the view is already occupied by ground geometry
            // and horizon glow. Drawing stars there would only make them intersect the ground.
            L += uLights.skyParams1.w * weight * star * tint * clamp(dir.y * 20.0, 0.0, 1.0);
        }
    }

    return L;
}

// 2-5 Step C: density of a single cloud layer at a given world-space XZ position in kilometers, in [0,1].
// Coverage remapping and high-frequency erosion are already included.
// Textually mirrors DX HLSL CloudDensityAt (saturate->clamp, SampleLevel->textureSampleLevel(..., 0.0)).
fn cloudDensityAt(posKm: vec2f, layer: i32) -> f32 {
    let uv = (posKm + uLights.cloudLayerB[layer].xy) * uLights.cloudLayerB[layer].z;
    let n = textureSampleLevel(uCloudNoise, uWrapSampler, uv, 0.0);
    let shape = n.r * mix(1.0, n.a, 0.7);
    let coverage = uLights.cloudLayerA[layer].z;
    let d = clamp((shape - (1.0 - coverage)) / max(coverage, 1e-3), 0.0, 1.0);
    let erode = uLights.cloudLayerB[layer].w * (0.5 * n.g + 0.5 * n.b);
    return clamp(d * clamp(1.0 - erode, 0.0, 1.0), 0.0, 1.0);
}

// 2-5 Step C: step along the light path, accumulate cloud optical thickness,
// and solve cloud-shadow transmittance (1 = no cloud shadow, 0 = fully occluded).
// Gating matches DX: sample only when layerCount>0, cloud-shadow intensity>0, and the light is above the horizon;
// otherwise return 1 immediately with zero cost.
fn computeCloudShadow(worldPos: vec3f, toLight: vec3f) -> f32 {
    var result = 1.0;
    let count = i32(uLights.cloudParams0.w);
    if (count > 0 && uLights.cloudParams1.x > 0.0 && toLight.y > 0.0) {
        let originKm = worldPos.xz * 0.001;
        let invY = 1.0 / max(toLight.y, 0.05);
        var tau = 0.0;
        for (var i: i32 = 0; i < count; i = i + 1) {
            let hKm = max(uLights.cloudLayerA[i].x - worldPos.y * 0.001, 0.0);
            let posKm = originKm + toLight.xz * (hKm * invY);
            tau = tau + cloudDensityAt(posKm, i) * uLights.cloudLayerA[i].w * uLights.cloudLayerA[i].y * invY;
        }
        result = 1.0 - uLights.cloudParams1.x * clamp(1.0 - exp(-tau), 0.0, 1.0);
    }
    return result;
}

// ── 2-5 Step C: procedural clouds (precomputed noise + multi-layer parallax composition) ──
// Like the analytical celestial disks, this is evaluated per pixel and does not go through the Sky-View LUT.
// Each LUT texel covers about 1.4 degrees, which spreads to roughly 45 pixels at 1080p / 60-degree FOV,
// blurring cloud edges into foggy blobs. All data comes from cloudLayerA/B + cloudParams0/1.
//
// Coordinate convention: each cloud layer is a horizontal thin sheet at height h.
// Noise is indexed by world-space XZ measured in kilometers (engine world units are meters, hence *0.001).
// Visible clouds and cloud shadows share the same indexing, the same noise, and the same coverage remapping,
// so the clouds you see are the clouds that cast shadows. The two paths differ only in how the ray intersection is computed (see CloudLayerHitKm).
//
// Distance from the view ray to the intersection with the given cloud layer, in kilometers.
// This is a spherical-shell intersection, not a plane intersection:
// the planar approximation t=h/dir.y blows up near the horizon and stretches clouds into infinitely long streaks,
// while the spherical-shell solution converges to sqrt(2Rh) as dir.y->0.
// For R=6360 and h=1.6km, that is about 142km, which is exactly why clouds visually collapse into a thin horizon band,
// and why lower clouds move faster than higher clouds when looking upward.
// The observer is at (0,R,0), the planet center is at the origin, and the positive root of |p+t*d| = R+h is taken.
// R comes from skyParams4.w (CPU-side GroundRadiusKm+ViewAltitudeKm).
// This is meaningful only for dir.y > 0; a downward ray would hit the far side of the planet, so the caller must gate it first.
fn CloudLayerHitKm(dir: vec3f, layerAltKm: f32) -> f32 {
    let r = max(uLights.skyParams4.w, 1.0);
    let b = r * dir.y;
    return -b + sqrt(max(b * b + 2.0 * r * layerAltKm + layerAltKm * layerAltKm, 0.0));
}

// Forward-scattering "silver lining" for clouds, normalized to [0,1] where pure forward = 1.
// Use the Henyey-Greenstein shape instead of pow(cos):
// g is cloudParams1.y, the same control knob used on the CPU. Self-normalization avoids hardcoding a second source of truth for the peak constant.
fn CloudSilverLining(cosTheta: f32, g: f32) -> f32 {
    let g2 = g * g;
    let dn = max(1.0 + g2 - 2.0 * g * cosTheta, 1e-4);
    let p = (1.0 - g2) / (dn * sqrt(dn));
    let dp = max(1.0 + g2 - 2.0 * g, 1e-4);
    let peak = (1.0 - g2) / (dp * sqrt(dp));
    return clamp(p / max(peak, 1e-6), 0.0, 1.0);
}

// Composite clouds into sky radiance, used only by the renderMode==3 branch.
// Ordering matters: clouds sit in front of all sky components
// (the Sky-View LUT is infinitely distant in-scattering, and the sun/moon disks and starfield are as well),
// so each layer is over-composited first and the accumulated transmittance then attenuates the sky behind it.
// That is what naturally lets clouds occlude the sun and stars.
// Layer order is height order: for dir.y>0, higher layers intersect farther away, and the CPU fills layers in ascending height.
fn CloudComposite(skyRadiance: vec3f, dir: vec3f, camXZKm: vec2f) -> vec3f {
    var acc = vec3f(0.0);
    var trans = 1.0;

    // Compute forward scattering only against the sun.
    // At moonlight levels the silver lining is not visible, so evaluating another HG term would just waste work.
    let fwd = uLights.cloudParams1.w * CloudSilverLining(dot(dir, uLights.skyParams0.xyz), uLights.cloudParams1.y);

    // Fade near the horizon by about 1.4 degrees, using the same pattern as the starfield's clamp(dir.y*20).
    // Otherwise a hard edge appears at dir.y=0, which is especially obvious in scenes without ground geometry.
    let horizonFade = clamp(dir.y * 40.0, 0.0, 1.0);

    let count = i32(uLights.cloudParams0.w);
    for (var i: i32 = 0; i < count; i = i + 1) {
        let tKm = CloudLayerHitKm(dir, uLights.cloudLayerA[i].x);
        let d = cloudDensityAt(camXZKm + dir.xz * tKm, i);

        // Slanted traversal path: the flatter the view ray, the longer the geometric path through the same layer.
        // The denominator is clamped at 0.05, about 3 degrees. Below that, the spherical-shell convergence should take over,
        // otherwise the full horizon ring darkens into a hard black wall.
        let tau = d * uLights.cloudLayerA[i].w * uLights.cloudLayerA[i].y / max(dir.y, 0.05);
        let alpha = clamp(1.0 - exp(-tau), 0.0, 1.0) * horizonFade;

        // Self-occlusion proxy with zero extra taps: optically thicker cloud cores get darker while edges stay brighter,
        // which matches the look of cumulus clouds viewed from below.
        // The physically better solution would re-step along the light ray several times, but that cost is left for a higher quality tier.
        let lit = clamp(1.0 - d, 0.0, 1.0);
        let radiance = uLights.cloudParams0.rgb * mix(uLights.cloudParams1.z, 1.0, lit) * (1.0 + fwd);

        acc += trans * alpha * radiance;
        trans *= 1.0 - alpha;
    }

    return skyRadiance * trans + acc;
}

// 1-7 Contract Clause 7: evaluate SH9 irradiance (Ramamoorthi & Hanrahan 2001).
// The basis functions use the unnormalized polynomial form, while the CPU has already pre-multiplied
// the convolution coefficients A_l*k_i^2/pi into irradianceSH9, so only the 9-term linear combination is done here.
// The return value is E(n)/pi, which has the same units as constant ambient light and can be multiplied directly by albedo.
// Term-by-term identical to the HLSL/GLSL/MSL versions; in WGSL, uLights is a module-scope var so it does not need explicit parameter passing like MSL.
fn EvaluateIrradianceSH9(n: vec3f) -> vec3f {
    var result = uLights.irradianceSH9[0].rgb;
    result += uLights.irradianceSH9[1].rgb * n.y;
    result += uLights.irradianceSH9[2].rgb * n.z;
    result += uLights.irradianceSH9[3].rgb * n.x;
    result += uLights.irradianceSH9[4].rgb * (n.x * n.y);
    result += uLights.irradianceSH9[5].rgb * (n.y * n.z);
    result += uLights.irradianceSH9[6].rgb * (3.0 * n.z * n.z - 1.0);
    result += uLights.irradianceSH9[7].rgb * (n.x * n.z);
    result += uLights.irradianceSH9[8].rgb * (n.x * n.x - n.y * n.y);
    return max(result, vec3f(0.0));
}

// 2-4 Clauses 9/10: probe irradiance sampling.
// Octahedral decoding strictly mirrors ddgiProbeUpdate's OctDecode/tile layout
// (tile 8^2 = 6^2 inner core + 1px gutter. The absolute center texel is tile*8+1+oct*6, so normalized UV divides directly by atlas size).
// worldPos is offset along the normal by giParams2.x (normalBias), then the 8 neighboring probes are sampled and mixed by trilinear weights
// multiplied by cosine-direction weights. uSampler(filtering) bilinearly samples the inner octahedral core of each probe,
// with the gutter handling seam overflow. The result is multiplied by GiIntensity.
// When uLights.giParams2.y>0.5, a Chebyshev variance test is performed per probe using the depth-moment atlas,
// and the resulting visibility factor is multiplied into the weight to suppress wall-gap, contact-area, and backside light leaks.
// Since Step 5, invalid probes with tile alpha<0.5 (backface hit rate above the threshold, Clause 13) are removed from the weighting.
// If all 8 neighbors are invalid, the path falls back to SH9 ambient irradiance.
// textureSampleLevel uses explicit LOD and therefore avoids uniformity constraints. The implementation matches the other three backends line by line.
fn DdgiOctEncode(dir: vec3f) -> vec2f {
    let a = abs(dir);
    var p = dir.xy / (a.x + a.y + a.z);
    if (dir.z < 0.0) {
        let s = vec2f(select(-1.0, 1.0, p.x >= 0.0), select(-1.0, 1.0, p.y >= 0.0));
        p = (vec2f(1.0) - abs(vec2f(p.y, p.x))) * s;
    }
    return p;
}

// fallback = the diffuse result that would have been used without DDGI
// (either SH9 or constant ambient). Used as the fallback for invalid probes in Step 5.
fn SampleProbeIrradiance(worldPos: vec3f, N: vec3f, fallback: vec3f) -> vec3f {
    let gridMin = uLights.giParams0.xyz;
    let spacing = uLights.giParams0.w;
    let dims = uLights.giParams1.xyz;
    let atlasSize = vec2f(dims.x * dims.z * 8.0, dims.y * 8.0);
    let oct = DdgiOctEncode(N) * 0.5 + 0.5;

    let wp = worldPos + N * uLights.giParams2.x;
    let gc = (wp - gridMin) / spacing - vec3f(0.5);
    let base = floor(gc);
    let f = gc - base;

    var sum = vec3f(0.0);
    var wsum = 0.0;
    var wraw = 0.0;
    for (var i: i32 = 0; i < 8; i = i + 1) {
        let off = vec3f(f32(i & 1), f32((i >> 1) & 1), f32((i >> 2) & 1));
        let tri = mix(vec3f(1.0) - f, f, off);
        var w = tri.x * tri.y * tri.z;
        let pi = clamp(base + off, vec3f(0.0), dims - vec3f(1.0));
        let probePos = gridMin + (pi + vec3f(0.5)) * spacing;
        let wdir = max(dot(normalize(probePos - worldPos), N), 0.0);
        w = w * (wdir * wdir + 0.01);
        let tile = vec2f(pi.x + pi.z * dims.x, pi.y);
        let uv = (tile * 8.0 + vec2f(1.0) + oct * 6.0) / atlasSize;
        // Step 5 validity weighting (Clause 13): alpha is a classification value constant across the tile,
        // so sampling any point inside the tile is sufficient.
        // Weight continuously instead of using a hard step threshold because alpha is a temporal EMA of the classification,
        // and hard gating would amplify probe jitter near the threshold into visible flicker.
        // wraw accumulates the pure geometric weights before validity so the end of the function can estimate
        // how much of this shaded point falls on valid probes.
        let valid = clamp(textureSampleLevel(uDdgiAtlas, uSampler, (tile * 8.0 + vec2f(4.0)) / atlasSize, 0.0).a, 0.0, 1.0);
        if (uLights.giParams2.y > 0.5) {
            let dirPW = wp - probePos;
            let distPW = length(dirPW);
            let octD = DdgiOctEncode(normalize(dirPW)) * 0.5 + 0.5;
            let depAtlasSize = vec2f(dims.x * dims.z * 16.0, dims.y * 16.0);
            let uvD = (tile * 16.0 + vec2f(1.0) + octD * 14.0) / depAtlasSize;
            let m = textureSampleLevel(uDdgiDepth, uSampler, uvD, 0.0).xy;
            let variance = max(m.y - m.x * m.x, 0.0);
            let d2 = distPW - m.x;
            var cheb = 1.0;
            if (distPW > m.x) {
                cheb = variance / (variance + d2 * d2);
            }
            let cheb3 = cheb * cheb * cheb;
            // Visibility floor: keep 20% indirect light even under full occlusion,
            // preventing wall surfaces from going pure black when AABB proxy occlusion over-occludes
            // (cheb^3 amplifies occlusion cubically).
            w = w * (0.2 + 0.8 * cheb3);
        }
        wraw = wraw + w;
        w = w * valid;
        sum = sum + textureSampleLevel(uDdgiAtlas, uSampler, uv, 0.0).rgb * w;
        wsum = wsum + w;
    }
    // Step 5: wsum/wraw gives the fraction of this shaded point's interpolation weight covered by valid probes.
    // Use it to linearly blend between probe irradiance and fallback
    // (the diffuse term that would exist without DDGI). If all 8 neighbors are invalid, including the atlas's zero-initialized state
    // before the first update, the result naturally becomes pure fallback, and the transition stays continuous without threshold pops or flicker.
    var probeIrr = vec3f(0.0);
    if (wsum > 1e-6) {
        probeIrr = sum / wsum;
    }
    let vfrac = clamp(wsum / max(wraw, 1e-6), 0.0, 1.0);
    return mix(fallback, probeIrr * uLights.giParams1.w, vec3f(vfrac));
}

fn HasTexture(flagMask: i32) -> bool {
    return (u.flags.w & flagMask) != 0;
}

fn msdfMedian(r: f32, g: f32, b: f32) -> f32 {
    return max(min(r, g), min(max(r, g), b));
}

// ── 1-4 Step B: inverse ACES (Narkowicz), used for text design-color compensation.
// Textually identical to DX AcesFilmInv / VK AcesFilmInv.
// Solve the quadratic form of AcesFilm and take the positive root. Clamp y to 0.999 to keep the denominator away from zero (Contracts 2/4).
fn AcesFilmInv(yIn: vec3f) -> vec3f {
    let y = min(yIn, vec3f(0.999));
    let A = 2.51 - 2.43 * y;
    let B = 0.03 - 0.59 * y;
    return (-B + sqrt(B * B + 4.0 * A * (0.14 * y))) / (2.0 * A);
}

// ── 1-5 shadow sampling (textual translation from the VK GLSL source):
// single-tile 3x3 PCF, clamped inside the tile to avoid leakage.
// textureSampleCompareLevel, not textureSampleCompare, avoids WGSL uniformity restrictions in non-uniform control flow.
fn SampleShadowTile(slot: i32, shadowNdc: vec3f) -> f32 {
    var result = 1.0;
    let uv = vec2f(shadowNdc.x * 0.5 + 0.5, 0.5 - shadowNdc.y * 0.5);
    if (uv.x >= 0.0 && uv.x <= 1.0 && uv.y >= 0.0 && uv.y <= 1.0 &&
        shadowNdc.z > 0.0 && shadowNdc.z < 1.0) {
        let texel = uLights.shadowParams0.z;
        let tileOrigin = vec2f(f32(slot & 1), f32(slot >> 1)) * 0.5;
        let tileMin = tileOrigin + texel * 1.5;
        let tileMax = tileOrigin + 0.5 - texel * 1.5;
        let atlasUV = tileOrigin + uv * 0.5;
        var sum = 0.0;
        for (var dy: i32 = -1; dy <= 1; dy = dy + 1) {
            for (var dx: i32 = -1; dx <= 1; dx = dx + 1) {
                let sampleUV = clamp(atlasUV + vec2f(f32(dx), f32(dy)) * texel, tileMin, tileMax);
                sum = sum + textureSampleCompareLevel(uShadowAtlas, uShadowSampler, sampleUV, shadowNdc.z);
            }
        }
        result = sum / 9.0;
    }
    return result;
}

// Directional light (CSM): choose the cascade slot by view-space depth, then sample after light-space projection and mix by shadowStrength.
// cascadeSplits is first copied into a local var because dynamic vec4f indexing requires reference semantics.
fn ComputeSunShadow(worldPos: vec3f, viewDepth: f32) -> f32 {
    var result = 1.0;
    let cascadeCount = i32(uLights.shadowParams0.y);
    var splits = uLights.cascadeSplits;
    if (uLights.shadowParams0.x >= 0.5 && viewDepth <= splits[cascadeCount - 1]) {
        var slot = cascadeCount - 1;
        for (var c: i32 = cascadeCount - 1; c >= 0; c = c - 1) {
            if (viewDepth <= splits[c]) { slot = c; }
        }
        let lightPos = uLights.cascadeViewProj[slot] * vec4f(worldPos, 1.0);
        let visibility = SampleShadowTile(slot, lightPos.xyz / lightPos.w);
        result = mix(1.0, visibility, uLights.shadowParams1.y);
    }
    return result;
}

// Spotlight: single tile at slot 3, sampled after perspective divide.
fn ComputeSpotShadow(worldPos: vec3f) -> f32 {
    var result = 1.0;
    if (uLights.shadowParams1.x >= 0.5) {
        let lightPos = uLights.spotShadowViewProj * vec4f(worldPos, 1.0);
        if (lightPos.w > 0.0) {
            let visibility = SampleShadowTile(3, lightPos.xyz / lightPos.w);
            result = mix(1.0, visibility, uLights.shadowParams1.y);
        }
    }
    return result;
}

// ── 2-3 Step A: extract the shading body into shade(), shared by both fs_main (single target)
// and fs_main_mrt (color + velocity).
// The parameter uses ShadeInput with no IO attributes to avoid the spec gray area around passing
// structs containing @builtin/@location through regular functions.
// Validation errors on this platform are reported asynchronously to the console instead of throwing,
// so this path intentionally avoids relying on undefined behavior.
// Member names match the original input.* names, and the shading body is otherwise unchanged.
// WGSL has no forward declarations, so shade() must appear before both entry points.
struct ShadeInput {
    uv: vec2f,
    normal: vec3f,
    worldPos: vec3f,
    tangent: vec4f,
    instanceColor: vec4f,
    viewDepth: f32,
    // 2-5 Step E: @builtin(position).xy in framebuffer-pixel coordinates, used to reconstruct AP screen UV
    // (apUv = position.xy * velocityParams.zw, textually identical to DX/VK/Metal)
    position: vec2f,
};

// 2-3 Contract Clause 2: MRT slot 1 = SceneVelocity (Rg16Float, in UV space).
// Clause 7: transparent objects zero out slot 1 through the PSO write mask instead of shader branching,
// so this path always writes it unconditionally.
struct FragmentOutput {
    @location(0) color: vec4f,
    @location(1) velocity: vec2f,
};

fn shade(input: ShadeInput) -> vec4f {
    let texColor = textureSample(uTexture, uSampler, input.uv);
    let albedo = texColor.rgb * u.baseColor.rgb;
    let alpha = texColor.a * u.material.z;

    // renderMode == 2 (TextMsdf): multi-channel signed distance field rendering
    if (u.flags.y == 2) {
        let msdfDist = msdfMedian(texColor.r, texColor.g, texColor.b) - 0.5;
        let trueDist = texColor.a - 0.5;
        let signedDistance = select(trueDist, msdfDist, msdfDist * trueDist > 0.0);
        let pxRange = max(u.material.x, 1.0);
        let texDims = vec2f(textureDimensions(uTexture));
        let unitRange = vec2f(pxRange / max(texDims.x, 1.0), pxRange / max(texDims.y, 1.0));
        let fw = vec2f(abs(dpdx(input.uv)) + abs(dpdy(input.uv)));
        let screenTexSize = max(vec2f(1.0) / max(fw, vec2f(1e-5)), vec2f(1.0));
        let screenPxRange = max(0.5 * dot(unitRange, screenTexSize), 1.0);
        let coverage = clamp(screenPxRange * signedDistance + 0.5, 0.0, 1.0);
        // instanceColor is decided by the VS:
        // on instanced paths it is per-glyph color (or baseColor), while on non-instanced paths it is always baseColor.
        let textColor = input.instanceColor.rgb;
        let textAlpha = input.instanceColor.a * u.material.z;
        if (HDR_CHAIN) {
            // 1-4 Step B: inverse-ACES compensation for design colors in display space.
            // This lets the full FinalBlit exposure * ACES + gamma chain reconstruct the design color exactly,
            // making glyphs pixel-equivalent to the LDR baseline (Contract 4, structurally aligned with the DX/VK text branch).
            // When exposure <= 0, fall back to 1.0 to avoid divide-by-zero and blown-out output when the value is not injected.
            // Note: target is a WGSL reserved word, so the DX/VK template variable name is renamed to designColor here.
            let designColor = clamp(textColor, vec3f(0.0), vec3f(1.0));
            let safeExposure = select(1.0, uLights.params0.y, uLights.params0.y > 0.0);
            let textHdr = AcesFilmInv(pow(designColor, vec3f(2.2))) / safeExposure;
            return vec4f(textHdr, textAlpha * coverage);
        }
        return vec4f(textColor, textAlpha * coverage);
    }

    // 2-5 procedural sky: reconstruct Sky-View LUT UVs from the world-space view direction,
    // explicitly ignoring vertex UV. The texColor sample at the function entry is discarded entirely in this branch.
    // The single source of truth for parameterization is the header of Season.Rendering.Atmosphere,
    // and the inversion here is textually aligned with the skyView kernel:
    // the U seam lies on +Z (north), celestial arcs never cross north, and the Mie peak therefore never hits the seam.
    // V uses sqrt folding to concentrate resolution toward the horizon; uniform V spacing would band there.
    // The LUT is rgba16float with no mip chain, and implicit derivatives miscompute LOD around the seam,
    // so sampling always uses textureSampleLevel(..., 0.0).
    if (u.flags.y == 3) {
        let skyDir = normalize(input.worldPos - uLights.cameraPos.xyz);
        var skyUv: vec2f;
        skyUv.x = atan2(skyDir.x, -skyDir.z) * (0.5 / PI) + 0.5;
        skyUv.y = 0.5 - 0.5 * sign(skyDir.y) * sqrt(abs(skyDir.y));
        var skyRadiance = textureSampleLevel(uTexture, uSampler, skyUv, 0.0).rgb * u.baseColor.rgb;

        // 2-5 Step B: add the analytical sun/moon disks and starfield.
        // The gate is skyParams0.w > 0. All four fields being zero means a non-procedural sky tier.
        // Since a real angular-radius cosine is about 0.99999 and never 0, the StaticCube tier leaves zero residue here.
        // pxAng is computed outside the disk/star branches and passed in because fwidth is a gradient operation
        // and cannot live inside those non-uniform branches.
        // The two branch conditions here, renderMode and skyParams0.w, are both uniform-buffer constants,
        // so WGSL uniformity analysis treats them as uniform, just like the textureSample calls below.
        if (uLights.skyParams0.w > 0.0) {
            // Fallback for pxAng is 1/screenHeight from velocityParams.w, injected every frame by UpdateCamera3D.
            // If fwidth erroneously returns 0 because derivative propagation failed, the old fallback of 1e-6 rad
            // is only about 0.001 pixel, which collapses stars below pixel scale and makes the whole starfield disappear.
            // The sun and moon disks themselves do not depend on pxAng, so the visible symptom is "sun/moon visible but no stars".
            let pxAng = max(length(fwidth(skyDir)), max(uLights.velocityParams.w, 1e-4));
            let cel = SkyCelestialRadiance(skyDir, pxAng);
            skyRadiance += cel * u.baseColor.rgb;
        }

        // 2-5 Step C: compose procedural clouds. This must happen after the celestial disks,
        // because clouds sit in front of all sky components and therefore must occlude the sun and stars.
        // That occlusion is exactly the skyRadiance*trans term at the end of CloudComposite.
        // There are two gates: cloudParams0.w (layer count, a uniform constant that also implies the noise texture is ready)
        // and dir.y > 0 (per pixel, because downward rays intersect the far side of the planet and are meaningless here).
        // The implementation uses only textureSampleLevel with explicit LOD 0, so this non-uniform branch does not involve implicit derivatives.
        if (uLights.cloudParams0.w > 0.0 && skyDir.y > 0.0) {
            skyRadiance = CloudComposite(skyRadiance, skyDir, uLights.cameraPos.xz * 0.001);
        }

        if (HDR_CHAIN) {
            // The LUT already stores linear HDR radiance, so output it directly and let the full FinalBlit
            // exposure + ACES + gamma chain converge it, per the 1-4 contract.
            return vec4f(skyRadiance, alpha);
        }
        // LDR baseline: gamma-encode in place. max(...,0) is not a visual-quality safeguard.
        // Radiance is physically non-negative, but the WGSL compiler cannot infer that from "sampled value * material color",
        // so explicitly clamping negative values before pow avoids Tint diagnostics.
        return vec4f(pow(max(skyRadiance, vec3f(0.0)), vec3f(1.0 / 2.2)), alpha);
    }

    if (u.flags.z == 1 && alpha < u.material.w) {
        discard;
    }

    if (u.flags.y == 0) {
        if (alpha < 0.001) { discard; }
        if (HDR_CHAIN) {
            // 1-4 Step A: output directly in pre-encoding space and move gamma into the FinalBlit tonemap variant.
            // This is a pure relocation of work and remains pixel-equivalent.
            return vec4f(albedo, alpha);
        }
        let colorUnlit = pow(albedo, vec3f(1.0 / 2.2));
        return vec4f(colorUnlit, alpha);
    }

    var metallic: f32 = u.material.x;
    var roughness: f32 = u.material.y;
    if (HasTexture(1)) {
        let mr = textureSample(uMetallicRoughnessTexture, uSampler, input.uv);
        metallic = u.material.x * mr.b;
        roughness = u.material.y * mr.g;
    }

    var ao = u.emissive.w;
    if (HasTexture(4)) {
        ao = textureSample(uAoTexture, uSampler, input.uv).r;
    }

    var emissive = u.emissive.xyz;
    if (HasTexture(8)) {
        emissive = textureSample(uEmissiveTexture, uSampler, input.uv).rgb;
    }

    var N = normalize(input.normal);
    if (HasTexture(2)) {
        let T0 = normalize(input.tangent.xyz);
        let T = normalize(T0 - dot(T0, N) * N);
        let B = cross(N, T) * input.tangent.w;
        let TBN = mat3x3f(T, B, N);
        let sampledNormal = textureSample(uNormalTexture, uSampler, input.uv).rgb * 2.0 - 1.0;
        // TBN built from columns must be pre-multiplied (tangent -> world, aligned with VK GLSL).
        // Post-multiplication would effectively use the transpose, i.e. the inverse transform, and produce completely wrong normals.
        N = normalize(TBN * sampledNormal);
    }

    let V = normalize(uLights.cameraPos.xyz - input.worldPos);
    var F0 = vec3f(0.04);
    F0 = mix(F0, albedo, metallic);

    // Accumulate direct lighting
    // (1-2 Contract 2: directional, point, and spot lights all live in the same lights array and are dispatched in one loop by dirType.w)
    var Lo = vec3f(0.0);

    let lightCount = min(i32(uLights.params0.x), 8);
    let dirShadowIdx = i32(uLights.params0.z);      // Index of the directional light that casts CSM shadows (-1 = none)
    let spotShadowIdx = i32(uLights.params0.w);     // Index of the spotlight that casts the 2D shadow map (-1 = none)
    for (var i: i32 = 0; i < lightCount; i = i + 1) {
        let lightType = uLights.lights[i].dirType.w;
        var L: vec3f;
        var radiance: vec3f;

        if (lightType >= 1.5) {
            // Directional light (sun/moon): L is constant with no attenuation,
            // radiance = color * intensity * CSM shadow visibility from 1-5.
            L = normalize(-uLights.lights[i].dirType.xyz);
            radiance = uLights.lights[i].colorIntensity.xyz * uLights.lights[i].colorIntensity.w;
            if (i == dirShadowIdx) {
                radiance = radiance * ComputeSunShadow(input.worldPos, input.viewDepth);
            }
            // 2-5 Step C: cloud shadows. Evaluate them independently for every directional light using that light's own L,
            // so sun and moon can each cast their own cloud shadow.
            // This differs from CSM, which only uses dirShadowIdx, because cloud shadows do not consume shadow-atlas slots and are not limited to one light.
            // Intentionally do not gate this by ShadowsEnabled, because that switch controls only CSM / the shadow atlas.
            // Cloud shadows should still sweep across the ground even when CSM is disabled, since that is a major lighting cue on overcast days.
            radiance = radiance * computeCloudShadow(input.worldPos, L);
        } else {
            let toLight = uLights.lights[i].posRange.xyz - input.worldPos;
            let dist = length(toLight);
            L = toLight / max(dist, 0.0001);

            // Attenuation (Contract 3, aligned with KHR_lights_punctual):
            // use a window function cutoff when range>0, and fall back to pure 1/d^2 when range<=0.
            var attenuation = 1.0 / max(dist * dist, 0.0001);
            let range = uLights.lights[i].posRange.w;
            if (range > 0.0) {
                let win = clamp(1.0 - pow(dist / range, 4.0), 0.0, 1.0);
                attenuation = attenuation * win * win;
            }

            // Spotlight cone (Contract 4): cosine values are precomputed on the CPU, and smoothstep softens the boundary.
            if (lightType > 0.5) {
                attenuation = attenuation * smoothstep(uLights.lights[i].spotParams.y, uLights.lights[i].spotParams.x,
                                                       dot(-L, normalize(uLights.lights[i].dirType.xyz)));
            }

            radiance = uLights.lights[i].colorIntensity.xyz * uLights.lights[i].colorIntensity.w * attenuation;
            // 1-5: spotlight shadows apply only to the light selected by params0.w
            // (contract across all four backends: one spotlight shadow map in slot 3).
            if (i == spotShadowIdx && lightType > 0.5) {
                radiance = radiance * ComputeSpotShadow(input.worldPos);
            }
        }

        Lo = Lo + EvaluatePbrLight(N, V, L, albedo, metallic, roughness, F0, radiance);
    }

    // Ambient lighting
    // (1-2 Contract 6: parameterized, with default (0.5,0.5,0.5)*1.0 matching the look of the old hardcoded path).
    // 1-7 Contract Clause 5: choose exactly one of SH9 ambient diffuse or constant ambient, since they share units and would double-count if added.
    // Both are gated by (1-metallic), because metallic surfaces have no diffuse term.
    // 2-4 Contract Clause 9: choose exactly one of three diffuse sources, never add them together.
    // When DDGI is ready and GiIntensity>0, probe irradiance replaces the SH9/constant-ambient choice; otherwise the path fully falls back to 1-7/1-2.
    // When DDGI_ENABLED is const false, the compiler removes that branch as dead code.
    // Clause 13: the probe path fades continuously back to giDiffuse based on validity, so giDiffuse also serves as the Step-5 fallback.
    let envDiffuse = EvaluateIrradianceSH9(N) * uLights.envParams.y;
    let constAmbient = uLights.ambientParams.xyz * uLights.ambientParams.w;
    var giDiffuse = mix(constAmbient, envDiffuse, step(0.5, uLights.envParams.z));
    if (DDGI_ENABLED && uLights.giParams2.z > 0.5 && uLights.giParams1.w > 0.0) {
        giDiffuse = SampleProbeIrradiance(input.worldPos, N, giDiffuse);
    }
    var ambient = giDiffuse * albedo * ao * (1.0 - metallic);

    // 1-7 Contract Clause 6: use mirrored reflection from the radiance cube at LOD0 for the specular term.
    // There is no mip chain and no GGX prefiltering, so it is masked by (1-roughness)^2.
    // The ambient energy of rough surfaces is carried by the SH9 diffuse term above.
    let R = reflect(-V, N);
    let envSpecular = textureSampleLevel(uEnvCube, uSampler, R, 0.0).rgb * uLights.envParams.x;
    let specMask = (1.0 - roughness) * (1.0 - roughness);
    ambient += envSpecular * F0 * specMask * ao * step(0.5, uLights.envParams.w);

    var color = ambient + Lo + emissive;
    // 2-5 Step E: add aerial perspective.
    // Its position is intentionally in linear HDR space before tonemapping, because atmospheric in-scattering is a real radiance contribution.
    // Applying a curve first and then adding it would wash distant blue haze into gray-white.
    // Only the renderMode==1 PBR path reaches here, because TextMsdf and Sprite2D return earlier, so the sky itself is not fogged twice.
    // The z axis uses sqrt(distance/maxDistance), which is the inverse of the slice-center distance used during skyAerial baking,
    // maxDist*((k+0.5)/N)^2. That makes slices denser near the camera and sparser in the distance, matching the fact that AP gradients are concentrated in the first few kilometers.
    if (uLights.apParams0.x > 0.0) {
        let apUv = input.position.xy * uLights.velocityParams.zw;
        let distKm = length(input.worldPos - uLights.cameraPos.xyz) * 0.001;
        let apW = sqrt(clamp(distKm / uLights.apParams0.x, 0.0, 1.0));
        let ap = textureSampleLevel(uAerialLut, uSampler, vec3f(apUv, apW), 0.0);
        color = mix(color, color * (1.0 - ap.a) + ap.rgb, uLights.apParams0.y);
    }
    // 1-4 Step B: in HDR tiers, output true linear color directly.
    // Reinhard+gamma is only for the LDR tier, and tone mapping converges in FinalBlit
    // (Contract 1, structurally aligned with DX/VK).
    if (!HDR_CHAIN) {
        color = color / (color + vec3f(1.0));
        color = pow(color, vec3f(1.0 / 2.2));
    }
    return vec4f(color, alpha);
}

@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4f {
    return shade(ShadeInput(input.uv, input.normal, input.worldPos, input.tangent,
                            input.instanceColor, input.viewDepth, input.position.xy));
}

// 2-3 Contract Clause 5: compute velocity unconditionally before any discard.
// prevClip.w <= 0 means no history, covering all 2D/UI/text paths and the VS sentinel fallback, so the result stays zero.
@fragment
fn fs_main_mrt(input: VertexOutput) -> FragmentOutput {
    var output: FragmentOutput;
    output.velocity = vec2f(0.0);
    if (input.prevClip.w > 0.0) {
        // curNdc: reconstruct NDC from @builtin(position), which is in framebuffer-pixel coordinates,
        // then subtract current-frame jitter to de-jitter it.
        // prevNdc: perspective divide of prevClip, whose source matrix prevViewProjection is itself non-jittered.
        // velocity = (curNdc - prevNdc) * (0.5, -0.5), converting to UV space with a flipped Y axis,
        // textually identical to DX/VK/Metal.
        var curNdc = input.position.xy * uLights.velocityParams.zw * vec2f(2.0, -2.0) + vec2f(-1.0, 1.0);
        curNdc -= uLights.velocityParams.xy;
        let prevNdc = input.prevClip.xy / input.prevClip.w;
        output.velocity = (curNdc - prevNdc) * vec2f(0.5, -0.5);
    }
    output.color = shade(ShadeInput(input.uv, input.normal, input.worldPos, input.tangent,
                                    input.instanceColor, input.viewDepth, input.position.xy));
    return output;
}

// ── Phase 4 Outline2D mask entry (mirrors VK OUTLINE_MASK / DX PSOutlineMask semantics) ──
// Alpha follows the material transparency chain (albedo alpha * material alpha; on Web, albedo is always sampled with White as fallback).
// In MASK mode, fragments below the threshold are discarded. Color passes through the group color and alpha is always 1,
// so any outline color, including pure black, remains valid.
// The outline color reuses the retired hdrParams slot and is uploaded per draw with zero layout changes.
// The VS reuses vs_main, so static, instanced, skinned, and morph paths all work naturally under flag control, matching the shadow pattern.
@fragment
fn fs_main_outline_mask(input: VertexOutput) -> @location(0) vec4f {
    let maskAlpha = textureSample(uTexture, uSampler, input.uv).a * u.material.z;
    if (u.flags.z == 1 && maskAlpha < u.material.w) {
        discard;
    }
    return vec4f(u.hdrParams.rgb, 1.0);
}
""";

    /// <summary>
    /// FinalBlit shader (WGSL, 1-1 Step 2/3 + 1-4 Step A): merges the full-screen-triangle variants into one module with six entry points.
    /// On the JS side, this means one createShaderModule call and four createRenderPipeline calls targeting different entry points:
    ///   - point (vs_point/fs_point): identity mapping through textureLoad(fragCoord) with zero resampling error
    ///     when the source matches the backbuffer size;
    ///   - linear (vs_linear/fs_linear): VS outputs UVs (NDC Y up -> UV Y down) and uses a linear sampler
    ///     for scaled presentation from non-full-size RTs;
    ///   - two tonemap variants (fs_point_tonemap/fs_linear_tonemap, 1-4 Step B): converge HDR sources
    ///     (rgba16float) through exposure * ACES (Narkowicz) + gamma, with exposure uploaded each frame through
    ///     the binding-2 uniform and selected automatically from the source RT format, mirroring the four DX/VK variants;
    ///   - 2-1 Step D (aligned with DX Step B/C): two tonemap+bloom variants
    ///     (binding 3 bloom texture is always linearly upsampled and added in linear space before ACES);
    ///     two uber variants used by the Post pass (tonemap(+bloom) -> LDR PostColor with Rec.601 luma packed into alpha);
    ///     and the FXAA variant used for FinalBlit resolve, textually ported from the DX reference implementation,
    ///     using binding 4 point-sampler neighborhood taps and binding 1 linear directional taps.
    ///     Parameters are unified in binding 2 vec4f (x=exposure, y=bloomIntensity, zw=texelSize).
    ///   - 2-2 Step C (aligned with DX 2-2 Step B): six AO variants
    ///     (tonemap±bloom × point/linear + uber±bloom), where AO occlusion is multiplied in linear space before ACES and bloom is then added
    ///     (scene × mix(1, ao, aoIntensity) + bloom × bloomIntensity, so AO darkens only the scene and not bloom).
    ///     The AO texture is always linearly upsampled at binding 5 (half-resolution GTAO output, r channel),
    ///     and aoIntensity uses a dedicated uniform at binding 6 (vec4f.x, mirroring the fifth constant in DX b0).
    /// layout:'auto' derives bindings from each pipeline's static references:
    /// point group 0 contains only the texture, linear adds the sampler
    /// (binding 1 is referenced only by fs_linear*), and tonemap variants add the exposure uniform at binding 2.
    /// Bind groups created from an auto layout cannot be reused across pipelines, so HDR RT bind groups
    /// must be created from the tonemap pipeline layout and include the exposure buffer entry, selected by JS according to format.
    /// This class is the single source of truth for WGSL; seasonWebGPU.js contains no shader source and only receives it during initialize.
    /// </summary>
    public const string BlitShader = """
@group(0) @binding(0) var srcTex : texture_2d<f32>;
@group(0) @binding(1) var srcSampler : sampler;

// ── Point variant: identity mapping with no sampler ──
@vertex fn vs_point(@builtin(vertex_index) vi : u32) -> @builtin(position) vec4f {
    let pos = vec2f(f32((vi << 1u) & 2u), f32(vi & 2u));
    return vec4f(pos * 2.0 - 1.0, 0.0, 1.0);
}
@fragment fn fs_point(@builtin(position) fragCoord : vec4f) -> @location(0) vec4f {
    return textureLoad(srcTex, vec2i(fragCoord.xy), 0);
}

// ── Linear variant: UV sampling for non-full-size upscaling ──
struct BlitVSOut {
    @builtin(position) pos : vec4f,
    @location(0) uv : vec2f,
}
@vertex fn vs_linear(@builtin(vertex_index) vi : u32) -> BlitVSOut {
    var o : BlitVSOut;
    let uv = vec2f(f32((vi << 1u) & 2u), f32(vi & 2u));
    o.pos = vec4f(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.0, 1.0);
    o.uv = uv;
    return o;
}
@fragment fn fs_linear(@location(0) uv : vec2f) -> @location(0) vec4f {
    return textureSample(srcTex, srcSampler, uv);
}

// ── Two tonemap variants (1-4 Step B): HDR source (rgba16float, linear space)
// -> exposure * ACES (Narkowicz) -> gamma(1/2.2) for presentation.
// The constants 2.51/0.03/2.43/0.59/0.14 are textually identical to DX/VK/Metal (Contract 2).
// In 2-1 Step D, parameters expand to x=exposure, y=bloomIntensity, zw=texelSize for FXAA,
// uploaded each frame by blitToBackbuffer/renderPost through writeBuffer.
@group(0) @binding(2) var<uniform> tonemapParams : vec4f;

fn AcesFilm(x : vec3f) -> vec3f {
    let a = 2.51; let b = 0.03; let c = 2.43; let d = 0.59; let e = 0.14;
    return clamp((x * (a * x + b)) / (x * (c * x + d) + e), vec3f(0.0), vec3f(1.0));
}

fn Tonemap(hdr : vec3f) -> vec3f {
    let mapped = AcesFilm(max(hdr, vec3f(0.0)) * tonemapParams.x);
    return pow(mapped, vec3f(1.0 / 2.2));
}

@fragment fn fs_point_tonemap(@builtin(position) fragCoord : vec4f) -> @location(0) vec4f {
    let c = textureLoad(srcTex, vec2i(fragCoord.xy), 0);
    return vec4f(Tonemap(c.rgb), c.a);
}
@fragment fn fs_linear_tonemap(@location(0) uv : vec2f) -> @location(0) vec4f {
    let c = textureSample(srcTex, srcSampler, uv);
    return vec4f(Tonemap(c.rgb), c.a);
}

// ── Two tonemap+bloom variants (2-1 Step D, aligned with DX Step B):
// add bloom in linear space before ACES, per the RenderQuality 2-1 contract.
// Bloom comes from the half-resolution chain and is always linearly upsampled.
// The point path still uses vs_linear because bloom sampling needs UVs, while the scene source itself remains identity-mapped by textureLoad.
@group(0) @binding(3) var bloomTex : texture_2d<f32>;

@fragment fn fs_tonemap_bloom(@builtin(position) fragCoord : vec4f, @location(0) uv : vec2f) -> @location(0) vec4f {
    var c = textureLoad(srcTex, vec2i(fragCoord.xy), 0);
    c = vec4f(c.rgb + textureSample(bloomTex, srcSampler, uv).rgb * tonemapParams.y, c.a);
    return vec4f(Tonemap(c.rgb), c.a);
}
@fragment fn fs_linear_tonemap_bloom(@location(0) uv : vec2f) -> @location(0) vec4f {
    var c = textureSample(srcTex, srcSampler, uv);
    c = vec4f(c.rgb + textureSample(bloomTex, srcSampler, uv).rgb * tonemapParams.y, c.a);
    return vec4f(Tonemap(c.rgb), c.a);
}

// ── Two uber variants (2-1 Step D, used by the Post pass and aligned with DX Step C):
// tonemap(+bloom) into LDR PostColor, with luma packed into alpha using Rec.601 weights in gamma space,
// a contract constant shared across all four backends, so FXAA does not need to recompute it.
// Source and target always share size because both are MatchBackbufferSize, so the path remains identity-mapped by textureLoad.
fn Luma(ldr : vec3f) -> f32 {
    return dot(ldr, vec3f(0.299, 0.587, 0.114));
}

@fragment fn fs_uber(@builtin(position) fragCoord : vec4f) -> @location(0) vec4f {
    let c = textureLoad(srcTex, vec2i(fragCoord.xy), 0);
    let ldr = Tonemap(c.rgb);
    return vec4f(ldr, Luma(ldr));
}
@fragment fn fs_uber_bloom(@builtin(position) fragCoord : vec4f, @location(0) uv : vec2f) -> @location(0) vec4f {
    var c = textureLoad(srcTex, vec2i(fragCoord.xy), 0);
    c = vec4f(c.rgb + textureSample(bloomTex, srcSampler, uv).rgb * tonemapParams.y, c.a);
    let ldr = Tonemap(c.rgb);
    return vec4f(ldr, Luma(ldr));
}

// ── Six AO variants (2-2 Step C, aligned with DX 2-2 Step B):
// apply AO occlusion in linear space before ACES, then add bloom.
// AO darkens only the scene and never the bloom contribution.
// AO comes from the half-resolution GTAO output in the r channel and is always linearly upsampled at binding 5.
// aoIntensity uses a dedicated uniform at binding 6, present only in the AO auto layout.
// All variants pair with vs_linear because AO upsampling needs UVs; the point path still identity-maps the source through textureLoad.
@group(0) @binding(5) var aoTex : texture_2d<f32>;
@group(0) @binding(6) var<uniform> aoParams : vec4f;

fn ApplyAo(scene : vec3f, uv : vec2f) -> vec3f {
    let ao = textureSample(aoTex, srcSampler, uv).r;
    return scene * mix(vec3f(1.0), vec3f(ao), aoParams.x);
}

@fragment fn fs_tonemap_ao(@builtin(position) fragCoord : vec4f, @location(0) uv : vec2f) -> @location(0) vec4f {
    var c = textureLoad(srcTex, vec2i(fragCoord.xy), 0);
    c = vec4f(ApplyAo(c.rgb, uv), c.a);
    return vec4f(Tonemap(c.rgb), c.a);
}
@fragment fn fs_linear_tonemap_ao(@location(0) uv : vec2f) -> @location(0) vec4f {
    var c = textureSample(srcTex, srcSampler, uv);
    c = vec4f(ApplyAo(c.rgb, uv), c.a);
    return vec4f(Tonemap(c.rgb), c.a);
}
@fragment fn fs_tonemap_bloom_ao(@builtin(position) fragCoord : vec4f, @location(0) uv : vec2f) -> @location(0) vec4f {
    var c = textureLoad(srcTex, vec2i(fragCoord.xy), 0);
    c = vec4f(ApplyAo(c.rgb, uv), c.a);
    c = vec4f(c.rgb + textureSample(bloomTex, srcSampler, uv).rgb * tonemapParams.y, c.a);
    return vec4f(Tonemap(c.rgb), c.a);
}
@fragment fn fs_linear_tonemap_bloom_ao(@location(0) uv : vec2f) -> @location(0) vec4f {
    var c = textureSample(srcTex, srcSampler, uv);
    c = vec4f(ApplyAo(c.rgb, uv), c.a);
    c = vec4f(c.rgb + textureSample(bloomTex, srcSampler, uv).rgb * tonemapParams.y, c.a);
    return vec4f(Tonemap(c.rgb), c.a);
}
@fragment fn fs_uber_ao(@builtin(position) fragCoord : vec4f, @location(0) uv : vec2f) -> @location(0) vec4f {
    var c = textureLoad(srcTex, vec2i(fragCoord.xy), 0);
    c = vec4f(ApplyAo(c.rgb, uv), c.a);
    let ldr = Tonemap(c.rgb);
    return vec4f(ldr, Luma(ldr));
}
@fragment fn fs_uber_bloom_ao(@builtin(position) fragCoord : vec4f, @location(0) uv : vec2f) -> @location(0) vec4f {
    var c = textureLoad(srcTex, vec2i(fragCoord.xy), 0);
    c = vec4f(ApplyAo(c.rgb, uv), c.a);
    c = vec4f(c.rgb + textureSample(bloomTex, srcSampler, uv).rgb * tonemapParams.y, c.a);
    let ldr = Tonemap(c.rgb);
    return vec4f(ldr, Luma(ldr));
}

// ── FXAA variant (2-1 Step D, used by FinalBlit):
// reduced-quality FXAA 3.11 with 5 taps for direction estimation and 4 taps along the direction.
// Luma comes from source alpha as packed by the uber pass.
// REDUCE_MIN / REDUCE_MUL / SPAN_MAX / contrast thresholds are contract constants shared across all four backends,
// textually ported from the DX reference implementation, using binding 4 point taps for the neighborhood
// and binding 1 linear taps for the directional samples.
// The full function uses textureSampleLevel at mip 0 because WGSL forbids textureSample with implicit derivatives
// inside non-uniform control flow, and the source is always single-mip, so the result is pixel-equivalent.
@group(0) @binding(4) var pointSampler : sampler;

@fragment fn fs_fxaa(@location(0) uv : vec2f) -> @location(0) vec4f {
    let FXAA_REDUCE_MIN = 1.0 / 128.0;
    let FXAA_REDUCE_MUL = 1.0 / 8.0;
    let FXAA_SPAN_MAX = 8.0;
    let FXAA_EDGE_THRESHOLD = 1.0 / 8.0;
    let FXAA_EDGE_THRESHOLD_MIN = 1.0 / 24.0;

    let rcpFrame = tonemapParams.zw;

    let colorM = textureSampleLevel(srcTex, pointSampler, uv, 0.0);
    let lumaM  = colorM.a;
    let lumaNW = textureSampleLevel(srcTex, pointSampler, uv + vec2f(-1.0, -1.0) * rcpFrame, 0.0).a;
    let lumaNE = textureSampleLevel(srcTex, pointSampler, uv + vec2f( 1.0, -1.0) * rcpFrame, 0.0).a;
    let lumaSW = textureSampleLevel(srcTex, pointSampler, uv + vec2f(-1.0,  1.0) * rcpFrame, 0.0).a;
    let lumaSE = textureSampleLevel(srcTex, pointSampler, uv + vec2f( 1.0,  1.0) * rcpFrame, 0.0).a;

    let lumaMin = min(lumaM, min(min(lumaNW, lumaNE), min(lumaSW, lumaSE)));
    let lumaMax = max(lumaM, max(max(lumaNW, lumaNE), max(lumaSW, lumaSE)));

    var result = colorM;

    // Low-contrast early out: pass non-edge pixels through directly to save directional-sampling bandwidth.
    if (lumaMax - lumaMin >= max(FXAA_EDGE_THRESHOLD_MIN, lumaMax * FXAA_EDGE_THRESHOLD)) {
        // Edge tangent direction, orthogonal to the luma gradient,
        // normalized by local brightness and clamped to the maximum span.
        var dir = vec2f(
            -((lumaNW + lumaNE) - (lumaSW + lumaSE)),
             ((lumaNW + lumaSW) - (lumaNE + lumaSE)));

        let dirReduce = max((lumaNW + lumaNE + lumaSW + lumaSE) * 0.25 * FXAA_REDUCE_MUL, FXAA_REDUCE_MIN);
        let rcpDirMin = 1.0 / (min(abs(dir.x), abs(dir.y)) + dirReduce);
        dir = clamp(dir * rcpDirMin, vec2f(-FXAA_SPAN_MAX), vec2f(FXAA_SPAN_MAX)) * rcpFrame;

        // Four taps along the tangent direction: the inner pair (±1/6 span) is always trusted,
        // while the outer pair (±1/2 span) falls back to the inner pair when it goes out of bounds.
        let rgbA = 0.5 * (
            textureSampleLevel(srcTex, srcSampler, uv + dir * (1.0 / 3.0 - 0.5), 0.0) +
            textureSampleLevel(srcTex, srcSampler, uv + dir * (2.0 / 3.0 - 0.5), 0.0));
        let rgbB = rgbA * 0.5 + 0.25 * (
            textureSampleLevel(srcTex, srcSampler, uv + dir * -0.5, 0.0) +
            textureSampleLevel(srcTex, srcSampler, uv + dir * 0.5, 0.0));

        result = select(rgbB, rgbA, rgbB.a < lumaMin || rgbB.a > lumaMax);
    }

    return vec4f(result.rgb, 1.0);
}

// ── Outline-composite variant (Phase 4, used by FinalBlit, textually ported from DX PSMainOutlineComposite):
// OutlineMask RT (rgba8: rgb=group outline color, alpha≡1, cleared to zero) is dilated over the 8-neighborhood to extract the contour.
// edge = saturate(maxNeighborAlpha - centerAlpha), and the color comes from the winning neighbor rgb.
// The result is alpha-blended over the backbuffer using SrcAlpha/InvSrcAlpha in a dedicated JS-side PSO,
// while depth remains read-only.
// Parameters reuse tonemapParams at binding 2: xy=mask RT texel size, z=outlineWidth in pixels, clamped to at least 1.
// The mask texture is bound at binding 7, referenced statically only by this entry point, so the auto layout is naturally isolated.
// All sampling uses textureSampleLevel at LOD0.
@group(0) @binding(7) var outlineMaskTex : texture_2d<f32>;

@fragment fn fs_outline_composite(@location(0) uv : vec2f) -> @location(0) vec4f {
    let stepUv = tonemapParams.xy * max(tonemapParams.z, 1.0);
    let center = textureSampleLevel(outlineMaskTex, pointSampler, uv, 0.0).a;
    var neighbor = 0.0;
    var outlineColor = vec3f(0.0);
    let offsets = array<vec2f, 8>(
        vec2f(1.0, 0.0), vec2f(-1.0, 0.0), vec2f(0.0, 1.0), vec2f(0.0, -1.0),
        vec2f(1.0, 1.0), vec2f(-1.0, 1.0), vec2f(1.0, -1.0), vec2f(-1.0, -1.0));
    for (var i = 0; i < 8; i = i + 1) {
        let s = textureSampleLevel(outlineMaskTex, pointSampler, uv + offsets[i] * stepUv, 0.0);
        if (s.a > neighbor) {
            neighbor = s.a;
            outlineColor = s.rgb;
        }
    }
    let edge = clamp(neighbor - center, 0.0, 1.0);
    return vec4f(outlineColor, edge);
}
""";
}
