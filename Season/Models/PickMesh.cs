// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Models;

/// <summary>
/// Compact mesh for picking, used as the shared-layer v2 picking validation data source.
/// It keeps only the minimum subset required for ray-triangle tests,
/// namely positions and indices, with joints and weights added for skinned primitives.
/// The object is built once from a glTF primitive by <see cref="GltfAsset"/> during loading and then stays immutable.
/// Memory cost is about 16 bytes per triangle, plus another 32 bytes per vertex for skinned primitives.
/// Coordinates and indices are strictly sourced from the render pipeline by reusing
/// <see cref="GLTFTools.LoadMeshPrimitive"/>, including RH-to-LH conversion,
/// Z negation on Position, winding reversal, and weight normalization,
/// so picking matches on-screen rendering pixel for pixel.
/// Platform code shares this object by reference when cloning node trees.
/// No deep copy is needed because the object is immutable.
/// NodeIndex is filled after template loading completes, and because the clone path preserves node order,
/// the shared reference sees the same index on both sides.
/// </summary>
public sealed class PickMesh
{
    /// <summary>Vertex positions sourced from the render path, with RH-to-LH conversion already applied.</summary>
    public Vector3[] Positions = Array.Empty<Vector3>();

    /// <summary>Triangle indices stored as uint to match platform PrimitiveData conventions, with winding already flipped for LH space.</summary>
    public uint[] Indices = Array.Empty<uint>();

    /// <summary>Skin joint indices as float4 per vertex. Null for non-skinned primitives.</summary>
    public Vector4[] Joints;

    /// <summary>Skin weights as float4 per vertex. Null for non-skinned primitives.</summary>
    public Vector4[] Weights;

    /// <summary>Owning node, used to fetch node world transforms or bones during picking.</summary>
    public GltfNodeBase OwnerNode;

    /// <summary>Index of the owning node inside <c>GltfAsset.gltfNodes</c>, filled near the end of loading and used for per-instance shadow lookup.</summary>
    public int NodeIndex = -1;

    /// <summary>Whether this is a skinned primitive, meaning both Joints and Weights are loaded.</summary>
    public bool IsSkinned => Joints != null && Weights != null;
}
