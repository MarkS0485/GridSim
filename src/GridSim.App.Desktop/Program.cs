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
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    /// <summary>The app builder; also the entry the Avalonia previewer in an IDE looks for, so keep the name.</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<GridSim.App.App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
