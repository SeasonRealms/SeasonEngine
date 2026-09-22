// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Microsoft.Xna.Framework.Graphics;
using NativeFont = global::Season.Fonts.Font;
using NativeImage = global::Season.Rendering.Image2D;
using NativeVector2 = System.Numerics.Vector2;

namespace SeasonXNA.Interop;

/// <summary>Explicit native resource interop. Load before Draw2D; all images must be straight-alpha.</summary>
public static class SeasonResources
{
    /// <summary>Does not retain a separate native lease. The owner must keep the image alive.</summary>
    public static Texture2D BorrowTexture(NativeImage image) => new(image, ownsImage: false);

    /// <summary>Transfers exclusive disposal responsibility after success. Do not transfer the same image twice.</summary>
    public static Texture2D TakeTextureOwnership(NativeImage image) => new(image, ownsImage: true);

    public static Texture2D LoadTexture(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        // XNA dimensions are pixels. Image2D's independent design size is not used by this handle.
        var image = NativeImage.Load(path, NativeVector2.One);
        try { return TakeTextureOwnership(image); }
        catch { image.Dispose(); throw; }
    }

    /// <summary>The returned font is managed by Season/GC, not a disposable GPU object.</summary>
    public static SpriteFont LoadFont(string path, int size)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        return BorrowFont(new NativeFont(path, size), size);
    }

    /// <summary>Shares the typeface. The owner must not mutate it while referenced by queued frames.</summary>
    public static SpriteFont BorrowFont(NativeFont font, int size) => new(font, size);

    // These are native escape hatches, not additions to the public XNA signatures.
    public static NativeImage GetImage(Texture2D texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        return texture.Image;
    }

    public static NativeFont GetFont(SpriteFont font)
    {
        ArgumentNullException.ThrowIfNull(font);
        return font.Font;
    }

    public static int GetFontSize(SpriteFont font)
    {
        _ = GetFont(font);
        return font.Size;
    }

    public static float GetBaselineOffset(SpriteFont font)
    {
        _ = GetFont(font);
        return font.BaselineOffset;
    }
}
