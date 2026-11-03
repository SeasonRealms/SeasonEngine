// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Numerics;
using System.Runtime.InteropServices;

namespace Season.Controls;

/// <summary>
/// A selectable, placeable, copyable waterfall control.
///
/// It does <b>not draw water</b> itself: the actual water curtain is still computed procedurally by <see cref="RenderPassId.SceneWaterfall"/> from the control points,
/// because that strip needs to sample the current frame's SceneDepth per pixel for the software depth test, which the main shader cannot do (it cannot sample its own attachment).
/// What the control is responsible for is the other half that the water curtain lacks on the "engine object" side:
/// <list type="bullet">
/// <item>an <b>invisible proxy mesh</b> (a coarse mesh likewise expanded from the control points), which carries the bounding box, triangle-level picking, and three kinds of outline highlight;</item>
/// <item>a unified placement model (Pos is the bounding box center, Rotation is the yaw around Y, Width/Height/Depth are per-axis scales);</item>
/// <item>handing the placement matrix to <see cref="WaterfallSheet.Placement"/> every frame, which the water curtain follows in the vertex shader.</item>
/// </list>
///
/// Thus "cannot be selected, cannot be dragged" is no longer because the water curtain is special, but because it is a <see cref="Mesh3D"/> just like a house or a ball:
/// spending the same effort maintaining one placement/picking setup also gets you frustum culling, shadow/GI gating, and the property panel.
///
/// Why the proxy is invisible: the material uses <see cref="SurfaceBlendMode.Opaque"/> while <c>Alpha</c> is set to a tiny value,
/// so the backend sorts it into the Fade bucket; then setting <see cref="Mesh3D.ExcludeFromAo"/> to true
/// makes the Fade bucket not write depth -- otherwise SceneDepth would be written in front of the water curtain, and the water curtain's software depth test would cull the whole thing.
/// Both color and alpha are 0, so the blend result equals the background itself, leaving only those few highlight edges and the shell visible.
///
/// Using Mask + AlphaCutoff=1 to hide it is <b>wrong</b>: the Outline mask shader also clips when alphaMode==1,
/// so the outline would disappear entirely. This constraint is written in <c>Pipeline.PSOutlineMask</c>.
/// </summary>
public sealed class Waterfall : Mesh3D
{
    /// <summary>
    /// The authoring data for the water curtain. The control points are written in <b>reference coordinates</b>: when <see cref="Place"/> has not been called, the reference coordinates are the world coordinates,
    /// so the image of old data (a string of world coordinates + no Placement set) is identical to P1 character for character.
    /// </summary>
    public WaterfallSheet Sheet { get; } = new();

    // Placement input waiting to be applied. Place may come before Load (at which point there is no bounding box yet, so an anchor is meaningless), so store it for now.
    bool _placed;
    Vector3 _placeCenter;
    float _placeYawRadians;
    float _placeScale = 1f;
    Vector3? _placeSize;

    /// <summary>
    /// Creates an empty waterfall: the control points / material / texture name are still defaults, and the shape is filled in by the caller via <see cref="Sheet"/>.
    /// The full entry point that also registers it is <c>App.SpawnWaterfall</c>.
    /// </summary>
    public Waterfall(string name = "Waterfall")
    {
        Name = name;

        // See the class header: a tiny alpha but not 0. DrawMesh3D returns immediately when Alpha == 0,
        // in which case not even the bounding box and highlight would be submitted, and picking would never light up.
        Alpha = 0.001f;

        // The Fade bucket's depth write is decided by this bit; turning it on is equivalent to writing the proxy into SceneDepth,
        // and the water curtain's software depth test would think "the rock is in front" and discard the whole thing.
        ExcludeFromAo = true;
        CastShadows = false;

        // Placement is computed every frame. Before Load it is the identity matrix (anchor, scale, and translation all 0), and after loading completes it still returns to the identity matrix,
        // see the step "Pos = AnchorLocal when not Placed" in ApplyPlacement.
        Sheet.Placement = () => BuildWorldMatrix();

        // The control toggle is the water curtain toggle: after it is disabled on the panel (or Alpha is zeroed out),
        // the SceneWaterfall Pass's predicate no longer holds, saving even the scene-splitting step. Using a probe instead of writing back every frame is because a hidden control happens to have no Update timing.
        Sheet.EnabledProbe = () => Enable && Alpha > 0f;
    }

    /// <summary>An instance that has not yet been Loaded after construction can still Place first: the input is stored and applied when the bounding box is established.</summary>
    public bool Placed => _placed;

    /// <summary>
    /// Places the waterfall at the world center point <paramref name="center"/>, rotating <paramref name="yawDegrees"/> degrees around the Y axis, scaled uniformly by <paramref name="scale"/>.
    ///
    /// <paramref name="center"/> and the PosX/PosY/PosZ in the property panel are <b>the same quantity</b> (the bounding box center),
    /// so the visual fine-tuning workflow is closed-loop: select -> drag or change a number -> copy the numbers from the panel back here to reproduce it.
    /// When this method is not called, the reference coordinates are the world coordinates, equivalent to Place to the bounding box center while keeping the original orientation.
    ///
    /// <paramref name="size"/> is the per-axis target size in meters -- exactly the numbers the panel shows as Width/Height/Depth --
    /// and it overrides the uniform <paramref name="scale"/> axis by axis; a null or non-positive axis falls back to <paramref name="scale"/>.
    /// It exists because the shape cannot absorb anisotropic scaling: the section's half width and thickness are isotropic scalars,
    /// so "15 m wide but 13 m tall" has to live in the placement matrix rather than in the control points.
    /// </summary>
    public Waterfall Place(Vector3 center, float yawDegrees = 0f, float scale = 1f, Vector3? size = null)
    {
        _placed = true;
        _placeCenter = center;
        _placeYawRadians = yawDegrees * MathF.PI / 180f;
        _placeScale = scale;
        _placeSize = size;
        if (HasBounds) ApplyPlacement();
        return this;
    }

    /// <summary>
    /// Copies into an independent new waterfall: the shape, material, texture name, and highlight settings are all copied verbatim, but the placement state is <b>not</b> copied.
    ///
    /// This is done so that "same shape, different position" becomes a single line: the new instance lands by default at the same position as the original (same reference coordinates),
    /// and calling <see cref="Place"/> once right after spreads it out. For the registry limit see <see cref="SceneWaterfalls.MaxActiveWaterfalls"/>.
    ///
    /// The control is only responsible for "copying itself": AddControl, registering in the scene waterfall table, and joining the picking targets all happen at the host (App) layer,
    /// and the full entry point on the engine side is <c>App.SpawnWaterfall</c>, which wraps these three things and one CopyFrom into a single call.
    /// </summary>
    public Waterfall Clone(string? name = null)
    {
        var copy = new Waterfall(name ?? $"{Name} Copy");
        copy.Sheet.CopyFrom(Sheet);
        copy.Highlight.CopyFrom(Highlight);
        return copy;
    }

    bool HasBounds => LocalBoundsRaw.Extents != Vector3.Zero;

    /// <summary>
    /// First builds the proxy mesh from the control points, then hands it to the base class: the bounding box is aggregated from the Surface vertices, and an empty collection would leave both picking and highlighting with nothing;
    /// only after the bounding box is established can the <see cref="Place"/> input be applied.
    /// </summary>
    /// <returns>Whether the base class load succeeded; the proxy's bounding box and placement are in place regardless of success or failure.</returns>
    public override async Task<bool> Load()
    {
        // The proxy must come before base.Load: the bounding box is aggregated from the Surface vertices, and an empty collection would leave both picking and highlighting with nothing.
        BuildProxy();

        bool result = await base.Load();

        // The bounding box is established, so the anchor is meaningful.
        ApplyPlacement();

        return result;
    }

    /// <summary>
    /// Applies the placement input into the unified placement model's fields. When not Placed, Pos directly takes the anchor,
    /// so BuildWorldMatrix degenerates into the identity matrix -- the reference coordinates are output as-is, and the image of old data is unchanged.
    /// </summary>
    void ApplyPlacement()
    {
        if (!HasBounds) return;

        // Set rotation and scale first so that AnchorWorldOffset is the current frame's value; Pos is computed from it together with the center point,
        // reversing the order would make Pos differ by an offset that varies with scale.
        if (_placed)
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, _placeYawRadians);

        var local = LocalSize;
        float scale = _placed ? _placeScale : 1f;
        // Per-axis target size wins when it is a usable number; otherwise the uniform scale decides. See Place.
        Width = Axis(_placeSize?.X, local.X * scale);
        Height = Axis(_placeSize?.Y, local.Y * scale);
        Depth = Axis(_placeSize?.Z, local.Z * scale);

        var center = _placed ? _placeCenter : AnchorLocal;
        PosX = center.X;
        PosY = center.Y;
        PosZ = center.Z;
    }

    /// <summary>Per-axis target size when it is a usable positive number, otherwise the uniform-scale fallback.</summary>
    static float Axis(float? target, float fallback) => target is > 0f ? target.Value : fallback;

    /// <summary>
    /// Expands a coarse mesh from the control points as the proxy.
    ///
    /// The row count matches the shader ((control point count - 1) x Subdiv + 1), the column count also matches, and the meaning of the columns is likewise
    /// the <b>ring parameter around the section</b>: each row goes a full loop via <see cref="WaterfallMath.Section"/> (start and end coinciding),
    /// so the proxy and the actually drawn closed water body are the same surface. Row midpoints go via <see cref="WaterfallMath.SampleRow"/>
    /// (word-for-word identical to the HLSL Catmull-Rom), and the section formula is also word-for-word identical to the VS.
    /// With the geometry coinciding point by point, picking needs no "roughly inside this box" tolerance, and the bounding box can enclose the thickness.
    ///
    /// The row count does not follow subsequent modifications to <see cref="WaterfallSheet.Points"/>: the control points are authoring data, so to change the shape rebuild the instance
    /// (or Clone then modify), because the vertex buffer is uploaded only once at Load.
    /// </summary>
    void BuildProxy()
    {
        Surfaces.Clear();

        int count = Math.Clamp(Sheet.Points.Count, 0, WaterfallMath.MaxControlPoints);
        if (count < 2) return;

        int columns = Math.Clamp(Sheet.Columns, 2, WaterfallMath.MaxColumns);
        int subdiv = Math.Clamp(Sheet.Subdiv, 1, WaterfallMath.MaxSubdiv);
        int rows = (count - 1) * subdiv + 1;

        var points = CollectionsMarshal.AsSpan(Sheet.Points);
        var vertices = new Vertex[rows * columns];
        var indices = new ushort[(rows - 1) * (columns - 1) * 6];

        int vertexIndex = 0;
        for (int row = 0; row < rows; row++)
        {
            var line = WaterfallMath.SampleRow(points, row / (float)subdiv);
            float v = row / (float)(rows - 1);

            for (int column = 0; column < columns; column++)
            {
                float fu = column / (float)(columns - 1);
                var section = WaterfallMath.Section(line, Sheet.Thickness, Sheet.Bow, fu);

                vertices[vertexIndex++] = new Vertex
                {
                    Position = section.Position,
                    // The proxy does not participate in lighting; the normal is only so the backend has valid interpolated data.
                    Normal = section.Normal,
                    TexCoord = new Vector2(section.U, v),
                };
            }
        }

        int index = 0;
        for (int row = 0; row < rows - 1; row++)
        {
            for (int column = 0; column < columns - 1; column++)
            {
                int v00 = row * columns + column;
                int v10 = v00 + columns;
                indices[index++] = (ushort)v00;
                indices[index++] = (ushort)v10;
                indices[index++] = (ushort)(v10 + 1);
                indices[index++] = (ushort)v00;
                indices[index++] = (ushort)(v10 + 1);
                indices[index++] = (ushort)(v00 + 1);
            }
        }

        Surfaces.Add(new Surface
        {
            Vertices = vertices,
            Indices = indices,
            // All-zero color and alpha: the Fade bucket's blend result equals the background itself, so this mesh draws nothing.
            // Highlight (bounding box / wireframe / outline) is submitted independently by the backend and does not read the color here, so it is unaffected.
            BaseColor = Vector4.Zero,
            Mode = SurfaceBlendMode.Opaque,
            DoubleSided = true,
            Unlit = true,
        });
    }
}
