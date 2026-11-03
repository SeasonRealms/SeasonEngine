// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Text;

namespace Season.Platforms.Web;

/// <summary>
/// WebGPU handle for the P1 waterfall curtain. The single PSO, the two-level bind groups and the pass identity check all live on the JS side;
/// this side only does type downcasting and one-shot consumption (isomorphic to <see cref="WGPUWaterPipeline"/>):
/// group 0 is frame-level resources (frame buffer, scene depth, the clamp/wrap samplers), group 1 is the three textures per curtain.
///
/// Two WebGPU-specific trade-offs, shared in spirit with the water surface:
/// <list type="bullet">
/// <item>No push constant: the slot and the mesh parameters (column count, subdivision count) must be packed into the same <c>instance_index</c>.
/// The water surface uses "slot * 256 + tile"; here it is changed to bit-field packing (<c>slot | columns &lt;&lt; 8 | subdiv &lt;&lt; 16</c>),
/// because a curtain's column count and subdivision count differ per sheet and cannot be fixed the way the water surface is;
/// the packed encoding is computed here by <see cref="BuildDrawsJson"/> and handed straight to JS, avoiding duplicating the derivation on both sides;
/// </item>
/// <item>Depth can only be read with <c>textureLoad</c>: this pass binds no depth attachment, occlusion is a software depth comparison in the shader,
/// and point sampling is the correct approach anyway - pixel-for-pixel consistent with the other three backends.</item>
/// </list>
///
/// No refraction snapshot: what is seen behind a curtain is the scene color itself, with the water surface and fog already drawn.
/// </summary>
internal sealed class WGPUWaterfallPipeline : Season.Rendering.WaterfallPipeline
{
    static int _nextId;
    readonly string _name = $"waterfall_{_nextId++}";
    readonly StringBuilder _draws = new(256);
    bool _disposed, _prepared;

    /// <summary>
    /// <paramref name="ribbon"/> switches the shader source only: a river ribbon is a flat horizontal band whose current runs in
    /// meters per second, a waterfall curtain is a closed section ring that accelerates from lip to landing point. The JS side is
    /// untouched - the bind group layouts, the 8-slot frame buffer size and the pass identity are identical, and the frame
    /// buffers are slot-for-slot equal (see <see cref="Season.Rendering.WaterRibbonMath"/>).
    /// </summary>
    internal WGPUWaterfallPipeline(bool ribbon = false) => _ribbon = ribbon;

    /// <summary>Whether this instance draws river ribbons instead of waterfall curtains.</summary>
    readonly bool _ribbon;

    internal bool Initialize() => WebGPUInterop.CreateWaterfallPipeline(_name,
        _ribbon ? Season.Rendering.WaterRibbonShaders.Wgsl : Season.Rendering.WaterfallShaders.Wgsl);

    public override bool Prepare(in Season.Rendering.WaterfallInputs inputs, Season.Rendering.RenderTarget destination)
    {
        _prepared = false;
        if (_disposed || inputs.Color is not WGPURenderTarget color || inputs.Depth is not WGPURenderTarget depth
            || inputs.FrameBuffer is not WGPUStorageBuffer frame || destination is not WGPURenderTarget target)
            return false;
        if (inputs.Count <= 0 || inputs.Count > Season.Rendering.WaterfallMath.MaxSheets) return false;
        // This pass treats the color target as the blend base map, so the shared layer must hand over that same target; a different one means the call order is wrong.
        if (!ReferenceEquals(color, target)) return false;
        _prepared = WebGPUInterop.PrepareWaterfall(_name, color.Name, depth.Name, frame.Id,
            BuildDrawsJson(inputs.Draws, inputs.Count), target.Name);
        return _prepared;
    }

    /// <summary>
    /// Encodes [slot, mask name, normal name, flow name, vertex count, instance index encoding] into JSON in draw order;
    /// a null texture name means empty, and the shader's flag gating will fall back to the procedural defaults.
    /// The instance index encoding is <c>slot | columns &lt;&lt; 8 | subdiv &lt;&lt; 16</c>, bit-for-bit consistent with the WGSL bit decoding;
    /// it is computed here rather than on the JS side because the slot and mesh parameters are only fully visible here.
    /// </summary>
    string BuildDrawsJson(Season.Rendering.WaterfallDraw[] draws, int count)
    {
        _draws.Clear().Append('[');
        for (int i = 0; i < count; i++)
        {
            var draw = draws[i];
            if (i > 0) _draws.Append(',');
            _draws.Append('[').Append(draw.Index).Append(',');
            AppendName(draw.Mask); _draws.Append(',');
            AppendName(draw.Normal); _draws.Append(',');
            AppendName(draw.Flow);
            // The column count and subdivision count each occupy 8 bits; out-of-range values are clamped to the lower bound on the WGSL side, and the upper bound is clamped again here to prevent bit spillover.
            int columns = Math.Clamp(draw.Columns, 0, 255);
            int subdiv = Math.Clamp(draw.Subdiv, 0, 255);
            int encoded = (draw.Index & 255) | (columns << 8) | (subdiv << 16);
            _draws.Append(',').Append(draw.VertexCount).Append(',').Append(encoded);
            _draws.Append(']');
        }
        return _draws.Append(']').ToString();

        void AppendName(string? name)
        {
            if (name == null) _draws.Append("null");
            else _draws.Append('"').Append(name).Append('"');
        }
    }

    public override bool Draw()
    {
        if (_disposed || !_prepared) return false;
        _prepared = false;
        return WebGPUInterop.DrawWaterfall(_name);
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _prepared = false;
        WebGPUInterop.DisposeWaterfallPipeline(_name);
    }
}
