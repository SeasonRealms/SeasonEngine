// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// P1: the Metal (MSL) kernel source for GPU skinning.
///
/// Matrix convention is GLSL's, not HLSL's: <c>float4x4</c> is column-major and the bake stores C# rows, so a matrix read from the
/// buffer is the transpose of the C# one and every product is spelled in the opposite order. The palette therefore receives
/// <c>inverseMesh * world * inverseBind</c>, which already equals <c>transpose(IBM * world * inverseMesh)</c>.
///
/// No offline MSL validator exists in this repository, so a mistake in this string is only caught when the Metal backend compiles it
/// at runtime, and a failed compile silently drops every model back to the CPU route. Any change here needs a device run.
/// </summary>
public sealed partial class InstanceSkinningEffect
{
    const string MslSource = @"#include <metal_stdlib>
using namespace metal;

struct KernelParams
{
    uint jointCount;
    uint instanceCount;
    uint paletteStride;
    uint pad0;
    uint jointStride;
    uint trackStride;
    uint keyStride;
    uint scratchStride;
};

// Joint table offsets, in floats.
constant uint JOINT_PALETTE  = 0u;
constant uint JOINT_PARENT   = 1u;
constant uint JOINT_REST_T   = 4u;
constant uint JOINT_REST_R   = 8u;
constant uint JOINT_REST_S   = 12u;
constant uint JOINT_CHAIN    = 16u;
constant uint JOINT_IBM      = 32u;
constant uint JOINT_INV_MESH = 48u;

static float4x4 LoadJointMat(const device float* f, uint o)
{
    return float4x4(
        float4(f[o +  0u], f[o +  1u], f[o +  2u], f[o +  3u]),
        float4(f[o +  4u], f[o +  5u], f[o +  6u], f[o +  7u]),
        float4(f[o +  8u], f[o +  9u], f[o + 10u], f[o + 11u]),
        float4(f[o + 12u], f[o + 13u], f[o + 14u], f[o + 15u]));
}

static float4x4 LoadScratchMat(const device float* f, uint o)
{
    return float4x4(
        float4(f[o +  0u], f[o +  1u], f[o +  2u], f[o +  3u]),
        float4(f[o +  4u], f[o +  5u], f[o +  6u], f[o +  7u]),
        float4(f[o +  8u], f[o +  9u], f[o + 10u], f[o + 11u]),
        float4(f[o + 12u], f[o + 13u], f[o + 14u], f[o + 15u]));
}

static void StoreMat(device float* f, uint o, float4x4 m)
{
    f[o +  0u] = m[0].x; f[o +  1u] = m[0].y; f[o +  2u] = m[0].z; f[o +  3u] = m[0].w;
    f[o +  4u] = m[1].x; f[o +  5u] = m[1].y; f[o +  6u] = m[1].z; f[o +  7u] = m[1].w;
    f[o +  8u] = m[2].x; f[o +  9u] = m[2].y; f[o + 10u] = m[2].z; f[o + 11u] = m[2].w;
    f[o + 12u] = m[3].x; f[o + 13u] = m[3].y; f[o + 14u] = m[3].z; f[o + 15u] = m[3].w;
}

// The transpose of S * R * T, so the caller can compose in column-major order and still land on the C# result.
static float4x4 LocalMatrixG(float3 s, float4 q, float3 t)
{
    float xx = q.x * q.x, yy = q.y * q.y, zz = q.z * q.z;
    float xy = q.x * q.y, wz = q.z * q.w, xz = q.x * q.z, wy = q.y * q.w;
    float yz = q.y * q.z, wx = q.x * q.w;
    float m00 = s.x * (1.0 - 2.0 * (yy + zz));
    float m01 = s.x * 2.0 * (xy + wz);
    float m02 = s.x * 2.0 * (xz - wy);
    float m10 = s.y * 2.0 * (xy - wz);
    float m11 = s.y * (1.0 - 2.0 * (xx + zz));
    float m12 = s.y * 2.0 * (yz + wx);
    float m20 = s.z * 2.0 * (xz + wy);
    float m21 = s.z * 2.0 * (yz - wx);
    float m22 = s.z * (1.0 - 2.0 * (xx + yy));
    return float4x4(
        float4(m00, m01, m02, 0.0),
        float4(m10, m11, m12, 0.0),
        float4(m20, m21, m22, 0.0),
        float4(t, 1.0));
}

// Shortest-arc slerp, matching Quaternion.Slerp including its near-parallel shortcut.
static float4 QSlerp(float4 a, float4 b, float t)
{
    float d = dot(a, b);
    if (d < 0.0) { b = -b; d = -d; }
    if (d > 0.999999) return normalize(mix(a, b, t));
    float theta = acos(clamp(d, -1.0, 1.0));
    float inv = 1.0 / sin(theta);
    return sin((1.0 - t) * theta) * inv * a + sin(t * theta) * inv * b;
}

kernel void CSMain(
    constant KernelParams& pc [[buffer(0)]],
    const device float* jointF [[buffer(1)]],
    const device float* trackF [[buffer(2)]],
    const device float* trackInF [[buffer(3)]],
    const device float* paramF [[buffer(4)]],
    device float* scratchF [[buffer(5)]],
    device float* paletteF [[buffer(6)]],
    uint3 tid [[thread_position_in_grid]])
{
    uint inst = tid.x;
    if (inst >= pc.instanceCount) return;

    uint pbase = inst * 4u;
    uint clip = as_type<uint>(paramF[pbase]);
    float time = paramF[pbase + 1u];

    uint jointFloats = pc.jointStride / 4u;
    uint trackFloats = pc.trackStride / 4u;
    uint keyFloats = pc.keyStride / 4u;
    uint scratchFloats = pc.scratchStride / 4u;
    uint paletteFloats = 16u;

    uint scratchBase = inst * pc.jointCount * scratchFloats;
    uint paletteBase = inst * pc.paletteStride * paletteFloats;

    for (uint j = 0u; j < pc.jointCount; j++)
    {
        uint jb = j * jointFloats;
        uint paletteIndex = as_type<uint>(jointF[jb + JOINT_PALETTE]);

        bool driven = false;
        float4 rt = float4(0.0, 0.0, 0.0, 0.0);
        float4 rq = float4(0.0, 0.0, 0.0, 1.0);
        float4 rs = float4(0.0, 0.0, 0.0, 0.0);
        {
            uint tb = (clip * pc.jointCount + j) * trackFloats;
            uint frameCount = as_type<uint>(trackF[tb]);
            if (frameCount != 0u)
            {
                driven = true;
                uint firstKey = as_type<uint>(trackF[tb + 1u]);
                float rate = trackF[tb + 2u];
                float ft = time * rate;
                uint f0 = min(uint(ft), frameCount - 1u);
                uint f1 = min(f0 + 1u, frameCount - 1u);
                float a = ft - float(f0);

                uint i0 = (firstKey + f0 * pc.jointCount + j) * keyFloats;
                uint i1 = (firstKey + f1 * pc.jointCount + j) * keyFloats;
                float4 t0 = float4(trackInF[i0], trackInF[i0 + 1u], trackInF[i0 + 2u], 0.0);
                float4 q0 = float4(trackInF[i0 + 4u], trackInF[i0 + 5u], trackInF[i0 + 6u], trackInF[i0 + 7u]);
                float4 s0 = float4(trackInF[i0 + 8u], trackInF[i0 + 9u], trackInF[i0 + 10u], 0.0);
                float4 t1 = float4(trackInF[i1], trackInF[i1 + 1u], trackInF[i1 + 2u], 0.0);
                float4 q1 = float4(trackInF[i1 + 4u], trackInF[i1 + 5u], trackInF[i1 + 6u], trackInF[i1 + 7u]);
                float4 s1 = float4(trackInF[i1 + 8u], trackInF[i1 + 9u], trackInF[i1 + 10u], 0.0);

                rt = float4(mix(t0.xyz, t1.xyz, a), 0.0);
                rq = QSlerp(q0, q1, a);
                rs = float4(mix(s0.xyz, s1.xyz, a), 0.0);
            }
        }

        if (!driven)
        {
            rt = float4(jointF[jb + JOINT_REST_T], jointF[jb + JOINT_REST_T + 1u], jointF[jb + JOINT_REST_T + 2u], 0.0);
            rq = float4(jointF[jb + JOINT_REST_R], jointF[jb + JOINT_REST_R + 1u], jointF[jb + JOINT_REST_R + 2u], jointF[jb + JOINT_REST_R + 3u]);
            rs = float4(jointF[jb + JOINT_REST_S], jointF[jb + JOINT_REST_S + 1u], jointF[jb + JOINT_REST_S + 2u], 0.0);
        }

        float4x4 localG = LocalMatrixG(rs.xyz, rq, rt.xyz);

        int porder = as_type<int>(jointF[jb + JOINT_PARENT]);
        float4x4 chainG = LoadJointMat(jointF, jb + JOINT_CHAIN);
        float4x4 parentG;
        if (porder >= 0)
            parentG = LoadScratchMat(scratchF, scratchBase + uint(porder) * scratchFloats) * chainG;
        else
            parentG = chainG;

        float4x4 worldG = parentG * localG;
        StoreMat(scratchF, scratchBase + j * scratchFloats, worldG);

        float4x4 ibmG = LoadJointMat(jointF, jb + JOINT_IBM);
        float4x4 invMeshG = LoadJointMat(jointF, jb + JOINT_INV_MESH);
        float4x4 boneG = invMeshG * worldG * ibmG;
        StoreMat(paletteF, paletteBase + paletteIndex * paletteFloats, boneG);
    }
}
";
}
