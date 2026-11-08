// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

public sealed partial class InstanceCullEffect
{
    // A7's three D3D12 kernels. The [numthreads] of cull must equal the C# value
    // InstanceCull.CullGroupSize(64), and MAX_PRIMITIVES must equal InstanceCull.MaxPrimitives(16): these two numbers are the entire
    // basis for "fixed-stride addressing", and any drift shows up as an arg-table misalignment rather than a compile error.
    // gather's [numthreads] is likewise 64, but its group count is given in the X/Y dimensions,
    // and finalize is fixed single-threaded -- it only reads 5 uints and reduces them into a dispatch size, with nothing to parallelize.
    //
    // A7b removed the fourth kernel (reset). What it did split in two: the frame-invariant part of the arg table is now uploaded from
    // InstanceCull.BuildArgsTemplate when the primitive set changes, and the per-slot instance counter is a region of the same arg
    // buffer that gather clears at the end of every run. That is one dispatch fewer for every participating group, every frame.
    //
    // Parameters always go through a cbuffer + struct instance rather than SM5.1's ConstantBuffer<T> template: the DX12 backend compiles cs_5_0 with fxc,
    // where the template form reports "unrecognized identifier 'ConstantBuffer'", and all three kernels would fail registration together,
    // showing up as all of A7 silently falling back to CPU culling (InstanceCull.Enabled stays false and the image looks completely normal, so it is hard to notice).
    // The two forms have the same byte layout on b0, and the field accesses in the code need not change either.
    //
    // Same failure shape, different cause -- known fxc trap: compiling cull with /Od /Zi makes fxc report
    // "X4000: use of potentially uninitialized variable (VisibleInSlot)". That is a false positive on a helper with several early returns; with the runtime's
    // flags (0) the same source is clean, which is why it is left as is. It matters anyway because any diagnostic is fatal to ShaderCompiler: turning debug
    // flags on does not produce a warning to investigate, it drops all of A7 back to CPU culling with a perfectly normal-looking image. If cull stops
    // participating, check the compile flags before checking the kernel.
    //
    // slotCountsOffset is a parameter of all three kernels on purpose. Replacing it with the literal 1600 would put the accumulator inside the arg table and
    // let the atomic add corrupt the indirect draw arguments -- see the A7b contract on InstanceCull for why that cannot be caught afterwards.

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
    uint slotCountsOffset;
    float texelNdc;     // L1.2: a caster's NDC radius below this is dropped from the slots in texelSlots
    uint texelSlots;    // L1.2: which slots that test may act on (the orthographic ones)
    uint occlusionSlots;    // L2.2: which slots may be confirmed visible from the pyramid (the main view only)
    uint occlusionWidth;    // L2.2: level 0 of the pyramid, in texels; zero unless occlusionSlots is set
    uint occlusionHeight;   // L2.2: same
    float occlusionMargin;  // L2.2: level-0 texels of slack on top of the block's circumradius
};

cbuffer CullParamsCB : register(b0)
{
    CullParams cullParams;
};
ByteAddressBuffer slotMatrices : register(t0);
ByteAddressBuffer instanceWorlds : register(t1);
Texture2D<float> occlusionPyramid : register(t2);
SamplerState occlusionSampler : register(s0);
RWByteAddressBuffer drawArgs : register(u0);
RWByteAddressBuffer visibleIds : register(u1);

// clip = pos * VP (row-vector convention), so clip.x is the dot product of the position with "column 0", and column 3 is w.
// The six planes thus fall out directly from row-column combinations, without needing to transpose first.
// Single exit with a preinitialized result: fxc calls a multi-exit callee "potentially uninitialized" (X4000) once it grows
// past a handful of returns, and these warnings are fatal -- the same reason SampleShadowTile in Pipeline.cs is shaped this way.
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

    bool visible = dot(c3 + c0, p) >= -radius;
    visible = visible && dot(c3 - c0, p) >= -radius;
    visible = visible && dot(c3 + c1, p) >= -radius;
    visible = visible && dot(c3 - c1, p) >= -radius;
    visible = visible && dot(c3 + c2, p) >= -radius;
    visible = visible && dot(c3 - c2, p) >= -radius;

    // L1.2: sub-texel casters. The lengths of the first two columns are this slot's world-to-NDC scale per axis, so radius
    // times the larger of them is the caster's NDC radius. Taking the larger is the conservative side: a caster is only
    // truly texel-sized when it shrinks below one on both axes. texelNdc is 0 when the test is off, and a radius is never
    // negative, so "off" is this comparison staying false rather than a separate branch to keep in sync across backends.
    bool orthographic = (cullParams.texelSlots & (1u << slot)) != 0u;
    float scale = max(length(float3(r0.x, r1.x, r2.x)), length(float3(r0.y, r1.y, r2.y)));
    visible = visible && (!orthographic || radius * scale >= cullParams.texelNdc);

    return visible;
}

// L2.2: pyramid confirmation, main-view slot only. The pyramid stores the NEAREST depth of every 2x2 block of the
// depth buffer -- it is the same min-reduce HiZ builds for SSR -- so the one statement it can support is a positive
// one: if the sphere's farthest point is still nearer than the block's nearest surface, then at that block the
// sphere is in front of everything and putting the instance in the slot is what the plane test would have decided
// too. The opposite statement does not follow: a block holding something nearer says nothing about whether that
// something covers the sphere. That asymmetry is why this helper can only confirm, and why the caller gates it on a
// mask instead of tuning it with a threshold. See InstanceCull.OcclusionCull.
//
// Single exit with preinitialized results, the same shape as VisibleInSlot and for the same fxc reason.
bool PyramidConfirmsVisible(uint slot, float3 center, float radius)
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

    float4 clip = float4(dot(c0, p), dot(c1, p), dot(c2, p), dot(c3, p));

    // Each clip component is widened by the sphere's extent along that row, which is the row's xyz length, and every
    // extent below is then divided by the smallest w the sphere reaches, so all four bounds stay conservative in the
    // direction that matters: a rectangle that can only be too large and a depth that can only be too far.
    float wMin = clip.w - radius * length(c3.xyz);
    float invW = 1.0 / max(wMin, 1e-4);

    // Level-0 texel holding the sphere's projected center, clamped so the fetch always stays in range. Clamping cannot
    // confirm anything the projection did not put there -- the coverage test below is what decides that.
    float2 size = float2(cullParams.occlusionWidth, cullParams.occlusionHeight);
    float2 uv = clip.xy * invW * float2(0.5, -0.5) + 0.5;
    float2 texel = uv * size;
    float2 coord = clamp(floor(texel), float2(0.0, 0.0), size - 1.0);

    // Screen radius in level-0 texels on the smaller axis, minus the block's own circumradius (a block spans one texel
    // on each side of its center, so sqrt(0.5)) and the caller's margin. What is left is how far the block's center may
    // sit from the projected center and still be covered by the sphere; a negative remainder means the sphere is too
    // small to contain a whole block and nothing can be confirmed from it.
    float2 rad = float2(radius * length(c0.xyz) * invW * 0.5 * size.x,
                        radius * length(c1.xyz) * invW * 0.5 * size.y);
    float reach = min(rad.x, rad.y) - 0.70710678 - cullParams.occlusionMargin;
    float2 delta = coord + 0.5 - texel;
    bool covered = reach > 0.0 && dot(delta, delta) <= reach * reach;

    // Farthest NDC depth the sphere can reach against the block's nearest surface. Sampling at a texel center is an
    // exact texel read under the engine's linear-clamp sampler, the same invariant HiZ's reduce leans on.
    float farDepth = (clip.z + radius * length(c2.xyz)) * invW;
    float tileNear = occlusionPyramid.SampleLevel(occlusionSampler, (coord + 0.5) / size, 0.0);

    return wMin > 1e-4 && covered && farDepth < tileNear;
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
        // L2.2: a confirmation replaces the plane test rather than adding to it: both answer "is this instance in the
        // main view", and the pyramid answers it with one fetch instead of six plane tests. An unconfirmed slot falls
        // through unchanged, so a frame with the mask clear runs exactly the code it ran before.
        bool confirmed = (p.occlusionSlots & (1u << slot)) != 0u
            && PyramidConfirmsVisible(slot, center, radius);
        if (!confirmed && !VisibleInSlot(slot, center, radius))
            continue;

        // Each slot is compressed independently: an instance can be visible in several cascades, and the index table holds one segment per slot.
        // A7b: the counter lives in the arg buffer's accumulator region, not in the arg table's InstanceCount -- see InstanceCull.SlotCountsOffset
        // for why the two cannot be the same word.
        uint k;
        drawArgs.InterlockedAdd(p.slotCountsOffset + slot * 4u, 1u, k);
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
    uint slotCountsOffset; // where the per-slot visible counts sit inside the arg buffer (InstanceCull.SlotCountsOffset)
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
        maxCount = max(maxCount, drawArgs.Load(p.slotCountsOffset + slot * 4));

    // At least one group, even when maxCount is 0. Dispatching 0 groups is legal and does nothing -- but "nothing" is
    // exactly the wrong thing here: gather's k == 0 thread is the only writer of the arg table's InstanceCount and the
    // only clearer of the slot-count accumulator, and a 0-group dispatch has no such thread. The arg table would keep
    // the previous frame's count and replay that frame's visible set once. One group of 64 threads that all fail
    // k >= count on the first test costs less than the CPU-side branch that would be needed to avoid it.
    uint groups = max((maxCount + p.groupSize - 1) / p.groupSize, 1u);
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
    uint slotCountsOffset;  // A7b: where the per-slot visible counts sit inside the arg buffer
    uint clearSlotCounts;   // A7b: 1 on the last stream of a group, which is the dispatch that resets the accumulator
    uint pad0;
    uint pad1;
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
    // A7b: the count comes from the accumulator, which cull filled this frame; the arg table's own InstanceCount is
    // written below, so it only ever holds a finished frame's result for the draw to read.
    uint count = drawArgs.Load(p.slotCountsOffset + slot * 4);

    // Fan-out: primitive 0's instance count is the slot's total visible count; the remaining primitives copy it verbatim.
    // Each primitive has a different index range, so the arg table must store one entry per primitive rather than sharing a single one.
    if (k == 0)
    {
        drawArgs.Store(argsBase + 4, count);
        for (uint q = 1; q < p.primCount; q++)
            drawArgs.Store(argsBase + q * ARGS_STRIDE + 4, count);
        // Only the last gather of a group clears: every stream of a group reads the same counts, so clearing on the first would
        // make the rest publish zeroes into the arg table and the group would vanish for the frame.
        if (p.clearSlotCounts != 0u)
            drawArgs.Store(p.slotCountsOffset + slot * 4, 0u);
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
