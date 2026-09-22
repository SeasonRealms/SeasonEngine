// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using NativeFont = global::Season.Fonts.Font;
using SeasonXNA.Internal;
using System.Text;

namespace Microsoft.Xna.Framework.Graphics;

/// <summary>A native font and fixed size, not an XNB bitmap SpriteFont implementation.</summary>
public sealed class SpriteFont : IDisposable
{
    private NativeFont? _font;
    private int _lineSpacing;
    private char? _defaultCharacter;
    private readonly object _layoutSync = new();
    private readonly Dictionary<string, TextLayout> _layouts = new(StringComparer.Ordinal);
    private int _cachedCharacters;

    internal SpriteFont(NativeFont font, int size)
    {
        ArgumentNullException.ThrowIfNull(font);
        if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        if (font.Typeface is null || font.Typeface.UnitsPerEm == 0)
            throw new ArgumentException("The native font must have a loaded typeface.", nameof(font));
        // Derive metrics at the requested size, not the native font's construction size.
        float ratio = size / (float)font.Typeface.UnitsPerEm;
        BaselineOffset = font.Typeface.Ascender * ratio;
        float lineHeight = (font.Typeface.Ascender - (float)font.Typeface.Descender + font.Typeface.LineGap) * ratio;
        if (!float.IsFinite(BaselineOffset) || !float.IsFinite(lineHeight) || lineHeight <= 0 || (double)lineHeight > int.MaxValue)
            throw new ArgumentException("The native font has invalid line metrics.", nameof(font));
        _lineSpacing = checked((int)Math.Ceiling(lineHeight));
        Size = size;
        _font = font;
    }

    public int LineSpacing
    {
        get => _lineSpacing;
        set
        {
            lock (_layoutSync)
            {
                _ = Font;
                if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value));
                if (_lineSpacing == value) return;
                _lineSpacing = value;
                ClearLayouts();
            }
        }
    }

    /// <summary>Null means strict missing-glyph errors; no automatic font fallback.</summary>
    public char? DefaultCharacter
    {
        get => _defaultCharacter;
        set
        {
            lock (_layoutSync)
            {
                var font = Font;
                if (value is { } ch && (char.IsSurrogate(ch) || char.IsControl(ch) ||
                    !font.TryGetGlyphLayoutMetrics(Size, ch, out _, out _)))
                    throw new ArgumentException("DefaultCharacter must be a supported, non-control BMP glyph.", nameof(value));
                if (_defaultCharacter == value) return;
                _defaultCharacter = value;
                ClearLayouts();
            }
        }
    }

    public Vector2 MeasureString(string text) => Layout(text).Size;
    public Vector2 MeasureString(StringBuilder text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return MeasureString(text.ToString());
    }

    internal TextLayout Layout(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_layoutSync)
        {
            var font = Font;
            if (_layouts.TryGetValue(text, out var layout)) return layout;
            layout = TextLayout.Build(font, Size, _lineSpacing, BaselineOffset, _defaultCharacter, text);
            // Bound retained text as well as entry count. Large one-off paragraphs are not cached.
            if (text.Length <= 4096)
            {
                if (_layouts.Count >= 256 || _cachedCharacters + text.Length > 16384) ClearLayouts();
                _layouts.Add(text, layout);
                _cachedCharacters += text.Length;
            }
            return layout;
        }
    }

    private void ClearLayouts() { _layouts.Clear(); _cachedCharacters = 0; }
    public bool IsDisposed => _font is null;
    internal int Size { get; }
    internal float BaselineOffset { get; }
    internal NativeFont Font
    {
        get
        {
            var font = _font;
            ObjectDisposedException.ThrowIf(font is null, this);
            return font!;
        }
    }

    // Season owns glyph caches; dropping this reference does not evict native glyphs.
    public void Dispose()
    {
        lock (_layoutSync)
        {
            _font = null;
            ClearLayouts();
        }
    }
}
