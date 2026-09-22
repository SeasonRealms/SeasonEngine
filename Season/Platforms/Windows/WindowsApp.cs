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

    static volatile bool _closing;
    static HostLifetime _lifetime = new();

    /// <summary>Completes after application and backend teardown, or faults with all lifecycle errors.</summary>
    public static Task Completion => _lifetime.Completion;

    /// <summary>Request orderly shutdown. Do not call Application.Exit before Completion.</summary>
    public static void RequestExit()
    {
        Window?.DispatcherQueue.TryEnqueue(() => Window.Close());
    }

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
        ArgumentNullException.ThrowIfNull(app);
        if (Window != null) throw new InvalidOperationException("WindowsApp supports one application per process.");
        _closing = false;
        firstTime = true;
        _lifetime = new();

        var keyboard = new WindowsKeyboardService();

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
            windowsFeatures: new WindowsFeatures(),
            keyboard: keyboard,
            recorder: new WindowsMediaRecorder());

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

            // Keys released while the window is inactive never raise KeyUp, so clear
            // the held state on deactivation to prevent stuck keys.
            if (!isActive)
            {
                keyboard.ResetKeys();
                TouchService.isDown = false;
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
            // Keep the HWND alive until the render thread releases its resources.
            e.Cancel = !_lifetime.Completion.IsCompleted;
            if (_closing)
                return;

            _closing = true;
            keyboard.Detach();
            SaveWindowState(immediate: true, source: "Closing");
            if (firstTime)
                _lifetime.Execute(() => { }, app.Dispose);
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

        // The keyboard service needs the window's ContentIsland, which only exists once
        // the XAML content has loaded into the visual tree. UIElement.XamlRoot is still
        // null in the post-Activate dispatcher callback, so attach from Loaded instead.
        // The top-level HWND never receives WM_KEYDOWN because keyboard focus lives on
        // the island's own child HWND, making the island keyboard source the only
        // reliable channel for key input.
        swapChainPanel.Loaded += (s, e) =>
        {
            keyboard.Attach(Window);
        };

        swapChainPanel.PointerPressed += (s, e) =>
        {
            UpdatePointer(e);
            swapChainPanel.CapturePointer(e.Pointer);
            TouchService.isDown = true;
        };

        swapChainPanel.PointerReleased += (s, e) =>
        {
            UpdatePointer(e);
            TouchService.isDown = false;
            swapChainPanel.ReleasePointerCapture(e.Pointer);
        };

        swapChainPanel.PointerCanceled += (s, e) => TouchService.isDown = false;
        swapChainPanel.PointerCaptureLost += (s, e) => TouchService.isDown = false;
        swapChainPanel.PointerEntered += (s, e) => UpdatePointer(e);
        swapChainPanel.PointerMoved += (s, e) => UpdatePointer(e);

        void UpdatePointer(Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            var pos = e.GetCurrentPoint(swapChainPanel).Position;
            var scale = DeviceServices.BaseApp.Scale > 0f ? DeviceServices.BaseApp.Scale : 1f;
            TouchService.PoX = (int)Math.Round(pos.X * DeviceServices.BaseApp.CompositionScale.X / scale);
            TouchService.PoY = (int)Math.Round(pos.Y * DeviceServices.BaseApp.CompositionScale.Y / scale);
        }

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
            if (_closing) return;
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
        CloseAfterCompletion();
    }

    static async void CloseAfterCompletion()
    {
        try { await _lifetime.Completion; }
        catch (Exception error)
        {
            DeviceServices.BaseApp.Status = "Failed";
            DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} [HostLifetime] {error}");
            System.Diagnostics.Trace.TraceError(error.ToString());
        }
        Window.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Window.Close());
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

    /// <summary>
    /// Windowed mode only: resize the OS window so its client area matches the given pixel size.
    /// The app calls this right after applying a display change (startup, resolution or presenter
    /// switch) so the design resolution fills the client area 1:1 and no letterbox is visible.
    /// Fullscreen is skipped (its bars are decided by the monitor aspect ratio, not the window size)
    /// and a maximized window is restored first, because maximizing cannot coexist with an
    /// exact-size window. Nothing is re-applied on later user resizes, so manual stretching is
    /// never fought; the resulting bounds still flow through the normal Changed -> SaveWindowState
    /// path, which keeps the next startup consistent without an extra restore pass.
    /// </summary>
    public static void ApplyWindowClientSize(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var window = Window;
        if (window is null)
        {
            return;
        }

        window.DispatcherQueue.TryEnqueue(() =>
        {
            if (_closing || AppWindow is null)
            {
                return;
            }

            // Fullscreen keeps its presenter untouched: its bars cannot be removed by a window size.
            if (AppWindow.Presenter is not OverlappedPresenter presenter)
            {
                return;
            }

            if (presenter.State == OverlappedPresenterState.Minimized)
            {
                return;
            }

            var size = new SizeInt32(width, height);

            if (presenter.State == OverlappedPresenterState.Maximized)
            {
                presenter.Restore();
            }
            else if (AppWindow.ClientSize.Width == size.Width && AppWindow.ClientSize.Height == size.Height)
            {
                return;
            }

            LogWindowState(
                "Fit",
                $"source=ApplyWindowClientSize pos=({AppWindow.Position.X},{AppWindow.Position.Y}) " +
                $"clientBefore=({AppWindow.ClientSize.Width},{AppWindow.ClientSize.Height}) " +
                $"target=({size.Width},{size.Height})");

            AppWindow.ResizeClient(size);
        });
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
        try { await InitializeAndRun(swapChainPanel); }
        catch (Exception error)
        {
            if (!_lifetime.HasStarted)
                _lifetime.Execute(() => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw(),
                    DeviceServices.BaseApp.Dispose, ShutdownBeforeFrames);
        }
    }

    static void ShutdownBeforeFrames()
    {
        if (DirectX.Device.CanWaitForGpu())
        {
            DirectX.Device.WaitForGpu();
            DirectX.Device.ResetAllAllocatorsForShutdown();
        }
        if (Season.Basic.Graphics.Instance is Graphics graphics)
        {
            graphics.DisposeImmediate2D();
            // Partial initialization may not have a usable fence; never force releases here.
            graphics.PumpDeferredReleases();
        }
    }

    static async Task InitializeAndRun(SwapChainPanel swapChainPanel)
    {
        Season.Basic.Graphics.Instance = new Graphics();

        // Immediate-2D compatibility mode (Season.Rendering.Immediate2DMode):
        // the app renders exclusively through the immediate 2D backend, so everything the 3D path
        // would need is skipped below - no PSO bake, no offscreen targets, no effect registration,
        // and the quality tier is never adjusted. The decision is read once here and is final for
        // the session: the mode is one-way, so the skipped resources are simply never created.
        bool immediate2D = Season.Rendering.Immediate2DMode.Enabled;

        // Freeze the decision: flipping it after this point would leave the frame schedule pointing
        // at resources that were never created, so the setter rejects any later change.
        Season.Rendering.Immediate2DMode.Freeze();

        // 1-4 quality tiering (owned by shared-layer RenderQuality starting from Step C;
        // see its summary for the cross-platform contract):
        // must be finalized before CreateSwapChain (Display/MSAA target formats) and
        // Pipeline.Init (PSO baking). The HDR path depends on offscreen SceneColor
        // because tone mapping is closed in FinalBlit; direct rendering must therefore
        // fall back to the LDR baseline.
        DirectX.Device.HdrSceneColor = !immediate2D && UseOffscreenSceneColor && RenderQuality.Current.HdrSceneColor;

        // 2-1 contract clause 5: finalize the AA tier during initialization
        // (mutually exclusive single-choice; fall back and log when unsupported, with zero runtime branching).
        // This must happen before CreateSwapChain because the Display MSAA sample count is derived
        // from the finalized tier. Taa/Fxaa both depend on the HDR offscreen path:
        // Taa fallback -> Fxaa, and Fxaa fallback -> Off.
        // The whole tier normalization is skipped in immediate-2D mode: no AA path exists to
        // configure, and the values stay unused because no pass ever consults them.
        if (!immediate2D && RenderQuality.Current.AntiAliasing == Season.Rendering.AaMode.Taa)
        {
            // 2-3 contract clause 1: selecting Taa forces the velocity path to be enabled
            // because TAA is invalid without velocity.
            RenderQuality.Current.MotionVectors = true;

            // 2-3 contract clause 10: resolve runs in linear HDR space before tone mapping
            // (both input and output are rgba16float), so it depends on the HDR offscreen path.
            // If unavailable, fall back to Fxaa here, and let the next block continue validating
            // Fxaa's own HDR dependency.
            // Note: whether the Taa tier creates PostColor depends on TaaSharpness (clause 17).
            // With sharpening off, composition still happens in FinalBlit, where TaaEffect output
            // is injected through SceneColorOverride (clause 12); with it on, the Post slot below
            // moves composition upstream and SceneColorOverride is consumed there instead.
            // Registration failure of TaaEffect itself does not trigger fallback here because it
            // has its own bypass path (TaaActive/SceneColorOverride remain false/null), so the
            // image falls back to non-TAA SceneColor without jitter (clauses 14/15).
            if (!DirectX.Device.HdrSceneColor)
            {
                RenderQuality.Current.AntiAliasing = Season.Rendering.AaMode.Fxaa;
                DeviceServices.BaseApp.AddLog(LogType.None, $"{DateTime.UtcNow} [RenderQuality] Taa requires the HDR offscreen path (currently disabled), falling back to Fxaa; MotionVectors remains enabled");
            }
        }
        if (!immediate2D && RenderQuality.Current.AntiAliasing == Season.Rendering.AaMode.Fxaa && !DirectX.Device.HdrSceneColor)
        {
            RenderQuality.Current.AntiAliasing = Season.Rendering.AaMode.Off;
            DeviceServices.BaseApp.AddLog(LogType.None, $"{DateTime.UtcNow} [RenderQuality] Fxaa requires the HDR offscreen path (currently disabled), falling back to Off");
        }

        // 2-3 contract clauses 1/8: finalize the MotionVectors tier during initialization,
        // before Pipeline.Init. The main shader's VELOCITY_OUTPUT variant and the PSO's
        // NumRenderTargets/RTVFormats[1] are both derived from it, so it must not change at runtime.
        // It is mutually exclusive with Msaa4x because all MRT attachments must use the same
        // sample count, and multisampled color cannot be bound together with single-sampled velocity.
        if (!immediate2D
            && RenderQuality.Current.MotionVectors
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
            throw;
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
            throw;
        }
        DeviceServices.BaseApp.AddLog(LogType.None, $"{DateTime.UtcNow} [Init] CreateSwapChain done");

        DirectX.Device.CreateDescriptorHeapsAndViews();

        // Skipped in immediate-2D mode: the main PSO family (and every fxc compile it triggers)
        // is never baked, which is where the startup cost would otherwise go.
        if (!immediate2D)
        {
            DeviceServices.BaseApp.AddLog(LogType.None, $"{DateTime.UtcNow} [Init] Pipeline.Init begin");
            try
            {
                Pipeline.Init();
            }
            catch (Exception ex)
            {
                DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} [Init] Pipeline.Init failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                throw;
            }
            DeviceServices.BaseApp.AddLog(LogType.None, $"{DateTime.UtcNow} [Init] Pipeline.Init done");
        }

        // Startup shader budget: Pipeline.Init plus the nested BlitPipeline.Init request far more shader
        // compilations than there are distinct sources, and every real fxc call sits in front of the first
        // frame. Reporting misses versus hits makes the dedup ratio verifiable without a Release build.
        DeviceServices.BaseApp.AddLog(LogType.None, $"{DateTime.UtcNow} [ShaderCache][graphics] fxc compiles={DirectX.ShaderCompiler.CompileCount}, cache hits={DirectX.ShaderCompiler.CacheHitCount}");

        // Global shared lighting CB: both the Pbr3D path and DXSpriteQuad's b1 read from it,
        // so it must be initialized before DXSprite2D.Init / DeviceServices.BaseApp.Create() load resources.
        // Skipped in immediate-2D mode: no 3D pass consumes it, and the matching per-frame
        // DXPrimitiveGroup.Update in the render loop is skipped as well.
        if (!immediate2D)
            DXPrimitiveGroup.InitLights();

        DXSprite2D.Init();

        // Per-frame command lists. In immediate-2D mode Pipeline.OpaquePipelineState is null,
        // which FrameContext.Initialize forwards as the optional pInitialState (D3D12 allows null).
        DirectX.Device.CreateGraphicsCommandLists();

        // Offscreen SceneColor (Step 2): when non-null, FrameSchedule automatically appends
        // the FinalBlit pass for presentation.
        // 1-4 Step A: use RGBA16F in the HDR path (FinalBlit switches to the tone-mapping variant automatically).
        if (!immediate2D && UseOffscreenSceneColor)
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
        if (!immediate2D && RenderQuality.Current.MotionVectors)
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
        // 2-3 clause 17: the TAA tier takes the same slot when TaaSharpness is above zero, because a
        // display-referred sharpener has nowhere else to run - the compute resolve happens in linear HDR,
        // and RCAS measures its headroom against display white. FinalBlit then degenerates into RCAS instead
        // of FXAA; RenderPostPass forwards SceneColorOverride, and the Post pass runs after the AfterScene
        // phase, so it reads the resolve output of the current frame rather than SceneColor.
        // In tiers that ask for neither, both remain null, leaving no residue in the pipeline.
        if (!immediate2D
            && (RenderQuality.Current.AntiAliasing == Season.Rendering.AaMode.Fxaa
                || (RenderQuality.Current.AntiAliasing == Season.Rendering.AaMode.Taa
                    && RenderQuality.Current.TaaSharpness > 0f))
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
        if (!immediate2D && RenderQuality.Current.ShadowsEnabled)
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
        if (!immediate2D && RenderQuality.Current.AmbientOcclusion != Season.Rendering.AoMode.Off && !DirectX.Device.HdrSceneColor)
        {
            RenderQuality.Current.AmbientOcclusion = Season.Rendering.AoMode.Off;
            DeviceServices.BaseApp.AddLog(LogType.None, $"{DateTime.UtcNow} [RenderQuality] AO requires the HDR offscreen path (currently disabled), falling back to Off");
        }
        if (!immediate2D
            && RenderQuality.Current.AmbientOcclusion != Season.Rendering.AoMode.Off
            && RenderQuality.Current.AntiAliasing == Season.Rendering.AaMode.Msaa4x)
        {
            RenderQuality.Current.AmbientOcclusion = Season.Rendering.AoMode.Off;
            DeviceServices.BaseApp.AddLog(LogType.None, $"{DateTime.UtcNow} [RenderQuality] AO is mutually exclusive with Msaa4x (MSAA depth cannot be used as compute input), falling back to Off");
        }
        if (!immediate2D && RenderQuality.Current.AmbientOcclusion != Season.Rendering.AoMode.Off)
        {
            Season.Rendering.FrameSchedule.SceneDepth = Season.Basic.Graphics.Instance.CreateRenderTarget(new Season.Rendering.RenderTargetDesc
            {
                DepthFormat = Season.Rendering.RtFormat.D32Float,
                MatchBackbufferSize = true,
                SampleCount = 1,
            });
        }

        // Supplement to contract 2-3 clause 2: the velocity path also needs SceneDepth to be explicit,
        // so that TAA can read the current frame's depth and dilate velocity towards the closest
        // neighbour (clause 10). Vulkan, Metal, and Android already created it here because their
        // velocity render pass demands three real attachments; D3D12 can run the MRT scene pass against
        // the backend default depth, which is why this platform was the one still leaving it null and
        // why the dilation input was missing on exactly the validated backend.
        // SampleCount 1 is unconditionally correct here: MotionVectors and Msaa4x were already made
        // mutually exclusive above, so this branch is never reached with a multisampled scene.
        if (!immediate2D && RenderQuality.Current.MotionVectors && Season.Rendering.FrameSchedule.SceneDepth == null)
        {
            Season.Rendering.FrameSchedule.SceneDepth = Season.Basic.Graphics.Instance.CreateRenderTarget(new Season.Rendering.RenderTargetDesc
            {
                DepthFormat = Season.Rendering.RtFormat.D32Float,
                MatchBackbufferSize = true,
                SampleCount = 1,
            });
        }

        // One-shot tier report: the app ships with the desktop-class defaults (HDR + TAA tier +
        // GTAO + shadows + bloom), so this line is what a device log needs to tell which pipeline the
        // frame actually pays for, and whether immediate-2D mode is active.
        DeviceServices.BaseApp.AddLog(LogType.None, $"{DateTime.UtcNow} [RenderQuality] tier: {(int)DeviceServices.BaseApp.DeviceResolution.X}x{(int)DeviceServices.BaseApp.DeviceResolution.Y} hdr={DirectX.Device.HdrSceneColor} aa={RenderQuality.Current.AntiAliasing} mv={RenderQuality.Current.MotionVectors} ao={RenderQuality.Current.AmbientOcclusion} shadows={RenderQuality.Current.ShadowsEnabled} bloom={RenderQuality.Current.BloomEnabled} taaSharpness={RenderQuality.Current.TaaSharpness} immediate2D={immediate2D}");

        // Extra line when the mode is on: this is what a device log needs to tell that the frame is
        // Overlay-only and that the tier line right above is inert.
        if (immediate2D)
            DeviceServices.BaseApp.AddLog(LogType.None, $"{DateTime.UtcNow} [RenderQuality] immediate-2D mode: the main PSO family was not compiled and no offscreen target, shadow map or post effect exists this session; every frame renders only the Overlay pass directly into the backbuffer");

        DeviceServices.BaseApp.Create();

        // Create() registers the compute effects, so this second reading covers graphics plus every kernel that
        // has to be compiled before the frame loop starts.
        DeviceServices.BaseApp.AddLog(LogType.None, $"{DateTime.UtcNow} [ShaderCache][total] fxc compiles={DirectX.ShaderCompiler.CompileCount}, cache hits={DirectX.ShaderCompiler.CacheHitCount}");

        WorkItemHandler handler = delegate
        {
            bool gpuIdle = false;
            _lifetime.Execute(() =>
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

                // Camera and lighting CBs, written before each frame.
                // Skipped in immediate-2D mode, where the lighting CB was never created because no 3D
                // pass consumes it.
                if (!Season.Rendering.Immediate2DMode.Enabled)
                {
                    DXPrimitiveGroup.Update(elapsed, DeviceServices.BaseApp.CameraPos, DeviceServices.BaseApp.CameraTarget, DeviceServices.BaseApp.EffectiveSceneLights);
                }

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

            },
            () =>
            {
                if (!DirectX.Device.CanWaitForGpu()) return;
                DirectX.Device.WaitForGpu();
                // Command allocators must stop referencing textures before forced release.
                DirectX.Device.ResetAllAllocatorsForShutdown();
                gpuIdle = true;
            },
            DeviceServices.BaseApp.Dispose,
            () =>
            {
                if (Season.Basic.Graphics.Instance is Graphics graphics)
                    graphics.DisposeImmediate2D();
            },
            () =>
            {
                if (Season.Basic.Graphics.Instance is Graphics graphics)
                    graphics.PumpDeferredReleases(force: gpuIdle);
            });
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
