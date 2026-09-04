// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

/// <summary>
/// Built-in compute effect implementing section 2-4: DDGI backed by an SDF volume.
///
/// Pipeline overview:
/// 1. `sdfGather` writes a single-resolution R16Float SDF volume from the proxy list.
/// 2. `probeUpdate` traces probe rays through the SDF, updates the octahedral irradiance atlas,
///    and also writes a depth-moment atlas for Chebyshev-based leak suppression.
/// 3. `sdfSlice` outputs the debug view used to inspect the volume and probe column state.
///
/// Key implementation rules locked by the current contract:
/// - The probe grid stays aligned to the snapped camera-centered volume.
/// - Irradiance uses 8x8 octahedral tiles; depth moments use independent 16x16 tiles.
/// - The effect amortizes probe tracing over frames and keeps the write surface complete by
///   copying previous values for probes not updated in the current phase.
/// - Hit shading uses `emi + alb * (E_direct / PI + L_bounce)`, where direct lighting matches the
///   main pass formulas and bounce lighting samples the previous irradiance atlas.
/// - Probe validity is classified continuously and written into irradiance alpha to avoid
///   threshold-driven flicker.
///
/// Runtime notes:
/// - The effect logs startup state, runtime knob changes, and optional periodic heartbeat messages.
/// - The volume center is snapped to voxel increments so sub-voxel camera motion does not cause
///   the entire field to shimmer.
/// - Initialization degrades gracefully: when DDGI is disabled or any kernel fails to compile,
///   Initialize returns false and leaves no residue.
///
/// Binding layout (declaration order defines the cross-backend slot contract; see the
/// ComputeBindingType summary):
/// sdfGather   [0] Params(volumeMin.xyz, voxelSize | resolution, proxyCount, far, _) 32B
///             [1] StorageBufferRead(proxies 4KB) [2] StorageTexture3DWrite(R16Float)
/// probeUpdate [0] Params(probeGridMin/spacing, volumeMin/voxel, res/count/rays/steps,
///                 hyst/normalBias/skyInt/frame, gridXYZ/divisor, extent/extent/backfaceHyst/validityOn,
///                 shadowSteps/bounceGain/punctualShadow/backfaceThr) 112B
///             [1] StorageBufferRead(proxies) [2] SampledTexture3D(sdf)
///             [3] StorageBufferRead(sh9 radiance 144B) [4] SampledTexture(previous irradiance atlas, texelLoad)
///             [5] StorageTextureWrite(Rgba16Float, current-frame irradiance atlas)
///             [6] SampledTexture(previous depth atlas 16x16 tile, texelLoad) [7] StorageTextureWrite(Rg16Float, current-frame depth atlas)
///             [8] StorageBufferRead(all light types: first vec4.x = count, followed by 8x64B GpuLight records)
/// sdfSlice    [0] Params(size, mainRows, halfExtent, gridX, gridY, gridZ, atlasW, atlasH) 32B
///             [1] SampledTexture3D(sdf) [2] SampledTexture(current-frame irradiance atlas)
///             [3] StorageTextureWrite(Rgba8Unorm)
///
/// The effect runs in AfterScene, so the generated atlases are consumed by the next frame's main pass.
/// Proxy-list uploads happen at the start of Record, outside any active render pass, satisfying the
/// UpdateStorageBuffer contract.
/// </summary>
public sealed class DdgiEffect : ComputeEffect
{
    /// <summary>Registered name of the proxy SDF volume texture in the dedicated 3D dictionary.</summary>
    public const string VolumeName = "compute3d://ddgi/sdf";

    /// <summary>Registered name of the debug slice output texture in the 2D dictionary.</summary>
    public const string DebugTextureName = "compute://ddgi/sdfslice";

    /// <summary>Debug slice side length.</summary>
    public const uint DebugSize = 256;

    /// <summary>Number of rows used by the main debug area; the remaining rows form the probe strip.</summary>
    public const uint DebugMainRows = 240;

    /// <summary>Workgroup edge length for the gather kernel (4^3 = 64 threads; the volume resolution must be a multiple of it).</summary>
    const uint GatherGroup = 4;

    /// <summary>Per-probe irradiance tile size in the atlas (6x6 core plus a 1px octahedral seam border on each side).</summary>
    const uint IrrTile = 8;

    /// <summary>Per-probe depth-moment tile size in the atlas (14x14 core plus a 1px seam border on each side).
    /// It is independent from <see cref="IrrTile"/> because Chebyshev visibility requires finer angular
    /// resolution than irradiance sampling.</summary>
    const uint DepTile = 16;

    /// <summary>Thread count for probeUpdate workgroups. This is fixed at 128, matching the maximum
    /// supported GiRaysPerProbe and avoiding dynamic workgroup sizes in shader source.</summary>
    const uint UpdateThreads = 128;

    /// <summary>Irradiance atlas ping-pong surface 0 (rgba16float).</summary>
    public const string IrrAtlasName0 = "compute://ddgi/irr0";
    /// <summary>Irradiance atlas ping-pong surface 1 (rgba16float).</summary>
    public const string IrrAtlasName1 = "compute://ddgi/irr1";

    /// <summary>Depth-moment atlas ping-pong surface 0 (rg16float, .x = mean, .y = mean^2).</summary>
    public const string DepAtlasName0 = "compute://ddgi/dep0";
    /// <summary>Depth-moment atlas ping-pong surface 1 (rg16float).</summary>
    public const string DepAtlasName1 = "compute://ddgi/dep1";

    /// <summary>Manual debug-dump switch. When set to true, the next <see cref="Record"/> logs the
    /// current DDGI state and triggers <see cref="GiProxies.DumpOnce"/>, then resets automatically.</summary>
    public static bool DumpOnce;

    /// <summary>
    /// Step 6 runtime GI settings entry point: all Gi* values are resolved from
    /// Settings.RenderQuality when available, and fall back to static Default* values otherwise.
    /// </summary>
    readonly struct GiSettings
    {
        readonly RenderQuality? _q;

        GiSettings(RenderQuality? q) => _q = q;

        /// <summary>Creates the resolver from the current app's Settings.RenderQuality.</summary>
        public static GiSettings Resolve() => new(DeviceServices.BaseApp?.Settings?.RenderQuality);

        public GiMode GlobalIllumination => _q?.GlobalIllumination ?? RenderQuality.DefaultGlobalIllumination;
        public int GiSdfResolution => _q?.GiSdfResolution ?? RenderQuality.DefaultGiSdfResolution;
        public float GiVolumeSize => _q?.GiVolumeSize ?? RenderQuality.DefaultGiVolumeSize;
        public int GiProbeGridX => _q?.GiProbeGridX ?? RenderQuality.DefaultGiProbeGridX;
        public int GiProbeGridY => _q?.GiProbeGridY ?? RenderQuality.DefaultGiProbeGridY;
        public int GiProbeGridZ => _q?.GiProbeGridZ ?? RenderQuality.DefaultGiProbeGridZ;
        public int GiRaysPerProbe => _q?.GiRaysPerProbe ?? RenderQuality.DefaultGiRaysPerProbe;
        public int GiProbeUpdateDivisor => _q?.GiProbeUpdateDivisor ?? RenderQuality.DefaultGiProbeUpdateDivisor;
        public int GiTraceMaxSteps => _q?.GiTraceMaxSteps ?? RenderQuality.DefaultGiTraceMaxSteps;
        public bool GiChebyshevOcclusion => _q?.GiChebyshevOcclusion ?? RenderQuality.DefaultGiChebyshevOcclusion;
        public float GiIntensity => _q?.GiIntensity ?? RenderQuality.DefaultGiIntensity;
        public float GiHysteresis => _q?.GiHysteresis ?? RenderQuality.DefaultGiHysteresis;
        public float GiBackfaceHysteresis => _q?.GiBackfaceHysteresis ?? RenderQuality.DefaultGiBackfaceHysteresis;
        public bool GiProbeValidity => _q?.GiProbeValidity ?? RenderQuality.DefaultGiProbeValidity;
        public float GiNormalBias => _q?.GiNormalBias ?? RenderQuality.DefaultGiNormalBias;
        public int GiShadowSteps => _q?.GiShadowSteps ?? RenderQuality.DefaultGiShadowSteps;
        public float GiBounceGain => _q?.GiBounceGain ?? RenderQuality.DefaultGiBounceGain;
        public bool GiPunctualShadow => _q?.GiPunctualShadow ?? RenderQuality.DefaultGiPunctualShadow;
        public float GiBackfaceThreshold => _q?.GiBackfaceThreshold ?? RenderQuality.DefaultGiBackfaceThreshold;
        public int GiLogIntervalFrames => _q?.GiLogIntervalFrames ?? RenderQuality.DefaultGiLogIntervalFrames;
    }

    /// <summary>Snapshot of runtime DDGI knobs used for change detection and logging.</summary>
    readonly struct GiKnobSnapshot
    {
        public readonly float Hyst, BackHyst, NormalBias, Intensity, Bounce, Threshold, Sky;
        public readonly int Rays, MaxSteps, ShadowSteps, Divisor;
        public readonly bool Cheb, Punctual, Validity;

        public GiKnobSnapshot(float hyst, float backHyst, float normalBias, float intensity,
            float bounce, float threshold, float sky, int rays, int maxSteps, int shadowSteps,
            int divisor, bool cheb, bool punctual, bool validity)
        {
            Hyst = hyst; BackHyst = backHyst; NormalBias = normalBias; Intensity = intensity;
            Bounce = bounce; Threshold = threshold; Sky = sky;
            Rays = rays; MaxSteps = maxSteps; ShadowSteps = shadowSteps; Divisor = divisor;
            Cheb = cheb; Punctual = punctual; Validity = validity;
        }

        public bool Equals(in GiKnobSnapshot o) =>
            Hyst == o.Hyst && BackHyst == o.BackHyst && NormalBias == o.NormalBias &&
            Intensity == o.Intensity && Bounce == o.Bounce && Threshold == o.Threshold &&
            Sky == o.Sky && Rays == o.Rays && MaxSteps == o.MaxSteps && ShadowSteps == o.ShadowSteps &&
            Divisor == o.Divisor && Cheb == o.Cheb && Punctual == o.Punctual && Validity == o.Validity;

        /// <summary>Captures a snapshot from GiSettings once per Record call without allocating.</summary>
        public static GiKnobSnapshot Capture(in GiSettings q) => new(
            q.GiHysteresis,
            q.GiBackfaceHysteresis,
            q.GiNormalBias,
            q.GiIntensity,
            q.GiBounceGain,
            q.GiBackfaceThreshold,
            DeviceServices.BaseApp?.SceneEnvironment?.SkyIntensity ?? 1f,
            Math.Max(1, q.GiRaysPerProbe),
            q.GiTraceMaxSteps,
            Math.Max(1, q.GiShadowSteps),
            Math.Max(1, q.GiProbeUpdateDivisor),
            q.GiChebyshevOcclusion,
            q.GiPunctualShadow,
            q.GiProbeValidity);
    }


    ComputeKernel? _gather;
    ComputeKernel? _slice;
    ComputeKernel? _update;

    StorageBuffer? _proxies;

    /// <summary>SH9 sky radiance buffer (9 x float4 = 144B), used for ray misses and updated every frame from SceneEnvironment.</summary>
    StorageBuffer? _giSh;

    /// <summary>Punctual-light upload buffer (544B): vec4.x stores the count, followed by up to
    /// 8 x 64B GpuLight records packed from EffectiveSceneLights every frame.</summary>
    StorageBuffer? _lights;

    // These arrays contain string references, so they cannot use stackalloc.
    // Cache them once and reuse them every frame without allocations.
    ComputeResourceRef[]? _gatherRes;
    // Slice samples the surface written this frame: two ping-pong variants (write side = irr0 / irr1).
    ComputeResourceRef[]? _sliceResP0;
    ComputeResourceRef[]? _sliceResP1;
    // Update reads the previous frame and writes the current frame: two ping-pong variants
    // ([proxies, sdf, sh, prevIrr(read), writeIrr, prevDep(read), writeDep]).
    ComputeResourceRef[]? _updateResP0;   // _pingWrite=false：write=irr0/dep0 read=irr1/dep1
    ComputeResourceRef[]? _updateResP1;   // _pingWrite=true ：write=irr1/dep1 read=irr0/dep0

    /// <summary>Resolved volume resolution for this run, rounded to a multiple of <see cref="GatherGroup"/>.</summary>
    uint _resolution;

    /// <summary>Probe grid dimensions, fixed during initialization.</summary>
    uint _gridX, _gridY, _gridZ;

    /// <summary>Total probe count and irradiance atlas size in pixels.</summary>
    uint _probeCount, _atlasW, _atlasH;

    /// <summary>Depth-moment atlas size in pixels, independent from irradiance; see <see cref="DepTile"/>.</summary>
    uint _depAtlasW, _depAtlasH;

    /// <summary>Frame counter used by amortization and hysteresis; incremented at the end of each Record.</summary>
    uint _frame;

    /// <summary>Tracks whether the startup parameter log has already been emitted.</summary>
    bool _startupLogged;

    /// <summary>Runtime knob snapshot and initialization flag for change detection.</summary>
    GiKnobSnapshot _knobSnapshot;
    bool _knobInit;


    /// <summary>Ping-pong write selector, flipped at the end of each Record. false means write irr0 and read irr1.</summary>
    bool _pingWrite;

    // ── Step 2b published state for the consumer side (name-as-handle, mirroring envCube's Active pattern).
    // This is a singleton effect, so static fields expose the active atlas names to all backends.
    /// <summary>Becomes true after the first Record completes; before that, consumers fall back entirely.</summary>
    static volatile bool s_ready;
    static Vector3 s_gridMin;
    static float s_spacing;
    static uint s_gx, s_gy, s_gz;
    /// <summary>Name of the irradiance atlas surface consumed this frame.</summary>
    static string s_readName = IrrAtlasName0;
    /// <summary>Name of the depth-moment atlas surface consumed this frame.</summary>
    static string s_depName = DepAtlasName0;

    /// <summary>Registered name of the irradiance atlas sampled by the main shader.</summary>
    public static string ActiveIrradianceName => s_readName;

    /// <summary>Registered name of the depth-moment atlas sampled by the main shader for Chebyshev visibility.</summary>
    public static string ActiveDepthName => s_depName;

    /// <summary>True once DDGI has initialized and produced the first probe atlas.</summary>
    public static bool Ready => s_ready;

    /// <summary>Applies GiParams0/1/2 to SceneLightParams once the atlases are ready.</summary>
    public static void Apply(ref SceneLightParams lp)
    {
        if (!s_ready)
            return;
        GiSettings q = GiSettings.Resolve();
        lp.GiParams0 = new Vector4(s_gridMin.X, s_gridMin.Y, s_gridMin.Z, s_spacing);
        lp.GiParams1 = new Vector4(s_gx, s_gy, s_gz, q.GiIntensity);
        lp.GiParams2 = new Vector4(
            q.GiNormalBias,
            q.GiChebyshevOcclusion ? 1f : 0f,
            1f,   // atlasReady: s_ready guarantees a real atlas is bound this frame
            0f);
    }

    public override string Name => "ddgi";

    public override ComputePhase Phase => ComputePhase.AfterScene;

    /// <summary>Resolves the SDF volume resolution by snapping the tier value to a multiple of the
    /// workgroup size and clamping it to [16, 128].</summary>
    static uint ResolveResolution(in GiSettings q)
    {
        int r = q.GiSdfResolution;
        r = r / (int)GatherGroup * (int)GatherGroup;
        return (uint)Math.Clamp(r, 16, 128);
    }

    public override bool Initialize(IGraphics g)
    {
        // Step 6: resolve all Gi* values from Settings.RenderQuality and fall back to static defaults when needed.
        GiSettings q = GiSettings.Resolve();
        // Tier gate (clauses 1/12): keep the whole effect inactive in Off mode, with no resources and no dispatches.
        if (q.GlobalIllumination != GiMode.Ddgi)
            return false;

        _resolution = ResolveResolution(in q);

        _gather = g.CreateComputeKernel(new ComputeKernelDesc
        {
            Name = "ddgiSdfGather",
            WorkgroupX = GatherGroup,
            WorkgroupY = GatherGroup,
            WorkgroupZ = GatherGroup,
            Source = new ShaderSourceSet
            {
                Hlsl = SourceGatherHlsl,
                Glsl = SourceGatherGlsl,
                Msl = SourceGatherMsl,
                Wgsl = SourceGatherWgsl,
                EntryPoint = "CSMain",
            },
            Bindings = new[]
            {
                new ComputeBindingDesc { Type = ComputeBindingType.Params, SizeInBytes = 32 },
                new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferRead },
                new ComputeBindingDesc { Type = ComputeBindingType.StorageTexture3DWrite, StorageFormat = ComputeStorageFormat.R16Float },
            },
        });
        _slice = g.CreateComputeKernel(new ComputeKernelDesc
        {
            Name = "ddgiSdfSlice",
            Source = new ShaderSourceSet
            {
                Hlsl = SourceSliceHlsl,
                Glsl = SourceSliceGlsl,
                Msl = SourceSliceMsl,
                Wgsl = SourceSliceWgsl,
                EntryPoint = "CSMain",
            },
            Bindings = new[]
            {
                new ComputeBindingDesc { Type = ComputeBindingType.Params, SizeInBytes = 32 },
                new ComputeBindingDesc { Type = ComputeBindingType.SampledTexture3D },
                new ComputeBindingDesc { Type = ComputeBindingType.SampledTexture },
                new ComputeBindingDesc { Type = ComputeBindingType.StorageTextureWrite, StorageFormat = ComputeStorageFormat.Rgba8Unorm },
            },
        });

        // Finalize probe-grid dimensions for this run, then derive the probe count and atlas sizes.
        _gridX = (uint)Math.Max(1, q.GiProbeGridX);
        _gridY = (uint)Math.Max(1, q.GiProbeGridY);
        _gridZ = (uint)Math.Max(1, q.GiProbeGridZ);
        _probeCount = _gridX * _gridY * _gridZ;
        _atlasW = _gridX * _gridZ * IrrTile;
        _atlasH = _gridY * IrrTile;
        _depAtlasW = _gridX * _gridZ * DepTile;
        _depAtlasH = _gridY * DepTile;

        _update = g.CreateComputeKernel(new ComputeKernelDesc
        {
            Name = "ddgiProbeUpdate",
            WorkgroupX = UpdateThreads,
            WorkgroupY = 1,
            WorkgroupZ = 1,
            Source = new ShaderSourceSet
            {
                Hlsl = SourceUpdateHlsl,
                Glsl = SourceUpdateGlsl,
                Msl = SourceUpdateMsl,
                Wgsl = SourceUpdateWgsl,
                EntryPoint = "CSMain",
            },
            Bindings = new[]
            {
                new ComputeBindingDesc { Type = ComputeBindingType.Params, SizeInBytes = 112 },
                new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferRead },
                new ComputeBindingDesc { Type = ComputeBindingType.SampledTexture3D },
                new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferRead },
                new ComputeBindingDesc { Type = ComputeBindingType.SampledTexture },
                new ComputeBindingDesc { Type = ComputeBindingType.StorageTextureWrite, StorageFormat = ComputeStorageFormat.Rgba16Float },
                new ComputeBindingDesc { Type = ComputeBindingType.SampledTexture },
                new ComputeBindingDesc { Type = ComputeBindingType.StorageTextureWrite, StorageFormat = ComputeStorageFormat.Rg16Float },
                new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferRead },
            },
        });
        if (_gather == null || _slice == null || _update == null)
        {
            Dispose();
            return false;
        }

        g.CreateComputeTexture3D(VolumeName, _resolution, _resolution, _resolution, ComputeStorageFormat.R16Float);
        g.CreateComputeTexture(DebugTextureName, DebugSize, DebugSize, ComputeStorageFormat.Rgba8Unorm);
        g.CreateComputeTexture(IrrAtlasName0, _atlasW, _atlasH, ComputeStorageFormat.Rgba16Float);
        g.CreateComputeTexture(IrrAtlasName1, _atlasW, _atlasH, ComputeStorageFormat.Rgba16Float);
        // Depth-moment atlas: independent 16x16 tiles (14x14 core, see DepTile), same ping-pong scheme,
        // rg16float format (.x = mean, .y = mean^2).
        g.CreateComputeTexture(DepAtlasName0, _depAtlasW, _depAtlasH, ComputeStorageFormat.Rg16Float);
        g.CreateComputeTexture(DepAtlasName1, _depAtlasW, _depAtlasH, ComputeStorageFormat.Rg16Float);

        _proxies = g.CreateStorageBuffer(GiProxies.BufferBytes);
        _giSh = g.CreateStorageBuffer(9 * 16);
        _lights = g.CreateStorageBuffer(16 + 8 * 64);

        _gatherRes = new ComputeResourceRef[] { _proxies, VolumeName };
        // Update resources: [proxies, sdf, sh, prevIrr(read), writeIrr, prevDep(read), writeDep, lights],
        // with two ping-pong variants.
        _updateResP0 = new ComputeResourceRef[] { _proxies, VolumeName, _giSh, IrrAtlasName1, IrrAtlasName0, DepAtlasName1, DepAtlasName0, _lights };
        _updateResP1 = new ComputeResourceRef[] { _proxies, VolumeName, _giSh, IrrAtlasName0, IrrAtlasName1, DepAtlasName0, DepAtlasName1, _lights };
        // Slice samples the surface written this frame (the one just produced by update): [sdf, atlas, output].
        _sliceResP0 = new ComputeResourceRef[] { VolumeName, IrrAtlasName0, DebugTextureName };
        _sliceResP1 = new ComputeResourceRef[] { VolumeName, IrrAtlasName1, DebugTextureName };
        return true;
    }

    public override void Record(IGraphics g)
    {
        // Step 6: resolve Settings.RenderQuality once per frame so runtime knob changes apply immediately.
        GiSettings q = GiSettings.Resolve();
        float extent = q.GiVolumeSize;
        float voxel = extent / _resolution;

        // Snap the volume center to the voxel grid; otherwise sub-voxel camera motion makes the whole field shimmer.
        var camera = DeviceServices.BaseApp?.CameraPos ?? Vector3.Zero;
        var snapped = new Vector3(
            MathF.Round(camera.X / voxel) * voxel,
            MathF.Round(camera.Y / voxel) * voxel,
            MathF.Round(camera.Z / voxel) * voxel);
        var half = new Vector3(extent * 0.5f);
        var min = snapped - half;

        // Runtime observability: log the full startup state once, log any runtime knob change,
        // and optionally emit compact heartbeat messages so periodic visual oscillation can be
        // correlated with frame index and amortization phase.
        var env = DeviceServices.BaseApp?.SceneEnvironment;
        GiKnobSnapshot knobs = GiKnobSnapshot.Capture(in q);
        bool logStartup = !_startupLogged;
        bool logKnobs = _knobInit && !_knobSnapshot.Equals(knobs);
        if (logStartup || logKnobs)
        {
            _knobSnapshot = knobs;
            _knobInit = true;
            _startupLogged = true;
        }
        bool logBeat = q.GiLogIntervalFrames > 0 &&
            _frame > 0 && (int)_frame % q.GiLogIntervalFrames == 0;
        // Manual debug dump: dump GiProxies in the same frame and print DDGI parameters after setup.
        bool dumpDdgi = DumpOnce;
        if (dumpDdgi)
            GiProxies.DumpOnce = true;

        // Proxy list: traverse the panel tree, clear the tail, and upload the full block outside any render pass.
        GiProxies.BeginCollect(min, snapped + half);
        GiProxies.Collect();
        GiProxies.EndCollect();
        g.UpdateStorageBuffer(_proxies!, GiProxies.AsBytes());

        // SH9 sky radiance for ray misses (clause 6): copy it from SceneEnvironment every frame, or fill with zero.
        Span<float> sh = stackalloc float[36];
        if (env != null && env.SphericalHarmonicsReady)
        {
            for (int i = 0; i < 9; i++)
            {
                var v = env.RadianceSH9[i];
                sh[i * 4] = v.X;
                sh[i * 4 + 1] = v.Y;
                sh[i * 4 + 2] = v.Z;
            }
        }
        g.UpdateStorageBuffer(_giSh!, MemoryMarshal.AsBytes(sh));

        // Upload all light types from EffectiveSceneLights into _lights every frame, including directional lights.
        // Record runs after App.Update (and baking), so this frame's lights are already ready.
        Span<byte> lightBytes = stackalloc byte[16 + 8 * 64];
        lightBytes.Clear();
        int packed = 0;
        var app = DeviceServices.BaseApp;
        if (app != null)
        {
            var lp = app.EffectiveSceneLights;
            int n = Math.Min(lp.LightCount, SceneLightParams.MaxLights);
            Span<GpuLight> src = MemoryMarshal.CreateSpan(ref lp.Lights[0], SceneLightParams.MaxLights);
            for (int i = 0; i < n && packed < 8; i++)
            {
                MemoryMarshal.AsBytes(src.Slice(i, 1)).CopyTo(lightBytes.Slice(16 + packed * 64, 64));
                packed++;
            }
        }
        MemoryMarshal.Cast<byte, float>(lightBytes)[0] = packed;
        g.UpdateStorageBuffer(_lights!, lightBytes);

        // Keep the probe grid centered on the same snapped point as the SDF volume; spacing is isotropic = extent / gridX.
        float spacing = extent / _gridX;
        var probeGridMin = snapped - new Vector3(_gridX * spacing, _gridY * spacing, _gridZ * spacing) * 0.5f;

        // 1) Gather: take the minimum proxy distance per voxel and write it into the R16Float volume.
        Span<float> gp = stackalloc float[8];
        gp[0] = min.X;
        gp[1] = min.Y;
        gp[2] = min.Z;
        gp[3] = voxel;
        gp[4] = _resolution;
        gp[5] = GiProxies.Count;
        // Empty-list fallback: fill the whole volume with far so later ray marching exits immediately.
        gp[6] = extent;
        gp[7] = 0f;
        uint groups = _resolution / GatherGroup;
        g.DispatchCompute(new ComputeDispatchArgs
        {
            Kernel = _gather!,
            Params = MemoryMarshal.AsBytes(gp),
            Resources = _gatherRes,
            GroupsX = groups,
            GroupsY = groups,
            GroupsZ = groups,
        });

        // 2) probeUpdate: one workgroup per probe, one ray per thread, followed by octahedral integration
        //    and hysteresis against the previous frame; amortization re-traces only 1/divisor of probes per frame.
        uint rays = Math.Min((uint)Math.Max(1, q.GiRaysPerProbe), UpdateThreads);
        Span<float> up = stackalloc float[28];
        up[0] = probeGridMin.X; up[1] = probeGridMin.Y; up[2] = probeGridMin.Z; up[3] = spacing;
        up[4] = min.X; up[5] = min.Y; up[6] = min.Z; up[7] = voxel;
        up[8] = _resolution; up[9] = GiProxies.Count; up[10] = rays; up[11] = q.GiTraceMaxSteps;
        up[12] = q.GiHysteresis; up[13] = q.GiNormalBias; up[14] = env?.SkyIntensity ?? 1f; up[15] = _frame;
        up[16] = _gridX; up[17] = _gridY; up[18] = _gridZ; up[19] = Math.Max(1, q.GiProbeUpdateDivisor);
        up[20] = extent; up[21] = extent;
        // Step 5 independent controls (uExtent.z/.w, clause 13): validity EMA window and validity toggle.
        up[22] = q.GiBackfaceHysteresis;
        up[23] = q.GiProbeValidity ? 1f : 0f;
        // Step 2c hit-shading controls (uShade): shadow steps, bounce gain, punctual-shadow toggle,
        // and the Step 5 backface-ratio threshold in .w.
        up[24] = Math.Max(1, q.GiShadowSteps);
        up[25] = q.GiBounceGain;
        up[26] = q.GiPunctualShadow ? 1f : 0f;
        up[27] = q.GiBackfaceThreshold;
        if (dumpDdgi || logStartup || logKnobs || logBeat)
        {
            if (dumpDdgi)
                DumpOnce = false;
            var dbg = DeviceServices.BaseApp;
            if (logStartup)
                dbg?.AddLog(LogType.GI, "[Ddgi] ==== startup ====");
            if (logKnobs)
                dbg?.AddLog(LogType.GI, $"[Ddgi] ==== knobs changed (frame={_frame}) ====");
            if (dumpDdgi)
                dbg?.AddLog(LogType.GI, "[Ddgi] ==== manual dump ====");
            bool shReady = env != null && env.SphericalHarmonicsReady;
            var sh0 = shReady ? env!.RadianceSH9[0] : Vector4.Zero;
            dbg?.AddLog(LogType.GI, $"[Ddgi] extent={extent:F2} spacing={spacing:F3} grid={_gridX}x{_gridY}x{_gridZ} " +
                $"probes={_probeCount} res={_resolution} voxel={voxel:F3} rays={rays} maxSteps={q.GiTraceMaxSteps} " +
                $"shadowSteps={q.GiShadowSteps}");
            dbg?.AddLog(LogType.GI, $"[Ddgi] hysteresis={q.GiHysteresis:F3} normalBias={q.GiNormalBias:F3} " +
                $"skyIntensity={(env?.SkyIntensity ?? 1f):F2} GiIntensity={q.GiIntensity:F2} " +
                $"chebyshev={q.GiChebyshevOcclusion} divisor={Math.Max(1, q.GiProbeUpdateDivisor)} " +
                $"backfaceThr={q.GiBackfaceThreshold:F2} backfaceHyst={q.GiBackfaceHysteresis:F3} " +
                $"validity={q.GiProbeValidity} bounce={q.GiBounceGain:F2} punctualShadow={q.GiPunctualShadow}");
            dbg?.AddLog(LogType.GI, $"[Ddgi] shReady={shReady} shAmbient=({sh0.X:F3},{sh0.Y:F3},{sh0.Z:F3}) " +
                $"proxies={GiProxies.Count} overflow={GiProxies.Overflow} lights={packed} " +
                $"atlas={_atlasW}x{_atlasH} pingWrite={_pingWrite}");
            if (logBeat)
            {
                uint beatDivisor = Math.Max(1u, (uint)q.GiProbeUpdateDivisor);
                uint phase = _frame % beatDivisor;
                uint traced = phase < _probeCount ? (_probeCount - phase + beatDivisor - 1) / beatDivisor : 0u;
                dbg?.AddLog(LogType.GI, $"[Ddgi] beat frame={_frame} phase={phase} traced={traced} " +
                    $"voxel={voxel:F3} center=({snapped.X:F2},{snapped.Y:F2},{snapped.Z:F2}) pingWrite={_pingWrite}");
            }
        }
        g.DispatchCompute(new ComputeDispatchArgs
        {
            Kernel = _update!,
            Params = MemoryMarshal.AsBytes(up),
            Resources = _pingWrite ? _updateResP1! : _updateResP0!,
            GroupsX = _probeCount,
            GroupsY = 1,
            GroupsZ = 1,
        });

        // 3) Slice: render the unchanged main debug area plus the lower probe-atlas strip from this frame's write surface.
        Span<float> sp = stackalloc float[8];
        sp[0] = DebugSize;
        sp[1] = DebugMainRows;
        sp[2] = extent * 0.5f;
        sp[3] = _gridX;
        sp[4] = _gridY;
        sp[5] = _gridZ;
        sp[6] = 0f;
        sp[7] = 0f;
        g.DispatchCompute(new ComputeDispatchArgs
        {
            Kernel = _slice!,
            Params = MemoryMarshal.AsBytes(sp),
            Resources = _pingWrite ? _sliceResP1! : _sliceResP0!,
            GroupsX = (DebugSize + 7) / 8,
            GroupsY = (DebugSize + 7) / 8,
            GroupsZ = 1,
        });

        // Step 2b publish state: the surface just written this frame becomes the consumed surface,
        // and will be sampled by the next frame's main pass.
        s_gridMin = probeGridMin;
        s_spacing = spacing;
        s_gx = _gridX; s_gy = _gridY; s_gz = _gridZ;
        s_readName = _pingWrite ? IrrAtlasName1 : IrrAtlasName0;
        s_depName = _pingWrite ? DepAtlasName1 : DepAtlasName0;
        s_ready = true;

        // Flip ping-pong and advance the frame counter for amortization and hysteresis.
        _frame++;
        _pingWrite = !_pingWrite;
    }

    public void Dispose()
    {
        s_ready = false;
        _gather?.Dispose();
        _gather = null;
        _slice?.Dispose();
        _slice = null;
        _update?.Dispose();
        _update = null;
        _proxies?.Dispose();
        _proxies = null;
        _giSh?.Dispose();
        _giSh = null;
        _lights?.Dispose();
        _lights = null;
    }

    // ── Shader sources shared across all four backends. Slot order follows the binding layout above,
    //    and every kernel keeps a single exit to avoid fxc X4000. The proxy SDF formula is ported verbatim: ──
    //   q = |p - center| - extents
    //   d = length(max(q, 0)) + min(max(q.x, q.y, q.z), 0) - round
    // World voxel coordinates: p = volumeMin + (id + 0.5) * voxelSize.
    // Distance field: d(p) = min proxySdf over i in [0, proxyCount); use far when proxyCount == 0.
    //
    // Each proxy occupies 64B / four float4 values (see GiProxy):
    //   +0  center.xyz, _        +16 extents.xyz, round
    //   +32 albedo.rgb, _        +48 emissive.rgb, _
    // Step 1 gather reads only the first two float4 values; the debug strip reads the third one (albedo).

    // ── D3D12 HLSL cs_5_0（fxc）──

    /// <summary>kernel1 sdfGather: writes the res^3 volume with a 4x4x4 workgroup. The typed UAV stores only the .x channel in R16_FLOAT.</summary>
    const string SourceGatherHlsl = @"
cbuffer DdgiGatherParams : register(b0)
{
    float4 uVolumeMin;
    float4 uGather;
};

ByteAddressBuffer uProxies : register(t0);
RWTexture3D<float4> uVolume : register(u0);

float ProxySdf(float3 p, float4 c, float4 er)
{
    float3 q = abs(p - c.xyz) - er.xyz;
    return length(max(q, 0.0)) + min(max(q.x, max(q.y, q.z)), 0.0) - er.w;
}

[numthreads(4, 4, 4)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    uint n = (uint)uGather.x;
    if (id.x < n && id.y < n && id.z < n)
    {
        float3 p = uVolumeMin.xyz + (float3(id) + 0.5) * uVolumeMin.w;
        float d = uGather.z;
        uint count = (uint)uGather.y;
        for (uint i = 0; i < count; i++)
        {
            int off = (int)(i * 64);
            float4 c = asfloat(uProxies.Load4(off));
            float4 er = asfloat(uProxies.Load4(off + 16));
            d = min(d, ProxySdf(p, c, er));
        }
        uVolume[id] = float4(d, 0.0, 0.0, 0.0);
    }
}
";

    /// <summary>kernel3 sdfSlice: default 8x8x1 layout, with an XZ horizontal slice in the main area and probe-atlas samples in the bottom strip.</summary>
    const string SourceSliceHlsl = @"
cbuffer DdgiSliceParams : register(b0)
{
    float uSize;
    float uMainRows;
    float uHalfExtent;
    float uGridX;
    float uGridY;
    float uGridZ;
    float uPad0;
    float uPad1;
};

Texture3D<float4> uVolume : register(t0);
Texture2D<float4> uAtlas : register(t1);
SamplerState uSampler : register(s0);
RWTexture2D<float4> uOutput : register(u0);

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x < (uint)uSize && id.y < (uint)uSize)
    {
        float3 rgb = float3(0.02, 0.02, 0.04);
        if (id.y < (uint)uMainRows)
        {
            float2 uv = (float2(id.xy) + 0.5) / float2(uSize, uMainRows);
            float d = uVolume.SampleLevel(uSampler, float3(uv.x, 0.5, uv.y), 0).x;
            float dn = d / uHalfExtent;
            float3 inside = float3(0.90, 0.32, 0.18) * (0.30 + 0.70 * saturate(-dn * 8.0));
            float3 outside = lerp(float3(0.06, 0.42, 0.62), float3(0.02, 0.02, 0.05), saturate(dn * 2.5));
            rgb = d < 0.0 ? inside : outside;
            if (d > 0.0 && frac(d) < 0.08)
                rgb += 0.18;
        }
        else
        {
            // Bottom strip: map screen X to the middle probe column (x = GridX / 2, z = GridZ / 2) along Y.
            // Normalize luminance against the whole column so small variation stays visible.
            // When the full column is flat, show the raw magnitude in cyan to distinguish zero from constant high output.
            uint gx = (uint)uGridX;
            uint gy = (uint)uGridY;
            uint px = gx / 2u;
            uint pz = (uint)uGridZ / 2u;
            uint acx = (px + pz * gx) * 8u + 4u;
            uint py = min((uint)((float)id.x / uSize * (float)gy), gy - 1u);
            float3 lw = float3(0.2126, 0.7152, 0.0722);
            float4 cc = uAtlas.Load(int3((int)acx, (int)(py * 8u + 4u), 0));
            float lum = dot(cc.rgb, lw);
            float mn = 1e30;
            float mx = -1e30;
            for (uint k = 0u; k < gy; k++)
            {
                float l = dot(uAtlas.Load(int3((int)acx, (int)(k * 8u + 4u), 0)).rgb, lw);
                mn = min(mn, l);
                mx = max(mx, l);
            }
            float rng = mx - mn;
            if (rng > 1e-4)
            {
                float t = (lum - mn) / rng;
                rgb = float3(t, t, t);
            }
            else
            {
                float m = mn / (1.0 + mn);
                rgb = float3(0.0, m, m);
            }
            // Step 5: mark invalid probes in red when classification alpha < 0.5, overriding grayscale or cyan display.
            if (cc.a < 0.5)
                rgb = float3(0.85, 0.15, 0.10);
        }
        uOutput[id.xy] = float4(rgb, 1.0);
    }
}
";

    // ── Vulkan GLSL 450 (glslang -> SPIR-V, entry point always main, binding index equals declaration order) ──

    /// <summary>kernel1 sdfGather. The r16f format qualifier must match the Vulkan image format (R16_SFLOAT).</summary>
    const string SourceGatherGlsl = @"#version 450
layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;

layout(push_constant) uniform DdgiGatherParams
{
    vec4 uVolumeMin;
    vec4 uGather;
};

layout(binding = 1, std430) readonly buffer Proxies { vec4 data[]; } uProxies;
layout(binding = 2, r16f) uniform writeonly image3D uVolume;

float ProxySdf(vec3 p, vec4 c, vec4 er)
{
    vec3 q = abs(p - c.xyz) - er.xyz;
    return length(max(q, vec3(0.0))) + min(max(q.x, max(q.y, q.z)), 0.0) - er.w;
}

void main()
{
    uvec3 id = gl_GlobalInvocationID;
    uint n = uint(uGather.x);
    if (id.x < n && id.y < n && id.z < n)
    {
        vec3 p = uVolumeMin.xyz + (vec3(id) + 0.5) * uVolumeMin.w;
        float d = uGather.z;
        uint count = uint(uGather.y);
        for (uint i = 0u; i < count; i++)
        {
            vec4 c = uProxies.data[i * 4u];
            vec4 er = uProxies.data[i * 4u + 1u];
            d = min(d, ProxySdf(p, c, er));
        }
        imageStore(uVolume, ivec3(id), vec4(d, 0.0, 0.0, 0.0));
    }
}
";

    /// <summary>kernel2 sdfSlice。</summary>
    const string SourceSliceGlsl = @"#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(push_constant) uniform DdgiSliceParams
{
    float uSize;
    float uMainRows;
    float uHalfExtent;
    float uGridX;
    float uGridY;
    float uGridZ;
    float uPad0;
    float uPad1;
};

layout(binding = 1) uniform sampler3D uVolume;
layout(binding = 2) uniform sampler2D uAtlas;
layout(binding = 3, rgba8) uniform writeonly image2D uOutput;

void main()
{
    uvec2 id = gl_GlobalInvocationID.xy;
    if (id.x < uint(uSize) && id.y < uint(uSize))
    {
        vec3 rgb = vec3(0.02, 0.02, 0.04);
        if (id.y < uint(uMainRows))
        {
            vec2 uv = (vec2(id) + 0.5) / vec2(uSize, uMainRows);
            float d = textureLod(uVolume, vec3(uv.x, 0.5, uv.y), 0.0).x;
            float dn = d / uHalfExtent;
            vec3 inside = vec3(0.90, 0.32, 0.18) * (0.30 + 0.70 * clamp(-dn * 8.0, 0.0, 1.0));
            vec3 outside = mix(vec3(0.06, 0.42, 0.62), vec3(0.02, 0.02, 0.05), clamp(dn * 2.5, 0.0, 1.0));
            rgb = d < 0.0 ? inside : outside;
            if (d > 0.0 && fract(d) < 0.08)
                rgb += vec3(0.18);
        }
        else
        {
            // Bottom strip: map screen X to the middle probe column along Y, normalize by the column min/max,
            // and fall back to cyan for flat columns.
            uint gx = uint(uGridX);
            uint gy = uint(uGridY);
            uint px = gx / 2u;
            uint pz = uint(uGridZ) / 2u;
            uint acx = (px + pz * gx) * 8u + 4u;
            uint py = min(uint(float(id.x) / uSize * float(gy)), gy - 1u);
            vec3 lw = vec3(0.2126, 0.7152, 0.0722);
            vec4 cc = texelFetch(uAtlas, ivec2(int(acx), int(py * 8u + 4u)), 0);
            float lum = dot(cc.rgb, lw);
            float mn = 1e30;
            float mx = -1e30;
            for (uint k = 0u; k < gy; k++)
            {
                float l = dot(texelFetch(uAtlas, ivec2(int(acx), int(k * 8u + 4u)), 0).rgb, lw);
                mn = min(mn, l);
                mx = max(mx, l);
            }
            float rng = mx - mn;
            if (rng > 1e-4)
            {
                float t = (lum - mn) / rng;
                rgb = vec3(t);
            }
            else
            {
                float m = mn / (1.0 + mn);
                rgb = vec3(0.0, m, m);
            }
            // Step 5: mark invalid probes in red when classification alpha < 0.5.
            if (cc.a < 0.5)
                rgb = vec3(0.85, 0.15, 0.10);
        }
        imageStore(uOutput, ivec2(id), vec4(rgb, 1.0));
    }
}
";

    // ── Metal MSL (textures map to texture(declaration index), buffers to buffer(declaration index + 1),
    //    and workgroup size is supplied at dispatch time through ComputeKernelDesc.WorkgroupX/Y/Z) ──

    /// <summary>kernel1 sdfGather。</summary>
    const string SourceGatherMsl = @"
#include <metal_stdlib>
using namespace metal;

struct DdgiGatherParams
{
    float4 uVolumeMin;
    float4 uGather;
};

static float ProxySdf(float3 p, float4 c, float4 er)
{
    float3 q = abs(p - c.xyz) - er.xyz;
    return length(max(q, float3(0.0))) + min(max(q.x, max(q.y, q.z)), 0.0) - er.w;
}

kernel void CSMain(
    constant DdgiGatherParams& params [[buffer(0)]],
    const device float4* uProxies [[buffer(1)]],
    texture3d<float, access::write> uVolume [[texture(0)]],
    uint3 id [[thread_position_in_grid]])
{
    uint n = (uint)params.uGather.x;
    if (id.x < n && id.y < n && id.z < n)
    {
        float3 p = params.uVolumeMin.xyz + (float3(id) + 0.5) * params.uVolumeMin.w;
        float d = params.uGather.z;
        uint count = (uint)params.uGather.y;
        for (uint i = 0; i < count; i++)
        {
            float4 c = uProxies[i * 4];
            float4 er = uProxies[i * 4 + 1];
            d = min(d, ProxySdf(p, c, er));
        }
        uVolume.write(float4(d, 0.0, 0.0, 0.0), id);
    }
}
";

    /// <summary>kernel3 sdfSlice with atlas bound explicitly at texture(1) and output moved to texture(2).</summary>
    const string SourceSliceMsl = @"
#include <metal_stdlib>
using namespace metal;

struct DdgiSliceParams
{
    float uSize;
    float uMainRows;
    float uHalfExtent;
    float uGridX;
    float uGridY;
    float uGridZ;
    float uPad0;
    float uPad1;
};

kernel void CSMain(
    constant DdgiSliceParams& params [[buffer(0)]],
    texture3d<float> uVolume [[texture(0)]],
    texture2d<float, access::read> uAtlas [[texture(1)]],
    texture2d<float, access::write> uOutput [[texture(2)]],
    sampler uSampler [[sampler(0)]],
    uint2 id [[thread_position_in_grid]])
{
    if (id.x < (uint)params.uSize && id.y < (uint)params.uSize)
    {
        float3 rgb = float3(0.02, 0.02, 0.04);
        if (id.y < (uint)params.uMainRows)
        {
            float2 uv = (float2(id) + 0.5) / float2(params.uSize, params.uMainRows);
            float d = uVolume.sample(uSampler, float3(uv.x, 0.5, uv.y), level(0)).x;
            float dn = d / params.uHalfExtent;
            float3 inside = float3(0.90, 0.32, 0.18) * (0.30 + 0.70 * saturate(-dn * 8.0));
            float3 outside = mix(float3(0.06, 0.42, 0.62), float3(0.02, 0.02, 0.05), saturate(dn * 2.5));
            rgb = d < 0.0 ? inside : outside;
            if (d > 0.0 && fract(d) < 0.08)
                rgb += float3(0.18);
        }
        else
        {
            // Bottom strip: map screen X to the middle probe column along Y, normalize by the column min/max,
            // and fall back to cyan for flat columns.
            uint gx = (uint)params.uGridX;
            uint gy = (uint)params.uGridY;
            uint px = gx / 2u;
            uint pz = (uint)params.uGridZ / 2u;
            uint acx = (px + pz * gx) * 8u + 4u;
            uint py = min((uint)((float)id.x / params.uSize * (float)gy), gy - 1u);
            float3 lw = float3(0.2126, 0.7152, 0.0722);
            float4 cc = uAtlas.read(uint2(acx, py * 8u + 4u));
            float lum = dot(cc.rgb, lw);
            float mn = 1e30;
            float mx = -1e30;
            for (uint k = 0u; k < gy; k++)
            {
                float l = dot(uAtlas.read(uint2(acx, k * 8u + 4u)).rgb, lw);
                mn = min(mn, l);
                mx = max(mx, l);
            }
            float rng = mx - mn;
            if (rng > 1e-4)
            {
                float t = (lum - mn) / rng;
                rgb = float3(t, t, t);
            }
            else
            {
                float m = mn / (1.0 + mn);
                rgb = float3(0.0, m, m);
            }
            // Step 5: mark invalid probes in red when classification alpha < 0.5.
            if (cc.a < 0.5)
                rgb = float3(0.85, 0.15, 0.10);
        }
        uOutput.write(float4(rgb, 1.0), id);
    }
}
";

    // ── WebGPU WGSL (submitted through interop; seasonWebGPU.js does not embed source; @binding(i)
    //    follows declaration order, the engine sampler stays at @binding(15), and the 3D volume uses rgba16float) ──

    /// <summary>kernel1 sdfGather。</summary>
    const string SourceGatherWgsl = @"
struct DdgiGatherParams
{
    uVolumeMin : vec4<f32>,
    uGather : vec4<f32>,
};

@group(0) @binding(0) var<uniform> params : DdgiGatherParams;
@group(0) @binding(1) var<storage, read> uProxies : array<vec4<f32>>;
@group(0) @binding(2) var uVolume : texture_storage_3d<rgba16float, write>;

fn ProxySdf(p : vec3<f32>, c : vec4<f32>, er : vec4<f32>) -> f32
{
    let q = abs(p - c.xyz) - er.xyz;
    return length(max(q, vec3<f32>(0.0))) + min(max(q.x, max(q.y, q.z)), 0.0) - er.w;
}

@compute @workgroup_size(4, 4, 4)
fn CSMain(@builtin(global_invocation_id) id : vec3<u32>)
{
    let n = u32(params.uGather.x);
    if (id.x < n && id.y < n && id.z < n)
    {
        let p = params.uVolumeMin.xyz + (vec3<f32>(id) + vec3<f32>(0.5)) * params.uVolumeMin.w;
        var d = params.uGather.z;
        let count = u32(params.uGather.y);
        for (var i : u32 = 0u; i < count; i = i + 1u)
        {
            let c = uProxies[i * 4u];
            let er = uProxies[i * 4u + 1u];
            d = min(d, ProxySdf(p, c, er));
        }
        textureStore(uVolume, vec3<i32>(id), vec4<f32>(d, 0.0, 0.0, 0.0));
    }
}
";

    /// <summary>kernel2 sdfSlice。</summary>
    const string SourceSliceWgsl = @"
struct DdgiSliceParams
{
    uSize : f32,
    uMainRows : f32,
    uHalfExtent : f32,
    uGridX : f32,
    uGridY : f32,
    uGridZ : f32,
    uPad0 : f32,
    uPad1 : f32,
};

@group(0) @binding(0) var<uniform> params : DdgiSliceParams;
@group(0) @binding(1) var uVolume : texture_3d<f32>;
@group(0) @binding(2) var uAtlas : texture_2d<f32>;
@group(0) @binding(3) var uOutput : texture_storage_2d<rgba8unorm, write>;
@group(0) @binding(15) var uSampler : sampler;

@compute @workgroup_size(8, 8, 1)
fn CSMain(@builtin(global_invocation_id) id : vec3<u32>)
{
    if (id.x < u32(params.uSize) && id.y < u32(params.uSize))
    {
        var rgb = vec3<f32>(0.02, 0.02, 0.04);
        if (id.y < u32(params.uMainRows))
        {
            let uv = (vec2<f32>(f32(id.x), f32(id.y)) + vec2<f32>(0.5))
                   / vec2<f32>(params.uSize, params.uMainRows);
            let d = textureSampleLevel(uVolume, uSampler, vec3<f32>(uv.x, 0.5, uv.y), 0.0).x;
            let dn = d / params.uHalfExtent;
            let inside = vec3<f32>(0.90, 0.32, 0.18) * (0.30 + 0.70 * clamp(-dn * 8.0, 0.0, 1.0));
            let outside = mix(vec3<f32>(0.06, 0.42, 0.62), vec3<f32>(0.02, 0.02, 0.05), clamp(dn * 2.5, 0.0, 1.0));
            if (d < 0.0)
            {
                rgb = inside;
            }
            else
            {
                rgb = outside;
                if (fract(d) < 0.08)
                {
                    rgb = rgb + vec3<f32>(0.18);
                }
            }
        }
        else
        {
            // Bottom strip: map screen X to the middle probe column along Y, normalize by the column min/max,
            // and fall back to cyan for flat columns.
            let gx = u32(params.uGridX);
            let gy = u32(params.uGridY);
            let px = gx / 2u;
            let pz = u32(params.uGridZ) / 2u;
            let acx = i32((px + pz * gx) * 8u + 4u);
            let py = min(u32(f32(id.x) / params.uSize * f32(gy)), gy - 1u);
            let lw = vec3<f32>(0.2126, 0.7152, 0.0722);
            let cc = textureLoad(uAtlas, vec2<i32>(acx, i32(py * 8u + 4u)), 0);
            let lum = dot(cc.rgb, lw);
            var mn = 1e30;
            var mx = -1e30;
            for (var k : u32 = 0u; k < gy; k = k + 1u)
            {
                let l = dot(textureLoad(uAtlas, vec2<i32>(acx, i32(k * 8u + 4u)), 0).rgb, lw);
                mn = min(mn, l);
                mx = max(mx, l);
            }
            let rng = mx - mn;
            if (rng > 1e-4)
            {
                let t = (lum - mn) / rng;
                rgb = vec3<f32>(t, t, t);
            }
            else
            {
                let m = mn / (1.0 + mn);
                rgb = vec3<f32>(0.0, m, m);
            }
            // Step 5: mark invalid probes in red when classification alpha < 0.5.
            if (cc.a < 0.5)
            {
                rgb = vec3<f32>(0.85, 0.15, 0.10);
            }
        }
        textureStore(uOutput, vec2<i32>(i32(id.x), i32(id.y)), vec4<f32>(rgb, 1.0));
    }
}
";

    // ── kernel ddgiProbeUpdate: one workgroup per probe, fixed at 128 threads. The trace stage launches
    //    one spherical Fibonacci ray per active thread, rotated per frame with the R2 sequence. Hits
    //    resolve the nearest proxy and shade with direct lighting plus previous-frame bounce; misses
    //    sample sky SH9. The integrate stage writes irradiance and depth-moment tiles, blends with
    //    hysteresis, and keeps barriers in unconditional uniform control flow. Step 5 also classifies
    //    probe validity, writes it into irradiance alpha, and filters invalid probes out of bounce feedback. ──

    // ── D3D12 HLSL cs_5_0（fxc）──
    const string SourceUpdateHlsl = @"
cbuffer DdgiUpdateParams : register(b0)
{
    float4 uProbeGrid;
    float4 uVolume;
    float4 uTrace;
    float4 uAccum;
    float4 uGrid;
    float4 uExtent;
    float4 uShade;
};

ByteAddressBuffer uProxies : register(t0);
Texture3D<float4> uSdf : register(t1);
ByteAddressBuffer uSh : register(t2);
Texture2D<float4> uPrev : register(t3);
Texture2D<float4> uPrevDep : register(t4);
ByteAddressBuffer uLights : register(t5);
SamplerState uSampler : register(s0);
RWTexture2D<float4> uWrite : register(u0);
RWTexture2D<float4> uWriteDep : register(u1);

groupshared float3 gRad[128];
groupshared float3 gDir[128];
groupshared float gHit[128];
// Step 5 validity classification: 0 = miss, 1 = front-face hit, 2 = back-face hit.
groupshared float gBack[128];

float ProxySdf(float3 p, float4 c, float4 er)
{
    float3 q = abs(p - c.xyz) - er.xyz;
    return length(max(q, 0.0)) + min(max(q.x, max(q.y, q.z)), 0.0) - er.w;
}

float3 EvalSh(float3 dir)
{
    float3 r = asfloat(uSh.Load4(0)).rgb;
    r += asfloat(uSh.Load4(16)).rgb * dir.y;
    r += asfloat(uSh.Load4(32)).rgb * dir.z;
    r += asfloat(uSh.Load4(48)).rgb * dir.x;
    r += asfloat(uSh.Load4(64)).rgb * (dir.x * dir.y);
    r += asfloat(uSh.Load4(80)).rgb * (dir.y * dir.z);
    r += asfloat(uSh.Load4(96)).rgb * (3.0 * dir.z * dir.z - 1.0);
    r += asfloat(uSh.Load4(112)).rgb * (dir.x * dir.z);
    r += asfloat(uSh.Load4(128)).rgb * (dir.x * dir.x - dir.y * dir.y);
    return r;
}

float3 SdfNormal(float3 p, float eps)
{
    float3 o = uVolume.xyz;
    float e = uExtent.x;
    float dx = uSdf.SampleLevel(uSampler, (p + float3(eps, 0.0, 0.0) - o) / e, 0).x
             - uSdf.SampleLevel(uSampler, (p - float3(eps, 0.0, 0.0) - o) / e, 0).x;
    float dy = uSdf.SampleLevel(uSampler, (p + float3(0.0, eps, 0.0) - o) / e, 0).x
             - uSdf.SampleLevel(uSampler, (p - float3(0.0, eps, 0.0) - o) / e, 0).x;
    float dz = uSdf.SampleLevel(uSampler, (p + float3(0.0, 0.0, eps) - o) / e, 0).x
             - uSdf.SampleLevel(uSampler, (p - float3(0.0, 0.0, eps) - o) / e, 0).x;
    return float3(dx, dy, dz);
}

// March from the hit point toward the light along L: return 0 when blocked, otherwise 1.
// Bias the start point by one voxel along L to avoid immediate self-shadowing.
// Use voxel * 0.5 as the hit threshold so grazing rays do not false-positive.
float SdfShadow(float3 p, float3 L, float maxDist, float voxel, float ext, uint steps)
{
    float vis = 1.0;
    float3 q = p + L * voxel;
    float t = voxel;
    for (uint s = 0; s < steps; s++)
    {
        float3 uvw = (q - uVolume.xyz) / ext;
        bool outside = uvw.x < 0.0 || uvw.x > 1.0 || uvw.y < 0.0 || uvw.y > 1.0 || uvw.z < 0.0 || uvw.z > 1.0;
        if (outside || t >= maxDist)
            break;
        float d = uSdf.SampleLevel(uSampler, uvw, 0).x;
        if (d < voxel * 0.5)
        {
            vis = 0.0;
            break;
        }
        float adv = max(d, voxel * 0.5);
        q += L * adv;
        t += adv;
    }
    return vis;
}

// Direct irradiance E at the hit point, before division by PI.
// Supports directional, spot, and point lights with formulas matching the main pass.
float3 EvalLights(float3 p, float3 N, float voxel, float ext, uint steps, float punctualShadow)
{
    float3 sum = float3(0.0, 0.0, 0.0);
    uint lcount = (uint)asfloat(uLights.Load(0));
    for (uint i = 0; i < lcount; i++)
    {
        int off = 16 + (int)(i * 64);
        float4 posRange = asfloat(uLights.Load4(off));
        float4 colorIntensity = asfloat(uLights.Load4(off + 16));
        float4 dirType = asfloat(uLights.Load4(off + 32));
        float4 spotParams = asfloat(uLights.Load4(off + 48));
        float3 L;
        float attenuation;
        float maxDist;
        bool wantShadow;
        if (dirType.w >= 1.5)
        {
            L = -normalize(dirType.xyz);
            attenuation = 1.0;
            maxDist = ext;
            wantShadow = true;
        }
        else
        {
            float3 toLight = posRange.xyz - p;
            float dist = length(toLight);
            L = toLight / max(dist, 0.0001);
            attenuation = 1.0 / max(dist * dist, 0.0001);
            float range = posRange.w;
            if (range > 0.0)
            {
                float win = saturate(1.0 - pow(dist / range, 4.0));
                attenuation *= win * win;
            }
            if (dirType.w > 0.5)
            {
                attenuation *= smoothstep(spotParams.y, spotParams.x, dot(-L, normalize(dirType.xyz)));
            }
            maxDist = dist;
            wantShadow = punctualShadow > 0.5;
        }
        float e = attenuation * max(dot(N, L), 0.0);
        // Pay the shadow cost only for lights that actually contribute.
        float vis = (e > 0.00001 && wantShadow) ? SdfShadow(p, L, maxDist, voxel, ext, steps) : 1.0;
        sum += colorIntensity.xyz * colorIntensity.w * e * vis;
    }
    return sum;
}

float3 FibDir(uint i, float n)
{
    float fi = float(i);
    float phi = fi * 2.399963229728653;
    float z = 1.0 - (2.0 * fi + 1.0) / n;
    float r = sqrt(saturate(1.0 - z * z));
    return float3(cos(phi) * r, sin(phi) * r, z);
}

// Two-axis rotation driven by an R2 low-discrepancy sequence: rotate around Y then X,
// changing the ray set every frame so hysteresis fills the sphere over time.
float3 RotateRay(float3 d, float frame)
{
    float a1 = frac(frame * 0.7548776662466927) * 6.28318530718;
    float a2 = frac(frame * 0.5698402909980532) * 6.28318530718;
    float s1 = sin(a1); float c1 = cos(a1); float s2 = sin(a2); float c2 = cos(a2);
    float3 r = float3(c1 * d.x + s1 * d.z, d.y, -s1 * d.x + c1 * d.z);
    return float3(r.x, c2 * r.y - s2 * r.z, s2 * r.y + c2 * r.z);
}

float3 OctDecode(float2 f)
{
    float3 n = float3(f.x, f.y, 1.0 - abs(f.x) - abs(f.y));
    float t = saturate(-n.z);
    n.x += n.x >= 0.0 ? -t : t;
    n.y += n.y >= 0.0 ? -t : t;
    return normalize(n);
}

float2 OctEncode(float3 dir)
{
    float3 a = abs(dir);
    float2 p = dir.xy / (a.x + a.y + a.z);
    if (dir.z < 0.0)
        p = (1.0 - abs(float2(p.y, p.x))) * float2(p.x >= 0.0 ? 1.0 : -1.0, p.y >= 0.0 ? 1.0 : -1.0);
    return p;
}

// Sample the previous irradiance atlas with 8-neighbor trilinear blending and back-face cosine weighting.
// This matches the E / PI scale of EvalLights / PI, so the values can be added directly.
float3 SampleBouncePrev(float3 p, float3 N)
{
    float3 gridMin = uProbeGrid.xyz;
    float spacing = uProbeGrid.w;
    float3 dims = uGrid.xyz;
    float2 atlasSize = float2(dims.x * dims.z * 8.0, dims.y * 8.0);
    float2 oct = OctEncode(N) * 0.5 + 0.5;
    float3 wp = p + N * uAccum.y;
    float3 gc = (wp - gridMin) / spacing - 0.5;
    float3 base = floor(gc);
    float3 f = gc - base;
    float3 sum = float3(0.0, 0.0, 0.0);
    float wsum = 0.0;
    for (int i = 0; i < 8; i++)
    {
        float3 off = float3(i & 1, (i >> 1) & 1, (i >> 2) & 1);
        float3 tri = lerp(1.0 - f, f, off);
        float w = tri.x * tri.y * tri.z;
        float3 pi = clamp(base + off, float3(0.0, 0.0, 0.0), dims - 1.0);
        float3 probePos = gridMin + (pi + 0.5) * spacing;
        float wdir = max(dot(normalize(probePos - p), N), 0.0);
        w *= wdir * wdir + 0.01;
        float2 tile = float2(pi.x + pi.z * dims.x, pi.y);
        float2 uv = (tile * 8.0 + 1.0 + oct * 6.0) / atlasSize;
        // Step 5: attenuate continuously by validity alpha so embedded or back-facing probes do not
        // spread light through bounce feedback. Continuous weighting avoids hard-gate flicker.
        float bval = uPrev.SampleLevel(uSampler, (tile * 8.0 + 4.0) / atlasSize, 0).a;
        w *= saturate(bval);
        sum += uPrev.SampleLevel(uSampler, uv, 0).rgb * w;
        wsum += w;
    }
    return wsum > 1e-6 ? sum / wsum : float3(0.0, 0.0, 0.0);
}

int2 BorderMap(int tx, int ty)
{
    bool lB = tx == 0; bool rB = tx == 7; bool tB = ty == 0; bool bB = ty == 7;
    int2 src = int2(tx, ty);
    if ((lB || rB) && (tB || bB))
        src = int2(lB ? 6 : 1, tB ? 6 : 1);
    else if (lB) src = int2(1, 7 - ty);
    else if (rB) src = int2(6, 7 - ty);
    else if (tB) src = int2(7 - tx, 1);
    else if (bB) src = int2(7 - tx, 6);
    return src;
}

int2 BorderMapD(int tx, int ty)
{
    bool lB = tx == 0; bool rB = tx == 15; bool tB = ty == 0; bool bB = ty == 15;
    int2 src = int2(tx, ty);
    if ((lB || rB) && (tB || bB))
        src = int2(lB ? 14 : 1, tB ? 14 : 1);
    else if (lB) src = int2(1, 15 - ty);
    else if (rB) src = int2(14, 15 - ty);
    else if (tB) src = int2(15 - tx, 1);
    else if (bB) src = int2(15 - tx, 14);
    return src;
}

[numthreads(128, 1, 1)]
void CSMain(uint3 gid : SV_GroupID, uint li : SV_GroupIndex)
{
    uint gx = (uint)uGrid.x;
    uint gz = (uint)uGrid.z;
    uint probe = gid.x;
    uint px = probe % gx;
    uint pz = (probe / gx) % gz;
    uint py = probe / (gx * gz);
    float3 probePos = uProbeGrid.xyz + (float3(px, py, pz) + 0.5) * uProbeGrid.w;
    uint tileX = px + pz * gx;
    uint tileY = py;

    uint divisor = (uint)uGrid.w;
    uint frame = (uint)uAccum.w;
    bool doTrace = divisor <= 1u || (frame % divisor) == (probe % divisor);

    uint rays = (uint)uTrace.z;
    float voxel = uVolume.w;
    float ext = uExtent.x;
    float farD = uExtent.y;
    float vHyst = uExtent.z;    // Step 5 validity EMA window, independent from irradiance hysteresis.
    float validityOn = uExtent.w; // Step 5 validity toggle: 1 = on, 0 = treat all probes as valid.
    uint maxSteps = (uint)uTrace.w;
    uint pcount = (uint)uTrace.y;

    if (doTrace && li < rays)
    {
        float3 dir = RotateRay(FibDir(li, (float)rays), uAccum.w);
        float3 p = probePos + dir * uAccum.y;
        float3 radiance = float3(0.0, 0.0, 0.0);
        bool hit = false;
        float back = 0.0;
        for (uint s = 0; s < maxSteps; s++)
        {
            float3 uvw = (p - uVolume.xyz) / ext;
            if (uvw.x < 0.0 || uvw.x > 1.0 || uvw.y < 0.0 || uvw.y > 1.0 || uvw.z < 0.0 || uvw.z > 1.0)
                break;
            float d = uSdf.SampleLevel(uSampler, uvw, 0).x;
            // Step 5 start-point clamp: if the biased origin lands inside geometry, retry from probePos.
            // This keeps near-wall rows from being misclassified while still rejecting truly embedded probes.
            if (s == 0u && d < 0.0)
            {
                p = probePos;
                uvw = (p - uVolume.xyz) / ext;
                if (uvw.x < 0.0 || uvw.x > 1.0 || uvw.y < 0.0 || uvw.y > 1.0 || uvw.z < 0.0 || uvw.z > 1.0)
                    break;
                d = uSdf.SampleLevel(uSampler, uvw, 0).x;
            }
            if (d < voxel)
            {
                float best = farD;
                float3 alb = float3(0.0, 0.0, 0.0);
                float3 emi = float3(0.0, 0.0, 0.0);
                for (uint j = 0; j < pcount; j++)
                {
                    int off = (int)(j * 64);
                    float4 c = asfloat(uProxies.Load4(off));
                    float4 er = asfloat(uProxies.Load4(off + 16));
                    float pd = ProxySdf(p, c, er);
                    if (pd < best)
                    {
                        best = pd;
                        alb = asfloat(uProxies.Load4(off + 32)).rgb;
                        emi = asfloat(uProxies.Load4(off + 48)).rgb;
                    }
                }
                float3 n = SdfNormal(p, voxel);
                n = dot(n, n) > 1e-16 ? normalize(n) : -dir;
                // Front-face-only hit test in SDF space: d < 0 means the ray is already inside solid geometry,
                // so treat it as a back-face hit. Do not switch back to a gradient-dot-ray test here.
                back = d < 0.0 ? 2.0 : 1.0;
                float3 e = EvalLights(p, n, voxel, ext, (uint)uShade.x, uShade.z);
                float3 bounce = uShade.y > 0.0 ? SampleBouncePrev(p, n) * uShade.y : float3(0.0, 0.0, 0.0);
                radiance = emi + alb * (e * 0.3183098861837907 + bounce);
                hit = true;
                break;
            }
            p += dir * max(d, voxel * 0.5);
        }
        if (!hit)
            radiance = max(EvalSh(dir), 0.0) * uAccum.z;
        gRad[li] = radiance;
        gDir[li] = dir;
        gHit[li] = hit ? length(p - probePos) : ext;
        gBack[li] = back;
    }
    GroupMemoryBarrierWithGroupSync();

    // Step 5 validity classification (Majercik 2019 §3.3, clause 13): map the back-face ratio to a
    // continuous valid value in [0, 1] with a linear ramp between 0.5 * thr and thr.
    // Misses are excluded from the denominator, and alpha starts at 0 so consumers naturally fall back
    // before the first valid update arrives.
    float hits = 0.0;
    float backs = 0.0;
    for (uint vr = 0; vr < rays; vr++)
    {
        hits += step(0.5, gBack[vr]);
        backs += step(1.5, gBack[vr]);
    }
    float ratio = hits > 0.0 ? backs / hits : 0.0;
    float lo = uShade.w * 0.5;
    // Toggle (uExtent.w, clause 13): when disabled, treat all probes as valid and write alpha = 1.
    float valid = validityOn > 0.5 ? 1.0 - saturate((ratio - lo) / max(uShade.w - lo, 1e-4)) : 1.0;

    // The workgroup has 128 threads while the irradiance tile has only 64 texels: li < 64 writes one
    // irradiance texel each, and all 128 threads cover the depth-moment tile two texels at a time.
    if (li < 64u)
    {
        int tx = (int)(li % 8u);
        int ty = (int)(li / 8u);
        uint2 ac = uint2(tileX * 8u + (uint)tx, tileY * 8u + (uint)ty);
        float4 prevFull = uPrev.Load(int3((int)ac.x, (int)ac.y, 0));
        float3 prev = prevFull.rgb;
        float3 res = prev;
        float va = prevFull.a;
        if (doTrace)
        {
            int2 src = BorderMap(tx, ty);
            float2 uv = ((float2(src.x - 1, src.y - 1) + 0.5) / 6.0) * 2.0 - 1.0;
            float3 tdir = OctDecode(uv);
            float3 sum = float3(0.0, 0.0, 0.0);
            float wsum = 0.0;
            for (uint r = 0; r < rays; r++)
            {
                float wgt = max(dot(gDir[r], tdir), 0.0);
                sum += gRad[r] * wgt;
                wsum += wgt;
            }
            float3 irr = wsum > 1e-6 ? sum / wsum : float3(0.0, 0.0, 0.0);
            res = lerp(irr, prev, uAccum.x);
        }
        // Alpha stores probe validity. Use a dedicated EMA window from uExtent.z so classification stays
        // more stable than irradiance, write 1 immediately when validity is disabled, and forward the
        // previous alpha on frames that skip tracing.
        uWrite[ac] = float4(res, validityOn > 0.5 ? (doTrace ? lerp(valid, va, vHyst) : va) : 1.0);
    }

    // Depth-moment integration over a 16x16 tile with a 14x14 core; 128 threads cover the full 256 texels.
    for (uint dti = li; dti < 256u; dti += 128u)
    {
        int dtx = (int)(dti % 16u);
        int dty = (int)(dti / 16u);
        uint2 dac = uint2(tileX * 16u + (uint)dtx, tileY * 16u + (uint)dty);
        float2 prevDep = uPrevDep.Load(int3((int)dac.x, (int)dac.y, 0)).xy;
        float2 resDep = prevDep;
        if (doTrace)
        {
            int2 dsrc = BorderMapD(dtx, dty);
            float2 duv = ((float2(dsrc.x - 1, dsrc.y - 1) + 0.5) / 14.0) * 2.0 - 1.0;
            float3 dtdir = OctDecode(duv);
            float2 dsum = float2(0.0, 0.0);
            float dwsum = 0.0;
            for (uint r = 0; r < rays; r++)
            {
                float dwgt = pow(max(dot(gDir[r], dtdir), 0.0), 16.0);
                dsum += float2(gHit[r], gHit[r] * gHit[r]) * dwgt;
                dwsum += dwgt;
            }
            float2 dep = dwsum > 1e-6 ? dsum / dwsum : float2(0.0, 0.0);
            resDep = lerp(dep, prevDep, uAccum.x);
        }
        uWriteDep[dac] = float4(resDep, 0.0, 0.0);
    }
}
";

    // ── Vulkan GLSL 450 (glslang -> SPIR-V; binding index equals declaration order) ──
    const string SourceUpdateGlsl = @"#version 450
layout(local_size_x = 128, local_size_y = 1, local_size_z = 1) in;

layout(push_constant) uniform DdgiUpdateParams
{
    vec4 uProbeGrid;
    vec4 uVolume;
    vec4 uTrace;
    vec4 uAccum;
    vec4 uGrid;
    vec4 uExtent;
    vec4 uShade;
};

layout(binding = 1, std430) readonly buffer Proxies { vec4 data[]; } uProxies;
layout(binding = 2) uniform sampler3D uSdf;
layout(binding = 3, std430) readonly buffer Sh { vec4 data[]; } uSh;
layout(binding = 4) uniform sampler2D uPrev;
layout(binding = 5, rgba16f) uniform writeonly image2D uWrite;
layout(binding = 6) uniform sampler2D uPrevDep;
layout(binding = 7, rg16f) uniform writeonly image2D uWriteDep;
layout(binding = 8, std430) readonly buffer Lights { vec4 data[]; } uLights;

shared vec3 gRad[128];
shared vec3 gDir[128];
shared float gHit[128];
// Step 5 validity classification: 0 = miss, 1 = front-face hit, 2 = back-face hit.
shared float gBack[128];

float ProxySdf(vec3 p, vec4 c, vec4 er)
{
    vec3 q = abs(p - c.xyz) - er.xyz;
    return length(max(q, vec3(0.0))) + min(max(q.x, max(q.y, q.z)), 0.0) - er.w;
}

vec3 EvalSh(vec3 dir)
{
    vec3 r = uSh.data[0].rgb;
    r += uSh.data[1].rgb * dir.y;
    r += uSh.data[2].rgb * dir.z;
    r += uSh.data[3].rgb * dir.x;
    r += uSh.data[4].rgb * (dir.x * dir.y);
    r += uSh.data[5].rgb * (dir.y * dir.z);
    r += uSh.data[6].rgb * (3.0 * dir.z * dir.z - 1.0);
    r += uSh.data[7].rgb * (dir.x * dir.z);
    r += uSh.data[8].rgb * (dir.x * dir.x - dir.y * dir.y);
    return r;
}

vec3 SdfNormal(vec3 p, float eps)
{
    vec3 o = uVolume.xyz;
    float e = uExtent.x;
    float dx = textureLod(uSdf, (p + vec3(eps, 0.0, 0.0) - o) / e, 0.0).x
             - textureLod(uSdf, (p - vec3(eps, 0.0, 0.0) - o) / e, 0.0).x;
    float dy = textureLod(uSdf, (p + vec3(0.0, eps, 0.0) - o) / e, 0.0).x
             - textureLod(uSdf, (p - vec3(0.0, eps, 0.0) - o) / e, 0.0).x;
    float dz = textureLod(uSdf, (p + vec3(0.0, 0.0, eps) - o) / e, 0.0).x
             - textureLod(uSdf, (p - vec3(0.0, 0.0, eps) - o) / e, 0.0).x;
    return vec3(dx, dy, dz);
}

// March from the hit point toward the light along L: return 0 when blocked, otherwise 1.
float SdfShadow(vec3 p, vec3 L, float maxDist, float voxel, float ext, uint steps)
{
    float vis = 1.0;
    vec3 q = p + L * voxel;
    float t = voxel;
    for (uint s = 0u; s < steps; s++)
    {
        vec3 uvw = (q - uVolume.xyz) / ext;
        bool outside = uvw.x < 0.0 || uvw.x > 1.0 || uvw.y < 0.0 || uvw.y > 1.0 || uvw.z < 0.0 || uvw.z > 1.0;
        if (outside || t >= maxDist)
            break;
        float d = textureLod(uSdf, uvw, 0.0).x;
        if (d < voxel * 0.5)
        {
            vis = 0.0;
            break;
        }
        float adv = max(d, voxel * 0.5);
        q += L * adv;
        t += adv;
    }
    return vis;
}

// Direct irradiance E at the hit point, before division by PI.
vec3 EvalLights(vec3 p, vec3 N, float voxel, float ext, uint steps, float punctualShadow)
{
    vec3 sum = vec3(0.0);
    uint lcount = uint(uLights.data[0].x);
    for (uint i = 0u; i < lcount; i++)
    {
        vec4 posRange = uLights.data[1u + i * 4u];
        vec4 colorIntensity = uLights.data[1u + i * 4u + 1u];
        vec4 dirType = uLights.data[1u + i * 4u + 2u];
        vec4 spotParams = uLights.data[1u + i * 4u + 3u];
        vec3 L;
        float attenuation;
        float maxDist;
        bool wantShadow;
        if (dirType.w >= 1.5)
        {
            L = -normalize(dirType.xyz);
            attenuation = 1.0;
            maxDist = ext;
            wantShadow = true;
        }
        else
        {
            vec3 toLight = posRange.xyz - p;
            float dist = length(toLight);
            L = toLight / max(dist, 0.0001);
            attenuation = 1.0 / max(dist * dist, 0.0001);
            float range = posRange.w;
            if (range > 0.0)
            {
                float win = clamp(1.0 - pow(dist / range, 4.0), 0.0, 1.0);
                attenuation *= win * win;
            }
            if (dirType.w > 0.5)
            {
                attenuation *= smoothstep(spotParams.y, spotParams.x, dot(-L, normalize(dirType.xyz)));
            }
            maxDist = dist;
            wantShadow = punctualShadow > 0.5;
        }
        float e = attenuation * max(dot(N, L), 0.0);
        float vis = (e > 0.00001 && wantShadow) ? SdfShadow(p, L, maxDist, voxel, ext, steps) : 1.0;
        sum += colorIntensity.xyz * colorIntensity.w * e * vis;
    }
    return sum;
}

vec3 FibDir(uint i, float n)
{
    float fi = float(i);
    float phi = fi * 2.399963229728653;
    float z = 1.0 - (2.0 * fi + 1.0) / n;
    float r = sqrt(clamp(1.0 - z * z, 0.0, 1.0));
    return vec3(cos(phi) * r, sin(phi) * r, z);
}

vec3 OctDecode(vec2 f)
{
    vec3 n = vec3(f.x, f.y, 1.0 - abs(f.x) - abs(f.y));
    float t = clamp(-n.z, 0.0, 1.0);
    n.x += n.x >= 0.0 ? -t : t;
    n.y += n.y >= 0.0 ? -t : t;
    return normalize(n);
}

// Two-axis rotation driven by an R2 low-discrepancy sequence: rotate around Y then X,
// changing the ray set every frame so hysteresis fills the sphere over time.
vec3 RotateRay(vec3 d, float frame)
{
    float a1 = fract(frame * 0.7548776662466927) * 6.28318530718;
    float a2 = fract(frame * 0.5698402909980532) * 6.28318530718;
    float s1 = sin(a1); float c1 = cos(a1); float s2 = sin(a2); float c2 = cos(a2);
    vec3 r = vec3(c1 * d.x + s1 * d.z, d.y, -s1 * d.x + c1 * d.z);
    return vec3(r.x, c2 * r.y - s2 * r.z, s2 * r.y + c2 * r.z);
}

vec2 OctEncode(vec3 dir)
{
    vec3 a = abs(dir);
    vec2 p = dir.xy / (a.x + a.y + a.z);
    if (dir.z < 0.0)
        p = (1.0 - abs(vec2(p.y, p.x))) * vec2(p.x >= 0.0 ? 1.0 : -1.0, p.y >= 0.0 ? 1.0 : -1.0);
    return p;
}

// Sample the previous irradiance atlas with 8-neighbor trilinear blending and back-face cosine weighting.
vec3 SampleBouncePrev(vec3 p, vec3 N)
{
    vec3 gridMin = uProbeGrid.xyz;
    float spacing = uProbeGrid.w;
    vec3 dims = uGrid.xyz;
    vec2 atlasSize = vec2(dims.x * dims.z * 8.0, dims.y * 8.0);
    vec2 oct = OctEncode(N) * 0.5 + 0.5;
    vec3 wp = p + N * uAccum.y;
    vec3 gc = (wp - gridMin) / spacing - 0.5;
    vec3 base = floor(gc);
    vec3 f = gc - base;
    vec3 sum = vec3(0.0);
    float wsum = 0.0;
    for (int i = 0; i < 8; i++)
    {
        vec3 off = vec3(float(i & 1), float((i >> 1) & 1), float((i >> 2) & 1));
        vec3 tri = mix(1.0 - f, f, off);
        float w = tri.x * tri.y * tri.z;
        vec3 pi = clamp(base + off, vec3(0.0), dims - 1.0);
        vec3 probePos = gridMin + (pi + 0.5) * spacing;
        float wdir = max(dot(normalize(probePos - p), N), 0.0);
        w *= wdir * wdir + 0.01;
        vec2 tile = vec2(pi.x + pi.z * dims.x, pi.y);
        vec2 uv = (tile * 8.0 + 1.0 + oct * 6.0) / atlasSize;
        // Step 5: attenuate continuously by validity alpha so invalid probes do not spread light
        // through bounce feedback. Continuous weighting avoids hard-gate flicker.
        float bval = textureLod(uPrev, (tile * 8.0 + 4.0) / atlasSize, 0.0).a;
        w *= clamp(bval, 0.0, 1.0);
        sum += textureLod(uPrev, uv, 0.0).rgb * w;
        wsum += w;
    }
    return wsum > 1e-6 ? sum / wsum : vec3(0.0);
}

ivec2 BorderMap(int tx, int ty)
{
    bool lB = tx == 0; bool rB = tx == 7; bool tB = ty == 0; bool bB = ty == 7;
    ivec2 src = ivec2(tx, ty);
    if ((lB || rB) && (tB || bB))
        src = ivec2(lB ? 6 : 1, tB ? 6 : 1);
    else if (lB) src = ivec2(1, 7 - ty);
    else if (rB) src = ivec2(6, 7 - ty);
    else if (tB) src = ivec2(7 - tx, 1);
    else if (bB) src = ivec2(7 - tx, 6);
    return src;
}

ivec2 BorderMapD(int tx, int ty)
{
    bool lB = tx == 0; bool rB = tx == 15; bool tB = ty == 0; bool bB = ty == 15;
    ivec2 src = ivec2(tx, ty);
    if ((lB || rB) && (tB || bB))
        src = ivec2(lB ? 14 : 1, tB ? 14 : 1);
    else if (lB) src = ivec2(1, 15 - ty);
    else if (rB) src = ivec2(14, 15 - ty);
    else if (tB) src = ivec2(15 - tx, 1);
    else if (bB) src = ivec2(15 - tx, 14);
    return src;
}

void main()
{
    uint li = gl_LocalInvocationID.x;
    uint gx = uint(uGrid.x);
    uint gz = uint(uGrid.z);
    uint probe = gl_WorkGroupID.x;
    uint px = probe % gx;
    uint pz = (probe / gx) % gz;
    uint py = probe / (gx * gz);
    vec3 probePos = uProbeGrid.xyz + (vec3(px, py, pz) + 0.5) * uProbeGrid.w;
    uint tileX = px + pz * gx;
    uint tileY = py;

    uint divisor = uint(uGrid.w);
    uint frame = uint(uAccum.w);
    bool doTrace = divisor <= 1u || (frame % divisor) == (probe % divisor);

    uint rays = uint(uTrace.z);
    float voxel = uVolume.w;
    float ext = uExtent.x;
    float farD = uExtent.y;
    float vHyst = uExtent.z;    // Step 5 validity EMA window, independent from irradiance hysteresis.
    float validityOn = uExtent.w; // Step 5 validity toggle: 1 = on, 0 = treat all probes as valid.
    uint maxSteps = uint(uTrace.w);
    uint pcount = uint(uTrace.y);

    if (doTrace && li < rays)
    {
        vec3 dir = RotateRay(FibDir(li, float(rays)), uAccum.w);
        vec3 p = probePos + dir * uAccum.y;
        vec3 radiance = vec3(0.0);
        bool hit = false;
        float back = 0.0;
        for (uint s = 0u; s < maxSteps; s++)
        {
            vec3 uvw = (p - uVolume.xyz) / ext;
            if (uvw.x < 0.0 || uvw.x > 1.0 || uvw.y < 0.0 || uvw.y > 1.0 || uvw.z < 0.0 || uvw.z > 1.0)
                break;
            float d = textureLod(uSdf, uvw, 0.0).x;
            // Step 5 start-point clamp: if the biased origin lands inside geometry, retry from probePos.
            // This preserves valid near-wall rows while still rejecting truly embedded probes.
            if (s == 0u && d < 0.0)
            {
                p = probePos;
                uvw = (p - uVolume.xyz) / ext;
                if (uvw.x < 0.0 || uvw.x > 1.0 || uvw.y < 0.0 || uvw.y > 1.0 || uvw.z < 0.0 || uvw.z > 1.0)
                    break;
                d = textureLod(uSdf, uvw, 0.0).x;
            }
            if (d < voxel)
            {
                float best = farD;
                vec3 alb = vec3(0.0);
                vec3 emi = vec3(0.0);
                for (uint j = 0u; j < pcount; j++)
                {
                    vec4 c = uProxies.data[j * 4u];
                    vec4 er = uProxies.data[j * 4u + 1u];
                    float pd = ProxySdf(p, c, er);
                    if (pd < best)
                    {
                        best = pd;
                        alb = uProxies.data[j * 4u + 2u].rgb;
                        emi = uProxies.data[j * 4u + 3u].rgb;
                    }
                }
                vec3 n = SdfNormal(p, voxel);
                n = dot(n, n) > 1e-16 ? normalize(n) : -dir;
                // Front-face-only hit test in SDF space: d < 0 means the ray is already inside solid geometry,
                // so treat it as a back-face hit. Do not switch back to a gradient-dot-ray test here.
                back = d < 0.0 ? 2.0 : 1.0;
                vec3 e = EvalLights(p, n, voxel, ext, uint(uShade.x), uShade.z);
                vec3 bounce = uShade.y > 0.0 ? SampleBouncePrev(p, n) * uShade.y : vec3(0.0);
                radiance = emi + alb * (e * 0.3183098861837907 + bounce);
                hit = true;
                break;
            }
            p += dir * max(d, voxel * 0.5);
        }
        if (!hit)
            radiance = max(EvalSh(dir), vec3(0.0)) * uAccum.z;
        gRad[li] = radiance;
        gDir[li] = dir;
        gHit[li] = hit ? length(p - probePos) : ext;
        gBack[li] = back;
    }
    memoryBarrierShared();
    barrier();

    // Step 5 validity classification (Majercik 2019 §3.3, clause 13): map the back-face ratio to a
    // continuous valid value in [0, 1] with a linear ramp between 0.5 * thr and thr.
    float hits = 0.0;
    float backs = 0.0;
    for (uint vr = 0u; vr < rays; vr++)
    {
        hits += step(0.5, gBack[vr]);
        backs += step(1.5, gBack[vr]);
    }
    float ratio = hits > 0.0 ? backs / hits : 0.0;
    float lo = uShade.w * 0.5;
    // Toggle (uExtent.w, clause 13): when disabled, treat all probes as valid and write alpha = 1.
    float valid = validityOn > 0.5 ? 1.0 - clamp((ratio - lo) / max(uShade.w - lo, 1e-4), 0.0, 1.0) : 1.0;

    // The workgroup has 128 threads while the irradiance tile has only 64 texels:
    // li < 64 writes irradiance, and all 128 threads cover the depth-moment tile.
    if (li < 64u)
    {
        int tx = int(li % 8u);
        int ty = int(li / 8u);
        ivec2 ac = ivec2(int(tileX * 8u) + tx, int(tileY * 8u) + ty);
        vec4 prevFull = texelFetch(uPrev, ac, 0);
        vec3 prev = prevFull.rgb;
        vec3 res = prev;
        float va = prevFull.a;
        if (doTrace)
        {
            ivec2 src = BorderMap(tx, ty);
            vec2 uv = ((vec2(src - 1) + 0.5) / 6.0) * 2.0 - 1.0;
            vec3 tdir = OctDecode(uv);
            vec3 sum = vec3(0.0);
            float wsum = 0.0;
            for (uint r = 0u; r < rays; r++)
            {
                float wgt = max(dot(gDir[r], tdir), 0.0);
                sum += gRad[r] * wgt;
                wsum += wgt;
            }
            vec3 irr = wsum > 1e-6 ? sum / wsum : vec3(0.0);
            res = mix(irr, prev, uAccum.x);
        }
        // Alpha stores probe validity. Use a dedicated EMA window from uExtent.z, write 1 immediately
        // when validity is disabled, and forward previous alpha on frames that skip tracing.
        imageStore(uWrite, ac, vec4(res, validityOn > 0.5 ? (doTrace ? mix(valid, va, vHyst) : va) : 1.0));
    }

    // Depth-moment integration over a 16x16 tile with a 14x14 core; 128 threads cover the full 256 texels.
    for (uint dti = li; dti < 256u; dti += 128u)
    {
        int dtx = int(dti % 16u);
        int dty = int(dti / 16u);
        ivec2 dac = ivec2(int(tileX * 16u) + dtx, int(tileY * 16u) + dty);
        vec2 prevDep = texelFetch(uPrevDep, dac, 0).xy;
        vec2 resDep = prevDep;
        if (doTrace)
        {
            ivec2 dsrc = BorderMapD(dtx, dty);
            vec2 duv = ((vec2(dsrc - 1) + 0.5) / 14.0) * 2.0 - 1.0;
            vec3 dtdir = OctDecode(duv);
            vec2 dsum = vec2(0.0);
            float dwsum = 0.0;
            for (uint r = 0u; r < rays; r++)
            {
                float dwgt = pow(max(dot(gDir[r], dtdir), 0.0), 16.0);
                dsum += vec2(gHit[r], gHit[r] * gHit[r]) * dwgt;
                dwsum += dwgt;
            }
            vec2 dep = dwsum > 1e-6 ? dsum / dwsum : vec2(0.0);
            resDep = mix(dep, prevDep, uAccum.x);
        }
        imageStore(uWriteDep, dac, vec4(resDep, 0.0, 0.0));
    }
}
";

    // ── Metal MSL: proxies -> buffer(1), sh -> buffer(2), sdf -> texture(0), prevIrr -> texture(1),
    //    writeIrr -> texture(2), prevDep -> texture(3), writeDep -> texture(4). Threadgroup arrays are
    //    declared inside the kernel, and prevIrr uses access::sample so SampleBouncePrev can bilinearly
    //    sample the octahedral core while still allowing read() in the hysteresis path. ──
    const string SourceUpdateMsl = @"
#include <metal_stdlib>
using namespace metal;

struct DdgiUpdateParams
{
    float4 uProbeGrid;
    float4 uVolume;
    float4 uTrace;
    float4 uAccum;
    float4 uGrid;
    float4 uExtent;
    float4 uShade;
};

static float ProxySdf(float3 p, float4 c, float4 er)
{
    float3 q = abs(p - c.xyz) - er.xyz;
    return length(max(q, float3(0.0))) + min(max(q.x, max(q.y, q.z)), 0.0) - er.w;
}

static float3 EvalSh(const device float4* sh, float3 dir)
{
    float3 r = sh[0].rgb;
    r += sh[1].rgb * dir.y;
    r += sh[2].rgb * dir.z;
    r += sh[3].rgb * dir.x;
    r += sh[4].rgb * (dir.x * dir.y);
    r += sh[5].rgb * (dir.y * dir.z);
    r += sh[6].rgb * (3.0 * dir.z * dir.z - 1.0);
    r += sh[7].rgb * (dir.x * dir.z);
    r += sh[8].rgb * (dir.x * dir.x - dir.y * dir.y);
    return r;
}

static float3 SdfNormal(texture3d<float> sdf, sampler sm, float3 volMin, float ext, float3 p, float eps)
{
    float dx = sdf.sample(sm, (p + float3(eps, 0.0, 0.0) - volMin) / ext, level(0)).x
             - sdf.sample(sm, (p - float3(eps, 0.0, 0.0) - volMin) / ext, level(0)).x;
    float dy = sdf.sample(sm, (p + float3(0.0, eps, 0.0) - volMin) / ext, level(0)).x
             - sdf.sample(sm, (p - float3(0.0, eps, 0.0) - volMin) / ext, level(0)).x;
    float dz = sdf.sample(sm, (p + float3(0.0, 0.0, eps) - volMin) / ext, level(0)).x
             - sdf.sample(sm, (p - float3(0.0, 0.0, eps) - volMin) / ext, level(0)).x;
    return float3(dx, dy, dz);
}

// March from the hit point toward the light along L: return 0 when blocked, otherwise 1.
static float SdfShadow(texture3d<float> sdf, sampler sm, float3 volMin, float3 p, float3 L, float maxDist, float voxel, float ext, uint steps)
{
    float vis = 1.0;
    float3 q = p + L * voxel;
    float t = voxel;
    for (uint s = 0; s < steps; s++)
    {
        float3 uvw = (q - volMin) / ext;
        bool outside = uvw.x < 0.0 || uvw.x > 1.0 || uvw.y < 0.0 || uvw.y > 1.0 || uvw.z < 0.0 || uvw.z > 1.0;
        if (outside || t >= maxDist)
            break;
        float d = sdf.sample(sm, uvw, level(0)).x;
        if (d < voxel * 0.5)
        {
            vis = 0.0;
            break;
        }
        float adv = max(d, voxel * 0.5);
        q += L * adv;
        t += adv;
    }
    return vis;
}

// Direct irradiance E at the hit point, before division by PI.
static float3 EvalLights(const device float4* lights, texture3d<float> sdf, sampler sm, float3 volMin,
    float3 p, float3 N, float voxel, float ext, uint steps, float punctualShadow)
{
    float3 sum = float3(0.0);
    uint lcount = (uint)lights[0].x;
    for (uint i = 0; i < lcount; i++)
    {
        float4 posRange = lights[1 + i * 4];
        float4 colorIntensity = lights[1 + i * 4 + 1];
        float4 dirType = lights[1 + i * 4 + 2];
        float4 spotParams = lights[1 + i * 4 + 3];
        float3 L;
        float attenuation;
        float maxDist;
        bool wantShadow;
        if (dirType.w >= 1.5)
        {
            L = -normalize(dirType.xyz);
            attenuation = 1.0;
            maxDist = ext;
            wantShadow = true;
        }
        else
        {
            float3 toLight = posRange.xyz - p;
            float dist = length(toLight);
            L = toLight / max(dist, 0.0001);
            attenuation = 1.0 / max(dist * dist, 0.0001);
            float range = posRange.w;
            if (range > 0.0)
            {
                float win = saturate(1.0 - pow(dist / range, 4.0));
                attenuation *= win * win;
            }
            if (dirType.w > 0.5)
            {
                attenuation *= smoothstep(spotParams.y, spotParams.x, dot(-L, normalize(dirType.xyz)));
            }
            maxDist = dist;
            wantShadow = punctualShadow > 0.5;
        }
        float e = attenuation * max(dot(N, L), 0.0);
        float vis = (e > 0.00001 && wantShadow) ? SdfShadow(sdf, sm, volMin, p, L, maxDist, voxel, ext, steps) : 1.0;
        sum += colorIntensity.xyz * colorIntensity.w * e * vis;
    }
    return sum;
}

static float3 FibDir(uint i, float n)
{
    float fi = float(i);
    float phi = fi * 2.399963229728653;
    float z = 1.0 - (2.0 * fi + 1.0) / n;
    float r = sqrt(saturate(1.0 - z * z));
    return float3(cos(phi) * r, sin(phi) * r, z);
}

static float3 OctDecode(float2 f)
{
    float3 n = float3(f.x, f.y, 1.0 - abs(f.x) - abs(f.y));
    float t = saturate(-n.z);
    n.x += n.x >= 0.0 ? -t : t;
    n.y += n.y >= 0.0 ? -t : t;
    return normalize(n);
}

// Two-axis rotation driven by an R2 low-discrepancy sequence: rotate around Y then X,
// changing the ray set every frame so hysteresis fills the sphere over time.
static float3 RotateRay(float3 d, float frame)
{
    float a1 = fract(frame * 0.7548776662466927) * 6.28318530718;
    float a2 = fract(frame * 0.5698402909980532) * 6.28318530718;
    float s1 = sin(a1); float c1 = cos(a1); float s2 = sin(a2); float c2 = cos(a2);
    float3 r = float3(c1 * d.x + s1 * d.z, d.y, -s1 * d.x + c1 * d.z);
    return float3(r.x, c2 * r.y - s2 * r.z, s2 * r.y + c2 * r.z);
}

static float2 OctEncode(float3 dir)
{
    float3 a = abs(dir);
    float2 p = dir.xy / (a.x + a.y + a.z);
    if (dir.z < 0.0)
        p = (1.0 - abs(float2(p.y, p.x))) * float2(p.x >= 0.0 ? 1.0 : -1.0, p.y >= 0.0 ? 1.0 : -1.0);
    return p;
}

// Sample the previous irradiance atlas with 8-neighbor trilinear blending and back-face cosine weighting.
static float3 SampleBouncePrev(texture2d<float> prev, sampler sm, float3 gridMin, float spacing,
    float3 dims, float normalBias, float3 p, float3 N)
{
    float2 atlasSize = float2(dims.x * dims.z * 8.0, dims.y * 8.0);
    float2 oct = OctEncode(N) * 0.5 + 0.5;
    float3 wp = p + N * normalBias;
    float3 gc = (wp - gridMin) / spacing - 0.5;
    float3 base = floor(gc);
    float3 f = gc - base;
    float3 sum = float3(0.0);
    float wsum = 0.0;
    for (int i = 0; i < 8; i++)
    {
        float3 off = float3(float(i & 1), float((i >> 1) & 1), float((i >> 2) & 1));
        float3 tri = mix(1.0 - f, f, off);
        float w = tri.x * tri.y * tri.z;
        float3 pidx = clamp(base + off, float3(0.0), dims - 1.0);
        float3 probePos = gridMin + (pidx + 0.5) * spacing;
        float wdir = max(dot(normalize(probePos - p), N), 0.0);
        w *= wdir * wdir + 0.01;
        float2 tile = float2(pidx.x + pidx.z * dims.x, pidx.y);
        float2 uv = (tile * 8.0 + 1.0 + oct * 6.0) / atlasSize;
        // Step 5: attenuate continuously by validity alpha so invalid probes do not spread light
        // through bounce feedback. Continuous weighting avoids hard-gate flicker.
        float bval = prev.sample(sm, (tile * 8.0 + 4.0) / atlasSize, level(0)).a;
        w *= saturate(bval);
        sum += prev.sample(sm, uv, level(0)).rgb * w;
        wsum += w;
    }
    return wsum > 1e-6 ? sum / wsum : float3(0.0);
}

static int2 BorderMap(int tx, int ty)
{
    bool lB = tx == 0; bool rB = tx == 7; bool tB = ty == 0; bool bB = ty == 7;
    int2 src = int2(tx, ty);
    if ((lB || rB) && (tB || bB))
        src = int2(lB ? 6 : 1, tB ? 6 : 1);
    else if (lB) src = int2(1, 7 - ty);
    else if (rB) src = int2(6, 7 - ty);
    else if (tB) src = int2(7 - tx, 1);
    else if (bB) src = int2(7 - tx, 6);
    return src;
}

static int2 BorderMapD(int tx, int ty)
{
    bool lB = tx == 0; bool rB = tx == 15; bool tB = ty == 0; bool bB = ty == 15;
    int2 src = int2(tx, ty);
    if ((lB || rB) && (tB || bB))
        src = int2(lB ? 14 : 1, tB ? 14 : 1);
    else if (lB) src = int2(1, 15 - ty);
    else if (rB) src = int2(14, 15 - ty);
    else if (tB) src = int2(15 - tx, 1);
    else if (bB) src = int2(15 - tx, 14);
    return src;
}

kernel void CSMain(
    constant DdgiUpdateParams& params [[buffer(0)]],
    const device float4* uProxies [[buffer(1)]],
    const device float4* uSh [[buffer(2)]],
    const device float4* uLights [[buffer(3)]],
    texture3d<float> uSdf [[texture(0)]],
    texture2d<float, access::sample> uPrev [[texture(1)]],
    texture2d<float, access::write> uWrite [[texture(2)]],
    texture2d<float, access::read> uPrevDep [[texture(3)]],
    texture2d<float, access::write> uWriteDep [[texture(4)]],
    sampler uSampler [[sampler(0)]],
    uint gxi [[threadgroup_position_in_grid]],
    uint li [[thread_position_in_threadgroup]])
{
    threadgroup float3 gRad[128];
    threadgroup float3 gDir[128];
    threadgroup float gHit[128];
    // Step 5 validity classification: 0 = miss, 1 = front-face hit, 2 = back-face hit.
    threadgroup float gBack[128];

    uint gx = (uint)params.uGrid.x;
    uint gz = (uint)params.uGrid.z;
    uint probe = gxi;
    uint px = probe % gx;
    uint pz = (probe / gx) % gz;
    uint py = probe / (gx * gz);
    float3 probePos = params.uProbeGrid.xyz + (float3(px, py, pz) + 0.5) * params.uProbeGrid.w;
    uint tileX = px + pz * gx;
    uint tileY = py;

    uint divisor = (uint)params.uGrid.w;
    uint frame = (uint)params.uAccum.w;
    bool doTrace = divisor <= 1u || (frame % divisor) == (probe % divisor);

    uint rays = (uint)params.uTrace.z;
    float voxel = params.uVolume.w;
    float ext = params.uExtent.x;
    float farD = params.uExtent.y;
    float vHyst = params.uExtent.z;    // Step 5 validity EMA window, independent from irradiance hysteresis.
    float validityOn = params.uExtent.w; // Step 5 validity toggle: 1 = on, 0 = treat all probes as valid.
    uint maxSteps = (uint)params.uTrace.w;
    uint pcount = (uint)params.uTrace.y;

    if (doTrace && li < rays)
    {
        float3 dir = RotateRay(FibDir(li, (float)rays), params.uAccum.w);
        float3 p = probePos + dir * params.uAccum.y;
        float3 radiance = float3(0.0);
        bool hit = false;
        float back = 0.0;
        for (uint s = 0; s < maxSteps; s++)
        {
            float3 uvw = (p - params.uVolume.xyz) / ext;
            if (uvw.x < 0.0 || uvw.x > 1.0 || uvw.y < 0.0 || uvw.y > 1.0 || uvw.z < 0.0 || uvw.z > 1.0)
                break;
            float d = uSdf.sample(uSampler, uvw, level(0)).x;
            // Step 5 start-point clamp: if the biased origin lands inside geometry, retry from probePos.
            // This preserves valid near-wall rows while still rejecting truly embedded probes.
            if (s == 0u && d < 0.0)
            {
                p = probePos;
                uvw = (p - params.uVolume.xyz) / ext;
                if (uvw.x < 0.0 || uvw.x > 1.0 || uvw.y < 0.0 || uvw.y > 1.0 || uvw.z < 0.0 || uvw.z > 1.0)
                    break;
                d = uSdf.sample(uSampler, uvw, level(0)).x;
            }
            if (d < voxel)
            {
                float best = farD;
                float3 alb = float3(0.0);
                float3 emi = float3(0.0);
                for (uint j = 0; j < pcount; j++)
                {
                    float4 c = uProxies[j * 4];
                    float4 er = uProxies[j * 4 + 1];
                    float pd = ProxySdf(p, c, er);
                    if (pd < best)
                    {
                        best = pd;
                        alb = uProxies[j * 4 + 2].rgb;
                        emi = uProxies[j * 4 + 3].rgb;
                    }
                }
                float3 n = SdfNormal(uSdf, uSampler, params.uVolume.xyz, ext, p, voxel);
                n = dot(n, n) > 1e-16 ? normalize(n) : -dir;
                // Front-face-only hit test in SDF space: d < 0 means the ray is already inside solid geometry,
                // so treat it as a back-face hit. Do not switch back to a gradient-dot-ray test here.
                back = d < 0.0 ? 2.0 : 1.0;
                float3 e = EvalLights(uLights, uSdf, uSampler, params.uVolume.xyz, p, n, voxel, ext, (uint)params.uShade.x, params.uShade.z);
                float3 bounce = params.uShade.y > 0.0
                    ? SampleBouncePrev(uPrev, uSampler, params.uProbeGrid.xyz, params.uProbeGrid.w, params.uGrid.xyz, params.uAccum.y, p, n) * params.uShade.y
                    : float3(0.0);
                radiance = emi + alb * (e * 0.3183098861837907 + bounce);
                hit = true;
                break;
            }
            p += dir * max(d, voxel * 0.5);
        }
        if (!hit)
            radiance = max(EvalSh(uSh, dir), float3(0.0)) * params.uAccum.z;
        gRad[li] = radiance;
        gDir[li] = dir;
        gHit[li] = hit ? length(p - probePos) : ext;
        gBack[li] = back;
    }
    threadgroup_barrier(mem_flags::mem_threadgroup);

    // Step 5 validity classification (Majercik 2019 §3.3, clause 13): map the back-face ratio to a
    // continuous valid value in [0, 1] with a linear ramp between 0.5 * thr and thr.
    float hits = 0.0;
    float backs = 0.0;
    for (uint vr = 0; vr < rays; vr++)
    {
        hits += step(0.5, gBack[vr]);
        backs += step(1.5, gBack[vr]);
    }
    float ratio = hits > 0.0 ? backs / hits : 0.0;
    float lo = params.uShade.w * 0.5;
    // Toggle (params.uExtent.w, clause 13): when disabled, treat all probes as valid and write alpha = 1.
    float valid = validityOn > 0.5 ? 1.0 - saturate((ratio - lo) / max(params.uShade.w - lo, 1e-4)) : 1.0;

    // The workgroup has 128 threads while the irradiance tile has only 64 texels:
    // li < 64 writes irradiance, and all 128 threads cover the depth-moment tile.
    if (li < 64u)
    {
        int tx = (int)(li % 8u);
        int ty = (int)(li / 8u);
        uint2 ac = uint2(tileX * 8u + (uint)tx, tileY * 8u + (uint)ty);
        float4 prevFull = uPrev.read(ac);
        float3 prev = prevFull.rgb;
        float3 res = prev;
        float va = prevFull.a;
        if (doTrace)
        {
            int2 src = BorderMap(tx, ty);
            float2 uv = ((float2(src.x - 1, src.y - 1) + 0.5) / 6.0) * 2.0 - 1.0;
            float3 tdir = OctDecode(uv);
            float3 sum = float3(0.0);
            float wsum = 0.0;
            for (uint r = 0; r < rays; r++)
            {
                float wgt = max(dot(gDir[r], tdir), 0.0);
                sum += gRad[r] * wgt;
                wsum += wgt;
            }
            float3 irr = wsum > 1e-6 ? sum / wsum : float3(0.0);
            res = mix(irr, prev, params.uAccum.x);
        }
        // Alpha stores probe validity. Use a dedicated EMA window from params.uExtent.z so classification
        // stays more stable than irradiance, write 1 immediately when validity is disabled, and forward
        // previous alpha on frames that skip tracing.
        uWrite.write(float4(res, validityOn > 0.5 ? (doTrace ? mix(valid, va, vHyst) : va) : 1.0), ac);
    }

    // Depth-moment integration over a 16x16 tile with a 14x14 core; 128 threads cover the full 256 texels.
    for (uint dti = li; dti < 256u; dti += 128u)
    {
        int dtx = (int)(dti % 16u);
        int dty = (int)(dti / 16u);
        uint2 dac = uint2(tileX * 16u + (uint)dtx, tileY * 16u + (uint)dty);
        float2 prevDep = uPrevDep.read(dac).xy;
        float2 resDep = prevDep;
        if (doTrace)
        {
            int2 dsrc = BorderMapD(dtx, dty);
            float2 duv = ((float2(dsrc.x - 1, dsrc.y - 1) + 0.5) / 14.0) * 2.0 - 1.0;
            float3 dtdir = OctDecode(duv);
            float2 dsum = float2(0.0);
            float dwsum = 0.0;
            for (uint r = 0; r < rays; r++)
            {
                float dwgt = pow(max(dot(gDir[r], dtdir), 0.0), 16.0);
                dsum += float2(gHit[r], gHit[r] * gHit[r]) * dwgt;
                dwsum += dwgt;
            }
            float2 dep = dwsum > 1e-6 ? dsum / dwsum : float2(0.0);
            resDep = mix(dep, prevDep, params.uAccum.x);
        }
        uWriteDep.write(float4(resDep, 0.0, 0.0), dac);
    }
}
";

    // ── WebGPU WGSL: @binding(i) follows declaration order, the engine sampler stays at @binding(15),
    //    workgroup arrays live at module scope, barriers stay in unconditional uniform control flow,
    //    trace uses textureSampleLevel with explicit LOD, and ternary selection is expressed with select(). ──
    const string SourceUpdateWgsl = @"
struct DdgiUpdateParams
{
    uProbeGrid : vec4<f32>,
    uVolume : vec4<f32>,
    uTrace : vec4<f32>,
    uAccum : vec4<f32>,
    uGrid : vec4<f32>,
    uExtent : vec4<f32>,
    uShade : vec4<f32>,
};

@group(0) @binding(0) var<uniform> params : DdgiUpdateParams;
@group(0) @binding(1) var<storage, read> uProxies : array<vec4<f32>>;
@group(0) @binding(2) var uSdf : texture_3d<f32>;
@group(0) @binding(3) var<storage, read> uSh : array<vec4<f32>>;
@group(0) @binding(4) var uPrev : texture_2d<f32>;
@group(0) @binding(5) var uWrite : texture_storage_2d<rgba16float, write>;
@group(0) @binding(6) var uPrevDep : texture_2d<f32>;
@group(0) @binding(7) var uWriteDep : texture_storage_2d<rgba16float, write>;
@group(0) @binding(8) var<storage, read> uLights : array<vec4<f32>>;
@group(0) @binding(15) var uSampler : sampler;

var<workgroup> gRad : array<vec3<f32>, 128>;
var<workgroup> gDir : array<vec3<f32>, 128>;
var<workgroup> gHit : array<f32, 128>;
// Step 5 validity classification: 0 = miss, 1 = front-face hit, 2 = back-face hit.
var<workgroup> gBack : array<f32, 128>;

fn ProxySdf(p : vec3<f32>, c : vec4<f32>, er : vec4<f32>) -> f32
{
    let q = abs(p - c.xyz) - er.xyz;
    return length(max(q, vec3<f32>(0.0))) + min(max(q.x, max(q.y, q.z)), 0.0) - er.w;
}

fn EvalSh(dir : vec3<f32>) -> vec3<f32>
{
    var r = uSh[0].rgb;
    r = r + uSh[1].rgb * dir.y;
    r = r + uSh[2].rgb * dir.z;
    r = r + uSh[3].rgb * dir.x;
    r = r + uSh[4].rgb * (dir.x * dir.y);
    r = r + uSh[5].rgb * (dir.y * dir.z);
    r = r + uSh[6].rgb * (3.0 * dir.z * dir.z - 1.0);
    r = r + uSh[7].rgb * (dir.x * dir.z);
    r = r + uSh[8].rgb * (dir.x * dir.x - dir.y * dir.y);
    return r;
}

fn SdfNormal(p : vec3<f32>, eps : f32) -> vec3<f32>
{
    let o = params.uVolume.xyz;
    let e = params.uExtent.x;
    let dx = textureSampleLevel(uSdf, uSampler, (p + vec3<f32>(eps, 0.0, 0.0) - o) / e, 0.0).x
           - textureSampleLevel(uSdf, uSampler, (p - vec3<f32>(eps, 0.0, 0.0) - o) / e, 0.0).x;
    let dy = textureSampleLevel(uSdf, uSampler, (p + vec3<f32>(0.0, eps, 0.0) - o) / e, 0.0).x
           - textureSampleLevel(uSdf, uSampler, (p - vec3<f32>(0.0, eps, 0.0) - o) / e, 0.0).x;
    let dz = textureSampleLevel(uSdf, uSampler, (p + vec3<f32>(0.0, 0.0, eps) - o) / e, 0.0).x
           - textureSampleLevel(uSdf, uSampler, (p - vec3<f32>(0.0, 0.0, eps) - o) / e, 0.0).x;
    return vec3<f32>(dx, dy, dz);
}

// March from the hit point toward the light along L: return 0 when blocked, otherwise 1.
fn SdfShadow(p : vec3<f32>, L : vec3<f32>, maxDist : f32, voxel : f32, ext : f32, steps : u32) -> f32
{
    var vis = 1.0;
    var q = p + L * voxel;
    var t = voxel;
    for (var s : u32 = 0u; s < steps; s = s + 1u)
    {
        let uvw = (q - params.uVolume.xyz) / ext;
        let outside = uvw.x < 0.0 || uvw.x > 1.0 || uvw.y < 0.0 || uvw.y > 1.0 || uvw.z < 0.0 || uvw.z > 1.0;
        if (outside || t >= maxDist)
        {
            break;
        }
        let d = textureSampleLevel(uSdf, uSampler, uvw, 0.0).x;
        if (d < voxel * 0.5)
        {
            vis = 0.0;
            break;
        }
        let adv = max(d, voxel * 0.5);
        q = q + L * adv;
        t = t + adv;
    }
    return vis;
}

// Direct irradiance E at the hit point, before division by PI.
fn EvalLights(p : vec3<f32>, N : vec3<f32>, voxel : f32, ext : f32, steps : u32, punctualShadow : f32) -> vec3<f32>
{
    var sum = vec3<f32>(0.0);
    let lcount = u32(uLights[0].x);
    for (var i : u32 = 0u; i < lcount; i = i + 1u)
    {
        let posRange = uLights[1u + i * 4u];
        let colorIntensity = uLights[1u + i * 4u + 1u];
        let dirType = uLights[1u + i * 4u + 2u];
        let spotParams = uLights[1u + i * 4u + 3u];
        var L = vec3<f32>(0.0, 1.0, 0.0);
        var attenuation = 1.0;
        var maxDist = ext;
        var wantShadow = true;
        if (dirType.w >= 1.5)
        {
            L = -normalize(dirType.xyz);
        }
        else
        {
            let toLight = posRange.xyz - p;
            let dist = length(toLight);
            L = toLight / max(dist, 0.0001);
            attenuation = 1.0 / max(dist * dist, 0.0001);
            let range = posRange.w;
            if (range > 0.0)
            {
                let win = clamp(1.0 - pow(dist / range, 4.0), 0.0, 1.0);
                attenuation = attenuation * win * win;
            }
            if (dirType.w > 0.5)
            {
                attenuation = attenuation * smoothstep(spotParams.y, spotParams.x, dot(-L, normalize(dirType.xyz)));
            }
            maxDist = dist;
            wantShadow = punctualShadow > 0.5;
        }
        let e = attenuation * max(dot(N, L), 0.0);
        var vis = 1.0;
        if (e > 0.00001 && wantShadow)
        {
            vis = SdfShadow(p, L, maxDist, voxel, ext, steps);
        }
        sum = sum + colorIntensity.xyz * colorIntensity.w * e * vis;
    }
    return sum;
}

fn FibDir(i : u32, n : f32) -> vec3<f32>
{
    let fi = f32(i);
    let phi = fi * 2.399963229728653;
    let z = 1.0 - (2.0 * fi + 1.0) / n;
    let r = sqrt(clamp(1.0 - z * z, 0.0, 1.0));
    return vec3<f32>(cos(phi) * r, sin(phi) * r, z);
}

fn OctDecode(f : vec2<f32>) -> vec3<f32>
{
    var n = vec3<f32>(f.x, f.y, 1.0 - abs(f.x) - abs(f.y));
    let t = clamp(-n.z, 0.0, 1.0);
    n.x = n.x + select(t, -t, n.x >= 0.0);
    n.y = n.y + select(t, -t, n.y >= 0.0);
    return normalize(n);
}

// Two-axis rotation driven by an R2 low-discrepancy sequence: rotate around Y then X,
// changing the ray set every frame so hysteresis fills the sphere over time.
fn RotateRay(d : vec3<f32>, frame : f32) -> vec3<f32>
{
    let a1 = fract(frame * 0.7548776662466927) * 6.28318530718;
    let a2 = fract(frame * 0.5698402909980532) * 6.28318530718;
    let s1 = sin(a1);
    let c1 = cos(a1);
    let s2 = sin(a2);
    let c2 = cos(a2);
    let r = vec3<f32>(c1 * d.x + s1 * d.z, d.y, -s1 * d.x + c1 * d.z);
    return vec3<f32>(r.x, c2 * r.y - s2 * r.z, s2 * r.y + c2 * r.z);
}

fn OctEncode(dir : vec3<f32>) -> vec2<f32>
{
    let a = abs(dir);
    var p = dir.xy / (a.x + a.y + a.z);
    if (dir.z < 0.0)
    {
        let s = vec2<f32>(select(-1.0, 1.0, p.x >= 0.0), select(-1.0, 1.0, p.y >= 0.0));
        p = (vec2<f32>(1.0) - abs(vec2<f32>(p.y, p.x))) * s;
    }
    return p;
}

// Sample the previous irradiance atlas with 8-neighbor trilinear blending and back-face cosine weighting.
fn SampleBouncePrev(p : vec3<f32>, N : vec3<f32>) -> vec3<f32>
{
    let gridMin = params.uProbeGrid.xyz;
    let spacing = params.uProbeGrid.w;
    let dims = params.uGrid.xyz;
    let atlasSize = vec2<f32>(dims.x * dims.z * 8.0, dims.y * 8.0);
    let oct = OctEncode(N) * 0.5 + 0.5;
    let wp = p + N * params.uAccum.y;
    let gc = (wp - gridMin) / spacing - vec3<f32>(0.5);
    let base = floor(gc);
    let f = gc - base;
    var sum = vec3<f32>(0.0);
    var wsum = 0.0;
    for (var i : i32 = 0; i < 8; i = i + 1)
    {
        let off = vec3<f32>(f32(i & 1), f32((i >> 1) & 1), f32((i >> 2) & 1));
        let tri = mix(vec3<f32>(1.0) - f, f, off);
        var w = tri.x * tri.y * tri.z;
        let pidx = clamp(base + off, vec3<f32>(0.0), dims - vec3<f32>(1.0));
        let probePos = gridMin + (pidx + vec3<f32>(0.5)) * spacing;
        let wdir = max(dot(normalize(probePos - p), N), 0.0);
        w = w * (wdir * wdir + 0.01);
        let tile = vec2<f32>(pidx.x + pidx.z * dims.x, pidx.y);
        let uv = (tile * 8.0 + vec2<f32>(1.0) + oct * 6.0) / atlasSize;
        // Step 5: attenuate continuously by validity alpha so invalid probes do not spread light
        // through bounce feedback. Continuous weighting avoids hard-gate flicker.
        let bval = textureSampleLevel(uPrev, uSampler, (tile * 8.0 + vec2<f32>(4.0)) / atlasSize, 0.0).a;
        w = w * clamp(bval, 0.0, 1.0);
        sum = sum + textureSampleLevel(uPrev, uSampler, uv, 0.0).rgb * w;
        wsum = wsum + w;
    }
    return select(vec3<f32>(0.0), sum / max(wsum, 1e-6), wsum > 1e-6);
}

fn BorderMap(tx : i32, ty : i32) -> vec2<i32>
{
    let lB = tx == 0;
    let rB = tx == 7;
    let tB = ty == 0;
    let bB = ty == 7;
    var src = vec2<i32>(tx, ty);
    if ((lB || rB) && (tB || bB))
    {
        src = vec2<i32>(select(1, 6, lB), select(1, 6, tB));
    }
    else if (lB)
    {
        src = vec2<i32>(1, 7 - ty);
    }
    else if (rB)
    {
        src = vec2<i32>(6, 7 - ty);
    }
    else if (tB)
    {
        src = vec2<i32>(7 - tx, 1);
    }
    else if (bB)
    {
        src = vec2<i32>(7 - tx, 6);
    }
    return src;
}

fn BorderMapD(tx : i32, ty : i32) -> vec2<i32>
{
    let lB = tx == 0;
    let rB = tx == 15;
    let tB = ty == 0;
    let bB = ty == 15;
    var src = vec2<i32>(tx, ty);
    if ((lB || rB) && (tB || bB))
    {
        src = vec2<i32>(select(1, 14, lB), select(1, 14, tB));
    }
    else if (lB)
    {
        src = vec2<i32>(1, 15 - ty);
    }
    else if (rB)
    {
        src = vec2<i32>(14, 15 - ty);
    }
    else if (tB)
    {
        src = vec2<i32>(15 - tx, 1);
    }
    else if (bB)
    {
        src = vec2<i32>(15 - tx, 14);
    }
    return src;
}

@compute @workgroup_size(128, 1, 1)
fn CSMain(@builtin(workgroup_id) gid : vec3<u32>, @builtin(local_invocation_index) li : u32)
{
    let gx = u32(params.uGrid.x);
    let gz = u32(params.uGrid.z);
    let probe = gid.x;
    let px = probe % gx;
    let pz = (probe / gx) % gz;
    let py = probe / (gx * gz);
    let probePos = params.uProbeGrid.xyz + (vec3<f32>(f32(px), f32(py), f32(pz)) + vec3<f32>(0.5)) * params.uProbeGrid.w;
    let tileX = px + pz * gx;
    let tileY = py;

    let divisor = u32(params.uGrid.w);
    let frame = u32(params.uAccum.w);
    let doTrace = divisor <= 1u || (frame % divisor) == (probe % divisor);

    let rays = u32(params.uTrace.z);
    let voxel = params.uVolume.w;
    let ext = params.uExtent.x;
    let farD = params.uExtent.y;
    let vHyst = params.uExtent.z;      // Step 5 validity EMA window, independent from irradiance hysteresis.
    let validityOn = params.uExtent.w; // Step 5 validity toggle: 1 = on, 0 = treat all probes as valid.
    let maxSteps = u32(params.uTrace.w);
    let pcount = u32(params.uTrace.y);

    if (doTrace && li < rays)
    {
        let dir = RotateRay(FibDir(li, f32(rays)), params.uAccum.w);
        var p = probePos + dir * params.uAccum.y;
        var radiance = vec3<f32>(0.0);
        var hit = false;
        var back = 0.0;
        for (var s : u32 = 0u; s < maxSteps; s = s + 1u)
        {
            var uvw = (p - params.uVolume.xyz) / ext;
            if (uvw.x < 0.0 || uvw.x > 1.0 || uvw.y < 0.0 || uvw.y > 1.0 || uvw.z < 0.0 || uvw.z > 1.0)
            {
                break;
            }
            var d = textureSampleLevel(uSdf, uSampler, uvw, 0.0).x;
            // Step 5 start-point clamp: if the biased origin lands inside geometry, retry from probePos.
            // This preserves valid near-wall rows while still rejecting truly embedded probes.
            if (s == 0u && d < 0.0)
            {
                p = probePos;
                uvw = (p - params.uVolume.xyz) / ext;
                if (uvw.x < 0.0 || uvw.x > 1.0 || uvw.y < 0.0 || uvw.y > 1.0 || uvw.z < 0.0 || uvw.z > 1.0)
                {
                    break;
                }
                d = textureSampleLevel(uSdf, uSampler, uvw, 0.0).x;
            }
            if (d < voxel)
            {
                var best = farD;
                var alb = vec3<f32>(0.0);
                var emi = vec3<f32>(0.0);
                for (var j : u32 = 0u; j < pcount; j = j + 1u)
                {
                    let c = uProxies[j * 4u];
                    let er = uProxies[j * 4u + 1u];
                    let pd = ProxySdf(p, c, er);
                    if (pd < best)
                    {
                        best = pd;
                        alb = uProxies[j * 4u + 2u].rgb;
                        emi = uProxies[j * 4u + 3u].rgb;
                    }
                }
                let gn = SdfNormal(p, voxel);
                let n = select(-dir, normalize(gn), dot(gn, gn) > 1e-16);
                // Front-face-only hit test in SDF space: d < 0 means the ray is already inside solid geometry,
                // so treat it as a back-face hit. Do not switch back to a gradient-dot-ray test here.
                back = select(1.0, 2.0, d < 0.0);
                let e = EvalLights(p, n, voxel, ext, u32(params.uShade.x), params.uShade.z);
                var bounce = vec3<f32>(0.0);
                if (params.uShade.y > 0.0)
                {
                    bounce = SampleBouncePrev(p, n) * params.uShade.y;
                }
                radiance = emi + alb * (e * 0.3183098861837907 + bounce);
                hit = true;
                break;
            }
            p = p + dir * max(d, voxel * 0.5);
        }
        if (!hit)
        {
            radiance = max(EvalSh(dir), vec3<f32>(0.0)) * params.uAccum.z;
        }
        gRad[li] = radiance;
        gDir[li] = dir;
        gHit[li] = select(ext, length(p - probePos), hit);
        gBack[li] = back;
    }
    workgroupBarrier();

    // Step 5 validity classification (Majercik 2019 §3.3, clause 13): map the back-face ratio to a
    // continuous valid value in [0, 1] with a linear ramp between 0.5 * thr and thr.
    var hits = 0.0;
    var backs = 0.0;
    for (var vr : u32 = 0u; vr < rays; vr = vr + 1u)
    {
        hits = hits + step(0.5, gBack[vr]);
        backs = backs + step(1.5, gBack[vr]);
    }
    var ratio = 0.0;
    if (hits > 0.0)
    {
        ratio = backs / hits;
    }
    let lo = params.uShade.w * 0.5;
    // Toggle (params.uExtent.w, clause 13): when disabled, treat all probes as valid and write alpha = 1.
    let valid = select(1.0, 1.0 - clamp((ratio - lo) / max(params.uShade.w - lo, 1e-4), 0.0, 1.0), validityOn > 0.5);

    // The workgroup has 128 threads while the irradiance tile has only 64 texels:
    // li < 64 writes irradiance, and all 128 threads cover the depth-moment tile.
    if (li < 64u)
    {
        let tx = i32(li % 8u);
        let ty = i32(li / 8u);
        let ac = vec2<i32>(i32(tileX * 8u) + tx, i32(tileY * 8u) + ty);
        let prevFull = textureLoad(uPrev, ac, 0);
        let prev = prevFull.rgb;
        var res = prev;
        let va = prevFull.a;
        if (doTrace)
        {
            let src = BorderMap(tx, ty);
            let uv = ((vec2<f32>(f32(src.x - 1), f32(src.y - 1)) + vec2<f32>(0.5)) / 6.0) * 2.0 - vec2<f32>(1.0);
            let tdir = OctDecode(uv);
            var sum = vec3<f32>(0.0);
            var wsum = 0.0;
            for (var r : u32 = 0u; r < rays; r = r + 1u)
            {
                let wgt = max(dot(gDir[r], tdir), 0.0);
                sum = sum + gRad[r] * wgt;
                wsum = wsum + wgt;
            }
            var irr = vec3<f32>(0.0);
            if (wsum > 1e-6)
            {
                irr = sum / wsum;
            }
            res = mix(irr, prev, params.uAccum.x);
        }
        // Alpha stores probe validity. Use a dedicated EMA window from params.uExtent.z so classification
        // stays more stable than irradiance, write 1 immediately when validity is disabled, and forward
        // previous alpha on frames that skip tracing.
        textureStore(uWrite, ac, vec4<f32>(res, select(1.0, select(va, mix(valid, va, vHyst), doTrace), validityOn > 0.5)));
    }

    // Depth-moment integration over a 16x16 tile with a 14x14 core; 128 threads cover the full 256 texels.
    for (var dti : u32 = li; dti < 256u; dti = dti + 128u)
    {
        let dtx = i32(dti % 16u);
        let dty = i32(dti / 16u);
        let dac = vec2<i32>(i32(tileX * 16u) + dtx, i32(tileY * 16u) + dty);
        let prevDep = textureLoad(uPrevDep, dac, 0).xy;
        var resDep = prevDep;
        if (doTrace)
        {
            let dsrc = BorderMapD(dtx, dty);
            let duv = ((vec2<f32>(f32(dsrc.x - 1), f32(dsrc.y - 1)) + vec2<f32>(0.5)) / 14.0) * 2.0 - vec2<f32>(1.0);
            let dtdir = OctDecode(duv);
            var dsum = vec2<f32>(0.0);
            var dwsum = 0.0;
            for (var r : u32 = 0u; r < rays; r = r + 1u)
            {
                let dwgt = pow(max(dot(gDir[r], dtdir), 0.0), 16.0);
                dsum = dsum + vec2<f32>(gHit[r], gHit[r] * gHit[r]) * dwgt;
                dwsum = dwsum + dwgt;
            }
            var dep = vec2<f32>(0.0);
            if (dwsum > 1e-6)
            {
                dep = dsum / dwsum;
            }
            resDep = mix(dep, prevDep, params.uAccum.x);
        }
        textureStore(uWriteDep, dac, vec4<f32>(resDep, 0.0, 0.0));
    }
}
";
}
