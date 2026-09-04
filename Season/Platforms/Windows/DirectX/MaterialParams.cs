// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Direct3D12;

namespace Season.Platforms.Windows.DirectX;

/// <summary>PBR texture-slot indices aligned with shader registers t0-t4.</summary>
internal enum TextureSlot
{
    BaseColor = 0,
    Normal = 1,
    MetallicRoughness = 2,
    Occlusion = 3,
    Emissive = 4
}

[StructLayout(LayoutKind.Explicit, Size = 128)]
internal struct MaterialParams
{
    [FieldOffset(0)] public Vector4 BaseColor;
    [FieldOffset(16)] public Vector4 EmissiveFactor;
    [FieldOffset(32)] public float MetallicFactor;
    [FieldOffset(36)] public float RoughnessFactor;
    [FieldOffset(40)] public uint UseAlbedoMap;
    [FieldOffset(44)] public uint UseNormalMap;
    [FieldOffset(48)] public uint UseMetallicRoughnessMap;
    [FieldOffset(52)] public uint UseOcclusionMap;
    [FieldOffset(56)] public uint UseEmissiveMap;
    [FieldOffset(60)] public float AlphaCutoff;
    [FieldOffset(64)] public uint AlphaMode;
    [FieldOffset(68)] public uint RenderMode;     // 0=Sprite2D, 1=Pbr3D, 2=TextMsdf (reserved)
    [FieldOffset(72)] public uint BonePaletteStride; // Instanced skinning: stride of the per-instance bone palette
    [FieldOffset(72)] public float Padding1;         // Backward compatibility: legacy Sprite2D/Text paths still use this slot as a generic float
    [FieldOffset(76)] public uint IsInstanced;  // 0=regular draw (uses b0 world matrix), 1=GPU instancing (uses per-instance matrix)
    // Phase 2: skeletal skinning
    [FieldOffset(80)] public uint IsSkinned;       // 0=static, 1=skinned, so shaders can skip unrelated paths
    // Phase 3: Morph Target
    [FieldOffset(84)] public uint HasMorphTargets;  // 0=no, 1=has morph-target delta data
    [FieldOffset(88)] public uint MorphTargetCount; // Number of active morph targets
    [FieldOffset(92)] public uint MorphVertexCount; // Total vertex count used for structured-buffer stride calculation
    [FieldOffset(96)] public Vector4 MorphWeights;  // Up to 4 morph-target weights
    // 2-3 Step C: previous-frame-data validity sentinels.
    // 0 means the corresponding prev SB is not ready or not applicable, so the
    // shader falls back to current-frame data.
    // The default is 0, which naturally handles the first frame and paths with
    // no prev SB, matching behavior before Step C.
    [FieldOffset(112)] public uint HasPrevBones;          // 0/1, whether the previous bone SB is valid
    [FieldOffset(116)] public uint HasPrevInstanceWorld;  // 0/1, whether the previous instanceWorld SB is valid
    [FieldOffset(120)] public uint HasPrevMorph;          // 0/1, whether the previous morphWeights SB is valid
    [FieldOffset(124)] public uint _Padding2;             // 16-byte alignment padding
}

internal unsafe class PrimitiveData
{
    public int InstanceStreamIndex = -1;
    public List<Vertex> Vertices;
    public Vertex[]? BaseVertices;
    public List<GLTFMorphTarget>? MorphTargets;
    public GltfNodeBase? OwnerNode;
    public uint LastAppliedWeightsVersion;
    public uint[] Indices;
    public bool Use32BitIndices;
    public bool DoubleSided;

    /// <summary>2-2 contract rule 7: GTAO exemption, synced from
    /// Mesh3D.ExcludeFromAo. Draw buckets use this to select the NoDepth PSO.</summary>
    public bool AoExempt;
    public Vector3 LocalBoundsCenter;
    /// <summary>1-3: half extents of the primitive-local AABB, paired with
    /// LocalBoundsCenter for primitive-level and instance-level culling.</summary>
    public Vector3 LocalBoundsExtents;

    public ID3D12Resource* VertexBuffer;
    public VertexBufferView VertexBufferView;
    public ID3D12Resource* IndexBuffer;
    public IndexBufferView IndexBufferView;

    public ID3D12Resource*[] MaterialBuffers;
    public byte*[] MappedMaterialBuffers;

    public ID3D12Resource*[] MatrixBuffers;
    public byte*[] MappedMatrixBuffers;

    /// <summary>
    /// 2-3 contract rule 6: CPU shadow copy of the previous frame's world
    /// matrix, stored without transpose.
    /// MatrixBuffers are N-buffered upload-heap resources, so previous frames
    /// must never be read back from them. History must come from this field.
    /// All-zero means no history yet, which is the first-frame case, and the
    /// shader falls back to the current world matrix.
    /// </summary>
    public System.Numerics.Matrix4x4 PrevWorldMatrix;

    public DXTexture BaseColorTexture;
    public DXTexture NormalTexture;
    public DXTexture MetallicRoughnessTexture;
    public DXTexture OcclusionTexture;
    public DXTexture EmissiveTexture;

    public MaterialParams MaterialParams;

    /// <summary>Original glTF material BaseColor.W (alpha), multiplied with
    /// Model.Alpha to produce the final opacity multiplier.</summary>
    public float OriginalBaseColorAlpha = 1.0f;

    /// <summary>Original Surface BaseColor frozen at load time, used as the
    /// multiplicative baseline for SyncColorTint. RGB is tinted while W stays untouched.</summary>
    public Vector4 OriginalBaseColor = Vector4.One;
    
    /// <summary>
    /// Original glTF material AlphaCutoff. When Model.Alpha < 1, this is scaled
    /// proportionally by alpha to avoid clipping away the entire MASK material
    /// through clip(alpha - alphaCutoff).
    /// </summary>
    public float OriginalAlphaCutoff = 0.5f;

    // Phase 3: Morph-target GPU data
    public ID3D12Resource* MorphDeltasBuffer;         // StructuredBuffer resource
    public GpuDescriptorHandle MorphDeltasSrvHandle;  // t5 SRV descriptor handle
    public int MorphDescriptorId = -1;                // Descriptor-heap ID used for release

    /// <summary>
    /// Whether the primitive is transparent, determined from the glTF material's AlphaMode.
    /// BLEND and MASK are treated as transparent, while OPAQUE is not.
    /// </summary>
    public bool IsTransparent;

    public void Dispose()
    {
        if (VertexBuffer != null) VertexBuffer->Release();
        if (IndexBuffer != null) IndexBuffer->Release();
        if (MaterialBuffers != null)
        {
            for (int i = 0; i < MaterialBuffers.Length; i++)
            {
                if (MaterialBuffers[i] != null)
                {
                    MaterialBuffers[i]->Unmap(0, null);
                    MaterialBuffers[i]->Release();
                }
            }
            MaterialBuffers = null;
            MappedMaterialBuffers = null;
        }
        if (MatrixBuffers != null)
        {
            for (int i = 0; i < MatrixBuffers.Length; i++)
            {
                if (MatrixBuffers[i] != null)
                {
                    MatrixBuffers[i]->Unmap(0, null);
                    MatrixBuffers[i]->Release();
                }
            }
            MatrixBuffers = null;
            MappedMatrixBuffers = null;
        }
        // Phase 3: release the morph-delta buffer only when it is owned here
        if (MorphDeltasBuffer != null && MorphDescriptorId >= 0)
        {
            MorphDeltasBuffer->Release();
            MorphDeltasBuffer = null;
            Device.DescriptorAllocator.Free(MorphDescriptorId);
            MorphDescriptorId = -1;
        }
    }
}
