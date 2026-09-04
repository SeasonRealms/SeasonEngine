// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// Unified highlight configuration as a composed object.
/// Bounds boxes and wireframe shells share the same style, switches, dual colors, and edge width,
/// while Outline2D uses an independent outline color and width through <see cref="OutlineColor"/> and <see cref="OutlineWidth"/>.
/// <see cref="Mesh3DBase"/>, <see cref="MeshInstanceTransform"/>, and <see cref="InstancedMesh3DBase"/> each hold a single <see cref="Highlight"/> property,
/// which makes whole-object assignment and cascading propagation straightforward.
/// Presentation is dispatched by ObjectPicker through <see cref="Style"/>, see <see cref="Season.Panels.HighlightStyle"/>.
/// Highlight primitives are lazily built and pooled by the DX backend, and they are independent from the host object's overall alpha chain.
/// The surface alpha is <see cref="SurfaceColor"/>.W and may pulse frame by frame without affecting the model's overall alpha.
/// All settings can be toggled or recolored at runtime, and edge width can be changed at any time.
/// When selected, colors still come from this configuration; ObjectPicker only overwrites the pulsing value in <see cref="SurfaceColor"/>.W and leaves RGB untouched.
/// </summary>
public sealed class Highlight
{
    /// <summary>Presentation style for hover and selection.
    /// ObjectPicker dispatches into the corresponding boolean channels according to this value, see <see cref="Season.Panels.HighlightStyle"/>.
    /// None means no visual effect at all, only selection state and the property panel.
    /// Default is <see cref="Season.Panels.HighlightStyle.Bounds"/>.</summary>
    public HighlightStyle Style { get; set; } = HighlightStyle.Bounds;

    /// <summary>Whether to draw a surface-conforming highlight, made of a translucent shell using SurfaceColor plus solid edge strips using EdgeColor.
    /// When SurfaceColor.w=0, it automatically degenerates into edges only.
    /// Default is false and it may be switched at runtime.</summary>
    public bool Wireframe { get; set; }

    /// <summary>When true, draws the world-space AABB bounds box with separate surface and edge colors from SurfaceColor and EdgeColor. Default is false.</summary>
    public bool Bounds { get; set; }

    /// <summary>Highlight surface color in RGBA.
    /// w controls translucency and can be modified every frame to create a pulse effect.
    /// When w=0, Wireframe automatically renders edges only.
    /// Default is white at 30% opacity.
    /// During selected highlighting, W is overridden by ObjectPicker with a pulsing value, BoxAlpha multiplied by a triangle wave.</summary>
    public Vector4 SurfaceColor { get; set; } = new Vector4(1f, 1f, 1f, 0.3f);

    /// <summary>Highlight edge color in RGBA, fully opaque and not affected by surface-alpha pulsing. Default is orange.</summary>
    public Vector4 EdgeColor { get; set; } = new Vector4(1f, 0.6f, 0.1f, 1f);

    /// <summary>
    /// Enables screen-space outer-outline highlighting when true.
    /// This is independent from the Bounds and Wireframe channels and uses the shared
    /// <see cref="OutlineColor"/> and <see cref="OutlineWidth"/> settings. Default is false.
    /// </summary>
    public bool Outline { get; set; }

    /// <summary>Screen-space outline color, as solid RGBA. Independent from EdgeColor and may coexist with the wireframe shell in the same frame. Default is bright gold.</summary>
    public Vector4 OutlineColor { get; set; } = new Vector4(1f, 0.84f, 0.25f, 1f);

    /// <summary>Screen-space outline width in screen pixels. It stays constant at the near plane and does not scale with model distance. Default is 2 pixels.</summary>
    public float OutlineWidth { get; set; } = 3f;

    /// <summary>Width of the shell edge strips, expressed relative to model size.
    /// 0.005 means 0.5% of the model's largest local dimension, so total world-space edge width is approximately
    /// 2× this value × the model's largest world dimension, consistent across assets and sizes, and it scales automatically after model resize.
    /// The same value is also used as the shell's outward expansion thickness.
    /// Default is 0.005.
    /// It is read when shell and edge geometry are built lazily, and runtime changes automatically release and rebuild geometry with the new width, taking effect immediately.
    /// Instanced controls share one template shell geometry and use the host value, so per-instance widths are not supported.</summary>
    public float EdgeWidth { get; set; } = 0.002f; //0.005f;

    /// <summary>Copies all fields from other as a whole.
    /// Used for cascading updates: when the host explicitly replaces its Highlight, the full configuration is propagated to existing instances.
    /// Instances remain independent objects and do not share references, so they can still be overridden afterward.</summary>
    public void CopyFrom(Highlight other)
    {
        Style = other.Style;
        Wireframe = other.Wireframe;
        Bounds = other.Bounds;
        SurfaceColor = other.SurfaceColor;
        EdgeColor = other.EdgeColor;
        EdgeWidth = other.EdgeWidth;
        Outline = other.Outline;
        OutlineColor = other.OutlineColor;
        OutlineWidth = other.OutlineWidth;
    }
}
