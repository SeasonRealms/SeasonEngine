// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Collections.Concurrent;
using System.Runtime.Intrinsics;
using System.Threading;
using Season.Basic;
using static Season.Platforms.Windows.WindowsMediaFoundationInterop;

namespace Season.Platforms.Windows;

/// <summary>
/// Streaming H.264 encoder built on a Media Foundation sink writer.
/// <para>
/// The whole point of this type is that frames never accumulate: the producer
/// hands over one buffer at a time and a dedicated thread muxes it immediately,
/// so a session is bounded by disk space instead of RAM. That is what separates
/// it from <c>IImageService.SaveVideo</c>, which needs every frame resident.
/// </para>
/// <para>
/// Threading contract: <see cref="WriteVideoFrame"/> is called by the render
/// thread and does nothing but hand the buffer to a bounded queue. Every COM
/// object lives on, and is only ever touched by, the encoder thread, which
/// initializes its own MTA apartment. All pixel work (RGBA to bottom-up BGRA)
/// happens on that thread too, so the render thread only ever pays for one
/// memcpy of the readback.
/// </para>
/// </summary>
internal sealed class WindowsVideoEncoderSink : IVideoEncoderSink
{
    readonly record struct PendingFrame(byte[] Buffer, long Index, FrameBufferPool? Pool);

    /// <summary>Hardware-encoder friendly H.264 High profile (eAVEncH264VProfile_High).</summary>
    const int H264ProfileHigh = 100;

    const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);

    readonly BlockingCollection<PendingFrame> _queue;

    readonly Thread _thread;

    readonly long _frameDuration;

    readonly int _frameBytes;

    Task? _finish;

    long _encodedFrames;

    long _duplicatedFrames;

    long _queueStallMilliseconds;

    Exception? _failure;

    public VideoEncodeOptions Options { get; }

    public long EncodedFrames => Volatile.Read(ref _encodedFrames);

    public long DuplicatedFrames => Volatile.Read(ref _duplicatedFrames);

    /// <summary>Milliseconds the producer spent blocked on a full queue. Non-zero
    /// means the encoder could not keep up with the capture rate.</summary>
    public long QueueStallMilliseconds => Volatile.Read(ref _queueStallMilliseconds);

    /// <summary>First error raised on the encoder thread, if any.</summary>
    public Exception? Failure => Volatile.Read(ref _failure);

    public WindowsVideoEncoderSink(VideoEncodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        Options = options;
        _frameBytes = checked(options.Width * options.Height * 4);
        _frameDuration = FrameDurationFromFps(Math.Max(1, options.FramesPerSecond));
        _queue = new BlockingCollection<PendingFrame>(Math.Max(2, options.QueueCapacity));

        _thread = new Thread(EncodeLoop)
        {
            IsBackground = true,
            Name = "Season.VideoEncoder",
            // Above normal so a busy render thread cannot starve the encoder and
            // turn every queue slot into back pressure on the render loop.
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    public void WriteVideoFrame(byte[] rgba, long frameIndex, FrameBufferPool? pool = null)
    {
        if (rgba is null) return;

        // Once the encoder failed or finished there is nowhere to put the frame,
        // and blocking the render thread on a queue nobody drains would hang the
        // app. Recycle and move on; the counters record the loss.
        if (_failure != null || _queue.IsAddingCompleted || rgba.Length < _frameBytes)
        {
            pool?.Return(rgba);
            return;
        }

        var frame = new PendingFrame(rgba, frameIndex, pool);

        // Fast path first: only measure the stall when the queue is actually full,
        // so the common case costs nothing but the Add itself.
        if (_queue.TryAdd(frame)) return;

        long stallStart = Stopwatch.GetTimestamp();
        try
        {
            _queue.Add(frame);
        }
        catch (Exception)
        {
            // CompleteAdding raced with us.
            pool?.Return(rgba);
            return;
        }

        _queueStallMilliseconds += (long)Stopwatch.GetElapsedTime(stallStart).TotalMilliseconds;
    }

    public Task<string?> Finish()
    {
        _finish ??= Task.Run(() =>
        {
            _queue.CompleteAdding();
            _thread.Join();
        });

        return _finish.ContinueWith(
            _ => _failure == null ? Options.FilePath : null,
            TaskScheduler.Default);
    }

    public async ValueTask DisposeAsync()
    {
        await Finish().ConfigureAwait(false);
        _queue.Dispose();
    }

    // ── Encoder thread ──────────────────────────────────────────────────────

    void EncodeLoop()
    {
        bool comInitialized = false;
        bool mediaFoundationStarted = false;
        IMFAttributes? attributes = null;
        IMFSinkWriter? writer = null;
        IMFMediaType? outputMediaType = null;
        IMFMediaType? inputMediaType = null;
        IMFMediaBuffer? lastBuffer = null;
        int streamIndex = 0;

        try
        {
            int hr = CoInitializeEx(IntPtr.Zero, 0);
            if (hr == 0 || hr == 1)
                comInitialized = true;
            else if (hr != RPC_E_CHANGED_MODE)
                CheckHr(hr, "CoInitializeEx failed");

            CheckHr(MFStartup(MF_VERSION, MFSTARTUP_FULL), "MFStartup failed");
            mediaFoundationStarted = true;

            attributes = CreateWriterAttributes();
            CheckHr(
                MFCreateSinkWriterFromURL(Options.FilePath, IntPtr.Zero, attributes, out writer),
                "MFCreateSinkWriterFromURL failed");

            ConfigureStream(writer, out streamIndex, out outputMediaType, out inputMediaType);
            CheckHr(writer.BeginWriting(), "BeginWriting failed");

            DrainQueue(writer, streamIndex, ref lastBuffer);

            CheckHr(writer.Finalize_(), "Finalize_ failed");
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _failure, ex);
            DeviceServices.BaseApp?.AddLog(LogType.Error, $"Video encoder failed: {ex}");

            // Nobody will consume the queue any more, so release whatever the
            // render thread already handed over and unblock it if it is waiting.
            foreach (var pending in _queue.GetConsumingEnumerable())
                pending.Pool?.Return(pending.Buffer);
        }
        finally
        {
            ReleaseCom(lastBuffer);
            ReleaseCom(inputMediaType);
            ReleaseCom(outputMediaType);
            ReleaseCom(writer);
            ReleaseCom(attributes);

            if (mediaFoundationStarted) MFShutdown();
            if (comInitialized) CoUninitialize();
        }
    }

    /// <summary>
    /// Pull frames until the producer is done, writing one output sample per slot
    /// on the constant-rate timeline. Gaps left by a render hitch are filled by
    /// re-submitting the previous frame's buffer, which H.264 turns into a nearly
    /// free skip frame, so the file always plays back at the requested fps with
    /// correct wall-clock timing.
    /// </summary>
    void DrainQueue(IMFSinkWriter writer, int streamIndex, ref IMFMediaBuffer? lastBuffer)
    {
        long nextIndex = 0;

        foreach (var frame in _queue.GetConsumingEnumerable())
        {
            try
            {
                while (nextIndex < frame.Index && lastBuffer != null)
                {
                    WriteSample(writer, streamIndex, lastBuffer, nextIndex);
                    nextIndex++;
                    _duplicatedFrames++;
                }

                var buffer = CreateFrameBuffer(frame.Buffer);
                WriteSample(writer, streamIndex, buffer, nextIndex);
                nextIndex++;

                // The previous buffer may still be referenced by samples the sink
                // writer has queued, so only our own reference is dropped here and
                // COM keeps it alive as long as it is needed.
                ReleaseCom(lastBuffer);
                lastBuffer = buffer;
            }
            finally
            {
                frame.Pool?.Return(frame.Buffer);
            }
        }
    }

    IMFAttributes CreateWriterAttributes()
    {
        CheckHr(MFCreateAttributes(out var attributes, 4), "MFCreateAttributes failed");

        // Hardware transforms are what makes 1080p30 affordable; without them the
        // software H.264 encoder becomes the bottleneck and the capture ring
        // starts applying back pressure to the render loop.
        CheckHr(
            attributes.SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1),
            "Failed to enable hardware transforms");
        CheckHr(
            attributes.SetUINT32(MF_SINK_WRITER_DISABLE_THROTTLING, 1),
            "Failed to disable sink writer throttling");
        // Stated explicitly rather than inferred from the URL's extension, so a
        // caller-supplied output path still lands in an MP4 container. The sink
        // writer validates the GUID up front: a wrong value here fails
        // MFCreateSinkWriterFromURL with MF_E_CANNOT_CREATE_SINK (0xC00D36FA).
        CheckHr(
            attributes.SetGUID(MF_TRANSCODE_CONTAINERTYPE, MFTranscodeContainerType_MPEG4),
            "Failed to set container type");

        return attributes;
    }

    void ConfigureStream(IMFSinkWriter writer, out int streamIndex,
        out IMFMediaType outputMediaType, out IMFMediaType inputMediaType)
    {
        int width = Options.Width;
        int height = Options.Height;
        int fps = Math.Max(1, Options.FramesPerSecond);

        CheckHr(MFCreateMediaType(out outputMediaType), "MFCreateMediaType for output failed");
        CheckHr(outputMediaType.SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video), "Failed to set output major type");
        CheckHr(outputMediaType.SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264), "Failed to set output subtype");
        CheckHr(outputMediaType.SetUINT32(MF_MT_AVG_BITRATE, Options.Bitrate), "Failed to set output bitrate");
        CheckHr(outputMediaType.SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive), "Failed to set output interlace mode");
        CheckHr(outputMediaType.SetUINT32(MF_MT_MPEG2_PROFILE, H264ProfileHigh), "Failed to set output profile");
        CheckHr(MFSetAttributeSize(outputMediaType, MF_MT_FRAME_SIZE, width, height), "Failed to set output frame size");
        CheckHr(MFSetAttributeRatio(outputMediaType, MF_MT_FRAME_RATE, fps, 1), "Failed to set output frame rate");
        CheckHr(MFSetAttributeRatio(outputMediaType, MF_MT_PIXEL_ASPECT_RATIO, 1, 1), "Failed to set output pixel aspect ratio");
        CheckHr(writer.AddStream(outputMediaType, out streamIndex), "AddStream failed");

        CheckHr(MFCreateMediaType(out inputMediaType), "MFCreateMediaType for input failed");
        CheckHr(inputMediaType.SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video), "Failed to set input major type");
        CheckHr(inputMediaType.SetGUID(MF_MT_SUBTYPE, MFVideoFormat_RGB32), "Failed to set input subtype");
        CheckHr(inputMediaType.SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive), "Failed to set input interlace mode");
        CheckHr(inputMediaType.SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, 1), "Failed to set input sample independence");
        CheckHr(inputMediaType.SetUINT32(MF_MT_FIXED_SIZE_SAMPLES, 1), "Failed to set input fixed size samples");
        CheckHr(inputMediaType.SetUINT32(MF_MT_SAMPLE_SIZE, _frameBytes), "Failed to set input sample size");
        CheckHr(MFSetAttributeSize(inputMediaType, MF_MT_FRAME_SIZE, width, height), "Failed to set input frame size");
        CheckHr(MFSetAttributeRatio(inputMediaType, MF_MT_FRAME_RATE, fps, 1), "Failed to set input frame rate");
        CheckHr(MFSetAttributeRatio(inputMediaType, MF_MT_PIXEL_ASPECT_RATIO, 1, 1), "Failed to set input pixel aspect ratio");
        CheckHr(writer.SetInputMediaType(streamIndex, inputMediaType, null), "SetInputMediaType failed");
    }

    /// <summary>
    /// Copy one captured frame into a fresh Media Foundation buffer, converting
    /// RGBA to BGRA and flipping it vertically on the way.
    /// <para>
    /// The flip is deliberate rather than a negative MF_MT_DEFAULT_STRIDE: RGB32
    /// samples default to bottom-up orientation, and this reproduces exactly what
    /// the already-proven single-shot MP4 path does, so orientation cannot regress.
    /// </para>
    /// </summary>
    unsafe IMFMediaBuffer CreateFrameBuffer(byte[] rgba)
    {
        CheckHr(MFCreateMemoryBuffer(_frameBytes, out var buffer), "MFCreateMemoryBuffer failed");

        try
        {
            CheckHr(buffer.Lock(out IntPtr destination, out _, out _), "IMFMediaBuffer.Lock failed");
            try
            {
                ConvertTopDownRgbaToBottomUpBgra(rgba, (byte*)destination, Options.Width, Options.Height);
            }
            finally
            {
                buffer.Unlock();
            }

            CheckHr(buffer.SetCurrentLength(_frameBytes), "SetCurrentLength failed");
        }
        catch
        {
            ReleaseCom(buffer);
            throw;
        }

        return buffer;
    }

    void WriteSample(IMFSinkWriter writer, int streamIndex, IMFMediaBuffer buffer, long frameIndex)
    {
        CheckHr(MFCreateSample(out var sample), "MFCreateSample failed");

        try
        {
            CheckHr(sample.AddBuffer(buffer), "AddBuffer failed");
            CheckHr(sample.SetSampleTime(frameIndex * _frameDuration), "SetSampleTime failed");
            CheckHr(sample.SetSampleDuration(_frameDuration), "SetSampleDuration failed");
            CheckHr(writer.WriteSample(streamIndex, sample), "WriteSample failed");
        }
        finally
        {
            ReleaseCom(sample);
        }

        _encodedFrames++;
    }

    static unsafe void ConvertTopDownRgbaToBottomUpBgra(byte[] source, byte* destination, int width, int height)
    {
        int rowBytes = width * 4;

        fixed (byte* origin = source)
        {
            for (int row = 0; row < height; row++)
            {
                SwizzleRow(
                    origin + (long)row * rowBytes,
                    destination + (long)(height - 1 - row) * rowBytes,
                    rowBytes);
            }
        }
    }

    /// <summary>
    /// Swap the red and blue channels of one row. Four pixels at a time when the
    /// hardware has 128-bit shuffles, which is what keeps the per-frame conversion
    /// cost of a 1080p frame in the sub-millisecond range.
    /// </summary>
    static unsafe void SwizzleRow(byte* source, byte* destination, int rowBytes)
    {
        int offset = 0;

        if (Vector128.IsHardwareAccelerated)
        {
            var mask = Vector128.Create(
                (byte)2, 1, 0, 3,
                6, 5, 4, 7,
                10, 9, 8, 11,
                14, 13, 12, 15);

            for (; offset + 16 <= rowBytes; offset += 16)
                Vector128.Shuffle(Vector128.Load(source + offset), mask).Store(destination + offset);
        }

        for (; offset + 4 <= rowBytes; offset += 4)
        {
            destination[offset] = source[offset + 2];
            destination[offset + 1] = source[offset + 1];
            destination[offset + 2] = source[offset];
            destination[offset + 3] = source[offset + 3];
        }
    }

    static void ReleaseCom(object? comObject)
    {
        if (comObject == null) return;

        try
        {
            Marshal.ReleaseComObject(comObject);
        }
        catch (ArgumentException)
        {
            // Not an RCW (possible under COM interop shims); nothing to release.
        }
    }
}
