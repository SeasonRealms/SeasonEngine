// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Numerics;

namespace Season.Rendering;

/// <summary>体积积分与 Froxel 坐标的 CPU 参考，实现用于数学测试和共享资源尺寸计算。</summary>
public static class FogMath
{
    public const float DepthOffset = 0.5f;
    public const int NoiseSize = 64;
    public const int MaxFroxels = 1_500_000;

    internal static float Finite(float value)
        => float.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), "雾参数必须为有限数值。");

    internal static Vector3 Finite(Vector3 value)
    {
        Finite(value.X); Finite(value.Y); Finite(value.Z);
        return value;
    }

    public static (int Width, int Height, int Slices) GridSize(int width, int height, FogQuality quality)
    {
        if (width <= 0 || height <= 0 || quality == FogQuality.Off) return (0, 0, 0);
        var (pixels, slices) = quality switch
        {
            FogQuality.Low => (24, 48),
            FogQuality.Medium => (16, 64),
            FogQuality.High => (12, 96),
            _ => throw new ArgumentOutOfRangeException(nameof(quality)),
        };
        double x = Math.Ceiling((double)width / pixels), y = Math.Ceiling((double)height / pixels);
        double scale = Math.Min(1d, Math.Min(256d / x, 256d / y));
        scale = Math.Min(scale, Math.Sqrt(MaxFroxels / (x * y * slices)));
        int gx = Math.Max(1, (int)Math.Floor(x * scale));
        int gy = Math.Max(1, (int)Math.Floor(y * scale));
        return (gx, gy, slices);
    }

    /// <summary>前向深度切片边界；0 为相机原点，N 为最大前向距离。</summary>
    public static float SliceDepth(float slice, int slices, float maxDistance)
    {
        if (slices <= 0 || maxDistance <= 0f) throw new ArgumentOutOfRangeException(nameof(slices));
        double t = Math.Clamp(Finite(slice) / slices, 0f, 1f);
        return (float)(DepthOffset * (Math.Pow(1d + Finite(maxDistance) / DepthOffset, t) - 1d));
    }

    public static float DepthSlice(float depth, int slices, float maxDistance)
    {
        if (slices <= 0 || maxDistance <= 0f) throw new ArgumentOutOfRangeException(nameof(slices));
        return (float)(slices * Math.Log(1d + Math.Clamp(Finite(depth), 0f, maxDistance) / DepthOffset)
            / Math.Log(1d + Finite(maxDistance) / DepthOffset));
    }

    /// <summary>沿真实射线路程积分一个恒定系数段。累计值 RGB=L、A=T；恒等值为 (0,0,0,1)。</summary>
    public static Vector4 Integrate(Vector4 accumulated, Vector3 source, float extinction, float distance)
    {
        extinction = Math.Max(0f, Finite(extinction));
        distance = Math.Max(0f, Finite(distance));
        float opticalDepth = extinction * distance;
        float transmittance = MathF.Exp(-opticalDepth);
        // 按光学厚度取极限，避免小消光、大距离时错误套用 Q*ds。
        float integral = opticalDepth < 0.001f
            ? distance * (1f - opticalDepth * 0.5f + opticalDepth * opticalDepth / 6f)
            : (1f - transmittance) / extinction;
        var radiance = new Vector3(accumulated.X, accumulated.Y, accumulated.Z)
            + accumulated.W * source * integral;
        return new Vector4(radiance, accumulated.W * transmittance);
    }

    /// <summary>查询一列累计体，切片内部以局部 Q/sigma_t 补积分；范围外保持末端值。</summary>
    public static Vector4 SampleColumn(ReadOnlySpan<Vector4> integrated, ReadOnlySpan<Vector4> local,
        float depth, float maxDistance, float rayLength)
    {
        if (local.Length == 0 || integrated.Length != local.Length + 1)
            throw new ArgumentException("累计体必须包含 N+1 个边界，局部体必须包含 N 个系数。");
        Finite(depth);
        if (Finite(rayLength) < 1 || Finite(maxDistance) <= 0) throw new ArgumentOutOfRangeException(nameof(rayLength));
        if (depth <= 0) return new Vector4(0, 0, 0, 1);
        if (depth >= maxDistance) return integrated[^1];
        int slice = Math.Min((int)DepthSlice(depth, local.Length, maxDistance), local.Length - 1);
        var coefficient = local[slice];
        return Integrate(integrated[slice], new Vector3(coefficient.X, coefficient.Y, coefficient.Z), coefficient.W,
            (depth - SliceDepth(slice, local.Length, maxDistance)) * rayLength);
    }

    /// <summary>cosine 为观察射线与指向光源方向的夹角余弦，正 g 朝光源观察时增强。</summary>
    public static float Phase(float cosine, float anisotropy)
    {
        float g = Math.Clamp(Finite(anisotropy), -0.9f, 0.9f);
        float denominator = 1f + g * g - 2f * g * Math.Clamp(Finite(cosine), -1f, 1f);
        return (1f - g * g) / (4f * MathF.PI * denominator * MathF.Sqrt(denominator));
    }

    /// <summary>预乘透明颜色的雾合成；加法发光只衰减，不额外贡献背景雾光。</summary>
    public static Vector4 CompositeTransparent(Vector4 premultiplied, Vector4 fog, bool additive = false)
    {
        var rgb = new Vector3(premultiplied.X, premultiplied.Y, premultiplied.Z) * fog.W;
        if (!additive) rgb += premultiplied.W * new Vector3(fog.X, fog.Y, fog.Z);
        return new Vector4(rgb, premultiplied.W);
    }

    /// <summary>深度使用本帧真实抖动投影的逆矩阵，UV 原点在左上角。</summary>
    public static Vector3 ReconstructWorld(Vector2 uv, float depth, Matrix4x4 inverseViewProjection)
    {
        var homogeneous = Vector4.Transform(new Vector4(uv.X * 2f - 1f, 1f - uv.Y * 2f, depth, 1f), inverseViewProjection);
        if (Math.Abs(homogeneous.W) < 1e-8f) throw new ArgumentException("深度反投影落在无穷远。");
        return new Vector3(homogeneous.X, homogeneous.Y, homogeneous.Z) / homogeneous.W;
    }

    /// <summary>返回每米前向深度的世界空间射线；长度通常大于 1，积分不能丢弃这个长度因子。</summary>
    public static Vector3 RayPerForwardDepth(Vector2 uv, Matrix4x4 inverseUnjitteredViewProjection,
        Vector3 cameraPosition, Vector3 cameraForward)
    {
        var ray = ReconstructWorld(uv, 0.5f, inverseUnjitteredViewProjection) - cameraPosition;
        float forward = Vector3.Dot(ray, Vector3.Normalize(cameraForward));
        if (forward <= 0f) throw new ArgumentException("Froxel 射线必须位于相机前方。");
        return ray / forward;
    }

    /// <summary>固定整数哈希；与 RGBA8 噪声纹理的量化值一致。三个坐标均周期为 64。</summary>
    public static float NoiseTexel(int x, int y, int z)
    {
        unchecked
        {
            uint h = (uint)(x & 63) * 73856093u ^ (uint)(y & 63) * 19349663u
                ^ (uint)(z & 63) * 83492791u ^ 0x9e3779b9u;
            h ^= h >> 16; h *= 0x7feb352du; h ^= h >> 15; h *= 0x846ca68bu; h ^= h >> 16;
            return (h & 255u) / 255f;
        }
    }

    /// <summary>手动周期三线性采样，八个 texel 均执行取模，避免 clamp 纹理的接缝。</summary>
    public static float PeriodicNoise(Vector3 uvw)
    {
        uvw = Finite(uvw);
        var p = new Vector3(uvw.X - MathF.Floor(uvw.X), uvw.Y - MathF.Floor(uvw.Y), uvw.Z - MathF.Floor(uvw.Z))
            * NoiseSize - new Vector3(0.5f);
        int x = (int)MathF.Floor(p.X), y = (int)MathF.Floor(p.Y), z = (int)MathF.Floor(p.Z);
        float fx = p.X - x, fy = p.Y - y, fz = p.Z - z;
        float value = 0f;
        for (int dz = 0; dz < 2; dz++)
            for (int dy = 0; dy < 2; dy++)
                for (int dx = 0; dx < 2; dx++)
                    value += NoiseTexel(x + dx, y + dy, z + dz)
                        * (dx == 0 ? 1f - fx : fx) * (dy == 0 ? 1f - fy : fy) * (dz == 0 ? 1f - fz : fz);
        return value;
    }

    internal static float DensityNoise(Vector3 position, float scale, float amount)
        => amount <= 0f ? 1f : 1f + amount * (2f * PeriodicNoise(position * scale) - 1f);
}
