// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

public sealed partial class InstanceCullEffect
{
    // A7's four kernels on the WebGPU side. WGSL uniform structs are 16-byte aligned, and CullParams is
    // exactly 64 bytes, bit-for-bit identical to the HLSL/GLSL/MSL sides; the dispatch workgroup count is
    // supplied by C# and must match @workgroup_size.

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

// clip = pos * VP (row-vector convention): clip.x is the dot product of the position with column 0, and column 3 is w.
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

    const string FinalizeWgsl = """
struct FinalizeParams
{
    slotCount: u32,
    slotStride: u32,
    groupSize: u32,
    pad: u32,
};

@group(0) @binding(0) var<uniform> finalizeParams: FinalizeParams;
@group(0) @binding(1) var<storage, read> drawArgs: array<u32>;
@group(0) @binding(2) var<storage, read_write> dispatchArgs: array<u32>;

// Single-threaded: the slot count is at most 5, and reading five uints serially is far cheaper than
// launching a workgroup and then synchronizing a reduction. It also avoids writing a workgroup-sync
// dialect for each of the four backends (WGSL has no cross-workgroup device-scope fence).
@compute @workgroup_size(1, 1, 1)
fn CSMain()
{
    var maxCount: u32 = 0u;
    for (var slot: u32 = 0u; slot < finalizeParams.slotCount; slot = slot + 1u)
    {
        maxCount = max(maxCount, drawArgs[(slot * finalizeParams.slotStride + 4u) / 4u]);
    }

    let groups: u32 = (maxCount + finalizeParams.groupSize - 1u) / finalizeParams.groupSize;
    dispatchArgs[0] = groups;
    dispatchArgs[1] = finalizeParams.slotCount;
    dispatchArgs[2] = 1u;
}
""";

    const string GatherWgsl = """
struct GatherParams
{
    capacity: u32,
    primCount: u32,
    slotCount: u32,
    tagMask: u32,
};

@group(0) @binding(0) var<uniform> gatherParams: GatherParams;
@group(0) @binding(1) var<storage, read> visibleIds: array<u32>;
@group(0) @binding(2) var<storage, read> sourceWorlds: array<f32>;
@group(0) @binding(3) var<storage, read_write> sortedWorlds: array<f32>;
@group(0) @binding(4) var<storage, read_write> drawArgs: array<u32>;

@compute @workgroup_size(64, 1, 1)
fn CSMain(@builtin(global_invocation_id) id: vec3<u32>)
{
    // Two-dimensional mapping: X = index within the slot, Y = slot. Fixed dispatch and indirect dispatch
    // share it, so the only variable in the A/B is the group count along the X dimension.
    let slot: u32 = id.y;
    let k: u32 = id.x;
    if (slot >= gatherParams.slotCount) { return; }

    let argsBase: u32 = slot * 16u * 5u;
    let count: u32 = drawArgs[argsBase + 1u];

    // Fan-out: the total visible count of a slot must be written into the instance count of every primitive in that slot.
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
    // Original-index tag for every slot in the mask: index + 1 in M14 (f32 index 3), decoded by the vertex stage. See the HLSL source for why.
    if (((gatherParams.tagMask >> slot) & 1u) != 0u)
    {
        sortedWorlds[dst + 3u] = f32(vis + 1u);
    }
}
""";
}
