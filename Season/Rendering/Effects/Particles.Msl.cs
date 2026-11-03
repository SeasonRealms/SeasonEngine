// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

/// <summary>
/// Metal MSL mirror of the HLSL sources, compiled to IMTLLibrary at runtime by the backend.
///
/// Three translation rules govern every line here.
///
/// Indexing: HLSL ByteAddressBuffer addresses bytes, MSL pointers address elements. Every offset
/// below is the HLSL byte offset divided by the element stride, written out so the files can be
/// diffed by eye. Getting this wrong produces no diagnostic on any backend, only displaced data.
///
/// Matrices: the frame buffer holds the four *rows* of the view-projection. HLSL float4x4(r0..r3)
/// builds rows and is consumed as mul(v, M); MSL float4x4(r0..r3) builds *columns* and is consumed
/// as M * v. Both evaluate to the same sum over v[i] * r[i], so no transpose is needed -- the same
/// cancellation already documented for the GLSL mirror.
///
/// Slots: Metal has no descriptor sets. Compute bindings land where Graphics.TryDispatchCompute puts
/// them -- Params at buffer(0) through SetBytes, then every buffer binding in declaration order
/// starting at buffer(1) -- which makes the numbers identical to the GLSL std430 bindings. The draw
/// side is bound by MTLParticleDrawPipeline, vertex buffer(0) = instances and buffer(1) = frame.
/// </summary>
public sealed partial class ParticleSystemEffect
{
    /// <summary>Metal compute: rewrites the five indirect arguments every frame. indexCount / instanceCount / startIndex / baseVertex come from Params; startInstance stays 0.</summary>
    const string ResetMsl = """
#include <metal_stdlib>
using namespace metal;
kernel void CSMain(constant float4& resetParams [[buffer(0)]],device uint* args [[buffer(1)]],
    uint3 id [[thread_position_in_grid]]) {
    if(id.x==0u) {
        // IndirectDrawIndexedArgs: indexCount / instanceCount / startIndex / baseVertex / startInstance.
        // uint indices 0..4 are the HLSL byte offsets 0/4/8/12/16 divided by the 4-byte stride.
        // startInstance stays 0 by contract so [[instance_id]] equals the compacted slot.
        // The particle path passes 0 for the last three, so the output is bit-identical to before the
        // parameterization; the lightning channel passes a fixed segment count.
        args[0]=uint(resetParams.x);
        args[1]=uint(resetParams.y);
        args[2]=uint(resetParams.z);
        args[3]=uint(resetParams.w);
        args[4]=0u;
    }
}
""";

    /// <summary>Metal compute: derives one particle per thread, culls it, and appends survivors while raising the indirect instance count.</summary>
    const string CompactMsl = """
#include <metal_stdlib>
using namespace metal;
float ParticleHash(uint n) {
    n=(n<<13u)^n;
    n=n*(n*n*15731u+789221u)+1376312589u;
    return float(n&0x00FFFFFFu)/16777216.0;
}
// Shares the 96B parameter block with HLSL, six float4 in total:
// p[0]=phase/box radius/box height/budget, p[1]=origin.xyz/particle size, p[2]=color.
// p[3]=density/sway/lifetime/seed, p[4]=acceleration.xyz/region count, p[5]=wind.xyz/channel and window flags.
// args is declared atomic because index 1 is the compaction counter; the reset kernel owns the
// plain-uint view of the same buffer and the two never run concurrently.
kernel void CSMain(constant float4* p [[buffer(0)]],const device float4* frameData [[buffer(1)]],
    device float4* instanceData [[buffer(2)]],device atomic_uint* args [[buffer(3)]],
    const device float4* regionData [[buffer(4)]],
    uint3 id [[thread_position_in_grid]]) {
    // p3.w=seed, p4.w=region count, p5=wind.xyz/channel and window flags; the parameter block is 96B.
    float4 p0=p[0],p1=p[1],p2=p[2],p3=p[3],p4=p[4],p5=p[5];
    uint index=id.x;
    if(index<uint(p0.w)) {
        // Density gating is index-ordered, not hash-based: what is admitted is the prefix
        // [0, density x budget), so the CPU only needs to dispatch that many indices and clear
        // weather or light snow need not spin at full budget.
        // The hash is still computed because the index is shifted by the seed -- that shift decides
        // the spatial distribution of this batch of indices.
        float ordinal=float(index);
        float cutoff=p3.x*p0.w;
        index+=uint(p3.w);
        float h0=ParticleHash(index*3u+1u);
        float h1=ParticleHash(index*5u+7u);
        float h2=ParticleHash(index*11u+23u);
        float h3=ParticleHash(index*17u+41u);
        if(ordinal<cutoff) {
            // The phase is timed by lifetime, so the former fall is at once the normalized age in
            // [0,1); converting it back to seconds allows closed-form integration.
            // When lifetime is 0, both t and invLife become zero, so position and velocity fall back
            // exactly to the pre-expansion result.
            float age=fract(p0.x+h2);
            float invLife=p3.z>0.0?1.0/p3.z:0.0;
            float t=age*p3.z;
            float3 accel=p4.xyz;
            // frameData[11] is the HLSL Load4(176): disk radius / radial speed / sway amount / curve-mode flag.
            float4 uCurve=frameData[11];
            // frameData[16..18] is the HLSL Load4(256/272/288): initial velocity and cone jitter / spawn
            // axis and half length and half depth / speed jitter and floor and toon edge (the compaction
            // stage uses only the first two).
            float4 uLaunch=frameData[16];
            float4 uRect=frameData[17];
            float4 uExtra=frameData[18];

            float3 center;
            float3 velocity;
            if((uint(p5.w)&16u)!=0u) {
                // Disk spawn: the radius takes sqrt(h0) to make the area uniform, otherwise particles
                // bunch up into a clump at the center.
                float ring=h1*6.283185;
                float2 radial=float2(cos(ring),sin(ring));
                float radius=sqrt(h0)*uCurve.x;
                center=float3(p1.x+radial.x*radius,p1.y,p1.z+radial.y*radius);
                // Disk spawn has no drop: vertical motion comes entirely from acceleration and wind,
                // so the rise speed of a flame is decided by AccelY alone.
                velocity=float3(0.0);
            } else if((uint(p5.w)&32u)!=0u) {
                // Rect spawn: take a uniform segment along the spawn axis and a uniform layer
                // perpendicular, giving the whole strip equal density, unlike the disk which needs a
                // square root to correct the area. h4..h6 are computed only here, so the default path
                // does not spend three extra hashes on them.
                float h4=ParticleHash(index*23u+59u);
                float h5=ParticleHash(index*29u+71u);
                float h6=ParticleHash(index*31u+83u);
                float2 axis=uRect.xy;
                float2 across=float2(-axis.y,axis.x);
                float along=(h0*2.0-1.0)*uRect.z;
                float depth=(h1*2.0-1.0)*uRect.w;
                center=float3(p1.x+axis.x*along+across.x*depth,p1.y,p1.z+axis.y*along+across.y*depth);
                // Initial velocity is shared by the whole strip; the cone jitter offsets the 3D
                // direction in proportion to |initial velocity|, and the speed jitter then scales the
                // magnitude as a whole.
                velocity=(uLaunch.xyz+(float3(h4,h5,h6)*2.0-1.0)*uLaunch.w*length(uLaunch.xyz))
                    *(1.0+(h6-0.5)*uExtra.x);
                // Position integrates the initial velocity: each particle on the strip travels along
                // its own initial velocity, and acceleration and wind are then added on top as usual.
                center+=velocity*t;
            } else {
                center.x=p1.x+(h0*2.0-1.0)*p0.y+sin((p0.x+h0)*6.283185)*p3.y;
                center.y=p1.y+p0.z*(0.7-age);
                center.z=p1.z+(h1*2.0-1.0)*p0.y+cos((p0.x+h1)*6.283185)*p3.y;
                // The base velocity is the time derivative of the three lines above: the phase rate is
                // exactly 1/life, so every term carries an invLife.
                // p0.z*invLife happens to equal FallSpeed at default settings, because life is itself
                // boxHeight / FallSpeed.
                velocity.x=cos((p0.x+h0)*6.283185)*6.283185*p3.y*invLife;
                velocity.y=-p0.z*invLife;
                velocity.z=-sin((p0.x+h1)*6.283185)*6.283185*p3.y*invLife;
            }
            // The acceleration displacement must be folded in before the clip is constructed,
            // otherwise the position used for culling and the position finally rasterized are not the
            // same point.
            center+=0.5*accel*t*t;
            velocity+=accel*t;
            center+=p5.xyz*t;
            velocity+=p5.xyz;
            if((uint(p5.w)&16u)!=0u) {
                float ring=h1*6.283185;
                float2 radial=float2(cos(ring),sin(ring));
                // Radial spread is proportional to age: the smoke column therefore widens as it rises;
                // flames use a negative Spread to pull the flame tongue in toward the axis.
                center.xz+=radial*uCurve.y*t;
                velocity.xz+=radial*uCurve.y;
                // Two sines of different frequencies combine into a low-frequency sway, still a
                // closed-form function of age and introducing no cross-frame state.
                center.xz+=float2(sin(t*1.3+h2*6.283185),cos(t*1.1+h3*6.283185))*uCurve.z*t;
            }
            if((uint(p5.w)&4u)!=0u) {
                float period=2.0*p0.y;
                center.xz-=floor((center.xz-frameData[6].xz+p0.y)/period)*period;
            }
            uint channel=uint(p5.w)&3u;
            float weight=1.0;
            if(channel!=0u) {
                weight=0.0;
                float remaining=1.0;
                for(uint r=0u;r<uint(p4.w);r++) {
                    float4 a=regionData[r*3u],b=regionData[r*3u+1u],c=regionData[r*3u+2u];
                    float2 d=center.xz-a.xy;
                    float2 local=float2(b.x*d.x+b.y*d.y,-b.y*d.x+b.x*d.y)*a.zw;
                    float m=clamp((1.0-length(local))/b.z,0.0,1.0);
                    m=m*m*(3.0-2.0*m);
                    if(center.y<c.x || center.y>c.y) m=0.0;
                    weight+=remaining*m*(channel==1u?c.z:c.w);
                    remaining*=1.0-m;
                }
            }

            // The wrapping of the periodic window happens at the invisible edge; an intensity change
            // only fades in new candidates rather than resetting the whole field.
            float viewDistance=frameData[7].w;
            if(viewDistance>0.0) {
                float f=clamp((1.0-length(center-frameData[6].xyz)/viewDistance)*4.0,0.0,1.0);
                weight*=f*f*(3.0-2.0*f);
            }
            // The soft edge width is 2% of the admitted prefix (at least one index): as density ramps,
            // newly admitted particles start from zero coverage instead of the whole batch appearing at
            // once. The old version took the edge from h3's 0.02, the same order of width.
            if((uint(p5.w)&8u)!=0u) weight*=clamp((cutoff-ordinal)/max(cutoff*0.02,1.0),0.0,1.0);
            // Die on floor: if the integrated position is already below the floor, this particle is
            // not emitted, rather than letting it sink through the ground.
            // This must come after the weight computation: the region sampling resets weight, and
            // placing it before would have it overwritten entirely.
            if((uint(p5.w)&64u)!=0u && center.y<uExtra.y) weight=0.0;

            // frameData[0..3] are the HLSL Load4(0/16/32/48): the four rows of the view-projection.
            float4x4 viewProjection=float4x4(frameData[0],frameData[1],frameData[2],frameData[3]);
            float4 clip=viewProjection*float4(center,1.0);
            // Add the billboard radius to w as a horizontal margin, so edge-hugging particles are not
            // discarded wholesale once expanded into a quad.
            float margin=clip.w+p1.w*4.0;
            float4 streak=abs(viewProjection*float4(velocity*frameData[7].z*0.5,0.0));
            if(weight>0.0 && clip.w+streak.w>1e-4 && clip.z+streak.z>-1e-4
                && abs(clip.x)<margin+streak.x+streak.w && abs(clip.y)<margin+streak.y+streak.w) {
                // Each thread adds at most one, and the thread count is the capacity, so slot is
                // necessarily below the capacity and needs no further clamping.
                uint slot=atomic_fetch_add_explicit(&args[1],1u,memory_order_relaxed);
                // The 48-byte instance stride is three float4: HLSL offset+0, +16 and +32.
                instanceData[slot*3u]=float4(center,p1.w);
                // In curve mode rgb instead stores two stable random numbers (hashed from the index,
                // unaffected by contention for instance slots), from which the vertex stage picks the
                // color gradient and the initial rotation; coverage still only goes through alpha,
                // exactly as before.
                float3 tint=uCurve.w>0.5?float3(h0,h1,0.0):p2.xyz;
                instanceData[slot*3u+1u]=float4(tint,p2.w*weight);
                instanceData[slot*3u+2u]=float4(velocity,age);
            }
        }
    }
}
""";

    // ── Draw-side shaders, consumed by MTLParticleDrawPipeline ──
    // No vertex descriptor and no [[stage_in]] on the vertex stage, matching the HLSL: the quad corner
    // comes from [[vertex_id]] over a unit-quad index buffer and every per-instance value is loaded by
    // [[instance_id]].
    //
    // [[instance_id]] excludes baseInstance, exactly like SV_InstanceID and unlike gl_InstanceIndex.
    // The IndirectDrawIndexedArgs contract pins baseInstance to zero anyway -- WebGPU cannot express a
    // non-zero one without an optional feature -- so all three families agree on the slot number.

    const string DrawCommonMsl = """
#include <metal_stdlib>
using namespace metal;
struct ParticleVertex {
    float4 position [[position]];
    float2 offset;
    float4 color;
    // (stable random number, normalized age, noise amount, additive blend flag). The curve-mode
    // appearance parameters are used only in the fragment stage, but WebGPU's bind group is visible
    // only to the vertex stage, so these values travel through interpolators rather than reading the
    // frame buffer again.
    float4 misc;
    // (toon edge width, reserved, reserved, reserved). Same as above, it can only reach the fragment
    // via interpolators.
    float4 misc2;
};

""";

    /// <summary>Metal vertex stage for the indirect billboard draw.</summary>
    public const string DrawVertexMsl = DrawCommonMsl + """
vertex ParticleVertex VSMain(uint vertexId [[vertex_id]],uint slot [[instance_id]],
    const device float4* instanceData [[buffer(0)]],const device float4* frameData [[buffer(1)]]) {
    float4 packed=instanceData[slot*3u];
    float3 center=packed.xyz;
    float size=packed.w;
    float4 color=instanceData[slot*3u+1u];
    float4 motion=instanceData[slot*3u+2u];
    float3 velocity=motion.xyz;
    float age=motion.w;

    float2 corner=float2((vertexId&1u)!=0u?1.0:-1.0,(vertexId&2u)!=0u?-1.0:1.0);
    // frameData[4] and [5] are the HLSL Load4(64) and Load4(80): camera right and up.
    float3 right=frameData[4].xyz;
    float3 up=frameData[5].xyz;
    float3 eye=frameData[6].xyz;
    // frameData[7] is the HLSL Load4(112): fadeIn / fadeOut (fractions of the lifetime) / stretch in seconds.
    float3 drawParams=frameData[7].xyz;
    // frameData[8..11] are the appearance curve and spawn shape: with curve mode off the flag is 0,
    // this branch does not hold, and size, color and corner are bit-identical to before the expansion.
    float4 tintStart=frameData[8];
    float4 tintMid=frameData[9];
    float4 tintEnd=frameData[10];
    float4 curve=frameData[11];
    // frameData[18] = speed jitter / floor height / toon edge / reserved: only the edge width needs to
    // reach the fragment.
    float4 uExtra=frameData[18];
    float4x4 viewProjection=float4x4(frameData[0],frameData[1],frameData[2],frameData[3]);
    float seed=0.0;
    float2 shape=corner;
    if(curve.w>0.5) {
        // The instance rgb is the two stable random numbers written by the compaction stage: hashed
        // from the particle index, unaffected by contention for instance slots.
        seed=color.r;
        // Two linear gradient segments: the first half goes start -> mid, the second half mid -> end.
        color.rgb=age<0.5?mix(tintStart.rgb,tintMid.rgb,age*2.0):mix(tintMid.rgb,tintEnd.rgb,age*2.0-1.0);
        size=mix(size,tintEnd.w,age);
        // Each particle gets its own initial rotation and slowly spins: rows of in-phase blobs are the
        // most obvious tell of cheap particles.
        float spin=seed*6.283185+age*(seed-0.5)*3.0;
        float spinCos=cos(spin);
        float spinSin=sin(spin);
        shape=float2(corner.x*spinCos-corner.y*spinSin,corner.x*spinSin+corner.y*spinCos);
    }
    // Must be written as a ternary, not as a division by an epsilon: when fadeIn is 0, age is also
    // often 0, and clamp(0/eps) gives 0 instead of 1, eating the whole field's coverage.
    float fadeIn=drawParams.x>0.0?clamp(age/drawParams.x,0.0,1.0):1.0;
    float fadeOut=drawParams.y>0.0?clamp((1.0-age)/drawParams.y,0.0,1.0):1.0;
    color.a*=fadeIn*fadeOut;
    // Shear the quad into a parallelogram along the velocity direction, with total length
    // |velocity| * stretch; the two corners each offset by half a length, so at stretch = 0 it
    // degenerates exactly back to the original square billboard.
    float3 world=center+(right*shape.x+up*shape.y)*size+velocity*(drawParams.z*shape.y*0.5);
    float2 uv=corner;

    // ── ribbon strip: segment polylines like the lightning channel do not use billboards ──
    // frameData[12] = (viewport width, viewport height, minimum half-width in pixels, ribbon flag),
    // frameData[13] = (intensity, fog extinction, reserved, reserved). When the flag is 0 the whole
    // block is skipped.
    float4 ribbon=frameData[12];
    if(ribbon.w>0.5) {
        // The instance record changes meaning in this mode: Position is the segment midpoint and
        // Velocity is the half-segment vector, so "midpoint +/- half segment" gives the two endpoints
        // and no extra instance per segment is needed.
        float segLen=length(velocity);
        float3 tangent=segLen>1e-5?velocity/segLen:float3(0.0,1.0,0.0);
        // A camera-facing strip: the lateral direction is the cross product of the tangent and the
        // view ray. When looking straight down the lens the cross product degenerates (the strip is
        // really just a point), so falling back to the camera right axis is fine, and the direction is
        // irrelevant in that case anyway.
        float3 side=cross(tangent,eye-center);
        float sideLen=length(side);
        side=sideLen>1e-5?side/sideLen:right;

        // Minimum screen width: project the world-space side into pixel space to get "how many pixels
        // one meter laterally equals", then invert it to find the world half-width needed to
        // guarantee the minimum pixel width. When a polyline is thinner than a pixel, rasterization
        // flickers in and out -- that is a fragment that was never generated at all, and no temporal
        // compensation can rescue it. TAA's reactive rect
        // (ViewTargets.ReactiveRect) only keeps already-drawn pixels from being held back by history;
        // it cannot fill in a fragment that does not exist.
        float4 clipSide=viewProjection*float4(side,0.0);
        float clipW=max(abs(clipSide.w),1e-3);
        float pxPerWorld=max(length(abs(clipSide.xy)/clipW*ribbon.xy*0.5),1e-4);
        float halfWidth=max(size,ribbon.z/pxPerWorld);

        // Extend half a strip width along the tangent at each end: adjacent segments therefore
        // overlap at the joints instead of leaving a gap.
        world=center+tangent*(corner.y*(segLen+halfWidth))+side*(corner.x*halfWidth);
        // The lateral falloff reuses the fragment's 1-dot(uv,uv): the longitudinal direction does not
        // participate, so the strip stays a line of uniform brightness.
        uv=float2(corner.x,0.0);

        // Both intensity and fog attenuation only scale coverage: the fragment multiplies rgb into
        // alpha, and applying them to rgb would square it.
        float4 trim=frameData[13];
        color.a*=trim.x*exp(-trim.y*length(center-eye));
    }

    // No Y flip: Metal clip space matches D3D, so this is the HLSL line verbatim.
    ParticleVertex result;
    result.position=viewProjection*float4(world,1.0);
    // offset keeps the unrotated corner: the falloff takes only its sum of squares, and rotation does
    // not change the result, so the default path stays untouched byte for byte.
    result.offset=uv;
    result.color=color;
    // (stable random number, normalized age, noise amount, additive blend flag)
    result.misc=float4(seed,age,curve.w>0.5?tintMid.w:0.0,tintStart.w);
    result.misc2=float4(uExtra.z,0.0,0.0,0.0);
    return result;
}

""";

    const string ShadeMsl = """
// Sine-free hash: sin loses precision for large arguments on some hardware, and the inputs here grow
// steadily with age.
float Hash2(float2 p) {
    float3 p3=fract(p.xyx*0.1031);
    p3+=dot(p3,p3.yzx+33.33);
    return fract((p3.x+p3.y)*p3.z);
}
float ValueNoise(float2 p) {
    float2 i=floor(p);
    float2 f=p-i;
    f=f*f*(3.0-2.0*f);
    return mix(mix(Hash2(i),Hash2(i+float2(1.0,0.0)),f.x),
        mix(Hash2(i+float2(0.0,1.0)),Hash2(i+float2(1.0,1.0)),f.x),f.y);
}
float4 Shade(ParticleVertex input) {
    float falloff=clamp(1.0-dot(input.offset,input.offset),0.0,1.0);
    // The default path stays as-is: with curve mode off misc.z is 0, so this line is bit-identical to
    // the old version.
    float alpha=input.color.a*falloff*falloff;
    if(input.misc.z>0.0) {
        // The noise scrolls along -y with age, so the flame tongue and smoke curl travel upward in
        // place instead of sticking motionless.
        float2 q=input.offset*1.7+input.misc.x*31.0+float2(0.0,-input.misc.y*2.5);
        float n=0.65*ValueNoise(q)+0.35*ValueNoise(q*2.3);
        alpha=input.color.a*clamp(falloff*falloff*(1.0+input.misc.z*(n*2.0-1.0)),0.0,1.0);
    }
    // Toon edge: compress the smooth radial falloff into a distinct boundary, with its width as a
    // fraction of the billboard radius.
    // 0 means off, the whole block is skipped, so every pixel of the default path is bit-identical to
    // before the expansion.
    float edge=input.misc2.x;
    if(edge>0.0) {
        // coverage is "how much coverage this pixel originally had"; the peak coverage is used as the
        // denominator to normalize back to [0,1] before cutting. The denominator is clamped to 1e-5
        // only to guard against division by zero: the additive mode's alpha is already 0, and it stays
        // 0 after the cut.
        float coverage=clamp(alpha/max(input.color.a,1e-5),0.0,1.0);
        alpha=input.color.a*smoothstep(0.5-edge,0.5+edge,coverage);
    }
    // The additive mode pushes alpha to 0: under One/InvSrcAlpha this equals pure addition, so no
    // extra PSO is needed.
    return float4(input.color.rgb*alpha,alpha*(1.0-input.misc.w));
}
""";

    /// <summary>Metal fragment stage for a colour-only scene pass.</summary>
    public const string DrawFragmentMsl = DrawCommonMsl + ShadeMsl + """

fragment float4 PSMain(ParticleVertex input [[stage_in]]) { return Shade(input); }
""";

    /// <summary>Metal fragment stage for the velocity-attachment variant of the scene pass.</summary>
    public const string DrawFragmentVelocityMsl = DrawCommonMsl + ShadeMsl + """

struct ParticleTargets {
    float4 color [[color(0)]];
    float2 velocity [[color(1)]];
};
fragment ParticleTargets PSMain(ParticleVertex input [[stage_in]]) {
    // Slot 1 is write-masked off in the velocity PSO variant, matching how the main pipeline keeps
    // opaque velocity underneath transparent geometry. The value here is never stored.
    ParticleTargets result;
    result.color=Shade(input);
    result.velocity=float2(0.0,0.0);
    return result;
}
""";
}
