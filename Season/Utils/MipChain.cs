// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Utils;

/// <summary>Geometry of one level inside a packed mip chain produced by <see cref="MipChain.Build"/>.</summary>
public readonly struct MipLevelInfo
{
    /// <summary>Level width in pixels.</summary>
    public readonly int Width;

    /// <summary>Level height in pixels.</summary>
    public readonly int Height;

    /// <summary>Byte offset of this level inside the packed block returned by Build.</summary>
    public readonly int ByteOffset;

    /// <summary>Byte length of this level, always Width * Height * 4 with no row padding.</summary>
    public readonly int ByteLength;

    internal MipLevelInfo(int width, int height, int byteOffset)
    {
        Width = width;
        Height = height;
        ByteOffset = byteOffset;
        ByteLength = width * height * 4;
    }
}

/// <summary>
/// 2-6 clause 3: the single mip-chain generator for the whole engine. Every backend consumes the packed block this
/// class produces and only re-pitches it into its own upload layout, so all four backends store bit-identical levels.
///
/// Why generation is on the CPU rather than on the GPU. Neither D3D12 nor Vulkan has a GenerateMips primitive, so a
/// GPU path would mean a per-backend blit pipeline plus a barrier per level, and those commands can only run on the
/// direct or compute queue. Texture upload in this engine deliberately does not: D3D12 batches into a shared upload
/// heap on the copy queue and hands over to the direct queue through a fence, and Vulkan uses the transfer queue.
/// Generating on the GPU would force uploads off the copy queue and dismantle that hand-off. Metal does have a blit
/// encoder primitive and WebGPU has nothing at all, so a GPU path would also give four different filtered results
/// for the same input. One CPU implementation costs a single pass over 1.33x the source pixels at load time, which
/// is negligible next to image decoding, and buys exact cross-backend agreement.
///
/// Filter contract. All arithmetic is integer, so results are reproducible bit for bit on any platform and any
/// floating-point mode. Level n has dimensions max(1, w0 >> n) by max(1, h0 >> n). Reduction is separable and each
/// axis is handled independently:
///   - source extent 1: the axis is already exhausted, single tap, weight 1;
///   - source extent even: two taps, weights 1/2 and 1/2, the ordinary box filter;
///   - source extent odd (2d+1 reducing to d): three taps at 2i, 2i+1, 2i+2 with weights
///     (d-i)/(2d+1), d/(2d+1), (i+1)/(2d+1).
/// The odd case is not a detail to skip. Simply shifting right would drop the final column or row, and because the
/// discarded edge is always on the same side, the error accumulates in one direction level after level - which shows
/// up as distant texture content visibly creeping toward one corner as the camera pulls away. The three-tap kernel
/// instead gives every source pixel a total weight of 2/(2d+1) across the two destinations that read it, so the
/// reduction is unbiased and energy preserving.
///
/// Filtering space. The engine performs no sRGB decode anywhere: textures are created as plain UNORM and the pixel
/// shader consumes the sampled value directly as linear radiance. The correctness criterion for a filter is that it
/// averages in the same space the consumer interprets, not that it averages in physically linear space, so
/// averaging the stored bytes is the self-consistent choice here. Should the engine ever gain real sRGB handling,
/// this is the one place that has to change with it.
/// </summary>
public static class MipChain
{
    /// <summary>
    /// Number of levels in a full chain for the given dimensions, equal to floor(log2(max(w, h))) + 1.
    /// Computed by iteration rather than a log so it agrees exactly with the halving rule used by Build.
    /// </summary>
    public static int ComputeLevelCount(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return 0;

        int levels = 1;
        int w = width, h = height;
        while (w > 1 || h > 1)
        {
            w = Math.Max(1, w >> 1);
            h = Math.Max(1, h >> 1);
            levels++;
        }
        return levels;
    }

    /// <summary>
    /// Whether a chain should actually be built for this texture. Both quality gates live here rather than in the
    /// backends so that the four cannot drift apart on when a chain exists - a divergence that would be invisible in
    /// code review and show up only as one platform aliasing where the others do not.
    /// </summary>
    public static bool ShouldGenerate(TextureMipPolicy policy, int width, int height)
    {
        if (policy == TextureMipPolicy.None)
            return false;
        if (!RenderQuality.Current.TextureMipmaps)
            return false;
        // A 1x1 texture, or any texture already at its smallest level on both axes, has nothing to reduce.
        if (width <= 1 && height <= 1)
            return false;
        return Math.Max(width, height) >= RenderQuality.Current.TextureMipMinSize;
    }

    /// <summary>
    /// Builds the packed chain. <paramref name="level0Rgba"/> must be tightly packed RGBA8 with a stride of exactly
    /// width * 4; callers that receive a padded decoder stride must de-stride first. Level 0 is copied through
    /// verbatim, including for <see cref="TextureMipPolicy.Normal"/>: renormalization repairs what this filter
    /// introduces and must not silently rewrite authored data.
    /// </summary>
    /// <returns>One contiguous block holding every level in order, with no padding between or inside levels.</returns>
    public static byte[] Build(
        ReadOnlySpan<byte> level0Rgba,
        int width,
        int height,
        TextureMipPolicy policy,
        out MipLevelInfo[] levels)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Mip chain source dimensions must be positive.");

        int expected = width * height * 4;
        if (level0Rgba.Length < expected)
            throw new ArgumentException(
                $"Mip chain source must be tightly packed RGBA8. Expected at least {expected} bytes " +
                $"for {width}x{height}, got {level0Rgba.Length}.",
                nameof(level0Rgba));

        int levelCount = ComputeLevelCount(width, height);
        var infos = new MipLevelInfo[levelCount];

        int offset = 0;
        int w = width, h = height;
        for (int i = 0; i < levelCount; i++)
        {
            infos[i] = new MipLevelInfo(w, h, offset);
            offset += infos[i].ByteLength;
            w = Math.Max(1, w >> 1);
            h = Math.Max(1, h >> 1);
        }

        var packed = new byte[offset];
        level0Rgba.Slice(0, expected).CopyTo(packed.AsSpan(0, expected));

        bool renormalize = policy == TextureMipPolicy.Normal;
        for (int i = 1; i < levelCount; i++)
        {
            var src = infos[i - 1];
            var dst = infos[i];
            Reduce(packed, src, dst);
            if (renormalize)
                Renormalize(packed, dst);
        }

        levels = infos;
        return packed;
    }

    /// <summary>
    /// One separable reduction step from <paramref name="src"/> to <paramref name="dst"/>, both inside the same
    /// packed block. Per-axis taps and integer weights are precomputed once per destination index, then combined as
    /// an outer product, so a level is produced in a single pass with no scratch buffer and no intermediate rounding.
    /// </summary>
    static void Reduce(byte[] block, MipLevelInfo src, MipLevelInfo dst)
    {
        BuildAxis(src.Width, dst.Width, out int[] xTaps, out int[] xWeights, out int xTapCount, out int xDenom);
        BuildAxis(src.Height, dst.Height, out int[] yTaps, out int[] yWeights, out int yTapCount, out int yDenom);

        long denom = (long)xDenom * yDenom;
        long half = denom / 2;

        for (int y = 0; y < dst.Height; y++)
        {
            int yBase = y * yTapCount;
            for (int x = 0; x < dst.Width; x++)
            {
                int xBase = x * xTapCount;
                long r = 0, g = 0, b = 0, a = 0;

                for (int ty = 0; ty < yTapCount; ty++)
                {
                    int rowOffset = src.ByteOffset + yTaps[yBase + ty] * src.Width * 4;
                    long wy = yWeights[yBase + ty];
                    for (int tx = 0; tx < xTapCount; tx++)
                    {
                        int p = rowOffset + xTaps[xBase + tx] * 4;
                        long weight = wy * xWeights[xBase + tx];
                        r += block[p] * weight;
                        g += block[p + 1] * weight;
                        b += block[p + 2] * weight;
                        a += block[p + 3] * weight;
                    }
                }

                int o = dst.ByteOffset + (y * dst.Width + x) * 4;
                block[o] = (byte)((r + half) / denom);
                block[o + 1] = (byte)((g + half) / denom);
                block[o + 2] = (byte)((b + half) / denom);
                block[o + 3] = (byte)((a + half) / denom);
            }
        }
    }

    /// <summary>
    /// Per-axis tap table for one reduction. Returns tap indices and integer weights laid out as
    /// destinationIndex * tapCount + tapSlot, sharing one denominator across the whole axis.
    /// </summary>
    static void BuildAxis(int srcExtent, int dstExtent, out int[] taps, out int[] weights, out int tapCount, out int denom)
    {
        if (srcExtent == dstExtent)
        {
            // Axis already exhausted (extent 1, or a level that cannot halve further): pass through.
            tapCount = 1;
            denom = 1;
            taps = new int[dstExtent];
            weights = new int[dstExtent];
            for (int i = 0; i < dstExtent; i++)
            {
                taps[i] = i;
                weights[i] = 1;
            }
            return;
        }

        if ((srcExtent & 1) == 0)
        {
            tapCount = 2;
            denom = 2;
            taps = new int[dstExtent * 2];
            weights = new int[dstExtent * 2];
            for (int i = 0; i < dstExtent; i++)
            {
                taps[i * 2] = i * 2;
                taps[i * 2 + 1] = i * 2 + 1;
                weights[i * 2] = 1;
                weights[i * 2 + 1] = 1;
            }
            return;
        }

        // Odd source extent: srcExtent == 2 * dstExtent + 1, three overlapping taps per destination.
        int d = dstExtent;
        tapCount = 3;
        denom = srcExtent;
        taps = new int[dstExtent * 3];
        weights = new int[dstExtent * 3];
        for (int i = 0; i < dstExtent; i++)
        {
            int slot = i * 3;
            taps[slot] = i * 2;
            taps[slot + 1] = i * 2 + 1;
            taps[slot + 2] = i * 2 + 2;
            weights[slot] = d - i;
            weights[slot + 1] = d;
            weights[slot + 2] = i + 1;
        }
    }

    /// <summary>
    /// Rescales every texel of one generated level back to unit length in tangent space, leaving alpha untouched.
    /// Averaging two normals that point in different directions always yields a shorter vector, so without this the
    /// shading normal contracts as distance grows and both the diffuse cosine term and Fresnel drift with it.
    /// Degenerate texels, where the averaged vector has collapsed to near zero, are pinned to +Z rather than
    /// normalized: dividing by a near-zero length would amplify quantization noise into an arbitrary direction.
    /// </summary>
    static void Renormalize(byte[] block, MipLevelInfo level)
    {
        int end = level.ByteOffset + level.ByteLength;
        for (int p = level.ByteOffset; p < end; p += 4)
        {
            float x = block[p] * (2f / 255f) - 1f;
            float y = block[p + 1] * (2f / 255f) - 1f;
            float z = block[p + 2] * (2f / 255f) - 1f;

            float lenSq = x * x + y * y + z * z;
            if (lenSq < 1e-8f)
            {
                block[p] = 128;
                block[p + 1] = 128;
                block[p + 2] = 255;
                continue;
            }

            float inv = 1f / MathF.Sqrt(lenSq);
            block[p] = Encode(x * inv);
            block[p + 1] = Encode(y * inv);
            block[p + 2] = Encode(z * inv);
        }
    }

    static byte Encode(float v)
    {
        int q = (int)MathF.Round((v * 0.5f + 0.5f) * 255f);
        return (byte)Math.Clamp(q, 0, 255);
    }
}
