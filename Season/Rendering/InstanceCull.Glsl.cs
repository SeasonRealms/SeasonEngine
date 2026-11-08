// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

public sealed partial class InstanceCullEffect
{
    // A7's three kernels on the Vulkan side. Params go through push constants, and the remaining resources
    // are placed by "declared index is the binding number", so binding 0 is reserved as a placeholder for
    // Params and is never taken by any buffer, consistent with the ComputeBindingType contract.
    // A7b removed the reset kernel: the frame-invariant part of the arg table is uploaded from InstanceCull.BuildArgsTemplate
    // when the primitive set changes, and the per-slot counter is a region of the arg buffer that gather clears every run.

    const string CullGlsl = """
#version 450

#define MAX_PRIMITIVES 16u
#define WORLD_STRIDE_F 20u
#define ARGS_STRIDE_U 5u

layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;

layout(push_constant) uniform CullParams
{
    vec4 localCenter;
    vec4 localExtents;
    uint instanceCount;
    uint slotCount;
    uint primCount;
    uint capacity;
    uint slotMask;
    uint slotCountsOffset;
    float texelNdc;     // L1.2: a caster's NDC radius below this is dropped from the slots in texelSlots
    uint texelSlots;    // L1.2: which slots that test may act on (the orthographic ones)
} cullParams;

layout(std430, binding = 1) readonly buffer SlotMatrices { float slotMatrices[]; };
layout(std430, binding = 2) readonly buffer Worlds { float worlds[]; };
layout(std430, binding = 3) buffer DrawArgs { uint drawArgs[]; };
layout(std430, binding = 4) buffer VisibleIds { uint visibleIds[]; };

// clip = pos * VP (row-vector convention): clip.x is the dot product of the position with column 0, and
// column 3 is w, so the six clip planes follow directly from row/column combinations.
bool visibleInSlot(uint slot, vec3 center, float radius)
{
    uint m = slot * 16u;
    vec4 r0 = vec4(slotMatrices[m + 0u], slotMatrices[m + 1u], slotMatrices[m + 2u], slotMatrices[m + 3u]);
    vec4 r1 = vec4(slotMatrices[m + 4u], slotMatrices[m + 5u], slotMatrices[m + 6u], slotMatrices[m + 7u]);
    vec4 r2 = vec4(slotMatrices[m + 8u], slotMatrices[m + 9u], slotMatrices[m + 10u], slotMatrices[m + 11u]);
    vec4 r3 = vec4(slotMatrices[m + 12u], slotMatrices[m + 13u], slotMatrices[m + 14u], slotMatrices[m + 15u]);

    vec4 c0 = vec4(r0.x, r1.x, r2.x, r3.x);
    vec4 c1 = vec4(r0.y, r1.y, r2.y, r3.y);
    vec4 c2 = vec4(r0.z, r1.z, r2.z, r3.z);
    vec4 c3 = vec4(r0.w, r1.w, r2.w, r3.w);
    vec4 p = vec4(center, 1.0);

    if (dot(c3 + c0, p) < -radius) return false;
    if (dot(c3 - c0, p) < -radius) return false;
    if (dot(c3 + c1, p) < -radius) return false;
    if (dot(c3 - c1, p) < -radius) return false;
    if (dot(c3 + c2, p) < -radius) return false;
    if (dot(c3 - c2, p) < -radius) return false;

    // L1.2: sub-texel casters. The lengths of the first two columns are this slot's world-to-NDC scale per axis, so radius
    // times the larger of them is the caster's NDC radius. Taking the larger is the conservative side: a caster is only
    // truly texel-sized when it shrinks below one on both axes. texelNdc is 0 when the test is off, and a radius is never
    // negative, so "off" is this comparison staying false rather than a separate branch to keep in sync across backends.
    float sx = length(vec3(r0.x, r1.x, r2.x));
    float sy = length(vec3(r0.y, r1.y, r2.y));
    if ((cullParams.texelSlots & (1u << slot)) != 0u && radius * max(sx, sy) < cullParams.texelNdc)
        return false;
    return true;
}

void main()
{
    uint i = gl_GlobalInvocationID.x;
    if (i >= cullParams.instanceCount)
        return;

    uint w = i * WORLD_STRIDE_F;
    vec3 r0 = vec3(worlds[w + 0u], worlds[w + 1u], worlds[w + 2u]);
    vec3 r1 = vec3(worlds[w + 4u], worlds[w + 5u], worlds[w + 6u]);
    vec3 r2 = vec3(worlds[w + 8u], worlds[w + 9u], worlds[w + 10u]);
    vec3 r3 = vec3(worlds[w + 12u], worlds[w + 13u], worlds[w + 14u]);

    vec3 ext = abs(cullParams.localExtents.xyz);
    vec3 center = cullParams.localCenter.x * r0 + cullParams.localCenter.y * r1
                + cullParams.localCenter.z * r2 + r3;
    vec3 bound = abs(r0) * ext.x + abs(r1) * ext.y + abs(r2) * ext.z;
    float radius = length(bound);

    for (uint slot = 0u; slot < cullParams.slotCount; ++slot)
    {
        if ((cullParams.slotMask & (1u << slot)) == 0u)
            continue;
        if (!visibleInSlot(slot, center, radius))
            continue;
        // A7b: the counter lives in the arg buffer's accumulator region, not in the arg table's InstanceCount -- see
        // InstanceCull.SlotCountsOffset for why the two cannot be the same word. The offset is in bytes, this array in uints.
        uint k = atomicAdd(drawArgs[(cullParams.slotCountsOffset >> 2u) + slot], 1u);
        visibleIds[slot * cullParams.capacity + k] = i;
    }
}
""";

    const string FinalizeGlsl = """
#version 450

layout(local_size_x = 1, local_size_y = 1, local_size_z = 1) in;

layout(push_constant) uniform FinalizeParams
{
    uint slotCount;
    uint slotCountsOffset;
    uint groupSize;
    uint pad;
} finalizeParams;

layout(std430, binding = 1) readonly buffer DrawArgs { uint drawArgs[]; };
layout(std430, binding = 2) buffer DispatchArgs { uint dispatchArgs[]; };

void main()
{
    uint maxCount = 0u;
    for (uint slot = 0u; slot < finalizeParams.slotCount; ++slot)
        maxCount = max(maxCount, drawArgs[(finalizeParams.slotCountsOffset >> 2u) + slot]);

    uint groups = (maxCount + finalizeParams.groupSize - 1u) / finalizeParams.groupSize;
    dispatchArgs[0] = groups;
    dispatchArgs[1] = finalizeParams.slotCount;
    dispatchArgs[2] = 1u;
}
""";

    const string GatherGlsl = """
#version 450

#define MAX_PRIMITIVES 16u
#define WORLD_STRIDE_F 20u
#define ARGS_STRIDE_U 5u

layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;

layout(push_constant) uniform GatherParams
{
    uint capacity;
    uint primCount;
    uint slotCount;
    uint tagMask;
    uint slotCountsOffset;
    uint clearSlotCounts;
    uint pad0;
    uint pad1;
} gatherParams;

layout(std430, binding = 1) readonly buffer VisibleIds { uint visibleIds[]; };
layout(std430, binding = 2) readonly buffer SourceWorlds { float sourceWorlds[]; };
layout(std430, binding = 3) buffer SortedWorlds { float sortedWorlds[]; };
layout(std430, binding = 4) buffer DrawArgs { uint drawArgs[]; };

void main()
{
    // Two-dimensional mapping: X = index within the slot, Y = slot. Fixed dispatch and indirect dispatch
    // share it, so the only variable in the A/B is the group count along the X dimension.
    uint slot = gl_GlobalInvocationID.y;
    uint k = gl_GlobalInvocationID.x;
    if (slot >= gatherParams.slotCount)
        return;

    uint argsBase = slot * MAX_PRIMITIVES * ARGS_STRIDE_U;
    // A7b: the count comes from the accumulator cull filled this frame; the arg table's own InstanceCount is written below,
    // so it only ever holds a finished frame's result for the draw to read.
    uint count = drawArgs[(gatherParams.slotCountsOffset >> 2u) + slot];

    if (k == 0u)
    {
        drawArgs[argsBase + 1u] = count;
        for (uint q = 1u; q < gatherParams.primCount; ++q)
            drawArgs[argsBase + q * ARGS_STRIDE_U + 1u] = count;
        // Only the last gather of a group clears: every stream reads the same counts, so clearing on the first
        // would make the rest publish zeroes into the arg table and the group would vanish for the frame.
        if (gatherParams.clearSlotCounts != 0u)
            drawArgs[(gatherParams.slotCountsOffset >> 2u) + slot] = 0u;
    }
    if (k >= count)
        return;

    uint vis = visibleIds[slot * gatherParams.capacity + k];
    uint src = vis * WORLD_STRIDE_F;
    uint dst = (slot * gatherParams.capacity + k) * WORLD_STRIDE_F;
    for (uint o = 0u; o < WORLD_STRIDE_F; ++o)
        sortedWorlds[dst + o] = sourceWorlds[src + o];
    // Original-index tag for every slot in the mask: index + 1 in M14 (float index 3), decoded by the vertex stage. See the HLSL source for why.
    if (((gatherParams.tagMask >> slot) & 1u) != 0u)
        sortedWorlds[dst + 3u] = float(vis + 1u);
}
""";
}
