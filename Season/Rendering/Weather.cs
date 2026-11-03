// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// Weather names are for configuration only; the concrete intensity and visual recipe are defined by the app.
/// Thunderstorm and ThunderRain are appended at the end of the enum: saved configuration is serialized by numeric value, so appending does not change the meaning of existing values.
/// Thunderstorm is "thunder only, no rain", and ThunderRain is a thunderstorm with rain: precipitation and electrical activity are supplied together by WeatherSample,
/// and the two coexist within the same region, so the user does not need to stack two regions.
/// </summary>
public enum WeatherKind { Clear, Cloudy, Overcast, LightRain, Rain, HeavyRain, LightSnow, HeavySnow, Thunderstorm, ThunderRain }

/// <summary>
/// A persistable world region. Lengths are in meters and rotations in radians; scalar properties avoid JSON's default of ignoring Vector fields.
/// Modify it by replacing the record with with, never by mutating a configuration that is being saved in place. The cloud extent is independent of the precipitation extent.
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
    /// <summary>Soft-edge width on the minor axis, scaled proportionally along the ellipse; the minimum soft band is 0.0001 of the semi-axis, and 0 means a near-hard edge.</summary>
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
    /// Indoor shelter region. It produces no rain or snow itself and only suppresses the regions ranked after it by coverage weight, so it must rank before every precipitation region.
    /// Its vertical range spans floor to ceiling rather than the full sky height, and rain still falls normally above the roof; the cloud extent must be given an extremely small value separately,
    /// otherwise it would punch a hole in the cloud layer.
    /// </summary>
    public bool Shelter { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || !Enum.IsDefined(Weather))
            throw new ArgumentException("A weather region must have a stable ID and a valid weather kind.");
        if (!float.IsFinite(CenterX) || !float.IsFinite(CenterZ) || !float.IsFinite(Rotation)
            || !float.IsFinite(Bottom) || !float.IsFinite(Top) || Top <= Bottom
            || !Positive(RadiusX) || !Positive(RadiusZ) || !Positive(CloudRadiusX) || !Positive(CloudRadiusZ)
            || !float.IsFinite(CloudBase) || !float.IsFinite(WindX) || !float.IsFinite(WindZ)
            || !float.IsFinite(EdgeFeather) || EdgeFeather < 0
            || !float.IsFinite(TransitionSeconds) || TransitionSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(WeatherRegionSettings), "Weather region parameters must be finite and ranges must be positive.");
    }

    // The reciprocal radius is uploaded to the GPU directly; exclude tiny values that would overflow when inverted.
    static bool Positive(float value) => float.IsFinite(value) && value > 0 && float.IsFinite(1 / value);
}

/// <summary>
/// Normalized rain, snow, weather-cloud intensity, and lightning activity. Separate from the display name, allowing rain and snow to coexist mid-transition.
/// <see cref="Lightning"/> is "electrical activity" rather than the flash brightness of any single frame: it determines the strike interval,
/// while the concrete brightness is computed frame by frame from WeatherVisualState's Poisson schedule and return-stroke envelope, so it decays naturally mid-transition.
/// </summary>
public readonly record struct WeatherSample(float Rain, float Snow, float Cloud, float Lightning = 0)
{
    internal Vector3 Vector => new(Rain, Snow, Cloud);
    internal static WeatherSample From(Vector3 value, float lightning) => new(value.X, value.Y, value.Z, lightning);
    internal void Validate()
    {
        if (!float.IsFinite(Rain) || !float.IsFinite(Snow) || !float.IsFinite(Cloud) || !float.IsFinite(Lightning)
            || Rain < 0 || Snow < 0 || Cloud < 0 || Lightning < 0
            || Rain + Snow > 1.00001f || Cloud > 1 || Lightning > 1)
            throw new ArgumentOutOfRangeException(nameof(WeatherSample));
    }
}

/// <summary>Smooth transition retargeted from the current actual value. The caller supplies frame times that have already handled pauses and hitches.</summary>
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
        float s = t * t * (3 - 2 * t);
        // Lightning shares the same smooth curve as rain and snow; otherwise, when switching to a thunderstorm, the clouds would thicken first while the electrical activity jumps instantly.
        Current = WeatherSample.From(Vector3.Lerp(_from.Vector, _target.Vector, s),
            _from.Lightning + (_target.Lightning - _from.Lightning) * s);
    }
}

/// <summary>Snapshot of the regions for this frame. The record is immutable, so switching at runtime never rewrites the data of an already-published frame.</summary>
public readonly record struct WeatherRegion(WeatherRegionSettings Settings, WeatherSample State);

/// <summary>
/// A bounded spatial weather field. Composed stably by descending priority and then by ID order; a clear region with higher priority also overrides lower-priority precipitation.
/// Each layer consumes the remaining weight to avoid doubling intensity at overlaps; smooth blending is allowed at soft edges, and outside the regions it is strictly clear.
/// Update, sampling, and particle Record are all called from the frame thread. An empty field is zero precipitation, not unconstrained particle emission.
/// </summary>
public sealed class WeatherField
{
    public const int MaxRegions = 8;
    /// <summary>Each GPU record is three float4 rows: position/reciprocal radius, rotation/feather, vertical range/rain-snow intensity.</summary>
    public const int RegionVectorCount = 3;
    readonly WeatherRegion[] _regions = new WeatherRegion[MaxRegions];
    public int Count { get; private set; }
    public ReadOnlySpan<WeatherRegion> Regions => _regions.AsSpan(0, Count);

    public void SetRegions(ReadOnlySpan<WeatherRegion> regions)
    {
        if (regions.Length > MaxRegions) throw new ArgumentOutOfRangeException(nameof(regions));
        // Validate fully first and keep the previous frame on failure; a partial update is not allowed.
        for (int i = 0; i < regions.Length; i++)
        {
            if (regions[i].Settings == null) throw new ArgumentException("A weather region configuration must not be null.");
            regions[i].Settings.Validate();
            regions[i].State.Validate();
            for (int j = 0; j < i; j++)
                if (regions[i].Settings.Id == regions[j].Settings.Id) throw new ArgumentException("Duplicate weather region ID.");
        }
        Count = 0;
        foreach (var region in regions)
            if (region.Settings.Enabled) _regions[Count++] = region;
        // The region limit is tiny, so insertion sort needs no per-frame delegate or temporary array.
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

    // A shelter region always ranks first: it only suppresses precipitation that appears later, carries no intensity of its own, and its priority is decided by semantics rather than configuration.
    static bool ComesBefore(WeatherRegionSettings a, WeatherRegionSettings b)
        => a.Shelter != b.Shelter ? a.Shelter
            : a.Priority > b.Priority || (a.Priority == b.Priority && string.CompareOrdinal(a.Id, b.Id) < 0);

    public WeatherSample Sample(Vector3 worldPosition)
    {
        if (!float.IsFinite(worldPosition.X) || !float.IsFinite(worldPosition.Y) || !float.IsFinite(worldPosition.Z))
            return default;
        Vector3 value = default;
        float lightning = 0, remaining = 1, cloudRemaining = 1;
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
            // Lightning only happens inside clouds, so it shares the same coverage weight as the clouds rather than the precipitation column weight.
            lightning += cloudRemaining * cloud * region.State.Lightning;
            cloudRemaining *= 1 - cloud;
        }
        return WeatherSample.From(value, lightning);
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

    /// <summary>Writes the exact same priority order as CPU sampling; the caller only needs to upload the valid prefix.</summary>
    public int WriteParticleData(Span<Vector4> destination)
    {
        if (destination.Length < Count * RegionVectorCount) throw new ArgumentException("Weather upload buffer is too small.");
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
