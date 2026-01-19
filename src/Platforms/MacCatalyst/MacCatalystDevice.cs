// Copyright // Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Season.Platforms.Shared.Apple;

namespace Season.Platforms.MacCatalyst;

internal class MacCatalystDeviceCore : AppleDeviceCore
{
    public new Basic.Platform Platform => Basic.Platform.MacCatalyst;

    public override string LoadFilePath(string res)
    {
        var location = AppContext.BaseDirectory.Replace("MonoBundle", "Resources");

        var file = Path.Combine(location, res);

        return file;
    }
}
