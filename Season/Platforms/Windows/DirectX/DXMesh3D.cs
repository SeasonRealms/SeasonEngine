// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Platforms.Windows.DirectX;

/// <summary>
/// DX12 backend implementation for Mesh3D. Inherits DXPrimitiveGroup to reuse
/// Matrix/Material CB creation, SyncAlpha, and the three-bucket draw flow.
/// This class only cares about geometry sources (Mesh3D.Surfaces) and material
/// mapping.
/// renderMode = 1 uses the PBR3D path and can reuse the same glTF-style
/// baseColor/normal/MR/AO/emissive and doubleSided behavior.
/// </summary>
internal unsafe class DXMesh3D : DXPrimitiveGroup
{
    // One PrimitiveData per Surface, aligned with Mesh3D.Surfaces order
    internal List<PrimitiveData> Primitives = new List<PrimitiveData>();

    public DXMesh3D(string name)
    {
        Name = name;
    }

    /// <summary>
    /// Loads Mesh3D: creates VB/IB/MatrixCB/MaterialCB for each Surface and
    /// processes its material.
    /// `resolveTexture` is injected by Windows Graphics. Given
    /// (Surface, slot), it returns the resolved DXTexture.
    /// `null` means the slot has no texture source and falls back to a solid
    /// color / default texture. Pixel-source textures are uploaded to the GPU
    /// before Load.
    /// </summary>
    public void Load(Season.Controls.Mesh3D mesh, Season.Basic.Camera camera, Func<Season.Controls.Surface, TextureSlot, DXTexture> resolveTexture)
    {
        foreach (var surface in mesh.Surfaces)
        {
            var primitive = CreatePrimitiveData(surface, resolveTexture, camera);
            // 2-2 contract rule 7: GTAO exemption bit
            // (can be switched at runtime during Update, see Update)
            primitive.AoExempt = mesh.ExcludeFromAo;
            Primitives.Add(primitive);
        }
    }

    PrimitiveData CreatePrimitiveData(Season.Controls.Surface surface, Func<Season.Controls.Surface, TextureSlot, DXTexture> resolveTexture, Season.Basic.Camera camera)
    {
        var primitiveData = new PrimitiveData();

        // Geometry data comes directly from Surface; the caller already
        // guarantees complete per-vertex data.
        primitiveData.Vertices = new List<Vertex>(surface.Vertices);
        primitiveData.Indices = Array.ConvertAll(surface.Indices, static i => (uint)i);
        primitiveData.Use32BitIndices = false;
        primitiveData.DoubleSided = surface.DoubleSided;
        var localBounds = Season.Rendering.Bounds3D.FromVertices(primitiveData.Vertices);
        primitiveData.LocalBoundsCenter = localBounds.Center;
        primitiveData.LocalBoundsExtents = localBounds.Extents;

        // GPU resources
        primitiveData.VertexBuffer = Device.CreateVertexBuffer(primitiveData.Vertices.ToArray(), out primitiveData.VertexBufferView);
        primitiveData.IndexBuffer = Device.CreateIndexBuffer(primitiveData.Indices, out primitiveData.IndexBufferView);

        // Constant buffers: reuse the base-class creation logic
        CreateMatrixBuffer(primitiveData);
        CreateMaterialBuffer(primitiveData);

        // Process materials and textures
        ProcessMaterial(surface, primitiveData, resolveTexture);

        // Initialize matrix buffers with the identity matrix for all frames so
        // other N-buffered frames never read garbage values.
        var matrices = new MatrixBuffer
        {
            World = System.Numerics.Matrix4x4.Transpose(System.Numerics.Matrix4x4.Identity),
            View = System.Numerics.Matrix4x4.Transpose(camera.View),
            Projection = System.Numerics.Matrix4x4.Transpose(camera.Projection)
        };
        for (int i = 0; i < Device.frameCount; i++)
            Unsafe.Write(primitiveData.MappedMatrixBuffers[i], matrices);

        return primitiveData;
    }

    void ProcessMaterial(Season.Controls.Surface surface, PrimitiveData primitiveData, Func<Season.Controls.Surface, TextureSlot, DXTexture> resolveTexture)
    {
        primitiveData.MaterialParams = new MaterialParams
        {
            // 2-5: procedural sky takes priority over Unlit
            // (renderMode=3 samples the Sky-View LUT by view direction and does
            // not read vertex UVs)
            RenderMode = surface.ProceduralSky ? 3u : (surface.Unlit ? 0u : 1u),
            BaseColor = surface.BaseColor,
            MetallicFactor = surface.MetallicFactor,
            RoughnessFactor = surface.RoughnessFactor,
            EmissiveFactor = surface.EmissiveFactor
        };

        // Matches the three glTF AlphaMode tiers: OPAQUE=0 / MASK=1 / BLEND=2.
        // Only Blend is truly transparent and requires the Transparent PSO.
        // Mask uses the Opaque PSO plus shader clip().
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
            default: // Opaque
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

        // Use*Map follows the "declared means enabled" rule: if a slot has a
        // valid texture source (path or pixels), the bit is set. This is
        // semantically equivalent to the legacy "non-empty path" rule.
        primitiveData.MaterialParams.UseAlbedoMap = surface.HasTexture(Season.Controls.SurfaceTextureSlot.BaseColor) ? 1u : 0u;
        primitiveData.MaterialParams.UseNormalMap = surface.HasTexture(Season.Controls.SurfaceTextureSlot.Normal) ? 1u : 0u;
        primitiveData.MaterialParams.UseMetallicRoughnessMap = surface.HasTexture(Season.Controls.SurfaceTextureSlot.MetallicRoughness) ? 1u : 0u;
        primitiveData.MaterialParams.UseOcclusionMap = surface.HasTexture(Season.Controls.SurfaceTextureSlot.Occlusion) ? 1u : 0u;
        primitiveData.MaterialParams.UseEmissiveMap = surface.HasTexture(Season.Controls.SurfaceTextureSlot.Emissive) ? 1u : 0u;

        // Record the original Surface BaseColor.W * Surface.Alpha so later
        // Mesh3D.Alpha multiplication stays stable:
        // final alpha = OriginalBaseColorAlpha * mesh.Alpha * texture alpha
        primitiveData.OriginalBaseColorAlpha = primitiveData.MaterialParams.BaseColor.W * surface.Alpha;
        primitiveData.OriginalBaseColor = surface.BaseColor;   // Multiplicative baseline for SyncColorTint at runtime
        primitiveData.OriginalAlphaCutoff = primitiveData.MaterialParams.AlphaCutoff;

        // Initialize the material buffer for every frame so other N-buffered
        // frames never read garbage and cause whole-object flicker.
        for (int i = 0; i < Device.frameCount; i++)
            Unsafe.Write(primitiveData.MappedMaterialBuffers[i], primitiveData.MaterialParams);
    }

    public void Update(Season.Controls.Mesh3D mesh, float time)
    {
        // Unified transform convention: route everything through
        // BuildWorldMatrix (anchor pivot: Scale -> anchor translation ->
        // Rotation -> Position, see Mesh3DBase)
        var world = mesh.BuildWorldMatrix();

        var matrices = new MatrixBuffer
        {
            World = System.Numerics.Matrix4x4.Transpose(world),
            View = System.Numerics.Matrix4x4.Transpose(Camera.View),
            Projection = System.Numerics.Matrix4x4.Transpose(Camera.Projection),
            // 2-3 contract rule 6: all-zero data makes the shader output zero
            // velocity, which is also the shape used when MotionVectors are off
            PrevViewProjection = System.Numerics.Matrix4x4.Transpose(Camera.PrevViewProjection),
        };

        int fi = (int)Device.FrameIndex;
        foreach (var primitive in Primitives)
        {
            // PrevWorld comes from the per-primitive CPU shadow copy and must
            // not be read back from the N-buffered constant buffer.
            matrices.PrevWorld = System.Numerics.Matrix4x4.Transpose(primitive.PrevWorldMatrix);
            Unsafe.Write(primitive.MappedMatrixBuffers[fi], matrices);
            primitive.PrevWorldMatrix = world;
        }

        _transformInitialized = true;

        // Sync Mesh3D.Alpha to every primitive material buffer
        // (written only when it changes; the base class handles the check)
        SyncAlpha(mesh.Alpha);

        // Sync Mesh3D.ColorTint
        // (the skybox modulates brightness / color temperature over the day-night
        // cycle; written only when it changes)
        SyncColorTint(mesh.ColorTint);

        // Unified highlighting: sync the wireframe bit
        // (runtime on/off is supported) and lazily build per-primitive shell
        // geometry on the first enabled frame, then keep it resident. When fully
        // disabled, there is no memory cost and no draw. Each frame writes the
        // model world matrix plus face/edge colors into every shell box
        // (face alpha is animated per frame).
        _wireframeEnabled = mesh.Highlight.Wireframe;
        if (_wireframeEnabled)
        {
            EnsureWireframeHighlights(mesh.Highlight.EdgeWidth,
                MathF.Max(mesh.LocalSize.X, MathF.Max(mesh.LocalSize.Y, mesh.LocalSize.Z)));
            if (_wireframeBoxes != null)
            {
                for (int i = 0; i < _wireframeBoxes.Count; i++)
                {
                    var highlight = _wireframeBoxes[i];
                    if (highlight != null)
                        WriteHighlightBox(highlight, world, mesh.Highlight.SurfaceColor, mesh.Highlight.EdgeColor);
                }
            }
        }

        // Unified highlighting: sync the bounds box.
        // Box geometry is built lazily on the first enabled frame. Face/edge
        // colors are independent of the model alpha chain and are written every
        // frame. Boxes with near-zero extents (unloaded / degenerate) stay off.
        _boundsActive = mesh.Highlight.Bounds;
        if (_boundsActive)
        {
            var bounds = mesh.GetWorldBoundsRaw();
            if (bounds.Extents.LengthSquared() >= 1e-12f)
            {
                _boundsBox ??= CreateBoundsBox();
                WriteHighlightBox(_boundsBox,
                    Matrix4x4.CreateScale(bounds.Extents * 2f) * Matrix4x4.CreateTranslation(bounds.Center),
                    mesh.Highlight.SurfaceColor, mesh.Highlight.EdgeColor);
            }
        }

        // 2-2 contract rule 7: sync runtime changes to the GTAO exemption bit
        // (change-gated, zero steady-state cost)
        if (Primitives.Count > 0 && Primitives[0].AoExempt != mesh.ExcludeFromAo)
        {
            foreach (var primitive in Primitives)
                primitive.AoExempt = mesh.ExcludeFromAo;
        }

        SetOutline2DState(mesh.Highlight.Outline,
            mesh.Highlight.OutlineColor, mesh.Highlight.OutlineWidth);
    }

    /// <summary>Called by the base Draw path: copies Primitives into
    /// `result` in Surface order.</summary>
    protected override void CollectPrimitives(List<PrimitiveData> result)
    {
        result.AddRange(Primitives);
    }

    public override void Dispose()
    {
        foreach (var primitive in Primitives)
        {
            primitive.Dispose();
        }
        Primitives.Clear();

        // Unified highlighting: release the highlight pool
        // (host bounds box + per-primitive wireframe shell boxes)
        DisposeHighlights();
    }
}
