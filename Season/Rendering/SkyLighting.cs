// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// 2-5 Step B: CPU-side evaluator for the procedural atmosphere.
/// It is the second implementation of the same physical model used by the three GPU kernels in <see cref="Effects.SkyAtmosphereEffect"/>,
/// with <see cref="Atmosphere"/> remaining the single source of truth for parameters.
///
/// Why keep a CPU implementation instead of reading GPU LUTs back:
/// direct-light color and SH9 environment lighting are decided on the CPU every frame and uploaded into <c>SceneLightParams</c>,
/// while GPU readback would add fence latency, per-backend readback plumbing, and inconsistent async semantics on WebGPU.
/// Point evaluation is cheap enough that recomputing it is far simpler and avoids one-frame lighting lag.
///
/// Model correspondence:
/// - <see cref="EvaluateTransmittance"/> mirrors the <c>skyTransmittance</c> kernel.
/// - <see cref="EvaluateMultiScatter"/> mirrors the <c>skyMultiScatter</c> kernel.
/// - <see cref="SkyRadiance"/> mirrors the <c>skyView</c> kernel.
/// All three return linear HDR radiance with no tonemap or clamp.
///
/// CPU LUT policy:
/// this class keeps smaller LUTs than the GPU path for CPU-side efficiency, but preserves the same physical model.
/// The radius axis intentionally matches the GPU height resolution to avoid a systematic CPU-only bias,
/// while the mu axis stays coarser because the remaining near-horizon limitation is shared by both CPU and GPU parameterization.
///
/// Practical notes:
/// - Rebake is gated by the same eight static-parameter checks as the GPU path.
/// - The expensive part is multiple-scattering rebake, which is acceptable for static-parameter changes but not for per-frame dynamic weather edits.
/// - When exact cross-checking against GPU math is needed, use the analytic entry points to bypass LUT-resolution differences.
/// - Mutable static state here is not thread-safe; call sites must keep it on a single frame phase/thread, matching the Atmosphere contract.
/// </summary>
public static class SkyLighting
{
    // [SkyDebug] Temporary diagnostic counters for missing-star debugging (LogType.Backend). Remove after investigation.
    static int _skyDebugFrame;
    static int _skyDebugEarlyExitCount;

    // -- CPU coarse-LUT specification. Changes here only affect CPU-side precision and rebake cost, not GPU behavior. --

    /// <summary>CPU transmittance LUT width in the mu direction. GPU side uses 256.</summary>
    public const int TransmittanceLutWidth = 64;

    /// <summary>CPU transmittance LUT height in the radius direction. Intentionally matches the GPU value of 64 to eliminate the only independent CPU-side bias.</summary>
    public const int TransmittanceLutHeight = 64;

    /// <summary>CPU transmittance LUT rebake step count. GPU side uses 40.</summary>
    public const int TransmittanceLutSteps = 32;

    /// <summary>CPU multiple-scattering LUT width in the cosZ direction. GPU side uses 32.</summary>
    public const int MultiScatterLutWidth = 16;

    /// <summary>CPU multiple-scattering LUT height in the radius direction. GPU side uses 32.</summary>
    public const int MultiScatterLutHeight = 8;

    /// <summary>Per-ray rebake step count for the CPU multiple-scattering LUT. GPU side uses 20.</summary>
    public const int MultiScatterLutSteps = 16;

    /// <summary>Square root of the spherical sample count (8 -> 64 directions). Must stay aligned with the GPU kernel because it is part of the psi_ms definition.</summary>
    public const int MultiScatterSqrtSamples = 8;

    /// <summary>GPU <c>skyTransmittance</c> rebake step count, used as the default for <see cref="EvaluateTransmittance"/> so the analytic path matches GPU texel discretization.</summary>
    public const int GpuTransmittanceSteps = 40;

    /// <summary>Per-ray step count of GPU <c>skyMultiScatter</c>, used as the default for <see cref="EvaluateMultiScatter"/>.</summary>
    public const int GpuMultiScatterSteps = 20;

    /// <summary>Radial ring count used by <see cref="EvaluateDiskTransmittance"/>. The default gives enough samples to make sunset disk occlusion fade smoothly without excessive CPU cost.</summary>
    public const int DiskRings = 4;

    /// <summary>Azimuth sample count per ring for <see cref="EvaluateDiskTransmittance"/>, offset by the golden angle to reduce stepped horizon artifacts.</summary>
    public const int DiskSpokes = 8;

    const float InvFourPi = 0.07957747155f;     // 1/4pi: isotropic phase

    const float InvPi = 0.31830988618f;         // 1/pi: Lambert ground bounce

    const float PhaseRayleighK = 0.05968310366f; // 3/(16π)

    static readonly Vector3[] _transLut = new Vector3[TransmittanceLutWidth * TransmittanceLutHeight];

    static readonly Vector3[] _msLut = new Vector3[MultiScatterLutWidth * MultiScatterLutHeight];

    // Rebake-gating snapshot (eight values, matching GPU-side StaticParamsChanged item by item)
    static bool _baked;
    static Vector3 _bakedRayleigh;
    static float _bakedRayleighH, _bakedMieExt, _bakedMieH, _bakedGround, _bakedTop;
    static float _bakedMieScat, _bakedAlbedo;

    /// <summary>Whether both CPU LUTs have been baked. Becomes true after the first <see cref="Update"/> or any evaluation entry point.</summary>
    public static bool Ready => _baked;

    /// <summary>Observer radius from the planet center in kilometers, equal to ground radius plus view altitude. This is the origin radius for atmospheric evaluation.</summary>
    public static float ViewRadiusKm => Atmosphere.GroundRadiusKm + Atmosphere.ViewAltitudeKm;

    /// <summary>
    /// Rebake gate: rebuild both CPU LUTs when any of the eight static atmosphere parameters changes; otherwise return immediately.
    /// Steady-state cost is just eight float comparisons, so callers may invoke this before any evaluation path without concern.
    /// </summary>
    public static void Update()
    {
        if (!_baked || StaticParamsChanged())
            Rebake();
    }

    /// <summary>
    /// Sky radiance along a single view ray in linear HDR. This mirrors the <c>skyView</c> kernel:
    /// non-uniform marching, analytic segment integration, dual-light single scattering plus psi_ms energy, and airglow floor.
    /// If the ray hits the ground it terminates there, matching GPU behavior for the lower hemisphere.
    /// </summary>
    /// <param name="dir">Normalized world-space view direction. The local up direction is always +Y; see the <see cref="Atmosphere"/> class header.</param>
    /// <param name="steps">Step count. Values &lt;=0 fall back to <c>RenderQuality.Current.SkyRayMarchSteps</c>; SH9 projection may use fewer steps because it is a low-frequency summary.</param>
    public static Vector3 SkyRadiance(Vector3 dir, int steps = 0)
    {
        Update();

        float rg = Atmosphere.GroundRadiusKm;
        float rt = Atmosphere.AtmosphereRadiusKm;
        float r0 = rg + Atmosphere.ViewAltitudeKm;
        var origin = new Vector3(0f, r0, 0f);
        float mu = dir.Y;

        float tMax = HitsGround(r0, mu, rg)
            ? MathF.Max(RaySphere(r0, mu, rg), 0f)
            : MathF.Max(RaySphere(r0, mu, rt), 0f);

        var rayleigh = Atmosphere.RayleighScattering;
        float mieScat = Atmosphere.MieScattering;
        float mieExt = Atmosphere.MieExtinction;
        float rayleighH = Atmosphere.RayleighHeightKm;
        float mieH = Atmosphere.MieHeightKm;
        float g = Atmosphere.MiePhaseG;
        float msGain = Atmosphere.MultiScatterGain;

        var sunDir = SafeNormalize(Atmosphere.SunDirection, Vector3.UnitY);
        var moonDir = SafeNormalize(Atmosphere.MoonDirection, -Vector3.UnitY);
        var sunRadiance = Atmosphere.SunColor * Atmosphere.SunIrradiance;
        var moonRadiance = Atmosphere.MoonColor * Atmosphere.MoonIrradiance;

        int n = steps > 0 ? steps : Math.Max(4, RenderQuality.Current.SkyRayMarchSteps);
        float invSteps = 1f / n;

        var radiance = Vector3.Zero;
        var throughput = Vector3.One;
        float tPrev = 0f;

        for (int i = 1; i <= n; i++)
        {
            float f = i * invSteps;
            float tCur = tMax * f * f;
            float dt = tCur - tPrev;
            float tMid = 0.5f * (tPrev + tCur);
            tPrev = tCur;

            var pos = origin + dir * tMid;
            float rr = MathF.Max(pos.Length(), 1e-6f);
            var upLocal = pos / rr;
            AirDensity(rr - rg, rayleighH, mieH, out float densR, out float densM);

            var scatR = rayleigh * densR;
            float scatM = mieScat * densM;
            var extinction = Vector3.Max(rayleigh * densR + new Vector3(mieExt * densM), new Vector3(1e-7f));

            // Each celestial body contributes its own phase term, transmittance, and MS lookup while sharing scattering and extinction.
            var inScatter = LightInScatter(dir, upLocal, rr, sunDir, sunRadiance, scatR, scatM, g, msGain)
                          + LightInScatter(dir, upLocal, rr, moonDir, moonRadiance, scatR, scatM, g, msGain);

            var stepT = Exp(-extinction * dt);
            radiance += throughput * (inScatter - inScatter * stepT) / extinction;
            throughput *= stepT;
        }

        // Night-sky baseline glow: tint by the Rayleigh channel ratio (same formula as the GPU path).
        radiance += rayleigh * (Atmosphere.NightAirglow / MathF.Max(rayleigh.Z, 1e-6f));
        return radiance;
    }

    /// <summary>
    /// In-scatter contribution from a single celestial body at the current sample point.
    /// This is the CPU mirror of <c>LightInScatter</c> inside the <c>skyView</c> kernel:
    /// exact-phase single scattering times body transmittance, plus the isotropic psi_ms energy term, multiplied by body radiance.
    /// Ground occlusion is already baked into the transmittance LUT, so bodies below the horizon naturally contribute zero.
    /// </summary>
    static Vector3 LightInScatter(Vector3 viewDir, Vector3 upLocal, float r,
        Vector3 lightDir, Vector3 lightRadiance, Vector3 scatR, float scatM, float g, float msGain)
    {
        float c = Vector3.Dot(viewDir, lightDir);
        float phaseR = PhaseRayleighK * (1f + c * c);
        float g2 = g * g;
        float hgDen = MathF.Max(1f + g2 - 2f * g * c, 1e-4f);
        float phaseM = InvFourPi * (1f - g2) / (hgDen * MathF.Sqrt(hgDen));

        float cosLight = Vector3.Dot(upLocal, lightDir);
        var tLight = SampleTransmittanceLut(r, cosLight);
        var psiMs = SampleMultiScatterLut(r, cosLight) * msGain;

        var single = (scatR * phaseR + new Vector3(scatM * phaseM)) * tLight;
        var multi = (scatR + new Vector3(scatM)) * psiMs;
        return (single + multi) * lightRadiance;
    }

    // -- SH9 environment-light projection (b7): compress full-sphere sky + ground radiance into nine coefficients for EnvironmentMap. --
    // This section owns its own constants and state, independent from the two CPU LUT bake states above.

    /// <summary>Number of SH9 coefficients (l=0..2), matching <see cref="EnvironmentMap.Sh9Count"/>.</summary>
    public const int Sh9Count = 9;

    /// <summary>Direction count used by SH9 projection. 128 Fibonacci directions are sufficient because the sky is low-frequency and the sampling is quasi-uniform without Monte-Carlo noise.</summary>
    public const int Sh9DirectionCount = 128;

    /// <summary>Number of frame slices used to amortize SH9 projection. Each frame evaluates 1/N of the directions and publishes only after a full cycle.</summary>
    public const int Sh9FrameSlices = 8;

    /// <summary>Ray-march step count used by SH9 projection. Intentionally lower than the on-screen sky path because environment lighting only needs a low-frequency summary.</summary>
    public const int Sh9RayMarchSteps = 8;

    static readonly Vector4[] _irradianceSh9 = new Vector4[Sh9Count];
    static readonly Vector4[] _radianceSh9 = new Vector4[Sh9Count];
    static readonly Vector3[] _sh9Accum = new Vector3[Sh9Count];
    static float _sh9OmegaAccum;
    static int _sh9Slice;
    static bool _sh9Ready;

    /// <summary>Whether at least one complete SH9 projection has been produced. Until then, <see cref="ApplyTo"/> leaves EnvironmentMap untouched.</summary>
    public static bool Sh9Ready => _sh9Ready;

    /// <summary>Most recent complete SH9 irradiance coefficients, directly compatible with <see cref="EnvironmentMap.IrradianceSH9"/>.</summary>
    public static ReadOnlySpan<Vector4> IrradianceSh9 => _irradianceSh9;

    /// <summary>Most recent complete SH9 radiance coefficients, matching the semantics of <see cref="EnvironmentMap.RadianceSH9"/>.</summary>
    public static ReadOnlySpan<Vector4> RadianceSh9 => _radianceSh9;

    /// <summary>
    /// Advance one amortized slice of SH9 projection. Called once per frame.
    /// Each frame evaluates only 1/<see cref="Sh9FrameSlices"/> of the directions and publishes only after a full cycle,
    /// keeping the projection energy-consistent while spreading the cost over multiple frames.
    /// </summary>
    public static void AccumulateSh9()
    {
        if (_sh9Slice == 0)
        {
            Array.Clear(_sh9Accum);
            _sh9OmegaAccum = 0f;
        }

        float dOmega = 4f * MathF.PI / Sh9DirectionCount;

        for (int i = _sh9Slice; i < Sh9DirectionCount; i += Sh9FrameSlices)
        {
            var dir = FibonacciDirection(i);

            // Basis order matches EnvironmentMap.ProjectIrradianceSH9: 1, y, z, x, xy, yz, 3z^2-1, xz, x^2-y^2
            var weighted = (SkyRadiance(dir, Sh9RayMarchSteps) + GroundRadiance(dir)) * dOmega;
            _sh9Accum[0] += weighted;
            _sh9Accum[1] += weighted * dir.Y;
            _sh9Accum[2] += weighted * dir.Z;
            _sh9Accum[3] += weighted * dir.X;
            _sh9Accum[4] += weighted * (dir.X * dir.Y);
            _sh9Accum[5] += weighted * (dir.Y * dir.Z);
            _sh9Accum[6] += weighted * (3f * dir.Z * dir.Z - 1f);
            _sh9Accum[7] += weighted * (dir.X * dir.Z);
            _sh9Accum[8] += weighted * (dir.X * dir.X - dir.Y * dir.Y);

            _sh9OmegaAccum += dOmega;
        }

        if (++_sh9Slice < Sh9FrameSlices)
            return;

        _sh9Slice = 0;
        PublishSh9();
    }

    /// <summary>Run a full SH9 projection immediately. Intended for first frame or large time jumps; avoid in the steady-state per-frame path.</summary>
    public static void ProjectSh9Immediate()
    {
        _sh9Slice = 0;
        for (int s = 0; s < Sh9FrameSlices; s++)
            AccumulateSh9();
    }

    /// <summary>
    /// Copy the SH9 results computed by this class into <see cref="EnvironmentMap"/>.
    /// This method intentionally does not become a second lighting-UBO writer; SetLighting still writes EnvParams and IrradianceSH9 from the EnvironmentMap side only.
    /// </summary>
    /// <param name="environment">Target environment-light holder, usually <c>BaseApp.SceneEnvironment</c>.</param>
    public static void ApplyTo(EnvironmentMap environment)
    {
        if (!_sh9Ready)
            return;

        for (int i = 0; i < Sh9Count; i++)
        {
            environment.IrradianceSH9[i] = _irradianceSh9[i];
            environment.RadianceSH9[i] = _radianceSh9[i];
        }

        environment.SphericalHarmonicsReady = true;
    }

    /// <summary>
    /// 2-5 Step C: advance wind offsets for all cloud layers over elapsed time, accumulating into <see cref="Atmosphere.CloudWindOffsetKm"/>.
    /// This is integrated on the CPU rather than reconstructed from total time in shaders so wind-speed changes do not cause large phase jumps.
    /// Offsets are wrapped by each layer's <c>TileKm</c> period to stay bounded without changing the sampled noise.
    /// </summary>
    /// <param name="deltaSeconds">Elapsed seconds for this frame. Non-positive or NaN values leave cloud offsets unchanged.</param>
    public static void AdvanceClouds(float deltaSeconds)
    {
        if (!(deltaSeconds > 0f))
            return;

        int count = Math.Clamp(Atmosphere.Clouds.LayerCount, 0, SkyState.MaxLayers);
        for (int i = 0; i < count; i++)
        {
            float tile = MathF.Max(Atmosphere.Clouds.Layers[i].TileKm, 1e-3f);
            var off = Atmosphere.CloudWindOffsetKm[i] + Atmosphere.Clouds.Layers[i].WindKmPerSec * deltaSeconds;
            off.X -= tile * MathF.Floor(off.X / tile);
            off.Y -= tile * MathF.Floor(off.Y / tile);
            Atmosphere.CloudWindOffsetKm[i] = off;
        }
    }

    /// <summary>
    /// 2-5 Step B (b11): pack the five float4 values needed by the analytic sun/moon disks and starfield into the tail of the lighting UBO
    /// (<see cref="SceneLightParams.SkyParams0"/>..4, where slot 4 was added by Step C for the celestial pole axis),
    /// then append the eight float4 values for procedural clouds (see <c>ApplyClouds</c>) and the single float4 for aerial perspective (see <c>ApplyAerial</c>).
    /// Every backend calls this from its single <c>SetLighting</c> entry point, matching the <c>EnvironmentMap.Apply</c> convention.
    ///
    /// The gate is <c>FrameSchedule.SkyViewTexture != null</c>, which clause 6 of step 2-5 defines as the
    /// **only criterion** for "procedural sky available". Under the StaticCube mode this method returns early as a whole,
    /// leaving the five fields at the struct-default zeros, so the main shader sees <c>skyParams0.w &gt; 0</c> as false.
    /// That gives the preset a strict zero-residue path, using the same zero-regression argument as <see cref="ApplyTo"/>.
    ///
    /// Why compute disk radiance on the CPU instead of letting the shader resample the Transmittance LUT:
    /// the disk average needs 32 transmittance evaluations (see <see cref="EvaluateDiskTransmittance"/>),
    /// yet there is only one body direction per frame, so doing it per pixel would just waste work. More importantly,
    /// this path shares **the exact same** evaluation with the application's direct-light intensity solve
    /// via the two-slot memoization, which naturally guarantees that the visible solar disk in the sky and the direct light on the ground
    /// fade together at the same speed. If the two sides sampled independently, sunset would inevitably produce contradictions
    /// such as "the sun disk is still visible in the sky while the ground is already dark."
    /// </summary>
    /// <param name="lightParams">Target lighting-UBO mirror held by each backend's <c>SetLighting</c> path.</param>
    public static void Apply(ref SceneLightParams lightParams)
    {
        // 2-5 Step E intentionally runs before the early-return below: it has its own gate and must write ApParams0 back to zero when unavailable.
        // If it were placed after the early return, a runtime downgrade of SkyMode (Procedural -> StaticCube) would leave consumers holding
        // the previous frame's non-zero ApParams0.x and sampling a volume that has already been released, matching the same "do not rely on early return alone" argument as ApplyClouds.
        ApplyAerial(ref lightParams);

        if (FrameSchedule.SkyViewTexture == null)
        {
            // [SkyDebug] Fallback-preset diagnostic: SkyViewTexture=null means the procedural sky is not active (no analytic sun/moon disks or starfield).
            if (_skyDebugEarlyExitCount < 3 || _skyDebugEarlyExitCount % 600 == 0)
                DeviceServices.BaseApp.AddLog(LogType.Backend,
                    $"[SkyDebug] Apply EARLY-EXIT SkyViewTexture=null -> StaticCube preset (cubemap + marker sphere, no starfield) cnt={_skyDebugEarlyExitCount}");
            _skyDebugEarlyExitCount++;
            return;
        }

        const float deg2Rad = MathF.PI / 180f;

        var sunDir = SafeNormalize(Atmosphere.SunDirection, Vector3.UnitY);
        var moonDir = SafeNormalize(Atmosphere.MoonDirection, -Vector3.UnitY);
        float sunRadius = Atmosphere.SunAngularRadiusDeg * deg2Rad;
        float moonRadius = Atmosphere.MoonAngularRadiusDeg * deg2Rad;

        // The disk-center mu is simply the direction's Y component (local up is always +Y, and Sun/MoonDirection points toward the body).
        var sunT = EvaluateDiskTransmittance(ViewRadiusKm, sunDir.Y, sunRadius);
        var moonT = EvaluateDiskTransmittance(ViewRadiusKm, moonDir.Y, moonRadius);

        // Star visibility: sun elevation 0 degrees -> 0 (fully hidden), dropping to -<c>Atmosphere.StarVisibilityTwilightDeg</c> -> 1 (fully visible).
        // Use smoothstep instead of a linear ramp so the first derivative is zero at both ends and the starfield does not show a visible "sudden brighten" kink on fade-in/out.
        float sunElevDeg = MathF.Asin(Math.Clamp(sunDir.Y, -1f, 1f)) / deg2Rad;
        float k = Saturate(-sunElevDeg / MathF.Max(Atmosphere.StarVisibilityTwilightDeg, 1e-3f));
        float starVisibility = k * k * (3f - 2f * k);

        lightParams.SkyParams0 = new Vector4(sunDir, MathF.Cos(sunRadius));
        lightParams.SkyParams1 = new Vector4(Atmosphere.SunDiskRadiance * Atmosphere.SunColor * sunT,
            Atmosphere.StarRadiance * starVisibility);
        lightParams.SkyParams2 = new Vector4(moonDir, MathF.Cos(moonRadius));
        lightParams.SkyParams3 = new Vector4(Atmosphere.MoonDiskRadiance * Atmosphere.MoonColor * moonT,
            Atmosphere.StarRotation);

        // Step C: diurnal rotation axis for the starfield (celestial pole axis). Normalize it here at the single injection point so the shader always receives a unit vector
        // (Rodrigues inverse rotation silently becomes non-orthogonal for a non-unit axis, which stretches the star map).
        // When the application does not provide a value (zero vector), fall back to +Y to preserve the old Step B behavior instead of degenerating into an identity-zero transform.
        //
        // The w component was previously reserved; Step C assigns it to clouds as the observer radius from the planet center (km).
        // Visible-cloud positioning uses ray/spherical-shell intersections rather than a plane, so the pixel shader must know this radius.
        // Keeping it here instead of opening another float4 makes sense because the slot was unused and belongs to the same "planet geometry" family as the pole axis.
        // Do not hardcode 6360 in the shader: GroundRadiusKm and ViewAltitudeKm are runtime knobs on Atmosphere, and hardcoding would create a second source of truth.
        lightParams.SkyParams4 = new Vector4(SafeNormalize(Atmosphere.StarPoleAxis, Vector3.UnitY), ViewRadiusKm);

        // [SkyDebug] Starfield-visibility diagnosis: log injected values every 90 frames in deep night (starVis>0.3) and every 900 frames otherwise.
        _skyDebugFrame++;
        if (_skyDebugFrame % (starVisibility > 0.3f ? 90 : 900) == 0)
            DeviceServices.BaseApp.AddLog(LogType.Backend,
                $"[SkyDebug] Apply f={_skyDebugFrame} sunElev={sunElevDeg:F2}° starVis={starVisibility:F3} " +
                $"SkyP0.w={lightParams.SkyParams0.W:F5} SkyP1.w={lightParams.SkyParams1.W:F4} " +
                $"starRot={lightParams.SkyParams3.W:F3} pole=({lightParams.SkyParams4.X:F3},{lightParams.SkyParams4.Y:F3},{lightParams.SkyParams4.Z:F3}) " +
                $"viewR={lightParams.SkyParams4.W:F0}");

        ApplyClouds(ref lightParams, sunDir, moonDir);
    }

    /// <summary>
    /// 2-5 Step C: fold <see cref="Atmosphere.Clouds"/> into the tail of the UBO as CloudLayerA/B[3] plus CloudParams0/1.
    /// This is split out from the tail of <see cref="Apply"/> only for readability and is not an independent injection point.
    ///
    /// The gate requires both <c>FrameSchedule.CloudNoiseTexture != null</c> and a layer count &gt; 0.
    /// If either condition fails, <c>CloudParams0</c> must be explicitly zeroed.
    /// We cannot rely on an early return alone: when the weather switches from Fair back to Clear, failing to write zero would leave consumers
    /// reading the previous layer count forever, so the clouds would never fully disappear.
    ///
    /// Cloud lighting is computed once at this single point and shared by all layers as one color:
    /// evaluate sun/moon transmittance once at the average cloud height of all layers, so clouds naturally shift from white to orange-red at sunset,
    /// then to moonlight after sunset, then down to the ambient floor after moonset.
    /// This intentionally avoids per-layer lighting, which would need three colors or three float4 slots,
    /// while the visible difference is limited to the short twilight window where low clouds are already gray and high clouds are still red.
    /// If that ever becomes necessary, we can extend the UBO then.
    /// </summary>
    static void ApplyClouds(ref SceneLightParams lightParams, Vector3 sunDir, Vector3 moonDir)
    {
        int count = FrameSchedule.CloudNoiseTexture == null
            ? 0
            : Math.Clamp(Atmosphere.Clouds.LayerCount, 0, SkyState.MaxLayers);

        if (count <= 0)
        {
            lightParams.CloudParams0 = Vector4.Zero;
            return;
        }

        float meanAltKm = 0f;
        for (int i = 0; i < count; i++)
            meanAltKm += MathF.Max(Atmosphere.Clouds.Layers[i].AltitudeKm, MinCloudAltitudeKm);
        meanAltKm /= count;

        // Celestial-body transmittance at the cloud-shell radius (ground hits return 0, so after the body sets the clouds automatically fall back to ambient only, with no day/night branch needed).
        float rCloud = ViewRadiusKm + meanAltKm;
        var sunT = EvaluateTransmittance(rCloud, sunDir.Y);
        var moonT = EvaluateTransmittance(rCloud, moonDir.Y);

        // Lambert cloud shell: L = albedo x E x T / pi. Intentionally do not multiply by cos(zenith angle):
        // that term represents irradiance received by a horizontal plane, but clouds are volumetric and are lit from the side at sunset,
        // which is exactly why sunset clouds remain bright. Multiplying by cos would incorrectly crush them toward black.
        // The fade after the body drops below the horizon is already handled by transmittance, so no second directional attenuation is needed here.
        var illum = Atmosphere.SunIrradiance * Atmosphere.SunColor * sunT
            + Atmosphere.MoonIrradiance * Atmosphere.MoonColor * moonT;
        var cloudColor = Atmosphere.Clouds.Albedo * illum * (1f / MathF.PI);

        for (int i = 0; i < count; i++)
        {
            var layer = Atmosphere.Clouds.Layers[i];
            lightParams.CloudLayerA[i] = new Vector4(
                MathF.Max(layer.AltitudeKm, MinCloudAltitudeKm),
                MathF.Max(layer.ThicknessKm, 0f),
                Saturate(layer.Coverage),
                MathF.Max(layer.Density, 0f));

            var off = Atmosphere.CloudWindOffsetKm[i];
            lightParams.CloudLayerB[i] = new Vector4(
                off.X,
                off.Y,
                1f / MathF.Max(layer.TileKm, 1e-3f),
                Saturate(layer.Detail));
        }

        lightParams.CloudParams0 = new Vector4(cloudColor, count);
        lightParams.CloudParams1 = new Vector4(
            Saturate(Atmosphere.Clouds.ShadowStrength),
            Math.Clamp(Atmosphere.Clouds.PhaseG, 0f, 0.95f),
            Saturate(Atmosphere.Clouds.AmbientFloor),
            MathF.Max(Atmosphere.Clouds.ForwardGain, 0f));
    }

    /// <summary>
    /// 2-5 Step E: upload the single float4 used for aerial perspective (<see cref="SceneLightParams.ApParams0"/>).
    ///
    /// The gate is <c>FrameSchedule.AerialLutTexture != null</c>, the only criterion for "aerial perspective available".
    /// The three cases of non-procedural sky mode, <c>AerialPerspective</c> disabled, or 3D texture creation failure all collapse to this single check.
    /// When unavailable we must explicitly write zero instead of skipping the write, because <c>lightParams</c> is a frame-skipping mirror held by the application layer.
    /// Otherwise it would keep the previous non-zero gate value and sample a volume that no longer exists, exactly like the argument in <c>ApplyClouds</c>.
    ///
    /// The far distance must use the **same knob** as the bake side: it defines the absolute slice distance during baking,
    /// and serves as the denominator for inverse z normalization on the consumer side. If they differ, the whole volume shifts along depth
    /// and distant mountain fog appears several kilometers too early or too late.
    /// The lower clamp matches <c>SkyAtmosphereEffect.Record</c> character for character, using the same 0.01 km divide-by-zero guard.
    /// </summary>
    static void ApplyAerial(ref SceneLightParams lightParams)
    {
        if (FrameSchedule.AerialLutTexture == null)
        {
            lightParams.ApParams0 = Vector4.Zero;
            return;
        }

        lightParams.ApParams0 = new Vector4(
            MathF.Max(0.01f, RenderQuality.Current.AerialMaxDistanceKm),
            MathF.Max(0f, RenderQuality.Current.AerialIntensity),
            0f, 0f);
    }

    // Lower bound for cloud-base altitude (km): the cloud shell must stay strictly above the observer,
    // otherwise the ray/shell intersection degenerates (if the observer lies on or outside the shell, t can land on the back side or go negative and clouds flip below the feet). 50 m is enough to stay away from the degeneracy.
    const float MinCloudAltitudeKm = 0.05f;

    /// <summary>
    /// Ground radiance seen when the view ray hits the planet surface (Lambert bounce, linear HDR); returns zero when the ray misses the ground.
    ///
    /// **Used only for SH9 projection**: the <c>skyView</c> kernel does not include this term because the GPU side draws the ground with scene geometry,
    /// so it is intentionally kept out of <see cref="SkyRadiance"/>, which must remain formula-for-formula identical to the kernel.
    /// SH9, however, **must** include the ground bounce. Otherwise the lower hemisphere keeps only a thin strip of near-horizon glow,
    /// the DC term becomes noticeably too low, and objects are lit only from above with the reflected ground fill missing.
    /// In measurement (sun elevation 60 degrees, albedo 0.1), lower-hemisphere bounce radiance (0.323, 0.314, 0.292)
    /// is on the same order as sky DC (0.250, 0.327, 0.473), so it is not negligible.
    ///
    /// Body transmittance uses the **analytic** path here instead of the coarse LUT:
    /// at the ground, the body's cosine zenith angle during sunset lands exactly in the near-horizon region where the LUT has its largest error (see class header),
    /// and only about half the directions in one sphere hit the ground, so the analytic cost is negligible.
    ///
    /// The incident side has two terms: celestial-body **direct** light plus sky **diffuse** light,
    /// where the latter comes from the previously published SH9 via <see cref="EvaluateIrradianceSh9"/>.
    /// We cannot drop the diffuse term because its ratio to the direct term equals
    /// ground sky irradiance / ground direct irradiance and is **independent of albedo** since both terms are linear in albedo.
    /// Measured at sun elevation 60 degrees, the ratio is about 8% in red and about 20% in blue
    /// because Rayleigh scattering makes the sky much bluer than direct sunlight. At sunset, and in Step D's overcast cases where direct light is extinguished,
    /// that ratio only gets larger. Including this term raises the measured DC blue component from 0.44853 to 0.47272 (+5.4%) and red by +2.3%.
    ///
    /// Using the previous SH9 breaks the circular dependency:
    /// this term needs the sky irradiance at the ground hit point, while sky irradiance itself already includes ground bounce.
    /// That is equivalent to expanding the infinite ground<->sky bounce series into a frame-by-frame geometric iteration whose ratio is about
    /// albedo times ground solid-angle coverage, which is &lt; 1 and therefore convergent.
    /// Measured at albedo 0.1, the second pass differs by 2.7e-2, the third by 7.7e-6, and from the fourth onward it is a strict fixed point.
    /// Environment light is low frequency, so this frame-to-frame iteration is visually invisible; before the first SH9 is ready this term is zero and catches up in one cycle.
    ///
    /// Two known approximations remain:
    /// 1) use the SH9 at the **observer** position to approximate the sky irradiance at the ground hit point
    /// (for grazing rays the hit point can be tens of kilometers away, but the DC-dominated low-frequency quantity is not very sensitive to that);
    /// 2) <see cref="Atmosphere.GroundAlbedo"/> is the albedo of the **ideal spherical ground in the atmospheric model**
    /// and also feeds the MS LUT ground bounce, so it is unrelated to the actual ground material in the scene.
    /// If the two differ a lot, lower-hemisphere environment light will not match the ground brightness in the rendered frame.
    /// When DDGI is enabled, the probes take over environment light through the consumer-side three-way gate and this gap does not appear.
    /// </summary>
    /// <param name="dir">Normalized world-space view direction (local up is always +Y).</param>
    public static Vector3 GroundRadiance(Vector3 dir)
    {
        float rg = Atmosphere.GroundRadiusKm;
        float r0 = ViewRadiusKm;
        if (!HitsGround(r0, dir.Y, rg))
            return Vector3.Zero;

        // Local up at the hit point (for grazing ground hits it already differs noticeably from the observer up, so compute it from the actual intersection, matching the MS kernel).
        float tGround = MathF.Max(RaySphere(r0, dir.Y, rg), 0f);
        var hit = new Vector3(0f, r0, 0f) + dir * tGround;
        var up = hit / MathF.Max(hit.Length(), 1e-6f);

        var irradiance = GroundIrradiance(up, SafeNormalize(Atmosphere.SunDirection, Vector3.UnitY),
                             Atmosphere.SunColor * Atmosphere.SunIrradiance)
                       + GroundIrradiance(up, SafeNormalize(Atmosphere.MoonDirection, -Vector3.UnitY),
                             Atmosphere.MoonColor * Atmosphere.MoonIrradiance);

        // Sky diffuse term: EvaluateIrradianceSh9 returns E/pi, so multiply by pi to recover irradiance and match the direct-light units.
        if (_sh9Ready)
            irradiance += EvaluateIrradianceSh9(up) * MathF.PI;

        return irradiance * (Atmosphere.GroundAlbedo * InvPi);
    }

    /// <summary>
    /// Reconstruct diffuse irradiance for normal n from the latest SH9 coefficients and return **E(n)/pi**,
    /// matching the pre-multiplied coefficient convention so callers can multiply by albedo directly as an ambient diffuse term.
    /// The implementation stays line-for-line aligned with the backend shaders' <c>EvaluateIrradianceSH9</c>
    /// and applies the same max(0,·) clamp because second-order SH reconstruction can ring below zero.
    ///
    /// Uses: the sky-diffuse incident term in <see cref="GroundRadiance"/>, and CPU-side b10 validation of object lighting color
    /// without needing screenshot sampling to answer questions like whether object lighting becomes warmer at sunset. Returns zero until ready.
    /// </summary>
    public static Vector3 EvaluateIrradianceSh9(Vector3 n)
    {
        var c = _irradianceSh9;
        var result = Rgb(c[0]);
        result += Rgb(c[1]) * n.Y;
        result += Rgb(c[2]) * n.Z;
        result += Rgb(c[3]) * n.X;
        result += Rgb(c[4]) * (n.X * n.Y);
        result += Rgb(c[5]) * (n.Y * n.Z);
        result += Rgb(c[6]) * (3f * n.Z * n.Z - 1f);
        result += Rgb(c[7]) * (n.X * n.Z);
        result += Rgb(c[8]) * (n.X * n.X - n.Y * n.Y);
        return Vector3.Max(result, Vector3.Zero);
    }

    static Vector3 Rgb(Vector4 v) => new(v.X, v.Y, v.Z);

    /// <summary>Direct irradiance from a single celestial body at a ground point: transmittance x cos(incidence) x body radiance.
    /// Returns zero when the body is below the local horizon at that point (cos &lt;= 0), without double-counting the ground occlusion already present in transmittance.</summary>
    static Vector3 GroundIrradiance(Vector3 up, Vector3 lightDir, Vector3 lightRadiance)
    {
        float cosZ = Vector3.Dot(up, lightDir);
        if (cosZ <= 0f)
            return Vector3.Zero;

        return EvaluateTransmittance(Atmosphere.GroundRadiusKm, cosZ) * lightRadiance * cosZ;
    }

    /// <summary>Publish the accumulated result: balance the denominator, pre-multiply the Lambert convolution weights, and derive the radiance coefficients at the same time.
    /// Both the weight table and the ratio table are character-for-character identical to <c>EnvironmentMap</c>'s <c>ProjectIrradianceSH9</c> and <c>DeriveRadianceSH9</c>, which contain the closed-form derivation and term-by-term cross-check.</summary>
    static void PublishSh9()
    {
        float norm = _sh9OmegaAccum > 0f ? 4f * MathF.PI / _sh9OmegaAccum : 0f;

        const float invPi = 1f / MathF.PI;
        float wL0 = 0.25f * invPi;              // 1/4π
        float wL1 = 0.5f * invPi;               // 1/2π
        float wL2 = 15f / 16f * invPi;          // xy, yz, xz
        float wL2z = 5f / 64f * invPi;          // 3z²-1
        float wL2d = 15f / 64f * invPi;         // x²-y²

        Span<float> scale = stackalloc float[Sh9Count]
        {
            wL0, wL1, wL1, wL1, wL2, wL2, wL2z, wL2, wL2d
        };

        // Per-band irradiance -> radiance ratio pi/A_l, where A_l = {pi, 2pi/3, pi/4}.
        Span<float> ratio = stackalloc float[Sh9Count]
        {
            1f, 1.5f, 1.5f, 1.5f, 4f, 4f, 4f, 4f, 4f
        };

        for (int i = 0; i < Sh9Count; i++)
        {
            var irradiance = new Vector4(_sh9Accum[i] * (norm * scale[i]), 0f);
            _irradianceSh9[i] = irradiance;
            _radianceSh9[i] = irradiance * ratio[i];
        }

        _sh9Ready = true;
    }

    /// <summary>The i-th direction on the Fibonacci sphere (quasi-uniform, equal-solid-angle, no rejection sampling needed):
    /// use world +Y as the pole, distribute y by (2i+1)/N to preserve equal solid angle, and advance azimuth by the golden angle.
    /// Choosing Y as the pole makes the interleaved-by-stride subsets naturally cover all latitudes (see <see cref="AccumulateSh9"/>).</summary>
    static Vector3 FibonacciDirection(int i)
    {
        const float goldenAngle = 2.39996322972865332f;   // π(3−√5)

        float y = 1f - (2f * i + 1f) / Sh9DirectionCount;
        float radius = MathF.Sqrt(Saturate(1f - y * y));
        float phi = goldenAngle * i;
        return new Vector3(radius * MathF.Cos(phi), y, radius * MathF.Sin(phi));
    }

    // -- Analytic evaluation (the half with no LUT dependency; b8 uses this for texel-by-texel comparison against the GPU path). --

    /// <summary>
    /// Transmittance from radius <paramref name="r"/> and local-up cosine <paramref name="mu"/> up to the top of the atmosphere.
    /// This is the CPU mirror of the <c>skyTransmittance</c> kernel and stays formula-for-formula aligned.
    /// **Return 0 immediately on ground hit**, with planetary self-occlusion already baked in so consumers do not need a shadow ray.
    /// </summary>
    /// <param name="r">Observer radius in kilometers, measured from the planet center.</param>
    /// <param name="mu">Cosine of the angle between the ray and local up.</param>
    /// <param name="steps">Uniform integration step count; defaults to the GPU bake value <see cref="GpuTransmittanceSteps"/>.</param>
    public static Vector3 EvaluateTransmittance(float r, float mu, int steps = GpuTransmittanceSteps)
    {
        float rg = Atmosphere.GroundRadiusKm;
        float rt = Atmosphere.AtmosphereRadiusKm;
        if (HitsGround(r, mu, rg))
            return Vector3.Zero;

        var rayleigh = Atmosphere.RayleighScattering;
        float mieExt = Atmosphere.MieExtinction;
        float rayleighH = Atmosphere.RayleighHeightKm;
        float mieH = Atmosphere.MieHeightKm;

        int n = Math.Max(steps, 1);
        float tTop = MathF.Max(RaySphere(r, mu, rt), 0f);
        float dt = tTop / n;
        var optical = Vector3.Zero;

        for (int i = 0; i < n; i++)
        {
            float t = (i + 0.5f) * dt;
            float rr = MathF.Sqrt(MathF.Max(r * r + t * t + 2f * r * mu * t, 1e-6f));
            AirDensity(rr - rg, rayleighH, mieH, out float densR, out float densM);
            optical += (rayleigh * densR + new Vector3(mieExt * densM)) * dt;
        }

        return Exp(-optical);
    }

    // Two-slot memoization for <c>EvaluateDiskTransmittance</c>: one slot for the sun and one for the moon.
    // The same body is evaluated at **two** call sites within one frame
    // (the application's direct-light solve plus the disk radiance written to the main shader by <c>Apply</c>),
    // and the inputs are bitwise identical, which is exactly the requirement that "the visible disk in the sky and the lighting on the ground come from the same source".
    // So the second lookup always hits and the steady-state cost remains only two real evaluations per frame.
    // The key stores all four inputs and only hits on exact equality; no tolerance is allowed, because fuzzy hits would turn the final sunset slope back into steps.
    // Static atmosphere changes are invalidated wholesale by <c>Rebake</c>, so this method must start with <c>Update</c>.
    const int DiskMemoSlots = 2;
    static readonly float[] _diskMemoKey = new float[DiskMemoSlots * 4];
    static readonly Vector3[] _diskMemoVal = new Vector3[DiskMemoSlots];
    static int _diskMemoNext;

    /// <summary>
    /// Average transmittance **across the disk** of a celestial body:
    /// sample the disk of angular radius <paramref name="angularRadiusRad"/> with <see cref="DiskRings"/> x <see cref="DiskSpokes"/> equal-solid-angle points,
    /// evaluate <see cref="EvaluateTransmittance"/> at each point, and take the arithmetic mean. Equal-area sampling means no extra weights are needed.
    ///
    /// The only reason this exists is the **horizon-ingestion transition**:
    /// <see cref="EvaluateTransmittance"/> returns a hard 0 for rays that hit the ground because planetary self-occlusion is baked in,
    /// so a single evaluation at the disk center would turn the whole body off instantly as soon as that center crosses the horizon.
    /// With the repository defaults, observer radius r = 6360.2 km gives horizon mu = -sqrt(1-(Rg/r)^2) ~= -0.00793,
    /// and the solar angular radius 0.2665 degrees = 0.00465 rad means the solar disk starts touching the horizon around mu ~= -0.0033
    /// and is fully gone only near -0.0126, so delta-mu ~= 0.0093, which is exactly the scale of a real "solar diameter taking about two minutes to set".
    /// Averaging across the disk turns that span into a continuous slope; a single sample compresses it into a one-frame step.
    ///
    /// The two consumers must use the same source curve, which is also why this method runs only once:
    /// direct-light intensity in the application layer (<c>ApplyBodyTransmittance</c>) and disk radiance for the analytic main-shader sun disk
    /// must share one curve, otherwise contradictions appear such as the solar disk still being visible in the sky while the ground is already dark.
    /// </summary>
    /// <param name="r">Observer radius in kilometers, measured from the planet center.</param>
    /// <param name="muCenter">Cosine between the **disk-center** direction and local up.</param>
    /// <param name="angularRadiusRad">Angular radius of the body in radians; &lt;= 0 degenerates to a single center sample.</param>
    /// <param name="steps">Transmittance integration step count; defaults to the GPU bake value <see cref="GpuTransmittanceSteps"/>.</param>
    public static Vector3 EvaluateDiskTransmittance(float r, float muCenter, float angularRadiusRad,
                                                   int steps = GpuTransmittanceSteps)
    {
        if (angularRadiusRad <= 0f)
            return EvaluateTransmittance(r, muCenter, steps);

        Update();   // Ensure static-parameter changes have invalidated the memoization below through Rebake; this also triggers the first bake on the first frame.

        for (int s = 0; s < DiskMemoSlots; s++)
        {
            int k = s * 4;
            if (_diskMemoKey[k] == r && _diskMemoKey[k + 1] == muCenter
                && _diskMemoKey[k + 2] == angularRadiusRad && _diskMemoKey[k + 3] == steps)
                return _diskMemoVal[s];
        }

        const float goldenAngle = 2.39996322972865332f;   // Same as FibonacciDirection: stagger azimuth per ring.

        // Sine of the disk-center zenith angle: for a point with in-disk offset theta and azimuth phi
        // (phi measured from the side facing zenith), the spherical law of cosines gives:
        //   mu = muCenter·cosθ + sin(zenith)·sinθ·cosφ
        float sinZ = MathF.Sqrt(Saturate(1f - muCenter * muCenter));
        var sum = Vector3.Zero;

        for (int i = 0; i < DiskRings; i++)
        {
            // Equal-area radial placement: theta proportional to sqrt((i+0.5)/N) makes each ring sample represent the same disk area (for small angles dω ~= theta dtheta dphi).
            float theta = angularRadiusRad * MathF.Sqrt((i + 0.5f) / DiskRings);
            float cosT = MathF.Cos(theta);
            float sinT = MathF.Sin(theta);

            for (int j = 0; j < DiskSpokes; j++)
            {
                float phi = MathF.Tau * (j + 0.5f) / DiskSpokes + i * goldenAngle;
                float mu = muCenter * cosT + sinZ * sinT * MathF.Cos(phi);
                sum += EvaluateTransmittance(r, mu, steps);
            }
        }

        var result = sum / (DiskRings * DiskSpokes);

        int slot = _diskMemoNext;
        _diskMemoNext = (slot + 1) % DiskMemoSlots;
        _diskMemoKey[slot * 4] = r;
        _diskMemoKey[slot * 4 + 1] = muCenter;
        _diskMemoKey[slot * 4 + 2] = angularRadiusRad;
        _diskMemoKey[slot * 4 + 3] = steps;
        _diskMemoVal[slot] = result;
        return result;
    }

    /// <summary>
    /// Multi-scattering transfer function psi_ms, the CPU mirror of the <c>skyMultiScatter</c> kernel:
    /// take <see cref="MultiScatterSqrtSamples"/>^2 uniform spherical directions around the observer,
    /// march each direction while accumulating first-order isotropic scattering L1 (including Lambert ground bounce) and the transfer term f_ms,
    /// then average over the sphere and take the geometric-series sum psi_ms = L1/(1-f_ms), clamping f_ms to 0.999 to avoid division by zero.
    /// The result is normalized against unit irradiance and white light, so it is independent of sun/moon pose.
    /// Consumers reconstruct the exact result by multiplying their own <c>*Color x *Irradiance</c>, thanks to per-channel linear atmospheric transport.
    /// </summary>
    /// <param name="r">Observer radius in kilometers.</param>
    /// <param name="cosZ">Cosine between the light direction and local up.</param>
    /// <param name="steps">Steps per ray; defaults to the GPU value <see cref="GpuMultiScatterSteps"/>.</param>
    /// <param name="analyticTransmittance">true means internal body transmittance uses analytic <see cref="EvaluateTransmittance"/>
    /// evaluation, which b8 needs for value-by-value comparison and to bypass LUT-resolution differences between CPU and GPU;
    /// false means sample the CPU coarse LUT to match the GPU kernel behavior, which samples the Transmittance LUT.</param>
    public static Vector3 EvaluateMultiScatter(float r, float cosZ,
        int steps = GpuMultiScatterSteps, bool analyticTransmittance = false)
    {
        float rg = Atmosphere.GroundRadiusKm;
        float rt = Atmosphere.AtmosphereRadiusKm;
        var rayleigh = Atmosphere.RayleighScattering;
        float mieScat = Atmosphere.MieScattering;
        float mieExt = Atmosphere.MieExtinction;
        float rayleighH = Atmosphere.RayleighHeightKm;
        float mieH = Atmosphere.MieHeightKm;
        float albedo = Atmosphere.GroundAlbedo;

        // Local coordinates: observer at (0,r,0), up direction = +Y. Light azimuth does not matter because the problem is rotationally symmetric around Y, so place it on the YZ plane.
        var origin = new Vector3(0f, r, 0f);
        var lightDir = new Vector3(0f, cosZ, MathF.Sqrt(Saturate(1f - cosZ * cosZ)));
        int n = Math.Max(steps, 1);

        var lSum = Vector3.Zero;
        var fSum = Vector3.Zero;

        for (int sy = 0; sy < MultiScatterSqrtSamples; sy++)
        {
            for (int sx = 0; sx < MultiScatterSqrtSamples; sx++)
            {
                // Stratified uniform sphere directions (uniform azimuth plus uniform cosine gives equal solid-angle weights).
                float a = (sx + 0.5f) / MultiScatterSqrtSamples;
                float b = (sy + 0.5f) / MultiScatterSqrtSamples;
                float theta = MathF.Tau * a;
                float dy = 1f - 2f * b;
                float dxz = MathF.Sqrt(Saturate(1f - dy * dy));
                var dir = new Vector3(dxz * MathF.Cos(theta), dy, dxz * MathF.Sin(theta));

                bool ground = HitsGround(r, dir.Y, rg);
                float tMax = ground ? MathF.Max(RaySphere(r, dir.Y, rg), 0f)
                                    : MathF.Max(RaySphere(r, dir.Y, rt), 0f);
                float dt = tMax / n;

                var throughput = Vector3.One;
                var lDir = Vector3.Zero;
                var fDir = Vector3.Zero;

                for (int i = 0; i < n; i++)
                {
                    float t = (i + 0.5f) * dt;
                    var pos = origin + dir * t;
                    float rr = MathF.Max(pos.Length(), 1e-6f);
                    var upLocal = pos / rr;
                    AirDensity(rr - rg, rayleighH, mieH, out float densR, out float densM);

                    var sigmaS = rayleigh * densR + new Vector3(mieScat * densM);
                    var sigmaE = Vector3.Max(rayleigh * densR + new Vector3(mieExt * densM), new Vector3(1e-7f));
                    var stepT = Exp(-sigmaE * dt);

                    float cosL = Vector3.Dot(upLocal, lightDir);
                    var tLight = analyticTransmittance
                        ? EvaluateTransmittance(rr, cosL)
                        : SampleTransmittanceLut(rr, cosL);

                    // First-order scattering source: unit irradiance, isotropic phase.
                    var s1 = sigmaS * InvFourPi * tLight;
                    lDir += throughput * (s1 - s1 * stepT) / sigmaE;

                    // Transfer term: for isotropic radiance 1 everywhere, the scattering source is exactly sigma_s because the phase integrates to 1 over the sphere.
                    fDir += throughput * (sigmaS - sigmaS * stepT) / sigmaE;

                    throughput *= stepT;
                }

                // Ground bounce: the Lambert surface sends the received direct light back into the atmosphere.
                if (ground)
                {
                    var gp = origin + dir * tMax;
                    var gn = gp / MathF.Max(gp.Length(), 1e-6f);
                    float cosG = Vector3.Dot(gn, lightDir);
                    var tg = analyticTransmittance
                        ? EvaluateTransmittance(rg, cosG)
                        : SampleTransmittanceLut(rg, cosG);
                    lDir += throughput * tg * (albedo * InvPi) * Saturate(cosG);
                }

                lSum += lDir;
                fSum += fDir;
            }
        }

        // Sphere average (weight 4pi/N times isotropic phase 1/4pi = 1/N), then take the geometric-series sum to get infinite-order scattering.
        float invN = 1f / (MultiScatterSqrtSamples * MultiScatterSqrtSamples);
        var l1 = lSum * invN;
        var fms = Vector3.Min(fSum * invN, new Vector3(0.999f));
        return l1 / (Vector3.One - fms);
    }

    // -- Coarse LUT sampling (bilinear + clamp addressing, mirroring the GPU s0 linear-clamp static sampler). --

    /// <summary>Sample the CPU coarse Transmittance LUT, with parameterization identical to the GPU path: u=(mu+1)/2, v=(r-Rg)/(Rt-Rg).
    /// Returns zero before baking, meaning full occlusion, so callers must first pass through <see cref="Update"/>.</summary>
    public static Vector3 SampleTransmittanceLut(float r, float mu)
    {
        float rg = Atmosphere.GroundRadiusKm;
        float span = MathF.Max(Atmosphere.AtmosphereRadiusKm - rg, 1e-6f);
        return Bilinear(_transLut, TransmittanceLutWidth, TransmittanceLutHeight,
            mu * 0.5f + 0.5f, (r - rg) / span);
    }

    /// <summary>Sample the CPU coarse multi-scattering LUT, using the same UV parameterization as Transmittance; see the GPU kernel summary.</summary>
    public static Vector3 SampleMultiScatterLut(float r, float cosZ)
    {
        float rg = Atmosphere.GroundRadiusKm;
        float span = MathF.Max(Atmosphere.AtmosphereRadiusKm - rg, 1e-6f);
        return Bilinear(_msLut, MultiScatterLutWidth, MultiScatterLutHeight,
            cosZ * 0.5f + 0.5f, (r - rg) / span);
    }

    static Vector3 Bilinear(Vector3[] lut, int w, int h, float u, float v)
    {
        // Texel centers are at (i+0.5)/W, so subtract half a texel from continuous coordinates before flooring to match GPU bilinear sampling bit-for-bit.
        float x = Saturate(u) * w - 0.5f;
        float y = Saturate(v) * h - 0.5f;
        int x0 = (int)MathF.Floor(x);
        int y0 = (int)MathF.Floor(y);
        float fx = x - x0;
        float fy = y - y0;

        int xa = Math.Clamp(x0, 0, w - 1);
        int xb = Math.Clamp(x0 + 1, 0, w - 1);
        int ya = Math.Clamp(y0, 0, h - 1);
        int yb = Math.Clamp(y0 + 1, 0, h - 1);

        var top = Vector3.Lerp(lut[ya * w + xa], lut[ya * w + xb], fx);
        var bottom = Vector3.Lerp(lut[yb * w + xa], lut[yb * w + xb], fx);
        return Vector3.Lerp(top, bottom, fy);
    }

    /// <summary>Rebake both CPU LUTs as one batch. The order is fixed: the multi-scatter bake depends on the already-ready Transmittance LUT,
    /// mirroring the same per-batch kernel dependency as the GPU side.</summary>
    static void Rebake()
    {
        float rg = Atmosphere.GroundRadiusKm;
        float span = Atmosphere.AtmosphereRadiusKm - rg;

        for (int y = 0; y < TransmittanceLutHeight; y++)
        {
            float r = rg + (y + 0.5f) / TransmittanceLutHeight * span;
            for (int x = 0; x < TransmittanceLutWidth; x++)
            {
                float mu = (x + 0.5f) / TransmittanceLutWidth * 2f - 1f;
                _transLut[y * TransmittanceLutWidth + x] = EvaluateTransmittance(r, mu, TransmittanceLutSteps);
            }
        }

        // SampleTransmittanceLut is valid from this point onward; the MS bake depends on it, matching the GPU kernel's LUT-sampling behavior.
        for (int y = 0; y < MultiScatterLutHeight; y++)
        {
            float r = rg + (y + 0.5f) / MultiScatterLutHeight * span;
            for (int x = 0; x < MultiScatterLutWidth; x++)
            {
                float cosZ = (x + 0.5f) / MultiScatterLutWidth * 2f - 1f;
                _msLut[y * MultiScatterLutWidth + x] = EvaluateMultiScatter(r, cosZ, MultiScatterLutSteps);
            }
        }

        CaptureStaticParams();

        // Invalidate disk-average memoization (NaN keys never hit): it caches analytic transmittance, which changes with static atmosphere parameters.
        Array.Fill(_diskMemoKey, float.NaN);
    }

    /// <summary>Rebake criterion with eight items, aligned item-by-item with <c>SkyAtmosphereEffect.StaticParamsChanged</c>.
    /// <c>MiePhaseG</c> is intentionally excluded for the same reason: both LUTs depend only on extinction and isotropic phase.</summary>
    static bool StaticParamsChanged()
        => _bakedRayleigh != Atmosphere.RayleighScattering
        || _bakedRayleighH != Atmosphere.RayleighHeightKm
        || _bakedMieExt != Atmosphere.MieExtinction
        || _bakedMieH != Atmosphere.MieHeightKm
        || _bakedGround != Atmosphere.GroundRadiusKm
        || _bakedTop != Atmosphere.AtmosphereRadiusKm
        || _bakedMieScat != Atmosphere.MieScattering
        || _bakedAlbedo != Atmosphere.GroundAlbedo;

    static void CaptureStaticParams()
    {
        _bakedRayleigh = Atmosphere.RayleighScattering;
        _bakedRayleighH = Atmosphere.RayleighHeightKm;
        _bakedMieExt = Atmosphere.MieExtinction;
        _bakedMieH = Atmosphere.MieHeightKm;
        _bakedGround = Atmosphere.GroundRadiusKm;
        _bakedTop = Atmosphere.AtmosphereRadiusKm;
        _bakedMieScat = Atmosphere.MieScattering;
        _bakedAlbedo = Atmosphere.GroundAlbedo;
        _baked = true;
    }

    // -- Geometry and density: CPU mirrors of the three helper functions shared by the GPU kernels. --

    /// <summary>Nearest non-negative intersection distance between a ray that starts at radius r with local-up cosine mu and a concentric sphere of radius R.
    /// Returns -1 when there is no intersection.</summary>
    static float RaySphere(float r, float mu, float radius)
    {
        float disc = r * r * (mu * mu - 1f) + radius * radius;
        if (disc < 0f)
            return -1f;

        float sq = MathF.Sqrt(disc);
        float tNear = -r * mu - sq;
        return tNear >= 0f ? tNear : -r * mu + sq;
    }

    /// <summary>Whether the ray hits the ground. The mu&lt;0 test is required in addition to the discriminant,
    /// otherwise near-ground upward rays (r~=Rg) can be misclassified because the discriminant is barely non-negative, matching the same GPU pitfall.</summary>
    static bool HitsGround(float r, float mu, float rg)
        => mu < 0f && r * r * (mu * mu - 1f) + rg * rg >= 0f;

    /// <summary>Relative Rayleigh and Mie density at altitude h in kilometers, each using its own exponential scale height.</summary>
    static void AirDensity(float h, float rayleighH, float mieH, out float densR, out float densM)
    {
        float hc = MathF.Max(h, 0f);
        densR = MathF.Exp(-hc / rayleighH);
        densM = MathF.Exp(-hc / mieH);
    }

    static Vector3 Exp(Vector3 v) => new(MathF.Exp(v.X), MathF.Exp(v.Y), MathF.Exp(v.Z));

    static float Saturate(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

    static Vector3 SafeNormalize(Vector3 dir, Vector3 fallback)
    {
        float len = dir.Length();
        return len > 1e-5f ? dir / len : fallback;
    }
}
