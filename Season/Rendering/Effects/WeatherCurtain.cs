// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

/// <summary>独立雨幕合成。复用后端全屏管线的资源快照机制，不持有或消费体积雾资源。</summary>
public sealed class WeatherCompositePipeline(FogCompositePipeline backend) : IDisposable
{
    public bool Prepare(RenderTarget color, RenderTarget depth, StorageBuffer frame,
        ComputeResourceRef ao, float aoIntensity, bool applyAo, RenderTarget destination)
        => backend.Prepare(new(color, depth, frame, "", "", ao, aoIntensity, applyAo), destination);
    public FogCompositeResult Draw() => backend.Draw();
    public void Dispose() => backend.Dispose();
}

/// <summary>不透明之后准备帧数据；独立 WeatherComposite Pass 合成，雾关闭仍可运行。</summary>
public sealed class WeatherCurtainEffect : ComputeEffect, IDisposable
{
    public const int LightingOffset = 8;
    public const int FrameVectorCount = 160;
    readonly Vector4[] _frame = new Vector4[FrameVectorCount];
    IGraphics? _graphics;
    StorageBuffer? _buffer;
    WeatherCompositePipeline? _pipeline;
    RenderTarget? _target;
    bool _prepared;
    public bool Ready { get; private set; }
    public override string Name => "weatherCurtain";
    public override ComputePhase Phase => ComputePhase.AfterOpaque;

    /// <summary>Per view: the curtain is projected through that view's depth and scene color and publishes
    /// <see cref="FrameSchedule.WeatherOutputReady"/> / <see cref="FrameSchedule.WeatherCompositeTarget"/> per view.</summary>
    public override ComputeScope Scope => ComputeScope.View;
    public override ViewFeature RequiredFeature => ViewFeature.Weather;

    public override bool Initialize(IGraphics g)
    {
        if (_graphics != null) throw new InvalidOperationException("雨幕已经初始化。");
        if (g is not ISegmentedSceneGraphics || !g.ComputeSupported || Immediate2DMode.Enabled
            || RenderQuality.Current.RegionalWeather is not (RegionalWeatherQuality.Low or RegionalWeatherQuality.High)
            || FrameSchedule.SceneColor is not { Desc.ColorFormat: RtFormat.Rgba16Float, Desc.SampleCount: <= 1 }
            || FrameSchedule.SceneDepth is not { Desc.DepthFormat: RtFormat.D32Float, Desc.SampleCount: <= 1 }
            || FrameSchedule.RenderWeatherComposite != null) return false;
        _graphics = g;
        try
        {
            _pipeline = g.CreateWeatherCompositePipeline();
            if (_pipeline == null) { Dispose(); return false; }
            _buffer = g.CreateStorageBuffer(FrameVectorCount * 16);
            if (_buffer == null || _buffer.SizeInBytes < FrameVectorCount * 16) { Dispose(); return false; }
            OnResize(g);
            if (_target == null) { Dispose(); return false; }
            FrameSchedule.PrepareWeatherComposite = Prepare;
            FrameSchedule.RenderWeatherComposite = Draw;
            return true;
        }
        catch (Exception ex)
        {
            DeviceServices.BaseApp.AddLog(LogType.Error, $"[WeatherCurtain] 初始化失败：{ex.Message}");
            Dispose(); return false;
        }
    }

    public override void OnResize(IGraphics g)
    {
        if (!ReferenceEquals(g, _graphics)) return;
        Ready = false;
        _prepared = false;
        FrameSchedule.WeatherOutputReady = false;
        if (_target != null) FrameSchedule.ForgetSceneComposite(_target);
        if (ReferenceEquals(FrameSchedule.WeatherCompositeTarget, _target)) FrameSchedule.WeatherCompositeTarget = null;
        var color = FrameSchedule.SceneColor;
        if (color is not { Desc.ColorFormat: RtFormat.Rgba16Float, Desc.SampleCount: <= 1 }
            || color.Desc.Width == 0 || color.Desc.Height == 0) return;
        try
        {
            if (_target == null || _target.Desc.Width != color.Desc.Width || _target.Desc.Height != color.Desc.Height)
            {
                var next = g.CreateRenderTarget(new RenderTargetDesc { ColorFormat = RtFormat.Rgba16Float,
                    NoImplicitDepth = true, SampleCount = 1, Width = color.Desc.Width, Height = color.Desc.Height,
                    MatchBackbufferSize = color.Desc.MatchBackbufferSize, ClearColor = Vector4.Zero });
                if (next == null) return;
                _target?.Dispose(); _target = next;
            }
            FrameSchedule.WeatherCompositeTarget = _target;
        }
        catch (Exception ex)
        {
            DeviceServices.BaseApp.AddLog(LogType.Error, $"[WeatherCurtain] 尺寸重建失败：{ex.Message}");
        }
    }

    public override void Record(IGraphics g)
    {
        if (!ReferenceEquals(g, _graphics)) return;
        Ready = false;
        _prepared = false;
        FrameSchedule.WeatherOutputReady = false;
        if (_target == null || !ReferenceEquals(_target, FrameSchedule.WeatherCompositeTarget)
            || _buffer == null || !FrameSchedule.SceneSegmented || !FrameSchedule.LightingSnapshotValid
            || FrameSchedule.LightingSnapshot.WeatherParams1.W <= 0
            || FrameSchedule.SceneColor is not { } color || FrameSchedule.SceneDepth is not { } depth
            || color.Desc.Width != _target.Desc.Width || color.Desc.Height != _target.Desc.Height
            || depth.Desc.Width != color.Desc.Width || depth.Desc.Height != color.Desc.Height
            || !Matrix4x4.Invert(FrameSchedule.RenderView * FrameSchedule.RenderProjection, out var inverse)) return;
        try
        {
            PackFrame(_frame, inverse, FrameSchedule.LightingSnapshot);
            Ready = FrameSchedule.WeatherOutputReady = g.TryUpdateStorageBuffer(_buffer, MemoryMarshal.AsBytes(_frame.AsSpan()));
        }
        catch (Exception ex)
        {
            DeviceServices.BaseApp.AddLog(LogType.Error, $"[WeatherCurtain] 帧上传失败：{ex.Message}");
        }
    }

    public static void PackFrame(Span<Vector4> frame, Matrix4x4 inverseJitteredVp, in SceneLightParams lights)
    {
        if (frame.Length < FrameVectorCount) throw new ArgumentException("雨幕帧缓冲不足。", nameof(frame));
        frame.Clear();
        MemoryMarshal.Cast<Matrix4x4, Vector4>(MemoryMarshal.CreateReadOnlySpan(ref inverseJitteredVp, 1)).CopyTo(frame);
        frame[4] = lights.CameraPos;
        var copy = lights;
        MemoryMarshal.Cast<SceneLightParams, Vector4>(MemoryMarshal.CreateReadOnlySpan(ref copy, 1)).CopyTo(frame[LightingOffset..]);
    }

    bool Prepare(IGraphics g)
    {
        if (!ReferenceEquals(g, _graphics)) return false;
        bool ready = Ready;
        Ready = false;
        _prepared = false;
        FrameSchedule.WeatherOutputReady = false;
        if (!ready || _pipeline == null || _target == null || _buffer == null
            || FrameSchedule.EffectiveSceneColor is not { } color || FrameSchedule.SceneDepth is not { } depth) return false;
        try
        {
            var ao = FrameSchedule.PostAoTexture;
            _prepared = FrameSchedule.WeatherOutputReady = _pipeline.Prepare(color, depth, _buffer,
                new ComputeResourceRef { TextureName = ao }, RenderQuality.Current.AoIntensity, ao != null, _target);
            return _prepared;
        }
        catch (Exception ex)
        {
            DeviceServices.BaseApp.AddLog(LogType.Error, $"[WeatherCurtain] 合成准备失败：{ex.Message}");
            return false;
        }
    }

    FogCompositeResult Draw(IGraphics g, RenderTarget source)
    {
        bool prepared = _prepared;
        _prepared = false;
        if (!prepared || !ReferenceEquals(g, _graphics) || !ReferenceEquals(source, FrameSchedule.EffectiveSceneColor)) return default;
        FrameSchedule.WeatherOutputReady = false;
        try
        {
            var result = _pipeline!.Draw();
            FrameSchedule.WeatherOutputReady = result.Success;
            return result;
        }
        catch (Exception ex)
        {
            DeviceServices.BaseApp.AddLog(LogType.Error, $"[WeatherCurtain] 合成绘制失败：{ex.Message}");
            return default;
        }
    }

    public override void OnUnregistered() => Dispose();
    public void Dispose()
    {
        Ready = false;
        _prepared = false;
        if (FrameSchedule.PrepareWeatherComposite == Prepare)
        {
            FrameSchedule.PrepareWeatherComposite = null;
            FrameSchedule.RenderWeatherComposite = null;
            FrameSchedule.WeatherOutputReady = false;
        }
        if (_target != null) FrameSchedule.ForgetSceneComposite(_target);
        if (ReferenceEquals(FrameSchedule.WeatherCompositeTarget, _target)) FrameSchedule.WeatherCompositeTarget = null;
        _pipeline?.Dispose(); _buffer?.Dispose(); _target?.Dispose();
        _pipeline = null; _buffer = null; _target = null; _graphics = null;
    }
}
