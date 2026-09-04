// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Season.Utils;

public static class MathHelper
{
    internal const float E = MathF.E;

    internal const float Log2E = 1.442695f;

    internal const float Log10E = 0.4342945f;

    public const float Pi = MathF.PI;

    public const float PiOver2 = (float)(Math.PI / 2.0);

    public const float PiOver4 = (float)(Math.PI / 4.0);

    public const float TwoPi = (float)(Math.PI * 2.0);

    internal const float Tau = TwoPi;

    public static bool Is64BitProcess { get; } = Unsafe.SizeOf<nuint>() == 8;

    public static float AllBitsSet
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            return BitConverter.UInt32BitsToSingle(0xFFFFFFFF);
        }
    }

    public const float ZeroTolerance = 1e-6f;

    public const double ZeroToleranceDouble = double.Epsilon * 8;

    private const double OneRadianInDegrees = 57.2957795131;

    private const double OneDegreeInRadians = 0.01745329252;

    public const float Epsilon = 1.1920929E-07f;

    public static MathSimdType SimdType
    {
        get
        {
            if (Sse.IsSupported)
            {
                return MathSimdType.Sse;
            }

            if (AdvSimd.IsSupported)
            {
                return MathSimdType.AdvSimd;
            }

            return MathSimdType.NoSimd;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CompareEqual(float left, float right) => MathF.Abs(left - right) <= ZeroTolerance;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CompareEqual(double left, double right, double epsilon) => Math.Abs(left - right) <= epsilon;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CompareEqual(float left, float right, float epsilon) => MathF.Abs(left - right) <= epsilon;

    public static bool IsZero(float a) => MathF.Abs(a) < ZeroTolerance;

    public static bool IsZero(double a) => Math.Abs(a) < ZeroToleranceDouble;

    public static bool IsOne(float a) => IsZero(a - 1.0f);

    public static bool WithinEpsilon(float a, float b, float epsilon)
    {
        float diff = a - b;
        return (-epsilon <= diff) && (diff <= epsilon);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ToDegrees(float radians) => radians * (180.0f / Pi);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ToRadians(float degree) => degree * (Pi / 180.0f);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Clamp(float value, float min, float max)
    {
        value = ((value > max) ? max : value);
        value = ((value < min) ? min : value);
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Clamp(double value, double min, double max)
    {
        value = ((value > max) ? max : value);
        value = ((value < min) ? min : value);
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Clamp(int value, int min, int max)
    {
        value = ((value > max) ? max : value);
        value = ((value < min) ? min : value);
        return value;
    }

    public static float SmoothStep(float amount)
    {
        return (amount <= 0) ? 0 : (amount >= 1) ? 1 : amount * amount * (3 - (2 * amount));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float SmoothStep(float value1, float value2, float amount)
    {
        float num = Clamp(amount, 0f, 1f);
        return Lerp(value1, value2, num * num * (3f - 2f * num));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Lerp(float value1, float value2, float amount)
    {
        return value1 + (value2 - value1) * amount;
    }

    internal static float LerpPrecise(float value1, float value2, float amount)
    {
        return ((1 - amount) * value1) + (value2 * amount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Distance(float value1, float value2)
    {
        return MathF.Abs(value1 - value2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsPow2(uint value)
    {
        return BitOperations.IsPow2(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsPow2(ulong value)
    {
        return BitOperations.IsPow2(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsPow2(nuint value)
    {
        return Is64BitProcess ? BitOperations.IsPow2(value) : BitOperations.IsPow2((uint)value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint AlignDown(uint address, uint alignment)
    {
        Debug.Assert(IsPow2(alignment));
        return address & ~(alignment - 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong AlignDown(ulong address, ulong alignment)
    {
        Debug.Assert(IsPow2(alignment));
        return address & ~(alignment - 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nuint AlignDown(nuint address, nuint alignment)
    {
        Debug.Assert(IsPow2(alignment));
        return address & ~(alignment - 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint AlignUp(uint address, uint alignment)
    {
        Debug.Assert(IsPow2(alignment));

        return (address + (alignment - 1)) & ~(alignment - 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong AlignUp(ulong address, ulong alignment)
    {
        Debug.Assert(IsPow2(alignment));

        return (address + (alignment - 1)) & ~(alignment - 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nuint AlignUp(nuint address, nuint alignment)
    {
        Debug.Assert(IsPow2(alignment));

        return (address + (alignment - 1)) & ~(alignment - 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint DivideByMultiple(uint value, uint alignment)
    {
        return ((value + alignment - 1) / alignment);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong DivideByMultiple(ulong value, ulong alignment)
    {
        return ((value + alignment - 1) / alignment);
    }

    public static float SRgbToLinear(float sRgbValue)
    {
        if (sRgbValue <= 0.04045f)
            return sRgbValue / 12.92f;
        return MathF.Pow((sRgbValue + 0.055f) / 1.055f, 2.4f);
    }

    public static float LinearToSRgb(float linearValue)
    {
        if (linearValue <= 0.0031308f)
            return 12.92f * linearValue;

        return 1.055f * MathF.Pow(linearValue, 0.4166667f) - 0.055f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int NextPowerOfTwo(int v)
    {
        v--;
        v |= v >> 1;
        v |= v >> 2;
        v |= v >> 4;
        v |= v >> 8;
        v |= v >> 16;
        v++;
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong NextPowerOfTwo(ulong v)
    {
        v--;
        v |= v >> 1;
        v |= v >> 2;
        v |= v >> 4;
        v |= v >> 8;
        v |= v >> 16;
        v++;
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Barycentric(float value1, float value2, float value3, float amount1, float amount2)
    {
        return value1 + amount1 * (value2 - value1) + amount2 * (value3 - value1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float CatmullRom(float value1, float value2, float value3, float value4, float amount)
    {
        float num = amount * amount;
        float num2 = amount * num;
        return 0.5f * (2f * value2 + (0f - value1 + value3) * amount + (2f * value1 - 5f * value2 + 4f * value3 - value4) * num + (0f - value1 + 3f * value2 - 3f * value3 + value4) * num2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Hermite(float value1, float tangent1, float value2, float tangent2, float amount)
    {
        float num = amount * amount;
        float num2 = amount * num;
        float num3 = 2f * num2 - 3f * num + 1f;
        float num4 = -2f * num2 + 3f * num;
        float num5 = num2 - 2f * num + amount;
        float num6 = num2 - num;
        return value1 * num3 + value2 * num4 + tangent1 * num5 + tangent2 * num6;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Area(ref Vector2 a, ref Vector2 b, ref Vector2 c)
    {
        return a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float LerpClamped(float value1, float value2, float amount)
    {
        return value1 + (value2 - value1) * Clamp(amount, 0f, 1f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float InverseLerp(float value1, float value2, float value)
    {
        return (value - value1) / (value2 - value1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Max(float value1, float value2)
    {
        return MathF.Max(value1, value2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Max(int value1, int value2)
    {
        return Math.Max(value1, value2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Min(float value1, float value2)
    {
        return MathF.Min(value1, value2);
    }

    internal static int Min(int value1, int value2)
    {
        return value1 < value2 ? value1 : value2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Max(ref Vector2 value)
    {
        float num = value.X;
        if (num < value.Y)
        {
            num = value.Y;
        }

        return num;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Min(ref Vector2 value)
    {
        float num = value.X;
        if (num > value.Y)
        {
            num = value.Y;
        }

        return num;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Max(ref Vector3 value)
    {
        float num = value.X;
        if (num < value.Y)
        {
            num = value.Y;
        }

        if (num < value.Z)
        {
            num = value.Z;
        }

        return num;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Min(ref Vector3 value)
    {
        float num = value.X;
        if (num > value.Y)
        {
            num = value.Y;
        }

        if (num > value.Z)
        {
            num = value.Z;
        }

        return num;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float SmoothDamp(float current, float target, ref float currentVelocity, float smoothTime, float gameTime)
    {
        smoothTime = MathF.Max(0.0001f, smoothTime);
        float num = 2f / smoothTime;
        float num2 = num * gameTime;
        float num3 = 1f / (1f + num2 + 0.48f * num2 * num2 + 0.235f * num2 * num2 * num2);
        float num4 = current - target;
        float num5 = target;
        target = current - num4;
        float num6 = (currentVelocity + num * num4) * gameTime;
        currentVelocity = (currentVelocity - num * num6) * num3;
        float num7 = target + (num4 + num6) * num3;
        if (num5 - current > 0f == num7 > num5)
        {
            num7 = num5;
            currentVelocity = (num7 - num5) / gameTime;
        }

        return num7;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ToRadians(double degrees)
    {
        return (float)(degrees * 0.01745329252);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float WrapAngle(float angle)
    {
        if ((angle > -Pi) && (angle <= Pi))
            return angle;
        angle %= TwoPi;
        if (angle <= -Pi)
            return angle + TwoPi;
        if (angle > Pi)
            return angle - TwoPi;
        return angle;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool FloatEquals(float value1, float value2)
    {
        return MathF.Abs(value1 - value2) <= 1.1920929E-07f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool FloatEquals(float value1, float value2, float delta)
    {
        return FloatInRange(value1, value2 - delta, value2 + delta);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool FloatInRange(float value, float min, float max)
    {
        if (value >= min)
        {
            return value <= max;
        }

        return false;
    }

    public static bool IsPowerOfTwo(int value)
    {
        return (value > 0) && ((value & (value - 1)) == 0);
    }
}

public enum MathSimdType
{
    NoSimd,

    Sse,

    AdvSimd
}

public static class FloatExtensions
{
    private const float DefaultError = 5.96046448E-06f;

    public static bool Equal(this float a, float b, float maxRelativeError = 5.96046448E-06f)
    {
        return MathF.Abs(a - b) < maxRelativeError;
    }

    public static bool Distinct(this float a, float b, float maxRelativeError = 5.96046448E-06f)
    {
        return MathF.Abs(a - b) > maxRelativeError;
    }
}

public struct Rect
{
    public int X;
    public int Y;
    public int Width;
    public int Height;

    public Rect(int x, int y, int width, int height)
    {
        X = x;
        Y = y; 
        Width = width; 
        Height = height;
    }
}