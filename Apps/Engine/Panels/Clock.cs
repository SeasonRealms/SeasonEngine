// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Numerics;

namespace Engine.Panels;

/// <summary>
/// Day/night clock modelled on Warcraft 3's top-centre dial: a bordered disc between the Settings
/// button and the rear-view mirror, showing the sun over a blue sky from 06:00 to 18:00 and a
/// crescent over a starry sky for the other half of the day.
///
/// <para>The face is built from layers instead of one painted picture, because it also answers for
/// the island weather: two skies, the sun or the moon, three cloud banks over the upper half, rain,
/// snow and a bolt under them. The skies, sun, moon, cloud and bolt are painted by .dbg/_clock_assets
/// through SeasonImage; rain and snow are drawn, not painted - 2D controls cannot rotate, so a drop
/// is a short run of dots stepping down and slightly to the left, and a flake is five dots that sway
/// as they fall. Everything else is procedural too: the bronze rim is three concentric
/// <see cref="Shape"/> circles, and the twelve hour markers are each an unlit ring (outer circle
/// plus a band-coloured hole) that gains a filled, haloed centre once its hour has come. WC3 draws
/// eight dots; this dial draws twelve, one per hour of the clock face, because here the ring has to
/// answer "what time is it" rather than "how far into the day are we".</para>
///
/// <para>The clock reads <see cref="App.HourOfDay"/>, i.e. the same integrated day phase that drives
/// the sun and moon, so the dial and the sky can never disagree. The markers read the twelve-hour
/// clock face instead of the progress through the half-day - the one place this dial deliberately
/// parts ways with WC3, whose dots track the day cycle: 12:xx and 00:xx light all twelve, while
/// 13:xx and 01:xx light one, so the lit count is simply the hour the dial is on. Which face stands
/// behind them is chosen by the hour alone: sun from 06:00 up to 18:00, moon for the other half,
/// with the cross-fade landing exactly on those two boundaries.</para>
///
/// <para>Weather is sampled from <see cref="Management.WeatherController.Field"/> at the island
/// centre, so the dial rides the same continuous strengths the sky, cloud deck and rain curtain
/// use - already blended by coverage weight and eased by the region's four-second transition.
/// Cloud strength decides how many banks stand over the upper half and how far they grey down, and
/// a closed sky (overcast and every rain or snow state) also grows them and slides them to the lower
/// right; the sun and moon are shown only while the sky is fair (clear and cloudy) and vanish once the
/// lid closes; rain strength decides how many drops fall under the cloud base and snow strength how
/// many flakes drift down through the same band; a live charge drives a short double flash. A dry
/// thunderstorm therefore shows a bolt under a black lid with no rain at all, which is exactly what
/// separates thunder from thunderstorm in the weather table.</para>
/// </summary>
internal class Clock : Panel
{
    // -- Framing, shared with the neighbours it sits between --

    // The Settings sprite is a fixed 80x80 at (80, 80) (see Setting.Update), so its right edge and
    // centre line are constants. The right-hand bound comes from RearViews.LeftEdge.
    const float SettingRight = 160f;
    const float SettingCenterY = 120f;

    const float GapPadding = 12f;
    const float DiameterMin = 120f;
    const float DiameterMax = 176f;

    // The face switches on these two hours only. Day runs from the engine's authored sunrise to
    // 18:00; the day-night cycle publishes the sunrise anchor but not its closing end, and the
    // world's lighting treats the two symmetrically, so the sunset stays a literal next to it.
    const float DayStartHour = Season.Rendering.DayNightCycle.SunriseHour;
    const float DayEndHour = 18f;

    // Twelve markers, one per hour of the twelve-hour clock face. 12:00 lights all of them.
    const int MarkerCount = 12;

    // Face and marker proportions, all relative to the diameter so the whole dial scales as one.
    const float FaceInset = 30f;          // rim + band thickness on each side, in design units at DiameterMax
    const float MarkerSize = 13f;         // outer diameter of one unlit ring
    const float MarkerHole = 0.54f;       // hole diameter as a fraction of MarkerSize
    const float MarkerCore = 0.58f;       // lit centre as a fraction of MarkerSize (just over the hole)
    const float MarkerGlow = 1.55f;       // halo diameter as a fraction of MarkerSize
    const float MarkerRadius = 0.457f;    // marker centres, as a fraction of the diameter

    // Seconds for the day/night face to cross-fade at the 06:00 and 18:00 switch.
    const float FaceFadeSeconds = 0.7f;

    // -- Layers: sky at the bottom, weather over it, hour markers on top --

    const int LayerSkyNight = 3, LayerSkyDay = 4;
    const int LayerMoon = 5, LayerSun = 6;
    const int LayerCloud = 7;
    const int LayerRain = 8;
    const int LayerSnow = 9;
    const int LayerBolt = 10;
    const int LayerHalo = 12, LayerRing = 13, LayerHole = 14, LayerCore = 15;

    // -- Weather proportions, all relative to the face diameter --

    // Cloud banks, back to front: centre offset, tuft size, and the cloud strength at which the
    // bank starts to fade in. All three sit in the upper half so the face reads as cloud above and
    // weather below: one fair-weather cloud (cloudy), a second bank as the sky fills in, and a lid for
    // overcast and the rain and snow states. Offsets keep each tuft's soft body inside the face circle.
    static readonly (float X, float Y, float Size, float From)[] CloudBanks =
    [
        ( 0.12f, -0.26f, 0.50f, 0.06f),
        (-0.14f, -0.27f, 0.52f, 0.38f),
        ( 0.02f, -0.24f, 0.62f, 0.52f),
    ];

    const float CloudFade = 0.20f;      // cloud strength a bank takes to reach full opacity

    // A closed sky grows the banks and slides them down and to the right, so overcast and the rain and
    // snow states fill the dial instead of hanging one small tuft over an empty disc. Cloudy sits
    // below the threshold and keeps the authored sizes.
    const float CloudOvercastFrom = 0.45f, CloudOvercastSpan = 0.35f;
    const float CloudGrow = 0.22f;      // added tuft size at full overcast
    const float CloudDown = 0.045f;     // added downward offset at full overcast
    const float CloudDrift = 0.05f;     // added rightward offset at full overcast

    const float SymbolSize = 0.58f;     // sun / moon diameter; deliberately large so it reads at a glance
    const float BoltSize = 0.45f;       // grew by a third from the first cut, all of it downward
    const float BoltOffsetX = 0.03f, BoltOffsetY = 0.205f;  // below the cloud base, never over it
    const float BoltFlashPeriod = 2.8f;

    // Rain band: fraction of the face diameter, measured from the face centre. It starts under the
    // cloud base and stops short of the lower markers. Snow drifts through the same band.
    const int RainDropCount = 30;
    const int RainSegments = 4;
    const float RainTop = 0.02f, RainBottom = 0.36f, RainHalfWidth = 0.28f;
    const float RainDot = 0.0135f;       // dot diameter
    const float RainSegStep = 0.011f;    // per-segment fall, so a stroke is about 4.4% of the face
    const float RainSegSlant = 0.0035f;  // per-segment lean, a little over ten degrees
    const float RainFallSeconds = 1.05f; // one full top-to-bottom pass

    // Snow is the same band drawn as flakes instead of strokes: five dots per flake (a centre and
    // four arms), a slow fall, and a side-to-side sway. Density, not size, is what separates light snow
    // from heavy snow.
    const int SnowFlakeCount = 26;
    const int SnowDotsPerFlake = 5;
    const float SnowCore = 0.020f;       // centre dot diameter
    const float SnowArm = 0.013f;        // arm dot diameter
    const float SnowReach = 0.019f;      // arm distance from the centre
    const float SnowHalfWidth = 0.23f;
    const float SnowSway = 0.050f;       // horizontal swing amplitude
    const float SnowSwaySeconds = 3.2f;  // one full swing
    const float SnowFallSeconds = 3.6f;  // one full top-to-bottom pass

    // Centre, then up, down, left, right: one flake is a centre dot plus four arms.
    static readonly (float X, float Y, bool Core)[] SnowShape =
    [
        (0f, 0f, true),
        (0f, -1f, false), (0f, 1f, false), (-1f, 0f, false), (1f, 0f, false),
    ];

    // Sampled above the island centre, inside the region's vertical span, so the offshore region
    // only contributes when it has actually spread over the island.
    static readonly Vector3 IslandSample = new(0f, 20f, 0f);

    // -- Palette: one antique-bronze rim for both faces, WC3-style --
    //
    // Overlay controls share the main shader, which consumes albedo as linear and encodes it to
    // display space on the way out (Pipeline.cs: the overlay variant is always built with
    // HDR_CHAIN 0 and the sprite path ends in pow(color, 1/2.2)). Entries are written as the colour
    // that should appear on screen and linearized here, so the numbers stay comparable with the
    // reference art and the ring is no brighter than the painted faces behind it.

    static readonly Season.Basic.Color RimDark = Srgb(30, 22, 14);
    static readonly Season.Basic.Color RimLight = Srgb(176, 138, 74);
    static readonly Season.Basic.Color RimBand = Srgb(64, 46, 28);
    static readonly Season.Basic.Color MarkerRing = Srgb(148, 116, 62);
    static readonly Season.Basic.Color SunCore = Srgb(255, 226, 128);
    static readonly Season.Basic.Color SunGlow = Srgb(255, 168, 48);
    static readonly Season.Basic.Color MoonCore = Srgb(198, 230, 255);
    static readonly Season.Basic.Color MoonGlow = Srgb(86, 158, 255);

    // Weather tints multiply the painted layers, so they are written as the colour the layer should
    // end up as on screen and linearized the same way.
    static readonly Season.Basic.Color RainDay = Srgb(198, 218, 242);
    static readonly Season.Basic.Color RainNight = Srgb(132, 158, 200);
    static readonly Season.Basic.Color SnowDay = Srgb(250, 253, 255);
    static readonly Season.Basic.Color SnowNight = Srgb(198, 216, 240);
    static readonly Season.Basic.Color BoltLight = Srgb(232, 244, 255);

    /// <summary>Display-space sRGB bytes to the linear albedo the 2D pipeline expects.</summary>
    static Season.Basic.Color Srgb(int r, int g, int b) => new(
        MathF.Pow(r / 255f, 2.2f),
        MathF.Pow(g / 255f, 2.2f),
        MathF.Pow(b / 255f, 2.2f));

    /// <summary>Neutral grey at the given display-space level, for dimming a painted layer.</summary>
    static Season.Basic.Color Grey(float level) => new(
        MathF.Pow(Math.Clamp(level, 0f, 1f), 2.2f),
        MathF.Pow(Math.Clamp(level, 0f, 1f), 2.2f),
        MathF.Pow(Math.Clamp(level, 0f, 1f), 2.2f));

    /// <summary>Linear blend between two display-space colours.</summary>
    static Season.Basic.Color Mix(Season.Basic.Color a, Season.Basic.Color b, float t) =>
        new(Vector4.Lerp(a.AsVector4, b.AsVector4, t));

    Shape rimOuter, rimEdge, rimBand;
    Sprite2D skyNight, skyDay, moon, sun, bolt;
    readonly Sprite2D[] cloudBanks = new Sprite2D[CloudBanks.Length];

    // Rain is drawn, not painted. Each drop is RainSegments dots stepping down and slightly left,
    // and its scatter, phase, fall rate and reveal threshold are fixed per index, so the pattern is
    // identical every launch and the density - not the opacity - is what tracks rain strength.
    readonly Shape[] rainDots = new Shape[RainDropCount * RainSegments];
    readonly float[] rainColumn = new float[RainDropCount];
    readonly float[] rainPhase = new float[RainDropCount];
    readonly float[] rainRate = new float[RainDropCount];
    readonly float[] rainThreshold = new float[RainDropCount];
    float _rainTime;

    // Snow follows the same rule: every flake keeps a fixed column, phase, fall rate, sway and
    // reveal threshold, so a drizzle of flakes and a blizzard differ only in how many are alive.
    readonly Shape[] snowDots = new Shape[SnowFlakeCount * SnowDotsPerFlake];
    readonly float[] snowColumn = new float[SnowFlakeCount];
    readonly float[] snowPhase = new float[SnowFlakeCount];
    readonly float[] snowRate = new float[SnowFlakeCount];
    readonly float[] snowSwayScale = new float[SnowFlakeCount];
    readonly float[] snowThreshold = new float[SnowFlakeCount];
    float _snowTime;

    // One entry per marker, in clock order starting at 12 o'clock.
    readonly Shape[] halo = new Shape[MarkerCount];
    readonly Shape[] ring = new Shape[MarkerCount];
    readonly Shape[] hole = new Shape[MarkerCount];
    readonly Shape[] core = new Shape[MarkerCount];

    // 1 = day face fully visible, 0 = night face. Eased so the 06:00/18:00 swap is a cross-fade
    // rather than a cut, and seeded on the first frame so opening at noon does not show a fade in.
    float _dayBlend;
    bool _blendSeeded;

    // Advances only while the island carries a charge; drives the double flash.
    float _flashTime;

    internal Clock()
    {
        RenderDomain = Season.Controls.RenderDomain.Overlay;

        rimOuter = new Shape { Type = ShapeType.Circle, Color = RimDark, Layer = 0 };
        rimEdge = new Shape { Type = ShapeType.Circle, Color = RimLight, Layer = 1 };
        rimBand = new Shape { Type = ShapeType.Circle, Color = RimBand, Layer = 2 };
        AddControl(rimOuter);
        AddControl(rimEdge);
        AddControl(rimBand);

        // Every face layer shares the same rect and scales with it. The skies carry no sun, moon or
        // cloud of their own, so weather can hide or reveal the symbols without repainting anything.
        skyNight = Sprite("Assets/ClockSkyNight.png", LayerSkyNight);
        skyDay = Sprite("Assets/ClockSkyDay.png", LayerSkyDay);
        moon = Sprite("Assets/ClockMoon.png", LayerMoon);
        sun = Sprite("Assets/ClockSun.png", LayerSun);
        for (var i = 0; i < cloudBanks.Length; i++) cloudBanks[i] = Sprite("Assets/ClockCloud.png", LayerCloud);
        bolt = Sprite("Assets/ClockBolt.png", LayerBolt);

        for (var i = 0; i < RainDropCount; i++)
        {
            rainColumn[i] = Hash(i, 1);
            rainPhase[i] = Hash(i, 2);
            rainRate[i] = 0.85f + 0.45f * Hash(i, 3);
            rainThreshold[i] = Hash(i, 4);

            for (var k = 0; k < RainSegments; k++)
            {
                var dot = new Shape { Type = ShapeType.Circle, Color = Season.Basic.Colors.Transparent, Layer = LayerRain };
                AddControl(dot);
                rainDots[i * RainSegments + k] = dot;
            }
        }

        for (var i = 0; i < SnowFlakeCount; i++)
        {
            snowColumn[i] = Hash(i, 5);
            snowPhase[i] = Hash(i, 6);
            snowRate[i] = 0.70f + 0.60f * Hash(i, 7);
            snowSwayScale[i] = 0.70f + 0.60f * Hash(i, 8);
            snowThreshold[i] = Hash(i, 9);

            for (var k = 0; k < SnowDotsPerFlake; k++)
            {
                var dot = new Shape { Type = ShapeType.Circle, Color = Season.Basic.Colors.Transparent, Layer = LayerSnow };
                AddControl(dot);
                snowDots[i * SnowDotsPerFlake + k] = dot;
            }
        }

        for (var i = 0; i < MarkerCount; i++)
        {
            // Halos sit under the rings so the glow spreads outward and never washes out the rim.
            halo[i] = new Shape { Type = ShapeType.Circle, Color = Season.Basic.Colors.Transparent, Layer = LayerHalo };
            ring[i] = new Shape { Type = ShapeType.Circle, Color = MarkerRing, Layer = LayerRing };
            hole[i] = new Shape { Type = ShapeType.Circle, Color = RimBand, Layer = LayerHole };
            core[i] = new Shape { Type = ShapeType.Circle, Color = Season.Basic.Colors.Transparent, Layer = LayerCore };

            AddControl(halo[i]);
            AddControl(ring[i]);
            AddControl(hole[i]);
            AddControl(core[i]);
        }
    }

    Sprite2D Sprite(string name, int layer)
    {
        var sprite = new Sprite2D { Name = name, Color = Season.Basic.Colors.White, Layer = layer };
        AddControl(sprite);
        return sprite;
    }

    /// <summary>Stable pseudo-random in [0,1): the drop pattern must survive a restart unchanged.</summary>
    static float Hash(int index, int salt)
    {
        var value = MathF.Sin(index * 12.9898f + salt * 78.233f) * 43758.5453f;
        return value - MathF.Floor(value);
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        var app = App.Instance;
        if (app == null) return result;

        // -- Geometry: centred in the free gap between the Settings button and the mirror stack --

        float screenWidth = app.ExtendResolution.X;
        float mirrorLeft = app.mirrors?.LeftEdge ?? 0f;
        if (mirrorLeft <= SettingRight + DiameterMin)
        {
            // Mirrors declined by the backend (or not built yet): fall back to the same arithmetic
            // RearViews.Layout uses, so the dial never collapses onto the Settings button.
            var total = RearViews.MirrorWidth + RearViews.Gap + RearViews.MinimapSize;
            mirrorLeft = MathF.Max(SettingRight + DiameterMin + GapPadding * 2f, (screenWidth - total) * 0.5f);
        }

        var diameter = MathF.Round(Math.Clamp(mirrorLeft - SettingRight - GapPadding * 2f, DiameterMin, DiameterMax));
        var faceDiameter = MathF.Max(1f, diameter - FaceInset);
        var centerX = (SettingRight + mirrorLeft) * 0.5f;
        var left = centerX - diameter * 0.5f;
        var top = SettingCenterY - diameter * 0.5f;
        var faceLeft = centerX - faceDiameter * 0.5f;
        var faceTop = top + (diameter - faceDiameter) * 0.5f;

        rimOuter.Update(time, alpha: 1f, posX: left, posY: top, width: diameter, height: diameter);
        rimEdge.Update(time, alpha: 1f, posX: left + 1.5f, posY: top + 1.5f, width: diameter - 3f, height: diameter - 3f);
        rimBand.Update(time, alpha: 1f, posX: left + 4.5f, posY: top + 4.5f, width: diameter - 9f, height: diameter - 9f);

        // -- Which face --

        float hour = app.HourOfDay;                       // 0..24, 0 and 24 are midnight
        bool isDay = hour >= DayStartHour && hour < DayEndHour;

        if (!_blendSeeded)
        {
            _dayBlend = isDay ? 1f : 0f;
            _blendSeeded = true;
        }
        else
        {
            var step = time / FaceFadeSeconds;
            _dayBlend = isDay
                ? MathF.Min(1f, _dayBlend + step)
                : MathF.Max(0f, _dayBlend - step);
        }

        // Night first, day over it. Both skies share the face rect, and the two symbols cross-fade
        // with them; a zero-alpha layer just costs one draw call.
        skyNight.Update(time, alpha: 1f - _dayBlend, posX: faceLeft, posY: faceTop, width: faceDiameter, height: faceDiameter);
        skyDay.Update(time, alpha: _dayBlend, posX: faceLeft, posY: faceTop, width: faceDiameter, height: faceDiameter);

        // -- Weather --

        // Strengths, not a preset: the field blends the regions by coverage and has already eased
        // them along the region's transition, so the dial drifts with the sky instead of cutting.
        var sample = app.weather?.Field.Sample(IslandSample) ?? default;
        var cloud = Math.Clamp(sample.Cloud, 0f, 1f);
        var rain = Math.Clamp(sample.Rain, 0f, 1f);
        var snow = Math.Clamp(sample.Snow, 0f, 1f);
        var charge = Math.Clamp(sample.Lightning, 0f, 1f);

        // Overcast has to grey the sky itself, not just the disc in front of the sun: a sky that
        // stays bright blue behind a rain sheet reads as a sunny day with a mark on the glass.
        var skyTint = Grey(1f - 0.35f * cloud - 0.25f * rain);
        skyNight.Color = skyTint;
        skyDay.Color = skyTint;

        var symbolSize = MathF.Round(faceDiameter * SymbolSize);
        var symbolLeft = centerX - symbolSize * 0.5f;
        var symbolTop = SettingCenterY - symbolSize * 0.5f;

        // Only a fair sky keeps its sun or moon: cloudy still shows it with a tuft across the top, but
        // once the lid closes (overcast and every rain or storm state) the symbol goes out entirely rather
        // than glowing through the cloud.
        var symbolAlpha = Math.Clamp((0.55f - cloud) / 0.20f, 0f, 1f);
        var symbolTint = Grey(1f - 0.35f * cloud);
        moon.Color = symbolTint;
        sun.Color = symbolTint;
        moon.Update(time, alpha: symbolAlpha * (1f - _dayBlend), posX: symbolLeft, posY: symbolTop, width: symbolSize, height: symbolSize);
        sun.Update(time, alpha: symbolAlpha * _dayBlend, posX: symbolLeft, posY: symbolTop, width: symbolSize, height: symbolSize);

        // -- Cloud --

        // Colour carries the water: overcast greys the tufts and rain darkens them further, while
        // at night the same shapes are lit only by the moon. How many banks stand in the upper half
        // is what separates cloudy from overcast and from every rain state.
        var cloudGrey = Math.Clamp(0.30f * (cloud - 0.30f) / 0.70f + 0.55f * rain, 0f, 0.80f);
        var cloudTint = Grey((1f - cloudGrey) * (0.42f + 0.58f * _dayBlend));

        // A closed sky also grows the banks and slides them down and to the right: overcast and every rain
        // or snow state should fill the dial, while cloudy keeps the smaller fair-weather tuft.
        var overcast = Math.Clamp((cloud - CloudOvercastFrom) / CloudOvercastSpan, 0f, 1f);

        for (var i = 0; i < cloudBanks.Length; i++)
        {
            var (offsetX, offsetY, scale, from) = CloudBanks[i];
            var size = MathF.Round(faceDiameter * (scale + CloudGrow * overcast));
            var cover = Math.Clamp((cloud - from) / CloudFade, 0f, 1f);

            cloudBanks[i].Color = cloudTint;
            cloudBanks[i].Update(time, alpha: cover,
                posX: centerX + faceDiameter * (offsetX + CloudDrift * overcast) - size * 0.5f,
                posY: SettingCenterY + faceDiameter * (offsetY + CloudDown * overcast) - size * 0.5f,
                width: size, height: size);
        }

        // -- Rain --

        // Drawn, not textured: a drop is RainSegments dots stepping down and a little to the left,
        // and rain strength decides how many drops are alive. Density, not opacity, is what says how
        // hard it is raining. Every drop starts under the cloud base and falls to the lower rim, so
        // the cloud band above stays clear of it.
        _rainTime += time;
        var rainTint = Mix(RainNight, RainDay, _dayBlend);
        var dropSize = MathF.Max(1f, MathF.Round(faceDiameter * RainDot));
        var stepDown = faceDiameter * RainSegStep;
        var stepLeft = faceDiameter * RainSegSlant;
        var rainSpan = RainBottom - RainTop;

        for (var i = 0; i < RainDropCount; i++)
        {
            // A drop's reveal threshold is fixed, so a drizzle keeps a thin scatter and a downpour
            // fills the band without any stroke changing its weight.
            var cover = Math.Clamp((rain - rainThreshold[i] * 0.85f) / 0.15f, 0f, 1f);

            var t = rainPhase[i] + _rainTime / RainFallSeconds * rainRate[i];
            t -= MathF.Floor(t);

            // Fade the tail just before the wrap, so drops leave the band instead of blinking out.
            var dropAlpha = cover * Math.Clamp((0.94f - t) / 0.12f, 0f, 1f) * 0.85f;
            var x = centerX + (rainColumn[i] * 2f - 1f) * faceDiameter * RainHalfWidth;
            var y = SettingCenterY + (RainTop + t * rainSpan) * faceDiameter;

            for (var k = 0; k < RainSegments; k++)
            {
                var dot = rainDots[i * RainSegments + k];
                dot.Color = rainTint;
                dot.Update(time, alpha: dropAlpha,
                    posX: x - k * stepLeft - dropSize * 0.5f,
                    posY: y + k * stepDown - dropSize * 0.5f,
                    width: dropSize, height: dropSize);
            }
        }

        // -- Snow --

        // Drawn with the same rule, but a flake is a five-dot star rather than a leaned stroke, it
        // falls slowly and swings side to side instead of dropping straight. Snow strength, like
        // rain strength, only decides how many flakes are alive - never how big or how opaque.
        _snowTime += time;
        var snowTint = Mix(SnowNight, SnowDay, _dayBlend);
        var flakeCore = MathF.Max(1f, MathF.Round(faceDiameter * SnowCore));
        var flakeArm = MathF.Max(1f, MathF.Round(faceDiameter * SnowArm));
        var armReach = faceDiameter * SnowReach;

        for (var i = 0; i < SnowFlakeCount; i++)
        {
            var cover = Math.Clamp((snow - snowThreshold[i] * 0.50f) / 0.12f, 0f, 1f);

            var t = snowPhase[i] + _snowTime / SnowFallSeconds * snowRate[i];
            t -= MathF.Floor(t);

            // Fade the tail just before the wrap, so flakes leave the band instead of blinking out.
            var flakeAlpha = cover * Math.Clamp((0.94f - t) / 0.12f, 0f, 1f);
            var sway = MathF.Sin((_snowTime / SnowSwaySeconds * snowSwayScale[i] + snowPhase[i]) * MathF.PI * 2f)
                * faceDiameter * SnowSway;
            var flakeX = centerX + (snowColumn[i] * 2f - 1f) * faceDiameter * SnowHalfWidth + sway;
            var flakeY = SettingCenterY + (RainTop + t * rainSpan) * faceDiameter;

            for (var k = 0; k < SnowDotsPerFlake; k++)
            {
                var (armX, armY, isCore) = SnowShape[k];
                var dotSize = isCore ? flakeCore : flakeArm;
                var flake = snowDots[i * SnowDotsPerFlake + k];
                flake.Color = snowTint;
                flake.Update(time, alpha: flakeAlpha,
                    posX: flakeX + armX * armReach - dotSize * 0.5f,
                    posY: flakeY + armY * armReach - dotSize * 0.5f,
                    width: dotSize, height: dotSize);
            }
        }

        // -- Lightning --

        // A charged island fires a short double flash on a slow cycle, so the bolt reads as a strike
        // and not as a sticker. It hangs under the cloud base with the rain; water is not required,
        // which is exactly what separates thunder (bolt, no water) from thunderstorm (bolt and water).
        _flashTime += time;
        var boltAlpha = 0f;
        if (charge > 0.02f)
        {
            var phase = _flashTime % BoltFlashPeriod;
            boltAlpha = (phase < 0.06f ? 1f : phase < 0.10f ? 0.12f : phase < 0.17f ? 0.75f : 0f) * charge;
        }

        var boltSize = MathF.Round(faceDiameter * BoltSize);
        bolt.Color = BoltLight;
        bolt.Update(time, alpha: boltAlpha,
            posX: centerX + faceDiameter * BoltOffsetX - boltSize * 0.5f,
            posY: SettingCenterY + faceDiameter * BoltOffsetY - boltSize * 0.5f,
            width: boltSize, height: boltSize);

        // -- Hour markers --

        // The lit count is the hour the dial is on, so the ring reads as a clock rather than as a
        // progress bar through the half-day. Folding modulo twelve puts 12:xx and 00:xx on the
        // twelve o'clock marker - all twelve lit - and restarts the count at one for 13:xx and 01:xx.
        var clockHour = (int)hour % 12;
        var lit = clockHour == 0 ? MarkerCount : clockHour;

        var markerSize = MathF.Round(MarkerSize * diameter / DiameterMax);
        var holeSize = MathF.Round(markerSize * MarkerHole);
        var coreSize = MathF.Round(markerSize * MarkerCore);
        var glowSize = MathF.Round(markerSize * MarkerGlow);
        var ringRadius = diameter * MarkerRadius;

        var glowColor = isDay ? SunGlow : MoonGlow;
        var coreColor = isDay ? SunCore : MoonCore;

        for (var i = 0; i < MarkerCount; i++)
        {
            // Index 0 at twelve o'clock, then clockwise: screen Y grows downward, so +Y is the
            // clockwise direction.
            var angle = -MathF.PI * 0.5f + i * MathF.PI * 2f / MarkerCount;
            var x = centerX + MathF.Cos(angle) * ringRadius;
            var y = SettingCenterY + MathF.Sin(angle) * ringRadius;
            var markerLeft = x - markerSize * 0.5f;
            var markerTop = y - markerSize * 0.5f;

            bool on = i < lit;

            halo[i].Color = glowColor;
            halo[i].Update(time, alpha: on ? 0.32f : 0f,
                posX: x - glowSize * 0.5f, posY: y - glowSize * 0.5f, width: glowSize, height: glowSize);

            ring[i].Update(time, alpha: 1f, posX: markerLeft, posY: markerTop, width: markerSize, height: markerSize);

            hole[i].Update(time, alpha: 1f,
                posX: x - holeSize * 0.5f, posY: y - holeSize * 0.5f, width: holeSize, height: holeSize);

            core[i].Color = coreColor;
            core[i].Update(time, alpha: on ? 1f : 0f,
                posX: x - coreSize * 0.5f, posY: y - coreSize * 0.5f, width: coreSize, height: coreSize);
        }

        return result;
    }
}
