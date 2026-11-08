// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

/// <summary>
/// L2.1 occlusion pyramid: the Hi-Z downsample chain rebuilt from SceneDepth, doubled into two ping-ponged
/// layers and restricted to the main view.
///
/// <para><b>Why a copy instead of a shared source.</b> <see cref="HiZEffect"/> keeps its four shader sources in
/// private <c>const string</c>s, and L2.1's change surface is "four kernels copied, one new effect, one
/// scheduling hook" - rewriting HiZ to expose them would touch a pass that already ships and is already
/// validated. The four sources below are therefore taken character for character from HiZ.cs, down to the
/// <c>HiZParams</c> block name. That name never crosses a kernel boundary (an HLSL cbuffer, a GLSL
/// push_constant block, an MSL struct and a WGSL struct are all translation-unit scoped), so keeping it costs
/// nothing at runtime and lets <c>.dbg/occpyramid_parity.ps1</c> prove the copy with a plain string compare
/// rather than a normalization rule that could itself be wrong.</para>
///
/// <para><b>Why two layers.</b> The consumer of this pyramid is occlusion culling, which has to test a caster
/// before the occluders of the same frame have been drawn, so it can only ever read the previous frame's
/// pyramid while the current one is being rebuilt. Ping-ponging two complete layers makes "write this frame" and
/// "read last frame" two different textures: no in-place hazard, no copy-on-write. <see cref="HistoryValid"/>
/// stays false until a second frame has landed, so a consumer that reads too early shows up as "skip the
/// occlusion test this frame" rather than as a pyramid full of the clear value.</para>
///
/// <para><b>Why the main view only.</b> A minimap or a rear-view mirror has its own camera, its own depth, and
/// no consumer for this chain; running there would overwrite the main view's history with a pyramid nobody can
/// use. <see cref="Scope"/> stays <see cref="ComputeScope.View"/> because that is the only way to see
/// <see cref="FrameSchedule.ActiveTargets"/> at all, and <see cref="Record"/> returns on every view that is not
/// the primary one before it touches <see cref="Ready"/> - a secondary view must not revoke a publication the
/// main view already made this frame.</para>
///
/// <para><b>Why it is off by default.</b> L2.1 ships the producer with no consumer yet: four dispatches per
/// frame that nothing samples. <see cref="Requested"/> is read from <c>SEASON_OCCLUSION_PYRAMID</c> at startup,
/// so the acceptance script opens it explicitly and a shipped frame never pays for it.</para>
///
/// <para><b>What "texel-for-texel equal to HiZ" reduces to.</b> Same level count (bound at compile time through
/// <see cref="HiZEffect.LevelCount"/>), same identical shader text, same input (the main view's SceneDepth,
/// same D32Float gate), same level-0 rule (2x2 minimum at half resolution), same <c>max(1, (n + 1) / 2)</c>
/// rounding, same R16Float storage, same 16-byte parameter block, same 8x8x1 workgroup and same clamped-corner
/// edge handling. What remains different is only the resource names each binding points at, which is exactly the
/// layer prefix.</para>
/// </summary>
public sealed class OcclusionPyramidEffect : ComputeEffect
{
    /// <summary>Level count, bound to HiZ at compile time: the acceptance criterion is per-texel equality with that chain.</summary>
    public const int LevelCount = HiZEffect.LevelCount;

    /// <summary>Two complete pyramids, ping-ponged one per frame: write this frame, read the previous one.</summary>
    public const int LayerCount = 2;

    /// <summary>Registry prefix for the storage textures; the full name adds the layer and the level.</summary>
    public const string TextureNamePrefix = "compute://occlusionpyramid/";

    /// <summary>Storage texture name of one level of one layer; layer 0/1 and level 0..<see cref="LevelCount"/>-1.</summary>
    public static string LevelName(int layer, int level) => TextureNamePrefix + layer + "/" + level;

    /// <summary>The layer this frame's rebuild wrote; a same-frame consumer would race the partial chain and should not use it.</summary>
    public static string CurrentName(int level) => LevelName(CurrentLayer, level);

    /// <summary>The layer a same-frame consumer may read, valid when <see cref="HistoryValid"/>.</summary>
    public static string HistoryName(int level) => LevelName(HistoryLayer, level);

    /// <summary>
    /// Whether the pyramid was requested at startup (<c>SEASON_OCCLUSION_PYRAMID=1</c>). Registration is skipped
    /// when false, so an unrequested run keeps its zero dispatch count and <see cref="Ready"/> stays false.
    /// </summary>
    public static bool Requested { get; set; } = ReadRequested();

    /// <summary>This frame's rebuild completed for the main view; consumers treat false as "do not sample".</summary>
    public static bool Ready { get; private set; }

    /// <summary>Whether <see cref="HistoryLayer"/> holds a complete pyramid from an earlier frame.</summary>
    public static bool HistoryValid { get; private set; }

    /// <summary>Layer written by the most recent completed frame.</summary>
    public static int CurrentLayer { get; private set; }

    /// <summary>Layer written by the frame before <see cref="CurrentLayer"/>; the only safe read for a same-frame consumer.</summary>
    public static int HistoryLayer { get; private set; } = 1;

    static bool ReadRequested()
    {
        var raw = Environment.GetEnvironmentVariable("SEASON_OCCLUSION_PYRAMID")?.Trim();

        return raw is "1" or "on" or "true" or "yes";
    }

    // Precomputed because ComputeEffect.Record's contract is "zero allocations": the per-frame path indexes
    // these instead of concatenating a name string for every binding update.
    static readonly string[,] Names = BuildNames();

    static string[,] BuildNames()
    {
        var names = new string[LayerCount, LevelCount];

        for (int layer = 0; layer < LayerCount; layer++)
            for (int level = 0; level < LevelCount; level++)
                names[layer, level] = LevelName(layer, level);

        return names;
    }

    readonly uint[] _widths = new uint[LevelCount];
    readonly uint[] _heights = new uint[LevelCount];
    IGraphics? _graphics;
    ComputeKernel? _fromDepth;
    ComputeKernel? _reduce;
    ComputeResourceRef[]? _fromDepthResources;
    ComputeResourceRef[]? _reduceResources;
    uint _sourceWidth, _sourceHeight;
    int _writeLayer;
    bool _hasHistory;

    /// <summary>Registry name of this effect; it has no consumer by name, so it only shows up in logs.</summary>
    public override string Name => "occlusionPyramid";

    /// <summary>AfterOpaque: SceneDepth is only a clean full-frame depth after the scene opaque stage ends.</summary>
    public override ComputePhase Phase => ComputePhase.AfterOpaque;

    /// <summary>View scope so the effect sees <see cref="FrameSchedule.ActiveTargets"/> once per view and can pick the main one.</summary>
    public override ComputeScope Scope => ComputeScope.View;

    /// <summary>Level 0's width (half of full resolution); zero means "not allocated". Published statically because
    /// the effect is registered anonymously and the sidecar has no instance to ask.</summary>
    public static uint Level0Width { get; private set; }

    /// <summary>Level 0's height; see <see cref="Level0Width"/>.</summary>
    public static uint Level0Height { get; private set; }

    /// <summary>Compiles the two kernels and allocates both layers. False means no depth attachment, a narrower depth format
    /// than D32Float, or a backend that cannot compile the sources.</summary>
    public override bool Initialize(IGraphics g)
    {
        if (_graphics != null) throw new InvalidOperationException("Occlusion pyramid is already initialized.");
        if (FrameSchedule.SceneDepth == null) return false;
        var depth = FrameSchedule.SceneDepth;
        // D32Float for the same reason HiZ asks for it: the acceptance criterion compares against that chain texel
        // for texel, so both must read the same attachment at the same precision. A backend that fell back to a
        // narrower depth simply builds neither pyramid, and this effect's absence is then not a regression.
        if (depth.Desc.DepthFormat != RtFormat.D32Float || depth.Desc.Width == 0 || depth.Desc.Height == 0) return false;
        if (!Allocate(g, depth.Desc.Width, depth.Desc.Height)) return false;

        _fromDepth = g.CreateComputeKernel(new ComputeKernelDesc
        {
            Name = "occPyramidFromDepth",
            Source = new ShaderSourceSet
            {
                Hlsl = SourceFromDepthHlsl,
                Glsl = SourceFromDepthGlsl,
                Msl = SourceFromDepthMsl,
                Wgsl = SourceFromDepthWgsl,
                EntryPoint = "CSMain",
            },
            Bindings = new[]
            {
                new ComputeBindingDesc { Type = ComputeBindingType.Params, SizeInBytes = 16 },
                new ComputeBindingDesc { Type = ComputeBindingType.DepthTexture },
                new ComputeBindingDesc { Type = ComputeBindingType.StorageTextureWrite, StorageFormat = ComputeStorageFormat.R16Float },
            },
        });
        _reduce = g.CreateComputeKernel(new ComputeKernelDesc
        {
            Name = "occPyramidReduce",
            Source = new ShaderSourceSet
            {
                Hlsl = SourceReduceHlsl,
                Glsl = SourceReduceGlsl,
                Msl = SourceReduceMsl,
                Wgsl = SourceReduceWgsl,
                EntryPoint = "CSMain",
            },
            Bindings = new[]
            {
                new ComputeBindingDesc { Type = ComputeBindingType.Params, SizeInBytes = 16 },
                new ComputeBindingDesc { Type = ComputeBindingType.SampledTexture },
                new ComputeBindingDesc { Type = ComputeBindingType.StorageTextureWrite, StorageFormat = ComputeStorageFormat.R16Float },
            },
        });
        if (_fromDepth == null || _reduce == null)
        {
            Dispose();
            return false;
        }
        _graphics = g;
        _fromDepthResources = new ComputeResourceRef[] { depth, Names[0, 0] };
        _reduceResources = new ComputeResourceRef[] { Names[0, 0], Names[0, 1] };
        return true;
    }

    bool Allocate(IGraphics g, uint width, uint height)
    {
        _sourceWidth = width;
        _sourceHeight = height;
        uint w = width, h = height;
        for (int level = 0; level < LevelCount; level++)
        {
            // Round up: when the source is odd, the last row/column must still be covered by the last block,
            // otherwise the edge would retain the previous frame's depth.
            w = Math.Max(1u, (w + 1u) / 2u);
            h = Math.Max(1u, (h + 1u) / 2u);
            _widths[level] = w;
            _heights[level] = h;
            for (int layer = 0; layer < LayerCount; layer++)
                g.CreateComputeTexture(Names[layer, level], w, h, ComputeStorageFormat.R16Float);
        }
        // A rebuilt layer holds nothing from before: the next frame writes layer 0, and its "history" is a lie
        // until two frames have landed. Publishing that as HistoryValid=false is what keeps a culler from
        // testing against freshly cleared 1.0 depth and deciding everything is occluded.
        _writeLayer = 0;
        _hasHistory = false;
        Ready = false;
        HistoryValid = false;
        CurrentLayer = 0;
        HistoryLayer = 1;
        Level0Width = _widths[0];
        Level0Height = _heights[0];
        return true;
    }

    /// <summary>Rebuilds both layers against the resized depth attachment. Kernels are size-independent and survive.</summary>
    public override void OnResize(IGraphics g)
    {
        if (!ReferenceEquals(g, _graphics)) return;
        var depth = FrameSchedule.SceneDepth;
        if (depth == null || depth.Desc.Width == 0 || depth.Desc.Height == 0) return;
        Allocate(g, depth.Desc.Width, depth.Desc.Height);
        _fromDepthResources![0] = depth;
    }

    /// <summary>Records the four dispatches for the main view only. Every other view returns before touching any publication.</summary>
    public override void Record(IGraphics g)
    {
        // Main view only, and this is the very first test: by the time a minimap or mirror runs, the main view has
        // already published this frame's pyramid, and clearing Ready here would revoke a valid publication.
        if (!ReferenceEquals(FrameSchedule.ActiveTargets, FrameSchedule.PrimaryTargets)) return;

        Ready = false;
        if (!ReferenceEquals(g, _graphics) || _fromDepth == null || _reduce == null) return;
        var depth = FrameSchedule.SceneDepth;
        if (depth == null || depth.Desc.Width != _sourceWidth || depth.Desc.Height != _sourceHeight) return;

        int write = _writeLayer;

        Span<float> p = stackalloc float[4];
        p[0] = _sourceWidth;
        p[1] = _sourceHeight;
        p[2] = _widths[0];
        p[3] = _heights[0];
        _fromDepthResources![1] = Names[write, 0];
        if (!g.TryDispatchCompute(new ComputeDispatchArgs
        {
            Kernel = _fromDepth,
            Params = MemoryMarshal.AsBytes(p),
            Resources = _fromDepthResources,
            GroupsX = (_widths[0] + 7) / 8,
            GroupsY = (_heights[0] + 7) / 8,
            GroupsZ = 1,
        })) return;

        for (int level = 1; level < LevelCount; level++)
        {
            _reduceResources![0] = Names[write, level - 1];
            _reduceResources[1] = Names[write, level];
            p[0] = _widths[level - 1];
            p[1] = _heights[level - 1];
            p[2] = _widths[level];
            p[3] = _heights[level];
            if (!g.TryDispatchCompute(new ComputeDispatchArgs
            {
                Kernel = _reduce,
                Params = MemoryMarshal.AsBytes(p),
                Resources = _reduceResources,
                GroupsX = (_widths[level] + 7) / 8,
                GroupsY = (_heights[level] + 7) / 8,
                GroupsZ = 1,
            }))
            {
                // A half-written layer is not a history anybody may read, so _writeLayer is not advanced: the next
                // frame overwrites the same layer from the top, and the previously published pair stays intact.
                return;
            }
        }

        // Publish only after the whole chain landed.
        CurrentLayer = write;
        HistoryLayer = 1 - write;
        HistoryValid = _hasHistory;
        _hasHistory = true;
        _writeLayer = 1 - write;
        Ready = true;
    }

    /// <summary>Unregistration is a full teardown: the kernels and the published names go away with the effect.</summary>
    public override void OnUnregistered() => Dispose();

    void Reset()
    {
        Ready = false;
        HistoryValid = false;
        CurrentLayer = 0;
        HistoryLayer = 1;
        Level0Width = 0;
        Level0Height = 0;
        _graphics = null;
    }

    /// <summary>Releases the kernels and revokes every published name and flag, so a re-registered effect starts without history.</summary>
    public void Dispose()
    {
        _fromDepth?.Dispose();
        _fromDepth = null;
        _reduce?.Dispose();
        _reduce = null;
        _fromDepthResources = null;
        _reduceResources = null;
        Array.Clear(_widths);
        Array.Clear(_heights);
        _sourceWidth = _sourceHeight = 0;
        _writeLayer = 0;
        _hasHistory = false;
        Reset();
    }

    // Shader sources: taken character for character from HiZ.cs (see the class summary). The slot convention is
    // the binding declaration order above; the workgroup is fixed at 8x8x1.

    /// <summary>D3D12 cs_5_0 (fxc; single exit avoids X4000). Level 0 reads the depth attachment directly and takes the minimum of the four corners.</summary>
    const string SourceFromDepthHlsl = @"
cbuffer HiZParams : register(b0)
{
    float uSrcWidth;
    float uSrcHeight;
    float uDstWidth;
    float uDstHeight;
};

Texture2D<float> uDepth : register(t0);
RWTexture2D<float4> uOutput : register(u0);

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x < (uint)uDstWidth && id.y < (uint)uDstHeight)
    {
        // When the source has odd dimensions the bottom-right corner goes out of bounds; clamp to the
        // last row/column: sampling the edge repeatedly will not raise the minimum, whereas dropping it
        // would make the pyramid's edge farther than the true depth and the ray would slip through the gap.
        int2 first = int2(id.xy) * 2;
        int2 last = int2((uint)uSrcWidth - 1u, (uint)uSrcHeight - 1u);
        float d0 = uDepth.Load(int3(min(first, last), 0));
        float d1 = uDepth.Load(int3(min(first + int2(1, 0), last), 0));
        float d2 = uDepth.Load(int3(min(first + int2(0, 1), last), 0));
        float d3 = uDepth.Load(int3(min(first + int2(1, 1), last), 0));
        uOutput[id.xy] = float4(min(min(d0, d1), min(d2, d3)), 0.0, 0.0, 0.0);
    }
}
";

    /// <summary>Vulkan GLSL 450 (glslang -> SPIR-V; Params use push_constant, binding follows declaration order).</summary>
    const string SourceFromDepthGlsl = @"#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(push_constant) uniform HiZParams
{
    float uSrcWidth;
    float uSrcHeight;
    float uDstWidth;
    float uDstHeight;
};

layout(binding = 1) uniform sampler2D uDepth;
layout(binding = 2, r16f) uniform writeonly image2D uOutput;

void main()
{
    uvec2 id = gl_GlobalInvocationID.xy;
    if (id.x < uint(uDstWidth) && id.y < uint(uDstHeight))
    {
        ivec2 first = ivec2(id) * 2;
        ivec2 last = ivec2(int(uint(uSrcWidth)) - 1, int(uint(uSrcHeight)) - 1);
        float d0 = texelFetch(uDepth, min(first, last), 0).r;
        float d1 = texelFetch(uDepth, min(first + ivec2(1, 0), last), 0).r;
        float d2 = texelFetch(uDepth, min(first + ivec2(0, 1), last), 0).r;
        float d3 = texelFetch(uDepth, min(first + ivec2(1, 1), last), 0).r;
        imageStore(uOutput, ivec2(id), vec4(min(min(d0, d1), min(d2, d3)), 0.0, 0.0, 0.0));
    }
}
";

    /// <summary>Metal MSL kernel.</summary>
    const string SourceFromDepthMsl = @"
#include <metal_stdlib>
using namespace metal;

struct HiZParams
{
    float uSrcWidth;
    float uSrcHeight;
    float uDstWidth;
    float uDstHeight;
};

kernel void CSMain(
    constant HiZParams& params [[buffer(0)]],
    depth2d<float, access::read> uDepth [[texture(0)]],
    texture2d<float, access::write> uOutput [[texture(1)]],
    uint2 id [[thread_position_in_grid]])
{
    if (id.x < uint(params.uDstWidth) && id.y < uint(params.uDstHeight))
    {
        int2 first = int2(id) * 2;
        int2 last = int2(int(uint(params.uSrcWidth)) - 1, int(uint(params.uSrcHeight)) - 1);
        float d0 = uDepth.read(uint2(min(first, last)));
        float d1 = uDepth.read(uint2(min(first + int2(1, 0), last)));
        float d2 = uDepth.read(uint2(min(first + int2(0, 1), last)));
        float d3 = uDepth.read(uint2(min(first + int2(1, 1), last)));
        uOutput.write(float4(min(min(d0, d1), min(d2, d3)), 0.0, 0.0, 0.0), id);
    }
}
";

    /// <summary>WebGPU WGSL. The storage format is pinned to rgba16float by the engine's narrow-format fallback chain, and reads take only .x.</summary>
    const string SourceFromDepthWgsl = @"
struct HiZParams
{
    uSrcWidth : f32,
    uSrcHeight : f32,
    uDstWidth : f32,
    uDstHeight : f32,
};

@group(0) @binding(0) var<uniform> params : HiZParams;
@group(0) @binding(1) var uDepth : texture_depth_2d;
@group(0) @binding(2) var uOutput : texture_storage_2d<rgba16float, write>;

@compute @workgroup_size(8, 8, 1)
fn CSMain(@builtin(global_invocation_id) id : vec3<u32>)
{
    if (id.x < u32(params.uDstWidth) && id.y < u32(params.uDstHeight))
    {
        let first = vec2<i32>(i32(id.x), i32(id.y)) * 2;
        let last = vec2<i32>(i32(u32(params.uSrcWidth)) - 1, i32(u32(params.uSrcHeight)) - 1);
        let d0 = textureLoad(uDepth, min(first, last), 0);
        let d1 = textureLoad(uDepth, min(first + vec2<i32>(1, 0), last), 0);
        let d2 = textureLoad(uDepth, min(first + vec2<i32>(0, 1), last), 0);
        let d3 = textureLoad(uDepth, min(first + vec2<i32>(1, 1), last), 0);
        textureStore(uOutput, vec2<i32>(i32(id.x), i32(id.y)), vec4<f32>(min(min(d0, d1), min(d2, d3)), 0.0, 0.0, 0.0));
    }
}
";

    /// <summary>D3D12 cs_5_0 (fxc; single exit avoids X4000). Takes the minimum over the previous level's 2x2.</summary>
    const string SourceReduceHlsl = @"
cbuffer HiZParams : register(b0)
{
    float uSrcWidth;
    float uSrcHeight;
    float uDstWidth;
    float uDstHeight;
};

Texture2D<float> uSource : register(t0);
SamplerState uLinearClamp : register(s0);
RWTexture2D<float4> uOutput : register(u0);

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x < (uint)uDstWidth && id.y < (uint)uDstHeight)
    {
        // The sample points land on the centers of the source's two rows and two columns of texels, where
        // linear filtering equals point sampling.
        float2 size = float2(uSrcWidth, uSrcHeight);
        float2 uv = (float2(id.xy) * 2.0 + 0.5) / size;
        float2 step = 1.0 / size;
        float d0 = uSource.SampleLevel(uLinearClamp, uv, 0.0);
        float d1 = uSource.SampleLevel(uLinearClamp, uv + float2(step.x, 0.0), 0.0);
        float d2 = uSource.SampleLevel(uLinearClamp, uv + float2(0.0, step.y), 0.0);
        float d3 = uSource.SampleLevel(uLinearClamp, uv + step, 0.0);
        uOutput[id.xy] = float4(min(min(d0, d1), min(d2, d3)), 0.0, 0.0, 0.0);
    }
}
";

    /// <summary>Vulkan GLSL 450.</summary>
    const string SourceReduceGlsl = @"#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(push_constant) uniform HiZParams
{
    float uSrcWidth;
    float uSrcHeight;
    float uDstWidth;
    float uDstHeight;
};

layout(binding = 1) uniform sampler2D uSource;
layout(binding = 2, r16f) uniform writeonly image2D uOutput;

void main()
{
    uvec2 id = gl_GlobalInvocationID.xy;
    if (id.x < uint(uDstWidth) && id.y < uint(uDstHeight))
    {
        vec2 size = vec2(uSrcWidth, uSrcHeight);
        vec2 uv = (vec2(id) * 2.0 + 0.5) / size;
        vec2 step = 1.0 / size;
        float d0 = textureLod(uSource, uv, 0.0).r;
        float d1 = textureLod(uSource, uv + vec2(step.x, 0.0), 0.0).r;
        float d2 = textureLod(uSource, uv + vec2(0.0, step.y), 0.0).r;
        float d3 = textureLod(uSource, uv + step, 0.0).r;
        imageStore(uOutput, ivec2(id), vec4(min(min(d0, d1), min(d2, d3)), 0.0, 0.0, 0.0));
    }
}
";

    /// <summary>Metal MSL kernel.</summary>
    const string SourceReduceMsl = @"
#include <metal_stdlib>
using namespace metal;

struct HiZParams
{
    float uSrcWidth;
    float uSrcHeight;
    float uDstWidth;
    float uDstHeight;
};

kernel void CSMain(
    constant HiZParams& params [[buffer(0)]],
    texture2d<float, access::sample> uSource [[texture(0)]],
    texture2d<float, access::write> uOutput [[texture(1)]],
    sampler uLinearClamp [[sampler(0)]],
    uint2 id [[thread_position_in_grid]])
{
    if (id.x < uint(params.uDstWidth) && id.y < uint(params.uDstHeight))
    {
        float2 size = float2(params.uSrcWidth, params.uSrcHeight);
        float2 uv = (float2(id) * 2.0 + 0.5) / size;
        float2 step = 1.0 / size;
        float d0 = uSource.sample(uLinearClamp, uv, level(0.0)).r;
        float d1 = uSource.sample(uLinearClamp, uv + float2(step.x, 0.0), level(0.0)).r;
        float d2 = uSource.sample(uLinearClamp, uv + float2(0.0, step.y), level(0.0)).r;
        float d3 = uSource.sample(uLinearClamp, uv + step, level(0.0)).r;
        uOutput.write(float4(min(min(d0, d1), min(d2, d3)), 0.0, 0.0, 0.0), id);
    }
}
";

    /// <summary>WebGPU WGSL.</summary>
    const string SourceReduceWgsl = @"
struct HiZParams
{
    uSrcWidth : f32,
    uSrcHeight : f32,
    uDstWidth : f32,
    uDstHeight : f32,
};

@group(0) @binding(0) var<uniform> params : HiZParams;
@group(0) @binding(1) var uSource : texture_2d<f32>;
@group(0) @binding(2) var uOutput : texture_storage_2d<rgba16float, write>;
@group(0) @binding(15) var uLinearClamp : sampler;

@compute @workgroup_size(8, 8, 1)
fn CSMain(@builtin(global_invocation_id) id : vec3<u32>)
{
    if (id.x < u32(params.uDstWidth) && id.y < u32(params.uDstHeight))
    {
        let size = vec2<f32>(params.uSrcWidth, params.uSrcHeight);
        let uv = (vec2<f32>(f32(id.x), f32(id.y)) * 2.0 + vec2<f32>(0.5)) / size;
        let texel = vec2<f32>(1.0) / size;
        let d0 = textureSampleLevel(uSource, uLinearClamp, uv, 0.0).r;
        let d1 = textureSampleLevel(uSource, uLinearClamp, uv + vec2<f32>(texel.x, 0.0), 0.0).r;
        let d2 = textureSampleLevel(uSource, uLinearClamp, uv + vec2<f32>(0.0, texel.y), 0.0).r;
        let d3 = textureSampleLevel(uSource, uLinearClamp, uv + texel, 0.0).r;
        textureStore(uOutput, vec2<i32>(i32(id.x), i32(id.y)), vec4<f32>(min(min(d0, d1), min(d2, d3)), 0.0, 0.0, 0.0));
    }
}
";
}
