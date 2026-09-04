// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Microsoft.JSInterop;

namespace Season.Platforms.Web;

/// <summary>
/// Browser-side rendering diagnostic log switch for the Web backend.
/// </summary>
public static class WebDebug
{
    /// <summary>
    /// Controls whether diagnostic logs are emitted on the C# side.
    /// </summary>
    public static bool Enabled = false;

    /// <summary>
    /// Writes a regular log message when debugging is enabled.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static void Log(string msg)
    {
        if (!Enabled) return;
        Debug.WriteLine(msg);
    }

    /// <summary>
    /// Writes a timing log when debugging is enabled.
    /// </summary>
    public static void LogTiming(string scope, string phase, TimeSpan elapsed, string extra = "")
    {
        if (!Enabled) return;
        var suffix = string.IsNullOrWhiteSpace(extra) ? string.Empty : $" {extra}";
        var message = $"[{scope}] {phase} {elapsed.TotalMilliseconds:F1}ms{suffix}";
        Debug.WriteLine(message);
    }

    /// <summary>
    /// Synchronizes the debug switch between the C# and JS sides.
    /// </summary>
    public static void SetEnabled(bool enabled, IJSInProcessRuntime jsRuntime = null)
    {
        Enabled = enabled;
        try
        {
            jsRuntime?.InvokeVoid("seasonWebGPU.setDebugLog", enabled);
        }
        catch
        {
        }
    }
}

sealed class WebTextureUploadResult
{
    public bool success { get; set; }
    public int width { get; set; }
    public int height { get; set; }
}
