// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Season.Models;
using SharpGLTF.Runtime;
using SharpGLTF.Schema2;
using Silk.NET.Vulkan;
using System.Runtime.CompilerServices;

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

// Extension of glTF nodes on the Linux/Android Vulkan backend,
// used to store the corresponding PrimitiveData list
internal class GLTFNode : GltfNodeBase
{
    public List<PrimitiveData> Primitives = new();
}

/// <summary>
/// Vulkan backend for glTF models:
/// inherits VKPrimitiveGroup to reuse Matrix/Material UBO creation,
/// SyncAlpha, and three-bucket grouped drawing;
/// uses composition with GltfAsset to load the node tree, animation, and skinning data.
/// It only carries glTF-specific responsibilities:
/// N-buffered bone-matrix UBOs (written in OnBeforeDraw), animation ticking,
/// recursive primitive collection from the node tree,
/// and ProcessMaterial (five PBR textures).
/// </summary>
internal unsafe class VKModel : VKPrimitiveGroup
{
    // Reuse GltfAsset loading/playback for nodes/animation/skin through composition,
    // so single inheritance is not consumed by GltfAsset.
    readonly GltfAsset _asset = new();

    internal GltfAsset Asset => _asset;

    // Bone-matrix buffers (N-buffered)
    BufferResource[] _boneMatrixBuffers = null!;

    byte*[] _mappedBoneMatrixBuffers = null!;
    int _bonePaletteStride = 1;
    BufferResource[] _instanceBoneBuffers = null!;
    byte*[] _mappedInstanceBoneBuffers = null!;

    // 2-3 Step C (track B): prev bone palette SSBO
    // (same capacity as _boneMatrixBuffers, one Matrix4x4 per entry).
    // Before each frame upload, memcpy the current mapped region to the prev mapped region,
    // so the GPU always holds the bone palette from the previous frame.
    // Before the first frame, the content is all zero (sentinel _m33 == 0),
    // and the shader falls back to the current bone per joint.
    BufferResource[] _prevBonePaletteBuffers = null!;
    byte*[] _mappedPrevBonePaletteBuffers = null!;

    // 2-3 Step C (track C-b completion): prev morph weights SSBO
    // (one float4 = 16 bytes).
    // In the non-instanced path, the shader reads g_PrevMorphWeights[0],
    // so only one element is needed.
    // Each frame, ApplyMorphTargetsIfNeeded copies the old weights here before writing new ones,
    // so the shader can reconstruct the previous local position to compute morph velocity.
    BufferResource[] _prevMorphWeightsBuffers = null!;
    float*[] _mappedPrevMorphWeightsBuffers = null!;

    public VKModel(string name)
    {
        Name = name;
        // Inject two glTF-specific hooks: node factory + primitive processing
        _asset.CreateGLTFNodeCallback = CreateGLTFNode;
        _asset.ProcessPrimitiveCallback = ProcessPrimitive;
    }

    public void Load(Season.Controls.Model model, Season.Basic.Camera camera)
    {
        // Animation querying/switching belongs to the glTF parsing domain and does not go through IGraphics:
        // inject the asset reference into the control in the direct-load path.
        model.Asset = _asset;

        _asset.Load(model, camera);
        _asset.PlayAnimation();

        // Create the bone-matrix buffers
        // (same as DX: N UBOs with 100 matrices each)
        CreateBoneMatrixBuffer();
        CreateInstanceBoneBuffers();
        // 2-3 Step C (tracks B/C-b completion): create prev bone + prev morph-weights SSBOs
        CreatePrevBonePaletteBuffer();
        CreatePrevMorphWeightsBuffer();
        SyncSkinningMaterialParams();

        _asset.ValidateSkinData();

        // After all resources are fully ready, including the bone UBO,
        // fill back the DescriptorSet for all PrimitiveData.
        // ProcessPrimitive is called before _boneMatrixBuffers is created,
        // so this is deferred until the end of Load.
        var all = new List<PrimitiveData>();
        CollectPrimitives(all);
        foreach (var p in all) AllocateAndWriteDescriptorSets(p);
    }

    public VKModel CreateInstance(Season.Controls.Model model, Season.Basic.Camera camera)
    {
        if (_asset.Model != null)
        {
            model.Size = _asset.Model.Size;
            model.OriginalScale = _asset.Model.OriginalScale;
            // 1-3: in the shared-template path, GltfAsset.Load only fills LocalBounds
            // on the template's temporary Model.
            // This must be copied back to the user control,
            // otherwise control-level culling never becomes effective because the empty-box guard remains active.
            model.LocalBounds = _asset.Model.LocalBounds;
            // Unified transform convention: likewise, copy back the original bounds.
            // The setter triggers OnBoundsEstablished to finalize the default size,
            // so this must happen after Size/OriginalScale.
            model.LocalBoundsRaw = _asset.Model.LocalBoundsRaw;
            // 1-2: likewise, copy back the imported KHR punctual lights
            // (read-only local-space data, so shared references are enough),
            // otherwise AppendWorldLights receives an empty list in the shared-template path.
            model.ImportedPunctualLights = _asset.Model.ImportedPunctualLights;
        }

        var instance = new VKModel(Name);
        instance._transformInitialized = false;
        instance._asset.Model = model;
        instance._asset._nodeTransforms = new Dictionary<GltfNodeBase, Matrix4x4>();
        instance.CreateBoneMatrixBuffer();
        // 2-3 Step C (tracks B/C-b completion): create prev bone + prev morph-weights SSBOs
        instance.CreatePrevBonePaletteBuffer();
        instance.CreatePrevMorphWeightsBuffer();

        var nodeMap = new Dictionary<GltfNodeBase, GltfNodeBase>();
        foreach (var nodeBase in _asset.gltfNodes)
            instance.EnsureClonedNode(nodeMap, nodeBase, model, camera);

        var skinMap = new Dictionary<GLTFSkin, GLTFSkin>();
        foreach (var sourceSkin in _asset.GetAllSkins())
        {
            skinMap[sourceSkin] = new GLTFSkin
            {
                Name = sourceSkin.Name,
                InverseBindMatrices = new List<Matrix4x4>(sourceSkin.InverseBindMatrices),
                Joints = new List<GltfNodeBase>(),
            };

            foreach (var joint in sourceSkin.Joints)
                instance.EnsureClonedNode(nodeMap, joint, model, camera);

            if (sourceSkin.SkeletonRoot != null)
                instance.EnsureClonedNode(nodeMap, sourceSkin.SkeletonRoot, model, camera);

            if (sourceSkin.BindNode != null)
                instance.EnsureClonedNode(nodeMap, sourceSkin.BindNode, model, camera);
        }

        foreach (var animation in _asset._animations)
        {
            foreach (var channel in animation.Channels)
            {
                if (channel.Target?.Node != null)
                    instance.EnsureClonedNode(nodeMap, channel.Target.Node, model, camera);
            }
        }

        foreach (var nodeBase in _asset.gltfNodes)
        {
            var sourceNode = nodeBase;
            var clonedNode = nodeMap[sourceNode];
            clonedNode.Children = sourceNode.Children.Select(child => instance.EnsureClonedNode(nodeMap, child, model, camera)).ToList();

            if (sourceNode.Skin != null && skinMap.TryGetValue(sourceNode.Skin, out var clonedSkin))
                clonedNode.Skin = clonedSkin;
        }

        foreach (var pair in skinMap)
        {
            var sourceSkin = pair.Key;
            var clonedSkin = pair.Value;
            clonedSkin.Joints = sourceSkin.Joints.Select(joint => instance.EnsureClonedNode(nodeMap, joint, model, camera)).ToList();
            clonedSkin.SkeletonRoot = sourceSkin.SkeletonRoot != null ? instance.EnsureClonedNode(nodeMap, sourceSkin.SkeletonRoot, model, camera) : null;
            clonedSkin.BindNode = sourceSkin.BindNode != null ? instance.EnsureClonedNode(nodeMap, sourceSkin.BindNode, model, camera) : null;
        }

        instance._asset.gltfNodes = _asset.gltfNodes.Select(node => nodeMap[node]).ToList();
        instance._asset._nodeMap = _asset._nodeMap.ToDictionary(kvp => kvp.Key, kvp => nodeMap[kvp.Value]);
        instance._asset._animations = CloneAnimations(_asset._animations, nodeMap);
        instance._asset._animationPlayer = new GLTFAnimationPlayer();
        instance._asset._animationPlayer.Initialize(instance._asset._animations);
        instance._asset.PlayAnimation();
        // Shared-template instancing path:
        // inject the instance asset into the control, matching the semantics of direct-load Load.
        model.Asset = instance._asset;
        instance.CreateInstanceBoneBuffers();
        instance.SyncSkinningMaterialParams();

        var all = new List<PrimitiveData>();
        instance.CollectPrimitives(all);
        foreach (var primitive in all)
            instance.RewriteDescriptorSets(primitive);

        return instance;
    }

    GltfNodeBase EnsureClonedNode(Dictionary<GltfNodeBase, GltfNodeBase> nodeMap, GltfNodeBase sourceNode, Season.Controls.Model model, Season.Basic.Camera camera)
    {
        if (nodeMap.TryGetValue(sourceNode, out var existing))
            return existing;

        GltfNodeBase clonedNode;
        if (sourceNode is GLTFNode sourceVkNode)
        {
            var vkNode = new GLTFNode
            {
                Name = sourceVkNode.Name,
                LogicalIndex = sourceVkNode.LogicalIndex,
                Mesh = sourceVkNode.Mesh,
                IsJoint = sourceVkNode.IsJoint,
                JointIndex = sourceVkNode.JointIndex,
                Translation = sourceVkNode.InitialTranslation,
                Rotation = sourceVkNode.InitialRotation,
                Scale = sourceVkNode.InitialScale,
                InitialTranslation = sourceVkNode.InitialTranslation,
                InitialRotation = sourceVkNode.InitialRotation,
                InitialScale = sourceVkNode.InitialScale,
                InitialWeights = sourceVkNode.InitialWeights.Length == 0 ? Array.Empty<float>() : (float[])sourceVkNode.InitialWeights.Clone(),
                Weights = sourceVkNode.Weights.Length == 0 ? Array.Empty<float>() : (float[])sourceVkNode.Weights.Clone(),
                WeightsVersion = sourceVkNode.WeightsVersion,
                WorldTransform = sourceVkNode.WorldTransform,
            };

            foreach (var primitive in sourceVkNode.Primitives)
                vkNode.Primitives.Add(ClonePrimitiveData(primitive, vkNode, model, camera));

            clonedNode = vkNode;
        }
        else
        {
            clonedNode = new GltfNodeBase
            {
                Name = sourceNode.Name,
                LogicalIndex = sourceNode.LogicalIndex,
                Mesh = sourceNode.Mesh,
                IsJoint = sourceNode.IsJoint,
                JointIndex = sourceNode.JointIndex,
                Translation = sourceNode.InitialTranslation,
                Rotation = sourceNode.InitialRotation,
                Scale = sourceNode.InitialScale,
                InitialTranslation = sourceNode.InitialTranslation,
                InitialRotation = sourceNode.InitialRotation,
                InitialScale = sourceNode.InitialScale,
                InitialWeights = sourceNode.InitialWeights.Length == 0 ? Array.Empty<float>() : (float[])sourceNode.InitialWeights.Clone(),
                Weights = sourceNode.Weights.Length == 0 ? Array.Empty<float>() : (float[])sourceNode.Weights.Clone(),
                WeightsVersion = sourceNode.WeightsVersion,
                WorldTransform = sourceNode.WorldTransform,
            };
        }

        nodeMap[sourceNode] = clonedNode;
        return clonedNode;
    }

    PrimitiveData ClonePrimitiveData(PrimitiveData source, GLTFNode ownerNode, Season.Controls.Model model, Season.Basic.Camera camera)
    {
        var clone = new PrimitiveData
        {
            BaseVertices = source.BaseVertices != null ? (Vertex[])source.BaseVertices.Clone() : null,
            MorphTargets = source.MorphTargets != null ? new List<GLTFMorphTarget>(source.MorphTargets) : null,
            OwnerNode = ownerNode,
            LastAppliedWeightsVersion = ownerNode.WeightsVersion,
            Indices = (uint[])source.Indices.Clone(),
            Use32BitIndices = source.Use32BitIndices,
            DoubleSided = source.DoubleSided,
            LocalBoundsCenter = source.LocalBoundsCenter,
            LocalBoundsExtents = source.LocalBoundsExtents,
            BaseColorTexture = source.BaseColorTexture,
            NormalTexture = source.NormalTexture,
            MetallicRoughnessTexture = source.MetallicRoughnessTexture,
            OcclusionTexture = source.OcclusionTexture,
            EmissiveTexture = source.EmissiveTexture,
            MaterialParams = source.MaterialParams,
            OriginalBaseColorAlpha = source.OriginalBaseColorAlpha,
            OriginalAlphaCutoff = source.OriginalAlphaCutoff,
            IsTransparent = source.IsTransparent,
        };

        if (clone.BaseVertices != null && clone.MorphTargets != null && clone.MorphTargets.Count > 0)
        {
            // GPU Morph Target (mirrors DX ClonePrimitiveData):
            // the vertex buffer always stays in rest pose,
            // and the shader blends the delta by MaterialParams.morphWeights.
            // The morph-delta SSBO is shared with the source
            // (it contains static geometry deltas that are identical per instance),
            // so OwnsMorphDeltasBuffer stays false and is released by the source.
            clone.Vertices = new List<Vertex>(clone.BaseVertices);
            clone.MorphDeltasBuffer = source.MorphDeltasBuffer;
        }
        else
        {
            clone.Vertices = new List<Vertex>(source.Vertices);
        }

        ApplyInstanceMaterialOverrides(clone, model);

        clone.VertexBuffer = Device.ResourceManager.CreateVertexBuffer(clone.Vertices.ToArray());
        clone.IndexBuffer = Device.ResourceManager.CreateIndexBuffer(clone.Indices);

        CreateMatrixBuffer(clone);
        CreateMaterialBuffer(clone);

        var matrices = new MatrixBuffer
        {
            World = Matrix4x4.Transpose(Matrix4x4.Identity),
            View = Matrix4x4.Transpose(camera.View),
            Projection = Matrix4x4.Transpose(camera.Projection)
        };

        for (int i = 0; i < Device.frameCount; i++)
        {
            Unsafe.Write(clone.MappedMatrixBuffers[i], matrices);
            Unsafe.Write(clone.MappedMaterialBuffers[i], clone.MaterialParams);
        }

        AllocateAndWriteDescriptorSets(clone);
        return clone;
    }

    static void ApplyInstanceMaterialOverrides(PrimitiveData primitive, Season.Controls.Model model)
    {
        var colorTint = model.MaterialColor ?? Vector4.One;
        primitive.MaterialParams.BaseColor *= colorTint;
        primitive.MaterialParams.RenderMode = model.Unlit ? 0u : 1u;
        primitive.MaterialParams.IsSkinned = primitive.OwnerNode?.Skin != null ? 1u : 0u;
        primitive.MaterialParams.BonePaletteStride = 1;
        primitive.OriginalBaseColorAlpha = primitive.MaterialParams.BaseColor.W;
    }

    static List<GLTFAnimation> CloneAnimations(List<GLTFAnimation> sourceAnimations, Dictionary<GltfNodeBase, GltfNodeBase> nodeMap)
    {
        var result = new List<GLTFAnimation>(sourceAnimations.Count);
        foreach (var sourceAnimation in sourceAnimations)
        {
            var clonedAnimation = new GLTFAnimation
            {
                Name = sourceAnimation.Name,
            };

            foreach (var sourceChannel in sourceAnimation.Channels)
            {
                var clonedSampler = new AnimationSampler
                {
                    Inputs = new List<float>(sourceChannel.Sampler?.Inputs ?? new List<float>()),
                    Values = new List<float>(sourceChannel.Sampler?.Values ?? new List<float>()),
                    InTangents = sourceChannel.Sampler?.InTangents != null ? new List<float>(sourceChannel.Sampler.InTangents) : null,
                    OutTangents = sourceChannel.Sampler?.OutTangents != null ? new List<float>(sourceChannel.Sampler.OutTangents) : null,
                    OutputElementCount = sourceChannel.Sampler?.OutputElementCount ?? 4,
                    Interpolation = sourceChannel.Sampler?.Interpolation ?? Season.Models.AnimationInterpolationMode.Linear,
                };

                var clonedChannel = new Season.Models.AnimationChannel
                {
                    Sampler = clonedSampler,
                    Target = sourceChannel.Target == null
                        ? null
                        : new AnimationChannelTarget
                        {
                            Node = sourceChannel.Target.Node != null ? nodeMap[sourceChannel.Target.Node] : null,
                            Path = sourceChannel.Target.Path,
                        }
                };

                clonedAnimation.Samplers.Add(clonedSampler);
                if (clonedChannel.Target != null)
                    clonedAnimation.Channels.Add(clonedChannel);
            }

            result.Add(clonedAnimation);
        }

        return result;
    }

    /// <summary>VKModel's BoneMatrixBuffers points to its own N-buffered UBOs,
    /// overriding the base class's default IdentityBoneBuffers.</summary>
    protected override BufferResource[] BoneMatrixBuffers => _boneMatrixBuffers;
    protected override BufferResource[] InstanceBoneBuffers
        => _instanceBoneBuffers != null && _instanceBoneBuffers.Length > 0
            ? _instanceBoneBuffers
            : Pipeline.IdentityInstanceBoneBuffers;

    GltfNodeBase CreateGLTFNode(SharpGLTF.Schema2.Node node)
    {
        return new GLTFNode
        {
            Name = node.Name ?? $"Node_{node.LogicalIndex}",
            LogicalIndex = node.LogicalIndex,
            Mesh = node.Mesh,
            Skin = node.Skin != null ? _asset.CreateSkin(node.Skin) : null,
            IsJoint = node.IsSkinJoint,
            JointIndex = node.IsSkinJoint ? _asset.GetJointIndex(node) : -1
        };
    }

    void CreateBoneMatrixBuffer()
    {
        int n = (int)Device.frameCount;
        _boneMatrixBuffers = new BufferResource[n];
        _mappedBoneMatrixBuffers = new byte*[n];
        ulong size = (ulong)(Unsafe.SizeOf<Matrix4x4>() * MaxBones);

        var identity = Matrix4x4.Identity;
        for (int i = 0; i < n; i++)
        {
            _boneMatrixBuffers[i] = Device.ResourceManager.CreateConstantBuffer(
                (uint)size, out _mappedBoneMatrixBuffers[i]);
            // Fill with Identity by default to avoid reading garbage
            // when animation is not playing yet
            for (int j = 0; j < MaxBones; j++)
                Unsafe.Write(_mappedBoneMatrixBuffers[i] + j * sizeof(float) * 16, identity);
        }
    }

    void CreateInstanceBoneBuffers()
    {
        _bonePaletteStride = Math.Max(1, _asset.GetAllSkins().Sum(static skin => skin.Joints.Count));

        int n = (int)Device.frameCount;
        _instanceBoneBuffers = new BufferResource[n];
        _mappedInstanceBoneBuffers = new byte*[n];
        ulong size = (ulong)(Unsafe.SizeOf<Matrix4x4>() * _bonePaletteStride);
        var identity = Matrix4x4.Identity;

        for (int i = 0; i < n; i++)
        {
            _instanceBoneBuffers[i] = Device.ResourceManager.CreateBuffer(
                size,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

            void* mapped;
            if (Device.Vk.MapMemory(Device.LogicalDevice, _instanceBoneBuffers[i].Memory, 0, size, 0, &mapped) != Result.Success)
                throw new Exception("vkMapMemory (VKModel instance bone storage) failed");

            _mappedInstanceBoneBuffers[i] = (byte*)mapped;
            for (int j = 0; j < _bonePaletteStride; j++)
                Unsafe.Write(_mappedInstanceBoneBuffers[i] + j * Unsafe.SizeOf<Matrix4x4>(), identity);
        }
    }

    // 2-3 Step C (track B): create the prev bone-palette SSBO
    // (same capacity as _boneMatrixBuffers)
    void CreatePrevBonePaletteBuffer()
    {
        int n = (int)Device.frameCount;
        _prevBonePaletteBuffers = new BufferResource[n];
        _mappedPrevBonePaletteBuffers = new byte*[n];
        ulong size = (ulong)(Unsafe.SizeOf<Matrix4x4>() * MaxBones);

        for (int i = 0; i < n; i++)
        {
            _prevBonePaletteBuffers[i] = Device.ResourceManager.CreateBuffer(
                size,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
            void* mapped;
            if (Device.Vk.MapMemory(Device.LogicalDevice, _prevBonePaletteBuffers[i].Memory, 0, size, 0, &mapped) != Result.Success)
                throw new Exception("vkMapMemory (PrevBonePaletteBuffers) failed");
            _mappedPrevBonePaletteBuffers[i] = (byte*)mapped;
            new Span<byte>(mapped, (int)size).Clear();
        }
    }

    // 2-3 Step C (track C-b completion): create the prev morph-weights SSBO
    // (one float4 = 16 bytes)
    void CreatePrevMorphWeightsBuffer()
    {
        int n = (int)Device.frameCount;
        _prevMorphWeightsBuffers = new BufferResource[n];
        _mappedPrevMorphWeightsBuffers = new float*[n];
        ulong size = 16; // One float4

        for (int i = 0; i < n; i++)
        {
            _prevMorphWeightsBuffers[i] = Device.ResourceManager.CreateBuffer(
                size,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
            void* mapped;
            if (Device.Vk.MapMemory(Device.LogicalDevice, _prevMorphWeightsBuffers[i].Memory, 0, size, 0, &mapped) != Result.Success)
                throw new Exception("vkMapMemory (PrevMorphWeightsBuffers) failed");
            _mappedPrevMorphWeightsBuffers[i] = (float*)mapped;
            new Span<byte>(mapped, (int)size).Clear();
        }
    }

    // 2-3 Step C: override the base virtual methods to return the actual prev SSBOs
    protected override DescriptorBufferInfo GetPrevBoneBufferInfo(int fi)
        => _prevBonePaletteBuffers != null && fi < _prevBonePaletteBuffers.Length
            ? new() { Buffer = _prevBonePaletteBuffers[fi].Buffer, Offset = 0, Range = Vk.WholeSize }
            : base.GetPrevBoneBufferInfo(fi);

    protected override DescriptorBufferInfo GetPrevMorphWeightsBufferInfo(int fi)
        => _prevMorphWeightsBuffers != null && fi < _prevMorphWeightsBuffers.Length
            ? new() { Buffer = _prevMorphWeightsBuffers[fi].Buffer, Offset = 0, Range = Vk.WholeSize }
            : base.GetPrevMorphWeightsBufferInfo(fi);

    void SyncSkinningMaterialParams()
    {
        var primitives = new List<PrimitiveData>();
        CollectPrimitives(primitives);
        foreach (var primitive in primitives)
        {
            primitive.MaterialParams.IsSkinned = primitive.OwnerNode?.Skin != null ? 1u : 0u;
            primitive.MaterialParams.BonePaletteStride = (uint)Math.Max(1, _bonePaletteStride);
            for (int i = 0; i < Device.frameCount; i++)
                Unsafe.Write(primitive.MappedMaterialBuffers[i], primitive.MaterialParams);
        }
    }

    void ProcessPrimitive(MeshPrimitive meshPrimitive, GltfNodeBase node, Season.Controls.Model model, Season.Basic.Camera camera)
    {
        var primitiveData = CreatePrimitiveData(meshPrimitive, node, model, camera);

        var gltfNode = (GLTFNode)node;
        gltfNode.Primitives.Add(primitiveData);
    }

    PrimitiveData CreatePrimitiveData(MeshPrimitive primitive, GltfNodeBase node, Season.Controls.Model model, Season.Basic.Camera camera)
    {
        var p = new PrimitiveData();

        // Load geometry data
        var verticleIndices = GLTFTools.LoadMeshPrimitive(primitive);
        var morphTargets = GLTFTools.LoadMorphTargets(primitive, verticleIndices.vertices.Count);
        p.OwnerNode = node;
        p.LastAppliedWeightsVersion = node.WeightsVersion;
        p.MaterialParams.IsSkinned = node.Skin != null ? 1u : 0u;
        p.MaterialParams.BonePaletteStride = 1;
        if (morphTargets.Count > 0)
        {
            p.BaseVertices = verticleIndices.vertices.ToArray();
            p.MorphTargets = morphTargets;
            // GPU Morph Target (mirrors DX ProcessPrimitive):
            // the vertex buffer directly uses the rest pose,
            // and the delta is blended on the shader side.
            // Never deform it again on the CPU, or the result would be double displacement
            // on top of the shader morph, and prev velocity reconstruction
            // (from rest pose + prevWeights) would break.
            p.Vertices = new List<Vertex>(p.BaseVertices);
            CreateMorphDeltaBuffer(p, p.BaseVertices, morphTargets);
        }
        else
        {
            p.Vertices = verticleIndices.vertices;
        }
        p.Indices = verticleIndices.indices.ToArray();
        p.Use32BitIndices = p.Indices.Any(i => i > ushort.MaxValue);
        p.DoubleSided = false;
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
        ProcessMaterial(primitive, p);

        if (p.MorphTargets != null && p.MorphTargets.Count > 0)
        {
            p.MaterialParams.HasMorphTargets = 1u;
            p.MaterialParams.MorphTargetCount = (uint)p.MorphTargets.Count;
            p.MaterialParams.MorphVertexCount = (uint)p.BaseVertices!.Length;
            p.MaterialParams.MorphWeights = Vector4.Zero;
            for (int i = 0; i < Device.frameCount; i++)
                Unsafe.Write(p.MappedMaterialBuffers[i], p.MaterialParams);
        }

        // Initialize the matrix buffer with identity matrices for all frames
        var matrices = new MatrixBuffer
        {
            World = Matrix4x4.Transpose(Matrix4x4.Identity),
            View = Matrix4x4.Transpose(camera.View),
            Projection = Matrix4x4.Transpose(camera.Projection)
        };
        for (int i = 0; i < Device.frameCount; i++)
            Unsafe.Write(p.MappedMatrixBuffers[i], matrices);

        // Note: the DescriptorSet is allocated uniformly at the end of Load,
        // because _boneMatrixBuffers must already exist
        return p;
    }

    void UpdateMorphTargetsRecursive(List<GltfNodeBase> nodes)
    {
        foreach (var nodeBase in nodes)
        {
            if (nodeBase is GLTFNode node)
            {
                foreach (var primitive in node.Primitives)
                    ApplyMorphTargetsIfNeeded(primitive, node);
            }

            UpdateMorphTargetsRecursive(nodeBase.Children);
        }
    }

    void ApplyMorphTargetsIfNeeded(PrimitiveData primitive, GltfNodeBase node)
    {
        if (primitive.BaseVertices == null || primitive.MorphTargets == null || primitive.MorphTargets.Count == 0)
            return;

        if (primitive.LastAppliedWeightsVersion == node.WeightsVersion)
            return;

        // 2-3 Step C (track C-b completion): before writing new weights,
        // first copy the current weights into the prev morph SSBO.
        // At this point, the CB still contains the old weights from the previous write,
        // which is exactly the "previous frame" data the shader needs.
        // On the first frame, the prev SSBO is all zero (sentinel),
        // so the shader falls back to current data and velocity has no morph contribution.
        CaptureCurrentMorphWeightsToPrev(primitive);

        // Phase 3: GPU Morph Target - write weights into the MaterialParams CB,
        // and let the shader blend the delta.
        // The vertex buffer always stays in rest pose and is never touched here
        // (mirrors DX ApplyMorphTargetsIfNeeded).
        // Shell boxes (whose shell deltas are expanded by shell-vertex layout and share weights with the source):
        // the same weights are synchronized into the Material UBOs of both shell primitives.
        // The shell and source share animation weights and stay aligned frame by frame through
        // the same VS morph path (see VKPrimitiveGroup.AttachShellMorph).
        // Shell boxes only exist when the source has morph targets, and dirty gating follows the source.
        WriteMorphWeightsToCB(primitive, node.Weights);
        if (_wireframeBoxes != null)
        {
            for (int i = 0; i < _wireframeBoxes.Count; i++)
            {
                var shell = _wireframeBoxes[i];
                if (shell != null && shell.SourcePrimitive == primitive)
                {
                    WriteMorphWeightsToCB(shell.Face, node.Weights);
                    WriteMorphWeightsToCB(shell.Edges, node.Weights);
                }
            }
        }
        primitive.LastAppliedWeightsVersion = node.WeightsVersion;
    }

    /// <summary>2-3 Step C (track C-b completion): read the current morph weights
    /// from the N-buffered CB and write them into the prev SSBO.
    /// The prev morph SSBO is initialized lazily on first use.</summary>
    void CaptureCurrentMorphWeightsToPrev(PrimitiveData primitive)
    {
        if (_prevMorphWeightsBuffers == null)
            return;

        // Read the current weights from this frame's CB
        // (WriteMorphWeightsToCB has not been called yet, so these are still the old weights)
        int fi = (int)Device.FrameIndex;
        if (fi >= _mappedPrevMorphWeightsBuffers.Length || _mappedPrevMorphWeightsBuffers[fi] == null)
            return;

        var mp = Unsafe.Read<MaterialParams>(primitive.MappedMaterialBuffers[fi]);
        _mappedPrevMorphWeightsBuffers[fi][0] = mp.MorphWeights.X;
        _mappedPrevMorphWeightsBuffers[fi][1] = mp.MorphWeights.Y;
        _mappedPrevMorphWeightsBuffers[fi][2] = mp.MorphWeights.Z;
        _mappedPrevMorphWeightsBuffers[fi][3] = mp.MorphWeights.W;
    }

    /// <summary>
    /// Phase 3: write morph weights into the current frame's N-buffered MaterialParams CB.
    /// At most four morph targets are supported; extra weights are ignored.
    /// </summary>
    static void WriteMorphWeightsToCB(PrimitiveData primitive, float[] weights)
    {
        int n = (int)Device.frameCount;
        for (int i = 0; i < n; i++)
        {
            var mp = Unsafe.Read<MaterialParams>(primitive.MappedMaterialBuffers[i]);
            mp.MorphWeights = new Vector4(
                weights.Length > 0 ? weights[0] : 0,
                weights.Length > 1 ? weights[1] : 0,
                weights.Length > 2 ? weights[2] : 0,
                weights.Length > 3 ? weights[3] : 0);
            Unsafe.Write(primitive.MappedMaterialBuffers[i], mp);
        }
    }

    void ProcessMaterial(MeshPrimitive primitive, PrimitiveData p)
    {
        var modelRoot = primitive.LogicalParent.LogicalParent;
        var (gLTFMaterial1, images) = GLTFTools.LoadMaterial(modelRoot, primitive);

        p.MaterialParams = new MaterialParams { RenderMode = _asset.Model.Unlit ? 0u : 1u };
        p.MaterialParams.IsSkinned = p.OwnerNode?.Skin != null ? 1u : 0u;
        p.MaterialParams.BonePaletteStride = 1;

        // Set transparency parameters based on AlphaMode.
        // Only BLEND is truly transparent and requires blending;
        // MASK uses the Opaque PSO plus shader discard.
        if (gLTFMaterial1 != null)
        {
            p.IsTransparent = gLTFMaterial1.AlphaMode == "BLEND";
            p.DoubleSided = gLTFMaterial1.DoubleSided;

            p.MaterialParams.AlphaMode = gLTFMaterial1.AlphaMode switch
            {
                "MASK" => 1u,
                "BLEND" => 2u,
                _ => 0u
            };

            p.MaterialParams.AlphaCutoff = gLTFMaterial1.AlphaCutoff;
        }
        else
        {
            p.IsTransparent = false;
            p.DoubleSided = false;
            p.MaterialParams.AlphaMode = 0u;
            p.MaterialParams.AlphaCutoff = 0.5f;
        }

        var colorTint = _asset.Model.MaterialColor ?? new Vector4(1f, 1f, 1f, 1f);
        p.MaterialParams.BaseColor = colorTint;

        if (gLTFMaterial1 != null)
        {
            // The caller-side MaterialColor acts as a tint
            // and is multiplied by the original glTF BaseColorFactor
            p.MaterialParams.BaseColor *= gLTFMaterial1.BaseColorFactor;
        }

        if (images.Count == 0)
        {
            // No textures at all: keep all UseXxxMap flags at 0
        }
        else
        {
            var baseColorImage = images[0];
            if (baseColorImage is null)
            {
                p.MaterialParams.UseAlbedoMap = 0u;
            }
            else
            {
                p.BaseColorTexture = Texture.GetOrCreate(
                    $"{_asset.Model.Name}-baseColor-{baseColorImage.LogicalIndex}", baseColorImage,
                    TextureMipPolicy.Color);
                p.MaterialParams.UseAlbedoMap = 1u;
            }

            var normalImage = images[1];
            if (normalImage is null)
            {
                p.MaterialParams.MetallicFactor = gLTFMaterial1!.MetallicFactor;
                p.MaterialParams.UseNormalMap = 0u;
            }
            else
            {
                p.NormalTexture = Texture.GetOrCreate(
                    $"{_asset.Model.Name}-normal-{normalImage.LogicalIndex}", normalImage,
                    TextureMipPolicy.Normal);
                p.MaterialParams.UseNormalMap = 1u;
            }

            var metallicRoughnessImage = images[2];
            if (metallicRoughnessImage is null)
            {
                p.MaterialParams.UseMetallicRoughnessMap = 0u;
                p.MaterialParams.RoughnessFactor = gLTFMaterial1!.RoughnessFactor;
            }
            else
            {
                p.MetallicRoughnessTexture = Texture.GetOrCreate(
                    $"{_asset.Model.Name}-metallicRoughness-{metallicRoughnessImage.LogicalIndex}", metallicRoughnessImage,
                    TextureMipPolicy.Linear);
                p.MaterialParams.UseMetallicRoughnessMap = 1u;
            }

            var occlusionImage = images[3];
            if (occlusionImage is null)
            {
                p.MaterialParams.UseOcclusionMap = 0u;
            }
            else
            {
                p.OcclusionTexture = Texture.GetOrCreate(
                    $"{_asset.Model.Name}-occlusion-{occlusionImage.LogicalIndex}", occlusionImage,
                    TextureMipPolicy.Linear);
                p.MaterialParams.UseOcclusionMap = 1u;
            }

            var emissiveImage = images[4];
            if (emissiveImage is null)
            {
                p.MaterialParams.UseEmissiveMap = 0u;
                p.MaterialParams.EmissiveFactor = gLTFMaterial1!.EmissiveFactor.AsVector4();
            }
            else
            {
                p.EmissiveTexture = Texture.GetOrCreate(
                    $"{_asset.Model.Name}-emissive-{emissiveImage.LogicalIndex}", emissiveImage,
                    TextureMipPolicy.Color);
                p.MaterialParams.UseEmissiveMap = 1u;
                p.MaterialParams.EmissiveFactor = gLTFMaterial1!.EmissiveFactor.AsVector4();
            }
        }

        // White fallback: bind a 1x1 white texture to every unresolved channel;
        // shader branches with UseXxxMap = 0 do not read it
        if (p.BaseColorTexture is null) p.BaseColorTexture = Device.White;
        if (p.NormalTexture is null) p.NormalTexture = Device.White;
        if (p.MetallicRoughnessTexture is null) p.MetallicRoughnessTexture = Device.White;
        if (p.OcclusionTexture is null) p.OcclusionTexture = Device.White;
        if (p.EmissiveTexture is null) p.EmissiveTexture = Device.White;

        // Record the original glTF BaseColor.W
        // so later Model.Alpha multiplication can be applied correctly
        p.OriginalBaseColorAlpha = p.MaterialParams.BaseColor.W;

        // Record the original glTF AlphaCutoff
        // so SyncAlpha can scale it proportionally with Model.Alpha
        p.OriginalAlphaCutoff = p.MaterialParams.AlphaCutoff;

        // Initialize the material buffer for all frames
        // to avoid flicker from other frames reading garbage values under N-buffering
        for (int i = 0; i < Device.frameCount; i++)
            Unsafe.Write(p.MappedMaterialBuffers[i], p.MaterialParams);
    }

    // 1-3: bounding-box calculation is unified through the shared
    // Season.Rendering.Bounds3D.FromVertices path (same source across all four backends)

    /// <summary>Compute bone matrices and write them into the current frame's bone UBO
    /// and dynamic storage buffer.</summary>
    void UploadBoneMatricesToGpu()
    {
        _asset._animationPlayer.UpdateBoneMatrices(_asset.GetAllSkins());
        var boneMatrices = _asset._animationPlayer.GetBoneMatricesArray();

        if (boneMatrices.Length == 0 || _mappedBoneMatrixBuffers == null) return;

        int matrixSize = Unsafe.SizeOf<Matrix4x4>();
        int totalSize = matrixSize * Math.Min(boneMatrices.Length, MaxBones);
        int fi = (int)Device.FrameIndex;

        // 2-3 Step C (track B): before uploading new bone matrices,
        // first copy the old matrices from the current frame buffer into the prev SSBO.
        // On the first frame, prev content is all zero (sentinel _m33 == 0),
        // and the shader falls back to the current bone per joint.
        if (_mappedPrevBonePaletteBuffers != null && fi < _mappedPrevBonePaletteBuffers.Length
            && _mappedPrevBonePaletteBuffers[fi] != null)
        {
            Unsafe.CopyBlock(_mappedPrevBonePaletteBuffers[fi], _mappedBoneMatrixBuffers[fi], (uint)totalSize);
        }

        fixed (void* matricesPtr = boneMatrices)
        {
            Unsafe.CopyBlock(_mappedBoneMatrixBuffers[fi], matricesPtr, (uint)totalSize);
            if (_mappedInstanceBoneBuffers != null && fi < _mappedInstanceBoneBuffers.Length && _mappedInstanceBoneBuffers[fi] != null)
            {
                int dynamicSize = matrixSize * Math.Min(boneMatrices.Length, _bonePaletteStride);
                Unsafe.CopyBlock(_mappedInstanceBoneBuffers[fi], matricesPtr, (uint)dynamicSize);
            }
        }
    }

    void ApplyUserTransformToNodeTree(GltfNodeBase nodeBase, Matrix4x4 userTransform, Season.Basic.Camera camera)
    {
        // Apply the user transform to the current node
        var finalWorldMatrix = nodeBase.WorldTransform * userTransform;

        var node = (GLTFNode)nodeBase;

        // Update the matrices for all primitives under this node
        foreach (var primitive in node.Primitives)
        {
            var matrices = new MatrixBuffer
            {
                World = Matrix4x4.Transpose(finalWorldMatrix),
                View = Matrix4x4.Transpose(camera.View),
                Projection = Matrix4x4.Transpose(camera.Projection),
                // 2-3 contract clause 6: history always comes from the CPU shadow copy
                // (Transpose(all-zero) == all-zero, so the first frame naturally uses the unwritten sentinel)
                PrevWorld = Matrix4x4.Transpose(primitive.PrevWorldMatrix),
                PrevViewProjection = Matrix4x4.Transpose(camera.PrevViewProjection),
            };
            int fi = (int)Device.FrameIndex;
            Unsafe.Write(primitive.MappedMatrixBuffers[fi], matrices);

            // Roll this frame's world matrix into the shadow copy
            // so it becomes history for the next frame
            // (advanced exactly once per primitive per frame)
            primitive.PrevWorldMatrix = finalWorldMatrix;
        }

        // Key point: recurse into child nodes and keep passing the same userTransform
        foreach (var child in nodeBase.Children)
        {
            ApplyUserTransformToNodeTree(child, userTransform, camera);
        }
    }

    public void Update(Season.Controls.Model model, float time)
    {
        bool wasInitialized = _transformInitialized;

        // Update animation through the animation player
        // (time advancement, keyframe lookup, TRS interpolation, and node-transform updates are handled internally)
        _asset._animationPlayer.Update(time, _asset.gltfNodes);
        UpdateMorphTargetsRecursive(_asset.gltfNodes);

        // Apply the user transform to the root nodes
        // (unified transform convention: converge on BuildWorldMatrix with anchor pivot, see Mesh3DBase)
        var userTransform = model.BuildWorldMatrix();

        // Find all root nodes and apply the user transform
        // (the player caches root-node list references to avoid O(N^2) lookup each frame)
        var rootNodes = _asset._animationPlayer.GetRootNodes(_asset.gltfNodes);

        foreach (var rootNode in rootNodes)
            ApplyUserTransformToNodeTree(rootNode, userTransform, Camera);

        // Update bone matrices
        UploadBoneMatricesToGpu();

        _transformInitialized = true;

        // 2-3 Step C (tracks B/C-b completion): from the second frame onward,
        // the prev bone + prev morph SSBOs contain valid data,
        // so notify the shader path that it can read them.
        if (wasInitialized)
        {
            SetPrevBonesReady();
            SetPrevMorphReady();
        }

        // Sync Model.Alpha to the material buffer of all primitives
        // (written only when changed, as determined by the base class)
        SyncAlpha(model.Alpha);

        // Unified highlighting: synchronize the wireframe flag
        // (can be toggled at runtime) + lazily build per-primitive shell geometry.
        // It is built on the first enabled frame and then kept resident;
        // when fully disabled, memory use and draw cost both stay at zero.
        // Each frame writes "node WorldTransform x userTransform" plus the face/edge dual colors
        // into each shell box.
        // This shares the same rendering source as ApplyUserTransformToNodeTree:
        // shell vertices are in node-local space, so without the node matrix the whole shell
        // would drift or scale incorrectly; face alpha pulsing is written every frame.
        _wireframeEnabled = model.Highlight.Wireframe;
        if (_wireframeEnabled)
        {
            EnsureWireframeHighlights(model.Highlight.EdgeWidth,
                MathF.Max(model.LocalSize.X, MathF.Max(model.LocalSize.Y, model.LocalSize.Z)));
            if (_wireframeBoxes != null)
            {
                for (int i = 0; i < _wireframeBoxes.Count; i++)
                {
                    var highlight = _wireframeBoxes[i];
                    if (highlight != null)
                    {
                        var nodeWorld = highlight.OwnerNode?.WorldTransform ?? Matrix4x4.Identity;
                        WriteHighlightBox(highlight, nodeWorld * userTransform,
                            model.Highlight.SurfaceColor, model.Highlight.EdgeColor);
                    }
                }
            }
        }

        // Unified highlighting: synchronize the Bounds box
        // (box geometry is built lazily on the first enabled frame;
        // face/edge dual colors are independent of the model alpha chain and written every frame;
        // do not light it up when Extents is near zero, meaning an unloaded or degenerate box)
        _boundsActive = model.Highlight.Bounds;
        if (_boundsActive)
        {
            var bounds = model.GetWorldBoundsRaw();
            if (bounds.Extents.LengthSquared() >= 1e-12f)
            {
                _boundsBox ??= CreateBoundsBox();
                WriteHighlightBox(_boundsBox,
                    Matrix4x4.CreateScale(bounds.Extents * 2f) * Matrix4x4.CreateTranslation(bounds.Center),
                    model.Highlight.SurfaceColor, model.Highlight.EdgeColor);
            }
        }

        // Unified highlighting: synchronize Outline2D state
        // (active state is collected by Graphics' OutlineMask pass; mirrors DXModel on DX)
        SetOutline2DState(model.Highlight.Outline, model.Highlight.OutlineColor, model.Highlight.OutlineWidth);
    }

    /// <summary>2-3 Step C (track B): after the prev bone-palette SSBO has been filled with valid data,
    /// set MaterialParams.HasPrevBones = 1 for all primitives.
    /// This is written only on the first call
    /// because the value does not change afterward and is guarded by early-out.</summary>
    void SetPrevBonesReady()
    {
        if (_prevBonePaletteBuffers == null)
            return;
        var primitives = new List<PrimitiveData>();
        CollectPrimitives(primitives);
        for (int i = 0; i < primitives.Count; i++)
        {
            var primitive = primitives[i];
            if (primitive.MaterialParams.HasPrevBones != 0)
                continue;
            primitive.MaterialParams.HasPrevBones = 1;
            for (int f = 0; f < Device.frameCount; f++)
                Unsafe.Write(primitive.MappedMaterialBuffers[f], primitive.MaterialParams);
        }
    }

    /// <summary>2-3 Step C (track C-b completion): after the prev morph-weights SSBO
    /// has been filled with valid data,
    /// set MaterialParams.HasPrevMorph = 1 for primitives that have morph targets.
    /// This is written only on the first call
    /// because the value does not change afterward and is guarded by early-out.</summary>
    void SetPrevMorphReady()
    {
        if (_prevMorphWeightsBuffers == null)
            return;
        var primitives = new List<PrimitiveData>();
        CollectPrimitives(primitives);
        for (int i = 0; i < primitives.Count; i++)
        {
            var primitive = primitives[i];
            if (primitive.MaterialParams.HasMorphTargets == 0 || primitive.MaterialParams.HasPrevMorph != 0)
                continue;
            primitive.MaterialParams.HasPrevMorph = 1;
            for (int f = 0; f < Device.frameCount; f++)
                Unsafe.Write(primitive.MappedMaterialBuffers[f], primitive.MaterialParams);
        }
    }

    /// <summary>Called by the base Draw: recursively walk _asset.gltfNodes
    /// and copy each GLTFNode's Primitives into result.</summary>
    protected override void CollectPrimitives(List<PrimitiveData> result)
    {
        CollectPrimitivesRecursive(_asset.gltfNodes, result);
    }

    /// <summary>Public version used by external callers such as InstancedModel
    /// to retrieve all primitives.</summary>
    public void CollectAllPrimitives(List<PrimitiveData> result)
    {
        CollectPrimitives(result);
    }

    void CollectPrimitivesRecursive(List<GltfNodeBase> nodes, List<PrimitiveData> result)
    {
        foreach (var nodeBase in nodes)
        {
            if (nodeBase is GLTFNode node)
                result.AddRange(node.Primitives);
            CollectPrimitivesRecursive(nodeBase.Children, result);
        }
    }

    /// <summary>
    /// Before recording the current frame's draw calls,
    /// ensure the bone UBO for the current FrameIndex has already been written
    /// with the latest bone matrices.
    /// This avoids reading previous-frame or identity data when Update and Draw land in different frame slots.
    /// </summary>
    protected override void OnBeforeDraw()
    {
        UploadBoneMatricesToGpu();
    }

    public override void Dispose()
    {
        foreach (var nodeBase in _asset.gltfNodes)
        {
            if (nodeBase is not GLTFNode node) continue;
            foreach (var primitive in node.Primitives)
                primitive.Dispose();
        }
        _asset._nodeMap.Clear();

        // Unified highlighting: release the highlight pool (host Bounds box)
        DisposeHighlights();

        var vk = Device.Vk;
        var d = Device.LogicalDevice;
        var rm = Device.ResourceManager;
        if (_boneMatrixBuffers != null)
        {
            for (int i = 0; i < _boneMatrixBuffers.Length; i++)
            {
                if (_boneMatrixBuffers[i].Memory.Handle != 0)
                    vk.UnmapMemory(d, _boneMatrixBuffers[i].Memory);
                rm?.DestroyBuffer(_boneMatrixBuffers[i]);
            }
            _boneMatrixBuffers = null!;
            _mappedBoneMatrixBuffers = null!;
        }

        if (_instanceBoneBuffers != null)
        {
            for (int i = 0; i < _instanceBoneBuffers.Length; i++)
            {
                if (_mappedInstanceBoneBuffers != null
                    && i < _mappedInstanceBoneBuffers.Length
                    && _mappedInstanceBoneBuffers[i] != null
                    && _instanceBoneBuffers[i].Memory.Handle != 0)
                {
                    vk.UnmapMemory(d, _instanceBoneBuffers[i].Memory);
                }

                if (_instanceBoneBuffers[i].Memory.Handle != 0)
                    rm?.DestroyBuffer(_instanceBoneBuffers[i]);
            }

            _instanceBoneBuffers = null!;
            _mappedInstanceBoneBuffers = null!;
        }

        // 2-3 Step C (tracks B/C-b completion): release prev bone + prev morph-weights SSBOs
        if (_prevBonePaletteBuffers != null)
        {
            for (int i = 0; i < _prevBonePaletteBuffers.Length; i++)
            {
                if (_mappedPrevBonePaletteBuffers != null
                    && i < _mappedPrevBonePaletteBuffers.Length
                    && _mappedPrevBonePaletteBuffers[i] != null
                    && _prevBonePaletteBuffers[i].Memory.Handle != 0)
                {
                    vk.UnmapMemory(d, _prevBonePaletteBuffers[i].Memory);
                }

                if (_prevBonePaletteBuffers[i].Memory.Handle != 0)
                    rm?.DestroyBuffer(_prevBonePaletteBuffers[i]);
            }
            _prevBonePaletteBuffers = null!;
            _mappedPrevBonePaletteBuffers = null!;
        }

        if (_prevMorphWeightsBuffers != null)
        {
            for (int i = 0; i < _prevMorphWeightsBuffers.Length; i++)
            {
                if (_mappedPrevMorphWeightsBuffers != null
                    && i < _mappedPrevMorphWeightsBuffers.Length
                    && _mappedPrevMorphWeightsBuffers[i] != null
                    && _prevMorphWeightsBuffers[i].Memory.Handle != 0)
                {
                    vk.UnmapMemory(d, _prevMorphWeightsBuffers[i].Memory);
                }

                if (_prevMorphWeightsBuffers[i].Memory.Handle != 0)
                    rm?.DestroyBuffer(_prevMorphWeightsBuffers[i]);
            }
            _prevMorphWeightsBuffers = null!;
            _mappedPrevMorphWeightsBuffers = null!;
        }
    }
}
