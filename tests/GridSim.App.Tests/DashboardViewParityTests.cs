using System.Reflection;
using System.Text.RegularExpressions;
using GridSim.Ui.Dashboard;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// UI programme Phase 1.4: the Avalonia dashboard (src/GridSim.App/Dashboard/DashboardView.axaml) binds every
/// dashboard property the WPF window (src/GridSim.Wpf/MainWindow.xaml) binds, and shows every fixed text the WPF
/// NETWORK tab shows. Both files are read at test time, so a tile added to the WPF dashboard (while both apps ship)
/// fails here until it is ported or listed below as deliberately omitted, with its reason.
///
/// <para>The WPF dashboard is the header's status lamp and clock and the NETWORK tab (the three columns and the map
/// overlays), plus the shared STATE NOT VALID banner. The header's pickers (their item templates bind
/// <c>Label</c> of the picker rows, not the dashboard) and the other tabs are not dashboard tiles.</para>
///
/// <para>WPF binds adapter names (a brush for each colour token, a Visibility for each bool, from
/// src/GridSim.Wpf/DashboardViewModel.cs); the Avalonia view binds the neutral DashboardModel property behind each,
/// through a converter or straight to IsVisible. <see cref="NeutralName"/> is that mapping, and every name it gives is
/// checked to be a real property of the model or of a row type, so the mapping cannot hide a typo.</para>
/// </summary>
public sealed class DashboardViewParityTests
{
    private static string RepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "GridSim.slnx"))) return d.FullName;
        throw new InvalidOperationException("The GridSim checkout (GridSim.slnx) was not found above " + AppContext.BaseDirectory);
    }

    private static string Read(params string[] parts)
    {
        string path = Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray());
        Assert.True(File.Exists(path), "missing " + path);
        return File.ReadAllText(path);
    }

    /// <summary>
    /// Dashboard properties MainWindow.xaml binds that DashboardView.axaml deliberately does not, each with the reason.
    /// Empty: every one is ported. (The map's hover tooltip is filled by the map's code, not by a binding, so it is not
    /// in this list; it moves with the map control, Phase 1.5.)
    /// </summary>
    private static readonly Dictionary<string, string> DeliberatelyOmitted = new(StringComparer.Ordinal);

    /// <summary>The WPF adapter property and the neutral property it stands for (DashboardViewModel.AdapterOf, plus
    /// the row adapters: BoundaryRow.Bar, AlarmRow.Brush, ViolationRow.Brush, VoltageBandDial.Brush).</summary>
    private static readonly Dictionary<string, string> AdapterToNeutral = new(StringComparer.Ordinal)
    {
        ["FreqBrush"] = "FreqColour",
        ["InertiaNoteBrush"] = "InertiaNoteColour",
        ["StatusBrush"] = "StatusColour",
        ["QueryVerdictBrush"] = "QueryVerdictColour",
        ["FragilityBrush"] = "FragilityColour",
        ["FragilityTrendBrush"] = "FragilityTrendColour",
        ["BridgeStatusBrush"] = "BridgeStatusColour",
        ["StabilityBrush"] = "StabilityColour",
        ["TripVerdictBrush"] = "TripVerdictColour",
        ["LegendBrush"] = "Legend",
        ["InvalidStateVis"] = "InvalidState",
        ["SelectionVis"] = "SelectionVisible",
        ["QueryVis"] = "QueryVisible",
        ["QueryViolationsEmptyVis"] = "QueryViolationsEmpty",
        ["HudVis"] = "HudVisible",
        ["BridgeStatusVis"] = "BridgeStatusVisible",
        ["AlarmsEmptyVis"] = "AlarmsEmpty",
        ["Bar"] = "BarColour",       // BoundaryRow.Bar
        ["Brush"] = "Colour",        // AlarmRow / ViolationRow / VoltageBandDial .Brush
    };

    private static string NeutralName(string wpf) => AdapterToNeutral.TryGetValue(wpf, out string? n) ? n : wpf;

    /// <summary>The types a dashboard binding resolves against: the model and every row type its lists hold.</summary>
    private static readonly Type[] BoundTypes =
    {
        typeof(DashboardModel), typeof(BoundaryRowModel), typeof(VoltageBandDialModel), typeof(AlarmEntry),
        typeof(ViolationEntry), typeof(KeyVal),
    };

    // {Binding}, {Binding Name}, {Binding Path=Name, ...}; "." stands for the bare {Binding} (the event-log line).
    private static readonly Regex BindingRx = new(@"\{Binding(?:\s+(?:Path=)?(?<p>[A-Za-z_][A-Za-z0-9_.]*))?\s*[,}]", RegexOptions.CultureInvariant);

    private static HashSet<string> Bindings(string xaml) =>
        BindingRx.Matches(xaml).Select(m => m.Groups["p"].Success ? m.Groups["p"].Value : ".").ToHashSet(StringComparer.Ordinal);

    /// <summary>The slice of <paramref name="text"/> from <paramref name="from"/> to the first <paramref name="to"/> after it.</summary>
    private static string Slice(string text, string from, string to)
    {
        int a = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(a >= 0, $"MainWindow.xaml no longer contains '{from}': update this test's view of the WPF dashboard");
        int b = text.IndexOf(to, a, StringComparison.Ordinal);
        Assert.True(b > a, $"MainWindow.xaml: no '{to}' after '{from}'");
        return text.Substring(a, b - a);
    }

    /// <summary>The WPF dashboard's XAML: the header without its pickers, the NETWORK tab, and the shared banner.</summary>
    private static (string Header, string Network, string Banner) WpfDashboard()
    {
        string wpf = Read("src", "GridSim.Wpf", "MainWindow.xaml");
        string header = Slice(wpf, "<!-- ================= HEADER", "<!-- ================= MAIN");
        // The pickers' item templates bind Label of their rows (sources, solvers), not the dashboard.
        header = Regex.Replace(header, @"<ComboBox\b.*?</ComboBox>", "", RegexOptions.Singleline | RegexOptions.CultureInvariant);
        string network = Slice(wpf, "<TabItem Header=\"NETWORK\"", "</TabItem>");
        string banner = Slice(wpf, "<!-- Shared banner over every tab", "<!-- ============ GLOBAL TRANSPORT BAR");
        return (header, network, banner);
    }

    private static string AvaloniaDashboard() => Read("src", "GridSim.App", "Dashboard", "DashboardView.axaml");

    [Fact]
    public void Every_dashboard_property_the_WPF_window_binds_is_bound_by_the_Avalonia_dashboard()
    {
        (string header, string network, string banner) = WpfDashboard();
        HashSet<string> wpf = Bindings(header + network + banner);
        HashSet<string> avalonia = Bindings(AvaloniaDashboard());

        // The WPF view of the dashboard is not empty (the slicing found the real tiles).
        foreach (string known in new[] { "Status", "StatusBrush", "Clock", "Tick", "Freq", "FreqAngle", "Demand", "Boundaries", "Alarms", "Events", "InvalidStateVis" })
            Assert.Contains(known, wpf);

        var missing = new List<string>();
        foreach (string w in wpf.OrderBy(s => s, StringComparer.Ordinal))
        {
            string neutral = NeutralName(w);
            // Every neutral name is a real property of the model or a row type (or the bare {Binding} of a string row).
            Assert.True(neutral == "." || BoundTypes.Any(t => t.GetProperty(neutral, BindingFlags.Public | BindingFlags.Instance) is not null),
                $"WPF binding {w} maps to {neutral}, which is not a property of the dashboard model or its rows");
            if (avalonia.Contains(neutral)) continue;
            if (DeliberatelyOmitted.ContainsKey(neutral)) continue;
            missing.Add($"{w} (neutral {neutral})");
        }
        Assert.True(missing.Count == 0,
            "DashboardView.axaml does not bind these WPF dashboard properties (port them, or list them in DeliberatelyOmitted with the reason): "
            + string.Join(", ", missing));

        // An omission is only listed while it is still omitted.
        foreach (string omitted in DeliberatelyOmitted.Keys)
            Assert.DoesNotContain(omitted, avalonia);
    }

    [Fact]
    public void Every_fixed_text_of_the_WPF_NETWORK_tab_and_banner_is_on_the_Avalonia_dashboard()
    {
        (_, string network, string banner) = WpfDashboard();
        string avalonia = AvaloniaDashboard();
        var literal = new Regex("\\s(?:Text|Content|ToolTip)=\"(?<t>[^\"{][^\"]*)\"", RegexOptions.CultureInvariant);

        var texts = literal.Matches(network + banner).Select(m => m.Groups["t"].Value).Distinct(StringComparer.Ordinal).ToList();
        Assert.Contains("SYSTEM FREQUENCY", texts);   // the slice found the tiles
        Assert.Contains("STATE NOT VALID  -  the power flow did not converge; every value on this tab comes from an unconverged iterate", texts);

        // Avalonia writes a tooltip as ToolTip.Tip; every other literal keeps its WPF attribute name.
        var missing = texts.Where(t =>
            !avalonia.Contains($"Text=\"{t}\"", StringComparison.Ordinal)
            && !avalonia.Contains($"Content=\"{t}\"", StringComparison.Ordinal)
            && !avalonia.Contains($"ToolTip.Tip=\"{t}\"", StringComparison.Ordinal)).ToList();
        Assert.True(missing.Count == 0, "Fixed WPF dashboard texts not on DashboardView.axaml: " + string.Join(" | ", missing));
    }
}
