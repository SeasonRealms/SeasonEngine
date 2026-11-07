// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// G1: the seven coefficients a <see cref="ColorGradingPreset"/> expands into, in the order the shaders apply them.
///
/// <para><b>Why these seven and no more.</b> Every coefficient here survives the round trip through a float constant
/// buffer, so the whole grade costs nothing but eight floats per frame and works identically on all four backends. A
/// half-float lookup table would need a 3D texture, a sampler and a new binding on every backend to deliver split
/// toning and hue warps these seven cannot express - deferred, not forgotten (see the post-processing document's known
/// gaps).</para>
///
/// <para><b>Domain.</b> Display-referred sRGB after tone mapping, not scene-linear: a grade authored by eye on a
/// monitor is a grade on the picture, and applying it before the tone curve would make every look respond to exposure
/// instead. FXAA, RCAS and the outline composite all read the already-graded image, so they cannot undo it.</para>
/// </summary>
public readonly struct ColorGradeParams
{
    /// <summary>Contrast about mid-grey: output = (rgb - 0.5) * contrast + 0.5. 1 is identity.</summary>
    public readonly float Contrast;

    /// <summary>Saturation against Rec.601 luma: 1 is identity, 0 is monochrome that keeps its luminance separation.</summary>
    public readonly float Saturation;

    /// <summary>White-point shift along the red/blue axis, applied as r *= 1 + t, b *= 1 - t. 0 is identity.</summary>
    public readonly float Temperature;

    /// <summary>White-point shift along the green/magenta axis, applied as g *= 1 + t. 0 is identity.</summary>
    public readonly float Tint;

    /// <summary>Black lift in [0, 1): how far pure black is raised toward the mid tones. 0 is identity.</summary>
    public readonly float Lift;

    /// <summary>Mid-tone power, applied as pow(saturate(rgb), 1 / gamma). Above 1 brightens the mid tones. 1 is identity.</summary>
    public readonly float Gamma;

    /// <summary>Linear gain: rgb *= gain before the lift blends it back. 1 is identity.</summary>
    public readonly float Gain;

    public ColorGradeParams(float contrast, float saturation, float temperature, float tint, float lift, float gamma, float gain)
    {
        Contrast = contrast;
        Saturation = saturation;
        Temperature = temperature;
        Tint = tint;
        Lift = lift;
        Gamma = gamma;
        Gain = gain;
    }

    /// <summary>Every step a no-op. <see cref="ColorGradingPreset.Neutral"/> resolves to this, which is what makes that
    /// preset the lossless control: the shaders still run every arithmetic step, and the picture has to come back
    /// unchanged. The only step that can alter a value here is the final saturate, and tone-mapped output is already
    /// inside [0, 1], so it does not.</summary>
    public static readonly ColorGradeParams Identity = new(1f, 1f, 0f, 0f, 0f, 1f, 1f);
}

/// <summary>
/// G1: turns the persisted <see cref="ColorGradingPreset"/> into the numbers the shaders consume, and packs them into
/// the one float layout every backend copies verbatim.
///
/// <para><b>Single source of truth.</b> The coefficient tables live here rather than in each backend because four copies
/// of a look would drift the moment one of them was tuned. Backends never see a preset - they see eight floats whose
/// meaning is fixed below - so a new look is a row in this file and nothing else.</para>
/// </summary>
public static class ColorGrading
{
    /// <summary>G1: the coefficient table. Structure shared across the film looks: lift the blacks slightly, open the
    /// mid tones, add a little contrast, then move the white point. The three looks differ in direction, not in shape,
    /// which is what keeps them feeling like the same lab graded the same footage.</summary>
    public static ColorGradeParams Lookup(ColorGradingPreset preset) => preset switch
    {
        // contrast, saturation, temperature, tint, lift, gamma, gain
        ColorGradingPreset.WarmFilm => new ColorGradeParams(1.08f, 1.10f, 0.05f, 0.01f, 0.020f, 1.06f, 1.01f),
        ColorGradingPreset.CoolTeal => new ColorGradeParams(1.10f, 1.05f, -0.06f, -0.02f, 0.015f, 0.98f, 1.00f),
        ColorGradingPreset.Mono => new ColorGradeParams(1.12f, 0f, 0f, 0f, 0.030f, 1.05f, 1.00f),

        // Neutral and Off both resolve to identity; they differ only in whether the caller runs the path at all, which
        // is exactly the difference a lossless-control test needs to observe.
        _ => ColorGradeParams.Identity,
    };

    /// <summary>
    /// G1: what this frame should upload. Returns identity with intensity 0 whenever the grade must not run - Off, or an
    /// intensity at or below zero - so the shaders' single <c>if (intensity &lt;= 0) return ldr;</c> guard is the only
    /// branch that has to be correct, and a disabled grade is bit-identical to the pipeline that had no grade at all.
    ///
    /// <para>Intensity is a blend against the ungraded image rather than a scale on the coefficients: at 0.5 the picture
    /// is halfway between the two renders, which is what an author expects from a strength slider, whereas scaling the
    /// coefficients toward identity would change the shape of the curve rather than its reach.</para>
    /// </summary>
    public static ColorGradeParams Resolve(RenderQuality quality, out float intensity)
    {
        if (quality.ColorGrade == ColorGradingPreset.Off || quality.ColorGradeIntensity <= 0f)
        {
            intensity = 0f;
            return ColorGradeParams.Identity;
        }

        // Above 1 is clamped, not extrapolated: a grade past its own coefficients is not a look anyone authored.
        intensity = Math.Clamp(quality.ColorGradeIntensity, 0f, 1f);
        return Lookup(quality.ColorGrade);
    }

    /// <summary>
    /// G1: writes the eight floats every backend uploads, in the order they are declared in the shaders. Destination must
    /// hold eight floats starting at index 0.
    ///
    /// <para>Layout: [0] intensity, [1] contrast, [2] saturation, [3] temperature, [4] tint, [5] lift, [6] gamma,
    /// [7] gain. Backends map this block onto their own channel and nothing else: D3D12 root constants 8-15, Vulkan push
    /// constants 7-14, Metal's BlitParams tail, and WebGPU a second and third vec4 after its existing one.</para>
    /// </summary>
    public static void Pack(in ColorGradeParams grade, float intensity, Span<float> destination)
    {
        destination[0] = intensity;
        destination[1] = grade.Contrast;
        destination[2] = grade.Saturation;
        destination[3] = grade.Temperature;
        destination[4] = grade.Tint;
        destination[5] = grade.Lift;
        destination[6] = grade.Gamma;
        destination[7] = grade.Gain;
    }

    /// <summary>G1: the block size <see cref="Pack"/> writes, exposed so a caller can size a stack buffer without
    /// restating the layout.</summary>
    public const int PackedFloatCount = 8;
}
