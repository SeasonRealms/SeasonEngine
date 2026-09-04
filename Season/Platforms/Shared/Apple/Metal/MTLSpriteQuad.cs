// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Metal;
using System.Runtime.CompilerServices;
using MTLTexture = Season.Platforms.Shared.Apple.Metal.Texture;

namespace Season.Platforms.Shared.Apple.Metal;

/// <summary>
/// Common base class for MTLSprite2D and MTLSprite3D, aligned one to one with DX12 DXSpriteQuad and Vulkan VKSpriteQuad:
/// - N-buffered Matrix UBO at VS buffer slot 1, matching Vulkan b0
/// - N-buffered Material UBO at FS buffer slot 2, matching Vulkan b2
/// - main texture plus four auxiliary PBR textures at FS texture slots 0 through 4
/// - globally shared lighting UBO and IdentityBone UBO reused from static fields on MTLPrimitiveGroup at FS slot 1 and VS slot 3
/// - DrawQuad template using the Transparent PSO and DrawPrimitives(Triangle, 0, 6) with no IB
///
/// Simplifications relative to Vulkan:
/// - Metal does not need DescriptorSet objects because each frame binds directly through SetVertexBuffer, SetFragmentBuffer, and SetFragmentTexture
/// - StorageMode.Private synchronizes automatically, so there is no EnsureReadyForRendering layout transition
/// - IMTLBuffer.Contents is a persistent IntPtr, so no separate mapped-pointer arrays are needed and each frame can write directly to buffer.Contents
/// </summary>
internal abstract class MTLSpriteQuad : ITextureHolder, IDisposable
{
    // === Sprite identifier, usually the main texture path ===
    internal string Name = string.Empty;

    // === Matrix and Material UBOs, N-buffered and synchronized with the frame ring ===
    protected IMTLBuffer[] _matrixBuffers = null!;
    protected IMTLBuffer[] _materialBuffers = null!;

    // === Main texture ===
    internal MTLTexture? AlbedoTexture;

    // === Auxiliary PBR textures kept as an extension point ===
    internal MTLTexture? NormalMap;
    internal MTLTexture? MetallicRoughnessMap;
    internal MTLTexture? OcclusionMap;
    internal MTLTexture? EmissiveMap;

    // === ITextureHolder ===
    Season.Controls.Texture _texture = null!;
    public Season.Controls.Texture Texture
    {
        get => _texture;
        set => _texture = value;
    }

    // === First-frame guard: skip Draw until the first Update has finished ===
    protected bool _transformInitialized;

    // ============================================================
    // Per-instance UBO creation and release.
    // ============================================================

    protected void CreateMatrixBuffer()
    {
        int n = Device.frameCount;
        _matrixBuffers = new IMTLBuffer[n];
        for (int i = 0; i < n; i++)
            _matrixBuffers[i] = Device.ResourceManager.CreateConstantBuffer((nuint)Unsafe.SizeOf<MatrixBuffer>());

        // Initialize with identity matrices so frames that have not been updated yet never read garbage under N-buffering.
        var identity = new MatrixBuffer
        {
            World = Matrix4x4.Transpose(Matrix4x4.Identity),
            View = Matrix4x4.Identity,
            Projection = Matrix4x4.Identity
        };
        for (int i = 0; i < n; i++) WriteStruct(_matrixBuffers[i], identity);
    }

    protected void CreateMaterialBuffer()
    {
        int n = Device.frameCount;
        _materialBuffers = new IMTLBuffer[n];
        for (int i = 0; i < n; i++)
            _materialBuffers[i] = Device.ResourceManager.CreateConstantBuffer((nuint)Unsafe.SizeOf<MaterialParams>());
    }

    protected void DisposeMatrixBuffers()
    {
        if (_matrixBuffers != null)
        {
            for (int i = 0; i < _matrixBuffers.Length; i++) _matrixBuffers[i]?.Dispose();
            _matrixBuffers = null!;
        }
    }

    protected void DisposeMaterialBuffers()
    {
        if (_materialBuffers != null)
        {
            for (int i = 0; i < _materialBuffers.Length; i++) _materialBuffers[i]?.Dispose();
            _materialBuffers = null!;
        }
    }

    /// <summary>Writes any unmanaged struct directly into IMTLBuffer.Contents, avoiding a separate mapped-pointer cache.</summary>
    protected static unsafe void WriteStruct<T>(IMTLBuffer buffer, T value, nuint offset = 0) where T : unmanaged
    {
        *(T*)((byte*)buffer.Contents + (long)offset) = value;
    }

    /// <summary>Reads any unmanaged struct directly from IMTLBuffer.Contents.</summary>
    protected static unsafe T ReadStruct<T>(IMTLBuffer buffer, nuint offset = 0) where T : unmanaged
    {
        return *(T*)((byte*)buffer.Contents + (long)offset);
    }

    // ============================================================
    // Quad vertex and shared material initialization.
    // ============================================================

    protected static Vertex CreateQuadVertex(float x, float y, float z, float u, float v)
    {
        return new Vertex
        {
            Position = new Vector3(x, y, z),
            TexCoord = new Vector2(u, v),
            Normal = new Vector3(0, 0, -1),  // Faces the camera.
            Tangent = new Vector4(1, 0, 0, 1),
            Joints = Vector4.Zero,
            Weights = Vector4.Zero
        };
    }

    protected void InitializeMaterial(uint renderMode = 0u)
    {
        var materialParams = new MaterialParams
        {
            BaseColor = new Vector4(1, 1, 1, 1),
            EmissiveFactor = Vector4.Zero,
            MetallicFactor = 0f,
            RoughnessFactor = 1f,
            UseAlbedoMap = AlbedoTexture != null ? 1u : 0u,
            UseNormalMap = NormalMap != null ? 1u : 0u,
            UseMetallicRoughnessMap = MetallicRoughnessMap != null ? 1u : 0u,
            UseOcclusionMap = OcclusionMap != null ? 1u : 0u,
            UseEmissiveMap = EmissiveMap != null ? 1u : 0u,
            AlphaCutoff = 0.5f,
            AlphaMode = 2u, // BLEND. Sprites use the Transparent PSO.
            RenderMode = renderMode
        };

        for (int i = 0; i < _materialBuffers.Length; i++)
            WriteStruct(_materialBuffers[i], materialParams);
    }

    // ============================================================
    // PBR texture setters. No DescriptorSet work is needed anymore, only Material flag refresh.
    // ============================================================

    public void SetNormalMap(string normalPath)
    {
        if (!string.IsNullOrEmpty(normalPath))
        {
            NormalMap = MTLTexture.GetOrCreate(normalPath, null);
            UpdateMaterialFlags();
        }
    }

    public void SetMetallicRoughnessMap(string metallicRoughnessPath)
    {
        if (!string.IsNullOrEmpty(metallicRoughnessPath))
        {
            MetallicRoughnessMap = MTLTexture.GetOrCreate(metallicRoughnessPath, null);
            UpdateMaterialFlags();
        }
    }

    public void SetOcclusionMap(string occlusionPath)
    {
        if (!string.IsNullOrEmpty(occlusionPath))
        {
            OcclusionMap = MTLTexture.GetOrCreate(occlusionPath, null);
            UpdateMaterialFlags();
        }
    }

    public void SetEmissiveMap(string emissivePath)
    {
        if (!string.IsNullOrEmpty(emissivePath))
        {
            EmissiveMap = MTLTexture.GetOrCreate(emissivePath, null);
            UpdateMaterialFlags();
        }
    }

    void UpdateMaterialFlags()
    {
        // Update only texture-usage flags and preserve the remaining material parameters.
        int fi = Device.FrameIndex;
        var current = ReadStruct<MaterialParams>(_materialBuffers[fi]);
        current.UseAlbedoMap = AlbedoTexture != null ? 1u : 0u;
        current.UseNormalMap = NormalMap != null ? 1u : 0u;
        current.UseMetallicRoughnessMap = MetallicRoughnessMap != null ? 1u : 0u;
        current.UseOcclusionMap = OcclusionMap != null ? 1u : 0u;
        current.UseEmissiveMap = EmissiveMap != null ? 1u : 0u;
        WriteStruct(_materialBuffers[fi], current);
    }

    /// <summary>Sets material parameters such as metallic factor, roughness factor, and emissive factor.</summary>
    public void SetMaterialParams(float metallicFactor = 0f, float roughnessFactor = 1f, Vector4? emissiveFactor = null)
    {
        int fi = Device.FrameIndex;
        var current = ReadStruct<MaterialParams>(_materialBuffers[fi]);
        current.MetallicFactor = metallicFactor;
        current.RoughnessFactor = roughnessFactor;
        if (emissiveFactor.HasValue)
            current.EmissiveFactor = emissiveFactor.Value;
        WriteStruct(_materialBuffers[fi], current);
    }

    // ============================================================
    // Draw template: Transparent PSO plus DrawPrimitives(Triangle, 0, 6).
    // ============================================================

    protected void DrawQuad(IMTLBuffer vb)
    {
        var enc = Device.GraphicsEncoder;

        // Transparent PSO plus DSS, static sampler, and cull/winding state.
        Pipeline.SetPipeline(enc, PipelineMode.Transparent);

        int fi = Device.FrameIndex;
        var fallback = Device.White;

        // VS buffer slots:
        // 0 = vertex stream, 1 = Matrices(b0), 2 = Instance(buff2), 3 = BoneMatrices(b3 using shared IdentityBone).
        enc.SetVertexBuffer(vb, 0, 0);
        enc.SetVertexBuffer(_matrixBuffers[fi], 0, 1);
        enc.SetVertexBuffer(Pipeline.IdentityInstanceBuffer, 0, 2);
        enc.SetVertexBuffer(MTLPrimitiveGroup.IdentityBoneBuffers[fi], 0, 3);
        // VS buffer(4) carries MaterialParams because the vertex shader depends on isInstanced and renderMode
        // when choosing the world-matrix path and UV branch.
        // It must be bound explicitly here, or residual state from the previous draw,
        // such as isInstanced = 1 from an instanced 3D model, could leak in.
        enc.SetVertexBuffer(_materialBuffers[fi], 0, 4);

        // FS buffer slots: 1=SceneLights(b1), 2=MaterialParams(b2)
        enc.SetFragmentBuffer(MTLPrimitiveGroup.LightConstantBuffers[fi], 0, 1);
        enc.SetFragmentBuffer(_materialBuffers[fi], 0, 2);

        // FS texture slots: 0=BaseColor, 1=Normal, 2=MR, 3=AO, 4=Emissive
        enc.SetFragmentTexture((AlbedoTexture ?? fallback).Image, 0);
        enc.SetFragmentTexture((NormalMap ?? fallback).Image, 1);
        enc.SetFragmentTexture((MetallicRoughnessMap ?? fallback).Image, 2);
        enc.SetFragmentTexture((OcclusionMap ?? fallback).Image, 3);
        enc.SetFragmentTexture((EmissiveMap ?? fallback).Image, 4);

        enc.DrawPrimitives(MTLPrimitiveType.Triangle, 0, 6);
    }

    // ============================================================
    // Resource release for the common base-class portion.
    // ============================================================

    protected void DisposeCommon()
    {
        DisposeMatrixBuffers();
        DisposeMaterialBuffers();
        // Note:
        // Texture lifetime is managed centrally by Device.DictionaryTexture and is not released here.
    }

    public abstract void Dispose();
}
