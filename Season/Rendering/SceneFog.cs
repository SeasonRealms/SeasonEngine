// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Collections.ObjectModel;
using System.Numerics;

namespace Season.Rendering;

/// <summary>The scene's own near-ground medium. Units are meters, seconds, 1/m; colors are linear RGB. Disabled by default in the class library.</summary>
public sealed class SceneFog
{
    public const int MaxActiveVolumes = 32;
    readonly List<FogVolume> _volumes = new();
    readonly ReadOnlyCollection<FogVolume> _view;
    bool _enabled;
    float _extinction = 0.005f, _baseHeight, _heightFalloff = 0.25f, _anisotropy = 0.6f;
    float _maxDistance = 160f, _noiseAmount, _noiseScale = 0.1f;
    Vector3 _albedo = new(0.9f), _windVelocity;
    double _windX, _windY, _windZ;

    public SceneFog() => _view = _volumes.AsReadOnly();

    public bool Enabled
    {
        get => _enabled;
        set { if (_enabled != value) { _enabled = value; ResetHistory(); } }
    }
    public float Extinction { get => _extinction; set => _extinction = Math.Max(0f, FogMath.Finite(value)); }
    public Vector3 Albedo { get => _albedo; set => _albedo = Vector3.Clamp(FogMath.Finite(value), Vector3.Zero, Vector3.One); }
    public float BaseHeight { get => _baseHeight; set => _baseHeight = FogMath.Finite(value); }
    public float HeightFalloff { get => _heightFalloff; set => _heightFalloff = Math.Max(0f, FogMath.Finite(value)); }
    public float Anisotropy { get => _anisotropy; set => _anisotropy = Math.Clamp(FogMath.Finite(value), -0.9f, 0.9f); }
    public float MaxDistance
    {
        get => _maxDistance;
        set
        {
            // The upper bound limits the numeric range of the floating-point projection; it is not used to control the quality budget.
            float distance = Math.Clamp(FogMath.Finite(value), 0.01f, 1_000_000f);
            if (distance != _maxDistance) { _maxDistance = distance; ResetHistory(); }
        }
    }
    public float NoiseAmount { get => _noiseAmount; set => _noiseAmount = Math.Clamp(FogMath.Finite(value), 0f, 1f); }
    public float NoiseScale { get => _noiseScale; set => _noiseScale = Math.Max(0.0001f, FogMath.Finite(value)); }
    public Vector3 WindVelocity { get => _windVelocity; set => _windVelocity = FogMath.Finite(value); }
    public Vector3 WindOffset => new((float)_windX, (float)_windY, (float)_windZ);
    public IReadOnlyList<FogVolume> Volumes => _view;
    public uint HistoryRevision { get; private set; }

    public int ActiveVolumeCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < _volumes.Count; i++) if (_volumes[i].Enabled) count++;
            return count;
        }
    }

    internal void EnsureActiveCapacity()
    {
        if (ActiveVolumeCount >= MaxActiveVolumes)
            throw new InvalidOperationException($"A scene supports at most {MaxActiveVolumes} active local fog volumes.");
    }

    public void Add(FogVolume volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        if (ReferenceEquals(volume.Owner, this)) return;
        if (volume.Owner != null)
            throw new InvalidOperationException("The same local fog volume cannot be registered with multiple scenes at once.");
        if (volume.Enabled) EnsureActiveCapacity();
        _volumes.Add(volume);
        volume.Owner = this;
        ResetHistory();
    }

    public bool Remove(FogVolume volume)
    {
        if (!_volumes.Remove(volume)) return false;
        volume.Owner = null;
        ResetHistory();
        return true;
    }

    public void Clear()
    {
        for (int i = 0; i < _volumes.Count; i++) _volumes[i].Owner = null;
        _volumes.Clear();
        ResetHistory();
    }

    /// <summary>Called once per simulated clock step; pass 0 when paused, changing speed only affects subsequent increments, and a jump should call ResetHistory separately.</summary>
    public void Advance(double deltaSeconds)
    {
        if (!double.IsFinite(deltaSeconds) || deltaSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
        double x = _windX + deltaSeconds * WindVelocity.X;
        double y = _windY + deltaSeconds * WindVelocity.Y;
        double z = _windZ + deltaSeconds * WindVelocity.Z;
        if (Math.Abs(x) > float.MaxValue || Math.Abs(y) > float.MaxValue || Math.Abs(z) > float.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds), "Fog wind offset is out of representable range.");
        _windX = x; _windY = y; _windZ = z;
    }

    public void ResetHistory() => HistoryRevision = unchecked(HistoryRevision + 1);

    /// <summary>CPU reference medium sample. Extinction and RGB scattering coefficients are accumulated first, then the exclusion regions are applied.</summary>
    public (float Extinction, Vector3 Scattering) SampleMedium(Vector3 position)
    {
        FogMath.Finite(position);
        if (!Enabled) return (0f, Vector3.Zero);
        var (density, scattering) = GlobalMedium(position);
        float exclusion = 1f;
        for (int i = 0; i < _volumes.Count; i++)
        {
            var volume = _volumes[i];
            if (!volume.Enabled) continue;
            Accumulate(volume, volume.WeightAt(position), position, ref density, ref scattering, ref exclusion);
        }
        return (density * exclusion, scattering * exclusion);
    }

    /// <summary>
    /// CPU reference for one injected froxel slice, matching the injection shader. The global medium varies smoothly, so
    /// it is sampled at the slice centre; a local volume has a hard boundary, and a slice near the far end of the depth
    /// range is several metres thick - more than a thin patch is tall - so one centre sample there makes the patch
    /// alternate between hit and missed as the camera moves. Each volume is therefore intersected with the ray and its
    /// weight scaled by the fraction of the slice it fills, read at the middle of that span.
    /// </summary>
    /// <param name="origin">Camera position.</param>
    /// <param name="ray">World-space ray per meter of forward depth, the same ray the froxels are built from.</param>
    /// <param name="start">Forward depth where the slice starts.</param>
    /// <param name="end">Forward depth where the slice ends.</param>
    /// <param name="center">Forward depth of the slice centre, where the global medium is sampled.</param>
    public (float Extinction, Vector3 Scattering) SampleSlice(Vector3 origin, Vector3 ray, float start, float end, float center)
    {
        FogMath.Finite(origin); FogMath.Finite(ray); FogMath.Finite(start);
        FogMath.Finite(end); FogMath.Finite(center);
        if (!(end > start)) throw new ArgumentOutOfRangeException(nameof(end), "A fog slice needs a positive depth range.");
        if (!Enabled) return (0f, Vector3.Zero);
        var (density, scattering) = GlobalMedium(origin + ray * center);
        float exclusion = 1f;
        for (int i = 0; i < _volumes.Count; i++)
        {
            var volume = _volumes[i];
            if (!volume.Enabled) continue;
            if (!volume.SliceCoverage(origin, ray, start, end, out float coverage, out var position)) continue;
            Accumulate(volume, volume.WeightAt(position) * coverage, position, ref density, ref scattering, ref exclusion);
        }
        return (density * exclusion, scattering * exclusion);
    }

    /// <summary>
    /// Density-weighted mean phase at a position: the CPU counterpart of what the injection kernel bakes into a froxel.
    ///
    /// Every medium has its own anisotropy now, so a froxel that straddles a valley mist and a spray plume carries a mix
    /// rather than one number. Weighting by each medium's local extinction makes the result reduce to
    /// <see cref="FogMath.Phase"/> at that volume's own g wherever a single volume covers the point - the property the
    /// tests assert - and it is why this cannot be read back from <see cref="SampleMedium"/>, which returns only the
    /// summed medium and has already forgotten which part came from where.
    /// </summary>
    public float PhaseAt(Vector3 position, float cosine)
    {
        FogMath.Finite(position);
        float global = FogMath.Phase(cosine, Anisotropy);
        if (!Enabled) return global;
        var (density, _) = GlobalMedium(position);
        double weight = density, accumulated = density * global;
        for (int i = 0; i < _volumes.Count; i++)
        {
            var volume = _volumes[i];
            if (!volume.Enabled) continue;
            float mask = volume.WeightAt(position);
            // An exclusion volume scales the whole medium by the same factor, so it cannot move the mean.
            if (volume.Mode == FogVolumeMode.Exclude) continue;
            float local = volume.Extinction * mask
                * FogMath.DensityNoise(position - WindOffset, volume.NoiseScale, volume.NoiseAmount);
            weight += local;
            accumulated += local * FogMath.Phase(cosine, volume.EffectiveAnisotropy(Anisotropy));
        }
        return weight > 1e-12 ? (float)(accumulated / weight) : global;
    }

    (float Density, Vector3 Scattering) GlobalMedium(Vector3 position)
    {
        float density = Extinction * MathF.Exp(-HeightFalloff * Math.Max(position.Y - BaseHeight, 0f));
        density *= FogMath.DensityNoise(position - WindOffset, NoiseScale, NoiseAmount);
        return (density, Albedo * density);
    }

    void Accumulate(FogVolume volume, float weight, Vector3 position,
        ref float density, ref Vector3 scattering, ref float exclusion)
    {
        if (volume.Mode == FogVolumeMode.Exclude)
        {
            exclusion *= 1f - weight;
            return;
        }
        float local = volume.Extinction * weight
            * FogMath.DensityNoise(position - WindOffset, volume.NoiseScale, volume.NoiseAmount);
        density += local;
        scattering += volume.Albedo * local;
    }
}
