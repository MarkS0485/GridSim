using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace GridSim.Core.Diagnostics;

/// <summary>
/// Writes a crash log when a GridSim app (the CLI or the desktop app) hits an exception that nothing handled (F08).
///
/// A crash is a defect, so it must stay loud: the caller still lets the process fail (the CLI keeps the runtime's
/// non-zero crash exit; the desktop app shows the message and closes). What this adds is evidence that survives the
/// crash: one plain-text file holding the exception (type, message, stack, every inner exception), the exact build
/// (<see cref="BuildInfo.InformationalVersion"/>), the runtime, the OS, the command-line arguments and the time, so a
/// bug report can quote it instead of a half-remembered screen.
///
/// Where the file goes, in order:
/// <list type="number">
/// <item>the folder named by the <c>GRIDSIM_CRASH_DIR</c> environment variable, when it is set (used by the tests,
/// and by anyone who wants crash logs somewhere specific);</item>
/// <item>otherwise <c>%LOCALAPPDATA%\GridSim\crash</c> on Windows (<c>~/.local/share/GridSim/crash</c> on Linux,
/// <c>~/Library/Application Support/GridSim/crash</c> on macOS), next to the licence file;</item>
/// <item>otherwise, if that cannot be created, the system temporary folder. On Linux and macOS a folder created there
/// is owner-only and the file is created new and owner-only (GR-839), so another user of a shared <c>/tmp</c> cannot
/// read it or redirect it.</item>
/// </list>
/// Writing the log must never throw: a failure to write it returns <c>null</c> and the caller still reports the crash.
/// </summary>
public static class CrashLog
{
    /// <summary>Environment variable that overrides the crash-log folder.</summary>
    public const string DirectoryVariable = "GRIDSIM_CRASH_DIR";

    /// <summary>
    /// The folder crash logs are written to (see the class summary for the order). Not created by this property.
    /// </summary>
    public static string DefaultDirectory()
    {
        string? overrideDir = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (!string.IsNullOrWhiteSpace(overrideDir)) return overrideDir;
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrEmpty(local)
            ? Path.Combine(Path.GetTempPath(), "GridSim", "crash")
            : Path.Combine(local, "GridSim", "crash");
    }

    /// <summary>
    /// Build the text of a crash log. Separate from <see cref="Write"/> so it can be tested without touching disk.
    /// </summary>
    /// <param name="app">Which app crashed, for example <c>gridsim</c> (the CLI) or <c>GridSim.Wpf</c>.</param>
    /// <param name="error">The unhandled exception object (normally an <see cref="Exception"/>; anything else is
    /// recorded by its type and text).</param>
    /// <param name="args">The command-line arguments, or null when there are none to record.</param>
    /// <param name="utcNow">The time to stamp (passed in so a test gets a fixed value).</param>
    public static string Format(string app, object? error, IReadOnlyList<string>? args, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(app);
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"GridSim crash log");
        sb.AppendLine(CultureInfo.InvariantCulture, $"app:     {app}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"time:    {utcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC");
        sb.AppendLine(CultureInfo.InvariantCulture, $"build:   {BuildInfo.InformationalVersion}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"runtime: {RuntimeInformation.FrameworkDescription} {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"os:      {RuntimeInformation.OSDescription}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"args:    {(args is null || args.Count == 0 ? "(none)" : string.Join(' ', args))}");
        sb.AppendLine();
        if (error is Exception ex)
        {
            // Exception.ToString() already carries the type, message, stack and the whole inner-exception chain
            // (including every inner exception of an AggregateException), which is exactly what a report needs.
            sb.AppendLine(ex.ToString());
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"non-exception object thrown: {error?.GetType().FullName ?? "null"}: {error}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Write a crash log and return its full path, or <c>null</c> if no file could be written anywhere. Never throws.
    /// The file name is <c>&lt;app&gt;-crash-&lt;yyyyMMdd-HHmmss&gt;-&lt;process id&gt;.log</c>, so two crashes never
    /// overwrite each other.
    /// </summary>
    public static string? Write(string app, object? error, IReadOnlyList<string>? args)
        => Write(app, error, args, DefaultDirectory());

    /// <summary>
    /// As <see cref="Write(string, object?, IReadOnlyList{string}?)"/>, into a given folder first (the system temporary
    /// folder is still the fallback). Lets a test write into its own folder without changing a process-wide variable.
    /// </summary>
    public static string? Write(string app, object? error, IReadOnlyList<string>? args, string directory)
    {
        string text;
        DateTime now = DateTime.UtcNow;
        try { text = Format(app, error, args, now); }
#pragma warning disable CA1031 // A crash logger must survive whatever the crashed exception's ToString does.
        catch (Exception formatFailure)
#pragma warning restore CA1031
        {
            // Last resort: something in the exception's own ToString threw. Still write what we can.
            text = $"GridSim crash log\napp: {app}\nthe crash could not be formatted: {formatFailure.GetType().Name}: {formatFailure.Message}\n";
        }
        string name = string.Create(CultureInfo.InvariantCulture,
            $"{app}-crash-{now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
        foreach (string dir in new[] { directory, Path.Combine(Path.GetTempPath(), "GridSim", "crash") })
        {
            try
            {
                // GR-839 (October 2026): the log can land in the shared temporary folder (/tmp on Linux and macOS), so
                // on those systems a folder this call creates is owner-only (0700) and the file is created owner-only
                // (0600) and NEW (O_EXCL: an existing file or a planted symbolic link of that name is refused, and
                // the next folder is tried), so another user can neither read the arguments and stack nor redirect
                // the write. On Windows the temporary folder is per-user and inherits the user's ACL.
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (OperatingSystem.IsWindows())
                    Directory.CreateDirectory(dir);
                else
                {
                    Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                }
                string path = Path.Combine(dir, name);
                using (var stream = new FileStream(path, options))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                    writer.Write(text);
                return path;
            }
            catch (Exception ioFailure) when (ioFailure is IOException or UnauthorizedAccessException
                                                  or ArgumentException or NotSupportedException)
            {
                // Try the next folder; a crash log is evidence, not a reason for a second crash.
            }
        }
        return null;
    }
}
