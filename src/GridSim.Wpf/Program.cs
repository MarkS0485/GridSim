using System.Runtime.CompilerServices;

namespace GridSim.Wpf;

/// <summary>
/// The Windows dashboard's entry point (the project's StartupObject). WPF generates its own <c>App.Main</c> from
/// App.xaml, and that would create the application before anything else could run; this one runs the telemetry guard
/// first and then hands over to the generated <c>App.Main</c> unchanged.
/// </summary>
internal static class Program
{
    /// <summary>The app's own assemblies, each of which must carry the TelemetryOff build marker.</summary>
    internal static readonly string[] AppAssemblies = ["GridSim.Wpf", "GridSim.Ui", "GridSim.Core"];

    [STAThread]
    public static void Main()
    {
        // The telemetry guard runs before anything else. It exits with code 78 (one line on stderr and the trace log)
        // when the build marker is missing, a telemetry assembly is loaded or loads later, or an exporter or
        // agent-injection variable is set; see GridSim.Core.Diagnostics.TelemetryGuard. The rest of start-up is in a
        // separate method so that nothing it touches (WPF itself) is loaded before the guard has run.
        GridSim.Core.Diagnostics.TelemetryGuard.EnforceAtStartup("GridSim.Wpf", AppAssemblies, foreignEntryAllowed: false);
        Start();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Start() => App.Main();
}
