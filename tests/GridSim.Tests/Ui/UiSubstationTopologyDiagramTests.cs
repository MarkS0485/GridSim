using System.Globalization;
using System.Text.Json;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Overlay;
using GridSim.Core.IO.Json.Schema;
using GridSim.Core.Licensing;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Tests.Editor;
using GridSim.Ui.Assets.Editing;
using GridSim.Ui.Assets.Index;
using GridSim.Ui.Assets.Keys;
using GridSim.Ui.Assets.Navigation;
using GridSim.Ui.Assets.Overlay;
using GridSim.Ui.Assets.Overviews;
using GridSim.Ui.Dashboard;
using GridSim.Ui.Diagrams;
using GridSim.Ui.Hosting;
using GridSim.Ui.Models;
using GridSim.Ui.Schematic;
using Xunit;

namespace GridSim.Tests.Ui;

/// <summary>
/// Phase 5c (2026-10-20): the substation topology single-line diagram (<see cref="OverlaySubstationDiagram"/>) on built
/// overlays, exactly: every symbol (id, IEC symbol kind, position, rotation, label, energised, switch state), every
/// connection (id, route, energised) and every free label, for a simple, a split, a coupled, an earth switch and a
/// multi-bay (double busbar, in-line and routed coupler) topology; hit testing on it; the Run-mode energised colour from
/// the solved voltage of each section's bus; the substation page's facts and CHECK warnings in the ASSETS navigator;
/// the click routes; and Author mode, where toggling a switching device's normalOpen redraws the diagram and re-evaluates
/// the warning. Every number is exact (the layout is plain arithmetic), compared as invariant text.
/// </summary>
public sealed class UiSubstationTopologyDiagramTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ---------------------------------------------------------------- building overlays (as OverlaySubstationTopologyTests)

    private const string Sub = """{ "id": "sub", "kind": "substation", "refs": {}, "params": {} }""";

    private static string Section(string id, int bus) =>
        $$"""{ "id": "{{id}}", "kind": "busbarSection", "parent": "sub", "refs": { "bus": { "bus": {{bus}} } }, "params": {} }""";

    private static string Coupler(string id, string from, string to, bool open, string device = "breaker") =>
        $$"""{ "id": "{{id}}", "kind": "switchingDevice", "parent": "sub", "refs": { "from": { "record": "{{from}}" }, "to": { "record": "{{to}}" } }, "params": { "device": "{{device}}", "normalOpen": {{(open ? "true" : "false")}} } }""";

    private static string Bay(string id, string from, string role, string name, bool open, string device = "breaker") =>
        $$"""{ "id": "{{id}}", "kind": "switchingDevice", "parent": "sub", "refs": { "from": { "record": "{{from}}" }, "{{role}}": { "{{role}}": "{{name}}" } }, "params": { "device": "{{device}}", "normalOpen": {{(open ? "true" : "false")}} } }""";

    private static string Lone(string id, string from, string device, bool open) =>
        $$"""{ "id": "{{id}}", "kind": "switchingDevice", "parent": "sub", "refs": { "from": { "record": "{{from}}" } }, "params": { "device": "{{device}}", "normalOpen": {{(open ? "true" : "false")}} } }""";

    // The overlay through the schema and kind specs (no case: element names are not resolved here), then projected.
    private static SubstationTopology Topology(params string[] records)
    {
        using JsonDocument doc = JsonDocument.Parse("{ \"schema\": \"gridsim-assets/2\", \"records\": [ " + string.Join(", ", records) + " ] }");
        var docs = new[] { ("site.assets.json", doc.RootElement.Clone()) };
        IReadOnlyList<SchemaError> errors = V2AssetChecker.Check(docs, BundledSchemas.V2Validate, OverlayIndex.Kinds, targets: null);
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors.Select(e => e.Pointer + " => " + e.Message)));
        return SubstationTopology.All(CaseAssetOverlay.FromCheckedDocuments(docs).Records).Single();
    }

    private static string P(ScreenPoint p) => string.Create(Inv, $"{p.X},{p.Y}");

    private static string[] Symbols(DiagramModel d) => d.Symbols.Select(s =>
        $"{s.Id}|{s.Kind}|{P(s.Position)}|{s.Rotation}|{s.Label}|{s.State.Energised}|{s.State.Switch}").ToArray();

    private static string[] Connections(DiagramModel d) => d.Connections.Select(c =>
        $"{c.Id}|{string.Join(" ", c.Points.Select(P))}|{c.State.Energised}").ToArray();

    private static string[] Labels(DiagramModel d) => d.Labels.Select(l =>
        string.Create(Inv, $"{l.Text}|{P(l.Anchor)}|{l.Size}|{l.Align}|{l.Muted}")).ToArray();

    private const string Header = "sub  as written in the asset overlay (carried, not used by the solver)|40,50|11|Left|False";
    private static readonly string ColdMode = OverlaySubstationDiagram.ModeNote(false) + "|40,64|9|Left|True";
    private static readonly string LiveMode = OverlaySubstationDiagram.ModeNote(true) + "|40,64|9|Left|True";

    // ---------------------------------------------------------------- the diagram, exactly

    [Fact]
    public void A_simple_topology_draws_two_sections_an_in_line_coupler_and_a_line_bay_exactly()
    {
        DiagramModel d = OverlaySubstationDiagram.Build(Topology(Sub, Section("bb1", 1), Section("bb2", 1),
            Coupler("cpl", "bb1", "bb2", open: false), Bay("cbl", "bb1", "line", "L", open: false)));

        Assert.Equal(new[]
        {
            "bb1|BusSection|40,120|None|bb1  (bus 1)|False|Closed",
            "bb2|BusSection|180,120|None|bb2  (bus 1)|False|Closed",
            "cbl|CircuitBreaker|80,160|None|cbl|False|Closed",
            "cbl/END|LineEnd|80,210|None|line 'L'|False|Closed",
            "cpl|CircuitBreaker|150,120|ThreeQuarter||False|Closed",
        }, Symbols(d));
        Assert.Equal("80|40", string.Create(Inv, $"{d.Symbol("bb1").Length:R}|{string.Join(", ", d.Symbol("bb1").Taps!.Select(x => x.ToString("R", Inv)))}"));
        Assert.Equal("80|40", string.Create(Inv, $"{d.Symbol("bb2").Length:R}|{string.Join(", ", d.Symbol("bb2").Taps!.Select(x => x.ToString("R", Inv)))}"));
        Assert.Equal(new[]
        {
            "bb1.T0-cbl.A|80,120 80,140|False",
            "cbl.B-cbl/END.T|80,180 80,190|False",
            "bb1.R-cpl.A|120,120 130,120|False",
            "cpl.B-bb2.L|170,120 180,120|False",
        }, Connections(d));
        Assert.Equal(new[]
        {
            "cpl  coupler|150,138|9|Centre|False",
            Header,
            ColdMode,
            "normal-state nodes: ['bb1', 'bb2']|40,78|9|Left|True",
        }, Labels(d));

        // Every connection ends exactly on the two ports it names.
        foreach (DiagramConnection c in d.Connections)
        {
            Assert.Equal(d.Port(c.From.SymbolId, c.From.Name).Position, c.Points[0]);
            Assert.Equal(d.Port(c.To.SymbolId, c.To.Name).Position, c.Points[^1]);
        }
    }

    [Fact]
    public void A_split_draws_the_open_coupler_green_and_counts_the_warning_and_a_coupling_colours_each_side_by_its_own_bus()
    {
        DiagramModel split = OverlaySubstationDiagram.Build(Topology(Sub, Section("bb1", 1), Section("bb2", 1), Coupler("cpl", "bb1", "bb2", open: true)));
        Assert.Equal("cpl|CircuitBreaker|150,120|ThreeQuarter||False|Open", Symbols(split)[^1]);
        Assert.Equal(DiagramStyle.Open, DiagramStyle.SwitchColour(split.Symbol("cpl").State.Switch));
        Assert.Equal("normal-state nodes: ['bb1'] and ['bb2']  -  1 topology warning (see CHECK)|40,78|9|Left|True", Labels(split)[^1]);

        // Coupled, Run mode: bus 1 solved at 1.02 pu, bus 2 at 0 (dead): bb1 side live, bb2 side not; the coupler takes
        // its "from" section's state and shows its blade closed (red) whatever the energisation.
        SubstationTopology coupled = Topology(Sub, Section("bb1", 1), Section("bb2", 2), Coupler("cpl", "bb1", "bb2", open: false));
        DiagramModel d = OverlaySubstationDiagram.Build(coupled, new Dictionary<long, double> { [1] = 1.02, [2] = 0.0 });
        Assert.Equal(new[]
        {
            "bb1|BusSection|40,120|None|bb1  (bus 1)|True|Closed",
            "bb2|BusSection|180,120|None|bb2  (bus 2)|False|Closed",
            "cpl|CircuitBreaker|150,120|ThreeQuarter||True|Closed",
        }, Symbols(d));
        Assert.Equal(new[] { "bb1.R-cpl.A|120,120 130,120|True", "cpl.B-bb2.L|170,120 180,120|False" }, Connections(d));
        Assert.Equal(new[]
        {
            "cpl  coupler|150,138|9|Centre|False",
            Header,
            LiveMode,
            "normal-state nodes: ['bb1', 'bb2']  -  1 topology warning (see CHECK)|40,78|9|Left|True",
        }, Labels(d));
        Assert.Equal(DiagramStyle.Energised, DiagramStyle.ConductorColour(d.Symbol("bb1").State));
        Assert.Equal(DiagramStyle.DeEnergised, DiagramStyle.ConductorColour(d.Symbol("bb2").State));
        Assert.Equal(DiagramStyle.Closed, DiagramStyle.SwitchColour(d.Symbol("cpl").State.Switch));

        // A bus missing from the solve, or a non-finite voltage, is not live either.
        DiagramModel nan = OverlaySubstationDiagram.Build(coupled, new Dictionary<long, double> { [1] = double.NaN });
        Assert.All(nan.Symbols, s => Assert.False(s.State.Energised));
    }

    [Fact]
    public void An_earth_switch_bay_ends_at_earth_and_a_closed_one_joins_nothing()
    {
        DiagramModel d = OverlaySubstationDiagram.Build(Topology(Sub, Section("bb1", 1), Section("bb2", 1),
            Coupler("es", "bb1", "bb2", open: false, device: "earthSwitch"), Lone("es2", "bb2", "earthSwitch", open: true)));
        Assert.Equal(new[]
        {
            "bb1|BusSection|40,120|None|bb1  (bus 1)|False|Closed",
            "bb2|BusSection|180,120|None|bb2  (bus 1)|False|Closed",
            "es|EarthSwitch|80,160|None|es|False|Closed",
            "es2|EarthSwitch|220,160|None|es2|False|Open",
        }, Symbols(d));
        Assert.Equal(new[] { "bb1.T0-es.T|80,120 80,140|False", "bb2.T0-es2.T|220,120 220,140|False" }, Connections(d));
        Assert.Equal("normal-state nodes: ['bb1'] and ['bb2']  -  1 topology warning (see CHECK)|40,78|9|Left|True", Labels(d)[^1]);
    }

    [Fact]
    public void A_multi_bay_double_busbar_draws_series_switchgear_every_feeder_end_an_in_line_and_a_routed_coupler_exactly()
    {
        SubstationTopology t = Topology(Sub, Section("bb1", 1), Section("bb2", 1),
            Coupler("cpl", "bb1", "bb2", open: false), Coupler("c2", "bb1", "bb2", open: true),
            Bay("ds1", "bb1", "line", "L", open: false, device: "disconnector"),
            Bay("cb1", "bb1", "line", "L", open: false),
            Bay("ds2", "bb2", "line", "L", open: false, device: "disconnector"),
            Bay("cbt", "bb1", "transformer", "T", open: false),
            Bay("cbg", "bb2", "generator", "G", open: false),
            Bay("cbd", "bb2", "demand", "D", open: true),
            Lone("sp", "bb1", "disconnector", open: true));
        DiagramModel d = OverlaySubstationDiagram.Build(t);

        Assert.Equal(new[]
        {
            "bb1|BusSection|40,120|None|bb1  (bus 1)|False|Closed",
            "bb2|BusSection|420,120|None|bb2  (bus 1)|False|Closed",
            "ds1|Disconnector|80,160|None|ds1|False|Closed",
            "cb1|CircuitBreaker|80,210|None|cb1|False|Closed",
            "cb1/END|LineEnd|80,260|None|line 'L'|False|Closed",
            "ds2|Disconnector|460,160|None|ds2|False|Closed",
            "ds2/END|LineEnd|460,210|None|line 'L'|False|Closed",
            "cbt|CircuitBreaker|160,160|None|cbt|False|Closed",
            "cbt/END|Transformer2W|160,210|None|transformer 'T'|False|Closed",
            "cbg|CircuitBreaker|540,160|None|cbg|False|Closed",
            "cbg/END|Generator|540,210|None|generator 'G'|False|Closed",
            "cbd|CircuitBreaker|620,160|None|cbd|False|Open",
            "cbd/END|Load|620,210|None|demand 'D'|False|Closed",
            "sp|Disconnector|240,160|None|sp|False|Open",
            "cpl|CircuitBreaker|390,120|ThreeQuarter||False|Closed",
            "c2|CircuitBreaker|510,460|ThreeQuarter|c2  coupler|False|Open",
        }, Symbols(d));
        Assert.Equal("320|40, 120, 200, 280", string.Create(Inv, $"{d.Symbol("bb1").Length:R}|{string.Join(", ", d.Symbol("bb1").Taps!.Select(x => x.ToString("R", Inv)))}"));
        Assert.Equal(new[]
        {
            "bb1.T0-ds1.A|80,120 80,140|False",
            "ds1.B-cb1.A|80,180 80,190|False",
            "cb1.B-cb1/END.T|80,230 80,240|False",
            "bb2.T0-ds2.A|460,120 460,140|False",
            "ds2.B-ds2/END.T|460,180 460,190|False",
            "bb1.T1-cbt.A|160,120 160,140|False",
            "cbt.B-cbt/END.HV|160,180 160,190|False",
            "bb2.T1-cbg.A|540,120 540,140|False",
            "cbg.B-cbg/END.T|540,180 540,190|False",
            "bb2.T2-cbd.A|620,120 620,140|False",
            "cbd.B-cbd/END.T|620,180 620,190|False",
            "bb1.T2-sp.A|240,120 240,140|False",
            "bb1.R-cpl.A|360,120 370,120|False",
            "cpl.B-bb2.L|410,120 420,120|False",
            "bb1.T3-c2.A|320,120 320,290 490,290 490,460|False",
            "bb2.T3-c2.B|700,120 700,290 530,290 530,460|False",
        }, Connections(d));
        Assert.Equal("normal-state nodes: ['bb1', 'bb2']|40,78|9|Left|True", Labels(d)[^1]);

        // The routed coupler's leads turn below every bay (at y 290, 10 below where a bay of this depth could reach).
        double lowestBay = d.Symbols.Where(s => !s.IsBar && s.Id != "cpl" && s.Id != "c2").Max(s => SymbolLibrary.Bounds(s).Bottom);
        Assert.Equal(271, lowestBay);   // the line end at y 260: its bounds reach 11 below its centre
        Assert.All(d.Connections.Where(c => c.To.SymbolId == "c2"), c => Assert.True(c.Points[1].Y > lowestBay));

        // Hit testing: each symbol at its centre, the bar between taps, a coupler lead, and empty space.
        Assert.Equal(new DiagramHit(DiagramHitKind.Symbol, "cpl", DiagramSymbolKind.CircuitBreaker), DiagramHitTester.HitTest(d, new ScreenPoint(390, 120), 4));
        Assert.Equal(new DiagramHit(DiagramHitKind.Symbol, "bb1", DiagramSymbolKind.BusSection), DiagramHitTester.HitTest(d, new ScreenPoint(120, 121), 4));
        Assert.Equal(new DiagramHit(DiagramHitKind.Symbol, "c2", DiagramSymbolKind.CircuitBreaker), DiagramHitTester.HitTest(d, new ScreenPoint(510, 460), 4));
        Assert.Equal(new DiagramHit(DiagramHitKind.Symbol, "cb1/END", DiagramSymbolKind.LineEnd), DiagramHitTester.HitTest(d, new ScreenPoint(80, 260), 4));
        Assert.Equal(new DiagramHit(DiagramHitKind.Symbol, "cbt/END", DiagramSymbolKind.Transformer2W), DiagramHitTester.HitTest(d, new ScreenPoint(160, 210), 4));
        Assert.Equal(new DiagramHit(DiagramHitKind.Connection, "bb1.T3-c2.A", null), DiagramHitTester.HitTest(d, new ScreenPoint(400, 291), 4));
        Assert.Null(DiagramHitTester.HitTest(d, new ScreenPoint(1000, 1000), 4));
        foreach (DiagramSymbol s in d.Symbols.Where(s => !s.IsBar))
            Assert.Equal(s.Id, DiagramHitTester.HitTest(d, s.Position, 4)!.Id);
    }

    [Fact]
    public void The_diagram_is_the_same_every_time_and_refuses_a_substation_without_sections()
    {
        string[] recs = { Sub, Section("bb1", 1), Section("bb2", 2), Coupler("cpl", "bb1", "bb2", open: false), Bay("cbl", "bb1", "line", "L", open: false) };
        DiagramModel a = OverlaySubstationDiagram.Build(Topology(recs)), b = OverlaySubstationDiagram.Build(Topology(recs));
        Assert.Equal(Symbols(a), Symbols(b));
        Assert.Equal(Connections(a), Connections(b));
        Assert.Equal(Labels(a), Labels(b));
        Assert.Throws<ArgumentException>(() => OverlaySubstationDiagram.Build(Topology(Sub)));
        Assert.Equal("cbl", OverlaySubstationDiagram.RecordIdOf("cbl/END"));
        Assert.Equal("cbl", OverlaySubstationDiagram.RecordIdOf("cbl"));
    }

    // ---------------------------------------------------------------- the ASSETS navigator (Run mode) on the fixture case

    // The case loaded as the app loads it, one solved frame, and the app's navigator fed it (as UiAssetOverlayTests).
    private static AssetNavigatorModel Navigator(string manifest)
    {
        var host = new SimHost();
        var dash = new DashboardModel();
        LiveLoadResult loaded = host.LoadLive(ModelCatalog.V2CaseSource(manifest), SolverKind.Auto, new LicenceGate(Entitlement.FreeDefault));
        Assert.True(loaded.Loaded, loaded.Refusal);
        HostFrame hf = Assert.IsType<HostFrame>(host.ProduceNext());
        dash.Update(hf.Frame!, hf.Boundaries, hf.SolveMs);
        var nav = new AssetNavigatorModel { Active = true };
        nav.SetSource(AssetCaseSource.From(ModelCatalog.V2CaseSource(manifest)), CaseSessionTests.Unlimited(), loaded.Overlay, null);
        nav.Update(hf.Frame!, dash.Clock);
        Assert.NotNull(nav.Index);
        return nav;
    }

    // The fixture's substation: sections ov.bb1 (bus 1) and ov.bb2 (bus 101), joined by the closed coupler ov.cpl.
    private const string FixtureCoupling =
        "substation 'ov.sub': busbar sections 'ov.bb1' (bus 1) and 'ov.bb2' (bus 101) collapse to different buses, but under the normal switch "
        + "states the closed device 'ov.cpl' joins them into one node. The switch states imply a coupling the authored case does not model. "
        + "This is a warning, not an error: the case still loads and solves exactly as written.";

    [Fact]
    public void The_substation_page_shows_its_topology_and_warning_and_CHECK_lists_the_warning_without_any_problem()
    {
        using var fx = new UiOverlayFixture();
        AssetNavigatorModel nav = Navigator(fx.ManifestPath);
        OverlayIndex ov = nav.Index!.Overlay!;
        OverlayEntry sub = ov.Find("ov.sub")!;
        Assert.Equal(new[] { FixtureCoupling }, sub.Warnings.Select(w => w.Message));
        Assert.Equal(new[] { FixtureCoupling }, ov.Warnings.Select(w => w.Message));
        Assert.All(ov.Entries.Where(e => e.Kind != "substation"), e => { Assert.Null(e.Topology); Assert.Empty(e.Warnings); });

        nav.Select(nav.Tree.NodeFor(sub.Key)!);
        AssetOverviewPage page = Assert.IsType<AssetOverviewPage>(nav.Current);
        Assert.Contains(new OverviewFact("Topology", "2 busbar sections, 7 switching devices, 6 bays"), page.Facts);
        Assert.Contains(new OverviewFact("Normal-state nodes", "['ov.bb1', 'ov.bb2']"), page.Facts);
        Assert.Contains(new OverviewFact(OverlayPages.TopologyWarningLabel, FixtureCoupling), page.Facts);
        Assert.Equal(1, page.Facts.Count(f => f.Label == OverlayPages.TopologyWarningLabel));

        Assert.Equal(new[] { "OVERVIEW", "PROPERTIES", "CHECK" }, nav.SubTabs);
        OverlayCheck check = Assert.IsType<OverlayCheck>(nav.OverlayCheck);
        Assert.Equal("1 topology warning in " + UiOverlayFixture.FileA + " (the case still loads and solves exactly as written)", check.Heading);
        Assert.Empty(check.Rows);
        Assert.False(check.HasRows);
        Assert.True(check.HasWarnings);
        Assert.Equal(new[] { new OverlayCheckRow(sub.Record.Pointer, FixtureCoupling) }, check.Warnings);
        Assert.Equal("/records/1", sub.Record.Pointer);

        // The diagram follows the frame: the page keeps the solved voltages and is rebuilt on its last notification.
        Assert.Equal(nameof(AssetOverviewPage.Content), AssetDiagrams.RebuildOn(page));
        DiagramModel d = AssetDiagrams.For(page)!;
        Assert.True(d.Symbol("ov.bb1").State.Energised);
        Assert.Contains(d.Labels, l => l.Text == OverlaySubstationDiagram.ModeNote(true));

        // A record without a topology: no RebuildOn, no topology facts, no CHECK.
        nav.Select(nav.Tree.NodeFor(ov.Find("ov.bb1")!.Key)!);
        Assert.Null(AssetDiagrams.RebuildOn(nav.Current));
        Assert.DoesNotContain(Assert.IsType<AssetOverviewPage>(nav.Current).Facts, f => f.Label is "Topology" or "Normal-state nodes" or OverlayPages.TopologyWarningLabel);
        Assert.DoesNotContain("CHECK", nav.SubTabs);
    }

    [Fact]
    public void A_click_on_a_coupler_or_device_opens_its_record_and_on_a_bay_end_the_asset()
    {
        using var fx = new UiOverlayFixture();
        AssetNavigatorModel nav = Navigator(fx.ManifestPath);
        OverlayIndex ov = nav.Index!.Overlay!;
        foreach (string id in new[] { "ov.cpl", "ov.es", "ov.ds", "ov.bb1" })
        {
            nav.Select(nav.Tree.NodeFor(ov.Find("ov.sub")!.Key)!);
            Assert.True(nav.OpenFromDiagram(id));
            Assert.Equal(ov.Find(id)!.Key, nav.Selected!.Key);
        }
        nav.Select(nav.Tree.NodeFor(ov.Find("ov.sub")!.Key)!);
        Assert.True(nav.OpenFromDiagram("ov.cbg/END"));
        Assert.Equal(AssetKeyKind.Generator, nav.Selected!.Key.Kind);
    }

    // ---------------------------------------------------------------- Author mode: toggling normalOpen redraws

    private static CaseRef Ov(CaseAuthoring a, string id)
        => a.Collections().Where(c => c.Role == CaseRoles.Overlay).SelectMany(a.Records).Select(r => r.Ref).First(r => a.OverlayIdentity(r).Id == id);

    [Fact]
    public void In_Author_mode_toggling_a_devices_normalOpen_redraws_the_diagram_and_re_evaluates_the_warning()
    {
        using var fx = new UiOverlayFixture();
        CaseAuthoring a = CaseAuthoring.Open(AssetCaseSource.V2(fx.ManifestPath), CaseSessionTests.Unlimited(), Array.Empty<string>());
        var m = new AssetAuthorModel(a);

        // The substation's OVERVIEW: the topology view with the coupling warning, nothing energised (no live values).
        Assert.True(m.SelectRecord(Ov(a, "ov.sub")));
        SubstationTopologyView before = Assert.IsType<SubstationTopologyView>(m.RecordPage!.Topology);
        Assert.True(m.RecordPage.HasTopology);
        Assert.Equal(new[] { FixtureCoupling }, before.Warnings);
        Assert.Equal("Normal-state nodes: ['ov.bb1', 'ov.bb2']", before.Nodes);
        Assert.Equal("SUBSTATION 'ov.sub'  -  SINGLE-LINE VIEW OF THE OVERLAY  (CARRIED, NOT USED BY THE SOLVER)", before.Heading);
        Assert.Equal(DiagramSwitchState.Closed, before.Diagram!.Symbol("ov.cpl").State.Switch);
        Assert.All(before.Diagram.Symbols, s => Assert.False(s.State.Energised));
        Assert.Contains(before.Diagram.Labels, l => l.Text == OverlaySubstationDiagram.ModeNote(false));

        // The coupler's own page shows the same substation; open it in PROPERTIES.
        Assert.True(m.SelectRecord(Ov(a, "ov.cpl")));
        Assert.Equal(before.Warnings, m.RecordPage!.Topology!.Warnings);
        m.SelectedSubTab = AssetNavigatorModel.PropertiesTab;
        Assert.True(m.EditField("params.normalOpen", "true").Ok);

        // Redrawn: the coupler open, two nodes, two buses each on its own: no warning.
        SubstationTopologyView after = m.RecordPage!.Topology!;
        Assert.NotSame(before.Diagram, after.Diagram);
        Assert.Equal(DiagramSwitchState.Open, after.Diagram!.Symbol("ov.cpl").State.Switch);
        Assert.Equal("Normal-state nodes: ['ov.bb1'] and ['ov.bb2']", after.Nodes);
        Assert.Empty(after.Warnings);
        Assert.False(after.HasWarnings);

        // Undo: closed again, the warning back.
        Assert.True(m.Undo().Ok);
        Assert.Equal(DiagramSwitchState.Closed, m.RecordPage!.Topology!.Diagram!.Symbol("ov.cpl").State.Switch);
        Assert.Equal(new[] { FixtureCoupling }, m.RecordPage.Topology.Warnings);

        // A click on the diagram selects the record (a section) or, on a bay end, the model record it switches.
        Assert.True(m.OpenFromDiagram("ov.bb2"));
        Assert.Equal("ov.bb2", m.Selected!.Label);
        Assert.True(m.OpenFromDiagram("ov.cbl/END"));
        Assert.Equal("'L1-2 N-S Scotland'", m.Selected!.Label);
        Assert.False(m.OpenFromDiagram("nothing"));

        // A record outside any substation has no topology view.
        Assert.True(m.SelectRecord(Ov(a, "ov.zebra")));
        Assert.Null(m.RecordPage!.Topology);
    }

    [Fact]
    public void In_Author_mode_the_CHECK_tab_lists_the_topology_warnings_of_the_case_as_it_is_now_beside_the_last_Checks_problems()
    {
        using var fx = new UiOverlayFixture();
        CaseAuthoring a = CaseAuthoring.Open(AssetCaseSource.V2(fx.ManifestPath), CaseSessionTests.Unlimited(), Array.Empty<string>());
        var m = new AssetAuthorModel(a);
        CaseRef sub = Ov(a, "ov.sub");
        var coupling = new AuthoringProblem(sub.FileName, sub.Pointer, FixtureCoupling);

        // No Check yet: no CHECK tab, as before (the warnings join the CHECK tab, they do not create it).
        Assert.True(m.SelectRecord(Ov(a, "ov.cpl")));
        Assert.Null(m.CheckPage);
        Assert.DoesNotContain(AssetNavigatorModel.CheckTab, m.SubTabs);

        // After a Check: Home lists every substation's warning; the substation and its children list their own.
        AuthoringReport report = m.Check();
        m.Select(m.Home);
        AuthorCheckPage home = Assert.IsType<AuthorCheckPage>(m.CheckPage);
        Assert.Equal(new[] { coupling }, home.TopologyWarnings);
        Assert.True(home.HasTopologyWarnings);
        Assert.Equal(report.Errors, home.Problems);   // the last Check's problems are unchanged by them
        foreach (string id in new[] { "ov.sub", "ov.cpl", "ov.bb2" })
        {
            Assert.True(m.SelectRecord(Ov(a, id)));
            Assert.Contains(AssetNavigatorModel.CheckTab, m.SubTabs);
            Assert.Equal(new[] { coupling }, m.CheckPage!.TopologyWarnings);
        }

        // Outside any substation, and on a model record: none.
        Assert.True(m.SelectRecord(Ov(a, "ov.zebra")));
        Assert.Empty(m.CheckPage!.TopologyWarnings);
        Assert.False(m.CheckPage.HasTopologyWarnings);
        m.Select(m.ClassNode(CaseRoles.Buses)!.Children[0]);
        Assert.Empty(m.CheckPage!.TopologyWarnings);

        // They follow the case as it is now, not the last Check: open the coupler and the warning goes; undo brings it back.
        Assert.True(m.SelectRecord(Ov(a, "ov.cpl")));
        m.SelectedSubTab = AssetNavigatorModel.PropertiesTab;
        Assert.True(m.EditField("params.normalOpen", "true").Ok);
        Assert.Empty(m.CheckPage!.TopologyWarnings);
        m.Select(m.Home);
        Assert.Empty(m.CheckPage!.TopologyWarnings);
        Assert.Equal(report.Errors, m.CheckPage.Problems);
        Assert.True(m.Undo().Ok);
        m.Select(m.Home);
        Assert.Equal(new[] { coupling }, m.CheckPage!.TopologyWarnings);
    }
}
