// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Runtime.CompilerServices;

namespace Season.Platforms.Linux;

internal static partial class SDL
{
    const string Library = "SDL3";

    // Native library resolution (dlopen of libSDL3.so from runtimes/<rid>/native/)
    // is handled centrally by LinuxNativeLibraryResolver, which registers a single
    // [ModuleInitializer]-driven resolver for the entire Season.dll assembly.

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

    // === Vulkan-related APIs ===
    [LibraryImport(Library, EntryPoint = "SDL_Vulkan_GetInstanceExtensions"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial IntPtr Vulkan_GetInstanceExtensions(out uint count);

    [LibraryImport(Library, EntryPoint = "SDL_Vulkan_CreateSurface"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool Vulkan_CreateSurface(IntPtr window, IntPtr instance, IntPtr allocator, out ulong surface);

    [LibraryImport(Library, EntryPoint = "SDL_GetWindowSizeInPixels"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool GetWindowSizeInPixels(IntPtr window, out int w, out int h);

    [LibraryImport(Library, EntryPoint = "SDL_SetWindowSize"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool SetWindowSize(IntPtr window, int w, int h);

    /// <summary>Restores the window from maximized or minimized state. A maximized window ignores SDL_SetWindowSize, so this must be called before diagnostic forced resizing.</summary>
    [LibraryImport(Library, EntryPoint = "SDL_RestoreWindow"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool RestoreWindow(IntPtr window);

    /// <summary>Blocks until the window manager confirms all pending window state. Under X11 and Wayland, window size confirmation is asynchronous.</summary>
    [LibraryImport(Library, EntryPoint = "SDL_SyncWindow"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool SyncWindow(IntPtr window);

    [LibraryImport(Library, EntryPoint = "SDL_GetWindowFlags"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial WindowFlags GetWindowFlags(IntPtr window);

    [LibraryImport(Library, EntryPoint = "SDL_GetWindowPosition"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool GetWindowPosition(IntPtr window, out int x, out int y);

    [LibraryImport(Library, EntryPoint = "SDL_SetWindowPosition"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool SetWindowPosition(IntPtr window, int x, int y);

    // === WGPU surface creation ===
    [LibraryImport(Library, EntryPoint = "SDL_GetWindowProperties"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint GetWindowProperties(IntPtr window);

    [LibraryImport(Library, EntryPoint = "SDL_GetPointerProperty", StringMarshalling = StringMarshalling.Utf8), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial IntPtr GetPointerProperty(uint props, string name, IntPtr defaultValue);

    [LibraryImport(Library, EntryPoint = "SDL_GetNumberProperty", StringMarshalling = StringMarshalling.Utf8), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial long GetNumberProperty(uint props, string name, long defaultValue);

    public const string PropWindowWaylandDisplayPointer = "SDL.window.wayland.display";
    public const string PropWindowWaylandSurfacePointer = "SDL.window.wayland.surface";
    public const string PropWindowX11DisplayPointer = "SDL.window.x11.display";
    public const string PropWindowX11WindowNumber = "SDL.window.x11.window";

    public static bool TryGetWindowNativeInfo(IntPtr window, out SDL_WindowNativeInfo info)
    {
        uint props = GetWindowProperties(window);
        if (props == 0)
        {
            info = default;
            return false;
        }

        IntPtr waylandDisplay = GetPointerProperty(props, PropWindowWaylandDisplayPointer, IntPtr.Zero);
        IntPtr waylandSurface = GetPointerProperty(props, PropWindowWaylandSurfacePointer, IntPtr.Zero);
        if (waylandDisplay != IntPtr.Zero && waylandSurface != IntPtr.Zero)
        {
            info = new SDL_WindowNativeInfo
            {
                Subsystem = SDL_WindowSubsystem.Wayland,
                Display = waylandDisplay,
                WindowOrSurface = waylandSurface,
            };
            return true;
        }

        IntPtr x11Display = GetPointerProperty(props, PropWindowX11DisplayPointer, IntPtr.Zero);
        long x11Window = GetNumberProperty(props, PropWindowX11WindowNumber, 0);
        if (x11Display != IntPtr.Zero && x11Window != 0)
        {
            info = new SDL_WindowNativeInfo
            {
                Subsystem = SDL_WindowSubsystem.X11,
                Display = x11Display,
                WindowOrSurface = (IntPtr)x11Window,
            };
            return true;
        }

        info = default;
        return false;
    }
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
    // Values must match SDL3/SDL_events.h exactly.
    // SDL3 promoted SDL2 SDL_WINDOWEVENT subtypes into standalone events and reassigned their numbers.
    // Copying SDL2 or preview-build values would dispatch events incorrectly,
    // for example treating 0x202, which means SHOWN, as CLOSE_REQUESTED
    // and falsely deciding to close during startup.
    SDL_EVENT_WINDOW_SHOWN = 0x202,
    SDL_EVENT_WINDOW_HIDDEN = 0x203,
    SDL_EVENT_WINDOW_EXPOSED = 0x204,
    SDL_EVENT_WINDOW_MOVED = 0x205,
    SDL_EVENT_WINDOW_RESIZED = 0x206,
    SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED = 0x207,
    SDL_EVENT_WINDOW_MINIMIZED = 0x209,
    SDL_EVENT_WINDOW_MAXIMIZED = 0x20A,
    SDL_EVENT_WINDOW_RESTORED = 0x20B,
    SDL_EVENT_WINDOW_MOUSE_ENTER = 0x20C,
    SDL_EVENT_WINDOW_MOUSE_LEAVE = 0x20D,
    SDL_EVENT_WINDOW_FOCUS_GAINED = 0x20E,
    SDL_EVENT_WINDOW_FOCUS_LOST = 0x20F,
    SDL_EVENT_WINDOW_CLOSE_REQUESTED = 0x210,

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

public readonly struct SDL_WindowNativeInfo
{
    public SDL_WindowSubsystem Subsystem { get; init; }
    public IntPtr Display { get; init; }
    public IntPtr WindowOrSurface { get; init; }
}

public enum SDL_WindowSubsystem : uint
{
    Unknown = 0,
    X11 = 2,
    Wayland = 6,
}
