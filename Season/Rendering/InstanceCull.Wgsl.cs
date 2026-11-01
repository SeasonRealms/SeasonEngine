// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

public sealed partial class InstanceCullEffect
{
    // A7 的 WebGPU 侧两份内核。WGSL 的 uniform 结构按 16 字节对齐，CullParams 正好 64 字节，
    // 与 HLSL/GLSL/MSL 三端逐位一致；dispatch 的 workgroup 数由 C# 给出，与 @workgroup_size 必须相符。

    const string ResetWgsl = """
struct ResetParams
{
    primCount: u32,
    slotCount: u32,
    mode: u32,
    pad: u32,
};

@group(0) @binding(0) var<uniform> resetParams: ResetParams;
@group(0) @binding(1) var<storage, read> primInfo: array<u32>;
@group(0) @binding(2) var<storage, read_write> drawArgs: array<u32>;

@compute @workgroup_size(64, 1, 1)
fn CSMain(@builtin(global_invocation_id) id: vec3<u32>)
{
    let idx: u32 = id.x;
    let slot: u32 = idx / 16u;
    let prim: u32 = idx % 16u;
    if (slot >= resetParams.slotCount || prim >= resetParams.primCount) { return; }

    let base: u32 = idx * 5u;
    let info: u32 = prim * 4u;
    drawArgs[base + 0u] = primInfo[info + 0u];
    drawArgs[base + 1u] = 0u;
    drawArgs[base + 2u] = primInfo[info + 1u];
    drawArgs[base + 3u] = primInfo[info + 2u];
    drawArgs[base + 4u] = 0u;
}
""";

    const string CullWgsl = """
struct CullParams
{
    localCenter: vec4<f32>,
    localExtents: vec4<f32>,
    instanceCount: u32,
    slotCount: u32,
    primCount: u32,
    capacity: u32,
    slotMask: u32,
    pad0: u32,
    pad1: u32,
    pad2: u32,
};

@group(0) @binding(0) var<uniform> cullParams: CullParams;
@group(0) @binding(1) var<storage, read> slotMatrices: array<f32>;
@group(0) @binding(2) var<storage, read> worlds: array<f32>;
@group(0) @binding(3) var<storage, read_write> drawArgs: array<u32>;
@group(0) @binding(4) var<storage, read_write> visibleIds: array<u32>;

// clip = pos * VP（行向量约定）：clip.x 是位置与第 0 列的点积，第 3 列就是 w。
fn visibleInSlot(slot: u32, center: vec3<f32>, radius: f32) -> bool
{
    let m: u32 = slot * 16u;
    let r0 = vec4<f32>(slotMatrices[m + 0u], slotMatrices[m + 1u], slotMatrices[m + 2u], slotMatrices[m + 3u]);
    let r1 = vec4<f32>(slotMatrices[m + 4u], slotMatrices[m + 5u], slotMatrices[m + 6u], slotMatrices[m + 7u]);
    let r2 = vec4<f32>(slotMatrices[m + 8u], slotMatrices[m + 9u], slotMatrices[m + 10u], slotMatrices[m + 11u]);
    let r3 = vec4<f32>(slotMatrices[m + 12u], slotMatrices[m + 13u], slotMatrices[m + 14u], slotMatrices[m + 15u]);

    let c0 = vec4<f32>(r0.x, r1.x, r2.x, r3.x);
    let c1 = vec4<f32>(r0.y, r1.y, r2.y, r3.y);
    let c2 = vec4<f32>(r0.z, r1.z, r2.z, r3.z);
    let c3 = vec4<f32>(r0.w, r1.w, r2.w, r3.w);
    let p = vec4<f32>(center, 1.0);

    if (dot(c3 + c0, p) < -radius) { return false; }
    if (dot(c3 - c0, p) < -radius) { return false; }
    if (dot(c3 + c1, p) < -radius) { return false; }
    if (dot(c3 - c1, p) < -radius) { return false; }
    if (dot(c3 + c2, p) < -radius) { return false; }
    if (dot(c3 - c2, p) < -radius) { return false; }
    return true;
}

@compute @workgroup_size(64, 1, 1)
fn CSMain(@builtin(global_invocation_id) id: vec3<u32>)
{
    let i: u32 = id.x;
    if (i >= cullParams.instanceCount) { return; }

    let w: u32 = i * 20u;
    let r0 = vec3<f32>(worlds[w + 0u], worlds[w + 1u], worlds[w + 2u]);
    let r1 = vec3<f32>(worlds[w + 4u], worlds[w + 5u], worlds[w + 6u]);
    let r2 = vec3<f32>(worlds[w + 8u], worlds[w + 9u], worlds[w + 10u]);
    let r3 = vec3<f32>(worlds[w + 12u], worlds[w + 13u], worlds[w + 14u]);

    let ext = abs(cullParams.localExtents.xyz);
    let center = cullParams.localCenter.x * r0 + cullParams.localCenter.y * r1
               + cullParams.localCenter.z * r2 + r3;
    let bound = abs(r0) * ext.x + abs(r1) * ext.y + abs(r2) * ext.z;
    let radius = length(bound);

    for (var slot: u32 = 0u; slot < cullParams.slotCount; slot = slot + 1u)
    {
        if ((cullParams.slotMask & (1u << slot)) == 0u) { continue; }
        if (!visibleInSlot(slot, center, radius)) { continue; }
        let idx: u32 = slot * 16u * 5u + 1u;
        let k: u32 = atomicAdd(&drawArgs[idx], 1u);
        visibleIds[slot * cullParams.capacity + k] = i;
    }
}
""";

    const string GatherWgsl = """
struct GatherParams
{
    capacity: u32,
    primCount: u32,
    slotCount: u32,
    pad: u32,
};

@group(0) @binding(0) var<uniform> gatherParams: GatherParams;
@group(0) @binding(1) var<storage, read> visibleIds: array<u32>;
@group(0) @binding(2) var<storage, read> sourceWorlds: array<f32>;
@group(0) @binding(3) var<storage, read_write> sortedWorlds: array<f32>;
@group(0) @binding(4) var<storage, read_write> drawArgs: array<u32>;

@compute @workgroup_size(64, 1, 1)
fn CSMain(@builtin(global_invocation_id) id: vec3<u32>)
{
    let idx: u32 = id.x;
    let slot: u32 = idx / gatherParams.capacity;
    let k: u32 = idx % gatherParams.capacity;
    if (slot >= gatherParams.slotCount) { return; }

    let argsBase: u32 = slot * 16u * 5u;
    let count: u32 = drawArgs[argsBase + 1u];

    // 扇出：一个槽位的可见总数要写进该槽位每一条 primitive 的实例数。
    if (k == 0u)
    {
        for (var q: u32 = 1u; q < gatherParams.primCount; q = q + 1u)
        {
            drawArgs[argsBase + q * 5u + 1u] = count;
        }
    }
    if (k >= count) { return; }

    let vis: u32 = visibleIds[slot * gatherParams.capacity + k];
    let src: u32 = vis * 20u;
    let dst: u32 = (slot * gatherParams.capacity + k) * 20u;
    for (var o: u32 = 0u; o < 20u; o = o + 1u)
    {
        sortedWorlds[dst + o] = sourceWorlds[src + o];
    }
}
""";
}
