
using Season.Platforms.Windows;

namespace Sample.WinUI;

public partial class App : Microsoft.UI.Xaml.Application
{
    public App()
    {
        this.InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {

        PlatformApp.Run();
    }
}
