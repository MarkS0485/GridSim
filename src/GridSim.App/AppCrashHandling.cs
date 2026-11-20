using System.Diagnostics;
using Avalonia.Threading;
using GridSim.Core.Diagnostics;

namespace GridSim.App;

/// <summary>
/// APP-07: crash handling for the Avalonia app, as the WPF app has it (GridSim.Wpf App.xaml.cs, F08). Every unhandled
/// exception writes a crash log through <see cref="CrashLog"/> (so <c>GRIDSIM_CRASH_DIR</c> applies) and names the file
/// on the error stream and the trace log; the app then ends, because it must not carry on in a broken state. An
/// unobserved task fault is logged only (the app is still consistent), exactly as WPF does.
/// </summary>
public static class AppCrashHandling
{
    private static int _processInstalled;
    private static int _uiInstalled;
    private static int _reported;
    private static object? _lastLogged;
    private static string? _lastLoggedPath;

    /// <summary>The path of the first crash log this process wrote (null when none was written).</summary>
    public static string? LastLogPath { get; private set; }

    /// <summary>
    /// Register the process-wide handlers (any thread, and unobserved tasks). Safe before Avalonia is started and safe
    /// to call more than once.
    /// </summary>
    public static void InstallProcessHandlers()
    {
        if (Interlocked.Exchange(ref _processInstalled, 1) != 0) return;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => CrashLog.Write("GridSim.App-task", e.Exception, Environment.GetCommandLineArgs());
    }

    /// <summary>
    /// Register the UI-thread handler. Needs Avalonia to be running, so the application calls it once the framework is
    /// initialised. The exception is logged and left unhandled, so the app ends as it would without a handler.
    /// </summary>
    public static void InstallUiHandler()
    {
        if (Interlocked.Exchange(ref _uiInstalled, 1) != 0) return;
        Dispatcher.UIThread.UnhandledException += (_, e) => ReportCrash(e.Exception);
    }

    /// <summary>Write the crash log and, the first time only, say where it is. Never throws; returns the log path.</summary>
    public static string? ReportCrash(object? error, string app = "GridSim.App")
    {
        try
        {
            // The same exception can arrive twice (a start-up catch, then the domain handler): log it once.
            if (error is not null && ReferenceEquals(Interlocked.Exchange(ref _lastLogged, error), error)) return _lastLoggedPath;
            string? path = CrashLog.Write(app, error, Environment.GetCommandLineArgs());
            _lastLoggedPath = path;
            if (Interlocked.Exchange(ref _reported, 1) != 0) return path;
            LastLogPath = path;
            string what = error is Exception ex ? $"{ex.GetType().Name}: {ex.Message}" : $"{error}";
            string where = path is null ? "The crash log could not be written (no writable folder)." : $"A crash log was written to: {path}";
            string text = $"GridSim hit an internal error and has to close. This is a defect in GridSim, not in your data.{Environment.NewLine}{what}{Environment.NewLine}{where}";
            Trace.WriteLine(text);
            Console.Error.WriteLine(text);
            return path;
        }
#pragma warning disable CA1031 // Reporting a crash must never itself throw; the crash log is the record.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }
}
