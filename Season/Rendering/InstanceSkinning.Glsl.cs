// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// P1: the Vulkan (GLSL 450) kernel source for GPU skinning.
///
/// Matrix convention. GLSL's <c>mat4</c> is column-major and the bake writes C# matrices row-major, so a matrix read straight out of
/// the buffer is the <em>transpose</em> of the C# one, and every product has to be spelled in the opposite order:
/// <c>local = T * R * S</c>, <c>world = parent * local</c>, <c>bone = inverseMesh * world * inverseBind</c>. The result is already
/// <c>transpose(IBM * world * inverseMesh)</c>, which is what the palette holds, so it is written without a further transpose.
/// </summary>
public sealed partial class InstanceSkinningEffect
{
    const string GlslSource = @"#version 450

layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;

layout(push_constant) uniform KernelParams
{
    uint jointCount;
    uint instanceCount;
    uint paletteStride;
    uint pad0;
    uint jointStride;
    uint trackStride;
    uint keyStride;
    uint scratchStride;
} pc;

layout(std430, binding = 1) readonly buffer JointTable { float jointF[]; };
layout(std430, binding = 2) readonly buffer TrackTable { float trackF[]; };
layout(std430, binding = 3) readonly buffer Tracks     { float trackInF[]; };
layout(std430, binding = 4) readonly buffer Params     { float paramF[]; };
layout(std430, binding = 5) buffer Scratch  { float scratchF[]; };
layout(std430, binding = 6) buffer Palette  { float paletteF[]; };

// Joint table offsets, in floats. Kept in step with InstanceSkinning's byte offsets by dividing by four at the use site.
const uint JOINT_PALETTE  = 0u;
const uint JOINT_PARENT   = 1u;
const uint JOINT_REST_T   = 4u;
const uint JOINT_REST_R   = 8u;
const uint JOINT_REST_S   = 12u;
const uint JOINT_CHAIN    = 16u;
const uint JOINT_IBM      = 32u;
const uint JOINT_INV_MESH = 48u;

mat4 LoadJointMat(uint f)
{
    return mat4(
        vec4(jointF[f +  0u], jointF[f +  1u], jointF[f +  2u], jointF[f +  3u]),
        vec4(jointF[f +  4u], jointF[f +  5u], jointF[f +  6u], jointF[f +  7u]),
        vec4(jointF[f +  8u], jointF[f +  9u], jointF[f + 10u], jointF[f + 11u]),
        vec4(jointF[f + 12u], jointF[f + 13u], jointF[f + 14u], jointF[f + 15u]));
}

mat4 LoadScratchMat(uint f)
{
    return mat4(
        vec4(scratchF[f +  0u], scratchF[f +  1u], scratchF[f +  2u], scratchF[f +  3u]),
        vec4(scratchF[f +  4u], scratchF[f +  5u], scratchF[f +  6u], scratchF[f +  7u]),
        vec4(scratchF[f +  8u], scratchF[f +  9u], scratchF[f + 10u], scratchF[f + 11u]),
        vec4(scratchF[f + 12u], scratchF[f + 13u], scratchF[f + 14u], scratchF[f + 15u]));
}

void StoreScratch(uint f, mat4 m)
{
    scratchF[f +  0u] = m[0].x; scratchF[f +  1u] = m[0].y; scratchF[f +  2u] = m[0].z; scratchF[f +  3u] = m[0].w;
    scratchF[f +  4u] = m[1].x; scratchF[f +  5u] = m[1].y; scratchF[f +  6u] = m[1].z; scratchF[f +  7u] = m[1].w;
    scratchF[f +  8u] = m[2].x; scratchF[f +  9u] = m[2].y; scratchF[f + 10u] = m[2].z; scratchF[f + 11u] = m[2].w;
    scratchF[f + 12u] = m[3].x; scratchF[f + 13u] = m[3].y; scratchF[f + 14u] = m[3].z; scratchF[f + 15u] = m[3].w;
}

void StorePalette(uint f, mat4 m)
{
    paletteF[f +  0u] = m[0].x; paletteF[f +  1u] = m[0].y; paletteF[f +  2u] = m[0].z; paletteF[f +  3u] = m[0].w;
    paletteF[f +  4u] = m[1].x; paletteF[f +  5u] = m[1].y; paletteF[f +  6u] = m[1].z; paletteF[f +  7u] = m[1].w;
    paletteF[f +  8u] = m[2].x; paletteF[f +  9u] = m[2].y; paletteF[f + 10u] = m[2].z; paletteF[f + 11u] = m[2].w;
    paletteF[f + 12u] = m[3].x; paletteF[f + 13u] = m[3].y; paletteF[f + 14u] = m[3].z; paletteF[f + 15u] = m[3].w;
}

// The transpose of S * R * T. Building it this way means the caller can compose in the natural column-major order and still land on
// the C# result: m[j] is C# row j.
mat4 LocalMatrixG(vec3 s, vec4 q, vec3 t)
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
    return mat4(
        vec4(m00, m01, m02, 0.0),
        vec4(m10, m11, m12, 0.0),
        vec4(m20, m21, m22, 0.0),
        vec4(t, 1.0));
}

// Shortest-arc slerp, matching Quaternion.Slerp including its near-parallel shortcut.
vec4 QSlerp(vec4 a, vec4 b, float t)
{
    float d = dot(a, b);
    if (d < 0.0) { b = -b; d = -d; }
    if (d > 0.999999) return normalize(mix(a, b, t));
    float theta = acos(clamp(d, -1.0, 1.0));
    float inv = 1.0 / sin(theta);
    return sin((1.0 - t) * theta) * inv * a + sin(t * theta) * inv * b;
}

void main()
{
    uint inst = gl_GlobalInvocationID.x;
    if (inst >= pc.instanceCount) return;

    uint pbase = inst * (16u / 4u);
    uint clip = floatBitsToUint(paramF[pbase]);
    float time = paramF[pbase + 1u];

    uint jointFloats = pc.jointStride / 4u;
    uint trackFloats = pc.trackStride / 4u;
    uint keyFloats = pc.keyStride / 4u;
    uint scratchFloats = pc.scratchStride / 4u;
    uint paletteFloats = 64u / 4u;

    uint scratchBase = inst * pc.jointCount * scratchFloats;
    uint paletteBase = inst * pc.paletteStride * paletteFloats;

    for (uint j = 0u; j < pc.jointCount; j++)
    {
        uint jb = j * jointFloats;
        uint paletteIndex = floatBitsToUint(jointF[jb + JOINT_PALETTE]);

        bool driven = false;
        vec4 rt = vec4(0.0, 0.0, 0.0, 0.0);
        vec4 rq = vec4(0.0, 0.0, 0.0, 1.0);
        vec4 rs = vec4(0.0, 0.0, 0.0, 0.0);
        {
            uint tb = (clip * pc.jointCount + j) * trackFloats;
            uint frameCount = floatBitsToUint(trackF[tb]);
            if (frameCount != 0u)
            {
                driven = true;
                uint firstKey = floatBitsToUint(trackF[tb + 1u]);
                float rate = trackF[tb + 2u];
                float ft = time * rate;
                uint f0 = min(uint(ft), frameCount - 1u);
                uint f1 = min(f0 + 1u, frameCount - 1u);
                float a = ft - float(f0);

                uint i0 = (firstKey + f0 * pc.jointCount + j) * keyFloats;
                uint i1 = (firstKey + f1 * pc.jointCount + j) * keyFloats;
                vec4 t0 = vec4(trackInF[i0], trackInF[i0 + 1u], trackInF[i0 + 2u], 0.0);
                vec4 q0 = vec4(trackInF[i0 + 4u], trackInF[i0 + 5u], trackInF[i0 + 6u], trackInF[i0 + 7u]);
                vec4 s0 = vec4(trackInF[i0 + 8u], trackInF[i0 + 9u], trackInF[i0 + 10u], 0.0);
                vec4 t1 = vec4(trackInF[i1], trackInF[i1 + 1u], trackInF[i1 + 2u], 0.0);
                vec4 q1 = vec4(trackInF[i1 + 4u], trackInF[i1 + 5u], trackInF[i1 + 6u], trackInF[i1 + 7u]);
                vec4 s1 = vec4(trackInF[i1 + 8u], trackInF[i1 + 9u], trackInF[i1 + 10u], 0.0);

                rt = vec4(mix(t0.xyz, t1.xyz, a), 0.0);
                rq = QSlerp(q0, q1, a);
                rs = vec4(mix(s0.xyz, s1.xyz, a), 0.0);
            }
        }

        if (!driven)
        {
            rt = vec4(jointF[jb + JOINT_REST_T], jointF[jb + JOINT_REST_T + 1u], jointF[jb + JOINT_REST_T + 2u], 0.0);
            rq = vec4(jointF[jb + JOINT_REST_R], jointF[jb + JOINT_REST_R + 1u], jointF[jb + JOINT_REST_R + 2u], jointF[jb + JOINT_REST_R + 3u]);
            rs = vec4(jointF[jb + JOINT_REST_S], jointF[jb + JOINT_REST_S + 1u], jointF[jb + JOINT_REST_S + 2u], 0.0);
        }

        mat4 localG = LocalMatrixG(rs.xyz, rq, rt.xyz);

        int porder = floatBitsToInt(jointF[jb + JOINT_PARENT]);
        mat4 chainG = LoadJointMat(jb + JOINT_CHAIN);
        mat4 parentG;
        if (porder >= 0)
            parentG = LoadScratchMat(scratchBase + uint(porder) * scratchFloats) * chainG;
        else
            parentG = chainG;

        mat4 worldG = parentG * localG;
        StoreScratch(scratchBase + j * scratchFloats, worldG);

        mat4 ibmG = LoadJointMat(jb + JOINT_IBM);
        mat4 invMeshG = LoadJointMat(jb + JOINT_INV_MESH);
        mat4 boneG = invMeshG * worldG * ibmG;
        StorePalette(paletteBase + paletteIndex * paletteFloats, boneG);
    }
}
";
}
