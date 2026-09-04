// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Platforms.Windows;

/// <summary>
/// Unified native library resolver for the Windows platform.
///
/// <para>Background:</para>
/// <list type="bullet">
/// <item>When Season is consumed as a ProjectReference, native DLLs are not included in the
/// consuming application's .deps.json, so their absolute paths must be resolved manually.</item>
/// <item>Each Assembly can register only one <c>SetDllImportResolver</c>, so every P/Invoke in
/// this Assembly that needs to look under runtimes/native/ must be routed through this
/// centralized resolver.</item>
/// </list>
///
/// <para>Dispatch rules: use the P/Invoke library name as the key and load from the absolute path
/// <c>{baseDir}/runtimes/{rid}/native/{libName}.dll</c>.
/// If loading fails, fall back to the OS default lookup. Unregistered library names are delegated
/// to <see cref="WebGPU.OnDllImport"/>, and finally to the .NET default mechanism.</para>
///
/// <para>Trigger: <see cref="ModuleInitializerAttribute"/> runs automatically when the Season.dll
/// module is loaded, with no caller involvement required.</para>
/// </summary>
internal static class WindowsNativeLibraryResolver
{
    /// <summary>
    /// Set of managed library names, meaning libraries that should be loaded by constructing a path
    /// under runtimes/native/.
    /// The library name must exactly match the string in <c>[DllImport(name)]</c> /
    /// <c>[LibraryImport(name)]</c>.
    /// </summary>
    private static readonly HashSet<string> ManagedLibraries =
    [
        "qwen",
        "__Internal",    // WebGPU native → wgpu_native.dll
        "wgpu_native",   // Direct reference name
    ];

    /// <summary>Cache of loaded library handles to avoid duplicate loads.</summary>
    private static readonly Dictionary<string, IntPtr> Handles = new();

    [ModuleInitializer]
    public static void Init()
    {
        NativeLibrary.SetDllImportResolver(typeof(WindowsNativeLibraryResolver).Assembly, Resolve);
    }

    private static IntPtr Resolve(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        // ── 1. Managed libraries: build a path under runtimes/<rid>/native/ ──
        if (ManagedLibraries.Contains(libraryName))
        {
            if (Handles.TryGetValue(libraryName, out IntPtr cached) && cached != IntPtr.Zero)
                return cached;

            IntPtr handle = IntPtr.Zero;

            // WebGPU native library: map to wgpu_native.dll
            if (libraryName == "__Internal")
            {
                string arch = RuntimeInformation.ProcessArchitecture switch
                {
                    Architecture.X64 => "x64",
                    Architecture.Arm64 => "arm64",
                    _ => "x64"
                };
                string wgpuPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "runtimes", $"win-{arch}", "native", "wgpu_native.dll");

                if (File.Exists(wgpuPath))
                    NativeLibrary.TryLoad(wgpuPath, assembly, searchPath, out handle);
            }
            else
            {
                // qwen and other managed libraries
                string arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
                string runtimeLibPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "runtimes", $"win-{arch}", "native", $"{libraryName}.dll");

                if (File.Exists(runtimeLibPath))
                    NativeLibrary.TryLoad(runtimeLibPath, assembly, searchPath, out handle);
            }

            if (handle != IntPtr.Zero)
            {
                Handles[libraryName] = handle;
                return handle;
            }

            // If managed-library resolution fails, do not fall back to the OS default lookup here.
            // Let WebGPU.OnDllImport try once more because it also has fallback logic.
        }

        // ── 3. Final fallback to the .NET default mechanism ──
        return IntPtr.Zero;
    }
}
