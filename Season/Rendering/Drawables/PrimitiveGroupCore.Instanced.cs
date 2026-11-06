// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Drawables;

/// <summary>
/// The instance-group half of the shared core: the two buckets, the per-instance transparent order, the previous-frame
/// publication and the merged cull box.
///
/// This is a partial rather than a second base class on purpose. An instance group is the *same* object as the
/// non-instanced group it derives from — <c>DXInstancedModel</c> and <c>DXModel</c> share one pipeline cache, one
/// highlight pool and one alpha state — so splitting the type would split state that must stay single.
///
/// What is shared here is the CPU-side bookkeeping only, and that boundary is load-bearing:
/// <list type="bullet">
///   <item>the instance stream itself stays backend code — D3D12 writes a mapped ring, Vulkan a host-visible
///   <c>BufferResource</c>, Metal an <c>IMTLBuffer</c>, and the three disagree on when a slot is free;</item>
///   <item><c>InstanceTransformData</c> and <c>InstanceExtras</c> stay backend code too: they are descriptor layouts,
///   referenced from the graphics layer, the pipeline layer and the scene collection on each backend, so hoisting them
///   would move twenty files to save sixty lines;</item>
///   <item>the per-instance highlight boxes stay backend code, because they are built from backend primitives.</item>
/// </list>
///
/// <see cref="_instanceWorlds"/> is deliberately hoisted even though it is "instance data": it is a plain
/// <see cref="Matrix4x4"/> array with no backend layout, and the transparent sort key is computed from it, so the sort
/// cannot move without it.
/// </summary>
internal abstract partial class PrimitiveGroupCore<TPrim, TMat, TTex>
    where TPrim : PrimitiveCpuData<TMat, TTex>, IDisposable
    where TMat : unmanaged, IMaterialFields
    where TTex : class
{
    /// <summary>Every primitive in the group, in authoring order. Buckets are rebuilt from it; the order here is what
    /// the trace names and what the transparent tie-break falls back to.</summary>
    protected readonly List<TPrim> _primitives = new();

    /// <summary>Primitives that write depth. Drawn in submission order under the Opaque (or Fade) pipeline.</summary>
    protected readonly List<TPrim> _opaquePrimitives = new();

    /// <summary>Primitives whose material is true BLEND. Drawn from the back-face pass, one instance at a time.</summary>
    protected readonly List<TPrim> _transparentPrimitives = new();

    /// <summary>Per-instance draw order for the transparent bucket, rebuilt per primitive per frame. Instance indices,
    /// not primitives: one primitive covers the whole batch.</summary>
    protected readonly List<int> _transparentInstanceOrder = new(64);

    /// <summary>
    /// CPU copy of the per-instance world matrices, parallel to the instance stream. Lives here rather than beside
    /// <c>_instanceData</c> because the backend struct packs to a GPU layout while this one is read by the sort.
    /// </summary>
    protected Matrix4x4[] _instanceWorlds = Array.Empty<Matrix4x4>();

    /// <summary>Instances uploaded this frame. Zero means every pass bails out before touching pipeline state.</summary>
    protected int _instanceCount;

    /// <summary>Whether any enabled instance turned Wireframe on this frame. Written by the derived Update and used as
    /// the zero-cost gate for the shell pass.</summary>
    protected bool _wireframeActive;

    /// <summary>Compacted slot list of the instances that enabled Outline2D this frame, rebuilt every Update.</summary>
    protected readonly List<int> _outline2DInstances = new();

    /// <summary>Per-instance outline colours, parallel to <see cref="_outline2DInstances"/>.</summary>
    protected readonly List<Vector4> _outline2DInstanceColors = new();

    /// <summary>Outline colour of the first instance that enabled Outline2D this frame. The composited frame colour
    /// uses one colour for the whole mask, so it takes the first — matching how ObjectPicker reports the batch.</summary>
    protected Vector4 _outline2DInstanceColor;

    /// <summary>Outline width of the first instance that enabled Outline2D this frame, in pixels.</summary>
    protected float _outline2DInstanceWidth;

    /// <summary>Host-level (non-instanced) Outline2D activation. When true the mask pass draws the whole batch in one
    /// submit and the per-instance list is ignored.</summary>
    protected bool _outline2DHostActive;

    /// <summary>
    /// Group-local merged bounding box. The A7 culling kernel converts instance matrices into world bounding spheres
    /// from a single box, so a multi-surface group must publish the union: one surface's box would cull every other
    /// surface's instances wholesale.
    /// </summary>
    protected Vector3 _cullLocalCenter;

    /// <inheritdoc cref="_cullLocalCenter"/>
    protected Vector3 _cullLocalExtents;

    // ============================================================
    // Hooks
    // ============================================================

    /// <summary>Whether this group owns a previous-frame bone palette. Model-only: a generated-geometry group carries
    /// no skeletons, so its bit stays off and the shader never looks for a palette.</summary>
    protected virtual bool HasPrevBoneHistory => false;

    /// <summary>Whether this group owns previous-frame per-instance morph weights. Model-only for the same reason as
    /// <see cref="HasPrevBoneHistory"/>.</summary>
    protected virtual bool HasPrevMorphHistory => false;

    /// <summary>
    /// Whether the bone bit applies to one primitive. Metal gates it per primitive, because only a skinned primitive
    /// has a palette to read at all; DX and Vulkan publish it for the whole group and let the shader decide.
    /// </summary>
    protected virtual bool PrevBonesApplies(TPrim primitive) => true;

    /// <summary>
    /// Whether a queued-replay recorder is active, in which case the transparent instance order must not be sorted.
    /// A recorder has already fixed the order and will replay it without coming back through here, so sorting would
    /// make the replayed stream disagree with the recorded one. Only DirectX queues its groups today; Vulkan and Metal
    /// submit immediately and return false.
    /// </summary>
    protected virtual bool IsQueuedReplayActive => false;

    // ============================================================
    // Buckets and the merged cull box
    // ============================================================

    /// <summary>Rebuilds the Opaque/Transparent buckets from <see cref="PrimitiveCpuData{TMat,TTex}.IsTransparent"/>
    /// and refreshes the merged cull box. Called from the derived Update after the primitive set changes.</summary>
    protected void RebuildPrimitiveBuckets()
    {
        _opaquePrimitives.Clear();
        _transparentPrimitives.Clear();

        foreach (var primitive in _primitives)
        {
            if (primitive.IsTransparent)
                _transparentPrimitives.Add(primitive);
            else
                _opaquePrimitives.Add(primitive);
        }

        UpdateCullLocalBounds();
    }

    /// <summary>
    /// Overrides the cull box with a caller-computed one. A glTF model must: its primitives' local boxes live in their
    /// own node spaces and ignore animation, while the kernel transforms the box by the root instance world, so the box
    /// has to be in model space and cover every pose the clips can reach.
    /// </summary>
    protected void SetCullLocalBounds(Bounds3D bounds)
    {
        _cullLocalCenter = bounds.Center;
        _cullLocalExtents = bounds.Extents;
    }

    /// <summary>Merges every primitive's local box into the group box the culling kernel reads.</summary>
    private void UpdateCullLocalBounds()
    {
        if (_primitives.Count == 0)
        {
            _cullLocalCenter = Vector3.Zero;
            _cullLocalExtents = Vector3.Zero;
            return;
        }

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var primitive in _primitives)
        {
            min = Vector3.Min(min, primitive.LocalBoundsCenter - primitive.LocalBoundsExtents);
            max = Vector3.Max(max, primitive.LocalBoundsCenter + primitive.LocalBoundsExtents);
        }

        _cullLocalCenter = (min + max) * 0.5f;
        _cullLocalExtents = (max - min) * 0.5f;
    }

    // ============================================================
    // Transparent instance order
    // ============================================================

    /// <summary>
    /// Fills <see cref="_transparentInstanceOrder"/> with every instance index, sorted back to front by the depth of
    /// the primitive's local-bounds centre under each instance's world matrix.
    ///
    /// Sorting is skipped while a queued-replay recorder is active (see <see cref="IsQueuedReplayActive"/>); the
    /// recorded order is the final one and the replay never re-enters this method.
    /// </summary>
    protected void BuildTransparentInstanceOrder(TPrim primitive)
        => BuildTransparentInstanceOrder(primitive, _instanceWorlds);

    /// <summary>
    /// The same order, sorted from a caller-supplied world array. A glTF model keeps one stream per primitive and
    /// each stream carries its own worlds, so <see cref="_instanceWorlds"/> is never filled on that path.
    /// </summary>
    protected void BuildTransparentInstanceOrder(TPrim primitive, Matrix4x4[] worlds)
    {
        _transparentInstanceOrder.Clear();
        for (int i = 0; i < _instanceCount; i++)
            _transparentInstanceOrder.Add(i);

        if (IsQueuedReplayActive)
            return;

        _transparentInstanceOrder.Sort((a, b) =>
        {
            float depthA = ComputeTransparentDepth(worlds[a], primitive.LocalBoundsCenter);
            float depthB = ComputeTransparentDepth(worlds[b], primitive.LocalBoundsCenter);
            return depthB.CompareTo(depthA);
        });
    }

    /// <summary>Sort depth of one instance's copy of a local point; larger is farther and therefore drawn earlier.</summary>
    protected static float ComputeTransparentDepth(Matrix4x4 world, Vector3 localCenter)
        => RenderView.SortDepth(Vector3.Transform(localCenter, world));

    // ============================================================
    // Previous-frame publication
    // ============================================================

    /// <summary>
    /// 2-3 Step C: publishes whether previous-frame data may be read this frame, on the main primitives and the
    /// highlight shells alike. It runs every frame rather than once, because "the previous frame" is only the previous
    /// frame while the compacted instance order holds: switching one instance off shifts every later instance's slot,
    /// and reading history then pairs each instance with its predecessor — a one-frame velocity spike on everything past
    /// the change. Passing false makes the shaders fall back to current data. Shell primitives are kept in sync,
    /// otherwise the shell keeps a trail the body no longer has.
    /// </summary>
    protected void UpdatePrevStateFlags(bool historyValid)
    {
        uint prevWorld = historyValid ? 1u : 0u;
        uint prevMorph = historyValid && HasPrevMorphHistory ? 1u : 0u;
        // Bones stay gated per primitive: only a skinned primitive has a palette to read at all. The group-level flag
        // is still what the shells publish, which is why it is kept apart from the per-primitive value below.
        bool hasPrevBones = historyValid && HasPrevBoneHistory;

        for (int i = 0; i < _primitives.Count; i++)
        {
            var primitive = _primitives[i];
            uint prevBones = hasPrevBones && PrevBonesApplies(primitive) ? 1u : 0u;
            if (primitive.MaterialParams.HasPrevInstanceWorld == prevWorld
                && primitive.MaterialParams.HasPrevBones == prevBones
                && primitive.MaterialParams.HasPrevMorph == prevMorph)
                continue;

            primitive.MaterialParams.HasPrevInstanceWorld = prevWorld;
            primitive.MaterialParams.HasPrevBones = prevBones;
            primitive.MaterialParams.HasPrevMorph = prevMorph;
            for (int f = 0; f < FrameCount; f++)
                MaterialSlot(primitive, f) = primitive.MaterialParams;
        }

        // Both template sets and both instance-box pools: a pooled box may have been created before the flags settled.
        SyncShellPrevFlags(prevWorld != 0, hasPrevBones, prevMorph != 0);
    }
}
