// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

/// <summary>近地体积雾：先过滤局部 Q/sigma_t，再积分；累计体 RGB=L、A=T。</summary>
public sealed partial class VolumetricFogEffect : ComputeEffect, IDisposable
{
    // 帧缓冲只含 float4，矩阵按 System.Numerics 原始行序存放；不复用表面光照 UBO。
    // 0..3 inverseVP，4..7 previousVP，8 camera/D，9 forward/g，10 grid/history，
    // 11 density/height/falloff/noise，12 albedo/scale，13 wind/count，14 environment/PCF，
    // 15 previousCamera/valid，16 previousForward/cloudReady，18..21 inverseJitteredVP，22..25 currentVP，
    // 32..117 最终 SceneLightParams，128..383 最多 32 个局部体，每体 8 个 float4。
    public const int FrameVectorCount = 384;
    public const int LightingOffset = 32;
    public const int VolumeOffset = 128;
    public const int VolumeStride = 8;
    public const string NoiseTexture = "compute3d://fog/noise";
    public const string InjectionTexture = "compute3d://fog/injection";
    public const string IntegratedTexture = "compute3d://fog/integrated";
    static readonly string[] HistoryTextures = { "compute3d://fog/history0", "compute3d://fog/history1" };
    static readonly string[] OwnedTextures = { NoiseTexture, InjectionTexture, HistoryTextures[0], HistoryTextures[1], IntegratedTexture };

    // FrameSchedule 和纹理名属于同一渲染设备域，只允许一个资源所有者。
    static VolumetricFogEffect? _active;
    readonly Vector4[] _frame = new Vector4[FrameVectorCount];
    readonly ComputeResourceRef[] _noiseResources = { NoiseTexture };
    readonly ComputeResourceRef[] _injectResources = new ComputeResourceRef[5];
    readonly ComputeResourceRef[] _filterResources = new ComputeResourceRef[4];
    readonly ComputeResourceRef[] _integrateResources = new ComputeResourceRef[3];
    IGraphics? _graphics;
    StorageBuffer? _frameBuffer;
    FogCompositePipeline? _composite;
    RenderTarget? _compositeTarget;
    ComputeKernel? _noise, _inject, _filter, _integrate;
    SceneFog? _scene;
    SceneLightParams _previousLights;
    Matrix4x4 _previousVp, _previousProjection;
    Vector3 _previousPosition, _previousForward;
    uint _revision;
    int _ping, _width, _height;
    bool _historyValid, _noiseReady, _allocated;
    FogQuality _quality;

    public override string Name => "volumetricFog";
    public override ComputePhase Phase => ComputePhase.AfterOpaque;
    public (int Width, int Height, int Slices) Grid { get; private set; }
    public string? FilteredTexture { get; private set; }
    public StorageBuffer? SamplingBuffer => _frameBuffer;
    public bool Ready { get; private set; }
    public float HistoryWeight { get; private set; }

    public override bool Initialize(IGraphics g)
    {
        if (_graphics != null) throw new InvalidOperationException("雾效果已经初始化。");
        var color = FrameSchedule.SceneColor;
        _quality = RenderQuality.Current.VolumetricFog;
        if (_active != null || FrameSchedule.FogCompositeTarget != null || FrameSchedule.RenderFogComposite != null
            || FrameSchedule.PrepareFogComposite != null || g is not ISegmentedSceneGraphics
            || !RenderQuality.Current.RequiresFogSceneDepth(color?.Desc.ColorFormat == RtFormat.Rgba16Float,
                g.ComputeSupported, Immediate2DMode.Enabled)
            || color == null || color.Desc.SampleCount > 1 || FrameSchedule.SceneDepth == null) return false;
        _active = this;
        _graphics = g;
        try
        {
            _composite = g.CreateFogCompositePipeline();
            if (_composite == null) { Dispose(); return false; }
            _noise = g.CreateComputeKernel(CreateNoiseKernel());
            _inject = g.CreateComputeKernel(CreateInjectionKernel());
            _filter = g.CreateComputeKernel(CreateFilterKernel());
            _integrate = g.CreateComputeKernel(CreateIntegrationKernel());
            if (_noise == null || _inject == null || _filter == null || _integrate == null)
            { Dispose(); return false; }
            _frameBuffer = g.CreateStorageBuffer(FrameVectorCount * 16);
            _injectResources[0] = _filterResources[0] = _integrateResources[0] = _frameBuffer;
            _injectResources[1] = NoiseTexture;
            _injectResources[4] = InjectionTexture;
            _filterResources[1] = InjectionTexture;
            _integrateResources[2] = IntegratedTexture;
            if (!Allocate(g)) { Dispose(); return false; }
            FrameSchedule.PrepareFogComposite = PrepareComposite;
            FrameSchedule.RenderFogComposite = DrawComposite;
            return true;
        }
        catch (Exception ex)
        {
            Dispose();
            DeviceServices.BaseApp.AddLog(LogType.Error, $"[VolumetricFog] 初始化失败：{ex.Message}");
            return false;
        }
    }

    bool Allocate(IGraphics g)
    {
        _allocated = false;
        ResetOutput();
        var color = FrameSchedule.SceneColor;
        _width = checked((int)(color?.Desc.Width ?? 0));
        _height = checked((int)(color?.Desc.Height ?? 0));
        Grid = FogMath.GridSize(_width, _height, _quality);
        if (ReferenceEquals(FrameSchedule.FogCompositeTarget, _compositeTarget)) FrameSchedule.FogCompositeTarget = null;
        if (_compositeTarget != null) FrameSchedule.ForgetSceneComposite(_compositeTarget);
        if (Grid.Width == 0) return false;
        bool valid = g.TryCreateComputeTexture3D(NoiseTexture, 64, 64, 64, ComputeStorageFormat.Rgba8Unorm);
        foreach (string name in OwnedTextures)
        {
            if (name == NoiseTexture) continue;
            valid &= g.TryCreateComputeTexture3D(name, (uint)Grid.Width, (uint)Grid.Height,
                (uint)(Grid.Slices + (name == IntegratedTexture ? 1 : 0)), ComputeStorageFormat.Rgba16Float);
        }
        if (valid && (_compositeTarget == null || _compositeTarget.Desc.Width != _width || _compositeTarget.Desc.Height != _height))
        {
            var replacement = g.CreateRenderTarget(new RenderTargetDesc {
                ColorFormat = RtFormat.Rgba16Float, DepthFormat = RtFormat.None, SampleCount = 1, NoImplicitDepth = true,
                Width = (uint)_width, Height = (uint)_height, MatchBackbufferSize = color!.Desc.MatchBackbufferSize,
                ClearColor = Vector4.Zero });
            _compositeTarget?.Dispose();
            _compositeTarget = replacement;
        }
        _allocated = valid && _compositeTarget != null;
        if (_allocated) FrameSchedule.FogCompositeTarget = _compositeTarget;
        return _allocated;
    }

    public override void OnResize(IGraphics g)
    {
        if (!ReferenceEquals(_graphics, g)) return;
        try { Allocate(g); }
        catch (Exception ex)
        {
            _allocated = false;
            ResetOutput();
            DeviceServices.BaseApp.AddLog(LogType.Error, $"[VolumetricFog] Resize 失败：{ex.Message}");
        }
    }

    void ResetOutput()
    {
        Ready = false;
        FilteredTexture = null;
        if (ReferenceEquals(_active, this)) FrameSchedule.FogOutputReady = false;
        _historyValid = false;
        HistoryWeight = 0;
    }

    public override void Record(IGraphics g)
    {
        Ready = false;
        FilteredTexture = null;
        if (!ReferenceEquals(_active, this)) return;
        FrameSchedule.FogOutputReady = false;
        var app = DeviceServices.BaseApp;
        var fog = app.Fog;
        if (!_allocated || !ReferenceEquals(g, _graphics) || !FrameSchedule.SceneSegmented
            || !fog.Enabled || RenderQuality.Current.VolumetricFog != _quality
            || FrameSchedule.SceneDepth == null || FrameSchedule.SceneColor == null
            || !FrameSchedule.LightingSnapshotValid
            || FrameSchedule.SceneColor.Desc.Width != _width || FrameSchedule.SceneColor.Desc.Height != _height
            || FrameSchedule.SceneDepth.Desc.Width != _width || FrameSchedule.SceneDepth.Desc.Height != _height)
        { ResetOutput(); return; }
        var vp = FrameSchedule.UnjitteredRenderViewProjection;
        var view = FrameSchedule.RenderView;
        if (!Matrix4x4.Invert(vp, out var inverse) || !Matrix4x4.Invert(view, out var inverseView)
            || !Matrix4x4.Invert(view * FrameSchedule.RenderProjection, out var inverseJittered))
        { ResetOutput(); return; }
        var position = inverseView.Translation;
        var forward = Vector3.Normalize(new Vector3(inverseView.M31, inverseView.M32, inverseView.M33));
        var projection = inverseView * vp;
        ref readonly var lights = ref FrameSchedule.LightingSnapshot;
        bool reset = !_historyValid || !ReferenceEquals(_scene, fog) || _revision != fog.HistoryRevision
            || Vector3.Distance(position, _previousPosition) > 2f || Vector3.Dot(forward, _previousForward) < 0.8f
            || !ProjectionMatches(projection, _previousProjection) || LightsChanged(lights, _previousLights);
        float requested = RenderQuality.Current.FogHistoryWeight;
        HistoryWeight = reset ? 0 : Math.Clamp(float.IsFinite(requested) ? requested : 0, 0, 0.98f)
            / (1f + fog.WindVelocity.Length() * 0.1f);
        PackFrame(_frame, fog, lights, Grid, inverse, inverseJittered, _previousVp, position, forward,
            _previousPosition, _previousForward, HistoryWeight, !reset, RenderQuality.Current.FogShadowSamples);
        // 无图集时用场景深度满足绑定形状，同时清掉所有几何阴影开关。
        if (FrameSchedule.ShadowMap == null)
        {
            _frame[LightingOffset + 56].X = 0;
            _frame[LightingOffset + 57].X = 0;
        }
        _injectResources[2] = FrameSchedule.ShadowMap ?? FrameSchedule.SceneDepth;
        _injectResources[3] = FrameSchedule.CloudNoiseTexture is string cloud
            ? new ComputeResourceRef { TextureName = cloud } : new ComputeResourceRef { Target = FrameSchedule.SceneColor };
        if (!g.TryUpdateStorageBuffer(_frameBuffer!, MemoryMarshal.AsBytes(_frame.AsSpan())))
        { ResetOutput(); return; }
        int write = _ping ^ 1;
        _filterResources[2] = HistoryTextures[_ping];
        _filterResources[3] = HistoryTextures[write];
        _integrateResources[1] = HistoryTextures[write];
        if (!_noiseReady)
        {
            _noiseReady = Dispatch(g, _noise!, _noiseResources, 16, 16, 16);
            if (!_noiseReady) { ResetOutput(); return; }
        }
        if (!Dispatch(g, _inject!, _injectResources, (Grid.Width + 3) / 4, (Grid.Height + 3) / 4, (Grid.Slices + 3) / 4)
            || !Dispatch(g, _filter!, _filterResources, (Grid.Width + 3) / 4, (Grid.Height + 3) / 4, (Grid.Slices + 3) / 4)
            || !Dispatch(g, _integrate!, _integrateResources, (Grid.Width + 7) / 8, (Grid.Height + 7) / 8, 1))
        { ResetOutput(); return; }
        _ping = write;
        _historyValid = true;
        _scene = fog; _revision = fog.HistoryRevision;
        _previousPosition = position; _previousForward = forward;
        _previousVp = vp; _previousProjection = projection; _previousLights = lights;
        FilteredTexture = HistoryTextures[write];
        Ready = FrameSchedule.FogOutputReady = true;
    }

    bool PrepareComposite(IGraphics g)
    {
        if (!Ready || !ReferenceEquals(g, _graphics) || _composite == null || _compositeTarget == null
            || !ReferenceEquals(FrameSchedule.FogCompositeTarget, _compositeTarget)
            || FrameSchedule.SceneColor == null || FrameSchedule.SceneDepth == null || FilteredTexture == null)
        { ResetOutput(); return false; }
        // 此时 GTAO 已完成，不受 AfterOpaque 注册先后次序影响。
        string? ao = FrameSchedule.PostAoTexture;
        var inputs = new FogCompositeInputs(FrameSchedule.SceneColor, FrameSchedule.SceneDepth, _frameBuffer!,
            IntegratedTexture, FilteredTexture, new ComputeResourceRef { TextureName = ao }, RenderQuality.Current.AoIntensity, ao != null);
        try
        {
            if (_composite.Prepare(inputs, _compositeTarget)) return true;
        }
        catch (Exception ex)
        {
            DeviceServices.BaseApp.AddLog(LogType.Error, $"[VolumetricFog] 合成准备失败：{ex.Message}");
        }
        ResetOutput();
        return false;
    }

    FogCompositeResult DrawComposite(IGraphics g, RenderTarget source)
    {
        if (!Ready || !ReferenceEquals(g, _graphics) || !ReferenceEquals(source, FrameSchedule.SceneColor))
        { ResetOutput(); return default; }
        try
        {
            var result = _composite!.Draw();
            if (!result.Success) ResetOutput();
            return result;
        }
        catch (Exception ex)
        {
            ResetOutput();
            DeviceServices.BaseApp.AddLog(LogType.Error, $"[VolumetricFog] 合成绘制失败：{ex.Message}");
            return default;
        }
    }

    static bool Dispatch(IGraphics g, ComputeKernel kernel, ComputeResourceRef[] resources, int x, int y, int z)
    {
        Vector4 parameters = new(x, y, z, 0);
        return g.TryDispatchCompute(new ComputeDispatchArgs { Kernel = kernel,
            Params = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref parameters, 1)),
            Resources = resources, GroupsX = (uint)x, GroupsY = (uint)y, GroupsZ = (uint)z });
    }

    static bool ProjectionMatches(Matrix4x4 a, Matrix4x4 b)
    {
        var left = MemoryMarshal.CreateReadOnlySpan(ref a, 1);
        var right = MemoryMarshal.CreateReadOnlySpan(ref b, 1);
        var av = MemoryMarshal.Cast<Matrix4x4, float>(left);
        var bv = MemoryMarshal.Cast<Matrix4x4, float>(right);
        for (int i = 0; i < 16; i++) if (Math.Abs(av[i] - bv[i]) > 0.0001f) return false;
        return true;
    }

    static bool LightsChanged(in SceneLightParams a, in SceneLightParams b)
    {
        if (a.Params0.X != b.Params0.X || a.Params0.Z != b.Params0.Z || a.Params0.W != b.Params0.W
            || a.ShadowParams0.X != b.ShadowParams0.X || a.ShadowParams1.X != b.ShadowParams1.X) return true;
        for (int i = 0; i < Math.Clamp(a.LightCount, 0, 8); i++)
        {
            var x = a.Lights[i]; var y = b.Lights[i];
            if (x.DirType.W != y.DirType.W || Vector4.Distance(x.ColorIntensity, y.ColorIntensity) > Math.Max(0.05f, y.ColorIntensity.Length() * 0.2f)
                || Vector4.Distance(x.PosRange, y.PosRange) > 0.5f || Vector4.Distance(x.DirType, y.DirType) > 0.1f) return true;
        }
        return false;
    }

    /// <summary>四语言共用的字节布局；公开以便数学与平台绑定测试逐字段核对。</summary>
    public static void PackFrame(Span<Vector4> data, SceneFog fog, in SceneLightParams lights,
        (int Width, int Height, int Slices) grid, Matrix4x4 inverseVp, Matrix4x4 inverseJitteredVp,
        Matrix4x4 previousVp, Vector3 position, Vector3 forward, Vector3 previousPosition, Vector3 previousForward,
        float historyWeight, bool historyValid, int shadowSamples)
    {
        if (data.Length < FrameVectorCount) throw new ArgumentException("雾帧缓冲不足。", nameof(data));
        data.Clear();
        WriteMatrix(data, 0, inverseVp); WriteMatrix(data, 4, previousVp); WriteMatrix(data, 18, inverseJitteredVp);
        if (!Matrix4x4.Invert(inverseVp, out var currentVp)) throw new ArgumentException("雾采样投影不可逆。", nameof(inverseVp));
        WriteMatrix(data, 22, currentVp);
        data[8] = new(position, fog.MaxDistance); data[9] = new(forward, fog.Anisotropy);
        data[10] = new(grid.Width, grid.Height, grid.Slices, historyWeight);
        data[11] = new(fog.Extinction, fog.BaseHeight, fog.HeightFalloff, fog.NoiseAmount);
        data[12] = new(fog.Albedo, fog.NoiseScale); data[13] = new(fog.WindOffset, fog.ActiveVolumeCount);
        // SH 的 l=0 辐照度除以 pi 还原平均辐亮度；不读取 DDGI，也不叠加直射光。
        Vector3 ambient = lights.EnvParams.Z > 0.5f
            ? new Vector3(lights.IrradianceSH9[0].X, lights.IrradianceSH9[0].Y, lights.IrradianceSH9[0].Z) * (0.282095f / MathF.PI) * lights.EnvParams.Y
            : new Vector3(lights.Ambient.X, lights.Ambient.Y, lights.Ambient.Z) * lights.Ambient.W;
        data[14] = new(Vector3.Max(ambient, Vector3.Zero), shadowSamples);
        data[15] = new(previousPosition, historyValid ? 1 : 0);
        data[16] = new(previousForward, FrameSchedule.CloudNoiseTexture != null ? 1 : 0);
        var copy = lights;
        MemoryMarshal.Cast<SceneLightParams, Vector4>(MemoryMarshal.CreateReadOnlySpan(ref copy, 1)).CopyTo(data[LightingOffset..]);
        int index = VolumeOffset;
        foreach (var volume in fog.Volumes)
        {
            if (!volume.Enabled) continue;
            var local = Matrix4x4.CreateTranslation(-volume.Position)
                * Matrix4x4.CreateFromQuaternion(Quaternion.Conjugate(volume.Rotation))
                * Matrix4x4.CreateScale(new Vector3(2) / volume.Size);
            WriteMatrix(data, index, local);
            data[index + 4] = new(volume.Albedo, volume.Extinction);
            data[index + 5] = new((int)volume.Shape, (int)volume.Mode, volume.EdgeFeather, volume.NoiseAmount);
            data[index + 6] = new(volume.NoiseScale, 0, 0, 0);
            index += VolumeStride;
        }
    }

    static void WriteMatrix(Span<Vector4> data, int offset, Matrix4x4 matrix)
        => MemoryMarshal.Cast<Matrix4x4, Vector4>(MemoryMarshal.CreateReadOnlySpan(ref matrix, 1)).CopyTo(data[offset..]);

    public override void OnUnregistered() => Dispose();

    public void Dispose()
    {
        var graphics = _graphics;
        if (graphics == null) return;
        _graphics = null;
        FrameSchedule.UnregisterCompute(this);
        ResetOutput();
        if (FrameSchedule.PrepareFogComposite == PrepareComposite) FrameSchedule.PrepareFogComposite = null;
        if (FrameSchedule.RenderFogComposite == DrawComposite) FrameSchedule.RenderFogComposite = null;
        if (ReferenceEquals(FrameSchedule.FogCompositeTarget, _compositeTarget)) FrameSchedule.FogCompositeTarget = null;
        _composite?.Dispose(); _composite = null;
        if (_compositeTarget != null) FrameSchedule.ForgetSceneComposite(_compositeTarget);
        _compositeTarget?.Dispose(); _compositeTarget = null;
        _noise?.Dispose(); _inject?.Dispose(); _filter?.Dispose(); _integrate?.Dispose();
        _noise = _inject = _filter = _integrate = null;
        _frameBuffer?.Dispose(); _frameBuffer = null;
        foreach (string name in OwnedTextures) graphics.ReleaseComputeTexture3D(name);
        if (ReferenceEquals(_active, this)) _active = null;
        _graphics = null; _scene = null; _allocated = _noiseReady = false;
        Array.Clear(_injectResources); Array.Clear(_filterResources); Array.Clear(_integrateResources);
    }

    static ComputeKernelDesc Kernel(string name, ShaderSourceSet source, bool columns, params ComputeBindingType[] types)
    {
        var bindings = new ComputeBindingDesc[types.Length + 1];
        bindings[0] = new() { Type = ComputeBindingType.Params, SizeInBytes = 16 };
        for (int i = 0; i < types.Length; i++) bindings[i + 1] = new() { Type = types[i], StorageFormat = ComputeStorageFormat.Rgba16Float };
        return new() { Name = name, Source = source, Bindings = bindings,
            WorkgroupX = columns ? 8u : 4u, WorkgroupY = columns ? 8u : 4u, WorkgroupZ = columns ? 1u : 4u };
    }

    public static ComputeKernelDesc CreateNoiseKernel()
    {
        var result = Kernel("fogNoise", new() { Hlsl = NoiseHlsl, Glsl = NoiseGlsl, Msl = NoiseMsl, Wgsl = NoiseWgsl }, false,
            ComputeBindingType.StorageTexture3DWrite);
        result.Bindings[1].StorageFormat = ComputeStorageFormat.Rgba8Unorm;
        return result;
    }

    public static ComputeKernelDesc CreateInjectionKernel() => Kernel("fogInject",
        new() { Hlsl = InjectionHlsl, Glsl = InjectionGlsl, Msl = InjectionMsl, Wgsl = InjectionWgsl }, false,
        ComputeBindingType.StorageBufferRead, ComputeBindingType.SampledTexture3D, ComputeBindingType.DepthTexture,
        ComputeBindingType.SampledTexture, ComputeBindingType.StorageTexture3DWrite);

    public static ComputeKernelDesc CreateFilterKernel() => Kernel("fogFilter",
        new() { Hlsl = FilterHlsl, Glsl = FilterGlsl, Msl = FilterMsl, Wgsl = FilterWgsl }, false,
        ComputeBindingType.StorageBufferRead, ComputeBindingType.SampledTexture3D, ComputeBindingType.SampledTexture3D,
        ComputeBindingType.StorageTexture3DWrite);

    public static ComputeKernelDesc CreateIntegrationKernel() => Kernel("fogIntegrate",
        new() { Hlsl = IntegrationHlsl, Glsl = IntegrationGlsl, Msl = IntegrationMsl, Wgsl = IntegrationWgsl }, true,
        ComputeBindingType.StorageBufferRead, ComputeBindingType.SampledTexture3D, ComputeBindingType.StorageTexture3DWrite);
}
