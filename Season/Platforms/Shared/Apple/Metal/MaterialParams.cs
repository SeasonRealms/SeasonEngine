// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Metal;

namespace Season.Platforms.Shared.Apple.Metal;

/// <summary>PBR texture-slot indices aligned with the shader binding numbers at texture(0..4).</summary>
internal enum TextureSlot
{
    BaseColor = 0,
    Normal = 1,
    MetallicRoughness = 2,
    Occlusion = 3,
    Emissive = 4
}

/// <summary>
/// Material constant buffer for fragment buffer slot 2, matching the MaterialParams byte layout in Pipeline.MetalShaderSource exactly.
/// It is fully consistent with the DX and Vulkan MaterialParams definitions and shares the same cross-platform semantics.
/// Within this 80-byte region, the MSL constant-buffer layout for scalars and vectors matches std140 and cbuffer semantics.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 144)]
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
    [FieldOffset(68)] public uint RenderMode;     // 0=Sprite2D, 1=Pbr3D, 2=TextMsdf, 3=ProceduralSky
    [FieldOffset(72)] public float Padding1;         // Reused by the text and MSDF path.
    [FieldOffset(76)] public uint IsInstanced;       // 0 = regular draw, 1 = GPU instancing.
    [FieldOffset(80)] public uint IsSkinned;         // 0 = static, 1 = skeletal skinning.
    [FieldOffset(84)] public uint BonePaletteStride; // Instanced skinning: per-instance bone-palette stride.
    [FieldOffset(88)] public uint HasMorphTargets;   // 0 = none, 1 = morph-target delta data is present.
    [FieldOffset(92)] public uint MorphTargetCount;  // Number of active morph targets.
    [FieldOffset(96)] public uint MorphVertexCount;  // Total vertex count, used for structured-buffer stride calculation.
    /// <summary>Contract clause 8(b) of 2-3: whether the previous-frame bone palette is available. Zero falls back to the current bone matrices.</summary>
    [FieldOffset(100)] public uint HasPrevBones;
    /// <summary>Contract clause 8(c) of 2-3: whether the previous-frame instance-world stream is available. Zero falls back to the current instance world.</summary>
    [FieldOffset(104)] public uint HasPrevInstanceWorld;
    /// <summary>Contract clause 8(c) of 2-3: whether previous-frame morph weights are available. Zero falls back to the current weights.</summary>
    [FieldOffset(108)] public uint HasPrevMorph;
    [FieldOffset(112)] public Vector4 MorphWeights;  // Used by regular models; instanced models use per-instance morph weights.
    /// <summary>
    /// Previous-frame morph weights for the non-instanced path.
    /// Metal does not introduce an extra structured buffer like Vulkan binding 15.
    /// Instead, the data is pushed inline with the constant buffer from a CPU shadow copy
    /// and never read back from the N-buffer ring, satisfying contract clause 6.
    /// </summary>
    [FieldOffset(128)] public Vector4 PrevMorphWeights;
}

/// <summary>
/// Metal primitive data aligned with DX12 and Vulkan PrimitiveData:
/// - vertex and index buffers
/// - N-buffered Matrix UBO plus Material UBO, synchronized with the frame ring to avoid overwriting in-flight frame data
/// - five PBR textures plus MaterialParams constants
/// Metal does not need DescriptorSet objects because each draw binds directly through SetVertexBuffer,
/// SetFragmentBuffer, and SetFragmentTexture.
/// IMTLBuffer.Contents is a persistent IntPtr, so writing directly to buffer.Contents plus offset each frame
/// is immediately visible to the GPU,
/// equivalent to persistent mapping in DX12 with N-buffered frame-index switching.
/// </summary>
internal sealed class PrimitiveData : IDisposable
{
    public int InstanceStreamIndex = -1;
    public List<Vertex> Vertices = null!;
    public Vertex[]? BaseVertices;
    public List<GLTFMorphTarget>? MorphTargets;
    public GltfNodeBase? OwnerNode;
    public uint LastAppliedWeightsVersion;

    public uint[] Indices = null!;
    public bool Use32BitIndices;
    public bool DoubleSided;
    /// <summary>Contract clause 7 of 2-2: GTAO exemption, synchronized from Mesh3D.ExcludeFromAo. The draw bucket uses it to switch to OpaqueNoDepthState.</summary>
    public bool AoExempt;
    public Vector3 LocalBoundsCenter;
    /// <summary>Local primitive AABB half-extents for render-quality 1-3, paired with LocalBoundsCenter and used by primitive-level and instance-level culling.</summary>
    public Vector3 LocalBoundsExtents;

    public IMTLBuffer VertexBuffer = null!;

    public IMTLBuffer IndexBuffer = null!;

    /// <summary>N-buffered matrices at b0 containing world, view, and projection, with length equal to Device.frameCount.</summary>
    public IMTLBuffer[] MatrixBuffers = null!;

    /// <summary>N-buffered MaterialParams at b2, with length equal to Device.frameCount.</summary>
    public IMTLBuffer[] MaterialBuffers = null!;

    public Texture BaseColorTexture = null!;

    public Texture NormalTexture = null!;

    public Texture MetallicRoughnessTexture = null!;

    public Texture OcclusionTexture = null!;

    public Texture EmissiveTexture = null!;

    public IMTLBuffer? MorphDeltasBuffer;
    public bool OwnsMorphDeltasBuffer;

    public MaterialParams MaterialParams;

    /// <summary>
    /// Contract clause 6 of 2-3: CPU shadow copy of the previous-frame world matrix, kept in non-transposed form.
    /// MatrixBuffers live in CPU-writable N-buffer heaps and must never be read back for historical frames,
    /// so history must come from this field instead.
    /// All zeros means no history exists yet, such as the first frame, and the shader falls back to the current world matrix.
    /// </summary>
    public System.Numerics.Matrix4x4 PrevWorldMatrix;

    /// <summary>The original glTF material BaseColor.W alpha, used together with Model.Alpha as the final transparency multiplier.</summary>
    public float OriginalBaseColorAlpha = 1.0f;

    /// <summary>The original Surface BaseColor captured during Load. It is the multiplicative base for SyncColorTint, where rgb is multiplied by tint and W stays unchanged.</summary>
    public Vector4 OriginalBaseColor = Vector4.One;

    /// <summary>
    /// The original glTF material AlphaCutoff.
    /// When Model.Alpha is less than 1, this value is scaled proportionally with alpha
    /// so MASK materials do not get discarded as a whole by discard(alpha - alphaCutoff) at low Model.Alpha.
    /// </summary>
    public float OriginalAlphaCutoff = 0.5f;

    /// <summary>
    /// Whether the material is transparent, derived from the glTF AlphaMode:
    /// BLEND is transparent and uses the Transparent PSO,
    /// MASK uses the Opaque PSO plus shader-side discard,
    /// and OPAQUE uses the Opaque PSO.
    /// </summary>
    public bool IsTransparent;

    public void Dispose()
    {
        VertexBuffer?.Dispose(); VertexBuffer = null!;
        IndexBuffer?.Dispose(); IndexBuffer = null!;

        if (MatrixBuffers != null)
        {
            for (int i = 0; i < MatrixBuffers.Length; i++) MatrixBuffers[i]?.Dispose();
            MatrixBuffers = null!;
        }

        if (MaterialBuffers != null)
        {
            for (int i = 0; i < MaterialBuffers.Length; i++) MaterialBuffers[i]?.Dispose();
            MaterialBuffers = null!;
        }

        if (OwnsMorphDeltasBuffer)
        {
            MorphDeltasBuffer?.Dispose();
            MorphDeltasBuffer = null;
            OwnsMorphDeltasBuffer = false;
        }
    }
}
