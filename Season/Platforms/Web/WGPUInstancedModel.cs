// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Platforms.Web;

/// <summary>
/// WebGPU rendering backend for GPU instancing of GLB models.
/// Owns the cloned glTF runtime plus per-primitive instance streams.
/// The current implementation already includes per-instance animation state,
/// aggregated bone matrices, and morph-weight writes for the unified WGSL pipeline on Web.
/// A7: also the GPU cull target of the model (see the A7 section below), since the Web side has no primitive-group object to hang it on.
///
/// A7c note -- the cull input is still a dedicated storage buffer uploaded every frame. A GPUBuffer's usage is fixed at creation time and the IA
/// instance buffer is created as a vertex buffer through the JS interop layer, so aliasing it would mean changing that layer's buffer creation rather
/// than the cull path. The bytes are identical, so the culling result matches the D3D12 path.
/// </summary>
internal sealed class WGPUInstancedModel : Season.Rendering.IInstanceCullTarget
{
    const int MaxBonesPerInstance = 100;

    WGPUModel? _runtimeModel;
    readonly List<WGPUPrimitiveData> _primitives = new();
    readonly Dictionary<WGPUPrimitiveData, PrimitiveInstanceStream> _primitiveStreams = new();
    /// <summary>Primitives named by <see cref="Season.Controls.InstancedModel.InstanceTintParts"/>, i.e. the ones whose uniform
    /// carries <see cref="WebGPUTextureFlags.TintInstanced"/>. Built once after Load, never rebuilt.</summary>
    readonly HashSet<WGPUPrimitiveData> _tintPrimitives = new();
    readonly List<GLTFSkin> _skins = new();

    List<GltfNodeBase> _workNodes = new();
    RestPoseSnapshot[] _restPoseSnapshots = Array.Empty<RestPoseSnapshot>();
    InstanceAnimationState[] _animationStates = Array.Empty<InstanceAnimationState>();
    Matrix4x4[] _instanceBoneMatrices = Array.Empty<Matrix4x4>();

    // P1: last sampled pose per instance (owner-node worlds / morph weights / bone palette). A throttled frame never
    // evaluates an instance's clip, and the shared node tree cannot hold every instance's pose at once, so the streams of
    // a skipped instance are rebuilt from here instead. Rows are keyed by the Instances index, see the cache's contract.
    readonly InstancePoseCache _poseCache = new();

    // P2: compacted slots that just re-entered the main camera's frustum. Their previous-frame bytes are republished from this
    // frame's record after the swap, so the motion-vector pass reads zero velocity across the frames the instance spent culled
    // instead of one frame of the distance it travelled. Rebuilt every Update.
    readonly List<int> _historyResetSlots = new();

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


    public int EnabledInstanceCount { get; private set; }
    /// <summary>Generic per-instance extras (binding 24): 32B per compacted slot, <c>.color</c> = that slot's
    /// <c>InstanceSpec.Color</c>. Rebuilt every Update in the same order as the instance streams, so it lines up with the
    /// compacted slot the VS decodes (direct draw: instance_id, A7: the gather's M14 stamp).</summary>
    public byte[] InstanceExtrasBytes { get; private set; } = Array.Empty<byte>();
    public float ModelAlpha { get; set; } = 1f;
    public bool TransformInitialized { get; set; }
    /// <summary>2-3 Step C: the bone palette has been ready for two consecutive frames, mirroring VK's SetPrevBonesReady.
    /// The actual previous bone buffer is populated automatically by the JS-side shadow copy in uploadSkinnedBones,
    /// and the C# side only controls the ready flag.</summary>
    public bool PrevBonesReady { get; private set; }

    /// <summary>2-3 Step C: whether the previous-frame streams still line up with this frame, taken from the control's
    /// <see cref="Season.Controls.InstancedMesh3DBase.InstanceHistoryValid"/>. They are addressed by the compacted instance slot,
    /// so a frame whose enabled order changed must not read them: each instance would be paired with its predecessor, which is a
    /// one-frame velocity spike on everything past the change. False makes every draw fall back to current-frame data.</summary>
    public bool InstanceHistoryValid { get; private set; }
    public byte[] BoneMatricesBytes { get; private set; } = Array.Empty<byte>();

    /// <summary>
    /// P1: the shared latch for this frame's palette ring, bake and per-instance parameter rows. The ring arrives from the control
    /// (<see cref="Season.Controls.InstancedModel.BoneStorage"/>) or is allocated here for the GPU route. On this backend the swap is two
    /// storage buffers bound as the current and previous palettes of <see cref="SkinKey"/>, which the draw paths already look up by that key,
    /// so no draw code changes. Its stride must stay <see cref="MaxBonesPerInstance"/>: the vertex stage addresses the palette at
    /// <c>instance * 100</c> regardless of which route produced it.
    /// </summary>
    readonly Season.Rendering.InstanceSkinningTarget _skinning = new();

    /// <summary>Whether this frame runs the GPU route: the palette comes from the FrameStart kernel and the CPU replay below is skipped.
    /// False is the historical route in full, which is what keeps the A/B comparison meaningful.</summary>
    bool _gpuSkin;

    /// <summary>The ring this model allocated for the GPU route when the control did not supply one. Disposed with the model; a ring supplied
    /// by the control is used as-is and never disposed here.</summary>
    Season.Rendering.InstanceBoneStorage? _ownedPalette;

    /// <summary>The ids last handed to JS, so a frame that does not advance the ring does not re-enter interop for nothing.</summary>
    string? _boneStorageBoundCurrent;
    string? _boneStorageBoundPrevious;

    /// <summary>Frames this model has advanced its ring by. There is no backend frame ring on Web, so the model is the clock both the
    /// producer and the consumer read their slot from.</summary>
    uint _boneStorageFrame;

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
    /// <summary>Parameter rows the loop above produced, indexed by the Instances index, so a bucket found later in the same replay
    /// can address the representative without assuming the write index equals the Instances index.</summary>
    (int Write, uint Clip, float Time)[] _posePoolPending = Array.Empty<(int, uint, float)>();

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

    // === A7 per-instance GPU culling ===
    // Same shape as WGPUInstancedMesh3D (JS handles only, uploads deferred to PrepareCullInputs outside any Pass), with the multi-stream
    // difference of a glTF model: the visibility decision is made once per instance from the root instance world against the model-space
    // clip-union box (InstancedModel.TemplateCullBounds), while every opaque primitive draws its own instance stream (node world x instance
    // world, plus its own morph weights), so gather materializes one reordered stream per primitive in the parameter table.

    /// <summary>Position of each primitive in <see cref="_primitives"/>, i.e. its bit in the parameter-table mask. Built once in Load.</summary>
    readonly Dictionary<WGPUPrimitiveData, int> _primitiveIndex = new();
    /// <summary>Root instance worlds in write order, 80B per instance like every instance stream (four rows + a zero vec4). None of the
    /// primitive streams can stand in for it: each one is pre-multiplied by its own node world, which the cull box is not expressed in.</summary>
    byte[] _cullSourceBytes = Array.Empty<byte>();
    Season.Rendering.StorageBuffer? _cullSource;
    Season.Rendering.StorageBuffer? _cullArgs;
    Season.Rendering.StorageBuffer? _cullVisible;
    /// <summary>Per gather stream, in parameter-table order: the primitive's own instance stream as uploaded for gather to read.</summary>
    readonly Season.Rendering.StorageBuffer?[] _cullStreamSources = new Season.Rendering.StorageBuffer?[Season.Rendering.InstanceCull.MaxPrimitives];
    /// <summary>Per gather stream, in parameter-table order: the slot-tiled reordered stream the draw side binds as its instance stream.</summary>
    readonly Season.Rendering.StorageBuffer?[] _cullStreamSorted = new Season.Rendering.StorageBuffer?[Season.Rendering.InstanceCull.MaxPrimitives];
    int _cullCapacity = -1;
    /// <summary>Number of parameter-table entries (and therefore gather streams) already uploaded to the GPU.</summary>
    int _cullPrimitiveCount = -1;
    byte[]? _cullPrimPendingBytes;
    int _cullPrimPendingCount = -1;
    /// <summary>A7b: the arg-buffer image composed from <see cref="_cullPrimPendingBytes"/>. It exists for the same reason as that table --
    /// its content only changes with the primitive set -- and it is what replaced the per-frame reset dispatch.</summary>
    byte[]? _cullArgsTemplateBytes;
    /// <summary>Uploaded parameter-table signature, one bit per entry of <see cref="_primitives"/>; the draw side derives each primitive's
    /// args record and reordered stream from it, so it only ever changes together with the table it describes.</summary>
    int _cullPrimMask = -1;
    /// <summary>Mask of the table built on the CPU but not uploaded yet (see WGPUInstancedMesh3D for why the two are kept apart).</summary>
    int _cullPrimPendingMask = -1;
    readonly int[] _cullPrimIndexCounts = new int[Season.Rendering.InstanceCull.MaxPrimitives];
    bool _cullRegistered;
    Vector3 _cullLocalCenter;
    Vector3 _cullLocalExtents;
    bool _cullDrawReady;
    /// <summary>Latched from the owner's draw path once per Update, so the cull record and the draw side of one frame agree on it.</summary>
    bool _cullMainView;

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
        _primitiveIndex.Clear();
        for (int i = 0; i < _primitives.Count; i++)
            _primitiveIndex[_primitives[i]] = i;

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

        // P1: build the load-time bake for the GPU route, once, while the node tree is still at its rest pose. It also owns the
        // eligibility test (every animated node being a joint among them), so an ineligible model simply gets no bake and keeps the CPU
        // replay no matter what the property says.
        Season.Models.InstanceSkinBake.TryBake(runtimeModel.Asset, model.SkinningMode);

        _runtimeModel = runtimeModel;
        SkinKey = _primitives.Any(static p => p.HasSkinning) ? $"INSTSKIN:{Name}:{model.ID}" : null;
        BoneMatricesBytes = Array.Empty<byte>();

        // Registered for the whole life of the model: the effect asks every target once per frame, and a target with no bake (or a frame
        // that fell back to the CPU) answers false in one branch.
        Season.Rendering.InstanceSkinning.Register(_skinning);
    }

    /// <summary>
    /// Applies <see cref="Season.Controls.InstancedModel.InstanceTintParts"/> to the primitives: matching ones are flagged for
    /// the tint gate and their authored RGB is rewritten to white.
    /// <para>
    /// The white rewrite is not cosmetic. The per-instance colour multiplies the albedo, so a saturated authored factor
    /// (this asset's sail is 0.97/0.93/0.0003) could only ever be darkened toward black, never turned green or blue.
    /// Setting RGB to 1 turns the same multiply into a replacement, which is what lets one geometry pass through all
    /// seven hues. Alpha is untouched: opacity stays with the material and the control's own Alpha chain.
    /// </para>
    /// <para>Called once after <see cref="Load"/>, while no draw can see the primitive yet.</para>
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

            _tintPrimitives.Add(primitive);
            primitive.BaseColor = new Vector4(1f, 1f, 1f, primitive.BaseColor.W);
            primitive.PartBaseColor = Vector3.One;
        }
    }

    /// <summary>Whether this primitive's uniform should carry <see cref="WebGPUTextureFlags.TintInstanced"/>.</summary>
    internal bool IsTintPrimitive(WGPUPrimitiveData primitive) => _tintPrimitives.Contains(primitive);

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
            prevInstanceBytes = stream.PrevReady && InstanceHistoryValid ? stream.PrevInstanceBytes : Array.Empty<byte>();
            return true;
        }

        worlds = Array.Empty<Matrix4x4>();
        instanceBytes = Array.Empty<byte>();
        prevInstanceBytes = Array.Empty<byte>();
        return false;
    }

    public void Update(Season.Controls.InstancedModel model, float time)
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
        _historyResetSlots.Clear();

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

        // P1: latch the palette source with the frame, before the early return, so a frame with nothing to draw also clears the latch
        // instead of leaving the kernel pointed at the previous frame's parameter block. The ring only exists on the GPU route - a model on
        // the CPU route never allocates one, which is what keeps its path byte-identical to what it was before P1. A model whose primitives
        // carry no skinning has nothing that reads a palette, so it never takes the route even when the asset itself could be baked.
        bool skinnable = !string.IsNullOrEmpty(SkinKey);
        Season.Rendering.InstanceBoneStorage? paletteRing = skinnable ? model.BoneStorage : null;
        if (skinnable && model.GpuSkinning)
        {
            if (paletteRing == null)
            {
                _ownedPalette ??= new Season.Rendering.InstanceBoneStorage();
                paletteRing = _ownedPalette;
            }
        }
        else if (_ownedPalette != null)
        {
            // The mode, the process-wide override, or the model itself turned the GPU route off after the ring had been allocated for it.
            _ownedPalette.Dispose();
            _ownedPalette = null;
        }

        // The Web palette is addressed at MaxBonesPerInstance rows per instance by the vertex stage, so that fixed stride - not the model's
        // joint total - is what the ring is shaped with; the shared latch rejects a bake that would not fit in it.
        //
        // P3 L2 asks for the tag row on top of the joints, and this is the one backend where that can fail: a bake filling all
        // MaxBonesPerInstance rows leaves no free row at the end. Rather than dropping a model that used to take the GPU route, the
        // request degrades to zero tag rows - the kernel then writes no tag, the vertex stage reads joint MaxBonesPerInstance - 1
        // whose two lanes an affine palette keeps at zero anyway, and the parameter write below skips pooling for the frame. The
        // route survives; only the sharing does not.
        var skinBake = _runtimeModel.Asset.SkinBake;
        int tagRows = skinBake != null && skinBake.PaletteStride + Season.Rendering.InstanceSkinning.PoseTagRows <= MaxBonesPerInstance
            ? Season.Rendering.InstanceSkinning.PoseTagRows
            : 0;
        _gpuSkin = _skinning.Begin(paletteRing, skinBake, MaxBonesPerInstance, _runtimeModel.Asset._animations.Count, tagRows);

        if (EnabledInstanceCount <= 0)
        {
            // Nothing reads a palette this frame: drop the target so the kernel skips it, and let the next frame re-latch.
            _skinning.Begin(null, null, 0, 0);
            _gpuSkin = false;
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
            InstanceExtrasBytes = Array.Empty<byte>();
            // No instance draws this frame, so nothing reads the ring either: release the binding so the next upload refills the CPU side.
            UnbindBoneStorageFromJs();
            TransformInitialized = true;
            PrevBonesReady = false;
            InstanceHistoryValid = false;
            ModelAlpha = model.Alpha;
            // Reset Outline2D aggregate state (all instances disabled -> no outline).
            Outline2DActive = false;
            Outline2DHostActive = false;
            Outline2DMaskColor = default;
            Outline2DMaskWidth = 0f;
            _cullDrawReady = false;
            _cullMainView = false;
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
        // P1: shape the GPU route's palette ring before anything else, so the CPU palette block below is allocated only when it will
        // actually be written. Web has no backend frame ring, so this model is the clock: the slot advanced here is both the one the
        // kernel writes and the one the draw path binds, which is what keeps the two on the same buffer.
        if (_gpuSkin && !ShapeSkinningRing(EnabledInstanceCount))
        {
            // The ring cannot be allocated at this shape, or the parameter block cannot be sized. The CPU route below is then the only
            // correct answer, and it is reachable: the CPU block is allocated on the next line.
            _skinning.Begin(null, null, 0, 0);
            _gpuSkin = false;
        }

        // On the GPU route the CPU palette block is neither written nor uploaded, so it is not allocated: that layout is the per-frame
        // upload's source, and its only remaining purpose was to be the fallback for a frame the kernel did not serve.
        if (!_gpuSkin)
            EnsureInstanceBoneCapacity(EnabledInstanceCount);

        bool hasAnimation = _runtimeModel.Asset._animations.Count > 0;
        bool hasSkinning = skinnable && _skins.Count > 0;
        float deltaTime = Math.Max(time, 0f);
        bool anySampled = false;

        // P1: one pose row per instance, sized by the primitive layout and the palette stride. The Web path's palette is
        // fixed at MaxBonesPerInstance per instance, so the cache matches what CopyInstanceBoneMatrices expects.
        _poseCache.Ensure(model.Instances.Count, _primitives.Count,
            hasSkinning ? Math.Min(MaxBonesPerInstance, Math.Max(1, _skins.Sum(static s => s.Joints.Count))) : 0);

        // A7 cull input: every 80B record is fully overwritten below, so the buffer is only reallocated when the instance count changes.
        int cullSourceBytes = EnabledInstanceCount * (int)Season.Rendering.InstanceCull.WorldStride;
        if (_cullSourceBytes.Length != cullSourceBytes)
            _cullSourceBytes = new byte[cullSourceBytes];
        var cullRoots = MemoryMarshal.Cast<byte, float>(_cullSourceBytes.AsSpan());

        // Generic per-instance extras: 32B per compacted slot, filled in the same loop and addressed by the same writeIndex as
        // every primitive's matrix rows, so a disabled instance cannot shift the two streams apart.
        int extrasByteLength = EnabledInstanceCount * WGPUInstanceExtras.BytesPerInstance;
        if (InstanceExtrasBytes.Length != extrasByteLength)
            InstanceExtrasBytes = new byte[extrasByteLength];
        var extrasData = MemoryMarshal.Cast<byte, float>(InstanceExtrasBytes.AsSpan());

        int writeIndex = 0;

        // P3 L2: the pool lives for exactly one replay, like P3a's dedup table it is keyed against. A pool block addressed by a
        // previous frame's tag would still hold that frame's pose, which is why a slot's tag is written for every instance every
        // frame rather than only for the ones that end up pooled.
        //
        // pooling is read from the latched target, not from tagRows above: a ring that could not be shaped calls Begin(null, ...) and
        // zeroes TagRows, and a pool decided against a tag nobody will write is a render error rather than wasted work.
        bool pooling = _gpuSkin && _skinning.TagRows > 0;
        _posePoolBucketBySource.Clear();
        _posePoolRewrites.Clear();
        if (_posePoolPending.Length < model.Instances.Count)
            _posePoolPending = new (int Write, uint Clip, float Time)[model.Instances.Count];
        for (int i = 0; i < model.Instances.Count; i++)
        {
            var instance = model.Instances[i];
            if (!instance.Enable)
                continue;

            // Unified transform pattern: converge on BuildInstanceMatrix (anchor-pivot semantics, see InstancedMesh3DBase).
            var instanceWorld = model.InstanceWorld(i);
            WriteCullRoot(cullRoots, writeIndex, instanceWorld);

            int extrasBase = writeIndex * WGPUInstanceExtras.FloatsPerInstance;
            extrasData[extrasBase] = instance.Color.X;
            extrasData[extrasBase + 1] = instance.Color.Y;
            extrasData[extrasBase + 2] = instance.Color.Z;
            extrasData[extrasBase + 3] = instance.Color.W;
            // The reserved scalar is cleared every frame rather than on allocation: the array is reused when the count holds.
            extrasData[extrasBase + 4] = 0f;
            extrasData[extrasBase + 5] = 0f;
            extrasData[extrasBase + 6] = 0f;
            extrasData[extrasBase + 7] = 0f;

            // P2: the CPU cull decision is made once per instance in the control's Update; this is where it reaches the replay.
            bool instanceCulled = model.IsInstanceAnimationCulled(i);
            if (model.ShouldResetInstanceHistory(i))
                _historyResetSlots.Add(writeIndex);

            int clip = 0;
            float playbackTime = 0f;
            if (hasAnimation)
            {
                clip = instance.AnimationClip;
                if (clip < 0 || clip >= _runtimeModel.Asset._animations.Count)
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

            // P1: on the GPU route nothing is replayed here at all - not the node tree, not the palette. The clock above already holds
            // this instance's (clip, time), and the kernel turns that into the pose at FrameStart.
            if (samplePose && !_gpuSkin)
            {
                RestoreNodesToRestPose();

                if (hasAnimation)
                    _runtimeModel.Asset._animationPlayer.Evaluate(clip, sampleTime, _workNodes);
                else
                    _runtimeModel.Asset._animationPlayer.UpdateAllNodeTransforms(_workNodes);
            }

            if (hasSkinning && !_gpuSkin)
            {
                if (samplePose)
                {
                    _runtimeModel.Asset._animationPlayer.UpdateBoneMatrices(_skins);
                    var palette = _runtimeModel.Asset._animationPlayer.GetBoneMatricesArray();
                    Array.Copy(palette, _poseCache.Bones[i], Math.Min(palette.Length, _poseCache.Bones[i].Length));
                }

                // P1: a throttled frame re-uploads the palette it sampled last, which is the pose its instance is drawn with.
                CopyInstanceBoneMatrices(writeIndex, _poseCache.Bones[i]);
            }

            // P1: the entire per-frame cost of the GPU route on the CPU side - one 16-byte row per instance, which the FrameStart kernel
            // expands into that instance's whole palette. The time is normalized by the clock first so the kernel only has to lerp.
            //
            // P3 L2: the time is the deduped one - a tick boundary for a participant, the exact clock for everyone else - because a
            // pool block is shared by instances that have to evaluate to bitwise the same pose. A shared instance writes only its tag
            // row; its representative's row is upgraded with the block after the loop, at which point the group is known to exist.
            if (_gpuSkin && _runtimeModel.Asset.SkinBake is { } gpuBake)
            {
                float gpuTime = gpuBake.NormalizeTime(clip, sampleTime, _runtimeModel.Asset._animationPlayer.Loop);
                uint poolRow = 0;
                bool evaluate = true;
                if (poseShared && pooling)
                {
                    if (!_posePoolBucketBySource.TryGetValue(poseSource, out int bucket))
                    {
                        // One block per *shared* group, so a frame in which nothing shares reserves nothing. The count is bounded by
                        // the instance count, which is what PreparePalette sized the pool region by, so the address below always
                        // lands inside the reservation.
                        if (_posePoolRewrites.Count < EnabledInstanceCount)
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
                        poolRow = (uint)((EnabledInstanceCount + bucket) * MaxBonesPerInstance);
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
                if (!_primitiveStreams.TryGetValue(primitive, out var stream))
                    continue;

                // P1: on a sampled frame the pose comes from the freshly evaluated tree and is parked in the cache; on a
                // throttled frame it comes back out of the cache. Either way the instance transform is applied this frame,
                // so movement is never throttled.
                var nodeWorld = samplePose
                    ? (primitive.OwnerNode?.WorldTransform ?? Matrix4x4.Identity)
                    : _poseCache.NodeWorlds[i][p];
                if (samplePose)
                {
                    _poseCache.NodeWorlds[i][p] = nodeWorld;
                    _poseCache.MorphWeights[i][p] = ExtractMorphWeights(primitive.OwnerNode);
                }

                stream.Worlds[writeIndex] = nodeWorld * instanceWorld;
                stream.MorphWeights[writeIndex] = _poseCache.MorphWeights[i][p];
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
                if (model.InstanceHistoryValid && _shellStream != null && writeIndex < _shellStream.Worlds.Length
                    && _shellStream.InstanceBytes.Length >= (writeIndex + 1) * 20 * sizeof(float))
                {
                    prevWorld = DecodeWorldFromInstanceBytes(_shellStream.InstanceBytes, writeIndex);
                }
                Matrix4x4 shellWorld = _shellStream != null && writeIndex < _shellStream.Worlds.Length
                    ? _shellStream.Worlds[writeIndex] : Matrix4x4.Identity;
                // P2: the shell's own previous world comes from the same swap side the patch below rewrites, but this value was
                // decoded before it; a reset slot reports the current world so the shell's velocity is zero too.
                if (model.ShouldResetInstanceHistory(i))
                    prevWorld = shellWorld;
                ShellDrawList.Add(new ShellDrawEntry(
                    writeIndex,
                    shellWorld,
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

        // P3 L2: upgrade each representative's parameter row now that this frame's groups are known. This is the only parameter write
        // that happens after the loop, and it is the reason the plan is recorded instead of decided inline: an instance cannot know it
        // is a representative until a second instance asks for the same (clip, tick) pair, by which time its own row is already
        // written. The write index it recorded is the same slot the vertex stage will read, so the block is found and the pose is not
        // evaluated twice.
        for (int b = 0; b < _posePoolRewrites.Count; b++)
        {
            var representative = _posePoolRewrites[b];
            uint poolRow = (uint)((EnabledInstanceCount + b) * MaxBonesPerInstance);
            _skinning.SetInstance(representative.Write, representative.Clip, representative.Time, true, poolRow);
        }

        // P3 L2: publish the group count. A frame that pools nothing renders the same image as the pre-pool path on purpose, so this
        // number is the only evidence that the pool code ran at all - "identical and pooled" and "never pooled" look the same.
        _skinning.PooledGroups = _posePoolRewrites.Count;

        // P1: publish the pose generation the shadow-atlas reuse digest keys on. It advances only on frames where at least
        // one instance's pose was actually replayed -- exactly when the rendered pose set can differ -- so throttled and held
        // frames keep the cached atlas and any real motion invalidates it. A static host never advances it. The GPU route replays
        // every submitted instance every frame, so its poses always change and the generation always moves.
        if (hasAnimation && (anySampled || _gpuSkin))
            model.AnimationPoseGeneration++;

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
        {
            FillInstanceBytes(stream);
            // P2: instances that just came back into view get one frame of zero velocity; the swap above is what makes the prev
            // half hold the previous frame's bytes, so the patch has to follow it.
            WriteHistoryResets(stream);
        }

        // A7: resources and the parameter table only; every upload waits for PrepareCullInputs (writeBuffer is not allowed inside a Pass).
        UpdateCullUpload(model);

        if (hasSkinning && _gpuSkin)
        {
            // P1: the palette is the kernel's output in the ring's current slot, so there is nothing to upload and no CPU block to fall
            // back to this frame. The binding is only re-sent when the ids moved. A key that cannot be pointed at the ring drops the route
            // for this frame (which also makes the kernel skip it) rather than leaving JS half-bound to a palette nobody writes.
            if (!BindBoneStorageToJs())
            {
                UnbindBoneStorageFromJs();
                _skinning.Begin(null, null, 0, 0);
                _gpuSkin = false;
            }

            BoneMatricesBytes = Array.Empty<byte>();
        }
        else if (hasSkinning)
        {
            // Reinterpret as bytes and write into the persistent buffer to avoid a per-frame ToByteArray allocation.
            int boneByteLength = _instanceBoneMatrices.Length * Unsafe.SizeOf<Matrix4x4>();
            if (BoneMatricesBytes.Length != boneByteLength)
                BoneMatricesBytes = new byte[boneByteLength];
            MemoryMarshal.AsBytes(_instanceBoneMatrices.AsSpan()).CopyTo(BoneMatricesBytes);
            UnbindBoneStorageFromJs();
        }
        else
        {
            BoneMatricesBytes = Array.Empty<byte>();
            UnbindBoneStorageFromJs();
        }

        TransformInitialized = true;
        InstanceHistoryValid = model.InstanceHistoryValid;
        PrevBonesReady = wasInitialized && hasSkinning && InstanceHistoryValid;
        ModelAlpha = model.Alpha;
    }

    // ============================================================
    // A7 per-instance GPU culling
    // ============================================================

    int Season.Rendering.IInstanceCullTarget.CullInstanceCount => EnabledInstanceCount;

    int Season.Rendering.IInstanceCullTarget.CullCapacity => _cullCapacity;

    /// <summary>Falls back to 0 while the parameter table is not yet aligned with the draw side, so the shared layer early-returns and the
    /// model keeps its direct draws, rather than drawing with a misaligned index range.</summary>
    int Season.Rendering.IInstanceCullTarget.CullPrimitiveCount => _cullPrimitiveCount;

    Season.Rendering.StorageBuffer? Season.Rendering.IInstanceCullTarget.CullSourceWorlds => _cullSource;

    /// <summary>One gather stream per parameter-table entry: entry n is the n-th primitive of the uploaded mask, and its reordered stream
    /// is what that primitive binds when it draws from args record n. Read by the shared layer only after PrepareCullInputs.</summary>
    int Season.Rendering.IInstanceCullTarget.CullStreamCount => _cullPrimitiveCount > 0 ? _cullPrimitiveCount : 0;

    Season.Rendering.StorageBuffer? Season.Rendering.IInstanceCullTarget.CullStreamSource(int stream)
        => (uint)stream < (uint)_cullStreamSources.Length ? _cullStreamSources[stream] : null;

    Season.Rendering.StorageBuffer? Season.Rendering.IInstanceCullTarget.CullStreamSorted(int stream)
        => (uint)stream < (uint)_cullStreamSorted.Length ? _cullStreamSorted[stream] : null;

    Season.Rendering.StorageBuffer? Season.Rendering.IInstanceCullTarget.CullArgs => _cullArgs;

    Season.Rendering.StorageBuffer? Season.Rendering.IInstanceCullTarget.CullVisible => _cullVisible;

    Vector3 Season.Rendering.IInstanceCullTarget.CullLocalCenter => _cullLocalCenter;

    Vector3 Season.Rendering.IInstanceCullTarget.CullLocalExtents => _cullLocalExtents;

    bool Season.Rendering.IInstanceCullTarget.CullMainView => _cullMainView;

    /// <summary>Whether this frame's indirect records exist at all: false when any of capacity, handles, or the parameter table is not ready.
    /// Which primitives may use them is a separate, per-primitive question (see <see cref="TryGetCullStream"/>).</summary>
    internal bool TryGetCullDrawHandles(out string argsId, out int capacity)
    {
        argsId = string.Empty;
        capacity = 0;
        if (!_cullDrawReady || _cullCapacity <= 0 || _cullCapacity < EnabledInstanceCount)
            return false;
        if (_cullPrimitiveCount <= 0 || _cullArgs is not WGPUStorageBuffer args)
            return false;
        argsId = args.Id;
        capacity = _cullCapacity;
        return true;
    }

    /// <summary>
    /// A7 main view: whether the opaque body may draw from the main-view segment right now, and where that segment sits in every
    /// reordered stream. Same reasons to fall back as WGPUInstancedMesh3D (owner did not ask, the chain skipped the main-view slot, or a
    /// view other than the culled one is being drawn). Unlike the mesh there is no "current mask must equal uploaded mask" check: the
    /// caller resolves every primitive through <see cref="TryGetCullStream"/>, which reads the uploaded mask itself, so a primitive that
    /// became drawable after the upload simply has no record and stays direct for one frame instead of shifting everyone's record.
    /// </summary>
    internal bool TryGetMainViewDrawHandles(out string argsId, out long vertexOffset, out int slotBytes)
    {
        vertexOffset = 0;
        slotBytes = 0;
        if (!_cullMainView || !Season.Rendering.InstanceCull.MainViewCulled
            || !TryGetCullDrawHandles(out argsId, out int capacity))
        {
            argsId = string.Empty;
            return false;
        }
        if (!ReferenceEquals(Season.Rendering.RenderView.CurrentOrNull, DeviceServices.BaseApp?.MainView))
            return false;

        vertexOffset = (long)Season.Rendering.InstanceCull.MainViewSlot * capacity * Season.Rendering.InstanceCull.WorldStride;
        slotBytes = capacity * (int)Season.Rendering.InstanceCull.WorldStride;
        return true;
    }

    /// <summary>
    /// Resolves a primitive to its parameter-table entry and reordered stream, from the uploaded mask: the entry is the number of masked
    /// primitives before it, which is exactly the order <see cref="EnsureCullPrimitiveInfo"/> wrote the table and gather wrote the streams in.
    /// False means the primitive has no record this frame and must take its direct draw.
    /// </summary>
    internal bool TryGetCullStream(WGPUPrimitiveData primitive, out string sortedId, out int entry)
    {
        sortedId = string.Empty;
        entry = -1;
        // A valid mask uses at most bits 0..30, so -1 (abandoned) and 0 (empty) both mean "no records".
        if (_cullPrimMask <= 0 || _cullPrimitiveCount <= 0)
            return false;
        if (!_primitiveIndex.TryGetValue(primitive, out int index) || (_cullPrimMask & (1 << index)) == 0)
            return false;

        int n = System.Numerics.BitOperations.PopCount((uint)(_cullPrimMask & ((1 << index) - 1)));
        if (n >= _cullPrimitiveCount || _cullStreamSorted[n] is not WGPUStorageBuffer sorted)
            return false;
        sortedId = sorted.Id;
        entry = n;
        return true;
    }

    /// <summary>
    /// The predicate of a parameter-table entry, identical to the one <c>DrawInstancedModelShadow</c> uses to pick primitives (has geometry,
    /// skin palette available when skinned, opaque, uploaded) plus having an instance stream to gather. The main view walks a superset of
    /// these and narrows per primitive through <see cref="TryGetCullStream"/>.
    /// </summary>
    bool IsCullPrimitive(WGPUPrimitiveData prim)
        => prim.VertexData != null && prim.IndexData != null && prim.VertexData.Length > 0
            && !(prim.HasSkinning && string.IsNullOrEmpty(SkinKey))
            && !prim.IsTransparent && prim.Uploaded && prim.IndexBytes != null
            && _primitiveStreams.ContainsKey(prim);

    /// <summary>Called once at the end of each frame's Update: only prepares resources. Every upload is deferred to the Record phase,
    /// see <see cref="Season.Rendering.IInstanceCullTarget.PrepareCullInputs"/>.</summary>
    void UpdateCullUpload(Season.Controls.InstancedModel model)
    {
        _cullDrawReady = false;
        _cullMainView = model.EffectiveDrawPath == Season.Controls.InstanceDrawPath.Indirect;
        if (EnabledInstanceCount <= 0)
            return;

        EnsureCullResources(EnabledInstanceCount);
        if (_cullSource == null)
            return;

        // The kernel culls the whole instance once with this box, and every primitive stream only replays that decision, so it must be in
        // the root instance's model space and cover every clip pose; TemplateCullBounds is that box (the rest box when nothing animates).
        var bounds = model.TemplateCullBounds;
        _cullLocalCenter = bounds.Center;
        _cullLocalExtents = bounds.Extents;

        if (!_cullRegistered)
        {
            Season.Rendering.InstanceCull.Register(this);
            _cullRegistered = true;
        }
    }

    /// <summary>
    /// Prepares the shared cull buffers for the instance count, then the parameter table and its per-entry streams. Capacity is aligned to 64
    /// as on the mesh; a capacity change rebuilds every stream too, since gather addresses each reordered stream by the same capacity.
    /// </summary>
    void EnsureCullResources(int count)
    {
        if (!Season.Rendering.InstanceCull.Enabled)
        {
            ReleaseCullResources();
            return;
        }
        if (count <= 0)
            return;

        int capacity = (count + 63) / 64 * 64;
        if (_cullCapacity != capacity || _cullSource == null)
        {
            ReleaseCullResources();
            try
            {
                var g = Season.Basic.Graphics.Instance;
                _cullSource = g.CreateStorageBuffer((uint)capacity * Season.Rendering.InstanceCull.WorldStride);
                _cullVisible = g.CreateStorageBuffer((uint)(Season.Rendering.InstanceCull.MaxSlots * capacity) * 4u);
                // A7b: fixed size, table plus the per-slot count accumulator the gather kernels clear; the parameter-table buffer that
                // used to sit next to it is gone, because what the kernels need from it now travels as the arg template built below.
                _cullArgs = g.CreateStorageBuffer(Season.Rendering.InstanceCull.ArgsTotalBytes,
                    Season.Rendering.StorageBufferUsage.ComputeAndIndirectArgs);
            }
            catch (Exception ex)
            {
                DeviceServices.BaseApp?.AddLog(LogType.Error, $"[InstanceCull] Buffer creation failed: {ex.Message}");
            }

            if (_cullSource == null || _cullVisible == null || _cullArgs == null)
            {
                ReleaseCullResources();
                return;
            }

            _cullCapacity = capacity;
            _cullPrimitiveCount = -1;
            _cullPrimMask = -1;
        }

        EnsureCullPrimitiveInfo();
    }

    /// <summary>
    /// Builds the parameter-table bytes and makes sure every entry has its stream pair, without touching the GPU contents. One entry per
    /// primitive passing <see cref="IsCullPrimitive"/>, in <see cref="_primitives"/> order. indexCount is counted the way JS does for the
    /// primitive's own index buffer (IndexBytes reinterpreted by format); startIndex and baseVertex are 0 because every primitive owns its
    /// vertex and index buffers, so its range always starts at the beginning of them.
    /// </summary>
    void EnsureCullPrimitiveInfo()
    {
        if (_cullArgs == null)
            return;

        if (_primitives.Count > 31)
        {
            // One bit per primitive is the only way the mask fits; past that the model explicitly leaves GPU culling rather than half-joining it.
            AbandonCullPrimitiveInfo();
            return;
        }

        int mask = 0;
        int n = 0;
        for (int i = 0; i < _primitives.Count; i++)
        {
            var prim = _primitives[i];
            if (!IsCullPrimitive(prim))
                continue;
            if (n >= Season.Rendering.InstanceCull.MaxPrimitives)
            {
                AbandonCullPrimitiveInfo();
                return;
            }

            _cullPrimIndexCounts[n] = prim.IndexBytes.Length / (prim.Use32BitIndices ? 4 : 2);
            mask |= 1 << i;
            n++;
        }

        if (mask == _cullPrimPendingMask && _cullPrimPendingCount == n)
            return;

        if (n <= 0 || !EnsureCullStreams(n))
        {
            AbandonCullPrimitiveInfo();
            return;
        }

        int stride = (int)Season.Rendering.InstanceCull.PrimitiveInfoStride;
        var bytes = new byte[n * stride];
        for (int i = 0; i < n; i++)
            Season.Rendering.InstanceCull.WritePrimitiveInfo(bytes, i * stride, (uint)_cullPrimIndexCounts[i], 0u, 0);

        _cullPrimPendingBytes = bytes;
        _cullPrimPendingCount = n;
        _cullPrimPendingMask = mask;
        // A7b: the arg image is composed here too -- it is a pure function of this table plus the fixed slot layout, i.e. exactly the bytes
        // the reset kernel used to write every frame, now built once when its input changes.
        _cullArgsTemplateBytes = Season.Rendering.InstanceCull.BuildArgsTemplate(bytes, n);
    }

    /// <summary>
    /// Makes sure entries 0..count-1 each have a gather source (this primitive's instance stream, compute-only) and a reordered stream
    /// (also bound as a vertex buffer, a usage WebGPU bakes in at creation). Streams past count are kept: a primitive that drops out of the
    /// table and comes back would otherwise rebuild them, and the shared layer only walks CullStreamCount of them.
    /// </summary>
    bool EnsureCullStreams(int count)
    {
        var g = Season.Basic.Graphics.Instance;
        for (int s = 0; s < count; s++)
        {
            if (_cullStreamSources[s] != null && _cullStreamSorted[s] != null)
                continue;
            try
            {
                _cullStreamSources[s] ??= g.CreateStorageBuffer((uint)_cullCapacity * Season.Rendering.InstanceCull.WorldStride);
                _cullStreamSorted[s] ??= g.CreateStorageBuffer(Season.Rendering.InstanceCull.SortedBytes(_cullCapacity),
                    Season.Rendering.StorageBufferUsage.ComputeAndVertexInput);
            }
            catch (Exception ex)
            {
                DeviceServices.BaseApp?.AddLog(LogType.Error, $"[InstanceCull] Stream buffer creation failed: {ex.Message}");
            }
            if (_cullStreamSources[s] == null || _cullStreamSorted[s] == null)
                return false;
        }
        return true;
    }

    /// <summary>The parameter table is unusable: clear the pending value together with the uploaded value, so the draw side stops resolving
    /// primitives against a stale table (see WGPUInstancedMesh3D.AbandonCullPrimitiveInfo).</summary>
    void AbandonCullPrimitiveInfo()
    {
        _cullPrimPendingBytes = null;
        _cullArgsTemplateBytes = null;
        _cullPrimPendingCount = 0;
        _cullPrimPendingMask = -1;
        _cullPrimitiveCount = 0;
        _cullPrimMask = -1;
    }

    /// <summary>
    /// A7: uploads the cull CPU-side inputs, called back by InstanceCullEffect outside all Passes: the parameter table when it changed, the
    /// root worlds, then each entry's primitive stream from the mask just made current, so the streams gather reads always match the table
    /// it reads them with. Uploads cover this frame's instances only, with no slack.
    /// </summary>
    bool Season.Rendering.IInstanceCullTarget.PrepareCullInputs()
    {
        int count = EnabledInstanceCount;
        if (!Season.Rendering.InstanceCull.Enabled || count <= 0)
            return false;

        var g = Season.Basic.Graphics.Instance;

        if (_cullPrimPendingCount != _cullPrimitiveCount || _cullPrimPendingMask != _cullPrimMask)
        {
            // A7b: the upload is the whole arg block, not just the index ranges -- its layout is fixed, so a single write replaces the
            // per-frame reset dispatch that used to rewrite the frame-invariant half of it.
            if (_cullPrimPendingCount <= 0 || _cullArgsTemplateBytes == null || _cullArgs == null)
                return false;
            if (!g.TryUpdateStorageBuffer(_cullArgs, _cullArgsTemplateBytes))
                return false;
            _cullPrimitiveCount = _cullPrimPendingCount;
            _cullPrimMask = _cullPrimPendingMask;
        }

        if (_cullSource == null || _cullPrimitiveCount <= 0 || _cullPrimMask <= 0)
            return false;
        int sourceBytes = count * (int)Season.Rendering.InstanceCull.WorldStride;
        if (sourceBytes > _cullSourceBytes.Length)
            return false;
        if (!g.TryUpdateStorageBuffer(_cullSource, new ReadOnlySpan<byte>(_cullSourceBytes, 0, sourceBytes)))
            return false;

        // Same 80B record as the root worlds, so the per-primitive IA bytes go to gather as they are.
        int entry = 0;
        for (int i = 0; i < _primitives.Count && entry < _cullPrimitiveCount; i++)
        {
            if ((_cullPrimMask & (1 << i)) == 0)
                continue;
            var streamSource = _cullStreamSources[entry];
            if (streamSource == null || !_primitiveStreams.TryGetValue(_primitives[i], out var stream)
                || stream.InstanceBytes.Length < sourceBytes)
                return false;
            if (!g.TryUpdateStorageBuffer(streamSource, new ReadOnlySpan<byte>(stream.InstanceBytes, 0, sourceBytes)))
                return false;
            entry++;
        }
        return entry == _cullPrimitiveCount;
    }

    /// <summary>
    /// A7: called back by InstanceCullEffect after the dispatches. As on the mesh, WebGPU's command and submission order is the
    /// synchronization point, so this is only an admission check over the args and every reordered stream the table refers to.
    /// </summary>
    public bool PrepareCullDraw()
    {
        _cullDrawReady = false;
        if (_cullArgs is not WGPUStorageBuffer args) return false;
        if (_cullCapacity <= 0 || _cullPrimitiveCount <= 0) return false;
        for (int s = 0; s < _cullPrimitiveCount; s++)
        {
            if (_cullStreamSorted[s] is not WGPUStorageBuffer) return false;
        }

        if (!Season.Basic.Graphics.Instance.TryPrepareIndirectArgs(args)) return false;

        _cullDrawReady = true;
        return true;
    }

    void ReleaseCullResources()
    {
        if (_cullRegistered)
        {
            Season.Rendering.InstanceCull.Unregister(this);
            _cullRegistered = false;
        }
        _cullSource?.Dispose(); _cullSource = null;
        _cullArgs?.Dispose(); _cullArgs = null;
        _cullVisible?.Dispose(); _cullVisible = null;
        for (int s = 0; s < _cullStreamSources.Length; s++)
        {
            _cullStreamSources[s]?.Dispose(); _cullStreamSources[s] = null;
            _cullStreamSorted[s]?.Dispose(); _cullStreamSorted[s] = null;
        }
        _cullCapacity = -1;
        _cullPrimitiveCount = -1;
        _cullPrimMask = -1;
        _cullPrimPendingBytes = null;
        _cullArgsTemplateBytes = null;
        _cullPrimPendingCount = -1;
        _cullPrimPendingMask = -1;
        _cullLocalCenter = Vector3.Zero;
        _cullLocalExtents = Vector3.Zero;
        _cullDrawReady = false;
    }

    /// <summary>On unload, remove the cull target from the shared layer, otherwise the list would keep a reference to dead JS handles.</summary>
    public void Dispose()
    {
        // The skinning target goes first: it has to leave the scheduler before the buffers it points at are released, and JS has to stop
        // reading the ring before the backend retires it.
        UnbindBoneStorageFromJs();
        _skinning.Dispose();
        _ownedPalette?.Dispose();
        _ownedPalette = null;
        ReleaseCullResources();
    }

    /// <summary>Writes one root instance world as an 80B cull record: four matrix rows plus a zero vec4, the layout of every instance stream.</summary>
    static void WriteCullRoot(Span<float> data, int index, in Matrix4x4 world)
    {
        int o = index * 20;
        data[o] = world.M11; data[o + 1] = world.M12; data[o + 2] = world.M13; data[o + 3] = world.M14;
        data[o + 4] = world.M21; data[o + 5] = world.M22; data[o + 6] = world.M23; data[o + 7] = world.M24;
        data[o + 8] = world.M31; data[o + 9] = world.M32; data[o + 10] = world.M33; data[o + 11] = world.M34;
        data[o + 12] = world.M41; data[o + 13] = world.M42; data[o + 14] = world.M43; data[o + 15] = world.M44;
        data[o + 16] = 0f; data[o + 17] = 0f; data[o + 18] = 0f; data[o + 19] = 0f;
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
        prevInstanceBytes = _shellStream.PrevReady && InstanceHistoryValid ? _shellStream.PrevInstanceBytes : Array.Empty<byte>();
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

    /// <summary>
    /// P1: points the ring at this frame's slots and grows it to the shape the draw needs. Web has no backend frame ring, so this model is
    /// the clock: the slot chosen here is the one the kernel writes at FrameStart and the one the draw binds later in the same frame, which
    /// is what keeps the producer and the consumer on the same buffer.
    ///
    /// The stride is not negotiated here: it is <see cref="MaxBonesPerInstance"/> because the vertex stage hardcodes
    /// <c>instance * 100</c>, and the shared latch has already rejected any bake that does not fit in a block that wide.
    /// </summary>
    bool ShapeSkinningRing(int instanceCount)
    {
        var palette = _skinning.Palette;
        if (palette == null) return false;

        // Two slots minimum: the previous palette is read from a different buffer, never from this frame's own slot.
        int slots = Math.Max(2, palette.Slots.Length);
        int slot = (int)(_boneStorageFrame % (uint)slots);
        int previousSlot = (slot + slots - 1) % slots;

        if (!_skinning.PreparePalette(Season.Basic.Graphics.Instance, slots, slot, previousSlot, instanceCount))
            return false;

        // Advanced only after the ring holds the shape, so a frame that fell back to the CPU does not skip a slot the GPU may still read.
        _boneStorageFrame++;
        return _skinning.BeginFrame(instanceCount);
    }

    /// <summary>Points the skin key at the ring's current and previous slots, skipping the interop round trip when the ids did not move.</summary>
    bool BindBoneStorageToJs()
    {
        if (_skinning.Palette is not { Current: WGPUStorageBuffer current } ring)
            return false;

        var previous = ring.Previous as WGPUStorageBuffer ?? current;
        if (string.Equals(current.Id, _boneStorageBoundCurrent, StringComparison.Ordinal)
            && string.Equals(previous.Id, _boneStorageBoundPrevious, StringComparison.Ordinal))
            return true;

        if (string.IsNullOrEmpty(SkinKey) || !WebGPUInterop.BindSkinnedBoneStorage(SkinKey, current.Id, previous.Id))
            return false;

        _boneStorageBoundCurrent = current.Id;
        _boneStorageBoundPrevious = previous.Id;
        return true;
    }

    /// <summary>Drops the binding so JS accepts the CPU upload again on its next frame. Frames that never bound anything skip the round trip.</summary>
    void UnbindBoneStorageFromJs()
    {
        if (_boneStorageBoundCurrent == null && _boneStorageBoundPrevious == null)
            return;

        _boneStorageBoundCurrent = null;
        _boneStorageBoundPrevious = null;
        if (!string.IsNullOrEmpty(SkinKey))
            WebGPUInterop.UnbindSkinnedBoneStorage(SkinKey);
    }

    void CopyInstanceBoneMatrices(int instanceIndex, Matrix4x4[] sourceBones)
    {
        int baseIndex = instanceIndex * MaxBonesPerInstance;
        int count = Math.Min(sourceBones.Length, MaxBonesPerInstance);
        for (int i = 0; i < count; i++)
            _instanceBoneMatrices[baseIndex + i] = sourceBones[i];

        // P2 L1 / P3 L2: this tail fill is also the tag-row clear the other three backends spell out explicitly. The block is
        // MaxBonesPerInstance rows wide and the bake is guaranteed to be at most one row narrower (the latch rejects the rest), so
        // writing Identity from count to the end always lands on the tag row and always writes the (0, 0, 0, 1) last row that makes
        // bytes 48 and 52 decode as an unpooled zero. A palette left over from an earlier pooled frame therefore cannot point the
        // vertex stage at a pool block this route never filled.
        for (int i = count; i < MaxBonesPerInstance; i++)
            _instanceBoneMatrices[baseIndex + i] = Matrix4x4.Identity;
    }

    static Vector4 ExtractMorphWeights(GltfNodeBase? node) => MorphDelta.ExtractWeights(node);

    /// <summary>
    /// P2: republishes this frame's 80-byte record for every slot that just came back into view into the previous-frame byte side,
    /// so the motion vector computed against it is zero for exactly this frame.
    ///
    /// Must run after <see cref="FillInstanceBytes"/>: that is what swaps the two byte sides and rewrites the current one, and the
    /// copy reads from the finished current side. The whole record is copied -- world and morph weights together -- because the
    /// shader reads both from this record and gates them on the same sentinel.
    /// </summary>
    void WriteHistoryResets(PrimitiveInstanceStream stream)
    {
        if (_historyResetSlots.Count == 0 || !stream.PrevReady)
            return;
        if (stream.PrevInstanceBytes.Length != stream.InstanceBytes.Length)
            return;

        int stride = 20 * sizeof(float);
        for (int k = 0; k < _historyResetSlots.Count; k++)
        {
            int slot = _historyResetSlots[k];
            int offset = slot * stride;
            if ((uint)(offset + stride) > (uint)stream.InstanceBytes.Length)
                continue;
            Array.Copy(stream.InstanceBytes, offset, stream.PrevInstanceBytes, offset, stride);
        }
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
