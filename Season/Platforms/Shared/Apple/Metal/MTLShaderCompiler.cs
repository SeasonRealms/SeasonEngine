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
    /// <summary>
    /// The cache was an unguarded Dictionary. Nothing compiled off the main thread until Step 2 gave the other two
    /// backends a worker-pool prewarm, and a Dictionary read racing a write corrupts the bucket chain rather than
    /// throwing - the failure would have shown up as a missing library much later. The lock covers the lookup and the
    /// insert only; CreateLibrary stays outside it, matching the DX12 and Vulkan compilers.
    /// </summary>
    static readonly object _cacheLock = new();

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
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(source, out var cached) && cached != null)
            {
                CacheHitCount++;

                // Contract 3-2: the dedup ratio is what makes the compile bill readable - a dozen PSOs over one library
                // are one MSL compile, and only this counter shows whether that reuse actually happened.
                StartupProfiler.Add("shader.cache-hit");

                return cached;
            }
        }

        var options = new MTLCompileOptions
        {
            LanguageVersion = version
        };

        // Contract 3-2: CreateLibrary is the one place shaders are translated on this backend, and on the pre-first-frame
        // path it runs on the calling (main) thread - the duration below is main-thread blocking time.
        long compileStart = StartupProfiler.Enabled ? StartupProfiler.Ticks() : 0;

        var library = device.CreateLibrary(source, options, out NSError? error);
        if (library == null)
        {
            // A failed compile still cost the time it cost, so the row is written before the throw.
            if (compileStart != 0)
            {
                double failedMs = StartupProfiler.ElapsedMs(compileStart);

                StartupProfiler.Item("shader", NameOf(CountFunctions(source)), failedMs, 0,
                    $"caller=MTLShaderCompiler src={source.Length} ms={failedMs:0.000} failed=1");

                StartupProfiler.Add("shader.compile.failed");
            }

            string msg = error?.LocalizedDescription ?? "(no NSError)";
            throw new Exception($"IMTLDevice.CreateLibrary failed: {msg}");
        }

        lock (_cacheLock)
        {
            // Two callers can miss the same source together; the winner's library is the one everything else shares,
            // so the loser's copy is disposed rather than leaked.
            if (_cache.TryGetValue(source, out var raced) && raced != null)
            {
                CacheHitCount++;
                library.Dispose();

                StartupProfiler.Add("shader.cache-hit");

                return raced;
            }

            _cache[source] = library;
            CompileCount++;

            if (compileStart != 0)
            {
                double ms = StartupProfiler.ElapsedMs(compileStart);

                // Metal compiles a whole library at once, so a row is a library and not a stage. The stage counts below are
                // what keeps the row comparable to Vulkan's per-stage rows: they say whether this library holds one
                // kernel or every graphics function of the frame.
                var counts = CountFunctions(source);

                StartupProfiler.Item("shader", NameOf(counts), ms, 0,
                    $"caller=MTLShaderCompiler src={source.Length} ver={version} v={counts.Vertex} f={counts.Fragment} k={counts.Kernel}");

                StartupProfiler.Add("shader.compile");
            }

            return library;
        }
    }

    /// <summary>
    /// Contract 3-2: the row name. Metal has no per-stage compile, so the name carries the shape of the library instead:
    /// <c>msl v3 f5 k2</c> is instantly readable next to Vulkan's <c>vertex pipeline.vert</c> rows.
    /// </summary>
    static string NameOf((int Vertex, int Fragment, int Kernel) counts) =>
        $"msl v{counts.Vertex} f{counts.Fragment} k{counts.Kernel}";

    /// <summary>
    /// Counts MSL function definitions by leading keyword. A declaration in this codebase always starts a line with
    /// <c>vertex</c>/<c>fragment</c>/<c>kernel</c>, so a line scan is enough and stays out of the compiler's hot path
    /// (it runs once per cache miss, never per request).
    /// </summary>
    static (int Vertex, int Fragment, int Kernel) CountFunctions(string source)
    {
        int vertex = 0, fragment = 0, kernel = 0;

        foreach (ReadOnlySpan<char> line in source.AsSpan().EnumerateLines())
        {
            ReadOnlySpan<char> trimmed = line.TrimStart();

            if (trimmed.StartsWith("vertex ", StringComparison.Ordinal)) vertex++;
            else if (trimmed.StartsWith("fragment ", StringComparison.Ordinal)) fragment++;
            else if (trimmed.StartsWith("kernel ", StringComparison.Ordinal)) kernel++;
        }

        return (vertex, fragment, kernel);
    }

    public static void Clear()
    {
        lock (_cacheLock)
        {
            foreach (var kv in _cache) kv.Value?.Dispose();
            _cache.Clear();
            CompileCount = 0;
            CacheHitCount = 0;
        }
    }
}
