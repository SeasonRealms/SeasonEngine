// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// 2-4 contract clauses 4/5: GPU layout of one GI proxy.
/// It is 64 bytes, byte-identical across all four backends, and the full array is uploaded every frame through <c>IGraphics.UpdateStorageBuffer</c>.
/// The first geometry set uses boxes and spheres, per signed-off decision (i), and both are represented by the **same formula** instead of a shader-side branch:
/// <code>
/// q = |p - center| - extents;  d = length(max(q,0)) + min(max(q.x,q.y,q.z),0) - round
/// </code>
/// When extents are all zero, this degenerates to a sphere with radius = round. When round is zero, it becomes a standard AABB.
/// That means no kind enum and no per-proxy branch are needed, saving one select inside a 16.7M-iterations-per-frame inner loop.
///
/// Albedo and emissive are already uploaded with the list in Step 1 but are not consumed yet.
/// The gather kernel currently computes only the distance field; these two fields are reserved for Step 2's ray-hit argmin resolution, per clause 5.
/// The layout is fixed here in one pass so Step 2 does not need to change struct declarations in shaders on all four backends again.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct GiProxy
{
    /// <summary>xyz = world-space center; w is reserved and always 0. It is not used as a validity bit, because the active count comes from Params.proxyCount.</summary>
    public Vector4 CenterPad;

    /// <summary>xyz = world-space half extents of the AABB; w = rounded corner radius or sphere radius, with 0 for box proxies.</summary>
    public Vector4 ExtentsRound;

    /// <summary>rgb = diffuse reflectance, explicitly set by the App per signed-off decision (iii), defaulting to neutral gray; a is reserved.</summary>
    public Vector4 Albedo;

    /// <summary>rgb = emissive radiance, defaulting to all zero; a is reserved.</summary>
    public Vector4 Emissive;
}

/// <summary>
/// 2-4 contract clause 4: shared-layer funnel for the proxy list.
/// It is rebuilt every frame, uploaded, and then consumed by the SDF gather kernel as a per-voxel min reduction.
///
/// Collection flow, driven by <see cref="Effects.DdgiEffect"/>.Record in the AfterScene phase and always outside render passes:
/// <code>
/// BeginCollect(volumeMin, volumeMax)   // Reset counters and remember this frame's volume range
///   → Collect()                        // Recursively walk the BaseApp panel tree, zero-allocation, same shape as Panel.DrawShadow
///   → EndCollect()                     // Clear trailing stale entries so buffer contents match Count exactly
///   → AsBytes()                        // Upload the full 4 KB block
/// </code>
///
/// Four deliberate boundaries in the first version, by design rather than as defects:
/// 1. Hard capacity cap <see cref="MaxProxies"/>=64.
///    Once exceeded, items are truncated in traversal order. No sorting is done, because choosing the nearest 64 would require per-frame sorting or O(n·64) insertion,
///    which conflicts with the design goal of zero per-frame allocation and limited complexity.
///    The overflow count is stored in <see cref="Overflow"/> for App-side inspection, but not logged, because per-frame logging is itself a performance problem.
/// 2. Proxies not intersecting this frame's SDF volume are dropped immediately.
///    The camera-anchored volume is only GiVolumeSize wide, so for distant objects the min distance to voxels inside the volume is always larger than the distance to the volume boundary.
///    The only effect is a slight overestimate on voxels hugging the boundary.
///    This filter is also the main source of capacity budgeting: the 64-item limit is for objects "around the camera", not for objects in the whole scene.
/// 3. Gating reuses <see cref="Control.CastShadows"/> instead of introducing a new switch.
///    Geometry that blocks light should also block indirect light, and this naturally aligns semantics between skyboxes and fullscreen overlays.
///    However, this reuse **depends on the App truly following 1-5 contract clause 7**:
///    "geometry that must never cast projected shadows, such as the skybox, must set false".
///    The Sample once missed this, causing a ±50 m camera-following skybox to swallow the whole volume every frame, which is exactly why boundary 4 exists.
///    Instanced types such as InstancedMesh3D and InstancedModel do not emit proxies in the first version, because expanding per instance would immediately exhaust the 64-item budget.
///    A "pick the nearest subset" strategy is needed first and is deferred.
/// 4. **Any proxy that completely contains the whole volume is dropped immediately**.
///    This is not an optimization, but a correctness fallback.
///    Such a proxy puts every voxel inside the object, making the signed distance field permanently negative.
///    Ray marching then starts inside solid geometry for every path, every probe is classified as buried, and the entire SDF collapses into a constant field with no information.
///    Accidentally marking a skybox or world bounds as CastShadows is a typical source of this case, and the visual symptom,
///    a solid warm debug image that does not change with the camera, is extremely hard to distinguish from "the kernel did not run", so the source is rejected outright.
/// </summary>
public static class GiProxies
{
    /// <summary>Hard cap on the number of proxies. Per contract clauses 4/5, this is both the inner-loop length in gather and the candidate count for argmin resolution.</summary>
    public const int MaxProxies = 64;

    /// <summary>Storage-buffer size in bytes. <see cref="MaxProxies"/> × 64 B = 4 KB, fixed and independent of quality level.</summary>
    public const uint BufferBytes = MaxProxies * 64u;

    static readonly GiProxy[] _items = new GiProxy[MaxProxies];

    /// <summary>Per-slot control name, used only to make <see cref="DumpOnce"/> readable. Writing it is just one reference assignment with no allocation.</summary>
    static readonly string?[] _names = new string?[MaxProxies];

    static Vector3 _volumeMin;
    static Vector3 _volumeMax;

    /// <summary>Number of valid proxies for this frame, uploaded to the kernel as proxyCount.</summary>
    public static int Count { get; private set; }

    /// <summary>Number of proxies dropped this frame because of the capacity limit. 0 means no overflow. Exposed for App-side inspection, see boundary 1 in the class header.</summary>
    public static int Overflow { get; private set; }

    /// <summary>
    /// Debug dump switch.
    /// When set to true, the **next** <see cref="EndCollect"/> prints all proxies of that frame, including index, control name, center, extents, and albedo, then resets itself automatically.
    /// That limits the log burst to at most one frame, since per-frame logging is itself a performance problem, just as in boundary 1's handling of Overflow.
    /// When disabled, the total overhead is one bool check.
    ///
    /// Why this exists:
    /// the index is the successful call order of <see cref="TryAdd"/>, and the control name is only visible at the call site.
    /// The binding between the two can only be observed truthfully here.
    /// If the App walks the control tree separately and tries to reconstruct indices, it would duplicate gating and volume-filter logic,
    /// and any mismatch with this class would make the most trusted column in the log lie.
    /// </summary>
    public static bool DumpOnce;

    /// <summary>Begins collection: resets counters and records the world-space bounds of this frame's SDF volume for intersection filtering.</summary>
    public static void BeginCollect(in Vector3 volumeMin, in Vector3 volumeMax)
    {
        Count = 0;
        Overflow = 0;
        _volumeMin = volumeMin;
        _volumeMax = volumeMax;
    }

    /// <summary>
    /// Recursively walks the panel tree to collect proxies.
    /// The structure is textually isomorphic to <c>Panel.DrawShadow</c>: zero allocation, no sorting, Alpha&gt;0 filtering,
    /// and type/gating decisions performed inside each control override.
    /// </summary>
    public static void Collect()
    {
        var app = DeviceServices.BaseApp;
        if (app != null)
            app.CollectGiProxies();
    }

    /// <summary>Ends collection by clearing stale entries in [Count, MaxProxies), keeping buffer contents strictly consistent with Count.
    /// The debug view verifies the upload path by counting entries whose extents are non-zero, so stale entries would lie.</summary>
    public static void EndCollect()
    {
        if (Count < MaxProxies)
            Array.Clear(_items, Count, MaxProxies - Count);

        if (DumpOnce)
            Dump();
    }

    /// <summary>
    /// Implementation of <see cref="DumpOnce"/> as a one-frame dump.
    /// It is intentionally kept out of EndCollect, because dumping allocates strings; keeping it separate leaves only one bool check on the steady-state path.
    /// Reset happens first so even an early return cannot cause repeated logging.
    /// </summary>
    static void Dump()
    {
        DumpOnce = false;
        var app = DeviceServices.BaseApp;
        if (app == null)
            return;

        app.AddLog(LogType.GI, $"[GiProxies] Count={Count} Overflow={Overflow} " +
            $"volume=({_volumeMin.X:F2},{_volumeMin.Y:F2},{_volumeMin.Z:F2}).." +
            $"({_volumeMax.X:F2},{_volumeMax.Y:F2},{_volumeMax.Z:F2})");
        for (int i = 0; i < Count; i++)
        {
            var c = _items[i].CenterPad;
            var e = _items[i].ExtentsRound;
            var a = _items[i].Albedo;
            var em = _items[i].Emissive;
            app.AddLog(LogType.GI, $"[GiProxies] #{i:D2} {_names[i] ?? "<unnamed>"} " +
                $"c=({c.X:F2},{c.Y:F2},{c.Z:F2}) e=({e.X:F2},{e.Y:F2},{e.Z:F2}) r={e.W:F2} " +
                $"albedo=({a.X:F2},{a.Y:F2},{a.Z:F2}) emissive=({em.X:F2},{em.Y:F2},{em.Z:F2})");
        }
    }

    /// <summary>
    /// Volume filter, corresponding to class-header boundaries 2 and 4.
    /// Returning true means the world-space AABB should be rejected.
    /// The two branches have different reasons:
    /// a non-intersecting object contributes nothing to the voxel-wise min inside the volume, while a fully containing object collapses the whole field into a constantly negative SDF.
    /// </summary>
    static bool Rejected(in Vector3 min, in Vector3 max)
    {
        // Boundary 2: separated on at least one axis.
        if (max.X < _volumeMin.X || min.X > _volumeMax.X ||
            max.Y < _volumeMin.Y || min.Y > _volumeMax.Y ||
            max.Z < _volumeMin.Z || min.Z > _volumeMax.Z)
            return true;

        // Boundary 4: all six faces cross the volume walls, so every voxel lies inside the proxy.
        return min.X <= _volumeMin.X && max.X >= _volumeMax.X &&
               min.Y <= _volumeMin.Y && max.Y >= _volumeMax.Y &&
               min.Z <= _volumeMin.Z && max.Z >= _volumeMax.Z;
    }

    /// <summary>
    /// Appends one box proxy from a world-space AABB.
    /// Returning false means it was dropped by volume filtering or the capacity is already full.
    /// Called by GI-participating controls from their <c>Control.CollectGiProxy</c> override.
    /// <paramref name="debugName"/> is only read by <see cref="DumpOnce"/> and may be omitted.
    /// </summary>
    public static bool TryAdd(in Bounds3D worldBounds, in Vector3 albedo, in Vector3 emissive, string? debugName = null)
    {
        var min = worldBounds.Center - worldBounds.Extents;
        var max = worldBounds.Center + worldBounds.Extents;
        if (Rejected(min, max))
            return false;

        if (Count >= MaxProxies)
        {
            Overflow++;
            return false;
        }

        ref var slot = ref _items[Count];
        slot.CenterPad = new Vector4(worldBounds.Center, 0f);
        slot.ExtentsRound = new Vector4(worldBounds.Extents, 0f);
        slot.Albedo = new Vector4(albedo, 0f);
        slot.Emissive = new Vector4(emissive, 0f);
        _names[Count] = debugName;
        Count++;
        return true;
    }

    /// <summary>
    /// Appends one sphere proxy, encoded as zero extents with the radius stored in w, following the unified formula in <see cref="GiProxy"/>.
    /// The first version has no caller yet because instanced types are not wired up, but this public entry point is fixed in advance as the other half of the formula.
    /// </summary>
    public static bool TryAddSphere(in Vector3 center, float radius, in Vector3 albedo, in Vector3 emissive, string? debugName = null)
    {
        var bounds = new Bounds3D(center, new Vector3(radius));
        var min = center - bounds.Extents;
        var max = center + bounds.Extents;
        if (Rejected(min, max))
            return false;

        if (Count >= MaxProxies)
        {
            Overflow++;
            return false;
        }

        ref var slot = ref _items[Count];
        slot.CenterPad = new Vector4(center, 0f);
        slot.ExtentsRound = new Vector4(0f, 0f, 0f, radius);
        slot.Albedo = new Vector4(albedo, 0f);
        slot.Emissive = new Vector4(emissive, 0f);
        _names[Count] = debugName;
        Count++;
        return true;
    }

    /// <summary>Whole-buffer byte view, always <see cref="BufferBytes"/> bytes long, with zero copy and zero allocation.</summary>
    public static ReadOnlySpan<byte> AsBytes() => MemoryMarshal.AsBytes(_items.AsSpan());
}
