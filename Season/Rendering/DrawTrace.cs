// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// S0 baseline: a text trace of the draw sequence, one line per state change and per draw.
///
/// <para><b>Why a trace and not a screenshot.</b> Every step of the drawable-object refactor moves code between
/// layers. A screenshot proves the picture did not change; it does not prove <em>how</em> it got there, and the
/// whole point of the refactor is that the sequence of PSO switches and draws stays identical while the code that
/// produces it changes shape. Two runs whose traces are line-for-line equal have emitted the same states in the
/// same order against the same geometry - that is the invariant every later step has to preserve.</para>
///
/// <para><b>Where it taps.</b> <c>Pipeline.SetPipeline</c>, <c>Pipeline.DrawPrimitive</c> and
/// <c>Pipeline.DrawShadowPrimitive</c> are the three exits every backend routes through, so the trace sees 2D,
/// text, sprites, models, instanced bodies, shadows and the outline mask without touching any of them. Each line
/// carries whether it was emitted during scene <em>collection</em> or during replay/direct submission, because
/// DX, VK and Metal all split the two and both halves matter: collection is the order the group code decided,
/// replay is the order the GPU actually received.</para>
///
/// <para><b>Off by default, and free.</b> <see cref="On"/> is a static readonly bool read from
/// <c>SEASON_DRAW_TRACE</c>; nothing is allocated and no file is opened until the first accepted event. The cost
/// on a normal run is one predictable branch per state change and per draw.</para>
///
/// <para><b>Bounded window.</b> Loading frames and teardown frames differ between runs for reasons that have
/// nothing to do with the refactor, so only frames inside
/// <c>[SEASON_DRAW_TRACE_AT, SEASON_DRAW_TRACE_AT + SEASON_DRAW_TRACE_FRAMES)</c> are recorded. The defaults
/// (900, 1) match the frame the pixel A/B harness in <c>Apps/Engine/AbCapture.cs</c> settles on, so a trace and a
/// screenshot can be taken from the same run.</para>
///
/// <para><b>Threading.</b> Render thread only. <c>Pipeline</c> and <c>PrimitiveGroup</c> are both called from the
/// frame thread, so no locking is needed and none is taken - a lock here would show up in the very frame timings
/// S0 is trying to record.</para>
/// </summary>
internal static class DrawTrace
{
    const string PathVariable = "SEASON_DRAW_TRACE";
    const string AtVariable = "SEASON_DRAW_TRACE_AT";
    const string FramesVariable = "SEASON_DRAW_TRACE_FRAMES";
    const string LimitVariable = "SEASON_DRAW_TRACE_LINES";

    /// <summary>Default first recorded frame. Well past the asynchronous asset landing that makes early frames differ.</summary>
    const int DefaultAt = 900;

    /// <summary>Default number of recorded frames. One frame is already a full signature; more is for bisecting.</summary>
    const int DefaultFrames = 1;

    /// <summary>Hard stop for the file. A frame of text plus a scene of models stays far below this.</summary>
    const int DefaultLineLimit = 2_000_000;

    /// <summary>
    /// Backend-neutral encodings of <c>PipelineMode</c>. The three backends do not share the enum, and a trace that
    /// printed backend-specific names could not be compared across a refactor that unifies the call sites.
    /// </summary>
    internal const int ModeOpaque = 0;
    internal const int ModeFade = 1;
    internal const int ModeTransparent = 2;

    /// <summary>Backend-neutral encodings of the cull variant: DX/Metal both sides, VK bit flags.</summary>
    internal const int CullNone = 0;
    internal const int CullBack = 1;
    internal const int CullFront = 2;

    static readonly string? OutputPath = Environment.GetEnvironmentVariable(PathVariable);

    static readonly long AtFrame = Read(AtVariable, DefaultAt);

    static readonly long FrameSpan = Read(FramesVariable, DefaultFrames);

    static readonly int LineLimit = (int)Read(LimitVariable, DefaultLineLimit);

    /// <summary>Whether a trace was requested. Call sites test this before building any argument.</summary>
    internal static readonly bool On = !string.IsNullOrEmpty(OutputPath);

    static System.IO.StreamWriter? writer;

    static int lines;

    /// <summary>Set once the file is finished, so a later event cannot reopen it and truncate the baseline.</summary>
    static bool closed;

    static long lastFrame = -1;

    static readonly StringBuilder scratch = new(192);

    static long Read(string name, long fallback)
    {
        string? text = Environment.GetEnvironmentVariable(name);

        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) && value >= 0
            ? value : fallback;
    }

    /// <summary>
    /// True when this event belongs in the file. Opening the writer is deferred to the first accepted event so a run
    /// that never reaches the window leaves no empty file behind.
    /// </summary>
    static bool Open()
    {
        if (!On || closed) return false;

        long frame = (long)FrameSchedule.FrameIndex;

        if (frame < AtFrame || frame >= AtFrame + FrameSpan) return false;

        if (writer == null) Begin(frame);

        return writer != null;
    }

    static void Begin(long frame)
    {
        try
        {
            string? directory = System.IO.Path.GetDirectoryName(OutputPath!);

            if (!string.IsNullOrEmpty(directory)) System.IO.Directory.CreateDirectory(directory);

            // UTF-8 without BOM: the comparison script reads these with git's diff and with PowerShell, and a BOM
            // would make the first byte of the first line differ for no reason.
            writer = new System.IO.StreamWriter(OutputPath!, append: false, new UTF8Encoding(false), 1 << 16)
            {
                AutoFlush = false,
            };
            writer.Write("# season-draw-trace v1 at=");
            writer.Write(AtFrame);
            writer.Write(" frames=");
            writer.Write(FrameSpan);
            writer.Write(" openedAt=");
            writer.Write(frame);
            writer.WriteLine();

            AppDomain.CurrentDomain.ProcessExit += static (_, _) => Close();
        }
        catch
        {
            // A trace that cannot be written must not take the process down; it is a measurement, not a feature.
            writer = null;
            closed = true;
        }
    }

    static void Flush()
    {
        writer?.Flush();
    }

    static void Close()
    {
        closed = true;

        var target = writer;

        writer = null;

        if (target == null) return;

        try { target.Flush(); target.Dispose(); } catch { }
    }

    /// <summary>Bookkeeping shared by every emitter: frame banner, line cap, periodic flush.</summary>
    static void Account()
    {
        lines++;

        if (lines >= LineLimit)
        {
            Close();
            return;
        }

        if ((lines & 0xFFF) == 0) Flush();
    }

    static void Banner(long frame)
    {
        lastFrame = frame;

        var w = writer!;

        w.Write('F');
        w.Write('\t');
        w.Write(frame);
        w.WriteLine();
    }

    /// <summary>
    /// One group starting its draw list. <paramref name="phase"/> is <c>main</c>, <c>shadow</c> or <c>outline</c>;
    /// <paramref name="owner"/> is the group's asset name. This line also resets the per-group draw counter, which is
    /// what makes <see cref="Draw"/>'s sequence number line up between runs.
    /// </summary>
    internal static void Group(string backend, string phase, string? owner, int count)
    {
        if (!Open()) return;

        long frame = (long)FrameSchedule.FrameIndex;

        if (frame != lastFrame) Banner(frame);

        var w = writer!;

        w.Write('G');
        w.Write('\t');
        w.Write(backend);
        w.Write('\t');
        w.Write(phase);
        w.Write('\t');
        w.Write(owner ?? string.Empty);
        w.Write('\t');
        w.Write(count);
        w.WriteLine();

        Account();
    }

    /// <summary>A PSO/DSS switch. <paramref name="collecting"/> marks the collect half of a collect/replay backend.</summary>
    internal static void State(string backend, int mode, int cull, bool depthWrite, bool collecting)
    {
        if (!Open()) return;

        var w = writer!;

        w.Write('S');
        w.Write('\t');
        w.Write(backend);
        w.Write('\t');
        w.Write(collecting ? 'c' : '.');
        w.Write('\t');
        w.Write(mode);
        w.Write('\t');
        w.Write(cull);
        w.Write('\t');
        w.Write(depthWrite ? 1 : 0);
        w.WriteLine();

        Account();
    }

    /// <summary>
    /// A cull-mode change on a backend that keeps it outside the PSO (Metal binds it as encoder dynamic state).
    /// Kept separate from <see cref="State"/> so the two never have to be reconciled into one line.
    /// </summary>
    internal static void Cull(string backend, int cull, bool collecting)
    {
        if (!Open()) return;

        var w = writer!;

        w.Write('C');
        w.Write('\t');
        w.Write(backend);
        w.Write('\t');
        w.Write(collecting ? 'c' : '.');
        w.Write('\t');
        w.Write(cull);
        w.WriteLine();

        Account();
    }

    /// <summary>
    /// One primitive draw. <paramref name="kind"/> distinguishes scene, shadow, outline and replay submissions;
    /// <paramref name="part"/> is the material name the part-colour API keys on, which is what makes a diff readable
    /// without a debugger.
    /// <para>
    /// The bound mode is deliberately <em>not</em> repeated here. It is already on the <see cref="State"/> line that
    /// precedes the draw, and on Metal it lives in encoder state that no draw entry point can see; duplicating it
    /// would mean inventing a value on one backend and reconciling two sources for the other two.
    /// </para>
    /// </summary>
    internal static void Draw(string backend, string kind, bool doubleSided, bool aoExempt,
        uint indexCount, uint instanceCount, uint startInstance, bool collecting, string? part)
    {
        if (!Open()) return;

        long frame = (long)FrameSchedule.FrameIndex;

        if (frame != lastFrame) Banner(frame);

        var w = writer!;

        w.Write('D');
        w.Write('\t');
        w.Write(backend);
        w.Write('\t');
        w.Write(kind);
        w.Write('\t');
        w.Write(collecting ? 'c' : '.');
        w.Write('\t');
        w.Write(doubleSided ? 1 : 0);
        w.Write('\t');
        w.Write(aoExempt ? 1 : 0);
        w.Write('\t');
        w.Write(indexCount);
        w.Write('\t');
        w.Write(instanceCount);
        w.Write('\t');
        w.Write(startInstance);
        w.Write('\t');
        w.Write(part ?? string.Empty);
        w.WriteLine();

        Account();
    }

    /// <summary>The file this run wrote, for the run-book's log line. Null when tracing was off.</summary>
    internal static string? DesiredPath => On ? OutputPath : null;
}
