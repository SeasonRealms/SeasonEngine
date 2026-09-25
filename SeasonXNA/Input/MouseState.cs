// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Microsoft.Xna.Framework.Input;

/// <summary>
/// XNA mouse snapshot. Ported titles read it once per frame and compare consecutive
/// snapshots, so every field is a value captured at <see cref="Mouse.GetState"/> time.
/// </summary>
public struct MouseState : IEquatable<MouseState>
{
    public int X { get; }

    public int Y { get; }

    public int ScrollWheelValue { get; }

    public ButtonState LeftButton { get; }

    public ButtonState MiddleButton { get; }

    public ButtonState RightButton { get; }

    public ButtonState XButton1 { get; }

    public ButtonState XButton2 { get; }

    public MouseState(int x, int y, int scrollWheel, ButtonState leftButton, ButtonState middleButton, ButtonState rightButton, ButtonState xButton1, ButtonState xButton2)
    {
        X = x;
        Y = y;
        ScrollWheelValue = scrollWheel;
        LeftButton = leftButton;
        MiddleButton = middleButton;
        RightButton = rightButton;
        XButton1 = xButton1;
        XButton2 = xButton2;
    }

    public static bool operator ==(MouseState left, MouseState right) => left.Equals(right);

    public static bool operator !=(MouseState left, MouseState right) => !left.Equals(right);

    public readonly bool Equals(MouseState other) =>
        X == other.X &&
        Y == other.Y &&
        ScrollWheelValue == other.ScrollWheelValue &&
        LeftButton == other.LeftButton &&
        MiddleButton == other.MiddleButton &&
        RightButton == other.RightButton &&
        XButton1 == other.XButton1 &&
        XButton2 == other.XButton2;

    public override readonly bool Equals(object? obj) => obj is MouseState other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(X, Y, ScrollWheelValue, LeftButton, MiddleButton, RightButton, XButton1, XButton2);

    public override readonly string ToString() => $"{{X:{X} Y:{Y} Buttons:{LeftButton},{MiddleButton},{RightButton}}}";
}
