// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Microsoft.JSInterop;

namespace Season.Platforms.Web;

/// <summary>
/// Blazor Wasm platform entry point.
/// Initializes Web platform services, creates the WebGPU rendering context, and starts the render loop.
///
/// Pass orchestration overview (1-1): after BeginFrame in the frame loop, the fixed pass chain is driven by
/// the shared-layer FrameSchedule.Execute (Shadow→Scene→Post→FinalBlit, with zero overhead for empty slots).
/// Offscreen SceneColor is created and registered during initialization
/// (after Graphics.InitializeAsync and before the frame loop starts). The UseOffscreenSceneColor switch can
/// fall back to direct rendering for comparison. For the full index of WebGPU-specific platform rules, see
/// the class header in Platforms/Web/Graphics.cs.
/// </summary>
public static class WebApp
{
    static IJSInProcessRuntime _jsRuntime;
    static bool _running;
    static Graphics _graphics;
    static HttpClient _httpClient;
    static string _assetBasePath = string.Empty;
    static readonly TimeSpan _controlLoadBudgetPerFrame = TimeSpan.FromMilliseconds(3);

    /// <summary>
    /// Step 2 switch: render Scene into offscreen SceneColor and then present through FinalBlit.
    /// false = render directly to the backbuffer (Step 1 path; both modes are pixel-identical and useful for regression comparison).
    /// </summary>
    static readonly bool UseOffscreenSceneColor = true;

    /// <summary>
    /// HttpClient used by WebDeviceCore.LoadFileAsync for dynamic asset downloads.
    /// </summary>
    internal static HttpClient HttpClient => _httpClient;

    /// <summary>
    /// Called from a Blazor component with the current JSRuntime and canvas element ID.
    /// </summary>
    public static async Task Run(BaseApp app, IJSRuntime jsRuntime, HttpClient httpClient, string canvasId = "season-canvas", string? assetBasePath = null)
    {
        try
        {
            _jsRuntime = (IJSInProcessRuntime)jsRuntime;
            _httpClient = httpClient;
            _assetBasePath = NormalizeAssetBasePath(assetBasePath);

            DeviceServices.Initialize(
                baseApp: app,
                core: new WebDeviceCore(),
                media: new WebMediaPlayer(),
                dialog: new WebDialogService(),
                image: new WebImageService(_jsRuntime),
                video: new WebVideoPlayerService(_jsRuntime),
                file: new WebFileService(),
                gallery: null,
                record: null,
                download: null,
                store: new WebStoreService(),
                ads: null,
                windowsFeatures: null
            );
            
            // Finalize the HDR tier (1-4 Step A, mirroring WindowsApp/LinuxApp): the HDR chain depends on
            // offscreen SceneColor, so direct backbuffer rendering must fall back to LDR.
            // This must be decided before InitializeAsync because WebGPU injects WGSL variants and bakes the
            // main pipeline inside JS initialize, earlier than on the other platforms.
            Graphics.HdrSceneColor = UseOffscreenSceneColor && RenderQuality.Current.HdrSceneColor;

            // 2-1 Contract Clause 5: finalize the AA tier at initialization time
            // (single-choice and mutually exclusive; fall back with a log when unsupported, with zero runtime branching).
            // Msaa4x is a legacy D3D12 tier and becomes Fxaa here because this backend has no MSAA offscreen chain.
            // Fxaa depends on the HDR offscreen chain because the Post uber pass is the tonemap convergence point.
            // If that is unavailable, fall back to Off. Mirrors LinuxApp.
            if (RenderQuality.Current.AntiAliasing == Season.Rendering.AaMode.Msaa4x)
            {
                RenderQuality.Current.AntiAliasing = Season.Rendering.AaMode.Fxaa;
                DeviceServices.BaseApp.AddLog(LogType.None, $"{DateTime.UtcNow} [RenderQuality] AaMode.Msaa4x is supported only on D3D12; falling back to Fxaa");
            }
            if (RenderQuality.Current.AntiAliasing == Season.Rendering.AaMode.Taa)
            {
                // 2-3 Contract Clause 1: selecting Taa forces the velocity infrastructure to be enabled,
                // because TAA is invalid without velocity.
                // This assignment must happen before InitializeAsync because JS initialize already bakes the
                // VELOCITY_OUTPUT variant and SceneVelocity RT creation, both of which happen below.
                RenderQuality.Current.MotionVectors = true;

                // 2-3 Contract Clause 10: resolve runs in linear HDR space before tonemap
                // (both input and output are rgba16float), so it depends on the HDR offscreen chain.
                // If that is unavailable, fall back to Fxaa, whose own HDR dependency is checked in the next block.
                // Registration failure of TaaEffect itself does not trigger fallback here because it already
                // provides a bypass (TaaActive/SceneColorOverride stay false/null), so the image falls back to
                // non-TAA SceneColor without jitter (Clauses 14/15). Mirrors Apple/Linux.
                if (!Graphics.HdrSceneColor)
                {
                    RenderQuality.Current.AntiAliasing = Season.Rendering.AaMode.Fxaa;
                    DeviceServices.BaseApp.AddLog(LogType.None, $"{DateTime.UtcNow} [RenderQuality] Taa depends on the HDR offscreen chain (currently disabled); falling back to Fxaa while keeping MotionVectors enabled");
                }
            }
            if (RenderQuality.Current.AntiAliasing == Season.Rendering.AaMode.Fxaa && !Graphics.HdrSceneColor)
            {
                RenderQuality.Current.AntiAliasing = Season.Rendering.AaMode.Off;
                DeviceServices.BaseApp.AddLog(LogType.None, $"{DateTime.UtcNow} [RenderQuality] Fxaa depends on the HDR offscreen chain (currently disabled); falling back to Off");
            }

            _graphics = new Graphics(_jsRuntime, canvasId, httpClient, _assetBasePath);
            
            await _graphics.InitializeAsync();

            //WebDebug.SetEnabled(true, _jsRuntime);

            Season.Basic.Graphics.Instance = _graphics;

            // Offscreen SceneColor (Step 2): when non-null, FrameSchedule automatically appends a FinalBlit pass for presentation.
            if (UseOffscreenSceneColor)
            {
                Season.Rendering.FrameSchedule.SceneColor = _graphics.CreateRenderTarget(new Season.Rendering.RenderTargetDesc
                {
                    // 1-4 Step A: switch SceneColor to Rgba16Float on the HDR tier
                    // so the FinalBlit tonemap variant can converge there.
                    // When false, fall back to BackbufferCompatible, which is pixel-identical to the LDR baseline.
                    ColorFormat = Graphics.HdrSceneColor
                        ? Season.Rendering.RtFormat.Rgba16Float
                        : Season.Rendering.RtFormat.BackbufferCompatible,
                    MatchBackbufferSize = true,
                    SampleCount = 1,
                });
            }

            // 2-1 Step D (Contract Clause 4): enable the Post slot for the FXAA tier.
            // PostColor (LDR, same format and size as the backbuffer) and RenderPost
            // (uber: tonemap+bloom composition with luma written into alpha) are registered as a pair, after
            // which FrameSchedule automatically inserts the Post pass and FinalBlit degrades into FXAA present.
            // Outside the FXAA tier, both stay null so the chain leaves no residue. Mirrors LinuxApp.
            if (RenderQuality.Current.AntiAliasing == Season.Rendering.AaMode.Fxaa
                && Season.Rendering.FrameSchedule.SceneColor != null)
            {
                Season.Rendering.FrameSchedule.PostColor = _graphics.CreateRenderTarget(new Season.Rendering.RenderTargetDesc
                {
                    ColorFormat = Season.Rendering.RtFormat.BackbufferCompatible,
                    MatchBackbufferSize = true,
                    SampleCount = 1,
                });
                Season.Rendering.FrameSchedule.RenderPost = _graphics.RenderPostPass;
            }

            // 1-5 Shadow Atlas (Contract 2): D32Float with fixed ShadowAtlasSize² over four quadrants,
            // not rebuilt on resize.
            // ShadowMap + RenderShadow are registered as a pair to activate the Shadow pass
            // in FrameSchedule.Execute before Scene.
            // The atlas name is also registered on the JS side so binding 11 of the main-pass bind group can resolve it.
            if (RenderQuality.Current.ShadowsEnabled)
            {
                var shadowRt = _graphics.CreateRenderTarget(new Season.Rendering.RenderTargetDesc
                {
                    DepthFormat = Season.Rendering.RtFormat.D32Float,
                    MatchBackbufferSize = false,
                    Width = (uint)RenderQuality.Current.ShadowAtlasSize,
                    Height = (uint)RenderQuality.Current.ShadowAtlasSize,
                    SampleCount = 1,
                });
                Season.Rendering.FrameSchedule.ShadowMap = shadowRt;
                Season.Rendering.FrameSchedule.RenderShadow = _graphics.RenderShadowPass;
                WebGPUInterop.SetShadowAtlas(((WGPURenderTarget)shadowRt).Name);
            }

            // 2-2 Contract Clause 1: finalize the AO tier at initialization time
            // (single-choice and mutually exclusive; fall back with a log when unsupported).
            // It depends on the HDR offscreen chain and is incompatible with MSAA.
            // Once finalized, create SceneDepth as a full-size depth-only target.
            // MatchBackbufferSize maps to JS formatKind 3 = depth24plus so the dual-target Scene pass can rebind it.
            // Mirrors WindowsApp.
            if (RenderQuality.Current.AmbientOcclusion != Season.Rendering.AoMode.Off && !Graphics.HdrSceneColor)
            {
                RenderQuality.Current.AmbientOcclusion = Season.Rendering.AoMode.Off;
                DeviceServices.BaseApp.AddLog(LogType.None, $"{DateTime.UtcNow} [RenderQuality] AO depends on the HDR offscreen chain (currently disabled); falling back to Off");
            }
            if (RenderQuality.Current.AmbientOcclusion != Season.Rendering.AoMode.Off
                && RenderQuality.Current.AntiAliasing == Season.Rendering.AaMode.Msaa4x)
            {
                RenderQuality.Current.AmbientOcclusion = Season.Rendering.AoMode.Off;
                DeviceServices.BaseApp.AddLog(LogType.None, $"{DateTime.UtcNow} [RenderQuality] AO is incompatible with Msaa4x (MSAA depth cannot be used as compute input); falling back to Off");
            }
            if (RenderQuality.Current.AmbientOcclusion != Season.Rendering.AoMode.Off)
            {
                Season.Rendering.FrameSchedule.SceneDepth = _graphics.CreateRenderTarget(new Season.Rendering.RenderTargetDesc
                {
                    DepthFormat = Season.Rendering.RtFormat.D32Float,
                    MatchBackbufferSize = true,
                    SampleCount = 1,
                });
            }

            // 2-3 Contract Clause 2: SceneVelocity
            // (full-size Rg16Float, with JS formatKind 4 = rg16float and no depth).
            // When non-null, the Scene pass becomes a three-target pass
            // (MRT slot 0 = color / slot 1 = velocity / depth), and the clear value stays fixed at (0,0,0,0).
            // When MotionVectors is disabled, keep it null so PassDesc.VelocityTarget also becomes null and the
            // chain leaves no residue. Mirrors WindowsApp/LinuxApp.
            // This must be ready before BaseApp.Create, where the app registers VelocityViewEffect.
            if (RenderQuality.Current.MotionVectors)
            {
                Season.Rendering.FrameSchedule.SceneVelocity = _graphics.CreateRenderTarget(new Season.Rendering.RenderTargetDesc
                {
                    ColorFormat = Season.Rendering.RtFormat.Rg16Float,
                    MatchBackbufferSize = true,
                    SampleCount = 1,
                });
            }
            
            var jsSize = await jsRuntime.InvokeAsync<CanvasSize>("seasonWebGPU.getCanvasSize");
            
            DeviceServices.BaseApp.AddLog(LogType.None, $"{DateTime.UtcNow} [WebApp.Run] Canvas size from JS: {jsSize.Width}x{jsSize.Height}");
            
            if (jsSize.Width > 0 && jsSize.Height > 0)
            {
                app.ApplyResolution(jsSize.Width, jsSize.Height, 1f, 1f);
            }
            else
            {
                var bw = (int)app.BasicResolution.X;
                var bh = (int)app.BasicResolution.Y;

                app.ApplyResolution(bw, bh, 1f, 1f);
            }

            DeviceServices.BaseApp.AddLog(LogType.None, $"{DateTime.UtcNow} [WebApp.Run] ApplyResolution => DeviceResolution={app.DeviceResolution}, Scale={app.Scale}");

            DeviceServices.BaseApp.Create();

            _running = true;

            await StartRenderLoop();
        }
        catch (Exception ex)
        {
            DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} [WebApp] Fatal error: {ex}");
        }
    }

    /// <summary>
    /// Render loop: beginFrame → Update → Draw → endFrame.
    /// Driven by requestAnimationFrame and aligned with browser vsync (about 60 fps),
    /// replacing the setTimeout path behind Task.Delay(8). The old path had heavy jitter and unnecessary
    /// wake-ups every frame, which was one of the main causes of rendering stutter in Blazor Wasm.
    /// </summary>
    static async Task StartRenderLoop()
    {
        var stopWatch = System.Diagnostics.Stopwatch.StartNew();
        double previousSeconds = 0;
        double excludedTailSeconds = 0;

        // [FrameStat] Timing stats for the work section
        // (input→Update→Draw→EndFrame, excluding rAF wait time and end-of-frame control loading).
        // Log once every 600 frames as a baseline for comparing performance before and after the JSImport migration (Phase 1~3).
        double frameStatAccumMs = 0;
        double frameStatMaxMs = 0;
        int frameStatCount = 0;

        while (_running)
        {
            // Wait for the next vsync through rAF. [JSImport] marshals Task<double> directly from a JS Promise without a JSON layer.
            try
            {
                await WebGPUInterop.RequestFrame();
            }
            catch (Exception ex)
            {
                DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} [WebApp] requestFrame error: {ex}");
                await Task.Delay(16);
                continue;
            }

            try
            {
                double newSeconds = stopWatch.Elapsed.TotalSeconds;
                double deltaSeconds = newSeconds - previousSeconds - excludedTailSeconds;
                if (deltaSeconds < 0)
                    deltaSeconds = 0;
                previousSeconds = newSeconds;
                excludedTailSeconds = 0;
                float elapsed = (float)deltaSeconds;

                var app = DeviceServices.BaseApp;

                // Detect window resize: the JS side caches the new size in the resize event, and it is applied
                // atomically here at the beginning of the frame so WebGPU beginFrame and C# layout use the
                // same size within the same frame.
                // [JSImport] The packed variant returns [width, height], or [0, 0] when there is no resize.
                var resizeSize = WebGPUInterop.ApplyPendingResize();
                if (resizeSize[0] > 0 && resizeSize[1] > 0)
                {
                    DeviceServices.BaseApp.AddLog(LogType.None, $"{DateTime.UtcNow} [WebApp] Resize to {resizeSize[0]}x{resizeSize[1]}");
                    app.ApplyResolution(resizeSize[0], resizeSize[1], 1f, 1f);
                    app.Resize();
                }

                // Pull input synchronously: JS-side _input → TouchService
                // (matching the integration contract on Windows/Linux/Android).
                // poZDelta accumulates wheel/pinch input since the last poll and is cleared on the JS side after pollInput.
                PollInput(app);

                // Update the 3D camera (same semantics as DXPrimitiveGroup.Update, and it must run before BaseApp.Update).
                // 1-2: switch to passing EffectiveSceneLights
                // (dual-track UseSceneLights ? SceneLights : FromLegacy, with zero required changes for old apps).
                Graphics.UpdateCamera3D(app.CameraPos, app.CameraTarget, app.EffectiveSceneLights);

                // Update logic
                app.Update(elapsed);

                // Begin frame: clear the frame using BaseApp's current background color.
                var backgroundColor = app.BackgroundColor;
                _graphics.BeginFrame(backgroundColor.X, backgroundColor.Y, backgroundColor.Z, backgroundColor.W);

                _graphics.FlushTextAtlas();

                // Pass orchestration (1-1 Step 1): Begin/End of the Scene pass is driven by FrameSchedule.
                // SceneColor = null means direct backbuffer rendering, which is pixel-identical to the old single-pass behavior.
                Season.Rendering.FrameSchedule.Execute(_graphics, app, backgroundColor);

                // End frame: submit rendering commands.
                _graphics.EndFrame();

                double workMs = (stopWatch.Elapsed.TotalSeconds - newSeconds) * 1000.0;
                frameStatAccumMs += workMs;
                if (workMs > frameStatMaxMs) frameStatMaxMs = workMs;
                if (++frameStatCount >= 600)
                {
                    app.AddLog(LogType.None, $"{DateTime.UtcNow} [FrameStat] avg={frameStatAccumMs / frameStatCount:F2}ms max={frameStatMaxMs:F2}ms over {frameStatCount} frames");
                    frameStatAccumMs = 0;
                    frameStatMaxMs = 0;
                    frameStatCount = 0;
                }

                // Let the first frame appear first, then process queued control loading progressively under budget in later frames.
                var tailStartSeconds = stopWatch.Elapsed.TotalSeconds;
                
                await BaseApp.ProcessControlQueueFrame(_controlLoadBudgetPerFrame);

                // Control loading happens at the end of the frame, so its cost must not be counted into the
                // animation delta of the next frame.
                // But the time spent in this frame's Update/Draw/EndFrame must be kept, or the entire app will look like slow motion.
                excludedTailSeconds = stopWatch.Elapsed.TotalSeconds - tailStartSeconds;
            }
            catch (Exception ex)
            {
                DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} [WebApp] Render loop error: {ex}");
            }
        }
    }

    /// <summary>
    /// Stops the current Web render loop. Intended to be called when the host component is unloaded.
    /// </summary>
    public static void Stop()
    {
        _running = false;
    }

    static string NormalizeAssetBasePath(string? assetBasePath)
    {
        if (string.IsNullOrWhiteSpace(assetBasePath))
            return string.Empty;

        return assetBasePath.Trim().Trim('/');
    }

    internal static string ResolveAssetPath(string assetName)
    {
        if (string.IsNullOrWhiteSpace(assetName))
            return assetName;

        if (Uri.TryCreate(assetName, UriKind.Absolute, out _))
            return assetName;

        var relativePath = assetName.TrimStart('/');
        if (string.IsNullOrEmpty(_assetBasePath))
            return relativePath;

        return $"{_assetBasePath}/{relativePath}";
    }

    /// <summary>
    /// Pulls an input snapshot from the JS side each frame and writes it into <see cref="TouchService"/>,
    /// equivalent to writing TouchService directly from event callbacks on Windows/Linux/Android.
    /// JS-side <c>poX/poY</c> are backing-pixel coordinates. Dividing them by <see cref="BaseApp.Scale"/>
    /// yields BasicResolution coordinates, matching the behavior of WindowsApp.PointerMoved.
    /// </summary>
    static void PollInput(BaseApp app)
    {
        try
        {
            // [JSImport] Packed variant: [isDown(0/1), poX, poY, poZDelta], bypassing JSON deserialization.
            var snap = WebGPUInterop.PollInput();

            float scale = app.Scale > 0f ? app.Scale : 1f;
            TouchService.PoX = (int)(snap[1] / scale);
            TouchService.PoY = (int)(snap[2] / scale);
            TouchService.isDown = snap[0] != 0;

            if (snap[3] != 0)
            {
                if (TouchService.PoZ is null) TouchService.PoZ = 0;
                TouchService.PoZ += (int)snap[3];
            }
        }
        catch (Exception ex)
        {
            DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} [WebApp.PollInput] error: {ex.Message}");
        }
    }

    /// <summary>
    /// Canvas size structure returned by the JS side
    /// (only the initialization-time getCanvasSize call still uses the IJSRuntime JSON path).
    /// </summary>
    struct CanvasSize
    {
        public int Width { get; set; }
        public int Height { get; set; }
    }
}
