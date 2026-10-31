// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

public sealed partial class VolumetricFogEffect
{
    /// <summary>后端声明只读 fogFrame.values 与 texture_3d fogIntegrated/fogLocal；只用整数 load，不依赖 sampler。</summary>
    public const string SamplingWgsl = """
fn FogF(i: u32) -> vec4f { return fogFrame.values[i]; }
fn FogTransform(p: vec4f,i: u32) -> vec4f { return p.x*FogF(i)+p.y*FogF(i+1u)+p.z*FogF(i+2u)+p.w*FogF(i+3u); }
fn FogZ(k: f32) -> f32 { return 0.5*(pow(1.0+2.0*FogF(8u).w,k/FogF(10u).z)-1.0); }
fn FogSegment(acc: vec4f,local: vec4f,ds: f32) -> vec4f {
    let tau=local.w*ds; let t=exp(-tau); var integral=ds*(1.0-tau*0.5+tau*tau/6.0);
    if(tau>=0.001) { integral=(1.0-t)/max(local.w,1e-30); }
    return vec4f(min(acc.xyz+acc.w*local.xyz*integral,vec3f(65000)),acc.w*t);
}
fn FogColumn(position: vec2i,k: i32,depth: f32) -> vec4f {
    let xy=clamp(position,vec2i(0),vec2i(FogF(10u).xy)-1);
    var acc=textureLoad(fogIntegrated,vec3i(xy,k),0);
    if(k<i32(FogF(10u).z)) {
        let uv=(vec2f(xy)+0.5)/FogF(10u).xy;
        let world=FogTransform(vec4f(uv*vec2f(2,-2)+vec2f(-1,1),0.5,1),0u);
        let ray=world.xyz/world.w-FogF(8u).xyz;
        let rayLength=length(ray/max(dot(ray,FogF(9u).xyz),1e-6));
        acc=FogSegment(acc,textureLoad(fogLocal,vec3i(xy,k),0),max(depth-FogZ(f32(k)),0.0)*rayLength);
    }
    return acc;
}
fn FogAtWorld(world: vec3f) -> vec4f {
    var depth=dot(world-FogF(8u).xyz,FogF(9u).xyz);
    if(depth<=0) { return vec4f(0,0,0,1); }
    let clip=FogTransform(vec4f(world,1),22u);
    if(clip.w<=0) { return vec4f(0,0,0,1); }
    let uv=clamp(clip.xy/clip.w*vec2f(0.5,-0.5)+0.5,vec2f(0),vec2f(1));
    depth=min(depth,FogF(8u).w);
    var k=min(i32(floor(log(1.0+2.0*depth)/log(1.0+2.0*FogF(8u).w)*FogF(10u).z)),i32(FogF(10u).z)-1);
    if(depth>=FogF(8u).w) { k=i32(FogF(10u).z); }
    let q=uv*FogF(10u).xy-0.5; let f=fract(q); let b=vec2i(floor(q));
    let a=mix(FogColumn(b,k,depth),FogColumn(b+vec2i(1,0),k,depth),f.x);
    let c=mix(FogColumn(b+vec2i(0,1),k,depth),FogColumn(b+vec2i(1,1),k,depth),f.x);
    return mix(a,c,f.y);
}
fn FogAtDepth(uv: vec2f,depth: f32) -> vec4f {
    let p=FogTransform(vec4f(uv*vec2f(2,-2)+vec2f(-1,1),select(depth,0.5,depth>=1.0),1),18u);
    var world=p.xyz/p.w;
    if(depth>=1.0) { let ray=world-FogF(8u).xyz; world=FogF(8u).xyz+ray*(FogF(8u).w/max(dot(ray,FogF(9u).xyz),1e-6)); }
    return FogAtWorld(world);
}
fn FogTransparent(premultiplied: vec4f,fog: vec4f,additive: bool) -> vec4f {
    return vec4f(premultiplied.xyz*fog.w+select(premultiplied.w,0.0,additive)*fog.xyz,premultiplied.w);
}
fn FogOpaque(color: vec4f,fog: vec4f,ao: f32) -> vec4f { return vec4f(color.xyz*ao*fog.w+fog.xyz,color.w); }
""";

    public const string CompositeVertexWgsl = """
@vertex fn VSMain(@builtin(vertex_index) id:u32)->@builtin(position) vec4f {
    let p=vec2f(f32((id<<1u)&2u),f32(id&2u));
    return vec4f(p*2.0-1.0,0,1);
}
""";

    public const string CompositeWgsl = """
struct FogCompositeFrame { values:array<vec4f> }
@group(0) @binding(0) var<storage,read> fogFrame:FogCompositeFrame;
@group(0) @binding(1) var fogIntegrated:texture_3d<f32>;
@group(0) @binding(2) var fogLocal:texture_3d<f32>;
@group(0) @binding(3) var sceneColor:texture_2d<f32>;
@group(0) @binding(4) var sceneDepth:texture_depth_2d;
@group(0) @binding(5) var sceneAo:texture_2d<f32>;
@group(0) @binding(6) var<uniform> compositeParams:vec4f;
@group(0) @binding(7) var linearClamp:sampler;
""" + SamplingWgsl + """

@fragment fn PSMain(@builtin(position) position:vec4f)->@location(0) vec4f {
    let uv=position.xy/vec2f(textureDimensions(sceneColor));
    let pixel=vec2i(position.xy);
    var ao=1.0;
    if(compositeParams.y>0.5) { ao=mix(1.0,textureSampleLevel(sceneAo,linearClamp,uv,0).r,compositeParams.x); }
    return FogOpaque(textureLoad(sceneColor,pixel,0),FogAtDepth(uv,textureLoad(sceneDepth,pixel,0)),ao);
}
""";

    const string CommonWgsl = """
struct Params { dispatchSize: vec4f }
struct Frame { values: array<vec4f> }
@group(0) @binding(0) var<uniform> params: Params;
@group(0) @binding(1) var<storage,read> frameData: Frame;
fn F(i: u32) -> vec4f { return frameData.values[i]; }
fn Transform(p: vec4f,i: u32) -> vec4f { return p.x*F(i)+p.y*F(i+1u)+p.z*F(i+2u)+p.w*F(i+3u); }
fn Z(k: f32) -> f32 { return 0.5*(pow(1.0+2.0*F(8u).w,k/F(10u).z)-1.0); }
fn Ray(uv: vec2f) -> vec3f {
    let p=Transform(vec4f(uv*vec2f(2,-2)+vec2f(-1,1),0.5,1),0u);
    let r=p.xyz/p.w-F(8u).xyz; return r/max(dot(r,F(9u).xyz),1e-6);
}
fn Segment(acc: vec4f,local: vec4f,ds: f32) -> vec4f {
    let tau=local.w*ds; let t=exp(-tau); var integral=ds*(1.0-tau*0.5+tau*tau/6.0);
    if(tau>=0.001) { integral=(1.0-t)/max(local.w,1e-30); }
    return vec4f(min(acc.xyz+acc.w*local.xyz*integral,vec3f(65000)),acc.w*t);
}
fn Sat(x: f32) -> f32 { return clamp(x,0.0,1.0); }
""";

    const string NoiseWgsl = """
struct Params { dispatchSize: vec4f }
@group(0) @binding(0) var<uniform> params: Params;
@group(0) @binding(1) var outputVolume: texture_storage_3d<rgba8unorm,write>;
@compute @workgroup_size(4,4,4) fn CSMain(@builtin(global_invocation_id) id: vec3u) {
    if(all(id<vec3u(64u))) {
        var h=(id.x*73856093u) ^ (id.y*19349663u) ^ (id.z*83492791u) ^ 0x9e3779b9u;
        h=h^(h>>16u); h=h*0x7feb352du; h=h^(h>>15u); h=h*0x846ca68bu; h=h^(h>>16u);
        let n=f32(h&255u)/255.0; textureStore(outputVolume,vec3i(id),vec4f(n));
    }
}
""";

    const string InjectionWgsl = CommonWgsl + """
@group(0) @binding(2) var noiseVolume: texture_3d<f32>;
@group(0) @binding(3) var shadowAtlas: texture_depth_2d;
@group(0) @binding(4) var cloudNoise: texture_2d<f32>;
@group(0) @binding(5) var outputVolume: texture_storage_3d<rgba16float,write>;
fn Noise(p: vec3f,scale: f32,amount: f32) -> f32 {
    let q=fract(p*scale)*64.0-0.5; let f=fract(q); let b=vec3i(floor(q)); var n=0.0;
    for(var z=0;z<2;z++) { for(var y=0;y<2;y++) { for(var x=0;x<2;x++) {
        let w=mix(1.0-f,f,vec3f(f32(x),f32(y),f32(z)));
        n+=textureLoad(noiseVolume,(b+vec3i(x,y,z))&vec3i(63),0).x*w.x*w.y*w.z;
    } } }
    return 1.0+amount*(2.0*n-1.0);
}
fn CloudSample(uv: vec2f) -> vec4f {
    let size=vec2i(textureDimensions(cloudNoise)); let p=fract(uv)*vec2f(size)-0.5; let f=fract(p); let b=vec2i(floor(p)); var n=vec4f(0);
    for(var y=0;y<2;y++) { for(var x=0;x<2;x++) {
        let c=(b+vec2i(x,y)+size)%size; let a=mix(1.0-f,f,vec2f(f32(x),f32(y)));
        n+=textureLoad(cloudNoise,c,0)*a.x*a.y;
    } }
    return n;
}
fn WeatherH(i: i32) -> vec4f { var h=F(u32(118+i)); if(i==0 && F(16u).w<0.5) { h.y=0.0; } return h; }
fn WeatherR(i: i32,row: i32) -> vec4f { return F(u32(120+i*8+row)); }
fn WeatherNoise(uv: vec2f) -> vec4f { return CloudSample(uv); }
""" + WeatherShaders.CommonWgsl + WeatherShaders.CloudWgsl + """
fn CloudShadow(p: vec3f,l: vec3f) -> f32 {
    var tau=0.0;
    if(F(16u).w>0.5 && F(116u).x>0 && l.y>0) {
        let invY=1.0/max(l.y,0.05);
        for(var i=0u;i<min(u32(F(115u).w),3u);i++) {
            let a=F(109u+i); let b=F(112u+i); let pos=p.xz*0.001+l.xz*(max(a.x-p.y*0.001,0.0)*invY);
            let n=CloudSample((pos+b.xy)*b.z);
            var d=Sat((n.x*mix(1.0,n.w,0.7)-(1.0-a.z))/max(a.z,0.001));
            d*=Sat(1.0-b.w*0.5*(n.y+n.z)); tau+=d*a.w*a.y*invY;
        }
    }
    return (1.0-F(116u).x*Sat(1.0-exp(-tau)))*WeatherCloudShadow(p,l);
}
fn ShadowTile(p: vec3f,matrixIndex: u32,tile: i32) -> f32 {
    let clip=Transform(vec4f(p,1),matrixIndex); var result=1.0;
    if(clip.w>0) {
        let ndc=clip.xyz/clip.w;
        if(all(abs(ndc.xy)<=vec2f(1)) && ndc.z>=0 && ndc.z<=1) {
            let halfSize=vec2i(textureDimensions(shadowAtlas))/2; let origin=vec2i(tile&1,tile>>1)*halfSize;
            let texel=(ndc.xy*vec2f(0.5,-0.5)+0.5)*vec2f(halfSize)+vec2f(origin);
            let count=select(1,4,F(14u).w>1.0); var visible=0.0;
            for(var i=0;i<count;i++) {
                let offset=select(vec2f(f32(i&1)-0.5,f32(i>>1)-0.5),vec2f(0),count==1);
                let c=clamp(vec2i(floor(texel+offset)),origin,origin+halfSize-1);
                visible+=select(0.0,1.0,ndc.z-0.0002<=textureLoad(shadowAtlas,c,0));
            }
            result=mix(1.0,visible/f32(count),Sat(F(89u).y));
        }
    }
    return result;
}
fn Visibility(p: vec3f,depth: f32,light: i32) -> f32 {
    var result=1.0; let indices=F(34u); let flags=F(88u); let splits=F(87u);
    if(light==i32(indices.z) && flags.x>0.5 && depth<splits.w) {
        let count=clamp(i32(flags.y),1,3); var cascade=0;
        if(count>1 && depth>splits.x) { cascade=1; }
        if(count>2 && depth>splits.y) { cascade=2; }
        result=ShadowTile(p,u32(67+cascade*4),cascade);
        let end=splits[cascade]; var start=0.0; if(cascade>0) { start=splits[cascade-1]; }
        let blend=Sat((depth-mix(start,end,0.9))/max((end-start)*0.1,0.001)); var next=1.0;
        if(cascade+1<count) { next=ShadowTile(p,u32(67+(cascade+1)*4),cascade+1); }
        result=mix(result,next,blend);
        result=mix(result,1.0,Sat((depth-splits.w*0.9)/max(splits.w*0.1,0.001)));
    }
    if(light==i32(indices.w) && F(89u).x>0.5) { result*=ShadowTile(p,83u,3); }
    return result;
}
@compute @workgroup_size(4,4,4) fn CSMain(@builtin(global_invocation_id) id: vec3u) {
    if(all(vec3f(id)<F(10u).xyz)) {
        let ray=Ray((vec2f(id.xy)+0.5)/F(10u).xy); let depth=Z(f32(id.z)+0.5);
        let p=F(8u).xyz+ray*depth; let windP=p-F(13u).xyz; let density=F(11u);
        var sigma=density.x*exp(-density.z*max(p.y-density.y,0.0))*Noise(windP,F(12u).w,density.w);
        var scattering=F(12u).xyz*sigma; var exclusion=1.0;
        for(var i=0u;i<min(u32(F(13u).w),32u);i++) {
            let base=192u+i*8u; let local=Transform(vec4f(p,1),base).xyz; let shape=F(base+5u); let medium=F(base+4u);
            let r=select(max(abs(local.x),max(abs(local.y),abs(local.z))),length(local),shape.x>0.5);
            let edge=Sat((1.0-r)/max(shape.z,1e-6)); let weight=select(0.0,edge*edge*(3.0-2.0*edge),r<1);
            if(shape.y>0.5) { exclusion*=1.0-weight; }
            else { let d=medium.w*weight*Noise(windP,F(base+6u).x,shape.w); sigma+=d; scattering+=medium.xyz*d; }
        }
        let fade=1.0-smoothstep(F(8u).w*0.9,F(8u).w,depth); sigma*=exclusion*fade; scattering*=exclusion*fade;
        var illumination=F(14u).xyz; let viewRay=normalize(ray); let g=F(9u).w;
        for(var i=0;i<clamp(i32(F(34u).x),0,8);i++) {
            let b=u32(35+i*4); let pos=F(b); let color=F(b+1u); let direction=F(b+2u); let spot=F(b+3u);
            let delta=pos.xyz-p; let distance=length(delta); var attenuation=1.0; var l=-direction.xyz;
            if(direction.w<1.5) {
                l=delta/max(distance,0.0001); attenuation=1.0/max(distance*distance,0.0001);
                if(pos.w>0) { let win=Sat(1.0-pow(distance/pos.w,4.0)); attenuation*=win*win; }
                if(direction.w>0.5) { attenuation*=smoothstep(spot.y,max(spot.x,spot.y+1e-6),dot(-l,direction.xyz)); }
            }
            else { attenuation*=CloudShadow(p,l); }
            let phase=(1.0-g*g)/(12.5663706144*pow(max(1.0+g*g-2.0*g*dot(viewRay,l),1e-4),1.5));
            illumination+=color.xyz*color.w*attenuation*phase*Visibility(p,depth,i);
        }
        textureStore(outputVolume,vec3i(id),min(vec4f(scattering*illumination,sigma),vec4f(65000)));
    }
}
""";

    const string FilterWgsl = CommonWgsl + """
@group(0) @binding(2) var currentVolume: texture_3d<f32>;
@group(0) @binding(3) var historyVolume: texture_3d<f32>;
@group(0) @binding(4) var outputVolume: texture_storage_3d<rgba16float,write>;
@group(0) @binding(15) var linearClamp: sampler;
@compute @workgroup_size(4,4,4) fn CSMain(@builtin(global_invocation_id) id: vec3u) {
    if(all(vec3f(id)<F(10u).xyz)) {
        let current=textureLoad(currentVolume,vec3i(id),0); var result=current;
        if(F(15u).w>0.5 && F(10u).w>0) {
            let p=F(8u).xyz+Ray((vec2f(id.xy)+0.5)/F(10u).xy)*Z(f32(id.z)+0.5);
            let clip=Transform(vec4f(p,1),4u); let depth=dot(p-F(15u).xyz,F(16u).xyz);
            let uv=clip.xy/max(clip.w,1e-6)*vec2f(0.5,-0.5)+0.5;
            if(clip.w>0 && all(uv>=vec2f(0)) && all(uv<=vec2f(1)) && depth>=0 && depth<=F(8u).w) {
                let z=log(1.0+2.0*depth)/log(1.0+2.0*F(8u).w);
                let history=textureSampleLevel(historyVolume,linearClamp,vec3f(uv,z),0); var lo=current; var hi=current;
                for(var axis=0;axis<3;axis++) { for(var sign=-1;sign<=1;sign+=2) {
                    var c=vec3i(id); c[axis]+=sign; c=clamp(c,vec3i(0),vec3i(F(10u).xyz)-1);
                    let value=textureLoad(currentVolume,c,0); lo=min(lo,value); hi=max(hi,value);
                } }
                let densityReject=Sat(1.0-abs(history.w-current.w)/max(current.w,0.0001));
                let lightReject=Sat(1.0-length(history.xyz-current.xyz)/max(length(current.xyz),0.0001));
                result=mix(current,clamp(history,lo,hi),F(10u).w*densityReject*lightReject);
            }
        }
        textureStore(outputVolume,vec3i(id),result);
    }
}
""";

    const string IntegrationWgsl = CommonWgsl + """
@group(0) @binding(2) var localVolume: texture_3d<f32>;
@group(0) @binding(3) var outputVolume: texture_storage_3d<rgba16float,write>;
@compute @workgroup_size(8,8,1) fn CSMain(@builtin(global_invocation_id) id: vec3u) {
    if(all(vec2f(id.xy)<F(10u).xy)) {
        let rayLength=length(Ray((vec2f(id.xy)+0.5)/F(10u).xy)); var acc=vec4f(0,0,0,1);
        textureStore(outputVolume,vec3i(vec2i(id.xy),0),acc);
        for(var z=0;z<i32(F(10u).z);z++) {
            let local=textureLoad(localVolume,vec3i(vec2i(id.xy),z),0);
            acc=Segment(acc,local,(Z(f32(z)+1.0)-Z(f32(z)))*rayLength);
            textureStore(outputVolume,vec3i(vec2i(id.xy),z+1),acc);
        }
    }
}
""";
}
