// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Drawables;

/// <summary>
/// The parts of a model that a <see cref="Season.Controls.Model"/> control drives from outside the asset: the instance
/// overrides applied when a shared template is cloned into a control, and the per-part color overrides the control
/// carries.
///
/// All of it is backend independent. <c>ApplyInstanceMaterialOverrides</c>, <c>ApplyPartColorOverride</c> and
/// <c>SyncPartColors</c> lived in <c>DXModel</c>, <c>VKModel</c> and <c>MTLModel</c> as three copies that differed only
/// in how each backend reached its material block - <c>Unsafe.Read/Write</c> over a mapped pointer, or
/// <c>ReadStruct/WriteStruct</c> over an MTL buffer. <see cref="MaterialSlot"/> is exactly that access path expressed
/// once, so the bodies collapse into one.
/// </summary>
internal abstract partial class PrimitiveGroupCore<TPrim, TMat, TTex>
    where TPrim : PrimitiveCpuData<TMat, TTex>, IDisposable
    where TMat : unmanaged, IMaterialFields
    where TTex : class
{
    /// <summary>Last <c>Model.PartColorVersion</c> folded into the material blocks. Starts at int.MinValue, a value no
    /// model can report, so the first Update always syncs - which also bakes in a set that was made between the
    /// load-time ProcessMaterial/ClonePrimitiveData pass and the first frame.</summary>
    int _appliedPartColorVersion = int.MinValue;

    /// <summary>
    /// Folds a control's material-level settings into a freshly cloned primitive: the <c>MaterialColor</c> tint
    /// multiplies the asset's base color, <c>Unlit</c> picks the render mode, and the skinning route is derived from
    /// whether the source node actually owns a skin. <c>OriginalBaseColorAlpha</c> is captured after the tint so that
    /// the alpha chain scales the color the control asked for, not the authored one.
    /// </summary>
    protected static void ApplyInstanceMaterialOverrides(TPrim primitive, Season.Controls.Model model)
    {
        var colorTint = model.MaterialColor ?? Vector4.One;
        primitive.MaterialParams.BaseColor *= colorTint;
        primitive.MaterialParams.RenderMode = model.Unlit ? 0u : 1u;
        primitive.MaterialParams.IsSkinned = primitive.OwnerNode?.Skin != null ? 1u : 0u;
        primitive.MaterialParams.BonePaletteStride = 1u;
        primitive.OriginalBaseColorAlpha = primitive.MaterialParams.BaseColor.W;
    }

    /// <summary>
    /// Replaces one primitive's RGB with the override the model holds for its part, if any. Absolute rather than
    /// multiplicative - see <see cref="Season.Controls.Model.SetPartColor"/> for why a tint cannot recolor a saturated
    /// material. W is left untouched because the alpha chain belongs to SyncAlpha.
    /// </summary>
    protected static void ApplyPartColorOverride(TPrim primitive, Season.Controls.Model model)
    {
        if (!model.TryGetPartColor(primitive.PartName, primitive.PartNodeName, out var color))
            return;

        var baseColor = primitive.MaterialParams.BaseColor;
        primitive.MaterialParams.BaseColor = new Vector4(color.X, color.Y, color.Z, baseColor.W);
    }

    /// <summary>
    /// Pushes <c>Model.SetPartColor</c> into every primitive's material block: RGB becomes the override, or the
    /// load-time baseline when the override was cleared. Gated on <see cref="Season.Controls.Model.PartColorVersion"/>,
    /// so a steady frame pays one integer compare; when it does move, all N-buffered frames are written, the same
    /// stale-frame rule SyncAlpha follows. The canonical <c>MaterialParams</c> struct is updated too, so anything that
    /// rewrites the blocks from it later (the skinning sync, a clone) sees the same colors this frame drew.
    ///
    /// <see cref="CollectPrimitives"/> is the whole-model walk here: it is the same recursion
    /// <c>CollectAllPrimitives</c> exposes to the instanced path, and it is already required to be side-effect free.
    /// </summary>
    protected void SyncPartColors(Season.Controls.Model model)
    {
        if (_appliedPartColorVersion == model.PartColorVersion)
            return;
        _appliedPartColorVersion = model.PartColorVersion;

        var primitives = new List<TPrim>();
        CollectPrimitives(primitives);

        foreach (var primitive in primitives)
        {
            var rgb = model.TryGetPartColor(primitive.PartName, primitive.PartNodeName, out var color)
                ? new Vector3(color.X, color.Y, color.Z)
                : primitive.PartBaseColor;

            var canonical = primitive.MaterialParams;
            canonical.BaseColor = new Vector4(rgb.X, rgb.Y, rgb.Z, canonical.BaseColor.W);
            primitive.MaterialParams = canonical;

            for (int i = 0; i < FrameCount; i++)
            {
                ref var slot = ref MaterialSlot(primitive, i);
                var gpuParams = slot;
                gpuParams.BaseColor = new Vector4(rgb.X, rgb.Y, rgb.Z, gpuParams.BaseColor.W);
                slot = gpuParams;
            }
        }
    }

    /// <summary>
    /// The highlight state a control's Update pushes into the group every frame: the wireframe shells, the Bounds box, and
    /// the Outline2D state the OutlineMask pass reads. The three native backends ran this as three copies of the same
    /// forty lines, which is how a highlight fix ends up landing on two of them.
    ///
    /// Shell geometry is per primitive and is written as <c>node WorldTransform x userTransform</c>: shell vertices live
    /// in node-local space, so without the node matrix a whole shell drifts or scales as soon as the glTF root carries
    /// one. Face alpha pulses, so nothing in here is change-gated. A degenerate bounds box (unloaded, or zero extents)
    /// is left dark rather than drawn as a single point.
    /// </summary>
    protected void SyncModelHighlight(Season.Controls.Model model, Matrix4x4 userTransform)
    {
        _wireframeEnabled = model.Highlight.Wireframe;
        if (_wireframeEnabled)
        {
            EnsureWireframeHighlights(model.Highlight.EdgeWidth,
                MathF.Max(model.LocalSize.X, MathF.Max(model.LocalSize.Y, model.LocalSize.Z)));
            if (_wireframeBoxes != null)
            {
                for (int i = 0; i < _wireframeBoxes.Count; i++)
                {
                    var highlight = _wireframeBoxes[i];
                    if (highlight != null)
                    {
                        var nodeWorld = highlight.OwnerNode?.WorldTransform ?? Matrix4x4.Identity;
                        WriteHighlightBox(highlight, nodeWorld * userTransform,
                            model.Highlight.SurfaceColor, model.Highlight.EdgeColor);
                    }
                }
            }
        }

        // Colors are independent of the model alpha chain and are written every frame, because the face alpha pulses.
        _boundsActive = model.Highlight.Bounds;
        if (_boundsActive)
        {
            var bounds = model.GetWorldBoundsRaw();
            if (bounds.Extents.LengthSquared() >= 1e-12f)
            {
                _boundsBox ??= CreateBoundsBox();
                WriteHighlightBox(_boundsBox,
                    Matrix4x4.CreateScale(bounds.Extents * 2f) * Matrix4x4.CreateTranslation(bounds.Center),
                    model.Highlight.SurfaceColor, model.Highlight.EdgeColor);
            }
        }

        SetOutline2DState(model.Highlight.Outline, model.Highlight.OutlineColor, model.Highlight.OutlineWidth);
    }
}
