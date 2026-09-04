// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using AudioToolbox;
using System.Diagnostics;
using System.Threading.Channels;
using UIKit;
using CoreGraphics;
using Foundation;
using ImageIO;
using AVFoundation;
using CoreMedia;
using CoreVideo;

namespace Season.Platforms.Shared.Apple;

internal class AppleImageService : IImageService
{
    public INativeImageDecoder GetImageFromStream(Stream stream, string ext)
    {
        return new iOSImageDecoder(stream);
    }

    public Task<INativeImageDecoder> GetImageFromStreamAsync(Stream stream, string ext)
    {
        return Task.FromResult(GetImageFromStream(stream, ext));
    }

    public unsafe byte[] SaveImage(INativeImageDecoder image, Basic.ImageFormat imageFormat, int quality = 90)
    {
        using var cgImage = CreateCGImage(image);

        switch (imageFormat)
        {
            case Basic.ImageFormat.Jpeg:
            {
                using var uiImage = new UIImage(cgImage);
                using var data = uiImage.AsJPEG(Math.Clamp(quality / 100f, 0f, 1f));
                return data.ToArray();
            }
            case Basic.ImageFormat.Png:
            {
                using var uiImage = new UIImage(cgImage);
                using var data = uiImage.AsPNG();
                return data.ToArray();
            }
            default:
            {
                string uttype = imageFormat switch
                {
                    Basic.ImageFormat.Bmp  => "com.microsoft.bmp",
                    Basic.ImageFormat.Gif  => "com.compuserve.gif",
                    Basic.ImageFormat.Tiff => "public.tiff",
                    _ => throw new NotSupportedException($"Unsupported format: {imageFormat}")
                };

                using var data = new NSMutableData();
                using var dest = CGImageDestination.Create(data, uttype, 1, null)
                    ?? throw new InvalidOperationException($"Failed to create CGImageDestination for {imageFormat}.");
                dest.AddImage(cgImage, new CGImageDestinationOptions());
                if (!dest.Close())
                    throw new InvalidOperationException($"Failed to encode {imageFormat}.");
                return data.ToArray();
            }
        }
    }

    public Task<byte[]> SaveImageAsync(INativeImageDecoder image, Basic.ImageFormat imageFormat, int quality = 90)
    {
        return Task.FromResult(SaveImage(image, imageFormat, quality));
    }

    public async Task<byte[]> SaveVideo(INativeImageDecoder[] images, VideoSaveOptions? options = null)
    {
        options ??= new VideoSaveOptions();
        var prepared = VideoEncodingHelper.PrepareFrames(images, options.Quality, options.FramesPerSecond);
        string outputPath = Path.Combine(Path.GetTempPath(), $"season-video-apple-{Guid.NewGuid():N}.mp4");
        var outputUrl = NSUrl.FromFilename(outputPath);

        try
        {
            NSError? error;
            using var writer = AVAssetWriter.FromUrl(outputUrl, AVFileTypes.Mpeg4.GetConstant()!, out error)
                ?? throw new InvalidOperationException(error?.LocalizedDescription ?? "Failed to create AVAssetWriter.");

            var codecSettings = new AVVideoCodecSettings
            {
                AverageBitRate = prepared.Bitrate,
                MaxKeyFrameInterval = prepared.FramesPerSecond
            };

            var outputSettings = new AVVideoSettingsCompressed
            {
                Codec = AVVideoCodec.H264,
                Width = prepared.Width,
                Height = prepared.Height,
                ExpectedSourceFrameRate = prepared.FramesPerSecond,
                CodecSettings = codecSettings
            };

            using var writerInput = new AVAssetWriterInput(AVMediaTypes.Video.GetConstant()!, outputSettings);
            writerInput.ExpectsMediaDataInRealTime = false;

            var pixelBufferAttributes = new CVPixelBufferAttributes
            {
                PixelFormatType = CVPixelFormatType.CV32BGRA,
                Width = prepared.Width,
                Height = prepared.Height,
                CGImageCompatibility = true,
                CGBitmapContextCompatibility = true
            };

            using var adaptor = new AVAssetWriterInputPixelBufferAdaptor(writerInput, pixelBufferAttributes);

            if (!writer.CanAddInput(writerInput))
                throw new InvalidOperationException("AVAssetWriter cannot accept a video input for H.264 encoding.");

            writer.AddInput(writerInput);

            if (!writer.StartWriting())
                throw new InvalidOperationException(writer.Error?.LocalizedDescription ?? "AVAssetWriter.StartWriting failed.");

            writer.StartSessionAtSourceTime(CMTime.Zero);

            for (int i = 0; i < prepared.Frames.Count; i++)
            {
                while (!writerInput.ReadyForMoreMediaData)
                    await Task.Delay(1).ConfigureAwait(false);

                using var pixelBuffer = CreatePixelBuffer(prepared.Frames[i], prepared.Width, prepared.Height);
                var presentationTime = new CMTime(i, prepared.FramesPerSecond);
                if (!adaptor.AppendPixelBufferWithPresentationTime(pixelBuffer, presentationTime))
                {
                    throw new InvalidOperationException(
                        writer.Error?.LocalizedDescription ??
                        $"Failed to append pixel buffer for frame {i}.");
                }
            }

            writerInput.MarkAsFinished();

            var tcs = new TaskCompletionSource<bool>();
            writer.FinishWriting(() =>
            {
                if (writer.Status == AVAssetWriterStatus.Failed)
                    tcs.TrySetException(new InvalidOperationException(writer.Error?.LocalizedDescription ?? "AVAssetWriter failed."));
                else
                    tcs.TrySetResult(true);
            });

            await tcs.Task.ConfigureAwait(false);
            return await File.ReadAllBytesAsync(outputPath).ConfigureAwait(false);
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

        string tempFile = Path.Combine(Path.GetTempPath(), $"season-video-load-apple-{Guid.NewGuid():N}.mp4");

        // Save stream to temp file
        if (stream.CanSeek)
            stream.Seek(0, SeekOrigin.Begin);
        using (var fs = File.Create(tempFile))
            stream.CopyTo(fs);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var videoChannel = System.Threading.Channels.Channel.CreateBounded<VideoFramePacket>(16);
        var audioChannel = System.Threading.Channels.Channel.CreateBounded<AudioPcmPacket>(32);

        var demuxTask = Task.Run(async () =>
        {
            try
            {
                await ReadMp4WithAVAssetReader(tempFile, options, videoChannel.Writer, audioChannel.Writer, cts.Token);
            }
            catch (OperationCanceledException) { }
            finally
            {
                videoChannel.Writer.TryComplete();
                audioChannel.Writer.TryComplete();
                VideoEncodingHelper.TryDeleteFile(tempFile);
            }
        }, cts.Token);

        // Probe metadata synchronously
        var (vw, vh, vfps, hasAudio, aSr, aCh) = ProbeAssetInfo(tempFile);
        if (vw <= 0) vw = 640;
        if (vh <= 0) vh = 480;
        if (vfps <= 0) vfps = 16;

        AudioTrackInfo? audioInfo = hasAudio ? new AudioTrackInfo(aSr, aCh, TimeSpan.Zero) : null;

        return new MediaStream(
            videoChannel.Reader,
            hasAudio ? audioChannel.Reader : null,
            new VideoTrackInfo(vw, vh, vfps, TimeSpan.Zero),
            audioInfo,
            cts,
            demuxTask);
    }

    /// <summary>
    /// AVAssetReader performs synchronized audio and video demuxing with one reader and two TrackOutput objects.
    /// </summary>
    static async Task ReadMp4WithAVAssetReader(
        string tempFile, VideoLoadOptions options,
        ChannelWriter<VideoFramePacket> videoWriter,
        ChannelWriter<AudioPcmPacket> audioWriter,
        CancellationToken ct)
    {
        var inputUrl = NSUrl.FromFilename(tempFile);
        using var asset = AVAsset.FromUrl(inputUrl)
            ?? throw new InvalidOperationException("Failed to create AVAsset.");

        // Video track
        var videoTracks = asset.Tracks
            .Where(t => t.MediaType == AVMediaTypes.Video.GetConstant()!)
            .ToArray();
        if (videoTracks.Length == 0)
            throw new InvalidOperationException("No video track found.");
        var videoTrack = videoTracks[0];

        var naturalSize = videoTrack.NaturalSize;
        int srcWidth = (int)Math.Ceiling(naturalSize.Width);
        int srcHeight = (int)Math.Ceiling(naturalSize.Height);
        float srcFps = videoTrack.NominalFrameRate;

        // Audio track (optional)
        var audioTracks = asset.Tracks
            .Where(t => t.MediaType == AVMediaTypes.Audio.GetConstant()!)
            .ToArray();
        AVAssetTrack? audioTrack = audioTracks.Length > 0 ? audioTracks[0] : null;

        // Determine target size
        int targetWidth = srcWidth, targetHeight = srcHeight;
        if (options.MaxWidth.HasValue && targetWidth > options.MaxWidth.Value)
        { float r = (float)targetHeight / targetWidth; targetWidth = options.MaxWidth.Value; targetHeight = (int)(targetWidth * r); }
        if (options.MaxHeight.HasValue && targetHeight > options.MaxHeight.Value)
        { float r = (float)targetWidth / targetHeight; targetHeight = options.MaxHeight.Value; targetWidth = (int)(targetHeight * r); }

        int srcFpsInt = srcFps > 0 ? (int)Math.Round(srcFps) : VideoLoadHelper.DefaultSourceFps;
        int targetFps = options.FramesPerSecond ?? srcFpsInt;
        if (targetFps <= 0) targetFps = srcFpsInt > 0 ? srcFpsInt : VideoLoadHelper.DefaultSourceFps;
        int frameInterval = srcFpsInt > 0 && targetFps > 0 ? Math.Max(1, srcFpsInt / targetFps) : 1;
        int maxFrames = options.MaxFrames ?? int.MaxValue;

        NSError? error;
        using var reader = new AVAssetReader(asset, out error)
            ?? throw new InvalidOperationException(error?.LocalizedDescription ?? "Failed to create AVAssetReader.");

        // Video output
        var videoSettings = new AVVideoSettingsUncompressed { PixelFormatType = CVPixelFormatType.CV32BGRA };
        using var videoOutput = new AVAssetReaderTrackOutput(videoTrack, videoSettings.Dictionary);
        reader.AddOutput(videoOutput);

        // Audio output (Linear PCM)
        AVAssetReaderTrackOutput? audioOutput = null;
        if (audioTrack != null)
        {
            var audioSettings = new AudioSettings
            {
                Format = AudioToolbox.AudioFormatType.LinearPCM,
                LinearPcmBigEndian = false,
                LinearPcmFloat = false,
                LinearPcmBitDepth = 16,
                NumberChannels = 2,
                SampleRate = 44100
            };
            audioOutput = new AVAssetReaderTrackOutput(audioTrack, audioSettings);
            reader.AddOutput(audioOutput);
        }

        if (options.StartTime.HasValue)
        {
            var start = new CMTime((long)(options.StartTime.Value.TotalSeconds * 1000), 1000);
            var range = new CMTimeRange { Start = start, Duration = asset.Duration - start };
            reader.TimeRange = range;
        }

        if (!reader.StartReading())
            throw new InvalidOperationException(reader.Error?.LocalizedDescription ?? "Failed to start reading.");

        int decodedCount = 0, collected = 0;

        while (reader.Status == AVAssetReaderStatus.Reading && collected < maxFrames && !ct.IsCancellationRequested)
        {
            // Read video
            using var videoSample = videoOutput.CopyNextSampleBuffer();
            if (videoSample != null)
            {
                decodedCount++;
                if (decodedCount % frameInterval == 0)
                {
                    using var pixelBuffer = videoSample.GetImageBuffer() as CVPixelBuffer;
                    if (pixelBuffer != null)
                    {
                        pixelBuffer.Lock(CVPixelBufferLock.ReadOnly);
                        try
                        {
                            int bw = (int)pixelBuffer.Width, bh = (int)pixelBuffer.Height;
                            int rp = (int)pixelBuffer.BytesPerRow;
                            IntPtr ba = pixelBuffer.BaseAddress;
                            int ds = rp * bh;
                            var bgra = new byte[ds];
                            System.Runtime.InteropServices.Marshal.Copy(ba, bgra, 0, ds);

                            var frame = VideoLoadHelper.CreateRgbaFrameFromBgra(bgra, bw, bh, rp, targetWidth, targetHeight);
                            await videoWriter.WriteAsync(new VideoFramePacket(frame, TimeSpan.Zero), ct);
                            collected++;
                        }
                        finally { pixelBuffer.Unlock(CVPixelBufferLock.ReadOnly); }
                    }
                }
            }

            // Read audio
            if (audioOutput != null)
            {
                using var audioSample = audioOutput.CopyNextSampleBuffer();
                if (audioSample != null)
                {
                    using var blockBuffer = audioSample.GetDataBuffer();
                    if (blockBuffer != null)
                    {
                        nuint dataLength = blockBuffer.DataLength;
                        if (dataLength > 0)
                        {
                            var pcm = new byte[(int)dataLength];
                            var handle = GCHandle.Alloc(pcm, GCHandleType.Pinned);
                            try
                            {
                                nuint offset = 0;
                                var status = blockBuffer.CopyDataBytes(offset, dataLength, handle.AddrOfPinnedObject());
                                if (status == 0) // kCMBlockBufferNoErr
                                    await audioWriter.WriteAsync(new AudioPcmPacket(pcm, 44100, 2, TimeSpan.Zero), ct);
                            }
                            finally { handle.Free(); }
                        }
                    }
                }
            }

            if (videoSample == null && (audioOutput == null || audioOutput.CopyNextSampleBuffer() == null))
                break;
        }
    }

    /// <summary>Probe video and audio track metadata synchronously.</summary>
    static (int width, int height, int fps, bool hasAudio, int sampleRate, int channels) ProbeAssetInfo(string tempFile)
    {
        try
        {
            var url = NSUrl.FromFilename(tempFile);
            using var asset = AVAsset.FromUrl(url);
            if (asset == null) return (0, 0, 0, false, 44100, 2);

            var videoTrack = asset.Tracks.FirstOrDefault(t => t.MediaType == AVMediaTypes.Video.GetConstant()!);
            int w = 0, h = 0, fps = 0;
            if (videoTrack != null)
            {
                w = (int)Math.Ceiling(videoTrack.NaturalSize.Width);
                h = (int)Math.Ceiling(videoTrack.NaturalSize.Height);
                fps = videoTrack.NominalFrameRate > 0 ? (int)Math.Round(videoTrack.NominalFrameRate) : 16;
            }

            var audioTrack = asset.Tracks.FirstOrDefault(t => t.MediaType == AVMediaTypes.Audio.GetConstant()!);
            bool hasAudio = audioTrack != null;
            int sr = 44100, ch = 2;
            // audioTrack does not directly expose sample rate/channels in the .NET binding;
            // use sensible defaults. The actual audio format will be determined during decoding.

            return (w, h, fps, hasAudio, sr, ch);
        }
        catch
        {
            return (0, 0, 0, false, 44100, 2);
        }
    }

    static unsafe CGImage CreateCGImage(INativeImageDecoder image)
    {
        int width = image.Width;
        int height = image.Height;
        int stride = width * 4;

        byte[] buffer = new byte[height * stride];
        image.PixelSpan.CopyTo(buffer);

        using var provider = new CGDataProvider(buffer, 0, buffer.Length);
        using var cs = CGColorSpace.CreateDeviceRGB()
            ?? throw new InvalidOperationException("Failed to create device RGB color space.");

        return new CGImage(
            width, height, 8, 32, stride,
            cs, CGImageAlphaInfo.PremultipliedLast,
            provider, null, false, CGColorRenderingIntent.Default);
    }

    static CVPixelBuffer CreatePixelBuffer(byte[] rgbaFrame, int width, int height)
    {
        var pixelBuffer = new CVPixelBuffer((IntPtr)width, (IntPtr)height, CVPixelFormatType.CV32BGRA);
        pixelBuffer.Lock(CVPixelBufferLock.None);

        try
        {
            byte[] bgra = VideoEncodingHelper.ConvertRgbaToBgra(rgbaFrame);
            Marshal.Copy(bgra, 0, pixelBuffer.BaseAddress, bgra.Length);
        }
        finally
        {
            pixelBuffer.Unlock(CVPixelBufferLock.None);
        }

        return pixelBuffer;
    }
}

internal sealed class iOSImageDecoder : INativeImageDecoder
{
    readonly byte[] _pixels;

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }

    public unsafe iOSImageDecoder(Stream stream)
    {
        using var data = NSData.FromStream(stream)
            ?? throw new InvalidOperationException("Failed to read image data from stream.");
        using var image = UIImage.LoadFromData(data)
            ?? throw new InvalidOperationException("Failed to decode image from data.");
        using var cg = image.CGImage
            ?? throw new InvalidOperationException("CGImage is null after decoding.");

        Width = (int)cg.Width;
        Height = (int)cg.Height;
        Stride = Width * 4;

        _pixels = new byte[Height * Stride];
        fixed (byte* ptr = _pixels)
        {
            using var cs = CGColorSpace.CreateDeviceRGB()
                ?? throw new InvalidOperationException("Failed to create device RGB color space.");
            using var ctx = new CGBitmapContext(
                (IntPtr)ptr, Width, Height, 8, Stride,
                cs, CGImageAlphaInfo.PremultipliedLast);

            ctx.DrawImage(new CGRect(0, 0, Width, Height), cg);
        }
    }

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public ReadOnlySpan<byte> PixelSpan => _pixels;

    public void Dispose() { }
}
