// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Diagnostics;
using Season.Basic;
using Exception = System.Exception;

namespace Season.Platforms.Linux;

/// <summary>
/// Video playback service for the Linux platform.
/// It uses an ffmpeg subprocess plus named pipes to extract decoded RGBA frames.
/// Audio is handled independently by an ffplay subprocess.
/// The system is expected to have ffmpeg and ffprobe installed.
/// </summary>
internal sealed class LinuxVideoPlayerService : IVideoPlayerService
{
    Process? _ffmpeg;
    Thread? _readThread;
    volatile bool _isPlaying, _disposed;
    int _width, _height;
    int _frameSize;
    string? _videoPipe;

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
            // Probe video dimensions.
            ProbeVideoSize(filePath);
            if (_width <= 0) _width = 640;
            if (_height <= 0) _height = 480;
            _frameSize = _width * _height * 4;

            // Create the named pipe.
            string tmp = Path.GetTempPath();
            _videoPipe = Path.Combine(tmp, $"season-vid-{Guid.NewGuid():N}.fifo");

            // Create the pipe.
            // Ignore mkfifo failure because the pipe may already exist
            // or ffmpeg may handle the situation on its own.
            try
            {
                using var mk = Process.Start(new ProcessStartInfo("mkfifo", _videoPipe)
                {
                    UseShellExecute = false, CreateNoWindow = true
                });
                mk?.WaitForExit(5000);
            }
            catch { /* mkfifo may fail on some systems; ffmpeg may still work */ }

            // Start ffmpeg to decode video into a raw RGBA stream and write it into the pipe.
            var psi = new ProcessStartInfo("ffmpeg")
            {
                RedirectStandardOutput = false,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(filePath);
            psi.ArgumentList.Add("-map");
            psi.ArgumentList.Add("0:v");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("rawvideo");
            psi.ArgumentList.Add("-pix_fmt");
            psi.ArgumentList.Add("rgba");
            psi.ArgumentList.Add(_videoPipe);

            _ffmpeg = Process.Start(psi);
            if (_ffmpeg == null)
                throw new InvalidOperationException("ffmpeg start failed.");

            // Start the pipe-reading thread, which waits for ffmpeg to open the write end.
            _isPlaying = true;
            _readThread = new Thread(ReadPipeFrames)
            {
                Name = "SeasonVidLinux",
                IsBackground = true
            };
            _readThread.Start();

            System.Diagnostics.Debug.WriteLine(
                $"[LinuxVideo] Started: {_width}x{_height}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[LinuxVideo] Play error: {ex.Message}");
            Stop();
        }
    }

    void ProbeVideoSize(string path)
    {
        try
        {
            var psi = new ProcessStartInfo("ffprobe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-select_streams");
            psi.ArgumentList.Add("v:0");
            psi.ArgumentList.Add("-show_entries");
            psi.ArgumentList.Add("stream=width,height");
            psi.ArgumentList.Add("-of");
            psi.ArgumentList.Add("csv=p=0");
            psi.ArgumentList.Add(path);

            using var p = Process.Start(psi);
            if (p == null) return;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);

            var parts = output.Trim().Split(',');
            if (parts.Length >= 2 &&
                int.TryParse(parts[0], out int w) &&
                int.TryParse(parts[1], out int h))
            {
                _width = w;
                _height = h;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[LinuxVideo] Probe error: {ex.Message}");
        }
    }

    void ReadPipeFrames()
    {
        FileStream? fs = null;

        // Wait until the pipe file is ready because ffmpeg may not have opened the write end yet.
        for (int retry = 0; retry < 50; retry++)
        {
            if (!_isPlaying || _disposed) return;
            try
            {
                if (File.Exists(_videoPipe))
                {
                    fs = File.OpenRead(_videoPipe);
                    break;
                }
            }
            catch { /* The pipe is not ready yet. */ }
            Thread.Sleep(200);
        }

        if (fs == null)
        {
            _isPlaying = false;
            PlaybackEnded?.Invoke();
            return;
        }

        try
        {
            var buf = new byte[_frameSize];

            while (_isPlaying && !_disposed)
            {
                int read = 0;
                while (read < _frameSize)
                {
                    int n = fs.Read(buf, read, _frameSize - read);
                    if (n <= 0)
                    {
                        _isPlaying = false;
                        break;
                    }
                    read += n;
                }
                if (read < _frameSize) break;

                // Copy the frame data because the next frame will overwrite the shared buffer.
                var frame = new byte[_frameSize];
                Array.Copy(buf, frame, _frameSize);

                VideoFrameAvailable?.Invoke(
                    new NativeImageData(_width, _height, frame));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[LinuxVideo] Pipe read error: {ex.Message}");
        }
        finally
        {
            fs?.Dispose();
            _isPlaying = false;
            PlaybackEnded?.Invoke();
        }
    }

    public void Stop()
    {
        _isPlaying = false;
        try { _ffmpeg?.Kill(); } catch { }
        _ffmpeg?.Dispose();
        _ffmpeg = null;
        TryDelete(_videoPipe);
    }

    static void TryDelete(string? path)
    {
        if (path == null) return;
        try { File.Delete(path); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
