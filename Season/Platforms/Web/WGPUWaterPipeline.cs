// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Text;
using Season.Rendering.Effects;

namespace Season.Platforms.Web;

/// <summary>
/// P2 池塘水的 WebGPU 句柄。一条 PSO、两级绑定组与 Pass 身份校对全在 JS 侧，
/// 这边只做类型下转与一次性消费（与 <see cref="WGPUDecalPipeline"/> 同构）。
///
/// 三处 WebGPU 特有的取舍：
/// <list type="bullet">
/// <item>没有 push constant：「本 draw 用哪个水面槽」与「本 draw 是哪个 tile」挤在同一个 <c>instance_index</c> 里
/// （JS 侧 <c>draw(vc, ic, 0, slot * 256)</c>，WGSL 里 <c>slot = index &gt;&gt; 8</c>、<c>tile = index &amp; 255</c>），
/// 再用 flat 插值把槽位送进片元阶段；</item>
/// <item>深度只能 <c>textureLoad</c>，但本 Pass 的软件深度测试本来就要点取样，所以不影响结果；</item>
/// <item>折射源是开画前的场景色快照，由 <see cref="Graphics.CopyRenderTarget"/> 在 Pass 外拷好 ——
/// 本 Pass 的颜色附件不能同时被采样。</item>
/// </list>
/// </summary>
internal sealed class WGPUWaterPipeline : Season.Rendering.WaterPipeline
{
    /// <summary>Hi-Z 四级的名字串（逗号分隔）：由常量拼出，帧间不变，建一次即可。</summary>
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
        // 本 Pass 把颜色目标当作混合底图，共享层因此必须交出同一张目标；换一张就说明调用次序错了。
        if (!ReferenceEquals(color, target)) return false;
        _prepared = WebGPUInterop.PrepareWater(_name, color.Name, depth.Name, refraction.Name, frame.Id,
            BuildDrawsJson(inputs.Draws, inputs.Count), inputs.SkyView,
            HiZEffect.Ready ? HzChain : string.Empty, target.Name);
        return _prepared;
    }

    /// <summary>
    /// 按绘制次序把 [槽位, 法线贴图名, 顶点数, 实例数] 编成 JSON；贴图名为 null 表示着色器走程序化波浪。
    /// 顶点/实例数在 P4 起不是常量：分块实例化时顶点数是 subdiv²×6、实例数是 tile 总数，
    /// 所以只能逐 draw 传下去，JS 侧再合成 <c>draw(vc, ic, 0, slot * 256)</c>。
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

    /// <summary>把四级金字塔的名字折成一行；JS 侧按逗号拆开绑定，不单独建缓存。</summary>
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
