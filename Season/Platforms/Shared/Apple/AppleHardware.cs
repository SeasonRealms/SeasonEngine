// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Platforms.Shared.Apple;

/// <summary>
/// Machine identification for the Apple targets. The hardware identifier answers "what is this",
/// which neither the user-facing model name nor the interface idiom can: on iPhone Duo the inner
/// display reports a Pad-sized, regular-width environment while the memory and layout budget are
/// still a phone's, so the idiom alone would grade it as a tablet.
/// </summary>
internal static class AppleHardware
{
    /// <summary>
    /// Hardware identifier of iPhone Duo, shared by the folded and the unfolded state. Matched
    /// exactly: iPhone19,2, iPhone19,3 and iPhone19,7 are other phones, so no prefix matching.
    /// </summary>
    public const string IPhoneDuoModel = "iPhone19,4";

    static readonly Lazy<string> ModelReader = new(ReadModel);

    /// <summary>
    /// Hardware identifier such as "iPhone19,4" or "Mac14,2"; empty when it cannot be read.
    /// Cached for the process lifetime and safe to read from any thread.
    /// </summary>
    public static string Model => ModelReader.Value;

    /// <summary>True when this is iPhone Duo, folded or unfolded.</summary>
    public static bool IsIPhoneDuo =>
        string.Equals(Model, IPhoneDuoModel, StringComparison.OrdinalIgnoreCase);

    static string ReadModel()
    {
        try
        {
            // A simulator reports the host architecture (arm64/x86_64) from hw.machine and puts
            // the simulated hardware in the environment instead. A real device has no such
            // variable, so the check falls through to sysctl there.
            var simulated = Environment.GetEnvironmentVariable("SIMULATOR_MODEL_IDENTIFIER");

            if (!string.IsNullOrWhiteSpace(simulated))
            {
                return simulated;
            }

            return ReadSysctl("hw.machine");
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    static string ReadSysctl(string name)
    {
        UIntPtr size = UIntPtr.Zero;

        if (sysctlbyname(name, IntPtr.Zero, ref size, IntPtr.Zero, UIntPtr.Zero) != 0 || size == UIntPtr.Zero)
        {
            return string.Empty;
        }

        var buffer = Marshal.AllocHGlobal((int)size);

        try
        {
            if (sysctlbyname(name, buffer, ref size, IntPtr.Zero, UIntPtr.Zero) != 0)
            {
                return string.Empty;
            }

            return Marshal.PtrToStringAnsi(buffer, (int)size)?.TrimEnd('\0') ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("libc", SetLastError = true)]
    static extern int sysctlbyname(string name, IntPtr oldp, ref UIntPtr oldlenp, IntPtr newp, UIntPtr newlen);
}
