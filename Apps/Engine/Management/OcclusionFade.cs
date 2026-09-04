// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Engine.Management;

/// <summary>
/// Camera occlusion fading: in follow-camera mode, when the Player is visually blocked
/// by another Model or Mesh3D, the occluder's alpha is reduced to OccludedAlpha so the
/// Player behind it remains visible; when the occlusion is cleared, alpha fades back to
/// the registered baseline.
/// Detection: each frame, a ray is cast from CameraPos to the center of the Player bounds.
/// A candidate is considered fully occluding if its own TryPick hits it
/// (OBB-accurate and aligned with picking; not-ready, disabled, and zero-size bounds are
/// automatically exempted) and the hit point lies before the Player.
/// Partial occlusion is handled the same as full occlusion by fading the whole object.
/// The candidate set is registered independently as a superset of movement obstacles:
/// overhead pieces such as roofs that do not block movement but do block the view are also included.
/// Instanced controls (Robots) are excluded in v1 because they do not support per-instance alpha.
/// This runs at the end of App.Update, after both camera and player positions have settled for the frame.
/// </summary>
internal sealed class OcclusionFade
{
    /// <summary>Registered occlusion candidates for single-instance controls.</summary>
    internal List<Mesh3DBase> Targets { get; } = new();

    /// <summary>Target alpha while occluded: multiplied by the registered baseline so translucent controls fade proportionally.</summary>
    const float OccludedAlpha = 0.35f;

    /// <summary>Fade speed in alpha units per second, shared by fade-in and fade-out.</summary>
    const float FadeSpeed = 3f;

    /// <summary>Hit points must be at least this much closer than the Player to count as occluding, avoiding false positives on touching surfaces.</summary>
    const float OccludeMargin = 1e-3f;

    // Baseline alpha captured at registration time. Current registered objects are all 1,
    // but this prevents future translucent controls from being forced back to full opacity.
    readonly Dictionary<Mesh3DBase, float> baseAlphas = new();

    /// <summary>Registers a candidate and snapshots its current alpha as the baseline. Registration may happen before asset loading completes because TryPick already exempts not-ready objects.</summary>
    internal void Register(Mesh3DBase target)
    {
        Targets.Add(target);
        baseAlphas[target] = target.Alpha;
    }

    /// <summary>
    /// Per-frame update: ray-test each candidate, determine occlusion, then linearly move alpha
    /// toward the target level. Once alpha reaches the target, no further writes occur
    /// because the setter is already gated by the Changed flag, so idle frames stay clean.
    /// </summary>
    internal void Update(float time)
    {
        var player = App.Instance.player?.model;
        if (player == null)
            return;

        var targetPoint = new Vector3(player.PosX, player.PosY, player.PosZ);
        var toTarget = targetPoint - App.Instance.CameraPos;
        float playerDistance = toTarget.Length();
        if (playerDistance < 1e-5f)
            return; // Camera and player coincide, so there is no meaningful line of sight.

        var rayDirection = toTarget / playerDistance;

        for (int i = 0; i < Targets.Count; i++)
        {
            var candidate = Targets[i];

            bool occluding = candidate.TryPick(App.Instance.CameraPos, rayDirection, out float distance)
                          && distance < playerDistance - OccludeMargin;

            float goal = occluding ? baseAlphas[candidate] * OccludedAlpha : baseAlphas[candidate];

            float alpha = candidate.Alpha;
            if (alpha == goal)
                continue;

            float step = FadeSpeed * time;
            alpha = alpha < goal ? MathF.Min(goal, alpha + step) : MathF.Max(goal, alpha - step);
            candidate.Alpha = alpha;
        }
    }
}
