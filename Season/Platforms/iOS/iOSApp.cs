// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using UIKit;

using Season.Platforms.Shared.Apple;
using Season.Platforms.iOS;

namespace Season.Platforms.iOS;

public static class iOSApp
{
    public static void Run(BaseApp app)
    {
        var keyboard = new AppleKeyboardService();
        AppDelegate.Keyboard = keyboard;

        DeviceServices.Initialize(
            baseApp: app,
            core: new iOSDeviceCore(),
            media: new AppleMediaPlayer(),
            dialog: new AppleDialogService(),
            file: new AppleFileService(),
            image: new AppleImageService(),
            video: new AppleVideoPlayerService(),
            gallery: new AppleGalleryService(),
            record: new AppleRecordService(),
            download: new AppleDownloadService(),
            store: new AppleStoreService(),
            ads: null, //new iOSAds(),
            windowsFeatures: null,
            keyboard: keyboard
        );

        UIApplication.Main(null, null, typeof(AppDelegate));
    }
}
