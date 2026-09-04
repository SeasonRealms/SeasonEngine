// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Metal;
using MTLTexture = Season.Platforms.Shared.Apple.Metal.Texture;

namespace Season.Platforms.Shared.Apple.Metal;

/// <summary>
/// Metal backend 2D sprite aligned one to one with DX12 DXSprite2D and Vulkan VKSprite2D:
/// - screen coordinates with a top-left origin are mapped orthographically into NDC through the World matrix,
///   while View and Projection remain Identity
/// - each instance owns its own 6-vertex VB, leaving room for future quad UV and position transforms used by text rendering and similar paths
/// - renderMode = 0 selects the Sprite path, where the shader outputs BaseColor multiplied by the texture directly
/// - the globally shared static 2D orthographic camera matches DXSprite2D.Camera and VKSprite2D.Camera
/// </summary>
internal sealed class MTLSprite2D : MTLSpriteQuad
{
    // === Static 2D orthographic camera aligned with DXSprite2D.Camera and VKSprite2D.Camera ===
    internal static Camera Camera;

    // === Per-instance vertex buffer with 6 vertices ===
    IMTLBuffer _vertexBuffer = null!;

    // Texture-coordinate transforms used by text rendering, kept as an extension point and aligned with the DX and VK pattern.
    public Vector4[] QuadVerts = null!;

    // Original image size.
    public float ImageWidth { get; private set; }

    public float ImageHeight { get; private set; }

    // Zero-copy SpriteBase reference.
    // When non-null, Update reads control properties directly from Sprite2D or Shape with no copy.
    internal Season.Controls.SpriteBase? SpriteRef;

    // ============================================================
    // Static initialization and camera updates.
    // ============================================================

    public static void Init()
    {
        // The lighting UBO is already initialized by AppDelegate through MTLPrimitiveGroup.InitLights.
        // Only the 2D camera is handled here.
        Init2DCamera();
    }

    static void Init2DCamera()
    {
        // Use an orthographic camera for 2D rendering in a left-handed coordinate system with Z ranging from 0 to 1.
        Camera.View = Matrix4x4.Identity;
        Camera.Projection = Matrix4x4.CreateOrthographicOffCenterLeftHanded(
            -1f, 1f,   // left, right (NDC space)
            -1f, 1f,   // bottom, top (NDC space)
            0.1f, 10f); // near, far
    }

    public static void UpdateCamera(float time, Vector3 cameraPos, Vector3 cameraTarget, SceneLightParams lightParams)
    {
        // For 1-3, keep matrix construction aligned with MTLPrimitiveGroup.Update
        // by using shared Camera3D instead of introducing a second hard-coded matrix path.
        var camera3D = DeviceServices.BaseApp.Camera;
        var aspectRatio = DeviceServices.BaseApp.DeviceResolution.X / (float)DeviceServices.BaseApp.DeviceResolution.Y;
        camera3D.UpdateIfChanged(aspectRatio);
        Camera.View = camera3D.View;
        Camera.Projection = camera3D.Projection;

        // Write the lighting UBO.
        MTLPrimitiveGroup.SetLighting(lightParams);
    }

    public static void InitDispose()
    {
        MTLPrimitiveGroup.InitLightsDispose();
    }

    // ============================================================
    // Construction.
    // ============================================================

    /// <summary>
    /// Creates a sprite from an image path, with optional PBR textures kept as an extension point for future advanced material effects.
    /// </summary>
    public MTLSprite2D(string albedoPath, string? normalPath = null, string? metallicRoughnessPath = null,
                       string? occlusionPath = null, string? emissivePath = null)
    {
        Name = albedoPath;

        AlbedoTexture = MTLTexture.GetOrCreate(albedoPath, null);
        if (!string.IsNullOrEmpty(normalPath))
            NormalMap = MTLTexture.GetOrCreate(normalPath, null);
        if (!string.IsNullOrEmpty(metallicRoughnessPath))
            MetallicRoughnessMap = MTLTexture.GetOrCreate(metallicRoughnessPath, null);
        if (!string.IsNullOrEmpty(occlusionPath))
            OcclusionMap = MTLTexture.GetOrCreate(occlusionPath, null);
        if (!string.IsNullOrEmpty(emissivePath))
            EmissiveMap = MTLTexture.GetOrCreate(emissivePath, null);

        if (AlbedoTexture != null)
        {
            ImageWidth = AlbedoTexture.Width;
            ImageHeight = AlbedoTexture.Height;
        }

        CreateGPUResources();
        InitializeQuadVertices();
        InitializeMaterial();
    }

    /// <summary>
    /// Creates a sprite from an already uploaded Metal Texture through the GraphicsApple dictionary facade path.
    /// </summary>
    internal MTLSprite2D(MTLTexture texture)
    {
        Texture = new Season.Controls.Texture();
        AlbedoTexture = texture;

        if (AlbedoTexture != null)
        {
            ImageWidth = AlbedoTexture.Width;
            ImageHeight = AlbedoTexture.Height;
        }

        CreateGPUResources();
        InitializeQuadVertices();
        InitializeMaterial();
    }

    // ============================================================
    // GPU resources and vertex initialization.
    // ============================================================

    void CreateGPUResources()
    {
        // Vertex buffer: 6 vertices forming 2 triangles.
        _vertexBuffer = Device.ResourceManager.CreateVertexBuffer<Vertex>(6);

        CreateMatrixBuffer();
        CreateMaterialBuffer();
    }

    void InitializeQuadVertices()
    {
        UploadQuadVertices();
    }

    void UploadQuadVertices()
    {
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
        else if (Texture != null)
        {
            clock = Texture.Clock;
            flipX = Texture.FlipX;
            flipY = Texture.FlipY;
            sourceX = Texture.SourceX;
            sourceY = Texture.SourceY;
            sourceWidth = Texture.SourceWidth;
            sourceHeight = Texture.SourceHeight;
            textureType = Texture.TextureType;
        }
        else
        {
            // Constructor path:
            // Texture and SpriteRef are not assigned yet, so use a simple full-texture quad.
            var vertices0 = new Vertex[6];
            vertices0[0] = CreateQuadVertex(-halfW, halfH, 0, 0, 0);
            vertices0[1] = CreateQuadVertex(halfW, halfH, 0, 1, 0);
            vertices0[2] = CreateQuadVertex(-halfW, -halfH, 0, 0, 1);
            vertices0[3] = CreateQuadVertex(halfW, halfH, 0, 1, 0);
            vertices0[4] = CreateQuadVertex(halfW, -halfH, 0, 1, 1);
            vertices0[5] = CreateQuadVertex(-halfW, -halfH, 0, 0, 1);
            Device.ResourceManager.UpdateBuffer(_vertexBuffer, vertices0);
            return;
        }

        var quadVerts = QuadVerts ?? TextCoords.GetTransforms(clock, flipX, flipY);
        float textureWidth = AlbedoTexture != null && AlbedoTexture.Width > 0 ? AlbedoTexture.Width : 1f;
        float textureHeight = AlbedoTexture != null && AlbedoTexture.Height > 0 ? AlbedoTexture.Height : 1f;
        bool insetTextAtlasUv = textureType == Season.Controls.TextureType.TextMsdf && sourceWidth > 1 && sourceHeight > 1;
        float mapSourceX = sourceWidth > 0 ? (sourceX + (insetTextAtlasUv ? 0.5f : 0f)) / textureWidth : 0f;
        float mapSourceY = sourceHeight > 0 ? (sourceY + (insetTextAtlasUv ? 0.5f : 0f)) / textureHeight : 0f;
        float mapSourceWidth = sourceWidth > 0 ? (sourceWidth - (insetTextAtlasUv ? 1f : 0f)) / textureWidth : 1f;
        float mapSourceHeight = sourceHeight > 0 ? (sourceHeight - (insetTextAtlasUv ? 1f : 0f)) / textureHeight : 1f;

        float MapU(float u) => mapSourceX + u * mapSourceWidth;
        float MapV(float v) => mapSourceY + v * mapSourceHeight;

        var vertices = new Vertex[6];
        vertices[0] = CreateQuadVertex(quadVerts[0].X * halfW, quadVerts[0].Y * halfH, 0, MapU(quadVerts[0].Z), MapV(quadVerts[0].W));
        vertices[1] = CreateQuadVertex(quadVerts[1].X * halfW, quadVerts[1].Y * halfH, 0, MapU(quadVerts[1].Z), MapV(quadVerts[1].W));
        vertices[2] = CreateQuadVertex(quadVerts[2].X * halfW, quadVerts[2].Y * halfH, 0, MapU(quadVerts[2].Z), MapV(quadVerts[2].W));
        vertices[3] = CreateQuadVertex(quadVerts[1].X * halfW, quadVerts[1].Y * halfH, 0, MapU(quadVerts[1].Z), MapV(quadVerts[1].W));
        vertices[4] = CreateQuadVertex(quadVerts[3].X * halfW, quadVerts[3].Y * halfH, 0, MapU(quadVerts[3].Z), MapV(quadVerts[3].W));
        vertices[5] = CreateQuadVertex(quadVerts[2].X * halfW, quadVerts[2].Y * halfH, 0, MapU(quadVerts[2].Z), MapV(quadVerts[2].W));

        Device.ResourceManager.UpdateBuffer(_vertexBuffer, vertices);
    }

    // ============================================================
    // Update: screen coordinates to NDC, then write N-buffered Matrix and Material data.
    // ============================================================

    public void Update()
    {
        float alpha;
        int clock, posX, posY, width, height;
        bool flipX, flipY;
        Vector4 color;
        Season.Controls.TextureType textureType;
        float factor;

        if (SpriteRef != null)
        {
            // Zero-copy path: read directly from SpriteBase, meaning Sprite2D or Shape.
            alpha = SpriteRef.Alpha;
            clock = SpriteRef.Clock;
            posX = (int)SpriteRef.PosX;
            posY = (int)SpriteRef.PosY;
            // On the first frame, Shape size may still be null.
            // Converting null to 0 is safe because the size gate below returns early.
            width = (int)(SpriteRef.Width ?? 0f);
            height = (int)(SpriteRef.Height ?? 0f);
            flipX = SpriteRef.FlipX;
            flipY = SpriteRef.FlipY;
            color = SpriteRef.Color;
            textureType = SpriteRef.TextureType;
            factor = SpriteRef.Factor;
        }
        else
        {
            // Texts and Shape path: read from the internal Texture.
            if (Texture == null || Texture.Alpha == 0)
                return;

            alpha = Texture.Alpha;
            clock = Texture.Clock;
            posX = Texture.PosX;
            posY = Texture.PosY;
            width = Texture.Width;
            height = Texture.Height;
            flipX = Texture.FlipX;
            flipY = Texture.FlipY;
            color = Texture.Color;
            textureType = Texture.TextureType;
            factor = Texture.Factor;
        }

        // When alpha is zero or size is not ready, such as Shape null becoming 0,
        // there is no valid drawable content yet.
        // A later property change triggers Update again, and this early return consumes no GPU resources.
        if (alpha == 0 || width <= 0 || height <= 0)
            return;

        QuadVerts = TextCoords.GetTransforms(clock, flipX, flipY);
        UploadQuadVertices();

        var extendRes = DeviceServices.BaseApp.ExtendResolution;
        var deviceRes = DeviceServices.BaseApp.DeviceResolution;
        var scale = DeviceServices.BaseApp.Scale;

        // Convert screen coordinates with a top-left origin into NDC.
        float ndcX = (posX - extendRes.X / 2) / (extendRes.X / 2);
        float ndcY = (extendRes.Y / 2 - posY) / (extendRes.Y / 2);

        // Scaled width and height in NDC.
        float ndcWidth = width * scale / (deviceRes.X / 2);
        float ndcHeight = height * scale / (deviceRes.Y / 2);

        var position = new Vector3(ndcX + ndcWidth / 2, ndcY - ndcHeight / 2, 0);
        var scaleVec = new Vector3(ndcWidth, ndcHeight, 1);

        uint renderMode = textureType == TextureType.TextMsdf ? 2u : 0u;

        // Material UBO: color, transparency, and PBR flags.
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
            AlphaMode = 2u,   // BLEND. Sprites use the Transparent PSO.
            RenderMode = renderMode,
            Padding1 = factor
        };

        // Matrix UBO: 2D orthographic path using NDC directly, with View and Projection both set to identity.
        var scaleMatrix = Matrix4x4.CreateScale(scaleVec);
        var translationMatrix = Matrix4x4.CreateTranslation(position);
        var worldMatrix = scaleMatrix * translationMatrix;

        var matrices = new MatrixBuffer
        {
            World = Matrix4x4.Transpose(worldMatrix),
            View = Matrix4x4.Identity,
            Projection = Matrix4x4.Identity
        };

        // A static sprite only needs one logical write, but mirror it into all frames
        // so other N-buffered frames never read stale values.
        int n = Device.frameCount;
        for (int i = 0; i < n; i++)
        {
            WriteStruct(_materialBuffers[i], materialParams);
            WriteStruct(_matrixBuffers[i], matrices);
        }

        _transformInitialized = true;
    }

    // ============================================================
    // Draw / Dispose
    // ============================================================

    public void Draw()
    {
        if (!_transformInitialized) return;
        if (AlbedoTexture == null) return;

        DrawQuad(_vertexBuffer);
    }

    public override void Dispose()
    {
        _vertexBuffer?.Dispose();
        _vertexBuffer = null!;
        DisposeCommon();
    }
}
