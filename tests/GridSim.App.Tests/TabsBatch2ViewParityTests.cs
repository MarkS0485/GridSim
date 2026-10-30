using System.Reflection;
using System.Text.RegularExpressions;
using GridSim.Ui.Dashboard;
using GridSim.Ui.Tabs.Inspector;
using GridSim.Ui.Tabs.InverterFleet;
using GridSim.Ui.Tabs.Machine;
using GridSim.Ui.Tabs.MachineEngineer;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// UI programme Phase 1.6 (second tab batch): each ported view (src/GridSim.App/Tabs/*.axaml) binds every property its
/// WPF view (src/GridSim.Wpf/Views/*.xaml) binds and shows every fixed text it shows. Both files are read at test time,
/// so a read-out or label added to a WPF tab (while both apps ship) fails here until it is ported or listed as
/// deliberately not bound, with the reason.
///
/// <para>WPF binds adapter names (a brush for each colour token, a Visibility for each bool, a command for each
/// action); the Avalonia view binds the neutral property behind each, through a converter or straight to IsVisible, and
/// handles each command as a click. The per-view maps below are those correspondences, and every neutral name they give
/// is checked to be a real property of the tab's model or one of its row types, so a map cannot hide a typo.</para>
/// </summary>
public sealed class TabsBatch2ViewParityTests
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

    /// <summary>One WPF tab and its port: the files, the WPF adapter names and their neutral properties, the WPF
    /// bindings deliberately not bound in the AXAML (with the reason), and the types its bindings resolve against.</summary>
    private sealed record ViewPair(
        string Wpf, string Avalonia, Dictionary<string, string> AdapterToNeutral, Dictionary<string, string> NotBound, Type[] BoundTypes);

    private static readonly ViewPair[] Pairs =
    {
        new("GenerationView.xaml", "GenerationView.axaml",
            new(StringComparer.Ordinal)
            {
                ["Color"] = "Colour",                  // FuelRow.Color
                ["Bar"] = "BarColour",                 // ImportLinkRow.Bar
                ["BadgeBrush"] = "BadgeColour",        // ImportLinkRow.BadgeBrush
                ["StabilityBrush"] = "StabilityColour",
                ["TripVerdictBrush"] = "TripVerdictColour",
            },
            new(StringComparer.Ordinal),
            new[] { typeof(DashboardModel), typeof(FuelRowModel), typeof(ImportLinkRowModel) }),
        new("MachineView.xaml", "MachineView.axaml",
            new(StringComparer.Ordinal)
            {
                ["Color"] = "Colour",                  // StreamRow / VoltageLayer / TapRow .Color
                ["DevBrush"] = "DevColour",
                ["DivBrush"] = "DivColour",
                ["AccumBrush"] = "AccumColour",
                ["BessBrush"] = "BessColour",
                ["VoltBrush"] = "VoltColour",
                ["QBrush"] = "QColour",
            },
            new(StringComparer.Ordinal)
            {
                // The WPF tab picks its view-model out of the window's dashboard; the Avalonia view's data context is the model.
                ["Machine"] = "the view's DataContext is its own MachineModel",
            },
            new[] { typeof(MachineModel), typeof(StreamRowModel), typeof(VoltageLayerModel), typeof(TapRowModel) }),
        new("MachineEngineerView.xaml", "MachineEngineerView.axaml",
            new(StringComparer.Ordinal)
            {
                ["RiskBrush"] = "RiskColour",
                ["Swatch"] = "SwatchColour",
                ["RampBrush"] = "RampColour",
                ["RocofLegacyBrush"] = "RocofLegacyColour",
                ["RocofModernBrush"] = "RocofModernColour",
                ["ProjBrush"] = "ProjColour",
            },
            new(StringComparer.Ordinal)
            {
                ["Mechanical"] = "the view's DataContext is its own MachineEngineerModel",
                ["Inspector"] = "the overlay's model is set in code-behind (Inspector.Model = Model.Inspector)",
                ["DataContext.InspectMachineCommand"] = "a row's click handler calls MachineEngineerModel.InspectMachine",
                ["."] = "the click handler reads the row from the button's DataContext (the WPF CommandParameter)",
            },
            new[] { typeof(MachineEngineerModel), typeof(MachineRowModel), typeof(StartRowModel) }),
        new("InverterFleetView.xaml", "InverterFleetView.axaml",
            new(StringComparer.Ordinal)
            {
                ["TrippedBrush"] = "TrippedColour",
                ["ModeBrush"] = "ModeColour",
            },
            new(StringComparer.Ordinal)
            {
                ["InverterFleet"] = "the view's DataContext is its own InverterFleetModel",
            },
            new[] { typeof(InverterFleetModel), typeof(InverterFleetRowModel) }),
        new("InspectorView.xaml", "InspectorView.axaml",
            new(StringComparer.Ordinal)
            {
                ["Visibility"] = "IsOpen",
                ["BackVisibility"] = "CanBack",
                ["Chevron"] = "HasDrill",
                ["ValueBrush"] = "ValueColour",
                ["Swatch"] = "SwatchColour",
            },
            new(StringComparer.Ordinal)
            {
                ["BackCommand"] = "the back button's click handler calls InspectorModel.Back",
                ["CloseCommand"] = "the close button's click handler calls InspectorModel.Close",
                ["Drill"] = "a row's click handler calls InspectRowModel.DrillIn",
            },
            new[] { typeof(InspectorModel), typeof(InspectStatModel), typeof(InspectRowModel) }),
    };

    public static TheoryData<string> Views => new() { "GenerationView", "MachineView", "MachineEngineerView", "InverterFleetView", "InspectorView" };

    private static ViewPair PairOf(string view) => Pairs.Single(p => p.Wpf == view + ".xaml");

    // {Binding}, {Binding Name}, {Binding Path=Name, ...}; "." stands for the bare {Binding}.
    private static readonly Regex BindingRx = new(@"\{Binding(?:\s+(?:Path=)?(?<p>[A-Za-z_][A-Za-z0-9_.]*))?\s*[,}]", RegexOptions.CultureInvariant);

    private static HashSet<string> Bindings(string xaml) =>
        BindingRx.Matches(xaml).Select(m => m.Groups["p"].Success ? m.Groups["p"].Value : ".").ToHashSet(StringComparer.Ordinal);

    [Theory]
    [MemberData(nameof(Views))]
    public void Every_property_the_WPF_tab_binds_is_bound_by_its_Avalonia_view(string view)
    {
        ViewPair pair = PairOf(view);
        HashSet<string> wpf = Bindings(Read("src", "GridSim.Wpf", "Views", pair.Wpf));
        HashSet<string> avalonia = Bindings(Read("src", "GridSim.App", "Tabs", pair.Avalonia));
        Assert.True(wpf.Count >= 5, $"{pair.Wpf}: only {wpf.Count} bindings found; the reader no longer sees the WPF tab");

        var missing = new List<string>();
        foreach (string w in wpf.OrderBy(s => s, StringComparer.Ordinal))
        {
            if (pair.NotBound.ContainsKey(w)) continue;
            // A dotted path (Selected.Name) maps its last step; the first step must be a model property.
            string neutral = string.Join(".", w.Split('.').Select(step => pair.AdapterToNeutral.TryGetValue(step, out string? n) ? n : step));
            string last = neutral.Split('.')[^1], first = neutral.Split('.')[0];
            Assert.True(pair.BoundTypes.Any(t => t.GetProperty(first, BindingFlags.Public | BindingFlags.Instance) is not null),
                $"{pair.Wpf}: binding {w} maps to {neutral}, whose first step is not a property of the tab model or its rows");
            Assert.True(pair.BoundTypes.Any(t => t.GetProperty(last, BindingFlags.Public | BindingFlags.Instance) is not null),
                $"{pair.Wpf}: binding {w} maps to {neutral}, whose last step is not a property of the tab model or its rows");
            if (!avalonia.Contains(neutral)) missing.Add($"{w} (neutral {neutral})");
        }
        Assert.True(missing.Count == 0, $"{pair.Avalonia} does not bind these WPF bindings (port them, or list them with the reason): "
            + string.Join(", ", missing));

        // A binding listed as not bound is really in the WPF tab and really absent from the port.
        foreach (string notBound in pair.NotBound.Keys)
        {
            Assert.Contains(notBound, wpf);
            Assert.DoesNotContain(notBound, avalonia);
        }
    }

    [Theory]
    [MemberData(nameof(Views))]
    public void Every_fixed_text_of_the_WPF_tab_is_on_its_Avalonia_view(string view)
    {
        ViewPair pair = PairOf(view);
        string wpf = Read("src", "GridSim.Wpf", "Views", pair.Wpf);
        string avalonia = Read("src", "GridSim.App", "Tabs", pair.Avalonia);
        var literal = new Regex("\\s(?:Text|Content|ToolTip|Caption|Unit)=\"(?<t>[^\"{][^\"]*)\"", RegexOptions.CultureInvariant);
        var texts = literal.Matches(wpf).Select(m => m.Groups["t"].Value).Distinct(StringComparer.Ordinal).ToList();
        Assert.NotEmpty(texts);
        // Avalonia writes a tooltip as ToolTip.Tip; every other literal keeps its WPF attribute name.
        var missing = texts.Where(t =>
            !avalonia.Contains($"Text=\"{t}\"", StringComparison.Ordinal)
            && !avalonia.Contains($"Content=\"{t}\"", StringComparison.Ordinal)
            && !avalonia.Contains($"ToolTip.Tip=\"{t}\"", StringComparison.Ordinal)
            && !avalonia.Contains($"Caption=\"{t}\"", StringComparison.Ordinal)
            && !avalonia.Contains($"Unit=\"{t}\"", StringComparison.Ordinal)).ToList();
        Assert.True(missing.Count == 0, $"Fixed WPF texts of {pair.Wpf} not on {pair.Avalonia}: " + string.Join(" | ", missing));
    }

    [Theory]
    [MemberData(nameof(Views))]
    public void Each_view_code_behind_documents_its_one_line_feed(string view)
    {
        // Test retargeted 2026-10-20 (UI programme integration): the tabs are now wired into the main window, and THE
        // MACHINE, MECHANICAL and INVERTER FLEET no longer feed themselves (their documented "<view>.Attach(viewModel.Run)"
        // line and the Attach method are gone): each is bound in MainWindow.axaml to the TabModels model the dashboard's
        // tab hooks feed, and GENERATION & INERTIA is given the window's dashboard model in MainWindow.axaml.cs. The
        // assertions are unchanged in kind (the code-behind starts with its feed note and documents its one line, the
        // header now carrying the integration date) and strengthened: the documented line must also be the wiring the
        // window really has (the same element in MainWindow.axaml, the same statement in MainWindow.axaml.cs), and a
        // view fed by the hooks must not keep any way of feeding itself (no Attach, no TabRunFeed).
        string code = Read("src", "GridSim.App", "Tabs", view + ".axaml.cs");
        Assert.StartsWith("// HOW TO FEED (UI programme Phase 1.6; integrated 2026-10-20).", code, StringComparison.Ordinal);
        string windowXaml = Read("src", "GridSim.App", "MainWindow.axaml");
        string windowCode = Read("src", "GridSim.App", "MainWindow.axaml.cs");
        (string documented, string wired, string where) = view switch
        {
            "GenerationView" => ("//     GenerationTab.Model = viewModel.Dashboard;", "GenerationTab.Model = _vm.Dashboard;", windowCode),
            "InspectorView" => ("//     InspectorView.Model = machineEngineerView.Model.Inspector;", "Inspector.Model = m.Inspector;",
                Read("src", "GridSim.App", "Tabs", "MachineEngineerView.axaml.cs")),
            "MachineView" => Bound("MachineView", "MachineTab", "Machine"),
            "MachineEngineerView" => Bound("MachineEngineerView", "MechanicalTab", "Mechanical"),
            "InverterFleetView" => Bound("InverterFleetView", "InverterFleetTab", "InverterFleet"),
            _ => throw new ArgumentOutOfRangeException(nameof(view), view, "not a second-batch view"),
        };
        Assert.Contains(documented, code, StringComparison.Ordinal);
        Assert.Contains(wired, where, StringComparison.Ordinal);
        Assert.DoesNotContain("TabRunFeed", code, StringComparison.Ordinal);
        Assert.DoesNotContain("public void Attach(", code, StringComparison.Ordinal);

        // A hook-fed tab: documented as the window's element, and the window has exactly that element.
        (string, string, string) Bound(string type, string name, string tab)
        {
            string element = $"<tabs:{type} x:Name=\"{name}\" DataContext=\"{{Binding Tabs.{tab}}}\" />";
            return ("//     " + element, element, windowXaml);
        }
    }
}
