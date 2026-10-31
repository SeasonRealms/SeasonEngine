// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>Projection model of a <see cref="Camera3D"/>.</summary>
public enum CameraProjectionMode
{
    /// <summary>FovY-driven perspective frustum (default, every pre-P3 camera).</summary>
    Perspective,

    /// <summary>Box frustum; the world height it sees is <see cref="Camera3D.OrthographicHeight"/> and the width follows
    /// the aspect ratio. Used by minimaps and other 2D-like plan views.</summary>
    Orthographic,
}

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
    CameraProjectionMode _mode = CameraProjectionMode.Perspective;
    float _orthoHeight = 10f;
    float _near = 0.1f;
    float _far = 100f;
    float _aspect;
    bool _dirty = true;


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

    /// <summary>Projection model. Defaults to <see cref="CameraProjectionMode.Perspective"/>.</summary>
    public CameraProjectionMode Mode
    {
        get => _mode;
        set { if (_mode != value) { _mode = value; _dirty = true; } }
    }

    /// <summary>
    /// World-space height covered vertically when <see cref="Mode"/> is orthographic; the horizontal extent is
    /// <c>OrthographicHeight × aspect</c>. Ignored in perspective mode. Defaults to 10.
    /// </summary>
    public float OrthographicHeight
    {
        get => _orthoHeight;
        set { if (_orthoHeight != value) { _orthoHeight = value; _dirty = true; } }
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

    /// <summary>Bumped by <see cref="ResetTemporal"/>. Every <see cref="RenderView"/> looking through this camera
    /// compares it on Prepare and drops its own history when it changed.</summary>
    public int TemporalResetVersion { get; private set; }

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
        Projection = _mode == CameraProjectionMode.Orthographic
            ? Matrix4x4.CreateOrthographicLeftHanded(_orthoHeight * aspect, _orthoHeight, _near, _far)
            : Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(_fovY, aspect, _near, _far);
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

        return true;
    }

    /// <summary>
    /// 2-3: resets temporal history. Call this after camera teleportation or scene switches to avoid full-screen ghosting from history mismatch.
    /// Temporal state (jitter phase, previous-frame ViewProjection) lives per view in <see cref="RenderView"/>; this bumps
    /// <see cref="TemporalResetVersion"/> so every view looking through this camera treats its next frame as history
    /// and outputs one frame of zero velocity.
    /// </summary>
    public void ResetTemporal() => TemporalResetVersion++;
}
