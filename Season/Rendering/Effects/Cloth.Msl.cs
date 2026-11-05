// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

/// <summary>
/// Metal MSL mirror of Cloth.Hlsl.cs, compiled to an IMTLLibrary at runtime by the backend. The maths is the HLSL's line for
/// line; the two places it cannot be are called out here because neither produces a compiler diagnostic, only a wrong image.
///
/// <para><b>Indexing.</b> HLSL ByteAddressBuffer addresses bytes, MSL pointers address elements, so every offset below is the
/// HLSL byte offset divided by the 16-byte float4 stride: rest and state at p and 2p, a vertex at 5p. The five float4 of one
/// Vertex carry pos.xyz/uv.x, uv.y/normal.xyz, tangent and two zero blocks, i.e. the same 80-byte record the static vertex
/// buffer holds.</para>
///
/// <para><b>No normal array.</b> The HLSL keeps <c>gN[512]</c> in groupshared memory, which with positions and weights would
/// be 18 KB and exceed the 16 KB of threadgroup memory an older Apple GPU guarantees. Here each thread holds the four normals
/// it predicted in registers instead: they are computed read-only over the positions, a barrier separates that from the
/// position write, and the two passes after the substep loop recompute the normal from the final, now read-only positions.
/// Threadgroup memory therefore holds positions (8192 B), weights (2048 B) and the reset flag (4 B) only - 10244 B.</para>
///
/// <para><b>Slot contract.</b> Params is a SetBytes block at buffer(0), so the buffers keep the number GLSL and HLSL use:
/// rest 1, state 2, front 3, back 4, rim 5. See <see cref="ComputeBindingType"/>.</para>
/// </summary>
public sealed partial class ClothSimulationEffect
{
    const string MslSource = """
#include <metal_stdlib>
using namespace metal;
#define THREADS 128
#define PER 4
#define MAXP 512
// Shares the 112-byte parameter block with the HLSL, seven float4 in all:
// windDt = wind.xyz / dt, gust = gust / phase / wavenumber / reset, swivel = cos yaw / sin yaw / pole radius / damping,
// material = shear / bend / normal drag / tangent drag, shape = gravity y / half thickness / spacing / front-u flip,
// origin = the mesh centre subtracted from every output vertex, grid = columns / rows / substeps / unused.
// grid is a uint4, not a float4: the CPU packs the four of them as integer bits, and reading them as floats gives 0.
struct ClothParams {
    float4 windDt;
    float4 gust;
    float4 swivel;
    float4 material;
    float4 shape;
    float4 origin;
    uint4 grid;
};

static float3 Swivel(constant ClothParams& u,float3 v) {
    return float3(v.x*u.swivel.x-v.z*u.swivel.y,v.y,v.x*u.swivel.y+v.z*u.swivel.x);
}

// Inf/NaN by bit pattern: a NaN comparison may be folded away, an exponent test may not.
static bool NotFinite(float3 v) {
    uint3 e=uint3(as_type<uint>(v.x)&0x7f800000u,as_type<uint>(v.y)&0x7f800000u,as_type<uint>(v.z)&0x7f800000u);
    return any(e==uint3(0x7f800000u));
}

static uint Col(constant ClothParams& u,uint p) { return p%u.grid.x; }

static uint Row(constant ClothParams& u,uint p) { return p/u.grid.x; }

// Centre differences clamped at the border, cross(dv, du): +Z for the sheet at rest, the side the front picture faces.
static float3 GridNormal(threadgroup float3* gPos,constant ClothParams& u,uint p) {
    uint cols=u.grid.x;
    uint i=Col(u,p),j=Row(u,p);
    uint ip=min(i+1u,cols-1u),im=i>0u?i-1u:0u;
    uint jp=min(j+1u,u.grid.y-1u),jm=j>0u?j-1u:0u;
    float3 du=gPos[j*cols+ip]-gPos[j*cols+im];
    float3 dv=gPos[jp*cols+i]-gPos[jm*cols+i];
    float3 n=cross(dv,du);
    float len2=dot(n,n);
    return len2>1e-24?n*rsqrt(len2):float3(0.0,0.0,1.0);
}

static float3 GridTangent(threadgroup float3* gPos,constant ClothParams& u,uint p) {
    uint cols=u.grid.x;
    uint i=Col(u,p),j=Row(u,p);
    uint ip=min(i+1u,cols-1u),im=i>0u?i-1u:0u;
    float3 du=gPos[j*cols+ip]-gPos[j*cols+im];
    float len2=dot(du,du);
    return len2>1e-24?du*rsqrt(len2):float3(1.0,0.0,0.0);
}

// One Vertex is five float4, written whole so the tail stays zeroed: HLSL byte offsets 0/16/32/48/64.
static void StoreFront(device float4* buf,uint index,float3 pos,float2 uv,float3 nrm,float4 tan) {
    uint o=index*5u;
    buf[o+0u]=float4(pos,uv.x);
    buf[o+1u]=float4(uv.y,nrm);
    buf[o+2u]=tan;
    buf[o+3u]=float4(0.0);
    buf[o+4u]=float4(0.0);
}

static void StoreBack(device float4* buf,uint index,float3 pos,float2 uv,float3 nrm,float4 tan) {
    uint o=index*5u;
    buf[o+0u]=float4(pos,uv.x);
    buf[o+1u]=float4(uv.y,nrm);
    buf[o+2u]=tan;
    buf[o+3u]=float4(0.0);
    buf[o+4u]=float4(0.0);
}

static void StoreRim(device float4* buf,uint index,float3 pos,float2 uv,float3 nrm,float4 tan) {
    uint o=index*5u;
    buf[o+0u]=float4(pos,uv.x);
    buf[o+1u]=float4(uv.y,nrm);
    buf[o+2u]=tan;
    buf[o+3u]=float4(0.0);
    buf[o+4u]=float4(0.0);
}

// Boundary walk, clockwise in (c, t): top hoist to fly, fly top to bottom, bottom fly to hoist, hoist bottom to top.
static uint LoopParticle(constant ClothParams& u,uint k) {
    uint cols=u.grid.x,rows=u.grid.y;
    uint top=cols-1u,side=rows-1u;
    uint p;
    if(k<top) p=k;
    else if(k<top+side) p=(k-top)*cols+(cols-1u);
    else if(k<2u*top+side) p=(rows-1u)*cols+(cols-1u-(k-top-side));
    else p=(rows-1u-(k-2u*top-side))*cols;
    return p;
}

kernel void CSMain(constant ClothParams& u [[buffer(0)]],const device float4* restData [[buffer(1)]],
    device float4* stateData [[buffer(2)]],device float4* frontData [[buffer(3)]],
    device float4* backData [[buffer(4)]],device float4* rimData [[buffer(5)]],
    uint tid [[thread_index_in_threadgroup]]) {
    threadgroup float3 gPos[MAXP];
    threadgroup float gW[MAXP];
    threadgroup uint gBad;

    uint cols=u.grid.x,rows=u.grid.y;
    uint count=min(cols*rows,(uint)MAXP);
    uint substeps=max(u.grid.z,1u);
    float h=u.windDt.w/float(substeps);
    float invH=1.0/h;
    float invH2=invH*invH;
    float spacing=u.shape.z;
    float3 gravity=float3(0.0,u.shape.x,0.0);
    float damp=exp(-u.swivel.w*h);
    float kN=u.material.z,kT=u.material.w;
    float collide=u.swivel.z;

    if(tid==0u) gBad=0u;

    float3 prev[PER];
    float3 pinned[PER];
    float3 weff[PER];

    // Sideways unit vector in the horizontal plane: the flutter gust blows across the wind, never along it.
    float3 wind=u.windDt.xyz;
    float horizontal=length(wind.xz);
    float3 side=horizontal>1e-6?float3(-wind.z,0.0,wind.x)/max(horizontal,1e-6):float3(0.0,0.0,0.0);

    for(uint k0=0u;k0<PER;k0++) {
        uint p=tid+k0*THREADS;
        prev[k0]=float3(0.0);
        pinned[k0]=float3(0.0);
        weff[k0]=wind;
        if(p<count) {
            float4 rest=restData[p];
            pinned[k0]=Swivel(u,rest.xyz);
            gW[p]=rest.w;
            float3 pos=stateData[p*2u].xyz;
            float3 vel=stateData[p*2u+1u].xyz;
            gPos[p]=pos;
            prev[k0]=vel;   // velocity until the reset decision below, then the previous position
            float c=float(Col(u,p))/float(cols-1u);
            weff[k0]=wind+u.gust.x*sin(u.gust.y-u.gust.z*c)*side;
        }
    }
    threadgroup_barrier(mem_flags::mem_threadgroup);

    // Reset on request, on a non-finite particle, or on one that has wandered off the pole (10 m), all as one sheet:
    // resetting some particles and not others would tear the constraints apart on the next substep.
    for(uint k1=0u;k1<PER;k1++) {
        uint p=tid+k1*THREADS;
        if(p<count) {
            float3 d=gPos[p]-pinned[k1];
            if(NotFinite(gPos[p])||NotFinite(prev[k1])||!(dot(d,d)<=100.0)) gBad=1u;
        }
    }
    threadgroup_barrier(mem_flags::mem_threadgroup);
    bool reset=u.gust.w>0.5||gBad!=0u;

    for(uint k2=0u;k2<PER;k2++) {
        uint p=tid+k2*THREADS;
        if(p<count) {
            if(reset) {
                gPos[p]=pinned[k2];
                prev[k2]=pinned[k2];
            } else {
                prev[k2]=gPos[p]-prev[k2]*h;
            }
        }
    }
    threadgroup_barrier(mem_flags::mem_threadgroup);

    for(uint s=0u;s<substeps;s++) {
        // Normals first, read-only over the positions, and kept in registers: the barrier below is what lets the whole sheet
        // predict from one consistent normal field without a second threadgroup array.
        float3 nrm[PER];
        for(uint ka=0u;ka<PER;ka++) {
            uint p=tid+ka*THREADS;
            if(p<count) nrm[ka]=GridNormal(gPos,u,p);
        }
        threadgroup_barrier(mem_flags::mem_threadgroup);

        // Predict: aerodynamic split on the relative wind, gravity, damping. Pins follow the swivel instead.
        for(uint kb=0u;kb<PER;kb++) {
            uint p=tid+kb*THREADS;
            if(p<count) {
                float3 P=gPos[p];
                float3 v=(P-prev[kb])*invH;
                float3 n=nrm[kb];
                float3 w=weff[kb]-v;
                float wn=dot(w,n);
                float3 a=kN*wn*n+kT*(w-wn*n);
                v=(v+h*(gravity+a))*damp;
                prev[kb]=P;
                gPos[p]=gW[p]>0.0?P+h*v:pinned[kb];
            }
        }
        threadgroup_barrier(mem_flags::mem_threadgroup);

        // Twelve colours, each a set of disjoint edges owned by their first endpoint: stretch H and V, shear in both
        // diagonals, bend H and V over two cells, each split by parity. Jacobi inside a colour, Gauss-Seidel across.
        for(uint colour=0u;colour<12u;colour++) {
            uint kind=colour>>1;
            uint parity=colour&1u;
            for(uint kc=0u;kc<PER;kc++) {
                uint p=tid+kc*THREADS;
                if(p<count) {
                    uint i=Col(u,p),j=Row(u,p);
                    bool on=false;
                    uint q=p;
                    float rest=spacing;
                    float compliance=0.0;
                    if(kind==0u)      { on=i+1u<cols&&(i&1u)==parity;                  q=p+1u; }
                    else if(kind==1u) { on=j+1u<rows&&(j&1u)==parity;                  q=p+cols; }
                    else if(kind==2u) { on=i+1u<cols&&j+1u<rows&&(i&1u)==parity;       q=p+cols+1u; rest=spacing*1.41421356; compliance=u.material.x; }
                    else if(kind==3u) { on=i>=1u&&j+1u<rows&&(i&1u)==parity;           q=p+cols-1u; rest=spacing*1.41421356; compliance=u.material.x; }
                    else if(kind==4u) { on=i+2u<cols&&((i>>1)&1u)==parity;             q=p+2u; rest=spacing*2.0; compliance=u.material.y; }
                    else              { on=j+2u<rows&&((j>>1)&1u)==parity;             q=p+2u*cols; rest=spacing*2.0; compliance=u.material.y; }

                    if(on) {
                        float wp=gW[p],wq=gW[q];
                        float3 d=gPos[p]-gPos[q];
                        float len=length(d);
                        float wsum=wp+wq;
                        if(len>1e-6&&wsum>0.0) {
                            float dl=-(len-rest)/(wsum+compliance*invH2);
                            float3 g=d/len;
                            gPos[p]+=(wp*dl)*g;
                            gPos[q]-=(wq*dl)*g;
                        }
                    }
                }
            }
            threadgroup_barrier(mem_flags::mem_threadgroup);
        }

        // Pole: push free particles out of the cylinder around the simulation-space Y axis.
        for(uint kd=0u;kd<PER;kd++) {
            uint p=tid+kd*THREADS;
            if(p<count&&gW[p]>0.0) {
                float3 P=gPos[p];
                float r=length(P.xz);
                if(r<collide&&r>1e-6) {
                    P.xz*=collide/r;
                    gPos[p]=P;
                }
            }
        }
        threadgroup_barrier(mem_flags::mem_threadgroup);
    }

    // The positions are final and read-only from here on, so the two output passes take their normals straight from them.
    float halfThick=u.shape.y;
    bool flipFront=u.shape.w>0.5;
    for(uint ke=0u;ke<PER;ke++) {
        uint p=tid+ke*THREADS;
        if(p<count) {
            float3 P=gPos[p];
            float3 vel=(P-prev[ke])*invH;
            stateData[p*2u]=float4(P,0.0);
            stateData[p*2u+1u]=float4(vel,0.0);

            float3 n=GridNormal(gPos,u,p);
            float3 du=GridTangent(gPos,u,p);
            float c=float(Col(u,p))/float(cols-1u);
            float t=float(Row(u,p))/float(rows-1u);
            float3 local=P-u.origin.xyz;
            StoreFront(frontData,p,local+halfThick*n,float2(flipFront?1.0-c:c,t),n,float4(flipFront?-du:du,1.0));
            StoreBack(backData,p,local-halfThick*n,float2(c,t),-n,float4(du,1.0));
        }
    }

    // Rim: two vertices per boundary point, the in-plane outward normal cross(n, T) with T the loop's centre difference.
    uint loopLength=2u*(cols-1u)+2u*(rows-1u);
    for(uint k=tid;k<loopLength;k+=THREADS) {
        uint p=LoopParticle(u,k);
        uint pn=LoopParticle(u,k+1u<loopLength?k+1u:0u);
        uint pp=LoopParticle(u,k>0u?k-1u:loopLength-1u);
        float3 n=GridNormal(gPos,u,p);
        float3 T=gPos[pn]-gPos[pp];
        float3 o=cross(n,T);
        float len2=dot(o,o);
        o=len2>1e-24?o*rsqrt(len2):float3(0.0,1.0,0.0);
        float c=float(Col(u,p))/float(cols-1u);
        float t=float(Row(u,p))/float(rows-1u);
        float3 local=gPos[p]-u.origin.xyz;
        float4 tangent=float4(T*rsqrt(max(dot(T,T),1e-24)),1.0);
        StoreRim(rimData,2u*k,local+halfThick*n,float2(c,t),o,tangent);
        StoreRim(rimData,2u*k+1u,local-halfThick*n,float2(c,t),o,tangent);
    }
}
""";
}
