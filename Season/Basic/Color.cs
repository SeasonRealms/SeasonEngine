// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Basic;

public struct Color : IEquatable<Color>
{
    // Stored internally as Vector4 (RGBA float [0,1]), matching the GPU constant-buffer memory layout.
    Vector4 _value;

    // Expose the GPU-compatible Vector4 directly (zero cost, no conversion).
    public Vector4 AsVector4 => _value;

    // PackedValue: preserved for backward compatibility and assembled from float components on demand.
    [CLSCompliant(false)]
    public uint PackedValue
    {
        readonly get
        {
            unchecked
            {
                return (uint)((byte)(_value.W * 255f + 0.5f) << 24)
                     | (uint)((byte)(_value.Z * 255f + 0.5f) << 16)
                     | (uint)((byte)(_value.Y * 255f + 0.5f) << 8)
                     | (uint)((byte)(_value.X * 255f + 0.5f));
            }
        }
        set
        {
            _value = new Vector4(
                (value & 0xFF) / 255f,
                ((value >> 8) & 0xFF) / 255f,
                ((value >> 16) & 0xFF) / 255f,
                ((value >> 24) & 0xFF) / 255f);
        }
    }

    [CLSCompliant(false)]
    public Color(uint packedValue)
    {
        _value = new Vector4(
            (packedValue & 0xFF) / 255f,
            ((packedValue >> 8) & 0xFF) / 255f,
            ((packedValue >> 16) & 0xFF) / 255f,
            ((packedValue >> 24) & 0xFF) / 255f);
    }

    // Constructors from Vector4 / Vector3 (stored directly with no precision loss).
    public Color(Vector4 color)
    {
        _value = color;
    }

    public Color(Vector3 color)
    {
        _value = new Vector4(color.X, color.Y, color.Z, 1f);
    }

    // Constructors that reuse an existing Color while replacing alpha.
    public Color(Color color, int alpha)
    {
        _value = color._value;
        _value.W = MathHelper.Clamp(alpha, 0, 255) / 255f;
    }

    public Color(Color color, float alpha)
    {
        _value = color._value;
        _value.W = MathHelper.Clamp(alpha, 0f, 1f);
    }

    // Constructors from float components in [0,1].
    public Color(float r, float g, float b)
    {
        _value = new Vector4(r, g, b, 1f);
    }

    public Color(float r, float g, float b, float alpha)
    {
        _value = new Vector4(r, g, b, alpha);
    }

    // Constructors from int components in [0,255].
    public Color(int r, int g, int b)
    {
        _value = new Vector4(
            MathHelper.Clamp(r, 0, 255) / 255f,
            MathHelper.Clamp(g, 0, 255) / 255f,
            MathHelper.Clamp(b, 0, 255) / 255f,
            1f);
    }

    public Color(int r, int g, int b, int alpha)
    {
        _value = new Vector4(
            MathHelper.Clamp(r, 0, 255) / 255f,
            MathHelper.Clamp(g, 0, 255) / 255f,
            MathHelper.Clamp(b, 0, 255) / 255f,
            MathHelper.Clamp(alpha, 0, 255) / 255f);
    }

    // Constructor from byte components in [0,255] (used by Colors.cs; signature unchanged).
    public Color(byte r, byte g, byte b, byte alpha)
    {
        _value = new Vector4(r / 255f, g / 255f, b / 255f, alpha / 255f);
    }

    // Byte channel accessors.
    public byte R
    {
        readonly get => (byte)(_value.X * 255f + 0.5f);
        set => _value.X = value / 255f;
    }

    public byte G
    {
        readonly get => (byte)(_value.Y * 255f + 0.5f);
        set => _value.Y = value / 255f;
    }

    public byte B
    {
        readonly get => (byte)(_value.Z * 255f + 0.5f);
        set => _value.Z = value / 255f;
    }

    public byte A
    {
        readonly get => (byte)(_value.W * 255f + 0.5f);
        set => _value.W = value / 255f;
    }

    // Implicit conversions: Color <-> Vector4 (zero-cost reinterpretation).
    public static implicit operator Vector4(Color c) => c._value;
    public static implicit operator Color(Vector4 v) => new(v);

    // Equality comparison.
    public static bool operator ==(Color left, Color right) => left._value == right._value;
    public static bool operator !=(Color left, Color right) => left._value != right._value;

    public override readonly int GetHashCode() => _value.GetHashCode();
    public override readonly bool Equals(object obj) => obj is Color other && Equals(other);
    public readonly bool Equals(Color other) => _value == other._value;

    // Interpolation / scaling.
    public static Color Lerp(Color start, Color end, float amount)
    {
        amount = MathHelper.Clamp(amount, 0f, 1f);
        return new Color(
            start._value.X + (end._value.X - start._value.X) * amount,
            start._value.Y + (end._value.Y - start._value.Y) * amount,
            start._value.Z + (end._value.Z - start._value.Z) * amount,
            start._value.W + (end._value.W - start._value.W) * amount);
    }

    public static Color Multiply(Color left, float right) => new(left._value * right);
    public static Color operator *(Color left, float right) => new(left._value * right);
    public static Color operator *(float left, Color right) => new(right._value * left);

    // ToVector: return the internal value directly (zero cost).
    public readonly Vector3 ToVector3() => new(_value.X, _value.Y, _value.Z);
    public readonly Vector4 ToVector4() => _value;
    public readonly Vector4 ToMathVector4() => _value;

    // Static factories.
    public static Color FromNonPremultiplied(Vector4 vector)
    {
        return new Color(vector.X * vector.W, vector.Y * vector.W, vector.Z * vector.W, vector.W);
    }

    public static Color FromNonPremultiplied(int r, int g, int b, int a)
    {
        return new Color(r * a / 255f, g * a / 255f, b * a / 255f, a / 255f);
    }

    // Debugging / deconstruction helpers.
    internal readonly string DebugDisplayString => string.Concat(
        R.ToString(), "  ",
        G.ToString(), "  ",
        B.ToString(), "  ",
        A.ToString());

    public override readonly string ToString()
    {
        var sb = new StringBuilder(25);
        sb.Append("{R:");
        sb.Append(R);
        sb.Append(" G:");
        sb.Append(G);
        sb.Append(" B:");
        sb.Append(B);
        sb.Append(" A:");
        sb.Append(A);
        sb.Append("}");
        return sb.ToString();
    }

    public readonly string ToStringLite() => $"{R} {G} {B} {A}";

    public readonly void Deconstruct(out byte r, out byte g, out byte b)
    {
        r = R; g = G; b = B;
    }

    public readonly void Deconstruct(out float r, out float g, out float b)
    {
        r = _value.X; g = _value.Y; b = _value.Z;
    }

    public readonly void Deconstruct(out byte r, out byte g, out byte b, out byte a)
    {
        r = R; g = G; b = B; a = A;
    }

    public readonly void Deconstruct(out float r, out float g, out float b, out float a)
    {
        r = _value.X; g = _value.Y; b = _value.Z; a = _value.W;
    }

}
