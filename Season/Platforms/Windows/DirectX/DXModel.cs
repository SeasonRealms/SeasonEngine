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
/// This type only carries glTF-specific pieces: the bone-matrix CB
/// (bound to slot 8 in OnBeforeDraw), animation ticking, recursive primitive
/// collection through the node tree, and ProcessMaterial for the five PBR textures.
/// </summary>
internal unsafe class DXModel : DXPrimitiveGroup
{
    // Reuse GltfAsset loading and playback for nodes / animations / skins
    // through composition, so single inheritance does not get consumed by GltfAsset.
    internal readonly GltfAsset _asset = new GltfAsset();

    // Bone-matrix buffers (N-buffered)
    private ID3D12Resource*[] _boneMatrixBuffers;
    private byte*[] _mappedBoneMatrixBuffers;
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

    public void Load(Season.Controls.Model model, Season.Basic.Camera camera)
    {
        // Animation query / switching belongs to the glTF parsing domain and
        // does not go through IGraphics. Inject the asset reference into the
        // control on the direct-load path.
        model.Asset = _asset;

        _asset.Load(model, camera);
        _asset.PlayAnimation();

        InitializeBonePaletteResources();
        CreateBoneMatrixBuffer();

        _asset.ValidateSkinData();
    }

    public DXModel CreateInstance(Season.Controls.Model model, Season.Basic.Camera camera)
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
        // Shared-template instancing path: inject the instance asset into the
        // control, matching the semantics of the direct-load Load path.
        model.Asset = instance._asset;
        instance.InitializeBonePaletteResources();
        instance.CreateBoneMatrixBuffer();

        return instance;
    }

    GltfNodeBase EnsureClonedNode(Dictionary<GltfNodeBase, GltfNodeBase> nodeMap, GltfNodeBase sourceNode, Season.Controls.Model model, Season.Basic.Camera camera)
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
                dxNode.Primitives.Add(ClonePrimitiveData(primitive, dxNode, model, camera));

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
            View = Matrix4x4.Transpose(camera.View),
            Projection = Matrix4x4.Transpose(camera.Projection)
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

    // Bone-matrix buffer creation
    void CreateBoneMatrixBuffer()
    {
        int n = (int)Device.frameCount;
        _boneMatrixBuffers = new ID3D12Resource*[n];
        _mappedBoneMatrixBuffers = new byte*[n];
        uint matrixCount = (uint)Math.Max(1, _bonePaletteStride);
        for (int i = 0; i < n; i++)
            _boneMatrixBuffers[i] = Device.ResourceManager.CreateConstantBuffer(
                (uint)(Unsafe.SizeOf<System.Numerics.Matrix4x4>() * matrixCount),
                out _mappedBoneMatrixBuffers[i]);
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

        _bonePaletteDescriptorId = Device.DescriptorAllocator.Allocate();
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

        _prevBonePaletteDescriptorId = Device.DescriptorAllocator.Allocate();
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

    void ProcessPrimitive(MeshPrimitive meshPrimitive, GltfNodeBase node, Season.Controls.Model model, Season.Basic.Camera camera)
    {
        var primitiveData = CreatePrimitiveData(meshPrimitive, node, model, camera);

        var gltfNode = node as GLTFNode;

        gltfNode.Primitives.Add(primitiveData);
    }

    PrimitiveData CreatePrimitiveData(MeshPrimitive primitive, GltfNodeBase node, Season.Controls.Model model, Season.Basic.Camera camera)
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
            View = System.Numerics.Matrix4x4.Transpose(camera.View),
            Projection = System.Numerics.Matrix4x4.Transpose(camera.Projection)
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

            _prevMorphWeightsDescriptorId = Device.DescriptorAllocator.Allocate();
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
                primitiveData.MaterialParams.MetallicFactor = gLTFMaterial1.MetallicFactor;
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
                primitiveData.MaterialParams.RoughnessFactor = gLTFMaterial1.RoughnessFactor;
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

        if (boneMatrices.Length > 0 && _mappedBoneMatrixBuffers != null)
        {
            int matrixSize = Unsafe.SizeOf<System.Numerics.Matrix4x4>();
            int totalSize = matrixSize * boneMatrices.Length;
            int fi = (int)Device.FrameIndex;

            fixed (void* matricesPtr = boneMatrices)
            {
                // 2-3 Step C (tier B): before uploading this frame, first copy
                // the current bone palette into the mapped previous-SB region.
                // On the first frame, the current side is all zero, so the
                // previous side stays zero and the sentinel semantics remain correct.
                if (_mappedPrevBonePaletteBuffer != null && _mappedBonePaletteBuffer != null)
                    Unsafe.CopyBlock((void*)_mappedPrevBonePaletteBuffer, (void*)_mappedBonePaletteBuffer, (uint)totalSize);

                Unsafe.CopyBlock(_mappedBoneMatrixBuffers[fi], matricesPtr, (uint)totalSize);
                if (_mappedBonePaletteBuffer != null)
                    Unsafe.CopyBlock(_mappedBonePaletteBuffer, matricesPtr, (uint)totalSize);
            }
        }

    }

    void ApplyUserTransformToNodeTree(GltfNodeBase nodeBase, System.Numerics.Matrix4x4 userTransform, Season.Basic.Camera camera)
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
                View = System.Numerics.Matrix4x4.Transpose(camera.View),
                Projection = System.Numerics.Matrix4x4.Transpose(camera.Projection),
                // 2-3 contract rule 6: previous state always comes from the CPU
                // shadow copy. Transpose(all-zero) is still all-zero, so the
                // first frame naturally uses the unwritten sentinel.
                PrevWorld = System.Numerics.Matrix4x4.Transpose(primitive.PrevWorldMatrix),
                PrevViewProjection = System.Numerics.Matrix4x4.Transpose(camera.PrevViewProjection),
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
            ApplyUserTransformToNodeTree(child, userTransform, camera);
        }
    }

    public void Update(Season.Controls.Model model, float time)
    {
        bool wasInitialized = _transformInitialized;

        // Update animation through the animation player
        // (time advance, keyframe lookup, TRS interpolation, and node-transform
        // updates are all handled internally)
        _asset._animationPlayer.Update(time, _asset.gltfNodes);
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
            ApplyUserTransformToNodeTree(rootNode, userTransform, Camera);
        }

        // Update bone matrices
        UploadBoneMatricesToGpu();

        _transformInitialized = true;

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

    /// <summary>Binds the bone-matrix CB before Draw, at root-signature slot 8.</summary>
    protected override void OnBeforeDraw()
    {
        if (_boneMatrixBuffers != null)
        {
            int fi = (int)Device.FrameIndex;
            Device.GraphicsCommandList->SetGraphicsRootConstantBufferView(
                8, _boneMatrixBuffers[fi]->GetGPUVirtualAddress());
        }
    }

    protected override GpuDescriptorHandle GetBoneSrvHandle() => _bonePaletteSrvHandle;
    protected override GpuDescriptorHandle GetPrevBoneSrvHandle() => _prevBonePaletteSrvHandle;
    protected override GpuDescriptorHandle GetPrevMorphSrvHandle() => _prevMorphWeightsSrvHandle;

    public override void Dispose()
    {
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
