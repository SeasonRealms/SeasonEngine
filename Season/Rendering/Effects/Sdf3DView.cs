// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

/// <summary>
/// Engine built-in compute effect: 3D SDF volume slice visualization
/// (the sole acceptance carrier for the 1-8 Compute 3D resource expansion).
/// This effect contains no GI algorithm. Its only purpose is to exercise all four items
/// in 1-8 in one pass and turn them into a visually inspectable output:
/// 1. 3D resources: StorageTexture3DWrite writes a 64^3 volume, and SampledTexture3D
///    reads it back with trilinear clamp sampling.
/// 2. Format whitelist: the volume texture uses R16Float
///    (single-channel half precision, the target format for the Global SDF).
/// 3. WorkgroupSize: the three kernels use 4x4x4 / 64x1x1 / default 8x8x1,
///    covering both voxel merge and probe-ray style workloads.
/// 4. Constant-block path: a 256B color ramp is uploaded through
///    IGraphics.UpdateStorageBuffer (>128B, proving the escape path beyond Params).
///    Since 2-4 Step 0, this upload happens every frame to stress-test that call path
///    as well (see the note above RampSweepHz).
///
/// Three-dispatch chain (all in the FrameStart phase, with in-frame dependencies resolved
/// internally by the platform DispatchCompute implementation):
/// 1. fill3d  4x4x4, groups 16,16,16 -> writes an analytic SDF into the 64^3 R16Float
///    volume (union of two spheres + low-frequency perturbation, pulsing over time).
/// 2. probe1d 64x1x1, groups 64,1,1 -> 64 columns, each with 64 threads sampling the
///    volume evenly along the Z axis, then reducing in groupshared memory to find the
///    first hit depth. The result lands in a 64x4B storage buffer, serving as the
///    smallest rehearsal of the DDGI probe-ray pattern
///    ("one ray per group, reduced within the group").
/// 3. slice2d default 8x8x1, groups 32,32,1 -> outputs a 256x256 rgba8unorm texture:
///    the top 240 rows form the main region, showing a 4x magnified trilinear slice
///    taken at a time-swept non-integer W coordinate;
///    the bottom 16 rows show 64 bars from the kernel2 reduction result
///    (3 px per bar + 1 px spacing for easy counting);
///    both areas fetch their colors from the 256B ramp constant block.
///
/// Binding layout (declaration order defines the cross-backend slot convention;
/// see ComputeBindingType summary; all Params are 16B):
/// fill3d  [0] Params(time, volumeSize, _, _) [1] StorageTexture3DWrite(R16Float)
/// probe1d [0] Params(time, volumeSize, _, _) [1] SampledTexture3D [2] StorageBufferReadWrite(probes 256B)
/// slice2d [0] Params(time, size, mainRows, _) [1] SampledTexture3D [2] StorageBufferRead(ramp 256B)
///         [3] StorageBufferRead(probes 256B) [4] StorageTextureWrite(Rgba8Unorm)
/// Note: slice2d needs to read both the color-ramp constant block and the probe reduction
/// result, so it uses one extra StorageBufferRead compared with the single-buffer form.
/// Those two channels have different semantics: the former is static CPU-uploaded
/// constant data, while the latter is an intermediate product from the same-frame kernel
/// chain. Merging them into one buffer would only make the offset convention harder to read,
/// so they are intentionally kept in separate slots.
///
/// The output texture is registered into the platform 2D texture dictionary under
/// TextureName and consumed by Sprite2D without changes. The volume texture goes into the
/// dedicated 3D texture dictionary using the compute3d:// prefix and cannot be consumed by
/// Sprite2D directly. Kernel3 is the visualization bridge for it (see IGraphics clause 1-8).
///
/// Storage format literals differ across the four backends, and the effect author is
/// responsible for that difference (see ComputeStorageFormat summary). HLSL typed UAVs and
/// MSL access::write textures need no explicit format literal; GLSL declares r16f
/// (mapped to VK R16_SFLOAT); WGSL declares rgba16float. WebGPU core does not support
/// r16float as STORAGE_BINDING because that belongs to the optional texture-formats-tier1
/// feature set, while rgba16float is the only core half-precision format that supports
/// both compute writes and trilinear filtering. Therefore the web backend uses rgba16float
/// uniformly and only reads/writes the .x channel, with identical numerical semantics to
/// the other three backends.
///
/// Visual acceptance criteria: the 4x magnified slice should look smooth without blocky
/// texels (trilinear filtering is working); W should sweep continuously over time and clamp
/// at the end faces instead of wrapping when it moves outside [0,1] (clamp is working);
/// the bottom should show exactly 64 bars pulsing with the SDF
/// (64x1x1 + read/write buffer path); and the coloring should be a cyan-to-magenta ramp
/// whose stop positions drift slowly (>128B constant-block path + per-frame upload is working).
/// If uploads fail silently, the coloring will freeze instead of sweeping.
/// </summary>
public sealed class Sdf3DViewEffect : ComputeEffect
{
    /// <summary>Registered output texture name for the slice view (2D dictionary; used directly by Sprite2D.Name).</summary>
    public const string TextureName = "compute://sdf3dview";

    /// <summary>Registered 3D volume texture name (dedicated 3D dictionary; compute3d:// is the 1-8 naming convention).</summary>
    public const string VolumeName = "compute3d://sdf3dview/sdf";

    /// <summary>Slice output size.</summary>
    public const uint Size = 256;

    /// <summary>Volume edge length (64^3 R16Float = 512 KB; the contract recommends each cascade to stay <= 128^3).</summary>
    public const uint VolumeSize = 64;

    /// <summary>Number of rows in the main slice region; the remaining Size - MainRows rows form the bar-strip area.</summary>
    public const uint MainRows = 240;

    /// <summary>Probe column count, equal to both the number of workgroups in kernel2 and the thread count inside each workgroup (64 x 4B = 256B).</summary>
    public const uint ProbeCount = 64;

    /// <summary>Number of color-ramp stops (each stop is a float4 = 16B, total 256B, which exceeds the 128B Params limit).</summary>
    const int RampStops = 16;

    const uint RampBytes = RampStops * 16u;

    ComputeKernel? _fill3d;
    ComputeKernel? _probe1d;
    ComputeKernel? _slice2d;

    StorageBuffer? _probes;
    StorageBuffer? _ramp;

    // 2-4 Step 0 stress validation: the ramp is now uploaded every frame instead of only once
    // on the first frame.
    // This verifies the per-frame UpdateStorageBuffer path on all four backends:
    // no crashes, zero per-frame allocations (the staging/upload heaps on the three native
    // backends are now persistent N-buffered rings), and no leaks. Elimination of frame-flight
    // races is guaranteed by construction rather than something this view can prove visually:
    // the slot is indexed by Device.FrameIndex, following the engine's instance/bone/light
    // constant-buffer convention, and the ring fence guarantees that any previous GPU work
    // for that slot has completed before reuse.
    // Uploads must still happen outside a pass. Record runs in the FrameStart phase before
    // the first render pass, which satisfies the UpdateStorageBuffer constraint of
    // "frame-loop thread, outside any pass". Initialize does not satisfy that constraint
    // because it runs before the frame loop, when command lists are not yet open.

    /// <summary>Sweep frequency of the ramp stops, in cycles per second.</summary>
    const float RampSweepHz = 0.15f;

    readonly Stopwatch _clock = Stopwatch.StartNew();

    // These cached arrays cannot use stackalloc because they contain string references
    // (see ComputeDispatchArgs summary). Reuse them every frame with zero allocations.
    ComputeResourceRef[]? _fillRes;
    ComputeResourceRef[]? _probeRes;
    ComputeResourceRef[]? _sliceRes;

    public override string Name => "sdf3dView";

    public override ComputePhase Phase => ComputePhase.FrameStart;

    public override bool Initialize(IGraphics g)
    {
        // Continue only if all three kernels compile successfully.
        // If any of them fails, dispose all created handles so registration leaves no residue.
        _fill3d = g.CreateComputeKernel(new ComputeKernelDesc
        {
            Name = "sdf3dFill",
            WorkgroupX = 4,
            WorkgroupY = 4,
            WorkgroupZ = 4,
            Source = new ShaderSourceSet
            {
                Hlsl = SourceFillHlsl,
                Glsl = SourceFillGlsl,
                Msl = SourceFillMsl,
                Wgsl = SourceFillWgsl,
                EntryPoint = "CSMain",
            },
            Bindings = new[]
            {
                new ComputeBindingDesc { Type = ComputeBindingType.Params, SizeInBytes = 16 },
                new ComputeBindingDesc { Type = ComputeBindingType.StorageTexture3DWrite, StorageFormat = ComputeStorageFormat.R16Float },
            },
        });
        _probe1d = g.CreateComputeKernel(new ComputeKernelDesc
        {
            Name = "sdf3dProbe",
            WorkgroupX = ProbeCount,
            WorkgroupY = 1,
            WorkgroupZ = 1,
            Source = new ShaderSourceSet
            {
                Hlsl = SourceProbeHlsl,
                Glsl = SourceProbeGlsl,
                Msl = SourceProbeMsl,
                Wgsl = SourceProbeWgsl,
                EntryPoint = "CSMain",
            },
            Bindings = new[]
            {
                new ComputeBindingDesc { Type = ComputeBindingType.Params, SizeInBytes = 16 },
                new ComputeBindingDesc { Type = ComputeBindingType.SampledTexture3D },
                new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferReadWrite },
            },
        });
        _slice2d = g.CreateComputeKernel(new ComputeKernelDesc
        {
            // Keep the default 8x8x1 workgroup size here as an additional validation that
            // the 1-8 defaults stay compatible with existing effects.
            Name = "sdf3dSlice",
            Source = new ShaderSourceSet
            {
                Hlsl = SourceSliceHlsl,
                Glsl = SourceSliceGlsl,
                Msl = SourceSliceMsl,
                Wgsl = SourceSliceWgsl,
                EntryPoint = "CSMain",
            },
            Bindings = new[]
            {
                new ComputeBindingDesc { Type = ComputeBindingType.Params, SizeInBytes = 16 },
                new ComputeBindingDesc { Type = ComputeBindingType.SampledTexture3D },
                new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferRead },
                new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferRead },
                new ComputeBindingDesc { Type = ComputeBindingType.StorageTextureWrite, StorageFormat = ComputeStorageFormat.Rgba8Unorm },
            },
        });
        if (_fill3d == null || _probe1d == null || _slice2d == null)
        {
            Dispose();
            return false;
        }

        g.CreateComputeTexture3D(VolumeName, VolumeSize, VolumeSize, VolumeSize, ComputeStorageFormat.R16Float);
        g.CreateComputeTexture(TextureName, Size, Size, ComputeStorageFormat.Rgba8Unorm);

        _probes = g.CreateStorageBuffer(ProbeCount * 4);
        _ramp = g.CreateStorageBuffer(RampBytes);

        _fillRes = new ComputeResourceRef[] { VolumeName };
        _probeRes = new ComputeResourceRef[] { VolumeName, _probes };
        _sliceRes = new ComputeResourceRef[] { VolumeName, _ramp, _probes, TextureName };
        return true;
    }

    public override void Record(IGraphics g)
    {
        float t = (float)_clock.Elapsed.TotalSeconds;

        // Ramp constant block: upload every frame (see RampSweepHz summary). This still runs outside any pass.
        Span<float> ramp = stackalloc float[RampStops * 4];
        BuildRamp(ramp, t * RampSweepHz);
        g.UpdateStorageBuffer(_ramp!, MemoryMarshal.AsBytes(ramp));

        Span<float> p = stackalloc float[4];

        // 1) fill3d: analytic SDF -> 64^3 volume
        // (4x4x4 workgroup, evenly divisible -> 16 groups per axis).
        p[0] = t;
        p[1] = VolumeSize;
        p[2] = 0f;
        p[3] = 0f;
        g.DispatchCompute(new ComputeDispatchArgs
        {
            Kernel = _fill3d!,
            Params = MemoryMarshal.AsBytes(p),
            Resources = _fillRes,
            GroupsX = VolumeSize / 4,
            GroupsY = VolumeSize / 4,
            GroupsZ = VolumeSize / 4,
        });

        // 2) probe1d: 64 groups x 64 threads ray-march along the Z axis and reduce within the group
        // (Params use the same layout as kernel1).
        g.DispatchCompute(new ComputeDispatchArgs
        {
            Kernel = _probe1d!,
            Params = MemoryMarshal.AsBytes(p),
            Resources = _probeRes,
            GroupsX = ProbeCount,
            GroupsY = 1,
            GroupsZ = 1,
        });

        // 3) slice2d: trilinear slice + bar chart -> 256x256 output.
        // W is swept by time inside the shader, so only time needs to be uploaded here.
        p[1] = Size;
        p[2] = MainRows;
        g.DispatchCompute(new ComputeDispatchArgs
        {
            Kernel = _slice2d!,
            Params = MemoryMarshal.AsBytes(p),
            Resources = _sliceRes,
            GroupsX = (Size + 7) / 8,
            GroupsY = (Size + 7) / 8,
            GroupsZ = 1,
        });
    }

    /// <summary>
    /// Cyan-to-magenta color ramp (16 stops x float4). The palette is intentionally
    /// non-default and easy to recognize: if the view shows default grayscale or rainbow
    /// coloring instead, UpdateStorageBuffer or StorageBufferRead did not really work.
    /// 2-4 Step 0: phase slowly sweeps the stop positions along the ramp so that per-frame
    /// uploads are visibly verifiable. If uploads fail silently, the coloring freezes
    /// instead of sweeping.
    /// </summary>
    static void BuildRamp(Span<float> dst, float phase)
    {
        for (int i = 0; i < RampStops; i++)
        {
            float k = (i / (float)(RampStops - 1) + phase) % 1f;
            dst[i * 4 + 0] = k * k;                    // R: rises late, ending in magenta
            dst[i * 4 + 1] = 0.05f + 0.85f * (1f - k); // G: brightest at the start, giving a cyan lead
            dst[i * 4 + 2] = 0.55f + 0.45f * k;        // B: stays relatively high, keeping both ends in a cool range
            dst[i * 4 + 3] = 1f;
        }
    }

    public void Dispose()
    {
        _fill3d?.Dispose();
        _fill3d = null;
        _probe1d?.Dispose();
        _probe1d = null;
        _slice2d?.Dispose();
        _slice2d = null;
        _probes?.Dispose();
        _probes = null;
        _ramp?.Dispose();
        _ramp = null;
    }

    // Shader sources for all four backends (single source of truth; slots follow the
    // class-level binding convention; all kernels use a single exit to avoid fxc X4000).
    //
    // The SDF formula is a cross-backend contract constant and must be ported literally
    // when aligning implementations (numerically identical):
    //   p in [-1,1]^3; r1 = 0.42 + 0.08*sin(t)
    //   d1 = |p - (-0.28,0,0)| - r1; d2 = |p - (0.30, 0.18*sin(0.7t), 0)| - 0.26
    //   d  = min(d1, d2) + 0.04*sin(2PI*p.x + t)*sin(2PI*p.y)*sin(2PI*p.z)
    // Slice shading looks up the ramp with s = 0.5 - 1.4*d;
    // W = 0.5 + 0.75*sin(0.4t), which is always non-integer and periodically sweeps past both ends of [0,1].

    // D3D12 HLSL cs_5_0 (fxc)

    /// <summary>kernel1 fill3d: writes the 64^3 volume with a 4x4x4 workgroup (typed UAV; R16_FLOAT uses only the .x channel).</summary>
    const string SourceFillHlsl = @"
cbuffer Sdf3DParams : register(b0)
{
    float uTime;
    float uVolumeSize;
    float uPad0;
    float uPad1;
};

RWTexture3D<float4> uVolume : register(u0);

[numthreads(4, 4, 4)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x < (uint)uVolumeSize && id.y < (uint)uVolumeSize && id.z < (uint)uVolumeSize)
    {
        float3 p = (float3(id) + 0.5) / uVolumeSize * 2.0 - 1.0;
        float t = uTime;
        float r1 = 0.42 + 0.08 * sin(t);
        float d1 = length(p - float3(-0.28, 0.0, 0.0)) - r1;
        float d2 = length(p - float3(0.30, 0.18 * sin(t * 0.7), 0.0)) - 0.26;
        float d = min(d1, d2)
                + 0.04 * sin(p.x * 6.2832 + t) * sin(p.y * 6.2832) * sin(p.z * 6.2832);
        uVolume[id] = float4(d, 0.0, 0.0, 0.0);
    }
}
";

    /// <summary>kernel2 probe1d: a 64x1x1 workgroup. Each group handles one column, with 64 threads
    /// sampling one Z position each, then a groupshared tree reduction finds the first hit
    /// (d <= 0) as normalized depth. Misses are stored as 1.0.</summary>
    const string SourceProbeHlsl = @"
cbuffer Sdf3DParams : register(b0)
{
    float uTime;
    float uVolumeSize;
    float uPad0;
    float uPad1;
};

Texture3D<float4> uVolume : register(t0);
SamplerState uSampler : register(s0);
RWByteAddressBuffer uProbes : register(u0);

groupshared float gHit[64];

[numthreads(64, 1, 1)]
void CSMain(uint3 gid : SV_GroupID, uint li : SV_GroupIndex)
{
    float u = (float(gid.x) + 0.5) / uVolumeSize;
    float w = (float(li) + 0.5) / 64.0;
    float d = uVolume.SampleLevel(uSampler, float3(u, 0.5, w), 0).x;
    gHit[li] = d <= 0.0 ? w : 1.0;
    GroupMemoryBarrierWithGroupSync();

    for (uint s = 32; s > 0; s >>= 1)
    {
        if (li < s)
            gHit[li] = min(gHit[li], gHit[li + s]);
        GroupMemoryBarrierWithGroupSync();
    }

    if (li == 0)
        uProbes.Store((int)gid.x * 4, asuint(gHit[0]));
}
";

    /// <summary>kernel3 slice2d: default 8x8x1 workgroup, drawing the trilinear slice in the main area and the bar chart at the bottom, both colored from the ramp constant block.</summary>
    const string SourceSliceHlsl = @"
cbuffer Sdf3DParams : register(b0)
{
    float uTime;
    float uSize;
    float uMainRows;
    float uPad0;
};

Texture3D<float4> uVolume : register(t0);
ByteAddressBuffer uRamp : register(t1);
ByteAddressBuffer uProbes : register(t2);
SamplerState uSampler : register(s0);
RWTexture2D<float4> uOutput : register(u0);

float3 RampLookup(float s)
{
    float f = saturate(s) * float(15);
    int i0 = (int)floor(f);
    int i1 = min(i0 + 1, 15);
    float3 c0 = asfloat(uRamp.Load4(i0 * 16)).rgb;
    float3 c1 = asfloat(uRamp.Load4(i1 * 16)).rgb;
    return lerp(c0, c1, f - float(i0));
}

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x < (uint)uSize && id.y < (uint)uSize)
    {
        float3 rgb = float3(0.03, 0.03, 0.05);
        if (id.y < (uint)uMainRows)
        {
            float w = 0.5 + 0.75 * sin(uTime * 0.4);
            float2 uv = (float2(id.xy) + 0.5) / float2(uSize, uMainRows);
            float d = uVolume.SampleLevel(uSampler, float3(uv, w), 0).x;
            rgb = RampLookup(0.5 - d * 1.4);
        }
        else
        {
            int col = (int)(id.x >> 2);
            int sub = (int)(id.x & 3);
            float bar = 1.0 - saturate(asfloat(uProbes.Load(col * 4)));
            float f = (uSize - 0.5 - float(id.y)) / (uSize - uMainRows);
            if (sub != 3 && f <= bar)
                rgb = RampLookup(1.0);
        }
        uOutput[id.xy] = float4(rgb, 1.0);
    }
}
";

    // Vulkan GLSL 450 (glslang -> SPIR-V; entry point is always main; binding = declaration slot i)

    /// <summary>kernel1 fill3d. The r16f format qualifier must match the Vulkan backing format
    /// (R16_SFLOAT). If a device does not support that format as STORAGE_IMAGE, the platform
    /// layer will log and fall back, and this source must then be updated to match.</summary>
    const string SourceFillGlsl = @"#version 450
layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;

layout(push_constant) uniform Sdf3DParams
{
    float uTime;
    float uVolumeSize;
    float uPad0;
    float uPad1;
};

layout(binding = 1, r16f) uniform writeonly image3D uVolume;

void main()
{
    uvec3 id = gl_GlobalInvocationID;
    if (id.x < uint(uVolumeSize) && id.y < uint(uVolumeSize) && id.z < uint(uVolumeSize))
    {
        vec3 p = (vec3(id) + 0.5) / uVolumeSize * 2.0 - 1.0;
        float t = uTime;
        float r1 = 0.42 + 0.08 * sin(t);
        float d1 = length(p - vec3(-0.28, 0.0, 0.0)) - r1;
        float d2 = length(p - vec3(0.30, 0.18 * sin(t * 0.7), 0.0)) - 0.26;
        float d = min(d1, d2)
                + 0.04 * sin(p.x * 6.2832 + t) * sin(p.y * 6.2832) * sin(p.z * 6.2832);
        imageStore(uVolume, ivec3(id), vec4(d, 0.0, 0.0, 0.0));
    }
}
";

    /// <summary>kernel2 probe1d (shared + barrier tree reduction; the raw storage buffer is addressed
    /// through a uint-array view, which is byte-for-byte equivalent to D3D12 RWByteAddressBuffer
    /// and WGSL array&lt;u32&gt;).</summary>
    const string SourceProbeGlsl = @"#version 450
layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;

layout(push_constant) uniform Sdf3DParams
{
    float uTime;
    float uVolumeSize;
    float uPad0;
    float uPad1;
};

layout(binding = 1) uniform sampler3D uVolume;
layout(binding = 2, std430) buffer Probes { uint data[]; } uProbes;

shared float gHit[64];

void main()
{
    uint li = gl_LocalInvocationID.x;
    float u = (float(gl_WorkGroupID.x) + 0.5) / uVolumeSize;
    float w = (float(li) + 0.5) / 64.0;
    float d = textureLod(uVolume, vec3(u, 0.5, w), 0.0).x;
    gHit[li] = d <= 0.0 ? w : 1.0;
    memoryBarrierShared();
    barrier();

    for (uint s = 32u; s > 0u; s >>= 1)
    {
        if (li < s)
            gHit[li] = min(gHit[li], gHit[li + s]);
        memoryBarrierShared();
        barrier();
    }

    if (li == 0u)
        uProbes.data[gl_WorkGroupID.x] = floatBitsToUint(gHit[0]);
}
";

    /// <summary>kernel3 slice2d.</summary>
    const string SourceSliceGlsl = @"#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(push_constant) uniform Sdf3DParams
{
    float uTime;
    float uSize;
    float uMainRows;
    float uPad0;
};

layout(binding = 1) uniform sampler3D uVolume;
layout(binding = 2, std430) readonly buffer Ramp { float data[]; } uRamp;
layout(binding = 3, std430) readonly buffer Probes { uint data[]; } uProbes;
layout(binding = 4, rgba8) uniform writeonly image2D uOutput;

vec3 RampLookup(float s)
{
    float f = clamp(s, 0.0, 1.0) * 15.0;
    int i0 = int(floor(f));
    int i1 = min(i0 + 1, 15);
    vec3 c0 = vec3(uRamp.data[i0 * 4], uRamp.data[i0 * 4 + 1], uRamp.data[i0 * 4 + 2]);
    vec3 c1 = vec3(uRamp.data[i1 * 4], uRamp.data[i1 * 4 + 1], uRamp.data[i1 * 4 + 2]);
    return mix(c0, c1, f - float(i0));
}

void main()
{
    uvec2 id = gl_GlobalInvocationID.xy;
    if (id.x < uint(uSize) && id.y < uint(uSize))
    {
        vec3 rgb = vec3(0.03, 0.03, 0.05);
        if (id.y < uint(uMainRows))
        {
            float w = 0.5 + 0.75 * sin(uTime * 0.4);
            vec2 uv = (vec2(id) + 0.5) / vec2(uSize, uMainRows);
            float d = textureLod(uVolume, vec3(uv, w), 0.0).x;
            rgb = RampLookup(0.5 - d * 1.4);
        }
        else
        {
            int col = int(id.x >> 2);
            int sub = int(id.x & 3u);
            float bar = 1.0 - clamp(uintBitsToFloat(uProbes.data[col]), 0.0, 1.0);
            float f = (uSize - 0.5 - float(id.y)) / (uSize - uMainRows);
            if (sub != 3 && f <= bar)
                rgb = RampLookup(1.0);
        }
        imageStore(uOutput, ivec2(id), vec4(rgb, 1.0));
    }
}
";

    // Metal MSL
    // Textures map to texture(texture declaration order), buffers map to
    // buffer(buffer declaration order + 1), and workgroup size is not declared at compile
    // time. It is supplied at dispatch time through ComputeKernelDesc.WorkgroupX/Y/Z.

    /// <summary>kernel1 fill3d.</summary>
    const string SourceFillMsl = @"
#include <metal_stdlib>
using namespace metal;

struct Sdf3DParams
{
    float uTime;
    float uVolumeSize;
    float uPad0;
    float uPad1;
};

kernel void CSMain(
    constant Sdf3DParams& params [[buffer(0)]],
    texture3d<float, access::write> uVolume [[texture(0)]],
    uint3 id [[thread_position_in_grid]])
{
    if (id.x < (uint)params.uVolumeSize && id.y < (uint)params.uVolumeSize && id.z < (uint)params.uVolumeSize)
    {
        float3 p = (float3(id) + 0.5) / params.uVolumeSize * 2.0 - 1.0;
        float t = params.uTime;
        float r1 = 0.42 + 0.08 * sin(t);
        float d1 = length(p - float3(-0.28, 0.0, 0.0)) - r1;
        float d2 = length(p - float3(0.30, 0.18 * sin(t * 0.7), 0.0)) - 0.26;
        float d = min(d1, d2)
                + 0.04 * sin(p.x * 6.2832 + t) * sin(p.y * 6.2832) * sin(p.z * 6.2832);
        uVolume.write(float4(d, 0.0, 0.0, 0.0), id);
    }
}
";

    /// <summary>kernel2 probe1d (threadgroup array + threadgroup_barrier reduction).</summary>
    const string SourceProbeMsl = @"
#include <metal_stdlib>
using namespace metal;

struct Sdf3DParams
{
    float uTime;
    float uVolumeSize;
    float uPad0;
    float uPad1;
};

kernel void CSMain(
    constant Sdf3DParams& params [[buffer(0)]],
    texture3d<float> uVolume [[texture(0)]],
    device uint* uProbes [[buffer(1)]],
    sampler uSampler [[sampler(0)]],
    uint gx [[threadgroup_position_in_grid]],
    uint li [[thread_position_in_threadgroup]])
{
    threadgroup float gHit[64];

    float u = (float(gx) + 0.5) / params.uVolumeSize;
    float w = (float(li) + 0.5) / 64.0;
    float d = uVolume.sample(uSampler, float3(u, 0.5, w), level(0)).x;
    gHit[li] = d <= 0.0 ? w : 1.0;
    threadgroup_barrier(mem_flags::mem_threadgroup);

    for (uint s = 32; s > 0; s >>= 1)
    {
        if (li < s)
            gHit[li] = min(gHit[li], gHit[li + s]);
        threadgroup_barrier(mem_flags::mem_threadgroup);
    }

    if (li == 0)
        uProbes[gx] = as_type<uint>(gHit[0]);
}
";

    /// <summary>kernel3 slice2d (the ramp pointer must be passed explicitly into helper functions because MSL has no global resource visibility).</summary>
    const string SourceSliceMsl = @"
#include <metal_stdlib>
using namespace metal;

struct Sdf3DParams
{
    float uTime;
    float uSize;
    float uMainRows;
    float uPad0;
};

static float3 RampLookup(const device float* ramp, float s)
{
    float f = saturate(s) * 15.0;
    int i0 = int(floor(f));
    int i1 = min(i0 + 1, 15);
    float3 c0 = float3(ramp[i0 * 4], ramp[i0 * 4 + 1], ramp[i0 * 4 + 2]);
    float3 c1 = float3(ramp[i1 * 4], ramp[i1 * 4 + 1], ramp[i1 * 4 + 2]);
    return mix(c0, c1, f - float(i0));
}

kernel void CSMain(
    constant Sdf3DParams& params [[buffer(0)]],
    texture3d<float> uVolume [[texture(0)]],
    const device float* uRamp [[buffer(1)]],
    const device uint* uProbes [[buffer(2)]],
    texture2d<float, access::write> uOutput [[texture(1)]],
    sampler uSampler [[sampler(0)]],
    uint2 id [[thread_position_in_grid]])
{
    if (id.x < (uint)params.uSize && id.y < (uint)params.uSize)
    {
        float3 rgb = float3(0.03, 0.03, 0.05);
        if (id.y < (uint)params.uMainRows)
        {
            float w = 0.5 + 0.75 * sin(params.uTime * 0.4);
            float2 uv = (float2(id) + 0.5) / float2(params.uSize, params.uMainRows);
            float d = uVolume.sample(uSampler, float3(uv, w), level(0)).x;
            rgb = RampLookup(uRamp, 0.5 - d * 1.4);
        }
        else
        {
            int col = int(id.x >> 2);
            uint sub = id.x & 3;
            float bar = 1.0 - saturate(as_type<float>(uProbes[col]));
            float f = (params.uSize - 0.5 - float(id.y)) / (params.uSize - params.uMainRows);
            if (sub != 3 && f <= bar)
                rgb = RampLookup(uRamp, 1.0);
        }
        uOutput.write(float4(rgb, 1.0), id);
    }
}
";

    // WebGPU WGSL (delivered through the interop layer; seasonWebGPU.js source is not included)
    // @binding(i) follows declaration order, engine samplers always use @binding(15),
    // and the 3D volume format uses core-guaranteed rgba16float; see the class header for details.

    /// <summary>kernel1 fill3d.</summary>
    const string SourceFillWgsl = @"
struct Sdf3DParams
{
    uTime : f32,
    uVolumeSize : f32,
    uPad0 : f32,
    uPad1 : f32,
};

@group(0) @binding(0) var<uniform> params : Sdf3DParams;
@group(0) @binding(1) var uVolume : texture_storage_3d<rgba16float, write>;

@compute @workgroup_size(4, 4, 4)
fn CSMain(@builtin(global_invocation_id) id : vec3<u32>)
{
    let n = u32(params.uVolumeSize);
    if (id.x < n && id.y < n && id.z < n)
    {
        let p = (vec3<f32>(id) + vec3<f32>(0.5)) / vec3<f32>(params.uVolumeSize) * 2.0 - vec3<f32>(1.0);
        let t = params.uTime;
        let r1 = 0.42 + 0.08 * sin(t);
        let d1 = length(p - vec3<f32>(-0.28, 0.0, 0.0)) - r1;
        let d2 = length(p - vec3<f32>(0.30, 0.18 * sin(t * 0.7), 0.0)) - 0.26;
        let d = min(d1, d2)
              + 0.04 * sin(p.x * 6.2832 + t) * sin(p.y * 6.2832) * sin(p.z * 6.2832);
        textureStore(uVolume, vec3<i32>(id), vec4<f32>(d, 0.0, 0.0, 0.0));
    }
}
";

    /// <summary>kernel2 probe1d (var&lt;workgroup&gt; + workgroupBarrier; the barrier stays inside uniform control flow).</summary>
    const string SourceProbeWgsl = @"
struct Sdf3DParams
{
    uTime : f32,
    uVolumeSize : f32,
    uPad0 : f32,
    uPad1 : f32,
};

@group(0) @binding(0) var<uniform> params : Sdf3DParams;
@group(0) @binding(1) var uVolume : texture_3d<f32>;
@group(0) @binding(2) var<storage, read_write> uProbes : array<u32>;
@group(0) @binding(15) var uSampler : sampler;

var<workgroup> gHit : array<f32, 64>;

@compute @workgroup_size(64, 1, 1)
fn CSMain(@builtin(workgroup_id) gid : vec3<u32>,
          @builtin(local_invocation_index) li : u32)
{
    let u = (f32(gid.x) + 0.5) / params.uVolumeSize;
    let w = (f32(li) + 0.5) / 64.0;
    let d = textureSampleLevel(uVolume, uSampler, vec3<f32>(u, 0.5, w), 0.0).x;
    gHit[li] = select(1.0, w, d <= 0.0);
    workgroupBarrier();

    for (var s : u32 = 32u; s > 0u; s = s >> 1u)
    {
        if (li < s)
        {
            gHit[li] = min(gHit[li], gHit[li + s]);
        }
        workgroupBarrier();
    }

    if (li == 0u)
    {
        uProbes[gid.x] = bitcast<u32>(gHit[0]);
    }
}
";

    /// <summary>kernel3 slice2d.</summary>
    const string SourceSliceWgsl = @"
struct Sdf3DParams
{
    uTime : f32,
    uSize : f32,
    uMainRows : f32,
    uPad0 : f32,
};

@group(0) @binding(0) var<uniform> params : Sdf3DParams;
@group(0) @binding(1) var uVolume : texture_3d<f32>;
@group(0) @binding(2) var<storage, read> uRamp : array<f32>;
@group(0) @binding(3) var<storage, read> uProbes : array<u32>;
@group(0) @binding(4) var uOutput : texture_storage_2d<rgba8unorm, write>;
@group(0) @binding(15) var uSampler : sampler;

fn RampLookup(s : f32) -> vec3<f32>
{
    let f = clamp(s, 0.0, 1.0) * 15.0;
    let i0 = i32(floor(f));
    let i1 = min(i0 + 1, 15);
    let c0 = vec3<f32>(uRamp[i0 * 4], uRamp[i0 * 4 + 1], uRamp[i0 * 4 + 2]);
    let c1 = vec3<f32>(uRamp[i1 * 4], uRamp[i1 * 4 + 1], uRamp[i1 * 4 + 2]);
    return mix(c0, c1, f - f32(i0));
}

@compute @workgroup_size(8, 8, 1)
fn CSMain(@builtin(global_invocation_id) id : vec3<u32>)
{
    if (id.x < u32(params.uSize) && id.y < u32(params.uSize))
    {
        var rgb = vec3<f32>(0.03, 0.03, 0.05);
        if (id.y < u32(params.uMainRows))
        {
            let w = 0.5 + 0.75 * sin(params.uTime * 0.4);
            let uv = (vec2<f32>(f32(id.x), f32(id.y)) + vec2<f32>(0.5))
                   / vec2<f32>(params.uSize, params.uMainRows);
            let d = textureSampleLevel(uVolume, uSampler, vec3<f32>(uv, w), 0.0).x;
            rgb = RampLookup(0.5 - d * 1.4);
        }
        else
        {
            let col = i32(id.x >> 2u);
            let sub = id.x & 3u;
            let bar = 1.0 - clamp(bitcast<f32>(uProbes[col]), 0.0, 1.0);
            let f = (params.uSize - 0.5 - f32(id.y)) / (params.uSize - params.uMainRows);
            if (sub != 3u && f <= bar)
            {
                rgb = RampLookup(1.0);
            }
        }
        textureStore(uOutput, vec2<i32>(i32(id.x), i32(id.y)), vec4<f32>(rgb, 1.0));
    }
}
";
}
