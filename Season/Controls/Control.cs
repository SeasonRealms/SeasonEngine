// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Controls;

public enum RenderDomain
{
    Inherit,
    Scene,
    Overlay,
}

/// <summary>
/// Contract for asynchronously loadable entities, as a cross-cutting concept unrelated to rendering.
/// Any control, panel, or background task entity that implements this interface can join the global
/// loading queue through <c>BaseApp.RequestLoad</c>, reusing concurrency limiting (ResizeSemaphore),
/// the Web per-frame budget, Dispose race protection, and load logging.
///
/// Contract terms:
/// 1) When <see cref="Load"/> returns true and <see cref="IsDisposed"/> is still false, the queue sets
///    <see cref="Ready"/> = true and records <see cref="LoadComplete"/>; returning false means loading failed
///    and the queue does not retry.
/// 2) The meaning of <see cref="Ready"/> is defined by the implementer: leaf control = GPU resources are ready
///    and drawable; container (Panel) = its own Load has completed, while children may still become ready
///    progressively in the queue; task entity = task output is available.
/// 3) If the implementer is disposed after being queued, queue consumption skips Load via the IsDisposed check.
///    If disposal happens during Load, the result is discarded and Ready is not set. The implementer must set
///    <see cref="IsDisposed"/> = true during Dispose.
/// 4) Container types (Panel) do not declare this interface by default: declaring it means there is actual
///    loading work, allowing the type system to prevent accidental queueing. The base class still provides
///    all required members, so derived classes satisfy the contract once they add the interface declaration
///    (for example, Mountains/Rocks only add the interface declaration).
/// </summary>
public interface ILoadable
{
    /// <summary>Globally unique ID used by load logs; BaseControl defaults to assigning it via Texture.NextID.</summary>
    long ID { get; }

    /// <summary>Name used by load logs and cache-key identification.</summary>
    string Name { get; set; }

    /// <summary>Set by the loading queue when Load succeeds and the instance has not been disposed (contract terms 1/2).</summary>
    bool Ready { get; set; }

    /// <summary>Timestamp when the queue starts executing Load, used for load logging.</summary>
    DateTime? LoadStart { get; set; }

    /// <summary>Timestamp when Load completes successfully, used for load logging.</summary>
    DateTime? LoadComplete { get; set; }

    /// <summary>Disposed state: the queue skips Load during consumption, and if set during Load the result is discarded (contract term 3).</summary>
    bool IsDisposed { get; }

    /// <summary>Run loading asynchronously; return true for success (the queue sets Ready), false for failure (no retry).</summary>
    Task<bool> Load();
}

/// <summary>
/// Contract for renderable leaf controls: every member of IControl represents a single drawable leaf in the Control tree.
/// Loading capability is inherited through <see cref="ILoadable"/>. The rendering pipeline
/// (Draw/DrawShadow/CollectGiProxy/ControlItem sorting) only recognizes IControl, while the loading queue
/// only recognizes ILoadable, keeping the two flows separate. Containers (Panel) implement only ILoadable
/// and not IControl, preserving the split between recursive containers and drawable leaves.
/// </summary>
public interface IControl : ILoadable
{
    float Alpha { get; set; }

    RenderDomain RenderDomain { get; set; }

    bool Changed { get; set; }

    bool ContentDirty { get; set; }

    bool Draw();

    void DrawShadow();

    void CollectGiProxy();

    void Dispose();
}

public abstract class BaseControl
{
    public long ID { get; } = Texture.NextID();

    protected string _name;
    public virtual string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; Changed = true; } }
    }

    float _alpha = 1f;
    public float Alpha
    {
        get => _alpha;
        set
        {
            if (_alpha != value)
            {
                float previous = _alpha;
                _alpha = value;
                Changed = true;
            }
        }
    }

    // Unified positioning model: 2D and 3D share the same six position/size fields,
    // and Changed is maintained automatically by the setters.
    // 2D controls use pixel coordinates (still represented with integer-valued inputs);
    // 3D controls (Model/Mesh3D/Instanced family) use world coordinates in meters:
    // (PosX, PosY, PosZ) is the anchor point (the top-left near-screen corner of the local bounding box),
    // Width extends along +X, Height drops along -Y, and Depth extends along +Z
    // (the anchor/scale contract Phase 2 is implemented in Mesh3DBase).
    // For 2D controls, PosZ and Depth stay 0 and do not participate in 2D layout for now.
    float _posX;
    public float PosX
    {
        get => _posX;
        set { if (_posX != value) { _posX = value; Changed = true; } }
    }

    float _posY;
    public float PosY
    {
        get => _posY;
        set { if (_posY != value) { _posY = value; Changed = true; } }
    }

    float _posZ;
    public float PosZ
    {
        get => _posZ;
        set { if (_posZ != value) { _posZ = value; Changed = true; } }
    }

    float? _width;
    public virtual float? Width
    {
        get => _width;
        set { if (_width != value) { _width = value; Changed = true; } }
    }

    float? _height;
    public virtual float? Height
    {
        get => _height;
        set { if (_height != value) { _height = value; Changed = true; } }
    }

    float? _depth;
    public float? Depth
    {
        get => _depth;
        set { if (_depth != value) { _depth = value; Changed = true; } }
    }

    public bool Enable { get; set; } = true;

    public virtual bool Ready { get; set; }

    public virtual bool MouseOver { get; set; }

    public virtual bool Selected { get; set; }

    public virtual bool Changed { get; set; }

    RenderDomain _renderDomain = RenderDomain.Inherit;
    public RenderDomain RenderDomain
    {
        get => _renderDomain;
        set
        {
            if (_renderDomain != value)
            {
                _renderDomain = value;
                Changed = true;
            }
        }
    }

    public Action? OnClick, OnTouch;

    internal RenderDomain ResolveRenderDomain(RenderDomain inheritedDomain)
    {
        return RenderDomain == RenderDomain.Inherit ? inheritedDomain : RenderDomain;
    }

    public virtual bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        if (alpha is not null) Alpha = alpha.Value;

        if (posX is not null) PosX = posX.Value;

        if (posY is not null) PosY = posY.Value;

        if (width is not null) Width = width.Value;

        if (height is not null) Height = height.Value;

        if (posZ is not null) PosZ = posZ.Value;

        if (depth is not null) Depth = depth.Value;

        if (TouchService.Enable && Enable && Ready && Alpha > 0 && MouseOver)
        {
            if (TouchService.IsDown && OnTouch != null)
            {
                TouchService.IsDown = TouchService.IsReleased = false;

                OnTouch?.Invoke();
            }

            if (TouchService.IsReleased && OnClick != null)
            {
                TouchService.IsReleased = false;

                OnClick?.Invoke();

                return true;
            }
        }

        return false;
    }

    public virtual void Dispose()
    {

    }
}

public abstract class Control : BaseControl, IControl
{
    /// <summary>
    /// 1-3: Whether this control participates in CPU frustum culling (see RenderQuality 1-3, clause 5).
    /// Defaults to true. Geometry that is always visible, such as skyboxes or fullscreen overlays,
    /// should set this to false for exemption.
    /// Only applies to controls with 3D bounds (Model/Mesh3D/Instanced family); ignored by 2D controls.
    /// </summary>
    public bool CullingEnabled { get; set; } = true;

    /// <summary>
    /// 1-5: Whether this control casts shadows into the shadow atlas (see RenderQuality 1-5, clause 7).
    /// Defaults to true. Geometry that should not cast shadows, such as skyboxes or fullscreen overlays,
    /// should set this to false.
    /// Only applies to 3D controls that participate in the shadow pass (Model/Mesh3D/Instanced family);
    /// ignored by 2D controls.
    /// </summary>
    public bool CastShadows { get; set; } = true;

    /// <summary>
    /// 2-4: Diffuse reflectance of the GI proxy (see the signed decision (iii) and clauses 4/5 in RenderQuality 2-4).
    /// Defaults to neutral gray 0.5. This is intentionally **not** inferred automatically from glTF baseColorFactor:
    /// the proxy is one box for the whole object, while baseColorFactor is material-specific, so choosing any one
    /// value has no sound correctness basis and would make indirect-light color shifts harder to diagnose.
    /// Objects that need colored indirect light must be set explicitly by the app, for example a red wall as (0.6,0.1,0.1).
    /// Only applies to controls that produce GI proxies (Model/Mesh3D); ignored by 2D controls.
    /// </summary>
    public Vector3 GiAlbedo { get; set; } = new Vector3(0.5f, 0.5f, 0.5f);

    /// <summary>
    /// 2-4: Emissive radiance of the GI proxy (see clause 5 in RenderQuality 2-4). Defaults to all zeros.
    /// Any non-zero value means the whole object participates in indirect lighting as an area light,
    /// which is the only way in the first version to make "emissive objects light their surroundings" work.
    /// Because the proxy granularity is the whole bounding box, it suits fully emissive light boxes or neon signs,
    /// but not localized emissive details inside a texture.
    /// Only applies to controls that produce GI proxies (Model/Mesh3D); ignored by 2D controls.
    /// </summary>
    public Vector3 GiEmissive { get; set; } = Vector3.Zero;

    public bool IsDisposed { get; protected set; }

    public bool ContentDirty { get; set; }

    public DateTime? LoadStart { get; set; }

    public DateTime? LoadComplete { get; set; }

    public virtual async Task<bool> Load()
    {
        return false;
    }

    public virtual bool Draw()
    {
        return true;
    }

    /// <summary>
    /// 1-5: Drawing entry for the shadow pass (contract clauses 3/7). Empty by default.
    /// 3D controls that participate in shadows (Model/Mesh3D/Instanced family) override this and project
    /// conditionally based on CastShadows, while also performing per-cascade light-space culling.
    /// This does not reuse camera frustum results; it tests the actual light-space box submitted for the current cascade,
    /// see <see cref="Rendering.CascadedShadow.IsCulled"/>.
    /// </summary>
    public virtual void DrawShadow()
    {

    }

    /// <summary>
    /// 2-4: Entry point for GI proxy collection (contract clause 4). Empty by default.
    /// 3D controls that produce proxies (Model/Mesh3D) override this and call <c>GiProxies.TryAdd</c>.
    /// Its gating mirrors DrawShadow by reusing CastShadows, see boundary 3 in the GiProxies class header,
    /// and it does not perform frustum culling because proxy volumes are camera-anchored, so off-screen objects
    /// must still block light.
    /// </summary>
    public virtual void CollectGiProxy()
    {

    }

    public override void Dispose()
    {
        base.Dispose();
        IsDisposed = true;
    }
}

public interface IRenderOrder
{
    int Layer { get; set; }

    int Order { get; set; }
}

public interface ITransparentSortable
{
    System.Numerics.Vector3 TransparentSortPosition { get; }

    bool EnableTransparentSort { get; }
}

internal readonly struct ControlItem
{
    public readonly IControl Control;
    public readonly int Layer;
    public readonly int Order;
    public readonly int Index;
    public readonly bool TransparentSortable;
    public readonly float TransparentDepth;

    public ControlItem(IControl control, int layer, int order, int index, bool transparentSortable, float transparentDepth)
    {
        Control = control;
        Layer = layer;
        Order = order;
        Index = index;
        TransparentSortable = transparentSortable;
        TransparentDepth = transparentDepth;
    }
}

internal sealed class ControlItemComparer : IComparer<ControlItem>
{
    public static readonly ControlItemComparer Instance = new ControlItemComparer();

    public int Compare(ControlItem x, ControlItem y)
    {
        int c = x.Layer.CompareTo(y.Layer);
        if (c != 0) return c;

        c = x.Order.CompareTo(y.Order);
        if (c != 0) return c;

        if (x.TransparentSortable != y.TransparentSortable)
            return x.TransparentSortable ? 1 : -1;

        if (x.TransparentSortable)
        {
            c = y.TransparentDepth.CompareTo(x.TransparentDepth);
            if (c != 0) return c;
        }

        return x.Index.CompareTo(y.Index);
    }
}

internal static class TransparentSortUtils
{
    public static float GetDepth(ITransparentSortable sortable)
    {
        var app = DeviceServices.BaseApp;
        if (app == null)
            return sortable.TransparentSortPosition.Z;

        var forward = app.CameraTarget - app.CameraPos;
        if (forward.LengthSquared() < 1e-6f)
            forward = System.Numerics.Vector3.UnitZ;
        else
            forward = System.Numerics.Vector3.Normalize(forward);

        return System.Numerics.Vector3.Dot(sortable.TransparentSortPosition - app.CameraPos, forward);
    }
}
