// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Drawables;

/// <summary>
/// The five material facts the shadow vertex shader actually reads, exposed without assuming a backend's
/// constant-buffer layout. Each backend's <c>PrimitiveData</c> implements this explicitly, so the class keeps its
/// field names and the interface adds nothing to its public surface.
/// </summary>
internal interface IShadowMaterialKey
{
    bool IsTransparent { get; }
    bool HasMorphTargets { get; }
    uint RenderMode { get; }
    uint IsInstanced { get; }
    uint IsSkinned { get; }
    uint BonePaletteStride { get; }
}

/// <summary>
/// Decides whether every opaque primitive in a group can reuse the material buffer one primitive bound.
///
/// The shadow vertex shader reads only five fields out of the material block: renderMode, isInstanced,
/// isSkinned, bonePaletteStride, and hasMorphTargets (DX Pipeline.cs VSMain L1041/1045/1085/1087/1114/1230,
/// and the mirrored MSL entry point). Material colour, alphaMode, alphaCutoff, and the texture-enable flags are
/// all fragment-shader concerns, and this pass draws with an empty fragment shader, so none of them are read.
/// When those five fields match across the group, the block bound by the first primitive is byte-for-byte
/// equivalent for the rest, and the remaining primitives can skip the material bind. When hasMorphTargets is
/// zero, morphTargetCount, morphVertexCount, and morphWeights all become dead code and the morph buffer is never
/// sampled either, so the two bindings can be skipped together.
///
/// Primitives with morph data are always treated as non-shareable. The test keys off the <c>MorphTargets</c>
/// list fixed at load time rather than the per-frame hasMorphTargets value, so the answer does not depend on
/// whether the shadow pass runs before or after morph weights are written.
///
/// isSkinned is node-level rather than model-level (see the per-backend SyncSkinningMaterialParams), so it has to
/// be compared per primitive instead of hoisted.
///
/// Transparent primitives do not participate: the shadow pass skips them anyway, so letting them disqualify the
/// whole group would throw away the optimization. The filter here must stay identical to the draw loop's filter,
/// or the two paths end up reasoning about different primitive sets.
/// </summary>
internal static class ShadowMaterialSharing
{
    internal static bool CanShare<T>(List<T> primitives) where T : class, IShadowMaterialKey
    {
        T? first = null;
        for (int i = 0; i < primitives.Count; i++)
        {
            var p = primitives[i];
            if (p.IsTransparent)
                continue;

            if (p.HasMorphTargets)
                return false;

            if (first == null)
            {
                first = p;
                continue;
            }

            if (p.RenderMode != first.RenderMode
                || p.IsInstanced != first.IsInstanced
                || p.IsSkinned != first.IsSkinned
                || p.BonePaletteStride != first.BonePaletteStride)
                return false;
        }
        return true;
    }
}
