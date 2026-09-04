// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Runtime.CompilerServices;

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

/// <summary>
/// Vulkan backend implementation of Mesh3D:
/// inherits VKPrimitiveGroup to reuse Matrix/Material UBO creation,
/// SyncAlpha, and three-bucket grouped drawing.
/// It only focuses on geometry sources (Mesh3D.Surfaces) and material mapping.
/// renderMode = 1 uses the PBR3D path and reuses glTF-style
/// baseColor/normal/MR/AO/emissive plus doubleSided.
/// </summary>
internal unsafe class VKMesh3D : VKPrimitiveGroup
{
    // One PrimitiveData per Surface; order matches Mesh3D.Surfaces
    internal List<PrimitiveData> Primitives = new();

    public VKMesh3D(string name)
    {
        Name = name;
    }

    /// <summary>
    /// Load Mesh3D: create VB/IB/MatrixUBO/MaterialUBO for each Surface and process materials.
    /// resolveTexture is injected by LinuxAndroidGraphics:
    /// given (Surface, slot), it returns the resolved Texture.
    /// null means the slot has no texture source and falls back to solid color / default behavior;
    /// pixel-source textures have already been uploaded directly to the GPU before Load.
    /// </summary>
    public void Load(Season.Controls.Mesh3D mesh, Season.Basic.Camera camera, Func<Season.Controls.Surface, TextureSlot, Texture> resolveTexture)
    {
        foreach (var surface in mesh.Surfaces)
        {
            var primitive = CreatePrimitiveData(surface, resolveTexture, camera);
            // 2-2 contract clause 7: GTAO exemption flag
            // (can be toggled at runtime during Update, see Update)
            primitive.AoExempt = mesh.ExcludeFromAo;
            Primitives.Add(primitive);
        }
    }

    PrimitiveData CreatePrimitiveData(Season.Controls.Surface surface, Func<Season.Controls.Surface, TextureSlot, Texture> resolveTexture, Season.Basic.Camera camera)
    {
        var p = new PrimitiveData();

        // Geometry data
        p.Vertices = new List<Vertex>(surface.Vertices);
        p.Indices = Array.ConvertAll(surface.Indices, static i => (uint)i);
        p.Use32BitIndices = false;
        p.DoubleSided = surface.DoubleSided;
        var localBounds = Season.Rendering.Bounds3D.FromVertices(p.Vertices);
        p.LocalBoundsCenter = localBounds.Center;
        p.LocalBoundsExtents = localBounds.Extents;

        // GPU resources: VB / IB
        p.VertexBuffer = Device.ResourceManager.CreateVertexBuffer(p.Vertices.ToArray());
        p.IndexBuffer = Device.ResourceManager.CreateIndexBuffer(p.Indices);

        // UBOs: reuse base-class creation logic
        CreateMatrixBuffer(p);
        CreateMaterialBuffer(p);

        // Process materials and textures
        ProcessMaterial(surface, p, resolveTexture);

        // Initialize the matrix buffer with identity matrices for all frames.
        // Note that Vulkan GLSL uses v*M as a direct translation of HLSL mul(v, M),
        // and this cancels out the implicit std140 column-major transpose.
        // So, just like DX, uploading Matrix4x4.Transpose is sufficient
        // (DX HLSL mul(v, M_T) is equivalent to GLSL v*M).
        var matrices = new MatrixBuffer
        {
            World = Matrix4x4.Transpose(Matrix4x4.Identity),
            View = Matrix4x4.Transpose(camera.View),
            Projection = Matrix4x4.Transpose(camera.Projection)
        };
        for (int i = 0; i < Device.frameCount; i++)
            Unsafe.Write(p.MappedMatrixBuffers[i], matrices);

        // Write the N-buffered DescriptorSet once after all resources are ready
        AllocateAndWriteDescriptorSets(p);
        return p;
    }

    void ProcessMaterial(Season.Controls.Surface surface, PrimitiveData p, Func<Season.Controls.Surface, TextureSlot, Texture> resolveTexture)
    {
        p.MaterialParams = new MaterialParams
        {
            RenderMode = surface.ProceduralSky ? 3u : (surface.Unlit ? 0u : 1u), // 2-5: procedural sky takes priority over Unlit (renderMode = 3 samples the Sky-View LUT by view direction and does not read vertex UVs)
            BaseColor = surface.BaseColor,
            MetallicFactor = surface.MetallicFactor,
            RoughnessFactor = surface.RoughnessFactor,
            EmissiveFactor = surface.EmissiveFactor
        };

        // Align with the three glTF AlphaMode states: OPAQUE = 0 / MASK = 1 / BLEND = 2.
        // Only Blend is truly transparent and requires the Transparent PSO;
        // Mask uses the Opaque PSO plus shader discard.
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

        // Resolve by slot: resolveTexture already handles both pixel sources
        // (uploaded directly to the GPU before Load) and path sources (dictionary-cached).
        // Missing textures return null -> White fallback,
        // matching the previous missing-path behavior.
        p.BaseColorTexture = resolveTexture(surface, TextureSlot.BaseColor) ?? Device.White;
        p.NormalTexture = resolveTexture(surface, TextureSlot.Normal) ?? Device.White;
        p.MetallicRoughnessTexture = resolveTexture(surface, TextureSlot.MetallicRoughness) ?? Device.White;
        p.OcclusionTexture = resolveTexture(surface, TextureSlot.Occlusion) ?? Device.White;
        p.EmissiveTexture = resolveTexture(surface, TextureSlot.Emissive) ?? Device.White;

        // The Use*Map rule is "declared means enabled":
        // if the slot has a valid texture source (path or pixels), mark it enabled.
        // This is semantically equivalent to the previous "path is non-empty" rule.
        p.MaterialParams.UseAlbedoMap = surface.HasTexture(Season.Controls.SurfaceTextureSlot.BaseColor) ? 1u : 0u;
        p.MaterialParams.UseNormalMap = surface.HasTexture(Season.Controls.SurfaceTextureSlot.Normal) ? 1u : 0u;
        p.MaterialParams.UseMetallicRoughnessMap = surface.HasTexture(Season.Controls.SurfaceTextureSlot.MetallicRoughness) ? 1u : 0u;
        p.MaterialParams.UseOcclusionMap = surface.HasTexture(Season.Controls.SurfaceTextureSlot.Occlusion) ? 1u : 0u;
        p.MaterialParams.UseEmissiveMap = surface.HasTexture(Season.Controls.SurfaceTextureSlot.Emissive) ? 1u : 0u;

        // Record the original Surface BaseColor.W * Surface.Alpha
        // so later Mesh3D.Alpha multiplication can be applied correctly
        p.OriginalBaseColorAlpha = p.MaterialParams.BaseColor.W * surface.Alpha;
        p.OriginalBaseColor = surface.BaseColor;   // Multiplicative baseline for SyncColorTint (runtime tinting)
        p.OriginalAlphaCutoff = p.MaterialParams.AlphaCutoff;

        // Initialize the material buffer for all frames
        // to avoid flicker from other frames reading garbage values under N-buffering
        for (int i = 0; i < Device.frameCount; i++)
            Unsafe.Write(p.MappedMaterialBuffers[i], p.MaterialParams);
    }

    public void Update(Season.Controls.Mesh3D mesh, float time)
    {
        // Unified transform convention: converge on BuildWorldMatrix
        // (anchor pivot: Scale -> anchor translation -> Rotation -> Position, see Mesh3DBase)
        var world = mesh.BuildWorldMatrix();

        var matrices = new MatrixBuffer
        {
            World = Matrix4x4.Transpose(world),
            View = Matrix4x4.Transpose(Camera.View),
            Projection = Matrix4x4.Transpose(Camera.Projection),
            // 2-3 contract clause 6: when all zero, the shader outputs zero velocity
            // (this is also the form used when MotionVectors are disabled)
            PrevViewProjection = Matrix4x4.Transpose(Camera.PrevViewProjection),
        };

        int fi = (int)Device.FrameIndex;
        foreach (var primitive in Primitives)
        {
            // PrevWorld is taken per primitive from the CPU shadow copy
            // and must not be read back from the N-buffered constant buffer
            matrices.PrevWorld = Matrix4x4.Transpose(primitive.PrevWorldMatrix);
            Unsafe.Write(primitive.MappedMatrixBuffers[fi], matrices);
            primitive.PrevWorldMatrix = world;
        }

        _transformInitialized = true;

        // Sync Mesh3D.Alpha to the material buffer of all primitives
        // (written only when changed, as determined by the base class)
        SyncAlpha(mesh.Alpha);

        // Sync Mesh3D.ColorTint
        // (the skybox adjusts brightness/color temperature with the day-night cycle;
        // written only when changed)
        SyncColorTint(mesh.ColorTint);

        // Unified highlighting: synchronize the wireframe flag
        // (can be toggled at runtime) + lazily build per-primitive shell geometry.
        // It is built on the first enabled frame and then kept resident;
        // when fully disabled, memory use and draw cost both stay at zero.
        // Each frame writes the model world matrix and the face/edge dual colors into each shell box.
        // Mesh3D procedural primitives have no nodes, so shell-box OwnerNode is always null;
        // face alpha pulsing is written every frame.
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

        // 2-2 contract clause 7: synchronize runtime toggling of the GTAO exemption flag
        // (Changed-gated, zero overhead in the steady state)
        if (Primitives.Count > 0 && Primitives[0].AoExempt != mesh.ExcludeFromAo)
        {
            foreach (var primitive in Primitives)
                primitive.AoExempt = mesh.ExcludeFromAo;
        }

        // Unified highlighting: synchronize the Bounds box
        // (box geometry is built lazily on the first enabled frame;
        // face/edge dual colors are independent of the model alpha chain and written every frame;
        // do not light it up when Extents is near zero, meaning an unloaded or degenerate box)
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

        // Unified highlighting: synchronize Outline2D state
        // (active state is collected by Graphics' OutlineMask pass; mirrors DXMesh3D on DX)
        SetOutline2DState(mesh.Highlight.Outline, mesh.Highlight.OutlineColor, mesh.Highlight.OutlineWidth);
    }

    /// <summary>Called by the base Draw: copy the Primitives into result
    /// in Surface order.</summary>
    protected override void CollectPrimitives(List<PrimitiveData> result)
    {
        result.AddRange(Primitives);
    }

    public override void Dispose()
    {
        foreach (var primitive in Primitives)
            primitive.Dispose();
        Primitives.Clear();

        // Unified highlighting: release the highlight pool (host Bounds box)
        DisposeHighlights();
    }
}
