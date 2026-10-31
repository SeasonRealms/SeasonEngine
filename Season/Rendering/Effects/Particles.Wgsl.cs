// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

/// <summary>
/// WebGPU WGSL mirror of the HLSL sources, compiled to GPU modules by the browser at runtime.
///
/// Four translation rules govern every line here.
///
/// Indexing: HLSL ByteAddressBuffer addresses bytes, WGSL arrays address elements. Every offset below
/// is the HLSL byte offset divided by the element stride, written out so the files can be diffed by
/// eye. Getting this wrong produces no diagnostic on any backend, only displaced data.
///
/// Matrices: the frame buffer holds the four *rows* of the view-projection. HLSL float4x4(r0..r3)
/// builds rows and is consumed as mul(v, M); WGSL mat4x4f(r0..r3) builds *columns* and is consumed as
/// M * v. Both evaluate to the same sum over v[i] * r[i], so no transpose is needed -- the same
/// cancellation already documented for the GLSL and MSL mirrors.
///
/// Slots: WGSL has no implicit binding order, so every slot is spelled out. The compute bindings must
/// match what Graphics.CreateComputeKernel hands to the JS layout builder, which uses the array index
/// of ComputeKernelDesc.Bindings as the binding number -- Params at 0, then every buffer in
/// declaration order. The draw side is bound by WGPUParticleDrawPipeline, group 0 binding 0 =
/// instances and binding 1 = frame.
///
/// Atomics: WGSL cannot alias one buffer as both plain and atomic within a single shader, and
/// atomicAdd demands a pointer into an atomic-typed slot. The counter therefore lives in a
/// five-element atomic array for the compaction kernel and a five-element plain array for the reset
/// kernel. Both views describe the same 20-byte <see cref="IndirectDrawIndexedArgs"/> block, and the
/// two kernels never run concurrently, so the aliasing is safe. Fixed length rather than
/// runtime-sized is deliberate: it pins the block to exactly the five fields the contract defines.
/// </summary>
public sealed partial class ParticleSystemEffect
{
    /// <summary>WebGPU compute: rewrites the five indirect arguments every frame, zeroing the count the compaction pass will raise.</summary>
    const string ResetWgsl = """
struct Params { resetParams: vec4f }
struct Args { values: array<u32,5> }
@group(0) @binding(0) var<uniform> params: Params;
@group(0) @binding(1) var<storage,read_write> args: Args;
@compute @workgroup_size(1,1,1) fn CSMain(@builtin(global_invocation_id) id: vec3u) {
    if(id.x==0u) {
        // IndirectDrawIndexedArgs: indexCount / instanceCount / startIndex / baseVertex / startInstance.
        // u32 indices 0..4 are the HLSL byte offsets 0/4/8/12/16 divided by the 4-byte stride.
        // startInstance stays 0 by contract -- WebGPU rejects a non-zero firstInstance without the
        // optional indirect-first-instance feature -- so instance_index equals the compacted slot.
        args.values[0]=u32(params.resetParams.x);
        args.values[1]=0u;
        args.values[2]=0u;
        args.values[3]=0u;
        args.values[4]=0u;
    }
}
""";

    /// <summary>WebGPU compute: derives one particle per thread, culls it, and appends survivors while raising the indirect instance count.</summary>
    const string CompactWgsl = """
// 与 HLSL 共用 96B 参数块，共六个 vec4f：
// p[0]=相位/盒半径/盒高度/预算，p[1]=原点.xyz/粒径，p[2]=颜色。
// p[3]=密度/摆幅/寿命/种子，p[4]=加速度.xyz/区域数，p[5]=风速.xyz/通道与窗口标志。
struct Params { p: array<vec4f,6> }
struct Frame { values: array<vec4f> }
struct Instances { values: array<vec4f> }
struct Args { values: array<atomic<u32>,5> }
@group(0) @binding(0) var<uniform> params: Params;
@group(0) @binding(1) var<storage,read> frameData: Frame;
@group(0) @binding(2) var<storage,read_write> instanceData: Instances;
@group(0) @binding(3) var<storage,read_write> args: Args;
@group(0) @binding(4) var<storage,read> regionData: Frame;
fn ParticleHash(seed: u32) -> f32 {
    var n=(seed<<13u)^seed;
    n=n*(n*n*15731u+789221u)+1376312589u;
    return f32(n&0x00FFFFFFu)/16777216.0;
}
@compute @workgroup_size(64,1,1) fn CSMain(@builtin(global_invocation_id) id: vec3u) {
    let p0=params.p[0]; let p1=params.p[1]; let p2=params.p[2]; let p3=params.p[3]; let p4=params.p[4];
    let p5=params.p[5];
    if(id.x<u32(p0.w)) {
        let index=id.x+u32(p3.w);
        let h0=ParticleHash(index*3u+1u);
        let h1=ParticleHash(index*5u+7u);
        let h2=ParticleHash(index*11u+23u);
        let h3=ParticleHash(index*17u+41u);
        if(h3<p3.x) {
            // 相位以寿命为时标，所以原来的 fall 同时就是 [0,1) 的归一化年龄；换算回秒即可闭式积分。
            // 寿命为 0 时 t 与 invLife 一并取零，位置和速度都精确退回未扩展前的结果。
            // WGSL 没有三元运算符，而 select() 会把两个分支都算出来，所以这里用语句形式写，
            // 顺带避免对 0 取倒数这件事进入任何一条求值路径。
            let age=fract(p0.x+h2);
            var invLife=0.0;
            if(p3.z>0.0) { invLife=1.0/p3.z; }
            let t=age*p3.z;
            let accel=p4.xyz;
            // frameData[11] 是 HLSL Load4(176)：圆盘半径 / 径向速度 / 摆动幅度 / 曲线模式标志。
            let uCurve=frameData.values[11];

            var center=vec3f(0.0);
            var velocity=vec3f(0.0);
            if((u32(p5.w)&16u)!=0u) {
                // 圆盘出生：半径取 sqrt(h0) 才让面积均匀，否则粒子会挤在圆心堆成一坨。
                let ring=h1*6.283185;
                let radial=vec2f(cos(ring),sin(ring));
                let radius=sqrt(h0)*uCurve.x;
                center=vec3f(p1.x+radial.x*radius,p1.y,p1.z+radial.y*radius);
                // 圆盘出生没有落差：竖直运动全部来自加速度与风速，火舌的上升速度因此由 AccelY 单独决定。
            } else {
                center=vec3f(
                    p1.x+(h0*2.0-1.0)*p0.y+sin((p0.x+h0)*6.283185)*p3.y,
                    p1.y+p0.z*(0.7-age),
                    p1.z+(h1*2.0-1.0)*p0.y+cos((p0.x+h1)*6.283185)*p3.y);
                // 基础速度是上面三行对时间求导：相位速率就是 1/life，所以每项都带一个 invLife。
                // p0.z*invLife 在默认档恰好等于 FallSpeed，因为 life 本就是 boxHeight / FallSpeed。
                velocity=vec3f(
                    cos((p0.x+h0)*6.283185)*6.283185*p3.y*invLife,
                    -p0.z*invLife,
                    -sin((p0.x+h1)*6.283185)*6.283185*p3.y*invLife);
            }
            // 加速度位移必须在构造 clip 之前并入，否则剔除判据用的位置与最终光栅化的位置不是同一个点。
            center+=0.5*accel*t*t;
            velocity+=accel*t;
            center+=p5.xyz*t;
            velocity+=p5.xyz;
            if((u32(p5.w)&16u)!=0u) {
                let ring=h1*6.283185;
                let spread=vec2f(cos(ring),sin(ring))*uCurve.y;
                // 径向扩散与年龄成正比：烟柱因此越往上越宽；火焰用负的 Spread 让火舌向中轴收拢。
                center=vec3f(center.x+spread.x*t,center.y,center.z+spread.y*t);
                velocity=vec3f(velocity.x+spread.x,velocity.y,velocity.z+spread.y);
                // 两条不同频率的正弦合成低频摆动，对年龄仍是闭式函数，不引入任何跨帧状态。
                let wobble=vec2f(sin(t*1.3+h2*6.283185),cos(t*1.1+h3*6.283185))*uCurve.z*t;
                center=vec3f(center.x+wobble.x,center.y,center.z+wobble.y);
            }
            if((u32(p5.w)&4u)!=0u) {
                let period=2.0*p0.y;
                let wrapped=center.xz-floor((center.xz-frameData.values[6].xz+p0.y)/period)*period;
                center=vec3f(wrapped.x,center.y,wrapped.y);
            }
            let channel=u32(p5.w)&3u;
            var weight=1.0;
            if(channel!=0u) {
                weight=0.0;
                var remaining=1.0;
                for(var r=0u;r<u32(p4.w);r++) {
                    let a=regionData.values[r*3u];
                    let b=regionData.values[r*3u+1u];
                    let c=regionData.values[r*3u+2u];
                    let d=center.xz-a.xy;
                    let local=vec2f(b.x*d.x+b.y*d.y,-b.y*d.x+b.x*d.y)*a.zw;
                    var m=clamp((1.0-length(local))/b.z,0.0,1.0);
                    m=m*m*(3.0-2.0*m);
                    if(center.y<c.x || center.y>c.y) { m=0.0; }
                    weight+=remaining*m*select(c.w,c.z,channel==1u);
                    remaining*=1.0-m;
                }
            }

            // 周期窗口的搬运发生在不可见边缘；强度变化只渐显新候选，不整场重置。
            let viewDistance=frameData.values[7].w;
            if(viewDistance>0.0) {
                let f=clamp((1.0-length(center-frameData.values[6].xyz)/viewDistance)*4.0,0.0,1.0);
                weight*=f*f*(3.0-2.0*f);
            }
            if((u32(p5.w)&8u)!=0u) { weight*=clamp((p3.x-h3)*50.0,0.0,1.0); }

            // frameData[0..3] are the HLSL Load4(0/16/32/48): the four rows of the view-projection.
            let viewProjection=mat4x4f(frameData.values[0],frameData.values[1],frameData.values[2],frameData.values[3]);
            let clip=viewProjection*vec4f(center,1.0);
            // 用 w 加上公告板半径做横向余量，避免贴边粒子在扩张成四边形后被整体丢弃。
            let margin=clip.w+p1.w*4.0;
            let streak=abs(viewProjection*vec4f(velocity*frameData.values[7].z*0.5,0.0));
            if(weight>0.0 && clip.w+streak.w>1e-4 && clip.z+streak.z>-1e-4
                && abs(clip.x)<margin+streak.x+streak.w && abs(clip.y)<margin+streak.y+streak.w) {
                // 每个线程最多加一，线程数即容量，所以 slot 必然小于容量，无需再夹取。
                let slot=atomicAdd(&args.values[1],1u);
                // The 48-byte instance stride is three vec4f: HLSL offset+0, +16 and +32.
                instanceData.values[slot*3u]=vec4f(center,p1.w);
                // 曲线模式下 rgb 改存两枚稳定随机数（按下标哈希，不受实例槽位争夺影响），
                // 顶点阶段据此选颜色梯度与初始转角；覆盖度仍然只走 alpha，与旧版完全一致。
                let tint=select(p2.xyz,vec3f(h0,h1,0.0),uCurve.w>0.5);
                instanceData.values[slot*3u+1u]=vec4f(tint,p2.w*weight);
                instanceData.values[slot*3u+2u]=vec4f(velocity,age);
            }
        }
    }
}
""";

    // ── Draw-side shaders, consumed by WGPUParticleDrawPipeline ──
    // No vertex buffer and no vertex attributes, matching the HLSL: the quad corner comes from
    // vertex_index over a unit-quad index buffer and every per-instance value is loaded by
    // instance_index out of a read-only storage buffer bound to the vertex stage.
    //
    // instance_index includes firstInstance, unlike SV_InstanceID. The IndirectDrawIndexedArgs
    // contract pins firstInstance to zero precisely because this backend cannot express anything else
    // without an optional feature, so all four families agree on the slot number.
    //
    // Vertex and fragment are separate modules rather than one, because the fragment side has two
    // variants and WGSL has no preprocessor: the single-target and velocity-target entry points would
    // otherwise both have to live in a module whose attachment count is fixed at pipeline bake time.

    const string DrawCommonWgsl = """
struct ParticleVertex {
    @builtin(position) position: vec4f,
    @location(0) offset: vec2f,
    @location(1) color: vec4f,
    // (稳定随机数, 归一化年龄, 噪声强度, 加法混合标志)。片元阶段读不到 vertex 可见的绑定组，
    // 所以曲线模式的外观参数跟着插值器过来，而不是再去读帧缓冲。
    @location(2) misc: vec4f,
}

""";

    /// <summary>WebGPU vertex stage for the indirect billboard draw.</summary>
    public const string DrawVertexWgsl = DrawCommonWgsl + """
struct Instances { values: array<vec4f> }
struct Frame { values: array<vec4f> }
@group(0) @binding(0) var<storage,read> instanceData: Instances;
@group(0) @binding(1) var<storage,read> frameData: Frame;
@vertex fn VSMain(@builtin(vertex_index) vertexId: u32,@builtin(instance_index) slot: u32) -> ParticleVertex {
    let packed=instanceData.values[slot*3u];
    let center=packed.xyz;
    var size=packed.w;
    var color=instanceData.values[slot*3u+1u];
    let motion=instanceData.values[slot*3u+2u];
    let velocity=motion.xyz;
    let age=motion.w;

    let corner=vec2f(select(-1.0,1.0,(vertexId&1u)!=0u),select(1.0,-1.0,(vertexId&2u)!=0u));
    // frameData[4] and [5] are the HLSL Load4(64) and Load4(80): camera right and up.
    let right=frameData.values[4].xyz;
    let up=frameData.values[5].xyz;
    // frameData[7] is the HLSL Load4(112): fadeIn / fadeOut (fractions of the lifetime) / stretch in seconds.
    let drawParams=frameData.values[7].xyz;
    // frameData[8..11] 是外观曲线与出生形状：曲线模式关闭时标志位为 0，下面这个分支不成立，
    // 于是 size、color、corner 三个量与扩容前逐位相同。
    let tintStart=frameData.values[8];
    let tintMid=frameData.values[9];
    let tintEnd=frameData.values[10];
    let curve=frameData.values[11];
    var seed=0.0;
    var shape=corner;
    if(curve.w>0.5) {
        // 实例 rgb 是压缩阶段写入的两枚稳定随机数：按粒子下标哈希，不受实例槽位争夺影响。
        seed=color.r;
        // 两段线性梯度：前半程起点→中点，后半程中点→终点。WGSL 的 select 会两边都求值，
        // 所以这里用 if/else 分开写。
        var tint=vec3f(0.0);
        if(age<0.5) { tint=mix(tintStart.rgb,tintMid.rgb,age*2.0); }
        else { tint=mix(tintMid.rgb,tintEnd.rgb,age*2.0-1.0); }
        color=vec4f(tint,color.a);
        size=mix(size,tintEnd.w,age);
        // 每颗粒子独立的初始转角并缓慢自旋：一排排同相的斑块是廉价粒子最明显的破绽。
        let spin=seed*6.283185+age*(seed-0.5)*3.0;
        let spinCos=cos(spin);
        let spinSin=sin(spin);
        shape=vec2f(corner.x*spinCos-corner.y*spinSin,corner.x*spinSin+corner.y*spinCos);
    }
    // 语句形式而非 select()：fadeIn 为 0 时 age 也常常是 0，select 会先把 0/0 算出来，
    // 而 clamp(NaN) 的结果由实现决定；用 if 则那条除法根本不会被求值。
    var fadeIn=1.0;
    if(drawParams.x>0.0) { fadeIn=clamp(age/drawParams.x,0.0,1.0); }
    var fadeOut=1.0;
    if(drawParams.y>0.0) { fadeOut=clamp((1.0-age)/drawParams.y,0.0,1.0); }
    color.a*=fadeIn*fadeOut;
    // 沿速度方向把四边形切变成平行四边形，总长度为 |velocity| * stretch；
    // 上下两角各偏移半个长度，所以 stretch 为 0 时精确退化回原来的正方形公告板。
    let world=center+(right*shape.x+up*shape.y)*size+velocity*(drawParams.z*shape.y*0.5);
    let viewProjection=mat4x4f(frameData.values[0],frameData.values[1],frameData.values[2],frameData.values[3]);

    // No Y flip: WebGPU clip space matches D3D, so this is the HLSL line verbatim.
    var result: ParticleVertex;
    result.position=viewProjection*vec4f(world,1.0);
    // offset 保持未旋转的角点：衰减只取它的平方和，旋转不改变结果，默认路径因此一字未改。
    result.offset=corner;
    result.color=color;
    // (稳定随机数, 归一化年龄, 噪声强度, 加法混合标志)
    result.misc=vec4f(seed,age,select(0.0,tintMid.w,curve.w>0.5),tintStart.w);
    return result;
}

""";

    const string ShadeWgsl = """
// 无正弦的哈希：sin 在部分硬件上对大参数会丢精度，而这里的输入随年龄一路增大。
fn Hash2(p: vec2f) -> f32 {
    var p3=fract(p.xyx*0.1031);
    p3+=dot(p3,p3.yzx+33.33);
    return fract((p3.x+p3.y)*p3.z);
}
fn ValueNoise(p: vec2f) -> f32 {
    let i=floor(p);
    let d=p-i;
    let f=d*d*(3.0-2.0*d);
    return mix(mix(Hash2(i),Hash2(i+vec2f(1.0,0.0)),f.x),
        mix(Hash2(i+vec2f(0.0,1.0)),Hash2(i+vec2f(1.0,1.0)),f.x),f.y);
}
fn Shade(input: ParticleVertex) -> vec4f {
    let falloff=clamp(1.0-dot(input.offset,input.offset),0.0,1.0);
    // 默认路径保持原样：曲线模式关闭时 misc.z 为 0，这一行与旧版逐位相同。
    var alpha=input.color.a*falloff*falloff;
    if(input.misc.z>0.0) {
        // 噪声随年龄沿 -y 滚动，火舌与烟卷因此在原地往上走，而不是贴着一动不动。
        let q=input.offset*1.7+input.misc.x*31.0+vec2f(0.0,-input.misc.y*2.5);
        let n=0.65*ValueNoise(q)+0.35*ValueNoise(q*2.3);
        alpha=input.color.a*clamp(falloff*falloff*(1.0+input.misc.z*(n*2.0-1.0)),0.0,1.0);
    }
    // 加法档把 alpha 压到 0：One/InvSrcAlpha 下就等于纯加法，不必多一个 PSO。
    return vec4f(input.color.rgb*alpha,alpha*(1.0-input.misc.w));
}
""";

    /// <summary>WebGPU fragment stage for a colour-only scene pass.</summary>
    public const string DrawFragmentWgsl = DrawCommonWgsl + ShadeWgsl + """

@fragment fn PSMain(input: ParticleVertex) -> @location(0) vec4f { return Shade(input); }
""";

    /// <summary>WebGPU fragment stage for the velocity-attachment variant of the scene pass.</summary>
    public const string DrawFragmentVelocityWgsl = DrawCommonWgsl + ShadeWgsl + """

struct ParticleTargets {
    @location(0) color: vec4f,
    @location(1) velocity: vec2f,
}
@fragment fn PSMain(input: ParticleVertex) -> ParticleTargets {
    // Slot 1 is write-masked off in the velocity pipeline variant, matching how the main pipeline
    // keeps opaque velocity underneath transparent geometry. The value here is never stored.
    var result: ParticleTargets;
    result.color=Shade(input);
    result.velocity=vec2f(0.0,0.0);
    return result;
}
""";
}
