// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Engine.Panels;

internal class StreetLight : Panel
{
    Season.Rendering.LightSource lampLight;

    internal Season.Controls.Model lightsPunctualLamp;

    Season.Controls.Model light;

    // 1-5 ceiling-lamp position, shared by the marker sphere and the spotlight itself.
    // Brightness cycles through the same pattern as Bulbs:
    // off -> 25% -> 50% -> 75% -> 100%.
    // The Lamp button advances the cycle, and Update writes the result into lampLight.
    // It is intentionally decoupled from the outdoor directional light.
    static readonly Vector3 LampPosition = new Vector3(0f, 1.8f, 0f);

    // Ceiling-lamp validation profile for DDGI and SDF diffuse bounce:
    // red light, high intensity, and a narrow cone. See the lampLight registration comments.
    static readonly Vector3 LampLightColor = new Vector3(1f, 0.2f, 0.1f);
    const float LampIntensity = 150f;

    // Semi-transparent alpha used for the bulb marker while lit, so the lamp head remains visible
    // instead of being fully covered by an opaque red sphere. It returns to opaque when turned off.
    const float LightOnAlpha = 0.5f;

    internal readonly float[] lampLevels = { 0f, 0.25f, 0.5f, 0.75f, 1f };

    internal int lampLevelIndex = 4;  // Start fully lit, matching the old lampOn=true baseline.

    // 1-5 brightness cycle for lightsPunctualLamp, the glTF KHR_lights_punctual five-light group:
    // off -> 35% -> 100%.
    // It starts off to match the 1-5 baseline, where this group was previously disabled as a whole.
    internal readonly float[] bulbsLevels = { 0f, 0.35f, 1f };
    internal int bulbsLevelIndex = 0;

    internal StreetLight()
    {
        lightsPunctualLamp = new Season.Controls.Model()
        {
            Name = @"Assets/street_lights.glb",
            PosX = 5,
            PosY = 2.5f,
            PosZ = 20,
            Width = 2,
            Height = 5,
            Depth = 2,
            Rotation = (float)Math.PI
        };
        AddControl(lightsPunctualLamp);

        light = new Season.Controls.Model()
        {
            Name = @"Assets/Sun.glb",
            Unlit = true,
            MaterialColor = new System.Numerics.Vector4(LampLightColor.X, LampLightColor.Y, LampLightColor.Z, 1f),
            CastShadows = false,
            PosX = lightsPunctualLamp.PosX,
            PosY = lightsPunctualLamp.PosY + 1.53f,
            PosZ = lightsPunctualLamp.PosZ,
            Width = 0.5f,
            Height = 0.5f,
            Depth = 0.5f
        };
        AddControl(light);

        lampLight = App.Instance.Lighting.Add(new Season.Rendering.LightSource
        {
            Name = "StreetLight",
            Kind = Season.Rendering.LightKind.Point,
            Color = LampLightColor,
            Intensity = LampIntensity,
            // Emission point equals the center of the bulb marker sphere, which matches the true lamp structure.
            // Point lights do not cast shadows in this engine, because cube shadow maps are outside the 1-5 scope,
            // so the lamp shade cannot self-occlude and there is no need to offset the light from the bulb.
            // The marker sphere itself has CastShadows=false and does not participate in shadowing or GI proxies.
            // If this changes back to Spot in the future, self-shadowing returns and the source must either move below the shade
            // or the lamp body must be excluded from shadow casting.
            Position = new Vector3(light.PosX, light.PosY, light.PosZ),
            Range = 8f,
            // DDGI+SDF diffuse-bounce validation profile: red and brighter.
            // Cone angles are meaningful only for Spot mode and are ignored by Point mode on the GPU,
            // but they are kept here in case this returns to Spot. With inner 12 degrees and outer 26 degrees,
            // the light pool hits only the floor and gives walls zero direct lighting, so any red on the wall
            // must come from indirect diffuse bounce injected through probes.
            InnerConeAngle = 12f * MathF.PI / 180f,
            OuterConeAngle = 26f * MathF.PI / 180f,
            CastShadows = true,
            Priority = 50,
        });
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        // Five-level dimming for the ceiling lamp.
        // Level 0 turns the light fully off by setting IsOpen=false so the slot is released,
        // matching Bulbs level 0. Other levels scale intensity by LampIntensity times the level factor.
        if (lampLight != null)
        {
            lampLight.Intensity = LampIntensity * lampLevels[lampLevelIndex];
            lampLight.IsOpen = lampLevels[lampLevelIndex] > 0f;

            // The marker sphere changes transparency with the lamp state:
            // lit means semi-transparent so the fixture stays visible, and off returns to opaque.
            // The Alpha setter is already gated by Changed, so idle states do not dirty anything extra.
            if (light != null)
                light.Alpha = lampLevels[lampLevelIndex] > 0f ? LightOnAlpha : 1f;
        }

        // The five KHR point lights inside lightsPunctualLamp are controlled by the Bulbs button.
        // At level 0, AppendWorldLights skips them internally so they occupy no slots.
        // They are appended after the baked lighting results.
        if (lightsPunctualLamp != null)
        {
            lightsPunctualLamp.LightIntensityScale = bulbsLevels[bulbsLevelIndex];
            lightsPunctualLamp.AppendWorldLights(ref App.Instance.SceneLights);
        }

        if (lightsPunctualLamp != null)
        {
            // Unified placement convention: pin the local origin at (0, 0.5, 0) plus the room offset.
            // KHR punctual lights follow the model transform, so the position must stay exact.
            //var lampPos = new Vector3(lightsPunctualLamp.PosX, lightsPunctualLamp.PosY, lightsPunctualLamp.PosZ) + lightsPunctualLamp.AnchorWorldOffset;
            //lightsPunctualLamp.PosX = lampPos.X;
            //lightsPunctualLamp.PosY = lampPos.Y;
            //lightsPunctualLamp.PosZ = lampPos.Z;
        }
        lightsPunctualLamp?.Update(time);

        light?.Update(time);

        return result;
    }
}
