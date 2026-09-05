// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Threading;
using Season.Basic;
using Season.Platforms.Windows.DirectX;
using Season.Storage;

namespace Season.Platforms.Windows;

/// <summary>
/// Windows implementation of <see cref="IMediaRecorder"/>: silent screen
/// recording of the app's own output, driven entirely from code through
/// <see cref="Start"/> and <see cref="Stop"/>.
/// <para>
/// The pipeline is three stages, each on its own thread, so no stage has to wait
/// for the next one in the common case:
/// </para>
/// <list type="number">
/// <item>Render thread: the presented backbuffer is copied into a readback ring
/// (<see cref="DXCaptureRing"/>) inside the barrier window that already exists
/// for the Present transition, then finished slots are memcpy'd into pooled
/// arrays. That is the only cost the render loop pays.</item>
/// <item>Encoder thread: RGBA to bottom-up BGRA conversion plus the Media
/// Foundation sink writer, which hands H.264 off to the GPU encoder.</item>
/// <item>Media Foundation's own worker threads: muxing and file I/O.</item>
/// </list>
/// <para>
/// Audio is not part of this iteration; the file is video-only.
/// </para>
/// </summary>
internal sealed class WindowsMediaRecorder : IMediaRecorder
{
    sealed class Session
    {
        public required RecordSessionOptions Options { get; init; }
        public required FrameCaptureRequest Request { get; init; }
        public required WindowsVideoEncoderSink Sink { get; init; }
        public required FrameBufferPool Pool { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required int FramesPerSecond { get; init; }
    }

    /// <summary>
    /// How long <see cref="Stop"/> waits for the render thread to retire the
    /// capture request. Only reached when the render loop has already stopped
    /// (window closing, device lost), in which case the in-flight readbacks are
    /// abandoned rather than deadlocking the caller.
    /// </summary>
    static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);

    readonly Lock _gate = new();

    Session? _session;

    RecorderStats _lastStats;

    public bool IsRecording => Volatile.Read(ref _session) != null;

    public RecorderStats Stats
    {
        get
        {
            var session = Volatile.Read(ref _session);
            return session == null ? _lastStats : Collect(session);
        }
    }

    public Task<bool> Start(RecordSessionOptions? options = null)
    {
        options ??= new RecordSessionOptions();

        lock (_gate)
        {
            if (_session != null) return Task.FromResult(false);

            try
            {
                _session = CreateSession(options);
            }
            catch (Exception ex)
            {
                DeviceServices.BaseApp?.AddLog(LogType.Error, $"Failed to start recording: {ex}");
                return Task.FromResult(false);
            }

            if (_session == null) return Task.FromResult(false);

            // Publishing the request is what actually switches recording on: from
            // this assignment onwards the render thread starts copying out frames.
            _session.Request.Begin();
            BaseApp.ActiveFrameCapture = _session.Request;

            return Task.FromResult(true);
        }
    }

    public async Task<RecordResult?> Stop()
    {
        Session session;

        lock (_gate)
        {
            if (_session == null) return null;

            session = _session;
            _session = null;

            // Clearing this stops new captures immediately and tells the ring to
            // retire itself once its in-flight slots have been delivered.
            BaseApp.ActiveFrameCapture = null;
        }

        var duration = session.Request.Elapsed;

        await WaitForDrain(session).ConfigureAwait(false);

        string? path = await session.Sink.Finish().ConfigureAwait(false);

        var stats = Collect(session);
        _lastStats = stats;
        long frameCount = session.Sink.EncodedFrames;

        await session.Sink.DisposeAsync().ConfigureAwait(false);

        byte[]? bytes = null;
        if (path != null && session.Options.ReturnBytes)
        {
            try
            {
                bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                DeviceServices.BaseApp?.AddLog(LogType.Error, $"Failed to read back recording: {ex}");
            }
        }

        session.Pool.Clear();

        return new RecordResult
        {
            FilePath = path,
            Bytes = bytes,
            Width = session.Width,
            Height = session.Height,
            FramesPerSecond = session.FramesPerSecond,
            FrameCount = frameCount,
            Duration = duration,
            Stats = stats,
        };
    }

    Session? CreateSession(RecordSessionOptions options)
    {
        int fps = Math.Clamp(options.FramesPerSecond, 1, 240);

        // The visible corner of the backbuffer, not the whole surface: under a
        // composition scale above 1 the renderer shrinks its output into that
        // corner, and recording the rest would show a wider view than the game.
        var (visibleWidth, visibleHeight) = DirectX.Device.GetPresentedSize();
        int width = options.Width > 0 ? options.Width : visibleWidth;
        int height = options.Height > 0 ? options.Height : visibleHeight;

        // H.264 requires even dimensions, and cropping is the only lossless-looking
        // way to get there.
        width &= ~1;
        height &= ~1;

        if (width <= 0 || height <= 0)
        {
            DeviceServices.BaseApp?.AddLog(LogType.Error, "Failed to start recording: the swap chain has no size yet.");
            return null;
        }

        string path = options.OutputFilePath
            ?? StorageService.SubPath(StorageService.DirectoryBase, $"Record-{DateTime.Now:yyyyMMddHHmmss}.mp4");

        string? directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        int slots = Math.Clamp(options.ReadbackSlots, 2, 8);
        int queueCapacity = Math.Clamp(options.QueueCapacity, 2, 32);

        var sink = new WindowsVideoEncoderSink(new VideoEncodeOptions
        {
            FilePath = path,
            Width = width,
            Height = height,
            FramesPerSecond = fps,
            Bitrate = options.Bitrate ?? VideoEncodingHelper.EstimateBitrate(width, height, fps, options.Quality),
            QueueCapacity = queueCapacity,
        });

        // One buffer per queue slot plus a couple in flight between the ring and
        // the queue, so the steady state never allocates.
        var pool = new FrameBufferPool(checked(width * height * 4), queueCapacity + 2);

        var request = new FrameCaptureRequest
        {
            Width = width,
            Height = height,
            FramesPerSecond = fps,
            Pool = pool,
            ReadbackSlots = slots,
            OnFrame = (buffer, frameIndex) => sink.WriteVideoFrame(buffer, frameIndex, pool),
        };

        return new Session
        {
            Options = options,
            Request = request,
            Sink = sink,
            Pool = pool,
            Width = width,
            Height = height,
            FramesPerSecond = fps,
        };
    }

    static async Task WaitForDrain(Session session)
    {
        var completion = session.Request.Completion.Task;

        if (completion == await Task.WhenAny(completion, Task.Delay(DrainTimeout)).ConfigureAwait(false))
            return;

        DeviceServices.BaseApp?.AddLog(LogType.Error,
            "Recording stopped without the render loop draining its readbacks; the tail of the video may be missing.");
    }

    static RecorderStats Collect(Session session) => new()
    {
        CapturedFrames = session.Request.DeliveredFrames,
        EncodedFrames = session.Sink.EncodedFrames,
        DuplicatedFrames = session.Sink.DuplicatedFrames,
        SizeMismatchFrames = session.Request.SizeMismatchFrames,
        ReadbackStallMilliseconds = session.Request.ReadbackStallMilliseconds,
        EncodeQueueStallMilliseconds = session.Sink.QueueStallMilliseconds,
    };
}
