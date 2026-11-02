// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

public enum WeatherMode { Regional, Showcase }
public enum RegionalWeatherQuality { Off, Low, High }

[InlineArray(WeatherField.MaxRegions * WeatherVisualState.RegionStride)]
public struct WeatherVisualArray
{
    private Vector4 _element0;
}

/// <summary>
/// 区域视觉帧状态。Update 是唯一时钟入口，Apply 仅打包；所有调用均在帧线程。
/// 每区域八行：0 云椭圆；1 旋转/云羽化/强度；2 云基/层距/噪声周期倒数/消光；
/// 3 风偏移/种子/云影强度；4 雨椭圆/羽化/强度；5 底/雨幕顶/雪强/下落相位；
/// 6 云颜色/云底亮度；7 雨幕颜色/消光系数。长度单位均为米。
/// 雷电不占区域行：每次落雷只有一个主导点和一条包络，记在全局的 SceneLightParams.WeatherFlash。
/// </summary>
public sealed class WeatherVisualState
{
    public const int RegionStride = 8;
    public const int HeaderOffset = 86;
    public const int RegionsOffset = 88;
    public const float NoisePeriod = 512;
    readonly string?[] _ids = new string?[WeatherField.MaxRegions];
    readonly Vector2[] _wind = new Vector2[WeatherField.MaxRegions];
    readonly float[] _phase = new float[WeatherField.MaxRegions];
    readonly WeatherRegion[] _regions = new WeatherRegion[WeatherField.MaxRegions];
    readonly int[] _slots = new int[WeatherField.MaxRegions];
    // 雷电调度按槽位保留：切换天气不重建区域时，计数器和静默期不会被重置。
    readonly bool[] _flashActive = new bool[WeatherField.MaxRegions];
    readonly float[] _flashDelay = new float[WeatherField.MaxRegions];
    readonly float[] _flashAge = new float[WeatherField.MaxRegions];
    readonly uint[] _flashStrike = new uint[WeatherField.MaxRegions];
    readonly Vector3[] _flashPoint = new Vector3[WeatherField.MaxRegions];
    int _count;
    bool _enabled;
    float _far = 1300;

    /// <summary>
    /// 本帧主导雷击：xyz 为云内外世界坐标，w 为回击包络亮度（0 表示无闪电）。
    /// 与写入 GPU 的 <see cref="SceneLightParams.WeatherFlash"/> 同一份数据，供应用层驱动瞬态光与调试展示。
    /// </summary>
    public Vector4 Flash { get; private set; }

    /// <summary>
    /// 落雷计数：每发生一次新的雷击就自增。包络亮度分不清「新落雷」和「上一道雷的余辉」，
    /// 所以应用层（雷声、震动）用这个单调计数做边沿检测，而不是看 <see cref="Flash"/> 的亮度。
    /// 天气切出雷暴不回退，重新进入时从原值继续。
    /// </summary>
    public int StrikeSerial { get; private set; }

    /// <summary>最近一次落雷的世界坐标，与 <see cref="StrikeSerial"/> 同步更新；用于按距离计算雷声延迟与音量。</summary>
    public Vector3 StrikePoint { get; private set; }

    /// <summary>
    /// 最近一次地闪的可见通道几何，与 <see cref="StrikeSerial"/> 同步重建。
    ///
    /// 挂在视觉状态上而不是挂在渲染效果上，是因为消费方有两个且都必须看到同一道雷：
    /// 闪电效果拿它生成 ribbon 实例，雷声拿它算最近点距离。云内闪不重建几何，
    /// 于是 <see cref="LightningChannel.Visible"/> 为假，只有 v1 的云内发光。
    /// </summary>
    public LightningChannel Channel { get; } = new();

    /// <summary>通道在地面处的米制半宽。整条通道向上收窄到它的 45%。</summary>
    public float ChannelHalfWidth = 1.1f;

    public void Update(WeatherField field, float seconds, bool enabled, float farDistance)
    {
        ArgumentNullException.ThrowIfNull(field);
        float dt = float.IsFinite(seconds) ? Math.Clamp(seconds, 0, 0.1f) : 0;
        _enabled = enabled;
        _far = float.IsFinite(farDistance) ? Math.Clamp(farDistance, 1, 100000) : 1300;
        // 先释放已删除的 ID，再分配；重排和配方强度变化不影响运动状态。
        for (int i = 0; i < _ids.Length; i++)
        {
            bool found = false;
            foreach (var region in field.Regions) found |= region.Settings.Id == _ids[i];
            if (!found) { _ids[i] = null; _wind[i] = default; _phase[i] = 0; ReleaseFlash(i); }
        }
        _count = 0;
        foreach (var region in field.Regions)
        {
            int slot = Array.IndexOf(_ids, region.Settings.Id);
            if (slot < 0) { slot = Array.IndexOf(_ids, null); _ids[slot] = region.Settings.Id; }
            var s = region.Settings;
            var off = _wind[slot] + new Vector2(Math.Clamp(s.WindX, -40, 40), Math.Clamp(s.WindZ, -40, 40)) * dt;
            _wind[slot] = new(Wrap(off.X, NoisePeriod), Wrap(off.Y, NoisePeriod));
            _phase[slot] = Wrap(_phase[slot] + dt * (12 + 8 * region.State.Rain), 256);
            AdvanceFlash(slot, region, dt);
            _regions[_count] = region;
            _slots[_count++] = slot;
        }
    }

    static float Wrap(float value, float period) => value - period * MathF.Floor(value / period);

    void ReleaseFlash(int slot)
    {
        // 槽位释放后延迟归零，重新分配时按“首次进入雷暴”处理，不会继承上一轮雷击的计时。
        _flashActive[slot] = false;
        _flashDelay[slot] = 0;
        _flashAge[slot] = 1e4f;
        _flashStrike[slot] = 0;
        _flashPoint[slot] = default;
    }

    /// <summary>
    /// 落雷调度：电活动 &gt; 0 时按指数间隔排队，活度越低间隔越长；活度归零时立刻遣散，
    /// 已经击中的包络继续按年龄自然衰减，所以天气切出雷暴时最后一道闪电不会硬切断。
    /// 这里的 dt 已经在 Update 入口限制到 0.1s，所以卡顿帧不会一次跳过整段回击。
    /// </summary>
    void AdvanceFlash(int slot, in WeatherRegion region, float dt)
    {
        if (region.State.Lightning <= 0)
        {
            if (_flashActive[slot]) { _flashActive[slot] = false; _flashDelay[slot] = 0; }
            _flashAge[slot] += dt;
            return;
        }
        if (!_flashActive[slot])
        {
            // 进入雷暴先蓄一段静默再落第一道雷：否则切换天气的那一帧会跟着按钮闪一下。
            _flashActive[slot] = true;
            _flashStrike[slot] = 0;
            _flashAge[slot] += dt;
            _flashDelay[slot] = 0.9f + 1.7f * Hash01(region.Settings.Seed ^ 0x5F356495u);
            return;
        }
        _flashAge[slot] += dt;
        _flashDelay[slot] -= dt;
        if (_flashDelay[slot] > 0) return;
        _flashStrike[slot]++;
        _flashAge[slot] = 0;
        _flashPoint[slot] = FlashPoint(region, _flashStrike[slot]);
        _flashDelay[slot] = StrikeInterval(region.Settings.Seed, _flashStrike[slot], region.State.Lightning);
        // 通道几何在这里一次性生成。code 与 FlashPoint / FlashEnvelope 里的表达式必须逐字一致：
        // 三者派生自同一枚种子，改动任何一处都会让"落点、包络、形状"来自三道不同的雷。
        // 从云内击穿点到地面，地面高度取天空参数里那块虚拟地面，通道因此贴在地面而不是穿进去。
        Channel.Build((region.Settings.Seed * 0x9E3779B9u) ^ (_flashStrike[slot] * 0x85EBCA6Bu),
            _flashPoint[slot], region.Settings.CloudBase, SkyLighting.ReflectionGroundHeight, ChannelHalfWidth);
        // 先落点、再发信号：消费方（雷声）读到的坐标一定属于刚刚计数的那道雷。
        StrikePoint = _flashPoint[slot];
        StrikeSerial++;
    }

    /// <summary>两次落雷之间的静默秒数。活度 1 时落在 2.6~9.0 秒，活度越低间隔按反比拉长。</summary>
    static float StrikeInterval(uint seed, uint strike, float activity)
        => (2.6f + 6.4f * Hash01(seed * 0x9E3779B9u + strike * 0x85EBCA6Bu))
            / Math.Clamp(activity, 0.2f, 1f);

    /// <summary>
    /// 回击包络：一次落雷包含四道递减的回击，间隔与幅度都由种子决定，单道形状是 6ms 上升叠 75ms 指数衰减。
    /// 人眼看到的雷电几乎全由包络而不是单帧脉冲决定，所以亮度必须是时间的连续函数，
    /// 否则每帧随机取一个值就成了频闪噪声。返回值 clamp 到 1。
    /// </summary>
    static float FlashEnvelope(float age, uint code)
    {
        if (!(age >= 0) || age > 1.2f) return 0;
        float total = 0, start = 0, amplitude = 1;
        for (uint k = 0; k < 4; k++)
        {
            if (k > 0)
            {
                start += 0.05f + 0.085f * Hash01(code + 0x27D4EB2Fu * k);
                amplitude *= 0.42f + 0.26f * Hash01(code ^ (0x165667B1u * k));
            }
            float d = age - start;
            if (d < 0) break;
            total += amplitude * (1 - MathF.Exp(-d / 0.006f)) * MathF.Exp(-d / 0.075f);
        }
        return Math.Clamp(total, 0, 1);
    }

    /// <summary>
    /// 雷击点在云椭圆内的位置。半径取 sqrt 以保证在椭圆面积上均匀，否则会向中心聚集；
    /// 高度取云基到第二层之间，留出 0.35 的余量避开云板底面的硬边。
    /// 旋转约定与 shader 的 WeatherFootprint 一致：local = Rᵀ·(world-center)，这里做的是反变换。
    /// </summary>
    static Vector3 FlashPoint(in WeatherRegion region, uint strike)
    {
        var s = region.Settings;
        float layer = 35 + 85 * region.State.Cloud;
        uint code = (s.Seed * 0x9E3779B9u) ^ (strike * 0x85EBCA6Bu);
        float angle = Hash01(code) * MathF.Tau;
        float radius = MathF.Sqrt(Hash01(code ^ 0x68BC21EBu)) * 0.78f;
        float c = MathF.Cos(s.Rotation), n = MathF.Sin(s.Rotation);
        float lx = radius * MathF.Cos(angle) * s.CloudRadiusX;
        float lz = radius * MathF.Sin(angle) * s.CloudRadiusZ;
        return new(s.CenterX + c * lx - n * lz,
            s.CloudBase + layer * (0.35f + Hash01(code + 0x2545F491u)),
            s.CenterZ + n * lx + c * lz);
    }

    /// <summary>整数雪崩哈希取 [0,1)。只用于调度与位置，不参与渲染积分，不需要任何分布性质。
    /// 与通道几何共用同一条公式，见 <see cref="LightningChannel.Hash01"/>。</summary>
    static float Hash01(uint value) => LightningChannel.Hash01(value);

    public void Apply(ref SceneLightParams lights, RegionalWeatherQuality quality, bool cloudReady)
    {
        lights.WeatherParams0 = lights.WeatherParams1 = default;
        lights.WeatherRegions = default;
        lights.WeatherFlash = default;
        Flash = default;
        if (!_enabled || quality is not (RegionalWeatherQuality.Low or RegionalWeatherQuality.High)) return;
        bool rain = false, cloud = false;
        Vector4 flash = default;
        for (int i = 0; i < _count; i++)
        {
            var region = _regions[i]; var s = region.Settings; var state = region.State;
            int b = i * RegionStride, slot = _slots[i];
            // 不改变持久化云基；雨幕允许向上补到近景降水顶面。
            float top = MathF.Max(s.CloudBase, s.Top);
            var illumination = SkyLighting.WeatherIllumination(s.CloudBase);
            if (!cloudReady)
                illumination = Vector3.Max(new(lights.Ambient.X, lights.Ambient.Y, lights.Ambient.Z), new Vector3(0.015f));
            lights.WeatherRegions[b] = new(s.CenterX, s.CenterZ, 1 / s.CloudRadiusX, 1 / s.CloudRadiusZ);
            lights.WeatherRegions[b + 1] = new(MathF.Cos(s.Rotation), MathF.Sin(s.Rotation),
                Math.Clamp(s.EdgeFeather / MathF.Min(s.CloudRadiusX, s.CloudRadiusZ), 0.0001f, 1), state.Cloud);
            lights.WeatherRegions[b + 2] = new(s.CloudBase, 35 + 85 * state.Cloud, 1 / NoisePeriod, 0.008f + 0.05f * state.Cloud);
            lights.WeatherRegions[b + 3] = new(_wind[slot], s.Seed & 0x00FFFFFFu, 0.9f);
            lights.WeatherRegions[b + 4] = new(1 / s.RadiusX, 1 / s.RadiusZ,
                Math.Clamp(s.EdgeFeather / MathF.Min(s.RadiusX, s.RadiusZ), 0.0001f, 1), state.Rain);
            lights.WeatherRegions[b + 5] = new(s.Bottom, top, state.Snow, _phase[slot]);
            lights.WeatherRegions[b + 6] = new(illumination * new Vector3(0.9f, 0.92f, 0.96f), 0.38f - 0.22f * state.Cloud);
            // 雨幕的散射色由云底遮挡决定：厚云下雨幕只可能借到云底的亮度，不能按地面辐照度直射发亮，
            // 否则远景会变成比天空更亮的灰墙，岛屿轮廓和近远交界被抹平。
            lights.WeatherRegions[b + 7] = new(illumination * new Vector3(0.92f, 0.97f, 1.08f) * (0.30f - 0.14f * state.Cloud), 0.006f);
            // 取当前最亮的一路。同一帧出现两处可分辨的独立闪光在物理上可能，但在 8 区域的尺度下
            // 观众只会看到同一片云的方向，多存一处换不到额外信息。
            float strength = state.Lightning > 0
                ? FlashEnvelope(_flashAge[slot], (s.Seed * 0x9E3779B9u) ^ (_flashStrike[slot] * 0x85EBCA6Bu)) * state.Lightning
                : 0;
            if (strength > flash.W) flash = new(_flashPoint[slot], strength);
            rain |= state.Rain > 0;
            cloud |= state.Cloud > 0;
        }
        lights.WeatherFlash = Flash = flash;
        lights.WeatherParams0 = new(_count, cloudReady && cloud ? 1 : 0,
            quality == RegionalWeatherQuality.Low ? 1 : 2, quality == RegionalWeatherQuality.Low ? 12 : 24);
        lights.WeatherParams1 = new(18, 40, _far, rain ? 1 : 0);
    }

    /// <summary>与 shader 同形的有限椭圆柱求交，用于边界与深度裁剪验收。</summary>
    public static Vector2 Intersect(WeatherRegionSettings s, Vector3 origin, Vector3 direction, float distance)
    {
        if (!(distance > 0) || !float.IsFinite(distance)) return default;
        double c = Math.Cos(s.Rotation), n = Math.Sin(s.Rotation);
        double x = origin.X - s.CenterX, z = origin.Z - s.CenterZ;
        double ox = (c * x + n * z) / s.RadiusX, oz = (-n * x + c * z) / s.RadiusZ;
        double dx = (c * direction.X + n * direction.Z) / s.RadiusX;
        double dz = (-n * direction.X + c * direction.Z) / s.RadiusZ;
        double a = dx * dx + dz * dz, b = ox * dx + oz * dz, q = ox * ox + oz * oz - 1;
        double lo = 0, hi = distance;
        if (a < 1e-20) { if (q > 0) return default; }
        else
        {
            double discriminant = b * b - a * q;
            if (discriminant <= 0) return default;
            double root = Math.Sqrt(discriminant);
            lo = Math.Max(lo, (-b - root) / a); hi = Math.Min(hi, (-b + root) / a);
        }
        double top = Math.Max(s.CloudBase, s.Top);
        if (Math.Abs(direction.Y) < 1e-10)
        { if (origin.Y < s.Bottom || origin.Y > top) return default; }
        else
        {
            double y0 = (s.Bottom - origin.Y) / direction.Y, y1 = (top - origin.Y) / direction.Y;
            lo = Math.Max(lo, Math.Min(y0, y1)); hi = Math.Min(hi, Math.Max(y0, y1));
        }
        return hi > lo ? new((float)lo, (float)hi) : default;
    }
}
