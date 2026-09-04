// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Direct3D12;

namespace Season.Platforms.Windows.DirectX;

/// <summary>
/// Common base class for the Sprite family (DXSprite2D / DXSprite3D).
/// Holds the shared resources that do not depend on VB strategy or Update semantics:
///   - Matrix / Material constant buffers (N-buffered)
///   - Main texture plus auxiliary PBR maps
///     (Normal / MetallicRoughness / Occlusion / Emissive)
///   - The shared lighting CB used by all sprites
///   - The Draw skeleton template
/// Derived classes only need to care about vertex-buffer strategy and Update
/// semantics. DXSprite2D additionally owns the 2D orthographic camera.
/// </summary>
internal unsafe abstract class DXSpriteQuad : ITextureHolder, IDisposable
{
    // === Sprite identifier (usually the main texture path, used for cache keys and logs) ===
    internal string Name;

    // The globally shared lighting CB has been unified in DXPrimitiveGroup,
    // which shares the same buffer with the Pbr3D path.

    // === Matrix / Material CBs (N-buffered) ===
    protected ID3D12Resource*[] _matrixBuffers;
    protected byte*[] _mappedMatrixBuffers;

    protected ID3D12Resource*[] _materialBuffers;
    protected byte*[] _mappedMaterialBuffers;

    // === Main texture ===
    internal DXTexture DXTexture;

    // === Auxiliary PBR maps (kept for future advanced material effects) ===
    internal DXTexture NormalMap;
    internal DXTexture MetallicRoughnessMap;
    internal DXTexture OcclusionMap;
    internal DXTexture EmissiveMap;

    // === ITextureHolder ===
    private Season.Controls.Texture _texture;
    public Season.Controls.Texture Texture
    {
        get => _texture;
        set => _texture = value;
    }

    // === First-frame guard: skip Draw until the first Update completes to
    // avoid misalignment from identity matrices ===
    protected bool _transformInitialized;

    // === Static lighting-CB lifetime is now managed by DXPrimitiveGroup;
    // DXSpriteQuad only reads it in DrawQuad. ===

    // === Per-instance CB creation / disposal ===
    protected void CreateMatrixBuffer()
    {
        int n = (int)Device.frameCount;
        _matrixBuffers = new ID3D12Resource*[n];
        _mappedMatrixBuffers = new byte*[n];
        for (int i = 0; i < n; i++)
            _matrixBuffers[i] = Device.ResourceManager.CreateConstantBuffer(
                (uint)Unsafe.SizeOf<MatrixBuffer>(),
                out _mappedMatrixBuffers[i]);

        // Initialize with identity matrices so frames that have not been
        // updated yet never read garbage under N-buffering.
        var identity = new MatrixBuffer
        {
            World = Matrix4x4.Transpose(Matrix4x4.Identity),
            View = Matrix4x4.Identity,
            Projection = Matrix4x4.Identity
        };
        for (int i = 0; i < n; i++)
            Unsafe.Write(_mappedMatrixBuffers[i], identity);
    }

    protected void CreateMaterialBuffer()
    {
        int n = (int)Device.frameCount;
        _materialBuffers = new ID3D12Resource*[n];
        _mappedMaterialBuffers = new byte*[n];
        for (int i = 0; i < n; i++)
            _materialBuffers[i] = Device.ResourceManager.CreateConstantBuffer(
                (uint)Unsafe.SizeOf<MaterialParams>(),
                out _mappedMaterialBuffers[i]);
    }

    protected void DisposeMatrixBuffers()
    {
        if (_matrixBuffers != null)
        {
            for (int i = 0; i < _matrixBuffers.Length; i++)
            {
                if (_matrixBuffers[i] != null)
                {
                    _matrixBuffers[i]->Unmap(0, null);
                    _matrixBuffers[i]->Release();
                    _matrixBuffers[i] = null;
                }
            }
            _matrixBuffers = null;
            _mappedMatrixBuffers = null;
        }
    }

    protected void DisposeMaterialBuffers()
    {
        if (_materialBuffers != null)
        {
            for (int i = 0; i < _materialBuffers.Length; i++)
            {
                if (_materialBuffers[i] != null)
                {
                    _materialBuffers[i]->Unmap(0, null);
                    _materialBuffers[i]->Release();
                    _materialBuffers[i] = null;
                }
            }
            _materialBuffers = null;
            _mappedMaterialBuffers = null;
        }
    }

    // === Quad vertex helper ===
    protected static Vertex CreateQuadVertex(float x, float y, float z, float u, float v)
    {
        return new Vertex
        {
            Position = new Vector3(x, y, z),
            TexCoord = new Vector2(u, v),
            Normal = new Vector3(0, 0, -1),  // Facing the camera
            Tangent = new Vector4(1, 0, 0, 1),
            Joints = Vector4.Zero,
            Weights = Vector4.Zero
        };
    }

    // === Shared material initialization ===
    protected void InitializeMaterial(uint renderMode = 0u)
    {
        var materialParams = new MaterialParams
        {
            BaseColor = new Vector4(1, 1, 1, 1),
            EmissiveFactor = Vector4.Zero,
            MetallicFactor = 0f,
            RoughnessFactor = 1f,
            UseAlbedoMap = DXTexture != null ? 1u : 0u,
            UseNormalMap = NormalMap != null ? 1u : 0u,
            UseMetallicRoughnessMap = MetallicRoughnessMap != null ? 1u : 0u,
            UseOcclusionMap = OcclusionMap != null ? 1u : 0u,
            UseEmissiveMap = EmissiveMap != null ? 1u : 0u,
            RenderMode = renderMode
        };

        for (int i = 0; i < _materialBuffers.Length; i++)
            Unsafe.Write(_mappedMaterialBuffers[i], materialParams);
    }

    // === PBR map setters (kept as a future material extension point) ===
    public void SetNormalMap(string normalPath)
    {
        if (!string.IsNullOrEmpty(normalPath))
        {
            NormalMap = DXTexture.GetOrCreate(normalPath, null);
            UpdateMaterialFlags();
        }
    }

    public void SetMetallicRoughnessMap(string metallicRoughnessPath)
    {
        if (!string.IsNullOrEmpty(metallicRoughnessPath))
        {
            MetallicRoughnessMap = DXTexture.GetOrCreate(metallicRoughnessPath, null);
            UpdateMaterialFlags();
        }
    }

    public void SetOcclusionMap(string occlusionPath)
    {
        if (!string.IsNullOrEmpty(occlusionPath))
        {
            OcclusionMap = DXTexture.GetOrCreate(occlusionPath, null);
            UpdateMaterialFlags();
        }
    }

    public void SetEmissiveMap(string emissivePath)
    {
        if (!string.IsNullOrEmpty(emissivePath))
        {
            EmissiveMap = DXTexture.GetOrCreate(emissivePath, null);
            UpdateMaterialFlags();
        }
    }

    private void UpdateMaterialFlags()
    {
        // Only update texture-usage flags and preserve all other material parameters.
        int fi = (int)Device.FrameIndex;
        var currentMaterial = Unsafe.Read<MaterialParams>(_mappedMaterialBuffers[fi]);
        currentMaterial.UseAlbedoMap = DXTexture != null ? 1u : 0u;
        currentMaterial.UseNormalMap = NormalMap != null ? 1u : 0u;
        currentMaterial.UseMetallicRoughnessMap = MetallicRoughnessMap != null ? 1u : 0u;
        currentMaterial.UseOcclusionMap = OcclusionMap != null ? 1u : 0u;
        currentMaterial.UseEmissiveMap = EmissiveMap != null ? 1u : 0u;
        Unsafe.Write(_mappedMaterialBuffers[fi], currentMaterial);
    }

    /// <summary>
    /// Sets material parameters such as metallic, roughness, and emissive factor.
    /// </summary>
    public void SetMaterialParams(float metallicFactor = 0f, float roughnessFactor = 1f, Vector4? emissiveFactor = null)
    {
        int fi = (int)Device.FrameIndex;
        var currentMaterial = Unsafe.Read<MaterialParams>(_mappedMaterialBuffers[fi]);
        currentMaterial.MetallicFactor = metallicFactor;
        currentMaterial.RoughnessFactor = roughnessFactor;
        if (emissiveFactor.HasValue)
            currentMaterial.EmissiveFactor = emissiveFactor.Value;
        Unsafe.Write(_mappedMaterialBuffers[fi], currentMaterial);
    }

    // === Draw template method ===
    /// <summary>
    /// Completes command-list binding for a Sprite quad: barriers, pipeline, VB,
    /// CBs, and descriptor tables t0-t4, then issues DrawInstanced(6,1,0,0).
    /// </summary>
    protected void DrawQuad(VertexBufferView* vbv)
    {
        // Ensure copy-queue uploads are complete and apply the
        // Common -> PixelShaderResource barrier.
        DXTexture.EnsureReadyForRendering(Device.GraphicsCommandList);
        NormalMap?.EnsureReadyForRendering(Device.GraphicsCommandList);
        MetallicRoughnessMap?.EnsureReadyForRendering(Device.GraphicsCommandList);
        OcclusionMap?.EnsureReadyForRendering(Device.GraphicsCommandList);
        EmissiveMap?.EnsureReadyForRendering(Device.GraphicsCommandList);

        // Set the transparent rendering pipeline.
        Pipeline.SetPipeline(PipelineMode.Transparent);

        // Bind vertex buffers: slot 0 = quad, slot 1 = identity instance
        // placeholder. The non-instanced shader path does not read slot 1, but
        // the input layout declares it, and leaving it unbound would spam Debug
        // Layer warning #202.
        var vertexViews = stackalloc VertexBufferView[2];
        vertexViews[0] = *vbv;
        vertexViews[1] = Pipeline.IdentityInstanceBufferView;
        Device.GraphicsCommandList->IASetVertexBuffers(0, 2, vertexViews);

        int fi = (int)Device.FrameIndex;

        // b0 - matrix CB
        Device.GraphicsCommandList->SetGraphicsRootConstantBufferView(
            0, _matrixBuffers[fi]->GetGPUVirtualAddress());

        // b1 - global lighting CB shared with the Pbr3D path
        Device.GraphicsCommandList->SetGraphicsRootConstantBufferView(
            1, DXPrimitiveGroup.lightConstantBuffers[fi]->GetGPUVirtualAddress());

        // b2 - material CB
        Device.GraphicsCommandList->SetGraphicsRootConstantBufferView(
            2, _materialBuffers[fi]->GetGPUVirtualAddress());

        // b4 - text-specific parameter CB
        // Bind the default value on non-text paths to keep the root signature complete.
        Device.GraphicsCommandList->SetGraphicsRootConstantBufferView(
            11, Pipeline.DefaultTextDrawParamsGpuAddress);

        // t0 - albedo
        Device.GraphicsCommandList->SetGraphicsRootDescriptorTable(
            3, DXTexture?.GpuDescriptorHandle ?? Device.White.GpuDescriptorHandle);

        // t1 - normal
        Device.GraphicsCommandList->SetGraphicsRootDescriptorTable(
            4, NormalMap?.GpuDescriptorHandle ?? Device.White.GpuDescriptorHandle);

        // t2 - metallicRoughness
        Device.GraphicsCommandList->SetGraphicsRootDescriptorTable(
            5, MetallicRoughnessMap?.GpuDescriptorHandle ?? Device.White.GpuDescriptorHandle);

        // t3 - occlusion
        Device.GraphicsCommandList->SetGraphicsRootDescriptorTable(
            6, OcclusionMap?.GpuDescriptorHandle ?? Device.White.GpuDescriptorHandle);

        // t4 - emissive
        Device.GraphicsCommandList->SetGraphicsRootDescriptorTable(
            7, EmissiveMap?.GpuDescriptorHandle ?? Device.White.GpuDescriptorHandle);

        Device.GraphicsCommandList->DrawInstanced(6, 1, 0, 0);
    }

    public abstract void Dispose();
}
