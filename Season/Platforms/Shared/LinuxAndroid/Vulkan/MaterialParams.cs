// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Vulkan;

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

/// <summary>PBR texture-slot indices aligned with shader binding numbers set=0 binding=3 through 7.</summary>
internal enum TextureSlot
{
    BaseColor = 0,
    Normal = 1,
    MetallicRoughness = 2,
    Occlusion = 3,
    Emissive = 4
}

/// <summary>
/// Material constant buffer, UBO b2, aligned one to one with the std140 byte layout of Pipeline GLSL MaterialParams.
/// std140 requires vec4 values to sit at 16-byte aligned locations.
/// The scalar region from 0 to 103 matches the DX cbuffer layout,
/// but morphWeights must live at offset 112, after 8 bytes of padding, instead of DX offset 96.
/// </summary>
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
    [FieldOffset(68)] public uint RenderMode;     // 0=Sprite2D, 1=Pbr3D, 2=TextMsdf reserved.
    [FieldOffset(72)] public float Padding1;         // Reused by the Text and MSDF path.
    [FieldOffset(76)] public uint IsInstanced;       // 0=regular draw using the b0 world matrix, 1=GPU instancing using a per-instance matrix.
    [FieldOffset(80)] public uint IsSkinned;         // 0=static, 1=skeletal skinning.
    [FieldOffset(84)] public uint BonePaletteStride; // Instanced skinning: stride of the bone palette per instance.
    [FieldOffset(88)] public uint HasMorphTargets;  // 0=none, 1=morph-target delta data exists.
    [FieldOffset(92)] public uint MorphTargetCount; // Number of active morph targets.
    [FieldOffset(96)] public uint MorphVertexCount; // Total vertex count, used to compute structured-buffer stride.
    [FieldOffset(100)] public uint HasPrevBones;          // 0 or 1, whether the prev bone storage buffer is valid.
    [FieldOffset(104)] public uint HasPrevInstanceWorld;  // 0 or 1, whether the prev instanceWorld storage buffer is valid.
    [FieldOffset(108)] public uint HasPrevMorph;          // 0 or 1, whether the prev morphWeights storage buffer is valid.
    [FieldOffset(112)] public Vector4 MorphWeights; // Used by regular models. Instanced models use per-instance morph weights.
}

/// <summary>
/// Primitive data aligned one to one with DX12 PrimitiveData:
/// geometry plus N-buffered Matrix and Material UBOs plus 5 PBR textures
/// plus N-buffered DescriptorSet objects synchronized with the frame ring to avoid write collisions across in-flight frames.
///
/// Note the DescriptorSet write strategy:
/// once UBO handles and ImageView handles are written, they no longer change.
/// Content updates are made by directly writing mapped host-coherent memory through pointers,
/// equivalent to persistent Map on DX12 plus N-buffered frame-index switching.
/// </summary>
internal unsafe class PrimitiveData : IDisposable
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

    /// <summary>Contract clause 7 of 2-2: GTAO exemption, synchronized with Mesh3D.ExcludeFromAo. Draw buckets use it to choose the NoDepth PSO.</summary>
    public bool AoExempt;
    public Vector3 LocalBoundsCenter;
    /// <summary>1-3: half extents of the primitive-local AABB, paired with LocalBoundsCenter for primitive-level and instance-level culling.</summary>
    public Vector3 LocalBoundsExtents;

    public BufferResource VertexBuffer;

    public BufferResource IndexBuffer;

    public BufferResource[] MatrixBuffers = null!;

    public byte*[] MappedMatrixBuffers = null!;

    public BufferResource[] MaterialBuffers = null!;

    public byte*[] MappedMaterialBuffers = null!;

    /// <summary>N-buffered DescriptorSet, one per frame, to avoid conflicts across in-flight frames.</summary>
    public DescriptorSet[] DescriptorSets = null!;

    /// <summary>
    /// 1-7:
    /// same length as <see cref="DescriptorSets"/>, recording per frame slot which cube view is currently written into binding 16 of that set,
    /// stored as VKTextureCube.ViewVersion, with 0 meaning not written yet.
    /// This must be tracked per slot rather than by a single scalar.
    /// vkUpdateDescriptorSets can safely modify only sets in already retired slots,
    /// and refreshing every slot at once would collide with command buffers still in flight.
    /// </summary>
    public ulong[] EnvCubeViewVersions = null!;

    /// <summary>Clause 10 of 2-4:
    /// same length as <see cref="DescriptorSets"/>, recording per frame slot which DDGI atlas view is currently written into binding 17 of that set,
    /// stored as Texture.ViewVersion, with 0 meaning not written yet.
    /// The reason for per-slot tracking is the same as <see cref="EnvCubeViewVersions"/>.</summary>
    public ulong[] DdgiAtlasViewVersions = null!;

    /// <summary>Step 3 of 2-4:
    /// same length as <see cref="DescriptorSets"/>, recording per frame slot which DDGI depth-atlas view is currently written into binding 18 of that set,
    /// with the same semantics as <see cref="DdgiAtlasViewVersions"/>.</summary>
    public ulong[] DdgiDepthViewVersions = null!;

    /// <summary>Step C of 2-5:
    /// same length as <see cref="DescriptorSets"/>, recording per frame slot which cloud-noise view is currently written into binding 19 of that set,
    /// with the same semantics as <see cref="DdgiAtlasViewVersions"/>.</summary>
    public ulong[] CloudNoiseViewVersions = null!;

    /// <summary>Step E of 2-5:
    /// same length as <see cref="DescriptorSets"/>, recording per frame slot which AP volume view is currently written into binding 20 of that set,
    /// with the same semantics as <see cref="DdgiAtlasViewVersions"/>.</summary>
    public ulong[] AerialLutViewVersions = null!;

    public Texture BaseColorTexture = null!;

    public Texture NormalTexture = null!;

    public Texture MetallicRoughnessTexture = null!;

    public Texture OcclusionTexture = null!;

    public Texture EmissiveTexture = null!;

    public BufferResource MorphDeltasBuffer;
    public bool OwnsMorphDeltasBuffer;

    public MaterialParams MaterialParams;

    /// <summary>
    /// Contract clause 6 of 2-3:
    /// CPU shadow copy of the previous frame's world matrix, not transposed.
    /// MatrixBuffers are N-buffered and live in an UPLOAD heap, so historical frames must never be read back from them.
    /// History therefore has to come from this field.
    /// All-zero means no history exists yet, the first frame, and shader code falls back to the current world matrix.
    /// </summary>
    public System.Numerics.Matrix4x4 PrevWorldMatrix;

    /// <summary>Original BaseColor.W, alpha, from the glTF material. Combined with Model.Alpha as the final transparency multiplier.</summary>
    public float OriginalBaseColorAlpha = 1.0f;

    /// <summary>Original Surface BaseColor captured during Load. Used as the multiplicative base for SyncColorTint, where rgb is multiplied by tint and W stays untouched.</summary>
    public Vector4 OriginalBaseColor = Vector4.One;

    /// <summary>
    /// Original AlphaCutoff from the glTF material.
    /// When Model.Alpha is less than 1, this is scaled proportionally with alpha
    /// to prevent MASK materials from being clipped away entirely at low Model.Alpha by clip(alpha - alphaCutoff).
    /// </summary>
    public float OriginalAlphaCutoff = 0.5f;

    /// <summary>
    /// Whether the material is transparent, determined from glTF AlphaMode:
    /// BLEND is transparent and uses the Transparent PSO,
    /// MASK uses the Opaque PSO plus shader discard,
    /// and OPAQUE uses the Opaque PSO.
    /// </summary>
    public bool IsTransparent;

    public void Dispose()
    {
        var rm = Device.ResourceManager;
        if (rm != null)
        {
            rm.DestroyBuffer(VertexBuffer); VertexBuffer = default;
            rm.DestroyBuffer(IndexBuffer); IndexBuffer = default;

            if (MatrixBuffers != null)
            {
                for (int i = 0; i < MatrixBuffers.Length; i++)
                {
                    if (MappedMatrixBuffers[i] != null && MatrixBuffers[i].Memory.Handle != 0)
                        Device.Vk.UnmapMemory(Device.LogicalDevice, MatrixBuffers[i].Memory);
                    rm.DestroyBuffer(MatrixBuffers[i]);
                }
                MatrixBuffers = null!;
                MappedMatrixBuffers = null!;
            }

            if (MaterialBuffers != null)
            {
                for (int i = 0; i < MaterialBuffers.Length; i++)
                {
                    if (MappedMaterialBuffers[i] != null && MaterialBuffers[i].Memory.Handle != 0)
                        Device.Vk.UnmapMemory(Device.LogicalDevice, MaterialBuffers[i].Memory);
                    rm.DestroyBuffer(MaterialBuffers[i]);
                }
                MaterialBuffers = null!;
                MappedMaterialBuffers = null!;
            }

            if (OwnsMorphDeltasBuffer && MorphDeltasBuffer.Buffer.Handle != 0)
            {
                rm.DestroyBuffer(MorphDeltasBuffer);
                MorphDeltasBuffer = default;
            }
        }

        if (DescriptorSets != null)
        {
            for (int i = 0; i < DescriptorSets.Length; i++)
                Device.DescriptorAllocator?.FreeSet(DescriptorSets[i]);
            DescriptorSets = null!;
        }
    }
}
