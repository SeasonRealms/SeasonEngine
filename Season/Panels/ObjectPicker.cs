// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using static Season.Panels.ObjectPicker;

namespace Season.Panels;

/// <summary>
/// Highlight presentation mode used by ObjectPicker.
/// It defines which highlight effect the target should render while hovered or selected.
/// The style is stored on the target itself through <see cref="Season.Controls.Highlight.Style"/>,
/// with the composed Highlight object defined by <see cref="Season.Controls.Highlight"/>.
/// ObjectPicker dispatches writes into the corresponding property channel based on that style.
/// </summary>
public enum HighlightStyle
{
    /// <summary>No highlight effect is rendered. All three channels stay disabled and no pulse, edge color, or outline color is written. The Selected state and property panel still work normally. Large background bodies such as grass fields, where a 150 m scale bounds box or wireframe would cover the entire view, should explicitly use this style.</summary>
    None,

    /// <summary>World-space AABB bounds box with dual colors for faces and edges. This is the default presentation for picking and is visually clear on small and medium-sized models.</summary>
    Bounds,

    /// <summary>Surface-fitted wireframe highlight with dual colors for shell faces and edge strips. It avoids giant bounds boxes on large models and stays more compact. Shell geometry is built lazily at runtime on the first enabled frame, remains resident after construction, and costs no memory when globally disabled. When SurfaceColor.w is 0, it automatically degrades to edges only. The style may be switched at runtime at any time.</summary>
    Wireframe,

    /// <summary>Screen-space outline drawn along the outermost projected contour. Color and width are configured independently through <see cref="Season.Controls.Highlight.OutlineColor"/> and <see cref="Season.Controls.Highlight.OutlineWidth"/>.</summary>
    Outline,
}

/// <summary>
/// Hover-picking highlight panel.
/// When the pointer, whether mouse or touch, lands on the screen projection of a target:
/// 1. The target's own Alpha remains unchanged. Highlighting is expressed by the highlight representation,
///    whose face alpha pulses with a triangular wave from 0 to <see cref="BoxAlpha"/> at uniform speed.
/// 2. Unified highlighting is rendered by the target itself. On the DX backend the required primitives are built lazily,
///    are not scene nodes, and require neither a new PSO nor async loading.
///    The presentation is dispatched according to <see cref="Season.Controls.Highlight.Style"/>:
///    Bounds means a world AABB box with translucent faces and solid dual-color edges,
///    fitted to the raw bounds from <see cref="Mesh3DBase.GetWorldBoundsRaw"/>.
///    Wireframe means a surface-fitted highlight made of a translucent shell and solid edge strips,
///    avoiding giant bounds boxes on large models.
///    None means no highlight is rendered at all, while Selected state and the property panel still work,
///    which is suitable for large background bodies such as grass.
///    Outline means a screen-space outline. Bounds and Wireframe share
///    <see cref="Season.Controls.Highlight.SurfaceColor"/> and <see cref="Season.Controls.Highlight.EdgeColor"/>,
///    with SurfaceColor.w equal to 0 automatically degrading to pure edges.
///    Outline uses its own <see cref="Season.Controls.Highlight.OutlineColor"/> and stays solid rather than pulsing.
///    This panel only writes properties every frame, and the pulse alpha is carried through SurfaceColor.W.
///
/// Click-to-lock behavior:
/// when a click is detected, where <see cref="TouchService.IsReleased"/> means the pointer was released
/// within 20 px of the press point and is therefore treated as a click rather than a drag,
/// a hit target is locked by setting <see cref="Mesh3DBase.Selected"/> or
/// <see cref="MeshInstanceTransform.Selected"/> to true.
/// The highlight remains visible even after the pointer moves out of the projection bounds.
/// Clicking outside the bounds with no picking hit clears the selection, and clicking another target switches it.
/// One exception exists: clicking inside the board information panel rectangle does not cancel or switch
/// the current selection because it is treated as panel interaction.
///
/// Usage:
/// add the panel to the scene with AddPanel, register pickable controls in <see cref="Targets"/>,
/// and keep background bodies such as ground, walls, or skyboxes unregistered as an opt-in rule.
/// Call <see cref="Panel.Update"/> once per frame after every target panel has updated,
/// so world matrices have already reached their final value for the frame.
///
/// Scope:
/// v2 supports Mesh3D and Model, meaning Mesh3DBase-derived controls registered in <see cref="Targets"/>,
/// and per-instance picking for InstancedMesh3D and InstancedModel registered in <see cref="InstancedTargets"/>.
/// The hit granularity is one instance, and selected state is expressed through <see cref="MeshInstanceTransform.Selected"/>.
/// 2D controls are out of scope.
/// Touch screens have no hover concept, so pressing means pick and releasing means release,
/// which naturally matches the semantics of TouchService.PoX and PoY.
/// Hover is immediate, meaning moving away releases it and the highlight disappears.
/// Click-to-lock is expressed through <see cref="Mesh3DBase.Selected"/>.
/// </summary>
public class ObjectPicker : Panel
{
    /// <summary>Registered pickable targets, using an opt-in model. Background bodies such as ground, walls, and skyboxes should not be registered, otherwise they will occlude targets behind them.</summary>
    public List<Mesh3DBase> Targets { get; } = new List<Mesh3DBase>();

    /// <summary>Registered instanced pickable targets, also opt-in. Hit granularity is a single instance inside the host. See <see cref="SelectedInstance"/>.</summary>
    public List<InstancedMesh3DBase> InstancedTargets { get; } = new List<InstancedMesh3DBase>();

    /// <summary>Current highlighted target, chosen as locked selection or hover hit. Null means neither exists, or the focus is an instanced hit.</summary>
    public Mesh3DBase Selected => _selected.Instance == null ? _selected.Control as Mesh3DBase : null;

    /// <summary>Instance corresponding to the current highlighted focus. Non-null only when the focus is a single instanced hit.</summary>
    public MeshInstanceTransform SelectedInstance => _selected.Instance;

    /// <summary>Host control of the current highlighted focus, either Mesh3DBase or InstancedMesh3DBase. Null means no hit and no lock.</summary>
    public Control SelectedHost => _selected.Control;

    /// <summary>Click-locked target. Null means nothing is locked, or the locked focus is an instanced hit. A lock is not released by moving the pointer away and is only cleared by clicking outside the bounds or switching to another target.</summary>
    public Mesh3DBase Locked => _locked.Instance == null ? _locked.Control as Mesh3DBase : null;

    /// <summary>Click-locked instance. Non-null only when the locked focus is a single instanced hit.</summary>
    public MeshInstanceTransform LockedInstance => _locked.Instance;

    public Focus SelectedFocus => _selected;

    /// <summary>Pulse period in seconds for the highlight, measured as one full rise-and-fall cycle.</summary>
    public float PulsePeriod { get; set; } = 1.2f;

    /// <summary>Upper alpha limit for highlight pulsing. Bounds and Wireframe share it by writing face alpha into SurfaceColor.W, with w=0 automatically degrading to edges only. Outline stays solid and does not pulse.</summary>
    public float BoxAlpha { get; set; } = 0.3f;

    /// <summary>Fallback face color for highlighting. RGB is the base color, while W is overwritten every frame by pulse alpha into the target SurfaceColor. It is applied only when the target is still using the default white and has not been customized. Explicitly customized targets keep their own SurfaceColor. The default is white.</summary>
    public Vector4 SurfaceColor { get; set; } = new Vector4(1f, 1f, 1f, 0.3f);

    /// <summary>Fallback edge color for highlighting. It stays solid and does not pulse. It is applied only when the target is still using the default orange and has not been customized. Explicitly customized targets keep their own EdgeColor. The default is orange.</summary>
    public Vector4 EdgeColor { get; set; } = new Vector4(1f, 0.6f, 0.1f, 1f);

    /// <summary>Fallback outline color. It stays solid and does not pulse. It is applied only when the target is still using the default bright-gold outline color and the style is Outline. Explicitly customized targets keep their own OutlineColor. The default is bright gold.</summary>
    public Vector4 OutlineColor { get; set; } = new Vector4(1f, 0.84f, 0.25f, 1f);

    /// <summary>Reference value used to decide whether a target highlight color is still uncustomized. It shares the same default source as Mesh3DBase, InstancedMesh3DBase, and MeshInstanceTransform. When RGB matches it exactly, the panel-level fallback color is applied.</summary>
    static readonly Vector4 DefaultHighlightEdgeColor = new(1f, 0.6f, 0.1f, 1f);

    /// <summary>Reference value used to decide whether a target screen outline color is still uncustomized. It shares the same default source as <see cref="Season.Controls.Highlight.OutlineColor"/>. When RGBA matches it exactly, the panel-level <see cref="OutlineColor"/> fallback is applied.</summary>
    static readonly Vector4 DefaultHighlightOutlineColor = new(1f, 0.84f, 0.25f, 1f);

    Focus _selected;
    Focus _locked;

    /// <summary>Throttle for diagnostic logging. Stable picking state logs are limited to at most one line every 0.5 seconds, while switching events bypass the throttle and are emitted immediately. Throttling uses accumulated clock time, since the frame parameter time is only a delta.</summary>
    float _lastLogClock = float.MinValue;

    ObjectPanel objectPanel;

    public ObjectPicker()
    {
        RenderDomain = RenderDomain.Overlay;

        objectPanel = new ObjectPanel(this);
        AddPanel(objectPanel);
    }

    /// <summary>
    /// Programmatically locks a target as selected, for scenarios such as App-side spawning or casting.
    /// The target is added to <see cref="Targets"/> if not already registered and is immediately locked as the current focus.
    /// This is equivalent to the user clicking and hitting the target:
    /// the highlight turns on, the board property panel is positioned,
    /// and PosX, PosY, PosZ, Width, Height, and Depth become editable immediately.
    /// The lock does not release when the pointer moves away.
    /// Callers must invoke this after TouchService click consumption,
    /// because this method does not consume IsReleased and the click branch in the same frame may otherwise overwrite the lock.
    /// Repeated calls first clear the Selected state of the previous locked focus.
    /// </summary>
    public void Select(Mesh3DBase target)
    {
        if (target == null || target.IsDisposed)
            return;

        if (!Targets.Contains(target))
            Targets.Add(target);

        var focus = new Focus(target, null);

        if (!_locked.IsEmpty)
            SetSelected(_locked, false);
        _locked = focus;
        SetSelected(_locked, true);
    }

    /// <summary>
    /// Per-frame driver: picking, selection switching, pulse update, and highlight fitting.
    /// It must be called after all target panels have updated,
    /// because both picking and highlight fitting read the final world matrices for the current frame.
    /// </summary>
    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? width = null, float? height = null, float? posZ = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        if (Alpha <= 0)
        {
            // Hiding the panel does not automatically turn off highlighting.
            // Once unified highlighting is enabled, the highlight is rendered by the target itself,
            // so the property channels must be shut down explicitly.
            // Otherwise Bounds, Wireframe, or Outline may remain stuck on the target,
            // and this early-return path would not re-enter to restore them.
            if (!_selected.IsEmpty)
                SetHighlight(_selected, false, 0f);
        }

        // 1. Build the picking ray.
        // Missing pointer coordinates, such as when touch is not pressed, are treated as no hit.
        Focus hit = default;
        var app = DeviceServices.BaseApp;
        // The frame parameter time is a delta, so pulse phase and log throttling both use the accumulated clock instead.
        // BaseApp.Update has already accumulated the current frame.
        float clock = app?.Time ?? time;
        bool rayReady = false;
        Vector3 rayDirection = default;
        float bestDistance = float.MaxValue;

        if (app != null
            && TouchService.PoX is int pointerX
            && TouchService.PoY is int pointerY
            && Season.Rendering.Picking.ScreenPointToRay(pointerX, pointerY, app.Camera,
                app.ExtendResolution.X, app.ExtendResolution.Y, out var rayOrigin, out rayDirection))
        {
            rayReady = true;

            // 2. Traverse targets and keep the nearest hit in world distance.
            // TryPickSurface performs broad-phase OBB testing plus exact mesh triangle tests.
            // Hit distance is the world-space distance from the ray to the nearest triangle intersection,
            // so min-t naturally prefers the surface closest to the screen.
            // Empty mesh regions inside the bounds no longer produce false picks.
            // Whole-object Mesh3DBase hits and per-instance instanced hits are mixed together
            // and compared uniformly by world distance.
            for (int i = 0; i < Targets.Count; i++)
            {
                var target = Targets[i];

                if (target == null || target.IsDisposed)
                    continue;

                if (target.TryPickSurface(rayOrigin, rayDirection, out var distance) && distance < bestDistance)
                {
                    bestDistance = distance;
                    hit = new Focus(target, null);
                }
            }

            for (int i = 0; i < InstancedTargets.Count; i++)
            {
                var target = InstancedTargets[i];

                if (target == null || target.IsDisposed)
                    continue;

                if (target.TryPickInstanceSurface(rayOrigin, rayDirection, out var instance, out var distance) && distance < bestDistance)
                {
                    bestDistance = distance;
                    hit = new Focus(target, instance);
                }
            }
        }

        // 2.5 Click selection.
        // IsReleased means the pointer was released within 20 px of the press point,
        // so it counts as a click rather than a camera drag.
        // A hit target becomes locked and selected, while no hit means the click happened outside the bounds
        // and clears the current selection.
        // Click and hover share the same pointer coordinates, so the ray hit result above is reused directly.
        // One exception exists: when the pointer is inside the board info panel rectangle,
        // the interaction is treated as panel interaction and does not cancel or switch the current focus.
        if (TouchService.IsReleased && rayReady && TouchService.PoX is int clickX && TouchService.PoY is int clickY)
        {
            bool overBoard = !_locked.IsEmpty && objectPanel.MouseOver;

            if (!overBoard)
            {
                if (IsFocusAlive(_locked) && !SameFocus(_locked, hit))
                    SetSelected(_locked, false);

                _locked = !hit.IsEmpty && IsFocusAlive(hit) ? hit : default;

                if (!_locked.IsEmpty)
                    SetSelected(_locked, true);
            }
        }

        // Silently clear the locked focus when it becomes invalid,
        // for example because the host was disposed or the instance was removed from Instances,
        // avoiding a dangling reference.
        if (!_locked.IsEmpty && !IsFocusAlive(_locked))
        {
            SetSelected(_locked, false);
            _locked = default;
        }

        // 3. Focus is locked selection if present, otherwise hover hit.
        // A lock is not released by moving the pointer away, so the fitted highlight persists.
        // The target's own Alpha is neither snapshotted nor restored,
        // because highlighting is expressed entirely through the pulsing highlight state and the target stays unchanged.
        var focus = !_locked.IsEmpty ? _locked : hit;
        var previous = _selected;

        if (!SameFocus(focus, _selected))
        {
            // On focus switch, turn off the old focus by clearing all three channels according to its style.
            // The new focus is turned on by the pulse block below.
            if (!_selected.IsEmpty)
                SetHighlight(_selected, false, 0f);
            _selected = focus;
        }

        // Diagnostic logging, gated by BaseApp.Debug.
        // Focus switches are emitted immediately, while steady-state pointer, ray, and hit logs are limited to one every 0.5 s.
        // When there is a hit, an additional round-trip self-check is logged:
        // the world-bounds center of the hit target is projected back to screen through the same RenderViewProjection,
        // then compared against the pointer position.
        // They should coincide, and any deviation is quantitative evidence that picking and rendering are mapped inconsistently.
        if (app != null && BaseApp.Debug)
        {
            var pointerInfo = $"ptr=({TouchService.PoX},{TouchService.PoY}) res=({app.ExtendResolution.X},{app.ExtendResolution.Y})";

            if (!SameFocus(previous, _selected))
                app.AddLog(LogType.Backend, $"{DateTime.UtcNow} [ObjectPicker] select {FocusDisplayName(previous)} -> {FocusDisplayName(_selected)} {pointerInfo}"
                    + $" cam=({app.Camera.Position.ToString("F2")}) dpi={app.CompositionScale.X:F3}");

            if (clock - _lastLogClock >= 0.5f)
            {
                _lastLogClock = clock;

                string proj = "-";
                if (!hit.IsEmpty
                    && Season.Rendering.Picking.ProjectToScreen(GetFocusWorldBoundsRaw(hit).Center, app.Camera,
                        app.ExtendResolution.X, app.ExtendResolution.Y, out var projX, out var projY))
                    proj = $"({projX:F0},{projY:F0})";

                app.AddLog(LogType.Backend, $"{DateTime.UtcNow} [ObjectPicker] {pointerInfo}"
                    + $" dev=({app.DeviceResolution.X},{app.DeviceResolution.Y})"
                    + $" ray={(rayReady ? rayDirection.ToString("F3") : "N/A")}"
                    + $" hit={FocusDisplayName(hit)} dist={(!hit.IsEmpty ? bestDistance.ToString("F2") : "-")} proj={proj}"
                    + $" cam=({app.Camera.Position.ToString("F2")} -> {app.Camera.Target.ToString("F2")}) dpi={app.CompositionScale.X:F3}");
            }
        }

        // 4. While a target is focused, pulse highlight alpha with a triangular wave from 0 to BoxAlpha
        // at uniform speed while keeping the target Alpha unchanged,
        // and fit the highlight to the target world AABB every frame.
        if (!_selected.IsEmpty)
        {
            var period = MathF.Max(PulsePeriod, 1e-3f);
            float phase = (clock % period) / period;
            float triangle = 1f - MathF.Abs(2f * phase - 1f);

            // Use the raw bounds rather than the 1.5x culling bounds.
            // Animated models apply AnimatedBoundsScale to LocalBounds,
            // and drawing a box from that would make it 50 percent larger than the actual object,
            // leaving about 0.25 body height of empty space above and below in the 2026-08 Robot measurement.
            // TryPick and TryPickInstance already use the raw bounds,
            // so the fitted drawing stays sourced from the same contract:
            // Mesh3DBase uses GetWorldBoundsRaw and instanced targets use GetInstanceWorldBoundsRaw.
            var bounds = GetFocusWorldBoundsRaw(_selected);

            // Unified highlighting is rendered by the target itself.
            // This panel only writes properties, where alpha = BoxAlpha times the triangle wave,
            // and dispatches one of Bounds, Wireframe, or Outline according to Highlight.Style.
            // Face alpha is written uniformly into SurfaceColor.W for pulsing,
            // while EdgeColor and OutlineColor stay solid and do not pulse.
            // Targets consume those properties during their own Update,
            // so this panel must be called after host control Update as part of the engine contract.
            SetHighlight(_selected, true, BoxAlpha * triangle);

            // Board positioning:
            // PosX is the left screen edge of the projected box plus its projected width,
            // meaning the right edge of the projection so the board sits to the right of the box.
            // PosY is the top screen edge of the projected box.
            // The code projects all eight corners and takes min and max values with the same RenderViewProjection,
            // so the mapping stays consistent with picking and rendering even under high DPI.
            // If a corner cannot be projected, such as when it is behind the camera,
            // the board keeps its previous frame position.
            if (app != null)
            {
                var bMin = bounds.Center - bounds.Extents;
                var bMax = bounds.Center + bounds.Extents;

                float sMinX = float.MaxValue, sMaxX = float.MinValue, sMinY = float.MaxValue;

                for (int i = 0; i < 8; i++)
                {
                    var corner = new Vector3(
                        (i & 1) == 0 ? bMin.X : bMax.X,
                        ((i >> 1) & 1) == 0 ? bMin.Y : bMax.Y,
                        ((i >> 2) & 1) == 0 ? bMin.Z : bMax.Z);

                    if (Season.Rendering.Picking.ProjectToScreen(corner, app.Camera, app.ExtendResolution.X, app.ExtendResolution.Y, out var sx, out var sy))
                    {
                        if (sx < sMinX) sMinX = sx;
                        if (sx > sMaxX) sMaxX = sx;
                        if (sy < sMinY) sMinY = sy;
                    }
                }

                if (sMinX < float.MaxValue)
                {
                    objectPanel.PosX = sMinX + (sMaxX - sMinX) + 50;
                    
                    var maxX = DeviceServices.BaseApp.ExtendResolution.X - (objectPanel.Width ?? 0) - 10;
                    
                    if (objectPanel.PosX > maxX)
                    {
                        objectPanel.PosX = maxX;
                    }

                    if (objectPanel.PosX < 10)
                    {
                        objectPanel.PosX = 10;
                    }

                    objectPanel.PosY = sMinY;
                    
                    var maxY = DeviceServices.BaseApp.ExtendResolution.Y - (objectPanel.Height ?? 0) - 50;

                    if (objectPanel.PosY > maxY)
                    {
                        objectPanel.PosY = maxY;
                    }

                    if (objectPanel.PosY < 10)
                    {
                        objectPanel.PosY = 10;
                    }
                }
            }

            objectPanel.Alpha = 1f;
        }
        else
        {
            // No focus.
            // The highlight has already been turned off by the focus-switch block above,
            // and it stays off when SameFocus remains true.

            objectPanel.Alpha = 0f;
        }

        objectPanel.Update(time);

        return result;
    }

    /// <summary>Cache for animation names. It is refreshed only when the selected Model changes, since the platform layer generates a fresh list on each call. The cached list is reused by the per-frame display and the popup picker.</summary>
    Model _animationNamesOwner;
    IReadOnlyList<string> _animationNames = Array.Empty<string>();

    internal IReadOnlyList<string> GetAnimationNames(Model model)
    {
        if (!ReferenceEquals(_animationNamesOwner, model))
        {
            _animationNamesOwner = model;
            _animationNames = model?.GetAnimationNames() ?? Array.Empty<string>();
        }

        return _animationNames;
    }

    /// <summary>
    /// Returns the animation row text as current animation name plus index and total count.
    /// Targets without animation, such as Mesh3D or InstancedMesh3D, or targets with an empty animation list, display "-".
    /// For per-instance focus, the value is resolved through the host AnimationNames at instance.AnimationClip.
    /// </summary>
    internal string GetAnimationText(Focus focus)
    {
        if (focus.Instance != null)
        {
            if (focus.Control is not InstancedModel instancedModel)
                return "-";

            var names = instancedModel.AnimationNames;
            if (names.Count == 0)
                return "-";

            int index = Math.Clamp(focus.Instance.AnimationClip, 0, names.Count - 1);
            var current = names[index];

            return $"{(string.IsNullOrEmpty(current) ? (index + 1).ToString() : current)} ({index + 1}/{names.Count})";
        }

        if (focus.Control is not Model model)
            return "-";

        var modelNames = GetAnimationNames(model);
        if (modelNames.Count == 0)
            return "-";

        var currentName = model.GetCurrentAnimationName();

        int found = -1;
        if (currentName != null)
            for (int i = 0; i < modelNames.Count; i++)
                if (modelNames[i] == currentName) { found = i; break; }

        return $"{(string.IsNullOrEmpty(currentName) ? (found + 1).ToString() : currentName)} ({found + 1}/{modelNames.Count})";
    }

    /// <summary>World bounds of the current focus using raw bounds. Mesh3DBase uses GetWorldBoundsRaw and per-instance focus uses GetInstanceWorldBoundsRaw.</summary>
    static Bounds3D GetFocusWorldBoundsRaw(Focus focus)
        => focus.Instance == null
            ? ((Mesh3DBase)focus.Control).GetWorldBoundsRaw()
            : ((InstancedMesh3DBase)focus.Control).GetInstanceWorldBoundsRaw(focus.Instance);

    /// <summary>Writes selection state. Whole-object focus writes Mesh3DBase.Selected, while per-instance focus writes MeshInstanceTransform.Selected.</summary>
    static void SetSelected(Focus focus, bool value)
    {
        if (focus.Instance == null)
            ((Mesh3DBase)focus.Control).Selected = value;
        else
            focus.Instance.Selected = value;
    }

    /// <summary>
    /// Writes unified highlight properties by dispatching to one of the Bounds, Wireframe,
    /// or Outline channels based on the focus's own <see cref="Highlight.Style"/>.
    /// Whole-object focus writes Mesh3DBase.Highlight, while per-instance focus writes MeshInstanceTransform.Highlight.
    /// Colors are taken from the target's own SurfaceColor, EdgeColor, and OutlineColor.
    /// The panel-level fallback colors are applied only when the target is still using default, uncustomized values,
    /// and runtime color changes on the target take effect immediately.
    /// Face alpha is overwritten every frame by the pulsing value, where SurfaceColor.W = BoxAlpha times the triangle wave.
    /// When w = 0, the Wireframe channel automatically degrades to pure edges.
    /// EdgeColor and OutlineColor remain solid and do not pulse.
    /// None means all three channels are cleared and nothing else is written,
    /// so there is no effect beyond Selected state and the property panel.
    /// The method always clears Bounds, Wireframe, and Outline first,
    /// preventing leftovers during release, focus switch, or runtime style changes.
    /// When value is false, only clearing is performed.
    /// </summary>
    void SetHighlight(Focus focus, bool value, float alpha)
    {
        if (focus.IsEmpty)
            return;

        if (focus.Instance == null)
        {
            var control = (Mesh3DBase)focus.Control;
            control.Highlight.Bounds = false;
            control.Highlight.Wireframe = false;
            control.Highlight.Outline = false;
            if (!value)
                return;

            // None is a terminal cleared state.
            // No face pulse, edge color, or outline color is written,
            // so selection has no visual effect beyond Selected state and the property panel.
            // Large background bodies such as grass should explicitly use this style.
            if (control.Highlight.Style == HighlightStyle.None)
                return;

            // Colors come from the target itself.
            // Explicitly assigned SurfaceColor, EdgeColor, and OutlineColor take priority,
            // and runtime changes apply immediately.
            // Panel-level fallback colors are used only while the target is still uncustomized.
            // This panel only overwrites the pulsing face alpha in W and never touches the target RGB channels.
            var surface = control.Highlight.SurfaceColor;
            if (surface.X == 1f && surface.Y == 1f && surface.Z == 1f)
                surface = SurfaceColor;
            surface.W = alpha;
            control.Highlight.SurfaceColor = surface;
            if (control.Highlight.EdgeColor == DefaultHighlightEdgeColor)
                control.Highlight.EdgeColor = EdgeColor;
            if (control.Highlight.Style == HighlightStyle.Outline)
            {
                if (control.Highlight.OutlineColor == DefaultHighlightOutlineColor)
                    control.Highlight.OutlineColor = OutlineColor;
                control.Highlight.Outline = true;
            }
            else if (control.Highlight.Style == HighlightStyle.Wireframe)
                control.Highlight.Wireframe = true;
            else
                control.Highlight.Bounds = true;
            return;
        }

        var instance = focus.Instance;
        instance.Highlight.Bounds = false;
        instance.Highlight.Wireframe = false;
        instance.Highlight.Outline = false;
        if (!value)
            return;

        // Resolve style for instanced focus.
        // An explicit instance style takes priority.
        // If the instance still uses the default Bounds style, it inherits the host batch style instead.
        // The host only propagates Highlight into instances that already exist at the time of replacement through CopyFrom.
        // If instances are added afterward, as in Mountains where Build sets the style first and Update plus SyncInstances
        // add instances frame by frame, the instance side remains at the default value.
        // In that case, this fallback reads from the host.
        var style = instance.Highlight.Style;
        if (style == HighlightStyle.Bounds
            && focus.Control is InstancedMesh3DBase host && host.Highlight.Style != HighlightStyle.Bounds)
            style = host.Highlight.Style;

        // None is already a fully cleared terminal state, identical to the whole-object path,
        // so no color or pulse is written.
        if (style == HighlightStyle.None)
            return;

        // Colors come from the instance itself, exactly like the whole-object path.
        // Panel-level fallback colors are used only while the instance still keeps default, uncustomized values.
        // This panel only overwrites the pulsing face alpha in W and never touches the instance RGB channels.
        var face = instance.Highlight.SurfaceColor;
        if (face.X == 1f && face.Y == 1f && face.Z == 1f)
            face = SurfaceColor;
        face.W = alpha;
        instance.Highlight.SurfaceColor = face;
        if (instance.Highlight.EdgeColor == DefaultHighlightEdgeColor)
            instance.Highlight.EdgeColor = EdgeColor;
        if (style == HighlightStyle.Outline)
        {
            if (instance.Highlight.OutlineColor == DefaultHighlightOutlineColor)
                instance.Highlight.OutlineColor = OutlineColor;
            instance.Highlight.Outline = true;
        }
        else if (style == HighlightStyle.Wireframe)
            instance.Highlight.Wireframe = true;
        else
            instance.Highlight.Bounds = true;
    }

    /// <summary>Checks whether a focus is still alive. The host must not be disposed, and the instance must still be present in the host Instances list, since runtime additions or removals can invalidate a locked reference.</summary>
    static bool IsFocusAlive(Focus focus)
        => focus.Control != null && !focus.Control.IsDisposed
        && (focus.Instance == null
            || (focus.Control is InstancedMesh3DBase host && host.Instances.Contains(focus.Instance)));

    static bool SameFocus(in Focus a, in Focus b)
        => ReferenceEquals(a.Control, b.Control) && ReferenceEquals(a.Instance, b.Instance);

    /// <summary>Display name used in logs. Whole-object focus uses Control.Name. Instance focus appends "host#instanceIndex". IndexOf is used only for diagnostics and instance counts are small.</summary>
    static string FocusDisplayName(Focus focus)
    {
        if (focus.IsEmpty)
            return "null";

        if (focus.Instance == null)
            return focus.Control.Name;

        var host = (InstancedMesh3DBase)focus.Control;
        return $"{host.Name}#{host.Instances.IndexOf(focus.Instance)}";
    }

    /// <summary>
    /// Picking focus.
    /// It may represent a whole Mesh3DBase target, where Instance is null,
    /// or a single instance inside InstancedMesh3DBase, where Instance is non-null and Control is the host.
    /// default means an empty focus with no hit and no lock.
    /// </summary>
    public readonly struct Focus
    {
        /// <summary>Host of the hit target: Mesh3DBase for a whole-object hit, or InstancedMesh3DBase for a per-instance hit.</summary>
        public readonly Control Control;

        /// <summary>Instance hit in a per-instance pick. Null means the hit is a whole-object target.</summary>
        public readonly MeshInstanceTransform Instance;

        public Focus(Control control, MeshInstanceTransform instance)
        {
            Control = control;
            Instance = instance;
        }

        public bool IsEmpty => Control == null;
    }

    /// <summary>
    /// Turns off the current focus highlight before disposal by clearing target-side highlight properties.
    /// Highlighting is rendered by the target itself and this panel owns no GPU resources.
    /// Focus-switch and focus-clear paths already call <see cref="SetHighlight"/> synchronously,
    /// and this is a final safety path for destruction order where the host may outlive the panel or vice versa.
    /// </summary>
    public override void Dispose()
    {
        if (!_selected.IsEmpty)
            SetHighlight(_selected, false, 0f);
        base.Dispose();
    }
}

public class ObjectPanel : Panel
{
    public override bool MouseOver
    {
        get
        {
            return board.MouseOver;
        }
    }

    ObjectPicker objectPicker;

    Shape board;

    Shape lineLeft, lineTop, lineRight, lineDown;

    Texts textsID, textsName, textsPosX, textsPosY, textsPosZ, textsWidth, textsHeight, textsDepth, textsRotation, textsAnimation;

    Input inputID, inputName, inputPosX, inputPosY, inputPosZ, inputWidth, inputHeight, inputDepth, inputRotation, inputAnimation;

    SimplePicker simplePicker;

    // Focus accessors: property-panel reads and writes are dispatched uniformly here
    // for whole-object Mesh3DBase focus versus per-instance MeshInstanceTransform focus.

    static long GetID(Focus focus) => focus.Instance == null ? ((Mesh3DBase)focus.Control).ID : focus.Instance.ID;
    static string GetName(Focus focus) => focus.Instance == null ? ((Mesh3DBase)focus.Control).Name : focus.Instance.Name;
    static float GetPosX(Focus focus) => focus.Instance == null ? ((Mesh3DBase)focus.Control).PosX : focus.Instance.PosX;
    static float GetPosY(Focus focus) => focus.Instance == null ? ((Mesh3DBase)focus.Control).PosY : focus.Instance.PosY;
    static float GetPosZ(Focus focus) => focus.Instance == null ? ((Mesh3DBase)focus.Control).PosZ : focus.Instance.PosZ;
    static float GetWidth(Focus focus) => focus.Instance == null ? (float)((Mesh3DBase)focus.Control).Width : focus.Instance.Width;
    static float GetHeight(Focus focus) => focus.Instance == null ? (float)((Mesh3DBase)focus.Control).Height : focus.Instance.Height;
    static float GetDepth(Focus focus) => focus.Instance == null ? (float)((Mesh3DBase)focus.Control).Depth : (float)focus.Instance.Depth;

    static void SetPosX(Focus focus, float value) { if (focus.Instance == null) ((Mesh3DBase)focus.Control).PosX = value; else focus.Instance.PosX = value; }
    static void SetPosY(Focus focus, float value) { if (focus.Instance == null) ((Mesh3DBase)focus.Control).PosY = value; else focus.Instance.PosY = value; }
    static void SetPosZ(Focus focus, float value) { if (focus.Instance == null) ((Mesh3DBase)focus.Control).PosZ = value; else focus.Instance.PosZ = value; }
    static void SetWidth(Focus focus, float value) { if (focus.Instance == null) ((Mesh3DBase)focus.Control).Width = value; else focus.Instance.Width = value; }
    static void SetHeight(Focus focus, float value) { if (focus.Instance == null) ((Mesh3DBase)focus.Control).Height = value; else focus.Instance.Height = value; }
    static void SetDepth(Focus focus, float value) { if (focus.Instance == null) ((Mesh3DBase)focus.Control).Depth = value; else focus.Instance.Depth = value; }

    /// <summary>Reads the current yaw angle of the focus in degrees, normalized to [0, 360). Model stores direct radians around the Y axis, while Mesh3D and instances reconstruct yaw from their quaternion.</summary>
    static float GetYawDegrees(Focus focus)
    {
        float radians;

        if (focus.Instance != null)
            radians = YawFromQuaternion(focus.Instance.Rotation);
        else
            radians = focus.Control switch
            {
                Model model => model.Rotation,
                Mesh3D mesh => YawFromQuaternion(mesh.Rotation),
                _ => 0f,
            };

        return NormalizeDegrees(radians * 180f / MathF.PI);
    }

    /// <summary>Extracts yaw from a quaternion by rotating local +X and applying atan2(-z, x) to recover the Y-axis angle. This is exact for pure Y rotations.</summary>
    static float YawFromQuaternion(Quaternion q)
    {
        var x = Vector3.TransformNormal(Vector3.UnitX, Matrix4x4.CreateFromQuaternion(q));
        return MathF.Atan2(-x.Z, x.X);
    }

    /// <summary>
    /// Applies rotation input in degrees.
    /// Under the unified positioning contract, the rotation pivot equals the anchor,
    /// which is the geometric center of the raw bounding box.
    /// The anchor is always mapped to PosX, PosY, and PosZ by BuildWorldMatrix or BuildInstanceMatrix,
    /// so the target rotates around its own center and rotation does not alter anchor position or size.
    /// No compensation algorithm is required.
    /// One full turn is 360 degrees or 2π radians.
    /// The input is normalized to [0, 360) before writing:
    /// Model.Rotation stores radians around the Y axis, while Mesh3D and instances are converted to Y-axis quaternions.
    /// </summary>
    static void ApplyRotationDegrees(Focus focus, float degrees)
    {
        if (focus.IsEmpty)
            return;

        float radians = NormalizeDegrees(degrees) * MathF.PI / 180f;

        if (focus.Instance != null)
        {
            focus.Instance.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, radians);
            return;
        }

        switch (focus.Control)
        {
            case Model model: model.Rotation = radians; break;
            case Mesh3D mesh: mesh.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, radians); break;
        }
    }

    static float NormalizeDegrees(float degrees) => ((degrees % 360f) + 360f) % 360f;

    public ObjectPanel(ObjectPicker picker)
    {
        objectPicker = picker;

        board = new Shape()
        {
            Alpha = 0.85f,
            Type = ShapeType.Dot,
            Color = new Season.Basic.Color(200, 200, 200, 255)
        };
        AddControl(board);

        lineLeft = new Shape()
        {
            Alpha = 0.85f,
            Type = ShapeType.Dot,
            Color = Season.Basic.Colors.DarkRed
        };
        AddControl(lineLeft);

        lineTop = new Shape()
        {
            Alpha = 0.85f,
            Type = ShapeType.Dot,
            Color = Season.Basic.Colors.DarkRed
        };
        AddControl(lineTop);

        lineRight = new Shape()
        {
            Alpha = 0.85f,
            Type = ShapeType.Dot,
            Color = Season.Basic.Colors.DarkRed
        };
        AddControl(lineRight);

        lineDown = new Shape()
        {
            Alpha = 0.85f,
            Type = ShapeType.Dot,
            Color = Season.Basic.Colors.DarkRed
        };
        AddControl(lineDown);

        textsID = new Texts()
        {
            Content = "ID",
            Color = Season.Basic.Colors.Black,
            Scale = Vector2.One * 0.85f
        };
        AddControl(textsID);

        textsName = new Texts()
        {
            Content = "Name",
            Color = Season.Basic.Colors.Black,
            Scale = Vector2.One * 0.85f
        };
        AddControl(textsName);

        textsPosX = new Texts()
        {
            Content = "PosX",
            Color = Season.Basic.Colors.Black,
            Scale = Vector2.One * 0.85f
        };
        AddControl(textsPosX);

        textsPosY = new Texts()
        {
            Content = "PosY",
            Color = Season.Basic.Colors.Black,
            Scale = Vector2.One * 0.85f
        };
        AddControl(textsPosY);

        textsPosZ = new Texts()
        {
            Content = "PosZ",
            Color = Season.Basic.Colors.Black,
            Scale = Vector2.One * 0.85f
        };
        AddControl(textsPosZ);

        textsWidth = new Texts()
        {
            Content = "Width",
            Color = Season.Basic.Colors.Black,
            Scale = Vector2.One * 0.85f
        };
        AddControl(textsWidth);

        textsHeight = new Texts()
        {
            Content = "Height",
            Color = Season.Basic.Colors.Black,
            Scale = Vector2.One * 0.85f
        };
        AddControl(textsHeight);

        textsDepth = new Texts()
        {
            Content = "Depth",
            Color = Season.Basic.Colors.Black,
            Scale = Vector2.One * 0.85f
        };
        AddControl(textsDepth);

        textsRotation = new Texts()
        {
            Content = "Rotation",
            Color = Season.Basic.Colors.Black,
            Scale = Vector2.One * 0.85f
        };
        AddControl(textsRotation);

        textsAnimation = new Texts()
        {
            Content = "Animation",
            Color = Season.Basic.Colors.Black,
            Scale = Vector2.One * 0.85f
        };
        AddControl(textsAnimation);

        inputID = new Input()
        {
            Abbreviate = true,
            Enable = false
        };
        AddPanel(inputID);

        inputName = new Input()
        {
            Abbreviate = true,
            Enable = false
        };
        AddPanel(inputName);

        inputPosX = new Input()
        {
            Abbreviate = true,
            OnAction = async () =>
            {
                var result = await DeviceServices.Dialog.ShowKeyboard("PosX".Translate(), "", new string[] { "OK".Translate(), "Cancel".Translate() }, inputPosX.Text);
                if (result is not null && !objectPicker.SelectedFocus.IsEmpty)
                {
                    inputPosX.Text = result;
                    SetPosX(objectPicker.SelectedFocus, float.Parse(result));
                }
            }
        };
        AddPanel(inputPosX);

        inputPosY = new Input()
        {
            Abbreviate = true,
            OnAction = async () =>
            {
                var result = await DeviceServices.Dialog.ShowKeyboard("PosY".Translate(), "", new string[] { "OK".Translate(), "Cancel".Translate() }, inputPosY.Text);
                if (result is not null && !objectPicker.SelectedFocus.IsEmpty)
                {
                    inputPosY.Text = result;
                    SetPosY(objectPicker.SelectedFocus, float.Parse(result));
                }
            }
        };
        AddPanel(inputPosY);

        inputPosZ = new Input()
        {
            Abbreviate = true,
            OnAction = async () =>
            {
                var result = await DeviceServices.Dialog.ShowKeyboard("PosZ".Translate(), "", new string[] { "OK".Translate(), "Cancel".Translate() }, inputPosZ.Text);
                if (result is not null && !objectPicker.SelectedFocus.IsEmpty)
                {
                    inputPosZ.Text = result;
                    SetPosZ(objectPicker.SelectedFocus, float.Parse(result));
                }
            }
        };
        AddPanel(inputPosZ);

        inputWidth = new Input()
        {
            Abbreviate = true,
            OnAction = async () =>
            {
                var result = await DeviceServices.Dialog.ShowKeyboard("Width".Translate(), "", new string[] { "OK".Translate(), "Cancel".Translate() }, inputWidth.Text);
                if (result is not null && !objectPicker.SelectedFocus.IsEmpty)
                {
                    inputWidth.Text = result;
                    SetWidth(objectPicker.SelectedFocus, float.Parse(result));
                }
            }
        };
        AddPanel(inputWidth);

        inputHeight = new Input()
        {
            Abbreviate = true,
            OnAction = async () =>
            {
                var result = await DeviceServices.Dialog.ShowKeyboard("Height".Translate(), "", new string[] { "OK".Translate(), "Cancel".Translate() }, inputHeight.Text);
                if (result is not null && !objectPicker.SelectedFocus.IsEmpty)
                {
                    inputHeight.Text = result;
                    SetHeight(objectPicker.SelectedFocus, float.Parse(result));
                }
            }
        };
        AddPanel(inputHeight);

        inputDepth = new Input()
        {
            Abbreviate = true,
            OnAction = async () =>
            {
                var result = await DeviceServices.Dialog.ShowKeyboard("Depth".Translate(), "", new string[] { "OK".Translate(), "Cancel".Translate() }, inputDepth.Text);
                if (result is not null && !objectPicker.SelectedFocus.IsEmpty)
                {
                    inputDepth.Text = result;
                    SetDepth(objectPicker.SelectedFocus, float.Parse(result));
                }
            }
        };
        AddPanel(inputDepth);

        inputRotation = new Input()
        {
            Abbreviate = true,
            OnAction = async () =>
            {
                var result = await DeviceServices.Dialog.ShowKeyboard("Rotation".Translate(), "", new string[] { "OK".Translate(), "Cancel".Translate() }, inputRotation.Text);
                if (result is not null && !objectPicker.SelectedFocus.IsEmpty && float.TryParse(result, out var degrees))
                {
                    inputRotation.Text = result;
                    ApplyRotationDegrees(objectPicker.SelectedFocus, degrees);
                }
            }
        };
        AddPanel(inputRotation);

        inputAnimation = new Input()
        {
            Abbreviate = true,
            OnAction = () =>
            {
                var focus = objectPicker.SelectedFocus;
                if (focus.IsEmpty)
                    return;

                // Animation-list source:
                // Model uses the platform animation-name list and selection triggers PlayAnimation.
                // InstancedModel instances use the host AnimationNames and selection writes instance.AnimationClip.
                // Mesh3D and InstancedMesh3D have no animation and therefore return immediately,
                // leaving the row text as "-".
                IReadOnlyList<string> names;
                Action<int> apply;

                if (focus.Instance != null)
                {
                    if (focus.Control is not InstancedModel instancedModel)
                        return;

                    names = instancedModel.AnimationNames;
                    apply = index => focus.Instance.AnimationClip = index;
                }
                else
                {
                    if (focus.Control is not Model model)
                        return;

                    names = objectPicker.GetAnimationNames(model);
                    apply = index => inputAnimation.Text = model.PlayAnimation(names[index]).NullToString();
                }

                if (names.Count == 0)
                    return;

                var sources = names.Select((name, i) => new Season.Entities.EData()
                {
                    Key = name,
                    Title = string.IsNullOrEmpty(name) ? $"Animation {i + 1}" : name,
                    Image = null,
                    Desc = null
                }).ToList();

                var result = new List<Season.Entities.EData> { };

                simplePicker = new Season.Panels.SimplePicker(sources, result)
                {
                    OnSelect = () =>
                    {
                        var picked = simplePicker.Results?.Count > 0 ? simplePicker.Results[0] : null;

                        if (picked != null)
                        {
                            for (int i = 0; i < names.Count; i++)
                                if (names[i] == picked.Key)
                                {
                                    apply(i);
                                    break;
                                }
                        }

                        simplePicker.OnClose?.Invoke();
                    },
                    OnClose = () =>
                    {
                        Panels.Remove(simplePicker);
                        simplePicker.Dispose();
                        simplePicker = null;
                    }
                };
                AddPanel(simplePicker);
            }
        };
        AddPanel(inputAnimation);
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        if (simplePicker != null)
        {
            if (simplePicker.Update(time, alpha: alpha, posX: (int)inputAnimation.PosX, posY: (int)(inputAnimation.PosY + inputAnimation.Height) + 20))
            {
                result = true;
            }
        }

        textsID.Alpha = textsName.Alpha = textsPosX.Alpha = textsPosY.Alpha = textsPosZ.Alpha = textsWidth.Alpha = textsHeight.Alpha = textsDepth.Alpha = textsRotation.Alpha = textsAnimation.Alpha = Alpha;
        inputID.Alpha = inputName.Alpha = inputPosX.Alpha = inputPosY.Alpha = inputPosZ.Alpha = inputWidth.Alpha = inputHeight.Alpha = inputDepth.Alpha = inputRotation.Alpha = inputAnimation.Alpha = Alpha;

        // For an empty focus, where the pointer moves into empty sky with no hit and no locked selection,
        // only hide the panel and do not read focus properties.
        // Accessors such as GetID would throw on default(Focus) because Control is null.
        // This guard was previously missed when ObjectPanel was separated.
        // Keyboard callbacks already protect themselves with IsEmpty, and this completes the guard on the read path.
        var focus = objectPicker.SelectedFocus;
        if (!focus.IsEmpty)
        {
            inputID.Text = GetID(focus).ToString();
            inputName.Text = GetName(focus);
            inputPosX.Text = GetPosX(focus).ToString();
            inputPosY.Text = GetPosY(focus).ToString();
            inputPosZ.Text = GetPosZ(focus).ToString();
            inputWidth.Text = GetWidth(focus).ToString();
            inputHeight.Text = GetHeight(focus).ToString();
            inputDepth.Text = GetDepth(focus).ToString();
            inputRotation.Text = GetYawDegrees(focus).ToString("F1");
            inputAnimation.Text = objectPicker.GetAnimationText(focus);
        }

        var padding = 20; var paddingH = 80;

        Width = 400;

        Height = paddingH * 10 + padding * 2;

        board.Update(time, alpha: Alpha * 0.75f, posX: PosX, posY: PosY, width: Width, height: Height);

        var thick = 2;
        lineLeft.Update(time, alpha: Alpha, board.PosX - thick, board.PosY, thick, board.Height);
        lineTop.Update(time, alpha: Alpha, board.PosX - thick, board.PosY - thick, board.Width + thick * 2, thick);
        lineRight.Update(time, alpha: Alpha, board.PosX + board.Width, board.PosY - thick, 2, 2 + board.Height);
        lineDown.Update(time, alpha: Alpha, board.PosX - thick, board.PosY + board.Height, board.Width + thick * 2, thick);

        textsID.Update(time, posX: PosX + padding, posY: board.PosY + padding);
        textsName.Update(time, posX: PosX + padding, posY: textsID.PosY + paddingH);
        textsPosX.Update(time, posX: PosX + padding, posY: textsName.PosY + paddingH);
        textsPosY.Update(time, posX: PosX + padding, posY: textsPosX.PosY + paddingH);
        textsPosZ.Update(time, posX: PosX + padding, posY: textsPosY.PosY + paddingH);
        textsWidth.Update(time, posX: PosX + padding, posY: textsPosZ.PosY + paddingH);
        textsHeight.Update(time, posX: PosX + padding, posY: textsWidth.PosY + paddingH);
        textsDepth.Update(time, posX: PosX + padding, posY: textsHeight.PosY + paddingH);
        textsRotation.Update(time, posX: PosX + padding, posY: textsDepth.PosY + paddingH);
        textsAnimation.Update(time, posX: PosX + padding, posY: textsRotation.PosY + paddingH);

        var width0 = 170; var inputLeft = 150; var height0 = 70;
        inputID.Color = inputID.MouseOver ? Season.Basic.Colors.Red : Season.Basic.Colors.Black;
        if (inputID.Update(time, posX: (int)textsID.PosX + inputLeft, posY: (int)textsID.PosY, width: width0, height: height0))
        {
            result = true;
        }
        inputName.Color = inputName.MouseOver ? Season.Basic.Colors.Red : Season.Basic.Colors.Black;
        if (inputName.Update(time, posX: (int)textsName.PosX + inputLeft, posY: (int)textsName.PosY, width: width0, height: height0))
        {
            result = true;
        }
        inputPosX.Color = inputPosX.MouseOver ? Season.Basic.Colors.Red : Season.Basic.Colors.Black;
        if (inputPosX.Update(time, posX: (int)textsPosX.PosX + inputLeft, posY: (int)textsPosX.PosY, width: width0, height: height0))
        {
            result = true;
        }
        inputPosY.Color = inputPosY.MouseOver ? Season.Basic.Colors.Red : Season.Basic.Colors.Black;
        if (inputPosY.Update(time, posX: (int)textsPosY.PosX + inputLeft, posY: (int)textsPosY.PosY, width: width0, height: height0))
        {
            result = true;
        }
        inputPosZ.Color = inputPosZ.MouseOver ? Season.Basic.Colors.Red : Season.Basic.Colors.Black;
        if (inputPosZ.Update(time, posX: (int)textsPosZ.PosX + inputLeft, posY: (int)textsPosZ.PosY, width: width0, height: height0))
        {
            result = true;
        }
        inputWidth.Color = inputWidth.MouseOver ? Season.Basic.Colors.Red : Season.Basic.Colors.Black;
        if (inputWidth.Update(time, posX: (int)textsWidth.PosX + inputLeft, posY: (int)textsWidth.PosY, width: width0, height: height0))
        {
            result = true;
        }
        inputHeight.Color = inputHeight.MouseOver ? Season.Basic.Colors.Red : Season.Basic.Colors.Black;
        if (inputHeight.Update(time, posX: (int)textsHeight.PosX + inputLeft, posY: (int)textsHeight.PosY, width: width0, height: height0))
        {
            result = true;
        }
        inputDepth.Color = inputDepth.MouseOver ? Season.Basic.Colors.Red : Season.Basic.Colors.Black;
        if (inputDepth.Update(time, posX: (int)textsDepth.PosX + inputLeft, posY: (int)textsDepth.PosY, width: width0, height: height0))
        {
            result = true;
        }
        inputRotation.Color = inputRotation.MouseOver ? Season.Basic.Colors.Red : Season.Basic.Colors.Black;
        if (inputRotation.Update(time, posX: (int)textsRotation.PosX + inputLeft, posY: (int)textsRotation.PosY, width: width0, height: height0))
        {
            result = true;
        }
        inputAnimation.Color = inputAnimation.MouseOver ? Season.Basic.Colors.Red : Season.Basic.Colors.Black;
        if (inputAnimation.Update(time, posX: (int)textsAnimation.PosX + inputLeft, posY: (int)textsAnimation.PosY, width: width0, height: height0))
        {
            result = true;
        }

        return result;
    }
}
