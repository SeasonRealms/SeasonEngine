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

    /// <summary>
    /// P1: the GPU skinning route's frame state - the palette ring t6/t8 read, the load-time bake the kernel evaluates, and the 16-byte
    /// per-instance parameter block. Shared with the other three backends so the layout cannot drift between them; this class only
    /// supplies the ring, the bake, the clock and the frame's slot numbers.
    /// </summary>
    private readonly Season.Rendering.InstanceSkinningTarget _skinning = new();

    /// <summary>
    /// True while this frame runs the GPU route. Latched once per Update, so a frame cannot half-switch between the two palettes.
    /// False is the historical path, untouched: <c>Evaluate</c> per instance, the palette written straight into the mapped ring by
    /// <see cref="UploadInstanceBoneMatrices"/>, and no storage buffer allocated at all.
    /// </summary>
    private bool _gpuSkin;

    /// <summary>
    /// The palette ring this class created because the model asked for the GPU route without supplying one (see
    /// <see cref="Season.Controls.InstancedModel.BoneStorage"/>). Owned here, disposed here; a ring the model supplied is never touched.
    /// </summary>
    private Season.Rendering.InstanceBoneStorage? _ownedPalette;

    /// <summary>
    /// The palette the GPU route renders with the very first frame, captured at load while the node tree is still at rest. Picking has
    /// no way to replay the kernel's evaluation on the CPU, so on the GPU route it reports this pose instead of a stale or zeroed one -
    /// a documented L0 boundary, not a silent approximation.
    /// </summary>
    private Matrix4x4[] _restPalette = Array.Empty<Matrix4x4>();

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

    // P1: last sampled pose per instance (owner-node worlds / morph weights / bone palette). A throttled frame never
    // evaluates an instance's clip, and the shared node tree cannot hold every instance's pose at once, so the streams of
    // a skipped instance are rebuilt from here instead. Rows are keyed by the Instances index, see the cache's contract.
    private readonly InstancePoseCache _poseCache = new();

    // P2: compacted slots that just re-entered the main camera's frustum. Their previous-frame world and morph weights are
    // republished from this frame's values after the loop, so the motion-vector pass reads zero velocity across the frames the
    // instance spent culled instead of one frame of the distance it travelled. Rebuilt every Update.
    private readonly List<int> _historyResetSlots = new();

    // P3 L2: the pose pool. Lazy bake, but along the instance axis rather than time: a group of instances that P3a decided shares one
    // (clip, quantized tick) sample is evaluated once into a palette block of its own, and every member's tag row points at that
    // block. The pool is rebuilt every frame, so its size is bounded by this frame's instance count and never by how many clips the
    // asset has - which is the whole difference from pre-baking poses at load.
    //
    // The representative is chosen by P3a (the first instance to register the key) and is not known to be one until a second
    // instance asks for it, so the plan is recorded here and written back after the loop; a representative's parameter row is
    // already uploaded by then and only gains its pool row.
    private readonly Dictionary<int, int> _posePoolBucketBySource = new();
    private readonly List<(int Write, uint Clip, float Time)> _posePoolRewrites = new();
    /// <summary>Parameter rows the loop above produced, indexed by the Instances index, so a bucket found later in the same replay
    /// can address the representative without assuming the write index equals the Instances index.</summary>
    private (int Write, uint Clip, float Time)[] _posePoolPending = Array.Empty<(int, uint, float)>();

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
    /// <paramref name="skinningMode"/> is the owning control's <c>SkinningMode</c>, copied in because the loaded template and the
    /// control only ever meet here: it decides whether a GPU bake is even attempted, and a model that declines keeps the CPU route.
    /// </summary>
    public void Load(DXModel template, Season.Controls.Model model, InstanceSkinningMode skinningMode = InstanceSkinningMode.Cpu)
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
        // P2 L1 / P3 L2: one extra palette row per instance block for the pose-pool tag. The joints keep their historical
        // instanceSlot * stride address, so the extra row only moves where the vertex stage looks for the tag - it never shifts
        // a joint, which is what lets this layout change ship without touching a byte of what the draw reads.
        _bonePaletteStride = Math.Max(1, _skins.Sum(s => s.Joints.Count)) + InstanceSkinning.PoseTagRows;

        // Step 4b (P1): build the GPU route's immutable tables now, while the node tree is still at rest and no instance has been
        // evaluated. TryBake declines - leaving SkinBake null and every instance on the CPU route - for a model whose animation moves
        // a node that is not a joint, whose skin bind node moves, or whose tables would exceed the size ceiling. Declining is a
        // property of the asset, not an error, and it costs one pass over the clips.
        InstanceSkinBake.TryBake(_asset, skinningMode);
        if (_asset.SkinBake != null && _skins.Count > 0)
        {
            // The GPU route's picking shadow, for the same reason as above: rest is the only pose the CPU can still name.
            _asset._animationPlayer.UpdateBoneMatrices(_skins);
            _restPalette = (Matrix4x4[])_asset._animationPlayer.GetBoneMatricesArray().Clone();
        }

        _primitiveInstanceStreams = new PrimitiveInstanceStream[_primitives.Count];
        for (int i = 0; i < _primitiveInstanceStreams.Length; i++)
            _primitiveInstanceStreams[i] = new PrimitiveInstanceStream();

        SyncInstancedSkinningMaterialParams();

        // Step 5: create the per-instance bone-matrix StructuredBuffer ring
        EnsureInstanceBoneCapacity(InitialInstanceBoneCapacity);

        RebuildPrimitiveBuckets();
        SyncAlpha(model.Alpha);

        // P1: the skinning scheduler holds this group for its lifetime, because the mode can be flipped per model after load (and the
        // A/B switch is process-wide). A frame that runs the CPU route simply reports nothing and is skipped.
        Season.Rendering.InstanceSkinning.Register(_skinning);
    }

    /// <summary>
    /// Applies <see cref="Season.Controls.InstancedModel.InstanceTintParts"/> to the cloned primitives: matching ones get
    /// <c>TintMode = 1</c> and their authored RGB rewritten to white.
    /// <para>
    /// The white rewrite is not cosmetic. The per-instance color multiplies the albedo, so a saturated authored factor
    /// (this asset's sail is 0.97/0.93/0.0003) could only ever be darkened toward black, never turned green or blue.
    /// Setting RGB to 1 turns the same multiply into a replacement, which is what lets one geometry pass through all
    /// seven hues. Alpha is untouched: opacity stays with the material and the control's own Alpha chain.
    /// </para>
    /// <para>
    /// Called once after <see cref="Load"/>, while no draw can see the material yet.
    /// </para>
    /// </summary>
    internal void ApplyInstanceTint(Season.Controls.InstancedModel owner)
    {
        if (owner.InstanceTintParts.Count == 0)
            return;

        for (int i = 0; i < _primitives.Count; i++)
        {
            var primitive = _primitives[i];
            if (!owner.IsInstanceTintPart(primitive.PartName, primitive.PartNodeName))
                continue;

            primitive.MaterialParams.TintMode = 1u;
            primitive.MaterialParams.BaseColor = new Vector4(1f, 1f, 1f, primitive.MaterialParams.BaseColor.W);
            for (int f = 0; f < Device.frameCount; f++)
                Unsafe.Write(primitive.MappedMaterialBuffers[f], primitive.MaterialParams);
        }
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
            // Part identity travels with the clone: the owner's InstanceTintParts resolves against exactly these two
            // names after loading, the same way Model.SetPartColor resolves against them on the non-instanced path.
            PartName = source.PartName,
            PartNodeName = source.PartNodeName,
            PartBaseColor = source.PartBaseColor,
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

        ReleaseBoneRingDeferred(_instanceBoneBuffers, _instanceBoneDescriptorIds, _mappedInstanceBoneBuffers);

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

            descriptorIds[slot] = Device.DescriptorAllocator.Allocate(DescriptorKind.InstanceRing);
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

        ReleaseBoneRingDeferred(_instanceMorphWeightsBuffers, _instanceMorphWeightsDescriptorIds, _mappedMorphWeightsBuffers);

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

            descriptorIds[slot] = Device.DescriptorAllocator.Allocate(DescriptorKind.InstanceRing);
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
    /// flight may still be reading through the descriptor and from the resource behind it. <paramref name="mapped"/> is
    /// forwarded to <see cref="ReleaseRingDeferred"/> so only a live mapping is ever unmapped, and both the ring and the
    /// id array are detached first, which makes a repeated retirement (shutdown after a failed grow) a no-op instead of
    /// a second Unmap or a second Free.</summary>
    static void ReleaseBoneRingDeferred(ID3D12Resource*[] ring, int[] descriptorIds, byte*[]? mapped = null)
    {
        ReleaseRingDeferred(ring, mapped);
        if (descriptorIds == null || descriptorIds.Length == 0)
            return;

        // -1 rather than 0 marks the retired entries, because 0 is a valid descriptor index.
        var retiredIds = new int[descriptorIds.Length];
        Array.Copy(descriptorIds, retiredIds, descriptorIds.Length);
        Array.Fill(descriptorIds, -1);

        ulong fence = Device.GetCurrentRetireFenceValue();
        Device.EnqueueDeferredRelease(fence, () =>
        {
            foreach (int id in retiredIds)
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

        // P2 L1 / P3 L2: the tag row is the one row of the block this upload does not fill from the pose, and it has to read zero
        // here. A slot that ran the GPU route on an earlier frame still carries the tag the kernel wrote, and a non-zero tag would
        // send the vertex stage to a pool block this route never wrote. Cleared unconditionally rather than only when the route
        // changed, so there is one rule for the row instead of two.
        byte* tagRow = destination + offset + (long)(_bonePaletteStride - InstanceSkinning.PoseTagRows) * sizeof(Matrix4x4);
        *(uint*)(tagRow + InstanceSkinning.PoseTagCurrentOffset) = 0;
        *(uint*)(tagRow + InstanceSkinning.PoseTagPreviousOffset) = 0;
    }

    /// <summary>
    /// P3a: a deduplicated instance does not replay its clip, so the backend's "rewrite the picking snapshot on every sampled
    /// frame" rule would leave its snapshot describing the pose it had before the copy. The snapshot is part of the rendered
    /// pose, so it follows the cache row.
    /// </summary>
    void CopyInstancePickPose(int source, int destination, bool hasSkin)
    {
        var nodeWorlds = _asset.InstancePickNodeWorlds;
        if ((uint)source < (uint)nodeWorlds.Length && (uint)destination < (uint)nodeWorlds.Length)
        {
            var from = nodeWorlds[source];
            var to = nodeWorlds[destination];
            if (from != null && to != null)
                Array.Copy(from, to, Math.Min(from.Length, to.Length));
        }

        if (!hasSkin)
            return;

        var bones = _asset.InstancePickBones;
        if ((uint)source < (uint)bones.Length && (uint)destination < (uint)bones.Length)
        {
            var from = bones[source];
            var to = bones[destination];
            if (from != null && to != null)
                Array.Copy(from, to, Math.Min(from.Length, to.Length));
        }
    }

    /// <summary>
    /// P2: republishes this frame's transform for every slot that just came back into view into the previous-frame ring, so the
    /// motion vector computed against it is zero for exactly this frame.
    ///
    /// The slot written is <see cref="PrevFrameSlot"/> -- the side this frame's shaders read as "previous" -- and no in-flight
    /// frame reads it: a frame reads the slot before its own, and this one is being filled for the commands about to be recorded.
    /// Morph weights are taken from the CPU shadow that was just uploaded rather than zeroed, because their previous-frame buffer
    /// has no sentinel: zero is a legitimate set of weights, not "no history".
    /// </summary>
    void WriteHistoryResets(Matrix4x4[]? frameWorlds)
    {
        if (_historyResetSlots.Count == 0 || frameWorlds == null)
            return;

        int prevSlot = PrevFrameSlot;
        byte* worlds = (uint)prevSlot < (uint)_mappedInstanceWorldBuffers.Length
            ? _mappedInstanceWorldBuffers[prevSlot] : null;
        byte* morphs = (uint)prevSlot < (uint)_mappedMorphWeightsBuffers.Length
            ? _mappedMorphWeightsBuffers[prevSlot] : null;

        for (int k = 0; k < _historyResetSlots.Count; k++)
        {
            int slot = _historyResetSlots[k];

            if (worlds != null && slot < _instanceWorldCapacity && slot < frameWorlds.Length)
                *(Matrix4x4*)(worlds + slot * sizeof(Matrix4x4)) = frameWorlds[slot];

            if (morphs != null && slot < _instanceMorphWeightsCapacity && slot < _currentMorphShadow.Length)
                *(Vector4*)(morphs + slot * sizeof(Vector4)) = _currentMorphShadow[slot];
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
        // P1: on the GPU route the storage ring is the palette. Its structured view is created once with the buffer, so this is a read of
        // an already-resident descriptor rather than the arena capture the CPU ring path below goes through. The gate is this frame's
        // latch, not the handoff result: the CPU ring was never allocated on this route, so falling through after a failed handoff would
        // bind a null descriptor and take the device down, while the ring still holds the last palette the kernel produced. Live is what
        // says that content is current - the handoff was recorded - and a frame where it is false draws one frame of the old pose.
        if (_gpuSkin && _skinning.Palette?.Current is DXStorageBuffer storageBone)
            return storageBone.VertexSrvGpuHandle;

        int slot = FrameSlot;
        if ((uint)slot >= (uint)_instanceBoneBuffers.Length)
            return default;
        return DXSceneDrawCollection.Structured(_instanceBoneBuffers[slot], 64, _instanceBoneSrvHandles[slot]);
    }

    /// <summary>2-3 Step C (tier B): the previous frame's palette is the same ring one slot back.</summary>
    protected override GpuDescriptorHandle GetPrevBoneSrvHandle()
    {
        if (_gpuSkin && _skinning.Palette?.Previous is DXStorageBuffer storageBone)
            return storageBone.VertexSrvGpuHandle;

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
        _historyResetSlots.Clear();
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

        // P1: latch the palette source with the frame, for the same reason the cull authorization is latched. The storage ring only
        // exists on the GPU route - a model on the CPU route never allocates one, which is what keeps its path byte-identical to what
        // it was before P1. A ring supplied by the model is used as-is and never disposed here.
        InstanceBoneStorage? paletteRing = model.BoneStorage;
        if (paletteRing == null && model.GpuSkinning)
        {
            _ownedPalette ??= new InstanceBoneStorage();
            paletteRing = _ownedPalette;
        }
        else if (!model.GpuSkinning && _ownedPalette != null)
        {
            // The mode (or the process-wide override) turned the GPU route off after the ring had been allocated for it.
            _ownedPalette.Dispose();
            _ownedPalette = null;
        }

        // False here is the CPU route in full: the CPU clock still advances below, but the pose is replayed and uploaded the way it
        // always was.
        // P2 L1 / P3 L2: the tag row count travels with the latch so the kernel writes a tag exactly on the backends whose vertex
        // stage reads one. Passing zero here would leave the kernel filling the last joint row instead.
        _gpuSkin = _skinning.Begin(paletteRing, _asset.SkinBake, _bonePaletteStride, _asset._animations.Count, InstanceSkinning.PoseTagRows);

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
        // P1: shape the GPU route's palette ring before anything else, so the CPU ring below is allocated only when it will actually be
        // written. Slot count is the third number besides stride and instance count: the ring's slot is the frame slot, so a ring of a
        // different depth would alias the slots and hand the vertex stage the pose of a frame the GPU may still be reading.
        if (_gpuSkin
            && !(_skinning.PreparePalette(Season.Basic.Graphics.Instance, FrameSlotCount, FrameSlot, PrevFrameSlot, _instanceCount)
                && _skinning.BeginFrame(_instanceCount)))
        {
            // The ring cannot be allocated at this shape, or the parameter block cannot be sized. The CPU route below is then the only
            // correct answer, and it is reachable: the ring is allocated on the next line.
            _skinning.Begin(null, null, 0, 0);
            _gpuSkin = false;
        }

        // On the GPU route the CPU palette ring is neither written nor read, so it is not allocated: that layout is the 2 MB-per-frame
        // upload's source, and its only remaining purpose was to be the fallback descriptor for a frame the kernel did not serve.
        if (!_gpuSkin)
            EnsureInstanceBoneCapacity((uint)Math.Max(_instanceCount, 1));
        EnsureMorphWeightsCapacity(_instanceCount);

        // A7 cull input: one root instance world per enabled instance, in write order. The per-primitive streams below hold
        // "node world × instance world", which differs per primitive, so none of them can stand in for the instance as a whole;
        // the cull box (model.TemplateCullBounds) is in the same model space as this root world.
        if (_instanceData.Length != _instanceCount)
            _instanceData = new InstanceTransformData[_instanceCount];

        // Generic per-instance extras travel with the same compacted slot as the root world above. This class replays its
        // own streams instead of going through UpdateInstanceData, so the ring has to be sized, filled and uploaded here
        // too. Without it every opaque draw binds the one-record white fallback and slots past it read as zero, which
        // shows up as one white instance and the rest black, with the colors never following the control.
        if (_instanceExtras.Length != _instanceCount)
            _instanceExtras = new InstanceExtras[_instanceCount];
        EnsureInstanceExtrasRingCapacity(_instanceCount);

        bool hasAnimation = _asset._animations.Count > 0;
        bool hasSkin = _skins.Count > 0;
        float deltaTime = Math.Max(time, 0f);
        bool anySampled = false;

        // P1: one pose row per instance, sized by the primitive layout and the palette stride. Cheap after the first frame.
        _poseCache.Ensure(model.Instances.Count, _primitives.Count, hasSkin ? _bonePaletteStride : 0);
        if (hasAnimation || hasSkin)
            EnsureInstancePickShadow(model.Instances.Count);

        // 2-3 Step C (tiers B/C-b): nothing is copied here any more. Each GPU ring above holds one frame in flight per
        // slot, so "the previous frame's palette / morph weights" is simply the slot the previous frame wrote, and the
        // shader reads it through PrevFrameSlot. The old code memcpy'd the whole palette forward once per frame, and on
        // D3D12 that read from write-combined upload memory -- the one access mode the CPU performs an order of
        // magnitude slower -- to produce bytes the ring already held.

        int writeIndex = 0;

        // P3 L2: the pool lives for exactly one replay, like P3a's dedup table it is keyed against. A pool block addressed by a
        // previous frame's tag would still hold that frame's pose, which is why a slot's tag is written for every instance every
        // frame rather than only for the ones that end up pooled.
        _posePoolBucketBySource.Clear();
        _posePoolRewrites.Clear();
        if (_posePoolPending.Length < model.Instances.Count)
            _posePoolPending = new (int Write, uint Clip, float Time)[model.Instances.Count];

        for (int i = 0; i < model.Instances.Count; i++)
        {
            var instance = model.Instances[i];
            if (!instance.Enable)
            {
                continue;
            }

            // Unified transform convention: route everything through
            // BuildInstanceMatrix (anchor pivot, see InstancedMesh3DBase)
            var instanceWorld = model.InstanceWorld(i);
            _instanceData[writeIndex] = InstanceTransformData.FromWorld(instanceWorld);
            // The color is whatever the control set; white (the default) is the identity, so an instance that never
            // sets one renders exactly as it did before the extras slot existed.
            _instanceExtras[writeIndex] = new InstanceExtras { Color = instance.Color, Extra = Vector4.Zero };

            // P2: the CPU cull decision is made once per instance in the control's Update; this is where it reaches the replay.
            bool instanceCulled = model.IsInstanceAnimationCulled(i);
            if (model.ShouldResetInstanceHistory(i))
                _historyResetSlots.Add(writeIndex);
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
            int clip = 0;
            float playbackTime = 0f;
            if (hasAnimation)
            {
                clip = instance.AnimationClip;
                if (clip < 0 || clip >= _asset._animations.Count)
                    clip = 0;

                ref var state = ref _animationStates[i];
                if (!state.Initialized || state.AnimationClip != clip)
                {
                    playbackTime = instance.AnimationTimeOffset + deltaTime * instance.AnimationSpeed;
                    state.Initialized = true;
                    state.AnimationClip = clip;
                }
                else
                {
                    playbackTime = state.PlaybackTime + deltaTime * instance.AnimationSpeed;
                }

                // P1: the clock advances every frame regardless of the tier, so a throttled instance is always sampled at
                // the true current time and only its refresh rate changes. A held instance (zero delta, zero speed) reads
                // the same time it already cached, which is what makes the hold a pure reuse.
                state.PlaybackTime = playbackTime;
            }

            bool samplePose = !instanceCulled && model.ShouldSampleInstanceAnimation(i, instance, clip);
            anySampled |= samplePose;

            // P3a: a sampled instance at tier 2 or slower may share another instance's sample for the same (clip, quantized
            // time) pair. On the CPU route a hit copies the cached rows and the streams below are built from them exactly as on a
            // throttled frame, so the instance transform still applies this frame. On the GPU route the same hit is P3 L2's signal
            // that this instance can point its tag at a pool block instead of evaluating again - see the parameter write below.
            float sampleTime = playbackTime;
            int poseSource = -1;
            bool poseShared = samplePose && model.TryReuseInstancePose(i, clip, playbackTime, out poseSource, out sampleTime);
            if (poseShared && !_gpuSkin)
            {
                _poseCache.CopyRow(poseSource, i);
                CopyInstancePickPose(poseSource, i, hasSkin);
                samplePose = false;
            }

            // P1: the whole CPU evaluation - rest restore, clip replay, node transform pass - is what the GPU route replaces. The clock
            // above still runs for every tier, so the instance is drawn at the pose its time names, whoever evaluates it.
            if (samplePose && !_gpuSkin)
            {
                RestoreNodesToRestPose();

                if (hasAnimation)
                    _asset._animationPlayer.Evaluate(clip, sampleTime, _workNodes);
                else
                    _asset._animationPlayer.UpdateAllNodeTransforms(_workNodes);
            }

            if (hasSkin && !_gpuSkin)
            {
                if (samplePose)
                {
                    _asset._animationPlayer.UpdateBoneMatrices(_skins);
                    var palette = _asset._animationPlayer.GetBoneMatricesArray();
                    Array.Copy(palette, _poseCache.Bones[i], Math.Min(palette.Length, _poseCache.Bones[i].Length));
                }

                // P1: a throttled frame re-uploads the palette it sampled last, which is the pose its instance is drawn with.
                UploadInstanceBoneMatrices(writeIndex, _poseCache.Bones[i]);
            }

            // P1 GPU route: 16 bytes per instance, the clip index plus the time the clock just produced, already wrapped or clamped the
            // way the player would have done it. This is the entire per-frame CPU work the palette costs now.
            //
            // P3 L2: the time is the deduped one - a tick boundary for a participant, the exact clock for everyone else - because a
            // pool block is shared by instances that have to evaluate to bitwise the same pose. A shared instance writes only its tag
            // row; its representative's row is upgraded with the block after the loop, at which point the group is known to exist.
            if (_gpuSkin && _asset.SkinBake is { } gpuBake)
            {
                float gpuTime = gpuBake.NormalizeTime(clip, sampleTime, _asset._animationPlayer.Loop);
                uint poolRow = 0;
                bool evaluate = true;
                if (poseShared)
                {
                    if (!_posePoolBucketBySource.TryGetValue(poseSource, out int bucket))
                    {
                        // One block per *shared* group, so a frame in which nothing shares reserves nothing. The count is bounded by
                        // the instance count, which is what PreparePalette sized the pool region by, so the address below always
                        // lands inside the reservation.
                        if (_posePoolRewrites.Count < _instanceCount)
                        {
                            bucket = _posePoolRewrites.Count;
                            _posePoolBucketBySource[poseSource] = bucket;
                            _posePoolRewrites.Add(_posePoolPending[poseSource]);
                        }
                        else
                        {
                            bucket = -1;
                        }
                    }

                    if (bucket >= 0)
                    {
                        poolRow = (uint)((_instanceCount + bucket) * _bonePaletteStride);
                        evaluate = false;
                    }
                }

                // Recorded whenever this instance ended up owning its own block, including the degenerate case where the pool
                // reservation ran out above: it is still a candidate, and the next instance that asks for the same pair will adopt it.
                // The write index is only known here - the Instances index skips disabled entries, the write index does not.
                if (evaluate)
                    _posePoolPending[i] = (writeIndex, (uint)clip, gpuTime);

                _skinning.SetInstance(writeIndex, (uint)clip, gpuTime, evaluate, poolRow);
            }

            if (samplePose && (hasAnimation || hasSkin))
            {
                // v2 picking: write a per-instance shadow copy (node worlds + bone palette) on every sampled frame, and
                // leave it in place on a throttled one -- the previous snapshot then describes the pose still being
                // rendered, so picking agrees with what the camera sees. Instance playback time and bones are
                // backend-private, so shared-layer picking cannot replay them. The shadow matches the render source of
                // truth: node world corresponds to the OwnerNode part of finalWorld below.
                var pickNodeWorlds = _asset.InstancePickNodeWorlds[i];
                for (int n = 0; n < _asset.gltfNodes.Count; n++)
                    pickNodeWorlds[n] = _asset.gltfNodes[n].WorldTransform;

                if (hasSkin)
                {
                    // The GPU route has no CPU palette to copy - the kernel's output exists only on the GPU - so picking reports the rest
                    // palette captured at load. Documented L0 boundary: the pick shape follows the model's rest pose while the render shows
                    // the animated one.
                    var source = _gpuSkin ? _restPalette : _poseCache.Bones[i];
                    var pickBones = _asset.InstancePickBones[i];
                    Array.Copy(source, pickBones, Math.Min(source.Length, pickBones.Length));
                }
            }

            for (int p = 0; p < _primitives.Count; p++)
            {
                var primitive = _primitives[p];
                var stream = GetPrimitiveInstanceStream(primitive);
                if (stream == null)
                    continue;

                // P1: on a sampled frame the pose comes from the freshly evaluated tree and is parked in the cache; on a
                // throttled frame it comes back out of the cache. Either way the instance transform is applied this frame,
                // so movement is never throttled.
                var nodeWorld = samplePose
                    ? (primitive.OwnerNode?.WorldTransform ?? Matrix4x4.Identity)
                    : _poseCache.NodeWorlds[i][p];
                if (samplePose)
                    _poseCache.NodeWorlds[i][p] = nodeWorld;

                var finalWorld = nodeWorld * instanceWorld;
                var instanceData = InstanceTransformData.FromWorld(finalWorld);
                if (primitive.MaterialParams.HasMorphTargets != 0)
                {
                    // 2-3 Step C (tier C-b): the destination is the compacted instance slot (the same slot the VS reads as
                    // instanceSlot), never the primitive's stream index: a model with several morphing primitives would
                    // otherwise scatter each instance's weights into a different slot, and the history stream would hold one
                    // primitive's last instance in place of every instance's own weights.
                    instanceData.MorphWeights = samplePose
                        ? ExtractMorphWeights(primitive.OwnerNode)
                        : _poseCache.MorphWeights[i][p];
                    if (samplePose)
                        _poseCache.MorphWeights[i][p] = instanceData.MorphWeights;
                    if ((uint)writeIndex < (uint)_currentMorphShadow.Length)
                        _currentMorphShadow[writeIndex] = instanceData.MorphWeights;
                }

                stream.Worlds[writeIndex] = finalWorld;
                stream.Data[writeIndex] = instanceData;
            }

            writeIndex++;
        }

        // P3 L2: upgrade each representative's parameter row now that this frame's groups are known. This is the only parameter write
        // that happens after the loop, and it is the reason the plan is recorded instead of decided inline: an instance cannot know it
        // is a representative until a second instance asks for the same (clip, tick) pair, by which time its own row is already
        // written. The write index it recorded is the same slot the vertex stage will read, so the block is found and the pose is not
        // evaluated twice.
        for (int b = 0; b < _posePoolRewrites.Count; b++)
        {
            var representative = _posePoolRewrites[b];
            uint poolRow = (uint)((_instanceCount + b) * _bonePaletteStride);
            _skinning.SetInstance(representative.Write, representative.Clip, representative.Time, true, poolRow);
        }

        // P3 L2: publish the group count. The image a zero-group frame produces is by design identical to the unpooled one, so a
        // capture that only compared pixels could not tell the two apart; this is the number that says the path ran.
        _skinning.PooledGroups = _posePoolRewrites.Count;

        // P1: nothing is uploaded from here any more. The GPU route's only per-frame CPU work is the 16-byte row written above; the
        // dispatch that turns it into a palette, and the compute-write-to-vertex-read handoff that makes the palette readable, are both
        // recorded by InstanceSkinningEffect at FrameStart. That is the only phase where the command list is open and no Pass is in
        // flight - the same constraint that governs the cull uploads, and the reason P0's staging could not stay in Update.

        // P1: publish the pose generation the shadow-atlas reuse digest keys on. It advances only on frames where at least
        // one instance's pose was actually replayed -- exactly when the rendered pose set can differ -- so throttled and held
        // frames keep the cached atlas and any real motion invalidates it. A static host never advances it.
        // The GPU route replays every instance every frame, so every frame can differ.
        if (hasAnimation && (anySampled || _gpuSkin))
            model.AnimationPoseGeneration++;

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

        // Same frame slot as the worlds above: the vertex stage indexes this table by the compacted instance slot.
        UploadInstanceExtrasForFrame();

        // P2: instances that just came back into view get one frame of zero velocity; see WriteHistoryResets.
        WriteHistoryResets(frameWorlds);

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
                    Season.Rendering.InstanceCull.ArgsOffset(Season.Rendering.InstanceCull.MainViewSlot, i), streamBuffer,
                    instanceExtrasSrvHandle: GetInstanceExtrasSrvHandle());
                Season.Rendering.InstanceCull.MainViewDraws++;
                continue;
            }

            Pipeline.SetPipeline(forceFadeByAlpha ? PipelineMode.Fade : PipelineMode.Opaque, primitive.DoubleSided);
            var instanceVBValue = stream.ViewAt(FrameSlot);
            VertexBufferView* instanceVB = &instanceVBValue;
            Pipeline.DrawPrimitive(primitive, lightCB, _matrixBuffers[fi], instanceVB, (uint)_instanceCount, 0,
                GetInstanceBoneSrvHandle(), GetPrevBoneSrvHandle(), GetPrevInstanceWorldSrvHandle(), GetPrevMorphSrvHandle(), streamBuffer,
                instanceExtrasSrvHandle: GetInstanceExtrasSrvHandle());
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
                        GetInstanceBoneSrvHandle(), GetPrevBoneSrvHandle(), GetPrevInstanceWorldSrvHandle(), GetPrevMorphSrvHandle(), transparentBuffer,
                        instanceExtrasSrvHandle: GetInstanceExtrasSrvHandle());
                }

                Pipeline.SetPipeline(PipelineMode.Transparent, primitive.DoubleSided ? PipelineCullVariant.Back : PipelineCullVariant.Back);
                Pipeline.DrawPrimitive(primitive, lightCB, _matrixBuffers[fi], instanceVB, 1, (uint)instanceIndex,
                    GetInstanceBoneSrvHandle(), GetPrevBoneSrvHandle(), GetPrevInstanceWorldSrvHandle(), GetPrevMorphSrvHandle(), transparentBuffer,
                    instanceExtrasSrvHandle: GetInstanceExtrasSrvHandle());
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
            var maskVBValue = stream.ViewAt(FrameSlot);
            VertexBufferView* instanceVB = &maskVBValue;
            if (_outline2DHostActive)
            {
                Pipeline.DrawPrimitive(primitive, lightCB, _matrixBuffers[fi], instanceVB, (uint)_instanceCount, 0,
                    boneSrv, prevBoneSrv, prevWorldSrv, prevMorphSrv,
                    instanceExtrasSrvHandle: GetInstanceExtrasSrvHandle());
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
                        boneSrv, prevBoneSrv, prevWorldSrv, prevMorphSrv,
                        instanceExtrasSrvHandle: GetInstanceExtrasSrvHandle());
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

        // P1: leave the skinning scheduler and release the two blocks it owns. The palette ring itself is not disposed here unless this
        // class created it, which is the same ownership rule the cull resources follow.
        _skinning.Dispose();
        _ownedPalette?.Dispose();
        _ownedPalette = null;
        _restPalette = Array.Empty<Matrix4x4>();

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

        ReleaseBoneRingDeferred(_instanceBoneBuffers, _instanceBoneDescriptorIds, _mappedInstanceBoneBuffers);
        _instanceBoneBuffers = EmptyResourceRing;
        _mappedInstanceBoneBuffers = EmptyMappedRing;
        _instanceBoneSrvHandles = Array.Empty<GpuDescriptorHandle>();
        _instanceBoneDescriptorIds = Array.Empty<int>();
        _instanceBoneCapacity = 0;

        ReleaseBoneRingDeferred(_instanceMorphWeightsBuffers, _instanceMorphWeightsDescriptorIds, _mappedMorphWeightsBuffers);
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
