using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using GridSim.Core.Licensing;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>UI programme 0.4: the skeleton app starts and its main window names the build.</summary>
public sealed class MainWindowTests
{
    [AvaloniaFact]
    public void The_app_starts_and_the_main_window_shows_the_title_and_the_build_line()
    {
        // The headless platform started the real App (TestAppBuilder), with its theme loaded.
        Assert.IsType<App>(Application.Current);
        Assert.NotEmpty(Application.Current!.Styles);

        // Test retargeted 2026-10-20 (UI programme 1.8): the window now loads a source by itself when it opens, as WPF
        // does, and the default window would load the last source in the user's own settings file (perhaps a live
        // bridge, a network connect no test may make). This test is about the title and the build line of the window
        // as built, so it turns the start-up load off (ShellOptions.AutoLoadAtStart); every other option is the
        // default, as before, and every assertion is unchanged.
        var window = new MainWindow(new GridSim.App.Shell.ShellOptions { AutoLoadAtStart = false });
        window.Show();
        try
        {
            Assert.True(window.IsVisible);
            // Test retargeted 2026-10-20 (UI programme 1.7): the title is now the WPF window's full title, its XAML title
            // then the version and the licence tier (WPF MainWindow.OnLoaded), not plain "GridSim". Stronger: it still
            // starts with "GridSim", and the whole text is pinned, character for character, in the WPF format.
            string tier = LicenceGate.Resolve().Entitlement.Tier.DisplayName();
            Assert.StartsWith("GridSim", window.Title, StringComparison.Ordinal);
            Assert.Equal($"GridSim  -  GB Transmission Control  -  v{GridSim.Core.BuildInfo.Version}  -  {tier} licence", window.Title);
            Assert.Matches(@"^GridSim  -  GB Transmission Control  -  v\d+\.\d+\.\d+  -  [^ ].* licence$", window.Title!);

            // Test retargeted 2026-10-20 (UI programme integration): the build line's side-panel copy (VersionText, in
            // the temporary live panel's column) is gone; the window shows the build line in its footer (VersionFooter,
            // the status bar, WPF's footer row). Every assertion on it is unchanged.
            TextBlock? version = window.FindControl<TextBlock>("VersionFooter");
            Assert.NotNull(version);
            // The exact text the WPF About box, the CLI version flag and the web footer show.
            Assert.Equal(GridSim.Core.BuildInfo.DisplayLine, version!.Text);
            // And it really names a release: "GridSim <major>.<minor>.<patch> (commit <sha or unknown>)".
            Assert.Matches(@"^GridSim \d+\.\d+\.\d+ \(commit [^)]+\)$", version.Text!);
            Assert.StartsWith($"GridSim {GridSim.Core.BuildInfo.Version} ", version.Text!, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }
}
