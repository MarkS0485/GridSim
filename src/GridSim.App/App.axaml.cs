using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace GridSim.App;

/// <summary>
/// The Avalonia application (UI programme 0.4). It loads the theme from App.axaml and, on a desktop, opens the main
/// window. Every head (GridSim.App.Desktop, the headless tests, later the mobile heads) starts this same class.
/// </summary>
public sealed class App : Application
{
    public override void Initialize()
    {
        PinInvariantCulture();
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// D-DOC-138, as the WPF app does at start-up: pin the invariant culture for every number the app formats or
    /// parses, so a machine set to (for example) German still shows "49.98", not "49,98", and the figures read the same
    /// characters as in WPF, the CLI and the web host. The default for new threads is pinned too, so the host's loop
    /// thread agrees with the UI thread. Avalonia's own bindings format with the thread culture, so this one setting
    /// covers them (WPF needed a second, its element language).
    /// </summary>
    private static void PinInvariantCulture()
    {
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // A desktop head gets the main window; the headless test platform has no lifetime of this kind and opens the
        // window itself, so nothing is shown here in that case.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();

        base.OnFrameworkInitializationCompleted();
    }
}
