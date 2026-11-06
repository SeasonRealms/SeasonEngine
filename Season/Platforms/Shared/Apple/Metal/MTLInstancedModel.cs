// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Metal;
using Season.Models;
using System.Runtime.CompilerServices;

namespace Season.Platforms.Shared.Apple.Metal;

/// <summary>
/// GPU instancing rendering backend for GLB models on Metal.
/// It extracts primitives from a shared MTLModel template, reusing VB, IB, and textures,
/// creates its own Material and Matrix UBOs,
/// and performs instanced rendering through Pipeline.
/// Version 2 also supports skeletal animation and morph targets with per-instance animation state.
/// </summary>
internal sealed class MTLInstancedModel : MTLInstancedPrimitiveGroup
{
    readonly GltfAsset _asset = new();
    
        /// <summary>Animation data source after instancing, exposed through the shared-layer entry points for picking and animation queries. See InstancedModel.Asset.</summary>
        internal GltfAsset Asset => _asset;

    int _bonePaletteStride = 1;
    IMTLBuffer[] _instanceBoneBuffers = Array.Empty<IMTLBuffer>();
    uint _instanceBoneCapacity = 1;

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

    /// <summary>
    /// P1: the GPU skinning route's frame state - the palette ring the draws bind, the load-time bake the kernel evaluates, and the
    /// 16-byte per-instance parameter block. Shared with the other three backends so the layout cannot drift between them.
    /// Metal needs nothing at creation time and nothing at submit time for the ring itself - an MTLBuffer has no usage bits and the
    /// driver's hazard tracking orders the producer's dispatch against the draw - so the whole backend-side cost is the buffer handle.
    /// </summary>
    readonly Season.Rendering.InstanceSkinningTarget _skinning = new();

    /// <summary>
    /// True while this frame runs the GPU route. Latched once per Update so a frame cannot half-switch between the two palettes.
    /// False is the historical path, untouched: <c>Evaluate</c> per instance, the palette written into the shared buffer by
    /// <see cref="UploadInstanceBoneMatrices"/>, and no storage ring bound.
    /// </summary>
    bool _gpuSkin;

    /// <summary>The palette ring this class created because the model asked for the GPU route without supplying one. Owned here,
    /// disposed here; a ring the model supplied is never touched.</summary>
    Season.Rendering.InstanceBoneStorage? _ownedPalette;

    (Vector3 Translation, Quaternion Rotation, Vector3 Scale, float[] Weights)[] _restPoseSnapshot = Array.Empty<(Vector3, Quaternion, Vector3, float[])>();
    List<GltfNodeBase> _workNodes = new();
    readonly List<GLTFSkin> _skins = new();
    InstanceAnimationState[] _animationStates = Array.Empty<InstanceAnimationState>();
    PrimitiveInstanceStream[] _primitiveInstanceStreams = Array.Empty<PrimitiveInstanceStream>();

    // P1: last sampled pose per instance (owner-node worlds / morph weights / bone palette). A throttled frame never
    // evaluates an instance's clip, and the shared node tree cannot hold every instance's pose at once, so the streams of
    // a skipped instance are rebuilt from here instead. Rows are keyed by the Instances index, see the cache's contract.
    readonly InstancePoseCache _poseCache = new();

    // P2: compacted slots that just re-entered the main camera's frustum. Their previous-frame instance record is republished
    // from this frame's data after the upload, so the motion-vector pass reads zero velocity across the frames the instance spent
    // culled instead of one frame of the distance it travelled. Rebuilt every Update.
    readonly List<int> _historyResetSlots = new();

    struct InstanceAnimationState
    {
        public bool Initialized;
        public int AnimationClip;
        public float PlaybackTime;
    }

    sealed class PrimitiveInstanceStream
    {
        /// <summary>
        /// Contract clause 8(c) of 2-3: the per-primitive instance stream is a frame ring, one slot per in-flight frame.
        /// Each frame fully rewrites the [FrameIndex] side, while the [FrameIndex - 1] side holds the previous-frame
        /// per-instance world transforms and morph weights and is bound directly as the prev source at VS buffer(9).
        /// Two slots would alias: with frameCount frames in flight, frame k would overwrite the slot frame k-2 still reads.
        /// </summary>
        public IMTLBuffer?[] Buffers = Array.Empty<IMTLBuffer?>();
        public InstanceTransformData[] Data = Array.Empty<InstanceTransformData>();
        public Matrix4x4[] Worlds = Array.Empty<Matrix4x4>();
        public int Capacity;

        public IMTLBuffer? Buffer
            => (uint)Device.FrameIndex < (uint)Buffers.Length ? Buffers[Device.FrameIndex] : null;

        /// <summary>The previous-frame side. If no history exists yet, it falls back to the current-frame side, which is already zeroed so the r3.w == 0 sentinel remains effective.</summary>
        public IMTLBuffer? PrevBuffer
            => ((uint)PrevFrameIndex < (uint)Buffers.Length ? Buffers[PrevFrameIndex] : null) ?? Buffer;

        public void Upload(int count)
        {
            var buffer = Buffer;
            if (buffer != null && count > 0)
                Device.ResourceManager.UpdateBuffer(buffer, new ReadOnlySpan<InstanceTransformData>(Data, 0, count));
        }
    }

    public MTLInstancedModel(string name) : base(name)
    {
    }

    /// <summary>
    /// Loads primitives from the shared model template. The caller must ensure template has already been loaded.
    /// <paramref name="skinningMode"/> is the owning control's <c>SkinningMode</c>, copied in because this is the only point where the
    /// control and the loaded template meet: it decides whether a GPU bake is attempted, and a model that declines keeps the CPU route.
    /// </summary>
    public void Load(MTLModel template, Season.Controls.Model model, InstanceSkinningMode skinningMode = InstanceSkinningMode.Cpu)
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
        _bonePaletteStride = Math.Max(1, _skins.Sum(static s => s.Joints.Count)) + Season.Rendering.InstanceSkinning.PoseTagRows;

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
                WriteStruct(primitive.MaterialBuffers[f], primitive.MaterialParams);
        }
    }

    /// <summary>
    /// Clones one template primitive for instanced rendering:
    /// - VB, IB, and textures keep shared pointer references and do not create new GPU resources
    /// - Matrix UBO is rebuilt locally with World = Identity, while instance transforms come from the instance buffer
    /// - Material UBO is rebuilt locally because alpha may differ
    /// - Model-level material overrides such as MaterialColor and Unlit are applied
    /// </summary>
    PrimitiveData CloneForInstancing(PrimitiveData source, Season.Controls.Model model)
    {
        var clone = new PrimitiveData
        {
            Vertices = source.Vertices,
            BaseVertices = source.BaseVertices,
            MorphTargets = source.MorphTargets != null ? new List<GLTFMorphTarget>(source.MorphTargets) : null,
            Indices = source.Indices,
            Use32BitIndices = source.Use32BitIndices,
            DoubleSided = source.DoubleSided,
            LocalBoundsCenter = source.LocalBoundsCenter,
            LocalBoundsExtents = source.LocalBoundsExtents,

            // Part identity, carried so InstanceTintParts can still be matched after the clone.
            PartName = source.PartName,
            PartNodeName = source.PartNodeName,
            PartBaseColor = source.PartBaseColor,

            // Shared GPU geometry.
            VertexBuffer = source.VertexBuffer,
            IndexBuffer = source.IndexBuffer,

            // Shared textures.
            BaseColorTexture = source.BaseColorTexture,
            NormalTexture = source.NormalTexture,
            MetallicRoughnessTexture = source.MetallicRoughnessTexture,
            OcclusionTexture = source.OcclusionTexture,
            EmissiveTexture = source.EmissiveTexture,

            // Copy material parameters and apply model-level overrides.
            MaterialParams = source.MaterialParams,
            OriginalBaseColorAlpha = source.OriginalBaseColorAlpha,
            OriginalAlphaCutoff = source.OriginalAlphaCutoff,
            IsTransparent = source.IsTransparent,
        };

        // Apply Model.MaterialColor and Unlit.
        var colorTint = model.MaterialColor ?? Vector4.One;
        clone.MaterialParams.BaseColor *= colorTint;
        clone.MaterialParams.RenderMode = model.Unlit ? 0u : 1u;
        clone.MaterialParams.IsInstanced = 1;
        clone.MaterialParams.IsSkinned = 0;
        clone.MaterialParams.BonePaletteStride = 1;
        clone.OriginalBaseColorAlpha = clone.MaterialParams.BaseColor.W;

        if (clone.BaseVertices != null && clone.MorphTargets != null && clone.MorphTargets.Count > 0)
        {
            CreateMorphDeltaBuffer(clone, clone.BaseVertices, clone.MorphTargets);
            clone.MaterialParams.HasMorphTargets = 1u;
            clone.MaterialParams.MorphTargetCount = (uint)clone.MorphTargets.Count;
            clone.MaterialParams.MorphVertexCount = (uint)clone.BaseVertices.Length;
            clone.MaterialParams.MorphWeights = Vector4.Zero;
        }
        else
        {
            clone.MaterialParams.HasMorphTargets = 0u;
            clone.MaterialParams.MorphTargetCount = 0u;
            clone.MaterialParams.MorphVertexCount = 0u;
            clone.MaterialParams.MorphWeights = Vector4.Zero;
        }

        // Create a dedicated Matrix UBO.
        CreateMatrixBuffer(clone);
        // Create a dedicated Material UBO.
        CreateMaterialBuffer(clone);

        // Initialize matrices with World = Identity, while instance transforms come from the instance buffer.
        var matrices = new MatrixBuffer
        {
            World = Matrix4x4.Transpose(Matrix4x4.Identity),
            // Contract clause 8(d) of 2-3:
            // PrevWorld in b0 stays all zeros on the instanced path because history comes from the double-buffered instance stream.
        };

        for (int i = 0; i < Device.frameCount; i++)
        {
            WriteStruct(clone.MatrixBuffers[i], matrices);
            WriteStruct(clone.MaterialBuffers[i], clone.MaterialParams);
        }

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
                // Version-2 picking:
                // PickMesh is immutable after loading, so it is shared by reference without deep copying,
                // and NodeIndex stays consistent on both sides.
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
            if (stream.Capacity >= count && stream.Buffers.Length == Device.frameCount)
                continue;

            // For 2-3, rebuild and clear every slot together when capacity changes,
            // because Metal CreateBuffer does not guarantee zero-filled memory.
            // This prevents stale instance indices from producing fake velocity after capacity changes.
            nuint size = (nuint)(Unsafe.SizeOf<InstanceTransformData>() * count);
            // The old ring starts empty and may lag Device.frameCount, so it is indexed only where it reaches.
            var previous = stream.Buffers;
            var buffers = new IMTLBuffer?[Device.frameCount];
            for (int slot = 0; slot < buffers.Length; slot++)
            {
                if ((uint)slot < (uint)previous.Length)
                    previous[slot]?.Dispose();
                var buffer = Device.ResourceManager.CreateVertexBuffer<InstanceTransformData>((uint)count);
                unsafe
                {
                    new Span<byte>((void*)buffer.Contents, (int)size).Clear();
                }
                buffers[slot] = buffer;
            }
            stream.Buffers = buffers;

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

            for (int face = 0; face < stream.Buffers.Length; face++)
            {
                stream.Buffers[face]?.Dispose();
                stream.Buffers[face] = null;
            }

            stream.Data = Array.Empty<InstanceTransformData>();
            stream.Worlds = Array.Empty<Matrix4x4>();
            stream.Capacity = 0;
        }
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
    /// root instance worlds in the base <see cref="MTLInstancedPrimitiveGroup._instanceData"/> against the model-space clip-union box.
    /// The previous-frame side of each stream (buffer 9) needs no reordering either: the VS indexes it by the row's tag, and that tag is
    /// exactly the compacted slot this stream was written in.
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
            // Contract clauses 8(b) and 8(c) of 2-3:
            // previous per-instance world transforms and morph weights come from the opposite side
            // of the double-buffered instance stream,
            // and the previous bone palette comes from the [fi-1] slot of the per-instance bone-frame ring.
            // These bits are only seeded here; Update republishes them every frame from
            // InstancedModel.InstanceHistoryValid (see MTLInstancedPrimitiveGroup.UpdatePrevStateFlags),
            // so a frame whose enabled order shifted withdraws them and the shaders fall back to current data.
            primitive.MaterialParams.HasPrevInstanceWorld = 1u;
            primitive.MaterialParams.HasPrevBones = primitive.MaterialParams.IsSkinned;
            for (int fi = 0; fi < Device.frameCount; fi++)
                WriteStruct(primitive.MaterialBuffers[fi], primitive.MaterialParams);
        }
    }

    void CreateInstanceBoneBuffers()
    {
        _instanceBoneBuffers = new IMTLBuffer[Device.frameCount];
        _instanceBoneCapacity = 1;
        CreateOrResizeInstanceBoneBuffers(_instanceBoneCapacity);
    }

    void EnsureInstanceBoneCapacity(uint capacity)
    {
        if (capacity <= _instanceBoneCapacity)
            return;

        CreateOrResizeInstanceBoneBuffers(capacity);
    }

    void CreateOrResizeInstanceBoneBuffers(uint capacity)
    {
        nuint size = (nuint)(Unsafe.SizeOf<Matrix4x4>() * Math.Max(1, _bonePaletteStride * (int)capacity));
        for (int i = 0; i < Device.frameCount; i++)
        {
            _instanceBoneBuffers[i]?.Dispose();
            _instanceBoneBuffers[i] = Device.ResourceManager.CreateBuffer(size);
            // Contract clause 8(b) of 2-3:
            // clear to zero instead of Identity.
            // When a cold slot, such as the first frame or a slot after resize, reads a zero matrix,
            // the shader naturally falls back to the current bone matrix by checking the per-joint Bh[3][3] == 0 sentinel,
            // and velocity becomes zero.
            unsafe
            {
                new Span<byte>((void*)_instanceBoneBuffers[i].Contents, (int)size).Clear();
            }
        }

        _instanceBoneCapacity = capacity;
    }

    void UploadInstanceBoneMatrices(int instanceIndex, Matrix4x4[] boneMatrices)
    {
        if (boneMatrices.Length == 0 || _instanceBoneBuffers.Length == 0)
            return;

        int fi = Device.FrameIndex;
        nuint offset = (nuint)(instanceIndex * _bonePaletteStride * Unsafe.SizeOf<Matrix4x4>());
        Device.ResourceManager.UpdateBuffer(_instanceBoneBuffers[fi], boneMatrices, offset);

        // P2 L1 / P3 L2: the tag row is the one row of the block this upload does not fill from the pose, and it has to read zero
        // here. A ring slot is reused every frame, so a row left over from an earlier pooled frame - or from a compute handoff the
        // CPU route took back - would send the vertex stage to a pool block this route never wrote. The two lanes are adjacent, so
        // one two-word copy covers both. Cleared unconditionally rather than only when the route changed, so there is one rule for
        // the row instead of two.
        nuint tagOffset = offset + (nuint)((_bonePaletteStride - Season.Rendering.InstanceSkinning.PoseTagRows)
            * Unsafe.SizeOf<Matrix4x4>() + Season.Rendering.InstanceSkinning.PoseTagCurrentOffset);
        Device.ResourceManager.UpdateBuffer(_instanceBoneBuffers[fi], ZeroPoseTag, tagOffset);
    }

    /// <summary>P2 L1 / P3 L2: zero words for the tag lanes, which sit at offsets 48 and 52 of the tag row.</summary>
    static readonly uint[] ZeroPoseTag = new uint[2];

    /// <summary>
    /// P0/P1: latches this frame's palette source. Runs before the empty-list early-out, so a group that stops submitting never leaves
    /// the scheduler holding the previous frame's instance count. The storage ring only exists on the GPU route.
    /// </summary>
    void BeginSkinningFrame(InstancedModel model)
    {
        // A one-slot ring has no previous frame to point at; Ready would then be reading the slot it just wrote as history.
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
        _gpuSkin = _skinning.Begin(paletteRing, _asset.SkinBake, _bonePaletteStride, _asset._animations.Count,
            Season.Rendering.InstanceSkinning.PoseTagRows);
    }

    /// <summary>
    /// P1: shapes the ring for this frame. The ring mirrors the backend's frame ring, so its depth is the backend's choice: growing it
    /// here keeps slot arithmetic and "previous frame" meaning the same thing on both sides. Returns false when the CPU route is the
    /// only correct answer, which the caller applies by clearing the latch.
    /// </summary>
    bool ShapeSkinningRing()
    {
        int slots = (int)Device.frameCount;
        int slot = (int)Device.FrameIndex % slots;
        return _skinning.PreparePalette(Season.Basic.Graphics.Instance, slots, slot, (slot + slots - 1) % slots, _instanceCount)
            && _skinning.BeginFrame(_instanceCount);
    }

    IMTLBuffer GetInstanceBoneBuffer(int frameIndex)
    {
        // P1: the ring's slot for this frame is the palette. It is laid out instance-major exactly like the CPU buffer, so the draw
        // offsets the shader and the callers already compute stay valid without a single call-site change. The gate is this frame's latch
        // rather than the handoff result: on this route the CPU ring was never allocated, so falling through after a failed handoff would
        // draw the rest pose, while the ring still holds the last palette the kernel produced.
        if (_gpuSkin && _skinning.Palette?.Current is MTLStorageBuffer storageBone)
            return storageBone.Buffer;

        if (_instanceBoneBuffers.Length == 0 || _instanceBoneBuffers[frameIndex] == null)
            return IdentityInstanceBoneBuffers[frameIndex];

        return _instanceBoneBuffers[frameIndex];
    }

    /// <summary>Shell rendering for skinned shells uses the current-frame per-instance bone-frame ring. Non-skinned shells follow the base-class Identity path.</summary>
    protected override IMTLBuffer InstanceBoneBufferForDraw(int fi) => GetInstanceBoneBuffer(fi);

    /// <summary>Shell rendering for skinned shells uses the previous-frame per-instance bone-frame ring at slot [fi-1], sharing the same source as the main pass.</summary>
    protected override IMTLBuffer PrevInstanceBoneBufferForDraw(int fi)
        => _gpuSkin && _skinning.Palette?.Previous is MTLStorageBuffer storageBone
            ? storageBone.Buffer
            : GetInstanceBoneBuffer(PrevFrameIndex);

    /// <summary>The per-instance bone-frame ring always exists on this path (created in Load), or the GPU route's storage ring stands in
    /// for it, so the group can publish the previous-bone bit; the per-primitive IsSkinned gate still decides which primitives read it.</summary>
    protected override bool HasPrevBoneHistory => _instanceBoneBuffers.Length > 0 || _gpuSkin;

    /// <summary>Bone addressing stride for shell rendering on skinned shells: the number of palette matrices per instance, where boneOffset = 64B × stride × slot.
    /// It is the same number in ring mode: the ring is laid out instance-major, so the same offset formula lands on the same rows.</summary>
    protected override int ShellBonePaletteStride => _bonePaletteStride;

    public IReadOnlyList<string> GetAnimationNames()
    {
        return _asset.GetAnimationNames();
    }

    /// <summary>
    /// P2: republishes this frame's record for every slot that just came back into view into the previous-frame half of the
    /// double buffer, so the motion vector computed against it is zero for exactly this frame.
    ///
    /// The record is copied whole (world and morph weights): this backend's shader reads both from the same 80-byte record and
    /// gates them together on r3.w, so patching one without the other would make the pair disagree. Nothing is written while the
    /// two halves are the same buffer, which only happens before the first swap -- and then there is no history to reset anyway.
    /// </summary>
    void WriteHistoryResets()
    {
        if (_historyResetSlots.Count == 0)
            return;

        foreach (var stream in _primitiveInstanceStreams)
        {
            if (stream == null)
                continue;

            var prev = stream.PrevBuffer;
            if (prev == null || ReferenceEquals(prev, stream.Buffer))
                continue;

            for (int k = 0; k < _historyResetSlots.Count; k++)
            {
                int slot = _historyResetSlots[k];
                if ((uint)slot >= (uint)_instanceCount)
                    continue;

                Device.ResourceManager.UpdateBuffer(prev,
                    new ReadOnlySpan<InstanceTransformData>(stream.Data, slot, 1),
                    (nuint)(slot * Unsafe.SizeOf<InstanceTransformData>()));
            }
        }
    }

    public void Update(InstancedModel model, float time)
    {
        bool wasInitialized = _transformInitialized;
        _instanceCount = 0;
        _historyResetSlots.Clear();

        // Unified highlighting:
        // clear this frame's per-instance Bounds and Wireframe draw lists.
        // They are rebuilt every frame, and _boundsActive and _wireframeActive
        // are set later by the per-instance hooks below.
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
            _skinning.Begin(null, null, 0, 0);
            _gpuSkin = false;
            _transformInitialized = true;
            SyncAlpha(model.Alpha);
            SetOutline2DState(false, default, default);
            return;
        }

        EnsureAnimationStateCapacity(model.Instances.Count);
        EnsurePrimitiveInstanceCapacity(_instanceCount);
        // P1: shape the GPU route's ring first, then allocate the CPU palette buffer only if it will actually be written.
        if (_gpuSkin && !ShapeSkinningRing())
        {
            _skinning.Begin(null, null, 0, 0);
            _gpuSkin = false;
        }
        if (!_gpuSkin)
            EnsureInstanceBoneCapacity((uint)Math.Max(_instanceCount, 1));
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

            // Unified positioning contract:
            // converge on BuildInstanceMatrix using the anchor pivot described by InstancedMesh3DBase.
            var instanceWorld = model.InstanceWorld(i);
            _instanceData[writeIndex] = InstanceTransformData.FromWorld(instanceWorld);
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
                    instanceData.MorphWeights = samplePose
                        ? ExtractMorphWeights(primitive.OwnerNode)
                        : _poseCache.MorphWeights[i][p];
                    if (samplePose)
                        _poseCache.MorphWeights[i][p] = instanceData.MorphWeights;
                }
                stream.Data[writeIndex] = instanceData;
            }

            // Outline2D when activated per instance:
            // record the writeIndex slot and per-instance outline color so the per-slot mask can fetch color by slot.
            // The first outlined instance also captures the frame-level composite color and width,
            // which are used by the host path and SetOutline2DState.
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

            // Unified highlighting for per-instance bounds boxes:
            // box alpha and color remain independent from the host-wide alpha chain.
            // Do not light the box when extents are near zero, such as unloaded or degenerate bounds.
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

            // Unified highlighting for per-instance wireframe:
            // lazily build shared shell templates, which stay resident after the first successful creation,
            // plus per-instance shell boxes whose matrices are addressed by the instance-stream writeIndex slot
            // and then drawn per instance.
            // Hybrid assets draw both rigid and skinned shells,
            // and the skinned shell follows animation through the per-instance bone-palette path.
            // When both templates are unavailable, such as no usable primitives, morphs, or multiple-skin cases,
            // the box stays null and is not added to the draw list.
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
        // dispatch that turns it into a palette, and the compute-write-to-vertex-read handoff that makes it readable, are both recorded
        // by InstanceSkinningEffect at FrameStart - outside any render or compute encoder, which Metal requires.

        // P1: publish the pose generation the shadow-atlas reuse digest keys on. It advances only on frames where at least
        // one instance's pose was actually replayed -- exactly when the rendered pose set can differ -- so throttled and held
        // frames keep the cached atlas and any real motion invalidates it. A static host never advances it.
        // The GPU route replays every instance every frame, so every frame can differ.
        if (hasAnimation && (anySampled || _gpuSkin))
            model.AnimationPoseGeneration++;

        // Outline2D activation is the union of host-wide activation and any per-instance activation.
        // Host-wide activation uses the full mask and ignores the per-instance list.
        // For color and width, per-instance activation takes priority and uses the instance values,
        // typically panel colors written by the picker.
        // Otherwise the host values are used, matching Mesh3D and Model semantics.
        _outline2DHostActive = model.Highlight.Outline;
        bool anyInstanceOutline = _outline2DInstances.Count > 0;
        SetOutline2DState(_outline2DHostActive || anyInstanceOutline,
            anyInstanceOutline ? _outline2DInstanceColor : model.Highlight.OutlineColor,
            anyInstanceOutline ? _outline2DInstanceWidth : model.Highlight.OutlineWidth);

        foreach (var stream in _primitiveInstanceStreams)
        {
            // For 2-3, advance the double-buffer write side so the opposite side automatically becomes
            // this frame's previous-instance stream.
            stream?.Upload(_instanceCount);
        }
        UploadInstanceExtras(_instanceCount);

        // P2: instances that just came back into view get one frame of zero velocity. This has to run after Upload, because the
        // prev side is the other half of the same double buffer and must not be written before the current side is.
        WriteHistoryResets();

        // A7: the box must be in the root instance's model space and cover every clip pose, because the kernel culls the whole
        // instance with it once and every primitive stream only replays that decision. The cull-side uploads themselves wait for
        // PrepareCullInputs (a blit encoder cannot be opened while a render encoder may still be open during Update).
        SetCullLocalBounds(model.TemplateCullBounds);
        UpdateCullUpload(model);

        int fi = Device.FrameIndex;
        var matrices = new MatrixBuffer
        {
            World = Matrix4x4.Transpose(Matrix4x4.Identity),
            // Contract clause 8(d) of 2-3:
            // PrevWorld in b0 remains all zeros because per-instance history on the instanced path
            // comes from the opposite side of the double-buffered instance stream at VS buffer 9,
            // not from b0.
        };
        foreach (var primitive in _primitives)
            WriteStruct(primitive.MatrixBuffers[fi], matrices);

        _transformInitialized = true;

        // 2-3 Step C: publishes whether the previous-frame streams may be read this frame. Not a one-shot call any more: the
        // history is addressed by the compacted instance slot, so it only lines up while the enabled order is unchanged
        // (see InstancedMesh3DBase.InstanceHistoryValid). The first frame after load has no history and passes false.
        UpdatePrevStateFlags(wasInitialized && model.InstanceHistoryValid);
        SyncAlpha(model.Alpha);
    }

    public new void Draw()
    {
        if (!_transformInitialized || _instanceCount == 0)
            return;

        var enc = Device.GraphicsEncoder;
        bool forceFadeByAlpha = _currentAlpha < 1.0f;
        int fi = Device.FrameIndex;
        var morphFallback = DefaultMorphDeltasBuffers[fi];
        var instanceBoneBuffer = GetInstanceBoneBuffer(fi);
        // Contract clause 8(b) of 2-3:
        // the per-instance bone-frame ring is fully rewritten every frame,
        // so slot [fi-1] is the previous-frame palette.
        // It is zeroed during creation and resize, and the shader falls back to the current bone
        // on cold slots using the per-joint Bh[3][3] == 0 sentinel.
        var prevInstanceBoneBuffer = GetInstanceBoneBuffer(PrevFrameIndex);

        // A7 main view: each opaque primitive reads the main-view segment of its own reordered stream, with the instance count from
        // the args entry (MainViewSlot, i). The bone / prev-bone tables need no reordering of their own: the tag gather writes into each
        // record IS the compacted instance slot (bone offset = stride × slot), which is exactly how those tables are laid out, so the
        // reordered rows line up row for row and skinned models need nothing extra.
        // Fade stays direct for the same reason as the base class (per-instance back-to-front splitting).
        for (int i = 0; i < _opaquePrimitives.Count; i++)
        {
            var primitive = _opaquePrimitives[i];
            var stream = GetPrimitiveInstanceStream(primitive);
            if (stream == null || stream.Buffer == null)
                continue;

            if (!forceFadeByAlpha && TryGetMainViewSegment(i, out var sortedBuffer, out nuint segmentOffset, out var argsBuffer))
            {
                Pipeline.SetPipeline(enc, PipelineMode.Opaque, primitive.DoubleSided);
                Pipeline.DrawPrimitive(enc, primitive, primitive.VertexBuffer, primitive.IndexBuffer,
                    primitive.MatrixBuffers[fi], primitive.MaterialBuffers[fi],
                    LightConstantBuffers[fi], IdentityBoneBuffers[fi], primitive.MorphDeltasBuffer ?? morphFallback, instanceBoneBuffer,
                    MTLPrimitiveType.Triangle, (nuint)primitive.Indices.Length,
                    primitive.Use32BitIndices ? MTLIndexType.UInt32 : MTLIndexType.UInt16, 0,
                    sortedBuffer, segmentOffset, 0, 0,
                    stream.PrevBuffer, prevInstanceBoneBuffer,
                    indirectBuffer: argsBuffer,
                    indirectOffset: Season.Rendering.InstanceCull.ArgsOffset(Season.Rendering.InstanceCull.MainViewSlot, i),
                    instanceExtrasBuffer: CurrentInstanceExtras);
                Season.Rendering.InstanceCull.MainViewDraws++;
                continue;
            }

            Pipeline.SetPipeline(enc, forceFadeByAlpha ? PipelineMode.Fade : PipelineMode.Opaque, primitive.DoubleSided);
            Pipeline.DrawPrimitive(enc, primitive, primitive.VertexBuffer, primitive.IndexBuffer,
                primitive.MatrixBuffers[fi], primitive.MaterialBuffers[fi],
                LightConstantBuffers[fi], IdentityBoneBuffers[fi], primitive.MorphDeltasBuffer ?? morphFallback, instanceBoneBuffer,
                MTLPrimitiveType.Triangle, (nuint)primitive.Indices.Length,
                primitive.Use32BitIndices ? MTLIndexType.UInt32 : MTLIndexType.UInt16, 0,
                stream.Buffer, 0, (nuint)_instanceCount, 0,
                stream.PrevBuffer, prevInstanceBoneBuffer, instanceExtrasBuffer: CurrentInstanceExtras);
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

            for (int orderIndex = 0; orderIndex < _transparentInstanceOrder.Count; orderIndex++)
            {
                int instanceIndex = _transparentInstanceOrder[orderIndex];
                nuint instOffset = (nuint)(Unsafe.SizeOf<InstanceTransformData>() * instanceIndex);
                nuint boneOffset = (nuint)(Unsafe.SizeOf<Matrix4x4>() * _bonePaletteStride * instanceIndex);
                if (primitive.DoubleSided)
                {
                    Pipeline.SetPipeline(enc, PipelineMode.Transparent, false);
                    enc.SetCullMode(MTLCullMode.Front);
                    Pipeline.DrawPrimitive(enc, primitive, primitive.VertexBuffer, primitive.IndexBuffer,
                        primitive.MatrixBuffers[fi], primitive.MaterialBuffers[fi],
                        LightConstantBuffers[fi], IdentityBoneBuffers[fi], primitive.MorphDeltasBuffer ?? morphFallback, instanceBoneBuffer,
                        MTLPrimitiveType.Triangle, (nuint)primitive.Indices.Length,
                        primitive.Use32BitIndices ? MTLIndexType.UInt32 : MTLIndexType.UInt16, 0,
                        stream.Buffer, instOffset, 1, boneOffset,
                        stream.PrevBuffer, prevInstanceBoneBuffer, boneOffset, instanceExtrasBuffer: CurrentInstanceExtras);
                }

                Pipeline.SetPipeline(enc, PipelineMode.Transparent, false);
                enc.SetCullMode(MTLCullMode.Back);
                Pipeline.DrawPrimitive(enc, primitive, primitive.VertexBuffer, primitive.IndexBuffer,
                    primitive.MatrixBuffers[fi], primitive.MaterialBuffers[fi],
                    LightConstantBuffers[fi], IdentityBoneBuffers[fi], primitive.MorphDeltasBuffer ?? morphFallback, instanceBoneBuffer,
                    MTLPrimitiveType.Triangle, (nuint)primitive.Indices.Length,
                    primitive.Use32BitIndices ? MTLIndexType.UInt32 : MTLIndexType.UInt16, 0,
                    stream.Buffer, instOffset, 1, boneOffset,
                    stream.PrevBuffer, prevInstanceBoneBuffer, boneOffset, instanceExtrasBuffer: CurrentInstanceExtras);
            }
        }

        // Unified highlighting for per-instance bounds boxes plus wireframe shell boxes.
        // It uses the instances enabled for this frame, draws transparent faces in two passes plus opaque edges,
        // and runs after all surfaces have finished.
        if (_boundsActive)
            DrawBoundsBoxes();
        if (_wireframeActive)
        {
            // Shell-box matrices are addressed by the instance-stream slot.
            // Use the first per-primitive stream that has a valid buffer, matching the DX structure:
            // all streams carry the same source content, so any stream is acceptable.
            // For multi-node assets this remains an approximation, which matches documented DX behavior.
            foreach (var stream in _primitiveInstanceStreams)
            {
                if (stream != null && stream.Buffer != null)
                {
                    DrawShellBoxes(stream.Buffer, stream.PrevBuffer);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Instanced-model shadow rendering for render-quality 1-5.
    /// It uses per-primitive streams plus the per-instance bone buffer and shares the same data source as Draw.
    /// Only opaque buckets are rendered, because true BLEND materials do not cast shadows per contract clause 7,
    /// and the shadow PSO has already been bound centrally by RenderShadowPass.
    /// </summary>
    public override void DrawShadow()
    {
        if (!_transformInitialized || _instanceCount == 0)
            return;

        var enc = Device.GraphicsEncoder;
        int fi = Device.FrameIndex;
        var morphFallback = DefaultMorphDeltasBuffers[fi];
        var instanceBoneBuffer = GetInstanceBoneBuffer(fi);

        // When b2 and t5, meaning Metal slots 4 and 5, are identical within the group,
        // bind them only for the first primitive. See CanShareShadowMaterial.
        bool shareMaterial = CanShareShadowMaterial(_opaquePrimitives);
        bool materialBound = false;

        for (int i = 0; i < _opaquePrimitives.Count; i++)
        {
            var primitive = _opaquePrimitives[i];
            var stream = GetPrimitiveInstanceStream(primitive);
            if (stream == null || stream.Buffer == null)
                continue;

            // A7: this slot's segment of primitive i's own reordered stream, instance count from args (slot, i). Decided per
            // primitive, but every stream is checked by the same PrepareCullDraw, so in practice the whole group goes one way.
            if (TryGetShadowSegment(i, out var sortedBuffer, out nuint instanceOffset, out var argsBuffer, out int slot))
            {
                Pipeline.DrawShadowPrimitiveIndirect(enc, primitive, primitive.VertexBuffer, primitive.IndexBuffer,
                    primitive.MatrixBuffers[fi], primitive.MaterialBuffers[fi], IdentityBoneBuffers[fi],
                    primitive.MorphDeltasBuffer ?? morphFallback, instanceBoneBuffer,
                    primitive.Use32BitIndices ? MTLIndexType.UInt32 : MTLIndexType.UInt16,
                    sortedBuffer, instanceOffset, argsBuffer,
                    Season.Rendering.InstanceCull.ArgsOffset(slot, i),
                    bindMaterial: !shareMaterial || !materialBound);
                materialBound = true;
                continue;
            }

            Pipeline.DrawShadowPrimitive(enc, primitive, primitive.VertexBuffer, primitive.IndexBuffer,
                primitive.MatrixBuffers[fi], primitive.MaterialBuffers[fi], IdentityBoneBuffers[fi],
                primitive.MorphDeltasBuffer ?? morphFallback, instanceBoneBuffer,
                (nuint)primitive.Indices.Length, primitive.Use32BitIndices ? MTLIndexType.UInt32 : MTLIndexType.UInt16,
                stream.Buffer, 0, (nuint)_instanceCount, 0,
                bindMaterial: !shareMaterial || !materialBound);
            // Primitives missing an instance stream are skipped by continue and submit no bindings,
            // so mark materialBound only after a real draw has happened.
            materialBound = true;
        }
    }

    /// <summary>
    /// Phase 4 Outline2D mask rendering.
    /// It uses per-primitive streams plus the per-instance bone buffer and shares the same data source as Draw.
    /// MTLInstancedModel does not maintain the base-class _instanceBuffer,
    /// because instance streams are resolved through GetPrimitiveInstanceStream.
    /// For that reason it cannot reuse the base-class DrawOutlineMask
    /// and must draw stream by stream, mirroring the override in VKInstancedModel.
    /// Host activation uses the full mask; otherwise rendering is per instance through _outline2DInstances.
    /// Only opaque buckets are drawn.
    /// </summary>
    public override void DrawOutlineMask()
    {
        if (!_transformInitialized || _instanceCount == 0 || !Outline2DActive)
            return;

        var enc = Device.GraphicsEncoder;
        int fi = Device.FrameIndex;
        var morphFallback = DefaultMorphDeltasBuffers[fi];
        var instanceBoneBuffer = GetInstanceBoneBuffer(fi);
        var prevInstanceBoneBuffer = GetInstanceBoneBuffer(PrevFrameIndex);
        Pipeline.SetOutlineMaskColor(enc, _outline2DColor);

        for (int i = 0; i < _opaquePrimitives.Count; i++)
        {
            var primitive = _opaquePrimitives[i];
            var stream = GetPrimitiveInstanceStream(primitive);
            if (stream == null || stream.Buffer == null)
                continue;

            Pipeline.SetPipeline(enc, PipelineMode.Opaque, primitive.DoubleSided);
            enc.SetDepthStencilState(Pipeline.OutlineMaskDepthState);

            if (_outline2DHostActive)
            {
                Pipeline.DrawPrimitive(enc, primitive, primitive.VertexBuffer, primitive.IndexBuffer,
                    primitive.MatrixBuffers[fi], primitive.MaterialBuffers[fi],
                    LightConstantBuffers[fi], IdentityBoneBuffers[fi], primitive.MorphDeltasBuffer ?? morphFallback, instanceBoneBuffer,
                    MTLPrimitiveType.Triangle, (nuint)primitive.Indices.Length,
                    primitive.Use32BitIndices ? MTLIndexType.UInt32 : MTLIndexType.UInt16, 0,
                    stream.Buffer, 0, (nuint)_instanceCount, 0,
                    stream.PrevBuffer, prevInstanceBoneBuffer, instanceExtrasBuffer: CurrentInstanceExtras);
            }
            else
            {
                for (int k = 0; k < _outline2DInstances.Count; k++)
                {
                    int idx = _outline2DInstances[k];
                    if ((uint)idx >= (uint)_instanceCount)
                        continue;
                    // Write this instance's own OutlineColor for the current slot,
                    // so the per-slot mask can fetch color by slot.
                    Pipeline.SetOutlineMaskColor(enc, _outline2DInstanceColors[k]);
                    nuint instOffset = (nuint)(Unsafe.SizeOf<InstanceTransformData>() * idx);
                    nuint boneOffset = (nuint)(Unsafe.SizeOf<Matrix4x4>() * _bonePaletteStride * idx);
                    Pipeline.DrawPrimitive(enc, primitive, primitive.VertexBuffer, primitive.IndexBuffer,
                        primitive.MatrixBuffers[fi], primitive.MaterialBuffers[fi],
                        LightConstantBuffers[fi], IdentityBoneBuffers[fi], primitive.MorphDeltasBuffer ?? morphFallback, instanceBoneBuffer,
                        MTLPrimitiveType.Triangle, (nuint)primitive.Indices.Length,
                        primitive.Use32BitIndices ? MTLIndexType.UInt32 : MTLIndexType.UInt16, 0,
                        stream.Buffer, instOffset, 1, boneOffset,
                        stream.PrevBuffer, prevInstanceBoneBuffer, boneOffset, instanceExtrasBuffer: CurrentInstanceExtras);
                }
            }
        }
    }

    protected override void CollectPrimitives(List<PrimitiveData> result)
    {
        result.AddRange(_primitives);
    }

    /// <summary>
    /// Releases only the resources owned by this object:
    /// MatrixBuffers, MaterialBuffers, MorphBuffer, InstanceBuffers, and BoneBuffers.
    /// VB, IB, and textures are owned by the template model and are not released here.
    /// </summary>
    public override void Dispose()
    {
        ReleasePrimitiveInstanceBuffers();

        // P1: leave the skinning scheduler and release the two blocks it owns. The palette ring is only disposed here when this class
        // created it, which is the same ownership rule the CPU buffer below follows.
        _skinning.Dispose();
        _ownedPalette?.Dispose();
        _ownedPalette = null;

        foreach (var primitive in _primitives)
        {
            if (primitive.MatrixBuffers != null)
            {
                for (int i = 0; i < primitive.MatrixBuffers.Length; i++)
                    primitive.MatrixBuffers[i]?.Dispose();
                primitive.MatrixBuffers = null!;
            }

            if (primitive.MaterialBuffers != null)
            {
                for (int i = 0; i < primitive.MaterialBuffers.Length; i++)
                    primitive.MaterialBuffers[i]?.Dispose();
                primitive.MaterialBuffers = null!;
            }

            if (primitive.OwnsMorphDeltasBuffer)
            {
                primitive.MorphDeltasBuffer?.Dispose();
                primitive.MorphDeltasBuffer = null;
                primitive.OwnsMorphDeltasBuffer = false;
            }
        }

        if (_instanceBoneBuffers != null)
        {
            for (int i = 0; i < _instanceBoneBuffers.Length; i++)
                _instanceBoneBuffers[i]?.Dispose();
        }

        _instanceBoneBuffers = Array.Empty<IMTLBuffer>();
        _primitiveInstanceStreams = Array.Empty<PrimitiveInstanceStream>();
        _animationStates = Array.Empty<InstanceAnimationState>();

        // Unified highlighting: release the highlight pool for bounds instance boxes.
        DisposeHighlights();

        base.Dispose();
    }
}
