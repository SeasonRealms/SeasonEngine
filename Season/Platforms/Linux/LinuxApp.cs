// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Season.Platforms.Shared.LinuxAndroid;
using Season.Platforms.Shared.LinuxAndroid.Vulkan;
using VkDevice = Season.Platforms.Shared.LinuxAndroid.Vulkan.Device;
using VkPipeline = Season.Platforms.Shared.LinuxAndroid.Vulkan.Pipeline;

namespace Season.Platforms.Linux;

// Known WSLg issue: [WARN:COPY MODE] appears, the taskbar shows a preview,
// but the window itself is not displayed.
//sudo mkdir -p /mnt/shared_memory
//sudo mount -t tmpfs tmpfs /mnt/shared_memory
//wsl --shutdown

public static class LinuxApp
{

    static bool _resized;

    public static unsafe void Run(BaseApp app)
    {
        // Global exception capture as the last line of defense for async void,
        // thread-pool work, and failures before a native crash.
        // Under WSL, the Vulkan driver can trigger native crashes in specific situations,
        // and these handlers can still capture part of those scenarios.
        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            Console.WriteLine($"[FATAL] UnhandledException: {ex?.GetType().Name}: {ex?.Message}");
            Console.WriteLine(ex?.StackTrace);
            Console.Error.Flush();
        };
        TaskScheduler.UnobservedTaskException += (sender, e) =>
        {
            Console.WriteLine($"[FATAL] UnobservedTaskException: {e.Exception?.GetType().Name}: {e.Exception?.Message}");
            Console.WriteLine(e.Exception?.StackTrace);
            Console.Error.Flush();
        };
        AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
        {
            Console.WriteLine($"[INFO] ProcessExit, exit code: {Environment.ExitCode}");
            Console.Error.Flush();
        };

        try
        {
        DeviceServices.Initialize(
            baseApp: app,
            core: new LinuxDeviceCore(),
            media: new LinuxMediaPlayer(),
            dialog: new LinuxDialogService(),
            file: new LinuxFileService(),
            image: new LinuxImageService(),
            video: new LinuxVideoPlayerService(),
            gallery: new LinuxGalleryService(),
            record: new LinuxRecordService(),
            download: new LinuxDownloadService(),
            store: new LinuxStoreService(),
            ads: null,
            windowsFeatures: null
        );

        Gtk.Application.Init();

        var des = RuntimeInformation.OSDescription;

        var art = RuntimeInformation.ProcessArchitecture;

        var basedi = AppContext.BaseDirectory;

        var video = 0x00000020u;

        if (!SDL.Init(video))
        {
            var text = Marshal.PtrToStringAnsi((IntPtr)SDL.GetError());

            throw new Exception("SDL initialization error " + text);
        }

        var primaryDisplayID = SDL.GetPrimaryDisplay();

        var modePtr = SDL.GetDesktopDisplayMode(primaryDisplayID);

        var mode = Marshal.PtrToStructure<SDL_DisplayMode>(modePtr);

        var width = mode.w;

        var height = mode.h;

        // Use the Vulkan path, equivalent to the DX SwapChainPanel flow,
        // with SDL creating VkSurfaceKHR.
        var flags = WindowFlags.Vulkan | WindowFlags.Resizable;
        var rect = GetInitialWindowRect(app, width, height, ref flags);

        var window = SDL.CreateWindow(app.Title, (int)rect.Width, (int)rect.Height, flags);

        if (!app.Settings.WindowState.FullScreen && !app.Settings.WindowState.Maximized && app.Settings.WindowState.Width > 0 && app.Settings.WindowState.Height > 0)
        {
            SDL.SetWindowPosition(window, rect.X, rect.Y);
        }

        SDL.ShowWindow(window);

        // Under X11 and Wayland, window size is confirmed asynchronously by the window manager.
        // Reading GetWindowSizeInPixels immediately after ShowWindow still returns the requested creation size.
        // The actual size only arrives once the window manager sends configure events
        // through RESIZED or PIXEL_SIZE_CHANGED, which happens later inside RunLoop.
        // If size is not stabilized first, every initialization-time resource sized from DeviceResolution
        // will be wrong, especially TAA ping-pong storage textures.
        // Under contract clause 15, resize does not rebuild them and a size mismatch permanently bypasses TAA,
        // which means TAA would never work on this backend.
        // SDL_SyncWindow blocks once until the window manager confirms all pending window state,
        // after which the reported size is the real pixel size.
        SDL.SyncWindow(window);

        // Read the actual pixel size, which can differ from logical size on high-DPI displays.
        SDL.GetWindowSizeInPixels(window, out int pixelW, out int pixelH);
        DeviceServices.BaseApp.ApplyResolution(pixelW, pixelH, 1f, 1f);

        InitializeVulkan(window, pixelW, pixelH);

        RunLoop(window);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FATAL] Run exception: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            Console.Error.Flush();
            // Console.WriteLine may be compiled out in Release,
            // and Linux desktop sessions often have no listener attached,
            // which makes exception output disappear entirely and look like the program exited silently.
            // Also write to stderr, which is not affected by build configuration in the same way.
            Console.Error.WriteLine($"[FATAL] Run exception: {ex}");
            Console.Error.Flush();
            throw;
        }
    }

    /// <summary>Offscreen SceneColor switch for step 2. When false, it falls back to the step-1 direct backbuffer path, mirroring WindowsApp.</summary>
    static readonly bool UseOffscreenSceneColor = true;

    /// <summary>
    /// Full Vulkan bootstrap chain, corresponding one to one with WindowsApp.CreateInstance:
    /// Device.Init → CreateSwapChain → CreateDescriptorHeapsAndViews → Pipeline.Init →
    /// VKPrimitiveGroup.InitLights → VKSprite2D.Init → CreateGraphicsCommandLists →
    /// Inject Graphics.Instance and call BaseApp.Create().
    /// </summary>
    static unsafe void InitializeVulkan(IntPtr window, int width, int height)
    {
        // Render-quality tier setup 1-4, mirroring WindowsApp.
        // See the RenderQuality summary for the cross-platform contract.
        // This must be finalized before Pipeline.Init,
        // where the main PSO is baked from RenderPass-derived formats.
        // The HDR chain depends on offscreen SceneColor because FinalBlit performs tone mapping at the end,
        // so direct rendering falls back to the LDR baseline.
        VkDevice.HdrSceneColor = UseOffscreenSceneColor && RenderQuality.Current.HdrSceneColor;

        // Anti-aliasing contract 2-1 clause 5:
        // finalize the AA tier during initialization, with mutually exclusive choices.
        // If the required capability is unavailable, fall back and log it so runtime stays branch-free.
        // Msaa4x is a D3D12 legacy mode and this backend has no MSAA offscreen chain, so it falls back to Fxaa.
        // Taa and Fxaa both depend on the HDR offscreen chain,
        // where the post uber pass finishes with tone mapping.
        // Fallback order is Taa -> Fxaa -> Off, mirroring WindowsApp.
        if (RenderQuality.Current.AntiAliasing == Season.Rendering.AaMode.Msaa4x)
        {
            RenderQuality.Current.AntiAliasing = Season.Rendering.AaMode.Fxaa;
            DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} [RenderQuality] AaMode.Msaa4x is only supported on D3D12, falling back to Fxaa");
        }
        if (RenderQuality.Current.AntiAliasing == Season.Rendering.AaMode.Taa)
        {
            // Contract 2-3 clause 1:
            // selecting Taa forces motion-vector infrastructure to be enabled,
            // because TAA is invalid without velocity.
            RenderQuality.Current.MotionVectors = true;

            // Contract 2-3 clause 10:
            // resolve runs in linear HDR space before tone mapping,
            // with both input and output in rgba16float.
            // It therefore depends on the HDR offscreen chain.
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

        // 1) Resolve instance extensions reported by SDL.
        IntPtr extArray = SDL.Vulkan_GetInstanceExtensions(out uint extCount);
        string[] sdlExts = new string[extCount];
        for (int i = 0; i < extCount; i++)
        {
            IntPtr p = Marshal.ReadIntPtr(extArray, i * IntPtr.Size);
            sdlExts[i] = Marshal.PtrToStringAnsi(p) ?? string.Empty;
        }

        // 2) Bootstrap the Vulkan Instance, Surface, Device, and queues.
        VkDevice.Init(
            window: window,
            debug: true,
            surfaceExtensions: sdlExts,
            createSurface: instHandle =>
            {
                if (!SDL.Vulkan_CreateSurface(window, (IntPtr)instHandle, IntPtr.Zero, out ulong surf))
                {
                    var err = Marshal.PtrToStringAnsi((IntPtr)SDL.GetError());
                    throw new Exception("SDL_Vulkan_CreateSurface failed: " + err);
                }
                return surf;
            });

        // 3) Create the SwapChain and upper-layer resource manager.
        VkDevice.CreateSwapChain(width, height);

        // 4) Display（Depth + RenderPass + Framebuffers）
        VkDevice.CreateDescriptorHeapsAndViews();

        // 5) Initialize the three Pipeline variants, which depend on the RenderPass.
        VkPipeline.Init(VkDevice.Display.RenderPass);

        // 6) Initialize the globally shared lighting UBO.
        // Pbr3D and SpriteQuad b1 both read from it,
        // so it must be ready before Sprite2D.Init and resource loading.
        VKPrimitiveGroup.InitLights();

        // 7) Set up the 2D orthographic camera.
        VKSprite2D.Init();

        // 8) Create per-frame CommandPool, buffers, semaphores, and the white placeholder texture.
        VkDevice.CreateGraphicsCommandLists();

        // 9) Inject the IGraphics implementation so BaseApp can run unchanged.
        Season.Basic.Graphics.Instance = new Season.Platforms.Shared.LinuxAndroid.Graphics();

        // 10) Offscreen SceneColor for step 2:
        // when not null, FrameSchedule automatically appends FinalBlit to present on screen,
        // mirroring WindowsApp.
        // Under render-quality step 1-4 stage A, the HDR chain uses RGBA16F
        // and FinalBlit automatically switches to the tone-mapping variant.
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
        // which is LDR and matches the backbuffer in format and size,
        // together with RenderPost, the uber pass that combines tone mapping and bloom
        // while writing luma into alpha.
        // Once both are registered, FrameSchedule inserts the Post pass automatically,
        // and FinalBlit degenerates into the FXAA present pass.
        // Under non-FXAA tiers both remain null, leaving no residual path,
        // mirroring WindowsApp.
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
        // finalize the AO tier during initialization, with mutually exclusive options.
        // If the capability is unavailable, fall back and log it.
        // AO depends on the HDR offscreen chain because it is multiplied in during composition,
        // and it is incompatible with MSAA because MSAA depth cannot be used directly as compute input.
        // Once finalized, create SceneDepth as a full-size depth-only target,
        // used explicitly as the Scene pass depth target and compute depth input, mirroring WindowsApp.
        if (RenderQuality.Current.AmbientOcclusion != Season.Rendering.AoMode.Off && !VkDevice.HdrSceneColor)
        {
            RenderQuality.Current.AmbientOcclusion = Season.Rendering.AoMode.Off;
            DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} [RenderQuality] AO depends on the HDR offscreen path, which is currently disabled, falling back to Off");
        }
        if (RenderQuality.Current.AmbientOcclusion != Season.Rendering.AoMode.Off
            && RenderQuality.Current.AntiAliasing == Season.Rendering.AaMode.Msaa4x)
        {
            RenderQuality.Current.AmbientOcclusion = Season.Rendering.AoMode.Off;
            DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} [RenderQuality] AO is incompatible with Msaa4x because MSAA depth cannot be used as compute input, falling back to Off");
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

        DeviceServices.BaseApp.Create();
    }

    static unsafe void RunLoop(nint window)
    {
        SDL.PumpEvents();

        var stopWatch = System.Diagnostics.Stopwatch.StartNew();

        double previousSeconds = 0;

        bool running = true;

        while (running)
        {
            while (SDL.PollEvent(out SDL_Event ev))
            {
                switch (ev.type)
                {
                    case SDL_EventType.SDL_EVENT_WINDOW_MOVED:
                        SaveWindowState(window, immediate: false);
                        break;

                    case SDL_EventType.SDL_EVENT_WINDOW_RESIZED:
                    case SDL_EventType.SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED:

                        // Always use pixel size as the source of truth.
                        // RESIZED carries window coordinates in data1 and data2, which are not pixel values on high-DPI setups,
                        // and mixing them with GetWindowSizeInPixels from startup would skew SwapChain sizing.
                        // Do not rebuild if size did not change:
                        // SyncWindow already stabilized the initial size, and the window manager may still send both events afterward.
                        SDL.GetWindowSizeInPixels(window, out int newW, out int newH);
                        if (newW > 0 && newH > 0
                            && ((int)DeviceServices.BaseApp.DeviceResolution.X != newW
                             || (int)DeviceServices.BaseApp.DeviceResolution.Y != newH))
                        {
                            DeviceServices.BaseApp.ApplyResolution(newW, newH, 1f, 1f);
                            _resized = true;
                        }
                        SaveWindowState(window, immediate: false);

                        break;

                    case SDL_EventType.SDL_EVENT_WINDOW_FOCUS_GAINED:

                        // DeviceServices.Media.Resume();
                        break;

                    case SDL_EventType.SDL_EVENT_WINDOW_FOCUS_LOST:

                        // DeviceServices.Media.Pause();
                        break;

                    case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN:

                        TouchService.isDown = true;

                        break;

                    case SDL_EventType.SDL_EVENT_FINGER_DOWN:

                        break;

                    case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP:

                        TouchService.isDown = false;

                        break;

                    case SDL_EventType.SDL_EVENT_FINGER_UP:

                        break;

                    case SDL_EventType.SDL_EVENT_MOUSE_MOTION:

                        TouchService.PoX = (int)((float)ev.motion.x / DeviceServices.BaseApp.Scale);
                        TouchService.PoY = (int)((float)ev.motion.y / DeviceServices.BaseApp.Scale);

                        break;

                    case SDL_EventType.SDL_EVENT_FINGER_MOTION:

                        break;

                    case SDL_EventType.SDL_EVENT_MOUSE_WHEEL:

                        var sDL_MouseWheelEvent = Unsafe.Read<SDL_MouseWheelEvent>(&ev);

                        if (TouchService.PoZ is null)
                        {
                            TouchService.PoZ = 0;
                        }

                        TouchService.PoZ -= (int)sDL_MouseWheelEvent.y * 50;

                        break;

                    case SDL_EventType.SDL_EVENT_QUIT:
                    case SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED:
                        SaveWindowState(window, immediate: true);
                        DeviceServices.BaseApp.DisposeSaveSettingsRequest();
                        running = false;
                        break;
                }
            }

            if (!running) break;

            double newSeconds = stopWatch.Elapsed.TotalSeconds;

            double deltaSeconds = newSeconds - previousSeconds;

            previousSeconds = newSeconds;

            float elapsed = (float)deltaSeconds;

            // Rebuild the SwapChain, equivalent to DX HandleResize.
            if (_resized)
            {
                _resized = false;
                int w = (int)DeviceServices.BaseApp.DeviceResolution.X;
                int h = (int)DeviceServices.BaseApp.DeviceResolution.Y;
                if (w > 0 && h > 0)
                {
                    // HandleResize returning false means ResizeSemaphore timed out because background Load still holds the lock,
                    // or an exception occurred.
                    // In that case DeviceWaitIdle did not run, so Resize must not be called yet.
                    // ResizeCompute rebuilds storage textures for TAA, Bloom, and GTAO,
                    // and that must happen only after the GPU is idle.
                    // Preserve _resized and retry on the next frame after background Load finishes.
                    if (VkDevice.HandleResize(w, h))
                        DeviceServices.BaseApp?.Resize();
                    else
                        _resized = true;
                }
            }

            // Camera and lighting UBOs, written before each frame.
            VKPrimitiveGroup.Update(
                elapsed,
                DeviceServices.BaseApp.CameraPos,
                DeviceServices.BaseApp.CameraTarget,
                DeviceServices.BaseApp.EffectiveSceneLights);

            DeviceServices.BaseApp.Update(elapsed);

            var backgroundColor = DeviceServices.BaseApp.BackgroundColor;
            VkDevice.BackgroundColor = backgroundColor;
            VkDevice.Display?.SetClearColor(backgroundColor);

            // Frame recording sequence:
            // Acquire -> FrameSchedule, meaning BeginPass, draw, EndPass -> Submit -> Present.
            if (!VkDevice.BeforeRender())
            {
                // Consecutive OutOfDate states, such as during dragging or minimization:
                // mark resize and let the next frame rebuild through the main-loop HandleResize path
                // using the latest DeviceResolution size.
                _resized = true;
                continue;
            }

            Season.Basic.Graphics.Instance.FlushTextAtlas();

            // Pass scheduling for step 1: Begin and End of the Scene pass are driven by FrameSchedule.
            Season.Rendering.FrameSchedule.Execute(Season.Basic.Graphics.Instance, DeviceServices.BaseApp, backgroundColor);

            VkDevice.AfterRender();
        }

        // Wait for the GPU to drain before cleanup.
        Silk.NET.Vulkan.Vk.GetApi().DeviceWaitIdle(VkDevice.LogicalDevice);

        SDL.Quit();
    }

    static Rect GetInitialWindowRect(BaseApp app, int displayWidth, int displayHeight, ref WindowFlags flags)
    {
        var windowState = app.Settings.WindowState;

        if (windowState.FullScreen)
        {
            flags |= WindowFlags.Fullscreen;
            return new Rect(0, 0, displayWidth, displayHeight);
        }

        if (windowState.Maximized)
        {
            flags |= WindowFlags.Maximized;
        }

        int width = windowState.Width > 0 ? windowState.Width : displayWidth / 2;
        int height = windowState.Height > 0 ? windowState.Height : displayHeight / 2;

        width = ClampDimension(width, displayWidth, 320);
        height = ClampDimension(height, displayHeight, 240);

        bool hasSavedBounds = windowState.Width > 0 && windowState.Height > 0;

        if (!hasSavedBounds)
        {
            return CenterRect(width, height, displayWidth, displayHeight);
        }

        var rect = new Rect(windowState.X, windowState.Y, width, height);

        if (IsRectVisibleOnPrimaryDisplay(rect, displayWidth, displayHeight))
        {
            return ClampRectToPrimaryDisplay(rect, displayWidth, displayHeight);
        }

        return CenterRect(width, height, displayWidth, displayHeight);
    }

    static Rect CenterRect(int width, int height, int displayWidth, int displayHeight)
        => new Rect((displayWidth - width) / 2, (displayHeight - height) / 2, width, height);

    static int ClampDimension(int value, int max, int preferredMin)
    {
        if (max <= 0)
        {
            return value;
        }

        return Math.Min(Math.Max(value, Math.Min(preferredMin, max)), max);
    }

    static Rect ClampRectToPrimaryDisplay(Rect rect, int displayWidth, int displayHeight)
    {
        int x = Math.Max(0, Math.Min(rect.X, displayWidth - rect.Width));
        int y = Math.Max(0, Math.Min(rect.Y, displayHeight - rect.Height));

        return new Rect(x, y, rect.Width, rect.Height);
    }

    static bool IsRectVisibleOnPrimaryDisplay(Rect rect, int displayWidth, int displayHeight)
    {
        int visibleWidth = Math.Max(0, Math.Min(rect.X + rect.Width, displayWidth) - Math.Max(rect.X, 0));
        int visibleHeight = Math.Max(0, Math.Min(rect.Y + rect.Height, displayHeight) - Math.Max(rect.Y, 0));
        int minVisibleWidth = Math.Min(rect.Width, 100);
        int minVisibleHeight = Math.Min(rect.Height, 100);

        return visibleWidth >= minVisibleWidth && visibleHeight >= minVisibleHeight;
    }

    static void SaveWindowState(IntPtr window, bool immediate)
    {
        var app = DeviceServices.BaseApp;
        var windowState = app.Settings.WindowState;
        var flags = SDL.GetWindowFlags(window);

        windowState.FullScreen = (flags & WindowFlags.Fullscreen) != 0;
        windowState.Maximized = !windowState.FullScreen && (flags & WindowFlags.Maximized) != 0;

        if (!windowState.FullScreen && !windowState.Maximized && SDL.GetWindowSizeInPixels(window, out int pixelW, out int pixelH))
        {
            if (SDL.GetWindowPosition(window, out int x, out int y))
            {
                windowState.X = x;
                windowState.Y = y;
            }

            windowState.Width = pixelW;
            windowState.Height = pixelH;
        }

        if (immediate)
        {
            app.SaveSettings();
        }
        else
        {
            app.RequestSaveSettings();
        }
    }

}
