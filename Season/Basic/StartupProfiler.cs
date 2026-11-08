// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Basic;

/// <summary>
/// Contract 3-2: startup cost, the sibling of Contract 3-1's frame cost. Contract 3-1 answers "what does one frame
/// cost"; this answers "what does it cost to reach the first frame, and how long after that until the asynchronous
/// loads are done".
///
/// <para><b>Two shapes, because startup has two shapes.</b> The blocking half is straight-line main-thread work -
/// device init, the PSO/fxc bake, UI construction - and is captured as nested <see cref="Scope"/> intervals. The
/// loading half is a background queue and is captured per item (<see cref="Item"/>) plus its queue wait
/// (<see cref="NoteQueued"/>/<see cref="TakeQueuedWait"/>). "The model finished loading" and "the queue let it start"
/// are different costs, and only the second one explains a startup that stalls while nothing appears to happen.</para>
///
/// <para><b>Where the report closes.</b> Startup ends once the load queue has been empty and untouched for
/// <see cref="quietMs"/> and at least <see cref="MinFrames"/> frames have been rendered. Closing on "everything
/// finished" instead would never fire for a control that only loads after user input; a queue that keeps loading is
/// closed by <see cref="maxMs"/> and the report records that as the reason.</para>
///
/// <para><b>Off is free.</b> <see cref="Enabled"/> is resolved once, on the first entry point, and every call site tests it
/// first, so a normal build pays one predictable branch per call site and allocates nothing. Resolution is lazy rather
/// than a static field initializer because a host without an environment (Android, a browser) has to push its switches in
/// through <see cref="ProfileSwitch"/> before the profiler reads them - see that type for the ordering contract.</para>
///
/// <para><b>Why the zero is <see cref="origin"/>.</b> It is the moment this type was first touched, which is the first
/// instrumentation site (<c>app-launch</c>). Everything before it - CLR, XAML, the shell - cannot be subdivided from
/// inside the engine, so it is reported once as <c>process-start -&gt; origin</c> and every number below stays relative
/// to the engine's own first instruction.</para>
///
/// <para><b>Threading.</b> Every producer may run on a different thread (the render loop, the load consumer, the font
/// task), so the entry list and the counters are lock-protected; only the nesting depth is thread-static. Ordering is
/// reconstructed from the timestamps, not from insertion order.</para>
/// </summary>
public static class StartupProfiler
{
    const string OutputVariable = "SEASON_STARTUP_OUT";
    const string FlagVariable = "SEASON_STARTUP";
    const string ExitVariable = "SEASON_STARTUP_EXIT";
    const string QuietVariable = "SEASON_STARTUP_QUIET_MS";
    const string MaxVariable = "SEASON_STARTUP_MAX_MS";
    const string TraceVariable = "SEASON_STARTUP_TRACE";

    /// <summary>Frames that must have been rendered before a quiet queue may close the report.</summary>
    const int MinFrames = 3;

    /// <summary>Default silence after the last load activity that counts as "the queue is done".</summary>
    const double DefaultQuietMs = 1000;

    /// <summary>Default hard limit on the report; a queue that keeps loading is closed here and says so.</summary>
    const double DefaultMaxMs = 60000;

    /// <summary>Zero of every number in the report: the moment this type was initialized, i.e. the first instrumentation site.</summary>
    static readonly long origin = Stopwatch.GetTimestamp();

    /// <summary>
    /// Where the report goes, or null when the profiler was asked to collect without writing (the one-line console summary
    /// is still printed). Resolved once by <see cref="Resolve"/>; see <see cref="ProfileOutput"/> for the destinations.
    /// </summary>
    static IProfileOutput? output;

    /// <summary>
    /// Optional breadcrumb file. Every mark appends one line, so "no report was written" can be told apart from "the run
    /// never reached the first frame": without it the two failures look identical - an empty output directory.
    /// </summary>
    static string? tracePath;

    /// <summary>Set when the trace destination is the console instead of a file; see <see cref="ProfileOutput.ConsoleDestination"/>.</summary>
    static bool traceToConsole;

    static readonly object traceLock = new();

    static bool exitWhenDone;

    static double quietMs = DefaultQuietMs;

    static double maxMs = DefaultMaxMs;

    static readonly string runId = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    static readonly string platform = RuntimeInformation.RuntimeIdentifier;

    /// <summary>Set after every switch above has been read; this is the fast path of <see cref="Enabled"/>.</summary>
    static volatile bool configResolved;

    static volatile bool enabled;

    /// <summary>True when <c>SEASON_STARTUP</c> or <c>SEASON_STARTUP_OUT</c> is set. Read at every site as the first line.</summary>
    public static bool Enabled
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (configResolved) return enabled;

            EnsureConfig();

            return enabled;
        }
    }

    /// <summary>
    /// Resolves every switch exactly once. Separate from <see cref="Enabled"/> so the hot path can inline away: the first
    /// call pays for the switch reads, every later one is a volatile bool read.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void EnsureConfig()
    {
        if (configResolved) return;

        Resolve();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Resolve()
    {
        lock (sync)
        {
            if (configResolved) return;

            // Contract 3-2: every read goes through ProfileSwitch, so a host that has no environment to set (an intent
            // extra, a URL query, a marker file) can push the same names in before this runs. See ProfileSwitch.Set.
            output = ProfileOutput.Create(ProfileSwitch.Text(OutputVariable));

            string? trace = ProfileSwitch.Text(TraceVariable);

            if (string.Equals(trace, ProfileOutput.ConsoleDestination, StringComparison.OrdinalIgnoreCase))
            {
                traceToConsole = true;
            }
            else
            {
                tracePath = trace;
            }

            exitWhenDone = ProfileSwitch.Flag(ExitVariable);

            quietMs = ProfileSwitch.Number(QuietVariable, DefaultQuietMs);

            maxMs = ProfileSwitch.Number(MaxVariable, DefaultMaxMs);

            enabled = output != null || ProfileSwitch.Flag(FlagVariable);

            configResolved = true;

            // Published last: from here on a late ProfileSwitch.Set has no effect and says so.
            ProfileSwitchTable.Resolved = true;
        }
    }

    static readonly object sync = new();

    static readonly List<Entry> items = new();

    static readonly Dictionary<string, Counter> counters = new(StringComparer.Ordinal);

    /// <summary>Queue entry time per loadable ID, so the wait can be charged to the item that paid it.</summary>
    static readonly Dictionary<long, long> queuedAt = new();

    /// <summary>
    /// IDs whose Load has already been accounted once. It is what separates startup content from runtime churn: a HUD
    /// text re-enters the queue for as long as the process lives, and a single pass over this set is enough to tell the
    /// second and every later Load of the same control apart from the one that was part of startup.
    /// </summary>
    static readonly HashSet<long> loadedOnce = new();

    /// <summary>Nesting depth of the current thread's scopes; drives the indentation of the written timeline.</summary>
    [ThreadStatic] static int depth;

    /// <summary>
    /// Loads queued as startup content and not accounted yet. It is the startup-only twin of the application's queue
    /// depth: the queue also carries runtime requeues, and those must not hold the report open forever.
    /// </summary>
    static int startupPending;

    static long lastActivityTicks;

    static int frames;

    static bool reported;

    /// <summary>
    /// Opens a measured interval. Use it around a blocking stage (<c>using (StartupProfiler.Scope("Pipeline.Init"))</c>)
    /// so the report shows both the stage's total and everything nested inside it.
    /// </summary>
    public static ScopeToken Scope(string name)
    {
        // Resolved before the trace test, because the trace destination is itself part of the configuration: a host that
        // pushes its switches in after the first scope must still get the breadcrumb it asked for.
        EnsureConfig();

        if (!enabled) return default;

        if (tracePath != null || traceToConsole) Trace($"scope-begin {name}");

        return new ScopeToken(name, Stopwatch.GetTimestamp(), depth++);
    }

    /// <summary>Records a point in time: a stage boundary that has no duration of its own.</summary>
    public static void Mark(string name)
    {
        EnsureConfig();

        if (tracePath != null || traceToConsole) Trace($"mark {name} enabled={enabled} out={output?.Description ?? "(null)"}");

        if (!enabled) return;

        double now = Ms(Stopwatch.GetTimestamp());

        lock (sync) items.Add(new Entry("mark", name, now, now, 0, 0, 0, null));
    }

    /// <summary>
    /// Records one finished piece of work measured by the caller. <paramref name="ms"/> is the whole duration; the start
    /// is derived from it, so an item that was measured around an <c>await</c> still lands on the right place in time.
    /// </summary>
    /// <param name="kind">Family, e.g. <c>shader</c>, <c>pso</c>, <c>load</c>, <c>font</c>.</param>
    /// <param name="name">What was measured, e.g. <c>vs_6_0 main</c> or the control name.</param>
    /// <param name="ms">Duration in milliseconds.</param>
    /// <param name="bytes">Payload size when one exists (bytecode, file), otherwise 0.</param>
    /// <param name="detail">Free-form column; keep it short, it is one CSV field.</param>
    /// <param name="aux">Family-specific number: for <c>load</c> it is the queue wait in milliseconds.</param>
    public static void Item(string kind, string name, double ms, long bytes = 0, string? detail = null, double aux = 0)
    {
        if (!Enabled) return;

        double now = Ms(Stopwatch.GetTimestamp());

        lock (sync) items.Add(new Entry(kind, name, now - ms, now, 0, bytes, aux, detail));
    }

    /// <summary>Adds to a named counter. Counters are the cheap half of the report: ratios and failures, no timeline.</summary>
    public static void Add(string name, double ms = 0, long bytes = 0)
    {
        if (!Enabled) return;

        lock (sync)
        {
            if (!counters.TryGetValue(name, out var counter))
            {
                counter = new Counter();

                counters[name] = counter;
            }

            counter.Count++;
            counter.Ms += ms;
            counter.Bytes += bytes;
        }
    }

    /// <summary>Remembers when a loadable entered the queue. Called by <see cref="BaseApp.RequestLoad"/> after the write succeeds.</summary>
    public static void NoteQueued(long id)
    {
        if (!Enabled) return;

        long now = Stopwatch.GetTimestamp();

        bool repeat;

        lock (sync)
        {
            queuedAt[id] = now;

            repeat = loadedOnce.Contains(id);
        }

        // A control that has already been loaded once is being requeued by ordinary runtime behaviour, so it must not
        // restart the quiet window - otherwise a twice-per-second readout holds the report open until the hard limit.
        if (!repeat)
        {
            Interlocked.Increment(ref startupPending);

            TouchActivity();
        }

        Add(repeat ? "load.requeue" : "load.requested");
    }

    /// <summary>Returns the milliseconds a loadable spent waiting in the queue and forgets the entry, or -1 when unknown.</summary>
    public static double TakeQueuedWait(long id)
    {
        if (!Enabled) return -1;

        lock (sync)
        {
            if (!queuedAt.Remove(id, out long queued)) return -1;

            return (Stopwatch.GetTimestamp() - queued) * 1000.0 / Stopwatch.Frequency;
        }
    }

    /// <summary>
    /// Accounts one finished Load. The first time an ID reaches this point it is startup content and keeps the quiet
    /// window open; every later pass over the same ID is recorded as <c>load-churn</c> and ignored by both
    /// <c>last-load-complete</c> and the quiet check, so "the scene became complete" is not reported as "some text on the
    /// HUD changed again". Failures keep their own kind on every pass, because a control that fails repeatedly is exactly
    /// the thing a report must not hide behind a repeat filter.
    /// </summary>
    public static void NoteLoaded(long id, string name, double ms, double queuedMs, string outcome)
    {
        if (!Enabled) return;

        bool first;

        lock (sync) first = loadedOnce.Add(id);

        if (first) Interlocked.Decrement(ref startupPending);

        bool failed = outcome is not ("ok" or "disposed" or "skipped");

        // Failures stay on the load-failed kind even on a repeat pass, so a control that keeps failing still moves
        // last-load-complete: the repeat filter is meant to hide HUD churn, never an error that has to be chased.
        Item(failed ? "load-failed" : first ? "load" : "load-churn", name, ms, 0,
            $"outcome={outcome} id={id}", queuedMs);

        if (first) TouchActivity();
    }

    /// <summary>Marks load activity without recording an item; keeps the quiet window from closing while work is still moving.</summary>
    public static void TouchActivity()
    {
        if (!Enabled) return;

        Interlocked.Exchange(ref lastActivityTicks, Stopwatch.GetTimestamp());
    }

    /// <summary>
    /// Per-frame tick, called from the one frame entry point every platform shares. It counts frames, records the first
    /// one as a milestone, and closes the report when the startup load queue has gone quiet (or when the hard limit is
    /// reached).
    /// </summary>
    /// <param name="pendingLoads">Application queue depth, reported as context; the close decision uses the startup-only
    /// counter, because the application queue also holds runtime requeues that are not part of startup.</param>
    public static void OnFrame(int pendingLoads)
    {
        if (!Enabled) return;

        long now = Stopwatch.GetTimestamp();

        int frame = ++frames;

        if (frame <= 3) Trace($"frame {frame} pending={pendingLoads} startupPending={Volatile.Read(ref startupPending)} elapsed={Ms(now):0.0}");

        if (frame == 1)
            Item("frame", "first-frame", 0, 0, pendingLoads > 0 ? $"pending={pendingLoads}" : "pending=0");

        if (Volatile.Read(ref startupPending) > 0)
            Interlocked.Exchange(ref lastActivityTicks, now);

        if (reported) return;

        double elapsed = Ms(now);

        if (elapsed >= maxMs)
        {
            Write("timeout", elapsed);

            return;
        }

        if (frame < MinFrames || elapsed < quietMs) return;

        long idleTicks = now - Interlocked.Read(ref lastActivityTicks);

        if (Volatile.Read(ref startupPending) == 0 && idleTicks * 1000.0 / Stopwatch.Frequency >= quietMs)
            Write("quiet", elapsed);
    }

    /// <summary>Raw timestamp for callers that time a block outside the scope helper; pair it with <see cref="ElapsedMs"/>.</summary>
    public static long Ticks() => Stopwatch.GetTimestamp();

    /// <summary>Milliseconds since <paramref name="sinceTicks"/>, taken with <see cref="Ticks"/>.</summary>
    public static double ElapsedMs(long sinceTicks) => (Stopwatch.GetTimestamp() - sinceTicks) * 1000.0 / Stopwatch.Frequency;

    static double Ms(long ticks) => (ticks - origin) * 1000.0 / Stopwatch.Frequency;

    static void CloseScope(string name, long start, int openDepth)
    {
        long now = Stopwatch.GetTimestamp();

        if (depth > 0) depth--;

        if (tracePath != null || traceToConsole) Trace($"scope-end {name} {(now - start) * 1000.0 / Stopwatch.Frequency:0.000}");

        var entry = new Entry("stage", name, Ms(start), Ms(now), openDepth, 0, 0, null);

        lock (sync) items.Add(entry);
    }

    /// <summary>
    /// Closes the first instrumented interval of a blocking stage. <see cref="ScopeToken"/> is a struct so opening a
    /// scope allocates nothing; the only reference is the name, which was already materialised at the call site.
    /// </summary>
    public struct ScopeToken : IDisposable
    {
        string? name;

        readonly long start;

        readonly int depth;

        internal ScopeToken(string name, long start, int depth)
        {
            this.name = name;
            this.start = start;
            this.depth = depth;
        }

        /// <summary>Closes the interval; disposing twice is a no-op.</summary>
        public void Dispose()
        {
            if (name is null) return;

            var closed = name;

            name = null;

            CloseScope(closed, start, depth);
        }
    }

    readonly struct Entry
    {
        public readonly string Kind;

        public readonly string Name;

        public readonly double StartMs;

        public readonly double EndMs;

        public readonly int Depth;

        public readonly long Bytes;

        public readonly double Aux;

        public readonly string? Detail;

        public Entry(string kind, string name, double startMs, double endMs, int depth, long bytes, double aux, string? detail)
        {
            Kind = kind;
            Name = name;
            StartMs = startMs;
            EndMs = endMs;
            Depth = depth;
            Bytes = bytes;
            Aux = aux;
            Detail = detail;
        }

        public double Ms => EndMs - StartMs;
    }

    sealed class Counter
    {
        public int Count;

        public double Ms;

        public long Bytes;
    }

    static void Write(string reason, double elapsedMs)
    {
        reported = true;

        Trace($"write reason={reason} elapsed={elapsedMs:0.0}");

        Entry[] snapshot;

        KeyValuePair<string, Counter>[] counterSnapshot;

        lock (sync)
        {
            snapshot = items.ToArray();

            counterSnapshot = counters.ToArray();
        }

        Array.Sort(snapshot, static (a, b) => a.StartMs.CompareTo(b.StartMs));

        double firstFrameMs = double.NaN;

        double lastLoadMs = double.NaN;

        int loadCount = 0;

        int loadFailures = 0;

        var waits = new List<double>();

        var durations = new List<double>();

        for (int i = 0; i < snapshot.Length; i++)
        {
            var entry = snapshot[i];

            if (entry.Kind == "frame" && entry.Name == "first-frame") firstFrameMs = entry.EndMs;

            if (entry.Kind is not ("load" or "load-failed" or "font")) continue;

            if (entry.Kind == "font")
            {
                if (double.IsNaN(lastLoadMs) || entry.EndMs > lastLoadMs) lastLoadMs = entry.EndMs;

                continue;
            }

            loadCount++;

            if (entry.Kind == "load-failed") loadFailures++;

            durations.Add(entry.Ms);

            if (entry.Aux >= 0) waits.Add(entry.Aux);

            if (double.IsNaN(lastLoadMs) || entry.EndMs > lastLoadMs) lastLoadMs = entry.EndMs;
        }

        double processBootMs = ReadProcessBootMs(elapsedMs);

        var report = new StringBuilder(8192);

        report.AppendLine($"===== run {runId} =====");
        report.AppendLine($"platform={platform} reason={reason} quiet={quietMs:0}ms max={maxMs:0}ms");
        report.AppendLine($"process-start -> origin: {processBootMs:0.000} ms");
        report.AppendLine($"origin -> report:        {elapsedMs:0.000} ms");
        report.AppendLine();

        report.AppendLine("milestones (ms since origin)");
        report.AppendLine($"  first-frame         {Format(firstFrameMs)}");
        report.AppendLine($"  last-load-complete  {Format(lastLoadMs)}");
        report.AppendLine($"  quiescent           {elapsedMs:0.000}");
        report.AppendLine();

        report.AppendLine("loads");
        report.AppendLine($"  count={loadCount} failed={loadFailures} " +
            $"wait_median={Format(Median(waits))} wait_p95={Format(Percentile(waits, 0.95))} " +
            $"load_median={Format(Median(durations))} load_p95={Format(Percentile(durations, 0.95))} " +
            $"load_total={Sum(durations):0.000}");

        // The split that answers the actual question: a load that finished before the first frame delayed the white
        // screen, one that finished after it only delayed the scene becoming complete.
        double beforeFirstFrame = 0;
        double afterFirstFrame = 0;

        for (int i = 0; i < snapshot.Length; i++)
        {
            var entry = snapshot[i];

            if (entry.Kind is not ("load" or "load-failed")) continue;

            if (double.IsNaN(firstFrameMs) || entry.EndMs <= firstFrameMs) beforeFirstFrame += entry.Ms;
            else afterFirstFrame += entry.Ms;
        }

        report.AppendLine($"  before-first-frame={beforeFirstFrame:0.000} after-first-frame={afterFirstFrame:0.000} (ms of Load body)");

        // The exclusion has to be visible in the report, otherwise a load list that ends early could read as "the scene
        // became complete" when it really means "the controls that keep requeueing themselves were left out of the bill".
        int churnCount = 0;

        double churnMs = 0;

        for (int i = 0; i < snapshot.Length; i++)
        {
            if (snapshot[i].Kind != "load-churn") continue;

            churnCount++;

            churnMs += snapshot[i].Ms;
        }

        if (churnCount > 0)
            report.AppendLine($"  load-churn={churnCount} ({churnMs:0.000} ms) excluded from last-load-complete and the quiet window");
        report.AppendLine();

        report.AppendLine("counters");
        Array.Sort(counterSnapshot, static (a, b) => string.CompareOrdinal(a.Key, b.Key));

        for (int i = 0; i < counterSnapshot.Length; i++)
        {
            var counter = counterSnapshot[i].Value;

            report.Append("  ").Append(counterSnapshot[i].Key.PadRight(24)).Append(counter.Count.ToString(CultureInfo.InvariantCulture));

            if (counter.Ms > 0) report.Append("  ms=").Append(counter.Ms.ToString("0.000", CultureInfo.InvariantCulture));

            if (counter.Bytes > 0) report.Append("  bytes=").Append(counter.Bytes.ToString(CultureInfo.InvariantCulture));

            report.AppendLine();
        }

        report.AppendLine();
        report.AppendLine("kinds (count / total_ms / median_ms / max_ms)");

        AppendKindSummary(report, snapshot, "stage");
        AppendKindSummary(report, snapshot, "shader");
        AppendKindSummary(report, snapshot, "pso");
        AppendKindSummary(report, snapshot, "effect");
        AppendKindSummary(report, snapshot, "load");
        AppendKindSummary(report, snapshot, "load-failed");
        AppendKindSummary(report, snapshot, "load-churn");
        AppendKindSummary(report, snapshot, "font");

        report.AppendLine();
        report.AppendLine("timeline (start_ms, indented by nesting depth)");

        for (int i = 0; i < snapshot.Length; i++)
        {
            var entry = snapshot[i];

            if (entry.Kind is not ("mark" or "stage" or "frame")) continue;

            report.Append("  ").Append(entry.StartMs.ToString("0.000", CultureInfo.InvariantCulture).PadLeft(10));

            report.Append("  ").Append(new string(' ', Math.Max(0, entry.Depth) * 2));

            report.Append(entry.Kind.PadRight(6)).Append(' ').Append(entry.Name.PadRight(32));

            if (entry.Kind == "stage") report.Append(entry.Ms.ToString("0.000", CultureInfo.InvariantCulture).PadLeft(10)).Append(" ms");

            if (!string.IsNullOrEmpty(entry.Detail)) report.Append("  ").Append(entry.Detail);

            report.AppendLine();
        }

        AppendTop(report, snapshot, "shader", 20);
        AppendTop(report, snapshot, "pso", 20);
        AppendTop(report, snapshot, "effect", 20);
        AppendTop(report, snapshot, "load", 20);

        try
        {
            if (output != null)
            {
                WriteCsv(snapshot);

                output.Append("startup-report.txt", report.ToString());
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[STARTUP] report failed: {ex.GetType().Name}: {ex.Message}");
        }

        Console.Error.WriteLine($"[STARTUP] {reason} first-frame={Format(firstFrameMs)} last-load={Format(lastLoadMs)} " +
            $"quiescent={elapsedMs:0.0}ms loads={loadCount} failed={loadFailures} " +
            $"fxc={FindCounter(counterSnapshot, "shader.compile")} hits={FindCounter(counterSnapshot, "shader.cache-hit")} " +
            $"output={(output == null ? "(none)" : output.Description)}");

        // Contract 3-2: the compute bill on its own line. kernels > cs_fxc means sources were shared; the ms totals are
        // what a deferral or a disk cache would be credited with, so they have to be readable without opening the report.
        Console.Error.WriteLine($"[STARTUP] compute kernels={FindCounter(counterSnapshot, "compute.pso")} " +
            $"cs_fxc={FindCounter(counterSnapshot, "shader.compile.cs")} cs_hits={FindCounter(counterSnapshot, "shader.cache-hit.cs")} " +
            $"cs_fxc_ms={FindCounterMs(counterSnapshot, "shader.compile.cs")} pso_ms={FindCounterMs(counterSnapshot, "compute.pso")} " +
            $"sig_ms={FindCounterMs(counterSnapshot, "compute.signature")}");

        // A browser has no process to exit and a phone would be killed before the log buffer drains; the report is already
        // written by now, so only a desktop host that asked for it honours the switch.
        if (exitWhenDone && !OperatingSystem.IsBrowser()) Environment.Exit(0);
    }

    static void AppendKindSummary(StringBuilder report, Entry[] snapshot, string kind)
    {
        var values = new List<double>();

        double total = 0;

        for (int i = 0; i < snapshot.Length; i++)
        {
            if (snapshot[i].Kind != kind) continue;

            values.Add(snapshot[i].Ms);

            total += snapshot[i].Ms;
        }

        if (values.Count == 0) return;

        report.Append("  ").Append(kind.PadRight(12)).Append(values.Count.ToString(CultureInfo.InvariantCulture).PadLeft(4));
        report.Append("  ").Append(total.ToString("0.000", CultureInfo.InvariantCulture).PadLeft(11));
        report.Append("  ").Append(Median(values).ToString("0.000", CultureInfo.InvariantCulture).PadLeft(10));
        report.Append("  ").Append(values.Max().ToString("0.000", CultureInfo.InvariantCulture).PadLeft(10));
        report.AppendLine();
    }

    static void AppendTop(StringBuilder report, Entry[] snapshot, string kind, int count)
    {
        var matching = new List<Entry>();

        for (int i = 0; i < snapshot.Length; i++)
        {
            if (snapshot[i].Kind == kind) matching.Add(snapshot[i]);
        }

        if (matching.Count == 0) return;

        matching.Sort(static (a, b) => b.Ms.CompareTo(a.Ms));

        report.AppendLine();
        report.AppendLine($"top {Math.Min(count, matching.Count)} of {matching.Count} {kind} (by ms)");

        for (int i = 0; i < matching.Count && i < count; i++)
        {
            var entry = matching[i];

            report.Append("  ").Append(entry.Ms.ToString("0.000", CultureInfo.InvariantCulture).PadLeft(10)).Append(" ms  ");
            report.Append(entry.Name.PadRight(36));

            if (entry.Bytes > 0) report.Append("  ").Append((entry.Bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture)).Append(" KB");

            if (entry.Aux >= 0 && kind == "load") report.Append("  wait=").Append(entry.Aux.ToString("0.000", CultureInfo.InvariantCulture)).Append(" ms");

            if (!string.IsNullOrEmpty(entry.Detail)) report.Append("  ").Append(entry.Detail);

            report.AppendLine();
        }
    }

    static void WriteCsv(Entry[] snapshot)
    {
        const string fileName = "startup-items.csv";

        // The whole table is built in memory and handed to the sink in one block, because a console sink has no file to
        // append to and a directory sink is one open either way. A startup run is a few hundred rows, so the copy is free.
        var text = new StringBuilder(1 << 16);

        if (!output!.Exists(fileName)) text.AppendLine("run,platform,kind,name,start_ms,end_ms,ms,depth,bytes,aux,detail");

        var line = new StringBuilder(256);

        for (int i = 0; i < snapshot.Length; i++)
        {
            var entry = snapshot[i];

            line.Clear();

            AppendField(line, runId);
            AppendField(line, platform);
            AppendField(line, entry.Kind);
            AppendField(line, entry.Name);

            line.Append(',').Append(entry.StartMs.ToString("0.###", CultureInfo.InvariantCulture));
            line.Append(',').Append(entry.EndMs.ToString("0.###", CultureInfo.InvariantCulture));
            line.Append(',').Append(entry.Ms.ToString("0.###", CultureInfo.InvariantCulture));
            line.Append(',').Append(entry.Depth.ToString(CultureInfo.InvariantCulture));
            line.Append(',').Append(entry.Bytes.ToString(CultureInfo.InvariantCulture));
            line.Append(',').Append(entry.Aux.ToString("0.###", CultureInfo.InvariantCulture));

            AppendField(line, entry.Detail ?? string.Empty);

            text.AppendLine(line.ToString());
        }

        output.Append(fileName, text.ToString());
    }

    static void AppendField(StringBuilder line, string value)
    {
        if (line.Length > 0) line.Append(',');

        if (value.IndexOf(',') < 0 && value.IndexOf('"') < 0 && value.IndexOf('\n') < 0)
        {
            line.Append(value);

            return;
        }

        line.Append('"').Append(value.Replace("\"", "\"\"")).Append('"');
    }

    static void Trace(string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} {message}";

        try
        {
            if (traceToConsole)
            {
                Console.Error.WriteLine($"[startup-trace] {line}");

                return;
            }

            if (tracePath == null) return;

            lock (traceLock)
            {
                File.AppendAllText(tracePath, line + Environment.NewLine);
            }
        }
        catch
        {
            // The breadcrumb is a diagnostic aid; failing to write it must not disturb what is being measured.
        }
    }

    static double ReadProcessBootMs(double elapsedMs)
    {
        try
        {
            using var process = Process.GetCurrentProcess();

            double runningMs = (DateTime.Now - process.StartTime).TotalMilliseconds;

            return Math.Max(0, runningMs - elapsedMs);
        }
        catch
        {
            // Not available on every platform (or before the loader exposes it); the bridge is a convenience column,
            // every other number is still correct without it.
            return double.NaN;
        }
    }

    static string Format(double value) => double.IsNaN(value) ? "-" : value.ToString("0.000", CultureInfo.InvariantCulture) + " ms";

    static string FindCounter(KeyValuePair<string, Counter>[] snapshot, string name)
    {
        for (int i = 0; i < snapshot.Length; i++)
        {
            if (string.Equals(snapshot[i].Key, name, StringComparison.Ordinal))
                return snapshot[i].Value.Count.ToString(CultureInfo.InvariantCulture);
        }

        return "0";
    }

    // Contract 3-2: FindCounter answers "how many"; the console line also needs "how much time", in the units the report
    // uses, and "-" when the counter never fired so an absent number cannot be mistaken for a measured zero.
    static string FindCounterMs(KeyValuePair<string, Counter>[] snapshot, string name)
    {
        for (int i = 0; i < snapshot.Length; i++)
        {
            if (string.Equals(snapshot[i].Key, name, StringComparison.Ordinal))
                return snapshot[i].Value.Ms.ToString("0.0", CultureInfo.InvariantCulture);
        }

        return "-";
    }

    static double Sum(List<double> values)
    {
        double total = 0;

        for (int i = 0; i < values.Count; i++) total += values[i];

        return total;
    }

    static double Median(List<double> values)
    {
        if (values.Count == 0) return double.NaN;

        values.Sort();

        return values[values.Count / 2];
    }

    static double Percentile(List<double> values, double percentile)
    {
        if (values.Count == 0) return double.NaN;

        values.Sort();

        int index = (int)Math.Round(percentile * (values.Count - 1), MidpointRounding.AwayFromZero);

        return values[Math.Clamp(index, 0, values.Count - 1)];
    }
}
