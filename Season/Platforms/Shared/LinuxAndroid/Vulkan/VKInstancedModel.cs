// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Season.Models;
using Silk.NET.Vulkan;
using System.Runtime.CompilerServices;

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

/// <summary>
/// GPU instancing rendering backend for GLB models (Vulkan).
/// Extracts primitives from the shared VKModel template (sharing VB/IB/textures),
/// creates its own Material/Matrix UBOs, and performs instanced rendering through Pipeline.
/// v2 supports skeletal animation: independent animation state per instance
/// plus a per-instance bone palette.
/// </summary>
internal unsafe class VKInstancedModel : VKInstancedPrimitiveGroup
{
    readonly GltfAsset _asset = new();

        /// <summary>Animation data source after instancing
        /// (shared-layer entry for picking/animation queries, see InstancedModel.Asset).</summary>
        internal GltfAsset Asset => _asset;

    int _bonePaletteStride = 1;
    BufferResource[] _instanceBoneBuffers = null!;
    byte*[] _mappedInstanceBoneBuffers = null!;
    uint _instanceBoneCapacity;

    // 2-3 Step C (track B): prev per-instance bone SSBO
    // (same capacity as _instanceBoneBuffers).
    // Before each frame upload, memcpy the current bone-buffer mapped region into the prev mapped region,
    // so the GPU always holds the bone palette from the previous frame.
    // Before the first frame, the content is all zero (sentinel _m33 == 0),
    // and the shader falls back to the current bone per joint.
    BufferResource[] _prevInstanceBoneBuffers = null!;
    byte*[] _mappedPrevInstanceBoneBuffers = null!;

    // 2-3 Step C (track C-a): prev per-instance world SSBO
    // (same capacity as _instanceWorlds).
    BufferResource[] _prevInstanceWorldBuffers = null!;
    byte*[] _mappedPrevInstanceWorldBuffers = null!;
    int _prevInstanceWorldCapacity;

    // 2-3 Step C (track C-b): prev per-instance morph weights SSBO
    // (one float4 per entry).
    BufferResource[] _prevMorphWeightsBuffers = null!;
    byte*[] _mappedPrevMorphWeightsBuffers = null!;
    int _prevMorphWeightsCapacity;
    // CPU shadow for the current per-primitive morph weights
    // (indexed by InstanceStreamIndex).
    Vector4[] _currentMorphShadow = Array.Empty<Vector4>();

    (Vector3 Translation, Quaternion Rotation, Vector3 Scale, float[] Weights)[] _restPoseSnapshot = Array.Empty<(Vector3, Quaternion, Vector3, float[])>();
    List<GltfNodeBase> _workNodes = new();
    readonly List<GLTFSkin> _skins = new();
    InstanceAnimationState[] _animationStates = Array.Empty<InstanceAnimationState>();
    PrimitiveInstanceStream[] _primitiveInstanceStreams = Array.Empty<PrimitiveInstanceStream>();

    struct InstanceAnimationState
    {
        public bool Initialized;
        public int AnimationClip;
        public float PlaybackTime;
    }

    sealed class PrimitiveInstanceStream
    {
        public BufferResource Buffer;
        public InstanceTransformData[] Data = Array.Empty<InstanceTransformData>();
        public Matrix4x4[] Worlds = Array.Empty<Matrix4x4>();
        public int Capacity;
    }

    public VKInstancedModel(string name) : base(name)
    {
    }

    /// <summary>
    /// Load primitives from the shared model template.
    /// The caller must ensure the template has already been loaded.
    /// </summary>
    public void Load(VKModel template, Season.Controls.Model model, Season.Basic.Camera camera)
    {
        var nodeMap = new Dictionary<GltfNodeBase, GltfNodeBase>();
        CloneAnimationData(template.Asset, nodeMap);
        _asset._animationPlayer.Initialize(_asset._animations);
        SaveRestPoseSnapshot();

        _skins.Clear();
        _skins.AddRange(_asset.GetAllSkins());
        _bonePaletteStride = Math.Max(1, _skins.Sum(static s => s.Joints.Count));
        CreateInstanceBoneBuffers();

        var templatePrimitives = new List<PrimitiveData>();
        template.CollectAllPrimitives(templatePrimitives);

        foreach (var source in templatePrimitives)
        {
            var clone = CloneForInstancing(source, model, camera);
            if (source.OwnerNode != null && nodeMap.TryGetValue(source.OwnerNode, out var clonedNode))
                clone.OwnerNode = clonedNode;
            clone.InstanceStreamIndex = _primitives.Count;
            _primitives.Add(clone);
        }

        _primitiveInstanceStreams = new PrimitiveInstanceStream[_primitives.Count];
        for (int i = 0; i < _primitiveInstanceStreams.Length; i++)
            _primitiveInstanceStreams[i] = new PrimitiveInstanceStream();

        SyncInstancedSkinningMaterialParams();
        RebuildPrimitiveBuckets();
        SyncAlpha(model.Alpha);
    }

    /// <summary>
    /// Clone a template primitive for instanced rendering:
    /// - VB / IB / textures -> shared references (no new GPU resources)
    /// - Matrix UBO -> created locally (World = Identity, instance transform comes from the instance buffer)
    /// - Material UBO -> created locally (because Alpha may differ)
    /// - Apply model-level material overrides (MaterialColor / Unlit)
    /// </summary>
    PrimitiveData CloneForInstancing(PrimitiveData source, Season.Controls.Model model, Season.Basic.Camera camera)
    {
        var clone = new PrimitiveData
        {
            Vertices = source.Vertices,
            BaseVertices = source.BaseVertices,
            MorphTargets = source.MorphTargets,
            Indices = source.Indices,
            Use32BitIndices = source.Use32BitIndices,
            DoubleSided = source.DoubleSided,
            LocalBoundsCenter = source.LocalBoundsCenter,
            LocalBoundsExtents = source.LocalBoundsExtents,

            // Shared GPU geometry
            VertexBuffer = source.VertexBuffer,
            IndexBuffer = source.IndexBuffer,

            // Shared textures
            BaseColorTexture = source.BaseColorTexture,
            NormalTexture = source.NormalTexture,
            MetallicRoughnessTexture = source.MetallicRoughnessTexture,
            OcclusionTexture = source.OcclusionTexture,
            EmissiveTexture = source.EmissiveTexture,
            MorphDeltasBuffer = source.MorphDeltasBuffer,

            // Copy material parameters and apply model-level overrides
            MaterialParams = source.MaterialParams,
            OriginalBaseColorAlpha = source.OriginalBaseColorAlpha,
            OriginalAlphaCutoff = source.OriginalAlphaCutoff,
            IsTransparent = source.IsTransparent,
        };

        // Apply Model.MaterialColor and Unlit
        var colorTint = model.MaterialColor ?? Vector4.One;
        clone.MaterialParams.BaseColor *= colorTint;
        clone.MaterialParams.RenderMode = model.Unlit ? 0u : 1u;
        clone.MaterialParams.IsInstanced = 1;
        clone.MaterialParams.IsSkinned = 0;
        clone.MaterialParams.BonePaletteStride = 1;
        clone.OriginalBaseColorAlpha = clone.MaterialParams.BaseColor.W;

        // Create the Matrix UBO locally
        CreateMatrixBuffer(clone);
        // Create the Material UBO locally
        CreateMaterialBuffer(clone);

        // Initialize matrices (World = Identity, instance transforms come from the instance buffer)
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
        {
            Unsafe.Write(clone.MappedMatrixBuffers[i], matrices);
            Unsafe.Write(clone.MappedMaterialBuffers[i], clone.MaterialParams);
        }

        // Allocate the DescriptorSet
        // (bind this primitive's Matrix/Material UBOs plus the shared textures)
        AllocateAndWriteDescriptorSets(clone);

        return clone;
    }

    void CloneAnimationData(GltfAsset templateAsset, Dictionary<GltfNodeBase, GltfNodeBase> nodeMap)
    {
        foreach (var sourceNode in templateAsset.gltfNodes)
        {
            var clone = new GltfNodeBase
            {
                Name = sourceNode.Name,
                LogicalIndex = sourceNode.LogicalIndex,
                Mesh = sourceNode.Mesh,
                Translation = sourceNode.Translation,
                Rotation = sourceNode.Rotation,
                Scale = sourceNode.Scale,
                InitialTranslation = sourceNode.InitialTranslation,
                InitialRotation = sourceNode.InitialRotation,
                InitialScale = sourceNode.InitialScale,
                Weights = sourceNode.Weights.ToArray(),
                InitialWeights = sourceNode.InitialWeights.ToArray(),
                WeightsVersion = sourceNode.WeightsVersion,
                IsJoint = sourceNode.IsJoint,
                JointIndex = sourceNode.JointIndex,
                WorldTransform = sourceNode.WorldTransform,
                // v2 picking: PickMesh is immutable after loading, so share it by reference
                // (no deep copy; NodeIndex remains consistent on both sides)
                PickMeshes = sourceNode.PickMeshes,
            };
            nodeMap[sourceNode] = clone;
            _asset.gltfNodes.Add(clone);
        }

        foreach (var sourceNode in templateAsset.gltfNodes)
        {
            var clone = nodeMap[sourceNode];
            foreach (var child in sourceNode.Children)
            {
                if (nodeMap.TryGetValue(child, out var clonedChild))
                    clone.Children.Add(clonedChild);
            }
        }

        var skinMap = new Dictionary<GLTFSkin, GLTFSkin>();
        foreach (var sourceSkin in templateAsset.GetAllSkins())
        {
            var clonedSkin = new GLTFSkin
            {
                Name = sourceSkin.Name,
                InverseBindMatrices = new List<Matrix4x4>(sourceSkin.InverseBindMatrices),
                Joints = new List<GltfNodeBase>(),
            };
            skinMap[sourceSkin] = clonedSkin;

            foreach (var joint in sourceSkin.Joints)
            {
                if (nodeMap.TryGetValue(joint, out var clonedJoint))
                {
                    clonedSkin.Joints.Add(clonedJoint);
                    clonedJoint.Skin = clonedSkin;
                }
            }

            if (sourceSkin.SkeletonRoot != null && nodeMap.TryGetValue(sourceSkin.SkeletonRoot, out var clonedRoot))
                clonedSkin.SkeletonRoot = clonedRoot;

            if (sourceSkin.BindNode != null && nodeMap.TryGetValue(sourceSkin.BindNode, out var clonedBindNode))
                clonedSkin.BindNode = clonedBindNode;
            else
                clonedSkin.BindNode = clonedSkin.SkeletonRoot ?? (clonedSkin.Joints.Count > 0 ? clonedSkin.Joints[0] : null);
        }

        foreach (var sourceNode in templateAsset.gltfNodes)
        {
            if (sourceNode.Skin != null
                && nodeMap.TryGetValue(sourceNode, out var clonedNode)
                && skinMap.TryGetValue(sourceNode.Skin, out var clonedSkin))
            {
                clonedNode.Skin = clonedSkin;
            }
        }

        _asset._animations = CloneAnimations(templateAsset._animations, nodeMap);
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
                    Interpolation = sourceChannel.Sampler?.Interpolation ?? AnimationInterpolationMode.Linear,
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

    void SaveRestPoseSnapshot()
    {
        _restPoseSnapshot = new (Vector3, Quaternion, Vector3, float[])[_asset.gltfNodes.Count];
        for (int i = 0; i < _asset.gltfNodes.Count; i++)
        {
            var node = _asset.gltfNodes[i];
            _restPoseSnapshot[i] = (
                node.InitialTranslation,
                node.InitialRotation,
                node.InitialScale,
                node.InitialWeights.Length > 0 ? (float[])node.InitialWeights.Clone() : Array.Empty<float>()
            );
        }

        _workNodes = _asset.gltfNodes;
    }

    void RestoreNodesToRestPose()
    {
        for (int i = 0; i < _workNodes.Count; i++)
        {
            var node = _workNodes[i];
            var snap = _restPoseSnapshot[i];
            node.Translation = snap.Translation;
            node.Rotation = snap.Rotation;
            node.Scale = snap.Scale;
            node.WeightsVersion = 0;

            if (snap.Weights.Length == 0)
            {
                node.Weights = Array.Empty<float>();
                continue;
            }

            if (node.Weights.Length != snap.Weights.Length)
                node.Weights = (float[])snap.Weights.Clone();
            else
                Array.Copy(snap.Weights, node.Weights, snap.Weights.Length);
        }
    }

    void EnsureAnimationStateCapacity(int count)
    {
        if (_animationStates.Length >= count)
            return;

        Array.Resize(ref _animationStates, count);
    }

    void EnsurePrimitiveInstanceCapacity(int count)
    {
        if (_primitiveInstanceStreams.Length == 0)
            return;

        for (int i = 0; i < _primitiveInstanceStreams.Length; i++)
        {
            var stream = _primitiveInstanceStreams[i];
            if (stream.Capacity >= count && stream.Buffer.Buffer.Handle != 0)
                continue;

            if (stream.Buffer.Buffer.Handle != 0)
                Device.ResourceManager.DestroyBuffer(stream.Buffer);

            stream.Buffer = Device.ResourceManager.CreateVertexBuffer<InstanceTransformData>((uint)count);
            stream.Capacity = count;
            stream.Data = new InstanceTransformData[count];
            stream.Worlds = new Matrix4x4[count];
        }
    }

    void ReleasePrimitiveInstanceBuffers()
    {
        foreach (var stream in _primitiveInstanceStreams)
        {
            if (stream == null)
                continue;

            if (stream.Buffer.Buffer.Handle != 0)
            {
                Device.ResourceManager.DestroyBuffer(stream.Buffer);
                stream.Buffer = default;
            }

            stream.Data = Array.Empty<InstanceTransformData>();
            stream.Worlds = Array.Empty<Matrix4x4>();
            stream.Capacity = 0;
        }
    }

    static Vector4 ExtractMorphWeights(GltfNodeBase? node)
    {
        if (node == null || node.Weights.Length == 0)
            return Vector4.Zero;

        var weights = node.Weights;
        return new Vector4(
            weights.Length > 0 ? weights[0] : 0f,
            weights.Length > 1 ? weights[1] : 0f,
            weights.Length > 2 ? weights[2] : 0f,
            weights.Length > 3 ? weights[3] : 0f);
    }

    PrimitiveInstanceStream? GetPrimitiveInstanceStream(PrimitiveData primitive)
    {
        int index = primitive.InstanceStreamIndex;
        if (index < 0 || index >= _primitiveInstanceStreams.Length)
            return null;

        return _primitiveInstanceStreams[index];
    }

    void SyncInstancedSkinningMaterialParams()
    {
        for (int i = 0; i < _primitives.Count; i++)
        {
            var primitive = _primitives[i];
            primitive.MaterialParams.IsSkinned = primitive.OwnerNode?.Skin != null ? 1u : 0u;
            primitive.MaterialParams.BonePaletteStride = (uint)Math.Max(1, _bonePaletteStride);
            for (int fi = 0; fi < Device.frameCount; fi++)
                Unsafe.Write(primitive.MappedMaterialBuffers[fi], primitive.MaterialParams);
        }
    }

    protected override BufferResource[] InstanceBoneBuffers
        => _instanceBoneBuffers != null && _instanceBoneBuffers.Length > 0
            ? _instanceBoneBuffers
            : Pipeline.IdentityInstanceBoneBuffers;

    void CreateInstanceBoneBuffers()
    {
        int n = (int)Device.frameCount;
        _instanceBoneBuffers = new BufferResource[n];
        _mappedInstanceBoneBuffers = new byte*[n];
        _instanceBoneCapacity = 1;

        CreateOrResizeInstanceBoneBuffers(_instanceBoneCapacity);
    }

    void EnsureInstanceBoneCapacity(uint capacity)
    {
        if (capacity <= _instanceBoneCapacity)
            return;

        CreateOrResizeInstanceBoneBuffers(capacity);
        for (int i = 0; i < _primitives.Count; i++)
            RewriteDescriptorSets(_primitives[i]);
        // Keep shell primitive descriptor sets in sync (plan risk 3):
        // after bone-buffer resize, shell descriptor sets may still point to old buffers
        // and read freed memory
        RewriteShellDescriptorSets();
    }

    void CreateOrResizeInstanceBoneBuffers(uint capacity)
    {
        var identity = Matrix4x4.Identity;
        int elementCount = Math.Max(1, _bonePaletteStride * (int)capacity);
        ulong size = (ulong)(elementCount * Unsafe.SizeOf<Matrix4x4>());

        for (int i = 0; i < Device.frameCount; i++)
        {
            if (_instanceBoneBuffers[i].Buffer.Handle != 0)
            {
                if (_mappedInstanceBoneBuffers != null && _mappedInstanceBoneBuffers[i] != null)
                    Device.Vk.UnmapMemory(Device.LogicalDevice, _instanceBoneBuffers[i].Memory);
                Device.ResourceManager.DestroyBuffer(_instanceBoneBuffers[i]);
            }

            _instanceBoneBuffers[i] = Device.ResourceManager.CreateBuffer(
                size,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

            void* mapped;
            if (Device.Vk.MapMemory(Device.LogicalDevice, _instanceBoneBuffers[i].Memory, 0, size, 0, &mapped) != Result.Success)
                throw new Exception("vkMapMemory (InstancedModel bone storage) failed");

            _mappedInstanceBoneBuffers[i] = (byte*)mapped;
            for (int j = 0; j < elementCount; j++)
                Unsafe.Write(_mappedInstanceBoneBuffers[i] + j * Unsafe.SizeOf<Matrix4x4>(), identity);
        }

        _instanceBoneCapacity = capacity;
    }

    void UploadInstanceBoneMatrices(int instanceIndex, Matrix4x4[] boneMatrices)
    {
        if (boneMatrices.Length == 0 || _mappedInstanceBoneBuffers == null)
            return;

        int fi = (int)Device.FrameIndex;
        int offset = instanceIndex * _bonePaletteStride * Unsafe.SizeOf<Matrix4x4>();
        int totalSize = Unsafe.SizeOf<Matrix4x4>() * Math.Min(boneMatrices.Length, _bonePaletteStride);
        fixed (void* matricesPtr = boneMatrices)
        {
            Unsafe.CopyBlock(_mappedInstanceBoneBuffers[fi] + offset, matricesPtr, (uint)totalSize);
        }
    }

    // 2-3 Step C (track B): create or grow the prev per-instance bone SSBO
    // (same capacity as current; zero-initialized on first creation)
    void EnsurePrevInstanceBoneCapacity(uint capacity)
    {
        if (capacity <= (_prevInstanceBoneBuffers != null ? (uint)_prevInstanceBoneBuffers.Length : 0))
            return;

        // Release the old buffers
        if (_prevInstanceBoneBuffers != null)
        {
            for (int i = 0; i < _prevInstanceBoneBuffers.Length; i++)
            {
                if (_mappedPrevInstanceBoneBuffers != null && i < _mappedPrevInstanceBoneBuffers.Length
                    && _mappedPrevInstanceBoneBuffers[i] != null && _prevInstanceBoneBuffers[i].Memory.Handle != 0)
                    Device.Vk.UnmapMemory(Device.LogicalDevice, _prevInstanceBoneBuffers[i].Memory);
                if (_prevInstanceBoneBuffers[i].Memory.Handle != 0)
                    Device.ResourceManager.DestroyBuffer(_prevInstanceBoneBuffers[i]);
            }
        }

        int n = (int)Device.frameCount;
        int elementCount = Math.Max(1, _bonePaletteStride * (int)capacity);
        ulong size = (ulong)(elementCount * Unsafe.SizeOf<Matrix4x4>());
        _prevInstanceBoneBuffers = new BufferResource[n];
        _mappedPrevInstanceBoneBuffers = new byte*[n];

        for (int i = 0; i < n; i++)
        {
            _prevInstanceBoneBuffers[i] = Device.ResourceManager.CreateBuffer(
                size,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
            void* mapped;
            if (Device.Vk.MapMemory(Device.LogicalDevice, _prevInstanceBoneBuffers[i].Memory, 0, size, 0, &mapped) != Result.Success)
                throw new Exception("vkMapMemory (PrevInstanceBoneBuffers) failed");
            _mappedPrevInstanceBoneBuffers[i] = (byte*)mapped;
            new Span<byte>(mapped, (int)size).Clear();
        }
    }

    // 2-3 Step C (track C-a): create or grow the prev per-instance world SSBO
    void EnsurePrevInstanceWorldCapacity(int count)
    {
        if (count <= _prevInstanceWorldCapacity)
            return;

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

        int n = (int)Device.frameCount;
        ulong size = (ulong)(count * Unsafe.SizeOf<Matrix4x4>());
        _prevInstanceWorldBuffers = new BufferResource[n];
        _mappedPrevInstanceWorldBuffers = new byte*[n];

        for (int i = 0; i < n; i++)
        {
            _prevInstanceWorldBuffers[i] = Device.ResourceManager.CreateBuffer(
                size,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
            void* mapped;
            if (Device.Vk.MapMemory(Device.LogicalDevice, _prevInstanceWorldBuffers[i].Memory, 0, size, 0, &mapped) != Result.Success)
                throw new Exception("vkMapMemory (PrevInstanceWorldBuffers) failed");
            _mappedPrevInstanceWorldBuffers[i] = (byte*)mapped;
            new Span<byte>(mapped, (int)size).Clear();
        }
        _prevInstanceWorldCapacity = count;
    }

    // 2-3 Step C (track C-b): create or grow the prev per-instance morph weights SSBO
    void EnsurePrevMorphWeightsCapacity(int count)
    {
        if (count <= _prevMorphWeightsCapacity)
            return;

        if (_prevMorphWeightsBuffers != null)
        {
            for (int i = 0; i < _prevMorphWeightsBuffers.Length; i++)
            {
                if (_mappedPrevMorphWeightsBuffers != null && i < _mappedPrevMorphWeightsBuffers.Length
                    && _mappedPrevMorphWeightsBuffers[i] != null && _prevMorphWeightsBuffers[i].Memory.Handle != 0)
                    Device.Vk.UnmapMemory(Device.LogicalDevice, _prevMorphWeightsBuffers[i].Memory);
                if (_prevMorphWeightsBuffers[i].Memory.Handle != 0)
                    Device.ResourceManager.DestroyBuffer(_prevMorphWeightsBuffers[i]);
            }
        }

        int n = (int)Device.frameCount;
        ulong size = (ulong)(count * Unsafe.SizeOf<Vector4>());
        _prevMorphWeightsBuffers = new BufferResource[n];
        _mappedPrevMorphWeightsBuffers = new byte*[n];

        for (int i = 0; i < n; i++)
        {
            _prevMorphWeightsBuffers[i] = Device.ResourceManager.CreateBuffer(
                size,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
            void* mapped;
            if (Device.Vk.MapMemory(Device.LogicalDevice, _prevMorphWeightsBuffers[i].Memory, 0, size, 0, &mapped) != Result.Success)
                throw new Exception("vkMapMemory (PrevMorphWeightsBuffers) failed");
            _mappedPrevMorphWeightsBuffers[i] = (byte*)mapped;
            new Span<byte>(mapped, (int)size).Clear();
        }
        _prevMorphWeightsCapacity = count;

        // Keep the CPU shadow at the same capacity as the SB
        if (_currentMorphShadow.Length < count)
            _currentMorphShadow = new Vector4[count];
    }

    // 2-3 Step C: override the base virtual methods to return the actual prev SSBOs
    protected override DescriptorBufferInfo GetPrevBoneBufferInfo(int fi)
        => _prevInstanceBoneBuffers != null && fi < _prevInstanceBoneBuffers.Length
            ? new() { Buffer = _prevInstanceBoneBuffers[fi].Buffer, Offset = 0, Range = Vk.WholeSize }
            : base.GetPrevBoneBufferInfo(fi);

    protected override DescriptorBufferInfo GetPrevInstanceWorldBufferInfo(int fi)
        => _prevInstanceWorldBuffers != null && fi < _prevInstanceWorldBuffers.Length
            ? new() { Buffer = _prevInstanceWorldBuffers[fi].Buffer, Offset = 0, Range = Vk.WholeSize }
            : base.GetPrevInstanceWorldBufferInfo(fi);

    protected override DescriptorBufferInfo GetPrevMorphWeightsBufferInfo(int fi)
        => _prevMorphWeightsBuffers != null && fi < _prevMorphWeightsBuffers.Length
            ? new() { Buffer = _prevMorphWeightsBuffers[fi].Buffer, Offset = 0, Range = Vk.WholeSize }
            : base.GetPrevMorphWeightsBufferInfo(fi);

    // 2-3 Step C (tracks B/C-b): after the prev bone + prev morph SBs contain valid data,
    // set MaterialParams.HasPrevBones / HasPrevInstanceWorld / HasPrevMorph = 1
    // for all primitives.
    void SetPrevBonesAndMorphReady()
    {
        for (int i = 0; i < _primitives.Count; i++)
        {
            var primitive = _primitives[i];
            bool changed = false;
            if (primitive.MaterialParams.HasPrevBones == 0 && _prevInstanceBoneBuffers != null)
            {
                primitive.MaterialParams.HasPrevBones = 1;
                changed = true;
            }
            if (primitive.MaterialParams.HasPrevInstanceWorld == 0 && _prevInstanceWorldBuffers != null)
            {
                primitive.MaterialParams.HasPrevInstanceWorld = 1;
                changed = true;
            }
            if (primitive.MaterialParams.HasPrevMorph == 0 && _prevMorphWeightsBuffers != null)
            {
                primitive.MaterialParams.HasPrevMorph = 1;
                changed = true;
            }
            if (changed)
            {
                for (int f = 0; f < Device.frameCount; f++)
                    Unsafe.Write(primitive.MappedMaterialBuffers[f], primitive.MaterialParams);
            }
        }
        // Synchronize prev flags for shell primitives:
        // cover both template sets and both instance-box pools,
        // since pooled boxes may have been created earlier and have stale flags
        SyncShellPrevFlags(_prevInstanceWorldBuffers != null, _prevInstanceBoneBuffers != null, _prevMorphWeightsBuffers != null);
    }

    public IReadOnlyList<string> GetAnimationNames()
    {
        return _asset.GetAnimationNames();
    }

    public void Update(InstancedModel model, float time)
    {
        bool wasInitialized = _transformInitialized;
        _instanceCount = 0;

        // Unified highlighting: clear the per-instance Bounds/Wireframe draw lists for this frame
        // (rebuilt every frame; _boundsActive/_wireframeActive are set by the per-instance hooks below)
        _boundsActive = false;
        _boundsBoxDrawList.Clear();
        _wireframeActive = false;
        _shellBoxDrawList.Clear();
        _outline2DInstances.Clear();
        _outline2DInstanceColors.Clear();

        for (int i = 0; i < model.Instances.Count; i++)
        {
            if (model.Instances[i].Enable)
                _instanceCount++;
        }

        if (_instanceCount == 0)
        {
            _transformInitialized = true;
            SyncAlpha(model.Alpha);
            SetOutline2DState(false, default, default);
            return;
        }

        EnsureAnimationStateCapacity(model.Instances.Count);
        EnsurePrimitiveInstanceCapacity(_instanceCount);
        EnsureInstanceBoneCapacity((uint)Math.Max(_instanceCount, 1));
        // 2-3 Step C: create or grow the prev SSBOs
        EnsurePrevInstanceBoneCapacity((uint)Math.Max(_instanceCount, 1));
        EnsurePrevInstanceWorldCapacity(_instanceCount);
        EnsurePrevMorphWeightsCapacity(_instanceCount);

        bool hasAnimation = _asset._animations.Count > 0;
        bool hasSkin = _skins.Count > 0;
        float deltaTime = Math.Max(time, 0f);

        // 2-3 Step C (tracks B/C-b): before uploading this frame,
        // first copy the current GPU buffer / CPU shadow to the prev side.
        // On the first frame, the current side is all zero (or the shadow is zero),
        // so the prev side remains zeroed and the sentinel semantics stay correct.
        int fi = (int)Device.FrameIndex;
        if (_mappedPrevInstanceBoneBuffers != null && _mappedInstanceBoneBuffers != null)
        {
            ulong copySize = (ulong)(_bonePaletteStride * _instanceCount * Unsafe.SizeOf<Matrix4x4>());
            if (copySize > 0 && fi < _mappedPrevInstanceBoneBuffers.Length && fi < _mappedInstanceBoneBuffers.Length)
                Unsafe.CopyBlock(_mappedPrevInstanceBoneBuffers[fi], _mappedInstanceBoneBuffers[fi], (uint)copySize);
        }
        if (_mappedPrevInstanceWorldBuffers != null)
        {
            if (fi < _mappedPrevInstanceWorldBuffers.Length && _mappedPrevInstanceWorldBuffers[fi] != null)
            {
                // Take the first valid stream.Worlds
                // (all primitives share the same Worlds source content)
                foreach (var stream in _primitiveInstanceStreams)
                {
                    if (stream?.Worlds != null && stream.Worlds.Length >= _instanceCount)
                    {
                        fixed (Matrix4x4* pSrc = stream.Worlds)
                            Unsafe.CopyBlock(_mappedPrevInstanceWorldBuffers[fi], pSrc,
                                (uint)(_instanceCount * Unsafe.SizeOf<Matrix4x4>()));
                        break;
                    }
                }
            }
        }
        if (_mappedPrevMorphWeightsBuffers != null && _currentMorphShadow.Length > 0)
        {
            if (fi < _mappedPrevMorphWeightsBuffers.Length && _mappedPrevMorphWeightsBuffers[fi] != null)
            {
                fixed (Vector4* pSrc = _currentMorphShadow)
                    Unsafe.CopyBlock(_mappedPrevMorphWeightsBuffers[fi], pSrc,
                        (uint)(_currentMorphShadow.Length * Unsafe.SizeOf<Vector4>()));
            }
        }

        int writeIndex = 0;
        for (int i = 0; i < model.Instances.Count; i++)
        {
            var instance = model.Instances[i];
            if (!instance.Enable)
                continue;

            // Unified transform convention: converge on BuildInstanceMatrix
            // (anchor pivot, see InstancedMesh3DBase)
            var instanceWorld = model.BuildInstanceMatrix(instance);
            RestoreNodesToRestPose();

            if (hasAnimation)
            {
                int clip = instance.AnimationClip;
                if (clip < 0 || clip >= _asset._animations.Count)
                    clip = 0;

                ref var state = ref _animationStates[i];
                float nextPlaybackTime;
                if (!state.Initialized || state.AnimationClip != clip)
                {
                    nextPlaybackTime = instance.AnimationTimeOffset + deltaTime * instance.AnimationSpeed;
                    state.Initialized = true;
                    state.AnimationClip = clip;
                }
                else
                {
                    nextPlaybackTime = state.PlaybackTime + deltaTime * instance.AnimationSpeed;
                }

                state.PlaybackTime = nextPlaybackTime;
                _asset._animationPlayer.Evaluate(clip, nextPlaybackTime, _workNodes);
            }
            else
            {
                _asset._animationPlayer.UpdateAllNodeTransforms(_workNodes);
            }

            if (hasSkin)
            {
                _asset._animationPlayer.UpdateBoneMatrices(_skins);
                UploadInstanceBoneMatrices(writeIndex, _asset._animationPlayer.GetBoneMatricesArray());
            }

            foreach (var primitive in _primitives)
            {
                var stream = GetPrimitiveInstanceStream(primitive);
                if (stream == null)
                    continue;

                var finalWorld = (primitive.OwnerNode?.WorldTransform ?? Matrix4x4.Identity) * instanceWorld;
                stream.Worlds[writeIndex] = finalWorld;
                var instanceData = InstanceTransformData.FromWorld(finalWorld);
                if (primitive.MaterialParams.HasMorphTargets != 0)
                {
                    instanceData.MorphWeights = ExtractMorphWeights(primitive.OwnerNode);
                    // 2-3 Step C (track C-b): update the CPU shadow immediately after extraction,
                    // so it can be uploaded as prev data in the next frame
                    int pidx = primitive.InstanceStreamIndex;
                    if ((uint)pidx < (uint)_currentMorphShadow.Length)
                        _currentMorphShadow[pidx] = instanceData.MorphWeights;
                }
                stream.Data[writeIndex] = instanceData;
            }

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
                var worldBounds = model.GetInstanceWorldBoundsRaw(instance);
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
                EnsureShellGeometry(model.Highlight.EdgeWidth,
                    MathF.Max(model.TemplateLocalSize.X, MathF.Max(model.TemplateLocalSize.Y, model.TemplateLocalSize.Z)));
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
        _outline2DHostActive = model.Highlight.Outline;
        bool anyInstanceOutline = _outline2DInstances.Count > 0;
        SetOutline2DState(_outline2DHostActive || anyInstanceOutline,
            anyInstanceOutline ? _outline2DInstanceColor : model.Highlight.OutlineColor,
            anyInstanceOutline ? _outline2DInstanceWidth : model.Highlight.OutlineWidth);

        foreach (var stream in _primitiveInstanceStreams)
        {
            if (stream != null && stream.Buffer.Buffer.Handle != 0)
                Device.ResourceManager.UpdateBuffer(stream.Buffer, stream.Data);
        }

        fi = (int)Device.FrameIndex;
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

        // 2-3 Step C: from the second frame onward, the prev SBs contain valid data,
        // so notify the shader path that it can start reading them.
        // Also rewrite the DescriptorSet to switch bindings 13/14/15
        // from the default zero buffers to the actual prev SSBOs.
        if (wasInitialized)
        {
            SetPrevBonesAndMorphReady();
            foreach (var primitive in _primitives)
                RewriteDescriptorSets(primitive);
            // Keep shell primitive descriptor sets in sync
            // (switch bindings 13/14/15 from zero placeholders to the actual prev SSBOs)
            RewriteShellDescriptorSets();
        }

        SyncAlpha(model.Alpha);
    }

    public new void Draw()
    {
        if (!_transformInitialized || _instanceCount == 0)
            return;

        var cmd = Device.GraphicsCommandBuffer;
        bool forceFadeByAlpha = _currentAlpha < 1.0f;
        int fi = (int)Device.FrameIndex;

        for (int i = 0; i < _opaquePrimitives.Count; i++)
        {
            var primitive = _opaquePrimitives[i];
            var stream = GetPrimitiveInstanceStream(primitive);
            if (stream == null || stream.Buffer.Buffer.Handle == 0)
                continue;

            Pipeline.SetPipeline(cmd, forceFadeByAlpha ? PipelineMode.Fade : PipelineMode.Opaque, primitive.DoubleSided);
            Pipeline.DrawPrimitive(cmd, primitive, primitive.VertexBuffer.Buffer, primitive.IndexBuffer.Buffer,
                primitive.DescriptorSets[fi], (uint)primitive.Indices.Length,
                stream.Buffer.Buffer, (uint)_instanceCount, 0);
        }

        for (int i = 0; i < _transparentPrimitives.Count; i++)
        {
            var primitive = _transparentPrimitives[i];
            var stream = GetPrimitiveInstanceStream(primitive);
            if (stream == null || stream.Buffer.Buffer.Handle == 0)
                continue;

            _transparentInstanceOrder.Clear();
            for (int instanceIndex = 0; instanceIndex < _instanceCount; instanceIndex++)
                _transparentInstanceOrder.Add(instanceIndex);

            _transparentInstanceOrder.Sort((a, b) =>
            {
                float depthA = ComputeTransparentDepth(stream.Worlds[a], primitive.LocalBoundsCenter);
                float depthB = ComputeTransparentDepth(stream.Worlds[b], primitive.LocalBoundsCenter);
                return depthB.CompareTo(depthA);
            });

            for (int orderIndex = 0; orderIndex < _transparentInstanceOrder.Count; orderIndex++)
            {
                uint instanceIndex = (uint)_transparentInstanceOrder[orderIndex];
                if (primitive.DoubleSided)
                {
                    Pipeline.SetPipeline(cmd, PipelineMode.Transparent, CullModeFlags.FrontBit);
                    Pipeline.DrawPrimitive(cmd, primitive, primitive.VertexBuffer.Buffer, primitive.IndexBuffer.Buffer,
                        primitive.DescriptorSets[fi], (uint)primitive.Indices.Length,
                        stream.Buffer.Buffer, 1, instanceIndex);
                }

                Pipeline.SetPipeline(cmd, PipelineMode.Transparent, CullModeFlags.BackBit);
                Pipeline.DrawPrimitive(cmd, primitive, primitive.VertexBuffer.Buffer, primitive.IndexBuffer.Buffer,
                    primitive.DescriptorSets[fi], (uint)primitive.Indices.Length,
                    stream.Buffer.Buffer, 1, instanceIndex);
            }
        }

        // Unified highlighting: per-instance Bounds boxes + Wireframe shell boxes
        // (for instances enabled in this frame; transparent faces use 2-pass rendering
        // plus opaque edges, finalized after all surfaces)
        if (_boundsActive)
            DrawBoundsBoxes();
        if (_wireframeActive)
        {
            // Shell-box matrices are addressed through the instance-stream slot.
            // Take the first per-primitive stream with a valid buffer
            // (isomorphic to DX: all streams share the same source content, so any stream works;
            // multi-node assets are approximate here, matching documented DX behavior)
            foreach (var stream in _primitiveInstanceStreams)
            {
                if (stream != null && stream.Buffer.Buffer.Handle != 0)
                {
                    DrawShellBoxes(stream.Buffer);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// 1-5 Shadow pass: depth-only instanced drawing.
    /// VKInstancedModel uses per-primitive instance streams
    /// (it does not maintain the base-class _instanceBuffer),
    /// so this override goes through GetPrimitiveInstanceStream and shares the same source as Draw.
    /// Only opaque primitives are drawn; transparent primitives do not cast shadows.
    /// </summary>
    public override void DrawShadow()
    {
        if (!_transformInitialized || _instanceCount == 0)
            return;

        var cmd = Device.GraphicsCommandBuffer;
        int fi = (int)Device.FrameIndex;

        for (int i = 0; i < _opaquePrimitives.Count; i++)
        {
            var primitive = _opaquePrimitives[i];
            var stream = GetPrimitiveInstanceStream(primitive);
            if (stream == null || stream.Buffer.Buffer.Handle == 0)
                continue;

            Pipeline.DrawShadowPrimitive(cmd, primitive, primitive.VertexBuffer.Buffer, primitive.IndexBuffer.Buffer,
                primitive.DescriptorSets[fi], (uint)primitive.Indices.Length,
                stream.Buffer.Buffer, (uint)_instanceCount, 0);
        }
    }

    /// <summary>
    /// Phase 4: Outline2D mask rendering.
    /// Same as DrawShadow, VKInstancedModel uses per-primitive instance streams
    /// (it does not maintain the base-class _instanceBuffer),
    /// so this override goes through GetPrimitiveInstanceStream and shares the same source as Draw.
    /// Host activation uses the full instance batch; otherwise it draws enabled instances one by one
    /// (instanceCount = 1 + first-instance index).
    /// </summary>
    public override void DrawOutlineMask()
    {
        if (!_transformInitialized || _instanceCount == 0 || !Outline2DActive)
            return;

        var cmd = Device.GraphicsCommandBuffer;
        Pipeline.SetOutlineMaskColor(cmd, _outline2DColor);
        int fi = (int)Device.FrameIndex;

        for (int i = 0; i < _opaquePrimitives.Count; i++)
        {
            var primitive = _opaquePrimitives[i];
            var stream = GetPrimitiveInstanceStream(primitive);
            if (stream == null || stream.Buffer.Buffer.Handle == 0)
                continue;

            Pipeline.SetPipeline(cmd, PipelineMode.Opaque,
                primitive.DoubleSided ? CullModeFlags.None : CullModeFlags.BackBit, depthWrite: false);
            OnBeforeDraw();
            if (_outline2DHostActive)
            {
                Pipeline.DrawPrimitive(cmd, primitive, primitive.VertexBuffer.Buffer, primitive.IndexBuffer.Buffer,
                    primitive.DescriptorSets[fi], (uint)primitive.Indices.Length,
                    stream.Buffer.Buffer, (uint)_instanceCount, 0);
            }
            else
            {
                for (int k = 0; k < _outline2DInstances.Count; k++)
                {
                    int idx = _outline2DInstances[k];
                    if ((uint)idx >= (uint)_instanceCount)
                        continue;
                    // Write this instance's own OutlineColor slot by slot
                    // (per-slot mask fetches color per slot)
                    Pipeline.SetOutlineMaskColor(cmd, _outline2DInstanceColors[k]);
                    Pipeline.DrawPrimitive(cmd, primitive, primitive.VertexBuffer.Buffer, primitive.IndexBuffer.Buffer,
                        primitive.DescriptorSets[fi], (uint)primitive.Indices.Length,
                        stream.Buffer.Buffer, 1, (uint)idx);
                }
            }
        }
    }

    /// <summary>
    /// Release only owned resources:
    /// MatrixBuffers / MaterialBuffers / DescriptorSets / InstanceBuffers / BoneStorageBuffers.
    /// VB / IB / textures belong to the template model and are not released here.
    /// </summary>
    public override void Dispose()
    {
        ReleasePrimitiveInstanceBuffers();

        foreach (var primitive in _primitives)
        {
            if (primitive.MatrixBuffers != null)
            {
                for (int i = 0; i < primitive.MatrixBuffers.Length; i++)
                {
                    if (primitive.MappedMatrixBuffers != null && i < primitive.MappedMatrixBuffers.Length
                        && primitive.MappedMatrixBuffers[i] != null
                        && primitive.MatrixBuffers[i].Memory.Handle != 0)
                    {
                        Device.Vk.UnmapMemory(Device.LogicalDevice, primitive.MatrixBuffers[i].Memory);
                    }

                    Device.ResourceManager?.DestroyBuffer(primitive.MatrixBuffers[i]);
                }

                primitive.MatrixBuffers = null!;
                primitive.MappedMatrixBuffers = null!;
            }

            if (primitive.MaterialBuffers != null)
            {
                for (int i = 0; i < primitive.MaterialBuffers.Length; i++)
                {
                    if (primitive.MappedMaterialBuffers != null && i < primitive.MappedMaterialBuffers.Length
                        && primitive.MappedMaterialBuffers[i] != null
                        && primitive.MaterialBuffers[i].Memory.Handle != 0)
                    {
                        Device.Vk.UnmapMemory(Device.LogicalDevice, primitive.MaterialBuffers[i].Memory);
                    }

                    Device.ResourceManager?.DestroyBuffer(primitive.MaterialBuffers[i]);
                }

                primitive.MaterialBuffers = null!;
                primitive.MappedMaterialBuffers = null!;
            }

            if (primitive.DescriptorSets != null)
            {
                for (int i = 0; i < primitive.DescriptorSets.Length; i++)
                    Device.DescriptorAllocator?.FreeSet(primitive.DescriptorSets[i]);
                primitive.DescriptorSets = null!;
            }
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
                    Device.Vk.UnmapMemory(Device.LogicalDevice, _instanceBoneBuffers[i].Memory);
                }

                if (_instanceBoneBuffers[i].Memory.Handle != 0)
                    Device.ResourceManager?.DestroyBuffer(_instanceBoneBuffers[i]);
            }
        }

        _instanceBoneBuffers = null!;
        _mappedInstanceBoneBuffers = null!;

        // 2-3 Step C: release the prev SSBOs
        if (_prevInstanceBoneBuffers != null)
        {
            for (int i = 0; i < _prevInstanceBoneBuffers.Length; i++)
            {
                if (_mappedPrevInstanceBoneBuffers != null && i < _mappedPrevInstanceBoneBuffers.Length
                    && _mappedPrevInstanceBoneBuffers[i] != null && _prevInstanceBoneBuffers[i].Memory.Handle != 0)
                    Device.Vk.UnmapMemory(Device.LogicalDevice, _prevInstanceBoneBuffers[i].Memory);
                if (_prevInstanceBoneBuffers[i].Memory.Handle != 0)
                    Device.ResourceManager?.DestroyBuffer(_prevInstanceBoneBuffers[i]);
            }
            _prevInstanceBoneBuffers = null!;
            _mappedPrevInstanceBoneBuffers = null!;
        }
        if (_prevInstanceWorldBuffers != null)
        {
            for (int i = 0; i < _prevInstanceWorldBuffers.Length; i++)
            {
                if (_mappedPrevInstanceWorldBuffers != null && i < _mappedPrevInstanceWorldBuffers.Length
                    && _mappedPrevInstanceWorldBuffers[i] != null && _prevInstanceWorldBuffers[i].Memory.Handle != 0)
                    Device.Vk.UnmapMemory(Device.LogicalDevice, _prevInstanceWorldBuffers[i].Memory);
                if (_prevInstanceWorldBuffers[i].Memory.Handle != 0)
                    Device.ResourceManager?.DestroyBuffer(_prevInstanceWorldBuffers[i]);
            }
            _prevInstanceWorldBuffers = null!;
            _mappedPrevInstanceWorldBuffers = null!;
        }
        if (_prevMorphWeightsBuffers != null)
        {
            for (int i = 0; i < _prevMorphWeightsBuffers.Length; i++)
            {
                if (_mappedPrevMorphWeightsBuffers != null && i < _mappedPrevMorphWeightsBuffers.Length
                    && _mappedPrevMorphWeightsBuffers[i] != null && _prevMorphWeightsBuffers[i].Memory.Handle != 0)
                    Device.Vk.UnmapMemory(Device.LogicalDevice, _prevMorphWeightsBuffers[i].Memory);
                if (_prevMorphWeightsBuffers[i].Memory.Handle != 0)
                    Device.ResourceManager?.DestroyBuffer(_prevMorphWeightsBuffers[i]);
            }
            _prevMorphWeightsBuffers = null!;
            _mappedPrevMorphWeightsBuffers = null!;
        }
        _currentMorphShadow = Array.Empty<Vector4>();

        _primitiveInstanceStreams = Array.Empty<PrimitiveInstanceStream>();
        _animationStates = Array.Empty<InstanceAnimationState>();

        // Unified highlighting: release the highlight pool
        // (per-instance Bounds box pool + host box)
        DisposeHighlights();
        base.Dispose();
    }
}
