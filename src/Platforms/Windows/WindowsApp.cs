// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Windows.Services.Store;

namespace Season.Platforms.Windows;

public static class WindowsApp
{

    internal static Microsoft.UI.Xaml.Window Window = null;

    internal static Microsoft.UI.Windowing.AppWindow AppWindow;

    internal static StoreContext StoreContext;

    static bool resized;

    static bool firstTime = true;

    public static void Run(BaseApp app)
    {
        DeviceServices.Initialize(
            baseApp: app,
            core: new WindowsDeviceCore(), 
            media: new WindowsMediaPlayer(), 
            dialog: new WindowsDialogService(), 
            file: new WindowsFileService(), 
            gallery: new WindowsGalleryService(), 
            record: new WindowsRecordService(), 
            download: new WindowsDownloadService(),
            store: new WindowsStoreService(),
            null,
            windowsFeatures: new WindowsFeatures());

        Window = new Microsoft.UI.Xaml.Window();

        Window.Title = app.Title;

        Window.Activated += (sender, e) =>
        {
            var isActive = e.WindowActivationState != WindowActivationState.Deactivated;

            if (DeviceServices.BaseApp.IsActive == isActive)
            {

            }
            else
            {
                if (DeviceServices.BaseApp.IsActive)
                {
                    DeviceServices.BaseApp.LastInActiveTime = DateTime.Now;
                }
                else
                {
                    DeviceServices.BaseApp.LastActiveTime = DateTime.Now;
                }

                DeviceServices.BaseApp.IsActive = isActive;
            }
        };

        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(Window);

        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(windowHandle);

        AppWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

        //var scalingFactor = DeviceWindows.Window.GetDisplayDensity();

        var displayArea = Microsoft.UI.Windowing.DisplayArea.Primary;

        AppWindow.SetIcon(@"favicon.ico");

        if (app.FullScreen)
        {
            AppWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen);
        }
        else
        {
            AppWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.Default);

            AppWindow.MoveAndResize(new RectInt32((displayArea.WorkArea.Width - displayArea.WorkArea.Width / 2) / 2, (displayArea.WorkArea.Height - displayArea.WorkArea.Height / 2) / 2, displayArea.WorkArea.Width / 2, displayArea.WorkArea.Height / 2));
        }

        var swapChainPanel = new Microsoft.UI.Xaml.Controls.SwapChainPanel();

        Window.Content = swapChainPanel;

        Window.Activate();

        swapChainPanel.PointerPressed += (s, e) =>
        {
            TouchService.isDown = true;
        };

        swapChainPanel.PointerReleased += (s, e) =>
        {
            TouchService.isDown = false;
        };

        swapChainPanel.PointerMoved += (s, e) =>
        {
            var currentPoint = e.GetCurrentPoint(s as UIElement);

            var pos = new Point((int)currentPoint.Position.X, (int)currentPoint.Position.Y);

            TouchService.PoX = (int)((float)pos.X / DeviceServices.BaseApp.Scale);

            TouchService.PoY = (int)((float)pos.Y / DeviceServices.BaseApp.Scale);
        };

        swapChainPanel.PointerWheelChanged += (s, e) =>
        {
            var currentPoint = e.GetCurrentPoint(s as UIElement);

            if (currentPoint.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse)
            {
                //var direction = ((currentPoint.Properties.MouseWheelDelta <= 0) ? MouseScrollDirections.Down : MouseScrollDirections.Up);

                if (TouchService.PoZ is null)
                {
                    TouchService.PoZ = 0;
                }

                TouchService.PoZ -= currentPoint.Properties.MouseWheelDelta;
            }
        };

        swapChainPanel.SizeChanged += (s, e) =>
        {
            DeviceServices.BaseApp.ApplyResolution((int)e.NewSize.Width, (int)e.NewSize.Height);

            if (firstTime)
            {
                firstTime = false;

                CreateInstance(swapChainPanel);
            }
            else
            {
                resized = true;
            }
        };

        StoreContext = StoreContext.GetDefault();

        WinRT.Interop.InitializeWithWindow.Initialize(StoreContext, windowHandle);

        app.Create();
    }

    static async void CreateInstance(SwapChainPanel swapChainPanel)
    {


        //CreateInstance(swapChainPanel);
    }
}
