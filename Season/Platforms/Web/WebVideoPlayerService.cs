// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Microsoft.JSInterop;
using Exception = System.Exception;

namespace Season.Platforms.Web;

/// <summary>
/// Web-platform video playback service.
/// Creates a hidden &lt;video&gt; element, Canvas, and requestVideoFrameCallback through JSInterop.
/// Video decoding and audio playback are handled natively by the browser, with no extra dependencies required.
/// Frame data is transferred to the C# side through Base64 encoding.
/// </summary>
internal sealed class WebVideoPlayerService : IVideoPlayerService
{
    readonly IJSInProcessRuntime _js;
    int _width, _height;
    bool _isPlaying, _disposed;

    public event Action<INativeImageDecoder>? VideoFrameAvailable;
    public event Action? PlaybackEnded;
    public int VideoWidth => _width;
    public int VideoHeight => _height;
    public bool IsPlaying => _isPlaying;

    public WebVideoPlayerService(IJSInProcessRuntime jsRuntime)
    {
        _js = jsRuntime;
    }

    public void Play(string filePath)
    {
        Stop();
        try
        {
            _js.InvokeVoid(
                "SeasonVideoPlayer.init",
                filePath,
                DotNetObjectReference.Create(this));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[WebVideo] Play: {ex.Message}");
        }
    }

    /// <summary>JS callback: video metadata is ready.</summary>
    [JSInvokable]
    public void OnReady(int width, int height)
    {
        _width = width;
        _height = height;
        _isPlaying = true;
        System.Diagnostics.Debug.WriteLine(
            $"[WebVideo] Ready: {_width}x{_height}");
    }

    /// <summary>JS callback: new RGBA frame data in Base64 format.</summary>
    [JSInvokable]
    public void OnFrame(string base64Rgba, int width, int height)
    {
        if (_disposed) return;
        try
        {
            var rgba = Convert.FromBase64String(base64Rgba);
            VideoFrameAvailable?.Invoke(
                new NativeImageData(width, height, rgba));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[WebVideo] Frame: {ex.Message}");
        }
    }

    /// <summary>JS callback: playback ended.</summary>
    [JSInvokable]
    public void OnEnded()
    {
        _isPlaying = false;
        PlaybackEnded?.Invoke();
    }

    /// <summary>JS callback: playback error.</summary>
    [JSInvokable]
    public void OnError(string message)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[WebVideo] Error: {message}");
    }

    public void Stop()
    {
        _isPlaying = false;
        try { _js.InvokeVoid("SeasonVideoPlayer.stop"); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
