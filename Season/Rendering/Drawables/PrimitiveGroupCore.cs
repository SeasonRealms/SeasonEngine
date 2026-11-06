// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Drawables;

/// <summary>
/// The per-group work that every backend performs identically: the alpha / tint / surface-parameter synchronization,
/// the three-bucket main draw, and the shadow pass with its per-epoch primitive cache.
///
/// The split is the same one <see cref="MaterialOps"/> makes, one level up. Control flow lives here; every leaf that
/// touches a graphics API is a hook. Three things are deliberately left to the backend because they are not shared
/// behavior, only shared shape:
/// <list type="bullet">
///   <item>the PSO / encoder state mapping (<see cref="ApplySurfaceState"/>) — the mode and cull enumerations differ
///   per backend and must be mapped explicitly, never cast;</item>
///   <item>the actual submit (<see cref="DrawMainPrimitive"/>, <see cref="DrawShadowPrimitive"/>) — root parameters,
///   descriptor sets and encoders stay backend code;</item>
///   <item>the trace identity (<see cref="TraceBackend"/>) — the trace never compares across backends, so each keeps
///   its own name in it.</item>
/// </list>
///
/// Order is the contract. Type parameters are concrete at every call site, so the hooks are ordinary virtual calls on
/// a closed type: no generic virtual method, and therefore nothing to trip AOT.
/// </summary>
internal abstract partial class PrimitiveGroupCore<TPrim, TMat, TTex>
    where TPrim : PrimitiveCpuData<TMat, TTex>, IDisposable
    where TMat : unmanaged, IMaterialFields
    where TTex : class
{
    /// <summary>Trace / diagnostics name of the object this group stands for.</summary>
    internal string Name = string.Empty;

    /// <summary>Overall alpha last written into the material block. Drives the Opaque-versus-Fade choice and is also
    /// the change gate for <see cref="SyncAlpha"/>.</summary>
    protected float _currentAlpha = 1.0f;

    /// <summary>Color multiplier last written into the material block; the change gate for
    /// <see cref="SyncColorTint"/>.</summary>
    protected Vector4 _currentColorTint = Vector4.One;

    /// <summary>Whether the first Update has completed. When false, Draw is skipped so nothing renders with the
    /// identity matrix.</summary>
    protected bool _transformInitialized;

    /// <summary>Whether the screen-space outline mask is enabled; collected by Graphics in a separate pass.</summary>
    protected bool _outline2DActive;

    /// <summary>Outline2D color for the current object.</summary>
    protected Vector4 _outline2DColor;

    /// <summary>Outline2D width for the current object, in pixels.</summary>
    protected float _outline2DWidth;

    /// <summary>Reusable collection buffer. Every sync path and the main draw reuse it so a steady frame allocates
    /// nothing here. Also used by the highlight builders, which collect into the same order the draw would see.</summary>
    protected readonly List<TPrim> _drawList = new List<TPrim>(64);

    private readonly List<TPrim> _singleSidedTransparentList = new List<TPrim>(32);
    private readonly List<TPrim> _doubleSidedTransparentList = new List<TPrim>(32);

    /// <summary>Shadow-pass primitive list, kept apart from <see cref="_drawList"/> so the two passes never overwrite
    /// each other's collection.</summary>
    private readonly List<TPrim> _shadowDrawList = new List<TPrim>(64);

    /// <summary>Epoch already collected into <see cref="_shadowDrawList"/> (int.MinValue = never).</summary>
    private int _shadowDrawListEpoch = int.MinValue;

    // ============================================================
    // Hooks
    // ============================================================

    /// <summary>Backend tag written into the draw trace ("dx" / "vk" / "mtl").</summary>
    protected abstract string TraceBackend { get; }

    /// <summary>Number of in-flight frames, i.e. how many copies of each constant block exist.</summary>
    protected abstract int FrameCount { get; }

    /// <summary>Material block of one primitive for one in-flight frame. Returned by reference so the sync paths edit
    /// the mapped block in place instead of reading it back into a temporary and writing it home.</summary>
    protected abstract ref TMat MaterialSlot(TPrim primitive, int frame);

    /// <summary>Appends every primitive that should be drawn this frame. Must be side-effect free: the shadow label
    /// replays it once per pass, and the sync paths replay it on demand.</summary>
    protected abstract void CollectPrimitives(List<TPrim> result);

    /// <summary>Maps one surface state onto backend pipeline state.</summary>
    protected abstract void ApplySurfaceState(in SurfaceState state);

    /// <summary>Submits one primitive in the main pass, using whatever surface state is currently bound.</summary>
    protected abstract void DrawMainPrimitive(TPrim primitive);

    /// <summary>Binds the bindings that are invariant across a whole shadow pass.</summary>
    protected abstract void BeginShadowPass();

    /// <summary>Submits one primitive in the shadow pass. <paramref name="bindMaterial"/> is false when the group
    /// proved the material binding is identical and an earlier primitive already bound it.</summary>
    protected abstract void DrawShadowPrimitive(TPrim primitive, bool bindMaterial);

    /// <summary>Whether the material binding can be shared by the whole group.</summary>
    protected abstract bool CanShareShadowMaterial(List<TPrim> primitives);

    /// <summary>How the transparent bucket is ordered. The difference is intentional, not drift.</summary>
    protected virtual TransparentOrder TransparentBucketOrder => TransparentOrder.SortedSplit;

    /// <summary>Whether the transparent bucket may be sorted right now. Recording passes must not sort, because the
    /// recorded order is replayed later and the two must agree.</summary>
    protected virtual bool SortTransparentNow => true;

    /// <summary>Sort key for the transparent bucket; larger is farther.</summary>
    protected virtual float TransparentSortDepth(TPrim primitive) => 0f;

    /// <summary>Runs once at the top of a main-pass draw, after the transform gate and before the primitive list is
    /// collected. Vulkan uses it to pin the group onto the snapshot page and refresh the bindings the frame owns;
    /// the backends that submit directly have nothing to do here.</summary>
    protected virtual void OnDrawBegin() { }

    // ============================================================
    // Sync paths
    // ============================================================

    /// <summary>
    /// Pushes the group-level alpha into every primitive's material block for every in-flight frame, so no frame can
    /// read a stale value. The formula lives in <see cref="MaterialOps.ApplyAlpha"/>; this method only owns the
    /// primitive walk and the frame loop.
    /// </summary>
    protected void SyncAlpha(float alpha)
    {
        if (_currentAlpha == alpha)
            return;
        _currentAlpha = alpha;

        int n = FrameCount;
        _drawList.Clear();
        CollectPrimitives(_drawList);

        for (int i = 0; i < n; i++)
        {
            foreach (var primitive in _drawList)
            {
                ref TMat material = ref MaterialSlot(primitive, i);
                MaterialOps.ApplyAlpha(ref material, alpha,
                    primitive.OriginalBaseColorAlpha, primitive.OriginalAlphaCutoff);
            }
        }
    }

    /// <summary>
    /// Pushes the group-level color multiplier into every primitive's material block for every in-flight frame.
    /// The formula lives in <see cref="MaterialOps.ApplyTint"/>.
    /// </summary>
    protected void SyncColorTint(Vector4 tint)
    {
        if (_currentColorTint == tint)
            return;
        _currentColorTint = tint;

        int n = FrameCount;
        _drawList.Clear();
        CollectPrimitives(_drawList);

        for (int i = 0; i < n; i++)
        {
            foreach (var primitive in _drawList)
            {
                ref TMat material = ref MaterialSlot(primitive, i);
                MaterialOps.ApplyTint(ref material, tint, primitive.OriginalBaseColor);
            }
        }
    }

    /// <summary>Writes material-parameter overrides into the N-buffered material block of every primitive.</summary>
    internal void SyncMaterialParams(float? metallic, float? roughness, Vector4? emissive)
    {
        _drawList.Clear();
        CollectPrimitives(_drawList);
        if (_drawList.Count == 0) return;

        int n = FrameCount;
        for (int i = 0; i < n; i++)
        {
            foreach (var primitive in _drawList)
            {
                ref TMat material = ref MaterialSlot(primitive, i);
                if (metallic.HasValue) material.MetallicFactor = metallic.Value;
                if (roughness.HasValue) material.RoughnessFactor = roughness.Value;
                if (emissive.HasValue) material.EmissiveFactor = emissive.Value;
            }
        }
    }

    // ============================================================
    // Outline2D state
    // ============================================================

    protected void SetOutline2DState(bool active, Vector4 color, float width)
    {
        _outline2DActive = active;
        _outline2DColor = color;
        _outline2DWidth = width;
    }

    protected bool HasOutline2D => _outline2DActive;
    protected Vector4 Outline2DColor => _outline2DColor;
    protected float Outline2DWidth => _outline2DWidth;
    internal bool Outline2DActive => _outline2DActive;
    internal Vector4 Outline2DMaskColor => _outline2DColor;
    internal float Outline2DMaskWidth => _outline2DWidth;

    // ============================================================
    // Main pass: three buckets
    // ============================================================

    public virtual void Draw()
    {
        if (!_transformInitialized)
            return;

        OnDrawBegin();

        // Collect all primitives for this frame, reusing the member list to avoid GC.
        _drawList.Clear();
        CollectPrimitives(_drawList);
        if (_drawList.Count == 0)
            return;

        // S0 baseline: opens a group in the draw trace so a golden diff can name the object a line moved in.
        DrawTrace.Group(TraceBackend, "main", Name, _drawList.Count);

        // Buckets: Opaque / Fade (forced when overall alpha < 1) / Transparent (true BLEND materials).
        //
        // When alpha < 1, non-BLEND materials use the Fade PSO (blending plus depth writes) rather than the Transparent
        // PSO. Depth writes prevent the excessive transparency and internal-geometry bleed that sequential blending of
        // overlapping mesh layers produces in complex models.
        //
        // The two buckets are the same loop with a different mode, and only one of them ever runs in a frame, so the
        // mode is chosen once and the dedupe state starts empty either way.
        var mode = _currentAlpha < 1.0f ? SurfaceMode.Fade : SurfaceMode.Opaque;
        var tracker = default(StateTracker);
        for (int i = 0; i < _drawList.Count; i++)
        {
            var p = _drawList[i];
            if (p.IsTransparent) continue;
            // 2-2 rule 7: AoExempt primitives use the NoDepth variant and do not write depth.
            Set(ref tracker, new SurfaceState(mode, p.DoubleSided ? SurfaceCull.None : SurfaceCull.Back, !p.AoExempt));
            DrawMainPrimitive(p);
        }

        tracker = default;
        if (TransparentBucketOrder == TransparentOrder.Submission)
            DrawTransparentSubmission(ref tracker);
        else
            DrawTransparentSortedSplit(ref tracker);

        DrawHighlights();
    }

    /// <summary>
    /// Transparent bucket in submission order: single-sided primitives are drawn back-face only, double-sided ones
    /// twice, front then back. Nothing is sorted, so the recorded order is already the final one.
    /// </summary>
    private void DrawTransparentSubmission(ref StateTracker tracker)
    {
        // DepthWrite is true here only because the two-argument SetPipeline overload the original per-backend code
        // used defaults it to true, and the trace records it. Transparent never actually writes depth — the PSO has
        // it off — so this value is a trace-compatibility detail, not a claim about the pipeline.
        var back = new SurfaceState(SurfaceMode.Transparent, SurfaceCull.Back, true);
        for (int i = 0; i < _drawList.Count; i++)
        {
            var p = _drawList[i];
            if (!p.IsTransparent) continue;

            if (p.DoubleSided)
            {
                // Both faces unconditionally: the pair is the point of the branch, and the state after it is known,
                // so the tracker is marked rather than re-queried.
                ApplySurfaceState(new SurfaceState(SurfaceMode.Transparent, SurfaceCull.Front, true));
                DrawMainPrimitive(p);
                ApplySurfaceState(back);
                tracker.Mark(back);
                DrawMainPrimitive(p);
                continue;
            }

            Set(ref tracker, back);
            DrawMainPrimitive(p);
        }
    }

    /// <summary>
    /// Transparent bucket split into single-sided and double-sided tables. Sorted back to front when the caller is not
    /// recording; then single-sided primitives take one back-face pass, and each double-sided primitive takes a front
    /// and a back pass.
    /// </summary>
    private void DrawTransparentSortedSplit(ref StateTracker tracker)
    {
        _singleSidedTransparentList.Clear();
        _doubleSidedTransparentList.Clear();
        for (int i = 0; i < _drawList.Count; i++)
        {
            var p = _drawList[i];
            if (!p.IsTransparent) continue;
            if (p.DoubleSided)
                _doubleSidedTransparentList.Add(p);
            else
                _singleSidedTransparentList.Add(p);
        }

        if (SortTransparentNow)
        {
            _singleSidedTransparentList.Sort(CompareTransparent);
            _doubleSidedTransparentList.Sort(CompareTransparent);
        }

        // DepthWrite follows the same two-argument-default reasoning as DrawTransparentSubmission.
        var back = new SurfaceState(SurfaceMode.Transparent, SurfaceCull.Back, true);

        for (int i = 0; i < _singleSidedTransparentList.Count; i++)
        {
            Set(ref tracker, back);
            DrawMainPrimitive(_singleSidedTransparentList[i]);
        }

        // The double-sided table draws both faces per primitive with no dedupe between primitives: the front pass of
        // the next primitive must rebind even though the previous one ended on the back pass.
        for (int i = 0; i < _doubleSidedTransparentList.Count; i++)
        {
            var p = _doubleSidedTransparentList[i];
            ApplySurfaceState(new SurfaceState(SurfaceMode.Transparent, SurfaceCull.Front, true));
            DrawMainPrimitive(p);

            ApplySurfaceState(back);
            tracker.Mark(back);
            DrawMainPrimitive(p);
        }
    }

    private int CompareTransparent(TPrim a, TPrim b)
        => TransparentSortDepth(b).CompareTo(TransparentSortDepth(a));

    /// <summary>
    /// Rebinds only when the state actually differs. This is the rule every backend's per-bucket bool triple was
    /// encoding by hand: "set it if it is not already set, or if it changed".
    /// </summary>
    private void Set(ref StateTracker tracker, in SurfaceState state)
    {
        if (tracker.Valid && tracker.Current == state) return;
        ApplySurfaceState(state);
        tracker.Mark(state);
    }

    private struct StateTracker
    {
        public bool Valid;
        public SurfaceState Current;

        public void Mark(in SurfaceState state)
        {
            Valid = true;
            Current = state;
        }
    }

    // ============================================================
    // Shadow pass
    // ============================================================

    /// <summary>
    /// Shadow-pass draw path. Under contract rule 7 it performs no frustum culling: every opaque primitive is drawn,
    /// and true BLEND materials are skipped because primitives that do not write depth must not cast shadows. The pass
    /// entry has already set the PSO and root signature, so this only supplies group-invariant bindings and the
    /// per-primitive submits.
    ///
    /// The primitive list is cached per pass using <see cref="CascadedShadow.Epoch"/>. The shadow entry replays this
    /// method once per atlas quadrant (three cascades plus the spotlight); between quadrants only the viewport and the
    /// light-space ViewProj change, while the primitive set stays the same.
    /// </summary>
    public virtual void DrawShadow()
    {
        if (!_transformInitialized)
            return;

        if (_shadowDrawListEpoch != CascadedShadow.Epoch)
        {
            _shadowDrawList.Clear();
            CollectPrimitives(_shadowDrawList);
            _shadowDrawListEpoch = CascadedShadow.Epoch;
        }

        if (_shadowDrawList.Count == 0)
            return;

        DrawTrace.Group(TraceBackend, "shadow", Name, _shadowDrawList.Count);

        BeginShadowPass();

        // The sharing decision is a plain field comparison with no allocation, so it is recomputed per call instead of
        // cached; a cache would only buy a stale-state window. The filter must stay identical to the draw loop's
        // filter, which is why the skip happens before materialBound is set.
        bool shareMaterial = CanShareShadowMaterial(_shadowDrawList);
        bool materialBound = false;

        for (int i = 0; i < _shadowDrawList.Count; i++)
        {
            var p = _shadowDrawList[i];
            if (p.IsTransparent)
                continue;
            DrawShadowPrimitive(p, !shareMaterial || !materialBound);
            materialBound = true;
        }
    }
}
