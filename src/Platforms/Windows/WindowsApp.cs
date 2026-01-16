// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Microsoft.UI.Xaml;
using Windows.Graphics;
using Windows.Services.Store;

namespace Season.Platforms.Windows;

public static class WindowsApp
{
    internal static BaseApp App;

    internal static Microsoft.UI.Xaml.Window Window = null;

    internal static Microsoft.UI.Windowing.AppWindow AppWindow;

    internal static StoreContext StoreContext;

    public static void Run(BaseApp app)
    {
        App = app;

        DeviceServices.Initialize(
            core: new WindowsDeviceCore(), 
            media: new WindowsMediaPlayer(), 
            dialog: new WindowsDialogService(), 
            file: new WindowsFileService(), 
            gallery: new WindowsGalleryService(), 
            record: new WindowsRecordService(), 
            download: new WindowsDownloadService(),
            store: new WindowsStoreService(),
            windowsFeatures: new WindowsFeatures());

        Window = new Microsoft.UI.Xaml.Window();

        Window.Title = "Sample";

        Window.Activated += (sender, e) =>
        {
            var now = e.WindowActivationState != WindowActivationState.Deactivated;

        };

        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(Window);

        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(windowHandle);

        AppWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

        //var scalingFactor = DeviceWindows.Window.GetDisplayDensity();

        var displayArea = Microsoft.UI.Windowing.DisplayArea.Primary;

        AppWindow.SetIcon(@"favicon.ico");
        AppWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.Default);

        AppWindow.MoveAndResize(new RectInt32((displayArea.WorkArea.Width - displayArea.WorkArea.Width / 2) / 2, (displayArea.WorkArea.Height - displayArea.WorkArea.Height / 2) / 2, displayArea.WorkArea.Width / 2, displayArea.WorkArea.Height / 2));

        var swapChainPanel = new Microsoft.UI.Xaml.Controls.SwapChainPanel();

        Window.Content = swapChainPanel;

        Window.Activate();

        StoreContext = StoreContext.GetDefault();

        WinRT.Interop.InitializeWithWindow.Initialize(StoreContext, windowHandle);

        app.Create();
    }
}
