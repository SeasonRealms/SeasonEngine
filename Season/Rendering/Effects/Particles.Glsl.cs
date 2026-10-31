// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

/// <summary>
/// Vulkan GLSL mirror of the HLSL sources, compiled to SPIR-V at runtime by the backend.
///
/// Two translation rules govern every line here and both have bitten this engine before.
///
/// Indexing: HLSL ByteAddressBuffer addresses bytes, GLSL std430 arrays address elements. Every
/// offset below is therefore the HLSL byte offset divided by the element stride, written out so the
/// two files can be diffed by eye. Getting this wrong produces no compiler diagnostic on any
/// backend, only silently displaced data.
///
/// Matrices: the frame buffer holds the four *rows* of the view-projection. HLSL float4x4(r0..r3)
/// builds rows and is consumed as mul(v, M); GLSL mat4(r0..r3) builds *columns* and is consumed as
/// M * v. Both evaluate to the same sum over v[i] * r[i], so the same four vectors in the same order
/// give identical results and no transpose is needed.
/// </summary>
public sealed partial class ParticleSystemEffect
{
    /// <summary>Vulkan compute: rewrites the five indirect arguments every frame, zeroing the count the compaction pass will raise.</summary>
    const string ResetGlsl = """
#version 450
layout(push_constant) uniform Params { vec4 resetParams; };
layout(std430,binding=1) buffer Args { uint args[]; };
layout(local_size_x=1,local_size_y=1,local_size_z=1) in;
void main() {
    if(gl_GlobalInvocationID.x==0u) {
        // IndirectDrawIndexedArgs: indexCount / instanceCount / startIndex / baseVertex / startInstance.
        // uint[] indices 0..4 are the HLSL byte offsets 0/4/8/12/16 divided by the 4-byte stride.
        // startInstance stays 0 by contract so gl_InstanceIndex equals the compacted slot.
        args[0]=uint(resetParams.x);
        args[1]=0u;
        args[2]=0u;
        args[3]=0u;
        args[4]=0u;
    }
}
""";

    /// <summary>Vulkan compute: derives one particle per thread, culls it, and appends survivors while raising the indirect instance count.</summary>
    const string CompactGlsl = """
#version 450
// 与 HLSL 共用 96B 参数块，共六个 vec4：
// p0=相位/盒半径/盒高度/预算，p1=原点.xyz/粒径，p2=颜色。
// p3=密度/摆幅/寿命/种子，p4=加速度.xyz/区域数，p5=风速.xyz/通道与窗口标志。
layout(push_constant) uniform Params { vec4 p0,p1,p2,p3,p4,p5; };
layout(std430,binding=1) readonly buffer Frame { vec4 frameData[]; };
layout(std430,binding=2) buffer Instances { vec4 instanceData[]; };
layout(std430,binding=3) buffer Args { uint args[]; };
layout(std430,binding=4) readonly buffer Regions { vec4 regionData[]; };
layout(local_size_x=64,local_size_y=1,local_size_z=1) in;
float Hash(uint n) {
    n=(n<<13u)^n;
    n=n*(n*n*15731u+789221u)+1376312589u;
    return float(n&0x00FFFFFFu)/16777216.0;
}
void main() {
    uint index=gl_GlobalInvocationID.x;
    if(index<uint(p0.w)) {
        index+=uint(p3.w);
        float h0=Hash(index*3u+1u);
        float h1=Hash(index*5u+7u);
        float h2=Hash(index*11u+23u);
        float h3=Hash(index*17u+41u);
        if(h3<p3.x) {
            // 相位以寿命为时标，所以原来的 fall 同时就是 [0,1) 的归一化年龄；换算回秒即可闭式积分。
            // 寿命为 0 时 t 与 invLife 一并取零，位置和速度都精确退回未扩展前的结果。
            float age=fract(p0.x+h2);
            float invLife=p3.z>0.0?1.0/p3.z:0.0;
            float t=age*p3.z;
            vec3 accel=p4.xyz;
            // frameData[11] 是 HLSL Load4(176)：圆盘半径 / 径向速度 / 摆动幅度 / 曲线模式标志。
            vec4 uCurve=frameData[11];

            vec3 center;
            vec3 velocity;
            if((uint(p5.w)&16u)!=0u) {
                // 圆盘出生：半径取 sqrt(h0) 才让面积均匀，否则粒子会挤在圆心堆成一坨。
                float ring=h1*6.283185;
                vec2 radial=vec2(cos(ring),sin(ring));
                float radius=sqrt(h0)*uCurve.x;
                center=vec3(p1.x+radial.x*radius,p1.y,p1.z+radial.y*radius);
                // 圆盘出生没有落差：竖直运动全部来自加速度与风速，火舌的上升速度因此由 AccelY 单独决定。
                velocity=vec3(0.0);
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
                vec2 radial=vec2(cos(ring),sin(ring));
                // 径向扩散与年龄成正比：烟柱因此越往上越宽；火焰用负的 Spread 让火舌向中轴收拢。
                center.xz+=radial*uCurve.y*t;
                velocity.xz+=radial*uCurve.y;
                // 两条不同频率的正弦合成低频摆动，对年龄仍是闭式函数，不引入任何跨帧状态。
                center.xz+=vec2(sin(t*1.3+h2*6.283185),cos(t*1.1+h3*6.283185))*uCurve.z*t;
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
                    vec4 a=regionData[r*3u],b=regionData[r*3u+1u],c=regionData[r*3u+2u];
                    vec2 d=center.xz-a.xy;
                    vec2 local=vec2(b.x*d.x+b.y*d.y,-b.y*d.x+b.x*d.y)*a.zw;
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
            mat4 viewProjection=mat4(frameData[0],frameData[1],frameData[2],frameData[3]);
            vec4 clip=viewProjection*vec4(center,1.0);
            // 用 w 加上公告板半径做横向余量，避免贴边粒子在扩张成四边形后被整体丢弃。
            float margin=clip.w+p1.w*4.0;
            vec4 streak=abs(viewProjection*vec4(velocity*frameData[7].z*0.5,0.0));
            if(weight>0.0 && clip.w+streak.w>1e-4 && clip.z+streak.z>-1e-4
                && abs(clip.x)<margin+streak.x+streak.w && abs(clip.y)<margin+streak.y+streak.w) {
                // 每个线程最多加一，线程数即容量，所以 slot 必然小于容量，无需再夹取。
                uint slot=atomicAdd(args[1],1u);
                // The 48-byte instance stride is three vec4: HLSL offset+0, +16 and +32.
                instanceData[slot*3u]=vec4(center,p1.w);
                // 曲线模式下 rgb 改存两枚稳定随机数（按下标哈希，不受实例槽位争夺影响），
                // 顶点阶段据此选颜色梯度与初始转角；覆盖度仍然只走 alpha，与旧版完全一致。
                vec3 tint=uCurve.w>0.5?vec3(h0,h1,0.0):p2.xyz;
                instanceData[slot*3u+1u]=vec4(tint,p2.w*weight);
                instanceData[slot*3u+2u]=vec4(velocity,age);
            }
        }
    }
}
""";

    // ── Draw-side shaders, consumed by VKParticleDrawPipeline ──
    // No vertex buffer and no input layout, matching the HLSL: the quad corner comes from
    // gl_VertexIndex over the engine's existing unit-quad index buffer and every per-instance value
    // is loaded by gl_InstanceIndex. Bindings are a dedicated set, 0 instances / 1 frame.
    //
    // gl_VertexIndex and gl_InstanceIndex include vertexOffset and firstInstance, which SV_VertexID
    // and SV_InstanceID do not. The IndirectDrawIndexedArgs contract pins both of those arguments to
    // zero -- WebGPU cannot express a non-zero firstInstance without an optional feature -- so the
    // two families agree here. They would diverge the moment that contract were relaxed.

    /// <summary>Vulkan vertex stage for the indirect billboard draw.</summary>
    public const string DrawVertexGlsl = """
#version 450
layout(std430,binding=0) readonly buffer Instances { vec4 instanceData[]; };
layout(std430,binding=1) readonly buffer Frame { vec4 frameData[]; };
layout(location=0) out vec2 vOffset;
layout(location=1) out vec4 vColor;
layout(location=2) out vec4 vMisc;
void main() {
    uint slot=uint(gl_InstanceIndex);
    vec4 packed=instanceData[slot*3u];
    vec3 center=packed.xyz;
    float size=packed.w;
    vec4 color=instanceData[slot*3u+1u];
    vec4 motion=instanceData[slot*3u+2u];
    vec3 velocity=motion.xyz;
    float age=motion.w;

    uint vertexId=uint(gl_VertexIndex);
    vec2 corner=vec2((vertexId&1u)!=0u?1.0:-1.0,(vertexId&2u)!=0u?-1.0:1.0);
    // frameData[4] and [5] are the HLSL Load4(64) and Load4(80): camera right and up.
    vec3 right=frameData[4].xyz;
    vec3 up=frameData[5].xyz;
    // frameData[7] is the HLSL Load4(112): fadeIn / fadeOut (fractions of the lifetime) / stretch in seconds.
    vec3 drawParams=frameData[7].xyz;
    // frameData[8..11] 是外观曲线与出生形状：曲线模式关闭时标志位为 0，下面这个分支不成立，
    // 于是 size、color、corner 三个量与扩容前逐位相同。
    vec4 tintStart=frameData[8];
    vec4 tintMid=frameData[9];
    vec4 tintEnd=frameData[10];
    vec4 curve=frameData[11];
    float seed=0.0;
    vec2 shape=corner;
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
        shape=vec2(corner.x*spinCos-corner.y*spinSin,corner.x*spinSin+corner.y*spinCos);
    }
    // 必须写成三元判断，不能写成除以一个 epsilon：fadeIn 为 0 时 age 也常常是 0，
    // clamp(0/eps) 给出 0 而不是 1，会把整片粒子的覆盖度吃掉。
    float fadeIn=drawParams.x>0.0?clamp(age/drawParams.x,0.0,1.0):1.0;
    float fadeOut=drawParams.y>0.0?clamp((1.0-age)/drawParams.y,0.0,1.0):1.0;
    color.a*=fadeIn*fadeOut;
    // 沿速度方向把四边形切变成平行四边形，总长度为 |velocity| * stretch；
    // 上下两角各偏移半个长度，所以 stretch 为 0 时精确退化回原来的正方形公告板。
    vec3 world=center+(right*shape.x+up*shape.y)*size+velocity*(drawParams.z*shape.y*0.5);
    mat4 viewProjection=mat4(frameData[0],frameData[1],frameData[2],frameData[3]);

    // No Y flip here: Device.BeginPass already binds a negative-height viewport.
    gl_Position=viewProjection*vec4(world,1.0);
    // vOffset 保持未旋转的角点：衰减只取它的平方和，旋转不改变结果，默认路径因此一字未改。
    vOffset=corner;
    vColor=color;
    // (稳定随机数, 归一化年龄, 噪声强度, 加法混合标志)
    vMisc=vec4(seed,age,curve.w>0.5?tintMid.w:0.0,tintStart.w);
}
""";

    const string ShadeGlsl = """
layout(location=0) in vec2 vOffset;
layout(location=1) in vec4 vColor;
layout(location=2) in vec4 vMisc;
// 无正弦的哈希：sin 在部分硬件上对大参数会丢精度，而这里的输入随年龄一路增大。
float Hash2(vec2 p) {
    vec3 p3=fract(p.xyx*0.1031);
    p3+=dot(p3,p3.yzx+33.33);
    return fract((p3.x+p3.y)*p3.z);
}
float ValueNoise(vec2 p) {
    vec2 i=floor(p);
    vec2 f=p-i;
    f=f*f*(3.0-2.0*f);
    return mix(mix(Hash2(i),Hash2(i+vec2(1.0,0.0)),f.x),
        mix(Hash2(i+vec2(0.0,1.0)),Hash2(i+vec2(1.0,1.0)),f.x),f.y);
}
vec4 Shade() {
    float falloff=clamp(1.0-dot(vOffset,vOffset),0.0,1.0);
    // 默认路径保持原样：曲线模式关闭时 vMisc.z 为 0，这一行与旧版逐位相同。
    float alpha=vColor.a*falloff*falloff;
    if(vMisc.z>0.0) {
        // 噪声随年龄沿 -y 滚动，火舌与烟卷因此在原地往上走，而不是贴着一动不动。
        vec2 q=vOffset*1.7+vMisc.x*31.0+vec2(0.0,-vMisc.y*2.5);
        float n=0.65*ValueNoise(q)+0.35*ValueNoise(q*2.3);
        alpha=vColor.a*clamp(falloff*falloff*(1.0+vMisc.z*(n*2.0-1.0)),0.0,1.0);
    }
    // 加法档把 alpha 压到 0：One/InvSrcAlpha 下就等于纯加法，不必多一个 PSO。
    return vec4(vColor.rgb*alpha,alpha*(1.0-vMisc.w));
}
""";

    /// <summary>Vulkan fragment stage for a colour-only scene pass.</summary>
    public const string DrawFragmentGlsl = """
#version 450

""" + ShadeGlsl + """

layout(location=0) out vec4 outColor;
void main() { outColor=Shade(); }
""";

    /// <summary>Vulkan fragment stage for the velocity-attachment variant of the scene pass.</summary>
    public const string DrawFragmentVelocityGlsl = """
#version 450

""" + ShadeGlsl + """

layout(location=0) out vec4 outColor;
layout(location=1) out vec2 outVelocity;
void main() {
    // Slot 1 is write-masked off in the velocity PSO variant, matching how the main pipeline keeps
    // opaque velocity underneath transparent geometry. The value here is never stored.
    outColor=Shade();
    outVelocity=vec2(0.0,0.0);
}
""";
}
