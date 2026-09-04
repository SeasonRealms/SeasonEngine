// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// Scene-lighting authoring layer: the app-side persistent light set is baked each frame into the GPU lighting UBO mirror.
///
/// Design notes:
/// - <see cref="Lights"/> is unbounded at the authoring layer, while the GPU limit
///   <see cref="Season.Controls.SceneLightParams.MaxLights"/> stays fixed at 8;
///   <see cref="Bake"/> trims overflow by priority (directional lights and shadow casters are kept first).
/// - Lights are persistent objects: the app calls <see cref="Add"/> once during initialization,
///   then only changes properties every frame (Direction/Intensity/IsOpen),
///   without rebuilding them or causing per-frame allocations.
/// - Bake writes in place (UBO mirror passed by ref), touching only CameraPos/Ambient/Lights/Params0.XZW;
///   Params0.Y (hdrExposure) plus shadow/env/GI fields are reinjected every frame by each backend's SetLighting and must never be cleared here.
///
/// Typical frame order: App.Update changes light properties -> Bake(ref SceneLights, CameraPos)
///   -> Model.AppendWorldLights (append glTF KHR punctual lights) -> backend frame loop computes shadows -> SetLighting uploads.
/// </summary>
public sealed class SceneLighting
{
    /// <summary>Scene light collection (unbounded). Use <see cref="Add"/> to enter and <see cref="Remove"/> to leave.</summary>
    public readonly List<LightSource> Lights = new();

    /// <summary>Constant ambient light: xyz=color, w=intensity. Written to SceneLightParams.Ambient during Bake
    /// (replaced by SH9 environment diffuse when EnvParams.Z&gt;0.5, see 1-7 clause 4).</summary>
    public Vector4 Ambient = new Vector4(0.1f, 0.1f, 0.1f, 1f);

    /// <summary>Selection scratch buffer (preallocated to the GPU limit and reused inside Bake -> zero per-frame allocations).</summary>
    readonly LightSource[] _selected = new LightSource[SceneLightParams.MaxLights];

    /// <summary>Add to the scene (returns the argument itself, convenient for chained retention like <c>var sun = Lighting.Add(new LightSource{...});</c>).</summary>
    public LightSource Add(LightSource light)
    {
        if (light != null && !Lights.Contains(light))
            Lights.Add(light);
        return light!;
    }

    /// <summary>Remove from the scene (hard delete; to turn a light off temporarily, use <see cref="LightSource.IsOpen"/> instead).</summary>
    public void Remove(LightSource light)
    {
        if (light != null)
            Lights.Remove(light);
    }

    /// <summary>
    /// Bake this frame's lighting into the UBO mirror (zero per-frame allocations, written in place).
    ///
    /// Flow: write CameraPos/Ambient -> filter (IsOpen &amp;&amp; Intensity&gt;0) and insertion-sort by priority into the
    /// <see cref="SceneLightParams.MaxLights"/> slots -> write each slot through ToGpu ->
    /// write Params0.X=count, Z=directionalIndex (the strongest shadow-casting directional light this frame), and W=spotShadowIndex (or -1 when absent).
    ///
    /// Only X/Z/W of Params0 are rewritten here: Y (hdrExposure) is injected by backend SetLighting and must be preserved.
    /// Tail fields (shadow/env/GI) are not cleared because they are reinjected from a single point every frame by the backend, and clearing them here would break that contract.
    /// </summary>
    public void Bake(ref SceneLightParams scene, Vector3 cameraPos)
    {
        scene.CameraPos = new Vector4(cameraPos, 0f);
        scene.Ambient = Ambient;

        int max = SceneLightParams.MaxLights;
        int count = 0;

        // Stable insertion in descending SortKey order: equal-priority lights preserve Add order, and overflow naturally drops the tail (lowest priority).
        // Complexity is O(n·max), where n is the authoring-layer light count; only the preallocated _selected buffer is written, with no allocations.
        for (int n = 0; n < Lights.Count; n++)
        {
            var light = Lights[n];
            if (light == null || !light.IsOpen || light.Intensity <= 0f)
                continue;

            int key = SortKey(light);
            int pos = count;
            while (pos > 0 && SortKey(_selected[pos - 1]) < key)
                pos--;

            if (pos >= max)
                continue;   // Cannot beat the already-full top max entries, so drop it directly

            for (int j = Math.Min(count, max - 1); j > pos; j--)
                _selected[j] = _selected[j - 1];
            _selected[pos] = light;
            if (count < max)
                count++;
        }

        int directionalIndex = -1;
        float directionalWeight = 0f;
        int spotShadowIndex = -1;

        for (int i = 0; i < count; i++)
        {
            var light = _selected[i];
            scene.Lights[i] = light.ToGpu();

            if (light.Kind == LightKind.Directional)
            {
                // Select the "strongest shadow-casting directional light this frame": CSM only has one cascade set (three atlas quadrants),
                // so when sun and moon are both in the sky we must choose one, and the stronger irradiance gives the right look
                // (sunlight dominates moonlight by day; when the moon is alone in the sky it wins automatically, with no app-side intervention).
                // Non-shadow-casting directional lights never occupy this slot: Params0.Z is only used for shadow gating in shaders
                // (dirShadowIdx, not the lighting itself), and pointing it at such a light would incorrectly give shadows to a light that should not cast them.
                if (light.CastShadows)
                {
                    float weight = light.Intensity * Luma(light.Color);
                    if (directionalIndex < 0 || weight > directionalWeight)
                    {
                        directionalIndex = i;
                        directionalWeight = weight;
                    }
                }
            }
            else if (light.Kind == LightKind.Spot && light.CastShadows && spotShadowIndex < 0)
            {
                spotShadowIndex = i;   // Spot shadowmap only has atlas slot 3, so take the first shadow caster
            }
        }

        scene.Params0.X = count;
        scene.Params0.Z = directionalIndex;
        scene.Params0.W = spotShadowIndex;
    }

    /// <summary>
    /// Priority weight for trimming: directional lights (the only global illumination source, losing one darkens the whole scene)
    /// &gt; shadow casters (losing them causes abrupt shadow changes) &gt; regular lights.
    /// The weight scale is intentionally much larger than the normal Priority range so manual Priority values cannot drown out type priority.
    /// </summary>
    static int SortKey(LightSource light)
    {
        int key = light.Priority;
        if (light.Kind == LightKind.Directional)
            key += 1000;
        if (light.CastShadows)
            key += 500;
        return key;
    }

    /// <summary>
    /// Rec.709 relative luminance: collapses linear RGB into a single scalar for comparing irradiance magnitude between two lights of the same kind
    /// (multiplied by <see cref="LightSource.Intensity"/>; the moon's cool blue has lower luma than the sun's warm white, and that is intentionally included here).
    /// </summary>
    static float Luma(Vector3 color)
    {
        return 0.2126f * color.X + 0.7152f * color.Y + 0.0722f * color.Z;
    }
}
