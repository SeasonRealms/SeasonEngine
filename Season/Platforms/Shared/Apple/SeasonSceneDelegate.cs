// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using CoreGraphics;
using Foundation;
using ObjCRuntime;
using UIKit;

namespace Season.Platforms.Shared.Apple;

/// <summary>
/// UIScene entry point for apps that declare UIApplicationSceneManifest. Xcode 27
/// and later require the UIScene lifecycle for iOS and Mac Catalyst alike; the
/// AppDelegate defers window creation to this delegate when that key is present,
/// so both platforms run the same Metal host through the scene lifecycle.
/// </summary>
[Register("SeasonSceneDelegate")]
[Preserve(AllMembers = true)]
public class SeasonSceneDelegate : UIWindowSceneDelegate
{
    // UIWindowSceneDelegate's model implementation of Window throws
    // ModelNotImplementedException; the scene delegate must supply the storage.
    public override UIWindow? Window { get; set; }

    public override void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
    {
        if (scene is not UIWindowScene windowScene)
        {
            return;
        }

#if IOS
        ConfigureMinimumSize(windowScene);
#endif

        Window = AppDelegate.CreateSceneWindow(windowScene);
    }

#if IOS
    /// <summary>
    /// Keeps the window above the size where the fixed layout offsets stop fitting.
    /// UIWindowScene.SizeRestrictions exists from iPadOS 26, so the call is version
    /// gated; iPhone scenes and older systems keep the system default. Mac Catalyst
    /// windows are intentionally left resizable down to the system minimum.
    /// </summary>
    static void ConfigureMinimumSize(UIWindowScene scene)
    {
        if (!OperatingSystem.IsIOSVersionAtLeast(26))
        {
            return;
        }

        if (UIDevice.CurrentDevice.UserInterfaceIdiom != UIUserInterfaceIdiom.Pad)
        {
            return;
        }

        if (scene.SizeRestrictions is not { } restrictions)
        {
            return;
        }

        restrictions.MinimumSize = new CGSize(640, 640);
    }
#endif
}
