// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using NLayer;

namespace Season.Platforms.Linux;

/// <summary>
/// In-process audio playback for Linux, replacing the mplayer child process
/// previously spawned by <c>LinuxMediaPlayer</c> so no external player has to be
/// installed on the user's machine.
///
/// Decoding is fully managed: NLayer for MP3 (music) and <see cref="WavDecoder"/>
/// for RIFF/WAVE (sound effects). Output goes through SDL3 audio streams on the
/// default playback device; SDL converts and resamples to the physical device
/// format, and per-stream gain gives real volume control. Music plays once and
/// stops at end of file, matching the previous per-platform behavior.
///
/// The legacy mplayer path remains as a fallback: <see cref="TryPlay"/> returns
/// false whenever the SDL audio stack or the decoder cannot serve the request,
/// and <c>LinuxMediaPlayer</c> then runs the child process exactly as before.
/// </summary>
internal static class LinuxAudioPlayer
{
    /// <summary>Sample frames decoded and pushed per iteration (one frame = one float per channel).</summary>
    const int ChunkFrames = 4096;

    /// <summary>Guards every field below and serializes stream use against stream destruction.</summary>
    static readonly object _sync = new();

    static Voice? _musicVoice;
    static Thread? _musicThread;
    static IntPtr _musicStream;   // Published for SetVolume/Pause/Resume; zeroed on teardown.
    static string? _musicFile;
    static readonly HashSet<IntPtr> _soundStreams = new();
    static readonly List<Voice> _soundVoices = new();
    static readonly List<Thread> _soundThreads = new();

    static bool _audioReady;   // SDL audio subsystem usable
    static bool _initFailed;   // Sticky: the subsystem refused to start, stop retrying.
    static bool _shutdown;     // Set by ShutdownForExit before SDL.Quit.

    /// <summary>
    /// Attempts to play <paramref name="file"/> in-process. Returns false when the
    /// caller should fall back to the legacy mplayer child process: unsupported
    /// container extension, missing file, undecodable stream, or the SDL audio
    /// stack being unavailable.
    /// </summary>
    public static bool TryPlay(string type, string file, int volume)
    {
        if (string.IsNullOrEmpty(file))
        {
            return false;
        }

        lock (_sync)
        {
            if (_shutdown || _initFailed)
            {
                return false;
            }
        }

        if (!File.Exists(file))
        {
            Log($"audio file not found: {file}");

            return false;
        }

        if (!EnsureAudioReady())
        {
            return false;
        }

        var decoder = OpenDecoder(file);

        if (decoder == null)
        {
            // Unknown extension or unreadable stream: let mplayer try (it may
            // support containers this backend does not).
            return false;
        }

        var stream = OpenStream(decoder);

        if (stream == IntPtr.Zero)
        {
            decoder.Dispose();

            return false;
        }

        if (type is "Music")
        {
            StartMusic(file, decoder, stream, volume);
        }
        else
        {
            StartSound(decoder, stream, volume);
        }

        return true;
    }

    /// <summary>True while music managed by this backend is playing.</summary>
    public static bool IsPlaying
    {
        get
        {
            lock (_sync)
            {
                return _musicStream != IntPtr.Zero;
            }
        }
    }

    /// <summary>True while the music channel is playing exactly this file.</summary>
    public static bool IsPlayingFile(string fileName)
    {
        lock (_sync)
        {
            return _musicStream != IntPtr.Zero && _musicFile != null && MediaPlayerFiles.IsSame(_musicFile, fileName);
        }
    }

    /// <summary>Stops managed music playback (no-op when nothing is playing).</summary>
    public static void StopMusic()
    {
        lock (_sync)
        {
            _musicVoice?.RequestStop();
        }
    }

    /// <summary>Applies 0-100 channel volumes to every live stream.</summary>
    public static void SetVolume(int music, int sound)
    {
        lock (_sync)
        {
            if (_musicStream != IntPtr.Zero)
            {
                SDL.SetAudioStreamGain(_musicStream, VolumeToGain(music));
            }

            foreach (var stream in _soundStreams)
            {
                SDL.SetAudioStreamGain(stream, VolumeToGain(sound));
            }
        }
    }

    public static void Pause()
    {
        lock (_sync)
        {
            if (_musicStream != IntPtr.Zero)
            {
                SDL.PauseAudioStreamDevice(_musicStream);
            }

            foreach (var stream in _soundStreams)
            {
                SDL.PauseAudioStreamDevice(stream);
            }
        }
    }

    public static void Resume()
    {
        lock (_sync)
        {
            if (_musicStream != IntPtr.Zero)
            {
                SDL.ResumeAudioStreamDevice(_musicStream);
            }

            foreach (var stream in _soundStreams)
            {
                SDL.ResumeAudioStreamDevice(stream);
            }
        }
    }

    /// <summary>
    /// Stops every voice and joins the worker threads. Must run before SDL.Quit:
    /// stream teardown touches SDL state and would be unsafe once SDL is shut down.
    /// </summary>
    public static void ShutdownForExit()
    {
        Thread[] threads;

        lock (_sync)
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;

            _musicVoice?.RequestStop();

            foreach (var voice in _soundVoices)
            {
                voice.RequestStop();
            }

            threads = new[] { _musicThread }.Concat(_soundThreads).Where(t => t != null).ToArray()!;
        }

        // Never join while holding _sync: the loops take that lock in their cleanup path.
        long deadline = Environment.TickCount64 + 3000;

        foreach (var thread in threads)
        {
            int wait = (int)(deadline - Environment.TickCount64);

            if (wait <= 0)
            {
                break;
            }

            thread.Join(wait);
        }
    }

    static bool EnsureAudioReady()
    {
        lock (_sync)
        {
            if (_audioReady)
            {
                return true;
            }

            if (_initFailed)
            {
                return false;
            }

            // The host initializes SDL with video only; audio joins on first playback.
            if (!SDL.InitSubSystem(SDL.InitAudio))
            {
                _initFailed = true;

                Log($"SDL audio subsystem unavailable ({SdlError()}), falling back to mplayer");

                return false;
            }

            _audioReady = true;

            Log("SDL audio backend active; playing in-process instead of spawning mplayer");

            return true;
        }
    }

    static IntPtr OpenStream(Decoder decoder)
    {
        // The application-side format is 32-bit signed PCM: some pulse servers
        // (observed on WSLg) reject float32 streams outright, and S32 keeps full
        // decoded precision for the 16/24-bit content the engine ships. SDL
        // converts and resamples to whatever the physical device wants.
        var spec = new SDL.AudioSpec
        {
            Format = SDL.AudioFormatS32LE,
            Channels = decoder.Channels,
            Freq = decoder.SampleRate,
        };

        var stream = SDL.OpenAudioDeviceStream(SDL.AudioDeviceDefaultPlayback, ref spec, IntPtr.Zero, IntPtr.Zero);

        if (stream == IntPtr.Zero)
        {
            Log($"cannot open audio device ({SdlError()}), falling back to mplayer");
        }

        return stream;
    }

    static Decoder? OpenDecoder(string file)
    {
        var ext = Path.GetExtension(file);

        try
        {
            if (string.Equals(ext, ".mp3", StringComparison.OrdinalIgnoreCase))
            {
                return new Mp3Decoder(file);
            }

            if (string.Equals(ext, ".wav", StringComparison.OrdinalIgnoreCase))
            {
                return new WavDecoder(file);
            }
        }
        catch (Exception ex)
        {
            Log($"cannot decode '{file}': {ex.Message}");
        }

        return null;
    }

    static void StartMusic(string file, Decoder decoder, IntPtr stream, int volume)
    {
        SDL.SetAudioStreamGain(stream, VolumeToGain(volume));

        var voice = new Voice();
        var thread = new Thread(() => MusicLoop(voice, decoder, stream))
        {
            IsBackground = true,
            Name = "SeasonAudioMusic",
        };

        lock (_sync)
        {
            _musicVoice?.RequestStop();
            _musicVoice = voice;
            _musicFile = file;
            _musicStream = stream;
            _musicThread = thread;
        }

        thread.Start();
    }

    static void StartSound(Decoder decoder, IntPtr stream, int volume)
    {
        SDL.SetAudioStreamGain(stream, VolumeToGain(volume));

        var voice = new Voice();
        var thread = new Thread(() => SoundLoop(voice, decoder, stream))
        {
            IsBackground = true,
            Name = "SeasonAudioSound",
        };

        lock (_sync)
        {
            if (_shutdown)
            {
                // Shutdown raced past the early check in TryPlay; make the loop bail out.
                voice.RequestStop();
            }

            _soundVoices.Add(voice);
            _soundStreams.Add(stream);
            _soundThreads.RemoveAll(t => !t.IsAlive);
            _soundThreads.Add(thread);
        }

        thread.Start();
    }

    static void MusicLoop(Voice voice, Decoder decoder, IntPtr stream)
    {
        try
        {
            if (SDL.ResumeAudioStreamDevice(stream))
            {
                var samplesBuffer = new float[ChunkFrames * decoder.Channels];
                var pcmBuffer = new byte[samplesBuffer.Length * sizeof(int)];
                int queueLimit = QueueLimitBytes(decoder);

                while (!voice.Stop)
                {
                    int samples = decoder.Read(samplesBuffer, 0, samplesBuffer.Length);

                    if (samples <= 0)
                    {
                        break;   // End of file: single-shot playback, the track stops here.
                    }

                    WaitForQueue(stream, queueLimit, voice);

                    if (voice.Stop)
                    {
                        break;
                    }

                    int bytes = ConvertFloatToS32(samplesBuffer.AsSpan(0, samples), pcmBuffer);

                    if (!SDL.PutAudioStreamData(stream, pcmBuffer.AsSpan(0, bytes), bytes))
                    {
                        break;
                    }
                }

                // Natural end: let the queued tail (~half a second) reach the device.
                if (!voice.Stop)
                {
                    DrainStream(stream, voice);
                }
            }
        }
        catch (Exception ex)
        {
            Log($"music playback error: {ex.Message}");
        }
        finally
        {
            lock (_sync)
            {
                if (_musicStream == stream)
                {
                    _musicStream = IntPtr.Zero;
                    _musicFile = null;
                }

                if (_musicVoice == voice)
                {
                    _musicVoice = null;
                }

                // Skipped after shutdown when the join timed out; SDL.Quit reclaims
                // the remaining streams in that case.
                if (!_shutdown)
                {
                    SDL.DestroyAudioStream(stream);
                }
            }

            decoder.Dispose();
        }
    }

    static void SoundLoop(Voice voice, Decoder decoder, IntPtr stream)
    {
        try
        {
            if (SDL.ResumeAudioStreamDevice(stream))
            {
                var samplesBuffer = new float[ChunkFrames * decoder.Channels];
                var pcmBuffer = new byte[samplesBuffer.Length * sizeof(int)];
                int queueLimit = QueueLimitBytes(decoder);

                while (!voice.Stop)
                {
                    int samples = decoder.Read(samplesBuffer, 0, samplesBuffer.Length);

                    if (samples <= 0)
                    {
                        break;
                    }

                    WaitForQueue(stream, queueLimit, voice);

                    if (voice.Stop)
                    {
                        break;
                    }

                    int bytes = ConvertFloatToS32(samplesBuffer.AsSpan(0, samples), pcmBuffer);

                    if (!SDL.PutAudioStreamData(stream, pcmBuffer.AsSpan(0, bytes), bytes))
                    {
                        break;
                    }
                }

                // Short effect: wait for the tail so the last chunk is not cut off.
                if (!voice.Stop)
                {
                    DrainStream(stream, voice);
                }
            }
        }
        catch (Exception ex)
        {
            Log($"sound playback error: {ex.Message}");
        }
        finally
        {
            lock (_sync)
            {
                _soundStreams.Remove(stream);
                _soundVoices.Remove(voice);
                _soundThreads.Remove(Thread.CurrentThread);

                if (!_shutdown)
                {
                    SDL.DestroyAudioStream(stream);
                }
            }

            decoder.Dispose();
        }
    }

    /// <summary>
    /// Push-as-fast-as-buffered pacing: wait while roughly half a second of audio is
    /// already queued ahead of the device, so the queue cannot grow without bound.
    /// </summary>
    static void WaitForQueue(IntPtr stream, int limit, Voice voice)
    {
        while (!voice.Stop && SDL.GetAudioStreamQueued(stream) > limit)
        {
            Thread.Sleep(10);
        }
    }

    /// <summary>Waits until the device consumed the queued tail, capped at ~15 seconds.</summary>
    static void DrainStream(IntPtr stream, Voice voice)
    {
        for (int i = 0; i < 1500 && !voice.Stop && SDL.GetAudioStreamQueued(stream) > 0; i++)
        {
            Thread.Sleep(10);
        }
    }

    static int QueueLimitBytes(Decoder decoder) => decoder.SampleRate * decoder.Channels * sizeof(float) / 2;

    /// <summary>
    /// Converts interleaved float samples to signed 32-bit PCM bytes. Scaling by the
    /// largest float below 2^31 avoids the rounding overflow that int.MaxValue would
    /// hit when a sample is exactly 1.0.
    /// </summary>
    static int ConvertFloatToS32(ReadOnlySpan<float> source, byte[] destination)
    {
        var target = MemoryMarshal.Cast<byte, int>(destination.AsSpan(0, source.Length * sizeof(int)));

        for (int i = 0; i < source.Length; i++)
        {
            target[i] = (int)(Math.Clamp(source[i], -1f, 1f) * 2147483520f);
        }

        return source.Length * sizeof(int);
    }

    static float VolumeToGain(int volume) => Math.Clamp(volume, 0, 100) / 100f;

    static string SdlError() => Marshal.PtrToStringUTF8(SDL.GetError()) ?? "";

    // Audio health lines are low-frequency (init result, fallback reasons, playback
    // errors), so they go to stdout as well as the app log: stdout always lands in
    // the launcher log even when the app log is disabled.
    static void Log(string message)
    {
        string line = $"{DateTime.UtcNow} [LinuxAudioPlayer] {message}";

        DeviceServices.BaseApp?.AddLog(LogType.None, line);

        Console.WriteLine(line);
    }

    /// <summary>Stop flag shared between the audio worker thread and control threads.</summary>
    sealed class Voice
    {
        volatile bool _stopped;

        public bool Stop => _stopped;

        public void RequestStop() => _stopped = true;
    }

    /// <summary>PCM source shared by the music/sound loops; read from the owning voice thread only.</summary>
    abstract class Decoder : IDisposable
    {
        public abstract int SampleRate { get; }

        public abstract int Channels { get; }

        /// <summary>Fills the buffer with interleaved floats; returns the number of floats written (0 = end).</summary>
        public abstract int Read(float[] buffer, int offset, int count);

        public abstract void Dispose();
    }

    sealed class Mp3Decoder : Decoder
    {
        readonly MpegFile _file;

        public Mp3Decoder(string path) => _file = new MpegFile(path);

        public override int SampleRate => _file.SampleRate;

        public override int Channels => _file.Channels;

        public override int Read(float[] buffer, int offset, int count) => _file.ReadSamples(buffer, offset, count);

        public override void Dispose() => _file.Dispose();
    }

    /// <summary>
    /// Minimal RIFF/WAVE reader for engine sound effects: PCM 8/16/24/32-bit and
    /// 32-bit float, including WAVE_FORMAT_EXTENSIBLE containers.
    /// </summary>
    sealed class WavDecoder : Decoder
    {
        enum SampleFormat { Pcm8U, Pcm16, Pcm24, Pcm32, Float32 }

        readonly FileStream _file;
        readonly SampleFormat _format;
        readonly int _sampleBytes;
        readonly long _dataSize;
        readonly int _sampleRate;
        readonly int _channels;
        long _dataPos;
        byte[] _raw = new byte[4096];

        public override int SampleRate => _sampleRate;

        public override int Channels => _channels;

        int BytesPerFrame => _sampleBytes * _channels;

        public WavDecoder(string path)
        {
            _file = File.OpenRead(path);

            try
            {
                using var reader = new BinaryReader(_file, Encoding.ASCII, leaveOpen: true);

                if (new string(reader.ReadChars(4)) != "RIFF")
                {
                    throw new InvalidDataException("missing RIFF header");
                }

                reader.ReadUInt32();   // RIFF chunk size

                if (new string(reader.ReadChars(4)) != "WAVE")
                {
                    throw new InvalidDataException("not a WAVE file");
                }

                int formatTag = 0, bitsPerSample = 0;
                long dataStart = 0;
                bool haveFormat = false, haveData = false;

                while (!(haveFormat && haveData) && _file.Position + 8 <= _file.Length)
                {
                    string chunkId = new(reader.ReadChars(4));
                    uint chunkSize = reader.ReadUInt32();
                    long nextChunk = _file.Position + chunkSize + (chunkSize % 2);   // Chunks are word-aligned.

                    if (chunkId == "fmt ")
                    {
                        formatTag = reader.ReadUInt16();
                        _channels = reader.ReadUInt16();
                        _sampleRate = (int)reader.ReadUInt32();
                        reader.ReadUInt32();   // Average bytes per second.
                        reader.ReadUInt16();   // Block align.
                        bitsPerSample = reader.ReadUInt16();

                        if (formatTag == 0xFFFE && chunkSize >= 40)
                        {
                            // WAVE_FORMAT_EXTENSIBLE: the effective tag leads the SubFormat GUID.
                            reader.ReadUInt16();   // cbSize
                            reader.ReadUInt16();   // Valid bits per sample.
                            reader.ReadUInt32();   // Channel mask.
                            formatTag = reader.ReadUInt16();
                        }

                        haveFormat = true;
                    }
                    else if (chunkId == "data")
                    {
                        dataStart = _file.Position;
                        _dataSize = chunkSize;

                        haveData = true;
                    }

                    _file.Position = Math.Min(nextChunk, _file.Length);
                }

                if (!haveFormat || !haveData || _channels <= 0 || _sampleRate <= 0)
                {
                    throw new InvalidDataException("missing fmt or data chunk");
                }

                switch (formatTag, bitsPerSample)
                {
                    case (1, 8): _format = SampleFormat.Pcm8U; _sampleBytes = 1; break;
                    case (1, 16): _format = SampleFormat.Pcm16; _sampleBytes = 2; break;
                    case (1, 24): _format = SampleFormat.Pcm24; _sampleBytes = 3; break;
                    case (1, 32): _format = SampleFormat.Pcm32; _sampleBytes = 4; break;
                    case (3, 32): _format = SampleFormat.Float32; _sampleBytes = 4; break;
                    default: throw new InvalidDataException($"unsupported WAV encoding tag={formatTag} bits={bitsPerSample}");
                }

                _file.Position = dataStart;
            }
            catch
            {
                _file.Dispose();

                throw;
            }
        }

        public override int Read(float[] buffer, int offset, int count)
        {
            int frames = count / _channels;

            if (frames <= 0)
            {
                return 0;
            }

            long remaining = _dataSize - _dataPos;

            if (remaining < BytesPerFrame)
            {
                return 0;
            }

            long want = Math.Min(remaining, (long)frames * BytesPerFrame);
            int byteCount = (int)(want - want % BytesPerFrame);

            if (_raw.Length < byteCount)
            {
                _raw = new byte[byteCount];
            }

            int read = 0;

            while (read < byteCount)
            {
                int n = _file.Read(_raw, read, byteCount - read);

                if (n <= 0)
                {
                    break;
                }

                read += n;
            }

            int framesRead = read / BytesPerFrame;

            if (framesRead <= 0)
            {
                return 0;
            }

            _dataPos += (long)framesRead * BytesPerFrame;

            int written = 0;

            for (int frame = 0; frame < framesRead; frame++)
            {
                int baseIndex = frame * BytesPerFrame;

                for (int channel = 0; channel < _channels; channel++)
                {
                    buffer[offset + written++] = DecodeSample(baseIndex + channel * _sampleBytes);
                }
            }

            return written;
        }

        float DecodeSample(int index) => _format switch
        {
            SampleFormat.Pcm8U => (_raw[index] - 128) / 128f,
            SampleFormat.Pcm16 => BitConverter.ToInt16(_raw, index) / 32768f,
            SampleFormat.Pcm24 => ((_raw[index] | (_raw[index + 1] << 8) | (_raw[index + 2] << 16)) << 8 >> 8) / 8388608f,
            SampleFormat.Pcm32 => BitConverter.ToInt32(_raw, index) / 2147483648f,
            _ => BitConverter.ToSingle(_raw, index),
        };

        public override void Dispose() => _file.Dispose();
    }
}
