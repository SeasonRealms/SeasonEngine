// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

/// <summary>
/// Engine built-in compute effect: procedural atmospheric-sky LUT baking
/// (2-5 Step A/B; see section 2-5 in the RenderQuality class header for the contract).
/// The physical model is a reduced Hillaire 2020 implementation: single scattering plus
/// a **multiple-scattering energy term** (completed in Step B), using Rayleigh + Mie dual
/// exponential density components, Cornette-Shanks / HG phase functions, and a Transmittance
/// LUT that also serves as planetary self-occlusion.
///
/// Behavior: FrameStart phase, which **must** happen before the scene pass because the skybox
/// is the first draw of that pass. If the LUT is produced one phase later, sampling would see
/// either the previous frame or uninitialized content. The effect uses five kernels:
/// 1. <c>skyTransmittance</c> -> 256x64 rgba16f: top-of-atmosphere transmittance for a given
///    (view zenith cosine mu, radius r).
///    It is rebaked **only when the static parameter snapshot changes** (including the first frame),
///    so steady state performs zero dispatches per frame.
/// 2. <c>skyMultiScatter</c> -> 32x32 rgba16f: multiple-scattering transfer function psi_ms
///    under the same (mu, r) parameterization.
///    This is **also statically baked**. It is normalized to unit irradiance + white light,
///    so it depends only on atmospheric parameters and planet geometry, not on sun/moon pose.
///    Atmospheric transport is linear per channel, so the consumer can multiply by each light's
///    <c>*Color * *Irradiance</c> and reconstruct the exact result rather than an approximation.
///    That is precisely why both lights can share the same LUT.
///    It is gated and rebaked together with Transmittance. This kernel reads Transmittance,
///    and implicit UAV->SRV state transitions provide synchronization.
/// 3. <c>skyView</c> -> <c>SkyViewLutWidth</c>x<c>SkyViewLutHeight</c> rgba16f:
///    full-sky radiance (single scattering + MS energy term), recomputed every frame
///    because the sun and moon move every frame.
/// 4. <c>skyCloudNoise</c> -> <c>CloudNoiseSize</c>^2 rgba8 (Step C):
///    four-channel tileable cloud noise.
///    It is baked **only once in its lifetime** because it depends on neither atmospheric
///    parameters nor sun/moon pose, so it does not participate in any rebake criteria.
///    It carries its own independent 16B Params block and does not touch the 128B block below
///    that already reaches the Vulkan push-constant minimum limit.
/// 5. <c>skyAerial</c> -> 32x32x32 rgba16f (Step E): **aerial perspective** froxel volume,
///    recomputed every frame.
///    It uses the same integrator as skyView; only the ray interval differs. skyView integrates
///    across the full sky direction to the top of the atmosphere, while this kernel integrates
///    only along the **camera frustum** to a finite distance and stores the accumulated value
///    for each distance slice separately.
///    As a result, any 3D surface pixel in the main shader can directly look up the in-scattering
///    and extinction of the atmosphere segment between the camera and that pixel using
///    (screen UV, distance). Distant mountains turning blue and horizon haze no longer require
///    an empirical fog formula.
///    It also owns its own independent 128B Params block (layout shown in the second table below)
///    and does not share the first three kernels' block.
///
/// The consumer adds no new binding slots:
/// <c>Surface.BaseColorTexturePath = SkyViewTextureName</c> +
/// <c>Surface.ProceduralSky = true</c>
/// makes the existing texture resolution path hit the compute texture dictionary by name, and
/// the main shader's <c>renderMode == 3</c> branch reuses sampling from t0 (albedoMap).
/// The root signature and descriptor tables stay unchanged.
/// Output is always linear HDR radiance. Per contract 1-4, shaders do not perform tonemapping
/// or clamping; the only convergence point is FinalBlit.
///
/// Binding layout (declaration order defines the cross-backend slot convention; see
/// ComputeBindingType summary; the first three kernels share the same 128B Params block):
/// skyTransmittance [0] Params(128B) [1] StorageWrite rgba16f
/// skyMultiScatter  [0] Params(128B) [1] SampledTexture(transmittance) [2] StorageWrite rgba16f
/// skyView          [0] Params(128B) [1] SampledTexture(transmittance) [2] SampledTexture(multiScatter) [3] StorageWrite rgba16f
/// skyCloudNoise    [0] Params(16B)  [1] StorageWrite rgba8unorm
/// skyAerial        [0] Params(128B) [1] SampledTexture(transmittance) [2] SampledTexture(multiScatter) [3] StorageTexture3DWrite rgba16f
///
/// Params layout (8 x float4 = 128B, **exactly filling the strict Vulkan push-constant minimum**
/// with no remaining space. The first three kernels interpret every field identically, with only
/// the values of uLut changing by target LUT):
/// <code>
/// uSun       xyz = unit vector pointing toward the sun,  w = sun irradiance scale
/// uSunColor  rgb = sun color in linear space,            a = nighttime airglow
/// uLut       x = LUT width, y = LUT height,              z = integration step count, w = multiple-scattering gain
/// uRayleigh  rgb = Rayleigh scattering (1/km),           a = Mie scattering (1/km)
/// uMie       x = Mie extinction, y = Rayleigh scale height, z = Mie scale height (km), w = HG g
/// uPlanet    x = ground radius, y = top-of-atmosphere radius, z = view altitude (km), w = ground albedo
/// uMoon      xyz = unit vector pointing toward the moon, w = moon irradiance scale
/// uMoonColor rgb = moon color in linear space,           a = reserved
/// </code>
/// If more parameters are needed later, **this block must not be expanded**. It already hits
/// the 128B limit, so new data must either use another kernel or move to a storage buffer.
/// Step C cloud noise and Step E aerial perspective both follow the former route by carrying
/// their own independent Params blocks.
///
/// skyAerial Params layout (also 8 x float4 = 128B, exactly full, no spare space):
/// <code>
/// uApSun      xyz = unit vector pointing toward the sun,  w = Rayleigh scale height (km)
/// uApSunRad   rgb = sun linear color * irradiance,        a = Mie scattering (1/km)
/// uApMoon     xyz = unit vector pointing toward the moon, w = Mie scale height (km)
/// uApMoonRad  rgb = moon linear color * irradiance,       a = Mie extinction (1/km)
/// uApRayleigh rgb = Rayleigh scattering (1/km),           a = HG g
/// uApPlanet   x = ground radius, y = top-of-atmosphere radius, z = view altitude (km), w = multiple-scattering gain
/// uApRight    xyz = camera right axis * tan(fovY/2) * aspect, w = farthest distance (km)
/// uApUp       xyz = camera up axis * tan(fovY/2),               w = reserved
/// </code>
/// Fitting this into 128B relies on three compressions, **none of which is an approximation**:
/// 1. Light color and irradiance are pre-multiplied into a single float3 on the CPU side.
///    The shader only ever uses their product anyway, so the freed alpha components can store scalars.
/// 2. LUT 3D dimensions and per-slice substep count are not uploaded.
///    The former is queried from <c>RWTexture3D::GetDimensions</c>, and the latter is an HLSL constant.
///    Changing it implies changing the shader, so it should not be a runtime knob in the first place.
///    That removes the entire uLut row.
/// 3. Only the two pre-scaled right/up axes are passed. Forward is recovered exactly from
///    <c>cross(normalize(right), normalize(up))</c>.
///    The engine camera uses a left-handed orthonormal basis
///    (<c>right = up x fwd</c>, <c>up = fwd x right</c>), and therefore
///    <c>right x up = fwd</c>. See the Camera3D class header.
/// airglow is intentionally excluded from this block. It is a uniform sky-light term, not part
/// of the contribution inside the view segment, and nighttime AP in-scattering is already two
/// orders of magnitude lower than daytime scattering, so the omission is visually negligible.
///
/// Step A was visually shaped on D3D12. From 2-5 onward, Vulkan (HLSL + GLSL), WebGPU
/// (HLSL + WGSL), and Metal (HLSL + MSL) all landed as well. Missing any source on any backend
/// makes CreateComputeKernel return null, leaving <c>FrameSchedule.SkyViewTexture</c> null,
/// which lets the application skybox fall back cleanly to the static cube texture with no residue.
/// </summary>
public sealed class SkyAtmosphereEffect : ComputeEffect
{
    /// <summary>Registered name of the Transmittance LUT. It can be attached directly to Sprite2D for debugging: mu on the X axis, radius on the Y axis, with a black ground-occlusion area expected in the lower half.</summary>
    public const string TransmittanceTextureName = "compute://sky/transmittance";

    /// <summary>Registered name of the multiple-scattering LUT. It can also be attached to Sprite2D for debugging and should appear as a low-frequency blue map, darker on the left and brighter toward the upper area.</summary>
    public const string MultiScatterTextureName = "compute://sky/multiscatter";

    /// <summary>Registered name of the Sky-View LUT. This is the sampled source for the main shader's renderMode == 3 branch, and it can also be attached to Sprite2D to inspect the full-sky unwrap.</summary>
    public const string SkyViewTextureName = "compute://sky/skyview";

    /// <summary>Transmittance LUT size: 256 along mu and 64 along radius. It stays fixed across quality levels because it is only a cache of a 2D analytic function and contributes no per-frame cost in steady state.</summary>
    public const uint TransmittanceWidth = 256;

    public const uint TransmittanceHeight = 64;

    /// <summary>Uniform integration step count for Transmittance baking. Forty steps are enough to push adjacent-LUT-texel error below rgba16f quantization, and since steady state does not rebake it, the cost can be generous.</summary>
    public const uint TransmittanceBakeSteps = 40;

    /// <summary>Edge length of the multiple-scattering LUT (Hillaire's original 32^2). psi_ms is an extremely low-frequency quantity, being the result of a sphere integral that has already smoothed out directional structure, so bilinear interpolation on 32^2 produces no visible error. It also determines total bake cost = 32^2 x 64 directions x 20 steps.</summary>
    public const uint MultiScatterSize = 32;

    /// <summary>Step count per ray for multiple-scattering baking (Hillaire's original value is 20). Because it is not rebaked in steady state, the cost can be generous.</summary>
    public const uint MultiScatterBakeSteps = 20;

    /// <summary>2-5 Step C: registered name of the prebaked cloud-noise texture. It can also be attached to Sprite2D for debugging: R should show low-frequency shape, G the fluffy structure, B the high-frequency erosion, and A the ultra-low-frequency coverage modulation, with both horizontal and vertical edges tiling seamlessly.</summary>
    public const string CloudNoiseTextureName = "compute://sky/cloudnoise";

    /// <summary>Number of grid cells covered by the lowest-frequency octave of the cloud noise within one tile period. Four octaves therefore use 4 / 8 / 16 / 32 cells, so the largest cloud body occupies one quarter of a tile period (about 3 km when <c>TileKm</c> = 12 km, i.e. cumulus scale). This intentionally does not change with texture size: the feature scale is fixed in UV space, so quality levels only change texture resolution and detail sharpness rather than making all clouds globally larger or smaller.</summary>
    public const uint CloudNoiseBasePeriod = 4;

    /// <summary>2-5 Step E: registered name of the aerial-perspective 3D LUT. It lives in the dedicated 3D dictionary, where the `compute3d://` prefix is the 1-8 naming convention. Because of that, Sprite2D cannot display it directly; a 3D-to-2D slice kernel is needed as a visualization bridge.</summary>
    public const string AerialLutTextureName = "compute3d://sky/aerial";

    /// <summary>Edge length of the aerial-perspective froxel volume (Hillaire's original 32 x 32 x 32). It intentionally stays fixed across quality levels: AP is a very low-frequency quantity because atmospheric in-scattering naturally lacks high-frequency screen-space structure, so trilinear interpolation on 32^3 already produces no visible error. Its per-frame cost is also too low to justify quality scaling: 1024 threads total, each marching one full column of 32 slices times <c>AerialSubSteps</c> substeps. The feature toggle and magnitude knobs live in RenderQuality (AerialPerspective / AerialMaxDistanceKm / AerialIntensity), while size does not. An edge length of 32 is also far below the 256 safety line from 1-8 for 3D textures, which is guaranteed by Vulkan's minimum maxImageDimension3D.</summary>
    public const uint AerialLutSize = 32;

    ComputeKernel? _transmittance;
    ComputeKernel? _multiScatter;
    ComputeKernel? _skyView;
    ComputeKernel? _cloudNoise;
    ComputeKernel? _aerial;

    uint _lutW, _lutH;
    uint _cloudNoiseSize;

    // These cached arrays cannot use stackalloc because they contain string references
    // (see ComputeDispatchArgs summary). Reuse them every frame with zero allocations.
    readonly ComputeResourceRef[] _transmittanceRes = { TransmittanceTextureName };

    readonly ComputeResourceRef[] _multiScatterRes = { TransmittanceTextureName, MultiScatterTextureName };

    readonly ComputeResourceRef[] _skyViewRes = { TransmittanceTextureName, MultiScatterTextureName, SkyViewTextureName };

    readonly ComputeResourceRef[] _cloudNoiseRes = { CloudNoiseTextureName };

    readonly ComputeResourceRef[] _aerialRes = { TransmittanceTextureName, MultiScatterTextureName, AerialLutTextureName };

    // One-shot bake gate for cloud noise. It depends on no runtime parameters, so it only needs a boolean instead of rebake criteria.
    bool _cloudNoiseBaked;

    // Rebake gate for the static LUT batch (Transmittance + MultiScatter). It depends only on atmospheric static parameters, not on light pose, and always bakes on the first frame.
    bool _baked;
    Vector3 _bakedRayleigh;
    float _bakedRayleighH, _bakedMieExt, _bakedMieH, _bakedGround, _bakedTop;
    float _bakedMieScat, _bakedAlbedo;

    public override string Name => "skyAtmosphere";

    public override ComputePhase Phase => ComputePhase.FrameStart;

    public override bool Initialize(IGraphics g)
    {
        // Quality-level gate. Under StaticCube mode, do not build any resources at all.
        if (RenderQuality.Current.Sky != SkyMode.Procedural)
        {
            // [SkyDebug] Troubleshooting invisible stars: do not build the procedural sky when the quality mode is not Procedural.
            DeviceServices.BaseApp.AddLog(LogType.Backend,
                $"[SkyDebug] SkyAtmosphere Initialize SKIP: SkyMode={RenderQuality.Current.Sky} (non-Procedural -> StaticCube mode, no star field)");
            return false;
        }

        var paramsBinding = new ComputeBindingDesc { Type = ComputeBindingType.Params, SizeInBytes = SkyParamsBytes };

        _transmittance = g.CreateComputeKernel(new ComputeKernelDesc
        {
            Name = "skyTransmittance",
            Source = new ShaderSourceSet { Hlsl = SourceTransmittanceHlsl, Glsl = SourceTransmittanceGlsl, Msl = SourceTransmittanceMsl, Wgsl = SourceTransmittanceWgsl, EntryPoint = "CSMain" },
            Bindings = new[]
            {
                paramsBinding,
                new ComputeBindingDesc { Type = ComputeBindingType.StorageTextureWrite, StorageFormat = ComputeStorageFormat.Rgba16Float },
            },
        });
        _multiScatter = g.CreateComputeKernel(new ComputeKernelDesc
        {
            Name = "skyMultiScatter",
            Source = new ShaderSourceSet { Hlsl = SourceMultiScatterHlsl, Glsl = SourceMultiScatterGlsl, Msl = SourceMultiScatterMsl, Wgsl = SourceMultiScatterWgsl, EntryPoint = "CSMain" },
            Bindings = new[]
            {
                paramsBinding,
                new ComputeBindingDesc { Type = ComputeBindingType.SampledTexture },
                new ComputeBindingDesc { Type = ComputeBindingType.StorageTextureWrite, StorageFormat = ComputeStorageFormat.Rgba16Float },
            },
        });
        _skyView = g.CreateComputeKernel(new ComputeKernelDesc
        {
            Name = "skyView",
            Source = new ShaderSourceSet { Hlsl = SourceSkyViewHlsl, Glsl = SourceSkyViewGlsl, Msl = SourceSkyViewMsl, Wgsl = SourceSkyViewWgsl, EntryPoint = "CSMain" },
            Bindings = new[]
            {
                paramsBinding,
                new ComputeBindingDesc { Type = ComputeBindingType.SampledTexture },
                new ComputeBindingDesc { Type = ComputeBindingType.SampledTexture },
                new ComputeBindingDesc { Type = ComputeBindingType.StorageTextureWrite, StorageFormat = ComputeStorageFormat.Rgba16Float },
            },
        });
        if (_transmittance == null || _multiScatter == null || _skyView == null)
        {
            // [SkyDebug] If any of the first three kernels fails to build, fall back entirely to StaticCube (texture + marker sphere, no star field).
            DeviceServices.BaseApp.AddLog(LogType.Error,
                $"[SkyDebug] SkyAtmosphere Initialize FAILED: trans={_transmittance != null} multi={_multiScatter != null} skyView={_skyView != null} -> fallback to StaticCube (no star field)");
            Dispose();
            return false;
        }

        // Step C cloud noise: uses its own 16B Params block rather than the 128B block above,
        // for the reasons explained in the class header. rgba8unorm is sufficient because all
        // four channels are 0..1 noise, and 8-bit quantization error stays below the cloud-edge
        // gradient width after density remapping.
        _cloudNoise = g.CreateComputeKernel(new ComputeKernelDesc
        {
            Name = "skyCloudNoise",
            Source = new ShaderSourceSet { Hlsl = SourceCloudNoiseHlsl, Glsl = SourceCloudNoiseGlsl, Msl = SourceCloudNoiseMsl, Wgsl = SourceCloudNoiseWgsl, EntryPoint = "CSMain" },
            Bindings = new[]
            {
                new ComputeBindingDesc { Type = ComputeBindingType.Params, SizeInBytes = CloudNoiseParamsBytes },
                new ComputeBindingDesc { Type = ComputeBindingType.StorageTextureWrite, StorageFormat = ComputeStorageFormat.Rgba8Unorm },
            },
        });

        // Step E aerial perspective: like cloud noise, this is an **optional add-on**.
        // If it cannot be built, only AP is disabled while the sky itself keeps working.
        // Therefore it must not return false the way the first three kernels do.
        // It carries its own 128B Params block (layout shown in the second table in the class header).
        // When the quality toggle is off, neither the kernel nor its texture is created, leaving no residue.
        if (RenderQuality.Current.AerialPerspective)
        {
            _aerial = g.CreateComputeKernel(new ComputeKernelDesc
            {
                Name = "skyAerial",
                Source = new ShaderSourceSet { Hlsl = SourceAerialHlsl, Glsl = SourceAerialGlsl, Msl = SourceAerialMsl, Wgsl = SourceAerialWgsl, EntryPoint = "CSMain" },
                Bindings = new[]
                {
                    paramsBinding,
                    new ComputeBindingDesc { Type = ComputeBindingType.SampledTexture },
                    new ComputeBindingDesc { Type = ComputeBindingType.SampledTexture },
                    new ComputeBindingDesc { Type = ComputeBindingType.StorageTexture3DWrite, StorageFormat = ComputeStorageFormat.Rgba16Float },
                },
            });
        }

        // Quality-sized LUT with minimum clamps. If the LUT gets too small, banding appears near the horizon.
        // 32x16 is the minimum that still preserves recognizable sky structure.
        _lutW = Math.Max(32u, (uint)RenderQuality.Current.SkyViewLutWidth);
        _lutH = Math.Max(16u, (uint)RenderQuality.Current.SkyViewLutHeight);

        g.CreateComputeTexture(TransmittanceTextureName, TransmittanceWidth, TransmittanceHeight, ComputeStorageFormat.Rgba16Float);
        g.CreateComputeTexture(MultiScatterTextureName, MultiScatterSize, MultiScatterSize, ComputeStorageFormat.Rgba16Float);
        g.CreateComputeTexture(SkyViewTextureName, _lutW, _lutH, ComputeStorageFormat.Rgba16Float);

        // The single source of truth for whether the procedural sky mode is active.
        // The application uses this to decide whether the skybox binds the LUT or falls back to the static cube.
        FrameSchedule.SkyViewTexture = SkyViewTextureName;

        // Cloud noise is an **optional add-on**. If its kernel cannot be built on this backend
        // because of a missing source or compile failure, only clouds are disabled and the sky body
        // keeps working. So this must not return false like the first three kernels.
        if (_cloudNoise != null)
        {
            _cloudNoiseSize = Math.Max(64u, (uint)RenderQuality.Current.CloudNoiseSize);
            g.CreateComputeTexture(CloudNoiseTextureName, _cloudNoiseSize, _cloudNoiseSize, ComputeStorageFormat.Rgba8Unorm);
            FrameSchedule.CloudNoiseTexture = CloudNoiseTextureName;
        }

// Step E follows the same rule. Only set the single aerial-perspective-available
        // indicator if the AP LUT is actually built. The consumer only looks at this flag.
        if (_aerial != null)
        {
            g.CreateComputeTexture3D(AerialLutTextureName, AerialLutSize, AerialLutSize, AerialLutSize,
                ComputeStorageFormat.Rgba16Float);
            FrameSchedule.AerialLutTexture = AerialLutTextureName;
        }

        // [SkyDebug] Procedural mode activated successfully: log the state of the three core kernels and the optional add-ons in one place.
        DeviceServices.BaseApp.AddLog(LogType.Backend,
            $"[SkyDebug] SkyAtmosphere Initialize OK: trans/multi/skyView built, cloudNoise={_cloudNoise != null} aerial={_aerial != null}, " +
            $"SkyViewTexture={FrameSchedule.SkyViewTexture}, cloudNoiseTex={FrameSchedule.CloudNoiseTexture ?? "null"}, lut={_lutW}x{_lutH}");

        return true;
    }

    public override void Record(IGraphics g)
    {
        Span<float> p = stackalloc float[SkyParamsFloats];

        // 0) Cloud noise: baked once in its lifetime because it is independent of both atmosphere
        //    parameters and sun/moon state. It comes first only because it has no dependencies.
        if (_cloudNoise != null && !_cloudNoiseBaked)
        {
            Span<float> np = stackalloc float[CloudNoiseParamsFloats];
            np[0] = _cloudNoiseSize;
            np[1] = _cloudNoiseSize;
            np[2] = CloudNoiseBasePeriod;
            np[3] = 0f;
            g.DispatchCompute(new ComputeDispatchArgs
            {
                Kernel = _cloudNoise,
                Params = MemoryMarshal.AsBytes(np),
                Resources = _cloudNoiseRes,
                GroupsX = (_cloudNoiseSize + 7) / 8,
                GroupsY = (_cloudNoiseSize + 7) / 8,
                GroupsZ = 1,
            });
            _cloudNoiseBaked = true;
        }

        // 1) Static LUT batch (Transmittance -> MultiScatter). Order cannot change because
        //    the latter reads the former. If static parameters did not change, skip the whole
        //    batch. In steady state, the effect then keeps only one skyView dispatch per frame.
        if (!_baked || StaticParamsChanged())
        {
            FillParams(p, TransmittanceWidth, TransmittanceHeight, TransmittanceBakeSteps);
            g.DispatchCompute(new ComputeDispatchArgs
            {
                Kernel = _transmittance!,
                Params = MemoryMarshal.AsBytes(p),
                Resources = _transmittanceRes,
                GroupsX = (TransmittanceWidth + 7) / 8,
                GroupsY = (TransmittanceHeight + 7) / 8,
                GroupsZ = 1,
            });

            FillParams(p, MultiScatterSize, MultiScatterSize, MultiScatterBakeSteps);
            g.DispatchCompute(new ComputeDispatchArgs
            {
                Kernel = _multiScatter!,
                Params = MemoryMarshal.AsBytes(p),
                Resources = _multiScatterRes,
                GroupsX = (MultiScatterSize + 7) / 8,
                GroupsY = (MultiScatterSize + 7) / 8,
                GroupsZ = 1,
            });
            CaptureStaticParams();
        }

        // 2) Sky-View LUT: recomputed every frame because the sun moves.
        //    Step count is a runtime knob, so it can be uploaded per frame without rebuilding the kernel.
        uint steps = Math.Max(4u, (uint)RenderQuality.Current.SkyRayMarchSteps);
        FillParams(p, _lutW, _lutH, steps);
        g.DispatchCompute(new ComputeDispatchArgs
        {
            Kernel = _skyView!,
            Params = MemoryMarshal.AsBytes(p),
            Resources = _skyViewRes,
            GroupsX = (_lutW + 7) / 8,
            GroupsY = (_lutH + 7) / 8,
            GroupsZ = 1,
        });

        // 3) Aerial-perspective volume: also recomputed every frame because the camera moves too.
        //    It appears after skyView purely for readability. The two are independent because both
        //    only read the same static LUTs, so swapping order would not change results.
        //    Note that it uses its own Params layout. The first 32 floats were already written by
        //    FillParams above, so they are fully overwritten here.
        if (_aerial != null)
        {
            FillAerialParams(p, MathF.Max(0.01f, RenderQuality.Current.AerialMaxDistanceKm));
            g.DispatchCompute(new ComputeDispatchArgs
            {
                Kernel = _aerial,
                Params = MemoryMarshal.AsBytes(p),
                Resources = _aerialRes,
                // Dispatch only over XY. Each thread marches one full column of Z slices along
                // the view ray, where the accumulated value naturally recurs slice by slice.
                // Splitting work into Z as well would force every thread to integrate again from t = 0.
                GroupsX = (AerialLutSize + 7) / 8,
                GroupsY = (AerialLutSize + 7) / 8,
                GroupsZ = 1,
            });
        }
    }

    public void Dispose()
    {
        _transmittance?.Dispose();
        _transmittance = null;
        _multiScatter?.Dispose();
        _multiScatter = null;
        _skyView?.Dispose();
        _skyView = null;
        _cloudNoise?.Dispose();
        _cloudNoise = null;
        _aerial?.Dispose();
        _aerial = null;
        _baked = false;
        _cloudNoiseBaked = false;
        if (FrameSchedule.SkyViewTexture == SkyViewTextureName)
            FrameSchedule.SkyViewTexture = null;
        if (FrameSchedule.CloudNoiseTexture == CloudNoiseTextureName)
            FrameSchedule.CloudNoiseTexture = null;
        if (FrameSchedule.AerialLutTexture == AerialLutTextureName)
            FrameSchedule.AerialLutTexture = null;
    }

    // Params filling helpers. The first three kernels share the same layout, differing only
    // in the three uLut values. Cloud noise uses a separate 16B block.

    const int SkyParamsFloats = 32;

    const uint SkyParamsBytes = SkyParamsFloats * sizeof(float);

    const int CloudNoiseParamsFloats = 4;

    const uint CloudNoiseParamsBytes = CloudNoiseParamsFloats * sizeof(float);

    // Normalization safety: application-provided directions may not be normalized arc points.
    // BodyPosition is already normalized, but this code does not require that guarantee.
    static Vector3 SafeNormalize(Vector3 dir, Vector3 fallback)
    {
        float len = dir.Length();
        return len > 1e-5f ? dir / len : fallback;
    }

    static void FillParams(Span<float> p, uint lutW, uint lutH, uint steps)
    {
        var sun = SafeNormalize(Atmosphere.SunDirection, Vector3.UnitY);
        var moon = SafeNormalize(Atmosphere.MoonDirection, -Vector3.UnitY);

        p[0] = sun.X;
        p[1] = sun.Y;
        p[2] = sun.Z;
        p[3] = Atmosphere.SunIrradiance;

        p[4] = Atmosphere.SunColor.X;
        p[5] = Atmosphere.SunColor.Y;
        p[6] = Atmosphere.SunColor.Z;
        p[7] = Atmosphere.NightAirglow;

        p[8] = lutW;
        p[9] = lutH;
        p[10] = steps;
        p[11] = Atmosphere.MultiScatterGain;

        p[12] = Atmosphere.RayleighScattering.X;
        p[13] = Atmosphere.RayleighScattering.Y;
        p[14] = Atmosphere.RayleighScattering.Z;
        p[15] = Atmosphere.MieScattering;

        p[16] = Atmosphere.MieExtinction;
        p[17] = Atmosphere.RayleighHeightKm;
        p[18] = Atmosphere.MieHeightKm;
        p[19] = Atmosphere.MiePhaseG;

        p[20] = Atmosphere.GroundRadiusKm;
        p[21] = Atmosphere.AtmosphereRadiusKm;
        p[22] = Atmosphere.ViewAltitudeKm;
        p[23] = Atmosphere.GroundAlbedo;

        p[24] = moon.X;
        p[25] = moon.Y;
        p[26] = moon.Z;
        p[27] = Atmosphere.MoonIrradiance;

        p[28] = Atmosphere.MoonColor.X;
        p[29] = Atmosphere.MoonColor.Y;
        p[30] = Atmosphere.MoonColor.Z;
        p[31] = 0f;
    }

    /// <summary>
    /// Step E aerial-perspective Params fill. It has the **same size but a different layout**
    /// from the block above, so the two cannot be mixed. See the second table in the class header.
    ///
    /// The camera basis is reconstructed on the CPU from <see cref="Camera3D"/> Position / Target / Up,
    /// deliberately without passing matrices.
    /// Uploading inverse view-projection would require six float4 values and would depend on row/column
    /// vector conventions and depth-range conventions, while only two axes are enough here to rebuild
    /// the full frustum and completely avoid matrix-layout ambiguity. Both axes are normalized first
    /// and then scaled by tan(fovY/2) (times aspect for the right axis), so the shader expression
    /// <c>dir = normalize(fwd + right*ndcX + up*ndcY)</c> reconstructs the perspective ray exactly.
    ///
    /// If aspect is not ready on the first frame, it falls back to 1, matching Gtao's treatment
    /// of camera.Aspect. In degenerate poses where the view direction is collinear with Up,
    /// right falls back to UnitX. The volume may skew for one frame, but AP is a very low-frequency
    /// quantity and recovers on the next frame, so extra branching is not worthwhile.
    /// </summary>
    static void FillAerialParams(Span<float> p, float maxDistKm)
    {
        var sun = SafeNormalize(Atmosphere.SunDirection, Vector3.UnitY);
        var moon = SafeNormalize(Atmosphere.MoonDirection, -Vector3.UnitY);

        // Pre-multiply because the shader only needs the color * irradiance product anyway.
        // Uploading them separately would waste two alpha slots.
        var sunRadiance = Atmosphere.SunColor * Atmosphere.SunIrradiance;
        var moonRadiance = Atmosphere.MoonColor * Atmosphere.MoonIrradiance;

        var camera = DeviceServices.BaseApp.Camera;
        var fwd = SafeNormalize(camera.Target - camera.Position, Vector3.UnitZ);
        var right = SafeNormalize(Vector3.Cross(camera.Up, fwd), Vector3.UnitX);
        var up = Vector3.Cross(fwd, right);
        float tanHalf = MathF.Tan(camera.FovY * 0.5f);
        float aspect = camera.Aspect > 0f ? camera.Aspect : 1f;
        right *= tanHalf * aspect;
        up *= tanHalf;

        p[0] = sun.X;
        p[1] = sun.Y;
        p[2] = sun.Z;
        p[3] = Atmosphere.RayleighHeightKm;

        p[4] = sunRadiance.X;
        p[5] = sunRadiance.Y;
        p[6] = sunRadiance.Z;
        p[7] = Atmosphere.MieScattering;

        p[8] = moon.X;
        p[9] = moon.Y;
        p[10] = moon.Z;
        p[11] = Atmosphere.MieHeightKm;

        p[12] = moonRadiance.X;
        p[13] = moonRadiance.Y;
        p[14] = moonRadiance.Z;
        p[15] = Atmosphere.MieExtinction;

        p[16] = Atmosphere.RayleighScattering.X;
        p[17] = Atmosphere.RayleighScattering.Y;
        p[18] = Atmosphere.RayleighScattering.Z;
        p[19] = Atmosphere.MiePhaseG;

        p[20] = Atmosphere.GroundRadiusKm;
        p[21] = Atmosphere.AtmosphereRadiusKm;
        p[22] = Atmosphere.ViewAltitudeKm;
        p[23] = Atmosphere.MultiScatterGain;

        p[24] = right.X;
        p[25] = right.Y;
        p[26] = right.Z;
        p[27] = maxDistKm;

        p[28] = up.X;
        p[29] = up.Y;
        p[30] = up.Z;
        p[31] = 0f;
    }

    /// <summary>Rebake criterion for the static LUT batch. Transmittance is only a cache of the analytic
    /// function (mu, r) -> transmittance and is independent of light pose. MultiScatter is normalized
    /// to unit irradiance + white light and therefore also depends only on atmospheric parameters and geometry.
    /// Consequently, rebaking is required only when these eight values change: the first six are
    /// extinction-related terms plus planet geometry, which together cover all Transmittance dependencies,
    /// while the last two are unique to MultiScatter: <c>MieScattering</c> enters the MS scattering source,
    /// and <c>GroundAlbedo</c> controls ground bounce. The phase parameter g is intentionally excluded:
    /// the MS bake uses an isotropic phase on purpose because multiple scattering has already washed out
    /// directional structure, so <c>MiePhaseG</c> only affects the per-frame skyView pass and does not trigger rebakes.</summary>
    bool StaticParamsChanged()
        => _bakedRayleigh != Atmosphere.RayleighScattering
        || _bakedRayleighH != Atmosphere.RayleighHeightKm
        || _bakedMieExt != Atmosphere.MieExtinction
        || _bakedMieH != Atmosphere.MieHeightKm
        || _bakedGround != Atmosphere.GroundRadiusKm
        || _bakedTop != Atmosphere.AtmosphereRadiusKm
        || _bakedMieScat != Atmosphere.MieScattering
        || _bakedAlbedo != Atmosphere.GroundAlbedo;

    void CaptureStaticParams()
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

    // Shader sources. This file is the single source of truth. When a backend source is missing,
    // registration degrades gracefully instead of leaving residual state.

    /// <summary>
    /// D3D12 cs_5_0 (fxc; helper functions and kernels use a single exit to avoid X4000):
    /// Transmittance LUT baking.
    /// Parameterization is u = (mu + 1) / 2 and v = (r - Rg) / (Rt - Rg), as described in the
    /// Atmosphere class header. Ground hits are written as 0, baking planetary self-occlusion
/// into the LUT so consumers do not need shadow rays, and the transition from sun below the horizon
/// to nighttime becomes a natural consequence of the integral itself.
    /// </summary>
    const string SourceTransmittanceHlsl = @"
cbuffer SkyParams : register(b0)
{
    float4 uSun;
    float4 uSunColor;
    float4 uLut;
    float4 uRayleigh;
    float4 uMie;
    float4 uPlanet;
    float4 uMoon;
    float4 uMoonColor;
};

RWTexture2D<float4> uOutput : register(u0);

// Nearest non-negative intersection distance between a view ray
// (starting at radius r with cosine mu against the local up direction)
// and a concentric sphere of radius R. Returns -1 when there is no hit.
float RaySphere(float r, float mu, float R)
{
    float disc = r * r * (mu * mu - 1.0) + R * R;
    float t = -1.0;
    if (disc >= 0.0)
    {
        float sq = sqrt(disc);
        float tNear = -r * mu - sq;
        float tFar = -r * mu + sq;
        t = tNear >= 0.0 ? tNear : tFar;
    }
    return t;
}

// Whether the view ray hits the ground. The mu < 0 test is required as well; otherwise
// an upward ray grazing the ground (r ~ Rg) would be misclassified as a hit just because
// the discriminant happens to be non-negative.
bool HitsGround(float r, float mu, float Rg)
{
    return mu < 0.0 && (r * r * (mu * mu - 1.0) + Rg * Rg) >= 0.0;
}

// Relative Rayleigh / Mie density at altitude h (km), each with its own exponential scale height.
float2 AirDensity(float h, float rayleighH, float mieH)
{
    float hc = max(h, 0.0);
    return float2(exp(-hc / rayleighH), exp(-hc / mieH));
}

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    uint w = (uint)uLut.x;
    uint h = (uint)uLut.y;
    if (id.x < w && id.y < h)
    {
        float Rg = uPlanet.x;
        float Rt = uPlanet.y;
        float mu = ((float(id.x) + 0.5) / uLut.x) * 2.0 - 1.0;
        float r = Rg + ((float(id.y) + 0.5) / uLut.y) * (Rt - Rg);

        // Ground hit -> transmittance is always 0 because of planetary self-occlusion.
        float3 trans = float3(0.0, 0.0, 0.0);
        if (!HitsGround(r, mu, Rg))
        {
            float tTop = max(RaySphere(r, mu, Rt), 0.0);
            int steps = max((int)uLut.z, 1);
            float dt = tTop / float(steps);
            float3 optical = float3(0.0, 0.0, 0.0);
            for (int i = 0; i < steps; i++)
            {
                // Midpoint sampling per segment. Uniform stepping is sufficient for this static LUT
                // and does not contribute to per-frame cost.
                float t = (float(i) + 0.5) * dt;
                float rr = sqrt(max(r * r + t * t + 2.0 * r * mu * t, 1e-6));
                float2 dens = AirDensity(rr - Rg, uMie.y, uMie.z);
                optical += (uRayleigh.rgb * dens.x + uMie.x * dens.y) * dt;
            }
            trans = exp(-optical);
        }
        uOutput[id.xy] = float4(trans, 1.0);
    }
}
";

    /// <summary>
    /// Vulkan GLSL 450 (glslang compiles to SPIR-V at runtime; entry point is always main).
    /// It is literally isomorphic to HLSL. Params use push_constant
    /// (128B reaches the Vulkan push-constant minimum; see the class header),
    /// and uOutput is an rgba16f storage image (Write slot -> StorageImage).
    /// </summary>
    const string SourceTransmittanceGlsl = @"#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(push_constant) uniform SkyParams
{
    vec4 uSun;
    vec4 uSunColor;
    vec4 uLut;
    vec4 uRayleigh;
    vec4 uMie;
    vec4 uPlanet;
    vec4 uMoon;
    vec4 uMoonColor;
};

layout(binding = 1, rgba16f) uniform writeonly image2D uOutput;

// Nearest non-negative intersection distance between a view ray
// (starting at radius r with cosine mu against the local up direction)
// and a concentric sphere of radius R. Returns -1 when there is no hit.
float RaySphere(float r, float mu, float R)
{
    float disc = r * r * (mu * mu - 1.0) + R * R;
    float t = -1.0;
    if (disc >= 0.0)
    {
        float sq = sqrt(disc);
        float tNear = -r * mu - sq;
        float tFar = -r * mu + sq;
        t = tNear >= 0.0 ? tNear : tFar;
    }
    return t;
}

// Whether the view ray hits the ground. The mu < 0 test is required as well; otherwise
// an upward ray grazing the ground (r ~ Rg) would be misclassified as a hit just because
// the discriminant happens to be non-negative.
bool HitsGround(float r, float mu, float Rg)
{
    return mu < 0.0 && (r * r * (mu * mu - 1.0) + Rg * Rg) >= 0.0;
}

// Relative Rayleigh / Mie density at altitude h (km), each with its own exponential scale height.
vec2 AirDensity(float h, float rayleighH, float mieH)
{
    float hc = max(h, 0.0);
    return vec2(exp(-hc / rayleighH), exp(-hc / mieH));
}

void main()
{
    uvec2 id = gl_GlobalInvocationID.xy;
    uint w = uint(uLut.x);
    uint h = uint(uLut.y);
    if (id.x < w && id.y < h)
    {
        float Rg = uPlanet.x;
        float Rt = uPlanet.y;
        float mu = ((float(id.x) + 0.5) / uLut.x) * 2.0 - 1.0;
        float r = Rg + ((float(id.y) + 0.5) / uLut.y) * (Rt - Rg);

        // Ground hit -> transmittance is always 0 because of planetary self-occlusion.
        vec3 trans = vec3(0.0);
        if (!HitsGround(r, mu, Rg))
        {
            float tTop = max(RaySphere(r, mu, Rt), 0.0);
            int steps = max(int(uLut.z), 1);
            float dt = tTop / float(steps);
            vec3 optical = vec3(0.0);
            for (int i = 0; i < steps; i++)
            {
                // Midpoint sampling per segment. Uniform stepping is sufficient for this static LUT
                // and does not contribute to per-frame cost.
                float t = (float(i) + 0.5) * dt;
                float rr = sqrt(max(r * r + t * t + 2.0 * r * mu * t, 1e-6));
                vec2 dens = AirDensity(rr - Rg, uMie.y, uMie.z);
                optical += (uRayleigh.rgb * dens.x + vec3(uMie.x * dens.y)) * dt;
            }
            trans = exp(-optical);
        }
        imageStore(uOutput, ivec2(id), vec4(trans, 1.0));
    }
}
";

    /// <summary>
    /// WebGPU WGSL (delivered through the interop layer; @binding(i) follows the Bindings declaration order,
    /// and the engine sampler always uses @binding(15)).
    /// It is literally isomorphic to HLSL. Params use binding 0 uniform
    /// (a 128B struct, because WebGPU has no push_constant concept),
    /// and uOutput is an rgba16f write-only storage texture
    /// (JS-side layoutEntries type 3, with the format coming from the core-guaranteed value of
    /// _mapStorageFormat and matching the literal below).
    /// </summary>
    const string SourceTransmittanceWgsl = @"
struct SkyParams
{
    uSun : vec4f,
    uSunColor : vec4f,
    uLut : vec4f,
    uRayleigh : vec4f,
    uMie : vec4f,
    uPlanet : vec4f,
    uMoon : vec4f,
    uMoonColor : vec4f,
};

@group(0) @binding(0) var<uniform> params : SkyParams;
@group(0) @binding(1) var uOutput : texture_storage_2d<rgba16float, write>;

// Nearest non-negative intersection distance between a view ray
// (starting at radius r with cosine mu against the local up direction)
// and a concentric sphere of radius R. Returns -1 when there is no hit.
fn RaySphere(r : f32, mu : f32, R : f32) -> f32
{
    let disc = r * r * (mu * mu - 1.0) + R * R;
    var t = -1.0;
    if (disc >= 0.0)
    {
        let sq = sqrt(disc);
        let tNear = -r * mu - sq;
        let tFar = -r * mu + sq;
        t = select(tFar, tNear, tNear >= 0.0);
    }
    return t;
}

// Whether the view ray hits the ground. The mu < 0 test is required as well; otherwise
// an upward ray grazing the ground (r ~ Rg) would be misclassified as a hit just because
// the discriminant happens to be non-negative.
fn HitsGround(r : f32, mu : f32, Rg : f32) -> bool
{
    return mu < 0.0 && (r * r * (mu * mu - 1.0) + Rg * Rg) >= 0.0;
}

// Relative Rayleigh / Mie density at altitude h (km), each with its own exponential scale height.
fn AirDensity(h : f32, rayleighH : f32, mieH : f32) -> vec2f
{
    let hc = max(h, 0.0);
    return vec2f(exp(-hc / rayleighH), exp(-hc / mieH));
}

@compute @workgroup_size(8, 8, 1)
fn CSMain(@builtin(global_invocation_id) id : vec3<u32>)
{
    let w = u32(params.uLut.x);
    let h = u32(params.uLut.y);
    if (id.x < w && id.y < h)
    {
        let Rg = params.uPlanet.x;
        let Rt = params.uPlanet.y;
        let mu = ((f32(id.x) + 0.5) / params.uLut.x) * 2.0 - 1.0;
        let r = Rg + ((f32(id.y) + 0.5) / params.uLut.y) * (Rt - Rg);

        // Ground hit -> transmittance is always 0 because of planetary self-occlusion.
        var trans = vec3f(0.0);
        if (!HitsGround(r, mu, Rg))
        {
            let tTop = max(RaySphere(r, mu, Rt), 0.0);
            let steps = max(i32(params.uLut.z), 1);
            let dt = tTop / f32(steps);
            var optical = vec3f(0.0);
            for (var i : i32 = 0; i < steps; i = i + 1)
            {
                // Midpoint sampling per segment. Uniform stepping is sufficient for this static LUT
                // and does not contribute to per-frame cost.
                let t = (f32(i) + 0.5) * dt;
                let rr = sqrt(max(r * r + t * t + 2.0 * r * mu * t, 1e-6));
                let dens = AirDensity(rr - Rg, params.uMie.y, params.uMie.z);
                optical += (params.uRayleigh.rgb * dens.x + vec3f(params.uMie.x * dens.y)) * dt;
            }
            trans = exp(-optical);
        }
        textureStore(uOutput, vec2i(id.xy), vec4f(trans, 1.0));
    }
}
";

    /// <summary>
    /// Apple Metal MSL (metal_stdlib). It is literally isomorphic to GLSL.
    /// Params use a constant reference at [[buffer(0)]]
    /// (128B <= the 4KB setBytes limit), and uOutput is an rgba16f write-only texture [[texture(0)]].
    /// MSL has no compile-time threadgroup declaration, so workgroup size is provided at runtime
    /// through ComputeKernelDesc.WorkgroupX/Y/Z in DispatchCompute
    /// (this kernel uses the default 8,8,1; see MTLComputeKernel).
    /// Sampling always uses explicit level(0.0) because LUTs have no mips, and this also avoids
    /// implicit-derivative restrictions in non-uniform control flow.
    /// </summary>
    const string SourceTransmittanceMsl = @"
#include <metal_stdlib>
#include <simd/simd.h>
using namespace metal;

struct SkyParams
{
    float4 uSun;
    float4 uSunColor;
    float4 uLut;
    float4 uRayleigh;
    float4 uMie;
    float4 uPlanet;
    float4 uMoon;
    float4 uMoonColor;
};

// Nearest non-negative intersection distance between a view ray
// (starting at radius r with cosine mu against the local up direction)
// and a concentric sphere of radius R. Returns -1 when there is no hit.
float RaySphere(float r, float mu, float R)
{
    float disc = r * r * (mu * mu - 1.0) + R * R;
    float t = -1.0;
    if (disc >= 0.0)
    {
        float sq = sqrt(disc);
        float tNear = -r * mu - sq;
        float tFar = -r * mu + sq;
        t = tNear >= 0.0 ? tNear : tFar;
    }
    return t;
}

// Whether the view ray hits the ground. The mu < 0 test is required as well; otherwise
// an upward ray grazing the ground (r ~ Rg) would be misclassified as a hit just because
// the discriminant happens to be non-negative.
bool HitsGround(float r, float mu, float Rg)
{
    return mu < 0.0 && (r * r * (mu * mu - 1.0) + Rg * Rg) >= 0.0;
}

// Relative Rayleigh / Mie density at altitude h (km), each with its own exponential scale height.
float2 AirDensity(float h, float rayleighH, float mieH)
{
    float hc = max(h, 0.0);
    return float2(exp(-hc / rayleighH), exp(-hc / mieH));
}

kernel void CSMain(uint3 gid [[thread_position_in_grid]],
                   constant SkyParams& p [[buffer(0)]],
                   texture2d<float, access::write> uOutput [[texture(0)]])
{
    uint w = uint(p.uLut.x);
    uint h = uint(p.uLut.y);
    if (gid.x < w && gid.y < h)
    {
        float Rg = p.uPlanet.x;
        float Rt = p.uPlanet.y;
        float mu = ((float(gid.x) + 0.5) / p.uLut.x) * 2.0 - 1.0;
        float r = Rg + ((float(gid.y) + 0.5) / p.uLut.y) * (Rt - Rg);

        // Ground hit -> transmittance is always 0 because of planetary self-occlusion.
        float3 trans = float3(0.0);
        if (!HitsGround(r, mu, Rg))
        {
            float tTop = max(RaySphere(r, mu, Rt), 0.0);
            int steps = max(int(p.uLut.z), 1);
            float dt = tTop / float(steps);
            float3 optical = float3(0.0);
            for (int i = 0; i < steps; i++)
            {
                // Midpoint sampling per segment. Uniform stepping is sufficient for this static LUT
                // and does not contribute to per-frame cost.
                float t = (float(i) + 0.5) * dt;
                float rr = sqrt(max(r * r + t * t + 2.0 * r * mu * t, 1e-6));
                float2 dens = AirDensity(rr - Rg, p.uMie.y, p.uMie.z);
                optical += (p.uRayleigh.rgb * dens.x + float3(p.uMie.x * dens.y)) * dt;
            }
            trans = exp(-optical);
        }
        uOutput.write(float4(trans, 1.0), uint2(gid.xy));
    }
}
";

    /// <summary>
    /// D3D12 cs_5_0: bake the multiple-scattering transfer function psi_ms
    /// (a single-thread equivalent of Hillaire 2020. The paper uses a 64-thread groupshared
    /// reduction; here one thread simply sweeps all 64 directions because the bake runs only once
    /// and the total cost is just 32^2 x 64 x 20 iterations, so groupshared memory and barrier
    /// semantics are unnecessary). Parameterization is **literally isomorphic** to Transmittance
    /// with u = (cos(theta_light) + 1) / 2 and v = (r - Rg) / (Rt - Rg), allowing the consumer
    /// to reuse the same UV directly.
    ///
    /// Algorithm: sample 64 uniformly distributed sphere directions around the observation point,
    /// and along each direction ray-march while accumulating two quantities:
    /// - <c>L1</c>: first-order scattering under unit irradiance
    ///   (using the **isotropic phase** 1 / 4PI, so the LUT is independent of MiePhaseG)
    ///   plus ground bounce.
    /// - <c>f_ms</c>: the energy ratio returning to the observation point when every point
    ///   emits isotropic scattering of 1. Since the sphere integral of isotropic incident light
    ///   is integral((1 / 4PI) domega) = 1, the source term is exactly sigma_s.
    /// Both spherical averages are Sigma / N because the weight 4PI / N multiplied by the isotropic
    /// phase 1 / 4PI collapses to 1 / N.
    /// Since every scattering order passes through the same transfer operator, the infinite sum is
    /// just the geometric series psi_ms = L1 / (1 - f_ms).
    /// Because sigma_s <= sigma_e (scattering albedo <= 1), f_ms < 1 always holds in theory,
    /// but it is still clamped to 0.999 to avoid divide-by-zero after rgba16f quantization.
    /// Note: the first term is the isotropic approximation of first-order scattering, so it overlaps
    /// slightly with the consumer-side exact-phase single-scattering term, exactly as in Hillaire's
    /// original formulation. It also compensates for the energy loss caused by the 16-step march.
    /// That overlap is globally controlled by <c>Atmosphere.MultiScatterGain</c>.
    /// </summary>
    const string SourceMultiScatterHlsl = @"
cbuffer SkyParams : register(b0)
{
    float4 uSun;
    float4 uSunColor;
    float4 uLut;
    float4 uRayleigh;
    float4 uMie;
    float4 uPlanet;
    float4 uMoon;
    float4 uMoonColor;
};

Texture2D<float4> uTransmittance : register(t0);
SamplerState uLinearClamp : register(s0);
RWTexture2D<float4> uOutput : register(u0);

// Number of sphere directions = MsSqrtSamples^2 (Hillaire's original value is 8x8 = 64).
// psi_ms is extremely low frequency, and 64 directions already converge below rgba16f quantization.
static const int MsSqrtSamples = 8;

float RaySphere(float r, float mu, float R)
{
    float disc = r * r * (mu * mu - 1.0) + R * R;
    float t = -1.0;
    if (disc >= 0.0)
    {
        float sq = sqrt(disc);
        float tNear = -r * mu - sq;
        float tFar = -r * mu + sq;
        t = tNear >= 0.0 ? tNear : tFar;
    }
    return t;
}

bool HitsGround(float r, float mu, float Rg)
{
    return mu < 0.0 && (r * r * (mu * mu - 1.0) + Rg * Rg) >= 0.0;
}

float2 AirDensity(float h, float rayleighH, float mieH)
{
    float hc = max(h, 0.0);
    return float2(exp(-hc / rayleighH), exp(-hc / mieH));
}

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    uint w = (uint)uLut.x;
    uint h = (uint)uLut.y;
    if (id.x < w && id.y < h)
    {
        float Rg = uPlanet.x;
        float Rt = uPlanet.y;

        // Parameterization literally matches the Transmittance LUT so the consumer can reuse the same UV.
        float cosZ = ((float(id.x) + 0.5) / uLut.x) * 2.0 - 1.0;
        float r = Rg + ((float(id.y) + 0.5) / uLut.y) * (Rt - Rg);

        // Local coordinates: the observation point sits at (0,r,0), local up is +Y,
        // and light azimuth is irrelevant because of rotational symmetry around Y, so it is fixed in the YZ plane.
        float3 origin = float3(0.0, r, 0.0);
        float3 lightDir = float3(0.0, cosZ, sqrt(saturate(1.0 - cosZ * cosZ)));
        float albedo = uPlanet.w;
        int steps = max((int)uLut.z, 1);

        float3 lSum = float3(0.0, 0.0, 0.0);
        float3 fSum = float3(0.0, 0.0, 0.0);

        [loop]
        for (int sy = 0; sy < MsSqrtSamples; sy++)
        {
            [loop]
            for (int sx = 0; sx < MsSqrtSamples; sx++)
            {
                // Stratified uniform sphere directions: uniform azimuth + uniform cosine -> equal solid-angle weight.
                float a = (float(sx) + 0.5) / float(MsSqrtSamples);
                float b = (float(sy) + 0.5) / float(MsSqrtSamples);
                float theta = 6.28318530718 * a;
                float dy = 1.0 - 2.0 * b;
                float dxz = sqrt(saturate(1.0 - dy * dy));
                float3 dir = float3(dxz * cos(theta), dy, dxz * sin(theta));

                bool ground = HitsGround(r, dir.y, Rg);
                float tMax = ground ? max(RaySphere(r, dir.y, Rg), 0.0) : max(RaySphere(r, dir.y, Rt), 0.0);
                float dt = tMax / float(steps);

                float3 throughput = float3(1.0, 1.0, 1.0);
                float3 lDir = float3(0.0, 0.0, 0.0);
                float3 fDir = float3(0.0, 0.0, 0.0);
                for (int i = 0; i < steps; i++)
                {
                    float t = (float(i) + 0.5) * dt;
                    float3 pos = origin + dir * t;
                    float rr = max(length(pos), 1e-6);
                    float3 upLocal = pos / rr;
                    float2 dens = AirDensity(rr - Rg, uMie.y, uMie.z);

                    float3 sigmaS = uRayleigh.rgb * dens.x + uRayleigh.a * dens.y;
                    float3 sigmaE = max(uRayleigh.rgb * dens.x + uMie.x * dens.y, 1e-7);
                    float3 stepT = exp(-sigmaE * dt);

                    // First-order scattering source: unit irradiance with isotropic phase.
                    // Celestial transmittance is sampled directly from the Transmittance LUT, which already contains ground occlusion.
                    float2 tuv = float2(dot(upLocal, lightDir) * 0.5 + 0.5, saturate((rr - Rg) / (Rt - Rg)));
                    float3 tLight = uTransmittance.SampleLevel(uLinearClamp, tuv, 0.0).rgb;
                    float3 s1 = sigmaS * 0.07957747155 * tLight;
                    lDir += throughput * (s1 - s1 * stepT) / sigmaE;

                    // Transfer term: if every point emits isotropic radiance 1, the scattering source is exactly sigma_s
                    // because the sphere integral of the phase function is 1.
                    fDir += throughput * (sigmaS - sigmaS * stepT) / sigmaE;

                    throughput *= stepT;
                }

                // Ground bounce: a Lambertian surface sends received direct light back into the atmosphere.
                // This is one of the uses of GroundAlbedo; without it, the region below the horizon becomes too dark
                // and sunset loses a layer of warm bounce light.
                if (ground)
                {
                    float3 gp = origin + dir * tMax;
                    float3 gn = gp / max(length(gp), 1e-6);
                    float2 guv = float2(dot(gn, lightDir) * 0.5 + 0.5, 0.0);
                    float3 tg = uTransmittance.SampleLevel(uLinearClamp, guv, 0.0).rgb;
                    lDir += throughput * tg * (albedo * 0.31830988618) * saturate(dot(gn, lightDir));
                }

                lSum += lDir;
                fSum += fDir;
            }
        }

        // Take the spherical mean (weight 4PI / N times isotropic phase 1 / 4PI = 1 / N),
        // then evaluate the geometric-series sum to get infinite-order scattering.
        float invN = 1.0 / float(MsSqrtSamples * MsSqrtSamples);
        float3 l1 = lSum * invN;
        float3 fms = min(fSum * invN, 0.999);
        uOutput[id.xy] = float4(l1 / (1.0 - fms), 1.0);
    }
}
";

    /// <summary>
    /// Vulkan GLSL 450. Literally isomorphic to HLSL. Transmittance is bound as a CombinedImageSampler
    /// (immutable linear sampler, see VKComputeKernel), and textureLod reads it explicitly.
    /// </summary>
    const string SourceMultiScatterGlsl = @"#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(push_constant) uniform SkyParams
{
    vec4 uSun;
    vec4 uSunColor;
    vec4 uLut;
    vec4 uRayleigh;
    vec4 uMie;
    vec4 uPlanet;
    vec4 uMoon;
    vec4 uMoonColor;
};

layout(binding = 1) uniform sampler2D uTransmittance;
layout(binding = 2, rgba16f) uniform writeonly image2D uOutput;

// Number of sphere directions = MsSqrtSamples^2 (Hillaire's original value is 8x8 = 64).
// psi_ms is extremely low frequency, and 64 directions already converge below rgba16f quantization.
const int MsSqrtSamples = 8;

float RaySphere(float r, float mu, float R)
{
    float disc = r * r * (mu * mu - 1.0) + R * R;
    float t = -1.0;
    if (disc >= 0.0)
    {
        float sq = sqrt(disc);
        float tNear = -r * mu - sq;
        float tFar = -r * mu + sq;
        t = tNear >= 0.0 ? tNear : tFar;
    }
    return t;
}

bool HitsGround(float r, float mu, float Rg)
{
    return mu < 0.0 && (r * r * (mu * mu - 1.0) + Rg * Rg) >= 0.0;
}

vec2 AirDensity(float h, float rayleighH, float mieH)
{
    float hc = max(h, 0.0);
    return vec2(exp(-hc / rayleighH), exp(-hc / mieH));
}

void main()
{
    uvec2 id = gl_GlobalInvocationID.xy;
    uint w = uint(uLut.x);
    uint h = uint(uLut.y);
    if (id.x < w && id.y < h)
    {
        float Rg = uPlanet.x;
        float Rt = uPlanet.y;

        // Parameterization literally matches the Transmittance LUT so the consumer can reuse the same UV.
        float cosZ = ((float(id.x) + 0.5) / uLut.x) * 2.0 - 1.0;
        float r = Rg + ((float(id.y) + 0.5) / uLut.y) * (Rt - Rg);

        // Local coordinates: the observation point sits at (0,r,0), local up is +Y,
        // and light azimuth is irrelevant because of rotational symmetry around Y, so it is fixed in the YZ plane.
        vec3 origin = vec3(0.0, r, 0.0);
        vec3 lightDir = vec3(0.0, cosZ, sqrt(clamp(1.0 - cosZ * cosZ, 0.0, 1.0)));
        float albedo = uPlanet.w;
        int steps = max(int(uLut.z), 1);

        vec3 lSum = vec3(0.0);
        vec3 fSum = vec3(0.0);

        for (int sy = 0; sy < MsSqrtSamples; sy++)
        {
            for (int sx = 0; sx < MsSqrtSamples; sx++)
            {
                // Stratified uniform sphere directions: uniform azimuth + uniform cosine -> equal solid-angle weight.
                float a = (float(sx) + 0.5) / float(MsSqrtSamples);
                float b = (float(sy) + 0.5) / float(MsSqrtSamples);
                float theta = 6.28318530718 * a;
                float dy = 1.0 - 2.0 * b;
                float dxz = sqrt(clamp(1.0 - dy * dy, 0.0, 1.0));
                vec3 dir = vec3(dxz * cos(theta), dy, dxz * sin(theta));

                bool ground = HitsGround(r, dir.y, Rg);
                float tMax = ground ? max(RaySphere(r, dir.y, Rg), 0.0) : max(RaySphere(r, dir.y, Rt), 0.0);
                float dt = tMax / float(steps);

                vec3 throughput = vec3(1.0);
                vec3 lDir = vec3(0.0);
                vec3 fDir = vec3(0.0);
                for (int i = 0; i < steps; i++)
                {
                    float t = (float(i) + 0.5) * dt;
                    vec3 pos = origin + dir * t;
                    float rr = max(length(pos), 1e-6);
                    vec3 upLocal = pos / rr;
                    vec2 dens = AirDensity(rr - Rg, uMie.y, uMie.z);

                    vec3 sigmaS = uRayleigh.rgb * dens.x + vec3(uRayleigh.a * dens.y);
                    vec3 sigmaE = max(uRayleigh.rgb * dens.x + vec3(uMie.x * dens.y), vec3(1e-7));
                    vec3 stepT = exp(-sigmaE * dt);

                    // First-order scattering source: unit irradiance with isotropic phase.
                    // Celestial transmittance is sampled directly from the Transmittance LUT, which already contains ground occlusion.
                    vec2 tuv = vec2(dot(upLocal, lightDir) * 0.5 + 0.5, clamp((rr - Rg) / (Rt - Rg), 0.0, 1.0));
                    vec3 tLight = textureLod(uTransmittance, tuv, 0.0).rgb;
                    vec3 s1 = sigmaS * 0.07957747155 * tLight;
                    lDir += throughput * (s1 - s1 * stepT) / sigmaE;

                    // Transfer term: if every point emits isotropic radiance 1, the scattering source is exactly sigma_s
                    // because the sphere integral of the phase function is 1.
                    fDir += throughput * (sigmaS - sigmaS * stepT) / sigmaE;

                    throughput *= stepT;
                }

                // Ground bounce: a Lambertian surface sends received direct light back into the atmosphere.
                // This is one of the uses of GroundAlbedo; without it, the region below the horizon becomes too dark
                // and sunset loses a layer of warm bounce light.
                if (ground)
                {
                    vec3 gp = origin + dir * tMax;
                    vec3 gn = gp / max(length(gp), 1e-6);
                    vec2 guv = vec2(dot(gn, lightDir) * 0.5 + 0.5, 0.0);
                    vec3 tg = textureLod(uTransmittance, guv, 0.0).rgb;
                    lDir += throughput * tg * (albedo * 0.31830988618) * clamp(dot(gn, lightDir), 0.0, 1.0);
                }

                lSum += lDir;
                fSum += fDir;
            }
        }

        // Take the spherical mean (weight 4PI / N times isotropic phase 1 / 4PI = 1 / N),
        // then evaluate the geometric-series sum to get infinite-order scattering.
        float invN = 1.0 / float(MsSqrtSamples * MsSqrtSamples);
        vec3 l1 = lSum * invN;
        vec3 fms = min(fSum * invN, vec3(0.999));
        imageStore(uOutput, ivec2(id), vec4(l1 / (1.0 - fms), 1.0));
    }
}
";

    /// <summary>
    /// WebGPU WGSL. Literally isomorphic to HLSL. Transmittance is bound as a sampled texture at binding 1,
    /// and all sampling goes through the engine compute sampler at @binding(15)
    /// (automatically appended by JS-side hasSampled, see seasonWebGPU.js).
    /// All sampling uses textureSampleLevel(..., 0.0): LUTs have no mips, and this also avoids the
    /// compile-time restrictions around implicit derivatives in non-uniform WGSL control flow.
    /// </summary>
    const string SourceMultiScatterWgsl = @"
struct SkyParams
{
    uSun : vec4f,
    uSunColor : vec4f,
    uLut : vec4f,
    uRayleigh : vec4f,
    uMie : vec4f,
    uPlanet : vec4f,
    uMoon : vec4f,
    uMoonColor : vec4f,
};

@group(0) @binding(0) var<uniform> params : SkyParams;
@group(0) @binding(1) var uTransmittance : texture_2d<f32>;
@group(0) @binding(2) var uOutput : texture_storage_2d<rgba16float, write>;
@group(0) @binding(15) var uSampler : sampler;

// Number of sphere directions = MsSqrtSamples^2 (Hillaire's original value is 8x8 = 64).
// psi_ms is extremely low frequency, and 64 directions already converge below rgba16f quantization.
const MsSqrtSamples : i32 = 8;

fn RaySphere(r : f32, mu : f32, R : f32) -> f32
{
    let disc = r * r * (mu * mu - 1.0) + R * R;
    var t = -1.0;
    if (disc >= 0.0)
    {
        let sq = sqrt(disc);
        let tNear = -r * mu - sq;
        let tFar = -r * mu + sq;
        t = select(tFar, tNear, tNear >= 0.0);
    }
    return t;
}

fn HitsGround(r : f32, mu : f32, Rg : f32) -> bool
{
    return mu < 0.0 && (r * r * (mu * mu - 1.0) + Rg * Rg) >= 0.0;
}

fn AirDensity(h : f32, rayleighH : f32, mieH : f32) -> vec2f
{
    let hc = max(h, 0.0);
    return vec2f(exp(-hc / rayleighH), exp(-hc / mieH));
}

@compute @workgroup_size(8, 8, 1)
fn CSMain(@builtin(global_invocation_id) id : vec3<u32>)
{
    let w = u32(params.uLut.x);
    let h = u32(params.uLut.y);
    if (id.x < w && id.y < h)
    {
        let Rg = params.uPlanet.x;
        let Rt = params.uPlanet.y;

        // Parameterization literally matches the Transmittance LUT so the consumer can reuse the same UV.
        let cosZ = ((f32(id.x) + 0.5) / params.uLut.x) * 2.0 - 1.0;
        let r = Rg + ((f32(id.y) + 0.5) / params.uLut.y) * (Rt - Rg);

        // Local coordinates: the observation point sits at (0,r,0), local up is +Y,
        // and light azimuth is irrelevant because of rotational symmetry around Y, so it is fixed in the YZ plane.
        let origin = vec3f(0.0, r, 0.0);
        let lightDir = vec3f(0.0, cosZ, sqrt(clamp(1.0 - cosZ * cosZ, 0.0, 1.0)));
        let albedo = params.uPlanet.w;
        let steps = max(i32(params.uLut.z), 1);

        var lSum = vec3f(0.0);
        var fSum = vec3f(0.0);

        for (var sy : i32 = 0; sy < MsSqrtSamples; sy = sy + 1)
        {
            for (var sx : i32 = 0; sx < MsSqrtSamples; sx = sx + 1)
            {
                // Stratified uniform sphere directions: uniform azimuth + uniform cosine -> equal solid-angle weight.
                let a = (f32(sx) + 0.5) / f32(MsSqrtSamples);
                let b = (f32(sy) + 0.5) / f32(MsSqrtSamples);
                let theta = 6.28318530718 * a;
                let dy = 1.0 - 2.0 * b;
                let dxz = sqrt(clamp(1.0 - dy * dy, 0.0, 1.0));
                let dir = vec3f(dxz * cos(theta), dy, dxz * sin(theta));

                let ground = HitsGround(r, dir.y, Rg);
                let tMax = select(max(RaySphere(r, dir.y, Rt), 0.0), max(RaySphere(r, dir.y, Rg), 0.0), ground);
                let dt = tMax / f32(steps);

                var throughput = vec3f(1.0);
                var lDir = vec3f(0.0);
                var fDir = vec3f(0.0);
                for (var i : i32 = 0; i < steps; i = i + 1)
                {
                    let t = (f32(i) + 0.5) * dt;
                    let pos = origin + dir * t;
                    let rr = max(length(pos), 1e-6);
                    let upLocal = pos / rr;
                    let dens = AirDensity(rr - Rg, params.uMie.y, params.uMie.z);

                    let sigmaS = params.uRayleigh.rgb * dens.x + vec3f(params.uRayleigh.a * dens.y);
                    let sigmaE = max(params.uRayleigh.rgb * dens.x + vec3f(params.uMie.x * dens.y), vec3f(1e-7));
                    let stepT = exp(-sigmaE * dt);

                    // First-order scattering source: unit irradiance with isotropic phase.
                    // Celestial transmittance is sampled directly from the Transmittance LUT, which already contains ground occlusion.
                    let tuv = vec2f(dot(upLocal, lightDir) * 0.5 + 0.5, clamp((rr - Rg) / (Rt - Rg), 0.0, 1.0));
                    let tLight = textureSampleLevel(uTransmittance, uSampler, tuv, 0.0).rgb;
                    let s1 = sigmaS * 0.07957747155 * tLight;
                    lDir += throughput * (s1 - s1 * stepT) / sigmaE;

                    // Transfer term: if every point emits isotropic radiance 1, the scattering source is exactly sigma_s
                    // because the sphere integral of the phase function is 1.
                    fDir += throughput * (sigmaS - sigmaS * stepT) / sigmaE;

                    throughput *= stepT;
                }

                // Ground bounce: a Lambertian surface sends received direct light back into the atmosphere.
                // This is one of the uses of GroundAlbedo; without it, the region below the horizon becomes too dark
                // and sunset loses a layer of warm bounce light.
                if (ground)
                {
                    let gp = origin + dir * tMax;
                    let gn = gp / max(length(gp), 1e-6);
                    let guv = vec2f(dot(gn, lightDir) * 0.5 + 0.5, 0.0);
                    let tg = textureSampleLevel(uTransmittance, uSampler, guv, 0.0).rgb;
                    lDir += throughput * tg * (albedo * 0.31830988618) * clamp(dot(gn, lightDir), 0.0, 1.0);
                }

                lSum += lDir;
                fSum += fDir;
            }
        }

        // Take the spherical mean (weight 4PI / N times isotropic phase 1 / 4PI = 1 / N),
        // then evaluate the geometric-series sum to get infinite-order scattering.
        let invN = 1.0 / f32(MsSqrtSamples * MsSqrtSamples);
        let l1 = lSum * invN;
        let fms = min(fSum * invN, vec3f(0.999));
        textureStore(uOutput, vec2i(id.xy), vec4f(l1 / (vec3f(1.0) - fms), 1.0));
    }
}
";

    /// <summary>
    /// Apple Metal MSL. Literally isomorphic to GLSL. Transmittance is bound as a sampled texture at [[texture(0)]],
    /// output writes to [[texture(1)]], and sampling always goes through [[sampler(0)]]
    /// (DispatchCompute binds the StaticSampler, linear-clamp). All samples use explicit level(0.0),
    /// for the same reasons given in SourceTransmittanceMsl.
    /// </summary>
    const string SourceMultiScatterMsl = @"
#include <metal_stdlib>
#include <simd/simd.h>
using namespace metal;

struct SkyParams
{
    float4 uSun;
    float4 uSunColor;
    float4 uLut;
    float4 uRayleigh;
    float4 uMie;
    float4 uPlanet;
    float4 uMoon;
    float4 uMoonColor;
};

// Number of sphere directions = MsSqrtSamples^2 (Hillaire's original value is 8x8 = 64).
// psi_ms is extremely low frequency, and 64 directions already converge below rgba16f quantization.
constant int MsSqrtSamples = 8;

float RaySphere(float r, float mu, float R)
{
    float disc = r * r * (mu * mu - 1.0) + R * R;
    float t = -1.0;
    if (disc >= 0.0)
    {
        float sq = sqrt(disc);
        float tNear = -r * mu - sq;
        float tFar = -r * mu + sq;
        t = tNear >= 0.0 ? tNear : tFar;
    }
    return t;
}

bool HitsGround(float r, float mu, float Rg)
{
    return mu < 0.0 && (r * r * (mu * mu - 1.0) + Rg * Rg) >= 0.0;
}

float2 AirDensity(float h, float rayleighH, float mieH)
{
    float hc = max(h, 0.0);
    return float2(exp(-hc / rayleighH), exp(-hc / mieH));
}

kernel void CSMain(uint3 gid [[thread_position_in_grid]],
                   constant SkyParams& p [[buffer(0)]],
                   texture2d<float> uTransmittance [[texture(0)]],
                   texture2d<float, access::write> uOutput [[texture(1)]],
                   sampler s [[sampler(0)]])
{
    uint w = uint(p.uLut.x);
    uint h = uint(p.uLut.y);
    if (gid.x < w && gid.y < h)
    {
        float Rg = p.uPlanet.x;
        float Rt = p.uPlanet.y;

        // Parameterization literally matches the Transmittance LUT so the consumer can reuse the same UV.
        float cosZ = ((float(gid.x) + 0.5) / p.uLut.x) * 2.0 - 1.0;
        float r = Rg + ((float(gid.y) + 0.5) / p.uLut.y) * (Rt - Rg);

        // Local coordinates: the observation point sits at (0,r,0), local up is +Y,
        // and light azimuth is irrelevant because of rotational symmetry around Y, so it is fixed in the YZ plane.
        float3 origin = float3(0.0, r, 0.0);
        float3 lightDir = float3(0.0, cosZ, sqrt(clamp(1.0 - cosZ * cosZ, 0.0, 1.0)));
        float albedo = p.uPlanet.w;
        int steps = max(int(p.uLut.z), 1);

        float3 lSum = float3(0.0);
        float3 fSum = float3(0.0);

        for (int sy = 0; sy < MsSqrtSamples; sy++)
        {
            for (int sx = 0; sx < MsSqrtSamples; sx++)
            {
                // Stratified uniform sphere directions: uniform azimuth + uniform cosine -> equal solid-angle weight.
                float a = (float(sx) + 0.5) / float(MsSqrtSamples);
                float b = (float(sy) + 0.5) / float(MsSqrtSamples);
                float theta = 6.28318530718 * a;
                float dy = 1.0 - 2.0 * b;
                float dxz = sqrt(clamp(1.0 - dy * dy, 0.0, 1.0));
                float3 dir = float3(dxz * cos(theta), dy, dxz * sin(theta));

                bool ground = HitsGround(r, dir.y, Rg);
                float tMax = ground ? max(RaySphere(r, dir.y, Rg), 0.0) : max(RaySphere(r, dir.y, Rt), 0.0);
                float dt = tMax / float(steps);

                float3 throughput = float3(1.0);
                float3 lDir = float3(0.0);
                float3 fDir = float3(0.0);
                for (int i = 0; i < steps; i++)
                {
                    float t = (float(i) + 0.5) * dt;
                    float3 pos = origin + dir * t;
                    float rr = max(length(pos), 1e-6);
                    float3 upLocal = pos / rr;
                    float2 dens = AirDensity(rr - Rg, p.uMie.y, p.uMie.z);

                    float3 sigmaS = p.uRayleigh.rgb * dens.x + float3(p.uRayleigh.a * dens.y);
                    float3 sigmaE = max(p.uRayleigh.rgb * dens.x + float3(p.uMie.x * dens.y), float3(1e-7));
                    float3 stepT = exp(-sigmaE * dt);

                    // First-order scattering source: unit irradiance with isotropic phase.
                    // Celestial transmittance is sampled directly from the Transmittance LUT, which already contains ground occlusion.
                    float2 tuv = float2(dot(upLocal, lightDir) * 0.5 + 0.5, clamp((rr - Rg) / (Rt - Rg), 0.0, 1.0));
                    float3 tLight = uTransmittance.sample(s, tuv, level(0.0)).rgb;
                    float3 s1 = sigmaS * 0.07957747155 * tLight;
                    lDir += throughput * (s1 - s1 * stepT) / sigmaE;

                    // Transfer term: if every point emits isotropic radiance 1, the scattering source is exactly sigma_s
                    // because the sphere integral of the phase function is 1.
                    fDir += throughput * (sigmaS - sigmaS * stepT) / sigmaE;

                    throughput *= stepT;
                }

                // Ground bounce: a Lambertian surface sends received direct light back into the atmosphere.
                // This is one of the uses of GroundAlbedo; without it, the region below the horizon becomes too dark
                // and sunset loses a layer of warm bounce light.
                if (ground)
                {
                    float3 gp = origin + dir * tMax;
                    float3 gn = gp / max(length(gp), 1e-6);
                    float2 guv = float2(dot(gn, lightDir) * 0.5 + 0.5, 0.0);
                    float3 tg = uTransmittance.sample(s, guv, level(0.0)).rgb;
                    lDir += throughput * tg * (albedo * 0.31830988618) * clamp(dot(gn, lightDir), 0.0, 1.0);
                }

                lSum += lDir;
                fSum += fDir;
            }
        }

        // Take the spherical mean (weight 4PI / N times isotropic phase 1 / 4PI = 1 / N),
        // then evaluate the geometric-series sum to get infinite-order scattering.
        float invN = 1.0 / float(MsSqrtSamples * MsSqrtSamples);
        float3 l1 = lSum * invN;
        float3 fms = min(fSum * invN, float3(0.999));
        uOutput.write(float4(l1 / (float3(1.0) - fms), 1.0), uint2(gid.xy));
    }
}
";

    /// <summary>
    /// D3D12 cs_5_0: Sky-View LUT single-scattering ray march (**dual light sources**: sun and moon contribute one term each).
    /// The inverse mapping from texel-center uv to world direction **must remain a literal mirror of the forward formula in the main shader**
    /// (the Atmosphere class header is the sole source of truth).
    /// Stepping is non-uniform: t_i = tMax·(i/N)² (uniform spacing badly undersamples grazing views and is one root cause of the
    /// horizon color discontinuity), and each segment uses the midpoint plus Hillaire's analytic segment integral
    /// (Sint = (S − S·exp(−σe·dt))/σe), so 16 steps are enough without visible banding.
    /// Both lights share the same segment extinction/throughput, so the two in-scattering terms are summed first and integrated once
    /// per segment (exact, not approximate).
    /// Each celestial term is single scattering with exact phase plus the ψ_ms isotropic energy term; both lights share the same static
    /// MS LUT (see the class header for the rationale).
    /// </summary>
    const string SourceSkyViewHlsl = @"
cbuffer SkyParams : register(b0)
{
    float4 uSun;
    float4 uSunColor;
    float4 uLut;
    float4 uRayleigh;
    float4 uMie;
    float4 uPlanet;
    float4 uMoon;
    float4 uMoonColor;
};

Texture2D<float4> uTransmittance : register(t0);
Texture2D<float4> uMultiScatter : register(t1);
SamplerState uLinearClamp : register(s0);
RWTexture2D<float4> uOutput : register(u0);

float RaySphere(float r, float mu, float R)
{
    float disc = r * r * (mu * mu - 1.0) + R * R;
    float t = -1.0;
    if (disc >= 0.0)
    {
        float sq = sqrt(disc);
        float tNear = -r * mu - sq;
        float tFar = -r * mu + sq;
        t = tNear >= 0.0 ? tNear : tFar;
    }
    return t;
}

bool HitsGround(float r, float mu, float Rg)
{
    return mu < 0.0 && (r * r * (mu * mu - 1.0) + Rg * Rg) >= 0.0;
}

float2 AirDensity(float h, float rayleighH, float mieH)
{
    float hc = max(h, 0.0);
    return float2(exp(-hc / rayleighH), exp(-hc / mieH));
}

// Single-light in-scattering at the sample point: exact-phase single scattering plus the ψ_ms isotropic multiple-scattering
// energy term, multiplied by the celestial radiance at the end.
// When the light falls below the horizon, the Transmittance LUT already bakes planetary self-occlusion as zero, so this term
// vanishes naturally with **no day/night branch**.
// That is exactly why the two light sources can always be summed unconditionally while remaining C0 continuous through the handoff
// (see the Atmosphere class header).
// Both lights share one MS LUT: ψ_ms is normalized by unit white-light irradiance, and atmospheric transport is linear per
// channel, so multiplying by each lightRadiance reconstructs the exact result (not an approximation).
float3 LightInScatter(float3 viewDir, float3 upLocal, float rh01, float4 lightDir, float3 lightRadiance,
                      float3 scatR, float scatM, float g, float msGain)
{
    float c = dot(viewDir, lightDir.xyz);
    float phaseR = 0.05968310366 * (1.0 + c * c);        // 3/(16π)·(1+cos²θ)
    float g2 = g * g;
    float hgDen = max(1.0 + g2 - 2.0 * g * c, 1e-4);
    float phaseM = 0.07957747155 * (1.0 - g2) / (hgDen * sqrt(hgDen));   // 1/(4π)·HG

    // Transmittance from the light to this point: sample the Transmittance LUT directly
    // (ground occlusion is already baked in), so no shadow ray is needed.
    float2 tuv = float2(dot(upLocal, lightDir.xyz) * 0.5 + 0.5, rh01);
    float3 tLight = uTransmittance.SampleLevel(uLinearClamp, tuv, 0.0).rgb;

    // MS energy term: ψ_ms uses the same literal parameterization as Transmittance, so they share the same uv.
    // Because it is already isotropic radiance, it is multiplied by neither the phase function nor tLight
    // (the transmittance of each path order is already baked into ψ_ms itself).
    float3 psiMs = uMultiScatter.SampleLevel(uLinearClamp, tuv, 0.0).rgb * msGain;

    return ((scatR * phaseR + scatM * phaseM) * tLight + (scatR + scatM) * psiMs) * lightRadiance;
}

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    uint w = (uint)uLut.x;
    uint h = (uint)uLut.y;
    if (id.x < w && id.y < h)
    {
        float Rg = uPlanet.x;
        float Rt = uPlanet.y;

        // Texel-center uv -> world view direction (inverse of the parameterization defined in the Atmosphere class header).
        float u = (float(id.x) + 0.5) / uLut.x;
        float v = (float(id.y) + 0.5) / uLut.y;
        float phi = (u - 0.5) * 6.28318530718;
        float s = 1.0 - 2.0 * v;
        float cosZ = sign(s) * s * s;                       // = dir.y
        float sinZ = sqrt(saturate(1.0 - cosZ * cosZ));
        float3 dir = float3(sinZ * sin(phi), cosZ, -sinZ * cos(phi));

        // Observation point: the planet center is straight below, so local up = world +Y.
        float r0 = Rg + uPlanet.z;
        float3 origin = float3(0.0, r0, 0.0);
        float mu = dir.y;

        // Ray endpoint: stop at the ground if the ray hits it (the lower hemisphere shows near-ground haze instead of pure black);
        // otherwise stop at the top of the atmosphere.
        float tMax = HitsGround(r0, mu, Rg) ? max(RaySphere(r0, mu, Rg), 0.0)
                                           : max(RaySphere(r0, mu, Rt), 0.0);

        float g = uMie.w;
        float msGain = uLut.w;
        float3 sunRadiance = uSun.w * uSunColor.rgb;
        float3 moonRadiance = uMoon.w * uMoonColor.rgb;
        float3 radiance = float3(0.0, 0.0, 0.0);
        float3 throughput = float3(1.0, 1.0, 1.0);

        int steps = max((int)uLut.z, 1);
        float invSteps = 1.0 / float(steps);
        float tPrev = 0.0;
        for (int i = 1; i <= steps; i++)
        {
            // Non-uniform stepping: the quadratic distribution concentrates samples near the origin,
            // where density and contribution are highest.
            float f = float(i) * invSteps;
            float tCur = tMax * f * f;
            float dt = tCur - tPrev;
            float tMid = 0.5 * (tPrev + tCur);
            tPrev = tCur;

            float3 pos = origin + dir * tMid;
            float rr = max(length(pos), 1e-6);
            float3 upLocal = pos / rr;
            float rh01 = saturate((rr - Rg) / (Rt - Rg));
            float2 dens = AirDensity(rr - Rg, uMie.y, uMie.z);

            float3 scatR = uRayleigh.rgb * dens.x;
            float scatM = uRayleigh.a * dens.y;
            float3 extinction = max(uRayleigh.rgb * dens.x + uMie.x * dens.y, 1e-7);

            // One term per celestial body: phase, transmittance, and MS lookup are evaluated independently,
            // while scattering coefficients and extinction are shared.
            float3 inScatter = LightInScatter(dir, upLocal, rh01, uSun, sunRadiance, scatR, scatM, g, msGain)
                             + LightInScatter(dir, upLocal, rh01, uMoon, moonRadiance, scatR, scatM, g, msGain);

            // Hillaire analytic segment integral: treat in-scattering as constant within the segment and handle extinction
            // exactly as an exponential.
            float3 stepT = exp(-extinction * dt);
            radiance += throughput * (inScatter - inScatter * stepT) / extinction;
            throughput *= stepT;
        }

        // Night-sky floor term: tint it by the Rayleigh channel ratio (about (0.175, 0.41, 1.0), a cool blue)
        // so the night sky is not pure black.
        // It is not mutually exclusive with the celestial terms; in daylight it sits two orders of magnitude below the scattering
        // and is effectively invisible.
        radiance += uSunColor.a * (uRayleigh.rgb / max(uRayleigh.b, 1e-6));

        // Output linear HDR directly (1-4 contract: no tonemap/clamp here; the only final closure happens in FinalBlit).
        uOutput[id.xy] = float4(radiance, 1.0);
    }
}
";

    /// <summary>
    /// Vulkan GLSL 450. Literal isomorph of HLSL: both LUTs are CombinedImageSamplers (immutable linear).
    /// </summary>
    const string SourceSkyViewGlsl = @"#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(push_constant) uniform SkyParams
{
    vec4 uSun;
    vec4 uSunColor;
    vec4 uLut;
    vec4 uRayleigh;
    vec4 uMie;
    vec4 uPlanet;
    vec4 uMoon;
    vec4 uMoonColor;
};

layout(binding = 1) uniform sampler2D uTransmittance;
layout(binding = 2) uniform sampler2D uMultiScatter;
layout(binding = 3, rgba16f) uniform writeonly image2D uOutput;

float RaySphere(float r, float mu, float R)
{
    float disc = r * r * (mu * mu - 1.0) + R * R;
    float t = -1.0;
    if (disc >= 0.0)
    {
        float sq = sqrt(disc);
        float tNear = -r * mu - sq;
        float tFar = -r * mu + sq;
        t = tNear >= 0.0 ? tNear : tFar;
    }
    return t;
}

bool HitsGround(float r, float mu, float Rg)
{
    return mu < 0.0 && (r * r * (mu * mu - 1.0) + Rg * Rg) >= 0.0;
}

vec2 AirDensity(float h, float rayleighH, float mieH)
{
    float hc = max(h, 0.0);
    return vec2(exp(-hc / rayleighH), exp(-hc / mieH));
}

// Single-light in-scattering at the sample point: exact-phase single scattering plus the ψ_ms isotropic multiple-scattering
// energy term, multiplied by the celestial radiance at the end.
// When the light falls below the horizon, the Transmittance LUT already bakes planetary self-occlusion as zero, so this term
// vanishes naturally with **no day/night branch**.
// That is exactly why the two light sources can always be summed unconditionally while remaining C0 continuous through the handoff
// (see the Atmosphere class header).
// Both lights share one MS LUT: ψ_ms is normalized by unit white-light irradiance, and atmospheric transport is linear per
// channel, so multiplying by each lightRadiance reconstructs the exact result (not an approximation).
vec3 LightInScatter(vec3 viewDir, vec3 upLocal, float rh01, vec4 lightDir, vec3 lightRadiance,
                    vec3 scatR, float scatM, float g, float msGain)
{
    float c = dot(viewDir, lightDir.xyz);
    float phaseR = 0.05968310366 * (1.0 + c * c);        // 3/(16π)·(1+cos²θ)
    float g2 = g * g;
    float hgDen = max(1.0 + g2 - 2.0 * g * c, 1e-4);
    float phaseM = 0.07957747155 * (1.0 - g2) / (hgDen * sqrt(hgDen));   // 1/(4π)·HG

    // Transmittance from the light to this point: sample the Transmittance LUT directly
    // (ground occlusion is already baked in), so no shadow ray is needed.
    vec2 tuv = vec2(dot(upLocal, lightDir.xyz) * 0.5 + 0.5, rh01);
    vec3 tLight = textureLod(uTransmittance, tuv, 0.0).rgb;

    // MS energy term: ψ_ms uses the same literal parameterization as Transmittance, so they share the same uv.
    // Because it is already isotropic radiance, it is multiplied by neither the phase function nor tLight
    // (the transmittance of each path order is already baked into ψ_ms itself).
    vec3 psiMs = textureLod(uMultiScatter, tuv, 0.0).rgb * msGain;

    return ((scatR * phaseR + vec3(scatM * phaseM)) * tLight + (scatR + vec3(scatM)) * psiMs) * lightRadiance;
}

void main()
{
    uvec2 id = gl_GlobalInvocationID.xy;
    uint w = uint(uLut.x);
    uint h = uint(uLut.y);
    if (id.x < w && id.y < h)
    {
        float Rg = uPlanet.x;
        float Rt = uPlanet.y;

        // Texel-center uv -> world view direction (inverse of the parameterization defined in the Atmosphere class header).
        float u = (float(id.x) + 0.5) / uLut.x;
        float v = (float(id.y) + 0.5) / uLut.y;
        float phi = (u - 0.5) * 6.28318530718;
        float s = 1.0 - 2.0 * v;
        float cosZ = sign(s) * s * s;                       // = dir.y
        float sinZ = sqrt(clamp(1.0 - cosZ * cosZ, 0.0, 1.0));
        vec3 dir = vec3(sinZ * sin(phi), cosZ, -sinZ * cos(phi));

        // Observation point: the planet center is straight below, so local up = world +Y.
        float r0 = Rg + uPlanet.z;
        vec3 origin = vec3(0.0, r0, 0.0);
        float mu = dir.y;

        // Ray endpoint: stop at the ground if the ray hits it (the lower hemisphere shows near-ground haze instead of pure black);
        // otherwise stop at the top of the atmosphere.
        float tMax = HitsGround(r0, mu, Rg) ? max(RaySphere(r0, mu, Rg), 0.0)
                                           : max(RaySphere(r0, mu, Rt), 0.0);

        float g = uMie.w;
        float msGain = uLut.w;
        vec3 sunRadiance = uSun.w * uSunColor.rgb;
        vec3 moonRadiance = uMoon.w * uMoonColor.rgb;
        vec3 radiance = vec3(0.0);
        vec3 throughput = vec3(1.0);

        int steps = max(int(uLut.z), 1);
        float invSteps = 1.0 / float(steps);
        float tPrev = 0.0;
        for (int i = 1; i <= steps; i++)
        {
            // Non-uniform stepping: the quadratic distribution concentrates samples near the origin,
            // where density and contribution are highest.
            float f = float(i) * invSteps;
            float tCur = tMax * f * f;
            float dt = tCur - tPrev;
            float tMid = 0.5 * (tPrev + tCur);
            tPrev = tCur;

            vec3 pos = origin + dir * tMid;
            float rr = max(length(pos), 1e-6);
            vec3 upLocal = pos / rr;
            float rh01 = clamp((rr - Rg) / (Rt - Rg), 0.0, 1.0);
            vec2 dens = AirDensity(rr - Rg, uMie.y, uMie.z);

            vec3 scatR = uRayleigh.rgb * dens.x;
            float scatM = uRayleigh.a * dens.y;
            vec3 extinction = max(uRayleigh.rgb * dens.x + vec3(uMie.x * dens.y), vec3(1e-7));

            // One term per celestial body: phase, transmittance, and MS lookup are evaluated independently,
            // while scattering coefficients and extinction are shared.
            vec3 inScatter = LightInScatter(dir, upLocal, rh01, uSun, sunRadiance, scatR, scatM, g, msGain)
                           + LightInScatter(dir, upLocal, rh01, uMoon, moonRadiance, scatR, scatM, g, msGain);

            // Hillaire analytic segment integral: treat in-scattering as constant within the segment and handle extinction
            // exactly as an exponential.
            vec3 stepT = exp(-extinction * dt);
            radiance += throughput * (inScatter - inScatter * stepT) / extinction;
            throughput *= stepT;
        }

        // Night-sky floor term: tint it by the Rayleigh channel ratio (about (0.175, 0.41, 1.0), a cool blue)
        // so the night sky is not pure black.
        // It is not mutually exclusive with the celestial terms; in daylight it sits two orders of magnitude below the scattering
        // and is effectively invisible.
        radiance += uSunColor.a * (uRayleigh.rgb / max(uRayleigh.b, 1e-6));

        // Output linear HDR directly (1-4 contract: no tonemap/clamp here; the only final closure happens in FinalBlit).
        imageStore(uOutput, ivec2(id), vec4(radiance, 1.0));
    }
}
";

    /// <summary>
    /// WebGPU WGSL. Literal isomorph of HLSL: both LUTs are bound as sampled textures (binding 1/2),
    /// and all sampling goes through the engine sampler at @binding(15) (linear-clamp, auto-appended by JS when hasSampled is true).
    /// </summary>
    const string SourceSkyViewWgsl = @"
struct SkyParams
{
    uSun : vec4f,
    uSunColor : vec4f,
    uLut : vec4f,
    uRayleigh : vec4f,
    uMie : vec4f,
    uPlanet : vec4f,
    uMoon : vec4f,
    uMoonColor : vec4f,
};

@group(0) @binding(0) var<uniform> params : SkyParams;
@group(0) @binding(1) var uTransmittance : texture_2d<f32>;
@group(0) @binding(2) var uMultiScatter : texture_2d<f32>;
@group(0) @binding(3) var uOutput : texture_storage_2d<rgba16float, write>;
@group(0) @binding(15) var uSampler : sampler;

fn RaySphere(r : f32, mu : f32, R : f32) -> f32
{
    let disc = r * r * (mu * mu - 1.0) + R * R;
    var t = -1.0;
    if (disc >= 0.0)
    {
        let sq = sqrt(disc);
        let tNear = -r * mu - sq;
        let tFar = -r * mu + sq;
        t = select(tFar, tNear, tNear >= 0.0);
    }
    return t;
}

fn HitsGround(r : f32, mu : f32, Rg : f32) -> bool
{
    return mu < 0.0 && (r * r * (mu * mu - 1.0) + Rg * Rg) >= 0.0;
}

fn AirDensity(h : f32, rayleighH : f32, mieH : f32) -> vec2f
{
    let hc = max(h, 0.0);
    return vec2f(exp(-hc / rayleighH), exp(-hc / mieH));
}

// Single-light in-scattering at the sample point: exact-phase single scattering plus the ψ_ms isotropic multiple-scattering
// energy term, multiplied by the celestial radiance at the end.
// When the light falls below the horizon, the Transmittance LUT already bakes planetary self-occlusion as zero, so this term
// vanishes naturally with **no day/night branch**.
// That is exactly why the two light sources can always be summed unconditionally while remaining C0 continuous through the handoff
// (see the Atmosphere class header).
// Both lights share one MS LUT: ψ_ms is normalized by unit white-light irradiance, and atmospheric transport is linear per
// channel, so multiplying by each lightRadiance reconstructs the exact result (not an approximation).
fn LightInScatter(viewDir : vec3f, upLocal : vec3f, rh01 : f32, lightDir : vec4f, lightRadiance : vec3f,
                  scatR : vec3f, scatM : f32, g : f32, msGain : f32) -> vec3f
{
    let c = dot(viewDir, lightDir.xyz);
    let phaseR = 0.05968310366 * (1.0 + c * c);        // 3/(16π)·(1+cos²θ)
    let g2 = g * g;
    let hgDen = max(1.0 + g2 - 2.0 * g * c, 1e-4);
    let phaseM = 0.07957747155 * (1.0 - g2) / (hgDen * sqrt(hgDen));   // 1/(4π)·HG

    // Transmittance from the light to this point: sample the Transmittance LUT directly
    // (ground occlusion is already baked in), so no shadow ray is needed.
    let tuv = vec2f(dot(upLocal, lightDir.xyz) * 0.5 + 0.5, rh01);
    let tLight = textureSampleLevel(uTransmittance, uSampler, tuv, 0.0).rgb;

    // MS energy term: ψ_ms uses the same literal parameterization as Transmittance, so they share the same uv.
    // Because it is already isotropic radiance, it is multiplied by neither the phase function nor tLight
    // (the transmittance of each path order is already baked into ψ_ms itself).
    let psiMs = textureSampleLevel(uMultiScatter, uSampler, tuv, 0.0).rgb * msGain;

    return ((scatR * phaseR + vec3f(scatM * phaseM)) * tLight + (scatR + vec3f(scatM)) * psiMs) * lightRadiance;
}

@compute @workgroup_size(8, 8, 1)
fn CSMain(@builtin(global_invocation_id) id : vec3<u32>)
{
    let w = u32(params.uLut.x);
    let h = u32(params.uLut.y);
    if (id.x < w && id.y < h)
    {
        let Rg = params.uPlanet.x;
        let Rt = params.uPlanet.y;

        // Texel-center uv -> world view direction (inverse of the parameterization defined in the Atmosphere class header).
        let u = (f32(id.x) + 0.5) / params.uLut.x;
        let v = (f32(id.y) + 0.5) / params.uLut.y;
        let phi = (u - 0.5) * 6.28318530718;
        let s = 1.0 - 2.0 * v;
        let cosZ = sign(s) * s * s;                       // = dir.y
        let sinZ = sqrt(clamp(1.0 - cosZ * cosZ, 0.0, 1.0));
        let dir = vec3f(sinZ * sin(phi), cosZ, -sinZ * cos(phi));

        // Observation point: the planet center is straight below, so local up = world +Y.
        let r0 = Rg + params.uPlanet.z;
        let origin = vec3f(0.0, r0, 0.0);
        let mu = dir.y;

        // Ray endpoint: stop at the ground if the ray hits it (the lower hemisphere shows near-ground haze instead of pure black);
        // otherwise stop at the top of the atmosphere.
        let tMax = select(max(RaySphere(r0, mu, Rt), 0.0), max(RaySphere(r0, mu, Rg), 0.0), HitsGround(r0, mu, Rg));

        let g = params.uMie.w;
        let msGain = params.uLut.w;
        let sunRadiance = params.uSun.w * params.uSunColor.rgb;
        let moonRadiance = params.uMoon.w * params.uMoonColor.rgb;
        var radiance = vec3f(0.0);
        var throughput = vec3f(1.0);

        let steps = max(i32(params.uLut.z), 1);
        let invSteps = 1.0 / f32(steps);
        var tPrev = 0.0;
        for (var i : i32 = 1; i <= steps; i = i + 1)
        {
            // Non-uniform stepping: the quadratic distribution concentrates samples near the origin,
            // where density and contribution are highest.
            let f = f32(i) * invSteps;
            let tCur = tMax * f * f;
            let dt = tCur - tPrev;
            let tMid = 0.5 * (tPrev + tCur);
            tPrev = tCur;

            let pos = origin + dir * tMid;
            let rr = max(length(pos), 1e-6);
            let upLocal = pos / rr;
            let rh01 = clamp((rr - Rg) / (Rt - Rg), 0.0, 1.0);
            let dens = AirDensity(rr - Rg, params.uMie.y, params.uMie.z);

            let scatR = params.uRayleigh.rgb * dens.x;
            let scatM = params.uRayleigh.a * dens.y;
            let extinction = max(params.uRayleigh.rgb * dens.x + vec3f(params.uMie.x * dens.y), vec3f(1e-7));

            // One term per celestial body: phase, transmittance, and MS lookup are evaluated independently,
            // while scattering coefficients and extinction are shared.
            let inScatter = LightInScatter(dir, upLocal, rh01, params.uSun, sunRadiance, scatR, scatM, g, msGain)
                          + LightInScatter(dir, upLocal, rh01, params.uMoon, moonRadiance, scatR, scatM, g, msGain);

            // Hillaire analytic segment integral: treat in-scattering as constant within the segment and handle extinction
            // exactly as an exponential.
            let stepT = exp(-extinction * dt);
            radiance += throughput * (inScatter - inScatter * stepT) / extinction;
            throughput *= stepT;
        }

        // Night-sky floor term: tint it by the Rayleigh channel ratio (about (0.175, 0.41, 1.0), a cool blue)
        // so the night sky is not pure black.
        // It is not mutually exclusive with the celestial terms; in daylight it sits two orders of magnitude below the scattering
        // and is effectively invisible.
        radiance += params.uSunColor.a * (params.uRayleigh.rgb / max(params.uRayleigh.b, 1e-6));

        // Output linear HDR directly (1-4 contract: no tonemap/clamp here; the only final closure happens in FinalBlit).
        textureStore(uOutput, vec2i(id.xy), vec4f(radiance, 1.0));
    }
}
";

    /// <summary>
    /// Apple Metal MSL. Literal isomorph of GLSL: both LUTs are bound as sampled textures at
    /// [[texture(0)]]/[[texture(1)]], the output is written to [[texture(2)]], and all sampling goes through
    /// [[sampler(0)]] with explicit level(0.0).
    /// MSL has no global texture/sampler variables, so LightInScatter must receive the textures and sampler as parameters.
    /// </summary>
    const string SourceSkyViewMsl = @"
#include <metal_stdlib>
#include <simd/simd.h>
using namespace metal;

struct SkyParams
{
    float4 uSun;
    float4 uSunColor;
    float4 uLut;
    float4 uRayleigh;
    float4 uMie;
    float4 uPlanet;
    float4 uMoon;
    float4 uMoonColor;
};

float RaySphere(float r, float mu, float R)
{
    float disc = r * r * (mu * mu - 1.0) + R * R;
    float t = -1.0;
    if (disc >= 0.0)
    {
        float sq = sqrt(disc);
        float tNear = -r * mu - sq;
        float tFar = -r * mu + sq;
        t = tNear >= 0.0 ? tNear : tFar;
    }
    return t;
}

bool HitsGround(float r, float mu, float Rg)
{
    return mu < 0.0 && (r * r * (mu * mu - 1.0) + Rg * Rg) >= 0.0;
}

float2 AirDensity(float h, float rayleighH, float mieH)
{
    float hc = max(h, 0.0);
    return float2(exp(-hc / rayleighH), exp(-hc / mieH));
}

// Single-light in-scattering at the sample point: exact-phase single scattering plus the ψ_ms isotropic multiple-scattering
// energy term, multiplied by the celestial radiance at the end.
// When the light falls below the horizon, the Transmittance LUT already bakes planetary self-occlusion as zero, so this term
// vanishes naturally with **no day/night branch**.
// That is exactly why the two light sources can always be summed unconditionally while remaining C0 continuous through the handoff
// (see the Atmosphere class header).
// Both lights share one MS LUT: ψ_ms is normalized by unit white-light irradiance, and atmospheric transport is linear per
// channel, so multiplying by each lightRadiance reconstructs the exact result (not an approximation).
float3 LightInScatter(float3 viewDir, float3 upLocal, float rh01, float4 lightDir, float3 lightRadiance,
                      float3 scatR, float scatM, float g, float msGain,
                      texture2d<float> uTransmittance, texture2d<float> uMultiScatter, sampler s)
{
    float c = dot(viewDir, lightDir.xyz);
    float phaseR = 0.05968310366 * (1.0 + c * c);        // 3/(16π)·(1+cos²θ)
    float g2 = g * g;
    float hgDen = max(1.0 + g2 - 2.0 * g * c, 1e-4);
    float phaseM = 0.07957747155 * (1.0 - g2) / (hgDen * sqrt(hgDen));   // 1/(4π)·HG

    // Transmittance from the light to this point: sample the Transmittance LUT directly
    // (ground occlusion is already baked in), so no shadow ray is needed.
    float2 tuv = float2(dot(upLocal, lightDir.xyz) * 0.5 + 0.5, rh01);
    float3 tLight = uTransmittance.sample(s, tuv, level(0.0)).rgb;

    // MS energy term: ψ_ms uses the same literal parameterization as Transmittance, so they share the same uv.
    // Because it is already isotropic radiance, it is multiplied by neither the phase function nor tLight
    // (the transmittance of each path order is already baked into ψ_ms itself).
    float3 psiMs = uMultiScatter.sample(s, tuv, level(0.0)).rgb * msGain;

    return ((scatR * phaseR + float3(scatM * phaseM)) * tLight + (scatR + float3(scatM)) * psiMs) * lightRadiance;
}

kernel void CSMain(uint3 gid [[thread_position_in_grid]],
                   constant SkyParams& p [[buffer(0)]],
                   texture2d<float> uTransmittance [[texture(0)]],
                   texture2d<float> uMultiScatter [[texture(1)]],
                   texture2d<float, access::write> uOutput [[texture(2)]],
                   sampler s [[sampler(0)]])
{
    uint w = uint(p.uLut.x);
    uint h = uint(p.uLut.y);
    if (gid.x < w && gid.y < h)
    {
        float Rg = p.uPlanet.x;
        float Rt = p.uPlanet.y;

        // Texel-center uv -> world view direction (inverse of the parameterization defined in the Atmosphere class header).
        float u = (float(gid.x) + 0.5) / p.uLut.x;
        float v = (float(gid.y) + 0.5) / p.uLut.y;
        float phi = (u - 0.5) * 6.28318530718;
        float s2 = 1.0 - 2.0 * v;
        float cosZ = sign(s2) * s2 * s2;                   // = dir.y
        float sinZ = sqrt(clamp(1.0 - cosZ * cosZ, 0.0, 1.0));
        float3 dir = float3(sinZ * sin(phi), cosZ, -sinZ * cos(phi));

        // Observation point: the planet center is straight below, so local up = world +Y.
        float r0 = Rg + p.uPlanet.z;
        float3 origin = float3(0.0, r0, 0.0);
        float mu = dir.y;

        // Ray endpoint: stop at the ground if the ray hits it (the lower hemisphere shows near-ground haze instead of pure black);
        // otherwise stop at the top of the atmosphere.
        float tMax = HitsGround(r0, mu, Rg) ? max(RaySphere(r0, mu, Rg), 0.0)
                                           : max(RaySphere(r0, mu, Rt), 0.0);

        float g = p.uMie.w;
        float msGain = p.uLut.w;
        float3 sunRadiance = p.uSun.w * p.uSunColor.rgb;
        float3 moonRadiance = p.uMoon.w * p.uMoonColor.rgb;
        float3 radiance = float3(0.0);
        float3 throughput = float3(1.0);

        int steps = max(int(p.uLut.z), 1);
        float invSteps = 1.0 / float(steps);
        float tPrev = 0.0;
        for (int i = 1; i <= steps; i++)
        {
            // Non-uniform stepping: the quadratic distribution concentrates samples near the origin,
            // where density and contribution are highest.
            float f = float(i) * invSteps;
            float tCur = tMax * f * f;
            float dt = tCur - tPrev;
            float tMid = 0.5 * (tPrev + tCur);
            tPrev = tCur;

            float3 pos = origin + dir * tMid;
            float rr = max(length(pos), 1e-6);
            float3 upLocal = pos / rr;
            float rh01 = clamp((rr - Rg) / (Rt - Rg), 0.0, 1.0);
            float2 dens = AirDensity(rr - Rg, p.uMie.y, p.uMie.z);

            float3 scatR = p.uRayleigh.rgb * dens.x;
            float scatM = p.uRayleigh.a * dens.y;
            float3 extinction = max(p.uRayleigh.rgb * dens.x + float3(p.uMie.x * dens.y), float3(1e-7));

            // One term per celestial body: phase, transmittance, and MS lookup are evaluated independently,
            // while scattering coefficients and extinction are shared.
            float3 inScatter = LightInScatter(dir, upLocal, rh01, p.uSun, sunRadiance, scatR, scatM, g, msGain, uTransmittance, uMultiScatter, s)
                             + LightInScatter(dir, upLocal, rh01, p.uMoon, moonRadiance, scatR, scatM, g, msGain, uTransmittance, uMultiScatter, s);

            // Hillaire analytic segment integral: treat in-scattering as constant within the segment and handle extinction
            // exactly as an exponential.
            float3 stepT = exp(-extinction * dt);
            radiance += throughput * (inScatter - inScatter * stepT) / extinction;
            throughput *= stepT;
        }

        // Night-sky floor term: tint it by the Rayleigh channel ratio (about (0.175, 0.41, 1.0), a cool blue)
        // so the night sky is not pure black.
        // It is not mutually exclusive with the celestial terms; in daylight it sits two orders of magnitude below the scattering
        // and is effectively invisible.
        radiance += p.uSunColor.a * (p.uRayleigh.rgb / max(p.uRayleigh.b, 1e-6));

        // Output linear HDR directly (1-4 contract: no tonemap/clamp here; the only final closure happens in FinalBlit).
        uOutput.write(float4(radiance, 1.0), uint2(gid.xy));
    }
}
";

    /// <summary>
    /// D3D12 cs_5_0 (2-5 Step C): tileable four-channel cloud-noise pre-bake. **Dispatched exactly once for the lifetime of the app**.
    ///
    /// Tileability rule: every noise function is built on **integer lattice points**, and the lattice coordinate is reduced modulo
    /// the cell count of that octave before sampling.
    /// Therefore uv and uv+1 land on the same lattice points, so the left/right and top/bottom edges connect naturally
    /// (combined with the consumer-side s2 linear-wrap sampler, sampling is seamless). Avoid mirror/fade stitching schemes;
    /// they leave visible symmetric patterns at the seam.
    ///
    /// The four channels each have a distinct role in the consumer-side density remapping (see CloudDensity in the main shader):
    /// <code>
    /// R = low-frequency value-noise FBM (4 octaves) -> cloud mass **silhouette**, deciding where clouds exist
    /// G = inverse Worley/cellular distance          -> **fluffy** breakup structure, giving the cloud interior clustered forms
    /// B = high-frequency FBM (3 octaves)            -> **erosion** detail on cloud edges
    /// A = ultra-low-frequency FBM (2 octaves)       -> **large-scale** coverage modulation, making one region cloudy and another clear
    /// </code>
    /// Why value noise instead of Perlin/gradient noise: value noise needs only one hash scalar per cell
    /// (Perlin needs a gradient vector), which saves most hash calls once all four channels are considered.
    /// Cloud appearance here is governed by the aggregate statistics after FBM layering, so the two are visually indistinguishable
    /// for this use case (Perlin's advantages, such as better isotropy and fewer axial lattice artifacts, are already mostly smoothed
    /// out by smoothstep interpolation plus four octaves).
    /// </summary>
    const string SourceCloudNoiseHlsl = @"
cbuffer CloudNoiseParams : register(b0)
{
    float4 uNoise;   // x=width, y=height, z=cell count of the lowest-frequency octave, w=reserved
};

RWTexture2D<float4> uOutput : register(u0);

// Integer lattice-point hash (reduce by the period first -> equivalent lattice points inside the same uv period must map to the
// same value; this is the **only** basis for tileability).
// The period is always **uint**: ShaderCompiler throws on any non-empty errorBlob (zero tolerance for warnings; see DXCompute),
// while int % and / trigger X3556 (integer modulus/divides may be much slower). This kernel actually failed to compile because
// of that once, and because cloud noise is an optional additive feature, the soft-failure symptom was simply no clouds in the sky
// with no error anywhere in the pipeline (the root cause captured on hardware in 2026-08). uint modulus also really is faster.
// Add 16 periods before taking the modulus: lattice coordinates may be negative (Worley needs baseCell-1), and casting negatives
// to uint turns them into astronomical values; the offset is an integer multiple of the period, so the modulo result is unchanged.
uint HashCell(int2 c, uint period)
{
    int bias = int(period) * 16;
    uint2 w = uint2(c + int2(bias, bias)) % period;
    uint n = w.x * 1597334677u + w.y * 3812015801u;
    n = (n ^ (n >> 13)) * 1274126177u;
    return n ^ (n >> 16);
}

float Hash01(int2 c, uint period)
{
    return float(HashCell(c, period) & 0x00FFFFFFu) * (1.0 / 16777215.0);
}

// Value noise for a single octave (smoothstep interpolation -> C1 continuous, with no visible grid lines).
float ValueNoise(float2 uv, uint period)
{
    float2 p = uv * float(period);
    float2 ip = floor(p);
    float2 f = p - ip;
    float2 s = f * f * (3.0 - 2.0 * f);
    int2 c = int2(ip);
    float a = Hash01(c, period);
    float b = Hash01(c + int2(1, 0), period);
    float d = Hash01(c + int2(0, 1), period);
    float e = Hash01(c + int2(1, 1), period);
    return lerp(lerp(a, b, s.x), lerp(d, e, s.x), s.y);
}

// Standard FBM: double the lattice frequency and halve the amplitude per octave; normalize by amplitude so the result stays in 0..1.
float Fbm(float2 uv, uint period, int octaves)
{
    float sum = 0.0;
    float norm = 0.0;
    float amp = 0.5;
    uint per = period;
    for (int i = 0; i < octaves; i++)
    {
        sum += amp * ValueNoise(uv, per);
        norm += amp;
        amp *= 0.5;
        per *= 2u;
    }
    return sum / max(norm, 1e-5);
}

// Worley (cellular): distance to the nearest feature point. Feature point = lattice point + hash jitter, so modulo-by-period
// is enough to make it tileable as well.
// The second component uses an **offset hash** from the same lattice point rather than a neighboring hash: the offset is constant,
// and (c + off) mod period is still a periodic function of c.
float WorleyDist(float2 uv, uint period)
{
    float2 p = uv * float(period);
    float2 ip = floor(p);
    int2 baseCell = int2(ip);
    float best = 8.0;
    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            int2 c = baseCell + int2(x, y);
            float2 jitter = float2(Hash01(c, period), Hash01(c + int2(37, 91), period));
            float2 feature = ip + float2(float(x), float(y)) + jitter;
            best = min(best, distance(p, feature));
        }
    }
    return best;
}

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    uint w = (uint)uNoise.x;
    uint h = (uint)uNoise.y;
    if (id.x < w && id.y < h)
    {
        // Half-texel center, aligned with the consumer-side continuous-uv bilinear sampling;
        // crossing the 0/1 seam is stitched automatically by wrap addressing.
        float2 uv = (float2(id.xy) + 0.5) / float2(uNoise.x, uNoise.y);
        uint per = max((uint)uNoise.z, 1u);

        float shape = Fbm(uv, per, 4);
        float floc = 1.0 - saturate(WorleyDist(uv, per * 2u));
        float erode = Fbm(uv, per * 4u, 3);
        float cover = Fbm(uv, max(per / 2u, 1u), 2);

        uOutput[id.xy] = float4(shape, floc, erode, cover);
    }
}
";

    /// <summary>
    /// Vulkan GLSL 450 (2-5 Step C). Literal isomorph of HLSL: it carries its own dedicated 16B push_constant block
    /// (leaving the earlier 128B untouched; rationale in the class header), and uOutput is an rgba8unorm storage image.
    /// </summary>
    const string SourceCloudNoiseGlsl = @"#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(push_constant) uniform CloudNoiseParams
{
    vec4 uNoise;   // x=width, y=height, z=cell count of the lowest-frequency octave, w=reserved
};

layout(binding = 1, rgba8) uniform writeonly image2D uOutput;

// Integer lattice-point hash (reduce by the period first -> equivalent lattice points inside the same uv period must map to the
// same value; this is the **only** basis for tileability).
// The period is always uint to stay bit-identical with HLSL. Add 16 periods before taking the modulus: lattice coordinates may be
// negative (Worley needs baseCell-1), and casting negatives to uint turns them into astronomical values; the offset is an integer
// multiple of the period, so the modulo result is unchanged.
uint HashCell(ivec2 c, uint period)
{
    int bias = int(period) * 16;
    uvec2 w = uvec2(c + ivec2(bias, bias)) % period;
    uint n = w.x * 1597334677u + w.y * 3812015801u;
    n = (n ^ (n >> 13)) * 1274126177u;
    return n ^ (n >> 16);
}

float Hash01(ivec2 c, uint period)
{
    return float(HashCell(c, period) & 0x00FFFFFFu) * (1.0 / 16777215.0);
}

// Value noise for a single octave (smoothstep interpolation -> C1 continuous, with no visible grid lines).
float ValueNoise(vec2 uv, uint period)
{
    vec2 p = uv * float(period);
    vec2 ip = floor(p);
    vec2 f = p - ip;
    vec2 s = f * f * (3.0 - 2.0 * f);
    ivec2 c = ivec2(ip);
    float a = Hash01(c, period);
    float b = Hash01(c + ivec2(1, 0), period);
    float d = Hash01(c + ivec2(0, 1), period);
    float e = Hash01(c + ivec2(1, 1), period);
    return mix(mix(a, b, s.x), mix(d, e, s.x), s.y);
}

// Standard FBM: double the lattice frequency and halve the amplitude per octave; normalize by amplitude so the result stays in 0..1.
float Fbm(vec2 uv, uint period, int octaves)
{
    float sum = 0.0;
    float norm = 0.0;
    float amp = 0.5;
    uint per = period;
    for (int i = 0; i < octaves; i++)
    {
        sum += amp * ValueNoise(uv, per);
        norm += amp;
        amp *= 0.5;
        per *= 2u;
    }
    return sum / max(norm, 1e-5);
}

// Worley (cellular): distance to the nearest feature point. Feature point = lattice point + hash jitter, so modulo-by-period
// is enough to make it tileable as well.
// The second component uses an **offset hash** from the same lattice point rather than a neighboring hash: the offset is constant,
// and (c + off) mod period is still a periodic function of c.
float WorleyDist(vec2 uv, uint period)
{
    vec2 p = uv * float(period);
    vec2 ip = floor(p);
    ivec2 baseCell = ivec2(ip);
    float best = 8.0;
    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            ivec2 c = baseCell + ivec2(x, y);
            vec2 jitter = vec2(Hash01(c, period), Hash01(c + ivec2(37, 91), period));
            vec2 feature = ip + vec2(float(x), float(y)) + jitter;
            best = min(best, distance(p, feature));
        }
    }
    return best;
}

void main()
{
    uvec2 id = gl_GlobalInvocationID.xy;
    uint w = uint(uNoise.x);
    uint h = uint(uNoise.y);
    if (id.x < w && id.y < h)
    {
        // Half-texel center, aligned with the consumer-side continuous-uv bilinear sampling;
        // crossing the 0/1 seam is stitched automatically by wrap addressing.
        vec2 uv = (vec2(id) + 0.5) / vec2(uNoise.x, uNoise.y);
        uint per = max(uint(uNoise.z), 1u);

        float shape = Fbm(uv, per, 4);
        float floc = 1.0 - clamp(WorleyDist(uv, per * 2u), 0.0, 1.0);
        float erode = Fbm(uv, per * 4u, 3);
        float cover = Fbm(uv, max(per / 2u, 1u), 2);

        imageStore(uOutput, ivec2(id), vec4(shape, floc, erode, cover));
    }
}
";

    /// <summary>
    /// WebGPU WGSL (2-5 Step C). Literal isomorph of HLSL: it carries its own dedicated 16B Params block
    /// (leaving the earlier 128B untouched; rationale in the class header), and uOutput is an rgba8unorm write-only storage texture.
    /// </summary>
    const string SourceCloudNoiseWgsl = @"
struct CloudNoiseParams
{
    uNoise : vec4f,   // x=width, y=height, z=cell count of the lowest-frequency octave, w=reserved
};

@group(0) @binding(0) var<uniform> params : CloudNoiseParams;
@group(0) @binding(1) var uOutput : texture_storage_2d<rgba8unorm, write>;

// Integer lattice-point hash (reduce by the period first -> equivalent lattice points inside the same uv period must map to the
// same value; this is the **only** basis for tileability).
// The period is always u32 to stay bit-identical with HLSL; see the HLSL source comment for the rationale.
// Negative lattice coordinates would be corrupted by a direct cast to u32, so 16 periods of integer bias are added first;
// because the offset is an integer multiple of the period, the modulo result is unchanged.
fn HashCell(c : vec2i, period : u32) -> u32
{
    let bias = i32(period) * 16;
    let w = vec2u(u32(c.x + bias), u32(c.y + bias)) % period;
    var n = w.x * 1597334677u + w.y * 3812015801u;
    n = (n ^ (n >> 13u)) * 1274126177u;
    return n ^ (n >> 16u);
}

fn Hash01(c : vec2i, period : u32) -> f32
{
    return f32(HashCell(c, period) & 0x00FFFFFFu) * (1.0 / 16777215.0);
}

// Value noise for a single octave (smoothstep interpolation -> C1 continuous, with no visible grid lines).
fn ValueNoise(uv : vec2f, period : u32) -> f32
{
    let p = uv * f32(period);
    let ip = floor(p);
    let f = p - ip;
    let s = f * f * (3.0 - 2.0 * f);
    let c = vec2i(ip);
    let a = Hash01(c, period);
    let b = Hash01(c + vec2i(1, 0), period);
    let d = Hash01(c + vec2i(0, 1), period);
    let e = Hash01(c + vec2i(1, 1), period);
    return mix(mix(a, b, s.x), mix(d, e, s.x), s.y);
}

// Standard FBM: double the lattice frequency and halve the amplitude per octave; normalize by amplitude so the result stays in 0..1.
fn Fbm(uv : vec2f, period : u32, octaves : i32) -> f32
{
    var sum = 0.0;
    var norm = 0.0;
    var amp = 0.5;
    var per = period;
    for (var i : i32 = 0; i < octaves; i = i + 1)
    {
        sum += amp * ValueNoise(uv, per);
        norm += amp;
        amp *= 0.5;
        per *= 2u;
    }
    return sum / max(norm, 1e-5);
}

// Worley (cellular): distance to the nearest feature point. Feature point = lattice point + hash jitter, so modulo-by-period
// is enough to make it tileable as well.
// The second component uses an **offset hash** from the same lattice point rather than a neighboring hash: the offset is constant,
// and (c + off) mod period is still a periodic function of c.
fn WorleyDist(uv : vec2f, period : u32) -> f32
{
    let p = uv * f32(period);
    let ip = floor(p);
    let baseCell = vec2i(ip);
    var best = 8.0;
    for (var y : i32 = -1; y <= 1; y = y + 1)
    {
        for (var x : i32 = -1; x <= 1; x = x + 1)
        {
            let c = baseCell + vec2i(x, y);
            let jitter = vec2f(Hash01(c, period), Hash01(c + vec2i(37, 91), period));
            let feature = ip + vec2f(f32(x), f32(y)) + jitter;
            best = min(best, distance(p, feature));
        }
    }
    return best;
}

@compute @workgroup_size(8, 8, 1)
fn CSMain(@builtin(global_invocation_id) id : vec3<u32>)
{
    let w = u32(params.uNoise.x);
    let h = u32(params.uNoise.y);
    if (id.x < w && id.y < h)
    {
        // Half-texel center, aligned with the consumer-side continuous-uv bilinear sampling;
        // crossing the 0/1 seam is stitched automatically by wrap addressing.
        let uv = (vec2f(id.xy) + vec2f(0.5)) / vec2f(params.uNoise.x, params.uNoise.y);
        let per = max(u32(params.uNoise.z), 1u);

        let shape = Fbm(uv, per, 4);
        let floc = 1.0 - clamp(WorleyDist(uv, per * 2u), 0.0, 1.0);
        let erode = Fbm(uv, per * 4u, 3);
        let cover = Fbm(uv, max(per / 2u, 1u), 2);

        textureStore(uOutput, vec2i(id.xy), vec4f(shape, floc, erode, cover));
    }
}
";

    /// <summary>
    /// Apple Metal MSL (2-5 Step C). Literal isomorph of GLSL: it carries its own dedicated 16B Params block
    /// (leaving the earlier 128B untouched; rationale in the class header), and uOutput is an rgba8unorm write-only texture
    /// (MSL declares it as texture2d&lt;float&gt;, while MTLPixelFormat provides the unorm quantization). Integer modulus follows
    /// the same uint semantics as GLSL.
    /// </summary>
    const string SourceCloudNoiseMsl = @"
#include <metal_stdlib>
#include <simd/simd.h>
using namespace metal;

struct CloudNoiseParams
{
    float4 uNoise;   // x=width, y=height, z=cell count of the lowest-frequency octave, w=reserved
};

// Integer lattice-point hash (reduce by the period first -> equivalent lattice points inside the same uv period must map to the
// same value; this is the **only** basis for tileability).
// The period is always uint to stay bit-identical with HLSL. Add 16 periods before taking the modulus: lattice coordinates may be
// negative (Worley needs baseCell-1), and casting negatives to uint turns them into astronomical values; the offset is an integer
// multiple of the period, so the modulo result is unchanged.
uint HashCell(int2 c, uint period)
{
    int bias = int(period) * 16;
    uint2 w = uint2(c + int2(bias, bias)) % period;
    uint n = w.x * 1597334677u + w.y * 3812015801u;
    n = (n ^ (n >> 13)) * 1274126177u;
    return n ^ (n >> 16);
}

float Hash01(int2 c, uint period)
{
    return float(HashCell(c, period) & 0x00FFFFFFu) * (1.0 / 16777215.0);
}

// Value noise for a single octave (smoothstep interpolation -> C1 continuous, with no visible grid lines).
float ValueNoise(float2 uv, uint period)
{
    float2 p = uv * float(period);
    float2 ip = floor(p);
    float2 f = p - ip;
    float2 s2 = f * f * (3.0 - 2.0 * f);
    int2 c = int2(ip);
    float a = Hash01(c, period);
    float b = Hash01(c + int2(1, 0), period);
    float d = Hash01(c + int2(0, 1), period);
    float e = Hash01(c + int2(1, 1), period);
    return mix(mix(a, b, s2.x), mix(d, e, s2.x), s2.y);
}

// Standard FBM: double the lattice frequency and halve the amplitude per octave; normalize by amplitude so the result stays in 0..1.
float Fbm(float2 uv, uint period, int octaves)
{
    float sum = 0.0;
    float norm = 0.0;
    float amp = 0.5;
    uint per = period;
    for (int i = 0; i < octaves; i++)
    {
        sum += amp * ValueNoise(uv, per);
        norm += amp;
        amp *= 0.5;
        per *= 2u;
    }
    return sum / max(norm, 1e-5);
}

// Worley (cellular): distance to the nearest feature point. Feature point = lattice point + hash jitter, so modulo-by-period
// is enough to make it tileable as well.
// The second component uses an **offset hash** from the same lattice point rather than a neighboring hash: the offset is constant,
// and (c + off) mod period is still a periodic function of c.
float WorleyDist(float2 uv, uint period)
{
    float2 p = uv * float(period);
    float2 ip = floor(p);
    int2 baseCell = int2(ip);
    float best = 8.0;
    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            int2 c = baseCell + int2(x, y);
            float2 jitter = float2(Hash01(c, period), Hash01(c + int2(37, 91), period));
            float2 feature = ip + float2(float(x), float(y)) + jitter;
            best = min(best, distance(p, feature));
        }
    }
    return best;
}

kernel void CSMain(uint3 gid [[thread_position_in_grid]],
                   constant CloudNoiseParams& p [[buffer(0)]],
                   texture2d<float, access::write> uOutput [[texture(0)]])
{
    uint w = uint(p.uNoise.x);
    uint h = uint(p.uNoise.y);
    if (gid.x < w && gid.y < h)
    {
        // Half-texel center, aligned with the consumer-side continuous-uv bilinear sampling;
        // crossing the 0/1 seam is stitched automatically by wrap addressing.
        float2 uv = (float2(gid.xy) + 0.5) / float2(p.uNoise.x, p.uNoise.y);
        uint per = max(uint(p.uNoise.z), 1u);

        float shape = Fbm(uv, per, 4);
        float floc = 1.0 - clamp(WorleyDist(uv, per * 2u), 0.0, 1.0);
        float erode = Fbm(uv, per * 4u, 3);
        float cover = Fbm(uv, max(per / 2u, 1u), 2);

        uOutput.write(float4(shape, floc, erode, cover), uint2(gid.xy));
    }
}
";

    /// <summary>
    /// D3D12 cs_5_0 (2-5 Step E): aerial-perspective froxel volume. Recomputed every frame.
    ///
    /// Volume encoding (inverse of the sampling-side mapping in the main shader; both places must be changed together):
    /// <code>
    /// xy -> screen uv (top-left origin)
    /// z -> sqrt(distance / maxDistance), so slice k centers at maxDist·((k+0.5)/N)²
    /// rgb -> accumulated **in-scattered radiance** from the camera to that distance (linear HDR; 1-4 contract: no tonemap/clamp)
    /// a -> accumulated **opacity** (1 - mean RGB transmittance)
    /// </code>
    /// The z axis uses quadratic spacing instead of linear spacing: atmospheric density decays exponentially with altitude, so the
    /// first tens of meters vary more violently than the far kilometers; uniform slicing would waste most resolution in the distance.
    ///
    /// Relation to skyView: the integrator (AirDensity + LightInScatter + Hillaire analytic segment integral) is a literal
    /// isomorph because the two passes are just different integration intervals of the same volumetric rendering equation.
    /// This file intentionally keeps helpers inline instead of sharing them: every Source*Hlsl string carries all of its own
    /// dependencies (the four backend sources are self-contained, with no shared headers).
    ///
    /// Refresh policy: no temporal stabilization and no amortization. The full cost is only 1024 threads × 32 slices × 4 substeps,
    /// and there is no randomness to average out; once the camera rotates, the previous frame's screen uv is entirely invalid, so
    /// amortization would only introduce ghosting.
    ///
    /// TAA jitter is intentionally excluded from the camera basis: the offset is sub-pixel, while this volume has only 32 lateral
    /// samples (each spans dozens of screen pixels), so the difference is not perceptible.
    /// </summary>
    const string SourceAerialHlsl = @"
cbuffer ApParams : register(b0)
{
    float4 uApSun;
    float4 uApSunRad;
    float4 uApMoon;
    float4 uApMoonRad;
    float4 uApRayleigh;
    float4 uApPlanet;
    float4 uApRight;
    float4 uApUp;
};

Texture2D<float4> uTransmittance : register(t0);
Texture2D<float4> uMultiScatter : register(t1);
SamplerState uLinearClamp : register(s0);
RWTexture3D<float4> uOutput : register(u0);

// Integration substeps per distance slice. The slices themselves already use quadratic spacing (dense near the camera), so
// 4 substeps are enough to keep the accumulated values continuous across neighboring slices.
// This is a compile-time constant rather than a Params field because changing it is effectively a shader-variant choice,
// and the saved float slot is one reason this block still fits within 128B (see class header).
#define AP_SUBSTEPS 4

float2 AirDensity(float h, float rayleighH, float mieH)
{
    float hc = max(h, 0.0);
    return float2(exp(-hc / rayleighH), exp(-hc / mieH));
}

// Literally identical to the function of the same name in skyView.
// The rationale is documented there: when a light drops below the horizon, the Transmittance LUT already bakes planetary
// self-occlusion as zero, so the sun and moon terms can be summed unconditionally with no day/night branch.
float3 LightInScatter(float3 viewDir, float3 upLocal, float rh01, float4 lightDir, float3 lightRadiance,
                      float3 scatR, float scatM, float g, float msGain)
{
    float c = dot(viewDir, lightDir.xyz);
    float phaseR = 0.05968310366 * (1.0 + c * c);
    float g2 = g * g;
    float hgDen = max(1.0 + g2 - 2.0 * g * c, 1e-4);
    float phaseM = 0.07957747155 * (1.0 - g2) / (hgDen * sqrt(hgDen));

    float2 tuv = float2(dot(upLocal, lightDir.xyz) * 0.5 + 0.5, rh01);
    float3 tLight = uTransmittance.SampleLevel(uLinearClamp, tuv, 0.0).rgb;
    float3 psiMs = uMultiScatter.SampleLevel(uLinearClamp, tuv, 0.0).rgb * msGain;

    return ((scatR * phaseR + scatM * phaseM) * tLight + (scatR + scatM) * psiMs) * lightRadiance;
}

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    uint dw, dh, dd;
    uOutput.GetDimensions(dw, dh, dd);
    if (id.x < dw && id.y < dh)
    {
        float Rg = uApPlanet.x;
        float Rt = uApPlanet.y;

        // Froxel column -> world view direction (half-texel center; y is flipped to match the vertical orientation of screen uv).
        float2 uv = (float2(id.xy) + 0.5) / float2(float(dw), float(dh));
        float2 ndc = float2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
        float3 rightAxis = uApRight.xyz;
        float3 upAxis = uApUp.xyz;
        float3 fwd = cross(normalize(rightAxis), normalize(upAxis));
        float3 dir = normalize(fwd + rightAxis * ndc.x + upAxis * ndc.y);

        // Observation point: same convention as skyView - the planet center is straight below, so local up = world +Y.
        float r0 = Rg + uApPlanet.z;
        float3 origin = float3(0.0, r0, 0.0);

        float g = uApRayleigh.a;
        float msGain = uApPlanet.w;
        float maxDist = max(uApRight.w, 1e-3);
        float3 radiance = float3(0.0, 0.0, 0.0);
        float3 throughput = float3(1.0, 1.0, 1.0);

        float invD = 1.0 / float(max(dd, 1u));
        float subScale = 1.0 / float(AP_SUBSTEPS);
        float tPrev = 0.0;

        for (uint k = 0u; k < dd; k++)
        {
            // Center distance of slice k: the consumer samples with w = sqrt(d / maxDist), so this pass uses the inverse function
            // and makes texel center (k+0.5)/dd map exactly to distance maxDist·((k+0.5)/dd)².
            float f = (float(k) + 0.5) * invD;
            float tSlice = maxDist * f * f;
            float dt = (tSlice - tPrev) * subScale;

            for (int s = 0; s < AP_SUBSTEPS; s++)
            {
                float tMid = tPrev + dt * (float(s) + 0.5);
                float3 pos = origin + dir * tMid;
                float rr = max(length(pos), 1e-6);
                float3 upLocal = pos / rr;
                float rh01 = saturate((rr - Rg) / (Rt - Rg));
                float2 dens = AirDensity(rr - Rg, uApSun.w, uApMoon.w);

                float3 scatR = uApRayleigh.rgb * dens.x;
                float scatM = uApSunRad.a * dens.y;
                float3 extinction = max(uApRayleigh.rgb * dens.x + uApMoonRad.a * dens.y, 1e-7);

                float3 inScatter = LightInScatter(dir, upLocal, rh01, uApSun, uApSunRad.rgb, scatR, scatM, g, msGain)
                                 + LightInScatter(dir, upLocal, rh01, uApMoon, uApMoonRad.rgb, scatR, scatM, g, msGain);

                float3 stepT = exp(-extinction * dt);
                radiance += throughput * (inScatter - inScatter * stepT) / extinction;
                throughput *= stepT;
            }

            tPrev = tSlice;

            // Store **opacity** in a (1 - mean RGB transmittance) instead of transmittance itself.
            // The consumer evaluates color * (1-a) + rgb, so a=0 means nothing is occluded, and the 1x1x1 zero-initialized
            // fallback volume becomes the natural identity element. If transmittance were stored instead, the fallback a=0 would
            // multiply the whole frame to black.
            // Per-channel transmittance would require a second volume and a second texture slot; the color tint is carried mainly
            // by the per-channel inscatter term, and the error from taking the mean is far below the larger uncertainty scale here.
            float avgT = (throughput.r + throughput.g + throughput.b) * 0.33333333;
            uOutput[uint3(id.xy, k)] = float4(radiance, saturate(1.0 - avgT));
        }
    }
}
";

    /// <summary>
    /// Vulkan GLSL 450 (2-5 Step E). Literal isomorph of HLSL: uOutput is an rgba16f storage image3D
    /// (the 3D compute path from the 1-8 contract), and AP_SUBSTEPS is also supplied as a compile-time constant.
    /// </summary>
    const string SourceAerialGlsl = @"#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(push_constant) uniform ApParams
{
    vec4 uApSun;
    vec4 uApSunRad;
    vec4 uApMoon;
    vec4 uApMoonRad;
    vec4 uApRayleigh;
    vec4 uApPlanet;
    vec4 uApRight;
    vec4 uApUp;
};

layout(binding = 1) uniform sampler2D uTransmittance;
layout(binding = 2) uniform sampler2D uMultiScatter;
layout(binding = 3, rgba16f) uniform writeonly image3D uOutput;

// Integration substeps per distance slice. The slices themselves already use quadratic spacing (dense near the camera), so
// 4 substeps are enough to keep the accumulated values continuous across neighboring slices.
// This is a compile-time constant rather than a Params field because changing it is effectively a shader-variant choice,
// and the saved float slot is one reason this block still fits within 128B (see class header).
#define AP_SUBSTEPS 4

vec2 AirDensity(float h, float rayleighH, float mieH)
{
    float hc = max(h, 0.0);
    return vec2(exp(-hc / rayleighH), exp(-hc / mieH));
}

// Literally identical to the function of the same name in skyView.
// The rationale is documented there: when a light drops below the horizon, the Transmittance LUT already bakes planetary
// self-occlusion as zero, so the sun and moon terms can be summed unconditionally with no day/night branch.
vec3 LightInScatter(vec3 viewDir, vec3 upLocal, float rh01, vec4 lightDir, vec3 lightRadiance,
                    vec3 scatR, float scatM, float g, float msGain)
{
    float c = dot(viewDir, lightDir.xyz);
    float phaseR = 0.05968310366 * (1.0 + c * c);
    float g2 = g * g;
    float hgDen = max(1.0 + g2 - 2.0 * g * c, 1e-4);
    float phaseM = 0.07957747155 * (1.0 - g2) / (hgDen * sqrt(hgDen));

    vec2 tuv = vec2(dot(upLocal, lightDir.xyz) * 0.5 + 0.5, rh01);
    vec3 tLight = textureLod(uTransmittance, tuv, 0.0).rgb;
    vec3 psiMs = textureLod(uMultiScatter, tuv, 0.0).rgb * msGain;

    return ((scatR * phaseR + vec3(scatM * phaseM)) * tLight + (scatR + vec3(scatM)) * psiMs) * lightRadiance;
}

void main()
{
    uvec2 id = gl_GlobalInvocationID.xy;
    uvec3 dims = uvec3(imageSize(uOutput));
    uint dw = dims.x;
    uint dh = dims.y;
    uint dd = dims.z;
    if (id.x < dw && id.y < dh)
    {
        float Rg = uApPlanet.x;
        float Rt = uApPlanet.y;

        // Froxel column -> world view direction (half-texel center; y is flipped to match the vertical orientation of screen uv).
        vec2 uv = (vec2(id) + 0.5) / vec2(float(dw), float(dh));
        vec2 ndc = vec2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
        vec3 rightAxis = uApRight.xyz;
        vec3 upAxis = uApUp.xyz;
        vec3 fwd = cross(normalize(rightAxis), normalize(upAxis));
        vec3 dir = normalize(fwd + rightAxis * ndc.x + upAxis * ndc.y);

        // Observation point: same convention as skyView - the planet center is straight below, so local up = world +Y.
        float r0 = Rg + uApPlanet.z;
        vec3 origin = vec3(0.0, r0, 0.0);

        float g = uApRayleigh.a;
        float msGain = uApPlanet.w;
        float maxDist = max(uApRight.w, 1e-3);
        vec3 radiance = vec3(0.0);
        vec3 throughput = vec3(1.0);

        float invD = 1.0 / float(max(dd, 1u));
        float subScale = 1.0 / float(AP_SUBSTEPS);
        float tPrev = 0.0;

        for (uint k = 0u; k < dd; k++)
        {
            // Center distance of slice k: the consumer samples with w = sqrt(d / maxDist), so this pass uses the inverse function
            // and makes texel center (k+0.5)/dd map exactly to distance maxDist·((k+0.5)/dd)².
            float f = (float(k) + 0.5) * invD;
            float tSlice = maxDist * f * f;
            float dt = (tSlice - tPrev) * subScale;

            for (int s = 0; s < AP_SUBSTEPS; s++)
            {
                float tMid = tPrev + dt * (float(s) + 0.5);
                vec3 pos = origin + dir * tMid;
                float rr = max(length(pos), 1e-6);
                vec3 upLocal = pos / rr;
                float rh01 = clamp((rr - Rg) / (Rt - Rg), 0.0, 1.0);
                vec2 dens = AirDensity(rr - Rg, uApSun.w, uApMoon.w);

                vec3 scatR = uApRayleigh.rgb * dens.x;
                float scatM = uApSunRad.a * dens.y;
                vec3 extinction = max(uApRayleigh.rgb * dens.x + vec3(uApMoonRad.a * dens.y), vec3(1e-7));

                vec3 inScatter = LightInScatter(dir, upLocal, rh01, uApSun, uApSunRad.rgb, scatR, scatM, g, msGain)
                               + LightInScatter(dir, upLocal, rh01, uApMoon, uApMoonRad.rgb, scatR, scatM, g, msGain);

                vec3 stepT = exp(-extinction * dt);
                radiance += throughput * (inScatter - inScatter * stepT) / extinction;
                throughput *= stepT;
            }

            tPrev = tSlice;

            // Store **opacity** in a (1 - mean RGB transmittance) instead of transmittance itself.
            // The consumer evaluates color * (1-a) + rgb, so a=0 means nothing is occluded, and the 1x1x1 zero-initialized
            // fallback volume becomes the natural identity element. If transmittance were stored instead, the fallback a=0 would
            // multiply the whole frame to black.
            // Per-channel transmittance would require a second volume and a second texture slot; the color tint is carried mainly
            // by the per-channel inscatter term, and the error from taking the mean is far below the larger uncertainty scale here.
            float avgT = (throughput.r + throughput.g + throughput.b) * 0.33333333;
            imageStore(uOutput, ivec3(id.xy, k), vec4(radiance, clamp(1.0 - avgT, 0.0, 1.0)));
        }
    }
}
";

    /// <summary>
    /// WebGPU WGSL (2-5 Step E). Literal isomorph of HLSL: uOutput is an rgba16f write-only storage 3D texture
    /// (layoutEntries type 7 on the JS side, viewDimension '3d'), and AP_SUBSTEPS is likewise supplied as a compile-time const.
    /// </summary>
    const string SourceAerialWgsl = @"
struct ApParams
{
    uApSun : vec4f,
    uApSunRad : vec4f,
    uApMoon : vec4f,
    uApMoonRad : vec4f,
    uApRayleigh : vec4f,
    uApPlanet : vec4f,
    uApRight : vec4f,
    uApUp : vec4f,
};

@group(0) @binding(0) var<uniform> params : ApParams;
@group(0) @binding(1) var uTransmittance : texture_2d<f32>;
@group(0) @binding(2) var uMultiScatter : texture_2d<f32>;
@group(0) @binding(3) var uOutput : texture_storage_3d<rgba16float, write>;
@group(0) @binding(15) var uSampler : sampler;

// Integration substeps per distance slice. The slices themselves already use quadratic spacing (dense near the camera), so
// 4 substeps are enough to keep the accumulated values continuous across neighboring slices.
// This is a compile-time constant rather than a Params field because changing it is effectively a shader-variant choice,
// and the saved float slot is one reason this block still fits within 128B (see class header).
const AP_SUBSTEPS : i32 = 4;

fn AirDensity(h : f32, rayleighH : f32, mieH : f32) -> vec2f
{
    let hc = max(h, 0.0);
    return vec2f(exp(-hc / rayleighH), exp(-hc / mieH));
}

// Literally identical to the function of the same name in skyView.
// The rationale is documented there: when a light drops below the horizon, the Transmittance LUT already bakes planetary
// self-occlusion as zero, so the sun and moon terms can be summed unconditionally with no day/night branch.
fn LightInScatter(viewDir : vec3f, upLocal : vec3f, rh01 : f32, lightDir : vec4f, lightRadiance : vec3f,
                  scatR : vec3f, scatM : f32, g : f32, msGain : f32) -> vec3f
{
    let c = dot(viewDir, lightDir.xyz);
    let phaseR = 0.05968310366 * (1.0 + c * c);
    let g2 = g * g;
    let hgDen = max(1.0 + g2 - 2.0 * g * c, 1e-4);
    let phaseM = 0.07957747155 * (1.0 - g2) / (hgDen * sqrt(hgDen));

    let tuv = vec2f(dot(upLocal, lightDir.xyz) * 0.5 + 0.5, rh01);
    let tLight = textureSampleLevel(uTransmittance, uSampler, tuv, 0.0).rgb;
    let psiMs = textureSampleLevel(uMultiScatter, uSampler, tuv, 0.0).rgb * msGain;

    return ((scatR * phaseR + vec3f(scatM * phaseM)) * tLight + (scatR + vec3f(scatM)) * psiMs) * lightRadiance;
}

@compute @workgroup_size(8, 8, 1)
fn CSMain(@builtin(global_invocation_id) id : vec3<u32>)
{
    let dims = textureDimensions(uOutput);
    let dw = dims.x;
    let dh = dims.y;
    let dd = dims.z;
    if (id.x < dw && id.y < dh)
    {
        let Rg = params.uApPlanet.x;
        let Rt = params.uApPlanet.y;

        // Froxel column -> world view direction (half-texel center; y is flipped to match the vertical orientation of screen uv).
        let uv = (vec2f(id.xy) + vec2f(0.5)) / vec2f(f32(dw), f32(dh));
        let ndc = vec2f(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
        let rightAxis = params.uApRight.xyz;
        let upAxis = params.uApUp.xyz;
        let fwd = cross(normalize(rightAxis), normalize(upAxis));
        let dir = normalize(fwd + rightAxis * ndc.x + upAxis * ndc.y);

        // Observation point: same convention as skyView - the planet center is straight below, so local up = world +Y.
        let r0 = Rg + params.uApPlanet.z;
        let origin = vec3f(0.0, r0, 0.0);

        let g = params.uApRayleigh.a;
        let msGain = params.uApPlanet.w;
        let maxDist = max(params.uApRight.w, 1e-3);
        var radiance = vec3f(0.0);
        var throughput = vec3f(1.0);

        let invD = 1.0 / f32(max(dd, 1u));
        let subScale = 1.0 / f32(AP_SUBSTEPS);
        var tPrev = 0.0;

        for (var k : u32 = 0u; k < dd; k = k + 1u)
        {
            // Center distance of slice k: the consumer samples with w = sqrt(d / maxDist), so this pass uses the inverse function
            // and makes texel center (k+0.5)/dd map exactly to distance maxDist·((k+0.5)/dd)².
            let f = (f32(k) + 0.5) * invD;
            let tSlice = maxDist * f * f;
            let dt = (tSlice - tPrev) * subScale;

            for (var s : i32 = 0; s < AP_SUBSTEPS; s = s + 1)
            {
                let tMid = tPrev + dt * (f32(s) + 0.5);
                let pos = origin + dir * tMid;
                let rr = max(length(pos), 1e-6);
                let upLocal = pos / rr;
                let rh01 = clamp((rr - Rg) / (Rt - Rg), 0.0, 1.0);
                let dens = AirDensity(rr - Rg, params.uApSun.w, params.uApMoon.w);

                let scatR = params.uApRayleigh.rgb * dens.x;
                let scatM = params.uApSunRad.a * dens.y;
                let extinction = max(params.uApRayleigh.rgb * dens.x + vec3f(params.uApMoonRad.a * dens.y), vec3f(1e-7));

                let inScatter = LightInScatter(dir, upLocal, rh01, params.uApSun, params.uApSunRad.rgb, scatR, scatM, g, msGain)
                              + LightInScatter(dir, upLocal, rh01, params.uApMoon, params.uApMoonRad.rgb, scatR, scatM, g, msGain);

                let stepT = exp(-extinction * dt);
                radiance += throughput * (inScatter - inScatter * stepT) / extinction;
                throughput *= stepT;
            }

            tPrev = tSlice;

            // Store **opacity** in a (1 - mean RGB transmittance) instead of transmittance itself.
            // The consumer evaluates color * (1-a) + rgb, so a=0 means nothing is occluded, and the 1x1x1 zero-initialized
            // fallback volume becomes the natural identity element. If transmittance were stored instead, the fallback a=0 would
            // multiply the whole frame to black.
            let avgT = (throughput.r + throughput.g + throughput.b) * 0.33333333;
            // The two-argument vec3i constructor requires both arguments to share the same type (id.xy is vec2<u32>),
            // so value-convert first and then combine.
            textureStore(uOutput, vec3i(vec2i(id.xy), i32(k)), vec4f(radiance, clamp(1.0 - avgT, 0.0, 1.0)));
        }
    }
}
";

    /// <summary>
    /// Apple Metal MSL (2-5 Step E). Literal isomorph of GLSL: uOutput is an rgba16f write-only 3D texture
    /// [[texture(2)]] (the 3D compute path from the 1-8 contract; dimensions are queried through get_width/get_height/get_depth
    /// instead of GLSL's imageSize). AP_SUBSTEPS is likewise a compile-time constant, and textures/samplers are passed as
    /// parameters because MSL has no global texture variables (see the note on SourceSkyViewMsl).
    /// </summary>
    const string SourceAerialMsl = @"
#include <metal_stdlib>
#include <simd/simd.h>
using namespace metal;

struct ApParams
{
    float4 uApSun;
    float4 uApSunRad;
    float4 uApMoon;
    float4 uApMoonRad;
    float4 uApRayleigh;
    float4 uApPlanet;
    float4 uApRight;
    float4 uApUp;
};

// Integration substeps per distance slice. The slices themselves already use quadratic spacing (dense near the camera), so
// 4 substeps are enough to keep the accumulated values continuous across neighboring slices.
// This is a compile-time constant rather than a Params field because changing it is effectively a shader-variant choice,
// and the saved float slot is one reason this block still fits within 128B (see class header).
constant int AP_SUBSTEPS = 4;

float2 AirDensity(float h, float rayleighH, float mieH)
{
    float hc = max(h, 0.0);
    return float2(exp(-hc / rayleighH), exp(-hc / mieH));
}

// Literally identical to the function of the same name in skyView.
// The rationale is documented there: when a light drops below the horizon, the Transmittance LUT already bakes planetary
// self-occlusion as zero, so the sun and moon terms can be summed unconditionally with no day/night branch.
float3 LightInScatter(float3 viewDir, float3 upLocal, float rh01, float4 lightDir, float3 lightRadiance,
                      float3 scatR, float scatM, float g, float msGain,
                      texture2d<float> uTransmittance, texture2d<float> uMultiScatter, sampler s)
{
    float c = dot(viewDir, lightDir.xyz);
    float phaseR = 0.05968310366 * (1.0 + c * c);
    float g2 = g * g;
    float hgDen = max(1.0 + g2 - 2.0 * g * c, 1e-4);
    float phaseM = 0.07957747155 * (1.0 - g2) / (hgDen * sqrt(hgDen));

    float2 tuv = float2(dot(upLocal, lightDir.xyz) * 0.5 + 0.5, rh01);
    float3 tLight = uTransmittance.sample(s, tuv, level(0.0)).rgb;
    float3 psiMs = uMultiScatter.sample(s, tuv, level(0.0)).rgb * msGain;

    return ((scatR * phaseR + float3(scatM * phaseM)) * tLight + (scatR + float3(scatM)) * psiMs) * lightRadiance;
}

kernel void CSMain(uint3 gid [[thread_position_in_grid]],
                   constant ApParams& p [[buffer(0)]],
                   texture2d<float> uTransmittance [[texture(0)]],
                   texture2d<float> uMultiScatter [[texture(1)]],
                   texture3d<float, access::write> uOutput [[texture(2)]],
                   sampler s [[sampler(0)]])
{
    uint dw = uOutput.get_width();
    uint dh = uOutput.get_height();
    uint dd = uOutput.get_depth();
    if (gid.x < dw && gid.y < dh)
    {
        float Rg = p.uApPlanet.x;
        float Rt = p.uApPlanet.y;

        // Froxel column -> world view direction (half-texel center; y is flipped to match the vertical orientation of screen uv).
        float2 uv = (float2(gid.xy) + 0.5) / float2(float(dw), float(dh));
        float2 ndc = float2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
        float3 rightAxis = p.uApRight.xyz;
        float3 upAxis = p.uApUp.xyz;
        float3 fwd = cross(normalize(rightAxis), normalize(upAxis));
        float3 dir = normalize(fwd + rightAxis * ndc.x + upAxis * ndc.y);

        // Observation point: same convention as skyView - the planet center is straight below, so local up = world +Y.
        float r0 = Rg + p.uApPlanet.z;
        float3 origin = float3(0.0, r0, 0.0);

        float g = p.uApRayleigh.a;
        float msGain = p.uApPlanet.w;
        float maxDist = max(p.uApRight.w, 1e-3);
        float3 radiance = float3(0.0);
        float3 throughput = float3(1.0);

        float invD = 1.0 / float(max(dd, 1u));
        float subScale = 1.0 / float(AP_SUBSTEPS);
        float tPrev = 0.0;

        for (uint k = 0u; k < dd; k++)
        {
            // Center distance of slice k: the consumer samples with w = sqrt(d / maxDist), so this pass uses the inverse function
            // and makes texel center (k+0.5)/dd map exactly to distance maxDist·((k+0.5)/dd)².
            float f = (float(k) + 0.5) * invD;
            float tSlice = maxDist * f * f;
            float dt = (tSlice - tPrev) * subScale;

            for (int s2 = 0; s2 < AP_SUBSTEPS; s2++)
            {
                float tMid = tPrev + dt * (float(s2) + 0.5);
                float3 pos = origin + dir * tMid;
                float rr = max(length(pos), 1e-6);
                float3 upLocal = pos / rr;
                float rh01 = clamp((rr - Rg) / (Rt - Rg), 0.0, 1.0);
                float2 dens = AirDensity(rr - Rg, p.uApSun.w, p.uApMoon.w);

                float3 scatR = p.uApRayleigh.rgb * dens.x;
                float scatM = p.uApSunRad.a * dens.y;
                float3 extinction = max(p.uApRayleigh.rgb * dens.x + float3(p.uApMoonRad.a * dens.y), float3(1e-7));

                float3 inScatter = LightInScatter(dir, upLocal, rh01, p.uApSun, p.uApSunRad.rgb, scatR, scatM, g, msGain, uTransmittance, uMultiScatter, s)
                                 + LightInScatter(dir, upLocal, rh01, p.uApMoon, p.uApMoonRad.rgb, scatR, scatM, g, msGain, uTransmittance, uMultiScatter, s);

                float3 stepT = exp(-extinction * dt);
                radiance += throughput * (inScatter - inScatter * stepT) / extinction;
                throughput *= stepT;
            }

            tPrev = tSlice;

            // Store **opacity** in a (1 - mean RGB transmittance) instead of transmittance itself.
            // The consumer evaluates color * (1-a) + rgb, so a=0 means nothing is occluded, and the 1x1x1 zero-initialized
            // fallback volume becomes the natural identity element. If transmittance were stored instead, the fallback a=0 would
            // multiply the whole frame to black.
            // Per-channel transmittance would require a second volume and a second texture slot; the color tint is carried mainly
            // by the per-channel inscatter term, and the error from taking the mean is far below the larger uncertainty scale here.
            float avgT = (throughput.r + throughput.g + throughput.b) * 0.33333333;
            uOutput.write(float4(radiance, saturate(1.0 - avgT)), uint3(gid.xy, k));
        }
    }
}
";
}
