// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Diagnostics;
using Season.Basic;
using LibavVideoStream = Season.Platforms.Linux.LinuxAudioDecoder.LibavApi.VideoStream;
using Exception = System.Exception;

namespace Season.Platforms.Linux;

/// <summary>
/// Video playback service for the Linux platform.
/// Two frame backends are chained at runtime, mirroring the audio decoder's design:
/// the first decodes through the FFmpeg 8.x shared libraries (P/Invoke,
/// <see cref="LinuxAudioDecoder.LibavApi.VideoStream"/>), which minimal installs such as WSL
/// carry even when the ffmpeg binary itself is absent; the second spawns the ffmpeg command
/// line tool and reads raw RGBA frames back over a named pipe. The CLI path is the only one
/// that needs ffprobe (for the size probe); when the binaries are missing it is skipped and
/// playback degrades to audio only with a diagnostic log.
/// Both backends deliver frames in real time - the libav reader paces on the container frame
/// rate, the CLI path decodes with ffmpeg's -re - so consumers that measure frame arrival
/// spacing see native playback timing.
/// Audio is handled independently by the platform audio stack (LinuxAudioPlayer).
/// </summary>
internal sealed class LinuxVideoPlayerService : IVideoPlayerService
{
    Process? _ffmpeg;
    string? _filePath;
    Thread? _readThread;
    volatile bool _isPlaying, _disposed;
    int _width, _height;
    int _frameSize;
    string? _videoPipe;

    // There is deliberately no field for the libav stream: it is created before its reader
    // thread starts and disposed inside that thread's exit path, so the service never frees
    // native decode state that a running read may still be using.

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
            // First backend: the FFmpeg 8 shared libraries through P/Invoke. This is the path
            // that works on minimal installs - WSL among them - carrying the libraries but no
            // ffmpeg binary.
            if (TryPlayLibav(filePath))
            {
                return;
            }

            // Second backend: the ffmpeg command line tool.
            PlayWithFfmpegCli(filePath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[LinuxVideo] Play error: {ex.Message}");
            Stop();
        }
    }

    /// <summary>
    /// Opens the file through the libav binding and starts the frame reader thread.
    /// Returns false - leaving the file to the CLI backend - when the shared libraries or
    /// the stream are unavailable. The stream object leaves this method owned by the reader
    /// thread; only a Start failure disposes it here, because the thread then never runs.
    /// </summary>
    bool TryPlayLibav(string filePath)
    {
        LibavVideoStream? stream = null;

        try
        {
            stream = LibavVideoStream.Open(filePath);

            _width = stream.Width;
            _height = stream.Height;
            _frameSize = _width * _height * 4;
            _isPlaying = true;
            _readThread = new Thread(() => ReadLibavFrames(stream))
            {
                Name = "SeasonVidLinux",
                IsBackground = true
            };
            _readThread.Start();

            System.Diagnostics.Debug.WriteLine(
                $"[LinuxVideo] Started (libav): {_width}x{_height}");

            return true;
        }
        catch (Exception ex)
        {
            stream?.Dispose();
            System.Diagnostics.Debug.WriteLine(
                $"[LinuxVideo] libav backend unavailable, using the ffmpeg CLI: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Reader thread of the libav backend: pulls whole RGBA frames off the stream, paced to
    /// the container frame rate so the picture plays in real time, until it ends or playback
    /// stops, then releases the stream - the thread is its sole owner, so
    /// the native decode state is never freed while a read may still be using it. Stop only
    /// raises the flag; Replay joins this thread before the next Play, which is what keeps
    /// its exit path from touching a fresh playback state.
    /// </summary>
    void ReadLibavFrames(LibavVideoStream stream)
    {
        try
        {
            // Pacing matters to the consumers: the notice screen measures the arrival spacing
            // of the forward pass to drive its backward bounce, and the panels replace one
            // texture per frame - decoding flat out would dump the whole clip in one burst.
            int interval = FrameIntervalMs(stream.FrameRate);
            long next = Environment.TickCount64;

            while (_isPlaying && !_disposed)
            {
                if (!stream.TryReadFrame(out var frame))
                {
                    break;
                }

                VideoFrameAvailable?.Invoke(
                    new NativeImageData(stream.Width, stream.Height, frame));

                next += interval;

                int delay = (int)(next - Environment.TickCount64);

                if (delay > 0)
                {
                    Thread.Sleep(delay);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[LinuxVideo] libav frame error: {ex.Message}");
        }
        finally
        {
            stream.Dispose();
            _isPlaying = false;
            PlaybackEnded?.Invoke();
        }
    }

    /// <summary>
    /// Frame interval for real-time delivery, from the container's frame rate. A rate outside
    /// the plausible video range cannot be trusted and falls back to the common 30 fps; the
    /// interval is clamped to [10, 1000] ms so a broken rate can neither spin the reader nor
    /// park it at a delay Stop would wait out.
    /// </summary>
    static int FrameIntervalMs(double frameRate)
    {
        if (frameRate < 1 || frameRate > 240)
        {
            frameRate = 30;
        }

        return (int)Math.Clamp(1000.0 / frameRate, 10, 1000);
    }

    /// <summary>
    /// Second backend: the ffmpeg command line tool decodes the video into a named pipe as a
    /// raw RGBA stream, paced with -re so frames arrive in real time like the libav reader.
    /// ffprobe supplies the size, with a 640x480 default when it is absent.
    /// A missing binary throws out of Process.Start and surfaces through the caller's
    /// diagnostics - which is the expected outcome on machines that only carry the shared
    /// libraries.
    /// </summary>
    void PlayWithFfmpegCli(string filePath)
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
        // -re reads the input at its native frame rate, matching the libav reader's pacing;
        // without it ffmpeg decodes flat out and the whole clip lands in the pipe at once.
        psi.ArgumentList.Add("-re");
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
            $"[LinuxVideo] Started (ffmpeg CLI): {_width}x{_height}");
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

    public void Replay()
    {
        if (_filePath == null) return;
        var path = _filePath;

        // Stop the current run, then wait for the frame reader to fully exit so
        // its exit path cannot reset the fresh playback state. Skipped when
        // Replay is re-entered from the reader thread itself (e.g. a caller
        // looping from the PlaybackEnded event).
        var old = _readThread;
        Stop();
        if (old != null && old != Thread.CurrentThread)
        {
            old.Join(2000);
        }
        _readThread = null;

        Play(path);
    }

    public void Stop()
    {
        _isPlaying = false;

        // The libav reader disposes its own stream on the way out (see ReadLibavFrames), so
        // only the CLI resources are released here - freeing decode state under a running
        // read is exactly what the single-owner design avoids.
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
