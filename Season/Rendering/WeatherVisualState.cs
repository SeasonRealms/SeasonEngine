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
    int _count;
    bool _enabled;
    float _far = 1300;

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
            if (!found) { _ids[i] = null; _wind[i] = default; _phase[i] = 0; }
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
            _regions[_count] = region;
            _slots[_count++] = slot;
        }
    }

    static float Wrap(float value, float period) => value - period * MathF.Floor(value / period);

    public void Apply(ref SceneLightParams lights, RegionalWeatherQuality quality, bool cloudReady)
    {
        lights.WeatherParams0 = lights.WeatherParams1 = default;
        lights.WeatherRegions = default;
        if (!_enabled || quality is not (RegionalWeatherQuality.Low or RegionalWeatherQuality.High)) return;
        bool rain = false, cloud = false;
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
            rain |= state.Rain > 0;
            cloud |= state.Cloud > 0;
        }
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
