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
}
