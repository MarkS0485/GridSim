using Avalonia;

namespace GridSim.App.Desktop;

/// <summary>
/// The desktop entry point (UI programme 0.4): the standard Avalonia start-up. UsePlatformDetect picks the Windows,
/// X11 or macOS backend, WithInterFont makes the bundled Inter font the default (the same glyphs on every OS, so
/// screenshots compare across them), and LogToTrace sends Avalonia's own log to System.Diagnostics.Trace.
/// </summary>
internal static class Program
{
    // Avalonia is not ready for use until Main has built the AppBuilder: nothing may touch Avalonia before this line.
    [STAThread]
    public static int Main(string[] args)
    {
        // The telemetry guard runs before anything else. It exits with code 78 (one line on stderr and the trace log)
        // when the build marker is missing, a telemetry assembly is loaded or loads later, or an exporter variable is
        // set; see GridSim.Core.Diagnostics.TelemetryGuard. The rest of start-up is in a separate method so that
        // nothing it touches is loaded before the guard has run.
        GridSim.Core.Diagnostics.TelemetryGuard.EnforceAtStartup("GridSim.App.Desktop", AppAssemblies, foreignEntryAllowed: false);
        return Start(args);
    }

    /// <summary>The app's own assemblies, each of which must carry the TelemetryOff build marker.</summary>
    internal static readonly string[] AppAssemblies =
        ["GridSim.App.Desktop", "GridSim.App", "GridSim.Ui", "GridSim.Core", "GridSim.Scripting"];

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int Start(string[] args)
    {
        // APP-07: crash handling first, so a failure while starting is logged too (CrashLog, GRIDSIM_CRASH_DIR).
        GridSim.App.AppCrashHandling.InstallProcessHandlers();
        try { return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
#pragma warning disable CA1031 // Rethrown: logged, then the process ends as it would have.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            GridSim.App.AppCrashHandling.ReportCrash(ex);
            throw;
        }
    }

    /// <summary>The app builder; also the entry the Avalonia previewer in an IDE looks for, so keep the name.</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<GridSim.App.App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
