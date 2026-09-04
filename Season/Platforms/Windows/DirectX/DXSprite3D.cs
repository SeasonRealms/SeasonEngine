// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Direct3D12;

namespace Season.Platforms.Windows.DirectX;

internal unsafe class DXSprite3D : DXSpriteQuad
{
    // === Static quad VB shared by all DXSprite3D instances
    // (reference-counted) ===
    private static ID3D12Resource* _sharedVertexBuffer;
    private static VertexBufferView _sharedVertexBufferView;
    private static int _sharedRefCount;

    // Most recently written World matrix.
    // Kept for diagnostic logging only and no longer used as the basis for
    // skipping writes, because that caused position flicker when other frame
    // CBs under N-buffering were not kept in sync.
    // Under 2-3 contract rule 6 it also serves as the CPU shadow copy of the
    // previous frame's world matrix. All-zero on the first frame means
    // "no history" sentinel.
    private Matrix4x4 _lastWorldMatrix;

    public DXSprite3D(DXTexture dxTexture)
    {
        Texture = new Texture();
        DXTexture = dxTexture;
        CreateGPUResources();
    }

    private void CreateGPUResources()
    {
        EnsureSharedQuad();

        CreateMatrixBuffer();
        CreateMaterialBuffer();
        InitializeMaterial();
    }

    private static void EnsureSharedQuad()
    {
        if (_sharedVertexBuffer != null)
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

        _sharedVertexBuffer = DirectX.Device.CreateVertexBuffer<Vertex>(6, out _sharedVertexBufferView);
        Device.ResourceManager.UpdateBuffer(
            _sharedVertexBuffer,
            (uint)(vertices.Length * Unsafe.SizeOf<Vertex>()),
            vertices);
        _sharedRefCount = 1;
    }

    /// <summary>
    /// Updates the 3D Sprite transform from world position plus billboard mode.
    /// </summary>
    public void Update(in Vector3 position, in Vector2 size, in Quaternion rotation,
                       in System.Numerics.Matrix4x4 cameraView, in System.Numerics.Matrix4x4 cameraProjection,
                       Season.Controls.BillboardMode mode, in Vector4 color, float alpha)
    {
        if (DXTexture == null) return;

        // Recover camera world position and forward vector from the inverse View matrix.
        System.Numerics.Matrix4x4.Invert(cameraView, out var viewInv);
        Vector3 cameraPosition = viewInv.Translation;
        Vector3 cameraForward = -new Vector3(viewInv.M31, viewInv.M32, viewInv.M33);

        System.Numerics.Matrix4x4 billboardRot = mode switch
        {
            Season.Controls.BillboardMode.Spherical => System.Numerics.Matrix4x4.CreateBillboard(
                position, cameraPosition, Vector3.UnitY, cameraForward),
            Season.Controls.BillboardMode.Cylindrical => System.Numerics.Matrix4x4.CreateConstrainedBillboard(
                position, cameraPosition, Vector3.UnitY, cameraForward, Vector3.UnitZ),
            _ => System.Numerics.Matrix4x4.CreateFromQuaternion(rotation) * System.Numerics.Matrix4x4.CreateTranslation(position)
        };

        // Note: the Spherical / Cylindrical branches already return matrices
        // whose translation moves to `position`. The None branch explicitly
        // multiplies by CreateTranslation(position) above, so all three branches
        // now share the same semantics.
        // Do not multiply an extra translation matrix here, or Translation would
        // become 2 * position, which was the root cause of billboards jumping
        // out of view.
        // Size scaling must be multiplied before billboardRot. The shared quad
        // is a unit plane centered at the origin, so scaling should only affect
        // local axes and must not disturb the translation already carried by the
        // billboard matrix. This matches the CreateScale x billboardRot order on
        // the Web backend.
        var scaleMatrix = size == Vector2.One ? Matrix4x4.Identity : Matrix4x4.CreateScale(size.X, size.Y, 1f);
        var worldMatrix = scaleMatrix * billboardRot;

        var prevWorldMatrix = _lastWorldMatrix;
        _lastWorldMatrix = worldMatrix;

        var matrices = new MatrixBuffer
        {
            World = Matrix4x4.Transpose(worldMatrix),
            View = Matrix4x4.Transpose(DXPrimitiveGroup.Camera.View),
            Projection = Matrix4x4.Transpose(DXPrimitiveGroup.Camera.Projection),
            // 2-3 contract rule 6: capture history first, then roll forward.
            // Previous state always comes from the CPU shadow copy.
            PrevWorld = Matrix4x4.Transpose(prevWorldMatrix),
            PrevViewProjection = Matrix4x4.Transpose(DXPrimitiveGroup.Camera.PrevViewProjection),
        };

        // Material CB: color x alpha is encoded through BaseColor.W.
        // If material is not written here, Sprite3D.Alpha becomes completely
        // ineffective because InitializeMaterial is otherwise immutable.
        var materialParams = new MaterialParams
        {
            BaseColor = new Vector4(color.X, color.Y, color.Z, color.W * alpha),
            EmissiveFactor = Vector4.Zero,
            MetallicFactor = 0f,
            RoughnessFactor = 1f,
            UseAlbedoMap = DXTexture != null ? 1u : 0u,
            UseNormalMap = NormalMap != null ? 1u : 0u,
            UseMetallicRoughnessMap = MetallicRoughnessMap != null ? 1u : 0u,
            UseOcclusionMap = OcclusionMap != null ? 1u : 0u,
            UseEmissiveMap = EmissiveMap != null ? 1u : 0u,
            RenderMode = 0u   // Sprite3D is unlit and follows the Sprite path
        };

        // Synchronize per-frame resources across all N-buffered frames so the
        // other N-1 frames never read stale or identity matrices and make the
        // billboard flicker at different positions.
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
        if (!_transformInitialized)
            return;

        if (DXTexture == null || DXTexture._textureResource == null)
            return;

        fixed (VertexBufferView* vbv = &_sharedVertexBufferView)
        {
            DrawQuad(vbv);
        }
    }

    public override void Dispose()
    {
        DisposeMatrixBuffers();
        DisposeMaterialBuffers();

        _sharedRefCount--;
        if (_sharedRefCount <= 0 && _sharedVertexBuffer != null)
        {
            _sharedVertexBuffer->Release();
            _sharedVertexBuffer = null;
        }
    }
}
