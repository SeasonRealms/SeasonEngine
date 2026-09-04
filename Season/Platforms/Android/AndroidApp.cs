// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Season.Platforms.Shared.LinuxAndroid;
using Season.Platforms.Shared.LinuxAndroid.Vulkan;
using Activity = Android.App.Activity;
using View = Android.Views.View;
using SurfaceFormat = Android.Graphics.Format;
using VkDevice = Season.Platforms.Shared.LinuxAndroid.Vulkan.Device;
using VkPipeline = Season.Platforms.Shared.LinuxAndroid.Vulkan.Pipeline;
using VkResult = Silk.NET.Vulkan.Result;

namespace Season.Platforms.Android;

/// <summary>
/// Vulkan rendering entry point on Android, equivalent to LinuxApp:
/// 1) MainActivity and BaseActivity create <see cref="SurfaceViewVulkan"/> and call SetContentView.
/// 2) When Android creates the SurfaceView surface, through the SurfaceCreated callback,
///    it obtains the native window handle via <c>ANativeWindow_fromSurface</c> and starts Vulkan bootstrap.
/// 3) After bootstrap completes, a dedicated render thread starts and repeatedly drives
///    BaseApp.Update and Draw plus Vulkan BeforeRender and AfterRender.
/// 4) When the Activity is destroyed or the surface becomes invalid, the render thread stops
///    and waits for the GPU to drain.
/// </summary>
public static class AndroidApp
{
    public static Activity MainActivity = null!;

    public static SurfaceViewVulkan SurfaceView = null!;

    static Thread? _renderThread;

    static volatile bool _running;

    static volatile bool _initialized;

    /// <summary>Used by MainActivity to determine whether initialization is happening for the first time, so Activity recreation during rotation can skip creating a new App instance.</summary>
    public static bool IsInitialized => _initialized;

    /// <summary>Whether VkSurfaceKHR and the SwapChain are currently bound to a valid ANativeWindow.
    /// It is set to false and released on SurfaceDestroyed, and SurfaceCreated uses it to decide
    /// between full bootstrap and soft restart.</summary>
    static volatile bool _surfaceAlive;

    static volatile bool _resized;

    static int _currentWidth;

    static int _currentHeight;

    static IntPtr _currentNativeWindow;

    /// <summary>
    /// Injects DeviceServices instances, equivalent to WindowsApp.Run and LinuxApp.Run.
    /// It does not take over UI creation. The UI is created by <see cref="BaseActivity"/>
    /// in OnCreate when it constructs the SurfaceView.
    /// </summary>
    public static void Run(BaseApp app)
    {
        DeviceServices.Initialize(
            baseApp: app,
            core: new AndroidDeviceCore(),
            media: new AndroidMediaPlayer(),
            dialog: new AndroidDialogService(),
            file: new AndroidFileService(),
            image: new AndroidImageService(),
            video: new AndroidVideoPlayerService(),
            gallery: new AndroidGalleryService(),
            record: new AndroidRecordService(),
            download: new AndroidDownloadService(),
            store: new AndroidStoreService(),
            ads: new AndroidAds(),
            windowsFeatures: null
        );
    }

    /// <summary>The SurfaceView surface is ready. There are three paths:
    /// 1) First creation: run full Vulkan bootstrap and start the render thread.
    /// 2) Soft restart, such as rotation or background to foreground:
    ///    the old VkSurface and SwapChain were already released by OnSurfaceLost,
    ///    so rebuild using the new ANativeWindow and restart the render thread.
    /// 3) Size change on an already-live surface, for example split screen:
    ///    only set the resize flag and let the render thread rebuild the SwapChain on the next frame.
    /// </summary>
    static void OnSurfaceAvailable(IntPtr nativeWindow, int width, int height)
    {
        if (width <= 0 || height <= 0) return;

        _currentNativeWindow = nativeWindow;

        if (!_initialized)
        {
            _currentWidth = width;
            _currentHeight = height;

            InitializeVulkan(nativeWindow, width, height);
            _initialized = true;
            _surfaceAlive = true;
            StartRenderLoop();
        }
        else if (!_surfaceAlive)
        {
            _currentWidth = width;
            _currentHeight = height;

            System.Diagnostics.Debug.WriteLine($"[Season] OnSurfaceAvailable SOFT-RESTART: w={width} h={height} nativeWindow=0x{nativeWindow:X}");

            // Soft restart: rebuild VkSurfaceKHR, the SwapChain, and Display attachments
            // with the new ANativeWindow, then restart the render thread.
            // Reuse the existing Instance, Device, Pipeline, and uploaded textures.
            VkDevice.RecreateSurfaceAndSwapChain(
                nativeWindow,
                instHandle => CreateAndroidSurface(instHandle, nativeWindow),
                width, height);

            System.Diagnostics.Debug.WriteLine($"[Season] After RecreateSurfaceAndSwapChain: SwapChain.Extent=({VkDevice.SwapChain.Extent.Width}x{VkDevice.SwapChain.Extent.Height})");

            DeviceServices.BaseApp.ApplyResolution(width, height, 1f, 1f);
            DeviceServices.BaseApp?.Resize();

            _surfaceAlive = true;
            StartRenderLoop();
        }
        else
        {
            // Already initialized and the surface is still valid.
            // Mark resize only when the dimensions actually changed,
            // preventing redundant rebuilds when SurfaceCreated and SurfaceChanged fire back to back.
            if (_currentWidth != width || _currentHeight != height)
            {
                _currentWidth = width;
                _currentHeight = height;
                _resized = true;
            }
        }
    }

    /// <summary>SurfaceCreated callback: only cache the nativeWindow and do not bootstrap Vulkan immediately, because the final size is provided by SurfaceChanged.</summary>
    internal static void OnNativeWindowReady(IntPtr nativeWindow)
    {
        _currentNativeWindow = nativeWindow;
    }

    /// <summary>SurfaceChanged callback: the surface now has its final size.
    /// It is triggered on first launch, rotation, background return, and split screen.
    /// This is the unified entry point for Vulkan bootstrap, soft restart, and resize.</summary>
    internal static void OnSurfaceChangedReady(int width, int height)
    {
        if (_currentNativeWindow == IntPtr.Zero) return;
        if (width <= 0 || height <= 0) return;

        OnSurfaceAvailable(_currentNativeWindow, width, height);
    }

    /// <summary>SurfaceDestroyed covers Activity pause, screen rotation, and moving to the background.
    /// It stops the render thread, waits for the GPU to drain, destroys VkSurfaceKHR and the SwapChain,
    /// and lets the next SurfaceCreated rebuild them through the soft-restart path.</summary>
    internal static void OnSurfaceLost()
    {
        if (_running)
        {
            _running = false;
            _renderThread?.Join();
            _renderThread = null;
        }

        if (_initialized && _surfaceAlive)
        {
            VkDevice.ReleaseSurfaceAndSwapChain();
            _surfaceAlive = false;
        }

        // The previous ANativeWindow became invalid together with SurfaceDestroyed.
        // Clear it to avoid accidental SurfaceChanged handling before the next SurfaceCreated.
        _currentNativeWindow = IntPtr.Zero;
    }

    /// <summary>
    /// Factory for VK_KHR_android_surface, reused by InitializeVulkan and RecreateSurfaceAndSwapChain.
    /// The caller provides the currently valid ANativeWindow pointer, which must not be cached
    /// because the old handle becomes invalid after SurfaceDestroyed.
    /// </summary>
    static unsafe ulong CreateAndroidSurface(ulong instanceHandle, IntPtr nativeWindow)
    {
        var instance = new Instance(unchecked((nint)instanceHandle));
        if (!VkDevice.Vk.TryGetInstanceExtension(instance, out KhrAndroidSurface androidSurfaceExt))
            throw new Exception("VK_KHR_android_surface extension unavailable");

        var info = new AndroidSurfaceCreateInfoKHR
        {
            SType = StructureType.AndroidSurfaceCreateInfoKhr,
            PNext = null,
            Flags = 0,
            Window = (IntPtr*)nativeWindow
        };

        if (androidSurfaceExt.CreateAndroidSurface(instance, in info, null, out var surface) != VkResult.Success)
            throw new Exception("vkCreateAndroidSurfaceKHR failed");

        return surface.Handle;
    }

    /// <summary>Offscreen SceneColor switch for step 2. When false, it falls back to the step-1 direct backbuffer path, mirroring WindowsApp.</summary>
    static readonly bool UseOffscreenSceneColor = true;

    /// <summary>
    /// Full Vulkan bootstrap chain, equivalent to <c>LinuxApp.InitializeVulkan</c>
    /// and <c>WindowsApp.CreateInstance</c>:
    /// Device.Init → CreateSwapChain → CreateDescriptorHeapsAndViews → Pipeline.Init →
    /// VKPrimitiveGroup.InitLights → VKSprite2D.Init → CreateGraphicsCommandLists →
    /// Inject Graphics.Instance and then call BaseApp.Create().
    /// </summary>
    static unsafe void InitializeVulkan(IntPtr window, int width, int height)
    {
        // Render-quality tier setup 1-4, mirroring WindowsApp and LinuxApp.
        // The cross-platform contract is documented in the RenderQuality summary.
        // This must be finalized before Pipeline.Init, where the main PSO is baked
        // from RenderPass-derived formats.
        // The HDR path depends on offscreen SceneColor because FinalBlit performs tone mapping at the end.
        // Direct rendering therefore falls back to the LDR baseline.
        VkDevice.HdrSceneColor = UseOffscreenSceneColor && RenderQuality.Current.HdrSceneColor;

        // Anti-aliasing contract 2-1 clause 5:
        // finalize the AA tier during initialization, where options are mutually exclusive.
        // If the capability is unavailable, fall back and log the change so runtime stays branch-free.
        // Msaa4x is a D3D12 legacy mode and this backend has no MSAA offscreen path, so it falls back to Fxaa.
        // Taa and Fxaa both depend on the HDR offscreen path,
        // where the post uber pass finishes with tone mapping.
        // Fallback order is Taa -> Fxaa -> Off, mirroring WindowsApp and LinuxApp.
        if (RenderQuality.Current.AntiAliasing == Season.Rendering.AaMode.Msaa4x)
        {
            RenderQuality.Current.AntiAliasing = Season.Rendering.AaMode.Fxaa;
            DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} [RenderQuality] AaMode.Msaa4x is only supported on D3D12, falling back to Fxaa");
        }
        if (RenderQuality.Current.AntiAliasing == Season.Rendering.AaMode.Taa)
        {
            // Contract 2-3 clause 1: selecting Taa forces motion-vector infrastructure to be enabled,
            // because TAA is invalid without velocity.
            RenderQuality.Current.MotionVectors = true;

            // Contract 2-3 clause 10: resolve runs in linear HDR space before tone mapping,
            // with both input and output in rgba16float.
            // It therefore depends on the HDR offscreen path.
            // If unavailable, fall back to Fxaa, while Fxaa's own HDR dependency is checked below.
            // Failure to register TaaEffect itself does not trigger fallback here,
            // because it has an internal bypass where TaaActive and SceneColorOverride stay false or null.
            // The image then falls back to non-TAA SceneColor without jitter, per clauses 14 and 15.
            if (!VkDevice.HdrSceneColor)
            {
                RenderQuality.Current.AntiAliasing = Season.Rendering.AaMode.Fxaa;
                DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} [RenderQuality] Taa depends on the HDR offscreen path, which is currently disabled, falling back to Fxaa; MotionVectors remains enabled");
            }
        }
        if (RenderQuality.Current.AntiAliasing == Season.Rendering.AaMode.Fxaa && !VkDevice.HdrSceneColor)
        {
            RenderQuality.Current.AntiAliasing = Season.Rendering.AaMode.Off;
            DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} [RenderQuality] Fxaa depends on the HDR offscreen path, which is currently disabled, falling back to Off");
        }

        // 1) Android instance extensions: fixed to VK_KHR_surface plus VK_KHR_android_surface.
        var androidExts = new[] { "VK_KHR_surface", "VK_KHR_android_surface" };

        // 2) Bootstrap the Vulkan Instance, Surface, Device, and queues.
        VkDevice.Init(
            window: window,
            debug: true,
            surfaceExtensions: androidExts,
            createSurface: instHandle => CreateAndroidSurface(instHandle, window));

        // 3) Create the SwapChain and upper-layer resource manager.
        VkDevice.CreateSwapChain(width, height);

        // 4) Display（Depth + RenderPass + Framebuffers）
        VkDevice.CreateDescriptorHeapsAndViews();

        // 5) Initialize the three Pipeline variants, which depend on the RenderPass.
        VkPipeline.Init(VkDevice.Display.RenderPass);

        // 6) Initialize the globally shared lighting UBO before Sprite2D.Init and resource loading.
        VKPrimitiveGroup.InitLights();

        // 7) Set up the 2D orthographic camera.
        VKSprite2D.Init();

        // 8) Create per-frame CommandPool, buffers, semaphores, and the white placeholder texture.
        VkDevice.CreateGraphicsCommandLists();

        // 9) Inject the IGraphics implementation so BaseApp can run unchanged.
        Season.Basic.Graphics.Instance = new Season.Platforms.Shared.LinuxAndroid.Graphics();

        // 10) Offscreen SceneColor for step 2:
        // when not null, FrameSchedule automatically appends the FinalBlit pass to present on screen,
        // mirroring WindowsApp.
        // In render-quality step 1-4 stage A, the HDR path switches to RGBA16F
        // and FinalBlit automatically uses the tone-mapping variant.
        if (UseOffscreenSceneColor)
        {
            Season.Rendering.FrameSchedule.SceneColor = Season.Basic.Graphics.Instance.CreateRenderTarget(new Season.Rendering.RenderTargetDesc
            {
                ColorFormat = VkDevice.HdrSceneColor
                    ? Season.Rendering.RtFormat.Rgba16Float
                    : Season.Rendering.RtFormat.BackbufferCompatible,
                MatchBackbufferSize = true,
                SampleCount = 1,
            });
        }

        // Contract 2-3 clause 2:
        // SceneVelocity uses full-size Rg16Float.
        // When non-null, the Scene pass becomes a three-target pass with color, velocity, and depth.
        // When MotionVectors is disabled it stays null, leaving no residual path, mirroring WindowsApp.
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

        // Step D of contract 2-1 clause 4:
        // under the FXAA tier, activate the Post slot by creating PostColor,
        // which is LDR and matches the backbuffer format and size,
        // together with RenderPost, the uber pass that combines tone mapping and bloom
        // while writing luma into alpha.
        // Once both are registered, FrameSchedule inserts the Post pass automatically,
        // and FinalBlit degenerates into the FXAA present pass.
        // Under non-FXAA tiers both remain null, leaving no residual path,
        // mirroring WindowsApp and LinuxApp.
        if (RenderQuality.Current.AntiAliasing == Season.Rendering.AaMode.Fxaa
            && Season.Rendering.FrameSchedule.SceneColor != null)
        {
            Season.Rendering.FrameSchedule.PostColor = Season.Basic.Graphics.Instance.CreateRenderTarget(new Season.Rendering.RenderTargetDesc
            {
                ColorFormat = Season.Rendering.RtFormat.BackbufferCompatible,
                MatchBackbufferSize = true,
                SampleCount = 1,
            });
            if (Season.Basic.Graphics.Instance is Season.Platforms.Shared.LinuxAndroid.Graphics postGraphics)
                Season.Rendering.FrameSchedule.RenderPost = postGraphics.RenderPostPass;
        }

        // Render-quality 1-5 shadow atlas:
        // a depth-only D32Float atlas with fixed ShadowAtlasSize squared that does not resize, per contract clause 2.
        // Once ShadowMap and RenderShadow are both registered during initialization,
        // FrameSchedule activates the Shadow pass before Scene.
        // The shadow PSO is baked against the depth-only RenderPass,
        // so it must be delayed until the shadow render target exists and can provide its RenderPass.
        if (RenderQuality.Current.ShadowsEnabled)
        {
            var shadowRT = Season.Basic.Graphics.Instance.CreateRenderTarget(new Season.Rendering.RenderTargetDesc
            {
                DepthFormat = Season.Rendering.RtFormat.D32Float,
                MatchBackbufferSize = false,
                Width = (uint)RenderQuality.Current.ShadowAtlasSize,
                Height = (uint)RenderQuality.Current.ShadowAtlasSize,
                SampleCount = 1,
            });
            Season.Rendering.FrameSchedule.ShadowMap = shadowRT;
            if (shadowRT is VKRenderTarget vkShadowRT)
                VkPipeline.EnsureShadowPipeline(vkShadowRT.RenderPass);
            if (Season.Basic.Graphics.Instance is Season.Platforms.Shared.LinuxAndroid.Graphics shadowGraphics)
                Season.Rendering.FrameSchedule.RenderShadow = shadowGraphics.RenderShadowPass;
        }

        // Contract 2-2 clause 1:
        // finalize the AO tier during initialization, where options are mutually exclusive.
        // If the capability is unavailable, fall back and log it.
        // AO depends on the HDR offscreen path because it is multiplied in during composition,
        // and it is incompatible with MSAA because MSAA depth cannot be used directly as compute input.
        // Once finalized, create SceneDepth as a full-size depth-only target,
        // used explicitly as the Scene pass depth target and compute depth input, mirroring WindowsApp.
        if (RenderQuality.Current.AmbientOcclusion != Season.Rendering.AoMode.Off && !VkDevice.HdrSceneColor)
        {
            RenderQuality.Current.AmbientOcclusion = Season.Rendering.AoMode.Off;
            DeviceServices.BaseApp.AddLog(LogType.None, $"{DateTime.UtcNow} [RenderQuality] AO depends on the HDR offscreen path, which is currently disabled, falling back to Off");
        }
        if (RenderQuality.Current.AmbientOcclusion != Season.Rendering.AoMode.Off
            && RenderQuality.Current.AntiAliasing == Season.Rendering.AaMode.Msaa4x)
        {
            RenderQuality.Current.AmbientOcclusion = Season.Rendering.AoMode.Off;
            DeviceServices.BaseApp.AddLog(LogType.None, $"{DateTime.UtcNow} [RenderQuality] AO is incompatible with Msaa4x because MSAA depth cannot be used as compute input, falling back to Off");
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

        // Supplement to contract 2-3 clause 2:
        // MotionVectors requires an explicit depth target because the velocity RenderPass expects
        // three attachments: color, velocity, and depth.
        // When AO is disabled, SceneDepth may be null, but MotionVectors still needs a depth attachment,
        // so fill it in here.
        if (RenderQuality.Current.MotionVectors && Season.Rendering.FrameSchedule.SceneDepth == null)
        {
            Season.Rendering.FrameSchedule.SceneDepth = Season.Basic.Graphics.Instance.CreateRenderTarget(new Season.Rendering.RenderTargetDesc
            {
                DepthFormat = Season.Rendering.RtFormat.D32Float,
                MatchBackbufferSize = true,
                SampleCount = 1,
            });
        }

        DeviceServices.BaseApp.ApplyResolution(width, height, 1f, 1f);
        DeviceServices.BaseApp.Create();
    }

    static void StartRenderLoop()
    {
        _running = true;
        _renderThread = new Thread(RenderLoopBody)
        {
            IsBackground = true,
            Name = "VulkanRenderThread"
        };
        _renderThread.Start();
    }

    /// <summary>Main render-thread loop, equivalent to LinuxApp.RunLoop, except that events are pushed asynchronously from the Android UI thread.</summary>
    static void RenderLoopBody()
    {
        var stopWatch = System.Diagnostics.Stopwatch.StartNew();
        double previousSeconds = 0;
        int frameCounter = 0;

        try
        {
        while (_running)
        {
            double newSeconds = stopWatch.Elapsed.TotalSeconds;
            float elapsed = (float)(newSeconds - previousSeconds);
            previousSeconds = newSeconds;

            frameCounter++;
            // Diagnostic log: print the first few frames to help locate crash positions.
            if (frameCounter <= 5)
                System.Diagnostics.Debug.WriteLine($"[Android] Frame {frameCounter} started");

            // Rebuild the SwapChain, equivalent to DX HandleResize.
            if (_resized)
            {
                _resized = false;
                int w = _currentWidth;
                int h = _currentHeight;
                IntPtr native = _currentNativeWindow;
                if (w > 0 && h > 0 && native != IntPtr.Zero)
                {
                    // Screen rotation can be intercepted by ConfigChanges so that only SurfaceChanged fires
                    // while ANativeWindow is not rebuilt.
                    // In that case, the old VkSurfaceKHR may keep caps.CurrentTransform locked to Rotate90 or Rotate270,
                    // applied by the compositor, while the newly passed width and height are already in the new screen orientation.
                    // That makes the SwapChain image extent, which is still pre-rotation, differ from the Display framebuffer
                    // and viewport in the new orientation.
                    // The result is that Mesh3D and Model perspective projection becomes misaligned and invisible,
                    // while only Sprite2D survives as stretched NDC rendering.
                    //
                    // The fix is to destroy the old VkSurfaceKHR and rebuild from the same ANativeWindow.
                    // At that point Android has already aligned the ANativeWindow to the new screen orientation,
                    // so the queried caps usually return to Identity and image extent matches the viewport again.
                    // This is equivalent to the background/foreground soft-restart path
                    // and reuses the existing Instance, Device, Pipeline, and textures.
                    if (frameCounter <= 5)
                        System.Diagnostics.Debug.WriteLine($"[Android] Frame {frameCounter}: Resize...");
                    VkDevice.ReleaseSurfaceAndSwapChain();
                    VkDevice.RecreateSurfaceAndSwapChain(
                        native,
                        instHandle => CreateAndroidSurface(instHandle, native),
                        w, h);

                    DeviceServices.BaseApp.ApplyResolution(w, h, 1f, 1f);
                    DeviceServices.BaseApp?.Resize();
                }
            }

            // Camera and lighting UBOs, written before each frame.
            if (frameCounter <= 5)
                System.Diagnostics.Debug.WriteLine($"[Android] Frame {frameCounter}: VKPrimitiveGroup.Update...");
            VKPrimitiveGroup.Update(
                elapsed,
                DeviceServices.BaseApp.CameraPos,
                DeviceServices.BaseApp.CameraTarget,
                DeviceServices.BaseApp.EffectiveSceneLights);

            if (frameCounter <= 5)
                System.Diagnostics.Debug.WriteLine($"[Android] Frame {frameCounter}: BaseApp.Update...");
            DeviceServices.BaseApp.Update(elapsed);

            var backgroundColor = DeviceServices.BaseApp.BackgroundColor;
            VkDevice.BackgroundColor = backgroundColor;
            VkDevice.Display?.SetClearColor(backgroundColor);

            // Frame recording sequence:
            // Acquire -> FrameSchedule, meaning BeginPass, draw, EndPass -> Submit -> Present.
            if (frameCounter <= 5)
                System.Diagnostics.Debug.WriteLine($"[Android] Frame {frameCounter}: BeforeRender...");
            if (!VkDevice.BeforeRender())
            {
                // Consecutive OutOfDate states, such as during rotation or background switching:
                // skip this frame and retry next frame.
                // If the surface size actually changed, SurfaceChanged will set _resized
                // and the code will follow the soft-restart path.
                continue;
            }

            if (frameCounter <= 5)
                System.Diagnostics.Debug.WriteLine($"[Android] Frame {frameCounter}: FlushTextAtlas...");
            Season.Basic.Graphics.Instance.FlushTextAtlas();

            // Pass scheduling for step 1: Begin and End of the Scene pass are driven by FrameSchedule.
            if (frameCounter <= 5)
                System.Diagnostics.Debug.WriteLine($"[Android] Frame {frameCounter}: FrameSchedule.Execute...");
            Season.Rendering.FrameSchedule.Execute(Season.Basic.Graphics.Instance, DeviceServices.BaseApp, backgroundColor);

            if (frameCounter <= 5)
                System.Diagnostics.Debug.WriteLine($"[Android] Frame {frameCounter}: AfterRender...");
            VkDevice.AfterRender();
            if (frameCounter <= 5)
                System.Diagnostics.Debug.WriteLine($"[Android] Frame {frameCounter}: completed");
        }
        }
        catch (Exception ex)
        {
            // Render-thread exception: log it for diagnostics and do not rethrow,
            // allowing the thread to exit normally.
            // The upper OnSurfaceLost path will observe _running and perform cleanup.
            System.Diagnostics.Debug.WriteLine($"[FATAL] RenderLoopBody exception at frame {frameCounter}: {ex.GetType().Name}: {ex.Message}");
            System.Diagnostics.Debug.WriteLine(ex.StackTrace);
            DeviceServices.BaseApp?.AddLog(LogType.Error, $"{System.DateTime.UtcNow} [Android] RenderLoopBody exception at frame {frameCounter}: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

[Activity(LaunchMode = LaunchMode.SingleTop, AlwaysRetainTaskState = true,
    ScreenOrientation = ScreenOrientation.FullSensor,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class BaseActivity : Activity
{
    protected override void OnCreate(Bundle savedInstanceState)
    {
        RequestWindowFeature(WindowFeatures.NoTitle);

        base.OnCreate(savedInstanceState);

        // Register MainActivity for services such as AndroidDeviceCore and AndroidFileService.
        AndroidApp.MainActivity = this;

        base.Window!.AddFlags(WindowManagerFlags.Fullscreen);
        base.Window.AddFlags(WindowManagerFlags.KeepScreenOn);
        base.Window.AddFlags(WindowManagerFlags.TranslucentStatus);

        base.Window.Attributes!.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;

        try
        {
            var insetsController = base.Window.InsetsController;

            if (insetsController != null)
            {
                insetsController.Hide(WindowInsets.Type.StatusBars() | WindowInsets.Type.NavigationBars());
                insetsController.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
            }

            base.Window.SetDecorFitsSystemWindows(false);
            base.Window.DecorView.WindowInsetsController?.Hide(WindowInsets.Type.NavigationBars());
            base.Window.InsetsController?.Hide(WindowInsets.Type.StatusBars());
        }
        catch (System.Exception)
        {
            // Backward-compatible tolerance for older APIs:
            // InsetsController is available only on Android 11 and above.
        }

        // Create the Vulkan rendering SurfaceView and install it as the root view.
        // The system triggers SurfaceCreated asynchronously after base.OnCreate,
        // and Vulkan bootstrap begins there.
        AndroidApp.SurfaceView = new SurfaceViewVulkan(this);
        SetContentView(AndroidApp.SurfaceView);
    }

    protected override void OnPause()
    {
        base.OnPause();
    }

    protected override void OnResume()
    {
        base.OnResume();
    }

    protected override void OnDestroy()
    {
        // Defensive path: force the render thread to stop when the Activity is destroyed.
        AndroidApp.OnSurfaceLost();
        base.OnDestroy();
    }

    protected override void OnActivityResult(int requestCode, [GeneratedEnum] global::Android.App.Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, [GeneratedEnum] Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
    }
}

/// <summary>
/// Vulkan rendering surface, equivalent to SDL CreateWindow(WindowFlags.Vulkan) on Linux.
/// It implements <see cref="ISurfaceHolderCallback"/> and bridges SurfaceView lifecycle events
/// into <see cref="AndroidApp"/>:
/// SurfaceCreated obtains ANativeWindow and triggers InitializeVulkan plus render-thread startup.
/// SurfaceChanged triggers SwapChain rebuild.
/// SurfaceDestroyed stops the render thread and waits for the device to go idle.
/// </summary>
public class SurfaceViewVulkan : SurfaceView, ISurfaceHolderCallback, View.IOnTouchListener
{
    // Previous-frame distance between two fingers during pinch gestures,
    // used to derive PoZ increments, equivalent to the desktop mouse wheel.
    float _prevPinchDistance;

    bool _isPinching;

    public SurfaceViewVulkan(Context context) : base(context)
    {
        Holder?.AddCallback(this);

        // Important: implementing IOnTouchListener alone does not make the system dispatch events.
        // The view must explicitly register itself as the touch listener.
        // Also enable focus and touch focus so SurfaceView can receive MotionEvent reliably.
        Focusable = true;
        FocusableInTouchMode = true;
        SetOnTouchListener(this);
    }

    public void SurfaceCreated(ISurfaceHolder holder)
    {
        if (holder.Surface is null) return;

        // ANativeWindow_fromSurface increments the reference count,
        // and it must be paired with ANativeWindow_release during SurfaceDestroyed.
        var nativeWindow = AndroidRuntime.ANativeWindow_fromSurface(JNIEnv.Handle, holder.Surface.Handle);
        if (nativeWindow == IntPtr.Zero) return;

        System.Diagnostics.Debug.WriteLine($"[Season] SurfaceCreated: nativeWindow=0x{nativeWindow:X}");
        AndroidApp.OnNativeWindowReady(nativeWindow);
    }

    bool IOnTouchListener.OnTouch(View v, MotionEvent e)
    {
        var posX = e.GetX(e.ActionIndex) / DeviceServices.BaseApp.Scale;

        var posY = e.GetY(e.ActionIndex) / DeviceServices.BaseApp.Scale;

        switch (e.ActionMasked)
        {
            // DOWN
            case MotionEventActions.Down:
                TouchService.isDown = true;
                TouchService.PoX = (int)posX;
                TouchService.PoY = (int)posY;
                break;

            // Second finger touched down: enter pinch mode and record the initial finger distance.
            case MotionEventActions.PointerDown:
                if (e.PointerCount >= 2)
                {
                    _prevPinchDistance = ComputePinchDistance(e);
                    _isPinching = true;
                    // Suspend single-finger drag semantics during pinching
                    // to avoid rotating or panning the camera at the same time.
                    TouchService.isDown = false;
                }
                else
                {
                    TouchService.isDown = true;
                    TouchService.PoX = (int)posX;
                    TouchService.PoY = (int)posY;
                }
                break;

            // UP: the last finger was lifted, so the gesture ends completely.
            case MotionEventActions.Up:
                TouchService.isDown = false;
                TouchService.PoX = (int)posX;
                TouchService.PoY = (int)posY;
                _isPinching = false;
                _prevPinchDistance = 0f;
                break;

            // A finger was lifted mid-gesture: if only one finger remains, exit pinch mode.
            case MotionEventActions.PointerUp:
                if (e.PointerCount <= 2)
                {
                    _isPinching = false;
                    _prevPinchDistance = 0f;
                }
                break;

            // MOVE
            case MotionEventActions.Move:
                if (_isPinching && e.PointerCount >= 2)
                {
                    // Two-finger pinch, equivalent to desktop MouseWheelDelta:
                    // fingers moving apart, where distance grows, means zoom in,
                    // which corresponds to wheel up and decreases PoZ.
                    // fingers moving together, where distance shrinks, means zoom out,
                    // which corresponds to wheel down and increases PoZ.
                    float curDist = ComputePinchDistance(e);
                    float delta = curDist - _prevPinchDistance;
                    _prevPinchDistance = curDist;

                    if (delta != 0f)
                    {
                        if (TouchService.PoZ is null)
                            TouchService.PoZ = 0;

                        TouchService.PoZ += (int)delta;
                    }
                }
                else
                {
                    posX = e.GetX(0);
                    posY = e.GetY(0);
                    TouchService.IsMoved = true;
                    TouchService.PoX = (int)(posX / DeviceServices.BaseApp.Scale);
                    TouchService.PoY = (int)(posY / DeviceServices.BaseApp.Scale);
                }
                break;

            // CANCEL, OUTSIDE
            case MotionEventActions.Cancel:
            case MotionEventActions.Outside:
                TouchService.isDown = false;
                TouchService.PoX = (int)posX;
                TouchService.PoY = (int)posY;
                _isPinching = false;
                _prevPinchDistance = 0f;
                break;
        }

        return true;
    }

    /// <summary>Computes the Euclidean distance, in pixels, between the first two fingers in a MotionEvent.</summary>
    static float ComputePinchDistance(MotionEvent e)
    {
        float dx = e.GetX(0) - e.GetX(1);
        float dy = e.GetY(0) - e.GetY(1);
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    public void SurfaceChanged(ISurfaceHolder holder, SurfaceFormat format, int width, int height)
    {
        System.Diagnostics.Debug.WriteLine($"[Season] SurfaceChanged: width={width} height={height}");
        // At this point width and height are guaranteed to be the final surface size
        // because layout has already completed.
        // The actual Vulkan bootstrap, soft restart, and resize routing all go through OnSurfaceChangedReady.
        AndroidApp.OnSurfaceChangedReady(width, height);
    }

    public void SurfaceDestroyed(ISurfaceHolder holder)
    {
        AndroidApp.OnSurfaceLost();
    }
}
