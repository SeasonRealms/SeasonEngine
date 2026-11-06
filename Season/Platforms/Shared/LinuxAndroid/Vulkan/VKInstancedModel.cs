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

    // P3 L2: the pose pool. Lazy bake, but along the instance axis rather than time: a group of instances that P3a decided shares one
    // (clip, quantized tick) sample is evaluated once into a palette block of its own, and every member's tag row points at that
    // block. The pool is rebuilt every frame, so its size is bounded by this frame's instance count and never by how many clips the
    // asset has - which is the whole difference from pre-baking poses at load.
    //
    // The representative is chosen by P3a (the first instance to register the key) and is not known to be one until a second
    // instance asks for it, so the plan is recorded here and written back after the loop; a representative's parameter row is
    // already uploaded by then and only gains its pool row.
    readonly Dictionary<int, int> _posePoolBucketBySource = new();
    readonly List<(int Write, uint Clip, float Time)> _posePoolRewrites = new();
    /// <summary>Parameter rows the loop below produced, indexed by the Instances index, so a bucket found later in the same replay
    /// can address the representative without assuming the write index equals the Instances index.</summary>
    (int Write, uint Clip, float Time)[] _posePoolPending = Array.Empty<(int, uint, float)>();

    // 2-3 Step C (track B): the previous frame's palette is NOT a second buffer. Each slot of the ring below
    // holds the palette of the frame that wrote it, so slot (fi + n - 1) % n already IS the previous frame's
    // palette and GetPrevBoneBufferInfo points the shader there. The old code kept a parallel prev buffer and
    // memcpy'd _mappedInstanceBoneBuffers[fi] into it before uploading, which copied the palette of the frame
    // that last used slot fi -- three frames old, not one. Cold slots stay zeroed, so the shader's _m33 sentinel
    // still falls back to this frame's bones joint by joint until a slot has real history.

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

    // P2: compacted slots that just re-entered the main camera's frustum. Their previous-frame world and morph weights are
    // republished from this frame's values after the loop, so the motion-vector pass reads zero velocity across the frames the
    // instance spent culled instead of one frame of the distance it travelled. Rebuilt every Update.
    readonly List<int> _historyResetSlots = new();

    (Vector3 Translation, Quaternion Rotation, Vector3 Scale, float[] Weights)[] _restPoseSnapshot = Array.Empty<(Vector3, Quaternion, Vector3, float[])>();
    List<GltfNodeBase> _workNodes = new();
    readonly List<GLTFSkin> _skins = new();
    InstanceAnimationState[] _animationStates = Array.Empty<InstanceAnimationState>();
    PrimitiveInstanceStream[] _primitiveInstanceStreams = Array.Empty<PrimitiveInstanceStream>();

    // P1: last sampled pose per instance (owner-node worlds / morph weights / bone palette). A throttled frame never
    // evaluates an instance's clip, and the shared node tree cannot hold every instance's pose at once, so the streams of
    // a skipped instance are rebuilt from here instead. Rows are keyed by the Instances index, see the cache's contract.
    readonly InstancePoseCache _poseCache = new();

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
    /// <paramref name="skinningMode"/> is the owning control's <c>SkinningMode</c>, copied in because this is the only point where the
    /// control and the loaded template meet: it decides whether a GPU bake is attempted, and a model that declines keeps the CPU route.
    /// </summary>
    public void Load(VKModel template, Season.Controls.Model model, InstanceSkinningMode skinningMode = InstanceSkinningMode.Cpu)
    {
        var nodeMap = new Dictionary<GltfNodeBase, GltfNodeBase>();
        CloneAnimationData(template.Asset, nodeMap);
        _asset._animationPlayer.Initialize(_asset._animations);
        SaveRestPoseSnapshot();

        _skins.Clear();
        _skins.AddRange(_asset.GetAllSkins());
        // P2 L1 / P3 L2: one extra palette row per instance block for the pose-pool tag, which the vertex stage finds at
        // instanceSlot * stride + stride - 1. The joints keep their instanceSlot * stride + jointIndex address, so this widens the
        // block without moving a byte of what the draw reads.
        _bonePaletteStride = Math.Max(1, _skins.Sum(static s => s.Joints.Count)) + InstanceSkinning.PoseTagRows;

        // P1: build the GPU route's immutable tables while the node tree is still at rest. Declining leaves SkinBake null, which keeps
        // every instance on the CPU route; it is a property of the asset (its animated nodes are not all joints), not an error.
        InstanceSkinBake.TryBake(_asset, skinningMode);
        if (_asset.SkinBake != null && _skins.Count > 0)
        {
            // The GPU route's picking pose. Once the kernel owns the evaluation nothing else advances the shared player, so it
            // would keep whatever the last CPU replay left. Park it at rest, which is what InstancedModel.TryPickInstanceSurface
            // falls back to on a backend with no per-instance pick shadow. L0 boundary: the pick shape stays at rest while the
            // render shows the animated pose.
            _asset._animationPlayer.UpdateBoneMatrices(_skins);
        }

        CreateInstanceBoneBuffers();

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

        _primitiveInstanceStreams = new PrimitiveInstanceStream[_primitives.Count];
        for (int i = 0; i < _primitiveInstanceStreams.Length; i++)
            _primitiveInstanceStreams[i] = new PrimitiveInstanceStream();

        SyncInstancedSkinningMaterialParams();
        RebuildPrimitiveBuckets();
        SyncAlpha(model.Alpha);

        // P1: the skinning scheduler holds this group for its lifetime; a frame that runs the CPU route reports nothing and is skipped.
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
    /// Clone a template primitive for instanced rendering:
    /// - VB / IB / textures -> shared references (no new GPU resources)
    /// - Matrix UBO -> created locally (World = Identity, instance transform comes from the instance buffer)
    /// - Material UBO -> created locally (because Alpha may differ)
    /// - Apply model-level material overrides (MaterialColor / Unlit)
    /// </summary>
    PrimitiveData CloneForInstancing(PrimitiveData source, Season.Controls.Model model)
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
            // Part identity travels with the clone: the owner's InstanceTintParts resolves against exactly these two names
            // after loading, the same way Model.SetPartColor resolves against them on the non-instanced path.
            PartName = source.PartName,
            PartNodeName = source.PartNodeName,
            PartBaseColor = source.PartBaseColor,
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
            // 2-3 Step C: in the instanced path, PrevWorld stays all zero
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
            {
                // Same rule as VKInstancedPrimitiveGroup.EnsureInstanceBufferCapacity: pending command buffers may still
                // reference this stream, so hand it to the retire queue instead of destroying it here.
                var retired = stream.Buffer;
                Device.EnqueueDeferredRelease(() => Device.ResourceManager.DestroyBuffer(retired));
            }

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
                var retired = stream.Buffer;
                Device.EnqueueDeferredRelease(() => Device.ResourceManager.DestroyBuffer(retired));
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

    /// <summary>
    /// A7: every opaque primitive draws its own instance stream (node world × instance world, plus its own morph weights), so gather
    /// materializes one reordered stream per opaque primitive. The visibility decision itself is made once per instance from the
    /// root instance worlds in the base <see cref="VKInstancedPrimitiveGroup._instanceData"/> against the model-space clip-union box.
    /// </summary>
    protected override bool CullPerPrimitiveStreams => true;

    protected override ReadOnlySpan<InstanceTransformData> GetCullDrawStream(int stream)
        => (uint)stream < (uint)_opaquePrimitives.Count && GetPrimitiveInstanceStream(_opaquePrimitives[stream]) is { } s
            ? s.Data
            : default;

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
    {
        get
        {
            // P0: descriptor set fi is written once for frame fi, not per frame, so a storage ring has to be handed over slot for slot -
            // ring slot fi is exactly what this frame writes and what descriptor set fi reads. That is also why the swap needs a
            // descriptor rewrite (see SyncBoneStorageRing) while a CPU-ring upload inside the same buffer does not.
            if (_boneStorageRing != null)
                return _boneStorageRing;

            return _instanceBoneBuffers != null && _instanceBoneBuffers.Length > 0
                ? _instanceBoneBuffers
                : Pipeline.IdentityInstanceBoneBuffers;
        }
    }

    /// <summary>
    /// P1: the GPU skinning route's frame state - the palette ring the descriptor sets bind, the load-time bake the kernel evaluates,
    /// and the 16-byte per-instance parameter block. Shared with the other three backends so the layout cannot drift between them.
    /// </summary>
    readonly Season.Rendering.InstanceSkinningTarget _skinning = new();

    /// <summary>
    /// True while this frame runs the GPU route. Latched once per Update so a frame cannot half-switch between the two palettes.
    /// False is the historical path, untouched: <c>Evaluate</c> per instance, the palette written into the mapped ring by
    /// <see cref="UploadInstanceBoneMatrices"/>, and no storage ring bound.
    /// </summary>
    bool _gpuSkin;

    /// <summary>The palette ring this class created because the model asked for the GPU route without supplying one. Owned here,
    /// disposed here; a ring the model supplied is never touched.</summary>
    Season.Rendering.InstanceBoneStorage? _ownedPalette;

    /// <summary>The ring flattened into the shape the descriptor writer consumes, rebuilt only when the ring's buffer set changes.</summary>
    BufferResource[]? _boneStorageRing;

    /// <summary>
    /// Rebuilds <see cref="_boneStorageRing"/> and returns whether the buffers it points at changed, which is the condition for rewriting the
    /// descriptor sets: the sets hold the VkBuffer handle by value, so a reallocation - or the first switch to a ring at all - leaves them
    /// addressing a retired buffer.
    /// </summary>
    bool SyncBoneStorageRing()
    {
        // Descriptor set fi binds ring slot fi, which is the slot this frame's kernel writes, so the binding decision is this frame's
        // latch rather than the handoff result: on Vulkan the set is per slot and the vertex-read barrier is recorded by the effect at
        // FrameStart, after Update has already rebuilt these sets. Dropping the flattened copy is what tells the commit below to
        // republish them to the CPU ring after a fallback.
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
    /// P1: latches this frame's palette source. Runs before the early-out for an empty instance list, so a group that stops submitting
    /// never leaves the scheduler holding last frame's instance count. The storage ring only exists on the GPU route - a model on the
    /// CPU route keeps the historical path and never allocates one.
    /// </summary>
    void BeginSkinningFrame(InstancedModel model)
    {
        // Descriptor set fi exists per frame slot, and a ring of one slot would read the palette it just wrote as "previous".
        if (Device.frameCount < 2)
        {
            _skinning.Begin(null, null, 0, 0);
            _gpuSkin = false;
            return;
        }

        Season.Rendering.InstanceBoneStorage? paletteRing = model.BoneStorage;
        if (paletteRing == null && model.GpuSkinning)
        {
            _ownedPalette ??= new Season.Rendering.InstanceBoneStorage();
            paletteRing = _ownedPalette;
        }
        else if (!model.GpuSkinning && _ownedPalette != null)
        {
            // The mode (or the process-wide override) turned the GPU route off after the ring had been allocated for it.
            _ownedPalette.Dispose();
            _ownedPalette = null;
        }

        // P2 L1 / P3 L2: the tag row count travels with the latch so the kernel writes a tag exactly on the backends whose vertex
        // stage reads one. Passing zero here would leave the kernel filling the last joint row instead.
        _gpuSkin = _skinning.Begin(paletteRing, _asset.SkinBake, _bonePaletteStride, _asset._animations.Count, InstanceSkinning.PoseTagRows);
    }

    /// <summary>
    /// P1: shapes the ring for this frame. "Previous" is the slot the previous frame wrote, exactly as for the CPU ring, and the slot
    /// count has to be the frame ring's depth because the descriptor writer reads <see cref="InstanceBoneBuffers"/> by frame index.
    /// Returns false when the CPU route is the only correct answer, which the caller applies by clearing the latch.
    /// </summary>
    bool ShapeSkinningRing()
    {
        int slots = (int)Device.frameCount;
        int slot = (int)Device.FrameIndex % slots;
        return _skinning.PreparePalette(Season.Basic.Graphics.Instance, slots, slot, (slot + slots - 1) % slots, _instanceCount)
            && _skinning.BeginFrame(_instanceCount);
    }

    /// <summary>
    /// P0: the ring becomes the palette only after a successful publish, so this runs after it and republishes the VkBuffer handles to
    /// every descriptor set when the buffer set changed - a first switch, a fallback, or a resize all leave the sets pointing at a buffer
    /// that is no longer the palette.
    /// </summary>
    void CommitBoneStorageRing()
    {
        if (!SyncBoneStorageRing())
            return;

        for (int i = 0; i < _primitives.Count; i++)
            RewriteDescriptorSets(_primitives[i]);
        RewriteShellDescriptorSets();
    }

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
        int elementCount = Math.Max(1, _bonePaletteStride * (int)capacity);
        ulong size = (ulong)(elementCount * Unsafe.SizeOf<Matrix4x4>());

        for (int i = 0; i < Device.frameCount; i++)
        {
            if (_instanceBoneBuffers[i].Buffer.Handle != 0)
            {
                // Capacity growth can happen at any time, including while the last frames still reference the old buffer
                // through binding 9. Unmapping is host-side only, so it stays here; the destroy goes to the retire queue
                // so it cannot land before those frames complete (VUID-vkDestroyBuffer-buffer-00922).
                var retired = _instanceBoneBuffers[i];
                if (_mappedInstanceBoneBuffers != null && _mappedInstanceBoneBuffers[i] != null)
                    Device.Vk.UnmapMemory(Device.LogicalDevice, retired.Memory);
                Device.EnqueueDeferredRelease(() => Device.ResourceManager.DestroyBuffer(retired));
            }

            _instanceBoneBuffers[i] = Device.ResourceManager.CreateBuffer(
                size,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

            void* mapped;
            if (Device.Vk.MapMemory(Device.LogicalDevice, _instanceBoneBuffers[i].Memory, 0, size, 0, &mapped) != Result.Success)
                throw new Exception("vkMapMemory (InstancedModel bone storage) failed");

            _mappedInstanceBoneBuffers[i] = (byte*)mapped;
            // Zero, not identity: a slot that has never been written must read as "no previous frame" through the
            // shader's _m33 == 0 sentinel, which is what makes it fall back to this frame's bones instead of
            // extrapolating from an identity pose.
            new Span<byte>(mapped, (int)size).Clear();
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

        // P2 L1 / P3 L2: the tag row is the one row of the block this upload does not fill from the pose, and it has to read zero
        // here. A ring slot is reused every frame, so a row left over from an earlier pooled frame - or from a compute handoff the
        // CPU route took back - would send the vertex stage to a pool block this route never wrote. Cleared unconditionally rather
        // than only when the route changed, so there is one rule for the row instead of two.
        byte* tagRow = _mappedInstanceBoneBuffers[fi] + offset
            + (long)(_bonePaletteStride - InstanceSkinning.PoseTagRows) * Unsafe.SizeOf<Matrix4x4>();
        *(uint*)(tagRow + InstanceSkinning.PoseTagCurrentOffset) = 0;
        *(uint*)(tagRow + InstanceSkinning.PoseTagPreviousOffset) = 0;
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
                var retired = _prevInstanceWorldBuffers[i];
                if (_mappedPrevInstanceWorldBuffers != null && i < _mappedPrevInstanceWorldBuffers.Length
                    && _mappedPrevInstanceWorldBuffers[i] != null && retired.Memory.Handle != 0)
                    Device.Vk.UnmapMemory(Device.LogicalDevice, retired.Memory);
                if (retired.Memory.Handle != 0)
                    Device.EnqueueDeferredRelease(() => Device.ResourceManager.DestroyBuffer(retired));
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
                var retired = _prevMorphWeightsBuffers[i];
                if (_mappedPrevMorphWeightsBuffers != null && i < _mappedPrevMorphWeightsBuffers.Length
                    && _mappedPrevMorphWeightsBuffers[i] != null && retired.Memory.Handle != 0)
                    Device.Vk.UnmapMemory(Device.LogicalDevice, retired.Memory);
                if (retired.Memory.Handle != 0)
                    Device.EnqueueDeferredRelease(() => Device.ResourceManager.DestroyBuffer(retired));
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

    /// <summary>
    /// P2: republishes this frame's transform for every slot that just came back into view into the previous-frame SSBO, so the
    /// motion vector computed against it is zero for exactly this frame.
    ///
    /// The frame-start bulk copy filled that SSBO with the previous frame's values; these slots then overwrite their own row. The
    /// morph weights come from the CPU shadow this frame just filled, not from zeros: their previous-frame buffer has no sentinel,
    /// so zero is a legitimate set of weights rather than "no history". The world source is the same first valid stream the bulk
    /// copy reads, so a multi-primitive model cannot publish one primitive's node transform as the instance's history.
    /// </summary>
    void WriteHistoryResets()
    {
        if (_historyResetSlots.Count == 0)
            return;

        int fi = (int)Device.FrameIndex;
        byte* worlds = _mappedPrevInstanceWorldBuffers != null && fi < _mappedPrevInstanceWorldBuffers.Length
            ? _mappedPrevInstanceWorldBuffers[fi] : null;
        byte* morphs = _mappedPrevMorphWeightsBuffers != null && fi < _mappedPrevMorphWeightsBuffers.Length
            ? _mappedPrevMorphWeightsBuffers[fi] : null;

        Matrix4x4[]? frameWorlds = null;
        foreach (var stream in _primitiveInstanceStreams)
        {
            if (stream?.Worlds != null && stream.Worlds.Length >= _instanceCount)
            {
                frameWorlds = stream.Worlds;
                break;
            }
        }

        for (int k = 0; k < _historyResetSlots.Count; k++)
        {
            int slot = _historyResetSlots[k];

            if (worlds != null && frameWorlds != null && slot < _prevInstanceWorldCapacity && slot < frameWorlds.Length)
                *(Matrix4x4*)(worlds + slot * Unsafe.SizeOf<Matrix4x4>()) = frameWorlds[slot];

            if (morphs != null && slot < _prevMorphWeightsCapacity && slot < _currentMorphShadow.Length)
                *(Vector4*)(morphs + slot * Unsafe.SizeOf<Vector4>()) = _currentMorphShadow[slot];
        }
    }

    // 2-3 Step C: override the base virtual methods to return the actual prev SSBOs
    protected override DescriptorBufferInfo GetPrevBoneBufferInfo(int fi)
    {
        // P0: with a storage ring the previous palette is the ring's previous slot, the same one-slot-back rule the CPU ring uses.
        if (_boneStorageRing != null && _boneStorageRing.Length > 0)
        {
            var prevSlot = _boneStorageRing[((fi % _boneStorageRing.Length) + _boneStorageRing.Length - 1) % _boneStorageRing.Length];
            if (prevSlot.Buffer.Handle != 0)
                return new() { Buffer = prevSlot.Buffer, Offset = 0, Range = Vk.WholeSize };
        }

        if (_instanceBoneBuffers == null || _instanceBoneBuffers.Length == 0)
            return base.GetPrevBoneBufferInfo(fi);
        int slots = _instanceBoneBuffers.Length;
        var prev = _instanceBoneBuffers[((fi % slots) + slots - 1) % slots];
        return prev.Buffer.Handle != 0
            ? new() { Buffer = prev.Buffer, Offset = 0, Range = Vk.WholeSize }
            : base.GetPrevBoneBufferInfo(fi);
    }

    protected override DescriptorBufferInfo GetPrevInstanceWorldBufferInfo(int fi)
        => _prevInstanceWorldBuffers != null && fi < _prevInstanceWorldBuffers.Length
            ? new() { Buffer = _prevInstanceWorldBuffers[fi].Buffer, Offset = 0, Range = Vk.WholeSize }
            : base.GetPrevInstanceWorldBufferInfo(fi);

    protected override DescriptorBufferInfo GetPrevMorphWeightsBufferInfo(int fi)
        => _prevMorphWeightsBuffers != null && fi < _prevMorphWeightsBuffers.Length
            ? new() { Buffer = _prevMorphWeightsBuffers[fi].Buffer, Offset = 0, Range = Vk.WholeSize }
            : base.GetPrevMorphWeightsBufferInfo(fi);

    // 2-3 Step C (tracks B/C-b): this path owns both history streams once their rings exist, so the base class can publish
    // all three flags from one place, every frame (see VKInstancedPrimitiveGroup.UpdatePrevStateFlags). The GPU route has no mapped CPU
    // ring - it was never allocated - but its storage ring does hold a previous slot, so that counts as history too.
    protected override bool HasPrevBoneHistory => (_instanceBoneBuffers != null && _instanceBoneBuffers.Length > 0) || _boneStorageRing != null;

    /// <inheritdoc cref="HasPrevBoneHistory"/>
    protected override bool HasPrevMorphHistory => _prevMorphWeightsBuffers != null;

    public IReadOnlyList<string> GetAnimationNames()
    {
        return _asset.GetAnimationNames();
    }

    public void Update(InstancedModel model, float time)
    {
        bool wasInitialized = _transformInitialized;
        _instanceCount = 0;
        _historyResetSlots.Clear();

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

        // P1: latch this frame's palette source. Runs before the empty-list early-out, so a group that stops submitting never leaves
        // the scheduler holding the previous frame's instance count.
        BeginSkinningFrame(model);

        if (_instanceCount == 0)
        {
            // P1: drop the latch before the early-out, so the scheduler never dispatches with last frame's instance count.
            _skinning.Begin(null, null, 0, 0);
            _gpuSkin = false;
            _transformInitialized = true;
            SyncAlpha(model.Alpha);
            SetOutline2DState(false, default, default);
            return;
        }

        EnsureAnimationStateCapacity(model.Instances.Count);
        EnsurePrimitiveInstanceCapacity(_instanceCount);
        // P1: shape the GPU route's ring first, then allocate the CPU palette ring only if it will actually be written.
        if (_gpuSkin && !ShapeSkinningRing())
        {
            _skinning.Begin(null, null, 0, 0);
            _gpuSkin = false;
        }
        if (!_gpuSkin)
            EnsureInstanceBoneCapacity((uint)Math.Max(_instanceCount, 1));
        // 2-3 Step C: create or grow the prev SSBOs
        EnsurePrevInstanceWorldCapacity(_instanceCount);
        EnsurePrevMorphWeightsCapacity(_instanceCount);
        // Generic per-instance extras: a separate stream from the 80-byte instance data, so the cull input and the text
        // reinterpretation of that stream stay untouched.
        EnsureInstanceExtrasCapacity(_instanceCount);

        // A7 cull input: one root instance world per enabled instance, in write order. The per-primitive streams below hold
        // "node world × instance world", which differs per primitive, so none of them can stand in for the instance as a whole;
        // the cull box (model.TemplateCullBounds) is in the same model space as this root world.
        if (_instanceData.Length != _instanceCount)
        {
            _instanceData = new InstanceTransformData[_instanceCount];
            _instanceExtras = new InstanceExtras[_instanceCount];
        }

        bool hasAnimation = _asset._animations.Count > 0;
        bool hasSkin = _skins.Count > 0;
        float deltaTime = Math.Max(time, 0f);
        bool anySampled = false;

        // P1: one pose row per instance, sized by the primitive layout and the palette stride. Cheap after the first frame.
        _poseCache.Ensure(model.Instances.Count, _primitives.Count, hasSkin ? _bonePaletteStride : 0);

        // 2-3 Step C (tracks C-a/C-b): hand the PREVIOUS frame's CPU-side data to the prev SSBO slots before
        // overwriting them below. The bone palette needs no equivalent: it is a ring read one slot back (see
        // GetPrevBoneBufferInfo), so the previous frame's palette is already the slot the GPU holds.
        int fi = (int)Device.FrameIndex;
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
                continue;

            // Unified transform convention: converge on BuildInstanceMatrix
            // (anchor pivot, see InstancedMesh3DBase)
            var instanceWorld = model.InstanceWorld(i);
            _instanceData[writeIndex] = InstanceTransformData.FromWorld(instanceWorld);
            // The extras record rides the same compacted slot as the matrix, including on the A7 indirect path, so the VS
            // indexes both streams with one decoded index.
            _instanceExtras[writeIndex] = new InstanceExtras { Color = instance.Color, Extra = Vector4.Zero };

            // P2: the CPU cull decision is made once per instance in the control's Update; this is where it reaches the replay.
            bool instanceCulled = model.IsInstanceAnimationCulled(i);
            if (model.ShouldResetInstanceHistory(i))
                _historyResetSlots.Add(writeIndex);

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
                stream.Worlds[writeIndex] = finalWorld;
                var instanceData = InstanceTransformData.FromWorld(finalWorld);
                if (primitive.MaterialParams.HasMorphTargets != 0)
                {
                    // 2-3 Step C (track C-b): the destination is the compacted instance slot (the same slot the VS reads as
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
        // dispatch that turns it into a palette, and the compute-write-to-vertex-read barrier that makes the palette readable, are both
        // recorded by InstanceSkinningEffect at FrameStart. That is the only phase where the command buffer is recording and no render
        // pass is open - the same constraint that governs the cull uploads, and the reason P0's staging could not stay in Update.
        //
        // The descriptor sets hold VkBuffer handles by value, so a ring that appeared, disappeared or was reallocated has to be
        // republished. It is keyed on this frame's latch rather than on the handoff result: on Vulkan the binding is per frame slot
        // (set fi binds ring slot fi, the slot this frame's kernel writes), so the sets are correct before the barrier is recorded.
        CommitBoneStorageRing();

        // P1: publish the pose generation the shadow-atlas reuse digest keys on. It advances only on frames where at least
        // one instance's pose was actually replayed -- exactly when the rendered pose set can differ -- so throttled and held
        // frames keep the cached atlas and any real motion invalidates it. A static host never advances it.
        // The GPU route replays every instance every frame, so every frame can differ.
        if (hasAnimation && (anySampled || _gpuSkin))
            model.AnimationPoseGeneration++;

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

        UploadInstanceExtras(_instanceCount);

        // P2: instances that just came back into view get one frame of zero velocity; see WriteHistoryResets.
        WriteHistoryResets();

        // A7: the box must be in the root instance's model space and cover every clip pose, because the kernel culls the whole
        // instance with it once and every primitive stream only replays that decision. The cull-side uploads themselves wait for
        // PrepareCullInputs (the command buffer is not recording yet during Update).
        SetCullLocalBounds(model.TemplateCullBounds);
        UpdateCullUpload(model);

        fi = (int)Device.FrameIndex;
        var matrices = new MatrixBuffer
        {
            World = Matrix4x4.Transpose(Matrix4x4.Identity),
            // 2-3 Step C (track C-a): the per-instance previous world matrices now come from
            // the prev instance world SB (binding 14),
            // so b0.PrevWorld stays all zero because the instanced shader path does not read it.
        };

        foreach (var primitive in _primitives)
            Unsafe.Write(primitive.MappedMatrixBuffers[fi], matrices);

        _transformInitialized = true;

        // 2-3 Step C: publishes whether the previous-frame streams may be read this frame. Not a one-shot call any more: the
        // history is addressed by the compacted instance slot, so it only lines up while the enabled order is unchanged
        // (see InstancedMesh3DBase.InstanceHistoryValid). The first frame after load has no history and passes false.
        // The DescriptorSet rewrite stays one-shot: it only switches bindings 13/14/15 to the real previous-frame SSBOs.
        UpdatePrevStateFlags(wasInitialized && model.InstanceHistoryValid);
        if (wasInitialized)
        {
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

        // During collection, pin this model on the snapshot page: the instance stream and the primitive descriptor sets will still be referenced during replay.
        VKSceneDrawCollection.RetainOwner(this);

        var cmd = Device.GraphicsCommandBuffer;
        bool forceFadeByAlpha = _currentAlpha < 1.0f;
        int fi = (int)Device.FrameIndex;

        // A7 main view: each opaque primitive reads the main-view segment of its own reordered stream, with the instance count from
        // the args entry (MainViewSlot, i). The bone / prev-bone / prev-world / prev-morph tables need no reordering of their own:
        // the tag gather writes into each record IS the compacted instance slot the cull kernel numbered, which is exactly how those
        // tables are laid out, so the reordered rows line up row for row and skinned / morphing models need nothing extra here.
        // Fade stays direct for the same reason as the base class (per-instance back-to-front splitting).
        for (int i = 0; i < _opaquePrimitives.Count; i++)
        {
            var primitive = _opaquePrimitives[i];
            var stream = GetPrimitiveInstanceStream(primitive);
            if (stream == null || stream.Buffer.Buffer.Handle == 0)
                continue;

            if (!forceFadeByAlpha && TryGetMainViewSegment(i, out var sortedBuffer, out ulong segmentOffset, out var argsBuffer))
            {
                Pipeline.SetPipeline(cmd, PipelineMode.Opaque, primitive.DoubleSided);
                Pipeline.DrawPrimitive(cmd, primitive, primitive.VertexBuffer.Buffer, primitive.IndexBuffer.Buffer,
                    primitive.DescriptorSets[fi], (uint)primitive.Indices.Length,
                    sortedBuffer, 0, 0, float.NaN,
                    argsBuffer, Season.Rendering.InstanceCull.ArgsOffset(Season.Rendering.InstanceCull.MainViewSlot, i),
                    segmentOffset);
                Season.Rendering.InstanceCull.MainViewDraws++;
                continue;
            }

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
                // The instance world matrix exists only on the CPU side (stream.Worlds); the GPU-side instance stream cannot be read back,
                // so the transparent sort depth must be provided here.
                float depth = ComputeTransparentDepth(stream.Worlds[instanceIndex], primitive.LocalBoundsCenter);
                if (primitive.DoubleSided)
                {
                    Pipeline.SetPipeline(cmd, PipelineMode.Transparent, CullModeFlags.FrontBit);
                    Pipeline.DrawPrimitive(cmd, primitive, primitive.VertexBuffer.Buffer, primitive.IndexBuffer.Buffer,
                        primitive.DescriptorSets[fi], (uint)primitive.Indices.Length,
                        stream.Buffer.Buffer, 1, instanceIndex, depth);
                }

                Pipeline.SetPipeline(cmd, PipelineMode.Transparent, CullModeFlags.BackBit);
                Pipeline.DrawPrimitive(cmd, primitive, primitive.VertexBuffer.Buffer, primitive.IndexBuffer.Buffer,
                    primitive.DescriptorSets[fi], (uint)primitive.Indices.Length,
                    stream.Buffer.Buffer, 1, instanceIndex, depth);
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

        // 3B: the pre-flush of bindings 16-20/25 is gone. Those six lighting inputs live in the per-view globals
        // set 3 now, written before this view's pass chain starts, so no set this loop touches can need a rewrite
        // after the first bind of the frame.

        for (int i = 0; i < _opaquePrimitives.Count; i++)
        {
            var primitive = _opaquePrimitives[i];
            var stream = GetPrimitiveInstanceStream(primitive);
            if (stream == null || stream.Buffer.Buffer.Handle == 0)
                continue;

            // A7: this slot's segment of primitive i's own reordered stream, instance count from args (slot, i). Decided per
            // primitive, but every stream is built in the same PrepareCullDraw, so in practice the whole group goes one way.
            if (TryGetShadowSegment(i, out var sortedBuffer, out ulong instanceOffset, out var argsBuffer, out int slot))
            {
                Pipeline.DrawShadowPrimitiveIndirect(cmd, primitive,
                    primitive.VertexBuffer.Buffer, primitive.IndexBuffer.Buffer,
                    primitive.DescriptorSets[fi],
                    sortedBuffer, instanceOffset,
                    argsBuffer, Season.Rendering.InstanceCull.ArgsOffset(slot, i));
                continue;
            }

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

        // P1: leave the skinning scheduler and release the two blocks it owns. The palette ring is only disposed here when this class
        // created it, which is the same ownership rule the CPU ring above follows.
        _skinning.Dispose();
        _ownedPalette?.Dispose();
        _ownedPalette = null;

        // 2-3 Step C: release the prev SSBOs
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
