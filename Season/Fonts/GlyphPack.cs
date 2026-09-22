// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Buffers.Binary;

namespace Season.Fonts;

// Offline pre-baked glyph packs (SGPK v1).
//
// The bake tool (Sanguo/Tools/SeasonGlyphBake) links Font.cs and calls the exact same
// CreateMsdfGlyph pipeline, so every entry is byte-identical to what the runtime would
// rasterize. The registry only replaces the *computation*: GlyphAtlasManager keeps packing,
// dirty-rect uploads and stable-page semantics unchanged, and any lookup miss falls back to
// the live rasterizer (player-typed glyphs keep working).

/// <summary>One pre-baked glyph, shaped like the CreateMsdfGlyph return tuple.</summary>
internal readonly struct GlyphPackGlyph
{
    public readonly byte[] ColorBuffer;
    public readonly GlyphMetrics GlyphMetrics;
    public readonly float PixelRange;
    public readonly int TextureWidth;
    public readonly int TextureHeight;

    public GlyphPackGlyph(byte[] colorBuffer, GlyphMetrics glyphMetrics, float pixelRange, int textureWidth, int textureHeight)
    {
        ColorBuffer = colorBuffer;
        GlyphMetrics = glyphMetrics;
        PixelRange = pixelRange;
        TextureWidth = textureWidth;
        TextureHeight = textureHeight;
    }
}

/// <summary>
/// Parsed SGPK v1 pack: 64-byte header + variable preamble (font name and SHA-256) +
/// fixed-size index entries + per-entry pixel blobs. Entry blobs are inflated on demand.
/// Immutable after construction; safe for concurrent readers.
/// </summary>
internal sealed class GlyphPackFile
{
    public const int HeaderSize = 64;
    public const int EntrySize = 108;
    public const int FontShaSize = 32;
    public const int Version = 1;
    public const int FlagDeflate = 1;

    static readonly byte[] Magic = { (byte)'S', (byte)'G', (byte)'P', (byte)'K', (byte)'1', 0, 0, 0 };

    readonly byte[] _data;
    readonly int _indexOffset;

    public readonly string SourcePath;
    public readonly string FontFileName;
    public readonly byte[] FontSha256;
    public readonly int RasterSize;
    public readonly int Flags;
    public readonly float PixelRange;
    public readonly float MinMsdfGlyphScale;
    public readonly float MsdfOversampleFactor;
    public readonly bool NativeBackend;
    public readonly int[] CodePoints;

    public int EntryCount => CodePoints.Length;
    public bool IsDeflated => (Flags & FlagDeflate) != 0;

    GlyphPackFile(byte[] data, string sourcePath, string fontFileName, byte[] fontSha256,
        int rasterSize, int flags, float pixelRange, float minScale, float oversample, bool nativeBackend,
        int[] codePoints, int indexOffset)
    {
        _data = data;
        SourcePath = sourcePath;
        FontFileName = fontFileName;
        FontSha256 = fontSha256;
        RasterSize = rasterSize;
        Flags = flags;
        PixelRange = pixelRange;
        MinMsdfGlyphScale = minScale;
        MsdfOversampleFactor = oversample;
        NativeBackend = nativeBackend;
        CodePoints = codePoints;
        _indexOffset = indexOffset;
    }

    /// <summary>Parses a pack image. Throws <see cref="InvalidDataException"/> with a descriptive message on malformed input.</summary>
    public static GlyphPackFile Parse(byte[] data, string sourcePath)
    {
        if (data.Length < HeaderSize)
            throw new InvalidDataException($"Glyph pack is shorter than its header ({data.Length} bytes): {sourcePath}");
        if (!data.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException($"Glyph pack magic mismatch: {sourcePath}");

        int version = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8));
        if (version != Version)
            throw new InvalidDataException($"Unsupported glyph pack version {version} (expected {Version}): {sourcePath}");

        int flags = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12));
        if ((flags & ~FlagDeflate) != 0)
            throw new InvalidDataException($"Glyph pack uses unknown flags 0x{flags:X}: {sourcePath}");

        int rasterSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(16));
        int entryCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(20));
        int fontNameLength = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(24));
        bool nativeBackend = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(28)) != 0;
        long indexOffset = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(32));
        long blobOffset = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(40));
        float pixelRange = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(48));
        float minScale = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(52));
        float oversample = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(56));

        long preambleEnd = HeaderSize + (long)fontNameLength + FontShaSize;
        if (rasterSize <= 0 || entryCount < 0 || fontNameLength <= 0 ||
            indexOffset != preambleEnd || blobOffset != indexOffset + (long)entryCount * EntrySize || blobOffset > data.Length)
            throw new InvalidDataException($"Glyph pack layout is invalid: {sourcePath}");

        string fontFileName = Encoding.UTF8.GetString(data, HeaderSize, fontNameLength);
        var sha = new byte[FontShaSize];
        Array.Copy(data, HeaderSize + fontNameLength, sha, 0, FontShaSize);

        var codePoints = new int[entryCount];
        for (int i = 0; i < entryCount; i++)
        {
            int at = (int)indexOffset + i * EntrySize;
            int codePoint = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at));
            if (i > 0 && codePoint <= codePoints[i - 1])
                throw new InvalidDataException($"Glyph pack index is not strictly ascending at U+{codePoint:X}: {sourcePath}");
            codePoints[i] = codePoint;
        }

        return new GlyphPackFile(data, sourcePath, fontFileName, sha, rasterSize, flags,
            pixelRange, minScale, oversample, nativeBackend, codePoints, (int)indexOffset);
    }

    /// <summary>Inflates the entry for <paramref name="codePoint"/>. Returns false when absent or corrupt.</summary>
    public bool TryGetGlyph(int codePoint, out GlyphPackGlyph glyph)
    {
        glyph = default;
        int index = Array.BinarySearch(CodePoints, codePoint);
        if (index < 0)
            return false;

        try
        {
            var entry = _data.AsSpan(_indexOffset + index * EntrySize, EntrySize);
            int textureWidth = BinaryPrimitives.ReadInt32LittleEndian(entry[4..]);
            int textureHeight = BinaryPrimitives.ReadInt32LittleEndian(entry[8..]);
            long offset = BinaryPrimitives.ReadInt64LittleEndian(entry[12..]);
            int storedSize = BinaryPrimitives.ReadInt32LittleEndian(entry[20..]);
            int rawSize = BinaryPrimitives.ReadInt32LittleEndian(entry[24..]);
            float pixelRange = BinaryPrimitives.ReadSingleLittleEndian(entry[28..]);
            if (offset < 0 || storedSize < 0 || rawSize <= 0 || offset + storedSize > _data.Length)
                return false;

            var pixels = new byte[rawSize];
            if (storedSize == rawSize)
            {
                Array.Copy(_data, offset, pixels, 0, rawSize);
            }
            else
            {
                using var source = new MemoryStream(_data, (int)offset, storedSize, writable: false);
                using var deflate = new DeflateStream(source, CompressionMode.Decompress);
                deflate.ReadExactly(pixels);
            }

            var metrics = new GlyphMetrics
            {
                Width = BinaryPrimitives.ReadInt32LittleEndian(entry[68..]),
                Height = BinaryPrimitives.ReadInt32LittleEndian(entry[72..]),
                X0 = BinaryPrimitives.ReadInt32LittleEndian(entry[76..]),
                Y0 = BinaryPrimitives.ReadInt32LittleEndian(entry[80..]),
                X1 = BinaryPrimitives.ReadInt32LittleEndian(entry[84..]),
                Y1 = BinaryPrimitives.ReadInt32LittleEndian(entry[88..]),
                AdvanceWidth = BinaryPrimitives.ReadSingleLittleEndian(entry[32..]),
                HasPlaneBounds = BinaryPrimitives.ReadInt32LittleEndian(entry[92..]) != 0,
                PlaneLeft = BinaryPrimitives.ReadSingleLittleEndian(entry[36..]),
                PlaneBottom = BinaryPrimitives.ReadSingleLittleEndian(entry[40..]),
                PlaneRight = BinaryPrimitives.ReadSingleLittleEndian(entry[44..]),
                PlaneTop = BinaryPrimitives.ReadSingleLittleEndian(entry[48..]),
                HasAtlasBounds = BinaryPrimitives.ReadInt32LittleEndian(entry[96..]) != 0,
                AtlasSourceX = BinaryPrimitives.ReadSingleLittleEndian(entry[52..]),
                AtlasSourceY = BinaryPrimitives.ReadSingleLittleEndian(entry[56..]),
                AtlasSourceWidth = BinaryPrimitives.ReadSingleLittleEndian(entry[60..]),
                AtlasSourceHeight = BinaryPrimitives.ReadSingleLittleEndian(entry[64..])
            };

            glyph = new GlyphPackGlyph(pixels, metrics, pixelRange, textureWidth, textureHeight);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>
/// Process-wide registry of loaded glyph packs, keyed by normalized font file name.
/// Registration is a best-effort side channel: a missing or rejected pack leaves the
/// runtime exactly as it was before pre-baking existed.
/// </summary>
public static class GlyphPackRegistry
{
    sealed class Slot
    {
        public GlyphPackFile? Pack;
    }

    static readonly object _sync = new();
    static readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    static readonly HashSet<string> _attemptedPaths = new(StringComparer.Ordinal);
    static long _hits;
    static long _misses;

    /// <summary>Successful pre-baked lookups since process start (diagnostics only).</summary>
    public static long HitCount => Interlocked.Read(ref _hits);

    /// <summary>Lookups that fell back to the live rasterizer since process start (diagnostics only).</summary>
    public static long MissCount => Interlocked.Read(ref _misses);

    /// <summary>Number of fonts with a usable pack registered.</summary>
    public static int RegisteredPackCount
    {
        get
        {
            lock (_sync)
            {
                int count = 0;
                foreach (var slot in _slots.Values)
                    if (slot.Pack != null) count++;
                return count;
            }
        }
    }

    /// <summary>
    /// Case-normalized file-name identity shared by the atlas key, the pack registry and
    /// the bake tool. Two Font instances of the same file map to the same key.
    /// </summary>
    public static string NormalizeFileName(string fileName)
        => string.IsNullOrEmpty(fileName) ? string.Empty : Path.GetFileName(fileName).ToUpperInvariant();

    /// <summary>Loads a pack file through <see cref="StorageService"/>. Missing files are ignored silently.</summary>
    public static void Register(string packPath)
        => Register(packPath, null);

    /// <summary>Registers a pack from caller-provided bytes (async prefetch or test hosts).</summary>
    public static void Register(string packPath, byte[]? packBytes)
    {
        if (string.IsNullOrWhiteSpace(packPath))
            return;

        lock (_sync)
        {
            if (!_attemptedPaths.Add(packPath))
                return;
        }

        byte[] data;
        try
        {
            data = packBytes ?? StorageService.LoadBytes(packPath);
        }
        catch (Exception)
        {
            // No pack shipped for this font (or this host cannot read it synchronously):
            // clear the attempt marker so a later async registration can still succeed.
            // The dynamic rasterizer keeps serving every glyph meanwhile.
            lock (_sync)
            {
                _attemptedPaths.Remove(packPath);
            }
            return;
        }

        try
        {
            var pack = GlyphPackFile.Parse(data, packPath);
            if (pack.PixelRange != Font.PixelRange || pack.MinMsdfGlyphScale != Font.PipelineFingerprint.MinMsdfGlyphScale ||
                pack.MsdfOversampleFactor != Font.PipelineFingerprint.MsdfOversampleFactor || pack.NativeBackend)
            {
                DeviceServices.BaseApp?.AddLog(LogType.Error,
                    $"Glyph pack {packPath} was baked with a different rasterizer pipeline and was rejected.");
                return;
            }

            var fontKey = NormalizeFileName(pack.FontFileName);
            lock (_sync)
            {
                _slots[fontKey] = new Slot { Pack = pack };
            }
            DeviceServices.BaseApp?.AddLog(LogType.Texts,
                $"Glyph pack registered: font={pack.FontFileName}, raster={pack.RasterSize}, glyphs={pack.EntryCount}, deflate={pack.IsDeflated}, source={packPath}");
        }
        catch (Exception error)
        {
            DeviceServices.BaseApp?.AddLog(LogType.Error, $"Glyph pack {packPath} failed to load: {error.Message}");
        }
    }

    /// <summary>Async variant for hosts whose storage only supports async reads (Web).</summary>
    public static async Task RegisterAsync(string packPath)
    {
        if (string.IsNullOrWhiteSpace(packPath))
            return;
        lock (_sync)
        {
            // The synchronous path already claimed this path (registered or definitively failed).
            if (_attemptedPaths.Contains(packPath))
                return;
        }
        byte[]? data = null;
        try
        {
            data = await StorageService.LoadBytesAsync(packPath);
        }
        catch (Exception)
        {
            return;
        }
        if (data is { Length: > 0 })
            Register(packPath, data);
    }

    /// <summary>True when any pack is registered for this font file (any raster size).</summary>
    public static bool HasPack(string fontFileName)
    {
        var key = NormalizeFileName(fontFileName);
        if (key.Length == 0)
            return false;
        lock (_sync)
        {
            return _slots.TryGetValue(key, out var slot) && slot.Pack != null;
        }
    }

    /// <summary>Code points available for pre-warming, sorted ascending. The array is shared and must not be mutated.</summary>
    public static bool TryGetCodePoints(string fontFileName, int rasterSize, out int[] codePoints)
    {
        codePoints = Array.Empty<int>();
        var key = NormalizeFileName(fontFileName);
        if (key.Length == 0)
            return false;
        lock (_sync)
        {
            if (!_slots.TryGetValue(key, out var slot) || slot.Pack is not { } pack || pack.RasterSize != rasterSize)
                return false;
            codePoints = pack.CodePoints;
            return codePoints.Length > 0;
        }
    }

    /// <summary>Single-point hook used by <see cref="Font.CreateMsdfGlyph"/>; a miss keeps the legacy pipeline.</summary>
    internal static bool TryFetch(string fontFileKey, int rasterSize, int codePoint, out GlyphPackGlyph glyph)
    {
        glyph = default;
        if (fontFileKey.Length == 0)
            return false;

        GlyphPackFile? pack;
        lock (_sync)
        {
            _slots.TryGetValue(fontFileKey, out var slot);
            pack = slot?.Pack;
        }

        if (pack is null || pack.RasterSize != rasterSize || !pack.TryGetGlyph(codePoint, out glyph))
        {
            Interlocked.Increment(ref _misses);
            return false;
        }

        Interlocked.Increment(ref _hits);
        return true;
    }

    /// <summary>Test/diagnostics reset. Loaded packs are dropped and may be registered again.</summary>
    public static void Clear()
    {
        lock (_sync)
        {
            _slots.Clear();
            _attemptedPaths.Clear();
        }
    }
}

/// <summary>
/// Shared batch pre-warmer: walks a font's pack code points in order and pushes them through
/// the platform glyph atlas on the frame thread, so the first page that displays them never
/// pays pack inflation inside Prepare.
/// </summary>
internal static class GlyphPrewarm
{
    public static int Run<TTexture>(GlyphAtlasManager<TTexture> atlas, int rasterSize, Font font, int maxCount,
        Dictionary<string, int> cursors)
    {
        if (maxCount <= 0)
            return 0;
        if (!GlyphPackRegistry.TryGetCodePoints(font.FileKey, rasterSize, out var codePoints))
            return 0;

        cursors.TryGetValue(font.FileKey, out int cursor);
        int done = 0;
        while (done < maxCount && cursor < codePoints.Length)
        {
            int codePoint = codePoints[cursor++];
            try
            {
                if (atlas.TryEnsureStableGlyph(font, rasterSize, codePoint, out _, out _))
                    done++;
            }
            catch (InvalidOperationException)
            {
                // Stable-page budget exhausted: stop pre-warming, remaining glyphs use the lazy path.
                cursor = codePoints.Length;
                break;
            }
        }
        cursors[font.FileKey] = cursor;
        return done;
    }
}
