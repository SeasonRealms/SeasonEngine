// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// P1: the WebGPU (WGSL) kernel source for GPU skinning.
///
/// Matrix convention is GLSL's: <c>mat4x4&lt;f32&gt;</c> is column-major and the bake stores C# rows, so a matrix read from a storage
/// buffer is the transpose of the C# one and every product is spelled in the opposite order. The palette therefore receives
/// <c>inverseMesh * world * inverseBind</c>, which already equals <c>transpose(IBM * world * inverseMesh)</c>.
///
/// Same verification gap as MSL: no offline WGSL validator here, and WebGPU additionally rejects any bind group whose layout does not
/// match the shader exactly. Both surface as the kernel failing to register, i.e. every model silently keeping the CPU route, so a
/// change to this string is only proven by a browser run.
/// </summary>
public sealed partial class InstanceSkinningEffect
{
    const string WgslSource = @"struct KernelParams
{
    jointCount: u32,
    instanceCount: u32,
    paletteStride: u32,
    pad0: u32,
    jointStride: u32,
    trackStride: u32,
    keyStride: u32,
    scratchStride: u32,
};

@group(0) @binding(0) var<uniform> pc: KernelParams;
@group(0) @binding(1) var<storage, read> jointTable: array<f32>;
@group(0) @binding(2) var<storage, read> trackTable: array<f32>;
@group(0) @binding(3) var<storage, read> trackIn: array<f32>;
@group(0) @binding(4) var<storage, read> instanceParams: array<f32>;
@group(0) @binding(5) var<storage, read_write> scratchOut: array<f32>;
@group(0) @binding(6) var<storage, read_write> paletteOut: array<f32>;

// Joint table offsets, in floats.
const JOINT_PALETTE: u32 = 0u;
const JOINT_PARENT: u32 = 1u;
const JOINT_REST_T: u32 = 4u;
const JOINT_REST_R: u32 = 8u;
const JOINT_REST_S: u32 = 12u;
const JOINT_CHAIN: u32 = 16u;
const JOINT_IBM: u32 = 32u;
const JOINT_INV_MESH: u32 = 48u;

fn LoadJointMat(o: u32) -> mat4x4<f32>
{
    return mat4x4<f32>(
        vec4<f32>(jointTable[o + 0u], jointTable[o + 1u], jointTable[o + 2u], jointTable[o + 3u]),
        vec4<f32>(jointTable[o + 4u], jointTable[o + 5u], jointTable[o + 6u], jointTable[o + 7u]),
        vec4<f32>(jointTable[o + 8u], jointTable[o + 9u], jointTable[o + 10u], jointTable[o + 11u]),
        vec4<f32>(jointTable[o + 12u], jointTable[o + 13u], jointTable[o + 14u], jointTable[o + 15u]));
}

fn LoadScratchMat(o: u32) -> mat4x4<f32>
{
    return mat4x4<f32>(
        vec4<f32>(scratchOut[o + 0u], scratchOut[o + 1u], scratchOut[o + 2u], scratchOut[o + 3u]),
        vec4<f32>(scratchOut[o + 4u], scratchOut[o + 5u], scratchOut[o + 6u], scratchOut[o + 7u]),
        vec4<f32>(scratchOut[o + 8u], scratchOut[o + 9u], scratchOut[o + 10u], scratchOut[o + 11u]),
        vec4<f32>(scratchOut[o + 12u], scratchOut[o + 13u], scratchOut[o + 14u], scratchOut[o + 15u]));
}

fn StoreScratch(o: u32, m: mat4x4<f32>)
{
    scratchOut[o + 0u] = m[0].x; scratchOut[o + 1u] = m[0].y; scratchOut[o + 2u] = m[0].z; scratchOut[o + 3u] = m[0].w;
    scratchOut[o + 4u] = m[1].x; scratchOut[o + 5u] = m[1].y; scratchOut[o + 6u] = m[1].z; scratchOut[o + 7u] = m[1].w;
    scratchOut[o + 8u] = m[2].x; scratchOut[o + 9u] = m[2].y; scratchOut[o + 10u] = m[2].z; scratchOut[o + 11u] = m[2].w;
    scratchOut[o + 12u] = m[3].x; scratchOut[o + 13u] = m[3].y; scratchOut[o + 14u] = m[3].z; scratchOut[o + 15u] = m[3].w;
}

fn StorePalette(o: u32, m: mat4x4<f32>)
{
    paletteOut[o + 0u] = m[0].x; paletteOut[o + 1u] = m[0].y; paletteOut[o + 2u] = m[0].z; paletteOut[o + 3u] = m[0].w;
    paletteOut[o + 4u] = m[1].x; paletteOut[o + 5u] = m[1].y; paletteOut[o + 6u] = m[1].z; paletteOut[o + 7u] = m[1].w;
    paletteOut[o + 8u] = m[2].x; paletteOut[o + 9u] = m[2].y; paletteOut[o + 10u] = m[2].z; paletteOut[o + 11u] = m[2].w;
    paletteOut[o + 12u] = m[3].x; paletteOut[o + 13u] = m[3].y; paletteOut[o + 14u] = m[3].z; paletteOut[o + 15u] = m[3].w;
}

// The transpose of S * R * T, so the caller can compose in column-major order and still land on the C# result.
fn LocalMatrixG(s: vec3<f32>, q: vec4<f32>, t: vec3<f32>) -> mat4x4<f32>
{
    let xx = q.x * q.x;
    let yy = q.y * q.y;
    let zz = q.z * q.z;
    let xy = q.x * q.y;
    let wz = q.z * q.w;
    let xz = q.x * q.z;
    let wy = q.y * q.w;
    let yz = q.y * q.z;
    let wx = q.x * q.w;
    let m00 = s.x * (1.0 - 2.0 * (yy + zz));
    let m01 = s.x * 2.0 * (xy + wz);
    let m02 = s.x * 2.0 * (xz - wy);
    let m10 = s.y * 2.0 * (xy - wz);
    let m11 = s.y * (1.0 - 2.0 * (xx + zz));
    let m12 = s.y * 2.0 * (yz + wx);
    let m20 = s.z * 2.0 * (xz + wy);
    let m21 = s.z * 2.0 * (yz - wx);
    let m22 = s.z * (1.0 - 2.0 * (xx + yy));
    return mat4x4<f32>(
        vec4<f32>(m00, m01, m02, 0.0),
        vec4<f32>(m10, m11, m12, 0.0),
        vec4<f32>(m20, m21, m22, 0.0),
        vec4<f32>(t, 1.0));
}

// Shortest-arc slerp, matching Quaternion.Slerp including its near-parallel shortcut.
fn QSlerp(a: vec4<f32>, b: vec4<f32>, t: f32) -> vec4<f32>
{
    var bb = b;
    var d = dot(a, bb);
    if (d < 0.0) { bb = -bb; d = -d; }
    if (d > 0.999999) { return normalize(mix(a, bb, t)); }
    let theta = acos(clamp(d, -1.0, 1.0));
    let inv = 1.0 / sin(theta);
    return sin((1.0 - t) * theta) * inv * a + sin(t * theta) * inv * bb;
}

@compute @workgroup_size(64, 1, 1)
fn CSMain(@builtin(global_invocation_id) tid: vec3<u32>)
{
    let inst = tid.x;
    if (inst >= pc.instanceCount) { return; }

    let pbase = inst * 4u;
    let clip = bitcast<u32>(instanceParams[pbase]);
    let time = instanceParams[pbase + 1u];

    let jointFloats = pc.jointStride / 4u;
    let trackFloats = pc.trackStride / 4u;
    let keyFloats = pc.keyStride / 4u;
    let scratchFloats = pc.scratchStride / 4u;
    let paletteFloats = 16u;

    let scratchBase = inst * pc.jointCount * scratchFloats;
    let paletteBase = inst * pc.paletteStride * paletteFloats;

    for (var j: u32 = 0u; j < pc.jointCount; j = j + 1u)
    {
        let jb = j * jointFloats;
        let paletteIndex = bitcast<u32>(jointTable[jb + JOINT_PALETTE]);

        var driven = false;
        var rt = vec4<f32>(0.0, 0.0, 0.0, 0.0);
        var rq = vec4<f32>(0.0, 0.0, 0.0, 1.0);
        var rs = vec4<f32>(0.0, 0.0, 0.0, 0.0);
        {
            let tb = (clip * pc.jointCount + j) * trackFloats;
            let frameCount = bitcast<u32>(trackTable[tb]);
            if (frameCount != 0u)
            {
                driven = true;
                let firstKey = bitcast<u32>(trackTable[tb + 1u]);
                let rate = trackTable[tb + 2u];
                let ft = time * rate;
                let f0 = min(u32(ft), frameCount - 1u);
                let f1 = min(f0 + 1u, frameCount - 1u);
                let a = ft - f32(f0);

                let i0 = (firstKey + f0 * pc.jointCount + j) * keyFloats;
                let i1 = (firstKey + f1 * pc.jointCount + j) * keyFloats;
                let t0 = vec3<f32>(trackIn[i0], trackIn[i0 + 1u], trackIn[i0 + 2u]);
                let q0 = vec4<f32>(trackIn[i0 + 4u], trackIn[i0 + 5u], trackIn[i0 + 6u], trackIn[i0 + 7u]);
                let s0 = vec3<f32>(trackIn[i0 + 8u], trackIn[i0 + 9u], trackIn[i0 + 10u]);
                let t1 = vec3<f32>(trackIn[i1], trackIn[i1 + 1u], trackIn[i1 + 2u]);
                let q1 = vec4<f32>(trackIn[i1 + 4u], trackIn[i1 + 5u], trackIn[i1 + 6u], trackIn[i1 + 7u]);
                let s1 = vec3<f32>(trackIn[i1 + 8u], trackIn[i1 + 9u], trackIn[i1 + 10u]);

                rt = vec4<f32>(mix(t0, t1, a), 0.0);
                rq = QSlerp(q0, q1, a);
                rs = vec4<f32>(mix(s0, s1, a), 0.0);
            }
        }

        if (!driven)
        {
            rt = vec4<f32>(jointTable[jb + JOINT_REST_T], jointTable[jb + JOINT_REST_T + 1u], jointTable[jb + JOINT_REST_T + 2u], 0.0);
            rq = vec4<f32>(jointTable[jb + JOINT_REST_R], jointTable[jb + JOINT_REST_R + 1u], jointTable[jb + JOINT_REST_R + 2u], jointTable[jb + JOINT_REST_R + 3u]);
            rs = vec4<f32>(jointTable[jb + JOINT_REST_S], jointTable[jb + JOINT_REST_S + 1u], jointTable[jb + JOINT_REST_S + 2u], 0.0);
        }

        let localG = LocalMatrixG(rs.xyz, rq, rt.xyz);

        let porder = bitcast<i32>(jointTable[jb + JOINT_PARENT]);
        let chainG = LoadJointMat(jb + JOINT_CHAIN);
        var parentG = chainG;
        if (porder >= 0)
        {
            parentG = LoadScratchMat(scratchBase + u32(porder) * scratchFloats) * chainG;
        }

        let worldG = parentG * localG;
        StoreScratch(scratchBase + j * scratchFloats, worldG);

        let ibmG = LoadJointMat(jb + JOINT_IBM);
        let invMeshG = LoadJointMat(jb + JOINT_INV_MESH);
        let boneG = invMeshG * worldG * ibmG;
        StorePalette(paletteBase + paletteIndex * paletteFloats, boneG);
    }
}
";
}
