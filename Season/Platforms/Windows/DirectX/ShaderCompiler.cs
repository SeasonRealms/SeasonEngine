// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;

namespace Season.Platforms.Windows.DirectX;

/// <summary>
/// Compiles inline HLSL to DXBC at runtime through fxc (D3DCompile) and caches the resulting blobs by
/// content, matching MTLShaderCompiler on Metal and ShaderCompiler on Vulkan.
///
/// Why the cache exists: Pipeline.Init and BlitPipeline.Init bake ~36 graphics PSO variants, but most
/// variants differ only in rasterizer / blend / depth / render-target state and share byte-for-byte
/// identical bytecode. Without caching, the same shader was recompiled six to seven times over, and every
/// duplicate compile landed inside the pre-first-frame window the user sees as a white screen.
///
/// Ownership: the cache keeps one reference per entry and every hand-out adds another, so callers keep the
/// existing contract of releasing the blob once the PSO is created. Entries live for the process lifetime,
/// which is the same trade Metal makes with its IMTLLibrary cache.
/// </summary>
internal static unsafe class ShaderCompiler
{
    static readonly object _cacheLock = new();

    /// <summary>The key covers every input that can change the emitted bytecode.</summary>
    static readonly Dictionary<(string Source, string EntryPoint, string Target, uint Flags), nint> _cache = new();

    static D3DCompiler _compiler;

    /// <summary>fxc invocations that actually ran, in other words cache misses.</summary>
    internal static int CompileCount { get; private set; }

    /// <summary>Requests answered from the blob cache without invoking fxc.</summary>
    internal static int CacheHitCount { get; private set; }

    /// <summary>
    /// Contract 3-2 A/B switch: <c>SEASON_FXC_LEVEL=od|o2|o3[:filter]</c> overrides the optimization level of the shaders
    /// whose label contains <c>filter</c> (a compute kernel passes its kernel name, so <c>od:fogInject</c> moves exactly one
    /// shader). A bare level applies to every labelled shader and a filter never reaches an unlabelled one, so a graphics
    /// PSO cannot change level behind the graphics numbers. Unset means no override at all, i.e. the shipped flags.
    ///
    /// <para>Why it exists: one kernel (<c>fogInject</c>, 116.9 KB of [unroll] code) spends ~14 s inside fxc's optimizer,
    /// which is 40% of the whole first-frame bill. "The optimizer is the cost" is only proven by compiling the same source
    /// both ways, and that comparison has to keep the two legs in one report format instead of two builds.</para>
    /// </summary>
    static readonly uint fxcOverrideFlags;

    static readonly string? fxcOverrideFilter;

    /// <summary>The active switch in log form, so a report can never be attributed to the wrong leg.</summary>
    internal static readonly string LevelLabel = "default";

    static ShaderCompiler()
    {
        string? raw = Environment.GetEnvironmentVariable("SEASON_FXC_LEVEL")?.Trim();

        if (string.IsNullOrEmpty(raw)) return;

        int separator = raw.IndexOf(':');

        string level = (separator < 0 ? raw : raw[..separator]).ToLowerInvariant();

        fxcOverrideFlags = level switch
        {
            "od" or "o0" => 0x4u,      // D3DCOMPILE_SKIP_OPTIMIZATION: front end only, the optimizer pass is skipped
            "o2" => 0xC000u,           // D3DCOMPILE_OPTIMIZATION_LEVEL2
            "o3" => 0x8000u,           // D3DCOMPILE_OPTIMIZATION_LEVEL3
            _ => 0u,
        };

        if (fxcOverrideFlags == 0) return;

        if (separator >= 0 && separator + 1 < raw.Length) fxcOverrideFilter = raw[(separator + 1)..];

        LevelLabel = fxcOverrideFilter == null ? level : $"{level}:{fxcOverrideFilter}";
    }

    internal static ID3D10Blob* CompileShaderFromSource(
        string hlslSource,
        string entryPoint,
        string target,
        uint compileFlags = 0,
        [CallerMemberName] string caller = "",
        string? label = null)
    {
        var flags = ResolveFlags(compileFlags, label);

        var key = (hlslSource, entryPoint, target, flags);

        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                var hit = (ID3D10Blob*)cached;
                hit->AddRef();
                CacheHitCount++;
                // Contract 3-2: the dedup ratio is what makes the compile bill readable - six PSOs over one source are
                // one fxc invocation, and only this counter shows whether that reuse actually happened.
                StartupProfiler.Add("shader.cache-hit");
                
                // Contract 3-2: a kernel whose source another kernel already compiled lands here instead of in the
                // compile rows, so this counter is what turns "N kernels" and "M compiles" into a readable pair.
                if (target == "cs_5_0") StartupProfiler.Add("shader.cache-hit.cs");
                return hit;
            }

            var compiled = Compile(hlslSource, entryPoint, target, flags, caller, label);
            _cache[key] = (nint)compiled;
            CompileCount++;

            // Contract 3-2: a counter that only exists on the A/B leg, so the report says which level produced it even
            // when both legs are read side by side later.
            if (flags != compileFlags) StartupProfiler.Add("shader.compile.ab");

            // One reference stays with the cache, the returned one belongs to the caller.
            compiled->AddRef();
            return compiled;
        }
    }

    // Contract 3-2: an A/B leg must not leak into the other leg's numbers. A filter restricts the override to the
    // labelled shaders it names; a bare level restricts it to labelled shaders at all (compute kernels are the only
    // callers that pass one), so graphics PSOs keep the shipped flags.
    static uint ResolveFlags(uint requested, string? label) => OnAbLeg(label) ? fxcOverrideFlags | requested : requested;

    // Contract 3-2: true when this shader is named by the A/B switch. It also decides whether a warnings-only compile
    // may continue: at /Od fxc's dataflow analysis is weaker and reports X4000 warnings the optimized build does not,
    // and the project's "any diagnostic is fatal" policy would otherwise abort the very registration being measured.
    static bool OnAbLeg(string? label)
    {
        if (fxcOverrideFlags == 0 || label == null) return false;

        return fxcOverrideFilter == null || label.Contains(fxcOverrideFilter, StringComparison.OrdinalIgnoreCase);
    }

    // Contract 3-2: label is the caller's own name for this shader - a compute kernel passes its kernel name - so a
    // startup row reads "kernel=taaResolve" instead of only "cs_5_0 CSMain". It stays out of the cache key: one source
    // compiled for two kernels is still one compile.
    static ID3D10Blob* Compile(string hlslSource, string entryPoint, string target, uint compileFlags, string caller, string? label)
    {
        // 1. Get the D3DCompiler instance, reused across compilations.
        _compiler ??= D3DCompiler.GetApi();

        // Contract 3-2: fxc is the one place every graphics and compute shader is actually translated, and on the
        // pre-first-frame path it runs on the calling (main) thread - the duration below is main-thread blocking time.
        long compileStart = StartupProfiler.Enabled ? StartupProfiler.Ticks() : 0;

        // 2. Prepare compilation parameters.
        ID3D10Blob* shaderBlob = null;
        ID3D10Blob* errorBlob = null;

        // 3. Convert the C# string to a byte array.
        byte[] sourceBytes = System.Text.Encoding.UTF8.GetBytes(hlslSource);

        // 4. Pin the byte array with GCHandle and marshal the ANSI parameters.
        GCHandle handle = GCHandle.Alloc(sourceBytes, GCHandleType.Pinned);
        IntPtr entryPtr = Marshal.StringToHGlobalAnsi(entryPoint);
        IntPtr targetPtr = Marshal.StringToHGlobalAnsi(target);
        try
        {
            // 5. Create safe pointers.
            nint sourcePtr = handle.AddrOfPinnedObject();

            // 6. Compile the shader.
            HResult result = _compiler.Compile(
        pSrcData: (void*)sourcePtr,
        SrcDataSize: (nuint)sourceBytes.Length,
        pSourceName: (byte*)null, // Explicit type to resolve ambiguity
        pDefines: null,
        pInclude: null,
        pEntrypoint: (byte*)entryPtr,
        pTarget: (byte*)targetPtr,
        Flags1: compileFlags,
        Flags2: 0,
        ppCode: ref shaderBlob,
        ppErrorMsgs: ref errorBlob
    );

            // 7. Check the compilation result. fxc also writes warnings such as X4000 into errorBlob, and this
            // project deliberately treats any diagnostic as fatal so kernel sources stay warning-free. The one
            // exception is the A/B leg of Contract 3-2, and only when fxc actually produced code: failing there would
            // measure nothing, so the diagnostics are printed and counted instead of thrown.
            if (errorBlob != null)
            {
                // Get the error message.
                string errorMsg = Marshal.PtrToStringAnsi((nint)errorBlob->GetBufferPointer()) ?? string.Empty;

                // Release the error blob.
                errorBlob->Release();

                errorBlob = null;

                if (!OnAbLeg(label) || shaderBlob == null || result.IsFailure)
                    throw new Exception($"Shader compilation failed [{entryPoint} {target}]:\n{errorMsg}");

                Console.Error.WriteLine($"[ShaderCompiler] A/B leg {LevelLabel}: {target} {entryPoint} ({label}) compiled with diagnostics:\n{errorMsg}");

                StartupProfiler.Add("shader.compile.ab.warned");
            }

            // Check whether the result succeeded.
            if (result.IsFailure)
            {
                throw new Exception($"Shader compilation failed [{entryPoint} {target}] with HResult: {result}");
            }

            if (compileStart != 0)
            {
                StartupProfiler.Item("shader", $"{target} {entryPoint}", StartupProfiler.ElapsedMs(compileStart),
                    (long)shaderBlob->GetBufferSize(),
                    string.IsNullOrEmpty(label) ? $"caller={caller}" : $"caller={caller} kernel={label}");

                StartupProfiler.Add("shader.compile");

                // Contract 3-2: the compute share of the compile bill, measured without counting rows by hand; the byte
                // total is what a disk cache or a size budget has to be sized against.
                if (target == "cs_5_0") StartupProfiler.Add("shader.compile.cs", StartupProfiler.ElapsedMs(compileStart), (long)shaderBlob->GetBufferSize());
            }

            return shaderBlob;
        }
        finally
        {
            // 8. Release the GCHandle and the marshalled parameter strings.
            if (handle.IsAllocated)
                handle.Free();
            Marshal.FreeHGlobal(entryPtr);
            Marshal.FreeHGlobal(targetPtr);
        }
    }
}
