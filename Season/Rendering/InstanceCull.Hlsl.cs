// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

public sealed partial class InstanceCullEffect
{
    // A7's four D3D12 kernels. The [numthreads] of reset and cull must equal the C# value
    // InstanceCull.CullGroupSize(64), and MAX_PRIMITIVES must equal InstanceCull.MaxPrimitives(16): these two numbers are the entire
    // basis for "fixed-stride addressing", and any drift shows up as an arg-table misalignment rather than a compile error. reset covers
    // InstanceCull.ResetThreads(80) entries in InstanceCull.ResetGroups(2) groups; the threads past the last slot exit on the slot test.
    // gather's [numthreads] is likewise 64, but its group count is given in the X/Y dimensions,
    // and finalize is fixed single-threaded -- it only reads 5 uints and reduces them into a dispatch size, with nothing to parallelize.
    //
    // Parameters always go through a cbuffer + struct instance rather than SM5.1's ConstantBuffer<T> template: the DX12 backend compiles cs_5_0 with fxc,
    // where the template form reports "unrecognized identifier 'ConstantBuffer'", and all four kernels would fail registration together,
    // showing up as all of A7 silently falling back to CPU culling (InstanceCull.Enabled stays false and the image looks completely normal, so it is hard to notice).
    // The two forms have the same byte layout on b0, and the field accesses in the code need not change either.

    const string ResetHlsl = """
// A7 reset: writes each (slot, primitive) indirect arg entry to fixed values and zeroes the instance count.
// mode 0 fills the args, mode 1 fans the instance count of primitive 0 within a slot out to the remaining primitives.
// It is split into two dispatches rather than done in one, because the fan-out must wait for the cull kernel to finish accumulating the counts.

#define MAX_PRIMITIVES 16
#define ARGS_STRIDE 20

struct ResetParams
{
    uint primCount;
    uint slotCount;
    uint mode;
    uint pad;
};

cbuffer ResetParamsCB : register(b0)
{
    ResetParams resetParams;
};
ByteAddressBuffer primInfo : register(t0);
RWByteAddressBuffer drawArgs : register(u0);

[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    uint idx = id.x;
    uint slot = idx / MAX_PRIMITIVES;
    uint prim = idx % MAX_PRIMITIVES;
    ResetParams p = resetParams;
    if (slot >= p.slotCount || prim >= p.primCount)
        return;

    uint base = idx * ARGS_STRIDE;
    uint info = prim * 16;
    drawArgs.Store(base + 0, primInfo.Load(info + 0));
    drawArgs.Store(base + 4, 0);
    drawArgs.Store(base + 8, primInfo.Load(info + 4));
    drawArgs.Store(base + 12, primInfo.Load(info + 8));
    drawArgs.Store(base + 16, 0);
}
""";

    const string CullHlsl = """
// A7 per-instance culling: one thread per instance, computing the world bounding sphere and then testing it against the six clipping planes for each slot.
// Passing instances write their original index into that slot's index table and increment the slot's instance count.
//
// The sphere and matrix conventions follow the engine: the world matrix uses System.Numerics' row order (M11..M44 stored row by row),
// point transform is a row vector on the right, so "a point's world position = the weighted sum of the rows plus the translation row".
// The radius uses the world AABB's circumscribed-sphere upper bound, the length of |Row0|*ex + |Row1|*ey + |Row2|*ez:
// it can only be too large, never too small; too large means drawing a few extra instances, too small would cull visible instances.

#define MAX_PRIMITIVES 16
#define WORLD_STRIDE 80
#define ARGS_STRIDE 20

struct CullParams
{
    float4 localCenter;
    float4 localExtents;
    uint instanceCount;
    uint slotCount;
    uint primCount;
    uint capacity;
    uint slotMask;
    uint pad0;
    uint pad1;
    uint pad2;
};

cbuffer CullParamsCB : register(b0)
{
    CullParams cullParams;
};
ByteAddressBuffer slotMatrices : register(t0);
ByteAddressBuffer instanceWorlds : register(t1);
RWByteAddressBuffer drawArgs : register(u0);
RWByteAddressBuffer visibleIds : register(u1);

// clip = pos * VP (row-vector convention), so clip.x is the dot product of the position with "column 0", and column 3 is w.
// The six planes thus fall out directly from row-column combinations, without needing to transpose first.
bool VisibleInSlot(uint slot, float3 center, float radius)
{
    uint m = slot * 64;
    float4 r0 = asfloat(slotMatrices.Load4(m));
    float4 r1 = asfloat(slotMatrices.Load4(m + 16));
    float4 r2 = asfloat(slotMatrices.Load4(m + 32));
    float4 r3 = asfloat(slotMatrices.Load4(m + 48));

    float4 c0 = float4(r0.x, r1.x, r2.x, r3.x);
    float4 c1 = float4(r0.y, r1.y, r2.y, r3.y);
    float4 c2 = float4(r0.z, r1.z, r2.z, r3.z);
    float4 c3 = float4(r0.w, r1.w, r2.w, r3.w);
    float4 p = float4(center, 1.0);

    if (dot(c3 + c0, p) < -radius) return false;
    if (dot(c3 - c0, p) < -radius) return false;
    if (dot(c3 + c1, p) < -radius) return false;
    if (dot(c3 - c1, p) < -radius) return false;
    if (dot(c3 + c2, p) < -radius) return false;
    if (dot(c3 - c2, p) < -radius) return false;
    return true;
}

[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    CullParams p = cullParams;
    uint i = id.x;
    if (i >= p.instanceCount)
        return;

    uint w = i * WORLD_STRIDE;
    float3 r0 = asfloat(instanceWorlds.Load4(w)).xyz;
    float3 r1 = asfloat(instanceWorlds.Load4(w + 16)).xyz;
    float3 r2 = asfloat(instanceWorlds.Load4(w + 32)).xyz;
    float3 r3 = asfloat(instanceWorlds.Load4(w + 48)).xyz;

    float3 ext = abs(p.localExtents.xyz);
    float3 center = p.localCenter.x * r0 + p.localCenter.y * r1 + p.localCenter.z * r2 + r3;
    float3 bound = abs(r0) * ext.x + abs(r1) * ext.y + abs(r2) * ext.z;
    float radius = length(bound);

    for (uint slot = 0; slot < p.slotCount; slot++)
    {
        if ((p.slotMask & (1u << slot)) == 0u)
            continue;
        if (!VisibleInSlot(slot, center, radius))
            continue;

        // Each slot is compressed independently: an instance can be visible in several cascades, and the index table holds one segment per slot.
        uint k;
        drawArgs.InterlockedAdd(slot * MAX_PRIMITIVES * ARGS_STRIDE + 4, 1u, k);
        visibleIds.Store((slot * p.capacity + k) * 4, i);
    }
}
""";

    const string FinalizeHlsl = """
// A7 finalize: folds the visible counts that cull left in each arg entry into a single dispatch size, written to IndirectDispatchArgs.
//
// Why it is needed: the index table is compressed, so the CPU has no way to know how many are visible, and gather can only dispatch by the capacity upper bound.
// This step replaces the "upper bound" with the "actual maximum visible count", letting gather skip that batch of threads that are bound to exit early.
//
// Single-threaded is deliberate. The slot count is at most 5; reading five uints serially is far below the cost of opening a workgroup and doing a reduction with synchronization,
// and it avoids writing a threadsync dialect per backend -- exactly that kind of duplication is the source of drift.

struct FinalizeParams
{
    uint slotCount;     // how many slots to scan, which is also dispatch's Y-dimension group count
    uint slotStride;    // how many bytes of arg table one slot occupies (MaxPrimitives * ARGS_STRIDE)
    uint groupSize;     // gather's workgroup size, X-dimension group count = ceil(maxCount / groupSize)
    uint pad;
};

cbuffer FinalizeParamsCB : register(b0)
{
    FinalizeParams finalizeParams;
};
ByteAddressBuffer drawArgs : register(t0);
RWByteAddressBuffer dispatchArgs : register(u0);

[numthreads(1, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    FinalizeParams p = finalizeParams;
    uint maxCount = 0;
    for (uint slot = 0; slot < p.slotCount; slot++)
        maxCount = max(maxCount, drawArgs.Load(slot * p.slotStride + 4));

    // When maxCount is 0, groups is also 0: all four backends allow dispatching "0 groups" (it does nothing),
    // so a frame whose whole group is culled away does not need another CPU-side branch.
    uint groups = (maxCount + p.groupSize - 1) / p.groupSize;
    dispatchArgs.Store3(0, uint3(groups, p.slotCount, 1));
}
""";

    const string GatherHlsl = """
// A7 gather: the second stage. Culling only produces "the original index table + per-slot counts"; this kernel materializes the instance data into
// the slot segments by index, and the draw side then uses this stream as the instance buffer, with the instance count given by the indirect args.
//
// This stage is necessary, not gratuitous: IA's instance stepping is linear and cannot be indexed by original index,
// and having the vertex stage index the table itself would need two new SRVs -- the D3D12 root signature has only 3 DWORDs left, which cannot afford them.
// The cost is writing "visible count" extra 80-byte records per frame, all done entirely on the GPU, producing no CPU upload or readback.
//
// Every slot in the tag mask additionally overwrites M14 of each record with float(original index + 1). Indirect draws are compressed, so
// SV_InstanceID is the position in the visible list, while motion vectors (t9/t10), per-instance bone palettes (t6/t8) and per-instance
// glyph tables are laid out by original index; the vertex stage decodes the tag back into that index and restores M14 to 0. M14 is free to
// carry it: every instance transform is affine, so M14 is exactly 0 in the source stream and an untagged record decodes as "no remap".
// The index is stored as a float rather than asuint: a small integer bit pattern is a denormal, which a vertex fetch is allowed to flush to
// zero, while float is exact up to 2^24.
// The shadow slots are tagged too, not only the main view: the shadow vertex stage is the same source under SHADOW_PASS and decodes before
// branching, so a tagged shadow record yields the very world matrix an untagged one did, and only then can a skinned group read its own
// palette from a compacted shadow segment. A mask rather than a single slot keeps "which slots are tagged" a caller decision.
// Several gathers may run against one visible/args pair (one per instance stream of a group): the fan-out only reads primitive 0, which no
// gather writes, so repeating it rewrites the same values.

#define MAX_PRIMITIVES 16
#define WORLD_STRIDE 80
#define ARGS_STRIDE 20

struct GatherParams
{
    uint capacity;
    uint primCount;
    uint slotCount;
    uint tagMask;
};

cbuffer GatherParamsCB : register(b0)
{
    GatherParams gatherParams;
};
ByteAddressBuffer visibleIds : register(t0);
ByteAddressBuffer sourceWorlds : register(t1);
RWByteAddressBuffer sortedWorlds : register(u0);
RWByteAddressBuffer drawArgs : register(u1);

[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    GatherParams p = gatherParams;
    // Two-dimensional mapping: X = index within a slot, Y = slot. Fixed dispatch and indirect dispatch share it --
    // the only thing that varies is the group count of the X dimension, so the two A/B runs must match pixel for pixel.
    uint slot = id.y;
    uint k = id.x;
    if (slot >= p.slotCount)
        return;

    uint argsBase = slot * MAX_PRIMITIVES * ARGS_STRIDE;
    uint count = drawArgs.Load(argsBase + 4);

    // Fan-out: primitive 0's instance count is the slot's total visible count; the remaining primitives copy it verbatim.
    // Each primitive has a different index range, so the arg table must store one entry per primitive rather than sharing a single one.
    if (k == 0)
    {
        for (uint q = 1; q < p.primCount; q++)
            drawArgs.Store(argsBase + q * ARGS_STRIDE + 4, count);
    }
    if (k >= count)
        return;

    uint vis = visibleIds.Load((slot * p.capacity + k) * 4);
    uint src = vis * WORLD_STRIDE;
    uint dst = ((slot * p.capacity) + k) * WORLD_STRIDE;
    for (uint o = 0; o < WORLD_STRIDE; o += 16)
        sortedWorlds.Store4(dst + o, sourceWorlds.Load4(src + o));
    if (((p.tagMask >> slot) & 1u) != 0u)
        sortedWorlds.Store(dst + 12, asuint(float(vis + 1)));
}
""";
}
