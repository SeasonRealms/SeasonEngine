// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using CoreGraphics;
using Foundation;
using ObjCRuntime;
using UIKit;

namespace Season.Platforms.Shared.Apple;

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
#if MACCATALYST

        var panGesture = new UIPanGestureRecognizer(HandlePanGesture);

        panGesture.AllowedScrollTypesMask = UIScrollTypeMask.All;

        panGesture.MinimumNumberOfTouches = 0;

        panGesture.MaximumNumberOfTouches = 0;

        AddGestureRecognizer(panGesture);

#endif
    }

    public override void TouchesBegan(NSSet nsset, UIEvent uIEvent)
    {
        base.TouchesBegan(nsset, uIEvent);

        ProcessTouches(nsset);
    }

    public override void TouchesEnded(NSSet nsset, UIEvent uIEvent)
    {
        base.TouchesBegan(nsset, uIEvent);

        ProcessTouches(nsset);
    }

    public override void TouchesMoved(NSSet nsset, UIEvent uIEvent)
    {
        base.TouchesBegan(nsset, uIEvent);

        ProcessTouches(nsset);
    }

    public override void TouchesCancelled(NSSet nsset, UIEvent uIEvent)
    {
        base.TouchesBegan(nsset, uIEvent);

        ProcessTouches(nsset);
    }

    void ProcessTouches(NSSet nsset)
    {
        if ((long)nsset.Count == 0)
            return;

        var touchesArray = nsset.ToArray<UITouch>();

        for (int i = 0; i < touchesArray.Length; ++i)
        {
            var touch = touchesArray[i];

            var location = touch.LocationInView(touch.View);

            var pos = new Vector2((float)location.X, (float)location.Y);

            var scale = (float)Layer.ContentsScale;

            var nativeScale = (float)UIScreen.MainScreen.NativeScale;

            //pos = pos / TouchScale; // * scale; * (float)UIScreen.MainScreen.NativeScale

            TouchService.PoX = (int)(pos.X * nativeScale / DeviceServices.BaseApp.Scale);
            TouchService.PoY = (int)(pos.Y * nativeScale / DeviceServices.BaseApp.Scale);

            //var id = (int)(long)(IntPtr)touch.Handle;

            switch (touch.Phase)
            {
                case UITouchPhase.Moved:
                    TouchService.IsMoved = true;
                    break;
                case UITouchPhase.Began:
                    TouchService.isDown = true;
                    break;
                case UITouchPhase.Ended:
                    TouchService.isDown = false;
                    break;
                case UITouchPhase.Cancelled:
                    TouchService.isDown = false;
                    break;
                default:
                    break;
            }
        }
    }

#if MACCATALYST
    private void HandlePanGesture(UIPanGestureRecognizer gesture)
    {
        var translation = gesture.TranslationInView(this);

        if (gesture.State == UIGestureRecognizerState.Changed)
        {
            var deltaX = (float)translation.X;
            var deltaY = (float)translation.Y;

            // TouchService.OnMouseWheel(deltaX, deltaY);

            if (TouchService.PoZ is null)
            {
                TouchService.PoZ = 0;
            }

            TouchService.PoZ -= (int)(deltaY * 50);

            // Reset move
            gesture.SetTranslation(CGPoint.Empty, this);
        }
    }
#endif
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
