// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

/// <summary>
/// WebGPU WGSL mirror of Cloth.Hlsl.cs, compiled to a GPU module by the browser at runtime. The maths is the HLSL's line for
/// line; the three places it cannot be are called out here because none produces a compiler diagnostic, only a wrong image.
///
/// <para><b>Indexing.</b> HLSL ByteAddressBuffer addresses bytes, WGSL arrays address elements, so every offset below is the
/// HLSL byte offset divided by the 16-byte vec4f stride: rest and state at p and 2p, a vertex at 5p. The five vec4f of one
/// Vertex carry pos.xyz/uv.x, uv.y/normal.xyz, tangent and two zero blocks, i.e. the same 80-byte record the static vertex
/// buffer holds.</para>
///
/// <para><b>No normal array.</b> The HLSL keeps <c>gN[512]</c> in groupshared memory, which with positions and weights would
/// be 18 KB and exceed the 16 KB <c>maxComputeWorkgroupStorageSize</c> every WebGPU implementation must provide. Here each
/// thread holds the four normals it predicted in registers instead: they are computed read-only over the positions, a barrier
/// separates that from the position write, and the two passes after the substep loop recompute the normal from the final, now
/// read-only positions. Workgroup memory therefore holds positions (8192 B), weights (2048 B) and the reset flag (4 B) only -
/// 10244 B.</para>
///
/// <para><b>Reset flag.</b> WGSL forbids a plain data race, so unlike the other three dialects gBad is an atomic. Every writer
/// sets the same bit, and the readers only ask whether it is non-zero, which makes it observably identical to the HLSL
/// <c>gBad = 1u</c> without the race.</para>
///
/// <para><b>Slot contract.</b> The bind group builder uses the index of <see cref="ComputeKernelDesc.Bindings"/> as the binding
/// number, so Params is 0 and the buffers keep 1 through 5, as in the other three dialects.</para>
/// </summary>
public sealed partial class ClothSimulationEffect
{
    const string WgslSource = """
const THREADS: u32 = 128u;
const PER: u32 = 4u;
const MAXP: u32 = 512u;

// Shares the 112-byte parameter block with the HLSL, seven vec4 in all:
// p[0] = wind.xyz / dt, p[1] = gust / phase / wavenumber / reset, p[2] = cos yaw / sin yaw / pole radius / damping,
// p[3] = shear / bend / normal drag / tangent drag, p[4] = gravity y / half thickness / spacing / front-u flip,
// p[5] = the mesh centre subtracted from every output vertex, grid = columns / rows / substeps / unused.
// grid is a vec4u, not a vec4f: the CPU packs the four of them as integer bits, and reading them as floats gives 0.
struct Params {
    p: array<vec4f,6>,
    grid: vec4u,
};
struct Rest { values: array<vec4f> };
struct State { values: array<vec4f> };
struct VertexBuf { values: array<vec4f> };

@group(0) @binding(0) var<uniform> params: Params;
@group(0) @binding(1) var<storage,read> restData: Rest;              // rest.xyz, inverse mass (0 = pinned)
@group(0) @binding(2) var<storage,read_write> stateData: State;      // pos (2p), vel (2p + 1), resident across frames
@group(0) @binding(3) var<storage,read_write> frontData: VertexBuf;  // Vertex (5 vec4) per particle, the +normal side
@group(0) @binding(4) var<storage,read_write> backData: VertexBuf;   // Vertex (5 vec4) per particle, the -normal side
@group(0) @binding(5) var<storage,read_write> rimData: VertexBuf;    // Vertex (5 vec4) x 2 per boundary point

var<workgroup> gPos: array<vec3f,MAXP>;
var<workgroup> gW: array<f32,MAXP>;
var<workgroup> gBad: atomic<u32>;

fn Swivel(v: vec3f) -> vec3f {
    return vec3f(v.x*params.p[2].x-v.z*params.p[2].y,v.y,v.x*params.p[2].y+v.z*params.p[2].x);
}

// Inf/NaN by bit pattern: a NaN comparison may be folded away, an exponent test may not.
fn NotFinite(v: vec3f) -> bool {
    let e=bitcast<vec3u>(v)&vec3u(0x7f800000u);
    return any(e==vec3u(0x7f800000u));
}

fn Col(p: u32) -> u32 { return p%params.grid.x; }

fn Row(p: u32) -> u32 { return p/params.grid.x; }

// Centre differences clamped at the border, cross(dv, du): +Z for the sheet at rest, the side the front picture faces.
fn GridNormal(p: u32) -> vec3f {
    let cols=params.grid.x;
    let i=Col(p);
    let j=Row(p);
    let ip=min(i+1u,cols-1u);
    let im=select(0u,i-1u,i>0u);
    let jp=min(j+1u,params.grid.y-1u);
    let jm=select(0u,j-1u,j>0u);
    let du=gPos[j*cols+ip]-gPos[j*cols+im];
    let dv=gPos[jp*cols+i]-gPos[jm*cols+i];
    let n=cross(dv,du);
    let len2=dot(n,n);
    return select(vec3f(0.0,0.0,1.0),n*inverseSqrt(len2),len2>1e-24);
}

fn GridTangent(p: u32) -> vec3f {
    let cols=params.grid.x;
    let i=Col(p);
    let j=Row(p);
    let ip=min(i+1u,cols-1u);
    let im=select(0u,i-1u,i>0u);
    let du=gPos[j*cols+ip]-gPos[j*cols+im];
    let len2=dot(du,du);
    return select(vec3f(1.0,0.0,0.0),du*inverseSqrt(len2),len2>1e-24);
}

// One Vertex is five vec4f, written whole so the tail stays zeroed: HLSL byte offsets 0/16/32/48/64.
fn StoreFront(index: u32,pos: vec3f,uv: vec2f,nrm: vec3f,tan: vec4f) {
    let o=index*5u;
    frontData.values[o+0u]=vec4f(pos,uv.x);
    frontData.values[o+1u]=vec4f(uv.y,nrm);
    frontData.values[o+2u]=tan;
    frontData.values[o+3u]=vec4f(0.0);
    frontData.values[o+4u]=vec4f(0.0);
}

fn StoreBack(index: u32,pos: vec3f,uv: vec2f,nrm: vec3f,tan: vec4f) {
    let o=index*5u;
    backData.values[o+0u]=vec4f(pos,uv.x);
    backData.values[o+1u]=vec4f(uv.y,nrm);
    backData.values[o+2u]=tan;
    backData.values[o+3u]=vec4f(0.0);
    backData.values[o+4u]=vec4f(0.0);
}

fn StoreRim(index: u32,pos: vec3f,uv: vec2f,nrm: vec3f,tan: vec4f) {
    let o=index*5u;
    rimData.values[o+0u]=vec4f(pos,uv.x);
    rimData.values[o+1u]=vec4f(uv.y,nrm);
    rimData.values[o+2u]=tan;
    rimData.values[o+3u]=vec4f(0.0);
    rimData.values[o+4u]=vec4f(0.0);
}

// Boundary walk, clockwise in (c, t): top hoist to fly, fly top to bottom, bottom fly to hoist, hoist bottom to top.
fn LoopParticle(k: u32) -> u32 {
    let cols=params.grid.x;
    let rows=params.grid.y;
    let top=cols-1u;
    let side=rows-1u;
    var p=0u;
    if(k<top) { p=k; }
    else if(k<top+side) { p=(k-top)*cols+(cols-1u); }
    else if(k<2u*top+side) { p=(rows-1u)*cols+(cols-1u-(k-top-side)); }
    else { p=(rows-1u-(k-2u*top-side))*cols; }
    return p;
}

@compute @workgroup_size(128,1,1) fn CSMain(@builtin(local_invocation_id) tid3: vec3u) {
    let tid=tid3.x;
    let windDt=params.p[0];
    let gust=params.p[1];
    let swivel=params.p[2];
    let material=params.p[3];
    let shape=params.p[4];
    let origin=params.p[5];
    let cols=params.grid.x;
    let rows=params.grid.y;
    let count=min(cols*rows,MAXP);
    let substeps=max(params.grid.z,1u);
    let h=windDt.w/f32(substeps);
    let invH=1.0/h;
    let invH2=invH*invH;
    let spacing=shape.z;
    let gravity=vec3f(0.0,shape.x,0.0);
    let damp=exp(-swivel.w*h);
    let kN=material.z;
    let kT=material.w;
    let collide=swivel.z;

    if(tid==0u) { atomicStore(&gBad,0u); }

    var prev: array<vec3f,PER>;
    var pinned: array<vec3f,PER>;
    var weff: array<vec3f,PER>;

    // Sideways unit vector in the horizontal plane: the flutter gust blows across the wind, never along it.
    let wind=windDt.xyz;
    let horizontal=length(wind.xz);
    let side=select(vec3f(0.0),vec3f(-wind.z,0.0,wind.x)/max(horizontal,1e-6),horizontal>1e-6);

    for(var k0:u32=0u;k0<PER;k0++) {
        let p=tid+k0*THREADS;
        prev[k0]=vec3f(0.0);
        pinned[k0]=vec3f(0.0);
        weff[k0]=wind;
        if(p<count) {
            let rest=restData.values[p];
            pinned[k0]=Swivel(rest.xyz);
            gW[p]=rest.w;
            let pos=stateData.values[p*2u].xyz;
            let vel=stateData.values[p*2u+1u].xyz;
            gPos[p]=pos;
            prev[k0]=vel;   // velocity until the reset decision below, then the previous position
            let c=f32(Col(p))/f32(cols-1u);
            weff[k0]=wind+gust.x*sin(gust.y-gust.z*c)*side;
        }
    }
    workgroupBarrier();

    // Reset on request, on a non-finite particle, or on one that has wandered off the pole (10 m), all as one sheet:
    // resetting some particles and not others would tear the constraints apart on the next substep.
    for(var k1:u32=0u;k1<PER;k1++) {
        let p=tid+k1*THREADS;
        if(p<count) {
            let d=gPos[p]-pinned[k1];
            if(NotFinite(gPos[p])||NotFinite(prev[k1])||!(dot(d,d)<=100.0)) { atomicOr(&gBad,1u); }
        }
    }
    workgroupBarrier();
    let reset=gust.w>0.5||atomicLoad(&gBad)!=0u;

    for(var k2:u32=0u;k2<PER;k2++) {
        let p=tid+k2*THREADS;
        if(p<count) {
            if(reset) {
                gPos[p]=pinned[k2];
                prev[k2]=pinned[k2];
            } else {
                prev[k2]=gPos[p]-prev[k2]*h;
            }
        }
    }
    workgroupBarrier();

    for(var s:u32=0u;s<substeps;s++) {
        // Normals first, read-only over the positions, and kept in registers: the barrier below is what lets the whole sheet
        // predict from one consistent normal field without a second workgroup array.
        var nrm: array<vec3f,PER>;
        for(var ka:u32=0u;ka<PER;ka++) {
            let p=tid+ka*THREADS;
            if(p<count) { nrm[ka]=GridNormal(p); }
        }
        workgroupBarrier();

        // Predict: aerodynamic split on the relative wind, gravity, damping. Pins follow the swivel instead.
        for(var kb:u32=0u;kb<PER;kb++) {
            let p=tid+kb*THREADS;
            if(p<count) {
                let P=gPos[p];
                var v=(P-prev[kb])*invH;
                let n=nrm[kb];
                let u=weff[kb]-v;
                let un=dot(u,n);
                let a=kN*un*n+kT*(u-un*n);
                v=(v+h*(gravity+a))*damp;
                prev[kb]=P;
                gPos[p]=select(pinned[kb],P+h*v,gW[p]>0.0);
            }
        }
        workgroupBarrier();

        // Twelve colours, each a set of disjoint edges owned by their first endpoint: stretch H and V, shear in both
        // diagonals, bend H and V over two cells, each split by parity. Jacobi inside a colour, Gauss-Seidel across.
        for(var colour:u32=0u;colour<12u;colour++) {
            let kind=colour>>1u;
            let parity=colour&1u;
            for(var kc:u32=0u;kc<PER;kc++) {
                let p=tid+kc*THREADS;
                if(p<count) {
                    let i=Col(p);
                    let j=Row(p);
                    var on=false;
                    var q=p;
                    var rest=spacing;
                    var compliance=0.0;
                    if(kind==0u) { on=i+1u<cols&&(i&1u)==parity; q=p+1u; }
                    else if(kind==1u) { on=j+1u<rows&&(j&1u)==parity; q=p+cols; }
                    else if(kind==2u) { on=i+1u<cols&&j+1u<rows&&(i&1u)==parity; q=p+cols+1u; rest=spacing*1.41421356; compliance=material.x; }
                    else if(kind==3u) { on=i>=1u&&j+1u<rows&&(i&1u)==parity; q=p+cols-1u; rest=spacing*1.41421356; compliance=material.x; }
                    else if(kind==4u) { on=i+2u<cols&&((i>>1u)&1u)==parity; q=p+2u; rest=spacing*2.0; compliance=material.y; }
                    else { on=j+2u<rows&&((j>>1u)&1u)==parity; q=p+2u*cols; rest=spacing*2.0; compliance=material.y; }

                    if(on) {
                        let wp=gW[p];
                        let wq=gW[q];
                        let d=gPos[p]-gPos[q];
                        let len=length(d);
                        let wsum=wp+wq;
                        if(len>1e-6&&wsum>0.0) {
                            let dl=-(len-rest)/(wsum+compliance*invH2);
                            let g=d/len;
                            gPos[p]=gPos[p]+(wp*dl)*g;
                            gPos[q]=gPos[q]-(wq*dl)*g;
                        }
                    }
                }
            }
            workgroupBarrier();
        }

        // Pole: push free particles out of the cylinder around the simulation-space Y axis.
        for(var kd:u32=0u;kd<PER;kd++) {
            let p=tid+kd*THREADS;
            if(p<count&&gW[p]>0.0) {
                var P=gPos[p];
                let r=length(P.xz);
                if(r<collide&&r>1e-6) {
                    P=vec3f(P.x*collide/r,P.y,P.z*collide/r);
                    gPos[p]=P;
                }
            }
        }
        workgroupBarrier();
    }

    // The positions are final and read-only from here on, so the two output passes take their normals straight from them.
    let halfThick=shape.y;
    let flipFront=shape.w>0.5;
    for(var ke:u32=0u;ke<PER;ke++) {
        let p=tid+ke*THREADS;
        if(p<count) {
            let P=gPos[p];
            let vel=(P-prev[ke])*invH;
            stateData.values[p*2u]=vec4f(P,0.0);
            stateData.values[p*2u+1u]=vec4f(vel,0.0);

            let n=GridNormal(p);
            let du=GridTangent(p);
            let c=f32(Col(p))/f32(cols-1u);
            let t=f32(Row(p))/f32(rows-1u);
            let local=P-origin.xyz;
            StoreFront(p,local+halfThick*n,vec2f(select(c,1.0-c,flipFront),t),n,vec4f(select(du,-du,flipFront),1.0));
            StoreBack(p,local-halfThick*n,vec2f(c,t),-n,vec4f(du,1.0));
        }
    }

    // Rim: two vertices per boundary point, the in-plane outward normal cross(n, T) with T the loop's centre difference.
    let loopLength=2u*(cols-1u)+2u*(rows-1u);
    for(var k:u32=tid;k<loopLength;k=k+THREADS) {
        let p=LoopParticle(k);
        let pn=LoopParticle(select(0u,k+1u,k+1u<loopLength));
        let pp=LoopParticle(select(loopLength-1u,k-1u,k>0u));
        let n=GridNormal(p);
        let T=gPos[pn]-gPos[pp];
        var o=cross(n,T);
        let len2=dot(o,o);
        o=select(vec3f(0.0,1.0,0.0),o*inverseSqrt(len2),len2>1e-24);
        let c=f32(Col(p))/f32(cols-1u);
        let t=f32(Row(p))/f32(rows-1u);
        let local=gPos[p]-origin.xyz;
        let tangent=vec4f(T*inverseSqrt(max(dot(T,T),1e-24)),1.0);
        StoreRim(2u*k,local+halfThick*n,vec2f(c,t),o,tangent);
        StoreRim(2u*k+1u,local-halfThick*n,vec2f(c,t),o,tangent);
    }
}
""";
}
