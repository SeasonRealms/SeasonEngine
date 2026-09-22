// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Microsoft.Xna.Framework;

[StructLayout(LayoutKind.Sequential)]
public struct Rectangle : IEquatable<Rectangle>
{
    public int X;
    public int Y;
    public int Width;
    public int Height;

    public Rectangle(int x, int y, int width, int height)
    {
        X = x; Y = y; Width = width; Height = height;
    }

    public static Rectangle Empty => default;
    public readonly int Left => X;
    public readonly int Top => Y;
    public readonly int Right => unchecked(X + Width);
    public readonly int Bottom => unchecked(Y + Height);
    public readonly bool IsEmpty => X == 0 && Y == 0 && Width == 0 && Height == 0;

    public readonly bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
    public readonly bool Contains(Vector2 value) =>
        value.X >= Left && value.X < Right && value.Y >= Top && value.Y < Bottom;
    public readonly bool Contains(Rectangle value) =>
        Left <= value.Left && value.Right <= Right && Top <= value.Top && value.Bottom <= Bottom;
    public readonly bool Intersects(Rectangle value) =>
        value.Left < Right && Left < value.Right && value.Top < Bottom && Top < value.Bottom;

    public static Rectangle Intersect(Rectangle a, Rectangle b)
    {
        if (!a.Intersects(b)) return Empty;
        int left = Math.Max(a.Left, b.Left), top = Math.Max(a.Top, b.Top);
        return new(left, top, unchecked(Math.Min(a.Right, b.Right) - left),
            unchecked(Math.Min(a.Bottom, b.Bottom) - top));
    }

    public void Offset(int x, int y) { X = unchecked(X + x); Y = unchecked(Y + y); }
    public void Inflate(int horizontalAmount, int verticalAmount)
    {
        X = unchecked(X - horizontalAmount); Y = unchecked(Y - verticalAmount);
        Width = unchecked(Width + horizontalAmount * 2); Height = unchecked(Height + verticalAmount * 2);
    }

    public static bool operator ==(Rectangle a, Rectangle b) =>
        a.X == b.X && a.Y == b.Y && a.Width == b.Width && a.Height == b.Height;
    public static bool operator !=(Rectangle a, Rectangle b) => !(a == b);
    public readonly bool Equals(Rectangle other) => this == other;
    public override readonly bool Equals(object? obj) => obj is Rectangle other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(X, Y, Width, Height);
    public override readonly string ToString() => $"{{X:{X} Y:{Y} Width:{Width} Height:{Height}}}";
}
