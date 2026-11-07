// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Numerics;
using System.Runtime.InteropServices;
using Season.Rendering.Effects;

namespace Season.Rendering;

/// <summary>
/// Shared-layer driver for river ribbons. It is the ribbon counterpart of <see cref="WaterfallEffect"/>: every frame it culls,
/// stably sorts and packs the ribbons, then hands the slot table to a <see cref="WaterfallPipeline"/> built from the ribbon
/// shader variant.
///
/// Why the slot table and the pipeline type are shared with the waterfall rather than duplicated: the two frame buffers have
/// identical layouts (<see cref="WaterRibbonMath"/> mirrors <see cref="WaterfallMath"/> slot for slot), and the backend work
/// around that layout - root signature, descriptor ring, software depth test, alpha blending, pass identity check - is exactly
/// the same. The only thing that differs is the shader source, so <see cref="IGraphics.CreateWaterRibbonPipeline"/> returns an
/// instance of the same abstract class with a different PSO. Duplicating a 350-line backend class per platform to change one
/// string would be four more places for the two to drift apart.
///
/// There is no refraction snapshot: the ribbon lives in <see cref="RenderPassId.SceneWaterfall"/>, where the color target is the
/// blend base and depth is a sampling source only.
/// </summary>
public sealed class WaterRibbonEffect : IDisposable
{
    readonly IGraphics _graphics;
    readonly WaterfallPipeline _pipeline;
    readonly Vector4[] _frame = new Vector4[WaterRibbonMath.VectorCount];
    readonly WaterfallDraw[] _draws = new WaterfallDraw[WaterRibbonMath.MaxRibbons];
    readonly WaterRibbon[] _sorted = new WaterRibbon[WaterRibbonMath.MaxRibbons];
    /// <summary>Frame block for a view that has no <see cref="ViewTargets"/> registered (the single-view shape); a
    /// registered view owns its own copy in <see cref="ViewTargets.RiverFrameBuffer"/>.</summary>
    StorageBuffer? _buffer;
    int _count;
    int _pipelineCount;
    bool _disposed;

    internal WaterRibbonEffect(IGraphics g, WaterfallPipeline pipeline)
    {
        _graphics = g;
        _pipeline = pipeline;
    }

    /// <summary>Whether a prepared ribbon table already exists this frame; Draw is only meaningful when it is true.</summary>
    public bool Ready { get; private set; }

    /// <summary>Number of ribbons that passed culling this frame, for logging and validation.</summary>
    public int VisibleCount => _pipelineCount;

    /// <summary>
    /// Collect and prepare this frame's ribbons. Any missing precondition returns false and leaves Ready untouched, so the
    /// caller skips the pass entirely (no BeginPass, no state transition).
    /// </summary>
    /// <param name="g">This frame's graphics device; gives up if it is not the instance registered with.</param>
    /// <param name="source">Scene ribbon registry; null or empty means nothing is drawn this frame.</param>
    /// <param name="color">This frame's scene color, used as the color target and blend base.</param>
    /// <param name="depth">This frame's SceneDepth, used only as a sampling source; its size must match the color.</param>
    /// <param name="time">This frame's time in seconds, driving the flow; frozen while paused, because it comes from BaseApp.Time.</param>
    public bool Prepare(IGraphics g, SceneWaterRibbons? source, RenderTarget? color, RenderTarget? depth, float time)
    {
        Ready = false;
        _pipelineCount = 0;
        if (_disposed || !ReferenceEquals(g, _graphics) || color == null || depth == null) return false;
        if (color.Desc.Width != depth.Desc.Width || color.Desc.Height != depth.Desc.Height) return false;
        if (source == null || source.Ribbons.Count == 0) return false;
        var view = RenderView.Current;
        if (view == null || !view.Prepared || view.Width <= 0 || view.Height <= 0) return false;
        _count = Collect(source, in view.Frustum);
        if (_count == 0) return false;
        var jitteredVp = view.View * view.RenderProjection;

        // Same lighting snapshot as the waterfall: when it is not ready, pack "no sun, no ambient" so the ribbon reads as a
        // dark film rather than sampling uninitialized data.
        var lights = FrameSchedule.LightingSnapshot;
        bool valid = FrameSchedule.LightingSnapshotValid;
        WaterfallEffect.SunLight(lights, valid, out var sunDirection, out var sunColor, out float sunIntensity);
        var ambient = valid ? new Vector3(lights.Ambient.X, lights.Ambient.Y, lights.Ambient.Z) : Vector3.Zero;
        float ambientIntensity = valid ? lights.Ambient.W : 0f;
        WaterRibbonMath.Pack(_frame, jitteredVp, view.Position, view.Width, view.Height,
            sunDirection, sunColor, sunIntensity, ambient, ambientIntensity, time,
            _sorted.AsSpan(0, _count), Season.Basic.DeviceServices.BaseApp?.Fog);

        // The frame block lives on the view, not on this effect: two views upload it in the same frame, and on backends
        // whose staging keeps only a frame's last write a shared buffer would leave the main view drawing with the
        // secondary view's matrices (see ViewTargets.RiverFrameBuffer).
        var buffer = view.Targets != null
            ? view.Targets.RiverFrameBuffer ??= g.CreateStorageBuffer(WaterRibbonMath.ByteCount)
            : _buffer ??= g.CreateStorageBuffer(WaterRibbonMath.ByteCount);
        if (buffer == null || !g.TryUpdateStorageBuffer(buffer, MemoryMarshal.AsBytes(_frame.AsSpan())))
            return false;
        for (int i = 0; i < _count; i++)
        {
            var ribbon = _sorted[i];
            _draws[i] = new WaterfallDraw(i, ribbon.MaskTexture, ribbon.NormalTexture, ribbon.FlowTexture,
                Math.Clamp(ribbon.Columns, 2, WaterRibbonMath.MaxColumns),
                Math.Clamp(ribbon.Subdiv, 1, WaterRibbonMath.MaxSubdiv),
                WaterRibbonMath.VertexCount(ribbon));
        }
        var inputs = new WaterfallInputs(color, depth, buffer, _draws, _count);
        if (!_pipeline.Prepare(inputs, color)) return false;
        _pipelineCount = _count;
        Ready = true;
        return true;
    }

    /// <summary>Called once inside <see cref="RenderPassId.SceneWaterfall"/>; does nothing when Prepare did not succeed.</summary>
    public void Draw(IGraphics g)
    {
        if (!Ready || _disposed || !ReferenceEquals(g, _graphics)) return;
        if (!_pipeline.Draw()) Ready = false;
    }

    /// <summary>
    /// Cull and sort. The bounds are taken from <b>sampled rows</b> rather than from the control points: a bend's water lies
    /// outside the chord between two control points, so a point-only box would cull the outside of every bend - the classic
    /// "disappears when you look at it from the wrong angle" bug. The sample count is the same one the mesh uses, so the box is
    /// the drawn band's own box plus a small margin.
    /// </summary>
    int Collect(SceneWaterRibbons source, in Frustum frustum)
    {
        int count = 0;
        var ribbons = source.Ribbons;
        for (int i = 0; i < ribbons.Count && count < WaterRibbonMath.MaxRibbons; i++)
        {
            var ribbon = ribbons[i];
            if (!ribbon.Enabled || ribbon.Points.Count < 2) continue;
            var points = CollectionsMarshal.AsSpan(ribbon.Points);
            int control = Math.Min(points.Length, WaterRibbonMath.MaxControlPoints);
            int subdiv = Math.Clamp(ribbon.Subdiv, 1, WaterRibbonMath.MaxSubdiv);
            int rows = (control - 1) * subdiv + 1;

            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            for (int row = 0; row < rows; row++)
            {
                var line = WaterRibbonMath.SampleRow(points[..control], row / (float)subdiv);
                // Half width is the lateral semi-axis and the band is flat, so the vertical extent is only the water film's own
                // thickness; the 0.2 m margin keeps the box honest at grazing angles.
                float radius = MathF.Max(line.HalfWidth, 0f) + 0.2f;
                var extent = new Vector3(radius, 0.2f, radius);
                min = Vector3.Min(min, line.Center - extent);
                max = Vector3.Max(max, line.Center + extent);
            }
            var bounds = Bounds3D.FromMinMax(min, max).Transform(ribbon.Transform);
            var center = bounds.Center;
            var extents = Vector3.Max(bounds.Extents, new Vector3(0.01f));
            if (!frustum.Intersects(in center, in extents)) continue;
            int slot = count;
            while (slot > 0 && _sorted[slot - 1].SortOrder > ribbon.SortOrder)
            {
                _sorted[slot] = _sorted[slot - 1];
                slot--;
            }
            _sorted[slot] = ribbon;
            count++;
        }
        return count;
    }

    /// <summary>Release the frame buffer and the backend pipeline; safe to call repeatedly.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Ready = false;
        _pipelineCount = 0;
        _count = 0;
        _buffer?.Dispose();
        _buffer = null;
        Array.Clear(_sorted);
        Array.Clear(_draws);
        _pipeline.Dispose();
    }
}
