// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Basic;

internal static class VideoEncodingHelper
{
    public const int DefaultFramesPerSecond = 16;

    public static PreparedVideoFrames PrepareFrames(INativeImageDecoder[] images, int quality, int? fps = null)
    {
        ArgumentNullException.ThrowIfNull(images);

        if (images.Length == 0)
            throw new ArgumentException("At least one frame is required for video encoding.", nameof(images));

        int sourceWidth = images[0]?.Width ?? throw new ArgumentException("Frame 0 is null.", nameof(images));
        int sourceHeight = images[0].Height;

        if (sourceWidth <= 0 || sourceHeight <= 0)
            throw new ArgumentException("Frame size must be positive.", nameof(images));

        int width = MakeEven(sourceWidth);
        int height = MakeEven(sourceHeight);
        int frameRate = fps.GetValueOrDefault(DefaultFramesPerSecond);
        if (frameRate <= 0)
            frameRate = DefaultFramesPerSecond;

        var frames = new byte[images.Length][];
        for (int i = 0; i < images.Length; i++)
        {
            var image = images[i] ?? throw new ArgumentException($"Frame {i} is null.", nameof(images));
            if (image.Width != sourceWidth || image.Height != sourceHeight)
            {
                throw new ArgumentException(
                    $"All frames must have the same size. Frame 0={sourceWidth}x{sourceHeight}, frame {i}={image.Width}x{image.Height}.",
                    nameof(images));
            }

            frames[i] = NormalizeRgbaFrame(image, width, height);
        }

        return new PreparedVideoFrames(
            width,
            height,
            sourceWidth,
            sourceHeight,
            frameRate,
            Math.Clamp(quality, 1, 100),
            EstimateBitrate(width, height, frameRate, quality),
            EstimateCrf(quality),
            frames);
    }

    public static int EstimateBitrate(int width, int height, int fps, int quality)
    {
        double qualityFactor = 0.35 + (Math.Clamp(quality, 1, 100) / 100.0) * 1.4;
        double raw = width * height * fps * 0.11 * qualityFactor;
        return (int)Math.Clamp(raw, 300_000d, 20_000_000d);
    }

    public static int EstimateCrf(int quality)
    {
        quality = Math.Clamp(quality, 1, 100);
        return 36 - (int)Math.Round((quality / 100.0) * 18.0);
    }

    public static byte[] ConvertRgbaToBgra(byte[] rgba)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        var bgra = new byte[rgba.Length];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            bgra[i] = rgba[i + 2];
            bgra[i + 1] = rgba[i + 1];
            bgra[i + 2] = rgba[i];
            bgra[i + 3] = rgba[i + 3];
        }

        return bgra;
    }

    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch
        {
        }
    }

    public static void TryDeleteFile(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    static byte[] NormalizeRgbaFrame(INativeImageDecoder image, int width, int height)
    {
        int destinationStride = width * 4;
        var output = new byte[height * destinationStride];
        var source = image.PixelSpan;
        int sourceRowBytes = image.Width * 4;

        for (int y = 0; y < image.Height; y++)
        {
            var sourceRow = source.Slice(y * image.Stride, sourceRowBytes);
            var destinationRow = output.AsSpan(y * destinationStride, sourceRowBytes);
            sourceRow.CopyTo(destinationRow);

            if (width != image.Width)
            {
                var lastPixel = destinationRow.Slice(sourceRowBytes - 4, 4);
                for (int x = image.Width; x < width; x++)
                    lastPixel.CopyTo(output.AsSpan(y * destinationStride + x * 4, 4));
            }
        }

        if (height != image.Height)
        {
            var lastRow = output.AsSpan((image.Height - 1) * destinationStride, destinationStride);
            for (int y = image.Height; y < height; y++)
                lastRow.CopyTo(output.AsSpan(y * destinationStride, destinationStride));
        }

        return output;
    }

    static int MakeEven(int value) => (value & 1) == 0 ? value : value + 1;
}

/// <summary>
/// Helper methods for building <see cref="INativeImageDecoder"/> frames from raw pixel data decoded on each platform.
/// </summary>
internal static class VideoLoadHelper
{
    public const int DefaultSourceFps = 16;

    /// <summary>
    /// Estimate the source video FPS. Platforms may override this default with an exact value discovered during decoding.
    /// </summary>
    public static int EstimateSourceFps(string? filePath = null) => DefaultSourceFps;

    /// <summary>
    /// Create RGBA <see cref="NativeImageData"/> from BGRA pixels, with optional resizing.
    /// Media Foundation / AVFoundation commonly output BGRA.
    /// </summary>
    public static INativeImageDecoder CreateRgbaFrameFromBgra(
        byte[] bgra, int srcWidth, int srcHeight, int srcRowPitch,
        int targetWidth, int targetHeight)
    {
        bool needResize = srcWidth != targetWidth || srcHeight != targetHeight;
        int dstRowPitch = targetWidth * 4;
        var rgba = new byte[targetHeight * dstRowPitch];

        for (int y = 0; y < targetHeight; y++)
        {
            int srcY = needResize ? y * srcHeight / targetHeight : y;
            int srcRowOffset = srcY * srcRowPitch;
            int dstRowOffset = y * dstRowPitch;

            for (int x = 0; x < targetWidth; x++)
            {
                int srcX = needResize ? x * srcWidth / targetWidth : x;
                int srcIdx = srcRowOffset + srcX * 4;
                int dstIdx = dstRowOffset + x * 4;

                rgba[dstIdx]     = bgra[Math.Min(srcIdx + 2, bgra.Length - 1)]; // R ← B
                rgba[dstIdx + 1] = bgra[Math.Min(srcIdx + 1, bgra.Length - 1)]; // G ← G
                rgba[dstIdx + 2] = bgra[Math.Min(srcIdx, bgra.Length - 1)];     // B ← R
                rgba[dstIdx + 3] = bgra[Math.Min(srcIdx + 3, bgra.Length - 1)]; // A ← A
            }
        }

        return new NativeImageData(targetWidth, targetHeight, rgba);
    }

    /// <summary>
    /// Create <see cref="NativeImageData"/> from RGBA pixels, with optional resizing.
    /// ffmpeg / Android decoders commonly output RGB/RGBA.
    /// </summary>
    public static INativeImageDecoder CreateRgbaFrame(
        byte[] rgba, int srcWidth, int srcHeight, int srcRowPitch,
        int targetWidth, int targetHeight)
    {
        bool needResize = srcWidth != targetWidth || srcHeight != targetHeight;
        int dstRowPitch = targetWidth * 4;
        var dst = new byte[targetHeight * dstRowPitch];

        for (int y = 0; y < targetHeight; y++)
        {
            int srcY = needResize ? y * srcHeight / targetHeight : y;
            int srcRowOffset = srcY * srcRowPitch;
            int dstRowOffset = y * dstRowPitch;
            int copyLen = Math.Min(dstRowPitch, Math.Max(0, rgba.Length - srcRowOffset));
            if (copyLen > 0)
                Array.Copy(rgba, srcRowOffset, dst, dstRowOffset, copyLen);
        }

        return new NativeImageData(targetWidth, targetHeight, dst);
    }

    /// <summary>
    /// Convert NV12 (YUV420 semi-planar) to RGBA8.
    /// This is the default output format of the Windows Media Foundation H.264 decoder.
    /// </summary>
    /// <param name="nv12">Raw NV12 bytes.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height.</param>
    /// <param name="yStride">Row stride of the Y plane (>= width; MF typically aligns to 16/32-byte boundaries). If 0 or negative, width is used.</param>
    public static byte[] ConvertNv12ToRgba(byte[] nv12, int width, int height, int yStride = 0)
    {
        if (yStride <= 0) yStride = width;
        int frameSize = yStride * height;  // Y plane may be padded
        var rgba = new byte[width * height * 4];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int yIdx = y * yStride + x;
                int yVal = yIdx < nv12.Length ? nv12[yIdx] & 0xFF : 0;
                int uvIndex = frameSize + (y / 2) * yStride + (x & ~1);
                int uVal = uvIndex < nv12.Length ? nv12[uvIndex] & 0xFF : 128;
                int vVal = uvIndex + 1 < nv12.Length ? nv12[uvIndex + 1] & 0xFF : 128;

                int c = yVal - 16;
                int d = uVal - 128;
                int e = vVal - 128;

                int r = (298 * c + 409 * e + 128) >> 8;
                int g = (298 * c - 100 * d - 208 * e + 128) >> 8;
                int b = (298 * c + 516 * d + 128) >> 8;

                int idx = (y * width + x) * 4;
                rgba[idx]     = (byte)Math.Clamp(r, 0, 255);
                rgba[idx + 1] = (byte)Math.Clamp(g, 0, 255);
                rgba[idx + 2] = (byte)Math.Clamp(b, 0, 255);
                rgba[idx + 3] = 255;
            }
        }

        return rgba;
    }
}

internal sealed class PreparedVideoFrames
{
    public PreparedVideoFrames(
        int width,
        int height,
        int sourceWidth,
        int sourceHeight,
        int framesPerSecond,
        int quality,
        int bitrate,
        int crf,
        IReadOnlyList<byte[]> frames)
    {
        Width = width;
        Height = height;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        FramesPerSecond = framesPerSecond;
        Quality = quality;
        Bitrate = bitrate;
        Crf = crf;
        Frames = frames;
    }

    public int Width { get; }
    public int Height { get; }
    public int SourceWidth { get; }
    public int SourceHeight { get; }
    public int FramesPerSecond { get; }
    public int Quality { get; }
    public int Bitrate { get; }
    public int Crf { get; }
    public IReadOnlyList<byte[]> Frames { get; }
}
