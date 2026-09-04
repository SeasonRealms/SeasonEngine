// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// 1-3: Formal camera object shared across all four backends, replacing per-backend hardcoded FOV/near/far values and the raw BaseApp field pair.
///
/// Responsibilities: Position/Target/FovY/Near/Far/aspect → View/Projection/ViewProjection/Frustum.
/// Everything is gated by Changed semantics: property setters only mark dirty when the value actually changes,
/// and UpdateIfChanged rebuilds only when dirty or when aspect changes, so matrix construction and frustum extraction happen at most once per frame, with zero cost for a static camera.
///
/// Matrix conventions remain exactly aligned with the rest of the engine (LH + [0,1] depth + row vectors):
///   View = CreateLookAtLeftHanded(Position, Target, Up)
///   Projection = CreatePerspectiveFieldOfViewLeftHanded(FovY, aspect, Near, Far)
/// Defaults match the original hardcoded values on all four backends (FovY=π/4, Near=0.1, Far=100, Position/Target=0),
/// so visuals remain unchanged before and after the wiring. BaseApp.CameraPos/CameraTarget are forwarded to this object, requiring zero changes to legacy call sites.
/// </summary>
public sealed class Camera3D
{
    Vector3 _position;
    Vector3 _target;
    Vector3 _up = Vector3.UnitY;
    float _fovY = MathF.PI / 4f;
    float _near = 0.1f;
    float _far = 100f;
    float _aspect;
    bool _dirty = true;

    // ── 2-3 temporal state (contract clauses 4/6): jitter phase and previous-frame ViewProjection ──
    int _jitterPhase;
    bool _temporalReady;

    /// <summary>Camera position in world space. Defaults to Zero, matching the old BaseApp.CameraPos field.</summary>
    public Vector3 Position
    {
        get => _position;
        set { if (_position != value) { _position = value; _dirty = true; } }
    }

    /// <summary>Look-at target in world space. Defaults to Zero.</summary>
    public Vector3 Target
    {
        get => _target;
        set { if (_target != value) { _target = value; _dirty = true; } }
    }

    /// <summary>Up direction. Defaults to +Y.</summary>
    public Vector3 Up
    {
        get => _up;
        set { if (_up != value) { _up = value; _dirty = true; } }
    }

    /// <summary>Vertical field of view in radians. Defaults to π/4, matching the original hardcoded value on all four backends.</summary>
    public float FovY
    {
        get => _fovY;
        set { if (_fovY != value) { _fovY = value; _dirty = true; } }
    }

    /// <summary>Near clip plane. Defaults to 0.1.</summary>
    public float Near
    {
        get => _near;
        set { if (_near != value) { _near = value; _dirty = true; } }
    }

    /// <summary>Far clip plane. Defaults to 100.</summary>
    public float Far
    {
        get => _far;
        set { if (_far != value) { _far = value; _dirty = true; } }
    }

    /// <summary>Current aspect ratio, valid after UpdateIfChanged. Used by 1-5 CascadedShadow when rebuilding frustum slices.</summary>
    public float Aspect => _aspect;

    /// <summary>View matrix, valid after UpdateIfChanged.</summary>
    public Matrix4x4 View { get; private set; } = Matrix4x4.Identity;

    /// <summary>Projection matrix, valid after UpdateIfChanged.</summary>
    public Matrix4x4 Projection { get; private set; } = Matrix4x4.Identity;

    /// <summary>View×Projection in row-vector order, valid after UpdateIfChanged.</summary>
    public Matrix4x4 ViewProjection { get; private set; } = Matrix4x4.Identity;

    /// <summary>
    /// View×Projection actually used for rendering, specifically for picking and screen-space unprojection:
    /// this applies desktop DPI compensation on top of <see cref="ViewProjection"/> using n=1/CompositionScale.X,
    /// scaled around the NDC top-left corner, consistent with the 2D "layout coordinates ÷ scale" behavior.
    /// Culling and CSM cascades still use uncompensated <see cref="ViewProjection"/>.
    /// Picking must use this matrix; otherwise under high DPI (n&lt;1), rays and rendered content shift together because the rendered image shrinks into the top-left corner.
    /// On platforms where CompositionScale=1 (VK/MTL/Web/Android), the compensation degenerates to identity, so the value matches across all backends.
    /// </summary>
    public Matrix4x4 RenderViewProjection { get; private set; } = Matrix4x4.Identity;

    /// <summary>Current frustum, valid after UpdateIfChanged, used for control-level and instance-level culling tests.</summary>
    public Frustum Frustum;

    /// <summary>2-3 contract clause 4: projection matrix with subpixel jitter baked in, actually used for rendering.
    /// When UpdateTemporal is not used or JitterScale=0, it stays identical to <see cref="Projection"/>, leaving no residue when the feature is off.</summary>
    public Matrix4x4 ProjectionJittered { get; private set; } = Matrix4x4.Identity;

    /// <summary>2-3 contract clause 6: previous-frame View×Projection, without jitter, used by the PS to compute prevNdc.
    /// On the first frame, or after ResetTemporal, it equals the current-frame ViewProjection so velocity naturally becomes 0.
    /// The all-zero initial value is the global "no history" sentinel used across the system: if read before UpdateTemporal runs,
    /// the shader-side "column 4 is all zero" test outputs zero velocity. It must never be set to Identity,
    /// because Identity has column 4 = (0,0,0,1) and would be mistaken for valid history, causing prevClip to be computed from identity.</summary>
    public Matrix4x4 PrevViewProjection { get; private set; }

    /// <summary>2-3: current-frame jitter in NDC units, already scaled by JitterScale. Used for de-jittering on the shader side via VelocityParams.xy.</summary>
    public Vector2 JitterNdc { get; private set; }

    /// <summary>2-3: current-frame jitter in pixel units, within ±0.5×JitterScale. Used for debugging and logging.</summary>
    public Vector2 JitterPixels { get; private set; }

    /// <summary>
    /// Rebuilds View/Projection/ViewProjection/Frustum when parameters or aspect change.
    /// Returns true if a rebuild happened this frame, so the caller can decide whether camera UBOs and related data need to be rewritten.
    /// When Target==Position, the view direction falls back to +Z to avoid a LookAt singularity.
    /// </summary>
    public bool UpdateIfChanged(float aspect)
    {
        if (!_dirty && aspect == _aspect)
            return false;

        _aspect = aspect;
        _dirty = false;

        var target = _target;
        if ((target - _position).LengthSquared() < 1e-12f)
            target = _position + Vector3.UnitZ;

        View = Matrix4x4.CreateLookAtLeftHanded(_position, target, _up);
        Projection = Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(_fovY, aspect, _near, _far);
        ViewProjection = View * Projection;
        Frustum.FromViewProjection(ViewProjection, out Frustum);

        // Picking matrix: apply desktop DPI compensation on top of the shared rendering matrix path (n=1/CompositionScale.X,
        // scaled around the NDC top-left corner). If CompositionScale is unavailable or equals 1, this collapses to identity consistently across all backends.
        float compositionScale = DeviceServices.BaseApp?.CompositionScale.X ?? 0f;
        float n = compositionScale > 1e-4f ? 1f / compositionScale : 1f;
        var dpiTransform = Matrix4x4.CreateScale(n, n, 1f);
        dpiTransform.M41 = n - 1f;
        dpiTransform.M42 = 1f - n;
        RenderViewProjection = ViewProjection * dpiTransform;

        // 2-3: outside temporal modes, ProjectionJittered remains identical to Projection with no residual state;
        // temporal modes overwrite it later in UpdateTemporal.
        ProjectionJittered = Projection;

        return true;
    }

    /// <summary>
    /// 2-3 contract clauses 4/6: advances one temporal frame. Called once per frame in MotionVectors mode, replacing UpdateIfChanged.
    ///
    /// The order is strict: snapshot the previous-frame non-jittered ViewProjection first, then call UpdateIfChanged to rebuild the current-frame matrices,
    /// then advance the Halton(2,3) phase and bake the subpixel offset into ProjectionJittered.
    /// Jitter only goes into ProjectionJittered and never contaminates Projection/ViewProjection/Frustum, so culling and shadow cascades always use non-jittered matrices,
    /// avoiding culling or cascade flicker at the edges.
    ///
    /// When screenWidth or screenHeight is 0, jitter falls back to 0 because the size is unknown and should not be guessed.
    /// Clause 14: jitter also falls back to 0 when anti-aliasing is not AaMode.Taa, or when FrameSchedule.TaaActive is false because the resolve path did not take effect this frame.
    /// Returns the same rebuild flag as UpdateIfChanged.
    /// </summary>
    public bool UpdateTemporal(float aspect, float screenWidth, float screenHeight)
    {
        // The previous-frame snapshot must be taken before rebuilding, and it must use the non-jittered ViewProjection.
        // On the first frame, initialize it to the all-zero no-history sentinel here; it is overwritten below with the current frame and must never be set to Identity.
        PrevViewProjection = _temporalReady ? ViewProjection : default;

        bool rebuilt = UpdateIfChanged(aspect);

        if (!_temporalReady)
        {
            // First frame: history = current frame, so velocity naturally becomes 0 and avoids full-screen ghosting.
            PrevViewProjection = ViewProjection;
            _temporalReady = true;
        }

        int phaseCount = RenderQuality.Current.JitterPhaseCount;
        if (phaseCount < 1) phaseCount = 1;
        _jitterPhase = (_jitterPhase + 1) % phaseCount;

        float scale = RenderQuality.Current.JitterScale;

        // 2-3 contract clause 14: jitter and resolve live or die together.
        // Jitter without accumulation only creates frame-to-frame image shaking, which is worse than no jitter,
        // so pure MotionVectors mode (used by cases like 2-4 SDFGI that only need velocity), and TAA bypass periods caused by size mismatch, stay non-jittered.
        // TaaActive is set by TaaEffect.Record at the end of the previous frame, so first-frame and bypass-recovery cases each lag by one frame, which is harmless.
        if (RenderQuality.Current.AntiAliasing != AaMode.Taa || !FrameSchedule.TaaActive)
            scale = 0f;

        if (scale != 0f && screenWidth > 0f && screenHeight > 0f)
        {
            // Center Halton(2,3) into [-0.5, 0.5], then scale it to pixel offsets.
            float jx = (Halton(_jitterPhase + 1, 2) - 0.5f) * scale;
            float jy = (Halton(_jitterPhase + 1, 3) - 0.5f) * scale;
            JitterPixels = new Vector2(jx, jy);

            // Pixels → NDC: one pixel spans 2/screen in NDC. The NDC Y axis points upward, opposite to pixel-space Y.
            JitterNdc = new Vector2(jx * 2f / screenWidth, -jy * 2f / screenHeight);
        }
        else
        {
            JitterPixels = Vector2.Zero;
            JitterNdc = Vector2.Zero;
        }

        // Under the row-vector convention (pos·M), the translation terms sit in row 3, equivalent to translating by jitter×w in clip space.
        var jittered = Projection;
        jittered.M31 += JitterNdc.X;
        jittered.M32 += JitterNdc.Y;
        ProjectionJittered = jittered;

        return rebuilt;
    }

    /// <summary>
    /// 2-3: resets temporal history. Call this after camera teleportation or scene switches to avoid full-screen ghosting from history mismatch.
    /// The next UpdateTemporal call will set history to the current frame and force one frame of zero velocity.
    /// </summary>
    public void ResetTemporal()
    {
        _temporalReady = false;
        _jitterPhase = 0;
        JitterNdc = Vector2.Zero;
        JitterPixels = Vector2.Zero;
        // All-zero = no-history sentinel. The next UpdateTemporal call will replace it with the current-frame ViewProjection because _temporalReady==false.
        // It must not be set to Identity, whose non-zero fourth column would be treated by the shader as valid history.
        PrevViewProjection = default;
        ProjectionJittered = Projection;
    }

    /// <summary>Returns the index-th Halton low-discrepancy sample, with index starting at 1 and radix being a prime base.</summary>
    static float Halton(int index, int radix)
    {
        float result = 0f;
        float fraction = 1f / radix;
        while (index > 0)
        {
            result += (index % radix) * fraction;
            index /= radix;
            fraction /= radix;
        }
        return result;
    }
}
