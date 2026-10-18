// SeasonGlyphBake: minimal stand-ins for the engine services that the linked
// Font.cs / GlyphPack.cs sources reference. The tool links both files verbatim, so
// only types *outside* those files need stubs. The real GlyphPackRegistry is used as-is:
// since no pack is ever registered during rasterization, CreateMsdfGlyph always renders
// freshly through the live pipeline, while the verify stage can register a written pack
// and exercise the exact runtime lookup path.

namespace Season.Basic
{
    public enum LogType
    {
        None = 0,
        Error = 1,
        Load = 2,
        Texts = 4,
        GI = 8,
        Backend = 16
    }

    public class BaseApp
    {
        public void AddLog(LogType logType, string log)
        {
            if (logType == LogType.Error)
                Console.Error.WriteLine($"[engine:{logType}] {log}");
        }
    }

    public static class DeviceServices
    {
        public static BaseApp BaseApp { get; set; } = new();
    }
}

namespace Season.Storage
{
    public static class StorageService
    {
        public static string DirectoryBase { get; set; } = Path.GetTempPath();

        public static string SubPath(string directory, string res)
            => Path.Combine(directory, res.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar));

        public static byte[] LoadBytes(string res) => File.ReadAllBytes(res);

        public static Task<byte[]> LoadBytesAsync(string res) => Task.FromResult(File.ReadAllBytes(res));
    }
}

namespace Season.Fonts
{
    /// <summary>Compile-only stand-in: GlyphPrewarm (linked from GlyphPack.cs) names the atlas entry.</summary>
    internal readonly struct GlyphAtlasEntry
    {
    }

    /// <summary>
    /// Compile-only stand-in for the platform glyph atlas. The bake tool never pre-warms and
    /// never instantiates this type; it exists solely so GlyphPrewarm compiles.
    /// </summary>
    internal sealed class GlyphAtlasManager<TTexture> : IDisposable
    {
        public bool TryEnsureStableGlyph(Font font, int size, int codePoint, out GlyphAtlasEntry entry, out TTexture texture)
        {
            entry = default;
            texture = default!;
            return false;
        }

        public void Dispose()
        {
        }
    }
}
