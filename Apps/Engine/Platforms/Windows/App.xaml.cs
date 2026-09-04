// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Season.Platforms.Windows;

namespace Engine.WinUI;

public partial class App : Microsoft.UI.Xaml.Application
{
    public App()
    {
        this.InitializeComponent();
    }

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        var app = new Engine.App();

        WindowsApp.Run(app);
    }
}
