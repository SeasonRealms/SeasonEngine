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
/// Regional visual frame state. Update is the only clock entry point; Apply only packs. All calls occur
/// on the frame thread. Eight rows per region: 0 cloud ellipse; 1 rotation/cloud feathering/intensity;
/// 2 cloud base/layer spacing/reciprocal noise period/extinction; 3 wind offset/seed/cloud shadow
/// intensity; 4 rain ellipse/feathering/intensity; 5 bottom/rain curtain top/snow intensity/fall phase;
/// 6 cloud color/cloud base brightness; 7 rain curtain color/extinction coefficient. All lengths are in
/// meters. Lightning does not occupy a region row: each strike has a single dominant point and a single
/// envelope, recorded globally in SceneLightParams.WeatherFlash.
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
    // Lightning scheduling is preserved per slot: when switching weather without rebuilding regions,
    // the counter and quiet period are not reset.
    readonly bool[] _flashActive = new bool[WeatherField.MaxRegions];
    readonly float[] _flashDelay = new float[WeatherField.MaxRegions];
    readonly float[] _flashAge = new float[WeatherField.MaxRegions];
    readonly uint[] _flashStrike = new uint[WeatherField.MaxRegions];
    readonly Vector3[] _flashPoint = new Vector3[WeatherField.MaxRegions];
    int _count;
    bool _enabled;
    float _far = 1300;

    /// <summary>
    /// This frame's dominant strike: xyz is the world position inside/outside the cloud, w is the
    /// return-stroke envelope brightness (0 means no lightning). Same data as the
    /// <see cref="SceneLightParams.WeatherFlash"/> written to the GPU, used by the application layer to
    /// drive transient light and debug displays.
    /// </summary>
    public Vector4 Flash { get; private set; }

    /// <summary>
    /// Strike counter: incremented for every new strike. Envelope brightness cannot distinguish a "new
    /// strike" from the "afterglow of the previous one", so the application layer (thunder, shake) uses
    /// this monotonic counter for edge detection instead of reading the brightness of <see cref="Flash"/>.
    /// Switching weather out of a thunderstorm does not rewind it; re-entering continues from the value.
    /// </summary>
    public int StrikeSerial { get; private set; }

    /// <summary>World position of the most recent strike, updated in sync with <see cref="StrikeSerial"/>; used to compute thunder delay and volume by distance.</summary>
    public Vector3 StrikePoint { get; private set; }

    /// <summary>
    /// Visible channel geometry of the most recent ground strike, rebuilt in sync with
    /// <see cref="StrikeSerial"/>.
    ///
    /// It lives on the visual state rather than on a render effect because there are two consumers and
    /// both must see the same bolt: the lightning effect uses it to generate ribbon instances, and the
    /// thunder uses it to compute nearest-point distance. An intra-cloud flash does not rebuild geometry,
    /// so <see cref="LightningChannel.Visible"/> is false and only the v1 intra-cloud glow remains.
    /// </summary>
    public LightningChannel Channel { get; } = new();

    /// <summary>Channel half-width in meters at the ground. The whole channel narrows upward to 45% of it.</summary>
    public float ChannelHalfWidth = 1.1f;

    public void Update(WeatherField field, float seconds, bool enabled, float farDistance)
    {
        ArgumentNullException.ThrowIfNull(field);
        float dt = float.IsFinite(seconds) ? Math.Clamp(seconds, 0, 0.1f) : 0;
        _enabled = enabled;
        _far = float.IsFinite(farDistance) ? Math.Clamp(farDistance, 1, 100000) : 1300;
        // Release deleted IDs first, then allocate; reordering and recipe intensity changes do not
        // affect motion state.
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
        // After a slot is released the delay is zeroed so reallocation is treated as "first entry into a
        // thunderstorm" and does not inherit the previous round's strike timing.
        _flashActive[slot] = false;
        _flashDelay[slot] = 0;
        _flashAge[slot] = 1e4f;
        _flashStrike[slot] = 0;
        _flashPoint[slot] = default;
    }

    /// <summary>
    /// Strike scheduling: when electrical activity &gt; 0, strikes are queued at exponential intervals,
    /// the lower the activity the longer the interval; when activity reaches zero they are dismissed
    /// immediately, but envelopes already triggered keep decaying naturally with age, so the last
    /// lightning is not hard-cut when weather switches out of a thunderstorm. The dt here is already
    /// clamped to 0.1s at the Update entry point, so a stuttering frame cannot skip an entire
    /// return-stroke sequence at once.
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
            // On entering a thunderstorm, hold a quiet period before the first strike: otherwise the
            // frame that switches the weather flashes along with the button.
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
        // The channel geometry is generated here in one shot. The code must match the expressions in
        // FlashPoint / FlashEnvelope verbatim: all three derive from the same seed, and changing any one
        // of them would make the "impact point, envelope, shape" come from three different bolts.
        // From the in-cloud breakdown point down to the ground, where the ground height is the virtual
        // ground from the sky parameters, so the channel rests on the ground instead of passing through it.
        Channel.Build((region.Settings.Seed * 0x9E3779B9u) ^ (_flashStrike[slot] * 0x85EBCA6Bu),
            _flashPoint[slot], region.Settings.CloudBase, SkyLighting.ReflectionGroundHeight, ChannelHalfWidth);
        // Set the point first, then signal: the coordinates a consumer (thunder) reads always belong to
        // the strike just counted.
        StrikePoint = _flashPoint[slot];
        StrikeSerial++;
    }

    /// <summary>Quiet seconds between strikes. At activity 1 it falls in 2.6-9.0 seconds; lower activity stretches the interval inversely.</summary>
    static float StrikeInterval(uint seed, uint strike, float activity)
        => (2.6f + 6.4f * Hash01(seed * 0x9E3779B9u + strike * 0x85EBCA6Bu))
            / Math.Clamp(activity, 0.2f, 1f);

    /// <summary>
    /// Return-stroke envelope: one strike contains four decaying return strokes whose spacing and
    /// amplitude are determined by the seed, and each stroke's shape is a 6ms rise over a 75ms
    /// exponential decay. Lightning as the eye perceives it is almost entirely governed by the envelope
    /// rather than any single-frame pulse, so brightness must be a continuous function of time; otherwise
    /// a random value per frame becomes strobing noise. The return value is clamped to 1.
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
    /// Position of the strike point within the cloud ellipse. The radius uses sqrt so it is uniform over
    /// the ellipse's area; otherwise it would cluster toward the center. The height ranges from the cloud
    /// base to the second layer, leaving a 0.35 margin to avoid the hard edge of the cloud slab's
    /// underside. The rotation convention matches the shader's WeatherFootprint:
    /// local = Rᵀ·(world-center); this does the inverse transform.
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

    /// <summary>Integer avalanche hash returning [0,1). Used only for scheduling and position, not part of
    /// the render integral, and needs no distribution properties. Shares the same formula as the channel
    /// geometry, see <see cref="LightningChannel.Hash01"/>.</summary>
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
            // Does not change the persistent cloud base; the rain curtain is allowed to fill upward to
            // the top of the near-field precipitation.
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
            // The rain curtain's scattering color is determined by cloud-base occlusion: under thick
            // clouds the curtain can only borrow the cloud base's brightness and must not shine directly
            // at ground irradiance, otherwise distant scenery becomes a gray wall brighter than the sky
            // and island silhouettes and the near/far boundary are flattened.
            lights.WeatherRegions[b + 7] = new(illumination * new Vector3(0.92f, 0.97f, 1.08f) * (0.30f - 0.14f * state.Cloud), 0.006f);
            // Take the brightest one. Two distinguishable independent flashes in the same frame are
            // physically possible, but at the scale of 8 regions the viewer only sees one cloud
            // direction, so storing a second gains no extra information.
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

    /// <summary>Finite elliptical-cylinder intersection matching the shader, used for boundary and depth-clip validation.</summary>
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
