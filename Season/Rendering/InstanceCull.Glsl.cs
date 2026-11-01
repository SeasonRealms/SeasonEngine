// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

public sealed partial class InstanceCullEffect
{
    // A7 的 Vulkan 侧两份内核。Params 走 push constant，其余资源按"声明索引即 binding 编号"落位，
    // 于是 binding 0 留给 Params 占位而不会被任何 buffer 占用，与 ComputeBindingType 的契约一致。

    const string ResetGlsl = """
#version 450

#define MAX_PRIMITIVES 16u
#define ARGS_STRIDE_U 5u

layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;

layout(push_constant) uniform ResetParams
{
    uint primCount;
    uint slotCount;
    uint mode;
    uint pad;
} resetParams;

layout(std430, binding = 1) readonly buffer PrimInfo { uint primInfo[]; };
layout(std430, binding = 2) buffer DrawArgs { uint drawArgs[]; };

void main()
{
    uint idx = gl_GlobalInvocationID.x;
    uint slot = idx / MAX_PRIMITIVES;
    uint prim = idx % MAX_PRIMITIVES;
    if (slot >= resetParams.slotCount || prim >= resetParams.primCount)
        return;

    uint base = idx * ARGS_STRIDE_U;
    uint info = prim * 4u;
    drawArgs[base + 0u] = primInfo[info + 0u];
    drawArgs[base + 1u] = 0u;
    drawArgs[base + 2u] = primInfo[info + 1u];
    drawArgs[base + 3u] = primInfo[info + 2u];
    drawArgs[base + 4u] = 0u;
}
""";

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
    uint pad0;
    uint pad1;
    uint pad2;
} cullParams;

layout(std430, binding = 1) readonly buffer SlotMatrices { float slotMatrices[]; };
layout(std430, binding = 2) readonly buffer Worlds { float worlds[]; };
layout(std430, binding = 3) buffer DrawArgs { uint drawArgs[]; };
layout(std430, binding = 4) buffer VisibleIds { uint visibleIds[]; };

// clip = pos * VP（行向量约定）：clip.x 是位置与第 0 列的点积，第 3 列就是 w，
// 六个裁剪平面因此直接由行列组合得出。
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
        uint k = atomicAdd(drawArgs[slot * MAX_PRIMITIVES * ARGS_STRIDE_U + 1u], 1u);
        visibleIds[slot * cullParams.capacity + k] = i;
    }
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
    uint pad;
} gatherParams;

layout(std430, binding = 1) readonly buffer VisibleIds { uint visibleIds[]; };
layout(std430, binding = 2) readonly buffer SourceWorlds { float sourceWorlds[]; };
layout(std430, binding = 3) buffer SortedWorlds { float sortedWorlds[]; };
layout(std430, binding = 4) buffer DrawArgs { uint drawArgs[]; };

void main()
{
    uint idx = gl_GlobalInvocationID.x;
    uint slot = idx / gatherParams.capacity;
    uint k = idx % gatherParams.capacity;
    if (slot >= gatherParams.slotCount)
        return;

    uint argsBase = slot * MAX_PRIMITIVES * ARGS_STRIDE_U;
    uint count = drawArgs[argsBase + 1u];

    if (k == 0u)
    {
        for (uint q = 1u; q < gatherParams.primCount; ++q)
            drawArgs[argsBase + q * ARGS_STRIDE_U + 1u] = count;
    }
    if (k >= count)
        return;

    uint vis = visibleIds[slot * gatherParams.capacity + k];
    uint src = vis * WORLD_STRIDE_F;
    uint dst = (slot * gatherParams.capacity + k) * WORLD_STRIDE_F;
    for (uint o = 0u; o < WORLD_STRIDE_F; ++o)
        sortedWorlds[dst + o] = sourceWorlds[src + o];
}
""";
}
