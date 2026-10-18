// SeasonGlyphBake: SGPK v1 writer. The layout below is the exact inverse of the runtime
// parser in Season/Fonts/GlyphPack.cs (linked into this project verbatim), so any drift
// is caught immediately by the verify stage.

using Season.Fonts;

namespace SanguoSeason.Tools.SeasonGlyphBake;

/// <summary>One successfully rasterized glyph held in memory until the pack is assembled.</summary>
readonly struct BakedGlyph
{
    public readonly int CodePoint;
    public readonly byte[] Pixels;
    public readonly GlyphMetrics Metrics;
    public readonly float PixelRange;
    public readonly int TextureWidth;
    public readonly int TextureHeight;

    public BakedGlyph(int codePoint, byte[] pixels, GlyphMetrics metrics, float pixelRange, int textureWidth, int textureHeight)
    {
        CodePoint = codePoint;
        Pixels = pixels;
        Metrics = metrics;
        PixelRange = pixelRange;
        TextureWidth = textureWidth;
        TextureHeight = textureHeight;
    }
}

static class PackWriter
{
    static readonly byte[] Magic = { (byte)'S', (byte)'G', (byte)'P', (byte)'K', (byte)'1', 0, 0, 0 };

    /// <summary>
    /// Assembles a pack image: 64-byte header + preamble (font name + SHA-256) +
    /// fixed-size index entries + per-entry pixel blobs (Deflate when smaller than raw).
    /// </summary>
    public static byte[] Write(IReadOnlyList<BakedGlyph> glyphs, string fontFileName, byte[] fontSha256,
        int rasterSize, bool deflate, float pixelRange, float minScale, float oversample, bool nativeBackend)
    {
        var nameBytes = Encoding.UTF8.GetBytes(fontFileName);
        int entryCount = glyphs.Count;
        long indexOffset = GlyphPackFile.HeaderSize + nameBytes.Length + GlyphPackFile.FontShaSize;
        long blobOffset = indexOffset + (long)entryCount * GlyphPackFile.EntrySize;

        // Compress first so the total image size and every blob offset are known up front.
        var payloads = new byte[entryCount][];
        var offsets = new long[entryCount];
        long cursor = blobOffset;
        for (int i = 0; i < entryCount; i++)
        {
            var raw = glyphs[i].Pixels;
            byte[] stored = raw;
            if (deflate)
            {
                var compressed = Deflate(raw);
                if (compressed.Length < raw.Length)
                    stored = compressed;
            }
            payloads[i] = stored;
            offsets[i] = cursor;
            cursor += stored.Length;
        }

        using var stream = new MemoryStream(checked((int)cursor));
        using var writer = new BinaryWriter(stream);

        // ── Header (64 bytes) ──
        writer.Write(Magic);
        writer.Write(GlyphPackFile.Version);
        writer.Write(deflate ? GlyphPackFile.FlagDeflate : 0);
        writer.Write(rasterSize);
        writer.Write(entryCount);
        writer.Write(nameBytes.Length);
        writer.Write(nativeBackend ? 1 : 0);
        writer.Write(indexOffset);
        writer.Write(blobOffset);
        writer.Write(pixelRange);
        writer.Write(minScale);
        writer.Write(oversample);
        writer.Write(0); // reserved
        if (stream.Position != GlyphPackFile.HeaderSize)
            throw new InvalidOperationException($"Header layout drifted: wrote {stream.Position} bytes, expected {GlyphPackFile.HeaderSize}.");

        // ── Preamble ──
        writer.Write(nameBytes);
        writer.Write(fontSha256);

        // ── Index entries (fixed size, code-point ascending) ──
        for (int i = 0; i < entryCount; i++)
        {
            var glyph = glyphs[i];
            var metrics = glyph.Metrics;
            long entryStart = stream.Position;
            writer.Write(glyph.CodePoint);
            writer.Write(glyph.TextureWidth);
            writer.Write(glyph.TextureHeight);
            writer.Write(offsets[i]);
            writer.Write(payloads[i].Length);
            writer.Write(glyph.Pixels.Length);
            writer.Write(glyph.PixelRange);
            writer.Write(metrics.AdvanceWidth);
            writer.Write(metrics.PlaneLeft);
            writer.Write(metrics.PlaneBottom);
            writer.Write(metrics.PlaneRight);
            writer.Write(metrics.PlaneTop);
            writer.Write(metrics.AtlasSourceX);
            writer.Write(metrics.AtlasSourceY);
            writer.Write(metrics.AtlasSourceWidth);
            writer.Write(metrics.AtlasSourceHeight);
            writer.Write(metrics.Width);
            writer.Write(metrics.Height);
            writer.Write(metrics.X0);
            writer.Write(metrics.Y0);
            writer.Write(metrics.X1);
            writer.Write(metrics.Y1);
            writer.Write(metrics.HasPlaneBounds ? 1 : 0);
            writer.Write(metrics.HasAtlasBounds ? 1 : 0);
            writer.Write(0); // reserved
            writer.Write(0); // reserved
            if (stream.Position - entryStart != GlyphPackFile.EntrySize)
                throw new InvalidOperationException($"Entry layout drifted at index {i}: wrote {stream.Position - entryStart} bytes, expected {GlyphPackFile.EntrySize}.");
        }

        // ── Pixel blobs ──
        for (int i = 0; i < entryCount; i++)
        {
            if (stream.Position != offsets[i])
                throw new InvalidOperationException($"Blob offset drift at index {i}: at {stream.Position}, expected {offsets[i]}.");
            writer.Write(payloads[i]);
        }

        return stream.ToArray();
    }

    static byte[] Deflate(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            deflate.Write(raw, 0, raw.Length);
        return output.ToArray();
    }
}
