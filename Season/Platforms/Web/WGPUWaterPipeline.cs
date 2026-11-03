// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Text;
using Season.Rendering.Effects;

namespace Season.Platforms.Web;

/// <summary>
/// P2 pond-water WebGPU handle. One PSO, two binding groups, and the Pass identity check all live on the JS side;
/// this side only does the type downcast and one-shot consumption (isomorphic to <see cref="WGPUDecalPipeline"/>).
///
/// Three WebGPU-specific trade-offs:
/// <list type="bullet">
/// <item>No push constant: "which water-surface slot this draw uses" and "which tile this draw is" are packed into the same <c>instance_index</c>
/// (JS side <c>draw(vc, ic, 0, slot * 256)</c>, with <c>slot = index &gt;&gt; 8</c> and <c>tile = index &amp; 255</c> in WGSL),
/// then flat interpolation carries the slot into the fragment stage;</item>
/// <item>Depth can only be <c>textureLoad</c>, but this Pass's software depth test already needs point sampling, so the result is unaffected;</item>
/// <item>The refraction source is a scene-color snapshot taken before drawing starts, copied outside the Pass by <see cref="Graphics.CopyRenderTarget"/> --
/// this Pass's color attachment cannot be sampled at the same time.</item>
/// </list>
/// </summary>
internal sealed class WGPUWaterPipeline : Season.Rendering.WaterPipeline
{
    /// <summary>Name string of the four Hi-Z levels (comma-separated): built from constants, unchanged between frames, so a single build suffices.</summary>
    static readonly string HzChain = BuildHzChain();
    static int _nextId;
    readonly string _name = $"water_{_nextId++}";
    readonly StringBuilder _draws = new(256);
    bool _disposed, _prepared;

    internal bool Initialize() => WebGPUInterop.CreateWaterPipeline(_name, Season.Rendering.WaterShaders.Wgsl);

    public override bool Prepare(in Season.Rendering.WaterInputs inputs, Season.Rendering.RenderTarget destination)
    {
        _prepared = false;
        if (_disposed || inputs.Color is not WGPURenderTarget color || inputs.Depth is not WGPURenderTarget depth
            || inputs.RefractionSource is not WGPURenderTarget refraction
            || inputs.FrameBuffer is not WGPUStorageBuffer frame || destination is not WGPURenderTarget target)
            return false;
        if (inputs.Count <= 0 || inputs.Count > Season.Rendering.WaterMath.MaxWaters) return false;
        // This Pass uses the color target as the blend destination, so the shared layer must hand over the same target; a different one means the call order is wrong.
        if (!ReferenceEquals(color, target)) return false;
        _prepared = WebGPUInterop.PrepareWater(_name, color.Name, depth.Name, refraction.Name, frame.Id,
            BuildDrawsJson(inputs.Draws, inputs.Count), inputs.SkyView,
            HiZEffect.Ready ? HzChain : string.Empty, target.Name);
        return _prepared;
    }

    /// <summary>
    /// Encodes [slot, normal-map name, vertex count, instance count] into JSON in draw order; a null texture name means the shader uses procedural waves.
    /// Since P4 the vertex/instance counts are not constants: with tiled instancing the vertex count is subdiv²×6 and the instance count is the total number of tiles,
    /// so they can only be passed down per draw, and the JS side then composes <c>draw(vc, ic, 0, slot * 256)</c>.
    /// </summary>
    string BuildDrawsJson(Season.Rendering.WaterDraw[] draws, int count)
    {
        _draws.Clear().Append('[');
        for (int i = 0; i < count; i++)
        {
            var draw = draws[i];
            if (i > 0) _draws.Append(',');
            _draws.Append('[').Append(draw.Index).Append(',');
            if (draw.Texture == null) _draws.Append("null");
            else _draws.Append('"').Append(draw.Texture).Append('"');
            _draws.Append(',').Append(draw.VertexCount).Append(',').Append(draw.InstanceCount);
            _draws.Append(']');
        }
        return _draws.Append(']').ToString();
    }

    /// <summary>Folds the four-level pyramid names into a single line; the JS side splits them by comma and binds, building no separate cache.</summary>
    static string BuildHzChain()
    {
        var builder = new StringBuilder(HiZEffect.LevelCount * 16);
        for (int level = 0; level < HiZEffect.LevelCount; level++)
        {
            if (level > 0) builder.Append(',');
            builder.Append(HiZEffect.LevelName(level));
        }
        return builder.ToString();
    }

    public override bool Draw()
    {
        if (_disposed || !_prepared) return false;
        _prepared = false;
        return WebGPUInterop.DrawWater(_name);
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _prepared = false;
        WebGPUInterop.DisposeWaterPipeline(_name);
    }
}
