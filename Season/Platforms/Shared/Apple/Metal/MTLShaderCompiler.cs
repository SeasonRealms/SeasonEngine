// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Foundation;
using Metal;

namespace Season.Platforms.Shared.Apple.Metal;

/// <summary>
/// Aligns with the DX and VK ShaderCompiler implementations by compiling MSL source into IMTLLibrary objects
/// and caching them by content for reuse.
/// Pipeline.Init calls it once during bootstrap, and all three pipeline tiers share the same library.
/// This content cache is the mechanism that keeps every backend at one compile per distinct source: DX12 caches
/// DXBC blobs and Vulkan caches SPIR-V the same way, while WebGPU achieves it structurally by creating a single
/// GPUShaderModule per shader on the JS side.
/// </summary>
internal static class MTLShaderCompiler
{
    static readonly Dictionary<string, IMTLLibrary> _cache = new();

    /// <summary>CreateLibrary invocations that actually ran, in other words cache misses.</summary>
    public static int CompileCount { get; private set; }

    /// <summary>Requests answered from the library cache without invoking the MSL compiler.</summary>
    public static int CacheHitCount { get; private set; }

    /// <summary>
    /// Compiles MSL source into an IMTLLibrary.
    /// Failures throw with the NSError description when available.
    /// The same source string is compiled only once.
    /// </summary>
    public static IMTLLibrary Compile(IMTLDevice device, string source, MTLLanguageVersion version = MTLLanguageVersion.v2_0)
    {
        if (_cache.TryGetValue(source, out var cached) && cached != null)
        {
            CacheHitCount++;
            return cached;
        }

        var options = new MTLCompileOptions
        {
            LanguageVersion = version
        };

        var library = device.CreateLibrary(source, options, out NSError? error);
        if (library == null)
        {
            string msg = error?.LocalizedDescription ?? "(no NSError)";
            throw new Exception($"IMTLDevice.CreateLibrary failed: {msg}");
        }

        _cache[source] = library;
        CompileCount++;
        return library;
    }

    public static void Clear()
    {
        foreach (var kv in _cache) kv.Value?.Dispose();
        _cache.Clear();
        CompileCount = 0;
        CacheHitCount = 0;
    }
}
