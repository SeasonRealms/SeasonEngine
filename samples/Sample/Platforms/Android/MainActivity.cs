
using Android.App;
using Android.OS;

using Season.Platforms.Android;

namespace Sample;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true)]
public class MainActivity : BaseActivity
{
    protected override void OnCreate(Bundle savedInstanceState)
    {
        var app = new App();

        AndroidApp.Run(app);

        base.OnCreate(savedInstanceState);
    }

}
