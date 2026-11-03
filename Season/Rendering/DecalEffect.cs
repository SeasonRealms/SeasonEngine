// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Numerics;
using System.Runtime.InteropServices;

namespace Season.Rendering;

/// <summary>
/// D1 decal shared-layer driver: each frame it performs frustum culling, stable sorting, and frame buffer packing, then hands off to the backend <see cref="DecalPipeline"/>.
///
/// The lifetime matches <see cref="FogCompositePipeline"/>: registration happens during initialization (the backend sets up the PSO and root signature at that time);
/// afterwards Prepare is called once per frame outside the pass, and Draw is consumed once inside <see cref="RenderPassId.SceneDecal"/>.
/// When there are no visible decals this frame Prepare returns false, and the caller does not even need to issue BeginPass -- an empty list means zero overhead.
/// </summary>
public sealed class DecalEffect : IDisposable
{
    readonly IGraphics _graphics;
    readonly DecalPipeline _pipeline;
    readonly Vector4[] _frame = new Vector4[DecalMath.VectorCount];
    readonly DecalDraw[] _draws = new DecalDraw[DecalMath.MaxDecals];
    readonly DecalVolume[] _sorted = new DecalVolume[DecalMath.MaxDecals];
    StorageBuffer? _buffer;
    int _count;
    int _pipelineCount;
    bool _disposed;

    internal DecalEffect(IGraphics g, DecalPipeline pipeline)
    {
        _graphics = g;
        _pipeline = pipeline;
    }

    /// <summary>Whether this frame already has a prepared decal list; Draw only has meaning when true.</summary>
    public bool Ready { get; private set; }

    /// <summary>The number of decals that passed culling this frame, for logging and acceptance checks.</summary>
    public int VisibleCount => _pipelineCount;

    /// <summary>
    /// Whether this frame needs to treat SceneNormal as a second color attachment. This is true only when "at least one of the visible decals declares
    /// <see cref="DecalVolume.AffectRoughness"/>" and a normal target exists;
    /// otherwise do not pay an extra attachment Load/Store for a decal that does not change roughness. The caller uses this to decide the pass's normal attachment.
    /// </summary>
    public bool WriteRoughness { get; private set; }

    /// <summary>
    /// Collects and prepares this frame's decals. If any precondition is missing it returns false without touching Ready,
    /// and the caller uses this to skip the pass entirely (no BeginPass, no test counter write).
    /// </summary>
    public bool Prepare(IGraphics g, SceneDecals? source, RenderTarget? color, RenderTarget? depth, RenderTarget? normal)
    {
        Ready = false;
        _pipelineCount = 0;
        WriteRoughness = false;
        if (_disposed || !ReferenceEquals(g, _graphics) || color == null || depth == null) return false;
        if (source == null || source.Decals.Count == 0) return false;
        var view = RenderView.Current;
        if (view == null || !view.Prepared || view.Width <= 0 || view.Height <= 0) return false;
        _count = Collect(source, in view.Frustum);
        if (_count == 0) return false;
        var jitteredVp = view.View * view.RenderProjection;
        if (!Matrix4x4.Invert(jitteredVp, out var inverseJitteredVp)) return false;
        bool writeRoughness = normal != null && AnyRoughness();
        DecalMath.Pack(_frame, jitteredVp, inverseJitteredVp, view.Position, view.Width, view.Height,
            writeRoughness, _sorted.AsSpan(0, _count));
        _buffer ??= g.CreateStorageBuffer(DecalMath.ByteCount);
        if (_buffer == null || !g.TryUpdateStorageBuffer(_buffer, MemoryMarshal.AsBytes(_frame.AsSpan())))
            return false;
        for (int i = 0; i < _count; i++)
            _draws[i] = new DecalDraw(i, _sorted[i].Mode, _sorted[i].Texture);
        var inputs = new DecalInputs(color, depth, normal, _buffer, _draws, _count, writeRoughness);
        if (!_pipeline.Prepare(inputs, color)) return false;
        _pipelineCount = _count;
        WriteRoughness = writeRoughness;
        Ready = true;
        return true;
    }

    bool AnyRoughness()
    {
        for (int i = 0; i < _count; i++) if (_sorted[i].AffectRoughness) return true;
        return false;
    }

    /// <summary>Called once inside <see cref="RenderPassId.SceneDecal"/>; does nothing when Prepare did not succeed.</summary>
    public void Draw(IGraphics g)
    {
        if (!Ready || _disposed || !ReferenceEquals(g, _graphics)) return;
        if (!_pipeline.Draw()) Ready = false;
    }

    /// <summary>
    /// Culls and sorts. The sort must be stable: decals with the same <see cref="DecalVolume.SortOrder"/> are drawn in registration order,
    /// otherwise per-frame order jitter makes semi-transparent compositing flicker under TAA.
    /// </summary>
    int Collect(SceneDecals source, in Frustum frustum)
    {
        int count = 0;
        var decals = source.Decals;
        for (int i = 0; i < decals.Count && count < DecalMath.MaxDecals; i++)
        {
            var decal = decals[i];
            if (!decal.Enabled) continue;
            // An additive decal with zero intensity contributes nothing to the scene (outputs 0, dst+0 = dst), so cull it outright.
            // Besides saving a draw, this also rules out visible consequences of backend misconfiguration such as "outputting 0 and painting the target black when blending is off".
            if (decal.Mode == DecalMode.Additive && decal.Intensity <= 0f) continue;
            decal.GetBounds(out var center, out var extents);
            if (!frustum.Intersects(in center, in extents)) continue;
            // Stable insertion sort: the cap is 64, and insertion sort is cheaper and less error-prone than allocating a key array.
            int slot = count;
            while (slot > 0 && _sorted[slot - 1].SortOrder > decal.SortOrder)
            {
                _sorted[slot] = _sorted[slot - 1];
                slot--;
            }
            _sorted[slot] = decal;
            count++;
        }
        return count;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Ready = false;
        WriteRoughness = false;
        _pipelineCount = 0;
        _count = 0;
        _buffer?.Dispose();
        _buffer = null;
        Array.Clear(_sorted);
        Array.Clear(_draws);
        _pipeline.Dispose();
    }
}
