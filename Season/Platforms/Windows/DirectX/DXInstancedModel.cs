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

    // Per-instance bone-matrix StructuredBuffer ring: one slot per frame in flight (see the frame-ring note in
    // DXInstancedPrimitiveGroup). A frame writes its own slot, so the palette it uploads cannot be overwritten by a
    // frame the GPU has not finished reading yet.
    private int _bonePaletteStride = 1;
    /// <summary>Capacity the palette ring starts at. It grows to the real instance count on the first Update, and
    /// pre-allocating one palette per frame slot for a crowd a model never reaches would only buy memory pressure.</summary>
    private const uint InitialInstanceBoneCapacity = 256;
    private ID3D12Resource*[] _instanceBoneBuffers = EmptyResourceRing;
    private byte*[] _mappedInstanceBoneBuffers = EmptyMappedRing;
    private GpuDescriptorHandle[] _instanceBoneSrvHandles = Array.Empty<GpuDescriptorHandle>();
    private int[] _instanceBoneDescriptorIds = Array.Empty<int>();
    private uint _instanceBoneCapacity;

    // 2-3 Step C (tier B): previous per-instance bone palette.
    // This is NOT a second buffer: it is the same ring read one slot back (PrevFrameSlot), because each slot holds
    // the palette of the frame that wrote it. That replaced the per-frame memcpy of the whole palette, and with it a
    // read of write-combined upload memory that cost far more than the write it fed.
    // Cold slots are zeroed before the first frame (sentinel _m33==0), so the shader falls back to current bones
    // joint by joint until a slot has real history.

    // 2-3 Step C (tier C-b): per-instance morph-weights ring (one float4 per entry), same one-slot-per-frame
    // contract as the bone palette; the previous frame is the slot the previous frame wrote.
    private ID3D12Resource*[] _instanceMorphWeightsBuffers = EmptyResourceRing;
    private byte*[] _mappedMorphWeightsBuffers = EmptyMappedRing;
    private GpuDescriptorHandle[] _instanceMorphWeightsSrvHandles = Array.Empty<GpuDescriptorHandle>();
    private int[] _instanceMorphWeightsDescriptorIds = Array.Empty<int>();
    private int _instanceMorphWeightsCapacity;
    // CPU shadow of the current morph weights per primitive, indexed by
    // InstanceStreamIndex. Uploaded into this frame's ring slot once per frame.
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
        /// <summary>One IA buffer per frame in flight; a frame writes and binds its own slot.</summary>
        public ID3D12Resource*[] Buffers = EmptyResourceRing;
        public VertexBufferView[] Views = Array.Empty<VertexBufferView>();
        public InstanceTransformData[] Data = Array.Empty<InstanceTransformData>();
        public Matrix4x4[] Worlds = Array.Empty<Matrix4x4>();
        public int Capacity;

        public ID3D12Resource* BufferAt(int slot)
            => (uint)slot < (uint)Buffers.Length ? Buffers[slot] : null;

        public VertexBufferView ViewAt(int slot)
            => (uint)slot < (uint)Views.Length ? Views[slot] : default;
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
    public void Load(DXModel template, Season.Controls.Model model)
    {
        CreateSharedMatrixBuffers();

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

        // Step 5: create the per-instance bone-matrix StructuredBuffer ring
        EnsureInstanceBoneCapacity(InitialInstanceBoneCapacity);

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

    /// <summary>
    /// Grows every primitive's IA instance stream. Each one is a ring with a slot per frame in flight, for the same
    /// reason as the group-level instance buffer: the GPU may still be reading the slot this frame is about to write.
    /// </summary>
    void EnsurePrimitiveInstanceCapacity(int count)
    {
        if (_primitiveInstanceStreams.Length == 0)
            return;

        for (int i = 0; i < _primitiveInstanceStreams.Length; i++)
        {
            var stream = _primitiveInstanceStreams[i];
            if (stream.Capacity >= count && stream.Buffers.Length == FrameSlotCount)
                continue;

            ReleaseRingDeferred(stream.Buffers);

            int slots = FrameSlotCount;
            var buffers = new ID3D12Resource*[slots];
            var views = new VertexBufferView[slots];
            for (int slot = 0; slot < slots; slot++)
                buffers[slot] = Device.CreateVertexBuffer<InstanceTransformData>((uint)count, out views[slot]);

            stream.Buffers = buffers;
            stream.Views = views;
            stream.Capacity = count;
            stream.Data = new InstanceTransformData[count];
            stream.Worlds = new Matrix4x4[count];
        }
    }

    void ReleasePrimitiveInstanceBuffers()
    {
        foreach (var stream in _primitiveInstanceStreams)
        {
            if (stream != null)
                ReleaseRingDeferred(stream.Buffers);

            if (stream != null)
            {
                stream.Buffers = EmptyResourceRing;
                stream.Views = Array.Empty<VertexBufferView>();
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

    /// <summary>
    /// A7: every opaque primitive draws its own instance stream (node world × instance world, plus its own morph weights), so gather
    /// materializes one reordered stream per opaque primitive. The visibility decision itself is made once per instance from the
    /// root instance worlds in the base <see cref="DXInstancedPrimitiveGroup._instanceData"/> against the model-space clip-union box.
    /// </summary>
    protected override bool CullPerPrimitiveStreams => true;

    protected override bool TryGetCullDrawStreamRing(int stream, out ID3D12Resource*[] ring, out int capacity)
    {
        var s = (uint)stream < (uint)_opaquePrimitives.Count
            ? GetPrimitiveInstanceStream(_opaquePrimitives[stream])
            : null;
        ring = s?.Buffers ?? EmptyResourceRing;
        capacity = s?.Capacity ?? 0;
        return ring.Length > 0 && capacity > 0;
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

    /// <summary>Creates the per-instance bone-matrix StructuredBuffer ring on the
    /// upload heap. Size follows the real instance count: the palette can be
    /// megabytes per slot, so pre-allocating for a crowd a model never reaches
    /// would only buy memory pressure.</summary>
    void EnsureInstanceBoneCapacity(uint capacity)
    {
        if (capacity <= _instanceBoneCapacity && _instanceBoneBuffers.Length == FrameSlotCount)
            return;

        ReleaseBoneRingDeferred(_instanceBoneBuffers, _instanceBoneDescriptorIds);

        int slots = FrameSlotCount;
        ulong bufferSize = (ulong)(_bonePaletteStride * capacity * sizeof(Matrix4x4));
        var buffers = new ID3D12Resource*[slots];
        var mapped = new byte*[slots];
        var handles = new GpuDescriptorHandle[slots];
        var descriptorIds = new int[slots];

        for (int slot = 0; slot < slots; slot++)
        {
            var buffer = Device.ResourceManager.CreateBuffer(HeapType.Upload, bufferSize, ResourceStates.GenericRead);
            void* pData;
            buffer->Map(0, null, &pData);
            mapped[slot] = (byte*)pData;
            // Zero every slot, not just the first: the prev-bone sentinel (Bh[3][3] == 0) is what makes a slot with no
            // history yet fall back to this frame's bones instead of reading whatever the driver handed back.
            new Span<byte>(mapped[slot], (int)bufferSize).Clear();

            descriptorIds[slot] = Device.DescriptorAllocator.Allocate();
            var srvDesc = new ShaderResourceViewDesc
            {
                Format = Silk.NET.DXGI.Format.FormatUnknown,
                ViewDimension = SrvDimension.Buffer,
                Shader4ComponentMapping = 0x00001688u,
                Buffer = new BufferSrv
                {
                    FirstElement = 0,
                    NumElements = (uint)(_bonePaletteStride * capacity),
                    StructureByteStride = (uint)sizeof(Matrix4x4),
                    Flags = BufferSrvFlags.None
                }
            };
            Device.D3dDevice->CreateShaderResourceView(buffer, &srvDesc, Device.SrvHeapManager.GetCpuHandle(descriptorIds[slot]));
            handles[slot] = Device.SrvHeapManager.GetGpuHandle(descriptorIds[slot]);
            buffers[slot] = buffer;
        }

        _instanceBoneBuffers = buffers;
        _mappedInstanceBoneBuffers = mapped;
        _instanceBoneSrvHandles = handles;
        _instanceBoneDescriptorIds = descriptorIds;
        _instanceBoneCapacity = capacity;
    }

    /// <summary>2-3 Step C (tier C-b): grow the previous per-instance morph-weights ring (one float4 per entry).</summary>
    void EnsureMorphWeightsCapacity(int count)
    {
        if (count <= _instanceMorphWeightsCapacity && _instanceMorphWeightsBuffers.Length == FrameSlotCount)
            return;

        ReleaseBoneRingDeferred(_instanceMorphWeightsBuffers, _instanceMorphWeightsDescriptorIds);

        int slots = FrameSlotCount;
        ulong bufferSize = (ulong)(count * sizeof(Vector4));
        var buffers = new ID3D12Resource*[slots];
        var mapped = new byte*[slots];
        var handles = new GpuDescriptorHandle[slots];
        var descriptorIds = new int[slots];

        for (int slot = 0; slot < slots; slot++)
        {
            var buffer = Device.ResourceManager.CreateBuffer(HeapType.Upload, bufferSize, ResourceStates.GenericRead);
            void* pData;
            buffer->Map(0, null, &pData);
            mapped[slot] = (byte*)pData;
            new Span<byte>(mapped[slot], (int)bufferSize).Clear();

            descriptorIds[slot] = Device.DescriptorAllocator.Allocate();
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
            Device.D3dDevice->CreateShaderResourceView(buffer, &srvDesc, Device.SrvHeapManager.GetCpuHandle(descriptorIds[slot]));
            handles[slot] = Device.SrvHeapManager.GetGpuHandle(descriptorIds[slot]);
            buffers[slot] = buffer;
        }

        _instanceMorphWeightsBuffers = buffers;
        _mappedMorphWeightsBuffers = mapped;
        _instanceMorphWeightsSrvHandles = handles;
        _instanceMorphWeightsDescriptorIds = descriptorIds;
        _instanceMorphWeightsCapacity = count;

        // Keep the CPU shadow at the same capacity as the ring
        // (per primitive, indexed by InstanceStreamIndex)
        if (_currentMorphShadow.Length < count)
            _currentMorphShadow = new Vector4[count];
    }

    /// <summary>Retires a ring together with its SRV descriptors. Both halves have to wait for the fence: a frame in
    /// flight may still be reading through the descriptor and from the resource behind it.</summary>
    static void ReleaseBoneRingDeferred(ID3D12Resource*[] ring, int[] descriptorIds)
    {
        ReleaseRingDeferred(ring);
        if (descriptorIds == null || descriptorIds.Length == 0)
            return;
        ulong fence = Device.GetCurrentRetireFenceValue();
        Device.EnqueueDeferredRelease(fence, () =>
        {
            foreach (int id in descriptorIds)
            {
                if (id >= 0)
                    Device.DescriptorAllocator.Free(id);
            }
        });
    }

    /// <summary>Uploads per-instance bone matrices into this frame's ring slot.</summary>
    void UploadInstanceBoneMatrices(int instanceIndex, Matrix4x4[] boneMatrices)
    {
        int slot = FrameSlot;
        if (boneMatrices.Length == 0 || (uint)slot >= (uint)_mappedInstanceBoneBuffers.Length)
            return;

        byte* destination = _mappedInstanceBoneBuffers[slot];
        if (destination == null)
            return;

        int offset = instanceIndex * _bonePaletteStride * sizeof(Matrix4x4);
        int totalSize = sizeof(Matrix4x4) * Math.Min(boneMatrices.Length, _bonePaletteStride);
        fixed (void* matricesPtr = boneMatrices)
        {
            Unsafe.CopyBlock(destination + offset, matricesPtr, (uint)totalSize);
        }
    }

    /// <summary>2-3 Step C (tier C-b): publishes this frame's morph shadow into this frame's ring slot. The previous
    /// frame is the slot that frame wrote, so no copy between the two exists.</summary>
    void UploadMorphWeightsForFrame()
    {
        int slot = FrameSlot;
        if ((uint)slot >= (uint)_mappedMorphWeightsBuffers.Length)
            return;
        byte* destination = _mappedMorphWeightsBuffers[slot];
        if (destination == null)
            return;

        int count = Math.Min(_currentMorphShadow.Length, _instanceMorphWeightsCapacity);
        if (count <= 0)
            return;
        fixed (Vector4* pSrc = _currentMorphShadow)
            Unsafe.CopyBlock(destination, pSrc, (uint)(count * sizeof(Vector4)));
    }

    protected override GpuDescriptorHandle GetInstanceBoneSrvHandle()
    {
        int slot = FrameSlot;
        if ((uint)slot >= (uint)_instanceBoneBuffers.Length)
            return default;
        return DXSceneDrawCollection.Structured(_instanceBoneBuffers[slot], 64, _instanceBoneSrvHandles[slot]);
    }

    /// <summary>2-3 Step C (tier B): the previous frame's palette is the same ring one slot back.</summary>
    protected override GpuDescriptorHandle GetPrevBoneSrvHandle()
    {
        int slot = PrevFrameSlot;
        if ((uint)slot >= (uint)_instanceBoneBuffers.Length)
            return default;
        return DXSceneDrawCollection.Structured(_instanceBoneBuffers[slot], 64, _instanceBoneSrvHandles[slot]);
    }

    /// <summary>2-3 Step C (tier C-b): the previous frame's morph weights are the same ring one slot back.</summary>
    protected override GpuDescriptorHandle GetPrevMorphSrvHandle()
    {
        int slot = PrevFrameSlot;
        if ((uint)slot >= (uint)_instanceMorphWeightsBuffers.Length)
            return default;
        return DXSceneDrawCollection.Structured(_instanceMorphWeightsBuffers[slot], 16, _instanceMorphWeightsSrvHandles[slot]);
    }

    public IReadOnlyList<string> GetAnimationNames()
    {
        return _asset._animationPlayer.GetAnimationNames();
    }

    public void Update(InstancedModel model, float time)
    {
        bool wasInitialized = _transformInitialized;
        _wireframeActive = false;
        _instanceCount = 0;
        // Latched before the early return below, so a frame with no instances can never inherit last frame's indirect authorization.
        BeginCullFrame(model);

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
        // This class never draws from the group-level IA buffer, but t9 (previous instance world) lives in the base
        // class's ring, so that ring has to be sized here rather than by EnsureInstanceBufferCapacity.
        EnsureInstanceWorldCapacity(_instanceCount);
        EnsureInstanceBoneCapacity((uint)Math.Max(_instanceCount, 1));
        EnsureMorphWeightsCapacity(_instanceCount);

        // A7 cull input: one root instance world per enabled instance, in write order. The per-primitive streams below hold
        // "node world × instance world", which differs per primitive, so none of them can stand in for the instance as a whole;
        // the cull box (model.TemplateCullBounds) is in the same model space as this root world.
        if (_instanceData.Length != _instanceCount)
            _instanceData = new InstanceTransformData[_instanceCount];

        bool hasAnimation = _asset._animations.Count > 0;
        bool hasSkin = _skins.Count > 0;
        float deltaTime = Math.Max(time, 0f);

        // 2-3 Step C (tiers B/C-b): nothing is copied here any more. Each GPU ring above holds one frame in flight per
        // slot, so "the previous frame's palette / morph weights" is simply the slot the previous frame wrote, and the
        // shader reads it through PrevFrameSlot. The old code memcpy'd the whole palette forward once per frame, and on
        // D3D12 that read from write-combined upload memory -- the one access mode the CPU performs an order of
        // magnitude slower -- to produce bytes the ring already held.

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
            _instanceData[writeIndex] = InstanceTransformData.FromWorld(instanceWorld);
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
                    // 2-3 Step C (tier C-b): update the CPU shadow immediately after extraction, so it can be published as
                    // this frame's history below. The destination is the compacted instance slot (the same slot the VS reads as
                    // instanceSlot), never the primitive's stream index: a model with several morphing primitives would otherwise
                    // scatter each instance's weights into a different slot, and the history stream would hold one primitive's
                    // last instance in place of every instance's own weights.
                    if ((uint)writeIndex < (uint)_currentMorphShadow.Length)
                        _currentMorphShadow[writeIndex] = instanceData.MorphWeights;
                }

                stream.Worlds[writeIndex] = finalWorld;
                stream.Data[writeIndex] = instanceData;
            }

            writeIndex++;
        }

        int slotIndex = FrameSlot;
        Matrix4x4[]? frameWorlds = null;
        foreach (var stream in _primitiveInstanceStreams)
        {
            var instanceBuffer = stream?.BufferAt(slotIndex);
            if (instanceBuffer == null)
                continue;

            Device.SetVertexBuffer(instanceBuffer, stream!.ViewAt(slotIndex), stream.Data);
            // 2-3 Step C (tier C-a): publish this frame's worlds into this frame's ring slot. The previous frame's worlds
            // are the slot that frame wrote, so nothing is copied between the two.
            frameWorlds ??= stream.Worlds;
        }

        if (frameWorlds != null)
            UploadInstanceWorldsForFrame(frameWorlds, _instanceCount);

        // A7: the box must be in the root instance's model space and cover every clip pose, because the kernel culls the whole
        // instance with it once and every primitive stream only replays that decision.
        SetCullLocalBounds(model.TemplateCullBounds);
        EnsureCullResources(_instanceCount);

        int fi = (int)Device.FrameIndex;
        var matrices = new MatrixBuffer
        {
            World = Matrix4x4.Transpose(Matrix4x4.Identity),
            // 2-3 Step C (tier C-a): previous per-instance world matrices now
            // live in the prev instance-world SB (t9), so b0.PrevWorld stays all
            // zero. The instanced shader path does not read b0 prevWorld.
        };
        Unsafe.Write(_mappedMatrixBuffers[fi], matrices);

        _transformInitialized = true;

        // 2-3 Step C: publishes whether the previous-frame streams may be read this frame. Not a one-shot call any more: the
        // history is addressed by the compacted instance slot, so it only lines up while the enabled order is unchanged
        // (see InstancedMesh3DBase.InstanceHistoryValid). The first frame after load has no history and passes false.
        UpdatePrevStateFlags(wasInitialized && model.InstanceHistoryValid);

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

    /// <summary>2-3 Step C (tiers B/C-b): this path owns both history streams once their rings exist, so the base class can
    /// publish all three flags from one place.</summary>
    protected override bool HasPrevBoneHistory => GetPrevBoneSrvHandle().Ptr != 0;

    /// <inheritdoc cref="HasPrevBoneHistory"/>
    protected override bool HasPrevMorphHistory => GetPrevMorphSrvHandle().Ptr != 0;

    public override void Draw()
    {
        if (!_transformInitialized || _instanceCount == 0)
            return;

        int fi = (int)Device.FrameIndex;
        var lightCB = DXPrimitiveGroup.lightConstantBuffers[fi];
        bool forceFadeByAlpha = _currentAlpha < 1f;

        // A7 main view: each opaque primitive reads the main-view segment of its own reordered stream, with the instance count from
        // the args entry (MainViewSlot, i). The bone / prev-bone / prev-world / prev-morph tables need no reordering of their own:
        // the tag gather writes into each record IS the compacted instance slot the cull kernel numbered, which is exactly how those
        // tables are laid out, so the reordered rows line up row for row and skinned / morphing models need nothing extra here.
        // Fade stays direct for the same reason as the base class (per-instance back-to-front splitting).
        for (int i = 0; i < _opaquePrimitives.Count; i++)
        {
            var primitive = _opaquePrimitives[i];
            var stream = GetPrimitiveInstanceStream(primitive);
            var streamBuffer = stream?.BufferAt(FrameSlot);
            if (stream == null || streamBuffer == null)
                continue;

            if (!forceFadeByAlpha && TryGetMainViewSegment(i, out var segment, out var sortedResource, out var argsResource))
            {
                Pipeline.SetPipeline(PipelineMode.Opaque, primitive.DoubleSided);
                Pipeline.DrawPrimitive(primitive, lightCB, _matrixBuffers[fi], &segment, 0, 0,
                    GetInstanceBoneSrvHandle(), GetPrevBoneSrvHandle(), GetPrevInstanceWorldSrvHandle(), GetPrevMorphSrvHandle(),
                    sortedResource, argsResource,
                    Season.Rendering.InstanceCull.ArgsOffset(Season.Rendering.InstanceCull.MainViewSlot, i), streamBuffer);
                Season.Rendering.InstanceCull.MainViewDraws++;
                continue;
            }

            Pipeline.SetPipeline(forceFadeByAlpha ? PipelineMode.Fade : PipelineMode.Opaque, primitive.DoubleSided);
            var instanceVBValue = stream.ViewAt(FrameSlot);
            VertexBufferView* instanceVB = &instanceVBValue;
            Pipeline.DrawPrimitive(primitive, lightCB, _matrixBuffers[fi], instanceVB, (uint)_instanceCount, 0,
                GetInstanceBoneSrvHandle(), GetPrevBoneSrvHandle(), GetPrevInstanceWorldSrvHandle(), GetPrevMorphSrvHandle(), streamBuffer);
        }

        for (int i = 0; i < _transparentPrimitives.Count; i++)
        {
            var primitive = _transparentPrimitives[i];
            var stream = GetPrimitiveInstanceStream(primitive);
            if (stream == null || stream.BufferAt(FrameSlot) == null)
                continue;

            _transparentInstanceOrder.Clear();
            for (int instanceIndex = 0; instanceIndex < _instanceCount; instanceIndex++)
                _transparentInstanceOrder.Add(instanceIndex);

            if (DXSceneDrawCollection.Active == null)
                _transparentInstanceOrder.Sort((a, b) =>
                {
                    float depthA = ComputeTransparentDepth(stream.Worlds[a], primitive.LocalBoundsCenter);
                    float depthB = ComputeTransparentDepth(stream.Worlds[b], primitive.LocalBoundsCenter);
                    return depthB.CompareTo(depthA);
                });

            var transparentVBValue = stream.ViewAt(FrameSlot);
            VertexBufferView* instanceVB = &transparentVBValue;
            var transparentBuffer = stream.BufferAt(FrameSlot);
            for (int orderIndex = 0; orderIndex < _transparentInstanceOrder.Count; orderIndex++)
            {
                int instanceIndex = _transparentInstanceOrder[orderIndex];
                if (primitive.DoubleSided)
                {
                    Pipeline.SetPipeline(PipelineMode.Transparent, PipelineCullVariant.Front);
                    Pipeline.DrawPrimitive(primitive, lightCB, _matrixBuffers[fi], instanceVB, 1, (uint)instanceIndex,
                        GetInstanceBoneSrvHandle(), GetPrevBoneSrvHandle(), GetPrevInstanceWorldSrvHandle(), GetPrevMorphSrvHandle(), transparentBuffer);
                }

                Pipeline.SetPipeline(PipelineMode.Transparent, primitive.DoubleSided ? PipelineCullVariant.Back : PipelineCullVariant.Back);
                Pipeline.DrawPrimitive(primitive, lightCB, _matrixBuffers[fi], instanceVB, 1, (uint)instanceIndex,
                    GetInstanceBoneSrvHandle(), GetPrevBoneSrvHandle(), GetPrevInstanceWorldSrvHandle(), GetPrevMorphSrvHandle(), transparentBuffer);
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
                var streamBuffer = stream?.BufferAt(FrameSlot);
                if (streamBuffer != null)
                {
                    DrawShellBoxes(lightCB, streamBuffer, stream!.ViewAt(FrameSlot));
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
        Pipeline.SetShadowGroupBindings(GetInstanceBoneSrvHandle(), GetPrevBoneSrvHandle(),
            GetPrevInstanceWorldSrvHandle(), GetPrevMorphSrvHandle());

        // When b2/t5 are identical within the group, only let the first
        // primitive bind them (see CanShareShadowMaterial)
        bool shareMaterial = CanShareShadowMaterial(_opaquePrimitives);
        bool materialBound = false;

        int fi = (int)Device.FrameIndex;
        for (int i = 0; i < _opaquePrimitives.Count; i++)
        {
            var primitive = _opaquePrimitives[i];
            var stream = GetPrimitiveInstanceStream(primitive);
            if (stream == null || stream.BufferAt(FrameSlot) == null)
                continue;

            // A7: this slot's segment of primitive i's own reordered stream, instance count from args (slot, i). Decided per
            // primitive, but every stream is built in the same PrepareCullDraw, so in practice the whole group goes one way.
            if (TryGetShadowSegment(i, out var slotView, out var argsResource, out int slot))
            {
                Pipeline.DrawShadowPrimitiveIndirect(primitive, _matrixBuffers[fi], &slotView,
                    argsResource, Season.Rendering.InstanceCull.ArgsOffset(slot, i),
                    bindMaterial: !shareMaterial || !materialBound);
                materialBound = true;
                continue;
            }

            var shadowVBValue = stream.ViewAt(FrameSlot);
            VertexBufferView* instanceVB = &shadowVBValue;
            Pipeline.DrawShadowPrimitive(primitive, _matrixBuffers[fi], instanceVB,
                (uint)_instanceCount, 0, bindMaterial: !shareMaterial || !materialBound);
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
            if (stream == null || stream.BufferAt(FrameSlot) == null)
                continue;

            var boneSrv = GetInstanceBoneSrvHandle();
            var prevBoneSrv = GetPrevBoneSrvHandle();
            var prevWorldSrv = GetPrevInstanceWorldSrvHandle();
            var prevMorphSrv = GetPrevMorphSrvHandle();

            Pipeline.SetPipeline(PipelineMode.Opaque,
                primitive.DoubleSided ? PipelineCullVariant.None : PipelineCullVariant.Back, depthWrite: false);
            OnBeforeDraw();
            var maskVBValue = stream.ViewAt(FrameSlot);
            VertexBufferView* instanceVB = &maskVBValue;
            if (_outline2DHostActive)
            {
                Pipeline.DrawPrimitive(primitive, lightCB, _matrixBuffers[fi], instanceVB, (uint)_instanceCount, 0,
                    boneSrv, prevBoneSrv, prevWorldSrv, prevMorphSrv);
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
                        boneSrv, prevBoneSrv, prevWorldSrv, prevMorphSrv);
                }
            }
        }
    }

    static float ComputeTransparentDepth(Matrix4x4 world, Vector3 localCenter)
    {
        return RenderView.SortDepth(Vector3.Transform(localCenter, world));
    }

    /// <summary>
    /// Releases only owned MaterialBuffers, per-primitive instance buffers,
    /// shared matrix buffers, and bone buffers.
    /// VB/IB/textures are owned by the template model and are not released here.
    /// </summary>
    public override void Dispose()
    {
        // This override does not chain to the base Dispose, so the A7 buffers (and the registration that would otherwise keep a
        // disposed group in the cull dispatch list) must be released here explicitly.
        ReleaseCullResources();
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

        ReleaseBoneRingDeferred(_instanceBoneBuffers, _instanceBoneDescriptorIds);
        _instanceBoneBuffers = EmptyResourceRing;
        _mappedInstanceBoneBuffers = EmptyMappedRing;
        _instanceBoneSrvHandles = Array.Empty<GpuDescriptorHandle>();
        _instanceBoneDescriptorIds = Array.Empty<int>();
        _instanceBoneCapacity = 0;

        ReleaseBoneRingDeferred(_instanceMorphWeightsBuffers, _instanceMorphWeightsDescriptorIds);
        _instanceMorphWeightsBuffers = EmptyResourceRing;
        _mappedMorphWeightsBuffers = EmptyMappedRing;
        _instanceMorphWeightsSrvHandles = Array.Empty<GpuDescriptorHandle>();
        _instanceMorphWeightsDescriptorIds = Array.Empty<int>();
        _instanceMorphWeightsCapacity = 0;
        _currentMorphShadow = Array.Empty<Vector4>();

        // Unified highlighting: release the highlight pool
        // (host bounds box + instance-box pool + wireframe shell boxes /
        // templates / instance shell-box pool)
        DisposeHighlights();
    }
}
