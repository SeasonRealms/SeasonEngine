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
        /// <summary>
        /// Contract clause 8(c) of 2-3:
        /// double-buffered per-primitive instance streams.
        /// Each frame fully rewrites the [WriteIndex] side,
        /// while the opposite side holds the previous-frame per-instance world transforms and morph weights
        /// and is bound directly as the prev source at VS buffer(9).
        /// </summary>
        public readonly IMTLBuffer?[] Buffers = new IMTLBuffer?[2];
        public int WriteIndex;
        public InstanceTransformData[] Data = Array.Empty<InstanceTransformData>();
        public Matrix4x4[] Worlds = Array.Empty<Matrix4x4>();
        public int Capacity;

        public IMTLBuffer? Buffer => Buffers[WriteIndex];

        /// <summary>The previous-frame write side. If no history exists yet, it falls back to the current-frame side, which is already zeroed so the r3.w == 0 sentinel remains effective.</summary>
        public IMTLBuffer? PrevBuffer => Buffers[WriteIndex ^ 1] ?? Buffers[WriteIndex];

        public void FlipAndUpload(int count)
        {
            WriteIndex ^= 1;
            var buffer = Buffers[WriteIndex];
            if (buffer != null && count > 0)
                Device.ResourceManager.UpdateBuffer(buffer, new ReadOnlySpan<InstanceTransformData>(Data, 0, count));
        }
    }

    public MTLInstancedModel(string name) : base(name)
    {
    }

    /// <summary>
    /// Loads primitives from the shared model template. The caller must ensure template has already been loaded.
    /// </summary>
    public void Load(MTLModel template, Season.Controls.Model model, Camera camera)
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
    /// Clones one template primitive for instanced rendering:
    /// - VB, IB, and textures keep shared pointer references and do not create new GPU resources
    /// - Matrix UBO is rebuilt locally with World = Identity, while instance transforms come from the instance buffer
    /// - Material UBO is rebuilt locally because alpha may differ
    /// - Model-level material overrides such as MaterialColor and Unlit are applied
    /// </summary>
    PrimitiveData CloneForInstancing(PrimitiveData source, Season.Controls.Model model, Camera camera)
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
            View = Matrix4x4.Transpose(camera.View),
            Projection = Matrix4x4.Transpose(camera.Projection),
            // Contract clause 8(d) of 2-3:
            // PrevWorld in b0 stays all zeros on the instanced path because history comes from the double-buffered instance stream.
            PrevViewProjection = Matrix4x4.Transpose(camera.PrevViewProjection),
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
            if (stream.Capacity >= count && stream.Buffers[0] != null)
                continue;

            // For 2-3, rebuild and clear both sides together when capacity changes,
            // because Metal CreateBuffer does not guarantee zero-filled memory.
            // This prevents stale instance indices from producing fake velocity after capacity changes.
            nuint size = (nuint)(Unsafe.SizeOf<InstanceTransformData>() * count);
            for (int face = 0; face < stream.Buffers.Length; face++)
            {
                stream.Buffers[face]?.Dispose();
                var buffer = Device.ResourceManager.CreateVertexBuffer<InstanceTransformData>((uint)count);
                unsafe
                {
                    new Span<byte>((void*)buffer.Contents, (int)size).Clear();
                }
                stream.Buffers[face] = buffer;
            }

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
            // Cold states for both are already covered by zero-value sentinels in the data itself,
            // so the sentinel flags can be enabled here once.
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
    }

    IMTLBuffer GetInstanceBoneBuffer(int frameIndex)
    {
        if (_instanceBoneBuffers.Length == 0 || _instanceBoneBuffers[frameIndex] == null)
            return IdentityInstanceBoneBuffers[frameIndex];

        return _instanceBoneBuffers[frameIndex];
    }

    /// <summary>Shell rendering for skinned shells uses the current-frame per-instance bone-frame ring. Non-skinned shells follow the base-class Identity path.</summary>
    protected override IMTLBuffer InstanceBoneBufferForDraw(int fi) => GetInstanceBoneBuffer(fi);

    /// <summary>Shell rendering for skinned shells uses the previous-frame per-instance bone-frame ring at slot [fi-1], sharing the same source as the main pass.</summary>
    protected override IMTLBuffer PrevInstanceBoneBufferForDraw(int fi) => GetInstanceBoneBuffer(PrevFrameIndex);

    /// <summary>Bone addressing stride for shell rendering on skinned shells: the number of palette matrices per instance, where boneOffset = 64B × stride × slot.</summary>
    protected override int ShellBonePaletteStride => _bonePaletteStride;

    public IReadOnlyList<string> GetAnimationNames()
    {
        return _asset.GetAnimationNames();
    }

    public void Update(InstancedModel model, float time)
    {
        _instanceCount = 0;

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

        bool hasAnimation = _asset._animations.Count > 0;
        bool hasSkin = _skins.Count > 0;
        float deltaTime = Math.Max(time, 0f);

        int writeIndex = 0;
        for (int i = 0; i < model.Instances.Count; i++)
        {
            var instance = model.Instances[i];
            if (!instance.Enable)
                continue;

            // Unified positioning contract:
            // converge on BuildInstanceMatrix using the anchor pivot described by InstancedMesh3DBase.
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
                    instanceData.MorphWeights = ExtractMorphWeights(primitive.OwnerNode);
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
            stream?.FlipAndUpload(_instanceCount);
        }

        int fi = Device.FrameIndex;
        var matrices = new MatrixBuffer
        {
            World = Matrix4x4.Transpose(Matrix4x4.Identity),
            View = Matrix4x4.Transpose(Camera.View),
            Projection = Matrix4x4.Transpose(Camera.Projection),
            // Contract clause 8(d) of 2-3:
            // PrevWorld in b0 remains all zeros because per-instance history on the instanced path
            // comes from the opposite side of the double-buffered instance stream at VS buffer 9,
            // not from b0.
            PrevViewProjection = Matrix4x4.Transpose(Camera.PrevViewProjection),
        };
        foreach (var primitive in _primitives)
            WriteStruct(primitive.MatrixBuffers[fi], matrices);

        _transformInitialized = true;
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

        for (int i = 0; i < _opaquePrimitives.Count; i++)
        {
            var primitive = _opaquePrimitives[i];
            var stream = GetPrimitiveInstanceStream(primitive);
            if (stream == null || stream.Buffer == null)
                continue;

            Pipeline.SetPipeline(enc, forceFadeByAlpha ? PipelineMode.Fade : PipelineMode.Opaque, primitive.DoubleSided);
            Pipeline.DrawPrimitive(enc, primitive, primitive.VertexBuffer, primitive.IndexBuffer,
                primitive.MatrixBuffers[fi], primitive.MaterialBuffers[fi],
                LightConstantBuffers[fi], IdentityBoneBuffers[fi], primitive.MorphDeltasBuffer ?? morphFallback, instanceBoneBuffer,
                MTLPrimitiveType.Triangle, (nuint)primitive.Indices.Length,
                primitive.Use32BitIndices ? MTLIndexType.UInt32 : MTLIndexType.UInt16, 0,
                stream.Buffer, 0, (nuint)_instanceCount, 0,
                stream.PrevBuffer, prevInstanceBoneBuffer);
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
                        stream.PrevBuffer, prevInstanceBoneBuffer, boneOffset);
                }

                Pipeline.SetPipeline(enc, PipelineMode.Transparent, false);
                enc.SetCullMode(MTLCullMode.Back);
                Pipeline.DrawPrimitive(enc, primitive, primitive.VertexBuffer, primitive.IndexBuffer,
                    primitive.MatrixBuffers[fi], primitive.MaterialBuffers[fi],
                    LightConstantBuffers[fi], IdentityBoneBuffers[fi], primitive.MorphDeltasBuffer ?? morphFallback, instanceBoneBuffer,
                    MTLPrimitiveType.Triangle, (nuint)primitive.Indices.Length,
                    primitive.Use32BitIndices ? MTLIndexType.UInt32 : MTLIndexType.UInt16, 0,
                    stream.Buffer, instOffset, 1, boneOffset,
                    stream.PrevBuffer, prevInstanceBoneBuffer, boneOffset);
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
                    stream.PrevBuffer, prevInstanceBoneBuffer);
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
                        stream.PrevBuffer, prevInstanceBoneBuffer, boneOffset);
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
