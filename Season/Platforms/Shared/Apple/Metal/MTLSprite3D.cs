// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Metal;
using MTLTexture = Season.Platforms.Shared.Apple.Metal.Texture;

namespace Season.Platforms.Shared.Apple.Metal;

/// <summary>
/// Metal backend 3D billboard sprite aligned one to one with DXSprite3D and VKSprite3D:
/// - all instances share one static quad VB managed through reference counting
/// - the World matrix is determined by BillboardMode, meaning Spherical, Cylindrical, or None
/// - View and Projection come from MTLPrimitiveGroup.Camera and stay in sync with the PBR3D path
/// - renderMode = 0 selects the Sprite path, with no PBR lighting and direct BaseColor times texture output
/// </summary>
internal sealed class MTLSprite3D : MTLSpriteQuad
{
    // === Static quad VB shared by all MTLSprite3D instances, managed through reference counting ===
    static IMTLBuffer? _sharedVertexBuffer;
    static int _sharedRefCount;

    // Most recently written World matrix, kept for diagnostic logging.
    Matrix4x4 _lastWorldMatrix;

    /// <summary>Contract clause 6 of 2-3: CPU shadow copy of the previous-frame World matrix, kept non-transposed.
    /// All zeros means no history exists yet, such as on the first frame, and the shader falls back to the current world.</summary>
    Matrix4x4 _prevWorldMatrix;

    public MTLSprite3D(MTLTexture texture)
    {
        Texture = new Season.Controls.Texture();
        AlbedoTexture = texture;
        CreateGPUResources();
    }

    void CreateGPUResources()
    {
        EnsureSharedQuad();

        CreateMatrixBuffer();
        CreateMaterialBuffer();
        InitializeMaterial();
    }

    static void EnsureSharedQuad()
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

        _sharedVertexBuffer = Device.ResourceManager.CreateVertexBuffer(vertices);
        _sharedRefCount = 1;
    }

    /// <summary>
    /// Updates the 3D sprite transform from world position plus Billboard mode.
    /// </summary>
    public void Update(in Vector3 position, in Vector2 size, in Quaternion rotation,
                       in Matrix4x4 cameraView, in Matrix4x4 cameraProjection,
                       Season.Controls.BillboardMode mode, in Vector4 color, float alpha)
    {
        if (AlbedoTexture == null) return;

        // Recover camera world position and forward vector from the inverse View matrix.
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

        // Note:
        // Spherical and Cylindrical branches already include translation to position.
        // The None branch also adds CreateTranslation(position) explicitly,
        // so all three branches share the same semantics and must not multiply another translation afterward.
        // Size scaling must happen before billboardRot because the shared quad is a unit plane centered at the origin.
        // Scaling should affect only local axes and must not modify the translation already carried by the billboard matrix,
        // matching the CreateScale times billboardRot order used on the Web backend.
        var scaleMatrix = size == Vector2.One ? Matrix4x4.Identity : Matrix4x4.CreateScale(size.X, size.Y, 1f);
        var worldMatrix = scaleMatrix * billboardRot;

        // Contract clause 6 of 2-3:
        // capture history first, then roll it forward, and always source history from the CPU shadow copy.
        var prevWorldMatrix = _prevWorldMatrix;
        _prevWorldMatrix = worldMatrix;
        _lastWorldMatrix = worldMatrix;

        var matrices = new MatrixBuffer
        {
            World = Matrix4x4.Transpose(worldMatrix),
            View = Matrix4x4.Transpose(MTLPrimitiveGroup.Camera.View),
            Projection = Matrix4x4.Transpose(MTLPrimitiveGroup.Camera.Projection),
            // Contract clause 6 of 2-3: write the history matrix into MatrixBuffer.
            PrevWorld = Matrix4x4.Transpose(prevWorldMatrix),
            PrevViewProjection = Matrix4x4.Transpose(MTLPrimitiveGroup.Camera.PrevViewProjection),
        };

        // Material UBO: color times alpha is expressed through BaseColor.W.
        var materialParams = new MaterialParams
        {
            BaseColor = new Vector4(color.X, color.Y, color.Z, color.W * alpha),
            EmissiveFactor = Vector4.Zero,
            MetallicFactor = 0f,
            RoughnessFactor = 1f,
            UseAlbedoMap = AlbedoTexture != null ? 1u : 0u,
            UseNormalMap = NormalMap != null ? 1u : 0u,
            UseMetallicRoughnessMap = MetallicRoughnessMap != null ? 1u : 0u,
            UseOcclusionMap = OcclusionMap != null ? 1u : 0u,
            UseEmissiveMap = EmissiveMap != null ? 1u : 0u,
            AlphaCutoff = 0.5f,
            AlphaMode = 2u,
            RenderMode = 0u   // Sprite3D does not use lit PBR and therefore takes the Sprite path.
        };

        // Mirror per-frame resources into all N-buffered frames
        // so the remaining frames never read stale or identity matrices and cause billboard flicker.
        int n = Device.frameCount;
        for (int i = 0; i < n; i++)
        {
            WriteStruct(_matrixBuffers[i], matrices);
            WriteStruct(_materialBuffers[i], materialParams);
        }

        _transformInitialized = true;
    }

    public void Draw()
    {
        if (!_transformInitialized) return;
        if (AlbedoTexture == null) return;
        if (_sharedVertexBuffer == null) return;

        DrawQuad(_sharedVertexBuffer);
    }

    public override void Dispose()
    {
        DisposeCommon();

        _sharedRefCount--;
        if (_sharedRefCount <= 0 && _sharedVertexBuffer != null)
        {
            _sharedVertexBuffer.Dispose();
            _sharedVertexBuffer = null;
        }
    }
}
