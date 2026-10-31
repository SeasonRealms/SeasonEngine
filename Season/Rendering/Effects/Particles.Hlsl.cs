// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

public sealed partial class ParticleSystemEffect
{
    /// <summary>D3D12 cs_5_0: rewrites the five indirect arguments every frame, zeroing the count the compaction pass will raise.</summary>
    const string ResetHlsl = @"
cbuffer ParticleResetParams : register(b0)
{
    float uIndexCount;
    float uPad0;
    float uPad1;
    float uPad2;
};

RWByteAddressBuffer uArgs : register(u0);

[numthreads(1, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x == 0)
    {
        // IndirectDrawIndexedArgs: indexCount / instanceCount / startIndex / baseVertex / startInstance.
        // startInstance stays 0 by contract so SV_InstanceID equals the compacted slot on every backend.
        uArgs.Store(0u, (uint)uIndexCount);
        uArgs.Store(4u, 0u);
        uArgs.Store(8u, 0u);
        uArgs.Store(12u, 0u);
        uArgs.Store(16u, 0u);
    }
}
";

    /// <summary>D3D12 cs_5_0: derives one particle per thread, culls it, and appends survivors while raising the indirect instance count.</summary>
    const string CompactHlsl = @"
cbuffer ParticleCompactParams : register(b0)
{
    float uPhase;
    float uBoxRadius;
    float uBoxHeight;
    float uCapacity;
    float uOriginX;
    float uOriginY;
    float uOriginZ;
    float uParticleSize;
    float uColorR;
    float uColorG;
    float uColorB;
    float uColorA;
    float uDensity;
    float uSway;
    float uLifeSeconds;
    float uSeed;
    float uAccelX;
    float uAccelY;
    float uAccelZ;
    float uRegionCount;
    float uWindX;
    float uWindY;
    float uWindZ;
    float uFlags;
};

ByteAddressBuffer uFrame : register(t0);
ByteAddressBuffer uRegions : register(t1);
RWByteAddressBuffer uInstances : register(u0);
RWByteAddressBuffer uArgs : register(u1);

float Hash(uint n)
{
    n = (n << 13u) ^ n;
    n = n * (n * n * 15731u + 789221u) + 1376312589u;
    return float(n & 0x00FFFFFFu) / 16777216.0;
}

[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    uint index = id.x;
    if (index < (uint)uCapacity)
    {
        index += (uint)uSeed;
        float h0 = Hash(index * 3u + 1u);
        float h1 = Hash(index * 5u + 7u);
        float h2 = Hash(index * 11u + 23u);
        float h3 = Hash(index * 17u + 41u);
        if (h3 < uDensity)
        {
            // 相位以寿命为时标，所以原来的 fall 同时就是 [0,1) 的归一化年龄；换算回秒即可闭式积分。
            // 寿命为 0 时 t 与 invLife 一并取零，位置和速度都精确退回未扩展前的结果。
            float age = frac(uPhase + h2);
            float invLife = uLifeSeconds > 0.0 ? 1.0 / uLifeSeconds : 0.0;
            float t = age * uLifeSeconds;
            float3 accel = float3(uAccelX, uAccelY, uAccelZ);
            // row 11：圆盘半径 / 径向速度 / 摆动幅度 / 曲线模式标志。
            float4 uCurve = asfloat(uFrame.Load4(176u));

            float3 center;
            float3 velocity;
            if (((uint)uFlags & 16u) != 0u)
            {
                // 圆盘出生：半径取 sqrt(h0) 才让面积均匀，否则粒子会挤在圆心堆成一坨。
                float ring = h1 * 6.283185;
                float2 radial = float2(cos(ring), sin(ring));
                float radius = sqrt(h0) * uCurve.x;
                center = float3(uOriginX + radial.x * radius, uOriginY, uOriginZ + radial.y * radius);
                // 圆盘出生没有落差：竖直运动全部来自加速度与风速，火舌的上升速度因此由 AccelY 单独决定。
                velocity = float3(0.0, 0.0, 0.0);
            }
            else
            {
                center.x = uOriginX + (h0 * 2.0 - 1.0) * uBoxRadius + sin((uPhase + h0) * 6.283185) * uSway;
                center.y = uOriginY + uBoxHeight * (0.7 - age);
                center.z = uOriginZ + (h1 * 2.0 - 1.0) * uBoxRadius + cos((uPhase + h1) * 6.283185) * uSway;
                // 基础速度是上面三行对时间求导：相位速率就是 1/life，所以每项都带一个 invLife。
                // uBoxHeight * invLife 在默认档恰好等于 FallSpeed，因为 life 本就是 height / FallSpeed。
                velocity.x = cos((uPhase + h0) * 6.283185) * 6.283185 * uSway * invLife;
                velocity.y = -uBoxHeight * invLife;
                velocity.z = -sin((uPhase + h1) * 6.283185) * 6.283185 * uSway * invLife;
            }
            // 加速度位移必须在构造 clip 之前并入，否则剔除判据用的位置与最终光栅化的位置不是同一个点。
            center += 0.5 * accel * t * t;
            velocity += accel * t;
            float3 wind = float3(uWindX, uWindY, uWindZ);
            center += wind * t;
            velocity += wind;
            if (((uint)uFlags & 16u) != 0u)
            {
                float ring = h1 * 6.283185;
                float2 radial = float2(cos(ring), sin(ring));
                // 径向扩散与年龄成正比：烟柱因此越往上越宽；火焰用负的 Spread 让火舌向中轴收拢。
                center.xz += radial * uCurve.y * t;
                velocity.xz += radial * uCurve.y;
                // 两条不同频率的正弦合成低频摆动，对年龄仍是闭式函数，不引入任何跨帧状态。
                center.xz += float2(sin(t * 1.3 + h2 * 6.283185), cos(t * 1.1 + h3 * 6.283185)) * uCurve.z * t;
            }
            if (((uint)uFlags & 4u) != 0u) {
                float2 camera = asfloat(uFrame.Load4(96u)).xz;
                float period = 2.0 * uBoxRadius;
                center.xz -= floor((center.xz - camera + uBoxRadius) / period) * period;
            }
            // 三行区域记录与 WeatherField.WriteParticleData 一致，按优先级合成剩余权重。
            uint channel = (uint)uFlags & 3u;
            float weight = 1.0;
            if (channel != 0u) {
                weight = 0.0;
                float remaining = 1.0;
                for (uint r = 0u; r < (uint)uRegionCount; r++) {
                    float4 a = asfloat(uRegions.Load4(r * 48u));
                    float4 b = asfloat(uRegions.Load4(r * 48u + 16u));
                    float4 c = asfloat(uRegions.Load4(r * 48u + 32u));
                    float2 d = center.xz - a.xy;
                    float2 local = float2(b.x*d.x+b.y*d.y, -b.y*d.x+b.x*d.y) * a.zw;
                    float m = saturate((1.0-length(local)) / b.z);
                    m = m*m*(3.0-2.0*m);
                    if (center.y < c.x || center.y > c.y) m = 0.0;
                    weight += remaining * m * (channel == 1u ? c.z : c.w);
                    remaining *= 1.0-m;
                }
            }

            // 周期窗口的搬运发生在不可见边缘；强度变化只渐显新候选，不整场重置。
            float viewDistance = asfloat(uFrame.Load4(112u)).w;
            if (viewDistance > 0.0) {
                float f = saturate((1.0 - length(center - asfloat(uFrame.Load4(96u)).xyz) / viewDistance) * 4.0);
                weight *= f * f * (3.0 - 2.0 * f);
            }
            if (((uint)uFlags & 8u) != 0u) weight *= saturate((uDensity - h3) * 50.0);

            float4x4 viewProjection = float4x4(
                asfloat(uFrame.Load4(0u)), asfloat(uFrame.Load4(16u)),
                asfloat(uFrame.Load4(32u)), asfloat(uFrame.Load4(48u)));
            float4 clip = mul(float4(center, 1.0), viewProjection);
            // 用 w 加上公告板半径做横向余量，避免贴边粒子在扩张成四边形后被整体丢弃。
            float margin = clip.w + uParticleSize * 4.0;
            float4 streak = abs(mul(float4(velocity * asfloat(uFrame.Load4(112u)).z * 0.5, 0.0), viewProjection));
            if (weight > 0.0 && clip.w + streak.w > 1e-4 && clip.z + streak.z > -1e-4
                && abs(clip.x) < margin + streak.x + streak.w && abs(clip.y) < margin + streak.y + streak.w)
            {
                uint slot;
                // 每个线程最多加一，线程数即容量，所以 slot 必然小于容量，无需再夹取。
                uArgs.InterlockedAdd(4u, 1u, slot);
                uint offset = slot * 48u;
                uInstances.Store3(offset, asuint(center));
                uInstances.Store(offset + 12u, asuint(uParticleSize));
                // 曲线模式下 rgb 改存两枚稳定随机数（按下标哈希，不受实例槽位争夺影响），
                // 顶点阶段据此选颜色梯度与初始转角；覆盖度仍然只走 alpha，与旧版完全一致。
                float4 stored = uCurve.w > 0.5
                    ? float4(h0, h1, 0.0, uColorA * weight)
                    : float4(uColorR, uColorG, uColorB, uColorA * weight);
                uInstances.Store4(offset + 16u, asuint(stored));
                uInstances.Store3(offset + 32u, asuint(velocity));
                uInstances.Store(offset + 44u, asuint(age));
            }
        }
    }
}
";

    // ── Draw-side shaders, consumed by the backend particle pipeline ──
    // No vertex buffer and no input layout: the quad corner comes from SV_VertexID over the engine's
    // existing unit-quad index buffer, and every per-instance value is loaded from the compacted
    // instance buffer by SV_InstanceID. Bindings are t0 instances / t1 frame, both root SRVs.

    /// <summary>D3D12 vs_5_0 / ps_5_0 source for the indirect billboard draw.</summary>
    public const string DrawHlsl = @"
ByteAddressBuffer uInstances : register(t0);
ByteAddressBuffer uFrame : register(t1);

struct VSOutput
{
    float4 Position : SV_Position;
    float2 Offset : TEXCOORD0;
    float4 Color : COLOR0;
    // (稳定随机数, 归一化年龄, 噪声强度, 加法混合标志)。曲线模式的外观参数只在片元阶段用到，
    // 而 WebGPU 的绑定组只对顶点阶段可见，所以这些值必须跟着插值器走而不是再去读帧缓冲。
    float4 Misc : TEXCOORD1;
};

VSOutput VSMain(uint vertexId : SV_VertexID, uint instanceId : SV_InstanceID)
{
    // startInstance is 0 by the IndirectDrawIndexedArgs contract, so SV_InstanceID is the slot the
    // compaction kernel wrote. It would not be if the arguments ever carried a non-zero firstInstance.
    uint offset = instanceId * 48u;
    float3 center = asfloat(uInstances.Load3(offset));
    float size = asfloat(uInstances.Load(offset + 12u));
    float4 color = asfloat(uInstances.Load4(offset + 16u));
    float3 velocity = asfloat(uInstances.Load3(offset + 32u));
    float age = asfloat(uInstances.Load(offset + 44u));

    float2 corner = float2((vertexId & 1u) != 0u ? 1.0 : -1.0, (vertexId & 2u) != 0u ? -1.0 : 1.0);
    float3 right = asfloat(uFrame.Load4(64u)).xyz;
    float3 up = asfloat(uFrame.Load4(80u)).xyz;
    // Frame row 7 = fadeIn / fadeOut (both fractions of the lifetime) / stretch in seconds.
    float3 drawParams = asfloat(uFrame.Load4(112u)).xyz;
    // rows 8..11 是外观曲线与出生形状：曲线模式关闭时标志位为 0，下面这个分支不成立，
    // 于是 size、color、corner 三个量与扩容前逐位相同。
    float4 tintStart = asfloat(uFrame.Load4(128u));
    float4 tintMid = asfloat(uFrame.Load4(144u));
    float4 tintEnd = asfloat(uFrame.Load4(160u));
    float4 curve = asfloat(uFrame.Load4(176u));
    float seed = 0.0;
    float2 shape = corner;
    if (curve.w > 0.5)
    {
        // 实例 rgb 是压缩阶段写入的两枚稳定随机数：按粒子下标哈希，不受实例槽位争夺影响。
        seed = color.r;
        // 两段线性梯度：前半程起点→中点，后半程中点→终点。
        color.rgb = age < 0.5
            ? lerp(tintStart.rgb, tintMid.rgb, age * 2.0)
            : lerp(tintMid.rgb, tintEnd.rgb, age * 2.0 - 1.0);
        size = lerp(size, tintEnd.w, age);
        // 每颗粒子独立的初始转角并缓慢自旋：一排排同相的斑块是廉价粒子最明显的破绽。
        float spin = seed * 6.283185 + age * (seed - 0.5) * 3.0;
        float spinCos = cos(spin);
        float spinSin = sin(spin);
        shape = float2(corner.x * spinCos - corner.y * spinSin, corner.x * spinSin + corner.y * spinCos);
    }
    // 必须写成三元判断，不能写成除以一个 epsilon：fadeIn 为 0 时 age 也常常是 0，
    // saturate(0 / eps) 给出 0 而不是 1，会把整片粒子的覆盖度吃掉。
    float fadeIn = drawParams.x > 0.0 ? saturate(age / drawParams.x) : 1.0;
    float fadeOut = drawParams.y > 0.0 ? saturate((1.0 - age) / drawParams.y) : 1.0;
    color.a *= fadeIn * fadeOut;
    // 沿速度方向把四边形切变成平行四边形，总长度为 |velocity| * stretch；
    // 上下两角各偏移半个长度，所以 stretch 为 0 时精确退化回原来的正方形公告板。
    float3 world = center + (right * shape.x + up * shape.y) * size
        + velocity * (drawParams.z * shape.y * 0.5);
    float4x4 viewProjection = float4x4(
        asfloat(uFrame.Load4(0u)), asfloat(uFrame.Load4(16u)),
        asfloat(uFrame.Load4(32u)), asfloat(uFrame.Load4(48u)));

    VSOutput output;
    output.Position = mul(float4(world, 1.0), viewProjection);
    // Offset 保持未旋转的角点：衰减只取它的平方和，旋转不改变结果，默认路径因此一字未改。
    output.Offset = corner;
    output.Color = color;
    output.Misc = float4(seed, age, curve.w > 0.5 ? tintMid.w : 0.0, tintStart.w);
    return output;
}

// 无正弦的哈希：sin 在部分硬件上对大参数会丢精度，而这里的输入随年龄一路增大。
float Hash2(float2 p)
{
    float3 p3 = frac(p.xyx * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

float ValueNoise(float2 p)
{
    float2 i = floor(p);
    float2 f = p - i;
    f = f * f * (3.0 - 2.0 * f);
    return lerp(lerp(Hash2(i), Hash2(i + float2(1.0, 0.0)), f.x),
        lerp(Hash2(i + float2(0.0, 1.0)), Hash2(i + float2(1.0, 1.0)), f.x), f.y);
}

float4 Shade(VSOutput input)
{
    float falloff = saturate(1.0 - dot(input.Offset, input.Offset));
    // 默认路径保持原样：曲线模式关闭时 Misc.z 为 0，这一行与旧版逐位相同。
    float alpha = input.Color.a * falloff * falloff;
    if (input.Misc.z > 0.0)
    {
        // 噪声随年龄沿 -y 滚动，火舌与烟卷因此在原地往上走，而不是贴着一动不动。
        float2 q = input.Offset * 1.7 + input.Misc.x * 31.0 + float2(0.0, -input.Misc.y * 2.5);
        float n = 0.65 * ValueNoise(q) + 0.35 * ValueNoise(q * 2.3);
        alpha = input.Color.a * saturate(falloff * falloff * (1.0 + input.Misc.z * (n * 2.0 - 1.0)));
    }
    // 加法档把 alpha 压到 0：One/InvSrcAlpha 下就等于纯加法，不必多一个 PSO。
    return float4(input.Color.rgb * alpha, alpha * (1.0 - input.Misc.w));
}

float4 PSMain(VSOutput input) : SV_Target0
{
    return Shade(input);
}

struct PSVelocityOutput
{
    float4 Color : SV_Target0;
    float2 Velocity : SV_Target1;
};

PSVelocityOutput PSMainVelocity(VSOutput input)
{
    // Slot 1 is write-masked off in the velocity PSO variant, matching how the main pipeline keeps
    // opaque velocity underneath transparent geometry. The value here is never stored.
    PSVelocityOutput output;
    output.Color = Shade(input);
    output.Velocity = float2(0.0, 0.0);
    return output;
}
";
}
