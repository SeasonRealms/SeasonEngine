// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using CoreGraphics;
using Foundation;
using ObjCRuntime;
using UIKit;

namespace Season.Platforms.Shared.Apple;

public static class AppleApp
{
    internal static BaseApp App;

    public static void Run(BaseApp app)
    {
        App = app;

        UIApplication.Main(null, null, typeof(AppDelegate));
    }
}

[Foundation.Register("AppDelegate")]
public class AppDelegate : UIApplicationDelegate
{
    public override bool FinishedLaunching(UIKit.UIApplication application, Foundation.NSDictionary launchOptions)
    {
        Runtime.MarshalManagedException += (_, e) => e.ExceptionMode = MarshalManagedExceptionMode.UnwindNativeCode;

        Runtime.MarshalObjectiveCException += (_, e) => e.ExceptionMode = MarshalObjectiveCExceptionMode.UnwindManagedCode;

        var scene = UIKit.UIApplication.SharedApplication.ConnectedScenes.ToArray().FirstOrDefault(cs => cs is UIWindowScene) as UIWindowScene;

        var bounds = scene.Screen.NativeBounds;

#if MACCATALYST

        scene.Titlebar.TitleVisibility = UITitlebarTitleVisibility.Hidden;

        scene.Titlebar.Toolbar = null;

#endif

        var uiWindow = new MetalUIWindow(bounds);

        var uiViewController = new MetalViewController(uiWindow);

        uiViewController.PrefersStatusBarHidden();

        uiWindow.Add(uiViewController.View);

        uiWindow.MakeKeyAndVisible();

        uiWindow.RootViewController = uiViewController;

        return true;
    }
}

public class MetalUIWindow : UIWindow
{

    public MetalUIWindow(CGRect frame) : base(frame)
    {

    }

    public override void TouchesBegan(NSSet nSSet, UIEvent uIEvent)
    {
        base.TouchesBegan(nSSet, uIEvent);

    }

    public override void TouchesEnded(NSSet nSSet, UIEvent uIEvent)
    {
        base.TouchesBegan(nSSet, uIEvent);

    }

    public override void TouchesMoved(NSSet nSSet, UIEvent uIEvent)
    {
        base.TouchesBegan(nSSet, uIEvent);

    }

    public override void TouchesCancelled(NSSet nSSet, UIEvent uIEvent)
    {
        base.TouchesBegan(nSSet, uIEvent);

    }

}

public class MetalViewController : UIViewController
{
    UIWindow uiWindow;

    public MetalViewController(UIWindow window)
    {
        uiWindow = window;
    }

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();

    }

    public override void ViewWillTransitionToSize(CGSize toSize, IUIViewControllerTransitionCoordinator coordinator)
    {
        base.ViewWillTransitionToSize(toSize, coordinator);
    }
}
