// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Buffers.Binary;
using System.Text;

namespace Season.Platforms.Linux;

/// <summary>
/// Writes pre-encoded MJPEG frames into a self-contained AVI (RIFF) container.
/// <para>
/// The regular Linux mp4 path shells out to an ffmpeg process, but minimal installs and WSL
/// frequently ship the shared libraries without the CLI. Losing an entire GPU-rendered clip
/// to a missing command is far worse than returning an older container, so when ffmpeg is
/// unavailable SaveVideo encodes the frames as JPEG (through GdkPixbuf, the same stack behind
/// SaveImage) and muxes them here. Every JPEG frame is a keyframe, so indexing is trivial and
/// the result demuxes in ffmpeg, VLC, MPV and other MJPEG-AVI capable players.
/// </para>
/// Layout follows FFmpeg's own AVI muxer (libavformat/avienc.c):
/// RIFF('AVI ' LIST('hdrl' avih LIST('strl' strh strf)) LIST('movi' 00dc..) idx1).
/// The idx1 chunk offsets are relative to the first byte after the 'movi' fourcc, which is the
/// exact convention FFmpeg produces (avienc stores pos = tell - movi_list, and ff_start_tag
/// returns the data start right after the fourcc and size fields).
/// </summary>
internal static class MjpegAviWriter
{
    /// <summary>AVIF_HASINDEX | AVIF_ISINTERLEAVED | AVIF_TRUSTCKTYPE; the flag set FFmpeg writes.</summary>
    const uint MainHeaderFlags = 0x00000010 | 0x00000100 | 0x00000800;

    /// <summary>AVIIF_KEYFRAME; every MJPEG frame is a keyframe.</summary>
    const uint IndexKeyFrameFlag = 0x00000010;

    public static byte[] Write(int width, int height, int framesPerSecond, IReadOnlyList<byte[]> jpegFrames)
    {
        ArgumentNullException.ThrowIfNull(jpegFrames);

        if (jpegFrames.Count == 0)
            throw new ArgumentException("At least one frame is required to write an AVI file.", nameof(jpegFrames));
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Frame dimensions must be positive.");
        if (framesPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond));

        long largestFrame = 0;
        long framesTotal = 0;
        foreach (var frame in jpegFrames)
        {
            ArgumentNullException.ThrowIfNull(frame);
            largestFrame = Math.Max(largestFrame, frame.Length);
            framesTotal += frame.Length + (frame.Length & 1);   // RIFF chunks are word aligned
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        // ---- RIFF header ----
        writer.Write(FourCc("RIFF"));
        long riffSizePosition = stream.Position;
        writer.Write(0u);                                       // patched below
        writer.Write(FourCc("AVI "));

        // ---- LIST hdrl ----
        // 'hdrl' + avih chunk (8+56) + LIST strl chunk (8 + 4 + 64 + 48) = 4 + 64 + 124 = 192
        writer.Write(FourCc("LIST"));
        writer.Write(192u);
        writer.Write(FourCc("hdrl"));

        // ---- avih: main AVI header (56 bytes = 14 DWORDs) ----
        writer.Write(FourCc("avih"));
        writer.Write(56u);
        writer.Write(1000000u / (uint)framesPerSecond);         // dwMicroSecPerFrame
        writer.Write(EstimateBytesPerSecond(framesTotal, jpegFrames.Count, framesPerSecond));   // dwMaxBytesPerSec
        writer.Write(0u);                                       // dwPaddingGranularity
        writer.Write(MainHeaderFlags);                          // dwFlags
        writer.Write((uint)jpegFrames.Count);                   // dwTotalFrames
        writer.Write(0u);                                       // dwInitialFrames
        writer.Write(1u);                                       // dwStreams
        writer.Write((uint)largestFrame);                       // dwSuggestedBufferSize
        writer.Write((uint)width);                              // dwWidth
        writer.Write((uint)height);                             // dwHeight
        writer.Write(0u);                                       // dwReserved[0..3]
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);

        // ---- LIST strl ----
        // 'strl' + strh chunk (8+56) + strf chunk (8+40) = 4 + 64 + 48 = 116
        writer.Write(FourCc("LIST"));
        writer.Write(116u);
        writer.Write(FourCc("strl"));

        // ---- strh: stream header (56 bytes) ----
        writer.Write(FourCc("strh"));
        writer.Write(56u);
        writer.Write(FourCc("vids"));                           // fccType
        writer.Write(FourCc("MJPG"));                           // fccHandler
        writer.Write(0u);                                       // dwFlags
        writer.Write((ushort)0);                                // wPriority
        writer.Write((ushort)0);                                // wLanguage
        writer.Write(0u);                                       // dwInitialFrames
        writer.Write(1u);                                       // dwScale
        writer.Write((uint)framesPerSecond);                    // dwRate (fps = dwRate / dwScale)
        writer.Write(0u);                                       // dwStart
        writer.Write((uint)jpegFrames.Count);                   // dwLength
        writer.Write((uint)largestFrame);                       // dwSuggestedBufferSize (FFmpeg fills the largest chunk)
        writer.Write(uint.MaxValue);                            // dwQuality (FFmpeg writes -1)
        writer.Write(0u);                                       // dwSampleSize
        writer.Write((ushort)0);                                // rcFrame.left
        writer.Write((ushort)0);                                // rcFrame.top
        writer.Write((ushort)width);                            // rcFrame.right
        writer.Write((ushort)height);                           // rcFrame.bottom

        // ---- strf: BITMAPINFOHEADER (40 bytes) ----
        writer.Write(FourCc("strf"));
        writer.Write(40u);
        writer.Write(40u);                                      // biSize
        writer.Write(width);                                    // biWidth
        writer.Write(height);                                   // biHeight
        writer.Write((ushort)1);                                // biPlanes
        writer.Write((ushort)24);                               // biBitCount
        writer.Write(FourCc("MJPG"));                           // biCompression
        writer.Write(width * height * 3);                       // biSizeImage
        writer.Write(0);                                        // biXPelsPerMeter
        writer.Write(0);                                        // biYPelsPerMeter
        writer.Write(0u);                                       // biClrUsed
        writer.Write(0u);                                       // biClrImportant

        // ---- LIST movi ----
        writer.Write(FourCc("LIST"));
        long moviSizePosition = stream.Position;
        writer.Write(0u);                                       // patched below
        long moviDataStart = stream.Position;                   // first byte after the 'movi' fourcc
        writer.Write(FourCc("movi"));

        var frameOffsets = new uint[jpegFrames.Count];
        var frameLengths = new int[jpegFrames.Count];

        for (int i = 0; i < jpegFrames.Count; i++)
        {
            var frame = jpegFrames[i];
            frameOffsets[i] = (uint)(stream.Position - moviDataStart);
            frameLengths[i] = frame.Length;

            writer.Write(FourCc("00dc"));
            writer.Write((uint)frame.Length);
            writer.Write(frame);

            if ((frame.Length & 1) != 0)
                writer.Write((byte)0);                          // RIFF chunks are word aligned
        }

        // LIST size covers the 'movi' fourcc plus every chunk (the field itself excluded).
        PatchUInt32(stream, moviSizePosition, (uint)(stream.Position - (moviSizePosition + 4)));

        // ---- idx1: one 16-byte entry per frame ----
        writer.Write(FourCc("idx1"));
        writer.Write((uint)jpegFrames.Count * 16u);

        for (int i = 0; i < jpegFrames.Count; i++)
        {
            writer.Write(FourCc("00dc"));
            writer.Write(IndexKeyFrameFlag);                    // dwFlags
            writer.Write(frameOffsets[i]);                      // dwChunkOffset, relative to the movi data start
            writer.Write((uint)frameLengths[i]);                // dwChunkLength, unpadded
        }

        // ---- patch the top-level RIFF size, then hand out the file ----
        PatchUInt32(stream, riffSizePosition, (uint)(stream.Position - 8));

        writer.Flush();
        return stream.ToArray();
    }

    static uint EstimateBytesPerSecond(long framesTotal, int frameCount, int framesPerSecond)
    {
        long value = framesTotal * framesPerSecond / Math.Max(1, frameCount);
        return (uint)Math.Clamp(value, 0, uint.MaxValue);
    }

    static void PatchUInt32(MemoryStream stream, long position, uint value)
    {
        long current = stream.Position;
        stream.Position = position;
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        stream.Write(buffer);
        stream.Position = current;
    }

    static byte[] FourCc(string value) => Encoding.ASCII.GetBytes(value);
}
