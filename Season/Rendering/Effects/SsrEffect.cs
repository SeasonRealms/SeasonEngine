// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

/// <summary>
/// P3 screen-space reflection: starting from each opaque pixel's shading normal, march along the
/// reflection direction in screen space, using the <see cref="HiZEffect"/> pyramid to skip empty
/// regions in whole blocks, and on a hit take that point's scene color as the reflected radiance.
///
/// Why this is a separate compute pass rather than a single sample in the main shader: reflection
/// needs the "opaque scene already drawn this frame", while the main shader is writing that same
/// image, and one resource cannot be both an attachment and a sampling source. Computing it
/// separately after the scene means the input is a complete, samplable opaque color, so there is no
/// need to copy another full-screen snapshot for reflections.
///
/// The output is a **delta**, not a radiance value: the kernel has already computed the difference
/// of "replacing the sky term with the hit point", Δ = conf · (1-r)² · w · (ssr − reflSky), where w
/// is the reflection weight the main shader writes into SceneNormal.w (the F0 Rec.709 luminance
/// × ao). So <see cref="SsrCompositePipeline"/> only needs a single full-screen additive draw: no
/// recomputing weights, no clearing, no separate target. In the delta, giDiffuse cancels out
/// automatically (replacement minus replacement), which is exactly why the kernel must know reflSky
/// -- the hit point's luminance cannot simply be added, since that would double the sky energy.
///
/// Pixels that miss write 0, nothing changes, and the image naturally falls back to the sky +
/// ground reflection the main shader already computed.
///
/// Marching has two stages: the coarse stage picks a pyramid level automatically from the per-pixel
/// step length (a larger step queries a coarser level, where one texel covers a whole region), and
/// it skips the block entirely while the ray depth has not passed that block's nearest depth; once
/// it passes, the block is refined using full-resolution depth to find the first intersection. The
/// coarse stage picks a level rather than always using the coarsest one because a short near ray
/// only moves a few pixels per step, and querying a 1/16 block would skip nothing at all.
///
/// Dependencies: <see cref="HiZEffect"/> must be registered first and rebuilt for this frame
/// (<see cref="HiZEffect.Ready"/>), and SceneNormal must exist -- normals reconstructed from depth
/// flip on either side of silhouettes, and the reflection would smear along the silhouette.
/// </summary>
public sealed class SsrEffect : ComputeEffect
{
    /// <summary>Reflection delta output: rgb = weighted HDR difference (may be negative), a is always 0 (additive compositing does not accumulate opacity).</summary>
    public const string TextureName = "compute://ssr/reflection";

    /// <summary>The frame buffer contains only float4; matrices are stored in System.Numerics' native row order.</summary>
    public const int FrameVectorCount = 16;

    /// <summary>Screen step count for the coarse stage. The step size is the ray's total screen displacement divided by it, so a long, thin distant ray also uses this many steps.</summary>
    const int CoarseSteps = 40;

    /// <summary>Refinement step count inside a hit block.</summary>
    const int RefineSteps = 8;

    static SsrEffect? _active;

    readonly Vector4[] _frame = new Vector4[FrameVectorCount];
    readonly ComputeResourceRef[] _resources = new ComputeResourceRef[10];
    IGraphics? _graphics;
    StorageBuffer? _frameBuffer;
    ComputeKernel? _kernel;
    SsrCompositePipeline? _composite;
    uint _width, _height;
    bool _ready;
    bool _unregistering;

    public override string Name => "ssr";

    /// <summary>AfterOpaque: reflects only the opaque scene, and must publish its result to later consumers before fog/water.</summary>
    public override ComputePhase Phase => ComputePhase.AfterOpaque;

    public override ComputeScope Scope => ComputeScope.View;

    public override ViewFeature RequiredFeature => ViewFeature.Reflections;

    /// <summary>Whether a usable reflection texture was published this frame; both compositing and water sampling gate on it.</summary>
    public bool Ready => _ready;

    public static bool AnyReady => _active is { _ready: true };

    /// <summary>Name of the currently published reflection texture; null when not ready, in which case callers fall back to sky reflection.</summary>
    public static string? PublishedTexture => _active is { _ready: true } ? TextureName : null;

    public override bool Initialize(IGraphics g)
    {
        if (_graphics != null) throw new InvalidOperationException("SSR is already initialized.");
        if (_active != null) return false;
        if (RenderQuality.Current.ScreenSpaceReflections != SsrMode.HiZ) return false;
        var color = FrameSchedule.SceneColor;
        if (color == null || color.Desc.ColorFormat != RtFormat.Rgba16Float || color.Desc.SampleCount > 1) return false;
        if (FrameSchedule.SceneDepth == null || FrameSchedule.SceneNormal == null) return false;
        if (color.Desc.Width == 0 || color.Desc.Height == 0) return false;
        _kernel = g.CreateComputeKernel(new ComputeKernelDesc
        {
            Name = "ssrTrace",
            Source = new ShaderSourceSet
            {
                Hlsl = SourceHlsl,
                Glsl = SourceGlsl,
                Msl = SourceMsl,
                Wgsl = SourceWgsl,
                EntryPoint = "CSMain",
            },
            Bindings = new[]
            {
                new ComputeBindingDesc { Type = ComputeBindingType.Params, SizeInBytes = 16 },
                new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferRead },
                new ComputeBindingDesc { Type = ComputeBindingType.SampledTexture },
                new ComputeBindingDesc { Type = ComputeBindingType.DepthTexture },
                new ComputeBindingDesc { Type = ComputeBindingType.SampledTexture },
                new ComputeBindingDesc { Type = ComputeBindingType.SampledTexture },
                new ComputeBindingDesc { Type = ComputeBindingType.SampledTexture },
                new ComputeBindingDesc { Type = ComputeBindingType.SampledTexture },
                new ComputeBindingDesc { Type = ComputeBindingType.SampledTexture },
                new ComputeBindingDesc { Type = ComputeBindingType.SampledTexture },
                new ComputeBindingDesc { Type = ComputeBindingType.StorageTextureWrite, StorageFormat = ComputeStorageFormat.Rgba16Float },
            },
        });
        if (_kernel == null) return false;
        _frameBuffer = g.CreateStorageBuffer(FrameVectorCount * 16);
        // Without additive full-screen compositing on the backend there is no point registering at
        // all: the reflection would be computed but never drawn, needlessly splitting the scene pass
        // (the split predicate just checks whether the delegate is non-null) and costing an extra
        // full-screen dispatch.
        _composite = g.CreateSsrCompositePipeline();
        if (_frameBuffer == null || _composite == null)
        {
            Dispose();
            return false;
        }
        _graphics = g;
        _active = this;
        FrameSchedule.PrepareSsrComposite = PrepareComposite;
        FrameSchedule.RenderSsrComposite = DrawComposite;
        Allocate(g, color.Desc.Width, color.Desc.Height);
        return true;
    }

    void Allocate(IGraphics g, uint width, uint height)
    {
        _width = width;
        _height = height;
        _params = new Vector4(width, height, 0f, 0f);
        g.CreateComputeTexture(TextureName, width, height, ComputeStorageFormat.Rgba16Float);
        // The slot table corresponds one-to-one with the binding declarations (numbered from 0 after
        // Params): 0 frame buffer, 1 scene color, 2 depth, 3 normals, 4..7 pyramid, 8 sky LUT,
        // 9 output. Built only once: the texture name is a constant, and on a size change
        // CreateComputeTexture recreates in place, so bindings referring to the same name do not need
        // to be swapped.
        _resources[0] = _frameBuffer!;
        _resources[1] = FrameSchedule.SceneColor!;
        _resources[2] = FrameSchedule.SceneDepth!;
        _resources[3] = FrameSchedule.SceneNormal!;
        for (int level = 0; level < HiZEffect.LevelCount; level++)
            _resources[4 + level] = HiZEffect.LevelName(level);
        _resources[9] = TextureName;
    }

    public override void OnResize(IGraphics g)
    {
        if (!ReferenceEquals(g, _graphics)) return;
        var color = FrameSchedule.SceneColor;
        if (color == null || color.Desc.Width == 0 || color.Desc.Height == 0) return;
        Allocate(g, color.Desc.Width, color.Desc.Height);
    }

    public override void Record(IGraphics g)
    {
        _ready = false;
        // Retract the publication first each frame: the composite pass should only be emitted after
        // the kernel has actually written successfully.
        FrameSchedule.SsrOutputReady = false;
        if (!ReferenceEquals(g, _graphics) || _kernel == null || _frameBuffer == null || _composite == null) return;
        if (!HiZEffect.Ready || RenderQuality.Current.ScreenSpaceReflections != SsrMode.HiZ) return;
        // Without the Sky-View LUT there is no reflSky to subtract: the kernel computes the delta of
        // "replaced by the hit point", and without the base term it could only compute "add a net
        // reflection", which is exactly the ghosting that round A was meant to fix. In that case the
        // reflection weight the main shader writes is also a full screen of 0, so just skip it and let
        // the image keep the sky + ground reflection the main shader already computed.
        if (FrameSchedule.SkyViewTexture is not string skyView || !FrameSchedule.LightingSnapshotValid) return;
        var color = FrameSchedule.SceneColor;
        var depth = FrameSchedule.SceneDepth;
        var normal = FrameSchedule.SceneNormal;
        if (color == null || depth == null || normal == null) return;
        if (color.Desc.Width != _width || color.Desc.Height != _height) return;
        var vp = FrameSchedule.UnjitteredRenderViewProjection;
        if (!Matrix4x4.Invert(vp, out var inverse)) return;
        var camera = RenderView.Current.Camera;
        float tanHalf = MathF.Tan(camera.FovY * 0.5f);
        float aspect = camera.Aspect > 0f ? camera.Aspect : (float)_width / _height;
        if (!(tanHalf > 0f) || !(aspect > 0f) || !(camera.Far > camera.Near)) return;

        // Row-order writes: 0..3 VP, 4..7 inverse VP, 8..11 camera and reconstruction params, 12 march params.
        MemoryMarshal.Cast<Matrix4x4, Vector4>(MemoryMarshal.CreateReadOnlySpan(ref vp, 1)).CopyTo(_frame);
        MemoryMarshal.Cast<Matrix4x4, Vector4>(MemoryMarshal.CreateReadOnlySpan(ref inverse, 1)).CopyTo(_frame.AsSpan(4));
        _frame[8] = new Vector4(camera.Position, 1f);
        _frame[9] = new Vector4(_width, _height, 1f / _width, 1f / _height);
        _frame[10] = new Vector4(camera.Near, camera.Far, tanHalf, aspect);
        _frame[11] = new Vector4(MathF.Max(0.01f, RenderQuality.Current.SsrMaxDistance),
            MathF.Max(0.01f, RenderQuality.Current.SsrThickness), RenderQuality.Current.SsrIntensity,
            FrameSchedule.SkyViewTexture != null ? 1f : 0f);
        _frame[12] = new Vector4((float)(FrameSchedule.FrameIndex & 0xFFFFUL), CoarseSteps, RefineSteps, 0f);
        // 13 = reflectionParams0 (ground radiance xyz + ground height w), 14 = sky intensity and publish slot.
        // The frame buffer already had three spare rows, so all the external inputs reflSky needs fit
        // exactly, with no need to grow it.
        _frame[13] = FrameSchedule.LightingSnapshot.ReflectionParams0;
        _frame[14] = new Vector4(FrameSchedule.LightingSnapshot.EnvParams.X, 1f, 0f, 0f);
        // The sky LUT only exists after registration (SkyAtmosphere registers later than this effect),
        // so resolve the name each frame here.
        _resources[8] = skyView;
        if (!g.TryUpdateStorageBuffer(_frameBuffer, MemoryMarshal.AsBytes(_frame.AsSpan()))) return;
        if (!g.TryDispatchCompute(new ComputeDispatchArgs
        {
            Kernel = _kernel,
            Params = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref _params, 1)),
            Resources = _resources,
            GroupsX = (_width + 7) / 8,
            GroupsY = (_height + 7) / 8,
            GroupsZ = 1,
        })) return;
        _ready = true;
        FrameSchedule.SsrOutputReady = true;
    }

    /// <summary>Called by the scheduler before <see cref="RenderPassId.SsrComposite"/> opens; false means no compositing this frame.</summary>
    bool PrepareComposite(IGraphics g) => _composite != null && FrameSchedule.SceneColor is { } color
        && _composite.Prepare(new SsrCompositeInputs(color, TextureName));

    /// <summary>Called exactly once inside that pass; on composite failure the scene stays as-is (reflection just loses its delta).</summary>
    bool DrawComposite(IGraphics g) => _composite?.Draw() ?? false;

    Vector4 _params;

    public override void OnUnregistered() => Dispose();

    public void Dispose()
    {
        if (_unregistering) return;
        _unregistering = true;
        var graphics = _graphics;
        _graphics = null;
        _ready = false;
        if (graphics != null) FrameSchedule.UnregisterCompute(this);
        // The delegates must be fully unhooked: the split predicate only checks whether a delegate is
        // non-null, so leaving them behind would needlessly split the scene pass.
        if (FrameSchedule.PrepareSsrComposite == PrepareComposite) FrameSchedule.PrepareSsrComposite = null;
        if (FrameSchedule.RenderSsrComposite == DrawComposite) FrameSchedule.RenderSsrComposite = null;
        FrameSchedule.SsrOutputReady = false;
        _composite?.Dispose();
        _composite = null;
        _kernel?.Dispose();
        _kernel = null;
        _frameBuffer?.Dispose();
        _frameBuffer = null;
        Array.Clear(_resources);
        if (ReferenceEquals(_active, this)) _active = null;
        _unregistering = false;
    }

    // Shader sources (single source of truth; slots follow the class-level binding
    // convention; workgroup is fixed at 8x8x1).

    /// <summary>D3D12 cs_5_0 (fxc; single exit avoids X4000).</summary>
    /// <remarks>
    /// Cannot be a const: the step counts in the source are int constants concatenated into the
    /// string, and "string + int" is not a compile-time constant.
    /// </remarks>
    static readonly string SourceHlsl = @"
cbuffer SsrParams : register(b0)
{
    float uDstWidth;
    float uDstHeight;
    float uPad0;
    float uPad1;
};

ByteAddressBuffer uFrame : register(t0);
Texture2D<float4> uScene : register(t1);
SamplerState uLinearClamp : register(s0);
Texture2D<float> uDepth : register(t2);
Texture2D<float4> uNormal : register(t3);
Texture2D<float> uHz0 : register(t4);
Texture2D<float> uHz1 : register(t5);
Texture2D<float> uHz2 : register(t6);
Texture2D<float> uHz3 : register(t7);
Texture2D<float4> uSkyView : register(t8);
RWTexture2D<float4> uOutput : register(u0);

// The frame buffer goes through a ByteAddressBuffer: Load4 offsets are in bytes, so i * 16 is the
// float4 index i.
float4 FrameF(uint i)
{
    return asfloat(uFrame.Load4(i * 16u));
}

// Row-order matrix: same shape as the other three backends; never write mul(M, p) -- System.Numerics
// stores rows.
float4 Transform(float4 p, uint offset)
{
    return p.x * FrameF(offset) + p.y * FrameF(offset + 1) + p.z * FrameF(offset + 2) + p.w * FrameF(offset + 3);
}

float3 OctDecode(float2 e)
{
    float3 n = float3(e, 1.0 - abs(e.x) - abs(e.y));
    if (n.z < 0.0) n.xy = (1.0 - abs(n.yx)) * float2(n.x >= 0.0 ? 1.0 : -1.0, n.y >= 0.0 ? 1.0 : -1.0);
    return normalize(n);
}

// View-space forward depth (LH + [0,1]), same origin as the GTAO reconstruction formula.
float Linearize(float d)
{
    float near = FrameF(10u).x, far = FrameF(10u).y;
    return near * far / (far - d * (far - near));
}

// Verbatim same shape as the main PS's SampleSkyViewRadiance: the horizon lands at uv.y = 0.5,
// zenith 0, nadir 1. This LUT is the sole source of sky radiance, and the kernel must use the same
// UV mapping, otherwise the delta will not line up.
float3 SkyRadiance(float3 dir)
{
    float2 uv;
    uv.x = atan2(dir.x, -dir.z) * 0.15915494309 + 0.5;
    uv.y = 0.5 - 0.5 * sign(dir.y) * sqrt(abs(dir.y));
    return uSkyView.SampleLevel(uLinearClamp, uv, 0.0).rgb;
}

// The sky reflection term the main PS originally added for this pixel (the value after A+B). The
// delta is 'replaced by the hit point', so it must know what is being replaced -- adding the hit
// point's radiance directly would count the sky energy twice.
float3 ReflectedSky(float3 P, float3 R)
{
    float3 lut = SkyRadiance(R) * FrameF(14u).x;
    float aboveGround = step(FrameF(13u).w, P.y);
    float horizonW = max(smoothstep(-0.09, 0.09, R.y), 1.0 - aboveGround);
    return lerp(FrameF(13u).xyz, lut, horizonW);
}

// Single exit + explicit .x: fxc 4.x misreports X4000 (use of potentially uninitialized variable)
// for the flow analysis of 'templated SampleLevel return implicitly converted to float + multiple
// returns'.
float HiZ(float2 uv, int level)
{
    float value;
    if (level <= 0) value = uHz0.SampleLevel(uLinearClamp, uv, 0.0).x;
    else if (level == 1) value = uHz1.SampleLevel(uLinearClamp, uv, 0.0).x;
    else if (level == 2) value = uHz2.SampleLevel(uLinearClamp, uv, 0.0).x;
    else value = uHz3.SampleLevel(uLinearClamp, uv, 0.0).x;
    return value;
}

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x < (uint)uDstWidth && id.y < (uint)uDstHeight)
    {
        float2 size = float2(uDstWidth, uDstHeight);
        float2 uv = (float2(id.xy) + 0.5) / size;
        float4 result = float4(0.0, 0.0, 0.0, 0.0);
        float depth = uDepth.Load(int3(id.xy, 0));

        // Sky and background produce no reflection: the depth sits at the far plane, and the normal
        // target has no usable surface either.
        if (depth < 1.0)
        {
            float4 packed = uNormal.SampleLevel(uLinearClamp, uv, 0.0);
            float roughness = saturate(packed.z);
            // w is the reflection weight the main PS computed (F0's Rec.709 luminance x ao). The
            // envCube path writes 0; for such pixels the base term is not sky and the delta is
            // undefined, so nothing should come out here either.
            float weight = saturate(packed.w);
            float3 N = OctDecode(packed.xy);

            // Screen-space reflection on a rough surface smears into a blur anyway, and is easily
            // amplified into noise by single-pixel false hits; the ambient energy of rough surfaces is
            // carried by the SH9 diffuse term, so emit no reflection here.
            if (roughness < 0.6 && weight > 0.0)
            {
                // World position reconstruction: UV Y points down, so the NDC Y must be flipped back.
                float2 ndc = float2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
                float4 world = Transform(float4(ndc, depth, 1.0), 4u);
                float3 P = world.xyz / world.w;
                float3 V = normalize(FrameF(8u).xyz - P);
                float3 R = reflect(-V, N);
                // Same roughness weight as the main PS's B term: the hit point replaces exactly this
                // share of specular energy.
                float mirrorW = (1.0 - roughness) * (1.0 - roughness);

                // Offset along the normal: without it the ray immediately hits the plane it lies on
                // and the whole screen reflects the same color.
                float bias = max(0.02, length(FrameF(8u).xyz - P) * 0.002);
                float3 origin = P + N * bias + R * bias;
                float maxDistance = FrameF(11u).x;
                float4 clip0 = Transform(float4(origin, 1.0), 0u);
                float4 clip1 = Transform(float4(origin + R * maxDistance, 1.0), 0u);

                // Where the ray cannot reach (behind the camera) there is no screen information to
                // sample, so shorten it to just before the near plane.
                float endT = 1.0;
                if (clip1.w <= 0.001)
                {
                    float denom = clip1.w - clip0.w;
                    endT = denom < -0.0001 ? saturate(-clip0.w / denom) : 0.0;
                    clip1 = Transform(float4(origin + R * (maxDistance * endT), 1.0), 0u);
                }

                if (clip0.w > 0.001 && clip1.w > 0.001 && endT > 0.001)
                {
                    float2 uv0 = clip0.xy / clip0.w * float2(0.5, -0.5) + 0.5;
                    float2 uv1 = clip1.xy / clip1.w * float2(0.5, -0.5) + 0.5;
                    float z0 = clip0.z / clip0.w;
                    float z1 = clip1.z / clip1.w;
                    float2 delta = uv1 - uv0;

                    // How many pixels each step spans decides which pyramid level to query: short rays
                    // use a finer level, otherwise a coarse block would never register a hit.
                    float pixels = length(delta * size) / (float)" + CoarseSteps + @";
                    int level = 3;
                    if (pixels < 12.0) level = 2;
                    if (pixels < 6.0) level = 1;
                    if (pixels < 3.0) level = 0;

                    float2 stepUv = delta / (float)" + CoarseSteps + @";
                    float2 coarse = uv0;
                    float coarseZ = z0;
                    [loop]
                    for (int i = 1; i <= " + CoarseSteps + @"; i++)
                    {
                        float2 next = uv0 + delta * ((float)i / (float)" + CoarseSteps + @");
                        float nextZ = lerp(z0, z1, (float)i / (float)" + CoarseSteps + @");
                        // Off screen means stop: information outside the screen does not exist, and
                        // continuing only samples the clamped edge pixels.
                        if (next.x < 0.0 || next.x > 1.0 || next.y < 0.0 || next.y > 1.0) break;
                        float tile = HiZ(next, level);
                        if (nextZ < tile)
                        {
                            coarse = next;
                            coarseZ = nextZ;
                            continue;
                        }
                        // The ray passed this block's nearest surface: refine point by point within
                        // (previous node, current node) to approach the true intersection.
                        float2 from = coarse;
                        float fromZ = coarseZ;
                        float2 to = next;
                        float toZ = nextZ;
                        [loop]
                        for (int j = 1; j <= " + RefineSteps + @"; j++)
                        {
                            float t = (float)j / (float)" + RefineSteps + @";
                            float2 suv = lerp(from, to, t);
                            float rayZ = lerp(fromZ, toZ, t);
                            int2 coord = clamp(int2(suv * size), int2(0, 0), int2((int)uDstWidth - 1, (int)uDstHeight - 1));
                            float sceneD = uDepth.Load(int3(coord, 0));
                            if (sceneD >= 1.0) continue;
                            // Thickness testing happens in linear depth: NDC depth compresses with
                            // distance, so a single threshold differs by orders of magnitude near and far.
                            float rayView = Linearize(rayZ);
                            float sceneView = Linearize(sceneD);
                            if (rayView >= sceneView && rayView - sceneView <= FrameF(11u).y)
                            {
                                float2 hitUv = saturate(suv);
                                float edge = min(min(hitUv.x, 1.0 - hitUv.x), min(hitUv.y, 1.0 - hitUv.y));
                                float fade = saturate(edge / 0.06);
                                fade *= saturate(1.0 - (float)i / (float)" + CoarseSteps + @");
                                // The delta is written here rather than at the composite side: the hit
                                // point's radiance minus this pixel's original sky term, then scaled by
                                // (1-r)^2, the reflection weight and the confidence -- so compositing is
                                // left with just one additive draw.
                                float3 hitRadiance = uScene.SampleLevel(uLinearClamp, hitUv, 0.0).rgb;
                                result = float4((hitRadiance - ReflectedSky(P, R)) * (mirrorW * weight * fade), 0.0);
                                break;
                            }
                        }
                        break;
                    }
                }
            }
        }
        uOutput[id.xy] = result;
    }
}
";

    /// <summary>Vulkan GLSL 450 (glslang -> SPIR-V; Params use push_constant, binding follows declaration order).</summary>
    static readonly string SourceGlsl = @"#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(push_constant) uniform SsrParams
{
    float uDstWidth;
    float uDstHeight;
    float uPad0;
    float uPad1;
};

layout(binding = 1, std430) readonly buffer Frame { vec4 data[]; } uFrame;
layout(binding = 2) uniform sampler2D uScene;
layout(binding = 3) uniform sampler2D uDepth;
layout(binding = 4) uniform sampler2D uNormal;
layout(binding = 5) uniform sampler2D uHz0;
layout(binding = 6) uniform sampler2D uHz1;
layout(binding = 7) uniform sampler2D uHz2;
layout(binding = 8) uniform sampler2D uHz3;
layout(binding = 9) uniform sampler2D uSkyView;
layout(binding = 10, rgba16f) uniform writeonly image2D uOutput;

vec4 Transform(vec4 p, int offset)
{
    return p.x * uFrame.data[offset] + p.y * uFrame.data[offset + 1] + p.z * uFrame.data[offset + 2] + p.w * uFrame.data[offset + 3];
}

vec3 OctDecode(vec2 e)
{
    vec3 n = vec3(e, 1.0 - abs(e.x) - abs(e.y));
    if (n.z < 0.0) n.xy = (1.0 - abs(n.yx)) * vec2(n.x >= 0.0 ? 1.0 : -1.0, n.y >= 0.0 ? 1.0 : -1.0);
    return normalize(n);
}

float Linearize(float d)
{
    float near = uFrame.data[10].x, far = uFrame.data[10].y;
    return near * far / (far - d * (far - near));
}

// Verbatim same shape as the main PS's SampleSkyViewRadiance: the horizon lands at uv.y = 0.5,
// zenith 0, nadir 1.
vec3 SkyRadiance(vec3 dir)
{
    vec2 uv;
    uv.x = atan(dir.x, -dir.z) * 0.15915494309 + 0.5;
    uv.y = 0.5 - 0.5 * sign(dir.y) * sqrt(abs(dir.y));
    return textureLod(uSkyView, uv, 0.0).rgb;
}

// The sky reflection term the main PS originally added for this pixel (the value after A+B); the
// delta must subtract it.
vec3 ReflectedSky(vec3 P, vec3 R)
{
    vec3 lut = SkyRadiance(R) * uFrame.data[14].x;
    float aboveGround = step(uFrame.data[13].w, P.y);
    float horizonW = max(smoothstep(-0.09, 0.09, R.y), 1.0 - aboveGround);
    return mix(uFrame.data[13].xyz, lut, horizonW);
}

float HiZ(vec2 uv, int level)
{
    if (level <= 0) return textureLod(uHz0, uv, 0.0).r;
    if (level == 1) return textureLod(uHz1, uv, 0.0).r;
    if (level == 2) return textureLod(uHz2, uv, 0.0).r;
    return textureLod(uHz3, uv, 0.0).r;
}

void main()
{
    uvec2 id = gl_GlobalInvocationID.xy;
    if (id.x < uint(uDstWidth) && id.y < uint(uDstHeight))
    {
        vec2 size = vec2(uDstWidth, uDstHeight);
        vec2 uv = (vec2(id) + 0.5) / size;
        vec4 result = vec4(0.0);
        float depth = texelFetch(uDepth, ivec2(id), 0).r;

        if (depth < 1.0)
        {
            vec4 packed = textureLod(uNormal, uv, 0.0);
            float roughness = clamp(packed.z, 0.0, 1.0);
            float weight = clamp(packed.w, 0.0, 1.0);
            vec3 N = OctDecode(packed.xy);

            if (roughness < 0.6 && weight > 0.0)
            {
                vec2 ndc = vec2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
                vec4 world = Transform(vec4(ndc, depth, 1.0), 4);
                vec3 P = world.xyz / world.w;
                vec3 V = normalize(uFrame.data[8].xyz - P);
                vec3 R = reflect(-V, N);
                float mirrorW = (1.0 - roughness) * (1.0 - roughness);

                float bias = max(0.02, length(uFrame.data[8].xyz - P) * 0.002);
                vec3 origin = P + N * bias + R * bias;
                float maxDistance = uFrame.data[11].x;
                vec4 clip0 = Transform(vec4(origin, 1.0), 0);
                vec4 clip1 = Transform(vec4(origin + R * maxDistance, 1.0), 0);

                float endT = 1.0;
                if (clip1.w <= 0.001)
                {
                    float denom = clip1.w - clip0.w;
                    endT = denom < -0.0001 ? clamp(-clip0.w / denom, 0.0, 1.0) : 0.0;
                    clip1 = Transform(vec4(origin + R * (maxDistance * endT), 1.0), 0);
                }

                if (clip0.w > 0.001 && clip1.w > 0.001 && endT > 0.001)
                {
                    vec2 uv0 = clip0.xy / clip0.w * vec2(0.5, -0.5) + 0.5;
                    vec2 uv1 = clip1.xy / clip1.w * vec2(0.5, -0.5) + 0.5;
                    float z0 = clip0.z / clip0.w;
                    float z1 = clip1.z / clip1.w;
                    vec2 delta = uv1 - uv0;

                    float pixels = length(delta * size) / float(" + CoarseSteps + @");
                    int level = 3;
                    if (pixels < 12.0) level = 2;
                    if (pixels < 6.0) level = 1;
                    if (pixels < 3.0) level = 0;

                    vec2 coarse = uv0;
                    float coarseZ = z0;
                    for (int i = 1; i <= " + CoarseSteps + @"; i++)
                    {
                        float t = float(i) / float(" + CoarseSteps + @");
                        vec2 next = uv0 + delta * t;
                        float nextZ = mix(z0, z1, t);
                        if (next.x < 0.0 || next.x > 1.0 || next.y < 0.0 || next.y > 1.0) break;
                        float tile = HiZ(next, level);
                        if (nextZ < tile)
                        {
                            coarse = next;
                            coarseZ = nextZ;
                            continue;
                        }
                        vec2 from = coarse;
                        float fromZ = coarseZ;
                        for (int j = 1; j <= " + RefineSteps + @"; j++)
                        {
                            float tj = float(j) / float(" + RefineSteps + @");
                            vec2 suv = mix(from, next, tj);
                            float rayZ = mix(fromZ, nextZ, tj);
                            ivec2 coord = clamp(ivec2(suv * size), ivec2(0), ivec2(int(uint(uDstWidth)) - 1, int(uint(uDstHeight)) - 1));
                            float sceneD = texelFetch(uDepth, coord, 0).r;
                            if (sceneD >= 1.0) continue;
                            float rayView = Linearize(rayZ);
                            float sceneView = Linearize(sceneD);
                            if (rayView >= sceneView && rayView - sceneView <= uFrame.data[11].y)
                            {
                                vec2 hitUv = clamp(suv, vec2(0.0), vec2(1.0));
                                float edge = min(min(hitUv.x, 1.0 - hitUv.x), min(hitUv.y, 1.0 - hitUv.y));
                                float fade = clamp(edge / 0.06, 0.0, 1.0);
                                fade *= clamp(1.0 - float(i) / float(" + CoarseSteps + @"), 0.0, 1.0);
                                vec3 hitRadiance = textureLod(uScene, hitUv, 0.0).rgb;
                                result = vec4((hitRadiance - ReflectedSky(P, R)) * (mirrorW * weight * fade), 0.0);
                                break;
                            }
                        }
                        break;
                    }
                }
            }
        }
        imageStore(uOutput, ivec2(id), result);
    }
}
";

    /// <summary>Metal MSL kernel.</summary>
    static readonly string SourceMsl = @"
#include <metal_stdlib>
using namespace metal;

struct SsrParams
{
    float uDstWidth;
    float uDstHeight;
    float uPad0;
    float uPad1;
};

float4 Transform(const device float4* frame, float4 p, int offset)
{
    return p.x * frame[offset] + p.y * frame[offset + 1] + p.z * frame[offset + 2] + p.w * frame[offset + 3];
}

float3 OctDecode(float2 e)
{
    float3 n = float3(e, 1.0 - abs(e.x) - abs(e.y));
    if (n.z < 0.0) n.xy = (1.0 - abs(n.yx)) * float2(n.x >= 0.0 ? 1.0 : -1.0, n.y >= 0.0 ? 1.0 : -1.0);
    return normalize(n);
}

float Linearize(const device float4* frame, float d)
{
    float near = frame[10].x, far = frame[10].y;
    return near * far / (far - d * (far - near));
}

// Verbatim same shape as the main PS's SampleSkyViewRadiance: the horizon lands at uv.y = 0.5,
// zenith 0, nadir 1.
float3 SkyRadiance(texture2d<float, access::sample> sky, sampler sm, float3 dir)
{
    float2 uv;
    uv.x = atan2(dir.x, -dir.z) * 0.15915494309 + 0.5;
    uv.y = 0.5 - 0.5 * sign(dir.y) * sqrt(abs(dir.y));
    return sky.sample(sm, uv, level(0.0)).rgb;
}

// The sky reflection term the main PS originally added for this pixel (the value after A+B); the
// delta must subtract it.
float3 ReflectedSky(texture2d<float, access::sample> sky, sampler sm, const device float4* frame, float3 P, float3 R)
{
    float3 lut = SkyRadiance(sky, sm, R) * frame[14].x;
    float aboveGround = step(frame[13].w, P.y);
    float horizonW = max(smoothstep(-0.09, 0.09, R.y), 1.0 - aboveGround);
    return mix(frame[13].xyz, lut, horizonW);
}

float HiZ(texture2d<float, access::sample> hz0, texture2d<float, access::sample> hz1,
    texture2d<float, access::sample> hz2, texture2d<float, access::sample> hz3,
    sampler sm, float2 uv, int level)
{
    if (level <= 0) return hz0.sample(sm, uv, level(0.0)).r;
    if (level == 1) return hz1.sample(sm, uv, level(0.0)).r;
    if (level == 2) return hz2.sample(sm, uv, level(0.0)).r;
    return hz3.sample(sm, uv, level(0.0)).r;
}

kernel void CSMain(
    constant SsrParams& params [[buffer(0)]],
    const device float4* frame [[buffer(1)]],
    texture2d<float, access::sample> uScene [[texture(0)]],
    depth2d<float, access::read> uDepth [[texture(1)]],
    texture2d<float, access::sample> uNormal [[texture(2)]],
    texture2d<float, access::sample> uHz0 [[texture(3)]],
    texture2d<float, access::sample> uHz1 [[texture(4)]],
    texture2d<float, access::sample> uHz2 [[texture(5)]],
    texture2d<float, access::sample> uHz3 [[texture(6)]],
    texture2d<float, access::sample> uSkyView [[texture(7)]],
    texture2d<float, access::write> uOutput [[texture(8)]],
    sampler uLinearClamp [[sampler(0)]],
    uint2 id [[thread_position_in_grid]])
{
    if (id.x < uint(params.uDstWidth) && id.y < uint(params.uDstHeight))
    {
        float2 size = float2(params.uDstWidth, params.uDstHeight);
        float2 uv = (float2(id) + 0.5) / size;
        float4 result = float4(0.0);
        float depth = uDepth.read(id);

        if (depth < 1.0)
        {
            float4 packed = uNormal.sample(uLinearClamp, uv, level(0.0));
            float roughness = saturate(packed.z);
            float weight = saturate(packed.w);
            float3 N = OctDecode(packed.xy);

            if (roughness < 0.6 && weight > 0.0)
            {
                float2 ndc = float2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
                float4 world = Transform(frame, float4(ndc, depth, 1.0), 4);
                float3 P = world.xyz / world.w;
                float3 V = normalize(frame[8].xyz - P);
                float3 R = reflect(-V, N);
                float mirrorW = (1.0 - roughness) * (1.0 - roughness);

                float bias = max(0.02, length(frame[8].xyz - P) * 0.002);
                float3 origin = P + N * bias + R * bias;
                float maxDistance = frame[11].x;
                float4 clip0 = Transform(frame, float4(origin, 1.0), 0);
                float4 clip1 = Transform(frame, float4(origin + R * maxDistance, 1.0), 0);

                float endT = 1.0;
                if (clip1.w <= 0.001)
                {
                    float denom = clip1.w - clip0.w;
                    endT = denom < -0.0001 ? saturate(-clip0.w / denom) : 0.0;
                    clip1 = Transform(frame, float4(origin + R * (maxDistance * endT), 1.0), 0);
                }

                if (clip0.w > 0.001 && clip1.w > 0.001 && endT > 0.001)
                {
                    float2 uv0 = clip0.xy / clip0.w * float2(0.5, -0.5) + 0.5;
                    float2 uv1 = clip1.xy / clip1.w * float2(0.5, -0.5) + 0.5;
                    float z0 = clip0.z / clip0.w;
                    float z1 = clip1.z / clip1.w;
                    float2 delta = uv1 - uv0;

                    float pixels = length(delta * size) / float(" + CoarseSteps + @");
                    int level = 3;
                    if (pixels < 12.0) level = 2;
                    if (pixels < 6.0) level = 1;
                    if (pixels < 3.0) level = 0;

                    float2 coarse = uv0;
                    float coarseZ = z0;
                    for (int i = 1; i <= " + CoarseSteps + @"; i++)
                    {
                        float t = float(i) / float(" + CoarseSteps + @");
                        float2 next = uv0 + delta * t;
                        float nextZ = mix(z0, z1, t);
                        if (next.x < 0.0 || next.x > 1.0 || next.y < 0.0 || next.y > 1.0) break;
                        float tile = HiZ(uHz0, uHz1, uHz2, uHz3, uLinearClamp, next, level);
                        if (nextZ < tile)
                        {
                            coarse = next;
                            coarseZ = nextZ;
                            continue;
                        }
                        float2 from = coarse;
                        float fromZ = coarseZ;
                        for (int j = 1; j <= " + RefineSteps + @"; j++)
                        {
                            float tj = float(j) / float(" + RefineSteps + @");
                            float2 suv = mix(from, next, tj);
                            float rayZ = mix(fromZ, nextZ, tj);
                            int2 coord = clamp(int2(suv * size), int2(0, 0), int2(int(params.uDstWidth) - 1, int(params.uDstHeight) - 1));
                            float sceneD = uDepth.read(uint2(coord));
                            if (sceneD >= 1.0) continue;
                            float rayView = Linearize(frame, rayZ);
                            float sceneView = Linearize(frame, sceneD);
                            if (rayView >= sceneView && rayView - sceneView <= frame[11].y)
                            {
                                float2 hitUv = saturate(suv);
                                float edge = min(min(hitUv.x, 1.0 - hitUv.x), min(hitUv.y, 1.0 - hitUv.y));
                                float fade = saturate(edge / 0.06);
                                fade *= saturate(1.0 - float(i) / float(" + CoarseSteps + @"));
                                float3 hitRadiance = uScene.sample(uLinearClamp, hitUv, level(0.0)).rgb;
                                result = float4((hitRadiance - ReflectedSky(uSkyView, uLinearClamp, frame, P, R))
                                    * (mirrorW * weight * fade), 0.0);
                                break;
                            }
                        }
                        break;
                    }
                }
            }
        }
        uOutput.write(result, id);
    }
}
";

    /// <summary>WebGPU WGSL. The storage format is pinned to rgba16float by the engine's narrow-format fallback chain.</summary>
    static readonly string SourceWgsl = @"
struct SsrParams
{
    uDstWidth : f32,
    uDstHeight : f32,
    uPad0 : f32,
    uPad1 : f32,
};

@group(0) @binding(0) var<uniform> params : SsrParams;
@group(0) @binding(1) var<storage, read> uFrame : array<vec4<f32>>;
@group(0) @binding(2) var uScene : texture_2d<f32>;
@group(0) @binding(3) var uDepth : texture_depth_2d;
@group(0) @binding(4) var uNormal : texture_2d<f32>;
@group(0) @binding(5) var uHz0 : texture_2d<f32>;
@group(0) @binding(6) var uHz1 : texture_2d<f32>;
@group(0) @binding(7) var uHz2 : texture_2d<f32>;
@group(0) @binding(8) var uHz3 : texture_2d<f32>;
@group(0) @binding(9) var uSkyView : texture_2d<f32>;
@group(0) @binding(10) var uOutput : texture_storage_2d<rgba16float, write>;
@group(0) @binding(15) var uLinearClamp : sampler;

fn Transform(p : vec4<f32>, offset : i32) -> vec4<f32>
{
    return p.x * uFrame[offset] + p.y * uFrame[offset + 1] + p.z * uFrame[offset + 2] + p.w * uFrame[offset + 3];
}

fn OctDecode(e : vec2<f32>) -> vec3<f32>
{
    var n = vec3<f32>(e, 1.0 - abs(e.x) - abs(e.y));
    if (n.z < 0.0) {
        n = vec3<f32>((1.0 - abs(n.yx)) * vec2<f32>(select(-1.0, 1.0, n.x >= 0.0), select(-1.0, 1.0, n.y >= 0.0)), n.z);
    }
    return normalize(n);
}

fn Linearize(d : f32) -> f32
{
    let near = uFrame[10].x;
    let far = uFrame[10].y;
    return near * far / (far - d * (far - near));
}

// Verbatim same shape as the main PS's SampleSkyViewRadiance: the horizon lands at uv.y = 0.5,
// zenith 0, nadir 1.
fn SkyRadiance(dir : vec3<f32>) -> vec3<f32>
{
    var uv : vec2<f32>;
    uv.x = atan2(dir.x, -dir.z) * 0.15915494309 + 0.5;
    uv.y = 0.5 - 0.5 * sign(dir.y) * sqrt(abs(dir.y));
    return textureSampleLevel(uSkyView, uLinearClamp, uv, 0.0).rgb;
}

// The sky reflection term the main PS originally added for this pixel (the value after A+B); the
// delta must subtract it.
fn ReflectedSky(P : vec3<f32>, R : vec3<f32>) -> vec3<f32>
{
    let lut = SkyRadiance(R) * uFrame[14].x;
    let aboveGround = step(uFrame[13].w, P.y);
    let horizonW = max(smoothstep(-0.09, 0.09, R.y), 1.0 - aboveGround);
    return mix(uFrame[13].xyz, lut, horizonW);
}

fn HiZ(uv : vec2<f32>, level : i32) -> f32
{
    if (level <= 0) { return textureSampleLevel(uHz0, uLinearClamp, uv, 0.0).r; }
    if (level == 1) { return textureSampleLevel(uHz1, uLinearClamp, uv, 0.0).r; }
    if (level == 2) { return textureSampleLevel(uHz2, uLinearClamp, uv, 0.0).r; }
    return textureSampleLevel(uHz3, uLinearClamp, uv, 0.0).r;
}

@compute @workgroup_size(8, 8, 1)
fn CSMain(@builtin(global_invocation_id) id : vec3<u32>)
{
    if (id.x < u32(params.uDstWidth) && id.y < u32(params.uDstHeight))
    {
        let size = vec2<f32>(params.uDstWidth, params.uDstHeight);
        let uv = (vec2<f32>(f32(id.x), f32(id.y)) + vec2<f32>(0.5)) / size;
        var result = vec4<f32>(0.0, 0.0, 0.0, 0.0);
        let coord = vec2<i32>(i32(id.x), i32(id.y));
        let depth = textureLoad(uDepth, coord, 0);

        if (depth < 1.0)
        {
            let packed = textureSampleLevel(uNormal, uLinearClamp, uv, 0.0);
            let roughness = saturate(packed.z);
            let weight = saturate(packed.w);
            let N = OctDecode(packed.xy);

            if (roughness < 0.6 && weight > 0.0)
            {
                let ndc = vec2<f32>(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
                let world = Transform(vec4<f32>(ndc, depth, 1.0), 4);
                let P = world.xyz / world.w;
                let V = normalize(uFrame[8].xyz - P);
                let R = reflect(-V, N);
                let mirrorW = (1.0 - roughness) * (1.0 - roughness);

                let bias = max(0.02, length(uFrame[8].xyz - P) * 0.002);
                let origin = P + N * bias + R * bias;
                let maxDistance = uFrame[11].x;
                let clip0 = Transform(vec4<f32>(origin, 1.0), 0);
                var clip1 = Transform(vec4<f32>(origin + R * maxDistance, 1.0), 0);

                var endT = 1.0;
                if (clip1.w <= 0.001)
                {
                    let denom = clip1.w - clip0.w;
                    endT = select(0.0, saturate(-clip0.w / denom), denom < -0.0001);
                    clip1 = Transform(vec4<f32>(origin + R * (maxDistance * endT), 1.0), 0);
                }

                if (clip0.w > 0.001 && clip1.w > 0.001 && endT > 0.001)
                {
                    let uv0 = clip0.xy / clip0.w * vec2<f32>(0.5, -0.5) + vec2<f32>(0.5);
                    let uv1 = clip1.xy / clip1.w * vec2<f32>(0.5, -0.5) + vec2<f32>(0.5);
                    let z0 = clip0.z / clip0.w;
                    let z1 = clip1.z / clip1.w;
                    let delta = uv1 - uv0;

                    let pixels = length(delta * size) / f32(" + CoarseSteps + @");
                    var level : i32 = 3;
                    if (pixels < 12.0) { level = 2; }
                    if (pixels < 6.0) { level = 1; }
                    if (pixels < 3.0) { level = 0; }

                    var coarse = uv0;
                    var coarseZ = z0;
                    for (var i : i32 = 1; i <= " + CoarseSteps + @"; i = i + 1)
                    {
                        let t = f32(i) / f32(" + CoarseSteps + @");
                        let next = uv0 + delta * t;
                        let nextZ = mix(z0, z1, t);
                        if (next.x < 0.0 || next.x > 1.0 || next.y < 0.0 || next.y > 1.0) { break; }
                        let tile = HiZ(next, level);
                        if (nextZ < tile)
                        {
                            coarse = next;
                            coarseZ = nextZ;
                            continue;
                        }
                        let from = coarse;
                        let fromZ = coarseZ;
                        for (var j : i32 = 1; j <= " + RefineSteps + @"; j = j + 1)
                        {
                            let tj = f32(j) / f32(" + RefineSteps + @");
                            let suv = mix(from, next, tj);
                            let rayZ = mix(fromZ, nextZ, tj);
                            let rc = clamp(vec2<i32>(suv * size), vec2<i32>(0, 0),
                                vec2<i32>(i32(u32(params.uDstWidth)) - 1, i32(u32(params.uDstHeight)) - 1));
                            let sceneD = textureLoad(uDepth, rc, 0);
                            if (sceneD >= 1.0) { continue; }
                            let rayView = Linearize(rayZ);
                            let sceneView = Linearize(sceneD);
                            if (rayView >= sceneView && rayView - sceneView <= uFrame[11].y)
                            {
                                let hitUv = clamp(suv, vec2<f32>(0.0), vec2<f32>(1.0));
                                let edge = min(min(hitUv.x, 1.0 - hitUv.x), min(hitUv.y, 1.0 - hitUv.y));
                                var fade = saturate(edge / 0.06);
                                fade = fade * saturate(1.0 - f32(i) / f32(" + CoarseSteps + @"));
                                let hitRadiance = textureSampleLevel(uScene, uLinearClamp, hitUv, 0.0).rgb;
                                result = vec4<f32>((hitRadiance - ReflectedSky(P, R)) * (mirrorW * weight * fade), 0.0);
                                break;
                            }
                        }
                        break;
                    }
                }
            }
        }
        textureStore(uOutput, coord, result);
    }
}
";
}
