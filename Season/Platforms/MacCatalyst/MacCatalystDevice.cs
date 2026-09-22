// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Season.Platforms.Shared.Apple;

namespace Season.Platforms.MacCatalyst;

internal class MacCatalystDeviceCore : AppleDeviceCore
{
    public override Basic.Platform Platform => Basic.Platform.MacCatalyst;

    public override string LoadFilePath(string res)
    {
        var mono = AppContext.BaseDirectory;
        var location = mono.Replace("MonoBundle", "Resources");

        // Callers may assemble absolute paths from AppContext.BaseDirectory (the MonoBundle
        // directory holding managed assemblies), while content lives in the sibling Resources
        // directory. Path.Combine returns rooted paths unchanged, which would bypass the
        // reroot, so translate MonoBundle-rooted paths explicitly and pass others through.
        string result;
        if (Path.IsPathRooted(res))
        {
            if (res.StartsWith(mono, StringComparison.Ordinal))
            {
                // Keep the rerooted path relative: a leading separator would make
                // Path.Combine treat it as rooted and discard the Resources location.
                var relative = res[mono.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                result = Path.Combine(location, relative);
            }
            else
                result = res;
        }
        else
        {
            result = Path.Combine(location, res);
        }

        return result;
    }
}
