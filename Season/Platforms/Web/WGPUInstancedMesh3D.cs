// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Platforms.Web;

internal sealed class WGPUInstancedMesh3D
{
    public string Name { get; }

    public readonly Dictionary<object, WGPUMesh3D.SurfaceCacheEntry> SurfaceCaches = new();

    /// <summary>Snapshot of resolved five-slot texture names taken during Load, with the same semantics as WGPUMesh3D.ResolvedTextures.</summary>
    public readonly Dictionary<Surface, WGPUMesh3D.ResolvedTextureSet> ResolvedTextures = new();

    public Matrix4x4 View { get; set; } = Matrix4x4.Identity;
    public Matrix4x4 Projection { get; set; } = Matrix4x4.Identity;
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

    public WGPUInstancedMesh3D(string name)
    {
        Name = name;
    }

    public void Update(Season.Controls.InstancedMesh3D mesh, Season.Basic.Camera camera)
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
            View = camera.View;
            Projection = camera.Projection;
            TransformInitialized = true;
            MeshAlpha = mesh.Alpha;
            // Reset Outline2D aggregate state (all instances disabled -> no outline).
            Outline2DActive = false;
            Outline2DHostActive = false;
            Outline2DMaskColor = default;
            Outline2DMaskWidth = 0f;
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
        View = camera.View;
        Projection = camera.Projection;
        TransformInitialized = true;
        MeshAlpha = mesh.Alpha;
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
