// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Numerics;
using System.Runtime.InteropServices;
using Season.Rendering.Effects;

namespace Season.Rendering;

/// <summary>
/// Shared-layer driver for waterfall sheets: every frame it performs frustum culling, stable sorting and frame buffer packing, then hands off to the backend <see cref="WaterfallPipeline"/>.
///
/// The lifecycle matches <see cref="WaterEffect"/>: registration happens during initialization (the backend builds the PSO and root signature at this point),
/// after which Prepare is called once per frame outside the Pass and Draw is consumed once inside <see cref="RenderPassId.SceneWaterfall"/>.
/// When there is no visible sheet this frame, Prepare returns false and the caller does not even need to issue BeginPass -- an empty table costs nothing.
///
/// Unlike the water surface, there is <b>no</b> refraction snapshot here: a sheet does not need to sample the already-drawn scene color,
/// so Prepare only performs a single depth state transition and involves no RT copy at all.
/// </summary>
public sealed class WaterfallEffect : IDisposable
{
    readonly IGraphics _graphics;
    readonly WaterfallPipeline _pipeline;
    readonly Vector4[] _frame = new Vector4[WaterfallMath.VectorCount];
    readonly WaterfallDraw[] _draws = new WaterfallDraw[WaterfallMath.MaxSheets];
    readonly WaterfallSheet[] _sorted = new WaterfallSheet[WaterfallMath.MaxSheets];
    StorageBuffer? _buffer;
    int _count;
    int _pipelineCount;
    bool _disposed;

    internal WaterfallEffect(IGraphics g, WaterfallPipeline pipeline)
    {
        _graphics = g;
        _pipeline = pipeline;
    }

    /// <summary>Whether a Prepared sheet table already exists this frame; Draw is only meaningful when it is true.</summary>
    public bool Ready { get; private set; }

    /// <summary>Number of sheets that passed culling this frame, for logging and validation.</summary>
    public int VisibleCount => _pipelineCount;

    /// <summary>
    /// Collect and prepare this frame's sheets. If any precondition is missing it returns false and leaves Ready untouched,
    /// so the caller skips the Pass entirely (no BeginPass, no test counter written).
    /// </summary>
    /// <param name="g">This frame's graphics device; gives up directly if it is not the same instance as at registration.</param>
    /// <param name="source">Scene waterfall registry; null or an empty table means nothing is drawn this frame.</param>
    /// <param name="color">This frame's scene color, used only as the color target.</param>
    /// <param name="depth">This frame's SceneDepth, used only as a sampling source; its size must match the color.</param>
    /// <param name="time">This frame's time (seconds), driving the scroll; from <c>BaseApp.Time</c>, naturally frozen when paused.</param>
    public bool Prepare(IGraphics g, SceneWaterfalls? source, RenderTarget? color, RenderTarget? depth, float time)
    {
        Ready = false;
        _pipelineCount = 0;
        if (_disposed || !ReferenceEquals(g, _graphics) || color == null || depth == null) return false;
        // The software depth test samples the same pixel: mismatched sizes would misalign the sampling coordinates, better to draw nothing.
        if (color.Desc.Width != depth.Desc.Width || color.Desc.Height != depth.Desc.Height) return false;
        if (source == null || source.Waterfalls.Count == 0) return false;
        var view = RenderView.Current;
        if (view == null || !view.Prepared || view.Width <= 0 || view.Height <= 0) return false;
        _count = Collect(source, in view.Frustum);
        if (_count == 0) return false;
        var jitteredVp = view.View * view.RenderProjection;

        // Lighting comes from this frame's already-frozen snapshot: when not ready, pack as "no sun, no ambient",
        // so the sheet becomes a dark film of pure foam color rather than reading uninitialized data; once lighting returns it comes back the next frame.
        var lights = FrameSchedule.LightingSnapshot;
        bool valid = FrameSchedule.LightingSnapshotValid;
        SunLight(lights, valid, out var sunDirection, out var sunColor, out float sunIntensity);
        var ambient = valid ? new Vector3(lights.Ambient.X, lights.Ambient.Y, lights.Ambient.Z) : Vector3.Zero;
        float ambientIntensity = valid ? lights.Ambient.W : 0f;
        WaterfallMath.Pack(_frame, jitteredVp, view.Position, view.Width, view.Height,
            sunDirection, sunColor, sunIntensity, ambient, ambientIntensity, time,
            _sorted.AsSpan(0, _count));

        _buffer ??= g.CreateStorageBuffer(WaterfallMath.ByteCount);
        if (_buffer == null || !g.TryUpdateStorageBuffer(_buffer, MemoryMarshal.AsBytes(_frame.AsSpan())))
            return false;
        for (int i = 0; i < _count; i++)
        {
            // The vertex count uses the same function as WaterfallMath: the SV_VertexID range issued by the backend must match
            // the mesh expanded by the vertex shader bit for bit, otherwise the last row/column would sample out of bounds into an adjacent sheet's slot.
            var sheet = _sorted[i];
            int columns = Math.Clamp(sheet.Columns, 2, WaterfallMath.MaxColumns);
            int subdiv = Math.Clamp(sheet.Subdiv, 1, WaterfallMath.MaxSubdiv);
            _draws[i] = new WaterfallDraw(i, sheet.MaskTexture, sheet.NormalTexture, sheet.FlowTexture,
                columns, subdiv, WaterfallMath.VertexCount(sheet));
        }
        var inputs = new WaterfallInputs(color, depth, _buffer, _draws, _count);
        if (!_pipeline.Prepare(inputs, color)) return false;
        _pipelineCount = _count;
        Ready = true;
        return true;
    }

    /// <summary>
    /// Fetch the sun's (the strongest directional light's) direction, color and intensity. By convention the direction is taken as the propagation direction "pointing toward the surface",
    /// consistent with the <c>dot(N, -sunDir)</c> usage in the main shader; when no directional light is found it returns zero intensity and the sheet is left with only ambient light.
    /// </summary>
    static void SunLight(in SceneLightParams lights, bool valid, out Vector3 direction, out Vector3 color, out float intensity)
    {
        direction = new Vector3(0f, -1f, 0f);
        color = Vector3.One;
        intensity = 0f;
        if (!valid) return;
        for (int i = 0; i < SceneLightParams.MaxLights; i++)
        {
            var light = lights.Lights[i];
            if (light.DirType.W != GpuLight.TypeDirectional) continue;
            float value = light.ColorIntensity.W;
            if (value <= intensity) continue;
            var dir = new Vector3(light.DirType.X, light.DirType.Y, light.DirType.Z);
            float length = dir.Length();
            if (length <= 1e-6f) continue;
            direction = dir / length;
            color = new Vector3(light.ColorIntensity.X, light.ColorIntensity.Y, light.ColorIntensity.Z);
            intensity = value;
        }
    }

    /// <summary>Called once inside <see cref="RenderPassId.SceneWaterfall"/>; does nothing when Prepare did not succeed.</summary>
    public void Draw(IGraphics g)
    {
        if (!Ready || _disposed || !ReferenceEquals(g, _graphics)) return;
        if (!_pipeline.Draw()) Ready = false;
    }

    /// <summary>
    /// Cull and sort. The bounding box conservatively expands each control point by half width + bow + margin:
    /// a sheet is a long thin band, so a per-point AABB is used rather than a curve-following bounding volume; the cost is drawing a few extra when viewed from above,
    /// but it avoids the class of disappearance-only-from-certain-angles where "the culler is smarter than the shader".
    /// The sort must be stable: equal <see cref="WaterfallSheet.SortOrder"/> values are drawn in registration order,
    /// otherwise the blended result of two overlapping sheets would flicker under TAA.
    ///
    /// The bounding box is computed in author space and then multiplied by <see cref="WaterfallSheet.Transform"/> once:
    /// the control points themselves do not move with the control, and the conservativeness under scale/rotation is left to the |M| method of Bounds3D.Transform,
    /// so "where it is drawn" and "where it is culled" always come from the same transform.
    /// </summary>
    int Collect(SceneWaterfalls source, in Frustum frustum)
    {
        int count = 0;
        var sheets = source.Waterfalls;
        for (int i = 0; i < sheets.Count && count < WaterfallMath.MaxSheets; i++)
        {
            var sheet = sheets[i];
            if (!sheet.Enabled || sheet.Points.Count < 2) continue;
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            for (int p = 0; p < sheet.Points.Count && p < WaterfallMath.MaxControlPoints; p++)
            {
                var point = sheet.Points[p];
                // The half width is the lateral semi-axis, and half the thickness is the semi-axis in the outward/against-rock directions; the bow only bulges forward.
                // The two are summed rather than taking the max: thickness and half width are perpendicular, and when projected onto world axes both are shared by the X and Z axes.
                float radius = MathF.Max(point.HalfWidth, 0f) + MathF.Abs(sheet.Bow)
                    + MathF.Max(sheet.Thickness, 0f) * 0.5f + 0.35f;
                var extent = new Vector3(radius, 0.2f, radius);
                min = Vector3.Min(min, point.Position - extent);
                max = Vector3.Max(max, point.Position + extent);
            }
            var bounds = Bounds3D.FromMinMax(min, max).Transform(sheet.Transform);
            var center = bounds.Center;
            var extents = Vector3.Max(bounds.Extents, new Vector3(0.01f));
            if (!frustum.Intersects(in center, in extents)) continue;
            int slot = count;
            while (slot > 0 && _sorted[slot - 1].SortOrder > sheet.SortOrder)
            {
                _sorted[slot] = _sorted[slot - 1];
                slot--;
            }
            _sorted[slot] = sheet;
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
