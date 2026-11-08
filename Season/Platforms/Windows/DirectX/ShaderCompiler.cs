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

    /// <summary>Serialises the diagnostic source write across the prewarm pool. Idle unless the dump is requested.</summary>
    static readonly object _dumpLock = new();

    static int _dumpIndex;

    /// <summary>
    /// One D3DCompiler per thread. ID3DCompiler carries no mutable state, but the lazy singleton this replaced was
    /// published with a plain <c>??=</c>: two threads reaching their first compile together could each call GetApi
    /// and one wrapper would simply be dropped. A thread-static field removes the shared cell instead of guarding
    /// it, which is the cheaper answer when Step 2 makes "first compile" mean several worker threads at once.
    /// </summary>
    [ThreadStatic] static D3DCompiler? t_compiler;

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

    /// <summary>
    /// Worker count for <see cref="Prewarm"/>. Measured, not guessed.
    ///
    /// <para>fxc is bandwidth-bound once several copies run at once. On a 20-thread machine a 20-way wave compiled the
    /// 116.9 KB fog kernel in 19.3 s against the sequential path's 14.2 s, i.e. 36% slower on the one shader that owns
    /// 82% of the compute bill - the parallelism was paying for itself by stealing from the only thing that mattered.
    /// The batch total moved the same way (19.3 s wall against 17.3 s of sequential compile time).</para>
    ///
    /// <para>Four keeps the handful of sub-second kernels behind the long one without the long one losing more than
    /// they gain. <c>SEASON_SHADER_PREWARM_THREADS</c> exists so the number can be re-measured on another machine rather
    /// than trusted, and so an A/B can put the sequential path back without a rebuild.</para>
    /// </summary>
    static readonly int PrewarmThreads = ReadPrewarmThreads();

    static int ReadPrewarmThreads()
    {
        string? raw = Environment.GetEnvironmentVariable("SEASON_SHADER_PREWARM_THREADS")?.Trim();

        if (!string.IsNullOrEmpty(raw) && int.TryParse(raw, out int configured) && configured > 0)
            return configured;

        return Math.Max(1, Math.Min(4, Environment.ProcessorCount));
    }

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
                return TakeCached((ID3D10Blob*)cached, target);
        }

        // A source that reaches this point is about to be handed to fxc, so the diagnostic write below mirrors exactly
        // the compiles the startup report charges for - no cache hits, no variants that never ran.
        DumpSource(hlslSource, entryPoint, target, caller);

        // The compile runs outside the lock. That single change is what lets Step 2's worker pool actually work:
        // holding the lock across fxc meant every "parallel" compile was serialized, and the prewarm would have cost
        // the same wall clock as the sequential path it replaced while looking concurrent in the report.
        var compiled = Compile(hlslSource, entryPoint, target, flags, caller, label);

        lock (_cacheLock)
        {
            // Two threads can miss the same key together. Whoever lands here second keeps the winner's blob and drops
            // their own: the cache holds exactly one reference per key, and callers cannot tell which thread compiled.
            if (_cache.TryGetValue(key, out var raced))
            {
                compiled->Release();
                return TakeCached((ID3D10Blob*)raced, target);
            }

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

    /// <summary>
    /// Diagnostic: when <c>SEASON_SHADER_DUMP</c> names a directory, every source that reaches fxc is written there
    /// under a numbered name. The startup report can say a variant cost 28 s and produced 148 KB, but not which macros
    /// were live when it did, so reproducing that number offline needs the exact text the runtime built. Off unless the
    /// variable is set; a source that fails to write is dropped rather than allowed to disturb a compile.
    /// </summary>
    static void DumpSource(string source, string entryPoint, string target, string caller)
    {
        string? dir = Environment.GetEnvironmentVariable("SEASON_SHADER_DUMP");

        if (string.IsNullOrEmpty(dir)) return;

        int index = Interlocked.Increment(ref _dumpIndex);

        try
        {
            lock (_dumpLock)
            {
                Directory.CreateDirectory(dir);

                // Target and caller first so the listing sorts into families; the index keeps same-sized siblings apart.
                File.WriteAllText(Path.Combine(dir, $"{target}-{entryPoint}-{caller}-{index:D3}-{source.Length}.hlsl"), source);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Hands out one reference to a cached blob. The caller must hold <see cref="_cacheLock"/>: the counters
    /// and the profiler rows are process-wide, and a worker pool would otherwise interleave them.</summary>
    static ID3D10Blob* TakeCached(ID3D10Blob* hit, string target)
    {
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

    /// <summary>One kernel's shader, as handed to <see cref="Prewarm"/>. Only the inputs that reach fxc are kept; the
    /// binding layout belongs to the caller. <paramref name="target"/> defaults to the compute profile because every
    /// Step-2 caller is a kernel; Step 5's background lane also carries the one graphics variant that is expensive
    /// enough to wait for.</summary>
    internal readonly struct PrewarmRequest
    {
        public PrewarmRequest(string hlsl, string entryPoint, string? label, string target = "cs_5_0")
        {
            Hlsl = hlsl;
            EntryPoint = entryPoint;
            Label = label;
            Target = target;
        }

        public readonly string Hlsl;

        public readonly string EntryPoint;

        /// <summary>Diagnostic name, and the A/B switch's only handle on a shader (see <see cref="ResolveFlags"/>).
        /// Null means "never overridden", which is what a graphics variant must pass: its PSO looks the blob up with
        /// no label, so compiling it under a level the lookup cannot reproduce would leave the cache permanently cold.</summary>
        public readonly string? Label;

        public readonly string Target;
    }

    /// <summary>
    /// Step 2: compiles a batch of kernel sources on a worker pool, longest source first, and blocks until every one
    /// is done. Called from the frame boundary with the kernels that were registered since the previous boundary, so
    /// the main thread pays one wave of parallel fxc runs instead of a sequential sum of them.
    ///
    /// <para><b>Why longest first.</b> fxc's cost tracks source size closely enough (<c>fogInject</c> is 116.9 KB and
    /// 40% of the whole bill) that ordering by length is the LPT scheduling heuristic. It only matters in the first
    /// wave: a short kernel dispatched ahead of the long one delays the wave that everyone else is waiting on.</para>
    ///
    /// <para><b>Why failures are swallowed.</b> Prewarming is a head start, never the authority. The request that
    /// decides whether registration succeeds arrives later from the kernel's own build, which owns the "degrade
    /// instead of throw" contract; a batch that threw here would turn a bad shader into a startup crash.</para>
    /// </summary>
    internal static void Prewarm(IReadOnlyList<PrewarmRequest> requests)
    {
        if (requests.Count == 0) return;

        // Dedupe on exactly the triple the cache keys on: two kernels over one source is one fxc run, and under a
        // pool the duplicate would otherwise be paid twice instead of once.
        var unique = new List<PrewarmRequest>(requests.Count);
        var seen = new HashSet<(string, string, string)>(requests.Count);

        for (int i = 0; i < requests.Count; i++)
        {
            var request = requests[i];
            if (string.IsNullOrEmpty(request.Hlsl)) continue;
            if (seen.Add((request.Hlsl, request.EntryPoint, request.Target))) unique.Add(request);
        }

        if (unique.Count == 0) return;

        unique.Sort(static (a, b) => b.Hlsl.Length.CompareTo(a.Hlsl.Length));

        long start = StartupProfiler.Enabled ? StartupProfiler.Ticks() : 0;

        // Every core can be lent to the pool because the thread that waits here is the main thread, and it has
        // nothing else to do until this returns. The ceiling is measured, not derived - see PrewarmThreads.
        var options = new ParallelOptions { MaxDegreeOfParallelism = PrewarmThreads };

        Parallel.ForEach(unique, options, static request =>
        {
            try
            {
                var blob = CompileShaderFromSource(request.Hlsl, request.EntryPoint, request.Target,
                    caller: "prewarm", label: request.Label);

                // The cache keeps its own reference; the returned one is only this call's lease.
                blob->Release();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ShaderCompiler] prewarm {request.Label}: {ex.GetType().Name}: {ex.Message}");
            }
        });

        if (start != 0)
        {
            double wallMs = StartupProfiler.ElapsedMs(start);

            StartupProfiler.Add("shader.prewarm", wallMs);

            // One row for the whole wave, so "37 kernels took 21.7 s" and "the wave took 14.2 s" can be read side by
            // side and the difference attributed to parallelism rather than guessed at.
            StartupProfiler.Item("stage", "compute.prewarm", wallMs, 0,
                $"sources={unique.Count} threads={options.MaxDegreeOfParallelism}");
        }
    }

    // ─────────────────────── Step 5: background lanes ───────────────────────

    /// <summary>
    /// How many fxc instances may run while the frame loop is presenting.
    ///
    /// <para>Two, and it is a different measurement from <see cref="PrewarmThreads"/>. There the pool owned the machine
    /// and the main thread was blocked on it, so the knee sat at four. Here every lane shares the memory bus with asset
    /// streaming, PSO creation and the frame itself, and the two long sources in this wave (the fog pixel shader and
    /// <c>fogInject</c>) are exactly the ones that lose most to contention. Two keeps those two concurrent - which is
    /// what lets the fog feature's two halves land together - while leaving the third copy of fxc off the bus.</para>
    ///
    /// <para><c>SEASON_SHADER_BACKGROUND_LANES</c> exists so the number can be re-measured instead of trusted, and so
    /// one lane can be forced for an A/B that puts the strict-sequential ordering back without a rebuild.</para>
    /// </summary>
    static readonly int BackgroundLanes = ReadBackgroundLanes();

    static int ReadBackgroundLanes()
    {
        string? raw = Environment.GetEnvironmentVariable("SEASON_SHADER_BACKGROUND_LANES")?.Trim();

        if (!string.IsNullOrEmpty(raw) && int.TryParse(raw, out int configured) && configured > 0)
            return configured;

        return 2;
    }

    static readonly object _backgroundLock = new();

    /// <summary>Longest source first, same LPT ordering as <see cref="Prewarm"/>. Arrival order is not the order that
    /// finishes first: with two lanes and a descending list, the two long sources take a lane each and the short ones
    /// fill in behind them, instead of one lane chewing through five short sources while the other holds the 116.9 KB
    /// one alone.</summary>
    static readonly List<PrewarmRequest> _backgroundQueue = new();

    static int _backgroundWorkers;
    static int _backgroundQueued;
    static bool _backgroundStarted;
    static long _backgroundWaveStart;

    /// <summary>Sources still waiting for a lane. A report reads this to tell "not compiled yet" from "refused".</summary>
    internal static int BackgroundPending { get { lock (_backgroundLock) return _backgroundQueue.Count; } }

    /// <summary>
    /// True once the exact key this request would compile is already in the blob cache, i.e. the background lane has
    /// reached it. This is the question a deferred kernel asks before deciding to build its PSO: a cache probe cannot
    /// block, so a kernel whose shader is still in flight answers false and the frame draws without it. Being wrong in
    /// the other direction - building on a miss - would put the 19.9 s fxc call inside a frame, which is the one thing
    /// the deferral exists to prevent.
    /// </summary>
    internal static bool IsCached(string hlslSource, string entryPoint, string target, uint compileFlags = 0, string? label = null)
    {
        if (string.IsNullOrEmpty(hlslSource)) return false;

        var key = (hlslSource, entryPoint, target, ResolveFlags(compileFlags, label));

        lock (_cacheLock) return _cache.ContainsKey(key);
    }

    /// <summary>
    /// Adds sources to the background wave. Never compiles on the calling thread, so a frame boundary can call it
    /// without paying for it.
    ///
    /// <para>A source already in the blob cache - because another kernel over the same text went through the
    /// pre-first-frame batch - is dropped here rather than queued: the cache is the wave's only output, so a hit means
    /// the work is already done.</para>
    /// </summary>
    internal static void QueueBackground(IReadOnlyList<PrewarmRequest> requests)
    {
        if (requests.Count == 0) return;

        bool added = false;

        lock (_backgroundLock)
        {
            for (int i = 0; i < requests.Count; i++)
            {
                var request = requests[i];

                if (string.IsNullOrEmpty(request.Hlsl)) continue;
                if (IsCached(request.Hlsl, request.EntryPoint, request.Target, label: request.Label)) continue;
                if (_backgroundQueue.Exists(existing => existing.Hlsl == request.Hlsl
                    && existing.EntryPoint == request.EntryPoint && existing.Target == request.Target)) continue;

                _backgroundQueue.Add(request);
                _backgroundQueued++;
                added = true;
            }

            // Re-sorting the whole list keeps the LPT property even when the wave is built from two enqueue points
            // (the graphics variant at Pipeline.Init, the kernels at the first frame boundary).
            if (added && _backgroundQueue.Count > 1)
                _backgroundQueue.Sort(static (a, b) => b.Hlsl.Length.CompareTo(a.Hlsl.Length));
        }

        if (added) TrySpawnWorkers();
    }

    /// <summary>
    /// Opens the lanes. Deliberately one frame late: <c>Graphics.FlushPendingCompute</c> calls this on the second
    /// boundary, so the first frame is recorded and presented before two fxc instances start pulling on the same memory
    /// bus the first frame is using.
    ///
    /// <para>Workers are <see cref="ThreadPriority.BelowNormal"/> on purpose. They are the only CPU work in the process
    /// that may be postponed without the picture going wrong, and the frame budget is the one thing this change is not
    /// allowed to spend.</para>
    /// </summary>
    internal static void StartBackground()
    {
        lock (_backgroundLock)
        {
            if (_backgroundStarted) return;
            _backgroundStarted = true;
        }

        StartupProfiler.Mark("background-compile-begin");

        TrySpawnWorkers();
    }

    static void TrySpawnWorkers()
    {
        int spawn;

        lock (_backgroundLock)
        {
            if (!_backgroundStarted) return;

            int desired = Math.Min(BackgroundLanes, _backgroundQueue.Count);
            if (desired <= _backgroundWorkers) return;

            spawn = desired - _backgroundWorkers;
            _backgroundWorkers = desired;

            if (_backgroundWaveStart == 0) _backgroundWaveStart = StartupProfiler.Enabled ? StartupProfiler.Ticks() : 0;
        }

        for (int i = 0; i < spawn; i++)
        {
            var worker = new Thread(BackgroundWorker)
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = $"season-fxc-{i}",
            };

            worker.Start();
        }
    }

    static void BackgroundWorker()
    {
        while (true)
        {
            PrewarmRequest request;

            lock (_backgroundLock)
            {
                if (_backgroundQueue.Count == 0)
                {
                    // The last thread out closes the wave row, so the report shows one background.compile line whose
                    // wall clock can be read against the blocking compute.prewarm line it replaced.
                    if (--_backgroundWorkers == 0 && _backgroundWaveStart != 0)
                    {
                        double wall = StartupProfiler.ElapsedMs(_backgroundWaveStart);
                        int sources = _backgroundQueued;
                        _backgroundWaveStart = 0;

                        StartupProfiler.Add("shader.background", wall);
                        StartupProfiler.Item("stage", "background.compile", wall, 0,
                            $"sources={sources} lanes={BackgroundLanes}");
                        StartupProfiler.Mark("background-compile-end");
                    }

                    return;
                }

                request = _backgroundQueue[0];
                _backgroundQueue.RemoveAt(0);
            }

            try
            {
                var blob = CompileShaderFromSource(request.Hlsl, request.EntryPoint, request.Target,
                    caller: "background", label: request.Label);

                // The cache keeps its own reference; the returned one is only this call's lease.
                blob->Release();
            }
            catch (Exception ex)
            {
                // Same contract as Prewarm: the wave is a head start, never the authority. Whatever wanted this source
                // reports its own failure when it finally tries to build, and the frame keeps drawing without it.
                StartupProfiler.Add("shader.background.failed");
                Console.Error.WriteLine($"[ShaderCompiler] background {request.Label} ({request.Target}): {ex.GetType().Name}: {ex.Message}");
            }
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
        // 1. Get this thread's D3DCompiler instance, reused across its compilations.
        var compiler = t_compiler ??= D3DCompiler.GetApi();

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
            HResult result = compiler.Compile(
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
