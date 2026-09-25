// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Microsoft.Xna.Framework;

/// <summary>The integer point type used by the 2D facade.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Point : IEquatable<Point>
{
    public int X;
    public int Y;

    public Point(int x, int y) { X = x; Y = y; }

    public static Point Zero => default;
    public static Point Empty => default;

    public static Point operator -(Point value) => new(unchecked(-value.X), unchecked(-value.Y));
    public static Point operator -(Point a, Point b) => new(unchecked(a.X - b.X), unchecked(a.Y - b.Y));
    public static Point operator +(Point a, Point b) => new(unchecked(a.X + b.X), unchecked(a.Y + b.Y));

    public void Offset(int offsetX, int offsetY)
    {
        X = unchecked(X + offsetX); Y = unchecked(Y + offsetY);
    }

    public readonly Vector2 ToVector2() => new(X, Y);

    public static bool operator ==(Point a, Point b) => a.X == b.X && a.Y == b.Y;
    public static bool operator !=(Point a, Point b) => !(a == b);
    public readonly bool Equals(Point other) => this == other;
    public override readonly bool Equals(object? obj) => obj is Point other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(X, Y);
    public override readonly string ToString() => $"{{X:{X} Y:{Y}}}";
}
