// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Collections.Concurrent;
using System.Numerics;
using Season.Basic;
using Season.Rendering;
using Season.Rendering.Effects;

namespace Engine.Management;

/// <summary>
/// 管理天气配置与空间快照；视觉配方由 WeatherPrecipitationEffect 消费，不改动旧天空与树林雾。
/// UI 排队请求，Update 在帧线程修改配置并发布结果；粒子与后续云层共用同一空间场。
/// </summary>
internal sealed class WeatherController
{
    internal const string IslandId = "island", OffshoreId = "east-offshore";
    readonly ConcurrentQueue<(string Id, WeatherKind Kind)> _requests = new();
    readonly Dictionary<string, (WeatherKind Kind, WeatherTransition Transition)> _states = new();
    readonly WeatherRegion[] _snapshot = new WeatherRegion[WeatherField.MaxRegions];
    internal WeatherField Field { get; } = new();

    /// <summary>
    /// 室内遮挡区域。来自场景几何而不是用户配置，因此既不写入 WorldSettings 也不参与状态过渡；
    /// 每帧重建快照时追加在配置区域之后，排序不变量保证它总是最先消耗覆盖权重。
    /// </summary>
    internal WeatherRegionSettings? Cover { get; set; }

    // Rocks 位于 X=+75；区域以世界坐标固定，转动或移动相机不改变天气所在位置。
    static readonly WeatherRegionSettings Island = new()
    {
        Id = IslandId, Priority = 10, RadiusX = 145, RadiusZ = 145, EdgeFeather = 30,
        Bottom = -5, Top = 90, CloudRadiusX = 300, CloudRadiusZ = 300, Seed = 2026,
    };
    static readonly WeatherRegionSettings Offshore = new()
    {
        Id = OffshoreId, CenterX = 400, RadiusX = 180, RadiusZ = 230, EdgeFeather = 45,
        Bottom = -8, Top = 110, CloudRadiusX = 300, CloudRadiusZ = 420, Seed = 2027,
    };

    WeatherRegionSettings[]? _configuration;

    internal WeatherController(WorldSettings settings)
    {
        Update(settings, 0);
    }

    WeatherRegionSettings[] Normalize(WeatherRegionSettings[]? saved)
    {
        // 遮挡区不占用户配额：有遮挡时给配置区少留一格，否则 SetRegions 会因超限抛错。
        int capacity = WeatherField.MaxRegions - (Cover != null ? 1 : 0);
        // 不覆盖用户已保存的范围；损坏条目才丢弃。复制数组避免保存线程枚举时被原地修改。
        var regions = new List<WeatherRegionSettings>();
        foreach (var region in saved ?? [])
        {
            if (region == null) continue;
            try { region.Validate(); }
            catch (ArgumentException) { continue; }
            if (!regions.Any(r => r.Id == region.Id)) regions.Add(region);
        }
        // 双区域始终保留槽位，其余区域仍能通过配置扩展。
        var island = regions.Find(r => r.Id == IslandId) ?? Island;
        var offshore = regions.Find(r => r.Id == OffshoreId) ?? Offshore;
        return new[] { island, offshore }
            .Concat(regions.Where(r => r.Id != IslandId && r.Id != OffshoreId))
            .Take(capacity).ToArray();
    }

    internal void Request(string id, WeatherKind kind)
    {
        if (id is not (IslandId or OffshoreId) || !Enum.IsDefined(kind)) return;
        _requests.Enqueue((id, kind));
    }

    /// <returns>是否消费了需保存的设置更改。</returns>
    internal bool Update(WorldSettings settings, float seconds)
    {
        bool changed = false;
        if (_configuration == null || !ReferenceEquals(_configuration, settings.WeatherRegions))
        {
            settings.WeatherRegions = Normalize(settings.WeatherRegions);
            foreach (var id in _states.Keys.ToArray())
                if (!settings.WeatherRegions.Any(r => r.Id == id)) _states.Remove(id);
            changed = true;
        }
        while (_requests.TryDequeue(out var request))
        {
            int index = Array.FindIndex(settings.WeatherRegions, r => r.Id == request.Id);
            if (index < 0 || settings.WeatherRegions[index].Weather == request.Kind) continue;
            var copy = (WeatherRegionSettings[])settings.WeatherRegions.Clone();
            copy[index] = copy[index] with { Weather = request.Kind };
            settings.WeatherRegions = copy;
            changed = true;
        }
        int count = 0;
        foreach (var config in settings.WeatherRegions)
        {
            if (!_states.TryGetValue(config.Id, out var state))
                state = (config.Weather, new WeatherTransition(Profile(config.Weather)));
            else if (state.Kind != config.Weather)
            {
                state.Transition.Retarget(Profile(config.Weather), config.TransitionSeconds);
                state.Kind = config.Weather;
            }
            state.Transition.Advance(float.IsFinite(seconds) ? Math.Clamp(seconds, 0, 0.25f) : 0);
            _states[config.Id] = state;
            _snapshot[count++] = new WeatherRegion(config, state.Transition.Current);
        }
        // 遮挡区不带强度，只在 GPU 与 CPU 两侧按同一覆盖规则压掉排在它之后的降水。
        if (Cover != null && count < WeatherField.MaxRegions)
            _snapshot[count++] = new WeatherRegion(Cover, new WeatherSample(0, 0, 0));
        Field.SetRegions(_snapshot.AsSpan(0, count));
        _configuration = settings.WeatherRegions;
        return changed;
    }

    internal string Status(string id)
    {
        if (!_states.TryGetValue(id, out var state)) return "晴";
        return Text(state.Kind) + (state.Transition.Progress < 1 ? $" {state.Transition.Progress:P0}" : "");
    }

    // 强度只是业务信号；雨雪效果把当前实际值映射为视觉配方，不能直接用目标枚举切换。
    internal static WeatherSample Profile(WeatherKind kind) => kind switch
    {
        WeatherKind.Cloudy => new(0, 0, 0.35f),
        WeatherKind.Overcast => new(0, 0, 0.8f),
        WeatherKind.LightRain => new(0.2f, 0, 0.6f),
        WeatherKind.Rain => new(0.5f, 0, 0.8f),
        WeatherKind.HeavyRain => new(1, 0, 1),
        WeatherKind.LightSnow => new(0, 0.2f, 0.55f),
        WeatherKind.HeavySnow => new(0, 1, 0.9f),
        _ => default,
    };

    internal static string Text(WeatherKind kind) => kind switch
    {
        WeatherKind.Cloudy => "多云", WeatherKind.Overcast => "阴",
        WeatherKind.LightRain => "小雨", WeatherKind.Rain => "中雨", WeatherKind.HeavyRain => "大雨",
        WeatherKind.LightSnow => "小雪", WeatherKind.HeavySnow => "大雪", _ => "晴",
    };
}

/// <summary>
/// 第二阶段的近景雨雪。每个区域保留一对系统、独立风和种子；只注册这一层 compute 调度，
/// 子系统仅初始化提交路由，由此处逐帧 Record，避免时间或派发重复。云、远景雨幕和碰撞不在此层实现。
/// </summary>
internal sealed class WeatherPrecipitationEffect(WeatherField field, bool reducedBudget = false) : ComputeEffect, IDisposable
{
    internal const uint RainBudget = 8192, SnowBudget = 4096;
    readonly RegionParticles?[] _systems = new RegionParticles[WeatherField.MaxRegions];
    readonly WeatherRegion[] _mask = new WeatherRegion[WeatherField.MaxRegions];
    IGraphics? _graphics;
    float _pendingDelta, _daylight = 1;

    internal bool Enabled { get; set; } = true;
    public override string Name => "weatherPrecipitation";
    public override ComputePhase Phase => ComputePhase.FrameStart;

    internal sealed class RegionParticles : IDisposable
    {
        internal string Id = "";
        internal readonly WeatherField RainMask = new(), SnowMask = new();
        internal readonly ParticleSystemEffect Rain, Snow;
        internal bool Ready;

        internal RegionParticles(bool reduced)
        {
            Rain = new ParticleSystemEffect(reduced ? RainBudget / 2 : RainBudget)
            {
                Weather = RainMask, WeatherChannel = 1, Enabled = false,
                ExternalTime = true, CameraWindow = true, SoftDensity = true,
                BoxRadius = 28, ViewDistance = 28, FadeIn = 0.025f, FadeOut = 0.035f,
            };
            Snow = new ParticleSystemEffect(reduced ? SnowBudget / 2 : SnowBudget)
            {
                Weather = SnowMask, WeatherChannel = 2, Enabled = false,
                ExternalTime = true, CameraWindow = true, SoftDensity = true,
                BoxRadius = 24, ViewDistance = 24, FadeIn = 0.025f, FadeOut = 0.035f,
            };
        }

        public void Dispose() { Rain.Dispose(); Snow.Dispose(); Ready = false; }
    }

    internal RegionParticles? Find(string id)
    {
        foreach (var system in _systems)
            if (system?.Id == id) return system;
        return null;
    }

    bool Contains(string id)
    {
        foreach (var region in field.Regions)
            if (region.Settings.Id == id) return true;
        return false;
    }

    RegionParticles Acquire(string id, IGraphics g)
    {
        var existing = Find(id);
        if (existing != null) return existing;
        for (int i = 0; i < _systems.Length; i++)
        {
            var slot = _systems[i];
            if (slot != null && Contains(slot.Id)) continue;
            if (slot == null)
            {
                slot = new RegionParticles(reducedBudget);
                _systems[i] = slot;
                // 初始化失败时粒子自身清理资源；连带释放另一半，不能留下半对提交路由。
                slot.Ready = slot.Rain.Initialize(g) && slot.Snow.Initialize(g);
                if (!slot.Ready) slot.Dispose();
            }
            // 移除/新增区域复用已分配槽，不在天气切换时反复编译 shader 或重新分配 GPU 缓冲。
            slot.Id = id;
            return slot;
        }
        throw new InvalidOperationException("天气区域数量超过粒子槽位上限。");
    }

    public override bool Initialize(IGraphics g)
    {
        if (_graphics != null) throw new InvalidOperationException("雨雪效果已经初始化。");
        if (!g.ComputeSupported || !g.IndirectDrawSupported) return false;
        _graphics = g;
        // 遮挡区只压制降水，自己不派发粒子；为它分配两套满额系统纯属浪费显存与初始化时间。
        foreach (var region in field.Regions)
            if (!region.Settings.Shelter && !Acquire(region.Settings.Id, g).Ready) { Dispose(); return false; }
        return true;
    }

    /// <summary>实际帧时间，不乘昼夜倍率；Record 消费一次，未更新时保持冻结。</summary>
    internal void Advance(float seconds, float daylight)
    {
        _pendingDelta = MathF.Min(0.1f, _pendingDelta + (float.IsFinite(seconds) ? Math.Clamp(seconds, 0, 0.1f) : 0));
        _daylight = float.IsFinite(daylight) ? Math.Clamp(daylight, 0, 1) : 0;
    }

    public override void Record(IGraphics g)
    {
        if (!ReferenceEquals(g, _graphics)) return;
        float delta = _pendingDelta;
        _pendingDelta = 0;
        bool validCamera = Matrix4x4.Invert(FrameSchedule.RenderView, out var inverse);
        Vector3 camera = inverse.Translation;
        foreach (var system in _systems)
            if (system != null) { system.Rain.Enabled = false; system.Snow.Enabled = false; }
        foreach (var region in field.Regions)
        {
            if (region.Settings.Shelter) continue;
            var system = Acquire(region.Settings.Id, g);
            if (!system.Ready) continue;
            // 其他区域仍消耗覆盖权重，只把当前区域强度改为单位基。
            // 强度交给 Density，避免再次乘 alpha 导致小雨变为强度平方；晴区遮罩也不会丢失。
            FillMask(system.RainMask, region.Settings.Id, new(1, 0, 0));
            FillMask(system.SnowMask, region.Settings.Id, new(0, 1, 0));
            ApplyRecipe(system.Rain, region.Settings, region.State.Rain, false, camera, validCamera);
            ApplyRecipe(system.Snow, region.Settings, region.State.Snow, true, camera, validCamera);
        }
        foreach (var system in _systems)
        {
            if (system is not { Ready: true }) continue;
            system.Rain.Advance(delta); system.Snow.Advance(delta);
            // 停用系统也要 Record 以清除上一帧 ready 和待消费时间，但不会上传、派发或绘制。
            system.Rain.Record(g); system.Snow.Record(g);
        }
    }

    void FillMask(WeatherField destination, string id, WeatherSample unit)
    {
        int count = 0;
        foreach (var region in field.Regions)
            _mask[count++] = new(region.Settings, region.Settings.Id == id ? unit : default);
        destination.SetRegions(_mask.AsSpan(0, count));
    }

    void ApplyRecipe(ParticleSystemEffect effect, WeatherRegionSettings region, float strength,
        bool snow, Vector3 camera, bool validCamera)
    {
        float height = (float)((double)region.Top - region.Bottom);
        if (!float.IsFinite(height)) return;
        effect.WorldOrigin = new(region.CenterX, (float)(region.Bottom + 0.3 * height), region.CenterZ);
        effect.BoxHeight = height;
        // 世界垂直落差覆盖区域，XZ 只选相机附近周期窗口；登高不会带着整片粒子向上平移。
        effect.WindVelocity = new(Math.Clamp(region.WindX, -40, 40), 0, Math.Clamp(region.WindZ, -40, 40));
        effect.Seed = region.Seed ^ (snow ? 0x005A17u : 0u);
        effect.Density = strength;
        effect.FallSpeed = snow ? 1.1f + 0.7f * strength : 12 + 8 * strength;
        effect.Sway = snow ? 1.2f : 0.08f;
        effect.ParticleSize = snow ? 0.028f + 0.022f * strength : 0.008f + 0.006f * strength;
        effect.Stretch = snow ? 0 : 0.018f + 0.016f * strength;
        float illumination = 0.08f + 0.92f * MathF.Sqrt(_daylight);
        var color = (snow ? new Vector3(0.95f, 1, 1.05f) : new Vector3(0.55f, 0.65f, 0.78f)) * illumination;
        effect.Color = new(color, snow ? 0.65f + 0.15f * strength : 0.28f + 0.12f * strength);
        // 保守球界只跳过完全远离近景窗口的区域，不能只采相机点，否则站在雨区外就看不到边缘。
        double dx = (double)camera.X - region.CenterX, dz = (double)camera.Z - region.CenterZ;
        double reach = Math.Max(region.RadiusX, region.RadiusZ) + effect.ViewDistance;
        effect.Enabled = Enabled && validCamera && strength > 0 && dx * dx + dz * dz <= reach * reach
            && camera.Y + effect.ViewDistance >= region.Bottom && camera.Y - effect.ViewDistance <= region.Top;
    }

    public override void OnUnregistered() => Dispose();

    public void Dispose()
    {
        foreach (var system in _systems) system?.Dispose();
        Array.Clear(_systems);
        _pendingDelta = 0;
        _graphics = null;
    }
}
