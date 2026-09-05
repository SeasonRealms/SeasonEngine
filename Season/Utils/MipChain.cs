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
    /// 2-6 clause 1: the mip policy a material slot implies. This lives here rather than once per backend because the
    /// mapping was in fact duplicated per backend and per code path, and one of those copies was missing: the pixel
    /// branch of EnsureSurfaceTexture created its texture with the default policy, so every material fed from an
    /// in-memory decoder through Surface.TextureOverride silently had no chain at all while its glTF-loaded neighbours
    /// did. Routing every call site through one function is what makes that class of omission impossible rather than
    /// merely unlikely - a missing chain is invisible in review and shows up only as aliasing on one kind of asset.
    ///
    /// Normal maps get their own policy because averaging unit vectors shortens them, and the linear channels
    /// (metallic-roughness, occlusion) get theirs because they are not colour and must not be treated as such.
    /// </summary>
    public static TextureMipPolicy PolicyForSurfaceSlot(SurfaceTextureSlot slot) => slot switch
    {
        SurfaceTextureSlot.Normal => TextureMipPolicy.Normal,
        SurfaceTextureSlot.MetallicRoughness => TextureMipPolicy.Linear,
        SurfaceTextureSlot.Occlusion => TextureMipPolicy.Linear,
        _ => TextureMipPolicy.Color,
    };

    /// <summary>
    /// The URI scheme every compute pass registers its output textures under - see Bloom.TextureNamePrefix,
    /// SkyAtmosphere.SkyViewTextureName, Gtao, Taa and the rest. A name in this scheme is not an asset that can be
    /// loaded from storage; it is a GPU resource the producing pass rewrites, and it is already in the backend's
    /// texture dictionary under exactly this name.
    /// </summary>
    public const string ComputeTextureScheme = "compute://";

    /// <summary>Whether a texture name refers to a compute pass output rather than to a loadable asset.</summary>
    public static bool IsComputeTextureName(string? name)
        => name != null && name.StartsWith(ComputeTextureScheme, StringComparison.Ordinal);

    /// <summary>
    /// 2-6 clause 4: the policy for a slot fed by a texture <em>name</em>, as opposed to one fed by pixels. It is
    /// <see cref="TextureMipPolicy.None"/> for a compute output, which keeps <see cref="CacheKey"/> returning the bare
    /// name for it. Surface.BaseColorTexturePath is documented to accept such a name - a procedural skybox sets it to
    /// FrameSchedule.SkyViewTexture - so treating every name as a loadable asset would give the LUT a policy-keyed key
    /// that no entry answers to, and the miss would bind White: a procedural sky rendered as a blank box.
    ///
    /// A chain would be meaningless for these anyway. The producing pass writes level 0 of a GPU resource every frame
    /// and there is nothing in that path that would refilter the smaller levels, so they would go stale immediately.
    /// </summary>
    public static TextureMipPolicy PolicyForNamedTexture(string? name, SurfaceTextureSlot slot)
        => IsComputeTextureName(name) ? TextureMipPolicy.None : PolicyForSurfaceSlot(slot);

    /// <summary>
    /// 2-6 clause 4: the cache key a texture occupies once its policy participates in identity. The formula lives here
    /// because every backend and every code path that keys a texture has to agree on it character for character. A
    /// disagreement does not fail loudly: the lookup simply misses, the caller falls back to the White texture, and the
    /// result reads as a missing asset rather than as a cache bug. Keeping one formula is what makes that impossible.
    ///
    /// The suffix is omitted for <see cref="TextureMipPolicy.None"/> so every key predating 2-6 stays byte-identical.
    /// That is what lets sprites and materials keep sharing one dictionary while only the latter carry chains: a sprite
    /// asks with no policy and keeps the bare path, a material asks with one and gets its own entry. They no longer
    /// contend for a single entry whose chain would be decided by whichever loaded first, and neither can release the
    /// other's texture out from under it.
    ///
    /// The policy is used as requested, not as effectively applied: <see cref="ShouldGenerate"/> may still decline to
    /// build a chain for a small texture or with mipmaps switched off. Folding that decision in would make the key
    /// depend on a mutable setting, so an insert and a later lookup could disagree if the setting changed between them.
    /// A redundant entry costs some memory; a disagreeing key costs the texture.
    /// </summary>
    public static string CacheKey(string name, TextureMipPolicy policy)
        => policy == TextureMipPolicy.None ? name : $"{name}#mip{policy}";

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
    /// 2-6 clause 5: the entry point every texture-creation path should use, in place of the
    /// <see cref="ShouldGenerate"/> / <see cref="Build"/> / single-level triple that was copied into all four
    /// backends. It exists because <see cref="TextureMipPolicy.Normal"/> now carries an engine-owned alpha channel
    /// (see <see cref="Build"/>), and that contract has to hold on the branch where no chain is built too. A normal
    /// map below <see cref="RenderQuality.TextureMipMinSize"/> that kept its authored alpha would be read by the
    /// shader as a variance measurement, so the no-chain branch is not a case that can be left alone.
    ///
    /// Never mutates <paramref name="level0Rgba"/>; the array to upload is the return value, which may or may not be
    /// the same instance. Copying instead of overwriting in place costs one allocation only for a normal map small
    /// enough to be denied a chain, and in exchange no caller has to know whether its pixel buffer is still intact.
    /// </summary>
    public static byte[] Prepare(
        byte[] level0Rgba,
        int width,
        int height,
        TextureMipPolicy policy,
        out MipLevelInfo[] levels)
    {
        if (ShouldGenerate(policy, width, height))
            return Build(level0Rgba, width, height, policy, out levels);

        levels = [new MipLevelInfo(width, height, 0)];

        if (policy != TextureMipPolicy.Normal)
            return level0Rgba;

        int expected = width * height * 4;
        var single = new byte[expected];
        level0Rgba.AsSpan(0, expected).CopyTo(single);
        for (int p = 3; p < expected; p += 4)
            single[p] = VarianceFree;
        return single;
    }

    /// <summary>
    /// The alpha value that means "this texel carries no normal variance", chosen so that the shader's
    /// variance-to-roughness mapping evaluates to an exact no-op on it. Level 0 always stores it, and so does every
    /// level when <see cref="RenderQuality.TextureNormalVariance"/> is off - which is why that switch needs no shader
    /// branch and no new constant in any backend's material block.
    /// </summary>
    const byte VarianceFree = 255;

    /// <summary>
    /// 2-6 clause 4/5: the counterpart of <see cref="Prepare"/> for in-place replacement of a texture that already
    /// exists on the GPU. Returns null when <paramref name="level0Rgba"/> can be uploaded exactly as it stands, and
    /// otherwise the block to upload together with its level table.
    ///
    /// The number of levels comes from the caller rather than from <see cref="ShouldGenerate"/>, and that distinction
    /// matters: the level count is baked into the GPU resource at creation, while ShouldGenerate reads mutable quality
    /// settings. Re-deciding here would let a setting changed since creation produce a block whose level count
    /// disagrees with the resource - too few levels leaves the smaller ones holding the previous image, too many
    /// overruns the subresource range.
    ///
    /// A single-level normal map still comes back as a copy rather than as null, because the engine owns alpha in that
    /// slot whether or not a chain exists.
    /// </summary>
    public static byte[]? Refresh(
        ReadOnlySpan<byte> level0Rgba,
        int width,
        int height,
        int existingLevelCount,
        TextureMipPolicy policy,
        out MipLevelInfo[]? levels)
    {
        if (existingLevelCount > 1)
            return Build(level0Rgba, width, height, policy, out levels);

        if (policy != TextureMipPolicy.Normal)
        {
            levels = null;
            return null;
        }

        levels = [new MipLevelInfo(width, height, 0)];
        int expected = width * height * 4;
        var single = new byte[expected];
        level0Rgba.Slice(0, expected).CopyTo(single);
        for (int p = 3; p < expected; p += 4)
            single[p] = VarianceFree;
        return single;
    }

    /// <summary>
    /// Builds the packed chain. <paramref name="level0Rgba"/> must be tightly packed RGBA8 with a stride of exactly
    /// width * 4; callers that receive a padded decoder stride must de-stride first. Level 0 colour is copied through
    /// verbatim, including for <see cref="TextureMipPolicy.Normal"/>: renormalization repairs what this filter
    /// introduces and must not silently rewrite authored data.
    ///
    /// 2-6 clause 5, Toksvig. For <see cref="TextureMipPolicy.Normal"/> the alpha channel is not authored data - it
    /// belongs to the engine and is overwritten on every level. It stores the mean resultant length of the level-0
    /// normals that fell into the texel: 255 for a flat footprint, lower as the footprint's normals disagree. The
    /// shader folds that into roughness, because renormalizing the average is the right answer for the diffuse term
    /// and the wrong one for the specular term - a texel covering a whole ridge gets a single unit normal and its
    /// authored narrow lobe, which is what makes distant specular highlights flicker between frames as the sampled
    /// footprint shifts. No mip filter can fix that; the lost width has to come back as roughness. Alpha was chosen
    /// as the carrier because it is already allocated and already filtered, so this costs no memory and, unlike
    /// baking the result into the metallic-roughness texture, it leaves that texture's content independent of any
    /// other texture - meaning <see cref="CacheKey"/> does not have to grow a second texture's identity, and it also
    /// reaches the many materials here that use a normal map with a scalar roughness factor and no such texture.
    ///
    /// The two passes below are not an implementation detail either. Reduction has to run over the whole chain first
    /// and stay unnormalized, because level n+1 is reduced from level n: renormalizing level n in place - as this did
    /// before - would destroy exactly the shortening that the next level needs to measure, so variance could never
    /// accumulate past one level and the coarse levels were averages of already-straightened normals rather than of
    /// the real level-0 ones. Doing it in this order needs no scratch buffer, since a byte encoding of a mean of unit
    /// vectors is an affine map of the vectors themselves and so the mean of the encoded bytes is the encoding of the
    /// mean. One honest caveat: with the odd-extent three-tap kernel the taps overlap, so a level is a weighted mean
    /// of the level below rather than a partition of it, and the mean of means is an approximation of the true
    /// level-0 mean rather than an identity. The bias is small and the alternative - keeping every level's footprint
    /// as an explicit list of level-0 texels - is not worth it for a roughness perturbation.
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

        // Pass one: reduce the entire chain. For a normal map every level below zero is left unnormalized here, so
        // each one holds the mean of the level-0 normals over its footprint and still carries the length that the
        // next level, and the variance pass, have to read. The step out of level 0 is the exception - see
        // ReduceUnitNormals for why it cannot be the plain byte-space average.
        bool isNormal = policy == TextureMipPolicy.Normal;
        for (int i = 1; i < levelCount; i++)
        {
            if (isNormal && i == 1)
                ReduceUnitNormals(packed, infos[0], infos[1]);
            else
                Reduce(packed, infos[i - 1], infos[i]);
        }

        if (isNormal)
        {
            // Level 0 is authored at full resolution, so by definition its footprint holds one normal and no
            // variance. Its alpha is still rewritten: the shader reads the channel unconditionally, and a normal map
            // that happens to pack something else in alpha would otherwise be misread as a variance measurement.
            for (int p = 3; p < expected; p += 4)
                packed[p] = VarianceFree;

            bool measureVariance = RenderQuality.Current.TextureNormalVariance;
            for (int i = 1; i < levelCount; i++)
                Renormalize(packed, infos[i], measureVariance);
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
    /// The level-0 reduction for a normal map, which has to normalize every tap before averaging it. Authored normal
    /// maps are routinely not unit length, and the most common way for that to happen is not a rounding error: the
    /// widespread convention of leaving blue at 255 makes every texel (x, y, 1), so every length is at least one. The
    /// byte-space average of such taps is itself at least one, the variance term 1 - length underflows to zero, and
    /// the entire Toksvig pass silently degrades to a no-op on exactly the assets it was written for -
    /// WaterBottle.glb in this repository is one, its blue channel a constant 255 and its measured spread therefore
    /// zero at every level. Normalizing first also removes a second, quieter error: a weighted mean that lets the
    /// longer taps count for more is not the footprint average it claims to be.
    ///
    /// Only the step out of level 0 needs this. Every deeper level already holds a mean of unit vectors, so its length
    /// cannot exceed one, and reducing those further without touching them is precisely the accumulation the variance
    /// pass depends on - normalizing there would erase the shortening it exists to measure.
    ///
    /// A tap that decodes to near-zero length contributes nothing rather than a substitute direction. It carries no
    /// direction to contribute, and pulling the mean shorter is the honest reading of a texel that cannot be
    /// normalized.
    /// </summary>
    static void ReduceUnitNormals(byte[] block, MipLevelInfo src, MipLevelInfo dst)
    {
        BuildAxis(src.Width, dst.Width, out int[] xTaps, out int[] xWeights, out int xTapCount, out int xDenom);
        BuildAxis(src.Height, dst.Height, out int[] yTaps, out int[] yWeights, out int yTapCount, out int yDenom);

        float denom = (float)xDenom * yDenom;

        for (int y = 0; y < dst.Height; y++)
        {
            int yBase = y * yTapCount;
            for (int x = 0; x < dst.Width; x++)
            {
                int xBase = x * xTapCount;
                float sx = 0f, sy = 0f, sz = 0f;

                for (int ty = 0; ty < yTapCount; ty++)
                {
                    int rowOffset = src.ByteOffset + yTaps[yBase + ty] * src.Width * 4;
                    float wy = yWeights[yBase + ty];
                    for (int tx = 0; tx < xTapCount; tx++)
                    {
                        int p = rowOffset + xTaps[xBase + tx] * 4;
                        float weight = wy * xWeights[xBase + tx];

                        float nx = block[p] * (2f / 255f) - 1f;
                        float ny = block[p + 1] * (2f / 255f) - 1f;
                        float nz = block[p + 2] * (2f / 255f) - 1f;

                        float lenSq = nx * nx + ny * ny + nz * nz;
                        if (lenSq < 1e-8f)
                            continue;

                        float inv = weight / MathF.Sqrt(lenSq);
                        sx += nx * inv;
                        sy += ny * inv;
                        sz += nz * inv;
                    }
                }

                int o = dst.ByteOffset + (y * dst.Width + x) * 4;
                block[o] = Encode(sx / denom);
                block[o + 1] = Encode(sy / denom);
                block[o + 2] = Encode(sz / denom);
                // Alpha is written by the variance pass; anything here would be overwritten, so it is left alone.
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
    /// Records the length of the averaged normal in alpha, then rescales every texel of one generated level back to
    /// unit length in tangent space. Averaging two normals that point in different directions always yields a shorter
    /// vector, so without the rescale the shading normal contracts as distance grows and both the diffuse cosine term
    /// and Fresnel drift with it. That length is not noise though, it is the width of the normal distribution inside
    /// the texel, so it is read out before being discarded - which is why the two steps are one function and cannot
    /// be separated: once the texel is unit length the measurement is gone.
    ///
    /// Degenerate texels, where the averaged vector has collapsed to near zero, are pinned to +Z rather than
    /// normalized: dividing by a near-zero length would amplify quantization noise into an arbitrary direction. Their
    /// alpha is left at zero, which the shader's mapping turns into fully rough - the correct reading of a footprint
    /// whose normals cancel out, and the reason that mapping needs a guarded denominator rather than a bare divide.
    ///
    /// The stored value is 1 - sqrt(1 - length), not the length itself, and 255 still means variance-free. Eight bits
    /// spent uniformly on the length cannot express a small spread at all: one code below 255 is a length of 254/255,
    /// which is about 3.6 degrees of normal spread, nine times the lobe width of a roughness-0.1 material. A nearly
    /// flat normal map would therefore step straight from no correction to roughness 0.30, losing the sheen this pass
    /// exists to preserve, and the step would land at different mip levels across a surface and read as banding.
    /// Squaring the complement spends codes quadratically near one, which puts the first step at roughly 0.2 degrees -
    /// under that lobe - and coarsens the low end instead, where roughness has already saturated to 1 and nothing is
    /// left to resolve. Alpha is unsigned here, unlike the signed encoding the colour channels use, because the
    /// quantity never leaves [0, 1] and spending half the range on negatives would halve its precision.
    /// </summary>
    static void Renormalize(byte[] block, MipLevelInfo level, bool measureVariance)
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
                block[p + 3] = measureVariance ? (byte)0 : VarianceFree;
                continue;
            }

            float len = MathF.Sqrt(lenSq);
            float inv = 1f / len;
            block[p] = Encode(x * inv);
            block[p + 1] = Encode(y * inv);
            block[p + 2] = Encode(z * inv);
            // Quantization of the colour channels can push the decoded length a hair past one, so the complement is
            // floored at zero before the square root rather than trusting it to stay in domain. That floor is a guard
            // against rounding and nothing more: it must not become the thing that absorbs systematically over-long
            // input, which is how this measurement was once silently dead on every normal map with blue pinned to 255
            // - see ReduceUnitNormals, which is what makes the length genuinely bounded by one.
            block[p + 3] = measureVariance
                ? (byte)Math.Clamp((int)MathF.Round((1f - MathF.Sqrt(MathF.Max(0f, 1f - len))) * 255f), 0, 255)
                : VarianceFree;
        }
    }

    static byte Encode(float v)
    {
        int q = (int)MathF.Round((v * 0.5f + 0.5f) * 255f);
        return (byte)Math.Clamp(q, 0, 255);
    }
}
