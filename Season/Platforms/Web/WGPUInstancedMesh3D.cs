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

    // === A7 逐实例 GPU 剔除 ===
    // Web 这一端没有"group"对象，剔除目标只能挂在网格实例宿主上；C# 侧只持有 StorageBuffer 的 JS 句柄，
    // 真正的 GPUBuffer 在 JS 的 _storageBuffers[Id] 里。与另外三端的一处结构差异是本类的实例流
    // 每帧由 C# 逐字节下发，但上传时机仍与另外三端一致：都延后到 Record 相位，
    // 由 InstanceCullEffect 在 Pass 之外回调 PrepareCullInputs（WebGPU 的 writeBuffer 同样不允许在 Pass 内调用）。

    Season.Rendering.StorageBuffer? _cullSource;
    Season.Rendering.StorageBuffer? _cullSorted;
    Season.Rendering.StorageBuffer? _cullArgs;
    Season.Rendering.StorageBuffer? _cullVisible;
    Season.Rendering.StorageBuffer? _cullPrimInfo;
    int _cullCapacity = -1;
    /// <summary>已上传到 GPU 的参数表条数。与 <see cref="_cullPrimPendingCount"/> 不等时说明本帧要重传。</summary>
    int _cullPrimitiveCount = -1;
    /// <summary>CPU 侧已构造好的参数表字节：构造随时可做，上传要等 Pass 之外的相位
    ///（见 <see cref="Season.Rendering.IInstanceCullTarget.PrepareCullInputs"/>）。</summary>
    byte[]? _cullPrimPendingBytes;
    /// <summary>CPU 侧参数表的条数，-1 表示还没构造过。</summary>
    int _cullPrimPendingCount = -1;
    bool _cullRegistered;
    Vector3 _cullLocalCenter;
    Vector3 _cullLocalExtents;
    bool _cullDrawReady;

    /// <summary>
    /// 参数表签名：一位一个 surface，取与 <c>DrawInstancedMesh3DShadow</c> 完全相同的谓词（非透明 + 有几何 + 已上传）。
    /// 用位掩码而不是计数是因为只比个数会把"少一个 surface、多一个 surface"当成没变，
    /// 而 indexCount 一旦错位画出来就是错的。
    /// </summary>
    int _cullPrimMask = -1;
    /// <summary>待上传的掩码。两者分开是因为 <see cref="_cullPrimMask"/> 是与绘制端对齐的已上传状态，
    /// 而这张表可能已经因为几何变化被重建：先写好待传值，上传成功才让绘制端看见。</summary>
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

        // A7：剔除输入与 IA 实例流逐位相同（都是 20 floats = 80B，与 InstanceCull.WorldStride 一致），
        // 所以直接复用 InstanceBytes，不多存一份副本。
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
    // A7 逐实例 GPU 剔除
    // ============================================================

    int Season.Rendering.IInstanceCullTarget.CullInstanceCount => EnabledInstanceCount;

    int Season.Rendering.IInstanceCullTarget.CullCapacity => _cullCapacity;

    /// <summary>参数表尚未与绘制端对齐时回落为 0，于是共享层早退、本网格退回 CPU 整批剔除，
    /// 而不是拿错位的索引区间去画。</summary>
    int Season.Rendering.IInstanceCullTarget.CullPrimitiveCount => _cullPrimitiveCount;

    Season.Rendering.StorageBuffer? Season.Rendering.IInstanceCullTarget.CullSourceWorlds => _cullSource;

    Season.Rendering.StorageBuffer? Season.Rendering.IInstanceCullTarget.CullSortedWorlds => _cullSorted;

    Season.Rendering.StorageBuffer? Season.Rendering.IInstanceCullTarget.CullPrimitiveInfo => _cullPrimInfo;

    Season.Rendering.StorageBuffer? Season.Rendering.IInstanceCullTarget.CullArgs => _cullArgs;

    Season.Rendering.StorageBuffer? Season.Rendering.IInstanceCullTarget.CullVisible => _cullVisible;

    Vector3 Season.Rendering.IInstanceCullTarget.CullLocalCenter => _cullLocalCenter;

    Vector3 Season.Rendering.IInstanceCullTarget.CullLocalExtents => _cullLocalExtents;

    /// <summary>本帧能否直接走间接阴影绘制：容量、句柄、参数表三项任一不就绪就为 false。</summary>
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

    /// <summary>每帧 Update 末尾调用一次：只备资源。矩阵流上传延后到 Record 相位，
    /// 见 <see cref="Season.Rendering.IInstanceCullTarget.PrepareCullInputs"/>。</summary>
    void UpdateCullUpload(Season.Controls.InstancedMesh3D mesh)
    {
        _cullDrawReady = false;
        if (EnabledInstanceCount <= 0)
            return;

        EnsureCullResources(EnabledInstanceCount, mesh);
    }

    /// <summary>
    /// 按实例数准备剔除资源。容量按 64 对齐，于是实例数小幅抖动不会每帧重建五个缓冲；
    /// 但接口上报的容量必须是真实容量，否则序号表会按本帧实例数寻址而与内核写入错位。
    /// </summary>
    void EnsureCullResources(int count, Season.Controls.InstancedMesh3D mesh)
    {
        if (!Season.Rendering.InstanceCull.Enabled)
        {
            ReleaseCullResources();
            return;
        }
        // 实例数掉到 0 不等于要释放：隐藏再显示会因此每帧重建五个缓冲。
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
                DeviceServices.BaseApp?.AddLog(LogType.Error, $"[InstanceCull] 缓冲创建失败：{ex.Message}");
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
    /// 只构造参数表字节，不碰 GPU —— 构造与上传的安全时机不同：构造随时可做，
    /// 上传要等 Pass 之外（见 <see cref="Season.Rendering.IInstanceCullTarget.PrepareCullInputs"/>）。
    /// 参数表的索引区间必须与 <c>DrawInstancedMesh3DShadow</c> 的迭代顺序逐位对齐，
    /// 所以谓词取完全相同的一份：非透明 + 有几何 + 已上传。
    /// </summary>
    void EnsureCullPrimitiveInfo(Season.Controls.InstancedMesh3D mesh)
    {
        if (_cullPrimInfo == null)
            return;

        int surfaces = mesh.Surfaces.Count;
        if (surfaces > 31)
        {
            // 一位一个 surface 才装得下；装不下就明确放弃本网格的 GPU 剔除，而不是取前 16 个就当完事。
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
    /// 参数表用不了：待传值与已上传值一起清掉。只清待传值是不够的 ——
    /// 已上传值仍在发布给绘制端，它会让绘制端按一张过期的表去取索引区间。
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
    /// A7：把剔除的 CPU 侧输入传到 GPU。由 InstanceCullEffect 在每次派发之前回调，
    /// 那个相位在所有 Pass 之外 —— WebGPU 的 writeBuffer 不允许在 Pass 内调用。
    /// 矩阵流与 IA 实例流逐位相同（都是 20 floats = 80B，与 WorldStride 一致），所以复用 InstanceBytes。
    /// </summary>
    bool Season.Rendering.IInstanceCullTarget.PrepareCullInputs()
    {
        int count = EnabledInstanceCount;
        if (!Season.Rendering.InstanceCull.Enabled || count <= 0)
            return false;

        var g = Season.Basic.Graphics.Instance;

        // 参数表跟着几何走而不跟帧走；掩码要和条数一起比，否则"少一个 surface、多一个 surface"
        // 会因条数不变而被当成没事发生，indexCount 从此整体错位。
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

        // 矩阵流每帧都变，只传有效段，不留余量。
        if (_cullSource == null)
            return false;
        int sourceBytes = count * (int)Season.Rendering.InstanceCull.WorldStride;
        if (sourceBytes > InstanceBytes.Length)
            return false;
        return g.TryUpdateStorageBuffer(_cullSource, new ReadOnlySpan<byte>(InstanceBytes, 0, sourceBytes));
    }

    /// <summary>
    /// 组级合并盒：内核只收一个盒，所以把全部 surface 的顶点局部 AABB 并起来。
    /// 几何在加载后不变，所以在拿到首个非退化盒之后就不再重算，不进每帧热路径。
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
    /// A7：在三次派发之后由 InstanceCullEffect 回调，核定本帧能否走间接绘制。
    /// Web 没有显式屏障可发：compute pass 与 render pass 的命令次序加 queue 提交顺序就是同步点，
    /// 与 Metal 同属“提交模型代管”的一侧，所以这里只做准入校验。
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

    /// <summary>卸载时把剔除目标从共享层撤掉，否则列表里会留下指向已死 JS 句柄的引用。</summary>
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
