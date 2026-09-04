// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Season.Platforms.MacCatalyst;

namespace Engine;

public class Program
{
    static void Main(string[] args)
    {
        var app = new App();

        MacCatalystApp.Run(app);
    }
}
