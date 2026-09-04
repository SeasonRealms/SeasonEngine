// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Controls;

/// <summary>
/// Per-instance transform data for static Mesh3D and animated InstancedModel.
/// v2 supports per-instance animation parameters for InstancedModel animation rendering.
/// Unified positioning model: PosX/PosY/PosZ are the world position of the instance anchor
/// (the geometric center of the host template box), Width/Height/Depth are per-axis scales,
/// and Rotation is a quaternion with the anchor as the pivot. The matrix is built uniformly by
/// <see cref="InstancedMesh3DBase.BuildInstanceMatrix"/>. When position needs to be expressed
/// in template-local-origin terms, convert with <see cref="InstancedMesh3DBase.InstanceAnchorWorldOffset"/>.
/// </summary>
public class MeshInstanceTransform
{
    public long ID { get; set; }

    public string Name { get; set; }
    // Unified positioning model (host anchor).

    /// <summary>World-space X position of the instance anchor (the anchor is the geometric center of the host template box).</summary>
    public float PosX { get; set; }

    /// <summary>World-space Y position of the instance anchor.</summary>
    public float PosY { get; set; }

    /// <summary>World-space Z position of the instance anchor.</summary>
    public float PosZ { get; set; }

    /// <summary>Target instance width in world meters along +X. 0 means unset and is settled to the template-local size during host Update.</summary>
    public float Width { get; set; }

    /// <summary>Target instance height in world meters along -Y. 0 means unset and is settled to the template-local size during host Update.</summary>
    public float Height { get; set; }

    /// <summary>Target instance depth in world meters along +Z. 0 means unset and is settled to the template-local size during host Update.</summary>
    public float Depth { get; set; }

    public Quaternion Rotation { get; set; } = Quaternion.Identity;
    public bool Enable { get; set; } = true;

    /// <summary>
    /// Click-selection state, driven uniformly by ObjectPicker and aligned semantically with
    /// <see cref="Mesh3DBase.Selected"/>. A successful click hit sets it to true and locks it
    /// until a click outside the bounding box results in no hit, which clears it back to false.
    /// The app side may read it directly, and writing false forcibly clears the lock.
    /// </summary>
    public bool Selected { get; set; }

    // Unified highlight for composite objects, see Highlight. PrimitiveData is created lazily
    // by the DX backend and pooled, independent from the host-wide alpha chain
    // (face alpha = SurfaceColor.W can pulse frame by frame). It can be enabled, disabled,
    // recolored, or have its outline width changed at runtime.
    /// <summary>Unified highlight configuration for this instance (style/on-off/two-color/outline width,
    /// see <see cref="Highlight"/>). When the host explicitly replaces Highlight, it cascades to existing
    /// instances as a whole via CopyFrom, see <see cref="InstancedMesh3DBase.Highlight"/>.
    /// Instances remain independent objects and may still override it individually. Defaults to the Bounds style.</summary>
    public Highlight Highlight { get; set; } = new();

    // Per-instance animation controls, used only by InstancedModel and ignored by InstancedMesh3D.
    /// <summary>Animation playback rate. 1.0 means normal speed. Defaults to 1.</summary>
    public float AnimationSpeed { get; set; } = 1f;
    /// <summary>Animation clip index. 0 means the default or first animation. Defaults to 0.</summary>
    public int AnimationClip { get; set; } = 0;
    /// <summary>Initial animation time offset in seconds. Defaults to 0.</summary>
    public float AnimationTimeOffset { get; set; } = 0f;
}

/// <summary>
/// Shared base class for GPU-instanced 3D controls, namely InstancedMesh3D and InstancedModel.
/// It extracts the common framework that is structurally identical between the two controls:
/// - Unified positioning model: template anchor (the geometric center of the raw template box)
///   plus per-instance Width/Height/Depth scaling, plus BuildInstanceMatrix with the anchor as
///   the rotation pivot. Use InstanceAnchorWorldOffset when instance position needs to be expressed
///   in template-local-origin form.
/// - Instance collection and template bounds: Instances + TemplateLocalBounds/TemplateLocalBoundsRaw,
///   filled once during Load.
/// - 1-3: Per-instance bounding-sphere frustum broad-phase culling. The whole batch is skipped only
///   when all instances are invisible, per contract clause 3.
/// - 1-5: DrawShadow is gated by CastShadows. Per contract clause 7, the shadow pass does not do frustum culling.
/// - Unified Draw/DrawShadow gating: Ready/Enable/has content/non-empty instance set/Alpha != 0.
/// Subclasses inject only the varying pieces through abstract members: the content predicate (HasContent)
/// and the per-backend Graphics dispatch points.
/// Note: instanced controls in v1 do not participate in transparent sorting, so they do not implement
/// ITransparentSortable. That interface exists only on Mesh3DBase.
/// </summary>
public abstract class InstancedMesh3DBase : Control
{
    /// <summary>All instance transforms participating in instanced rendering.</summary>
    public List<MeshInstanceTransform> Instances { get; } = new List<MeshInstanceTransform>();

    /// <summary>
    /// Host-level batch highlight configuration for composite objects, see <see cref="Highlight"/>.
    /// When explicitly replaced, it cascades as a whole to all currently existing instances via CopyFrom,
    /// applying style/on-off/two-color/outline width in one step. When instances are added only after the host
    /// highlight has been set, for example Mountains where Build sets it first and Update then adds instances
    /// through SyncInstances frame by frame, ObjectPicker falls back to resolving "instance keeps default -> inherit host",
    /// see ObjectPicker.SetHighlight. Instances remain independent objects and may still override their own Highlight.
    /// Nested assignment like Highlight = { ... } does not trigger cascading; only whole-object replacement does.
    /// </summary>
    Highlight _highlight = new();

    public Highlight Highlight
    {
        get
        {
            return _highlight;
        }
        set
        {
            _highlight = value ?? new();

            foreach (var ins in Instances)
                ins.Highlight.CopyFrom(_highlight);
        }
    }

    /// <summary>
    /// 1-3: Local AABB of the shared geometry template, filled once during loading per contract clause 2.
    /// InstancedMesh3D builds it by aggregating Surface vertices. InstancedModel fills it from the GltfAsset
    /// loading path after RH-to-LH conversion. The per-instance world bounds are obtained by transforming
    /// this template box by each instance transform and are consumed by the per-backend instance paths.
    /// </summary>
    public Bounds3D TemplateLocalBounds { get; internal set; }

    /// <summary>
    /// Unified positioning model: raw template bounding box with no conservative animation expansion applied,
    /// already converted from RH to LH. TemplateLocalBounds, which is used for 1-3 culling, may be expanded
    /// by AnimatedBoundsScale for animated InstancedModel. Anchor computation and per-axis scaling always
    /// use this raw box. InstancedMesh3D has no expansion, so this equals TemplateLocalBounds.
    /// InstancedModel backfills it from the template Model.LocalBoundsRaw through each backend loading path.
    /// </summary>
    public Bounds3D TemplateLocalBoundsRaw { get; internal set; }

    /// <summary>Unified positioning model: template-local size before scaling, equal to the full size of the raw template box and used for per-axis scale computation.</summary>
    public Vector3 TemplateLocalSize => TemplateLocalBoundsRaw.Extents * 2f;

    /// <summary>
    /// Unified positioning model: the template-local anchor is the geometric center of the raw template box, A = Center.
    /// Since 2026-08 this replaced the former "top-left near-screen" corner (Min.X, Max.Y, Min.Z) so it matches
    /// the center anchor used by Mesh3DBase.AnchorLocal. The instance position (PosX, PosY, PosZ) is the world
    /// position of that anchor, meaning the template-box center lands at the instance position. The rotation pivot
    /// is therefore naturally centered and Pos remains stable while rotating.
    /// </summary>
    public Vector3 TemplateAnchorLocal => TemplateLocalBoundsRaw.Center;

    /// <summary>
    /// Unified positioning model: per-instance per-axis scale = target instance size / template-local size,
    /// using the same zero-dimension guard as <see cref="Mesh3DBase.AxisScale"/>.
    /// </summary>
    internal Vector3 InstanceComputedScale(MeshInstanceTransform instance)
    {
        var local = TemplateLocalSize;
        float maxLocal = MathF.Max(local.X, MathF.Max(local.Y, local.Z));
        return new Vector3(
            Mesh3DBase.AxisScale(instance.Width, local.X, maxLocal),
            Mesh3DBase.AxisScale(instance.Height, local.Y, maxLocal),
            Mesh3DBase.AxisScale(instance.Depth, local.Z, maxLocal));
    }

    /// <summary>
    /// Unified positioning model for the instance world matrix, mapping template space to world space:
    /// CreateScale(per-axis scale) x CreateTranslation(-anchor) x Rotation x CreateTranslation(PosX, PosY, PosZ).
    /// The rotation pivot is fixed at the anchor, that is, the geometric center of the template box.
    /// Matrix computation across all four backend instance paths converges to this method.
    /// </summary>
    public Matrix4x4 BuildInstanceMatrix(MeshInstanceTransform instance)
    {
        return Matrix4x4.CreateScale(InstanceComputedScale(instance))
             * Matrix4x4.CreateTranslation(-TemplateAnchorLocal)
             * Matrix4x4.CreateFromQuaternion(instance.Rotation)
             * Matrix4x4.CreateTranslation(instance.PosX, instance.PosY, instance.PosZ);
    }

    /// <summary>
    /// World-space offset of the instance anchor: the world displacement from the template-local origin
    /// to the instance anchor, which is the geometric center of the template box,
    /// computed as A with per-axis scaling applied and then rotated, without translation.
    /// Identity:
    /// p*S*R + t == ((p-A)*S)*R + Pos  <=>  t = Pos - InstanceAnchorWorldOffset.
    /// So when instance position must be expressed in terms of the template-local origin, write
    /// Pos = target + InstanceAnchorWorldOffset. As long as size and rotation are settled first and
    /// the offset is recomputed per frame, the identity holds. This identity is valid for any anchor definition,
    /// and code that pins objects under a center anchor needs no changes.
    /// </summary>
    public Vector3 InstanceAnchorWorldOffset(MeshInstanceTransform instance)
        => Vector3.TransformNormal(TemplateAnchorLocal * InstanceComputedScale(instance), Matrix4x4.CreateFromQuaternion(instance.Rotation));

    /// <summary>
    /// Current-frame world bounds of the instance, based on the raw template box and the unified positioning model,
    /// computed by transforming TemplateLocalBoundsRaw with <see cref="BuildInstanceMatrix"/> using the |M| method.
    /// This matches the rendered object itself: both picking via <see cref="TryPickInstance"/> and selected-box drawing
    /// through ObjectPicker use this method. With rotation, the result is a conservative world AABB,
    /// consistent with Mesh3DBase.GetWorldBoundsRaw.
    /// </summary>
    public Bounds3D GetInstanceWorldBoundsRaw(MeshInstanceTransform instance)
    {
        return TemplateLocalBoundsRaw.Transform(BuildInstanceMatrix(instance));
    }

    /// <summary>
    /// Per-instance picking test: determines which instance in this control is hit by a world-space ray,
    /// with OBB precision and semantics aligned with <see cref="Mesh3DBase.TryPick"/>.
    /// For each enabled instance, the ray is transformed into template-local space by
    /// Invert(<see cref="BuildInstanceMatrix"/>) and then slab-tested against TemplateLocalBoundsRaw.
    /// Returns false when not Ready, not Enable, the template box is empty because it is not loaded yet,
    /// or there are no instances. distance is a world-space distance and is therefore comparable across
    /// controls and instances, allowing ObjectPicker to choose the nearest mixed result.
    /// </summary>
    public bool TryPickInstance(Vector3 rayOrigin, Vector3 rayDirection, out MeshInstanceTransform hit, out float distance)
    {
        hit = null;
        distance = 0f;

        if (!Ready || !Enable || TemplateLocalBoundsRaw.Extents == Vector3.Zero)
            return false;

        float bestDistance = float.MaxValue;

        for (int i = 0; i < Instances.Count; i++)
        {
            var instance = Instances[i];
            if (!instance.Enable)
                continue;

            if (Picking.RayIntersectsObb(rayOrigin, rayDirection, BuildInstanceMatrix(instance), TemplateLocalBoundsRaw, out var instanceDistance)
                && instanceDistance < bestDistance)
            {
                bestDistance = instanceDistance;
                hit = instance;
            }
        }

        if (hit != null)
            distance = bestDistance;

        return hit != null;
    }

    /// <summary>
    /// Entry point for per-instance surface-accurate picking at v2 mesh granularity.
    /// Derived classes override this with ray-triangle narrow-phase testing when triangle data is available,
    /// typically broad-phase OBB culling followed by per-triangle narrow phase.
    /// By default it falls back to <see cref="TryPickInstance"/> with OBB precision.
    /// Its semantic contract matches TryPickInstance: world-space distance, comparability across controls
    /// and instances, and nearest-hit selection.
    /// </summary>
    public virtual bool TryPickInstanceSurface(Vector3 rayOrigin, Vector3 rayDirection, out MeshInstanceTransform hit, out float distance)
        => TryPickInstance(rayOrigin, rayDirection, out hit, out distance);

    /// <summary>
    /// Settles default instance dimensions, called per instance by subclass Update before backend consumption.
    /// Any unset dimension equal to 0 is settled to the template-local size, meaning scale factor 1:
    /// the instance matrix does not multiply by OriginalScale and defaults to the template's original size.
    /// This also lazily settles newly added instances after the template has loaded. It is skipped until
    /// the template bounds have been established.
    /// </summary>
    internal void SettleInstanceDimensions(MeshInstanceTransform instance)
    {
        var local = TemplateLocalSize;
        if (local == Vector3.Zero)
            return; // Template bounds not established yet: zero dimensions remain unset and settle on the first frame after creation.

        if (instance.Width == 0f) instance.Width = local.X;
        if (instance.Height == 0f) instance.Height = local.Y;
        if (instance.Depth == 0f) instance.Depth = local.Z;
    }

    /// <summary>Content predicate: whether geometry or model resources have been declared. InstancedMesh3D requires non-empty Surfaces; InstancedModel requires a non-empty ModelName.</summary>
    protected abstract bool HasContent { get; }

    ///// <summary>Per-platform Scene-pass instanced draw dispatch point, after gating and bounding-sphere broad-phase culling have already been handled by the base Draw.</summary>
    //protected abstract void DrawCore();

    /// <summary>Per-platform shadow-pass instanced projection dispatch point, after CastShadows gating and per-cascade culling have already been handled by the base DrawShadow.</summary>
    protected abstract void DrawShadowCore();

    ///// <summary>Per-platform resource-release dispatch point, aligned with each backend's DisposeInstanced* contract.</summary>
    //protected abstract void DisposeCore();

    public override bool Draw()
    {
        var result = false;

        if (base.Draw())
        {
            // 1-3: Instancing bounding-sphere broad-phase culling. Skip the whole batch only when all instances are invisible.
            // The pass chain is unaffected, per contract clause 3.
            if (Ready && Enable && HasContent && Instances.Count > 0 && Alpha > 0f && !IsFrustumCulled())
            {
                result = true;
            }
        }

        return result;
    }

    /// <summary>
    /// 1-5: Shadow-pass projection under contract clause 7, gated by CastShadows plus per-cascade light-space culling.
    ///
    /// The predicate reuses the same per-instance bounding-sphere broad-phase test as camera culling,
    /// via <see cref="IsCulledBy"/>, only replacing the camera frustum with the active cascade's light-space box.
    /// If any enabled instance intersects that box, the whole batch is submitted. The granularity is deliberately
    /// batch-level: per-instance culling would require rebuilding the instance buffer for every cascade and uploading
    /// it every frame, violating the 1-3 clause 2 rule that pure CPU work may only reduce cost and never add cost.
    /// That is outside the authorization of clause 7.
    /// </summary>
    public override void DrawShadow()
    {
        if (!CastShadows || !Ready || !Enable || !HasContent || Instances.Count == 0 || Alpha == 0f)
            return;

        if (CullingEnabled && CascadedShadow.CullingActive
            && CascadedShadow.Register(IsCulledBy(in CascadedShadow.ActiveFrustum)))
            return;

        DrawShadowCore();
    }

    /// <summary>1-3: Camera-frustum version of per-instance broad-phase culling. The global switch and per-object exemption live here, while the shared predicate body is in <see cref="IsCulledBy"/>.</summary>
    bool IsFrustumCulled()
    {
        if (!RenderQuality.Current.FrustumCulling || !CullingEnabled)
            return false;

        return IsCulledBy(in DeviceServices.BaseApp.Camera.Frustum);
    }

    /// <summary>
    /// 1-3: Per-instance bounding-sphere broad-phase culling, independent of frustum type, shared by all four backends,
    /// and implemented as a zero-allocation for loop. Camera culling and shadow per-cascade culling reuse the same
    /// predicate body so the two paths cannot drift apart semantically.
    /// center = template-box center transformed through the new instance chain
    /// (anchor translation -> per-axis scaling -> rotation -> Pos), sharing the same source as BuildInstanceMatrix.
    /// Under a center anchor with a non-animated template, the (templateCenter - anchor) term is zero, so center equals
    /// instance Pos, while the formula remains fully general.
    /// radius = template bounding-sphere radius times the largest absolute axis of the per-axis scale,
    /// which is the conservative upper bound from |M| and collapses to the legacy form under uniform scaling.
    /// The whole batch is submitted if any enabled instance is visible. Empty template bounds do not participate in culling.
    /// </summary>
    bool IsCulledBy(in Frustum frustum)
    {
        if (TemplateLocalBounds.Extents == Vector3.Zero)
            return false;

        var templateCenter = TemplateLocalBounds.Center;
        var templateRadius = TemplateLocalBounds.SphereRadius;
        var anchor = TemplateAnchorLocal;
        bool anyEnabled = false;
        for (int i = 0; i < Instances.Count; i++)
        {
            var instance = Instances[i];
            if (!instance.Enable)
                continue;

            anyEnabled = true;
            var scale = InstanceComputedScale(instance);
            var center = Vector3.Transform((templateCenter - anchor) * scale, instance.Rotation)
                       + new Vector3(instance.PosX, instance.PosY, instance.PosZ);
            float radiusFactor = MathF.Max(MathF.Abs(scale.X), MathF.Max(MathF.Abs(scale.Y), MathF.Abs(scale.Z)));
            if (frustum.IntersectsSphere(in center, templateRadius * radiusFactor))
                return false;
        }

        // No enabled instances is not treated as "culled"; keep the existing empty-instance handling on the backend side.
        return anyEnabled;
    }
}
