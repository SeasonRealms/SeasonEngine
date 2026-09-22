// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using CoreGraphics;
using Foundation;
using Metal;
using MetalKit;
using ObjCRuntime;
using Season.Platforms.Shared.Apple.Metal;
using UIKit;
using MtlDevice = Season.Platforms.Shared.Apple.Metal.Device;

namespace Season.Platforms.Shared.Apple;

/// <summary>
/// Entry point for iOS and MacCatalyst applications.
/// FinishedLaunching only assembles the window and RootViewController.
/// The real Metal bootstrap chain lives in <see cref="MetalViewController"/> plus <see cref="SeasonMTKViewDelegate"/>,
/// and runs only after MTKView.DrawableSize becomes valid, matching the timing of LinuxApp.InitializeVulkan.
///
/// Pass orchestration position for 1-1:
/// SeasonMTKViewDelegate.Draw drives the fixed pass chain through FrameSchedule.Execute,
/// Shadow, Scene, Post, and FinalBlit.
/// SceneColor is registered in step 8.5 of InitializeMetal,
/// with UseOffscreenSceneColor acting as the fallback switch, mirrored across all backends.
/// For the full catalog of Metal platform-specific rules, see the class header of Platforms/Shared/Apple/Metal/Device.cs.
/// </summary>
[Foundation.Register("AppDelegate")]
public class AppDelegate : UIApplicationDelegate
{
    /// <summary>Keyboard service instance injected by iOSApp.Run or MacCatalystApp.Run before UIApplication.Main, then consumed by MetalViewController press events.</summary>
    public static AppleKeyboardService Keyboard { get; set; } = null!;

    public override bool FinishedLaunching(UIKit.UIApplication application, Foundation.NSDictionary launchOptions)
    {
        Runtime.MarshalManagedException += (_, e) =>
        {
            // Log the managed exception with its real stack before it crosses the native boundary.
            // Without this, escaping exceptions (async void callbacks, runloop delegates) abort the
            // app behind a bare native xamarin_UIApplicationMain frame, hiding the throw site.
            var log = $"{DateTime.UtcNow} [FATAL] MarshalManagedException: {e.Exception}";
            Debug.WriteLine(log);
            DeviceServices.BaseApp?.AddLog(LogType.Error, log);

            e.ExceptionMode = MarshalManagedExceptionMode.UnwindNativeCode;
        };
        Runtime.MarshalObjectiveCException += (_, e) => e.ExceptionMode = MarshalObjectiveCExceptionMode.UnwindManagedCode;

        var scene = UIKit.UIApplication.SharedApplication.ConnectedScenes
            .ToArray().FirstOrDefault(cs => cs is UIWindowScene) as UIWindowScene;

        // UIKit window coordinates use logical points.
        // NativeBounds is in physical pixels and always stays in portrait orientation.
        // Building the window from NativeBounds would create a window enlarged by nativeScale on iPhone,
        // which makes the whole UI scale up and clips the bottom and right edges.
        // Retina pixel resolution is obtained automatically from MTKView.DrawableSize,
        // which equals bounds times contentScaleFactor.
        var bounds = scene?.Screen.Bounds ?? UIScreen.MainScreen.Bounds;

#if MACCATALYST
        if (scene?.Titlebar is { } titlebar)
        {
            titlebar.TitleVisibility = UITitlebarTitleVisibility.Hidden;
            titlebar.Toolbar = null;
        }
#endif

        var uiWindow = new UIWindow(bounds);
        var uiViewController = new MetalViewController(uiWindow);

        uiViewController.PrefersStatusBarHidden();

        uiWindow.RootViewController = uiViewController;
        uiWindow.MakeKeyAndVisible();

#if MACCATALYST
        _geometryRestoreAttempts = 0;
        _restoreTargetFrame = null;
        if (scene != null) RestoreWindowGeometry(scene);
#endif

        return true;
    }

    public override void WillTerminate(UIApplication application)
    {
#if MACCATALYST
        // Capture the final frame, including moves that happened without a resize.
        var windowScene = GetWindowScene();
        if (windowScene is not null)
        {
            SaveWindowGeometry(windowScene);
        }
#endif

        try { DeviceServices.BaseApp.SaveSettings(); }
        finally { SeasonMTKViewDelegate.Current?.Stop(); }
    }

    public override void OnResignActivation(UIApplication application)
    {
        SeasonMTKViewDelegate.Current?.SetPaused(true);
        Keyboard?.ResetKeys();
    }

    public override void OnActivated(UIApplication application)
    {
        SeasonMTKViewDelegate.Current?.SetPaused(false);
    }

#if MACCATALYST
    // ── Mac Catalyst window geometry ───────────────────────────────────────────
    //
    // The NSWindow is owned by the scene / macOS window server: neither the bounds
    // passed to the UIWindow constructor nor assignments to UIWindow.Frame resize
    // the actual window. The only supported way to resize or reposition it is
    // -[UIWindowScene requestGeometryUpdateWithPreferences:errorHandler:] with a
    // UIWindowSceneGeometryPreferencesMac carrying the target systemFrame
    // (screen coordinates in points, origin at the bottom-left corner).
    // AppKit is unavailable in Mac Catalyst processes, so NSScreen.visibleFrame
    // cannot be queried; requesting the full screen bounds lets the window server
    // clamp the frame to the usable area, which produces the maximized state.

    /// <summary>Maximum vertical space the menu bar plus a visible dock can occupy, used to classify a frame as maximized.</summary>
    const int MenuBarAndDockMaxSlack = 140;

    /// <summary>Geometry restore attempts issued for the current launch.</summary>
    static int _geometryRestoreAttempts;

    /// <summary>
    /// Target frame captured from settings when the restore sequence starts. All
    /// retries reuse it so transient save events during the settle cannot re-target
    /// the retries; null means no restore is in flight.
    /// </summary>
    static CGRect? _restoreTargetFrame;

    internal static UIWindowScene? GetWindowScene()
        => UIApplication.SharedApplication.ConnectedScenes
            .ToArray().FirstOrDefault(cs => cs is UIWindowScene) as UIWindowScene;

    /// <summary>
    /// Restores the saved window geometry through requestGeometryUpdate.
    /// Deferred onto the main queue because during FinishedLaunching the scene is
    /// not active yet and geometry requests issued before activation are dropped.
    /// Retries a few times because macOS applies its own initial window frame on a
    /// later run loop — observed overwriting an early request about 50 ms after
    /// launch — plus the documented Ventura 13.2 bug where the settled height comes
    /// back smaller than requested. The target frame is captured once on the first
    /// attempt; settle-time saves must not feed back into the retries, otherwise the
    /// first save would re-target them at the default frame the system just applied.
    /// </summary>
    static void RestoreWindowGeometry(UIWindowScene scene)
    {
        CoreFoundation.DispatchQueue.MainQueue.DispatchAsync(() =>
        {
            var screen = scene.Screen.Bounds;

            if (_restoreTargetFrame is null)
            {
                var windowState = DeviceServices.BaseApp.Settings.WindowState;

                if (windowState.Maximized)
                {
                    // The full screen bounds are honored exactly, which produces the
                    // maximized state; the menu bar overlays the top of the window.
                    _restoreTargetFrame = new CGRect(0, 0, screen.Width, screen.Height);
                }
                else if (windowState.Width > 0 && windowState.Height > 0)
                {
                    var frame = new CGRect(windowState.X, windowState.Y, windowState.Width, windowState.Height);

                    // Honor the saved frame only while it still fits the current screen.
                    if (frame.Width <= screen.Width && frame.Height <= screen.Height)
                    {
                        _restoreTargetFrame = frame;
                    }
                }
            }

            if (_restoreTargetFrame is null)
            {
                // Nothing to restore; persist whatever geometry the system chose.
                SaveWindowGeometry(scene);
                return;
            }

            RequestGeometry(scene, _restoreTargetFrame.Value);

            _geometryRestoreAttempts++;

            if (_geometryRestoreAttempts < 3)
            {
                var when = new CoreFoundation.DispatchTime(CoreFoundation.DispatchTime.Now, 150_000_000);
                CoreFoundation.DispatchQueue.MainQueue.DispatchAfter(when, () => RestoreWindowGeometry(scene));
            }
            else
            {
                // Final request issued; release the intent so resize saves resume.
                _restoreTargetFrame = null;
            }
        });
    }

    static void RequestGeometry(UIWindowScene scene, CGRect frame)
    {
        var preferences = new UIWindowSceneGeometryPreferencesMac(frame);

        scene.RequestGeometryUpdate(preferences, error =>
            DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} [WindowGeometry] request frame={frame} failed err={error?.LocalizedDescription}"));
    }

    /// <summary>Persists the current window frame (points, screen coordinates) and the maximized flag derived from it.</summary>
    internal static void SaveWindowGeometry(UIWindowScene scene)
    {
        var frame = scene.EffectiveGeometry.SystemFrame;
        var screen = scene.Screen.Bounds;

        // Restore in flight: the window may be mid-resize between the system's own
        // initial frame and the re-asserted target; do not persist transient states.
        if (_restoreTargetFrame is not null)
        {
            return;
        }

        if (frame.Width <= 0 || frame.Height <= 0 || screen.Width <= 0 || screen.Height <= 0)
        {
            return;
        }

        var windowState = DeviceServices.BaseApp.Settings.WindowState;

        // A maximized window spans the full usable width and leaves no more than
        // the menu bar plus dock uncovered vertically. The exact usable frame is
        // not queryable from Mac Catalyst, hence the slack.
        windowState.Maximized = frame.Width >= screen.Width - 8
            && frame.Height >= screen.Height - MenuBarAndDockMaxSlack;
        windowState.FullScreen = false;

        if (!windowState.Maximized)
        {
            windowState.X = (int)frame.X;
            windowState.Y = (int)frame.Y;
            windowState.Width = (int)frame.Width;
            windowState.Height = (int)frame.Height;
        }

        DeviceServices.BaseApp.RequestSaveSettings();
    }
#endif
}

/// <summary>
/// MetalView:
/// MTKView subclass that maps UIView touch events into TouchService.
/// Equivalent to the SDL_EVENT_MOUSE_BUTTON_DOWN, UP, and MOTION branches inside LinuxApp.RunLoop.
/// </summary>
public class MetalView : MTKView
{
    public MetalView(CGRect frame, IMTLDevice device) : base(frame, device) { }

    public override void TouchesBegan(NSSet nsset, UIEvent uIEvent)
    {
        base.TouchesBegan(nsset, uIEvent);
        ProcessTouches(nsset);
    }

    public override void TouchesEnded(NSSet nsset, UIEvent uIEvent)
    {
        base.TouchesEnded(nsset, uIEvent);
        ProcessTouches(nsset);
    }

    public override void TouchesMoved(NSSet nsset, UIEvent uIEvent)
    {
        base.TouchesMoved(nsset, uIEvent);
        ProcessTouches(nsset);
    }

    public override void TouchesCancelled(NSSet nsset, UIEvent uIEvent)
    {
        base.TouchesCancelled(nsset, uIEvent);
        ProcessTouches(nsset);
    }

    void ProcessTouches(NSSet nsset)
    {
        if ((long)nsset.Count == 0)
            return;

        var touchesArray = nsset.ToArray<UITouch>();

        for (int i = 0; i < touchesArray.Length; ++i)
        {
            var touch = touchesArray[i];

            var location = touch.LocationInView(touch.View);

            var pos = new Vector2((float)location.X, (float)location.Y);

            var nativeScale = (float)UIScreen.MainScreen.NativeScale;

            TouchService.PoX = (int)(pos.X * nativeScale / DeviceServices.BaseApp.Scale);
            TouchService.PoY = (int)(pos.Y * nativeScale / DeviceServices.BaseApp.Scale);

            switch (touch.Phase)
            {
                case UITouchPhase.Moved:
                    TouchService.IsMoved = true;
                    break;
                case UITouchPhase.Began:
                    TouchService.isDown = true;
                    break;
                case UITouchPhase.Ended:
                    TouchService.isDown = false;
                    break;
                case UITouchPhase.Cancelled:
                    TouchService.isDown = false;
                    break;
                default:
                    break;
            }
        }
    }
}

/// <summary>
/// Root ViewController:
/// creates MetalView in ViewDidLoad and takes over PanGesture as the MacCatalyst scroll-wheel substitute.
/// The real Metal bootstrap chain is executed by SeasonMTKViewDelegate during the first Draw call,
/// ensuring DrawableSize is already valid.
/// </summary>
public class MetalViewController : UIViewController
{
    UIWindow _uiWindow;

    public MetalView MetalView { get; private set; } = null!;

    SeasonMTKViewDelegate _delegate = null!;

    public MetalViewController(UIWindow window)
    {
        _uiWindow = window;
    }

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();

        var mtlDevice = MTLDevice.SystemDefault
            ?? throw new Exception("Metal is not supported on this device");

        MetalView = new MetalView(View!.Bounds, mtlDevice)
        {
            AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight
        };

#if MACCATALYST
        var panGesture = new UIPanGestureRecognizer(HandlePanGesture);
        panGesture.AllowedScrollTypesMask = UIScrollTypeMask.All;
        panGesture.MinimumNumberOfTouches = 0;
        panGesture.MaximumNumberOfTouches = 0;
        MetalView.AddGestureRecognizer(panGesture);
#endif

#if IOS
        var pinchGesture = new UIPinchGestureRecognizer(HandlePinchGesture);
        MetalView.AddGestureRecognizer(pinchGesture);
#endif

        View!.AddSubview(MetalView);

        _delegate = new SeasonMTKViewDelegate();
        MetalView.Delegate = _delegate;
    }

    public override void ViewWillTransitionToSize(CGSize toSize, IUIViewControllerTransitionCoordinator coordinator)
    {
        base.ViewWillTransitionToSize(toSize, coordinator);
    }

    public override bool CanBecomeFirstResponder => true;

    public override void ViewDidAppear(bool animated)
    {
        base.ViewDidAppear(animated);

        // Take the responder chain so the hardware keyboard's key presses are
        // delivered to PressesBegan/PressesChanged/PressesEnded.
        BecomeFirstResponder();
    }

    public override void PressesBegan(NSSet<UIPress> presses, UIPressesEvent evt)
    {
        base.PressesBegan(presses, evt);

        foreach (var press in presses.ToArray<UIPress>())
        {
            AppDelegate.Keyboard.OnPress(press, down: true);
        }
    }

    public override void PressesChanged(NSSet<UIPress> presses, UIPressesEvent evt)
    {
        base.PressesChanged(presses, evt);

        // Key repeat can arrive through this callback as well; OnPress is
        // idempotent for keys that are already down.
        foreach (var press in presses.ToArray<UIPress>())
        {
            AppDelegate.Keyboard.OnPress(press, down: true);
        }
    }

    public override void PressesEnded(NSSet<UIPress> presses, UIPressesEvent evt)
    {
        base.PressesEnded(presses, evt);

        foreach (var press in presses.ToArray<UIPress>())
        {
            AppDelegate.Keyboard.OnPress(press, down: false);
        }
    }

    public override void PressesCancelled(NSSet<UIPress> presses, UIPressesEvent evt)
    {
        base.PressesCancelled(presses, evt);

        // The press stream was interrupted; keys released under cancellation never
        // arrive, so clear everything to prevent stuck keys.
        AppDelegate.Keyboard.ResetKeys();
    }

#if MACCATALYST
    void HandlePanGesture(UIPanGestureRecognizer gesture)
    {
        var translation = gesture.TranslationInView(MetalView);

        if (gesture.State == UIGestureRecognizerState.Changed)
        {
            var deltaY = (float)translation.Y;

            if (TouchService.PoZ is null)
                TouchService.PoZ = 0;

            TouchService.PoZ -= (int)(deltaY * 50);

            gesture.SetTranslation(CGPoint.Empty, MetalView);
        }
    }
#endif

#if IOS
    float _prevPinchScale = 1f;

    void HandlePinchGesture(UIPinchGestureRecognizer gesture)
    {
        switch (gesture.State)
        {
            case UIGestureRecognizerState.Began:
                _prevPinchScale = (float)gesture.Scale;
                break;

            case UIGestureRecognizerState.Changed:
                var curScale = (float)gesture.Scale;
                var delta = curScale - _prevPinchScale;
                _prevPinchScale = curScale;

                if (delta != 0f)
                {
                    if (TouchService.PoZ is null)
                        TouchService.PoZ = 0;

                    // Spread fingers, scale increases, zoom in, PoZ decreases.
                    // Pinch fingers, scale decreases, zoom out, PoZ increases.
                    TouchService.PoZ += (int)(delta * 500);
                }
                break;

            case UIGestureRecognizerState.Ended:
            case UIGestureRecognizerState.Cancelled:
                _prevPinchScale = 1f;
                break;
        }
    }
#endif
}

/// <summary>
/// MTKViewDelegate:
/// moves the LinuxApp.RunLoop frame sequence, Update, BeforeRender, Draw, and AfterRender,
/// into MetalKit callbacks.
/// The first Draw executes the full 9-step Metal bootstrap chain after DrawableSize becomes available.
/// Every later Draw advances time and submits one frame.
/// </summary>
public class SeasonMTKViewDelegate : MTKViewDelegate
{
    internal static SeasonMTKViewDelegate? Current { get; private set; }
    readonly HostLifetime _lifetime = new();
    MTKView? _view;
    Graphics? _graphics;
    bool _paused;
    bool _initialized;
    bool _pendingResize;
    int _pendingW;
    int _pendingH;

    /// <summary>Offscreen SceneColor switch for step 2. When false, fall back to the step 1 direct backbuffer path, mirrored with WindowsApp.</summary>
    static readonly bool UseOffscreenSceneColor = true;

    Stopwatch _stopwatch = Stopwatch.StartNew();
    double _previousSeconds;

    public SeasonMTKViewDelegate()
    {
        if (Current != null)
            throw new InvalidOperationException("Only one Metal host is supported per process.");
        Current = this;
    }

    internal void SetPaused(bool paused)
    {
        _paused = paused;
        _previousSeconds = _stopwatch.Elapsed.TotalSeconds;
        if (_view != null) _view.Paused = paused || _lifetime.HasStarted;

        // BaseApp.IsActive is the game's input gate (InputManager.Update drops every press and
        // ScreenManager skips HandleInput while it is false). Mirror AndroidApp.Pause/Resume so
        // activation follows app foreground/background instead of only pausing the render loop.
        DeviceServices.BaseApp.IsActive = !paused;
    }

    internal void Stop(Exception? error = null)
    {
        if (_lifetime.HasStarted) return;
        if (_view != null) _view.Paused = true;
        _lifetime.Execute(
            () => { if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw(); },
            () => MtlDevice.AbortFrame(),
            () => MtlDevice.WaitForIdle(),
            () => DeviceServices.BaseApp.Dispose(),
            () => _graphics?.DisposeImmediate2D(),
            () =>
            {
                var capture = BaseApp.CaptureAppTcs;
                BaseApp.CaptureAppTcs = null;
                capture?.TrySetResult(null);
            },
            () => DeviceServices.BaseApp.DisposeSaveSettingsRequest());
        try { _lifetime.Completion.GetAwaiter().GetResult(); }
        catch (Exception failure)
        {
            Debug.WriteLine(failure);
            DeviceServices.BaseApp.AddLog(LogType.Error, $"[Metal host] {failure}");
        }
    }

    public override void DrawableSizeWillChange(MTKView view, CGSize size)
    {
#if MACCATALYST
        // The drawable size tracks the window content area, so this is also where
        // window resizes are persisted. The authoritative frame is read back from
        // the scene because drawable sizes are physical pixels while settings
        // store points.
        if (size.Width > 0 && size.Height > 0)
        {
            var scene = AppDelegate.GetWindowScene();
            if (scene is not null)
            {
                AppDelegate.SaveWindowGeometry(scene);
            }
        }
#endif

        _pendingW = (int)size.Width;
        _pendingH = (int)size.Height;
        _pendingResize = true;
    }

    public override void Draw(MTKView view)
    {
        _view = view;
        if (_paused || _lifetime.HasStarted)
            return;
        try { DrawCore(view); }
        catch (Exception error) { Stop(error); }
    }

    void DrawCore(MTKView view)
    {
        if (!_initialized)
        {
            if (view.DrawableSize.Width <= 0 || view.DrawableSize.Height <= 0) return;
            InitializeMetal(view);

            _initialized = true;
            _previousSeconds = _stopwatch.Elapsed.TotalSeconds;
            // The first callback runs the full bootstrap chain.
            // This frame is yielded, and the next Draw enters the regular frame sequence.
            return;
        }

        if (_pendingResize && _pendingW > 0 && _pendingH > 0)
        {
            // HandleResize returning false means ResizeSemaphore timed out because background Load is still holding the lock.
            // Resize must not be driven in that state because ResizeCompute would recreate compute-storage textures.
            // Keep _pendingResize and retry on the next frame.
            if (MtlDevice.HandleResize(_pendingW, _pendingH))
            {
                DeviceServices.BaseApp.ApplyResolution(_pendingW, _pendingH, 1f, 1f);
                _pendingResize = false;

                DeviceServices.BaseApp?.Resize();
            }
        }

        double newSeconds = _stopwatch.Elapsed.TotalSeconds;
        double deltaSeconds = newSeconds - _previousSeconds;
        _previousSeconds = newSeconds;
        float elapsed = (float)deltaSeconds;

        MtlDevice.WaitForFrameSlot();
        // Camera and lighting UBO, written before every frame, equivalent to LinuxApp.VKPrimitiveGroup.Update.
        // Skipped in immediate-2D mode, where the lighting UBO was never created because no 3D
        // pass consumes it.
        if (!Season.Rendering.Immediate2DMode.Enabled)
        {
            MTLPrimitiveGroup.Update(
                elapsed,
                DeviceServices.BaseApp.CameraPos,
                DeviceServices.BaseApp.CameraTarget,
                DeviceServices.BaseApp.EffectiveSceneLights);
        }

        DeviceServices.BaseApp.Update(elapsed);
        if (!string.IsNullOrEmpty(DeviceServices.BaseApp.Status))
        {
            Stop();
            return;
        }

        var backgroundColor = DeviceServices.BaseApp.BackgroundColor;
        MtlDevice.BackgroundColor = backgroundColor;
        MtlDevice.Display?.SetClearColor(backgroundColor);

        // Frame recording:
        // BeforeRender allocates the CommandBuffer, and pass begin and end are driven by FrameSchedule, step 1 of 1-1.
        if (MtlDevice.BeforeRender())
        {
            Season.Basic.Graphics.Instance.FlushTextAtlas();

            // Pass orchestration in step 1:
            // Scene-pass begin and end are driven by FrameSchedule.
            Season.Rendering.FrameSchedule.Execute(Season.Basic.Graphics.Instance, DeviceServices.BaseApp, backgroundColor);

            MtlDevice.AfterRender();
        }
    }

    /// <summary>
    /// Full Metal bootstrap chain, aligned one to one with LinuxApp.InitializeVulkan and WindowsApp.CreateInstance:
    ///   Device.Init -> CreateSwapChain -> CreateDescriptorHeapsAndViews -> Pipeline.Init ->
    ///   MTLPrimitiveGroup.InitLights -> MTLSprite2D.Init -> CreateGraphicsCommandLists ->
    ///   inject Graphics.Instance -> BaseApp.Create().
    /// In immediate-2D mode (Season.Rendering.Immediate2DMode) everything the 3D path would need is skipped:
    /// no Pipeline.Init, no InitLights, no offscreen target, and no shadow or AO resource.
    /// </summary>
    void InitializeMetal(MTKView view)
    {
        // Immediate-2D compatibility mode (Season.Rendering.Immediate2DMode):
        // the app renders exclusively through the immediate 2D backend, so everything the 3D path
        // would need is skipped below - no PSO bake, no offscreen targets, no effect registration,
        // and the quality tier is never adjusted. The decision is read once here and is final for
        // the session: the mode is one-way, so the skipped resources are simply never created.
        bool immediate2D = Season.Rendering.Immediate2DMode.Enabled;

        // Freeze the decision: flipping it after this point would leave the frame schedule pointing
        // at resources that were never created, so the setter rejects any later change.
        Season.Rendering.Immediate2DMode.Freeze();

        var drawable = view.DrawableSize;
        int w = (int)drawable.Width;
        int h = (int)drawable.Height;
        if (w <= 0) w = (int)view.Bounds.Width;
        if (h <= 0) h = (int)view.Bounds.Height;

        DeviceServices.BaseApp.ApplyResolution(w, h, 1f, 1f);

        // 1) Bind IMTLDevice and MTKView, including color and depth pixel-format configuration.
        MtlDevice.Init(view);

        // 2) CommandQueue, ResourceManager, and TextureUploadBatch.
        MtlDevice.CreateSwapChain(w, h);

        // 3) Display, including Viewport and Scissor.
        MtlDevice.CreateDescriptorHeapsAndViews();

        // Step A of 1-4:
        // must be finalized before Pipeline.Init, where PSOs are baked.
        // The HDR chain depends on offscreen SceneColor with FinalBlit closing tone mapping.
        // Direct rendering forces fallback to the LDR baseline, mirrored across all backends.
        MtlDevice.HdrSceneColor = !immediate2D && UseOffscreenSceneColor && RenderQuality.Current.HdrSceneColor;

        // Step D of 2-1:
        // the AA tier is finalized during initialization, matching LinuxApp and AndroidApp under contract clause 5.
        // Msaa4x is D3D12-only.
        // Taa depends on the HDR offscreen chain as implemented in step D of 2-3.
        // Fxaa also depends on the HDR offscreen chain because the uber pass bakes luma.
        // The whole tier normalization is skipped in immediate-2D mode: no AA path exists to
        // configure, and the values stay unused because no pass ever consults them.
        if (!immediate2D && RenderQuality.Current.AntiAliasing == Season.Rendering.AaMode.Msaa4x)
        {
            RenderQuality.Current.AntiAliasing = Season.Rendering.AaMode.Fxaa;
            DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} [RenderQuality] AaMode.Msaa4x is supported only on D3D12, falling back to Fxaa");
        }
        if (!immediate2D && RenderQuality.Current.AntiAliasing == Season.Rendering.AaMode.Taa)
        {
            // Contract clause 1 of 2-3:
            // selecting Taa forces the velocity infrastructure to be enabled because TAA is invalid without velocity.
            // This assignment must happen before Pipeline.Init, where VELOCITY_OUTPUT variants are baked,
            // and before SceneVelocity RT creation, both of which happen below.
            RenderQuality.Current.MotionVectors = true;
            // Contract clause 10 of 2-3:
            // resolve runs in linear HDR space before tone mapping, with both input and output in rgba16float,
            // so it depends on the HDR offscreen chain.
            // When that requirement is not met, fall back to Fxaa, whose own HDR dependency is checked by the next branch.
            // Registration failure of TaaEffect itself does not trigger fallback here.
            // It already has a bypass path where TaaActive and SceneColorOverride stay false and null,
            // so the image falls back to non-TAA SceneColor without jitter, matching clauses 14 and 15.
            if (!MtlDevice.HdrSceneColor)
            {
                RenderQuality.Current.AntiAliasing = Season.Rendering.AaMode.Fxaa;
                DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} [RenderQuality] Taa depends on the HDR offscreen chain, which is not enabled now, falling back to Fxaa while keeping MotionVectors enabled");
            }
        }
        if (!immediate2D && RenderQuality.Current.AntiAliasing == Season.Rendering.AaMode.Fxaa && !MtlDevice.HdrSceneColor)
        {
            RenderQuality.Current.AntiAliasing = Season.Rendering.AaMode.Off;
            DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} [RenderQuality] Fxaa depends on the HDR offscreen chain, which is not enabled now, falling back to Off");
        }

        // 4) Three pipeline variants, Opaque, Transparent, and Fade.
        // The main PSO is baked against SceneColorFormat.
        // In HDR tiers that means RGBA16Float, and Scene pass is always offscreen.
        // See rule 7-2 in the Metal Device class header.
        // Skipped in immediate-2D mode: the main PSO family is never compiled, which is where the
        // startup bake cost would otherwise go.
        if (!immediate2D)
            Pipeline.Init(MtlDevice.SceneColorFormat, MtlDevice.DepthBufferFormat);

        // 5) Global shared lighting UBO.
        // Both Pbr3D and SpriteQuad buffer b1 read from it,
        // so it must be initialized before Sprite2D.Init and before resource loading.
        // Skipped in immediate-2D mode: no 3D pass consumes it, and the matching per-frame
        // MTLPrimitiveGroup.Update in the render loop is skipped as well.
        if (!immediate2D)
            MTLPrimitiveGroup.InitLights();

        // 6) 2D orthographic camera.
        MTLSprite2D.Init();

        // 7) FrameContext ring plus the White placeholder texture.
        MtlDevice.CreateGraphicsCommandLists();

        // 8) Inject the IGraphics implementation so BaseApp can run unchanged.
        _graphics = new Graphics();
        Season.Basic.Graphics.Instance = _graphics;

        // 8.5) Offscreen SceneColor for step 2.
        // When non-null, FrameSchedule automatically appends a FinalBlit pass to present it, mirrored with WindowsApp.
        // Under step A of 1-4, the HDR chain switches it to Rgba16Float,
        // and FinalBlit automatically selects the tonemap variant.
        if (!immediate2D && UseOffscreenSceneColor)
        {
            Season.Rendering.FrameSchedule.SceneColor = Season.Basic.Graphics.Instance.CreateRenderTarget(new Season.Rendering.RenderTargetDesc
            {
                ColorFormat = MtlDevice.HdrSceneColor
                    ? Season.Rendering.RtFormat.Rgba16Float
                    : Season.Rendering.RtFormat.BackbufferCompatible,
                MatchBackbufferSize = true,
                SampleCount = 1,
            });
        }

        // Contract clause 2 of 2-3:
        // SceneVelocity is a full-size Rg16Float target.
        // When non-null, Scene pass becomes a three-target pass, color, velocity, and depth.
        // When MotionVectors is disabled it stays null, leaving zero residual state in the chain, mirrored with WindowsApp.
        // It must be ready before BaseApp.Create, where the app registers VelocityViewEffect.
        if (!immediate2D && RenderQuality.Current.MotionVectors)
        {
            Season.Rendering.FrameSchedule.SceneVelocity = Season.Basic.Graphics.Instance.CreateRenderTarget(new Season.Rendering.RenderTargetDesc
            {
                ColorFormat = Season.Rendering.RtFormat.Rg16Float,
                MatchBackbufferSize = true,
                SampleCount = 1,
            });
        }

        // 8.55) Step D of 2-1:
        // register the FXAA Post pair, PostColor plus RenderPost, mirrored with WindowsApp.
        // Uber composition moves into the Post pass, and FinalBlit degenerates into FXAA resolve,
        // see the contract-1 revision in RenderQuality 1-4.
        // 2-3 clause 17: the TAA tier takes the same slot when TaaSharpness is above zero, because a
        // display-referred sharpener has nowhere else to run - the compute resolve happens in linear HDR,
        // and RCAS measures its headroom against display white. FinalBlit then degenerates into RCAS instead
        // of FXAA; RenderPostPass forwards SceneColorOverride, and the Post pass runs after the AfterScene
        // phase, so it reads the resolve output of the current frame rather than SceneColor.
        // In tiers that ask for neither, both remain null, leaving no residue in the pipeline.
        if ((RenderQuality.Current.AntiAliasing == Season.Rendering.AaMode.Fxaa
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

        // 8.6) Shadow atlas for 1-5.
        // It is depth-only D32Float, with fixed ShadowAtlasSize squared and no resize tracking, following contract clause 2.
        // After ShadowMap plus RenderShadow are registered as a pair,
        // FrameSchedule activates the Shadow pass before Scene, mirrored with WindowsApp.
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

        // 8.7) Contract clause 1 of 2-2:
        // the AO tier is finalized during initialization.
        // It is a mutually exclusive single-choice tier, and unsupported configurations fall back with a log entry.
        // AO depends on the HDR offscreen chain because it is multiplied in at composition time,
        // and it is mutually exclusive with MSAA because depth cannot be used directly as compute input.
        // Once finalized, create SceneDepth as a full-size depth-only target,
        // used as the explicit Scene-pass DepthTarget and as compute depth input, mirrored with WindowsApp.
        if (!immediate2D && RenderQuality.Current.AmbientOcclusion != Season.Rendering.AoMode.Off && !MtlDevice.HdrSceneColor)
        {
            RenderQuality.Current.AmbientOcclusion = Season.Rendering.AoMode.Off;
            DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} [RenderQuality] AO depends on the HDR offscreen chain, which is not enabled now, falling back to Off");
        }
        if (!immediate2D
            && RenderQuality.Current.AmbientOcclusion != Season.Rendering.AoMode.Off
            && RenderQuality.Current.AntiAliasing == Season.Rendering.AaMode.Msaa4x)
        {
            RenderQuality.Current.AmbientOcclusion = Season.Rendering.AoMode.Off;
            DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} [RenderQuality] AO is mutually exclusive with Msaa4x because MSAA depth cannot be used as compute input, falling back to Off");
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

        // Supplement to contract clause 2 of 2-3:
        // MotionVectors requires an explicit depth target because the velocity render pass needs three attachments, color, velocity, and depth.
        // When AO is disabled, SceneDepth may still be null,
        // but MotionVectors still needs a depth attachment, so create it here, mirrored with WindowsApp, LinuxApp, and AndroidApp.
        if (!immediate2D && RenderQuality.Current.MotionVectors && Season.Rendering.FrameSchedule.SceneDepth == null)
        {
            Season.Rendering.FrameSchedule.SceneDepth = Season.Basic.Graphics.Instance.CreateRenderTarget(new Season.Rendering.RenderTargetDesc
            {
                DepthFormat = Season.Rendering.RtFormat.D32Float,
                MatchBackbufferSize = true,
                SampleCount = 1,
            });
        }

        // 9) Create BaseApp, the entry point for resource loading.
        DeviceServices.BaseApp.Create();

        // On launch, OnActivated (with its SetPaused(false)) runs before Create, so the input
        // gate must be armed here too; mirrors LinuxApp where ShowWindow is followed by
        // IsActive = true for a window that starts focused.
        DeviceServices.BaseApp.IsActive = !_paused;
    }

}
