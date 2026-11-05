// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

/// <summary>
/// C1: Vulkan GLSL mirror of Cloth.Hlsl.cs, compiled to SPIR-V at runtime by the backend. The maths is the HLSL's line for
/// line; the two places it cannot be are called out here because neither produces a compiler diagnostic, only a wrong image.
///
/// <para><b>Indexing.</b> HLSL ByteAddressBuffer addresses bytes, GLSL std430 arrays address elements, so every offset below
/// is the HLSL byte offset divided by the 16-byte vec4 stride: rest and state at p and 2p, a vertex at 5p. The five vec4 of
/// one Vertex carry pos.xyz/uv.x, uv.y/normal.xyz, tangent and two zero blocks, i.e. the same 80-byte record the static
/// vertex buffer holds.</para>
///
/// <para><b>No normal array.</b> The HLSL keeps <c>gN[512]</c> in groupshared memory, which with positions and weights would
/// be 18 KB and exceed the 16 KB <c>maxComputeSharedMemorySize</c> every Vulkan implementation must provide. Here each thread
/// holds the four normals it predicted in registers instead: they are computed read-only over the positions, a barrier
/// separates that from the position write, and the two passes after the substep loop recompute the normal from the final,
/// now read-only positions. Workgroup memory therefore holds positions (8192 B), weights (2048 B) and the reset flag
/// (4 B) only - 10244 B.</para>
///
/// <para><b>Slot contract.</b> Params is a push constant, so the buffers keep binding = declared index: rest 1, state 2,
/// front 3, back 4, rim 5. See <see cref="ComputeBindingType"/>.</para>
/// </summary>
public sealed partial class ClothSimulationEffect
{
    const string GlslSource = """
#version 450
// Shares the 112-byte parameter block with the HLSL, seven vec4 in all:
// uWindDt = wind.xyz / dt, uGust = gust / phase / wavenumber / reset, uSwivel = cos yaw / sin yaw / pole radius / damping,
// uMaterial = shear / bend / normal drag / tangent drag, uShape = gravity y / half thickness / spacing / front-u flip,
// uOrigin = the mesh centre subtracted from every output vertex, uGrid = columns / rows / substeps / unused.
layout(push_constant) uniform Params {
    vec4 uWindDt;
    vec4 uGust;
    vec4 uSwivel;
    vec4 uMaterial;
    vec4 uShape;
    vec4 uOrigin;
    uvec4 uGrid;
};
layout(std430,binding=1) readonly buffer Rest { vec4 restData[]; };   // rest.xyz, inverse mass (0 = pinned)
layout(std430,binding=2) buffer State { vec4 stateData[]; };          // pos (2p), vel (2p + 1), resident across frames
layout(std430,binding=3) buffer Front { vec4 frontData[]; };          // Vertex (5 vec4) per particle, the +normal side
layout(std430,binding=4) buffer Back { vec4 backData[]; };            // Vertex (5 vec4) per particle, the -normal side
layout(std430,binding=5) buffer Rim { vec4 rimData[]; };              // Vertex (5 vec4) x 2 per boundary point

#define THREADS 128
#define PER 4
#define MAXP 512

shared vec3 gPos[MAXP];
shared float gW[MAXP];
shared uint gBad;

layout(local_size_x=THREADS,local_size_y=1,local_size_z=1) in;

vec3 Swivel(vec3 v) {
    return vec3(v.x*uSwivel.x-v.z*uSwivel.y,v.y,v.x*uSwivel.y+v.z*uSwivel.x);
}

// Inf/NaN by bit pattern: a NaN comparison may be folded away, an exponent test may not.
bool NotFinite(vec3 v) {
    uvec3 e=floatBitsToUint(v)&0x7f800000u;
    return any(equal(e,uvec3(0x7f800000u)));
}

uint Col(uint p) { return p%uGrid.x; }

uint Row(uint p) { return p/uGrid.x; }

// Centre differences clamped at the border, cross(dv, du): +Z for the sheet at rest, the side the front picture faces.
vec3 GridNormal(uint p) {
    uint cols=uGrid.x;
    uint i=Col(p),j=Row(p);
    uint ip=min(i+1u,cols-1u),im=i>0u?i-1u:0u;
    uint jp=min(j+1u,uGrid.y-1u),jm=j>0u?j-1u:0u;
    vec3 du=gPos[j*cols+ip]-gPos[j*cols+im];
    vec3 dv=gPos[jp*cols+i]-gPos[jm*cols+i];
    vec3 n=cross(dv,du);
    float len2=dot(n,n);
    return len2>1e-24?n*inversesqrt(len2):vec3(0.0,0.0,1.0);
}

vec3 GridTangent(uint p) {
    uint cols=uGrid.x;
    uint i=Col(p),j=Row(p);
    uint ip=min(i+1u,cols-1u),im=i>0u?i-1u:0u;
    vec3 du=gPos[j*cols+ip]-gPos[j*cols+im];
    float len2=dot(du,du);
    return len2>1e-24?du*inversesqrt(len2):vec3(1.0,0.0,0.0);
}

// One Vertex is five vec4, written whole so the tail stays zeroed: HLSL byte offsets 0/16/32/48/64.
void StoreFront(uint index,vec3 pos,vec2 uv,vec3 nrm,vec4 tan) {
    uint o=index*5u;
    frontData[o+0u]=vec4(pos,uv.x);
    frontData[o+1u]=vec4(uv.y,nrm);
    frontData[o+2u]=tan;
    frontData[o+3u]=vec4(0.0);
    frontData[o+4u]=vec4(0.0);
}

void StoreBack(uint index,vec3 pos,vec2 uv,vec3 nrm,vec4 tan) {
    uint o=index*5u;
    backData[o+0u]=vec4(pos,uv.x);
    backData[o+1u]=vec4(uv.y,nrm);
    backData[o+2u]=tan;
    backData[o+3u]=vec4(0.0);
    backData[o+4u]=vec4(0.0);
}

void StoreRim(uint index,vec3 pos,vec2 uv,vec3 nrm,vec4 tan) {
    uint o=index*5u;
    rimData[o+0u]=vec4(pos,uv.x);
    rimData[o+1u]=vec4(uv.y,nrm);
    rimData[o+2u]=tan;
    rimData[o+3u]=vec4(0.0);
    rimData[o+4u]=vec4(0.0);
}

// Boundary walk, clockwise in (c, t): top hoist to fly, fly top to bottom, bottom fly to hoist, hoist bottom to top.
uint LoopParticle(uint k) {
    uint cols=uGrid.x,rows=uGrid.y;
    uint top=cols-1u,side=rows-1u;
    uint p;
    if(k<top) p=k;
    else if(k<top+side) p=(k-top)*cols+(cols-1u);
    else if(k<2u*top+side) p=(rows-1u)*cols+(cols-1u-(k-top-side));
    else p=(rows-1u-(k-2u*top-side))*cols;
    return p;
}

void main() {
    uint tid=gl_LocalInvocationID.x;
    uint cols=uGrid.x,rows=uGrid.y;
    uint count=min(cols*rows,uint(MAXP));
    uint substeps=max(uGrid.z,1u);
    float h=uWindDt.w/float(substeps);
    float invH=1.0/h;
    float invH2=invH*invH;
    float spacing=uShape.z;
    vec3 gravity=vec3(0.0,uShape.x,0.0);
    float damp=exp(-uSwivel.w*h);
    float kN=uMaterial.z,kT=uMaterial.w;
    float collide=uSwivel.z;

    if(tid==0u) gBad=0u;

    vec3 prev[PER];
    vec3 pinned[PER];
    vec3 weff[PER];

    // Sideways unit vector in the horizontal plane: the flutter gust blows across the wind, never along it.
    vec3 wind=uWindDt.xyz;
    float horizontal=length(wind.xz);
    vec3 side=horizontal>1e-6?vec3(-wind.z,0.0,wind.x)/max(horizontal,1e-6):vec3(0.0);

    for(uint k0=0u;k0<PER;k0++) {
        uint p=tid+k0*THREADS;
        prev[k0]=vec3(0.0);
        pinned[k0]=vec3(0.0);
        weff[k0]=wind;
        if(p<count) {
            vec4 rest=restData[p];
            pinned[k0]=Swivel(rest.xyz);
            gW[p]=rest.w;
            vec3 pos=stateData[p*2u].xyz;
            vec3 vel=stateData[p*2u+1u].xyz;
            gPos[p]=pos;
            prev[k0]=vel;   // velocity until the reset decision below, then the previous position
            float c=float(Col(p))/float(cols-1u);
            weff[k0]=wind+uGust.x*sin(uGust.y-uGust.z*c)*side;
        }
    }
    barrier();

    // Reset on request, on a non-finite particle, or on one that has wandered off the pole (10 m), all as one sheet:
    // resetting some particles and not others would tear the constraints apart on the next substep.
    for(uint k1=0u;k1<PER;k1++) {
        uint p=tid+k1*THREADS;
        if(p<count) {
            vec3 d=gPos[p]-pinned[k1];
            if(NotFinite(gPos[p])||NotFinite(prev[k1])||!(dot(d,d)<=100.0)) gBad=1u;
        }
    }
    barrier();
    bool reset=uGust.w>0.5||gBad!=0u;

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
    barrier();

    for(uint s=0u;s<substeps;s++) {
        // Normals first, read-only over the positions, and kept in registers: the barrier below is what lets the whole sheet
        // predict from one consistent normal field without a second workgroup array.
        vec3 nrm[PER];
        for(uint ka=0u;ka<PER;ka++) {
            uint p=tid+ka*THREADS;
            if(p<count) nrm[ka]=GridNormal(p);
        }
        barrier();

        // Predict: aerodynamic split on the relative wind, gravity, damping. Pins follow the swivel instead.
        for(uint kb=0u;kb<PER;kb++) {
            uint p=tid+kb*THREADS;
            if(p<count) {
                vec3 P=gPos[p];
                vec3 v=(P-prev[kb])*invH;
                vec3 n=nrm[kb];
                vec3 u=weff[kb]-v;
                float un=dot(u,n);
                vec3 a=kN*un*n+kT*(u-un*n);
                v=(v+h*(gravity+a))*damp;
                prev[kb]=P;
                gPos[p]=gW[p]>0.0?P+h*v:pinned[kb];
            }
        }
        barrier();

        // Twelve colours, each a set of disjoint edges owned by their first endpoint: stretch H and V, shear in both
        // diagonals, bend H and V over two cells, each split by parity. Jacobi inside a colour, Gauss-Seidel across.
        for(uint colour=0u;colour<12u;colour++) {
            uint kind=colour>>1;
            uint parity=colour&1u;
            for(uint kc=0u;kc<PER;kc++) {
                uint p=tid+kc*THREADS;
                if(p<count) {
                    uint i=Col(p),j=Row(p);
                    bool on=false;
                    uint q=p;
                    float rest=spacing;
                    float compliance=0.0;
                    if(kind==0u)      { on=i+1u<cols&&(i&1u)==parity;                  q=p+1u; }
                    else if(kind==1u) { on=j+1u<rows&&(j&1u)==parity;                  q=p+cols; }
                    else if(kind==2u) { on=i+1u<cols&&j+1u<rows&&(i&1u)==parity;       q=p+cols+1u; rest=spacing*1.41421356; compliance=uMaterial.x; }
                    else if(kind==3u) { on=i>=1u&&j+1u<rows&&(i&1u)==parity;           q=p+cols-1u; rest=spacing*1.41421356; compliance=uMaterial.x; }
                    else if(kind==4u) { on=i+2u<cols&&((i>>1)&1u)==parity;             q=p+2u; rest=spacing*2.0; compliance=uMaterial.y; }
                    else              { on=j+2u<rows&&((j>>1)&1u)==parity;             q=p+2u*cols; rest=spacing*2.0; compliance=uMaterial.y; }

                    if(on) {
                        float wp=gW[p],wq=gW[q];
                        vec3 d=gPos[p]-gPos[q];
                        float len=length(d);
                        float wsum=wp+wq;
                        if(len>1e-6&&wsum>0.0) {
                            float dl=-(len-rest)/(wsum+compliance*invH2);
                            vec3 g=d/len;
                            gPos[p]+=(wp*dl)*g;
                            gPos[q]-=(wq*dl)*g;
                        }
                    }
                }
            }
            barrier();
        }

        // Pole: push free particles out of the cylinder around the simulation-space Y axis.
        for(uint kd=0u;kd<PER;kd++) {
            uint p=tid+kd*THREADS;
            if(p<count&&gW[p]>0.0) {
                vec3 P=gPos[p];
                float r=length(P.xz);
                if(r<collide&&r>1e-6) {
                    P.xz*=collide/r;
                    gPos[p]=P;
                }
            }
        }
        barrier();
    }

    // The positions are final and read-only from here on, so the two output passes take their normals straight from them.
    float halfThick=uShape.y;
    bool flipFront=uShape.w>0.5;
    for(uint ke=0u;ke<PER;ke++) {
        uint p=tid+ke*THREADS;
        if(p<count) {
            vec3 P=gPos[p];
            vec3 vel=(P-prev[ke])*invH;
            stateData[p*2u]=vec4(P,0.0);
            stateData[p*2u+1u]=vec4(vel,0.0);

            vec3 n=GridNormal(p);
            vec3 du=GridTangent(p);
            float c=float(Col(p))/float(cols-1u);
            float t=float(Row(p))/float(rows-1u);
            vec3 local=P-uOrigin.xyz;
            StoreFront(p,local+halfThick*n,vec2(flipFront?1.0-c:c,t),n,vec4(flipFront?-du:du,1.0));
            StoreBack(p,local-halfThick*n,vec2(c,t),-n,vec4(du,1.0));
        }
    }

    // Rim: two vertices per boundary point, the in-plane outward normal cross(n, T) with T the loop's centre difference.
    uint loopLength=2u*(cols-1u)+2u*(rows-1u);
    for(uint k=tid;k<loopLength;k+=THREADS) {
        uint p=LoopParticle(k);
        uint pn=LoopParticle(k+1u<loopLength?k+1u:0u);
        uint pp=LoopParticle(k>0u?k-1u:loopLength-1u);
        vec3 n=GridNormal(p);
        vec3 T=gPos[pn]-gPos[pp];
        vec3 o=cross(n,T);
        float len2=dot(o,o);
        o=len2>1e-24?o*inversesqrt(len2):vec3(0.0,1.0,0.0);
        float c=float(Col(p))/float(cols-1u);
        float t=float(Row(p))/float(rows-1u);
        vec3 local=gPos[p]-uOrigin.xyz;
        vec4 tangent=vec4(T*inversesqrt(max(dot(T,T),1e-24)),1.0);
        StoreRim(2u*k,local+halfThick*n,vec2(c,t),o,tangent);
        StoreRim(2u*k+1u,local-halfThick*n,vec2(c,t),o,tangent);
    }
}
""";
}
