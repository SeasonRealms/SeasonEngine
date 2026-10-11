// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Runtime.InteropServices;

namespace Season.Platforms.Linux;

/// <summary>
/// In-process microphone capture for Linux, the input-side counterpart of
/// <see cref="LinuxAudioPlayer"/> built on the same SDL3 audio stack: no external
/// recorder binary, one audio subsystem shared with playback, and the pinned
/// 16 kHz mono 16-bit PCM layout <see cref="IRecordService.StopRecord"/> promises
/// on every platform (identical to Windows AudioGraph and Android AudioRecord).
///
/// Capture uses the SDL3 device-stream API in its recording direction. The stream
/// is opened on the default recording device with the target format as the stream
/// format, so SDL converts and resamples whatever the physical device produces
/// (44.1/48 kHz, stereo, float) before the application sees it. Recording streams
/// are pull-model: a background pump thread polls <c>SDL_GetAudioStreamAvailable</c>
/// and reads ready bytes into memory, and <see cref="Stop"/> stops the pump and
/// wraps the captured PCM in the canonical 44-byte RIFF header.
///
/// <see cref="Start"/> reports why it failed through <see cref="Failure"/> so the UI
/// can distinguish "no input device" from "sandbox denied the microphone" - the two
/// need different remedies, exactly like <c>RecordFailure</c> on the other platforms.
/// Blocking (device open, and up to 3 s waiting for the pump on stop), so
/// <c>LinuxRecordService</c> runs every call on the thread pool.
/// </summary>
internal static class LinuxRecorder
{
    // The WAV output contract: 16 kHz mono 16-bit little-endian PCM (see IRecordService).
    // Public for the record service log line, which owns the app-log side.
    public const int SampleRate = 16000;
    public const int Channels = 1;
    public const int BitsPerSample = 16;

    /// <summary>Bytes pulled per pump iteration: 2048 samples, ~128 ms at 16 kHz.</summary>
    const int ChunkBytes = 4096;

    /// <summary>Guards <see cref="_session"/> and <see cref="_shutdown"/>.</summary>
    static readonly object _sync = new();

    static Session? _session;
    static bool _shutdown;   // Set by ShutdownForExit before SDL.Quit.

    /// <summary>Why <see cref="Start"/> failed; mirrors the platform RecordFailure values without the dependency.</summary>
    public enum Failure
    {
        /// <summary>No failure recorded; the last attempt either succeeded or never ran.</summary>
        None,

        /// <summary>Microphone permission was denied, by the sandbox or platform policy.</summary>
        Permission,

        /// <summary>No usable input device: none connected, none selected, or the device cannot be opened.</summary>
        Device,

        /// <summary>The recorder failed for a reason that fits neither of the above.</summary>
        Unknown
    }

    /// <summary>
    /// Starts capturing from the default recording device. Returns false when a
    /// recording is already running, the application is shutting down, the SDL audio
    /// subsystem cannot start, there is no input device, or the device refuses to
    /// open — <paramref name="failure"/> and <paramref name="error"/> then say why.
    /// </summary>
    public static bool Start(out Failure failure, out string? error)
    {
        failure = Failure.Unknown;
        error = null;

        lock (_sync)
        {
            if (_shutdown)
            {
                error = "the application is shutting down";

                return false;
            }

            if (_session != null)
            {
                // A second start is a no-op, not an error: the first session keeps
                // recording (same contract as WindowsRecordService).
                failure = Failure.None;
                error = "a recording is already in progress";

                return false;
            }

            try
            {
                // The host initializes SDL with video only; audio joins on the first
                // capture, exactly like the playback side.
                if (!SDL.InitSubSystem(SDL.InitAudio))
                {
                    failure = Failure.Device;
                    error = $"SDL audio subsystem unavailable ({SdlError()})";

                    return false;
                }

                // Enumerate up front: the open call would report the same condition
                // with a vaguer message, and the live device names make the log line
                // tell "no input at all" apart from "device present but not opening".
                string? devices = ListRecordingDevices();

                if (devices == null)
                {
                    failure = Failure.Device;
                    error = "no recording device available";

                    return false;
                }

                var spec = new SDL.AudioSpec
                {
                    Format = SDL.AudioFormatS16LE,
                    Channels = Channels,
                    Freq = SampleRate,
                };

                // The stream format is the output contract; SDL converts/resamples the
                // physical device's rate and channel count to it inside the stream.
                var stream = SDL.OpenAudioDeviceStream(SDL.AudioDeviceDefaultRecording, ref spec, IntPtr.Zero, IntPtr.Zero);

                if (stream == IntPtr.Zero)
                {
                    string sdlError = SdlError();

                    failure = IsPermissionError(sdlError) ? Failure.Permission : Failure.Device;
                    error = $"cannot open the default recording device '{devices}' ({sdlError})";

                    return false;
                }

                if (!SDL.ResumeAudioStreamDevice(stream))
                {
                    failure = Failure.Device;
                    error = $"cannot activate the recording stream ({SdlError()})";

                    SDL.DestroyAudioStream(stream);

                    return false;
                }

                var session = new Session(stream);

                _session = session;
                session.Thread.Start();

                Log($"recording from '{devices}' at {SampleRate}Hz/{Channels}ch/{BitsPerSample}bit");

                return true;
            }
            catch (Exception ex)
            {
                failure = Failure.Unknown;
                error = ex.Message;

                return false;
            }
        }
    }

    /// <summary>
    /// Stops the running recording and returns the captured audio as a complete
    /// WAV (16 kHz mono 16-bit PCM behind the canonical 44-byte RIFF header), or
    /// null when nothing was recording or no sample arrived. Safe to call after
    /// <see cref="ShutdownForExit"/>; a no-op then.
    /// </summary>
    public static byte[]? Stop()
    {
        Session? session;

        lock (_sync)
        {
            session = _session;
            _session = null;   // Claim the session: a concurrent second Stop returns null.
        }

        if (session == null)
        {
            return null;
        }

        session.RequestStop();

        // The pump exits on its own within a poll tick; anything longer means it is
        // stuck inside SDL, and the captured prefix is still consistent (the PCM
        // buffer is written and read under its own lock).
        if (!session.Thread.Join(3000))
        {
            Log("recording pump did not stop in time, returning the captured prefix");
        }

        var pcm = session.TakePcm();

        if (pcm.Length == 0)
        {
            return null;
        }

        Log($"recording stopped, {pcm.Length} PCM bytes captured");

        return BuildWav(pcm);
    }

    /// <summary>
    /// Stops any live recording so its stream is torn down before SDL.Quit reclaims
    /// the audio subsystem; called from the application's exit sequence next to
    /// <see cref="LinuxAudioPlayer.ShutdownForExit"/>. The stream is deliberately not
    /// destroyed here when the join times out - SDL.Quit closes every remaining
    /// device, the same trade-off the playback teardown makes.
    /// </summary>
    public static void ShutdownForExit()
    {
        Session? session;

        lock (_sync)
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            session = _session;
            _session = null;
        }

        if (session == null)
        {
            return;
        }

        session.RequestStop();
        session.Thread.Join(3000);
    }

    /// <summary>
    /// Names of every recording device, or null when the list is empty or cannot be
    /// read. Names are only for diagnostics.
    /// </summary>
    static string? ListRecordingDevices()
    {
        var devices = SDL.GetAudioRecordingDevices(out int count);

        if (devices == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (count <= 0)
            {
                return null;
            }

            var names = new string[count];

            for (int i = 0; i < count; i++)
            {
                uint id = (uint)Marshal.ReadInt32(devices, i * sizeof(uint));

                names[i] = Marshal.PtrToStringUTF8(SDL.GetAudioDeviceName(id)) ?? id.ToString();
            }

            return string.Join(", ", names);
        }
        finally
        {
            SDL.Free(devices);
        }
    }

    /// <summary>
    /// The SDL error text is the only signal a sandbox (Flatpak/Snap without the
    /// microphone permission) leaves; every other open failure means the audio
    /// system has no usable input.
    /// </summary>
    static bool IsPermissionError(string sdlError)
    {
        return sdlError.Contains("denied", StringComparison.OrdinalIgnoreCase)
            || sdlError.Contains("permission", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Drains ready bytes from the recording stream into the session buffer until a
    /// stop is requested and the queue is empty, then releases the stream. Runs on
    /// its own background thread; the buffer is the only shared state and has its
    /// own lock.
    /// </summary>
    static void PumpLoop(Session session)
    {
        var stream = session.Stream;
        var buffer = new byte[ChunkBytes];

        try
        {
            while (true)
            {
                int available = SDL.GetAudioStreamAvailable(stream);

                if (available <= 0)
                {
                    // Nothing ready: after a stop request this is the drain point,
                    // otherwise wait for the device to push captured audio.
                    if (session.Stop)
                    {
                        break;
                    }

                    Thread.Sleep(5);

                    continue;
                }

                // Never ask for more than is available: SDL_GetAudioStreamData blocks
                // until the requested byte count exists, and the device-fed queue never
                // shrinks on its own, so reading available bytes returns at once.
                int want = Math.Min(available, buffer.Length);
                int got = SDL.GetAudioStreamData(stream, buffer.AsSpan(0, want), want);

                if (got > 0)
                {
                    session.Append(buffer.AsSpan(0, got));
                }
                else if (got < 0)
                {
                    Log($"recording stream read failed ({SdlError()})");

                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"recording pump error: {ex.Message}");
        }
        finally
        {
            lock (_sync)
            {
                if (_session == session)
                {
                    _session = null;
                }

                // Skipped after shutdown when the join timed out; SDL.Quit reclaims
                // the remaining streams in that case (same as LinuxAudioPlayer).
                if (!_shutdown)
                {
                    SDL.DestroyAudioStream(stream);
                }
            }
        }
    }

    /// <summary>
    /// Wraps the captured PCM in the canonical 44-byte RIFF/WAVE header
    /// (16 kHz mono 16-bit little-endian), byte-for-byte the layout every platform's
    /// StopRecord returns and the only one the STT front ends parse.
    /// </summary>
    static byte[] BuildWav(byte[] pcm)
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
        writer.Write((short)Channels);
        writer.Write(SampleRate);
        writer.Write(SampleRate * Channels * BitsPerSample / 8);
        writer.Write((short)(Channels * BitsPerSample / 8));
        writer.Write((short)BitsPerSample);
        writer.Write("data"u8.ToArray());
        writer.Write(pcm.Length);
        writer.Write(pcm);

        writer.Flush();

        return bytes;
    }

    static string SdlError() => Marshal.PtrToStringUTF8(SDL.GetError()) ?? "";

    // Recording health lines are low-frequency (start result, device names, stop),
    // so they go to stdout, which always lands in the launcher log even when the app
    // log is disabled; the record service adds the app-log side (same split as
    // LinuxAudioPlayer).
    static void Log(string message)
    {
        Console.WriteLine($"{DateTime.UtcNow} [LinuxRecorder] {message}");
    }

    /// <summary>
    /// One live recording: the SDL stream, its pump thread, and the PCM captured so
    /// far. The buffer is written by the pump thread and read by Stop after the
    /// join, so it carries its own lock instead of relying on the join ordering.
    /// </summary>
    sealed class Session
    {
        readonly object _pcmLock = new();
        readonly MemoryStream _pcm = new();
        volatile bool _stopped;

        public Session(IntPtr stream)
        {
            Stream = stream;

            Thread = new Thread(() => PumpLoop(this))
            {
                IsBackground = true,
                Name = "SeasonAudioRecord",
            };
        }

        public IntPtr Stream { get; }

        public Thread Thread { get; }

        public bool Stop => _stopped;

        public void RequestStop() => _stopped = true;

        public void Append(ReadOnlySpan<byte> data)
        {
            lock (_pcmLock)
            {
                _pcm.Write(data);
            }
        }

        public byte[] TakePcm()
        {
            lock (_pcmLock)
            {
                return _pcm.ToArray();
            }
        }
    }
}
