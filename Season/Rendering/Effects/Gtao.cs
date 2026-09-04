// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

/// <summary>
/// Engine built-in compute effect: GTAO-lite ambient occlusion
/// (2-2 Step B implementation body; see section 2-2 in the RenderQuality class header for the contract).
/// Two kernels and a three-dispatch chain (all in the AfterScene phase, half resolution,
/// bandwidth-first):
/// 1. gtaoMain: SceneDepth (DepthTexture input) -> raw.
///    Reconstructs view-space normals from depth (center differences over the four
///    neighbors, picking the smaller-|Delta z| side to avoid depth-cliff artifacts),
///    performs horizon-based 2-slice x dual-side x 4-step integration
///    (GTAO slice integral in XeGTAO form), and rotates the starting angle with IGN
///    spatial noise (pure ALU, no noise texture).
/// 2. gtaoBlur x2: the X and Y passes share one kernel, with direction passed in Params:
///    raw -> blurx -> ao. Uses a depth-aware separable Gaussian blur (9 taps, normalized
///    linear depth in the g channel as the bilateral weight, preserving edges without light leaks).
///
/// Output: ao (half-resolution rgba8unorm, r = AO visibility with 1 = unoccluded,
/// g = normalized linear depth). After successful Initialize, it writes to
/// FrameSchedule.AoTexture. The composition point resolves the ao variant by name and
/// multiplies it in linear space before ACES
/// (scene * lerp(1, ao, AoIntensity), see contract clause 5).
/// The chain textures are created at half the current backbuffer resolution during
/// initialization and are recreated in place on resize (same shape as Bloom contract
/// clause 2). The depth source is sampled with normalized UVs, so source size changes
/// are handled automatically.
///
/// Binding layout (declaration order defines the cross-backend slot convention;
/// see ComputeBindingType summary):
/// gtaoMain [0] Params 32B (near, far, tanHalfFovY, aspect, radius, dstW, dstH, _pad)
///          [1] DepthTexture (SceneDepth target, texel load without sampler)
///          [2] StorageWrite rgba8unorm
/// gtaoBlur [0] Params 16B (dstW, dstH, dirX, dirY) [1] SampledTexture [2] StorageWrite rgba8unorm
///
/// View-space reconstruction is the exact inverse of the engine projection convention
/// (LH + [0,1] depth + UV Y-down implies view-space Y is flipped):
/// z = near*far/(far - d*(far-near));
/// P = (ndc.x*tanHalfFovY*aspect*z, ndc.y*tanHalfFovY*z, z).
///
/// Step B was shaped visually on D3D12 (HLSL first). Step C completed the aligned
/// four-source implementation after DepthTexture support landed on all backends
/// (integration formulas, falloff, and blur weights are numerically identical and
/// verified on all four backends).
/// </summary>
public sealed class GtaoEffect : ComputeEffect
{
    /// <summary>Registered prefix for chain textures; raw, blurx, and ao can all be attached directly to Sprite2D for debugging, following the Bloom precedent.</summary>
    public const string TextureNamePrefix = "compute://gtao/";

    /// <summary>Registered name of the final output (after blurY); also written to FrameSchedule.AoTexture after successful Initialize.</summary>
    public const string TextureName = TextureNamePrefix + "ao";

    const string RawName = TextureNamePrefix + "raw";

    const string BlurXName = TextureNamePrefix + "blurx";

    ComputeKernel? _main;
    ComputeKernel? _blur;

    // Half-resolution size. Set during Initialize and updated in place on resize.
    uint _width;
    uint _height;

    // Resource reference arrays used by each dispatch. Built during Initialize and reused
    // every frame with zero allocations. They cannot use stackalloc because they contain
    // reference types; see ComputeDispatchArgs summary.
    ComputeResourceRef[]? _mainRes;
    ComputeResourceRef[]? _blurXRes;
    ComputeResourceRef[]? _blurYRes;

    public override string Name => "gtao";

    public override ComputePhase Phase => ComputePhase.AfterScene;

    public override bool Initialize(IGraphics g)
    {
        // Quality gating plus shape dependency: AO mode must be GTAO and SceneDepth must
        // exist. If AO is Off or falls back, SceneDepth stays null and the whole effect remains inactive.
        if (RenderQuality.Current.AmbientOcclusion != AoMode.Gtao || FrameSchedule.SceneDepth == null)
            return false;

        _main = g.CreateComputeKernel(new ComputeKernelDesc
        {
            Name = "gtaoMain",
            Source = new ShaderSourceSet
            {
                Hlsl = SourceMainHlsl,
                Glsl = SourceMainGlsl,
                Msl = SourceMainMsl,
                Wgsl = SourceMainWgsl,
                EntryPoint = "CSMain",
            },
            Bindings = new[]
            {
                new ComputeBindingDesc { Type = ComputeBindingType.Params, SizeInBytes = 32 },
                new ComputeBindingDesc { Type = ComputeBindingType.DepthTexture },
                new ComputeBindingDesc { Type = ComputeBindingType.StorageTextureWrite },
            },
        });
        _blur = g.CreateComputeKernel(new ComputeKernelDesc
        {
            Name = "gtaoBlur",
            Source = new ShaderSourceSet
            {
                Hlsl = SourceBlurHlsl,
                Glsl = SourceBlurGlsl,
                Msl = SourceBlurMsl,
                Wgsl = SourceBlurWgsl,
                EntryPoint = "CSMain",
            },
            Bindings = new[]
            {
                new ComputeBindingDesc { Type = ComputeBindingType.Params, SizeInBytes = 16 },
                new ComputeBindingDesc { Type = ComputeBindingType.SampledTexture },
                new ComputeBindingDesc { Type = ComputeBindingType.StorageTextureWrite },
            },
        });
        if (_main == null || _blur == null)
        {
            Dispose();
            return false;
        }

        // Half resolution (minimum side clamped to >= 8, same behavior as Bloom).
        var res = DeviceServices.BaseApp.DeviceResolution;
        _width = Math.Max(8u, (uint)res.X / 2);
        _height = Math.Max(8u, (uint)res.Y / 2);

        g.CreateComputeTexture(RawName, _width, _height);
        g.CreateComputeTexture(BlurXName, _width, _height);
        g.CreateComputeTexture(TextureName, _width, _height);

        _mainRes = new ComputeResourceRef[] { FrameSchedule.SceneDepth, RawName };
        _blurXRes = new ComputeResourceRef[] { RawName, BlurXName };
        _blurYRes = new ComputeResourceRef[] { BlurXName, TextureName };

        FrameSchedule.AoTexture = TextureName;
        return true;
    }

    public override void OnResize(IGraphics g)
    {
        // Recreate the three chain textures in place at half resolution
        // (minimum side clamped to >= 8, same behavior as Bloom).
        var res = DeviceServices.BaseApp.DeviceResolution;
        _width = Math.Max(8u, (uint)res.X / 2);
        _height = Math.Max(8u, (uint)res.Y / 2);
        g.CreateComputeTexture(RawName, _width, _height);
        g.CreateComputeTexture(BlurXName, _width, _height);
        g.CreateComputeTexture(TextureName, _width, _height);
        // Resource reference arrays do not depend on size because the names stay fixed.
    }

    public override void Record(IGraphics g)
    {
        var camera = DeviceServices.BaseApp.Camera;
        uint gx = (_width + 7) / 8;
        uint gy = (_height + 7) / 8;

        // 1) gtaoMain: SceneDepth -> raw.
        // Reconstruction parameters are uploaded from Camera3D every frame. If aspect is
        // not ready on the first frame, fall back to the chain texture aspect ratio,
        // which matches the full-resolution depth aspect and avoids distortion.
        Span<float> p = stackalloc float[8];
        p[0] = camera.Near;
        p[1] = camera.Far;
        p[2] = MathF.Tan(camera.FovY * 0.5f);
        p[3] = camera.Aspect > 0f ? camera.Aspect : (float)_width / _height;
        p[4] = RenderQuality.Current.AoRadius;
        p[5] = _width;
        p[6] = _height;
        g.DispatchCompute(new ComputeDispatchArgs
        {
            Kernel = _main!,
            Params = MemoryMarshal.AsBytes(p),
            Resources = _mainRes,
            GroupsX = gx,
            GroupsY = gy,
            GroupsZ = 1,
        });

        // 2) gtaoBlur X/Y passes: raw -> blurx -> ao.
        // Depth-aware separable Gaussian blur, with direction supplied through Params.
        Span<float> b = stackalloc float[4];
        b[0] = _width;
        b[1] = _height;
        b[2] = 1f;
        b[3] = 0f;
        g.DispatchCompute(new ComputeDispatchArgs
        {
            Kernel = _blur!,
            Params = MemoryMarshal.AsBytes(b),
            Resources = _blurXRes,
            GroupsX = gx,
            GroupsY = gy,
            GroupsZ = 1,
        });

        b[2] = 0f;
        b[3] = 1f;
        g.DispatchCompute(new ComputeDispatchArgs
        {
            Kernel = _blur!,
            Params = MemoryMarshal.AsBytes(b),
            Resources = _blurYRes,
            GroupsX = gx,
            GroupsY = gy,
            GroupsZ = 1,
        });
    }

    public void Dispose()
    {
        _main?.Dispose();
        _main = null;
        _blur?.Dispose();
        _blur = null;
        if (FrameSchedule.AoTexture == TextureName)
            FrameSchedule.AoTexture = null;
    }

    // Shader sources (single source of truth; slots follow the class-level binding
    // convention; workgroup is fixed at 8x8x1).

    /// <summary>D3D12 cs_5_0 (fxc; single exit avoids X4000). GTAO slice integration follows the
    /// XeGTAO form. Sky / clear pixels (d >= 1) write ao = 1 directly, and falloff pushes
    /// samples beyond the radius back below the horizon so they contribute nothing.</summary>
    const string SourceMainHlsl = @"
static const float PI = 3.14159265359;

cbuffer GtaoParams : register(b0)
{
    float uNear;
    float uFar;
    float uTanHalfFovY;
    float uAspect;
    float uRadius;
    float uDstWidth;
    float uDstHeight;
    float uPad0;
};

Texture2D<float> uDepth : register(t0);
RWTexture2D<float4> uOutput : register(u0);

float LinearizeDepth(float d)
{
    return uNear * uFar / (uFar - d * (uFar - uNear));
}

// UV Y points downward, so NDC Y is flipped. LH view space uses +X right, +Y up, +Z forward.
float3 ViewPos(float2 uv, float z)
{
    float2 ndc = float2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
    return float3(ndc.x * uTanHalfFovY * uAspect * z, ndc.y * uTanHalfFovY * z, z);
}

float DepthAt(float2 uv, uint2 dim)
{
    int2 c = clamp(int2(uv * float2(dim)), int2(0, 0), int2(dim) - 1);
    return uDepth.Load(int3(c, 0));
}

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x < (uint)uDstWidth && id.y < (uint)uDstHeight)
    {
        uint dw, dh;
        uDepth.GetDimensions(dw, dh);
        uint2 dim = uint2(dw, dh);
        float2 dst = float2(uDstWidth, uDstHeight);
        float2 uv = (float2(id.xy) + 0.5) / dst;

        float d = DepthAt(uv, dim);
        float4 result = float4(1.0, 1.0, 0.0, 1.0); // Sky / clear: unoccluded + farthest depth

        if (d < 1.0)
        {
            float z = LinearizeDepth(d);
            float3 P = ViewPos(uv, z);
            float3 V = normalize(-P);

            // Reconstruct the normal from depth using centered differences over four neighbors,
            // choosing the smaller-|Delta z| side to avoid depth-cliff artifacts.
            float2 texel = 1.0 / dst;
            float zl = LinearizeDepth(DepthAt(uv - float2(texel.x, 0.0), dim));
            float zr = LinearizeDepth(DepthAt(uv + float2(texel.x, 0.0), dim));
            float zu = LinearizeDepth(DepthAt(uv - float2(0.0, texel.y), dim));
            float zd = LinearizeDepth(DepthAt(uv + float2(0.0, texel.y), dim));
            float3 dpdx = abs(zr - z) < abs(z - zl)
                ? ViewPos(uv + float2(texel.x, 0.0), zr) - P
                : P - ViewPos(uv - float2(texel.x, 0.0), zl);
            float3 dpdy = abs(zd - z) < abs(z - zu)
                ? ViewPos(uv + float2(0.0, texel.y), zd) - P
                : P - ViewPos(uv - float2(0.0, texel.y), zu);
            float3 N = normalize(cross(dpdy, dpdx));
            N = dot(N, V) < 0.0 ? -N : N;

            // IGN (Interleaved Gradient Noise, pure ALU): rotate the slice start angle per pixel.
            float ign = frac(52.9829189 * frac(0.06711056 * id.x + 0.00583715 * id.y));

            // Convert world-space radius to a UV offset
            // (full height = 2*z*tanHalfFovY, and the X axis is divided by aspect).
            float radiusUv = uRadius / (2.0 * z * uTanHalfFovY);
            float2 radiusUv2 = radiusUv * float2(1.0 / uAspect, 1.0);

            float visibility = 0.0;

            [unroll]
            for (int s = 0; s < 2; s++)
            {
                float phi = (ign + s * 0.5) * PI;
                float2 dir2 = float2(cos(phi), sin(phi));
                float3 sliceDir = float3(dir2.x, -dir2.y, 0.0);

                // Horizon cosines on both sides. Start at -1, and let falloff attenuate
                // beyond-radius samples back to -1 so they contribute nothing.
                float hc1 = -1.0;
                float hc2 = -1.0;
                [unroll]
                for (int j = 0; j < 4; j++)
                {
                    float t = (j + 1.0) / 4.0;
                    float2 offs = dir2 * (t * radiusUv2);

                    float2 suv = uv - offs;
                    float3 S = ViewPos(suv, LinearizeDepth(DepthAt(suv, dim)));
                    float3 w = S - P;
                    float l = max(length(w), 1e-4);
                    hc1 = max(hc1, lerp(-1.0, dot(w, V) / l, saturate(1.0 - l / uRadius)));

                    suv = uv + offs;
                    S = ViewPos(suv, LinearizeDepth(DepthAt(suv, dim)));
                    w = S - P;
                    l = max(length(w), 1e-4);
                    hc2 = max(hc2, lerp(-1.0, dot(w, V) / l, saturate(1.0 - l / uRadius)));
                }

                // GTAO slice integration (XeGTAO form): project the normal into the slice
                // plane and clamp the normal hemisphere with the horizon angles from both sides.
                float3 axis = normalize(cross(sliceDir, V));
                float3 orthoDir = sliceDir - V * dot(sliceDir, V);
                float3 projN = N - axis * dot(N, axis);
                float projNLen = max(length(projN), 1e-4);
                float cosGamma = clamp(dot(projN, V) / projNLen, -1.0, 1.0);
                float gamma = sign(dot(projN, orthoDir)) * acos(cosGamma);

                float h1 = gamma + max(-acos(clamp(hc1, -1.0, 1.0)) - gamma, -PI * 0.5);
                float h2 = gamma + min(acos(clamp(hc2, -1.0, 1.0)) - gamma, PI * 0.5);
                float a = 0.25 * ((-cos(2.0 * h1 - gamma) + cos(gamma) + 2.0 * h1 * sin(gamma))
                                + (-cos(2.0 * h2 - gamma) + cos(gamma) + 2.0 * h2 * sin(gamma)));
                visibility += projNLen * a;
            }

            float ao = saturate(visibility * 0.5);
            float zNorm = saturate((z - uNear) / (uFar - uNear));
            result = float4(ao, zNorm, 0.0, 1.0);
        }

        uOutput[id.xy] = result;
    }
}
";

    /// <summary>Vulkan GLSL 450 (glslang compiles to SPIR-V at runtime; entry point is always main).
    /// Depth is bound as CombinedImageSampler (immutable point sampler, see VKComputeKernel)
    /// and read per texel with texelFetch without filtering. dpdx/dpdy are GLSL built-ins,
    /// so the local variables are renamed to dPdx/dPdy only; the math is identical.</summary>
    const string SourceMainGlsl = @"#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

const float PI = 3.14159265359;

layout(push_constant) uniform GtaoParams
{
    float uNear;
    float uFar;
    float uTanHalfFovY;
    float uAspect;
    float uRadius;
    float uDstWidth;
    float uDstHeight;
    float uPad0;
};

layout(binding = 1) uniform sampler2D uDepth;
layout(binding = 2, rgba8) uniform writeonly image2D uOutput;

float LinearizeDepth(float d)
{
    return uNear * uFar / (uFar - d * (uFar - uNear));
}

// UV Y points downward, so NDC Y is flipped. LH view space uses +X right, +Y up, +Z forward.
vec3 ViewPos(vec2 uv, float z)
{
    vec2 ndc = vec2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
    return vec3(ndc.x * uTanHalfFovY * uAspect * z, ndc.y * uTanHalfFovY * z, z);
}

float DepthAt(vec2 uv, ivec2 dim)
{
    ivec2 c = clamp(ivec2(uv * vec2(dim)), ivec2(0), dim - 1);
    return texelFetch(uDepth, c, 0).r;
}

void main()
{
    uvec2 id = gl_GlobalInvocationID.xy;
    if (id.x < uint(uDstWidth) && id.y < uint(uDstHeight))
    {
        ivec2 dim = textureSize(uDepth, 0);
        vec2 dst = vec2(uDstWidth, uDstHeight);
        vec2 uv = (vec2(id) + 0.5) / dst;

        float d = DepthAt(uv, dim);
        vec4 result = vec4(1.0, 1.0, 0.0, 1.0); // Sky / clear: unoccluded + farthest depth

        if (d < 1.0)
        {
            float z = LinearizeDepth(d);
            vec3 P = ViewPos(uv, z);
            vec3 V = normalize(-P);

            // Reconstruct the normal from depth using centered differences over four neighbors,
            // choosing the smaller-|Delta z| side to avoid depth-cliff artifacts.
            vec2 texel = 1.0 / dst;
            float zl = LinearizeDepth(DepthAt(uv - vec2(texel.x, 0.0), dim));
            float zr = LinearizeDepth(DepthAt(uv + vec2(texel.x, 0.0), dim));
            float zu = LinearizeDepth(DepthAt(uv - vec2(0.0, texel.y), dim));
            float zd = LinearizeDepth(DepthAt(uv + vec2(0.0, texel.y), dim));
            vec3 dPdx = abs(zr - z) < abs(z - zl)
                ? ViewPos(uv + vec2(texel.x, 0.0), zr) - P
                : P - ViewPos(uv - vec2(texel.x, 0.0), zl);
            vec3 dPdy = abs(zd - z) < abs(z - zu)
                ? ViewPos(uv + vec2(0.0, texel.y), zd) - P
                : P - ViewPos(uv - vec2(0.0, texel.y), zu);
            vec3 N = normalize(cross(dPdy, dPdx));
            N = dot(N, V) < 0.0 ? -N : N;

            // IGN (Interleaved Gradient Noise, pure ALU): rotate the slice start angle per pixel.
            float ign = fract(52.9829189 * fract(0.06711056 * float(id.x) + 0.00583715 * float(id.y)));

            // Convert world-space radius to a UV offset
            // (full height = 2*z*tanHalfFovY, and the X axis is divided by aspect).
            float radiusUv = uRadius / (2.0 * z * uTanHalfFovY);
            vec2 radiusUv2 = radiusUv * vec2(1.0 / uAspect, 1.0);

            float visibility = 0.0;

            for (int s = 0; s < 2; s++)
            {
                float phi = (ign + float(s) * 0.5) * PI;
                vec2 dir2 = vec2(cos(phi), sin(phi));
                vec3 sliceDir = vec3(dir2.x, -dir2.y, 0.0);

                // Horizon cosines on both sides. Start at -1, and let falloff attenuate
                // beyond-radius samples back to -1 so they contribute nothing.
                float hc1 = -1.0;
                float hc2 = -1.0;
                for (int j = 0; j < 4; j++)
                {
                    float t = (float(j) + 1.0) / 4.0;
                    vec2 offs = dir2 * (t * radiusUv2);

                    vec2 suv = uv - offs;
                    vec3 S = ViewPos(suv, LinearizeDepth(DepthAt(suv, dim)));
                    vec3 w = S - P;
                    float l = max(length(w), 1e-4);
                    hc1 = max(hc1, mix(-1.0, dot(w, V) / l, clamp(1.0 - l / uRadius, 0.0, 1.0)));

                    suv = uv + offs;
                    S = ViewPos(suv, LinearizeDepth(DepthAt(suv, dim)));
                    w = S - P;
                    l = max(length(w), 1e-4);
                    hc2 = max(hc2, mix(-1.0, dot(w, V) / l, clamp(1.0 - l / uRadius, 0.0, 1.0)));
                }

                // GTAO slice integration (XeGTAO form): project the normal into the slice
                // plane and clamp the normal hemisphere with the horizon angles from both sides.
                vec3 axis = normalize(cross(sliceDir, V));
                vec3 orthoDir = sliceDir - V * dot(sliceDir, V);
                vec3 projN = N - axis * dot(N, axis);
                float projNLen = max(length(projN), 1e-4);
                float cosGamma = clamp(dot(projN, V) / projNLen, -1.0, 1.0);
                float gamma = sign(dot(projN, orthoDir)) * acos(cosGamma);

                float h1 = gamma + max(-acos(clamp(hc1, -1.0, 1.0)) - gamma, -PI * 0.5);
                float h2 = gamma + min(acos(clamp(hc2, -1.0, 1.0)) - gamma, PI * 0.5);
                float a = 0.25 * ((-cos(2.0 * h1 - gamma) + cos(gamma) + 2.0 * h1 * sin(gamma))
                                + (-cos(2.0 * h2 - gamma) + cos(gamma) + 2.0 * h2 * sin(gamma)));
                visibility += projNLen * a;
            }

            float ao = clamp(visibility * 0.5, 0.0, 1.0);
            float zNorm = clamp((z - uNear) / (uFar - uNear), 0.0, 1.0);
            result = vec4(ao, zNorm, 0.0, 1.0);
        }

        imageStore(uOutput, ivec2(id), result);
    }
}
";

    /// <summary>Metal MSL kernel. Depth uses depth2d + access::read for per-texel reads without
    /// a sampler. Helper functions explicitly pass params and uDepth because MSL has no
    /// global resource bindings; the math is otherwise identical.</summary>
    const string SourceMainMsl = @"
#include <metal_stdlib>
using namespace metal;

constant float PI = 3.14159265359;

struct GtaoParams
{
    float uNear;
    float uFar;
    float uTanHalfFovY;
    float uAspect;
    float uRadius;
    float uDstWidth;
    float uDstHeight;
    float uPad0;
};

static float LinearizeDepth(float d, constant GtaoParams& p)
{
    return p.uNear * p.uFar / (p.uFar - d * (p.uFar - p.uNear));
}

// UV Y points downward, so NDC Y is flipped. LH view space uses +X right, +Y up, +Z forward.
static float3 ViewPos(float2 uv, float z, constant GtaoParams& p)
{
    float2 ndc = float2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
    return float3(ndc.x * p.uTanHalfFovY * p.uAspect * z, ndc.y * p.uTanHalfFovY * z, z);
}

static float DepthAt(float2 uv, uint2 dim, depth2d<float, access::read> uDepth)
{
    int2 c = clamp(int2(uv * float2(dim)), int2(0, 0), int2(dim) - 1);
    return uDepth.read(uint2(c));
}

kernel void CSMain(
    constant GtaoParams& params [[buffer(0)]],
    depth2d<float, access::read> uDepth [[texture(0)]],
    texture2d<float, access::write> uOutput [[texture(1)]],
    uint2 id [[thread_position_in_grid]])
{
    if (id.x < (uint)params.uDstWidth && id.y < (uint)params.uDstHeight)
    {
        uint2 dim = uint2(uDepth.get_width(), uDepth.get_height());
        float2 dst = float2(params.uDstWidth, params.uDstHeight);
        float2 uv = (float2(id) + 0.5) / dst;

        float d = DepthAt(uv, dim, uDepth);
        float4 result = float4(1.0, 1.0, 0.0, 1.0); // Sky / clear: unoccluded + farthest depth

        if (d < 1.0)
        {
            float z = LinearizeDepth(d, params);
            float3 P = ViewPos(uv, z, params);
            float3 V = normalize(-P);

            // Reconstruct the normal from depth using centered differences over four neighbors,
            // choosing the smaller-|Delta z| side to avoid depth-cliff artifacts.
            float2 texel = 1.0 / dst;
            float zl = LinearizeDepth(DepthAt(uv - float2(texel.x, 0.0), dim, uDepth), params);
            float zr = LinearizeDepth(DepthAt(uv + float2(texel.x, 0.0), dim, uDepth), params);
            float zu = LinearizeDepth(DepthAt(uv - float2(0.0, texel.y), dim, uDepth), params);
            float zd = LinearizeDepth(DepthAt(uv + float2(0.0, texel.y), dim, uDepth), params);
            float3 dPdx = abs(zr - z) < abs(z - zl)
                ? ViewPos(uv + float2(texel.x, 0.0), zr, params) - P
                : P - ViewPos(uv - float2(texel.x, 0.0), zl, params);
            float3 dPdy = abs(zd - z) < abs(z - zu)
                ? ViewPos(uv + float2(0.0, texel.y), zd, params) - P
                : P - ViewPos(uv - float2(0.0, texel.y), zu, params);
            float3 N = normalize(cross(dPdy, dPdx));
            N = dot(N, V) < 0.0 ? -N : N;

            // IGN (Interleaved Gradient Noise, pure ALU): rotate the slice start angle per pixel.
            float ign = fract(52.9829189 * fract(0.06711056 * id.x + 0.00583715 * id.y));

            // Convert world-space radius to a UV offset
            // (full height = 2*z*tanHalfFovY, and the X axis is divided by aspect).
            float radiusUv = params.uRadius / (2.0 * z * params.uTanHalfFovY);
            float2 radiusUv2 = radiusUv * float2(1.0 / params.uAspect, 1.0);

            float visibility = 0.0;

            for (int s = 0; s < 2; s++)
            {
                float phi = (ign + s * 0.5) * PI;
                float2 dir2 = float2(cos(phi), sin(phi));
                float3 sliceDir = float3(dir2.x, -dir2.y, 0.0);

                // Horizon cosines on both sides. Start at -1, and let falloff attenuate
                // beyond-radius samples back to -1 so they contribute nothing.
                float hc1 = -1.0;
                float hc2 = -1.0;
                for (int j = 0; j < 4; j++)
                {
                    float t = (j + 1.0) / 4.0;
                    float2 offs = dir2 * (t * radiusUv2);

                    float2 suv = uv - offs;
                    float3 S = ViewPos(suv, LinearizeDepth(DepthAt(suv, dim, uDepth), params), params);
                    float3 w = S - P;
                    float l = max(length(w), 1e-4);
                    hc1 = max(hc1, mix(-1.0, dot(w, V) / l, saturate(1.0 - l / params.uRadius)));

                    suv = uv + offs;
                    S = ViewPos(suv, LinearizeDepth(DepthAt(suv, dim, uDepth), params), params);
                    w = S - P;
                    l = max(length(w), 1e-4);
                    hc2 = max(hc2, mix(-1.0, dot(w, V) / l, saturate(1.0 - l / params.uRadius)));
                }

                // GTAO slice integration (XeGTAO form): project the normal into the slice
                // plane and clamp the normal hemisphere with the horizon angles from both sides.
                float3 axis = normalize(cross(sliceDir, V));
                float3 orthoDir = sliceDir - V * dot(sliceDir, V);
                float3 projN = N - axis * dot(N, axis);
                float projNLen = max(length(projN), 1e-4);
                float cosGamma = clamp(dot(projN, V) / projNLen, -1.0, 1.0);
                float gamma = sign(dot(projN, orthoDir)) * acos(cosGamma);

                float h1 = gamma + max(-acos(clamp(hc1, -1.0, 1.0)) - gamma, -PI * 0.5);
                float h2 = gamma + min(acos(clamp(hc2, -1.0, 1.0)) - gamma, PI * 0.5);
                float a = 0.25 * ((-cos(2.0 * h1 - gamma) + cos(gamma) + 2.0 * h1 * sin(gamma))
                                + (-cos(2.0 * h2 - gamma) + cos(gamma) + 2.0 * h2 * sin(gamma)));
                visibility += projNLen * a;
            }

            float ao = saturate(visibility * 0.5);
            float zNorm = saturate((z - params.uNear) / (params.uFar - params.uNear));
            result = float4(ao, zNorm, 0.0, 1.0);
        }

        uOutput.write(result, id);
    }
}
";

    /// <summary>WebGPU WGSL (delivered through the interop layer). Depth uses
    /// texture_depth_2d + textureLoad (returns f32, no sampler). Ternary expressions are
    /// written with select using the parameter order false, true, cond; the math is identical.</summary>
    const string SourceMainWgsl = @"
const PI : f32 = 3.14159265359;

struct GtaoParams
{
    uNear : f32,
    uFar : f32,
    uTanHalfFovY : f32,
    uAspect : f32,
    uRadius : f32,
    uDstWidth : f32,
    uDstHeight : f32,
    uPad0 : f32,
};

@group(0) @binding(0) var<uniform> params : GtaoParams;
@group(0) @binding(1) var uDepth : texture_depth_2d;
@group(0) @binding(2) var uOutput : texture_storage_2d<rgba8unorm, write>;

fn LinearizeDepth(d : f32) -> f32
{
    return params.uNear * params.uFar / (params.uFar - d * (params.uFar - params.uNear));
}

// UV Y points downward, so NDC Y is flipped. LH view space uses +X right, +Y up, +Z forward.
fn ViewPos(uv : vec2<f32>, z : f32) -> vec3<f32>
{
    let ndc = vec2<f32>(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
    return vec3<f32>(ndc.x * params.uTanHalfFovY * params.uAspect * z, ndc.y * params.uTanHalfFovY * z, z);
}

fn DepthAt(uv : vec2<f32>, dim : vec2<i32>) -> f32
{
    let c = clamp(vec2<i32>(uv * vec2<f32>(dim)), vec2<i32>(0), dim - vec2<i32>(1));
    return textureLoad(uDepth, c, 0);
}

@compute @workgroup_size(8, 8, 1)
fn CSMain(@builtin(global_invocation_id) id : vec3<u32>)
{
    if (id.x < u32(params.uDstWidth) && id.y < u32(params.uDstHeight))
    {
        let dim = vec2<i32>(textureDimensions(uDepth));
        let dst = vec2<f32>(params.uDstWidth, params.uDstHeight);
        let uv = (vec2<f32>(f32(id.x), f32(id.y)) + vec2<f32>(0.5)) / dst;

        let d = DepthAt(uv, dim);
        var result = vec4<f32>(1.0, 1.0, 0.0, 1.0); // Sky / clear: unoccluded + farthest depth

        if (d < 1.0)
        {
            let z = LinearizeDepth(d);
            let P = ViewPos(uv, z);
            let V = normalize(-P);

            // Reconstruct the normal from depth using centered differences over four neighbors,
            // choosing the smaller-|Delta z| side to avoid depth-cliff artifacts.
            let texel = vec2<f32>(1.0) / dst;
            let zl = LinearizeDepth(DepthAt(uv - vec2<f32>(texel.x, 0.0), dim));
            let zr = LinearizeDepth(DepthAt(uv + vec2<f32>(texel.x, 0.0), dim));
            let zu = LinearizeDepth(DepthAt(uv - vec2<f32>(0.0, texel.y), dim));
            let zd = LinearizeDepth(DepthAt(uv + vec2<f32>(0.0, texel.y), dim));
            let dPdx = select(
                P - ViewPos(uv - vec2<f32>(texel.x, 0.0), zl),
                ViewPos(uv + vec2<f32>(texel.x, 0.0), zr) - P,
                abs(zr - z) < abs(z - zl));
            let dPdy = select(
                P - ViewPos(uv - vec2<f32>(0.0, texel.y), zu),
                ViewPos(uv + vec2<f32>(0.0, texel.y), zd) - P,
                abs(zd - z) < abs(z - zu));
            var N = normalize(cross(dPdy, dPdx));
            N = select(N, -N, dot(N, V) < 0.0);

            // IGN (Interleaved Gradient Noise, pure ALU): rotate the slice start angle per pixel.
            let ign = fract(52.9829189 * fract(0.06711056 * f32(id.x) + 0.00583715 * f32(id.y)));

            // Convert world-space radius to a UV offset
            // (full height = 2*z*tanHalfFovY, and the X axis is divided by aspect).
            let radiusUv = params.uRadius / (2.0 * z * params.uTanHalfFovY);
            let radiusUv2 = radiusUv * vec2<f32>(1.0 / params.uAspect, 1.0);

            var visibility = 0.0;

            for (var s : i32 = 0; s < 2; s = s + 1)
            {
                let phi = (ign + f32(s) * 0.5) * PI;
                let dir2 = vec2<f32>(cos(phi), sin(phi));
                let sliceDir = vec3<f32>(dir2.x, -dir2.y, 0.0);

                // Horizon cosines on both sides. Start at -1, and let falloff attenuate
                // beyond-radius samples back to -1 so they contribute nothing.
                var hc1 = -1.0;
                var hc2 = -1.0;
                for (var j : i32 = 0; j < 4; j = j + 1)
                {
                    let t = (f32(j) + 1.0) / 4.0;
                    let offs = dir2 * (t * radiusUv2);

                    var suv = uv - offs;
                    var S = ViewPos(suv, LinearizeDepth(DepthAt(suv, dim)));
                    var w = S - P;
                    var l = max(length(w), 1e-4);
                    hc1 = max(hc1, mix(-1.0, dot(w, V) / l, saturate(1.0 - l / params.uRadius)));

                    suv = uv + offs;
                    S = ViewPos(suv, LinearizeDepth(DepthAt(suv, dim)));
                    w = S - P;
                    l = max(length(w), 1e-4);
                    hc2 = max(hc2, mix(-1.0, dot(w, V) / l, saturate(1.0 - l / params.uRadius)));
                }

                // GTAO slice integration (XeGTAO form): project the normal into the slice
                // plane and clamp the normal hemisphere with the horizon angles from both sides.
                let axis = normalize(cross(sliceDir, V));
                let orthoDir = sliceDir - V * dot(sliceDir, V);
                let projN = N - axis * dot(N, axis);
                let projNLen = max(length(projN), 1e-4);
                let cosGamma = clamp(dot(projN, V) / projNLen, -1.0, 1.0);
                let gamma = sign(dot(projN, orthoDir)) * acos(cosGamma);

                let h1 = gamma + max(-acos(clamp(hc1, -1.0, 1.0)) - gamma, -PI * 0.5);
                let h2 = gamma + min(acos(clamp(hc2, -1.0, 1.0)) - gamma, PI * 0.5);
                let a = 0.25 * ((-cos(2.0 * h1 - gamma) + cos(gamma) + 2.0 * h1 * sin(gamma))
                              + (-cos(2.0 * h2 - gamma) + cos(gamma) + 2.0 * h2 * sin(gamma)));
                visibility = visibility + projNLen * a;
            }

            let ao = saturate(visibility * 0.5);
            let zNorm = saturate((z - params.uNear) / (params.uFar - params.uNear));
            result = vec4<f32>(ao, zNorm, 0.0, 1.0);
        }

        textureStore(uOutput, vec2<i32>(i32(id.x), i32(id.y)), result);
    }
}
";

    /// <summary>D3D12 cs_5_0 (fxc; single exit). Depth-aware separable 9-tap Gaussian blur:
    /// normalized linear depth in the g channel serves as the bilateral weight
    /// (larger depth differences decay exponentially, preventing light leaks at edges).
    /// The g channel is passed through unchanged for the next pass.</summary>
    const string SourceBlurHlsl = @"
cbuffer BlurParams : register(b0)
{
    float uDstWidth;
    float uDstHeight;
    float uDirX;
    float uDirY;
};

Texture2D<float4> uSrc : register(t0);
SamplerState uLinearClamp : register(s0);
RWTexture2D<float4> uOutput : register(u0);

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x < (uint)uDstWidth && id.y < (uint)uDstHeight)
    {
        const float w[5] = { 0.2270270270, 0.1945945946, 0.1216216216, 0.0540540541, 0.0162162162 };

        int2 c = int2(id.xy);
        int2 dir = int2(uDirX, uDirY);
        int2 maxC = int2(uDstWidth, uDstHeight) - 1;

        float4 center = uSrc.Load(int3(c, 0));
        float sumAo = center.r * w[0];
        float sumW = w[0];

        [unroll]
        for (int j = 1; j <= 4; j++)
        {
            float4 s1 = uSrc.Load(int3(clamp(c - dir * j, int2(0, 0), maxC), 0));
            float4 s2 = uSrc.Load(int3(clamp(c + dir * j, int2(0, 0), maxC), 0));
            float w1 = w[j] * exp(-abs(s1.g - center.g) * 400.0);
            float w2 = w[j] * exp(-abs(s2.g - center.g) * 400.0);
            sumAo += s1.r * w1 + s2.r * w2;
            sumW += w1 + w2;
        }

        uOutput[id.xy] = float4(sumAo / sumW, center.g, 0.0, 1.0);
    }
}
";

    /// <summary>Vulkan GLSL 450. The source is a regular color texture
    /// (CombinedImageSampler + linear sampler), but like HLSL it still uses texelFetch
    /// for per-texel reads, with blur weights and bilateral math kept identical.</summary>
    const string SourceBlurGlsl = @"#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(push_constant) uniform BlurParams
{
    float uDstWidth;
    float uDstHeight;
    float uDirX;
    float uDirY;
};

layout(binding = 1) uniform sampler2D uSrc;
layout(binding = 2, rgba8) uniform writeonly image2D uOutput;

void main()
{
    uvec2 id = gl_GlobalInvocationID.xy;
    if (id.x < uint(uDstWidth) && id.y < uint(uDstHeight))
    {
        const float w[5] = float[5](0.2270270270, 0.1945945946, 0.1216216216, 0.0540540541, 0.0162162162);

        ivec2 c = ivec2(id);
        ivec2 dir = ivec2(int(uDirX), int(uDirY));
        ivec2 maxC = ivec2(int(uDstWidth), int(uDstHeight)) - 1;

        vec4 center = texelFetch(uSrc, c, 0);
        float sumAo = center.r * w[0];
        float sumW = w[0];

        for (int j = 1; j <= 4; j++)
        {
            vec4 s1 = texelFetch(uSrc, clamp(c - dir * j, ivec2(0), maxC), 0);
            vec4 s2 = texelFetch(uSrc, clamp(c + dir * j, ivec2(0), maxC), 0);
            float w1 = w[j] * exp(-abs(s1.g - center.g) * 400.0);
            float w2 = w[j] * exp(-abs(s2.g - center.g) * 400.0);
            sumAo += s1.r * w1 + s2.r * w2;
            sumW += w1 + w2;
        }

        imageStore(uOutput, ivec2(id), vec4(sumAo / sumW, center.g, 0.0, 1.0));
    }
}
";

    /// <summary>Metal MSL kernel. Although the texture uses the access::sample form
    /// (matching the SampledTexture slot convention with sampler(0)), the implementation
    /// actually uses read for per-texel access, with identical blur weights and bilateral math.</summary>
    const string SourceBlurMsl = @"
#include <metal_stdlib>
using namespace metal;

struct BlurParams
{
    float uDstWidth;
    float uDstHeight;
    float uDirX;
    float uDirY;
};

kernel void CSMain(
    constant BlurParams& params [[buffer(0)]],
    texture2d<float, access::sample> uSrc [[texture(0)]],
    texture2d<float, access::write> uOutput [[texture(1)]],
    sampler uLinearClamp [[sampler(0)]],
    uint2 id [[thread_position_in_grid]])
{
    if (id.x < (uint)params.uDstWidth && id.y < (uint)params.uDstHeight)
    {
        const float w[5] = { 0.2270270270, 0.1945945946, 0.1216216216, 0.0540540541, 0.0162162162 };

        int2 c = int2(id);
        int2 dir = int2(params.uDirX, params.uDirY);
        int2 maxC = int2(params.uDstWidth, params.uDstHeight) - 1;

        float4 center = uSrc.read(uint2(c));
        float sumAo = center.r * w[0];
        float sumW = w[0];

        for (int j = 1; j <= 4; j++)
        {
            float4 s1 = uSrc.read(uint2(clamp(c - dir * j, int2(0, 0), maxC)));
            float4 s2 = uSrc.read(uint2(clamp(c + dir * j, int2(0, 0), maxC)));
            float w1 = w[j] * exp(-abs(s1.g - center.g) * 400.0);
            float w2 = w[j] * exp(-abs(s2.g - center.g) * 400.0);
            sumAo += s1.r * w1 + s2.r * w2;
            sumW += w1 + w2;
        }

        uOutput.write(float4(sumAo / sumW, center.g, 0.0, 1.0), id);
    }
}
";

    /// <summary>WebGPU WGSL. The SampledTexture slot automatically carries a sampler at
    /// @binding(15) as a layout placeholder, but this kernel uses textureLoad rather than
    /// sampling. The weight array is declared with var to support runtime indexing.</summary>
    const string SourceBlurWgsl = @"
struct BlurParams
{
    uDstWidth : f32,
    uDstHeight : f32,
    uDirX : f32,
    uDirY : f32,
};

@group(0) @binding(0) var<uniform> params : BlurParams;
@group(0) @binding(1) var uSrc : texture_2d<f32>;
@group(0) @binding(2) var uOutput : texture_storage_2d<rgba8unorm, write>;
@group(0) @binding(15) var uLinearClamp : sampler;

@compute @workgroup_size(8, 8, 1)
fn CSMain(@builtin(global_invocation_id) id : vec3<u32>)
{
    if (id.x < u32(params.uDstWidth) && id.y < u32(params.uDstHeight))
    {
        var w = array<f32, 5>(0.2270270270, 0.1945945946, 0.1216216216, 0.0540540541, 0.0162162162);

        let c = vec2<i32>(i32(id.x), i32(id.y));
        let dir = vec2<i32>(i32(params.uDirX), i32(params.uDirY));
        let maxC = vec2<i32>(i32(params.uDstWidth), i32(params.uDstHeight)) - vec2<i32>(1);

        let center = textureLoad(uSrc, c, 0);
        var sumAo = center.r * w[0];
        var sumW = w[0];

        for (var j : i32 = 1; j <= 4; j = j + 1)
        {
            let s1 = textureLoad(uSrc, clamp(c - dir * j, vec2<i32>(0), maxC), 0);
            let s2 = textureLoad(uSrc, clamp(c + dir * j, vec2<i32>(0), maxC), 0);
            let w1 = w[j] * exp(-abs(s1.g - center.g) * 400.0);
            let w2 = w[j] * exp(-abs(s2.g - center.g) * 400.0);
            sumAo = sumAo + s1.r * w1 + s2.r * w2;
            sumW = sumW + w1 + w2;
        }

        textureStore(uOutput, c, vec4<f32>(sumAo / sumW, center.g, 0.0, 1.0));
    }
}
";
}
