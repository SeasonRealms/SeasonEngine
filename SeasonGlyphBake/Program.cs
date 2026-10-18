// SeasonGlyphBake: CLI entry point. Rasterizes every corpus code point supported by one
// font file through the engine's live MSDF pipeline (linked Font.cs) and writes a SGPK v1
// pack next to the font, byte-identical to what the runtime would compute on demand.
//
//   SeasonGlyphBake --font <ttf> --out <pack> --roots <dir;dir> [--map <ChineseCodes.txt>]
//                   [--raster 48] [--compress deflate|none] [--threads N]
//                   [--verify N] [--extra "chars"]

using System.Diagnostics;
using System.Security.Cryptography;
using Season.Fonts;

namespace SanguoSeason.Tools.SeasonGlyphBake;

static class Program
{
    static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"FATAL: {error}");
            return 1;
        }
    }

    static int Run(string[] args)
    {
        var options = ParseArgs(args);
        string? fontPath = Get(options, "--font");
        string? outPath = Get(options, "--out");
        if (string.IsNullOrEmpty(fontPath) || string.IsNullOrEmpty(outPath))
        {
            PrintUsage();
            return 2;
        }

        int raster = int.TryParse(Get(options, "--raster"), out var r) ? r : 48;
        bool deflate = !string.Equals(Get(options, "--compress") ?? "deflate", "none", StringComparison.OrdinalIgnoreCase);
        int verifySamples = int.TryParse(Get(options, "--verify"), out var v) ? v : 0;
        int threads = int.TryParse(Get(options, "--threads"), out var t) ? t : Environment.ProcessorCount;
        string? mapPath = Get(options, "--map");
        string? extra = Get(options, "--extra");
        var roots = (Get(options, "--roots") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // GB18030 fallback decoding needs the CodePages provider on .NET.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var stopwatch = Stopwatch.StartNew();
        var fontBytes = File.ReadAllBytes(fontPath);
        var fontSha256 = SHA256.HashData(fontBytes);
        string fontFileName = Path.GetFileName(fontPath);

        Console.WriteLine("── SeasonGlyphBake ──────────────────────────────────────");
        Console.WriteLine($"font        : {fontPath}");
        Console.WriteLine($"sha256      : {Convert.ToHexStringLower(fontSha256)}");
        Console.WriteLine($"raster      : {raster} (pixelRange={Font.PixelRange}, minScale={Font.PipelineFingerprint.MinMsdfGlyphScale}, oversample={Font.PipelineFingerprint.MsdfOversampleFactor})");
        Console.WriteLine();

        // ── Character set ──
        var corpus = Corpus.Collect(roots, mapPath, extra);
        Console.WriteLine($"corpus      : {corpus.Count} unique code points");

        // ── Per-font support filter ──
        var probe = new Font(fontFileName, fontBytes, raster);
        var bakeList = corpus.Where(cp => cp > 0 && probe.Typeface.GetGlyphIndex(cp) != 0).ToArray();
        int unsupported = corpus.Count - bakeList.Length;
        Console.WriteLine($"supported   : {bakeList.Length} glyphs ({unsupported} unsupported code points dropped)");
        Console.WriteLine();

        // ── Parallel rasterization (one Font per worker: independent shape caches) ──
        var baked = new BakedGlyph?[bakeList.Length];
        var skipped = new List<int>();
        var failed = new List<(int CodePoint, string Message)>();
        long doneCount = 0;
        int progressStep = Math.Max(1, bakeList.Length / 20);

        Parallel.For(0, bakeList.Length,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads) },
            () => new Font(fontFileName, fontBytes, raster),
            (i, _, threadFont) =>
            {
                int codePoint = bakeList[i];
                try
                {
                    var glyph = threadFont.CreateMsdfGlyph(raster, codePoint);
                    if (glyph.colorBuffer is null || glyph.textureWidth <= 0 || glyph.textureHeight <= 0)
                    {
                        lock (skipped) skipped.Add(codePoint);
                    }
                    else
                    {
                        baked[i] = new BakedGlyph(codePoint, glyph.colorBuffer, glyph.glyphMetrics, glyph.pixelRange,
                            glyph.textureWidth, glyph.textureHeight);
                    }
                }
                catch (Exception error)
                {
                    lock (failed) failed.Add((codePoint, error.Message));
                }

                long done = Interlocked.Increment(ref doneCount);
                if (done % progressStep == 0 || done == bakeList.Length)
                    Console.WriteLine($"  rasterize {done}/{bakeList.Length}");
                return threadFont;
            },
            _ => { });

        var glyphs = new List<BakedGlyph>(bakeList.Length);
        foreach (var glyph in baked)
        {
            if (glyph.HasValue)
                glyphs.Add(glyph.Value);
        }
        Console.WriteLine();

        // ── Assemble the pack ──
        var fingerprint = Font.PipelineFingerprint;
        var packBytes = PackWriter.Write(glyphs, fontFileName, fontSha256, raster, deflate,
            fingerprint.PixelRange, fingerprint.MinMsdfGlyphScale, fingerprint.MsdfOversampleFactor, fingerprint.NativeBackend);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllBytes(outPath, packBytes);

        // ── Verify: runtime parser reload, full inflate comparison ──
        var parsed = GlyphPackFile.Parse(packBytes, outPath);
        foreach (var glyph in glyphs)
        {
            if (!parsed.TryGetGlyph(glyph.CodePoint, out var loaded))
                throw new InvalidDataException($"verify: U+{glyph.CodePoint:X4} missing after reload");
            if (loaded.TextureWidth != glyph.TextureWidth || loaded.TextureHeight != glyph.TextureHeight ||
                loaded.PixelRange != glyph.PixelRange || !MetricsEqual(loaded.GlyphMetrics, glyph.Metrics) ||
                !loaded.ColorBuffer.AsSpan().SequenceEqual(glyph.Pixels))
                throw new InvalidDataException($"verify: U+{glyph.CodePoint:X4} differs from the written pack");
        }

        // ── Verify: fresh-instance re-rasterization samples ──
        int sampleCount = 0;
        if (verifySamples > 0 && glyphs.Count > 0)
        {
            var reraster = new Font(fontFileName, fontBytes, raster);
            var random = new Random(0x5EED);
            sampleCount = Math.Min(verifySamples, glyphs.Count);
            for (int i = 0; i < sampleCount; i++)
            {
                var glyph = glyphs[random.Next(glyphs.Count)];
                var fresh = reraster.CreateMsdfGlyph(raster, glyph.CodePoint);
                if (fresh.colorBuffer is null || fresh.textureWidth != glyph.TextureWidth ||
                    fresh.textureHeight != glyph.TextureHeight ||
                    !fresh.colorBuffer.AsSpan().SequenceEqual(glyph.Pixels))
                    throw new InvalidDataException($"verify: re-raster of U+{glyph.CodePoint:X4} is not byte-identical");
            }
        }

        // ── Verify: runtime registry acceptance (fingerprint + lookup path) ──
        GlyphPackRegistry.Register(outPath);
        if (GlyphPackRegistry.RegisteredPackCount != 1)
            throw new InvalidDataException("verify: the runtime registry rejected the pack (pipeline fingerprint mismatch?)");
        // The runtime always queries with the case-normalized key (Font.FileKey); mirror that here.
        string fontFileKey = GlyphPackRegistry.NormalizeFileName(fontFileName);
        if (!GlyphPackRegistry.TryFetch(fontFileKey, raster, glyphs[0].CodePoint, out var fetched) ||
            fetched.ColorBuffer.Length != glyphs[0].Pixels.Length)
            throw new InvalidDataException("verify: the runtime fetch path failed");

        // ── Report ──
        long rawTotal = 0;
        int minW = int.MaxValue, maxW = 0, minH = int.MaxValue, maxH = 0;
        long areaSum = 0;
        foreach (var glyph in glyphs)
        {
            rawTotal += glyph.Pixels.Length;
            minW = Math.Min(minW, glyph.TextureWidth);
            maxW = Math.Max(maxW, glyph.TextureWidth);
            minH = Math.Min(minH, glyph.TextureHeight);
            maxH = Math.Max(maxH, glyph.TextureHeight);
            areaSum += (long)glyph.TextureWidth * glyph.TextureHeight;
        }
        double averageSide = glyphs.Count > 0 ? Math.Sqrt((double)areaSum / glyphs.Count) : 0;

        Console.WriteLine("── report ───────────────────────────────────────────────");
        Console.WriteLine($"baked       : {glyphs.Count} glyphs (empty-outline {skipped.Count}, failed {failed.Count})");
        if (skipped.Count > 0)
            Console.WriteLine($"  skipped   : {FormatCodePoints(skipped)}");
        if (failed.Count > 0)
            Console.WriteLine($"  failed    : {string.Join(", ", failed.Select(f => $"U+{f.CodePoint:X4}: {f.Message}"))}");
        Console.WriteLine($"glyph box   : {minW}x{minH} .. {maxW}x{maxH} (average side {averageSide:F1}px)");
        Console.WriteLine($"pixels      : raw {rawTotal / 1024.0 / 1024.0:F2} MiB → pack {packBytes.Length / 1024.0 / 1024.0:F2} MiB ({packBytes.Length * 100.0 / Math.Max(1, rawTotal):F1}%)");
        Console.WriteLine($"pack        : {outPath}");
        Console.WriteLine($"verify      : {glyphs.Count} reload+inflate byte-equal, {sampleCount} re-raster samples, registry accepted");
        Console.WriteLine($"elapsed     : {stopwatch.Elapsed:hh\\:mm\\:ss\\.fff}");
        return 0;
    }

    static bool MetricsEqual(GlyphMetrics a, GlyphMetrics b)
        => a.Width == b.Width && a.Height == b.Height && a.X0 == b.X0 && a.Y0 == b.Y0 && a.X1 == b.X1 && a.Y1 == b.Y1 &&
           a.AdvanceWidth == b.AdvanceWidth &&
           a.HasPlaneBounds == b.HasPlaneBounds && a.PlaneLeft == b.PlaneLeft && a.PlaneBottom == b.PlaneBottom &&
           a.PlaneRight == b.PlaneRight && a.PlaneTop == b.PlaneTop &&
           a.HasAtlasBounds == b.HasAtlasBounds && a.AtlasSourceX == b.AtlasSourceX && a.AtlasSourceY == b.AtlasSourceY &&
           a.AtlasSourceWidth == b.AtlasSourceWidth && a.AtlasSourceHeight == b.AtlasSourceHeight;

    static string FormatCodePoints(List<int> codePoints)
    {
        const int limit = 40;
        var head = codePoints.Take(limit).Select(cp => $"U+{cp:X4}");
        return codePoints.Count <= limit
            ? string.Join(" ", head)
            : $"{string.Join(" ", head)} … (+{codePoints.Count - limit} more)";
    }

    static Dictionary<string, string> ParseArgs(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                continue;
            string key = args[i];
            string value = "true";
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                value = args[++i];
            map[key] = value;
        }
        return map;
    }

    static string? Get(Dictionary<string, string> options, string key)
        => options.TryGetValue(key, out var value) ? value : null;

    static void PrintUsage()
    {
        Console.WriteLine("""
            SeasonGlyphBake — pre-bake MSDF glyph packs for one font file.

            Usage:
              SeasonGlyphBake --font <ttf> --out <pack> --roots <dir;dir> [options]

            Options:
              --map <file>        simplified/traditional mapping table (both columns captured)
              --extra "chars"     additional characters that must be covered
              --raster <n>        raster size saved into the pack (default 48, must match the runtime)
              --compress <mode>   deflate (default) | none
              --threads <n>       rasterization workers (default: processor count)
              --verify <n>        number of random re-rasterization samples compared byte-for-byte
            """);
    }
}
