// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

/// <summary>
/// C1: the D3D12 kernel source for <see cref="ClothSimulationEffect"/>. The GLSL, MSL and WGSL mirrors live in the sibling
/// partials and must stay line for line with this one; see the effect summary for the buffer layout all four agree on byte for
/// byte, and .dbg/_cloth_check/cloth_ref.py for the line-by-line numpy mirror it was tuned against.
///
/// <para>This file also declares the kernel itself: <see cref="CreateClothKernel"/> is the single place the binding list and the
/// workgroup size are stated, and the other three dialects read their slot numbers out of it.</para>
/// </summary>
public sealed partial class ClothSimulationEffect
{
    static ComputeKernelDesc CreateClothKernel() => new()
    {
        Name = "clothStep",
        Source = new ShaderSourceSet
        {
            Hlsl = HlslSource,
            Glsl = GlslSource,
            Msl = MslSource,
            Wgsl = WgslSource,
            EntryPoint = "CSMain",
        },
        Bindings = new[]
        {
            new ComputeBindingDesc { Type = ComputeBindingType.Params, SizeInBytes = ParamsBytes },
            new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferRead },
            new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferReadWrite },
            new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferReadWrite },
            new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferReadWrite },
            new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferReadWrite },
        },
        WorkgroupX = GroupSize,
        WorkgroupY = 1,
        WorkgroupZ = 1,
        // Step 5: cloth is the second Deferred feature. Its Record() already refuses to hand a surface over while the
        // dispatch is false (the streams keep their static vertices), so a flag nobody has written yet never reaches a
        // vertex buffer - the same path a failed dispatch has always taken.
        Deferred = true,
    };

    const string HlslSource = @"
cbuffer KernelParams : register(b0)
{
    float4 uWindDt;         // wind.xyz (m/s, simulation space), frame dt (s)
    float4 uGust;           // sideways gust amplitude (m/s), flutter phase, wavenumber (rad over the sheet), reset
    float4 uSwivel;         // cos yaw, sin yaw, pole collision radius, velocity damping (1/s)
    float4 uMaterial;       // shear compliance, bend compliance, normal drag, tangential drag
    float4 uShape;          // gravity y, half thickness, grid spacing, front u flipped
    float4 uOrigin;         // simulation-space origin subtracted from every output vertex
    uint4  uGrid;           // columns, rows, substeps, 0
};

ByteAddressBuffer   restIn    : register(t0);   // float4 per particle: rest.xyz, inverse mass (0 = pinned)
RWByteAddressBuffer stateIO   : register(u0);   // float4 pos + float4 vel per particle, resident across frames
RWByteAddressBuffer frontOut  : register(u1);   // Vertex (80 B) per particle, the +normal side
RWByteAddressBuffer backOut   : register(u2);   // Vertex (80 B) per particle, the -normal side
RWByteAddressBuffer rimOut    : register(u3);   // Vertex (80 B) x 2 per boundary point, front then back

#define THREADS 128
#define PER 4
#define MAXP 512
#define REST_STRIDE 16
#define STATE_STRIDE 32
#define VERTEX_STRIDE 80

groupshared float3 gPos[MAXP];
groupshared float3 gN[MAXP];
groupshared float  gW[MAXP];
groupshared uint   gBad;

float3 Swivel(float3 v)
{
    return float3(v.x * uSwivel.x - v.z * uSwivel.y, v.y, v.x * uSwivel.y + v.z * uSwivel.x);
}

// Inf/NaN by bit pattern: fxc is free to fold a NaN comparison away, an exponent test it is not.
bool NotFinite(float3 v)
{
    uint3 e = asuint(v) & 0x7f800000u;
    return any(e == 0x7f800000u);
}

uint Col(uint p) { return p % uGrid.x; }
uint Row(uint p) { return p / uGrid.x; }

// Centre differences clamped at the border, cross(dv, du): +Z for the sheet at rest, the side the front picture faces.
float3 GridNormal(uint p)
{
    uint cols = uGrid.x;
    uint i = Col(p), j = Row(p);
    uint ip = min(i + 1u, cols - 1u), im = i > 0u ? i - 1u : 0u;
    uint jp = min(j + 1u, uGrid.y - 1u), jm = j > 0u ? j - 1u : 0u;
    float3 du = gPos[j * cols + ip] - gPos[j * cols + im];
    float3 dv = gPos[jp * cols + i] - gPos[jm * cols + i];
    float3 n = cross(dv, du);
    float len2 = dot(n, n);
    return len2 > 1e-24 ? n * rsqrt(len2) : float3(0.0, 0.0, 1.0);
}

float3 GridTangent(uint p)
{
    uint cols = uGrid.x;
    uint i = Col(p), j = Row(p);
    uint ip = min(i + 1u, cols - 1u), im = i > 0u ? i - 1u : 0u;
    float3 du = gPos[j * cols + ip] - gPos[j * cols + im];
    float len2 = dot(du, du);
    return len2 > 1e-24 ? du * rsqrt(len2) : float3(1.0, 0.0, 0.0);
}

void StoreVertex(RWByteAddressBuffer buf, uint index, float3 pos, float2 uv, float3 normal, float4 tangent)
{
    uint o = index * VERTEX_STRIDE;
    buf.Store3(o + 0, asuint(pos));
    buf.Store2(o + 12, asuint(uv));
    buf.Store3(o + 20, asuint(normal));
    buf.Store4(o + 32, asuint(tangent));
    buf.Store4(o + 48, uint4(0u, 0u, 0u, 0u));
    buf.Store4(o + 64, uint4(0u, 0u, 0u, 0u));
}

// Boundary walk, clockwise in (c, t): top hoist to fly, fly top to bottom, bottom fly to hoist, hoist bottom to top.
uint LoopParticle(uint k)
{
    uint cols = uGrid.x, rows = uGrid.y;
    uint top = cols - 1u, side = rows - 1u;
    uint p;
    if (k < top)
        p = k;
    else if (k < top + side)
        p = (k - top) * cols + (cols - 1u);
    else if (k < 2u * top + side)
        p = (rows - 1u) * cols + (cols - 1u - (k - top - side));
    else
        p = (rows - 1u - (k - 2u * top - side)) * cols;
    return p;
}

[numthreads(THREADS, 1, 1)]
void CSMain(uint3 gtid : SV_GroupThreadID)
{
    uint tid = gtid.x;
    uint cols = uGrid.x, rows = uGrid.y;
    uint count = min(cols * rows, (uint)MAXP);
    uint substeps = max(uGrid.z, 1u);
    float h = uWindDt.w / (float)substeps;
    float invH = 1.0 / h;
    float invH2 = invH * invH;
    float spacing = uShape.z;
    float3 gravity = float3(0.0, uShape.x, 0.0);
    float damp = exp(-uSwivel.w * h);
    float kN = uMaterial.z, kT = uMaterial.w;
    float collide = uSwivel.z;

    if (tid == 0u)
        gBad = 0u;

    float3 prev[PER];
    float3 pinned[PER];
    float3 weff[PER];

    // Sideways unit vector in the horizontal plane: the flutter gust blows across the wind, never along it.
    float3 wind = uWindDt.xyz;
    float horizontal = length(wind.xz);
    float3 side = horizontal > 1e-6 ? float3(-wind.z, 0.0, wind.x) / max(horizontal, 1e-6) : float3(0.0, 0.0, 0.0);

    [unroll]
    for (uint k0 = 0u; k0 < PER; k0++)
    {
        uint p = tid + k0 * THREADS;
        prev[k0] = float3(0.0, 0.0, 0.0);
        pinned[k0] = float3(0.0, 0.0, 0.0);
        weff[k0] = wind;
        if (p < count)
        {
            float4 rest = asfloat(restIn.Load4(p * REST_STRIDE));
            pinned[k0] = Swivel(rest.xyz);
            gW[p] = rest.w;
            float3 pos = asfloat(stateIO.Load3(p * STATE_STRIDE));
            float3 vel = asfloat(stateIO.Load3(p * STATE_STRIDE + 16u));
            gPos[p] = pos;
            prev[k0] = vel;     // velocity until the reset decision below, then the previous position
            float c = (float)Col(p) / (float)(cols - 1u);
            weff[k0] = wind + uGust.x * sin(uGust.y - uGust.z * c) * side;
        }
    }
    GroupMemoryBarrierWithGroupSync();

    // Reset on request, on a non-finite particle, or on one that has wandered off the pole (10 m), all as one sheet:
    // resetting some particles and not others would tear the constraints apart on the next substep.
    [unroll]
    for (uint k1 = 0u; k1 < PER; k1++)
    {
        uint p = tid + k1 * THREADS;
        if (p < count)
        {
            float3 d = gPos[p] - pinned[k1];
            if (NotFinite(gPos[p]) || NotFinite(prev[k1]) || !(dot(d, d) <= 100.0))
                gBad = 1u;
        }
    }
    GroupMemoryBarrierWithGroupSync();
    bool reset = uGust.w > 0.5 || gBad != 0u;

    [unroll]
    for (uint k2 = 0u; k2 < PER; k2++)
    {
        uint p = tid + k2 * THREADS;
        if (p < count)
        {
            if (reset)
            {
                gPos[p] = pinned[k2];
                prev[k2] = pinned[k2];
            }
            else
            {
                prev[k2] = gPos[p] - prev[k2] * h;
            }
        }
    }
    GroupMemoryBarrierWithGroupSync();

    [loop]
    for (uint s = 0u; s < substeps; s++)
    {
        [unroll]
        for (uint ka = 0u; ka < PER; ka++)
        {
            uint p = tid + ka * THREADS;
            if (p < count)
                gN[p] = GridNormal(p);
        }
        GroupMemoryBarrierWithGroupSync();

        // Predict: aerodynamic split on the relative wind, gravity, damping. Pins follow the swivel instead.
        [unroll]
        for (uint kb = 0u; kb < PER; kb++)
        {
            uint p = tid + kb * THREADS;
            if (p < count)
            {
                float3 P = gPos[p];
                float3 v = (P - prev[kb]) * invH;
                float3 n = gN[p];
                float3 u = weff[kb] - v;
                float un = dot(u, n);
                float3 a = kN * un * n + kT * (u - un * n);
                v = (v + h * (gravity + a)) * damp;
                prev[kb] = P;
                gPos[p] = gW[p] > 0.0 ? P + h * v : pinned[kb];
            }
        }
        GroupMemoryBarrierWithGroupSync();

        // Twelve colours, each a set of disjoint edges owned by their first endpoint: stretch H and V, shear in both
        // diagonals, bend H and V over two cells, each split by parity. Jacobi inside a colour, Gauss-Seidel across.
        [loop]
        for (uint colour = 0u; colour < 12u; colour++)
        {
            uint kind = colour >> 1;
            uint parity = colour & 1u;
            [unroll]
            for (uint kc = 0u; kc < PER; kc++)
            {
                uint p = tid + kc * THREADS;
                if (p < count)
                {
                    uint i = Col(p), j = Row(p);
                    bool on = false;
                    uint q = p;
                    float rest = spacing;
                    float compliance = 0.0;
                    if (kind == 0u)      { on = i + 1u < cols && (i & 1u) == parity;                  q = p + 1u; }
                    else if (kind == 1u) { on = j + 1u < rows && (j & 1u) == parity;                  q = p + cols; }
                    else if (kind == 2u) { on = i + 1u < cols && j + 1u < rows && (i & 1u) == parity; q = p + cols + 1u; rest = spacing * 1.41421356; compliance = uMaterial.x; }
                    else if (kind == 3u) { on = i >= 1u && j + 1u < rows && (i & 1u) == parity;       q = p + cols - 1u; rest = spacing * 1.41421356; compliance = uMaterial.x; }
                    else if (kind == 4u) { on = i + 2u < cols && ((i >> 1) & 1u) == parity;           q = p + 2u; rest = spacing * 2.0; compliance = uMaterial.y; }
                    else                 { on = j + 2u < rows && ((j >> 1) & 1u) == parity;           q = p + 2u * cols; rest = spacing * 2.0; compliance = uMaterial.y; }

                    if (on)
                    {
                        float wp = gW[p], wq = gW[q];
                        float3 d = gPos[p] - gPos[q];
                        float len = length(d);
                        float wsum = wp + wq;
                        if (len > 1e-6 && wsum > 0.0)
                        {
                            float dl = -(len - rest) / (wsum + compliance * invH2);
                            float3 g = d / len;
                            gPos[p] += (wp * dl) * g;
                            gPos[q] -= (wq * dl) * g;
                        }
                    }
                }
            }
            GroupMemoryBarrierWithGroupSync();
        }

        // Pole: push free particles out of the cylinder around the simulation-space Y axis.
        [unroll]
        for (uint kd = 0u; kd < PER; kd++)
        {
            uint p = tid + kd * THREADS;
            if (p < count && gW[p] > 0.0)
            {
                float3 P = gPos[p];
                float r = length(P.xz);
                if (r < collide && r > 1e-6)
                {
                    P.xz *= collide / r;
                    gPos[p] = P;
                }
            }
        }
        GroupMemoryBarrierWithGroupSync();
    }

    [unroll]
    for (uint ke = 0u; ke < PER; ke++)
    {
        uint p = tid + ke * THREADS;
        if (p < count)
            gN[p] = GridNormal(p);
    }
    GroupMemoryBarrierWithGroupSync();

    float halfThick = uShape.y;
    bool flipFront = uShape.w > 0.5;
    [unroll]
    for (uint kf = 0u; kf < PER; kf++)
    {
        uint p = tid + kf * THREADS;
        if (p < count)
        {
            float3 P = gPos[p];
            float3 vel = (P - prev[kf]) * invH;
            stateIO.Store4(p * STATE_STRIDE, asuint(float4(P, 0.0)));
            stateIO.Store4(p * STATE_STRIDE + 16u, asuint(float4(vel, 0.0)));

            float3 n = gN[p];
            float3 du = GridTangent(p);
            float c = (float)Col(p) / (float)(cols - 1u);
            float t = (float)Row(p) / (float)(rows - 1u);
            float3 local = P - uOrigin.xyz;
            StoreVertex(frontOut, p, local + halfThick * n, float2(flipFront ? 1.0 - c : c, t), n, float4(flipFront ? -du : du, 1.0));
            StoreVertex(backOut, p, local - halfThick * n, float2(c, t), -n, float4(du, 1.0));
        }
    }

    // Rim: two vertices per boundary point, the in-plane outward normal cross(n, T) with T the loop's centre difference.
    uint loopLength = 2u * (cols - 1u) + 2u * (rows - 1u);
    for (uint k = tid; k < loopLength; k += THREADS)
    {
        uint p = LoopParticle(k);
        uint pn = LoopParticle(k + 1u < loopLength ? k + 1u : 0u);
        uint pp = LoopParticle(k > 0u ? k - 1u : loopLength - 1u);
        float3 n = gN[p];
        float3 T = gPos[pn] - gPos[pp];
        float3 o = cross(n, T);
        float len2 = dot(o, o);
        o = len2 > 1e-24 ? o * rsqrt(len2) : float3(0.0, 1.0, 0.0);
        float c = (float)Col(p) / (float)(cols - 1u);
        float t = (float)Row(p) / (float)(rows - 1u);
        float3 local = gPos[p] - uOrigin.xyz;
        float4 tangent = float4(T * rsqrt(max(dot(T, T), 1e-24)), 1.0);
        StoreVertex(rimOut, 2u * k, local + halfThick * n, float2(c, t), o, tangent);
        StoreVertex(rimOut, 2u * k + 1u, local - halfThick * n, float2(c, t), o, tangent);
    }
}
";
}
