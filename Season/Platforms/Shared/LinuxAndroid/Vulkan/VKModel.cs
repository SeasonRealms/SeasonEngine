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
/// N-buffered palette ring at binding 9 (refreshed in OnBeforeDraw), animation ticking,
/// recursive primitive collection from the node tree,
/// and ProcessMaterial (five PBR textures).
/// </summary>
internal unsafe class VKModel : VKPrimitiveGroup
{
    // Reuse GltfAsset loading/playback for nodes/animation/skin through composition,
    // so single inheritance is not consumed by GltfAsset.
    readonly GltfAsset _asset = new();

    internal GltfAsset Asset => _asset;

    // Skinning palettes: binding-9 storage ring (current) plus the previous-frame ring
    int _bonePaletteStride = 1;
    BufferResource[] _instanceBoneBuffers = null!;
    byte*[] _mappedInstanceBoneBuffers = null!;

    // 2-3 Step C (track B): prev bone palette SSBO
    // (same capacity as the MaxBones headroom, one Matrix4x4 per entry).
    // Before each frame upload, memcpy the current mapped region to the prev mapped region,
    // so the GPU always holds the bone palette from the previous frame.
    // Before the first frame, the content is all zero (sentinel _m33 == 0),
    // and the shader falls back to the current bone per joint.
    BufferResource[] _prevBonePaletteBuffers = null!;
    byte*[] _mappedPrevBonePaletteBuffers = null!;
    /// <summary>CPU copy of the bone palette this model uploaded last frame. The prev SSBO is filled from here
    /// rather than from the current frame's mapped region: that slot holds the palette of the frame that last used
    /// fi, which with N frames in flight is N frames old, not one.</summary>
    Matrix4x4[] _prevBonePaletteCpu = Array.Empty<Matrix4x4>();

    // 2-3 Step C (track C-b completion): prev morph weights SSBO
    // (one float4 = 16 bytes).
    // In the non-instanced path, the shader reads g_PrevMorphWeights[0],
    // so only one element is needed.
    // Each frame, ApplyMorphTargetsIfNeeded copies the old weights here before writing new ones,
    // so the shader can reconstruct the previous local position to compute morph velocity.
    BufferResource[] _prevMorphWeightsBuffers = null!;
    float*[] _mappedPrevMorphWeightsBuffers = null!;

    // --- single-model GPU skinning (Model.SkinningMode = Gpu) ---
    //
    // The same kernel, parameter row and frame latch the instanced path uses, filled with exactly one row. What differs on this
    // backend is only where the palette lives: the kernel writes a compute-created storage buffer, so the host-visible ring both
    // routes uploaded into before cannot carry it, and the descriptor sets - which hold the VkBuffer handle by value - have to be
    // rewritten whenever the buffer they name changes. That rewrite is the one thing VK needs and Metal does not.

    /// <summary>The shared target implementation, one instance wide. Registered at load, unregistered by Dispose.</summary>
    readonly Season.Rendering.InstanceSkinningTarget _skinning = new();

    /// <summary>True while this frame draws from the kernel's palette. Latched once per Update so a frame cannot half-switch
    /// between the two palettes, which would compose half a pose from one source and half from the other.</summary>
    bool _gpuSkin;

    /// <summary>Set once a bake has been asked for and refused. A refusal is permanent - it comes from the asset's shape, which
    /// nothing changes after load - and without this the retry in <see cref="BeginSkinningFrame"/> would rescan the whole clip set
    /// every frame for the life of a model the bake declines.</summary>
    bool _bakeRefused;

    /// <summary>The palette ring this class creates because the GPU route needs a compute-written palette. Owned here, disposed here.</summary>
    Season.Rendering.InstanceBoneStorage? _gpuPalette;

    /// <summary>The ring flattened into the shape the descriptor writer consumes, rebuilt only when the ring's buffer set changes.</summary>
    BufferResource[]? _boneStorageRing;

    /// <summary>The rest pose, shared with the other three backends so a route whose correctness rests on two matrices cancelling
    /// has one implementation of the snapshot rather than four.</summary>
    readonly Season.Models.RestPoseSnapshot _restPose = new();

    public VKModel(string name)
    {
        Name = name;
        // Inject two glTF-specific hooks: node factory + primitive processing
        _asset.CreateGLTFNodeCallback = CreateGLTFNode;
        _asset.ProcessPrimitiveCallback = ProcessPrimitive;
    }

    public void Load(Season.Controls.Model model)
    {
        // Animation querying/switching belongs to the glTF parsing domain and does not go through IGraphics:
        // inject the asset reference into the control in the direct-load path.
        model.Asset = _asset;

        _asset.Load(model);
        _asset.PlayAnimation();

        // Create the binding-9 skinning palette ring
        CreateInstanceBoneBuffers();
        // 2-3 Step C (tracks B/C-b completion): create prev bone + prev morph-weights SSBOs
        CreatePrevBonePaletteBuffer();
        CreatePrevMorphWeightsBuffer();
        SyncSkinningMaterialParams();

        // Single-model GPU skinning: the palette ring and the bake are created lazily by the first BeginSkinningFrame, because
        // whether either is wanted is a property of the control (SkinningMode, possibly overridden per process) and this call may
        // precede that decision. The rest snapshot does belong here: it is a property of the asset alone, and capturing it at load
        // means the first GPU frame already has the matrix the palette will be relative to.
        _restPose.Capture(_asset);
        Season.Rendering.InstanceSkinning.Register(_skinning);

        _asset.ValidateSkinData();

        // After all resources are fully ready, including the binding-9 palette ring,
        // fill back the DescriptorSet for all PrimitiveData.
        // ProcessPrimitive is called before that ring is created,
        // so this is deferred until the end of Load.
        var all = new List<PrimitiveData>();
        CollectPrimitives(all);
        foreach (var p in all) AllocateAndWriteDescriptorSets(p);
    }

    public VKModel CreateInstance(Season.Controls.Model model)
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
        // 2-3 Step C (tracks B/C-b completion): create prev bone + prev morph-weights SSBOs
        instance.CreatePrevBonePaletteBuffer();
        instance.CreatePrevMorphWeightsBuffer();

        var nodeMap = new Dictionary<GltfNodeBase, GltfNodeBase>();
        foreach (var nodeBase in _asset.gltfNodes)
            instance.EnsureClonedNode(nodeMap, nodeBase, model);

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
                instance.EnsureClonedNode(nodeMap, joint, model);

            if (sourceSkin.SkeletonRoot != null)
                instance.EnsureClonedNode(nodeMap, sourceSkin.SkeletonRoot, model);

            if (sourceSkin.BindNode != null)
                instance.EnsureClonedNode(nodeMap, sourceSkin.BindNode, model);
        }

        foreach (var animation in _asset._animations)
        {
            foreach (var channel in animation.Channels)
            {
                if (channel.Target?.Node != null)
                    instance.EnsureClonedNode(nodeMap, channel.Target.Node, model);
            }
        }

        foreach (var nodeBase in _asset.gltfNodes)
        {
            var sourceNode = nodeBase;
            var clonedNode = nodeMap[sourceNode];
            clonedNode.Children = sourceNode.Children.Select(child => instance.EnsureClonedNode(nodeMap, child, model)).ToList();

            if (sourceNode.Skin != null && skinMap.TryGetValue(sourceNode.Skin, out var clonedSkin))
                clonedNode.Skin = clonedSkin;
        }

        foreach (var pair in skinMap)
        {
            var sourceSkin = pair.Key;
            var clonedSkin = pair.Value;
            clonedSkin.Joints = sourceSkin.Joints.Select(joint => instance.EnsureClonedNode(nodeMap, joint, model)).ToList();
            clonedSkin.SkeletonRoot = sourceSkin.SkeletonRoot != null ? instance.EnsureClonedNode(nodeMap, sourceSkin.SkeletonRoot, model) : null;
            clonedSkin.BindNode = sourceSkin.BindNode != null ? instance.EnsureClonedNode(nodeMap, sourceSkin.BindNode, model) : null;
        }

        instance._asset.gltfNodes = _asset.gltfNodes.Select(node => nodeMap[node]).ToList();
        instance._asset._nodeMap = _asset._nodeMap.ToDictionary(kvp => kvp.Key, kvp => nodeMap[kvp.Value]);
        instance._asset._animations = AnimationOps.CloneAnimations(_asset._animations, nodeMap);
        instance._asset._animationPlayer = new GLTFAnimationPlayer();
        instance._asset._animationPlayer.Initialize(instance._asset._animations);
        instance._asset.PlayAnimation();
        // Shared-template instancing path:
        // inject the instance asset into the control, matching the semantics of direct-load Load.
        model.Asset = instance._asset;
        instance.CreateInstanceBoneBuffers();
        instance.SyncSkinningMaterialParams();

        // Single-model GPU skinning on a shared-template instance: the cloned tree is complete by now, so the rest snapshot is
        // taken from the instance's own nodes rather than shared with the template - the clone is what the draw walks.
        instance._restPose.Capture(instance._asset);
        Season.Rendering.InstanceSkinning.Register(instance._skinning);

        var all = new List<PrimitiveData>();
        instance.CollectPrimitives(all);
        // Same contract as AllocateAndWriteDescriptorSets: a rewrite reachable from the background loading thread
        // must stay under the GPU gate, otherwise it reads LightConstantBuffers / ShadowMap / Pipeline.SetLayout
        // while the render thread is rebuilding them during a resize or quality switch.
        // The scope covers only this loop - holding the gate across the whole CreateInstance would stall frames
        // for the duration of an asset clone.
        using (Device.EnterGpuAccessScope())
        {
            foreach (var primitive in all)
                instance.RewriteDescriptorSets(primitive);
        }

        return instance;
    }

    GltfNodeBase EnsureClonedNode(Dictionary<GltfNodeBase, GltfNodeBase> nodeMap, GltfNodeBase sourceNode, Season.Controls.Model model)
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
                vkNode.Primitives.Add(ClonePrimitiveData(primitive, vkNode, model));

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

    PrimitiveData ClonePrimitiveData(PrimitiveData source, GLTFNode ownerNode, Season.Controls.Model model)
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
            PartName = source.PartName,
            PartNodeName = source.PartNodeName,
            PartBaseColor = source.PartBaseColor,
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

        // The clone's own baseline is the template's BaseColor with this model's MaterialColor folded in, so a part
        // override that is later cleared restores what this control would draw without one.
        clone.PartBaseColor = new Vector3(clone.MaterialParams.BaseColor.X, clone.MaterialParams.BaseColor.Y, clone.MaterialParams.BaseColor.Z);
        ApplyPartColorOverride(clone, model);

        clone.VertexBuffer = Device.ResourceManager.CreateVertexBuffer(clone.Vertices.ToArray());
        clone.IndexBuffer = Device.ResourceManager.CreateIndexBuffer(clone.Indices);

        CreateMatrixBuffer(clone);
        CreateMaterialBuffer(clone);

        var matrices = new MatrixBuffer
        {
            World = Matrix4x4.Transpose(Matrix4x4.Identity),
        };

        for (int i = 0; i < Device.frameCount; i++)
        {
            Unsafe.Write(clone.MappedMatrixBuffers[i], matrices);
            Unsafe.Write(clone.MappedMaterialBuffers[i], clone.MaterialParams);
        }

        AllocateAndWriteDescriptorSets(clone);
        return clone;
    }

    /// <summary>VKModel supplies its own binding-9 storage ring, overriding the base class's default.</summary>
    protected override BufferResource[] InstanceBoneBuffers
        => _boneStorageRing
            ?? (_instanceBoneBuffers != null && _instanceBoneBuffers.Length > 0
                ? _instanceBoneBuffers
                : Pipeline.IdentityInstanceBoneBuffers);

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
    // (same capacity as the current ring: MaxBones rows)
    void CreatePrevBonePaletteBuffer()
    {
        int n = (int)Device.frameCount;
        _prevBonePaletteBuffers = new BufferResource[n];
        _mappedPrevBonePaletteBuffers = new byte*[n];
        _prevBonePaletteCpu = Array.Empty<Matrix4x4>();
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
    {
        // GPU route: the previous palette is the ring's slot one back, not a CPU copy. Descriptor set fi binds ring slot fi - the
        // slot this frame's kernel writes - so fi - 1 is exactly what the previous frame wrote. The CPU ring's own prev SSBO is
        // not updated on this route at all, and pointing the motion-vector path at it would compare this frame against whichever
        // CPU-route pose the model last left there.
        if (_boneStorageRing != null && _boneStorageRing.Length > 0)
        {
            int slots = _boneStorageRing.Length;
            var prevSlot = _boneStorageRing[((fi % slots) + slots - 1) % slots];
            if (prevSlot.Buffer.Handle != 0)
                return new() { Buffer = prevSlot.Buffer, Offset = 0, Range = Vk.WholeSize };
        }

        return _prevBonePaletteBuffers != null && fi < _prevBonePaletteBuffers.Length
            ? new() { Buffer = _prevBonePaletteBuffers[fi].Buffer, Offset = 0, Range = Vk.WholeSize }
            : base.GetPrevBoneBufferInfo(fi);
    }

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

    /// <summary>
    /// Locks this frame onto one of the two routes and returns the bake the kernel will evaluate, or null for the CPU route. The
    /// null return is what the caller branches on, so it never has to re-read the mode.
    ///
    /// Two settings differ from the instanced path. The block is one instance wide, and it reserves no tag row: a tag exists so a
    /// non-zero instance index can find its own block, and a single model always addresses row <c>jointIndex</c> from row zero
    /// because <c>isInstanced</c> is false. Reserving one here would make the kernel write a row the vertex stage never reads.
    /// </summary>
    Season.Models.InstanceSkinBake? BeginSkinningFrame(Season.Controls.Model model)
    {
        bool wanted = Season.Rendering.InstanceSkinning.Enabled
            && (Season.Rendering.InstanceSkinning.Override ?? model.SkinningMode) == Season.Rendering.InstanceSkinningMode.Gpu;

        if (wanted && _asset.SkinBake == null && !_bakeRefused)
        {
            // The mode may have been turned on after load, which is not the intended order but is not worth refusing: the build is
            // a one-off cost at the next Update, and silently keeping the CPU route for the rest of the run is invisible to whoever
            // set the property. TryCreate rather than TryBake: what has to be told apart here is "cannot be built" from "not built
            // yet", and a backend without vertex-stage storage reads counts as the former even though it is a capability rather
            // than a shape - capability is fixed for the life of the process, and building tables on such a backend would spend
            // memory on data no draw could read.
            var g = Season.Basic.Graphics.Instance;
            if (g == null || !g.ComputeSupported || !g.VertexShaderStorageReadSupported)
                _bakeRefused = true;
            else if (Season.Models.InstanceSkinBake.TryCreate(_asset, out var freshBake))
                _asset.SkinBake = freshBake;
            else
                _bakeRefused = true;
        }

        var bake = wanted ? _asset.SkinBake : null;
        bool wasGpu = _gpuSkin;

        if (bake == null)
        {
            _skinning.Begin(null, null, 0, 0);
            _gpuSkin = false;
            return null;
        }

        // A ring of one slot has no previous frame to point at, and the previous-slot read would then alias the slot this frame
        // writes. Vulkan always runs with at least two frames in flight, but the fallback is one comparison.
        if (Device.frameCount < 2)
        {
            _skinning.Begin(null, null, 0, 0);
            _gpuSkin = false;
            return null;
        }

        _gpuPalette ??= new Season.Rendering.InstanceBoneStorage();
        // tagRows = 0. The stride is the joint count on both sides: the bake's PaletteStride and the block the ring allocates.
        if (!_skinning.Begin(_gpuPalette, bake, _bonePaletteStride, _asset._animations.Count, 0))
        {
            _skinning.Begin(null, null, 0, 0);
            _gpuSkin = false;
            return null;
        }

        int slots = (int)Device.frameCount;
        int current = (int)(Device.FrameIndex % Device.frameCount);
        int previous = (int)((Device.FrameIndex + Device.frameCount - 1) % Device.frameCount);

        // One instance, so one parameter row; the kernel evaluates that row and writes rows [0, jointCount).
        if (!_skinning.PreparePalette(Season.Basic.Graphics.Instance, slots, current, previous, 1)
            || !_skinning.BeginFrame(1))
        {
            _skinning.Begin(null, null, 0, 0);
            _gpuSkin = false;
            return null;
        }

        _gpuSkin = true;

        // The palette is relative to the rest pose, so the per-primitive factor has to be the rest world too. Entering the route
        // from the CPU one leaves an animated pose in the tree, and picking leaves one behind mid-route; both are undone here
        // rather than where they happen, because this is the only place that knows the tree is being handed over.
        if (!wasGpu || _asset.CpuPoseDisturbed)
        {
            _asset.CpuPoseDisturbed = false;
            _restPose.Restore(_asset);
        }

        return bake;
    }

    /// <summary>
    /// Rebuilds <see cref="_boneStorageRing"/> and returns whether the buffers it points at changed, which is the condition for
    /// rewriting the descriptor sets: the sets hold the VkBuffer handle by value, so a reallocation - or the first switch to a ring
    /// at all - leaves them addressing a retired buffer. The decision is this frame's latch rather than the handoff result, because
    /// the set is per slot and the vertex-read barrier is recorded by the effect at FrameStart, after Update has already rebuilt
    /// these sets.
    /// </summary>
    bool SyncBoneStorageRing()
    {
        var ring = _gpuSkin ? _skinning.Palette : null;
        if (ring == null)
        {
            bool had = _boneStorageRing != null;
            _boneStorageRing = null;
            return had;
        }

        int count = ring.Slots.Length;
        bool changed = _boneStorageRing == null || _boneStorageRing.Length != count;
        if (changed)
            _boneStorageRing = new BufferResource[count];

        for (int i = 0; i < count; i++)
        {
            var buffer = ring.Slots[i] is VKStorageBuffer vk ? vk.Buffer : default;
            if (_boneStorageRing![i].Buffer.Handle != buffer.Buffer.Handle)
            {
                _boneStorageRing[i] = buffer;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// Republishes the palette binding to every descriptor set when the buffer set changed - a first switch to the ring, a
    /// fallback to the CPU ring, or a resize. Shell sets are included for the same reason as on a bone-buffer resize: they bind
    /// the same palette, and leaving them on a retired buffer reads freed memory.
    /// </summary>
    void CommitBoneStorageRing()
    {
        if (!SyncBoneStorageRing())
            return;

        var primitives = new List<PrimitiveData>();
        CollectPrimitives(primitives);
        for (int i = 0; i < primitives.Count; i++)
            RewriteDescriptorSets(primitives[i]);
        RewriteShellDescriptorSets();
    }

    void ProcessPrimitive(MeshPrimitive meshPrimitive, GltfNodeBase node, Season.Controls.Model model)
    {
        var primitiveData = CreatePrimitiveData(meshPrimitive, node, model);

        var gltfNode = (GLTFNode)node;
        gltfNode.Primitives.Add(primitiveData);
    }

    PrimitiveData CreatePrimitiveData(MeshPrimitive primitive, GltfNodeBase node, Season.Controls.Model model)
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
        };
        for (int i = 0; i < Device.frameCount; i++)
            Unsafe.Write(p.MappedMatrixBuffers[i], matrices);

        // Note: the DescriptorSet is allocated uniformly at the end of Load,
        // because the binding-9 palette ring must already exist
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

        // Part identity: the material name is the primary key Model.SetPartColor resolves against, the owning node name the
        // fallback for material-less primitives. Recorded before the colors below so the override can use it in this same call.
        p.PartName = primitive.Material?.Name;
        p.PartNodeName = p.OwnerNode?.Name;

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

            // The two scalar factors are written unconditionally: glTF defines the effective value as
            // factor * texture channel, so the shader needs them even when a metallic-roughness map exists.
            // They used to be written only in the "map missing" branches (and MetallicFactor was keyed off the
            // normal map by mistake), which left every textured material multiplying against a stale zero.
            p.MaterialParams.MetallicFactor = gLTFMaterial1.MetallicFactor;
            p.MaterialParams.RoughnessFactor = gLTFMaterial1.RoughnessFactor;
        }
        else
        {
            // No glTF material at all: fall back to the engine's documented neutral dielectric rather than
            // the all-zero struct default, whose roughness 0 would read as a perfect mirror.
            p.MaterialParams.MetallicFactor = 0f;
            p.MaterialParams.RoughnessFactor = 0.5f;
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

        // Part colors: the load-time RGB baseline (with the MaterialColor tint already folded in) is what a cleared
        // override returns to, and a set override replaces the RGB outright. Direct-load path only: the shared-template
        // path bakes the requesting model's overrides in ClonePrimitiveData instead, where they are actually known.
        p.PartBaseColor = new Vector3(
            p.MaterialParams.BaseColor.X,
            p.MaterialParams.BaseColor.Y,
            p.MaterialParams.BaseColor.Z);
        ApplyPartColorOverride(p, _asset.Model);

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

    /// <summary>Compute bone matrices and write them into the current frame's dynamic storage buffer.</summary>
    void UploadBoneMatricesToGpu()
    {
        // The GPU route's whole saving is this early exit: the kernel produced the palette and the ring it wrote is what the
        // descriptor sets bind, so no curve is sampled and no byte is copied here. Both callers (Update and OnBeforeDraw) land
        // on this same gate, which matters because OnBeforeDraw runs after Update has latched the route for the frame.
        if (_gpuSkin) return;

        _asset._animationPlayer.UpdateBoneMatrices(_asset.GetAllSkins());
        var boneMatrices = _asset._animationPlayer.GetBoneMatricesArray();

        if (boneMatrices.Length == 0 || _mappedInstanceBoneBuffers == null) return;

        int matrixSize = Unsafe.SizeOf<Matrix4x4>();
        int fi = (int)Device.FrameIndex;

        // 2-3 Step C (track B): publish the PREVIOUS frame's palette (CPU shadow) into this frame's prev SSBO slot
        // before the current palette overwrites anything. On the first frame the shadow is empty, so the slot stays
        // zeroed and the shader's _m33 == 0 sentinel falls back to the current bone per joint.
        if (_mappedPrevBonePaletteBuffers != null && fi < _mappedPrevBonePaletteBuffers.Length
            && _mappedPrevBonePaletteBuffers[fi] != null && _prevBonePaletteCpu.Length > 0)
        {
            int prevCount = Math.Min(_prevBonePaletteCpu.Length, MaxBones);
            fixed (Matrix4x4* pPrev = _prevBonePaletteCpu)
                Unsafe.CopyBlock(_mappedPrevBonePaletteBuffers[fi], pPrev, (uint)(matrixSize * prevCount));
        }

        fixed (void* matricesPtr = boneMatrices)
        {
            if (fi < _mappedInstanceBoneBuffers.Length && _mappedInstanceBoneBuffers[fi] != null)
            {
                int dynamicSize = matrixSize * Math.Min(boneMatrices.Length, _bonePaletteStride);
                Unsafe.CopyBlock(_mappedInstanceBoneBuffers[fi], matricesPtr, (uint)dynamicSize);
            }
        }

        // Remember this frame's palette for the next frame's prev slot.
        if (_prevBonePaletteCpu.Length != boneMatrices.Length)
            _prevBonePaletteCpu = new Matrix4x4[boneMatrices.Length];
        Array.Copy(boneMatrices, _prevBonePaletteCpu, boneMatrices.Length);
    }

    void ApplyUserTransformToNodeTree(GltfNodeBase nodeBase, Matrix4x4 userTransform)
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
                // 2-3 contract clause 6: history always comes from the CPU shadow copy
                // (Transpose(all-zero) == all-zero, so the first frame naturally uses the unwritten sentinel)
                PrevWorld = Matrix4x4.Transpose(primitive.PrevWorldMatrix),
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
            ApplyUserTransformToNodeTree(child, userTransform);
        }
    }

    public void Update(Season.Controls.Model model, float time)
    {
        bool wasInitialized = _transformInitialized;

        // Latch the route before anything reads the tree. A frame that switched halfway would compose half a pose on the CPU and
        // half in the kernel, which renders as a plausible wrong pose rather than as an error.
        var gpuBake = BeginSkinningFrame(model);

        if (gpuBake != null)
        {
            // The kernel samples the bake, so the CPU only advances the clock and publishes the instance row.
            GpuBakeRoute.Advance(_asset, _skinning, gpuBake, time);
        }
        else
        {
            // Update animation through the animation player
            // (time advancement, keyframe lookup, TRS interpolation, and node-transform updates are handled internally)
            _asset._animationPlayer.Update(time, _asset.gltfNodes);
        }

        // Morph channels are one of the bake's eligibility rules, so on the GPU route no curve can move a weight and this pass
        // rewrites last frame's values. It is kept regardless because it is also the upload, and a weight set by code rather than
        // by a clip still has to reach the GPU.
        UpdateMorphTargetsRecursive(_asset.gltfNodes);

        // Apply the user transform to the root nodes
        // (unified transform convention: converge on BuildWorldMatrix with anchor pivot, see Mesh3DBase)
        var userTransform = model.BuildWorldMatrix();

        // Find all root nodes and apply the user transform
        // (the player caches root-node list references to avoid O(N^2) lookup each frame)
        var rootNodes = _asset._animationPlayer.GetRootNodes(_asset.gltfNodes);

        foreach (var rootNode in rootNodes)
            ApplyUserTransformToNodeTree(rootNode, userTransform);

        // Update bone matrices. On the GPU route the kernel already produced them and the ring it wrote is what the draw binds,
        // so the CPU palette is not written at all - which is the saving this route exists for.
        if (gpuBake == null)
            UploadBoneMatricesToGpu();

        // The descriptor sets hold VkBuffer handles by value, so a ring that appeared, disappeared or was reallocated has to be
        // republished. Keyed on this frame's latch rather than on the handoff result, exactly as the instanced path does: the set
        // is per frame slot, and slot fi is the slot this frame's kernel writes.
        CommitBoneStorageRing();

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

        // Per-part base colors follow the alpha sync: the two write different channels of the same UBO (RGB vs W), and the
        // sync is version-gated, so this costs one compare on every frame that did not change the part-color set.
        SyncPartColors(model);

        // Unified highlighting: wireframe shells, Bounds box and Outline2D state (the OutlineMask pass in Graphics
        // collects the state). Shared with DX/Metal; only WriteHighlightBox differs.
        SyncModelHighlight(model, userTransform);
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
    /// ensure the binding-9 palette ring for the current FrameIndex already holds
    /// the latest bone matrices.
    /// This avoids reading previous-frame or identity data when Update and Draw land in different frame slots.
    /// </summary>
    protected override void OnBeforeDraw()
    {
        UploadBoneMatricesToGpu();
    }

    public override void Dispose()
    {
        // Leave the scheduler before the buffers it points at go away, and release the compute-side parameter and scratch blocks
        // the target owns. The palette ring is this class's own, so it dies here too; the flattened ring is only a view and is
        // dropped without touching the buffers.
        _skinning.Dispose();
        _gpuPalette?.Dispose();
        _gpuPalette = null;
        _boneStorageRing = null;
        _gpuSkin = false;

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
