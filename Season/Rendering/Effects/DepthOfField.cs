// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

/// <summary>
/// Engine built-in compute effect: F1 depth of field.
///
/// Behavior: one AfterScene gather kernel at full resolution in linear HDR, before tone mapping. It reads the
/// upstream scene image (motion blur's output when A1 is active, TAA's resolve when it is not, SceneColor otherwise),
/// this frame's SceneDepth, and the current view's lens - focus distance, aperture and focal length, all off
/// <see cref="Camera3D"/> - and writes one blurred texture that it publishes on
/// <see cref="ViewTargets.SceneColorOverride"/> for bloom, the Post uber pass and FinalBlit.
///
/// <para><b>Why it runs where it does.</b> Registration order is execution order, and the app registers this effect
/// after <see cref="MotionBlurEffect"/> and before <see cref="BloomEffect"/>. After motion blur because a lens blurs
/// the exposure-correct image, not the velocity field - a streak and a defocus circle are both image-space
/// convolutions and the cheaper-then-pricier order also lets the streak be gathered once instead of once per CoC
/// tap. Before bloom because bloom is a light-bleed model that must see the final, defocused highlights; the other
/// order would bloom the sharp image and then smear the glow into a soft-edged halo.</para>
///
/// <para><b>The override is a chain, not a slot.</b> Motion blur or TAA publishes first and this effect publishes
/// second, so the upstream is read through <see cref="ViewTargets.EffectiveSceneSource"/> inside Record and never
/// cached in Initialize; the field is written only after a successful dispatch; and when the effect stands down it
/// revokes only its own name, because clearing the field unconditionally would remove the upstream image from the
/// chain.</para>
///
/// <para><b>A lens, not a volume.</b> The blur is derived purely from the view's own camera optics: a pixel is sharp
/// where the linearized depth equals <see cref="Camera3D.FocusDistance"/> and softens on both sides at a rate set by
/// <see cref="Camera3D.Aperture"/> and <see cref="Camera3D.FocalLengthMM"/>. Because each view carries its own camera,
/// a rack focus is a runtime write to the camera and a second view (minimap, rear mirror) is soft only if its camera
/// is pointed with a shallow lens - which, together with <see cref="ViewFeature.DepthOfField"/>, is what keeps a
/// 192 px minimap from paying for a lens nobody set.</para>
///
/// <para><b>Acceptance.</b> Nothing at the focus distance -&gt; per-pixel identical to the upstream image. A near
/// object against a far background at a wide aperture -&gt; the background spreads while the object keeps its edge,
/// and the far background does not pull a sharp silhouette outward. Aperture stopped down -&gt; the whole frame
/// converges on the upstream image. <see cref="ViewFeature.DepthOfField"/> off on a view -&gt; that view allocates no
/// texture and records no dispatch.</para>
/// </summary>
public sealed class DepthOfFieldEffect : ComputeEffect
{
    /// <summary>Registered name of the blurred texture; per view it is scoped by <see cref="ViewTargets.Scope"/>.</summary>
    public const string TextureName = "compute://dof";

    /// <summary>
    /// True once <see cref="Initialize"/> produced a kernel, false again after <see cref="Dispose"/>. The panel reads this
    /// as the only honest answer to "is it running": the tier saying <see cref="DofMode.Gather"/> means the launch asked
    /// for the effect, while this flag means the registration actually went through - so a row that shows "On" describes a
    /// dispatch that exists, not a request that does.
    /// </summary>
    public static bool Ready { get; private set; }

    /// <summary>
    /// Gather taps per pixel on the golden-angle disc. A compiled-in constant rather than a quality knob, because a
    /// disc gather is not a long axis walk: the taps are spread over an area, so the visible return of adding more is
    /// a smoother disc edge and less banding at large radii, and 24 is the low end of the range real bokeh gathers
    /// use. The cost is linear in it and the radius is capped independently, so a quality tier can raise this later
    /// without touching the algorithm.
    /// </summary>
    const int Taps = 24;

    /// <summary>Radius in pixels below which the centre pixel is returned untouched. A sub-pixel blur circle cannot
    /// change a pixel's value by a visible amount, and skipping the loop keeps every in-focus pixel - the majority of
    /// a well-composed frame - at exactly the cost the scene chain paid before F1 existed.</summary>
    const float MinBlurRadiusPx = 0.75f;

    ComputeKernel? _kernel;

    sealed class ViewState
    {
        /// <summary>Scoped backend texture name for this view; null until the view first records.</summary>
        public string? Name;

        /// <summary>Creation size, used to detect a resize the callback missed.</summary>
        public uint Width, Height;

        /// <summary>Fixed slot table: 0 upstream scene, 1 depth, 2 output.</summary>
        public ComputeResourceRef[] Resources = [];
    }

    public override string Name => "dof";

    public override ComputePhase Phase => ComputePhase.AfterScene;

    /// <summary>Per view: reads that view's scene chain and publishes its override.</summary>
    public override ComputeScope Scope => ComputeScope.View;

    public override ViewFeature RequiredFeature => ViewFeature.DepthOfField;

    public override bool Initialize(IGraphics g)
    {
        // A retry must not inherit the previous attempt's answer.
        Ready = false;

        // F1 clause 1: the tier is fixed here, before any resource exists. Off registers nothing and allocates nothing.
        if (RenderQuality.Current.DepthOfField != DofMode.Gather) return false;

        // Shape dependency: the whole effect is one gather over the single-sampled HDR scene image and the explicit
        // SceneDepth the platform materialized for it. A missing target is a clean bypass, not a kernel built against
        // a resource array with a null in it - and the depth being present is exactly what RequiresDofSceneDepth
        // negotiated with the platform, so a null here means the platform declined.
        if (FrameSchedule.SceneColor == null || FrameSchedule.SceneDepth == null) return false;

        _kernel = g.CreateComputeKernel(new ComputeKernelDesc
        {
            Name = "depthOfField",
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
                // 12 floats: size and texel step, both clip planes, the three lens values, the vertical field of
                // view (so the shader can derive the sensor height), and the radius cap. 48 bytes, 16-byte aligned.
                new ComputeBindingDesc { Type = ComputeBindingType.Params, SizeInBytes = 48 },
                new ComputeBindingDesc { Type = ComputeBindingType.SampledTexture },
                new ComputeBindingDesc { Type = ComputeBindingType.DepthTexture },
                new ComputeBindingDesc
                {
                    Type = ComputeBindingType.StorageTextureWrite,
                    StorageFormat = ComputeStorageFormat.Rgba16Float,
                },
            },
        });
        if (_kernel == null) return false;

        // The blurred texture is created lazily per view, sized against that view's own SceneColor, so a 192 px
        // minimap owns a 192 px scratch target instead of a full-screen one.
        Ready = true;
        return true;
    }

    /// <summary>Creates this view's scoped blurred texture on first record; the name is stable afterwards.</summary>
    ViewState EnsureState(IGraphics g, ViewTargets targets)
    {
        var state = ViewState<ViewState>(targets);
        if (state.Name != null) return state;

        state.Name = targets.Scope(TextureName);
        state.Width = Math.Max(8u, targets.Width);
        state.Height = Math.Max(8u, targets.Height);
        g.CreateComputeTexture(state.Name, state.Width, state.Height, ComputeStorageFormat.Rgba16Float);

        state.Resources = new ComputeResourceRef[3];
        state.Resources[2] = state.Name;
        return state;
    }

    public override void OnViewResize(IGraphics g, ViewTargets targets)
    {
        if (targets.SceneColor == null) return;
        uint w = targets.Width, h = targets.Height;
        if (w == 0 || h == 0) return;

        var state = EnsureState(g, targets);
        if (w == state.Width && h == state.Height) return;

        // In-place recreation keeps the name and the C# object identity that downstream consumers hold.
        g.CreateComputeTexture(state.Name!, w, h, ComputeStorageFormat.Rgba16Float);
        state.Width = w;
        state.Height = h;
    }

    public override void Record(IGraphics g)
    {
        var targets = FrameSchedule.ActiveTargets;
        string name = targets.Scope(TextureName);

        // F1 clause 2: the tier switched off at runtime means this effect does nothing this frame - and "nothing"
        // must not include clearing the override. The upstream producer has already published its own value earlier
        // in this same phase, so leaving the field alone is what keeps its image on the chain. The one thing that
        // must be revoked is a stale name of ours, or the downstream chain would keep reading last frame's blur.
        if (RenderQuality.Current.DepthOfField != DofMode.Gather)
        {
            if (targets.SceneColorOverride == name) targets.SceneColorOverride = null;
            return;
        }

        // Direct-to-backbuffer rendering has no sampleable scene target, so there is nothing to gather from.
        uint w = targets.Width, h = targets.Height;
        if (w == 0 || h == 0) return;

        var source = targets.EffectiveSceneSource;

        // The upstream did not refresh this frame (for example TAA bypassed itself on a size mismatch), so the
        // field still resolves to our own last output. Sampling it would feed the effect its own previous frame;
        // revoke and let the downstream chain fall back to the raw scene image instead.
        if (source.TextureName == name)
        {
            targets.SceneColorOverride = null;
            return;
        }

        var state = EnsureState(g, targets);
        if (w != state.Width || h != state.Height)
        {
            // A missed resize callback. Recreate in place and skip this one frame: dispatching against a texture
            // of the wrong size would sample outside it, and a skipped frame is a clean degradation.
            g.CreateComputeTexture(state.Name!, w, h, ComputeStorageFormat.Rgba16Float);
            state.Width = w;
            state.Height = h;
            targets.SceneColorOverride = null;
            return;
        }

        // F1 clause 2: the radius cap is a fraction of screen height, converted here so the shader works in pixels. The
        // 0.05 ceiling (54 px at 1080p) is a hard bound on the author, not a second safety valve: past the 0.02 the tier
        // ships with, the fixed 24-tap spiral starts leaving gaps wider than the blur kernel itself, so a larger value buys
        // a bigger disc at the price of visible ringing rather than a smoother one.
        float maxCoc = Math.Clamp(RenderQuality.Current.DofMaxCoCRadius, 0f, 0.05f) * state.Height;

        // The lens comes off the view's own camera, read fresh every frame, which is what makes a rack focus a plain
        // runtime write with nothing to invalidate. Focus is clamped to at least the near plane, and the aperture and
        // focal length to values the circle-of-confusion formula is finite for, so a hand-set camera degrades the
        // image instead of producing a NaN that spreads through the whole gather.
        var camera = RenderView.Current.Camera;
        float focus = Math.Max(camera.FocusDistance, camera.Near);

        Span<float> p = stackalloc float[12];
        p[0] = state.Width;
        p[1] = state.Height;
        p[2] = 1f / state.Width;
        p[3] = 1f / state.Height;
        p[4] = camera.Near;
        p[5] = camera.Far;
        p[6] = focus;
        p[7] = Math.Max(camera.Aperture, 0.5f);
        p[8] = Math.Max(camera.FocalLengthMM, 1f);
        p[9] = camera.FovY;
        p[10] = maxCoc;
        p[11] = 0f;

        state.Resources[0] = source;
        state.Resources[1] = targets.SceneDepth!;

        g.DispatchCompute(new ComputeDispatchArgs
        {
            Kernel = _kernel!,
            Params = MemoryMarshal.AsBytes(p),
            Resources = state.Resources,
            GroupsX = (state.Width + 7) / 8,
            GroupsY = (state.Height + 7) / 8,
            GroupsZ = 1,
        });

        // Publish after the dispatch, so every downstream reader sees fully written content. The upstream producer
        // re-publishes its own value at the start of the next frame, which is what makes this chain self-healing
        // without any bookkeeping here.
        targets.SceneColorOverride = name;
    }

    public void Dispose()
    {
        // Revoke only our own publications, for every view - the same rule TAA and motion blur follow, so
        // unregistering this effect can never break an image another producer owns.
        FrameSchedule.ForEachTargets(targets =>
        {
            if (targets.SceneColorOverride == targets.Scope(TextureName))
                targets.SceneColorOverride = null;
        });
        ClearViewStates();
        _kernel?.Dispose();
        _kernel = null;
        Ready = false;
    }

    // Shader sources (single source of truth; slots follow the class-level binding convention;
    // workgroup is fixed at 8x8x1).
    //
    // Algorithm (F1 clause 4), identical on all four backends:
    //   1. The circle of confusion is the thin-lens formula, all four backends byte-for-byte the same algebra:
    //        z        = near*far / (far - d*(far-near))            linearize the hardware depth to world units
    //        c_mm     = f^2 / (N*(F-f)) * |z-F| / z                CoC diameter on the sensor, f/N/F in millimetres
    //        sensorH  = 2*f*tan(fovY/2)                            sensor height implied by the focal length and the FOV
    //        coc_px   = c_mm / sensorH * screenHeight              to pixels of the target
    //      The 1000 factor below is the world unit being the metre (Near/Far of 0.1/100 imply it) while the lens is
    //      authored in millimetres, which is the unit a photographer reads an f-number and a focal length in.
    //   2. A pixel whose CoC is under MinBlurRadiusPx is returned untouched - the in-focus majority never enters the
    //      loop, so F1 adds nothing to the cost of a frame that is mostly sharp.
    //   3. Otherwise a fixed 24-tap golden-angle disc, scaled by the centre pixel's own CoC, is averaged. Each tap is
    //      weighted by its own CoC divided by the radius, so an in-focus tap contributes almost nothing: that is what
    //      stops a sharp foreground from being pulled outward over the blurred background behind it, and it is the
    //      cheap one-pass stand-in for the depth-aware upsampling a full bokeh solution would do.
    //   4. Alpha is taken from the centre pixel rather than accumulated, exactly as TAA and motion blur treat it: the
    //      colour channels are what the lens spreads, and alpha carries scene semantics.

    /// <summary>D3D12 cs_5_0 (fxc; single exit avoids X4000). When Texture2D&lt;float4&gt; reads an
    /// rg16float SRV, missing components are filled as (0,1); taking .rgb yields colour.</summary>
    const string SourceHlsl = @"
cbuffer DepthOfFieldParams : register(b0)
{
    float uWidth;
    float uHeight;
    float uTexelX;
    float uTexelY;
    float uNear;
    float uFar;
    float uFocusDistance;
    float uAperture;
    float uFocalLength;
    float uFovY;
    float uMaxCoCRadius;
    float uPad;
};

Texture2D<float4> uScene : register(t0);
Texture2D<float> uDepth : register(t1);
SamplerState uLinearClamp : register(s0);
RWTexture2D<float4> uOutput : register(u0);

float CoCPixels(float hardwareDepth)
{
    float z = uNear * uFar / max(uFar - hardwareDepth * (uFar - uNear), 1e-6);
    float f = uFocalLength;
    float n = max(uAperture, 0.1);
    float F = uFocusDistance;
    float sensorH = max(2.0 * f * tan(0.5 * uFovY), 1e-3);
    float zm = z * 1000.0;
    float Fm = F * 1000.0;
    float cocMm = (f * f) / (n * max(Fm - f, 1e-3)) * (abs(zm - Fm) / max(zm, 1e-3));
    return min(cocMm / sensorH * uHeight, uMaxCoCRadius);
}

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x < (uint)uWidth && id.y < (uint)uHeight)
    {
        int2 px = int2(id.xy);
        float2 uv = (float2(id.xy) + 0.5) * float2(uTexelX, uTexelY);
        float4 center = uScene.SampleLevel(uLinearClamp, uv, 0.0);

        float radius = CoCPixels(uDepth.Load(int3(px, 0)));

        float3 sum = center.rgb;
        float wsum = 1.0;

        if (radius > 0.75)
        {
            float2 lo = float2(uTexelX, uTexelY) * 0.5;
            float2 hi = float2(1.0, 1.0) - lo;

            for (int i = 0; i < 24; i++)
            {
                float fi = (float)i;
                float a = fi * 2.39996323;
                float r = sqrt((fi + 0.5) / 24.0);
                float2 off = float2(cos(a), sin(a)) * (r * radius);
                float2 suv = clamp(uv + off * float2(uTexelX, uTexelY), lo, hi);
                int2 sp = clamp(int2(suv * float2(uWidth, uHeight)), int2(0, 0),
                    int2((int)uWidth - 1, (int)uHeight - 1));

                float w = saturate(CoCPixels(uDepth.Load(int3(sp, 0))) / radius);
                sum += uScene.SampleLevel(uLinearClamp, suv, 0.0).rgb * w;
                wsum += w;
            }
        }

        float3 color = (wsum > 1e-5) ? (sum / wsum) : center.rgb;
        uOutput[px] = float4(color, center.a);
    }
}
";

    /// <summary>Vulkan GLSL 450 (glslang compiles to SPIR-V at runtime; entry point is always main).
    /// The SampledTexture slot is bound as a CombinedImageSampler, and textureLod takes no derivatives, so it is
    /// legal inside the dynamic loop.</summary>
    const string SourceGlsl = @"#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(push_constant) uniform DepthOfFieldParams
{
    float uWidth;
    float uHeight;
    float uTexelX;
    float uTexelY;
    float uNear;
    float uFar;
    float uFocusDistance;
    float uAperture;
    float uFocalLength;
    float uFovY;
    float uMaxCoCRadius;
    float uPad;
};

layout(binding = 1) uniform sampler2D uScene;
layout(binding = 2) uniform sampler2D uDepth;
layout(binding = 3, rgba16f) uniform writeonly image2D uOutput;

float cocPixels(float hardwareDepth)
{
    float z = uNear * uFar / max(uFar - hardwareDepth * (uFar - uNear), 1e-6);
    float f = uFocalLength;
    float n = max(uAperture, 0.1);
    float F = uFocusDistance;
    float sensorH = max(2.0 * f * tan(0.5 * uFovY), 1e-3);
    float zm = z * 1000.0;
    float Fm = F * 1000.0;
    float cocMm = (f * f) / (n * max(Fm - f, 1e-3)) * (abs(zm - Fm) / max(zm, 1e-3));
    return min(cocMm / sensorH * uHeight, uMaxCoCRadius);
}

void main()
{
    uvec2 id = gl_GlobalInvocationID.xy;
    if (id.x < uint(uWidth) && id.y < uint(uHeight))
    {
        ivec2 px = ivec2(id);
        vec2 uv = (vec2(id) + 0.5) * vec2(uTexelX, uTexelY);
        vec4 center = textureLod(uScene, uv, 0.0);

        float radius = cocPixels(texelFetch(uDepth, px, 0).r);

        vec3 sum = center.rgb;
        float wsum = 1.0;

        if (radius > 0.75)
        {
            vec2 lo = vec2(uTexelX, uTexelY) * 0.5;
            vec2 hi = vec2(1.0) - lo;

            for (int i = 0; i < 24; i++)
            {
                float fi = float(i);
                float a = fi * 2.39996323;
                float r = sqrt((fi + 0.5) / 24.0);
                vec2 off = vec2(cos(a), sin(a)) * (r * radius);
                vec2 suv = clamp(uv + off * vec2(uTexelX, uTexelY), lo, hi);
                ivec2 sp = clamp(ivec2(suv * vec2(uWidth, uHeight)), ivec2(0),
                    ivec2(int(uWidth) - 1, int(uHeight) - 1));

                float w = clamp(cocPixels(texelFetch(uDepth, sp, 0).r) / radius, 0.0, 1.0);
                sum += textureLod(uScene, suv, 0.0).rgb * w;
                wsum += w;
            }
        }

        vec3 color = (wsum > 1e-5) ? (sum / wsum) : center.rgb;
        imageStore(uOutput, px, vec4(color, center.a));
    }
}
";

    /// <summary>Metal MSL kernel. SampledTexture provides sampler(0) for the one filtered fetch, and depth is a
    /// depth2d because that is the view the backend binds for a DepthTexture slot (the same shape TaaEffect uses).</summary>
    const string SourceMsl = @"
#include <metal_stdlib>
using namespace metal;

struct DepthOfFieldParams
{
    float uWidth;
    float uHeight;
    float uTexelX;
    float uTexelY;
    float uNear;
    float uFar;
    float uFocusDistance;
    float uAperture;
    float uFocalLength;
    float uFovY;
    float uMaxCoCRadius;
    float uPad;
};

float cocPixels(constant DepthOfFieldParams& params, float hardwareDepth)
{
    float z = params.uNear * params.uFar / max(params.uFar - hardwareDepth * (params.uFar - params.uNear), 1e-6);
    float f = params.uFocalLength;
    float n = max(params.uAperture, 0.1);
    float F = params.uFocusDistance;
    float sensorH = max(2.0 * f * tan(0.5 * params.uFovY), 1e-3);
    float zm = z * 1000.0;
    float Fm = F * 1000.0;
    float cocMm = (f * f) / (n * max(Fm - f, 1e-3)) * (abs(zm - Fm) / max(zm, 1e-3));
    return min(cocMm / sensorH * params.uHeight, params.uMaxCoCRadius);
}

kernel void CSMain(
    constant DepthOfFieldParams& params [[buffer(0)]],
    texture2d<float, access::sample> uScene [[texture(0)]],
    sampler uLinearClamp [[sampler(0)]],
    depth2d<float, access::read> uDepth [[texture(1)]],
    texture2d<float, access::write> uOutput [[texture(2)]],
    uint2 id [[thread_position_in_grid]])
{
    if (id.x < (uint)params.uWidth && id.y < (uint)params.uHeight)
    {
        float2 uv = (float2(id) + 0.5) * float2(params.uTexelX, params.uTexelY);
        float4 center = uScene.sample(uLinearClamp, uv, level(0.0));

        float radius = cocPixels(params, uDepth.read(id));

        float3 sum = center.rgb;
        float wsum = 1.0;

        if (radius > 0.75)
        {
            float2 lo = float2(params.uTexelX, params.uTexelY) * 0.5;
            float2 hi = float2(1.0) - lo;

            for (int i = 0; i < 24; i++)
            {
                float fi = (float)i;
                float a = fi * 2.39996323;
                float r = sqrt((fi + 0.5) / 24.0);
                float2 off = float2(cos(a), sin(a)) * (r * radius);
                float2 suv = clamp(uv + off * float2(params.uTexelX, params.uTexelY), lo, hi);
                uint2 sp = uint2(clamp(int2(suv * float2(params.uWidth, params.uHeight)),
                    int2(0, 0), int2((int)params.uWidth - 1, (int)params.uHeight - 1)));

                float w = saturate(cocPixels(params, uDepth.read(sp)) / radius);
                sum += uScene.sample(uLinearClamp, suv, level(0.0)).rgb * w;
                wsum += w;
            }
        }

        float3 color = (wsum > 1e-5) ? (sum / wsum) : center.rgb;
        uOutput.write(float4(color, center.a), id);
    }
}
";

    /// <summary>WebGPU WGSL (delivered through the interop layer; seasonWebGPU.js source is not included).
    /// The SampledTexture slot carries a sampler at @binding(15) as a layout placeholder, and every fetch uses an
    /// explicit level so it carries no uniform-control-flow requirement inside the dynamic loop.</summary>
    const string SourceWgsl = @"
struct DepthOfFieldParams
{
    uWidth : f32,
    uHeight : f32,
    uTexelX : f32,
    uTexelY : f32,
    uNear : f32,
    uFar : f32,
    uFocusDistance : f32,
    uAperture : f32,
    uFocalLength : f32,
    uFovY : f32,
    uMaxCoCRadius : f32,
    uPad : f32,
};

@group(0) @binding(0) var<uniform> params : DepthOfFieldParams;
@group(0) @binding(1) var uScene : texture_2d<f32>;
@group(0) @binding(2) var uDepth : texture_depth_2d;
@group(0) @binding(3) var uOutput : texture_storage_2d<rgba16float, write>;
@group(0) @binding(15) var uLinearClamp : sampler;

fn cocPixels(hardwareDepth : f32) -> f32
{
    let z = params.uNear * params.uFar / max(params.uFar - hardwareDepth * (params.uFar - params.uNear), 1e-6);
    let f = params.uFocalLength;
    let n = max(params.uAperture, 0.1);
    let F = params.uFocusDistance;
    let sensorH = max(2.0 * f * tan(0.5 * params.uFovY), 1e-3);
    let zm = z * 1000.0;
    let Fm = F * 1000.0;
    let cocMm = (f * f) / (n * max(Fm - f, 1e-3)) * (abs(zm - Fm) / max(zm, 1e-3));
    return min(cocMm / sensorH * params.uHeight, params.uMaxCoCRadius);
}

@compute @workgroup_size(8, 8, 1)
fn CSMain(@builtin(global_invocation_id) id : vec3<u32>)
{
    if (id.x < u32(params.uWidth) && id.y < u32(params.uHeight))
    {
        let px = vec2<i32>(i32(id.x), i32(id.y));
        let uv = (vec2<f32>(f32(id.x), f32(id.y)) + vec2<f32>(0.5))
            * vec2<f32>(params.uTexelX, params.uTexelY);
        let center = textureSampleLevel(uScene, uLinearClamp, uv, 0.0);

        let radius = cocPixels(textureLoad(uDepth, px, 0));

        var sum = center.rgb;
        var wsum = 1.0;

        if (radius > 0.75)
        {
            let lo = vec2<f32>(params.uTexelX, params.uTexelY) * 0.5;
            let hi = vec2<f32>(1.0) - lo;

            for (var i = 0; i < 24; i = i + 1)
            {
                let fi = f32(i);
                let a = fi * 2.39996323;
                let r = sqrt((fi + 0.5) / 24.0);
                let off = vec2<f32>(cos(a), sin(a)) * (r * radius);
                let suv = clamp(uv + off * vec2<f32>(params.uTexelX, params.uTexelY), lo, hi);
                let sp = clamp(vec2<i32>(suv * vec2<f32>(params.uWidth, params.uHeight)),
                    vec2<i32>(0, 0), vec2<i32>(i32(params.uWidth) - 1, i32(params.uHeight) - 1));

                let w = clamp(cocPixels(textureLoad(uDepth, sp, 0)) / radius, 0.0, 1.0);
                sum = sum + textureSampleLevel(uScene, uLinearClamp, suv, 0.0).rgb * w;
                wsum = wsum + w;
            }
        }

        var color = center.rgb;
        if (wsum > 1e-5)
        {
            color = sum / wsum;
        }
        textureStore(uOutput, px, vec4<f32>(color, center.a));
    }
}
";
}
