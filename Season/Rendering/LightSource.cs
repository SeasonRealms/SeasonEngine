// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// Authoring-layer light kinds.
/// They map one-to-one to GPU-side <see cref="Season.Controls.GpuLight"/> DirType.w values,
/// namely <see cref="Season.Controls.GpuLight.TypePoint"/>, TypeSpot, and TypeDirectional.
/// </summary>
public enum LightKind
{
    /// <summary>Directional light, such as the sun or moon. It has direction only, with no position and no attenuation.</summary>
    Directional,

    /// <summary>Point light. It has a position and attenuates as 1/d², with <see cref="LightSource.Range"/> optionally providing a cutoff window.</summary>
    Point,

    /// <summary>Spot light. It is a point light plus a cone, with inner and outer smoothstep boundaries.</summary>
    Spot,
}

/// <summary>
/// Authoring-layer light source, a persistent App-side object rather than a GPU layout.
///
/// Division of responsibility with <see cref="Season.Controls.GpuLight"/>:
/// - LightSource is "one light in the scene", held long-term by the App, with freely editable properties such as angles in radians and linear 0~1 color.
/// - GpuLight is "the 64-byte payload sent to shaders this frame", packed in place every frame by <see cref="ToGpu"/>.
///
/// Managed by <see cref="SceneLighting"/> through Add and Remove.
/// <see cref="IsOpen"/> is a temporary switch: when false, the light does not participate in the current frame's Bake, but the object remains in the scene and requires no reconstruction.
/// The authoring layer allows an unlimited number of lights, and Bake trims them into
/// <see cref="Season.Controls.SceneLightParams.MaxLights"/> GPU slots according to <see cref="Priority"/>.
/// </summary>
public sealed class LightSource
{
    /// <summary>Light kind, which determines how <see cref="ToGpu"/> packs the data.</summary>
    public LightKind Kind;

    /// <summary>Temporary switch. When false, the light does not participate in this frame's Bake and does not consume a GPU slot, but the object remains in the scene.</summary>
    public bool IsOpen = true;

    /// <summary>Name used for debugging and lookup. It does not participate in rendering.</summary>
    public string Name = string.Empty;

    /// <summary>Linear color in 0~1, decoupled from intensity.</summary>
    public Vector3 Color = Vector3.One;

    /// <summary>Intensity multiplier. When &lt;=0, the light does not participate in this frame's Bake, effectively meaning it is off. Sun and moon lights use this path once they fall below the horizon.</summary>
    public float Intensity = 1f;

    /// <summary>World-space position, used by point and spot lights. Ignored by directional lights.</summary>
    public Vector3 Position;

    /// <summary>World-space propagation direction pointing toward the lit surface. Used by directional and spot lights, and normalized internally by <see cref="ToGpu"/>.</summary>
    public Vector3 Direction = -Vector3.UnitY;

    /// <summary>Attenuation range, used by point and spot lights. Values &lt;=0 mean infinite range and reduce to pure 1/d² attenuation under the KHR semantic.</summary>
    public float Range;

    /// <summary>Spot-light inner cone half-angle in radians. Full intensity is used inside this cone.</summary>
    public float InnerConeAngle = MathF.PI / 6f;

    /// <summary>Spot-light outer cone half-angle in radians. Intensity falls to zero outside this cone, with a smoothstep transition between inner and outer angles.</summary>
    public float OuterConeAngle = MathF.PI / 4f;

    /// <summary>Whether the light casts shadows. Bake raises its trimming priority and also considers it when choosing directionalIndex and spotShadowIndex.</summary>
    public bool CastShadows;

    /// <summary>Trimming priority. When the GPU limit is exceeded, higher values are kept first. Directional lights and shadow casters receive additional Bake-side weighting.</summary>
    public int Priority;

    /// <summary>
    /// Packs the light into its GPU representation, returning by value with zero allocation.
    /// Directional lights have no position and no attenuation.
    /// Point and spot lights keep the established 1-2 semantics, where SpotParams store cos(inner) and cos(outer) for shader-side smoothstep evaluation.
    /// </summary>
    public GpuLight ToGpu()
    {
        switch (Kind)
        {
            case LightKind.Directional:
                return GpuLight.Directional(NormalizedDirection(), Color, Intensity);

            case LightKind.Spot:
                return GpuLight.Spot(
                    Position, NormalizedDirection(), Color, Intensity, Range,
                    MathF.Cos(InnerConeAngle), MathF.Cos(OuterConeAngle));

            default:
                return GpuLight.Point(Position, Color, Intensity, Range);
        }
    }

    /// <summary>Normalizes the propagation direction. Degenerate zero vectors fall back to -Y, pointing downward, to avoid shader-side normalize(0) producing NaN.</summary>
    Vector3 NormalizedDirection()
    {
        return Direction.LengthSquared() < 1e-12f ? -Vector3.UnitY : Vector3.Normalize(Direction);
    }
}
