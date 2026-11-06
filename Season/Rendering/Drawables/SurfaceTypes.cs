// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Drawables;

/// <summary>
/// PBR texture-slot indices. This is the one canonical copy for all backends; each backend used to declare an
/// identical enum next to its own <c>MaterialParams</c>. The numeric values are part of the shared contract:
/// the shader registers (t0-t4 on DX, set=0 binding=3..7 on VK, texture(0..4) on Metal) are indexed by them, so
/// the ordering must never change.
/// </summary>
internal enum TextureSlot
{
    BaseColor = 0,
    Normal = 1,
    MetallicRoughness = 2,
    Occlusion = 3,
    Emissive = 4
}

/// <summary>
/// The blend family a primitive is drawn in, independent of any backend's pipeline objects.
/// The three backends already agree on the semantics but not on the enumeration order
/// (DX/VK/Metal all declare <c>PipelineMode { Opaque, Transparent, Fade }</c>), so anything that maps between a
/// backend mode and this type must use an explicit switch rather than a cast.
/// </summary>
internal enum SurfaceMode : byte
{
    Opaque = 0,
    Fade = 1,
    Transparent = 2
}

/// <summary>Face-culling selection, in the order used by the draw-order tables: none, back, front.</summary>
internal enum SurfaceCull : byte
{
    None = 0,
    Back = 1,
    Front = 2
}

/// <summary>
/// The complete per-draw surface state: everything that has to be re-bound when two consecutive primitives
/// differ. On DX and VK these are PSO-level choices; on Metal the cull mode lives in encoder state and is
/// switched separately, but the tuple is the same.
/// </summary>
internal readonly record struct SurfaceState(SurfaceMode Mode, SurfaceCull Cull, bool DepthWrite);

/// <summary>
/// How the transparent bucket is ordered. The two backends differ here for a reason and the difference is not
/// a bug to be unified: DX splits single-sided from double-sided and sorts by depth outside recording, while
/// VK and Metal interleave in submission order and draw each double-sided primitive twice, front then back.
/// </summary>
internal enum TransparentOrder : byte
{
    /// <summary>Submission order; double-sided primitives are drawn Front then Back.</summary>
    Submission = 0,

    /// <summary>Split tables, depth-sorted when not recording.</summary>
    SortedSplit = 1
}
