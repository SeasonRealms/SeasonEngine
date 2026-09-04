
using Android.App;
using Android.Content.PM;
using Android.OS;

using Season.Platforms.Android;

namespace Creator;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true,
    ScreenOrientation = ScreenOrientation.FullSensor)]
internal class MainActivity : BaseActivity
{
    protected override void OnCreate(Bundle savedInstanceState)
    {
        if (!AndroidApp.IsInitialized)
        {
            var app = new App();
            AndroidApp.Run(app);
        }

        base.OnCreate(savedInstanceState);
    }

}
