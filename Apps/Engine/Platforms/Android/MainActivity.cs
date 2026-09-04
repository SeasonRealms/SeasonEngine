// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Android.App;
using Android.Content.PM;
using Android.OS;
using Season.Platforms.Android;

namespace Engine;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, ScreenOrientation = ScreenOrientation.FullSensor)]
public class MainActivity : BaseActivity
{
    protected override void OnCreate(Bundle savedInstanceState)
    {
        // Initialize App and DeviceServices only on the first run (Activity will be recreated when the screen orientation changes,
        // However, static Vulkan states and user resources need to be preserved—soft reboot paths reuse the Instance/ Device / Pipeline / texture).
        if (!AndroidApp.IsInitialized)
        {
            var app = new App();
            AndroidApp.Run(app);
        }

        base.OnCreate(savedInstanceState);
    }
}
