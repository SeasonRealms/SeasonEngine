// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Engine;

/// <summary>
/// Contract 3-1: the island this sample renders, described the way the cost runner needs it - three fixed camera paths
/// and a table of switches it can flip while running.
///
/// <para><b>Why the runtime table is only <see cref="ViewFeature"/> bits.</b> A quality tier that is read once at
/// initialization (shadow PSO, decal PSO, the <c>SHADOW_ENABLED</c> shader define, HdrSceneColor target formats) cannot
/// be flipped at frame N and produce the same pipeline minus one effect: the PSO or the define was already baked, so the
/// measurement would describe a half-configured renderer rather than the cost of the effect. Those rows go through
/// <c>SEASON_PROFILE_SCENARIO</c> one process at a time instead, with the tier variable set before launch. Everything in
/// the table below is consulted per frame inside <see cref="FrameSchedule"/>'s pass chain, so switching it off really
/// does make the work disappear.</para>
///
/// <para><b>Why the paths are keyframes.</b> A path has to be a pure function of the frame number, otherwise the two
/// rows of the table were rendered from different vantage points and their difference is not the effect's cost. There is
/// no feedback from the app, no <c>LookAt</c> against live state, and no accumulation: given a <c>t</c>, the pose is
/// known.</para>
/// </summary>
internal sealed class CostProfileScene : ICostProfileScene
{
    /// <summary>
    /// Frames of an unchanged per-frame gather dispatch count before the scene counts as loaded. Four seconds at 60 Hz:
    /// rocks, forest and the mountain ring all stream in through <c>RequestLoad</c> and each arrival changes the count,
    /// so "the count stopped changing" is the point where the scene stops being a moving target.
    /// </summary>
    const int ReadyFrames = 240;

    /// <summary>Where the orbit points. Slightly above ground so the horizon sits below centre.</summary>
    static readonly Vector3 Focus = new(0f, 6f, 0f);

    readonly CameraPath[] paths;

    readonly string[] pathNames;

    readonly CostScenario[] scenarios;

    long dispatchTotal;

    long dispatchDelta = -1;

    int stableFrames;

    internal CostProfileScene()
    {
        paths = new[]
        {
            // A full circuit at 165 m looking down at the island: sea, shoreline, rocks, the waterfall and the river all
            // cross the frame, which is what makes one orbit a fair sample of the whole scene.
            new CameraPath("orbit-slow", 20f,
                new[] { new Vector3(165f, 62f, 0f), new Vector3(0f, 62f, 165f), new Vector3(-165f, 62f, 0f),
                    new Vector3(0f, 62f, -165f), new Vector3(165f, 62f, 0f) },
                new[] { Focus, Focus, Focus, Focus, Focus }),

            // The east shore at eye height: near-field shadows, decals and water fill the frame instead of the horizon.
            new CameraPath("shoreline-pan", 16f,
                new[] { new Vector3(88f, 6f, -72f), new Vector3(84f, 5f, 0f), new Vector3(88f, 6f, 72f) },
                new[] { new Vector3(40f, 2f, -40f), new Vector3(28f, 1.5f, 0f), new Vector3(40f, 2f, 40f) }),

            // A high diagonal pass over the ridge: the volumetric fog band, the clouds and the far mountain ring are the
            // subject here, so the camera looks across the island rather than down at it.
            new CameraPath("harbor-flyover", 18f,
                new[] { new Vector3(-340f, 140f, -240f), new Vector3(-140f, 95f, -80f),
                    new Vector3(140f, 95f, 80f), new Vector3(340f, 140f, 240f) },
                new[] { new Vector3(0f, 12f, 0f), Focus, Focus, new Vector3(0f, 12f, 0f) }),
        };

        // Derived rather than listed twice: a path whose name came from two places would eventually disagree with
        // itself, and SEASON_PROFILE_CAMERA would silently stop finding it.
        pathNames = new string[paths.Length];

        for (int i = 0; i < paths.Length; i++) pathNames[i] = paths[i].Name;

        scenarios = new[]
        {
            Feature("no-shadow", ViewFeature.Shadows),
            Feature("no-fog", ViewFeature.Fog),
            Feature("no-weather", ViewFeature.Weather),
            Feature("no-ao", ViewFeature.AmbientOcclusion),
            Feature("no-bloom", ViewFeature.Bloom),
            Feature("no-taa", ViewFeature.AntiAliasing),
            // A1 rides on the same velocity buffer as TAA, so its row is the one that answers "what does the gather cost
            // on top of the tier that already pays for the motion vectors".
            Feature("no-motion-blur", ViewFeature.MotionBlur),
            Feature("no-particles", ViewFeature.Particles),
            Feature("no-post", ViewFeature.Post),
            Feature("no-decals", ViewFeature.Decals),
            Feature("no-water", ViewFeature.Water),
            Feature("no-reflections", ViewFeature.Reflections),
            Feature("no-waterfall", ViewFeature.Waterfalls),
            Feature("no-transparent", ViewFeature.Transparent),

            // The whole geometry draw. Everything segmented (decals, water, waterfalls, reflections) is gated on opaque,
            // so this row is "the scene, minus nothing else" and is the reference point for the three below. The collection
            // still runs; the opaque bucket is consumed without a replay (SceneDrawQueue.DiscardOpaque), so the delta against
            // baseline is the geometry stage itself.
            Feature("no-geometry", ViewFeature.Opaque),

            // Composite rows: the two full-screen passes, and every compute chain at once. A composite is not a sum of
            // its parts - the AO result is consumed by the fog composite and the fog result by the post resolve - so the
            // rows above will not add up to these.
            Feature("no-prepass", ViewFeature.AmbientOcclusion | ViewFeature.Bloom | ViewFeature.AntiAliasing | ViewFeature.Reflections),
            Feature("no-atmosphere", ViewFeature.Fog | ViewFeature.Weather),
        };
    }

    public string Name => "island";

    public IReadOnlyList<string> CameraPaths => pathNames;

    /// <summary>
    /// Ready when the number of gather dispatches per frame has held still for <see cref="ReadyFrames"/> frames. The
    /// cumulative counter is not compared directly: it grows by the number of instanced groups every single frame, so it
    /// would never stop changing; the delta is what identifies "a panel arrived".
    /// </summary>
    public bool IsReady
    {
        get
        {
            long total = InstanceCull.FixedGatherDispatches + InstanceCull.IndirectGatherDispatches;
            long delta = total - dispatchTotal;

            dispatchTotal = total;

            // A zero delta means nothing was submitted at all - still loading, or an empty scene - and that is not a
            // ready scene either.
            if (delta == 0 || delta != dispatchDelta)
            {
                dispatchDelta = delta;
                stableFrames = 0;

                return false;
            }

            if (stableFrames < ReadyFrames) stableFrames++;

            return stableFrames >= ReadyFrames;
        }
    }

    public Vector3 SampleCameraPose(string path, float t, out Vector3 target)
    {
        for (int i = 0; i < paths.Length; i++)
        {
            if (!string.Equals(paths[i].Name, path, StringComparison.Ordinal)) continue;

            return paths[i].Sample(t, out target);
        }

        return paths[0].Sample(t, out target);
    }

    public IReadOnlyList<CostScenario> Scenarios => scenarios;

    /// <summary>
    /// A row that turns one feature bit off and puts back exactly the mask it found. Restoring the saved mask rather
    /// than setting the bit means a run that started with a bit already off stays that way.
    /// </summary>
    static CostScenario Feature(string name, ViewFeature feature)
    {
        ViewFeature saved = ViewFeature.All;

        return new CostScenario(name,
            () =>
            {
                if (RenderView.CurrentOrNull is not { } view) return;

                saved = view.Features;
                view.Features = saved & ~feature;
            },
            () =>
            {
                if (RenderView.CurrentOrNull is not { } view) return;

                view.Features = saved;
            });
    }

    /// <summary>
    /// A closed keyframe path: positions and look targets, evenly spaced in time, eased with <c>smoothstep</c> inside
    /// each segment. The first and last keyframes are the same point, so <c>t</c> wraps cleanly and any window length
    /// can be sampled without a discontinuity.
    /// </summary>
    readonly struct CameraPath
    {
        internal readonly string Name;

        internal readonly float Duration;

        readonly Vector3[] positions;

        readonly Vector3[] targets;

        internal CameraPath(string name, float duration, Vector3[] positions, Vector3[] targets)
        {
            Name = name;
            Duration = duration;
            this.positions = positions;
            this.targets = targets;
        }

        internal Vector3 Sample(float t, out Vector3 target)
        {
            int segments = positions.Length - 1;

            if (segments <= 0)
            {
                target = targets[0];

                return positions[0];
            }

            float u = Duration <= 0f ? 0f : t / Duration;

            // Wrap so a long window keeps looping instead of freezing on the last keyframe.
            u -= MathF.Floor(u);

            float scaled = u * segments;
            int index = (int)scaled;

            if (index >= segments) index = segments - 1;

            float local = scaled - index;
            float eased = local * local * (3f - 2f * local);

            target = Vector3.Lerp(targets[index], targets[index + 1], eased);

            return Vector3.Lerp(positions[index], positions[index + 1], eased);
        }
    }
}
