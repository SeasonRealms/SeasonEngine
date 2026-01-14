// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.

using CoreAnimation;
using CoreGraphics;
using Foundation;
using Metal;
using MetalKit;
using ObjCRuntime;
using System.Diagnostics;
using System.Numerics;
using UIKit;

namespace Season.Platforms.Shared.Apple;

[Foundation.Register("AppDelegate")]
public class AppDelegate : UIKit.UIApplicationDelegate
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
