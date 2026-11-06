// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Drawables;

/// <summary>
/// The per-primitive material arithmetic that every backend performs identically. Written once here; the backends keep
/// the parts that are genuinely theirs — collecting the primitive list, walking the in-flight frames, and reading and
/// writing their own constant buffer.
///
/// The split matters because these two functions encode a contract the three copies each restated in slightly different
/// words: <see cref="ApplyAlpha"/> owns <c>BaseColor.W</c> and <c>AlphaCutoff</c>, <see cref="ApplyTint"/> owns
/// <c>BaseColor.rgb</c>, and neither touches the other's channel. Mixing that up is silent — the model just goes
/// translucent or loses its tint — so having a single body to read is worth more than the line count.
///
/// Both work on <c>ref TMat</c> so the struct is edited in place. With <c>unmanaged</c> on the constraint the interface
/// calls compile to constrained calls, which means no boxing and no copy of the whole material block.
/// </summary>
internal static class MaterialOps
{
    /// <summary>
    /// The alpha product itself: <c>originalAlpha * alpha</c>. It is public to the backends because the Web
    /// primitives keep their alpha as loose fields instead of a material block and multiply it in four places;
    /// a second copy of a one-line product is exactly how two copies end up disagreeing.
    /// </summary>
    internal static float CombineAlpha(float originalAlpha, float alpha) => originalAlpha * alpha;

    /// <summary>
    /// The cutoff that travels with <see cref="CombineAlpha"/> on the Web path, where it is scaled only for MASK
    /// materials. The native backends scale it unconditionally in <see cref="ApplyAlpha"/>, which is observationally the
    /// same thing: only the mask path ever reads the value.
    /// </summary>
    internal static float CombineCutoff(uint alphaMode, float originalCutoff, float alpha)
        => alphaMode == 1u ? originalCutoff * alpha : originalCutoff;

    /// <summary>
    /// Scales the alpha chain by the mesh-level alpha:
    /// <c>BaseColor.W = originalAlpha * alpha</c> and <c>AlphaCutoff = originalCutoff * alpha</c>.
    ///
    /// The cutoff has to scale with the alpha rather than stay at its authored value: a MASK material at low alpha
    /// would otherwise be clipped away entirely by <c>clip(alpha - alphaCutoff)</c>.
    /// </summary>
    internal static void ApplyAlpha<TMat>(ref TMat m, float alpha, float originalAlpha, float originalCutoff)
        where TMat : unmanaged, IMaterialFields
    {
        var baseColor = m.BaseColor;
        baseColor.W = CombineAlpha(originalAlpha, alpha);
        m.BaseColor = baseColor;
        m.AlphaCutoff = originalCutoff * alpha;
    }

    /// <summary>
    /// Applies the mesh-level color multiplier: <c>BaseColor.rgb = originalBaseColor.rgb * tint.rgb</c>.
    ///
    /// W is left alone on purpose. The alpha chain belongs to <see cref="ApplyAlpha"/> alone, so a tint change can
    /// never resurrect the alpha of a different model that shares this primitive.
    /// </summary>
    internal static void ApplyTint<TMat>(ref TMat m, Vector4 tint, Vector4 originalBaseColor)
        where TMat : unmanaged, IMaterialFields
    {
        var baseColor = m.BaseColor;
        baseColor.X = originalBaseColor.X * tint.X;
        baseColor.Y = originalBaseColor.Y * tint.Y;
        baseColor.Z = originalBaseColor.Z * tint.Z;
        m.BaseColor = baseColor;
    }
}
