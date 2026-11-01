// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Numerics;
using System.Runtime.InteropServices;
using Season.Rendering.Effects;

namespace Season.Rendering;

/// <summary>
/// P2 水面的共享层驱动：每帧做视锥剔除、稳定排序、帧缓冲打包，然后交给后端 <see cref="WaterPipeline"/>。
///
/// 生命周期与 <see cref="DecalEffect"/> 一致：注册在初始化期完成（后端在此时建立 PSO 与根签名），
/// 之后每帧 Prepare 在 Pass 外调用一次，Draw 在 <see cref="RenderPassId.SceneWater"/> 内消费一次。
/// 本帧没有可见水面时 Prepare 返回 false，调用方连 BeginPass 与场景色快照都不需要发 —— 空表就是零开销。
/// </summary>
public sealed class WaterEffect : IDisposable
{
    readonly IGraphics _graphics;
    readonly WaterPipeline _pipeline;
    readonly Vector4[] _frame = new Vector4[WaterMath.VectorCount];
    readonly WaterDraw[] _draws = new WaterDraw[WaterMath.MaxWaters];
    readonly WaterPlane[] _sorted = new WaterPlane[WaterMath.MaxWaters];
    StorageBuffer? _buffer;
    int _count;
    int _pipelineCount;
    bool _disposed;

    internal WaterEffect(IGraphics g, WaterPipeline pipeline)
    {
        _graphics = g;
        _pipeline = pipeline;
    }

    /// <summary>本帧是否已经有 Prepared 的水面表；Draw 只在为 true 时有意义。</summary>
    public bool Ready { get; private set; }

    /// <summary>本帧通过剔除的水面数量，供日志与验收使用。</summary>
    public int VisibleCount => _pipelineCount;

    /// <summary>
    /// 收集并准备本帧的水面。任何一项前提缺失都返回 false 且不改动 Ready，
    /// 调用方据此完全跳过快照与 Pass（不 BeginPass、不写测试计数）。
    /// </summary>
    /// <param name="refractionSource">开画前拷贝的场景色；由调用方在 Prepare 之前准备好。</param>
    /// <param name="time">本帧时间（秒），驱动法线滚动与雨涟漪；来自 <c>BaseApp.Time</c>，暂停时自然冻结。</param>
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

        // 天气与太阳都来自本帧已经冻结的光照快照，未就绪时按「晴天、无太阳」打包：
        // 涟漪项被 0 乘掉，天空兜底色到暗灰，两条分支都不会读未初始化的数据。
        var lights = FrameSchedule.LightingSnapshot;
        bool raining = FrameSchedule.LightingSnapshotValid && lights.WeatherParams1.W > 0;
        SunDirection(lights, FrameSchedule.LightingSnapshotValid, out var sunDirection, out float sunIntensity);
        // 屏幕反射的开关跟 HiZEffect 走：金字塔本帧没重建（关档、没深度、刚 resize）时
        // Enabled 为 0，着色器完全不去采那四张纹理，后端也就可以绑任意占位纹理。
        var reflection = new WaterReflection(
            HiZEffect.Ready && RenderQuality.Current.ScreenSpaceReflections != SsrMode.Off,
            MathF.Max(0f, RenderQuality.Current.SsrIntensity),
            view.Camera.Near, view.Camera.Far,
            MathF.Max(0.01f, RenderQuality.Current.SsrMaxDistance),
            MathF.Max(0.01f, RenderQuality.Current.SsrThickness));
        WaterMath.Pack(_frame, jitteredVp, inverseJitteredVp, view.Position, view.Width, view.Height,
            raining, time, FrameSchedule.SkyViewTexture != null, reflection, sunDirection, sunIntensity,
            _sorted.AsSpan(0, _count));
        _buffer ??= g.CreateStorageBuffer(WaterMath.ByteCount);
        if (_buffer == null || !g.TryUpdateStorageBuffer(_buffer, MemoryMarshal.AsBytes(_frame.AsSpan())))
            return false;
        for (int i = 0; i < _count; i++)
        {
            // 分块参数与 Pack 用同一个 Tiling：帧数据里的 tilesX/tilesZ/subdiv 必须与后端 draw 投出的
            // 顶点数、实例数逐位一致，否则顶点着色器会把 tile 索引解到网格外面去。
            var tiling = WaterMath.Tiling(_sorted[i]);
            _draws[i] = new WaterDraw(i, _sorted[i].NormalTexture, tiling.X, tiling.Z, tiling.Subdiv);
        }
        var inputs = new WaterInputs(color, refractionSource, depth, FrameSchedule.SkyViewTexture,
            _buffer, _draws, _count);
        if (!_pipeline.Prepare(inputs, color)) return false;
        _pipelineCount = _count;
        Ready = true;
        return true;
    }

    /// <summary>
    /// 取太阳（最强的方向光）的方向与强度。方向已按惯例取「指向表面」的传播方向，
    /// 与主着色器里 <c>dot(N, -sunDir)</c> 的用法一致；找不到方向光时返回零强度，水面只剩天空反射。
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

    /// <summary>在 <see cref="RenderPassId.SceneWater"/> 内调用一次；Prepare 未成功时什么都不做。</summary>
    public void Draw(IGraphics g)
    {
        if (!Ready || _disposed || !ReferenceEquals(g, _graphics)) return;
        if (!_pipeline.Draw()) Ready = false;
    }

    /// <summary>
    /// 剔除并排序。排序必须稳定：同 <see cref="WaterPlane.SortOrder"/> 的水面按注册顺序绘制，
    /// 否则每帧次序抖动会让两层水的叠加结果在 TAA 下发闪。
    /// </summary>
    int Collect(SceneWaters source, in Frustum frustum)
    {
        int count = 0;
        var waters = source.Waters;
        for (int i = 0; i < waters.Count && count < WaterMath.MaxWaters; i++)
        {
            var water = waters[i];
            if (!water.Enabled || water.Size.X <= 0f || water.Size.Y <= 0f) continue;
            // 平面没有厚度：用一个极薄的包围盒做视锥测试，俯视与侧视都能正确剔除。
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
