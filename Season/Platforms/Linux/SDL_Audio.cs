// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Season.Platforms.Linux;

/// <summary>
/// SDL3 audio-output bindings (part of the partial <see cref="SDL"/> class).
///
/// Playback uses the SDL3 "device stream" push model: <see cref="OpenAudioDeviceStream"/>
/// opens a logical playback device with an application-chosen input format, and
/// <see cref="PutAudioStreamData"/> pushes PCM into it while SDL converts and resamples
/// to the physical device format automatically.
///
/// This is the native half of <c>LinuxAudioPlayer</c>, which replaced the legacy mplayer
/// child-process backend on Linux: no external player installation, real volume/pause
/// control, and one in-process mixer instead of one process per sound effect.
/// Audio is an independent subsystem here — the host initializes SDL with video only,
/// so <see cref="InitSubSystem"/> adds audio on first playback.
/// </summary>
internal static partial class SDL
{
    /// <summary>SDL_INIT_AUDIO.</summary>
    public const uint InitAudio = 0x00000010;

    /// <summary>SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK: let SDL pick the default playback device.</summary>
    public const uint AudioDeviceDefaultPlayback = 0xFFFFFFFF;

    /// <summary>SDL_AUDIO_DEVICE_DEFAULT_RECORDING: let SDL pick the default recording device.</summary>
    public const uint AudioDeviceDefaultRecording = 0xFFFFFFFE;

    // SDL_AudioFormat (SDL3/SDL_audio.h): bit 15 = signed, bit 12 = float,
    // bit 11 = big-endian, low byte = bit size.
    public const uint AudioFormatU8 = 0x0008;
    public const uint AudioFormatS16LE = 0x8010;
    public const uint AudioFormatS24LE = 0x8018;
    public const uint AudioFormatS32LE = 0x8020;
    public const uint AudioFormatF32LE = 0x8034;

    /// <summary>
    /// SDL3 trimmed SDL_AudioSpec down to these three fields
    /// (SDL2's `samples` field no longer exists).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AudioSpec
    {
        public uint Format;
        public int Channels;
        public int Freq;
    }

    [LibraryImport(Library, EntryPoint = "SDL_InitSubSystem"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool InitSubSystem(uint flags);

    /// <summary>
    /// Opens a logical playback device and returns its audio stream for manual PCM pushes.
    /// The stream starts paused; call <see cref="ResumeAudioStreamDevice"/> once gain is set.
    /// Returns <see cref="IntPtr.Zero"/> when no audio backend/device is available.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "SDL_OpenAudioDeviceStream"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial IntPtr OpenAudioDeviceStream(uint devid, ref AudioSpec spec, IntPtr callback, IntPtr userdata);

    [LibraryImport(Library, EntryPoint = "SDL_ResumeAudioStreamDevice"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool ResumeAudioStreamDevice(IntPtr stream);

    [LibraryImport(Library, EntryPoint = "SDL_PauseAudioStreamDevice"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool PauseAudioStreamDevice(IntPtr stream);

    /// <summary>
    /// Pushes PCM into the stream without blocking; pacing is the caller's responsibility
    /// (poll <see cref="GetAudioStreamQueued"/> and back off while the queue is deep).
    /// </summary>
    [LibraryImport(Library, EntryPoint = "SDL_PutAudioStreamData"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool PutAudioStreamData(IntPtr stream, ReadOnlySpan<byte> buf, int len);

    /// <summary>Bytes still queued: input buffer plus converted-but-unplayed device frames.</summary>
    [LibraryImport(Library, EntryPoint = "SDL_GetAudioStreamQueued"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int GetAudioStreamQueued(IntPtr stream);

    /// <summary>Per-stream software gain; this is how the engine implements volume (0..n, 1 = unity).</summary>
    [LibraryImport(Library, EntryPoint = "SDL_SetAudioStreamGain"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool SetAudioStreamGain(IntPtr stream, float gain);

    [LibraryImport(Library, EntryPoint = "SDL_DestroyAudioStream"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void DestroyAudioStream(IntPtr stream);

    // === Microphone (recording) APIs, used by LinuxRecorder ===

    /// <summary>
    /// Returns a malloc'd array of recording device ids (free with <see cref="Free"/>
    /// and read with <see cref="GetAudioDeviceName"/>); <see cref="IntPtr.Zero"/> on
    /// failure. The count is 0 when the audio stack simply has no input.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "SDL_GetAudioRecordingDevices"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial IntPtr GetAudioRecordingDevices(out int count);

    /// <summary>Human-readable device name for logs; UTF-8 pointer, may be null.</summary>
    [LibraryImport(Library, EntryPoint = "SDL_GetAudioDeviceName"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial IntPtr GetAudioDeviceName(uint devid);

    /// <summary>
    /// Bytes already captured and converted to the stream's input format. Recording
    /// streams are pull-model: SDL pushes nothing, the application polls this and then
    /// reads what is ready.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "SDL_GetAudioStreamAvailable"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int GetAudioStreamAvailable(IntPtr stream);

    /// <summary>
    /// Pulls up to <paramref name="len"/> bytes into <paramref name="buf"/>; blocks when
    /// fewer bytes are available, so callers pass at most <see cref="GetAudioStreamAvailable"/>
    /// to keep the read non-blocking. Returns the byte count, negative on error.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "SDL_GetAudioStreamData"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int GetAudioStreamData(IntPtr stream, Span<byte> buf, int len);

    /// <summary>SDL's allocator free, for the device array returned by <see cref="GetAudioRecordingDevices"/>.</summary>
    [LibraryImport(Library, EntryPoint = "SDL_free"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void Free(IntPtr mem);
}
