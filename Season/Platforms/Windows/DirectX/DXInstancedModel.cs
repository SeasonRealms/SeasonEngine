// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Direct3D12;
using Season.Models;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Season.Platforms.Windows.DirectX;

/// <summary>
/// GPU-instancing rendering backend for GLB models.
/// Extracts primitives from a shared DXModel template
/// (sharing VB/IB/textures), builds its own Material CBs, and performs
/// instanced rendering through the DXInstancedPrimitiveGroup base class.
/// v2 supports skeletal animation and morph targets by cloning the template's
/// animation data and playing it independently.
/// </summary>
internal unsafe class DXInstancedModel : DXInstancedPrimitiveGroup
{
    // ============================================================
    // Animation support: clone the template animation data so each instance can
    // play independently
    // ============================================================
    internal readonly GltfAsset _asset = new GltfAsset();

    // Per-instance bone-matrix StructuredBuffer, replacing the old CBV path
    private int _bonePaletteStride = 1;
    private ID3D12Resource* _instanceBoneBuffer;
    private byte* _mappedInstanceBoneBuffer;
    private GpuDescriptorHandle _instanceBoneSrvHandle;
    private int _instanceBoneDescriptorId = -1;
    private uint _instanceBoneCapacity;

    // 2-3 Step C (tier B): previous per-instance bone SB
    // (same capacity as _instanceBoneBuffer, with _bonePaletteStride matrices
    // per entry).
    // Before each frame upload, memcpy the current mapped bone-buffer region into
    // the mapped prev region so the GPU always holds the previous frame's bone
    // palette.
    // Contents are zeroed before the first frame (sentinel _m33==0), so the
    // shader falls back to current bones joint by joint.
    private ID3D12Resource* _prevInstanceBoneBuffer;
    private byte* _mappedPrevInstanceBoneBuffer;
    private GpuDescriptorHandle _prevInstanceBoneSrvHandle;
    private int _prevInstanceBoneDescriptorId = -1;
    private uint _prevInstanceBoneCapacity;

    // 2-3 Step C (tier C-b): previous per-instance morph-weights SB
    // (one float4 per entry).
    // Capacity = _instanceCount. All primitives share the same SB and index it
    // by instanceID.
    // Before extracting this frame's morph weights, copy the current CPU shadow
    // into the mapped prev-SB region, then extract new weights and update the
    // shadow.
    private ID3D12Resource* _prevMorphWeightsBuffer;
    private byte* _mappedPrevMorphWeightsBuffer;
    private GpuDescriptorHandle _prevMorphWeightsSrvHandle;
    private int _prevMorphWeightsDescriptorId = -1;
    private int _prevMorphWeightsCapacity;
    // CPU shadow of the current morph weights per primitive, indexed by
    // InstanceStreamIndex. Copy it to the prev SB before extracting new weights,
    // then update it afterward.
    private Vector4[] _currentMorphShadow = Array.Empty<Vector4>();

    // Rest-pose snapshot of nodes, used to restore the initial TRS state for
    // each instance every frame
    // Layout: [nodeIndex].Translation/Rotation/Scale/Weights
    private (Vector3 Translation, Quaternion Rotation, Vector3 Scale, float[] Weights)[] _restPoseSnapshot;
    // Working node list referencing _asset.gltfNodes. It is restored from the
    // snapshot before evaluating each instance.
    private List<GltfNodeBase> _workNodes;
    private readonly List<GLTFSkin> _skins = new();
    private InstanceAnimationState[] _animationStates = Array.Empty<InstanceAnimationState>();
    private PrimitiveInstanceStream[] _primitiveInstanceStreams = Array.Empty<PrimitiveInstanceStream>();

    struct InstanceAnimationState
    {
        public bool Initialized;
        public int AnimationClip;
        public float PlaybackTime;
    }

    sealed class PrimitiveInstanceStream
    {
        public ID3D12Resource* Buffer;
        public VertexBufferView View;
        public InstanceTransformData[] Data = Array.Empty<InstanceTransformData>();
        public Matrix4x4[] Worlds = Array.Empty<Matrix4x4>();
        public int Capacity;
    }

    public DXInstancedModel(string name) : base(name)
    {
    }

    /// <summary>
    /// Loads primitives from a shared model template. The caller guarantees that
    /// `template` has already been loaded.
    /// Phase 4 clones the template animation data (nodes / skins / animations)
    /// and creates the bone-matrix buffer.
    /// </summary>
    public void Load(DXModel template, Season.Controls.Model model, Season.Basic.Camera camera)
    {
        CreateSharedMatrixBuffers(camera);

        // Step 1: clone the template animation data
        // (node tree, skins, animations)
        var nodeMap = new Dictionary<GltfNodeBase, GltfNodeBase>();
        CloneAnimationData(template, nodeMap);

        // Step 2: clone primitives and remap OwnerNode to the cloned nodes
        var templatePrimitives = new List<PrimitiveData>();
        template.CollectAllPrimitives(templatePrimitives);

        foreach (var source in templatePrimitives)
        {
            var clone = CloneForInstancing(source, model);
            if (source.OwnerNode != null && nodeMap.TryGetValue(source.OwnerNode, out var clonedNode))
                clone.OwnerNode = clonedNode;
            clone.InstanceStreamIndex = _primitives.Count;
            _primitives.Add(clone);
        }

        // Step 3: initialize the animation player
        _asset._animationPlayer.Initialize(_asset._animations);

        // Step 4: save the node rest-pose snapshot so initial TRS can be
        // restored before evaluating each instance
        SaveRestPoseSnapshot();

        _skins.Clear();
        _skins.AddRange(_asset.GetAllSkins());
        _bonePaletteStride = Math.Max(1, _skins.Sum(s => s.Joints.Count));
        _primitiveInstanceStreams = new PrimitiveInstanceStream[_primitives.Count];
        for (int i = 0; i < _primitiveInstanceStreams.Length; i++)
            _primitiveInstanceStreams[i] = new PrimitiveInstanceStream();

        SyncInstancedSkinningMaterialParams();

        // Step 5: create the per-instance bone-matrix StructuredBuffer
        CreateInstanceBoneBuffer();

        RebuildPrimitiveBuckets();
        SyncAlpha(model.Alpha);
    }

    /// <summary>
    /// Clones a template primitive for instanced rendering:
    /// - VB / IB / textures -> shared pointer references, with no new GPU resources;
    /// - Material CB -> its own N-buffered allocation, because Alpha may differ;
    /// - Matrix CB -> unnecessary, because the base class manages the shared
    ///   instanced Matrix CB;
    /// - apply model-level material overrides (MaterialColor / Unlit).
    /// </summary>
    static PrimitiveData CloneForInstancing(PrimitiveData source, Season.Controls.Model model)
    {
        var clone = new PrimitiveData
        {
            Vertices = source.Vertices,
            Indices = source.Indices,
            Use32BitIndices = source.Use32BitIndices,
            DoubleSided = source.DoubleSided,
            LocalBoundsCenter = source.LocalBoundsCenter,
            LocalBoundsExtents = source.LocalBoundsExtents,

            // Shared GPU geometry
            VertexBuffer = source.VertexBuffer,
            VertexBufferView = source.VertexBufferView,
            IndexBuffer = source.IndexBuffer,
            IndexBufferView = source.IndexBufferView,

            // Shared textures
            BaseColorTexture = source.BaseColorTexture,
            NormalTexture = source.NormalTexture,
            MetallicRoughnessTexture = source.MetallicRoughnessTexture,
            OcclusionTexture = source.OcclusionTexture,
            EmissiveTexture = source.EmissiveTexture,

            // Copy material parameters and apply model-level overrides
            MaterialParams = source.MaterialParams,
            OriginalBaseColorAlpha = source.OriginalBaseColorAlpha,
            OriginalAlphaCutoff = source.OriginalAlphaCutoff,
            IsTransparent = source.IsTransparent,

            // Phase 3: share the morph-delta buffer
            // (same policy as VB/IB/textures)
            MorphDeltasBuffer = source.MorphDeltasBuffer,
            MorphDeltasSrvHandle = source.MorphDeltasSrvHandle,
            MorphDescriptorId = -1, // Not owned here; follows the template model lifetime
        };

        // Apply Model.MaterialColor and Unlit
        var colorTint = model.MaterialColor ?? System.Numerics.Vector4.One;
        clone.MaterialParams.BaseColor *= colorTint;
        clone.MaterialParams.RenderMode = model.Unlit ? 0u : 1u;
        clone.MaterialParams.IsInstanced = 1;  // GPU instancing path: VS reads per-instance matrices
        clone.MaterialParams.BonePaletteStride = 1;
        clone.OriginalBaseColorAlpha = clone.MaterialParams.BaseColor.W;

        // Create an independent Material CB (N-buffered)
        CreateMaterialBuffer(clone);
        WriteMaterialBuffer(clone);

        return clone;
    }

    // ============================================================
    // Phase 4: clone animation data by copying nodes / skins / animations
    // independently from the template
    // ============================================================

    void CloneAnimationData(DXModel template, Dictionary<GltfNodeBase, GltfNodeBase> nodeMap)
    {
        // Pass 1: create cloned nodes
        // (keep only transform properties, without primitives)
        foreach (var sourceNode in template._asset.gltfNodes)
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
                // v2 picking: PickMesh is immutable after loading, so it is
                // shared by reference with no deep copy. NodeIndex stays aligned.
                PickMeshes = sourceNode.PickMeshes,
            };
            nodeMap[sourceNode] = clone;
            _asset.gltfNodes.Add(clone);
        }

        // Pass 2: fix parent-child relationships
        foreach (var sourceNode in template._asset.gltfNodes)
        {
            var clone = nodeMap[sourceNode];
            foreach (var child in sourceNode.Children)
            {
                if (nodeMap.TryGetValue(child, out var clonedChild))
                    clone.Children.Add(clonedChild);
            }
        }

        // Pass 3: clone skins
        // The InstancedModel per-instance matrix replaces the mesh node's
        // WorldTransform (b0 world=Identity), but joint.WorldTransform still
        // contains shared-ancestor scaling such as 0.01, which must be canceled
        // by inverseMeshWorld.
        // Set BindNode to the skeleton root (SkeletonRoot or the first joint) so
        // bone = IBM x joint.WT x Inv(root.WT) = Identity.
        var skinMap = new Dictionary<GLTFSkin, GLTFSkin>();
        foreach (var sourceSkin in template._asset.GetAllSkins())
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

        foreach (var sourceNode in template._asset.gltfNodes)
        {
            if (sourceNode.Skin != null
                && nodeMap.TryGetValue(sourceNode, out var clonedNode)
                && skinMap.TryGetValue(sourceNode.Skin, out var clonedSkin))
            {
                clonedNode.Skin = clonedSkin;
            }
        }

        // Pass 4: clone animations by reusing DXModel.CloneAnimations
        // to deep-copy samplers and remap nodes
        _asset._animations = DXModel.CloneAnimations(template._asset._animations, nodeMap);
    }

    // ============================================================
    // Bone matrices and per-primitive instance data
    // ============================================================

    /// <summary>Saves the rest-pose TRS snapshot for every node.</summary>
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

    /// <summary>Restores nodes to the rest-pose snapshot state.</summary>
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
            if (stream.Capacity >= count && stream.Buffer != null)
                continue;

            if (stream.Buffer != null)
            {
                stream.Buffer->Release();
                stream.Buffer = null;
            }

            stream.Buffer = Device.CreateVertexBuffer<InstanceTransformData>((uint)count, out stream.View);
            stream.Capacity = count;
            stream.Data = new InstanceTransformData[count];
            stream.Worlds = new Matrix4x4[count];
        }
    }

    void ReleasePrimitiveInstanceBuffers()
    {
        foreach (var stream in _primitiveInstanceStreams)
        {
            if (stream?.Buffer != null)
            {
                stream.Buffer->Release();
                stream.Buffer = null;
            }

            if (stream != null)
            {
                stream.Data = Array.Empty<InstanceTransformData>();
                stream.Worlds = Array.Empty<Matrix4x4>();
                stream.Capacity = 0;
            }
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
            WriteMaterialBuffer(primitive);
        }
    }

    /// <summary>Creates the per-instance bone-matrix StructuredBuffer on the
    /// upload heap.</summary>
    void CreateInstanceBoneBuffer()
    {
        _instanceBoneCapacity = 256;
        ulong bufferSize = (ulong)(_bonePaletteStride * _instanceBoneCapacity * sizeof(Matrix4x4));
        _instanceBoneBuffer = Device.ResourceManager.CreateBuffer(
            HeapType.Upload, bufferSize, ResourceStates.GenericRead);
        void* pData;
        _instanceBoneBuffer->Map(0, null, &pData);
        _mappedInstanceBoneBuffer = (byte*)pData;

        _instanceBoneDescriptorId = Device.DescriptorAllocator.Allocate();
        var cpuHandle = Device.SrvHeapManager.GetCpuHandle(_instanceBoneDescriptorId);
        var srvDesc = new ShaderResourceViewDesc
        {
            Format = Silk.NET.DXGI.Format.FormatUnknown,
            ViewDimension = SrvDimension.Buffer,
            Shader4ComponentMapping = 0x00001688u,
            Buffer = new BufferSrv
            {
                FirstElement = 0,
                NumElements = (uint)(_bonePaletteStride * _instanceBoneCapacity),
                StructureByteStride = (uint)sizeof(Matrix4x4),
                Flags = BufferSrvFlags.None
            }
        };
        Device.D3dDevice->CreateShaderResourceView(_instanceBoneBuffer, &srvDesc, cpuHandle);
        _instanceBoneSrvHandle = Device.SrvHeapManager.GetGpuHandle(_instanceBoneDescriptorId);
    }

    void EnsureInstanceBoneCapacity(uint capacity)
    {
        if (capacity <= _instanceBoneCapacity)
            return;

        if (_instanceBoneBuffer != null)
        {
            _instanceBoneBuffer->Unmap(0, null);
            _instanceBoneBuffer->Release();
        }

        if (_instanceBoneDescriptorId >= 0)
        {
            Device.DescriptorAllocator.Free(_instanceBoneDescriptorId);
            _instanceBoneDescriptorId = -1;
        }

        _instanceBoneCapacity = capacity;
        ulong bufferSize = (ulong)(_bonePaletteStride * _instanceBoneCapacity * sizeof(Matrix4x4));
        _instanceBoneBuffer = Device.ResourceManager.CreateBuffer(
            HeapType.Upload, bufferSize, ResourceStates.GenericRead);
        void* pData;
        _instanceBoneBuffer->Map(0, null, &pData);
        _mappedInstanceBoneBuffer = (byte*)pData;

        _instanceBoneDescriptorId = Device.DescriptorAllocator.Allocate();
        var cpuHandle = Device.SrvHeapManager.GetCpuHandle(_instanceBoneDescriptorId);
        var srvDesc = new ShaderResourceViewDesc
        {
            Format = Silk.NET.DXGI.Format.FormatUnknown,
            ViewDimension = SrvDimension.Buffer,
            Shader4ComponentMapping = 0x00001688u,
            Buffer = new BufferSrv
            {
                FirstElement = 0,
                NumElements = (uint)(_bonePaletteStride * _instanceBoneCapacity),
                StructureByteStride = (uint)sizeof(Matrix4x4),
                Flags = BufferSrvFlags.None
            }
        };
        Device.D3dDevice->CreateShaderResourceView(_instanceBoneBuffer, &srvDesc, cpuHandle);
        _instanceBoneSrvHandle = Device.SrvHeapManager.GetGpuHandle(_instanceBoneDescriptorId);
    }

    // 2-3 Step C (tier B): grow/shrink the previous per-instance bone SB
    // (same capacity as current; clear on first creation)
    void EnsurePrevInstanceBoneCapacity(uint capacity)
    {
        if (capacity <= _prevInstanceBoneCapacity)
            return;

        if (_prevInstanceBoneBuffer != null)
        {
            _prevInstanceBoneBuffer->Unmap(0, null);
            _prevInstanceBoneBuffer->Release();
        }
        if (_prevInstanceBoneDescriptorId >= 0)
        {
            Device.DescriptorAllocator.Free(_prevInstanceBoneDescriptorId);
            _prevInstanceBoneDescriptorId = -1;
        }

        _prevInstanceBoneCapacity = capacity;
        ulong bufferSize = (ulong)(_bonePaletteStride * _prevInstanceBoneCapacity * sizeof(Matrix4x4));
        _prevInstanceBoneBuffer = Device.ResourceManager.CreateBuffer(
            HeapType.Upload, bufferSize, ResourceStates.GenericRead);
        void* pData;
        _prevInstanceBoneBuffer->Map(0, null, &pData);
        _mappedPrevInstanceBoneBuffer = (byte*)pData;
        new Span<byte>(_mappedPrevInstanceBoneBuffer, (int)bufferSize).Clear();

        _prevInstanceBoneDescriptorId = Device.DescriptorAllocator.Allocate();
        var cpuHandle = Device.SrvHeapManager.GetCpuHandle(_prevInstanceBoneDescriptorId);
        var srvDesc = new ShaderResourceViewDesc
        {
            Format = Silk.NET.DXGI.Format.FormatUnknown,
            ViewDimension = SrvDimension.Buffer,
            Shader4ComponentMapping = 0x00001688u,
            Buffer = new BufferSrv
            {
                FirstElement = 0,
                NumElements = (uint)(_bonePaletteStride * _prevInstanceBoneCapacity),
                StructureByteStride = (uint)sizeof(Matrix4x4),
                Flags = BufferSrvFlags.None
            }
        };
        Device.D3dDevice->CreateShaderResourceView(_prevInstanceBoneBuffer, &srvDesc, cpuHandle);
        _prevInstanceBoneSrvHandle = Device.SrvHeapManager.GetGpuHandle(_prevInstanceBoneDescriptorId);
    }

    // 2-3 Step C (tier C-b): grow/shrink the previous per-instance
    // morph-weights SB (one float4 per entry; clear on first creation)
    void EnsurePrevMorphWeightsCapacity(int count)
    {
        if (count <= _prevMorphWeightsCapacity)
            return;

        if (_prevMorphWeightsBuffer != null)
        {
            _prevMorphWeightsBuffer->Unmap(0, null);
            _prevMorphWeightsBuffer->Release();
        }
        if (_prevMorphWeightsDescriptorId >= 0)
        {
            Device.DescriptorAllocator.Free(_prevMorphWeightsDescriptorId);
            _prevMorphWeightsDescriptorId = -1;
        }

        _prevMorphWeightsCapacity = count;
        ulong bufferSize = (ulong)(count * sizeof(Vector4));
        _prevMorphWeightsBuffer = Device.ResourceManager.CreateBuffer(
            HeapType.Upload, bufferSize, ResourceStates.GenericRead);
        void* pData;
        _prevMorphWeightsBuffer->Map(0, null, &pData);
        _mappedPrevMorphWeightsBuffer = (byte*)pData;
        new Span<byte>(_mappedPrevMorphWeightsBuffer, (int)bufferSize).Clear();

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
                NumElements = (uint)count,
                StructureByteStride = (uint)sizeof(Vector4),
                Flags = BufferSrvFlags.None
            }
        };
        Device.D3dDevice->CreateShaderResourceView(_prevMorphWeightsBuffer, &srvDesc, cpuHandle);
        _prevMorphWeightsSrvHandle = Device.SrvHeapManager.GetGpuHandle(_prevMorphWeightsDescriptorId);

        // Keep the CPU shadow at the same capacity as the SB
        // (per primitive, indexed by InstanceStreamIndex)
        if (_currentMorphShadow.Length < count)
            _currentMorphShadow = new Vector4[count];
    }

    /// <summary>Uploads per-instance bone matrices into the GPU StructuredBuffer.</summary>
    void UploadInstanceBoneMatrices(int instanceIndex, Matrix4x4[] boneMatrices)
    {
        if (boneMatrices.Length == 0 || _mappedInstanceBoneBuffer == null)
            return;

        int offset = instanceIndex * _bonePaletteStride * sizeof(Matrix4x4);
        int totalSize = sizeof(Matrix4x4) * Math.Min(boneMatrices.Length, _bonePaletteStride);
        fixed (void* matricesPtr = boneMatrices)
        {
            Unsafe.CopyBlock(_mappedInstanceBoneBuffer + offset, matricesPtr, (uint)totalSize);
        }
    }

    protected override GpuDescriptorHandle GetInstanceBoneSrvHandle() => _instanceBoneSrvHandle;
    protected override GpuDescriptorHandle GetPrevBoneSrvHandle() => _prevInstanceBoneSrvHandle;
    protected override GpuDescriptorHandle GetPrevMorphSrvHandle() => _prevMorphWeightsSrvHandle;

    public IReadOnlyList<string> GetAnimationNames()
    {
        return _asset._animationPlayer.GetAnimationNames();
    }

    public void Update(InstancedModel model, float time)
    {
        bool wasInitialized = _transformInitialized;
        _wireframeActive = false;
        _instanceCount = 0;

        // Unified highlighting: clear this frame's per-instance draw lists
        // (rebuilt every frame; _boundsActive / _wireframeActive are set by the
        // per-instance hooks below)
        _boundsActive = false;
        _boundsBoxDrawList.Clear();
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
            // No instances: also turn off Outline2D to avoid leaving the last
            // frame's mask active
            _outline2DHostActive = false;
            SetOutline2DState(false, model.Highlight.OutlineColor, model.Highlight.OutlineWidth);
            _transformInitialized = true;
            SyncAlpha(model.Alpha);
            return;
        }

        EnsureAnimationStateCapacity(model.Instances.Count);
        EnsurePrimitiveInstanceCapacity(_instanceCount);
        EnsureInstanceBoneCapacity((uint)Math.Max(_instanceCount, 1));
        EnsurePrevInstanceBoneCapacity((uint)Math.Max(_instanceCount, 1));
        EnsurePrevMorphWeightsCapacity(_instanceCount);

        bool hasAnimation = _asset._animations.Count > 0;
        bool hasSkin = _skins.Count > 0;
        float deltaTime = Math.Max(time, 0f);

        // 2-3 Step C (tiers B/C-b): before uploading this frame, first copy the
        // current GPU buffer / CPU shadow to the prev side.
        // On the first frame, the current side is still all zero
        // (or the shadow is zero), so the prev side stays zero and the sentinel
        // semantics remain correct.
        if (_mappedPrevInstanceBoneBuffer != null && _mappedInstanceBoneBuffer != null)
        {
            ulong copySize = (ulong)(_bonePaletteStride * _instanceCount * sizeof(Matrix4x4));
            if (copySize > 0)
                Unsafe.CopyBlock((void*)_mappedPrevInstanceBoneBuffer, (void*)_mappedInstanceBoneBuffer, (uint)copySize);
        }
        if (_mappedPrevMorphWeightsBuffer != null && _currentMorphShadow.Length > 0)
        {
            fixed (Vector4* pSrc = _currentMorphShadow)
                Unsafe.CopyBlock(_mappedPrevMorphWeightsBuffer, pSrc,
                    (uint)(_currentMorphShadow.Length * sizeof(Vector4)));
        }

        // 2-3 Step C (tier C-a): copy the previous frame's instance worlds into
        // the prev instance-world SB maintained by the base class.
        // Use the first valid stream.Worlds because all primitives share the
        // same world data source.
        foreach (var stream in _primitiveInstanceStreams)
        {
            if (stream?.Worlds != null && stream.Worlds.Length >= _instanceCount)
            {
                FillPrevInstanceWorldFrom(stream.Worlds, _instanceCount);
                break;
            }
        }

        int writeIndex = 0;

        for (int i = 0; i < model.Instances.Count; i++)
        {
            var instance = model.Instances[i];
            if (!instance.Enable)
            {
                continue;
            }

            // Unified transform convention: route everything through
            // BuildInstanceMatrix (anchor pivot, see InstancedMesh3DBase)
            var instanceWorld = model.BuildInstanceMatrix(instance);
            bool instWire = instance.Highlight.Wireframe;
            _wireframeActive |= instWire;

            // Outline2D (per-instance activation): record the writeIndex slot and
            // per-instance outline color (the per-slot mask fetches color by slot).
            // The first active instance also captures the frame-level composed
            // color / width used by the host path and SetOutline2DState.
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

            // Unified highlighting (per-instance bounds box): use pooled boxes by
            // compressed writeIndex, growing lazily. The draw list is rebuilt
            // every frame. Box alpha / color are independent of the host alpha
            // chain. Boxes with near-zero extents (unloaded / degenerate) stay off.
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

            // Unified highlighting (per-instance wireframe): lazily build shared
            // shell templates plus per-instance shell boxes. Matrices are fetched
            // through the instance-stream writeIndex slot and drawn per instance.
            // Mixed assets draw both shells (rigid + skinned), and the skinned
            // shell follows animation through the per-instance bone-palette path.
            // If neither template is usable (no valid primitives / morph / multiple
            // skins), the box stays null and is not added to the draw list.
            if (instWire)
            {
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

            if (hasAnimation || hasSkin)
            {
                // v2 picking: write a per-instance shadow copy
                // (node worlds + bone palette). Instance playback time and bones
                // are backend-private, so shared-layer picking cannot replay
                // them. This backend writes the shadow each frame for
                // InstancedModel.TryPickInstanceSurface to read. It matches the
                // render source of truth: node world corresponds to the OwnerNode
                // part of finalWorld below.
                EnsureInstancePickShadow(model.Instances.Count);
                var pickNodeWorlds = _asset.InstancePickNodeWorlds[i];
                for (int n = 0; n < _asset.gltfNodes.Count; n++)
                    pickNodeWorlds[n] = _asset.gltfNodes[n].WorldTransform;

                if (hasSkin)
                    _asset._animationPlayer.GetBoneMatricesArray().CopyTo(_asset.InstancePickBones[i], 0);
            }

            foreach (var primitive in _primitives)
            {
                var stream = GetPrimitiveInstanceStream(primitive);
                if (stream == null)
                    continue;

                var finalWorld = (primitive.OwnerNode?.WorldTransform ?? Matrix4x4.Identity) * instanceWorld;
                var instanceData = InstanceTransformData.FromWorld(finalWorld);
                if (primitive.MaterialParams.HasMorphTargets != 0)
                {
                    instanceData.MorphWeights = ExtractMorphWeights(primitive.OwnerNode);
                    // 2-3 Step C (tier C-b): update the CPU shadow immediately
                    // after extraction so it can be uploaded as prev on the next frame
                    int pidx = primitive.InstanceStreamIndex;
                    if ((uint)pidx < (uint)_currentMorphShadow.Length)
                        _currentMorphShadow[pidx] = instanceData.MorphWeights;
                }

                stream.Worlds[writeIndex] = finalWorld;
                stream.Data[writeIndex] = instanceData;
            }

            writeIndex++;
        }

        foreach (var stream in _primitiveInstanceStreams)
        {
            if (stream?.Buffer != null)
                Device.SetVertexBuffer(stream.Buffer, stream.View, stream.Data);
        }

        int fi = (int)Device.FrameIndex;
        var matrices = new MatrixBuffer
        {
            World = Matrix4x4.Transpose(Matrix4x4.Identity),
            View = Matrix4x4.Transpose(DXPrimitiveGroup.Camera.View),
            Projection = Matrix4x4.Transpose(DXPrimitiveGroup.Camera.Projection),
            // 2-3 Step C (tier C-a): previous per-instance world matrices now
            // live in the prev instance-world SB (t9), so b0.PrevWorld stays all
            // zero. The instanced shader path does not read b0 prevWorld.
            PrevViewProjection = Matrix4x4.Transpose(DXPrimitiveGroup.Camera.PrevViewProjection),
        };
        Unsafe.Write(_mappedMatrixBuffers[fi], matrices);

        _transformInitialized = true;

        // 2-3 Step C: from the second frame onward, prev SBs contain valid data,
        // so notify the shader that it may start reading them.
        if (wasInitialized)
        {
            SetPrevInstanceWorldReady();
            SetPrevBonesAndMorphReady();
        }

        // Outline2D active = host-active union any-instance-active.
        // Host activation uses the full mask and ignores the per-instance list.
        // Color / width prefer instance values when any instance is active
        // (the picker writes panel colors there); otherwise they fall back to
        // host values, matching Mesh3D/Model semantics.
        _outline2DHostActive = model.Highlight.Outline;
        bool anyInstanceOutline = _outline2DInstances.Count > 0;
        SetOutline2DState(_outline2DHostActive || anyInstanceOutline,
            anyInstanceOutline ? _outline2DInstanceColor : model.Highlight.OutlineColor,
            anyInstanceOutline ? _outline2DInstanceWidth : model.Highlight.OutlineWidth);

        SyncAlpha(model.Alpha);
    }

    /// <summary>
    /// v2 picking: lazily allocates per-instance picking-shadow arrays, indexed
    /// by the instance-list index and shaped like the animation state arrays.
    /// Enabled only when hasAnimation || hasSkin. Static hosts keep zero cost and
    /// picking falls back to rest-pose node worlds.
    /// </summary>
    void EnsureInstancePickShadow(int instanceCount)
    {
        if (_asset.InstancePickNodeWorlds.Length >= instanceCount)
            return;

        int nodeCount = Math.Max(_asset.gltfNodes.Count, 1);
        int boneStride = Math.Max(_bonePaletteStride, 1);
        var worlds = new Matrix4x4[instanceCount][];
        var bones = new Matrix4x4[instanceCount][];
        for (int i = 0; i < instanceCount; i++)
        {
            worlds[i] = new Matrix4x4[nodeCount];
            bones[i] = new Matrix4x4[boneStride];
        }
        _asset.InstancePickNodeWorlds = worlds;
        _asset.InstancePickBones = bones;
    }

    /// <summary>2-3 Step C (tiers B/C-b): once prev bone + prev morph SBs
    /// contain valid data, sets MaterialParams.HasPrevBones / HasPrevMorph = 1
    /// for every primitive.
    /// This is written only on the first call because later frames keep the same
    /// value and are guarded by early-out. Shell primitives are updated in sync
    /// as well (plan risk 2), otherwise the shell has no trail.</summary>
    void SetPrevBonesAndMorphReady()
    {
        for (int i = 0; i < _primitives.Count; i++)
        {
            var primitive = _primitives[i];
            bool changed = false;
            if (primitive.MaterialParams.HasPrevBones == 0 && _prevInstanceBoneSrvHandle.Ptr != 0)
            {
                primitive.MaterialParams.HasPrevBones = 1;
                changed = true;
            }
            if (primitive.MaterialParams.HasPrevMorph == 0 && _prevMorphWeightsSrvHandle.Ptr != 0)
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
        // Synchronize prev flags for shell primitives too: cover both template
        // sets and both instance-box pools, because pooled boxes may have been
        // created before this frame and still carry stale flags.
        SyncShellPrevFlags(false, _prevInstanceBoneSrvHandle.Ptr != 0, _prevMorphWeightsSrvHandle.Ptr != 0);
    }

    public override void Draw()
    {
        if (!_transformInitialized || _instanceCount == 0)
            return;

        int fi = (int)Device.FrameIndex;
        var lightCB = DXPrimitiveGroup.lightConstantBuffers[fi];
        bool forceFadeByAlpha = _currentAlpha < 1f;

        for (int i = 0; i < _opaquePrimitives.Count; i++)
        {
            var primitive = _opaquePrimitives[i];
            var stream = GetPrimitiveInstanceStream(primitive);
            if (stream == null || stream.Buffer == null)
                continue;

            Pipeline.SetPipeline(forceFadeByAlpha ? PipelineMode.Fade : PipelineMode.Opaque, primitive.DoubleSided);
            fixed (VertexBufferView* instanceVB = &stream.View)
            {
                Pipeline.DrawPrimitive(primitive, lightCB, _matrixBuffers[fi], instanceVB, (uint)_instanceCount, 0,
                    _instanceBoneSrvHandle, _prevInstanceBoneSrvHandle, _prevInstanceWorldSrvHandle, _prevMorphWeightsSrvHandle);
            }
        }

        for (int i = 0; i < _transparentPrimitives.Count; i++)
        {
            var primitive = _transparentPrimitives[i];
            var stream = GetPrimitiveInstanceStream(primitive);
            if (stream == null || stream.Buffer == null)
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

            fixed (VertexBufferView* instanceVB = &stream.View)
            {
                for (int orderIndex = 0; orderIndex < _transparentInstanceOrder.Count; orderIndex++)
                {
                    int instanceIndex = _transparentInstanceOrder[orderIndex];
                    if (primitive.DoubleSided)
                    {
                        Pipeline.SetPipeline(PipelineMode.Transparent, PipelineCullVariant.Front);
                        Pipeline.DrawPrimitive(primitive, lightCB, _matrixBuffers[fi], instanceVB, 1, (uint)instanceIndex,
                            _instanceBoneSrvHandle, _prevInstanceBoneSrvHandle, _prevInstanceWorldSrvHandle, _prevMorphWeightsSrvHandle);
                    }

                    Pipeline.SetPipeline(PipelineMode.Transparent, primitive.DoubleSided ? PipelineCullVariant.Back : PipelineCullVariant.Back);
                    Pipeline.DrawPrimitive(primitive, lightCB, _matrixBuffers[fi], instanceVB, 1, (uint)instanceIndex,
                        _instanceBoneSrvHandle, _prevInstanceBoneSrvHandle, _prevInstanceWorldSrvHandle, _prevMorphWeightsSrvHandle);
                }
            }
        }

        // Unified highlighting: per-instance highlighting
        // (bounds boxes + wireframe shell boxes; enabled instances this frame,
        // with transparent faces in 2-pass mode plus opaque edges, drawn after
        // all regular surfaces). Shell boxes are rendered through the
        // per-primitive instance stream because the base-class _instanceBuffer is
        // all-zero and unusable here. All stream slot layouts are isomorphic, so
        // any live stream works.
        if (_boundsActive)
            DrawBoundsBoxes(lightCB);
        if (_wireframeActive)
        {
            foreach (var stream in _primitiveInstanceStreams)
            {
                if (stream?.Buffer != null)
                {
                    DrawShellBoxes(lightCB, stream.Buffer, stream.View);
                    break;
                }
            }
        }

    }

    /// <summary>
    /// 1-5 shadow pass: this class stores instance data in each primitive's
    /// stream.View. The base-class _instanceBufferView is all-zero and unusable
    /// here, so the base implementation cannot be used: binding a zero view
    /// spams Debug Layer warning #202, instance matrices read as 0, and shadows
    /// disappear.
    /// Otherwise the flow matches the base class: draw all opaque buckets for all
    /// instances and skip transparent buckets.
    /// </summary>
    public override void DrawShadow()
    {
        if (!_transformInitialized || _instanceCount == 0)
            return;

        OnBeforeDraw();

        // Group-invariant t6/t8/t9/t10 are bound once per group.
        // This class changes only VB/IB/matrix CB per primitive.
        Pipeline.SetShadowGroupBindings(_instanceBoneSrvHandle, _prevInstanceBoneSrvHandle,
            _prevInstanceWorldSrvHandle, _prevMorphWeightsSrvHandle);

        // When b2/t5 are identical within the group, only let the first
        // primitive bind them (see CanShareShadowMaterial)
        bool shareMaterial = CanShareShadowMaterial(_opaquePrimitives);
        bool materialBound = false;

        int fi = (int)Device.FrameIndex;
        for (int i = 0; i < _opaquePrimitives.Count; i++)
        {
            var primitive = _opaquePrimitives[i];
            var stream = GetPrimitiveInstanceStream(primitive);
            if (stream == null || stream.Buffer == null)
                continue;

            fixed (VertexBufferView* instanceVB = &stream.View)
            {
                Pipeline.DrawShadowPrimitive(primitive, _matrixBuffers[fi], instanceVB,
                    (uint)_instanceCount, 0, bindMaterial: !shareMaterial || !materialBound);
            }
            // Primitives without an instance stream are skipped by continue and
            // submit no bindings, so mark this only after a real draw.
            materialBound = true;
        }
    }

    /// <summary>
    /// Outline2D mask: drawn through the per-primitive instance stream.
    /// This class stores instance data in each primitive's stream.View, while the
    /// base-class _instanceBufferView is all-zero and unusable.
    /// Host-wide activation draws a single full batch. Per-instance activation
    /// draws instanceCount=1 for each writeIndex slot.
    /// Transparent buckets are skipped, matching the base-class mask semantics.
    /// </summary>
    public override void DrawOutlineMask()
    {
        if (!_transformInitialized || _instanceCount == 0 || !Outline2DActive)
            return;

        // Rewrite outline color per group through root constant b6. Multiple
        // colors may exist in the same frame; colors come from the group color
        // fixed during Update (instance color or host color).
        Pipeline.SetOutlineMaskColor(_outline2DColor);

        int fi = (int)Device.FrameIndex;
        var lightCB = DXPrimitiveGroup.lightConstantBuffers[fi];
        for (int i = 0; i < _opaquePrimitives.Count; i++)
        {
            var primitive = _opaquePrimitives[i];
            var stream = GetPrimitiveInstanceStream(primitive);
            if (stream == null || stream.Buffer == null)
                continue;

            Pipeline.SetPipeline(PipelineMode.Opaque,
                primitive.DoubleSided ? PipelineCullVariant.None : PipelineCullVariant.Back, depthWrite: false);
            OnBeforeDraw();
            fixed (VertexBufferView* instanceVB = &stream.View)
            {
                if (_outline2DHostActive)
                {
                    Pipeline.DrawPrimitive(primitive, lightCB, _matrixBuffers[fi], instanceVB, (uint)_instanceCount, 0,
                        _instanceBoneSrvHandle, _prevInstanceBoneSrvHandle, _prevInstanceWorldSrvHandle, _prevMorphWeightsSrvHandle);
                }
                else
                {
                    for (int k = 0; k < _outline2DInstances.Count; k++)
                    {
                        int idx = _outline2DInstances[k];
                        if ((uint)idx >= (uint)_instanceCount)
                            continue;
                        // boneBase=idx: SV_InstanceID does not include
                        // StartInstanceLocation, so the slot base must be carried
                        // explicitly by a root constant.
                        // Color comes from this slot's own instance OutlineColor.
                        // Multiple colors may coexist in the same frame, and the
                        // mask carries them per pixel.
                        Pipeline.SetOutlineMaskColor(_outline2DInstanceColors[k], (uint)idx);
                        Pipeline.DrawPrimitive(primitive, lightCB, _matrixBuffers[fi], instanceVB, 1, (uint)idx,
                            _instanceBoneSrvHandle, _prevInstanceBoneSrvHandle, _prevInstanceWorldSrvHandle, _prevMorphWeightsSrvHandle);
                    }
                }
            }
        }
    }

    static float ComputeTransparentDepth(Matrix4x4 world, Vector3 localCenter)
    {
        var center = Vector3.Transform(localCenter, world);

        var app = DeviceServices.BaseApp;
        if (app == null)
            return center.Z;

        var forward = app.CameraTarget - app.CameraPos;
        if (forward.LengthSquared() < 1e-6f)
            forward = Vector3.UnitZ;
        else
            forward = Vector3.Normalize(forward);

        return Vector3.Dot(center - app.CameraPos, forward);
    }

    /// <summary>
    /// Releases only owned MaterialBuffers, per-primitive instance buffers,
    /// shared matrix buffers, and bone buffers.
    /// VB/IB/textures are owned by the template model and are not released here.
    /// </summary>
    public override void Dispose()
    {
        ReleasePrimitiveInstanceBuffers();

        foreach (var primitive in _primitives)
        {
            if (primitive.MaterialBuffers != null)
            {
                for (int i = 0; i < primitive.MaterialBuffers.Length; i++)
                {
                    if (primitive.MaterialBuffers[i] != null)
                    {
                        primitive.MaterialBuffers[i]->Unmap(0, null);
                        primitive.MaterialBuffers[i]->Release();
                    }
                }

                primitive.MaterialBuffers = null!;
                primitive.MappedMaterialBuffers = null!;
            }
        }

        _primitives.Clear();
        _opaquePrimitives.Clear();
        _transparentPrimitives.Clear();

        if (_matrixBuffers != null)
        {
            for (int i = 0; i < _matrixBuffers.Length; i++)
            {
                if (_matrixBuffers[i] == null)
                    continue;

                _matrixBuffers[i]->Unmap(0, null);
                _matrixBuffers[i]->Release();
                _matrixBuffers[i] = null;
            }
        }

        _mappedMatrixBuffers = null!;
        _matrixBuffers = null!;
        _primitiveInstanceStreams = Array.Empty<PrimitiveInstanceStream>();
        _animationStates = Array.Empty<InstanceAnimationState>();

        if (_instanceBoneBuffer != null)
        {
            _instanceBoneBuffer->Unmap(0, null);
            _instanceBoneBuffer->Release();
            _instanceBoneBuffer = null;
        }

        if (_instanceBoneDescriptorId >= 0)
        {
            Device.DescriptorAllocator.Free(_instanceBoneDescriptorId);
            _instanceBoneDescriptorId = -1;
        }

        _mappedInstanceBoneBuffer = null;

        // 2-3 Step C (tier B): release the previous per-instance bone SB
        if (_prevInstanceBoneBuffer != null)
        {
            _prevInstanceBoneBuffer->Unmap(0, null);
            _prevInstanceBoneBuffer->Release();
            _prevInstanceBoneBuffer = null;
        }
        if (_prevInstanceBoneDescriptorId >= 0)
        {
            Device.DescriptorAllocator.Free(_prevInstanceBoneDescriptorId);
            _prevInstanceBoneDescriptorId = -1;
        }
        _mappedPrevInstanceBoneBuffer = null;

        // 2-3 Step C (tier C-b): release the previous per-instance morph-weights SB
        if (_prevMorphWeightsBuffer != null)
        {
            _prevMorphWeightsBuffer->Unmap(0, null);
            _prevMorphWeightsBuffer->Release();
            _prevMorphWeightsBuffer = null;
        }
        if (_prevMorphWeightsDescriptorId >= 0)
        {
            Device.DescriptorAllocator.Free(_prevMorphWeightsDescriptorId);
            _prevMorphWeightsDescriptorId = -1;
        }
        _mappedPrevMorphWeightsBuffer = null;
        _currentMorphShadow = Array.Empty<Vector4>();

        // Unified highlighting: release the highlight pool
        // (host bounds box + instance-box pool + wireframe shell boxes /
        // templates / instance shell-box pool)
        DisposeHighlights();
    }
}
