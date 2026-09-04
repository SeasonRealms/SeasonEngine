// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Microsoft.Graphics.Canvas;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace Season.Platforms.Windows;

/// <summary>
/// Video playback service for the Windows platform.
/// Uses Windows.Media.Playback.MediaPlayer for hardware-accelerated decoding.
/// Audio/video synchronization is handled internally by the system, so there is no need to manage
/// WASAPI / MF SourceReader manually.
/// The VideoFrameAvailable event retrieves RGBA frame data through a Win2D CanvasRenderTarget.
/// </summary>
internal sealed class WindowsVideoPlayerService : IVideoPlayerService
{
    MediaPlayer? _player;
    CanvasRenderTarget? _renderTarget;
    int _width, _height;
    bool _isPlaying;
    bool _disposed;

    public event Action<INativeImageDecoder>? VideoFrameAvailable;
    public event Action? PlaybackEnded;

    public int VideoWidth => _width;
    public int VideoHeight => _height;
    public bool IsPlaying => _isPlaying;

    /// <summary>
    /// Starts playback. Initialization is asynchronous, including reading the file and obtaining
    /// the video size. Once complete, VideoFrameAvailable events start being pushed immediately.
    /// </summary>
    public async void Play(string filePath)
    {
        filePath = filePath.Replace("/", "\\");

        Stop();

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(filePath);

            _player = new MediaPlayer
            {
                IsVideoFrameServerEnabled = true,
                IsLoopingEnabled = false,
                AudioCategory = MediaPlayerAudioCategory.Media
            };

            _player.Source = MediaSource.CreateFromStorageFile(file);

            // Wait for MediaOpened to get the video dimensions.
            var opened = new TaskCompletionSource<bool>();
            _player.MediaOpened += (s, e) =>
            {
                var session = _player.PlaybackSession;
                _width = (int)session.NaturalVideoWidth;
                _height = (int)session.NaturalVideoHeight;

                var device = CanvasDevice.GetSharedDevice();
                _renderTarget = new CanvasRenderTarget(device, _width, _height, 96);

                opened.TrySetResult(true);
            };

            _player.MediaFailed += (s, e) =>
            {
                Debug.WriteLine($"[VideoPlayer] MediaFailed: {e.Error} {e.ErrorMessage}");
                opened.TrySetResult(false);
            };

            if (!await opened.Task)
                return;

            _player.VideoFrameAvailable += OnVideoFrameAvailable;
            _player.MediaEnded += OnMediaEnded;

            _player.Play();
            _isPlaying = true;

            Debug.WriteLine($"[VideoPlayer] Started: {_width}x{_height}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VideoPlayer] Play error: {ex.Message}");
        }
    }

    public void Stop()
    {
        _isPlaying = false;

        if (_player != null)
        {
            _player.VideoFrameAvailable -= OnVideoFrameAvailable;
            _player.MediaEnded -= OnMediaEnded;

            try { _player.Pause(); } catch { }
            _player.Dispose();
            _player = null;
        }

        _renderTarget?.Dispose();
        _renderTarget = null;
    }

    void OnVideoFrameAvailable(MediaPlayer sender, object args)
    {
        if (_renderTarget == null || _disposed) return;

        try
        {
            // Copy the video frame into the Win2D render target (GPU copy).
            sender.CopyFrameToVideoSurface(_renderTarget);

            // Read back BGRA8 pixels from the GPU.
            var bgra = _renderTarget.GetPixelBytes();

            // Swap BGRA -> RGBA channels because the engine expects RGBA8.
            var rgba = new byte[bgra.Length];
            for (int i = 0; i < bgra.Length; i += 4)
            {
                rgba[i]     = bgra[i + 2]; // R ← B
                rgba[i + 1] = bgra[i + 1]; // G ← G
                rgba[i + 2] = bgra[i];     // B ← R
                rgba[i + 3] = bgra[i + 3]; // A ← A
            }

            var frame = new NativeImageData(_width, _height, rgba);
            VideoFrameAvailable?.Invoke(frame);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VideoPlayer] Frame error: {ex.Message}");
        }
    }

    void OnMediaEnded(MediaPlayer sender, object args)
    {
        _isPlaying = false;
        Debug.WriteLine("[VideoPlayer] MediaEnded");
        PlaybackEnded?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
