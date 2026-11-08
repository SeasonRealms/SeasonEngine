// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

public sealed partial class VolumetricFogEffect
{
    /// <summary>The backend declares vec4 fogFrame[] and sampler3D fogIntegrated/fogLocal; each column completes integration first, then performs XY interpolation.</summary>
    public const string SamplingGlsl = """
vec4 FogF(int i) { return fogFrame[i]; }
vec4 FogTransform(vec4 p,int i) { return p.x*FogF(i)+p.y*FogF(i+1)+p.z*FogF(i+2)+p.w*FogF(i+3); }
float FogZ(float k) { return 0.5*(pow(1.0+2.0*FogF(8).w,k/FogF(10).z)-1.0); }
vec4 FogSegment(vec4 acc,vec4 local,float ds) {
    float tau=local.w*ds,t=exp(-tau),integral=ds*(1.0-tau*0.5+tau*tau/6.0);
    if(tau>=0.001) integral=(1.0-t)/max(local.w,1e-30);
    return vec4(min(acc.xyz+acc.w*local.xyz*integral,vec3(65000)),acc.w*t);
}
vec4 FogColumn(ivec2 xy,int k,float depth) {
    xy=clamp(xy,ivec2(0),ivec2(FogF(10).xy)-1);
    vec4 acc=texelFetch(fogIntegrated,ivec3(xy,k),0);
    if(k<int(FogF(10).z)) {
        vec2 uv=(vec2(xy)+0.5)/FogF(10).xy;
        vec4 world=FogTransform(vec4(uv*vec2(2,-2)+vec2(-1,1),0.5,1),0);
        vec3 ray=world.xyz/world.w-FogF(8).xyz;
        float rayLength=length(ray/max(dot(ray,FogF(9).xyz),1e-6));
        acc=FogSegment(acc,texelFetch(fogLocal,ivec3(xy,k),0),max(depth-FogZ(float(k)),0.0)*rayLength);
    }
    return acc;
}
vec4 FogAtWorld(vec3 world) {
    float depth=dot(world-FogF(8).xyz,FogF(9).xyz);
    if(depth<=0) return vec4(0,0,0,1);
    vec4 clip=FogTransform(vec4(world,1),22);
    if(clip.w<=0) return vec4(0,0,0,1);
    vec2 uv=clamp(clip.xy/clip.w*vec2(0.5,-0.5)+0.5,vec2(0),vec2(1));
    depth=min(depth,FogF(8).w);
    int k=min(int(floor(log(1.0+2.0*depth)/log(1.0+2.0*FogF(8).w)*FogF(10).z)),int(FogF(10).z)-1);
    if(depth>=FogF(8).w) k=int(FogF(10).z);
    vec2 q=uv*FogF(10).xy-0.5,f=fract(q); ivec2 b=ivec2(floor(q));
    vec4 a=mix(FogColumn(b,k,depth),FogColumn(b+ivec2(1,0),k,depth),f.x);
    vec4 c=mix(FogColumn(b+ivec2(0,1),k,depth),FogColumn(b+ivec2(1,1),k,depth),f.x);
    return mix(a,c,f.y);
}
vec4 FogAtDepth(vec2 uv,float depth) {
    vec4 p=FogTransform(vec4(uv*vec2(2,-2)+vec2(-1,1),depth>=1.0?0.5:depth,1),18);
    vec3 world=p.xyz/p.w;
    if(depth>=1.0) { vec3 ray=world-FogF(8).xyz; world=FogF(8).xyz+ray*(FogF(8).w/max(dot(ray,FogF(9).xyz),1e-6)); }
    return FogAtWorld(world);
}
vec4 FogTransparent(vec4 premultiplied,vec4 fog,bool additive) {
    return vec4(premultiplied.xyz*fog.w+(additive?0.0:premultiplied.w)*fog.xyz,premultiplied.w);
}
vec4 FogOpaque(vec4 color,vec4 fog,float ao) { return vec4(color.xyz*ao*fog.w+fog.xyz,color.w); }
""";

    public const string CompositeVertexGlsl = """
#version 450
void main() {
    vec2 p=vec2((gl_VertexIndex<<1)&2,gl_VertexIndex&2);
    gl_Position=vec4(p*2.0-1.0,0,1);
}
""";

    public const string CompositeGlsl = """
#version 450
layout(std430,binding=0) readonly buffer Frame { vec4 fogFrame[]; };
layout(binding=1) uniform sampler3D fogIntegrated;
layout(binding=2) uniform sampler3D fogLocal;
layout(binding=3) uniform sampler2D sceneColor;
layout(binding=4) uniform sampler2D sceneDepth;
layout(binding=5) uniform sampler2D sceneAo;
layout(push_constant) uniform Params { vec4 compositeParams; };
layout(location=0) out vec4 result;
""" + SamplingGlsl + """

void main() {
    vec2 uv=gl_FragCoord.xy/vec2(textureSize(sceneColor,0));
    ivec2 pixel=ivec2(gl_FragCoord.xy);
    float ao=1.0;
    if(compositeParams.y>0.5) ao=mix(1.0,textureLod(sceneAo,uv,0).r,compositeParams.x);
    result=FogOpaque(texelFetch(sceneColor,pixel,0),FogAtDepth(uv,texelFetch(sceneDepth,pixel,0).r),ao);
}
""";

    const string CommonGlsl = """
#version 450
layout(push_constant) uniform Params { vec4 dispatchSize; };
layout(std430,binding=1) readonly buffer Frame { vec4 frameData[]; };
vec4 F(int i) { return frameData[i]; }
vec4 Transform(vec4 p,int i) { return p.x*F(i)+p.y*F(i+1)+p.z*F(i+2)+p.w*F(i+3); }
float Z(float k) { return 0.5*(pow(1.0+2.0*F(8).w,k/F(10).z)-1.0); }
vec3 Ray(vec2 uv) {
    vec4 p=Transform(vec4(uv*vec2(2,-2)+vec2(-1,1),0.5,1),0);
    vec3 r=p.xyz/p.w-F(8).xyz; return r/max(dot(r,F(9).xyz),1e-6);
}
vec4 Segment(vec4 acc,vec4 local,float ds) {
    float tau=local.w*ds,t=exp(-tau),integral=ds*(1.0-tau*0.5+tau*tau/6.0);
    if(tau>=0.001) integral=(1.0-t)/max(local.w,1e-30);
    return vec4(min(acc.xyz+acc.w*local.xyz*integral,vec3(65000)),acc.w*t);
}
float Sat(float x) { return clamp(x,0.0,1.0); }
""";

    const string NoiseGlsl = """
#version 450
layout(push_constant) uniform Params { vec4 dispatchSize; };
layout(binding=1,rgba8) uniform writeonly image3D outputVolume;
layout(local_size_x=4,local_size_y=4,local_size_z=4) in;
void main() {
    uvec3 id=gl_GlobalInvocationID;
    if(all(lessThan(id,uvec3(64)))) {
        uint h=id.x*73856093u ^ id.y*19349663u ^ id.z*83492791u ^ 0x9e3779b9u;
        h^=h>>16; h*=0x7feb352du; h^=h>>15; h*=0x846ca68bu; h^=h>>16;
        float n=float(h&255u)/255.0; imageStore(outputVolume,ivec3(id),vec4(n));
    }
}
""";

    const string InjectionGlsl = CommonGlsl + """
layout(binding=2) uniform sampler3D noiseVolume;
layout(binding=3) uniform sampler2D shadowAtlas;
layout(binding=4) uniform sampler2D cloudNoise;
layout(binding=5,rgba16f) uniform writeonly image3D outputVolume;
layout(binding=6) uniform sampler2D farShadow;
layout(binding=7) uniform sampler2D localShadow;
float Noise(vec3 p,float scale,float amount) {
    vec3 q=fract(p*scale)*64.0-0.5,f=fract(q); ivec3 b=ivec3(floor(q)); float n=0;
    for(int z=0;z<2;z++) for(int y=0;y<2;y++) for(int x=0;x<2;x++) {
        vec3 w=mix(1.0-f,f,vec3(x,y,z));
        n+=texelFetch(noiseVolume,(b+ivec3(x,y,z))&63,0).x*w.x*w.y*w.z;
    }
    return 1.0+amount*(2.0*n-1.0);
}
vec4 CloudSample(vec2 uv) {
    ivec2 size=textureSize(cloudNoise,0); vec2 p=fract(uv)*vec2(size)-0.5,f=fract(p); ivec2 b=ivec2(floor(p)); vec4 n=vec4(0);
    for(int y=0;y<2;y++) for(int x=0;x<2;x++) {
        ivec2 c=(b+ivec2(x,y)+size)%size; vec2 a=mix(1.0-f,f,vec2(x,y));
        n+=texelFetch(cloudNoise,c,0)*a.x*a.y;
    }
    return n;
}
vec4 WeatherH(int i) { vec4 h=F(118+i); if(i==0 && F(16).w<0.5) h.y=0; return h; }
vec4 WeatherR(int i,int row) { return F(120+i*8+row); }
// 2-7 lightning: in the fog frame SceneLightParams starts at index 32, and WeatherFlash is row 152 within it, so the absolute position is 184.
// The graphics pipeline reads the same-named UBO member; both sides are the same data.
vec4 WeatherFlash() { return F(184); }
vec4 WeatherNoise(vec2 uv) { return CloudSample(uv); }
""" + WeatherShaders.CommonGlsl + WeatherShaders.CloudGlsl + """
float CloudShadow(vec3 p,vec3 l) {
    float tau=0;
    if(F(16).w>0.5 && F(116).x>0 && l.y>0) {
        float invY=1.0/max(l.y,0.05);
        for(int i=0;i<min(int(F(115).w),3);i++) {
            vec4 a=F(109+i),b=F(112+i); vec2 pos=p.xz*0.001+l.xz*(max(a.x-p.y*0.001,0.0)*invY);
            vec4 n=CloudSample((pos+b.xy)*b.z);
            float d=Sat((n.x*mix(1.0,n.w,0.7)-(1.0-a.z))/max(a.z,0.001));
            d*=Sat(1.0-b.w*0.5*(n.y+n.z)); tau+=d*a.w*a.y*invY;
        }
    }
    return (1.0-F(116).x*Sat(1.0-exp(-tau)))*WeatherCloudShadow(p,l);
}
float ShadowTile(vec3 p,int matrixIndex,int tile) {
    vec4 clip=Transform(vec4(p,1),matrixIndex); float result=1;
    if(clip.w>0) {
        vec3 ndc=clip.xyz/clip.w;
        if(all(lessThanEqual(abs(ndc.xy),vec2(1))) && ndc.z>=0 && ndc.z<=1) {
            ivec2 halfSize=textureSize(shadowAtlas,0)/2,origin=ivec2(tile&1,tile>>1)*halfSize;
            vec2 texel=(ndc.xy*vec2(0.5,-0.5)+0.5)*vec2(halfSize)+vec2(origin);
            int count=F(14).w>1.0?4:1; float visible=0;
            for(int i=0;i<count;i++) {
                vec2 offset=count==1?vec2(0):vec2((i&1)-0.5,(i>>1)-0.5);
                ivec2 c=clamp(ivec2(floor(texel+offset)),origin,origin+halfSize-1);
                visible+=ndc.z-0.0002<=texelFetch(shadowAtlas,c,0).x?1.0:0.0;
            }
            result=mix(1.0,visible/float(count),Sat(F(89).y));
        }
    }
    return result;
}
// One 1024^2 orthographic map centred on the medium rather than on the camera, so its matrix does not move when the eye
// does. Coarser than any cascade by design: it exists to keep a shaft readable where the cascades have already faded
// out, not to resolve the trunk that makes it. Four taps over one texel of spread, because a hard edge on a volumetric
// shaft reads as a hole punched in the light rather than as the silhouette of a branch.
float FogFarShadow(vec3 p,float depth,float reach) {
    vec4 clip=Transform(vec4(p,1),448); float result=1;
    if(clip.w>0) {
        vec3 ndc=clip.xyz/clip.w;
        if(all(lessThanEqual(abs(ndc.xy),vec2(1))) && ndc.z>=0 && ndc.z<=1) {
            ivec2 size=textureSize(farShadow,0);
            vec2 texel=(ndc.xy*vec2(0.5,-0.5)+0.5)*vec2(size)-0.5;
            ivec2 base=ivec2(floor(texel)); float visible=0; float bias=F(452).y;
            for(int i=0;i<4;i++) {
                ivec2 c=clamp(base+ivec2(i&1,i>>1),ivec2(0),size-1);
                visible+=ndc.z-bias<=texelFetch(farShadow,c,0).x?1.0:0.0;
            }
            result=mix(1.0,visible*0.25,Sat(F(89).y));
        }
    }
    // Hand over exactly where the cascades hand off: the same 10% band of the same reach, so a froxel crossing the
    // boundary keeps one shadow instead of acquiring a second. Inside the band the far map is not consulted at all -
    // it is the coarser of the two, and mixing it in would only soften edges the cascades already resolve correctly.
    return mix(1.0,result,Sat((depth-reach*0.9)/max(reach*0.1,0.001)));
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
ivec2 LocalRingOffset(int tapIndex) {
    // 0..3 walk -Y,+X,+Y,-X at radius two; 4..7 walk the diagonals at radius one. Generated from the index rather than
    // tabulated so all four languages spell the ring the same way: there is no array to keep in step with the C# side.
    if(tapIndex<4) {
        int reach=((tapIndex^(tapIndex>>1))&1)!=0?2:-2;
        return (tapIndex&1)!=0?ivec2(reach,0):ivec2(0,reach);
    }
    int diagonal=tapIndex-4;
    return ivec2(((diagonal^(diagonal>>1))&1)!=0?1:-1,(diagonal>>1)!=0?1:-1);
}
// One face's visibility at p in 0..1, before the slot's strength is applied. Shared by the main face and, at a seam, by the
// neighbour. The margin is meta.z / clip.w: clip.w is the distance along the face's own axis, and meta.z already folded the
// lamp's near/far pair and the texel width into one number, so the division is the whole of the reconstruction.
float LocalFaceVisibility(vec3 p,int matrixRow,ivec2 origin,vec4 meta) {
    vec4 clip=Transform(vec4(p,1),matrixRow);
    if(clip.w<=0) return 1.0;
    vec3 ndc=clip.xyz/clip.w;
    // A tenth of a texel of slack on the bounds: the seam blend evaluates the neighbour face right up to its own edge, and a
    // strict 1.0 would reject that tap over a rounding error and hand back "unshadowed", drawing a bright hairline.
    if(!(all(lessThan(abs(ndc.xy),vec2(1.001))) && ndc.z>=0 && ndc.z<=1)) return 1.0;
    int faceSize=128;
    vec2 texel=(ndc.xy*vec2(0.5,-0.5)+0.5)*vec2(faceSize)+vec2(origin)-0.5;
    ivec2 base=ivec2(floor(texel));
    float depths[8]; float sum=0,gx=0,gy=0,lo=1,hi=0;
    for(int tapIndex=0;tapIndex<8;tapIndex++) {
        ivec2 offset=LocalRingOffset(tapIndex);
        // tapDepth, not sample: sample is a reserved word in GLSL (it is the interpolation qualifier).
        float tapDepth=texelFetch(localShadow,clamp(base+offset,origin,origin+faceSize-1),0).x;
        depths[tapIndex]=tapDepth; sum+=tapDepth; lo=min(lo,tapDepth); hi=max(hi,tapDepth);
        gx+=float(offset.x)*tapDepth; gy+=float(offset.y)*tapDepth;
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
    for(int tapIndex=0;tapIndex<8;tapIndex++) {
        ivec2 offset=LocalRingOffset(tapIndex);
        float fitted=clamp(plane+gx*float(offset.x)+gy*float(offset.y),lo,hi);
        visible+=ndc.z-bias<=mix(depths[tapIndex],fitted,tilt)?1.0:0.0;
    }
    return visible*0.125;
}
float FogLocalShadow(vec3 p,int light) {
    float result=1;
    for(int slotIndex=0;slotIndex<2;slotIndex++) {
        int row=456+slotIndex*25; vec4 meta=F(row+24);
        if(light==int(meta.x)) {
            vec3 d=p-F(35+light*4).xyz, ad=abs(d);
            int face=ad.x>=ad.y?(ad.x>=ad.z?(d.x>0?0:1):(d.z>0?4:5)):(ad.y>=ad.z?(d.y>0?2:3):(d.z>0?4:5));
            int tile=slotIndex*6+face;
            ivec2 origin=ivec2(tile%4,tile/4)*128;
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
                float other=LocalFaceVisibility(p,row+secondFace*4,ivec2(secondTile%4,secondTile/4)*128,meta);
                float edge=Sat((dominant-second)/max(band,1e-6));
                visible=mix(other,visible,0.5+0.5*edge*edge*(3.0-2.0*edge));
            }
            result*=mix(1.0,visible,Sat(meta.y));
        }
    }
    return result;
}
float Visibility(vec3 p,float depth,int light) {
    float result=1; vec4 indices=F(34),flags=F(88),splits=F(87);
    if(light==int(indices.z) && flags.x>0.5 && depth<splits.w) {
        int count=clamp(int(flags.y),1,3),cascade=0;
        if(count>1 && depth>splits.x) cascade=1;
        if(count>2 && depth>splits.y) cascade=2;
        result=ShadowTile(p,67+cascade*4,cascade);
        float end=splits[cascade],start=cascade==0?0:splits[cascade-1];
        float blend=Sat((depth-mix(start,end,0.9))/max((end-start)*0.1,0.001)),next=1;
        if(cascade+1<count) next=ShadowTile(p,67+(cascade+1)*4,cascade+1);
        result=mix(result,next,blend);
        result=mix(result,1.0,Sat((depth-splits.w*0.9)/max(splits.w*0.1,0.001)));
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
float SliceVisibility(vec3 ray,float depth,float z0,float span,int light) {
    // One exit, both arms writing the same variable, matching DX. The centre sample is only taken when the
    // resample does not run: the two Gauss taps below overwrite it anyway, so computing it first spent one full
    // evaluation of the shadow chain - eight ring taps, two face reads and the seam blend - on a value that was
    // then thrown away, on exactly the froxels far enough out to make that chain expensive.
    float result=0;
    if(light!=int(F(34).z) || span<0.5) {
        result=Visibility(F(8).xyz+ray*depth,depth,light);
    } else {
        // Both Gauss points as one loop body. glslang keeps Visibility as a function, so the two-call form would
        // cost two OpFunctionCall instead of one and nothing else - see the note on DX, where DXBC has no call
        // instruction and the same shape had to be inlined in full at every site. Same weights, same order.
        float sum=0;
        for(int gaussIndex=0;gaussIndex<2;gaussIndex++) {
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
layout(local_size_x=4,local_size_y=4,local_size_z=4) in;
void main() {
    uvec3 id=gl_GlobalInvocationID;
    if(all(lessThan(vec3(id),F(10).xyz))) {
        vec3 ray=Ray((vec2(id.xy)+0.5)/F(10).xy); float depth=Z(float(id.z)+0.5);
        // A slice near the far end of the range is several metres thick, which is taller than a thin mist patch, so a
        // single centre sample makes that patch snap between hit and missed as the camera moves. Each volume is
        // therefore intersected with the ray and its weight scaled by the fraction of the slice it actually fills.
        float z0=Z(float(id.z)),span=max(Z(float(id.z)+1.0)-z0,1e-6);
        vec3 p=F(8).xyz+ray*depth,windP=p-F(13).xyz; vec4 global=F(11);
        float sigma=global.x*exp(-global.z*max(p.y-global.y,0.0))*Noise(windP,F(12).w,global.w);
        vec3 scattering=F(12).xyz*sigma; float exclude=1;
        // Per-volume self-illumination (row 7): radiance the patch emits regardless of what lights it.
        vec3 emission=vec3(0.0);
        // Directionality belongs to the medium, so the lights are resolved before the media are walked and the phase is
        // applied where each medium's own g is known. The expensive half of a light - the cascade atlas, the far map, the
        // cloud deck - stays once per light, never once per volume; only the phase, a handful of ALU, repeats.
        // rgb carries the radiance that reaches this froxel, w the cosine the phase is a function of.
        vec3 viewRay=normalize(ray); int lightCount=clamp(int(F(34).x),0,8);
        vec4 lights[8];
        for(int lightIndex=0;lightIndex<lightCount;lightIndex++) {
            int b=35+lightIndex*4; vec4 pos=F(b),color=F(b+1),direction=F(b+2),spot=F(b+3);
            vec3 delta=pos.xyz-p; float distance=length(delta),attenuation=1; vec3 l=-direction.xyz;
            if(direction.w<1.5) {
                l=delta/max(distance,0.0001); attenuation=1.0/max(distance*distance,0.0001);
                if(pos.w>0) { float win=Sat(1.0-pow(distance/pos.w,4.0)); attenuation*=win*win; }
                if(direction.w>0.5) attenuation*=smoothstep(spot.y,max(spot.x,spot.y+1e-6),dot(-l,direction.xyz));
            }
            else attenuation*=CloudShadow(p,l);
            lights[lightIndex]=vec4(color.xyz*color.w*attenuation*SliceVisibility(ray,depth,z0,span,lightIndex),dot(viewRay,l));
        }
        vec3 ambient=F(14).xyz,illumination=ambient;
        for(int phaseIndex=0;phaseIndex<lightCount;phaseIndex++) illumination+=lights[phaseIndex].xyz*FogPhase(lights[phaseIndex].w,F(9).w);
        scattering=F(12).xyz*sigma*illumination;
        for(int i=0;i<min(int(F(13).w),32);i++) {
            int base=192+i*8; vec4 shape=F(base+5),medium=F(base+4),noiseRow=F(base+6);
            vec3 lo=Transform(vec4(F(8).xyz,1),base).xyz,ld=Transform(vec4(ray,0),base).xyz;
            float t0,t1;
            if(shape.x>0.5) {
                float a=dot(ld,ld),b=dot(lo,ld),disc=b*b-a*(dot(lo,lo)-1.0);
                if(a<=1e-12 || disc<=0.0) continue;
                float s=sqrt(disc); t0=(-b-s)/a; t1=(-b+s)/a;
            }
            else {
                vec3 inv=vec3(1.0)/ld,ta=(-1.0-lo)*inv,tb=(1.0-lo)*inv;
                vec3 tn=min(ta,tb),tf=max(ta,tb);
                t0=max(max(tn.x,tn.y),tn.z); t1=min(min(tf.x,tf.y),tf.z);
            }
            t0=max(t0,z0); t1=min(t1,z0+span);
            if(!(t1>t0)) continue;
            float coverage=(t1-t0)/span,ts=0.5*(t0+t1);
            vec3 pl=lo+ld*ts,pw=F(8).xyz+ray*ts;
            float r=shape.x>0.5?length(pl):max(abs(pl.x),max(abs(pl.y),abs(pl.z)));
            float edge=Sat((1.0-r)/max(shape.z,1e-6)),weight=(r<1?edge*edge*(3.0-2.0*edge):0.0)*coverage;
            if(shape.y>0.5) exclude*=1.0-weight;
            else {
                float d=medium.w*weight*Noise(pw-F(13).xyz,noiseRow.x,shape.w); sigma+=d;
                // This patch's own g, not the scene's - the whole reason it is carried per volume. A volume that never
                // authored one holds the scene value, so packing decides nothing and the shader reads one row.
                vec3 light=ambient;
                for(int patchIndex=0;patchIndex<lightCount;patchIndex++) light+=lights[patchIndex].xyz*FogPhase(lights[patchIndex].w,noiseRow.y);
                scattering+=medium.xyz*d*light;
                emission+=F(base+7).xyz*d;
            }
        }
        float fade=1.0-smoothstep(F(8).w*0.9,F(8).w,depth);
        sigma*=exclude*fade; scattering*=exclude*fade; emission*=exclude*fade;
        imageStore(outputVolume,ivec3(id),min(vec4(scattering+emission,sigma),vec4(65000)));
    }
}
""";

    const string FilterGlsl = CommonGlsl + """
layout(binding=2) uniform sampler3D currentVolume;
layout(binding=3) uniform sampler3D historyVolume;
layout(binding=4,rgba16f) uniform writeonly image3D outputVolume;
layout(local_size_x=4,local_size_y=4,local_size_z=4) in;
void main() {
    uvec3 id=gl_GlobalInvocationID;
    if(all(lessThan(vec3(id),F(10).xyz))) {
        vec4 current=texelFetch(currentVolume,ivec3(id),0),result=current;
        if(F(15).w>0.5 && F(10).w>0) {
            vec3 p=F(8).xyz+Ray((vec2(id.xy)+0.5)/F(10).xy)*Z(float(id.z)+0.5);
            vec4 clip=Transform(vec4(p,1),4); float depth=dot(p-F(15).xyz,F(16).xyz);
            vec2 uv=clip.xy/max(clip.w,1e-6)*vec2(0.5,-0.5)+0.5;
            if(clip.w>0 && all(greaterThanEqual(uv,vec2(0))) && all(lessThanEqual(uv,vec2(1))) && depth>=0 && depth<=F(8).w) {
                float z=log(1.0+2.0*depth)/log(1.0+2.0*F(8).w);
                vec4 history=textureLod(historyVolume,vec3(uv,z),0),lo=current,hi=current;
                for(int axis=0;axis<3;axis++) for(int sign=-1;sign<=1;sign+=2) {
                    ivec3 c=ivec3(id); c[axis]+=sign; c=clamp(c,ivec3(0),ivec3(F(10).xyz)-1);
                    vec4 value=texelFetch(currentVolume,c,0); lo=min(lo,value); hi=max(hi,value);
                }
                float densityReject=Sat(1.0-abs(history.w-current.w)/max(current.w,0.0001));
                float lightReject=Sat(1.0-length(history.xyz-current.xyz)/max(length(current.xyz),0.0001));
                result=mix(current,clamp(history,lo,hi),F(10).w*densityReject*lightReject);
            }
        }
        imageStore(outputVolume,ivec3(id),result);
    }
}
""";

    const string IntegrationGlsl = CommonGlsl + """
layout(binding=2) uniform sampler3D localVolume;
layout(binding=3,rgba16f) uniform writeonly image3D outputVolume;
layout(local_size_x=8,local_size_y=8,local_size_z=1) in;
void main() {
    uvec3 id=gl_GlobalInvocationID;
    if(all(lessThan(vec2(id.xy),F(10).xy))) {
        float rayLength=length(Ray((vec2(id.xy)+0.5)/F(10).xy)); vec4 acc=vec4(0,0,0,1);
        imageStore(outputVolume,ivec3(id.xy,0),acc);
        for(int z=0;z<int(F(10).z);z++) {
            vec4 local=texelFetch(localVolume,ivec3(id.xy,z),0);
            acc=Segment(acc,local,(Z(float(z)+1.0)-Z(float(z)))*rayLength);
            imageStore(outputVolume,ivec3(id.xy,z+1),acc);
        }
    }
}
""";
}
