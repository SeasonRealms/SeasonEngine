// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using AVFoundation;
using CoreAnimation;
using CoreGraphics;
using CoreMedia;
using CoreVideo;
using Foundation;

namespace Season.Platforms.Shared.Apple;

/// <summary>
/// Video playback service for Apple platforms, shared by iOS and MacCatalyst.
/// Uses AVPlayer plus AVPlayerItemVideoOutput for hardware decoding.
/// Audio is handled internally by AVPlayer, and video frames are read directly from CVPixelBuffer.
/// </summary>
internal sealed class AppleVideoPlayerService : IVideoPlayerService
{
    AVPlayer? _player;
    AVPlayerItem? _playerItem;
    AVPlayerItemVideoOutput? _videoOutput;
    NSTimer? _timer;
    int _width, _height;
    bool _isPlaying, _disposed;

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
            var url = NSUrl.FromFilename(filePath);
            var asset = AVAsset.FromUrl(url);

            // Query video dimensions.
            var videoTrack = asset.Tracks.FirstOrDefault(
                t => t.MediaType == AVMediaTypes.Video.GetConstant()!);
            if (videoTrack != null)
            {
                _width = (int)Math.Ceiling(videoTrack.NaturalSize.Width);
                _height = (int)Math.Ceiling(videoTrack.NaturalSize.Height);
            }
            if (_width <= 0) _width = 640;
            if (_height <= 0) _height = 480;

            _playerItem = new AVPlayerItem(asset);

            // Configure BGRA pixel output.
            var outputSettings = new AVVideoSettingsUncompressed
            {
                PixelFormatType = CVPixelFormatType.CV32BGRA
            };
            _videoOutput = new AVPlayerItemVideoOutput(outputSettings);
            _playerItem.AddOutput(_videoOutput);

            _player = new AVPlayer(_playerItem);
            _player.ActionAtItemEnd = AVPlayerActionAtItemEnd.None;

            // Listen for playback completion.
            NSNotificationCenter.DefaultCenter.AddObserver(
                AVPlayerItem.DidPlayToEndTimeNotification,
                _ => OnPlaybackEnded(),
                _playerItem);

            _player.Play();
            _isPlaying = true;

            // Drive frame capture from a display-timed timer.
            _timer = NSTimer.CreateRepeatingScheduledTimer(
                TimeSpan.FromMilliseconds(16),
                CaptureFrame);

            System.Diagnostics.Debug.WriteLine(
                $"[AppleVideo] Started: {_width}x{_height}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[AppleVideo] Play error: {ex.Message}");
        }
    }

    void OnPlaybackEnded()
    {
        _isPlaying = false;
        PlaybackEnded?.Invoke();
    }

    void CaptureFrame(NSTimer timer)
    {
        if (_videoOutput == null || !_isPlaying || _player == null)
            return;

        try
        {
            var outputTime = _videoOutput.GetItemTime(
                CoreAnimation.CAAnimation.CurrentMediaTime());

            if (!_videoOutput.HasNewPixelBufferForItemTime(outputTime))
                return;

            var itemTimeForDisplay = new CMTime();
            using var pixelBuffer = _videoOutput.CopyPixelBuffer(
                outputTime, ref itemTimeForDisplay);
            if (pixelBuffer == null) return;

            pixelBuffer.Lock(CVPixelBufferLock.ReadOnly);
            try
            {
                int w = (int)pixelBuffer.Width;
                int h = (int)pixelBuffer.Height;
                int stride = (int)pixelBuffer.BytesPerRow;
                IntPtr baseAddr = pixelBuffer.BaseAddress;

                if (w <= 0 || h <= 0) return;

                var rgba = new byte[w * h * 4];
                unsafe
                {
                    byte* src = (byte*)baseAddr;
                    for (int y = 0; y < h; y++)
                    {
                        int dstOff = y * w * 4;
                        int srcOff = y * stride;
                        for (int x = 0; x < w; x++)
                        {
                            // BGRA → RGBA
                            rgba[dstOff + x * 4 + 0] = src[srcOff + x * 4 + 2];
                            rgba[dstOff + x * 4 + 1] = src[srcOff + x * 4 + 1];
                            rgba[dstOff + x * 4 + 2] = src[srcOff + x * 4 + 0];
                            rgba[dstOff + x * 4 + 3] = src[srcOff + x * 4 + 3];
                        }
                    }
                }

                VideoFrameAvailable?.Invoke(
                    new NativeImageData(w, h, rgba));
            }
            finally
            {
                pixelBuffer.Unlock(CVPixelBufferLock.ReadOnly);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[AppleVideo] Frame error: {ex.Message}");
        }
    }

    public void Stop()
    {
        _isPlaying = false;
        _timer?.Invalidate();
        _timer = null;
        _player?.Pause();
        _player?.Dispose();
        _player = null;
        _playerItem?.Dispose();
        _playerItem = null;
        _videoOutput?.Dispose();
        _videoOutput = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
