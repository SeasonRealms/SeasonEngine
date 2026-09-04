// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// 1-5: CPU-side matrix computation for CSM cascades and spot shadow maps, shared across all four backends.
/// See RenderQuality 1-5 clause 9 for the contract.
///
/// Responsibilities: called every frame by the backend frame loop after the camera is updated:
///   BeginFrame() → ComputeSun(camera, sunDir) when directional light is enabled → ComputeSpot(light) when slot 0 is a spot
///   → Apply(ref sceneLights) to write matrices, splits, and enable flags into the lighting UBO mirror.
/// During shadow-pass rendering, each atlas quadrant calls <see cref="BeginSlot"/> to get that slot's view×projection
/// as a replacement for the main-pass camera matrix, while the world matrix remains unchanged, and <see cref="EndPass"/> is called after the slot loop ends.
/// BeginSlot also publishes <see cref="ActiveFrustum"/> so shared-layer controls can cull shadow casters according to contract clause 7.
/// The culling frustum and uploaded matrix must originate from the same call, so reading CascadeViewProj directly instead of going through BeginSlot is forbidden.
///
/// Algorithm notes (zero allocations per frame, no virtual dispatch):
/// - Practical split: lambda-blended log/uniform partitioning over [Near, min(Far, ShadowDistance)];
/// - For each cascade, orthographic projection is built from the bounding sphere of the 8 corners of the frustum slice
///   (a sphere is invariant to camera rotation, so shadow-map coverage stays stable and does not shimmer under rotation);
/// - Texel snapping: align light-space translation to the texel grid, with tile resolution = atlas/2, to remove shimmer from camera translation;
/// - Move the light-space eye back by radius + zPad so caster geometry outside the slice but on the light side, such as room walls, still fits into the depth range;
/// - Matrix conventions stay aligned with the engine: LH + [0,1] depth + row vectors via the System.Numerics Create*LeftHanded family.
///
/// Atlas quadrant convention (contract clause 2): slot i uses tile origin = ((i%2)·half, (i/2)·half), where half = atlas/2.
/// Slots 0..2 are cascades and slot 3 is the spotlight. The shader maps light-space UV into the corresponding quadrant using the same convention.
/// </summary>
public static class CascadedShadow
{
    /// <summary>Maximum number of CSM cascades. In the four atlas quadrants, three belong to cascades and the fourth belongs to the spotlight.</summary>
    public const int MaxCascades = 3;

    /// <summary>Atlas slot used by the spotlight shadow map.</summary>
    public const int SpotSlot = 3;

    /// <summary>Near clip plane for the spotlight shadow map. This is a perspective projection, so setting it too small harms depth precision.</summary>
    const float SpotNear = 0.05f;

    /// <summary>Fallback far clip when spotlight range&lt;=0, matching the KHR "infinite range" semantic.</summary>
    const float SpotFarFallback = 20f;

    /// <summary>Active cascade light-space ViewProj matrices for the current frame. Preallocated; only the first <see cref="ActiveCascadeCount"/> entries are valid.</summary>
    public static readonly Matrix4x4[] CascadeViewProj = new Matrix4x4[MaxCascades];

    /// <summary>View-space far bounds of the current-frame cascade splits. x/y/z are the far bounds of cascades 0/1/2, and w is the shadow maximum distance.</summary>
    public static Vector4 CascadeSplits;

    /// <summary>Number of active cascades for the current frame, valid after ComputeSun.</summary>
    public static int ActiveCascadeCount;

    /// <summary>Whether directional-light shadows are active this frame. Cleared in BeginFrame and set by ComputeSun.</summary>
    public static bool SunActive;

    /// <summary>Spotlight light-space ViewProj for the current frame, valid after ComputeSpot.</summary>
    public static Matrix4x4 SpotViewProj = Matrix4x4.Identity;

    /// <summary>Whether spotlight shadows are active this frame. Cleared in BeginFrame and set by ComputeSpot.</summary>
    public static bool SpotActive;

    /// <summary>
    /// Generation counter for the shadow pass. It is incremented monotonically by <see cref="BeginFrame"/> and stays constant within a frame.
    /// Backends use it to cache "the caster primitive list for this pass": within one shadow pass, the same list is replayed for all four atlas quadrants
    /// (3 cascades + 1 spotlight), with only the viewport and light-space ViewProj changing per slot.
    /// The primitive set itself is unchanged, so CollectPrimitives does not need to run per quadrant.
    /// Monotonicity is the only contract. Extra increments only cause an extra collection pass and affect performance only, not correctness.
    /// </summary>
    public static int Epoch;

    /// <summary>Total number of atlas quadrants: 3 cascades + 1 spotlight, per contract clause 2.</summary>
    public const int SlotCount = MaxCascades + 1;

    /// <summary>
    /// Light-space culling frustum for the current quadrant of this pass, written by <see cref="BeginSlot"/> and read-only during DrawShadow traversal.
    /// It is derived from the exact ViewProj uploaded for that quadrant; see <see cref="BeginSlot"/> for the no-drift guarantee.
    /// </summary>
    public static Frustum ActiveFrustum;

    /// <summary>
    /// Current active slot. -1 means execution is outside the shadow pass, in which case culling is always disabled to avoid false rejection.
    /// Reset both by <see cref="BeginFrame"/> and <see cref="EndPass"/>.
    /// </summary>
    public static int ActiveSlot = -1;

    /// <summary>Whether culling is active for the current frame and slot, gated by the global switch and whether execution is inside a shadow-pass slot.</summary>
    public static bool CullingActive
        => ActiveSlot >= 0 && RenderQuality.Current.ShadowCulling;

    /// <summary>Per-slot group-level submitted/culled counters used for diagnostics. Cleared at the start of each frame; index = slot.</summary>
    static readonly int[] _submitted = new int[SlotCount];
    static readonly int[] _culled = new int[SlotCount];

    /// <summary>Print interval in frames for diagnostic summaries. Logging every frame would flood the log, and these group-level counts are stable enough that per-frame output is unnecessary.</summary>
    const int DiagFrameInterval = 60;

    /// <summary>
    /// Clears activation flags at the beginning of the frame, so any slot not recomputed this frame has no shadows, and advances <see cref="Epoch"/>.
    /// Also resets slot state and culling counters, after first reporting the previous frame.
    /// </summary>
    public static void BeginFrame()
    {
        ReportCulling();

        Epoch++;
        SunActive = false;
        SpotActive = false;

        ActiveSlot = -1;
        Array.Clear(_submitted);
        Array.Clear(_culled);
    }

    /// <summary>
    /// Enters one atlas quadrant, as defined by contract clause 7: publishes the culling frustum derived from this slot's light-space ViewProj,
    /// and returns that same matrix for backend upload.
    ///
    /// This method is the **only** derivation point for both the culling volume and the rendering matrix.
    /// The backend receives the exact matrix whose planes were extracted, so the two cannot drift structurally.
    /// Constructing another culling volume elsewhere, for example by recomputing a bounding sphere, is forbidden.
    /// Doing so would make CPU culling and GPU clipping disagree, typically showing up as shadow flicker or popping as the camera moves.
    /// </summary>
    public static Matrix4x4 BeginSlot(int slot)
    {
        var vp = slot == SpotSlot ? SpotViewProj : CascadeViewProj[slot];
        ActiveSlot = slot;
        Frustum.FromViewProjection(in vp, out ActiveFrustum);
        return vp;
    }

    /// <summary>Leaves the shadow pass. Called after the quadrant loop ends; <see cref="BeginFrame"/> also provides a fallback reset.</summary>
    public static void EndPass() => ActiveSlot = -1;

    /// <summary>
    /// Slot-level shadow-caster culling test using control-level world-space AABBs, per contract clause 7.
    ///
    /// Correctness: the depth image of slot c is the rasterization result of geometry transformed by <see cref="CascadeViewProj"/>[c].
    /// Triangles outside clip-space [-w,w]×[-w,w]×[0,w] are clipped by the GPU and cannot write into that tile.
    /// If the AABB does not intersect the frustum, then none of the object's triangles, which are contained by that AABB, can be inside it.
    /// Skipping submission therefore leaves atlas contents bit-identical. This is not a quality tradeoff: A/B results must match pixel for pixel.
    ///
    /// The usual concern that "objects outside the slice but on the light side can still cast shadows into it" does not apply here.
    /// <see cref="ComputeSun"/> already moves the box back by zPad along the light direction; any caster farther on the light side has already been clipped by the orthographic near plane for the current setup,
    /// so its shadow is already absent today and this culling introduces no new loss.
    ///
    /// Empty boxes with extents=0, typically while resources are still loading, do not participate in culling, following 1-3 clause 6 to avoid false rejection.
    /// </summary>
    public static bool IsCulled(in Bounds3D worldBounds)
    {
        if (!CullingActive || worldBounds.Extents == Vector3.Zero)
            return false;

        return Register(!ActiveFrustum.Intersects(in worldBounds));
    }

    /// <summary>
    /// Records one group-level culling decision and returns it. This is the only entry point for diagnostic counting.
    /// Used by callers that already evaluated against <see cref="ActiveFrustum"/> themselves,
    /// such as InstancedMesh3DBase doing per-instance sphere rejection, where the test must iterate instances and cannot be moved into this class.
    /// </summary>
    public static bool Register(bool culled)
    {
        if (!CullingActive)
            return false;

        if (culled)
            _culled[ActiveSlot]++;
        else
            _submitted[ActiveSlot]++;

        return culled;
    }

    /// <summary>
    /// Every <see cref="DiagFrameInterval"/> frames, reports previous-frame per-slot group-level submitted/culled counts
    /// in the form x/y = culled/total tested. Nothing is printed if no tests occurred, either because shadows were disabled or because the scene had no casters.
    /// </summary>
    static void ReportCulling()
    {
        if (Epoch % DiagFrameInterval != 0)
            return;

        int culled = 0, total = 0;
        for (int i = 0; i < SlotCount; i++)
        {
            culled += _culled[i];
            total += _culled[i] + _submitted[i];
        }

        if (total == 0)
            return;

        DeviceServices.BaseApp?.AddLog(LogType.Backend,
            $"{DateTime.UtcNow} [ShadowCull] " +
            $"c0 {_culled[0]}/{_culled[0] + _submitted[0]}  " +
            $"c1 {_culled[1]}/{_culled[1] + _submitted[1]}  " +
            $"c2 {_culled[2]}/{_culled[2] + _submitted[2]}  " +
            $"spot {_culled[SpotSlot]}/{_culled[SpotSlot] + _submitted[SpotSlot]}  " +
            $"total {culled}/{total} ({(total > 0 ? culled * 100 / total : 0)}% of group submissions skipped)");
    }

    /// <summary>
    /// Computes the directional-light CSM cascade matrices. The camera must already have completed UpdateIfChanged for this frame so aspect and frustum parameters are ready.
    /// sunDir is the world-space propagation direction pointing toward the lit surface, matching the semantics of directional-light DirType.xyz, and is normalized internally.
    /// </summary>
    public static void ComputeSun(Camera3D camera, Vector3 sunDir)
    {
        if (sunDir.LengthSquared() < 1e-12f)
            return;
        sunDir = Vector3.Normalize(sunDir);

        int count = Math.Clamp(RenderQuality.Current.ShadowCascadeCount, 2, MaxCascades);
        float near = camera.Near;
        float far = MathF.Max(MathF.Min(camera.Far, RenderQuality.Current.ShadowDistance), near + 1e-3f);
        float lambda = Math.Clamp(RenderQuality.Current.CascadeSplitLambda, 0f, 1f);

        // Practical split: blend logarithmic and uniform partitioning by lambda, per contract clause 9.
        Span<float> splits = stackalloc float[MaxCascades];
        for (int i = 0; i < count; i++)
        {
            float p = (i + 1) / (float)count;
            float logSplit = near * MathF.Pow(far / near, p);
            float uniSplit = near + (far - near) * p;
            splits[i] = lambda * logSplit + (1f - lambda) * uniSplit;
        }

        // Camera basis for LH space: right = up × forward.
        var forward = camera.Target - camera.Position;
        if (forward.LengthSquared() < 1e-12f)
            forward = Vector3.UnitZ;
        forward = Vector3.Normalize(forward);
        var right = Vector3.Cross(camera.Up, forward);
        if (right.LengthSquared() < 1e-12f)
            right = Vector3.UnitX;
        right = Vector3.Normalize(right);
        var up = Vector3.Cross(forward, right);

        float tanHalfFov = MathF.Tan(camera.FovY * 0.5f);
        float aspect = camera.Aspect > 0f ? camera.Aspect : 1f;
        float tileRes = RenderQuality.Current.ShadowAtlasSize * 0.5f;
        var lightUp = MathF.Abs(sunDir.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;

        Span<Vector3> corners = stackalloc Vector3[8];
        float sliceNear = near;
        for (int c = 0; c < count; c++)
        {
            float sliceFar = splits[c];

            // The 8 corners of the current frustum slice.
            int k = 0;
            for (int e = 0; e < 2; e++)
            {
                float d = e == 0 ? sliceNear : sliceFar;
                float halfH = d * tanHalfFov;
                float halfW = halfH * aspect;
                var center = camera.Position + forward * d;
                corners[k++] = center - right * halfW - up * halfH;
                corners[k++] = center + right * halfW - up * halfH;
                corners[k++] = center - right * halfW + up * halfH;
                corners[k++] = center + right * halfW + up * halfH;
            }

            // Bounding sphere: centroid + max distance. Stable under rotation, with radius quantization further suppressing tiny FOV/aspect jitter.
            var sphereCenter = Vector3.Zero;
            for (int i = 0; i < 8; i++)
                sphereCenter += corners[i];
            sphereCenter *= 1f / 8f;

            float radius = 0f;
            for (int i = 0; i < 8; i++)
                radius = MathF.Max(radius, Vector3.Distance(corners[i], sphereCenter));
            radius = MathF.Ceiling(radius * 16f) / 16f;

            // Light-space view: move the eye back along -sunDir by radius + zPad so light-side casters outside the slice still fit in range.
            float zPad = radius;
            var eye = sphereCenter - sunDir * (radius + zPad);
            var view = Matrix4x4.CreateLookAtLeftHanded(eye, sphereCenter, lightUp);

            // Texel snapping: align the translation terms of the view matrix, M41/M42, which are the light-space XY offsets, to the texel grid per contract clause 9.
            float texelSize = radius * 2f / tileRes;
            view.M41 = MathF.Floor(view.M41 / texelSize) * texelSize;
            view.M42 = MathF.Floor(view.M42 / texelSize) * texelSize;

            var proj = Matrix4x4.CreateOrthographicLeftHanded(radius * 2f, radius * 2f, 0f, (radius + zPad) * 2f);
            CascadeViewProj[c] = view * proj;

            sliceNear = sliceFar;
        }

        CascadeSplits = new Vector4(
            splits[0],
            count > 1 ? splits[1] : far,
            count > 2 ? splits[2] : far,
            far);
        ActiveCascadeCount = count;
        SunActive = true;
    }

    /// <summary>
    /// Computes the spotlight shadow-map matrix. Contract clause 8 states that it only applies to the light designated by Params0.W and only when that light is a spot; the caller is responsible for that check.
    /// fov = 2×outerConeAngle to fully cover the cone, and far = range, or <see cref="SpotFarFallback"/> when range&lt;=0.
    /// </summary>
    public static void ComputeSpot(in GpuLight spot)
    {
        var dir = new Vector3(spot.DirType.X, spot.DirType.Y, spot.DirType.Z);
        if (dir.LengthSquared() < 1e-12f)
            return;
        dir = Vector3.Normalize(dir);

        var pos = new Vector3(spot.PosRange.X, spot.PosRange.Y, spot.PosRange.Z);
        float range = spot.PosRange.W > 0f ? spot.PosRange.W : SpotFarFallback;

        // Convert cosOuter to the full cone angle. Clamp to avoid acos domain errors and projection degeneration when the cone gets too wide and approaches a hemisphere.
        float cosOuter = Math.Clamp(spot.SpotParams.Y, 0.05f, 0.995f);
        float fovY = MathF.Min(2f * MathF.Acos(cosOuter), MathF.PI * 0.9f);

        var lightUp = MathF.Abs(dir.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        var view = Matrix4x4.CreateLookAtLeftHanded(pos, pos + dir, lightUp);
        var proj = Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(fovY, 1f, SpotNear, range);

        SpotViewProj = view * proj;
        SpotActive = true;
    }

    /// <summary>
    /// Writes this frame's shadow results into the lighting UBO mirror. This is the single write entry point for shadow fields under contract clause 1.
    /// When ShadowsEnabled=false or no light is active this frame, zeros are written so shader-side ShadowParams all become zero and shadows are fully disabled.
    /// </summary>
    public static void Apply(ref SceneLightParams scene)
    {
        if (!RenderQuality.Current.ShadowsEnabled || (!SunActive && !SpotActive))
        {
            scene.ShadowParams0 = default;
            scene.ShadowParams1 = default;
            return;
        }

        if (SunActive)
        {
            for (int i = 0; i < ActiveCascadeCount; i++)
                scene.CascadeViewProj[i] = CascadeViewProj[i];
            scene.CascadeSplits = CascadeSplits;
        }

        if (SpotActive)
            scene.SpotShadowViewProj = SpotViewProj;

        scene.ShadowParams0 = new Vector4(
            SunActive ? 1f : 0f,
            SunActive ? ActiveCascadeCount : 0f,
            1f / RenderQuality.Current.ShadowAtlasSize,
            0f);
        scene.ShadowParams1 = new Vector4(
            SpotActive ? 1f : 0f,
            Math.Clamp(RenderQuality.Current.ShadowStrength, 0f, 1f),
            0f,
            0f);
    }

    /// <summary>
    /// Maps an atlas slot to its quadrant viewport in pixels. Contract clause 2 defines origin = ((slot%2)·half, (slot/2)·half).
    /// Called by backend shadow-pass rendering per quadrant, together with the controlled SetViewport path from contract clause 6.
    /// </summary>
    public static void GetAtlasViewport(int slot, out int x, out int y, out int size)
    {
        size = RenderQuality.Current.ShadowAtlasSize / 2;
        x = (slot % 2) * size;
        y = (slot / 2) * size;
    }
}
