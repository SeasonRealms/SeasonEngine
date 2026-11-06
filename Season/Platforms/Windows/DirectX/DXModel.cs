// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Season.Models;
using SharpGLTF.Runtime;
using SharpGLTF.Schema2;
using Silk.NET.Direct3D12;

namespace Season.Platforms.Windows.DirectX;

// Extension of a glTF node for the Windows DX12 backend, used to store the
// corresponding PrimitiveData list
internal class GLTFNode : GltfNodeBase
{
    public List<PrimitiveData> Primitives = new List<PrimitiveData>();
}

/// <summary>
/// DX12 backend for glTF models. Inherits DXPrimitiveGroup to reuse
/// Matrix/Material CB creation, SyncAlpha, and the three-bucket draw flow, and
/// composes GltfAsset for node-tree / animation / skin loading.
/// This type only carries glTF-specific pieces: the instance bone palette
/// (t6/t8 StructuredBuffers), animation ticking, recursive primitive
/// collection through the node tree, and ProcessMaterial for the five PBR textures.
/// </summary>
internal unsafe class DXModel : DXPrimitiveGroup
{
    // Reuse GltfAsset loading and playback for nodes / animations / skins
    // through composition, so single inheritance does not get consumed by GltfAsset.
    internal readonly GltfAsset _asset = new GltfAsset();

    // Instance bone palette (t6/t8 StructuredBuffers)
    private int _bonePaletteStride = 1;
    private ID3D12Resource* _bonePaletteBuffer;
    private byte* _mappedBonePaletteBuffer;
    private int _bonePaletteDescriptorId = -1;
    private GpuDescriptorHandle _bonePaletteSrvHandle;

    // 2-3 Step C (tier B): previous bone-palette SB
    // (same capacity as _bonePaletteBuffer, one Matrix4x4 per entry).
    // Before each frame upload, memcpy the current mapped region into the
    // previous mapped region so the GPU always holds the previous frame's bone
    // palette.
    // Contents are zeroed before the first frame (sentinel _m33==0), so the
    // shader falls back to current bones joint by joint.
    private ID3D12Resource* _prevBonePaletteBuffer;
    private byte* _mappedPrevBonePaletteBuffer;
    private int _prevBonePaletteDescriptorId = -1;
    private GpuDescriptorHandle _prevBonePaletteSrvHandle;

    // --- single-model GPU skinning (Model.SkinningMode = Gpu) ---
    //
    // P1's kernel, parameter block and frame latch are reused verbatim, filled with exactly one row. What a single model needs
    // that the instanced path does not is a palette the CPU never touches: the kernel writes a storage buffer, so the mapped
    // upload ring both routes used before cannot carry it, and the previous-frame copy stops being a memcpy and becomes the
    // ring's other slot.

    /// <summary>The shared target implementation, one instance wide. Registered at load, unregistered by Dispose.</summary>
    readonly Season.Rendering.InstanceSkinningTarget _skinning = new();

    /// <summary>True while this frame draws from the kernel's palette. Latched once per Update so a frame cannot half-switch
    /// between the two palettes, which would compose half a pose from one source and half from the other.</summary>
    bool _gpuSkin;

    /// <summary>The palette ring this class creates because the GPU route needs a compute-written palette. Owned here, disposed here.</summary>
    Season.Rendering.InstanceBoneStorage? _gpuPalette;

    /// <summary>The rest pose, shared with the other three backends so a route whose correctness rests on two matrices cancelling
    /// has one implementation of the snapshot rather than four.</summary>
    readonly Season.Models.RestPoseSnapshot _restPose = new();

    /// <summary>Rest world of every node, indexed exactly like <c>_asset.gltfNodes</c>. The GPU route's palette carries
    /// inverse(rest world), so the per-primitive factor the vertex stage multiplies by has to be that same rest world; anything
    /// evaluated on the CPU in the meantime (picking) is undone from this snapshot.</summary>
    System.Numerics.Matrix4x4[] _restWorld => _restPose.Worlds;

    /// <summary>Set once a bake has been asked for and refused. The retry in <see cref="BeginSkinningFrame"/> exists so the mode
    /// can be turned on after load, which is why it cannot simply run at load; but a refusal is not the same answer as "not yet",
    /// and treating them alike would rescan the whole clip set every frame for the life of a model the bake declines - a channel
    /// on a non-joint, a morph weight track, no animations at all. A refusal is permanent: it comes from the asset's shape, which
    /// nothing changes after load.</summary>
    bool _bakeRefused;

    /// <summary>Diagnostic only (<c>SEASON_AB_SKIN_PALETTE=storage</c>): the second half of the palette control. <c>cpu</c> swaps the
    /// palette source but keeps the CPU upload ring as the binding, so it proves the route's plumbing is clean; this one keeps the
    /// CPU palette and swaps the *binding* to the compute-written storage ring instead. A frame that reproduces the CPU route under
    /// this switch says the storage view and its handoff are byte-equivalent to the upload ring, which leaves the kernel's output as
    /// the only remaining suspect. Never set outside a comparison run.</summary>
    readonly bool _paletteFromStorage = PaletteSourceIs("storage");

    /// <summary>Diagnostic only (<c>SEASON_AB_SKIN_PALETTE=cpu</c>): keep the GPU route's schedule intact - the bake, the
    /// parameter row, the dispatch, the handoff - and draw with a palette recomputed on the CPU from the very clip and time
    /// the kernel was handed. The two halves of this route fail differently: a wrong table or a wrong parameter renders as a
    /// limb in the wrong place, while a palette the vertex stage cannot actually see renders as the previous pose or as
    /// nothing at all. Swapping only the palette source tells those apart, because everything else stays put. Never set
    /// outside a comparison run: on this path the CPU does the work twice and the kernel's output goes unread.</summary>
    readonly bool _paletteFromCpu = PaletteSourceIs("cpu") || PaletteSourceIs("storage");

    /// <summary>Whether the palette diagnostic names <paramref name="source"/>. Static so the two latches above can each be a field
    /// initializer; comparing against a word rather than a flag is what let an earlier round read "1" as a run that had never
    /// entered the branch it was meant to select.</summary>
    static bool PaletteSourceIs(string source) => string.Equals(
        Environment.GetEnvironmentVariable("SEASON_AB_SKIN_PALETTE"), source, StringComparison.OrdinalIgnoreCase);

#if DEBUG
    /// <summary>One-shot latch for the handoff line below, so a 7000-frame run logs the parameters once instead of per frame.</summary>
    bool _gpuSkinLogged;

    /// <summary>Frame the storage probe last printed on, so the per-primitive SRV read logs once per sampled frame.</summary>
    uint _storageProbeFrame = uint.MaxValue;

    /// <summary>Same latch for the publish-side probe, kept separate so the two cannot swallow each other's line.</summary>
    uint _publishProbeFrame = uint.MaxValue;

    /// <summary>One-shot latch for the rest-versus-posed measurement.</summary>
    bool _restCheckLogged;
#endif

    // 2-3 Step C (tier C-b completion): previous morph-weights SB
    // (1 float4 = 16 bytes).
    // In the non-instanced path, the shader reads g_PrevMorphWeights[0], so only
    // one element is required.
    // Each frame, ApplyMorphTargetsIfNeeded copies the old weights into this SB
    // before writing new ones, letting the shader reconstruct the previous local
    // position for morph velocity.
    private ID3D12Resource* _prevMorphWeightsBuffer;
    private float* _mappedPrevMorphWeightsBuffer;
    private int _prevMorphWeightsDescriptorId = -1;
    private GpuDescriptorHandle _prevMorphWeightsSrvHandle;

    public DXModel(string name)
    {
        Name = name;
        // Inject two glTF-specific hooks: node factory + primitive processing
        _asset.CreateGLTFNodeCallback = CreateGLTFNode;
        _asset.ProcessPrimitiveCallback = ProcessPrimitive;
    }

    public void Load(Season.Controls.Model model)
    {
        // Animation query / switching belongs to the glTF parsing domain and
        // does not go through IGraphics. Inject the asset reference into the
        // control on the direct-load path.
        model.Asset = _asset;

        _asset.Load(model);
        _asset.PlayAnimation();

        InitializeBonePaletteResources();

        _asset.ValidateSkinData();

        // P1: on the direct-load path the asset belongs to this model, so the bake is built here. The shared-template path builds
        // it in CreateInstance instead, where the requesting control's mode is first known. Either way it runs before anything
        // disturbs the rest pose, so the snapshot taken right after it is the world the bake used for its inverse-mesh factor.
        InstanceSkinBake.TryBake(_asset, model.SkinningMode);
        CaptureRestWorld();
        Season.Rendering.InstanceSkinning.Register(_skinning);
    }

    public DXModel CreateInstance(Season.Controls.Model model)
    {
        if (_asset.Model != null)
        {
            model.Size = _asset.Model.Size;
            model.OriginalScale = _asset.Model.OriginalScale;
            // 1-3: on the shared-template path, GltfAsset.Load only fills the
            // temporary template Model's LocalBounds. We must copy it back to the
            // user control, or control-level culling never activates because the
            // empty-box guard always wins.
            model.LocalBounds = _asset.Model.LocalBounds;
            // Unified transform convention: likewise copy back the raw bounds
            // box. Its setter triggers OnBoundsEstablished, which finalizes the
            // default size, so this must run after Size / OriginalScale.
            model.LocalBoundsRaw = _asset.Model.LocalBoundsRaw;
            // 1-2: likewise copy back KHR imported punctual lights.
            // They are local-space read-only data, so sharing references is
            // sufficient. Otherwise AppendWorldLights sees an empty list on the
            // shared-template path.
            model.ImportedPunctualLights = _asset.Model.ImportedPunctualLights;
        }

        var instance = new DXModel(Name);
        instance._transformInitialized = false;
        instance._asset.Model = model;
        instance._asset._nodeTransforms = new Dictionary<GltfNodeBase, Matrix4x4>();

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
        instance._asset.PlayAnimation();
        // Shared-template instancing path: inject the instance asset into the
        // control, matching the semantics of the direct-load Load path.
        model.Asset = instance._asset;
        instance.InitializeBonePaletteResources();

        // P1: the three bake tables are a function of the rest pose and the curves, both of which are identical here and on the
        // template, so the template builds them once and every clone references that one set. Without this a scene holding fifty
        // copies of one character would carry fifty identical copies of the tracks - the same argument that keeps the kernel's
        // output buffers shared through EnsureUploaded rather than allocated per instance.
        InstanceSkinBake.TryBake(_asset, model.SkinningMode);
        instance._asset.SkinBake = _asset.SkinBake;
        instance.CaptureRestWorld();
        Season.Rendering.InstanceSkinning.Register(instance._skinning);

        return instance;
    }

    GltfNodeBase EnsureClonedNode(Dictionary<GltfNodeBase, GltfNodeBase> nodeMap, GltfNodeBase sourceNode, Season.Controls.Model model)
    {
        if (nodeMap.TryGetValue(sourceNode, out var existing))
            return existing;

        GltfNodeBase clonedNode;
        if (sourceNode is GLTFNode sourceDxNode)
        {
            var dxNode = new GLTFNode
            {
                Name = sourceDxNode.Name,
                LogicalIndex = sourceDxNode.LogicalIndex,
                Mesh = sourceDxNode.Mesh,
                IsJoint = sourceDxNode.IsJoint,
                JointIndex = sourceDxNode.JointIndex,
                Translation = sourceDxNode.InitialTranslation,
                Rotation = sourceDxNode.InitialRotation,
                Scale = sourceDxNode.InitialScale,
                InitialTranslation = sourceDxNode.InitialTranslation,
                InitialRotation = sourceDxNode.InitialRotation,
                InitialScale = sourceDxNode.InitialScale,
                InitialWeights = sourceDxNode.InitialWeights.Length == 0 ? Array.Empty<float>() : (float[])sourceDxNode.InitialWeights.Clone(),
                Weights = sourceDxNode.Weights.Length == 0 ? Array.Empty<float>() : (float[])sourceDxNode.Weights.Clone(),
                WeightsVersion = sourceDxNode.WeightsVersion,
                WorldTransform = sourceDxNode.WorldTransform,
                // v2 picking: PickMesh is immutable after loading, so it is
                // shared by reference with no deep copy. NodeIndex stays aligned.
                PickMeshes = sourceDxNode.PickMeshes,
            };

            foreach (var primitive in sourceDxNode.Primitives)
                dxNode.Primitives.Add(ClonePrimitiveData(primitive, dxNode, model));

            clonedNode = dxNode;
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
                // v2 picking: PickMesh is immutable after loading, so it is
                // shared by reference with no deep copy. NodeIndex stays aligned.
                PickMeshes = sourceNode.PickMeshes,
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
            // Phase 3: GPU Morph Target - use base vertices directly together
            // with the shared morph-delta buffer
            clone.Vertices = new List<Vertex>(clone.BaseVertices);
            clone.MorphDeltasBuffer = source.MorphDeltasBuffer;
            clone.MorphDeltasSrvHandle = source.MorphDeltasSrvHandle;
            clone.MorphDescriptorId = -1; // Not owned here; follows the source lifetime
            clone.MaterialParams.HasMorphTargets = 1;
            clone.MaterialParams.MorphTargetCount = (uint)clone.MorphTargets.Count;
            clone.MaterialParams.MorphVertexCount = (uint)clone.BaseVertices.Length;
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

        clone.VertexBuffer = Device.CreateVertexBuffer(clone.Vertices.ToArray(), out clone.VertexBufferView);
        clone.IndexBuffer = Device.CreateIndexBuffer(clone.Indices, out clone.IndexBufferView);

        // Unified highlighting: shell geometry is not built here. It is created
        // lazily by the base class on the first frame that enables wireframe
        // (see EnsureWireframeHighlights).
        // Cloned primitives are collected through CollectPrimitives and own their
        // resources in the same way as VB/IB, without sharing source pointers, so
        // Dispose cannot double-release them.

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

        return clone;
    }

    static void ApplyInstanceMaterialOverrides(PrimitiveData primitiveData, Season.Controls.Model model)
    {
        var colorTint = model.MaterialColor ?? Vector4.One;
        primitiveData.MaterialParams.BaseColor *= colorTint;
        primitiveData.MaterialParams.RenderMode = model.Unlit ? 0u : 1u;
        primitiveData.MaterialParams.IsSkinned = primitiveData.OwnerNode?.Skin != null ? 1u : 0u;
        primitiveData.MaterialParams.BonePaletteStride = 1;
        primitiveData.OriginalBaseColorAlpha = primitiveData.MaterialParams.BaseColor.W;
    }

    /// <summary>
    /// Replaces one primitive's RGB with the override the model holds for its part, if any. Absolute rather than
    /// multiplicative - see <see cref="Season.Controls.Model.SetPartColor"/> for why a tint cannot recolor a saturated
    /// material. W is left untouched because the alpha chain belongs to SyncAlpha.
    /// </summary>
    static void ApplyPartColorOverride(PrimitiveData primitiveData, Season.Controls.Model model)
    {
        if (!model.TryGetPartColor(primitiveData.PartName, primitiveData.PartNodeName, out var color))
            return;

        var baseColor = primitiveData.MaterialParams.BaseColor;
        primitiveData.MaterialParams.BaseColor = new Vector4(color.X, color.Y, color.Z, baseColor.W);
    }

    /// <summary>
    /// Pushes <c>Model.SetPartColor</c> into every primitive's material CB: RGB becomes the override, or the load-time
    /// baseline when the override was cleared. Gated on <see cref="Season.Controls.Model.PartColorVersion"/>, so a steady
    /// frame pays one integer compare; when it does move, all N-buffered frames are written, the same stale-frame rule
    /// SyncAlpha follows. The canonical <c>MaterialParams</c> struct is updated too, so anything that rewrites the CBs from
    /// it later (the skinning sync, a clone) sees the same colors this frame drew.
    /// </summary>
    void SyncPartColors(Season.Controls.Model model)
    {
        if (_appliedPartColorVersion == model.PartColorVersion)
            return;
        _appliedPartColorVersion = model.PartColorVersion;

        int n = (int)Device.frameCount;
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
                var materialParams = Unsafe.Read<MaterialParams>(primitive.MappedMaterialBuffers[i]);
                materialParams.BaseColor = new Vector4(rgb.X, rgb.Y, rgb.Z, materialParams.BaseColor.W);
                Unsafe.Write(primitive.MappedMaterialBuffers[i], materialParams);
            }
        }
    }

    internal static List<GLTFAnimation> CloneAnimations(List<GLTFAnimation> sourceAnimations, Dictionary<GltfNodeBase, GltfNodeBase> nodeMap)
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

    GltfNodeBase CreateGLTFNode(SharpGLTF.Schema2.Node node)
    {
        var gltfNode = new GLTFNode
        {
            Name = node.Name ?? $"Node_{node.LogicalIndex}",
            LogicalIndex = node.LogicalIndex,
            Mesh = node.Mesh,
            Skin = node.Skin != null ? _asset.CreateSkin(node.Skin) : null,
            IsJoint = node.IsSkinJoint,
            JointIndex = node.IsSkinJoint ? _asset.GetJointIndex(node) : -1
        };

        return gltfNode;
    }

    void InitializeBonePaletteResources()
    {
        _bonePaletteStride = Math.Max(1, _asset.GetAllSkins().Sum(static skin => skin.Joints.Count));

        ulong bufferSize = (ulong)(_bonePaletteStride * sizeof(Matrix4x4));
        _bonePaletteBuffer = Device.ResourceManager.CreateBuffer(
            HeapType.Upload, bufferSize, ResourceStates.GenericRead);

        void* pData;
        _bonePaletteBuffer->Map(0, null, &pData);
        _mappedBonePaletteBuffer = (byte*)pData;

        _bonePaletteDescriptorId = Device.DescriptorAllocator.Allocate(DescriptorKind.ModelData);
        var cpuHandle = Device.SrvHeapManager.GetCpuHandle(_bonePaletteDescriptorId);
        var srvDesc = new ShaderResourceViewDesc
        {
            Format = Silk.NET.DXGI.Format.FormatUnknown,
            ViewDimension = SrvDimension.Buffer,
            Shader4ComponentMapping = 0x00001688u,
            Buffer = new BufferSrv
            {
                FirstElement = 0,
                NumElements = (uint)_bonePaletteStride,
                StructureByteStride = (uint)sizeof(Matrix4x4),
                Flags = BufferSrvFlags.None
            }
        };
        Device.D3dDevice->CreateShaderResourceView(_bonePaletteBuffer, &srvDesc, cpuHandle);
        _bonePaletteSrvHandle = Device.SrvHeapManager.GetGpuHandle(_bonePaletteDescriptorId);

        // 2-3 Step C (tier B): create the previous bone-palette SB as well
        // (same capacity as current, cleared on first creation)
        _prevBonePaletteBuffer = Device.ResourceManager.CreateBuffer(
            HeapType.Upload, bufferSize, ResourceStates.GenericRead);
        void* pPrevData;
        _prevBonePaletteBuffer->Map(0, null, &pPrevData);
        _mappedPrevBonePaletteBuffer = (byte*)pPrevData;
        new Span<byte>(_mappedPrevBonePaletteBuffer, (int)bufferSize).Clear();

        _prevBonePaletteDescriptorId = Device.DescriptorAllocator.Allocate(DescriptorKind.ModelData);
        var prevCpuHandle = Device.SrvHeapManager.GetCpuHandle(_prevBonePaletteDescriptorId);
        var prevSrvDesc = new ShaderResourceViewDesc
        {
            Format = Silk.NET.DXGI.Format.FormatUnknown,
            ViewDimension = SrvDimension.Buffer,
            Shader4ComponentMapping = 0x00001688u,
            Buffer = new BufferSrv
            {
                FirstElement = 0,
                NumElements = (uint)_bonePaletteStride,
                StructureByteStride = (uint)sizeof(Matrix4x4),
                Flags = BufferSrvFlags.None
            }
        };
        Device.D3dDevice->CreateShaderResourceView(_prevBonePaletteBuffer, &prevSrvDesc, prevCpuHandle);
        _prevBonePaletteSrvHandle = Device.SrvHeapManager.GetGpuHandle(_prevBonePaletteDescriptorId);

        SyncSkinningMaterialParams();
    }

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

    void ProcessPrimitive(MeshPrimitive meshPrimitive, GltfNodeBase node, Season.Controls.Model model)
    {
        var primitiveData = CreatePrimitiveData(meshPrimitive, node, model);

        var gltfNode = node as GLTFNode;

        gltfNode.Primitives.Add(primitiveData);
    }

    PrimitiveData CreatePrimitiveData(MeshPrimitive primitive, GltfNodeBase node, Season.Controls.Model model)
    {
        var primitiveData = new PrimitiveData();

        // Load geometry data
        var verticleIndices = GLTFTools.LoadMeshPrimitive(primitive);
        var morphTargets = GLTFTools.LoadMorphTargets(primitive, verticleIndices.vertices.Count);
        primitiveData.OwnerNode = node;
        primitiveData.LastAppliedWeightsVersion = node.WeightsVersion;
        if (morphTargets.Count > 0)
        {
            primitiveData.BaseVertices = verticleIndices.vertices.ToArray();
            primitiveData.MorphTargets = morphTargets;
            // GPU Morph Target: use base vertices directly (rest pose), and
            // blend deltas in the GPU shader
            primitiveData.Vertices = new List<Vertex>(primitiveData.BaseVertices);
            // Create the morph-delta StructuredBuffer and fill its SRV.
            // The related flags are set after ProcessMaterial.
            CreateMorphDeltaBuffer(primitiveData, primitiveData.BaseVertices, morphTargets);
        }
        else
        {
            primitiveData.Vertices = verticleIndices.vertices;
        }

        primitiveData.Indices = verticleIndices.indices.ToArray();
        primitiveData.Use32BitIndices = primitiveData.Indices.Any(i => i > ushort.MaxValue);
        var localBounds = Season.Rendering.Bounds3D.FromVertices(primitiveData.Vertices);
        primitiveData.LocalBoundsCenter = localBounds.Center;
        primitiveData.LocalBoundsExtents = localBounds.Extents;

        // Create GPU resources
        primitiveData.VertexBuffer = Device.CreateVertexBuffer(primitiveData.Vertices.ToArray(), out primitiveData.VertexBufferView);

        primitiveData.IndexBuffer = Device.CreateIndexBuffer(primitiveData.Indices, out primitiveData.IndexBufferView);

        // Constant buffers: reuse the base-class creation logic
        CreateMatrixBuffer(primitiveData);
        CreateMaterialBuffer(primitiveData);

        // Process materials and textures
        ProcessMaterial(primitive, primitiveData);

        // Phase 3: ProcessMaterial creates a new MaterialParams instance, so set
        // morph flags only afterward and write them back to every frame.
        if (primitiveData.MorphTargets != null && primitiveData.MorphTargets.Count > 0)
        {
            primitiveData.MaterialParams.HasMorphTargets = 1;
            primitiveData.MaterialParams.MorphTargetCount = (uint)primitiveData.MorphTargets.Count;
            primitiveData.MaterialParams.MorphVertexCount = (uint)primitiveData.BaseVertices!.Length;
            for (int i = 0; i < Device.frameCount; i++)
                Unsafe.Write(primitiveData.MappedMaterialBuffers[i], primitiveData.MaterialParams);
        }

        // Initialize matrix buffers with the identity matrix for all frames so
        // other N-buffered frames never read garbage values.
        var matrices = new MatrixBuffer
        {
            World = System.Numerics.Matrix4x4.Transpose(System.Numerics.Matrix4x4.Identity),
        };

        for (int i = 0; i < Device.frameCount; i++)
            Unsafe.Write(primitiveData.MappedMatrixBuffers[i], matrices);

        return primitiveData;
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

    void ApplyMorphTargetsIfNeeded(PrimitiveData primitiveData, GltfNodeBase node)
    {
        if (primitiveData.BaseVertices == null || primitiveData.MorphTargets == null || primitiveData.MorphTargets.Count == 0)
            return;

        if (primitiveData.LastAppliedWeightsVersion == node.WeightsVersion)
            return;

        // 2-3 Step C (tier C-b completion): copy current weights into the
        // previous morph SB before writing new weights.
        // At this point the CB still contains the old weights from the previous
        // write, which is exactly the "previous frame" data the shader needs.
        // On the first frame the prev SB is all zero (sentinel), so the shader
        // falls back to current data and morph contributes no velocity.
        CaptureCurrentMorphWeightsToPrev(primitiveData);

        // Phase 3: GPU Morph Target - write weights into the MaterialParams CB
        WriteMorphWeightsToCB(primitiveData, node.Weights);
        primitiveData.LastAppliedWeightsVersion = node.WeightsVersion;

        // Morph shell boxes: shell deltas are expanded to the shell-vertex
        // layout, while weights are shared with the source. Synchronize the same
        // weight set into both shell-primitive Material CBs so shell and source
        // share animation weights and stay aligned through the same VS morph
        // path frame by frame (see DXPrimitiveGroup.AttachShellMorph).
        // Shell boxes exist only when the source has morph targets, and their
        // dirty gating follows the source.
        if (_wireframeBoxes != null)
        {
            for (int i = 0; i < _wireframeBoxes.Count; i++)
            {
                var shell = _wireframeBoxes[i];
                if (shell != null && shell.SourcePrimitive == primitiveData)
                {
                    WriteMorphWeightsToCB(shell.Face, node.Weights);
                    WriteMorphWeightsToCB(shell.Edges, node.Weights);
                }
            }
        }
    }

    /// <summary>2-3 Step C (tier C-b completion): reads the current morph
    /// weights from the N-buffered CB and writes them into the previous SB.
    /// Lazily initializes the previous morph SB on first use.</summary>
    void CaptureCurrentMorphWeightsToPrev(PrimitiveData primitiveData)
    {
        // Lazily initialize the previous morph-weights SB
        // (1 float4 = 16 bytes)
        if (_prevMorphWeightsBuffer == null)
        {
            _prevMorphWeightsBuffer = Device.ResourceManager.CreateBuffer(
                HeapType.Upload, 16, ResourceStates.GenericRead);
            void* pData;
            _prevMorphWeightsBuffer->Map(0, null, &pData);
            _mappedPrevMorphWeightsBuffer = (float*)pData;
            new Span<byte>(pData, 16).Clear();

            _prevMorphWeightsDescriptorId = Device.DescriptorAllocator.Allocate(DescriptorKind.ModelData);
            var cpuHandle = Device.SrvHeapManager.GetCpuHandle(_prevMorphWeightsDescriptorId);
            var srvDesc = new ShaderResourceViewDesc
            {
                Format = Silk.NET.DXGI.Format.FormatUnknown,
                ViewDimension = SrvDimension.Buffer,
                Shader4ComponentMapping = 0x00001688u,
                Buffer = new BufferSrv
                {
                    FirstElement = 0,
                    NumElements = 1,
                    StructureByteStride = 16,
                    Flags = BufferSrvFlags.None
                }
            };
            Device.D3dDevice->CreateShaderResourceView(_prevMorphWeightsBuffer, &srvDesc, cpuHandle);
            _prevMorphWeightsSrvHandle = Device.SrvHeapManager.GetGpuHandle(_prevMorphWeightsDescriptorId);
        }

        // Read current weights from this frame's CB.
        // WriteMorphWeightsToCB has not been called yet, so these are still the
        // old weights.
        int fi = (int)Device.FrameIndex;
        var mp = Unsafe.Read<MaterialParams>(primitiveData.MappedMaterialBuffers[fi]);
        _mappedPrevMorphWeightsBuffer[0] = mp.MorphWeights.X;
        _mappedPrevMorphWeightsBuffer[1] = mp.MorphWeights.Y;
        _mappedPrevMorphWeightsBuffer[2] = mp.MorphWeights.Z;
        _mappedPrevMorphWeightsBuffer[3] = mp.MorphWeights.W;
    }

    /// <summary>
    /// Phase 3: writes morph weights into the N-buffered MaterialParams CB.
    /// Supports up to 4 morph targets; extra weights are ignored.
    /// </summary>
    static void WriteMorphWeightsToCB(PrimitiveData primitiveData, float[] weights)
    {
        int n = (int)Device.frameCount;
        for (int i = 0; i < n; i++)
        {
            var mp = Unsafe.Read<MaterialParams>(primitiveData.MappedMaterialBuffers[i]);
            mp.MorphWeights = new Vector4(
                weights.Length > 0 ? weights[0] : 0,
                weights.Length > 1 ? weights[1] : 0,
                weights.Length > 2 ? weights[2] : 0,
                weights.Length > 3 ? weights[3] : 0);
            Unsafe.Write(primitiveData.MappedMaterialBuffers[i], mp);
        }
    }

    // 1-3: bounds calculation is centralized in the shared
    // Season.Rendering.Bounds3D.FromVertices helper for all four backends

    void ProcessMaterial(MeshPrimitive primitive, PrimitiveData primitiveData)
    {
        var modelRoot = primitive.LogicalParent.LogicalParent;
        var (gLTFMaterial1, images) = GLTFTools.LoadMaterial(modelRoot, primitive);

        // Part identity: the material name is the primary key Model.SetPartColor resolves against, the owning node name the
        // fallback for material-less primitives. Recorded before the colors below so the override can use it in this same call.
        primitiveData.PartName = primitive.Material?.Name;
        primitiveData.PartNodeName = primitiveData.OwnerNode?.Name;

        primitiveData.MaterialParams = new MaterialParams { RenderMode = _asset.Model.Unlit ? 0u : 1u };   // 0: Unlit, 1: Pbr3D

        // Configure transparency parameters from AlphaMode.
        // Only BLEND is truly transparent and requires blending.
        // MASK uses the Opaque PSO plus clip().
        if (gLTFMaterial1 != null)
        {
            primitiveData.IsTransparent = gLTFMaterial1.AlphaMode == "BLEND";
            primitiveData.DoubleSided = gLTFMaterial1.DoubleSided;

            // Set AlphaMode: 0=OPAQUE, 1=MASK, 2=BLEND
            primitiveData.MaterialParams.AlphaMode = gLTFMaterial1.AlphaMode switch
            {
                "MASK" => 1u,
                "BLEND" => 2u,
                _ => 0u  // OPAQUE
            };

            // Set AlphaCutoff (used by MASK mode, default 0.5)
            primitiveData.MaterialParams.AlphaCutoff = gLTFMaterial1.AlphaCutoff;
        }
        else
        {
            primitiveData.IsTransparent = false;
            primitiveData.DoubleSided = false;
            primitiveData.MaterialParams.AlphaMode = 0u;     // OPAQUE
            primitiveData.MaterialParams.AlphaCutoff = 0.5f; // Default value
        }

        var colorTint = _asset.Model.MaterialColor ?? new System.Numerics.Vector4(1.0f, 1.0f, 1.0f, 1.0f);
        primitiveData.MaterialParams.BaseColor = colorTint;

        if (gLTFMaterial1 != null)
        {
            // Multiply the caller-side MaterialColor in as tint together with
            // the glTF BaseColorFactor so glTF default white / material colors do
            // not override the intended tint when no base-color texture exists.
            primitiveData.MaterialParams.BaseColor *= gLTFMaterial1.BaseColorFactor;

            // The two scalar factors are written unconditionally: glTF defines the effective value as
            // factor * texture channel, so the shader needs them even when a metallic-roughness map exists.
            // They used to be written only in the "map missing" branches (and MetallicFactor was keyed off the
            // normal map by mistake), which left every textured material multiplying against a stale zero.
            primitiveData.MaterialParams.MetallicFactor = gLTFMaterial1.MetallicFactor;
            primitiveData.MaterialParams.RoughnessFactor = gLTFMaterial1.RoughnessFactor;
        }
        else
        {
            // No glTF material at all: fall back to the engine's documented neutral dielectric rather than
            // the all-zero struct default, whose roughness 0 would read as a perfect mirror.
            primitiveData.MaterialParams.MetallicFactor = 0.0f;
            primitiveData.MaterialParams.RoughnessFactor = 0.5f;
        }

        if (images.Count == 0)
        {

        }
        else
        {
            var baseColorImage = images[0];
            if (baseColorImage is null)
            {
                primitiveData.MaterialParams.UseAlbedoMap = 0u;
            }
            else
            {
                var dXTexture = DXTexture.GetOrCreate($"{_asset.Model.Name}-baseColor-{baseColorImage.LogicalIndex}", baseColorImage, TextureMipPolicy.Color);

                primitiveData.BaseColorTexture = dXTexture;
                primitiveData.MaterialParams.UseAlbedoMap = 1u;
            }

            var normalImage = images[1];
            if (normalImage is null)
            {
                primitiveData.MaterialParams.UseNormalMap = 0u;
            }
            else
            {
                var dXTexture = DXTexture.GetOrCreate($"{_asset.Model.Name}-normal-{normalImage.LogicalIndex}", normalImage, TextureMipPolicy.Normal);

                primitiveData.NormalTexture = dXTexture;
                primitiveData.MaterialParams.UseNormalMap = 1u;
            }

            var metallicRoughnessImage = images[2];
            if (metallicRoughnessImage is null)
            {
                primitiveData.MaterialParams.UseMetallicRoughnessMap = 0u;
            }
            else
            {
                var dXTexture = DXTexture.GetOrCreate($"{_asset.Model.Name}-metallicRoughness-{metallicRoughnessImage.LogicalIndex}", metallicRoughnessImage, TextureMipPolicy.Linear);

                primitiveData.MetallicRoughnessTexture = dXTexture;
                primitiveData.MaterialParams.UseMetallicRoughnessMap = 1u;
            }

            var occlusionImage = images[3];
            if (occlusionImage is null)
            {
                primitiveData.MaterialParams.UseOcclusionMap = 0u;
            }
            else
            {
                var dXTexture = DXTexture.GetOrCreate($"{_asset.Model.Name}-occlusion-{occlusionImage.LogicalIndex}", occlusionImage, TextureMipPolicy.Linear);

                primitiveData.OcclusionTexture = dXTexture;
                primitiveData.MaterialParams.UseOcclusionMap = 1u;
            }

            var emissiveImage = images[4];
            if (emissiveImage is null)
            {
                primitiveData.MaterialParams.UseEmissiveMap = 0u;
                primitiveData.MaterialParams.EmissiveFactor = gLTFMaterial1.EmissiveFactor.AsVector4();
            }
            else
            {
                var dXTexture = DXTexture.GetOrCreate($"{_asset.Model.Name}-emissive-{emissiveImage.LogicalIndex}", emissiveImage, TextureMipPolicy.Color);

                primitiveData.EmissiveTexture = dXTexture;
                primitiveData.MaterialParams.UseEmissiveMap = 1u;
                // Even with a texture present, a default emissiveFactor can still
                // be used as the base value.
                primitiveData.MaterialParams.EmissiveFactor = gLTFMaterial1.EmissiveFactor.AsVector4();
            }
        }

        if (primitiveData.BaseColorTexture is null)
        {
            primitiveData.BaseColorTexture = Device.White;
        }

        if (primitiveData.NormalTexture is null)
        {
            primitiveData.NormalTexture = Device.White;
        }

        if (primitiveData.MetallicRoughnessTexture is null)
        {
            primitiveData.MetallicRoughnessTexture = Device.White;
        }

        if (primitiveData.OcclusionTexture is null)
        {
            primitiveData.OcclusionTexture = Device.White;
        }

        if (primitiveData.EmissiveTexture is null)
        {
            primitiveData.EmissiveTexture = Device.White;
        }

        // Record the original glTF BaseColor.W so later Model.Alpha
        // multiplication stays stable
        primitiveData.OriginalBaseColorAlpha = primitiveData.MaterialParams.BaseColor.W;

        // Part colors: the load-time RGB baseline (with the MaterialColor tint already folded in) is what a cleared
        // override returns to, and a set override replaces the RGB outright. Direct-load path only: the shared-template
        // path bakes the requesting model's overrides in ClonePrimitiveData instead, where they are actually known.
        primitiveData.PartBaseColor = new Vector3(
            primitiveData.MaterialParams.BaseColor.X,
            primitiveData.MaterialParams.BaseColor.Y,
            primitiveData.MaterialParams.BaseColor.Z);
        ApplyPartColorOverride(primitiveData, _asset.Model);

        // Record the original glTF AlphaCutoff so SyncAlpha can scale it in
        // proportion to Model.Alpha and avoid clipping away the entire MASK
        // material at low Model.Alpha.
        primitiveData.OriginalAlphaCutoff = primitiveData.MaterialParams.AlphaCutoff;

        // Initialize the material buffer for every frame so other N-buffered
        // frames never read garbage and cause whole-object flicker.
        for (int i = 0; i < Device.frameCount; i++)
            Unsafe.Write(primitiveData.MappedMaterialBuffers[i], primitiveData.MaterialParams);
    }

    /// <summary>Uploads bone matrices to the GPU.</summary>
    void UploadBoneMatricesToGpu()
    {
        // Use the animation player to compute bone matrices
        _asset._animationPlayer.UpdateBoneMatrices(_asset.GetAllSkins());
        var boneMatrices = _asset._animationPlayer.GetBoneMatricesArray();

        if (boneMatrices.Length > 0 && _mappedBonePaletteBuffer != null)
        {
            int matrixSize = Unsafe.SizeOf<System.Numerics.Matrix4x4>();
            // Clamp to the palette capacity: the upload ring and the palette share one allocation sized by the
            // summed joint count, so a player reporting more matrices would otherwise copy past its end.
            int rows = Math.Min(boneMatrices.Length, _bonePaletteStride);
            int totalSize = matrixSize * rows;

            fixed (void* matricesPtr = boneMatrices)
            {
                // 2-3 Step C (tier B): before uploading this frame, first copy
                // the current bone palette into the mapped previous-SB region.
                // On the first frame, the current side is all zero, so the
                // previous side stays zero and the sentinel semantics remain correct.
                if (_mappedPrevBonePaletteBuffer != null)
                    Unsafe.CopyBlock((void*)_mappedPrevBonePaletteBuffer, (void*)_mappedBonePaletteBuffer, (uint)totalSize);

                Unsafe.CopyBlock(_mappedBonePaletteBuffer, matricesPtr, (uint)totalSize);
            }
        }

    }

    /// <summary>
    /// Diagnostic half of <c>SEASON_AB_SKIN_PALETTE=storage</c>: overwrites the slot the kernel just wrote with the palette the CPU
    /// route uploaded this frame, laid out the way the kernel would have laid it out. Runs from the skinning target between the
    /// dispatch and the handoff, which is the only point in the frame where the command list is open, no Pass is in flight, and the
    /// kernel's own output is already behind us. Everything the vertex stage sees - the resource, its structured view, the handoff -
    /// is then the one the real route uses, with only the bytes changed.
    /// </summary>
    void PublishCpuPalette(IGraphics g)
    {
        var ring = _gpuPalette;
        if (g == null || ring == null || ring.Current == null) return;

        var palette = _asset._animationPlayer.GetBoneMatricesArray();
        int rows = Math.Min(palette.Length, ring.Stride);
        if (rows <= 0) return;

        bool published;
        fixed (System.Numerics.Matrix4x4* source = palette)
            published = ring.Publish(g, new ReadOnlySpan<byte>(source, rows * InstanceBoneStorage.BoneMatrixBytes));

#if DEBUG
        // The control kept landing in one of two states, both stable for thousands of frames and both reachable from the very same
        // bytes, so neither is explained by the palette content. What is left is the draw reading a different resource - in which
        // case these bytes never reach the vertex stage at all, however correct they are. The descriptor identifiers and GPU
        // pointers are what tell the two apart: a zero or a reused slot names the mechanism, a healthy pair sends the search back to
        // the table index the draw sets.
        if (_publishProbeFrame != Device.FrameIndex && (uint)(Device.FrameIndex - _publishProbeFrame) >= 500u)
        {
            _publishProbeFrame = Device.FrameIndex;
            var current = ring.Current as DXStorageBuffer;
            var previous = ring.Previous as DXStorageBuffer;
            ulong currentPtr = current != null ? (ulong)current.VertexSrvGpuHandle.Ptr : 0;
            ulong previousPtr = previous != null ? (ulong)previous.VertexSrvGpuHandle.Ptr : 0;
            var head = palette.Length > 0 ? palette[0] : System.Numerics.Matrix4x4.Identity;
            DeviceServices.BaseApp?.AddLog(LogType.Error,
                $"[StoragePublish] frame={Device.FrameIndex} publish={published} live={_skinning.Live} "
                + $"slots={ring.Slots.Length} cur={ring.CurrentSlot} prev={ring.PreviousSlot} "
                + $"stride={ring.Stride} cap={ring.Capacity} pool={ring.PoolRows} rows={rows} "
                + $"curId={current?.VertexSrvDescriptorID ?? -1} curPtr={currentPtr} "
                + $"prevId={previous?.VertexSrvDescriptorID ?? -1} prevPtr={previousPtr} "
                + $"m0=({head.M11:F4},{head.M12:F4},{head.M42:F4},{head.M43:F4})");
        }
#endif
    }

    void ApplyUserTransformToNodeTree(GltfNodeBase nodeBase, System.Numerics.Matrix4x4 userTransform)
    {
        // Apply the user transform to the current node.
        // Note that node.WorldTransform has already been computed by
        // UpdateAllNodeTransforms() with parent-child hierarchy included, but it
        // still does not include the user transform
        // (under the unified BuildWorldMatrix convention:
        // anchor + size + rotation + position), so it must be multiplied here.
        var finalWorldMatrix = nodeBase.WorldTransform * userTransform;

        var node = nodeBase as GLTFNode;

        // Update matrices for all primitives owned by this node
        foreach (var primitive in node.Primitives)
        {
            var matrices = new MatrixBuffer
            {
                World = System.Numerics.Matrix4x4.Transpose(finalWorldMatrix),
                // 2-3 contract rule 6: previous state always comes from the CPU
                // shadow copy. Transpose(all-zero) is still all-zero, so the
                // first frame naturally uses the unwritten sentinel.
                PrevWorld = System.Numerics.Matrix4x4.Transpose(primitive.PrevWorldMatrix),
            };
            int fi = (int)Device.FrameIndex;
            Unsafe.Write(primitive.MappedMatrixBuffers[fi], matrices);

            // Roll this frame's world matrix into the shadow copy so it becomes
            // the previous matrix on the next frame. Each primitive advances
            // exactly once per frame.
            primitive.PrevWorldMatrix = finalWorldMatrix;
        }

        // Critical fix: recursively apply the same userTransform to child nodes.
        // Child WorldTransform values already include parent local transforms
        // through hierarchical accumulation, but they still need the same global
        // user transform.
        foreach (var child in nodeBase.Children)
        {
            ApplyUserTransformToNodeTree(child, userTransform);
        }
    }

    /// <summary>
    /// Latch of <see cref="Season.Controls.Model.PartColorVersion"/> this group's material CBs currently hold. Starts at a
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
            // parameter row are the whole per-frame cost. WorldTransform stays at rest, which is exactly what the palette is
            // relative to - the vertex stage multiplies each primitive by restWorld * userTransform while the palette carries
            // inverse(restWorld), so the two cancel and only the user transform survives.
            var player = _asset._animationPlayer;
            player.Advance(time);
            int clip = player.CurrentAnimationIndex;
            _skinning.SetInstance(0, (uint)clip, gpuBake.NormalizeTime(clip, player.AnimationTime, player.Loop));

            // Picking cannot read a storage buffer, so it asks for a CPU pose instead; see GltfAsset.EnsureCpuPose.
            _asset.CpuPoseStale = true;
        }
        else
        {
            // Update animation through the animation player
            // (time advance, keyframe lookup, TRS interpolation, and node-transform
            // updates are all handled internally)
            _asset._animationPlayer.Update(time, _asset.gltfNodes);
        }

        // Morph channels are one of the bake's eligibility rules, so on the GPU route no curve can move a weight and this pass
        // rewrites last frame's values. It is kept regardless because it is also the upload, and a weight set by code rather than
        // by a clip still has to reach the GPU.
        UpdateMorphTargetsRecursive(_asset.gltfNodes);

        // Apply the user transform to root nodes
        // (unified transform convention: route through BuildWorldMatrix with the
        // anchor pivot, see Mesh3DBase)
        var userTransform = model.BuildWorldMatrix();

        // Find all root nodes and apply the user transform.
        // Root nodes are cached by list reference in the player to avoid O(N^2)
        // scans every frame.
        var rootNodes = _asset._animationPlayer.GetRootNodes(_asset.gltfNodes);

        foreach (var rootNode in rootNodes)
        {
            ApplyUserTransformToNodeTree(rootNode, userTransform);
        }

        // Update bone matrices. On the GPU route the kernel already produced them and the ring it wrote is what the draw binds,
        // so the CPU palette is not written at all - which is the saving this route exists for.
        if (gpuBake == null)
            UploadBoneMatricesToGpu();
        else if (_paletteFromCpu)
        {
            // Diagnostic: the same clip and time the kernel was handed, evaluated here instead, and then the node walk is run
            // a second time so the primitive buffers carry the very pose this palette is relative to. Restoring the rest world
            // instead - which is what the real route does - would pair the palette with a matrix it was not built from, and the
            // difference that produced could then be blamed on either half. Re-walking is what leaves the control one question:
            // palette and world both from the CPU should reproduce the CPU route's frame exactly.
            var probe = _asset._animationPlayer;
            int probeClip = probe.CurrentAnimationIndex;
            probe.Evaluate(probeClip, gpuBake.NormalizeTime(probeClip, probe.AnimationTime, probe.Loop), _asset.gltfNodes);
            UploadBoneMatricesToGpu();

            foreach (var rootNode in rootNodes)
            {
                ApplyUserTransformToNodeTree(rootNode, userTransform);
            }
        }

        _transformInitialized = true;

#if DEBUG
        // One measurement of the assumption this route rests on. The palette carries inverse(rest world) and the primitive
        // buffers carry rest world * user, so the pair cancels only if the tree really is still at rest here - after the morph
        // pass and the node walk - and only if the inverse the bake holds was taken from that same rest world. Both are cheap
        // to check once and impossible to read off a picture, since a wrong one renders as a slightly wrong pose.
        if (!_restCheckLogged && gpuBake != null)
        {
            _restCheckLogged = true;
            var skins = _asset.GetAllSkins();
            var bind = skins.Count > 0 ? skins[0].BindNode : null;
            int bindIndex = bind != null ? _asset.gltfNodes.IndexOf(bind) : -1;
            if (bind != null && bindIndex >= 0 && _restWorld != null && bindIndex < _restWorld.Length)
            {
                var snapshot = _restWorld[bindIndex];

                // Delta between the world the draw will actually use and the matrix the palette inverted.
                float walkDelta = MaxElementDelta(bind.WorldTransform, snapshot);

                // Every node that owns a primitive is a node whose world the draw multiplies by, while the palette only cancels
                // the one its skin binds to. Those are the same node in the usual export, and the route depends on it: if any
                // other owner moves between rest and the clip's first frame, the two routes cancel against different matrices
                // and the rendered pose differs even when both palettes agree to the last bit. Measured rather than assumed,
                // because the export shape is not something this file can see.
                var owners = new List<GLTFNode>();
                foreach (var nodeBase in _asset.gltfNodes)
                {
                    if (nodeBase is GLTFNode owner && owner.Primitives.Count > 0)
                        owners.Add(owner);
                }
                var restOwners = new System.Numerics.Matrix4x4[owners.Count];
                for (int i = 0; i < owners.Count; i++)
                    restOwners[i] = owners[i].WorldTransform;

                // Delta between the clip's first frame and that same snapshot: what the CPU route composes instead, and the
                // reason the two routes cancel against different matrices. A large number here is expected and harmless as
                // long as each side cancels against its own; it is only reported so the numbers can be read together.
                _asset._animationPlayer.Evaluate(0, 0f, _asset.gltfNodes);
                float posedDelta = MaxElementDelta(bind.WorldTransform, snapshot);

                string moving = "";
                for (int i = 0; i < owners.Count; i++)
                {
                    float d = MaxElementDelta(owners[i].WorldTransform, restOwners[i]);
                    if (d > 1e-5f)
                        moving += $" {owners[i].Name}={d:E1}";
                }
                RestoreRestWorld();

                DeviceServices.BaseApp?.AddLog(LogType.Error,
                    $"[SingleGpuSkin] bind='{bind.Name}' idx={bindIndex} skins={skins.Count} owners={owners.Count} "
                    + $"bindOwns={owners.Exists(o => ReferenceEquals(o, bind))} "
                    + $"restVsDraw={walkDelta:E3} posedVsRest={posedDelta:E3} moving=[{moving} ]");
            }
        }
#endif

        // 2-3 Step C (tier B): from the second frame onward, previous
        // bone-palette SB contains valid data, so notify the shader that it may
        // read the previous bone SB.
        if (wasInitialized)
        {
            SetPrevBonesReady();
            SetPrevMorphReady();
        }

        // Sync Model.Alpha to all primitive material buffers
        // (written only when it changes; the base class handles the check)
        SyncAlpha(model.Alpha);

        // Per-part base colors follow the alpha sync: the two write different channels of the same CB (RGB vs W), and the
        // sync is version-gated, so this costs one compare on every frame that did not change the part-color set.
        SyncPartColors(model);

        // Unified highlighting: sync the wireframe bit
        // (runtime on/off is supported) and lazily build per-primitive shell
        // geometry on the first enabled frame, then keep it resident. When fully
        // disabled, there is no memory cost and no draw.
        // Each frame writes "node WorldTransform x userTransform" plus face/edge
        // colors into every shell box. This stays in sync with
        // ApplyUserTransformToNodeTree rendering, because shell vertices are in
        // node-local space. Without the node matrix, shells shift or scale
        // incorrectly as a whole, for example when GLB root-node scaling makes
        // the shell larger than the model. Face alpha is animated per frame.
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
                        WriteHighlightBox(highlight, nodeWorld * userTransform, model.Highlight.SurfaceColor, model.Highlight.EdgeColor);
                    }
                }
            }
        }

        // Unified highlighting: sync the bounds box.
        // Box geometry is built lazily on the first enabled frame. Face/edge
        // colors are independent of the model alpha chain and are written every
        // frame. Boxes with near-zero extents (unloaded / degenerate) stay off.
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

        SetOutline2DState(model.Highlight.Outline,
            model.Highlight.OutlineColor, model.Highlight.OutlineWidth);
    }

    /// <summary>
    /// Locks this frame onto one of the two routes and returns the bake the kernel will evaluate, or null for the CPU route. The
    /// null return is what the caller branches on, so it never has to re-read the mode.
    ///
    /// Two settings differ from the instanced path. The block is one instance wide, and it reserves no tag row: a tag exists so a
    /// non-zero instance index can find its own block, and a single model always addresses row <c>jointIndex</c> from row zero
    /// because <c>isInstanced</c> is false. Reserving one here would make the kernel write a row the vertex stage never reads.
    /// </summary>
    InstanceSkinBake? BeginSkinningFrame(Season.Controls.Model model)
    {
        bool wanted = Season.Rendering.InstanceSkinning.Enabled
            && (Season.Rendering.InstanceSkinning.Override ?? model.SkinningMode) == Season.Rendering.InstanceSkinningMode.Gpu;

        if (wanted && _asset.SkinBake == null && !_bakeRefused)
        {
            // The mode was turned on after the asset loaded, which is not the intended order but is not worth refusing: the build
            // is a one-off cost at the next Update, and the alternative - silently keeping the CPU route for the rest of the run -
            // is invisible to whoever set the property.
            //
            // TryCreate rather than TryBake: the effective mode is already known to be Gpu here, and what has to be told apart is
            // "cannot be built" from "not built yet". A backend without vertex-stage storage reads counts as the former even
            // though it is a capability rather than a shape, because capability is fixed for the life of the process - and
            // building the tables on such a backend would spend memory on data no draw could read.
            var g = Season.Basic.Graphics.Instance;
            if (g == null || !g.ComputeSupported || !g.VertexShaderStorageReadSupported)
                _bakeRefused = true;
            else if (InstanceSkinBake.TryCreate(_asset, out var freshBake))
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

        // The storage diagnostic keeps the whole schedule - bake, parameter row, dispatch - and changes only what the vertex stage
        // reads afterwards. The slot is overwritten with the CPU palette at the one point between the dispatch and the handoff, so
        // the block the kernel just wrote is never what the draw sees; a frame that still differs from the CPU route then cannot be
        // explained by the kernel at all.
        _skinning.AfterDispatch = _paletteFromStorage ? PublishCpuPalette : null;

#if DEBUG
        // The numbers the kernel and the vertex stage each have to agree on, logged once. A mismatch here renders as a plausible
        // pose rather than as an error, so it is worth having in the capture's stderr next to the dispatch counts.
        if (!_gpuSkinLogged)
        {
            _gpuSkinLogged = true;
            var logged = _asset._animationPlayer;
            DeviceServices.BaseApp?.AddLog(LogType.Error,
                $"[SingleGpuSkin] joints={_skinning.JointCount} stride={_skinning.PaletteStride}/{_bonePaletteStride} "
                + $"clips={_skinning.ClipCount} bakeStride={bake.PaletteStride} slots={_gpuPalette.Slots.Length} "
                + $"cur={_gpuPalette.CurrentSlot} prev={_gpuPalette.PreviousSlot} "
                + $"palette={(_paletteFromStorage ? "storage" : _paletteFromCpu ? "cpu" : "gpu")} "
                + $"rest={_restWorld?.Length}/{_asset.gltfNodes.Count} clip={logged.CurrentAnimationIndex} "
                + $"t={logged.AnimationTime:F4} loop={logged.Loop}");
        }
#endif

        // The palette is relative to the rest pose, so the per-primitive factor has to be the rest world too. Entering the route
        // from the CPU one leaves an animated pose in the tree, and picking leaves one behind mid-route; both are undone here
        // rather than where they happen, because this is the only place that knows the tree is being handed over.
        if (!wasGpu || _asset.CpuPoseDisturbed)
        {
            _asset.CpuPoseDisturbed = false;
            RestoreRestWorld();
        }

        return bake;
    }

    /// <summary>
    /// Snapshots every node's rest world through the shared <see cref="Season.Models.RestPoseSnapshot"/>. That type exists because
    /// the palette carries <c>inverse(rest world)</c> and the vertex stage multiplies by this matrix immediately after, so the pair
    /// cancels only when both sides are the same value; computing it the way the bake does - local TRS from the initial values,
    /// walked up the parent chain - is what makes it depend on nothing.
    /// </summary>
    void CaptureRestWorld()
    {
        _restPose.Capture(_asset);
    }

#if DEBUG
    /// <summary>Largest absolute element difference between two matrices, for the route's one-shot diagnostics.</summary>
    static float MaxElementDelta(in System.Numerics.Matrix4x4 a, in System.Numerics.Matrix4x4 b)
    {
        float d = 0f;
        d = MathF.Max(d, MathF.Abs(a.M11 - b.M11)); d = MathF.Max(d, MathF.Abs(a.M12 - b.M12));
        d = MathF.Max(d, MathF.Abs(a.M13 - b.M13)); d = MathF.Max(d, MathF.Abs(a.M14 - b.M14));
        d = MathF.Max(d, MathF.Abs(a.M21 - b.M21)); d = MathF.Max(d, MathF.Abs(a.M22 - b.M22));
        d = MathF.Max(d, MathF.Abs(a.M23 - b.M23)); d = MathF.Max(d, MathF.Abs(a.M24 - b.M24));
        d = MathF.Max(d, MathF.Abs(a.M31 - b.M31)); d = MathF.Max(d, MathF.Abs(a.M32 - b.M32));
        d = MathF.Max(d, MathF.Abs(a.M33 - b.M33)); d = MathF.Max(d, MathF.Abs(a.M34 - b.M34));
        d = MathF.Max(d, MathF.Abs(a.M41 - b.M41)); d = MathF.Max(d, MathF.Abs(a.M42 - b.M42));
        d = MathF.Max(d, MathF.Abs(a.M43 - b.M43)); d = MathF.Max(d, MathF.Abs(a.M44 - b.M44));
        return d;
    }
#endif

    /// <summary>
    /// Puts every node back to the snapshot through the shared <see cref="Season.Models.RestPoseSnapshot"/>, which writes the TRS
    /// triple alongside the world matrix: rest is a property of the triple, not of the cached product, so leaving an evaluated
    /// triple behind would read back as animated the first time anything recomposes the tree.
    /// </summary>
    void RestoreRestWorld()
    {
        _restPose.Restore(_asset);
    }

    /// <summary>2-3 Step C (tier B): once previous bone-palette SB contains
    /// valid data, sets MaterialParams.HasPrevBones = 1 on all primitives.
    /// Written only on the first call because later frames keep the same value
    /// and are guarded by early-out.</summary>
    void SetPrevBonesReady()
    {
        var primitives = new List<PrimitiveData>();
        CollectPrimitives(primitives);
        for (int i = 0; i < primitives.Count; i++)
        {
            var primitive = primitives[i];
            if (primitive.MaterialParams.HasPrevBones != 0)
                continue;
            primitive.MaterialParams.HasPrevBones = 1;
            // Flip only the flag bit. Perform read-modify-write per frame so all
            // other fields are preserved and only this flag changes.
            for (int f = 0; f < Device.frameCount; f++)
            {
                var mp = Unsafe.Read<MaterialParams>(primitive.MappedMaterialBuffers[f]);
                mp.HasPrevBones = 1;
                Unsafe.Write(primitive.MappedMaterialBuffers[f], mp);
            }
        }
    }

    /// <summary>2-3 Step C (tier C-b completion): once previous morph-weights SB
    /// contains valid data, sets MaterialParams.HasPrevMorph = 1 for primitives
    /// that have morph targets.
    /// Written only on the first call because later frames keep the same value
    /// and are guarded by early-out.</summary>
    void SetPrevMorphReady()
    {
        if (_prevMorphWeightsSrvHandle.Ptr == 0)
            return;
        var primitives = new List<PrimitiveData>();
        CollectPrimitives(primitives);
        for (int i = 0; i < primitives.Count; i++)
        {
            var primitive = primitives[i];
            if (primitive.MaterialParams.HasMorphTargets == 0 || primitive.MaterialParams.HasPrevMorph != 0)
                continue;
            primitive.MaterialParams.HasPrevMorph = 1;
            // Same as SetPrevBonesReady: preserve all other fields through
            // per-frame read-modify-write.
            for (int f = 0; f < Device.frameCount; f++)
            {
                var mp = Unsafe.Read<MaterialParams>(primitive.MappedMaterialBuffers[f]);
                mp.HasPrevMorph = 1;
                Unsafe.Write(primitive.MappedMaterialBuffers[f], mp);
            }
        }
    }

    /// <summary>Called by the base Draw path: recursively walks _asset.gltfNodes
    /// and copies each GLTFNode's Primitives into `result`.</summary>
    protected override void CollectPrimitives(List<PrimitiveData> result)
    {
        CollectPrimitivesRecursive(_asset.gltfNodes, result);
    }

    /// <summary>
    /// Used by DXInstancedModel to collect all primitives from the template
    /// model.
    /// Callers should clone these PrimitiveData instances, sharing VB/IB/textures
    /// while creating their own Material CBs.
    /// </summary>
    internal void CollectAllPrimitives(List<PrimitiveData> result)
    {
        CollectPrimitivesRecursive(_asset.gltfNodes, result);
    }

    void CollectPrimitivesRecursive(List<GltfNodeBase> nodes, List<PrimitiveData> result)
    {
        foreach (var nodeBase in nodes)
        {
            var node = nodeBase as GLTFNode;
            if (node != null)
                result.AddRange(node.Primitives);
            CollectPrimitivesRecursive(nodeBase.Children, result);
        }
    }

    protected override GpuDescriptorHandle GetBoneSrvHandle()
    {
        // On the GPU route the palette lives in a compute-written storage buffer, whose structured view was created with it, so
        // the mapped upload ring is not consulted at all. Gated on the latch rather than on the handoff result, for the reason the
        // instanced path spells out: the ring always holds the last palette the kernel produced, while falling through here would
        // bind a buffer this route never wrote. The storage diagnostic deliberately keeps this binding while swapping the source,
        // which is what makes it a different question from the cpu switch.
        if (_gpuSkin && (!_paletteFromCpu || _paletteFromStorage) && _skinning.Palette?.Current is DXStorageBuffer gpuBone)
        {
#if DEBUG
            // Printed from the draw side rather than the publish side, because the question is not what was written but what this
            // call hands the command list. A zero pointer here is the whole answer: the draw then binds the default palette, whose
            // contents no dispatch ever touches, and every run that lands on it renders the same wrong pose regardless of bytes.
            if (_paletteFromStorage && _storageProbeFrame != Device.FrameIndex
                && (uint)(Device.FrameIndex - _storageProbeFrame) >= 500u)
            {
                _storageProbeFrame = Device.FrameIndex;
                var prev = _skinning.Palette?.Previous as DXStorageBuffer;
                DeviceServices.BaseApp?.AddLog(LogType.Error,
                    $"[StorageDraw] frame={Device.FrameIndex} gpuSkin={_gpuSkin} live={_skinning.Live} "
                    + $"cur={_skinning.Palette?.CurrentSlot} prev={_skinning.Palette?.PreviousSlot} "
                    + $"id={gpuBone.VertexSrvDescriptorID} ptr={(ulong)gpuBone.VertexSrvGpuHandle.Ptr} "
                    + $"prevPtr={(prev != null ? (ulong)prev.VertexSrvGpuHandle.Ptr : 0)}");
            }
#endif
            return gpuBone.VertexSrvGpuHandle;
        }
        return DXSceneDrawCollection.Structured(_bonePaletteBuffer, 64, _bonePaletteSrvHandle);
    }

    protected override GpuDescriptorHandle GetPrevBoneSrvHandle()
    {
        // The previous frame is the ring one slot back, which is where the kernel wrote it last frame. The CPU memcpy that feeds
        // the upload ring cannot see a compute-written buffer at all, so on this route that copy simply does not happen. Under the
        // storage diagnostic the slot is one this class published itself last frame, with the same CPU palette.
        if (_gpuSkin && (!_paletteFromCpu || _paletteFromStorage) && _skinning.Palette?.Previous is DXStorageBuffer gpuPrev)
            return gpuPrev.VertexSrvGpuHandle;
        return DXSceneDrawCollection.Structured(_prevBonePaletteBuffer, 64, _prevBonePaletteSrvHandle);
    }
    protected override GpuDescriptorHandle GetPrevMorphSrvHandle()
        => DXSceneDrawCollection.Structured(_prevMorphWeightsBuffer, 16, _prevMorphWeightsSrvHandle);

    public override void Dispose()
    {
        // Leave the scheduler before the buffers it points at go away, and release the compute-side parameter and scratch blocks
        // the target owns. The palette ring is this class's own, so it dies here too.
        _skinning.Dispose();
        _gpuPalette?.Dispose();
        _gpuPalette = null;
        _gpuSkin = false;

        foreach (var nodeBase in _asset.gltfNodes)
        {
            var node = nodeBase as GLTFNode;
            if (node == null) continue;

            foreach (var primitive in node.Primitives)
            {
                // Release all GPU resources
                primitive.Dispose();
            }
        }
        _asset._nodeMap.Clear();

        if (_bonePaletteBuffer != null)
        {
            _bonePaletteBuffer->Unmap(0, null);
            _bonePaletteBuffer->Release();
            _bonePaletteBuffer = null;
            _mappedBonePaletteBuffer = null;
        }

        if (_bonePaletteDescriptorId >= 0)
        {
            Device.DescriptorAllocator.Free(_bonePaletteDescriptorId);
            _bonePaletteDescriptorId = -1;
            _bonePaletteSrvHandle = default;
        }

        // 2-3 Step C (tier B): release the previous bone-palette SB
        if (_prevBonePaletteBuffer != null)
        {
            _prevBonePaletteBuffer->Unmap(0, null);
            _prevBonePaletteBuffer->Release();
            _prevBonePaletteBuffer = null;
            _mappedPrevBonePaletteBuffer = null;
        }
        if (_prevBonePaletteDescriptorId >= 0)
        {
            Device.DescriptorAllocator.Free(_prevBonePaletteDescriptorId);
            _prevBonePaletteDescriptorId = -1;
            _prevBonePaletteSrvHandle = default;
        }

        // 2-3 Step C (tier C-b completion): release the previous morph-weights SB
        if (_prevMorphWeightsBuffer != null)
        {
            _prevMorphWeightsBuffer->Unmap(0, null);
            _prevMorphWeightsBuffer->Release();
            _prevMorphWeightsBuffer = null;
            _mappedPrevMorphWeightsBuffer = null;
        }
        if (_prevMorphWeightsDescriptorId >= 0)
        {
            Device.DescriptorAllocator.Free(_prevMorphWeightsDescriptorId);
            _prevMorphWeightsDescriptorId = -1;
            _prevMorphWeightsSrvHandle = default;
        }

        // Unified highlighting: release the highlight pool
        // (host bounds box + per-primitive wireframe shell boxes)
        DisposeHighlights();
    }
}
