// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Runtime.InteropServices;

using Season.Platforms.Shared.LinuxAndroid;

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

        int width = 1280;
        int height = 720;

        SDL.CreateWindow(app.Title, width, height, WindowFlags.Vulkan);

        while (true)
        {

        }
    }
}
