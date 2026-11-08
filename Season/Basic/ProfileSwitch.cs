// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Collections.Concurrent;

namespace Season.Basic;

/// <summary>
/// Contract 3-1/3-2: the one place a profiling switch is read.
///
/// <para><b>Why this exists.</b> Every switch started out as a bare <c>Environment.GetEnvironmentVariable</c> call, which
/// is fine on the four desktop targets and wrong on the other two: an Android process has no environment to set without a
/// <c>wrap.sh</c>, and a browser has no process at all. The host therefore has a second way in - it reads whatever channel
/// it does have (an intent extra, a URL query, a marker file, a debug setting) and pushes the values here before the first
/// profiler call.</para>
///
/// <para><b>Order matters.</b> An override only takes effect while the profiler has not resolved its configuration yet.
/// That resolution happens on the first <c>StartupProfiler</c> entry point, so a host must call <see cref="Set"/> from its
/// own entry point, which is exactly where the earliest instrumentation site lives. Later calls succeed but change
/// nothing, because the resolved values are already baked into the running report - returning false from
/// <see cref="Set"/> is how a host learns its override arrived too late.</para>
///
/// <para><b>Cost.</b> A hit is one dictionary lookup on the caller's thread. Every profiling entry point reads at most a
/// handful of switches once per process, so nothing here is on a hot path.</para>
/// </summary>
public static class ProfileSwitch
{
    /// <summary>
    /// Values pushed by the host, which win over the environment. Keyed by the same names the environment uses so a
    /// single log line can print either without translation.
    /// </summary>
    static readonly ConcurrentDictionary<string, string> overrides = new(StringComparer.Ordinal);

    /// <summary>
    /// Forces a switch to a value. Returns false when the profiler already resolved its configuration, in which case the
    /// override is still stored (so a second reader sees it) but has no effect on the running report.
    /// </summary>
    /// <remarks>
    /// A null or empty <paramref name="value"/> clears the switch rather than setting it to an empty string, so a host can
    /// pass an absent query parameter straight through without special-casing.
    /// </remarks>
    public static bool Set(string name, string? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (string.IsNullOrWhiteSpace(value))
        {
            overrides.TryRemove(name, out _);
        }
        else
        {
            overrides[name] = value.Trim();
        }

        // The profiler publishes this once it has read every switch; until then the value above is what it will see.
        return !ProfileSwitchTable.Resolved;
    }

    /// <summary>Forgets an override, falling back to the environment on the next read.</summary>
    public static void Clear(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        overrides.TryRemove(name, out _);
    }

    /// <summary>
    /// The effective text of a switch: the host override when one exists, else the environment, else null. Blank values
    /// are reported as null so "set to empty" and "not set" cannot diverge at the call site.
    /// </summary>
    public static string? Text(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (overrides.TryGetValue(name, out string? overridden))
            return string.IsNullOrWhiteSpace(overridden) ? null : overridden;

        string? value = ReadEnvironment(name);

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>True when the switch is set to something other than <c>0</c> or <c>false</c>.</summary>
    public static bool Flag(string name)
    {
        string? value = Text(name);

        if (string.IsNullOrEmpty(value)) return false;

        return value != "0" && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The switch parsed as a positive number, or <paramref name="fallback"/> when absent or unparseable.</summary>
    public static double Number(string name, double fallback)
    {
        string? value = Text(name);

        if (string.IsNullOrEmpty(value)) return fallback;

        return double.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double parsed) && parsed > 0
            ? parsed
            : fallback;
    }

    /// <summary>
    /// Applies a whole set at once from a <c>name=value;name=value</c> spec, which is the form a URL query or an intent
    /// extra can carry without the host having to know every switch name. Malformed segments are ignored.
    /// </summary>
    /// <returns>How many overrides were accepted.</returns>
    public static int Apply(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return 0;

        int applied = 0;

        foreach (string segment in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int separator = segment.IndexOf('=');

            if (separator <= 0) continue;

            if (Set(segment[..separator], segment[(separator + 1)..])) applied++;
        }

        return applied;
    }

    /// <summary>
    /// The environment is the default channel on desktop, and a sandboxed or browser host may throw instead of returning
    /// null. A switch that cannot be read is simply unset.
    /// </summary>
    static string? ReadEnvironment(string name)
    {
        try
        {
            return Environment.GetEnvironmentVariable(name);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Contract 3-2: the resolution bit <see cref="ProfileSwitch"/> consults. It lives in its own type so that reading a
/// switch never triggers the profiler's static initialization - the earliest host override has to be able to land before
/// the profiler exists, and touching its type to ask "are you resolved?" would defeat that ordering.
/// </summary>
internal static class ProfileSwitchTable
{
    /// <summary>Set once the profiler has read every switch it cares about, which happens on its first entry point.</summary>
    internal static volatile bool Resolved;
}
