// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using MTLTexture = Season.Platforms.Shared.Apple.Metal.Texture;

namespace Season.Platforms.Shared.Apple.Metal;

/// <summary>
/// Metal backend implementation for Mesh3D.
/// It inherits from MTLPrimitiveGroup to reuse Matrix and Material UBO creation,
/// SyncAlpha, and three-bucket grouped drawing.
/// Its own responsibility is limited to geometry sources from Mesh3D.Surfaces plus material mapping.
/// renderMode = 1 uses the PBR3D path and can reuse glTF-style baseColor, normal, MR, AO, emissive, and doubleSided handling.
/// </summary>
internal sealed class MTLMesh3D : MTLPrimitiveGroup
{
    // One PrimitiveData per Surface, kept in the same order as Mesh3D.Surfaces.
    internal List<PrimitiveData> Primitives = new();

    public MTLMesh3D(string name)
    {
        Name = name;
    }

    /// <summary>
    /// Loads Mesh3D by creating VB, IB, MatrixUBO, and MaterialUBO for each Surface and then processing materials.
    /// resolveTexture is injected by Apple/Graphics.cs and returns the resolved Texture for a given Surface and slot.
    /// A null result means the slot has no texture source and should use solid-color or default fallback behavior.
    /// Pixel-source textures have already been uploaded directly to the GPU before Load.
    /// </summary>
    public void Load(Season.Controls.Mesh3D mesh, Camera camera, Func<Season.Controls.Surface, TextureSlot, MTLTexture> resolveTexture)
    {
        foreach (var surface in mesh.Surfaces)
        {
            var primitive = CreatePrimitiveData(surface, resolveTexture, camera);
            // Contract clause 7 of 2-2: GTAO exemption flag, which can be switched at runtime during Update.
            primitive.AoExempt = mesh.ExcludeFromAo;
            Primitives.Add(primitive);
        }
    }

    PrimitiveData CreatePrimitiveData(Season.Controls.Surface surface, Func<Season.Controls.Surface, TextureSlot, MTLTexture> resolveTexture, Camera camera)
    {
        var p = new PrimitiveData();

        // Geometry data.
        p.Vertices = new List<Vertex>(surface.Vertices);
        p.Indices = Array.ConvertAll(surface.Indices, static i => (uint)i);
        p.Use32BitIndices = false;
        p.DoubleSided = surface.DoubleSided;
        var localBounds = Season.Rendering.Bounds3D.FromVertices(p.Vertices);
        p.LocalBoundsCenter = localBounds.Center;
        p.LocalBoundsExtents = localBounds.Extents;

        // GPU resources: VB and IB.
        p.VertexBuffer = Device.ResourceManager.CreateVertexBuffer(p.Vertices.ToArray());
        p.IndexBuffer = Device.ResourceManager.CreateIndexBuffer(p.Indices);

        // UBOs: reuse the base-class creation logic.
        CreateMatrixBuffer(p);
        CreateMaterialBuffer(p);

        // Process materials and textures.
        ProcessMaterial(surface, p, resolveTexture);

        // Initialize matrix buffers with identity matrices for all frames.
        // Note: Metal MSL follows GLSL in using v * M, which is equivalent to HLSL mul(v, M_T),
        // so Matrix4x4.Transpose is uploaded to match std140 layout expectations.
        var matrices = new MatrixBuffer
        {
            World = Matrix4x4.Transpose(Matrix4x4.Identity),
            View = Matrix4x4.Transpose(camera.View),
            Projection = Matrix4x4.Transpose(camera.Projection)
        };
        for (int i = 0; i < Device.frameCount; i++)
            WriteStruct(p.MatrixBuffers[i], matrices);

        return p;
    }

    void ProcessMaterial(Season.Controls.Surface surface, PrimitiveData p, Func<Season.Controls.Surface, TextureSlot, MTLTexture> resolveTexture)
    {
        p.MaterialParams = new MaterialParams
        {
            // For 2-5, procedural sky takes priority over Unlit.
            // renderMode = 3 samples the Sky-View LUT by view direction and does not read vertex UVs.
            RenderMode = surface.ProceduralSky ? 3u : (surface.Unlit ? 0u : 1u),
            BaseColor = surface.BaseColor,
            MetallicFactor = surface.MetallicFactor,
            RoughnessFactor = surface.RoughnessFactor,
            EmissiveFactor = surface.EmissiveFactor
        };

        // Align with the three glTF AlphaMode tiers:
        // OPAQUE = 0, MASK = 1, BLEND = 2.
        // Only Blend is truly transparent and therefore needs the Transparent PSO.
        // Mask uses the Opaque PSO plus shader-side discard.
        switch (surface.Mode)
        {
            case Season.Controls.SurfaceBlendMode.Mask:
                p.IsTransparent = false;
                p.MaterialParams.AlphaMode = 1u;
                p.MaterialParams.AlphaCutoff = surface.AlphaCutoff;
                break;
            case Season.Controls.SurfaceBlendMode.Blend:
                p.IsTransparent = true;
                p.MaterialParams.AlphaMode = 2u;
                p.MaterialParams.AlphaCutoff = 0.5f;
                break;
            default: // Opaque
                p.IsTransparent = false;
                p.MaterialParams.AlphaMode = 0u;
                p.MaterialParams.AlphaCutoff = 0.5f;
                break;
        }

        // Resolve textures by slot.
        // resolveTexture already handles both pixel sources pushed directly to the GPU before Load
        // and path-based sources through dictionary caching.
        // Missing slots return null and therefore fall back to White, matching the previous missing-path behavior.
        p.BaseColorTexture = resolveTexture(surface, TextureSlot.BaseColor) ?? Device.White;
        p.NormalTexture = resolveTexture(surface, TextureSlot.Normal) ?? Device.White;
        p.MetallicRoughnessTexture = resolveTexture(surface, TextureSlot.MetallicRoughness) ?? Device.White;
        p.OcclusionTexture = resolveTexture(surface, TextureSlot.Occlusion) ?? Device.White;
        p.EmissiveTexture = resolveTexture(surface, TextureSlot.Emissive) ?? Device.White;

        // The Use*Map rule is "declared means enabled":
        // if a slot has a valid texture source, whether path-based or pixel-based, the flag is set.
        // This is equivalent to the old "non-empty path" semantics.
        p.MaterialParams.UseAlbedoMap = surface.HasTexture(Season.Controls.SurfaceTextureSlot.BaseColor) ? 1u : 0u;
        p.MaterialParams.UseNormalMap = surface.HasTexture(Season.Controls.SurfaceTextureSlot.Normal) ? 1u : 0u;
        p.MaterialParams.UseMetallicRoughnessMap = surface.HasTexture(Season.Controls.SurfaceTextureSlot.MetallicRoughness) ? 1u : 0u;
        p.MaterialParams.UseOcclusionMap = surface.HasTexture(Season.Controls.SurfaceTextureSlot.Occlusion) ? 1u : 0u;
        p.MaterialParams.UseEmissiveMap = surface.HasTexture(Season.Controls.SurfaceTextureSlot.Emissive) ? 1u : 0u;

        // Record the original Surface BaseColor.W multiplied by Surface.Alpha
        // so Mesh3D.Alpha can be applied later as an additional multiplier.
        p.OriginalBaseColorAlpha = p.MaterialParams.BaseColor.W * surface.Alpha;
        p.OriginalBaseColor = surface.BaseColor;   // Multiplicative base for SyncColorTint during runtime tinting.
        p.OriginalAlphaCutoff = p.MaterialParams.AlphaCutoff;

        // Initialize material buffers for every frame to avoid garbage values on other N-buffered frames causing full-object flicker.
        for (int i = 0; i < Device.frameCount; i++)
            WriteStruct(p.MaterialBuffers[i], p.MaterialParams);
    }

    public void Update(Season.Controls.Mesh3D mesh, float time)
    {
        // Unified positioning contract:
        // converge on BuildWorldMatrix using the anchor pivot order Scale -> anchor translation -> Rotation -> Position, as described by Mesh3DBase.
        var world = mesh.BuildWorldMatrix();

        var matrices = new MatrixBuffer
        {
            World = Matrix4x4.Transpose(world),
            View = Matrix4x4.Transpose(Camera.View),
            Projection = Matrix4x4.Transpose(Camera.Projection),
            // Contract clause 6 of 2-3:
            // when this is all zeros, the shader outputs zero velocity.
            // This is also the shape used when MotionVectors is disabled.
            PrevViewProjection = Matrix4x4.Transpose(Camera.PrevViewProjection),
        };

        int fi = Device.FrameIndex;
        foreach (var primitive in Primitives)
        {
            // PrevWorld is taken per primitive from the CPU shadow copy
            // and must never be read back from the N-buffered constant buffers.
            matrices.PrevWorld = Matrix4x4.Transpose(primitive.PrevWorldMatrix);
            WriteStruct(primitive.MatrixBuffers[fi], matrices);
            primitive.PrevWorldMatrix = world;
        }

        _transformInitialized = true;

        // Sync Mesh3D.Alpha into every primitive material buffer.
        // The base class writes only when the value actually changes.
        SyncAlpha(mesh.Alpha);

        // Sync Mesh3D.ColorTint.
        // The skybox may modulate brightness and color temperature through day-night cycles,
        // and writes happen only when values change.
        SyncColorTint(mesh.ColorTint);

        // Unified highlighting:
        // sync the wireframe flag, which can be toggled at runtime,
        // and lazily build per-primitive shell geometry.
        // The shell is created on the first enabled frame and remains resident afterward.
        // When fully disabled it incurs no memory and no draw cost.
        // Each frame writes the world matrix plus face and edge colors into each shell box.
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

        // Contract clause 7 of 2-2:
        // synchronize runtime changes to the GTAO exemption flag.
        // It is gated by actual changes, so steady-state cost stays at zero.
        if (Primitives.Count > 0 && Primitives[0].AoExempt != mesh.ExcludeFromAo)
        {
            foreach (var primitive in Primitives)
                primitive.AoExempt = mesh.ExcludeFromAo;
        }

        // Unified highlighting for the Bounds box:
        // lazily build box geometry on the first enabled frame.
        // Face and edge colors stay independent from the model alpha chain and are written every frame.
        // Do not light the box when extents are near zero, such as unloaded or degenerate bounds.
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

        // Unified highlighting:
        // synchronize Outline2D state.
        // Activation is collected by the OutlineMask pass in Graphics, mirroring DXMesh3D and VKMesh3D.
        SetOutline2DState(mesh.Highlight.Outline, mesh.Highlight.OutlineColor, mesh.Highlight.OutlineWidth);
    }

    /// <summary>Called by the base-class Draw to append primitives into result in Surface order.</summary>
    protected override void CollectPrimitives(List<PrimitiveData> result)
    {
        result.AddRange(Primitives);
    }

    public override void Dispose()
    {
        foreach (var primitive in Primitives)
            primitive.Dispose();
        Primitives.Clear();

        // Unified highlighting: release the highlight pool for the host Bounds box.
        DisposeHighlights();
    }
}
