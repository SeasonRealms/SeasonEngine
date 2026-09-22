// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using NumericsMatrix = System.Numerics.Matrix4x4;

namespace Microsoft.Xna.Framework;

/// <summary>XNA row-vector storage with construction helpers limited to the 2D use case.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Matrix : IEquatable<Matrix>
{
    public float M11, M12, M13, M14;
    public float M21, M22, M23, M24;
    public float M31, M32, M33, M34;
    public float M41, M42, M43, M44;

    public Matrix(float m11, float m12, float m13, float m14,
        float m21, float m22, float m23, float m24,
        float m31, float m32, float m33, float m34,
        float m41, float m42, float m43, float m44)
    {
        M11 = m11; M12 = m12; M13 = m13; M14 = m14;
        M21 = m21; M22 = m22; M23 = m23; M24 = m24;
        M31 = m31; M32 = m32; M33 = m33; M34 = m34;
        M41 = m41; M42 = m42; M43 = m43; M44 = m44;
    }

    public static Matrix Identity => new(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
    public static Matrix CreateScale(float xScale, float yScale, float zScale) =>
        new(xScale, 0, 0, 0, 0, yScale, 0, 0, 0, 0, zScale, 0, 0, 0, 0, 1);
    public static void CreateScale(float xScale, float yScale, float zScale, out Matrix result) =>
        result = CreateScale(xScale, yScale, zScale);
    public static Matrix CreateTranslation(float xPosition, float yPosition, float zPosition) =>
        new(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, xPosition, yPosition, zPosition, 1);
    public static Matrix CreateRotationZ(float radians)
    {
        float cos = (float)Math.Cos(radians), sin = (float)Math.Sin(radians);
        return new(cos, sin, 0, 0, -sin, cos, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
    }

    internal readonly NumericsMatrix ToNumerics() => new(
        M11, M12, M13, M14, M21, M22, M23, M24,
        M31, M32, M33, M34, M41, M42, M43, M44);
    private static Matrix FromNumerics(NumericsMatrix value) => new(
        value.M11, value.M12, value.M13, value.M14, value.M21, value.M22, value.M23, value.M24,
        value.M31, value.M32, value.M33, value.M34, value.M41, value.M42, value.M43, value.M44);

    public static Matrix operator *(Matrix a, Matrix b) => FromNumerics(a.ToNumerics() * b.ToNumerics());
    public static void Multiply(ref Matrix a, ref Matrix b, out Matrix result) => result = a * b;
    public static bool operator ==(Matrix a, Matrix b) => a.ToNumerics() == b.ToNumerics();
    public static bool operator !=(Matrix a, Matrix b) => !(a == b);
    public readonly bool Equals(Matrix other) => this == other;
    public override readonly bool Equals(object? obj) => obj is Matrix other && Equals(other);
    public override readonly int GetHashCode() => ToNumerics().GetHashCode();
}
