// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Basic;

/// <summary>
/// Contract 3-1/3-2: where a profiler report goes.
///
/// <para><b>Why a sink and not a path.</b> The desktop targets can all write a file, so the first version simply took a
/// directory. Two targets cannot: a browser has no writable file system, and a phone's app sandbox is technically writable
/// but awkward to get a file out of. The measurement itself is identical on all of them - only the destination differs -
/// so the destination is the one thing that gets abstracted.</para>
///
/// <para><b>Append semantics.</b> A report is appended, never overwritten: several runs against the same directory
/// accumulate, which is what makes an A/B leg readable in one file. A sink therefore has to answer "does this file already
/// exist" so the CSV writer knows whether to emit its header.</para>
/// </summary>
public interface IProfileOutput
{
    /// <summary>Human-readable destination, printed in the report and the console summary.</summary>
    string Description { get; }

    /// <summary>Whether the named file already has content, i.e. whether a header line is needed.</summary>
    bool Exists(string fileName);

    /// <summary>Appends one block (a report, a batch of CSV rows, a trace line) to the named file.</summary>
    void Append(string fileName, string content);
}

/// <summary>
/// Contract 3-2: the desktop sink - one directory, files created on first write.
///
/// <para>Allocation happens on the first append rather than in the constructor: a profiler that is enabled but never
/// reaches the first frame must not leave an empty directory behind, because "no directory" and "no report" are the two
/// observations a failed run is diagnosed from.</para>
/// </summary>
internal sealed class DirectoryProfileOutput : IProfileOutput
{
    readonly string directory;

    /// <summary>The report writer and the breadcrumb writer can run on different threads; each append is one file open.</summary>
    readonly object sync = new();

    internal DirectoryProfileOutput(string directory) => this.directory = directory;

    public string Description => directory;

    public bool Exists(string fileName)
    {
        lock (sync)
        {
            return File.Exists(Path.Combine(directory, fileName));
        }
    }

    public void Append(string fileName, string content)
    {
        lock (sync)
        {
            Directory.CreateDirectory(directory);

            File.AppendAllText(Path.Combine(directory, fileName), content, Encoding.UTF8);
        }
    }
}

/// <summary>
/// Contract 3-2: the hosted sink - everything goes to the process's standard error stream, which is the console on a
/// desktop, logcat on Android and the browser console on WebAssembly.
///
/// <para>Every line is prefixed with the file it would have been written to, because the three streams interleave and a
/// bare column of numbers is not diagnosable. <see cref="Exists"/> always answers false: a console has no memory, so each
/// block carries its own header and a captured log contains a complete, self-describing table.</para>
/// </summary>
internal sealed class ConsoleProfileOutput : IProfileOutput
{
    readonly object sync = new();

    public string Description => "console";

    public bool Exists(string fileName) => false;

    public void Append(string fileName, string content)
    {
        // "startup-report.txt" -> "startup-report": one token that greps cleanly out of a noisy device log.
        int dot = fileName.LastIndexOf('.');

        string tag = dot > 0 ? fileName[..dot] : fileName;

        lock (sync)
        {
            int start = 0;

            while (start < content.Length)
            {
                int end = content.IndexOf('\n', start);

                if (end < 0) end = content.Length;

                ReadOnlySpan<char> line = content.AsSpan(start, end - start).TrimEnd('\r');

                if (!line.IsEmpty) Console.Error.WriteLine($"[{tag}] {line}");

                start = end + 1;
            }
        }
    }
}

/// <summary>
/// Contract 3-1/3-2: turns a destination string into a sink.
///
/// <para><c>console</c> (any case) selects the hosted sink; anything else is treated as a directory. That keeps the
/// existing desktop behaviour bit-for-bit - the value was always a path - while giving a browser host a way to receive a
/// report without inventing a second switch.</para>
/// </summary>
public static class ProfileOutput
{
    /// <summary>The destination value that selects <see cref="ConsoleProfileOutput"/>.</summary>
    public const string ConsoleDestination = "console";

    /// <summary>Returns null when no destination was configured, which means "collect, but write nothing".</summary>
    public static IProfileOutput? Create(string? destination)
    {
        if (string.IsNullOrWhiteSpace(destination)) return null;

        string value = destination.Trim();

        return string.Equals(value, ConsoleDestination, StringComparison.OrdinalIgnoreCase)
            ? new ConsoleProfileOutput()
            : new DirectoryProfileOutput(value);
    }
}
