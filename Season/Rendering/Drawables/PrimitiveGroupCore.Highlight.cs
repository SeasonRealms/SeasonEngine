// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Drawables;

/// <summary>
/// Unified highlighting: the lazy shell-geometry construction and the highlight-box lifetime, shared by every backend.
///
/// A highlight box is two primitives — faces and edges — that are built once on the first enabled frame and stay
/// resident afterwards. They live outside <see cref="CollectPrimitives"/> on purpose: <c>SyncAlpha</c> and
/// <c>SyncColorTint</c> never touch their material blocks, so the highlight alpha (a pulsing SurfaceColor.W) stays
/// independent of the host object's overall alpha, and the shadow pass excludes them for free.
///
/// Two styles exist:
/// <list type="bullet">
///   <item>Bounds — a world-space AABB box: faces plus edge strips, unit cube baked in [-0.5, 0.5]^3, world matrix =
///   Scale(Extents * 2) x Translate(Center);</item>
///   <item>Wireframe — surface-fitted shell faces plus edge strips in model-local space, with the world matrix coming
///   from the model or instance matrix. Vertices carry bone indices and weights, so the same vertex-skinning path keeps
///   them tightly aligned to animation.</item>
/// </list>
/// Both styles share the color pair: faces take SurfaceColor (w = 0 makes the box edge-only and skips the face
/// passes), edges take EdgeColor.
///
/// Only GPU resource creation is left to the backend, through the hooks below.
/// </summary>
internal abstract partial class PrimitiveGroupCore<TPrim, TMat, TTex>
{
    /// <summary>Whether wireframe highlighting (surface-fitted shell faces + edge strips) is enabled. Maintained by the
    /// derived Update methods. Geometry is built lazily on the first enabled frame and stays resident afterwards, so a
    /// fully disabled highlight costs no memory and no draw time.</summary>
    protected bool _wireframeEnabled;

    /// <summary>Whether the host Bounds box is enabled for this frame in the non-instanced path. Written during
    /// Update and used as a zero-cost gate during Draw.</summary>
    protected bool _boundsActive;

    /// <summary>Host Bounds box for the non-instanced path. Built lazily on the first enabled frame and kept
    /// resident afterwards.</summary>
    protected HighlightBox _boundsBox = null!;

    /// <summary>Per-instance Bounds-box pool indexed by compacted writeIndex. Grows lazily and stays resident until
    /// the group is released.</summary>
    protected readonly List<HighlightBox> _instanceBoundsBoxes = new();

    /// <summary>Compacted instance indices whose Bounds boxes are enabled this frame. Rebuilt every Update and drawn
    /// box by box during Draw.</summary>
    protected readonly List<int> _boundsBoxDrawList = new();

    /// <summary>Host Wireframe highlight boxes for the non-instanced path, one per primitive in
    /// <see cref="CollectPrimitives"/> order, with null placeholders for primitives that have no valid triangles.
    /// Built lazily on the first enabled frame and kept resident afterwards.</summary>
    protected List<HighlightBox>? _wireframeBoxes;

    /// <summary>Shared shell geometry for instanced templates, merging all non-skinned primitives. Per-instance boxes
    /// share its VB/IB and own only their material blocks. Built once lazily on the first enabled frame.</summary>
    protected HighlightBox? _shellGeometry;

    /// <summary>Shared skinned shell geometry for instanced templates, merging all skinned primitives that share one
    /// Skin. With IsSkinned = 1 it uses the per-instance bone-palette path and matches animation through the same
    /// vertex-skinning path as the main pass. Multi-skin assets, where each node owns its own Skin, are intentionally
    /// skipped in phase 1, so this stays null and is retried on later frames.</summary>
    protected HighlightBox? _skinnedShellGeometry;

    /// <summary>Per-instance Wireframe highlight-box pool over the shared template shell geometry. Matrices are read
    /// through the instance-stream writeIndex slot and drawn per instance.</summary>
    protected readonly List<HighlightBox> _instanceShellBoxes = new();

    /// <summary>Per-instance Wireframe highlight-box pool over the shared skinned template shell geometry. It shares
    /// the same index space as <see cref="_instanceShellBoxes"/>; for mixed assets the same writeIndex can own one box
    /// in each pool and draw both.</summary>
    protected readonly List<HighlightBox> _skinnedInstanceShellBoxes = new();

    /// <summary>Compacted instance indices whose Wireframe highlighting is enabled this frame. Rebuilt every Update
    /// and drawn box by box during Draw.</summary>
    protected readonly List<int> _shellBoxDrawList = new();

    /// <summary>Edge-strip width of the currently cached shell geometry. Recorded at build time, and rebuilt when it
    /// no longer matches the host's HighlightEdgeWidth so runtime width changes take effect immediately.</summary>
    protected float _builtShellEdgeWidth;

    /// <summary>
    /// One highlight box: a face primitive and an edge primitive.
    ///
    /// Faces are blended semi-transparent geometry using the Transparent pipeline; edges are solid thin strips using
    /// the Opaque pipeline with depth writes. PrevWorld is a CPU shadow copy because N-buffered constant blocks must
    /// never be read back; it feeds the TAA / motion-vector velocity field.
    /// </summary>
    protected sealed class HighlightBox
    {
        public TPrim Face = null!;
        public TPrim Edges = null!;

        /// <summary>Face alpha for this frame, taken from SurfaceColor.W. Faces are drawn only when it is greater than
        /// zero; at zero the box becomes edge-only. Recorded every frame by the write hook.</summary>
        public float FaceAlpha;

        /// <summary>Box world matrix from the previous frame, stored as a CPU shadow copy. Identity on the first
        /// frame, which acts as the zero-velocity sentinel.</summary>
        public Matrix4x4 PrevWorld = Matrix4x4.Identity;

        /// <summary>Owner node of the shell source primitive, recorded for non-instanced per-primitive shell boxes.
        /// Each frame it supplies WorldTransform x group world matrix from the same source as rendering, keeping node
        /// hierarchy scaling, translation and animation aligned. Null means identity, which is what Mesh3D procedural
        /// primitives and instanced shared-template boxes use.</summary>
        public GltfNodeBase? OwnerNode;

        /// <summary>Shell source primitive for non-instanced per-primitive shell boxes. Used for morph-weight
        /// synchronization: when source weights are written, the same set is propagated into the material blocks of
        /// both shell primitives. Shell delta buffers are expanded to the shell-vertex layout, while weights are shared
        /// with the source.</summary>
        public TPrim? SourcePrimitive;
    }

    // ============================================================
    // GPU creation hooks
    // ============================================================

    /// <summary>Builds the Bounds-box face primitive: eight corners baked into a unit cube, 36 indices.</summary>
    protected abstract TPrim CreateBoundsFacePrimitive();

    /// <summary>Builds the Bounds-box edge primitive: twelve thin boxes, four per axis.</summary>
    protected abstract TPrim CreateBoundsEdgesPrimitive();

    /// <summary>Builds a shell primitive from vertices and indices, copying the source material and forcing it to Unlit
    /// plus transparent or opaque. Skinning and instancing flags must be inherited from the source: that is what keeps
    /// non-instanced shell boxes aligned to animation.</summary>
    protected abstract TPrim CreateShellPrimitive(List<Vertex> vertices, uint[] indices, TMat sourceParams, bool isTransparent);

    /// <summary>Derives a box primitive that aliases a template's geometry and owns only its own material block and
    /// texture references.</summary>
    protected abstract TPrim CreateSharedGeometryPrimitive(TPrim template);

    /// <summary>Expands the source morph deltas onto a shell primitive's vertex layout. Called only for source
    /// primitives that actually carry morph targets.</summary>
    protected abstract void AttachShellMorph(TPrim shell, TPrim source, IReadOnlyList<int> sourceIndices);

    /// <summary>Draws one highlight box: faces as double-sided transparent two-pass, then edges as opaque.</summary>
    protected abstract void DrawHighlightBox(HighlightBox box);

    /// <summary>Writes the box world matrix and its dual face/edge colors into the box's own N-buffered constants for
    /// the current frame. There is no change gate: face alpha (SurfaceColor.W) pulses every frame, so the steady cost
    /// matches an ordinary primitive. <paramref name="world"/> belongs to the caller - <c>Scale(Extents * 2) x
    /// Translate(Center)</c> for the Bounds box, the model or instance world matrix for a shell. The shared model trait
    /// calls this, so only the write path is per-API.</summary>
    protected abstract void WriteHighlightBox(HighlightBox box, Matrix4x4 world, Vector4 faceColor, Vector4 edgeColor);

    /// <summary>Whether the previous-frame bone bit applies to this shell primitive. Metal gates it per primitive
    /// because only a skinned shell has a palette to read; DX and Vulkan publish it for every shell.</summary>
    protected virtual bool ShellPrevBonesApplies(TPrim primitive) => true;

    // ============================================================
    // Bounds boxes
    // ============================================================

    /// <summary>Lazily builds the host Bounds box (faces + edges). Called once on the first enabled frame and kept
    /// resident afterwards.</summary>
    protected HighlightBox CreateBoundsBox()
    {
        var box = new HighlightBox();
        box.Face = CreateBoundsFacePrimitive();
        box.Edges = CreateBoundsEdgesPrimitive();
        return box;
    }

    /// <summary>Gets or creates the instance Bounds box for the given compacted writeIndex. The pool grows lazily and
    /// stays resident until the group is released.</summary>
    protected HighlightBox AcquireBoundsBox(int index)
    {
        while (_instanceBoundsBoxes.Count <= index)
            _instanceBoundsBoxes.Add(CreateBoundsBox());
        return _instanceBoundsBoxes[index];
    }

    // ============================================================
    // Wireframe shells
    // ============================================================

    /// <summary>
    /// Lazily builds the non-instanced Wireframe highlight boxes on the first frame that enables wireframe.
    ///
    /// All primitives are processed in <see cref="CollectPrimitives"/> order, with null placeholders for primitives
    /// that have no valid triangles. When fully disabled this costs no memory; once built, the geometry stays resident
    /// and is not rebuilt or released when toggled at runtime. Each primitive gets its own box, and skinning
    /// parameters are inherited from the source primitive, so skinned models stay aligned through the same bone-palette
    /// path.
    ///
    /// <paramref name="edgeWidth"/> comes from the host Highlight.EdgeWidth (a model-size proportion) and
    /// <paramref name="localSizeMax"/> is the host model's maximum local dimension used as the scale baseline. The
    /// baked per-primitive local thickness is h = edgeWidth x localSizeMax / node scale, giving a world-space edge
    /// width of roughly edgeWidth x model world max dimension that stays consistent across assets.
    /// </summary>
    protected void EnsureWireframeHighlights(float edgeWidth, float localSizeMax)
    {
        if (_wireframeBoxes != null)
        {
            if (_builtShellEdgeWidth == edgeWidth)
                return;
            // Edge width changed: release old shell geometry and rebuild with the new width so it takes effect
            // immediately this frame.
            foreach (var box in _wireframeBoxes)
                DisposeHighlightBox(box);
            _wireframeBoxes = null;
        }
        _drawList.Clear();
        CollectPrimitives(_drawList);
        if (_drawList.Count == 0)
            return;
        _wireframeBoxes = new List<HighlightBox>(_drawList.Count);
        for (int i = 0; i < _drawList.Count; i++)
        {
            var source = _drawList[i];
            _wireframeBoxes.Add(source.Indices.Length >= 3 && source.Vertices.Count > 0
                ? CreateShellBox(source, HighlightGeometry.ComputeShellThickness(edgeWidth, localSizeMax, source.OwnerNode))
                : null!);
            // Record the node reference on the box so WorldTransform can be fetched each frame from the same source as
            // rendering. Cloned primitives are collected by CollectPrimitives and share the group's lifetime.
            if (_wireframeBoxes[i] != null)
                _wireframeBoxes[i].OwnerNode = source.OwnerNode;
        }
        _builtShellEdgeWidth = edgeWidth;
    }

    /// <summary>
    /// Builds a Wireframe shell highlight box for a single source primitive: shell faces plus edge strips.
    ///
    /// Source vertices are copied field by field, including bone indices and weights, so skinned models stay aligned
    /// through the same vertex-skinning path. Material parameters are copied from the source and then forced to Unlit
    /// plus transparent or opaque as needed. For morph-target sources, shell delta buffers are expanded to the
    /// shell-vertex layout, with the source-vertex index recorded per shell vertex at build time; weights are shared
    /// with the source, so the same morph path stays tightly aligned to morph animation.
    ///
    /// Edge strips have a full width of 2h and shell faces use the same outward expansion thickness.
    /// </summary>
    protected HighlightBox CreateShellBox(TPrim source, float edgeWidth)
    {
        var faceVerts = new List<Vertex>(source.Vertices.Count);
        var faceIndices = new List<uint>(source.Indices.Length);
        var faceSrcIdx = new List<int>(source.Vertices.Count);
        HighlightGeometry.AppendShellFace(faceVerts, faceIndices, source.Vertices, source.Indices, edgeWidth, faceSrcIdx);

        var edgeVerts = new List<Vertex>(source.Indices.Length * 2);
        var edgeIndices = new List<uint>(source.Indices.Length * 2);
        var edgeSrcIdx = new List<int>(source.Indices.Length * 2);
        HighlightGeometry.AppendShellEdges(edgeVerts, edgeIndices, source.Vertices, source.Indices, edgeWidth, edgeSrcIdx);

        var box = new HighlightBox();
        box.Face = CreateShellPrimitive(faceVerts, faceIndices.ToArray(), source.MaterialParams, isTransparent: true);
        box.Edges = CreateShellPrimitive(edgeVerts, edgeIndices.ToArray(), source.MaterialParams, isTransparent: false);
        box.SourcePrimitive = source;
        if (source.MaterialParams.HasMorphTargets != 0 && source.MorphTargets != null && source.MorphTargets.Count > 0)
        {
            AttachShellMorph(box.Face, source, faceSrcIdx);
            AttachShellMorph(box.Edges, source, edgeSrcIdx);
        }
        return box;
    }

    /// <summary>
    /// Lazily builds shared shell geometry for instanced templates on the first frame that enables wireframe.
    ///
    /// It groups by source: a rigid shell from merged non-skinned primitives, with per-instance boxes sharing VB/IB,
    /// plus a skinned shell from merged primitives that share the same Skin. The skinned shell inherits IsSkinned = 1
    /// and the bone-palette stride from the source material and uses the per-instance bone-palette path to stay aligned
    /// to animation. Mixed assets draw both shells; assets that are purely skinned with a single Skin only emit the
    /// skinned one.
    ///
    /// Morph-target primitives are skipped, because morph weights are indexed per instance and would need shell-shape
    /// delta buffers that merged geometry cannot express. This is documented behaviour: for instanced models with
    /// morph, Wireframe highlighting covers only the remaining parts, while Bounds highlighting is unaffected.
    ///
    /// Multi-skin assets, where each node has an independent Skin, skip the skinned shell because the merged template
    /// cannot express per-skin palette offsets. When no usable primitives exist the templates stay null and are retried
    /// on later frames.
    /// </summary>
    protected void EnsureShellGeometry(float edgeWidth, float localSizeMax)
    {
        if (_shellGeometry != null || _skinnedShellGeometry != null)
        {
            if (_builtShellEdgeWidth == edgeWidth)
                return;
            // Edge width changed: release all shared templates and instance shell-box pools, which share template
            // geometry, and rebuild with the new width so it takes effect immediately this frame.
            DisposeShellTemplatesAndPools();
        }
        _drawList.Clear();
        CollectPrimitives(_drawList);
        if (_drawList.Count == 0)
            return;

        var rigidFaceVerts = new List<Vertex>();
        var rigidFaceIndices = new List<uint>();
        var rigidEdgeVerts = new List<Vertex>();
        var rigidEdgeIndices = new List<uint>();
        var skinFaceVerts = new List<Vertex>();
        var skinFaceIndices = new List<uint>();
        var skinEdgeVerts = new List<Vertex>();
        var skinEdgeIndices = new List<uint>();
        TMat rigidParams = default;
        TMat skinParams = default;
        bool anyRigid = false;
        bool anySkinned = false;
        bool multiSkin = false;
        GLTFSkin? sharedSkin = null;

        for (int i = 0; i < _drawList.Count; i++)
        {
            var source = _drawList[i];
            if (source.Indices.Length < 3 || source.Vertices.Count == 0)
                continue;
            if (source.MaterialParams.HasMorphTargets != 0)
                continue; // Skip morph-target primitives because merged instanced
                          // templates cannot express per-primitive morph sets.
            float h = HighlightGeometry.ComputeShellThickness(edgeWidth, localSizeMax, source.OwnerNode);
            if (source.MaterialParams.IsSkinned != 0)
            {
                if (multiSkin)
                    continue; // Multi-skin already detected: skip the skinned shell entirely.
                // For primitives sharing the same Skin, OwnerNode.Skin is mapped through the same skinMap to the same
                // cloned reference, so ReferenceEquals is reliable here.
                var skin = source.OwnerNode?.Skin;
                if (skin == null)
                    continue; // Defensive skip: marked skinned but has no Skin data.
                if (sharedSkin == null)
                {
                    sharedSkin = skin;
                }
                else if (!ReferenceEquals(sharedSkin, skin))
                {
                    // Multi-skin asset: discard accumulated skinned data and skip the skinned shell entirely.
                    multiSkin = true;
                    anySkinned = false;
                    skinFaceVerts.Clear();
                    skinFaceIndices.Clear();
                    skinEdgeVerts.Clear();
                    skinEdgeIndices.Clear();
                    continue;
                }
                if (!anySkinned)
                    skinParams = source.MaterialParams;
                HighlightGeometry.AppendShellFace(skinFaceVerts, skinFaceIndices, source.Vertices, source.Indices, h);
                HighlightGeometry.AppendShellEdges(skinEdgeVerts, skinEdgeIndices, source.Vertices, source.Indices, h);
                anySkinned = true;
            }
            else
            {
                if (!anyRigid)
                    rigidParams = source.MaterialParams;
                HighlightGeometry.AppendShellFace(rigidFaceVerts, rigidFaceIndices, source.Vertices, source.Indices, h);
                HighlightGeometry.AppendShellEdges(rigidEdgeVerts, rigidEdgeIndices, source.Vertices, source.Indices, h);
                anyRigid = true;
            }
        }
        if (!anyRigid && !anySkinned)
            return; // No usable primitives: keep null and retry on later frames.

        if (anyRigid)
        {
            var shell = new HighlightBox();
            shell.Face = CreateShellPrimitive(rigidFaceVerts, rigidFaceIndices.ToArray(), rigidParams, isTransparent: true);
            shell.Edges = CreateShellPrimitive(rigidEdgeVerts, rigidEdgeIndices.ToArray(), rigidParams, isTransparent: false);
            _shellGeometry = shell;
        }
        if (anySkinned)
        {
            var shell = new HighlightBox();
            shell.Face = CreateShellPrimitive(skinFaceVerts, skinFaceIndices.ToArray(), skinParams, isTransparent: true);
            shell.Edges = CreateShellPrimitive(skinEdgeVerts, skinEdgeIndices.ToArray(), skinParams, isTransparent: false);
            _skinnedShellGeometry = shell;
        }
        _builtShellEdgeWidth = edgeWidth;
    }

    /// <summary>Gets or creates the instance Wireframe shell box for the given compacted writeIndex using a lazily
    /// growing pool. VB/IB are shared from the template, so the box's own buffers stay null and per-primitive disposal
    /// is null-safe. Only material blocks are created locally; matrices come from the group-level shared block.
    ///
    /// Returns null when the template is not ready because no non-skinned, non-morph primitives exist, and callers
    /// should skip adding it to the draw list. Empty slots created before the template was ready are backfilled on
    /// first use after it becomes available, which covers runtime asset replacement followed by a shell rebuild.</summary>
    protected HighlightBox? AcquireShellBox(int index)
    {
        if (_shellGeometry == null)
            return null;
        while (_instanceShellBoxes.Count <= index)
            _instanceShellBoxes.Add(CreateInstanceShellBox()!);
        var box = _instanceShellBoxes[index];
        if (box == null)
        {
            box = CreateInstanceShellBox()!;
            _instanceShellBoxes[index] = box;
        }
        return box;
    }

    /// <summary>Builds the per-instance shell box that shares the template geometry.</summary>
    private HighlightBox? CreateInstanceShellBox()
    {
        if (_shellGeometry == null)
            return null;
        var box = new HighlightBox();
        box.Face = CreateSharedGeometryPrimitive(_shellGeometry.Face);
        box.Edges = CreateSharedGeometryPrimitive(_shellGeometry.Edges);
        return box;
    }

    /// <summary>Gets or creates the skinned instance Wireframe shell box for the given compacted writeIndex, following
    /// the same structure as <see cref="AcquireShellBox"/>. The shell uses the per-instance bone-palette path.
    ///
    /// Returns null when the template is not ready because there are no single-Skin primitives or the asset is
    /// multi-skin.</summary>
    protected HighlightBox? AcquireSkinnedShellBox(int index)
    {
        if (_skinnedShellGeometry == null)
            return null;
        while (_skinnedInstanceShellBoxes.Count <= index)
            _skinnedInstanceShellBoxes.Add(CreateSkinnedInstanceShellBox()!);
        var box = _skinnedInstanceShellBoxes[index];
        if (box == null)
        {
            box = CreateSkinnedInstanceShellBox()!;
            _skinnedInstanceShellBoxes[index] = box;
        }
        return box;
    }

    /// <summary>Builds the per-instance shell box that shares the skinned template geometry.</summary>
    private HighlightBox? CreateSkinnedInstanceShellBox()
    {
        if (_skinnedShellGeometry == null)
            return null;
        var box = new HighlightBox();
        box.Face = CreateSharedGeometryPrimitive(_skinnedShellGeometry.Face);
        box.Edges = CreateSharedGeometryPrimitive(_skinnedShellGeometry.Edges);
        return box;
    }

    /// <summary>Releases the shared shell templates (rigid and skinned) and both instance shell-box pools, which share
    /// template geometry. Used by edge-width rebuilds and by <see cref="DisposeHighlights"/>.</summary>
    private void DisposeShellTemplatesAndPools()
    {
        if (_shellGeometry != null)
        {
            DisposeHighlightBox(_shellGeometry);
            _shellGeometry = null;
        }
        if (_skinnedShellGeometry != null)
        {
            DisposeHighlightBox(_skinnedShellGeometry);
            _skinnedShellGeometry = null;
        }
        foreach (var box in _instanceShellBoxes)
            DisposeHighlightBox(box);
        _instanceShellBoxes.Clear();
        foreach (var box in _skinnedInstanceShellBoxes)
            DisposeHighlightBox(box);
        _skinnedInstanceShellBoxes.Clear();
        _shellBoxDrawList.Clear();
    }

    /// <summary>
    /// 2-3 Step C: synchronizes the previous-state flags of shell primitives. HasPrevBones, HasPrevInstanceWorld and
    /// HasPrevMorph on a shell's Face/Edges have to be enabled together with the main primitive, or the shell loses its
    /// motion trail. Both shared templates and both instance-box pools are covered, because a pooled box may have been
    /// created before previous-state data became ready and still carry stale flags.
    /// </summary>
    protected void SyncShellPrevFlags(bool hasPrevInstanceWorld, bool hasPrevBones, bool hasPrevMorph)
    {
        if (_shellGeometry != null)
            SyncShellBoxPrevFlags(_shellGeometry, hasPrevInstanceWorld, hasPrevBones, hasPrevMorph);
        if (_skinnedShellGeometry != null)
            SyncShellBoxPrevFlags(_skinnedShellGeometry, hasPrevInstanceWorld, hasPrevBones, hasPrevMorph);
        foreach (var box in _instanceShellBoxes)
            SyncShellBoxPrevFlags(box, hasPrevInstanceWorld, hasPrevBones, hasPrevMorph);
        foreach (var box in _skinnedInstanceShellBoxes)
            SyncShellBoxPrevFlags(box, hasPrevInstanceWorld, hasPrevBones, hasPrevMorph);
    }

    private void SyncShellBoxPrevFlags(HighlightBox box, bool hasPrevInstanceWorld, bool hasPrevBones, bool hasPrevMorph)
    {
        SyncShellPrimPrevFlags(box.Face, hasPrevInstanceWorld, hasPrevBones, hasPrevMorph);
        SyncShellPrimPrevFlags(box.Edges, hasPrevInstanceWorld, hasPrevBones, hasPrevMorph);
    }

    // Not static: it publishes through the backend's material-slot hook, which is what makes the write land in the
    // same place the backend's own copy of this helper used to write.
    private void SyncShellPrimPrevFlags(TPrim primitive, bool hasPrevInstanceWorld, bool hasPrevBones, bool hasPrevMorph)
    {
        // Set, not just raise: the caller is the single per-frame publisher of these three bits, and it has to be able
        // to withdraw previous-frame data for a frame whose instance order changed.
        uint prevWorld = hasPrevInstanceWorld ? 1u : 0u;
        uint prevBones = hasPrevBones && ShellPrevBonesApplies(primitive) ? 1u : 0u;
        uint prevMorph = hasPrevMorph ? 1u : 0u;
        if (primitive.MaterialParams.HasPrevInstanceWorld == prevWorld
            && primitive.MaterialParams.HasPrevBones == prevBones
            && primitive.MaterialParams.HasPrevMorph == prevMorph)
            return;

        primitive.MaterialParams.HasPrevInstanceWorld = prevWorld;
        primitive.MaterialParams.HasPrevBones = prevBones;
        primitive.MaterialParams.HasPrevMorph = prevMorph;
        // Publishes the whole CPU shadow block, not just the three bits. That is what the backend copies used to do,
        // and the shadow is the authoritative copy for every field no sync path owns.
        for (int f = 0; f < FrameCount; f++)
            MaterialSlot(primitive, f) = primitive.MaterialParams;
    }

    // ============================================================
    // Drawing and lifetime
    // ============================================================

    /// <summary>Draws the highlight boxes this group owns: the host Bounds box, then the per-primitive Wireframe
    /// boxes. Instance pools are drawn by the instanced paths, which know their own instance streams.</summary>
    protected virtual void DrawHighlights()
    {
        if (_boundsActive && _boundsBox != null)
            DrawHighlightBox(_boundsBox);
        if (_wireframeEnabled && _wireframeBoxes != null)
        {
            for (int i = 0; i < _wireframeBoxes.Count; i++)
            {
                var highlight = _wireframeBoxes[i];
                if (highlight != null)
                    DrawHighlightBox(highlight);
            }
        }
    }

    /// <summary>Releases all highlight GPU resources: the host Bounds box, the instance Bounds-box pool, per-primitive
    /// Wireframe shell boxes, shared shell templates (rigid and skinned) and the Wireframe instance-box pools.</summary>
    protected void DisposeHighlights()
    {
        DisposeHighlightBox(_boundsBox);
        _boundsBox = null!;
        foreach (var box in _instanceBoundsBoxes)
            DisposeHighlightBox(box);
        _instanceBoundsBoxes.Clear();
        _boundsBoxDrawList.Clear();
        _boundsActive = false;

        if (_wireframeBoxes != null)
        {
            foreach (var box in _wireframeBoxes)
                DisposeHighlightBox(box);
            _wireframeBoxes = null;
        }
        DisposeShellTemplatesAndPools();
        _wireframeEnabled = false;
        _builtShellEdgeWidth = 0f;
        _outline2DActive = false;
    }

    private static void DisposeHighlightBox(HighlightBox? box)
    {
        if (box == null)
            return;
        box.Face?.Dispose();
        box.Edges?.Dispose();
    }
}
