using System.Text.RegularExpressions;
using GridSim.Core;
using GridSim.Core.Diagnostics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// F08 (release register section 1): crash handling. Before F08 the desktop app had no DispatcherUnhandledException
/// handler (a UI-thread exception closed it with no message and no record) and the CLI recorded a crash only on an
/// --audit-log run. Both now write a crash log through <see cref="CrashLog"/>; these tests pin what the log holds and
/// where it goes, and that the CLI still crashes loudly (the handler never converts a defect into an exit code).
/// </summary>
public class CrashLogTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static Exception Thrown()
    {
        // A real thrown exception (so it has a stack) with an inner exception, as a crash would.
        try
        {
            try { throw new InvalidOperationException("inner cause"); }
            catch (InvalidOperationException inner) { throw new ArgumentException("outer failure", inner); }
        }
        catch (ArgumentException ex) { return ex; }
    }

    [Fact]
    public void Format_records_build_args_time_and_the_whole_exception_chain()
    {
        var at = new DateTime(2026, 10, 3, 12, 34, 56, DateTimeKind.Utc);
        string text = CrashLog.Format("gridsim", Thrown(), new[] { "nminus1", "case9" }, at);

        Assert.Contains("app:     gridsim", text, StringComparison.Ordinal);
        Assert.Contains("time:    2026-10-03 12:34:56 UTC", text, StringComparison.Ordinal);
        Assert.Contains($"build:   {BuildInfo.InformationalVersion}", text, StringComparison.Ordinal);
        Assert.Contains("args:    nminus1 case9", text, StringComparison.Ordinal);
        Assert.Contains("System.ArgumentException: outer failure", text, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException: inner cause", text, StringComparison.Ordinal);
        Assert.Contains(nameof(Thrown), text, StringComparison.Ordinal);   // the stack is there
    }

    [Fact]
    public void Format_handles_no_args_and_a_non_exception_object()
    {
        string text = CrashLog.Format("GridSim.Wpf", "not an exception", null, DateTime.UnixEpoch);
        Assert.Contains("args:    (none)", text, StringComparison.Ordinal);
        Assert.Contains("non-exception object thrown: System.String: not an exception", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_creates_a_uniquely_named_log_in_the_given_folder()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-crashlog-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            string? path = CrashLog.Write("gridsim", Thrown(), new[] { "fault", "case9" }, dir);
            Assert.NotNull(path);
            Assert.Equal(Path.GetFullPath(dir), Path.GetFullPath(Path.GetDirectoryName(path)!));
            Assert.Matches(new Regex($@"^gridsim-crash-\d{{8}}-\d{{6}}-{Environment.ProcessId}\.log$"), Path.GetFileName(path));
            string text = File.ReadAllText(path!);
            Assert.Contains("outer failure", text, StringComparison.Ordinal);
            Assert.Contains("args:    fault case9", text, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [UnixFact("Unix file modes")]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void On_unix_the_log_and_a_folder_it_creates_are_owner_only()
    {
        // GR-839: the fallback is the shared /tmp on Linux and macOS, so the log (arguments and stack) is 0600 and a
        // folder the writer creates is 0700. Windows keeps its per-user ACLs and has no Unix mode to check (skipped there).
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-crashlog-mode-" + Guid.NewGuid().ToString("N"));
        try
        {
            string? path = CrashLog.Write("gridsim", Thrown(), null, dir);
            Assert.NotNull(path);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path!));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Write_falls_back_to_the_temp_folder_when_the_given_folder_is_unusable()
    {
        // A folder path that is actually an existing FILE cannot be created as a directory: the log must still be
        // written (to the temp fallback), never lost and never a second exception.
        string blocker = Path.Combine(Path.GetTempPath(), "gridsim-crashlog-blocker-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "not a folder");
        string? path = null;
        try
        {
            path = CrashLog.Write("gridsim", Thrown(), null, blocker);
            Assert.NotNull(path);
            Assert.StartsWith(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "GridSim", "crash")),
                Path.GetFullPath(path!), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(blocker);
            if (path is not null && File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Default_directory_is_under_the_user_data_folder()
    {
        // Only checked when the override variable is not set in this environment (it is process-wide, so this test
        // never sets it). %LOCALAPPDATA%\GridSim\crash on Windows, ~/.local/share/GridSim/crash on Linux.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(CrashLog.DirectoryVariable))) return;
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string expected = string.IsNullOrEmpty(local)
            ? Path.Combine(Path.GetTempPath(), "GridSim", "crash")
            : Path.Combine(local, "GridSim", "crash");
        Assert.Equal(expected, CrashLog.DefaultDirectory());
    }

    [Fact]
    public void Cli_crash_handler_reports_then_still_crashes_loudly()
    {
        // Source-level guard on the CLI's first UnhandledException handler: missing files and unloadable cases stay
        // exit 1; everything else goes to CrashReport.Write and the handler ends WITHOUT Environment.Exit, so the
        // runtime still prints the stack and exits with its crash code (a defect must never look like exit 0 or 1).
        string src = File.ReadAllText(Path.Combine(Root(), "src", "GridSim.Cli", "Program.cs")).Replace("\r\n", "\n");
        int start = src.IndexOf("AppDomain.CurrentDomain.UnhandledException += (_, e) =>", StringComparison.Ordinal);
        Assert.True(start >= 0, "the CLI's first UnhandledException handler was not found");
        int end = src.IndexOf("\n};", start, StringComparison.Ordinal);
        string handler = src[start..end];
        int report = handler.IndexOf("CrashReport.Write(e.ExceptionObject, args);", StringComparison.Ordinal);
        Assert.True(report > 0, "the handler must call CrashReport.Write for an unexpected exception");
        Assert.DoesNotContain("Environment.Exit", handler[report..], StringComparison.Ordinal);
        Assert.Contains("is FileNotFoundException or DirectoryNotFoundException or CaseLoadException", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void Wpf_app_registers_all_three_crash_handlers_at_startup()
    {
        string src = File.ReadAllText(Path.Combine(Root(), "src", "GridSim.Wpf", "App.xaml.cs"));
        Assert.Contains("DispatcherUnhandledException += OnDispatcherUnhandledException;", src, StringComparison.Ordinal);
        Assert.Contains("AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;", src, StringComparison.Ordinal);
        Assert.Contains("TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;", src, StringComparison.Ordinal);
        Assert.Contains("CrashLog.Write(\"GridSim.Wpf\"", src, StringComparison.Ordinal);
    }
}
