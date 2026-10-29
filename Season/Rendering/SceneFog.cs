// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Collections.ObjectModel;
using System.Numerics;

namespace Season.Rendering;

/// <summary>场景自己的近地介质。单位为米、秒、1/m；颜色为线性 RGB。类库默认关闭。</summary>
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
            // 上界限制浮点投影的数值范围，不用于控制画质预算。
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
            throw new InvalidOperationException($"每个场景最多支持 {MaxActiveVolumes} 个活动局部雾体。");
    }

    public void Add(FogVolume volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        if (ReferenceEquals(volume.Owner, this)) return;
        if (volume.Owner != null)
            throw new InvalidOperationException("同一局部雾体不能同时注册到多个场景。");
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

    /// <summary>模拟时钟调用一次；暂停传 0，调速只改变后续增量，跳时另调用 ResetHistory。</summary>
    public void Advance(double deltaSeconds)
    {
        if (!double.IsFinite(deltaSeconds) || deltaSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
        double x = _windX + deltaSeconds * WindVelocity.X;
        double y = _windY + deltaSeconds * WindVelocity.Y;
        double z = _windZ + deltaSeconds * WindVelocity.Z;
        if (Math.Abs(x) > float.MaxValue || Math.Abs(y) > float.MaxValue || Math.Abs(z) > float.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds), "雾的风偏移超出可表示范围。");
        _windX = x; _windY = y; _windZ = z;
    }

    public void ResetHistory() => HistoryRevision = unchecked(HistoryRevision + 1);

    /// <summary>CPU 参考介质采样。先累加消光与 RGB 散射系数，最后应用排除区。</summary>
    public (float Extinction, Vector3 Scattering) SampleMedium(Vector3 position)
    {
        FogMath.Finite(position);
        if (!Enabled) return (0f, Vector3.Zero);
        float density = Extinction * MathF.Exp(-HeightFalloff * Math.Max(position.Y - BaseHeight, 0f));
        density *= FogMath.DensityNoise(position - WindOffset, NoiseScale, NoiseAmount);
        var scattering = Albedo * density;
        float exclusion = 1f;
        for (int i = 0; i < _volumes.Count; i++)
        {
            var volume = _volumes[i];
            if (!volume.Enabled) continue;
            float weight = volume.WeightAt(position);
            if (volume.Mode == FogVolumeMode.Exclude)
            {
                exclusion *= 1f - weight;
                continue;
            }
            float local = volume.Extinction * weight
                * FogMath.DensityNoise(position - WindOffset, volume.NoiseScale, volume.NoiseAmount);
            density += local;
            scattering += volume.Albedo * local;
        }
        return (density * exclusion, scattering * exclusion);
    }
}
