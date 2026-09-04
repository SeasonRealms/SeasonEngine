// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Direct3D12;

namespace Season.Platforms.Windows.DirectX;

internal unsafe class DXSprite2D : DXSpriteQuad
{
    // === Static 2D orthographic camera ===
    internal static Season.Basic.Camera Camera;

    // === Per-instance exclusive vertex buffer ===
    // 2D vertices may be flipped or rotated by font rendering through QuadVerts,
    // so each instance keeps its own buffer.
    private ID3D12Resource* _vertexBuffer;
    private VertexBufferView _vertexBufferView;

    // Texture-coordinate transform used by font rendering
    public Vector4[] QuadVerts;

    // Original image size
    public float ImageWidth { get; private set; }
    public float ImageHeight { get; private set; }

    // Zero-copy SpriteBase reference: when non-null, Update() reads control
    // properties directly from Sprite2D / Shape without copying.
    internal Season.Controls.SpriteBase? SpriteRef;

    public static void Init()
    {
        // The lighting CB has already been initialized by WindowsApp through
        // DXPrimitiveGroup.InitLights(). Only the 2D camera is handled here.
        Init2DCamera();
    }

    static void Init2DCamera()
    {
        // Orthographic camera for 2D rendering. Left-handed coordinate system,
        // with Z ranging from 0 to 1.
        Camera.View = Matrix4x4.Identity;
        Camera.Projection = Matrix4x4.CreateOrthographicOffCenterLeftHanded(
            -1f, 1f,   // left, right (NDC space)
            -1f, 1f,   // bottom, top (NDC space)
            0.1f, 10f); // near, far
    }

    public static void UpdateCamera(float time, Vector3 cameraPos, Vector3 cameraTarget, SceneLightParams lightParams)
    {
        // 1-3: same source of truth as DXPrimitiveGroup.Update. Matrix building
        // is centralized in the shared Camera3D layer to avoid inconsistencies
        // from a second set of hard-coded parameters.
        var camera3D = DeviceServices.BaseApp.Camera;
        var aspectRatio = DeviceServices.BaseApp.DeviceResolution.X / (float)DeviceServices.BaseApp.DeviceResolution.Y;
        camera3D.UpdateIfChanged(aspectRatio);
        Camera.View = camera3D.View;
        Camera.Projection = camera3D.Projection;

        // Write the lighting CB.
        DXPrimitiveGroup.SetLighting(lightParams);
    }

    public static void InitDispose()
    {
        DXPrimitiveGroup.InitLightsDispose();
    }

    /// <summary>
    /// Creates a Sprite from an image path, with optional PBR maps kept as a
    /// future extension point for advanced material effects.
    /// </summary>
    public DXSprite2D(string albedoPath, string normalPath = null, string metallicRoughnessPath = null,
                      string occlusionPath = null, string emissivePath = null)
    {
        Name = albedoPath;

        DXTexture = DXTexture.GetOrCreate(albedoPath, null);

        if (!string.IsNullOrEmpty(normalPath))
            NormalMap = DXTexture.GetOrCreate(normalPath, null);
        if (!string.IsNullOrEmpty(metallicRoughnessPath))
            MetallicRoughnessMap = DXTexture.GetOrCreate(metallicRoughnessPath, null);
        if (!string.IsNullOrEmpty(occlusionPath))
            OcclusionMap = DXTexture.GetOrCreate(occlusionPath, null);
        if (!string.IsNullOrEmpty(emissivePath))
            EmissiveMap = DXTexture.GetOrCreate(emissivePath, null);

        if (DXTexture != null)
        {
            ImageWidth = DXTexture.Width;
            ImageHeight = DXTexture.Height;
        }

        CreateGPUResources();
        InitializeQuadVertices();
        InitializeMaterial();
    }

    /// <summary>
    /// Creates a Sprite from a DXTexture on the GraphicsDirectX integration path.
    /// </summary>
    internal DXSprite2D(DXTexture dxTexture)
    {
        Texture = new Season.Controls.Texture();
        DXTexture = dxTexture;

        if (DXTexture != null)
        {
            ImageWidth = DXTexture.Width;
            ImageHeight = DXTexture.Height;
        }

        CreateGPUResources();
        InitializeQuadVertices();
        InitializeMaterial();
    }

    private void CreateGPUResources()
    {
        // Vertex buffer: 6 vertices forming 2 triangles.
        _vertexBuffer = DirectX.Device.CreateVertexBuffer<Vertex>(6, out _vertexBufferView);

        CreateMatrixBuffer();
        CreateMaterialBuffer();
    }

    private void InitializeQuadVertices()
    {
        UploadQuadVertices();
    }

    void UploadQuadVertices()
    {
        // Unit quad centered at the origin, with width and height both 1.
        // Actual size is applied through Scale during Update.
        float halfW = 0.5f;
        float halfH = 0.5f;

        int clock;
        bool flipX, flipY;
        float sourceX, sourceY, sourceWidth, sourceHeight;
        Season.Controls.TextureType textureType;

        if (SpriteRef != null)
        {
            clock = SpriteRef.Clock;
            flipX = SpriteRef.FlipX;
            flipY = SpriteRef.FlipY;
            sourceX = SpriteRef.SourceX;
            sourceY = SpriteRef.SourceY;
            sourceWidth = SpriteRef.SourceWidth;
            sourceHeight = SpriteRef.SourceHeight;
            textureType = SpriteRef.TextureType;
        }
        else
        {
            var t = Texture;
            clock = t.Clock;
            flipX = t.FlipX;
            flipY = t.FlipY;
            sourceX = t.SourceX;
            sourceY = t.SourceY;
            sourceWidth = t.SourceWidth;
            sourceHeight = t.SourceHeight;
            textureType = t.TextureType;
        }

        var quadVerts = QuadVerts ?? TextCoords.GetTransforms(clock, flipX, flipY);
        float textureWidth = DXTexture != null && DXTexture.Width > 0 ? DXTexture.Width : 1f;
        float textureHeight = DXTexture != null && DXTexture.Height > 0 ? DXTexture.Height : 1f;
        float mapSourceX = sourceWidth > 0 ? sourceX / textureWidth : 0f;
        float mapSourceY = sourceHeight > 0 ? sourceY / textureHeight : 0f;
        float mapSourceWidth = sourceWidth > 0 ? sourceWidth / textureWidth : 1f;
        float mapSourceHeight = sourceHeight > 0 ? sourceHeight / textureHeight : 1f;

        float MapU(float u) => mapSourceX + u * mapSourceWidth;
        float MapV(float v) => mapSourceY + v * mapSourceHeight;

        var vertices = new Vertex[6];
        // First triangle: top-left -> top-right -> bottom-left
        vertices[0] = CreateQuadVertex(quadVerts[0].X * halfW, quadVerts[0].Y * halfH, 0, MapU(quadVerts[0].Z), MapV(quadVerts[0].W));
        vertices[1] = CreateQuadVertex(quadVerts[1].X * halfW, quadVerts[1].Y * halfH, 0, MapU(quadVerts[1].Z), MapV(quadVerts[1].W));
        vertices[2] = CreateQuadVertex(quadVerts[2].X * halfW, quadVerts[2].Y * halfH, 0, MapU(quadVerts[2].Z), MapV(quadVerts[2].W));
        // Second triangle: top-right -> bottom-right -> bottom-left
        vertices[3] = CreateQuadVertex(quadVerts[1].X * halfW, quadVerts[1].Y * halfH, 0, MapU(quadVerts[1].Z), MapV(quadVerts[1].W));
        vertices[4] = CreateQuadVertex(quadVerts[3].X * halfW, quadVerts[3].Y * halfH, 0, MapU(quadVerts[3].Z), MapV(quadVerts[3].W));
        vertices[5] = CreateQuadVertex(quadVerts[2].X * halfW, quadVerts[2].Y * halfH, 0, MapU(quadVerts[2].Z), MapV(quadVerts[2].W));

        Device.ResourceManager.UpdateBuffer(
            _vertexBuffer,
            (uint)(vertices.Length * Unsafe.SizeOf<Vertex>()),
            vertices);
    }

    public void Update()
    {
        float alpha;
        int clock, posX, posY, width, height;
        float sourceWidth, sourceHeight;
        bool flipX, flipY;
        Vector4 color;
        Season.Controls.TextureType textureType;
        float factor;

        if (SpriteRef != null)
        {
            // Zero-copy path: read directly from SpriteBase (Sprite2D / Shape).
            alpha = SpriteRef.Alpha;
            clock = SpriteRef.Clock;
            posX = (int)SpriteRef.PosX;
            posY = (int)SpriteRef.PosY;
            // Shape width/height may still be null on the first frame.
            // Treat null as 0, and the size gate below will return early.
            width = (int)(SpriteRef.Width ?? 0f);
            height = (int)(SpriteRef.Height ?? 0f);
            flipX = SpriteRef.FlipX;
            flipY = SpriteRef.FlipY;
            color = SpriteRef.Color;
            textureType = SpriteRef.TextureType;
            factor = SpriteRef.Factor;
            sourceWidth = SpriteRef.SourceWidth;
            sourceHeight = SpriteRef.SourceHeight;
        }
        else
        {
            // Texts / Shape path: read from the internal Texture.
            var t = Texture;
            alpha = t.Alpha;
            clock = t.Clock;
            posX = t.PosX;
            posY = t.PosY;
            width = t.Width;
            height = t.Height;
            flipX = t.FlipX;
            flipY = t.FlipY;
            color = t.Color;
            textureType = t.TextureType;
            factor = t.Factor;
            sourceWidth = t.SourceWidth;
            sourceHeight = t.SourceHeight;
        }

        // When alpha == 0 or size is not ready yet (Shape null -> 0), there is
        // nothing valid to draw. A later property change will trigger Update
        // again. This early return consumes no GPU resources.
        if (alpha == 0 || width <= 0 || height <= 0)
            return;

        QuadVerts = TextCoords.GetTransforms(clock, flipX, flipY);
        UploadQuadVertices();

        var extendRes = DeviceServices.BaseApp.ExtendResolution;
        var deviceRes = DeviceServices.BaseApp.DeviceResolution;
        var scale = DeviceServices.BaseApp.Scale;

        float n = 1f / DeviceServices.BaseApp.CompositionScale.X;

        float ndcPosX = posX * n;
        float ndcPosY = posY * n;
        float ndcWidth = width * n;
        float ndcHeight = height * n;

        // Convert screen coordinates with a top-left origin into NDC.
        float ndcX = (ndcPosX - extendRes.X / 2) / (extendRes.X / 2);
        float ndcY = (extendRes.Y / 2 - ndcPosY) / (extendRes.Y / 2);

        // Scaled width and height in NDC.
        float ndcScaledWidth = ndcWidth * scale / (deviceRes.X / 2);
        float ndcScaledHeight = ndcHeight * scale / (deviceRes.Y / 2);

        uint renderMode = textureType == Season.Controls.TextureType.TextMsdf ? 2u : 0u;

        var position = new Vector3(ndcX + ndcScaledWidth / 2, ndcY - ndcScaledHeight / 2, 0);
        var scaleVec = new Vector3(ndcScaledWidth, ndcScaledHeight, 1);

        var materialParams = new MaterialParams
        {
            BaseColor = new Vector4(color.X, color.Y, color.Z, color.W * alpha),
            EmissiveFactor = textureType == Season.Controls.TextureType.TextMsdf
                ? new Vector4(sourceWidth, sourceHeight, 0f, 0f)
                : Vector4.Zero,
            MetallicFactor = 0f,
            RoughnessFactor = 1f,
            UseAlbedoMap = 1u,
            UseNormalMap = NormalMap != null ? 1u : 0u,
            UseMetallicRoughnessMap = MetallicRoughnessMap != null ? 1u : 0u,
            UseOcclusionMap = OcclusionMap != null ? 1u : 0u,
            UseEmissiveMap = EmissiveMap != null ? 1u : 0u,
            RenderMode = renderMode,
            Padding1 = factor,
        };

        var scaleMatrix = Matrix4x4.CreateScale(scaleVec);
        var translationMatrix = Matrix4x4.CreateTranslation(position);
        var worldMatrix = scaleMatrix * translationMatrix;

        var matrices = new MatrixBuffer
        {
            World = Matrix4x4.Transpose(worldMatrix),
            View = Matrix4x4.Identity,
            Projection = Matrix4x4.Identity
        };

        int nFrames = (int)Device.frameCount;
        for (int i = 0; i < nFrames; i++)
        {
            Unsafe.Write(_mappedMaterialBuffers[i], materialParams);
            Unsafe.Write(_mappedMatrixBuffers[i], matrices);
        }

        _transformInitialized = true;
    }

    public void Draw()
    {
        if (!_transformInitialized)
            return;

        if (DXTexture == null || DXTexture._textureResource == null)
            return;

        fixed (VertexBufferView* vbv = &_vertexBufferView)
        {
            DrawQuad(vbv);
        }
    }

    public override void Dispose()
    {
        try
        {
            if (_vertexBuffer != null)
            {
                _vertexBuffer->Release();
                _vertexBuffer = null;
            }
        }
        catch
        {
            _vertexBuffer = null;
        }

        try
        {
            DisposeMatrixBuffers();
            DisposeMaterialBuffers();
        }
        catch
        {
        }

        // Note: SpriteRef and Texture are owned externally and are not released here.
    }
}
