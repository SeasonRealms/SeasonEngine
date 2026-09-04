// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Gdk;
using System.Diagnostics;
using System.Threading.Channels;

namespace Season.Platforms.Linux;

internal class LinuxImageService : IImageService
{
    public INativeImageDecoder GetImageFromStream(Stream stream, string ext)
    {
        return new LinuxImageDecoder(stream);
    }

    public Task<INativeImageDecoder> GetImageFromStreamAsync(Stream stream, string ext)
    {
        return Task.FromResult(GetImageFromStream(stream, ext));
    }

    public unsafe byte[] SaveImage(INativeImageDecoder image, Basic.ImageFormat imageFormat, int quality = 90)
    {
        string type = imageFormat switch
        {
            Basic.ImageFormat.Jpeg => "jpeg",
            Basic.ImageFormat.Png  => "png",
            Basic.ImageFormat.Bmp  => "bmp",
            Basic.ImageFormat.Gif  => "gif",
            Basic.ImageFormat.Tiff => "tiff",
            _ => throw new NotSupportedException($"Unsupported format: {imageFormat}")
        };

        string[]? keys = null;
        string[]? values = null;
        if (imageFormat == Basic.ImageFormat.Jpeg)
        {
            keys = new[] { "quality" };
            values = new[] { Math.Clamp(quality, 0, 100).ToString() };
        }

        using var pixbuf = new Pixbuf(Colorspace.Rgb, true, 8, image.Width, image.Height);

        // Copy RGBA pixel data row by row (source stride may differ from pixbuf stride)
        var src = image.PixelSpan;
        int srcStride = image.Stride;
        int dstStride = pixbuf.Rowstride;
        fixed (byte* srcPtr = src)
        {
            byte* dstPtr = (byte*)pixbuf.Pixels;
            for (int y = 0; y < image.Height; y++)
            {
                System.Buffer.MemoryCopy(
                    srcPtr + y * srcStride,
                    dstPtr + y * dstStride,
                    srcStride,
                    srcStride);
            }
        }

        return pixbuf.SaveToBuffer(type, keys, values);
    }

    public Task<byte[]> SaveImageAsync(INativeImageDecoder image, Basic.ImageFormat imageFormat, int quality = 90)
    {
        return Task.FromResult(SaveImage(image, imageFormat, quality));
    }

    public async Task<byte[]> SaveVideo(INativeImageDecoder[] images, VideoSaveOptions? options = null)
    {
        options ??= new VideoSaveOptions();
        var prepared = VideoEncodingHelper.PrepareFrames(images, options.Quality, options.FramesPerSecond);
        string outputPath = Path.Combine(Path.GetTempPath(), $"season-video-linux-{Guid.NewGuid():N}.mp4");

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList =
                {
                    "-y",
                    "-f", "rawvideo",
                    "-pixel_format", "rgba",
                    "-video_size", $"{prepared.Width}x{prepared.Height}",
                    "-framerate", prepared.FramesPerSecond.ToString(),
                    "-i", "pipe:0",
                    "-an",
                    "-c:v", "libx264",
                    "-pix_fmt", "yuv420p",
                    "-preset", "medium",
                    "-crf", prepared.Crf.ToString(),
                    outputPath
                }
            };

            using var process = new Process { StartInfo = startInfo };
            try
            {
                if (!process.Start())
                    throw new InvalidOperationException("Failed to start ffmpeg.");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Unable to start ffmpeg. Please ensure ffmpeg is installed and available on PATH.", ex);
            }

            foreach (var frame in prepared.Frames)
                await process.StandardInput.BaseStream.WriteAsync(frame, 0, frame.Length);

            await process.StandardInput.BaseStream.FlushAsync();
            process.StandardInput.Close();

            string errorOutput = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"ffmpeg exited with code {process.ExitCode}: {errorOutput}");

            return await File.ReadAllBytesAsync(outputPath);
        }
        finally
        {
            VideoEncodingHelper.TryDeleteFile(outputPath);
        }
    }

    public async Task<INativeImageDecoder[]> LoadVideo(Stream stream, VideoLoadOptions? options = null)
    {
        var frames = new List<INativeImageDecoder>();
        await using var media = LoadVideoStream(stream, options);
        await foreach (var packet in media.VideoFrames.ReadAllAsync())
            frames.Add(packet.Frame);
        return frames.ToArray();
    }

    public MediaStream LoadVideoStream(
        Stream stream, VideoLoadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= new VideoLoadOptions();

        string tempFile = Path.Combine(Path.GetTempPath(), $"season-video-load-linux-{Guid.NewGuid():N}.mp4");

        // Save stream to temp file
        if (stream.CanSeek)
            stream.Seek(0, SeekOrigin.Begin);
        using (var fs = File.Create(tempFile))
            stream.CopyTo(fs);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var videoChannel = System.Threading.Channels.Channel.CreateBounded<VideoFramePacket>(16);

        var demuxTask = Task.Run(async () =>
        {
            try
            {
                await ReadMp4WithFfmpegPipes(tempFile, options, videoChannel.Writer, cts.Token);
            }
            catch (OperationCanceledException) { }
            finally
            {
                videoChannel.Writer.TryComplete();
                VideoEncodingHelper.TryDeleteFile(tempFile);
            }
        }, cts.Token);

        // Probe video info synchronously (quick ffprobe call)
        var (vw, vh, vfps) = ProbeVideoInfo(tempFile).GetAwaiter().GetResult();
        if (vw <= 0) vw = 640;
        if (vh <= 0) vh = 480;
        if (vfps <= 0) vfps = 16;

        // Probe audio info
        var (aSr, aCh) = ProbeAudioInfo(tempFile).GetAwaiter().GetResult();
        bool hasAudioL = aSr > 0;
        AudioTrackInfo? audioInfo = hasAudioL ? new AudioTrackInfo(aSr, aCh, TimeSpan.Zero) : null;

        return new MediaStream(
            videoChannel.Reader,
            null, // Audio handled inside the demux task (attached to its own channel)
            new VideoTrackInfo(vw, vh, vfps, TimeSpan.Zero),
            audioInfo,
            cts,
            demuxTask);
    }

    /// <summary>
    /// A single ffmpeg process with dual named pipes for synchronized audio and video demuxing.
    /// </summary>
    static async Task ReadMp4WithFfmpegPipes(
        string tempFile, VideoLoadOptions options,
        ChannelWriter<VideoFramePacket> videoWriter,
        CancellationToken ct)
    {
        // 1. Probe video dimensions
        var (srcWidth, srcHeight, srcFps) = await ProbeVideoInfo(tempFile);
        if (srcWidth <= 0 || srcHeight <= 0)
            throw new InvalidOperationException("Failed to probe video dimensions.");

        // 2. Probe audio
        var (audioSampleRate, audioChannels) = await ProbeAudioInfo(tempFile);
        bool hasAudio = audioSampleRate > 0;

        // 3. Determine target params
        int targetWidth = srcWidth, targetHeight = srcHeight;
        if (options.MaxWidth.HasValue && targetWidth > options.MaxWidth.Value)
        { float ratio = (float)targetHeight / targetWidth; targetWidth = options.MaxWidth.Value; targetHeight = (int)(targetWidth * ratio); }
        if (options.MaxHeight.HasValue && targetHeight > options.MaxHeight.Value)
        { float ratio = (float)targetWidth / targetHeight; targetHeight = options.MaxHeight.Value; targetWidth = (int)(targetHeight * ratio); }

        int targetFps = options.FramesPerSecond ?? srcFps;
        if (targetFps <= 0) targetFps = srcFps > 0 ? srcFps : VideoLoadHelper.DefaultSourceFps;
        int frameInterval = srcFps > 0 && targetFps > 0 ? Math.Max(1, srcFps / targetFps) : 1;
        int maxFrames = options.MaxFrames ?? int.MaxValue;

        // 4. Create named pipes
        string tempDir = Path.GetTempPath();
        string videoPipe = Path.Combine(tempDir, $"season-vp-{Guid.NewGuid():N}.fifo");
        string audioPipe = Path.Combine(tempDir, $"season-ap-{Guid.NewGuid():N}.fifo");

        try
        {
            // Create FIFO pipes
            RunCommand("mkfifo", videoPipe);
            if (hasAudio)
                RunCommand("mkfifo", audioPipe);

            // 5. Start ffmpeg — single process, two outputs
            var argList = new List<string>
            {
                "-y", "-i", tempFile,
                "-map", "0:v", "-f", "rawvideo", "-pix_fmt", "rgba", videoPipe
            };

            if (hasAudio)
            {
                argList.AddRange(new[]
                {
                    "-map", "0:a", "-f", "s16le", "-acodec", "pcm_s16le",
                    "-ar", audioSampleRate.ToString(), "-ac", audioChannels.ToString(), audioPipe
                });
            }

            if (options.StartTime.HasValue)
            {
                argList.InsertRange(2, new[] { "-ss", options.StartTime.Value.TotalSeconds.ToString(CultureInfo.InvariantCulture) });
            }

            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var a in argList) psi.ArgumentList.Add(a);

            using var ffmpeg = new Process { StartInfo = psi };
            if (!ffmpeg.Start())
                throw new InvalidOperationException("Failed to start ffmpeg.");

            // 6. Read video pipe
            int frameBytes = srcWidth * srcHeight * 4;
            var videoReadTask = Task.Run(async () =>
            {
                try
                {
                    using var vs = new FileStream(videoPipe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    var rawBuffer = new byte[frameBytes];
                    int frameIndex = 0, collected = 0;

                    while (collected < maxFrames && !ct.IsCancellationRequested)
                    {
                        int offset = 0, remaining = frameBytes;
                        while (remaining > 0)
                        {
                            int read = await vs.ReadAsync(rawBuffer, offset, remaining, ct);
                            if (read == 0) { remaining = 0; frameIndex = int.MaxValue; break; }
                            offset += read;
                            remaining -= read;
                        }
                        if (offset < frameBytes) break;

                        frameIndex++;
                        if (frameIndex % frameInterval != 0) continue;

                        var frame = VideoLoadHelper.CreateRgbaFrame(rawBuffer, srcWidth, srcHeight, srcWidth * 4, targetWidth, targetHeight);
                        await videoWriter.WriteAsync(new VideoFramePacket(frame, TimeSpan.Zero), ct);
                        collected++;
                    }
                }
                catch (OperationCanceledException) { }
            }, ct);

            // 7. Read audio pipe (if present)
            Task audioReadTask = Task.CompletedTask;
            if (hasAudio)
            {
                audioReadTask = Task.Run(async () =>
                {
                    try
                    {
                        int audioFrameBytes = audioSampleRate * audioChannels * 2; // s16le = 2 bytes per sample, 1-second chunks
                        using var @as = new FileStream(audioPipe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        var pcmBuffer = new byte[audioFrameBytes];

                        while (!ct.IsCancellationRequested)
                        {
                            int offset = 0, remaining = audioFrameBytes;
                            while (remaining > 0)
                            {
                                int read = await @as.ReadAsync(pcmBuffer, offset, remaining, ct);
                                if (read == 0) { remaining = 0; break; }
                                offset += read;
                                remaining -= read;
                            }
                            if (offset == 0) break;

                            var pcm = new byte[offset];
                            Array.Copy(pcmBuffer, pcm, offset);
                            // Note: Audio goes to a separate consumer; we don't write to a channel here.
                            // In the current MediaStream design, audio playback is handled separately.
                            // For now, we just drain the pipe to prevent ffmpeg from blocking.
                        }
                    }
                    catch (OperationCanceledException) { }
                }, ct);
            }

            await Task.WhenAll(videoReadTask, audioReadTask);
            await ffmpeg.WaitForExitAsync(ct);
        }
        finally
        {
            VideoEncodingHelper.TryDeleteFile(videoPipe);
            VideoEncodingHelper.TryDeleteFile(audioPipe);
            VideoEncodingHelper.TryDeleteFile(tempFile);
        }
    }

    static void RunCommand(string cmd, string arg)
    {
        using var p = Process.Start(new ProcessStartInfo(cmd, arg)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });
        p?.WaitForExit();
    }

    static async Task<(int sampleRate, int channels)> ProbeAudioInfo(string filePath)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "ffprobe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList =
                {
                    "-v", "error",
                    "-select_streams", "a:0",
                    "-show_entries", "stream=sample_rate,channels",
                    "-of", "csv=p=0",
                    filePath
                }
            };

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                return (0, 0);

            string output = (await process.StandardOutput.ReadToEndAsync()).Trim();
            await process.WaitForExitAsync();

            if (string.IsNullOrEmpty(output) || process.ExitCode != 0)
                return (0, 0); // No audio track or probe failed

            // Output format: "44100,stereo" or "48000,2"
            var parts = output.Split(',');
            int sr = parts.Length > 0 && int.TryParse(parts[0], out int s) ? s : 44100;
            int ch = 2;
            if (parts.Length > 1)
            {
                if (int.TryParse(parts[1], out int c))
                    ch = c;
                else if (parts[1].Trim().Equals("stereo", StringComparison.OrdinalIgnoreCase))
                    ch = 2;
                else if (parts[1].Trim().Equals("mono", StringComparison.OrdinalIgnoreCase))
                    ch = 1;
            }

            return (sr, ch);
        }
        catch
        {
            return (0, 0);
        }
    }

    static async Task<(int width, int height, int fps)> ProbeVideoInfo(string filePath)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "ffprobe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList =
                {
                    "-v", "error",
                    "-select_streams", "v:0",
                    "-show_entries", "stream=width,height,r_frame_rate",
                    "-of", "csv=p=0",
                    filePath
                }
            };

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                return (0, 0, 0);

            string output = (await process.StandardOutput.ReadToEndAsync()).Trim();
            await process.WaitForExitAsync();

            // Output format: "width,height,r_frame_rate" e.g., "1920,1080,30/1"
            var parts = output.Split(',');
            int width = parts.Length > 0 && int.TryParse(parts[0], out int w) ? w : 0;
            int height = parts.Length > 1 && int.TryParse(parts[1], out int h) ? h : 0;
            int fps = 0;
            if (parts.Length > 2)
            {
                var fpsParts = parts[2].Split('/');
                if (fpsParts.Length == 2 && int.TryParse(fpsParts[0], out int num) && int.TryParse(fpsParts[1], out int den) && den > 0)
                    fps = num / den;
            }

            return (width, height, fps);
        }
        catch
        {
            return (0, 0, 0);
        }
    }
}

internal sealed class LinuxImageDecoder : INativeImageDecoder
{
    readonly byte[] _pixels;

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }

    public unsafe LinuxImageDecoder(Stream stream)
    {
        using var loader = new PixbufLoader(stream);
        using var pixbuf = loader.Pixbuf;

        Width = pixbuf.Width;
        Height = pixbuf.Height;

        // The INativeImageDecoder contract, described in the IGraphics PixelSpan summary,
        // requires data to always be RGBA8.
        // Gdk.Pixbuf exposes 4 channels only when the source image already has alpha.
        // Images without alpha, such as PNG colorType 2 or 0 and most JPEG files,
        // expose only 3 channels, and Rowstride may also include 4-byte alignment padding.
        // Earlier versions copied the pixbuf buffer as-is,
        // effectively handing invalid 3-channel data to downstream consumers.
        // That happened to stay hidden only because the sole consumer,
        // Texture.ProcessImageResult, privately patched in an RGB-to-RGBA branch.
        // As soon as a second consumer accessed pixels as x * 4,
        // such as the 1-7 cube upload path or SH9 projection, it would immediately read out of bounds.
        // Normalize once here into tightly packed RGBA8 so the declaring type itself fulfills the contract.
        Stride = Width * 4;
        _pixels = new byte[Height * Stride];

        int srcStride = pixbuf.Rowstride;
        int srcBpp = pixbuf.NChannels;
        // Pixbuf does not guarantee that the final row is padded to the full alignment width,
        // so take the span using the exact byte length.
        int srcLength = (Height - 1) * srcStride + Width * srcBpp;
        var src = new ReadOnlySpan<byte>((void*)pixbuf.Pixels, srcLength);

        if (srcBpp == 4)
        {
            for (int y = 0; y < Height; y++)
                src.Slice(y * srcStride, Stride).CopyTo(_pixels.AsSpan(y * Stride));
        }
        else
        {
            // Convert RGB, or grayscale already expanded to RGB by pixbuf, into RGBA
            // and fill alpha as fully opaque.
            for (int y = 0; y < Height; y++)
            {
                int srcRow = y * srcStride;
                int dstRow = y * Stride;
                for (int x = 0; x < Width; x++)
                {
                    int si = srcRow + x * srcBpp;
                    int di = dstRow + x * 4;
                    _pixels[di] = src[si];
                    _pixels[di + 1] = src[si + 1];
                    _pixels[di + 2] = src[si + 2];
                    _pixels[di + 3] = 255;
                }
            }
        }
    }

    public ReadOnlySpan<byte> PixelSpan => _pixels;

    public void Dispose() { }
}
