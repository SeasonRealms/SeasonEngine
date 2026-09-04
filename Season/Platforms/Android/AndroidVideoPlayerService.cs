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
/// </summary>
internal sealed class AndroidVideoPlayerService : IVideoPlayerService
{
    MediaPlayer? _player;
    MediaCodec? _codec;
    MediaExtractor? _extractor;
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
        Stop();
        try
        {
            // Audio: MediaPlayer.
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

            // Video: MediaCodec.
            _extractor = new MediaExtractor();
            _extractor.SetDataSource(filePath);

            int videoTrack = -1;
            string? mime = null;
            for (int i = 0; i < _extractor.TrackCount; i++)
            {
                var fmt = _extractor.GetTrackFormat(i);
                var m = fmt?.GetString(MediaFormat.KeyMime);
                if (m?.StartsWith("video/", StringComparison.Ordinal) == true)
                {
                    videoTrack = i;
                    mime = m;
                    _width = fmt!.GetInteger(MediaFormat.KeyWidth);
                    _height = fmt.GetInteger(MediaFormat.KeyHeight);
                    break;
                }
            }
            if (videoTrack < 0 || mime == null)
                throw new InvalidOperationException("No video track found.");

            _extractor.SelectTrack(videoTrack);
            _codec = MediaCodec.CreateDecoderByType(mime);
            _codec.Configure(new MediaFormat(), null, null,
                MediaCodecConfigFlags.None);
            _codec.Start();

            // Background decode thread.
            _isPlaying = true;
            _decodeThread = new Thread(DecodeLoop)
            {
                Name = "SeasonVidAndroid",
                IsBackground = true
            };
            _decodeThread.Start();

            System.Diagnostics.Debug.WriteLine(
                $"[AVideo] Started: {_width}x{_height}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[AVideo] Play error: {ex.Message}");
            Stop();
        }
    }

    void DecodeLoop()
    {
        var bufInfo = new MediaCodec.BufferInfo();
        try
        {
            while (_isPlaying && !_disposed && _codec != null && _extractor != null)
            {
                // Input: feed encoded data into the decoder.
                int inIdx = _codec.DequeueInputBuffer(10_000);
                if (inIdx >= 0)
                {
                    var inputBuf = _codec.GetInputBuffer(inIdx);
                    int sampleSize = _extractor.ReadSampleData(inputBuf!, 0);
                    if (sampleSize < 0)
                    {
                        _codec.QueueInputBuffer(inIdx, 0, 0, 0,
                            MediaCodecBufferFlags.EndOfStream);
                        break;
                    }
                    _codec.QueueInputBuffer(inIdx, 0, sampleSize,
                        _extractor.SampleTime,
                        MediaCodecBufferFlags.None);
                    _extractor.Advance();
                }

                // Output: obtain decoded frames through the Image API using direct shared-memory reads.
                int outIdx = _codec.DequeueOutputBuffer(bufInfo, 10_000);
                if (outIdx >= 0)
                {
                    var image = _codec.GetOutputImage(outIdx);
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
                    _codec.ReleaseOutputBuffer(outIdx, false);
                }
                else if (outIdx == (int)MediaCodecInfoState.OutputBuffersChanged ||
                         outIdx == (int)MediaCodecInfoState.OutputFormatChanged)
                {
                    // Format change: ignore it. Surface mode rarely uses it, but handling it here is still safe.
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[AVideo] Decode error: {ex.Message}");
        }
        finally
        {
            _isPlaying = false;
            PlaybackEnded?.Invoke();
        }
    }

    /// <summary>
    /// Converts YUV 420 SemiPlanar to RGBA8888 using full-range BT.601 integer arithmetic.
    /// This matches the standard MediaCodec output COLOR_FormatYUV420SemiPlanar,
    /// where U and V are interleaved in the same plane and pixel stride is 2.
    /// </summary>
    static byte[] ConvertYuv420ToRgba(
        Java.Nio.ByteBuffer yBuf,
        Java.Nio.ByteBuffer uBuf,
        Java.Nio.ByteBuffer vBuf,
        int yRowStride, int uRowStride, int vRowStride,
        int yPixelStride, int uPixelStride,
        int width, int height)
    {
        var rgba = new byte[width * height * 4];
        bool uvInterleaved = uBuf == vBuf
            || uRowStride == vRowStride; // semi-planar

        for (int j = 0; j < height; j++)
        {
            for (int i = 0; i < width; i++)
            {
                int y = yBuf.Get(j * yRowStride + i * yPixelStride) & 0xFF;

                int u, v;
                if (uvInterleaved)
                {
                    int uvOff = (j / 2) * uRowStride + (i / 2) * uPixelStride;
                    u = uBuf.Get(uvOff) & 0xFF;
                    v = uBuf.Get(uvOff + 1) & 0xFF;
                }
                else
                {
                    u = uBuf.Get((j / 2) * uRowStride + (i / 2) * uPixelStride) & 0xFF;
                    v = vBuf.Get((j / 2) * vRowStride + (i / 2) * uPixelStride) & 0xFF;
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

    public void Stop()
    {
        _isPlaying = false;
        try { _player?.Stop(); } catch { }
        _player?.Release();
        _player?.Dispose();
        _player = null;
        try { _codec?.Stop(); } catch { }
        _codec?.Release();
        _codec?.Dispose();
        _codec = null;
        _extractor?.Release();
        _extractor?.Dispose();
        _extractor = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
