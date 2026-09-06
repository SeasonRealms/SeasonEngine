// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

/// <summary>
/// Engine built-in compute effect: TAA resolve
/// (2-3 Step B implementation body; see clauses 10-16 in section 2-3 of the RenderQuality class header).
///
/// Behavior: one kernel in the AfterScene phase, running at full resolution in linear HDR
/// space before tonemapping. It reads the current frame's SceneColor + SceneVelocity +
/// previous-frame output (history), writes the current output, and publishes that output
/// name to FrameSchedule.SceneColorOverride so bloom and the composition entry point can
/// use it as their source (clause 12).
///
/// Why this uses a storage texture instead of an RT-UAV: ComputeBindingType.StorageTextureWrite
/// is always write-only and only accepts textures created by CreateComputeTexture. Compute
/// cannot write directly to a RenderTarget UAV under the four-backend contract. So TAA writes
/// into a storage texture, and downstream stages read it by name through SceneColorOverride.
/// SceneColor RT itself remains the render target for the scene pass, leaving the pipeline unchanged.
///
/// Ping-pong (clause 11): history and output cannot be the same resource. Reading through SRV
/// and writing through UAV inside the same dispatch is a race, and WebGPU core does not support
/// rgba read-write storage. Therefore `taa0` and `taa1` alternate roles each frame: this frame
/// reads `_p^1` and writes `_p`, then publishes `_p` as the downstream source for the current frame.
/// Both resources are created once in Initialize, and runtime only swaps references with zero allocations.
///
/// Binding layout (declaration order defines the cross-backend slot convention;
/// see ComputeBindingType summary):
/// [0] Params 32B (width, height, 1/width, 1/height, feedback, clipGamma, historyValid, staticFeedback) -> HLSL b0
/// [1] SampledTexture (SceneColor target, rgba16float) -> HLSL t0
/// [2] SampledTexture (SceneVelocity target, rg16float) -> HLSL t1
/// [3] SampledTexture (history storage texture) -> HLSL t2
/// [4] DepthTexture (SceneDepth target, texel load without sampler) -> HLSL t3
/// [5] StorageTextureWrite rgba16float (current-frame output) -> HLSL u0
/// Sampler s0 (linear-clamp) is provided statically by the engine and is only needed for
/// history reprojection filtering, where clause 16 spends it on five taps instead of one.
/// Scene, velocity and depth always use texel loads.
///
/// Why depth is a hard dependency rather than an optimization: velocity is a per-pixel quantity
/// but a pixel on a silhouette is a mixture of two surfaces, and the one that ends up in the
/// velocity buffer is whichever fragment won the depth test, not the one that dominates the
/// colour. Reprojecting the whole pixel along the losing surface's motion is the standard source
/// of edge ghosting, so the velocity fetch is dilated towards the closest of the nine neighbours
/// (clause 10). SceneDepth is therefore required, and every platform app now creates it whenever
/// MotionVectors is on - Vulkan, Metal and Android already did because their velocity render pass
/// needs three real attachments, while D3D12 and WebGPU were extended for this.
///
/// Step B was shaped on D3D12 only (HLSL is the validated backend). The other three shader
/// sources are already provided in the aligned four-backend form and will be validated one by one in Step D.
/// </summary>
public sealed class TaaEffect : ComputeEffect
{
    /// <summary>Registered names of the ping-pong textures in the platform texture dictionary. Downstream stages resolve them by name, and Sprite2D can also sample them directly for debugging.</summary>
    public const string TextureName0 = "compute://taa0";

    public const string TextureName1 = "compute://taa1";

    ComputeKernel? _kernel;

    // Two resource arrays that alternate every frame. They are built once in Initialize
    // and then reused. ComputeResourceRef arrays containing strings cannot use stackalloc.
    // _res[p] points its history slot to [p^1] and its output slot to [p].
    ComputeResourceRef[][]? _res;

    readonly string[] _names = { TextureName0, TextureName1 };

    /// <summary>Index of the writer for the current frame (0/1). Record flips it before dispatch.</summary>
    int _p;

    /// <summary>Creation size of the ping-pong textures (clause 15: resize should be handled in place; on mismatch the effect bypasses itself).</summary>
    uint _width, _height;

    /// <summary>Whether the history contains valid data. It stays false on the first frame, when texture contents are undefined, and after any bypass; feedback is forced to zero based on this.</summary>
    bool _historyValid;

    public override string Name => "taa";

    public override ComputePhase Phase => ComputePhase.AfterScene;

    public override bool Initialize(IGraphics g)
    {
        // Quality mode and dependencies (clauses 1/10): TAA requires both velocity and
        // offscreen SceneColor. If either is missing, the whole effect stays inactive.
        if (RenderQuality.Current.AntiAliasing != AaMode.Taa || !RenderQuality.Current.MotionVectors)
            return false;
        // SceneDepth feeds velocity dilation. It is created by every platform app on the MotionVectors
        // condition, so with the check above this branch is unreachable in a correctly wired backend;
        // it stays as the shape dependency that keeps a missing depth target a clean bypass rather than
        // a kernel built against a resource array with a null in it.
        if (FrameSchedule.SceneColor == null || FrameSchedule.SceneVelocity == null || FrameSchedule.SceneDepth == null)
            return false;

        _kernel = g.CreateComputeKernel(new ComputeKernelDesc
        {
            Name = "taaResolve",
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

        // Clause 15: create the ping-pong textures at the current full backbuffer resolution.
        // If size mismatches later, the effect falls back frame by frame to the original SceneColor.
        var res = DeviceServices.BaseApp.DeviceResolution;
        _width = Math.Max(8u, (uint)res.X);
        _height = Math.Max(8u, (uint)res.Y);
        g.CreateComputeTexture(TextureName0, _width, _height, ComputeStorageFormat.Rgba16Float);
        g.CreateComputeTexture(TextureName1, _width, _height, ComputeStorageFormat.Rgba16Float);

        _res = new ComputeResourceRef[2][];
        for (int p = 0; p < 2; p++)
        {
            _res[p] = new ComputeResourceRef[]
            {
                FrameSchedule.SceneColor,
                FrameSchedule.SceneVelocity,
                _names[p ^ 1],  // History = previous frame's writer
                FrameSchedule.SceneDepth,
                _names[p],      // Output = current frame's writer
            };
        }

        _p = 0;
        _historyValid = false;
        return true;
    }

    public override void OnResize(IGraphics g)
    {
        // Clause 15 revision: recreate the ping-pong storage textures in place after resize
        // while keeping the name and C# object identity unchanged. Update the captured size
        // and invalidate history so convergence restarts on the next frame. The kernel itself
        // does not need rebuilding. CreateComputeTexture handles the size-match guard.
        var res = DeviceServices.BaseApp.DeviceResolution;
        uint w = Math.Max(8u, (uint)res.X);
        uint h = Math.Max(8u, (uint)res.Y);
        g.CreateComputeTexture(TextureName0, w, h, ComputeStorageFormat.Rgba16Float);
        g.CreateComputeTexture(TextureName1, w, h, ComputeStorageFormat.Rgba16Float);
        _width = w;
        _height = h;
        _historyValid = false;
    }

    public override void Record(IGraphics g)
    {
        // Clause 15: OnResize should already have recreated the textures in place, so sizes
        // normally match. This guard only handles degraded paths where OnResize was not called
        // (for example, a platform path that missed the resize callback). In that case, downstream
        // stages fall back to the original SceneColor, history is invalidated, and convergence
        // restarts automatically once the size matches again.
        var res = DeviceServices.BaseApp.DeviceResolution;
        if ((uint)res.X != _width || (uint)res.Y != _height)
        {
            FrameSchedule.SceneColorOverride = null;
            FrameSchedule.TaaActive = false;
            _historyValid = false;
            return;
        }

        _p ^= 1;

        Span<float> p = stackalloc float[8];
        p[0] = _width;
        p[1] = _height;
        p[2] = 1f / _width;
        p[3] = 1f / _height;
        p[4] = _historyValid ? RenderQuality.Current.TaaFeedback : 0f;
        p[5] = RenderQuality.Current.TaaVarianceClipGamma;
        p[6] = _historyValid ? 1f : 0f;
        // Clause 10: static-pixel feedback. The shader interpolates between this and TaaFeedback
        // by reprojection length, so a still camera gets the longer accumulation window that
        // jitter convergence needs while moving pixels keep the original, ghost-free weight.
        p[7] = _historyValid ? RenderQuality.Current.TaaStaticFeedback : 0f;

        g.DispatchCompute(new ComputeDispatchArgs
        {
            Kernel = _kernel!,
            Params = MemoryMarshal.AsBytes(p),
            Resources = _res![_p],
            GroupsX = (_width + 7) / 8,
            GroupsY = (_height + 7) / 8,
            GroupsZ = 1,
        });

        // Clauses 12/13: publish the current frame's writer for bloom and the composition
        // entry point after dispatch, so downstream stages always read fully written content.
        // Clause 14: this flag also enables jitter injection.
        FrameSchedule.SceneColorOverride = _names[_p];
        FrameSchedule.TaaActive = true;
        _historyValid = true;
    }

    public void Dispose()
    {
        // Clause 12: clear the override only when it still points to one of our own outputs,
        // so we do not accidentally clear another producer's override. After reset, the whole chain falls back to SceneColor.
        if (FrameSchedule.SceneColorOverride == TextureName0 || FrameSchedule.SceneColorOverride == TextureName1)
            FrameSchedule.SceneColorOverride = null;
        FrameSchedule.TaaActive = false;
        _historyValid = false;
        _kernel?.Dispose();
        _kernel = null;
    }

    // Shader sources (single source of truth; slots follow the class-level binding convention;
    // workgroup is fixed at 8x8x1; single exit avoids fxc X4000; the resolve formula is a
    // cross-backend contract constant and should be ported literally when aligned).
    //
    // Algorithm (clause 10):
    //   1. Reprojection: prevUV = uv - velocity, where velocity is dilated. The 3x3 neighborhood is
    //      searched for the smallest depth and that neighbour's velocity is used, while the uv being
    //      reprojected stays the centre pixel's. Depth is [0,1] with 0 at the near plane, so the
    //      smallest value is the closest surface, and the comparison needs no linearization because
    //      the mapping is monotonic. The search is seeded with the centre pixel and uses a strict
    //      less-than, so ties resolve to the centre: on flat depth, such as sky, a non-strict
    //      comparison would systematically pick a corner tap and shift the whole reprojection by a
    //      pixel. Clause 5 defines velocity as "current pointing to history", so it can be used directly.
    //   2. History sampling: 5-tap Catmull-Rom (clause 16). This is the only sample that needs filtering.
    //      Scene, velocity and depth always use texel loads: filtering velocity would mix motion
    //      from adjacent objects, filtering depth would invent surfaces between silhouettes, and
    //      filtering scene at the same resolution is meaningless.
    //      Why not the single bilinear fetch this started as: reprojection almost never lands on a texel
    //      centre, so history was resampled every frame through a filter whose response is already well
    //      down before Nyquist, and the loss compounds inside the feedback loop. At staticFeedback 0.97 a
    //      converged pixel is the accumulation of roughly 1/(1-0.97) = 33 frames, which is what turns a
    //      few percent of per-frame softening into the dominant reason a resolved image looks softer than
    //      the jittered samples it was built from. Catmull-Rom is interpolating and carries a mild negative
    //      lobe, so it holds the passband close to flat instead of trading it away.
    //      The negative lobe can overshoot, including below zero next to a highlight, which is bounded
    //      rather than avoided: step 3 clamps at zero entering the tonemapped domain and step 4's box
    //      bounds the positive side, so ringing has nowhere to accumulate.
    //      The filter runs in linear HDR and is tonemapped afterwards, which is the order the single
    //      bilinear fetch already used (hardware filtering is linear too), so this is a filter-quality
    //      change rather than a domain change. Cost is five bilinear fetches where there was one.
    //   3. Reversible tonemap domain: every scene tap and the reprojected history are mapped
    //      through c/(1+luma(c)) before statistics, clipping and blending, and the result is
    //      mapped back with the exact inverse c/(1-luma(c)). Steps 4 and 5 therefore operate on
    //      a perceptual quantity instead of raw linear HDR radiance.
    //      Why this is required rather than cosmetic: a jittered sample that lands on a
    //      high-frequency highlight (mipmap-free material detail on distant geometry is the
    //      typical source) carries radiance one or two orders of magnitude above its neighbours.
    //      Averaged in linear HDR, that single tap drags the pixel up by (1-feedback) of a huge
    //      delta and then decays over ~1/(1-feedback) frames, which is exactly the per-frame
    //      "boiling" that makes jitter visible. In the tonemapped domain the same tap is bounded
    //      by construction, so the accumulated mean is the perceptual mean of the subpixel
    //      samples, which is what jitter is supposed to reconstruct in the first place.
    //      Scene taps are clamped to >= 0 before the mapping: 1+luma must stay strictly positive,
    //      and negative radiance is reachable when a consumer extrapolates (AerialIntensity > 1
    //      is a lerp weight, not a multiplier).
    //   4. Neighborhood clamping in YCoCg: one 3x3 pass computes first/second moments plus min/max,
    //      all of it in YCoCg rather than per RGB channel.
    //      Clamp range = [mean-gamma*sigma, mean+gamma*sigma] intersected with [min, max].
    //      When gamma <= 0, this degenerates into a pure min/max bounding box.
    //      Why not RGB: across an edge between two differently coloured surfaces, every RGB channel
    //      spans both the luminance step and the hue difference, so the axis-aligned RGB box around
    //      the nine samples is far larger than the set of colours that actually occur there, and
    //      history that is wrong in hue passes the clamp untouched. YCoCg puts the luminance step on
    //      one axis and leaves the two chroma axes narrow, so the same nine samples give a much
    //      tighter box. That reduces flicker and ghosting at the same time for about ten operations
    //      per tap, which is why it is preferred over widening or narrowing gamma.
    //      The reconstructed colour is then intersected with the tonemapped RGB min/max box, and that
    //      second clamp is load-bearing rather than defensive: the YCoCg box is not a subset of the
    //      RGB one, so a corner of it can reconstruct to a triple whose Rec709 luma reaches one -
    //      take a neighbourhood holding a saturated red and a saturated green tap and combine the
    //      largest Y with the largest Cg. That is precisely where step 3's inverse divides by
    //      1-luma, which would scale the pixel by four orders of magnitude and then feed the result
    //      back into history. The RGB box is a set whose members are invertible by construction.
    //      Note the division of labour with step 5: on smooth surfaces sigma collapses, so the
    //      clamp pins history to the local mean and lighting changes are tracked with no lag;
    //      on edges sigma is wide, so history is free to accumulate. The clamp handles change,
    //      the feedback weight handles noise. They must not be tuned as if they were one knob.
    //   5. Blending: lerp(cur, clamp(hist), fb). fb interpolates from staticFeedback at zero
    //      reprojection to feedback at one pixel of motion per frame. A still camera is the case
    //      where jitter has the most frames to converge and therefore tolerates the highest
    //      feedback, while anything actually moving keeps the original weight so no new ghosting
    //      is introduced. Setting staticFeedback equal to feedback restores uniform blending.
    //      If reprojection lands outside the screen or history is invalid, CPU-side logic has
    //      already forced both weights to zero.

    /// <summary>D3D12 cs_5_0 (fxc; single exit avoids X4000). When Texture2D&lt;float4&gt; reads an
    /// rg16float SRV, missing components are filled as (0,1); taking .xy yields velocity,
    /// matching the VelocityView precedent.</summary>
    const string SourceHlsl = @"
cbuffer TaaParams : register(b0)
{
    float uWidth;
    float uHeight;
    float uTexelX;
    float uTexelY;
    float uFeedback;
    float uClipGamma;
    float uHistoryValid;
    float uStaticFeedback;
};

Texture2D<float4> uScene : register(t0);
Texture2D<float4> uVelocity : register(t1);
Texture2D<float4> uHistory : register(t2);
Texture2D<float> uDepth : register(t3);
SamplerState uLinearClamp : register(s0);
RWTexture2D<float4> uOutput : register(u0);

float TaaLuma(float3 c)
{
    return dot(c, float3(0.2126, 0.7152, 0.0722));
}

float3 TaaTonemap(float3 c)
{
    float3 x = max(c, 0.0);
    return x / (1.0 + TaaLuma(x));
}

float3 TaaUntonemap(float3 c)
{
    return c / max(1.0 - TaaLuma(c), 1e-4);
}

// Lifting matrix, exactly invertible in real arithmetic: Y is the (1,2,1)/4 average, Co the red-blue
// difference, Cg the green-magenta difference. Chosen over a Rec601 YCbCr rotation because both
// directions are a handful of adds and one multiply, with no matrix constants to keep in sync across
// four shading languages.
float3 TaaRgbToYCoCg(float3 c)
{
    return float3((c.r + 2.0 * c.g + c.b) * 0.25,
                  (c.r - c.b) * 0.5,
                  (-c.r + 2.0 * c.g - c.b) * 0.25);
}

float3 TaaYCoCgToRgb(float3 c)
{
    float t = c.x - c.z;
    return float3(t + c.y, c.x + c.z, t - c.y);
}

// Clause 16: 5-tap Catmull-Rom history resampling. A separable Catmull-Rom needs four taps per axis, but the
// (w1,w2) pair on each axis is exactly reproducible by one bilinear fetch placed at their weighted midpoint,
// which takes the 4x4 footprint from sixteen texels to a 3x3 arrangement of nine; the four corners of that
// arrangement are then dropped, leaving the five of a cross.
float3 TaaSampleHistory(float2 uv)
{
    float2 samplePos = uv * float2(uWidth, uHeight);
    float2 texPos1 = floor(samplePos - 0.5) + 0.5;
    float2 f = samplePos - texPos1;
    float2 f2 = f * f;
    float2 f3 = f2 * f;

    float2 w0 = -0.5 * f3 + f2 - 0.5 * f;
    float2 w1 = 1.5 * f3 - 2.5 * f2 + 1.0;
    float2 w2 = -1.5 * f3 + 2.0 * f2 + 0.5 * f;
    float2 w3 = 0.5 * f3 - 0.5 * f2;

    // w1 + w2 simplifies to 1 + 0.5 * f * (1 - f), so it stays inside [1, 1.125] and this divide needs no guard.
    float2 w12 = w1 + w2;
    float2 off12 = w2 / w12;

    float2 texel = float2(uTexelX, uTexelY);
    float2 p0 = (texPos1 - 1.0) * texel;
    float2 p3 = (texPos1 + 2.0) * texel;
    float2 p12 = (texPos1 + off12) * texel;

    float k0 = w12.x * w0.y;
    float k1 = w0.x * w12.y;
    float k2 = w12.x * w12.y;
    float k3 = w3.x * w12.y;
    float k4 = w12.x * w3.y;

    float3 sum = uHistory.SampleLevel(uLinearClamp, float2(p12.x, p0.y), 0.0).rgb * k0
               + uHistory.SampleLevel(uLinearClamp, float2(p0.x, p12.y), 0.0).rgb * k1
               + uHistory.SampleLevel(uLinearClamp, float2(p12.x, p12.y), 0.0).rgb * k2
               + uHistory.SampleLevel(uLinearClamp, float2(p3.x, p12.y), 0.0).rgb * k3
               + uHistory.SampleLevel(uLinearClamp, float2(p12.x, p3.y), 0.0).rgb * k4;

    // Renormalization is load-bearing, not tidiness. The nine separable weights sum to one, but the five kept
    // here sum to 1 - (1 - w12.x) * (1 - w12.y), which reaches its minimum of 0.984375 at the half-texel offset.
    // Uncorrected that is a per-frame energy loss sitting inside the feedback loop: the steady state of
    // h = (1 - fb) * cur + fb * k * h is cur * (1 - fb) / (1 - fb * k), which at fb = 0.97 and k = 0.984375 is
    // 0.66 - a third of the brightness gone. A flat region is rescued by step 4 (min equals max there, so the
    // clamp pins history to cur), which means the loss would have shown up specifically on the detailed and
    // edge pixels where the clamp is loose, i.e. the ones this filter exists to preserve.
    return sum / (k0 + k1 + k2 + k3 + k4);
}

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x < (uint)uWidth && id.y < (uint)uHeight)
    {
        int2 maxCoord = int2((int)uWidth - 1, (int)uHeight - 1);
        float4 curRaw = uScene.Load(int3(id.xy, 0));
        float3 cur = TaaTonemap(curRaw.rgb);

        // 3x3 neighborhood statistics: first/second moments and the min/max box in YCoCg, plus the
        // RGB box kept for the invertibility clamp, computed in one pass.
        // The same walk carries the velocity dilation search, so the nine taps are paid for once.
        float3 m1 = 0.0;
        float3 m2 = 0.0;
        float3 cmin = TaaRgbToYCoCg(cur);
        float3 cmax = cmin;
        float3 nmin = cur;
        float3 nmax = cur;
        int2 nearest = int2(id.xy);
        float nearestDepth = uDepth.Load(int3(id.xy, 0));
        [unroll] for (int y = -1; y <= 1; ++y)
        {
            [unroll] for (int x = -1; x <= 1; ++x)
            {
                int2 c = clamp(int2(id.xy) + int2(x, y), int2(0, 0), maxCoord);
                float3 sRgb = TaaTonemap(uScene.Load(int3(c, 0)).rgb);
                nmin = min(nmin, sRgb);
                nmax = max(nmax, sRgb);

                float3 s = TaaRgbToYCoCg(sRgb);
                m1 += s;
                m2 += s * s;
                cmin = min(cmin, s);
                cmax = max(cmax, s);

                float d = uDepth.Load(int3(c, 0));
                if (d < nearestDepth)
                {
                    nearestDepth = d;
                    nearest = c;
                }
            }
        }
        float3 mean = m1 / 9.0;
        float3 sigma = sqrt(max(m2 / 9.0 - mean * mean, 0.0));
        float3 ext = uClipGamma * sigma;
        float3 lo = (uClipGamma > 0.0) ? max(mean - ext, cmin) : cmin;
        float3 hi = (uClipGamma > 0.0) ? min(mean + ext, cmax) : cmax;

        float2 uv = (float2(id.xy) + 0.5) * float2(uTexelX, uTexelY);
        float2 v = uVelocity.Load(int3(nearest, 0)).xy;
        float2 prevUv = uv - v;

        float3 hist = TaaTonemap(TaaSampleHistory(prevUv));
        float3 clamped = clamp(TaaYCoCgToRgb(clamp(TaaRgbToYCoCg(hist), lo, hi)), nmin, nmax);

        // Motion-adaptive feedback: reprojection length in pixels, saturated at one pixel per frame.
        float vPixels = length(v * float2(uWidth, uHeight));
        float fb = lerp(uStaticFeedback, uFeedback, clamp(vPixels, 0.0, 1.0));

        // Reprojection outside the screen means no valid history.
        // Clamping out-of-range history to the border would be wrong and leaves ghosts along the frame edge.
        bool inside = all(prevUv > 0.0) && all(prevUv < 1.0);
        float w = (uHistoryValid > 0.5 && inside) ? fb : 0.0;

        uOutput[id.xy] = float4(max(TaaUntonemap(lerp(cur, clamped, w)), 0.0), curRaw.a);
    }
}
";

    /// <summary>Vulkan GLSL 450 (glslang -> SPIR-V; Params use push_constant, binding follows declaration order, and binding 0 remains empty).</summary>
    const string SourceGlsl = @"#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(push_constant) uniform TaaParams
{
    float uWidth;
    float uHeight;
    float uTexelX;
    float uTexelY;
    float uFeedback;
    float uClipGamma;
    float uHistoryValid;
    float uStaticFeedback;
};

layout(binding = 1) uniform sampler2D uScene;
layout(binding = 2) uniform sampler2D uVelocity;
layout(binding = 3) uniform sampler2D uHistory;
layout(binding = 4) uniform sampler2D uDepth;
layout(binding = 5, rgba16f) uniform writeonly image2D uOutput;

float TaaLuma(vec3 c)
{
    return dot(c, vec3(0.2126, 0.7152, 0.0722));
}

vec3 TaaTonemap(vec3 c)
{
    vec3 x = max(c, vec3(0.0));
    return x / (1.0 + TaaLuma(x));
}

vec3 TaaUntonemap(vec3 c)
{
    return c / max(1.0 - TaaLuma(c), 1e-4);
}

vec3 TaaRgbToYCoCg(vec3 c)
{
    return vec3((c.r + 2.0 * c.g + c.b) * 0.25,
                (c.r - c.b) * 0.5,
                (-c.r + 2.0 * c.g - c.b) * 0.25);
}

vec3 TaaYCoCgToRgb(vec3 c)
{
    float t = c.x - c.z;
    return vec3(t + c.y, c.x + c.z, t - c.y);
}

// Clause 16: 5-tap Catmull-Rom history resampling. See the HLSL source for the derivation and for why the
// renormalization at the end is load-bearing.
vec3 TaaSampleHistory(vec2 uv)
{
    vec2 samplePos = uv * vec2(uWidth, uHeight);
    vec2 texPos1 = floor(samplePos - 0.5) + 0.5;
    vec2 f = samplePos - texPos1;
    vec2 f2 = f * f;
    vec2 f3 = f2 * f;

    vec2 w0 = -0.5 * f3 + f2 - 0.5 * f;
    vec2 w1 = 1.5 * f3 - 2.5 * f2 + 1.0;
    vec2 w2 = -1.5 * f3 + 2.0 * f2 + 0.5 * f;
    vec2 w3 = 0.5 * f3 - 0.5 * f2;

    vec2 w12 = w1 + w2;
    vec2 off12 = w2 / w12;

    vec2 texel = vec2(uTexelX, uTexelY);
    vec2 p0 = (texPos1 - 1.0) * texel;
    vec2 p3 = (texPos1 + 2.0) * texel;
    vec2 p12 = (texPos1 + off12) * texel;

    float k0 = w12.x * w0.y;
    float k1 = w0.x * w12.y;
    float k2 = w12.x * w12.y;
    float k3 = w3.x * w12.y;
    float k4 = w12.x * w3.y;

    vec3 sum = textureLod(uHistory, vec2(p12.x, p0.y), 0.0).rgb * k0
             + textureLod(uHistory, vec2(p0.x, p12.y), 0.0).rgb * k1
             + textureLod(uHistory, vec2(p12.x, p12.y), 0.0).rgb * k2
             + textureLod(uHistory, vec2(p3.x, p12.y), 0.0).rgb * k3
             + textureLod(uHistory, vec2(p12.x, p3.y), 0.0).rgb * k4;

    return sum / (k0 + k1 + k2 + k3 + k4);
}

void main()
{
    uvec2 id = gl_GlobalInvocationID.xy;
    if (id.x < uint(uWidth) && id.y < uint(uHeight))
    {
        ivec2 maxCoord = ivec2(int(uWidth) - 1, int(uHeight) - 1);
        vec4 curRaw = texelFetch(uScene, ivec2(id), 0);
        vec3 cur = TaaTonemap(curRaw.rgb);

        vec3 m1 = vec3(0.0);
        vec3 m2 = vec3(0.0);
        vec3 cmin = TaaRgbToYCoCg(cur);
        vec3 cmax = cmin;
        vec3 nmin = cur;
        vec3 nmax = cur;
        ivec2 nearest = ivec2(id);
        float nearestDepth = texelFetch(uDepth, ivec2(id), 0).r;
        for (int y = -1; y <= 1; ++y)
        {
            for (int x = -1; x <= 1; ++x)
            {
                ivec2 c = clamp(ivec2(id) + ivec2(x, y), ivec2(0), maxCoord);
                vec3 sRgb = TaaTonemap(texelFetch(uScene, c, 0).rgb);
                nmin = min(nmin, sRgb);
                nmax = max(nmax, sRgb);

                vec3 s = TaaRgbToYCoCg(sRgb);
                m1 += s;
                m2 += s * s;
                cmin = min(cmin, s);
                cmax = max(cmax, s);

                float d = texelFetch(uDepth, c, 0).r;
                if (d < nearestDepth)
                {
                    nearestDepth = d;
                    nearest = c;
                }
            }
        }
        vec3 mean = m1 / 9.0;
        vec3 sigma = sqrt(max(m2 / 9.0 - mean * mean, vec3(0.0)));
        vec3 ext = uClipGamma * sigma;
        vec3 lo = (uClipGamma > 0.0) ? max(mean - ext, cmin) : cmin;
        vec3 hi = (uClipGamma > 0.0) ? min(mean + ext, cmax) : cmax;

        vec2 uv = (vec2(id) + 0.5) * vec2(uTexelX, uTexelY);
        vec2 v = texelFetch(uVelocity, nearest, 0).xy;
        vec2 prevUv = uv - v;

        vec3 hist = TaaTonemap(TaaSampleHistory(prevUv));
        vec3 clamped = clamp(TaaYCoCgToRgb(clamp(TaaRgbToYCoCg(hist), lo, hi)), nmin, nmax);

        float vPixels = length(v * vec2(uWidth, uHeight));
        float fb = mix(uStaticFeedback, uFeedback, clamp(vPixels, 0.0, 1.0));

        bool inside = all(greaterThan(prevUv, vec2(0.0))) && all(lessThan(prevUv, vec2(1.0)));
        float w = (uHistoryValid > 0.5 && inside) ? fb : 0.0;

        imageStore(uOutput, ivec2(id), vec4(max(TaaUntonemap(mix(cur, clamped, w)), vec3(0.0)), curRaw.a));
    }
}
";

    /// <summary>Metal MSL kernel (textures map to texture(i) by declaration order, Params go through buffer(0)).</summary>
    const string SourceMsl = @"
#include <metal_stdlib>
using namespace metal;

struct TaaParams
{
    float uWidth;
    float uHeight;
    float uTexelX;
    float uTexelY;
    float uFeedback;
    float uClipGamma;
    float uHistoryValid;
    float uStaticFeedback;
};

static inline float TaaLuma(float3 c)
{
    return dot(c, float3(0.2126, 0.7152, 0.0722));
}

static inline float3 TaaTonemap(float3 c)
{
    float3 x = max(c, float3(0.0));
    return x / (1.0 + TaaLuma(x));
}

static inline float3 TaaUntonemap(float3 c)
{
    return c / max(1.0 - TaaLuma(c), 1e-4);
}

static inline float3 TaaRgbToYCoCg(float3 c)
{
    return float3((c.r + 2.0 * c.g + c.b) * 0.25,
                  (c.r - c.b) * 0.5,
                  (-c.r + 2.0 * c.g - c.b) * 0.25);
}

static inline float3 TaaYCoCgToRgb(float3 c)
{
    float t = c.x - c.z;
    return float3(t + c.y, c.x + c.z, t - c.y);
}

// Clause 16: 5-tap Catmull-Rom history resampling. See the HLSL source for the derivation and for why the
// renormalization at the end is load-bearing. Unlike the other three backends the parameter block is a
// function argument here rather than module scope, so the two sizes are passed in explicitly.
static inline float3 TaaSampleHistory(
    texture2d<float, access::sample> tex,
    sampler samp,
    float2 uv,
    float2 texSize,
    float2 texel)
{
    float2 samplePos = uv * texSize;
    float2 texPos1 = floor(samplePos - 0.5) + 0.5;
    float2 f = samplePos - texPos1;
    float2 f2 = f * f;
    float2 f3 = f2 * f;

    float2 w0 = -0.5 * f3 + f2 - 0.5 * f;
    float2 w1 = 1.5 * f3 - 2.5 * f2 + 1.0;
    float2 w2 = -1.5 * f3 + 2.0 * f2 + 0.5 * f;
    float2 w3 = 0.5 * f3 - 0.5 * f2;

    float2 w12 = w1 + w2;
    float2 off12 = w2 / w12;

    float2 p0 = (texPos1 - 1.0) * texel;
    float2 p3 = (texPos1 + 2.0) * texel;
    float2 p12 = (texPos1 + off12) * texel;

    float k0 = w12.x * w0.y;
    float k1 = w0.x * w12.y;
    float k2 = w12.x * w12.y;
    float k3 = w3.x * w12.y;
    float k4 = w12.x * w3.y;

    float3 sum = tex.sample(samp, float2(p12.x, p0.y), level(0.0)).rgb * k0
               + tex.sample(samp, float2(p0.x, p12.y), level(0.0)).rgb * k1
               + tex.sample(samp, float2(p12.x, p12.y), level(0.0)).rgb * k2
               + tex.sample(samp, float2(p3.x, p12.y), level(0.0)).rgb * k3
               + tex.sample(samp, float2(p12.x, p3.y), level(0.0)).rgb * k4;

    return sum / (k0 + k1 + k2 + k3 + k4);
}

kernel void CSMain(
    constant TaaParams& params [[buffer(0)]],
    texture2d<float, access::sample> uScene [[texture(0)]],
    texture2d<float, access::sample> uVelocity [[texture(1)]],
    texture2d<float, access::sample> uHistory [[texture(2)]],
    depth2d<float, access::read> uDepth [[texture(3)]],
    texture2d<float, access::write> uOutput [[texture(4)]],
    sampler uLinearClamp [[sampler(0)]],
    uint2 id [[thread_position_in_grid]])
{
    if (id.x < (uint)params.uWidth && id.y < (uint)params.uHeight)
    {
        int2 maxCoord = int2((int)params.uWidth - 1, (int)params.uHeight - 1);
        float4 curRaw = uScene.read(id);
        float3 cur = TaaTonemap(curRaw.rgb);

        float3 m1 = float3(0.0);
        float3 m2 = float3(0.0);
        float3 cmin = TaaRgbToYCoCg(cur);
        float3 cmax = cmin;
        float3 nmin = cur;
        float3 nmax = cur;
        int2 nearest = int2(id);
        float nearestDepth = uDepth.read(id);
        for (int y = -1; y <= 1; ++y)
        {
            for (int x = -1; x <= 1; ++x)
            {
                int2 c = clamp(int2(id) + int2(x, y), int2(0), maxCoord);
                float3 sRgb = TaaTonemap(uScene.read(uint2(c)).rgb);
                nmin = min(nmin, sRgb);
                nmax = max(nmax, sRgb);

                float3 s = TaaRgbToYCoCg(sRgb);
                m1 += s;
                m2 += s * s;
                cmin = min(cmin, s);
                cmax = max(cmax, s);

                float d = uDepth.read(uint2(c));
                if (d < nearestDepth)
                {
                    nearestDepth = d;
                    nearest = c;
                }
            }
        }
        float3 mean = m1 / 9.0;
        float3 sigma = sqrt(max(m2 / 9.0 - mean * mean, float3(0.0)));
        float3 ext = params.uClipGamma * sigma;
        float3 lo = (params.uClipGamma > 0.0) ? max(mean - ext, cmin) : cmin;
        float3 hi = (params.uClipGamma > 0.0) ? min(mean + ext, cmax) : cmax;

        float2 uv = (float2(id) + 0.5) * float2(params.uTexelX, params.uTexelY);
        float2 v = uVelocity.read(uint2(nearest)).xy;
        float2 prevUv = uv - v;

        float3 hist = TaaTonemap(TaaSampleHistory(uHistory, uLinearClamp, prevUv,
                                                  float2(params.uWidth, params.uHeight),
                                                  float2(params.uTexelX, params.uTexelY)));
        float3 clamped = clamp(TaaYCoCgToRgb(clamp(TaaRgbToYCoCg(hist), lo, hi)), nmin, nmax);

        float vPixels = length(v * float2(params.uWidth, params.uHeight));
        float fb = mix(params.uStaticFeedback, params.uFeedback, clamp(vPixels, 0.0, 1.0));

        bool inside = all(prevUv > float2(0.0)) && all(prevUv < float2(1.0));
        float w = (params.uHistoryValid > 0.5 && inside) ? fb : 0.0;

        uOutput.write(float4(max(TaaUntonemap(mix(cur, clamped, w)), float3(0.0)), curRaw.a), id);
    }
}
";

    /// <summary>WebGPU WGSL (delivered through the interop layer; seasonWebGPU.js source is not included).</summary>
    const string SourceWgsl = @"
struct TaaParams
{
    uWidth : f32,
    uHeight : f32,
    uTexelX : f32,
    uTexelY : f32,
    uFeedback : f32,
    uClipGamma : f32,
    uHistoryValid : f32,
    uStaticFeedback : f32,
};

@group(0) @binding(0) var<uniform> params : TaaParams;
@group(0) @binding(1) var uScene : texture_2d<f32>;
@group(0) @binding(2) var uVelocity : texture_2d<f32>;
@group(0) @binding(3) var uHistory : texture_2d<f32>;
@group(0) @binding(4) var uDepth : texture_depth_2d;
@group(0) @binding(5) var uOutput : texture_storage_2d<rgba16float, write>;
@group(0) @binding(15) var uLinearClamp : sampler;

fn TaaLuma(c : vec3<f32>) -> f32
{
    return dot(c, vec3<f32>(0.2126, 0.7152, 0.0722));
}

fn TaaTonemap(c : vec3<f32>) -> vec3<f32>
{
    let x = max(c, vec3<f32>(0.0));
    return x / (1.0 + TaaLuma(x));
}

fn TaaUntonemap(c : vec3<f32>) -> vec3<f32>
{
    return c / max(1.0 - TaaLuma(c), 1e-4);
}

fn TaaRgbToYCoCg(c : vec3<f32>) -> vec3<f32>
{
    return vec3<f32>((c.r + 2.0 * c.g + c.b) * 0.25,
                     (c.r - c.b) * 0.5,
                     (-c.r + 2.0 * c.g - c.b) * 0.25);
}

fn TaaYCoCgToRgb(c : vec3<f32>) -> vec3<f32>
{
    let t = c.x - c.z;
    return vec3<f32>(t + c.y, c.x + c.z, t - c.y);
}

// Clause 16: 5-tap Catmull-Rom history resampling. See the HLSL source for the derivation and for why the
// renormalization at the end is load-bearing. All five fetches use textureSampleLevel with an explicit level,
// which takes no derivatives and therefore carries no uniform-control-flow requirement - the same reason the
// single fetch it replaces was already legal inside the bounds check this is called from.
fn TaaSampleHistory(uv : vec2<f32>) -> vec3<f32>
{
    let samplePos = uv * vec2<f32>(params.uWidth, params.uHeight);
    let texPos1 = floor(samplePos - vec2<f32>(0.5)) + vec2<f32>(0.5);
    let f = samplePos - texPos1;
    let f2 = f * f;
    let f3 = f2 * f;

    let w0 = -0.5 * f3 + f2 - 0.5 * f;
    let w1 = 1.5 * f3 - 2.5 * f2 + vec2<f32>(1.0);
    let w2 = -1.5 * f3 + 2.0 * f2 + 0.5 * f;
    let w3 = 0.5 * f3 - 0.5 * f2;

    let w12 = w1 + w2;
    let off12 = w2 / w12;

    let texel = vec2<f32>(params.uTexelX, params.uTexelY);
    let p0 = (texPos1 - vec2<f32>(1.0)) * texel;
    let p3 = (texPos1 + vec2<f32>(2.0)) * texel;
    let p12 = (texPos1 + off12) * texel;

    let k0 = w12.x * w0.y;
    let k1 = w0.x * w12.y;
    let k2 = w12.x * w12.y;
    let k3 = w3.x * w12.y;
    let k4 = w12.x * w3.y;

    let sum = textureSampleLevel(uHistory, uLinearClamp, vec2<f32>(p12.x, p0.y), 0.0).rgb * k0
            + textureSampleLevel(uHistory, uLinearClamp, vec2<f32>(p0.x, p12.y), 0.0).rgb * k1
            + textureSampleLevel(uHistory, uLinearClamp, vec2<f32>(p12.x, p12.y), 0.0).rgb * k2
            + textureSampleLevel(uHistory, uLinearClamp, vec2<f32>(p3.x, p12.y), 0.0).rgb * k3
            + textureSampleLevel(uHistory, uLinearClamp, vec2<f32>(p12.x, p3.y), 0.0).rgb * k4;

    return sum / (k0 + k1 + k2 + k3 + k4);
}

@compute @workgroup_size(8, 8, 1)
fn CSMain(@builtin(global_invocation_id) id : vec3<u32>)
{
    if (id.x < u32(params.uWidth) && id.y < u32(params.uHeight))
    {
        let coord = vec2<i32>(i32(id.x), i32(id.y));
        let maxCoord = vec2<i32>(i32(params.uWidth) - 1, i32(params.uHeight) - 1);
        let curRaw = textureLoad(uScene, coord, 0);
        let cur = TaaTonemap(curRaw.rgb);

        var m1 = vec3<f32>(0.0);
        var m2 = vec3<f32>(0.0);
        var cmin = TaaRgbToYCoCg(cur);
        var cmax = cmin;
        var nmin = cur;
        var nmax = cur;
        var nearest = coord;
        var nearestDepth = textureLoad(uDepth, coord, 0);
        for (var y : i32 = -1; y <= 1; y = y + 1)
        {
            for (var x : i32 = -1; x <= 1; x = x + 1)
            {
                let c = clamp(coord + vec2<i32>(x, y), vec2<i32>(0, 0), maxCoord);
                let sRgb = TaaTonemap(textureLoad(uScene, c, 0).rgb);
                nmin = min(nmin, sRgb);
                nmax = max(nmax, sRgb);

                let s = TaaRgbToYCoCg(sRgb);
                m1 = m1 + s;
                m2 = m2 + s * s;
                cmin = min(cmin, s);
                cmax = max(cmax, s);

                let d = textureLoad(uDepth, c, 0);
                if (d < nearestDepth)
                {
                    nearestDepth = d;
                    nearest = c;
                }
            }
        }
        let mean = m1 / 9.0;
        let sigma = sqrt(max(m2 / 9.0 - mean * mean, vec3<f32>(0.0)));
        let ext = params.uClipGamma * sigma;
        var lo = cmin;
        var hi = cmax;
        if (params.uClipGamma > 0.0)
        {
            lo = max(mean - ext, cmin);
            hi = min(mean + ext, cmax);
        }

        let uv = (vec2<f32>(f32(id.x), f32(id.y)) + vec2<f32>(0.5)) * vec2<f32>(params.uTexelX, params.uTexelY);
        let v = textureLoad(uVelocity, nearest, 0).xy;
        let prevUv = uv - v;

        let hist = TaaTonemap(TaaSampleHistory(prevUv));
        let clamped = clamp(TaaYCoCgToRgb(clamp(TaaRgbToYCoCg(hist), lo, hi)), nmin, nmax);

        let vPixels = length(v * vec2<f32>(params.uWidth, params.uHeight));
        let fb = mix(params.uStaticFeedback, params.uFeedback, clamp(vPixels, 0.0, 1.0));

        let inside = all(prevUv > vec2<f32>(0.0)) && all(prevUv < vec2<f32>(1.0));
        var w = 0.0;
        if (params.uHistoryValid > 0.5 && inside)
        {
            w = fb;
        }

        textureStore(uOutput, coord, vec4<f32>(max(TaaUntonemap(mix(cur, clamped, w)), vec3<f32>(0.0)), curRaw.a));
    }
}
";
}
