// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

public static partial class WeatherShaders
{
    public const string CommonWgsl = """
fn WeatherCount() -> i32 { return clamp(i32(WeatherH(0).x),0,8); }
fn WeatherRotate(p: vec2f,cs: vec2f) -> vec2f { return vec2f(dot(p,cs),dot(p,vec2f(-cs.y,cs.x))); }
fn WeatherFootprint(p: vec3f,i: i32,cloud: bool) -> f32 {
    let a=WeatherR(i,0); let b=WeatherR(i,1); let r=WeatherR(i,4);
    let inverse=select(r.xy,a.zw,cloud); let feather=select(r.z,b.z,cloud);
    let t=clamp((1.0-length(WeatherRotate(p.xz-a.xy,b.xy)*inverse))/max(feather,0.0001),0.0,1.0);
    var result=t*t*(3.0-2.0*t); let h=WeatherR(i,5);
    if(!cloud && (p.y<h.x || p.y>h.y)) { result=0.0; }
    return result;
}
fn WeatherContribution(p: vec3f,i: i32,cloud: bool) -> f32 {
    var remaining=1.0;
    for(var j=0;j<8;j++) { if(j<i) { remaining*=1.0-WeatherFootprint(p,j,cloud); } }
    return remaining*WeatherFootprint(p,i,cloud);
}
""";

    public const string CloudWgsl = """
fn WeatherCloudDensity(p: vec3f,i: i32,layer: i32) -> f32 {
    let shape=WeatherR(i,2); let motion=WeatherR(i,3);
    let seed=vec2f(motion.z%251.0,floor(motion.z/251.0)%241.0);
    let uv=(p.xz-motion.xy+seed+vec2f(67.0,97.0)*f32(layer))*shape.z;
    let n=WeatherNoise(uv); let coverage=0.25+0.75*WeatherR(i,1).w;
    let d=clamp((n.x*mix(1.0,n.w,0.7)-(1.0-coverage))/max(coverage,0.001),0.0,1.0);
    return d*clamp(1.0-0.175*(n.y+n.z),0.0,1.0);
}
// Optical depth depends only on density and the view inclination angle. Density is passed in
// separately so that the distance-LOD-smoothed density is shared with brightness, avoiding the
// half-measure where brightness is already flattened but opacity still retains its moire.
fn WeatherCloudTau(p: vec3f,density: f32,vertical: f32,i: i32,layer: i32) -> f32 {
    let shape=WeatherR(i,2);
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
fn WeatherCloudDensityLod(p: vec3f,dir: vec3f,travel: f32,i: i32,layer: i32) -> f32 {
    var d=WeatherCloudDensity(p,i,layer);
    if(travel*inverseSqrt(max(abs(dir.y),0.02))>900.0) {
        var tangent=vec3f(dir.z,0.0,-dir.x);
        let tangentLength=length(tangent);
        tangent=select(tangent/tangentLength,vec3f(1.0,0.0,0.0),tangentLength<=0.000001);
        let bitangent=cross(tangent,dir);
        let spread=travel*0.0025/max(abs(dir.y),0.02);
        d=0.25*(WeatherCloudDensity(p+tangent*spread,i,layer)+WeatherCloudDensity(p-tangent*spread,i,layer)
            +WeatherCloudDensity(p+bitangent*spread,i,layer)+WeatherCloudDensity(p-bitangent*spread,i,layer));
    }
    return d;
}
fn WeatherCloudShadow(p: vec3f,toLight: vec3f) -> f32 {
    var visibility=1.0;
    if(WeatherH(0).y>0.5 && toLight.y>0.00001) {
        for(var i=0;i<WeatherCount();i++) {
            let shape=WeatherR(i,2); var tau=0.0;
            for(var layer=0;layer<clamp(i32(WeatherH(0).z),0,2);layer++) {
                let h=shape.x+f32(layer)*shape.y;
                if(h>p.y) { let hit=p+toLight*((h-p.y)/toLight.y); tau+=WeatherCloudTau(hit,WeatherCloudDensity(hit,i,layer),toLight.y,i,layer); }
            }
            visibility*=1.0-WeatherR(i,3).w*(1.0-exp(-tau));
        }
    }
    return visibility;
}
fn WeatherCloudComposite(sky: vec3f,origin: vec3f,dir: vec3f,sunDirection: vec3f) -> vec3f {
    var acc=vec3f(0.0); var trans=1.0;
    var hits: array<f32,16>; var keys: array<i32,16>; var count=0;
    if(WeatherH(0).y>0.5 && abs(dir.y)>0.00001) {
        for(var i=0;i<WeatherCount();i++) {
            let shape=WeatherR(i,2);
            for(var layer=0;layer<clamp(i32(WeatherH(0).z),0,2);layer++) {
                let t=(shape.x+f32(layer)*shape.y-origin.y)/dir.y;
                if(t>0.0 && t<WeatherH(1).z && WeatherR(i,1).w>0.0) {
                    var at=count;
                    for(var j=14;j>=0;j--) {
                        if(j<count && hits[j]>t) { hits[j+1]=hits[j]; keys[j+1]=keys[j]; at=j; }
                    }
                    hits[at]=t; keys[at]=i*2+layer; count++;
                }
            }
        }
        let g=0.6; let dn=max(1.0+g*g-2.0*g*dot(dir,sunDirection),0.0001);
        let silver=0.25*pow((1.0-g)*(1.0-g)/dn,1.5);
        for(var k=0;k<count;k++) {
            let regionIndex=keys[k]>>1u; let layerIndex=keys[k]&1; let p=origin+dir*hits[k];
            // Brightness and optical depth share the same density: if alpha kept point-sampling the true
            // density, the undersampling ring near the horizon would show up as a flickering grid -- it
            // looks like cloud structure but is entirely a sampling artifact.
            let d=WeatherCloudDensityLod(p,dir,hits[k],regionIndex,layerIndex);
            let alpha=1.0-exp(-WeatherCloudTau(p,d,dir.y,regionIndex,layerIndex));
            // The cloud slab is bounded only at the far clip plane: the last stretch of distance fades
            // out coverage so the cloud layer resolves into the sky color instead of a hard arc.
            let faded=alpha*(1.0-clamp((hits[k]-WeatherH(1).z*0.72)/max(WeatherH(1).z*0.28,0.001),0.0,1.0));
            let light=WeatherR(regionIndex,6);
            let radiance0=light.xyz*mix(light.w,1.0,1.0-d)*(1.0+silver);
            // Atmospheric perspective: at the 6km scale the cloud slab converges to the same pixel's
            // sky color, so clouds near the horizon fade naturally instead of leaving a hard cut where
            // the coverage fades out.
            var radiance=mix(sky,radiance0,exp(-hits[k]/6000.0));
            // 2-7 Lightning: intra-cloud glow. WeatherFlash stores the dominant strike's world position
            // plus the return-stroke envelope brightness; 1/(1+d^2/L^2) spreads the point strike into a
            // small illuminated cloud volume. L=250m is on the order of the cloud slab's horizontal
            // scale, and farther out it degenerates into uniform brightening rather than a "flash inside
            // the cloud".
            let flashDelta=p-WeatherFlash().xyz;
            radiance+=WeatherFlash().w/(1.0+dot(flashDelta,flashDelta)/62500.0)*vec3f(0.55,0.62,0.85)*12.0;
            acc+=trans*faded*radiance; trans*=1.0-faded;
        }
    }
    return sky*trans+acc;
}
""";

    public const string RainWgsl = """
fn WeatherRainInterval(origin: vec3f,dir: vec3f,distance: f32,i: i32) -> vec2f {
    let region=WeatherR(i,0); let rot=WeatherR(i,1); let r=WeatherR(i,4); let h=WeatherR(i,5);
    let o=WeatherRotate(origin.xz-region.xy,rot.xy)*r.xy;
    let d=WeatherRotate(dir.xz,rot.xy)*r.xy;
    let a=dot(d,d); let b=dot(o,d); let q=dot(o,o)-1.0;
    var lo=0.0; var hi=distance;
    if(a<1e-20) { if(q>0.0) { hi=0.0; } }
    else {
        let disc=b*b-a*q;
        if(disc<=0.0) { hi=0.0; }
        else { let root=sqrt(disc); lo=max(lo,(-b-root)/a); hi=min(hi,(-b+root)/a); }
    }
    if(abs(dir.y)<1e-10) { if(origin.y<h.x || origin.y>h.y) { hi=0.0; } }
    else { let y0=(h.x-origin.y)/dir.y; let y1=(h.y-origin.y)/dir.y; lo=max(lo,min(y0,y1)); hi=min(hi,max(y0,y1)); }
    return select(vec2f(0.0),vec2f(lo,hi),hi>lo);
}
fn WeatherRainTexture(p: vec3f,i: i32) -> f32 {
    let m=WeatherR(i,3); let h=WeatherR(i,5); let q=(p.xz-m.xy)/32.0; let seed=m.z%127.0;
    let a=sin(6.2831853*(q.x+0.25*sin(q.y*3.14159265)+p.y/256.0+h.w/256.0)+seed);
    let b=sin(6.2831853*(q.y*0.5-q.x*0.25)+seed*0.37);
    return 0.55+0.25*a+0.2*b;
}
// The distant rain curtain's structure cannot rely on the 32m-period rain pattern: with a budget of
// 24 steps spanning over a kilometer the step length reaches 40-60m and samples land between adjacent
// peaks and troughs, leaving only a stable bright/dark band. Here the high-frequency pattern is faded
// toward its mean by step length and a 260m-period low-frequency cloud-scale swell takes over the far
// layer; both components have a mean of 0.55, so no brightness jump appears at the switch.
fn WeatherRainBank(p: vec3f,i: i32) -> f32 {
    let m=WeatherR(i,3); let q=(p.xz-m.xy)/260.0; let seed=m.z%127.0;
    let a=sin(6.2831853*(q.x*0.5+q.y*0.35)+seed*0.81);
    let b=sin(6.2831853*(q.y*0.8-q.x*0.4)+seed*0.19);
    return 0.55+0.12*a+0.08*b;
}
fn WeatherRainDetail(stepLength: f32) -> f32 {
    let t=clamp((64.0-stepLength)/48.0,0.0,1.0);
    return t*t*(3.0-2.0*t);
}
fn WeatherRainMedium(p: vec3f,distance: f32,stepLength: f32) -> vec4f {
    var source=vec3f(0.0); var sigma=0.0; var remaining=1.0;
    let fade=smoothstep(WeatherH(1).x,WeatherH(1).y,distance);
    let detail=WeatherRainDetail(stepLength);
    for(var i=0;i<WeatherCount();i++) {
        let weight=WeatherFootprint(p,i,false); let rain=WeatherR(i,4); let h=WeatherR(i,5); let light=WeatherR(i,7);
        let edge=min(20.0,max((h.y-h.x)*0.08,0.001));
        let vertical=smoothstep(0.0,edge,p.y-h.x)*smoothstep(0.0,edge,h.y-p.y);
        let pattern=mix(WeatherRainBank(p,i),WeatherRainTexture(p,i),detail);
        let density=remaining*weight*rain.w*light.w*vertical*fade*pattern;
        sigma+=density; source+=light.xyz*density; remaining*=1.0-weight;
    }
    return vec4f(source,sigma);
}
fn WeatherRain(origin: vec3f,dir: vec3f,distance: f32) -> vec4f {
    var rgb=vec3f(0.0); var trans=1.0; var spans: array<vec2f,8>; var count=0;
    if(WeatherH(1).w>0.5 && distance>WeatherH(1).x) {
        for(var i=0;i<WeatherCount();i++) {
            let hit=WeatherRainInterval(origin,dir,min(distance,WeatherH(1).z),i);
            if(WeatherR(i,4).w>0.0 && hit.y>hit.x) {
                var at=count;
                for(var j=6;j>=0;j--) {
                    if(j<count && spans[j].x>hit.x) { spans[j+1]=spans[j]; at=j; }
                }
                spans[at]=hit; count++;
            }
        }
        // Merge overlapping spans and sample along the full field to avoid double integration and
        // missing narrow regions.
        var merged=0;
        for(var mergeIndex=0;mergeIndex<8;mergeIndex++) {
            if(mergeIndex<count) {
                let previous=max(merged-1,0);
                if(merged>0 && spans[mergeIndex].x<=spans[previous].y) {
                    spans[previous].y=max(spans[previous].y,spans[mergeIndex].y);
                } else { spans[merged]=spans[mergeIndex]; merged++; }
            }
        }
        var lengthSum=0.0;
        for(var sumIndex=0;sumIndex<merged;sumIndex++) { lengthSum+=spans[sumIndex].y-spans[sumIndex].x; }
        let budget=clamp(i32(WeatherH(0).w),8,24); var left=budget;
        for(var segmentIndex=0;segmentIndex<merged;segmentIndex++) {
            let len=spans[segmentIndex].y-spans[segmentIndex].x;
            var steps=1+i32(floor(f32(budget-merged)*len/max(lengthSum,0.001)));
            if(segmentIndex==merged-1) { steps=left; }
            left-=steps; let ds=len/f32(steps);
            for(var k=0;k<steps;k++) {
                // Fixed lattice points beat against the rain pattern period at distance; a per-pixel,
                // per-segment deterministic jitter turns the beating into fine noise. The integral stays
                // unbiased and no extra ABI or random table is needed.
                let jitter=fract(sin(dot(dir,vec3f(12.9898,78.233,37.719))+f32(k)*13.77+spans[segmentIndex].x*0.001)*43758.5453);
                let t=spans[segmentIndex].x+(f32(k)+jitter)*ds;
                let local=WeatherRainMedium(origin+dir*t,t,ds); let transmission=exp(-local.w*ds);
                var integral=ds; if(local.w>1e-8) { integral=(1.0-transmission)/local.w; }
                rgb+=trans*local.xyz*integral; trans*=transmission;
            }
        }
    }
    return vec4f(rgb,trans);
}
""";

    public const string CompositeWgsl = """
struct WeatherFrame { values: array<vec4f> }
@group(0) @binding(0) var<storage,read> weatherFrame: WeatherFrame;
@group(0) @binding(3) var sceneColor: texture_2d<f32>;
@group(0) @binding(4) var sceneDepth: texture_depth_2d;
@group(0) @binding(5) var sceneAo: texture_2d<f32>;
@group(0) @binding(6) var<uniform> compositeParams: vec4f;
@group(0) @binding(7) var linearClamp: sampler;
fn WF(i: i32) -> vec4f { return weatherFrame.values[i]; }
fn WeatherH(i: i32) -> vec4f { return WF(94+i); }
fn WeatherR(i: i32,row: i32) -> vec4f { return WF(96+i*8+row); }
// 2-7 Lightning: row 161 of weatherFrame (index 160) = dominant strike (xyz world position + w
// envelope brightness).
fn WeatherFlash() -> vec4f { return WF(160); }
""" + CommonWgsl + RainWgsl + """
@fragment fn PSMain(@builtin(position) position: vec4f) -> @location(0) vec4f {
    let pixel=vec2i(position.xy); let uv=position.xy/vec2f(textureDimensions(sceneColor));
    let depth=textureLoad(sceneDepth,pixel,0);
    let ndc=vec4f(uv*vec2f(2,-2)+vec2f(-1,1),select(depth,0.5,depth>=1.0),1.0);
    let p=ndc.x*WF(0)+ndc.y*WF(1)+ndc.z*WF(2)+WF(3);
    let delta=p.xyz/p.w-WF(4).xyz; var distance=length(delta); let ray=delta/max(distance,1e-8);
    if(depth>=1.0) { distance=WeatherH(1).z; }
    let medium=WeatherRain(WF(4).xyz,ray,distance); let color=textureLoad(sceneColor,pixel,0);
    var ao=1.0;
    if(compositeParams.y>0.5) { ao=mix(1.0,textureSampleLevel(sceneAo,linearClamp,uv,0.0).r,compositeParams.x); }
    return vec4f(color.xyz*ao*medium.w+medium.xyz,color.w);
}
""";
}
