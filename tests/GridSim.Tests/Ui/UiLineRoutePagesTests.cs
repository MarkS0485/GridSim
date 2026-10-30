using System.Text.Json.Nodes;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Overlay;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Tests.Editor;
using GridSim.Ui.Assets.Editing;
using GridSim.Ui.Assets.Index;
using GridSim.Ui.Assets.Keys;
using GridSim.Ui.Assets.Navigation;
using GridSim.Ui.Assets.Overlay;
using GridSim.Ui.Assets.Overviews;
using GridSim.Ui.Commands;
using GridSim.Ui.Dashboard;
using GridSim.Ui.Geography;
using GridSim.Ui.Hosting;
using GridSim.Ui.Models;
using GridSim.Ui.Schematic;
using GridSim.Ui.Settings;
using GridSim.Ui.Shell;
using Xunit;

namespace GridSim.Tests.Ui;

/// <summary>
/// UI programme Phase 5d (2026-10-21): line routes, spans, tower types and conductor types of the asset overlay, headless.
/// The case is the 5-view fixture (UiOverlayFixture: data/uk as a v2 case; its file B keeps the conductor 'ov.zebra', the
/// tower type 'ov.l6' with one conductor position, the route 'ov.route' of line L2-3 with one geographic span) with file A
/// replaced by two routes of this test:
/// <list type="bullet">
/// <item>'rt.geo' carries line L3-4 (whose network record is given lengthKm 500 here): three spans of 166800 m by chainage
/// AND geographic far towers at longitude -6.5 (latitudes 57.5, 56.0, 54.5), routeLengthKm 500.4, and an earth wire. Its
/// spans add to 500.4 km, within 1% of the line's 500 km, and they chain: no CHECK note;</item>
/// <item>'rt.chain' carries line L1-2 (given lengthKm 2): three spans placed by chainage only, written out of order (the
/// third first), adding to 2.5 km (+25%: a note), with a 100 m gap between the second and third (a note) and the second
/// naming no conductor type (a note).</item>
/// </list>
/// Pinned exactly: each page's facts and tables, the CHECK notes, the map layer's geometry (the projection of the tower
/// positions by bits), its hit test, colour bin and framing, the View / Line routes toggle and its setting, Show on map
/// from a route page, and the solver bit-identical with and without the overlay.
/// </summary>
public sealed class UiLineRoutePagesTests
{
    private const string RoutesA = """
        { "schema": "gridsim-assets/2", "description": "Phase 5d fixture, file A.", "records": [
          { "id": "rt.geo", "kind": "lineRoute", "refs": { "line": { "line": "L3-4 N England-Yorks" } }, "params": { "name": "Geo route", "routeLengthKm": 500.4 } },
          { "id": "rt.g1", "kind": "span", "parent": "rt.geo", "refs": { "conductor": { "record": "ov.zebra" }, "tower": { "record": "ov.l6" } }, "params": { "lengthM": 166800, "chainageM": 0, "sagM": 8, "latitudeDeg": 57.5, "longitudeDeg": -6.5 } },
          { "id": "rt.g2", "kind": "span", "parent": "rt.geo", "refs": { "conductor": { "record": "ov.zebra" }, "tower": { "record": "ov.l6" } }, "params": { "lengthM": 166800, "chainageM": 166800, "latitudeDeg": 56.0, "longitudeDeg": -6.5 } },
          { "id": "rt.g3", "kind": "span", "parent": "rt.geo", "refs": { "conductor": { "record": "ov.zebra" } }, "params": { "lengthM": 166800, "chainageM": 333600, "latitudeDeg": 54.5, "longitudeDeg": -6.5 } },
          { "id": "rt.ew", "kind": "earthWire", "parent": "rt.geo", "refs": { "conductor": { "record": "ov.zebra" } }, "params": { "count": 2 } },
          { "id": "rt.chain", "kind": "lineRoute", "refs": { "line": { "line": "L1-2 N-S Scotland" } }, "params": { "name": "Chainage route" } },
          { "id": "rt.c3", "kind": "span", "parent": "rt.chain", "refs": { "conductor": { "record": "ov.zebra" } }, "params": { "lengthM": 500, "chainageM": 2100 } },
          { "id": "rt.c1", "kind": "span", "parent": "rt.chain", "refs": { "conductor": { "record": "ov.zebra" } }, "params": { "lengthM": 1000, "chainageM": 0 } },
          { "id": "rt.c2", "kind": "span", "parent": "rt.chain", "refs": {}, "params": { "lengthM": 1000, "chainageM": 1000 } }
        ] }
        """;

    private const string FileA = UiOverlayFixture.FileA;

    private static long Bits(double v) => BitConverter.DoubleToInt64Bits(v);

    // The fixture with this test's file A, and the two lines given a length in the network file (both runs of the bit
    // identity test get the same network).
    private static UiOverlayFixture Fixture(bool withOverlay = true)
    {
        var fx = new UiOverlayFixture(withOverlay, withOverlay ? RoutesA : null);
        JsonObject root = JsonNode.Parse(File.ReadAllText(fx.NetworkPath))!.AsObject();
        foreach ((string name, double km) in new[] { ("L3-4 N England-Yorks", 500.0), ("L1-2 N-S Scotland", 2.0) })
        {
            JsonObject line = root["lines"]!.AsArray().Select(n => n!.AsObject()).Single(o => (string?)o["name"] == name);
            line["lengthKm"] = km;
        }
        File.WriteAllText(fx.NetworkPath, root.ToJsonString());
        return fx;
    }

    private static (LiveLoadResult Loaded, (SimFrame Frame, string Clock)[] Frames) Run(string manifest, int count = 2)
    {
        var host = new SimHost();
        var dash = new DashboardModel();
        LiveLoadResult loaded = host.LoadLive(ModelCatalog.V2CaseSource(manifest), SolverKind.Auto, new LicenceGate(Entitlement.FreeDefault));
        Assert.True(loaded.Loaded, loaded.Refusal);
        var frames = new (SimFrame, string)[count];
        for (int i = 0; i < frames.Length; i++)
        {
            HostFrame hf = Assert.IsType<HostFrame>(host.ProduceNext());
            Assert.Equal(HostFrameKind.LiveSolved, hf.Kind);
            dash.Update(hf.Frame!, hf.Boundaries, hf.SolveMs);
            frames[i] = (hf.Frame!, dash.Clock);
        }
        return (loaded, frames);
    }

    private static AssetNavigatorModel Navigator(string manifest, CaseAssetOverlay? overlay, (SimFrame Frame, string Clock)[] frames)
    {
        var nav = new AssetNavigatorModel { Active = true };
        nav.SetSource(AssetCaseSource.From(ModelCatalog.V2CaseSource(manifest)), CaseSessionTests.Unlimited(), overlay, null);
        foreach (var (f, c) in frames) nav.Update(f, c);
        Assert.NotNull(nav.Index);
        return nav;
    }

    private static AssetOverviewPage Page(AssetNavigatorModel nav, string id)
    {
        nav.Select(nav.Tree.NodeFor(nav.Index!.Overlay!.Find(id)!.Key)!);
        return Assert.IsType<AssetOverviewPage>(nav.Current);
    }

    private static OverviewTable Table(AssetOverviewPage page, string title) => Assert.Single(page.Tables, t => t.Title == title);

    private static string[][] Cells(OverviewTable t) => t.Rows.Select(r => r.Cells.ToArray()).ToArray();

    private static void AssertCarried(AssetOverviewPage page)
    {
        Assert.Contains(OverlayIndex.NotUsedBySolver, page.Sub, StringComparison.Ordinal);
        Assert.Equal(OverlayIndex.SolverNote, page.Note);
        Assert.NotEmpty(page.Tables);
        foreach (OverviewTable t in page.Tables)
        {
            Assert.EndsWith(OverlayKindPages.TableTag, t.Title, StringComparison.Ordinal);
            Assert.All(t.Rows, r => Assert.Equal(t.Columns.Count, r.Cells.Count));
        }
    }

    private static int LineIndex(GridModel m, string name) =>
        Enumerable.Range(0, m.Branches.Count).Single(i => !m.Branches[i].IsTransformer && m.Branches[i].Name == name);

    private static string LineText(GridModel m, string name)
    {
        int i = LineIndex(m, name);
        return $"line #{i}  {m.Branches[i].From} -> {m.Branches[i].To}";
    }

    // ================================================================================================================
    // The pages
    // ================================================================================================================

    [Fact]
    public void The_geographic_route_page_shows_its_line_ordered_spans_with_towers_total_length_against_the_line_and_earth_wires()
    {
        using UiOverlayFixture fx = Fixture();
        (LiveLoadResult loaded, var frames) = Run(fx.ManifestPath);
        AssetNavigatorModel nav = Navigator(fx.ManifestPath, loaded.Overlay, frames);
        GridModel m = nav.Index!.Model;
        string line = LineText(m, "L3-4 N England-Yorks");
        Assert.Equal(500.0, m.Branches[LineIndex(m, "L3-4 N England-Yorks")].LengthKm);   // the network edit took

        AssetOverviewPage page = Page(nav, "rt.geo");
        AssertCarried(page);
        Assert.Equal("Line route  -  carried, not used by the solver", page.Sub);
        Assert.Equal(new[]
        {
            // the generic part, unchanged
            new OverviewFact("Kind", "lineRoute  (Line route)"),
            new OverviewFact("Written in", FileA + "  /records/0"),
            new OverviewFact("Parent", "none (a top-level record)"),
            new OverviewFact("Children", "4"),
            new OverviewFact("Provenance", "none given"),
            new OverviewFact("Note", "none"),
            new OverviewFact("Parameter name", "Geo route"),
            new OverviewFact("Parameter routeLengthKm", "500.4 km"),
            // the Phase 5d part
            new OverviewFact("Carries line", line),
            new OverviewFact("Route name", "Geo route"),
            new OverviewFact("Spans", "3"),
            new OverviewFact("Span order", "by chainage (every span writes chainageM)"),
            new OverviewFact("Tower positions", "geographic (latitude and longitude of every far tower) and chainage on every span"),
            new OverviewFact("Total route length", "500400 m (500.4 km): the sum of the span lengths, display only"),
            new OverviewFact("Route length as written", "500.4 km"),
            new OverviewFact("Line length", "500 km (the line record's lengthKm)"),
            new OverviewFact("Route against line", "within 1% (+0.08%): the sum of the 3 span lengths against the line's lengthKm"),
            new OverviewFact("Earth wires", "1"),
            new OverviewFact("Map", "drawn on the NETWORK map's Line routes layer (View / Line routes) through 3 towers"),
            new OverviewFact("Line values", "the solver uses the line's authored r, x, b and lengthKm; this route changes none of them"),
        }, page.Facts);

        Assert.Equal(new[] { OverlayKindPages.SpansTable, OverlayKindPages.RouteLengthTable, OverlayKindPages.RouteEarthWiresTable }, page.Tables.Select(t => t.Title));
        OverviewTable spans = Table(page, OverlayKindPages.SpansTable);
        Assert.Equal(new[] { "Span", "From tower", "To tower", "Length (m)", "Chainage at start (m)", "Latitude (deg)", "Longitude (deg)", "Tower type", "Conductor", "Sag (m)" }, spans.Columns);
        Assert.Equal(new[]
        {
            new[] { "rt.g1", "T0", "T1", "166800", "0", "57.5", "-6.5", "ov.l6 (L6)", "ov.zebra (Zebra)", "8" },
            new[] { "rt.g2", "T1", "T2", "166800", "166800", "56.0", "-6.5", "ov.l6 (L6)", "ov.zebra (Zebra)", "absent" },
            new[] { "rt.g3", "T2", "T3", "166800", "333600", "54.5", "-6.5", "none given", "ov.zebra (Zebra)", "absent" },
        }, Cells(spans));
        Assert.Equal(OverlayKindPages.TowerNumbering + " Spans by chainage (every span writes chainageM); '-' = not written on that span.", spans.Note);

        OverviewTable length = Table(page, OverlayKindPages.RouteLengthTable);
        Assert.Equal(new[] { "Quantity", "Value", "Unit" }, length.Columns);
        Assert.Equal(new[]
        {
            new[] { "Sum of the span lengths", "500400", "m" },
            new[] { "Sum of the span lengths", "500.4", "km" },
            new[] { "Route length as written (routeLengthKm)", "500.4", "km" },
            new[] { "Line length as written (lengthKm)", "500", "km" },
            new[] { "Route against line", "+0.08", "%" },
        }, Cells(length));
        Assert.Equal("Display only: the route's length is the sum of the 3 span lengths; a difference of more than 1% of the line's length is a CHECK note, never an error.", length.Note);

        OverviewTable wires = Table(page, OverlayKindPages.RouteEarthWiresTable);
        Assert.Equal(new[] { "Earth wire", "Earth wires (count)", "Bonded at both ends", "Conductor" }, wires.Columns);
        Assert.Equal(new[] { new[] { "rt.ew", "2", "absent", "ov.zebra (Zebra)" } }, Cells(wires));

        // The links: the line it carries (a model link), each child, and the earth wire again as a route row; each opens.
        Assert.Contains(page.Rows, r => r.Name == "line 'L3-4 N England-Yorks'" && r.Target == AssetKey.Branch(m, LineIndex(m, "L3-4 N England-Yorks")));
        OverviewRow ew = page.Rows[^1];
        Assert.Equal(new OverviewRow("rt.ew", "earth wire on this route  Earth wire", "earthWire", OverlayIndex.KeyOf("rt.ew")), ew);
        foreach (OverviewRow r in page.Rows)
        {
            Assert.True(nav.OpenKey(r.Target!), r.Name);
            Assert.Equal(r.Target, nav.Selected!.Key);
            nav.GoBack();
        }

        // Within 1% and continuous: no CHECK sub-tab.
        Page(nav, "rt.geo");
        Assert.Null(nav.OverlayCheck);
        Assert.DoesNotContain("CHECK", nav.SubTabs);
    }

    [Fact]
    public void The_chainage_route_orders_its_spans_by_chainage_and_its_CHECK_notes_the_length_mismatch_the_gap_and_the_missing_conductor()
    {
        using UiOverlayFixture fx = Fixture();
        (LiveLoadResult loaded, var frames) = Run(fx.ManifestPath);
        AssetNavigatorModel nav = Navigator(fx.ManifestPath, loaded.Overlay, frames);
        GridModel m = nav.Index!.Model;

        AssetOverviewPage page = Page(nav, "rt.chain");
        AssertCarried(page);
        OverviewFact Fact(string label) => Assert.Single(page.Facts, f => f.Label == label);
        Assert.Equal(LineText(m, "L1-2 N-S Scotland"), Fact("Carries line").Value);
        Assert.Equal("by chainage (every span writes chainageM)", Fact("Span order").Value);
        Assert.Equal("chainage along the route only (no geographic position)", Fact("Tower positions").Value);
        Assert.Equal("2500 m (2.5 km): the sum of the span lengths, display only", Fact("Total route length").Value);
        Assert.Equal("absent (routeLengthKm not written)", Fact("Route length as written").Value);
        Assert.Equal("2 km (the line record's lengthKm)", Fact("Line length").Value);
        Assert.Equal("differs by +25.00% (more than 1%; see CHECK): the sum of the 3 span lengths against the line's lengthKm", Fact("Route against line").Value);
        Assert.Equal("not drawn on the NETWORK map: chainage only: no tower has a geographic position", Fact("Map").Value);

        // Written c3, c1, c2; shown in chainage order c1, c2, c3, the towers numbered along it.
        Assert.Equal(new[]
        {
            new[] { "rt.c1", "T0", "T1", "1000", "0", "-", "-", "none given", "ov.zebra (Zebra)", "absent" },
            new[] { "rt.c2", "T1", "T2", "1000", "1000", "-", "-", "none given", "none given", "absent" },
            new[] { "rt.c3", "T2", "T3", "500", "2100", "-", "-", "none given", "ov.zebra (Zebra)", "absent" },
        }, Cells(Table(page, OverlayKindPages.SpansTable)));
        Assert.Equal(new[] { "Route against line", "+25.00", "%" }, Cells(Table(page, OverlayKindPages.RouteLengthTable))[4]);
        OverviewTable wires = Table(page, OverlayKindPages.RouteEarthWiresTable);
        Assert.Empty(wires.Rows);
        Assert.Equal("No earth wire record sits under this route.", wires.Note);

        // CHECK: three display-only notes, each at the pointer of the record it is about, and no checker problem.
        OverlayCheck check = Assert.IsType<OverlayCheck>(nav.OverlayCheck);
        Assert.Contains("CHECK", nav.SubTabs);
        Assert.Empty(check.Rows);
        Assert.Empty(check.Warnings);
        Assert.Equal("3 consistency notes on lineRoute 'rt.chain' (display only, never an error)", check.Heading);
        Assert.Equal(new[]
        {
            new OverlayCheckRow("/records/5", "Route length against line length: the sum of the 3 span lengths is 2.5 km but the line 'L1-2 N-S Scotland' writes lengthKm 2 km, a difference of +25.00% (more than 1%). Display only: the solver uses the line's authored values."),
            new OverlayCheckRow("/records/5", "The spans do not form a continuous chain: span 'rt.c2' (T1 to T2) ends at chainage 2000 m (chainageM 1000 + lengthM 1000) but span 'rt.c3' starts at chainage 2100 m: a gap of 100 m. Display only."),
            new OverlayCheckRow("/records/8", "Span 'rt.c2' names no conductor type (its 'conductor' reference is absent). Display only."),
        }, check.Notes);

        // The span itself carries its own conductor note; the spans that name one carry none.
        Page(nav, "rt.c2");
        Assert.Equal(new[] { new OverlayCheckRow("/records/8", "Span 'rt.c2' names no conductor type (its 'conductor' reference is absent). Display only.") },
            Assert.IsType<OverlayCheck>(nav.OverlayCheck).Notes);
        Page(nav, "rt.c1");
        Assert.Null(nav.OverlayCheck);

        // The fixture's own route: one geographic span of 350 m against its routeLengthKm 120 (a note), not drawable.
        AssetOverviewPage b6 = Page(nav, "ov.route");
        Assert.Equal("not drawn on the NETWORK map: only one tower has a geographic position (a polyline needs two)", Assert.Single(b6.Facts, f => f.Label == "Map").Value);
        Assert.Equal("not written in the line record (lengthKm 0 = unknown)", Assert.Single(b6.Facts, f => f.Label == "Line length").Value);
        Assert.Equal("not compared: the line's length is not written", Assert.Single(b6.Facts, f => f.Label == "Route against line").Value);
        Assert.Equal(new[] { new OverlayCheckRow("/records/3", "The route's routeLengthKm 120 km differs from the sum of its span lengths 0.35 km by +34185.71% (more than 1%). Display only.") },
            Assert.IsType<OverlayCheck>(nav.OverlayCheck).Notes);
    }

    [Fact]
    public void A_span_page_shows_its_route_towers_length_chainage_position_sag_and_its_conductor_with_units()
    {
        using UiOverlayFixture fx = Fixture();
        (LiveLoadResult loaded, var frames) = Run(fx.ManifestPath);
        AssetNavigatorModel nav = Navigator(fx.ManifestPath, loaded.Overlay, frames);
        GridModel m = nav.Index!.Model;

        AssetOverviewPage g2 = Page(nav, "rt.g2");
        AssertCarried(g2);
        Assert.Equal(new[]
        {
            new OverviewFact("Route", "lineRoute 'rt.geo'  (carries " + LineText(m, "L3-4 N England-Yorks") + ")"),
            new OverviewFact("Span number", "2 of 3 (spans by chainage (every span writes chainageM))"),
            new OverviewFact("From tower", "T1 (the far tower of span 'rt.g1')"),
            new OverviewFact("To tower", "T2 (this span's far tower)"),
            new OverviewFact("Length", "166800 m"),
            new OverviewFact("Chainage at start", "166800 m"),
            new OverviewFact("Far tower position", "latitude 56.0 deg, longitude -6.5 deg (WGS84)"),
            new OverviewFact("Tower type", "ov.l6 (L6)"),
            new OverviewFact("Conductor", "ov.zebra (Zebra)"),
            new OverviewFact("Sag", "absent"),
            new OverviewFact("Clearance", "not carried: the span kind has no clearance parameter"),
        }, g2.Facts.SkipWhile(f => f.Label != "Route").ToArray());
        Assert.Equal(new[] { OverlayKindPages.SpanTable, OverlayKindPages.SpanConductorTable }, g2.Tables.Select(t => t.Title));
        Assert.Equal(new[]
        {
            new[] { "Length", "166800", "m" },
            new[] { "Chainage at start", "166800", "m" },
            new[] { "Chainage at end (start + length)", "333600", "m" },
            new[] { "Sag at the design temperature", "absent", "m" },
            new[] { "Far tower latitude (WGS84)", "56.0", "deg" },
            new[] { "Far tower longitude (WGS84)", "-6.5", "deg" },
        }, Cells(Table(g2, OverlayKindPages.SpanTable)));
        OverviewTable cond = Table(g2, OverlayKindPages.SpanConductorTable);
        Assert.Equal(new[] { "Conductor", "Code", "Material", "Cross-section (mm2)", "DC resistance (ohm/km)", "Rated current (A)" }, cond.Columns);
        Assert.Equal(new[] { new[] { "ov.zebra", "Zebra", "acsr", "428.9", "0.0674", "900" } }, Cells(cond));

        // The first span starts at the route's start; a chainage-only span has no position; no conductor reads so.
        AssetOverviewPage g1 = Page(nav, "rt.g1");
        Assert.Equal("T0 (the route's start)", Assert.Single(g1.Facts, f => f.Label == "From tower").Value);
        Assert.Equal("8 m", Assert.Single(g1.Facts, f => f.Label == "Sag").Value);
        AssetOverviewPage c2 = Page(nav, "rt.c2");
        Assert.Equal("not given (the span is placed by chainage)", Assert.Single(c2.Facts, f => f.Label == "Far tower position").Value);
        Assert.Equal("none given (see CHECK)", Assert.Single(c2.Facts, f => f.Label == "Conductor").Value);
        OverviewTable none = Table(c2, OverlayKindPages.SpanConductorTable);
        Assert.Empty(none.Rows);
        Assert.Equal("No conductor type is named for this span (see CHECK).", none.Note);
    }

    [Fact]
    public void The_tower_type_page_tables_its_conductor_positions_and_the_conductor_type_page_its_values_with_units()
    {
        using UiOverlayFixture fx = Fixture();
        (LiveLoadResult loaded, var frames) = Run(fx.ManifestPath);
        AssetNavigatorModel nav = Navigator(fx.ManifestPath, loaded.Overlay, frames);

        AssetOverviewPage tower = Page(nav, "ov.l6");
        AssertCarried(tower);
        Assert.Equal(new[] { OverlayKindPages.TowerTypeTable, OverlayKindPages.PositionsTable }, tower.Tables.Select(t => t.Title));
        Assert.Equal(new[] { new[] { "Design code", "L6", "" }, new[] { "Height to peak", "50", "m" }, new[] { "Circuits", "2", "" } },
            Cells(Table(tower, OverlayKindPages.TowerTypeTable)));
        OverviewTable pos = Table(tower, OverlayKindPages.PositionsTable);
        Assert.Equal(new[] { "Position", "Phase", "Circuit", "x (m)", "y (m)" }, pos.Columns);
        Assert.Equal(new[] { new[] { "ov.l6.a", "a", "1", "-6.5", "30" } }, Cells(pos));
        Assert.Equal("3", Assert.Single(tower.Facts, f => f.Label == "Spans using this tower type").Value);
        // The spans that use it, in overlay order (file A, then file B), each a link to its record.
        Assert.Equal(new[] { "rt.g1", "rt.g2", "ov.span1" }, tower.Rows.Where(r => r.Detail.StartsWith("span using this tower type", StringComparison.Ordinal)).Select(r => r.Name));

        AssetOverviewPage at = Page(nav, "ov.l6.a");
        AssertCarried(at);
        Assert.Equal(new[] { new[] { "ov.l6 (L6)", "a", "1", "-6.5", "30" } }, Cells(Table(at, OverlayKindPages.PositionTable)));

        AssetOverviewPage zebra = Page(nav, "ov.zebra");
        AssertCarried(zebra);
        OverviewTable c = Table(zebra, OverlayKindPages.ConductorTable);
        Assert.Equal(new[]
        {
            new[] { "Code", "Zebra", "" },
            new[] { "Material", "acsr", "" },
            new[] { "Cross-section", "428.9", "mm2" },
            new[] { "Diameter", "28.62", "mm" },
            new[] { "DC resistance at 20 degC", "0.0674", "ohm/km" },
            new[] { "Rated current (continuous)", "900", "A" },
        }, Cells(c));
        Assert.Equal("428.9 mm2", Assert.Single(zebra.Facts, f => f.Label == "Cross-section").Value);
        Assert.Equal("0.0674 ohm/km", Assert.Single(zebra.Facts, f => f.Label == "DC resistance at 20 degC").Value);
        Assert.Equal("900 A", Assert.Single(zebra.Facts, f => f.Label == "Rated current").Value);
        Assert.Equal("6 spans, 2 earth wires", Assert.Single(zebra.Facts, f => f.Label == "Used by").Value);
        Assert.Equal(new[] { "rt.g1", "rt.g2", "rt.g3", "rt.c3", "rt.c1", "ov.span1", "rt.ew", "ov.ew" },
            zebra.Rows.Where(r => r.Detail.StartsWith("span strung", StringComparison.Ordinal) || r.Detail.StartsWith("earth wire of", StringComparison.Ordinal)).Select(r => r.Name));
    }

    [Fact]
    public void In_Author_mode_a_span_edits_through_the_existing_field_grid_one_undo_step_and_the_route_sum_follows()
    {
        using UiOverlayFixture fx = Fixture();
        CaseAuthoring a = CaseAuthoring.Open(AssetCaseSource.V2(fx.ManifestPath), CaseSessionTests.Unlimited(), Array.Empty<string>());
        CaseRef Ov(string id) => a.Collections().Where(c => c.Role == CaseRoles.Overlay).SelectMany(a.Records)
            .Select(r => r.Ref).First(r => a.OverlayIdentity(r).Id == id);
        int depth = a.UndoDepth;
        FieldGridModel grid = a.FieldGrid(Ov("rt.c3"));
        Assert.True(grid.IsOverlay);
        AuthoringResult r = grid.Edit("params.chainageM", "2000");
        Assert.True(r.Ok && r.Changed, r.Message);
        Assert.Equal(depth + 1, a.UndoDepth);
        // The session's records now chain: no continuity problem, and the order and sum are as before.
        LineRouteSpans now = LineRoutes.Order(a.OverlayRecordsNow().Where(x => x.Parent == "rt.chain"));
        Assert.Equal(new[] { "rt.c1", "rt.c2", "rt.c3" }, now.Spans.Select(s => s.Id));
        Assert.Empty(LineRoutes.ContinuityProblems(now));
        Assert.Equal(2500.0, now.TotalM);
        // A negative length is refused by the kind spec's minimum; nothing changes.
        Assert.False(a.FieldGrid(Ov("rt.c3")).Edit("params.lengthM", "-1").Ok);
        Assert.Equal(depth + 1, a.UndoDepth);
        a.Undo();
        Assert.Equal(depth, a.UndoDepth);
        Assert.Single(LineRoutes.ContinuityProblems(LineRoutes.Order(a.OverlayRecordsNow().Where(x => x.Parent == "rt.chain"))));
    }

    // ================================================================================================================
    // The map layer
    // ================================================================================================================

    // A SchematicView of the model at a fitted 900 x 700 view.
    private static SchematicView View(GridModel m, PowerFlowResult? state = null)
    {
        var v = new SchematicView();
        v.Load(m);
        v.ViewportWidth = 900;
        v.ViewportHeight = 700;
        Assert.True(v.FitToView());
        if (state is not null) v.ApplyState(state);
        return v;
    }

    [Fact]
    public void The_layer_draws_the_geographic_route_through_its_towers_projected_by_the_geography_projection_bit_for_bit()
    {
        using UiOverlayFixture fx = Fixture();
        (LiveLoadResult loaded, var frames) = Run(fx.ManifestPath);
        GridModel m = frames[0].Frame.Model;
        LineRouteMap map = LineRouteMap.Build(loaded.Overlay, m);

        LineRoutePolyline geo = Assert.Single(map.Drawn);
        Assert.Equal("rt.geo", geo.RouteId);
        Assert.Equal(OverlayIndex.KeyOf("rt.geo"), geo.RouteKey);
        Assert.Equal("L3-4 N England-Yorks", geo.LineName);
        Assert.Equal(LineIndex(m, "L3-4 N England-Yorks"), geo.BranchIndex);
        Assert.Equal(new[] { "T1", "T2", "T3" }, geo.TowerIds);
        // Each tower is the geography layers' projection of its WGS84 position, bit for bit: the GB box
        // (lon -8.2..1.9, lat 49.8..59.0), x west to east, y north-down; written out here as the projection computes it.
        (double Lat, double Lon)[] towers = { (57.5, -6.5), (56.0, -6.5), (54.5, -6.5) };
        Assert.Equal(towers.Length, geo.Points.Count);
        for (int i = 0; i < towers.Length; i++)
        {
            WorldPoint want = GeographyData.WorldOf(towers[i].Lon, towers[i].Lat);
            Assert.Equal(Bits(want.X), Bits(geo.Points[i].X));
            Assert.Equal(Bits(want.Y), Bits(geo.Points[i].Y));
            double x = (towers[i].Lon - -8.2) / (1.9 - -8.2), y = 1.0 - (towers[i].Lat - 49.8) / (59.0 - 49.8);
            Assert.Equal(Bits(x), Bits(geo.Points[i].X));
            Assert.Equal(Bits(y), Bits(geo.Points[i].Y));
            Assert.Equal(want, LineRouteMap.Project(towers[i].Lat, towers[i].Lon));
        }
        // On the screen: the map's own world-to-screen transform of each point, bit for bit.
        SchematicView v = View(m);
        IReadOnlyList<ScreenPoint> s = LineRouteMap.ToScreen(geo, v);
        for (int i = 0; i < s.Count; i++)
        {
            ScreenPoint want = v.WorldToScreen(geo.Points[i].X, geo.Points[i].Y);
            Assert.Equal(Bits(want.X), Bits(s[i].X));
            Assert.Equal(Bits(want.Y), Bits(s[i].Y));
        }

        // The routes not drawn, in overlay order, with why; the hint says both.
        Assert.Equal(new[]
        {
            new LineRouteNotDrawn("rt.chain", "chainage only: no tower has a geographic position"),
            new LineRouteNotDrawn("ov.route", "only one tower has a geographic position (a polyline needs two)"),
        }, map.NotDrawn);
        Assert.Equal("LINE ROUTES (CARRIED, NOT USED BY THE SOLVER): 1 drawn; not drawn: 'rt.chain' (chainage only: no tower has a geographic position); "
            + "'ov.route' (only one tower has a geographic position (a polyline needs two))", map.Hint);
        Assert.Same(geo, map.Find("rt.geo"));
        Assert.Null(map.Find("rt.chain"));

        // No overlay, or none of its records a route: nothing, and no hint.
        Assert.Same(LineRouteMap.Empty, LineRouteMap.Build(null, m));
        Assert.Empty(LineRouteMap.Empty.Drawn);
        Assert.Equal("", LineRouteMap.Empty.Hint);
    }

    [Fact]
    public void A_click_on_the_route_polyline_selects_the_route_and_the_colour_is_the_carried_lines_loading_bin()
    {
        using UiOverlayFixture fx = Fixture();
        (LiveLoadResult loaded, var frames) = Run(fx.ManifestPath);
        SimFrame f = frames[^1].Frame;
        LineRouteMap map = LineRouteMap.Build(loaded.Overlay, f.Model);
        LineRoutePolyline geo = map.Drawn[0];

        SchematicView v = View(f.Model);
        IReadOnlyList<ScreenPoint> s = LineRouteMap.ToScreen(geo, v);
        var mid = new ScreenPoint((s[0].X + s[1].X) / 2, (s[0].Y + s[1].Y) / 2);
        Assert.Same(geo, map.HitTest(v, mid));
        Assert.Same(geo, map.HitTest(v, new ScreenPoint(mid.X + LineRouteMap.PickTolerancePx - 1, mid.Y)));   // the route is vertical
        Assert.Null(map.HitTest(v, new ScreenPoint(mid.X + LineRouteMap.PickTolerancePx + 1, mid.Y)));
        Assert.Null(LineRouteMap.Empty.HitTest(v, mid));

        // Before a state the route is neutral; with the frame's state it takes the carried line's corridor bin.
        Assert.Null(LineRouteMap.LoadingBucketOf(geo, v));
        v.ApplyState(f.Result);
        int bi = LineIndex(f.Model, "L3-4 N England-Yorks");
        SchematicEdge edge = v.Edges.Single(e => e.Valid && e.BranchIndex == bi);
        Assert.Equal(edge.LoadBucket, LineRouteMap.LoadingBucketOf(geo, v));
        // A route whose line is not in the model stays neutral.
        Assert.Null(LineRouteMap.LoadingBucketOf(geo with { BranchIndex = null }, v));
    }

    [Fact]
    public void Framing_a_route_fits_its_towers_in_the_view_with_the_fit_margin()
    {
        using UiOverlayFixture fx = Fixture();
        (LiveLoadResult loaded, var frames) = Run(fx.ManifestPath);
        GridModel m = frames[0].Frame.Model;
        LineRoutePolyline geo = LineRouteMap.Build(loaded.Overlay, m).Drawn[0];
        SchematicView v = View(m);
        Assert.True(LineRouteMap.Frame(v, geo));
        // The route is vertical (one longitude): the zoom is the height that fits, the box centred.
        double top = geo.Points.Min(p => p.Y), bottom = geo.Points.Max(p => p.Y);
        double zoom = Math.Clamp((700 - 2 * LineRouteMap.FramePadPx) / (bottom - top), SchematicView.MinZoom, v.MaxZoom);
        Assert.Equal(Bits(zoom), Bits(v.Zoom));
        Assert.Equal(Bits(900 / 2.0 - geo.Points[0].X * v.Zoom), Bits(v.OffsetX));
        Assert.Equal(Bits(700 / 2.0 - (top + (bottom - top) / 2) * v.Zoom), Bits(v.OffsetY));
        foreach (ScreenPoint p in LineRouteMap.ToScreen(geo, v))
        {
            Assert.InRange(p.X, 0, 900);
            Assert.InRange(p.Y, LineRouteMap.FramePadPx - 1e-6, 700 - LineRouteMap.FramePadPx + 1e-6);
        }
        // No viewport yet: nothing changes.
        var none = new SchematicView();
        none.Load(m);
        Assert.False(LineRouteMap.Frame(none, geo));
        Assert.Equal(1.0, none.Zoom);
    }

    // ================================================================================================================
    // Show on map, the View toggle and its setting
    // ================================================================================================================

    [Fact]
    public void Show_on_map_from_a_route_page_asks_for_the_route_and_its_line_and_a_span_page_has_no_place_on_the_map()
    {
        using UiOverlayFixture fx = Fixture();
        (LiveLoadResult loaded, var frames) = Run(fx.ManifestPath);
        AssetNavigatorModel nav = Navigator(fx.ManifestPath, loaded.Overlay, frames);
        GridModel m = nav.Index!.Model;
        var asked = new List<AssetMapTarget>();
        nav.ShowOnMapRequested += (_, t) => asked.Add(t);

        Page(nav, "rt.geo");
        Assert.True(nav.CanShowOnMap);
        Assert.Equal(new AssetMapTarget(null, LineIndex(m, "L3-4 N England-Yorks")) { LineRouteId = "rt.geo" }, nav.MapTarget);
        Assert.True(nav.ShowOnMap());
        Page(nav, "rt.chain");
        Assert.Equal(new AssetMapTarget(null, LineIndex(m, "L1-2 N-S Scotland")) { LineRouteId = "rt.chain" }, nav.MapTarget);
        Assert.True(nav.ShowOnMap());
        Assert.Equal(new[]
        {
            new AssetMapTarget(null, LineIndex(m, "L3-4 N England-Yorks")) { LineRouteId = "rt.geo" },
            new AssetMapTarget(null, LineIndex(m, "L1-2 N-S Scotland")) { LineRouteId = "rt.chain" },
        }, asked);
        // The route id is part of the target's identity: a plain branch target is a different target.
        Assert.NotEqual(new AssetMapTarget(null, LineIndex(m, "L3-4 N England-Yorks")), asked[0]);

        foreach (string id in new[] { "rt.g1", "ov.zebra", "ov.l6", "rt.ew" })
        {
            Page(nav, id);
            Assert.Null(nav.MapTarget);
            Assert.False(nav.CanShowOnMap);
            Assert.False(nav.ShowOnMap());
        }
        Assert.Equal(2, asked.Count);
    }

    private sealed class FakeMap : IMapActions, ILineRouteMapActions
    {
        public bool Routes { get; set; }
        public int Toggles { get; private set; }
        public bool LineRoutesShown => Routes;
        public Task ToggleLineRoutesAsync() { Toggles++; Routes = !Routes; return Task.CompletedTask; }
        public Task FitToViewAsync() => Task.CompletedTask;
        public Task ToggleMinimapAsync() => Task.CompletedTask;
        public Task ToggleCoastAsync() => Task.CompletedTask;
        public Task ToggleBoundariesAsync() => Task.CompletedTask;
        public Task ToggleGspAreasAsync() => Task.CompletedTask;
        public Task ToggleDnoAreasAsync() => Task.CompletedTask;
        public Task ToggleGspRegionsAsync() => Task.CompletedTask;
        public Task ToggleAssetsAsync() => Task.CompletedTask;
        public Task ToggleFleetAsync() => Task.CompletedTask;
        public Task ChooseColourModeAsync(MapColourMode mode) => Task.CompletedTask;
        public Task ClearSelectionAsync() => Task.CompletedTask;
        public Task TripLargestInfeedAsync() => Task.CompletedTask;
        public Task TripAtContextBusAsync() => Task.CompletedTask;
        public int? ContextBusId => null;
        public bool MinimapShown => false;
        public bool CoastShown => false;
        public bool BoundariesShown => false;
        public bool GspAreasShown => false;
        public bool DnoAreasShown => false;
        public bool GspRegionsShown => false;
        public bool AssetsShown => false;
        public bool FleetShown => false;
        public MapColourMode ColourMode => MapColourMode.Loading;
        public bool HasGeography => false;
    }

    [Fact]
    public async Task View_Line_routes_is_a_Run_mode_toggle_after_Fleet_whose_tick_follows_the_layer_and_is_absent_without_the_actions()
    {
        var r = new CommandRegistry();
        var map = new FakeMap();
        IReadOnlyList<AppCommand> added = MapCommands.Register(r, map, map);
        AppCommand routes = Assert.Single(added, c => c.Id == MapCommands.LineRoutes);
        Assert.Equal("view.lineRoutes", MapCommands.LineRoutes);
        Assert.Equal("Line routes", routes.Title);
        Assert.Equal("View/Line routes", routes.MenuPath);
        Assert.Equal(CommandModes.Run, routes.Modes);
        Assert.Null(routes.Gesture);
        Assert.True(routes.IsToggle);
        MenuNode view = Assert.Single(r.MenuTree(CommandModes.Run, CommandPlatforms.Desktop), n => n.Title == "View");
        Assert.Equal(new[] { "Fit to view", "Minimap", "Coast", "Boundaries", "GSP areas", "DNO areas", "GSP regions", "Assets", "Fleet", "Line routes",
            "Colour by", "Trip largest infeed", "Clear selection" }, view.Children.Select(n => n.Title));
        Assert.DoesNotContain(r.MenuTree(CommandModes.Author, CommandPlatforms.Desktop), n => n.Children.Any(c => c.Command == routes));

        // The tick reads the layer; running the command flips it and raises the tick change.
        int raised = 0;
        routes.CheckedChanged += (_, _) => raised++;
        Assert.False(routes.Checked);
        await routes.RunAsync();
        Assert.True(routes.Checked);
        Assert.Equal(1, map.Toggles);
        Assert.True(raised >= 1);
        await routes.RunAsync();
        Assert.False(routes.Checked);

        // Without the line route actions the commands are exactly as before.
        var plain = new CommandRegistry();
        IReadOnlyList<AppCommand> before = MapCommands.Register(plain, map);
        Assert.DoesNotContain(before, c => c.Id == MapCommands.LineRoutes);
        Assert.Equal(added.Select(c => c.Id).Where(id => id != MapCommands.LineRoutes), before.Select(c => c.Id));
    }

    [Fact]
    public void The_Line_routes_setting_is_off_by_default_and_round_trips_through_the_settings_file()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-5d-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "settings.json");
            ShellSettingsStore store = ShellSettingsStore.ForFile(file);
            Assert.False(new AppSettings().LineRoutes);
            AppSettings s = store.Load();
            Assert.False(s.LineRoutes);
            s.LineRoutes = true;
            s.Save();
            Assert.Null(s.SaveProblem);
            Assert.Contains("\"LineRoutes\": true", File.ReadAllText(file), StringComparison.Ordinal);
            Assert.True(store.Load().LineRoutes);
            // A file written before the member existed reads it as off, and keeps every other flag.
            File.WriteAllText(file, """{ "Coast": true, "Fleet": true }""");
            AppSettings old = store.Load();
            Assert.Null(old.LoadProblem);
            Assert.False(old.LineRoutes);
            Assert.True(old.Coast && old.Fleet);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { /* a leftover temp folder is harmless */ }
        }
    }

    // ================================================================================================================
    // The solver never sees the overlay
    // ================================================================================================================

    [Fact]
    public void The_solved_frames_are_bit_identical_with_and_without_the_route_overlay()
    {
        using UiOverlayFixture plain = Fixture(withOverlay: false);
        using UiOverlayFixture overlaid = Fixture();
        (LiveLoadResult a, var fa) = Run(plain.ManifestPath, 3);
        (LiveLoadResult b, var fb) = Run(overlaid.ManifestPath, 3);
        Assert.Null(a.Overlay);
        Assert.NotNull(b.Overlay);
        Assert.Equal(7 + 9, b.Overlay!.Records.Count);   // file B's 7 and this test's 9
        for (int i = 0; i < fa.Length; i++)
        {
            PowerFlowResult x = fa[i].Frame.Result, y = fb[i].Frame.Result;
            Assert.Equal(x.BusIds, y.BusIds);
            foreach ((double[] p, double[] q) in new[] { (x.Vm, y.Vm), (x.VaDeg, y.VaDeg), (x.PInjMw, y.PInjMw), (x.QInjMvar, y.QInjMvar) })
                Assert.Equal(p.Select(Bits), q.Select(Bits));
            Assert.Equal(x.LineFlows.Count, y.LineFlows.Count);
            for (int k = 0; k < x.LineFlows.Count; k++)
            {
                LineFlow u = x.LineFlows[k], w = y.LineFlows[k];
                Assert.Equal((u.From, u.To, u.Name), (w.From, w.To, w.Name));
                Assert.Equal(new[] { u.PFromMw, u.QFromMvar, u.PToMw, u.QToMvar, u.PLossMw, u.QLossMvar, u.RatingMva, u.LoadingPercent }.Select(Bits),
                    new[] { w.PFromMw, w.QFromMvar, w.PToMw, w.QToMvar, w.PLossMw, w.QLossMvar, w.RatingMva, w.LoadingPercent }.Select(Bits));
            }
            Assert.Equal(Bits(x.TotalLossMw), Bits(y.TotalLossMw));
            Assert.Equal(fa[i].Frame.Model.Branches.Select(br => Bits(br.LengthKm)), fb[i].Frame.Model.Branches.Select(br => Bits(br.LengthKm)));
        }
    }
}
