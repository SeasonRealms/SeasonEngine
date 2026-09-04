// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Vulkan;
using System.Runtime.CompilerServices;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkTexture = Season.Platforms.Shared.LinuxAndroid.Vulkan.Texture;

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

/// <summary>
/// Vulkan backend 3D billboard sprite aligned 1:1 with DXSprite3D:
///   - All instances share one static quad VB (reference-counted)
///   - The World matrix is determined by BillboardMode (Spherical/Cylindrical/None)
///   - View/Projection reuse VKPrimitiveGroup.Camera (kept in sync with the PBR3D path)
///   - renderMode = 0: Sprite path (no PBR lighting)
/// </summary>
internal unsafe class VKSprite3D : VKSpriteQuad
{
    // Shared static quad VB for all VKSprite3D instances (reference-counted)
    static BufferResource _sharedVertexBuffer;
    static int _sharedRefCount;

    // Most recently written World matrix (kept for diagnostic logging)
    Matrix4x4 _lastWorldMatrix;

    // 2-3 contract clause 6: CPU shadow copy of the previous frame's world matrix
    // (plays the same role as DXSprite3D._lastWorldMatrix, but explicitly carries prev semantics)
    Matrix4x4 _prevWorldMatrix;

    public VKSprite3D(VkTexture vkTexture)
    {
        Texture = new Season.Controls.Texture();
        VKTexture = vkTexture;
        CreateGPUResources();
    }

    void CreateGPUResources()
    {
        EnsureSharedQuad();

        CreateMatrixBuffer();
        CreateMaterialBuffer();
        InitializeMaterial();
        AllocateAndWriteDescriptorSets();
    }

    static void EnsureSharedQuad()
    {
        if (_sharedVertexBuffer.Buffer.Handle != 0)
        {
            _sharedRefCount++;
            return;
        }

        float halfW = 0.5f;
        float halfH = 0.5f;
        var vertices = new Vertex[6];
        vertices[0] = CreateQuadVertex(-halfW, halfH, 0, 0, 0);
        vertices[1] = CreateQuadVertex(halfW, halfH, 0, 1, 0);
        vertices[2] = CreateQuadVertex(-halfW, -halfH, 0, 0, 1);
        vertices[3] = CreateQuadVertex(halfW, halfH, 0, 1, 0);
        vertices[4] = CreateQuadVertex(halfW, -halfH, 0, 1, 1);
        vertices[5] = CreateQuadVertex(-halfW, -halfH, 0, 0, 1);

        _sharedVertexBuffer = Device.ResourceManager.CreateVertexBuffer(vertices);
        _sharedRefCount = 1;
    }

    /// <summary>
    /// Update the 3D sprite transform from world position and Billboard mode.
    /// </summary>
    public void Update(in Vector3 position, in Vector2 size, in Quaternion rotation,
                       in Matrix4x4 cameraView, in Matrix4x4 cameraProjection,
                       Season.Controls.BillboardMode mode, in Vector4 color, float alpha)
    {
        if (VKTexture == null) return;

        // Extract the camera world position and forward vector from the inverse View matrix
        Matrix4x4.Invert(cameraView, out var viewInv);
        Vector3 cameraPosition = viewInv.Translation;
        Vector3 cameraForward = -new Vector3(viewInv.M31, viewInv.M32, viewInv.M33);

        Matrix4x4 billboardRot = mode switch
        {
            Season.Controls.BillboardMode.Spherical => Matrix4x4.CreateBillboard(
                position, cameraPosition, Vector3.UnitY, cameraForward),
            Season.Controls.BillboardMode.Cylindrical => Matrix4x4.CreateConstrainedBillboard(
                position, cameraPosition, Vector3.UnitY, cameraForward, Vector3.UnitZ),
            _ => Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position)
        };

        // Note: the Spherical / Cylindrical branches already include translation to `position`;
        // the None branch also explicitly appends CreateTranslation(position),
        // so all three paths share the same semantics and must not multiply another transMatrix.
        // Size scaling must be applied before billboardRot:
        // the shared quad is a unit plane centered at the origin, so scaling affects only local axes
        // and does not affect the translation already carried by the billboard matrix
        // (same ordering as CreateScale x billboardRot on the Web backend).
        var scaleMatrix = size == Vector2.One ? Matrix4x4.Identity : Matrix4x4.CreateScale(size.X, size.Y, 1f);
        var worldMatrix = scaleMatrix * billboardRot;

        // 2-3 contract clause 6: capture history before rolling forward,
        // and always source historical data from the CPU shadow copy
        var prevWorldMatrix = _prevWorldMatrix;
        _prevWorldMatrix = worldMatrix;
        _lastWorldMatrix = worldMatrix;

        var matrices = new MatrixBuffer
        {
            World = Matrix4x4.Transpose(worldMatrix),
            View = Matrix4x4.Transpose(VKPrimitiveGroup.Camera.View),
            Projection = Matrix4x4.Transpose(VKPrimitiveGroup.Camera.Projection),
            // 2-3 contract clause 6: write the historical matrix into MatrixBuffer
            PrevWorld = Matrix4x4.Transpose(prevWorldMatrix),
            PrevViewProjection = Matrix4x4.Transpose(VKPrimitiveGroup.Camera.PrevViewProjection),
        };

        // Material UBO: color x alpha is expressed through BaseColor.W
        var materialParams = new MaterialParams
        {
            BaseColor = new Vector4(color.X, color.Y, color.Z, color.W * alpha),
            EmissiveFactor = Vector4.Zero,
            MetallicFactor = 0f,
            RoughnessFactor = 1f,
            UseAlbedoMap = VKTexture != null ? 1u : 0u,
            UseNormalMap = NormalMap != null ? 1u : 0u,
            UseMetallicRoughnessMap = MetallicRoughnessMap != null ? 1u : 0u,
            UseOcclusionMap = OcclusionMap != null ? 1u : 0u,
            UseEmissiveMap = EmissiveMap != null ? 1u : 0u,
            AlphaCutoff = 0.5f,
            AlphaMode = 2u,
            RenderMode = 0u   // Sprite3D has no lit PBR path and uses the Sprite path
        };

        // Synchronize all N-buffered per-frame resources across all frames
        // to avoid the other N-1 frames reading stale / identity matrices and causing billboard flicker
        int n = (int)Device.frameCount;
        for (int i = 0; i < n; i++)
        {
            Unsafe.Write(_mappedMatrixBuffers[i], matrices);
            Unsafe.Write(_mappedMaterialBuffers[i], materialParams);
        }

        _transformInitialized = true;
    }

    public void Draw()
    {
        if (!_transformInitialized) return;
        if (VKTexture == null) return;

        DrawQuad(_sharedVertexBuffer.Buffer);
    }

    public override void Dispose()
    {
        DisposeCommon();

        _sharedRefCount--;
        if (_sharedRefCount <= 0 && _sharedVertexBuffer.Buffer.Handle != 0)
        {
            Device.ResourceManager?.DestroyBuffer(_sharedVertexBuffer);
            _sharedVertexBuffer = default;
        }
    }
}
