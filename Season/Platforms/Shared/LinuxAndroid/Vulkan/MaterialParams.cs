// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

// PBR texture-slot indices aligned with shader binding numbers set=0 binding=3 through 7. The canonical
// definition is the shared Season.Rendering.Drawables.TextureSlot; this alias keeps every call site unchanged.
using TextureSlot = Season.Rendering.Drawables.TextureSlot;

/// <summary>
/// Material constant buffer, UBO b2, aligned one to one with the std140 byte layout of Pipeline GLSL MaterialParams.
/// std140 requires vec4 values to sit at 16-byte aligned locations.
/// The scalar region from 0 to 103 matches the DX cbuffer layout,
/// but morphWeights must live at offset 112, after 8 bytes of padding, instead of DX offset 96.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 144)]
internal partial struct MaterialParams
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
    [FieldOffset(72)] public uint TintMode;         // 0=ignore the per-instance color, 1=multiply the PBR albedo by it. Doubles as the retired pxRange alignment slot.
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
    /// <summary>
    /// Vertex swell (currently only the sea uses it): x = half peak-to-trough amplitude in meters (0 = off),
    /// y = current phase, z = previous-frame phase for the velocity path, w = fade-out radius in meters.
    /// The amplitude is what the vertex shader branches on, so a zero here costs one compare per vertex.
    /// </summary>
    [FieldOffset(128)] public Vector4 VertexWave;
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
// Geometry, material constants, textures and part keys live in the shared PrimitiveCpuData; only the Vulkan buffers,
// descriptor sets and the vertex-stream resolution below are Vulkan's own.
internal unsafe class PrimitiveData : PrimitiveCpuData<MaterialParams, Texture>, IDisposable
{
    public BufferResource VertexBuffer;

    /// <summary>
    /// C1: the compute-written per-vertex stream mirrored from Surface.VertexStream by VKMesh3D.Update; a reference, never owned
    /// here (the producing effect disposes it). Null for every primitive whose geometry is static.
    /// </summary>
    public VKStorageBuffer? VertexStream;

    /// <summary>
    /// C1: the single place every draw path (scene direct, scene collection, shadow direct, shadow indirect) takes its slot-0
    /// buffer from, so the shadow can never rasterise a different shape than the scene shows. The stream is used only when its
    /// flag says the handoff to vertex input was recorded this frame (IGraphics.TryPrepareVertexInput) and it is large enough for
    /// every vertex the indices may address; any other frame draws the static buffer, which keeps validation quiet and the two
    /// passes consistent with each other.
    /// </summary>
    public VkBuffer ResolveVertexBuffer(VkBuffer fallback)
    {
        var stream = VertexStream;
        if (stream == null || stream.Buffer.Buffer.Handle == 0 || !stream.VertexInputReady || Vertices == null)
            return fallback;
        ulong bytes = (ulong)Vertices.Count * (ulong)sizeof(Vertex);
        if (bytes == 0 || stream.SizeInBytes < bytes)
            return fallback;
        return stream.Buffer.Buffer;
    }

    public BufferResource IndexBuffer;

    public BufferResource[] MatrixBuffers = null!;

    public byte*[] MappedMatrixBuffers = null!;

    public BufferResource[] MaterialBuffers = null!;

    public byte*[] MappedMaterialBuffers = null!;

    /// <summary>N-buffered DescriptorSet, one per frame, to avoid conflicts across in-flight frames.</summary>
    public DescriptorSet[] DescriptorSets = null!;

    // 3B: the six per-slot view-version caches (envCube / DDGI atlas / DDGI depth / cloud noise / AP LUT / Sky-View)
    // are gone together with the frame-start refresh pass. Those six lighting inputs now live in the per-view globals
    // set 3, which is written once per view by Pipeline.PrepareViewGlobals, so a primitive's set 0 never changes again
    // after allocation and needs no version bookkeeping.

    public BufferResource MorphDeltasBuffer;
    public bool OwnsMorphDeltasBuffer;

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
