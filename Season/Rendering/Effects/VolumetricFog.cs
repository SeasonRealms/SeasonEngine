// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

/// <summary>Near-ground volumetric fog: filter the local Q/sigma_t first, then integrate; accumulated volume RGB=L, A=T.</summary>
public sealed partial class VolumetricFogEffect : ComputeEffect, IDisposable
{
    // The frame buffer holds only float4; matrices are stored in System.Numerics raw row order; the surface lighting UBO is not reused.
    // 0..3 inverseVP, 4..7 previousVP, 8 camera/D, 9 forward/g, 10 grid/history,
    // 11 density/height/falloff/noise, 12 albedo/scale, 13 wind/count, 14 environment/PCF,
    // 15 previousCamera/valid, 16 previousForward/cloudReady, 18..21 inverseJitteredVP, 22..25 currentVP,
    // 32..185 final SceneLightParams (including regional weather and the 2-8 virtual ground), 192..447 for 32 local volumes.
    // 448..455 the fog's own far shadow: view-projection at 448, parameters at 452.
    // 456..505 the local point-light cube shadows: two slots of 25 rows, six 4-row matrices then one meta row.
    // Each local volume is 8 rows: matrix (4), albedo/extinction, shape/mode/feather/noise, noise scale/anisotropy, self-illumination.
    // 186..191 is headroom reserved for SceneLightParams to keep appending: appended fields only grow here and never reach 192.
    // The far-shadow and local-shadow blocks sit past the volumes rather than in that headroom for the same reason they
    // exist at all - they belong to this kernel alone, while the headroom belongs to the lighting struct every shader reads.
    public const int FrameVectorCount = LocalShadowOffset + CascadedShadow.LocalShadowLights * LocalShadowStride;
    public const int LightingOffset = 32;
    public const int VolumeOffset = 192;
    public const int VolumeStride = 8;

    /// <summary>Row of the far shadow's light-space ViewProj. Rows 449..451 continue the matrix; 452 carries its parameters.</summary>
    public const int FarShadowOffset = 448;

    /// <summary>
    /// Row of the first local point-light slot. The far block occupies eight rows (four of matrix, one of parameters, three
    /// unused), so this starts right after it rather than in the lighting headroom the volumes are kept away from.
    /// </summary>
    public const int LocalShadowOffset = FarShadowOffset + 8;

    /// <summary>Rows per local slot: six cube faces of four matrix rows each, then a meta row at +24.</summary>
    public const int LocalShadowStride = CascadedShadow.LocalShadowFaces * 4 + 1;
    public const string NoiseTexture = "compute3d://fog/noise";
    public const string InjectionTexture = "compute3d://fog/injection";
    public const string IntegratedTexture = "compute3d://fog/integrated";
    static readonly string[] HistoryTextures = { "compute3d://fog/history0", "compute3d://fog/history1" };
    static readonly string[] OwnedTextures = { NoiseTexture, InjectionTexture, HistoryTextures[0], HistoryTextures[1], IntegratedTexture };

    // FrameSchedule and texture names belong to the same rendering device domain; only one resource owner is allowed.
    static VolumetricFogEffect? _active;
    readonly Vector4[] _frame = new Vector4[FrameVectorCount];
    readonly ComputeResourceRef[] _noiseResources = { NoiseTexture };
    readonly ComputeResourceRef[] _injectResources = new ComputeResourceRef[7];
    readonly ComputeResourceRef[] _filterResources = new ComputeResourceRef[4];
    readonly ComputeResourceRef[] _integrateResources = new ComputeResourceRef[3];
    IGraphics? _graphics;
    StorageBuffer? _frameBuffer;
    FogCompositePipeline? _composite;
    RenderTarget? _compositeTarget;
    ComputeKernel? _noise, _inject, _filter, _integrate;
    int _width, _height;
    bool _noiseReady, _allocated;
    FogQuality _quality;

    /// <summary>Per-view temporal and scene-tracking state, replacing the former flat instance fields.</summary>
    sealed class FogViewState
    {
        public int Ping;
        public bool HistoryValid;
        public SceneFog? Scene;
        public uint Revision;
        public Matrix4x4 PreviousVp, PreviousProjection;
        public Vector3 PreviousPosition, PreviousForward;
        public SceneLightParams PreviousLights;
        public float HistoryWeight;
        public bool Ready;
        public string? FilteredTexture;
    }

    public override string Name => "volumetricFog";
    public override ComputePhase Phase => ComputePhase.AfterOpaque;

    /// <summary>Per view: injection and integration read one view's depth and scene color and publish on that view's
    /// targets (<see cref="FrameSchedule.FogCompositeTarget"/> / <see cref="FrameSchedule.FogOutputReady"/>).</summary>
    public override ComputeScope Scope => ComputeScope.View;
    public override ViewFeature RequiredFeature => ViewFeature.Fog;
    public (int Width, int Height, int Slices) Grid { get; private set; }

    /// <summary>Convenience accessor: the primary view's filtered texture name (history-write slot).
    /// Use <see cref="GetViewFilteredTexture"/> for per-view access.</summary>
    public string? FilteredTexture => FrameSchedule.PrimaryTargets.FogLocalName;

    /// <summary>Convenience accessor: the primary view's fogFrame buffer.
    /// Backends should read <see cref="ViewTargets.FogFrameBuffer"/> for per-view binding.</summary>
    public StorageBuffer? SamplingBuffer => _frameBuffer;

    /// <summary>Primary-view Ready; check <see cref="ViewTargets.FogOutputReady"/> for per-view.</summary>
    public bool Ready { get; private set; }

    /// <summary>Primary-view history blend weight; per-view weight is in <see cref="FogViewState"/>.</summary>
    public float HistoryWeight { get; private set; }

    public override bool Initialize(IGraphics g)
    {
        if (_graphics != null) throw new InvalidOperationException("The fog effect is already initialized.");
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
            // Register primary-view fog resources on its targets (so Record() reads uniformly from ActiveTargets).
            var primary = FrameSchedule.PrimaryTargets;
            primary.FogFrameBuffer = _frameBuffer;
            primary.FogIntegratedName = IntegratedTexture;
            primary.FogInjectionName = InjectionTexture;
            primary.FogHistory0Name = HistoryTextures[0];
            primary.FogHistory1Name = HistoryTextures[1];
            primary.FogLocalName = HistoryTextures[0]; // initial write slot
            FrameSchedule.PrepareFogComposite = PrepareComposite;
            FrameSchedule.RenderFogComposite = DrawComposite;
            return true;
        }
        catch (Exception ex)
        {
            Dispose();
            DeviceServices.BaseApp.AddLog(LogType.Error, $"[VolumetricFog] initialization failed: {ex.Message}");
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

    /// <summary>
    /// Lazily allocates per-view fog resources (frame buffer + scoped 3D volumes) for secondary views.
    /// The primary view (Index==0) already has resources from Initialize(); this is a no-op for it.
    /// </summary>
    bool EnsureViewFogResources(ViewTargets targets, IGraphics g)
    {
        if (targets.HasFogVolumes) return true;
        if (targets.Index == 0) return _allocated; // primary uses Initialize-time allocation
        if (!_allocated) return false; // volumes depend on primary allocation succeeding first

        targets.FogFrameBuffer = g.CreateStorageBuffer(FrameVectorCount * 16);
        targets.FogInjectionName = targets.Scope(InjectionTexture);
        targets.FogHistory0Name = targets.Scope(HistoryTextures[0]);
        targets.FogHistory1Name = targets.Scope(HistoryTextures[1]);
        targets.FogIntegratedName = targets.Scope(IntegratedTexture);

        bool valid = true;
        uint w = (uint)Grid.Width, h = (uint)Grid.Height, slices = (uint)Grid.Slices;
        valid &= g.TryCreateComputeTexture3D(targets.FogInjectionName, w, h, slices, ComputeStorageFormat.Rgba16Float);
        valid &= g.TryCreateComputeTexture3D(targets.FogHistory0Name, w, h, slices, ComputeStorageFormat.Rgba16Float);
        valid &= g.TryCreateComputeTexture3D(targets.FogHistory1Name, w, h, slices, ComputeStorageFormat.Rgba16Float);
        valid &= g.TryCreateComputeTexture3D(targets.FogIntegratedName, w, h, slices + 1, ComputeStorageFormat.Rgba16Float);

        if (valid)
        {
            targets.FogLocalName = targets.FogHistory0Name; // initial write slot
            targets.FogPing = 0;
            targets.FogHistoryValid = false;
        }
        else
        {
            // Rollback on partial failure
            targets.FogFrameBuffer?.Dispose();
            targets.FogFrameBuffer = null;
            targets.FogInjectionName = targets.FogHistory0Name = targets.FogHistory1Name = targets.FogIntegratedName = null;
        }

        // Allocate per-view composite target matching this view's scene dimensions
        if (valid && targets.FogCompositeTarget == null && targets.SceneColor != null)
        {
            targets.FogCompositeTarget = g.CreateRenderTarget(new RenderTargetDesc
            {
                ColorFormat = RtFormat.Rgba16Float, DepthFormat = RtFormat.None, SampleCount = 1,
                NoImplicitDepth = true, Width = targets.SceneColor.Desc.Width, Height = targets.SceneColor.Desc.Height,
                MatchBackbufferSize = false, ClearColor = Vector4.Zero
            });
        }
        return valid;
    }

    /// <summary>Releases per-view fog resources allocated by <see cref="EnsureViewFogResources"/>.</summary>
    void ReleaseViewFogResources(ViewTargets targets, IGraphics? g)
    {
        if (targets.Index == 0) return; // primary is cleaned by Dispose()
        if (g != null)
        {
            if (targets.FogInjectionName != null) g.ReleaseComputeTexture3D(targets.FogInjectionName);
            if (targets.FogHistory0Name != null) g.ReleaseComputeTexture3D(targets.FogHistory0Name);
            if (targets.FogHistory1Name != null) g.ReleaseComputeTexture3D(targets.FogHistory1Name);
            if (targets.FogIntegratedName != null) g.ReleaseComputeTexture3D(targets.FogIntegratedName);
        }
        targets.FogFrameBuffer?.Dispose();
        targets.FogFrameBuffer = null;
        targets.FogInjectionName = targets.FogHistory0Name = targets.FogHistory1Name = null;
        targets.FogIntegratedName = targets.FogLocalName = null;
        targets.FogPing = 0;
        targets.FogHistoryValid = false;
        targets.FogCompositeTarget?.Dispose();
        targets.FogCompositeTarget = null;
    }

    public override void OnResize(IGraphics g)
    {
        if (!ReferenceEquals(_graphics, g)) return;
        try
        {
            Allocate(g);
            // Primary view targets get re-synced after allocate
            var primary = FrameSchedule.PrimaryTargets;
            primary.FogFrameBuffer = _frameBuffer;
            primary.FogIntegratedName = IntegratedTexture;
            primary.FogInjectionName = InjectionTexture;
            primary.FogHistory0Name = HistoryTextures[0];
            primary.FogHistory1Name = HistoryTextures[1];
            primary.FogLocalName = HistoryTextures[0];
            // Invalidate per-view state for secondary views (grid dimensions may change)
            ClearViewStates();
            foreach (var slot in FrameSchedule.Views)
                if (slot.Targets != primary && slot.Targets.HasFogVolumes)
                    ReleaseViewFogResources(slot.Targets, g);
        }
        catch (Exception ex)
        {
            _allocated = false;
            DeviceServices.BaseApp.AddLog(LogType.Error, $"[VolumetricFog] Resize failed: {ex.Message}");
        }
    }

    /// <summary>Per-view resize: releases and lets the next Record() re-allocate the secondary view's fog resources.</summary>
    public override void OnViewResize(IGraphics g, ViewTargets targets)
    {
        if (!ReferenceEquals(_graphics, g) || targets.Index == 0) return;
        if (targets.HasFogVolumes) ReleaseViewFogResources(targets, g);
    }

    void ResetOutput(ViewTargets targets)
    {
        Ready = false;
        targets.FogOutputReady = false;
        var state = ViewState<FogViewState>(targets);
        state.HistoryValid = false;
        state.HistoryWeight = 0;
        state.Ready = false;
        state.FilteredTexture = null;
        targets.FogLocalName = targets.FogHistory0Name;
        // 对外暴露的 HistoryWeight 只在 Record 成功路径与 state 同步；失效路径如果不一并清零，
        // 外部读到的是上一帧的历史权重，看不出本帧已经放弃历史。
        if (targets.Index == 0) HistoryWeight = 0;
    }

    /// <summary>Legacy no-arg reset for Initialize-failure paths (targets may not have fog fields yet).</summary>
    void ResetOutput()
    {
        Ready = false;
        HistoryWeight = 0;
        if (ReferenceEquals(_active, this)) FrameSchedule.FogOutputReady = false;
    }

    public override void Record(IGraphics g)
    {
        Ready = false;
        if (!ReferenceEquals(_active, this)) return;
        var targets = FrameSchedule.ActiveTargets;
        targets.FogOutputReady = false;
        var state = ViewState<FogViewState>(targets);
        state.Ready = false;
        state.FilteredTexture = null;
        var app = DeviceServices.BaseApp;
        var fog = app.Fog;
        if (!_allocated || !ReferenceEquals(g, _graphics) || !targets.SceneSegmented
            || !fog.Enabled || RenderQuality.Current.VolumetricFog != _quality
            || targets.SceneDepth == null || targets.SceneColor == null
            || !FrameSchedule.LightingSnapshotValid)
        { ResetOutput(targets); return; }
        // Primary view: match allocated dimensions; secondary: rely on primary grid (shared resolution).
        if (targets.Index == 0 && (targets.SceneColor.Desc.Width != _width || targets.SceneColor.Desc.Height != _height
            || targets.SceneDepth.Desc.Width != _width || targets.SceneDepth.Desc.Height != _height))
        { ResetOutput(targets); return; }
        // Lazily allocate per-view fog resources for secondary views.
        if (!EnsureViewFogResources(targets, g))
        { ResetOutput(targets); return; }
        // The camera terms belong to the view being recorded. The frame snapshot (FrameSchedule.RenderView /
        // RenderProjection / UnjitteredRenderViewProjection) is captured once per frame at the main camera's update
        // point, so a secondary view would otherwise rebuild its rays, orient its volume and judge its history
        // against the main camera. view.View / RenderViewProjection / RenderProjection are that view's own triple,
        // identical to the snapshot's for the main view.
        //
        // A host that never prepares a view (the acceptance harnesses) has no camera of its own, and the snapshot it
        // captures is the only one both it and the scheduler know about; reading the app's MainView there would make
        // the fog ignore the matrices the harness just fed in, and history would never be reset on a camera jump.
        var currentView = RenderView.CurrentOrNull;
        bool ownCamera = currentView is { Prepared: true };
        var viewMatrix = ownCamera ? currentView!.View : FrameSchedule.RenderView;
        var vp = ownCamera ? currentView!.RenderViewProjection : FrameSchedule.UnjitteredRenderViewProjection;
        var jitteredProjection = ownCamera ? currentView!.RenderProjection : FrameSchedule.RenderProjection;
        if (!Matrix4x4.Invert(vp, out var inverse) || !Matrix4x4.Invert(viewMatrix, out var inverseView)
            || !Matrix4x4.Invert(viewMatrix * jitteredProjection, out var inverseJittered))
        { ResetOutput(targets); return; }
        var position = inverseView.Translation;
        var forward = Vector3.Normalize(new Vector3(inverseView.M31, inverseView.M32, inverseView.M33));
        var projection = inverseView * vp;
        ref readonly var lights = ref FrameSchedule.LightingSnapshot;
        bool reset = !state.HistoryValid || !ReferenceEquals(state.Scene, fog) || state.Revision != fog.HistoryRevision
            || Vector3.Distance(position, state.PreviousPosition) > 2f || Vector3.Dot(forward, state.PreviousForward) < 0.8f
            || !ProjectionMatches(projection, state.PreviousProjection) || LightsChanged(lights, state.PreviousLights);
        float requested = RenderQuality.Current.FogHistoryWeight;
        float historyWeight = reset ? 0 : Math.Clamp(float.IsFinite(requested) ? requested : 0, 0, 0.98f)
            / (1f + fog.WindVelocity.Length() * 0.1f);
        state.HistoryWeight = historyWeight;
        // For primary view, keep legacy property in sync:
        if (targets.Index == 0) HistoryWeight = historyWeight;
        PackFrame(_frame, fog, lights, Grid, inverse, inverseJittered, state.PreviousVp, position, forward,
            state.PreviousPosition, state.PreviousForward, historyWeight, !reset, RenderQuality.Current.FogShadowSamples);
        // With no atlas, use the scene depth to satisfy the binding shape and clear every geometric shadow flag.
        if (FrameSchedule.ShadowMap == null)
        {
            _frame[LightingOffset + 56].X = 0;
            _frame[LightingOffset + 57].X = 0;
        }
        // Resolve per-view frame buffer and scoped volume names.
        var frameBuf = targets.FogFrameBuffer!;
        string injection = targets.FogInjectionName!;
        string historyRead = (state.Ping == 0 ? targets.FogHistory0Name : targets.FogHistory1Name)!;
        string historyWrite = (state.Ping == 0 ? targets.FogHistory1Name : targets.FogHistory0Name)!;
        string integrated = targets.FogIntegratedName!;

        // Update CS resource arrays to per-view names.
        _injectResources[0] = frameBuf;
        _injectResources[2] = FrameSchedule.ShadowMap ?? targets.SceneDepth;
        _injectResources[3] = FrameSchedule.CloudNoiseTexture is string cloud
            ? new ComputeResourceRef { TextureName = cloud } : new ComputeResourceRef { Target = targets.SceneColor };
        // Slot 4 stays the injection output: the far map is the sixth binding, appended after the storage write so the
        // four existing slots keep the meaning the kernel declaration already gave them.
        _injectResources[4] = injection;
        // The far map is bound only when the host registered it; otherwise the scene depth keeps the binding shape valid
        // while the shader's enable flag stays zero, so the texture is never read.
        _injectResources[5] = FrameSchedule.FarShadowMap is { } farShadow
            ? new ComputeResourceRef { Target = farShadow } : new ComputeResourceRef { Target = targets.SceneDepth };
        // Same shape for the local point-light atlas. PackFrame is what actually switches the shader term off - it writes
        // -1 into every meta row when the host registered no local map - so the scene depth here is a placeholder the
        // kernel's light-index test can never reach.
        _injectResources[6] = FrameSchedule.LocalShadowMap is { } localShadow
            ? new ComputeResourceRef { Target = localShadow } : new ComputeResourceRef { Target = targets.SceneDepth };
        _filterResources[0] = frameBuf;
        _filterResources[1] = injection;
        _filterResources[2] = historyRead;
        _filterResources[3] = historyWrite;
        _integrateResources[0] = frameBuf;
        _integrateResources[1] = historyWrite;
        _integrateResources[2] = integrated;

        if (!g.TryUpdateStorageBuffer(frameBuf, MemoryMarshal.AsBytes(_frame.AsSpan())))
        { ResetOutput(targets); return; }
        if (!_noiseReady)
        {
            _noiseReady = Dispatch(g, _noise!, _noiseResources, 16, 16, 16);
            if (!_noiseReady) { ResetOutput(targets); return; }
        }
        if (!Dispatch(g, _inject!, _injectResources, (Grid.Width + 3) / 4, (Grid.Height + 3) / 4, (Grid.Slices + 3) / 4)
            || !Dispatch(g, _filter!, _filterResources, (Grid.Width + 3) / 4, (Grid.Height + 3) / 4, (Grid.Slices + 3) / 4)
            || !Dispatch(g, _integrate!, _integrateResources, (Grid.Width + 7) / 8, (Grid.Height + 7) / 8, 1))
        { ResetOutput(targets); return; }
        // Swap ping for next frame's history read/write.
        state.Ping ^= 1;
        state.HistoryValid = true;
        state.Scene = fog; state.Revision = fog.HistoryRevision;
        state.PreviousPosition = position; state.PreviousForward = forward;
        state.PreviousVp = vp; state.PreviousProjection = projection; state.PreviousLights = lights;
        state.FilteredTexture = historyWrite;
        state.Ready = true;
        targets.FogLocalName = historyWrite;
        targets.FogOutputReady = true;
        // Keep primary-view properties in sync for legacy consumers.
        if (targets.Index == 0) { Ready = true; }
    }

    bool PrepareComposite(IGraphics g)
    {
        var targets = FrameSchedule.ActiveTargets;
        var state = ViewState<FogViewState>(targets);
        if (!state.Ready || !ReferenceEquals(g, _graphics) || _composite == null)
        { ResetOutput(targets); return false; }
        // Resolve per-view composite target: primary uses _compositeTarget, secondary uses targets.FogCompositeTarget.
        var compositeTarget = targets.Index == 0 ? _compositeTarget : targets.FogCompositeTarget;
        if (compositeTarget == null)
        { ResetOutput(targets); return false; }
        // For primary, also verify our target hasn't been replaced:
        if (targets.Index == 0 && !ReferenceEquals(targets.FogCompositeTarget, _compositeTarget))
        { ResetOutput(targets); return false; }
        if (targets.SceneColor == null || targets.SceneDepth == null || state.FilteredTexture == null)
        { ResetOutput(targets); return false; }
        // GTAO is already complete at this point, independent of AfterOpaque registration order.
        string? ao = FrameSchedule.PostAoTexture;
        var frameBuf = targets.FogFrameBuffer!;
        var inputs = new FogCompositeInputs(FrameSchedule.EffectiveSceneColor!, targets.SceneDepth, frameBuf,
            targets.FogIntegratedName!, state.FilteredTexture, new ComputeResourceRef { TextureName = ao },
            RenderQuality.Current.AoIntensity, ao != null);
        try
        {
            if (_composite.Prepare(inputs, compositeTarget)) return true;
        }
        catch (Exception ex)
        {
            DeviceServices.BaseApp.AddLog(LogType.Error, $"[VolumetricFog] composite prepare failed: {ex.Message}");
        }
        ResetOutput(targets);
        return false;
    }

    FogCompositeResult DrawComposite(IGraphics g, RenderTarget source)
    {
        var targets = FrameSchedule.ActiveTargets;
        var state = ViewState<FogViewState>(targets);
        if (!state.Ready || !ReferenceEquals(g, _graphics) || !ReferenceEquals(source, FrameSchedule.EffectiveSceneColor))
        { ResetOutput(targets); return default; }
        try
        {
            var result = _composite!.Draw();
            if (!result.Success) ResetOutput(targets);
            return result;
        }
        catch (Exception ex)
        {
            ResetOutput(targets);
            DeviceServices.BaseApp.AddLog(LogType.Error, $"[VolumetricFog] composite draw failed: {ex.Message}");
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

    /// <summary>Byte layout shared by all four languages; public so math and platform binding tests can verify it field by field.</summary>
    public static void PackFrame(Span<Vector4> data, SceneFog fog, in SceneLightParams lights,
        (int Width, int Height, int Slices) grid, Matrix4x4 inverseVp, Matrix4x4 inverseJitteredVp,
        Matrix4x4 previousVp, Vector3 position, Vector3 forward, Vector3 previousPosition, Vector3 previousForward,
        float historyWeight, bool historyValid, int shadowSamples)
    {
        if (data.Length < FrameVectorCount) throw new ArgumentException("Fog frame buffer too small.", nameof(data));
        data.Clear();
        WriteMatrix(data, 0, inverseVp); WriteMatrix(data, 4, previousVp); WriteMatrix(data, 18, inverseJitteredVp);
        if (!Matrix4x4.Invert(inverseVp, out var currentVp)) throw new ArgumentException("Fog sampling projection is not invertible.", nameof(inverseVp));
        WriteMatrix(data, 22, currentVp);
        data[8] = new(position, fog.MaxDistance); data[9] = new(forward, fog.Anisotropy);
        data[10] = new(grid.Width, grid.Height, grid.Slices, historyWeight);
        data[11] = new(fog.Extinction, fog.BaseHeight, fog.HeightFalloff, fog.NoiseAmount);
        data[12] = new(fog.Albedo, fog.NoiseScale); data[13] = new(fog.WindOffset, fog.ActiveVolumeCount);
        // Divide the SH l=0 irradiance by pi to recover the average radiance; DDGI is not read and direct light is not added.
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
            data[index + 6] = new(volume.NoiseScale, volume.EffectiveAnisotropy(fog.Anisotropy), 0, 0);
            // Row 7 was padding: it now carries the self-illumination floor, so the stride and every existing index stay put.
            data[index + 7] = new(volume.SelfIllumination, 0);
            index += VolumeStride;
        }
        // Far shadow, written only when the host registered the pass and a directional light actually casts into a live
        // volume. Zero means the injection kernel never samples the map, which is also how a host that never registered
        // it behaves - there is no second flag to keep in sync.
        //
        // .x is the enable flag and .y the depth bias: those are the whole of what the injection kernel reads. .z and .w
        // carry the map's half extent and the texel size, filled so a depth view can read its resolution off this row.
        if (CascadedShadow.FarActive)
        {
            WriteMatrix(data, FarShadowOffset, CascadedShadow.FarViewProj);
            data[FarShadowOffset + 4] = new(1f, CascadedShadow.FarBias, CascadedShadow.FarExtent, 1f / CascadedShadow.FarSize);
        }
        // Local point-light cubes, one slot per selected light: six light-space ViewProj followed by the row the kernel
        // keys on. A slot is written only when a light was selected *and* the host registered the atlas; anything else
        // writes .x = -1, which names no light. Leaving the row at the zero PackFrame cleared the frame to would have
        // made an empty slot claim light 0, so the -1 is load-bearing rather than a spare sentinel.
        bool localBound = FrameSchedule.LocalShadowMap != null && RenderQuality.Current.ShadowsEnabled;
        float localStrength = Math.Clamp(RenderQuality.Current.ShadowStrength, 0f, 1f);
        for (int slot = 0; slot < CascadedShadow.LocalShadowLights; slot++)
        {
            int baseRow = LocalShadowOffset + slot * LocalShadowStride;
            int light = localBound ? CascadedShadow.LocalLightIndex[slot] : -1;
            if (light < 0)
            {
                data[baseRow + LocalShadowStride - 1] = new(-1f, 0f, 0f, 0f);
                continue;
            }
            for (int face = 0; face < CascadedShadow.LocalShadowFaces; face++)
                WriteMatrix(data, baseRow + face * 4, CascadedShadow.LocalViewProj[slot * CascadedShadow.LocalShadowFaces + face]);
            // .x the light index, .y the strength, .z the per-metre bias coefficient the kernel divides by clip.w,
            // .w the receiver-plane tilt strength. Both come from the lamp's range rather than from a fixed NDC margin:
            // see CascadedShadow.LocalBiasCoefficient for why a constant here was the wrong unit. An empty slot writes
            // zeros, which the kernel never reads because .x names no light.
            data[baseRow + LocalShadowStride - 1] = new(light, localStrength,
                CascadedShadow.LocalBiasCoefficient(lights.Lights[light].PosRange.W),
                CascadedShadow.LocalShadowNormalOffset);
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
        // Release all per-view fog resources (secondary views only).
        foreach (var slot in FrameSchedule.Views)
            ReleaseViewFogResources(slot.Targets, graphics);
        if (FrameSchedule.PrimaryTargets.FogFrameBuffer == _frameBuffer)
        {
            FrameSchedule.PrimaryTargets.FogFrameBuffer = null;
            FrameSchedule.PrimaryTargets.FogIntegratedName = FrameSchedule.PrimaryTargets.FogInjectionName = null;
            FrameSchedule.PrimaryTargets.FogHistory0Name = FrameSchedule.PrimaryTargets.FogHistory1Name = null;
            FrameSchedule.PrimaryTargets.FogLocalName = null;
        }
        ClearViewStates();
        if (ReferenceEquals(_active, this)) _active = null;
        _graphics = null; _allocated = _noiseReady = false;
        Array.Clear(_injectResources); Array.Clear(_filterResources); Array.Clear(_integrateResources);
    }

    static ComputeKernelDesc Kernel(string name, ShaderSourceSet source, bool columns, params ComputeBindingType[] types)
    {
        var bindings = new ComputeBindingDesc[types.Length + 1];
        bindings[0] = new() { Type = ComputeBindingType.Params, SizeInBytes = 16 };
        for (int i = 0; i < types.Length; i++) bindings[i + 1] = new() { Type = types[i], StorageFormat = ComputeStorageFormat.Rgba16Float };
        // Step 5: the whole fog family is Deferred, and it is deferred as one unit - Record() publishes
        // FogOutputReady for the view and every consumer (the composite pass, the transparent PS) reads that flag
        // instead of the kernels, so no frame can observe a half-built fog volume. Deferring it is what buys the
        // first frame its screen: these four sources are 33.6 s of fxc in one report (fogInject alone 19.9 s), and
        // without them the frame still renders - unfogged, with the composite simply not happening.
        return new() { Name = name, Source = source, Bindings = bindings, Deferred = true,
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
        ComputeBindingType.SampledTexture, ComputeBindingType.StorageTexture3DWrite, ComputeBindingType.DepthTexture,
        ComputeBindingType.DepthTexture);

    public static ComputeKernelDesc CreateFilterKernel() => Kernel("fogFilter",
        new() { Hlsl = FilterHlsl, Glsl = FilterGlsl, Msl = FilterMsl, Wgsl = FilterWgsl }, false,
        ComputeBindingType.StorageBufferRead, ComputeBindingType.SampledTexture3D, ComputeBindingType.SampledTexture3D,
        ComputeBindingType.StorageTexture3DWrite);

    public static ComputeKernelDesc CreateIntegrationKernel() => Kernel("fogIntegrate",
        new() { Hlsl = IntegrationHlsl, Glsl = IntegrationGlsl, Msl = IntegrationMsl, Wgsl = IntegrationWgsl }, true,
        ComputeBindingType.StorageBufferRead, ComputeBindingType.SampledTexture3D, ComputeBindingType.StorageTexture3DWrite);
}
