// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Microsoft.UI.Xaml.Controls;
using Silk.NET.Core.Native;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Channels;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Season.Platforms.Windows;

internal class WindowsImageService : IImageService
{
    public INativeImageDecoder GetImageFromStream(Stream stream, string ext)
    {
        return new WindowsImageDecoder(stream);
    }

    public Task<INativeImageDecoder> GetImageFromStreamAsync(Stream stream, string ext)
    {
        return Task.FromResult(GetImageFromStream(stream, ext));
    }

    public byte[] SaveImage(INativeImageDecoder image, Basic.ImageFormat imageFormat, int quality = 90)
    {
        Guid encoderId = imageFormat switch
        {
            Basic.ImageFormat.Jpeg => BitmapEncoder.JpegEncoderId,
            Basic.ImageFormat.Png => BitmapEncoder.PngEncoderId,
            Basic.ImageFormat.Bmp => BitmapEncoder.BmpEncoderId,
            Basic.ImageFormat.Gif => BitmapEncoder.GifEncoderId,
            Basic.ImageFormat.Tiff => BitmapEncoder.TiffEncoderId,
            _ => throw new NotSupportedException($"Unsupported format: {imageFormat}")
        };

        // 1. Prepare encoder options (only JPEG needs quality).
        BitmapPropertySet props = null!;
        if (imageFormat == Basic.ImageFormat.Jpeg)
        {
            float q = Math.Clamp(quality / 100f, 0f, 1f);
            props = new BitmapPropertySet();
            props.Add("ImageQuality",
                new BitmapTypedValue(q, PropertyType.Single));
        }

        // 2. In-memory stream.
        using var ras = new InMemoryRandomAccessStream();

        // 3. Create the encoder (with quality options when needed).
        BitmapEncoder encoder;
        if (props != null)
            encoder = BitmapEncoder.CreateAsync(encoderId, ras, props).GetAwaiter().GetResult();
        else
            encoder = BitmapEncoder.CreateAsync(encoderId, ras).GetAwaiter().GetResult();

        // 4. Feed pixel data.
        // The Windows rendering path decodes textures with Straight alpha, but WinRT encoders are
        // not consistently compatible with Straight alpha when exporting screenshots, so this path
        // still writes out Premultiplied alpha.
        // CaptureApp has already flattened alpha to 255 during readback, so saved screenshots no
        // longer suffer from black-edge artifacts.
        encoder.SetPixelData(
            BitmapPixelFormat.Rgba8,
            BitmapAlphaMode.Premultiplied,
            (uint)image.Width,
            (uint)image.Height,
            96.0, 96.0,
            image.PixelSpan.ToArray());              // Span -> byte[], RGBA unchanged

        // 5. Flush.
        encoder.FlushAsync().GetAwaiter().GetResult();

        // 6. Read back the result.
        ras.Seek(0);
        using var ms = ras.AsStreamForRead();       // Key point: WinRT stream -> .NET Stream
        byte[] buf = new byte[ms.Length];
        ms.ReadExactly(buf, 0, buf.Length);          // .NET 6+; older versions need a read loop
        return buf;
    }

    public Task<byte[]> SaveImageAsync(INativeImageDecoder image, Basic.ImageFormat imageFormat, int quality = 90)
    {
        return Task.FromResult(SaveImage(image, imageFormat, quality));
    }

    public async Task<byte[]> SaveVideo(INativeImageDecoder[] images, VideoSaveOptions? options = null)
    {
        options ??= new VideoSaveOptions();

        var prepared = VideoEncodingHelper.PrepareFrames(images, options.Quality, options.FramesPerSecond);

        var path = StorageService.SubPath(StorageService.DirectoryBase, "Temp");
        var tempDirectory = Path.Combine(path, $"season-video-win-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            string outputPath = Path.Combine(tempDirectory, "output.mp4");
            await Task.Run(() => WriteMp4WithSinkWriter(
                outputPath,
                prepared.Width,
                prepared.Height,
                prepared.Bitrate,
                prepared.FramesPerSecond,
                prepared.Frames,
                static (frame, width, height) => ConvertTopDownRgbaToBottomUpBgra(frame, width, height)));
            return await File.ReadAllBytesAsync(outputPath);
        }
        finally
        {
            VideoEncodingHelper.TryDeleteDirectory(tempDirectory);
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

        var path = StorageService.SubPath(StorageService.DirectoryBase, "Temp");
        var tempDirectory = Path.Combine(path, $"season-video-load-win-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        string tempFile = Path.Combine(tempDirectory, "input.mp4");

        // Save stream to temp file (sync — must complete before demux)
        if (stream.CanSeek)
            stream.Seek(0, SeekOrigin.Begin);
        using (var fs = File.Create(tempFile))
            stream.CopyTo(fs);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var videoChannel = System.Threading.Channels.Channel.CreateBounded<VideoFramePacket>(16);

        // Quick probe: determine whether an audio track exists before deciding whether to create audioChannel.
        var (hasAudio, audioSr, audioCh) = ProbeAudioTrackMf(tempFile);
        System.Threading.Channels.Channel<AudioPcmPacket>? audioChannel =
            hasAudio ? System.Threading.Channels.Channel.CreateBounded<AudioPcmPacket>(32) : null;

        // Run COM-heavy MF demux + decode on a dedicated thread
        var demuxTask = Task.Run(() =>
        {
            try
            {
                ReadMp4WithSourceReader(
                    tempFile, options, videoChannel.Writer,
                    audioChannel, cts.Token);
            }
            catch (OperationCanceledException) { }
            finally
            {
                videoChannel.Writer.TryComplete();
                audioChannel?.Writer.TryComplete();
                VideoEncodingHelper.TryDeleteDirectory(tempDirectory);
            }
        }, cts.Token);

        return new MediaStream(
            videoChannel.Reader,
            audioChannel?.Reader,
            new VideoTrackInfo(0, 0, 16, TimeSpan.Zero),
            hasAudio ? new AudioTrackInfo(audioSr, audioCh, TimeSpan.Zero) : null,
            cts,
            demuxTask);
    }

    /// <summary>
    /// Quickly probes whether an audio track exists and retrieves its sampling parameters
    /// using a lightweight SourceReader probe without starting decoding.
    /// </summary>
    static (bool hasAudio, int sampleRate, int channels) ProbeAudioTrackMf(string filePath)
    {
        IMFSourceReader? sourceReader = null;
        bool mfStarted = false;
        const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);

        try
        {
            int hr = WindowsMediaFoundationInterop.CoInitializeEx(IntPtr.Zero, 0);
            if (hr != 0 && hr != 1 && hr != RPC_E_CHANGED_MODE)
                return (false, 44100, 2);

            WindowsMediaFoundationInterop.CheckHr(
                WindowsMediaFoundationInterop.MFStartup(WindowsMediaFoundationInterop.MF_VERSION, WindowsMediaFoundationInterop.MFSTARTUP_FULL),
                "MFStartup probe");
            mfStarted = true;

            WindowsMediaFoundationInterop.CheckHr(
                WindowsMediaFoundationInterop.MFCreateSourceReaderFromURL(filePath, null, out sourceReader),
                "MFCreateSourceReaderFromURL probe");

            for (int i = 0; i < 10; i++)
            {
                IMFMediaType? nativeType;
                hr = sourceReader.GetNativeMediaType(i, 0, out nativeType);
                if (hr >= 0 && nativeType != null)
                {
                    try
                    {
                        Guid major;
                        nativeType.GetGUID(WindowsMediaFoundationInterop.MF_MT_MAJOR_TYPE, out major);
                        if (major == WindowsMediaFoundationInterop.MFMediaType_Audio)
                        {
                            int sr = 44100, ch = 2;
                            nativeType.GetUINT32(WindowsMediaFoundationInterop.MF_MT_AUDIO_SAMPLES_PER_SECOND, out int _sr);
                            nativeType.GetUINT32(WindowsMediaFoundationInterop.MF_MT_AUDIO_NUM_CHANNELS, out int _ch);
                            sr = _sr > 0 ? _sr : 44100;
                            ch = _ch > 0 ? _ch : 2;
                            return (true, sr, ch);
                        }
                    }
                    finally { ReleaseComObject(nativeType); }
                }
            }

            return (false, 44100, 2);
        }
        catch
        {
            return (false, 44100, 2); // No audio track or probe failed
        }
        finally
        {
            ReleaseComObject(sourceReader);
            if (mfStarted) WindowsMediaFoundationInterop.MFShutdown();
            WindowsMediaFoundationInterop.CoUninitialize();
        }
    }

    /// <summary>
    /// Media Foundation SourceReader for synchronized audio/video demuxing and decoding.
    /// Uses MF_SOURCE_READER_ANY_STREAM to read interleaved video/audio samples
    /// and dispatch them into their respective channels.
    /// </summary>
    static void ReadMp4WithSourceReader(
        string filePath, VideoLoadOptions options,
        ChannelWriter<VideoFramePacket> videoWriter,
        System.Threading.Channels.Channel<AudioPcmPacket>? audioChannel,
        CancellationToken ct)
    {
        IMFSourceReader? sourceReader = null;
        bool mediaFoundationStarted = false;
        bool comInitialized = false;
        const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);
        const long OneSecond = 10_000_000;

        try
        {
            int hr = WindowsMediaFoundationInterop.CoInitializeEx(IntPtr.Zero, 0);
            if (hr == 0 || hr == 1)
                comInitialized = true;
            else if (hr != RPC_E_CHANGED_MODE)
                WindowsMediaFoundationInterop.CheckHr(hr, "CoInitializeEx failed");

            WindowsMediaFoundationInterop.CheckHr(
                WindowsMediaFoundationInterop.MFStartup(
                    WindowsMediaFoundationInterop.MF_VERSION,
                    WindowsMediaFoundationInterop.MFSTARTUP_FULL),
                "MFStartup failed");
            mediaFoundationStarted = true;

            WindowsMediaFoundationInterop.CheckHr(
                WindowsMediaFoundationInterop.MFCreateSourceReaderFromURL(
                    filePath, null, out sourceReader),
                "MFCreateSourceReaderFromURL failed");

            // ── Enumerate all streams and locate the video/audio stream indices. ──
            int videoStreamIndex = -1;
            int audioStreamIndex = -1;
            for (int i = 0; i < 10; i++)
            {
                bool selected;
                hr = sourceReader.GetStreamSelection(i, out selected);
                if (hr < 0) continue;

                IMFMediaType? nativeType;
                hr = sourceReader.GetNativeMediaType(i, 0, out nativeType);
                if (hr >= 0 && nativeType != null)
                {
                    try
                    {
                        Guid major;
                        nativeType.GetGUID(WindowsMediaFoundationInterop.MF_MT_MAJOR_TYPE, out major);
                        if (major == WindowsMediaFoundationInterop.MFMediaType_Video && videoStreamIndex < 0)
                            videoStreamIndex = i;
                        else if (major == WindowsMediaFoundationInterop.MFMediaType_Audio && audioStreamIndex < 0)
                            audioStreamIndex = i;
                    }
                    catch (Exception ex)
                    {

                    }
                    finally { ReleaseComObject(nativeType); }
                }
            }

            if (videoStreamIndex < 0)
                throw new InvalidOperationException("No video stream found in the MP4 file.");

            // Select video stream + request NV12 decoded output
            WindowsMediaFoundationInterop.CheckHr(
                sourceReader.SetStreamSelection(videoStreamIndex, true),
                "SetStreamSelection(video) failed");
            {
                IMFMediaType? partialType = null;
                WindowsMediaFoundationInterop.CheckHr(
                    WindowsMediaFoundationInterop.MFCreateMediaType(out partialType),
                    "MFCreateMediaType for NV12 failed");
                try
                {
                    partialType.SetGUID(WindowsMediaFoundationInterop.MF_MT_MAJOR_TYPE, WindowsMediaFoundationInterop.MFMediaType_Video);
                    partialType.SetGUID(WindowsMediaFoundationInterop.MF_MT_SUBTYPE, WindowsMediaFoundationInterop.MFVideoFormat_NV12);
                    hr = sourceReader.SetCurrentMediaType(videoStreamIndex, IntPtr.Zero, partialType);
                    if (hr < 0)
                        WindowsMediaFoundationInterop.CheckHr(hr, "SetCurrentMediaType for NV12 failed");
                }
                catch (Exception ex)
                {

                }
                finally { ReleaseComObject(partialType); }
            }

            // Select audio stream + request PCM decoded output (if audio track exists)
            int audioSampleRate = 0, audioChannels = 0;
            if (audioStreamIndex >= 0)
            {
                WindowsMediaFoundationInterop.CheckHr(
                    sourceReader.SetStreamSelection(audioStreamIndex, true),
                    "SetStreamSelection(audio) failed");

                // Get native audio format to read sample rate & channels
                IMFMediaType? nativeAudioType;
                hr = sourceReader.GetNativeMediaType(audioStreamIndex, 0, out nativeAudioType);
                if (hr >= 0 && nativeAudioType != null)
                {
                    try
                    {
                        nativeAudioType.GetUINT32(WindowsMediaFoundationInterop.MF_MT_AUDIO_SAMPLES_PER_SECOND, out int _sr);
                        nativeAudioType.GetUINT32(WindowsMediaFoundationInterop.MF_MT_AUDIO_NUM_CHANNELS, out int _ch);
                        audioSampleRate = _sr;
                        audioChannels = _ch;
                    }
                    catch (Exception ex)
                    {

                    }
                    finally { ReleaseComObject(nativeAudioType); }
                }

                if (audioSampleRate <= 0) audioSampleRate = 44100;
                if (audioChannels <= 0) audioChannels = 2;

                IMFMediaType? audioPartialType = null;
                WindowsMediaFoundationInterop.CheckHr(
                    WindowsMediaFoundationInterop.MFCreateMediaType(out audioPartialType),
                    "MFCreateMediaType for PCM failed");
                try
                {
                    audioPartialType.SetGUID(WindowsMediaFoundationInterop.MF_MT_MAJOR_TYPE, WindowsMediaFoundationInterop.MFMediaType_Audio);
                    audioPartialType.SetGUID(WindowsMediaFoundationInterop.MF_MT_SUBTYPE, WindowsMediaFoundationInterop.MFAudioFormat_PCM);
                    hr = sourceReader.SetCurrentMediaType(audioStreamIndex, IntPtr.Zero, audioPartialType);
                    if (hr < 0)
                        WindowsMediaFoundationInterop.CheckHr(hr, "SetCurrentMediaType for PCM failed");
                }
                catch (Exception ex)
                {

                }
                finally { ReleaseComObject(audioPartialType); }
            }

            // ── Read the first frame to initialize video parameters. ──
            // Note: MF_SOURCE_READER_ANY_STREAM may return an audio sample first,
            // so keep reading until the first video frame is obtained.
            int nativeWidth = 0, nativeHeight = 0, nv12Stride = 0, nativeFps = 0;
            const int MF_SOURCE_READER_ANY_STREAM = unchecked((int)0xFFFFFFFE);
            while (nativeWidth <= 0 || nativeHeight <= 0)
            {
                int actualIdx, streamFlags;
                long ts;
                IMFSample? probeSample = null;
                hr = sourceReader.ReadSample(MF_SOURCE_READER_ANY_STREAM, 0, out actualIdx, out streamFlags, out ts, out probeSample);
                if (hr < 0 || probeSample == null)
                    throw new InvalidOperationException($"SourceReader cannot read samples. HR=0x{hr:X8}.");

                try
                {
                    if (actualIdx == videoStreamIndex)
                        ProcessVideoSample(probeSample, sourceReader, videoStreamIndex, options, videoWriter, ref nativeWidth, ref nativeHeight, ref nv12Stride, ref nativeFps, isFirstFrame: true);
                    else if (actualIdx == audioStreamIndex && audioChannel != null)
                        ProcessAudioSample(probeSample, audioSampleRate, audioChannels, ts, audioChannel.Writer);
                }
                catch (Exception ex)
                {

                }
                finally { ReleaseComObject(probeSample); }
            }

            // ── Compute target parameters. ──
            int targetWidth = nativeWidth, targetHeight = nativeHeight;
            if (options.MaxWidth.HasValue && nativeWidth > options.MaxWidth.Value)
            { float r = (float)nativeHeight / nativeWidth; targetWidth = options.MaxWidth.Value; targetHeight = (int)(targetWidth * r); }
            if (options.MaxHeight.HasValue && nativeHeight > options.MaxHeight.Value)
            { float r = (float)nativeWidth / nativeHeight; targetHeight = options.MaxHeight.Value; targetWidth = (int)(targetHeight * r); }

            // NOTE: nv12Stride / nativeWidth / nativeHeight / targetWidth / targetHeight are captured.
            // For subsequent frames, ProcessVideoSample snapshot will need these values.
            // (We set them as local captures available to the loop below.)

            int srcFps = nativeFps > 0 ? nativeFps : VideoLoadHelper.EstimateSourceFps(filePath);
            int targetFps = options.FramesPerSecond ?? srcFps;
            if (targetFps <= 0) targetFps = srcFps > 0 ? srcFps : 16;
            int frameInterval = srcFps > 0 && targetFps > 0 ? Math.Max(1, srcFps / targetFps) : 1;
            int maxFrames = options.MaxFrames ?? int.MaxValue;

            // Seek
            if (options.StartTime.HasValue)
            {
                long hnsPos = (long)(options.StartTime.Value.TotalSeconds * OneSecond);
                var p = Marshal.AllocHGlobal(8);
                try { Marshal.WriteInt64(p, hnsPos); sourceReader.SetCurrentPosition(Guid.Empty, p); }
                catch (Exception ex)
                {

                }
                finally { Marshal.FreeHGlobal(p); }
            }

            // ── Read interleaved audio/video samples. ──
            int videoFrameCount = 1; // first frame already processed
            int videoReads = 1;
            const int MF_EOF = 0x2, MF_ERR = 0x1, MF_TYPECHANGED = 0x20;
            bool videoDone = false, audioDone = (audioChannel == null);

            while ((!videoDone || !audioDone) && !ct.IsCancellationRequested)
            {
                IMFSample? sample = null;
                int actualIdx, streamFlags;
                long timestamp;

                hr = sourceReader.ReadSample(MF_SOURCE_READER_ANY_STREAM, 0, out actualIdx, out streamFlags, out timestamp, out sample);
                if (hr < 0) break;
                if ((streamFlags & (MF_EOF | MF_ERR)) != 0) { ReleaseComObject(sample); break; }
                if ((streamFlags & MF_TYPECHANGED) != 0) { ReleaseComObject(sample); continue; }

                try
                {
                    if (sample == null) break;

                    if (actualIdx == videoStreamIndex)
                    {
                        videoReads++;
                        if (videoFrameCount >= maxFrames) { videoDone = true; continue; }
                        if (videoReads % frameInterval != 0) continue;

                        ProcessVideoSample(sample, sourceReader, videoStreamIndex, options, videoWriter, ref nativeWidth, ref nativeHeight, ref nv12Stride, ref nativeFps, isFirstFrame: false);
                        videoFrameCount++;
                    }
                    else if (actualIdx == audioStreamIndex && audioChannel != null)
                    {
                        ProcessAudioSample(sample, audioSampleRate, audioChannels, timestamp, audioChannel.Writer);
                    }
                }
                catch (Exception ex)
                {

                }
                finally { ReleaseComObject(sample); }
            }
        }
        catch (Exception ex)
        {

        }
        finally
        {
            ReleaseComObject(sourceReader);
            if (mediaFoundationStarted)
                WindowsMediaFoundationInterop.MFShutdown();
            if (comInitialized)
                WindowsMediaFoundationInterop.CoUninitialize();
        }
    }

    static void ProcessVideoSample(
        IMFSample sample, IMFSourceReader sourceReader, int videoStreamIndex,
        VideoLoadOptions options, ChannelWriter<VideoFramePacket> writer,
        ref int nativeWidth, ref int nativeHeight, ref int nv12Stride, ref int nativeFps,
        bool isFirstFrame)
    {
        if (isFirstFrame)
        {
            IMFMediaType? currentType = null;
            if (sourceReader.GetCurrentMediaType(videoStreamIndex, out currentType) >= 0 && currentType != null)
            {
                try
                {
                    long sizeValue;
                    currentType.GetUINT64(WindowsMediaFoundationInterop.MF_MT_FRAME_SIZE, out sizeValue);
                    nativeWidth = (int)(sizeValue >> 32);
                    nativeHeight = (int)(sizeValue & 0xFFFFFFFF);

                    long fpsValue;
                    int hr = currentType.GetUINT64(WindowsMediaFoundationInterop.MF_MT_FRAME_RATE, out fpsValue);
                    if (hr >= 0 && fpsValue != 0)
                    {
                        int fpsNum = (int)(fpsValue >> 32);
                        int fpsDen = (int)(fpsValue & 0xFFFFFFFF);
                        if (fpsDen > 0) nativeFps = fpsNum / fpsDen;
                    }
                }
                catch (Exception ex)
                {

                }
                finally { ReleaseComObject(currentType); }
            }
        }

        IMFMediaBuffer? buf = null;
        if (sample.ConvertToContiguousBuffer(out buf) < 0 || buf == null) return;
        try
        {
            IntPtr scan;
            int maxL, curL;
            if (buf.Lock(out scan, out maxL, out curL) < 0 || scan == IntPtr.Zero) return;
            try
            {
                if (isFirstFrame)
                {
                    nv12Stride = (curL * 2) / (3 * nativeHeight);
                    if (nv12Stride < nativeWidth) nv12Stride = nativeWidth;
                }

                var nv12 = new byte[curL];
                Marshal.Copy(scan, nv12, 0, curL);

                int targetW = nativeWidth, targetH = nativeHeight;
                if (options.MaxWidth.HasValue && targetW > options.MaxWidth.Value)
                { float r = (float)targetH / targetW; targetW = options.MaxWidth.Value; targetH = (int)(targetW * r); }
                if (options.MaxHeight.HasValue && targetH > options.MaxHeight.Value)
                { float r = (float)targetW / targetH; targetH = options.MaxHeight.Value; targetW = (int)(targetH * r); }

                var rgba = VideoLoadHelper.ConvertNv12ToRgba(nv12, nativeWidth, nativeHeight, nv12Stride);
                var frame = VideoLoadHelper.CreateRgbaFrame(rgba, nativeWidth, nativeHeight, nativeWidth * 4, targetW, targetH);

                writer.TryWrite(new VideoFramePacket(frame, TimeSpan.FromTicks(0)));
            }
            catch (Exception ex)
            {

            }
            finally { buf.Unlock(); }
        }
        catch (Exception ex)
        {

        }
        finally { ReleaseComObject(buf); }
    }

    static void ProcessAudioSample(
        IMFSample sample, int sampleRate, int channels, long timestamp,
        ChannelWriter<AudioPcmPacket> writer)
    {
        IMFMediaBuffer? buf;
        if (sample.ConvertToContiguousBuffer(out buf) < 0 || buf == null) return;
        try
        {
            IntPtr scan;
            int maxL, curL;
            if (buf.Lock(out scan, out maxL, out curL) < 0 || scan == IntPtr.Zero) return;
            try
            {
                var pcm = new byte[curL];
                Marshal.Copy(scan, pcm, 0, curL);
                writer.TryWrite(new AudioPcmPacket(pcm, sampleRate, channels, TimeSpan.FromTicks(timestamp)));
            }
            finally { buf.Unlock(); }
        }
        finally { ReleaseComObject(buf); }
    }

    public static string BuildMp4FromDirectorySinkWriter(string path, int width, int height, int bitrate, int framesPerSecond)
    {
        var fullPath = StorageService.SubPath(StorageService.DirectoryBase, path);

        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException("width/height must be positive.");

        if ((width & 1) != 0 || (height & 1) != 0)
            throw new ArgumentException("H.264 on Windows usually requires even width/height.");

        if (framesPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond));

        if (bitrate <= 0)
            throw new ArgumentOutOfRangeException(nameof(bitrate));

        var pngs = Directory.GetFiles(fullPath, "*.png")
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (pngs.Length == 0)
            throw new InvalidOperationException($"No PNG frames were found in '{fullPath}'.");

        string outputPath = Path.Combine(fullPath, "output.mp4");
        if (File.Exists(outputPath))
            File.Delete(outputPath);

        WriteMp4WithSinkWriter(
            outputPath,
            width,
            height,
            bitrate,
            framesPerSecond,
            pngs,
            static (png, frameWidth, frameHeight) => LoadBgraFrame(png, frameWidth, frameHeight));

        return outputPath;
    }

    static void WriteMp4WithSinkWriter<TFrame>(
        string outputPath,
        int width,
        int height,
        int bitrate,
        int framesPerSecond,
        IEnumerable<TFrame> frames,
        Func<TFrame, int, int, byte[]> frameConverter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(frameConverter);

        IMFAttributes? attributes = null;
        IMFSinkWriter? sinkWriter = null;
        IMFMediaType? outputMediaType = null;
        IMFMediaType? inputMediaType = null;
        bool mediaFoundationStarted = false;
        bool comInitialized = false;
        const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);

        try
        {
            int hr = WindowsMediaFoundationInterop.CoInitializeEx(IntPtr.Zero, 0);
            if (hr == 0 || hr == 1)
                comInitialized = true;
            else if (hr != RPC_E_CHANGED_MODE)
                WindowsMediaFoundationInterop.CheckHr(hr, "CoInitializeEx failed");

            WindowsMediaFoundationInterop.CheckHr(
                WindowsMediaFoundationInterop.MFStartup(
                    WindowsMediaFoundationInterop.MF_VERSION,
                    WindowsMediaFoundationInterop.MFSTARTUP_FULL),
                "MFStartup failed");
            mediaFoundationStarted = true;

            WindowsMediaFoundationInterop.CheckHr(
                WindowsMediaFoundationInterop.MFCreateSinkWriterFromURL(
                    outputPath,
                    IntPtr.Zero,
                    null,
                    out sinkWriter),
                "MFCreateSinkWriterFromURL failed");

            int frameBytes = checked(width * height * 4);
            long frameDuration = WindowsMediaFoundationInterop.FrameDurationFromFps(framesPerSecond);

            WindowsMediaFoundationInterop.CheckHr(
                WindowsMediaFoundationInterop.MFCreateMediaType(out outputMediaType),
                "MFCreateMediaType for output failed");
            WindowsMediaFoundationInterop.CheckHr(
                outputMediaType.SetGUID(WindowsMediaFoundationInterop.MF_MT_MAJOR_TYPE, WindowsMediaFoundationInterop.MFMediaType_Video),
                "Failed to set output major type");
            WindowsMediaFoundationInterop.CheckHr(
                outputMediaType.SetGUID(WindowsMediaFoundationInterop.MF_MT_SUBTYPE, WindowsMediaFoundationInterop.MFVideoFormat_H264),
                "Failed to set output subtype");
            WindowsMediaFoundationInterop.CheckHr(
                outputMediaType.SetUINT32(WindowsMediaFoundationInterop.MF_MT_AVG_BITRATE, bitrate),
                "Failed to set output bitrate");
            WindowsMediaFoundationInterop.CheckHr(
                outputMediaType.SetUINT32(WindowsMediaFoundationInterop.MF_MT_INTERLACE_MODE, WindowsMediaFoundationInterop.MFVideoInterlace_Progressive),
                "Failed to set output interlace mode");
            WindowsMediaFoundationInterop.CheckHr(
                WindowsMediaFoundationInterop.MFSetAttributeSize(outputMediaType, WindowsMediaFoundationInterop.MF_MT_FRAME_SIZE, width, height),
                "Failed to set output frame size");
            WindowsMediaFoundationInterop.CheckHr(
                WindowsMediaFoundationInterop.MFSetAttributeRatio(outputMediaType, WindowsMediaFoundationInterop.MF_MT_FRAME_RATE, framesPerSecond, 1),
                "Failed to set output frame rate");
            WindowsMediaFoundationInterop.CheckHr(
                WindowsMediaFoundationInterop.MFSetAttributeRatio(outputMediaType, WindowsMediaFoundationInterop.MF_MT_PIXEL_ASPECT_RATIO, 1, 1),
                "Failed to set output pixel aspect ratio");
            WindowsMediaFoundationInterop.CheckHr(
                sinkWriter.AddStream(outputMediaType, out int streamIndex),
                "IMFSinkWriter.AddStream failed");

            WindowsMediaFoundationInterop.CheckHr(
                WindowsMediaFoundationInterop.MFCreateMediaType(out inputMediaType),
                "MFCreateMediaType for input failed");
            WindowsMediaFoundationInterop.CheckHr(
                inputMediaType.SetGUID(WindowsMediaFoundationInterop.MF_MT_MAJOR_TYPE, WindowsMediaFoundationInterop.MFMediaType_Video),
                "Failed to set input major type");
            WindowsMediaFoundationInterop.CheckHr(
                inputMediaType.SetGUID(WindowsMediaFoundationInterop.MF_MT_SUBTYPE, WindowsMediaFoundationInterop.MFVideoFormat_RGB32),
                "Failed to set input subtype");
            WindowsMediaFoundationInterop.CheckHr(
                inputMediaType.SetUINT32(WindowsMediaFoundationInterop.MF_MT_INTERLACE_MODE, WindowsMediaFoundationInterop.MFVideoInterlace_Progressive),
                "Failed to set input interlace mode");
            WindowsMediaFoundationInterop.CheckHr(
                inputMediaType.SetUINT32(WindowsMediaFoundationInterop.MF_MT_ALL_SAMPLES_INDEPENDENT, 1),
                "Failed to set input independence");
            WindowsMediaFoundationInterop.CheckHr(
                inputMediaType.SetUINT32(WindowsMediaFoundationInterop.MF_MT_FIXED_SIZE_SAMPLES, 1),
                "Failed to set input fixed sample size");
            WindowsMediaFoundationInterop.CheckHr(
                inputMediaType.SetUINT32(WindowsMediaFoundationInterop.MF_MT_SAMPLE_SIZE, frameBytes),
                "Failed to set input sample size");
            WindowsMediaFoundationInterop.CheckHr(
                WindowsMediaFoundationInterop.MFSetAttributeSize(inputMediaType, WindowsMediaFoundationInterop.MF_MT_FRAME_SIZE, width, height),
                "Failed to set input frame size");
            WindowsMediaFoundationInterop.CheckHr(
                WindowsMediaFoundationInterop.MFSetAttributeRatio(inputMediaType, WindowsMediaFoundationInterop.MF_MT_FRAME_RATE, framesPerSecond, 1),
                "Failed to set input frame rate");
            WindowsMediaFoundationInterop.CheckHr(
                WindowsMediaFoundationInterop.MFSetAttributeRatio(inputMediaType, WindowsMediaFoundationInterop.MF_MT_PIXEL_ASPECT_RATIO, 1, 1),
                "Failed to set input pixel aspect ratio");
            WindowsMediaFoundationInterop.CheckHr(
                sinkWriter.SetInputMediaType(streamIndex, inputMediaType, null),
                "IMFSinkWriter.SetInputMediaType failed");
            WindowsMediaFoundationInterop.CheckHr(
                sinkWriter.BeginWriting(),
                "IMFSinkWriter.BeginWriting failed");

            long sampleTime = 0;
            foreach (var frame in frames)
            {
                byte[] frameBytesData = frameConverter(frame, width, height);
                WriteVideoFrame(sinkWriter, streamIndex, frameBytesData, sampleTime, frameDuration);
                sampleTime += frameDuration;
            }

            WindowsMediaFoundationInterop.CheckHr(
                sinkWriter.Finalize_(),
                "IMFSinkWriter.Finalize failed");
        }
        catch (Exception ex)
        {

        }
        finally
        {
            ReleaseComObject(inputMediaType);
            ReleaseComObject(outputMediaType);
            ReleaseComObject(sinkWriter);
            ReleaseComObject(attributes);

            if (mediaFoundationStarted)
                WindowsMediaFoundationInterop.MFShutdown();

            if (comInitialized)
                WindowsMediaFoundationInterop.CoUninitialize();
        }
    }

    static byte[] LoadBgraFrame(string pngPath, int width, int height)
    {
        using var stream = File.OpenRead(pngPath);
        var decoder = ImageUtils.GetImageFromStream(stream, ".png");
        if (decoder.Width != width || decoder.Height != height)
            throw new InvalidOperationException(
                $"Frame size mismatch for '{pngPath}'. Expected {width}x{height}, got {decoder.Width}x{decoder.Height}.");

        int rowBytes = width * 4;
        var bgra = new byte[height * rowBytes];
        for (int y = 0; y < height; y++)
        {
            decoder.PixelSpan.Slice(y * decoder.Stride, rowBytes)
                .CopyTo(bgra.AsSpan(y * rowBytes, rowBytes));
        }

        return ConvertTopDownRgbaToBottomUpBgra(bgra, width, height);
    }

    static byte[] ConvertTopDownRgbaToBottomUpBgra(byte[] rgba, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(rgba);

        int rowBytes = checked(width * 4);
        if (rgba.Length != checked(height * rowBytes))
            throw new ArgumentException("Unexpected RGBA frame length.", nameof(rgba));

        var bgra = new byte[rgba.Length];
        for (int y = 0; y < height; y++)
        {
            int sourceOffset = y * rowBytes;
            int destinationOffset = (height - 1 - y) * rowBytes;
            for (int x = 0; x < rowBytes; x += 4)
            {
                int src = sourceOffset + x;
                int dst = destinationOffset + x;
                bgra[dst] = rgba[src + 2];
                bgra[dst + 1] = rgba[src + 1];
                bgra[dst + 2] = rgba[src];
                bgra[dst + 3] = rgba[src + 3];
            }
        }

        return bgra;
    }

    static void WriteVideoFrame(IMFSinkWriter sinkWriter, int streamIndex, byte[] frameData, long sampleTime, long sampleDuration)
    {
        IMFSample? sample = null;
        IMFMediaBuffer? buffer = null;
        IntPtr scan0 = IntPtr.Zero;

        try
        {
            WindowsMediaFoundationInterop.CheckHr(
                WindowsMediaFoundationInterop.MFCreateMemoryBuffer(frameData.Length, out buffer),
                "MFCreateMemoryBuffer failed");
            WindowsMediaFoundationInterop.CheckHr(
                buffer.Lock(out scan0, out _, out _),
                "IMFMediaBuffer.Lock failed");

            Marshal.Copy(frameData, 0, scan0, frameData.Length);

            WindowsMediaFoundationInterop.CheckHr(
                buffer.Unlock(),
                "IMFMediaBuffer.Unlock failed");
            scan0 = IntPtr.Zero;

            WindowsMediaFoundationInterop.CheckHr(
                buffer.SetCurrentLength(frameData.Length),
                "IMFMediaBuffer.SetCurrentLength failed");
            WindowsMediaFoundationInterop.CheckHr(
                WindowsMediaFoundationInterop.MFCreateSample(out sample),
                "MFCreateSample failed");
            WindowsMediaFoundationInterop.CheckHr(
                sample.AddBuffer(buffer),
                "IMFSample.AddBuffer failed");
            WindowsMediaFoundationInterop.CheckHr(
                sample.SetSampleTime(sampleTime),
                "IMFSample.SetSampleTime failed");
            WindowsMediaFoundationInterop.CheckHr(
                sample.SetSampleDuration(sampleDuration),
                "IMFSample.SetSampleDuration failed");
            WindowsMediaFoundationInterop.CheckHr(
                sinkWriter.WriteSample(streamIndex, sample),
                "IMFSinkWriter.WriteSample failed");
        }
        catch (Exception ex)
        {

        }
        finally
        {
            if (scan0 != IntPtr.Zero)
                buffer?.Unlock();

            ReleaseComObject(buffer);
            ReleaseComObject(sample);
        }
    }

    static void ReleaseComObject(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
            Marshal.ReleaseComObject(comObject);
    }

    public async Task<string> BuildMp4FromDirectory0(string fullPath, int width, int height, int bitrate, int framesPerSecond)
    {
        string result = null;

        //var fullPath = StorageService.SubPath(StorageService.DirectoryBase, directory);

        var pngs = Directory.GetFiles(fullPath, "*.png");

        try
        {
            var composition = new MediaComposition();

            for (int i = 0; i < pngs.Length; i++)
            {
                var png = pngs[i];

                var stream = File.OpenRead(png);

                var decoder = ImageUtils.GetImageFromStream(stream, ".png");

                stream.Dispose();

                if (decoder.Width != width || decoder.Height != height)
                {
                    throw new Exception("width or height error");
                }

                var storageFile = await StorageFile.GetFileFromPathAsync(png);

                var clip = await MediaClip.CreateFromImageFileAsync(
                    storageFile,
                    TimeSpan.FromSeconds(1d / framesPerSecond));

                composition.Clips.Add(clip);
            }

            var folder = await StorageFolder.GetFolderFromPathAsync(fullPath);

            var outputFile = await folder.CreateFileAsync("output.mp4", CreationCollisionOption.ReplaceExisting);

            //var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);
            //profile.Video.Width = (uint)width;
            //profile.Video.Height = (uint)height;
            //profile.Video.Bitrate = (uint)bitrate;
            //profile.Video.FrameRate.Numerator = (uint)framesPerSecond;
            //profile.Video.FrameRate.Denominator = 1;

            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException("width/height must be positive.");

            if ((width & 1) != 0 || (height & 1) != 0)
                throw new ArgumentException("H.264 on Windows usually requires even width/height.");

            if (framesPerSecond <= 0)
                throw new ArgumentOutOfRangeException(nameof(framesPerSecond));

            if (bitrate <= 0)
                throw new ArgumentOutOfRangeException(nameof(bitrate));

            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);

            var result0 = await composition.RenderToFileAsync(
                outputFile,
                MediaTrimmingPreference.Precise,
                profile);

            if (result0 != TranscodeFailureReason.None)
                throw new InvalidOperationException($"Media Foundation render failed: {result0}.");

            result = outputFile.Path;

            //result = File.ReadAllBytes(outputFile.Path);
        }
        catch (Exception ex)
        {

        }
        finally
        {
            //VideoEncodingHelper.TryDeleteDirectory(tempDirectory);
        }

        return result;
    }
}

internal sealed class WindowsImageDecoder : INativeImageDecoder
{
    readonly byte[] _pixels;

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }

    public WindowsImageDecoder(Stream stream)
    {
        (_pixels, Width, Height) = LoadAsync(stream).GetAwaiter().GetResult();
        Stride = Width * 4;

        // BGRA → RGBA in-place
        ConvertBgraToRgba(_pixels);
    }

    static async Task<(byte[] pixels, int width, int height)> LoadAsync(Stream stream)
    {
        using var ras = new InMemoryRandomAccessStream();

        if (stream.CanSeek)
            stream.Seek(0, SeekOrigin.Begin);

        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms).ConfigureAwait(false);

        var bytes = ms.ToArray();
        await ras.WriteAsync(bytes.AsBuffer()).AsTask().ConfigureAwait(false);
        ras.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(ras).AsTask().ConfigureAwait(false);
        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage).AsTask().ConfigureAwait(false);

        return (pixelData.DetachPixelData(), (int)decoder.PixelWidth, (int)decoder.PixelHeight);
    }

    public ReadOnlySpan<byte> PixelSpan => _pixels;

    static void ConvertBgraToRgba(Span<byte> data)
    {
        for (int i = 0; i < data.Length; i += 4)
        {
            (data[i], data[i + 2]) = (data[i + 2], data[i]); // B ↔ R
        }
    }

    public void Dispose() { }
}
