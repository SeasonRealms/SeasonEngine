// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// One-shot "immediate 2D" compatibility mode for legacy MonoGame-style applications
/// (SeasonXNA ports such as SanguoSeason) that render exclusively through the immediate 2D backend
/// (Image2D / Draw2D canvas) and never use the 3D path.
///
/// When enabled before platform initialization:
/// - the main 3D PSO family is never compiled (Pipeline.Init is skipped),
/// - no 3D resources are registered or created: offscreen SceneColor / SceneVelocity / SceneDepth /
///   PostColor / ShadowMap and their passes stay null, so no effect ever runs and the render-quality
///   tier is never consulted,
/// - every frame bypasses the whole pass chain and executes only the Overlay pass, which submits the
///   2D canvas directly into the backbuffer of the current frame (first backbuffer pass, so it clears).
///
/// The switch is one-way by contract: it must be assigned before the platform backend initializes
/// (before AndroidApp.Run / the first surface creation) and cannot be turned off afterwards, because
/// the skipped resources are never created. It is not a runtime quality setting; keep it false for
/// regular 3D applications.
/// </summary>
public static class Immediate2DMode
{
    static bool _enabled;
    static bool _frozen;

    /// <summary>
    /// True to run this session in immediate-2D-only mode. Read by platform initialization
    /// (AndroidApp / LinuxApp) and by <see cref="RenderPass.ExecutePasses"/>.
    /// Must be assigned before the platform backend freezes it during initialization; a later
    /// change is rejected with an <see cref="InvalidOperationException"/>, which enforces the
    /// one-way contract.
    /// </summary>
    public static bool Enabled
    {
        get => _enabled;
        set
        {
            if (_frozen && value != _enabled)
                throw new InvalidOperationException(
                    "Immediate2DMode.Enabled is frozen for this session: the 3D resources it skips (main PSO family, offscreen targets, effects) were never created, so the mode cannot be switched after platform initialization.");
            _enabled = value;
        }
    }

    /// <summary>
    /// Freezes the current decision so it cannot change for the rest of the session.
    /// Called by the platform backend once initialization has consumed it, turning the one-way
    /// contract into an enforced invariant. Idempotent.
    /// </summary>
    public static void Freeze() => _frozen = true;
}
