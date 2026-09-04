// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Direct3D12;

namespace Season.Platforms.Windows.DirectX;

[StructLayout(LayoutKind.Sequential)]
internal struct InstanceTransformData
{
    public Vector4 Row0;
    public Vector4 Row1;
    public Vector4 Row2;
    public Vector4 Row3;
    // Per-instance morph weights (up to 4 morph targets)
    public Vector4 MorphWeights;

    public static InstanceTransformData FromWorld(Matrix4x4 world)
    {
        return new InstanceTransformData
        {
            Row0 = new Vector4(world.M11, world.M12, world.M13, world.M14),
            Row1 = new Vector4(world.M21, world.M22, world.M23, world.M24),
            Row2 = new Vector4(world.M31, world.M32, world.M33, world.M34),
            Row3 = new Vector4(world.M41, world.M42, world.M43, world.M44),
            MorphWeights = Vector4.Zero,
        };
    }
}

internal unsafe class DXInstancedMesh3D : DXInstancedPrimitiveGroup
{
    public DXInstancedMesh3D(string name) : base(name)
    {
    }

    public void Load(InstancedMesh3D mesh, Season.Basic.Camera camera, Func<Season.Controls.Surface, TextureSlot, DXTexture> resolveTexture)
    {
        CreateSharedMatrixBuffers(camera);

        foreach (var surface in mesh.Surfaces)
            _primitives.Add(CreatePrimitiveData(surface, resolveTexture));

        RebuildPrimitiveBuckets();
        SyncAlpha(mesh.Alpha);
    }

    PrimitiveData CreatePrimitiveData(Season.Controls.Surface surface, Func<Season.Controls.Surface, TextureSlot, DXTexture> resolveTexture)
    {
        var localBounds = Season.Rendering.Bounds3D.FromVertices(surface.Vertices);
        var primitiveData = new PrimitiveData
        {
            Vertices = new List<Vertex>(surface.Vertices),
            Indices = Array.ConvertAll(surface.Indices, static i => (uint)i),
            Use32BitIndices = false,
            DoubleSided = surface.DoubleSided,
            LocalBoundsCenter = localBounds.Center,
            LocalBoundsExtents = localBounds.Extents,
        };

        primitiveData.VertexBuffer = Device.CreateVertexBuffer(primitiveData.Vertices.ToArray(), out primitiveData.VertexBufferView);
        primitiveData.IndexBuffer = Device.CreateIndexBuffer(primitiveData.Indices, out primitiveData.IndexBufferView);

        CreateMaterialBuffer(primitiveData);
        ProcessMaterial(surface, primitiveData, resolveTexture);
        return primitiveData;
    }

    void ProcessMaterial(Season.Controls.Surface surface, PrimitiveData primitiveData, Func<Season.Controls.Surface, TextureSlot, DXTexture> resolveTexture)
    {
        primitiveData.MaterialParams = new MaterialParams
        {
            RenderMode = surface.Unlit ? 0u : 1u,
            BaseColor = surface.BaseColor,
            MetallicFactor = surface.MetallicFactor,
            RoughnessFactor = surface.RoughnessFactor,
            EmissiveFactor = surface.EmissiveFactor
        };

        switch (surface.Mode)
        {
            case Season.Controls.SurfaceBlendMode.Mask:
                primitiveData.IsTransparent = false;
                primitiveData.MaterialParams.AlphaMode = 1u;
                primitiveData.MaterialParams.AlphaCutoff = surface.AlphaCutoff;
                break;
            case Season.Controls.SurfaceBlendMode.Blend:
                primitiveData.IsTransparent = true;
                primitiveData.MaterialParams.AlphaMode = 2u;
                primitiveData.MaterialParams.AlphaCutoff = 0.5f;
                break;
            default:
                primitiveData.IsTransparent = false;
                primitiveData.MaterialParams.AlphaMode = 0u;
                primitiveData.MaterialParams.AlphaCutoff = 0.5f;
                break;
        }

        // Resolve per slot: resolveTexture already handles both pixel sources
        // (uploaded directly to the GPU before Load) and path sources
        // (dictionary cache). Missing textures return null and fall back to
        // White, matching the legacy missing-path behavior.
        primitiveData.BaseColorTexture = resolveTexture(surface, TextureSlot.BaseColor) ?? Device.White;
        primitiveData.NormalTexture = resolveTexture(surface, TextureSlot.Normal) ?? Device.White;
        primitiveData.MetallicRoughnessTexture = resolveTexture(surface, TextureSlot.MetallicRoughness) ?? Device.White;
        primitiveData.OcclusionTexture = resolveTexture(surface, TextureSlot.Occlusion) ?? Device.White;
        primitiveData.EmissiveTexture = resolveTexture(surface, TextureSlot.Emissive) ?? Device.White;

        // Use*Map follows the "declared means enabled" rule: if the slot has a
        // valid texture source (path or pixels), the flag is set. This is
        // semantically equivalent to the legacy "non-empty path" rule.
        primitiveData.MaterialParams.UseAlbedoMap = surface.HasTexture(Season.Controls.SurfaceTextureSlot.BaseColor) ? 1u : 0u;
        primitiveData.MaterialParams.UseNormalMap = surface.HasTexture(Season.Controls.SurfaceTextureSlot.Normal) ? 1u : 0u;
        primitiveData.MaterialParams.UseMetallicRoughnessMap = surface.HasTexture(Season.Controls.SurfaceTextureSlot.MetallicRoughness) ? 1u : 0u;
        primitiveData.MaterialParams.UseOcclusionMap = surface.HasTexture(Season.Controls.SurfaceTextureSlot.Occlusion) ? 1u : 0u;
        primitiveData.MaterialParams.UseEmissiveMap = surface.HasTexture(Season.Controls.SurfaceTextureSlot.Emissive) ? 1u : 0u;

        primitiveData.OriginalBaseColorAlpha = primitiveData.MaterialParams.BaseColor.W * surface.Alpha;
        primitiveData.OriginalAlphaCutoff = primitiveData.MaterialParams.AlphaCutoff;
        primitiveData.MaterialParams.IsInstanced = 1;  // GPU instancing path

        WriteMaterialBuffer(primitiveData);
    }

    public void Update(InstancedMesh3D mesh, float time)
    {
        UpdateInstanceData(mesh, mesh.Instances, mesh.Alpha);
    }
}
