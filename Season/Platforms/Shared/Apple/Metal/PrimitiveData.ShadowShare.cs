// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Platforms.Shared.Apple.Metal;

// The shadow-pass material-sharing rule itself lives in Season.Rendering.Drawables.ShadowMaterialSharing,
// together with the explanation of which shader fields it keys off. Everything the shared predicate needs to
// know about a Metal primitive is here, implemented explicitly so no member is added to the class's public surface.
internal sealed partial class PrimitiveData : IShadowMaterialKey
{
    bool IShadowMaterialKey.IsTransparent => IsTransparent;

    bool IShadowMaterialKey.HasMorphTargets => MorphTargets is { Count: > 0 };

    uint IShadowMaterialKey.RenderMode => MaterialParams.RenderMode;

    uint IShadowMaterialKey.IsInstanced => MaterialParams.IsInstanced;

    uint IShadowMaterialKey.IsSkinned => MaterialParams.IsSkinned;

    uint IShadowMaterialKey.BonePaletteStride => MaterialParams.BonePaletteStride;
}
