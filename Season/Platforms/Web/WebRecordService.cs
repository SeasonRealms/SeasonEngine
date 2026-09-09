// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace Season.Platforms.Web;

/// <summary>
/// Direct [JSImport] bindings to seasonAudioRecorder.js.
/// The JS side returns Promises; [JSImport] maps them to Task directly
/// (same pattern as WebGPUInterop.RequestFrame). Task&lt;byte[]&gt; returns are not
/// supported by the JSImport source generator, so the PCM payload is staged in
/// JS by Stop() (which reports its byte length) and pulled back through a
/// caller-allocated MemoryView with CopyPcm.
/// </summary>
[SupportedOSPlatform("browser")]
internal static partial class WebAudioInterop
{
    [JSImport("globalThis.seasonAudioRecorder.start")]
    internal static partial Task<bool> Start();

    /// <summary>Stops capture and returns the pending PCM byte length.</summary>
    [JSImport("globalThis.seasonAudioRecorder.stop")]
    internal static partial Task<int> Stop();

    /// <summary>Copies the staged PCM into the provided buffer and returns the byte count.
    /// Synchronous on both sides: the JS function copies straight into the MemoryView.</summary>
    [JSImport("globalThis.seasonAudioRecorder.copyPcm")]
    internal static partial int CopyPcm([JSMarshalAs<JSType.MemoryView>] Span<byte> buffer);

    /// <summary>0 = granted, 1 = prompt, 2 = denied, 3 = unsupported.</summary>
    [JSImport("globalThis.seasonAudioRecorder.queryPermission")]
    internal static partial Task<int> QueryPermission();
}

/// <summary>
/// Web-platform microphone recording service.
/// Capture runs in seasonAudioRecorder.js through an AudioWorklet that forwards
/// 16 kHz mono Float32 blocks; stop() concatenates them into Int16 PCM. The C#
/// side only prepends the RIFF header, so the byte[] returned from StopRecord is
/// a full WAV identical in layout to the Android/Windows output
/// (16 kHz / mono / 16-bit PCM), and STT consumers see no difference.
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed class WebRecordService : RecordService, IRecordService
{
    bool _recording;

    public async Task<bool> StartRecord()
    {
        if (_recording) return false;

        var permissions = new string[]
        {
            "RECORD_AUDIO"
        };

        var hasPermission = await DeviceServices.Core.RequestPermissionAsync(permissions);

        if (!hasPermission) return false;

        var started = await WebAudioInterop.Start();

        _recording = started;

        return started;
    }

    public async Task<byte[]> StopRecord()
    {
        if (!_recording) return null;

        _recording = false;

        var pcmLength = await WebAudioInterop.Stop();

        if (pcmLength <= 0) return null;

        var pcm = new byte[pcmLength];

        var copied = WebAudioInterop.CopyPcm(pcm);

        if (copied <= 0) return null;

        return WriteWav(copied < pcm.Length ? pcm.AsSpan(0, copied).ToArray() : pcm);
    }

    /// <summary>Wraps raw 16 kHz mono PCM16 samples into a 44-byte RIFF/WAV container.</summary>
    static byte[] WriteWav(byte[] pcm)
    {
        var wav = new byte[44 + pcm.Length];

        using (var writer = new BinaryWriter(new MemoryStream(wav), Encoding.UTF8))
        {
            const int sampleRate = 16000;
            const short channels = 1;
            const short bitsPerSample = 16;
            var blockAlign = (short)(channels * bitsPerSample / 8);
            var byteRate = sampleRate * blockAlign;

            writer.Write(Encoding.UTF8.GetBytes("RIFF"));
            writer.Write(36 + pcm.Length);          // file size - 8
            writer.Write(Encoding.UTF8.GetBytes("WAVE"));
            writer.Write(Encoding.UTF8.GetBytes("fmt "));
            writer.Write(16);                       // fmt chunk size
            writer.Write((short)1);                 // PCM
            writer.Write(channels);
            writer.Write(sampleRate);
            writer.Write(byteRate);
            writer.Write(blockAlign);
            writer.Write(bitsPerSample);
            writer.Write(Encoding.UTF8.GetBytes("data"));
            writer.Write(pcm.Length);
            writer.Write(pcm);
        }

        return wav;
    }

    public Task<INativeImageDecoder?> CaptureScreen()
    {
        // The browser sandbox cannot capture the external screen.
        return Task.FromResult<INativeImageDecoder?>(null);
    }

    public Task<INativeImageDecoder?> CaptureApp()
    {
        // Inject the TaskCompletionSource, set the NeedCaptureApp flag,
        // and let the render thread detect it during AfterRender, read back from the GPU,
        // and complete it through TrySetResult.
        var tcs = new TaskCompletionSource<INativeImageDecoder?>();
        BaseApp.CaptureAppTcs = tcs;
        return tcs.Task;
    }
}
