// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Reflection;
using System.Runtime.CompilerServices;

namespace Season.Platforms.Linux;

/// <summary>
/// Unified native-library resolver for the Linux platform.
///
/// <para>Background:</para>
/// <list type="bullet">
/// <item>On Linux, .NET dlopen does not automatically search AssemblyDirectory,
/// and it also does not search the <c>runtimes/&lt;rid&gt;/native/</c> subdirectory
/// unless it is registered in <c>.deps.json</c>.</item>
/// <item>When Season is consumed as a ProjectReference, the native <c>.so</c> files
/// do not appear in the consumer's <c>.deps.json</c>, so absolute-path dlopen must be done manually.</item>
/// <item>Each assembly may register only one <c>SetDllImportResolver</c>,
/// so every P/Invoke inside this assembly that needs to search <c>runtimes/native/</c>
/// must be routed through this single resolver.</item>
/// </list>
///
/// <para>Dispatch rule: use the P/Invoke library name as the key and compose an absolute path of
/// <c>{baseDir}/runtimes/linux-{arch}/native/lib{libName}.so</c> for loading.
/// If that fails, fall back to the operating system's default lookup.
/// Unregistered library names are delegated to <see cref="WebGPU.OnDllImport"/>,
/// and finally fall through to the default .NET mechanism.</para>
///
/// <para>Trigger: <see cref="ModuleInitializerAttribute"/> runs automatically when the Season.dll module loads,
/// with no caller intervention required.</para>
/// </summary>
internal static class LinuxNativeLibraryResolver
{
    /// <summary>
    /// The set of managed library names, meaning libraries that must be resolved
    /// by composing a path under <c>runtimes/native/</c>.
    /// The names must match the strings used in <c>[DllImport(name)]</c> or <c>[LibraryImport(name)]</c>.
    /// The deployed file name is <c>lib{name}.so</c>.
    /// </summary>
    private static readonly HashSet<string> ManagedLibraries =
    [
        "SDL3",
        "glslang",
        "__Internal",       // WebGPU native → libwgpu_native.so
        "wgpu_native",      // Direct reference name.
    ];

    /// <summary>Cache of loaded library handles to avoid repeated dlopen calls.</summary>
    private static readonly Dictionary<string, IntPtr> Handles = new();

    [ModuleInitializer]
    public static void Init()
    {
        NativeLibrary.SetDllImportResolver(typeof(LinuxNativeLibraryResolver).Assembly, Resolve);
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        // 1. Managed libraries: resolve by composing a path under runtimes/<rid>/native/.
        if (ManagedLibraries.Contains(libraryName))
        {
            if (Handles.TryGetValue(libraryName, out IntPtr cached) && cached != IntPtr.Zero)
                return cached;

            string arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
            string libFileName = "";

            if (libraryName == "__Internal")
            {

            }
            else
            {
                libFileName = $"lib{libraryName}.so";
            }

            string runtimeLibPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "runtimes",
                $"linux-{arch}",
                "native",
                libFileName);

            IntPtr handle = IntPtr.Zero;

            // 1. Try the runtimes/<rid>/native/ deploy location (canonical for both libs).
            if (File.Exists(runtimeLibPath))
                NativeLibrary.TryLoad(runtimeLibPath, assembly, searchPath, out handle);

            // 2. Fallback to OS default resolution (RPATH/LD_LIBRARY_PATH/ld.so.cache).
            if (handle == IntPtr.Zero)
                NativeLibrary.TryLoad(libFileName, assembly, searchPath, out handle);

            if (handle != IntPtr.Zero)
            {
                Handles[libraryName] = handle;
                return handle;
            }
        }

        // 3. Final fallback to the default .NET resolution mechanism.
        return IntPtr.Zero;
    }
}
