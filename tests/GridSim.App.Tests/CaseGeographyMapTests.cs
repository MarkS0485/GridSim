using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GridSim.App.Dashboard;
using GridSim.App.Map;
using GridSim.App.Shell;
using GridSim.Core.Licensing;
using GridSim.Ui.Models;
using GridSim.Ui.Shell;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// A case's own map geography in the window, on the headless platform. A case folder with a valid geography.json draws
/// that geography (its land in place of the GB coastline, its regions on the DNO toggle, no GB layer) and names the case
/// in the title, header and map panel; a case folder with a malformed one falls back to the GB drawing and says so in
/// the status bar; and a case without one is exactly the GB window as before. The cases are copies of the bundled
/// reduced GB case (data/uk) in a temporary folder, with a synthetic geography.json written by the test.
/// </summary>
public sealed class CaseGeographyMapTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string RepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "GridSim.slnx"))) return d.FullName;
        throw new InvalidOperationException("The GridSim checkout (GridSim.slnx) was not found above " + AppContext.BaseDirectory);
    }

    /// <summary>A copy of data/uk in <paramref name="temp"/>/<paramref name="name"/>, with <paramref name="geography"/>
    /// written beside its buses.json when it is not null.</summary>
    private static ModelSource CaseCopy(TempFolder temp, string name, string? geography)
    {
        string src = Path.Combine(RepoRoot(), "data", "uk");
        Assert.True(File.Exists(Path.Combine(src, "system.json")), "missing test case " + src);
        string dir = temp.File(name);
        Directory.CreateDirectory(dir);
        foreach (string f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
        Assert.False(File.Exists(Path.Combine(dir, "geography.json")), "data/uk must carry no geography.json");
        if (geography is not null) File.WriteAllText(Path.Combine(dir, "geography.json"), geography);
        return new ModelSource(name, dir);
    }

    // A synthetic gda-geography/1 file: box S 49, W -9, N 60, E 3, margin 0.03, one land ring and two regions.
    private static string Geography(string format = "gda-geography/1")
    {
        const double s = 49, w = -9, n = 60, e = 3, m = 0.03;
        string Pt(double lon, double lat)
        {
            double x = m + (1 - 2 * m) * (lon - w) / (e - w);
            double y = m + (1 - 2 * m) * (lat - s) / (n - s);
            return string.Create(Inv, $"[{lon}, {lat}, {Math.Round(x, 5)}, {Math.Round(y, 5)}]");
        }
        string Ring(params (double Lon, double Lat)[] p) => "[" + string.Join(", ", p.Select(q => Pt(q.Lon, q.Lat))) + "]";
        string land = Ring((-6, 50), (2, 50), (2, 59), (-6, 59));
        string ra = Ring((-6, 50), (-2, 50), (-2, 59), (-6, 59));
        string rb = Ring((-2, 50), (2, 50), (2, 59), (-2, 59));
        return string.Create(Inv,
            $"{{\"format\": \"{format}\", \"projection\": {{\"kind\": \"lonlat-affine\", \"south\": {s}, \"west\": {w}, \"north\": {n}, \"east\": {e}, \"margin\": {m}}}, " +
            $"\"point_layout\": [\"lon\", \"lat\", \"x\", \"y\"], \"land\": [{{\"name\": \"Testland\", \"rings\": [{land}]}}], " +
            $"\"regions\": [{{\"name\": \"West\", \"iso\": \"TL-W\", \"rings\": [{ra}]}}, {{\"name\": \"East\", \"iso\": \"TL-E\", \"rings\": [{rb}]}}], \"sources\": []}}");
    }

    private static ShellOptions Options(TempFolder temp, IReadOnlyList<ModelSource> cases) => new()
    {
        SettingsStore = ShellSettingsStore.ForFile(temp.File("settings.json")),
        DiscoverCases = (_, _) => cases.ToList(),
        LicenceDescription = () => "Test licence",
        Licence = () => new LicenceGate(Entitlement.FreeDefault),
        UseNativeMenu = false,
        AutoLoadAtStart = false,
        LoadGeography = _ => null,
    };

    private static void Await(Task task)
    {
        var clock = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            if (clock.ElapsedMilliseconds > 120_000) throw new TimeoutException("Timed out waiting for a case to load");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(2);
        }
        Dispatcher.UIThread.RunJobs();
        task.GetAwaiter().GetResult();
    }

    private static void Close(MainWindow w)
    {
        w.Close();
        if (w.StopLoop is { } stop) Await(stop);
    }

    private static string GbTitle(MainWindow w) => $"{MainWindowViewModel.TitleStem}  -  {w.ViewModel.Run.LicenceTierText}";

    private static string PanelHeader(MainWindow w) =>
        w.FindControl<DashboardView>("NetworkDashboard")!.FindControl<TextBlock>("NetworkPanelHeader")!.Text ?? "";

    [AvaloniaFact]
    public void A_case_with_its_own_geography_draws_it_and_is_named_and_a_case_without_one_is_GB_as_before()
    {
        using var temp = new TempFolder();
        ModelSource own = CaseCopy(temp, "testland", Geography());
        ModelSource plain = CaseCopy(temp, "plain", null);
        var w = new MainWindow(Options(temp, new[] { own, plain }));
        w.Show();
        try
        {
            SchematicControl map = w.FindControl<SchematicControl>("Schematic")!;

            // Before any case: the GB window.
            Assert.False(map.HasCaseGeography);
            Assert.Equal(GbTitle(w), w.Title);
            Assert.Equal(MainWindowViewModel.GbHeaderSubtitle, w.ViewModel.HeaderSubtitle);
            Assert.Equal(DashboardView.GbNetworkHeader, PanelHeader(w));

            // The case with its own geography: drawn, its regions are the geography layer, and it is named.
            Await(w.ViewModel.LoadCaseAsync(own));
            Assert.False(w.ViewModel.Run.HasError, w.ViewModel.Run.ErrorText);
            Assert.True(map.HasCaseGeography);
            Assert.True(map.HasGeography);   // its two regions (no GB geography was given to this window)
            Assert.Null(w.ViewModel.ProblemText);
            string name = w.ViewModel.Run.LoadedModel!.Name;
            Assert.Equal("GB reduced (10-zone)", name);   // the copied system.json's name
            Assert.Equal($"GridSim  -  {name}  -  {w.ViewModel.Run.LicenceTierText}", w.Title);
            Assert.Equal("    TESTLAND TRANSMISSION CONTROL", w.ViewModel.HeaderSubtitle);
            Assert.Equal("TESTLAND TRANSMISSION NETWORK  LIVE POWER FLOW", PanelHeader(w));
            Assert.True(w.FindControl<Button>("DnoButton")!.IsEnabled);
            Assert.False(w.FindControl<Button>("GspRegionButton")!.IsEnabled);   // GSP regions are GB only

            // The coastline and region layers can be drawn over it without fault (headless render).
            map.ShowCoast = true;
            map.ShowDnoAreas = true;
            map.ShowMinimap = true;
            Dispatcher.UIThread.RunJobs();
            w.CaptureRenderedFrame()?.Dispose();
            Dispatcher.UIThread.RunJobs();

            // The case without one: the GB window again, exactly.
            Await(w.ViewModel.LoadCaseAsync(plain));
            Assert.False(w.ViewModel.Run.HasError, w.ViewModel.Run.ErrorText);
            Assert.False(map.HasCaseGeography);
            Assert.False(map.HasGeography);
            Assert.Equal(GbTitle(w), w.Title);
            Assert.Equal(MainWindowViewModel.GbHeaderSubtitle, w.ViewModel.HeaderSubtitle);
            Assert.Equal(DashboardView.GbNetworkHeader, PanelHeader(w));
            Assert.Null(w.ViewModel.ProblemText);
        }
        finally { Close(w); }
    }

    [AvaloniaFact]
    public void A_malformed_geography_file_falls_back_to_GB_and_says_why_and_a_good_load_clears_it()
    {
        using var temp = new TempFolder();
        ModelSource bad = CaseCopy(temp, "bad", Geography(format: "gda-geography/9"));
        ModelSource plain = CaseCopy(temp, "plain", null);
        var w = new MainWindow(Options(temp, new[] { bad, plain }));
        w.Show();
        try
        {
            SchematicControl map = w.FindControl<SchematicControl>("Schematic")!;

            // The case itself loads and runs; only its geography is refused, loudly, and the map is the GB drawing.
            Await(w.ViewModel.LoadCaseAsync(bad));
            Assert.False(w.ViewModel.Run.HasError, w.ViewModel.Run.ErrorText);
            Assert.False(map.HasCaseGeography);
            Assert.Equal(
                MainWindow.CaseGeographyProblemPrefix + "geography.json: format is \"gda-geography/9\"; only \"gda-geography/1\" can be read",
                w.ViewModel.ProblemText);
            Assert.True(w.ViewModel.HasProblem);
            Assert.Equal(GbTitle(w), w.Title);
            Assert.Equal(MainWindowViewModel.GbHeaderSubtitle, w.ViewModel.HeaderSubtitle);
            Assert.Equal(DashboardView.GbNetworkHeader, PanelHeader(w));

            // The next case loads cleanly: that case's problem no longer applies, so it is cleared.
            Await(w.ViewModel.LoadCaseAsync(plain));
            Assert.Null(w.ViewModel.ProblemText);
        }
        finally { Close(w); }
    }
}
