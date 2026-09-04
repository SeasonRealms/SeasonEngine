// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Metal;
using MTLTexture = Season.Platforms.Shared.Apple.Metal.Texture;

namespace Season.Platforms.Shared.Apple.Metal;

internal sealed class MTLInstancedMesh3D : MTLInstancedPrimitiveGroup
{
    public MTLInstancedMesh3D(string name) : base(name)
    {
    }

    public void Load(Season.Controls.InstancedMesh3D mesh, Camera camera, Func<Season.Controls.Surface, TextureSlot, MTLTexture> resolveTexture)
    {
        foreach (var surface in mesh.Surfaces)
            _primitives.Add(CreatePrimitiveData(surface, resolveTexture, camera));

        RebuildPrimitiveBuckets();
        SyncAlpha(mesh.Alpha);
    }

    PrimitiveData CreatePrimitiveData(Season.Controls.Surface surface, Func<Season.Controls.Surface, TextureSlot, MTLTexture> resolveTexture, Camera camera)
    {
        var localBounds = Season.Rendering.Bounds3D.FromVertices(surface.Vertices);
        var p = new PrimitiveData
        {
            Vertices = new List<Vertex>(surface.Vertices),
            Indices = Array.ConvertAll(surface.Indices, static i => (uint)i),
            Use32BitIndices = false,
            DoubleSided = surface.DoubleSided,
            LocalBoundsCenter = localBounds.Center,
            LocalBoundsExtents = localBounds.Extents,
        };

        p.VertexBuffer = Device.ResourceManager.CreateVertexBuffer(p.Vertices.ToArray());
        p.IndexBuffer = Device.ResourceManager.CreateIndexBuffer(p.Indices);
        CreateMatrixBuffer(p);
        CreateMaterialBuffer(p);
        ProcessMaterial(surface, p, resolveTexture);

        var matrices = new MatrixBuffer
        {
            World = Matrix4x4.Transpose(Matrix4x4.Identity),
            View = Matrix4x4.Transpose(camera.View),
            Projection = Matrix4x4.Transpose(camera.Projection),
            // Contract clause 8(d) of 2-3:
            // PrevWorld in b0 stays all zeros on the instanced path because history comes from the double-buffered instance stream.
            PrevViewProjection = Matrix4x4.Transpose(camera.PrevViewProjection),
        };
        for (int i = 0; i < Device.frameCount; i++)
            WriteStruct(p.MatrixBuffers[i], matrices);

        return p;
    }

    void ProcessMaterial(Season.Controls.Surface surface, PrimitiveData p, Func<Season.Controls.Surface, TextureSlot, MTLTexture> resolveTexture)
    {
        p.MaterialParams = new MaterialParams
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
                p.IsTransparent = false;
                p.MaterialParams.AlphaMode = 1u;
                p.MaterialParams.AlphaCutoff = surface.AlphaCutoff;
                break;
            case Season.Controls.SurfaceBlendMode.Blend:
                p.IsTransparent = true;
                p.MaterialParams.AlphaMode = 2u;
                p.MaterialParams.AlphaCutoff = 0.5f;
                break;
            default:
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
        // if the slot has a valid texture source, whether path-based or pixel-based, the flag is set.
        // This is equivalent to the old "non-empty path" semantics.
        p.MaterialParams.UseAlbedoMap = surface.HasTexture(Season.Controls.SurfaceTextureSlot.BaseColor) ? 1u : 0u;
        p.MaterialParams.UseNormalMap = surface.HasTexture(Season.Controls.SurfaceTextureSlot.Normal) ? 1u : 0u;
        p.MaterialParams.UseMetallicRoughnessMap = surface.HasTexture(Season.Controls.SurfaceTextureSlot.MetallicRoughness) ? 1u : 0u;
        p.MaterialParams.UseOcclusionMap = surface.HasTexture(Season.Controls.SurfaceTextureSlot.Occlusion) ? 1u : 0u;
        p.MaterialParams.UseEmissiveMap = surface.HasTexture(Season.Controls.SurfaceTextureSlot.Emissive) ? 1u : 0u;

        p.OriginalBaseColorAlpha = p.MaterialParams.BaseColor.W * surface.Alpha;
        p.OriginalAlphaCutoff = p.MaterialParams.AlphaCutoff;
        p.MaterialParams.IsInstanced = 1;
        // Contract clause 8(c) of 2-3:
        // previous per-instance world transforms come from the double-buffered instance stream.
        // On the first frame or after capacity changes, the opposite side is zeroed,
        // and the shader falls back to the current world when r3.w == 0.
        p.MaterialParams.HasPrevInstanceWorld = 1;

        for (int i = 0; i < Device.frameCount; i++)
            WriteStruct(p.MaterialBuffers[i], p.MaterialParams);
    }

    public void Update(Season.Controls.InstancedMesh3D mesh, float time)
    {
        _instanceCount = 0;

        // Unified highlighting:
        // clear the per-instance Bounds and Wireframe draw lists for this frame.
        // They are rebuilt every frame, and _boundsActive and _wireframeActive
        // are set later by the per-instance hooks below.
        _boundsActive = false;
        _boundsBoxDrawList.Clear();
        _wireframeActive = false;
        _shellBoxDrawList.Clear();
        _outline2DInstances.Clear();
        _outline2DInstanceColors.Clear();

        for (int i = 0; i < mesh.Instances.Count; i++)
        {
            if (mesh.Instances[i].Enable)
                _instanceCount++;
        }

        if (_instanceCount == 0)
        {
            _transformInitialized = true;
            SyncAlpha(mesh.Alpha);
            SetOutline2DState(false, default, default);
            return;
        }

        if (_instanceWorlds.Length != _instanceCount)
        {
            _instanceWorlds = new Matrix4x4[_instanceCount];
            _instanceData = new InstanceTransformData[_instanceCount];
        }

        EnsureInstanceBufferCapacity(_instanceCount);

        int writeIndex = 0;
        for (int i = 0; i < mesh.Instances.Count; i++)
        {
            var instance = mesh.Instances[i];
            if (!instance.Enable)
                continue;

            // Unified positioning contract:
            // converge on BuildInstanceMatrix using the anchor pivot described by InstancedMesh3DBase.
            var world = mesh.BuildInstanceMatrix(instance);

            _instanceWorlds[writeIndex] = world;
            _instanceData[writeIndex] = InstanceTransformData.FromWorld(world);

            // Outline2D when activated per instance:
            // record the writeIndex slot and the per-instance outline color so per-slot masks can fetch colors by slot.
            // The first outlined instance also captures the frame-level composite color and width,
            // which are used by the host path and SetOutline2DState.
            if (instance.Highlight.Outline)
            {
                _outline2DInstances.Add(writeIndex);
                _outline2DInstanceColors.Add(instance.Highlight.OutlineColor);
                if (_outline2DInstances.Count == 1)
                {
                    _outline2DInstanceColor = instance.Highlight.OutlineColor;
                    _outline2DInstanceWidth = instance.Highlight.OutlineWidth;
                }
            }

            // Unified highlighting for per-instance bounds boxes:
            // box alpha and color stay independent from the host-wide alpha chain.
            // Do not light the box when extents are near zero, such as unloaded or degenerate bounds.
            if (instance.Highlight.Bounds)
            {
                var worldBounds = mesh.GetInstanceWorldBoundsRaw(instance);
                if (worldBounds.Extents.LengthSquared() >= 1e-12f)
                {
                    _boundsActive = true;
                    var box = AcquireBoundsBox(writeIndex);
                    WriteHighlightBox(box,
                        Matrix4x4.CreateScale(worldBounds.Extents * 2f)
                        * Matrix4x4.CreateTranslation(worldBounds.Center),
                        instance.Highlight.SurfaceColor, instance.Highlight.EdgeColor);
                    _boundsBoxDrawList.Add(writeIndex);
                }
            }

            // Unified highlighting for per-instance wireframe:
            // lazily build shared shell templates, which remain resident after the first successful creation,
            // plus per-instance shell boxes whose matrices are addressed by the instance-stream writeIndex slot
            // and then drawn per instance.
            // Hybrid assets draw both shells, rigid plus skinned,
            // and the skinned shell follows animation through the per-instance bone-palette path.
            // When both templates are unavailable, such as no usable primitives, morphs, or multiple-skin cases,
            // the box stays null and is not added to the draw list.
            if (instance.Highlight.Wireframe)
            {
                _wireframeActive = true;
                EnsureShellGeometry(mesh.Highlight.EdgeWidth,
                    MathF.Max(mesh.TemplateLocalSize.X, MathF.Max(mesh.TemplateLocalSize.Y, mesh.TemplateLocalSize.Z)));
                var shellBox = AcquireShellBox(writeIndex);
                var skinnedShellBox = AcquireSkinnedShellBox(writeIndex);
                if (shellBox != null || skinnedShellBox != null)
                {
                    if (shellBox != null)
                        WriteInstanceShell(shellBox, instance.Highlight.SurfaceColor, instance.Highlight.EdgeColor);
                    if (skinnedShellBox != null)
                        WriteInstanceShell(skinnedShellBox, instance.Highlight.SurfaceColor, instance.Highlight.EdgeColor);
                    _shellBoxDrawList.Add(writeIndex);
                }
            }

            writeIndex++;
        }

        // Outline2D activation is the union of host-wide activation and any per-instance activation.
        // Host-wide activation uses the full mask and ignores the per-instance list.
        // For color and width, per-instance activation takes priority and uses the instance values,
        // which are typically panel colors written by the picker.
        // Otherwise the host values are used, matching Mesh3D and Model semantics.
        _outline2DHostActive = mesh.Highlight.Outline;
        bool anyInstanceOutline = _outline2DInstances.Count > 0;
        SetOutline2DState(_outline2DHostActive || anyInstanceOutline,
            anyInstanceOutline ? _outline2DInstanceColor : mesh.Highlight.OutlineColor,
            anyInstanceOutline ? _outline2DInstanceWidth : mesh.Highlight.OutlineWidth);

        FlipAndUploadInstanceStream(_instanceData, _instanceCount);

        int fi = Device.FrameIndex;
        var matrices = new MatrixBuffer
        {
            World = Matrix4x4.Transpose(Matrix4x4.Identity),
            View = Matrix4x4.Transpose(Camera.View),
            Projection = Matrix4x4.Transpose(Camera.Projection),
            // Contract clause 8(d) of 2-3:
            // PrevWorld in b0 remains all zeros because per-instance history on the instanced path
            // comes from the opposite side of the double-buffered instance stream at VS buffer 9,
            // not from b0.
            PrevViewProjection = Matrix4x4.Transpose(Camera.PrevViewProjection),
        };
        foreach (var primitive in _primitives)
            WriteStruct(primitive.MatrixBuffers[fi], matrices);

        _transformInitialized = true;
        SyncAlpha(mesh.Alpha);
    }

    public override void Dispose()
    {
        foreach (var primitive in _primitives)
            primitive.Dispose();

        // Unified highlighting: release the highlight pool for bounds instance boxes.
        DisposeHighlights();

        base.Dispose();
    }
}
