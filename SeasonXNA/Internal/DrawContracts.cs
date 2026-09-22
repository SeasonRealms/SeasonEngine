// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Affine = System.Numerics.Matrix3x2;
using Tint = System.Numerics.Vector4;

namespace SeasonXNA.Internal;

/// <summary>Pure CPU checks shared by future image and text draw paths.</summary>
internal static class DrawContracts
{
    internal static Tint ToStraightTint(Color color)
    {
        // D01-A accepts premultiplied tint only, never guesses the texture's encoding.
        if (color.R > color.A || color.G > color.A || color.B > color.A)
            throw new NotSupportedException("D01-A requires RGB <= Alpha. Convert the tint explicitly before drawing.");
        if (color.A == 0) return Tint.Zero;
        float alpha = color.A;
        return new(color.R / alpha, color.G / alpha, color.B / alpha, alpha / 255f);
    }

    internal static Affine ToAffine(Matrix value)
    {
        ReadOnlySpan<float> elements =
        [
            value.M11, value.M12, value.M13, value.M14,
            value.M21, value.M22, value.M23, value.M24,
            value.M31, value.M32, value.M33, value.M34,
            value.M41, value.M42, value.M43, value.M44
        ];
        foreach (float element in elements)
            if (!float.IsFinite(element))
                throw new ArgumentOutOfRangeException(nameof(value), "Draw transforms must be finite.");
        if (value.M13 != 0 || value.M14 != 0 || value.M23 != 0 || value.M24 != 0 ||
            value.M31 != 0 || value.M32 != 0 || value.M33 != 1 || value.M34 != 0 ||
            value.M43 != 0 || value.M44 != 1)
            throw new NotSupportedException("Only a planar affine matrix (Z scale 1, no perspective or Z translation) is supported.");
        return new(value.M11, value.M12, value.M21, value.M22, value.M41, value.M42);
    }

    internal static void ValidateSource(Rectangle source, int textureWidth, int textureHeight)
    {
        if (textureWidth <= 0) throw new ArgumentOutOfRangeException(nameof(textureWidth));
        if (textureHeight <= 0) throw new ArgumentOutOfRangeException(nameof(textureHeight));
        if (source.X < 0 || source.Y < 0 || source.Width <= 0 || source.Height <= 0 ||
            (long)source.X + source.Width > textureWidth || (long)source.Y + source.Height > textureHeight)
            throw new ArgumentOutOfRangeException(nameof(source), "Source region must be nonempty and inside the texture.");
    }

    internal static void ValidateSortMode(SpriteSortMode mode)
    {
        if (mode != SpriteSortMode.Deferred)
            throw new NotSupportedException("Only Deferred submission order is supported.");
    }

    internal static void ValidateEffects(SpriteEffects effects)
    {
        if ((effects & ~(SpriteEffects.FlipHorizontally | SpriteEffects.FlipVertically)) != 0)
            throw new ArgumentOutOfRangeException(nameof(effects), "Unknown sprite effect bits.");
    }
}
