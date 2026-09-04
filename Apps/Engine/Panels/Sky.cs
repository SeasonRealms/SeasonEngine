// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Engine.Panels;

/// <summary>
/// Purely visual skybox panel after the 2026-08 split. It keeps the six skybox faces, material selection,
/// fallback tinting, and the sun and moon marker spheres. All lighting work now lives in <see cref="CelestialLighting"/>,
/// including light sources, environment light, weather, and SH9 projection. Each frame this panel only reads the cached
/// day-night results from CelestialLighting and no longer evaluates DayNightCycle on its own.
/// CelestialLighting.Update must therefore run before Sky.Update inside App.Update.
/// </summary>
internal class Sky : Panel
{
    CelestialLighting _celestial;

    Sprite2D background;

    Mesh3D skybox;

    // Since Step C, these only exist in the StaticCube fallback mode. The procedural mode uses analytic sun and moon disks in the shader instead.
    Season.Controls.Model? sun, moon;

    // One-time size guards for the normalized sun and moon marker spheres, after bounds become available.
    bool sunSized, moonSized;

    // Marker-orbit radius for the sun and moon. The orbit center follows the camera, just like the skybox,
    // so these infinitely distant bodies keep a stable apparent direction under camera translation.
    // The radius must stay outside the mountain ring but still inside the skybox, and 200m places
    // the rise and set points beyond the mountains while keeping the bodies within the box.
    const float SkyOrbitRadius = 200f;      // Camera-relative marker-orbit radius, outside the mountain ring and inside the skybox.

    // Real angular-size calibration for the fallback marker spheres. The normalized size is the diameter subtended
    // by the authoritative Atmosphere angular radius at SkyOrbitRadius, so the fallback markers match the analytic disks
    // in the procedural shader. The older heuristic made them far too large, which could cover the analytic sun disk entirely.
    static float MarkerSize(float angularRadiusDeg)
        => 2f * SkyOrbitRadius * MathF.Tan(angularRadiusDeg * MathF.PI / 180f);

    // Fallback skybox tinting: brightness varies with solar or lunar elevation and color shifts between a warm day tint
    // and a cool moonlit night tint, using the same night-brightness control as the rest of the lighting system.
    internal readonly Vector3 SunSkyTint = new Vector3(1f, 0.98f, 0.92f);
    internal readonly Vector3 MoonSkyTint = new Vector3(0.5f, 0.6f, 0.9f);

    public Sky(CelestialLighting celestial)
    {
        _celestial = celestial;

        // The skybox must be added and drawn first. BaseApp.Draw renders controls in insertion order, and each control
        // performs its own Opaque -> Fade -> Transparent passes. If the skybox came later, blended objects would see only
        // the clear color behind them. Face corners are defined from the camera's view, but winding still matches the normal
        // cube convention. At runtime the box follows the camera so it keeps the usual "infinitely far" behavior.
        skybox = new Mesh3D()
        {
            Name = "Assets/Skybox",
            // Unified placement convention: geometry is a unit cube centered at the origin and sizing is controlled externally.
            // A side length of 900 gives a half-extent of 450, enlarged together with the 2026-08 sea expansion.
            // Since rule 2-2 exempted the skybox into a NoDepth PSO, the box no longer hard-clips distant scenery.
            // The only hard requirement is that the far plane still covers the ray distance to a cube corner.
            Width = 900f,
            Height = 900f,
            Depth = 900f,
            // The skybox is always visible, so keep it exempt from frustum culling.
            CullingEnabled = false,
            // Skybox-like geometry must never cast shadows. This is also the GI-proxy gate, and because the box follows the camera
            // with a +/-450m extent, forgetting this would create a proxy around the whole SDF volume every frame.
            CastShadows = false,
            // Exempt the skybox from GTAO. As real geometry it would otherwise produce dark seams at the cube edges
            // from discontinuous reconstructed normals and horizon searches crossing those edges.
            ExcludeFromAo = true
        };
        BuildSkyboxSurfaces(skybox, _celestial.ProceduralSkyTexture);
        AddControl(skybox);

        // [SkyDebug] Log the construction-time mode snapshot once to help investigate missing stars or disk rendering.
        DeviceServices.BaseApp.AddLog(LogType.Backend,
            $"[SkyDebug] Sample Sky ctor: _skyViewTexture={_celestial.ProceduralSkyTexture ?? "null"} → " +
            (_celestial.IsProceduralSky
                ? "procedural mode (Sky-View LUT on all six faces, analytic disks and star path, no marker spheres)"
                : "StaticCube fallback (cloudtop textures plus marker-sphere sun and moon, no analytic stars)"));

        // Since Step C, the procedural mode retires these marker spheres entirely because the shader already renders
        // analytic sun and moon disks with real angular size, transmittance tint, stars, and moon phases. The fallback
        // StaticCube mode still needs visible celestial markers, so only that branch creates them.
        if (!_celestial.IsProceduralSky)
        {
            sun = new Season.Controls.Model()
            {
                Name = @"Assets/Sun.glb",
                Unlit = true,
                MaterialColor = new System.Numerics.Vector4(1f, 0.85f, 0.4f, 1f),
                CastShadows = false
            };
            AddControl(sun);

            moon = new Season.Controls.Model()
            {
                Name = @"Assets/Sun.glb",
                Unlit = true,
                MaterialColor = new System.Numerics.Vector4(0.75f, 0.85f, 1f, 1f),
                CastShadows = false
            };
            AddControl(moon);
        }
    }

    static Vertex MakeCubeVertex(Vector3 pos, Vector2 uv, Vector3 normal)
    {
        // Any reasonable tangent works here because Mesh3D v1 renderMode=0 does not read the TBN basis.
        return new Vertex
        {
            Position = pos,
            TexCoord = uv,
            Normal = normal,
            Tangent = new Vector4(1, 0, 0, 1),
            Joints = Vector4.Zero,
            Weights = Vector4.Zero,
        };
    }

    /// <summary>
    /// Builds the six skybox faces. Geometry is a unit cube centered at the origin and sized externally to 900,
    /// so the world-space half-extent is 450. Since the skybox uses a NoDepth PSO, the only meaningful geometric
    /// requirement is that the far plane still covers the ray distance to a cube corner.
    ///
    /// Winding stays {2,3,1,2,1,0}, matching a normal cube. The only difference is that face corners are defined
    /// from the camera inside the cube rather than an observer outside it, so the visible faces are still clockwise
    /// in NDC and the hidden faces still become counter-clockwise and get culled.
    ///
    /// In procedural mode all six faces share one Sky-View LUT and the shader derives LUT UVs from the world view
    /// direction, so the per-face vertex UV orientation becomes irrelevant there.
    /// </summary>
    static void BuildSkyboxSurfaces(Mesh3D mesh, string? skyViewTexture)
    {
        const float h = 0.5f;
        const float d = 2f * h;

        AddSkyboxFace(mesh, "Assets/cloudtop_bk.png", skyViewTexture,
            bl: new Vector3(-h, -h, +h), u: new Vector3(+d, 0, 0), v: new Vector3(0, +d, 0));
        AddSkyboxFace(mesh, "Assets/cloudtop_ft.png", skyViewTexture,
            bl: new Vector3(+h, -h, -h), u: new Vector3(-d, 0, 0), v: new Vector3(0, +d, 0));
        AddSkyboxFace(mesh, "Assets/cloudtop_lf.png", skyViewTexture,
            bl: new Vector3(-h, -h, -h), u: new Vector3(0, 0, +d), v: new Vector3(0, +d, 0));
        AddSkyboxFace(mesh, "Assets/cloudtop_rt.png", skyViewTexture,
            bl: new Vector3(+h, -h, +h), u: new Vector3(0, 0, -d), v: new Vector3(0, +d, 0));
        AddSkyboxFace(mesh, "Assets/cloudtop_up.png", skyViewTexture,
            bl: new Vector3(-h, +h, +h), u: new Vector3(+d, 0, 0), v: new Vector3(0, 0, -d));
        AddSkyboxFace(mesh, "Assets/cloudtop_dn.png", skyViewTexture,
            bl: new Vector3(-h, -h, -h), u: new Vector3(+d, 0, 0), v: new Vector3(0, 0, +d));
    }

    static void AddSkyboxFace(Mesh3D mesh, string texPath, string? skyViewTexture, Vector3 bl, Vector3 u, Vector3 v)
    {
        // Inward normal pointing toward the camera side of the box. It is not actually read in renderMode=0.
        var normal = -Vector3.Normalize(Vector3.Cross(u, v));

        // Four corners: BL=(0,1), BR=(1,1), TL=(0,0), TR=(1,0).
        var verts = new Vertex[4];
        verts[0] = MakeCubeVertex(bl, new Vector2(0, 1), normal);
        verts[1] = MakeCubeVertex(bl + u, new Vector2(1, 1), normal);
        verts[2] = MakeCubeVertex(bl + v, new Vector2(0, 0), normal);
        verts[3] = MakeCubeVertex(bl + u + v, new Vector2(1, 0), normal);

        // Same winding as a regular cube: TL->TR->BR and TL->BR->BL.
        // On the visible face that becomes clockwise in NDC, while on the far side it flips and is back-face culled.
        var indices = new ushort[] { 2, 3, 1, 2, 1, 0 };

        mesh.Surfaces.Add(new Surface
        {
            Vertices = verts,
            Indices = indices,
            // Procedural mode binds the same Sky-View LUT to all six faces and ignores per-vertex UVs in the shader.
            // Fallback mode keeps the original per-face cloudtop textures.
            BaseColorTexturePath = skyViewTexture ?? texPath,
            ProceduralSky = skyViewTexture != null,
            BaseColor = Vector4.One,
            Mode = SurfaceBlendMode.Opaque,
        });
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        background?.SetTexture("Assets/GreenWheatFields.jpg", ext: Path.GetExtension(background?.Name).ToLower());
        background?.Update(time: time, alpha: 1f,
            width: (int)DeviceServices.BaseApp.ExtendResolution.X, height: (int)DeviceServices.BaseApp.ExtendResolution.Y);

        // Day-night state comes from CelestialLighting, which must already have updated this frame.
        // Marker spheres orbit around the camera so their apparent direction is driven only by phase, not by camera translation.
        var dayPhase = _celestial.DayPhase;
        var sunPos = App.Instance.CameraPos + Season.Rendering.DayNightCycle.BodyPosition(dayPhase, forMoon: false) * SkyOrbitRadius;
        var moonPos = App.Instance.CameraPos + Season.Rendering.DayNightCycle.BodyPosition(dayPhase, forMoon: true) * SkyOrbitRadius;

        // Make the skybox follow the camera so the camera always remains inside it.
        // In fallback mode, brightness and tint track the day-night cycle through the same source as direct lighting.
        if (skybox != null)
        {
            // Under the unified placement convention, pin the local origin at the camera by compensating with AnchorWorldOffset.
            var skyboxPos = App.Instance.CameraPos + skybox.AnchorWorldOffset;
            skybox.PosX = skyboxPos.X;
            skybox.PosY = skyboxPos.Y;
            skybox.PosZ = skyboxPos.Z;
            // Do not re-tint the procedural mode. The LUT already contains physical radiance for the current day-night state.
            if (!_celestial.IsProceduralSky)
                skybox.ColorTint = Season.Rendering.DayNightCycle.SkyTint(dayPhase, SunSkyTint, MoonSkyTint, _celestial.NightSkyBrightness);
            skybox.Update(time);
        }

        // Marker spheres also use the unified placement convention: pin their local origin at the orbit point
        // and assign size once bounds exist. They only exist in StaticCube fallback mode, so null checks are enough.
        if (sun != null)
        {
            if (!sunSized && sun.LocalSize != Vector3.Zero)
            {
                // Use the true diameter subtended by the atmospheric angular radius at SkyOrbitRadius.
                float sunSize = MarkerSize(Season.Rendering.Atmosphere.SunAngularRadiusDeg);
                sun.Width = sun.LocalSize.X * sun.OriginalScale * sunSize;
                sun.Height = sun.LocalSize.Y * sun.OriginalScale * sunSize;
                sun.Depth = sun.LocalSize.Z * sun.OriginalScale * sunSize;
                sunSized = true;
            }

            var sunAnchorPos = sunPos + sun.AnchorWorldOffset;
            sun.PosX = sunAnchorPos.X;
            sun.PosY = sunAnchorPos.Y;
            sun.PosZ = sunAnchorPos.Z;
            sun.Update(time: time, alpha: 1f);
            sun.Alpha = _celestial.SunUp ? 1f : 0f;
        }

        // The moon marker follows its own tilted arc and is visible only while the moon is above the horizon.
        if (moon != null)
        {
            if (!moonSized && moon.LocalSize != Vector3.Zero)
            {
                float moonSize = MarkerSize(Season.Rendering.Atmosphere.MoonAngularRadiusDeg);
                moon.Width = moon.LocalSize.X * moon.OriginalScale * moonSize;
                moon.Height = moon.LocalSize.Y * moon.OriginalScale * moonSize;
                moon.Depth = moon.LocalSize.Z * moon.OriginalScale * moonSize;
                moonSized = true;
            }

            var moonAnchorPos = moonPos + moon.AnchorWorldOffset;
            moon.PosX = moonAnchorPos.X;
            moon.PosY = moonAnchorPos.Y;
            moon.PosZ = moonAnchorPos.Z;
            moon.Update(time: time, alpha: 1f);
            // Fallback mode has no analytic disk shader for the moon terminator, so phase is represented through overall alpha.
            // Keep a 0.4 minimum so a new moon still leaves a faint silhouette instead of disappearing completely.
            moon.Alpha = _celestial.MoonUp ? 0.4f + 0.6f * _celestial.MoonPhase : 0f;
        }

        return result;
    }
}
