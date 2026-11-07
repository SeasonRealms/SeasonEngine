// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

public sealed partial class VolumetricFogEffect
{
    /// <summary>The scene entry point instantiates FogSampler, passing an independent frame buffer and two read-only volumes; surface lighting parameters are left unchanged.</summary>
    public const string SamplingMsl = """
struct FogSampler {
    const device float4* data;
    texture3d<float,access::sample> integratedVolume;
    texture3d<float,access::sample> localVolume;
    float4 F(uint i) const { return data[i]; }
    float4 Transform(float4 p,uint i) const { return p.x*F(i)+p.y*F(i+1)+p.z*F(i+2)+p.w*F(i+3); }
    float Z(float k) const { return 0.5*(pow(1.0+2.0*F(8).w,k/F(10).z)-1.0); }
    float4 Segment(float4 acc,float4 local,float ds) const {
        float tau=local.w*ds,t=exp(-tau),integral=ds*(1.0-tau*0.5+tau*tau/6.0);
        if(tau>=0.001) integral=(1.0-t)/max(local.w,1e-30f);
        return float4(min(acc.xyz+acc.w*local.xyz*integral,65000.0f),acc.w*t);
    }
    float4 Column(int2 xy,int k,float depth) const {
        xy=clamp(xy,int2(0),int2(F(10).xy)-1);
        float4 acc=integratedVolume.read(uint3(xy,k),0);
        if(k<int(F(10).z)) {
            float2 uv=(float2(xy)+0.5)/F(10).xy;
            float4 world=Transform(float4(uv*float2(2,-2)+float2(-1,1),0.5,1),0);
            float3 ray=world.xyz/world.w-F(8).xyz;
            float rayLength=length(ray/max(dot(ray,F(9).xyz),1e-6f));
            acc=Segment(acc,localVolume.read(uint3(xy,k),0),max(depth-Z(float(k)),0.0f)*rayLength);
        }
        return acc;
    }
    float4 AtWorld(float3 world) const {
        float depth=dot(world-F(8).xyz,F(9).xyz);
        if(depth<=0) return float4(0,0,0,1);
        float4 clip=Transform(float4(world,1),22);
        if(clip.w<=0) return float4(0,0,0,1);
        float2 uv=saturate(clip.xy/clip.w*float2(0.5,-0.5)+0.5);
        depth=min(depth,F(8).w);
        int k=min(int(floor(log(1.0+2.0*depth)/log(1.0+2.0*F(8).w)*F(10).z)),int(F(10).z)-1);
        if(depth>=F(8).w) k=int(F(10).z);
        float2 q=uv*F(10).xy-0.5,f=fract(q); int2 b=int2(floor(q));
        float4 a=mix(Column(b,k,depth),Column(b+int2(1,0),k,depth),f.x);
        float4 c=mix(Column(b+int2(0,1),k,depth),Column(b+int2(1,1),k,depth),f.x);
        return mix(a,c,f.y);
    }
    float4 AtDepth(float2 uv,float depth) const {
        float4 p=Transform(float4(uv*float2(2,-2)+float2(-1,1),depth>=1.0?0.5:depth,1),18);
        float3 world=p.xyz/p.w;
        if(depth>=1.0) { float3 ray=world-F(8).xyz; world=F(8).xyz+ray*(F(8).w/max(dot(ray,F(9).xyz),1e-6f)); }
        return AtWorld(world);
    }
};
float4 FogTransparent(float4 premultiplied,float4 fog,bool additive) {
    return float4(premultiplied.xyz*fog.w+(additive?0.0:premultiplied.w)*fog.xyz,premultiplied.w);
}
float4 FogOpaque(float4 color,float4 fog,float ao) { return float4(color.xyz*ao*fog.w+fog.xyz,color.w); }
""";

    /// <summary>
    /// Fog composite glue for the main shader entry point; the four exits (text / sky / Sprite2D / PBR) share one shape
    /// and correspond line by line to VK ApplySceneFog and DX ApplySceneFog.
    /// It directly concatenates <see cref="SamplingMsl"/>, so the volumetric sampling math in the main scene shader and the composite Pass share one source with no second copy.
    /// Gating is a runtime parameter rather than a compile-time macro:
    /// the sky and opaque geometry are drawn in the same Pass but must not consume transmittance sampling, which only per-command pushed fogParams can express;
    /// a shared UBO would force a uniform read for the whole frame.
    /// </summary>
    public const string SceneSamplingMsl = SamplingMsl + """

float4 ApplySceneFog(float4 color,float3 worldPos,FogSampler fog,float4 fogParams) {
    if(fogParams.x>0.5) {
        float4 sampled=fog.AtWorld(worldPos);
        color.rgb=color.rgb*sampled.w+sampled.rgb;
    }
    return color;
}

""";

    public const string CompositeVertexMsl = """
#include <metal_stdlib>
using namespace metal;
vertex float4 VSMain(uint id [[vertex_id]]) {
    float2 p=float2((id<<1)&2,id&2);
    return float4(p*2.0-1.0,0,1);
}
""";

    public const string CompositeMsl = """
#include <metal_stdlib>
using namespace metal;
""" + SamplingMsl + """

fragment float4 PSMain(float4 position [[position]],const device float4* data [[buffer(0)]],
    constant float4& compositeParams [[buffer(1)]],texture3d<float> integrated [[texture(0)]],
    texture3d<float> local [[texture(1)]],texture2d<float> color [[texture(2)]],
    depth2d<float> depth [[texture(3)]],texture2d<float> aoTexture [[texture(4)]]) {
    constexpr sampler linearClamp(coord::normalized,address::clamp_to_edge,filter::linear);
    float2 uv=position.xy/float2(color.get_width(),color.get_height());
    uint2 pixel=uint2(position.xy);
    FogSampler fog{data,integrated,local};
    float ao=1.0;
    if(compositeParams.y>0.5) ao=mix(1.0,aoTexture.sample(linearClamp,uv,level(0)).r,compositeParams.x);
    return FogOpaque(color.read(pixel),fog.AtDepth(uv,depth.read(pixel)),ao);
}
""";

    // Each entry point instantiates its own read-only frame context; texture parameters are not stored in a shared UBO.
    const string CommonMsl = """
#include <metal_stdlib>
using namespace metal;
""" + WeatherShaders.ContextMsl + """
struct FogFrame {
    const device float4* data;
    float4 F(uint i) const { return data[i]; }
    float4 Transform(float4 p,uint i) const { return p.x*F(i)+p.y*F(i+1)+p.z*F(i+2)+p.w*F(i+3); }
    float Z(float k) const { return 0.5*(pow(1.0+2.0*F(8).w,k/F(10).z)-1.0); }
    float3 Ray(float2 uv) const {
        float4 p=Transform(float4(uv*float2(2,-2)+float2(-1,1),0.5,1),0);
        float3 r=p.xyz/p.w-F(8).xyz; return r/max(dot(r,F(9).xyz),1e-6f);
    }
    float4 Segment(float4 acc,float4 local,float ds) const {
        float tau=local.w*ds,t=exp(-tau),integral=ds*(1.0-tau*0.5+tau*tau/6.0);
        if(tau>=0.001) integral=(1.0-t)/max(local.w,1e-30f);
        return float4(min(acc.xyz+acc.w*local.xyz*integral,65000.0f),acc.w*t);
    }
""";

    const string NoiseMsl = """
#include <metal_stdlib>
using namespace metal;
kernel void CSMain(constant float4& params [[buffer(0)]],texture3d<float,access::write> outputVolume [[texture(0)]],uint3 id [[thread_position_in_grid]]) {
    if(all(id<uint3(64))) {
        uint h=id.x*73856093u ^ id.y*19349663u ^ id.z*83492791u ^ 0x9e3779b9u;
        h^=h>>16; h*=0x7feb352du; h^=h>>15; h*=0x846ca68bu; h^=h>>16;
        float n=float(h&255u)/255.0; outputVolume.write(float4(n),id);
    }
}
""";

    const string InjectionMsl = CommonMsl + """
    texture3d<float,access::sample> noiseVolume;
    depth2d<float,access::read> shadowAtlas;
    texture2d<float,access::sample> cloudNoise;
    depth2d<float,access::read> farShadow;
    depth2d<float,access::read> localShadow;
    float Noise(float3 p,float scale,float amount) const {
        float3 q=fract(p*scale)*64.0-0.5,f=fract(q); int3 b=int3(floor(q)); float n=0;
        for(int z=0;z<2;z++) for(int y=0;y<2;y++) for(int x=0;x<2;x++) {
            float3 w=mix(1.0-f,f,float3(x,y,z));
            n+=noiseVolume.read(uint3((b+int3(x,y,z))&63),0).x*w.x*w.y*w.z;
        }
        return 1.0+amount*(2.0*n-1.0);
    }
    float4 CloudSample(float2 uv) const {
        int2 size=int2(cloudNoise.get_width(),cloudNoise.get_height());
        float2 p=fract(uv)*float2(size)-0.5,f=fract(p); int2 b=int2(floor(p)); float4 n=0;
        for(int y=0;y<2;y++) for(int x=0;x<2;x++) {
            int2 c=(b+int2(x,y)+size)%size; float2 a=mix(1.0-f,f,float2(x,y));
            n+=cloudNoise.read(uint2(c),0)*a.x*a.y;
        }
        return n;
    }
    float CloudShadow(float3 p,float3 l) const {
        float tau=0;
        if(F(16).w>0.5 && F(116).x>0 && l.y>0) {
            float invY=1.0/max(l.y,0.05f);
            for(int i=0;i<min(int(F(115).w),3);i++) {
                float4 a=F(109+i),b=F(112+i); float2 pos=p.xz*0.001+l.xz*(max(a.x-p.y*0.001,0.0f)*invY);
                float4 n=CloudSample((pos+b.xy)*b.z);
                float d=saturate((n.x*mix(1.0f,n.w,0.7f)-(1.0-a.z))/max(a.z,0.001f));
                d*=saturate(1.0-b.w*0.5*(n.y+n.z)); tau+=d*a.w*a.y*invY;
            }
        }
        float4 header=F(118); if(F(16).w<0.5) header.y=0;
        WeatherCloudContext<const device float4*> weather{header,F(119),data+120,cloudNoise,F(184)};
        return (1.0-F(116).x*saturate(1.0-exp(-tau)))*weather.WeatherCloudShadow(p,l);
    }
    float ShadowTile(float3 p,uint matrixIndex,int tile) const {
        float4 clip=Transform(float4(p,1),matrixIndex); float result=1;
        if(clip.w>0) {
            float3 ndc=clip.xyz/clip.w;
            if(all(abs(ndc.xy)<=1.0) && ndc.z>=0 && ndc.z<=1) {
                int2 halfSize=int2(shadowAtlas.get_width(),shadowAtlas.get_height())/2,origin=int2(tile&1,tile>>1)*halfSize;
                float2 texel=(ndc.xy*float2(0.5,-0.5)+0.5)*float2(halfSize)+float2(origin);
                int count=F(14).w>1.0?4:1; float visible=0;
                for(int i=0;i<count;i++) {
                    float2 offset=count==1?float2(0):float2((i&1)-0.5,(i>>1)-0.5);
                    int2 c=clamp(int2(floor(texel+offset)),origin,origin+halfSize-1);
                    visible+=ndc.z-0.0002<=shadowAtlas.read(uint2(c))?1.0:0.0;
                }
                result=mix(1.0f,visible/float(count),saturate(F(89).y));
            }
        }
        return result;
    }
    // One 1024^2 orthographic map centred on the medium rather than on the camera, so its matrix does not move when the
    // eye does. Coarser than any cascade by design: it exists to keep a shaft readable where the cascades have already
    // faded out, not to resolve the trunk that makes it. Four taps over one texel of spread, because a hard edge on a
    // volumetric shaft reads as a hole punched in the light rather than as the silhouette of a branch.
    float FogFarShadow(float3 p,float depth,float reach) const {
        float4 clip=Transform(float4(p,1),448); float result=1;
        if(clip.w>0) {
            float3 ndc=clip.xyz/clip.w;
            if(all(abs(ndc.xy)<=1.0) && ndc.z>=0 && ndc.z<=1) {
                int2 size=int2(farShadow.get_width(),farShadow.get_height());
                float2 texel=(ndc.xy*float2(0.5,-0.5)+0.5)*float2(size)-0.5;
                int2 base=int2(floor(texel)); float visible=0; float bias=F(452).y;
                for(int i=0;i<4;i++) {
                    int2 c=clamp(base+int2(i&1,i>>1),int2(0),size-1);
                    visible+=ndc.z-bias<=farShadow.read(uint2(c))?1.0:0.0;
                }
                result=mix(1.0f,visible*0.25,saturate(F(89).y));
            }
        }
        // Hand over exactly where the cascades hand off: the same 10% band of the same reach, so a froxel crossing the
        // boundary keeps one shadow instead of acquiring a second. Inside the band the far map is not consulted at all -
        // it is the coarser of the two, and mixing it in would only soften edges the cascades already resolve correctly.
        return mix(1.0f,result,saturate((depth-reach*0.9)/max(reach*0.1,0.001f)));
    }
    // Local point-light cubes: up to two lamps whose light actually reaches the medium, six 90 degree faces each, packed four
    // to a row. Occlusion is the one thing a punctual light cannot otherwise express - without this term a torch inside a
    // barn lights the mist through the wall, because 1/d^2 attenuation has no notion of what stands between.
    //
    // The face is the dominant axis of (froxel - light), which is exactly the axis the CPU built that face's matrix around,
    // so no projection is re-derived here: the uploaded matrix is the only source of truth for both the projection and the
    // culling volume the texels came from. 128^2 per face is far coarser than a froxel, so the taps are spread over two rings
    // rather than one texel - a hard edge on a volumetric shaft reads as a hole punched in the light.
    //
    // Eight taps: four axial at radius two texels and four diagonals at radius one. The axial pair carries three quarters of
    // the second moment and the diagonals close the sectors between them, so no direction along the face is under-sampled.
    // The sum of squares is twelve in x and twelve in y with every cross term zero, which is what lets LocalFaceVisibility
    // fit a plane with two dot products instead of solving a normal-equation system.
    int2 LocalRingOffset(int tapIndex) const {
        // 0..3 walk -Y,+X,+Y,-X at radius two; 4..7 walk the diagonals at radius one. Generated from the index rather than
        // tabulated, so all four languages spell the same ring and there is no array to keep in step with the C# side.
        if(tapIndex<4) {
            int reach=((tapIndex^(tapIndex>>1))&1)!=0?2:-2;
            return (tapIndex&1)!=0?int2(reach,0):int2(0,reach);
        }
        int diagonal=tapIndex-4;
        return int2(((diagonal^(diagonal>>1))&1)!=0?1:-1,(diagonal>>1)!=0?1:-1);
    }
    // One face's visibility at p in 0..1, before the slot's strength is applied. Shared by the main face and, at a seam, by
    // the neighbour. The margin is meta.z / clip.w: clip.w is the distance along the face's own axis, and meta.z already
    // folded the lamp's near/far pair and the texel width into one number, so the division is the whole reconstruction.
    float LocalFaceVisibility(float3 p,uint matrixRow,int2 origin,float4 meta) const {
        float4 clip=Transform(float4(p,1),matrixRow);
        if(clip.w<=0) return 1;
        float3 ndc=clip.xyz/clip.w;
        // A tenth of a texel of slack on the bounds: the seam blend evaluates the neighbour face right up to its own edge,
        // and a strict 1.0 would reject that tap over a rounding error and hand back "unshadowed", drawing a bright hairline.
        if(!(all(abs(ndc.xy)<1.001f) && ndc.z>=0 && ndc.z<=1)) return 1;
        int faceSize=128;
        float2 texel=(ndc.xy*float2(0.5,-0.5)+0.5)*float2(faceSize)+float2(origin)-0.5f;
        int2 base=int2(floor(texel));
        float depths[8]; float sum=0,gx=0,gy=0,lo=1,hi=0;
        for(int tapIndex=0;tapIndex<8;tapIndex++) {
            int2 offset=LocalRingOffset(tapIndex);
            // tapDepth, not sample: sample is a Metal keyword ([[sample_id]], [[sample_mask]]).
            float tapDepth=localShadow.read(uint2(clamp(base+offset,origin,origin+faceSize-1)));
            depths[tapIndex]=tapDepth; sum+=tapDepth; lo=min(lo,tapDepth); hi=max(hi,tapDepth);
            gx+=offset.x*tapDepth; gy+=offset.y*tapDepth;
        }
        // 1/12 rather than 1/8: the least-squares slope over these eight offsets has sum(offset^2) = 12 as its denominator,
        // and the cross terms vanish, so the gradient is the offset-weighted sum rescaled. The plane's value at the centre is
        // the plain mean, because the offsets themselves sum to zero in both axes.
        gx*=0.083333333f; gy*=0.083333333f;
        float bias=meta.z/max(clip.w,0.001f);
        // A plane through a silhouette is not a plane through a surface. When the taps already disagree by more than a dozen
        // margins, the fit would invent a slope across the edge and smear the very silhouette the tap was meant to resolve,
        // so the tilt is dropped and every tap is read raw. Eight margins is twelve texels at the authored 1.5, comfortably
        // above the four-texel spread even for a surface leaning a full texel per texel, while a step wider than that is
        // a discontinuity the fit cannot follow anyway.
        float tilt=meta.w*(hi-lo<8.0f*bias?1.0f:0.0f);
        float plane=sum*0.125f;
        float visible=0;
        for(int tapIndex=0;tapIndex<8;tapIndex++) {
            int2 offset=LocalRingOffset(tapIndex);
            float fitted=clamp(plane+gx*offset.x+gy*offset.y,lo,hi);
            visible+=ndc.z-bias<=mix(depths[tapIndex],fitted,tilt)?1.0:0.0;
        }
        return visible*0.125f;
    }
    float FogLocalShadow(float3 p,int light) const {
        float result=1;
        for(int slotIndex=0;slotIndex<2;slotIndex++) {
            int row=456+slotIndex*25; float4 meta=F(uint(row+24));
            if(light==int(meta.x)) {
                float3 d=p-F(uint(35+light*4)).xyz,ad=abs(d);
                int face=ad.x>=ad.y?(ad.x>=ad.z?(d.x>0?0:1):(d.z>0?4:5)):(ad.y>=ad.z?(d.y>0?2:3):(d.z>0?4:5));
                int tile=slotIndex*6+face;
                int2 origin=int2(tile%4,tile/4)*128;
                float visible=LocalFaceVisibility(p,uint(row+face*4),origin,meta);
                // Seam: a froxel within a texel of the 45 degree boundary lands where the face it picked and the face it
                // passed over disagree about the silhouette, and that disagreement is a visible line running down a wall in
                // the mist. Both faces store the distance along their own axis, and the two axes are the two components of
                // the same vector, so on the boundary the two hold the same depth and mixing them is continuous. The band is
                // one texel wide, expressed in the face's own ndc - two units across 128 texels - so it needs no distance.
                float dominant=max(ad.x,max(ad.y,ad.z));
                float second=max(min(ad.x,ad.y),min(max(ad.x,ad.y),ad.z));
                float band=dominant*0.015625f;
                if(dominant-second<band) {
                    int secondAxis=face<2?(ad.y>=ad.z?1:2):(face<4?(ad.x>=ad.z?0:2):(ad.x>=ad.y?0:1));
                    float secondSign=secondAxis==0?d.x:(secondAxis==1?d.y:d.z);
                    int secondFace=secondAxis*2+(secondSign>0?0:1),secondTile=slotIndex*6+secondFace;
                    float other=LocalFaceVisibility(p,uint(row+secondFace*4),int2(secondTile%4,secondTile/4)*128,meta);
                    float edge=saturate((dominant-second)/max(band,1e-6f));
                    visible=mix(other,visible,0.5f+0.5f*edge*edge*(3.0f-2.0f*edge));
                }
                result*=mix(1.0f,visible,saturate(meta.y));
            }
        }
        return result;
    }
    float Visibility(float3 p,float depth,int light) const {
        float result=1; float4 indices=F(34),flags=F(88),splits=F(87);
        if(light==int(indices.z) && flags.x>0.5 && depth<splits.w) {
            int count=clamp(int(flags.y),1,3),cascade=0;
            if(count>1 && depth>splits.x) cascade=1;
            if(count>2 && depth>splits.y) cascade=2;
            result=ShadowTile(p,67+cascade*4,cascade);
            float end=splits[cascade],start=cascade==0?0:splits[cascade-1];
            float blend=saturate((depth-mix(start,end,0.9f))/max((end-start)*0.1,0.001f)),next=1;
            if(cascade+1<count) next=ShadowTile(p,67+(cascade+1)*4,cascade+1);
            result=mix(result,next,blend);
            result=mix(result,1.0f,saturate((depth-splits.w*0.9)/max(splits.w*0.1,0.001f)));
        }
        if(light==int(indices.w) && F(89).x>0.5) result*=ShadowTile(p,83,3);
        // The fog's own far map, and only for the cascade light: it is that light's depth from a different viewpoint,
        // not a second occluder. Past the cascade reach the fog had nothing to consult, which is what flattened every
        // shaft seen from outside the volume into a slab of uniform light.
        if(light==int(indices.z) && F(448).x>0.5) result*=FogFarShadow(p,depth,splits.w);
        // The one term here that has nothing to do with the sun. A slot whose meta row names a different light costs two
        // row reads and a comparison, which is why this is unconditional rather than gated by a separate enable flag.
        result*=FogLocalShadow(p,light);
        return result;
    }
    // A slice 20 m out is 1.35 m thick at the High tier, and a low sun crosses that much depth of a trunk's shadow
    // easily. One sample at the slice centre therefore collapses a beam onto a single depth, which is what makes shafts
    // read as mottling instead of stripes. The medium terms (sigma, scattering, emission) are smooth over the same span
    // and stay at the centre; only the term carrying the occluder's silhouette is re-evaluated, and only for the one light
    // that casts into the cascade atlas - the other slots have no depth map to consult, so re-sampling them would change
    // nothing. Two-point Gauss-Legendre over the span. The default is the old centre sample, so a slice thinner than half
    // a metre (everything the near field produces) is bit-identical to the previous behaviour.
    float SliceVisibility(float3 ray,float depth,float z0,float span,int light) const {
        float result=Visibility(F(8).xyz+ray*depth,depth,light);
        if(light==int(F(34).z) && span>=0.5f) {
            float a=z0+span*0.21132487f,b=z0+span*0.78867513f;
            result=0.5f*(Visibility(F(8).xyz+ray*a,a,light)+Visibility(F(8).xyz+ray*b,b,light));
        }
        return result;
    }
    // Two-lobe Henyey-Greenstein, the exact counterpart of FogMath.Phase: three quarters forward at the medium's own g,
    // one quarter back at half that magnitude and the opposite sign. A single lobe has a 64:1 peak-to-back ratio at the
    // scene default of 0.6, which reads as a flat sheet with the sun behind the viewer and as a blown-out disc with it in
    // front; splitting it keeps the forward peak and restores enough back-scatter that a patch stays readable from either
    // side. The weights sum to one, so the phase stays normalized, and g == 0 collapses to 1/(4 pi) for every cosine.
    // 0.059683104 is 0.75/(4 pi) and 0.019894368 is 0.25/(4 pi); d^-1.5 is written as i*sqrt(i) to skip the pow.
    float FogPhase(float c,float g) const {
        float gb=-0.5f*g;
        float d0=max(1.0f+g*g-2.0f*g*c,1e-4f), d1=max(1.0f+gb*gb-2.0f*gb*c,1e-4f);
        float i0=1.0f/d0, i1=1.0f/d1;
        return 0.059683104f*(1.0f-g*g)*i0*sqrt(i0)+0.019894368f*(1.0f-gb*gb)*i1*sqrt(i1);
    }
    float4 Inject(uint3 id) const {
        float3 ray=Ray((float2(id.xy)+0.5)/F(10).xy); float depth=Z(float(id.z)+0.5);
        // A slice near the far end of the range is several metres thick, which is taller than a thin mist patch, so a
        // single centre sample makes that patch snap between hit and missed as the camera moves. Each volume is
        // therefore intersected with the ray and its weight scaled by the fraction of the slice it actually fills.
        float z0=Z(float(id.z)),span=max(Z(float(id.z)+1.0)-z0,1e-6f);
        float3 p=F(8).xyz+ray*depth,windP=p-F(13).xyz; float4 global=F(11);
        float sigma=global.x*exp(-global.z*max(p.y-global.y,0.0f))*Noise(windP,F(12).w,global.w);
        float3 scattering=F(12).xyz*sigma; float exclude=1;
        // Per-volume self-illumination (row 7): radiance the patch emits regardless of what lights it.
        float3 emission=float3(0.0);
        // Directionality belongs to the medium, so the lights are resolved before the media are walked and the phase is
        // applied where each medium's own g is known. The expensive half of a light - the cascade atlas, the far map, the
        // cloud deck - stays once per light, never once per volume; only the phase, a handful of ALU, repeats.
        // rgb carries the radiance that reaches this froxel, w the cosine the phase is a function of.
        float3 viewRay=normalize(ray); int lightCount=clamp(int(F(34).x),0,8);
        float4 lights[8];
        for(int lightIndex=0;lightIndex<lightCount;lightIndex++) {
            uint b=35+lightIndex*4; float4 pos=F(b),color=F(b+1),direction=F(b+2),spot=F(b+3);
            float3 delta=pos.xyz-p; float distance=length(delta),attenuation=1; float3 l=-direction.xyz;
            if(direction.w<1.5f) {
                l=delta/max(distance,0.0001f); attenuation=1.0f/max(distance*distance,0.0001f);
                if(pos.w>0) { float win=saturate(1.0f-pow(distance/pos.w,4.0f)); attenuation*=win*win; }
                if(direction.w>0.5f) attenuation*=smoothstep(spot.y,max(spot.x,spot.y+1e-6f),dot(-l,direction.xyz));
            }
            else attenuation*=CloudShadow(p,l);
            lights[lightIndex]=float4(color.xyz*color.w*attenuation*SliceVisibility(ray,depth,z0,span,lightIndex),dot(viewRay,l));
        }
        float3 ambient=F(14).xyz,illumination=ambient;
        for(int phaseIndex=0;phaseIndex<lightCount;phaseIndex++) illumination+=lights[phaseIndex].xyz*FogPhase(lights[phaseIndex].w,F(9).w);
        scattering=F(12).xyz*sigma*illumination;
        for(int i=0;i<min(int(F(13).w),32);i++) {
            uint base=192+i*8; float4 shape=F(base+5),medium=F(base+4),noiseRow=F(base+6);
            float3 lo=Transform(float4(F(8).xyz,1),base).xyz,ld=Transform(float4(ray,0),base).xyz;
            float t0,t1;
            if(shape.x>0.5) {
                float a=dot(ld,ld),b=dot(lo,ld),disc=b*b-a*(dot(lo,lo)-1.0f);
                if(a<=1e-12f || disc<=0.0f) continue;
                float s=sqrt(disc); t0=(-b-s)/a; t1=(-b+s)/a;
            }
            else {
                float3 inv=float3(1.0f)/ld,ta=(-1.0f-lo)*inv,tb=(1.0f-lo)*inv;
                float3 tn=min(ta,tb),tf=max(ta,tb);
                t0=max(max(tn.x,tn.y),tn.z); t1=min(min(tf.x,tf.y),tf.z);
            }
            t0=max(t0,z0); t1=min(t1,z0+span);
            if(!(t1>t0)) continue;
            float coverage=(t1-t0)/span,ts=0.5f*(t0+t1);
            float3 pl=lo+ld*ts,pw=F(8).xyz+ray*ts;
            float r=shape.x>0.5?length(pl):max(abs(pl.x),max(abs(pl.y),abs(pl.z)));
            float edge=saturate((1.0f-r)/max(shape.z,1e-6f)),weight=(r<1?edge*edge*(3.0f-2.0f*edge):0.0f)*coverage;
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
        return min(float4(scattering+emission,sigma),65000.0f);
    }
};
kernel void CSMain(constant float4& params [[buffer(0)]],const device float4* data [[buffer(1)]],
    texture3d<float,access::sample> noiseVolume [[texture(0)]],depth2d<float,access::read> shadowAtlas [[texture(1)]],
    texture2d<float,access::sample> cloudNoise [[texture(2)]],texture3d<float,access::write> outputVolume [[texture(3)]],
    depth2d<float,access::read> farShadow [[texture(4)]],depth2d<float,access::read> localShadow [[texture(5)]],
    uint3 id [[thread_position_in_grid]]) {
    FogFrame f{data,noiseVolume,shadowAtlas,cloudNoise,farShadow,localShadow};
    if(all(float3(id)<f.F(10).xyz)) outputVolume.write(f.Inject(id),id);
}
""";

    const string FilterMsl = CommonMsl + """
};
kernel void CSMain(constant float4& params [[buffer(0)]],const device float4* data [[buffer(1)]],
    texture3d<float,access::sample> currentVolume [[texture(0)]],texture3d<float,access::sample> historyVolume [[texture(1)]],
    texture3d<float,access::write> outputVolume [[texture(2)]],sampler linearClamp [[sampler(0)]],uint3 id [[thread_position_in_grid]]) {
    FogFrame f{data};
    if(all(float3(id)<f.F(10).xyz)) {
        float4 current=currentVolume.read(id,0),result=current;
        if(f.F(15).w>0.5 && f.F(10).w>0) {
            float3 p=f.F(8).xyz+f.Ray((float2(id.xy)+0.5)/f.F(10).xy)*f.Z(float(id.z)+0.5);
            float4 clip=f.Transform(float4(p,1),4); float depth=dot(p-f.F(15).xyz,f.F(16).xyz);
            float2 uv=clip.xy/max(clip.w,1e-6f)*float2(0.5,-0.5)+0.5;
            if(clip.w>0 && all(uv>=0) && all(uv<=1) && depth>=0 && depth<=f.F(8).w) {
                float z=log(1.0+2.0*depth)/log(1.0+2.0*f.F(8).w);
                float4 history=historyVolume.sample(linearClamp,float3(uv,z),level(0)),lo=current,hi=current;
                for(int axis=0;axis<3;axis++) for(int sign=-1;sign<=1;sign+=2) {
                    int3 c=int3(id); c[axis]+=sign; c=clamp(c,int3(0),int3(f.F(10).xyz)-1);
                    float4 value=currentVolume.read(uint3(c),0); lo=min(lo,value); hi=max(hi,value);
                }
                float densityReject=saturate(1.0-abs(history.w-current.w)/max(current.w,0.0001f));
                float lightReject=saturate(1.0-length(history.xyz-current.xyz)/max(length(current.xyz),0.0001f));
                result=mix(current,clamp(history,lo,hi),f.F(10).w*densityReject*lightReject);
            }
        }
        outputVolume.write(result,id);
    }
}
""";

    const string IntegrationMsl = CommonMsl + """
};
kernel void CSMain(constant float4& params [[buffer(0)]],const device float4* data [[buffer(1)]],
    texture3d<float,access::sample> localVolume [[texture(0)]],texture3d<float,access::write> outputVolume [[texture(1)]],uint3 id [[thread_position_in_grid]]) {
    FogFrame f{data};
    if(all(float2(id.xy)<f.F(10).xy)) {
        float rayLength=length(f.Ray((float2(id.xy)+0.5)/f.F(10).xy)); float4 acc=float4(0,0,0,1);
        outputVolume.write(acc,uint3(id.xy,0));
        for(uint z=0;z<uint(f.F(10).z);z++) {
            float4 local=localVolume.read(uint3(id.xy,z),0); acc=f.Segment(acc,local,(f.Z(float(z)+1.0)-f.Z(float(z)))*rayLength);
            outputVolume.write(acc,uint3(id.xy,z+1));
        }
    }
}
""";
}
