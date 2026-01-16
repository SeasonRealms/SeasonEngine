// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Android.Views;

namespace Season.Platforms.Android;

public static class AndroidApp
{
    internal static BaseApp App;

    public static void Run(BaseApp app)
    {
        App = app;
    }
}

[Activity(LaunchMode = LaunchMode.SingleTop, AlwaysRetainTaskState = true,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class BaseActivity : Activity
{
    protected override void OnCreate(Bundle savedInstanceState)
    {
        RequestWindowFeature(WindowFeatures.NoTitle);

        base.OnCreate(savedInstanceState);

        base.Window.AddFlags(WindowManagerFlags.Fullscreen);
        base.Window.AddFlags(WindowManagerFlags.KeepScreenOn);
        base.Window.AddFlags(WindowManagerFlags.TranslucentStatus);

        base.Window.Attributes.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;

        try
        {
            var insetsController = base.Window.InsetsController;
            if (insetsController != null)
            {
                insetsController.Hide(WindowInsets.Type.StatusBars() | WindowInsets.Type.NavigationBars());
                insetsController.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
            }

            base.Window.SetDecorFitsSystemWindows(false);

            base.Window.DecorView.WindowInsetsController?.Hide(WindowInsets.Type.NavigationBars());

            base.Window.InsetsController?.Hide(WindowInsets.Type.StatusBars());
        }
        catch (System.Exception ex)
        {

        }
    }

    protected override void OnPause()
    {
        base.OnPause();

    }

    protected override void OnResume()
    {
        base.OnResume();

    }

    protected override void OnActivityResult(int requestCode, [GeneratedEnum] Result resultCode, Intent data)
    {

        base.OnActivityResult(requestCode, resultCode, data);
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, [GeneratedEnum] Permission[] grantResults)
    {

        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
    }
}
