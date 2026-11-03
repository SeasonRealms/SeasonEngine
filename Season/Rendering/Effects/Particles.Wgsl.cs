// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

/// <summary>
/// WebGPU WGSL mirror of the HLSL sources, compiled to GPU modules by the browser at runtime.
///
/// Four translation rules govern every line here.
///
/// Indexing: HLSL ByteAddressBuffer addresses bytes, WGSL arrays address elements. Every offset below
/// is the HLSL byte offset divided by the element stride, written out so the files can be diffed by
/// eye. Getting this wrong produces no diagnostic on any backend, only displaced data.
///
/// Matrices: the frame buffer holds the four *rows* of the view-projection. HLSL float4x4(r0..r3)
/// builds rows and is consumed as mul(v, M); WGSL mat4x4f(r0..r3) builds *columns* and is consumed as
/// M * v. Both evaluate to the same sum over v[i] * r[i], so no transpose is needed -- the same
/// cancellation already documented for the GLSL and MSL mirrors.
///
/// Slots: WGSL has no implicit binding order, so every slot is spelled out. The compute bindings must
/// match what Graphics.CreateComputeKernel hands to the JS layout builder, which uses the array index
/// of ComputeKernelDesc.Bindings as the binding number -- Params at 0, then every buffer in
/// declaration order. The draw side is bound by WGPUParticleDrawPipeline, group 0 binding 0 =
/// instances and binding 1 = frame.
///
/// Atomics: WGSL cannot alias one buffer as both plain and atomic within a single shader, and
/// atomicAdd demands a pointer into an atomic-typed slot. The counter therefore lives in a
/// five-element atomic array for the compaction kernel and a five-element plain array for the reset
/// kernel. Both views describe the same 20-byte <see cref="IndirectDrawIndexedArgs"/> block, and the
/// two kernels never run concurrently, so the aliasing is safe. Fixed length rather than
/// runtime-sized is deliberate: it pins the block to exactly the five fields the contract defines.
/// </summary>
public sealed partial class ParticleSystemEffect
{
    /// <summary>WebGPU compute: rewrites the five indirect arguments every frame. indexCount / instanceCount / startIndex / baseVertex come from Params; startInstance stays 0.</summary>
    const string ResetWgsl = """
struct Params { resetParams: vec4f }
struct Args { values: array<u32,5> }
@group(0) @binding(0) var<uniform> params: Params;
@group(0) @binding(1) var<storage,read_write> args: Args;
@compute @workgroup_size(1,1,1) fn CSMain(@builtin(global_invocation_id) id: vec3u) {
    if(id.x==0u) {
        // IndirectDrawIndexedArgs: indexCount / instanceCount / startIndex / baseVertex / startInstance.
        // u32 indices 0..4 are the HLSL byte offsets 0/4/8/12/16 divided by the 4-byte stride.
        // startInstance stays 0 by contract -- WebGPU rejects a non-zero firstInstance without the
        // optional indirect-first-instance feature -- so instance_index equals the compacted slot.
        // The particle path passes 0 for the last three, so the output is bit-identical to before the
        // parameterization; the lightning channel passes a fixed segment count.
        args.values[0]=u32(params.resetParams.x);
        args.values[1]=u32(params.resetParams.y);
        args.values[2]=u32(params.resetParams.z);
        args.values[3]=u32(params.resetParams.w);
        args.values[4]=0u;
    }
}
""";

    /// <summary>WebGPU compute: derives one particle per thread, culls it, and appends survivors while raising the indirect instance count.</summary>
    const string CompactWgsl = """
// Shares the 96B parameter block with HLSL, six vec4f in total:
// p[0]=phase/box radius/box height/budget, p[1]=origin.xyz/particle size, p[2]=color.
// p[3]=density/sway/lifetime/seed, p[4]=acceleration.xyz/region count, p[5]=wind.xyz/channel and window flags.
struct Params { p: array<vec4f,6> }
struct Frame { values: array<vec4f> }
struct Instances { values: array<vec4f> }
struct Args { values: array<atomic<u32>,5> }
@group(0) @binding(0) var<uniform> params: Params;
@group(0) @binding(1) var<storage,read> frameData: Frame;
@group(0) @binding(2) var<storage,read_write> instanceData: Instances;
@group(0) @binding(3) var<storage,read_write> args: Args;
@group(0) @binding(4) var<storage,read> regionData: Frame;
fn ParticleHash(seed: u32) -> f32 {
    var n=(seed<<13u)^seed;
    n=n*(n*n*15731u+789221u)+1376312589u;
    return f32(n&0x00FFFFFFu)/16777216.0;
}
@compute @workgroup_size(64,1,1) fn CSMain(@builtin(global_invocation_id) id: vec3u) {
    let p0=params.p[0]; let p1=params.p[1]; let p2=params.p[2]; let p3=params.p[3]; let p4=params.p[4];
    let p5=params.p[5];
    if(id.x<u32(p0.w)) {
        // Density gating is index-ordered, not hash-based: what is admitted is the prefix
        // [0, density x budget), so the CPU only needs to dispatch that many indices and clear
        // weather or light snow need not spin at full budget.
        // The hash is still computed because the index is shifted by the seed -- that shift decides
        // the spatial distribution of this batch of indices.
        let ordinal=f32(id.x);
        let cutoff=p3.x*p0.w;
        let index=id.x+u32(p3.w);
        let h0=ParticleHash(index*3u+1u);
        let h1=ParticleHash(index*5u+7u);
        let h2=ParticleHash(index*11u+23u);
        let h3=ParticleHash(index*17u+41u);
        if(ordinal<cutoff) {
            // The phase is timed by lifetime, so the former fall is at once the normalized age in
            // [0,1); converting it back to seconds allows closed-form integration.
            // When lifetime is 0, both t and invLife become zero, so position and velocity fall back
            // exactly to the pre-expansion result.
            // WGSL has no ternary operator, and select() evaluates both branches, so this is written
            // as a statement, which also keeps taking the reciprocal of 0 out of every evaluation path.
            let age=fract(p0.x+h2);
            var invLife=0.0;
            if(p3.z>0.0) { invLife=1.0/p3.z; }
            let t=age*p3.z;
            let accel=p4.xyz;
            // frameData[11] is the HLSL Load4(176): disk radius / radial speed / sway amount / curve-mode flag.
            let uCurve=frameData.values[11];
            // frameData[16..18] is the HLSL Load4(256/272/288): initial velocity and cone jitter / spawn
            // axis and half length and half depth / speed jitter and floor and toon edge (the compaction
            // stage uses only the first two).
            let uLaunch=frameData.values[16];
            let uRect=frameData.values[17];
            let uExtra=frameData.values[18];

            var center=vec3f(0.0);
            var velocity=vec3f(0.0);
            if((u32(p5.w)&16u)!=0u) {
                // Disk spawn: the radius takes sqrt(h0) to make the area uniform, otherwise particles
                // bunch up into a clump at the center.
                let ring=h1*6.283185;
                let radial=vec2f(cos(ring),sin(ring));
                let radius=sqrt(h0)*uCurve.x;
                center=vec3f(p1.x+radial.x*radius,p1.y,p1.z+radial.y*radius);
                // Disk spawn has no drop: vertical motion comes entirely from acceleration and wind,
                // so the rise speed of a flame is decided by AccelY alone.
            } else if((u32(p5.w)&32u)!=0u) {
                // Rect spawn: take a uniform segment along the spawn axis and a uniform layer
                // perpendicular, giving the whole strip equal density, unlike the disk which needs a
                // square root to correct the area. h4..h6 are computed only here, so the default path
                // does not spend three extra hashes on them.
                let h4=ParticleHash(index*23u+59u);
                let h5=ParticleHash(index*29u+71u);
                let h6=ParticleHash(index*31u+83u);
                let axis=uRect.xy;
                let across=vec2f(-axis.y,axis.x);
                let along=(h0*2.0-1.0)*uRect.z;
                let depth=(h1*2.0-1.0)*uRect.w;
                center=vec3f(p1.x+axis.x*along+across.x*depth,p1.y,p1.z+axis.y*along+across.y*depth);
                // Initial velocity is shared by the whole strip; the cone jitter offsets the 3D
                // direction in proportion to |initial velocity|, and the speed jitter then scales the
                // magnitude as a whole.
                velocity=(uLaunch.xyz+(vec3f(h4,h5,h6)*2.0-1.0)*uLaunch.w*length(uLaunch.xyz))
                    *(1.0+(h6-0.5)*uExtra.x);
                // Position integrates the initial velocity: each particle on the strip travels along
                // its own initial velocity, and acceleration and wind are then added on top as usual.
                center+=velocity*t;
            } else {
                center=vec3f(
                    p1.x+(h0*2.0-1.0)*p0.y+sin((p0.x+h0)*6.283185)*p3.y,
                    p1.y+p0.z*(0.7-age),
                    p1.z+(h1*2.0-1.0)*p0.y+cos((p0.x+h1)*6.283185)*p3.y);
                // The base velocity is the time derivative of the three lines above: the phase rate is
                // 1/life, so each term carries an invLife. In the default case p0.z*invLife equals
                // FallSpeed, since life is already boxHeight / FallSpeed.
                velocity=vec3f(
                    cos((p0.x+h0)*6.283185)*6.283185*p3.y*invLife,
                    -p0.z*invLife,
                    -sin((p0.x+h1)*6.283185)*6.283185*p3.y*invLife);
            }
            // The acceleration displacement must be folded in before building clip, otherwise the position used for the cull test and the final rasterized position are not the same point.
            center+=0.5*accel*t*t;
            velocity+=accel*t;
            center+=p5.xyz*t;
            velocity+=p5.xyz;
            if((u32(p5.w)&16u)!=0u) {
                let ring=h1*6.283185;
                let spread=vec2f(cos(ring),sin(ring))*uCurve.y;
                // Radial spread is proportional to age: the column therefore widens as it rises; fire uses a negative Spread to draw the flame tongues toward the axis.
                center=vec3f(center.x+spread.x*t,center.y,center.z+spread.y*t);
                velocity=vec3f(velocity.x+spread.x,velocity.y,velocity.z+spread.y);
                // Two sines at different frequencies compose a low-frequency sway that is still a closed-form function of age, introducing no cross-frame state.
                let wobble=vec2f(sin(t*1.3+h2*6.283185),cos(t*1.1+h3*6.283185))*uCurve.z*t;
                center=vec3f(center.x+wobble.x,center.y,center.z+wobble.y);
            }
            if((u32(p5.w)&4u)!=0u) {
                let period=2.0*p0.y;
                let wrapped=center.xz-floor((center.xz-frameData.values[6].xz+p0.y)/period)*period;
                center=vec3f(wrapped.x,center.y,wrapped.y);
            }
            let channel=u32(p5.w)&3u;
            var weight=1.0;
            if(channel!=0u) {
                weight=0.0;
                var remaining=1.0;
                for(var r=0u;r<u32(p4.w);r++) {
                    let a=regionData.values[r*3u];
                    let b=regionData.values[r*3u+1u];
                    let c=regionData.values[r*3u+2u];
                    let d=center.xz-a.xy;
                    let local=vec2f(b.x*d.x+b.y*d.y,-b.y*d.x+b.x*d.y)*a.zw;
                    var m=clamp((1.0-length(local))/b.z,0.0,1.0);
                    m=m*m*(3.0-2.0*m);
                    if(center.y<c.x || center.y>c.y) { m=0.0; }
                    weight+=remaining*m*select(c.w,c.z,channel==1u);
                    remaining*=1.0-m;
                }
            }

            // The periodic-window relocation happens at the invisible edge; intensity changes only fade in new candidates rather than resetting the whole field.
            let viewDistance=frameData.values[7].w;
            if(viewDistance>0.0) {
                let f=clamp((1.0-length(center-frameData.values[6].xyz)/viewDistance)*4.0,0.0,1.0);
                weight*=f*f*(3.0-2.0*f);
            }
            // The soft-edge width is 2% of the admitted prefix (at least one slot): as the density ramps,
            // newly admitted particles start at zero coverage instead of appearing all at once. The prior
            // version took its edge from h3 * 0.02, which is the same order of width.
            if((u32(p5.w)&8u)!=0u) { weight*=clamp((cutoff-ordinal)/max(cutoff*0.02,1.0),0.0,1.0); }
            // Die on floor: if the integrated position is already below the floor, this particle is not
            // emitted, instead of letting it pass below the ground. It must come after the weight
            // computation: region sampling resets weight, so placing it earlier would be overwritten.
            if((u32(p5.w)&64u)!=0u && center.y<uExtra.y) { weight=0.0; }

            // frameData[0..3] are the HLSL Load4(0/16/32/48): the four rows of the view-projection.
            let viewProjection=mat4x4f(frameData.values[0],frameData.values[1],frameData.values[2],frameData.values[3]);
            let clip=viewProjection*vec4f(center,1.0);
            // Use w plus the billboard radius as a lateral margin, so edge-hugging particles are not discarded wholesale once expanded into a quad.
            let margin=clip.w+p1.w*4.0;
            let streak=abs(viewProjection*vec4f(velocity*frameData.values[7].z*0.5,0.0));
            if(weight>0.0 && clip.w+streak.w>1e-4 && clip.z+streak.z>-1e-4
                && abs(clip.x)<margin+streak.x+streak.w && abs(clip.y)<margin+streak.y+streak.w) {
                // Each thread adds at most one and the thread count equals capacity, so slot is guaranteed to be less than capacity and needs no clamping.
                let slot=atomicAdd(&args.values[1],1u);
                // The 48-byte instance stride is three vec4f: HLSL offset+0, +16 and +32.
                instanceData.values[slot*3u]=vec4f(center,p1.w);
                // In curve mode rgb instead stores two stable random numbers (hashed by index, immune to
                // instance-slot contention); the vertex stage uses them to pick the colour gradient and
                // the initial rotation. Coverage still travels only through alpha, exactly as before.
                let tint=select(p2.xyz,vec3f(h0,h1,0.0),uCurve.w>0.5);
                instanceData.values[slot*3u+1u]=vec4f(tint,p2.w*weight);
                instanceData.values[slot*3u+2u]=vec4f(velocity,age);
            }
        }
    }
}
""";

    // ── Draw-side shaders, consumed by WGPUParticleDrawPipeline ──
    // No vertex buffer and no vertex attributes, matching the HLSL: the quad corner comes from
    // vertex_index over a unit-quad index buffer and every per-instance value is loaded by
    // instance_index out of a read-only storage buffer bound to the vertex stage.
    //
    // instance_index includes firstInstance, unlike SV_InstanceID. The IndirectDrawIndexedArgs
    // contract pins firstInstance to zero precisely because this backend cannot express anything else
    // without an optional feature, so all four families agree on the slot number.
    //
    // Vertex and fragment are separate modules rather than one, because the fragment side has two
    // variants and WGSL has no preprocessor: the single-target and velocity-target entry points would
    // otherwise both have to live in a module whose attachment count is fixed at pipeline bake time.

    const string DrawCommonWgsl = """
struct ParticleVertex {
    @builtin(position) position: vec4f,
    @location(0) offset: vec2f,
    @location(1) color: vec4f,
    // (stable random, normalized age, noise strength, additive-blend flag). The fragment stage cannot
    // read the bind groups visible to the vertex stage, so curve-mode appearance parameters arrive via
    // the interpolator instead of reading the frame buffer again.
    @location(2) misc: vec4f,
    // (toon edge width, reserved, reserved, reserved). Same as above: it can only reach the fragment via the interpolator.
    @location(3) misc2: vec4f,
}

""";

    /// <summary>WebGPU vertex stage for the indirect billboard draw.</summary>
    public const string DrawVertexWgsl = DrawCommonWgsl + """
struct Instances { values: array<vec4f> }
struct Frame { values: array<vec4f> }
@group(0) @binding(0) var<storage,read> instanceData: Instances;
@group(0) @binding(1) var<storage,read> frameData: Frame;
@vertex fn VSMain(@builtin(vertex_index) vertexId: u32,@builtin(instance_index) slot: u32) -> ParticleVertex {
    let packed=instanceData.values[slot*3u];
    let center=packed.xyz;
    var size=packed.w;
    var color=instanceData.values[slot*3u+1u];
    let motion=instanceData.values[slot*3u+2u];
    let velocity=motion.xyz;
    let age=motion.w;

    let corner=vec2f(select(-1.0,1.0,(vertexId&1u)!=0u),select(1.0,-1.0,(vertexId&2u)!=0u));
    // frameData[4] and [5] are the HLSL Load4(64) and Load4(80): camera right and up.
    let right=frameData.values[4].xyz;
    let up=frameData.values[5].xyz;
    let eye=frameData.values[6].xyz;
    // frameData[7] is the HLSL Load4(112): fadeIn / fadeOut (fractions of the lifetime) / stretch in seconds.
    let drawParams=frameData.values[7].xyz;
    // frameData[8..11] are the appearance curve and spawn shape: when curve mode is off the flag is 0,
    // so the branch below does not fire and size, color and corner are bit-identical to before the
    // extension.
    let tintStart=frameData.values[8];
    let tintMid=frameData.values[9];
    let tintEnd=frameData.values[10];
    let curve=frameData.values[11];
    // frameData[18] = speed jitter / floor height / toon edge / reserved: only the edge width travels to the fragment.
    let uExtra=frameData.values[18];
    let viewProjection=mat4x4f(frameData.values[0],frameData.values[1],frameData.values[2],frameData.values[3]);
    var seed=0.0;
    var shape=corner;
    if(curve.w>0.5) {
        // The instance rgb holds the two stable random numbers written by the compaction stage: hashed by particle index, immune to instance-slot contention.
        seed=color.r;
        // Two-segment linear gradient: first half start->mid, second half mid->end. WGSL's select
        // evaluates both operands, so this is written out with if/else instead.
        var tint=vec3f(0.0);
        if(age<0.5) { tint=mix(tintStart.rgb,tintMid.rgb,age*2.0); }
        else { tint=mix(tintMid.rgb,tintEnd.rgb,age*2.0-1.0); }
        color=vec4f(tint,color.a);
        size=mix(size,tintEnd.w,age);
        // A per-particle initial rotation plus a slow spin: rows of in-phase blobs are the most obvious giveaway of cheap particles.
        let spin=seed*6.283185+age*(seed-0.5)*3.0;
        let spinCos=cos(spin);
        let spinSin=sin(spin);
        shape=vec2f(corner.x*spinCos-corner.y*spinSin,corner.x*spinSin+corner.y*spinCos);
    }
    // Statement form rather than select(): when fadeIn is 0 age is often 0 too, and select would
    // compute 0/0 first, leaving clamp(NaN) implementation-defined; with if that division is never
    // evaluated at all.
    var fadeIn=1.0;
    if(drawParams.x>0.0) { fadeIn=clamp(age/drawParams.x,0.0,1.0); }
    var fadeOut=1.0;
    if(drawParams.y>0.0) { fadeOut=clamp((1.0-age)/drawParams.y,0.0,1.0); }
    color.a*=fadeIn*fadeOut;
    // Shear the quad into a parallelogram along the velocity direction, with total length
    // |velocity| * stretch; the top and bottom corners each offset by half that length, so at
    // stretch = 0 it degenerates exactly back to the original square billboard.
    var world=center+(right*shape.x+up*shape.y)*size+velocity*(drawParams.z*shape.y*0.5);
    var uv=corner;

    // ── ribbon strip: "segmented polylines" like lightning channels do not use billboards ──
    // frameData[12] = (viewport width, viewport height, minimum half-width in pixels, ribbon flag),
    // frameData[13] = (intensity, fog extinction, reserved, reserved). When the flag is 0 this whole
    // section is skipped.
    let ribbon=frameData.values[12];
    if(ribbon.w>0.5) {
        // In this mode the instance record changes meaning: Position is the segment midpoint and
        // Velocity is the half-segment vector, so "midpoint ± half segment" gives the two endpoints
        // without storing an extra instance per segment.
        let segLen=length(velocity);
        var tangent=vec3f(0.0,1.0,0.0);
        if(segLen>1e-5) { tangent=velocity/segLen; }
        // Camera-facing strip: the lateral direction is the cross product of the tangent and the view
        // ray. When facing the camera head-on that cross product degenerates (the strip is just a point
        // anyway), so falling back to the camera right axis is fine; direction is irrelevant then.
        let lateral=cross(tangent,eye-center);
        let sideLen=length(lateral);
        var side=right;
        if(sideLen>1e-5) { side=lateral/sideLen; }

        // Minimum screen width: project the world-unit side into pixel space to get "how many pixels one
        // metre laterally spans", then invert it to find the world half-width needed for the minimum
        // pixel width. When a polyline is thinner than a pixel, rasterization flickers on and off -- that
        // is a loss where the fragment is simply never produced, and no temporal compensation can recover
        // it. TAA's reactive rect (ViewTargets.ReactiveRect) only keeps already-drawn pixels from being
        // dragged by history; it cannot fill in a fragment that never existed.
        let clipSide=viewProjection*vec4f(side,0.0);
        let clipW=max(abs(clipSide.w),1e-3);
        let pxPerWorld=max(length(abs(clipSide.xy)/clipW*ribbon.xy*0.5),1e-4);
        let halfWidth=max(size,ribbon.z/pxPerWorld);

        // Extend half a band width along the tangent at each end: adjacent segments then overlap at the bend instead of leaving a seam.
        world=center+tangent*(corner.y*(segLen+halfWidth))+side*(corner.x*halfWidth);
        // The lateral falloff reuses the 1-dot(uv,uv) from the fragment: the longitudinal axis does not participate, so the strip stays a line of uniform brightness.
        uv=vec2f(corner.x,0.0);

        // Both intensity and fog attenuation only scale coverage: the fragment multiplies rgb into alpha, so applying them to rgb would square the result.
        let trim=frameData.values[13];
        color.a*=trim.x*exp(-trim.y*length(center-eye));
    }

    // No Y flip: WebGPU clip space matches D3D, so this is the HLSL line verbatim.
    var result: ParticleVertex;
    result.position=viewProjection*vec4f(world,1.0);
    // offset keeps the unrotated corner: the falloff only takes its sum of squares, rotation does not change the result, so the default path is untouched.
    result.offset=uv;
    result.color=color;
    // (stable random, normalized age, noise strength, additive-blend flag)
    result.misc=vec4f(seed,age,select(0.0,tintMid.w,curve.w>0.5),tintStart.w);
    result.misc2=vec4f(uExtra.z,0.0,0.0,0.0);
    return result;
}

""";

    const string ShadeWgsl = """
// Sine-free hash: sin loses precision for large arguments on some hardware, and the input here grows monotonically with age.
fn Hash2(p: vec2f) -> f32 {
    var p3=fract(p.xyx*0.1031);
    p3+=dot(p3,p3.yzx+33.33);
    return fract((p3.x+p3.y)*p3.z);
}
fn ValueNoise(p: vec2f) -> f32 {
    let i=floor(p);
    let d=p-i;
    let f=d*d*(3.0-2.0*d);
    return mix(mix(Hash2(i),Hash2(i+vec2f(1.0,0.0)),f.x),
        mix(Hash2(i+vec2f(0.0,1.0)),Hash2(i+vec2f(1.0,1.0)),f.x),f.y);
}
fn Shade(input: ParticleVertex) -> vec4f {
    let falloff=clamp(1.0-dot(input.offset,input.offset),0.0,1.0);
    // The default path is unchanged: with curve mode off misc.z is 0, so this line is bit-identical to before.
    var alpha=input.color.a*falloff*falloff;
    if(input.misc.z>0.0) {
        // The noise scrolls along -y with age, so flame tongues and smoke curls travel upward in place rather than staying stuck.
        let q=input.offset*1.7+input.misc.x*31.0+vec2f(0.0,-input.misc.y*2.5);
        let n=0.65*ValueNoise(q)+0.35*ValueNoise(q*2.3);
        alpha=input.color.a*clamp(falloff*falloff*(1.0+input.misc.z*(n*2.0-1.0)),0.0,1.0);
    }
    // Toon edge: compress the smooth radial falloff into a crisp boundary, with the width as a fraction
    // of the billboard radius. 0 means off and the whole section is skipped, so every pixel of the
    // default path is bit-identical to before the extension.
    let edge=input.misc2.x;
    if(edge>0.0) {
        // coverage is "how much coverage this pixel originally had"; dividing by the peak coverage
        // restores it to [0,1] before the hard cut. The denominator is clamped to 1e-5 only to guard
        // against divide-by-zero: in the additive mode alpha is 0 anyway and stays 0 after the cut.
        let coverage=clamp(alpha/max(input.color.a,1e-5),0.0,1.0);
        alpha=input.color.a*smoothstep(0.5-edge,0.5+edge,coverage);
    }
    // The additive mode drives alpha to 0: under One/InvSrcAlpha this equals pure addition, so no extra PSO is needed.
    return vec4f(input.color.rgb*alpha,alpha*(1.0-input.misc.w));
}
""";

    /// <summary>WebGPU fragment stage for a colour-only scene pass.</summary>
    public const string DrawFragmentWgsl = DrawCommonWgsl + ShadeWgsl + """

@fragment fn PSMain(input: ParticleVertex) -> @location(0) vec4f { return Shade(input); }
""";

    /// <summary>WebGPU fragment stage for the velocity-attachment variant of the scene pass.</summary>
    public const string DrawFragmentVelocityWgsl = DrawCommonWgsl + ShadeWgsl + """

struct ParticleTargets {
    @location(0) color: vec4f,
    @location(1) velocity: vec2f,
}
@fragment fn PSMain(input: ParticleVertex) -> ParticleTargets {
    // Slot 1 is write-masked off in the velocity pipeline variant, matching how the main pipeline
    // keeps opaque velocity underneath transparent geometry. The value here is never stored.
    var result: ParticleTargets;
    result.color=Shade(input);
    result.velocity=vec2f(0.0,0.0);
    return result;
}
""";
}
