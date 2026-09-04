// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Platforms.Web;

/// <summary>
/// WebGPU rendering backend for GPU instancing of GLB models.
/// Owns the cloned glTF runtime plus per-primitive instance streams.
/// The current implementation already includes per-instance animation state,
/// aggregated bone matrices, and morph-weight writes for the unified WGSL pipeline on Web.
/// </summary>
internal sealed class WGPUInstancedModel
{
    const int MaxBonesPerInstance = 100;

    WGPUModel? _runtimeModel;
    readonly List<WGPUPrimitiveData> _primitives = new();
    readonly Dictionary<WGPUPrimitiveData, PrimitiveInstanceStream> _primitiveStreams = new();
    readonly List<GLTFSkin> _skins = new();

    List<GltfNodeBase> _workNodes = new();
    RestPoseSnapshot[] _restPoseSnapshots = Array.Empty<RestPoseSnapshot>();
    InstanceAnimationState[] _animationStates = Array.Empty<InstanceAnimationState>();
    Matrix4x4[] _instanceBoneMatrices = Array.Empty<Matrix4x4>();

    readonly struct RestPoseSnapshot
    {
        public readonly Vector3 Translation;
        public readonly Quaternion Rotation;
        public readonly Vector3 Scale;
        public readonly float[] Weights;

        public RestPoseSnapshot(Vector3 translation, Quaternion rotation, Vector3 scale, float[] weights)
        {
            Translation = translation;
            Rotation = rotation;
            Scale = scale;
            Weights = weights;
        }
    }

    struct InstanceAnimationState
    {
        public bool Initialized;
        public int AnimationClip;
        public float PlaybackTime;
    }

    sealed class PrimitiveInstanceStream
    {
        public Matrix4x4[] Worlds = Array.Empty<Matrix4x4>();
        public Vector4[] MorphWeights = Array.Empty<Vector4>();
        public byte[] InstanceBytes = Array.Empty<byte>();

        // 2-3 Step C (contract clauses 6 + 8(b)(d)): the other side of the double buffer for instance byte streams,
        // structurally identical to the Metal backend.
        // Each instance stores 5 vec4 values (the first 4 = previous-frame world, the 5th = previous-frame morph weights),
        // and JS uploads it to storage binding 14.
        // PrevReady means the stream has been ready for two consecutive frames
        // (false on the first frame or after an instance-count change, which also clears the sentinel bits).
        public byte[] PrevInstanceBytes = Array.Empty<byte>();
        public bool PrevReady;
    }

    public string Name { get; }

    /// <summary>Animation data source after instancing, used as the shared entry point for picking and animation queries
    /// (see InstancedModel.Asset). This matches the runtime asset injected on VK/Metal/DX: a cloned node tree plus an animation player
    /// evaluated every frame. The template asset's player is never evaluated, so its bone matrices always stay empty.
    /// As a result, per-instance skinned narrow-phase picking would skip everything when reading from the template
    /// (<c>hasData</c> also blocks the OBB fallback), which would disable the full hover/selection highlight chain.</summary>
    internal GltfAsset Asset => _runtimeModel!.Asset;

    public IReadOnlyList<WGPUPrimitiveData> Primitives => _primitives;

    public Matrix4x4 View { get; set; } = Matrix4x4.Identity;
    public Matrix4x4 Projection { get; set; } = Matrix4x4.Identity;

    public int EnabledInstanceCount { get; private set; }
    public float ModelAlpha { get; set; } = 1f;
    public bool TransformInitialized { get; set; }
    /// <summary>2-3 Step C: the bone palette has been ready for two consecutive frames, mirroring VK's SetPrevBonesReady.
    /// The actual previous bone buffer is populated automatically by the JS-side shadow copy in uploadSkinnedBones,
    /// and the C# side only controls the ready flag.</summary>
    public bool PrevBonesReady { get; private set; }
    public byte[] BoneMatricesBytes { get; private set; } = Array.Empty<byte>();

    // Unified highlight: per-instance bounds boxes for the current frame (lazy-growing pool + draw list; Wireframe shell extends this in Phase 3).
    internal bool BoundsActive { get; private set; }
    internal readonly List<WebBoundsBox> InstanceBoundsBoxes = new();
    internal readonly List<int> BoundsBoxDrawList = new();

    // Unified highlight: per-instance wireframe shells for the current frame
    // (merged template shell geometry + draw entries captured during Update; no JS-side changes;
    // the per-instance world matrix is baked into uniforms for non-instanced batch draws, following the DrawBoundsBox pattern;
    // previous-world data is decoded from the previous-frame instance stream before the buffer swap).
    // Single-skin assets (all skinned primitives share one Skin) build one merged skinned shell and render it through the instanced skinning path
    // with per-slot bone addressing.
    // Mixed assets (skinned + rigid primitives) render two shells; multi-skin assets skip skinned sources
    // as a documented Phase 1 boundary.
    internal bool WireframeActive { get; private set; }
    internal readonly List<WebShellBox> ShellGeometries = new();
    internal float BuiltShellEdgeWidth;
    // [ShellDiag] One-shot diagnostic log gate for tracking down missing wireframe output in the build/draw path (remove after verification).
    bool _shellBuildLogged;
    internal bool ShellDrawLogged;
    internal readonly List<ShellDrawEntry> ShellDrawList = new();
    PrimitiveInstanceStream? _shellStream;

    // Unified highlight (Outline2D): host-union-per-instance aggregate state
    // (collected during Update, mirroring VKInstancedPrimitiveGroup);
    // per-instance slot list plus the color/width captured from the first active instance
    // (the composited frame color comes from the first active instance, matching picker per-instance writes).
    internal bool Outline2DActive { get; private set; }
    internal bool Outline2DHostActive { get; private set; }
    internal Vector4 Outline2DMaskColor { get; private set; }
    internal float Outline2DMaskWidth { get; private set; }
    internal readonly List<int> Outline2DInstances = new();
    internal readonly List<Vector4> Outline2DInstanceColors = new();
    Vector4 _outline2DInstanceColor;
    float _outline2DInstanceWidth;

    /// <summary>Bone identifier for a skinned model. When non-null, rendering goes through the skinned path.</summary>
    public string? SkinKey { get; set; }

    public WGPUInstancedModel(string name)
    {
        Name = name;
    }

    public void Load(WGPUModel template, Season.Controls.InstancedModel model)
    {
        var wrapperModel = new Season.Controls.Model { Name = Name, Alpha = 1f };
        var runtimeModel = template.CreateInstance(wrapperModel);

        _primitives.Clear();
        _primitives.AddRange(runtimeModel.GetAllPrimitives());

        _primitiveStreams.Clear();
        foreach (var primitive in _primitives)
        {
            if (primitive.BaseVertices != null)
                WGPUModel.UpdatePrimitiveVertexPayload(primitive, primitive.BaseVertices);
            _primitiveStreams[primitive] = new PrimitiveInstanceStream();
        }

        _workNodes = runtimeModel.Asset.gltfNodes;
        SaveRestPoseSnapshots(_workNodes);

        _skins.Clear();
        _skins.AddRange(runtimeModel.Asset.GetAllSkins());

        _runtimeModel = runtimeModel;
        SkinKey = _primitives.Any(static p => p.HasSkinning) ? $"INSTSKIN:{Name}:{model.ID}" : null;
        BoneMatricesBytes = Array.Empty<byte>();
    }

    public IReadOnlyList<string> GetAnimationNames()
    {
        return _runtimeModel?.Asset.GetAnimationNames() ?? Array.Empty<string>();
    }

    public bool TryGetPrimitiveInstanceData(WGPUPrimitiveData primitive, out Matrix4x4[] worlds, out byte[] instanceBytes)
        => TryGetPrimitiveInstanceData(primitive, out worlds, out instanceBytes, out _);

    /// <summary>2-3 Step C overload: also retrieves the previous-frame instance byte stream (empty when no history is available).</summary>
    public bool TryGetPrimitiveInstanceData(WGPUPrimitiveData primitive, out Matrix4x4[] worlds, out byte[] instanceBytes, out byte[] prevInstanceBytes)
    {
        if (_primitiveStreams.TryGetValue(primitive, out var stream))
        {
            worlds = stream.Worlds;
            instanceBytes = stream.InstanceBytes;
            prevInstanceBytes = stream.PrevReady ? stream.PrevInstanceBytes : Array.Empty<byte>();
            return true;
        }

        worlds = Array.Empty<Matrix4x4>();
        instanceBytes = Array.Empty<byte>();
        prevInstanceBytes = Array.Empty<byte>();
        return false;
    }

    public void Update(Season.Controls.InstancedModel model, float time, Season.Basic.Camera camera)
    {
        // Unified highlight: clear the per-instance bounds draw list for this frame
        // (rebuilt every frame; BoundsActive is set by the per-instance hook below).
        BoundsActive = false;
        BoundsBoxDrawList.Clear();
        // Unified highlight: clear the per-instance wireframe shell draw list for this frame
        // (rebuilt every frame; WireframeActive is set by the per-instance hook below).
        WireframeActive = false;
        ShellDrawList.Clear();
        _shellStream = null;
        // Unified highlight: clear the per-instance Outline2D slot list for this frame
        // (rebuilt every frame; aggregate state is written by the hook below and finalized after the loop).
        Outline2DInstances.Clear();
        Outline2DInstanceColors.Clear();

        if (_runtimeModel == null)
            return;

        // 2-3 Step C (mirroring VK's SetPrevBonesReady): if TransformInitialized is already true when entering this Update,
        // then one bone palette has already been uploaded in the previous frame, so the JS-side shadow copy must exist by now
        // and previous bone data is available.
        bool wasInitialized = TransformInitialized;

        EnabledInstanceCount = 0;
        for (int i = 0; i < model.Instances.Count; i++)
        {
            if (model.Instances[i].Enable)
                EnabledInstanceCount++;
        }

        if (EnabledInstanceCount <= 0)
        {
            foreach (var stream in _primitiveStreams.Values)
            {
                stream.Worlds = Array.Empty<Matrix4x4>();
                stream.MorphWeights = Array.Empty<Vector4>();
                stream.InstanceBytes = Array.Empty<byte>();
                // 2-3 Step C: all instances are disabled, so previous-frame history is discarded
                // and restarts from an empty-history state when re-enabled.
                stream.PrevInstanceBytes = Array.Empty<byte>();
                stream.PrevReady = false;
            }
            BoneMatricesBytes = Array.Empty<byte>();
            View = camera.View;
            Projection = camera.Projection;
            TransformInitialized = true;
            PrevBonesReady = false;
            ModelAlpha = model.Alpha;
            // Reset Outline2D aggregate state (all instances disabled -> no outline).
            Outline2DActive = false;
            Outline2DHostActive = false;
            Outline2DMaskColor = default;
            Outline2DMaskWidth = 0f;
            return;
        }

        EnsureAnimationStateCapacity(model.Instances.Count);
        EnsurePrimitiveStreamCapacity(EnabledInstanceCount);
        // Unified highlight (wireframe shell): use the first non-empty instance stream as the source for previous-world decoding
        // because world/previous-world data is consistent per slot across all primitives.
        _shellStream = null;
        foreach (var stream in _primitiveStreams.Values)
        {
            if (stream.Worlds.Length > 0)
            {
                _shellStream = stream;
                break;
            }
        }
        EnsureInstanceBoneCapacity(EnabledInstanceCount);

        bool hasAnimation = _runtimeModel.Asset._animations.Count > 0;
        bool hasSkinning = !string.IsNullOrEmpty(SkinKey) && _skins.Count > 0;

        int writeIndex = 0;
        for (int i = 0; i < model.Instances.Count; i++)
        {
            var instance = model.Instances[i];
            if (!instance.Enable)
                continue;

            // Unified transform pattern: converge on BuildInstanceMatrix (anchor-pivot semantics, see InstancedMesh3DBase).
            var instanceWorld = model.BuildInstanceMatrix(instance);

            RestoreNodesToRestPose();

            if (hasAnimation)
            {
                int clip = instance.AnimationClip;
                if (clip < 0 || clip >= _runtimeModel.Asset._animations.Count)
                    clip = 0;

                ref var state = ref _animationStates[i];
                float nextPlaybackTime;
                if (!state.Initialized || state.AnimationClip != clip)
                {
                    nextPlaybackTime = instance.AnimationTimeOffset + time * instance.AnimationSpeed;
                    state.Initialized = true;
                    state.AnimationClip = clip;
                }
                else
                {
                    nextPlaybackTime = state.PlaybackTime + time * instance.AnimationSpeed;
                }

                state.PlaybackTime = nextPlaybackTime;
                _runtimeModel.Asset._animationPlayer.Evaluate(clip, nextPlaybackTime, _workNodes);
            }
            else
            {
                _runtimeModel.Asset._animationPlayer.UpdateAllNodeTransforms(_workNodes);
            }

            if (hasSkinning)
            {
                _runtimeModel.Asset._animationPlayer.UpdateBoneMatrices(_skins);
                CopyInstanceBoneMatrices(writeIndex, _runtimeModel.Asset._animationPlayer.GetBoneMatricesArray());
            }

            foreach (var primitive in _primitives)
            {
                if (!_primitiveStreams.TryGetValue(primitive, out var stream))
                    continue;

                stream.Worlds[writeIndex] = (primitive.OwnerNode?.WorldTransform ?? Matrix4x4.Identity) * instanceWorld;
                stream.MorphWeights[writeIndex] = ExtractMorphWeights(primitive.OwnerNode);
            }

            // Unified highlight (per-instance bounds box): box alpha/color is independent from the host-wide alpha chain.
            // Do not enable highlighting when extents are near zero (not loaded or degenerate box).
            if (instance.Highlight.Bounds)
            {
                var worldBounds = model.GetInstanceWorldBoundsRaw(instance);
                if (worldBounds.Extents.LengthSquared() >= 1e-12f)
                {
                    BoundsActive = true;
                    var box = AcquireBoundsBox(writeIndex);
                    box.PrevWorld = box.World;
                    box.World = Matrix4x4.CreateScale(worldBounds.Extents * 2f) * Matrix4x4.CreateTranslation(worldBounds.Center);
                    box.FaceColor = instance.Highlight.SurfaceColor;
                    box.FaceAlpha = instance.Highlight.SurfaceColor.W;
                    box.EdgeColor = instance.Highlight.EdgeColor;
                    BoundsBoxDrawList.Add(writeIndex);
                }
            }

            // Unified highlight (per-instance wireframe shell): use the merged template shell
            // (morph primitives and multi-skin primitives are skipped; see EnsureShellGeometry).
            // Each instance contributes the world/previous-world/color captured during Update to the draw list,
            // and previous-world is decoded from the previous-frame instance stream before the buffer swap
            // (empty InstanceBytes on first frame or after a capacity change -> zero-velocity sentinel).
            if (instance.Highlight.Wireframe)
            {
                EnsureShellGeometry(model, model.Highlight.EdgeWidth);
                WireframeActive = true;
                Matrix4x4 prevWorld = default;
                if (_shellStream != null && writeIndex < _shellStream.Worlds.Length
                    && _shellStream.InstanceBytes.Length >= (writeIndex + 1) * 20 * sizeof(float))
                {
                    prevWorld = DecodeWorldFromInstanceBytes(_shellStream.InstanceBytes, writeIndex);
                }
                ShellDrawList.Add(new ShellDrawEntry(
                    writeIndex,
                    _shellStream != null && writeIndex < _shellStream.Worlds.Length ? _shellStream.Worlds[writeIndex] : Matrix4x4.Identity,
                    prevWorld,
                    instance.Highlight.SurfaceColor,
                    instance.Highlight.EdgeColor));
            }

            // Outline2D (per-instance active): record the writeIndex slot and per-instance outline color
            // (per-slot mask uses per-slot color), and also capture the frame-level composited color/width from the first active instance
            // for the host path and Outline2DMaskColor.
            if (instance.Highlight.Outline)
            {
                Outline2DInstances.Add(writeIndex);
                Outline2DInstanceColors.Add(instance.Highlight.OutlineColor);
                if (Outline2DInstances.Count == 1)
                {
                    _outline2DInstanceColor = instance.Highlight.OutlineColor;
                    _outline2DInstanceWidth = instance.Highlight.OutlineWidth;
                }
            }

            writeIndex++;
        }

        // Outline2D active = host active union any active instance
        // (host activation uses the full mask and ignores the per-instance list).
        // Color/width: when any instance is active, prefer the instance values
        // (the panel color written by picker); otherwise fall back to the host values, matching Mesh3D/Model semantics.
        Outline2DHostActive = model.Highlight.Outline;
        bool anyInstanceOutline = Outline2DInstances.Count > 0;
        Outline2DActive = Outline2DHostActive || anyInstanceOutline;
        Outline2DMaskColor = anyInstanceOutline ? _outline2DInstanceColor : model.Highlight.OutlineColor;
        Outline2DMaskWidth = anyInstanceOutline ? _outline2DInstanceWidth : model.Highlight.OutlineWidth;

        foreach (var stream in _primitiveStreams.Values)
            FillInstanceBytes(stream);

        if (hasSkinning)
        {
            // Reinterpret as bytes and write into the persistent buffer to avoid a per-frame ToByteArray allocation.
            int boneByteLength = _instanceBoneMatrices.Length * Unsafe.SizeOf<Matrix4x4>();
            if (BoneMatricesBytes.Length != boneByteLength)
                BoneMatricesBytes = new byte[boneByteLength];
            MemoryMarshal.AsBytes(_instanceBoneMatrices.AsSpan()).CopyTo(BoneMatricesBytes);
        }
        else
        {
            BoneMatricesBytes = Array.Empty<byte>();
        }

        View = camera.View;
        Projection = camera.Projection;
        TransformInitialized = true;
        PrevBonesReady = wasInitialized && hasSkinning;
        ModelAlpha = model.Alpha;
    }

    void SaveRestPoseSnapshots(List<GltfNodeBase> nodes)
    {
        _restPoseSnapshots = new RestPoseSnapshot[nodes.Count];
        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            _restPoseSnapshots[i] = new RestPoseSnapshot(
                node.InitialTranslation,
                node.InitialRotation,
                node.InitialScale,
                node.InitialWeights.Length == 0 ? Array.Empty<float>() : (float[])node.InitialWeights.Clone());
        }
    }

    void RestoreNodesToRestPose()
    {
        for (int i = 0; i < _workNodes.Count; i++)
        {
            var node = _workNodes[i];
            var snapshot = _restPoseSnapshots[i];
            node.Translation = snapshot.Translation;
            node.Rotation = snapshot.Rotation;
            node.Scale = snapshot.Scale;
            node.WeightsVersion = 0;

            if (snapshot.Weights.Length == 0)
            {
                node.Weights = Array.Empty<float>();
                continue;
            }

            if (node.Weights.Length != snapshot.Weights.Length)
                node.Weights = (float[])snapshot.Weights.Clone();
            else
                Array.Copy(snapshot.Weights, node.Weights, snapshot.Weights.Length);
        }
    }

    void EnsureAnimationStateCapacity(int count)
    {
        if (_animationStates.Length >= count)
            return;

        Array.Resize(ref _animationStates, count);
    }

    void EnsurePrimitiveStreamCapacity(int count)
    {
        foreach (var stream in _primitiveStreams.Values)
        {
            if (stream.Worlds.Length == count)
                continue;

            stream.Worlds = new Matrix4x4[count];
            stream.MorphWeights = new Vector4[count];
            stream.InstanceBytes = Array.Empty<byte>();
            // 2-3 Step C: instance-count changes invalidate the per-instance correspondence,
            // so all previous history is discarded and restarts on the next frame.
            stream.PrevInstanceBytes = Array.Empty<byte>();
            stream.PrevReady = false;
        }
    }

    /// <summary>Unified highlight: gets or creates the per-instance bounds box for the compacted writeIndex
    /// (lazy-growing pool, resident after creation; cache keys are stable and unique per slot, so GPU geometry is uploaded only once).</summary>
    WebBoundsBox AcquireBoundsBox(int index)
    {
        while (InstanceBoundsBoxes.Count <= index)
            InstanceBoundsBoxes.Add(WebBoundsBox.Create($"{Name}:INST:{InstanceBoundsBoxes.Count}"));
        return InstanceBoundsBoxes[index];
    }

    /// <summary>Unified highlight (wireframe shell): lazily builds merged template shell geometry.
    /// On the first instance frame with wireframe enabled, all primitives are grouped by source and merged
    /// (faces and edges use separate cache keys, and geometry is uploaded only once).
    /// Skinned primitives using the same Skin are merged into one skinned shell (<c>IsSkinned = 1</c>) rendered through the instanced skinning path,
    /// while rigid primitives are merged into one rigid shell as in the current behavior; mixed assets render both shells.
    /// Morph primitives are skipped because combining instanced skeletons with morph variants is expensive and low-yield,
    /// following the same strategy as VK merged instancing templates.
    /// Multi-skin assets, where each node has its own Skin, cannot express per-skin palette offsets in a merged template,
    /// so skinned sources are skipped in Phase 1 and documented as such; Phase 2 can solve this by baking per-vertex palette offsets.
    /// Degenerate primitives are skipped as well. If every primitive is skipped, no shell is produced, which naturally means no draw calls because the draw list stays empty.
    /// <c>edgeWidth</c> comes from the host Highlight.EdgeWidth (scaled relative to model size), and <c>localSizeMax</c> is the largest dimension of
    /// TemplateLocalSize (the scaling baseline), so the per-primitive baked local thickness is
    /// <c>h = edgeWidth x localSizeMax / nodeScale</c> (see HighlightGeometry.NodeScaleOf).
    /// When these values no longer match the host state, the shell is released and rebuilt immediately in the same frame.</summary>
    void EnsureShellGeometry(Season.Controls.InstancedModel model, float edgeWidth)
    {
        if (ShellGeometries.Count > 0)
        {
            if (BuiltShellEdgeWidth == edgeWidth)
            {
                LogShellBuild("reuse", 0, 0, 0, false);
                return;
            }
            // Edge width changed: invalidate the old shell geometry
            // (JS-side GPU resources are reclaimed by GC) and rebuild with the new width immediately in this frame.
            ShellGeometries.Clear();
        }

        var rigidSources = new List<ShellMeshSource>();
        var skinnedSources = new List<ShellMeshSource>();
        GLTFSkin? sharedSkin = null;
        bool multiSkin = false;
        // [ShellDiag] Counters for primitive skip reasons (morph / degenerate).
        int morphSkipped = 0, degenerateSkipped = 0;
        var localSizeMax = MathF.Max(model.TemplateLocalSize.X, MathF.Max(model.TemplateLocalSize.Y, model.TemplateLocalSize.Z));
        foreach (var prim in _primitives)
        {
            if (prim.MorphTargetCount > 0)
            {
                morphSkipped++;
                continue;
            }
            if (prim.VertexData == null || prim.IndexData == null || prim.VertexData.Length == 0 || prim.IndexData.Length < 3)
            {
                degenerateSkipped++;
                continue;
            }
            var source = new ShellMeshSource(
                WGPUModel.ReconstructVertices(prim), prim.IndexData,
                HighlightGeometry.ComputeShellThickness(edgeWidth, localSizeMax, prim.OwnerNode));

            // The cloned node tree preserves source mapping through skinMap:
            // in a single-skin asset, all primitive OwnerNode.Skin references point to the same instance (checked via ReferenceEquals).
            var skin = prim.HasSkinning ? prim.OwnerNode?.Skin : null;
            if (skin != null)
            {
                if (sharedSkin == null)
                    sharedSkin = skin;
                else if (!ReferenceEquals(sharedSkin, skin))
                    multiSkin = true;
                skinnedSources.Add(source);
            }
            else
            {
                rigidSources.Add(source);
            }
        }

        // Rigid merged shell: current behavior (world matrices are baked into uniforms and drawn per instance, with unchanged cache keys).
        if (rigidSources.Count > 0)
        {
            var rigid = WebShellBox.CreateMerged($"{Name}:INST", rigidSources);
            if (rigid != null)
                ShellGeometries.Add(rigid);
        }
        // Skinned merged shell: only for single-skin assets
        // (multi-skin assets keep the current "skip" behavior and document it as a boundary).
        // Shell vertices carry joints and weights, and drawing uses the instanced skinning path
        // with per-slot bone addressing = instanceIndex x 100 + jointIndex, matching the main pass.
        if (skinnedSources.Count > 0 && !multiSkin)
        {
            var skinned = WebShellBox.CreateMerged($"{Name}:INST:SKIN", skinnedSources, hasSkinning: true);
            if (skinned != null)
                ShellGeometries.Add(skinned);
        }

        BuiltShellEdgeWidth = edgeWidth;
        LogShellBuild("build", morphSkipped, degenerateSkipped, _primitives.Count, multiSkin);
    }

    /// <summary>[ShellDiag] One-shot diagnostic for shell build results
    /// (used to track down missing wireframe output and removed after verification):
    /// source grouping, skip reasons, and the produced shell list, answering whether a shell was built and why it might be empty.</summary>
    void LogShellBuild(string phase, int morphSkipped, int degenerateSkipped, int primCount, bool multiSkin)
    {
        if (_shellBuildLogged)
            return;
        _shellBuildLogged = true;
        var shellsDesc = ShellGeometries.Count == 0
            ? "<empty>"
            : string.Join(" | ", ShellGeometries.ConvertAll(s =>
                $"key={s.FaceCacheKey} skin={s.HasSkinning} fVtx={s.FaceVertexBytes.Length / 80} fIdxB={s.FaceIndexBytes.Length} f32={s.Use32BitFaceIndices} eIdxB={s.EdgeIndexBytes.Length} e32={s.Use32BitEdgeIndices}"));
        DeviceServices.BaseApp?.AddLog(LogType.Backend,
            $"{DateTime.UtcNow} [ShellDiag] EnsureShellGeometry({phase}) model={Name} prims={primCount} morphSkipped={morphSkipped} degenerateSkipped={degenerateSkipped} multiSkin={multiSkin} shells={ShellGeometries.Count} [{shellsDesc}]");
    }

    /// <summary>Unified highlight (wireframe shell): retrieves the current-frame and previous-frame instance byte streams
    /// from the first non-empty primitive stream for the draw side
    /// (world/previous-world data is documented to be consistent per slot across primitives).
    /// This must be called after FillInstanceBytes performs the buffer swap during Update:
    /// after the swap, InstanceBytes is the current-frame data and PrevInstanceBytes is the finished buffer from the previous frame,
    /// with PrevReady gating history validity.</summary>
    internal bool TryGetShellStreamBytes(out byte[] instanceBytes, out byte[] prevInstanceBytes)
    {
        if (_shellStream == null || _shellStream.InstanceBytes.Length == 0)
        {
            instanceBytes = Array.Empty<byte>();
            prevInstanceBytes = Array.Empty<byte>();
            return false;
        }

        instanceBytes = _shellStream.InstanceBytes;
        prevInstanceBytes = _shellStream.PrevReady ? _shellStream.PrevInstanceBytes : Array.Empty<byte>();
        return true;
    }

    /// <summary>Unified highlight (wireframe shell): decodes the previous-frame world matrix for a given slot from an instance byte stream
    /// (16 row-major floats). This must be called before FillInstanceBytes swaps the buffers, because InstanceBytes becomes current-frame data afterward.</summary>
    static Matrix4x4 DecodeWorldFromInstanceBytes(byte[] instanceBytes, int slot)
    {
        var floats = MemoryMarshal.Cast<byte, float>(instanceBytes.AsSpan());
        int o = slot * 20;
        return new Matrix4x4(
            floats[o], floats[o + 1], floats[o + 2], floats[o + 3],
            floats[o + 4], floats[o + 5], floats[o + 6], floats[o + 7],
            floats[o + 8], floats[o + 9], floats[o + 10], floats[o + 11],
            floats[o + 12], floats[o + 13], floats[o + 14], floats[o + 15]);
    }

    void EnsureInstanceBoneCapacity(int instanceCount)
    {
        int required = instanceCount * MaxBonesPerInstance;
        if (_instanceBoneMatrices.Length == required)
            return;

        _instanceBoneMatrices = new Matrix4x4[required];
        for (int i = 0; i < _instanceBoneMatrices.Length; i++)
            _instanceBoneMatrices[i] = Matrix4x4.Identity;
    }

    void CopyInstanceBoneMatrices(int instanceIndex, Matrix4x4[] sourceBones)
    {
        int baseIndex = instanceIndex * MaxBonesPerInstance;
        int count = Math.Min(sourceBones.Length, MaxBonesPerInstance);
        for (int i = 0; i < count; i++)
            _instanceBoneMatrices[baseIndex + i] = sourceBones[i];

        for (int i = count; i < MaxBonesPerInstance; i++)
            _instanceBoneMatrices[baseIndex + i] = Matrix4x4.Identity;
    }

    static Vector4 ExtractMorphWeights(GltfNodeBase? node)
    {
        if (node == null || node.Weights.Length == 0)
            return Vector4.Zero;

        return new Vector4(
            node.Weights.Length > 0 ? node.Weights[0] : 0f,
            node.Weights.Length > 1 ? node.Weights[1] : 0f,
            node.Weights.Length > 2 ? node.Weights[2] : 0f,
            node.Weights.Length > 3 ? node.Weights[3] : 0f);
    }

    // Writes instance world matrices and morph weights into the stream's persistent byte buffer
    // (reallocated only when capacity changes).
    static void FillInstanceBytes(PrimitiveInstanceStream stream)
    {
        var worlds = stream.Worlds;
        var morphWeights = stream.MorphWeights;
        int byteLength = worlds.Length * 20 * sizeof(float);

        // 2-3 Step C (contract clause 6: previous-frame data lives in a CPU shadow copy):
        // swap the byte-stream double buffer.
        // The finished buffer from the previous frame becomes the prev side, and the old prev side is recycled as the current-frame write target.
        // The loop below overwrites all 20 floats per instance, so no clearing is needed.
        // A length mismatch means first frame or a recent capacity change, so history is treated as unavailable.
        if (byteLength > 0 && stream.InstanceBytes.Length == byteLength)
        {
            var lastFrame = stream.InstanceBytes;
            stream.InstanceBytes = stream.PrevInstanceBytes.Length == byteLength
                ? stream.PrevInstanceBytes
                : new byte[byteLength];
            stream.PrevInstanceBytes = lastFrame;
            stream.PrevReady = true;
        }
        else
        {
            if (stream.InstanceBytes.Length != byteLength)
                stream.InstanceBytes = new byte[byteLength];
            stream.PrevInstanceBytes = Array.Empty<byte>();
            stream.PrevReady = false;
        }

        var data = MemoryMarshal.Cast<byte, float>(stream.InstanceBytes.AsSpan());
        for (int i = 0; i < worlds.Length; i++)
        {
            int offset = i * 20;
            var world = worlds[i];
            data[offset] = world.M11;
            data[offset + 1] = world.M12;
            data[offset + 2] = world.M13;
            data[offset + 3] = world.M14;
            data[offset + 4] = world.M21;
            data[offset + 5] = world.M22;
            data[offset + 6] = world.M23;
            data[offset + 7] = world.M24;
            data[offset + 8] = world.M31;
            data[offset + 9] = world.M32;
            data[offset + 10] = world.M33;
            data[offset + 11] = world.M34;
            data[offset + 12] = world.M41;
            data[offset + 13] = world.M42;
            data[offset + 14] = world.M43;
            data[offset + 15] = world.M44;

            var weights = i < morphWeights.Length ? morphWeights[i] : Vector4.Zero;
            data[offset + 16] = weights.X;
            data[offset + 17] = weights.Y;
            data[offset + 18] = weights.Z;
            data[offset + 19] = weights.W;
        }
    }
}
