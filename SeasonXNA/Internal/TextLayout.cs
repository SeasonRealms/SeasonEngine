// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Buffers;
using System.Text;
using Microsoft.Xna.Framework;
using Glyph = global::Season.Rendering.Glyph2D;
using NativeFont = global::Season.Fonts.Font;

namespace SeasonXNA.Internal;

/// <summary>Immutable logical line layout. Ink may overhang the measured advance box.</summary>
internal sealed record TextLayout(Vector2 Size, Glyph[] Glyphs)
{
    internal static TextLayout Build(NativeFont font, int size, int lineSpacing, float baseline, char? replacement, string text)
    {
        if (text.Length == 0) return new(Vector2.Zero, []);
        var glyphs = new List<Glyph>(Math.Min(text.Length, 1024));
        float x = 0, y = 0, width = 0;
        for (int index = 0; index < text.Length;)
        {
            char ch = text[index];
            if (ch is '\r' or '\n')
            {
                index += ch == '\r' && index + 1 < text.Length && text[index + 1] == '\n' ? 2 : 1;
                width = Math.Max(width, x);
                x = 0;
                y += lineSpacing;
                continue;
            }
            if (Rune.DecodeFromUtf16(text.AsSpan(index), out var rune, out int consumed) != OperationStatus.Done)
                throw new ArgumentException("Text contains invalid UTF-16.", nameof(text));
            index += consumed;
            if (Rune.IsControl(rune))
                throw new ArgumentException($"Unsupported control U+{rune.Value:X}; expand tabs explicitly.", nameof(text));
            int codePoint = rune.Value;
            if (!font.TryGetGlyphLayoutMetrics(size, codePoint, out var metrics, out _))
            {
                if (replacement is not { } substitute ||
                    !font.TryGetGlyphLayoutMetrics(size, substitute, out metrics, out _))
                    throw new ArgumentException($"Font has no glyph U+{codePoint:X}; choose a font or set DefaultCharacter.", nameof(text));
                codePoint = substitute;
            }
            if (!float.IsFinite(metrics.AdvanceWidth) || metrics.AdvanceWidth < 0)
                throw new ArgumentException($"Invalid advance for U+{codePoint:X}.", nameof(text));
            if (metrics.Width != 0 || metrics.Height != 0)
                glyphs.Add(new(codePoint, new(x, y + baseline)));
            x += metrics.AdvanceWidth;
        }
        width = Math.Max(width, x);
        float height = y + lineSpacing;
        if (!float.IsFinite(width) || !float.IsFinite(height) || !float.IsFinite(y + baseline))
            throw new ArgumentOutOfRangeException(nameof(text), "Text layout overflow.");
        return new(new(width, height), glyphs.ToArray());
    }
}
