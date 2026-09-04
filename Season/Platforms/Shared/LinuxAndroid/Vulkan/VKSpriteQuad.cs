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

    // Descriptor-set invalidation detection:
    // when the underlying View of the main texture changes
    // (after in-place rebuild via RecreateComputeStorage),
    // all N-buffered descriptor sets must be rewritten.
    // Otherwise CmdBindDescriptorSets would still bind descriptors baked from a destroyed ImageView,
    // leading the GPU to read freed memory and crash natively.
    // Compare Texture.ViewVersion, not View.Handle:
    // handles are heap pointers and are very likely to reuse the same value after recreation.
    ulong _cachedViewVersion;

    // 1-7: per-frame-slot cache, aligned with _descriptorSets,
    // recording which cube view is currently written to binding 16.
    // Like _cachedViewVersion, compare ViewVersion instead of handles.
    // This must remain per-slot because when an environment map finishes loading,
    // only the current frame slot can be safely rewritten
    // (other slots may still be in flight).
    ulong[] _cachedEnvCubeViewVersions = Array.Empty<ulong>();

    // 2-4 clause 10: per-frame-slot cache, aligned with _descriptorSets,
    // recording which DDGI atlas view is currently written to binding 17.
    // Semantics and per-slot reasoning match _cachedEnvCubeViewVersions.
    ulong[] _cachedDdgiAtlasViewVersions = Array.Empty<ulong>();

    // 2-4 Step 3: per-frame-slot cache, aligned with _descriptorSets,
    // recording which DDGI depth-atlas view is currently written to binding 18.
    // Semantics match _cachedDdgiAtlasViewVersions.
    ulong[] _cachedDdgiDepthViewVersions = Array.Empty<ulong>();

    // 2-5 Step C: per-frame-slot cache, aligned with _descriptorSets,
    // recording which cloud-noise view is currently written to binding 19.
    // Semantics match _cachedDdgiAtlasViewVersions.
    ulong[] _cachedCloudNoiseViewVersions = Array.Empty<ulong>();

    // 2-5 Step E: per-frame-slot cache, aligned with _descriptorSets,
    // recording which AP-volume view is currently written to binding 20.
    // Semantics match _cachedDdgiAtlasViewVersions.
    ulong[] _cachedAerialLutViewVersions = Array.Empty<ulong>();

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
            View = Matrix4x4.Identity,
            Projection = Matrix4x4.Identity
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
    //  + 2 storage buffers + 1 placeholder UBO, 17 bindings in total)
    // ============================================================

    /// <summary>
    /// Call after resources are ready: allocate and write one DescriptorSet per frame for this Sprite.
    /// Callers must invoke this again after adding/replacing PBR textures so handles are rewritten.
    /// </summary>
    protected void AllocateAndWriteDescriptorSets()
    {
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
        // (WriteDescriptorSet stores the cube version written this time)
        if (_cachedEnvCubeViewVersions.Length != n)
            _cachedEnvCubeViewVersions = new ulong[n];

        // 2-4 clause 10: per-slot version cache for binding 17
        // (same semantics).
        if (_cachedDdgiAtlasViewVersions.Length != n)
            _cachedDdgiAtlasViewVersions = new ulong[n];

        // 2-4 Step 3: per-slot version cache for binding 18 depth atlas
        // (same semantics).
        if (_cachedDdgiDepthViewVersions.Length != n)
            _cachedDdgiDepthViewVersions = new ulong[n];

        // 2-5 Step C: per-slot version cache for binding 19 cloud noise
        // (same semantics).
        if (_cachedCloudNoiseViewVersions.Length != n)
            _cachedCloudNoiseViewVersions = new ulong[n];

        // 2-5 Step E: per-slot version cache for binding 20 AP volume
        // (same semantics).
        if (_cachedAerialLutViewVersions.Length != n)
            _cachedAerialLutViewVersions = new ulong[n];

        for (int fi = 0; fi < n; fi++)
            WriteDescriptorSet(fi);

        // Record the current main-texture View version for DrawQuad invalidation checks
        _cachedViewVersion = (VKTexture ?? Device.White).ViewVersion;
    }

    void WriteDescriptorSet(int fi)
    {
        var fallback = Device.White;
        var matrixInfo = new DescriptorBufferInfo
        { Buffer = _matrixBuffers[fi].Buffer, Offset = 0, Range = Vk.WholeSize };
        var lightInfo = new DescriptorBufferInfo
        { Buffer = VKPrimitiveGroup.LightConstantBuffers[fi].Buffer, Offset = 0, Range = Vk.WholeSize };
        var materialInfo = new DescriptorBufferInfo
        { Buffer = _materialBuffers[fi].Buffer, Offset = 0, Range = Vk.WholeSize };
        var boneInfo = new DescriptorBufferInfo
        { Buffer = VKPrimitiveGroup.IdentityBoneBuffers[fi].Buffer, Offset = 0, Range = Vk.WholeSize };
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
        var writes = stackalloc WriteDescriptorSet[21];
        writes[0] = MakeBufferWrite(set, 0, DescriptorType.UniformBuffer, &matrixInfo);
        writes[1] = MakeBufferWrite(set, 1, DescriptorType.UniformBuffer, &lightInfo);
        writes[2] = MakeBufferWrite(set, 2, DescriptorType.UniformBuffer, &materialInfo);
        writes[3] = MakeBufferWrite(set, 3, DescriptorType.UniformBuffer, &boneInfo);
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

        // 1-7: binding 16 environment radiance cube.
        // The Sprite path shares SetLayout / FS with the 3D path,
        // so a valid descriptor must still be written here as well
        // (Pipeline.Init already prebuilds DummyBlack, and Bound is never null).
        var envCube = VKTextureCube.Bound;
        var envCubeInfo = new DescriptorImageInfo
        { ImageView = envCube.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        writes[16] = MakeImageWrite(set, VKTextureCube.EnvCubeBinding, &envCubeInfo);
        if (fi < _cachedEnvCubeViewVersions.Length)
            _cachedEnvCubeViewVersions[fi] = envCube.ViewVersion;

        // 2-4 clause 10: binding 17 DDGI irradiance atlas.
        // The Sprite path shares SetLayout / FS with the 3D path,
        // so a valid descriptor must still be written
        // (DdgiAtlasBound falls back to White and is never null).
        var ddgiAtlas = VKPrimitiveGroup.DdgiAtlasBound;
        var ddgiInfo = new DescriptorImageInfo
        { ImageView = ddgiAtlas.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        writes[17] = MakeImageWrite(set, VKPrimitiveGroup.DdgiAtlasBinding, &ddgiInfo);
        if (fi < _cachedDdgiAtlasViewVersions.Length)
            _cachedDdgiAtlasViewVersions[fi] = ddgiAtlas.ViewVersion;

        // 2-4 Step 3: binding 18 DDGI depth-moment atlas.
        // Same pattern as binding 17
        // (DdgiDepthBound falls back to White and is never null).
        var ddgiDepth = VKPrimitiveGroup.DdgiDepthBound;
        var ddgiDepthInfo = new DescriptorImageInfo
        { ImageView = ddgiDepth.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        writes[18] = MakeImageWrite(set, VKPrimitiveGroup.DdgiDepthBinding, &ddgiDepthInfo);
        if (fi < _cachedDdgiDepthViewVersions.Length)
            _cachedDdgiDepthViewVersions[fi] = ddgiDepth.ViewVersion;

        // 2-5 Step C: binding 19 cloud noise.
        // Same pattern as binding 17
        // (CloudNoiseBound falls back to White and is never null);
        // actual sampling is runtime-gated by cloudParams0.w (layer count).
        var cloudNoise = VKPrimitiveGroup.CloudNoiseBound;
        var cloudNoiseInfo = new DescriptorImageInfo
        { ImageView = cloudNoise.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        writes[19] = MakeImageWrite(set, VKPrimitiveGroup.CloudNoiseBinding, &cloudNoiseInfo);
        if (fi < _cachedCloudNoiseViewVersions.Length)
            _cachedCloudNoiseViewVersions[fi] = cloudNoise.ViewVersion;

        // 2-5 Step E: binding 20 aerial-perspective 3D LUT.
        // Same pattern as binding 17
        // (AerialLutBound falls back to DummyBlack and is never null);
        // actual sampling is runtime-gated by apParams0.x (sampling is simply skipped).
        var aerialLut = VKPrimitiveGroup.AerialLutBound;
        var aerialLutInfo = new DescriptorImageInfo
        { ImageView = aerialLut.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        writes[20] = MakeImageWrite(set, VKPrimitiveGroup.AerialLutBinding, &aerialLutInfo);
        if (fi < _cachedAerialLutViewVersions.Length)
            _cachedAerialLutViewVersions[fi] = aerialLut.ViewVersion;

        Device.Vk.UpdateDescriptorSets(Device.LogicalDevice, 21, writes, 0, null);
    }

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

        // Set the transparent rendering pipeline
        Pipeline.SetPipeline(cmd, PipelineMode.Transparent);

        // Bind the vertex buffer
        ulong offset = 0;
        vk.CmdBindVertexBuffers(cmd, 0, 1, in vb, in offset);

        int fi = (int)Device.FrameIndex;

        // 1-7: refresh binding 16 of the current frame slot to the environment radiance cube
        // active for this frame (compare ViewVersion, not handles).
        // Only this slot is touched - completion of its previous submission
        // is guaranteed by the same-slot fence waited at the end of AfterRender.
        if (fi < _cachedEnvCubeViewVersions.Length)
            VKTextureCube.RefreshBinding(_descriptorSets[fi], ref _cachedEnvCubeViewVersions[fi]);

        // 2-4 clause 10: refresh binding 17 to this frame's DDGI atlas
        // (same per-frame-slot patching pattern as envCube).
        if (fi < _cachedDdgiAtlasViewVersions.Length)
            VKPrimitiveGroup.RefreshDdgiBinding(_descriptorSets[fi], ref _cachedDdgiAtlasViewVersions[fi]);

        // 2-4 Step 3: likewise refresh binding 18 to this frame's DDGI depth atlas.
        if (fi < _cachedDdgiDepthViewVersions.Length)
            VKPrimitiveGroup.RefreshDdgiDepthBinding(_descriptorSets[fi], ref _cachedDdgiDepthViewVersions[fi]);

        // 2-5 Step C: likewise refresh binding 19 to this frame's cloud noise
        // (it is baked only once in its lifetime and converges after the first frame).
        if (fi < _cachedCloudNoiseViewVersions.Length)
            VKPrimitiveGroup.RefreshCloudNoiseBinding(_descriptorSets[fi], ref _cachedCloudNoiseViewVersions[fi]);

        // 2-5 Step E: likewise refresh binding 20 to this frame's AP volume.
        if (fi < _cachedAerialLutViewVersions.Length)
            VKPrimitiveGroup.RefreshAerialLutBinding(_descriptorSets[fi], ref _cachedAerialLutViewVersions[fi]);

        var set = _descriptorSets[fi];
        vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics,
            Pipeline.PipelineLayout, 0, 1, in set, 0, null);

        vk.CmdDraw(cmd, 6, 1, 0, 0);
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
