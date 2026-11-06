// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Drawables;

/// <summary>
/// The parts of a backend material block that every backend names and types the same way. It exists so the shared
/// code that fills in a material (alpha chains, tints, surface descriptors) can be written once and applied to any
/// backend's <c>MaterialParams</c> without reflection, boxing, or a per-backend copy.
///
/// This interface carries no layout. Each backend keeps its own <c>[StructLayout(Explicit)]</c> and
/// <c>[FieldOffset]</c> table — the three are already different (Vulkan and Metal park <c>MorphWeights</c> at 112 to
/// satisfy std140, Metal pushes <c>VertexWave</c> to 144, DirectX keeps <c>MorphWeights</c> at 96). The interface only
/// promises that these names exist and are readable/writable in place; byte offsets stay a backend concern guarded by
/// the layout checks in <c>Tests/Shared/MaterialParamsLayoutChecks.cs</c>.
///
/// Only members that all three backends actually have belong here. Metal's <c>PrevMorphWeights</c> is not in the
/// list: DirectX and Vulkan keep previous morph weights in a structured buffer instead of the constant block, so
/// giving it a member would force two backends to invent a field they do not have.
/// </summary>
internal interface IMaterialFields
{
    // PBR inputs. Use*Map are 0/1 gates read by the shader, not booleans.
    Vector4 BaseColor { get; set; }

    Vector4 EmissiveFactor { get; set; }

    float MetallicFactor { get; set; }

    float RoughnessFactor { get; set; }

    uint UseAlbedoMap { get; set; }

    uint UseNormalMap { get; set; }

    uint UseMetallicRoughnessMap { get; set; }

    uint UseOcclusionMap { get; set; }

    uint UseEmissiveMap { get; set; }

    // Alpha and shading model.
    float AlphaCutoff { get; set; }

    uint AlphaMode { get; set; }

    /// <summary>0 = Sprite2D, 1 = Pbr3D, 2 = TextMsdf (reserved). Metal additionally writes 3 for procedural sky.</summary>
    uint RenderMode { get; set; }

    /// <summary>0 = ignore the per-instance color, 1 = multiply the PBR albedo by it.</summary>
    uint TintMode { get; set; }

    // Draw-path selectors. These are the same five fields the shadow pass compares to decide whether a material can
    // be bound once for a whole group (see <see cref="ShadowMaterialSharing"/>).
    uint IsInstanced { get; set; }

    uint IsSkinned { get; set; }

    uint BonePaletteStride { get; set; }

    // Morph targets. The counts drive the structured-buffer stride, so they are written per primitive, not per group.
    uint HasMorphTargets { get; set; }

    uint MorphTargetCount { get; set; }

    uint MorphVertexCount { get; set; }

    Vector4 MorphWeights { get; set; }

    // 2-3 clause 8: previous-frame validity sentinels. Zero makes the shader fall back to current-frame data, which
    // is exactly what a first frame or a path without history needs.
    uint HasPrevBones { get; set; }

    uint HasPrevInstanceWorld { get; set; }

    uint HasPrevMorph { get; set; }

    /// <summary>
    /// Vertex swell (currently only the sea uses it): x = half peak-to-trough amplitude in meters (0 = off),
    /// y = current phase, z = previous-frame phase for the velocity path, w = fade-out radius in meters.
    /// </summary>
    Vector4 VertexWave { get; set; }
}
