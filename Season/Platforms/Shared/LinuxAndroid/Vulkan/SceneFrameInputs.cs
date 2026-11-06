// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

/// <summary>
/// 3B/B2: the single resolution point for the five frame-level lighting inputs that used to hang off
/// <see cref="VKPrimitiveGroup"/>: the two DDGI atlases, cloud noise, the aerial-perspective volume and the Sky-View LUT.
/// <para>
/// They are "resolved once per frame, read by every view" resources, so their <c>Active</c>/<c>Bound</c> pair belongs to
/// neither a primitive group nor a view. The consumer is the per-view globals set 3 written by
/// <see cref="Pipeline.PrepareViewGlobals"/>; <see cref="Resolve"/> runs once per frame from <c>VKPrimitiveGroup.SetLighting</c>,
/// before any view is prepared, so every view of a frame sees the same handles.
/// </para>
/// <para>
/// The environment cube is deliberately not here: <see cref="VKTextureCube.Active"/> clears itself inside
/// <c>VKTextureCube.Dispose</c>, and that self-invalidation is the invariant that keeps a destroyed cube out of a
/// descriptor set. Moving the field would split that ownership across two types, so the cube stays on
/// <see cref="VKTextureCube"/> and only its binding number is shared.
/// </para>
/// <para>
/// The binding numbers are the ones the shader declarations already carry (16..20 and 25); 3B only changed their set
/// index from 0 to 3, so nothing here introduces a new numbering.
/// </para>
/// </summary>
internal static class SceneFrameInputs
{
    /// <summary>Environment radiance cube (GLSL <c>samplerCube envCube</c>), globals set 3 binding 16. The texture itself
    /// stays on <see cref="VKTextureCube"/> (see the note above); only the number is re-exported so every reader of set 3
    /// takes its six bindings from one place.</summary>
    internal const uint EnvCubeBinding = VKTextureCube.EnvCubeBinding;

    /// <summary>DDGI irradiance atlas (GLSL <c>sampler2D ddgiAtlas</c>), globals set 3 binding 17.</summary>
    internal const uint DdgiAtlasBinding = 17;

    /// <summary>DDGI depth-moment atlas (GLSL <c>sampler2D ddgiDepth</c>), globals set 3 binding 18.</summary>
    internal const uint DdgiDepthBinding = 18;

    /// <summary>Cloud noise (GLSL <c>sampler2D cloudNoise</c>, wrap immutable), globals set 3 binding 19.</summary>
    internal const uint CloudNoiseBinding = 19;

    /// <summary>Aerial-perspective volume (GLSL <c>sampler3D aerialLut</c>), globals set 3 binding 20.</summary>
    internal const uint AerialLutBinding = 20;

    /// <summary>Sky-View LUT (GLSL <c>sampler2D skyView</c>), globals set 3 binding 25.</summary>
    internal const uint SkyViewBinding = Pipeline.SkyViewBinding;

    // Each Active field is cleared to null on the same frame its producer's FrameSchedule slot goes null (quality
    // downgrade, Dispose), so an already-freed handle is never re-bound. The Bound sibling turns that null into a
    // placeholder that is safe to sample: a wild descriptor makes the WSL/Android drivers abort, not merely render wrong.
    // White is not a dangerous value for cloud noise / Sky-View: both consumers are gated by a parameter that the
    // producer only writes while the name is non-null (cloudParams0.w layer count, skyParams0.w).

    internal static Texture? DdgiAtlasActive;
    internal static Texture DdgiAtlasBound => DdgiAtlasActive ?? Device.White;

    internal static Texture? DdgiDepthActive;
    internal static Texture DdgiDepthBound => DdgiDepthActive ?? Device.White;

    internal static Texture? CloudNoiseActive;
    internal static Texture CloudNoiseBound => CloudNoiseActive ?? Device.White;

    /// <summary>The identity element for additive AP composition: black contributes nothing even if sampled.</summary>
    internal static VKTexture3D? AerialLutActive;
    internal static VKTexture3D AerialLutBound => AerialLutActive ?? VKTexture3D.DummyBlack;

    internal static Texture? SkyViewActive;
    internal static Texture SkyViewBound => SkyViewActive ?? Device.White;

    /// <summary>
    /// Resolves all five inputs for the current frame. Every lookup is by name into the static texture dictionaries, so a
    /// producer that was disposed since the last frame simply yields null and the Bound fallback takes over.
    /// </summary>
    internal static void Resolve()
    {
        bool ddgiReady = Season.Rendering.Effects.DdgiEffect.Ready;
        DdgiAtlasActive = ddgiReady
            ? Graphics.FindDdgiAtlas(Season.Rendering.Effects.DdgiEffect.ActiveIrradianceName)
            : null;
        DdgiDepthActive = ddgiReady
            ? Graphics.FindDdgiAtlas(Season.Rendering.Effects.DdgiEffect.ActiveDepthName)
            : null;

        CloudNoiseActive = Season.Rendering.FrameSchedule.CloudNoiseTexture is string cloudNoiseName
            ? Graphics.FindDdgiAtlas(cloudNoiseName)
            : null;

        // The AP volume is a compute 3D texture, registered in VKTexture3D's own dictionary (isolated from the 2D
        // DictionaryVKTexture, see 1-8), so only Find applies here.
        AerialLutActive = Season.Rendering.FrameSchedule.AerialLutTexture is string aerialName
            ? VKTexture3D.Find(aerialName)
            : null;

        SkyViewActive = Season.Rendering.FrameSchedule.SkyViewTexture is string skyViewName
            ? Graphics.FindDdgiAtlas(skyViewName)
            : null;
    }
}
