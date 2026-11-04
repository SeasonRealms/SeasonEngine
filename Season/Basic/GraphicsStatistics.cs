// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Basic;

/// <summary>
/// One reading of what a frame costs at runtime: the presented frame rate plus the memory the process holds on the GPU
/// and in RAM (contract 1-9). Read through <see cref="GraphicsStatistics.Current"/>.
/// </summary>
public readonly struct GraphicsStatisticsSnapshot
{
    /// <summary>Smoothed presented frames per second. Zero until the first sampling window closes.</summary>
    public float Fps { get; init; }

    /// <summary>Average milliseconds per presented frame over the same window as <see cref="Fps"/>.</summary>
    public double FrameMilliseconds { get; init; }

    /// <summary>Bytes the process holds on the GPU; meaningful only when <see cref="GpuUsageKnown"/> is true.</summary>
    public long GpuUsedBytes { get; init; }

    /// <summary>GPU ceiling reported by the backend, or 0 when it reports none.</summary>
    public long GpuTotalBytes { get; init; }

    /// <summary>False when the platform exposes no way to measure GPU usage (the HUD prints "--" for it).</summary>
    public bool GpuUsageKnown { get; init; }

    /// <summary>Bytes of CPU memory held by the process (working set, or the managed heap where the OS refuses).</summary>
    public long CpuBytes { get; init; }

    /// <summary>
    /// The one-line HUD form: <c>FPS 60 / GPU 200M / CPU 500M</c>. An unmeasurable GPU prints <c>GPU --</c> rather than a
    /// zero, and sizes switch to one decimal of a gigabyte once they pass 1 GiB so the line never grows without bound.
    /// </summary>
    public string ToCompactString()
    {
        var gpu = GpuUsageKnown ? GraphicsStatistics.FormatBytes(GpuUsedBytes) : "--";

        return $"FPS {MathF.Round(Fps)} / GPU {gpu} / CPU {GraphicsStatistics.FormatBytes(CpuBytes)}";
    }
}

/// <summary>
/// Shared runtime statistics for every backend (contract 1-9): frame rate, GPU memory and CPU memory.
///
/// <para>It is a static class fed by the one place every platform already agrees on - <see cref="Season.Rendering.FrameSchedule.Execute"/> -
/// which is what keeps the four backends from growing four definitions of "a frame". Frame timing therefore measures the
/// period between submitted frames, i.e. what the engine actually presents, not what a backend thinks it presents.</para>
///
/// <para>Cost: one <see cref="Stopwatch"/> read per frame (nanoseconds) and a memory refresh at most twice per second. The
/// GPU half is a backend driver query, which is why it is not on the frame path at all; the CPU half is a working-set read
/// with the <see cref="Process"/> handle cached for the lifetime of the process.</para>
///
/// <para>Threading: every member is written and read on the render thread (Update draws the HUD, Execute closes the frame),
/// so no locking is involved. A platform that ever calls Execute from a second thread would need to revisit this.</para>
/// </summary>
public static class GraphicsStatistics
{
    /// <summary>Length of the FPS sampling window. Half a second keeps the number readable while still reacting to a hitch.</summary>
    const double SampleIntervalSeconds = 0.5;

    /// <summary>Weight of the newest sample. Low enough that a single slow frame does not rewrite the HUD.</summary>
    const float FpsSmoothing = 0.35f;

    const long MiB = 1L << 20;

    const long GiB = 1L << 30;

    // Zero until the first frame arrives. Starting the window on the first frame rather than on the first touch of this
    // class keeps launch time (device creation, asset loading) out of the first reading, which would otherwise report a
    // frame rate the engine never ran at.
    static long windowStart;

    static int windowFrames;

    static float fps;

    static double frameMilliseconds;

    static long gpuUsedBytes;

    static long gpuTotalBytes;

    static bool gpuUsageKnown;

    static long cpuBytes;

    static Process process;

    // Set once when the OS refuses a process query (the browser sandbox does), after which the managed heap is the fallback
    // for the rest of the run rather than an exception per second.
    static bool processUnavailable;

    /// <summary>Latest reading. Never null, never throws, and never blocks on a driver query.</summary>
    public static GraphicsStatisticsSnapshot Current => new()
    {
        Fps = fps,
        FrameMilliseconds = frameMilliseconds,
        GpuUsedBytes = gpuUsedBytes,
        GpuTotalBytes = gpuTotalBytes,
        GpuUsageKnown = gpuUsageKnown,
        CpuBytes = cpuBytes,
    };

    /// <summary>
    /// Called once per submitted frame by <see cref="Season.Rendering.FrameSchedule.Execute"/>. Every platform therefore gets
    /// the same measurement without implementing anything.
    /// </summary>
    public static void OnFrameRendered()
    {
        long now = Stopwatch.GetTimestamp();

        if (windowStart == 0)
            windowStart = now;

        windowFrames++;

        double elapsed = Stopwatch.GetElapsedTime(windowStart, now).TotalSeconds;

        if (elapsed < SampleIntervalSeconds)
            return;

        float instant = (float)(windowFrames / elapsed);

        // The first window establishes the value outright: starting from 60 and easing in would print a frame rate the
        // engine never measured.
        fps = fps <= 0f ? instant : fps + (instant - fps) * FpsSmoothing;

        frameMilliseconds = elapsed * 1000.0 / windowFrames;

        windowFrames = 0;

        windowStart = now;

        RefreshMemory();
    }

    static void RefreshMemory()
    {
        var gpu = Graphics.Instance?.QueryGraphicsMemory() ?? GraphicsMemoryInfo.Unknown;

        gpuUsedBytes = gpu.UsedBytes;
        gpuTotalBytes = gpu.TotalBytes;
        gpuUsageKnown = gpu.UsedKnown;

        cpuBytes = ReadCpuBytes();
    }

    /// <summary>
    /// Working set rather than the managed heap: half of what a 3D frame costs lives inside native allocations (textures,
    /// decoders, the audio graph), and a heap-only number would report a few megabytes while the process holds hundreds.
    /// The browser has no process object at all, so Web reports the managed heap, which is its real CPU budget.
    /// </summary>
    static long ReadCpuBytes()
    {
        if (!processUnavailable)
        {
            try
            {
                process ??= Process.GetCurrentProcess();

                return process.WorkingSet64;
            }
            catch
            {
                processUnavailable = true;
            }
        }

        return GC.GetTotalMemory(false);
    }

    /// <summary>MiB below one GiB, then one decimal of a GiB: the form the HUD line is written in.</summary>
    internal static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
            return "0M";

        if (bytes < GiB)
            return $"{(bytes + MiB / 2) / MiB}M";

        return $"{bytes / (double)GiB:0.0}G";
    }
}
