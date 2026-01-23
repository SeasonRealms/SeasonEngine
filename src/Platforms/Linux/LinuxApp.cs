// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Microsoft.Maui.Controls.Shapes;

namespace Season.Platforms.Linux;

public static class LinuxApp
{

    public static unsafe void Run(BaseApp app)
    {
        DeviceServices.Initialize(
            baseApp: app,
            core: new LinuxDeviceCore(),
            media: new LinuxMediaPlayer(),
            dialog: new LinuxDialogService(),
            file: new LinuxFileService(),
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

        Rect rect;

        var flags = WindowFlags.None; //.Vulkan;

        if (app.FullScreen)
        {
            flags |= WindowFlags.Fullscreen;

            rect = new Rect(0, 0, width, height);
        }
        else
        {
            rect = new Rect((width - width / 2) / 2, (height - height / 2) / 2, width / 2, height / 2);
        }

        var window = SDL.CreateWindow(app.Title, (int)rect.Width, (int)rect.Height, flags);

        SDL.ShowWindow(window);

        CreateInstance(window);
    }

    static unsafe void CreateInstance(nint window)
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
                    case SDL_EventType.SDL_EVENT_WINDOW_RESIZED:

                        DeviceServices.BaseApp.ApplyResolution(ev.window.data1, ev.window.data2);

                        // DeviceServices.Core.OnSizeChanged(newWidth, newHeight);
                        break;

                    case SDL_EventType.SDL_EVENT_WINDOW_FOCUS_GAINED:

                        // DeviceServices.Media.Resume();
                        break;

                    case SDL_EventType.SDL_EVENT_WINDOW_FOCUS_LOST:

                        // DeviceServices.Media.Pause();
                        break;

                    case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN:

                        // ev.button.x, ev.button.y, ev.button.button
                        TouchService.isDown = true;

                        break;

                    case SDL_EventType.SDL_EVENT_FINGER_DOWN:

                        // ev.tfinger.x, ev.tfinger.y, ev.tfinger.fingerID
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

                        var wheelX = ev.wheel.x;
                        var wheelY = ev.wheel.y;
                        var mouseX = ev.wheel.mouse_x;

                        if (TouchService.PoZ is null)
                        {
                            TouchService.PoZ = 0;
                        }

                        TouchService.PoZ -= (int)(ev.wheel.mouse_y * 50);

                        break;

                    case SDL_EventType.SDL_EVENT_QUIT:
                    case SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED:
                        running = false;

                        var text = Marshal.PtrToStringAnsi((IntPtr)SDL.GetError());

                        break;
                }
            }

            double newSeconds = stopWatch.Elapsed.TotalSeconds;

            double deltaSeconds = newSeconds - previousSeconds;

            previousSeconds = newSeconds;

            // Render(deltaSeconds);
        }

        SDL.Quit();
    }
}
