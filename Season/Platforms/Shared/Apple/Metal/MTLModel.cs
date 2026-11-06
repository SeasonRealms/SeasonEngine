// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Metal;
using Season.Models;
using SharpGLTF.Runtime;
using SharpGLTF.Schema2;
using System.Runtime.CompilerServices;
using MTLTexture = Season.Platforms.Shared.Apple.Metal.Texture;

namespace Season.Platforms.Shared.Apple.Metal;

// glTF node extension on Apple Metal, used to store the corresponding PrimitiveData list.
internal class GLTFNode : GltfNodeBase
{
    public List<PrimitiveData> Primitives = new();
}

/// <summary>
/// Metal backend for glTF models.
/// It inherits from MTLPrimitiveGroup to reuse Matrix and Material UBO creation,
/// SyncAlpha, and three-bucket grouped drawing,
/// while composing GltfAsset to load the node tree, animation data, and skins.
/// Its own responsibilities are glTF-specific:
/// N-buffered CPU bone palette written directly during Update and bound at VS buffer(6),
/// animation ticking,
/// recursive primitive collection from the node tree,
/// and ProcessMaterial for the five PBR textures.
/// </summary>
internal sealed unsafe class MTLModel : MTLPrimitiveGroup
{
    // Reuse node, animation, and skin loading plus playback through composition with GltfAsset,
    // avoiding conflicts with single inheritance.
    readonly GltfAsset _asset = new();

    // CPU bone palette, N-buffered. It is the slot-6 palette on the single-model route; there is no b3 UBO any more.
    IMTLBuffer[] _bonePaletteBuffers = null!;

    // Contract clause 8(b) of 2-3: warmup for the palette frame ring.
    // The frame ring is initialized with Identity rather than all zeros,
    // so bm[3][3] == 0 cannot be used to detect an unwritten cold slot.
    // The shader may use slot [fi-1] as the previous palette only after the ring has been fully written once.
    int _boneRingWarmup;
    bool _hasPrevBonesPublished;

    // --- single-model GPU skinning (Model.SkinningMode = Gpu) ---
    //
    // The same kernel, parameter row and frame latch the instanced path uses, filled with exactly one row. Metal needs the least
    // glue of the four backends: the palette is bound per draw through setVertexBuffer rather than through a descriptor set, so a
    // compute-written storage buffer can stand in for the mapped bone palette by swapping the handle this class hands the group - no
    // rewrite step, no binding table.

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

    /// <summary>The ring flattened to the frame-indexed shape <see cref="BonePaletteBuffers"/> returns, rebuilt only when a slot's
    /// buffer is replaced. Null on the CPU route, which is what makes that property fall through to the mapped ring.</summary>
    IMTLBuffer[]? _boneStorageRing;

    /// <summary>The rest pose, shared with the other three backends so a route whose correctness rests on two matrices cancelling
    /// has one implementation of the snapshot rather than four.</summary>
    readonly Season.Models.RestPoseSnapshot _restPose = new();

    internal GltfAsset Asset => _asset;

    public MTLModel(string name)
    {
        Name = name;
        // Inject two glTF-specific hooks: node factory and primitive processing.
        _asset.CreateGLTFNodeCallback = CreateGLTFNode;
        _asset.ProcessPrimitiveCallback = ProcessPrimitive;
    }

    public void Load(Season.Controls.Model model)
    {
        // Animation queries and switching belong to the glTF parsing domain and do not go through IGraphics.
        // On the direct-load path, inject the asset reference into the control.
        model.Asset = _asset;

        _asset.Load(model);

        // Create the CPU bone palette, N-buffered like DX and VK. The shader reads it at VS buffer(6), not the retired b3 slot.
        CreateBonePaletteBuffer();

        // Single-model GPU skinning: the palette ring and the bake are created lazily by the first BeginSkinningFrame, because
        // whether either is wanted is a property of the control (SkinningMode, possibly overridden per process) and this call may
        // precede that decision. The rest snapshot does belong here: it is a property of the asset alone.
        _restPose.Capture(_asset);
        Season.Rendering.InstanceSkinning.Register(_skinning);

        _asset.ValidateSkinData();
    }

    public MTLModel CreateInstance(Season.Controls.Model model)
    {
        if (_asset.Model != null)
        {
            model.Size = _asset.Model.Size;
            model.OriginalScale = _asset.Model.OriginalScale;
            // For 1-3, GltfAsset.Load on the shared-template path only fills LocalBounds on the temporary template Model.
            // Copy it back into the user control, or control-level culling will never activate because the empty-bounds guard stays in effect.
            model.LocalBounds = _asset.Model.LocalBounds;
            // Unified positioning contract:
            // also copy back the raw bounds.
            // The setter triggers OnBoundsEstablished, which finalizes default size,
            // so this must happen after Size and OriginalScale.
            model.LocalBoundsRaw = _asset.Model.LocalBoundsRaw;
            // For 1-2, also copy back imported KHR punctual lights.
            // They are local-space read-only data, so sharing references is sufficient.
            // Otherwise AppendWorldLights would see an empty list on the shared-template path.
            model.ImportedPunctualLights = _asset.Model.ImportedPunctualLights;
        }

        var instance = new MTLModel(Name);
        instance._transformInitialized = false;
        instance._asset.Model = model;
        instance._asset._nodeTransforms = new Dictionary<GltfNodeBase, Matrix4x4>();
        instance.CreateBonePaletteBuffer();

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
        instance._asset._animations = CloneAnimations(_asset._animations, nodeMap);
        instance._asset._animationPlayer = new GLTFAnimationPlayer();
        instance._asset._animationPlayer.Initialize(instance._asset._animations);
        // Shared-template instancing path:
        // inject the instance asset into the control, matching the semantics of the direct-load path.
        model.Asset = instance._asset;

        // When primitives are cloned, OwnerNode.Skin has not been finalized yet.
        // ClonePrimitiveData and ProcessMaterial run before skin assignment above,
        // so IsSkinned is computed as 0 from a null skin and the shader would otherwise skip skinning and animation.
        // After skin relationships are finalized, recompute the value and write it back to material UBOs for all frames,
        // aligned with DX InitializeBonePaletteResources and VK SyncSkinningMaterialParams.
        var primitives = new List<PrimitiveData>();
        instance.CollectPrimitives(primitives);
        foreach (var primitive in primitives)
        {
            primitive.MaterialParams.IsSkinned = primitive.OwnerNode?.Skin != null ? 1u : 0u;
            primitive.MaterialParams.BonePaletteStride = 1u;
            for (int i = 0; i < Device.frameCount; i++)
                WriteStruct(primitive.MaterialBuffers[i], primitive.MaterialParams);
        }

        // Single-model GPU skinning on a shared-template instance: the cloned tree is complete by now, so the rest snapshot is
        // taken from the instance's own nodes rather than shared with the template - the clone is what the draw walks.
        instance._restPose.Capture(instance._asset);
        Season.Rendering.InstanceSkinning.Register(instance._skinning);

        return instance;
    }

    GltfNodeBase EnsureClonedNode(Dictionary<GltfNodeBase, GltfNodeBase> nodeMap, GltfNodeBase sourceNode, Season.Controls.Model model)
    {
        if (nodeMap.TryGetValue(sourceNode, out var existing))
            return existing;

        GltfNodeBase clonedNode;
        if (sourceNode is GLTFNode sourceMtlNode)
        {
            var mtlNode = new GLTFNode
            {
                Name = sourceMtlNode.Name,
                LogicalIndex = sourceMtlNode.LogicalIndex,
                Mesh = sourceMtlNode.Mesh,
                IsJoint = sourceMtlNode.IsJoint,
                JointIndex = sourceMtlNode.JointIndex,
                Translation = sourceMtlNode.InitialTranslation,
                Rotation = sourceMtlNode.InitialRotation,
                Scale = sourceMtlNode.InitialScale,
                InitialTranslation = sourceMtlNode.InitialTranslation,
                InitialRotation = sourceMtlNode.InitialRotation,
                InitialScale = sourceMtlNode.InitialScale,
                InitialWeights = sourceMtlNode.InitialWeights.Length == 0 ? Array.Empty<float>() : (float[])sourceMtlNode.InitialWeights.Clone(),
                Weights = sourceMtlNode.Weights.Length == 0 ? Array.Empty<float>() : (float[])sourceMtlNode.Weights.Clone(),
                WeightsVersion = sourceMtlNode.WeightsVersion,
                WorldTransform = sourceMtlNode.WorldTransform,
            };

            foreach (var primitive in sourceMtlNode.Primitives)
                mtlNode.Primitives.Add(ClonePrimitiveData(primitive, mtlNode, model));

            clonedNode = mtlNode;
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
            // For 2-3, morphing stays on the GPU.
            // Clone the shared-template morph delta buffer as read-only constant data,
            // and keep OwnsMorphDeltasBuffer false to avoid double release.
            // Vertices remain in rest pose.
            clone.Vertices = new List<Vertex>(source.Vertices);
            clone.MorphDeltasBuffer = source.MorphDeltasBuffer;
            clone.OwnsMorphDeltasBuffer = false;
            clone.MaterialParams.HasMorphTargets = source.MaterialParams.HasMorphTargets;
            clone.MaterialParams.MorphTargetCount = source.MaterialParams.MorphTargetCount;
            clone.MaterialParams.MorphVertexCount = source.MaterialParams.MorphVertexCount;
            clone.MaterialParams.MorphWeights = ExtractMorphWeights(ownerNode);
            clone.MaterialParams.PrevMorphWeights = clone.MaterialParams.MorphWeights;
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
            WriteStruct(clone.MatrixBuffers[i], matrices);
            WriteStruct(clone.MaterialBuffers[i], clone.MaterialParams);
        }

        return clone;
    }

    static void ApplyInstanceMaterialOverrides(PrimitiveData primitive, Season.Controls.Model model)
    {
        var colorTint = model.MaterialColor ?? Vector4.One;
        primitive.MaterialParams.BaseColor *= colorTint;
        primitive.MaterialParams.RenderMode = model.Unlit ? 0u : 1u;
        primitive.MaterialParams.IsSkinned = primitive.OwnerNode?.Skin != null ? 1u : 0u;
        primitive.MaterialParams.BonePaletteStride = 1u;
        primitive.OriginalBaseColorAlpha = primitive.MaterialParams.BaseColor.W;
    }

    /// <summary>
    /// Replaces one primitive's RGB with the override the model holds for its part, if any. Absolute rather than
    /// multiplicative - see <see cref="Season.Controls.Model.SetPartColor"/> for why a tint cannot recolor a saturated
    /// material. W is left untouched because the alpha chain belongs to SyncAlpha.
    /// </summary>
    static void ApplyPartColorOverride(PrimitiveData primitive, Season.Controls.Model model)
    {
        if (!model.TryGetPartColor(primitive.PartName, primitive.PartNodeName, out var color))
            return;

        var baseColor = primitive.MaterialParams.BaseColor;
        primitive.MaterialParams.BaseColor = new Vector4(color.X, color.Y, color.Z, baseColor.W);
    }

    /// <summary>
    /// Pushes <c>Model.SetPartColor</c> into every primitive's material UBO: RGB becomes the override, or the load-time
    /// baseline when the override was cleared. Gated on <see cref="Season.Controls.Model.PartColorVersion"/>, so a steady
    /// frame pays one integer compare; when it does move, all N-buffered frames are written, the same stale-frame rule
    /// SyncAlpha follows. The canonical <c>MaterialParams</c> struct is updated too, so anything that rewrites the UBOs from
    /// it later (the skinning sync, a clone) sees the same colors this frame drew.
    /// </summary>
    void SyncPartColors(Season.Controls.Model model)
    {
        if (_appliedPartColorVersion == model.PartColorVersion)
            return;
        _appliedPartColorVersion = model.PartColorVersion;

        int n = Device.frameCount;
        var primitives = new List<PrimitiveData>();
        CollectAllPrimitives(primitives);

        foreach (var primitive in primitives)
        {
            var rgb = model.TryGetPartColor(primitive.PartName, primitive.PartNodeName, out var color)
                ? new Vector3(color.X, color.Y, color.Z)
                : primitive.PartBaseColor;

            var canonical = primitive.MaterialParams;
            canonical.BaseColor = new Vector4(rgb.X, rgb.Y, rgb.Z, canonical.BaseColor.W);
            primitive.MaterialParams = canonical;

            for (int i = 0; i < n; i++)
            {
                var materialParams = ReadStruct<MaterialParams>(primitive.MaterialBuffers[i]);
                materialParams.BaseColor = new Vector4(rgb.X, rgb.Y, rgb.Z, materialParams.BaseColor.W);
                WriteStruct(primitive.MaterialBuffers[i], materialParams);
            }
        }
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

    /// <summary>MTLModel points BonePaletteBuffers at its own N-buffered CPU palette, overriding the base-class default IdentityBoneBuffers.
    /// On the GPU route it points at the storage ring instead: the vertex stage addresses joint <c>idx</c> from row zero when
    /// <c>isInstanced</c> is false, which is the kernel's instance-0 block, so the same VS buffer(6) binding carries the palette
    /// with no shader branch.</summary>
    protected override IMTLBuffer[] BonePaletteBuffers => _boneStorageRing ?? _bonePaletteBuffers;

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

    /// <summary>Allocates the CPU palette ring bound at VS buffer(6). Rows are seeded with Identity so a primitive drawn
    /// before its animation ever played reads a neutral pose rather than leftover memory.</summary>
    void CreateBonePaletteBuffer()
    {
        int n = Device.frameCount;
        _bonePaletteBuffers = new IMTLBuffer[n];
        nuint size = (nuint)(Unsafe.SizeOf<Matrix4x4>() * MaxBones);

        var identity = Matrix4x4.Identity;
        for (int i = 0; i < n; i++)
        {
            _bonePaletteBuffers[i] = Device.ResourceManager.CreateConstantBuffer(size);
            // Fill with Identity by default so non-playing animations never read garbage values.
            byte* basePtr = (byte*)_bonePaletteBuffers[i].Contents;
            for (int j = 0; j < MaxBones; j++)
                *(Matrix4x4*)(basePtr + j * sizeof(float) * 16) = identity;
        }
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

        // Load geometry data.
        var verticleIndices = GLTFTools.LoadMeshPrimitive(primitive);
        var morphTargets = GLTFTools.LoadMorphTargets(primitive, verticleIndices.vertices.Count);
        p.OwnerNode = node;
        p.LastAppliedWeightsVersion = node.WeightsVersion;
        p.MaterialParams.IsSkinned = node.Skin != null ? 1u : 0u;
        p.MaterialParams.BonePaletteStride = 1u;
        if (morphTargets.Count > 0)
        {
            // For 2-3, move morphing to the GPU, matching DX and VK.
            // Vertices remain in rest pose, and deformation is performed in the vertex shader through morphDeltas and morphWeights.
            // If the CPU rewrote the VB, the GPU would no longer be able to reconstruct the previous-frame shape.
            p.BaseVertices = verticleIndices.vertices.ToArray();
            p.MorphTargets = morphTargets;
            p.Vertices = verticleIndices.vertices;
        }
        else
        {
            p.Vertices = verticleIndices.vertices;
        }
        p.Indices = verticleIndices.indices.ToArray();
        p.Use32BitIndices = p.Indices.Any(i => i > ushort.MaxValue);
        p.DoubleSided = false;
        // Bounds are computed from the rest pose, matching DX and VK.
        // Under GPU morphing, the CPU no longer owns deformed vertices.
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
        ProcessMaterial(primitive, p);

        // Morph parameters must be set after ProcessMaterial,
        // because ProcessMaterial rebuilds p.MaterialParams as a whole.
        // Then rewrite the material UBO for all frames.
        if (p.BaseVertices != null && p.MorphTargets != null && p.MorphTargets.Count > 0)
        {
            CreateMorphDeltaBuffer(p, p.BaseVertices, p.MorphTargets);
            p.MaterialParams.HasMorphTargets = 1u;
            p.MaterialParams.MorphTargetCount = (uint)p.MorphTargets.Count;
            p.MaterialParams.MorphVertexCount = (uint)p.BaseVertices.Length;
            p.MaterialParams.MorphWeights = ExtractMorphWeights(node);
            p.MaterialParams.PrevMorphWeights = p.MaterialParams.MorphWeights;
            for (int i = 0; i < Device.frameCount; i++)
                WriteStruct(p.MaterialBuffers[i], p.MaterialParams);
        }

        // Initialize matrix buffers with identity matrices for all frames.
        var matrices = new MatrixBuffer
        {
            World = Matrix4x4.Transpose(Matrix4x4.Identity),
        };
        for (int i = 0; i < Device.frameCount; i++)
            WriteStruct(p.MatrixBuffers[i], matrices);

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

    /// <summary>
    /// For 2-3, upload GPU morph weights.
    /// The CPU no longer rewrites the vertex buffer and instead writes current-frame weights only into the current-frame material UBO,
    /// while rolling the previously submitted weights into PrevMorphWeights.
    /// The CPU shadow copy in p.MaterialParams is the only authoritative source
    /// and never reads back from the N-buffer ring, satisfying contract clause 6.
    /// Note that SyncAlpha and SyncMaterialParams both use read-modify-write on the UBO,
    /// so this method must do the same to avoid overwriting alpha or material overrides.
    /// </summary>
    void ApplyMorphTargetsIfNeeded(PrimitiveData primitive, GltfNodeBase node)
    {
        if (primitive.MaterialParams.HasMorphTargets == 0u)
            return;

        var weights = ExtractMorphWeights(node);

        // Advance history:
        // weights submitted on the previous frame become prev for the current frame.
        // When weights do not change, the two are equal and velocity naturally becomes zero.
        primitive.MaterialParams.PrevMorphWeights = primitive.MaterialParams.MorphWeights;
        primitive.MaterialParams.MorphWeights = weights;
        primitive.MaterialParams.HasPrevMorph = 1u;
        primitive.LastAppliedWeightsVersion = node.WeightsVersion;

        int fi = Device.FrameIndex;
        var mp = ReadStruct<MaterialParams>(primitive.MaterialBuffers[fi]);
        mp.HasMorphTargets = primitive.MaterialParams.HasMorphTargets;
        mp.MorphTargetCount = primitive.MaterialParams.MorphTargetCount;
        mp.MorphVertexCount = primitive.MaterialParams.MorphVertexCount;
        mp.MorphWeights = primitive.MaterialParams.MorphWeights;
        mp.PrevMorphWeights = primitive.MaterialParams.PrevMorphWeights;
        mp.HasPrevMorph = 1u;
        WriteStruct(primitive.MaterialBuffers[fi], mp);

        // Unified highlighting for wireframe shells:
        // shell delta buffers are expanded to shell-vertex layout and share weights with the source primitive.
        // When source weights are submitted, synchronize the same weights into both shell primitives' Material UBOs
        // using the same read-modify-write convention as this method.
        if (_wireframeBoxes != null)
        {
            for (int i = 0; i < _wireframeBoxes.Count; i++)
            {
                var shell = _wireframeBoxes[i];
                if (shell != null && shell.SourcePrimitive == primitive)
                {
                    WriteMorphWeightsToCB(shell.Face, weights);
                    WriteMorphWeightsToCB(shell.Edges, weights);
                }
            }
        }
    }

    /// <summary>
    /// Phase 3 writes morph weights into the N-buffered MaterialParams UBO for the current frame through read-modify-write.
    /// At most four morph targets are supported, and any extra targets are ignored.
    /// </summary>
    static void WriteMorphWeightsToCB(PrimitiveData primitive, Vector4 weights)
    {
        int fi = Device.FrameIndex;
        var mp = ReadStruct<MaterialParams>(primitive.MaterialBuffers[fi]);
        mp.MorphWeights = weights;
        WriteStruct(primitive.MaterialBuffers[fi], mp);
    }

    void ProcessMaterial(MeshPrimitive primitive, PrimitiveData p)
    {
        var modelRoot = primitive.LogicalParent.LogicalParent;
        var (gLTFMaterial1, images) = GLTFTools.LoadMaterial(modelRoot, primitive);

        // Part identity: the material name is the primary key Model.SetPartColor resolves against, the owning node name the
        // fallback for material-less primitives. Recorded before the colors below so the override can use it in this same call.
        p.PartName = primitive.Material?.Name;
        p.PartNodeName = p.OwnerNode?.Name;

        p.MaterialParams = new MaterialParams
        {
            RenderMode = _asset.Model.Unlit ? 0u : 1u,
            IsSkinned = p.OwnerNode?.Skin != null ? 1u : 0u,
            BonePaletteStride = 1u,
        };

        // Set transparency parameters from AlphaMode.
        // Only BLEND is truly transparent and requires blending.
        // MASK uses the Opaque PSO plus shader-side discard.
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
            // Use caller-side MaterialColor as tint and multiply it by the original glTF BaseColorFactor.
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
            // No textures are present, so keep all UseXxxMap flags at 0.
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
                p.BaseColorTexture = MTLTexture.GetOrCreate(
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
                p.NormalTexture = MTLTexture.GetOrCreate(
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
                p.MetallicRoughnessTexture = MTLTexture.GetOrCreate(
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
                p.OcclusionTexture = MTLTexture.GetOrCreate(
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
                p.EmissiveTexture = MTLTexture.GetOrCreate(
                    $"{_asset.Model.Name}-emissive-{emissiveImage.LogicalIndex}", emissiveImage,
                    TextureMipPolicy.Color);
                p.MaterialParams.UseEmissiveMap = 1u;
                p.MaterialParams.EmissiveFactor = gLTFMaterial1!.EmissiveFactor.AsVector4();
            }
        }

        // White fallback:
        // any unresolved channel binds the 1x1 white texture,
        // and shader branches with UseXxxMap = 0 do not read it.
        if (p.BaseColorTexture is null) p.BaseColorTexture = Device.White;
        if (p.NormalTexture is null) p.NormalTexture = Device.White;
        if (p.MetallicRoughnessTexture is null) p.MetallicRoughnessTexture = Device.White;
        if (p.OcclusionTexture is null) p.OcclusionTexture = Device.White;
        if (p.EmissiveTexture is null) p.EmissiveTexture = Device.White;

        // Record the original glTF BaseColor.W so Model.Alpha can be applied later as an additional multiplier.
        p.OriginalBaseColorAlpha = p.MaterialParams.BaseColor.W;

        // Part colors: the load-time RGB baseline (with the MaterialColor tint already folded in) is what a cleared
        // override returns to, and a set override replaces the RGB outright. Direct-load path only: the shared-template
        // path bakes the requesting model's overrides in ClonePrimitiveData instead, where they are actually known.
        p.PartBaseColor = new Vector3(
            p.MaterialParams.BaseColor.X,
            p.MaterialParams.BaseColor.Y,
            p.MaterialParams.BaseColor.Z);
        ApplyPartColorOverride(p, _asset.Model);

        // Record the original glTF AlphaCutoff so SyncAlpha can scale it proportionally with Model.Alpha.
        p.OriginalAlphaCutoff = p.MaterialParams.AlphaCutoff;

        // Initialize material buffers for every frame to avoid garbage values on other N-buffered frames causing full-object flicker.
        for (int i = 0; i < Device.frameCount; i++)
            WriteStruct(p.MaterialBuffers[i], p.MaterialParams);
    }

    // For 1-3, bounds calculation is unified through the shared Season.Rendering.Bounds3D.FromVertices helper on all four backends.

    /// <summary>Computes bone matrices and writes them into the current-frame palette row by writing directly into Contents memory.</summary>
    void UploadBoneMatricesToGpu()
    {
        // The GPU route's whole saving is this early exit: the kernel produced the palette and the ring it wrote is what the group
        // binds, so no curve is sampled and no byte is copied here.
        if (_gpuSkin) return;

        _asset._animationPlayer.UpdateBoneMatrices(_asset.GetAllSkins());
        var boneMatrices = _asset._animationPlayer.GetBoneMatricesArray();

        if (boneMatrices.Length == 0 || _bonePaletteBuffers == null) return;

        int matrixSize = Unsafe.SizeOf<Matrix4x4>();
        // Clamp to the palette capacity: a joint count past MaxBones would otherwise copy past the allocation.
        int totalSize = matrixSize * Math.Min(boneMatrices.Length, MaxBones);
        int fi = Device.FrameIndex;

        fixed (void* matricesPtr = boneMatrices)
        {
            Unsafe.CopyBlock((void*)_bonePaletteBuffers[fi].Contents, matricesPtr, (uint)totalSize);
        }

        // Once the frame ring has been filled for one full cycle, slot [fi-1] is guaranteed to be the previous-frame palette,
        // so publish the hasPrevBones sentinel once.
        if (!_hasPrevBonesPublished && ++_boneRingWarmup >= Device.frameCount)
        {
            PublishHasPrevBones();
            _hasPrevBonesPublished = true;
        }
    }

    /// <summary>Contract clause 8(b) of 2-3: writes hasPrevBones = 1 into material UBOs for all frames in a single read-modify-write pass.</summary>
    void PublishHasPrevBones()
    {
        var primitives = new List<PrimitiveData>(32);
        CollectPrimitives(primitives);

        foreach (var primitive in primitives)
        {
            if (primitive.MaterialParams.IsSkinned == 0u)
                continue;

            primitive.MaterialParams.HasPrevBones = 1u;
            for (int i = 0; i < Device.frameCount; i++)
            {
                var mp = ReadStruct<MaterialParams>(primitive.MaterialBuffers[i]);
                mp.HasPrevBones = 1u;
                WriteStruct(primitive.MaterialBuffers[i], mp);
            }
        }
    }

    /// <summary>
    /// Locks this frame onto one of the two routes and returns the bake the kernel will evaluate, or null for the CPU route. The
    /// null return is what the caller branches on, so it never has to re-read the mode.
    ///
    /// Two settings differ from the instanced path. The block is one instance wide, and it reserves no tag row: a tag exists so a
    /// non-zero instance index can find its own block, and a single model always addresses row jointIndex from row zero because
    /// isInstanced is false. Reserving one here would make the kernel write a row the vertex stage never reads.
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
            // than a shape - capability is fixed for the life of the process.
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
        // writes. Metal always runs with at least two frames in flight, but the fallback is one comparison.
        if (Device.frameCount < 2)
        {
            _skinning.Begin(null, null, 0, 0);
            _gpuSkin = false;
            return null;
        }

        _gpuPalette ??= new Season.Rendering.InstanceBoneStorage();
        // tagRows = 0, and the stride is the bake's own palette stride: this backend leaves BonePaletteStride at 1 because the
        // non-instanced vertex stage never multiplies by it, so there is no draw-side stride that could disagree with the bake.
        if (!_skinning.Begin(_gpuPalette, bake, bake.PaletteStride, _asset._animations.Count, 0))
        {
            _skinning.Begin(null, null, 0, 0);
            _gpuSkin = false;
            return null;
        }

        int slots = Device.frameCount;
        int current = Device.FrameIndex % Device.frameCount;
        int previous = (Device.FrameIndex + Device.frameCount - 1) % Device.frameCount;

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
    /// Rebuilds the frame-indexed view over the palette ring, or clears it after a fallback. Unlike Vulkan there is nothing to
    /// republish: the draw reads this property and binds the buffer directly, so replacing an entry is the whole update. The slot
    /// count always matches the frame ring, because the shared target is given this backend's depth.
    /// </summary>
    void SyncBoneStorageRing()
    {
        var ring = _gpuSkin ? _skinning.Palette : null;
        if (ring == null)
        {
            _boneStorageRing = null;
            return;
        }

        int count = ring.Slots.Length;
        if (_boneStorageRing == null || _boneStorageRing.Length != count)
            _boneStorageRing = new IMTLBuffer[count];

        for (int i = 0; i < count; i++)
        {
            // A slot without a storage buffer can only be a ring that failed to allocate, which PreparePalette already rejects;
            // falling back to the mapped ring per slot keeps a broken frame drawable rather than binding null.
            var buffer = ring.Slots[i] is MTLStorageBuffer slot ? slot.Buffer : _bonePaletteBuffers[i];
            if (!ReferenceEquals(_boneStorageRing[i], buffer))
                _boneStorageRing[i] = buffer;
        }
    }

    void ApplyUserTransformToNodeTree(GltfNodeBase nodeBase, Matrix4x4 userTransform)
    {
        // Apply the user transform to the current node.
        var finalWorldMatrix = nodeBase.WorldTransform * userTransform;

        var node = (GLTFNode)nodeBase;

        // Update matrices for all primitives under this node.
        foreach (var primitive in node.Primitives)
        {
            var matrices = new MatrixBuffer
            {
                World = Matrix4x4.Transpose(finalWorldMatrix),
                // Contract clause 6 of 2-3:
                // history always comes from the CPU shadow copy.
                // Transpose of all zeros remains all zeros, so the first frame naturally uses the unwritten sentinel.
                PrevWorld = Matrix4x4.Transpose(primitive.PrevWorldMatrix),
            };
            int fi = Device.FrameIndex;
            WriteStruct(primitive.MatrixBuffers[fi], matrices);

            // Roll the current-frame world matrix into the shadow copy for use as history on the next frame.
            // Each primitive advances exactly once per frame.
            primitive.PrevWorldMatrix = finalWorldMatrix;
        }

        // Important: recurse into child nodes while carrying the same userTransform forward.
        foreach (var child in nodeBase.Children)
        {
            ApplyUserTransformToNodeTree(child, userTransform);
        }
    }

    /// <summary>
    /// Latch of <see cref="Season.Controls.Model.PartColorVersion"/> this group's material UBOs currently hold. Starts at a
    /// value no model can report, so the first Update always syncs - which also bakes in a set that was made between the
    /// load-time ProcessMaterial/ClonePrimitiveData pass and the first frame.
    /// </summary>
    int _appliedPartColorVersion = int.MinValue;

    public void Update(Season.Controls.Model model, float time)
    {
        bool wasInitialized = _transformInitialized;

        // Latch the route before anything reads the tree. A frame that switched halfway would compose half a pose on the CPU and
        // half in the kernel, which renders as a plausible wrong pose rather than as an error.
        var gpuBake = BeginSkinningFrame(model);

        if (gpuBake != null)
        {
            // The kernel samples the bake, so no curve is evaluated and no node transform is touched: the clock and one 16-byte
            // parameter row are the whole per-frame cost. WorldTransform stays at rest, which is what the palette is relative to.
            var player = _asset._animationPlayer;
            player.Advance(time);
            int clip = player.CurrentAnimationIndex;
            _skinning.SetInstance(0, (uint)clip, gpuBake.NormalizeTime(clip, player.AnimationTime, player.Loop));

            // Picking cannot read a storage buffer, so it asks for a CPU pose instead; see GltfAsset.EnsureCpuPose.
            _asset.CpuPoseStale = true;
        }
        else
        {
            // Update animation through the animation player, which already handles time advancement,
            // keyframe lookup, TRS interpolation, and node-transform updates internally.
            _asset._animationPlayer.Update(time, _asset.gltfNodes);
        }

        // Morph channels are one of the bake's eligibility rules, so on the GPU route no curve can move a weight and this pass
        // rewrites last frame's values. It is kept regardless because it is also the upload, and a weight set by code rather than
        // by a clip still has to reach the GPU.
        UpdateMorphTargetsRecursive(_asset.gltfNodes);

        // Apply the user transform to root nodes,
        // following the unified positioning contract that converges on BuildWorldMatrix with the anchor pivot described by Mesh3DBase.
        var userTransform = model.BuildWorldMatrix();

        // Find all root nodes and apply the user transform.
        // The player caches root nodes by list reference to avoid O(N^2) lookup every frame.
        var rootNodes = _asset._animationPlayer.GetRootNodes(_asset.gltfNodes);

        foreach (var rootNode in rootNodes)
            ApplyUserTransformToNodeTree(rootNode, userTransform);

        // Update bone matrices. On the GPU route the kernel already produced them and the handle this class hands the group is the
        // ring it wrote, so the mapped ring is not touched at all - which is the saving this route exists for.
        if (gpuBake == null)
            UploadBoneMatricesToGpu();

        // Point the frame-indexed view at the ring, or back at the mapped UBOs after a fallback. There is no binding table to
        // rewrite on this backend; the property above is read per draw.
        SyncBoneStorageRing();

        // From the second GPU frame onward the ring's previous slot holds a real palette, so the motion-vector path may read it.
        // The CPU route publishes the same bit from its own warmup counter inside UploadBoneMatricesToGpu.
        if (gpuBake != null && wasInitialized && !_hasPrevBonesPublished)
        {
            PublishHasPrevBones();
            _hasPrevBonesPublished = true;
        }

        _transformInitialized = true;

        // Sync Model.Alpha into every primitive material buffer.
        // The base class writes only when the value actually changes.
        SyncAlpha(model.Alpha);

        // Per-part base colors follow the alpha sync: the two write different channels of the same UBO (RGB vs W), and the
        // sync is version-gated, so this costs one compare on every frame that did not change the part-color set.
        SyncPartColors(model);

        // Unified highlighting:
        // sync the wireframe flag, which can be toggled at runtime,
        // and lazily build per-primitive shell geometry.
        // The shell is created on the first enabled frame and remains resident afterward.
        // When fully disabled it incurs no memory and no draw cost.
        // Each frame writes node WorldTransform multiplied by the user transform plus face and edge colors into each shell box.
        // This stays aligned with ApplyUserTransformToNodeTree rendering,
        // because shell vertices live in node-local space and missing node matrices would shift or scale them incorrectly.
        // Face alpha is also written every frame because it may pulse over time.
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

        // Unified highlighting for the Bounds box:
        // lazily build box geometry on the first enabled frame.
        // Face and edge colors stay independent from the model alpha chain and are written every frame.
        // Do not light the box when extents are near zero, such as unloaded or degenerate bounds.
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

        // Unified highlighting:
        // synchronize Outline2D state.
        // Activation is collected by the OutlineMask pass in Graphics, mirroring DXModel and VKModel.
        SetOutline2DState(model.Highlight.Outline, model.Highlight.OutlineColor, model.Highlight.OutlineWidth);
    }

    /// <summary>Called by the base-class Draw to recursively traverse _asset.gltfNodes and append each GLTFNode's primitives into result.</summary>
    protected override void CollectPrimitives(List<PrimitiveData> result)
    {
        CollectPrimitivesRecursive(_asset.gltfNodes, result);
    }

    /// <summary>Public version used by external callers such as InstancedModel to collect all primitives.</summary>
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

    public override void Dispose()
    {
        // Leave the scheduler before the buffers it points at go away, and release the compute-side parameter and scratch blocks
        // the target owns. The palette ring is this class's own; the frame-indexed view is only a view.
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

        // Unified highlighting: release the highlight pool for the host Bounds box.
        DisposeHighlights();

        if (_bonePaletteBuffers != null)
        {
            for (int i = 0; i < _bonePaletteBuffers.Length; i++)
                _bonePaletteBuffers[i]?.Dispose();
            _bonePaletteBuffers = null!;
        }
    }
}
