// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Platforms.Shared.Apple.Metal;

/// <summary>
/// <see cref="IMaterialFields"/> mapping for the Metal material block. Every member forwards to the field of the
/// same name, so the MSL-driven <c>FieldOffset</c> table (MorphWeights at 112, PrevMorphWeights at 128, VertexWave at
/// 144) is untouched and the layout checks keep passing.
///
/// <c>PrevMorphWeights</c> is deliberately absent: DirectX and Vulkan keep previous morph weights in a structured
/// buffer rather than the constant block, so it stays a Metal-only field written by the Metal morph path.
///
/// The getters are declared <c>readonly</c> on purpose: without it, reading a member through the interface makes the
/// compiler copy the whole struct (defensive copy), and these reads sit inside per-primitive, per-frame loops.
/// </summary>
internal partial struct MaterialParams : IMaterialFields
{
    Vector4 IMaterialFields.BaseColor { readonly get => BaseColor; set => BaseColor = value; }

    Vector4 IMaterialFields.EmissiveFactor { readonly get => EmissiveFactor; set => EmissiveFactor = value; }

    float IMaterialFields.MetallicFactor { readonly get => MetallicFactor; set => MetallicFactor = value; }

    float IMaterialFields.RoughnessFactor { readonly get => RoughnessFactor; set => RoughnessFactor = value; }

    uint IMaterialFields.UseAlbedoMap { readonly get => UseAlbedoMap; set => UseAlbedoMap = value; }

    uint IMaterialFields.UseNormalMap { readonly get => UseNormalMap; set => UseNormalMap = value; }

    uint IMaterialFields.UseMetallicRoughnessMap { readonly get => UseMetallicRoughnessMap; set => UseMetallicRoughnessMap = value; }

    uint IMaterialFields.UseOcclusionMap { readonly get => UseOcclusionMap; set => UseOcclusionMap = value; }

    uint IMaterialFields.UseEmissiveMap { readonly get => UseEmissiveMap; set => UseEmissiveMap = value; }

    float IMaterialFields.AlphaCutoff { readonly get => AlphaCutoff; set => AlphaCutoff = value; }

    uint IMaterialFields.AlphaMode { readonly get => AlphaMode; set => AlphaMode = value; }

    uint IMaterialFields.RenderMode { readonly get => RenderMode; set => RenderMode = value; }

    uint IMaterialFields.TintMode { readonly get => TintMode; set => TintMode = value; }

    uint IMaterialFields.IsInstanced { readonly get => IsInstanced; set => IsInstanced = value; }

    uint IMaterialFields.IsSkinned { readonly get => IsSkinned; set => IsSkinned = value; }

    uint IMaterialFields.BonePaletteStride { readonly get => BonePaletteStride; set => BonePaletteStride = value; }

    uint IMaterialFields.HasMorphTargets { readonly get => HasMorphTargets; set => HasMorphTargets = value; }

    uint IMaterialFields.MorphTargetCount { readonly get => MorphTargetCount; set => MorphTargetCount = value; }

    uint IMaterialFields.MorphVertexCount { readonly get => MorphVertexCount; set => MorphVertexCount = value; }

    Vector4 IMaterialFields.MorphWeights { readonly get => MorphWeights; set => MorphWeights = value; }

    uint IMaterialFields.HasPrevBones { readonly get => HasPrevBones; set => HasPrevBones = value; }

    uint IMaterialFields.HasPrevInstanceWorld { readonly get => HasPrevInstanceWorld; set => HasPrevInstanceWorld = value; }

    uint IMaterialFields.HasPrevMorph { readonly get => HasPrevMorph; set => HasPrevMorph = value; }

    Vector4 IMaterialFields.VertexWave { readonly get => VertexWave; set => VertexWave = value; }
}
