// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

/// <summary>
/// Engine built-in compute effect: A1 motion blur.
///
/// Behavior: one AfterScene gather kernel at full resolution in linear HDR, before tone mapping. It reads the
/// upstream scene image (TAA's resolve output when TAA is active, SceneColor otherwise), this frame's
/// SceneVelocity and SceneDepth, and writes one streak texture that it publishes on
/// <see cref="ViewTargets.SceneColorOverride"/> for bloom, the Post uber pass and FinalBlit.
///
/// <para><b>Why it runs where it does.</b> Registration order is execution order, and the app registers this
/// effect after <see cref="TaaEffect"/> and before <see cref="BloomEffect"/>. After TAA is the only correct side:
/// TAA resolves the jittered, reprojected image and reads SceneColor rather than the override, so blurring before
/// it would feed a smeared image into the history and compound it into permanent softening, and would also smear
/// the subpixel jitter along every streak. Before bloom because a highlight that was streaked has to be the
/// highlight bloom sees; the other order leaves a hard-edged glow floating over a blurred body.</para>
///
/// <para><b>The override is a chain, not a slot.</b> TAA publishes first and this effect publishes second, so the
/// upstream is read through <see cref="ViewTargets.EffectiveSceneSource"/> inside Record and never cached in
/// Initialize; the field is written only after a successful dispatch; and when the effect stands down it revokes
/// only its own name, because clearing the field unconditionally would remove the upstream TAA image from the
/// chain. A stale name of ours is revoked too - otherwise a shutter that was just set to zero would leave the
/// downstream chain sampling last frame's streak instead of this frame's scene.</para>
///
/// <para><b>What is deliberately not blurred.</b> Only geometry drawn into a scene pass that carries a velocity
/// attachment writes SceneVelocity. Water, the waterfall curtain, the river ribbon, decals, the Overlay pass and
/// every 2D/text path write none, so their velocity reads zero and they stay sharp. That is a property of the 2-3
/// tier this effect inherits rather than a defect here, and for text it is the desirable outcome.</para>
///
/// <para><b>Acceptance.</b> Camera and objects still -&gt; per-pixel identical to the upstream image, because the
/// velocity is exactly zero and every tap lands on the same texel. Panning -&gt; a streak trailing opposite to the
/// motion and no longer than <see cref="RenderQuality.MotionBlurMaxRadius"/>. A cut that goes through
/// Camera3D.ResetTemporal -&gt; one frame of zero velocity, so no full-screen smear. A silhouette -&gt; no bright
/// or dark fringe pulled across the depth break.</para>
/// </summary>
public sealed class MotionBlurEffect : ComputeEffect
{
    /// <summary>Registered name of the streak texture; per view it is scoped by <see cref="ViewTargets.Scope"/>.</summary>
    public const string TextureName = "compute://motionblur";

    /// <summary>
    /// Relative depth difference at which a gather sample stops contributing, as a fraction of the centre pixel's
    /// hardware depth.
    ///
    /// Working in hardware depth is what keeps this a single scalar: the mapping to view distance is monotonic,
    /// so a relative difference orders samples exactly like a linear depth ratio would, and no near/far planes
    /// have to be threaded through the parameters - the same argument the TAA velocity dilation makes. It stays a
    /// constant rather than a quality knob because it describes where a streak may cross a surface boundary,
    /// which is a property of the image and not of the hardware budget. Raising it stretches streaks over
    /// silhouettes; lowering it cuts them at the first contact edge.
    /// </summary>
    const float DepthThreshold = 0.12f;

    ComputeKernel? _kernel;

    sealed class ViewState
    {
        /// <summary>Scoped backend texture name for this view; null until the view first records.</summary>
        public string? Name;

        /// <summary>Creation size, used to detect a resize the callback missed.</summary>
        public uint Width, Height;

        /// <summary>Fixed slot table: 0 upstream scene, 1 velocity, 2 depth, 3 output.</summary>
        public ComputeResourceRef[] Resources = [];
    }

    public override string Name => "motionBlur";

    public override ComputePhase Phase => ComputePhase.AfterScene;

    /// <summary>Per view: reads that view's scene chain and publishes its override.</summary>
    public override ComputeScope Scope => ComputeScope.View;

    public override ViewFeature RequiredFeature => ViewFeature.MotionBlur;

    public override bool Initialize(IGraphics g)
    {
        // A1 clause 1: the tier is fixed here, before any resource exists. Off registers nothing and allocates nothing.
        if (!RenderQuality.Current.MotionBlur) return false;

        // Shape dependency: the whole effect is one gather over the 2-3 velocity buffer, and the depth fetch is
        // what stops the streak from crossing silhouettes. A missing target is a clean bypass, not a kernel built
        // against a resource array with a null in it.
        if (!RenderQuality.Current.MotionVectors) return false;
        if (FrameSchedule.SceneColor == null || FrameSchedule.SceneVelocity == null || FrameSchedule.SceneDepth == null)
            return false;

        _kernel = g.CreateComputeKernel(new ComputeKernelDesc
        {
            Name = "motionBlur",
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
                new ComputeBindingDesc { Type = ComputeBindingType.Params, SizeInBytes = 32 },
                new ComputeBindingDesc { Type = ComputeBindingType.SampledTexture },
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

        // The streak texture is created lazily per view, sized against that view's own SceneColor, so a 192 px
        // minimap owns a 192 px scratch target instead of a full-screen one.
        return true;
    }

    /// <summary>Creates this view's scoped streak texture on first record; the name is stable afterwards.</summary>
    ViewState EnsureState(IGraphics g, ViewTargets targets)
    {
        var state = ViewState<ViewState>(targets);
        if (state.Name != null) return state;

        state.Name = targets.Scope(TextureName);
        state.Width = Math.Max(8u, targets.Width);
        state.Height = Math.Max(8u, targets.Height);
        g.CreateComputeTexture(state.Name, state.Width, state.Height, ComputeStorageFormat.Rgba16Float);

        state.Resources = new ComputeResourceRef[4];
        state.Resources[3] = state.Name;
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

        // A1 clause 2: zero shutter (or the tier switched off at runtime) means this effect does nothing this
        // frame - and "nothing" must not include clearing the override. The upstream producer has already
        // published its own value earlier in this same phase, so leaving the field alone is what keeps TAA's
        // image on the chain. The one thing that must be revoked is a name of ours, or the downstream chain
        // would keep reading last frame's streak after the switch was turned off.
        float shutter = RenderQuality.Current.MotionBlur
            ? Math.Clamp(RenderQuality.Current.MotionBlurShutter, 0f, 1f)
            : 0f;
        if (shutter <= 0f)
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

        // Clause 1: the tap count is the kernel's loop bound and is clamped rather than trusted, so a hand-edited
        // Settings.json degrades the image instead of building a kernel the shaders were not written for.
        int taps = Math.Clamp(RenderQuality.Current.MotionBlurSamples, 4, 32);

        // Clause 2: the radius cap is a fraction of screen height, converted here so the shader works in pixels.
        float radius = Math.Clamp(RenderQuality.Current.MotionBlurMaxRadius, 0f, 0.5f) * state.Height;

        Span<float> p = stackalloc float[8];
        p[0] = state.Width;
        p[1] = state.Height;
        p[2] = 1f / state.Width;
        p[3] = 1f / state.Height;
        p[4] = shutter;
        p[5] = radius;
        p[6] = taps;
        p[7] = DepthThreshold;

        state.Resources[0] = source;
        state.Resources[1] = targets.SceneVelocity!;
        state.Resources[2] = targets.SceneDepth!;

        g.DispatchCompute(new ComputeDispatchArgs
        {
            Kernel = _kernel!,
            Params = MemoryMarshal.AsBytes(p),
            Resources = state.Resources,
            GroupsX = (state.Width + 7) / 8,
            GroupsY = (state.Height + 7) / 8,
            GroupsZ = 1,
        });

        // Publish after the dispatch, so every downstream reader sees fully written content. The upstream
        // producer re-publishes its own value at the start of the next frame, which is what makes this chain
        // self-healing without any bookkeeping here.
        targets.SceneColorOverride = name;
    }

    public void Dispose()
    {
        // Revoke only our own publications, for every view - the same rule TAA follows, so unregistering this
        // effect can never break an image another producer owns.
        FrameSchedule.ForEachTargets(targets =>
        {
            if (targets.SceneColorOverride == targets.Scope(TextureName))
                targets.SceneColorOverride = null;
        });
        ClearViewStates();
        _kernel?.Dispose();
        _kernel = null;
    }

    // Shader sources (single source of truth; slots follow the class-level binding convention;
    // workgroup is fixed at 8x8x1).
    //
    // Algorithm (A1 clause 3), identical on all four backends:
    //   1. Velocity points from the previous position to this one - the same convention TAA reprojects with - so the
    //      gather walks forwards along it: tap t reads the current image where the content that occupied this pixel at
    //      t = 1 sits now, and t = 0 lands on this pixel. The streak therefore trails behind the motion. Walking the
    //      other way pulls the current image from the future instead and drags a leading ghost out in front of
    //      everything that moves. Sampling symmetric around the centre would put the streak's midpoint behind the
    //      object that cast it.
    //   2. Shutter and the radius cap scale the whole velocity vector, never its components: a per-axis clamp
    //      would rotate the streak away from the direction the pixel actually moved, which reads as the image
    //      smearing sideways during a diagonal pan.
    //   3. Each tap is weighted by a depth bilateral term. A sample whose surface differs from the centre pixel's
    //      by more than DepthThreshold does not belong to the same exposure window - either it is foreground the
    //      streak would pull into the background, or background the foreground would dissolve into. The weight is
    //      symmetric on purpose; the asymmetric version only fixes one of those two fringes and doubles the other.
    //   4. If every tap is rejected the result is the centre pixel, not a normalised zero: that case means the
    //      streak immediately crossed a different surface, and the honest answer is "no blur here".
    //   5. Alpha is taken from the centre pixel rather than accumulated. The colour channels are what the exposure
    //      integrates; alpha carries scene semantics, and TAA treats it the same way.

    /// <summary>D3D12 cs_5_0 (fxc; single exit avoids X4000). When Texture2D&lt;float4&gt; reads an
    /// rg16float SRV, missing components are filled as (0,1); taking .xy yields velocity.</summary>
    const string SourceHlsl = @"
cbuffer MotionBlurParams : register(b0)
{
    float uWidth;
    float uHeight;
    float uTexelX;
    float uTexelY;
    float uShutter;
    float uMaxRadius;
    float uSampleCount;
    float uDepthThreshold;
};

Texture2D<float4> uScene : register(t0);
Texture2D<float4> uVelocity : register(t1);
Texture2D<float> uDepth : register(t2);
SamplerState uLinearClamp : register(s0);
RWTexture2D<float4> uOutput : register(u0);

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x < (uint)uWidth && id.y < (uint)uHeight)
    {
        int2 px = int2(id.xy);
        float2 uv = (float2(id.xy) + 0.5) * float2(uTexelX, uTexelY);
        float4 center = uScene.SampleLevel(uLinearClamp, uv, 0.0);
        float2 v = uVelocity.Load(int3(px, 0)).xy;

        float2 vPixels = v * float2(uWidth, uHeight) * uShutter;
        float len = length(vPixels);
        float k = (len > uMaxRadius) ? (uMaxRadius / max(len, 1e-6)) : 1.0;
        float2 delta = v * uShutter * k;

        float dc = max(uDepth.Load(int3(px, 0)), 1e-6);
        int taps = max((int)uSampleCount, 2);
        float2 lo = float2(uTexelX, uTexelY) * 0.5;
        float2 hi = float2(1.0, 1.0) - lo;

        float3 sum = 0.0;
        float wsum = 0.0;

        for (int i = 0; i < taps; i++)
        {
            float t = (float)i / (float)(taps - 1);
            float2 suv = clamp(uv + delta * t, lo, hi);
            int2 sp = clamp(int2(suv * float2(uWidth, uHeight)), int2(0, 0), int2((int)uWidth - 1, (int)uHeight - 1));

            float sd = uDepth.Load(int3(sp, 0));
            float rel = abs(dc - sd) / dc;
            float w = saturate(1.0 - rel / max(uDepthThreshold, 1e-6));

            sum += uScene.SampleLevel(uLinearClamp, suv, 0.0).rgb * w;
            wsum += w;
        }

        float3 color = (wsum > 1e-5) ? (sum / wsum) : center.rgb;
        uOutput[px] = float4(color, center.a);
    }
}
";

    /// <summary>Vulkan GLSL 450 (glslang compiles to SPIR-V at runtime; entry point is always main).
    /// The SampledTexture slot is bound as a CombinedImageSampler, and textureLod is the compute-stage
    /// equivalent of SampleLevel - it takes no derivatives, so it is legal inside the dynamic loop.</summary>
    const string SourceGlsl = @"#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(push_constant) uniform MotionBlurParams
{
    float uWidth;
    float uHeight;
    float uTexelX;
    float uTexelY;
    float uShutter;
    float uMaxRadius;
    float uSampleCount;
    float uDepthThreshold;
};

layout(binding = 1) uniform sampler2D uScene;
layout(binding = 2) uniform sampler2D uVelocity;
layout(binding = 3) uniform sampler2D uDepth;
layout(binding = 4, rgba16f) uniform writeonly image2D uOutput;

void main()
{
    uvec2 id = gl_GlobalInvocationID.xy;
    if (id.x < uint(uWidth) && id.y < uint(uHeight))
    {
        ivec2 px = ivec2(id);
        vec2 uv = (vec2(id) + 0.5) * vec2(uTexelX, uTexelY);
        vec4 center = textureLod(uScene, uv, 0.0);
        vec2 v = texelFetch(uVelocity, px, 0).xy;

        vec2 vPixels = v * vec2(uWidth, uHeight) * uShutter;
        float len = length(vPixels);
        float k = (len > uMaxRadius) ? (uMaxRadius / max(len, 1e-6)) : 1.0;
        vec2 delta = v * uShutter * k;

        float dc = max(texelFetch(uDepth, px, 0).r, 1e-6);
        int taps = max(int(uSampleCount), 2);
        vec2 lo = vec2(uTexelX, uTexelY) * 0.5;
        vec2 hi = vec2(1.0) - lo;

        vec3 sum = vec3(0.0);
        float wsum = 0.0;

        for (int i = 0; i < taps; i++)
        {
            float t = float(i) / float(taps - 1);
            vec2 suv = clamp(uv + delta * t, lo, hi);
            ivec2 sp = clamp(ivec2(suv * vec2(uWidth, uHeight)), ivec2(0),
                ivec2(int(uWidth) - 1, int(uHeight) - 1));

            float sd = texelFetch(uDepth, sp, 0).r;
            float rel = abs(dc - sd) / dc;
            float w = clamp(1.0 - rel / max(uDepthThreshold, 1e-6), 0.0, 1.0);

            sum += textureLod(uScene, suv, 0.0).rgb * w;
            wsum += w;
        }

        vec3 color = (wsum > 1e-5) ? (sum / wsum) : center.rgb;
        imageStore(uOutput, px, vec4(color, center.a));
    }
}
";

    /// <summary>Metal MSL kernel. SampledTexture provides sampler(0) for the one filtered fetch; velocity and
    /// depth stay texel loads, and depth is a depth2d because that is the view the backend binds for a
    /// DepthTexture slot (the same shape TaaEffect uses).</summary>
    const string SourceMsl = @"
#include <metal_stdlib>
using namespace metal;

struct MotionBlurParams
{
    float uWidth;
    float uHeight;
    float uTexelX;
    float uTexelY;
    float uShutter;
    float uMaxRadius;
    float uSampleCount;
    float uDepthThreshold;
};

kernel void CSMain(
    constant MotionBlurParams& params [[buffer(0)]],
    texture2d<float, access::sample> uScene [[texture(0)]],
    sampler uLinearClamp [[sampler(0)]],
    texture2d<float, access::sample> uVelocity [[texture(1)]],
    depth2d<float, access::read> uDepth [[texture(2)]],
    texture2d<float, access::write> uOutput [[texture(3)]],
    uint2 id [[thread_position_in_grid]])
{
    if (id.x < (uint)params.uWidth && id.y < (uint)params.uHeight)
    {
        float2 uv = (float2(id) + 0.5) * float2(params.uTexelX, params.uTexelY);
        float4 center = uScene.sample(uLinearClamp, uv, level(0.0));
        float2 v = uVelocity.read(id).xy;

        float2 vPixels = v * float2(params.uWidth, params.uHeight) * params.uShutter;
        float len = length(vPixels);
        float k = (len > params.uMaxRadius) ? (params.uMaxRadius / max(len, 1e-6)) : 1.0;
        float2 delta = v * params.uShutter * k;

        float dc = max(uDepth.read(id), 1e-6);
        int taps = max((int)params.uSampleCount, 2);
        float2 lo = float2(params.uTexelX, params.uTexelY) * 0.5;
        float2 hi = float2(1.0) - lo;

        float3 sum = float3(0.0);
        float wsum = 0.0;

        for (int i = 0; i < taps; i++)
        {
            float t = (float)i / (float)(taps - 1);
            float2 suv = clamp(uv + delta * t, lo, hi);
            uint2 sp = uint2(clamp(int2(suv * float2(params.uWidth, params.uHeight)),
                int2(0, 0), int2((int)params.uWidth - 1, (int)params.uHeight - 1)));

            float sd = uDepth.read(sp);
            float rel = abs(dc - sd) / dc;
            float w = saturate(1.0 - rel / max(params.uDepthThreshold, 1e-6));

            sum += uScene.sample(uLinearClamp, suv, level(0.0)).rgb * w;
            wsum += w;
        }

        float3 color = (wsum > 1e-5) ? (sum / wsum) : center.rgb;
        uOutput.write(float4(color, center.a), id);
    }
}
";

    /// <summary>WebGPU WGSL (delivered through the interop layer; seasonWebGPU.js source is not included).
    /// The SampledTexture slot carries a sampler at @binding(15) as a layout placeholder. Every scene fetch uses
    /// textureSampleLevel with an explicit level: it takes no derivatives, so it carries no uniform-control-flow
    /// requirement inside the dynamic loop.</summary>
    const string SourceWgsl = @"
struct MotionBlurParams
{
    uWidth : f32,
    uHeight : f32,
    uTexelX : f32,
    uTexelY : f32,
    uShutter : f32,
    uMaxRadius : f32,
    uSampleCount : f32,
    uDepthThreshold : f32,
};

@group(0) @binding(0) var<uniform> params : MotionBlurParams;
@group(0) @binding(1) var uScene : texture_2d<f32>;
@group(0) @binding(2) var uVelocity : texture_2d<f32>;
@group(0) @binding(3) var uDepth : texture_depth_2d;
@group(0) @binding(4) var uOutput : texture_storage_2d<rgba16float, write>;
@group(0) @binding(15) var uLinearClamp : sampler;

@compute @workgroup_size(8, 8, 1)
fn CSMain(@builtin(global_invocation_id) id : vec3<u32>)
{
    if (id.x < u32(params.uWidth) && id.y < u32(params.uHeight))
    {
        let px = vec2<i32>(i32(id.x), i32(id.y));
        let uv = (vec2<f32>(f32(id.x), f32(id.y)) + vec2<f32>(0.5))
            * vec2<f32>(params.uTexelX, params.uTexelY);
        let center = textureSampleLevel(uScene, uLinearClamp, uv, 0.0);
        let v = textureLoad(uVelocity, px, 0).xy;

        let vPixels = v * vec2<f32>(params.uWidth, params.uHeight) * params.uShutter;
        let len = length(vPixels);
        let k = select(1.0, params.uMaxRadius / max(len, 1e-6), len > params.uMaxRadius);
        let delta = v * params.uShutter * k;

        let dc = max(textureLoad(uDepth, px, 0), 1e-6);
        let taps = max(i32(params.uSampleCount), 2);
        let lo = vec2<f32>(params.uTexelX, params.uTexelY) * 0.5;
        let hi = vec2<f32>(1.0) - lo;

        var sum = vec3<f32>(0.0);
        var wsum = 0.0;

        for (var i = 0; i < taps; i = i + 1)
        {
            let t = f32(i) / f32(taps - 1);
            let suv = clamp(uv + delta * t, lo, hi);
            let sp = clamp(vec2<i32>(suv * vec2<f32>(params.uWidth, params.uHeight)),
                vec2<i32>(0, 0), vec2<i32>(i32(params.uWidth) - 1, i32(params.uHeight) - 1));

            let sd = textureLoad(uDepth, sp, 0);
            let rel = abs(dc - sd) / dc;
            let w = clamp(1.0 - rel / max(params.uDepthThreshold, 1e-6), 0.0, 1.0);

            sum = sum + textureSampleLevel(uScene, uLinearClamp, suv, 0.0).rgb * w;
            wsum = wsum + w;
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
