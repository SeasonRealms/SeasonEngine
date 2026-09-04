// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Platforms.Web;

/// <summary>
/// Wrapper for an offscreen WebGPU RenderTarget (1-1 Step 2/3).
/// The real GPU resources live on the JS side
/// (in <c>seasonWebGPU.js</c> under <c>_renderTargets[Name]</c>, with two shapes:
/// color = preferred format / rgba16float plus matching depth24plus,
/// depth-only = depth32float shadow map).
/// This class stores only the name handle (name-as-handle), which avoids cross-layer object lifetime issues.
/// Render targets with MatchBackbufferSize are rebuilt lazily on the JS side during beginPass based on the canvas size,
/// while this reference and its name handle remain valid the whole time, matching the resize contract used by FrameSchedule.
/// Fixed-size render targets such as shadow maps are not affected by resize.
/// </summary>
internal sealed class WGPURenderTarget : Season.Rendering.RenderTarget
{
    static int _nextId;

    internal readonly string Name;

    internal WGPURenderTarget(in Season.Rendering.RenderTargetDesc desc)
    {
        Desc = desc;
        Name = $"rt_{_nextId++}";
    }

    public override void Dispose() => WebGPUInterop.DisposeRenderTarget(Name);
}
