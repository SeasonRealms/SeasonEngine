// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Android.Graphics;
using Android.Media;
using System.Threading.Channels;

namespace Season.Platforms.Android;

internal class AndroidImageService : IImageService
{
    public INativeImageDecoder GetImageFromStream(System.IO.Stream stream, string ext)
    {
        return new AndroidImageDecoder(stream);
    }

    public Task<INativeImageDecoder> GetImageFromStreamAsync(System.IO.Stream stream, string ext)
    {
        return Task.FromResult(GetImageFromStream(stream, ext));
    }

    public byte[] SaveImage(INativeImageDecoder image, Basic.ImageFormat imageFormat, int quality = 90)
    {
        Bitmap.CompressFormat compressFormat = imageFormat switch
        {
            Basic.ImageFormat.Jpeg => Bitmap.CompressFormat.Jpeg,
            Basic.ImageFormat.Png  => Bitmap.CompressFormat.Png,
            _ => throw new NotSupportedException($"Android SaveImage does not support format: {imageFormat}")
        };

        var pixels = image.PixelSpan;
        int pixelCount = image.Width * image.Height;
        var ints = new int[pixelCount];

        // RGBA8 → ARGB8888
        for (int i = 0; i < pixelCount; i++)
        {
            int o = i * 4;
            ints[i] = (pixels[o + 3] << 24) | // A
                      (pixels[o]     << 16) | // R
                      (pixels[o + 1] << 8)  | // G
                      (pixels[o + 2]);        // B
        }

        using var bitmap = Bitmap.CreateBitmap(ints, image.Width, image.Height, Bitmap.Config.Argb8888!)
            ?? throw new InvalidOperationException("Failed to create bitmap for encoding.");

        using var ms = new MemoryStream();
        bool ok = bitmap.Compress(compressFormat, quality, ms);
        if (!ok)
            throw new InvalidOperationException($"Bitmap compress failed for format {imageFormat}.");

        return ms.ToArray();
    }

    public Task<byte[]> SaveImageAsync(INativeImageDecoder image, Basic.ImageFormat imageFormat, int quality = 90)
    {
        return Task.FromResult(SaveImage(image, imageFormat, quality));
    }

    public async Task<byte[]> SaveVideo(INativeImageDecoder[] images, VideoSaveOptions? options = null)
    {
        options ??= new VideoSaveOptions();
        var prepared = VideoEncodingHelper.PrepareFrames(images, options.Quality, options.FramesPerSecond);
        string outputPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"season-video-android-{Guid.NewGuid():N}.mp4");

        MediaCodec? encoder = null;
        MediaMuxer? muxer = null;

        try
        {
            encoder = MediaCodec.CreateEncoderByType(MediaFormat.MimetypeVideoAvc);
            int colorFormat = SelectColorFormat(encoder);

            using var format = MediaFormat.CreateVideoFormat(MediaFormat.MimetypeVideoAvc, prepared.Width, prepared.Height);
            format.SetInteger(MediaFormat.KeyColorFormat, colorFormat);
            format.SetInteger(MediaFormat.KeyBitRate, prepared.Bitrate);
            format.SetInteger(MediaFormat.KeyFrameRate, prepared.FramesPerSecond);
            format.SetInteger(MediaFormat.KeyIFrameInterval, 1);

            encoder.Configure(format, null, null, MediaCodecConfigFlags.Encode);
            encoder.Start();

            muxer = new MediaMuxer(outputPath, MuxerOutputType.Mpeg4);
            using var bufferInfo = new MediaCodec.BufferInfo();

            int trackIndex = -1;
            bool muxerStarted = false;
            bool inputDone = false;
            bool outputDone = false;
            int frameIndex = 0;

            while (!outputDone)
            {
                if (!inputDone)
                {
                    int inputBufferIndex = encoder.DequeueInputBuffer(10_000);
                    if (inputBufferIndex >= 0)
                    {
                        var inputBuffer = encoder.GetInputBuffer(inputBufferIndex)
                            ?? throw new InvalidOperationException("MediaCodec returned a null input buffer.");
                        inputBuffer.Clear();

                        if (frameIndex < prepared.Frames.Count)
                        {
                            byte[] yuv = ConvertRgbaToEncoderInput(
                                prepared.Frames[frameIndex],
                                prepared.Width,
                                prepared.Height,
                                colorFormat);
                            inputBuffer.Put(yuv);
                            long ptsUs = 1_000_000L * frameIndex / prepared.FramesPerSecond;
                            encoder.QueueInputBuffer(inputBufferIndex, 0, yuv.Length, ptsUs, MediaCodecBufferFlags.None);
                            frameIndex++;
                        }
                        else
                        {
                            long ptsUs = 1_000_000L * prepared.Frames.Count / prepared.FramesPerSecond;
                            encoder.QueueInputBuffer(inputBufferIndex, 0, 0, ptsUs, MediaCodecBufferFlags.EndOfStream);
                            inputDone = true;
                        }
                    }
                }

                int outputBufferIndex = encoder.DequeueOutputBuffer(bufferInfo, 10_000);
                switch (outputBufferIndex)
                {
                    case (int)MediaCodecInfoState.TryAgainLater:
                        break;
                    case (int)MediaCodecInfoState.OutputFormatChanged:
                        if (muxerStarted)
                            throw new InvalidOperationException("MediaCodec output format changed more than once.");

                        trackIndex = muxer.AddTrack(encoder.OutputFormat!);
                        muxer.Start();
                        muxerStarted = true;
                        break;
                    default:
                        if (outputBufferIndex < 0)
                            break;

                        var outputBuffer = encoder.GetOutputBuffer(outputBufferIndex)
                            ?? throw new InvalidOperationException("MediaCodec returned a null output buffer.");

                        if ((bufferInfo.Flags & MediaCodecBufferFlags.CodecConfig) != 0)
                            bufferInfo.Size = 0;

                        if (bufferInfo.Size > 0)
                        {
                            if (!muxerStarted)
                                throw new InvalidOperationException("MediaMuxer has not started yet.");

                            outputBuffer.Position(bufferInfo.Offset);
                            outputBuffer.Limit(bufferInfo.Offset + bufferInfo.Size);
                            muxer.WriteSampleData(trackIndex, outputBuffer, bufferInfo);
                        }

                        encoder.ReleaseOutputBuffer(outputBufferIndex, false);

                        if ((bufferInfo.Flags & MediaCodecBufferFlags.EndOfStream) != 0)
                            outputDone = true;
                        break;
                }
            }

            return await File.ReadAllBytesAsync(outputPath);
        }
        finally
        {
            try
            {
                encoder?.Stop();
            }
            catch
            {
            }

            encoder?.Release();

            try
            {
                muxer?.Stop();
            }
            catch
            {
            }

            muxer?.Release();
            VideoEncodingHelper.TryDeleteFile(outputPath);
        }
    }

    public async Task<INativeImageDecoder[]> LoadVideo(System.IO.Stream stream, VideoLoadOptions? options = null)
    {
        var frames = new List<INativeImageDecoder>();
        await using var media = LoadVideoStream(stream, options);
        await foreach (var packet in media.VideoFrames.ReadAllAsync())
            frames.Add(packet.Frame);
        return frames.ToArray();
    }

    public MediaStream LoadVideoStream(
        System.IO.Stream stream, VideoLoadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= new VideoLoadOptions();

        string tempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"season-video-load-android-{Guid.NewGuid():N}.mp4");

        // Save stream to temp file
        if (stream.CanSeek)
            stream.Seek(0, System.IO.SeekOrigin.Begin);
        using (var fs = System.IO.File.Create(tempFile))
            stream.CopyTo(fs);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var videoChannel = System.Threading.Channels.Channel.CreateBounded<VideoFramePacket>(16);
        var audioChannel = System.Threading.Channels.Channel.CreateBounded<AudioPcmPacket>(32);

        // Probe metadata
        var (vw, vh, vfps, hasAudio, aSr, aCh) = ProbeAndroidMediaInfo(tempFile);
        if (vw <= 0) vw = 640;
        if (vh <= 0) vh = 480;
        if (vfps <= 0) vfps = 16;

        var demuxTask = Task.Run(async () =>
        {
            try
            {
                await ReadMp4WithMediaExtractor(tempFile, options, videoChannel.Writer,
                    hasAudio ? audioChannel.Writer : null, cts.Token);
            }
            catch (OperationCanceledException) { }
            finally
            {
                videoChannel.Writer.TryComplete();
                if (hasAudio) audioChannel.Writer.TryComplete();
                VideoEncodingHelper.TryDeleteFile(tempFile);
            }
        }, cts.Token);

        return new MediaStream(
            videoChannel.Reader,
            hasAudio ? audioChannel.Reader : null,
            new VideoTrackInfo(vw, vh, vfps, TimeSpan.Zero),
            hasAudio ? new AudioTrackInfo(aSr, aCh, TimeSpan.Zero) : null,
            cts,
            demuxTask);
    }

    /// <summary>
    /// MediaExtractor plus dual MediaCodec decoders for synchronized audio and video demuxing.
    /// MediaExtractor.ReadSampleData returns interleaved packets in PTS order,
    /// which are dispatched by SampleTrackIndex.
    /// </summary>
    static async Task ReadMp4WithMediaExtractor(
        string tempFile, VideoLoadOptions options,
        ChannelWriter<VideoFramePacket> videoWriter,
        ChannelWriter<AudioPcmPacket>? audioWriter,
        CancellationToken ct)
    {
        using var extractor = new MediaExtractor();
        extractor.SetDataSource(tempFile);

        // Find video and audio tracks
        int videoTrackIndex = -1, audioTrackIndex = -1;
        int srcWidth = 0, srcHeight = 0, srcFps = 0;
        int audioSampleRate = 44100, audioChannels = 2;

        for (int i = 0; i < extractor.TrackCount; i++)
        {
            var trackFormat = extractor.GetTrackFormat(i);
            if (trackFormat == null) continue;
            string? mime = trackFormat.ContainsKey(MediaFormat.KeyMime)
                ? trackFormat.GetString(MediaFormat.KeyMime) : null;
            if (mime == null) continue;

            if (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase) && videoTrackIndex < 0)
            {
                videoTrackIndex = i;
                srcWidth = trackFormat.GetInteger(MediaFormat.KeyWidth);
                srcHeight = trackFormat.GetInteger(MediaFormat.KeyHeight);
                srcFps = trackFormat.ContainsKey(MediaFormat.KeyFrameRate)
                    ? trackFormat.GetInteger(MediaFormat.KeyFrameRate) : 0;
            }
            else if (mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) && audioTrackIndex < 0)
            {
                audioTrackIndex = i;
                audioSampleRate = trackFormat.ContainsKey(MediaFormat.KeySampleRate)
                    ? trackFormat.GetInteger(MediaFormat.KeySampleRate) : 44100;
                audioChannels = trackFormat.ContainsKey(MediaFormat.KeyChannelCount)
                    ? trackFormat.GetInteger(MediaFormat.KeyChannelCount) : 2;
            }
        }

        if (videoTrackIndex < 0)
            throw new InvalidOperationException("No video track found.");

        extractor.SelectTrack(videoTrackIndex);
        bool hasAudio = audioTrackIndex >= 0 && audioWriter != null;
        if (hasAudio)
            extractor.SelectTrack(audioTrackIndex);

        // Target params
        int targetWidth = srcWidth, targetHeight = srcHeight;
        if (options.MaxWidth.HasValue && targetWidth > options.MaxWidth.Value)
        { float r = (float)targetHeight / targetWidth; targetWidth = options.MaxWidth.Value; targetHeight = (int)(targetWidth * r); }
        if (options.MaxHeight.HasValue && targetHeight > options.MaxHeight.Value)
        { float r = (float)targetWidth / targetHeight; targetHeight = options.MaxHeight.Value; targetWidth = (int)(targetHeight * r); }

        int targetFps = options.FramesPerSecond ?? srcFps;
        if (targetFps <= 0) targetFps = srcFps > 0 ? srcFps : VideoLoadHelper.DefaultSourceFps;
        int frameInterval = srcFps > 0 && targetFps > 0 ? Math.Max(1, srcFps / targetFps) : 1;
        int maxFrames = options.MaxFrames ?? int.MaxValue;

        if (options.StartTime.HasValue)
            extractor.SeekTo((long)(options.StartTime.Value.TotalMicroseconds), MediaExtractorSeekTo.PreviousSync);

        // Create video decoder
        using var videoDecoder = MediaCodec.CreateDecoderByType(MediaFormat.MimetypeVideoAvc);
        var vFmt = MediaFormat.CreateVideoFormat(MediaFormat.MimetypeVideoAvc, srcWidth, srcHeight);
        videoDecoder.Configure(vFmt, null, null, MediaCodecConfigFlags.None);
        videoDecoder.Start();

        // Create audio decoder (AAC)
        MediaCodec? audioDecoder = null;
        if (hasAudio)
        {
            audioDecoder = MediaCodec.CreateDecoderByType("audio/mp4a-latm");
            var aFmt = MediaFormat.CreateAudioFormat("audio/mp4a-latm", audioSampleRate, audioChannels);
            audioDecoder.Configure(aFmt, null, null, MediaCodecConfigFlags.None);
            audioDecoder.Start();
        }

        try
        {
            using var vBufInfo = new MediaCodec.BufferInfo();
            using var aBufInfo = new MediaCodec.BufferInfo();
            bool vInputDone = false, vOutputDone = false;
            bool aInputDone = !hasAudio, aOutputDone = !hasAudio;
            int collected = 0, decodedCount = 0;

            while ((!vOutputDone || !aOutputDone) && collected < maxFrames && !ct.IsCancellationRequested)
            {
                // Feed video packets from extractor
                if (!vInputDone)
                {
                    int inIdx = videoDecoder.DequeueInputBuffer(10_000);
                    if (inIdx >= 0)
                    {
                        var inBuf = videoDecoder.GetInputBuffer(inIdx);
                        if (inBuf != null)
                        {
                            int sampleSize = extractor.ReadSampleData(inBuf, 0);
                            if (sampleSize < 0)
                            {
                                videoDecoder.QueueInputBuffer(inIdx, 0, 0, 0, MediaCodecBufferFlags.EndOfStream);
                                vInputDone = true;
                            }
                            else
                            {
                                videoDecoder.QueueInputBuffer(inIdx, 0, sampleSize, extractor.SampleTime, MediaCodecBufferFlags.None);
                                extractor.Advance();
                            }
                        }
                    }
                }

                // Drain video output
                int vOutIdx = videoDecoder.DequeueOutputBuffer(vBufInfo, 5000);
                if (vOutIdx >= 0)
                {
                    if ((vBufInfo.Flags & MediaCodecBufferFlags.EndOfStream) != 0)
                    {
                        videoDecoder.ReleaseOutputBuffer(vOutIdx, false);
                        vOutputDone = true;
                    }
                    else if (vBufInfo.Size > 0)
                    {
                        var outBuf = videoDecoder.GetOutputBuffer(vOutIdx);
                        if (outBuf != null)
                        {
                            decodedCount++;
                            if (decodedCount % frameInterval == 0)
                            {
                                var yuvData = new byte[vBufInfo.Size];
                                outBuf.Position(vBufInfo.Offset);
                                outBuf.Get(yuvData, 0, vBufInfo.Size);
                                outBuf.Position(0);
                                var rgba = ConvertYuvToRgba(yuvData, srcWidth, srcHeight);
                                var frame = VideoLoadHelper.CreateRgbaFrame(rgba, srcWidth, srcHeight, srcWidth * 4, targetWidth, targetHeight);
                                await videoWriter.WriteAsync(new VideoFramePacket(frame, TimeSpan.FromMilliseconds(vBufInfo.PresentationTimeUs / 1000.0)), ct);
                                collected++;
                            }
                        }
                    }
                    videoDecoder.ReleaseOutputBuffer(vOutIdx, false);
                }

                // Feed audio packets (separate extractor read for audio)
                if (audioDecoder != null && hasAudio && !aInputDone)
                {
                    int aInIdx = audioDecoder.DequeueInputBuffer(5000);
                    if (aInIdx >= 0)
                    {
                        var aInBuf = audioDecoder.GetInputBuffer(aInIdx);
                        if (aInBuf != null)
                        {
                            int sampleSize = extractor.ReadSampleData(aInBuf, 0);
                            if (sampleSize < 0)
                            {
                                audioDecoder.QueueInputBuffer(aInIdx, 0, 0, 0, MediaCodecBufferFlags.EndOfStream);
                                aInputDone = true;
                            }
                            else
                            {
                                audioDecoder.QueueInputBuffer(aInIdx, 0, sampleSize, extractor.SampleTime, MediaCodecBufferFlags.None);
                                extractor.Advance();
                            }
                        }
                    }
                }

                // Drain audio output
                if (audioDecoder != null && !aOutputDone)
                {
                    int aOutIdx = audioDecoder.DequeueOutputBuffer(aBufInfo, 5000);
                    if (aOutIdx >= 0)
                    {
                        if ((aBufInfo.Flags & MediaCodecBufferFlags.EndOfStream) != 0)
                        {
                            audioDecoder.ReleaseOutputBuffer(aOutIdx, false);
                            aOutputDone = true;
                        }
                        else if (aBufInfo.Size > 0)
                        {
                            var aOutBuf = audioDecoder.GetOutputBuffer(aOutIdx);
                            if (aOutBuf != null)
                            {
                                var pcm = new byte[aBufInfo.Size];
                                aOutBuf.Position(aBufInfo.Offset);
                                aOutBuf.Get(pcm, 0, aBufInfo.Size);
                                aOutBuf.Position(0);
                                await audioWriter!.WriteAsync(new AudioPcmPacket(pcm, audioSampleRate, audioChannels,
                                    TimeSpan.FromMilliseconds(aBufInfo.PresentationTimeUs / 1000.0)), ct);
                            }
                        }
                        audioDecoder.ReleaseOutputBuffer(aOutIdx, false);
                    }
                }
            }
        }
        finally
        {
            try { audioDecoder?.Stop(); } catch { }
            audioDecoder?.Release();
        }
    }

    static (int width, int height, int fps, bool hasAudio, int sampleRate, int channels) ProbeAndroidMediaInfo(string tempFile)
    {
        try
        {
            using var extractor = new MediaExtractor();
            extractor.SetDataSource(tempFile);

            int w = 0, h = 0, fps = 0;
            bool hasAudio = false;
            int sr = 44100, ch = 2;

            for (int i = 0; i < extractor.TrackCount; i++)
            {
                var fmt = extractor.GetTrackFormat(i);
                if (fmt == null) continue;
                string? mime = fmt.ContainsKey(MediaFormat.KeyMime) ? fmt.GetString(MediaFormat.KeyMime) : null;
                if (mime == null) continue;

                if (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
                {
                    w = fmt.GetInteger(MediaFormat.KeyWidth);
                    h = fmt.GetInteger(MediaFormat.KeyHeight);
                    fps = fmt.ContainsKey(MediaFormat.KeyFrameRate) ? fmt.GetInteger(MediaFormat.KeyFrameRate) : 0;
                }
                else if (mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
                {
                    hasAudio = true;
                    sr = fmt.ContainsKey(MediaFormat.KeySampleRate) ? fmt.GetInteger(MediaFormat.KeySampleRate) : 44100;
                    ch = fmt.ContainsKey(MediaFormat.KeyChannelCount) ? fmt.GetInteger(MediaFormat.KeyChannelCount) : 2;
                }
            }

            return (w, h, fps, hasAudio, sr, ch);
        }
        catch
        {
            return (0, 0, 0, false, 44100, 2);
        }
    }

    static byte[] ConvertYuvToRgba(byte[] yuv, int width, int height)
    {
        // Assume NV12 (YUV420 semi-planar) - most common on Android
        int frameSize = width * height;
        var rgba = new byte[frameSize * 4];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int yVal = yuv[y * width + x] & 0xFF;
                int uvIndex = frameSize + (y / 2) * width + (x & ~1);
                int uVal = uvIndex < yuv.Length ? yuv[uvIndex] & 0xFF : 128;
                int vVal = uvIndex + 1 < yuv.Length ? yuv[uvIndex + 1] & 0xFF : 128;

                int c = yVal - 16;
                int d = uVal - 128;
                int e = vVal - 128;

                int r = (298 * c + 409 * e + 128) >> 8;
                int g = (298 * c - 100 * d - 208 * e + 128) >> 8;
                int b = (298 * c + 516 * d + 128) >> 8;

                int idx = (y * width + x) * 4;
                rgba[idx]     = (byte)Math.Clamp(r, 0, 255);
                rgba[idx + 1] = (byte)Math.Clamp(g, 0, 255);
                rgba[idx + 2] = (byte)Math.Clamp(b, 0, 255);
                rgba[idx + 3] = 255;
            }
        }

        return rgba;
    }

    static int SelectColorFormat(MediaCodec encoder)
    {
        var capabilities = encoder.CodecInfo?.GetCapabilitiesForType(MediaFormat.MimetypeVideoAvc)
            ?? throw new InvalidOperationException("Unable to query MediaCodec capabilities for H.264.");
        var formats = capabilities.ColorFormats ?? [];

        int[] preferredFormats =
        [
            (int)MediaCodecCapabilities.Formatyuv420semiplanar,
            (int)MediaCodecCapabilities.Formatyuv420planar,
            (int)MediaCodecCapabilities.Formatyuv420flexible
        ];

        foreach (int format in preferredFormats)
        {
            if (formats.Contains(format))
                return format;
        }

        throw new NotSupportedException("No supported YUV420 input format was found for the H.264 encoder.");
    }

    static byte[] ConvertRgbaToEncoderInput(byte[] rgba, int width, int height, int colorFormat)
    {
        return colorFormat switch
        {
            (int)MediaCodecCapabilities.Formatyuv420semiplanar => ConvertRgbaToNv12(rgba, width, height),
            (int)MediaCodecCapabilities.Formatyuv420planar => ConvertRgbaToI420(rgba, width, height),
            (int)MediaCodecCapabilities.Formatyuv420flexible => ConvertRgbaToNv12(rgba, width, height),
            _ => throw new NotSupportedException($"Unsupported MediaCodec color format: 0x{colorFormat:X8}")
        };
    }

    static byte[] ConvertRgbaToI420(byte[] rgba, int width, int height)
    {
        int frameSize = width * height;
        int chromaSize = frameSize / 4;
        var yuv = new byte[frameSize + chromaSize * 2];

        int yOffset = 0;
        int uOffset = frameSize;
        int vOffset = frameSize + chromaSize;

        for (int y = 0; y < height; y += 2)
        {
            for (int x = 0; x < width; x += 2)
            {
                int uSum = 0;
                int vSum = 0;

                for (int dy = 0; dy < 2; dy++)
                {
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int px = x + dx;
                        int py = y + dy;
                        int src = (py * width + px) * 4;

                        int r = rgba[src];
                        int g = rgba[src + 1];
                        int b = rgba[src + 2];

                        yuv[yOffset + py * width + px] = ToByte(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
                        uSum += ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
                        vSum += ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;
                    }
                }

                int chromaIndex = (y / 2) * (width / 2) + (x / 2);
                yuv[uOffset + chromaIndex] = ToByte(uSum / 4);
                yuv[vOffset + chromaIndex] = ToByte(vSum / 4);
            }
        }

        return yuv;
    }

    static byte[] ConvertRgbaToNv12(byte[] rgba, int width, int height)
    {
        int frameSize = width * height;
        var yuv = new byte[frameSize + frameSize / 2];

        int uvOffset = frameSize;
        for (int y = 0; y < height; y += 2)
        {
            for (int x = 0; x < width; x += 2)
            {
                int uSum = 0;
                int vSum = 0;

                for (int dy = 0; dy < 2; dy++)
                {
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int px = x + dx;
                        int py = y + dy;
                        int src = (py * width + px) * 4;

                        int r = rgba[src];
                        int g = rgba[src + 1];
                        int b = rgba[src + 2];

                        yuv[py * width + px] = ToByte(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
                        uSum += ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
                        vSum += ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;
                    }
                }

                int chromaIndex = uvOffset + (y / 2) * width + x;
                yuv[chromaIndex] = ToByte(uSum / 4);
                yuv[chromaIndex + 1] = ToByte(vSum / 4);
            }
        }

        return yuv;
    }

    static byte ToByte(int value) => (byte)Math.Clamp(value, 0, 255);
}

internal sealed class AndroidImageDecoder : INativeImageDecoder
{
    readonly Bitmap? _bitmap;
    readonly byte[] _pixels;

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }

    public AndroidImageDecoder(System.IO.Stream stream)
    {
        _bitmap = BitmapFactory.DecodeStream(stream)
            ?? throw new InvalidOperationException("Failed to decode image from stream.");
        Width = _bitmap.Width;
        Height = _bitmap.Height;
        Stride = Width * 4;

        _pixels = new byte[Height * Stride];
        var ints = new int[Width * Height];
        _bitmap.GetPixels(ints, 0, Width, 0, 0, Width, Height);

        // ARGB8888 → RGBA8
        for (int i = 0; i < ints.Length; i++)
        {
            int p = ints[i];
            int o = i * 4;
            _pixels[o]     = (byte)((p >> 16) & 0xFF); // R
            _pixels[o + 1] = (byte)((p >> 8)  & 0xFF); // G
            _pixels[o + 2] = (byte)( p        & 0xFF); // B
            _pixels[o + 3] = (byte)((p >> 24) & 0xFF); // A
        }
    }

    public ReadOnlySpan<byte> PixelSpan => _pixels;

    public void Dispose() => _bitmap?.Dispose();
}
