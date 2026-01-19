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
        DeviceServices.Initialize(
            baseApp: app,
            core: new MacCatalystDeviceCore(),
            media: new AppleMediaPlayer(),
            dialog: new AppleDialogService(),
            file: new AppleFileService(),
            gallery: new AppleGalleryService(),
            record: new AppleRecordService(),
            download: new AppleDownloadService(),
            store: new AppleStoreService(),
            ads: null,
            windowsFeatures: null
        );

        UIApplication.Main(null, null, typeof(AppDelegate));
    }
}
