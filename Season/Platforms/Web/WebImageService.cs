// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Microsoft.JSInterop;

namespace Season.Platforms.Web;

/// <summary>
/// Web-platform image service implementation.
/// Browser image encoding and decoding depend on asynchronous APIs such as createImageBitmap and Canvas Blob,
/// so the full Web implementation is completed through asynchronous JSInterop calls into seasonWebGPU.js.
/// </summary>
internal class WebImageService(IJSRuntime jsRuntime) : IImageService
{
    readonly IJSRuntime _jsRuntime = jsRuntime ?? throw new ArgumentNullException(nameof(jsRuntime));

    public INativeImageDecoder GetImageFromStream(Stream stream, string ext)
    {
        throw new NotSupportedException("WebImageService.GetImageFromStream is a synchronous API. Browser image decoding depends on asynchronous APIs, so please use GetImageFromStreamAsync instead.");
    }

    public async Task<INativeImageDecoder> GetImageFromStreamAsync(Stream stream, string ext)
    {
        if (stream == null)
            throw new ArgumentNullException(nameof(stream));

        var encodedBytes = await ReadAllBytesAsync(stream);
        var decoded = await _jsRuntime.InvokeAsync<WebDecodedImage>("seasonWebGPU.decodeImageBytes", encodedBytes);

        if (decoded == null || decoded.Width <= 0 || decoded.Height <= 0 || decoded.RgbaData == null || decoded.RgbaData.Length == 0)
            throw new InvalidOperationException("WebImageService: seasonWebGPU.decodeImageBytes returned invalid image data.");

        return new NativeImageData(decoded.Width, decoded.Height, decoded.RgbaData);
    }

    public byte[] SaveImage(INativeImageDecoder image, Season.Basic.ImageFormat imageFormat, int quality = 90)
    {
        throw new NotSupportedException("WebImageService.SaveImage is a synchronous API. Browser image encoding depends on asynchronous APIs, so please use SaveImageAsync instead.");
    }

    public async Task<byte[]> SaveImageAsync(INativeImageDecoder image, Season.Basic.ImageFormat imageFormat, int quality = 90)
    {
        if (image == null)
            throw new ArgumentNullException(nameof(image));

        var rgbaBytes = EnsureRgba8(image);
        var encodedBytes = await _jsRuntime.InvokeAsync<byte[]>(
            "seasonWebGPU.encodeImageBytes",
            rgbaBytes,
            image.Width,
            image.Height,
            GetFormatName(imageFormat),
            Math.Clamp(quality, 1, 100));

        return encodedBytes ?? throw new InvalidOperationException("WebImageService: seasonWebGPU.encodeImageBytes returned null.");
    }

    public async Task<byte[]> SaveVideo(INativeImageDecoder[] images, VideoSaveOptions? options = null)
    {
        options ??= new VideoSaveOptions();
        var prepared = VideoEncodingHelper.PrepareFrames(images, options.Quality, options.FramesPerSecond);
        var encodedBytes = await _jsRuntime.InvokeAsync<byte[]>(
            "seasonWebGPU.encodeVideo",
            prepared.Frames.ToArray(),
            prepared.Width,
            prepared.Height,
            prepared.FramesPerSecond,
            prepared.Quality);

        return encodedBytes ?? throw new InvalidOperationException("WebImageService: seasonWebGPU.encodeVideo returned null.");
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

        var mp4Bytes = ReadAllBytesAsync(stream).GetAwaiter().GetResult();

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var videoChannel = System.Threading.Channels.Channel.CreateBounded<VideoFramePacket>(16);
        var audioChannel = System.Threading.Channels.Channel.CreateBounded<AudioPcmPacket>(32);

        var demuxTask = Task.Run(async () =>
        {
            try
            {
                // Decode video
                var decodedFrames = await _jsRuntime.InvokeAsync<WebDecodedFrame[]>(
                    "seasonWebGPU.decodeVideo",
                    mp4Bytes,
                    options.MaxFrames ?? -1,
                    options.MaxWidth ?? 0,
                    options.MaxHeight ?? 0,
                    options.FramesPerSecond ?? 0,
                    options.StartTime?.TotalSeconds ?? 0);

                if (decodedFrames != null)
                {
                    foreach (var f in decodedFrames)
                    {
                        if (f.RgbaData == null || f.RgbaData.Length == 0) continue;
                        var frame = new NativeImageData(f.Width, f.Height, f.RgbaData);
                        await videoChannel.Writer.WriteAsync(new VideoFramePacket(frame, TimeSpan.Zero), cts.Token);
                    }
                }

                // Decode audio (Web Audio API: decodeAudioData -> PCM)
                try
                {
                    var audioResult = await _jsRuntime.InvokeAsync<WebDecodedAudio?>(
                        "seasonWebGPU.decodeAudioFromVideo",
                        mp4Bytes,
                        options.StartTime?.TotalSeconds ?? 0);

                    if (audioResult != null && audioResult.PcmData != null && audioResult.PcmData.Length > 0)
                    {
                        await audioChannel.Writer.WriteAsync(new AudioPcmPacket(
                            audioResult.PcmData,
                            audioResult.SampleRate > 0 ? audioResult.SampleRate : 44100,
                            audioResult.Channels > 0 ? audioResult.Channels : 2,
                            TimeSpan.Zero), cts.Token);
                    }
                }
                catch
                {
                    // No audio track or decode failed — that's fine
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                videoChannel.Writer.TryComplete();
                audioChannel.Writer.TryComplete();
            }
        }, cts.Token);

        // Probe metadata via JS
        var info = ProbeWebMediaInfo(mp4Bytes).GetAwaiter().GetResult();

        return new MediaStream(
            videoChannel.Reader,
            info.hasAudio ? audioChannel.Reader : null,
            new VideoTrackInfo(info.width, info.height, info.fps, TimeSpan.Zero),
            info.hasAudio ? new AudioTrackInfo(info.sampleRate, info.channels, TimeSpan.Zero) : null,
            cts,
            demuxTask);
    }

    async Task<(int width, int height, int fps, bool hasAudio, int sampleRate, int channels)> ProbeWebMediaInfo(byte[] mp4Bytes)
    {
        try
        {
            return await _jsRuntime.InvokeAsync<(int, int, int, bool, int, int)>(
                "seasonWebGPU.probeMediaInfo", mp4Bytes);
        }
        catch
        {
            return (640, 480, 16, false, 44100, 2);
        }
    }

    static async Task<byte[]> ReadAllBytesAsync(Stream stream)
    {
        if (stream.CanSeek)
            stream.Seek(0, SeekOrigin.Begin);

        if (stream is MemoryStream memoryStream)
            return memoryStream.ToArray();

        using var tempMs = new MemoryStream();
        await stream.CopyToAsync(tempMs);
        return tempMs.ToArray();
    }

    static byte[] EnsureRgba8(INativeImageDecoder image)
    {
        var pixels = image.PixelSpan;
        int pixelCount = image.Width * image.Height;

        if (pixels.Length == pixelCount * 4)
            return pixels.ToArray();

        if (pixels.Length != pixelCount * 3)
        {
            throw new NotSupportedException(
                $"WebImageService.SaveImageAsync supports only RGB/RGBA input. Current pixel length = {pixels.Length}, expected {pixelCount * 3} or {pixelCount * 4}.");
        }

        var rgba = new byte[pixelCount * 4];
        for (int i = 0; i < pixelCount; i++)
        {
            int src = i * 3;
            int dst = i * 4;
            rgba[dst] = pixels[src];
            rgba[dst + 1] = pixels[src + 1];
            rgba[dst + 2] = pixels[src + 2];
            rgba[dst + 3] = 255;
        }

        return rgba;
    }

    static string GetFormatName(Season.Basic.ImageFormat imageFormat)
    {
        return imageFormat switch
        {
            Season.Basic.ImageFormat.Jpeg => "jpeg",
            Season.Basic.ImageFormat.Png => "png",
            Season.Basic.ImageFormat.Bmp => "bmp",
            Season.Basic.ImageFormat.Gif => "gif",
            Season.Basic.ImageFormat.Tiff => "tiff",
            _ => throw new NotSupportedException($"WebImageService does not support SaveImage format: {imageFormat}")
        };
    }

    sealed class WebDecodedImage
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public byte[] RgbaData { get; set; } = [];
    }

    sealed class WebDecodedFrame
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public byte[] RgbaData { get; set; } = [];
    }

    sealed class WebDecodedAudio
    {
        public byte[] PcmData { get; set; } = [];
        public int SampleRate { get; set; }
        public int Channels { get; set; }
    }
}
