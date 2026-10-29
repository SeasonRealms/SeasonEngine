// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

public sealed partial class VolumetricFogEffect
{
    /// <summary>后端声明 vec4 fogFrame[] 与 sampler3D fogIntegrated/fogLocal；每列先补积分再做 XY 插值。</summary>
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
    return 1.0-F(116).x*Sat(1.0-exp(-tau));
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
    return result;
}
layout(local_size_x=4,local_size_y=4,local_size_z=4) in;
void main() {
    uvec3 id=gl_GlobalInvocationID;
    if(all(lessThan(vec3(id),F(10).xyz))) {
        vec3 ray=Ray((vec2(id.xy)+0.5)/F(10).xy); float depth=Z(float(id.z)+0.5);
        vec3 p=F(8).xyz+ray*depth,windP=p-F(13).xyz; vec4 global=F(11);
        float sigma=global.x*exp(-global.z*max(p.y-global.y,0.0))*Noise(windP,F(12).w,global.w);
        vec3 scattering=F(12).xyz*sigma; float exclude=1;
        for(int i=0;i<min(int(F(13).w),32);i++) {
            int base=128+i*8; vec3 local=Transform(vec4(p,1),base).xyz; vec4 shape=F(base+5),medium=F(base+4);
            float r=shape.x>0.5?length(local):max(abs(local.x),max(abs(local.y),abs(local.z)));
            float edge=Sat((1.0-r)/max(shape.z,1e-6)),weight=r<1?edge*edge*(3.0-2.0*edge):0;
            if(shape.y>0.5) exclude*=1.0-weight;
            else { float d=medium.w*weight*Noise(windP,F(base+6).x,shape.w); sigma+=d; scattering+=medium.xyz*d; }
        }
        float fade=1.0-smoothstep(F(8).w*0.9,F(8).w,depth); sigma*=exclude*fade; scattering*=exclude*fade;
        vec3 illumination=F(14).xyz,viewRay=normalize(ray); float g=F(9).w;
        for(int i=0;i<clamp(int(F(34).x),0,8);i++) {
            int b=35+i*4; vec4 pos=F(b),color=F(b+1),direction=F(b+2),spot=F(b+3);
            vec3 delta=pos.xyz-p; float distance=length(delta),attenuation=1; vec3 l=-direction.xyz;
            if(direction.w<1.5) {
                l=delta/max(distance,0.0001); attenuation=1.0/max(distance*distance,0.0001);
                if(pos.w>0) { float win=Sat(1.0-pow(distance/pos.w,4.0)); attenuation*=win*win; }
                if(direction.w>0.5) attenuation*=smoothstep(spot.y,max(spot.x,spot.y+1e-6),dot(-l,direction.xyz));
            }
            else attenuation*=CloudShadow(p,l);
            float phase=(1.0-g*g)/(12.5663706144*pow(max(1.0+g*g-2.0*g*dot(viewRay,l),1e-4),1.5));
            illumination+=color.xyz*color.w*attenuation*phase*Visibility(p,depth,i);
        }
        imageStore(outputVolume,ivec3(id),min(vec4(scattering*illumination,sigma),vec4(65000)));
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
