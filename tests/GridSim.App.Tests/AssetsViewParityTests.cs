using System.Reflection;
using System.Text.RegularExpressions;
using GridSim.App.Assets;
using GridSim.Ui.Assets.Navigation;
using GridSim.Ui.Assets.Pages;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// UI programme ASSETS A3: the Avalonia ASSETS tab (src/GridSim.App/Assets/AssetsView.axaml) binds every property the WPF
/// ASSETS tab binds and shows every fixed text it shows, reading both at test time, as DashboardViewParityTests and
/// TabsBatch2ViewParityTests do: the WPF side is the explorer view and its four template dictionaries
/// (src/GridSim.Wpf/Views/Assets: AssetExplorerView.xaml, GeneratorTemplates.xaml, TransformerTemplates.xaml,
/// CircuitTemplates.xaml, SubstationTemplates.xaml). A read-out or label added to a WPF asset page (while both apps ship)
/// fails here until it is ported or listed as deliberately not bound, with the reason.
///
/// <para>WPF binds adapter names (a brush for each colour token, a Visibility for each bool, a command for each action);
/// the Avalonia view binds the neutral A2 property behind each, through TokenBrush or straight to IsVisible, and handles
/// each command as a click. The maps below are those correspondences; every neutral name is checked to be a real property
/// of an A2 page or row type or of the navigator, so a map cannot hide a typo.</para>
///
/// <para>Also pinned: the D-DOC-145 STATIC DEMO tags beside the same ten panel headers and the four DEMO captions (as
/// Doc14DesktopPresentationTests pins them in WPF); a DataTemplate for every page type the Avalonia page factory makes;
/// and no placeholder page anywhere under the ASSETS folders.</para>
/// </summary>
public sealed class AssetsViewParityTests
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

    private static readonly string[] WpfFiles =
        { "AssetExplorerView.xaml", "GeneratorTemplates.xaml", "TransformerTemplates.xaml", "CircuitTemplates.xaml", "SubstationTemplates.xaml" };

    private static string Wpf(string file) => Read("src", "GridSim.Wpf", "Views", "Assets", file);

    private static string Avalonia() => Read("src", "GridSim.App", "Assets", "AssetsView.axaml");

    /// <summary>The WPF adapter property and the neutral A2 property it stands for (the WPF adapters in
    /// src/GridSim.Wpf/Views/Assets/*Pages.cs).</summary>
    private static readonly Dictionary<string, string> AdapterToNeutral = new(StringComparer.Ordinal)
    {
        ["MetricBrush"] = "MetricColour",       // AssetClassCard
        ["Bar"] = "BarColour",                  // LinkFleetRow, GenFleetRow, TransformerFleetRow, CircuitFleetRow, ConnectionRow
        ["DirectionBrush"] = "DirectionColour", // InterconnectorDetailVm
        ["FuelBrush"] = "FuelColour",           // GenFleetRow, GeneratorDetailVm, PhasorTile, PlantRow
        ["LeadLagBrush"] = "LeadLagColour",     // GeneratorDetailVm, PhasorTile
        ["Chip"] = "ChipColour",                // TransformerReading, TransformerEvent, TapChangerRow
        ["OltcBrush"] = "OltcColour",           // TransformerDetailVm
        ["Dot"] = "DotColour",                  // CircuitStatusRow
        ["QverdictBrush"] = "QverdictColour",   // CircuitDetailVm
        ["VmBrush"] = "VmColour",               // SubstationFleetRow
        ["VoltageBrush"] = "VoltageColour",     // SubstationDetailVm
        ["AssetVis"] = "AssetVisible",          // SubstationDetailVm (Visibility -> bool)
        ["MapData"] = "MapInput",               // SubstationDetailVm (the WPF map data -> the A1 geometry's input)
        ["BackVis"] = "CanGoBack",              // AssetExplorerVm (BACK is the navigator's history now)
    };

    /// <summary>WPF bindings the AXAML deliberately does not bind, each with the reason.</summary>
    private static readonly Dictionary<string, string> NotBound = new(StringComparer.Ordinal)
    {
        ["Assets"] = "the WPF view picks its explorer out of the window's view model; the Avalonia view's data context is the navigator",
        ["HomeCmd"] = "ASSETS is a click handler selecting the tree's home node (AssetNavigatorModel.Select)",
        ["BackCmd"] = "BACK is a click handler calling AssetNavigatorModel.GoBack",
        ["Open"] = "a card's or row's click handler runs its OnOpen action",
        ["Select"] = "a band chip's click handler runs its OnSelect action",
        ["Toggle"] = "a kV chip's click handler runs its OnToggle action",
        ["OpenLanding"] = "the link's click handler calls OpenLandingSubstation with its CommandParameter (LandingBusId, bound)",
        ["OpenBus"] = "the link's click handler calls OpenBusSubstation with its CommandParameter (BusId, bound)",
        ["OpenHv"] = "the link's click handler calls OpenHvSubstation with its CommandParameter (HvBusId, bound)",
        ["OpenLv"] = "the link's click handler calls OpenLvSubstation with its CommandParameter (LvBusId, bound)",
        ["OpenOltcBus"] = "the link's click handler calls OpenOltcBusSubstation with its CommandParameter (OltcBusId, bound)",
        ["OpenIn"] = "the link's click handler calls OpenInSubstation with its CommandParameter (VInBusId, bound)",
        ["OpenOut"] = "the link's click handler calls OpenOutSubstation with its CommandParameter (VOutBusId, bound)",
    };

    /// <summary>Every type a WPF asset binding resolves against, as its neutral A2 (or navigator) type.</summary>
    private static readonly Type[] BoundTypes =
    {
        typeof(AssetNavigatorModel), typeof(AssetExplorerModel), typeof(AssetPageModel),
        typeof(AssetHomeModel<AssetClassCardModel>), typeof(AssetClassCardModel),
        typeof(InterconnectorFleetModel<LinkFleetRowModel>), typeof(LinkFleetRowModel), typeof(InterconnectorDetailModel),
        typeof(GeneratorFleetModel<GenFleetRowModel>), typeof(GenFleetRowModel), typeof(GeneratorDetailModel), typeof(SubsystemRow), typeof(EventRow),
        typeof(GeneratorPhasorFleetModel<PhasorTileModel>), typeof(PhasorTileModel),
        typeof(TransformerFleetModel<TransformerFleetRowModel>), typeof(TransformerFleetRowModel),
        typeof(TransformerDetailModel<TransformerReadingModel, TransformerEventModel>), typeof(TransformerReadingModel), typeof(TransformerEventModel),
        typeof(TapChangerFleetModel<TapChangerRowModel>), typeof(TapChangerRowModel),
        typeof(CircuitFleetModel<BandChipModel, CircuitFleetRowModel>), typeof(BandChipModel), typeof(CircuitFleetRowModel),
        typeof(CircuitDetailModel<CircuitStatusRowModel>), typeof(CircuitStatusRowModel),
        typeof(SubstationFleetModel<KvFilterChipModel, SubstationFleetRowModel>), typeof(KvFilterChipModel), typeof(SubstationFleetRowModel),
        typeof(SubstationDetailModel<PlantRowModel, ConnectionRowModel>), typeof(PlantRowModel), typeof(ConnectionRowModel), typeof(AssetInsightRow),
    };

    // {Binding}, {Binding Name}, {Binding Path=Name, ...}, {Binding !Name}; "." stands for the bare {Binding}.
    private static readonly Regex BindingRx = new(@"\{Binding(?:\s+!?(?:Path=)?(?<p>[A-Za-z_][A-Za-z0-9_.]*))?\s*[,}]", RegexOptions.CultureInvariant);

    private static HashSet<string> Bindings(string xaml) =>
        BindingRx.Matches(xaml).Select(m => m.Groups["p"].Success ? m.Groups["p"].Value : ".").ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Every_property_the_WPF_asset_pages_bind_is_bound_by_the_Avalonia_ASSETS_view()
    {
        var wpf = new HashSet<string>(StringComparer.Ordinal);
        foreach (string f in WpfFiles) wpf.UnionWith(Bindings(Wpf(f)));
        HashSet<string> avalonia = Bindings(Avalonia());
        Assert.True(wpf.Count >= 150, $"only {wpf.Count} WPF bindings found; the reader no longer sees the WPF asset pages");

        var missing = new List<string>();
        foreach (string w in wpf.OrderBy(s => s, StringComparer.Ordinal))
        {
            if (NotBound.ContainsKey(w)) continue;
            string neutral = AdapterToNeutral.TryGetValue(w, out string? n) ? n : w;
            if (neutral != ".")
                Assert.True(BoundTypes.Any(t => t.GetProperty(neutral, BindingFlags.Public | BindingFlags.Instance) is not null),
                    $"binding {w} maps to {neutral}, which is not a property of any A2 page or row type or of the navigator");
            if (!avalonia.Contains(neutral)) missing.Add($"{w} (neutral {neutral})");
        }
        Assert.True(missing.Count == 0, "AssetsView.axaml does not bind these WPF bindings (port them, or list them with the reason): "
            + string.Join(", ", missing));

        // A binding listed as not bound is really in the WPF pages and really absent from the port.
        foreach (string notBound in NotBound.Keys)
        {
            Assert.Contains(notBound, wpf);
            Assert.DoesNotContain(notBound, avalonia);
        }
        // An adapter name never reaches the AXAML (it would bind nothing on the neutral pages).
        foreach (string adapter in AdapterToNeutral.Keys)
            Assert.DoesNotContain(adapter, avalonia);
        // Every WPF command's link button keeps its CommandParameter binding (the id read at click time, as WPF).
        foreach (string id in new[] { "LandingBusId", "BusId", "HvBusId", "LvBusId", "OltcBusId", "VInBusId", "VOutBusId" })
            Assert.Contains($"CommandParameter=\"{{Binding {id}}}\"", Avalonia(), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_fixed_text_of_the_WPF_asset_pages_is_on_the_Avalonia_ASSETS_view_in_the_same_order()
    {
        string avalonia = Avalonia();
        var literal = new Regex("\\s(?:Text|Content|ToolTip|Caption|Unit)=\"(?<t>[^\"{][^\"]*)\"", RegexOptions.CultureInvariant);
        var all = new List<string>();
        foreach (string f in WpfFiles)
        {
            List<string> texts = literal.Matches(Wpf(f)).Select(m => m.Groups["t"].Value).ToList();
            Assert.NotEmpty(texts);
            all.AddRange(texts);
            // Avalonia writes a tooltip as ToolTip.Tip; every other literal keeps its WPF attribute name.
            var missing = texts.Distinct(StringComparer.Ordinal).Where(t =>
                !avalonia.Contains($"Text=\"{t}\"", StringComparison.Ordinal)
                && !avalonia.Contains($"Content=\"{t}\"", StringComparison.Ordinal)
                && !avalonia.Contains($"ToolTip.Tip=\"{t}\"", StringComparison.Ordinal)
                && !avalonia.Contains($"Caption=\"{t}\"", StringComparison.Ordinal)
                && !avalonia.Contains($"Unit=\"{t}\"", StringComparison.Ordinal)).ToList();
            Assert.True(missing.Count == 0, $"Fixed WPF texts of {f} not on AssetsView.axaml: " + string.Join(" | ", missing));
        }

        // The panel headers keep their WPF order: each WPF header text (Text="...", upper case) appears in the AXAML in
        // the same relative order (the templates are carried over in the WPF files' order).
        var headerRx = new Regex("\\sText=\"(?<t>[A-Z][A-Z0-9 &;:()/\\-.%>]+)\"", RegexOptions.CultureInvariant);
        int at = 0;
        foreach (string f in WpfFiles)
            foreach (Match m in headerRx.Matches(Wpf(f)))
            {
                string needle = $"Text=\"{m.Groups["t"].Value}\"";
                int found = avalonia.IndexOf(needle, at, StringComparison.Ordinal);
                Assert.True(found >= 0, $"{f}: header {needle} is missing or out of order in AssetsView.axaml");
                at = found + needle.Length;
            }
    }

    [Fact]
    public void Every_fixed_value_panel_header_carries_the_STATIC_DEMO_tag_and_the_fixed_dials_say_DEMO()
    {
        // The ten D-DOC-145 panels (Doc14DesktopPresentationTests pins them in WPF): each header shares its DockPanel
        // with a StaticDemoTag in the AXAML too.
        string avalonia = Avalonia();
        var blocks = Regex.Matches(avalonia, "<DockPanel Margin=\"0,0,0,8\">(?<b>.*?)</DockPanel>", RegexOptions.Singleline)
            .Select(m => m.Groups["b"].Value).Where(b => b.Contains("Classes=\"StaticDemoTag\"", StringComparison.Ordinal)).ToList();
        var tagged = blocks.SelectMany(b => Regex.Matches(b, "Text=\"(?<t>[^\"]+)\"").Select(m => m.Groups["t"].Value)).ToList();
        foreach ((string header, int times) in new[]
                 {
                     ("THERMAL", 1), ("DISSOLVED GAS ANALYSIS", 1), ("PROTECTION", 1), ("EVENT LOG", 3), ("ROUTE INTEGRITY", 1),
                     ("THERMAL ENVELOPE", 1), ("ACCESS &amp; CIVIL", 1), ("SUBSYSTEMS", 1),
                 })
            Assert.True(tagged.Count(t => t == header) == times, $"{header}: {tagged.Count(t => t == header)} tagged panels, expected {times}");
        foreach (string caption in new[] { "Caption=\"TOP-OIL  DEMO\"", "Caption=\"HOT-SPOT  DEMO\"", "Caption=\"CONDUCTOR  DEMO\"", "Text=\"TEMPERATURES: STATIC DEMO\"" })
            Assert.Contains(caption, avalonia, StringComparison.Ordinal);
        string theme = Read("src", "GridSim.App", "Theme", "ControlRoom.axaml");
        Assert.Contains("<Style Selector=\"TextBlock.StaticDemoTag\">", theme, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Text\" Value=\"STATIC DEMO\" />", theme, StringComparison.Ordinal);
        Assert.DoesNotContain("demo data", avalonia, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_page_the_Avalonia_factory_makes_has_its_template_and_no_placeholder_page_exists()
    {
        string avalonia = Avalonia();
        var explorer = new NavigableAssetExplorer(AppAssetPageFactory.Instance);
        AssetPageModel[] pages =
        {
            explorer.Home, explorer.CreateGeneratorFleet(), explorer.CreatePhasorFleet(), explorer.CreateInterconnectorFleet(),
            explorer.CreateTransformerFleet(), explorer.CreateCircuitFleet(), explorer.CreateSubstationFleet(), explorer.CreateTapChangerFleet(),
            explorer.CreateSubstationDetail(1), explorer.CreateGeneratorDetail(0, 1, "G"), explorer.CreateInterconnectorDetail(0, 1, "I"),
            explorer.CreateCircuitDetail(0, 1, 2, "C"), explorer.CreateTransformerDetail(0, 1, 2, "T"),
        };
        Assert.Equal(13, pages.Select(p => p.GetType()).Distinct().Count());
        foreach (AssetPageModel p in pages)
        {
            string prefix = p.GetType().Namespace == "GridSim.App.Assets" ? "assets:" : "pages:";
            Assert.Contains($"<DataTemplate DataType=\"{prefix}{p.GetType().Name}\">", avalonia, StringComparison.Ordinal);
            Assert.False(p.GetType().IsGenericType, $"{p.GetType()}: an AXAML template needs a closed, named type");
        }
        foreach (string dir in new[] { Path.Combine(RepoRoot(), "src", "GridSim.App", "Assets"), Path.Combine(RepoRoot(), "src", "GridSim.Ui", "Assets") })
            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                Assert.DoesNotContain("PlaceholderPage", File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Fact]
    public void Under_S7_the_protection_tag_shows_only_while_the_panel_is_the_demo()
    {
        // Test added 2026-10-20 (ASSETS fix S7, Mark approved): the PROTECTION panel's STATIC DEMO tag is shown by the
        // page's ProtectionIsDemo flag (true over the demo rows, false over the case's own settings: both directions are
        // asserted by behaviour in GridSim.Tests UiAssetFixesTests and the parity test's FixedS7ProtectionTag), exactly as
        // the WPF template binds it; every other STATIC DEMO tag in the AXAML stays unconditional.
        string avalonia = Avalonia();
        Assert.Matches(new Regex(@"<TextBlock Classes=""StaticDemoTag"" DockPanel\.Dock=""Right"" IsVisible=""\{Binding ProtectionIsDemo\}"" />\s*<TextBlock Text=""PROTECTION"""), avalonia);
        List<string> tags = Regex.Matches(avalonia, @"<TextBlock[^>]*Classes=""StaticDemoTag""[^>]*/>").Select(m => m.Value).ToList();
        Assert.Equal(1, tags.Count(t => t.Contains("IsVisible", StringComparison.Ordinal)));
    }
}
