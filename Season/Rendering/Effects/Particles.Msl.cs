// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

/// <summary>
/// Metal MSL mirror of the HLSL sources, compiled to IMTLLibrary at runtime by the backend.
///
/// Three translation rules govern every line here.
///
/// Indexing: HLSL ByteAddressBuffer addresses bytes, MSL pointers address elements. Every offset
/// below is the HLSL byte offset divided by the element stride, written out so the files can be
/// diffed by eye. Getting this wrong produces no diagnostic on any backend, only displaced data.
///
/// Matrices: the frame buffer holds the four *rows* of the view-projection. HLSL float4x4(r0..r3)
/// builds rows and is consumed as mul(v, M); MSL float4x4(r0..r3) builds *columns* and is consumed
/// as M * v. Both evaluate to the same sum over v[i] * r[i], so no transpose is needed -- the same
/// cancellation already documented for the GLSL mirror.
///
/// Slots: Metal has no descriptor sets. Compute bindings land where Graphics.TryDispatchCompute puts
/// them -- Params at buffer(0) through SetBytes, then every buffer binding in declaration order
/// starting at buffer(1) -- which makes the numbers identical to the GLSL std430 bindings. The draw
/// side is bound by MTLParticleDrawPipeline, vertex buffer(0) = instances and buffer(1) = frame.
/// </summary>
public sealed partial class ParticleSystemEffect
{
    /// <summary>Metal compute: rewrites the five indirect arguments every frame, zeroing the count the compaction pass will raise.</summary>
    const string ResetMsl = """
#include <metal_stdlib>
using namespace metal;
kernel void CSMain(constant float4& resetParams [[buffer(0)]],device uint* args [[buffer(1)]],
    uint3 id [[thread_position_in_grid]]) {
    if(id.x==0u) {
        // IndirectDrawIndexedArgs: indexCount / instanceCount / startIndex / baseVertex / startInstance.
        // uint indices 0..4 are the HLSL byte offsets 0/4/8/12/16 divided by the 4-byte stride.
        // startInstance stays 0 by contract so [[instance_id]] equals the compacted slot.
        args[0]=uint(resetParams.x);
        args[1]=0u;
        args[2]=0u;
        args[3]=0u;
        args[4]=0u;
    }
}
""";

    /// <summary>Metal compute: derives one particle per thread, culls it, and appends survivors while raising the indirect instance count.</summary>
    const string CompactMsl = """
#include <metal_stdlib>
using namespace metal;
float ParticleHash(uint n) {
    n=(n<<13u)^n;
    n=n*(n*n*15731u+789221u)+1376312589u;
    return float(n&0x00FFFFFFu)/16777216.0;
}
// 与 HLSL 共用 96B 参数块，共六个 float4：
// p[0]=相位/盒半径/盒高度/预算，p[1]=原点.xyz/粒径，p[2]=颜色。
// p[3]=密度/摆幅/寿命/种子，p[4]=加速度.xyz/区域数，p[5]=风速.xyz/通道与窗口标志。
// args is declared atomic because index 1 is the compaction counter; the reset kernel owns the
// plain-uint view of the same buffer and the two never run concurrently.
kernel void CSMain(constant float4* p [[buffer(0)]],const device float4* frameData [[buffer(1)]],
    device float4* instanceData [[buffer(2)]],device atomic_uint* args [[buffer(3)]],
    const device float4* regionData [[buffer(4)]],
    uint3 id [[thread_position_in_grid]]) {
    // p3.w=种子，p4.w=区域数，p5=风速.xyz/通道与窗口标志；参数块共 96B。
    float4 p0=p[0],p1=p[1],p2=p[2],p3=p[3],p4=p[4],p5=p[5];
    uint index=id.x;
    if(index<uint(p0.w)) {
        index+=uint(p3.w);
        float h0=ParticleHash(index*3u+1u);
        float h1=ParticleHash(index*5u+7u);
        float h2=ParticleHash(index*11u+23u);
        float h3=ParticleHash(index*17u+41u);
        if(h3<p3.x) {
            // 相位以寿命为时标，所以原来的 fall 同时就是 [0,1) 的归一化年龄；换算回秒即可闭式积分。
            // 寿命为 0 时 t 与 invLife 一并取零，位置和速度都精确退回未扩展前的结果。
            float age=fract(p0.x+h2);
            float invLife=p3.z>0.0?1.0/p3.z:0.0;
            float t=age*p3.z;
            float3 accel=p4.xyz;
            // frameData[11] 是 HLSL Load4(176)：圆盘半径 / 径向速度 / 摆动幅度 / 曲线模式标志。
            float4 uCurve=frameData[11];

            float3 center;
            float3 velocity;
            if((uint(p5.w)&16u)!=0u) {
                // 圆盘出生：半径取 sqrt(h0) 才让面积均匀，否则粒子会挤在圆心堆成一坨。
                float ring=h1*6.283185;
                float2 radial=float2(cos(ring),sin(ring));
                float radius=sqrt(h0)*uCurve.x;
                center=float3(p1.x+radial.x*radius,p1.y,p1.z+radial.y*radius);
                // 圆盘出生没有落差：竖直运动全部来自加速度与风速，火舌的上升速度因此由 AccelY 单独决定。
                velocity=float3(0.0);
            } else {
                center.x=p1.x+(h0*2.0-1.0)*p0.y+sin((p0.x+h0)*6.283185)*p3.y;
                center.y=p1.y+p0.z*(0.7-age);
                center.z=p1.z+(h1*2.0-1.0)*p0.y+cos((p0.x+h1)*6.283185)*p3.y;
                // 基础速度是上面三行对时间求导：相位速率就是 1/life，所以每项都带一个 invLife。
                // p0.z*invLife 在默认档恰好等于 FallSpeed，因为 life 本就是 boxHeight / FallSpeed。
                velocity.x=cos((p0.x+h0)*6.283185)*6.283185*p3.y*invLife;
                velocity.y=-p0.z*invLife;
                velocity.z=-sin((p0.x+h1)*6.283185)*6.283185*p3.y*invLife;
            }
            // 加速度位移必须在构造 clip 之前并入，否则剔除判据用的位置与最终光栅化的位置不是同一个点。
            center+=0.5*accel*t*t;
            velocity+=accel*t;
            center+=p5.xyz*t;
            velocity+=p5.xyz;
            if((uint(p5.w)&16u)!=0u) {
                float ring=h1*6.283185;
                float2 radial=float2(cos(ring),sin(ring));
                // 径向扩散与年龄成正比：烟柱因此越往上越宽；火焰用负的 Spread 让火舌向中轴收拢。
                center.xz+=radial*uCurve.y*t;
                velocity.xz+=radial*uCurve.y;
                // 两条不同频率的正弦合成低频摆动，对年龄仍是闭式函数，不引入任何跨帧状态。
                center.xz+=float2(sin(t*1.3+h2*6.283185),cos(t*1.1+h3*6.283185))*uCurve.z*t;
            }
            if((uint(p5.w)&4u)!=0u) {
                float period=2.0*p0.y;
                center.xz-=floor((center.xz-frameData[6].xz+p0.y)/period)*period;
            }
            uint channel=uint(p5.w)&3u;
            float weight=1.0;
            if(channel!=0u) {
                weight=0.0;
                float remaining=1.0;
                for(uint r=0u;r<uint(p4.w);r++) {
                    float4 a=regionData[r*3u],b=regionData[r*3u+1u],c=regionData[r*3u+2u];
                    float2 d=center.xz-a.xy;
                    float2 local=float2(b.x*d.x+b.y*d.y,-b.y*d.x+b.x*d.y)*a.zw;
                    float m=clamp((1.0-length(local))/b.z,0.0,1.0);
                    m=m*m*(3.0-2.0*m);
                    if(center.y<c.x || center.y>c.y) m=0.0;
                    weight+=remaining*m*(channel==1u?c.z:c.w);
                    remaining*=1.0-m;
                }
            }

            // 周期窗口的搬运发生在不可见边缘；强度变化只渐显新候选，不整场重置。
            float viewDistance=frameData[7].w;
            if(viewDistance>0.0) {
                float f=clamp((1.0-length(center-frameData[6].xyz)/viewDistance)*4.0,0.0,1.0);
                weight*=f*f*(3.0-2.0*f);
            }
            if((uint(p5.w)&8u)!=0u) weight*=clamp((p3.x-h3)*50.0,0.0,1.0);

            // frameData[0..3] are the HLSL Load4(0/16/32/48): the four rows of the view-projection.
            float4x4 viewProjection=float4x4(frameData[0],frameData[1],frameData[2],frameData[3]);
            float4 clip=viewProjection*float4(center,1.0);
            // 用 w 加上公告板半径做横向余量，避免贴边粒子在扩张成四边形后被整体丢弃。
            float margin=clip.w+p1.w*4.0;
            float4 streak=abs(viewProjection*float4(velocity*frameData[7].z*0.5,0.0));
            if(weight>0.0 && clip.w+streak.w>1e-4 && clip.z+streak.z>-1e-4
                && abs(clip.x)<margin+streak.x+streak.w && abs(clip.y)<margin+streak.y+streak.w) {
                // 每个线程最多加一，线程数即容量，所以 slot 必然小于容量，无需再夹取。
                uint slot=atomic_fetch_add_explicit(&args[1],1u,memory_order_relaxed);
                // The 48-byte instance stride is three float4: HLSL offset+0, +16 and +32.
                instanceData[slot*3u]=float4(center,p1.w);
                // 曲线模式下 rgb 改存两枚稳定随机数（按下标哈希，不受实例槽位争夺影响），
                // 顶点阶段据此选颜色梯度与初始转角；覆盖度仍然只走 alpha，与旧版完全一致。
                float3 tint=uCurve.w>0.5?float3(h0,h1,0.0):p2.xyz;
                instanceData[slot*3u+1u]=float4(tint,p2.w*weight);
                instanceData[slot*3u+2u]=float4(velocity,age);
            }
        }
    }
}
""";

    // ── Draw-side shaders, consumed by MTLParticleDrawPipeline ──
    // No vertex descriptor and no [[stage_in]] on the vertex stage, matching the HLSL: the quad corner
    // comes from [[vertex_id]] over a unit-quad index buffer and every per-instance value is loaded by
    // [[instance_id]].
    //
    // [[instance_id]] excludes baseInstance, exactly like SV_InstanceID and unlike gl_InstanceIndex.
    // The IndirectDrawIndexedArgs contract pins baseInstance to zero anyway -- WebGPU cannot express a
    // non-zero one without an optional feature -- so all three families agree on the slot number.

    const string DrawCommonMsl = """
#include <metal_stdlib>
using namespace metal;
struct ParticleVertex {
    float4 position [[position]];
    float2 offset;
    float4 color;
    // (稳定随机数, 归一化年龄, 噪声强度, 加法混合标志)。曲线模式的外观参数只在片元阶段用到，
    // 而 WebGPU 的绑定组只对顶点阶段可见，所以这些值跟着插值器走而不是再去读帧缓冲。
    float4 misc;
};

""";

    /// <summary>Metal vertex stage for the indirect billboard draw.</summary>
    public const string DrawVertexMsl = DrawCommonMsl + """
vertex ParticleVertex VSMain(uint vertexId [[vertex_id]],uint slot [[instance_id]],
    const device float4* instanceData [[buffer(0)]],const device float4* frameData [[buffer(1)]]) {
    float4 packed=instanceData[slot*3u];
    float3 center=packed.xyz;
    float size=packed.w;
    float4 color=instanceData[slot*3u+1u];
    float4 motion=instanceData[slot*3u+2u];
    float3 velocity=motion.xyz;
    float age=motion.w;

    float2 corner=float2((vertexId&1u)!=0u?1.0:-1.0,(vertexId&2u)!=0u?-1.0:1.0);
    // frameData[4] and [5] are the HLSL Load4(64) and Load4(80): camera right and up.
    float3 right=frameData[4].xyz;
    float3 up=frameData[5].xyz;
    // frameData[7] is the HLSL Load4(112): fadeIn / fadeOut (fractions of the lifetime) / stretch in seconds.
    float3 drawParams=frameData[7].xyz;
    // frameData[8..11] 是外观曲线与出生形状：曲线模式关闭时标志位为 0，下面这个分支不成立，
    // 于是 size、color、corner 三个量与扩容前逐位相同。
    float4 tintStart=frameData[8];
    float4 tintMid=frameData[9];
    float4 tintEnd=frameData[10];
    float4 curve=frameData[11];
    float seed=0.0;
    float2 shape=corner;
    if(curve.w>0.5) {
        // 实例 rgb 是压缩阶段写入的两枚稳定随机数：按粒子下标哈希，不受实例槽位争夺影响。
        seed=color.r;
        // 两段线性梯度：前半程起点→中点，后半程中点→终点。
        color.rgb=age<0.5?mix(tintStart.rgb,tintMid.rgb,age*2.0):mix(tintMid.rgb,tintEnd.rgb,age*2.0-1.0);
        size=mix(size,tintEnd.w,age);
        // 每颗粒子独立的初始转角并缓慢自旋：一排排同相的斑块是廉价粒子最明显的破绽。
        float spin=seed*6.283185+age*(seed-0.5)*3.0;
        float spinCos=cos(spin);
        float spinSin=sin(spin);
        shape=float2(corner.x*spinCos-corner.y*spinSin,corner.x*spinSin+corner.y*spinCos);
    }
    // 必须写成三元判断，不能写成除以一个 epsilon：fadeIn 为 0 时 age 也常常是 0，
    // clamp(0/eps) 给出 0 而不是 1，会把整片粒子的覆盖度吃掉。
    float fadeIn=drawParams.x>0.0?clamp(age/drawParams.x,0.0,1.0):1.0;
    float fadeOut=drawParams.y>0.0?clamp((1.0-age)/drawParams.y,0.0,1.0):1.0;
    color.a*=fadeIn*fadeOut;
    // 沿速度方向把四边形切变成平行四边形，总长度为 |velocity| * stretch；
    // 上下两角各偏移半个长度，所以 stretch 为 0 时精确退化回原来的正方形公告板。
    float3 world=center+(right*shape.x+up*shape.y)*size+velocity*(drawParams.z*shape.y*0.5);
    float4x4 viewProjection=float4x4(frameData[0],frameData[1],frameData[2],frameData[3]);

    // No Y flip: Metal clip space matches D3D, so this is the HLSL line verbatim.
    ParticleVertex result;
    result.position=viewProjection*float4(world,1.0);
    // offset 保持未旋转的角点：衰减只取它的平方和，旋转不改变结果，默认路径因此一字未改。
    result.offset=corner;
    result.color=color;
    // (稳定随机数, 归一化年龄, 噪声强度, 加法混合标志)
    result.misc=float4(seed,age,curve.w>0.5?tintMid.w:0.0,tintStart.w);
    return result;
}

""";

    const string ShadeMsl = """
// 无正弦的哈希：sin 在部分硬件上对大参数会丢精度，而这里的输入随年龄一路增大。
float Hash2(float2 p) {
    float3 p3=fract(p.xyx*0.1031);
    p3+=dot(p3,p3.yzx+33.33);
    return fract((p3.x+p3.y)*p3.z);
}
float ValueNoise(float2 p) {
    float2 i=floor(p);
    float2 f=p-i;
    f=f*f*(3.0-2.0*f);
    return mix(mix(Hash2(i),Hash2(i+float2(1.0,0.0)),f.x),
        mix(Hash2(i+float2(0.0,1.0)),Hash2(i+float2(1.0,1.0)),f.x),f.y);
}
float4 Shade(ParticleVertex input) {
    float falloff=clamp(1.0-dot(input.offset,input.offset),0.0,1.0);
    // 默认路径保持原样：曲线模式关闭时 misc.z 为 0，这一行与旧版逐位相同。
    float alpha=input.color.a*falloff*falloff;
    if(input.misc.z>0.0) {
        // 噪声随年龄沿 -y 滚动，火舌与烟卷因此在原地往上走，而不是贴着一动不动。
        float2 q=input.offset*1.7+input.misc.x*31.0+float2(0.0,-input.misc.y*2.5);
        float n=0.65*ValueNoise(q)+0.35*ValueNoise(q*2.3);
        alpha=input.color.a*clamp(falloff*falloff*(1.0+input.misc.z*(n*2.0-1.0)),0.0,1.0);
    }
    // 加法档把 alpha 压到 0：One/InvSrcAlpha 下就等于纯加法，不必多一个 PSO。
    return float4(input.color.rgb*alpha,alpha*(1.0-input.misc.w));
}
""";

    /// <summary>Metal fragment stage for a colour-only scene pass.</summary>
    public const string DrawFragmentMsl = DrawCommonMsl + ShadeMsl + """

fragment float4 PSMain(ParticleVertex input [[stage_in]]) { return Shade(input); }
""";

    /// <summary>Metal fragment stage for the velocity-attachment variant of the scene pass.</summary>
    public const string DrawFragmentVelocityMsl = DrawCommonMsl + ShadeMsl + """

struct ParticleTargets {
    float4 color [[color(0)]];
    float2 velocity [[color(1)]];
};
fragment ParticleTargets PSMain(ParticleVertex input [[stage_in]]) {
    // Slot 1 is write-masked off in the velocity PSO variant, matching how the main pipeline keeps
    // opaque velocity underneath transparent geometry. The value here is never stored.
    ParticleTargets result;
    result.color=Shade(input);
    result.velocity=float2(0.0,0.0);
    return result;
}
""";
}
