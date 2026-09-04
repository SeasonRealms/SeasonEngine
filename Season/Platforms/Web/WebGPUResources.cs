// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Platforms.Web;

internal class WGPUTexture
{
    public string Name { get; set; }
    public uint Width { get; set; }
    public uint Height { get; set; }

    int _refCount;
    public int RefCount => _refCount;

    public void AddRef() => Interlocked.Increment(ref _refCount);

    /// <summary>
    /// Releases one reference. When the reference count reaches zero, the texture is removed from the global cache.
    /// Web GPU resources are managed by the JS-side seasonWebGPU layer; this method only clears C#-side metadata.
    /// </summary>
    public void Release()
    {
        if (Interlocked.Decrement(ref _refCount) == 0)
        {
            // The texture is no longer referenced by any Sprite2D; the caller is responsible for removing it from DictionaryWGPUTexture.
        }
    }

    internal static WGPUTexture CreateFromPixels(string name, uint w, uint h)
        => new() { Name = name, Width = w, Height = h };
}

internal class WGPUSprite2D : ITextureHolder
{
    public WGPUTexture WGPUTexture { get; set; }

    public Controls.Texture Texture { get; set; } = new();

    // Cached values gated by Changed: precomputed in UpdateSprite2D and consumed directly by DrawSprite2D.
    public float CachedNdcX, CachedNdcY, CachedNdcW, CachedNdcH;
    public float CachedAlpha;
    public Vector4 CachedColor;
    public bool CachedFlipX, CachedFlipY;
    public bool TransformCached;

    // Cache for Clock rotation and Source partial draws (aligned with DX/VK/MTL TextCoords.GetTransforms semantics).
    public int CachedClock;
    public float CachedSourceX, CachedSourceY, CachedSourceWidth, CachedSourceHeight;

    public WGPUSprite2D(WGPUTexture texture)
    {
        WGPUTexture = texture;
    }
}


internal class WGPUGLTFNode : GltfNodeBase
{
    public List<WGPUPrimitiveData> Primitives = new();
}

internal class WGPUPrimitiveData
{
    public Vertex[]? BaseVertices;
    public List<GLTFMorphTarget>? MorphTargets;
    public byte[] MorphDeltasBytes = Array.Empty<byte>();
    public uint MorphTargetCount;
    public uint MorphVertexCount;
    public GltfNodeBase? OwnerNode;
    public uint LastAppliedWeightsVersion;
    public Vector3 LocalBoundsCenter;
    /// <summary>1-3: Primitive-local AABB half extents (paired with LocalBoundsCenter for primitive-level and instance-level culling).</summary>
    public Vector3 LocalBoundsExtents;
    public float[] VertexData;
    public byte[] VertexBytes;
    public uint[] IndexData;
    public byte[] IndexBytes;
    public int VertexStrideFloats = 20;
    public bool HasSkinning;
    public bool Use32BitIndices;

    public Vector4 SourceBaseColor = Vector4.One;
    public Vector4 BaseColor;
    public float OriginalBaseColorAlpha;
    public float AlphaCutoff;
    public uint AlphaMode;
    public bool IsTransparent;
    public uint RenderMode;
    public bool DoubleSided;

    public float MetallicFactor = 1f;
    public float RoughnessFactor = 1f;
    public Vector3 EmissiveFactor = Vector3.Zero;

    public string BaseColorTextureName;
    public string NormalTextureName;
    public string MetallicRoughnessTextureName;
    public string OcclusionTextureName;
    public string EmissiveTextureName;

    public SharpGLTF.Schema2.Image? BaseColorTexture;
    public SharpGLTF.Schema2.Image? NormalTexture;
    public SharpGLTF.Schema2.Image? MetallicRoughnessTexture;
    public SharpGLTF.Schema2.Image? OcclusionTexture;
    public SharpGLTF.Schema2.Image? EmissiveTexture;

    public Matrix4x4 World = Matrix4x4.Identity;
    public Matrix4x4 View = Matrix4x4.Identity;
    public Matrix4x4 Projection = Matrix4x4.Identity;

    /// <summary>
    /// 2-3 contract clause 6: CPU shadow copy of the previous frame world matrix (not transposed, semantically identical to
    /// MaterialParams.PrevWorldMatrix on DX/VK/Metal). The uniform buffer is a scratch array overwritten in place every frame,
    /// so previous data must never be read back from it and has to be provided by this field instead. Advanced once per frame by
    /// WGPUModel.ApplyUserTransformToNodeTree. All zeros means no history yet (first frame), so the shader falls back to the
    /// current-frame world matrix and produces zero velocity.
    /// </summary>
    public Matrix4x4 PrevWorldMatrix;

    /// <summary>
    /// 2-3 Step C: CPU shadow copy of morph weights (the first four weights, matching uniform floats 80-83 and the native
    /// MaterialParams.PrevMorphWeights). <c>MorphWeights</c> stores the current frame, and <c>PrevMorphWeights</c> stores the
    /// previous frame. Both are advanced at the same point and in the same order as PrevWorldMatrix by
    /// WGPUModel.ApplyUserTransformToNodeTree. Because all zeros is a valid weight set, history readiness cannot be detected by
    /// a sentinel; the <c>hasPrevMorph</c> bit is controlled by WGPUModel.PrevDeformReady instead (after two consecutive frames).
    /// </summary>
    public Vector4 MorphWeights;
    public Vector4 PrevMorphWeights;

    public float CurrentAlpha = 1f;
    public float CurrentAlphaCutoff = 0.5f;

    public string CacheKey;
    public bool Uploaded;
    public bool UploadQueued;
    public bool GeometryDirty;
    public string LastTextureName;
    public string LastNormalTextureName;
    public string LastMRTextureName;
    public string LastAOTextureName;
    public string LastEmissiveTextureName;
}

sealed class PendingStaticMeshUpload
{
    public string OwnerName;
    public WGPUPrimitiveData Primitive;
    public string TextureName;
    public string NormalTextureName;
    public string MRTextureName;
    public string AOTextureName;
    public string EmissiveTextureName;
}

/// <summary>
/// Unified highlight: Web-side bounds box (faces + edges, uploaded with separate cache keys and drawn in batches).
/// Box geometry is a unit cube in the range [-0.5, 0.5]^3 (reusing shared HighlightGeometry). The world matrix is
/// Scale(Extents x 2) x Translate(Center) after the caller filters degenerate boxes. Faces are rendered as translucent BLEND
/// (alphaMode 2, drawn only when FaceAlpha &gt; 0; FaceAlpha = 0 falls back to edge-only mode). Edges are rendered as OPAQUE with
/// depth writes enabled (alphaMode 0, always solid, and EdgeColor does not pulse with face alpha). PrevWorld is a CPU shadow
/// copy (Identity on the first frame, matching the native zero-velocity sentinel). The uniform buffer is scratch memory
/// overwritten in place every frame, so previous data must never be read back from it. Cache keys are unique per owner and
/// instance slot. Geometry bytes are built lazily and then kept resident. Uploads go through WebGPUInterop.UploadStaticMesh
/// (20-float vertex layout, all five textures bound as "White", and Unlit does not sample them).
/// </summary>
internal sealed class WebBoundsBox
{
    public string FaceCacheKey;
    public string EdgeCacheKey;
    public bool Uploaded;

    public byte[] FaceVertexBytes;
    public byte[] FaceIndexBytes;
    public byte[] EdgeVertexBytes;
    public byte[] EdgeIndexBytes;

    /// <summary>Box world matrix for the current frame (written every frame by the Update hook).</summary>
    public Matrix4x4 World = Matrix4x4.Identity;

    /// <summary>Box world matrix from the previous frame (CPU shadow copy; first-frame Identity acts as the zero-velocity sentinel).</summary>
    public Matrix4x4 PrevWorld = Matrix4x4.Identity;

    /// <summary>Face alpha for the current frame (SurfaceColor.W; faces are drawn only when &gt; 0, and 0 switches to edge-only mode).</summary>
    public float FaceAlpha;

    /// <summary>Face color for the current frame, including alpha.</summary>
    public Vector4 FaceColor = new Vector4(1f, 1f, 1f, 0.3f);

    /// <summary>Edge color for the current frame (opaque solid color that does not pulse with face alpha).</summary>
    public Vector4 EdgeColor = new Vector4(1f, 0.6f, 0.1f, 1f);

    /// <summary>Builds box geometry lazily (faces: 8 vertices / 36 indices, edges: 96 vertices / 432 indices, 20-float vertex payload;
    /// geometry reuses shared HighlightGeometry and matches DX/VK/Metal bit-for-bit).</summary>
    public static WebBoundsBox Create(string cacheKeyPrefix)
    {
        var box = new WebBoundsBox
        {
            FaceCacheKey = $"HLB:{cacheKeyPrefix}:FACE",
            EdgeCacheKey = $"HLB:{cacheKeyPrefix}:EDGE",
        };

        var faceVertices = HighlightGeometry.BuildBoxFaceVertices();
        box.FaceVertexBytes = Graphics.ToByteArray(BuildVertexPayload(faceVertices));
        // The index-buffer byte width must match the uploaded indexFormat declaration ("uint16", see EnsureBoundsBoxUploaded).
        // Box indices are always <= 95, so they are serialized as ushort values. The previous combination of a 32-bit byte stream
        // with a uint16 declaration made the JS side reinterpret the bytes as a Uint16Array, corrupting the indices
        // (symptom: translucent faces disappeared and only slanted degenerate triangles remained), matching the same root-cause class
        // as the hardcoded VK Use32BitIndices bug.
        box.FaceIndexBytes = Graphics.ToByteArray(
            Array.ConvertAll(HighlightGeometry.BuildBoxFaceIndices().ToArray(), static i => (ushort)i));

        var edgeIndices = new List<uint>(12 * 36);
        var edgeVertices = HighlightGeometry.BuildBoxEdgesVertices(edgeIndices);
        box.EdgeVertexBytes = Graphics.ToByteArray(BuildVertexPayload(edgeVertices));
        box.EdgeIndexBytes = Graphics.ToByteArray(
            Array.ConvertAll(edgeIndices.ToArray(), static i => (ushort)i));

        return box;
    }

    /// <summary>20-float vertex payload: the first 3 components store Position, and all remaining channels are zeroed
    /// (UV / normal / tangent / joints / weights). Unlit with all-white textures does not consume them; they exist only to
    /// complete the Web vertex layout.</summary>
    static float[] BuildVertexPayload(List<Vertex> vertices)
    {
        var data = new float[vertices.Count * 20];
        for (int i = 0; i < vertices.Count; i++)
        {
            int off = i * 20;
            var p = vertices[i].Position;
            data[off] = p.X;
            data[off + 1] = p.Y;
            data[off + 2] = p.Z;
        }
        return data;
    }
}

/// <summary>Unified highlight: source entry for a merged shell template (instanced owners expand all sources into the same box geometry;
/// thickness is baked per source with per-primitive node scaling, matching EnsureShellGeometry and its per-source ComputeShellThickness behavior).</summary>
internal readonly struct ShellMeshSource
{
    public readonly IReadOnlyList<Vertex> Vertices;
    public readonly uint[] Indices;
    public readonly float Thickness;

    public ShellMeshSource(IReadOnlyList<Vertex> vertices, uint[] indices, float thickness)
    {
        Vertices = vertices;
        Indices = indices;
        Thickness = thickness;
    }
}

/// <summary>Unified highlight: draw entry for a single wireframe shell instance (the Update hook captures world/history/two colors per instance,
/// and the draw tail bakes the world matrix into the shared shell box per entry so all instances reuse the same box geometry).</summary>
internal readonly struct ShellDrawEntry
{
    public readonly int WriteIndex;
    public readonly Matrix4x4 World;
    public readonly Matrix4x4 PrevWorld;
    public readonly Vector4 FaceColor;
    public readonly Vector4 EdgeColor;

    public ShellDrawEntry(int writeIndex, Matrix4x4 world, Matrix4x4 prevWorld, Vector4 faceColor, Vector4 edgeColor)
    {
        WriteIndex = writeIndex;
        World = world;
        PrevWorld = prevWorld;
        FaceColor = faceColor;
        EdgeColor = edgeColor;
    }
}

/// <summary>
/// Unified highlight: Web-side wireframe shell (faces + edges, uploaded with separate cache keys and drawn in batches).
/// Shell geometry is expanded from source primitives through shared HighlightGeometry.AppendShellFace / AppendShellEdges
/// (vertices are extruded along normals by h, and edge strips are flattened quads that hug the source edges, matching DX/VK/Metal bit-for-bit).
/// Morph sources carry side-by-side deltas expanded to the shell vertex layout (the shell-vertex to source-vertex mapping is recorded while building).
/// Skinned sources carry a skinned flag as well (shell vertices copy joints and weights wholesale, so animation stays aligned through the same bone channel).
/// Owners such as Mesh3D/Model use one box per primitive (see <see cref="Create"/>). Instanced owners use a single merged template box
/// for all sources (see <see cref="CreateMerged"/>), reusing the same geometry per instance while baking the world matrix into the uniform buffer.
/// Faces are rendered as translucent BLEND (alphaMode 2, drawn only when FaceAlpha &gt; 0; FaceAlpha = 0 falls back to edge-only mode).
/// Edges are rendered as OPAQUE with depth writes enabled (alphaMode 0, always solid, and EdgeColor does not pulse with face alpha).
/// PrevWorld is a CPU shadow copy (Identity on the first frame, matching the native zero-velocity sentinel). The uniform buffer is scratch memory
/// overwritten in place every frame, so previous data must never be read back from it. Cache keys are unique per owner, primitive, and slot.
/// Geometry bytes are built lazily and then kept resident. Uploads go through WebGPUInterop.UploadStaticMesh
/// (20-float vertex layout, all five textures bound as "White", and Unlit does not sample them).
/// </summary>
internal sealed class WebShellBox
{
    public string FaceCacheKey;
    public string EdgeCacheKey;
    public bool Uploaded;

    public byte[] FaceVertexBytes = Array.Empty<byte>();
    public byte[] FaceIndexBytes = Array.Empty<byte>();
    public byte[] EdgeVertexBytes = Array.Empty<byte>();
    public byte[] EdgeIndexBytes = Array.Empty<byte>();

    /// <summary>Morph deltas expanded to the shell vertex layout (empty arrays when there is no morph source; face and edge paths keep independent counts because their vertex counts differ).</summary>
    public byte[] FaceMorphDeltaBytes = Array.Empty<byte>();
    public byte[] EdgeMorphDeltaBytes = Array.Empty<byte>();
    public uint MorphTargetCount;
    public uint FaceMorphVertexCount;
    public uint EdgeMorphVertexCount;
    public bool HasSkinning;
    /// <summary>Face and edge index widths are determined independently (they use separate cache keys and separate uploads).
    /// The serialized byte width must match the declared indexFormat, otherwise the JS side reinterprets the byte stream using the declared width,
    /// corrupting indices and making the shell disappear. This is the same root-cause class as the hardcoded VK InitShellPrimitive issue and follows
    /// the same coupled pattern used by regular WGPUModel primitives.</summary>
    public bool Use32BitFaceIndices;
    public bool Use32BitEdgeIndices;

    /// <summary>Shell world matrix for the current frame (written every frame by the Update hook).</summary>
    public Matrix4x4 World = Matrix4x4.Identity;

    /// <summary>Shell world matrix from the previous frame (CPU shadow copy; first-frame Identity acts as the zero-velocity sentinel).</summary>
    public Matrix4x4 PrevWorld = Matrix4x4.Identity;

    /// <summary>Face alpha for the current frame (SurfaceColor.W; faces are drawn only when &gt; 0, and 0 switches to edge-only mode).</summary>
    public float FaceAlpha;

    /// <summary>Face color for the current frame, including alpha.</summary>
    public Vector4 FaceColor = new Vector4(1f, 1f, 1f, 0.3f);

    /// <summary>Edge color for the current frame (opaque solid color that does not pulse with face alpha).</summary>
    public Vector4 EdgeColor = new Vector4(1f, 0.6f, 0.1f, 1f);

    /// <summary>Builds a single-source shell box lazily for the per-primitive owner path. Source triangles are copied as-is, vertices are extruded
    /// along normals by h to form shell faces, and deduplicated source edges become flattened quad strips for shell edges
    /// (through shared HighlightGeometry, matching DX/VK/Metal bit-for-bit). Morph sources (when both morphBaseVertices and morphTargets are non-null)
    /// are packaged together with deltas expanded to the shell vertex layout. Skinned sources carry a skinned flag as well, because shell vertices
    /// include joints and weights and must render through the skinning path.</summary>
    public static WebShellBox Create(string cacheKeyPrefix,
        IReadOnlyList<Vertex> sourceVertices, uint[] sourceIndices, float thickness,
        Vertex[]? morphBaseVertices = null, List<GLTFMorphTarget>? morphTargets = null, bool hasSkinning = false)
    {
        var box = new WebShellBox
        {
            FaceCacheKey = $"HLS:{cacheKeyPrefix}:FACE",
            EdgeCacheKey = $"HLS:{cacheKeyPrefix}:EDGE",
        };

        var faceVertices = new List<Vertex>();
        var faceIndices = new List<uint>();
        var faceSrcMap = morphTargets != null ? new List<int>() : null;
        HighlightGeometry.AppendShellFace(faceVertices, faceIndices, sourceVertices, sourceIndices, thickness, faceSrcMap);

        var edgeVertices = new List<Vertex>();
        var edgeIndices = new List<uint>();
        var edgeSrcMap = morphTargets != null ? new List<int>() : null;
        HighlightGeometry.AppendShellEdges(edgeVertices, edgeIndices, sourceVertices, sourceIndices, thickness, edgeSrcMap);

        FinalizePayload(box, faceVertices, faceIndices, faceSrcMap, edgeVertices, edgeIndices, edgeSrcMap,
            morphBaseVertices, morphTargets, hasSkinning);
        return box;
    }

    /// <summary>Builds an instanced merged shell template lazily. Shell faces and shell edge strips from all source primitives are merged into a
    /// single box, and each instance reuses the same geometry while its world matrix is baked into the uniform buffer at draw time,
    /// matching the semantics of native <c>_shellGeometry</c>. When <c>hasSkinning</c> is true, shell vertex payloads carry joints and weights
    /// alongside them (the sources already include skinning data after WGPUModel.ReconstructVertices), so drawing goes through the instanced
    /// skinning path. Returns null when every source is degenerate.</summary>
    public static WebShellBox? CreateMerged(string cacheKeyPrefix, IReadOnlyList<ShellMeshSource> sources, bool hasSkinning = false)
    {
        var box = new WebShellBox
        {
            FaceCacheKey = $"HLS:{cacheKeyPrefix}:FACE",
            EdgeCacheKey = $"HLS:{cacheKeyPrefix}:EDGE",
        };

        var faceVertices = new List<Vertex>();
        var faceIndices = new List<uint>();
        var edgeVertices = new List<Vertex>();
        var edgeIndices = new List<uint>();
        for (int i = 0; i < sources.Count; i++)
        {
            var src = sources[i];
            if (src.Vertices == null || src.Indices == null || src.Vertices.Count == 0 || src.Indices.Length < 3)
                continue;
            HighlightGeometry.AppendShellFace(faceVertices, faceIndices, src.Vertices, src.Indices, src.Thickness);
            HighlightGeometry.AppendShellEdges(edgeVertices, edgeIndices, src.Vertices, src.Indices, src.Thickness);
        }
        if (faceIndices.Count == 0 && edgeIndices.Count == 0)
            return null;

        FinalizePayload(box, faceVertices, faceIndices, null, edgeVertices, edgeIndices, null, null, null, hasSkinning);
        return box;
    }

    static void FinalizePayload(WebShellBox box,
        List<Vertex> faceVertices, List<uint> faceIndices, List<int>? faceSrcMap,
        List<Vertex> edgeVertices, List<uint> edgeIndices, List<int>? edgeSrcMap,
        Vertex[]? morphBaseVertices, List<GLTFMorphTarget>? morphTargets, bool hasSkinning)
    {
        bool face32 = faceIndices.Any(static i => i > ushort.MaxValue);
        bool edge32 = edgeIndices.Any(static i => i > ushort.MaxValue);
        box.FaceVertexBytes = Graphics.ToByteArray(BuildVertexPayload(faceVertices));
        box.FaceIndexBytes = face32
            ? Graphics.ToByteArray(faceIndices.ToArray())
            : Graphics.ToByteArray(Array.ConvertAll(faceIndices.ToArray(), static i => (ushort)i));
        box.EdgeVertexBytes = Graphics.ToByteArray(BuildVertexPayload(edgeVertices));
        box.EdgeIndexBytes = edge32
            ? Graphics.ToByteArray(edgeIndices.ToArray())
            : Graphics.ToByteArray(Array.ConvertAll(edgeIndices.ToArray(), static i => (ushort)i));
        box.Use32BitFaceIndices = face32;
        box.Use32BitEdgeIndices = edge32;
        box.HasSkinning = hasSkinning;
        if (morphTargets != null)
        {
            box.MorphTargetCount = (uint)Math.Min(morphTargets.Count, 4);
            box.FaceMorphDeltaBytes = Graphics.ToByteArray(BuildMorphDeltaData(morphBaseVertices!, morphTargets, faceSrcMap));
            box.FaceMorphVertexCount = (uint)faceVertices.Count;
            box.EdgeMorphDeltaBytes = Graphics.ToByteArray(BuildMorphDeltaData(morphBaseVertices!, morphTargets, edgeSrcMap));
            box.EdgeMorphVertexCount = (uint)edgeVertices.Count;
        }
    }

    /// <summary>Morph deltas are expanded to the shell vertex layout: delta for shell vertex v = source delta[vertexMap[v]],
    /// and the vertex count equals vertexMap.Count. This follows the shared-layer contract of whole-vertex copies plus source-index mapping
    /// described by AppendShellFace and AppendShellEdges.</summary>
    static float[] BuildMorphDeltaData(Vertex[] baseVertices, List<GLTFMorphTarget> morphTargets, List<int>? vertexMap)
    {
        int targetCount = Math.Min(morphTargets.Count, 4);
        int vertexCount = vertexMap?.Count ?? baseVertices.Length;
        var deltaData = new float[targetCount * vertexCount * 9];

        for (int t = 0; t < targetCount; t++)
        {
            var target = morphTargets[t];
            for (int v = 0; v < vertexCount; v++)
            {
                int srcIdx = vertexMap != null ? vertexMap[v] : v;
                int baseIdx = (t * vertexCount + v) * 9;
                if (srcIdx < target.PositionDeltas.Length)
                {
                    deltaData[baseIdx] = target.PositionDeltas[srcIdx].X;
                    deltaData[baseIdx + 1] = target.PositionDeltas[srcIdx].Y;
                    deltaData[baseIdx + 2] = target.PositionDeltas[srcIdx].Z;
                }
                if (srcIdx < target.NormalDeltas.Length)
                {
                    deltaData[baseIdx + 3] = target.NormalDeltas[srcIdx].X;
                    deltaData[baseIdx + 4] = target.NormalDeltas[srcIdx].Y;
                    deltaData[baseIdx + 5] = target.NormalDeltas[srcIdx].Z;
                }
                if (srcIdx < target.TangentDeltas.Length)
                {
                    deltaData[baseIdx + 6] = target.TangentDeltas[srcIdx].X;
                    deltaData[baseIdx + 7] = target.TangentDeltas[srcIdx].Y;
                    deltaData[baseIdx + 8] = target.TangentDeltas[srcIdx].Z;
                }
            }
        }

        return deltaData;
    }

    /// <summary>20-float vertex payload: writes shell vertices field by field (extruded Position plus carried-through normal, tangent,
    /// joints, and weights). Skinned shells consume joints and weights through the same VS skinning path, matching the shared-layer
    /// "whole vertex copy" contract. The Unlit path itself consumes only Position; the remaining channels exist to keep the skinning
    /// and morph paths complete.</summary>
    static float[] BuildVertexPayload(List<Vertex> vertices)
    {
        var data = new float[vertices.Count * 20];
        for (int i = 0; i < vertices.Count; i++)
        {
            int off = i * 20;
            var v = vertices[i];
            data[off] = v.Position.X;
            data[off + 1] = v.Position.Y;
            data[off + 2] = v.Position.Z;
            data[off + 3] = v.TexCoord.X;
            data[off + 4] = v.TexCoord.Y;
            data[off + 5] = v.Normal.X;
            data[off + 6] = v.Normal.Y;
            data[off + 7] = v.Normal.Z;
            data[off + 8] = v.Tangent.X;
            data[off + 9] = v.Tangent.Y;
            data[off + 10] = v.Tangent.Z;
            data[off + 11] = v.Tangent.W;
            data[off + 12] = v.Joints.X;
            data[off + 13] = v.Joints.Y;
            data[off + 14] = v.Joints.Z;
            data[off + 15] = v.Joints.W;
            data[off + 16] = v.Weights.X;
            data[off + 17] = v.Weights.Y;
            data[off + 18] = v.Weights.Z;
            data[off + 19] = v.Weights.W;
        }
        return data;
    }
}
