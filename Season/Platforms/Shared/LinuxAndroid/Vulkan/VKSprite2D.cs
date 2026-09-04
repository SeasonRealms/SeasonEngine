// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Vulkan;
using System.Runtime.CompilerServices;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkTexture = Season.Platforms.Shared.LinuxAndroid.Vulkan.Texture;

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

/// <summary>
/// Vulkan backend 2D sprite aligned 1:1 with DXSprite2D:
///   - Orthographic mapping from screen coordinates (top-left origin) to NDC is carried by the World matrix
///     (View/Projection are both Identity)
///   - Each instance owns its 6-vertex VB
///     (preserving the extension point for quad UV/position transforms used by text rendering and similar paths)
///   - renderMode = 0 (Sprite path), and the shader outputs BaseColor x texture directly
///   - Global shared static field for the 2D orthographic camera
///     (equivalent to DXSprite2D.Camera)
/// </summary>
internal unsafe class VKSprite2D : VKSpriteQuad
{
    // 2D orthographic camera (static, aligned with DXSprite2D.Camera)
    internal static Season.Basic.Camera Camera;

    // Per-instance vertex buffer
    private BufferResource _vertexBuffer;

    // Texture-coordinate transforms for text rendering
    // (kept as an extension point, matching the DX design)
    public Vector4[] QuadVerts = null!;

    // Original image size
    public float ImageWidth { get; private set; }
    public float ImageHeight { get; private set; }

    // Zero-copy SpriteBase reference:
    // when non-null, Update() reads control properties directly (Sprite2D / Shape) with no copying
    internal Season.Controls.SpriteBase? SpriteRef;

    // ============================================================
    // Static initialization / camera update
    // ============================================================

    public static void Init()
    {
        // The lighting UBO has already been initialized by LinuxAndroidApp through
        // VKPrimitiveGroup.InitLights(); this path only handles the 2D camera.
        Init2DCamera();
    }

    static void Init2DCamera()
    {
        // Orthographic camera used for 2D rendering; left-handed coordinate system, Z in 0..1
        Camera.View = Matrix4x4.Identity;
        Camera.Projection = Matrix4x4.CreateOrthographicOffCenterLeftHanded(
            -1f, 1f,   // left, right (NDC space)
            -1f, 1f,   // bottom, top (NDC space)
            0.1f, 10f); // near, far
    }

    public static void UpdateCamera(float time, Vector3 cameraPos, Vector3 cameraTarget, SceneLightParams lightParams)
    {
        // 1-3: shares the same source as VKPrimitiveGroup.Update -
        // matrix construction is unified through the shared Camera3D layer
        // to avoid inconsistent matrices from a second hardcoded parameter path.
        var camera3D = DeviceServices.BaseApp.Camera;
        var aspectRatio = DeviceServices.BaseApp.DeviceResolution.X / (float)DeviceServices.BaseApp.DeviceResolution.Y;
        camera3D.UpdateIfChanged(aspectRatio);
        Camera.View = camera3D.View;
        Camera.Projection = camera3D.Projection;

        // Write the lighting UBO
        VKPrimitiveGroup.SetLighting(lightParams);
    }

    public static void InitDispose()
    {
        VKPrimitiveGroup.InitLightsDispose();
    }

    // ============================================================
    // Construction
    // ============================================================

    /// <summary>
    /// Create a Sprite from an image path
    /// (including optional PBR textures as an extension point for future advanced material effects).
    /// </summary>
    public VKSprite2D(string albedoPath, string? normalPath = null, string? metallicRoughnessPath = null,
                      string? occlusionPath = null, string? emissivePath = null)
    {
        Name = albedoPath;

        VKTexture = VkTexture.GetOrCreate(albedoPath, null);
        if (!string.IsNullOrEmpty(normalPath))
            NormalMap = VkTexture.GetOrCreate(normalPath, null);
        if (!string.IsNullOrEmpty(metallicRoughnessPath))
            MetallicRoughnessMap = VkTexture.GetOrCreate(metallicRoughnessPath, null);
        if (!string.IsNullOrEmpty(occlusionPath))
            OcclusionMap = VkTexture.GetOrCreate(occlusionPath, null);
        if (!string.IsNullOrEmpty(emissivePath))
            EmissiveMap = VkTexture.GetOrCreate(emissivePath, null);

        if (VKTexture != null)
        {
            ImageWidth = VKTexture.Width;
            ImageHeight = VKTexture.Height;
        }

        CreateGPUResources();
        InitializeQuadVertices();
        InitializeMaterial();
        AllocateAndWriteDescriptorSets();
    }

    /// <summary>
    /// Create a Sprite from a VK Texture (GraphicsLinuxAndroid integration path).
    /// </summary>
    internal VKSprite2D(VkTexture vkTexture)
    {
        Texture = new Season.Controls.Texture();
        VKTexture = vkTexture;

        if (VKTexture != null)
        {
            ImageWidth = VKTexture.Width;
            ImageHeight = VKTexture.Height;
        }

        CreateGPUResources();
        InitializeQuadVertices();
        InitializeMaterial();
        AllocateAndWriteDescriptorSets();
    }

    // ============================================================
    // GPU resources + vertex initialization
    // ============================================================

    void CreateGPUResources()
    {
        // Vertex buffer: 6 vertices forming 2 triangles
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
        // Unit quad centered at the origin (width and height are both 1);
        // actual size is applied through Scale during Update
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
        float textureWidth = VKTexture != null && VKTexture.Width > 0 ? VKTexture.Width : 1f;
        float textureHeight = VKTexture != null && VKTexture.Height > 0 ? VKTexture.Height : 1f;
        bool insetTextAtlasUv = textureType == Season.Controls.TextureType.TextMsdf && sourceWidth > 1 && sourceHeight > 1;
        float mapSourceX = sourceWidth > 0 ? (sourceX + (insetTextAtlasUv ? 0.5f : 0f)) / textureWidth : 0f;
        float mapSourceY = sourceHeight > 0 ? (sourceY + (insetTextAtlasUv ? 0.5f : 0f)) / textureHeight : 0f;
        float mapSourceWidth = sourceWidth > 0 ? (sourceWidth - (insetTextAtlasUv ? 1f : 0f)) / textureWidth : 1f;
        float mapSourceHeight = sourceHeight > 0 ? (sourceHeight - (insetTextAtlasUv ? 1f : 0f)) / textureHeight : 1f;

        float MapU(float u) => mapSourceX + u * mapSourceWidth;
        float MapV(float v) => mapSourceY + v * mapSourceHeight;

        var vertices = new Vertex[6];
        // First triangle (top-left -> top-right -> bottom-left)
        vertices[0] = CreateQuadVertex(quadVerts[0].X * halfW, quadVerts[0].Y * halfH, 0, MapU(quadVerts[0].Z), MapV(quadVerts[0].W));
        vertices[1] = CreateQuadVertex(quadVerts[1].X * halfW, quadVerts[1].Y * halfH, 0, MapU(quadVerts[1].Z), MapV(quadVerts[1].W));
        vertices[2] = CreateQuadVertex(quadVerts[2].X * halfW, quadVerts[2].Y * halfH, 0, MapU(quadVerts[2].Z), MapV(quadVerts[2].W));
        // Second triangle (top-right -> bottom-right -> bottom-left)
        vertices[3] = CreateQuadVertex(quadVerts[1].X * halfW, quadVerts[1].Y * halfH, 0, MapU(quadVerts[1].Z), MapV(quadVerts[1].W));
        vertices[4] = CreateQuadVertex(quadVerts[3].X * halfW, quadVerts[3].Y * halfH, 0, MapU(quadVerts[3].Z), MapV(quadVerts[3].W));
        vertices[5] = CreateQuadVertex(quadVerts[2].X * halfW, quadVerts[2].Y * halfH, 0, MapU(quadVerts[2].Z), MapV(quadVerts[2].W));

        Device.ResourceManager.UpdateBuffer(_vertexBuffer, vertices);
    }

    // ============================================================
    // Update: screen coordinates -> NDC, then write N-buffered Matrix/Material data
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
            // Zero-copy path: read directly from SpriteBase (Sprite2D / Shape)
            alpha = SpriteRef.Alpha;
            clock = SpriteRef.Clock;
            posX = (int)SpriteRef.PosX;
            posY = (int)SpriteRef.PosY;
            // Shape size may still be unset (null) on the first frame:
            // null -> 0 is accepted, and the size guard below exits early
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
            // Texts / Shape path: read from the internal Texture
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

        // alpha == 0 or size not ready yet (Shape null -> 0):
        // there is no valid drawable content, so wait for a property change
        // to trigger Update again (the early return consumes no GPU resources)
        if (alpha == 0 || width <= 0 || height <= 0)
            return;

        QuadVerts = TextCoords.GetTransforms(clock, flipX, flipY);
        InitializeQuadVertices();

        var extendRes = DeviceServices.BaseApp.ExtendResolution;
        var deviceRes = DeviceServices.BaseApp.DeviceResolution;
        var scale = DeviceServices.BaseApp.Scale;

        // Screen coordinates (top-left origin) -> NDC
        float ndcX = (posX - extendRes.X / 2) / (extendRes.X / 2);
        float ndcY = (extendRes.Y / 2 - posY) / (extendRes.Y / 2);

        // Scaled width and height in NDC
        float ndcWidth = width * scale / (deviceRes.X / 2);
        float ndcHeight = height * scale / (deviceRes.Y / 2);

        var position = new Vector3(ndcX + ndcWidth / 2, ndcY - ndcHeight / 2, 0);
        var scaleVec = new Vector3(ndcWidth, ndcHeight, 1);

        uint renderMode = textureType == TextureType.TextMsdf ? 2u : 0u;

        // Material UBO: color + opacity + PBR feature flags
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
            AlphaMode = 2u,   // BLEND (Sprite uses the Transparent PSO)
            RenderMode = renderMode,
            Padding1 = factor
        };

        // Matrix UBO: 2D orthographic path using NDC directly
        // (View/Projection are both identity matrices)
        var scaleMatrix = Matrix4x4.CreateScale(scaleVec);
        var translationMatrix = Matrix4x4.CreateTranslation(position);
        var worldMatrix = scaleMatrix * translationMatrix;

        var matrices = new MatrixBuffer
        {
            World = Matrix4x4.Transpose(worldMatrix),
            View = Matrix4x4.Identity,
            Projection = Matrix4x4.Identity
        };

        // Static sprites only need one logical write:
        // mirror it to all frames to avoid stale values in the other N-buffered frames
        int n = (int)Device.frameCount;
        for (int i = 0; i < n; i++)
        {
            Unsafe.Write(_mappedMaterialBuffers[i], materialParams);
            Unsafe.Write(_mappedMatrixBuffers[i], matrices);
        }

        _transformInitialized = true;
    }

    // ============================================================
    // Draw / Dispose
    // ============================================================

    public void Draw()
    {
        if (!_transformInitialized) return;
        if (VKTexture == null) return;

        DrawQuad(_vertexBuffer.Buffer);
    }

    public override void Dispose()
    {
        if (_vertexBuffer.Buffer.Handle != 0)
        {
            Device.ResourceManager?.DestroyBuffer(_vertexBuffer);
            _vertexBuffer = default;
        }
        DisposeCommon();
    }
}
