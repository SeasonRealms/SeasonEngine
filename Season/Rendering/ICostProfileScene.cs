// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// Contract 3-1: one row of the cost table - a name plus the pair of switches that puts the running engine into that
/// configuration and takes it back out again.
///
/// <para><b>Two ways to be a scenario.</b> A scenario the engine can switch at frame time carries both delegates and the
/// runner fires them around its measurement window. A scenario whose switch has to be in place before initialization (a
/// quality tier that bakes a PSO or a shader define) carries neither: there is nothing to flip, and the row is labelled
/// by <c>SEASON_PROFILE_SCENARIO</c> while an outer shell sets the tier variables. A scenario with no delegates and no
/// label is the baseline - the engine exactly as configured.</para>
/// </summary>
public sealed class CostScenario
{
    /// <summary>Captures one row of the table.</summary>
    /// <param name="name">Row label in both CSVs, for example <c>no-shadow</c>. It is the key the final table is pivoted
    /// on, so it must be stable across runs.</param>
    /// <param name="apply">Runtime switch, or null when the configuration is established before the process starts.</param>
    /// <param name="revert">Restores exactly what <paramref name="apply"/> changed, or null when nothing was changed.</param>
    public CostScenario(string name, Action? apply = null, Action? revert = null)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Apply = apply;
        Revert = revert;
    }

    /// <summary>Row label in both CSVs.</summary>
    public string Name { get; }

    /// <summary>
    /// Applies the runtime switch. Fired by the runner after the previous scenario has been reverted, i.e. every scenario
    /// starts from the same engine state.
    /// </summary>
    public Action? Apply { get; }

    /// <summary>
    /// Undoes <see cref="Apply"/>. A scenario reverts the one field it touched - captured on entry - rather than
    /// restoring a whole <see cref="RenderQuality"/> snapshot: a snapshot would also roll back whatever else legitimately
    /// changed during the measurement window, and a scenario that forgets to revert is visible immediately as the next
    /// baseline drifting.
    /// </summary>
    public Action? Revert { get; }

    /// <summary>True when there is nothing to switch, i.e. this row measures the engine as configured.</summary>
    public bool IsBaseline => Apply == null && Revert == null;
}

/// <summary>
/// Contract 3-1: everything the cost runner needs to know about a scene in order to measure it.
///
/// <para>The runner knows nothing about islands, cameras or quality tiers: it applies a scenario, waits, measures,
/// writes a row, reverts and moves on. Keeping that knowledge behind one interface is what lets the same runner measure
/// another app's scene by registering a different implementation, and it is why the engine half of the feature contains
/// no app-specific names.</para>
/// </summary>
public interface ICostProfileScene
{
    /// <summary>Scene label, printed next to the run id so two scenes never merge into one pivot.</summary>
    string Name { get; }

    /// <summary>
    /// Camera paths the runner can select with <c>SEASON_PROFILE_CAMERA</c>. The first one is the default. A path is a
    /// name, not a description: the runner only ever passes it back to <see cref="SampleCameraPose"/>.
    /// </summary>
    IReadOnlyList<string> CameraPaths { get; }

    /// <summary>
    /// True once the scene has finished streaming in. The runner counts no frame before this, because a half-loaded
    /// scene would put a load-time number in the "cost of this effect" column and poison every delta derived from it.
    /// </summary>
    bool IsReady { get; }

    /// <summary>
    /// Camera pose <paramref name="t"/> seconds along <paramref name="path"/>, with the look target written to
    /// <paramref name="target"/>.
    ///
    /// <para>It must be a pure function of <paramref name="t"/> and of nothing else. Every scenario replays the same
    /// path, and "the same camera at the same frame number" is the only reason two rows of the table can be subtracted
    /// from each other.</para>
    /// </summary>
    Vector3 SampleCameraPose(string path, float t, out Vector3 target);

    /// <summary>Rows of the cost table. The first one is normally the baseline.</summary>
    IReadOnlyList<CostScenario> Scenarios { get; }
}
