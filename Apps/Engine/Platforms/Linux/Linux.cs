// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Season.Platforms.Linux;

namespace Engine;

internal class Program
{
    static void Main(string[] args)
    {
        var app = new App();

        LinuxApp.Run(app);
    }
}
