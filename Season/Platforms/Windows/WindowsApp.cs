// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

//using Microsoft.Graphics.Display;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Season.Platforms.Windows.DirectX;
using Silk.NET.Direct3D12;
using Silk.NET.Core.Native;
using Windows.Graphics;
using Windows.Services.Store;
using Windows.System.Threading;
using ThreadPool = Windows.System.Threading.ThreadPool;

namespace Season.Platforms.Windows;

public static class WindowsApp
{
    internal static Microsoft.UI.Xaml.Window Window = null;

    internal static Microsoft.UI.Windowing.AppWindow AppWindow;

    internal static StoreContext StoreContext;

    static bool sizeChanged;

    static DateTime? _lastSwapChainChangeTime;

    const double SizeSettleDurationSeconds = 0.5f;

    static bool firstTime = true;

    static bool _applyingWindowState;

    static bool _closing;

    static bool _startupRestorePending;

    static int ConvertLogicalToPhysicalPixels(double logicalSize, float compositionScale)
    {
        if (logicalSize <= 0)
        {
            return 0;
        }

        return Math.Max(1, (int)Math.Ceiling(logicalSize * compositionScale));
    }

    public static void Run(BaseApp app)
    {
        _closing = false;

        DeviceServices.Initialize(
            baseApp: app,
            core: new WindowsDeviceCore(),
            media: new WindowsMediaPlayer(),
            dialog: new WindowsDialogService(),
            file: new WindowsFileService(),
            image: new WindowsImageService(),
            video: new WindowsVideoPlayerService(),
            gallery: new WindowsGalleryService(),
            record: new WindowsRecordService(),
            download: new WindowsDownloadService(),
            store: new WindowsStoreService(),
            ads: null,
            windowsFeatures: new WindowsFeatures());

        var birate = VideoEncodingHelper.EstimateBitrate(832, 480, 16, 90);

        Window = new Microsoft.UI.Xaml.Window();

        var dispatcherQueue = WindowsApp.Window?.DispatcherQueue;

        Window.Title = app.Title;
        
        Window.Activated += (sender, e) =>
        {
            var isActive = e.WindowActivationState != WindowActivationState.Deactivated;

            if (DeviceServices.BaseApp.IsActive == isActive)
            {

            }
            else
            {
                if (DeviceServices.BaseApp.IsActive)
                {
                    DeviceServices.BaseApp.LastInActiveTime = DateTime.Now;
                }
                else
                {
                    DeviceServices.BaseApp.LastActiveTime = DateTime.Now;
                }

                DeviceServices.BaseApp.IsActive = isActive;
            }
        };

        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(Window);

        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(windowHandle);

        AppWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

        AppWindow.SetIcon(@"Assets/favicon.ico");
        
        var swapChainPanel = new Microsoft.UI.Xaml.Controls.SwapChainPanel();

        //var sc = XamlRoot.RasterizationScale;

        AppWindow.Changed += (sender, e) =>
        {
            if (_applyingWindowState || _closing)
                return;

            if (!e.DidSizeChange && !e.DidPositionChange && !e.DidPresenterChange)
                return;

            if (_startupRestorePending)
            {
                _startupRestorePending = false;

                LogWindowState(
                    "Restore",
                    $"source=StartupChangedReplay actualPos=({AppWindow.Position.X},{AppWindow.Position.Y}) " +
                    $"actualSize=({AppWindow.Size.Width},{AppWindow.Size.Height})");

                if (app.Settings.WindowState.Maximized && IsWindowCurrentlyMaximized())
                {
                    LogWindowState("Restore", "source=StartupChangedReplay skipped=AlreadyMaximized");
                    return;
                }

                ApplyWindowState(app, "StartupChangedReplay");
                return;
            }

            SaveWindowState(immediate: false, source: $"Changed size={e.DidSizeChange} pos={e.DidPositionChange} presenter={e.DidPresenterChange}");
        };

        AppWindow.Closing += (sender, e) =>
        {
            if (_closing)
                return;

            _closing = true;
            SaveWindowState(immediate: true, source: "Closing");
        };

        Window.Closed += (sender, e) =>
        {
            DeviceServices.BaseApp.Status ??= "Closed";
            LogWindowState(
                "Save",
                $"source=ClosedFlush immediate=True " +
                $"saved=({DeviceServices.BaseApp.Settings.WindowState.X},{DeviceServices.BaseApp.Settings.WindowState.Y}," +
                $"{DeviceServices.BaseApp.Settings.WindowState.Width},{DeviceServices.BaseApp.Settings.WindowState.Height}) " +
                $"savedMax={DeviceServices.BaseApp.Settings.WindowState.Maximized} " +
                $"savedFull={DeviceServices.BaseApp.Settings.WindowState.FullScreen}");
            DeviceServices.BaseApp.SaveSettings();
            DeviceServices.BaseApp.DisposeSaveSettingsRequest();
        };

        Window.Content = swapChainPanel;

        _startupRestorePending = app.Settings.WindowState.Width > 0 ||
            app.Settings.WindowState.Height > 0 ||
            app.Settings.WindowState.Maximized ||
            app.Settings.WindowState.FullScreen;

        ApplyWindowState(app, "Startup");

        Window.Activate();

        dispatcherQueue?.TryEnqueue(() =>
        {
            if (!_startupRestorePending || _closing)
                return;

            _startupRestorePending = false;

            LogWindowState(
                "Restore",
                $"source=PostActivateReplay actualPos=({AppWindow.Position.X},{AppWindow.Position.Y}) " +
                $"actualSize=({AppWindow.Size.Width},{AppWindow.Size.Height})");

            if (app.Settings.WindowState.Maximized && IsWindowCurrentlyMaximized())
            {
                LogWindowState("Restore", "source=PostActivateReplay skipped=AlreadyMaximized");
                return;
            }

            ApplyWindowState(app, "PostActivateReplay");
        });

        swapChainPanel.PointerPressed += (s, e) =>
        {
            TouchService.isDown = true;
        };

        swapChainPanel.PointerReleased += (s, e) =>
        {
            TouchService.isDown = false;
        };

        swapChainPanel.PointerMoved += (s, e) =>
        {
            var currentPoint = e.GetCurrentPoint(s as UIElement);

            var pos = currentPoint.Position;

            var scale = DeviceServices.BaseApp.Scale > 0f ? DeviceServices.BaseApp.Scale : 1f;

            TouchService.PoX = (int)Math.Round(pos.X * DeviceServices.BaseApp.CompositionScale.X / scale);

            TouchService.PoY = (int)Math.Round(pos.Y * DeviceServices.BaseApp.CompositionScale.Y / scale);
        };

        swapChainPanel.PointerWheelChanged += (s, e) =>
        {
            var currentPoint = e.GetCurrentPoint(s as UIElement);

            if (currentPoint.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse)
            {
                //var direction = ((currentPoint.Properties.MouseWheelDelta <= 0) ? MouseScrollDirections.Down : MouseScrollDirections.Up);

                if (TouchService.PoZ is null)
                {
                    TouchService.PoZ = 0;
                }

                TouchService.PoZ -= currentPoint.Properties.MouseWheelDelta;
            }
        };

        swapChainPanel.SizeChanged += (s, e) =>
        {
            lock (swapChainPanel)
            {
                var backBufferWidth = ConvertLogicalToPhysicalPixels(swapChainPanel.ActualWidth, swapChainPanel.CompositionScaleX);
                var backBufferHeight = ConvertLogicalToPhysicalPixels(swapChainPanel.ActualHeight, swapChainPanel.CompositionScaleY);

                if (swapChainPanel.ActualWidth <= 0 || swapChainPanel.ActualHeight <= 0 || backBufferWidth <= 0 || backBufferHeight <= 0
                    || swapChainPanel.CompositionScaleX < 0.01f || swapChainPanel.CompositionScaleY < 0.01f)
                {
                    return;
                }

                DeviceServices.BaseApp.ApplyResolution(backBufferWidth, backBufferHeight, swapChainPanel.CompositionScaleX, swapChainPanel.CompositionScaleY);

                if (firstTime)
                {
                    firstTime = false;

                    CreateInstance(swapChainPanel);
                }
                else
                {
                    sizeChanged = true;

                    _lastSwapChainChangeTime = DateTime.Now;

                    //var logs = String.Join("\r\n", DeviceServices.BaseApp.Logs);
                    //File.WriteAllText(@"D:\Surface\log.txt", logs);
                }
            }
        };

        swapChainPanel.CompositionScaleChanged += (s, e) =>
        {

        };

        StoreContext = StoreContext.GetDefault();

        WinRT.Interop.InitializeWithWindow.Initialize(StoreContext, windowHandle);
    }

    static void ApplyWindowState(BaseApp app, string source = "Unknown")
    {
        var windowState = app.Settings.WindowState;
        var primaryWorkArea = DisplayArea.Primary.WorkArea;

        _applyingWindowState = true;

        try
        {
            if (windowState.FullScreen)
            {
                AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                return;
            }

            AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);

            bool hasSavedBounds = windowState.Width > 0 && windowState.Height > 0;
            DisplayArea targetDisplayArea = DisplayArea.Primary;
            int width = windowState.Width > 0 ? windowState.Width : primaryWorkArea.Width / 2;
            int height = windowState.Height > 0 ? windowState.Height : primaryWorkArea.Height / 2;

            if (width < DeviceServices.BaseApp.BasicResolution.X)
            {
                width = (int)DeviceServices.BaseApp.BasicResolution.X;
            }
            if (height < DeviceServices.BaseApp.BasicResolution.Y)
            {
                height = (int)DeviceServices.BaseApp.BasicResolution.Y;
            }

            int x = primaryWorkArea.X + (primaryWorkArea.Width - width) / 2;
            int y = primaryWorkArea.Y + (primaryWorkArea.Height - height) / 2;
            bool useSavedRect = false;

            if (hasSavedBounds)
            {
                var savedRect = new RectInt32(windowState.X, windowState.Y, width, height);

                if (TryGetVisibleDisplayArea(savedRect, out DisplayArea displayArea))
                {
                    targetDisplayArea = displayArea;
                    x = windowState.X;
                    y = windowState.Y;
                    useSavedRect = true;
                }
            }

            var rect = new RectInt32(x, y, width, height);
            rect = useSavedRect
                ? rect
                : CenterRectInWorkArea(rect.Width, rect.Height, targetDisplayArea.WorkArea);

            LogWindowState(
                "Restore",
                $"source={source} " +
                $"saved=({windowState.X},{windowState.Y},{windowState.Width},{windowState.Height}) " +
                $"savedMax={windowState.Maximized} savedFull={windowState.FullScreen} " +
                $"actualBefore=({AppWindow.Position.X},{AppWindow.Position.Y},{AppWindow.Size.Width},{AppWindow.Size.Height}) " +
                $"targetWork={FormatRect(targetDisplayArea.WorkArea)} final={FormatRect(rect)}");

            AppWindow.MoveAndResize(rect);

            if (windowState.Maximized && AppWindow.Presenter is OverlappedPresenter overlappedPresenter)
            {
                overlappedPresenter.Maximize();
            }
        }
        finally
        {
            _applyingWindowState = false;
        }
    }

    static bool TryGetVisibleDisplayArea(RectInt32 rect, out DisplayArea displayArea)
    {
        displayArea = DisplayArea.GetFromRect(rect, DisplayAreaFallback.None);

        if (displayArea is null)
        {
            return false;
        }

        int minVisibleWidth = Math.Min(rect.Width, 100);
        int minVisibleHeight = Math.Min(rect.Height, 100);

        return GetIntersectionWidth(rect, displayArea.WorkArea) >= minVisibleWidth
            && GetIntersectionHeight(rect, displayArea.WorkArea) >= minVisibleHeight;
    }

    static bool IsWindowCurrentlyMaximized()
        => AppWindow.Presenter is OverlappedPresenter overlappedPresenter
            && overlappedPresenter.State == OverlappedPresenterState.Maximized;

    static RectInt32 CenterRectInWorkArea(int width, int height, RectInt32 workArea)
    {
        width = ClampDimension(width, workArea.Width, 320);
        height = ClampDimension(height, workArea.Height, 240);

        return new RectInt32(
            workArea.X + (workArea.Width - width) / 2,
            workArea.Y + (workArea.Height - height) / 2,
            width,
            height);
    }

    static int ClampDimension(int value, int max, int preferredMin)
    {
        if (max <= 0)
        {
            return value;
        }

        return Math.Min(Math.Max(value, Math.Min(preferredMin, max)), max);
    }

    static int GetIntersectionWidth(RectInt32 a, RectInt32 b)
        => Math.Max(0, Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X));

    static int GetIntersectionHeight(RectInt32 a, RectInt32 b)
        => Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y));

    static void SaveWindowState(bool immediate, string source)
    {
        var app = DeviceServices.BaseApp;
        var windowState = app.Settings.WindowState;

        windowState.FullScreen = AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;

        if (windowState.FullScreen)
        {
            windowState.Maximized = false;
            if (immediate)
            {
                app.SaveSettings();
            }
            else
            {
                app.RequestSaveSettings();
            }
            return;
        }

        if (AppWindow.Presenter is OverlappedPresenter overlappedPresenter)
        {
            windowState.Maximized = overlappedPresenter.State == OverlappedPresenterState.Maximized;
        }
        else
        {
            windowState.Maximized = false;
        }

        if (!windowState.Maximized && AppWindow.Position.X > 0 && AppWindow.Position.Y > 0)
        {
            windowState.X = AppWindow.Position.X;
            windowState.Y = AppWindow.Position.Y;
            windowState.Width = AppWindow.Size.Width;
            windowState.Height = AppWindow.Size.Height;
        }

        LogWindowState(
            "Save",
            $"source={source} immediate={immediate} " +
            $"appPos=({AppWindow.Position.X},{AppWindow.Position.Y}) appSize=({AppWindow.Size.Width},{AppWindow.Size.Height}) " +
            $"saved=({windowState.X},{windowState.Y},{windowState.Width},{windowState.Height}) " +
            $"savedMax={windowState.Maximized} savedFull={windowState.FullScreen}");

        if (immediate)
        {
            app.SaveSettings();
        }
        else
        {
            app.RequestSaveSettings();
        }
    }

    static string FormatRect(RectInt32 rect)
        => $"({rect.X},{rect.Y},{rect.Width},{rect.Height})";

    static void LogWindowState(string stage, string message)
    {
        DeviceServices.BaseApp.AddLog(LogType.None, $"{DateTime.UtcNow} [WindowState][{stage}] {message}");
    }

    /// <summary>
    /// Step 2 switch: render Scene into the offscreen SceneColor, then present through FinalBlit.
    /// false = render directly to the backbuffer (the Step 1 path; both modes should stay
    /// pixel-identical for regression comparison).
    /// </summary>
    static readonly bool UseOffscreenSceneColor = true;

    static async void CreateInstance(SwapChainPanel swapChainPanel)
    {
        Season.Basic.Graphics.Instance = new Graphics();

        // 1-4 quality tiering (owned by shared-layer RenderQuality starting from Step C;
        // see its summary for the cross-platform contract):
        // must be finalized before CreateSwapChain (Display/MSAA target formats) and
        // Pipeline.Init (PSO baking). The HDR path depends on offscreen SceneColor
        // because tone mapping is closed in FinalBlit; direct rendering must therefore
        // fall back to the LDR baseline.
        DirectX.Device.HdrSceneColor = UseOffscreenSceneColor && RenderQuality.Current.HdrSceneColor;

        // 2-1 contract clause 5: finalize the AA tier during initialization
        // (mutually exclusive single-choice; fall back and log when unsupported, with zero runtime branching).
        // This must happen before CreateSwapChain because the Display MSAA sample count is derived
        // from the finalized tier. Taa/Fxaa both depend on the HDR offscreen path:
        // Taa fallback -> Fxaa, and Fxaa fallback -> Off.
        if (RenderQuality.Current.AntiAliasing == Season.Rendering.AaMode.Taa)
        {
            // 2-3 contract clause 1: selecting Taa forces the velocity path to be enabled
            // because TAA is invalid without velocity.
            RenderQuality.Current.MotionVectors = true;

            // 2-3 contract clause 10: resolve runs in linear HDR space before tone mapping
            // (both input and output are rgba16float), so it depends on the HDR offscreen path.
            // If unavailable, fall back to Fxaa here, and let the next block continue validating
            // Fxaa's own HDR dependency.
            // Note: the Taa tier does not create PostColor (only the Fxaa tier does below), so
            // composition still happens in FinalBlit, where TaaEffect output is injected through
            // SceneColorOverride (clause 12).
            // Registration failure of TaaEffect itself does not trigger fallback here because it
            // has its own bypass path (TaaActive/SceneColorOverride remain false/null), so the
            // image falls back to non-TAA SceneColor without jitter (clauses 14/15).
            if (!DirectX.Device.HdrSceneColor)
            {
                RenderQuality.Current.AntiAliasing = Season.Rendering.AaMode.Fxaa;
                DeviceServices.BaseApp.AddLog(LogType.None, $"{DateTime.UtcNow} [RenderQuality] Taa requires the HDR offscreen path (currently disabled), falling back to Fxaa; MotionVectors remains enabled");
            }
        }
        if (RenderQuality.Current.AntiAliasing == Season.Rendering.AaMode.Fxaa && !DirectX.Device.HdrSceneColor)
        {
            RenderQuality.Current.AntiAliasing = Season.Rendering.AaMode.Off;
            DeviceServices.BaseApp.AddLog(LogType.None, $"{DateTime.UtcNow} [RenderQuality] Fxaa requires the HDR offscreen path (currently disabled), falling back to Off");
        }

        // 2-3 contract clauses 1/8: finalize the MotionVectors tier during initialization,
        // before Pipeline.Init. The main shader's VELOCITY_OUTPUT variant and the PSO's
        // NumRenderTargets/RTVFormats[1] are both derived from it, so it must not change at runtime.
        // It is mutually exclusive with Msaa4x because all MRT attachments must use the same
        // sample count, and multisampled color cannot be bound together with single-sampled velocity.
        if (RenderQuality.Current.MotionVectors
            && RenderQuality.Current.AntiAliasing == Season.Rendering.AaMode.Msaa4x)
        {
            RenderQuality.Current.MotionVectors = false;
            DeviceServices.BaseApp.AddLog(LogType.None, $"{DateTime.UtcNow} [RenderQuality] MotionVectors is mutually exclusive with Msaa4x (all MRT attachments must use the same sample count), falling back to false");
        }

        DeviceServices.BaseApp.AddLog(LogType.None, $"{DateTime.UtcNow} [Init] Device.Init begin");
        try
        {
            DirectX.Device.Init(true);
        }
        catch (Exception ex)
        {
            DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} [Init] Device.Init failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            return;
        }
        DeviceServices.BaseApp.AddLog(LogType.None, $"{DateTime.UtcNow} [Init] Device.Init done");

        // The optimized clear value for offscreen RT / MSAA targets is baked from the background
        // color at creation time (the Scene pass uses the same clear color every frame).
        // Synchronize the app background color during initialization first
        // (Device.Init defaults to white), otherwise any non-white background will trigger
        // CLEARRENDERTARGETVIEW_MISMATCHINGCLEARVALUE every frame and degrade into a slow clear.
        DirectX.Device.BackgroundColor = DeviceServices.BaseApp.BackgroundColor;

        DeviceServices.BaseApp.AddLog(LogType.None, $"{DateTime.UtcNow} [Init] CreateSwapChain begin");
        try
        {
            DirectX.Device.CreateSwapChain((int)DeviceServices.BaseApp.DeviceResolution.X, (int)DeviceServices.BaseApp.DeviceResolution.Y);
        }
        catch (Exception ex)
        {
            DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} [Init] CreateSwapChain failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            return;
        }
        DeviceServices.BaseApp.AddLog(LogType.None, $"{DateTime.UtcNow} [Init] CreateSwapChain done");

        DirectX.Device.CreateDescriptorHeapsAndViews();

        DeviceServices.BaseApp.AddLog(LogType.None, $"{DateTime.UtcNow} [Init] Pipeline.Init begin");
        try
        {
            Pipeline.Init();
        }
        catch (Exception ex)
        {
            DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} [Init] Pipeline.Init failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            return;
        }
        DeviceServices.BaseApp.AddLog(LogType.None, $"{DateTime.UtcNow} [Init] Pipeline.Init done");

        // Startup shader budget: Pipeline.Init plus the nested BlitPipeline.Init request far more shader
        // compilations than there are distinct sources, and every real fxc call sits in front of the first
        // frame. Reporting misses versus hits makes the dedup ratio verifiable without a Release build.
        DeviceServices.BaseApp.AddLog(LogType.None, $"{DateTime.UtcNow} [ShaderCache][graphics] fxc compiles={DirectX.ShaderCompiler.CompileCount}, cache hits={DirectX.ShaderCompiler.CacheHitCount}");

        // Global shared lighting CB: both the Pbr3D path and DXSpriteQuad's b1 read from it,
        // so it must be initialized before DXSprite2D.Init / DeviceServices.BaseApp.Create() load resources.
        DXPrimitiveGroup.InitLights();

        DXSprite2D.Init();

        DirectX.Device.CreateGraphicsCommandLists();

        // Offscreen SceneColor (Step 2): when non-null, FrameSchedule automatically appends
        // the FinalBlit pass for presentation.
        // 1-4 Step A: use RGBA16F in the HDR path (FinalBlit switches to the tone-mapping variant automatically).
        if (UseOffscreenSceneColor)
        {
            Season.Rendering.FrameSchedule.SceneColor = Season.Basic.Graphics.Instance.CreateRenderTarget(new Season.Rendering.RenderTargetDesc
            {
                ColorFormat = DirectX.Device.HdrSceneColor
                    ? Season.Rendering.RtFormat.Rgba16Float
                    : Season.Rendering.RtFormat.BackbufferCompatible,
                MatchBackbufferSize = true,
                SampleCount = 1,
            });
        }

        // 2-3 contract clause 2: SceneVelocity (full-size rg16float). When non-null,
        // the Scene pass uses three targets (color + velocity + depth); when MotionVectors is off,
        // keep this null so the path leaves no residue.
        // It must be ready before BaseApp.Create (where the app registers VelocityViewEffect).
        if (RenderQuality.Current.MotionVectors)
        {
            Season.Rendering.FrameSchedule.SceneVelocity = Season.Basic.Graphics.Instance.CreateRenderTarget(new Season.Rendering.RenderTargetDesc
            {
                ColorFormat = Season.Rendering.RtFormat.Rg16Float,
                MatchBackbufferSize = true,
                SampleCount = 1,
            });
        }

        // 2-1 Step C (contract clause 4): enable the Post slot for the FXAA tier.
        // Once PostColor (LDR, same format and size as the backbuffer) and RenderPost
        // (uber pass: tonemap + bloom composition, luma written into alpha) are registered as a pair,
        // FrameSchedule inserts the Post pass automatically and FinalBlit degenerates into FXAA presentation.
        // In non-FXAA tiers both remain null, leaving no residue in the pipeline.
        if (RenderQuality.Current.AntiAliasing == Season.Rendering.AaMode.Fxaa
            && Season.Rendering.FrameSchedule.SceneColor != null)
        {
            Season.Rendering.FrameSchedule.PostColor = Season.Basic.Graphics.Instance.CreateRenderTarget(new Season.Rendering.RenderTargetDesc
            {
                ColorFormat = Season.Rendering.RtFormat.BackbufferCompatible,
                MatchBackbufferSize = true,
                SampleCount = 1,
            });
            if (Season.Basic.Graphics.Instance is Graphics postGraphics)
                Season.Rendering.FrameSchedule.RenderPost = postGraphics.RenderPostPass;
        }

        // 1-5 Shadow atlas (depth-only D32Float, fixed ShadowAtlasSize^2 and not resized; contract clause 2):
        // once ShadowMap + RenderShadow are registered as a pair during initialization,
        // FrameSchedule activates the Shadow pass before Scene.
        if (RenderQuality.Current.ShadowsEnabled)
        {
            Season.Rendering.FrameSchedule.ShadowMap = Season.Basic.Graphics.Instance.CreateRenderTarget(new Season.Rendering.RenderTargetDesc
            {
                DepthFormat = Season.Rendering.RtFormat.D32Float,
                MatchBackbufferSize = false,
                Width = (uint)RenderQuality.Current.ShadowAtlasSize,
                Height = (uint)RenderQuality.Current.ShadowAtlasSize,
                SampleCount = 1,
            });
            if (Season.Basic.Graphics.Instance is Graphics shadowGraphics)
                Season.Rendering.FrameSchedule.RenderShadow = shadowGraphics.RenderShadowPass;
        }

        // 2-2 contract clause 1: finalize the AO tier during initialization
        // (mutually exclusive single-choice; fall back and log when unsupported).
        // It depends on the HDR offscreen path (AO is multiplied in at composition time)
        // and is mutually exclusive with MSAA (depth cannot be used directly as compute input).
        // After finalization, create SceneDepth (full-size depth-only, explicit DepthTarget for
        // the Scene pass, and depth input for compute).
        if (RenderQuality.Current.AmbientOcclusion != Season.Rendering.AoMode.Off && !DirectX.Device.HdrSceneColor)
        {
            RenderQuality.Current.AmbientOcclusion = Season.Rendering.AoMode.Off;
            DeviceServices.BaseApp.AddLog(LogType.None, $"{DateTime.UtcNow} [RenderQuality] AO requires the HDR offscreen path (currently disabled), falling back to Off");
        }
        if (RenderQuality.Current.AmbientOcclusion != Season.Rendering.AoMode.Off
            && RenderQuality.Current.AntiAliasing == Season.Rendering.AaMode.Msaa4x)
        {
            RenderQuality.Current.AmbientOcclusion = Season.Rendering.AoMode.Off;
            DeviceServices.BaseApp.AddLog(LogType.None, $"{DateTime.UtcNow} [RenderQuality] AO is mutually exclusive with Msaa4x (MSAA depth cannot be used as compute input), falling back to Off");
        }
        if (RenderQuality.Current.AmbientOcclusion != Season.Rendering.AoMode.Off)
        {
            Season.Rendering.FrameSchedule.SceneDepth = Season.Basic.Graphics.Instance.CreateRenderTarget(new Season.Rendering.RenderTargetDesc
            {
                DepthFormat = Season.Rendering.RtFormat.D32Float,
                MatchBackbufferSize = true,
                SampleCount = 1,
            });
        }

        DeviceServices.BaseApp.Create();

        // Create() registers the compute effects, so this second reading covers graphics plus every kernel that
        // has to be compiled before the frame loop starts.
        DeviceServices.BaseApp.AddLog(LogType.None, $"{DateTime.UtcNow} [ShaderCache][total] fxc compiles={DirectX.ShaderCompiler.CompileCount}, cache hits={DirectX.ShaderCompiler.CacheHitCount}");

        WorkItemHandler handler = delegate
        {
            var synchronizationContext = new SynchronizationContext();

            SynchronizationContext.SetSynchronizationContext(synchronizationContext);

            var clockTimer = Stopwatch.StartNew();

            var total = 0f;

            while (!_closing && DeviceServices.BaseApp.Status is null)
            {
                var gameTime = clockTimer.Elapsed;

                float elapsed = (float)gameTime.TotalSeconds;

                total += elapsed;

                clockTimer.Restart();

                if (sizeChanged)
                {
                    // Rebuild SwapChain/Display/RTV/DSV on the render thread
                    // to avoid GPU resource races with the UI thread's SizeChanged event.
                    // HandleResize returning false means ResizeSemaphore timed out
                    // (background Load is holding the lock) and the GPU is not idle yet.
                    // Resize() must not be driven in that state because ResizeCompute would destroy
                    // and recreate storage resources that may still be in flight, so keep
                    // sizeChanged set and retry on the next frame.
                    if (DirectX.Device.HandleResize((int)DeviceServices.BaseApp.DeviceResolution.X, (int)DeviceServices.BaseApp.DeviceResolution.Y))
                    {
                        DeviceServices.BaseApp?.Resize();

                        sizeChanged = false;
                    }
                }

                if (_lastSwapChainChangeTime is null)
                {

                }
                else
                {
                    if ((DateTime.Now - (DateTime)_lastSwapChainChangeTime).TotalSeconds >= SizeSettleDurationSeconds)
                    {
                        _lastSwapChainChangeTime = null;

                        //DeviceServices.BaseApp?.ResizeContent();
                    }
                }

                DXPrimitiveGroup.Update(elapsed, DeviceServices.BaseApp.CameraPos, DeviceServices.BaseApp.CameraTarget, DeviceServices.BaseApp.EffectiveSceneLights);

                if (Season.Basic.Graphics.Instance is Graphics textFrameGraphics)
                {
                    textFrameGraphics.BeginTextFrame();
                }

                DeviceServices.BaseApp.Update(elapsed);

                if (_closing || DeviceServices.BaseApp.Status is not null)
                {
                    break;
                }

                var backgroundColor = DeviceServices.BaseApp.BackgroundColor;
                DirectX.Device.BackgroundColor = backgroundColor;
                DirectX.Device.Display?.SetClearColor(backgroundColor);

                DirectX.Device.BeforeRender();

                Season.Basic.Graphics.Instance.FlushTextAtlas();

                // Pass scheduling (Step 1): FrameSchedule drives Begin/End for the Scene pass.
                Season.Rendering.FrameSchedule.Execute(Season.Basic.Graphics.Instance, DeviceServices.BaseApp, backgroundColor);

                DirectX.Device.AfterRender();

                if (Season.Basic.Graphics.Instance is Graphics windowsGraphics)
                {
                    windowsGraphics.PumpDeferredReleases();
                }

                //if (DeviceServices.BaseApp.TexturesCreated && !DeviceServices.BaseApp.FirstRendered)
                //{
                //    DeviceServices.BaseApp.FirstRendered = true;
                //}
            }

            if (DirectX.Device.CanWaitForGpu())
            {
                DirectX.Device.WaitForGpu();
            }

            // Shutdown: the GPU has finished all commands, but the FrameContext command allocators
            // may still hold references to texture resources from the previous frame's render commands.
            // All allocators must be reset before PumpDeferredReleases, otherwise the Debug Layer
            // will refuse Release() because the resources are still referenced.
            DirectX.Device.ResetAllAllocatorsForShutdown();

            if (Season.Basic.Graphics.Instance is Graphics shutdownGraphics)
            {
                shutdownGraphics.PumpDeferredReleases(force: true);
            }
        };

        await ThreadPool.RunAsync(handler, WorkItemPriority.High, WorkItemOptions.TimeSliced);
    }

}

//static RectInt32 ClampRectToWorkArea(RectInt32 rect, RectInt32 workArea)
//{
//    int width = ClampDimension(rect.Width, workArea.Width, 320);
//    int height = ClampDimension(rect.Height, workArea.Height, 240);
//    int maxX = workArea.X + workArea.Width - width;
//    int maxY = workArea.Y + workArea.Height - height;
//    int x = Math.Max(workArea.X, Math.Min(rect.X, maxX));
//    int y = Math.Max(workArea.Y, Math.Min(rect.Y, maxY));

//    return new RectInt32(x, y, width, height);
//}

//var di = DisplayInformation.GetForCurrentView();
//int rawW = (int)di.ScreenWidthInRawPixels;   // Physical pixels, for example 3840
//int rawH = (int)di.ScreenHeightInRawPixels;  // Physical pixels, for example 2160
//var deviceResolution = DeviceServices.BaseApp.DeviceResolution;
//if ((int)deviceResolution.X != backBufferWidth || (int)deviceResolution.Y != backBufferHeight)
//{
//    DeviceServices.BaseApp.ApplyResolution(backBufferWidth, backBufferHeight);
//    layoutChanged = true;
//}
