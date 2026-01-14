// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Season.Platforms.Windows;

public static class PlatformApp
{
    internal static Microsoft.UI.Xaml.Window Window = null;

    internal static Microsoft.UI.Windowing.AppWindow AppWindow;

    public static void Run()
    {
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
    }
}
