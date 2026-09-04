
using Microsoft.UI.Xaml;
using Season.Platforms.Windows;

namespace Creator.WinUI;

public partial class App : Microsoft.UI.Xaml.Application
{
    public App()
    {
        this.InitializeComponent();
    }

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        var app = new Creator.App();
        
        WindowsApp.Run(app);
    }
}
