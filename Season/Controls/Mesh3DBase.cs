// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Controls;

/// <summary>
/// Shared base class for single-instance 3D geometry controls, namely Mesh3D and Model.
/// It extracts the common framework that is structurally identical between the two controls:
/// - Unified positioning model: anchor at the geometric center of the raw bounding box,
///   plus per-axis Width/Height/Depth scaling, plus BuildWorldMatrix with the anchor as the rotation pivot.
/// - 1-3: LocalBounds and control-level AABB frustum culling, with zero-box exemption and zero allocations.
/// - 1-5: DrawShadow is gated by CastShadows plus per-cascade light-space culling.
///   Per contract clause 7, it does not reuse camera-culling results and instead tests the actual light-space box
///   submitted for the current cascade, so atlas contents remain bit-identical.
/// - 2-4: CollectGiProxy outputs one box proxy under signed decision (i), with gating mirroring DrawShadow
///   and an additional requirement that the bounds be non-zero.
/// - Unified Draw gating: Ready/has content/Enable/Alpha != 0 -> frustum culling -> backend DrawCore.
///   Per contract clause 3, culling only skips this control's own submission.
/// - Transparent sorting: implements ITransparentSortable. Instanced controls in v1 do not participate in transparent sorting,
///   so the interface is only implemented at this layer.
/// Subclasses inject only the varying pieces through abstract members: rotation representation via GetRotationMatrix,
/// the content predicate via HasContent, the sort anchor, and the per-backend Graphics dispatch points
/// via DrawCore/DrawShadowCore/DisposeCore.
/// </summary>
public abstract class Mesh3DBase : Control, ITransparentSortable
{
    /// <summary>
    /// 1-3: Control-level local AABB, filled once during loading per contract clause 2.
    /// Recomputing it every frame by scanning vertices is forbidden.
    /// Mesh3D builds it by aggregating Surface vertices; Model fills it from the GltfAsset loading path after RH-to-LH conversion.
    /// </summary>
    public Bounds3D LocalBounds { get; internal set; }

    /// <summary>
    /// Click-selection state, driven uniformly by ObjectPicker.
    /// A successful click hit sets it to true and locks it. Even if the pointer leaves the control,
    /// the projected screen-space bounds keep showing until a click outside the bounds produces no hit,
    /// which clears it back to false. The app side may read it directly, and writing false forcibly clears the lock.
    /// </summary>
    //public bool Selected { get; set; }

    /// <summary>
    /// Unified highlight configuration for composite objects, see <see cref="Highlight"/>.
    /// The Bounds box and Wireframe shell share one set of style/on-off/two-color/outline-width settings.
    /// Highlight primitives are created lazily and pooled by the DX backend, independent from this control's Alpha chain.
    /// Face alpha comes from SurfaceColor.W and may pulse frame by frame without affecting the model's overall Alpha.
    /// It can be enabled, disabled, recolored, or have its outline width changed at runtime.
    /// When highlighted by selection, colors follow this configuration. ObjectPicker only overrides the pulsing value
    /// in SurfaceColor.W and does not touch RGB.
    /// Nested object-initializer assignment is supported:
    /// Highlight = { Style = ..., SurfaceColor = ... }。
    /// </summary>
    public Highlight Highlight { get; set; } = new();

    /// <summary>
    /// Unified positioning model: raw local bounding box with no conservative animation expansion applied,
    /// already converted from RH to LH.
    /// LocalBounds, used for 1-3 culling, may be expanded by AnimatedBoundsScale for animated models.
    /// Anchor and scaling calculations always use this raw box, avoiding inflated default sizes on animated models.
    /// It is filled alongside LocalBounds during loading. The setter triggers <see cref="OnBoundsEstablished"/>,
    /// so it must be assigned after prerequisite data such as Size and OriginalScale is ready.
    /// </summary>
    Bounds3D _localBoundsRaw;
    public Bounds3D LocalBoundsRaw
    {
        get => _localBoundsRaw;
        internal set
        {
            _localBoundsRaw = value;
            OnBoundsEstablished();
        }
    }

    /// <summary>Unified positioning model: model-space local size before scaling, equal to the full size of the raw bounding box and used for per-axis scale computation.</summary>
    public Vector3 LocalSize => _localBoundsRaw.Extents * 2f;

    /// <summary>
    /// Unified positioning model: the local-space anchor is the geometric center of the raw bounding box, A = Center.
    /// Since 2026-08 this replaced the former "top-left near-screen" corner (Min.X, Max.Y, Min.Z),
    /// because corner anchoring caused practical issues such as biased rotation pivots and awkward mental placement.
    /// The new center anchor matches mainstream 3D software.
    /// (PosX, PosY, PosZ) is the world position of that anchor, so the bounding-box center lands at Pos,
    /// the rotation pivot is naturally centered, and Pos stays constant during rotation.
    /// </summary>
    public Vector3 AnchorLocal => _localBoundsRaw.Center;

    /// <summary>
    /// Unified positioning model: per-axis scale = target size / local size, see <see cref="AxisScale"/>.
    /// Degenerate axes stay fixed at 1 when the local dimension is effectively zero, or so small relative to the largest axis
    /// that it is considered exported "zero-thickness" noise.
    /// </summary>
    public Vector3 ComputedScale
    {
        get
        {
            var local = LocalSize;
            float maxLocal = MathF.Max(local.X, MathF.Max(local.Y, local.Z));
            return new Vector3(AxisScale(Width ?? 0, local.X, maxLocal), AxisScale(Height ?? 0, local.Y, maxLocal), AxisScale(Depth ?? 0, local.Z, maxLocal));
        }
    }

    /// <summary>
    /// Guard for one-axis scaling: if the local dimension is effectively zero in absolute terms (&lt; 1e-6),
    /// or so small relative to the largest local dimension that it reaches the zero-thickness range
    /// (&lt; maxLocal x 1e-4, that is, more than four orders of magnitude smaller than the main body,
    /// typically floating-point thickness noise from exported planar assets), this axis stays fixed at 1.
    /// Allowing such axes through can produce hyperbolic scales around 1e5, amplifying sub-millimeter vertex noise
    /// into meter-scale height differences and tearing a plane into broken offset fragments.
    /// This was observed in `warcraft_3_style_pbr_grass.glb` in 2026-08.
    /// </summary>
    internal static float AxisScale(float target, float local, float maxLocal)
        => local > 1e-6f && local > maxLocal * 1e-4f ? target / local : 1f;

    /// <summary>Rotation injection point without translation: Mesh3D uses a quaternion; Model uses CreateRotationY(Rotation) around the world Y axis.</summary>
    protected abstract Matrix4x4 GetRotationMatrix();

    /// <summary>
    /// Unified positioning model for the world matrix, mapping model space to world space:
    /// CreateTranslation(−AnchorLocal) × CreateScale(ComputedScale) × Rotation × CreateTranslation(PosX,PosY,PosZ)。
    /// Under the row-vector convention (p * M), point evaluation becomes ((p - A) * S) * R + Pos.
    /// The anchor A, which is the geometric center of the bounding box, maps exactly to (PosX, PosY, PosZ),
    /// and the rotation pivot is fixed at the anchor. With a center anchor, rotation therefore happens around
    /// the bounding-box center while Pos remains constant.
    /// Note that S must come after the -A translation, meaning subtract the anchor before scaling.
    /// If Scale were applied first, the evaluation would degrade to (p * S - A) * R + Pos,
    /// the anchor would no longer land at Pos, and the world-space center would shift by A * (1 - S),
    /// causing picking, bounds, and screen presentation to drift apart. This was fixed in 2026-08.
    /// Since Phase 3, matrix computation across all backends converges to this method.
    /// </summary>
    public Matrix4x4 BuildWorldMatrix()
    {
        return Matrix4x4.CreateTranslation(-AnchorLocal)
             * Matrix4x4.CreateScale(ComputedScale)
             * GetRotationMatrix()
             * Matrix4x4.CreateTranslation(PosX, PosY, PosZ);
    }

    /// <summary>
    /// World-space anchor offset: the world displacement from the local origin to the anchor,
    /// which is the geometric center of the bounding box, computed as A with ComputedScale applied
    /// and then rotated, without translation.
    /// Identity:
    /// p*S*R + t == ((p-A)*S)*R + Pos  <=>  t = Pos - AnchorWorldOffset.
    /// So when position needs to be expressed in terms of the local origin, for example pinning the model origin
    /// to a world-space point, write Pos = target + AnchorWorldOffset. Recomputing it each frame preserves the identity
    /// as rotation and scaling evolve. This identity holds for any anchor definition, and pin-style placement code
    /// needs no changes under the center anchor.
    /// </summary>
    public Vector3 AnchorWorldOffset => Vector3.TransformNormal(AnchorLocal * ComputedScale, GetRotationMatrix());

    /// <summary>Normalization factor applied when settling default size: Mesh3D = 1 so local size is used as-is; Model = OriginalScale to preserve the normalized loaded appearance.</summary>
    protected virtual float DefaultSizeFactor => 1f;

    /// <summary>
    /// Called when local bounds have been established, either after Mesh3D.Load finishes aggregation
    /// or after Model loading backfills them.
    /// Any unset dimension, null or 0, is settled to local size x <see cref="DefaultSizeFactor"/>,
    /// preserving the loaded appearance expected by legacy behavior, where ComputedScale matches the old default scaling.
    /// Note that Width/Height/Depth are float?. Null means "unset", and lifted comparisons make null == 0 false,
    /// so null must be handled explicitly. Otherwise controls with unset size would get a zero ComputedScale and become invisible.
    /// This was observed in MorphStressTest bee cases in 2026-08.
    /// </summary>
    internal virtual void OnBoundsEstablished()
    {
        var local = LocalSize;

        if (Width is null or 0f) Width = local.X * DefaultSizeFactor;
        if (Height is null or 0f) Height = local.Y * DefaultSizeFactor;
        if (Depth is null or 0f) Depth = local.Z * DefaultSizeFactor;
    }

    /// <summary>
    /// 1-3: Current-frame world bounds, computed as LocalBounds transformed by <see cref="BuildWorldMatrix"/>
    /// using the |M| method with zero allocations.
    /// Since Phase 3, rendering, culling, shadowing, and GI proxies across all backends share this matrix
    /// as a single source of truth.
    /// Note that for animated models, LocalBounds is a conservative culling box equal to Raw x AnimatedBoundsScale.
    /// When bounds need to match the rendered body closely, for example picking or selected-box drawing,
    /// use <see cref="GetWorldBoundsRaw"/>.
    /// </summary>
    public virtual Bounds3D GetWorldBounds()
    {
        return LocalBounds.Transform(BuildWorldMatrix());
    }

    /// <summary>
    /// Current-frame world bounds based on the raw box, computed as LocalBoundsRaw transformed by <see cref="BuildWorldMatrix"/>.
    /// This matches the rendered body exactly. LocalBounds multiplies animated models by AnimatedBoundsScale (1.5x)
    /// for conservative culling and shadowing, so using it directly to draw a selected box would make the box 50% larger
    /// than the rendered body, leaving 0.25 body heights of empty space above and below, as observed on Robot in 2026-08.
    /// Picking via TryPick and any box-fitting logic should therefore use this method. For non-animated controls,
    /// the raw and conservative boxes are the same.
    /// </summary>
    public Bounds3D GetWorldBoundsRaw()
    {
        return LocalBoundsRaw.Transform(BuildWorldMatrix());
    }

    /// <summary>
    /// Picking test: whether a world-space ray hits this control's bounding box, with OBB precision.
    /// The ray is transformed to local space through Invert(<see cref="BuildWorldMatrix"/>) and then slab-tested
    /// against the min/max of LocalBoundsRaw. This is equivalent to testing the world-space OBB and matches the
    /// screen projection exactly under rotation and non-uniform scaling, making it stricter than a coarse world AABB test.
    /// Picking uses the raw box instead of LocalBounds because the latter includes AnimatedBoundsScale (1.5x)
    /// for conservative animation culling and shadowing, which would cause false hits outside the rendered silhouette.
    /// Returns false when not Ready, not Enable, or the box is empty because it has not loaded yet.
    /// If the camera is inside the box (tMin &lt; 0 <= tMax), it still counts as a hit with distance = 0.
    /// distance is a world-space distance and is therefore comparable across controls.
    /// </summary>
    public bool TryPick(Vector3 rayOrigin, Vector3 rayDirection, out float distance)
    {
        distance = 0f;

        if (!Ready || !Enable || LocalBoundsRaw.Extents == Vector3.Zero)
            return false;

        return Picking.RayIntersectsObb(rayOrigin, rayDirection, BuildWorldMatrix(), LocalBoundsRaw, out distance);
    }

    /// <summary>
    /// Entry point for surface-accurate picking at v2 mesh granularity.
    /// Derived classes override this with ray-triangle narrow-phase tests when triangle data is available,
    /// typically broad-phase OBB culling plus per-triangle narrow phase.
    /// By default it falls back to <see cref="TryPick"/> with OBB precision.
    /// The semantic and return-value contract matches TryPick: world-space distance, comparability across controls,
    /// and nearest-hit selection.
    /// </summary>
    public virtual bool TryPickSurface(Vector3 rayOrigin, Vector3 rayDirection, out float distance)
        => TryPick(rayOrigin, rayDirection, out distance);

    /// <summary>
    /// Broad-phase gate for surface picking: ray versus the control-level conservative box, that is LocalBounds,
    /// which uses AnimatedBoundsScale 1.5x on animated models to avoid missing animated frames and equals LocalBoundsRaw
    /// on non-animated controls, tested as an OBB slab in world space.
    /// Returns false when not Ready, not Enable, or the box is empty. The narrow-phase result determines the final distance,
    /// so the distance from this method can be ignored.
    /// </summary>
    protected bool TryPickBroadPhase(Vector3 rayOrigin, Vector3 rayDirection, in Matrix4x4 world)
    {
        if (!Ready || !Enable || LocalBounds.Extents == Vector3.Zero)
            return false;

        return Picking.RayIntersectsObb(rayOrigin, rayDirection, world, LocalBounds, out _);
    }

    /// <summary>Content predicate: whether geometry or model resources have been declared. Mesh3D requires non-empty Surfaces; Model requires a non-empty Name.</summary>
    protected abstract bool HasContent { get; }

    public abstract System.Numerics.Vector3 TransparentSortPosition { get; }

    public abstract bool EnableTransparentSort { get; }

    /// <summary>Per-platform shadow-pass projection dispatch point, after CastShadows gating and per-cascade culling have already been handled by the base DrawShadow.</summary>
    protected abstract void DrawShadowCore();

    public override bool Draw()
    {
        var result = false;

        if (base.Draw())
        {
            if (!Ready || !HasContent || !Enable || Alpha == 0f)
            {
                
            }
            else if (IsFrustumCulled())
            {
                // 1-3: Control-level frustum culling hit. Skip only this control's draw submission;
                // the pass chain remains unaffected, per contract clause 3.
            }
            else
            {
                result = true;
            }
        }

        return result;
    }

    /// <summary>
    /// 1-5: Shadow-pass projection under contract clause 7, gated by CastShadows plus per-cascade light-space culling.
    ///
    /// Culling tests the actual light-space box submitted for the current cascade,
    /// uniquely derived from CascadedShadow.BeginSlot.
    /// Geometry outside that box would already be clipped by the GPU and would not write into that tile,
    /// so skipping submission keeps atlas contents bit-identical. This is not a quality trade-off:
    /// screenshots with ShadowCulling on or off must match pixel for pixel.
    /// It uses GetWorldBounds, not GetWorldBoundsRaw, because animated models need the conservative
    /// AnimatedBoundsScale-expanded bounds here. The culling box must err on the large side,
    /// consistent with main-pass camera culling.
    /// </summary>
    public override void DrawShadow()
    {
        if (!CastShadows || !Ready || !HasContent || !Enable)
            return;

        if (CullingEnabled && CascadedShadow.IsCulled(GetWorldBounds()))
            return;

        DrawShadowCore();
    }

    /// <summary>
    /// 2-4: Emits one box proxy under signed decision (i), meaning one world AABB for the whole object.
    /// Gating mirrors <see cref="DrawShadow"/> by reusing CastShadows, see boundary 3 in the GiProxies class header.
    /// It additionally requires non-zero bounds. A control that has not finished loading still has zero LocalBounds,
    /// and uploading that would effectively insert a point-like occluder at the world origin.
    /// </summary>
    public override void CollectGiProxy()
    {
        if (!CastShadows || !Ready || !HasContent || !Enable || LocalBounds.Extents == Vector3.Zero)
            return;

        GiProxies.TryAdd(GetWorldBounds(), GiAlbedo, GiEmissive, Name);
    }

    /// <summary>
    /// 1-3: Control-level AABB frustum test, shared across all four backends and allocation-free.
    /// Empty bounding boxes, from unloaded or geometry-free controls, do not participate in culling,
    /// preventing zero boxes from being misclassified as invisible.
    /// </summary>
    bool IsFrustumCulled()
    {
        if (!RenderQuality.Current.FrustumCulling || !CullingEnabled)
            return false;

        if (LocalBounds.Extents == Vector3.Zero)
            return false;

        return !DeviceServices.BaseApp.Camera.Frustum.Intersects(GetWorldBounds());
    }
}
