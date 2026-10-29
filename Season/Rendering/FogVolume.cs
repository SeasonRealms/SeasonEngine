// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Numerics;

namespace Season.Rendering;

public enum FogVolumeShape { Box, Sphere }
public enum FogVolumeMode { Add, Exclude }

/// <summary>持久局部介质，尺寸为完整边长或各轴直径（米），旋转为单位四元数。</summary>
public sealed class FogVolume
{
    internal SceneFog? Owner;
    bool _enabled = true;
    Vector3 _position, _size = Vector3.One, _albedo = new(0.9f);
    Quaternion _rotation = Quaternion.Identity;
    float _extinction = 0.02f, _edgeFeather = 0.2f, _noiseAmount, _noiseScale = 0.1f;
    FogVolumeShape _shape;
    FogVolumeMode _mode;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            if (value) Owner?.EnsureActiveCapacity();
            _enabled = value;
            Owner?.ResetHistory();
        }
    }

    public FogVolumeShape Shape
    {
        get => _shape;
        set
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            _shape = value;
        }
    }

    public FogVolumeMode Mode
    {
        get => _mode;
        set
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            _mode = value;
        }
    }

    public Vector3 Position { get => _position; set => _position = FogMath.Finite(value); }
    public Vector3 Size
    {
        get => _size;
        set => _size = Vector3.Max(FogMath.Finite(value), new Vector3(0.001f));
    }
    public Quaternion Rotation
    {
        get => _rotation;
        set
        {
            FogMath.Finite(new Vector3(value.X, value.Y, value.Z));
            FogMath.Finite(value.W);
            float length = value.Length();
            if (!float.IsFinite(length) || length < 1e-6f)
                throw new ArgumentOutOfRangeException(nameof(value), "雾体旋转必须是有限的非零四元数。");
            _rotation = Quaternion.Normalize(value);
        }
    }
    public float Extinction { get => _extinction; set => _extinction = Math.Max(0f, FogMath.Finite(value)); }
    public Vector3 Albedo { get => _albedo; set => _albedo = Vector3.Clamp(FogMath.Finite(value), Vector3.Zero, Vector3.One); }

    /// <summary>由外边界向内部羽化的比例，0 为硬边，1 为全范围羽化。</summary>
    public float EdgeFeather { get => _edgeFeather; set => _edgeFeather = Math.Clamp(FogMath.Finite(value), 0f, 1f); }
    public float NoiseAmount { get => _noiseAmount; set => _noiseAmount = Math.Clamp(FogMath.Finite(value), 0f, 1f); }
    public float NoiseScale { get => _noiseScale; set => _noiseScale = Math.Max(0.0001f, FogMath.Finite(value)); }

    /// <summary>CPU 参考软遮罩，与注入着色器使用相同的归一化形状定义。</summary>
    public float WeightAt(Vector3 worldPosition)
    {
        var local = Vector3.Transform(worldPosition - Position, Quaternion.Conjugate(Rotation)) * 2f / Size;
        float radius = Shape == FogVolumeShape.Sphere ? local.Length()
            : Math.Max(Math.Abs(local.X), Math.Max(Math.Abs(local.Y), Math.Abs(local.Z)));
        if (radius >= 1f) return 0f;
        if (EdgeFeather <= 0f) return 1f;
        float t = Math.Clamp((1f - radius) / EdgeFeather, 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
