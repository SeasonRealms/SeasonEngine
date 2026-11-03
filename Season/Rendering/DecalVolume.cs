// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Numerics;

namespace Season.Rendering;

/// <summary>
/// Blending semantics for D1 screen-space decals.
///
/// Under forward rendering, SceneColor already holds the result of "albedo × lighting + specular", so the correctness each of these can offer differs;
/// use this table to decide the semantics rather than judging by "what looks like UI":
/// - <see cref="Modulate"/> treats the decal as a modulation factor on albedo. Diffuse and SH indirect light are linear in albedo, so this step is physically valid;
///   specular is darkened along with it, which is an accepted approximation (wet streaks, stains, cracks, and darkened moss all rely on it).
/// - <see cref="Additive"/> is completely unaffected by lighting and is only correct when "the thing itself is emissive" (runes, magic circles, caustics on a pool bottom).
///
/// "Writing back to albedo and re-evaluating with scene lighting" is deliberately excluded from v1: it requires a complete evaluation copy of direct light, CSM shadows, and SH ambient light,
/// which would be a new shader per each of the four backends, and the benefit only holds for graffiti-type decals. If it is ever done it is a separate step after D1, not a third mode here.
/// </summary>
public enum DecalMode
{
    Modulate,
    Additive,
}

/// <summary>
/// D1 decal volume: an oriented rectangular projection box, with size given as the full edge length per axis (in meters) and rotation as a unit quaternion.
///
/// Isomorphic to <see cref="FogVolume"/>: the property accessors handle finiteness and range validation, and <see cref="SceneDecals.Add"/> handles the count cap.
/// The decal makes no decisions beyond geometry tests inside the box and writes no velocity target -- it projects onto geometry already drawn this frame,
/// and that geometry carries its own correct motion vectors, so TAA needs no extra history information from the decal.
/// </summary>
public sealed class DecalVolume
{
    internal SceneDecals? Owner;
    bool _enabled = true;
    Vector3 _position, _size = Vector3.One, _tint = Vector3.One;
    Quaternion _rotation = Quaternion.Identity;
    float _opacity = 1f, _angleFade = 0.35f, _edgeFade = 0.15f, _intensity = 1f, _roughness = 0.2f;
    DecalMode _mode;
    int _sortOrder;
    string? _texture;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            if (value) Owner?.EnsureActiveCapacity();
            _enabled = value;
        }
    }

    public DecalMode Mode
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
                throw new ArgumentOutOfRangeException(nameof(value), "Decal rotation must be a finite, non-zero quaternion.");
            _rotation = Quaternion.Normalize(value);
        }
    }

    /// <summary>Linear RGB multiplier color; under Modulate it is the "target color to modulate to", and under Additive it is the emissive color.</summary>
    public Vector3 Tint { get => _tint; set => _tint = Vector3.Clamp(FogMath.Finite(value), Vector3.Zero, Vector3.One); }

    /// <summary>Overall opacity, multiplied with the texture alpha.</summary>
    public float Opacity { get => _opacity; set => _opacity = Math.Clamp(FogMath.Finite(value), 0f, 1f); }

    /// <summary>Grazing-angle fade threshold (cosine of the angle between the normal and the box axis). 0 gives a hard-edged projection, used to eliminate decal streaks stretched along side walls.</summary>
    public float AngleFade { get => _angleFade; set => _angleFade = Math.Clamp(FogMath.Finite(value), 0f, 1f); }

    /// <summary>Inward feather ratio from the box's inner boundary: 0 is a hard cut, 1 feathers the entire box.</summary>
    public float EdgeFade { get => _edgeFade; set => _edgeFade = Math.Clamp(FogMath.Finite(value), 0f, 1f); }

    /// <summary>Intensity multiplier for Additive; ignored by Modulate.</summary>
    public float Intensity { get => _intensity; set => _intensity = Math.Max(0f, FogMath.Finite(value)); }

    /// <summary>Slot 9: the target roughness written into SceneNormal.z. Only takes effect when <see cref="AffectRoughness"/> is set and a normal target exists this frame.</summary>
    public float Roughness { get => _roughness; set => _roughness = Math.Clamp(FogMath.Finite(value), 0f, 1f); }

    /// <summary>Whether to overwrite roughness for this region (wet streaks, puddles). Requires SceneNormal; when off the decal only changes color.</summary>
    public bool AffectRoughness { get; set; }

    /// <summary>Draw order when multiple decals overlap on screen; smaller values draw first. Equal values follow registration order.</summary>
    public int SortOrder { get => _sortOrder; set => _sortOrder = value; }

    /// <summary>
    /// Texture name (the registration name in the backend's texture dictionary). null means a procedural decal, used for resource-independent smoke validation.
    ///
    /// A decal does not load files on its own: when the name is unregistered or the texture has not finished uploading, that decal is skipped entirely by the backend (rather than drawn as a placeholder color).
    /// Pre-registration goes through the existing named-texture path (for example, handing the same name to an invisible Sprite2D to load),
    /// so no new asynchronous load chain needs to be opened for decals.
    /// </summary>
    public string? Texture
    {
        get => _texture;
        set => _texture = value is { Length: > 0 } ? value : null;
    }

    /// <summary>Local-to-world matrix (including scale); recomputed from the current Position/Rotation/Size each time it is packed, not cached.</summary>
    internal Matrix4x4 WorldMatrix =>
        Matrix4x4.CreateScale(Size) * Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Position);

    /// <summary>
    /// Normalized world-to-local transform; the decal shader uses it to fold world points into [-1,1]³ to fetch UVs.
    ///
    /// It is the inverse of <see cref="WorldMatrix"/> multiplied by a uniform factor of 2 (the inverse folds the offset into [-0.5,0.5]³, and multiplying by 2 gives
    /// [-1,1]³), so the multiplication order must be reversed: first subtract the center, then undo the rotation, and finally scale the "edge length" down to a "half-width of 1".
    /// System.Numerics uses the row-vector convention (<c>p * A * B</c> means A is applied first),
    /// so writing it as <c>Scale * Rot^-1 * Translate(-P)</c> would apply "scale first, then translate" to world coordinates,
    /// making the mapped local coordinate independent of world position (equal to <c>p * 2/Size - P</c>), and the inside-box test would then be shifted as a whole:
    /// a decal that should lie on a pool would end up on the grass near <c>P * Size / 2</c>, and the Additive one would be a
    /// glowing cube unrelated to the pool. The shader's <c>all(abs(local)&lt;=1)</c> and <see cref="SceneDecals.Contains"/> share
    /// this matrix; getting it wrong produces no error, only silent misalignment.
    /// </summary>
    internal Matrix4x4 WorldToLocalMatrix =>
        Matrix4x4.CreateTranslation(-Position)
        * Matrix4x4.CreateFromQuaternion(Quaternion.Conjugate(Rotation))
        * Matrix4x4.CreateScale(new Vector3(2f) / Size);

    /// <summary>Conservative world AABB (the axis-aligned bounds of the rotated box), used for frustum culling; it accepts a few more false positives than an exact OBB test, which is harmless for culling.</summary>
    internal void GetBounds(out Vector3 center, out Vector3 extents)
    {
        var half = Size * 0.5f;
        var rotation = Matrix4x4.CreateFromQuaternion(Rotation);
        extents = new Vector3(
            MathF.Abs(rotation.M11) * half.X + MathF.Abs(rotation.M21) * half.Y + MathF.Abs(rotation.M31) * half.Z,
            MathF.Abs(rotation.M12) * half.X + MathF.Abs(rotation.M22) * half.Y + MathF.Abs(rotation.M32) * half.Z,
            MathF.Abs(rotation.M13) * half.X + MathF.Abs(rotation.M23) * half.Y + MathF.Abs(rotation.M33) * half.Z);
        center = Position;
    }
}
