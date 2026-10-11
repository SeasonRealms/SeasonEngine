// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Season.Platforms.Linux;

/// <summary>
/// Decodes the audio track of a media file (.m4a/.mp4/.mp3/... the containers the STT panel
/// accepts) into the 16 kHz mono 16-bit PCM WAV contract <see cref="IRecordService.DecodeToWavPcm16"/>
/// promises - the same layout the Apple (AVFoundation) and Windows (Media Foundation) record
/// services produce.
/// <para>
/// Linux has no single system-wide media API, so two decoders are chained at runtime. The first
/// is the libavformat/libavcodec/libswresample shared libraries (FFmpeg 8.x, SONAMEs .so.62/.so.62
/// /.so.6) bound with P/Invoke: minimal installs - WSL among them - routinely carry them because
/// another package links against them, even when the ffmpeg binary itself is absent. The second is
/// the ffmpeg command line tool, for systems that have the binary but not the libraries. When
/// neither exists the caller gets an InvalidDataException naming both failures.
/// </para>
/// <para>
/// The P/Invoke side reads a handful of leading fields of the public FFmpeg structs
/// (AVFormatContext, AVStream, AVCodecParameters, AVPacket, AVFrame). Those fields have been
/// stable for many years but are not formally ABI-frozen across major versions, so Load()
/// accepts only the FFmpeg 8.x SONAMEs (verified through the version functions) and Decode
/// guards every read before trusting it (the stream must report itself as audio, with a
/// plausible sample rate and channel count).
/// Blocking, so callers run it on a background thread (STT.EnsureWavPcm16 already does).
/// </para>
/// <para>
/// The same binding also backs <see cref="LibavApi.PlaybackStream"/>, the streaming decoder
/// LinuxAudioPlayer uses for the containers its managed decoders cannot read (.m4a/.mp4/
/// .m4v), so both consumers share one verified load of the libraries.
/// </para>
/// </summary>
internal static class LinuxAudioDecoder
{
    // The WAV output contract: 16 kHz mono 16-bit little-endian PCM (see IRecordService).
    const int TargetSampleRate = 16000;
    const int TargetChannels = 1;
    const int TargetBitsPerSample = 16;

    const int AvMediaTypeAudio = 1;   // enum AVMediaType
    const int AvSampleFormatS16 = 1;  // enum AVSampleFormat
    const int AvSampleFormatFlt = 3;  // enum AVSampleFormat
    const int AvErrorEagain = -11;          // AVERROR(EAGAIN), errno-style on Linux
    const int AvErrorEof = -541478725;      // AVERROR_EOF (negated MKTAG('E','O','F',' '))

    public static byte[] DecodeToWavPcm16(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"The audio file does not exist: {path}");
        }

        Exception libavFailure = null;

        try
        {
            return DecodeWithLibav(path);
        }
        catch (Exception ex)
        {
            // Keep the failure for the aggregate report below: the CLI may still succeed
            // (and the CLI's failure alone rarely explains a missing shared library).
            libavFailure = ex;

            Debug.WriteLine($"[LinuxAudioDecoder] libav decode of '{Path.GetFileName(path)}' failed: {ex.Message}");
        }

        try
        {
            return DecodeWithFfmpegCli(path);
        }
        catch (Exception cliFailure)
        {
            throw new InvalidDataException(
                $"Unable to decode '{path}'. " +
                $"libav shared libraries: {libavFailure.Message} " +
                $"ffmpeg tool: {cliFailure.Message}",
                libavFailure);
        }
    }

    /// <summary>
    /// First decoder: libavformat/libavcodec/libswresample through P/Invoke. The libraries are
    /// loaded per call (dlopen reference counting makes this cheap) so nothing needs to be
    /// cached, and a machine without them simply falls through to the CLI path.
    /// </summary>
    static byte[] DecodeWithLibav(string path)
    {
        var libav = LibavApi.Load();

        try
        {
            return libav.Decode(path);
        }
        finally
        {
            libav.Dispose();
        }
    }

    /// <summary>
    /// Second decoder: the ffmpeg binary. Writes to a temp file instead of stdout because a
    /// pipe is not seekable, and ffmpeg then emits placeholders instead of real RIFF sizes.
    /// </summary>
    static byte[] DecodeWithFfmpegCli(string path)
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"season-audio-linux-{Guid.NewGuid():N}.wav");

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList =
                {
                    "-hide_banner",
                    "-loglevel", "error",
                    "-y",
                    "-i", path,
                    "-vn",                      // drop the picture of an .mp4/.m4v
                    "-ac", TargetChannels.ToString(),
                    "-ar", TargetSampleRate.ToString(),
                    "-c:a", "pcm_s16le",
                    tempPath
                }
            };

            using var process = new Process { StartInfo = startInfo };

            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException("Failed to start ffmpeg.");
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("ffmpeg is not installed or not on PATH.", ex);
            }

            string errorOutput = process.StandardError.ReadToEnd();

            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new InvalidDataException($"ffmpeg exited with code {process.ExitCode}: {errorOutput}");
            }

            var bytes = File.ReadAllBytes(tempPath);

            if (bytes.Length <= 44)
            {
                throw new InvalidDataException($"The decoded WAV does not contain any audio: {path}");
            }

            Debug.WriteLine($"[LinuxAudioDecoder] ffmpeg decoded '{Path.GetFileName(path)}' into {bytes.Length} WAV bytes");

            return bytes;
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    /// <summary>
    /// Writes the canonical 44-byte WAV header in front of little-endian PCM16 bytes that
    /// already are in the target format (same layout as AppleRecordService.WriteWavPcm16).
    /// </summary>
    static byte[] WriteWavPcm16(byte[] pcm, int sampleRate)
    {
        var bytes = new byte[44 + pcm.Length];

        using var stream = new MemoryStream(bytes);
        using var writer = new BinaryWriter(stream);

        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + pcm.Length);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)TargetChannels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * TargetChannels * TargetBitsPerSample / 8);
        writer.Write((short)(TargetChannels * TargetBitsPerSample / 8));
        writer.Write((short)TargetBitsPerSample);
        writer.Write("data"u8.ToArray());
        writer.Write(pcm.Length);
        writer.Write(pcm);

        writer.Flush();

        return bytes;
    }

    /// <summary>
    /// One loaded set of libav libraries and their function pointers. Instances are created
    /// per decode call, own every native resource they allocate, and are safe to use from a
    /// single thread only (which is all a blocking decode needs).
    /// </summary>
    internal sealed class LibavApi : IDisposable
    {
        // Leading-field offsets of the FFmpeg 8.x public structs, transcribed from the
        // release/8.0 headers (avformat.h, codec_par.h, packet.h, frame.h) and verified
        // at runtime by the guards in Decode. These are NOT ABI-guaranteed across FFmpeg
        // major versions, which is why Load() insists on the .62/.62/.60/.6 SONAME set.
        const int FormatContextNbStreams = 44;             // AVFormatContext.nb_streams
        const int FormatContextStreams = 48;               // AVFormatContext.streams (AVStream**)
        const int StreamCodecpar = 16;                     // AVStream.codecpar (AVCodecParameters*)
        const int CodecParametersCodecType = 0;            // AVCodecParameters.codec_type
        const int CodecParametersFormat = 44;              // AVCodecParameters.format (AVSampleFormat)
        const int CodecParametersChannelLayout = 128;      // AVCodecParameters.ch_layout (24 bytes)
        const int CodecParametersSampleRate = 152;         // AVCodecParameters.sample_rate
        const int PacketStreamIndex = 36;                  // AVPacket.stream_index
        const int FrameExtendedData = 96;                  // AVFrame.extended_data (uint8_t**)
        const int FrameNbSamples = 112;                    // AVFrame.nb_samples

        const int ChannelLayoutSize = 24;                  // AVChannelLayout: order(4) nb_channels(4) u(8) opaque(8)
        const int ChannelOrderNative = 1;                  // AV_CHANNEL_ORDER_NATIVE
        const int ChannelLayoutMono = 0x4;                 // AV_CH_LAYOUT_MONO

        // ---- library handles ----
        IntPtr _avformat;
        IntPtr _avcodec;
        IntPtr _avutil;
        IntPtr _swresample;

        // ---- libavformat ----
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int AvFormatOpenInput(ref IntPtr ps, IntPtr url, IntPtr inputFormat, IntPtr options);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int AvFormatFindStreamInfo(IntPtr context, IntPtr options);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int AvFindBestStream(IntPtr context, int type, int wantedStream, int relatedStream, ref IntPtr decoder, int flags);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int AvReadFrame(IntPtr context, IntPtr packet);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void AvFormatCloseInput(ref IntPtr context);

        // ---- libavcodec ----
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate IntPtr AvCodecAllocContext3(IntPtr codec);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int AvCodecParametersToContext(IntPtr context, IntPtr parameters);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int AvCodecOpen2(IntPtr context, IntPtr codec, IntPtr options);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int AvCodecSendPacket(IntPtr context, IntPtr packet);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int AvCodecReceiveFrame(IntPtr context, IntPtr frame);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void AvCodecFreeContext(ref IntPtr context);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate IntPtr AvPacketAlloc();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void AvPacketUnref(IntPtr packet);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void AvPacketFree(ref IntPtr packet);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate IntPtr AvFrameAlloc();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void AvFrameUnref(IntPtr frame);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void AvFrameFree(ref IntPtr frame);

        // ---- libswresample ----
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int SwrAllocSetOpts2(ref IntPtr context, IntPtr outLayout, int outFormat, int outRate, IntPtr inLayout, int inFormat, int inRate, int logOffset, IntPtr logContext);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int SwrInit(IntPtr context);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int SwrConvert(IntPtr context, ref IntPtr outPtr, int outCount, IntPtr inPtr, int inCount);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate long SwrGetDelay(IntPtr context, long baseValue);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void SwrFree(ref IntPtr context);

        // ---- libavutil ----
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate uint LibVersion();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void AvLogSetLevel(int level);

        AvFormatOpenInput _avformatOpenInput;
        AvFormatFindStreamInfo _avformatFindStreamInfo;
        AvFindBestStream _avFindBestStream;
        AvReadFrame _avReadFrame;
        AvFormatCloseInput _avformatCloseInput;
        AvCodecAllocContext3 _avcodecAllocContext3;
        AvCodecParametersToContext _avcodecParametersToContext;
        AvCodecOpen2 _avcodecOpen2;
        AvCodecSendPacket _avcodecSendPacket;
        AvCodecReceiveFrame _avcodecReceiveFrame;
        AvCodecFreeContext _avcodecFreeContext;
        AvPacketAlloc _avPacketAlloc;
        AvPacketUnref _avPacketUnref;
        AvPacketFree _avPacketFree;
        AvFrameAlloc _avFrameAlloc;
        AvFrameUnref _avFrameUnref;
        AvFrameFree _avFrameFree;
        SwrAllocSetOpts2 _swrAllocSetOpts2;
        SwrInit _swrInit;
        SwrConvert _swrConvert;
        SwrGetDelay _swrGetDelay;
        SwrFree _swrFree;

        /// <summary>
        /// Loads the four FFmpeg 8.x shared libraries and binds every function used below.
        /// Both the versioned and the plain ("-dev" symlink) names are tried for each library,
        /// but the version function has to report the expected major: an unversioned symlink
        /// pointing at another FFmpeg generation must not reach the offset-based reads.
        /// </summary>
        public static LibavApi Load()
        {
            var api = new LibavApi();

            try
            {
                api._avformat = LoadLibrary("libavformat", 62);
                api._avcodec = LoadLibrary("libavcodec", 62);
                api._avutil = LoadLibrary("libavutil", 60);
                api._swresample = LoadLibrary("libswresample", 6);
                api.Resolve();
                return api;
            }
            catch
            {
                api.Dispose();
                throw;
            }
        }

        static IntPtr LoadLibrary(string name, int expectedMajor)
        {
            // Versioned SONAME first, the plain "-dev" symlink second; either way the version
            // function has to report the expected major, so an unversioned symlink pointing at
            // another FFmpeg generation can never reach the offset-based reads below.
            foreach (var candidate in new[] { $"{name}.so.{expectedMajor}", $"{name}.so" })
            {
                if (!NativeLibrary.TryLoad(candidate, out var handle))
                {
                    continue;
                }

                // avformat_version() style symbol: libavformat -> avformat_version.
                if (NativeLibrary.TryGetExport(handle, name[3..] + "_version", out var versionExport))
                {
                    uint version = Marshal.GetDelegateForFunctionPointer<LibVersion>(versionExport)();

                    if (version >> 16 == expectedMajor)
                    {
                        return handle;
                    }
                }

                NativeLibrary.Free(handle);
            }

            throw new InvalidOperationException($"The {name} shared library (FFmpeg {expectedMajor} series) is not available.");
        }

        void Resolve()
        {
            _avformatOpenInput = Bind<AvFormatOpenInput>(_avformat, "avformat_open_input");
            _avformatFindStreamInfo = Bind<AvFormatFindStreamInfo>(_avformat, "avformat_find_stream_info");
            _avFindBestStream = Bind<AvFindBestStream>(_avformat, "av_find_best_stream");
            _avReadFrame = Bind<AvReadFrame>(_avformat, "av_read_frame");
            _avformatCloseInput = Bind<AvFormatCloseInput>(_avformat, "avformat_close_input");

            _avcodecAllocContext3 = Bind<AvCodecAllocContext3>(_avcodec, "avcodec_alloc_context3");
            _avcodecParametersToContext = Bind<AvCodecParametersToContext>(_avcodec, "avcodec_parameters_to_context");
            _avcodecOpen2 = Bind<AvCodecOpen2>(_avcodec, "avcodec_open2");
            _avcodecSendPacket = Bind<AvCodecSendPacket>(_avcodec, "avcodec_send_packet");
            _avcodecReceiveFrame = Bind<AvCodecReceiveFrame>(_avcodec, "avcodec_receive_frame");
            _avcodecFreeContext = Bind<AvCodecFreeContext>(_avcodec, "avcodec_free_context");
            _avPacketAlloc = Bind<AvPacketAlloc>(_avcodec, "av_packet_alloc");
            _avPacketUnref = Bind<AvPacketUnref>(_avcodec, "av_packet_unref");
            _avPacketFree = Bind<AvPacketFree>(_avcodec, "av_packet_free");
            _avFrameAlloc = Bind<AvFrameAlloc>(_avcodec, "av_frame_alloc");
            _avFrameUnref = Bind<AvFrameUnref>(_avcodec, "av_frame_unref");
            _avFrameFree = Bind<AvFrameFree>(_avcodec, "av_frame_free");

            _swrAllocSetOpts2 = Bind<SwrAllocSetOpts2>(_swresample, "swr_alloc_set_opts2");
            _swrInit = Bind<SwrInit>(_swresample, "swr_init");
            _swrConvert = Bind<SwrConvert>(_swresample, "swr_convert");
            _swrGetDelay = Bind<SwrGetDelay>(_swresample, "swr_get_delay");
            _swrFree = Bind<SwrFree>(_swresample, "swr_free");

            // Optional: silence the libraries' info chatter; decoding failures still surface
            // through return codes and exceptions.
            if (NativeLibrary.TryGetExport(_avutil, "av_log_set_level", out var logExport))
            {
                var avLogSetLevel = Marshal.GetDelegateForFunctionPointer<AvLogSetLevel>(logExport);
                avLogSetLevel(16); // AV_LOG_ERROR
            }
        }

        static T Bind<T>(IntPtr library, string symbol) where T : Delegate
        {
            if (!NativeLibrary.TryGetExport(library, symbol, out var export))
            {
                throw new InvalidOperationException($"The shared library does not export {symbol}.");
            }

            return Marshal.GetDelegateForFunctionPointer<T>(export);
        }

        /// <summary>
        /// Runs the full demux/decode/resample chain for one file: demux with libavformat,
        /// decode the audio packets with libavcodec, convert to 16 kHz mono signed 16-bit
        /// with libswresample, and wrap the samples in the canonical WAV header.
        /// </summary>
        public byte[] Decode(string path)
        {
            IntPtr url = Marshal.StringToCoTaskMemUTF8(path);

            IntPtr formatContext = IntPtr.Zero;
            IntPtr codecContext = IntPtr.Zero;
            IntPtr packet = IntPtr.Zero;
            IntPtr frame = IntPtr.Zero;
            IntPtr resampler = IntPtr.Zero;
            IntPtr inLayout = IntPtr.Zero;
            IntPtr outLayout = IntPtr.Zero;

            try
            {
                int result = _avformatOpenInput(ref formatContext, url, IntPtr.Zero, IntPtr.Zero);

                if (result < 0)
                {
                    throw new InvalidDataException($"Failed to open the audio file ({result}): {path}");
                }

                result = _avformatFindStreamInfo(formatContext, IntPtr.Zero);

                if (result < 0)
                {
                    throw new InvalidDataException($"Failed to read the stream information ({result}): {path}");
                }

                IntPtr decoder = IntPtr.Zero;
                int audioIndex = _avFindBestStream(formatContext, AvMediaTypeAudio, -1, -1, ref decoder, 0);

                if (audioIndex < 0 || decoder == IntPtr.Zero)
                {
                    throw new InvalidDataException($"The file does not contain an audio track: {path}");
                }

                // ---- pick the audio stream's codec parameters ----
                int streamCount = Marshal.ReadInt32(formatContext, FormatContextNbStreams);

                if (audioIndex >= streamCount)
                {
                    throw new InvalidDataException($"Unexpected libavformat layout: stream {audioIndex} of {streamCount}.");
                }

                IntPtr streams = Marshal.ReadIntPtr(formatContext, FormatContextStreams);
                IntPtr stream = Marshal.ReadIntPtr(streams, audioIndex * IntPtr.Size);
                IntPtr parameters = Marshal.ReadIntPtr(stream, StreamCodecpar);

                if (parameters == IntPtr.Zero)
                {
                    throw new InvalidDataException($"The audio stream carries no codec parameters: {path}");
                }

                // First ABI guard: whatever we read as AVCodecParameters must report itself
                // as audio, otherwise the offset table does not match the loaded libraries.
                int codecType = Marshal.ReadInt32(parameters, CodecParametersCodecType);

                if (codecType != AvMediaTypeAudio)
                {
                    throw new InvalidDataException($"Unexpected libavformat layout: codec type {codecType} is not audio.");
                }

                int inFormat = Marshal.ReadInt32(parameters, CodecParametersFormat);
                int inRate = Marshal.ReadInt32(parameters, CodecParametersSampleRate);

                if (inRate <= 0 || inRate > 384000 || inFormat < 0 || inFormat > 11)
                {
                    throw new InvalidDataException($"Unexpected libavformat layout: format {inFormat} at {inRate} Hz.");
                }

                // ---- open the decoder ----
                codecContext = _avcodecAllocContext3(decoder);

                if (codecContext == IntPtr.Zero)
                {
                    throw new InvalidDataException($"Failed to allocate the audio decoder: {path}");
                }

                result = _avcodecParametersToContext(codecContext, parameters);

                if (result < 0)
                {
                    throw new InvalidDataException($"Failed to apply the codec parameters ({result}): {path}");
                }

                result = _avcodecOpen2(codecContext, decoder, IntPtr.Zero);

                if (result < 0)
                {
                    throw new InvalidDataException($"Failed to open the audio decoder ({result}): {path}");
                }

                // ---- configure the resampler (decoded format -> 16 kHz mono s16) ----
                inLayout = Marshal.AllocHGlobal(ChannelLayoutSize);
                Marshal.WriteInt64(inLayout, 0, Marshal.ReadInt64(parameters, CodecParametersChannelLayout));
                Marshal.WriteInt64(inLayout, 8, Marshal.ReadInt64(parameters, CodecParametersChannelLayout + 8));
                Marshal.WriteInt64(inLayout, 16, Marshal.ReadInt64(parameters, CodecParametersChannelLayout + 16));

                outLayout = Marshal.AllocHGlobal(ChannelLayoutSize);
                Marshal.WriteInt32(outLayout, 0, ChannelOrderNative);
                Marshal.WriteInt32(outLayout, 4, TargetChannels);
                Marshal.WriteInt64(outLayout, 8, ChannelLayoutMono);
                Marshal.WriteInt64(outLayout, 16, 0);

                result = _swrAllocSetOpts2(ref resampler, outLayout, AvSampleFormatS16, TargetSampleRate,
                    inLayout, inFormat, inRate, 0, IntPtr.Zero);

                if (result < 0)
                {
                    throw new InvalidDataException($"Failed to create the audio resampler ({result}): {path}");
                }

                result = _swrInit(resampler);

                if (result < 0)
                {
                    throw new InvalidDataException($"Failed to initialize the audio resampler ({result}): {path}");
                }

                packet = _avPacketAlloc();
                frame = _avFrameAlloc();

                if (packet == IntPtr.Zero || frame == IntPtr.Zero)
                {
                    throw new InvalidDataException($"Failed to allocate decode buffers: {path}");
                }

                // ---- demux + decode + resample ----
                var pcm = new MemoryStream();
                int decodedFrames = 0;

                while (_avReadFrame(formatContext, packet) >= 0)
                {
                    if (Marshal.ReadInt32(packet, PacketStreamIndex) == audioIndex)
                    {
                        SendPacket(codecContext, packet);
                        DrainFrames(codecContext, frame, resampler, inRate, pcm, ref decodedFrames);
                    }

                    _avPacketUnref(packet);
                }

                // End of input: flush the decoder, then drain the resampler's delay line.
                SendPacket(codecContext, IntPtr.Zero);
                DrainFrames(codecContext, frame, resampler, inRate, pcm, ref decodedFrames);
                FlushResampler(resampler, pcm);

                byte[] samples = pcm.ToArray();

                if (samples.Length == 0)
                {
                    throw new InvalidDataException($"The audio track did not contain any samples: {path}");
                }

                Debug.WriteLine($"[LinuxAudioDecoder] '{Path.GetFileName(path)}': {decodedFrames} frames -> " +
                    $"{samples.Length / (TargetChannels * TargetBitsPerSample / 8)} samples at {TargetSampleRate} Hz, {inRate} Hz source");

                return WriteWavPcm16(samples, TargetSampleRate);
            }
            finally
            {
                if (resampler != IntPtr.Zero) _swrFree(ref resampler);
                if (frame != IntPtr.Zero) _avFrameFree(ref frame);
                if (packet != IntPtr.Zero) _avPacketFree(ref packet);
                if (codecContext != IntPtr.Zero) _avcodecFreeContext(ref codecContext);
                if (formatContext != IntPtr.Zero) _avformatCloseInput(ref formatContext);
                if (inLayout != IntPtr.Zero) Marshal.FreeHGlobal(inLayout);
                if (outLayout != IntPtr.Zero) Marshal.FreeHGlobal(outLayout);
                Marshal.FreeCoTaskMem(url);
            }
        }

        /// <summary>
        /// avcodec_send_packet for one demuxed packet (or the null flush). The decode loop
        /// drains the decoder to EAGAIN after every submission, so a full input queue is
        /// impossible here and EAGAIN is treated as a hard error rather than dropping data.
        /// </summary>
        void SendPacket(IntPtr codecContext, IntPtr packet)
        {
            int result = _avcodecSendPacket(codecContext, packet);

            if (result == AvErrorEagain)
            {
                // Caller's drain loop runs right after; a stale EAGAIN can only mean the
                // previous drain did not finish, which our loop shape prevents - so treat
                // it as a hard error rather than silently dropping the packet.
                throw new InvalidDataException($"The audio decoder refused a packet ({result}).");
            }

            if (result < 0)
            {
                throw new InvalidDataException($"Failed to submit an audio packet to the decoder ({result}).");
            }
        }

        /// <summary>
        /// Receives every frame the decoder currently has and feeds each one into the resampler.
        /// </summary>
        void DrainFrames(IntPtr codecContext, IntPtr frame, IntPtr resampler, int inRate, MemoryStream pcm, ref int decodedFrames)
        {
            while (true)
            {
                int result = _avcodecReceiveFrame(codecContext, frame);

                if (result < 0)
                {
                    return;
                }

                decodedFrames++;

                int nbSamples = Marshal.ReadInt32(frame, FrameNbSamples);

                // Second ABI guard: an AAC frame is at most a few thousand samples; anything
                // else means AVFrame does not match the loaded library generation.
                if (nbSamples < 0 || nbSamples > 48000)
                {
                    throw new InvalidDataException($"Unexpected libavcodec layout: frame holds {nbSamples} samples.");
                }

                IntPtr plane = Marshal.ReadIntPtr(frame, FrameExtendedData);

                if (nbSamples > 0)
                {
                    FeedResampler(resampler, inRate, plane, nbSamples, pcm);
                }

                _avFrameUnref(frame);
            }
        }

        /// <summary>
        /// Converts one decoded frame into the target format. The output capacity accounts for
        /// the samples the resampler still buffers (swr_get_delay) plus the new ones, scaled by
        /// the rate ratio.
        /// </summary>
        void FeedResampler(IntPtr resampler, int inRate, IntPtr planes, int inSamples, MemoryStream pcm)
        {
            long delay = _swrGetDelay(resampler, inRate);
            int outCount = (int)((delay + inSamples) * (long)TargetSampleRate / inRate) + 32;

            IntPtr buffer = Marshal.AllocHGlobal(outCount * (TargetBitsPerSample / 8));

            try
            {
                IntPtr outPtr = buffer;
                int converted = _swrConvert(resampler, ref outPtr, outCount, planes, inSamples);

                if (converted < 0)
                {
                    throw new InvalidDataException($"Failed to resample the audio ({converted}).");
                }

                Append(pcm, buffer, converted * (TargetBitsPerSample / 8));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>Drains the resampler's internal delay line once the input is exhausted.</summary>
        void FlushResampler(IntPtr resampler, MemoryStream pcm)
        {
            const int Chunk = 4096;

            IntPtr buffer = Marshal.AllocHGlobal(Chunk * (TargetBitsPerSample / 8));

            try
            {
                while (true)
                {
                    IntPtr outPtr = buffer;
                    int converted = _swrConvert(resampler, ref outPtr, Chunk, IntPtr.Zero, 0);

                    if (converted < 0)
                    {
                        throw new InvalidDataException($"Failed to flush the audio resampler ({converted}).");
                    }

                    if (converted == 0)
                    {
                        return;
                    }

                    Append(pcm, buffer, converted * (TargetBitsPerSample / 8));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        static void Append(MemoryStream pcm, IntPtr buffer, int byteCount)
        {
            if (byteCount <= 0)
            {
                return;
            }

            var chunk = new byte[byteCount];

            Marshal.Copy(buffer, chunk, 0, byteCount);

            pcm.Write(chunk, 0, byteCount);
        }

        public void Dispose()
        {
            if (_avformat != IntPtr.Zero) { NativeLibrary.Free(_avformat); _avformat = IntPtr.Zero; }
            if (_avcodec != IntPtr.Zero) { NativeLibrary.Free(_avcodec); _avcodec = IntPtr.Zero; }
            if (_avutil != IntPtr.Zero) { NativeLibrary.Free(_avutil); _avutil = IntPtr.Zero; }
            if (_swresample != IntPtr.Zero) { NativeLibrary.Free(_swresample); _swresample = IntPtr.Zero; }
        }

        /// <summary>
        /// One streaming playback session on top of this binding: demuxes and decodes the
        /// audio track of one file and delivers packed interleaved 32-bit floats at the
        /// source sample rate and channel layout. No resampling happens here - the SDL
        /// playback stream converts to the physical device format, mirroring the Mp3Decoder
        /// and WavDecoder contracts in LinuxAudioPlayer.
        /// <para>
        /// Unlike Decode (which swallows the whole file for the STT WAV contract), this is
        /// the pull model the playback loop uses: Read returns whatever is decoded so far,
        /// calling it again advances the demuxer, and 0 marks the end of the file. The
        /// instance is created on the caller's thread and used exclusively by one voice
        /// thread afterwards, exactly like the other decoders.
        /// </para>
        /// </summary>
        internal sealed class PlaybackStream : IDisposable
        {
            /// <summary>Second ABI guard bound, same as Decode: an audio frame never holds anywhere near this many samples.</summary>
            const int MaxSamplesPerFrame = 48000;

            /// <summary>Slack over the per-frame output when sizing the conversion buffer; the converter runs at the source rate, so it holds no delay.</summary>
            const int ResamplerSlack = 64;

            readonly LibavApi _api;
            readonly string _path;

            IntPtr _formatContext;
            IntPtr _codecContext;
            IntPtr _packet;
            IntPtr _frame;
            IntPtr _resampler;
            IntPtr _inLayout;
            IntPtr _outLayout;
            IntPtr _converted;       // scratch for one converted chunk, in float samples
            int _convertedCapacity;  // in float samples (all channels)
            int _audioIndex;
            bool _inputDone;         // av_read_frame hit EOF; the decoder flush was submitted

            float[] _pending = [];   // converted floats not yet returned to Read
            int _pendingOffset;
            int _pendingCount;

            public int SampleRate { get; private set; }

            public int Channels { get; private set; }

            PlaybackStream(string path, LibavApi api)
            {
                _path = path;
                _api = api;
            }

            /// <summary>
            /// Loads the libraries, opens the file and configures the decoder for its native
            /// layout. Any failure throws (the caller's OpenDecoder turns that into the
            /// mplayer fallback) and releases everything that was already opened.
            /// </summary>
            public static PlaybackStream Open(string path)
            {
                var api = LibavApi.Load();
                var stream = new PlaybackStream(path, api);

                try
                {
                    stream.OpenCore();
                    return stream;
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            }

            void OpenCore()
            {
                IntPtr url = Marshal.StringToCoTaskMemUTF8(_path);

                try
                {
                    int result = _api._avformatOpenInput(ref _formatContext, url, IntPtr.Zero, IntPtr.Zero);

                    if (result < 0)
                    {
                        throw new InvalidDataException($"Failed to open the audio file ({result}): {_path}");
                    }

                    result = _api._avformatFindStreamInfo(_formatContext, IntPtr.Zero);

                    if (result < 0)
                    {
                        throw new InvalidDataException($"Failed to read the stream information ({result}): {_path}");
                    }

                    IntPtr decoder = IntPtr.Zero;
                    _audioIndex = _api._avFindBestStream(_formatContext, AvMediaTypeAudio, -1, -1, ref decoder, 0);

                    if (_audioIndex < 0 || decoder == IntPtr.Zero)
                    {
                        throw new InvalidDataException($"The file does not contain an audio track: {_path}");
                    }

                    // ---- pick the audio stream's codec parameters ----
                    int streamCount = Marshal.ReadInt32(_formatContext, FormatContextNbStreams);

                    if (_audioIndex >= streamCount)
                    {
                        throw new InvalidDataException($"Unexpected libavformat layout: stream {_audioIndex} of {streamCount}.");
                    }

                    IntPtr streams = Marshal.ReadIntPtr(_formatContext, FormatContextStreams);
                    IntPtr stream = Marshal.ReadIntPtr(streams, _audioIndex * IntPtr.Size);
                    IntPtr parameters = Marshal.ReadIntPtr(stream, StreamCodecpar);

                    if (parameters == IntPtr.Zero)
                    {
                        throw new InvalidDataException($"The audio stream carries no codec parameters: {_path}");
                    }

                    // First ABI guard: whatever we read as AVCodecParameters must report
                    // itself as audio, otherwise the offset table does not match the loaded
                    // libraries (same check as Decode).
                    int codecType = Marshal.ReadInt32(parameters, CodecParametersCodecType);

                    if (codecType != AvMediaTypeAudio)
                    {
                        throw new InvalidDataException($"Unexpected libavformat layout: codec type {codecType} is not audio.");
                    }

                    int inFormat = Marshal.ReadInt32(parameters, CodecParametersFormat);
                    int inRate = Marshal.ReadInt32(parameters, CodecParametersSampleRate);
                    int order = Marshal.ReadInt32(parameters, CodecParametersChannelLayout);
                    int channels = Marshal.ReadInt32(parameters, CodecParametersChannelLayout + 4);

                    if (inRate <= 0 || inRate > 384000 || inFormat < 0 || inFormat > 11)
                    {
                        throw new InvalidDataException($"Unexpected libavformat layout: format {inFormat} at {inRate} Hz.");
                    }

                    // Playback keeps the native layout, so the channel layout has to describe
                    // itself - a wildcard or ambisonic order cannot be reproduced channelwise.
                    if (order != ChannelOrderNative || channels < 1 || channels > 8)
                    {
                        throw new InvalidDataException($"Unsupported channel layout: order {order} with {channels} channels.");
                    }

                    SampleRate = inRate;
                    Channels = channels;

                    // ---- open the decoder ----
                    _codecContext = _api._avcodecAllocContext3(decoder);

                    if (_codecContext == IntPtr.Zero)
                    {
                        throw new InvalidDataException($"Failed to allocate the audio decoder: {_path}");
                    }

                    result = _api._avcodecParametersToContext(_codecContext, parameters);

                    if (result < 0)
                    {
                        throw new InvalidDataException($"Failed to apply the codec parameters ({result}): {_path}");
                    }

                    result = _api._avcodecOpen2(_codecContext, decoder, IntPtr.Zero);

                    if (result < 0)
                    {
                        throw new InvalidDataException($"Failed to open the audio decoder ({result}): {_path}");
                    }

                    // ---- configure the converter (native -> packed floats, same rate/layout) ----
                    _inLayout = CopyChannelLayout(parameters, CodecParametersChannelLayout);
                    _outLayout = CopyChannelLayout(parameters, CodecParametersChannelLayout);

                    result = _api._swrAllocSetOpts2(ref _resampler, _outLayout, AvSampleFormatFlt, inRate,
                        _inLayout, inFormat, inRate, 0, IntPtr.Zero);

                    if (result < 0)
                    {
                        throw new InvalidDataException($"Failed to create the audio converter ({result}): {_path}");
                    }

                    result = _api._swrInit(_resampler);

                    if (result < 0)
                    {
                        throw new InvalidDataException($"Failed to initialize the audio converter ({result}): {_path}");
                    }

                    _packet = _api._avPacketAlloc();
                    _frame = _api._avFrameAlloc();

                    if (_packet == IntPtr.Zero || _frame == IntPtr.Zero)
                    {
                        throw new InvalidDataException($"Failed to allocate decode buffers: {_path}");
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(url);
                }
            }

            static IntPtr CopyChannelLayout(IntPtr parameters, int offset)
            {
                // 24 bytes of AVChannelLayout { order, nb_channels, u, opaque } copied verbatim.
                var layout = Marshal.AllocHGlobal(ChannelLayoutSize);

                Marshal.WriteInt64(layout, 0, Marshal.ReadInt64(parameters, offset));
                Marshal.WriteInt64(layout, 8, Marshal.ReadInt64(parameters, offset + 8));
                Marshal.WriteInt64(layout, 16, Marshal.ReadInt64(parameters, offset + 16));

                return layout;
            }

            /// <summary>
            /// Fills <paramref name="buffer"/> with interleaved floats; returns the number of
            /// floats written, 0 at end of stream. Matches Decoder.Read in LinuxAudioPlayer.
            /// </summary>
            public int Read(float[] buffer, int offset, int count)
            {
                int written = 0;

                while (written < count)
                {
                    if (_pendingCount > 0)
                    {
                        int take = Math.Min(_pendingCount, count - written);

                        Array.Copy(_pending, _pendingOffset, buffer, offset + written, take);

                        _pendingOffset += take;
                        _pendingCount -= take;
                        written += take;

                        continue;
                    }

                    if (!DecodeNextChunk())
                    {
                        break;   // end of stream
                    }
                }

                return written;
            }

            /// <summary>
            /// Advances the demux/decode chain until one converted chunk is pending, or
            /// reports the end of the stream. Called from Read only.
            /// </summary>
            bool DecodeNextChunk()
            {
                while (true)
                {
                    int result = _api._avcodecReceiveFrame(_codecContext, _frame);

                    if (result >= 0)
                    {
                        int nbSamples = Marshal.ReadInt32(_frame, FrameNbSamples);

                        // Second ABI guard: an audio frame holds at most a few thousand
                        // samples; anything else means AVFrame does not match the loaded
                        // library generation.
                        if (nbSamples < 0 || nbSamples > MaxSamplesPerFrame)
                        {
                            throw new InvalidDataException($"Unexpected libavcodec layout: frame holds {nbSamples} samples.");
                        }

                        if (nbSamples > 0)
                        {
                            ConvertFrame(Marshal.ReadIntPtr(_frame, FrameExtendedData), nbSamples);
                        }

                        _api._avFrameUnref(_frame);

                        if (_pendingCount > 0)
                        {
                            return true;
                        }

                        continue;   // an empty frame: pull the next one
                    }

                    if (result == AvErrorEof)
                    {
                        return false;   // the decoder is drained: end of stream
                    }

                    if (result != AvErrorEagain)
                    {
                        throw new InvalidDataException($"The audio decoder failed mid-stream ({result}).");
                    }

                    // EAGAIN: no frame is ready because the decoder wants input.
                    if (_inputDone)
                    {
                        // The flush was submitted; stalling here means the decoder ignores it.
                        throw new InvalidDataException("The audio decoder stalled after the input ended.");
                    }

                    if (!SubmitNextPacket())
                    {
                        _inputDone = true;

                        // End of input: flush the decoder so the frames it still buffers
                        // drain through the receive calls above (same sequence as Decode).
                        SendPacket(IntPtr.Zero);
                    }
                }
            }

            /// <summary>
            /// Demuxes forward until one packet of the audio stream has been submitted.
            /// Returns false when the input is exhausted; the caller then flushes the decoder.
            /// </summary>
            bool SubmitNextPacket()
            {
                while (_api._avReadFrame(_formatContext, _packet) >= 0)
                {
                    try
                    {
                        if (Marshal.ReadInt32(_packet, PacketStreamIndex) == _audioIndex)
                        {
                            SendPacket(_packet);

                            return true;
                        }
                    }
                    finally
                    {
                        _api._avPacketUnref(_packet);
                    }
                }

                return false;
            }

            /// <summary>
            /// avcodec_send_packet for one demuxed packet (or the null flush). The receive
            /// side is drained to EAGAIN after every submission, so a full input queue is
            /// impossible and EAGAIN is a hard error, exactly like Decode.
            /// </summary>
            void SendPacket(IntPtr packet)
            {
                int result = _api._avcodecSendPacket(_codecContext, packet);

                if (result == AvErrorEagain)
                {
                    throw new InvalidDataException($"The audio decoder refused a packet ({result}).");
                }

                if (result < 0)
                {
                    throw new InvalidDataException($"Failed to submit an audio packet to the decoder ({result}).");
                }
            }

            /// <summary>
            /// Converts one decoded frame into packed interleaved floats. The converter runs
            /// at the source rate, so it holds no delay and one frame always maps to its own
            /// samples plus slack.
            /// </summary>
            void ConvertFrame(IntPtr planes, int inSamples)
            {
                int needed = (inSamples + ResamplerSlack) * Channels;

                if (_pending.Length < needed)
                {
                    _pending = new float[needed];
                }

                if (_convertedCapacity < needed)
                {
                    if (_converted != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(_converted);
                    }

                    _converted = Marshal.AllocHGlobal(needed * sizeof(float));
                    _convertedCapacity = needed;
                }

                IntPtr outPtr = _converted;
                int converted = _api._swrConvert(_resampler, ref outPtr, inSamples + ResamplerSlack, planes, inSamples);

                if (converted < 0)
                {
                    throw new InvalidDataException($"Failed to convert the decoded audio ({converted}).");
                }

                int floats = converted * Channels;

                Marshal.Copy(_converted, _pending, 0, floats);

                _pendingOffset = 0;
                _pendingCount = floats;
            }

            /// <summary>Releases the session and the library handles it owns; the native free calls null their pointers, so Dispose is idempotent.</summary>
            public void Dispose()
            {
                if (_converted != IntPtr.Zero) { Marshal.FreeHGlobal(_converted); _converted = IntPtr.Zero; }
                if (_outLayout != IntPtr.Zero) { Marshal.FreeHGlobal(_outLayout); _outLayout = IntPtr.Zero; }
                if (_inLayout != IntPtr.Zero) { Marshal.FreeHGlobal(_inLayout); _inLayout = IntPtr.Zero; }
                if (_resampler != IntPtr.Zero) { _api._swrFree(ref _resampler); }
                if (_frame != IntPtr.Zero) { _api._avFrameFree(ref _frame); }
                if (_packet != IntPtr.Zero) { _api._avPacketFree(ref _packet); }
                if (_codecContext != IntPtr.Zero) { _api._avcodecFreeContext(ref _codecContext); }
                if (_formatContext != IntPtr.Zero) { _api._avformatCloseInput(ref _formatContext); }

                _api.Dispose();
            }
        }
    }
}
