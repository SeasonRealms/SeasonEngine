// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Numerics;

namespace Season.Rendering;

/// <summary>CPU reference for volumetric integration and Froxel coordinates, used by math tests and shared resource size computation.</summary>
public static class FogMath
{
    public const float DepthOffset = 0.5f;
    public const int NoiseSize = 64;
    public const int MaxFroxels = 1_500_000;

    internal static float Finite(float value)
        => float.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), "Fog parameters must be finite values.");

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

    /// <summary>Forward depth slice boundary; 0 is the camera origin, N is the maximum forward distance.</summary>
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

    /// <summary>Integrate one constant-coefficient segment along the true ray path. Accumulated value RGB=L, A=T; identity is (0,0,0,1).</summary>
    public static Vector4 Integrate(Vector4 accumulated, Vector3 source, float extinction, float distance)
    {
        extinction = Math.Max(0f, Finite(extinction));
        distance = Math.Max(0f, Finite(distance));
        float opticalDepth = extinction * distance;
        float transmittance = MathF.Exp(-opticalDepth);
        // Take the limit by optical depth to avoid wrongly applying Q*ds for small extinction and large distance.
        float integral = opticalDepth < 0.001f
            ? distance * (1f - opticalDepth * 0.5f + opticalDepth * opticalDepth / 6f)
            : (1f - transmittance) / extinction;
        var radiance = new Vector3(accumulated.X, accumulated.Y, accumulated.Z)
            + accumulated.W * source * integral;
        return new Vector4(radiance, accumulated.W * transmittance);
    }

    /// <summary>Sample one accumulated column, completing integration inside the slice with the local Q/sigma_t; outside the range the end value is kept.</summary>
    public static Vector4 SampleColumn(ReadOnlySpan<Vector4> integrated, ReadOnlySpan<Vector4> local,
        float depth, float maxDistance, float rayLength)
    {
        if (local.Length == 0 || integrated.Length != local.Length + 1)
            throw new ArgumentException("The accumulated volume must contain N+1 boundaries and the local volume must contain N coefficients.");
        Finite(depth);
        if (Finite(rayLength) < 1 || Finite(maxDistance) <= 0) throw new ArgumentOutOfRangeException(nameof(rayLength));
        if (depth <= 0) return new Vector4(0, 0, 0, 1);
        if (depth >= maxDistance) return integrated[^1];
        int slice = Math.Min((int)DepthSlice(depth, local.Length, maxDistance), local.Length - 1);
        var coefficient = local[slice];
        return Integrate(integrated[slice], new Vector3(coefficient.X, coefficient.Y, coefficient.Z), coefficient.W,
            (depth - SliceDepth(slice, local.Length, maxDistance)) * rayLength);
    }

    /// <summary>
    /// Weight of the forward lobe of <see cref="Phase"/>; the remainder goes to a weaker back-scattering lobe.
    ///
    /// A single Henyey-Greenstein lobe at the scene default of 0.6 has a peak-to-back ratio of about 64:1, which is what
    /// makes mist read as a flat sheet with the sun behind the viewer and as a blown-out disc with it in front. Splitting
    /// the lobe keeps the forward peak, sends part of the energy back the other way, and is what every backend evaluates
    /// per medium. The two weights sum to one, so the phase stays normalized over the sphere.
    /// </summary>
    public const float PhaseForwardWeight = 0.75f;

    /// <summary>Anisotropy of the back-scattering lobe as a fraction of the medium's own g, with the sign flipped.</summary>
    public const float PhaseBackwardRatio = 0.5f;

    /// <summary>
    /// Two-lobe phase. <paramref name="cosine"/> is the cosine of the angle between the view ray and the direction toward
    /// the light; a positive <paramref name="anisotropy"/> strengthens when viewing toward the light.
    ///
    /// g = 0 collapses to 1/(4 pi) for every cosine, exactly as the single lobe did, so a scene that never sets an
    /// anisotropy is numerically unchanged.
    /// </summary>
    public static float Phase(float cosine, float anisotropy)
    {
        float g = Math.Clamp(Finite(anisotropy), -0.9f, 0.9f);
        float c = Math.Clamp(Finite(cosine), -1f, 1f);
        return PhaseForwardWeight * HenyeyGreenstein(c, g)
            + (1f - PhaseForwardWeight) * HenyeyGreenstein(c, -PhaseBackwardRatio * g);
    }

    static float HenyeyGreenstein(float cosine, float g)
    {
        float denominator = 1f + g * g - 2f * g * cosine;
        return (1f - g * g) / (4f * MathF.PI * denominator * MathF.Sqrt(denominator));
    }

    /// <summary>Fog composite of a premultiplied transparent color; additive emission only attenuates and contributes no extra background fog light.</summary>
    public static Vector4 CompositeTransparent(Vector4 premultiplied, Vector4 fog, bool additive = false)
    {
        var rgb = new Vector3(premultiplied.X, premultiplied.Y, premultiplied.Z) * fog.W;
        if (!additive) rgb += premultiplied.W * new Vector3(fog.X, fog.Y, fog.Z);
        return new Vector4(rgb, premultiplied.W);
    }

    /// <summary>Depth uses the inverse of this frame's real jittered projection; the UV origin is the top-left corner.</summary>
    public static Vector3 ReconstructWorld(Vector2 uv, float depth, Matrix4x4 inverseViewProjection)
    {
        var homogeneous = Vector4.Transform(new Vector4(uv.X * 2f - 1f, 1f - uv.Y * 2f, depth, 1f), inverseViewProjection);
        if (Math.Abs(homogeneous.W) < 1e-8f) throw new ArgumentException("Depth unprojection lands at infinity.");
        return new Vector3(homogeneous.X, homogeneous.Y, homogeneous.Z) / homogeneous.W;
    }

    /// <summary>Return the world-space ray per meter of forward depth; the length is usually greater than 1, and integration must not drop this length factor.</summary>
    public static Vector3 RayPerForwardDepth(Vector2 uv, Matrix4x4 inverseUnjitteredViewProjection,
        Vector3 cameraPosition, Vector3 cameraForward)
    {
        var ray = ReconstructWorld(uv, 0.5f, inverseUnjitteredViewProjection) - cameraPosition;
        float forward = Vector3.Dot(ray, Vector3.Normalize(cameraForward));
        if (forward <= 0f) throw new ArgumentException("Froxel rays must lie in front of the camera.");
        return ray / forward;
    }

    /// <summary>Fixed integer hash; matches the quantized values of the RGBA8 noise texture. All three coordinates have period 64.</summary>
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

    /// <summary>Manual periodic trilinear sampling; all eight texels apply the modulo to avoid seams on a clamp texture.</summary>
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
