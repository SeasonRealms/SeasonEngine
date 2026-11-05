// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// Contract 3-1: the measurement runner. It owns the scenario list, the warm-up/measure windows, the two CSV tables and
/// the process exit; it owns nothing about what is being measured.
///
/// <para><b>Why a runner and not a switch panel.</b> "Turn the shadow off and see" answers a question once. A runner
/// answers it the same way every time: the same camera path, the same frame count per cell, the same window length, the
/// same table. The only input that changes between rows is the scenario, which is the point.</para>
///
/// <para><b>Three ways in.</b> <c>SEASON_PROFILE_OUT</c> names the output directory and measures the baseline;
/// <c>SEASON_PROFILE_SWEEP=1</c> additionally walks the whole scenario table in one process (baseline between every
/// pair, so thermal drift shows up as a moving baseline instead of as a fake delta); and
/// <c>SEASON_PROFILE_SCENARIO=name</c> measures exactly one row, for switches that have to be set before initialization
/// and are therefore applied by an outer shell, one process at a time.</para>
///
/// <para><b>The one-row path is a labelling contract, not a switch panel.</b> A row named by
/// <c>SEASON_PROFILE_SCENARIO</c> is not applied by this class - the work happened before the window opened. Those
/// switches are the ones baked into pipelines and shader defines at startup (the volumetric fog tier, decals,
/// HDR scene colour, motion vectors, scene normals, <c>SEASON_INSTANCE_DRAW</c>); the shell sets them, launches, and the
/// row records what they cost. That is also why the tables use one <c>run</c> per process: a sweep of those rows is
/// necessarily a sweep of processes, and <c>run</c> is what separates them.</para>
///
/// <para><b>Windows are token ranges, not frame counters.</b> A ring buffer hands a frame's GPU numbers back one to
/// three frames late, so "the frames I measured" cannot be a counter that closes when the last frame is recorded - it
/// would lose the tail. Each scenario instead remembers the token range it measured, and a frame is written under that
/// scenario whenever it arrives, however late. <c>frame</c> in both tables is the offset inside that range, so row order
/// is stable to pivot even if the writes interleave at a window boundary.</para>
///
/// <para><b>Off costs one static bool read per update.</b> <see cref="Enabled"/> is false unless an environment variable
/// asked for this, and <see cref="Tick"/> returns on its first line. Nothing is allocated and no file is opened.</para>
///
/// <para><b>Threading.</b> Render thread only. <see cref="Tick"/> is called from the app's update and the sink fires from
/// inside <see cref="FrameProfiler.BeginFrame"/> on the same thread, so the runner needs no locking either.</para>
/// </summary>
public static class CostProfile
{
    const string OutputVariable = "SEASON_PROFILE_OUT";
    const string SweepVariable = "SEASON_PROFILE_SWEEP";
    const string ScenarioVariable = "SEASON_PROFILE_SCENARIO";
    const string CameraVariable = "SEASON_PROFILE_CAMERA";
    const string WarmupVariable = "SEASON_PROFILE_WARMUP";
    const string FramesVariable = "SEASON_PROFILE_FRAMES";
    const string ExitVariable = "SEASON_PROFILE_EXIT";

    /// <summary>Frames served before a window opens. About 2 s at 60 Hz: long enough for TAA history and every lazy
    /// allocation to settle, short enough that a 20-row sweep still finishes in minutes.</summary>
    const int DefaultWarmupFrames = 120;

    /// <summary>Frames inside a measurement window. About 5 s at 60 Hz, i.e. 300 samples per cell - far past the point
    /// where a median stops moving, and identical for every cell so the active particle count matches.</summary>
    const int DefaultMeasureFrames = 300;

    /// <summary>
    /// Frames waited after the last window before the run closes. A frame's GPU numbers come back one to three frames
    /// late, so closing on the last recorded frame would drop the tail of the table; this matches the profiler's own
    /// eight-frame label ring, after which it publishes whatever is left with an unknown GPU column.
    /// </summary>
    const int DrainFrames = 8;

    static readonly string? outputDirectory = ReadText(OutputVariable);

    static readonly bool sweep = ReadFlag(SweepVariable);

    static readonly string? requestedScenario = ReadText(ScenarioVariable);

    static readonly string? requestedCamera = ReadText(CameraVariable);

    static readonly int warmupFrames = ReadCount(WarmupVariable, DefaultWarmupFrames);

    static readonly int measureFrames = ReadCount(FramesVariable, DefaultMeasureFrames);

    static readonly bool exitWhenDone = ReadFlag(ExitVariable);

    /// <summary>
    /// Identity of this process in the tables. It carries the scene name after an <c>@</c> because one process measures
    /// exactly one scene: without the suffix a sweep of two apps would pivot into a single ambiguous row.
    /// </summary>
    static readonly string runId = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    static readonly string platform = ReadPlatform();

    /// <summary>
    /// True when the profiler is on <em>and</em> something asked for a table or a sweep. A plain
    /// <c>SEASON_PROFILE=1</c> run keeps the HUD line and does not touch the disk.
    /// </summary>
    public static readonly bool Enabled = FrameProfiler.Enabled
        && (!string.IsNullOrEmpty(outputDirectory) || sweep || !string.IsNullOrEmpty(requestedScenario));

    static readonly StringBuilder line = new(256);

    static ICostProfileScene? scene;

    static Segment[]? plan;

    static Segment? active;

    static int planIndex;

    static int drainedFrames;

    static Stage stage;

    static string cameraPath = string.Empty;

    static bool outputOpened;

    static StreamWriter? framesWriter;

    static StreamWriter? passesWriter;

    static bool dayNightFrozen;

    static float dayNightSpeed;

    static CostProfile()
    {
        if (!Enabled) return;

        // A scenario's t must be a pure function of its frame number, otherwise two rows of the table were measured
        // against two different worlds and their difference means nothing.
        FrameProfiler.PinDelta = true;
        FrameProfiler.Sink = OnFrame;
    }

    /// <summary>
    /// Teaches the runner what to measure. Called once by the app; without it the runner measures the baseline only,
    /// which is still a valid table, just a one-row one. Calling it again replaces the previous scene.
    /// </summary>
    public static void RegisterScene(ICostProfileScene? scene)
    {
        CostProfile.scene = scene;
    }

    /// <summary>
    /// Contract 3-1 per-frame entry point, called at the very end of the app's update so every panel and every camera
    /// rig has finished writing before the pose below overwrites them.
    ///
    /// <para>It advances the scenario state machine and writes the camera pose for the next rendered frame. The pose
    /// lands one frame late (the platform reads the camera before the app update) and that is fine: the path is a pure
    /// function of the frame number, so the same offset applies to every scenario.</para>
    /// </summary>
    /// <param name="app">The app whose camera and simulation clock are driven. Only the render thread may pass it.</param>
    public static void Tick(BaseApp app)
    {
        if (!Enabled || stage == Stage.Finished) return;

        if (stage == Stage.Waiting)
        {
            if (scene != null && !scene.IsReady) return;

            Begin();
        }

        if (active == null && !StartNext())
        {
            // Let the ring empty before the sink is removed, otherwise the last few measured frames are written after
            // there is no writer listening for them.
            if (++drainedFrames <= DrainFrames) return;

            Finish();

            return;
        }

        var segment = active!;
        int frame = segment.Frames++;

        if (frame == 0) ResetScene(app);

        ApplyCamera(app, frame * FrameProfiler.PinnedDelta);

        // The next frame to open is the first one whose numbers belong to the window: everything already submitted
        // (warm-up) stays outside it, and the camera pose written this frame is the one that frame will render.
        if (frame == warmupFrames) segment.MeasureFrom = FrameProfiler.CurrentToken + 1;

        if (frame >= warmupFrames + measureFrames) Close();
    }

    enum Stage : byte
    {
        Waiting = 0,
        Running = 1,
        Finished = 2,
    }

    static void Begin()
    {
        stage = Stage.Running;

        plan = BuildPlan();

        dayNightSpeed = WorldSettings.Current.DayNightSpeed;
        dayNightFrozen = true;

        var paths = scene?.CameraPaths;

        if (paths != null && paths.Count > 0)
        {
            cameraPath = requestedCamera ?? paths[0];

            bool found = false;

            for (int i = 0; i < paths.Count; i++)
            {
                if (!string.Equals(paths[i], cameraPath, StringComparison.Ordinal)) continue;

                found = true;

                break;
            }

            // An unknown path would silently keep whatever pose the app last wrote, which looks like a measurement and
            // is not one; fall back to the default and say so.
            if (!found)
            {
                Log($"[CostProfile] camera path '{cameraPath}' is not in {scene!.Name}; using '{paths[0]}'");

                cameraPath = paths[0];
            }
        }

        OpenOutput();

        Log($"[CostProfile] run {runId} scene={scene?.Name ?? "(none)"} camera={(cameraPath.Length == 0 ? "(app)" : cameraPath)} " +
            $"gpu={(FrameProfiler.GpuTimingAvailable ? "timestamps" : "unavailable")} " +
            $"warmup={warmupFrames} frames={measureFrames} scenarios={plan.Length}");
    }

    /// <summary>
    /// Turns the scenario list into the order actually measured. The sweep opens with a baseline, then measures one
    /// scenario and one baseline alternately: a baseline that drifts between the first and the last row is the honest
    /// error bar on every delta in the table, and it is invisible if you measure the baseline only once.
    /// </summary>
    static Segment[] BuildPlan()
    {
        string? single = requestedScenario;

        if (!string.IsNullOrEmpty(single))
        {
            var matched = Find(single);

            return new[] { new Segment(single, matched) };
        }

        if (scene == null || !sweep) return new[] { new Segment("baseline") };

        var scenarios = scene.Scenarios;
        var list = new List<Segment>(scenarios.Count * 2 + 1) { new("baseline") };
        int baselines = 1;

        for (int i = 0; i < scenarios.Count; i++)
        {
            var scenario = scenarios[i];

            if (scenario.IsBaseline) continue;

            list.Add(new Segment(scenario.Name, scenario));

            baselines++;
            list.Add(new Segment(baselines == 1 ? "baseline" : "baseline#" + baselines.ToString(CultureInfo.InvariantCulture)));
        }

        return list.ToArray();
    }

    static CostScenario? Find(string name)
    {
        var scenarios = scene?.Scenarios;

        if (scenarios == null) return null;

        for (int i = 0; i < scenarios.Count; i++)
        {
            if (string.Equals(scenarios[i].Name, name, StringComparison.Ordinal)) return scenarios[i];
        }

        return null;
    }

    static bool StartNext()
    {
        var segments = plan;

        if (segments == null || planIndex >= segments.Length) return false;

        var segment = segments[planIndex++];

        segment.Scenario?.Apply?.Invoke();

        active = segment;

        return true;
    }

    static void Close()
    {
        var segment = active!;

        active = null;

        // Everything opened up to and including this frame belongs to the window. Frames still in the readback ring
        // arrive after this line and are still attributed correctly, which is the whole reason the window is a range.
        segment.MeasureTo = FrameProfiler.CurrentToken + 1;

        segment.Scenario?.Revert?.Invoke();

        FlushOutput();

        Log($"[CostProfile] {segment.Name}: {FrameProfiler.ToCompactString()}");
    }

    static void Finish()
    {
        stage = Stage.Finished;

        FrameProfiler.Sink = null;
        FrameProfiler.PinDelta = false;

        if (dayNightFrozen)
        {
            WorldSettings.Current.DayNightSpeed = dayNightSpeed;

            dayNightFrozen = false;
        }

        FlushOutput();
        CloseOutput();

        Log($"[CostProfile] done: {plan?.Length ?? 0} scenario(s) x {measureFrames} frames after {warmupFrames} warm-up frames" +
            $", output={(string.IsNullOrEmpty(outputDirectory) ? "(none)" : outputDirectory)}");

        if (!exitWhenDone) return;

        // Copying AbCapture: leaving through the normal shutdown path runs teardown frames that would be measured into
        // the next cell of a shell-driven sweep. The tables are already flushed above.
        Environment.Exit(0);
    }

    /// <summary>
    /// Puts the simulation back to a known origin at the start of a window. This is deliberately not the pixel-exact
    /// phase alignment AbCapture does: a cost table compares milliseconds, and a particle one frame out of phase costs
    /// the same. What must match is the number of frames in each cell - which the window length fixes - and the camera
    /// path, which is a pure function of the frame number.
    /// </summary>
    static void ResetScene(BaseApp app)
    {
        app.Time = 0f;

        FrameSchedule.ResetFrameIndex();

        // The clock is a seed, not a driver: StartHour can only be changed before initialization, so freezing the rate
        // is what actually gives every scenario the same sun. The original rate is restored in Finish.
        WorldSettings.Current.DayNightSpeed = 0f;

        FrameProfiler.ResetStatistics();
    }

    static void ApplyCamera(BaseApp app, float t)
    {
        var target = scene;

        if (target == null || cameraPath.Length == 0) return;

        app.CameraPos = target.SampleCameraPose(cameraPath, t, out Vector3 lookAt);
        app.CameraTarget = lookAt;
    }

    /// <summary>
    /// The sink. Called from <see cref="FrameProfiler.BeginFrame"/> - after the previous frame's numbers have been
    /// drained and <em>before</em> the allocation counter is armed - so none of the formatting below is charged to any
    /// frame's allocation column. That ordering is load-bearing: move the writer after the counter and every row gains a
    /// few kilobytes of exporter cost that has nothing to do with the scene.
    /// </summary>
    static void OnFrame(in CompletedFrame frame)
    {
        if (framesWriter == null && passesWriter == null) return;

        var segments = plan;

        if (segments == null) return;

        for (int i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];

            if (frame.Token < segment.MeasureFrom || frame.Token >= segment.MeasureTo) continue;

            long index = frame.Token - segment.MeasureFrom;

            // A full disk must not become a render-thread exception: the frame that discovers it is the frame inside
            // every pass. Dropping the writers keeps the HUD and the log alive and says so once.
            try
            {
                if (framesWriter != null) WriteFrame(segment, index, in frame);
                if (passesWriter != null) WriteIntervals(segment, index, in frame);
            }
            catch (Exception exception)
            {
                CloseOutput();

                Log($"[CostProfile] output stopped: {exception.Message}");
            }

            return;
        }
    }

    static void WriteFrame(Segment segment, long index, in CompletedFrame frame)
    {
        line.Clear();

        AppendKey(segment, index);
        line.Append(',');

        AppendMilliseconds(frame.GpuKnown, frame.GpuTotalMs);
        line.Append(',');
        AppendMilliseconds(true, frame.ExecuteMs);
        line.Append(',');
        AppendMilliseconds(true, frame.FrameMs);

        line.Append(',').Append(frame.AllocBytes.ToString(CultureInfo.InvariantCulture));
        line.Append(',').Append(frame.Gen0.ToString(CultureInfo.InvariantCulture));
        line.Append(',').Append(frame.Gen1.ToString(CultureInfo.InvariantCulture));
        line.Append(',').Append(frame.Gen2.ToString(CultureInfo.InvariantCulture));

        framesWriter!.WriteLine(line);
    }

    static void WriteIntervals(Segment segment, long index, in CompletedFrame frame)
    {
        var intervals = frame.Intervals;
        var writer = passesWriter!;

        for (int i = 0; i < intervals.Length; i++)
        {
            ref readonly var interval = ref intervals[i];

            line.Clear();

            AppendKey(segment, index);

            line.Append(',').Append(interval.Kind == ProfileIntervalKind.Pass ? "pass" : "compute");
            line.Append(',');
            AppendField(interval.Name);
            line.Append(',').Append(interval.View.ToString(CultureInfo.InvariantCulture));
            line.Append(',');
            AppendMilliseconds(interval.GpuKnown, interval.GpuMs);
            line.Append(',');
            AppendMilliseconds(true, interval.CpuMs);

            writer.WriteLine(line);
        }
    }

    static void AppendKey(Segment segment, long index)
    {
        AppendField(runId);
        line.Append(',');
        AppendField(platform);
        line.Append(',');
        AppendField(segment.Name);
        line.Append(',').Append(index.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>NaN is not zero: an interval the backend could not time prints <c>--</c>, never a number it did not
    /// measure. A table that silently shows zero for "unavailable" is worse than no table.</summary>
    static void AppendMilliseconds(bool known, double milliseconds)
    {
        if (!known)
        {
            line.Append("--");

            return;
        }

        line.Append(milliseconds.ToString("0.###", CultureInfo.InvariantCulture));
    }

    static void AppendField(string value)
    {
        if (value.IndexOf(',') < 0 && value.IndexOf('"') < 0)
        {
            line.Append(value);

            return;
        }

        line.Append('"').Append(value.Replace("\"", "\"\"")).Append('"');
    }

    static void OpenOutput()
    {
        if (outputOpened) return;

        outputOpened = true;

        string? directory = outputDirectory;

        if (string.IsNullOrEmpty(directory)) return;

        try
        {
            Directory.CreateDirectory(directory);

            // Append: the per-process path measures one row per launch and expects every launch to land in the same
            // table, so the files are opened for append and identified by the run column.
            var frames = new StreamWriter(Path.Combine(directory, "cost-frames.csv"), true, Encoding.UTF8, 1 << 16);
            frames.WriteLine("run,platform,scenario,frame,gpu_total_ms,cpu_execute_ms,cpu_frame_ms,alloc_bytes,gen0,gen1,gen2");
            framesWriter = frames;

            var passes = new StreamWriter(Path.Combine(directory, "cost-passes.csv"), true, Encoding.UTF8, 1 << 16);
            passes.WriteLine("run,platform,scenario,frame,kind,name,view,gpu_ms,cpu_ms");
            passesWriter = passes;
        }
        catch (Exception exception)
        {
            // A read-only output directory must not end the run: the HUD and the log line still work, so the numbers
            // are still readable, they just are not on disk.
            framesWriter = null;
            passesWriter = null;

            Log($"[CostProfile] cannot write to '{directory}': {exception.Message}");
        }
    }

    static void FlushOutput()
    {
        try
        {
            framesWriter?.Flush();
            passesWriter?.Flush();
        }
        catch (Exception)
        {
            // Flushing is best effort; CloseOutput disposes what could not be flushed.
        }
    }

    static void CloseOutput()
    {
        try
        {
            framesWriter?.Dispose();
            passesWriter?.Dispose();
        }
        catch (Exception)
        {
            // Nothing left to report to: the process is about to exit or the run is over.
        }

        framesWriter = null;
        passesWriter = null;
    }

    static void Log(string message)
    {
        Debug.WriteLine(message);

        DeviceServices.BaseApp?.AddLog(LogType.None, message);
    }

    static string ReadPlatform()
    {
        // One string that already distinguishes win/osx/android/ios and the architecture, without inventing a
        // four-backend switch the engine does not otherwise keep around.
        string identifier = RuntimeInformation.RuntimeIdentifier;

        return string.IsNullOrEmpty(identifier) ? Environment.OSVersion.Platform.ToString() : identifier;
    }

    static string? ReadText(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);

        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    static bool ReadFlag(string name)
    {
        string? value = ReadText(name);

        if (value == null) return false;

        return value != "0" && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
    }

    static int ReadCount(string name, int fallback)
    {
        string? value = ReadText(name);

        if (value == null) return fallback;

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0
            ? parsed
            : fallback;
    }

    /// <summary>
    /// One cell of the table. <see cref="MeasureFrom"/>/<see cref="MeasureTo"/> are profiler tokens, closed to an open
    /// range: the tail frames that arrive after the cell ended still fall inside it.
    /// </summary>
    sealed class Segment
    {
        internal Segment(string name, CostScenario? scenario = null)
        {
            Name = name;
            Scenario = scenario;
        }

        internal readonly string Name;

        internal readonly CostScenario? Scenario;

        internal long MeasureFrom = long.MaxValue;

        internal long MeasureTo = long.MaxValue;

        internal int Frames;
    }
}
