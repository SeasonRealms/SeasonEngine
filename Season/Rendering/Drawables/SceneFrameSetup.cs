// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Drawables;

/// <summary>
/// The per-frame lighting choreography that every backend performs in the same order. It was written three times
/// with different comments and no differences in behaviour, which is exactly the kind of copy that drifts: a new
/// shared lighting input gets added to two backends and silently not to the third.
///
/// What stays in the backends is everything that touches a backend resource — binding the radiance cube, DDGI
/// atlases, cloud noise, the AP 3D LUT, and writing the UBO/CB. Those are per-API by construction.
/// </summary>
internal static class SceneFrameSetup
{
    /// <summary>
    /// Runs the 1-5 CPU shadow-matrix chain: reset the per-frame state, compute the sun and, when configured, the
    /// spotlight, then write the results back into the lighting block. Must run after the camera is prepared and
    /// before the lighting block is uploaded.
    ///
    /// Shadow sources are selected by light indices held in Params0.Z (directional) and Params0.W (spot), which
    /// the authoring layer bakes; the light-type test happens only here. When shadows are disabled or no index is
    /// active, nothing is written and the shader sees the zeros that mean "no shadow".
    /// </summary>
    internal static void ApplyShadowChain(ref SceneLightParams lightParams, Camera3D viewCamera)
    {
        if (!RenderQuality.Current.ShadowsEnabled)
            return;

        CascadedShadow.BeginFrame();

        int dirIdx = (int)lightParams.Params0.Z;
        if (dirIdx >= 0 && dirIdx < lightParams.LightCount)
        {
            var dirType = lightParams.Lights[dirIdx].DirType;
            CascadedShadow.ComputeSun(viewCamera, new Vector3(dirType.X, dirType.Y, dirType.Z));
        }

        int spotIdx = (int)lightParams.Params0.W;
        if (spotIdx >= 0 && spotIdx < lightParams.LightCount
            && lightParams.Lights[spotIdx].DirType.W == GpuLight.TypeSpot)
            CascadedShadow.ComputeSpot(in lightParams.Lights[spotIdx]);

        // Local point-light cube shadows, consumed only by the fog's injection kernel. Selected and ranked against the fog
        // volumes rather than the camera, so the matrices hold still while the eye moves - the same reasoning that anchors
        // the far map to the medium. Nothing here reaches the lighting UBO, so a frame with no fog pays nothing.
        CascadedShadow.ComputeLocal(in lightParams);

        CascadedShadow.Apply(ref lightParams);
    }

    /// <summary>
    /// Fills in the lighting-block fields that are computed rather than authored: the exposure injection, the
    /// velocity reconstruction parameters, the environment, DDGI, and procedural-sky inputs.
    ///
    /// This must be the only place exposure reaches Params0.Y. Writing the block directly elsewhere would leave
    /// the field at zero and let the text path's inverse-ACES compensation divide by 1e-4 and saturate text to
    /// white (RenderQuality contract 5).
    ///
    /// VelocityParams zw is the inverse screen size, taken from the device resolution so it matches the render
    /// target actually being written; xy is the current frame's jitter in NDC, which stays zero when motion
    /// vectors are off. Writing it unconditionally is harmless because shaders built without VELOCITY_OUTPUT
    /// never read the field.
    /// </summary>
    internal static void PrepareLighting(ref SceneLightParams lightParams, float hdrExposure)
    {
        lightParams.Params0.Y = hdrExposure;

        var res = DeviceServices.BaseApp.DeviceResolution;
        var jitter = RenderView.Current.JitterNdc;
        lightParams.VelocityParams = new Vector4(
            jitter.X, jitter.Y,
            res.X > 0 ? 1f / res.X : 0f,
            res.Y > 0 ? 1f / res.Y : 0f);

        // 1-7 contract clause 4. When SceneEnvironment is null, EnvParams keeps its zeros and the shader falls
        // back per pixel to the 1-2 constant ambient term.
        DeviceServices.BaseApp.SceneEnvironment?.Apply(ref lightParams);

        // 2-4 clause 10: writes nothing when the probes are not ready, so consumers fall back on their own.
        Effects.DdgiEffect.Apply(ref lightParams);

        // 2-5 step B (b11). On the StaticCube tier the whole path returns early, leaving SkyParams0..3 at zero so
        // the pixel-shader gate skyParams0.w > 0 stays false with no residue.
        SkyLighting.Apply(ref lightParams);
    }
}
