// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// 1-2 lighting system: GPU structure for one punctual light, shared by point and spot lights, 64 bytes = 4×vec4.
/// The all-vec4 layout keeps byte-identical offsets across HLSL cbuffer, GLSL std140, MSL constant, and WGSL uniform packing rules.
/// Mixing float3 and scalars is forbidden because MSL constant float3 aligns to 16 bytes and would shift later fields.
/// Field offsets inside the struct are PosRange=0, ColorIntensity=16, DirType=32, and SpotParams=48.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct GpuLight
{
    /// <summary>xyz = world-space position; w = attenuation range. Values &lt;=0 mean infinite range and reduce to pure 1/d² attenuation under the KHR semantic.</summary>
    public System.Numerics.Vector4 PosRange;

    /// <summary>xyz = linear color in 0~1, decoupled from intensity; w = intensity multiplier. Radiance = color × intensity × attenuation.</summary>
    public System.Numerics.Vector4 ColorIntensity;

    /// <summary>xyz = world-space illumination direction, used by spot and directional lights under the KHR node -Z convention.
    /// w = type, where 0=point, 1=spot, and 2=directional, using the TypePoint, TypeSpot, and TypeDirectional constants.</summary>
    public System.Numerics.Vector4 DirType;

    /// <summary>x = cos(innerConeAngle), y = cos(outerConeAngle), giving the smoothstep boundaries of the spot cone. zw are reserved and kept at 0.</summary>
    public System.Numerics.Vector4 SpotParams;

    /// <summary>DirType.W value for point lights.</summary>
    public const float TypePoint = 0f;
    /// <summary>DirType.W value for spot lights.</summary>
    public const float TypeSpot = 1f;
    /// <summary>DirType.W value for directional lights such as the sun or moon, which have no position and no attenuation.
    /// After the unified-lighting refactor, they live in the Lights array and no longer use a separate sun field.</summary>
    public const float TypeDirectional = 2f;

    /// <summary>Constructs a point light: xyz = world position, color = linear color in 0~1, intensity = intensity multiplier, and range&lt;=0 means infinite range with pure 1/d² attenuation.</summary>
    public static GpuLight Point(System.Numerics.Vector3 position, System.Numerics.Vector3 color, float intensity, float range = 0f)
    {
        return new GpuLight
        {
            PosRange = new System.Numerics.Vector4(position, range),
            ColorIntensity = new System.Numerics.Vector4(color, intensity),
            DirType = new System.Numerics.Vector4(0f, 0f, 0f, TypePoint),
        };
    }

    /// <summary>Constructs a spot light: direction = world-space illumination direction following KHR -Z, and cosInner/cosOuter are the cosine values of the cone's smoothstep boundaries.</summary>
    public static GpuLight Spot(System.Numerics.Vector3 position, System.Numerics.Vector3 direction, System.Numerics.Vector3 color, float intensity, float range, float cosInner, float cosOuter)
    {
        return new GpuLight
        {
            PosRange = new System.Numerics.Vector4(position, range),
            ColorIntensity = new System.Numerics.Vector4(color, intensity),
            DirType = new System.Numerics.Vector4(direction, TypeSpot),
            SpotParams = new System.Numerics.Vector4(cosInner, cosOuter, 0f, 0f),
        };
    }

    /// <summary>Constructs a directional light: direction = world-space propagation direction pointing toward the lit surface, with no position and no attenuation. Radiance = color × intensity.</summary>
    public static GpuLight Directional(System.Numerics.Vector3 direction, System.Numerics.Vector3 color, float intensity)
    {
        return new GpuLight
        {
            DirType = new System.Numerics.Vector4(direction, TypeDirectional),
            ColorIntensity = new System.Numerics.Vector4(color, intensity),
        };
    }
}

/// <summary>
/// Inline array of GpuLight with <see cref="SceneLightParams.MaxLights"/> elements and 64-byte stride,
/// keeping SceneLightParams fully blittable so it can be written to the UBO in one Unsafe.Write block.
/// </summary>
[InlineArray(SceneLightParams.MaxLights)]
public struct GpuLightArray
{
    private GpuLight _element0;
}

/// <summary>
/// 1-5: inline array of shadow matrices, 4 × Matrix4x4 with 64-byte stride, storing CSM cascade light-space ViewProj matrices.
/// The layout always keeps 4 slots regardless of the active cascade count, and only the first int(ShadowParams0.Y) entries are valid.
/// </summary>
[InlineArray(4)]
public struct ShadowMatrixArray
{
    private System.Numerics.Matrix4x4 _element0;
}

/// <summary>
/// 1-7: inline array of SH9 irradiance coefficients, 9 × Vector4 with 16-byte stride, holding environment diffuse terms with xyz=RGB and w reserved as 0.
/// The layout always keeps 9 slots for l=0..2, and the CPU has already pre-multiplied convolution coefficients A_l so shaders only perform a 9-term linear combination.
/// </summary>
[InlineArray(9)]
public struct Sh9Array
{
    private System.Numerics.Vector4 _element0;
}

/// <summary>
/// 2-5 Step C: inline array of cloud-layer parameters, <see cref="SkyState.MaxLayers"/> × Vector4 with 16-byte stride.
/// The layout always keeps 3 slots, just like <see cref="ShadowMatrixArray"/>, regardless of the actual layer count.
/// Only the first int(CloudParams0.W) layers are valid.
/// <see cref="SceneLightParams.CloudLayerA"/> and <see cref="SceneLightParams.CloudLayerB"/> share this type, and matching indices describe the same layer.
/// </summary>
[InlineArray(SkyState.MaxLayers)]
public struct CloudLayerArray
{
    private System.Numerics.Vector4 _element0;
}

/// <summary>
/// 1-2 lighting system: C#-side mirror of the main-pipeline lighting UBO, with contract layout defined by the 1-2, 1-5, and 1-7 sections in the RenderQuality header.
/// The structure uses only vec4 and mat4 layout and has a total size of 1376 bytes
/// (=48 + 8×64 + 4×64 + 64 + 4×16 + 16 + 9×16 + 3×16 + 5×16 + 9×16).
/// The SceneLights UBO declared in shaders on all four backends must match it byte for byte.
/// Byte offsets are:
/// CameraPos=0, Ambient=16, Params0=32, Lights[i]=48+64i,
/// CascadeViewProj[i]=560+64i, SpotShadowViewProj=816, CascadeSplits=880, ShadowParams0=896, ShadowParams1=912,
/// VelocityParams=928, EnvParams=944, IrradianceSH9[i]=960+16i, GiParams0=1104, GiParams1=1120, GiParams2=1136,
/// SkyParams0=1152, SkyParams1=1168, SkyParams2=1184, SkyParams3=1200, SkyParams4=1216,
/// CloudLayerA[i]=1232+16i, CloudLayerB[i]=1280+16i, CloudParams0=1328, CloudParams1=1344, ApParams0=1360.
/// UBO/CB sizes on all backends follow Unsafe.SizeOf&lt;SceneLightParams&gt;() automatically.
/// The only hardcoded external copy is SCENE_LIGHT_BYTES in the web file `js/seasonWebGPU.js`; if its length differs, updates are silently discarded, so layout changes must update it as well.
/// Shader-side struct declarations may be shorter than this struct, because unread trailing fields are harmless, but they must never be longer.
/// Directional lights have already been merged into the <see cref="Lights"/> array with DirType.w = <see cref="GpuLight.TypeDirectional"/>, so there is no separate sun field anymore.
/// All four backends now keep one unified lighting loop in shader code, dispatching directional, point, and spot lights by DirType.w, and all lights share the same <see cref="MaxLights"/> limit.
/// hdrExposure lives in Params0.Y at byte offset 36 and is injected only from each backend's SetLighting path, so App-side writes are ineffective because they are overwritten.
/// The 1-5 shadow fields starting at offset 560 are written only by Season.Rendering.CascadedShadow.Apply.
/// Matrices are stored in raw System.Numerics row-major memory order, exactly as they appear in memory.
/// This struct is written as one block into the UBO, so there is no CPU-side opportunity to transpose.
/// Each backend's shader declaration and multiplication order must adapt accordingly so world→light-space clip transforms match the CPU-side pos·M result.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct SceneLightParams
{
    /// <summary>Maximum number of lights. Directional, point, and spot lights all share this same limit.
    /// It stays at the conservative 1-2 value until Forward+ or clustered lighting is introduced.
    /// The authoring layer, Season.Rendering.SceneLighting, can hold unlimited lights and trims them into this limit during Bake according to priority.</summary>
    public const int MaxLights = 8;

    /// <summary>Offset 0: xyz = camera world position; w reserved.</summary>
    public System.Numerics.Vector4 CameraPos;

    /// <summary>Offset 16: xyz = ambient light color; w = intensity. ambient = color × intensity × albedo × ao, replacing the old hardcoded shader value 0.5.</summary>
    public System.Numerics.Vector4 Ambient;

    /// <summary>Offset 32: x = lightCount, stored as a float integer and recovered with int() in shaders;
    /// y = hdrExposure, injected every frame from Device.HdrExposure by SetLighting;
    /// z = directionalIndex, the index in Lights of the directional light casting CSM shadows this frame, or -1 if none;
    /// w = spotShadowIndex, the index of the spotlight casting the 2D shadow map this frame, or -1 if none.</summary>
    public System.Numerics.Vector4 Params0;

    /// <summary>Offset 48: unified light array. Directional, point, and spot lights all live here with 64-byte stride, and only the first int(Params0.X) entries are valid.</summary>
    public GpuLightArray Lights;

    /// <summary>Offset 560: 1-5 CSM cascade light-space ViewProj[4], transforming world to light-space clip in raw row-major order.
    /// Only the first int(ShadowParams0.Y) entries are valid, and they are written by CascadedShadow.Apply.</summary>
    public ShadowMatrixArray CascadeViewProj;

    /// <summary>Offset 816: 1-5 spotlight shadow-map light-space ViewProj, using perspective projection and corresponding to atlas slot 3.</summary>
    public System.Numerics.Matrix4x4 SpotShadowViewProj;

    /// <summary>Offset 880: view-space far bounds of the cascade splits, where x/y/z are the far bounds of cascades 0/1/2 and w is the maximum shadow distance.
    /// The shader selects cascades by comparing fragment view-space depth in order, and samples no shadow if depth exceeds w.</summary>
    public System.Numerics.Vector4 CascadeSplits;

    /// <summary>Offset 896: x = directional-shadow enable flag, active when &gt;0.5; y = cascadeCount, stored as a float integer;
    /// z = 1/shadowAtlasSize, the texel size used as the PCF step baseline; w reserved as 0.
    /// All zeros means shadows are fully disabled.</summary>
    public System.Numerics.Vector4 ShadowParams0;

    /// <summary>Offset 912: x = spotlight-shadow enable flag, active when &gt;0.5 and applied to the spotlight referenced by Params0.W, see the 1-5 clauses in RenderQuality;
    /// y = shadow strength in 0~1, where 1 means direct light drops fully to zero under complete occlusion; zw reserved as 0.</summary>
    public System.Numerics.Vector4 ShadowParams1;

    /// <summary>Offset 928, 2-3 contract clause 6: xy = subpixel jitter of the current frame in NDC units, used for de-jittering in the pixel shader;
    /// z = 1/screenWidth and w = 1/screenHeight, used to reconstruct NDC from SV_Position.
    /// Written once per frame only by each backend's SetLighting, following the same rule as hdrExposure, so App-side writes are ineffective.
    /// All zeros means non-jittered rendering, where velocity remains correct but de-jitter compensation is absent.</summary>
    public System.Numerics.Vector4 VelocityParams;

    /// <summary>Offset 944, 1-7 contract clause 4:
    /// x = specular reflection intensity multiplier; y = environment diffuse intensity multiplier;
    /// z = diffuse switch, using IrradianceSH9 when &gt;0.5 and otherwise using constant Ambient, strictly one-or-the-other and never additive;
    /// w = specular switch, enabling the radiance-cube LOD0 specular term when &gt;0.5.
    /// Injected only from each backend's SetLighting path through Season.Rendering.EnvironmentMap.Apply, following the same rule as hdrExposure.
    /// App-side writes are ineffective.
    /// All zeros means a complete fallback to the 1-2 constant ambient behavior.</summary>
    public System.Numerics.Vector4 EnvParams;

    /// <summary>Offset 960, 1-7 contract clause 7: SH9 environment irradiance with xyz=RGB and w reserved as 0.
    /// Convolution coefficients A_l are already pre-multiplied on the CPU, so shaders perform only the 9-term linear combination.
    /// Valid only when EnvParams.Z &gt; 0.5.</summary>
    public Sh9Array IrradianceSH9;

    /// <summary>Offset 1104, 2-4 DDGI clause 10: xyz = world-space corner of the probe grid box, probeGridMin; w = probe spacing.
    /// Injected only from each backend's SetLighting path through Season.Rendering.Effects.DdgiEffect.Apply, following the same hdrExposure rule.
    /// App-side writes are ineffective.
    /// All zeros means DDGI is absent and consumers fall back to 1-7 or 1-2 behavior.</summary>
    public System.Numerics.Vector4 GiParams0;

    /// <summary>Offset 1120, 2-4 DDGI: xyz = probe-grid dimensions gridX/gridY/gridZ, stored as float integers and recovered by int() in shaders;
    /// w = GiIntensity, the diffuse indirect-light multiplier on the consumer side, where 0 means the A-side falls back to the old image.
    /// Atlas pixel size is derived in the shader from these dimensions, with atlasW=gridX·gridZ·8, atlasH=gridY·8, tile=8, inner core=6, and border=1 pixel.</summary>
    public System.Numerics.Vector4 GiParams1;

    /// <summary>Offset 1136, 2-4 DDGI:
    /// x = normalBias for probe sampling;
    /// y = Chebyshev visibility switch, enabled when &gt;0.5. It is a runtime knob, on by default, and injected every frame by Apply from GiChebyshevOcclusion;
    /// z = atlasReady, meaning the real probe atlas is bound this frame when &gt;0.5, otherwise a fallback is bound;
    /// w reserved as 0.
    /// Consumer-side selection is a three-way gate:
    /// DDGI_ENABLED &amp;&amp; GiParams2.z&gt;0.5 &amp;&amp; GiParams1.w&gt;0 ? probe : (EnvParams.z&gt;0.5 ? SH9 : Ambient).</summary>
    public System.Numerics.Vector4 GiParams2;

    /// <summary>Offset 1152, 2-5 Step B (b11), for analytic celestial disks:
    /// xyz = <c>Atmosphere.SunDirection</c>, the unit vector **from the observer toward the sun**;
    /// w = cos(solar angular radius).
    /// Injected only from each backend's SetLighting path through <c>Season.Rendering.SkyLighting.Apply</c>, following the same rule as hdrExposure.
    /// App-side writes are ineffective.
    /// **All zeros means the current mode is not procedural sky**. The only consumer-side gate is w &gt; 0, and a real angular radius has cos ≈ 0.99999, so it can never be 0 in valid data.
    /// This does not reuse directional-light data from <see cref="Lights"/> because <c>Params0.Z</c>, directionalIndex, can become -1 during the sun/moon handoff once intensity is filtered down to zero.
    /// After b9, celestial intensity fades continuously through transmittance, so the handoff would necessarily lose the direction for one frame and make the solar disk blink out.</summary>
    public System.Numerics.Vector4 SkyParams0;

    /// <summary>Offset 1168:
    /// xyz = linear radiance of the solar disk, computed as <c>Atmosphere.SunDiskRadiance</c> × <c>SunColor</c> × **mean in-disk transmittance**.
    /// This uses the same T value that <c>Sky.ApplyBodyTransmittance</c> feeds to direct lighting, so the sun in the sky and direct illumination on the ground fade out together and at the same speed.
    /// w = star-field radiance, computed as <c>Atmosphere.StarRadiance</c> times twilight visibility derived from <c>StarVisibilityTwilightDeg</c>, where 0 means stars are fully hidden.</summary>
    public System.Numerics.Vector4 SkyParams1;

    /// <summary>Offset 1184:
    /// xyz = <c>Atmosphere.MoonDirection</c>, following the same convention as <see cref="SkyParams0"/>;
    /// w = cos(lunar angular radius).
    /// Lunar phase needs no extra parameters.
    /// The moon is lit by the sun, and the spherical normal of a point on the disk is analytically derived from xyz and the view direction.
    /// The light/dark test is <c>dot(normal, sunDirection) &gt; 0</c>, so phase is derived entirely from this field together with <see cref="SkyParams0"/> and evolves automatically over the day-night cycle.</summary>
    public System.Numerics.Vector4 SkyParams2;

    /// <summary>Offset 1200:
    /// xyz = linear radiance of the lunar disk, following the same convention as <see cref="SkyParams1"/>.xyz and then multiplied in shader by the phase mask;
    /// w = <c>Atmosphere.StarRotation</c>, the star-field rotation angle in radians around the celestial-pole axis stored in <see cref="SkyParams4"/>.xyz.</summary>
    public System.Numerics.Vector4 SkyParams3;

    /// <summary>Offset 1216, 2-5 Step C:
    /// xyz = the **celestial-pole axis** of star-field diurnal motion, from <c>Atmosphere.StarPoleAxis</c>, as a world-space unit vector already normalized on the CPU and falling back to +Y for zero vectors;
    /// w = observer radius from the planet center in kilometers, equal to <c>GroundRadiusKm + ViewAltitudeKm</c>.
    /// Visible clouds use this for spherical-shell intersections, and the pixel shader uses it to intersect the view ray with each cloud layer.
    /// Hardcoding 6360 here would create a second source of truth.
    /// This field pairs with <see cref="SkyParams3"/>.w, the rotation angle: the shader rotates the view direction **backward** into the star-field's co-rotating frame using Rodrigues rotation before sampling.
    /// It gets its own slot because the first four slots have already spent all 16 components on sun and moon direction plus angular radius, the two disk radiance values, star radiance, and rotation angle.
    /// The axis is inherently 3 components.
    /// In this model it could be derived from a single tilt angle, but that would hard-bind the DayNightCycle arc model into shader code.
    /// All zeros means non-procedural sky, just like the zero-residue argument for SkyParams0 through SkyParams3.</summary>
    public System.Numerics.Vector4 SkyParams4;

    /// <summary>Offset 1232, 2-5 Step C: procedural cloud **layer-shape** group A.
    /// It keeps 3 fixed layer slots, with only the first int(<see cref="CloudParams0"/>.W) layers being valid.
    /// x = <c>AltitudeKm</c>, the cloud-base height above the observer in kilometers;
    /// y = <c>ThicknessKm</c>, geometric thickness in kilometers;
    /// z = <c>Coverage</c> in 0~1;
    /// w = <c>Density</c>, the extinction coefficient in 1/km.
    /// Injected only by each backend's SetLighting through <c>Season.Rendering.SkyLighting.Apply</c>, following the same rule as hdrExposure.
    /// App-side writes are ineffective.</summary>
    public CloudLayerArray CloudLayerA;

    /// <summary>Offset 1280: procedural cloud **layer-shape** group B, aligned by index with <see cref="CloudLayerA"/>.
    /// xy = accumulated horizontal wind offset of the layer in kilometers over world XZ, integrated every frame on the CPU, see <c>SkyLighting.AdvanceClouds</c>;
    /// z = 1/<c>TileKm</c>, the **reciprocal** of the noise tiling period, since the shader needs one multiplication per layer per pixel and precomputing the reciprocal on the CPU saves a per-pixel divide on the GPU;
    /// w = <c>Detail</c>, the high-frequency erosion strength in 0~1.</summary>
    public CloudLayerArray CloudLayerB;

    /// <summary>Offset 1328:
    /// xyz = cloud **scattering color**, computed as <c>SkyState.Albedo</c> multiplied by the CPU-side merged sun and moon cloud-illumination radiance,
    /// each already multiplied by its mean in-disk transmittance at the corresponding altitude.
    /// This is why clouds turn from white to orange-red at sunset and then into moon color at night, all computed once on the CPU.
    /// w = number of valid cloud layers, stored as a float integer and recovered with int() in shaders.
    /// **w = 0 means "no clouds this frame"**, and is the only whole-feature gate on the consumer side, used by both the sky branch and <c>ComputeCloudShadow</c>.
    /// Non-procedural sky, <c>SkyState.Clear</c>, and unavailable noise textures all force this field to zero.</summary>
    public System.Numerics.Vector4 CloudParams0;

    /// <summary>Offset 1344:
    /// x = <c>ShadowStrength</c>, cloud-shadow intensity in 0~1, sharing the same semantic as <see cref="ShadowParams1"/>.y and multiplied with it;
    /// y = <c>PhaseG</c>, the HG anisotropy factor for clouds, controlling only the width of the backlit silver lining;
    /// z = <c>AmbientFloor</c>, lower bound for darkening under cloud bottoms in 0~1;
    /// w = <c>ForwardGain</c>, silver-lining intensity multiplier.</summary>
    public System.Numerics.Vector4 CloudParams1;

    /// <summary>Offset 1360, 2-5 Step E:
    /// x = maximum aerial-perspective distance in kilometers. **A value &gt;0 means the AP LUT is ready this frame**.
    /// This is the only whole-feature gate on the consumer side, and it also acts as the denominator used to unnormalize the z axis, sharing the same value as the bake-side <c>AerialMaxDistanceKm</c>.
    /// y = intensity multiplier, <c>AerialIntensity</c>, where 1 is the physical value and 0 disables the effect.
    /// zw are reserved.
    /// Non-procedural sky, disabled AerialPerspective, and a missing 3D texture all force this field to zero, see <c>SkyLighting.ApplyAerial</c>.</summary>
    public System.Numerics.Vector4 ApParams0;

    /// <summary>Convenience accessor for lightCount, wrapping the float-as-integer semantic of Params0.X and clamping writes into [0, MaxLights].</summary>
    public int LightCount
    {
        readonly get => (int)Params0.X;
        set => Params0.X = Math.Clamp(value, 0, MaxLights);
    }
}
