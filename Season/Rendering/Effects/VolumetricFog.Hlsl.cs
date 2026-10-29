// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

public sealed partial class VolumetricFogEffect
{
    /// <summary>由场景后端绑定 fogFrame（ByteAddressBuffer）、fogIntegrated/fogLocal（Texture3D）；调用方按 Pass 门控。</summary>
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
    // 单出口保证被材质的动态分支调用时 fxc 不产生 X4000。
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
    return 1.0-F(116).x*saturate(1.0-exp(-tau));
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
    return result;
}
[numthreads(4,4,4)] void CSMain(uint3 id : SV_DispatchThreadID) {
    if(all(float3(id)<F(10).xyz)) {
        float3 ray=Ray((float2(id.xy)+0.5)/F(10).xy); float depth=Z(float(id.z)+0.5);
        float3 p=F(8).xyz+ray*depth, windP=p-F(13).xyz;
        float4 global=F(11); float sigma=global.x*exp(-global.z*max(p.y-global.y,0.0))*Noise(windP,F(12).w,global.w);
        float3 scattering=F(12).xyz*sigma; float exclude=1;
        for(int i=0;i<min(int(F(13).w),32);i++) {
            uint base=128+i*8; float3 local=Transform(float4(p,1),base).xyz;
            float4 shape=F(base+5), medium=F(base+4);
            float r=shape.x>0.5?length(local):max(abs(local.x),max(abs(local.y),abs(local.z)));
            float edge=saturate((1.0-r)/max(shape.z,1e-6)); float weight=r<1?edge*edge*(3.0-2.0*edge):0;
            if(shape.y>0.5) exclude*=1.0-weight;
            else { float d=medium.w*weight*Noise(windP,F(base+6).x,shape.w); sigma+=d; scattering+=medium.xyz*d; }
        }
        float fade=1.0-smoothstep(F(8).w*0.9,F(8).w,depth);
        sigma*=exclude*fade; scattering*=exclude*fade;
        float3 illumination=F(14).xyz, viewRay=normalize(ray); float g=F(9).w;
        for(int lightIndex=0;lightIndex<clamp(int(F(34).x),0,8);lightIndex++) {
            uint b=35+lightIndex*4; float4 pos=F(b), color=F(b+1), direction=F(b+2), spot=F(b+3);
            float3 delta=pos.xyz-p; float distance=length(delta), attenuation=1; float3 l=-direction.xyz;
            if(direction.w<1.5) {
                l=delta/max(distance,0.0001); attenuation=1.0/max(distance*distance,0.0001);
                if(pos.w>0) { float win=saturate(1.0-pow(distance/pos.w,4.0)); attenuation*=win*win; }
                if(direction.w>0.5) attenuation*=smoothstep(spot.y,max(spot.x,spot.y+1e-6),dot(-l,direction.xyz));
            }
            else attenuation*=CloudShadow(p,l);
            float phase=(1.0-g*g)/(12.5663706144*pow(max(1.0+g*g-2.0*g*dot(viewRay,l),1e-4),1.5));
            illumination+=color.xyz*color.w*attenuation*phase*Visibility(p,depth,lightIndex);
        }
        outputVolume[id]=min(float4(scattering*illumination,sigma),65000.0);
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
