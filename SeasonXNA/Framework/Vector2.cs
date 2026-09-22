// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Microsoft.Xna.Framework;

/// <summary>The CPU vector subset used by the 2D facade.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Vector2 : IEquatable<Vector2>
{
    public float X;
    public float Y;

    public Vector2(float value) : this(value, value) { }
    public Vector2(float x, float y) { X = x; Y = y; }

    public static Vector2 Zero => default;
    public static Vector2 One => new(1);
    public static Vector2 UnitX => new(1, 0);
    public static Vector2 UnitY => new(0, 1);

    public readonly float LengthSquared() => X * X + Y * Y;
    public readonly float Length() => (float)Math.Sqrt(LengthSquared());
    public static float DistanceSquared(Vector2 a, Vector2 b) => (a - b).LengthSquared();
    public static float Distance(Vector2 a, Vector2 b) => (a - b).Length();
    public static float Dot(Vector2 a, Vector2 b) => a.X * b.X + a.Y * b.Y;

    public static Vector2 Transform(Vector2 position, Matrix matrix) =>
        new(position.X * matrix.M11 + position.Y * matrix.M21 + matrix.M41,
            position.X * matrix.M12 + position.Y * matrix.M22 + matrix.M42);

    public static void Transform(ref Vector2 position, ref Matrix matrix, out Vector2 result) =>
        result = Transform(position, matrix);

    public static Vector2 operator +(Vector2 a, Vector2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vector2 operator -(Vector2 a, Vector2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vector2 operator -(Vector2 value) => new(-value.X, -value.Y);
    public static Vector2 operator *(Vector2 a, Vector2 b) => new(a.X * b.X, a.Y * b.Y);
    public static Vector2 operator *(Vector2 value, float scale) => new(value.X * scale, value.Y * scale);
    public static Vector2 operator *(float scale, Vector2 value) => value * scale;
    public static Vector2 operator /(Vector2 a, Vector2 b) => new(a.X / b.X, a.Y / b.Y);
    public static Vector2 operator /(Vector2 value, float divisor) => value * (1f / divisor);
    public static bool operator ==(Vector2 a, Vector2 b) => a.X == b.X && a.Y == b.Y;
    public static bool operator !=(Vector2 a, Vector2 b) => !(a == b);
    public readonly bool Equals(Vector2 other) => this == other;
    public override readonly bool Equals(object? obj) => obj is Vector2 other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(X, Y);
    public override readonly string ToString() => $"{{X:{X} Y:{Y}}}";
}
