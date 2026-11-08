// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

public sealed partial class VolumetricFogEffect
{
    /// <summary>The backend declares read-only fogFrame.values and texture_3d fogIntegrated/fogLocal; only integer loads are used, with no sampler dependency.</summary>
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
@group(0) @binding(6) var farShadow: texture_depth_2d;
@group(0) @binding(7) var localShadow: texture_depth_2d;
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
// 2-7 lightning: in the fog frame SceneLightParams starts at index 32, and WeatherFlash is row 152 within it, so the absolute position is 184.
fn WeatherFlash() -> vec4f { return F(184u); }
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
// One 1024^2 orthographic map centred on the medium rather than on the camera, so its matrix does not move when the eye
// does. Coarser than any cascade by design: it exists to keep a shaft readable where the cascades have already faded
// out, not to resolve the trunk that makes it. Four taps over one texel of spread, because a hard edge on a volumetric
// shaft reads as a hole punched in the light rather than as the silhouette of a branch.
fn FogFarShadow(p: vec3f,depth: f32,reach: f32) -> f32 {
    let clip=Transform(vec4f(p,1),448u); var result=1.0;
    if(clip.w>0) {
        let ndc=clip.xyz/clip.w;
        if(all(abs(ndc.xy)<=vec2f(1)) && ndc.z>=0 && ndc.z<=1) {
            let size=vec2i(textureDimensions(farShadow));
            let texel=(ndc.xy*vec2f(0.5,-0.5)+0.5)*vec2f(size)-0.5;
            let base=vec2i(floor(texel)); var visible=0.0; let bias=F(452u).y;
            for(var i=0;i<4;i++) {
                let c=clamp(base+vec2i(i&1,i>>1),vec2i(0),size-1);
                visible+=select(0.0,1.0,ndc.z-bias<=textureLoad(farShadow,c,0));
            }
            result=mix(1.0,visible*0.25,Sat(F(89u).y));
        }
    }
    // Hand over exactly where the cascades hand off: the same 10% band of the same reach, so a froxel crossing the
    // boundary keeps one shadow instead of acquiring a second. Inside the band the far map is not consulted at all -
    // it is the coarser of the two, and mixing it in would only soften edges the cascades already resolve correctly.
    return mix(1.0,result,Sat((depth-reach*0.9)/max(reach*0.1,0.001)));
}
// Local point-light cubes: up to two lamps whose light actually reaches the medium, six 90 degree faces each, packed four
// to a row. Occlusion is the one thing a punctual light cannot otherwise express - without this term a torch inside a
// barn lights the mist through the wall, because 1/d^2 attenuation has no notion of what stands between.
// Eight taps: four axial at radius two texels and four diagonals at radius one. The axial pair carries three quarters of the
// second moment and the diagonals close the sectors between them, so no direction along the face is under-sampled. The sum
// of squares is twelve in x and twelve in y with every cross term zero, which is what lets LocalFaceVisibility fit a plane
// with two dot products instead of solving a normal-equation system.
fn LocalRingOffset(tapIndex: i32) -> vec2i {
    // 0..3 walk -Y,+X,+Y,-X at radius two; 4..7 walk the diagonals at radius one. Generated from the index rather than
    // tabulated, so all four languages spell the same ring and there is no array to keep in step with the C# side.
    if(tapIndex<4) {
        let reach=select(-2,2,((tapIndex^(tapIndex>>1))&1)!=0);
        return select(vec2i(0,reach),vec2i(reach,0),(tapIndex&1)!=0);
    }
    let diagonal=tapIndex-4;
    return vec2i(select(-1,1,((diagonal^(diagonal>>1))&1)!=0),select(-1,1,(diagonal>>1)!=0));
}
// One face's visibility at p in 0..1, before the slot's strength is applied. Shared by the main face and, at a seam, by the
// neighbour. The margin is slotMeta.z / clip.w: clip.w is the distance along the face's own axis, and slotMeta.z already
// folded the lamp's near/far pair and the texel width into one number, so the division is the whole reconstruction.
fn LocalFaceVisibility(p: vec3f,matrixRow: u32,origin: vec2i,slotMeta: vec4f) -> f32 {
    let clip=Transform(vec4f(p,1),matrixRow);
    if(clip.w<=0) { return 1.0; }
    let ndc=clip.xyz/clip.w;
    // A tenth of a texel of slack on the bounds: the seam blend evaluates the neighbour face right up to its own edge, and a
    // strict 1.0 would reject that tap over a rounding error and hand back "unshadowed", drawing a bright hairline.
    if(!(all(abs(ndc.xy)<vec2f(1.001)) && ndc.z>=0 && ndc.z<=1)) { return 1.0; }
    let faceSize=128;
    let texel=(ndc.xy*vec2f(0.5,-0.5)+0.5)*vec2f(f32(faceSize))+vec2f(origin)-0.5;
    let base=vec2i(floor(texel));
    var depths: array<f32,8>;
    var sum=0.0; var gx=0.0; var gy=0.0; var lo=1.0; var hi=0.0;
    for(var tapIndex=0;tapIndex<8;tapIndex++) {
        let offset=LocalRingOffset(tapIndex);
        // tapDepth, not sample: sample is a WGSL keyword (textureSample and the sampling modes).
        let tapDepth=textureLoad(localShadow,clamp(base+offset,origin,origin+faceSize-1),0);
        depths[tapIndex]=tapDepth; sum+=tapDepth; lo=min(lo,tapDepth); hi=max(hi,tapDepth);
        gx+=f32(offset.x)*tapDepth; gy+=f32(offset.y)*tapDepth;
    }
    // 1/12 rather than 1/8: the least-squares slope over these eight offsets has sum(offset^2) = 12 as its denominator, and
    // the cross terms vanish, so the gradient is just the offset-weighted sum rescaled. The plane's value at the centre is
    // the plain mean, because the offsets themselves sum to zero in both axes.
    gx*=0.083333333; gy*=0.083333333;
    let bias=slotMeta.z/max(clip.w,0.001);
    // A plane through a silhouette is not a plane through a surface. When the taps already disagree by more than a dozen
    // margins, the fit would invent a slope across the edge and smear the very silhouette the tap was meant to resolve, so
    // the tilt is dropped and every tap is read raw. Eight margins is twelve texels at the authored 1.5, comfortably above
    // the four-texel spread even for a surface leaning a full texel per texel, while a step wider than that is a
    // discontinuity the fit cannot follow anyway.
    let tilt=slotMeta.w*select(0.0,1.0,hi-lo<8.0*bias);
    let plane=sum*0.125;
    var visible=0.0;
    for(var tapIndex=0;tapIndex<8;tapIndex++) {
        let offset=LocalRingOffset(tapIndex);
        let fitted=clamp(plane+gx*f32(offset.x)+gy*f32(offset.y),lo,hi);
        visible+=select(0.0,1.0,ndc.z-bias<=mix(depths[tapIndex],fitted,tilt));
    }
    return visible*0.125;
}
fn FogLocalShadow(p: vec3f,light: i32) -> f32 {
    var result=1.0;
    for(var slotIndex=0;slotIndex<2;slotIndex++) {
        let row=456+slotIndex*25; let slotMeta=F(u32(row+24));
        if(light==i32(slotMeta.x)) {
            // The face is chosen by the dominant axis of the lamp-to-froxel direction, the same test the CPU used to build
            // the matrices: -X,+X,-Y,+Y,-Z,+Z map to 1,0,3,2,5,4 under the leading sign, which is what keeps the pack
            // order the CPU writes and the order read here one and the same thing.
            let d=p-F(35u+u32(light)*4u).xyz; let ad=abs(d); var face=0;
            if(ad.x>=ad.y) { if(ad.x>=ad.z) { face=select(1,0,d.x>0); } else { face=select(5,4,d.z>0); } }
            else { if(ad.y>=ad.z) { face=select(3,2,d.y>0); } else { face=select(5,4,d.z>0); } }
            let tile=slotIndex*6+face;
            let origin=vec2i(tile%4,tile/4)*128;
            var visible=LocalFaceVisibility(p,u32(row+face*4),origin,slotMeta);
            // Seam: a froxel within a texel of the 45 degree boundary lands where the face it picked and the face it passed
            // over disagree about the silhouette, and that disagreement is a visible line running down a wall in the mist.
            // Both faces store the distance along their own axis, and the two axes are the two components of the same
            // vector, so on the boundary the two hold the same depth and mixing them is continuous. The band is one texel
            // wide, expressed in the face's own ndc - two units across 128 texels - so it needs no distance in metres.
            let dominant=max(ad.x,max(ad.y,ad.z));
            let second=max(min(ad.x,ad.y),min(max(ad.x,ad.y),ad.z));
            let band=dominant*0.015625;
            if(dominant-second<band) {
                var secondAxis=0;
                if(face<2) { secondAxis=select(2,1,ad.y>=ad.z); }
                else if(face<4) { secondAxis=select(2,0,ad.x>=ad.z); }
                else { secondAxis=select(1,0,ad.x>=ad.y); }
                let secondSign=select(select(d.z,d.y,secondAxis==1),d.x,secondAxis==0);
                let secondFace=secondAxis*2+select(1,0,secondSign>0);
                let secondTile=slotIndex*6+secondFace;
                let other=LocalFaceVisibility(p,u32(row+secondFace*4),vec2i(secondTile%4,secondTile/4)*128,slotMeta);
                let edge=Sat((dominant-second)/max(band,1e-6));
                visible=mix(other,visible,0.5+0.5*edge*edge*(3.0-2.0*edge));
            }
            result*=mix(1.0,visible,Sat(slotMeta.y));
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
    // The fog's own far map, and only for the cascade light: it is that light's depth from a different viewpoint, not a
    // second occluder. Past the cascade reach the fog had nothing to consult, which is what flattened every shaft seen
    // from outside the volume into a slab of uniform light.
    if(light==i32(indices.z) && F(448u).x>0.5) { result*=FogFarShadow(p,depth,splits.w); }
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
fn SliceVisibility(ray: vec3f,depth: f32,z0: f32,span: f32,light: i32) -> f32 {
    // One exit, both arms writing the same variable, matching DX. The centre sample is only taken when the
    // resample does not run: the two Gauss taps below overwrite it anyway, so computing it first spent one full
    // evaluation of the shadow chain on a value that was then thrown away, on exactly the froxels far enough out
    // to make that chain expensive.
    var result=0.0;
    if(light!=i32(F(34u).z) || span<0.5) {
        result=Visibility(F(8u).xyz+ray*depth,depth,light);
    } else {
        // Both Gauss points as one loop body, so the backend below Tint is handed one call site rather than two.
        // Tint itself keeps the function, but the driver that lowers this further is free to inline, and the
        // two-call form is what it would then duplicate. Same weights, same order, so the result is unchanged.
        var sum=0.0;
        for(var gaussIndex=0u;gaussIndex<2u;gaussIndex++) {
            let gaussT=select(z0+span*0.78867513,z0+span*0.21132487,gaussIndex==0u);
            sum+=Visibility(F(8u).xyz+ray*gaussT,gaussT,light);
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
fn FogPhase(c: f32,g: f32) -> f32 {
    let gb=-0.5*g;
    let d0=max(1.0+g*g-2.0*g*c,1e-4); let d1=max(1.0+gb*gb-2.0*gb*c,1e-4);
    let i0=1.0/d0; let i1=1.0/d1;
    return 0.059683104*(1.0-g*g)*i0*sqrt(i0)+0.019894368*(1.0-gb*gb)*i1*sqrt(i1);
}
@compute @workgroup_size(4,4,4) fn CSMain(@builtin(global_invocation_id) id: vec3u) {
    if(all(vec3f(id)<F(10u).xyz)) {
        let ray=Ray((vec2f(id.xy)+0.5)/F(10u).xy); let depth=Z(f32(id.z)+0.5);
        // A slice near the far end of the range is several metres thick, which is taller than a thin mist patch, so a
        // single centre sample makes that patch snap between hit and missed as the camera moves. Each volume is
        // therefore intersected with the ray and its weight scaled by the fraction of the slice it actually fills.
        let z0=Z(f32(id.z)); let span=max(Z(f32(id.z)+1.0)-z0,1e-6);
        let p=F(8u).xyz+ray*depth; let windP=p-F(13u).xyz; let density=F(11u);
        var sigma=density.x*exp(-density.z*max(p.y-density.y,0.0))*Noise(windP,F(12u).w,density.w);
        var scattering=F(12u).xyz*sigma; var exclusion=1.0; var emission=vec3f(0.0);
        // Directionality belongs to the medium, so the lights are resolved before the media are walked and the phase is
        // applied where each medium's own g is known. The expensive half of a light - the cascade atlas, the far map, the
        // cloud deck - stays once per light, never once per volume; only the phase, a handful of ALU, repeats.
        // rgb carries the radiance that reaches this froxel, w the cosine the phase is a function of.
        let viewRay=normalize(ray); let lightCount=u32(clamp(i32(F(34u).x),0,8));
        var lights=array<vec4f, 8>();
        for(var lightIndex=0u;lightIndex<lightCount;lightIndex++) {
            let b=35u+lightIndex*4u; let pos=F(b); let color=F(b+1u); let direction=F(b+2u); let spot=F(b+3u);
            let delta=pos.xyz-p; let distance=length(delta); var attenuation=1.0; var l=-direction.xyz;
            if(direction.w<1.5) {
                l=delta/max(distance,0.0001); attenuation=1.0/max(distance*distance,0.0001);
                if(pos.w>0) { let win=Sat(1.0-pow(distance/pos.w,4.0)); attenuation*=win*win; }
                if(direction.w>0.5) { attenuation*=smoothstep(spot.y,max(spot.x,spot.y+1e-6),dot(-l,direction.xyz)); }
            }
            else { attenuation*=CloudShadow(p,l); }
            lights[lightIndex]=vec4f(color.xyz*color.w*attenuation*SliceVisibility(ray,depth,z0,span,i32(lightIndex)),dot(viewRay,l));
        }
        let ambient=F(14u).xyz; var illumination=ambient;
        for(var phaseIndex=0u;phaseIndex<lightCount;phaseIndex++) { illumination+=lights[phaseIndex].xyz*FogPhase(lights[phaseIndex].w,F(9u).w); }
        scattering=F(12u).xyz*sigma*illumination;
        for(var i=0u;i<min(u32(F(13u).w),32u);i++) {
            let base=192u+i*8u; let shape=F(base+5u); let medium=F(base+4u); let noiseRow=F(base+6u);
            let lo=Transform(vec4f(F(8u).xyz,1),base).xyz; let ld=Transform(vec4f(ray,0),base).xyz;
            var t0: f32; var t1: f32;
            if(shape.x>0.5) {
                let a=dot(ld,ld); let b=dot(lo,ld); let disc=b*b-a*(dot(lo,lo)-1.0);
                if(a<=1e-12 || disc<=0.0) { continue; }
                let s=sqrt(disc); t0=(-b-s)/a; t1=(-b+s)/a;
            }
            else {
                let inv=vec3f(1.0)/ld; let ta=(-1.0-lo)*inv; let tb=(1.0-lo)*inv;
                let tn=min(ta,tb); let tf=max(ta,tb);
                t0=max(max(tn.x,tn.y),tn.z); t1=min(min(tf.x,tf.y),tf.z);
            }
            t0=max(t0,z0); t1=min(t1,z0+span);
            if(!(t1>t0)) { continue; }
            let coverage=(t1-t0)/span; let ts=0.5*(t0+t1);
            let pl=lo+ld*ts; let pw=F(8u).xyz+ray*ts;
            let r=select(max(abs(pl.x),max(abs(pl.y),abs(pl.z))),length(pl),shape.x>0.5);
            let edge=Sat((1.0-r)/max(shape.z,1e-6)); let weight=select(0.0,edge*edge*(3.0-2.0*edge),r<1)*coverage;
            if(shape.y>0.5) { exclusion*=1.0-weight; }
            else {
                let d=medium.w*weight*Noise(pw-F(13u).xyz,noiseRow.x,shape.w); sigma+=d;
                // This patch's own g, not the scene's - the whole reason it is carried per volume. A volume that never
                // authored one holds the scene value, so packing decides nothing and the shader reads one row.
                var light=ambient;
                for(var patchIndex=0u;patchIndex<lightCount;patchIndex++) { light+=lights[patchIndex].xyz*FogPhase(lights[patchIndex].w,noiseRow.y); }
                scattering+=medium.xyz*d*light;
                emission+=F(base+7u).xyz*d;
            }
        }
        let fade=1.0-smoothstep(F(8u).w*0.9,F(8u).w,depth);
        sigma*=exclusion*fade; scattering*=exclusion*fade; emission*=exclusion*fade;
        textureStore(outputVolume,vec3i(id),min(vec4f(scattering+emission,sigma),vec4f(65000)));
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
