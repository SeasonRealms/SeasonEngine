// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

public sealed partial class VolumetricFogEffect
{
    /// <summary>Bound by the scene backend to fogFrame (ByteAddressBuffer) and fogIntegrated/fogLocal (Texture3D); the caller gates by Pass.</summary>
    public const string SamplingHlsl = """
float4 FogF(uint i) { return asfloat(fogFrame.Load4(i*16)); }
float4 FogTransform(float4 p,uint i) { return p.x*FogF(i)+p.y*FogF(i+1)+p.z*FogF(i+2)+p.w*FogF(i+3); }
float FogZ(float k) { return 0.5*(pow(abs(1.0+2.0*FogF(8).w),k/FogF(10).z)-1.0); }
float4 FogSegment(float4 acc,float4 local,float ds) {
    float tau=local.w*ds,t=exp(-tau),integral=ds*(1.0-tau*0.5+tau*tau/6.0);
    if(tau>=0.001) integral=(1.0-t)/max(local.w,1e-30);
    return float4(min(acc.xyz+acc.w*local.xyz*integral,65000.0),acc.w*t);
}
float4 FogColumn(int2 xy,int k,float depth) {
    xy=clamp(xy,int2(0,0),int2(FogF(10).xy)-1);
    float4 acc=fogIntegrated.Load(int4(xy,k,0));
    if(k<int(FogF(10).z)) {
        float2 uv=(float2(xy)+0.5)/FogF(10).xy;
        float4 world=FogTransform(float4(uv*float2(2,-2)+float2(-1,1),0.5,1),0);
        float3 ray=world.xyz/world.w-FogF(8).xyz;
        float rayLength=length(ray/max(dot(ray,FogF(9).xyz),1e-6));
        acc=FogSegment(acc,fogLocal.Load(int4(xy,k,0)),max(depth-FogZ(float(k)),0.0)*rayLength);
    }
    return acc;
}
float4 FogAtWorld(float3 world) {
    // A single exit ensures fxc does not emit X4000 when called from a material's dynamic branch.
    float4 result=float4(0,0,0,1);
    float depth=dot(world-FogF(8).xyz,FogF(9).xyz);
    float4 clip=FogTransform(float4(world,1),22);
    if(depth>0 && clip.w>0) {
        float2 uv=saturate(clip.xy/clip.w*float2(0.5,-0.5)+0.5);
        depth=min(depth,FogF(8).w);
        int k=min(int(floor(log(1.0+2.0*depth)/log(1.0+2.0*FogF(8).w)*FogF(10).z)),int(FogF(10).z)-1);
        if(depth>=FogF(8).w) k=int(FogF(10).z);
        float2 q=uv*FogF(10).xy-0.5,f=frac(q); int2 b=int2(floor(q));
        float4 a=lerp(FogColumn(b,k,depth),FogColumn(b+int2(1,0),k,depth),f.x);
        float4 c=lerp(FogColumn(b+int2(0,1),k,depth),FogColumn(b+int2(1,1),k,depth),f.x);
        result=lerp(a,c,f.y);
    }
    return result;
}
float4 FogAtDepth(float2 uv,float depth) {
    float4 p=FogTransform(float4(uv*float2(2,-2)+float2(-1,1),depth>=1.0?0.5:depth,1),18);
    float3 world=p.xyz/p.w;
    if(depth>=1.0) { float3 ray=world-FogF(8).xyz; world=FogF(8).xyz+ray*(FogF(8).w/max(dot(ray,FogF(9).xyz),1e-6)); }
    return FogAtWorld(world);
}
float4 FogTransparent(float4 premultiplied,float4 fog,bool additive) {
    return float4(premultiplied.xyz*fog.w+(additive?0.0:premultiplied.w)*fog.xyz,premultiplied.w);
}
float4 FogOpaque(float4 color,float4 fog,float ao) { return float4(color.xyz*ao*fog.w+fog.xyz,color.w); }
""";

    public const string CompositeVertexHlsl = """
float4 VSMain(uint id:SV_VertexID):SV_Position {
    float2 p=float2((id<<1)&2,id&2);
    return float4(p*2.0-1.0,0,1);
}
""";

    public const string CompositeHlsl = """
ByteAddressBuffer fogFrame:register(t0);
Texture3D<float4> fogIntegrated:register(t1);
Texture3D<float4> fogLocal:register(t2);
Texture2D<float4> sceneColor:register(t3);
Texture2D<float> sceneDepth:register(t4);
Texture2D<float4> sceneAo:register(t5);
SamplerState linearClamp:register(s0);
cbuffer CompositeParams:register(b0) { float4 compositeParams; };
""" + SamplingHlsl + """

float4 PSMain(float4 position:SV_Position):SV_Target {
    uint width,height; sceneColor.GetDimensions(width,height);
    float2 uv=position.xy/float2(width,height);
    int2 pixel=int2(position.xy);
    float ao=1.0;
    if(compositeParams.y>0.5) ao=lerp(1.0,sceneAo.SampleLevel(linearClamp,uv,0).r,compositeParams.x);
    return FogOpaque(sceneColor.Load(int3(pixel,0)),FogAtDepth(uv,sceneDepth.Load(int3(pixel,0))),ao);
}
""";

    const string CommonHlsl = """
cbuffer Params : register(b0) { float4 dispatchSize; };
ByteAddressBuffer frameData : register(t0);
SamplerState linearClamp : register(s0);
float4 F(uint i) { return asfloat(frameData.Load4(i * 16)); }
float4 Transform(float4 p, uint i) { return p.x*F(i)+p.y*F(i+1)+p.z*F(i+2)+p.w*F(i+3); }
float Z(float k) { return 0.5*(pow(abs(1.0+2.0*F(8).w),k/F(10).z)-1.0); }
float3 Ray(float2 uv) {
    float4 p=Transform(float4(uv*float2(2,-2)+float2(-1,1),0.5,1),0);
    float3 r=p.xyz/p.w-F(8).xyz;
    return r/max(dot(r,F(9).xyz),1e-6);
}
float4 Segment(float4 acc,float4 local,float ds) {
    float tau=local.w*ds, t=exp(-tau);
    float integral=ds*(1.0-tau*0.5+tau*tau/6.0);
    if(tau>=0.001) integral=(1.0-t)/max(local.w,1e-30);
    return float4(min(acc.xyz+acc.w*local.xyz*integral,65000.0),acc.w*t);
}
""";

    const string NoiseHlsl = """
cbuffer Params : register(b0) { float4 dispatchSize; };
RWTexture3D<float4> outputVolume : register(u0);
[numthreads(4,4,4)] void CSMain(uint3 id : SV_DispatchThreadID) {
    if(all(id<64)) {
        uint h=id.x*73856093u ^ id.y*19349663u ^ id.z*83492791u ^ 0x9e3779b9u;
        h^=h>>16; h*=0x7feb352du; h^=h>>15; h*=0x846ca68bu; h^=h>>16;
        float n=float(h&255u)/255.0; outputVolume[id]=float4(n,n,n,n);
    }
}
""";

    const string InjectionHlsl = CommonHlsl + """
Texture3D<float4> noiseVolume : register(t1);
Texture2D<float> shadowAtlas : register(t2);
Texture2D<float4> cloudNoise : register(t3);
Texture2D<float> farShadow : register(t4);
Texture2D<float> localShadow : register(t5);
RWTexture3D<float4> outputVolume : register(u0);
float Noise(float3 p,float scale,float amount) {
    float3 q=frac(p*scale)*64.0-0.5, f=frac(q); int3 b=int3(floor(q)); float n=0;
    [unroll] for(int z=0;z<2;z++) [unroll] for(int y=0;y<2;y++) [unroll] for(int x=0;x<2;x++) {
        float3 w=lerp(1.0-f,f,float3(x,y,z));
        n+=noiseVolume.Load(int4((b+int3(x,y,z))&63,0)).x*w.x*w.y*w.z;
    }
    return 1.0+amount*(2.0*n-1.0);
}
float4 CloudSample(float2 uv) {
    uint w,h; cloudNoise.GetDimensions(w,h); int2 size=int2(w,h);
    float2 p=frac(uv)*float2(size)-0.5,f=frac(p); int2 b=int2(floor(p)); float4 n=0;
    [unroll] for(int y=0;y<2;y++) [unroll] for(int x=0;x<2;x++) {
        int2 c=int2(uint2(b+int2(x,y)+size)%uint2(size)); float2 a=lerp(1-f,f,float2(x,y));
        n+=cloudNoise.Load(int3(c,0))*a.x*a.y;
    }
    return n;
}
float4 WeatherH(int i) { float4 h=F(118+i); if(i==0 && F(16).w<0.5) h.y=0; return h; }
float4 WeatherR(int i,int row) { return F(120+i*8+row); }
// 2-7 lightning: in the fog frame SceneLightParams starts at index 32, and WeatherFlash is row 152 within it, so the absolute position is 184.
// The graphics pipeline reads the same-named cbuffer member; both sides are the same data. Without this line CloudHlsl fails to compile and the entire fog chain is rejected at Initialize.
float4 WeatherFlash() { return F(184); }
float4 WeatherNoise(float2 uv) { return CloudSample(uv); }
""" + WeatherShaders.CommonHlsl + WeatherShaders.CloudHlsl + """
float CloudShadow(float3 p,float3 l) {
    float tau=0;
    if(F(16).w>0.5 && F(116).x>0 && l.y>0) {
        float invY=1.0/max(l.y,0.05);
        for(int i=0;i<min(int(F(115).w),3);i++) {
            float4 a=F(109+i), b=F(112+i);
            float2 pos=p.xz*0.001+l.xz*(max(a.x-p.y*0.001,0.0)*invY);
            float4 n=CloudSample((pos+b.xy)*b.z);
            float d=saturate((n.x*lerp(1.0,n.w,0.7)-(1.0-a.z))/max(a.z,0.001));
            d*=saturate(1.0-b.w*0.5*(n.y+n.z)); tau+=d*a.w*a.y*invY;
        }
    }
    return (1.0-F(116).x*saturate(1.0-exp(-tau)))*WeatherCloudShadow(p,l);
}
float ShadowTile(float3 p,uint matrixIndex,int tile) {
    float4 clip=Transform(float4(p,1),matrixIndex); float result=1;
    if(clip.w>0) {
        float3 ndc=clip.xyz/clip.w;
        if(all(abs(ndc.xy)<=1.0) && ndc.z>=0 && ndc.z<=1) {
            uint w,h; shadowAtlas.GetDimensions(w,h); int2 size=int2(w,h), halfSize=size>>1;
            int2 origin=int2(tile&1,tile>>1)*halfSize;
            float2 texel=(ndc.xy*float2(0.5,-0.5)+0.5)*float2(halfSize)+float2(origin);
            int count=F(14).w>1.0?4:1; float visible=0;
            [unroll] for(int i=0;i<4;i++) {
                if(i<count) {
                    float2 offset=count==1?float2(0,0):float2((i&1)-0.5,(i>>1)-0.5);
                    int2 c=clamp(int2(floor(texel+offset)),origin,origin+halfSize-1);
                    visible+=ndc.z-0.0002<=shadowAtlas.Load(int3(c,0))?1.0:0.0;
                }
            }
            result=lerp(1.0,visible/float(count),saturate(F(89).y));
        }
    }
    return result;
}
// One 1024^2 orthographic map centred on the medium rather than on the camera, so its matrix does not move when the eye
// does. Coarser than any cascade by design: it exists to keep a shaft readable where the cascades have already faded
// out, not to resolve the trunk that makes it. Four taps over one texel of spread, because a hard edge on a volumetric
// shaft reads as a hole punched in the light rather than as the silhouette of a branch.
float FogFarShadow(float3 p,float depth,float reach) {
    float4 clip=Transform(float4(p,1),448); float result=1;
    if(clip.w>0) {
        float3 ndc=clip.xyz/clip.w;
        if(all(abs(ndc.xy)<=1.0) && ndc.z>=0 && ndc.z<=1) {
            uint w,h; farShadow.GetDimensions(w,h); int2 size=int2(w,h);
            float2 texel=(ndc.xy*float2(0.5,-0.5)+0.5)*float2(size)-0.5;
            int2 base=int2(floor(texel)); float visible=0; float bias=F(452).y;
            [unroll] for(int i=0;i<4;i++) {
                int2 c=clamp(base+int2(i&1,i>>1),int2(0,0),size-1);
                visible+=ndc.z-bias<=farShadow.Load(int3(c,0))?1.0:0.0;
            }
            result=lerp(1.0,visible*0.25,saturate(F(89).y));
        }
    }
    // Hand over exactly where the cascades hand off: the same 10% band of the same reach, so a froxel crossing the
    // boundary keeps one shadow instead of acquiring a second. Inside the band the far map is not consulted at all -
    // it is the coarser of the two, and mixing it in would only soften edges the cascades already resolve correctly.
    return lerp(1.0,result,saturate((depth-reach*0.9)/max(reach*0.1,0.001)));
}
// Local point-light cubes: up to two lamps whose light actually reaches the medium, six 90 degree faces each, packed four to
// a row. Occlusion is the one thing a punctual light cannot otherwise express - without this term a torch inside a barn
// lights the mist through the wall, because 1/d^2 attenuation has no notion of what stands between.
//
// The face is the dominant axis of (froxel - light), which is exactly the axis the CPU built that face's matrix around, so
// no projection is re-derived here: the uploaded matrix is the only source of truth for both the projection and the culling
// volume the texels came from. 128^2 per face is far coarser than a froxel, so the taps are spread over two rings rather
// than one texel - a hard edge on a volumetric shaft reads as a hole punched in the light.
//
// Eight taps: four axial at radius two texels and four diagonals at radius one. The axial pair carries three quarters of the
// second moment and the diagonals close the sectors between them, so no direction along the face is under-sampled. The sum
// of squares is twelve in x and twelve in y with every cross term zero, which is what lets LocalFaceVisibility fit a plane
// with two dot products instead of solving a normal-equation system.
int2 LocalRingOffset(int tapIndex) {
    // 0..3 walk -Y,+X,+Y,-X at radius two; 4..7 walk the diagonals at radius one. Both halves are generated from the index
    // rather than tabulated: fxc lowers indexed constant-array access inside an unrolled loop to a per-invocation warning
    // (X3556), and this file compiles with warnings as errors.
    if(tapIndex<4) {
        int reach=((tapIndex^(tapIndex>>1))&1)!=0?2:-2;
        return (tapIndex&1)!=0?int2(reach,0):int2(0,reach);
    }
    int diagonal=tapIndex-4;
    return int2(((diagonal^(diagonal>>1))&1)!=0?1:-1,(diagonal>>1)!=0?1:-1);
}
// One face's visibility at p in 0..1, before the slot's strength is applied. Shared by the main face and, at a seam, by the
// neighbour. The margin is meta.z / clip.w: clip.w is the distance along the face's own axis, and meta.z already folded the
// lamp's near/far pair and the texel width into one number, so the division is the whole of the reconstruction.
float LocalFaceVisibility(float3 p,uint matrixRow,int2 origin,float4 meta) {
    float4 clip=Transform(float4(p,1),matrixRow);
    if(clip.w<=0) return 1;
    float3 ndc=clip.xyz/clip.w;
    // A tenth of a texel of slack on the bounds: the seam blend evaluates the neighbour face right up to its own edge, and a
    // strict 1.0 would reject that tap over a rounding error and hand back "unshadowed", drawing a bright hairline.
    if(!(all(abs(ndc.xy)<1.001) && ndc.z>=0 && ndc.z<=1)) return 1;
    int faceSize=128;
    float2 texel=(ndc.xy*float2(0.5,-0.5)+0.5)*faceSize+float2(origin.x,origin.y)-0.5;
    int2 base=int2(floor(texel));
    float depths[8], sum=0, gx=0, gy=0, lo=1, hi=0;
    [unroll] for(int tapIndex=0;tapIndex<8;tapIndex++) {
        int2 offset=LocalRingOffset(tapIndex);
        // tapDepth, not sample: sample is a shader-model keyword on every one of the four languages.
        float tapDepth=localShadow.Load(int3(clamp(base+offset,origin,origin+faceSize-1),0));
        depths[tapIndex]=tapDepth; sum+=tapDepth; lo=min(lo,tapDepth); hi=max(hi,tapDepth);
        gx+=offset.x*tapDepth; gy+=offset.y*tapDepth;
    }
    // 1/12 rather than 1/8: the least-squares slope over these eight offsets has sum(offset^2) = 12 as its denominator, and
    // the cross terms vanish, so the gradient is just the offset-weighted sum rescaled. The plane's value at the centre is
    // the plain mean, because the offsets themselves sum to zero in both axes.
    gx*=0.083333333; gy*=0.083333333;
    float bias=meta.z/max(clip.w,0.001);
    // A plane through a silhouette is not a plane through a surface. When the taps already disagree by more than a dozen
    // margins, the fit would invent a slope across the edge and smear the very silhouette the tap was meant to resolve, so
    // the tilt is dropped and every tap is read raw. Eight margins is twelve texels at the authored 1.5, comfortably above
    // the four-texel spread even for a surface leaning a full texel per texel, while a step wider than that is a
    // discontinuity the fit cannot follow anyway.
    float tilt=meta.w*(hi-lo<8.0*bias?1.0:0.0);
    float plane=sum*0.125;
    float visible=0;
    // Named apart from tapIndex above: fxc rejects a second for-loop that redeclares a loop variable of the same name in the
    // same function (X3078), even though the two scopes do not overlap. This file compiles with warnings as errors.
    [unroll] for(int judgeIndex=0;judgeIndex<8;judgeIndex++) {
        int2 offset=LocalRingOffset(judgeIndex);
        float fitted=clamp(plane+gx*offset.x+gy*offset.y,lo,hi);
        visible+=ndc.z-bias<=lerp(depths[judgeIndex],fitted,tilt)?1.0:0.0;
    }
    return visible*0.125;
}
float FogLocalShadow(float3 p,int light) {
    float result=1;
    // Two slots, read as a loop rather than unrolled. The body carries the whole LocalFaceVisibility chain - sixteen
    // taps, a seam blend and two more face reads - and having it inline twice is most of this kernel's code size. The
    // loop body is identical either way, so every froxel gets exactly the value it got before; only the code is shared.
    [loop] for(int slotIndex=0;slotIndex<2;slotIndex++) {
        uint row=456+slotIndex*25; float4 meta=F(row+24);
        if(light==int(meta.x)) {
            float3 d=p-F(35+light*4).xyz, ad=abs(d);
            int face=ad.x>=ad.y?(ad.x>=ad.z?(d.x>0?0:1):(d.z>0?4:5)):(ad.y>=ad.z?(d.y>0?2:3):(d.z>0?4:5));
            // Four tiles to a row, so the tile index splits by a shift and a mask.
            int2 origin=int2((slotIndex*6+face)&3,(slotIndex*6+face)>>2)*128;
            float visible=LocalFaceVisibility(p,row+face*4,origin,meta);
            // Seam: a froxel within a texel of the 45 degree boundary lands where the face it picked and the face it passed
            // over disagree about the silhouette, and that disagreement is a visible line running down a wall in the mist.
            // Both faces store the distance along their own axis, and the two axes are the two components of the same
            // vector, so on the boundary the two hold the same depth and mixing them is continuous. The band is one texel
            // wide, expressed in the face's own ndc - two units across 128 texels - so it needs no distance in metres.
            float dominant=max(ad.x,max(ad.y,ad.z));
            float second=max(min(ad.x,ad.y),min(max(ad.x,ad.y),ad.z));
            float band=dominant*0.015625;
            if(dominant-second<band) {
                int secondAxis=face<2?(ad.y>=ad.z?1:2):(face<4?(ad.x>=ad.z?0:2):(ad.x>=ad.y?0:1));
                float secondSign=secondAxis==0?d.x:(secondAxis==1?d.y:d.z);
                int secondFace=secondAxis*2+(secondSign>0?0:1), secondTile=slotIndex*6+secondFace;
                float other=LocalFaceVisibility(p,row+secondFace*4,int2(secondTile&3,secondTile>>2)*128,meta);
                float edge=saturate((dominant-second)/max(band,1e-6));
                visible=lerp(other,visible,0.5+0.5*edge*edge*(3.0-2.0*edge));
            }
            result*=lerp(1.0,visible,saturate(meta.y));
        }
    }
    return result;
}
float Visibility(float3 p,float depth,int light) {
    float result=1; float4 indices=F(34), flags=F(88), splits=F(87);
    if(light==int(indices.z) && flags.x>0.5 && depth<splits.w) {
        int count=clamp(int(flags.y),1,3), cascade=0;
        if(count>1 && depth>splits.x) cascade=1;
        if(count>2 && depth>splits.y) cascade=2;
        result=ShadowTile(p,67+cascade*4,cascade);
        float end=splits[cascade], start=cascade==0?0:splits[cascade-1];
        float blend=saturate((depth-lerp(start,end,0.9))/max((end-start)*0.1,0.001));
        float next=1;
        if(cascade+1<count) next=ShadowTile(p,67+(cascade+1)*4,cascade+1);
        result=lerp(result,next,blend);
        result=lerp(result,1.0,saturate((depth-splits.w*0.9)/max(splits.w*0.1,0.001)));
    }
    if(light==int(indices.w) && F(89).x>0.5) result*=ShadowTile(p,83,3);
    // The fog's own far map, and only for the cascade light: it is that light's depth from a different viewpoint, not a
    // second occluder. Past the cascade reach the fog had nothing to consult, which is what flattened every shaft seen
    // from outside the volume into a slab of uniform light.
    if(light==int(indices.z) && F(448).x>0.5) result*=FogFarShadow(p,depth,splits.w);
    // The one term here that has nothing to do with the sun. A slot whose meta row names a different light costs two row
    // reads and a comparison, which is why this is unconditional rather than gated by a separate enable flag.
    result*=FogLocalShadow(p,light);
    return result;
}
// A slice 20 m out is 1.35 m thick at the High tier, and a low sun crosses that much depth of a trunk's shadow easily.
// One sample at the slice centre therefore collapses a beam onto a single depth, which is what makes shafts read as
// mottling instead of stripes. The medium terms (sigma, scattering, emission) are smooth over the same span and stay at
// the centre; only the term carrying the occluder's silhouette is re-evaluated, and only for the one light that casts
// into the cascade atlas - the other slots have no depth map to consult, so re-sampling them would change nothing.
// Two-point Gauss-Legendre over the span. The default is the old centre sample, so a slice thinner than half a metre
// (everything the near field produces) is bit-identical to the previous behaviour.
float SliceVisibility(float3 ray,float depth,float z0,float span,int light) {
    // One exit, both branches writing the same variable. With an early return ahead of the loop, fxc reports X4000
    // (potentially uninitialised) on this function, and this file compiles with warnings as errors - the loop alone
    // triggers it, not the [loop] attribute, so the shape is what has to change.
    float result=0;
    if(light!=int(F(34).z) || span<0.5) {
        // When the resample runs below, it overwrites the centre value anyway, so not computing it here saves one
        // full evaluation of the shadow chain per froxel.
        result=Visibility(F(8).xyz+ray*depth,depth,light);
    } else {
        // Both Gauss points as one loop body. Written as two calls, fxc inlines the entire shadow chain twice and
        // that duplication is what dominated this kernel's compile; the arithmetic is untouched - same weights, same
        // order - so every froxel gets exactly the value it got before.
        float sum=0;
        [loop] for(int gaussIndex=0;gaussIndex<2;gaussIndex++) {
            float gaussT=gaussIndex==0?z0+span*0.21132487:z0+span*0.78867513;
            sum+=Visibility(F(8).xyz+ray*gaussT,gaussT,light);
        }
        result=0.5*sum;
    }
    return result;
}
// Two-lobe Henyey-Greenstein, the exact counterpart of FogMath.Phase: three quarters forward at the medium's own g,
// one quarter back at half that magnitude and the opposite sign. A single lobe has a 64:1 peak-to-back ratio at the
// scene default of 0.6, which reads as a flat sheet with the sun behind the viewer and as a blown-out disc with it in
// front; splitting it keeps the forward peak and restores enough back-scatter that a patch stays readable from either
// side. The weights sum to one, so the phase stays normalized, and g == 0 collapses to 1/(4 pi) for every cosine.
// 0.059683104 is 0.75/(4 pi) and 0.019894368 is 0.25/(4 pi); d^-1.5 is written as i*sqrt(i) to skip the pow.
float FogPhase(float c,float g) {
    float gb=-0.5*g;
    float d0=max(1.0+g*g-2.0*g*c,1e-4), d1=max(1.0+gb*gb-2.0*gb*c,1e-4);
    float i0=1.0/d0, i1=1.0/d1;
    return 0.059683104*(1.0-g*g)*i0*sqrt(i0)+0.019894368*(1.0-gb*gb)*i1*sqrt(i1);
}
[numthreads(4,4,4)] void CSMain(uint3 id : SV_DispatchThreadID) {
    if(all(float3(id)<F(10).xyz)) {
        float3 ray=Ray((float2(id.xy)+0.5)/F(10).xy); float depth=Z(float(id.z)+0.5);
        // A slice near the far end of the range is several metres thick, which is taller than a thin mist patch, so a
        // single centre sample makes that patch snap between hit and missed as the camera moves. Each volume is
        // therefore intersected with the ray and its weight scaled by the fraction of the slice it actually fills.
        float z0=Z(float(id.z)), span=max(Z(float(id.z)+1.0)-z0,1e-6);
        float3 p=F(8).xyz+ray*depth, windP=p-F(13).xyz;
        float4 global=F(11); float sigma=global.x*exp(-global.z*max(p.y-global.y,0.0))*Noise(windP,F(12).w,global.w);
        float exclude=1; float3 emission=0;
        // Directionality belongs to the medium, so the lights are resolved before the media are walked and the phase is
        // applied where each medium's own g is known. The expensive half of a light - the cascade atlas, the far map, the
        // cloud deck - stays once per light, never once per volume; only the phase, a handful of ALU, repeats.
        // rgb carries the radiance that reaches this froxel, w the cosine the phase is a function of.
        float3 viewRay=normalize(ray); int lightCount=clamp(int(F(34).x),0,8);
        float4 lights[8];
        for(int lightIndex=0;lightIndex<lightCount;lightIndex++) {
            uint b=35+lightIndex*4; float4 pos=F(b), color=F(b+1), direction=F(b+2), spot=F(b+3);
            float3 delta=pos.xyz-p; float distance=length(delta), attenuation=1; float3 l=-direction.xyz;
            if(direction.w<1.5) {
                l=delta/max(distance,0.0001); attenuation=1.0/max(distance*distance,0.0001);
                if(pos.w>0) { float win=saturate(1.0-pow(distance/pos.w,4.0)); attenuation*=win*win; }
                if(direction.w>0.5) attenuation*=smoothstep(spot.y,max(spot.x,spot.y+1e-6),dot(-l,direction.xyz));
            }
            else attenuation*=CloudShadow(p,l);
            lights[lightIndex]=float4(color.xyz*color.w*attenuation*SliceVisibility(ray,depth,z0,span,lightIndex),dot(viewRay,l));
        }
        float3 ambient=F(14).xyz, illumination=ambient;
        for(int phaseIndex=0;phaseIndex<lightCount;phaseIndex++) illumination+=lights[phaseIndex].xyz*FogPhase(lights[phaseIndex].w,F(9).w);
        float3 scattering=F(12).xyz*sigma*illumination;
        for(int i=0;i<min(int(F(13).w),32);i++) {
            uint base=192+i*8; float4 shape=F(base+5), medium=F(base+4), noiseRow=F(base+6);
            float3 lo=Transform(float4(F(8).xyz,1),base).xyz, ld=Transform(float4(ray,0),base).xyz;
            float t0,t1;
            if(shape.x>0.5) {
                float a=dot(ld,ld), b=dot(lo,ld), disc=b*b-a*(dot(lo,lo)-1.0);
                if(a<=1e-12 || disc<=0.0) continue;
                float s=sqrt(disc); t0=(-b-s)/a; t1=(-b+s)/a;
            }
            else {
                float3 inv=1.0/ld, ta=(-1.0-lo)*inv, tb=(1.0-lo)*inv;
                float3 tn=min(ta,tb), tf=max(ta,tb);
                t0=max(max(tn.x,tn.y),tn.z); t1=min(min(tf.x,tf.y),tf.z);
            }
            t0=max(t0,z0); t1=min(t1,z0+span);
            if(!(t1>t0)) continue;
            float coverage=(t1-t0)/span, ts=0.5*(t0+t1);
            float3 pl=lo+ld*ts, pw=F(8).xyz+ray*ts;
            float r=shape.x>0.5?length(pl):max(abs(pl.x),max(abs(pl.y),abs(pl.z)));
            float edge=saturate((1.0-r)/max(shape.z,1e-6)); float weight=(r<1?edge*edge*(3.0-2.0*edge):0.0)*coverage;
            if(shape.y>0.5) exclude*=1.0-weight;
            else {
                float d=medium.w*weight*Noise(pw-F(13).xyz,noiseRow.x,shape.w); sigma+=d;
                // This patch's own g, not the scene's - the whole reason it is carried per volume. A volume that never
                // authored one holds the scene value, so packing decides nothing and the shader reads one row.
                float3 light=ambient;
                for(int patchIndex=0;patchIndex<lightCount;patchIndex++) light+=lights[patchIndex].xyz*FogPhase(lights[patchIndex].w,noiseRow.y);
                scattering+=medium.xyz*d*light;
                emission+=F(base+7).xyz*d;
            }
        }
        float fade=1.0-smoothstep(F(8).w*0.9,F(8).w,depth);
        sigma*=exclude*fade; scattering*=exclude*fade; emission*=exclude*fade;
        outputVolume[id]=min(float4(scattering+emission,sigma),65000.0);
    }
}
""";

    const string FilterHlsl = CommonHlsl + """
Texture3D<float4> currentVolume : register(t1);
Texture3D<float4> historyVolume : register(t2);
RWTexture3D<float4> outputVolume : register(u0);
[numthreads(4,4,4)] void CSMain(uint3 id : SV_DispatchThreadID) {
    if(all(float3(id)<F(10).xyz)) {
        float4 current=currentVolume.Load(int4(id,0)), result=current;
        if(F(15).w>0.5 && F(10).w>0) {
            float3 p=F(8).xyz+Ray((float2(id.xy)+0.5)/F(10).xy)*Z(float(id.z)+0.5);
            float4 clip=Transform(float4(p,1),4); float depth=dot(p-F(15).xyz,F(16).xyz);
            float2 uv=clip.xy/max(clip.w,1e-6)*float2(0.5,-0.5)+0.5;
            if(clip.w>0 && all(uv>=0) && all(uv<=1) && depth>=0 && depth<=F(8).w) {
                float z=log(1.0+2.0*depth)/log(1.0+2.0*F(8).w);
                float4 history=historyVolume.SampleLevel(linearClamp,float3(uv,z),0), lo=current, hi=current;
                [unroll] for(int axis=0;axis<3;axis++) [unroll] for(int sign=-1;sign<=1;sign+=2) {
                    int3 c=int3(id); c[axis]+=sign; c=clamp(c,int3(0,0,0),int3(F(10).xyz)-1);
                    float4 value=currentVolume.Load(int4(c,0)); lo=min(lo,value); hi=max(hi,value);
                }
                float densityReject=saturate(1.0-abs(history.w-current.w)/max(current.w,0.0001));
                float lightReject=saturate(1.0-length(history.xyz-current.xyz)/max(length(current.xyz),0.0001));
                result=lerp(current,clamp(history,lo,hi),F(10).w*densityReject*lightReject);
            }
        }
        outputVolume[id]=result;
    }
}
""";

    const string IntegrationHlsl = CommonHlsl + """
Texture3D<float4> localVolume : register(t1);
RWTexture3D<float4> outputVolume : register(u0);
[numthreads(8,8,1)] void CSMain(uint3 id : SV_DispatchThreadID) {
    if(all(float2(id.xy)<F(10).xy)) {
        float rayLength=length(Ray((float2(id.xy)+0.5)/F(10).xy)); float4 acc=float4(0,0,0,1);
        outputVolume[uint3(id.xy,0)]=acc;
        for(uint z=0;z<uint(F(10).z);z++) {
            float4 local=localVolume.Load(int4(id.xy,z,0)); acc=Segment(acc,local,(Z(float(z)+1.0)-Z(float(z)))*rayLength);
            outputVolume[uint3(id.xy,z+1)]=acc;
        }
    }
}
""";
}
