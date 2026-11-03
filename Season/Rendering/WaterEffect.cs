// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Numerics;
using System.Runtime.InteropServices;
using Season.Rendering.Effects;

namespace Season.Rendering;

/// <summary>
/// Shared-layer driver for P2 water surfaces: each frame it does frustum culling, stable sorting, and frame-buffer packing, then hands off to the backend <see cref="WaterPipeline"/>.
///
/// Its lifecycle matches <see cref="DecalEffect"/>: registration happens during initialization (the backend builds the PSO and root signature at that point),
/// after which Prepare is called once per frame outside a pass and Draw is consumed once inside <see cref="RenderPassId.SceneWater"/>.
/// When there is no visible water surface this frame, Prepare returns false and the caller need not even begin the pass or take the scene color snapshot -- an empty table means zero cost.
/// </summary>
public sealed class WaterEffect : IDisposable
{
    readonly IGraphics _graphics;
    readonly WaterPipeline _pipeline;
    readonly Vector4[] _frame = new Vector4[WaterMath.VectorCount];
    readonly WaterDraw[] _draws = new WaterDraw[WaterMath.MaxWaters];
    readonly WaterPlane[] _sorted = new WaterPlane[WaterMath.MaxWaters];
    /// <summary>Frame block for a view that has no <see cref="ViewTargets"/> registered (the single-view shape); a
    /// registered view owns its own copy in <see cref="ViewTargets.WaterFrameBuffer"/>.</summary>
    StorageBuffer? _buffer;
    int _count;
    int _pipelineCount;
    bool _disposed;

    internal WaterEffect(IGraphics g, WaterPipeline pipeline)
    {
        _graphics = g;
        _pipeline = pipeline;
    }

    /// <summary>Whether a prepared water table already exists for this frame; Draw is only meaningful while true.</summary>
    public bool Ready { get; private set; }

    /// <summary>Number of water surfaces that passed culling this frame, for logging and acceptance checks.</summary>
    public int VisibleCount => _pipelineCount;

    /// <summary>
    /// Collects and prepares this frame's water surfaces. If any precondition is missing it returns false without touching Ready,
    /// and the caller then skips the snapshot and the pass entirely (no BeginPass, no test counters written).
    /// </summary>
    /// <param name="refractionSource">The scene color copied before drawing begins; the caller prepares it before Prepare.</param>
    /// <param name="time">This frame's time (seconds), driving normal scrolling and rain ripples; comes from <c>BaseApp.Time</c> and freezes naturally when paused.</param>
    public bool Prepare(IGraphics g, SceneWaters? source, RenderTarget? color, RenderTarget? refractionSource,
        RenderTarget? depth, float time)
    {
        Ready = false;
        _pipelineCount = 0;
        if (_disposed || !ReferenceEquals(g, _graphics) || color == null || depth == null) return false;
        if (refractionSource == null || ReferenceEquals(refractionSource, color)) return false;
        if (source == null || source.Waters.Count == 0) return false;
        var view = RenderView.Current;
        if (view == null || !view.Prepared || view.Width <= 0 || view.Height <= 0) return false;
        _count = Collect(source, in view.Frustum);
        if (_count == 0) return false;
        var jitteredVp = view.View * view.RenderProjection;
        if (!Matrix4x4.Invert(jitteredVp, out var inverseJitteredVp)) return false;

        // Weather and sun both come from this frame's already-frozen lighting snapshot, and when it is not ready it is packed as "clear sky, no sun":
        // the ripple term is multiplied out by 0 and the sky fallback color goes to dark gray, so neither branch reads uninitialized data.
        var lights = FrameSchedule.LightingSnapshot;
        bool raining = FrameSchedule.LightingSnapshotValid && lights.WeatherParams1.W > 0;
        SunDirection(lights, FrameSchedule.LightingSnapshotValid, out var sunDirection, out float sunIntensity);
        // The screen reflection toggle follows HiZEffect: when the pyramid was not rebuilt this frame (disabled, no depth, just resized)
        // Enabled is 0, the shader never samples those four textures at all, and the backend is free to bind any placeholder texture.
        var reflection = new WaterReflection(
            HiZEffect.Ready && RenderQuality.Current.ScreenSpaceReflections != SsrMode.Off,
            MathF.Max(0f, RenderQuality.Current.SsrIntensity),
            view.Camera.Near, view.Camera.Far,
            MathF.Max(0.01f, RenderQuality.Current.SsrMaxDistance),
            MathF.Max(0.01f, RenderQuality.Current.SsrThickness));
        WaterMath.Pack(_frame, jitteredVp, inverseJitteredVp, view.Position, view.Width, view.Height,
            raining, time, FrameSchedule.SkyViewTexture != null, reflection, sunDirection, sunIntensity,
            _sorted.AsSpan(0, _count));
        // The frame block lives on the view, not on this effect: two views upload it in the same frame, and on backends
        // whose staging keeps only a frame's last write a shared buffer would leave the main view drawing with the
        // secondary view's matrices (see ViewTargets.WaterFrameBuffer).
        var buffer = view.Targets != null
            ? view.Targets.WaterFrameBuffer ??= g.CreateStorageBuffer(WaterMath.ByteCount)
            : _buffer ??= g.CreateStorageBuffer(WaterMath.ByteCount);
        if (buffer == null || !g.TryUpdateStorageBuffer(buffer, MemoryMarshal.AsBytes(_frame.AsSpan())))
            return false;
        for (int i = 0; i < _count; i++)
        {
            // The tiling parameters use the same Tiling as Pack: the tilesX/tilesZ/subdiv in the frame data must match the vertex and instance
            // counts the backend draw emits, bit for bit, otherwise the vertex shader resolves tile indices outside the mesh.
            var tiling = WaterMath.Tiling(_sorted[i]);
            _draws[i] = new WaterDraw(i, _sorted[i].NormalTexture, tiling.X, tiling.Z, tiling.Subdiv);
        }
        var inputs = new WaterInputs(color, refractionSource, depth, FrameSchedule.SkyViewTexture,
            buffer, _draws, _count);
        if (!_pipeline.Prepare(inputs, color)) return false;
        _pipelineCount = _count;
        Ready = true;
        return true;
    }

    /// <summary>
    /// Gets the sun's (strongest directional light) direction and intensity. The direction is conventionally taken as the propagation direction "pointing toward the surface",
    /// consistent with the <c>dot(N, -sunDir)</c> usage in the main shader; when no directional light is found it returns zero intensity and the water surface is left with sky reflection only.
    /// </summary>
    static void SunDirection(in SceneLightParams lights, bool valid, out Vector3 direction, out float intensity)
    {
        direction = new Vector3(0f, -1f, 0f);
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
            intensity = value;
        }
    }

    /// <summary>Called once inside <see cref="RenderPassId.SceneWater"/>; does nothing when Prepare did not succeed.</summary>
    public void Draw(IGraphics g)
    {
        if (!Ready || _disposed || !ReferenceEquals(g, _graphics)) return;
        if (!_pipeline.Draw()) Ready = false;
    }

    /// <summary>
    /// Culls and sorts. The sort must be stable: water surfaces with the same <see cref="WaterPlane.SortOrder"/> are drawn in registration order,
    /// otherwise per-frame order jitter would make the blended result of two water layers flicker under TAA.
    /// </summary>
    int Collect(SceneWaters source, in Frustum frustum)
    {
        int count = 0;
        var waters = source.Waters;
        for (int i = 0; i < waters.Count && count < WaterMath.MaxWaters; i++)
        {
            var water = waters[i];
            if (!water.Enabled || water.Size.X <= 0f || water.Size.Y <= 0f) continue;
            // A plane has no thickness: use a very thin bounding box for the frustum test, so both top-down and side views cull correctly.
            var center = water.Position;
            var extents = new Vector3(water.Size.X * 0.5f, 0.001f, water.Size.Y * 0.5f);
            if (!frustum.Intersects(in center, in extents)) continue;
            int slot = count;
            while (slot > 0 && _sorted[slot - 1].SortOrder > water.SortOrder)
            {
                _sorted[slot] = _sorted[slot - 1];
                slot--;
            }
            _sorted[slot] = water;
            count++;
        }
        return count;
    }

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
