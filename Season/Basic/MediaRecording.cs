// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Collections.Concurrent;

namespace Season.Basic;

// ── Recording contract (shared layer) ────────────────────────────────────────
//
// Design rules that every backend implementation must honor:
//
// 1. Steady-state zero overhead. The cost of the recording capability is only
//    allowed to exist while a session is running. When no session is active,
//    <see cref="BaseApp.ActiveFrameCapture"/> is null and the render path must
//    be instruction-for-instruction equivalent to the code before recording
//    existed. A single null check per frame is the entire budget.
//
// 2. The capture point is the presented backbuffer, exactly the same texture
//    that <c>IRecordService.CaptureApp</c> reads back. That means the recording
//    always sees the final LDR RGBA8 image after MSAA resolve, FinalBlit,
//    tonemapping and overlay, with no extra passes and no shader variants.
//
// 3. Constant output frame rate. Pacing is decided on the render thread by
//    <see cref="FrameCaptureRequest.ShouldCapture"/> against wall-clock time,
//    and gaps caused by render hitches are filled by the encoder re-submitting
//    the previous frame. The produced file therefore always plays back at
//    <see cref="VideoEncodeOptions.FramesPerSecond"/> with correct timing.
//
// 4. Back pressure instead of silent loss. Both the GPU readback ring and the
//    encoder queue are bounded. When they are full the producer blocks, which
//    lowers the render frame rate but never corrupts the timeline.
//
// This first iteration covers video only. Audio (<c>WriteAudioSamples</c>) is
// declared on the sink so muxing can be added later without touching the
// capture or pacing code.

/// <summary>
/// Video (and later audio) recording of the application's own rendered output.
/// This is the "inside the engine" counterpart to <see cref="IRecordService"/>,
/// which records the microphone, i.e. something outside the engine.
/// Obtain the platform instance through <see cref="DeviceServices.Recorder"/>;
/// it is null on platforms that have no implementation yet.
/// </summary>
public interface IMediaRecorder
{
    /// <summary>Whether a recording session is currently running.</summary>
    bool IsRecording { get; }

    /// <summary>Live counters of the running (or last finished) session.</summary>
    RecorderStats Stats { get; }

    /// <summary>
    /// Start a session. Returns false when a session is already running, when
    /// the graphics device is not ready, or when the platform encoder failed to
    /// initialize. The frame size is locked at this point: a later window resize
    /// is cropped or letterboxed instead of breaking the encoder invariant.
    /// </summary>
    Task<bool> Start(RecordSessionOptions? options = null);

    /// <summary>
    /// Stop the session, drain every frame that is still in flight, and
    /// finalize the container. Returns null when no session was running.
    /// </summary>
    Task<RecordResult?> Stop();
}

/// <summary>Per-session recording parameters.</summary>
public sealed class RecordSessionOptions
{
    /// <summary>Constant output frame rate. The render loop may run faster or
    /// slower; the recorder paces against wall-clock time either way.</summary>
    public int FramesPerSecond { get; set; } = 30;

    /// <summary>Quality hint (1-100) used to derive a bitrate when
    /// <see cref="Bitrate"/> is not set.</summary>
    public int Quality { get; set; } = 90;

    /// <summary>Explicit average bitrate in bits per second. Overrides
    /// <see cref="Quality"/> when set.</summary>
    public int? Bitrate { get; set; }

    /// <summary>Output file path. When null the recorder writes
    /// <c>Record-yyyyMMddHHmmss.mp4</c> under the app storage directory.</summary>
    public string? OutputFilePath { get; set; }

    /// <summary>Also read the finished file back into
    /// <see cref="RecordResult.Bytes"/>. Off by default because a minute of
    /// 1080p is tens of megabytes.</summary>
    public bool ReturnBytes { get; set; }

    /// <summary>Capture width. 0 means "use the current backbuffer width".</summary>
    public int Width { get; set; }

    /// <summary>Capture height. 0 means "use the current backbuffer height".</summary>
    public int Height { get; set; }

    /// <summary>Number of GPU readback slots. More slots absorb longer encoder
    /// hiccups at the cost of memory (width * height * 4 each).</summary>
    public int ReadbackSlots { get; set; } = 4;

    /// <summary>Depth of the queue between the render thread and the encoder
    /// thread, in frames.</summary>
    public int QueueCapacity { get; set; } = 8;
}

/// <summary>Outcome of a finished recording session.</summary>
public sealed class RecordResult
{
    /// <summary>Path of the written container, or null when writing failed.</summary>
    public string? FilePath { get; init; }

    /// <summary>File content, only when <see cref="RecordSessionOptions.ReturnBytes"/> was set.</summary>
    public byte[]? Bytes { get; init; }

    /// <summary>Encoded frame width.</summary>
    public int Width { get; init; }

    /// <summary>Encoded frame height.</summary>
    public int Height { get; init; }

    /// <summary>Constant output frame rate of the container.</summary>
    public int FramesPerSecond { get; init; }

    /// <summary>Total number of frames written, including duplicate fills.</summary>
    public long FrameCount { get; init; }

    /// <summary>Wall-clock length of the session.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Counters collected during the session.</summary>
    public RecorderStats Stats { get; init; }
}

/// <summary>
/// Diagnostic counters. <see cref="DuplicatedFrames"/> is the honest measure of
/// "did we drop frames": it counts output frames that had to repeat the previous
/// image because the render loop did not produce one in time.
/// </summary>
public struct RecorderStats
{
    /// <summary>Frames read back from the GPU and handed to the encoder.</summary>
    public long CapturedFrames;

    /// <summary>Frames written into the container, duplicates included.</summary>
    public long EncodedFrames;

    /// <summary>Output frames filled by repeating the previous image.</summary>
    public long DuplicatedFrames;

    /// <summary>Frames whose backbuffer size differed from the locked session
    /// size and were therefore cropped or letterboxed.</summary>
    public long SizeMismatchFrames;

    /// <summary>Milliseconds the render thread spent waiting for a free
    /// readback slot.</summary>
    public long ReadbackStallMilliseconds;

    /// <summary>Milliseconds the render thread spent waiting for room in the
    /// encoder queue.</summary>
    public long EncodeQueueStallMilliseconds;
}

/// <summary>
/// Streaming video encoder. Unlike <see cref="IImageService.SaveVideo"/>, which
/// requires every frame to be resident in memory at once, a sink accepts frames
/// one at a time and muxes them as they arrive, so session length is bounded by
/// disk space rather than RAM.
/// </summary>
public interface IVideoEncoderSink : IAsyncDisposable
{
    /// <summary>Parameters the sink was created with.</summary>
    VideoEncodeOptions Options { get; }

    /// <summary>Frames written so far, duplicate fills included.</summary>
    long EncodedFrames { get; }

    /// <summary>Output frames produced by repeating the previous image.</summary>
    long DuplicatedFrames { get; }

    /// <summary>
    /// Submit one frame. <paramref name="rgba"/> is tightly packed top-down
    /// RGBA8 of exactly <c>Width * Height * 4</c> bytes and is owned by the sink
    /// until it is returned to <paramref name="pool"/>.
    /// <paramref name="frameIndex"/> is the frame's position on the constant-rate
    /// output timeline; skipped indices are filled with duplicates.
    /// May block when the internal queue is full.
    /// </summary>
    void WriteVideoFrame(byte[] rgba, long frameIndex, FrameBufferPool? pool = null);

    /// <summary>
    /// Submit interleaved s16le PCM at the sink's audio format. Not used by the
    /// video-only implementations of this iteration.
    /// </summary>
    void WriteAudioSamples(ReadOnlySpan<byte> pcm, TimeSpan presentationTimestamp) { }

    /// <summary>Drain the queue and finalize the container.</summary>
    Task<string?> Finish();
}

/// <summary>Immutable configuration of an <see cref="IVideoEncoderSink"/>.</summary>
public sealed class VideoEncodeOptions
{
    /// <summary>Output path. The container is inferred from the extension.</summary>
    public required string FilePath { get; init; }

    /// <summary>Frame width in pixels. Must be even for H.264.</summary>
    public required int Width { get; init; }

    /// <summary>Frame height in pixels. Must be even for H.264.</summary>
    public required int Height { get; init; }

    /// <summary>Constant output frame rate.</summary>
    public int FramesPerSecond { get; init; } = 30;

    /// <summary>Average bitrate in bits per second.</summary>
    public int Bitrate { get; init; }

    /// <summary>Depth of the queue between producer and encoder thread.</summary>
    public int QueueCapacity { get; init; } = 8;
}

/// <summary>
/// Continuous frame readback request installed on <see cref="BaseApp.ActiveFrameCapture"/>.
/// While it is non-null every backend that supports recording copies the
/// presented backbuffer into a readback ring and delivers finished frames
/// through <see cref="OnFrame"/>.
/// <para>
/// Everything on this type is touched by the render thread except
/// <see cref="Completion"/>, which the render thread signals once and the
/// stopping thread awaits.
/// </para>
/// </summary>
public sealed class FrameCaptureRequest
{
    /// <summary>Locked capture width. Frames of a different size are cropped or
    /// letterboxed so the encoder invariant survives a window resize.</summary>
    public required int Width { get; init; }

    /// <summary>Locked capture height.</summary>
    public required int Height { get; init; }

    /// <summary>Target constant frame rate used for pacing.</summary>
    public required int FramesPerSecond { get; init; }

    /// <summary>Pool the backend rents delivery buffers from. Buffers are
    /// exactly <c>Width * Height * 4</c> bytes.</summary>
    public required FrameBufferPool Pool { get; init; }

    /// <summary>
    /// Called on the render thread with a rented buffer holding tightly packed
    /// top-down RGBA8, plus the frame's index on the constant-rate timeline.
    /// The callee takes ownership of the buffer and must return it to
    /// <see cref="Pool"/>.
    /// </summary>
    public required Action<byte[], long> OnFrame { get; init; }

    /// <summary>Number of readback slots the backend should keep in flight.</summary>
    public int ReadbackSlots { get; init; } = 4;

    /// <summary>Signaled by the backend after the request has been retired and
    /// every captured frame has been delivered.</summary>
    public TaskCompletionSource Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    long _startTimestamp;

    long _nextFrameIndex;

    /// <summary>Frames handed to <see cref="OnFrame"/> so far.</summary>
    public long DeliveredFrames;

    /// <summary>Frames whose source size differed from the locked size.</summary>
    public long SizeMismatchFrames;

    /// <summary>Milliseconds the render thread blocked waiting for a slot.</summary>
    public long ReadbackStallMilliseconds;

    /// <summary>Start the pacing clock. Called once before the request is published.</summary>
    public void Begin() => _startTimestamp = Stopwatch.GetTimestamp();

    /// <summary>Wall-clock length since <see cref="Begin"/>.</summary>
    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(_startTimestamp);

    /// <summary>
    /// Render-thread pacing decision. Returns true when the current frame should
    /// be captured, together with its slot on the constant-rate output timeline.
    /// A render hitch simply produces a gap in the indices, which the encoder
    /// fills with duplicates, so the output stays exactly
    /// <see cref="FramesPerSecond"/> fps.
    /// </summary>
    public bool ShouldCapture(out long frameIndex)
    {
        long target = (long)((Stopwatch.GetTimestamp() - _startTimestamp)
            * (double)FramesPerSecond / Stopwatch.Frequency);

        if (target < _nextFrameIndex)
        {
            frameIndex = -1;
            return false;
        }

        frameIndex = target;
        _nextFrameIndex = target + 1;
        return true;
    }

    /// <summary>Called by the backend once the request is fully retired.</summary>
    public void SignalCompleted() => Completion.TrySetResult();
}

/// <summary>
/// Fixed-size frame buffer pool. Recording frames are multi-megabyte arrays that
/// would otherwise churn the large object heap every frame, and
/// <c>ArrayPool&lt;byte&gt;.Shared</c> gives no guarantee for buckets this large.
/// </summary>
public sealed class FrameBufferPool
{
    readonly ConcurrentQueue<byte[]> _free = new();

    readonly int _bufferBytes;

    readonly int _maxRetained;

    /// <summary>Create a pool of buffers of exactly <paramref name="bufferBytes"/> bytes.</summary>
    public FrameBufferPool(int bufferBytes, int maxRetained)
    {
        _bufferBytes = bufferBytes;
        _maxRetained = Math.Max(1, maxRetained);
    }

    /// <summary>Size of every buffer handed out by this pool.</summary>
    public int BufferBytes => _bufferBytes;

    /// <summary>Take a buffer. Contents are undefined.</summary>
    public byte[] Rent() => _free.TryDequeue(out var buffer) ? buffer : new byte[_bufferBytes];

    /// <summary>Give a buffer back. Foreign or oversized buffers are dropped.</summary>
    public void Return(byte[]? buffer)
    {
        if (buffer is null || buffer.Length != _bufferBytes) return;

        if (_free.Count < _maxRetained)
            _free.Enqueue(buffer);
    }

    /// <summary>Drop every retained buffer.</summary>
    public void Clear()
    {
        while (_free.TryDequeue(out _)) { }
    }
}
