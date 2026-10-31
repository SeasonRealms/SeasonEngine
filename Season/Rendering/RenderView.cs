// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// Per-view shader constants (256B, byte-for-byte identical across all four backends).
/// The size is exactly one D3D12 CBV / WebGPU dynamic-offset alignment unit, so later multi-view work can lay
/// several views out back to back in one buffer without padding rules. Matrices are kept here in the engine's
/// row-major, non-transposed form; each backend applies its own transpose convention when uploading
/// (<see cref="Transposed"/>), exactly as it already does for the per-object <c>MatrixBuffer</c>.
///
///   offset   0  View
///   offset  64  Projection          (jitter and backend NDC adjustment already baked in; what the VS multiplies by)
///   offset 128  PrevViewProjection  (2-3 clause 6, unjittered, same NDC adjustment; all zero = no history sentinel)
///   offset 192  CameraPos           (xyz = world position, w = view index)
///   offset 208  VelocityParams      (xy = jitter in NDC, zw = 1 / view size; same meaning as SceneLightParams)
///   offset 224  ViewParams0         (x = width, y = height, z = near, w = far)
///   offset 240  Reserved0
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = SizeInBytes)]
public struct ViewConstants
{
    public const int SizeInBytes = 256;

    public Matrix4x4 View;
    public Matrix4x4 Projection;
    public Matrix4x4 PrevViewProjection;
    public Vector4 CameraPos;
    public Vector4 VelocityParams;
    public Vector4 ViewParams0;
    public Vector4 Reserved0;

    /// <summary>Screen-space view used by 2D sprites and text: identity View/Projection, all-zero history
    /// (no velocity), matching the identity matrices those paths used to write into the per-object buffer.</summary>
    public static ViewConstants ScreenSpace => new()
    {
        View = Matrix4x4.Identity,
        Projection = Matrix4x4.Identity,
    };

    /// <summary>Copy with the three matrices transposed, for shader declarations that read column-major data
    /// (HLSL/GLSL/MSL default). The vector fields are unaffected.</summary>
    public readonly ViewConstants Transposed()
    {
        var copy = this;
        copy.View = Matrix4x4.Transpose(View);
        copy.Projection = Matrix4x4.Transpose(Projection);
        copy.PrevViewProjection = Matrix4x4.Transpose(PrevViewProjection);
        return copy;
    }
}

/// <summary>
/// Per-view feature switches. A secondary view (minimap, rear mirror) turns off everything it does not need,
/// and the pass scheduler skips the corresponding work instead of rendering and discarding it. The defaults
/// are <see cref="All"/>, so a view that never touches this property renders exactly like the pre-P3 main view.
/// </summary>
[Flags]
public enum ViewFeature
{
    None = 0,

    /// <summary>Opaque scene geometry. Turning this off leaves the camera's clear color only.</summary>
    Opaque = 1 << 0,

    /// <summary>Transparent scene geometry and the transparent sort pass.</summary>
    Transparent = 1 << 1,

    /// <summary>Receive the frame-wide cascaded shadow atlas. The atlas itself is never skipped per view.</summary>
    Shadows = 1 << 2,

    /// <summary>Volumetric fog injection, filtering and composite.</summary>
    Fog = 1 << 3,

    /// <summary>Weather curtain and rain composite.</summary>
    Weather = 1 << 4,

    /// <summary>GTAO compute chain.</summary>
    AmbientOcclusion = 1 << 5,

    /// <summary>Bloom downsample/upsample chain.</summary>
    Bloom = 1 << 6,

    /// <summary>TAA jitter, history and resolve. Off means the view stays unjittered even under AaMode.Taa.</summary>
    AntiAliasing = 1 << 7,

    /// <summary>Particle submitter hand-off and submission.</summary>
    Particles = 1 << 8,

    /// <summary>The Post slot (tonemap composite). Off means FinalBlit takes the raw scene source.</summary>
    Post = 1 << 9,

    /// <summary>D1 屏幕空间贴花。关闭时本视图不写贴花，也不会为了贴花把场景打散成不透明/透明两段。</summary>
    Decals = 1 << 10,

    All = Opaque | Transparent | Shadows | Fog | Weather | AmbientOcclusion | Bloom | AntiAliasing | Particles | Post | Decals,
}

/// <summary>How a view consumes the frame-wide cascaded shadow atlas.</summary>
public enum ViewShadowPolicy
{
    /// <summary>Sample the atlas rendered for the main view (default; identical to pre-P3 behavior).</summary>
    Shared,

    /// <summary>Bind the 1x1 white fallback instead of the atlas: geometry renders unshadowed. Used by minimap-scale
    /// views, where the shadow texel density of the main view is meaningless anyway.</summary>
    Off,
}

/// <summary>
/// One rendered view of the scene: the camera it looks through plus all per-view derived state.
///
/// <see cref="Camera3D"/> stays a pure description of a lens (pose, FOV, clip planes). Everything that depends on
/// <em>which</em> view is being rendered lives here instead - render size, jitter phase, previous-frame history
/// and the matrices actually handed to the GPU - so two views looking through the same camera (XR eyes) or two
/// cameras rendered in one frame (main view + minimap / rear mirror) never trample each other's TAA history.
///
/// Today the engine renders exactly one view, <see cref="BaseApp.MainView"/>, and every backend drives it through
/// <see cref="Prepare(float, float, in Matrix4x4)"/> once per frame at the point where it used to update the camera.
/// The outcome is bit-for-bit what the old per-backend camera block produced. Additional views are registered through
/// <see cref="FrameSchedule.AddView"/> and rendered by <see cref="FrameSchedule.ExecuteView"/>, each with its own
/// <see cref="ViewTargets"/>.</summary>
public sealed class RenderView
{
    Camera3D _camera;
    int _jitterPhase;
    bool _temporalReady;
    int _resetVersion;

    public RenderView(Camera3D camera, int index = 0)
    {
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _resetVersion = camera.TemporalResetVersion;
        Index = index;
    }

    /// <summary>
    /// The current view, i.e. the one whose matrices the scene is being prepared / drawn with right now.
    /// Falls back to the main view when no view has been prepared yet this session.
    /// Culling, transparent sorting and screen-space effects should read view state through this instead of
    /// going to <see cref="BaseApp.CameraPos"/> directly.
    /// </summary>
    public static RenderView Current => FrameSchedule.CurrentView ?? DeviceServices.BaseApp.MainView;

    /// <summary>Camera this view looks through. Switching cameras drops the temporal history, because the
    /// previous frame of another camera is not a valid reprojection source.</summary>
    public Camera3D Camera
    {
        get => _camera;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(_camera, value)) return;
            _camera = value;
            _resetVersion = value.TemporalResetVersion;
            ResetTemporal();
        }
    }

    /// <summary>View slot index (0 = main view). Written into <see cref="ViewConstants.CameraPos"/>.w.</summary>
    public int Index { get; }

    /// <summary>Target set this view renders into. Attached by <see cref="FrameSchedule.AddView"/>; null means the view
    /// renders into <see cref="FrameSchedule.PrimaryTargets"/> (or has not been registered yet). Used by
    /// <see cref="Prepare(float, float, in Matrix4x4)"/> to read the per-view TAA gate before the frame switches over.</summary>
    public ViewTargets? Targets { get; set; }

    // -- P3 per-view policy. Defaults reproduce the single-view era exactly, so an app only opts out of work. --

    /// <summary>Feature switches for this view. Default <see cref="ViewFeature.All"/>.</summary>
    public ViewFeature Features { get; set; } = ViewFeature.All;

    /// <summary>How this view consumes the frame-wide shadow atlas. Default <see cref="ViewShadowPolicy.Shared"/>.</summary>
    public ViewShadowPolicy ShadowPolicy { get; set; } = ViewShadowPolicy.Shared;

    /// <summary>
    /// Cull layers this view can see: bit n admits controls whose <c>CullLayer</c> is n. Default all ones, which
    /// keeps every existing control (layer 0) visible. Two views can therefore render disjoint subsets of one
    /// scene without touching the scene graph.
    /// </summary>
    public uint LayerMask { get; set; } = uint.MaxValue;

    /// <summary>
    /// Render this view once every N frames (1 = every frame). Intermediate frames keep the previous contents of
    /// the view's targets, which is exactly what a slowly changing minimap wants. The very first frame always renders.
    /// </summary>
    public int UpdateInterval
    {
        get => _updateInterval;
        set => _updateInterval = value < 1 ? 1 : value;
    }
    int _updateInterval = 1;
    ulong _lastRenderedFrame;
    bool _renderedOnce;

    /// <summary>Where this view is displayed, in layout pixels (top-left origin, same space as control PosX/PosY).
    /// Empty (the default) means the whole screen. Used by <see cref="PickRay"/> and by the UI widget that displays
    /// the view's texture; the renderer itself only cares about <see cref="Width"/>/<see cref="Height"/>.</summary>
    public Rect2D ScreenRect { get; set; }

    /// <summary>
    /// Layer mask of the view being executed right now, read by control-level culling. Set by
    /// <see cref="FrameSchedule.ExecuteView"/> and restored to <see cref="uint.MaxValue"/> outside a view, so draw
    /// code never has to know which view it runs under.
    /// </summary>
    public static uint CurrentLayerMask { get; internal set; } = uint.MaxValue;

    /// <summary>True when the view currently being executed admits <paramref name="layer"/> (0..31).</summary>
    public static bool IsLayerVisible(uint layer) => (CurrentLayerMask & (1u << (int)(layer & 31u))) != 0;

    /// <summary>True when this view has every flag of <paramref name="feature"/>.</summary>
    public bool HasFeature(ViewFeature feature) => (Features & feature) == feature;

    /// <summary>
    /// Whether this view renders on <paramref name="frameIndex"/>. Mutates the pacing state, so it must be called
    /// once per frame per view (the scheduler does that).
    /// </summary>
    public bool ShouldRender(ulong frameIndex)
    {
        if (UpdateInterval <= 1) return true;
        if (!_renderedOnce)
        {
            _renderedOnce = true;
            _lastRenderedFrame = frameIndex;
            return true;
        }
        if (frameIndex - _lastRenderedFrame < (ulong)UpdateInterval) return false;
        _lastRenderedFrame = frameIndex;
        return true;
    }

    /// <summary>Render size in pixels used for the last <see cref="Prepare(float, float, in Matrix4x4)"/>.</summary>
    public float Width { get; private set; }
    public float Height { get; private set; }

    /// <summary>Aspect ratio used for the last Prepare.</summary>
    public float Aspect { get; private set; }

    /// <summary>World-space camera position and normalized view direction for the last Prepare.
    /// Forward falls back to +Z when Target coincides with Position.</summary>
    public Vector3 Position { get; private set; }
    public Vector3 Forward { get; private set; } = Vector3.UnitZ;

    /// <summary>Unjittered View / Projection / ViewProjection of this view (no backend NDC adjustment).
    /// Used by culling and CSM; never contaminated by jitter.</summary>
    public Matrix4x4 View { get; private set; } = Matrix4x4.Identity;
    public Matrix4x4 Projection { get; private set; } = Matrix4x4.Identity;
    public Matrix4x4 ViewProjection { get; private set; } = Matrix4x4.Identity;

    /// <summary>Frustum of the unjittered ViewProjection, for control-level and instance-level culling.</summary>
    public Frustum Frustum;

    /// <summary>2-3 clause 4: projection with this frame's subpixel jitter baked in (no backend NDC adjustment).
    /// Equals <see cref="Projection"/> whenever jitter is inactive.</summary>
    public Matrix4x4 ProjectionJittered { get; private set; } = Matrix4x4.Identity;

    /// <summary>2-3 clause 6: previous-frame unjittered ViewProjection of this view (no backend NDC adjustment).
    /// All zero is the global "no history" sentinel and must never be replaced by Identity.</summary>
    public Matrix4x4 PrevViewProjection { get; private set; }

    /// <summary>Projection actually used by the vertex shader: jittered projection × backend NDC transform.</summary>
    public Matrix4x4 RenderProjection { get; private set; } = Matrix4x4.Identity;

    /// <summary>Unjittered ViewProjection × backend NDC transform: the matrix matching the pixels on screen.
    /// Screen↔world conversion (picking, debug overlays) and the lighting snapshot use this one, because the
    /// jittered <see cref="RenderProjection"/> would make reprojection depend on the frame's subpixel phase.
    /// Equals <see cref="ViewProjection"/> on backends without an NDC adjustment.</summary>
    public Matrix4x4 RenderViewProjection { get; private set; } = Matrix4x4.Identity;

    /// <summary>History matrix actually used by the vertex shader: PrevViewProjection × backend NDC transform,
    /// or all zero when there is no history.</summary>
    public Matrix4x4 RenderPrevViewProjection { get; private set; }

    /// <summary>Inverse of the backend NDC adjustment handed to the last <see cref="Prepare(float, float, in Matrix4x4)"/>.
    /// <see cref="RenderViewProjection"/> bakes the adjustment in, so screen-space coordinates have to be mapped back
    /// through this before unprojecting. Identity on every backend without a DPI compensation.</summary>
    public Matrix4x4 NdcTransformInverse { get; private set; } = Matrix4x4.Identity;

    /// <summary>2-3: this frame's jitter in NDC units, already scaled by JitterScale.</summary>
    public Vector2 JitterNdc { get; private set; }

    /// <summary>2-3: this frame's jitter in pixels, within ±0.5×JitterScale. Debugging and logging only.</summary>
    public Vector2 JitterPixels { get; private set; }

    /// <summary>2-3 clause 6 value of SceneLightParams.VelocityParams for this view:
    /// xy = jitter in NDC, zw = 1 / render size.</summary>
    public Vector4 VelocityParams => new(
        JitterNdc.X, JitterNdc.Y,
        Width > 0 ? 1f / Width : 0f,
        Height > 0 ? 1f / Height : 0f);

    /// <summary>Row-major constants for this view, rebuilt by every Prepare.</summary>
    public ViewConstants Constants { get; private set; } = ViewConstants.ScreenSpace;

    /// <summary>Whether Prepare has run at least once.</summary>
    public bool Prepared { get; private set; }

    /// <summary>Prepare with no backend NDC adjustment (Vulkan / Metal / WebGPU).</summary>
    public void Prepare(float width, float height) => Prepare(width, height, Matrix4x4.Identity);

    /// <summary>
    /// Advances this view by one frame. Replaces the per-backend "UpdateTemporal / UpdateIfChanged" camera block.
    ///
    /// Order is strict (2-3 clauses 4/6): snapshot the previous unjittered ViewProjection, rebuild the camera
    /// matrices, advance the Halton(2,3) phase and bake the subpixel offset into the jittered projection only.
    /// <paramref name="ndcTransform"/> is a backend post-projection affine adjustment (the D3D12 desktop DPI
    /// compensation); it applies to the current and history render matrices alike, so velocity stays aligned,
    /// but never to the unjittered matrices used for culling and shadow cascades.
    /// </summary>
    public void Prepare(float width, float height, in Matrix4x4 ndcTransform)
    {
        var camera = _camera;
        if (camera.TemporalResetVersion != _resetVersion)
        {
            _resetVersion = camera.TemporalResetVersion;
            ResetTemporal();
        }

        Width = width;
        Height = height;
        float aspect = width / height;
        var previous = ViewProjection;

        // Kept for screen-space mapping (picking): RenderViewProjection bakes this in, so unprojection needs the way back.
        NdcTransformInverse = Matrix4x4.Invert(ndcTransform, out var ndcInverse) ? ndcInverse : Matrix4x4.Identity;

        camera.UpdateIfChanged(aspect);
        Aspect = camera.Aspect;
        View = camera.View;
        Projection = camera.Projection;
        ViewProjection = camera.ViewProjection;
        Frustum = camera.Frustum;
        Position = camera.Position;
        var forward = camera.Target - camera.Position;
        Forward = forward.LengthSquared() < 1e-6f ? Vector3.UnitZ : Vector3.Normalize(forward);

        if (RenderQuality.Current.MotionVectors)
        {
            // First frame (or after a reset): history = current frame, so velocity is naturally zero instead of
            // a full-screen smear. Never Identity - its fourth column would pass the shader's history test.
            PrevViewProjection = _temporalReady ? previous : ViewProjection;
            _temporalReady = true;

            int phaseCount = RenderQuality.Current.JitterPhaseCount;
            if (phaseCount < 1) phaseCount = 1;
            _jitterPhase = (_jitterPhase + 1) % phaseCount;

            // 2-3 clause 14: jitter and resolve live or die together. Pure MotionVectors mode and TAA bypass
            // frames stay unjittered; TaaActive lags by one frame, which is harmless.
            // P3: a view that switched AntiAliasing off is not resolved at all, so it must not be jittered either.
            float scale = RenderQuality.Current.JitterScale;
            if (RenderQuality.Current.EffectiveAntiAliasing != AaMode.Taa || !HasFeature(ViewFeature.AntiAliasing)
                || !(Targets?.TaaActive ?? FrameSchedule.TaaActive))
                scale = 0f;

            if (scale != 0f && width > 0f && height > 0f)
            {
                float jx = (Halton(_jitterPhase + 1, 2) - 0.5f) * scale;
                float jy = (Halton(_jitterPhase + 1, 3) - 0.5f) * scale;
                JitterPixels = new Vector2(jx, jy);
                // One pixel spans 2/size in NDC; NDC Y points up, opposite to pixel Y.
                JitterNdc = new Vector2(jx * 2f / width, -jy * 2f / height);
            }
            else
            {
                JitterPixels = Vector2.Zero;
                JitterNdc = Vector2.Zero;
            }

            // Row-vector convention (pos·M): the translation terms sit in row 3, i.e. a clip-space shift of jitter×w.
            var jittered = Projection;
            jittered.M31 += JitterNdc.X;
            jittered.M32 += JitterNdc.Y;
            ProjectionJittered = jittered;

            RenderProjection = Matrix4x4.Multiply(ProjectionJittered, ndcTransform);
            RenderPrevViewProjection = Matrix4x4.Multiply(PrevViewProjection, ndcTransform);
            RenderViewProjection = Matrix4x4.Multiply(ViewProjection, ndcTransform);
        }
        else
        {
            // All zero = no history, so nothing stale survives if the feature is switched off mid-run.
            _temporalReady = false;
            JitterPixels = Vector2.Zero;
            JitterNdc = Vector2.Zero;
            ProjectionJittered = Projection;
            PrevViewProjection = default;
            RenderProjection = Matrix4x4.Multiply(Projection, ndcTransform);
            RenderPrevViewProjection = default;
            RenderViewProjection = Matrix4x4.Multiply(ViewProjection, ndcTransform);
        }

        Constants = new ViewConstants
        {
            View = View,
            Projection = RenderProjection,
            PrevViewProjection = RenderPrevViewProjection,
            CameraPos = new Vector4(Position, Index),
            VelocityParams = VelocityParams,
            ViewParams0 = new Vector4(width, height, camera.Near, camera.Far),
        };
        Prepared = true;
    }

    /// <summary>
    /// Distance of <paramref name="worldPoint"/> along this view's forward axis, used as the transparent sort key.
    /// Reads the live camera pose rather than the Prepare snapshot: draws are collected after the app update of the
    /// same frame, and sorting has always tracked the camera as moved by that update.
    /// </summary>
    public float ViewDepth(Vector3 worldPoint)
    {
        var position = _camera.Position;
        var forward = _camera.Target - position;
        forward = forward.LengthSquared() < 1e-6f ? Vector3.UnitZ : Vector3.Normalize(forward);
        return Vector3.Dot(worldPoint - position, forward);
    }

    /// <summary>Transparent sort key of <paramref name="worldPoint"/> in the current view (world Z when no app exists).</summary>
    public static float SortDepth(Vector3 worldPoint)
        => DeviceServices.BaseApp is null ? worldPoint.Z : Current.ViewDepth(worldPoint);

    /// <summary>
    /// View-local point to world ray. <paramref name="u"/>/<paramref name="v"/> are 0..1 across this view's render
    /// target (0,0 = top-left). This is the picking entry for a secondary view: the pointer has to be mapped into
    /// the view rectangle first, and unlike the full-screen path nothing is divided by the composition scale because
    /// the fraction is already scale-free.
    /// </summary>
    public bool PickRay(float u, float v, out Vector3 origin, out Vector3 direction)
        => Picking.ViewPointToRay(u, v, this, out origin, out direction);

    /// <summary>World point → view-local 0..1 coordinates (inverse of <see cref="PickRay"/>). False behind the camera.</summary>
    public bool ProjectToViewFraction(Vector3 worldPoint, out float u, out float v)
        => Picking.ProjectToViewFraction(worldPoint, this, out u, out v);

    /// <summary>
    /// 2-3: drops temporal history. The next Prepare treats the current frame as history (one frame of zero
    /// velocity). <see cref="Camera3D.ResetTemporal"/> reaches every view looking through that camera.
    /// </summary>
    public void ResetTemporal()
    {
        _temporalReady = false;
        _jitterPhase = 0;
        JitterNdc = Vector2.Zero;
        JitterPixels = Vector2.Zero;
        PrevViewProjection = default;
        RenderPrevViewProjection = default;
        ProjectionJittered = Projection;
    }

    /// <summary>Index-th Halton low-discrepancy sample (index starts at 1, radix is a prime base).</summary>
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
