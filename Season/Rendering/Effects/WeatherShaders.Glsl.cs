// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

public static partial class WeatherShaders
{
    public const string CommonGlsl = """
float WSat(float v) { return clamp(v,0.0,1.0); }
int WeatherCount() { return clamp(int(WeatherH(0).x),0,8); }
vec2 WeatherRotate(vec2 p,vec2 cs) { return vec2(dot(p,cs),dot(p,vec2(-cs.y,cs.x))); }
float WeatherFootprint(vec3 p,int i,bool cloud) {
    vec4 a=WeatherR(i,0),b=WeatherR(i,1),r=WeatherR(i,4);
    vec2 inv=cloud?a.zw:r.xy; float feather=cloud?b.z:r.z;
    float t=WSat((1.0-length(WeatherRotate(p.xz-a.xy,b.xy)*inv))/max(feather,0.0001));
    float result=t*t*(3.0-2.0*t);
    vec4 h=WeatherR(i,5);
    if(!cloud && (p.y<h.x || p.y>h.y)) result=0.0;
    return result;
}
float WeatherContribution(vec3 p,int i,bool cloud) {
    float remaining=1.0;
    for(int j=0;j<8;j++) { if(j<i) remaining*=1.0-WeatherFootprint(p,j,cloud); }
    return remaining*WeatherFootprint(p,i,cloud);
}
""";

    public const string CloudGlsl = """
float WeatherCloudDensity(vec3 p,int i,int layer) {
    vec4 shape=WeatherR(i,2),motion=WeatherR(i,3);
    vec2 seed=vec2(mod(motion.z,251.0),mod(floor(motion.z/251.0),241.0));
    vec2 uv=(p.xz-motion.xy+seed+vec2(67.0,97.0)*float(layer))*shape.z;
    vec4 n=WeatherNoise(uv);
    float coverage=0.25+0.75*WeatherR(i,1).w;
    float d=WSat((n.x*mix(1.0,n.w,0.7)-(1.0-coverage))/max(coverage,0.001));
    return d*WSat(1.0-0.175*(n.y+n.z));
}
// 光学厚度只取决于密度和视线倾角。密度单独传进来，远距 LOD 平滑过的密度才能和亮度用同一份，
// 不会出现「亮度已经抹平、不透明度还留着摩尔纹」的半截处理。
float WeatherCloudTau(vec3 p,float density,float vertical,int i,int layer) {
    vec4 shape=WeatherR(i,2);
    return density*WeatherContribution(p,i,true)*WeatherR(i,1).w
        *shape.y*shape.w/max(abs(vertical),0.05)/max(WeatherH(0).z,1.0);
}
// 一个像素在数公里外已经横跨数十米，512m 周期的云噪声在这个尺度上必然欠采样，点采会显出重复网格。
// 按「像素在云平面上的足迹」取四个错开的样本求均值，相当于手工做一次 mip：云团的大尺度轮廓留住，
// 低于一个像素的细纹被抹平。判据必须是足迹而不是纯视距——掠射时同一像素沿视线方向被拉长
// 1/|dir.y|，只按视距切换会在地平线上留下一条断续的亮带。近处四个样本重合，退化为单次采样。
float WeatherCloudDensityLod(vec3 p,vec3 dir,float travel,int i,int layer) {
    float d=WeatherCloudDensity(p,i,layer);
    if(travel*inversesqrt(max(abs(dir.y),0.02))>900.0) {
        vec3 tangent=vec3(dir.z,0.0,-dir.x);
        float tangentLength=length(tangent);
        tangent=tangentLength>0.000001?tangent/tangentLength:vec3(1.0,0.0,0.0);
        vec3 bitangent=cross(tangent,dir);
        float spread=travel*0.0025/max(abs(dir.y),0.02);
        d=0.25*(WeatherCloudDensity(p+tangent*spread,i,layer)+WeatherCloudDensity(p-tangent*spread,i,layer)
            +WeatherCloudDensity(p+bitangent*spread,i,layer)+WeatherCloudDensity(p-bitangent*spread,i,layer));
    }
    return d;
}
float WeatherCloudShadow(vec3 p,vec3 toLight) {
    float visibility=1.0;
    if(WeatherH(0).y>0.5 && toLight.y>0.00001) {
        for(int i=0;i<WeatherCount();i++) {
            vec4 shape=WeatherR(i,2); float tau=0.0;
            for(int layer=0;layer<clamp(int(WeatherH(0).z),0,2);layer++) {
                float h=shape.x+float(layer)*shape.y;
                if(h>p.y) {
                    vec3 hit=p+toLight*((h-p.y)/toLight.y);
                    tau+=WeatherCloudTau(hit,WeatherCloudDensity(hit,i,layer),toLight.y,i,layer);
                }
            }
            visibility*=1.0-WeatherR(i,3).w*(1.0-exp(-tau));
        }
    }
    return visibility;
}
vec3 WeatherCloudComposite(vec3 sky,vec3 origin,vec3 dir,vec3 sunDirection) {
    vec3 acc=vec3(0.0,0.0,0.0); float trans=1.0;
    float hits[16]; int keys[16]; int count=0;
    for(int z=0;z<16;z++) { hits[z]=0.0; keys[z]=0; }
    if(WeatherH(0).y>0.5 && abs(dir.y)>0.00001) {
        for(int i=0;i<WeatherCount();i++) {
            vec4 shape=WeatherR(i,2);
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
            int regionIndex=keys[k]>>1,layerIndex=keys[k]&1; vec3 p=origin+dir*hits[k];
            // 亮度与光学厚度共用同一份密度：alpha 若继续点采真实密度，地平线附近那一圈欠采样就会以
            // 闪烁栅格的形式留在画面上——看着像云的结构，其实全是采样伪影。
            float d=WeatherCloudDensityLod(p,dir,hits[k],regionIndex,layerIndex);
            float alpha=1.0-exp(-WeatherCloudTau(p,d,dir.y,regionIndex,layerIndex));
            // 云板只在远裁剪面处有边界：末段距离把覆盖率淡出，云层改以天空底色收场，而不是截出一道硬弧。
            alpha*=1.0-WSat((hits[k]-WeatherH(1).z*0.72)/max(WeatherH(1).z*0.28,0.001));
            vec4 light=WeatherR(regionIndex,6);
            vec3 radiance=light.xyz*mix(light.w,1.0,1.0-d)*(1.0+silver);
            // 大气透视：6km 尺度上云板收向同一像素的天空底色，地平线附近的云层才会自然消失，
            // 而不是在覆盖率淡出处留下一条切边。
            radiance=mix(sky,radiance,exp(-hits[k]/6000.0));
            acc+=trans*alpha*radiance; trans*=1.0-alpha;
        }
    }
    return sky*trans+acc;
}
""";

    public const string RainGlsl = """
vec2 WeatherRainInterval(vec3 origin,vec3 dir,float distance,int i) {
    vec4 region=WeatherR(i,0),rot=WeatherR(i,1),r=WeatherR(i,4),h=WeatherR(i,5);
    vec2 o=WeatherRotate(origin.xz-region.xy,rot.xy)*r.xy;
    vec2 d=WeatherRotate(dir.xz,rot.xy)*r.xy;
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
    return hi>lo?vec2(lo,hi):vec2(0.0,0.0);
}
float WeatherRainTexture(vec3 p,int i) {
    vec4 m=WeatherR(i,3),h=WeatherR(i,5);
    vec2 q=(p.xz-m.xy)/32.0;
    float seed=mod(m.z,127.0);
    float a=sin(6.2831853*(q.x+0.25*sin(q.y*3.14159265)+p.y/256.0+h.w/256.0)+seed);
    float b=sin(6.2831853*(q.y*0.5-q.x*0.25)+seed*0.37);
    return 0.55+0.25*a+0.2*b;
}
// 远景雨幕的层次不能交给 32m 周期雨纹：预算 24 步铺满上千米时步长已达 40~60m，采样必然落在
// 相邻峰谷之间，只剩一条稳定的亮暗带。这里按步长把高频纹路淡到均值，再用 260m 周期的低频
// 云团尺度起伏接管远层，两个分量的均值都是 0.55，切换处不会产生亮度跳变。
float WeatherRainBank(vec3 p,int i) {
    vec4 m=WeatherR(i,3);
    vec2 q=(p.xz-m.xy)/260.0;
    float seed=mod(m.z,127.0);
    float a=sin(6.2831853*(q.x*0.5+q.y*0.35)+seed*0.81);
    float b=sin(6.2831853*(q.y*0.8-q.x*0.4)+seed*0.19);
    return 0.55+0.12*a+0.08*b;
}
float WeatherRainDetail(float stepLength) {
    float t=WSat((64.0-stepLength)/48.0);
    return t*t*(3.0-2.0*t);
}
vec4 WeatherRainMedium(vec3 p,float distance,float stepLength) {
    vec3 source=vec3(0.0,0.0,0.0); float sigma=0.0,remaining=1.0;
    float fade=smoothstep(WeatherH(1).x,WeatherH(1).y,distance);
    float detail=WeatherRainDetail(stepLength);
    for(int i=0;i<WeatherCount();i++) {
        float weight=WeatherFootprint(p,i,false);
        vec4 rain=WeatherR(i,4),h=WeatherR(i,5),light=WeatherR(i,7);
        float edge=min(20.0,max((h.y-h.x)*0.08,0.001));
        float vertical=smoothstep(0.0,edge,p.y-h.x)*smoothstep(0.0,edge,h.y-p.y);
        float pattern=mix(WeatherRainBank(p,i),WeatherRainTexture(p,i),detail);
        float density=remaining*weight*rain.w*light.w*vertical*fade*pattern;
        sigma+=density; source+=light.xyz*density; remaining*=1.0-weight;
    }
    return vec4(source,sigma);
}
vec4 WeatherRain(vec3 origin,vec3 dir,float distance) {
    vec4 result=vec4(0.0,0.0,0.0,1.0);
    vec2 spans[8]; int count=0;
    for(int z=0;z<8;z++) spans[z]=vec2(0.0,0.0);
    if(WeatherH(1).w>0.5 && distance>WeatherH(1).x) {
        for(int i=0;i<WeatherCount();i++) {
            vec2 hit=WeatherRainInterval(origin,dir,min(distance,WeatherH(1).z),i);
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
        // 合并交叠段，沿完整场采样，避免重复积分及漏掉狭窄区域。
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
                // 固定格点在远距离会与雨纹周期拍频；逐像素逐段的确定性抖动把拍频换成细噪点，
                // 积分仍无偏，且不需要额外 ABI 或随机表。
                float jitter=fract(sin(dot(dir,vec3(12.9898,78.233,37.719))+float(k)*13.77+spans[segmentIndex].x*0.001)*43758.5453);
                float t=spans[segmentIndex].x+(float(k)+jitter)*ds;
                vec4 local=WeatherRainMedium(origin+dir*t,t,ds);
                float transmission=exp(-local.w*ds);
                float integral=local.w>1e-8?(1.0-transmission)/local.w:ds;
                result.xyz+=result.w*local.xyz*integral; result.w*=transmission;
            }
        }
    }
    return result;
}
""";

    public const string CompositeGlsl = """
#version 450
layout(std430,binding=0) readonly buffer WeatherFrame { vec4 weatherFrame[]; };
layout(binding=3) uniform sampler2D sceneColor;
layout(binding=4) uniform sampler2D sceneDepth;
layout(binding=5) uniform sampler2D sceneAo;
layout(push_constant) uniform Params { vec4 compositeParams; };
layout(location=0) out vec4 result;
vec4 WF(int i) { return weatherFrame[i]; }
vec4 WeatherH(int i) { return WF(94+i); }
vec4 WeatherR(int i,int row) { return WF(96+i*8+row); }
""" + CommonGlsl + RainGlsl + """
void main() {
    ivec2 pixel=ivec2(gl_FragCoord.xy);
    vec2 uv=gl_FragCoord.xy/vec2(textureSize(sceneColor,0));
    float depth=texelFetch(sceneDepth,pixel,0).r;
    vec4 ndc=vec4(uv*vec2(2,-2)+vec2(-1,1),depth>=1.0?0.5:depth,1.0);
    vec4 p=ndc.x*WF(0)+ndc.y*WF(1)+ndc.z*WF(2)+WF(3);
    vec3 ray=p.xyz/p.w-WF(4).xyz; float distance=length(ray);
    ray/=max(distance,1e-8); if(depth>=1.0) distance=WeatherH(1).z;
    vec4 medium=WeatherRain(WF(4).xyz,ray,distance),color=texelFetch(sceneColor,pixel,0);
    float ao=1.0;
    if(compositeParams.y>0.5) ao=mix(1.0,textureLod(sceneAo,uv,0.0).r,compositeParams.x);
    result=vec4(color.xyz*ao*medium.w+medium.xyz,color.w);
}
""";
}
