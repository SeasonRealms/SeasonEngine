// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

public sealed partial class ParticleSystemEffect
{
    /// <summary>D3D12 cs_5_0: rewrites the five indirect arguments every frame. indexCount / instanceCount / startIndex / baseVertex come from Params; startInstance stays 0.</summary>
    const string ResetHlsl = @"
cbuffer ParticleResetParams : register(b0)
{
    float uIndexCount;
    float uInstanceCount;
    float uStartIndex;
    float uBaseVertex;
};

RWByteAddressBuffer uArgs : register(u0);

[numthreads(1, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x == 0)
    {
        // IndirectDrawIndexedArgs: indexCount / instanceCount / startIndex / baseVertex / startInstance.
        // startInstance stays 0 by contract so SV_InstanceID equals the compacted slot on every backend.
        // The particle path passes 0 for the last three, so the output is bit-identical to before the
        // parameterization; the lightning channel passes a fixed segment count.
        uArgs.Store(0u, (uint)uIndexCount);
        uArgs.Store(4u, (uint)uInstanceCount);
        uArgs.Store(8u, (uint)uStartIndex);
        uArgs.Store(12u, (uint)uBaseVertex);
        uArgs.Store(16u, 0u);
    }
}
";

    /// <summary>D3D12 cs_5_0: derives one particle per thread, culls it, and appends survivors while raising the indirect instance count.</summary>
    const string CompactHlsl = @"
cbuffer ParticleCompactParams : register(b0)
{
    float uPhase;
    float uBoxRadius;
    float uBoxHeight;
    float uCapacity;
    float uOriginX;
    float uOriginY;
    float uOriginZ;
    float uParticleSize;
    float uColorR;
    float uColorG;
    float uColorB;
    float uColorA;
    float uDensity;
    float uSway;
    float uLifeSeconds;
    float uSeed;
    float uAccelX;
    float uAccelY;
    float uAccelZ;
    float uRegionCount;
    float uWindX;
    float uWindY;
    float uWindZ;
    float uFlags;
};

ByteAddressBuffer uFrame : register(t0);
ByteAddressBuffer uRegions : register(t1);
RWByteAddressBuffer uInstances : register(u0);
RWByteAddressBuffer uArgs : register(u1);

float Hash(uint n)
{
    n = (n << 13u) ^ n;
    n = n * (n * n * 15731u + 789221u) + 1376312589u;
    return float(n & 0x00FFFFFFu) / 16777216.0;
}

[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    uint index = id.x;
    if (index < (uint)uCapacity)
    {
        // Density gating is index-ordered, not hash-based: what is admitted is the prefix
        // [0, density x budget), so the CPU only needs to dispatch that many indices and clear
        // weather or light snow need not spin at full budget.
        // The hash is still computed because the index is shifted by the seed -- that shift decides
        // the spatial distribution of this batch of indices.
        float ordinal = (float)index;
        float cutoff = uDensity * uCapacity;
        index += (uint)uSeed;
        float h0 = Hash(index * 3u + 1u);
        float h1 = Hash(index * 5u + 7u);
        float h2 = Hash(index * 11u + 23u);
        float h3 = Hash(index * 17u + 41u);
        if (ordinal < cutoff)
        {
            // The phase is timed by lifetime, so the former fall is at once the normalized age in
            // [0,1); converting it back to seconds allows closed-form integration.
            // When lifetime is 0, both t and invLife become zero, so position and velocity fall back
            // exactly to the pre-expansion result.
            float age = frac(uPhase + h2);
            float invLife = uLifeSeconds > 0.0 ? 1.0 / uLifeSeconds : 0.0;
            float t = age * uLifeSeconds;
            float3 accel = float3(uAccelX, uAccelY, uAccelZ);
            // row 11: disk radius / radial speed / sway amount / curve-mode flag.
            float4 uCurve = asfloat(uFrame.Load4(176u));
            // row 16 = initial velocity xyz / cone jitter, row 17 = spawn axis xy / half length along
            // the axis / half depth perpendicular, row 18 = speed jitter / floor height / toon edge /
            // reserved (the compaction stage uses only the first two).
            float4 uLaunch = asfloat(uFrame.Load4(256u));
            float4 uRect = asfloat(uFrame.Load4(272u));
            float4 uExtra = asfloat(uFrame.Load4(288u));

            float3 center;
            float3 velocity;
            if (((uint)uFlags & 16u) != 0u)
            {
                // Disk spawn: the radius takes sqrt(h0) to make the area uniform, otherwise particles
                // bunch up into a clump at the center.
                float ring = h1 * 6.283185;
                float2 radial = float2(cos(ring), sin(ring));
                float radius = sqrt(h0) * uCurve.x;
                center = float3(uOriginX + radial.x * radius, uOriginY, uOriginZ + radial.y * radius);
                // Disk spawn has no drop: vertical motion comes entirely from acceleration and wind,
                // so the rise speed of a flame is decided by AccelY alone.
                velocity = float3(0.0, 0.0, 0.0);
            }
            else if (((uint)uFlags & 32u) != 0u)
            {
                // Rect spawn: take a uniform segment along the spawn axis and a uniform layer
                // perpendicular, giving the whole strip equal density, unlike the disk which needs a
                // square root to correct the area. h4..h6 are computed only here, so the default path
                // does not spend three extra hashes on them.
                float h4 = Hash(index * 23u + 59u);
                float h5 = Hash(index * 29u + 71u);
                float h6 = Hash(index * 31u + 83u);
                float2 axis = uRect.xy;
                float2 across = float2(-axis.y, axis.x);
                float along = (h0 * 2.0 - 1.0) * uRect.z;
                float depth = (h1 * 2.0 - 1.0) * uRect.w;
                center = float3(uOriginX + axis.x * along + across.x * depth, uOriginY,
                    uOriginZ + axis.y * along + across.y * depth);
                // Initial velocity is shared by the whole strip; the cone jitter offsets the 3D
                // direction in proportion to |initial velocity|, and the speed jitter then scales the
                // magnitude as a whole.
                velocity = (uLaunch.xyz + (float3(h4, h5, h6) * 2.0 - 1.0) * uLaunch.w * length(uLaunch.xyz))
                    * (1.0 + (h6 - 0.5) * uExtra.x);
                // Position integrates the initial velocity: each particle on the strip travels along
                // its own initial velocity, and acceleration and wind are then added on top as usual.
                center += velocity * t;
            }
            else
            {
                center.x = uOriginX + (h0 * 2.0 - 1.0) * uBoxRadius + sin((uPhase + h0) * 6.283185) * uSway;
                center.y = uOriginY + uBoxHeight * (0.7 - age);
                center.z = uOriginZ + (h1 * 2.0 - 1.0) * uBoxRadius + cos((uPhase + h1) * 6.283185) * uSway;
                // The base velocity is the time derivative of the three lines above: the phase rate is
                // exactly 1/life, so every term carries an invLife.
                // uBoxHeight * invLife happens to equal FallSpeed at default settings, because life is
                // itself height / FallSpeed.
                velocity.x = cos((uPhase + h0) * 6.283185) * 6.283185 * uSway * invLife;
                velocity.y = -uBoxHeight * invLife;
                velocity.z = -sin((uPhase + h1) * 6.283185) * 6.283185 * uSway * invLife;
            }
            // The acceleration displacement must be folded in before the clip is constructed,
            // otherwise the position used for culling and the position finally rasterized are not the
            // same point.
            center += 0.5 * accel * t * t;
            velocity += accel * t;
            float3 wind = float3(uWindX, uWindY, uWindZ);
            center += wind * t;
            velocity += wind;
            if (((uint)uFlags & 16u) != 0u)
            {
                float ring = h1 * 6.283185;
                float2 radial = float2(cos(ring), sin(ring));
                // Radial spread is proportional to age: the smoke column therefore widens as it rises;
                // flames use a negative Spread to pull the flame tongue in toward the axis.
                center.xz += radial * uCurve.y * t;
                velocity.xz += radial * uCurve.y;
                // Two sines of different frequencies combine into a low-frequency sway, still a
                // closed-form function of age and introducing no cross-frame state.
                center.xz += float2(sin(t * 1.3 + h2 * 6.283185), cos(t * 1.1 + h3 * 6.283185)) * uCurve.z * t;
            }
            if (((uint)uFlags & 4u) != 0u) {
                float2 camera = asfloat(uFrame.Load4(96u)).xz;
                float period = 2.0 * uBoxRadius;
                center.xz -= floor((center.xz - camera + uBoxRadius) / period) * period;
            }
            // The three row region records match WeatherField.WriteParticleData; the residual weight
            // is combined by priority.
            uint channel = (uint)uFlags & 3u;
            float weight = 1.0;
            if (channel != 0u) {
                weight = 0.0;
                float remaining = 1.0;
                for (uint r = 0u; r < (uint)uRegionCount; r++) {
                    float4 a = asfloat(uRegions.Load4(r * 48u));
                    float4 b = asfloat(uRegions.Load4(r * 48u + 16u));
                    float4 c = asfloat(uRegions.Load4(r * 48u + 32u));
                    float2 d = center.xz - a.xy;
                    float2 local = float2(b.x*d.x+b.y*d.y, -b.y*d.x+b.x*d.y) * a.zw;
                    float m = saturate((1.0-length(local)) / b.z);
                    m = m*m*(3.0-2.0*m);
                    if (center.y < c.x || center.y > c.y) m = 0.0;
                    weight += remaining * m * (channel == 1u ? c.z : c.w);
                    remaining *= 1.0-m;
                }
            }

            // The wrapping of the periodic window happens at the invisible edge; an intensity change
            // only fades in new candidates rather than resetting the whole field.
            float viewDistance = asfloat(uFrame.Load4(112u)).w;
            if (viewDistance > 0.0) {
                float f = saturate((1.0 - length(center - asfloat(uFrame.Load4(96u)).xyz) / viewDistance) * 4.0);
                weight *= f * f * (3.0 - 2.0 * f);
            }
            if (((uint)uFlags & 8u) != 0u)
            {
                // The soft edge width is 2% of the admitted prefix (at least one index): as density
                // ramps, newly admitted particles start from zero coverage instead of the whole batch
                // appearing at once. The old version took the edge from h3's 0.02, the same order of
                // width.
                weight *= saturate((cutoff - ordinal) / max(cutoff * 0.02, 1.0));
            }
            // Die on floor: if the integrated position is already below the floor, this particle is
            // not emitted, rather than letting it sink through the ground.
            // This must come after the weight computation: the region sampling resets weight, and
            // placing it before would have it overwritten entirely.
            if (((uint)uFlags & 64u) != 0u && center.y < uExtra.y) weight = 0.0;

            float4x4 viewProjection = float4x4(
                asfloat(uFrame.Load4(0u)), asfloat(uFrame.Load4(16u)),
                asfloat(uFrame.Load4(32u)), asfloat(uFrame.Load4(48u)));
            float4 clip = mul(float4(center, 1.0), viewProjection);
            // Add the billboard radius to w as a horizontal margin, so edge-hugging particles are not
            // discarded wholesale once expanded into a quad.
            float margin = clip.w + uParticleSize * 4.0;
            float4 streak = abs(mul(float4(velocity * asfloat(uFrame.Load4(112u)).z * 0.5, 0.0), viewProjection));
            if (weight > 0.0 && clip.w + streak.w > 1e-4 && clip.z + streak.z > -1e-4
                && abs(clip.x) < margin + streak.x + streak.w && abs(clip.y) < margin + streak.y + streak.w)
            {
                uint slot;
                // Each thread adds at most one, and the thread count is the capacity, so slot is
                // necessarily below the capacity and needs no further clamping.
                uArgs.InterlockedAdd(4u, 1u, slot);
                uint offset = slot * 48u;
                uInstances.Store3(offset, asuint(center));
                uInstances.Store(offset + 12u, asuint(uParticleSize));
                // In curve mode rgb instead stores two stable random numbers (hashed from the index,
                // unaffected by contention for instance slots), from which the vertex stage picks the
                // color gradient and the initial rotation; coverage still only goes through alpha,
                // exactly as before.
                float4 stored = uCurve.w > 0.5
                    ? float4(h0, h1, 0.0, uColorA * weight)
                    : float4(uColorR, uColorG, uColorB, uColorA * weight);
                uInstances.Store4(offset + 16u, asuint(stored));
                uInstances.Store3(offset + 32u, asuint(velocity));
                uInstances.Store(offset + 44u, asuint(age));
            }
        }
    }
}
";

    // ── Draw-side shaders, consumed by the backend particle pipeline ──
    // No vertex buffer and no input layout: the quad corner comes from SV_VertexID over the engine's
    // existing unit-quad index buffer, and every per-instance value is loaded from the compacted
    // instance buffer by SV_InstanceID. Bindings are t0 instances / t1 frame, both root SRVs.

    /// <summary>D3D12 vs_5_0 / ps_5_0 source for the indirect billboard draw.</summary>
    public const string DrawHlsl = @"
ByteAddressBuffer uInstances : register(t0);
ByteAddressBuffer uFrame : register(t1);

struct VSOutput
{
    float4 Position : SV_Position;
    float2 Offset : TEXCOORD0;
    float4 Color : COLOR0;
    // (stable random number, normalized age, noise amount, additive blend flag). The curve-mode
    // appearance parameters are used only in the fragment stage, but WebGPU's bind group is visible
    // only to the vertex stage, so these values must travel through interpolators rather than reading
    // the frame buffer again.
    float4 Misc : TEXCOORD1;
    // (toon edge width, reserved, reserved, reserved). It likewise can only reach the fragment via
    // interpolators: the WebGPU fragment stage cannot read the frame buffer.
    float4 Misc2 : TEXCOORD2;
};

VSOutput VSMain(uint vertexId : SV_VertexID, uint instanceId : SV_InstanceID)
{
    // startInstance is 0 by the IndirectDrawIndexedArgs contract, so SV_InstanceID is the slot the
    // compaction kernel wrote. It would not be if the arguments ever carried a non-zero firstInstance.
    uint offset = instanceId * 48u;
    float3 center = asfloat(uInstances.Load3(offset));
    float size = asfloat(uInstances.Load(offset + 12u));
    float4 color = asfloat(uInstances.Load4(offset + 16u));
    float3 velocity = asfloat(uInstances.Load3(offset + 32u));
    float age = asfloat(uInstances.Load(offset + 44u));

    float2 corner = float2((vertexId & 1u) != 0u ? 1.0 : -1.0, (vertexId & 2u) != 0u ? -1.0 : 1.0);
    float3 right = asfloat(uFrame.Load4(64u)).xyz;
    float3 up = asfloat(uFrame.Load4(80u)).xyz;
    float3 eye = asfloat(uFrame.Load4(96u)).xyz;
    // Frame row 7 = fadeIn / fadeOut (both fractions of the lifetime) / stretch in seconds.
    float3 drawParams = asfloat(uFrame.Load4(112u)).xyz;
    // rows 8..11 are the appearance curve and spawn shape: with curve mode off the flag is 0, this
    // branch does not hold, and size, color and corner are bit-identical to before the expansion.
    float4 tintStart = asfloat(uFrame.Load4(128u));
    float4 tintMid = asfloat(uFrame.Load4(144u));
    float4 tintEnd = asfloat(uFrame.Load4(160u));
    float4 curve = asfloat(uFrame.Load4(176u));
    // row 18 = speed jitter / floor height / toon edge / reserved: only the edge width needs to
    // travel through interpolators to the fragment.
    float4 uExtra = asfloat(uFrame.Load4(288u));
    float4x4 viewProjection = float4x4(
        asfloat(uFrame.Load4(0u)), asfloat(uFrame.Load4(16u)),
        asfloat(uFrame.Load4(32u)), asfloat(uFrame.Load4(48u)));
    float seed = 0.0;
    float2 shape = corner;
    if (curve.w > 0.5)
    {
        // The instance rgb is the two stable random numbers written by the compaction stage: hashed
        // from the particle index, unaffected by contention for instance slots.
        seed = color.r;
        // Two linear gradient segments: the first half goes start -> mid, the second half mid -> end.
        color.rgb = age < 0.5
            ? lerp(tintStart.rgb, tintMid.rgb, age * 2.0)
            : lerp(tintMid.rgb, tintEnd.rgb, age * 2.0 - 1.0);
        size = lerp(size, tintEnd.w, age);
        // Each particle gets its own initial rotation and slowly spins: rows of in-phase blobs are the
        // most obvious tell of cheap particles.
        float spin = seed * 6.283185 + age * (seed - 0.5) * 3.0;
        float spinCos = cos(spin);
        float spinSin = sin(spin);
        shape = float2(corner.x * spinCos - corner.y * spinSin, corner.x * spinSin + corner.y * spinCos);
    }
    // Must be written as a ternary, not as a division by an epsilon: when fadeIn is 0, age is also
    // often 0, and saturate(0 / eps) gives 0 instead of 1, eating the whole field's coverage.
    float fadeIn = drawParams.x > 0.0 ? saturate(age / drawParams.x) : 1.0;
    float fadeOut = drawParams.y > 0.0 ? saturate((1.0 - age) / drawParams.y) : 1.0;
    color.a *= fadeIn * fadeOut;
    // Shear the quad into a parallelogram along the velocity direction, with total length
    // |velocity| * stretch; the two corners each offset by half a length, so at stretch = 0 it
    // degenerates exactly back to the original square billboard.
    float3 world = center + (right * shape.x + up * shape.y) * size
        + velocity * (drawParams.z * shape.y * 0.5);
    float2 uv = corner;

    // ── ribbon strip: segment polylines like the lightning channel do not use billboards ──
    // row 12 = (viewport width, viewport height, minimum half-width in pixels, ribbon flag), row 13 =
    // (intensity, fog extinction, reserved, reserved).
    // When the flag is 0 the whole block is skipped, so every byte of the particle path matches the
    // pre-expansion layout.
    float4 ribbon = asfloat(uFrame.Load4(192u));
    if (ribbon.w > 0.5)
    {
        // The instance record changes meaning in this mode: Position is the segment midpoint and
        // Velocity is the half-segment vector, so 'midpoint +/- half segment' gives the two endpoints
        // and no extra instance per segment is needed.
        float segLen = length(velocity);
        float3 tangent = segLen > 1e-5 ? velocity / segLen : float3(0.0, 1.0, 0.0);
        // A camera-facing strip: the lateral direction is the cross product of the tangent and the
        // view ray. When looking straight down the lens the cross product degenerates (the strip is
        // really just a point), so falling back to the camera right axis is fine, and the direction is
        // irrelevant in that case anyway.
        float3 side = cross(tangent, eye - center);
        float sideLen = length(side);
        side = sideLen > 1e-5 ? side / sideLen : right;

        // Minimum screen width: project the world-space side into pixel space to get 'how many pixels
        // one meter laterally equals', then invert it to find the world half-width needed to
        // guarantee the minimum pixel width. When a polyline is thinner than a pixel, rasterization
        // flickers in and out -- that is a fragment that was never generated at all, and no temporal
        // compensation can rescue it. TAA's reactive rect
        // (ViewTargets.ReactiveRect) only keeps already-drawn pixels from being held back by history;
        // it cannot fill in a fragment that does not exist.
        float4 clipSide = mul(float4(side, 0.0), viewProjection);
        float clipW = max(abs(clipSide.w), 1e-3);
        float pxPerWorld = max(length(abs(clipSide.xy) / clipW * ribbon.xy * 0.5), 1e-4);
        float halfWidth = max(size, ribbon.z / pxPerWorld);

        // Extend half a strip width along the tangent at each end: adjacent segments therefore
        // overlap at the joints instead of leaving a gap.
        world = center + tangent * (corner.y * (segLen + halfWidth)) + side * (corner.x * halfWidth);
        // The lateral falloff reuses the fragment's 1-dot(uv,uv): the longitudinal direction does not
        // participate, so the strip stays a line of uniform brightness.
        uv = float2(corner.x, 0.0);

        // Both intensity and fog attenuation only scale coverage: the fragment multiplies rgb into
        // alpha, and applying them to rgb would square it.
        float4 trim = asfloat(uFrame.Load4(208u));
        color.a *= trim.x * exp(-trim.y * length(center - eye));
    }

    VSOutput output;
    output.Position = mul(float4(world, 1.0), viewProjection);
    // Offset keeps the unrotated corner: the falloff takes only its sum of squares, and rotation does
    // not change the result, so the default path stays untouched byte for byte.
    output.Offset = uv;
    output.Color = color;
    output.Misc = float4(seed, age, curve.w > 0.5 ? tintMid.w : 0.0, tintStart.w);
    output.Misc2 = float4(uExtra.z, 0.0, 0.0, 0.0);
    return output;
}

// Sine-free hash: sin loses precision for large arguments on some hardware, and the inputs here grow
// steadily with age.
float Hash2(float2 p)
{
    float3 p3 = frac(p.xyx * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

float ValueNoise(float2 p)
{
    float2 i = floor(p);
    float2 f = p - i;
    f = f * f * (3.0 - 2.0 * f);
    return lerp(lerp(Hash2(i), Hash2(i + float2(1.0, 0.0)), f.x),
        lerp(Hash2(i + float2(0.0, 1.0)), Hash2(i + float2(1.0, 1.0)), f.x), f.y);
}

float4 Shade(VSOutput input)
{
    float falloff = saturate(1.0 - dot(input.Offset, input.Offset));
    // The default path stays as-is: with curve mode off Misc.z is 0, so this line is bit-identical to
    // the old version.
    float alpha = input.Color.a * falloff * falloff;
    if (input.Misc.z > 0.0)
    {
        // The noise scrolls along -y with age, so the flame tongue and smoke curl travel upward in
        // place instead of sticking motionless.
        float2 q = input.Offset * 1.7 + input.Misc.x * 31.0 + float2(0.0, -input.Misc.y * 2.5);
        float n = 0.65 * ValueNoise(q) + 0.35 * ValueNoise(q * 2.3);
        alpha = input.Color.a * saturate(falloff * falloff * (1.0 + input.Misc.z * (n * 2.0 - 1.0)));
    }
    // Toon edge: compress the smooth radial falloff into a distinct boundary, with its width as a
    // fraction of the billboard radius.
    // 0 means off, the whole block is skipped, so every pixel of the default path is bit-identical to
    // before the expansion.
    float edge = input.Misc2.x;
    if (edge > 0.0)
    {
        // coverage is 'how much coverage this pixel originally had'; the peak coverage is used as the
        // denominator to normalize back to [0,1] before cutting. The denominator is clamped to 1e-5
        // only to guard against division by zero: the additive mode's alpha is already 0, and it stays
        // 0 after the cut.
        float coverage = saturate(alpha / max(input.Color.a, 1e-5));
        alpha = input.Color.a * smoothstep(0.5 - edge, 0.5 + edge, coverage);
    }
    // The additive mode pushes alpha to 0: under One/InvSrcAlpha this equals pure addition, so no
    // extra PSO is needed.
    return float4(input.Color.rgb * alpha, alpha * (1.0 - input.Misc.w));
}

float4 PSMain(VSOutput input) : SV_Target0
{
    return Shade(input);
}

struct PSVelocityOutput
{
    float4 Color : SV_Target0;
    float2 Velocity : SV_Target1;
};

PSVelocityOutput PSMainVelocity(VSOutput input)
{
    // Slot 1 is write-masked off in the velocity PSO variant, matching how the main pipeline keeps
    // opaque velocity underneath transparent geometry. The value here is never stored.
    PSVelocityOutput output;
    output.Color = Shade(input);
    output.Velocity = float2(0.0, 0.0);
    return output;
}
";
}
