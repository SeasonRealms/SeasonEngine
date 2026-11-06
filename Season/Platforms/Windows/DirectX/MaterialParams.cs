// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Direct3D12;

namespace Season.Platforms.Windows.DirectX;

// PBR texture-slot indices aligned with shader registers t0-t4. The canonical definition is the shared
// Season.Rendering.Drawables.TextureSlot; this alias keeps every existing call site spelled the same way.
using TextureSlot = Season.Rendering.Drawables.TextureSlot;

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
    [FieldOffset(68)] public uint RenderMode;     // 0=Sprite2D, 1=Pbr3D, 2=TextMsdf (reserved)
    [FieldOffset(72)] public uint BonePaletteStride; // Instanced skinning: stride of the per-instance bone palette
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
    [FieldOffset(124)] public uint TintMode;              // 0=ignore the per-instance color, 1=multiply the PBR albedo by it (a per-part gate; also the 16-byte alignment slot)
    // Vertex swell (currently only the sea uses it): x = half peak-to-trough amplitude in meters (0 = off),
    // y = current phase, z = previous-frame phase for the velocity path, w = fade-out radius in meters.
    // The amplitude is what the vertex shader branches on, so a zero here costs one compare per vertex.
    [FieldOffset(128)] public Vector4 VertexWave;
}

// Geometry, material constants, textures and part keys live in the shared PrimitiveCpuData; only the D3D12 handles
// and the vertex-stream resolution below are DirectX's own.
internal unsafe partial class PrimitiveData : PrimitiveCpuData<MaterialParams, DXTexture>, IDisposable
{
    // The shared highlight shell does not own the VB/IB; during collection resources are taken from the template and kept alive (AddRef up to this frame's GPU safe point), the command does not retain the template.
    internal PrimitiveData? SharedGeometrySource;
    public ID3D12Resource* VertexBuffer;
    public VertexBufferView VertexBufferView;
    public ID3D12Resource* IndexBuffer;
    public IndexBufferView IndexBufferView;

    /// <summary>
    /// C1: the compute-written per-vertex stream mirrored from Surface.VertexStream by DXMesh3D.Update; a reference, never owned
    /// here (the producing effect disposes it). Null for every primitive whose geometry is static.
    /// </summary>
    public DXStorageBuffer? VertexStream;

    /// <summary>
    /// C1: the single place every draw path (scene direct, scene collection, shadow direct, shadow indirect) takes its slot-0 view
    /// from, so the shadow can never rasterize a different shape than the scene shows. The stream is used only when its tracked
    /// state says the handoff to vertex input was recorded (IGraphics.TryPrepareVertexInput) and it is large enough for every
    /// vertex the indices may address; any other frame draws the static buffer, which keeps the debug layer quiet and the two
    /// passes consistent with each other.
    /// </summary>
    public VertexBufferView ResolveVertexView()
    {
        // A highlight shell shares its template's geometry, so it follows the template's stream as well.
        var geometry = SharedGeometrySource ?? this;
        var stream = geometry.VertexStream;
        if (stream == null || stream.Resource == null || stream.CurrentState != ResourceStates.VertexAndConstantBuffer
            || geometry.Vertices == null)
            return VertexBufferView;
        uint stride = (uint)sizeof(Vertex);
        uint bytes = (uint)geometry.Vertices.Count * stride;
        if (bytes == 0 || stream.SizeInBytes < bytes)
            return VertexBufferView;
        return new VertexBufferView
        {
            BufferLocation = stream.Resource->GetGPUVirtualAddress(),
            SizeInBytes = bytes,
            StrideInBytes = stride,
        };
    }

    public ID3D12Resource*[] MaterialBuffers;
    public byte*[] MappedMaterialBuffers;

    public ID3D12Resource*[] MatrixBuffers;
    public byte*[] MappedMatrixBuffers;

    // Phase 3: Morph-target GPU data
    public ID3D12Resource* MorphDeltasBuffer;         // StructuredBuffer resource
    public GpuDescriptorHandle MorphDeltasSrvHandle;  // t5 SRV descriptor handle
    public int MorphDescriptorId = -1;                // Descriptor-heap ID used for release

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
