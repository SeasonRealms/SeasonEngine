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
        // Contract 3-2: the earliest engine-owned instruction in the process. Every startup number is relative to the
        // moment this type was first touched, and this call is what touches it.
        StartupProfiler.Mark("app-launch");

        var app = new Engine.App();

        WindowsApp.Run(app);
    }
}
