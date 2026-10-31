// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Panels;

/// <summary>
/// Pure container type.
/// It intentionally does not implement IControl, avoiding a dual identity where a container
/// is also treated as a drawable or sortable leaf.
/// Controls are leaves implementing IControl, while Panels are containers and may recurse without limit.
/// This class does not declare ILoadable directly.
/// Loading capability is declared explicitly by individual panels only when needed,
/// such as Mountains or Rocks, so the type system can prevent accidental queueing of non-loadable panels.
/// The base class still provides all ILoadable members,
/// including ID, Name, Ready, LoadStart, LoadComplete, IsDisposed, and Load,
/// so derived panels that declare the interface satisfy the contract automatically through inheritance.
/// </summary>
public abstract class Panel : BaseControl, IRenderOrder
{
    public int Layer { get; set; }

    public int Order { get; set; }

    public DateTime? LoadStart { get; set; }

    public DateTime? LoadComplete { get; set; }

    /// <summary>
    /// Lifecycle state flag set after Dispose.
    /// Load queues such as LoadControlAsync and async harvest paths use this flag
    /// to discard stale results.
    /// It is also part of the ILoadable contract, so derived panels that declare ILoadable
    /// inherit a valid implementation automatically.
    /// </summary>
    public bool IsDisposed { get; protected set; }

    public List<Panel> Panels = new List<Panel>();

    public List<IControl> Controls = new List<IControl> { };

    public Action OnClose;

    List<ControlItem>? _sceneControls;
    List<PanelItem>? _scenePanels;
    bool _collectingScene;

    public Panel()
    {

    }

    public virtual bool AddControl(Control control)
    {
        if (control is null || Controls.Contains(control))
        {
            return false;
        }
        else
        {
            Controls.Add(control);

            DeviceServices.BaseApp?.RequestLoad(control);
        }

        return true;
    }

    public virtual bool RemoveControl(Control control)
    {
        if (control is null || !Controls.Contains(control))
        {
            return false;
        }
        else
        {
            Controls.Remove(control);

            control.Dispose();
        }

        return true;
    }

    public virtual bool AddPanel(Panel panel)
    {
        if (panel is null || Panels.Contains(panel))
        {
            return false;
        }
        else
        {
            Panels.Add(panel);
        }

        return true;
    }

    public virtual bool RemovePanel(Panel panel)
    {
        if (panel is null || !Panels.Contains(panel))
        {
            return false;
        }
        else
        {
            Panels.Remove(panel);

            panel.Dispose();
        }

        return true;
    }

    public override bool Changed
    {
        get
        {
            if (Controls.Count == 0 && Panels.Count == 0)
            {
                return false;
            }
            else
            {
                return Controls.Any(c => c.Changed) || Panels.Any(p => p.Changed);
            }
        }
        set
        {
            if (Controls.Count > 0)
            {
                foreach (var c in Controls)
                {
                    if (c != null)
                    {
                        c.Changed = value;
                    }
                }
            }

            if (Panels.Count > 0)
            {
                foreach (var p in Panels)
                {
                    if (p != null)
                    {
                        p.Changed = value;
                    }
                }
            }
        }
    }

    public bool ContentDirty
    {
        get
        {
            if (Controls.Count == 0)
            {
                return false;
            }
            else
            {
                return Controls.Any(c => c.ContentDirty);
            }
        }
        set
        {
            if (Controls.Count == 0)
            {
                return;
            }

            foreach (var c in Controls)
            {
                if (c is not null)
                {
                    c.ContentDirty = value;
                }
            }
        }
    }

    public virtual void SetMode(string mode)
    {
        //var iSetMode = this as ISetMode;

        //if (iSetMode == null)
        //{

        //}
        //else
        //{
        //    iSetMode.SetMode(mode);
        //}

        if (Controls.Count == 0)
        {

        }
        else
        {
            foreach (var c in Controls)
            {
                if (c is null)
                {

                }
                else if (c is ISetMode)
                {
                    (c as ISetMode).SetMode(mode);
                }
                else
                {

                }
            }
        }
    }

    public virtual void Dispose()
    {
        IsDisposed = true;

        Controls.ForEach(c => c?.Dispose());

        Controls.Clear();

        Panels.ForEach(p => p?.Dispose());

        Panels.Clear();
    }

    public virtual async Task<bool> Load()
    {
        return false;
    }

    public virtual void Draw()
    {
        Draw(Season.Controls.RenderDomain.Scene);
    }

    public void Draw(Season.Controls.RenderDomain renderDomain)
    {
        Draw(renderDomain, Season.Controls.RenderDomain.Scene);
    }

    void Draw(Season.Controls.RenderDomain renderDomain, Season.Controls.RenderDomain inheritedDomain)
    {
        if (Alpha <= 0f)
            return;

        // P3: a panel that the executing view cannot see drops its entire subtree, not just its own draw.
        if (!VisibleInCurrentView(this))
            return;

        if (Season.Rendering.FrameSchedule.SceneCollectionActive && renderDomain == Season.Controls.RenderDomain.Scene)
        {
            CollectScene(inheritedDomain, Layer, Order);
            return;
        }

        var effectiveDomain = ResolveRenderDomain(inheritedDomain);

        if (Controls.Count > 0)
        {
            int count = Controls.Count;
            var items = new List<ControlItem>();

            for (int i = 0; i < count; i++)
            {
                var control = Controls[i];

                if (control.Alpha > 0 && VisibleInCurrentView(control)
                    && ResolveControlRenderDomain(control, effectiveDomain) == renderDomain)
                {
                    int layer = 0;
                    int order = 0;

                    if (control is IRenderOrder renderOrder)
                    {
                        layer = renderOrder.Layer;
                        order = renderOrder.Order;
                    }

                    bool transparentSortable = control is ITransparentSortable sortable && sortable.EnableTransparentSort;
                    float transparentDepth = transparentSortable ? TransparentSortUtils.GetDepth((ITransparentSortable)control) : 0f;

                    items.Add(new ControlItem(control, layer, order, i, transparentSortable, transparentDepth));
                }
            }
            items.Sort(ControlItemComparer.Instance);

            items.ForEach(it => it.Control.Draw());
        }

        if (Panels.Count > 0)
        {
            int count = Panels.Count;
            var items = new List<PanelItem>();

            for (int i = 0; i < count; i++)
            {
                var panel = Panels[i];

                if (panel.Alpha > 0f && VisibleInCurrentView(panel))
                {
                    int layer = 0;
                    int order = 0;

                    if (panel is IRenderOrder renderOrder)
                    {
                        layer = renderOrder.Layer;
                        order = renderOrder.Order;
                    }

                    bool transparentSortable = panel is ITransparentSortable sortable && sortable.EnableTransparentSort;
                    float transparentDepth = transparentSortable ? TransparentSortUtils.GetDepth((ITransparentSortable)panel) : 0f;

                    items.Add(new PanelItem(panel, layer, order, i, transparentSortable, transparentDepth));
                }
            }

            items.Sort(PanelItemComparer.Instance);

            items.ForEach(it => it.Panel.Draw(renderDomain, effectiveDomain));
        }
    }

    // P3 layer culling: the executing view's mask decides whether a control (or a whole panel subtree) is drawn at
    // all. Outside a view the mask is all ones, so every existing single-view traversal is unchanged.
    static bool VisibleInCurrentView(BaseControl control)
        => Season.Rendering.RenderView.IsLayerVisible(control.CullLayer);

    // The leaf list is typed IControl, and a leaf that is not a BaseControl has no layer of its own, so it counts as
    // layer 0: visible to every view that still draws anything. Panels do not implement IControl on purpose, which is
    // why this is an overload rather than one wider parameter.
    static bool VisibleInCurrentView(IControl control)
        => control is BaseControl baseControl
            ? VisibleInCurrentView(baseControl)
            : Season.Rendering.RenderView.IsLayerVisible(0u);

    // 收集只调用一次叶子 Draw；材质分类与深度排序由后端图元队列完成。
    // 复用成员列表固定本次成员与显式优先级，不在回放时访问控件或容器。
    void CollectScene(Season.Controls.RenderDomain inheritedDomain, int layer, int order)
    {
        if (_collectingScene) throw new InvalidOperationException("场景容器不能在收集期间重入。");
        _collectingScene = true;
        var controls = _sceneControls ??= new();
        var panels = _scenePanels ??= new();
        try
        {
            using var priority = Season.Rendering.FrameSchedule.SceneDrawOrder.Push(layer, order);
            var effectiveDomain = ResolveRenderDomain(inheritedDomain);
            for (int i = 0; i < Controls.Count; i++)
            {
                var control = Controls[i];
                if (control.Alpha <= 0 || !VisibleInCurrentView(control)
                    || ResolveControlRenderDomain(control, effectiveDomain) != Season.Controls.RenderDomain.Scene)
                    continue;
                var sorting = control as IRenderOrder;
                controls.Add(new ControlItem(control, sorting?.Layer ?? 0, sorting?.Order ?? 0, i, false, 0));
            }
            for (int i = 0; i < Panels.Count; i++)
            {
                var panel = Panels[i];
                if (panel.Alpha > 0 && VisibleInCurrentView(panel))
                    panels.Add(new PanelItem(panel, panel.Layer, panel.Order, i, false, 0));
            }
            controls.Sort(ControlItemComparer.Instance);
            panels.Sort(PanelItemComparer.Instance);
            for (int i = 0; i < controls.Count; i++)
            {
                var item = controls[i];
                using var leafPriority = Season.Rendering.FrameSchedule.SceneDrawOrder.Push(item.Layer, item.Order);
                item.Control.Draw();
            }
            for (int i = 0; i < panels.Count; i++)
            {
                var item = panels[i];
                item.Panel.CollectScene(effectiveDomain, item.Layer, item.Order);
            }
        }
        finally
        {
            controls.Clear(); panels.Clear();
            _collectingScene = false;
        }
    }

    /// <summary>
    /// Traverses the shadow pass.
    /// This is a simplified replay path, so no sorting or transparent-depth handling is needed in the depth-only pass.
    /// It only filters by Alpha > 0, calls DrawShadow on each control, and then recurses into child panels.
    /// Control-specific CastShadows and type gating are handled inside each override.
    /// </summary>
    public virtual void DrawShadow()
    {
        DrawShadow(Season.Controls.RenderDomain.Scene);
    }

    void DrawShadow(Season.Controls.RenderDomain inheritedDomain)
    {
        if (Alpha <= 0f)
            return;

        if (!VisibleInCurrentView(this))
            return;

        var effectiveDomain = ResolveRenderDomain(inheritedDomain);

        if (Controls.Count > 0)
        {
            int count = Controls.Count;
            for (int i = 0; i < count; i++)
            {
                var control = Controls[i];
                if (control.Alpha > 0 && VisibleInCurrentView(control)
                    && ResolveControlRenderDomain(control, effectiveDomain) == Season.Controls.RenderDomain.Scene)
                    control.DrawShadow();
            }
        }

        if (Panels.Count > 0)
        {
            int count = Panels.Count;
            for (int i = 0; i < count; i++)
            {
                var panel = Panels[i];
                if (panel.Alpha > 0f && VisibleInCurrentView(panel))
                    panel.DrawShadow(effectiveDomain);
            }
        }
    }

    /// <summary>
    /// Traverses GI proxy collection.
    /// This is a zero-allocation replay path structurally identical to <see cref="DrawShadow"/>.
    /// No sorting is performed, filtering is only by Alpha &gt; 0,
    /// and control-specific CastShadows or type gating is handled inside each override.
    /// The traversal is driven from the BaseApp root panel by <c>GiProxies.Collect</c>
    /// during the DdgiEffect.Record AfterScene phase.
    /// </summary>
    public virtual void CollectGiProxies()
    {
        CollectGiProxies(Season.Controls.RenderDomain.Scene);
    }

    void CollectGiProxies(Season.Controls.RenderDomain inheritedDomain)
    {
        if (Alpha <= 0f)
            return;

        var effectiveDomain = ResolveRenderDomain(inheritedDomain);

        if (Controls.Count > 0)
        {
            int count = Controls.Count;
            for (int i = 0; i < count; i++)
            {
                var control = Controls[i];
                if (control.Alpha > 0 && ResolveControlRenderDomain(control, effectiveDomain) == Season.Controls.RenderDomain.Scene)
                    control.CollectGiProxy();
            }
        }

        if (Panels.Count > 0)
        {
            int count = Panels.Count;
            for (int i = 0; i < count; i++)
            {
                var panel = Panels[i];
                if (panel.Alpha > 0f)
                    panel.CollectGiProxies(effectiveDomain);
            }
        }
    }

    static Season.Controls.RenderDomain ResolveControlRenderDomain(IControl control, Season.Controls.RenderDomain inheritedDomain)
    {
        return control is BaseControl baseControl
            ? baseControl.ResolveRenderDomain(inheritedDomain)
            : inheritedDomain;
    }
}

internal readonly struct PanelItem
{
    public readonly Panel Panel;
    public readonly int Layer;
    public readonly int Order;
    public readonly int Index;
    public readonly bool TransparentSortable;
    public readonly float TransparentDepth;

    public PanelItem(Panel panel, int layer, int order, int index, bool transparentSortable, float transparentDepth)
    {
        Panel = panel;
        Layer = layer;
        Order = order;
        Index = index;
        TransparentSortable = transparentSortable;
        TransparentDepth = transparentDepth;
    }
}

internal sealed class PanelItemComparer : IComparer<PanelItem>
{
    public static readonly PanelItemComparer Instance = new PanelItemComparer();

    public int Compare(PanelItem x, PanelItem y)
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
