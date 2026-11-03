// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

/// <summary>
/// Vulkan GLSL mirror of the HLSL sources, compiled to SPIR-V at runtime by the backend.
///
/// Two translation rules govern every line here and both have bitten this engine before.
///
/// Indexing: HLSL ByteAddressBuffer addresses bytes, GLSL std430 arrays address elements. Every
/// offset below is therefore the HLSL byte offset divided by the element stride, written out so the
/// two files can be diffed by eye. Getting this wrong produces no compiler diagnostic on any
/// backend, only silently displaced data.
///
/// Matrices: the frame buffer holds the four *rows* of the view-projection. HLSL float4x4(r0..r3)
/// builds rows and is consumed as mul(v, M); GLSL mat4(r0..r3) builds *columns* and is consumed as
/// M * v. Both evaluate to the same sum over v[i] * r[i], so the same four vectors in the same order
/// give identical results and no transpose is needed.
/// </summary>
public sealed partial class ParticleSystemEffect
{
    /// <summary>Vulkan compute: rewrites the five indirect arguments every frame. indexCount / instanceCount / startIndex / baseVertex come from Params; startInstance stays 0.</summary>
    const string ResetGlsl = """
#version 450
layout(push_constant) uniform Params { vec4 resetParams; };
layout(std430,binding=1) buffer Args { uint args[]; };
layout(local_size_x=1,local_size_y=1,local_size_z=1) in;
void main() {
    if(gl_GlobalInvocationID.x==0u) {
        // IndirectDrawIndexedArgs: indexCount / instanceCount / startIndex / baseVertex / startInstance.
        // uint[] indices 0..4 are the HLSL byte offsets 0/4/8/12/16 divided by the 4-byte stride.
        // startInstance stays 0 by contract so gl_InstanceIndex equals the compacted slot.
        // Particles pass 0 for the last three, so the output is bit-identical to before parameterization; the lightning channel passes a fixed segment count.
        args[0]=uint(resetParams.x);
        args[1]=uint(resetParams.y);
        args[2]=uint(resetParams.z);
        args[3]=uint(resetParams.w);
        args[4]=0u;
    }
}
""";

    /// <summary>Vulkan compute: derives one particle per thread, culls it, and appends survivors while raising the indirect instance count.</summary>
    const string CompactGlsl = """
#version 450
// Shares the 96B parameter block with the HLSL, six vec4 in all:
// p0 = phase / box radius / box height / budget, p1 = origin.xyz / particle size, p2 = colour.
// p3 = density / sway amplitude / lifetime / seed, p4 = acceleration.xyz / region count, p5 = wind.xyz / channel and window flags.
layout(push_constant) uniform Params { vec4 p0,p1,p2,p3,p4,p5; };
layout(std430,binding=1) readonly buffer Frame { vec4 frameData[]; };
layout(std430,binding=2) buffer Instances { vec4 instanceData[]; };
layout(std430,binding=3) buffer Args { uint args[]; };
layout(std430,binding=4) readonly buffer Regions { vec4 regionData[]; };
layout(local_size_x=64,local_size_y=1,local_size_z=1) in;
float Hash(uint n) {
    n=(n<<13u)^n;
    n=n*(n*n*15731u+789221u)+1376312589u;
    return float(n&0x00FFFFFFu)/16777216.0;
}
void main() {
    uint index=gl_GlobalInvocationID.x;
    if(index<uint(p0.w)) {
        // Density gating is ordered by index, not by hash: what is admitted is the prefix
        // [0, density*budget), so the CPU only has to dispatch that many indices and clear skies and
        // light snow need not burn the full budget. The hash is still needed, because the index is
        // shifted by the seed -- and that shift determines the spatial distribution of this batch.
        float ordinal=float(index);
        float cutoff=p3.x*p0.w;
        index+=uint(p3.w);
        float h0=Hash(index*3u+1u);
        float h1=Hash(index*5u+7u);
        float h2=Hash(index*11u+23u);
        float h3=Hash(index*17u+41u);
        if(ordinal<cutoff) {
            // The phase is timed by the lifetime, so the former fall is at the same time the normalized
            // age in [0,1); converting back to seconds allows closed-form integration. When the lifetime
            // is 0, t and invLife both become 0 and position and velocity fall back exactly to the
            // pre-extension result.
            float age=fract(p0.x+h2);
            float invLife=p3.z>0.0?1.0/p3.z:0.0;
            float t=age*p3.z;
            vec3 accel=p4.xyz;
            // frameData[11] is the HLSL Load4(176): disk radius / radial velocity / sway amplitude / curve-mode flag.
            vec4 uCurve=frameData[11];
            // frameData[16..18] are the HLSL Load4(256/272/288): initial velocity and cone jitter / spawn
            // axis and half-length and half-depth / speed jitter and floor and toon edge (the compaction
            // stage uses only the first two).
            vec4 uLaunch=frameData[16];
            vec4 uRect=frameData[17];
            vec4 uExtra=frameData[18];

            vec3 center;
            vec3 velocity;
            if((uint(p5.w)&16u)!=0u) {
                // Disk spawn: taking sqrt(h0) for the radius is what makes the area uniform, otherwise particles crowd into a clump at the disk centre.
                float ring=h1*6.283185;
                vec2 radial=vec2(cos(ring),sin(ring));
                float radius=sqrt(h0)*uCurve.x;
                center=vec3(p1.x+radial.x*radius,p1.y,p1.z+radial.y*radius);
                // Disk spawn has no initial drop: all vertical motion comes from acceleration and wind, so the rise speed of the flame is decided by AccelY alone.
                velocity=vec3(0.0);
            } else if((uint(p5.w)&32u)!=0u) {
                // Rect spawn: take a uniform stretch along the spawn axis and a uniform layer across it,
                // so the whole band is equally dense and, unlike the disk, needs no square root to
                // correct the area. h4..h6 are computed only here, so the default path does not spend
                // three extra hashes on them.
                float h4=Hash(index*23u+59u);
                float h5=Hash(index*29u+71u);
                float h6=Hash(index*31u+83u);
                vec2 axis=uRect.xy;
                vec2 across=vec2(-axis.y,axis.x);
                float along=(h0*2.0-1.0)*uRect.z;
                float depth=(h1*2.0-1.0)*uRect.w;
                center=vec3(p1.x+axis.x*along+across.x*depth,p1.y,p1.z+axis.y*along+across.y*depth);
                // The initial velocity is shared by the whole band; cone jitter gives a 3D direction offset proportional to |initial velocity|, and the speed jitter then scales the magnitude as a whole.
                velocity=(uLaunch.xyz+(vec3(h4,h5,h6)*2.0-1.0)*uLaunch.w*length(uLaunch.xyz))
                    *(1.0+(h6-0.5)*uExtra.x);
                // Position integrates the initial velocity: each particle on the band travels along its own initial velocity, and acceleration and wind are then added on top as usual.
                center+=velocity*t;
            } else {
                center.x=p1.x+(h0*2.0-1.0)*p0.y+sin((p0.x+h0)*6.283185)*p3.y;
                center.y=p1.y+p0.z*(0.7-age);
                center.z=p1.z+(h1*2.0-1.0)*p0.y+cos((p0.x+h1)*6.283185)*p3.y;
                // The base velocity is the time derivative of the three lines above: the phase rate is
                // 1/life, so each term carries an invLife. In the default case p0.z*invLife equals
                // FallSpeed, since life is already boxHeight / FallSpeed.
                velocity.x=cos((p0.x+h0)*6.283185)*6.283185*p3.y*invLife;
                velocity.y=-p0.z*invLife;
                velocity.z=-sin((p0.x+h1)*6.283185)*6.283185*p3.y*invLife;
            }
            // The acceleration displacement must be folded in before building clip, otherwise the position used for the cull test and the final rasterized position are not the same point.
            center+=0.5*accel*t*t;
            velocity+=accel*t;
            center+=p5.xyz*t;
            velocity+=p5.xyz;
            if((uint(p5.w)&16u)!=0u) {
                float ring=h1*6.283185;
                vec2 radial=vec2(cos(ring),sin(ring));
                // Radial spread is proportional to age: the column therefore widens as it rises; fire uses a negative Spread to draw the flame tongues toward the axis.
                center.xz+=radial*uCurve.y*t;
                velocity.xz+=radial*uCurve.y;
                // Two sines at different frequencies compose a low-frequency sway that is still a closed-form function of age, introducing no cross-frame state.
                center.xz+=vec2(sin(t*1.3+h2*6.283185),cos(t*1.1+h3*6.283185))*uCurve.z*t;
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
                    vec4 a=regionData[r*3u],b=regionData[r*3u+1u],c=regionData[r*3u+2u];
                    vec2 d=center.xz-a.xy;
                    vec2 local=vec2(b.x*d.x+b.y*d.y,-b.y*d.x+b.x*d.y)*a.zw;
                    float m=clamp((1.0-length(local))/b.z,0.0,1.0);
                    m=m*m*(3.0-2.0*m);
                    if(center.y<c.x || center.y>c.y) m=0.0;
                    weight+=remaining*m*(channel==1u?c.z:c.w);
                    remaining*=1.0-m;
                }
            }

            // The periodic-window relocation happens at the invisible edge; intensity changes only fade in new candidates rather than resetting the whole field.
            float viewDistance=frameData[7].w;
            if(viewDistance>0.0) {
                float f=clamp((1.0-length(center-frameData[6].xyz)/viewDistance)*4.0,0.0,1.0);
                weight*=f*f*(3.0-2.0*f);
            }
            // The soft-edge width is 2% of the admitted prefix (at least one slot): as the density ramps,
            // newly admitted particles start at zero coverage instead of appearing all at once. The prior
            // version took its edge from h3 * 0.02, which is the same order of width.
            if((uint(p5.w)&8u)!=0u) weight*=clamp((cutoff-ordinal)/max(cutoff*0.02,1.0),0.0,1.0);
            // Die on floor: if the integrated position is already below the floor, this particle is not
            // emitted, instead of letting it pass below the ground. It must come after the weight
            // computation: region sampling resets weight, so placing it earlier would be overwritten.
            if((uint(p5.w)&64u)!=0u && center.y<uExtra.y) weight=0.0;

            // frameData[0..3] are the HLSL Load4(0/16/32/48): the four rows of the view-projection.
            mat4 viewProjection=mat4(frameData[0],frameData[1],frameData[2],frameData[3]);
            vec4 clip=viewProjection*vec4(center,1.0);
            // Use w plus the billboard radius as a lateral margin, so edge-hugging particles are not discarded wholesale once expanded into a quad.
            float margin=clip.w+p1.w*4.0;
            vec4 streak=abs(viewProjection*vec4(velocity*frameData[7].z*0.5,0.0));
            if(weight>0.0 && clip.w+streak.w>1e-4 && clip.z+streak.z>-1e-4
                && abs(clip.x)<margin+streak.x+streak.w && abs(clip.y)<margin+streak.y+streak.w) {
                // Each thread adds at most one and the thread count equals capacity, so slot is guaranteed to be less than capacity and needs no clamping.
                uint slot=atomicAdd(args[1],1u);
                // The 48-byte instance stride is three vec4: HLSL offset+0, +16 and +32.
                instanceData[slot*3u]=vec4(center,p1.w);
                // In curve mode rgb instead stores two stable random numbers (hashed by index, immune to
                // instance-slot contention); the vertex stage uses them to pick the colour gradient and
                // the initial rotation. Coverage still travels only through alpha, exactly as before.
                vec3 tint=uCurve.w>0.5?vec3(h0,h1,0.0):p2.xyz;
                instanceData[slot*3u+1u]=vec4(tint,p2.w*weight);
                instanceData[slot*3u+2u]=vec4(velocity,age);
            }
        }
    }
}
""";

    // ── Draw-side shaders, consumed by VKParticleDrawPipeline ──
    // No vertex buffer and no input layout, matching the HLSL: the quad corner comes from
    // gl_VertexIndex over the engine's existing unit-quad index buffer and every per-instance value
    // is loaded by gl_InstanceIndex. Bindings are a dedicated set, 0 instances / 1 frame.
    //
    // gl_VertexIndex and gl_InstanceIndex include vertexOffset and firstInstance, which SV_VertexID
    // and SV_InstanceID do not. The IndirectDrawIndexedArgs contract pins both of those arguments to
    // zero -- WebGPU cannot express a non-zero firstInstance without an optional feature -- so the
    // two families agree here. They would diverge the moment that contract were relaxed.

    /// <summary>Vulkan vertex stage for the indirect billboard draw.</summary>
    public const string DrawVertexGlsl = """
#version 450
layout(std430,binding=0) readonly buffer Instances { vec4 instanceData[]; };
layout(std430,binding=1) readonly buffer Frame { vec4 frameData[]; };
layout(location=0) out vec2 vOffset;
layout(location=1) out vec4 vColor;
layout(location=2) out vec4 vMisc;
// (toon edge width, reserved, reserved, reserved). The fragment stage cannot read the frame buffer, so it can only arrive via the interpolator.
layout(location=3) out vec4 vMisc2;
void main() {
    uint slot=uint(gl_InstanceIndex);
    vec4 packed=instanceData[slot*3u];
    vec3 center=packed.xyz;
    float size=packed.w;
    vec4 color=instanceData[slot*3u+1u];
    vec4 motion=instanceData[slot*3u+2u];
    vec3 velocity=motion.xyz;
    float age=motion.w;

    uint vertexId=uint(gl_VertexIndex);
    vec2 corner=vec2((vertexId&1u)!=0u?1.0:-1.0,(vertexId&2u)!=0u?-1.0:1.0);
    // frameData[4] and [5] are the HLSL Load4(64) and Load4(80): camera right and up.
    vec3 right=frameData[4].xyz;
    vec3 up=frameData[5].xyz;
    vec3 eye=frameData[6].xyz;
    // frameData[7] is the HLSL Load4(112): fadeIn / fadeOut (fractions of the lifetime) / stretch in seconds.
    vec3 drawParams=frameData[7].xyz;
    // frameData[8..11] are the appearance curve and spawn shape: when curve mode is off the flag is 0,
    // so the branch below does not fire and size, color and corner are bit-identical to before the
    // extension.
    vec4 tintStart=frameData[8];
    vec4 tintMid=frameData[9];
    vec4 tintEnd=frameData[10];
    vec4 curve=frameData[11];
    // frameData[18] = speed jitter / floor height / toon edge / reserved: only the edge width travels to the fragment.
    vec4 uExtra=frameData[18];
    mat4 viewProjection=mat4(frameData[0],frameData[1],frameData[2],frameData[3]);
    float seed=0.0;
    vec2 shape=corner;
    if(curve.w>0.5) {
        // The instance rgb holds the two stable random numbers written by the compaction stage: hashed by particle index, immune to instance-slot contention.
        seed=color.r;
        // Two-segment linear gradient: first half start->mid, second half mid->end.
        color.rgb=age<0.5?mix(tintStart.rgb,tintMid.rgb,age*2.0):mix(tintMid.rgb,tintEnd.rgb,age*2.0-1.0);
        size=mix(size,tintEnd.w,age);
        // A per-particle initial rotation plus a slow spin: rows of in-phase blobs are the most obvious giveaway of cheap particles.
        float spin=seed*6.283185+age*(seed-0.5)*3.0;
        float spinCos=cos(spin);
        float spinSin=sin(spin);
        shape=vec2(corner.x*spinCos-corner.y*spinSin,corner.x*spinSin+corner.y*spinCos);
    }
    // This must be written as a ternary, not as a division by an epsilon: when fadeIn is 0 age is often
    // 0 too, and clamp(0/eps) gives 0 rather than 1, which would eat the coverage of a whole field of
    // particles.
    float fadeIn=drawParams.x>0.0?clamp(age/drawParams.x,0.0,1.0):1.0;
    float fadeOut=drawParams.y>0.0?clamp((1.0-age)/drawParams.y,0.0,1.0):1.0;
    color.a*=fadeIn*fadeOut;
    // Shear the quad into a parallelogram along the velocity direction, with total length
    // |velocity| * stretch; the top and bottom corners each offset by half that length, so at
    // stretch = 0 it degenerates exactly back to the original square billboard.
    vec3 world=center+(right*shape.x+up*shape.y)*size+velocity*(drawParams.z*shape.y*0.5);
    vec2 uv=corner;

    // ── ribbon strip: "segmented polylines" like lightning channels do not use billboards ──
    // frameData[12] = (viewport width, viewport height, minimum half-width in pixels, ribbon flag),
    // frameData[13] = (intensity, fog extinction, reserved, reserved). When the flag is 0 this whole
    // section is skipped.
    vec4 ribbon=frameData[12];
    if(ribbon.w>0.5) {
        // In this mode the instance record changes meaning: Position is the segment midpoint and
        // Velocity is the half-segment vector, so "midpoint ± half segment" gives the two endpoints
        // without storing an extra instance per segment.
        float segLen=length(velocity);
        vec3 tangent=segLen>1e-5?velocity/segLen:vec3(0.0,1.0,0.0);
        // Camera-facing strip: the lateral direction is the cross product of the tangent and the view
        // ray. When facing the camera head-on that cross product degenerates (the strip is just a point
        // anyway), so falling back to the camera right axis is fine; direction is irrelevant then.
        vec3 side=cross(tangent,eye-center);
        float sideLen=length(side);
        side=sideLen>1e-5?side/sideLen:right;

        // Minimum screen width: project the world-unit side into pixel space to get "how many pixels one
        // metre laterally spans", then invert it to find the world half-width needed for the minimum
        // pixel width. When a polyline is thinner than a pixel, rasterization flickers on and off -- that
        // is a loss where the fragment is simply never produced, and no temporal compensation can recover
        // it. TAA's reactive rect (ViewTargets.ReactiveRect) only keeps already-drawn pixels from being
        // dragged by history; it cannot fill in a fragment that never existed.
        vec4 clipSide=viewProjection*vec4(side,0.0);
        float clipW=max(abs(clipSide.w),1e-3);
        float pxPerWorld=max(length(abs(clipSide.xy)/clipW*ribbon.xy*0.5),1e-4);
        float halfWidth=max(size,ribbon.z/pxPerWorld);

        // Extend half a band width along the tangent at each end: adjacent segments then overlap at the bend instead of leaving a seam.
        world=center+tangent*(corner.y*(segLen+halfWidth))+side*(corner.x*halfWidth);
        // The lateral falloff reuses the 1-dot(uv,uv) from the fragment: the longitudinal axis does not participate, so the strip stays a line of uniform brightness.
        uv=vec2(corner.x,0.0);

        // Both intensity and fog attenuation only scale coverage: the fragment multiplies rgb into alpha, so applying them to rgb would square the result.
        vec4 trim=frameData[13];
        color.a*=trim.x*exp(-trim.y*length(center-eye));
    }

    // No Y flip here: Device.BeginPass already binds a negative-height viewport.
    gl_Position=viewProjection*vec4(world,1.0);
    // vOffset keeps the unrotated corner: the falloff only takes its sum of squares, rotation does not change the result, so the default path is untouched.
    vOffset=uv;
    vColor=color;
    // (stable random, normalized age, noise strength, additive-blend flag)
    vMisc=vec4(seed,age,curve.w>0.5?tintMid.w:0.0,tintStart.w);
    vMisc2=vec4(uExtra.z,0.0,0.0,0.0);
}
""";

    const string ShadeGlsl = """
layout(location=0) in vec2 vOffset;
layout(location=1) in vec4 vColor;
layout(location=2) in vec4 vMisc;
layout(location=3) in vec4 vMisc2;
// Sine-free hash: sin loses precision for large arguments on some hardware, and the input here grows monotonically with age.
float Hash2(vec2 p) {
    vec3 p3=fract(p.xyx*0.1031);
    p3+=dot(p3,p3.yzx+33.33);
    return fract((p3.x+p3.y)*p3.z);
}
float ValueNoise(vec2 p) {
    vec2 i=floor(p);
    vec2 f=p-i;
    f=f*f*(3.0-2.0*f);
    return mix(mix(Hash2(i),Hash2(i+vec2(1.0,0.0)),f.x),
        mix(Hash2(i+vec2(0.0,1.0)),Hash2(i+vec2(1.0,1.0)),f.x),f.y);
}
vec4 Shade() {
    float falloff=clamp(1.0-dot(vOffset,vOffset),0.0,1.0);
    // The default path is unchanged: with curve mode off vMisc.z is 0, so this line is bit-identical to before.
    float alpha=vColor.a*falloff*falloff;
    if(vMisc.z>0.0) {
        // The noise scrolls along -y with age, so flame tongues and smoke curls travel upward in place rather than staying stuck.
        vec2 q=vOffset*1.7+vMisc.x*31.0+vec2(0.0,-vMisc.y*2.5);
        float n=0.65*ValueNoise(q)+0.35*ValueNoise(q*2.3);
        alpha=vColor.a*clamp(falloff*falloff*(1.0+vMisc.z*(n*2.0-1.0)),0.0,1.0);
    }
    // Toon edge: compress the smooth radial falloff into a crisp boundary, with the width as a fraction
    // of the billboard radius. 0 means off and the whole section is skipped, so every pixel of the
    // default path is bit-identical to before the extension.
    float edge=vMisc2.x;
    if(edge>0.0) {
        // coverage is "how much coverage this pixel originally had"; dividing by the peak coverage
        // restores it to [0,1] before the hard cut. The denominator is clamped to 1e-5 only to guard
        // against divide-by-zero: in the additive mode alpha is 0 anyway and stays 0 after the cut.
        float coverage=clamp(alpha/max(vColor.a,1e-5),0.0,1.0);
        alpha=vColor.a*smoothstep(0.5-edge,0.5+edge,coverage);
    }
    // The additive mode drives alpha to 0: under One/InvSrcAlpha this equals pure addition, so no extra PSO is needed.
    return vec4(vColor.rgb*alpha,alpha*(1.0-vMisc.w));
}
""";

    /// <summary>Vulkan fragment stage for a colour-only scene pass.</summary>
    public const string DrawFragmentGlsl = """
#version 450

""" + ShadeGlsl + """

layout(location=0) out vec4 outColor;
void main() { outColor=Shade(); }
""";

    /// <summary>Vulkan fragment stage for the velocity-attachment variant of the scene pass.</summary>
    public const string DrawFragmentVelocityGlsl = """
#version 450

""" + ShadeGlsl + """

layout(location=0) out vec4 outColor;
layout(location=1) out vec2 outVelocity;
void main() {
    // Slot 1 is write-masked off in the velocity PSO variant, matching how the main pipeline keeps
    // opaque velocity underneath transparent geometry. The value here is never stored.
    outColor=Shade();
    outVelocity=vec2(0.0,0.0);
}
""";
}
