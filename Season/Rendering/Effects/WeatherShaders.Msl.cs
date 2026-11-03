// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

public static partial class WeatherShaders
{
    public const string CommonMsl = """
float WSat(float v) { return clamp(v,0.0,1.0); }
int WeatherCount() { return clamp(int(WeatherH(0).x),0,8); }
float2 WeatherRotate(float2 p,float2 cs) { return float2(dot(p,cs),dot(p,float2(-cs.y,cs.x))); }
float WeatherFootprint(float3 p,int i,bool cloud) {
    float4 a=WeatherR(i,0),b=WeatherR(i,1),r=WeatherR(i,4);
    float2 inv=cloud?a.zw:r.xy; float feather=cloud?b.z:r.z;
    float t=WSat((1.0-length(WeatherRotate(p.xz-a.xy,b.xy)*inv))/max(feather,0.0001));
    float result=t*t*(3.0-2.0*t);
    float4 h=WeatherR(i,5);
    if(!cloud && (p.y<h.x || p.y>h.y)) result=0.0;
    return result;
}
float WeatherContribution(float3 p,int i,bool cloud) {
    float remaining=1.0;
    for(int j=0;j<8;j++) { if(j<i) remaining*=1.0-WeatherFootprint(p,j,cloud); }
    return remaining*WeatherFootprint(p,i,cloud);
}
""";

    public const string CloudMsl = """
float WeatherCloudDensity(float3 p,int i,int layer) {
    float4 shape=WeatherR(i,2),motion=WeatherR(i,3);
    float2 seed=float2(fmod(motion.z,251.0),fmod(floor(motion.z/251.0),241.0));
    float2 uv=(p.xz-motion.xy+seed+float2(67.0,97.0)*float(layer))*shape.z;
    float4 n=WeatherNoise(uv);
    float coverage=0.25+0.75*WeatherR(i,1).w;
    float d=WSat((n.x*mix(1.0,n.w,0.7)-(1.0-coverage))/max(coverage,0.001));
    return d*WSat(1.0-0.175*(n.y+n.z));
}
// Optical depth depends only on density and the view inclination angle. Density is passed in
// separately so that the distance-LOD-smoothed density is shared with brightness, avoiding the
// half-measure where brightness is already flattened but opacity still retains its moire.
float WeatherCloudTau(float3 p,float density,float vertical,int i,int layer) {
    float4 shape=WeatherR(i,2);
    return density*WeatherContribution(p,i,true)*WeatherR(i,1).w
        *shape.y*shape.w/max(abs(vertical),0.05)/max(WeatherH(0).z,1.0);
}
// A pixel already spans tens of meters several kilometers out, and the 512m-period cloud noise is
// necessarily undersampled at that scale, so point sampling reveals a repeating grid. Averaging four
// offset samples by the pixel's footprint on the cloud plane amounts to a manual mip: the large-scale
// cloud silhouettes survive while sub-pixel detail is flattened. The criterion must be the footprint,
// not pure view distance -- under grazing angles the same pixel is stretched 1/|dir.y| along the view
// direction, so switching on view distance alone leaves a broken bright band on the horizon. Nearby the
// four samples coincide and it degenerates to a single sample.
float WeatherCloudDensityLod(float3 p,float3 dir,float travel,int i,int layer) {
    float d=WeatherCloudDensity(p,i,layer);
    if(travel*rsqrt(max(abs(dir.y),0.02))>900.0) {
        float3 tangent=float3(dir.z,0.0,-dir.x);
        float tangentLength=length(tangent);
        tangent=tangentLength>0.000001?tangent/tangentLength:float3(1.0,0.0,0.0);
        float3 bitangent=cross(tangent,dir);
        float spread=travel*0.0025/max(abs(dir.y),0.02);
        d=0.25*(WeatherCloudDensity(p+tangent*spread,i,layer)+WeatherCloudDensity(p-tangent*spread,i,layer)
            +WeatherCloudDensity(p+bitangent*spread,i,layer)+WeatherCloudDensity(p-bitangent*spread,i,layer));
    }
    return d;
}
float WeatherCloudShadow(float3 p,float3 toLight) {
    float visibility=1.0;
    if(WeatherH(0).y>0.5 && toLight.y>0.00001) {
        for(int i=0;i<WeatherCount();i++) {
            float4 shape=WeatherR(i,2); float tau=0.0;
            for(int layer=0;layer<clamp(int(WeatherH(0).z),0,2);layer++) {
                float h=shape.x+float(layer)*shape.y;
                if(h>p.y) {
                    float3 hit=p+toLight*((h-p.y)/toLight.y);
                    tau+=WeatherCloudTau(hit,WeatherCloudDensity(hit,i,layer),toLight.y,i,layer);
                }
            }
            visibility*=1.0-WeatherR(i,3).w*(1.0-exp(-tau));
        }
    }
    return visibility;
}
float3 WeatherCloudComposite(float3 sky,float3 origin,float3 dir,float3 sunDirection) {
    float3 acc=float3(0.0,0.0,0.0); float trans=1.0;
    float hits[16]; int keys[16]; int count=0;
    for(int z=0;z<16;z++) { hits[z]=0.0; keys[z]=0; }
    if(WeatherH(0).y>0.5 && abs(dir.y)>0.00001) {
        for(int i=0;i<WeatherCount();i++) {
            float4 shape=WeatherR(i,2);
            for(int layer=0;layer<clamp(int(WeatherH(0).z),0,2);layer++) {
                float t=(shape.x+float(layer)*shape.y-origin.y)/dir.y;
                if(t>0.0 && t<WeatherH(1).z && WeatherR(i,1).w>0.0) {
                    int at=count;
                    for(int j=14;j>=0;j--) {
                        if(j<count && hits[j]>t) {
                            hits[j+1]=hits[j]; keys[j+1]=keys[j]; at=j;
                        }
                    }
                    hits[at]=t; keys[at]=i*2+layer; count++;
                }
            }
        }
        float g=0.6, dn=max(1.0+g*g-2.0*g*dot(dir,sunDirection),0.0001);
        float silver=0.25*pow((1.0-g)*(1.0-g)/dn,1.5);
        for(int k=0;k<count;k++) {
            int regionIndex=keys[k]>>1,layerIndex=keys[k]&1; float3 p=origin+dir*hits[k];
            // Brightness and optical depth share the same density: if alpha kept point-sampling the true
            // density, the undersampling ring near the horizon would show up as a flickering grid -- it
            // looks like cloud structure but is entirely a sampling artifact.
            float d=WeatherCloudDensityLod(p,dir,hits[k],regionIndex,layerIndex);
            float alpha=1.0-exp(-WeatherCloudTau(p,d,dir.y,regionIndex,layerIndex));
            // The cloud slab is bounded only at the far clip plane: the last stretch of distance fades
            // out coverage so the cloud layer resolves into the sky color instead of a hard arc.
            alpha*=1.0-WSat((hits[k]-WeatherH(1).z*0.72)/max(WeatherH(1).z*0.28,0.001));
            float4 light=WeatherR(regionIndex,6);
            float3 radiance=light.xyz*mix(light.w,1.0,1.0-d)*(1.0+silver);
            // Atmospheric perspective: at the 6km scale the cloud slab converges to the same pixel's
            // sky color, so clouds near the horizon fade naturally instead of leaving a hard cut where
            // the coverage fades out.
            radiance=mix(sky,radiance,exp(-hits[k]/6000.0));
            // 2-7 Lightning: intra-cloud glow. flash stores the dominant strike's world position plus
            // the return-stroke envelope brightness; 1/(1+d^2/L^2) spreads the point strike into a small
            // illuminated cloud volume. L=250m is on the order of the cloud slab's horizontal scale, and
            // farther out it degenerates into uniform brightening rather than a "flash inside the cloud".
            float3 flashDelta=p-WeatherFlash().xyz;
            radiance+=WeatherFlash().w/(1.0+dot(flashDelta,flashDelta)/62500.0)*float3(0.55,0.62,0.85)*12.0;
            acc+=trans*alpha*radiance; trans*=1.0-alpha;
        }
    }
    return sky*trans+acc;
}
""";

    public const string RainMsl = """
float2 WeatherRainInterval(float3 origin,float3 dir,float distance,int i) {
    float4 region=WeatherR(i,0),rot=WeatherR(i,1),r=WeatherR(i,4),h=WeatherR(i,5);
    float2 o=WeatherRotate(origin.xz-region.xy,rot.xy)*r.xy;
    float2 d=WeatherRotate(dir.xz,rot.xy)*r.xy;
    float a=dot(d,d),b=dot(o,d),q=dot(o,o)-1.0;
    float lo=0.0,hi=distance;
    if(a<1e-20) { if(q>0.0) hi=0.0; }
    else {
        float disc=b*b-a*q;
        if(disc<=0.0) hi=0.0;
        else { float root=sqrt(disc); lo=max(lo,(-b-root)/a); hi=min(hi,(-b+root)/a); }
    }
    if(abs(dir.y)<1e-10) { if(origin.y<h.x || origin.y>h.y) hi=0.0; }
    else { float y0=(h.x-origin.y)/dir.y,y1=(h.y-origin.y)/dir.y;
        lo=max(lo,min(y0,y1)); hi=min(hi,max(y0,y1)); }
    return hi>lo?float2(lo,hi):float2(0.0,0.0);
}
float WeatherRainTexture(float3 p,int i) {
    float4 m=WeatherR(i,3),h=WeatherR(i,5);
    float2 q=(p.xz-m.xy)/32.0;
    float seed=fmod(m.z,127.0);
    float a=sin(6.2831853*(q.x+0.25*sin(q.y*3.14159265)+p.y/256.0+h.w/256.0)+seed);
    float b=sin(6.2831853*(q.y*0.5-q.x*0.25)+seed*0.37);
    return 0.55+0.25*a+0.2*b;
}
// The distant rain curtain's structure cannot rely on the 32m-period rain pattern: with a budget of
// 24 steps spanning over a kilometer the step length reaches 40-60m and samples land between adjacent
// peaks and troughs, leaving only a stable bright/dark band. Here the high-frequency pattern is faded
// toward its mean by step length and a 260m-period low-frequency cloud-scale swell takes over the far
// layer; both components have a mean of 0.55, so no brightness jump appears at the switch.
float WeatherRainBank(float3 p,int i) {
    float4 m=WeatherR(i,3);
    float2 q=(p.xz-m.xy)/260.0;
    float seed=fmod(m.z,127.0);
    float a=sin(6.2831853*(q.x*0.5+q.y*0.35)+seed*0.81);
    float b=sin(6.2831853*(q.y*0.8-q.x*0.4)+seed*0.19);
    return 0.55+0.12*a+0.08*b;
}
float WeatherRainDetail(float stepLength) {
    float t=saturate((64.0-stepLength)/48.0);
    return t*t*(3.0-2.0*t);
}
float4 WeatherRainMedium(float3 p,float distance,float stepLength) {
    float3 source=float3(0.0,0.0,0.0); float sigma=0.0,remaining=1.0;
    float fade=smoothstep(WeatherH(1).x,WeatherH(1).y,distance);
    float detail=WeatherRainDetail(stepLength);
    for(int i=0;i<WeatherCount();i++) {
        float weight=WeatherFootprint(p,i,false);
        float4 rain=WeatherR(i,4),h=WeatherR(i,5),light=WeatherR(i,7);
        float edge=min(20.0,max((h.y-h.x)*0.08,0.001));
        float vertical=smoothstep(0.0,edge,p.y-h.x)*smoothstep(0.0,edge,h.y-p.y);
        float pattern=mix(WeatherRainBank(p,i),WeatherRainTexture(p,i),detail);
        float density=remaining*weight*rain.w*light.w*vertical*fade*pattern;
        sigma+=density; source+=light.xyz*density; remaining*=1.0-weight;
    }
    return float4(source,sigma);
}
float4 WeatherRain(float3 origin,float3 dir,float distance) {
    float4 result=float4(0.0,0.0,0.0,1.0);
    float2 spans[8]; int count=0;
    for(int z=0;z<8;z++) spans[z]=float2(0.0,0.0);
    if(WeatherH(1).w>0.5 && distance>WeatherH(1).x) {
        for(int i=0;i<WeatherCount();i++) {
            float2 hit=WeatherRainInterval(origin,dir,min(distance,WeatherH(1).z),i);
            if(WeatherR(i,4).w>0.0 && hit.y>hit.x) {
                int at=count;
                for(int j=6;j>=0;j--) {
                    if(j<count && spans[j].x>hit.x) {
                        spans[j+1]=spans[j]; at=j;
                    }
                }
                spans[at]=hit; count++;
            }
        }
        // Merge overlapping spans and sample along the full field to avoid double integration and
        // missing narrow regions.
        int merged=0;
        for(int mergeIndex=0;mergeIndex<8;mergeIndex++) {
            if(mergeIndex<count) {
                if(merged>0 && spans[mergeIndex].x<=spans[max(merged-1,0)].y)
                    spans[max(merged-1,0)].y=max(spans[max(merged-1,0)].y,spans[mergeIndex].y);
                else { spans[merged]=spans[mergeIndex]; merged++; }
            }
        }
        float lengthSum=0.0;
        for(int sumIndex=0;sumIndex<merged;sumIndex++) lengthSum+=spans[sumIndex].y-spans[sumIndex].x;
        int budget=clamp(int(WeatherH(0).w),8,24),left=budget;
        for(int segmentIndex=0;segmentIndex<merged;segmentIndex++) {
            float len=spans[segmentIndex].y-spans[segmentIndex].x;
            int steps=segmentIndex==merged-1?left:1+int(floor(float(budget-merged)*len/max(lengthSum,0.001)));
            left-=steps; float ds=len/float(steps);
            for(int k=0;k<steps;k++) {
                // Fixed lattice points beat against the rain pattern period at distance; a per-pixel,
                // per-segment deterministic jitter turns the beating into fine noise. The integral stays
                // unbiased and no extra ABI or random table is needed.
                float jitter=fract(sin(dot(dir,float3(12.9898,78.233,37.719))+float(k)*13.77+spans[segmentIndex].x*0.001)*43758.5453);
                float t=spans[segmentIndex].x+(float(k)+jitter)*ds;
                float4 local=WeatherRainMedium(origin+dir*t,t,ds);
                float transmission=exp(-local.w*ds);
                float integral=local.w>1e-8?(1.0-transmission)/local.w:ds;
                result.xyz+=result.w*local.xyz*integral; result.w*=transmission;
            }
        }
    }
    return result;
}
""";

    // constant scene lighting and the device fog frame share the same math, differing only in the
    // region pointer address space.
    public const string ContextMsl = """
template<typename RegionBuffer> struct WeatherCloudContext {
    float4 h0,h1;
    RegionBuffer regions;
    texture2d<float> cloudNoise;
    // 2-7 Lightning: dominant strike (xyz = in-cloud world position, w = return-stroke envelope
    // brightness 0-1). It is a struct member rather than a global because the same math must serve both
    // the constant (main lighting) and device (fog frame) address spaces.
    float4 flash;
    float4 WeatherH(int i) { return i==0?h0:h1; }
    float4 WeatherR(int i,int row) { return regions[i*8+row]; }
    float4 WeatherFlash() { return flash; }
    float4 WeatherNoise(float2 uv) {
        constexpr sampler wrap(coord::normalized,address::repeat,filter::linear);
        return cloudNoise.sample(wrap,uv,level(0));
    }
""" + CommonMsl + CloudMsl + """
};
template<typename RegionBuffer> struct WeatherRainContext {
    float4 h0,h1;
    RegionBuffer regions;
    float4 WeatherH(int i) { return i==0?h0:h1; }
    float4 WeatherR(int i,int row) { return regions[i*8+row]; }
""" + CommonMsl + RainMsl + """
};
""";

    public const string CompositeMsl = """
#include <metal_stdlib>
using namespace metal;
""" + ContextMsl + """
fragment float4 PSMain(float4 position [[position]],const device float4* data [[buffer(0)]],
    constant float4& compositeParams [[buffer(1)]],texture2d<float> color [[texture(2)]],
    depth2d<float> depthTexture [[texture(3)]],texture2d<float> aoTexture [[texture(4)]]) {
    constexpr sampler linearClamp(coord::normalized,address::clamp_to_edge,filter::linear);
    uint2 pixel=uint2(position.xy);
    float2 uv=position.xy/float2(color.get_width(),color.get_height());
    float depth=depthTexture.read(pixel);
    float4 ndc=float4(uv*float2(2,-2)+float2(-1,1),depth>=1.0?0.5:depth,1.0);
    float4 p=ndc.x*data[0]+ndc.y*data[1]+ndc.z*data[2]+data[3];
    float3 ray=p.xyz/p.w-data[4].xyz; float distance=length(ray);
    ray/=max(distance,1e-8f); if(depth>=1.0) distance=data[95].z;
    WeatherRainContext<const device float4*> weather{data[94],data[95],data+96};
    float4 medium=weather.WeatherRain(data[4].xyz,ray,distance),c=color.read(pixel);
    float ao=1.0;
    if(compositeParams.y>0.5) ao=mix(1.0,aoTexture.sample(linearClamp,uv,level(0)).r,compositeParams.x);
    return float4(c.xyz*ao*medium.w+medium.xyz,c.w);
}
""";
}
