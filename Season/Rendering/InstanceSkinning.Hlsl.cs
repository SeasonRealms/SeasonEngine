// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// P1: the D3D12 kernel source for GPU skinning. See <see cref="InstanceSkinning"/> for the buffer layout, which this file and the
/// other three dialects must agree on byte for byte.
///
/// Matrix convention. C# <c>Matrix4x4</c> is row-major with row vectors, and the bake writes it verbatim, so an HLSL <c>float4x4</c>
/// read back from the same bytes is the same matrix and <c>mul</c> composes it in the same order the CPU does. HLSL indexing is by
/// row, which is why <c>m[0]</c> is the first float4 in memory. The palette however stores <c>transpose(IBM * world * inverseMesh)</c>,
/// matching the CPU path, because the vertex shader consumes it as a column-vector transform.
/// </summary>
public sealed partial class InstanceSkinningEffect
{
    static ComputeKernelDesc CreateSkinningKernel() => new()
    {
        Name = "instanceSkinning",
        Source = new ShaderSourceSet
        {
            Hlsl = HlslSource,
            Glsl = GlslSource,
            Msl = MslSource,
            Wgsl = WgslSource,
            EntryPoint = "CSMain",
        },
        Bindings = new[]
        {
            new ComputeBindingDesc { Type = ComputeBindingType.Params, SizeInBytes = InstanceSkinning.KernelParamsBytes },
            new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferRead },
            new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferRead },
            new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferRead },
            new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferRead },
            new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferReadWrite },
            new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferReadWrite },
        },
        WorkgroupX = InstanceSkinning.GroupSize,
        WorkgroupY = 1,
        WorkgroupZ = 1,
    };

    const string HlslSource = @"
cbuffer KernelParams : register(b0)
{
    uint jointCount;
    uint instanceCount;
    uint paletteStride;
    uint tagRows;
    uint jointStride;
    uint trackStride;
    uint keyStride;
    uint scratchStride;
};

ByteAddressBuffer jointTable : register(t0);
ByteAddressBuffer trackTable : register(t1);
ByteAddressBuffer tracksIn   : register(t2);
ByteAddressBuffer paramsIn   : register(t3);
RWByteAddressBuffer scratchOut : register(u0);
RWByteAddressBuffer paletteOut : register(u1);

// Joint table byte offsets. The layout lives in InstanceSkinning's summary; these names exist so a mismatch is a compile error in
// one place instead of a plausible-looking wrong pose.
#define JOINT_PALETTE    0
#define JOINT_PARENT     4
#define JOINT_MASK       8
#define JOINT_REST_T    16
#define JOINT_REST_R    32
#define JOINT_REST_S    48
#define JOINT_CHAIN     64
#define JOINT_IBM      128
#define JOINT_INV_MESH 192

// The pose-pool tag lanes, mirroring PoseTagCurrentOffset/PoseTagPreviousOffset. They live in the last row of each instance
// block, one row after the joints, where nothing multiplies by the matrix - see the layout note in InstanceSkinning.
#define PALETTE_TAG_CURRENT  48
#define PALETTE_TAG_PREVIOUS 52

float4x4 LoadMatrix(ByteAddressBuffer buf, uint offset)
{
    float4x4 m;
    m[0] = asfloat(buf.Load4(offset + 0));
    m[1] = asfloat(buf.Load4(offset + 16));
    m[2] = asfloat(buf.Load4(offset + 32));
    m[3] = asfloat(buf.Load4(offset + 48));
    return m;
}

// RWByteAddressBuffer is a distinct type in HLSL and does not convert to ByteAddressBuffer implicitly
// (fxc X3017), which the scratch read below needs. Both expose the same Load4, so a second overload is
// cheaper than a common base type HLSL does not have.
float4x4 LoadMatrix(RWByteAddressBuffer buf, uint offset)
{
    float4x4 m;
    m[0] = asfloat(buf.Load4(offset + 0));
    m[1] = asfloat(buf.Load4(offset + 16));
    m[2] = asfloat(buf.Load4(offset + 32));
    m[3] = asfloat(buf.Load4(offset + 48));
    return m;
}

// S * R * T, identical to Matrix4x4.CreateScale * CreateFromQuaternion * CreateTranslation.
float4x4 LocalMatrix(float3 s, float4 q, float3 t)
{
    float xx = q.x * q.x, yy = q.y * q.y, zz = q.z * q.z;
    float xy = q.x * q.y, wz = q.z * q.w, xz = q.x * q.z, wy = q.y * q.w;
    float yz = q.y * q.z, wx = q.x * q.w;
    float4x4 m;
    m[0] = float4(s.x * (1.0 - 2.0 * (yy + zz)), s.x * 2.0 * (xy + wz), s.x * 2.0 * (xz - wy), 0.0);
    m[1] = float4(s.y * 2.0 * (xy - wz), s.y * (1.0 - 2.0 * (xx + zz)), s.y * 2.0 * (yz + wx), 0.0);
    m[2] = float4(s.z * 2.0 * (xz + wy), s.z * 2.0 * (yz - wx), s.z * (1.0 - 2.0 * (xx + yy)), 0.0);
    m[3] = float4(t, 1.0);
    return m;
}

// Shortest-arc slerp, matching Quaternion.Slerp including its near-parallel shortcut.
float4 QSlerp(float4 a, float4 b, float t)
{
    float d = dot(a, b);
    if (d < 0.0) { b = -b; d = -d; }
    if (d > 0.999999) return normalize(lerp(a, b, t));
    float theta = acos(clamp(d, -1.0, 1.0));
    float inv = 1.0 / sin(theta);
    return sin((1.0 - t) * theta) * inv * a + sin(t * theta) * inv * b;
}

[numthreads(64, 1, 1)]
void CSMain(uint3 tid : SV_DispatchThreadID)
{
    uint inst = tid.x;
    if (inst < instanceCount)
    {
        uint pbase = inst * 16;
        uint clip = paramsIn.Load(pbase + 0);
        float time = asfloat(paramsIn.Load(pbase + 4));
        uint evaluate = paramsIn.Load(pbase + 8);
        uint poolRow = paramsIn.Load(pbase + 12);

        // P3 L2: poolRow is the first row of the block this instance draws its joints from, or 0 when it owns its own block.
        // Pool blocks are numbered after the instance blocks, so one block index addresses both the scratch and the palette -
        // and the instance slot is that index only when the instance is unpooled.
        uint blockIndex = poolRow != 0u ? poolRow / paletteStride : inst;

        // The tag is written for every instance, including the unpooled ones, which store 0. A frame that pools nothing then
        // produces exactly the bytes the historical path did, and a slot whose previous occupant was pooled cannot keep a stale
        // row number that would send the vertex stage into a block this frame never wrote.
        //
        // A pooled instance publishes the same row for both frames on purpose. Pool blocks are numbered inside the frame that built
        // them, so last frame's row would name a block holding a different group's pose - a wrong velocity rather than a missing
        // one. Pointing both at this frame's block yields zero motion instead, which is the honest answer for an instance whose
        // history the pool did not keep.
        if (tagRows != 0u)
        {
            uint tagOffset = (inst * paletteStride + paletteStride - 1u) * 64u;
            paletteOut.Store(tagOffset + PALETTE_TAG_CURRENT, poolRow);
            paletteOut.Store(tagOffset + PALETTE_TAG_PREVIOUS, poolRow);
        }

        // A sharer has nothing left to compute: its representative is already writing the block the tag above points at, and
        // the tag is the only thing this thread had to publish. This early-out is the whole point - it turns N evaluations of a
        // (clip, tick) group into one.
        if (poolRow != 0u && evaluate == 0u)
            return;

        uint scratchBase = blockIndex * jointCount * scratchStride;
        uint paletteBase = blockIndex * paletteStride * 64;

        for (uint j = 0; j < jointCount; j++)
        {
            uint jb = j * jointStride;
            uint paletteIndex = jointTable.Load(jb + JOINT_PALETTE);

            // Which clip drives which joint is a per-track fact: frameCount == 0 means this clip never touches it and the joint
            // keeps the rest pose baked into the joint table, exactly what the CPU path's reset-to-initial leaves behind.
            bool driven = false;
            float4 rt = float4(0.0, 0.0, 0.0, 0.0);
            float4 rq = float4(0.0, 0.0, 0.0, 1.0);
            float4 rs = float4(0.0, 0.0, 0.0, 0.0);
            {
                uint tb = (clip * jointCount + j) * trackStride;
                uint frameCount = trackTable.Load(tb + 0);
                if (frameCount != 0)
                {
                    driven = true;
                    uint firstKey = trackTable.Load(tb + 4);
                    float rate = asfloat(trackTable.Load(tb + 8));
                    float ft = time * rate;
                    uint f0 = min((uint)ft, frameCount - 1);
                    uint f1 = min(f0 + 1, frameCount - 1);
                    float a = ft - (float)f0;

                    uint i0 = (firstKey + f0 * jointCount + j) * keyStride;
                    uint i1 = (firstKey + f1 * jointCount + j) * keyStride;
                    float4 t0 = asfloat(tracksIn.Load4(i0 + 0));
                    float4 q0 = asfloat(tracksIn.Load4(i0 + 16));
                    float4 s0 = asfloat(tracksIn.Load4(i0 + 32));
                    float4 t1 = asfloat(tracksIn.Load4(i1 + 0));
                    float4 q1 = asfloat(tracksIn.Load4(i1 + 16));
                    float4 s1 = asfloat(tracksIn.Load4(i1 + 32));

                    rt = float4(lerp(t0.xyz, t1.xyz, a), 0.0);
                    rq = QSlerp(q0, q1, a);
                    rs = float4(lerp(s0.xyz, s1.xyz, a), 0.0);
                }
            }

            if (!driven)
            {
                rt = asfloat(jointTable.Load4(jb + JOINT_REST_T));
                rq = asfloat(jointTable.Load4(jb + JOINT_REST_R));
                rs = asfloat(jointTable.Load4(jb + JOINT_REST_S));
            }

            float4x4 local = LocalMatrix(rs.xyz, rq, rt.xyz);

            // Parent = staticChain * (porder >= 0 ? scratch[porder] : identity). The chain holds the non-joint nodes between this
            // joint and its nearest joint ancestor; those cannot move, which is exactly what the applicability rule guarantees.
            int porder = (int)jointTable.Load(jb + JOINT_PARENT);
            float4x4 chain = LoadMatrix(jointTable, jb + JOINT_CHAIN);
            float4x4 parent;
            if (porder >= 0)
                parent = mul(chain, LoadMatrix(scratchOut, scratchBase + (uint)porder * scratchStride));
            else
                parent = chain;

            float4x4 world = mul(local, parent);
            uint sb = scratchBase + j * scratchStride;
            scratchOut.Store4(sb + 0, asuint(world[0]));
            scratchOut.Store4(sb + 16, asuint(world[1]));
            scratchOut.Store4(sb + 32, asuint(world[2]));
            scratchOut.Store4(sb + 48, asuint(world[3]));

            float4x4 ibm = LoadMatrix(jointTable, jb + JOINT_IBM);
            float4x4 invMesh = LoadMatrix(jointTable, jb + JOINT_INV_MESH);
            float4x4 bone = transpose(mul(mul(ibm, world), invMesh));

            uint pb = paletteBase + paletteIndex * 64;
            paletteOut.Store4(pb + 0, asuint(bone[0]));
            paletteOut.Store4(pb + 16, asuint(bone[1]));
            paletteOut.Store4(pb + 32, asuint(bone[2]));
            paletteOut.Store4(pb + 48, asuint(bone[3]));
        }
    }
}
";
}
