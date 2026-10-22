// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Foundation;
using UIKit;
using Metal;
using AVFoundation;
using CoreMedia;
using CoreGraphics;
using StoreKit;

namespace Season.Platforms.Shared.Apple;

//Need Record Permission
internal class AppleRecordService : RecordService, IRecordService
{
    //Record (Record Permission)

    // Output contract shared with the Android/Windows/Web recorders: 16 kHz, mono, 16-bit
    // little-endian PCM in a .wav container, handed back as byte[] by StopRecord.
    const double RecordSampleRate = 16000;

    const int RecordChannels = 1;

    const int RecordBitsPerSample = 16;

    // Fallback capture rate: the Mac hardware sample rate, used to tell whether a refused Record()
    // is about the 16 kHz request rather than about the input itself.
    const double RecordHardwareSampleRate = 48000;

    // The binding exposes no "None" member for the options flag enum.
    const AVAudioSessionCategoryOptions NoCategoryOptions = (AVAudioSessionCategoryOptions)0;

    /// <summary>One AVAudioRecorder configuration to try; see <see cref="Attempts"/>.</summary>
    readonly record struct RecordAttempt(string Name, AVAudioSessionCategory Category, AVAudioSessionCategoryOptions Options, double SampleRate, bool ConfigureSession, bool Prepare);

    // AVAudioRecorder configurations, plainest first: AllowBluetooth/DefaultToSpeaker are iOS-only
    // options that Mac Catalyst accepts and then ignores, and its AVAudioSession is just a shim over
    // CoreAudio, so every step drops one more iOS assumption. The first attempt whose Record()
    // actually starts wins, and each attempt logs the session state it ended up with.
    static readonly RecordAttempt[] Attempts =
    {
        new("record-16k", AVAudioSessionCategory.Record, NoCategoryOptions, RecordSampleRate, true, true),
        new("record-16k-noprepare", AVAudioSessionCategory.Record, NoCategoryOptions, RecordSampleRate, true, false),
        new("playandrecord-16k", AVAudioSessionCategory.PlayAndRecord, NoCategoryOptions, RecordSampleRate, true, true),
        new("record-16k-nosession", AVAudioSessionCategory.Record, NoCategoryOptions, RecordSampleRate, false, true),
        new("record-48k", AVAudioSessionCategory.Record, NoCategoryOptions, RecordHardwareSampleRate, true, true),
        new("playandrecord-16k-iosoptions", AVAudioSessionCategory.PlayAndRecord, AVAudioSessionCategoryOptions.DefaultToSpeaker | AVAudioSessionCategoryOptions.AllowBluetooth, RecordSampleRate, true, true)
    };

    AVAudioRecorder recorder = null;

    TaskCompletionSource<byte[]> tcsRecord = null;

    // Absolute path of the temp .wav the live recorder writes to, kept so StopRecord can delete
    // it once the bytes have been read back.
    string recordFile = null;

#if MACCATALYST
    // AVAudioEngine capture state: the Mac Catalyst path, which reaches CoreAudio without going
    // through the AVAudioSession shim at all.
    AVAudioEngine captureEngine = null;

    // Strong reference to the installed tap block: it is invoked on the audio render thread and has
    // to stay alive for as long as the engine runs.
    AVAudioNodeTapBlock captureTap = null;

    object captureGate = null;

    List<float> captureSamples = null;

    double captureSampleRate = 0;
#endif

    public async Task<bool> StartRecord()
    {
        LastFailure = RecordFailure.None;

        var permissions = new string[]
        {
            "RECORD_AUDIO"
        };

        var hasPermission = await DeviceServices.Core.RequestPermissionAsync(permissions);

        if (!hasPermission)
        {
            Log(LogType.Error, "StartRecord aborted, microphone permission not granted");

            LastFailure = RecordFailure.Permission;

            return false;
        }

        // AVAudioSession and AVAudioRecorder have to be driven from the main thread: the permission
        // await above resumes on an AVFoundation callback queue, and configuring the session off
        // the main run loop is on its own enough to make PrepareToRecord return false. A recorder
        // left over from a session that was never stopped is released there too.
        var tcs = new TaskCompletionSource<bool>();

        UIApplication.SharedApplication.InvokeOnMainThread(delegate
        {
            tcs.TrySetResult(StartRecorder());
        });

        var success = await tcs.Task;

        if (!success)
        {
            // Not a bare SetActive(false): the failed attempt already put the session on
            // an input-only category, and leaving it there would silence every later play.
            AppleAudioSession.ReleaseCapture();
        }

        return success;
    }

    bool StartRecorder()
    {
        try
        {
            ReleaseStaleRecorder();

            var session = AVAudioSession.SharedInstance();

            LogSessionState(session);

#if MACCATALYST
            // The session starts out in SoloAmbient, a category with no input route at all, and on Mac
            // Catalyst that leaves AVAudioEngine.InputNode without a hardware format (CommonFormat
            // Other, 0 Hz, 0 channels) even though the microphone itself works, so the session has to
            // be brought into an input-capable category and activated before the engine can see any
            // hardware. PlayAndRecord rather than Record because the engine also instantiates its
            // output node, and because the app keeps playing sound while a recording is armed.
            AppleAudioSession.ApplyCapture(session, AVAudioSessionCategory.PlayAndRecord, NoCategoryOptions, true);

            LogRouteState(session, "session");

            // Fail fast when the machine has no microphone: neither AVAudioRecorder nor AVAudioEngine
            // reports a usable error in that case, PrepareToRecord still succeeds and writes a 4 KB
            // WAV skeleton, Record() just returns false, and every rung of the ladder below then
            // spends seconds re-activating a session that can never get a route. Mac mini and Mac
            // Studio ship without a built-in microphone.
            if (!ProbeInputDevice(out string inputDevice))
            {
                Log(LogType.Error, $"no input device ({inputDevice}): CoreAudio exposes no microphone to this Mac, connect a USB, Bluetooth or 3.5mm microphone and select it under System Settings > Sound > Input");

                LastFailure = RecordFailure.Device;

                return false;
            }

            Log(LogType.None, $"input device present: {inputDevice}");

            // AVAudioRecorder reads its samples through the AVAudioSession route, while the engine's
            // input node is wired to the CoreAudio HAL, so the engine is the more direct path here.
            if (TryStartEngineCapture())
            {
                return true;
            }
#endif

            foreach (var attempt in Attempts)
            {
                if (TryStartRecording(session, attempt))
                {
                    return true;
                }
            }

            Log(LogType.Error, "StartRecord failed for every capture path, check System Settings > Privacy & Security > Microphone (an app launched from a terminal or IDE inherits that parent's microphone status) and System Settings > Sound > Input for a selected device");

            LastFailure = RecordFailure.Unknown;

            return false;
        }
        catch (Exception ex)
        {
            Log(LogType.Error, $"StartRecord failed: {ex}");

            LastFailure = RecordFailure.Unknown;

            return false;
        }
    }

    bool TryStartRecording(AVAudioSession session, RecordAttempt attempt)
    {
        // On Mac Catalyst the audio session is only a partial shim of the iOS one, so a rejected
        // category or activation is logged and recording is attempted anyway: the recorder is the
        // authority on whether an input is really reachable.
        if (attempt.ConfigureSession)
        {
            // Tracks the session as capture-owned so a play issued during the recording
            // does not switch the category out from under the recorder.
            AppleAudioSession.ApplyCapture(session, attempt.Category, attempt.Options, true);
        }
        else
        {
            // Leave the session untouched (and deactivated) to tell a recorder-settings problem apart
            // from a session-route problem.
            AppleAudioSession.ReleaseCapture();
        }

        LogRouteState(session, attempt.Name);

        var audioFile = Path.Combine(Path.GetTempPath(), $"Record-{DateTime.Now:yyyyMMddHHmmssfff}-{attempt.Name}.wav");

        Directory.CreateDirectory(Path.GetDirectoryName(audioFile));

        var audioSettings = new AudioSettings
        {
            SampleRate = attempt.SampleRate,
            NumberChannels = RecordChannels,
            AudioQuality = AVAudioQuality.High,
            Format = AudioToolbox.AudioFormatType.LinearPCM,
            LinearPcmBitDepth = RecordBitsPerSample,
            // WAV is little-endian integer PCM; leaving these unset lets the encoder fall back to
            // its own defaults and produces a file the STT backends cannot decode.
            LinearPcmFloat = false,
            LinearPcmBigEndian = false
        };

        recorder = AVAudioRecorder.Create(NSUrl.FromFilename(audioFile), audioSettings, out NSError createError);

        if (recorder == null)
        {
            Log(LogType.Error, $"{attempt.Name}: AVAudioRecorder.Create({audioFile}) failed: {Describe(createError)}");

            DeleteTempFile(audioFile);

            return false;
        }

        // The completion source is captured locally: StopRecord nulls the field before this callback
        // is delivered, and reading the field here would throw and leave the caller awaiting a task
        // that never completes.
        var tcs = new TaskCompletionSource<byte[]>();

        tcsRecord = tcs;

        recordFile = audioFile;

        // Set once this attempt gives up: Stop still queues FinishedRecording, and the callback would
        // otherwise read a temp file that the failure path has just deleted.
        var abandoned = false;

        recorder.FinishedRecording += (s, e) =>
        {
            byte[] bytes = null;

            try
            {
                if (!abandoned && e.Status && System.IO.File.Exists(audioFile))
                {
                    bytes = System.IO.File.ReadAllBytes(audioFile);
                }
            }
            catch (Exception ex)
            {
                Log(LogType.Error, $"FinishedRecording read failed: {ex.Message}");
            }

            tcs.TrySetResult(bytes);
        };

        var prepared = !attempt.Prepare || recorder.PrepareToRecord();

        // Deliberately no RecordAt(DeviceCurrentTime + delay) retry here: it blocks the calling thread
        // until the scheduled device time and, when the reason for the refusal is a missing input
        // rather than timing, it adds tens of seconds of frozen UI for a guaranteed false.
        var started = prepared && recorder.Record();

        // bytes is the decisive fact: PrepareToRecord alone writes a 4 KB WAV skeleton (RIFF/JUNK/fmt
        // /FLLR, no data chunk), so a non-zero size still says nothing about audio having been read.
        Log(started ? LogType.None : LogType.Error, $"{attempt.Name}: prepareToRecord={prepared} record={started} recording={recorder.Recording} bytes={FileLength(audioFile)} createError={Describe(createError)} file={audioFile}");

        if (!started)
        {
            abandoned = true;

            try { recorder.Stop(); }
            catch { }

            recorder.Dispose();

            recorder = null;

            tcsRecord = null;

            recordFile = null;

            // Release the session so the next category can be activated cleanly.
            AppleAudioSession.ReleaseCapture();

            DeleteTempFile(audioFile);

            return false;
        }

        // The recorder may have started on a rung that deliberately left the session
        // untouched; the capture still owns it until StopRecord.
        AppleAudioSession.Recording = true;

        return true;
    }

    /// <summary>
    /// Logs the route the session actually ended up with, which is the fact that decides whether
    /// AVAudioRecorder can ever start. inputAvailable=false with a non-empty availableInputs means the
    /// session shim never built an input route; both empty means macOS gave the process no input
    /// device at all. Purely diagnostic, so a failure here must never abort the attempt.
    /// </summary>
    static void LogRouteState(AVAudioSession session, string tag)
    {
        try
        {
            var inputs = session.CurrentRoute?.Inputs.NullToEmptyArray().Select(input => $"{input.PortType} {input.PortName}").ToArray();

            var available = session.AvailableInputs.NullToEmptyArray().Select(input => $"{input.PortType} {input.PortName}").ToArray();

            Log(LogType.None, $"{tag}: applied category={session.Category} options={session.CategoryOptions} sampleRate={session.SampleRate} inputAvailable={session.InputAvailable} inputs=[{string.Join(", ", inputs.NullToEmptyArray())}] availableInputs=[{string.Join(", ", available.NullToEmptyArray())}]");
        }
        catch (Exception ex)
        {
            Log(LogType.None, $"{tag}: route state unavailable: {ex.Message}");
        }
    }

    static long FileLength(string file)
    {
        try
        {
            return file != null && System.IO.File.Exists(file) ? new FileInfo(file).Length : -1;
        }
        catch
        {
            return -1;
        }
    }

#if MACCATALYST
    /// <summary>
    /// Reports whether a microphone exists at all, through AVCaptureDevice discovery. That is the
    /// right authority because it is independent of AVAudioSession: AVAudioEngine.InputNode has no
    /// hardware format until the session sits in an input-capable category and is active, so probing
    /// through the engine reads CommonFormat Other / 0 Hz / 0 channels on a machine whose microphone
    /// works perfectly and would short-circuit every capture path behind it. GetDefaultDevice is the
    /// AVFoundation counterpart of CoreAudio's default input device.
    /// </summary>
    static bool ProbeInputDevice(out string detail)
    {
        detail = "none";

        try
        {
            var device = AVCaptureDevice.GetDefaultDevice(AVMediaTypes.Audio);

            if (device == null)
            {
                return false;
            }

            detail = $"{device.LocalizedName} connected={device.Connected} uniqueId={device.UniqueID}";

            return device.Connected;
        }
        catch (Exception ex)
        {
            detail = ex.Message;

            return false;
        }
    }

    /// <summary>
    /// Captures through a tap on an AVAudioEngine input node instead of AVAudioRecorder. The recorder
    /// reads its samples through the AVAudioSession route, and the Mac Catalyst session shim reports
    /// inputAvailable=false with no route inputs even after the category was accepted, the session was
    /// activated and TCC granted the microphone, which makes Record() answer false while
    /// PrepareToRecord already wrote the WAV skeleton. The engine's input node is wired straight to the
    /// CoreAudio HAL and does not depend on that route; the float samples it delivers are converted
    /// here to the same 16 kHz mono PCM16 WAV the other platforms hand back.
    /// </summary>
    bool TryStartEngineCapture()
    {
        AVAudioEngine engine = null;

        try
        {
            engine = new AVAudioEngine();

            var inputNode = engine.InputNode;

            if (inputNode == null)
            {
                Log(LogType.Error, "engine: no input node");

                return false;
            }

            var format = inputNode.GetBusOutputFormat((nuint)0);

            // Only float32 exposes a FloatChannelData pointer; any other common format would need a
            // conversion node in between, and the recorder ladder below is the cheaper fallback.
            if (format == null || format.CommonFormat != AVAudioCommonFormat.PCMFloat32 || format.SampleRate <= 0 || format.ChannelCount == 0)
            {
                Log(LogType.Error, $"engine: unusable input format commonFormat={format?.CommonFormat} sampleRate={format?.SampleRate} channels={format?.ChannelCount}");

                return false;
            }

            var gate = new object();

            var samples = new List<float>();

            var channels = (int)format.ChannelCount;

            // Deinterleaved float32 (what an input node normally reports) hands over one pointer per
            // channel; interleaved keeps them all in the first buffer with a stride.
            var interleaved = format.Interleaved;

            // Runs on the audio render thread, so it must not throw, must not touch the audio session
            // and must keep its per-callback allocation down to the mono scratch buffer.
            var tap = new AVAudioNodeTapBlock((buffer, when) =>
            {
                try
                {
                    var frames = (int)buffer.FrameLength;

                    if (frames <= 0)
                    {
                        return;
                    }

                    var channelPointers = buffer.FloatChannelData;

                    if (channelPointers == IntPtr.Zero)
                    {
                        return;
                    }

                    var mono = new float[frames];

                    var data = interleaved ? new float[frames * channels] : new float[frames];

                    if (interleaved)
                    {
                        var pointer = System.Runtime.InteropServices.Marshal.ReadIntPtr(channelPointers, 0);

                        if (pointer != IntPtr.Zero)
                        {
                            System.Runtime.InteropServices.Marshal.Copy(pointer, data, 0, data.Length);

                            for (var i = 0; i < frames; i++)
                            {
                                var sum = 0f;

                                for (var channel = 0; channel < channels; channel++)
                                {
                                    sum += data[i * channels + channel];
                                }

                                mono[i] = sum / channels;
                            }
                        }
                    }
                    else
                    {
                        for (var channel = 0; channel < channels; channel++)
                        {
                            var pointer = System.Runtime.InteropServices.Marshal.ReadIntPtr(channelPointers, channel * IntPtr.Size);

                            if (pointer == IntPtr.Zero)
                            {
                                continue;
                            }

                            System.Runtime.InteropServices.Marshal.Copy(pointer, data, 0, frames);

                            for (var i = 0; i < frames; i++)
                            {
                                mono[i] += data[i];
                            }
                        }

                        if (channels > 1)
                        {
                            for (var i = 0; i < frames; i++)
                            {
                                mono[i] /= channels;
                            }
                        }
                    }

                    lock (gate)
                    {
                        samples.AddRange(mono);
                    }
                }
                catch
                {
                    // Never propagate into the render thread.
                }
            });

            inputNode.InstallTapOnBus((nuint)0, (uint)4096, format, tap);

            engine.Prepare();

            if (!engine.StartAndReturnError(out NSError startError))
            {
                Log(LogType.Error, $"engine: StartAndReturnError failed: {Describe(startError)}");

                return false;
            }

            // captureTap keeps the block alive: without a strong reference it can be collected while
            // the engine is still running, which crashes the render thread.
            captureEngine = engine;

            captureTap = tap;

            captureGate = gate;

            captureSamples = samples;

            captureSampleRate = format.SampleRate;

            Log(LogType.None, $"engine: capturing sampleRate={captureSampleRate} channels={channels} commonFormat={format.CommonFormat} interleaved={interleaved} running={engine.Running}");

            return true;
        }
        catch (Exception ex)
        {
            Log(LogType.Error, $"engine: capture start failed: {ex.Message}");

            return false;
        }
        finally
        {
            // Torn down here only when the engine never became the live capture; ownership moved to
            // captureEngine otherwise.
            if (engine != null && !ReferenceEquals(engine, captureEngine))
            {
                try { engine.InputNode.RemoveTapOnBus((nuint)0); }
                catch { }

                try { engine.Dispose(); }
                catch { }
            }
        }
    }

    /// <summary>
    /// Stops the tap, resamples what it collected and packs it into the shared WAV contract.
    /// </summary>
    byte[] FinishEngineCapture()
    {
        var engine = captureEngine;

        var gate = captureGate;

        var samples = captureSamples;

        var rate = captureSampleRate;

        captureEngine = null;

        captureTap = null;

        captureGate = null;

        captureSamples = null;

        captureSampleRate = 0;

        if (engine == null || samples == null || gate == null)
        {
            return null;
        }

        try
        {
            engine.InputNode.RemoveTapOnBus((nuint)0);
        }
        catch (Exception ex)
        {
            Log(LogType.None, $"engine: RemoveTapOnBus failed: {ex.Message}");
        }

        try
        {
            engine.Stop();

            engine.Dispose();
        }
        catch (Exception ex)
        {
            Log(LogType.None, $"engine: shutdown failed: {ex.Message}");
        }

        float[] captured;

        lock (gate)
        {
            captured = samples.ToArray();
        }

        var audio = Resample(captured, rate, RecordSampleRate);

        Log(LogType.None, $"engine: captured {captured.Length} frames at {rate} Hz, delivering {audio.Length} frames at {RecordSampleRate} Hz");

        return WriteWavPcm16Mono(audio, (int)RecordSampleRate);
    }

    /// <summary>
    /// Linear resample, which is all speech at 48 kHz down to 16 kHz needs; the STT backends resample
    /// again internally.
    /// </summary>
    static float[] Resample(float[] source, double sourceRate, double targetRate)
    {
        if (source == null || source.Length == 0)
        {
            return Array.Empty<float>();
        }

        if (sourceRate <= 0 || targetRate <= 0 || Math.Abs(sourceRate - targetRate) < 0.5)
        {
            return source;
        }

        var ratio = sourceRate / targetRate;

        var count = (int)(source.Length / ratio);

        var result = new float[count];

        for (var i = 0; i < count; i++)
        {
            var position = i * ratio;

            var index = (int)position;

            if (index >= source.Length)
            {
                break;
            }

            var fraction = (float)(position - index);

            var left = source[index];

            var right = index + 1 < source.Length ? source[index + 1] : left;

            result[i] = left + (right - left) * fraction;
        }

        return result;
    }

    /// <summary>
    /// Writes the canonical 44-byte WAV header plus little-endian PCM16 samples, the layout
    /// Whisper.ReadWavPcm16 walks chunk by chunk.
    /// </summary>
    static byte[] WriteWavPcm16Mono(float[] samples, int sampleRate)
    {
        samples ??= Array.Empty<float>();

        var dataBytes = samples.Length * 2;

        var bytes = new byte[44 + dataBytes];

        using var stream = new MemoryStream(bytes);
        using var writer = new BinaryWriter(stream);

        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)RecordChannels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * RecordChannels * RecordBitsPerSample / 8);
        writer.Write((short)(RecordChannels * RecordBitsPerSample / 8));
        writer.Write((short)RecordBitsPerSample);
        writer.Write("data"u8.ToArray());
        writer.Write(dataBytes);

        foreach (var sample in samples)
        {
            writer.Write((short)(Math.Clamp(sample, -1f, 1f) * 32767f));
        }

        writer.Flush();

        return bytes;
    }
#endif

    /// <summary>
    /// Drops a recorder whose StopRecord never ran (the caller was closed or threw in between), so
    /// the field does not stay occupied and silently block every later attempt. A caller still
    /// awaiting that session is completed with null instead of hanging.
    /// </summary>
    void ReleaseStaleRecorder()
    {
#if MACCATALYST
        if (captureEngine != null)
        {
            Log(LogType.None, "releasing the leftover engine capture");

            FinishEngineCapture();
        }
#endif

        var rec = recorder;

        var tcs = tcsRecord;

        var file = recordFile;

        recorder = null;

        tcsRecord = null;

        recordFile = null;

        if (rec == null)
        {
            return;
        }

        Log(LogType.None, "releasing the leftover recorder");

        try { rec.Stop(); }
        catch { }

        try { rec.Dispose(); }
        catch { }

        tcs?.TrySetResult(null);

        DeleteTempFile(file);
    }

    static void DeleteTempFile(string file)
    {
        // The temp .wav is only an intermediate; callers keep the bytes, same as the Windows
        // recorder which deletes its temp file after reading it back.
        try
        {
            if (file != null && System.IO.File.Exists(file))
            {
                System.IO.File.Delete(file);
            }
        }
        catch (Exception ex)
        {
            Log(LogType.None, $"temp cleanup failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Logs the permission, route and mute facts that decide whether PrepareToRecord can ever
    /// succeed. Purely diagnostic, so a failure here must never abort the recording attempt.
    /// </summary>
    static void LogSessionState(AVAudioSession session)
    {
        try
        {
            var inputs = session.CurrentRoute?.Inputs.NullToEmptyArray().Select(input => $"{input.PortType} {input.PortName}").ToArray();

            Log(LogType.None, $"tcc={AVCaptureDevice.GetAuthorizationStatus(AVAuthorizationMediaType.Audio)} {AudioApplicationState()} category={session.Category} inputAvailable={session.InputAvailable} sampleRate={session.SampleRate} inputs=[{string.Join(", ", inputs.NullToEmptyArray())}] temp={Path.GetTempPath()}");
        }
        catch (Exception ex)
        {
            Log(LogType.None, $"session state unavailable: {ex.Message}");
        }
    }

    /// <summary>
    /// AVAudioApplication, which also reports the system-level microphone mute, only exists on
    /// iOS/Mac Catalyst 17+ while the engine still supports 15+, so the query is version gated.
    /// </summary>
    static string AudioApplicationState()
    {
        if (OperatingSystem.IsIOSVersionAtLeast(17) || OperatingSystem.IsMacCatalystVersionAtLeast(17))
        {
            var application = AVAudioApplication.SharedInstance;

            return $"recordPermission={application.RecordPermission} inputMuted={application.InputMuted}";
        }

        return "recordPermission=unavailable";
    }

    static string Describe(NSError error)
    {
        return error == null ? "none" : $"{error.Domain} {error.Code} {error.LocalizedDescription}";
    }

    static void Log(LogType logType, string message)
    {
        DeviceServices.BaseApp?.AddLog(logType, $"{DateTime.UtcNow} [Record] {message}");
    }

    public async Task<byte[]> StopRecord()
    {
#if MACCATALYST
        // The engine path never touches the recorder, the temp file or FinishedRecording: it hands
        // back the WAV bytes it assembled from the tap itself.
        if (captureEngine != null)
        {
            var captured = FinishEngineCapture();

            AppleAudioSession.ReleaseCapture();

            return captured;
        }
#endif

        var rec = recorder;

        var tcs = tcsRecord;

        var file = recordFile;

        recorder = null;

        tcsRecord = null;

        recordFile = null;

        if (rec == null || tcs == null)
        {
            return null;
        }

        try
        {
            rec.Stop();
        }
        catch (Exception ex)
        {
            Log(LogType.Error, $"StopRecord stop failed: {ex.Message}");
        }

        // Stop only queues audioRecorderDidFinishRecording on the main run loop, and disposing the
        // recorder before that callback lands drops it, so wait for the file to be finalized first.
        // The timeout keeps a lost callback from hanging the caller forever.
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(5000)) == tcs.Task;

        if (!completed)
        {
            Log(LogType.Error, "StopRecord timed out waiting for FinishedRecording");
        }

        try
        {
            rec.Dispose();
        }
        catch (Exception ex)
        {
            Log(LogType.Error, $"StopRecord dispose failed: {ex.Message}");
        }

        // Restores a playback category as well as deactivating: the recorder's category
        // (usually Record) has no output route and would silence every later play.
        AppleAudioSession.ReleaseCapture();

        var bytes = completed ? await tcs.Task : null;

        DeleteTempFile(file);

        return bytes;
    }

    public Task<INativeImageDecoder?> CaptureScreen()
    {
        // iOS and MacCatalyst sandbox mode does not allow capturing external screen content.
        return Task.FromResult<INativeImageDecoder?>(null);
    }

    public Task<INativeImageDecoder?> CaptureApp()
    {
        // Inject the TaskCompletionSource, set the NeedCaptureApp flag,
        // then let the render thread detect it in AfterRender, read back from GPU, and call TrySetResult.
        var tcs = new TaskCompletionSource<INativeImageDecoder?>();
        BaseApp.CaptureAppTcs = tcs;
        return tcs.Task;
    }

    /// <summary>
    /// Decodes the audio of any container AVFoundation understands (.m4a/.m4v/.mp4/.mov/.mp3 ...)
    /// into a complete WAV: 16 kHz mono 16-bit PCM behind the canonical 44-byte RIFF header. That is
    /// the very output contract StopRecord has, and the only layout the STT front ends parse.
    /// AVAssetReaderTrackOutput does the decoding, the channel mixing and the resampling in one pass,
    /// and only the audio track is requested, so audio-only files and movies both work.
    /// Blocking, so callers run it on a background thread.
    /// </summary>
    public byte[] DecodeToWavPcm16(string path)
    {
        if (!System.IO.File.Exists(path))
        {
            throw new InvalidDataException($"The audio file does not exist: {path}");
        }

        using var asset = AVAsset.FromUrl(NSUrl.FromFilename(path))
            ?? throw new InvalidDataException($"Failed to open the audio file: {path}");

        var audioTrack = asset.Tracks.FirstOrDefault(track => track.MediaType == AVMediaTypes.Audio.GetConstant()!)
            ?? throw new InvalidDataException($"The file does not contain an audio track: {path}");

        using var reader = new AVAssetReader(asset, out NSError error)
            ?? throw new InvalidDataException(error?.LocalizedDescription ?? "Failed to create AVAssetReader.");

        var audioSettings = new AudioSettings
        {
            Format = AudioToolbox.AudioFormatType.LinearPCM,
            LinearPcmBigEndian = false,
            LinearPcmFloat = false,
            LinearPcmBitDepth = RecordBitsPerSample,
            NumberChannels = RecordChannels,
            SampleRate = RecordSampleRate
        };

        using var audioOutput = new AVAssetReaderTrackOutput(audioTrack, audioSettings);

        reader.AddOutput(audioOutput);

        if (!reader.StartReading())
        {
            throw new InvalidDataException(reader.Error?.LocalizedDescription ?? "Failed to start reading the audio track.");
        }

        using var pcm = new MemoryStream();

        while (reader.Status == AVAssetReaderStatus.Reading)
        {
            using var sample = audioOutput.CopyNextSampleBuffer();

            if (sample == null)
            {
                break;
            }

            using var blockBuffer = sample.GetDataBuffer();

            if (blockBuffer == null)
            {
                continue;
            }

            nuint dataLength = blockBuffer.DataLength;

            if (dataLength == 0)
            {
                continue;
            }

            var chunk = new byte[(int)dataLength];
            var handle = GCHandle.Alloc(chunk, GCHandleType.Pinned);

            try
            {
                nuint offset = 0;
                var status = blockBuffer.CopyDataBytes(offset, dataLength, handle.AddrOfPinnedObject());

                if (status == 0) // kCMBlockBufferNoErr
                {
                    pcm.Write(chunk, 0, chunk.Length);
                }
            }
            finally
            {
                handle.Free();
            }
        }

        if (reader.Status != AVAssetReaderStatus.Completed)
        {
            throw new InvalidDataException(reader.Error?.LocalizedDescription ?? $"Failed to decode the audio track ({reader.Status}).");
        }

        var pcmBytes = pcm.ToArray();

        if (pcmBytes.Length == 0)
        {
            throw new InvalidDataException($"The audio track did not contain any samples: {path}");
        }

        Log(LogType.None, $"decode: '{Path.GetFileName(path)}' -> {pcmBytes.Length / (RecordChannels * RecordBitsPerSample / 8)} frames at {RecordSampleRate} Hz");

        return WriteWavPcm16(pcmBytes, (int)RecordSampleRate);
    }

    /// <summary>
    /// Writes the canonical 44-byte WAV header in front of little-endian PCM16 bytes that already are
    /// in the recorder's output format, so no conversion is left to do.
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
        writer.Write((short)RecordChannels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * RecordChannels * RecordBitsPerSample / 8);
        writer.Write((short)(RecordChannels * RecordBitsPerSample / 8));
        writer.Write((short)RecordBitsPerSample);
        writer.Write("data"u8.ToArray());
        writer.Write(pcm.Length);
        writer.Write(pcm);

        writer.Flush();

        return bytes;
    }
}
