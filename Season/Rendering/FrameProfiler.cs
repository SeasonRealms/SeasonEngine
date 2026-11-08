// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>Contract 3-1: which kind of work a measured interval belongs to.</summary>
public enum ProfileIntervalKind : byte
{
    /// <summary>One <c>BeginPass</c>/<c>EndPass</c> pair, i.e. one <see cref="RenderPassId"/> range.</summary>
    Pass = 0,

    /// <summary>One <see cref="ComputeEffect.Record"/> call, i.e. one compute effect outside any render pass.</summary>
    Compute = 1,
}

/// <summary>
/// Contract 3-1: one interval of one frame exactly as it was measured, handed to a <see cref="FrameProfileSink"/>.
/// </summary>
public readonly struct IntervalSample
{
    /// <summary>Pass name from the fixed table, or <see cref="ComputeEffect.Name"/>.</summary>
    public readonly string Name;

    /// <summary>Which family the interval belongs to.</summary>
    public readonly ProfileIntervalKind Kind;

    /// <summary>Index of the view that produced the interval; 0 is the main view.</summary>
    public readonly int View;

    /// <summary>
    /// GPU milliseconds, or <see cref="double.NaN"/> when the backend could not measure this interval. NaN is not zero: a
    /// pass that was not timed must print "--" rather than claim it was free.
    /// </summary>
    public readonly double GpuMs;

    /// <summary>CPU milliseconds spent recording this interval's commands; not the time the GPU spent running them.</summary>
    public readonly double CpuMs;

    /// <summary>Captures one measurement. Called by the profiler itself; consumers only read.</summary>
    public IntervalSample(string name, ProfileIntervalKind kind, int view, double gpuMs, double cpuMs)
    {
        Name = name;
        Kind = kind;
        View = view;
        GpuMs = gpuMs;
        CpuMs = cpuMs;
    }

    /// <summary>False when this interval's GPU number is unknown and must be printed as "--".</summary>
    public bool GpuKnown => !double.IsNaN(GpuMs);
}

/// <summary>
/// Contract 3-1: one interval aggregated over the rolling statistics window. The HUD and the CSV read the same numbers
/// through <see cref="FrameProfiler.Intervals"/> and <see cref="FrameProfiler.Summary"/>, so there is only one definition
/// of "what this pass costs" in the engine.
/// </summary>
public readonly struct ProfileInterval
{
    /// <summary>Pass name from the fixed table, or <see cref="ComputeEffect.Name"/>.</summary>
    public readonly string Name;

    /// <summary>Which family the interval belongs to.</summary>
    public readonly ProfileIntervalKind Kind;

    /// <summary>Index of the view that produced the interval; 0 is the main view.</summary>
    public readonly int View;

    /// <summary>Median GPU milliseconds over the window, or NaN when the backend has no timestamp support.</summary>
    public readonly double GpuMedianMs;

    /// <summary>95th-percentile GPU milliseconds. Spikes live here: a stable median with a wide p95 is a hitching pass.</summary>
    public readonly double GpuP95Ms;

    /// <summary>Median CPU milliseconds spent recording this interval.</summary>
    public readonly double CpuMedianMs;

    /// <summary>How many frames this interval was seen in inside the window; fewer than the window means it is intermittent.</summary>
    public readonly int Samples;

    /// <summary>Joins one slot's aggregates. Called by the profiler's rolling statistics; consumers only read.</summary>
    public ProfileInterval(string name, ProfileIntervalKind kind, int view,
        double gpuMedianMs, double gpuP95Ms, double cpuMedianMs, int samples)
    {
        Name = name;
        Kind = kind;
        View = view;
        GpuMedianMs = gpuMedianMs;
        GpuP95Ms = gpuP95Ms;
        CpuMedianMs = cpuMedianMs;
        Samples = samples;
    }

    /// <summary>False when this interval's GPU column is unknown and must be printed as "--".</summary>
    public bool GpuKnown => !double.IsNaN(GpuMedianMs);
}

/// <summary>
/// Contract 3-1: the frame-level half of the picture. Times are medians over the rolling window (an average lets a single
/// GC or driver hitch rewrite the number), while the collection counts are the last published frame's deltas.
/// </summary>
public readonly struct FrameProfileSummary
{
    /// <summary>Median milliseconds between consecutive frame starts: what the engine actually presents.</summary>
    public double FrameMs { get; init; }

    /// <summary>Median milliseconds spent inside <see cref="FrameSchedule.Execute"/>, i.e. the CPU cost of building the frame.</summary>
    public double ExecuteMs { get; init; }

    /// <summary>Median GPU milliseconds of the whole frame, or NaN when no backend timer is available.</summary>
    public double GpuTotalMs { get; init; }

    /// <summary>Median bytes allocated on the render thread per frame.</summary>
    public long AllocBytes { get; init; }

    /// <summary>Gen0 collections that happened during the last published frame.</summary>
    public int Gen0 { get; init; }

    /// <summary>Gen1 collections that happened during the last published frame.</summary>
    public int Gen1 { get; init; }

    /// <summary>Gen2 collections that happened during the last published frame.</summary>
    public int Gen2 { get; init; }

    /// <summary>Frames folded into the window so far.</summary>
    public int PublishedFrames { get; init; }

    /// <summary>Frames thrown away because a Begin/End pair did not balance. Non-zero means the instrumentation is wrong, not the GPU.</summary>
    public int DroppedFrames { get; init; }

    /// <summary>Intervals of the last published frame that could not be fully measured (mark table or slot table full).</summary>
    public int DroppedIntervals { get; init; }

    /// <summary>False when no backend timer is attached, in which case the GPU column reads "--" rather than zero.</summary>
    public bool GpuKnown => !double.IsNaN(GpuTotalMs);
}

/// <summary>
/// Contract 3-1: one finished frame, as delivered to <see cref="FrameProfileSink"/>. It is a <c>ref struct</c> on purpose:
/// the interval span points straight into a reusable buffer, so publishing a frame allocates nothing and the profiler does
/// not pollute the very number it reports.
/// </summary>
public readonly ref struct CompletedFrame
{
    /// <summary>Monotonic frame number; the same token handed to the backend timer.</summary>
    public readonly long Token;

    /// <summary>Milliseconds between this frame's start and the previous frame's start.</summary>
    public readonly double FrameMs;

    /// <summary>Milliseconds spent inside <c>FrameSchedule.Execute</c>.</summary>
    public readonly double ExecuteMs;

    /// <summary>GPU milliseconds of the whole frame, or NaN when unknown.</summary>
    public readonly double GpuTotalMs;

    /// <summary>Bytes allocated on the render thread during this frame.</summary>
    public readonly long AllocBytes;

    /// <summary>Gen0 collections during this frame.</summary>
    public readonly int Gen0;

    /// <summary>Gen1 collections during this frame.</summary>
    public readonly int Gen1;

    /// <summary>Gen2 collections during this frame.</summary>
    public readonly int Gen2;

    /// <summary>Intervals this frame could not fully measure; non-zero means the table below has holes.</summary>
    public readonly int DroppedIntervals;

    /// <summary>The frame's intervals in recording order; valid only for the duration of the sink call.</summary>
    public readonly ReadOnlySpan<IntervalSample> Intervals;

    internal CompletedFrame(long token, double frameMs, double executeMs, double gpuTotalMs, long allocBytes,
        int gen0, int gen1, int gen2, int droppedIntervals, ReadOnlySpan<IntervalSample> intervals)
    {
        Token = token;
        FrameMs = frameMs;
        ExecuteMs = executeMs;
        GpuTotalMs = gpuTotalMs;
        AllocBytes = allocBytes;
        Gen0 = gen0;
        Gen1 = gen1;
        Gen2 = gen2;
        DroppedIntervals = droppedIntervals;
        Intervals = intervals;
    }

    /// <summary>False when this frame's GPU column is unknown and must be written out as "--".</summary>
    public bool GpuKnown => !double.IsNaN(GpuTotalMs);
}

/// <summary>Contract 3-1: receives every frame the profiler finishes publishing. Called on the render thread.</summary>
public delegate void FrameProfileSink(in CompletedFrame frame);

/// <summary>
/// Contract 3-1: per-pass and per-compute cost, measured once for all backends.
///
/// <para><b>Why here.</b> The engine already funnels every frame through <see cref="FrameSchedule.Execute"/>, every pass
/// through that method's <c>g.BeginPass</c>/<c>g.EndPass</c>, and every compute effect through
/// <see cref="FrameSchedule"/>'s single <c>effect.Record(g)</c> call. Those three single points are the only instrumentation sites the
/// profiler needs, so four backends do not grow four timing implementations - exactly the split
/// <see cref="GraphicsStatistics"/> uses for the frame rate.</para>
///
/// <para><b>Interval = a pair of marks.</b> <c>Begin*</c> writes a GPU timestamp and remembers its slot,
/// <c>End*</c> writes a second one; the interval is <c>ticks[end] - ticks[begin]</c>. The label table travels with the
/// frame token, so a readback that lands one to three frames later still matches names to numbers. Marks are recorded in
/// stream order on the queue, which is why no nesting is needed: passes and compute never interleave by construction.</para>
///
/// <para><b>Two columns, two questions.</b> The GPU column answers "is the GPU the wall" and the CPU column answers "is the
/// driver the wall". They are measured separately because a pass can be cheap on one and expensive on the other, and the
/// fix is different in each case.</para>
///
/// <para><b>Never on the critical path.</b> The GPU half is read back through a fence-gated ring: if a slot is still in
/// flight, its frame is published with an unknown GPU column rather than waited for. A profiler that blocks measures
/// itself - the opposite trade-off from <c>DXCaptureRing</c>, which may stall because a recording must not lose frames.</para>
///
/// <para><b>Off costs nothing.</b> <see cref="Enabled"/> is a <c>static readonly bool</c> read; every entry point returns
/// on the first line when it is false, no query heap is created, and no array is allocated.</para>
///
/// <para><b>Threading.</b> Every member is called on the render thread only (Execute records, the HUD reads inside the
/// same Execute). There is no locking anywhere in this type.</para>
/// </summary>
public static class FrameProfiler
{
    /// <summary>Upper bound of intervals a single frame can report.</summary>
    public const int MaxIntervals = 250;

    /// <summary>Step used by <see cref="FixedDelta"/> when <see cref="PinDelta"/> is on: 60 simulated frames per second.</summary>
    public const float PinnedDelta = 1f / 60f;

    /// <summary>Distinct (kind, view, name) triples the slot table holds. 34 exist today; the rest is room for growth.</summary>
    const int MaxSlots = 96;

    /// <summary>
    /// Frames whose label tables are kept alive. It must exceed the backend readback ring (four slots) so a token can
    /// never be handed out again while its numbers are still on their way back.
    /// </summary>
    const int RingFrames = 8;

    /// <summary>Frames in the rolling statistics window. 96 frames is about 1.6 s at 60 Hz.</summary>
    const int StatsWindow = 96;

    /// <summary>
    /// Frames between two statistics recomputations. Sorting 96 windows every frame would show up in the very number it
    /// produces; every 15th frame is four updates a second, which is faster than the eye reads the HUD.
    /// </summary>
    const int StatsPublishInterval = 15;

    /// <summary>Depth of the "forgot to End" guard. Passes and compute never nest, so anything above one is a bug.</summary>
    const int MaxOpenDepth = 8;

    /// <summary>Fixed pass names indexed by <see cref="RenderPassId"/>. Avoids the per-frame <c>enum.ToString()</c> allocation.</summary>
    static readonly string[] PassNames =
    {
        "Shadow", "Scene", "Post", "OutlineMask", "FinalBlit", "Overlay",
        "SceneOpaque", "FogComposite", "SceneTransparent", "WeatherComposite",
        "SceneDecal", "SceneWater", "SsrComposite", "SceneWaterfall", "ShadowFar",
        "ShadowLocal",
    };

    // Small fixed tables, allocated once with the type. They are read on every Begin even when the profiler is off, so
    // they deliberately hold no state that would have to be cleared between runs.
    static readonly PendingInterval[] open = new PendingInterval[MaxOpenDepth];
    static readonly int[] topOrder = new int[8];

    /// <summary>
    /// True when <c>SEASON_PROFILE</c> or <c>SEASON_PROFILE_OUT</c> is set, or when the host pushed one of them through
    /// <see cref="Season.Basic.ProfileSwitch"/>. Read on every instrumentation site as the first line. The switch table is
    /// consulted rather than the environment directly because a browser has no environment to set - it arrives through
    /// the same host channel StartupProfiler uses.
    /// </summary>
    public static readonly bool Enabled = ReadEnabled();

    static bool attached;

    static bool allocUnavailable;

    static IGpuFrameTimer? timer;

    static Buffers? buffers;

    static long frameToken;

    static long frameStart;

    static long lastFrameStart;

    static long allocStart;

    static int gen0Start;

    static int gen1Start;

    static int gen2Start;

    static bool frameValid;

    static int openDepth;

    static int intervalCount;

    static int droppedIntervals;

    static int publishedFrames;

    static int droppedFrames;

    static int framesSincePublish;

    static int version;

    static int publishedCount;

    static int frameWindowPos;

    static int frameWindowCount;

    static int lastGen0;

    static int lastGen1;

    static int lastGen2;

    static int lastDroppedIntervals;

    static FrameRing? currentRing;

    /// <summary>Receives every published frame. Set by the cost runner; null means nobody is listening.</summary>
    public static FrameProfileSink? Sink { get; set; }

    /// <summary>
    /// When set, <see cref="FixedDelta"/> replaces the real frame delta with <see cref="PinnedDelta"/> so that a scenario's
    /// t is a pure function of its frame number. Left off in normal runs, where the real delta passes through untouched.
    /// </summary>
    public static bool PinDelta { get; set; }

    /// <summary>Whether a backend timer is attached and healthy. False until a backend implements <see cref="IGraphics.CreateGpuFrameTimer"/>.</summary>
    public static bool GpuTimingAvailable => timer is { Available: true };

    /// <summary>
    /// The attached backend timer, for the backends whose marks cannot be written when they are taken: neither Metal nor
    /// WebGPU can record a timestamp outside a pass, so their timers hold the marks and the encoder-opening sites pick
    /// them up. Null on the other two, where a mark is a command that stands on its own.
    /// </summary>
    internal static IGpuFrameTimer? AttachedTimer => timer;

    /// <summary>Increments every time <see cref="Intervals"/> and <see cref="Summary"/> are recomputed; lets the HUD cache its string.</summary>
    public static int Version => version;

    /// <summary>
    /// Token of the frame that most recently opened. The cost runner records its measurement windows as token ranges,
    /// because the token is the only frame number that travels all the way to <see cref="CompletedFrame"/> - a window
    /// expressed in app frames would have to assume the loop calls <c>Execute</c> exactly once per update, which is a
    /// property of the host and not of the profiler.
    /// </summary>
    public static long CurrentToken => frameToken;

    /// <summary>Latest aggregated numbers. Safe to read at any time; empty until the first window recomputation.</summary>
    public static FrameProfileSummary Summary => new()
    {
        FrameMs = Median(buffers?.FrameWindow, frameWindowCount),
        ExecuteMs = Median(buffers?.ExecWindow, frameWindowCount),
        GpuTotalMs = Median(buffers?.GpuTotalWindow, frameWindowCount),
        AllocBytes = (long)Median(buffers?.AllocWindow, frameWindowCount),
        Gen0 = lastGen0,
        Gen1 = lastGen1,
        Gen2 = lastGen2,
        PublishedFrames = publishedFrames,
        DroppedFrames = droppedFrames,
        DroppedIntervals = lastDroppedIntervals,
    };

    /// <summary>Per-interval aggregates, ordered by first appearance. Empty until the first window recomputation.</summary>
    public static ReadOnlySpan<ProfileInterval> Intervals
    {
        get
        {
            var b = buffers;

            return b == null ? default : b.Published.AsSpan(0, publishedCount);
        }
    }

    /// <summary>
    /// Contract 3-1: the one shared fixed-step switch. Returns <paramref name="time"/> unchanged unless a scenario runner
    /// asked for a pinned step, so ordinary frames keep their real delta and measurement frames become reproducible.
    /// </summary>
    public static float FixedDelta(float time) => PinDelta ? PinnedDelta : time;

    /// <summary>
    /// Opens a frame: attaches the backend timer on first use, publishes whatever the GPU has finished since last time,
    /// and writes the frame's first mark. Called by <see cref="FrameSchedule.Execute"/> before anything else.
    /// </summary>
    public static void BeginFrame(IGraphics g)
    {
        if (!Enabled) return;

        if (!attached) Attach(g);

        Drain();

        long now = Stopwatch.GetTimestamp();

        frameStart = now;
        allocStart = ReadAllocated();
        gen0Start = GC.CollectionCount(0);
        gen1Start = GC.CollectionCount(1);
        gen2Start = GC.CollectionCount(2);
        frameToken++;
        frameValid = true;
        openDepth = 0;
        intervalCount = 0;
        droppedIntervals = 0;

        var ring = currentRing = ReserveRing();

        ring.Token = frameToken;
        ring.Count = 0;
        ring.InFlight = false;
        ring.Age = 0;

        // The backend must learn the token before the first mark: the numbers come back keyed by it, and a timer that
        // never had its cursor reset would either hand out tokens it invented or run out of heap slots after one frame.
        if (timer != null) timer.BeginFrame(frameToken);

        ring.FirstMark = timer != null ? timer.Mark() : -1;
        ring.LastMark = -1;
    }

    /// <summary>
    /// Closes the frame: writes the trailing mark, records the frame-level numbers and hands the label table to the
    /// pending set. Called by <see cref="FrameSchedule.Execute"/> in a <c>finally</c>, so a throwing frame still closes.
    /// </summary>
    public static void EndFrame()
    {
        if (!Enabled) return;

        var ring = currentRing;

        if (ring == null) return;

        long now = Stopwatch.GetTimestamp();

        // An unbalanced Begin/End pair means the pairing - and therefore every number derived from it - is wrong, so the
        // whole frame is dropped and counted instead of published.
        if (openDepth != 0)
        {
            openDepth = 0;
            frameValid = false;
        }

        if (frameValid && timer != null) ring.LastMark = timer.Mark();

        // The timer's cursor is only meaningful inside a frame that reached EndFrame; a dropped frame arms nothing, which
        // is exactly what the "no resolve recorded" path on the backend expects.
        if (timer != null && frameValid) timer.EndFrame();

        if (!frameValid)
        {
            droppedFrames++;
            lastFrameStart = frameStart;
            return;
        }

        ring.FrameMs = lastFrameStart == 0 ? 0 : Stopwatch.GetElapsedTime(lastFrameStart, frameStart).TotalMilliseconds;
        ring.ExecuteMs = Stopwatch.GetElapsedTime(frameStart, now).TotalMilliseconds;
        ring.AllocBytes = ReadAllocated() - allocStart;
        ring.Gen0 = GC.CollectionCount(0) - gen0Start;
        ring.Gen1 = GC.CollectionCount(1) - gen1Start;
        ring.Gen2 = GC.CollectionCount(2) - gen2Start;
        ring.DroppedIntervals = droppedIntervals;

        lastFrameStart = frameStart;

        if (timer != null)
        {
            ring.InFlight = true;
            ring.Age = 0;
        }
        else
        {
            Publish(ring, default);
        }
    }

    /// <summary>
    /// Called by the backend right after it submits the frame's command lists, so the timer can signal its dedicated fence.
    /// </summary>
    public static void OnSubmitted()
    {
        if (!Enabled) return;

        timer?.OnSubmitted();
    }

    /// <summary>Opens a pass interval. <paramref name="view"/> is <see cref="ViewTargets.Index"/>; 0 is the main view.</summary>
    public static void BeginPass(RenderPassId id, int view)
    {
        if (!Enabled) return;

        int index = (int)id;

        BeginInterval(ProfileIntervalKind.Pass, view,
            index >= 0 && index < PassNames.Length ? PassNames[index] : "Unknown");
    }

    /// <summary>Closes the pass interval opened by <see cref="BeginPass"/>.</summary>
    public static void EndPass()
    {
        if (!Enabled) return;

        EndInterval();
    }

    /// <summary>
    /// Opens a compute interval. The name is the effect's own <see cref="ComputeEffect.Name"/>, and the view comes from the
    /// targets that are active right now - a view-scope effect records inside its view, an app-scope one on the view that
    /// closes the frame, and both are what the pass column of the same frame reports.
    /// </summary>
    public static void BeginCompute(string name)
    {
        if (!Enabled) return;

        BeginInterval(ProfileIntervalKind.Compute, FrameSchedule.ActiveTargets.Index, name);
    }

    /// <summary>Closes the compute interval opened by <see cref="BeginCompute"/>.</summary>
    public static void EndCompute()
    {
        if (!Enabled) return;

        EndInterval();
    }

    /// <summary>Drops every aggregated number and starts a new measurement window. Used between cost scenarios.</summary>
    public static void ResetStatistics()
    {
        if (!Enabled) return;

        var b = buffers;

        if (b == null) return;

        Array.Clear(b.GpuWindow);
        Array.Clear(b.CpuWindow);
        Array.Clear(b.WindowCount);
        Array.Clear(b.WindowPos);
        Array.Clear(b.FrameWindow);
        Array.Clear(b.ExecWindow);
        Array.Clear(b.GpuTotalWindow);
        Array.Clear(b.AllocWindow);

        // SlotCount is deliberately not cleared. A slot index is baked into every frame still in the readback ring, and
        // those frames are published into this window one to three frames from now; renumbering the table here would let
        // a late frame carry another interval's name. Keeping the numbering also keeps FindSlot's linear scan short,
        // because the table only ever grows to the number of distinct (kind, view, name) triples that exist at all.

        publishedFrames = 0;
        droppedFrames = 0;
        lastDroppedIntervals = 0;
        framesSincePublish = 0;
        publishedCount = 0;
        frameWindowPos = 0;
        frameWindowCount = 0;
        version++;
    }

    /// <summary>
    /// Contract 3-1 HUD line: frame totals first, then the most expensive intervals. Names are truncated because a HUD line
    /// is read at a glance; the CSV keeps the full names. An unavailable GPU column prints <c>--</c> and falls back to
    /// ranking by CPU cost, which is still a real measurement.
    /// </summary>
    public static string ToCompactString(int top = 5)
    {
        if (!Enabled) return string.Empty;

        var b = buffers;

        if (b == null || publishedCount == 0) return string.Empty;

        var summary = Summary;
        var sb = new StringBuilder(160);

        sb.Append("GPU ");
        if (summary.GpuKnown) AppendMs(sb, summary.GpuTotalMs);
        else sb.Append("--");

        sb.Append("  CPU ");
        AppendMs(sb, summary.ExecuteMs);

        sb.Append("  ").Append(FormatAllocation(summary.AllocBytes)).Append("/f");

        if (summary.DroppedFrames > 0) sb.Append("  drop ").Append(summary.DroppedFrames);

        AppendTop(sb, top, summary.GpuKnown);

        return sb.ToString();
    }

    static void AppendTop(StringBuilder sb, int top, bool byGpu)
    {
        var b = buffers!;

        int limit = top < topOrder.Length ? top : topOrder.Length;

        if (limit > publishedCount) limit = publishedCount;

        for (int i = 0; i < limit; i++) topOrder[i] = -1;

        for (int i = 0; i < publishedCount; i++)
        {
            double value = byGpu ? b.Published[i].GpuMedianMs : b.Published[i].CpuMedianMs;

            if (double.IsNaN(value) || value <= 0) continue;

            int at = limit;

            for (int j = 0; j < limit; j++)
            {
                if (topOrder[j] < 0) continue;

                double other = byGpu ? b.Published[topOrder[j]].GpuMedianMs : b.Published[topOrder[j]].CpuMedianMs;

                if (value <= other) continue;

                at = j;

                break;
            }

            if (at >= limit) continue;

            for (int j = limit - 1; j > at; j--) topOrder[j] = topOrder[j - 1];

            topOrder[at] = i;
        }

        for (int i = 0; i < limit; i++)
        {
            int index = topOrder[i];

            if (index < 0) continue;

            ref readonly var interval = ref b.Published[index];

            sb.Append("  ").Append(Shorten(interval.Name)).Append(' ');
            AppendMs(sb, byGpu ? interval.GpuMedianMs : interval.CpuMedianMs);
        }
    }

    static string Shorten(string name) => name.Length <= 9 ? name : name[..9];

    static void AppendMs(StringBuilder sb, double milliseconds)
        => sb.Append(milliseconds.ToString("0.0", CultureInfo.InvariantCulture)).Append("ms");

    static string FormatAllocation(long bytes)
    {
        if (bytes <= 0) return "0K";

        if (bytes < 1L << 20) return ((bytes + 512) / 1024).ToString(CultureInfo.InvariantCulture) + "K";

        return (bytes / (double)(1L << 20)).ToString("0.0", CultureInfo.InvariantCulture) + "M";
    }

    static void BeginInterval(ProfileIntervalKind kind, int view, string name)
    {
        long start = Stopwatch.GetTimestamp();

        // Passes and compute cannot interleave, so a second Begin before the matching End is a missing End somewhere. The
        // frame is marked invalid now and dropped at EndFrame, which keeps a broken instrumentation site from publishing a plausible table.
        if (openDepth != 0) frameValid = false;

        if (openDepth >= MaxOpenDepth)
        {
            droppedIntervals++;
            frameValid = false;

            return;
        }

        ref var pending = ref open[openDepth++];

        pending.Slot = -1;
        pending.BeginMark = -1;
        pending.CpuStart = start;

        if (intervalCount >= MaxIntervals)
        {
            droppedIntervals++;

            return;
        }

        int slot = FindSlot(kind, view, name);

        if (slot < 0)
        {
            droppedIntervals++;

            return;
        }

        if (timer != null)
        {
            pending.BeginMark = timer.Mark();

            if (pending.BeginMark < 0) droppedIntervals++;
        }

        pending.Slot = slot;
    }

    static void EndInterval()
    {
        long end = Stopwatch.GetTimestamp();

        if (openDepth == 0)
        {
            // An End without a Begin means the pairing is broken; nothing sensible can be derived from this frame.
            droppedIntervals++;
            frameValid = false;

            return;
        }

        ref var pending = ref open[--openDepth];

        if (pending.Slot < 0) return;

        var ring = currentRing;

        if (ring == null || ring.Count >= MaxIntervals) return;

        ref var raw = ref ring.Intervals[ring.Count++];

        raw.Slot = pending.Slot;
        raw.BeginMark = pending.BeginMark;
        raw.EndMark = timer != null ? timer.Mark() : -1;
        raw.CpuMs = Stopwatch.GetElapsedTime(pending.CpuStart, end).TotalMilliseconds;

        if (timer != null && raw.EndMark < 0) droppedIntervals++;

        intervalCount++;
    }

    static int FindSlot(ProfileIntervalKind kind, int view, string name)
    {
        var b = buffers!;

        for (int i = 0; i < b.SlotCount; i++)
        {
            if (b.SlotKind[i] != kind || b.SlotView[i] != view) continue;

            if (!string.Equals(b.SlotName[i], name, StringComparison.Ordinal)) continue;

            return i;
        }

        if (b.SlotCount >= MaxSlots) return -1;

        int slot = b.SlotCount++;

        b.SlotKind[slot] = kind;
        b.SlotView[slot] = view;
        b.SlotName[slot] = name;
        b.WindowCount[slot] = 0;
        b.WindowPos[slot] = 0;

        return slot;
    }

    static void Attach(IGraphics g)
    {
        attached = true;

        var b = buffers = new Buffers();

        IGpuFrameTimer? created = null;

        try
        {
            created = g.CreateGpuFrameTimer();
        }
        catch
        {
            // A backend that cannot even build its query heap must not take the frame down with it: the CPU and allocation
            // columns keep working and the GPU column reports "--", which is the same state as a platform without support.
            created = null;
        }

        if (created != null && created.Available) timer = created;
        else created?.Dispose();

        if (timer != null) b.NanosecondsPerTick = timer.NanosecondsPerTick;
    }

    static FrameRing ReserveRing()
    {
        var rings = buffers!.Rings;

        int oldest = 0;

        for (int i = 0; i < RingFrames; i++)
        {
            if (!rings[i].InFlight) return rings[i];

            if (rings[i].Age > rings[oldest].Age) oldest = i;
        }

        // Every slot is still in flight, which only happens when the backend stopped signalling. Give up on the oldest
        // one instead of waiting: the profiler is not allowed to become the reason a frame is late.
        Publish(rings[oldest], default);

        return rings[oldest];
    }

    static FrameRing? FindInFlight(long token)
    {
        var rings = buffers!.Rings;

        for (int i = 0; i < RingFrames; i++)
        {
            if (rings[i].InFlight && rings[i].Token == token) return rings[i];
        }

        return null;
    }

    static void Drain()
    {
        var b = buffers;

        if (b == null || timer == null) return;

        while (timer.TryTake(out long token, out ReadOnlySpan<ulong> ticks))
        {
            var ring = FindInFlight(token);

            if (ring == null) continue;

            Publish(ring, ticks);
        }

        for (int i = 0; i < RingFrames; i++)
        {
            var ring = b.Rings[i];

            if (!ring.InFlight) continue;

            if (++ring.Age <= RingFrames) continue;

            // The readback never came back (a dropped slot, a device reset, a stalled submission). Publish what the CPU
            // measured and leave the GPU column unknown rather than hold the label table hostage.
            Publish(ring, default);
        }
    }

    static void Publish(FrameRing ring, ReadOnlySpan<ulong> ticks)
    {
        var b = buffers!;

        ring.InFlight = false;

        double nanosecondsPerTick = ticks.Length > 0 ? b.NanosecondsPerTick : 0.0;
        double gpuTotal = double.NaN;

        if (ticks.Length > 0 && IsMeasurable(ring.FirstMark, ring.LastMark, ticks, nanosecondsPerTick))
            gpuTotal = (ticks[ring.LastMark] - ticks[ring.FirstMark]) * nanosecondsPerTick / 1e6;

        int count = ring.Count;

        for (int i = 0; i < count; i++)
        {
            ref var raw = ref ring.Intervals[i];

            double gpu = double.NaN;

            if (ticks.Length > 0 && IsMeasurable(raw.BeginMark, raw.EndMark, ticks, nanosecondsPerTick))
                gpu = (ticks[raw.EndMark] - ticks[raw.BeginMark]) * nanosecondsPerTick / 1e6;

            b.Samples[i] = new IntervalSample(b.SlotName[raw.Slot], b.SlotKind[raw.Slot], b.SlotView[raw.Slot],
                gpu, raw.CpuMs);

            AccumulateSlot(raw.Slot, gpu, raw.CpuMs);
        }

        if (count > 0) AccumulateFrame(ring, gpuTotal);

        lastGen0 = ring.Gen0;
        lastGen1 = ring.Gen1;
        lastGen2 = ring.Gen2;
        lastDroppedIntervals = ring.DroppedIntervals;
        publishedFrames++;

        if (++framesSincePublish >= StatsPublishInterval) Recompute();

        Sink?.Invoke(new CompletedFrame(ring.Token, ring.FrameMs, ring.ExecuteMs, gpuTotal, ring.AllocBytes,
            ring.Gen0, ring.Gen1, ring.Gen2, ring.DroppedIntervals, b.Samples.AsSpan(0, count)));
    }

    /// <summary>
    /// A mark pair is measurable only when both slots are inside the span the backend returned and the pair is monotonic.
    /// A backwards pair means a wrap or a reused slot, and reporting it would be worse than reporting nothing.
    /// </summary>
    static bool IsMeasurable(int beginMark, int endMark, ReadOnlySpan<ulong> ticks, double nanosecondsPerTick)
    {
        if (nanosecondsPerTick <= 0) return false;

        if (beginMark < 0 || endMark < 0) return false;

        if ((uint)beginMark >= (uint)ticks.Length || (uint)endMark >= (uint)ticks.Length) return false;

        if (ticks[endMark] < ticks[beginMark]) return false;

        return (ticks[endMark] - ticks[beginMark]) * nanosecondsPerTick / 1e6 < 5000.0;
    }

    static void AccumulateSlot(int slot, double gpu, double cpu)
    {
        var b = buffers!;

        int pos = b.WindowPos[slot];
        int index = slot * StatsWindow + pos;

        b.GpuWindow[index] = gpu;
        b.CpuWindow[index] = cpu;

        b.WindowPos[slot] = pos + 1 >= StatsWindow ? 0 : pos + 1;

        if (b.WindowCount[slot] < StatsWindow) b.WindowCount[slot]++;
    }

    static void AccumulateFrame(FrameRing ring, double gpuTotal)
    {
        var b = buffers!;

        b.FrameWindow[frameWindowPos] = ring.FrameMs;
        b.ExecWindow[frameWindowPos] = ring.ExecuteMs;
        b.GpuTotalWindow[frameWindowPos] = gpuTotal;
        b.AllocWindow[frameWindowPos] = ring.AllocBytes;

        frameWindowPos = frameWindowPos + 1 >= StatsWindow ? 0 : frameWindowPos + 1;

        if (frameWindowCount < StatsWindow) frameWindowCount++;
    }

    static void Recompute()
    {
        var b = buffers!;

        framesSincePublish = 0;

        int count = 0;

        for (int slot = 0; slot < b.SlotCount; slot++)
        {
            int window = b.WindowCount[slot];

            ComputeStats(b.GpuWindow, slot * StatsWindow, window, out double gpuMedian, out double gpuP95);
            ComputeStats(b.CpuWindow, slot * StatsWindow, window, out double cpuMedian, out _);

            b.Published[count++] = new ProfileInterval(b.SlotName[slot], b.SlotKind[slot], b.SlotView[slot],
                gpuMedian, gpuP95, cpuMedian, window);
        }

        publishedCount = count;
        version++;
    }

    /// <summary>
    /// Median and 95th percentile in one pass over a NaN-filtered copy. NaN entries are frames whose GPU column was
    /// unknown (expired readback, no timer), and folding them in as zero would make the pass look a little cheaper
    /// every time a readback was lost.
    /// </summary>
    static void ComputeStats(double[] window, int offset, int count, out double median, out double p95)
    {
        var scratch = buffers!.Scratch;

        int n = 0;

        for (int i = 0; i < count; i++)
        {
            double value = window[offset + i];

            if (!double.IsNaN(value)) scratch[n++] = value;
        }

        if (n == 0)
        {
            median = double.NaN;
            p95 = double.NaN;

            return;
        }

        Array.Sort(scratch, 0, n);

        median = scratch[n >> 1];

        int index = (int)Math.Ceiling(0.95 * (n - 1));

        p95 = scratch[index < 0 ? 0 : index >= n ? n - 1 : index];
    }

    static double Median(double[]? window, int count)
    {
        if (window == null || count <= 0) return double.NaN;

        ComputeStats(window, 0, count, out double median, out _);

        return median;
    }

    static long ReadAllocated()
    {
        if (allocUnavailable) return 0;

        try
        {
            return GC.GetAllocatedBytesForCurrentThread();
        }
        catch
        {
            // The browser sandbox can refuse the query. One probe decides for the rest of the run, the same shape
            // GraphicsStatistics uses for its process handle.
            allocUnavailable = true;

            return 0;
        }
    }

    static bool ReadEnabled()
    {
        if (!string.IsNullOrEmpty(Season.Basic.ProfileSwitch.Text("SEASON_PROFILE_OUT"))) return true;

        return Season.Basic.ProfileSwitch.Flag("SEASON_PROFILE");
    }

    /// <summary>One pending Begin: where its name lives, which mark opened it and when the CPU started recording.</summary>
    struct PendingInterval
    {
        public int Slot;

        public int BeginMark;

        public long CpuStart;
    }

    /// <summary>One recorded interval of one frame, before its GPU ticks are known.</summary>
    struct RawInterval
    {
        public int Slot;

        public int BeginMark;

        public int EndMark;

        public double CpuMs;
    }

    sealed class FrameRing
    {
        public readonly RawInterval[] Intervals = new RawInterval[MaxIntervals];

        public long Token;

        public int Age;

        public bool InFlight;

        public int Count;

        public int DroppedIntervals;

        public double FrameMs;

        public double ExecuteMs;

        public long AllocBytes;

        public int Gen0;

        public int Gen1;

        public int Gen2;

        public int FirstMark;

        public int LastMark;
    }

    /// <summary>Everything the profiler allocates, created on attach so a disabled run allocates nothing at all.</summary>
    sealed class Buffers
    {
        public readonly FrameRing[] Rings = new FrameRing[RingFrames];

        public readonly double[] GpuWindow = new double[MaxSlots * StatsWindow];

        public readonly double[] CpuWindow = new double[MaxSlots * StatsWindow];

        public readonly int[] WindowCount = new int[MaxSlots];

        public readonly int[] WindowPos = new int[MaxSlots];

        public readonly ProfileIntervalKind[] SlotKind = new ProfileIntervalKind[MaxSlots];

        public readonly int[] SlotView = new int[MaxSlots];

        public readonly string[] SlotName = new string[MaxSlots];

        public readonly ProfileInterval[] Published = new ProfileInterval[MaxSlots];

        public readonly IntervalSample[] Samples = new IntervalSample[MaxIntervals];

        public readonly double[] Scratch = new double[StatsWindow];

        public readonly double[] FrameWindow = new double[StatsWindow];

        public readonly double[] ExecWindow = new double[StatsWindow];

        public readonly double[] GpuTotalWindow = new double[StatsWindow];

        public readonly double[] AllocWindow = new double[StatsWindow];

        public double NanosecondsPerTick;

        public int SlotCount;

        public Buffers()
        {
            for (int i = 0; i < RingFrames; i++) Rings[i] = new FrameRing();
        }
    }
}
