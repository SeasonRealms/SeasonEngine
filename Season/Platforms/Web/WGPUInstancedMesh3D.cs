// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Platforms.Web;

internal sealed class WGPUInstancedMesh3D : Season.Rendering.IInstanceCullTarget
{
    public string Name { get; }

    public readonly Dictionary<object, WGPUMesh3D.SurfaceCacheEntry> SurfaceCaches = new();

    /// <summary>Snapshot of resolved five-slot texture names taken during Load, with the same semantics as WGPUMesh3D.ResolvedTextures.</summary>
    public readonly Dictionary<Surface, WGPUMesh3D.ResolvedTextureSet> ResolvedTextures = new();

    public Matrix4x4[] InstanceWorlds { get; private set; } = Array.Empty<Matrix4x4>();
    public byte[] InstanceBytes { get; private set; } = Array.Empty<byte>();

    // 2-3 Step C (contract clauses 6 + 8(b)): the other side of the double buffer for instance byte streams
    // (5 vec4 values per instance; this class has no morph path, so the 5th one is always zero).
    // A non-empty PrevInstanceBytes means the stream has been ready for two consecutive frames.
    public byte[] PrevInstanceBytes { get; private set; } = Array.Empty<byte>();
    public int EnabledInstanceCount { get; private set; }
    public float MeshAlpha { get; set; } = 1f;
    public bool TransformInitialized { get; set; }

    // Unified highlight: per-instance bounds boxes for the current frame (lazy-growing pool + draw list; Wireframe shell extends this in Phase 3).
    internal bool BoundsActive { get; private set; }
    internal readonly List<WebBoundsBox> InstanceBoundsBoxes = new();
    internal readonly List<int> BoundsBoxDrawList = new();

    // Unified highlight: per-instance wireframe shells for the current frame
    // (merged template shell geometry + draw entries captured during Update; no JS-side changes,
    // and the per-instance world matrix is baked into uniforms for non-instanced batch draws, following the DrawBoundsBox pattern).
    internal bool WireframeActive { get; private set; }
    internal WebShellBox? ShellGeometry { get; private set; }
    internal float BuiltShellEdgeWidth;
    internal readonly List<ShellDrawEntry> ShellDrawList = new();
    // CPU shadow copy of the previous-frame world matrix per slot
    // (rolled forward before overwriting the current frame, in the same order as host-box PrevWorld; invalidated on capacity changes).
    Matrix4x4[] ShellPrevWorlds = Array.Empty<Matrix4x4>();

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

    // === A7 per-instance GPU culling ===
    // The Web side has no "group" object, so the cull target can only hang off the mesh-instance host; the C# side only holds the JS handle of the StorageBuffer,
    // while the real GPUBuffer lives in JS _storageBuffers[Id]. One structural difference from the other three backends is that this class's instance stream
    // is pushed down byte by byte by C# every frame, but the upload timing still matches the other three: all are deferred to the Record phase,
    // where InstanceCullEffect calls back PrepareCullInputs outside any Pass (WebGPU's writeBuffer is likewise not allowed inside a Pass).

    Season.Rendering.StorageBuffer? _cullSource;
    Season.Rendering.StorageBuffer? _cullSorted;
    Season.Rendering.StorageBuffer? _cullArgs;
    Season.Rendering.StorageBuffer? _cullVisible;
    Season.Rendering.StorageBuffer? _cullPrimInfo;
    int _cullCapacity = -1;
    /// <summary>Number of parameter-table entries already uploaded to the GPU. A mismatch with <see cref="_cullPrimPendingCount"/> means this frame must re-upload.</summary>
    int _cullPrimitiveCount = -1;
    /// <summary>Parameter-table bytes already built on the CPU side: building can happen at any time, but uploading must wait for a phase outside the Pass
    /// (see <see cref="Season.Rendering.IInstanceCullTarget.PrepareCullInputs"/>).</summary>
    byte[]? _cullPrimPendingBytes;
    /// <summary>Entry count of the CPU-side parameter table; -1 means it has not been built yet.</summary>
    int _cullPrimPendingCount = -1;
    bool _cullRegistered;
    Vector3 _cullLocalCenter;
    Vector3 _cullLocalExtents;
    bool _cullDrawReady;
    /// <summary>Latched from the owner's draw path once per Update, so the cull record and the draw side of one frame agree on it.</summary>
    bool _cullMainView;

    /// <summary>
    /// Parameter-table signature: one bit per surface, using exactly the same predicate as <c>DrawInstancedMesh3DShadow</c> (opaque + has geometry + uploaded).
    /// A bit mask is used instead of a count because comparing only the count would treat "one fewer surface, one more surface" as unchanged,
    /// and once indexCount is misaligned the draw is simply wrong.
    /// </summary>
    int _cullPrimMask = -1;
    /// <summary>Mask pending upload. The two are kept separate because <see cref="_cullPrimMask"/> is the uploaded state aligned with the draw side,
    /// while this table may already have been rebuilt due to geometry changes: write the pending value first, and only let the draw side see it after a successful upload.</summary>
    int _cullPrimPendingMask = -1;
    readonly int[] _cullPrimIndexCounts = new int[Season.Rendering.InstanceCull.MaxPrimitives];

    public WGPUInstancedMesh3D(string name)
    {
        Name = name;
    }

    public void Update(Season.Controls.InstancedMesh3D mesh)
    {
        // Unified highlight: clear the per-instance bounds draw list for this frame
        // (rebuilt every frame; BoundsActive is set by the per-instance hook below).
        BoundsActive = false;
        BoundsBoxDrawList.Clear();
        // Unified highlight: clear the per-instance wireframe shell draw list for this frame
        // (rebuilt every frame; WireframeActive is set by the per-instance hook below).
        WireframeActive = false;
        ShellDrawList.Clear();
        // Unified highlight: clear the per-instance Outline2D slot list for this frame
        // (rebuilt every frame; aggregate state is written by the hook below and finalized after the loop).
        Outline2DInstances.Clear();
        Outline2DInstanceColors.Clear();

        EnabledInstanceCount = 0;
        for (int i = 0; i < mesh.Instances.Count; i++)
        {
            if (mesh.Instances[i].Enable)
                EnabledInstanceCount++;
        }

        if (EnabledInstanceCount <= 0)
        {
            InstanceWorlds = Array.Empty<Matrix4x4>();
            InstanceBytes = Array.Empty<byte>();
            // 2-3 Step C: all instances are disabled, so previous-frame history is discarded.
            PrevInstanceBytes = Array.Empty<byte>();
            TransformInitialized = true;
            MeshAlpha = mesh.Alpha;
            // Reset Outline2D aggregate state (all instances disabled -> no outline).
            Outline2DActive = false;
            Outline2DHostActive = false;
            Outline2DMaskColor = default;
            Outline2DMaskWidth = 0f;
            _cullDrawReady = false;
            _cullMainView = false;
            return;
        }

        if (InstanceWorlds.Length != EnabledInstanceCount)
        {
            InstanceWorlds = new Matrix4x4[EnabledInstanceCount];
            // Capacity changed: discard per-slot history
            // (same policy as the InstanceBytes double-buffer reset below; the first frame also goes through this path).
            ShellPrevWorlds = new Matrix4x4[EnabledInstanceCount];
        }

        int writeIndex = 0;
        for (int i = 0; i < mesh.Instances.Count; i++)
        {
            var instance = mesh.Instances[i];
            if (!instance.Enable)
                continue;

            // Unified transform pattern: converge on BuildInstanceMatrix
            // (anchor-pivot semantics, see InstancedMesh3DBase).
            // 2-3 contract clause 6: roll the shadow copy forward before overwriting the current-frame world matrix
            // (same ordering as host UpdateMesh3D; zero matrix sentinel on the first frame).
            var instanceWorld = mesh.BuildInstanceMatrix(instance);
            if (TransformInitialized)
                ShellPrevWorlds[writeIndex] = InstanceWorlds[writeIndex];
            InstanceWorlds[writeIndex] = instanceWorld;

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

            // Unified highlight (per-instance bounds box): box alpha/color is independent from the host-wide alpha chain.
            // Do not enable highlighting when extents are near zero (not loaded or degenerate box).
            if (instance.Highlight.Bounds)
            {
                var worldBounds = mesh.GetInstanceWorldBoundsRaw(instance);
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
            // (skips skinned, morph, and degenerate surfaces; see EnsureShellGeometry).
            // Each instance contributes the world/previous-world/color captured during Update to the draw list
            // (no previous state on the first frame -> zero-velocity sentinel).
            if (instance.Highlight.Wireframe)
            {
                EnsureShellGeometry(mesh, mesh.Highlight.EdgeWidth);
                WireframeActive = true;
                ShellDrawList.Add(new ShellDrawEntry(
                    writeIndex,
                    InstanceWorlds[writeIndex],
                    ShellPrevWorlds[writeIndex],
                    instance.Highlight.SurfaceColor,
                    instance.Highlight.EdgeColor));
            }

            writeIndex++;
        }

        // Outline2D active = host active union any active instance
        // (host activation uses the full mask and ignores the per-instance list).
        // Color/width: when any instance is active, prefer the instance values
        // (the panel color written by picker); otherwise fall back to the host values, matching Mesh3D/Model semantics.
        Outline2DHostActive = mesh.Highlight.Outline;
        bool anyInstanceOutline = Outline2DInstances.Count > 0;
        Outline2DActive = Outline2DHostActive || anyInstanceOutline;
        Outline2DMaskColor = anyInstanceOutline ? _outline2DInstanceColor : mesh.Highlight.OutlineColor;
        Outline2DMaskWidth = anyInstanceOutline ? _outline2DInstanceWidth : mesh.Highlight.OutlineWidth;

        // 2-3 Step C: swap the two sides of the byte-stream double buffer
        // - the previous frame's finished buffer becomes the prev side, and the old prev side is recycled as the current-frame write target.
        // FillInstanceBytes overwrites all 20 floats per instance, so no clearing is needed.
        // A length mismatch means first frame or a recent instance-count change, so history is treated as unavailable.
        int expectedBytes = InstanceWorlds.Length * 20 * sizeof(float);
        byte[] writeTarget;
        if (expectedBytes > 0 && InstanceBytes.Length == expectedBytes)
        {
            writeTarget = PrevInstanceBytes.Length == expectedBytes ? PrevInstanceBytes : new byte[expectedBytes];
            PrevInstanceBytes = InstanceBytes;
        }
        else
        {
            writeTarget = InstanceBytes;
            PrevInstanceBytes = Array.Empty<byte>();
        }

        InstanceBytes = FillInstanceBytes(writeTarget, InstanceWorlds);
        TransformInitialized = true;
        MeshAlpha = mesh.Alpha;

        // A7: the cull input is bit-for-bit identical to the IA instance stream (both are 20 floats = 80B, matching InstanceCull.WorldStride),
        // so reuse InstanceBytes directly instead of keeping another copy.
        UpdateCullUpload(mesh);
    }

    /// <summary>Unified highlight (wireframe shell): lazily builds merged template shell geometry.
    /// On the first instance frame with wireframe enabled, all surfaces are merged into a single shell
    /// (faces and edges use separate cache keys, and geometry is uploaded only once).
    /// If every surface is degenerate, an empty shell is created, which naturally produces no draw calls because the draw list stays empty.
    /// <c>edgeWidth</c> comes from the host Highlight.EdgeWidth (scaled relative to model size), and <c>localSizeMax</c> is the largest dimension of
    /// TemplateLocalSize (the scaling baseline). When they no longer match the host state, the shell is released and rebuilt immediately in the same frame.</summary>
    void EnsureShellGeometry(Season.Controls.InstancedMesh3D mesh, float edgeWidth)
    {
        if (ShellGeometry != null)
        {
            if (BuiltShellEdgeWidth == edgeWidth)
                return;
            // Edge width changed: invalidate the old shell geometry
            // (JS-side GPU resources are reclaimed by GC) and rebuild with the new width immediately in this frame.
            ShellGeometry = null;
        }

        var sources = new List<ShellMeshSource>();
        var localSizeMax = MathF.Max(mesh.TemplateLocalSize.X, MathF.Max(mesh.TemplateLocalSize.Y, mesh.TemplateLocalSize.Z));
        foreach (var surface in mesh.Surfaces)
        {
            if (surface.Vertices == null || surface.Indices == null || surface.Vertices.Length == 0 || surface.Indices.Length < 3)
                continue;
            sources.Add(new ShellMeshSource(
                surface.Vertices, Array.ConvertAll(surface.Indices, static i => (uint)i),
                HighlightGeometry.ComputeShellThickness(edgeWidth, localSizeMax, null)));
        }

        ShellGeometry = WebShellBox.CreateMerged($"{Name}:INST", sources);
        BuiltShellEdgeWidth = edgeWidth;
    }

    /// <summary>Unified highlight: gets or creates the per-instance bounds box for the compacted writeIndex
    /// (lazy-growing pool, resident after creation; cache keys are stable and unique per slot, so GPU geometry is uploaded only once).</summary>
    WebBoundsBox AcquireBoundsBox(int index)
    {
        while (InstanceBoundsBoxes.Count <= index)
            InstanceBoundsBoxes.Add(WebBoundsBox.Create($"{Name}:INST:{InstanceBoundsBoxes.Count}"));
        return InstanceBoundsBoxes[index];
    }

    // ============================================================
    // A7 per-instance GPU culling
    // ============================================================

    int Season.Rendering.IInstanceCullTarget.CullInstanceCount => EnabledInstanceCount;

    int Season.Rendering.IInstanceCullTarget.CullCapacity => _cullCapacity;

    /// <summary>Falls back to 0 while the parameter table is not yet aligned with the draw side, so the shared layer early-returns and this mesh falls back to whole-batch CPU culling,
    /// rather than drawing with a misaligned index range.</summary>
    int Season.Rendering.IInstanceCullTarget.CullPrimitiveCount => _cullPrimitiveCount;

    Season.Rendering.StorageBuffer? Season.Rendering.IInstanceCullTarget.CullSourceWorlds => _cullSource;

    /// <summary>Single stream: every surface reads the one instance stream, which is also the cull input, so gather reads it straight
    /// from <see cref="_cullSource"/> and writes the one reordered stream.</summary>
    int Season.Rendering.IInstanceCullTarget.CullStreamCount => _cullSorted != null ? 1 : 0;

    Season.Rendering.StorageBuffer? Season.Rendering.IInstanceCullTarget.CullStreamSource(int stream) => stream == 0 ? _cullSource : null;

    Season.Rendering.StorageBuffer? Season.Rendering.IInstanceCullTarget.CullStreamSorted(int stream) => stream == 0 ? _cullSorted : null;

    Season.Rendering.StorageBuffer? Season.Rendering.IInstanceCullTarget.CullPrimitiveInfo => _cullPrimInfo;

    Season.Rendering.StorageBuffer? Season.Rendering.IInstanceCullTarget.CullArgs => _cullArgs;

    Season.Rendering.StorageBuffer? Season.Rendering.IInstanceCullTarget.CullVisible => _cullVisible;

    Vector3 Season.Rendering.IInstanceCullTarget.CullLocalCenter => _cullLocalCenter;

    Vector3 Season.Rendering.IInstanceCullTarget.CullLocalExtents => _cullLocalExtents;

    bool Season.Rendering.IInstanceCullTarget.CullMainView => _cullMainView;

    /// <summary>Whether this frame can go straight to indirect shadow drawing: false when any of capacity, handles, or the parameter table is not ready.</summary>
    internal bool TryGetCullDrawHandles(out string sortedId, out string argsId, out int capacity)
    {
        sortedId = string.Empty;
        argsId = string.Empty;
        capacity = 0;
        if (!_cullDrawReady || _cullCapacity <= 0 || _cullCapacity < EnabledInstanceCount)
            return false;
        if (_cullPrimitiveCount <= 0)
            return false;
        if (_cullSorted is not WGPUStorageBuffer sorted || _cullArgs is not WGPUStorageBuffer args)
            return false;
        sortedId = sorted.Id;
        argsId = args.Id;
        capacity = _cullCapacity;
        return true;
    }

    /// <summary>
    /// A7 main view: whether the opaque body may draw from the main-view segment right now, and where it is.
    /// Every condition is a reason the segment would be wrong rather than merely slower, so any miss falls back to the direct draw:
    /// the owner did not ask for it; this frame's chain did not run or did not include the main-view slot (its args would still
    /// hold reset's zero instances and the mesh would vanish); the view being drawn is not the one the segment was culled for
    /// (mirrors, minimaps); or the uploaded parameter table no longer covers the surfaces the caller is about to walk.
    /// </summary>
    /// <param name="primitiveMask">
    /// One bit per surface that has an args record, in the table's own order. The caller must pick indirect surfaces by this mask
    /// rather than re-evaluating "uploaded", because its loop uploads surfaces as it goes and would otherwise count a surface the
    /// table does not have.
    /// </param>
    internal bool TryGetMainViewDrawHandles(Season.Controls.InstancedMesh3D mesh,
        out string sortedId, out string argsId, out long vertexOffset, out int slotBytes, out int primitiveMask)
    {
        vertexOffset = 0;
        slotBytes = 0;
        primitiveMask = 0;
        if (!_cullMainView || !Season.Rendering.InstanceCull.MainViewCulled
            || !TryGetCullDrawHandles(out sortedId, out argsId, out int capacity))
        {
            sortedId = string.Empty;
            argsId = string.Empty;
            return false;
        }
        if (!ReferenceEquals(Season.Rendering.RenderView.CurrentOrNull, DeviceServices.BaseApp?.MainView))
            return false;
        if (CurrentCullPrimitiveMask(mesh) != _cullPrimMask)
            return false;

        vertexOffset = (long)Season.Rendering.InstanceCull.MainViewSlot * capacity * Season.Rendering.InstanceCull.WorldStride;
        slotBytes = capacity * (int)Season.Rendering.InstanceCull.WorldStride;
        primitiveMask = _cullPrimMask;
        return true;
    }

    /// <summary>The surface mask the parameter table would have if it were built now, with the predicate of <see cref="EnsureCullPrimitiveInfo"/>.</summary>
    int CurrentCullPrimitiveMask(Season.Controls.InstancedMesh3D mesh)
    {
        int surfaces = mesh.Surfaces.Count;
        if (surfaces > 31)
            return -1;
        int mask = 0;
        for (int i = 0; i < surfaces; i++)
        {
            var surface = mesh.Surfaces[i];
            if (surface.Vertices == null || surface.Indices == null || surface.Vertices.Length == 0)
                continue;
            if (surface.Mode == SurfaceBlendMode.Blend)
                continue;
            if (!SurfaceCaches.TryGetValue(surface, out var cache) || !cache.Uploaded)
                continue;
            mask |= 1 << i;
        }
        return mask;
    }

    /// <summary>Called once at the end of each frame's Update: only prepares resources. The matrix-stream upload is deferred to the Record phase,
    /// see <see cref="Season.Rendering.IInstanceCullTarget.PrepareCullInputs"/>.</summary>
    void UpdateCullUpload(Season.Controls.InstancedMesh3D mesh)
    {
        _cullDrawReady = false;
        _cullMainView = mesh.EffectiveDrawPath == Season.Controls.InstanceDrawPath.Indirect;
        if (EnabledInstanceCount <= 0)
            return;

        EnsureCullResources(EnabledInstanceCount, mesh);
    }

    /// <summary>
    /// Prepares cull resources for the instance count. Capacity is aligned to 64, so small instance-count jitter does not rebuild the five buffers every frame;
    /// but the capacity reported through the interface must be the real capacity, or the index table would address by this frame's instance count and misalign with the kernel writes.
    /// </summary>
    void EnsureCullResources(int count, Season.Controls.InstancedMesh3D mesh)
    {
        if (!Season.Rendering.InstanceCull.Enabled)
        {
            ReleaseCullResources();
            return;
        }
        // An instance count dropping to 0 does not mean release: hiding and re-showing would otherwise rebuild the five buffers every frame.
        if (count <= 0)
            return;

        int capacity = (count + 63) / 64 * 64;
        if (_cullCapacity != capacity || _cullSorted == null)
        {
            ReleaseCullResources();
            try
            {
                var g = Season.Basic.Graphics.Instance;
                _cullSource = g.CreateStorageBuffer((uint)capacity * Season.Rendering.InstanceCull.WorldStride);
                _cullSorted = g.CreateStorageBuffer(Season.Rendering.InstanceCull.SortedBytes(capacity),
                    Season.Rendering.StorageBufferUsage.ComputeAndVertexInput);
                _cullVisible = g.CreateStorageBuffer((uint)(Season.Rendering.InstanceCull.MaxSlots * capacity) * 4u);
                _cullArgs = g.CreateStorageBuffer(Season.Rendering.InstanceCull.ArgsBytes,
                    Season.Rendering.StorageBufferUsage.ComputeAndIndirectArgs);
                _cullPrimInfo = g.CreateStorageBuffer(
                    (uint)Season.Rendering.InstanceCull.MaxPrimitives * Season.Rendering.InstanceCull.PrimitiveInfoStride);
            }
            catch (Exception ex)
            {
                DeviceServices.BaseApp?.AddLog(LogType.Error, $"[InstanceCull] Buffer creation failed: {ex.Message}");
            }

            if (_cullSource == null || _cullSorted == null || _cullVisible == null
                || _cullArgs == null || _cullPrimInfo == null)
            {
                ReleaseCullResources();
                return;
            }

            _cullCapacity = capacity;
            _cullPrimitiveCount = -1;
            _cullPrimMask = -1;
        }

        EnsureCullPrimitiveInfo(mesh);
        UpdateCullLocalBounds(mesh);

        if (!_cullRegistered)
        {
            Season.Rendering.InstanceCull.Register(this);
            _cullRegistered = true;
        }
    }

    /// <summary>
    /// Only builds the parameter-table bytes, without touching the GPU - building and uploading have different safe timings: building can happen at any time,
    /// while uploading must wait for a phase outside the Pass (see <see cref="Season.Rendering.IInstanceCullTarget.PrepareCullInputs"/>).
    /// The index ranges in the parameter table must be bit-for-bit aligned with the iteration order of <c>DrawInstancedMesh3DShadow</c>,
    /// so the same predicate is used: opaque + has geometry + uploaded.
    /// </summary>
    void EnsureCullPrimitiveInfo(Season.Controls.InstancedMesh3D mesh)
    {
        if (_cullPrimInfo == null)
            return;

        int surfaces = mesh.Surfaces.Count;
        if (surfaces > 31)
        {
            // One bit per surface is the only way it fits; if it does not fit, explicitly abandon GPU culling for this mesh, rather than taking the first 16 and calling it done.
            AbandonCullPrimitiveInfo();
            return;
        }

        int mask = 0;
        int n = 0;
        for (int i = 0; i < surfaces; i++)
        {
            var surface = mesh.Surfaces[i];
            if (surface.Vertices == null || surface.Indices == null || surface.Vertices.Length == 0)
                continue;
            if (surface.Mode == SurfaceBlendMode.Blend)
                continue;
            if (!SurfaceCaches.TryGetValue(surface, out var cache) || !cache.Uploaded)
                continue;
            if (n >= Season.Rendering.InstanceCull.MaxPrimitives)
            {
                AbandonCullPrimitiveInfo();
                return;
            }

            _cullPrimIndexCounts[n] = surface.Indices.Length;
            mask |= 1 << i;
            n++;
        }

        if (mask == _cullPrimPendingMask && _cullPrimPendingCount == n)
            return;

        if (n <= 0)
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
    }

    /// <summary>
    /// The parameter table is unusable: clear the pending value together with the uploaded value. Clearing only the pending value is not enough -
    /// the uploaded value is still published to the draw side, which would make it fetch index ranges from a stale table.
    /// </summary>
    void AbandonCullPrimitiveInfo()
    {
        _cullPrimPendingBytes = null;
        _cullPrimPendingCount = 0;
        _cullPrimPendingMask = -1;
        _cullPrimitiveCount = 0;
        _cullPrimMask = -1;
    }

    /// <summary>
    /// A7: uploads the cull CPU-side inputs to the GPU. Called back by InstanceCullEffect before each dispatch,
    /// in a phase outside all Passes - WebGPU's writeBuffer is not allowed inside a Pass.
    /// The matrix stream is bit-for-bit identical to the IA instance stream (both are 20 floats = 80B, matching WorldStride), so InstanceBytes is reused.
    /// </summary>
    bool Season.Rendering.IInstanceCullTarget.PrepareCullInputs()
    {
        int count = EnabledInstanceCount;
        if (!Season.Rendering.InstanceCull.Enabled || count <= 0)
            return false;

        var g = Season.Basic.Graphics.Instance;

        // The parameter table follows geometry, not frames; the mask must be compared together with the count, otherwise "one fewer surface, one more surface"
        // would be treated as a no-op because the count is unchanged, and indexCount would be misaligned from then on.
        if (_cullPrimPendingCount != _cullPrimitiveCount || _cullPrimPendingMask != _cullPrimMask)
        {
            if (_cullPrimPendingCount <= 0 || _cullPrimPendingBytes == null || _cullPrimInfo == null)
                return false;
            int primBytes = _cullPrimPendingCount * (int)Season.Rendering.InstanceCull.PrimitiveInfoStride;
            if (!g.TryUpdateStorageBuffer(_cullPrimInfo,
                    new ReadOnlySpan<byte>(_cullPrimPendingBytes, 0, primBytes)))
                return false;
            _cullPrimitiveCount = _cullPrimPendingCount;
            _cullPrimMask = _cullPrimPendingMask;
        }

        // The matrix stream changes every frame; upload only the valid range, with no slack.
        if (_cullSource == null)
            return false;
        int sourceBytes = count * (int)Season.Rendering.InstanceCull.WorldStride;
        if (sourceBytes > InstanceBytes.Length)
            return false;
        return g.TryUpdateStorageBuffer(_cullSource, new ReadOnlySpan<byte>(InstanceBytes, 0, sourceBytes));
    }

    /// <summary>
    /// Group-level merged box: the kernel takes only one box, so merge the vertex local AABBs of all surfaces.
    /// Geometry does not change after loading, so once the first non-degenerate box is obtained it is never recomputed, staying off the per-frame hot path.
    /// </summary>
    void UpdateCullLocalBounds(Season.Controls.InstancedMesh3D mesh)
    {
        if (_cullLocalExtents.LengthSquared() > 0f)
            return;

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var surface in mesh.Surfaces)
        {
            var vertices = surface.Vertices;
            if (vertices == null)
                continue;
            for (int i = 0; i < vertices.Length; i++)
            {
                var p = vertices[i].Position;
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        }

        if (min.X > max.X)
        {
            _cullLocalCenter = Vector3.Zero;
            _cullLocalExtents = Vector3.Zero;
            return;
        }

        _cullLocalCenter = (min + max) * 0.5f;
        _cullLocalExtents = (max - min) * 0.5f;
    }

    /// <summary>
    /// A7: called back by InstanceCullEffect after the three dispatches to determine whether this frame can go indirect.
    /// The Web has no explicit barrier to issue: the command order of the compute pass and render pass plus the queue submission order is the synchronization point,
    /// putting it on the same "submission-model-managed" side as Metal, so only an admission check is done here.
    /// </summary>
    public bool PrepareCullDraw()
    {
        _cullDrawReady = false;
        if (_cullSorted is not WGPUStorageBuffer) return false;
        if (_cullArgs is not WGPUStorageBuffer args) return false;
        if (_cullCapacity <= 0 || _cullPrimitiveCount <= 0) return false;

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
        _cullSorted?.Dispose(); _cullSorted = null;
        _cullArgs?.Dispose(); _cullArgs = null;
        _cullVisible?.Dispose(); _cullVisible = null;
        _cullPrimInfo?.Dispose(); _cullPrimInfo = null;
        _cullCapacity = -1;
        _cullPrimitiveCount = -1;
        _cullPrimMask = -1;
        _cullPrimPendingBytes = null;
        _cullPrimPendingCount = -1;
        _cullPrimPendingMask = -1;
        _cullLocalCenter = Vector3.Zero;
        _cullLocalExtents = Vector3.Zero;
        _cullDrawReady = false;
    }

    /// <summary>On unload, remove the cull target from the shared layer, otherwise the list would keep a reference to a dead JS handle.</summary>
    public void Dispose() => ReleaseCullResources();

    // Writes instance world matrices into a persistent byte buffer
    // (reallocated only when capacity changes), and returns the reused or newly created buffer.
    static byte[] FillInstanceBytes(byte[] buffer, Matrix4x4[] worlds)
    {
        int byteLength = worlds.Length * 20 * sizeof(float);
        if (buffer.Length != byteLength)
            buffer = new byte[byteLength];

        var data = MemoryMarshal.Cast<byte, float>(buffer.AsSpan());
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
            // InstancedMesh3D does not use morph targets; write four zeros here to keep the 80-byte WebGPU instance-stream layout aligned.
            data[offset + 16] = 0f;
            data[offset + 17] = 0f;
            data[offset + 18] = 0f;
            data[offset + 19] = 0f;
        }

        return buffer;
    }
}
