// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Vulkan;
using System.Runtime.CompilerServices;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkTexture = Season.Platforms.Shared.LinuxAndroid.Vulkan.Texture;

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

/// <summary>
/// Common base class for VKSprite2D / VKSprite3D, aligned 1:1 with DXSpriteQuad:
///   - Matrix / Material UBOs (N-buffered)
///   - Main texture + 4 auxiliary PBR textures
///   - N-buffered DescriptorSet
///     (4 UBOs + 5 CombinedImageSampler bindings, sharing the same PipelineLayout as PrimitiveGroup)
///   - Global shared lighting UBO and IdentityBone UBO borrowed from VKPrimitiveGroup static fields
///   - DrawQuad template (Transparent PSO, VkCmdDraw with 6 vertices and no IB)
/// Derived classes only need to care about vertex-buffer strategy + Update semantics;
/// VKSprite2D also owns the 2D orthographic camera path.
/// </summary>
internal unsafe abstract class VKSpriteQuad : ITextureHolder, IDisposable
{
    /// <summary>
    /// 3B/B2: the binding numbers the set-0 writes below cover, ascending. The Sprite path shares set 0's layout with the
    /// 3D path, so this list must equal <c>Pipeline.SceneSetBindings</c>; the Vulkan harness checks exactly that.
    /// </summary>
    internal static readonly uint[] SpriteSetBindings =
    {
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 21, 26,
    };

    // Sprite identifier (usually the main-texture path, used for cache keys and logs)
    internal string Name = string.Empty;

    // Matrix / Material UBOs (N-buffered)
    protected BufferResource[] _matrixBuffers = null!;
    protected byte*[] _mappedMatrixBuffers = null!;

    protected BufferResource[] _materialBuffers = null!;
    protected byte*[] _mappedMaterialBuffers = null!;

    // N-buffered DescriptorSet
    // synchronized with the frame ring to avoid conflicts across in-flight frames
    protected DescriptorSet[] _descriptorSets = null!;

    // Main texture
    internal VkTexture? VKTexture;

    // Auxiliary PBR textures (kept for future advanced material effects)
    internal VkTexture? NormalMap;
    internal VkTexture? MetallicRoughnessMap;
    internal VkTexture? OcclusionMap;
    internal VkTexture? EmissiveMap;

    // ITextureHolder
    private Season.Controls.Texture _texture = null!;
    public Season.Controls.Texture Texture
    {
        get => _texture;
        set => _texture = value;
    }

    // First-frame guard: skip Draw before the first Update completes
    // to avoid misalignment from identity matrices
    protected bool _transformInitialized;

    /// <summary>true: bind the screen-space view set (2D); false: bind the main view set (3D billboards).</summary>
    protected virtual bool ScreenSpaceView => true;

    // Descriptor-set invalidation detection:
    // when the underlying View of the main texture changes
    // (after in-place rebuild via RecreateComputeStorage),
    // all N-buffered descriptor sets must be rewritten.
    // Otherwise CmdBindDescriptorSets would still bind descriptors baked from a destroyed ImageView,
    // leading the GPU to read freed memory and crash natively.
    // Compare Texture.ViewVersion, not View.Handle:
    // handles are heap pointers and are very likely to reuse the same value after recreation.
    ulong _cachedViewVersion;

    // 1-7: per-frame-slot cache, aligned with _descriptorSets, recording which cube view is currently written to binding 16.
    // Like _cachedViewVersion, compare ViewVersion instead of handles.
    // 3B: the six frame-level lighting inputs no longer live in this set at all -- they moved to the per-view globals
    // set 3 (Pipeline.PrepareViewGlobals), so every per-sprite version cache is gone.

    // ============================================================
    // Instance UBO creation / release
    // ============================================================

    protected void CreateMatrixBuffer()
    {
        int n = (int)Device.frameCount;
        _matrixBuffers = new BufferResource[n];
        _mappedMatrixBuffers = new byte*[n];
        for (int i = 0; i < n; i++)
            _matrixBuffers[i] = Device.ResourceManager.CreateConstantBuffer(
                (uint)Unsafe.SizeOf<MatrixBuffer>(),
                out _mappedMatrixBuffers[i]);

        // Initialize to identity matrices so frames not yet updated under N-buffering
        // do not read garbage values
        var identity = new MatrixBuffer
        {
            World = Matrix4x4.Transpose(Matrix4x4.Identity),
        };
        for (int i = 0; i < n; i++)
            Unsafe.Write(_mappedMatrixBuffers[i], identity);
    }

    protected void CreateMaterialBuffer()
    {
        int n = (int)Device.frameCount;
        _materialBuffers = new BufferResource[n];
        _mappedMaterialBuffers = new byte*[n];
        for (int i = 0; i < n; i++)
            _materialBuffers[i] = Device.ResourceManager.CreateConstantBuffer(
                (uint)Unsafe.SizeOf<MaterialParams>(),
                out _mappedMaterialBuffers[i]);
    }

    protected void DisposeMatrixBuffers()
    {
        var rm = Device.ResourceManager;
        if (_matrixBuffers != null && rm != null)
        {
            for (int i = 0; i < _matrixBuffers.Length; i++)
            {
                if (_mappedMatrixBuffers[i] != null && _matrixBuffers[i].Memory.Handle != 0)
                    Device.Vk.UnmapMemory(Device.LogicalDevice, _matrixBuffers[i].Memory);
                rm.DestroyBuffer(_matrixBuffers[i]);
            }
            _matrixBuffers = null!;
            _mappedMatrixBuffers = null!;
        }
    }

    protected void DisposeMaterialBuffers()
    {
        var rm = Device.ResourceManager;
        if (_materialBuffers != null && rm != null)
        {
            for (int i = 0; i < _materialBuffers.Length; i++)
            {
                if (_mappedMaterialBuffers[i] != null && _materialBuffers[i].Memory.Handle != 0)
                    Device.Vk.UnmapMemory(Device.LogicalDevice, _materialBuffers[i].Memory);
                rm.DestroyBuffer(_materialBuffers[i]);
            }
            _materialBuffers = null!;
            _mappedMaterialBuffers = null!;
        }
    }

    // ============================================================
    // Quad vertices / shared material initialization
    // ============================================================

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

    protected void InitializeMaterial(uint renderMode = 0u)
    {
        var materialParams = new MaterialParams
        {
            BaseColor = new Vector4(1, 1, 1, 1),
            EmissiveFactor = Vector4.Zero,
            MetallicFactor = 0f,
            RoughnessFactor = 1f,
            UseAlbedoMap = VKTexture != null ? 1u : 0u,
            UseNormalMap = NormalMap != null ? 1u : 0u,
            UseMetallicRoughnessMap = MetallicRoughnessMap != null ? 1u : 0u,
            UseOcclusionMap = OcclusionMap != null ? 1u : 0u,
            UseEmissiveMap = EmissiveMap != null ? 1u : 0u,
            AlphaCutoff = 0.5f,
            AlphaMode = 2u, // BLEND (Sprite uses the Transparent PSO)
            RenderMode = renderMode
        };

        for (int i = 0; i < _materialBuffers.Length; i++)
            Unsafe.Write(_mappedMaterialBuffers[i], materialParams);
    }

    // ============================================================
    // DescriptorSet allocation + write
    // (4 UBOs + 5 material samplers + shadow atlas + 1-7 env cube
    //  + 2 storage buffers + 1 placeholder UBO + 4 fog bindings, 25 bindings in total)
    // ============================================================

    /// <summary>
    /// Call after resources are ready: allocate and write one DescriptorSet per frame for this Sprite.
    /// Callers must invoke this again after adding/replacing PBR textures so handles are rewritten.
    /// </summary>
    protected void AllocateAndWriteDescriptorSets()
    {
        // A Sprite2D loaded on the background thread allocates and writes its per-frame sets here. The frame already
        // recording may have bound the same slots, so the whole allocate-and-write stays under the GPU gate;
        // see Device.EnterGpuAccessScope.
        using var gpuAccess = Device.EnterGpuAccessScope();

        int n = (int)Device.frameCount;
        if (_descriptorSets == null || _descriptorSets.Length != n)
        {
            // Release old sets, if any
            if (_descriptorSets != null)
            {
                for (int i = 0; i < _descriptorSets.Length; i++)
                    Device.DescriptorAllocator?.FreeSet(_descriptorSets[i]);
            }
            _descriptorSets = new DescriptorSet[n];
            for (int i = 0; i < n; i++)
                _descriptorSets[i] = Device.DescriptorAllocator.AllocateSet(Pipeline.SetLayout);
        }

        // 1-7: per-slot version cache for binding 16
        // 3B: gone with the move to set 3; no per-sprite lighting-input state remains.

        for (int fi = 0; fi < n; fi++)
            WriteDescriptorSet(fi);

        // Record the current main-texture View version for DrawQuad invalidation checks
        _cachedViewVersion = (VKTexture ?? Device.White).ViewVersion;
    }

    void WriteDescriptorSet(int fi)
    {
        // Same rule as VKPrimitiveGroup.WriteDescriptorSet: the set of a frame still on the GPU must not be rewritten.
        Device.WaitForFrameSlot(fi);

        var fallback = Device.White;
        var matrixInfo = new DescriptorBufferInfo
        { Buffer = _matrixBuffers[fi].Buffer, Offset = 0, Range = Vk.WholeSize };
        var lightInfo = new DescriptorBufferInfo
        { Buffer = VKPrimitiveGroup.LightConstantBuffers[fi].Buffer, Offset = 0, Range = Vk.WholeSize };
        var materialInfo = new DescriptorBufferInfo
        { Buffer = _materialBuffers[fi].Buffer, Offset = 0, Range = Vk.WholeSize };
        var placeholderInfo = new DescriptorBufferInfo
        { Buffer = Pipeline.DefaultTextDrawParamsBuffer.Buffer, Offset = 0, Range = Vk.WholeSize };
        // bindings 9/10/11 are statically declared in the global SetLayout
        // (all shaders reference them statically),
        // so even though the Sprite path does not use them, valid placeholder resources must still be written.
        // Otherwise they become wild descriptors and cause rendering corruption on Android.
        var instanceBoneInfo = new DescriptorBufferInfo
        { Buffer = Pipeline.IdentityInstanceBoneBuffers[fi].Buffer, Offset = 0, Range = Vk.WholeSize };
        var morphInfo = new DescriptorBufferInfo
        { Buffer = Pipeline.DefaultMorphDeltasBuffers[fi].Buffer, Offset = 0, Range = Vk.WholeSize };
        var textDrawParamsInfo = new DescriptorBufferInfo
        { Buffer = Pipeline.DefaultTextDrawParamsBuffer.Buffer, Offset = 0, Range = Vk.WholeSize };

        var imgInfos = stackalloc DescriptorImageInfo[5];
        imgInfos[0] = new DescriptorImageInfo
        { ImageView = (VKTexture ?? fallback).View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        imgInfos[1] = new DescriptorImageInfo
        { ImageView = (NormalMap ?? fallback).View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        imgInfos[2] = new DescriptorImageInfo
        { ImageView = (MetallicRoughnessMap ?? fallback).View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        imgInfos[3] = new DescriptorImageInfo
        { ImageView = (OcclusionMap ?? fallback).View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        imgInfos[4] = new DescriptorImageInfo
        { ImageView = (EmissiveMap ?? fallback).View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };

        var set = _descriptorSets[fi];
        // 3B: 18 writes -- the 16 prefix bindings plus 21 and the per-instance extras table. The six lighting
        // inputs that used to be 16..20/25 now live in set 3, so neither the descriptor write nor the version cache is
        // per-sprite any more.
        var writes = stackalloc WriteDescriptorSet[18];
        writes[0] = MakeBufferWrite(set, 0, DescriptorType.UniformBuffer, &matrixInfo);
        writes[1] = MakeBufferWrite(set, 1, DescriptorType.UniformBuffer, &lightInfo);
        writes[2] = MakeBufferWrite(set, 2, DescriptorType.UniformBuffer, &materialInfo);
        writes[3] = MakeBufferWrite(set, 3, DescriptorType.UniformBuffer, &placeholderInfo);
        for (int i = 0; i < 5; i++)
            writes[4 + i] = MakeImageWrite(set, (uint)(4 + i), imgInfos + i);
        writes[9] = MakeBufferWrite(set, 9, DescriptorType.StorageBuffer, &instanceBoneInfo);
        writes[10] = MakeBufferWrite(set, 10, DescriptorType.StorageBuffer, &morphInfo);
        writes[11] = MakeBufferWrite(set, 11, DescriptorType.UniformBuffer, &textDrawParamsInfo);

        // 1-5: binding 12 shadow atlas.
        // Same pattern as VKPrimitiveGroup: ShadowMap is created during app initialization,
        // so it must be non-null when ShadowsEnabled is true.
        // When shadows are disabled, ShadowMap is null and a placeholder must be supplied -
        // stackalloc memory is not zero-initialized, and an unwritten writes[12]
        // would contain stack garbage that causes the WSL Vulkan driver to abort on an invalid descriptor.
        var shadowInfo = default(DescriptorImageInfo);
        if (Season.Rendering.FrameSchedule.ShadowMap is VKRenderTarget shadowRt && shadowRt.DepthView.Handle != 0)
        {
            shadowInfo = new DescriptorImageInfo
            { ImageView = shadowRt.DepthView, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        }
        else
        {
            // Shadows disabled: fill binding 12 with the White placeholder texture
            // to avoid UB from stack garbage
            shadowInfo = new DescriptorImageInfo
            { ImageView = fallback.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        }
        writes[12] = MakeImageWrite(set, 12, &shadowInfo);

        // 2-3 Step C: previous-frame SSBO data (binding 13/14/15),
        // using default zero-value placeholders
        var prevBoneInfo = new DescriptorBufferInfo
        { Buffer = Pipeline.DefaultPrevBoneBuffers[fi].Buffer, Offset = 0, Range = Vk.WholeSize };
        var prevInstanceWorldInfo = new DescriptorBufferInfo
        { Buffer = Pipeline.DefaultPrevInstanceWorldBuffers[fi].Buffer, Offset = 0, Range = Vk.WholeSize };
        var prevMorphWeightsInfo = new DescriptorBufferInfo
        { Buffer = Pipeline.DefaultPrevMorphWeightsBuffers[fi].Buffer, Offset = 0, Range = Vk.WholeSize };
        writes[13] = MakeBufferWrite(set, 13, DescriptorType.StorageBuffer, &prevBoneInfo);
        writes[14] = MakeBufferWrite(set, 14, DescriptorType.StorageBuffer, &prevInstanceWorldInfo);
        writes[15] = MakeBufferWrite(set, 15, DescriptorType.StorageBuffer, &prevMorphWeightsInfo);

        // 1-5 clause 15: binding 21 is the shadow view again, reached through the immutable
        // point sampler for the blocker search. The Sprite path shares the layout / FS
        // with the 3D path, so it must supply the same pair (12 uses the comparison sampler).
        writes[16] = MakeImageWrite(set, 21, &shadowInfo);

        // B2: the 22/23/24 fog placeholders are gone. The transparent stage reads the fog trio from its own set 1, and no
        // shader declares 22/23/24 in set 0, so the previous three writes bound nothing.

        // Generic per-instance extras (binding 26): the VS declares the table in every variant, so the Sprite path must
        // still write it, with the shared white record. isInstanced is 0 here, so nothing ever reads it.
        var extrasInfo = new DescriptorBufferInfo
        { Buffer = Pipeline.DefaultInstanceExtrasBuffers[fi].Buffer, Offset = 0, Range = Vk.WholeSize };
        writes[17] = MakeBufferWrite(set, Pipeline.InstanceExtrasBinding, DescriptorType.StorageBuffer, &extrasInfo);

        Device.Vk.UpdateDescriptorSets(Device.LogicalDevice, 18, writes, 0, null);
    }

    // 3B: RefreshSceneBindings(fi) is gone. The six frame-level lighting inputs are written once per view into
    // the globals set 3 by Pipeline.PrepareViewGlobals, which cannot invalidate a recording command buffer:
    // each view owns its own set per frame slot, and the previous owner of that slot retired frameCount frames ago.


    static WriteDescriptorSet MakeBufferWrite(DescriptorSet set, uint binding, DescriptorType descriptorType, DescriptorBufferInfo* info)
    {
        return new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = binding,
            DstArrayElement = 0,
            DescriptorType = descriptorType,
            DescriptorCount = 1,
            PBufferInfo = info
        };
    }

    static WriteDescriptorSet MakeImageWrite(DescriptorSet set, uint binding, DescriptorImageInfo* info)
    {
        return new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = binding,
            DstArrayElement = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            PImageInfo = info
        };
    }

    void DisposeDescriptorSets()
    {
        if (_descriptorSets != null)
        {
            for (int i = 0; i < _descriptorSets.Length; i++)
                Device.DescriptorAllocator?.FreeSet(_descriptorSets[i]);
            _descriptorSets = null!;
        }
    }

    // ============================================================
    // PBR texture setters (kept as extension points for future material features)
    // ============================================================

    public void SetNormalMap(string normalPath)
    {
        if (!string.IsNullOrEmpty(normalPath))
        {
            NormalMap = VkTexture.GetOrCreate(normalPath, null);
            UpdateMaterialFlags();
            AllocateAndWriteDescriptorSets();
        }
    }

    public void SetMetallicRoughnessMap(string metallicRoughnessPath)
    {
        if (!string.IsNullOrEmpty(metallicRoughnessPath))
        {
            MetallicRoughnessMap = VkTexture.GetOrCreate(metallicRoughnessPath, null);
            UpdateMaterialFlags();
            AllocateAndWriteDescriptorSets();
        }
    }

    public void SetOcclusionMap(string occlusionPath)
    {
        if (!string.IsNullOrEmpty(occlusionPath))
        {
            OcclusionMap = VkTexture.GetOrCreate(occlusionPath, null);
            UpdateMaterialFlags();
            AllocateAndWriteDescriptorSets();
        }
    }

    public void SetEmissiveMap(string emissivePath)
    {
        if (!string.IsNullOrEmpty(emissivePath))
        {
            EmissiveMap = VkTexture.GetOrCreate(emissivePath, null);
            UpdateMaterialFlags();
            AllocateAndWriteDescriptorSets();
        }
    }

    void UpdateMaterialFlags()
    {
        // Update only the texture-usage flags and preserve the other material parameters
        int fi = (int)Device.FrameIndex;
        var currentMaterial = Unsafe.Read<MaterialParams>(_mappedMaterialBuffers[fi]);
        currentMaterial.UseAlbedoMap = VKTexture != null ? 1u : 0u;
        currentMaterial.UseNormalMap = NormalMap != null ? 1u : 0u;
        currentMaterial.UseMetallicRoughnessMap = MetallicRoughnessMap != null ? 1u : 0u;
        currentMaterial.UseOcclusionMap = OcclusionMap != null ? 1u : 0u;
        currentMaterial.UseEmissiveMap = EmissiveMap != null ? 1u : 0u;
        Unsafe.Write(_mappedMaterialBuffers[fi], currentMaterial);
    }

    /// <summary>Set material parameters such as metallic, roughness, and emissive factors.</summary>
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

    // ============================================================
    // Draw template: Transparent PSO + VkCmdDraw(6, 1, 0, 0)
    // ============================================================

    protected void DrawQuad(VkBuffer vb)
    {
        var cmd = Device.GraphicsCommandBuffer;
        var vk = Device.Vk;

        // Detect main-texture View-version changes
        // (after RecreateComputeStorage, the old ImageView has been destroyed,
        // but N-buffered descriptor sets may still hold descriptors pointing to it,
        // so they must be rewritten to avoid native crashes)
        var currentViewVersion = (VKTexture ?? Device.White).ViewVersion;
        if (currentViewVersion != _cachedViewVersion)
        {
            AllocateAndWriteDescriptorSets();
            _cachedViewVersion = currentViewVersion;
        }

        // Ensure all 5 textures are transitioned to ShaderReadOnlyOptimal
        VKTexture?.EnsureReadyForRendering(cmd);
        NormalMap?.EnsureReadyForRendering(cmd);
        MetallicRoughnessMap?.EnsureReadyForRendering(cmd);
        OcclusionMap?.EnsureReadyForRendering(cmd);
        EmissiveMap?.EnsureReadyForRendering(cmd);

        int fi = (int)Device.FrameIndex;

        var set = _descriptorSets[fi];

        // Collection phase: the descriptor set is ready, so just record the handle and the world matrix; the actual draw happens in the transparent pass.
        if (VKSceneDrawCollection.Active is { } collection)
        {
            VKSceneDrawCollection.RetainOwner(this);
            collection.CaptureQuad(set, vb, QuadWorld(fi), ScreenSpaceView);
            return;
        }

        // Set the transparent rendering pipeline
        Pipeline.SetPipeline(cmd, PipelineMode.Transparent);

        // Bind the vertex buffer
        ulong offset = 0;
        vk.CmdBindVertexBuffers(cmd, 0, 1, in vb, in offset);

        vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics,
            Pipeline.PipelineLayout, 0, 1, in set, 0, null);
        Pipeline.BindViewSets(cmd,
            ScreenSpaceView ? Pipeline.ScreenViewSet : Pipeline.ActiveViewSet,
            ScreenSpaceView ? Pipeline.ScreenViewGlobals : Pipeline.ActiveViewGlobals);
        // The fog sampling trio lives in its own set 1, for the same reason as DrawPrimitiveCore: the transparent variant statically references it.
        Pipeline.BindFogSet(cmd);

        vk.CmdDraw(cmd, 6, 1, 0, 0);
    }

    /// <summary>Read the world matrix from the current frame slot's matrix UBO (stored already transposed).</summary>
    Matrix4x4 QuadWorld(int fi)
    {
        var mapped = _mappedMatrixBuffers[fi];
        if (mapped == null) return Matrix4x4.Identity;
        return Matrix4x4.Transpose(Unsafe.Read<MatrixBuffer>(mapped).World);
    }

    // ============================================================
    // Resource release (common base-class portion)
    // ============================================================

    protected void DisposeCommon()
    {
        DisposeDescriptorSets();
        DisposeMatrixBuffers();
        DisposeMaterialBuffers();
        // Note: textures are centrally managed by Device.DictionaryTexture and are not released here
    }

    public abstract void Dispose();
}
