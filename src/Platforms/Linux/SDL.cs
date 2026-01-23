// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Runtime.CompilerServices;

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

    [LibraryImport(Library, EntryPoint = "SDL_GetPrimaryDisplay"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint GetPrimaryDisplay();

    [LibraryImport(Library, EntryPoint = "SDL_GetDesktopDisplayMode"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial IntPtr GetDesktopDisplayMode(uint displayID);

    [LibraryImport(Library, EntryPoint = "SDL_CreateWindow"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial IntPtr CreateWindow([MarshalAs(UnmanagedType.LPUTF8Str)] string title, int w, int h, WindowFlags flags);

    [LibraryImport(Library, EntryPoint = "SDL_ShowWindow"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool ShowWindow(IntPtr window);

    [LibraryImport(Library, EntryPoint = "SDL_HideWindow"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool HideWindow(IntPtr window);

    [LibraryImport(Library, EntryPoint = "SDL_GetWindowSurface"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial IntPtr GetWindowSurface(IntPtr window);

    [LibraryImport(Library, EntryPoint = "SDL_PumpEvents"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void PumpEvents();

    [LibraryImport(Library, EntryPoint = "SDL_PollEvent"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool PollEvent(out SDL_Event e);

    [LibraryImport(Library, EntryPoint = "SDL_Quit"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void Quit();
}

[Flags]
public enum WindowFlags : ulong
{
    None = 0x0000000000000000,
    Fullscreen = 0x0000000000000001,
    Borderless = 0x0000000000000010,
    Resizable = 0x0000000000000020,
    Maximized = 0x0000000000000080,
    Vulkan = 0x0000000010000000
}

[StructLayout(LayoutKind.Sequential)]
public struct SDL_DisplayMode
{
    public uint displayID;     
    public uint format;        
    public int w;              
    public int h;              
    public float pixel_density;
    public float refresh_rate; 
    public IntPtr internal_;   
}

public enum SDL_EventType : uint
{
    SDL_EVENT_WINDOW_RESIZED = 0x206,
    SDL_EVENT_WINDOW_FOCUS_GAINED = 0x20D,
    SDL_EVENT_WINDOW_FOCUS_LOST = 0x20E,
    SDL_EVENT_WINDOW_CLOSE_REQUESTED = 0x202,

    SDL_EVENT_KEY_DOWN = 0x300,
    SDL_EVENT_KEY_UP = 0x301,

    SDL_EVENT_MOUSE_MOTION = 0x400,
    SDL_EVENT_MOUSE_BUTTON_DOWN = 0x401,
    SDL_EVENT_MOUSE_BUTTON_UP = 0x402,
    SDL_EVENT_MOUSE_WHEEL = 0x403,

    SDL_EVENT_FINGER_DOWN = 0x700,
    SDL_EVENT_FINGER_UP = 0x701,
    SDL_EVENT_FINGER_MOTION = 0x702,

    SDL_EVENT_QUIT = 0x100,
}

[StructLayout(LayoutKind.Explicit, Size = 128)]
public struct SDL_Event
{
    [FieldOffset(0)] public SDL_EventType type;
    [FieldOffset(0)] public SDL_WindowEvent window;
    [FieldOffset(0)] public SDL_MouseMotionEvent motion;
    [FieldOffset(0)] public SDL_MouseButtonEvent button;
    [FieldOffset(0)] public SDL_MouseWheelEvent wheel;
    [FieldOffset(0)] public SDL_TouchFingerEvent tfinger;
}

[StructLayout(LayoutKind.Sequential)]
public struct SDL_WindowEvent
{
    public SDL_EventType type;
    public uint reserved;
    public ulong timestamp;
    public uint windowID;
    public int data1;
    public int data2;
}

[StructLayout(LayoutKind.Sequential)]
public struct SDL_MouseMotionEvent
{
    public SDL_EventType type;
    public uint reserved;
    public ulong timestamp;
    public uint windowID;
    public uint which;
    public uint state;
    public float x;
    public float y;
    public float xrel;
    public float yrel;
}

[StructLayout(LayoutKind.Sequential)]
public struct SDL_MouseButtonEvent
{
    public SDL_EventType type;
    public uint reserved;
    public ulong timestamp;
    public uint windowID;
    public uint which;
    public byte button;
    public byte state;
    public byte clicks;
    public byte padding;
    public float x;
    public float y;
}

[StructLayout(LayoutKind.Sequential)]
public struct SDL_MouseWheelEvent
{
    public SDL_EventType type;
    public uint reserved;
    public ulong timestamp;
    public uint windowID;
    public uint which;
    public float x;
    public float y;
    public int direction;
    public float mouse_x;
    public float mouse_y;
}

[StructLayout(LayoutKind.Sequential)]
public struct SDL_TouchFingerEvent
{
    public SDL_EventType type;
    public uint reserved;
    public ulong timestamp;
    public ulong touchID;
    public ulong fingerID;
    public float x;
    public float y;
    public float dx;
    public float dy;
    public float pressure;
    public uint windowID;
}
