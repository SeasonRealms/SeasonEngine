// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

public sealed partial class InstanceCullEffect
{
    // A7 的 D3D12 侧两份内核。两者的 [numthreads] 必须等于 C# 里的 InstanceCull.ResetThreads(64)
    // 与 InstanceCull.CullGroupSize(64)，而 MAX_PRIMITIVES 必须等于 InstanceCull.MaxPrimitives(16)：
    // 这三组数字是"定长寻址"的全部依据，任何一处漂移都表现为参数表错位而不是编译错误。

    const string ResetHlsl = """
// A7 reset：把每 (槽位, primitive) 条间接参数写成定值，实例数归零。
// mode 0 填参数，mode 1 把槽位内 primitive 0 的实例数扇出到其余 primitive。
// 分两次派发而不是一次做完，是因为扇出必须等剔除内核把计数累加完。

#define MAX_PRIMITIVES 16
#define ARGS_STRIDE 20

struct ResetParams
{
    uint primCount;
    uint slotCount;
    uint mode;
    uint pad;
};

ConstantBuffer<ResetParams> resetParams : register(b0);
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
// A7 逐实例剔除：一个线程一个实例，算出世界包围球后逐槽位做六个裁剪平面的球测试。
// 通过的实例用原始下标写进该槽位的序号表，并把该槽位的实例计数加一。
//
// 球与矩阵的约定跟着引擎走：世界矩阵是 System.Numerics 的行序（M11..M44 逐行存放），
// 点变换是行向量右乘，所以"某点的世界位置 = 各行的加权和再加平移行"。
// 半径用世界 AABB 的外接球上界，取 |Row0|*ex + |Row1|*ey + |Row2|*ez 的模长：
// 它只会偏大不会偏小，偏大的后果是多画几个实例，偏小会把可见实例剔掉。

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

ConstantBuffer<CullParams> cullParams : register(b0);
ByteAddressBuffer slotMatrices : register(t0);
ByteAddressBuffer instanceWorlds : register(t1);
RWByteAddressBuffer drawArgs : register(u0);
RWByteAddressBuffer visibleIds : register(u1);

// clip = pos * VP（行向量约定），所以 clip.x 是位置与"第 0 列"的点积，第 3 列就是 w。
// 于是六个平面直接由行列组合得出，不必先转置。
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

        // 每个槽位独立压缩：一个实例可以在多个级联里都可见，序号表按槽位各占一份。
        uint k;
        drawArgs.InterlockedAdd(slot * MAX_PRIMITIVES * ARGS_STRIDE + 4, 1u, k);
        visibleIds.Store((slot * p.capacity + k) * 4, i);
    }
}
""";

    const string GatherHlsl = """
// A7 gather：第二阶段。剔除只产出"原始序号表 + 每槽位计数"，本内核按序号把实例数据物化到
// 槽位分段里，绘制端就把这份流当实例缓冲用，实例数由间接参数给出。
//
// 这一阶段是必要的，不是画蛇添足：IA 的实例步进是线性的，无法按序号索引，
// 而顶点阶段自己查表需要两个新的 SRV —— D3D12 根签名只剩 3 DWORD，给不起。
// 代价是每帧多写"可见数"份 80 字节，且全在 GPU 内完成，不产生任何 CPU 上传或回读。

#define MAX_PRIMITIVES 16
#define WORLD_STRIDE 80
#define ARGS_STRIDE 20

struct GatherParams
{
    uint capacity;
    uint primCount;
    uint slotCount;
    uint pad;
};

ConstantBuffer<GatherParams> gatherParams : register(b0);
ByteAddressBuffer visibleIds : register(t0);
ByteAddressBuffer sourceWorlds : register(t1);
RWByteAddressBuffer sortedWorlds : register(u0);
RWByteAddressBuffer drawArgs : register(u1);

[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    GatherParams p = gatherParams;
    uint idx = id.x;
    uint slot = idx / p.capacity;
    uint k = idx % p.capacity;
    if (slot >= p.slotCount)
        return;

    uint argsBase = slot * MAX_PRIMITIVES * ARGS_STRIDE;
    uint count = drawArgs.Load(argsBase + 4);

    // 扇出：primitive 0 的实例数就是本槽位的可见总数，其余 primitive 逐字复制。
    // 每个 primitive 的索引区间不同，所以参数表必须逐条存在，不能共用一份。
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
}
""";
}
