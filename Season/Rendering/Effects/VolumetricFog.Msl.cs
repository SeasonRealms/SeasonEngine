// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

public sealed partial class VolumetricFogEffect
{
    /// <summary>场景入口实例化 FogSampler，传入独立帧缓冲与两个只读体积；不改变表面光照参数。</summary>
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

    // 每个入口各自实例化只读帧上下文；纹理参数不存入共享 UBO。
    const string CommonMsl = """
#include <metal_stdlib>
using namespace metal;
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
        return 1.0-F(116).x*saturate(1.0-exp(-tau));
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
        return result;
    }
    float4 Inject(uint3 id) const {
        float3 ray=Ray((float2(id.xy)+0.5)/F(10).xy); float depth=Z(float(id.z)+0.5);
        float3 p=F(8).xyz+ray*depth,windP=p-F(13).xyz; float4 global=F(11);
        float sigma=global.x*exp(-global.z*max(p.y-global.y,0.0f))*Noise(windP,F(12).w,global.w);
        float3 scattering=F(12).xyz*sigma; float exclude=1;
        for(int i=0;i<min(int(F(13).w),32);i++) {
            uint base=128+i*8; float3 local=Transform(float4(p,1),base).xyz; float4 shape=F(base+5),medium=F(base+4);
            float r=shape.x>0.5?length(local):max(abs(local.x),max(abs(local.y),abs(local.z)));
            float edge=saturate((1.0-r)/max(shape.z,1e-6f)),weight=r<1?edge*edge*(3.0-2.0*edge):0;
            if(shape.y>0.5) exclude*=1.0-weight;
            else { float d=medium.w*weight*Noise(windP,F(base+6).x,shape.w); sigma+=d; scattering+=medium.xyz*d; }
        }
        float fade=1.0-smoothstep(F(8).w*0.9,F(8).w,depth); sigma*=exclude*fade; scattering*=exclude*fade;
        float3 illumination=F(14).xyz,viewRay=normalize(ray); float g=F(9).w;
        for(int i=0;i<clamp(int(F(34).x),0,8);i++) {
            uint b=35+i*4; float4 pos=F(b),color=F(b+1),direction=F(b+2),spot=F(b+3);
            float3 delta=pos.xyz-p; float distance=length(delta),attenuation=1; float3 l=-direction.xyz;
            if(direction.w<1.5) {
                l=delta/max(distance,0.0001f); attenuation=1.0/max(distance*distance,0.0001f);
                if(pos.w>0) { float win=saturate(1.0-pow(distance/pos.w,4.0f)); attenuation*=win*win; }
                if(direction.w>0.5) attenuation*=smoothstep(spot.y,max(spot.x,spot.y+1e-6f),dot(-l,direction.xyz));
            }
            else attenuation*=CloudShadow(p,l);
            float phase=(1.0-g*g)/(12.5663706144*pow(max(1.0+g*g-2.0*g*dot(viewRay,l),1e-4f),1.5f));
            illumination+=color.xyz*color.w*attenuation*phase*Visibility(p,depth,i);
        }
        return min(float4(scattering*illumination,sigma),65000.0f);
    }
};
kernel void CSMain(constant float4& params [[buffer(0)]],const device float4* data [[buffer(1)]],
    texture3d<float,access::sample> noiseVolume [[texture(0)]],depth2d<float,access::read> shadowAtlas [[texture(1)]],
    texture2d<float,access::sample> cloudNoise [[texture(2)]],texture3d<float,access::write> outputVolume [[texture(3)]],uint3 id [[thread_position_in_grid]]) {
    FogFrame f{data,noiseVolume,shadowAtlas,cloudNoise};
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
