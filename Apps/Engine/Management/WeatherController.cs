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
/// Manages weather configuration and the spatial snapshot; the visual recipe is consumed by
/// WeatherPrecipitationEffect and does not touch the legacy sky or the forest fog. The UI queues requests,
/// and Update modifies the configuration on the frame thread and publishes the result; particles and the
/// later cloud layers share the same spatial field.
/// </summary>
internal sealed class WeatherController
{
    internal const string IslandId = "island", OffshoreId = "east-offshore";
    readonly ConcurrentQueue<(string Id, WeatherKind Kind)> _requests = new();
    readonly Dictionary<string, (WeatherKind Kind, WeatherTransition Transition)> _states = new();
    readonly WeatherRegion[] _snapshot = new WeatherRegion[WeatherField.MaxRegions];
    internal WeatherField Field { get; } = new();

    /// <summary>
    /// The indoor shelter region. It comes from scene geometry rather than user configuration, so it is neither
    /// written into WorldSettings nor part of any state transition; when the snapshot is rebuilt each frame it is
    /// appended after the configured regions, and the ordering invariant keeps it first to consume coverage weight.
    /// </summary>
    internal WeatherRegionSettings? Cover { get; set; }

    /// <summary>
    /// Whether any region in the current snapshot is charged. Thunder synthesis starts the first time this is true:
    /// without entering a thunderstorm the model is never loaded, and once entered the cache stays resident, so a
    /// later entry only reads the local WAV.
    /// </summary>
    internal bool LightningActive
    {
        get
        {
            foreach (var region in Field.Regions)
            {
                if (region.State.Lightning > 0) return true;
            }

            return false;
        }
    }

    // The rocks are at X=+75; regions are fixed in world coordinates, so turning or moving the camera does not change where the weather sits.
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
        // The shelter region does not take a user slot: when there is shelter, one less slot is left for the configured
        // regions, otherwise SetRegions would throw on exceeding the limit.
        int capacity = WeatherField.MaxRegions - (Cover != null ? 1 : 0);
        // The user's saved ranges are not overwritten; only corrupt entries are dropped. The array is copied so the save
        // thread is not modified in place while enumerating.
        var regions = new List<WeatherRegionSettings>();
        foreach (var region in saved ?? [])
        {
            if (region == null) continue;
            try { region.Validate(); }
            catch (ArgumentException) { continue; }
            if (!regions.Any(r => r.Id == region.Id)) regions.Add(region);
        }
        // The two regions always keep their slots, and other regions can still be added through configuration.
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

    /// <returns>Whether a settings change that needs saving was consumed.</returns>
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
        // The shelter region carries no strength; it only suppresses precipitation ranked after it, using the same coverage rule on both the GPU and CPU sides.
        if (Cover != null && count < WeatherField.MaxRegions)
            _snapshot[count++] = new WeatherRegion(Cover, new WeatherSample(0, 0, 0));
        Field.SetRegions(_snapshot.AsSpan(0, count));
        _configuration = settings.WeatherRegions;
        return changed;
    }

    internal string Status(string id)
    {
        if (!_states.TryGetValue(id, out var state)) return "Clear";
        return Text(state.Kind) + (state.Transition.Progress < 1 ? $" {state.Transition.Progress:P0}" : "");
    }

    // The strength is only a business signal; the rain/snow effect maps the current actual value to a visual recipe, so it cannot switch directly on the target enum.
    internal static WeatherSample Profile(WeatherKind kind) => kind switch
    {
        WeatherKind.Cloudy => new(0, 0, 0.35f),
        WeatherKind.Overcast => new(0, 0, 0.8f),
        WeatherKind.LightRain => new(0.2f, 0, 0.6f),
        WeatherKind.Rain => new(0.5f, 0, 0.8f),
        WeatherKind.HeavyRain => new(1, 0, 1),
        WeatherKind.LightSnow => new(0, 0.2f, 0.55f),
        WeatherKind.HeavySnow => new(0, 1, 0.9f),
        // Thunder: the storm cloud reaches the top and no rain or snow falls. Cloud thickness is taken as 1 directly,
        // because the cloud sheet count and extinction are both driven by it, and a flash is only readable against a
        // cloud layer that is thick enough and low enough.
        WeatherKind.Thunderstorm => new(0, 0, 1, 1),
        // Thunderstorm: the same full-thickness storm cloud, with heavy rain falling at the same time. Rain amount is
        // taken as 1 to match heavy rain; the flash, the thunder and the rain curtain all read the same transition
        // curve of the same region, so on a switch the rain never arrives before the lightning.
        WeatherKind.ThunderRain => new(1, 0, 1, 1),
        _ => default,
    };

    internal static string Text(WeatherKind kind) => kind switch
    {
        WeatherKind.Cloudy => "Cloudy", WeatherKind.Overcast => "Overcast",
        WeatherKind.LightRain => "Light Rain", WeatherKind.Rain => "Moderate Rain", WeatherKind.HeavyRain => "Heavy Rain",
        WeatherKind.LightSnow => "Light Snow", WeatherKind.HeavySnow => "Heavy Snow",
        WeatherKind.Thunderstorm => "Thunder", WeatherKind.ThunderRain => "Thunderstorm", _ => "Clear",
    };
}

/// <summary>
/// The second stage's near-field rain and snow. Each region keeps one pair of systems, its own wind and its own
/// seed; only this compute pass is registered, and the subsystems merely initialise the submission routes, which
/// are Recorded frame by frame from here to avoid duplicated time or dispatch. Clouds, the distant rain curtain
/// and collision are not implemented in this layer.
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
                // On initialisation failure the particles clean up their own resources; release the other half too, so no half-pair of submission routes is left behind.
                slot.Ready = slot.Rain.Initialize(g) && slot.Snow.Initialize(g);
                if (!slot.Ready) slot.Dispose();
            }
            // Removed/added regions reuse an already allocated slot, so a weather switch does not repeatedly compile shaders or reallocate GPU buffers.
            slot.Id = id;
            return slot;
        }
        throw new InvalidOperationException("The number of weather regions exceeds the particle slot limit.");
    }

    public override bool Initialize(IGraphics g)
    {
        if (_graphics != null) throw new InvalidOperationException("The rain/snow effect is already initialized.");
        if (!g.ComputeSupported || !g.IndirectDrawSupported) return false;
        _graphics = g;
        // The shelter region only suppresses precipitation and dispatches no particles itself; allocating two full systems for it would waste VRAM and initialisation time.
        foreach (var region in field.Regions)
            if (!region.Settings.Shelter && !Acquire(region.Settings.Id, g).Ready) { Dispose(); return false; }
        return true;
    }

    /// <summary>The actual frame time, without the day/night multiplier; Record consumes it once and it stays frozen until the next update.</summary>
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
            // Other regions still consume coverage weight; only the current region's strength is set to the unit basis.
            // The strength is handed to Density, to avoid multiplying alpha again and turning light rain into strength
            // squared; the clear-region mask is not lost either.
            FillMask(system.RainMask, region.Settings.Id, new(1, 0, 0));
            FillMask(system.SnowMask, region.Settings.Id, new(0, 1, 0));
            ApplyRecipe(system.Rain, region.Settings, region.State.Rain, false, camera, validCamera);
            ApplyRecipe(system.Snow, region.Settings, region.State.Snow, true, camera, validCamera);
        }
        foreach (var system in _systems)
        {
            if (system is not { Ready: true }) continue;
            system.Rain.Advance(delta); system.Snow.Advance(delta);
            // Disabled systems are still Recorded to clear the previous frame's ready flag and pending time, but they never upload, dispatch or draw.
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
        // The world's vertical drop covers the region; XZ only selects a periodic window near the camera, so climbing does not shift the whole particle field upwards.
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
        // The conservative sphere bound only skips regions entirely far from the near-field window; sampling the camera point alone would make the edge invisible when standing outside the rain region.
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
