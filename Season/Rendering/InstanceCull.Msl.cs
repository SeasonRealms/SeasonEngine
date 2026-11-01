// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

public sealed partial class InstanceCullEffect
{
    // A7 的 Metal 侧两份内核。MSL 没有编译期的组大小声明，64 由 DispatchThreadgroups 的第二个参数给出，
    // 所以这里的 C# 常量是唯一的来源；buffer 槽位按"Params 为 0，其余 buffer 从 1 起"排布。

    const string ResetMsl = """
#include <metal_stdlib>
using namespace metal;

#define MAX_PRIMITIVES 16u
#define ARGS_STRIDE_U 5u

struct ResetParams
{
    uint primCount;
    uint slotCount;
    uint mode;
    uint pad;
};

kernel void CSMain(uint3 id [[thread_position_in_grid]],
                   constant ResetParams& resetParams [[buffer(0)]],
                   const device uint* primInfo [[buffer(1)]],
                   device uint* drawArgs [[buffer(2)]])
{
    uint idx = id.x;
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

    const string CullMsl = """
#include <metal_stdlib>
using namespace metal;

#define MAX_PRIMITIVES 16u
#define WORLD_STRIDE_F 20u
#define ARGS_STRIDE_U 5u

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

// clip = pos * VP（行向量约定）：clip.x 是位置与第 0 列的点积，第 3 列就是 w。
static bool visible_in_slot(const device float* slotMatrices, uint slot, float3 center, float radius)
{
    uint m = slot * 16u;
    float4 r0 = float4(slotMatrices[m + 0u], slotMatrices[m + 1u], slotMatrices[m + 2u], slotMatrices[m + 3u]);
    float4 r1 = float4(slotMatrices[m + 4u], slotMatrices[m + 5u], slotMatrices[m + 6u], slotMatrices[m + 7u]);
    float4 r2 = float4(slotMatrices[m + 8u], slotMatrices[m + 9u], slotMatrices[m + 10u], slotMatrices[m + 11u]);
    float4 r3 = float4(slotMatrices[m + 12u], slotMatrices[m + 13u], slotMatrices[m + 14u], slotMatrices[m + 15u]);

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

kernel void CSMain(uint3 id [[thread_position_in_grid]],
                   constant CullParams& cullParams [[buffer(0)]],
                   const device float* slotMatrices [[buffer(1)]],
                   const device float* worlds [[buffer(2)]],
                   device atomic_uint* drawArgs [[buffer(3)]],
                   device uint* visibleIds [[buffer(4)]])
{
    uint i = id.x;
    if (i >= cullParams.instanceCount)
        return;

    uint w = i * WORLD_STRIDE_F;
    float3 r0 = float3(worlds[w + 0u], worlds[w + 1u], worlds[w + 2u]);
    float3 r1 = float3(worlds[w + 4u], worlds[w + 5u], worlds[w + 6u]);
    float3 r2 = float3(worlds[w + 8u], worlds[w + 9u], worlds[w + 10u]);
    float3 r3 = float3(worlds[w + 12u], worlds[w + 13u], worlds[w + 14u]);

    float3 ext = abs(cullParams.localExtents.xyz);
    float3 center = cullParams.localCenter.x * r0 + cullParams.localCenter.y * r1
                  + cullParams.localCenter.z * r2 + r3;
    float3 bound = abs(r0) * ext.x + abs(r1) * ext.y + abs(r2) * ext.z;
    float radius = length(bound);

    for (uint slot = 0u; slot < cullParams.slotCount; ++slot)
    {
        if ((cullParams.slotMask & (1u << slot)) == 0u)
            continue;
        if (!visible_in_slot(slotMatrices, slot, center, radius))
            continue;
        uint k = atomic_fetch_add_explicit(&drawArgs[slot * MAX_PRIMITIVES * ARGS_STRIDE_U + 1u], 1u,
                                          memory_order_relaxed);
        visibleIds[slot * cullParams.capacity + k] = i;
    }
}
""";

    const string GatherMsl = """
#include <metal_stdlib>
using namespace metal;

#define MAX_PRIMITIVES 16u
#define WORLD_STRIDE_F 20u
#define ARGS_STRIDE_U 5u

struct GatherParams
{
    uint capacity;
    uint primCount;
    uint slotCount;
    uint pad;
};

kernel void CSMain(uint3 id [[thread_position_in_grid]],
                   constant GatherParams& gatherParams [[buffer(0)]],
                   const device uint* visibleIds [[buffer(1)]],
                   const device float* sourceWorlds [[buffer(2)]],
                   device float* sortedWorlds [[buffer(3)]],
                   device uint* drawArgs [[buffer(4)]])
{
    uint idx = id.x;
    uint slot = idx / gatherParams.capacity;
    uint k = idx % gatherParams.capacity;
    if (slot >= gatherParams.slotCount)
        return;

    uint argsBase = slot * MAX_PRIMITIVES * ARGS_STRIDE_U;
    uint count = drawArgs[argsBase + 1u];

    // 扇出：一个槽位的可见总数要写进该槽位每一条 primitive 的实例数。
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
