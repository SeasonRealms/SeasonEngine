// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Season.Platforms.Linux;

internal static partial class SDL
{
    const string Library = "SDL3";

    static IntPtr IntPtr;

    [ModuleInitializer]
    public static void Init()
    {
        NativeLibrary.SetDllImportResolver(typeof(SDL).Assembly, ResolveSDL3);
    }

    private static IntPtr ResolveSDL3(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (IntPtr != IntPtr.Zero)
        {
            return IntPtr;
        }

        var libExtension = ".so";

        var libBaseName = "SDL3";

        var os = "linux";

        var arch = RuntimeInformation.ProcessArchitecture.ToString().ToLower();

        var runtimeLibPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "runtimes",
            $"{os}-{arch}",
            "native",
            $"lib{libBaseName}{libExtension}");

        if (File.Exists(runtimeLibPath))
        {
            if (NativeLibrary.TryLoad(runtimeLibPath, assembly, searchPath, out IntPtr))
            {

            }
        }

        if (IntPtr == IntPtr.Zero)
        {
            if (NativeLibrary.TryLoad(libBaseName, assembly, searchPath, out IntPtr))
            {

            }
        }

        return IntPtr;
    }

    [LibraryImport(Library, EntryPoint = "SDL_Init")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool Init(uint flags);

    [LibraryImport(Library, EntryPoint = "SDL_GetError"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial IntPtr GetError();

    [LibraryImport(Library, EntryPoint = "SDL_CreateWindow"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial IntPtr CreateWindow([MarshalAs(UnmanagedType.LPUTF8Str)] string title, int w, int h, WindowFlags flags);


}

[Flags]
public enum WindowFlags : ulong
{
    Vulkan = 0x0000000010000000
}
