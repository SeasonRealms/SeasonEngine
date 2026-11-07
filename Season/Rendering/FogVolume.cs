// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Numerics;

namespace Season.Rendering;

public enum FogVolumeShape { Box, Sphere }
public enum FogVolumeMode { Add, Exclude }

/// <summary>A persistent local medium; the size is the full edge length or per-axis diameter (meters), and the rotation is a unit quaternion.</summary>
public sealed class FogVolume
{
    internal SceneFog? Owner;
    bool _enabled = true;
    Vector3 _position, _size = Vector3.One, _albedo = new(0.9f), _selfIllumination;
    Quaternion _rotation = Quaternion.Identity;
    float _extinction = 0.02f, _edgeFeather = 0.2f, _noiseAmount, _noiseScale = 0.1f;
    float? _anisotropy;
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
                throw new ArgumentOutOfRangeException(nameof(value), "Fog volume rotation must be a finite, non-zero quaternion.");
            _rotation = Quaternion.Normalize(value);
        }
    }
    public float Extinction { get => _extinction; set => _extinction = Math.Max(0f, FogMath.Finite(value)); }
    public Vector3 Albedo { get => _albedo; set => _albedo = Vector3.Clamp(FogMath.Finite(value), Vector3.Zero, Vector3.One); }

    /// <summary>
    /// Radiance the medium emits on its own (HDR, per meter of optical depth), i.e. a floor under the lighting term.
    ///
    /// Why it is needed: <see cref="Albedo"/> alone can never exceed the light that reaches the volume, so a patch that sits
    /// in shadow - under a canopy at dawn, behind a hill - attenuates the background while contributing almost nothing and
    /// reads as black-grey smoke instead of mist. Authoring a floor here is the fog-volume counterpart of the self-lit colour
    /// the particle systems use for spray: the patch keeps reading as white mist in dim light and still brightens with the sun
    /// on top of it. Default zero, which leaves every existing fog numerically unchanged.
    /// </summary>
    public Vector3 SelfIllumination
    {
        get => _selfIllumination;
        set => _selfIllumination = Vector3.Clamp(FogMath.Finite(value), Vector3.Zero, new Vector3(8f));
    }

    /// <summary>Falloff ratio from the outer boundary inward; 0 is a hard edge, 1 is feathering over the full range.</summary>
    public float EdgeFeather { get => _edgeFeather; set => _edgeFeather = Math.Clamp(FogMath.Finite(value), 0f, 1f); }
    public float NoiseAmount { get => _noiseAmount; set => _noiseAmount = Math.Clamp(FogMath.Finite(value), 0f, 1f); }
    public float NoiseScale { get => _noiseScale; set => _noiseScale = Math.Max(0.0001f, FogMath.Finite(value)); }

    /// <summary>
    /// Per-volume scattering anisotropy; null follows <see cref="SceneFog.Anisotropy"/>.
    ///
    /// The global medium is one medium with one directionality, but a mist patch lying in a valley and the spray plume
    /// at the foot of a waterfall have no reason to scatter alike: the first is fine droplets, the second is coarse spray
    /// that sends a visible share of the light back toward the viewer. Authoring it here lets the same phase recipe read
    /// differently across one scene instead of putting the same halo on every patch.
    /// </summary>
    public float? Anisotropy
    {
        get => _anisotropy;
        set => _anisotropy = value is float g ? Math.Clamp(FogMath.Finite(g), -0.9f, 0.9f) : null;
    }

    /// <summary>
    /// The anisotropy the injection kernel should use for this volume: its own when authored, the scene's otherwise.
    /// Kept here rather than at the packing site so the CPU reference and the frame builder cannot drift apart.
    /// </summary>
    internal float EffectiveAnisotropy(float fallback) => _anisotropy ?? fallback;

    /// <summary>CPU reference soft mask, using the same normalized shape definition as the injection shader.</summary>
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

    /// <summary>
    /// Ray intersection in the same normalized local space <see cref="WeightAt"/> uses, so the injection shader and this
    /// reference stay in step. A froxel near the far end of the depth range is several metres thick, which can be taller
    /// than a thin patch; a single centre sample then makes the patch alternate between hit and missed as the camera
    /// moves. This reports how much of the slice the volume fills and where its middle is, so the mask and the noise are
    /// read where the coverage actually is.
    /// </summary>
    /// <param name="origin">Ray origin, normally the camera.</param>
    /// <param name="ray">Ray per meter of forward depth, the same parameter the froxels are laid out in.</param>
    /// <param name="start">Forward depth where the slice starts.</param>
    /// <param name="end">Forward depth where the slice ends.</param>
    /// <param name="coverage">Fraction of the slice inside the volume, in (0,1].</param>
    /// <param name="position">World position at the middle of the intersected span.</param>
    internal bool SliceCoverage(Vector3 origin, Vector3 ray, float start, float end,
        out float coverage, out Vector3 position)
    {
        coverage = 0f; position = default;
        var inverse = Quaternion.Conjugate(Rotation);
        var scale = new Vector3(2f) / Size;
        var localOrigin = Vector3.Transform(origin - Position, inverse) * scale;
        var localRay = Vector3.Transform(ray, inverse) * scale;
        float t0, t1;
        if (Shape == FogVolumeShape.Sphere)
        {
            float a = Vector3.Dot(localRay, localRay), b = Vector3.Dot(localOrigin, localRay);
            float discriminant = b * b - a * (Vector3.Dot(localOrigin, localOrigin) - 1f);
            if (a <= 1e-12f || discriminant <= 0f) return false;
            float root = MathF.Sqrt(discriminant);
            t0 = (-b - root) / a; t1 = (-b + root) / a;
        }
        else
        {
            t0 = float.NegativeInfinity; t1 = float.PositiveInfinity;
            Slab(localOrigin.X, localRay.X, ref t0, ref t1);
            Slab(localOrigin.Y, localRay.Y, ref t0, ref t1);
            Slab(localOrigin.Z, localRay.Z, ref t0, ref t1);
        }
        float near = MathF.Max(t0, start), far = MathF.Min(t1, end);
        if (!(far > near)) return false;
        coverage = (far - near) / (end - start);
        position = origin + ray * (0.5f * (near + far));
        return true;

        // A ray parallel to a face neither enters nor leaves through it: the pair of reciprocals is then an infinite
        // interval, unless the origin already sits outside that slab, in which case the volume cannot be hit at all.
        static void Slab(float slabOrigin, float direction, ref float near, ref float far)
        {
            if (direction == 0f)
            {
                if (MathF.Abs(slabOrigin) > 1f) { near = 1f; far = 0f; }
                return;
            }
            float a = (-1f - slabOrigin) / direction, b = (1f - slabOrigin) / direction;
            near = MathF.Max(near, MathF.Min(a, b));
            far = MathF.Min(far, MathF.Max(a, b));
        }
    }
}
