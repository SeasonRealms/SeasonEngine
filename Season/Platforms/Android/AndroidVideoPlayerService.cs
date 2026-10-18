// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Android.Media;
using Android.Views;
using Season.Basic;
using Exception = System.Exception;

namespace Season.Platforms.Android;

/// <summary>
/// Video playback service for Android.
/// Audio is handled by MediaPlayer with system-managed playback.
/// Video is decoded through MediaCodec in surface mode, then YUV planes are read from getOutputImage
/// and converted on the CPU from YUV to RGBA.
/// It has no GLES or EGL dependency and does not conflict with the Vulkan rendering pipeline.
/// Frames are paced on the container presentation timeline so consumers that measure frame
/// arrival spacing (the notice screen drives its reverse bounce from it) see native playback timing.
/// The MediaExtractor/MediaCodec are created, configured and released entirely on the decode
/// thread: MediaCodec.CreateDecoderByType/Configure/Start are synchronous and used to stall the
/// calling (UI) thread for the length of the codec instantiation.
/// </summary>
internal sealed class AndroidVideoPlayerService : IVideoPlayerService
{
    MediaPlayer? _player;
    string? _filePath;
    Thread? _decodeThread;
    volatile bool _isPlaying, _disposed;
    int _width, _height;

    public event Action<INativeImageDecoder>? VideoFrameAvailable;
    public event Action? PlaybackEnded;
    public int VideoWidth => _width;
    public int VideoHeight => _height;
    public bool IsPlaying => _isPlaying;

    public void Play(string filePath)
    {
        _filePath = filePath;

        Stop();
        try
        {
            // Audio: MediaPlayer. Stays on the calling thread - PrepareAsync completes on the
            // platform's own callback thread, so it does not block this one.
            _player = new MediaPlayer();
            _player.SetDataSource(filePath);
            _player.Prepared += (s, e) => _player.Start();
            _player.Completion += (s, e) =>
            {
                _isPlaying = false;
                PlaybackEnded?.Invoke();
            };
            _player.Error += (s, e) =>
                System.Diagnostics.Debug.WriteLine(
                    $"[AVideo] MediaPlayer error: {e.What}");
            _player.PrepareAsync();

            // Video: the extractor and codec are built on the decode thread. The path is
            // captured by the closure so a late Stop cannot redirect this run to the next
            // playback's file.
            _isPlaying = true;
            _decodeThread = new Thread(() => DecodeLoop(filePath))
            {
                Name = "SeasonVidAndroid",
                IsBackground = true
            };
            _decodeThread.Start();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[AVideo] Play error: {ex.Message}");
            Stop();
        }
    }

    void DecodeLoop(string filePath)
    {
        var bufInfo = new MediaCodec.BufferInfo();

        // Pacing state: the first output's presentation time and wall clock, so every later
        // frame can wait until its in-timeline moment instead of being pushed as soon as decoded.
        long firstPtsUs = -1;
        long firstTick = 0;

        // EOS drain state: once the input side ends, keep dequeuing so the frames still
        // buffered in the decoder drain out instead of being discarded at release.
        bool inputDone = false;
        long inputDoneAt = 0;

        // Owned by this thread and never released elsewhere: Stop only signals and joins, so a
        // release can never land underneath a live DequeueOutputBuffer/GetOutputImage call.
        MediaExtractor? extractor = null;
        MediaCodec? codec = null;
        bool started = false;
        try
        {
            // Decoder setup. Runs here so MediaCodec.CreateDecoderByType/Configure/Start do not
            // block the calling thread.
            extractor = new MediaExtractor();
            extractor.SetDataSource(filePath);

            int videoTrack = -1;
            string? mime = null;
            MediaFormat? trackFormat = null;
            for (int i = 0; i < extractor.TrackCount; i++)
            {
                var fmt = extractor.GetTrackFormat(i);
                var m = fmt?.GetString(MediaFormat.KeyMime);
                if (m?.StartsWith("video/", StringComparison.Ordinal) == true)
                {
                    videoTrack = i;
                    mime = m;
                    trackFormat = fmt;
                    _width = fmt!.GetInteger(MediaFormat.KeyWidth);
                    _height = fmt.GetInteger(MediaFormat.KeyHeight);
                    break;
                }
            }
            if (videoTrack < 0 || mime == null || trackFormat == null)
                throw new InvalidOperationException("No video track found.");

            extractor.SelectTrack(videoTrack);
            codec = MediaCodec.CreateDecoderByType(mime);

            // Configure must receive the extractor's track format: MediaCodec's JNI layer
            // hard-asserts (CHECK in android_media_MediaCodec.cpp) on a "mime" entry, and
            // the track format also carries width/height plus the codec-specific data
            // (csd-0/csd-1 SPS/PPS). A fresh empty MediaFormat aborts the whole process -
            // that is a native assertion, not a catchable Java exception.
            codec.Configure(trackFormat, null, null,
                MediaCodecConfigFlags.None);
            codec.Start();

            started = true;

            System.Diagnostics.Debug.WriteLine(
                $"[AVideo] Started: {_width}x{_height}");

            var decoder = codec!;
            var source = extractor!;

            while (_isPlaying && !_disposed)
            {
                // Input: feed encoded data into the decoder.
                if (!inputDone)
                {
                    int inIdx = decoder.DequeueInputBuffer(10_000);
                    if (inIdx >= 0)
                    {
                        var inputBuf = decoder.GetInputBuffer(inIdx);
                        int sampleSize = source.ReadSampleData(inputBuf!, 0);
                        if (sampleSize < 0)
                        {
                            decoder.QueueInputBuffer(inIdx, 0, 0, 0,
                                MediaCodecBufferFlags.EndOfStream);
                            inputDone = true;
                            inputDoneAt = Environment.TickCount64;
                        }
                        else
                        {
                            decoder.QueueInputBuffer(inIdx, 0, sampleSize,
                                source.SampleTime,
                                MediaCodecBufferFlags.None);
                            source.Advance();
                        }
                    }
                }

                // Output: obtain decoded frames through the Image API using direct shared-memory reads.
                int outIdx = decoder.DequeueOutputBuffer(bufInfo, 10_000);
                if (outIdx >= 0)
                {
                    var image = decoder.GetOutputImage(outIdx);
                    if (image != null)
                    {
                        try
                        {
                            int w = image.Width;
                            int h = image.Height;
                            if (w > 0 && h > 0 && (_width != w || _height != h))
                            {
                                _width = w;
                                _height = h;
                            }
                            if (w <= 0 || h <= 0) { image.Close(); continue; }

                            // Read YUV planes from shared memory without any GPU readback cost.
                            var planes = image.GetPlanes();
                            var rgba = ConvertYuv420ToRgba(
                                planes[0].Buffer!,
                                planes[1].Buffer!,
                                planes[2].Buffer!,
                                planes[0].RowStride,
                                planes[1].RowStride,
                                planes[2].RowStride,
                                planes[0].PixelStride,
                                planes[1].PixelStride,
                                w, h);

                            // Pace to the container timeline: consumers measure frame arrival
                            // spacing (the notice screen derives its reverse bounce from it), and
                            // decoding flat out would flash the whole clip in one burst. Waits are
                            // sliced so Stop and Replay stay responsive on low-frame-rate files.
                            if (bufInfo.PresentationTimeUs >= 0)
                            {
                                if (firstPtsUs < 0)
                                {
                                    firstPtsUs = bufInfo.PresentationTimeUs;
                                    firstTick = Environment.TickCount64;
                                }
                                else
                                {
                                    long dueMs = (bufInfo.PresentationTimeUs - firstPtsUs) / 1000;
                                    long wait = dueMs - (Environment.TickCount64 - firstTick);

                                    while (wait > 0 && _isPlaying && !_disposed)
                                    {
                                        Thread.Sleep((int)Math.Min(wait, 100));
                                        wait = dueMs - (Environment.TickCount64 - firstTick);
                                    }
                                }
                            }

                            VideoFrameAvailable?.Invoke(
                                new NativeImageData(w, h, rgba));
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"[AVideo] Image error: {ex.Message}");
                        }
                        finally
                        {
                            image.Close();
                        }
                    }
                    decoder.ReleaseOutputBuffer(outIdx, false);
                }
                else if (outIdx == (int)MediaCodecInfoState.OutputBuffersChanged ||
                         outIdx == (int)MediaCodecInfoState.OutputFormatChanged)
                {
                    // Format change: ignore it. Surface mode rarely uses it, but handling it here is still safe.
                }
                else if (inputDone && Environment.TickCount64 - inputDoneAt > 250)
                {
                    // EOS queued and nothing is left to dequeue: the drain is complete.
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[AVideo] {(started ? "Decode" : "Play")} error: {ex.Message}");
        }
        finally
        {
            _isPlaying = false;

            // Release on this thread: it is the only one that ever touches these objects, so no
            // release can race a live DequeueOutputBuffer/GetOutputImage call.
            try { codec?.Stop(); } catch { }
            codec?.Release();
            codec?.Dispose();
            extractor?.Release();
            extractor?.Dispose();

            // Only a run that reached decoding reports its end; a setup failure is already
            // logged above and must not request a reversal.
            if (started) PlaybackEnded?.Invoke();
        }
    }

    /// <summary>
    /// Converts YUV 420 to RGBA8888 using full-range BT.601 integer arithmetic.
    /// Handles both semi-planar (UV interleaved, the common MediaCodec output where U and V
    /// share a plane and pixel stride is 2) and planar layouts. The planes are bulk-copied out
    /// of their JNI buffers first: per-pixel ByteBuffer.Get costs a JNI call each and pushes a
    /// 688x688 frame into the seconds range, while the copy is a single native read. Indexed
    /// reads are clamped to the copied length because a plane's last row can be shorter than
    /// its row stride (the UV plane's final byte is often one short of the stride x rows
    /// layout, and reading it threw "index out of bounds" which dropped every frame).
    /// </summary>
    static byte[] ConvertYuv420ToRgba(
        Java.Nio.ByteBuffer yBuf,
        Java.Nio.ByteBuffer uBuf,
        Java.Nio.ByteBuffer vBuf,
        int yRowStride, int uRowStride, int vRowStride,
        int yPixelStride, int uPixelStride,
        int width, int height)
    {
        bool uvInterleaved = uBuf == vBuf
            || uRowStride == vRowStride; // semi-planar

        byte[] yPlane = CopyPlane(yBuf);
        byte[] uPlane = CopyPlane(uBuf);
        byte[] vPlane = uvInterleaved ? uPlane : CopyPlane(vBuf);

        var rgba = new byte[width * height * 4];

        for (int j = 0; j < height; j++)
        {
            for (int i = 0; i < width; i++)
            {
                int y = yPlane[Clamp(j * yRowStride + i * yPixelStride, yPlane.Length)] & 0xFF;

                int u, v;
                if (uvInterleaved)
                {
                    int uvOff = (j / 2) * uRowStride + (i / 2) * uPixelStride;
                    u = uPlane[Clamp(uvOff, uPlane.Length)] & 0xFF;
                    v = uPlane[Clamp(uvOff + 1, uPlane.Length)] & 0xFF;
                }
                else
                {
                    u = uPlane[Clamp((j / 2) * uRowStride + (i / 2) * uPixelStride, uPlane.Length)] & 0xFF;
                    v = vPlane[Clamp((j / 2) * vRowStride + (i / 2) * uPixelStride, vPlane.Length)] & 0xFF;
                }

                // ITU-R BT.601 full range (JPEG)
                int c = y - 16;
                int d = u - 128;
                int e = v - 128;

                int r = (298 * c + 409 * e + 128) >> 8;
                int g = (298 * c - 100 * d - 208 * e + 128) >> 8;
                int b = (298 * c + 516 * d + 128) >> 8;

                int idx = (j * width + i) * 4;
                rgba[idx]     = (byte)Math.Clamp(r, 0, 255);
                rgba[idx + 1] = (byte)Math.Clamp(g, 0, 255);
                rgba[idx + 2] = (byte)Math.Clamp(b, 0, 255);
                rgba[idx + 3] = 255;
            }
        }
        return rgba;
    }

    /// <summary>
    /// Copies a MediaCodec plane ByteBuffer into managed memory with one bulk JNI read.
    /// Image planes expose their valid bytes at [0, limit), so rewind first to make the
    /// transfer start there.
    /// </summary>
    static byte[] CopyPlane(Java.Nio.ByteBuffer buffer)
    {
        int length = buffer.Limit();
        var data = new byte[length];
        buffer.Rewind();
        buffer.Get(data, 0, length);
        return data;
    }

    /// <summary>Clamps a plane index to the last copied byte, since stride padding can exceed the buffer length.</summary>
    static int Clamp(int index, int length) => index < length ? index : length - 1;

    public void Replay()
    {
        if (_filePath == null) return;
        var path = _filePath;

        // Stop now joins the decode thread itself, so the old run cannot touch the
        // fresh codec/extractor instances that Play builds next.
        Stop();

        Play(path);
    }

    public void Stop()
    {
        _isPlaying = false;

        // The decode thread owns the extractor/codec and releases them in its own finally
        // block. Releasing them here would race a live DequeueOutputBuffer/GetOutputImage
        // call on that thread - a use-after-free observed as SIGSEGV in the SeasonVidAndroid
        // thread when playback was stopped mid-run. Join it first - the dequeue timeouts and
        // sliced pacing waits keep this bounded. Skipped when Stop re-enters from the decode
        // thread itself (PlaybackEnded loops).
        var thread = _decodeThread;
        if (thread != null && thread != Thread.CurrentThread)
        {
            thread.Join(2000);
        }
        _decodeThread = null;

        // Audio: owned by the calling thread.
        try { _player?.Stop(); } catch { }
        _player?.Release();
        _player?.Dispose();
        _player = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
