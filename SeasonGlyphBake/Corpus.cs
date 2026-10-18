// SeasonGlyphBake: character-set extraction. The goal is a safe *superset* of every code
// point the running game can hand to a given FontPair:
//   source corpus (string literals + text resources)
//   ∪ default symbols (ASCII / CJK punctuation / full-width forms)
//   ∪ both columns of the simplified-traditional mapping table
// A superset that over-collects is harmless (unsupported code points are dropped by the
// per-font GetGlyphIndex filter); under-collecting would reintroduce first-display jank.

namespace SanguoSeason.Tools.SeasonGlyphBake;

static class Corpus
{
    static readonly string[] ScanExtensions = { ".cs", ".xml", ".jso", ".txt", ".json" };
    static readonly string[] SkipDirectoryNames = { "bin", "obj", ".git", ".vs", "node_modules" };
    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Common UI / typesetting symbols outside the ASCII and full-width ranges.</summary>
    static readonly int[] ExtraSymbols =
    {
        0x00B7, // ·
        0x2010, 0x2013, 0x2014, // ‐ – —
        0x2018, 0x2019, 0x201C, 0x201D, // ‘ ’ “ ”
        0x2022, 0x2026, // • …
        0x203B, // ※
        0x2190, 0x2191, 0x2192, 0x2193, // ← ↑ → ↓
        0x25A0, 0x25A1, // ■ □
        0x25B2, 0x25B3, // ▲ △
        0x25C6, 0x25C7, // ◆ ◇
        0x25CB, 0x25CF, // ○ ●
        0x2605, 0x2606, // ★ ☆
        0x2665, 0x2666, 0x266A, 0x266B // ♥ ♦ ♪ ♫
    };

    /// <summary>Collects the deduplicated, sorted code-point set from all configured sources.</summary>
    public static SortedSet<int> Collect(IReadOnlyList<string> roots, string? mapPath, string? extra)
    {
        var set = new SortedSet<int>();
        AddDefaultSymbols(set);

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                Console.Error.WriteLine($"[warn] corpus root not found, skipped: {root}");
                continue;
            }

            int files = 0;
            foreach (var file in EnumerateFiles(root))
            {
                AddText(set, Decode(File.ReadAllBytes(file)));
                files++;
            }
            Console.WriteLine($"corpus root : {root} ({files} files scanned)");
        }

        if (!string.IsNullOrEmpty(mapPath))
        {
            if (File.Exists(mapPath))
            {
                AddText(set, Decode(File.ReadAllBytes(mapPath)));
                Console.WriteLine($"corpus map  : {mapPath}");
            }
            else
            {
                Console.Error.WriteLine($"[warn] map file not found, skipped: {mapPath}");
            }
        }

        if (!string.IsNullOrEmpty(extra))
        {
            AddText(set, extra);
            Console.WriteLine($"corpus extra: {extra}");
        }

        return set;
    }

    /// <summary>Strict UTF-8, then GB18030, then Latin1 — legacy sources may carry either encoding.</summary>
    public static string Decode(byte[] bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
        }

        try
        {
            return Encoding.GetEncoding("GB18030").GetString(bytes);
        }
        catch (ArgumentException)
        {
        }

        return Encoding.Latin1.GetString(bytes);
    }

    /// <summary>Adds every non-control scalar value in <paramref name="text"/> (surrogate pairs are single runes).</summary>
    public static void AddText(SortedSet<int> set, string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsControl(rune) || rune.Value < 0x20)
                continue;
            set.Add(rune.Value);
        }
    }

    public static void AddDefaultSymbols(SortedSet<int> set)
    {
        for (int cp = 0x20; cp <= 0x7E; cp++) set.Add(cp); // printable ASCII
        for (int cp = 0x3000; cp <= 0x303F; cp++) set.Add(cp); // CJK symbols and punctuation
        for (int cp = 0xFF01; cp <= 0xFF5E; cp++) set.Add(cp); // full-width forms
        for (int cp = 0xFFE0; cp <= 0xFFE5; cp++) set.Add(cp); // full-width currency signs
        foreach (int cp in ExtraSymbols) set.Add(cp);
    }

    /// <summary>Depth-first scan with build-output directories pruned; the mapping table is fed separately.</summary>
    static IEnumerable<string> EnumerateFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var directory = stack.Pop();
            foreach (var sub in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(sub);
                if (SkipDirectoryNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                    continue;
                stack.Push(sub);
            }

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (!ScanExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    continue;
                if (string.Equals(Path.GetFileName(file), "ChineseCodes.txt", StringComparison.OrdinalIgnoreCase))
                    continue; // fed through --map so its simplified+traditional columns are both captured
                yield return file;
            }
        }
    }
}
