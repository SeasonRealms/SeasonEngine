// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using UIKit;

using Season.Platforms.Shared.Apple;
using Season.Platforms.MacCatalyst;

namespace Season.Platforms.MacCatalyst;

public static class MacCatalystApp
{
    public static void Run(BaseApp app)
    {
        // Global exception capture as the last line of defense for async void callbacks,
        // thread-pool work, and managed exceptions escaping across the native boundary.
        // Mirrors LinuxApp.Run. Without these handlers such failures abort the process
        // with only a native runloop stack, and the real managed throw site is lost
        // (reported as bare xamarin_UIApplicationMain / UIApplication.Main frames).
        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            var log = $"{DateTime.UtcNow} [FATAL] UnhandledException: {ex?.GetType().Name}: {ex?.Message}\n{ex?.StackTrace}";
            Debug.WriteLine(log);
            app.AddLog(LogType.Error, log);
        };
        TaskScheduler.UnobservedTaskException += (sender, e) =>
        {
            var ex = e.Exception;
            var log = $"{DateTime.UtcNow} [FATAL] UnobservedTaskException: {ex?.GetType().Name}: {ex?.Message}\n{ex?.StackTrace}";
            Debug.WriteLine(log);
            app.AddLog(LogType.Error, log);
        };

        var keyboard = new AppleKeyboardService();
        AppDelegate.Keyboard = keyboard;

        DeviceServices.Initialize(
            baseApp: app,
            core: new MacCatalystDeviceCore(),
            media: new AppleMediaPlayer(),
            dialog: new AppleDialogService(),
            file: new AppleFileService(),
            image: new AppleImageService(),
            video: new AppleVideoPlayerService(),
            gallery: new AppleGalleryService(),
            record: new AppleRecordService(),
            download: new AppleDownloadService(),
            store: new AppleStoreService(),
            ads: null,
            windowsFeatures: null,
            keyboard: keyboard
        );

        UIApplication.Main(null, null, typeof(AppDelegate));
    }
}
