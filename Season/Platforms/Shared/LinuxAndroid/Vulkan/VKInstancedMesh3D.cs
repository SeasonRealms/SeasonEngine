// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Vulkan;
using System.Runtime.CompilerServices;

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

internal unsafe class VKInstancedMesh3D : VKInstancedPrimitiveGroup
{
    // 2-3 Step C (track C-a): prev per-instance world SSBO
    // (same capacity as _instanceWorlds).
    // Each Update first copies the current _instanceWorlds into the mapped prev SB region,
    // then uploads the current frame, so the GPU always holds the instance world from the
    // previous frame, indexed by instanceID in the shader.
    // Before the first frame, the content is all zero (sentinel _m33 == 0),
    // and the shader falls back to the current worldMatrix.
    BufferResource[] _prevInstanceWorldBuffers = null!;
    byte*[] _mappedPrevInstanceWorldBuffers = null!;

    public VKInstancedMesh3D(string name) : base(name)
    {
    }

    public void Load(Season.Controls.InstancedMesh3D mesh, Season.Basic.Camera camera, Func<Season.Controls.Surface, TextureSlot, Texture> resolveTexture)
    {
        foreach (var surface in mesh.Surfaces)
            _primitives.Add(CreatePrimitiveData(surface, resolveTexture, camera));

        RebuildPrimitiveBuckets();
        SyncAlpha(mesh.Alpha);
    }

    PrimitiveData CreatePrimitiveData(Season.Controls.Surface surface, Func<Season.Controls.Surface, TextureSlot, Texture> resolveTexture, Season.Basic.Camera camera)
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
            // 2-3 Step C: in the instanced path, PrevWorld stays all zero;
            // PrevViewProjection follows the same convention as DX
            PrevViewProjection = Matrix4x4.Transpose(camera.PrevViewProjection),
        };

        for (int i = 0; i < Device.frameCount; i++)
            Unsafe.Write(p.MappedMatrixBuffers[i], matrices);

        AllocateAndWriteDescriptorSets(p);
        return p;
    }

    void ProcessMaterial(Season.Controls.Surface surface, PrimitiveData p, Func<Season.Controls.Surface, TextureSlot, Texture> resolveTexture)
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

        p.OriginalBaseColorAlpha = p.MaterialParams.BaseColor.W * surface.Alpha;
        p.OriginalAlphaCutoff = p.MaterialParams.AlphaCutoff;

        p.MaterialParams.IsInstanced = 1;

        for (int i = 0; i < Device.frameCount; i++)
            Unsafe.Write(p.MappedMaterialBuffers[i], p.MaterialParams);
    }

    public void Update(Season.Controls.InstancedMesh3D mesh, float time)
    {
        _instanceCount = 0;

        // Unified highlighting: clear the per-instance Bounds/Wireframe draw lists for this frame
        // (rebuilt every frame; _boundsActive/_wireframeActive are set by the per-instance hooks below)
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
        // 2-3 Step C: create or grow the prev instance world SSBO
        EnsurePrevInstanceWorldCapacity(_instanceCount);

        bool wasInitialized = _transformInitialized;

        // 2-3 Step C (track C-a): before uploading this frame, first copy the current
        // _instanceWorlds (the current-frame data from the previous frame) into the mapped prev SB region.
        // On the first frame, _instanceWorlds is all zero (newly allocated and not yet filled),
        // so the prev SB stays zeroed and the sentinel semantics remain correct.
        int fi = (int)Device.FrameIndex;
        if (_mappedPrevInstanceWorldBuffers != null && _instanceCount > 0)
        {
            if (fi < _mappedPrevInstanceWorldBuffers.Length && _mappedPrevInstanceWorldBuffers[fi] != null)
            {
                fixed (Matrix4x4* pSrc = _instanceWorlds)
                    Unsafe.CopyBlock(_mappedPrevInstanceWorldBuffers[fi], pSrc,
                        (uint)(_instanceCount * Unsafe.SizeOf<Matrix4x4>()));
            }
        }

        int writeIndex = 0;
        for (int i = 0; i < mesh.Instances.Count; i++)
        {
            var instance = mesh.Instances[i];
            if (!instance.Enable)
                continue;

            // Unified transform convention: converge on BuildInstanceMatrix
            // (anchor pivot, see InstancedMesh3DBase)
            var world = mesh.BuildInstanceMatrix(instance);

            _instanceWorlds[writeIndex] = world;
            _instanceData[writeIndex] = InstanceTransformData.FromWorld(world);

            // Outline2D (per-instance activation): record the writeIndex slot and the
            // per-instance outline color (per-slot mask fetches color per slot).
            // The first active instance also captures the frame-level composited color/width
            // used by the host path and SetOutline2DState.
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

            // Unified highlighting (per-instance Bounds box):
            // box alpha/color are independent from the host-level alpha chain.
            // Do not light it up when Extents is near zero (unloaded or degenerate box).
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

            // Unified highlighting (per-instance Wireframe):
            // shared shell templates are created lazily and kept resident after the first success,
            // plus a per-instance shell box.
            // The matrix is addressed through the instance-stream writeIndex slot and drawn per instance.
            // Mixed assets draw both shells (rigid + skinned); the skinned shell follows animation
            // through the per-instance bone-palette path.
            // If neither template is available (no usable primitive/morph/multi-skinning),
            // the box is null and is not added to the draw list.
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

        // Outline2D activation = host-level activation union any per-instance activation.
        // Host activation uses the full-instance mask and ignores the per-instance list.
        // Color/width: prefer the instance values when any instance is active
        // (panel color written by the picker); otherwise use the host values,
        // matching Mesh3D/Model semantics.
        _outline2DHostActive = mesh.Highlight.Outline;
        bool anyInstanceOutline = _outline2DInstances.Count > 0;
        SetOutline2DState(_outline2DHostActive || anyInstanceOutline,
            anyInstanceOutline ? _outline2DInstanceColor : mesh.Highlight.OutlineColor,
            anyInstanceOutline ? _outline2DInstanceWidth : mesh.Highlight.OutlineWidth);

        Device.ResourceManager.UpdateBuffer(_instanceBuffer, _instanceData);

        var matrices = new MatrixBuffer
        {
            World = Matrix4x4.Transpose(Matrix4x4.Identity),
            View = Matrix4x4.Transpose(Camera.View),
            Projection = Matrix4x4.Transpose(Camera.Projection),
            // 2-3 Step C (track C-a): the per-instance previous world matrices now come from
            // the prev instance world SB (binding 14),
            // so b0.PrevWorld stays all zero because the instanced shader path does not read it.
            PrevViewProjection = Matrix4x4.Transpose(Camera.PrevViewProjection),
        };

        foreach (var primitive in _primitives)
            Unsafe.Write(primitive.MappedMatrixBuffers[fi], matrices);

        _transformInitialized = true;

        // 2-3 Step C: from the second frame onward, the prev SB contains valid data,
        // so notify the shader path that it can start reading it.
        // Also rewrite the DescriptorSet to switch binding 14 from the default zero buffer
        // to the actual prev SSBO.
        if (wasInitialized)
        {
            SetPrevInstanceWorldReady();
            foreach (var primitive in _primitives)
                RewriteDescriptorSets(primitive);
            // Keep shell primitive descriptor sets in sync
            // (switch binding 14 from the zero placeholder to the actual prev instance world SSBO)
            RewriteShellDescriptorSets();
        }

        SyncAlpha(mesh.Alpha);
    }

    // 2-3 Step C: create or grow the prev instance world SSBO
    // (same capacity as _instanceWorlds; zero-initialized on first creation)
    void EnsurePrevInstanceWorldCapacity(int count)
    {
        if (count <= 0)
            return;

        int n = (int)Device.frameCount;
        if (_prevInstanceWorldBuffers != null && _prevInstanceWorldBuffers.Length == n)
            return;

        // Release the old buffers
        if (_prevInstanceWorldBuffers != null)
        {
            for (int i = 0; i < _prevInstanceWorldBuffers.Length; i++)
            {
                if (_mappedPrevInstanceWorldBuffers != null && i < _mappedPrevInstanceWorldBuffers.Length
                    && _mappedPrevInstanceWorldBuffers[i] != null && _prevInstanceWorldBuffers[i].Memory.Handle != 0)
                    Device.Vk.UnmapMemory(Device.LogicalDevice, _prevInstanceWorldBuffers[i].Memory);
                if (_prevInstanceWorldBuffers[i].Memory.Handle != 0)
                    Device.ResourceManager.DestroyBuffer(_prevInstanceWorldBuffers[i]);
            }
        }

        ulong size = (ulong)(count * Unsafe.SizeOf<Matrix4x4>());
        _prevInstanceWorldBuffers = new BufferResource[n];
        _mappedPrevInstanceWorldBuffers = new byte*[n];

        for (int i = 0; i < n; i++)
        {
            _prevInstanceWorldBuffers[i] = Device.ResourceManager.CreateBuffer(
                size,
                Silk.NET.Vulkan.BufferUsageFlags.StorageBufferBit | Silk.NET.Vulkan.BufferUsageFlags.TransferDstBit,
                Silk.NET.Vulkan.MemoryPropertyFlags.HostVisibleBit | Silk.NET.Vulkan.MemoryPropertyFlags.HostCoherentBit);
            void* mapped;
            if (Device.Vk.MapMemory(Device.LogicalDevice, _prevInstanceWorldBuffers[i].Memory, 0, size, 0, &mapped) != Silk.NET.Vulkan.Result.Success)
                throw new System.Exception("vkMapMemory (PrevInstanceWorldBuffers) failed");
            _mappedPrevInstanceWorldBuffers[i] = (byte*)mapped;
            new Span<byte>(mapped, (int)size).Clear();
        }
    }

    // 2-3 Step C: override the base virtual method to return the actual prev instance world SSBO
    protected override Silk.NET.Vulkan.DescriptorBufferInfo GetPrevInstanceWorldBufferInfo(int fi)
        => _prevInstanceWorldBuffers != null && fi < _prevInstanceWorldBuffers.Length
            ? new() { Buffer = _prevInstanceWorldBuffers[fi].Buffer, Offset = 0, Range = Vk.WholeSize }
            : base.GetPrevInstanceWorldBufferInfo(fi);

    /// <summary>2-3 Step C (track C-a): after the prev instance world SB has been filled with valid data,
    /// set MaterialParams.HasPrevInstanceWorld = 1 for all primitives
    /// (written across all N-buffered frames),
    /// so the shader side starts reading the prev instance world SB.
    /// This is written only on the first call because the value does not change afterward.</summary>
    void SetPrevInstanceWorldReady()
    {
        for (int i = 0; i < _primitives.Count; i++)
        {
            var primitive = _primitives[i];
            if (primitive.MaterialParams.HasPrevInstanceWorld != 0)
                continue;
            primitive.MaterialParams.HasPrevInstanceWorld = 1;
            for (int f = 0; f < Device.frameCount; f++)
                Unsafe.Write(primitive.MappedMaterialBuffers[f], primitive.MaterialParams);
        }
        // Synchronize prev flags for shell primitives:
        // cover both template sets and both instance-box pools,
        // since pooled boxes may have been created earlier and have stale flags
        SyncShellPrevFlags(hasPrevInstanceWorld: true, hasPrevBones: false, hasPrevMorph: false);
    }

    public override void Dispose()
    {
        // Release the prev instance world SSBO
        if (_prevInstanceWorldBuffers != null)
        {
            for (int i = 0; i < _prevInstanceWorldBuffers.Length; i++)
            {
                if (_mappedPrevInstanceWorldBuffers != null && i < _mappedPrevInstanceWorldBuffers.Length
                    && _mappedPrevInstanceWorldBuffers[i] != null && _prevInstanceWorldBuffers[i].Memory.Handle != 0)
                    Device.Vk.UnmapMemory(Device.LogicalDevice, _prevInstanceWorldBuffers[i].Memory);
                if (_prevInstanceWorldBuffers[i].Memory.Handle != 0)
                    Device.ResourceManager.DestroyBuffer(_prevInstanceWorldBuffers[i]);
            }
            _prevInstanceWorldBuffers = null!;
            _mappedPrevInstanceWorldBuffers = null!;
        }

        foreach (var primitive in _primitives)
            primitive.Dispose();

        // Unified highlighting: release the highlight pool (Bounds instance boxes)
        DisposeHighlights();

        base.Dispose();
    }
}
