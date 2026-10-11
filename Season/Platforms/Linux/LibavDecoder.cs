// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using LibavStream = Season.Platforms.Linux.LinuxAudioDecoder.LibavApi.PlaybackStream;

namespace Season.Platforms.Linux;

/// <summary>
/// The decoder LinuxAudioPlayer uses for the containers its managed decoders cannot read:
/// AAC/ALAC audio in an ISO base media container (.m4a, plus .mp4/.m4v with the video track
/// demuxed away). Wraps <see cref="LinuxAudioDecoder.LibavApi.PlaybackStream"/> - the same
/// libav binding that backs <see cref="IRecordService.DecodeToWavPcm16"/> - as a playback
/// <see cref="LinuxAudioPlayer.Decoder"/>, so the file plays in-process with gain, pause and
/// resume instead of falling back to the mplayer child process (which most machines no
/// longer have installed).
///
/// Construction throws when the FFmpeg 8 shared libraries are missing or the stream is
/// unreadable; OpenDecoder logs that and the mplayer fallback handles the rest.
/// </summary>
internal sealed class LibavDecoder : LinuxAudioPlayer.Decoder
{
    readonly LibavStream _stream;

    public LibavDecoder(string path) => _stream = LibavStream.Open(path);

    public override int SampleRate => _stream.SampleRate;

    public override int Channels => _stream.Channels;

    public override int Read(float[] buffer, int offset, int count) => _stream.Read(buffer, offset, count);

    public override void Dispose() => _stream.Dispose();
}
