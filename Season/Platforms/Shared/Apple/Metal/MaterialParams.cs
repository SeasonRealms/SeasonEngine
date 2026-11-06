// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Metal;

namespace Season.Platforms.Shared.Apple.Metal;

// PBR texture-slot indices aligned with the shader binding numbers at texture(0..4). The canonical definition
// is the shared Season.Rendering.Drawables.TextureSlot; this alias keeps every call site unchanged.
using TextureSlot = Season.Rendering.Drawables.TextureSlot;

/// <summary>
/// Material constant buffer for fragment buffer slot 2, matching the MaterialParams byte layout in Pipeline.MetalShaderSource exactly.
/// It is fully consistent with the DX and Vulkan MaterialParams definitions and shares the same cross-platform semantics.
/// Within this 80-byte region, the MSL constant-buffer layout for scalars and vectors matches std140 and cbuffer semantics.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 160)]
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
    [FieldOffset(68)] public uint RenderMode;     // 0=Sprite2D, 1=Pbr3D, 2=TextMsdf, 3=ProceduralSky
    [FieldOffset(72)] public uint TintMode;          // 0=ignore the per-instance color, 1=multiply the PBR albedo by it. Doubles as the retired pxRange alignment slot.
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
    /// <summary>
    /// Vertex swell (currently only the sea uses it): x = half peak-to-trough amplitude in meters (0 = off),
    /// y = current phase, z = previous-frame phase for the velocity path, w = fade-out radius in meters.
    /// The amplitude is what the vertex shader branches on, so a zero here costs one compare per vertex.
    /// </summary>
    [FieldOffset(144)] public Vector4 VertexWave;
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
// Geometry, material constants, textures and part keys live in the shared PrimitiveCpuData; only the Metal buffers
// and the vertex-stream resolution below are Metal's own.
internal sealed partial class PrimitiveData : PrimitiveCpuData<MaterialParams, Texture>, IDisposable
{
    public IMTLBuffer VertexBuffer = null!;

    /// <summary>
    /// C1: the compute-written per-vertex stream mirrored from Surface.VertexStream by MTLMesh3D.Update; a reference, never owned
    /// here. Dispose below releases VertexBuffer, so the stream must not be released through this field as well.
    /// </summary>
    public MTLStorageBuffer? VertexStream;

    /// <summary>
    /// C1: the single place every draw path (scene direct, scene collection, shadow direct, shadow indirect) takes its slot-0
    /// buffer from, so the shadow can never rasterise a different shape than the scene shows. The stream is used only when its
    /// flag says the handoff to vertex input was made this frame (IGraphics.TryPrepareVertexInput) and it is large enough for
    /// every vertex the indices may address; any other frame draws the static buffer, which keeps the two passes consistent.
    /// </summary>
    public unsafe IMTLBuffer ResolveVertexBuffer(IMTLBuffer fallback)
    {
        var stream = VertexStream;
        if (stream == null || stream.Buffer == null || !stream.VertexInputReady || Vertices == null)
            return fallback;
        ulong bytes = (ulong)Vertices.Count * (ulong)sizeof(Vertex);
        if (bytes == 0 || stream.SizeInBytes < bytes)
            return fallback;
        return stream.Buffer;
    }

    public IMTLBuffer IndexBuffer = null!;

    /// <summary>N-buffered matrices at b0 containing world, view, and projection, with length equal to Device.frameCount.</summary>
    public IMTLBuffer[] MatrixBuffers = null!;

    /// <summary>N-buffered MaterialParams at b2, with length equal to Device.frameCount.</summary>
    public IMTLBuffer[] MaterialBuffers = null!;

    public IMTLBuffer? MorphDeltasBuffer;
    public bool OwnsMorphDeltasBuffer;

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
