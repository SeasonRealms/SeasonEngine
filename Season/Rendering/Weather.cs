// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>天气名称只用于配置；具体强度和视觉配方由应用定义。</summary>
public enum WeatherKind { Clear, Cloudy, Overcast, LightRain, Rain, HeavyRain, LightSnow, HeavySnow }

/// <summary>
/// 可持久化的世界区域。长度使用米、旋转使用弧度；标量属性避免 JSON 默认忽略 Vector 字段。
/// 修改时使用 with 替换记录，不原地修改正在保存的配置。云的范围独立于降水范围。
/// </summary>
public sealed record WeatherRegionSettings
{
    public string Id { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public WeatherKind Weather { get; init; }
    public int Priority { get; init; }
    public float CenterX { get; init; }
    public float CenterZ { get; init; }
    public float RadiusX { get; init; } = 100;
    public float RadiusZ { get; init; } = 100;
    public float Rotation { get; init; }
    /// <summary>短半轴上的软边宽度，沿椭圆同比缩放；最小软带为半轴的 0.0001，0 表示近似硬边。</summary>
    public float EdgeFeather { get; init; } = 20;
    public float Bottom { get; init; } = -5;
    public float Top { get; init; } = 80;
    public float CloudRadiusX { get; init; } = 300;
    public float CloudRadiusZ { get; init; } = 300;
    public float CloudBase { get; init; } = 600;
    public float WindX { get; init; }
    public float WindZ { get; init; }
    public uint Seed { get; init; }
    public float TransitionSeconds { get; init; } = 4;
    /// <summary>
    /// 室内遮挡区域。自身不产生任何雨雪，只按覆盖权重压掉排在它之后的区域，因此必须排在所有降水区之前。
    /// 纵向范围取地板到天花板而不是整个天空高度，屋顶以上仍照常降水；云范围必须另行给到极小值，
    /// 否则它会在云层里开一个洞。
    /// </summary>
    public bool Shelter { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || !Enum.IsDefined(Weather))
            throw new ArgumentException("天气区域必须具有稳定 ID 和有效天气类型。");
        if (!float.IsFinite(CenterX) || !float.IsFinite(CenterZ) || !float.IsFinite(Rotation)
            || !float.IsFinite(Bottom) || !float.IsFinite(Top) || Top <= Bottom
            || !Positive(RadiusX) || !Positive(RadiusZ) || !Positive(CloudRadiusX) || !Positive(CloudRadiusZ)
            || !float.IsFinite(CloudBase) || !float.IsFinite(WindX) || !float.IsFinite(WindZ)
            || !float.IsFinite(EdgeFeather) || EdgeFeather < 0
            || !float.IsFinite(TransitionSeconds) || TransitionSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(WeatherRegionSettings), "天气区域参数必须有限，范围必须为正。");
    }

    // 半径倒数会直接上传 GPU；排除会在取倒数时溢出的极小值。
    static bool Positive(float value) => float.IsFinite(value) && value > 0 && float.IsFinite(1 / value);
}

/// <summary>归一化的雨、雪、天气云强度。与显示名称分离，允许过渡途中雨雪共存。</summary>
public readonly record struct WeatherSample(float Rain, float Snow, float Cloud)
{
    internal Vector3 Vector => new(Rain, Snow, Cloud);
    internal static WeatherSample From(Vector3 value) => new(value.X, value.Y, value.Z);
    internal void Validate()
    {
        if (!float.IsFinite(Rain) || !float.IsFinite(Snow) || !float.IsFinite(Cloud)
            || Rain < 0 || Snow < 0 || Cloud < 0 || Rain + Snow > 1.00001f || Cloud > 1)
            throw new ArgumentOutOfRangeException(nameof(WeatherSample));
    }
}

/// <summary>从当前实际值重定向的平滑切换。调用者提供经过暂停和卡顿处理的帧时间。</summary>
public sealed class WeatherTransition
{
    WeatherSample _from, _target;
    float _elapsed, _duration;
    public WeatherSample Current { get; private set; }
    public float Progress => _duration > 0 ? Math.Clamp(_elapsed / _duration, 0, 1) : 1;

    public WeatherTransition(WeatherSample initial)
    {
        initial.Validate();
        Current = _from = _target = initial;
    }

    public void Retarget(WeatherSample target, float seconds)
    {
        target.Validate();
        if (!float.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        _from = Current;
        _target = target;
        _duration = seconds;
        _elapsed = 0;
        if (seconds == 0) Current = target;
    }

    public void Advance(float seconds)
    {
        if (!float.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        _elapsed = MathF.Min(_duration, _elapsed + seconds);
        float t = Progress;
        Current = WeatherSample.From(Vector3.Lerp(_from.Vector, _target.Vector, t * t * (3 - 2 * t)));
    }
}

/// <summary>本帧区域快照。记录不可变，运行中切换不会改写已发布帧的数据。</summary>
public readonly record struct WeatherRegion(WeatherRegionSettings Settings, WeatherSample State);

/// <summary>
/// 有界的空间天气场。按优先级降序、ID 序稳定合成；高优先级的晴天也会覆盖低优先级降水。
/// 每层消耗剩余权重，避免重叠处强度翻倍；软边处允许平滑混合，区域外严格为晴。
/// 更新、采样和粒子 Record 均由帧线程调用。空场是零降水，不是无约束粒子。
/// </summary>
public sealed class WeatherField
{
    public const int MaxRegions = 8;
    /// <summary>GPU 记录为三行 float4：位置/半径倒数，旋转/羽化，垂直范围/雨雪强度。</summary>
    public const int RegionVectorCount = 3;
    readonly WeatherRegion[] _regions = new WeatherRegion[MaxRegions];
    public int Count { get; private set; }
    public ReadOnlySpan<WeatherRegion> Regions => _regions.AsSpan(0, Count);

    public void SetRegions(ReadOnlySpan<WeatherRegion> regions)
    {
        if (regions.Length > MaxRegions) throw new ArgumentOutOfRangeException(nameof(regions));
        // 先完整验证，失败时保留上一帧；不允许半更新。
        for (int i = 0; i < regions.Length; i++)
        {
            if (regions[i].Settings == null) throw new ArgumentException("天气区域配置不能为空。");
            regions[i].Settings.Validate();
            regions[i].State.Validate();
            for (int j = 0; j < i; j++)
                if (regions[i].Settings.Id == regions[j].Settings.Id) throw new ArgumentException("天气区域 ID 重复。");
        }
        Count = 0;
        foreach (var region in regions)
            if (region.Settings.Enabled) _regions[Count++] = region;
        // 区域上限很小，插入排序无需每帧委托或临时数组。
        for (int i = 1; i < Count; i++)
        {
            var value = _regions[i];
            int j = i - 1;
            while (j >= 0 && ComesBefore(value.Settings, _regions[j].Settings))
            {
                _regions[j + 1] = _regions[j];
                j--;
            }
            _regions[j + 1] = value;
        }
    }

    // 遮挡区永远排在最前：它只压制后出现的降水，自己不带强度，优先级由语义而非配置决定。
    static bool ComesBefore(WeatherRegionSettings a, WeatherRegionSettings b)
        => a.Shelter != b.Shelter ? a.Shelter
            : a.Priority > b.Priority || (a.Priority == b.Priority && string.CompareOrdinal(a.Id, b.Id) < 0);

    public WeatherSample Sample(Vector3 worldPosition)
    {
        if (!float.IsFinite(worldPosition.X) || !float.IsFinite(worldPosition.Y) || !float.IsFinite(worldPosition.Z))
            return default;
        Vector3 value = default;
        float remaining = 1, cloudRemaining = 1;
        foreach (var region in Regions)
        {
            var s = region.Settings;
            float weight = FootprintWeight(worldPosition, s, false);
            if (worldPosition.Y < s.Bottom || worldPosition.Y > s.Top) weight = 0;
            value.X += remaining * weight * region.State.Rain;
            value.Y += remaining * weight * region.State.Snow;
            remaining *= 1 - weight;
            float cloud = FootprintWeight(worldPosition, s, true);
            value.Z += cloudRemaining * cloud * region.State.Cloud;
            cloudRemaining *= 1 - cloud;
        }
        return WeatherSample.From(value);
    }

    static float FootprintWeight(Vector3 p, WeatherRegionSettings s, bool cloud)
    {
        float rx = cloud ? s.CloudRadiusX : s.RadiusX, rz = cloud ? s.CloudRadiusZ : s.RadiusZ;
        float x = p.X - s.CenterX, z = p.Z - s.CenterZ;
        float c = MathF.Cos(s.Rotation), n = MathF.Sin(s.Rotation);
        float r = new Vector2((c * x + n * z) / rx, (-n * x + c * z) / rz).Length();
        float feather = Math.Clamp(s.EdgeFeather / MathF.Min(rx, rz), 0.0001f, 1);
        float t = Math.Clamp((1 - r) / feather, 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>写入与 CPU 采样完全相同的优先级顺序；调用者上传有效前缀即可。</summary>
    public int WriteParticleData(Span<Vector4> destination)
    {
        if (destination.Length < Count * RegionVectorCount) throw new ArgumentException("天气上传缓冲过小。");
        for (int i = 0; i < Count; i++)
        {
            var s = _regions[i].Settings;
            var state = _regions[i].State;
            destination[i * 3] = new Vector4(s.CenterX, s.CenterZ, 1 / s.RadiusX, 1 / s.RadiusZ);
            destination[i * 3 + 1] = new Vector4(MathF.Cos(s.Rotation), MathF.Sin(s.Rotation),
                Math.Clamp(s.EdgeFeather / MathF.Min(s.RadiusX, s.RadiusZ), 0.0001f, 1), 0);
            destination[i * 3 + 2] = new Vector4(s.Bottom, s.Top, state.Rain, state.Snow);
        }
        return Count * RegionVectorCount;
    }
}
