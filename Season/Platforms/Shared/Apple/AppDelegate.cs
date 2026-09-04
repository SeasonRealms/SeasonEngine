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
    public override bool FinishedLaunching(UIKit.UIApplication application, Foundation.NSDictionary launchOptions)
    {
        Runtime.MarshalManagedException += (_, e) => e.ExceptionMode = MarshalManagedExceptionMode.UnwindNativeCode;
        Runtime.MarshalObjectiveCException += (_, e) => e.ExceptionMode = MarshalObjectiveCExceptionMode.UnwindManagedCode;

        var scene = UIKit.UIApplication.SharedApplication.ConnectedScenes
            .ToArray().FirstOrDefault(cs => cs is UIWindowScene) as UIWindowScene;

        // UIKit window coordinates use logical points.
        // NativeBounds is in physical pixels and always stays in portrait orientation.
        // Building the window from NativeBounds would create a window enlarged by nativeScale on iPhone,
        // which makes the whole UI scale up and clips the bottom and right edges.
        // Retina pixel resolution is obtained automatically from MTKView.DrawableSize,
        // which equals bounds times contentScaleFactor.
        var bounds = scene.Screen.Bounds;

#if MACCATALYST
        scene.Titlebar.TitleVisibility = UITitlebarTitleVisibility.Hidden;
        scene.Titlebar.Toolbar = null;

        var windowState = DeviceServices.BaseApp.Settings.WindowState;
        if (windowState.Width > 0 && windowState.Height > 0)
        {
            bounds = new CGRect(bounds.X, bounds.Y, windowState.Width, windowState.Height);
        }
#endif

        var uiWindow = new UIWindow(bounds);
        var uiViewController = new MetalViewController(uiWindow);

        uiViewController.PrefersStatusBarHidden();

        uiWindow.RootViewController = uiViewController;
        uiWindow.MakeKeyAndVisible();

        return true;
    }

    public override void WillTerminate(UIApplication application)
    {
        DeviceServices.BaseApp.SaveSettings();
        DeviceServices.BaseApp.DisposeSaveSettingsRequest();
    }
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
    bool _initialized;
    bool _pendingResize;
    int _pendingW;
    int _pendingH;

    /// <summary>Offscreen SceneColor switch for step 2. When false, fall back to the step 1 direct backbuffer path, mirrored with WindowsApp.</summary>
    static readonly bool UseOffscreenSceneColor = true;

    Stopwatch _stopwatch = Stopwatch.StartNew();
    double _previousSeconds;

    public override void DrawableSizeWillChange(MTKView view, CGSize size)
    {
#if MACCATALYST
        if (size.Width > 0 && size.Height > 0)
        {
            var windowState = DeviceServices.BaseApp.Settings.WindowState;
            windowState.Width = (int)size.Width;
            windowState.Height = (int)size.Height;
            windowState.FullScreen = false;
            windowState.Maximized = false;
            DeviceServices.BaseApp.RequestSaveSettings();
        }
#endif

        _pendingW = (int)size.Width;
        _pendingH = (int)size.Height;
        _pendingResize = true;
    }

    public override void Draw(MTKView view)
    {
        if (!_initialized)
        {
            InitializeMetal(view);

            _initialized = true;
            // The first callback runs the full bootstrap chain.
            // This frame is yielded, and the next Draw enters the regular frame sequence.
            return;
        }

        if (_pendingResize && _pendingW > 0 && _pendingH > 0)
        {
            DeviceServices.BaseApp.ApplyResolution(_pendingW, _pendingH, 1f, 1f);

            // HandleResize returning false means ResizeSemaphore timed out because background Load is still holding the lock.
            // Resize must not be driven in that state because ResizeCompute would recreate compute-storage textures.
            // Keep _pendingResize and retry on the next frame.
            if (MtlDevice.HandleResize(_pendingW, _pendingH))
            {
                _pendingResize = false;

                DeviceServices.BaseApp?.Resize();
            }
        }

        double newSeconds = _stopwatch.Elapsed.TotalSeconds;
        double deltaSeconds = newSeconds - _previousSeconds;
        _previousSeconds = newSeconds;
        float elapsed = (float)deltaSeconds;

        // Camera and lighting UBO, written before every frame, equivalent to LinuxApp.VKPrimitiveGroup.Update.
        MTLPrimitiveGroup.Update(
            elapsed,
            DeviceServices.BaseApp.CameraPos,
            DeviceServices.BaseApp.CameraTarget,
            DeviceServices.BaseApp.EffectiveSceneLights);

        DeviceServices.BaseApp.Update(elapsed);

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
    /// </summary>
    void InitializeMetal(MTKView view)
    {
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
        MtlDevice.HdrSceneColor = UseOffscreenSceneColor && RenderQuality.Current.HdrSceneColor;

        // Step D of 2-1:
        // the AA tier is finalized during initialization, matching LinuxApp and AndroidApp under contract clause 5.
        // Msaa4x is D3D12-only.
        // Taa depends on the HDR offscreen chain as implemented in step D of 2-3.
        // Fxaa also depends on the HDR offscreen chain because the uber pass bakes luma.
        if (RenderQuality.Current.AntiAliasing == Season.Rendering.AaMode.Msaa4x)
        {
            RenderQuality.Current.AntiAliasing = Season.Rendering.AaMode.Fxaa;
            DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} [RenderQuality] AaMode.Msaa4x is supported only on D3D12, falling back to Fxaa");
        }
        if (RenderQuality.Current.AntiAliasing == Season.Rendering.AaMode.Taa)
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
        if (RenderQuality.Current.AntiAliasing == Season.Rendering.AaMode.Fxaa && !MtlDevice.HdrSceneColor)
        {
            RenderQuality.Current.AntiAliasing = Season.Rendering.AaMode.Off;
            DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} [RenderQuality] Fxaa depends on the HDR offscreen chain, which is not enabled now, falling back to Off");
        }

        // 4) Three pipeline variants, Opaque, Transparent, and Fade.
        // The main PSO is baked against SceneColorFormat.
        // In HDR tiers that means RGBA16Float, and Scene pass is always offscreen.
        // See rule 7-2 in the Metal Device class header.
        Pipeline.Init(MtlDevice.SceneColorFormat, MtlDevice.DepthBufferFormat);

        // 5) Global shared lighting UBO.
        // Both Pbr3D and SpriteQuad buffer b1 read from it,
        // so it must be initialized before Sprite2D.Init and before resource loading.
        MTLPrimitiveGroup.InitLights();

        // 6) 2D orthographic camera.
        MTLSprite2D.Init();

        // 7) FrameContext ring plus the White placeholder texture.
        MtlDevice.CreateGraphicsCommandLists();

        // 8) Inject the IGraphics implementation so BaseApp can run unchanged.
        Season.Basic.Graphics.Instance = new Graphics();

        // 8.5) Offscreen SceneColor for step 2.
        // When non-null, FrameSchedule automatically appends a FinalBlit pass to present it, mirrored with WindowsApp.
        // Under step A of 1-4, the HDR chain switches it to Rgba16Float,
        // and FinalBlit automatically selects the tonemap variant.
        if (UseOffscreenSceneColor)
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
        if (RenderQuality.Current.MotionVectors)
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

        // 8.6) Shadow atlas for 1-5.
        // It is depth-only D32Float, with fixed ShadowAtlasSize squared and no resize tracking, following contract clause 2.
        // After ShadowMap plus RenderShadow are registered as a pair,
        // FrameSchedule activates the Shadow pass before Scene, mirrored with WindowsApp.
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

        // 8.7) Contract clause 1 of 2-2:
        // the AO tier is finalized during initialization.
        // It is a mutually exclusive single-choice tier, and unsupported configurations fall back with a log entry.
        // AO depends on the HDR offscreen chain because it is multiplied in at composition time,
        // and it is mutually exclusive with MSAA because depth cannot be used directly as compute input.
        // Once finalized, create SceneDepth as a full-size depth-only target,
        // used as the explicit Scene-pass DepthTarget and as compute depth input, mirrored with WindowsApp.
        if (RenderQuality.Current.AmbientOcclusion != Season.Rendering.AoMode.Off && !MtlDevice.HdrSceneColor)
        {
            RenderQuality.Current.AmbientOcclusion = Season.Rendering.AoMode.Off;
            DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} [RenderQuality] AO depends on the HDR offscreen chain, which is not enabled now, falling back to Off");
        }
        if (RenderQuality.Current.AmbientOcclusion != Season.Rendering.AoMode.Off
            && RenderQuality.Current.AntiAliasing == Season.Rendering.AaMode.Msaa4x)
        {
            RenderQuality.Current.AmbientOcclusion = Season.Rendering.AoMode.Off;
            DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} [RenderQuality] AO is mutually exclusive with Msaa4x because MSAA depth cannot be used as compute input, falling back to Off");
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

        // Supplement to contract clause 2 of 2-3:
        // MotionVectors requires an explicit depth target because the velocity render pass needs three attachments, color, velocity, and depth.
        // When AO is disabled, SceneDepth may still be null,
        // but MotionVectors still needs a depth attachment, so create it here, mirrored with WindowsApp, LinuxApp, and AndroidApp.
        if (RenderQuality.Current.MotionVectors && Season.Rendering.FrameSchedule.SceneDepth == null)
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
    }

}
