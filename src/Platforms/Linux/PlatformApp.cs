// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.

using Season.Platforms.Shared.Unix;
using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Season.Platforms.Linux;

public static class PlatformApp
{
    public static unsafe void Run()
    {
        Gtk.Application.Init();

        var des = RuntimeInformation.OSDescription;

        var art = RuntimeInformation.ProcessArchitecture;

        var basedi = AppContext.BaseDirectory;

        var video = 0x00000020u;
        if (!SDL.Init(video))
        {
            var text = Marshal.PtrToStringAnsi((IntPtr)SDL.GetError());

            throw new Exception("SDL initialization error " + text);
        }

        int width = 1280;
        int height = 720;

        SDL.CreateWindow("Sample", width, height, WindowFlags.Vulkan);

        while (true)
        {

        }
    }
}

