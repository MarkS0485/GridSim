using GridSim.Core.Diagnostics;

namespace GridSim.Cli;

/// <summary>
/// F08: the CLI's crash report for an exception nothing handled.
///
/// Program.cs registers one process-wide UnhandledException handler first thing. Missing input files and unloadable
/// cases are turned into a one-line "error:" and exit 1 there (they are input errors, not defects). Every other
/// unhandled exception arrives here: a defect in GridSim. This writes a crash log (see <see cref="CrashLog"/>) and
/// prints a short message on stderr naming the log, then returns, so the runtime still prints the full stack and ends
/// the process with its non-zero crash code. The crash is never turned into success, and never into exit 1 (which
/// would wrongly blame the user's input).
/// </summary>
public static class CrashReport
{
    /// <summary>The app name used in the crash-log file name and header (the binary is called gridsim, F03).</summary>
    public const string AppName = "gridsim";

    /// <summary>Write the crash log and the stderr message. Never throws.</summary>
    public static void Write(object? error, IReadOnlyList<string> args)
    {
        try
        {
            Console.Out.Flush();
            string? path = CrashLog.Write(AppName, error, args);
            string what = error is Exception ex ? $"{ex.GetType().Name}: {ex.Message}" : $"{error}";
            Console.Error.WriteLine($"gridsim: internal error - this is a defect in GridSim, not a problem with your input.");
            Console.Error.WriteLine($"gridsim: {what}");
            Console.Error.WriteLine(path is null
                ? "gridsim: the crash log could not be written (no writable folder); the stack trace follows."
                : $"gridsim: crash log written to {path} - please include it when reporting the problem.");
        }
#pragma warning disable CA1031 // A crash reporter must never raise a second exception over the first.
        catch (Exception)
#pragma warning restore CA1031
        {
            // Swallowed on purpose: the original exception still crashes the process with its stack trace.
        }
    }
}
