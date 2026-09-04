// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// Shape parameters for a single cloud layer (2-5 Step C; contract clauses are in the RenderQuality class header, section 2-5 clause 11).
/// All length units are **kilometers**, matching the convention documented in the <see cref="Atmosphere"/> class header.
///
/// Geometry model: layer i is an infinitely thin **spherical shell**
/// (radius = GroundRadiusKm + ViewAltitudeKm + <see cref="AltitudeKm"/>).
/// The main shader intersects the view ray with that shell to get "world XZ on the cloud plane", then folds it into noise uv by <see cref="TileKm"/>.
/// A shell is used instead of a horizontal plane so clouds naturally converge near the horizon because of curvature
/// (a planar model goes to t->inf when dir.y->0, stretching the noise into infinitely long streaks);
/// cloud-shadow reverse intersection uses a planar approximation instead, where the difference is far below one noise texel at scene scales of a few hundred meters,
/// in exchange for one division instead of solving a quadratic.
/// </summary>
public struct SkyCloudLayer
{
    /// <summary>Cloud-base height above the observer (km). Low clouds: 1-2, mid clouds: 3-5, high clouds: 6-9.</summary>
    public float AltitudeKm;

    /// <summary>Geometric thickness (km). It only enters optical thickness (<c>tau = density x thickness / |dir.y|</c>)
    /// with no volumetric marching: this model is a "thin shell + analytic Beer-Lambert", so thickness is an optical control rather than a traversable volume.</summary>
    public float ThicknessKm;

    /// <summary>Horizontal scale covered by one tiled period of the noise (km). Low clouds use 10-15
    /// (a single cumulus is about 1 km, roughly 30-50 texels in the 512 noise texture), while high clouds use 25-35
    /// (cirrus spans larger areas and uses sparser texture detail).
    /// Too small -> visible repeating patterns; too large -> a single cloud spans half the sky and turns into a blurry fog mass.</summary>
    public float TileKm;

    /// <summary>Coverage (0=no clouds, 1=full overcast). Used as the threshold for noise remapping on the shader side:
    /// <c>d = saturate((base - (1 - coverage)) / coverage)</c>. So coverage determines both "what fraction of the sky contains clouds"
    /// and the softness of the edges as a side effect (small coverage means a small denominator and sharper edges).</summary>
    public float Coverage;

    /// <summary>Extinction coefficient (1/km). Cumulus: 3-6, stratus: 5-8, storm clouds: 10-15; multiplied by <see cref="ThicknessKm"/>
    /// to get vertical optical thickness (tau~1 is semi-transparent, tau&gt;4 is fully opaque).</summary>
    public float Density;

    /// <summary>High-frequency erosion strength (0-1). Uses the noise B channel to carve wispy detail into cloud edges; 0 = only low-frequency contours (plastic look),
    /// too high -> clouds get eaten through like a sieve.</summary>
    public float Detail;

    /// <summary>Horizontal wind speed (km/s, world XZ). 6 m/s = 0.006.
    /// It is **not multiplied by time in the shader**: the offset is integrated frame by frame into
    /// <see cref="Atmosphere.CloudWindOffsetKm"/> by <c>SkyLighting.AdvanceClouds</c>; see that method summary for the rationale.</summary>
    public Vector2 WindKmPerSec;

    /// <summary>Per-field linear interpolation (all geometric and optical parameters interpolate continuously; wind speed interpolates too, so clouds change speed gradually when switching presets).</summary>
    public static SkyCloudLayer Lerp(in SkyCloudLayer a, in SkyCloudLayer b, float t)
    {
        return new SkyCloudLayer
        {
            AltitudeKm = a.AltitudeKm + (b.AltitudeKm - a.AltitudeKm) * t,
            ThicknessKm = a.ThicknessKm + (b.ThicknessKm - a.ThicknessKm) * t,
            TileKm = a.TileKm + (b.TileKm - a.TileKm) * t,
            Coverage = a.Coverage + (b.Coverage - a.Coverage) * t,
            Density = a.Density + (b.Density - a.Density) * t,
            Detail = a.Detail + (b.Detail - a.Detail) * t,
            WindKmPerSec = Vector2.Lerp(a.WindKmPerSec, b.WindKmPerSec, t),
        };
    }
}

/// <summary>
/// Inline cloud-layer array (<see cref="SkyState.MaxLayers"/> entries). The limit of 3 comes from:
/// low clouds (cumulus/stratus) + mid clouds + high clouds (cirrus) already covering the three main cloud families in meteorology,
/// and each extra layer adds one spherical-shell intersection + three noise samples in the sky branch,
/// plus one noise sample for cloud shadows per directional light.
/// The cloud-shadow side is a **per-fragment x per-light** cost, so layer count is a real pixel cost.
/// </summary>
[InlineArray(SkyState.MaxLayers)]
public struct SkyCloudLayerArray
{
    private SkyCloudLayer _element0;
}

/// <summary>
/// Overall state of procedural clouds (2-5 Step C): a set of cloud layers plus global optical parameters.
/// The intended use is an **interpolable weather preset**:
/// the app interpolates between a few presets through <see cref="Lerp"/> and writes the result into <see cref="Atmosphere.Clouds"/>,
/// while the engine does not decide how the weather should evolve, matching the division of responsibility used by <see cref="Atmosphere"/> and <see cref="DayNightCycle"/>.
///
/// -- Why clouds do not go into the Sky-View LUT and are evaluated per pixel in the main shader --
/// The Sky-View LUT is a 256x128 full-sky map, so each texel spans about 1.4 degrees; at 1080p / 60 degree FOV one texel covers about 45 pixels,
/// which would blur cloud edges into foggy blobs under bilinear filtering.
/// This is the same reason the analytic sun disk does not go into the LUT; see the b11 note in <see cref="Atmosphere"/>.
/// The cost is that clouds **do not participate** in SH9 environment lighting or multiple scattering:
/// overcast skies do not darken ambient lighting, and clouds do not gray out the whole sky.
/// That is a deliberate tradeoff rather than an omission, because supporting it would require feeding average cloud attenuation back into the skyView kernel and SH9 projection,
/// and both of those run in the FrameStart phase, one full frame earlier than cloud parameters are consumed.
///
/// -- Consistency with cloud shadows --
/// The clouds visible in the sky branch and the shadows cast by <c>ComputeCloudShadow</c> in the geometry PS use the **same** noise texture,
/// the same coverage remapping, and the same parameters from this struct, differing only in shell-vs-plane intersection method (see <see cref="SkyCloudLayer"/>).
/// Therefore "the cloud you see is the cloud that casts the shadow", rather than two separately tuned approximations.
/// </summary>
public struct SkyState
{
    /// <summary>Maximum number of cloud layers (see <see cref="SkyCloudLayerArray"/> for the cost rationale).</summary>
    public const int MaxLayers = 3;

    /// <summary>Cloud-layer array; only the first <see cref="LayerCount"/> entries are valid.</summary>
    public SkyCloudLayerArray Layers;

    /// <summary>Active layer count (0 = no clouds, equivalent to the Step B sky). Clamped to [0, <see cref="MaxLayers"/>].</summary>
    public int LayerCount;

    /// <summary>Single-scattering albedo of the cloud (linear RGB). Cloud droplets are Mie scatterers and nearly neutral white,
    /// so normal values stay near (1,1,1); polluted or dusty skies may shift yellow. It multiplies the cloud illumination to produce cloud radiance.</summary>
    public Vector3 Albedo;

    /// <summary>Cloud-shadow strength (0=no shadowing, 1=direct light falls to zero under full occlusion). Semantically equivalent to
    /// <c>SceneLightParams.ShadowParams1.y</c> (CSM shadow strength), and the two multiply together.</summary>
    public float ShadowStrength;

    /// <summary>Cloud Henyey-Greenstein anisotropy factor (0-1). Cloud droplets are strongly forward-scattering and the physical value is close to 0.85;
    /// here it stays around 0.7 because it only controls the width of the silver lining when viewing clouds against the light.</summary>
    public float PhaseG;

    /// <summary>Minimum darkening floor for cloud bottoms (0-1): the portion of cloud radiance that does not vary with lighting direction.
    /// 0 = pure black backlit sides (not realistic, since real cloud bottoms still receive substantial light from multiple scattering and ground reflection),
    /// 1 = clouds are fully unaffected by lighting direction (flat sticker look). Thick clouds use smaller values, thin clouds larger ones.</summary>
    public float AmbientFloor;

    /// <summary>Forward-scattering gain: intensity multiplier for the silver lining (multiplied onto the HG phase term). 0 = disable the silver lining.</summary>
    public float ForwardGain;

    /// <summary>Clear sky: zero layers. This is the default value of <see cref="Atmosphere.Clouds"/>, and an app that never feeds cloud data remains bit-for-bit consistent with Step B
    /// (layer count 0 -> uploaded layer-count field is 0 -> both the sky branch and cloud-shadow function are skipped entirely in the shader).</summary>
    public static readonly SkyState Clear = MakeClear();

    /// <summary>Fair weather: low cumulus (about 40% coverage with semi-transparent edges) + high cirrus. Default showcase preset.</summary>
    public static readonly SkyState Fair = MakeFair();

    /// <summary>Overcast: low stratus almost fully covering and opaque + mid layer + thin high layer, reducing direct light close to full occlusion.</summary>
    public static readonly SkyState Overcast = MakeOvercast();

    /// <summary>Storm: fully covered thick clouds, doubled extinction, very dark cloud bottoms, and nearly disabled silver lining.</summary>
    public static readonly SkyState Storm = MakeStorm();

    /// <summary>
    /// Interpolate between two weather presets (t is clamped to [0,1]). When layer counts differ, take the **larger** one,
    /// and for any missing layer borrow geometry (height/thickness/scale/wind) from the other side while starting coverage and density from 0.
    /// That way a new cloud layer fades in from transparency rather than popping into the wrong altitude and then sliding into place.
    /// </summary>
    public static SkyState Lerp(in SkyState a, in SkyState b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        int count = Math.Max(a.LayerCount, b.LayerCount);

        var result = new SkyState
        {
            LayerCount = count,
            Albedo = Vector3.Lerp(a.Albedo, b.Albedo, t),
            ShadowStrength = a.ShadowStrength + (b.ShadowStrength - a.ShadowStrength) * t,
            PhaseG = a.PhaseG + (b.PhaseG - a.PhaseG) * t,
            AmbientFloor = a.AmbientFloor + (b.AmbientFloor - a.AmbientFloor) * t,
            ForwardGain = a.ForwardGain + (b.ForwardGain - a.ForwardGain) * t,
        };

        for (int i = 0; i < count; i++)
        {
            bool hasA = i < a.LayerCount;
            bool hasB = i < b.LayerCount;
            var la = hasA ? a.Layers[i] : Faded(b.Layers[i]);
            var lb = hasB ? b.Layers[i] : Faded(a.Layers[i]);
            result.Layers[i] = SkyCloudLayer.Lerp(la, lb, t);
        }

        return result;
    }

    /// <summary>Keep the geometry of one layer and zero out its optical terms (the fade endpoint used by <see cref="Lerp"/>).</summary>
    static SkyCloudLayer Faded(in SkyCloudLayer src)
    {
        var faded = src;
        faded.Coverage = 0f;
        faded.Density = 0f;
        return faded;
    }

    static SkyState MakeClear()
    {
        // When layer count is 0 the other fields are not consumed, but we still provide neutral values so they remain valid interpolation endpoints between presets.
        return new SkyState
        {
            LayerCount = 0,
            Albedo = Vector3.One,
            ShadowStrength = 0f,
            PhaseG = 0.7f,
            AmbientFloor = 0.4f,
            ForwardGain = 0.6f,
        };
    }

    static SkyState MakeFair()
    {
        var s = new SkyState
        {
            LayerCount = 2,
            Albedo = Vector3.One,
            ShadowStrength = 0.7f,
            PhaseG = 0.7f,
            AmbientFloor = 0.4f,
            ForwardGain = 0.6f,
        };

        // Low cumulus: peak tau ~ 0.55 (d after density remapping) x 3.5 x 0.6 ~ 1.15 -> transmittance about 0.32,
        // so the cloud core is close to opaque while edges (small d) stay naturally semi-transparent, which is exactly where the soft cumulus fringe comes from.
        s.Layers[0] = new SkyCloudLayer
        {
            AltitudeKm = 1.6f,
            ThicknessKm = 0.6f,
            TileKm = 12f,
            Coverage = 0.42f,
            Density = 3.5f,
            Detail = 0.45f,
            WindKmPerSec = new Vector2(0.006f, 0.002f),
        };

        // High cirrus: peak tau is only about 0.2 -> always semi-transparent, with larger scale and faster wind (upper-level westerlies).
        s.Layers[1] = new SkyCloudLayer
        {
            AltitudeKm = 7f,
            ThicknessKm = 0.4f,
            TileKm = 30f,
            Coverage = 0.3f,
            Density = 0.7f,
            Detail = 0.7f,
            WindKmPerSec = new Vector2(0.02f, 0.004f),
        };

        return s;
    }

    static SkyState MakeOvercast()
    {
        var s = new SkyState
        {
            LayerCount = 3,
            Albedo = new Vector3(0.95f, 0.95f, 0.97f),
            ShadowStrength = 0.9f,
            PhaseG = 0.6f,
            AmbientFloor = 0.28f,
            ForwardGain = 0.25f,
        };

        // Low stratus: d~0.9 -> tau ~ 0.9 x 5 x 1.2 = 5.4 -> transmittance 0.005, effectively fully opaque.
        s.Layers[0] = new SkyCloudLayer
        {
            AltitudeKm = 0.9f,
            ThicknessKm = 1.2f,
            TileKm = 20f,
            Coverage = 0.85f,
            Density = 5f,
            Detail = 0.25f,
            WindKmPerSec = new Vector2(0.008f, 0.003f),
        };

        s.Layers[1] = new SkyCloudLayer
        {
            AltitudeKm = 3.2f,
            ThicknessKm = 0.8f,
            TileKm = 16f,
            Coverage = 0.6f,
            Density = 3f,
            Detail = 0.4f,
            WindKmPerSec = new Vector2(0.012f, 0.004f),
        };

        s.Layers[2] = new SkyCloudLayer
        {
            AltitudeKm = 7.5f,
            ThicknessKm = 0.4f,
            TileKm = 30f,
            Coverage = 0.45f,
            Density = 0.8f,
            Detail = 0.6f,
            WindKmPerSec = new Vector2(0.024f, 0.005f),
        };

        return s;
    }

    static SkyState MakeStorm()
    {
        var s = new SkyState
        {
            LayerCount = 2,
            Albedo = new Vector3(0.9f, 0.9f, 0.94f),
            ShadowStrength = 0.96f,
            PhaseG = 0.55f,
            AmbientFloor = 0.14f,
            ForwardGain = 0.1f,
        };

        // Storm clouds: tau easily exceeds 10 -> fully black and opaque, cloud bottoms darken to 0.14x, and ShadowStrength 0.96 clamps down direct light.
        s.Layers[0] = new SkyCloudLayer
        {
            AltitudeKm = 0.7f,
            ThicknessKm = 2f,
            TileKm = 22f,
            Coverage = 0.95f,
            Density = 12f,
            Detail = 0.3f,
            WindKmPerSec = new Vector2(0.018f, 0.007f),
        };

        s.Layers[1] = new SkyCloudLayer
        {
            AltitudeKm = 4f,
            ThicknessKm = 1f,
            TileKm = 14f,
            Coverage = 0.8f,
            Density = 6f,
            Detail = 0.5f,
            WindKmPerSec = new Vector2(0.026f, 0.009f),
        };

        return s;
    }
}
