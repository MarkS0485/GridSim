using GridSim.Core.IO.Json.Overlay;
using GridSim.Core.IO.Json.Schema;
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
using GridSim.Ui.Dashboard;
using GridSim.Ui.Hosting;
using GridSim.Ui.Models;
using Xunit;

namespace GridSim.Tests.Ui;

/// <summary>
/// UI programme Phase 5e (2026-10-20): the earthing and soil pages of the asset overlay, headless, on the 5-view fixture
/// (UiOverlayFixture: data/uk as a v2 case with the soil zone 'ov.soil', the earth path 'ov.earth' under the substation
/// 'ov.sub', and the earth wire 'ov.ew' under the route of line L2-3). Pinned exactly: each page's kind facts, its tables
/// (columns with units, every cell as the overlay file writes it) and its link rows; each link selects the right node;
/// every page and table says "carried, not used by the solver"; the generic part of each page is unchanged (the kind part
/// is added after it); and in Author mode the earthing values edit through the existing field grid, one undo step each.
/// </summary>
public sealed class UiOverlayEarthingPagesTests
{
    private static LicenceGate Gate() => CaseSessionTests.Unlimited();

    // Two solved frames of a v2 case, from the live host exactly as the app runs it (as UiAssetOverlayTests).
    private static (LiveLoadResult Loaded, (SimFrame Frame, string Clock)[] Frames) Run(string manifest)
    {
        var host = new SimHost();
        var dash = new DashboardModel();
        LiveLoadResult loaded = host.LoadLive(ModelCatalog.V2CaseSource(manifest), SolverKind.Auto, new LicenceGate(Entitlement.FreeDefault));
        Assert.True(loaded.Loaded, loaded.Refusal);
        var frames = new (SimFrame, string)[2];
        for (int i = 0; i < frames.Length; i++)
        {
            HostFrame hf = Assert.IsType<HostFrame>(host.ProduceNext());
            Assert.Equal(HostFrameKind.LiveSolved, hf.Kind);
            dash.Update(hf.Frame!, hf.Boundaries, hf.SolveMs);
            frames[i] = (hf.Frame!, dash.Clock);
        }
        return (loaded, frames);
    }

    private static AssetNavigatorModel Navigator(string manifest, CaseAssetOverlay? overlay, (SimFrame Frame, string Clock)[] frames,
        IReadOnlyList<SchemaError>? problems = null)
    {
        var nav = new AssetNavigatorModel { Active = true };
        nav.SetSource(AssetCaseSource.From(ModelCatalog.V2CaseSource(manifest)), Gate(), overlay, problems);
        foreach (var (f, c) in frames) nav.Update(f, c);
        Assert.NotNull(nav.Index);
        return nav;
    }

    // The fixture's navigator, with the record 'id' selected and its OVERVIEW page returned.
    private static AssetOverviewPage Page(AssetNavigatorModel nav, string id)
    {
        nav.Select(nav.Tree.NodeFor(nav.Index!.Overlay!.Find(id)!.Key)!);
        return Assert.IsType<AssetOverviewPage>(nav.Current);
    }

    // The table with this caption on the page (exactly one).
    private static OverviewTable Table(AssetOverviewPage page, string title) => Assert.Single(page.Tables, t => t.Title == title);

    // The rows of a table as plain string arrays (for exact comparison).
    private static string[][] Cells(OverviewTable t) => t.Rows.Select(r => r.Cells.ToArray()).ToArray();

    // Every page says it is carried and not used by the solver, in its sub-title, its note and every table caption.
    private static void AssertCarried(AssetOverviewPage page)
    {
        Assert.Contains(OverlayIndex.NotUsedBySolver, page.Sub, StringComparison.Ordinal);
        Assert.Equal(OverlayIndex.SolverNote, page.Note);
        Assert.NotEmpty(page.Tables);
        Assert.True(page.HasTables);
        foreach (OverviewTable t in page.Tables)
        {
            Assert.EndsWith(OverlayKindPages.TableTag, t.Title, StringComparison.Ordinal);
            Assert.Equal("  (CARRIED, NOT USED BY THE SOLVER)", OverlayKindPages.TableTag);
            Assert.All(t.Rows, r => Assert.Equal(t.Columns.Count, r.Cells.Count));
        }
    }

    // A link row selects the node of what it names (the canonical node of a record, the tree node of a model asset).
    private static void AssertOpens(AssetNavigatorModel nav, OverviewRow row)
    {
        Assert.NotNull(row.Target);
        Assert.True(nav.OpenKey(row.Target!), "row '" + row.Name + "' opened nothing");
        Assert.Equal(row.Target, nav.Selected!.Key);
        Assert.Same(nav.Tree.NodeFor(row.Target!), nav.Selected);
        nav.GoBack();
    }

    [Fact]
    public void The_soil_zone_page_shows_its_resistivity_depth_and_the_earth_paths_in_it_with_units_and_links()
    {
        using var fx = new UiOverlayFixture();
        (LiveLoadResult loaded, var frames) = Run(fx.ManifestPath);
        AssetNavigatorModel nav = Navigator(fx.ManifestPath, loaded.Overlay, frames);
        OverlayIndex ov = nav.Index!.Overlay!;
        GridModel m = nav.Index.Model;
        string bus1 = "bus 1 '" + m.Buses.First(b => b.Id == 1).Name + "'";

        AssetOverviewPage soil = Page(nav, "ov.soil");
        AssertCarried(soil);
        Assert.Equal("Soil zone  -  carried, not used by the solver", soil.Sub);
        // The generic part first, unchanged, then the kind part.
        Assert.Equal(new[]
        {
            new OverviewFact("Kind", "soilZone  (Soil zone)"),
            new OverviewFact("Written in", UiOverlayFixture.FileB + "  /records/6"),
            new OverviewFact("Parent", "none (a top-level record)"),
            new OverviewFact("Children", "0"),
            new OverviewFact("Provenance", "none given"),
            new OverviewFact("Note", "none"),
            new OverviewFact("Parameter resistivityOhmM", "100 ohm.m"),
            new OverviewFact("Parameter depthM", "2 m"),
            new OverviewFact("Soil resistivity", "100 ohm.m"),
            new OverviewFact("Upper layer depth", "2 m"),
            new OverviewFact("Extent", "not carried: the soilZone kind declares no geometry and no references; the earth paths that point at the zone are its extent"),
            new OverviewFact("Earth paths in this zone", "1"),
        }, soil.Facts);

        Assert.Equal(new[] { OverlayKindPages.SoilTable, OverlayKindPages.SoilPathsTable }, soil.Tables.Select(t => t.Title));
        OverviewTable own = Table(soil, OverlayKindPages.SoilTable);
        Assert.Equal(new[] { "Quantity", "Value", "Unit" }, own.Columns);
        Assert.Equal(new[] { new[] { "Resistivity", "100", "ohm.m" }, new[] { "Upper layer depth", "2", "m" } }, Cells(own));
        Assert.Equal("As written in " + UiOverlayFixture.FileB + " /records/6; absent depth = uniform soil.", own.Note);
        OverviewTable paths = Table(soil, OverlayKindPages.SoilPathsTable);
        Assert.Equal(new[] { "Earth path", "Bus", "Resistance (ohm)", "Under" }, paths.Columns);
        Assert.Equal(new[] { new[] { "ov.earth", bus1, "0.5", "substation 'ov.sub'" } }, Cells(paths));
        Assert.Equal("", paths.Note);

        // The links: the earth path in the zone (its record) and the bus it earths.
        Assert.Equal(new[]
        {
            new OverviewRow("ov.earth", "earth path in this soil zone  Earth path", "earthPath", ov.Find("ov.earth")!.Key),
            new OverviewRow("bus 1", "earthed by 'ov.earth':  " + bus1, "model", AssetKey.Substation(1)),
        }, soil.Rows);
        foreach (OverviewRow row in soil.Rows.ToList()) AssertOpens(nav, row);
    }

    [Fact]
    public void The_earth_path_page_shows_its_bus_resistance_and_soil_zone_with_units_and_links()
    {
        using var fx = new UiOverlayFixture();
        (LiveLoadResult loaded, var frames) = Run(fx.ManifestPath);
        AssetNavigatorModel nav = Navigator(fx.ManifestPath, loaded.Overlay, frames);
        OverlayIndex ov = nav.Index!.Overlay!;
        GridModel m = nav.Index.Model;
        string bus1 = "bus 1 '" + m.Buses.First(b => b.Id == 1).Name + "'";

        AssetOverviewPage path = Page(nav, "ov.earth");
        AssertCarried(path);
        Assert.Equal(new[]
        {
            new OverviewFact("Earths bus", bus1),
            new OverviewFact("Earth resistance", "0.5 ohm"),
            new OverviewFact("Soil zone", "ov.soil  (100 ohm.m)"),
            new OverviewFact("Neutral earthing", "the authored transformers' neutralXPu stay the earthing the fault studies use; this record does not change them"),
        }, path.Facts.Skip(7));   // after the generic seven (kind, file, parent, children, prov, note, the one parameter)
        Assert.Equal(new OverviewFact("Parameter resistanceOhm", "0.5 ohm"), path.Facts[6]);
        OverviewTable t = Assert.Single(path.Tables);
        Assert.Equal(OverlayKindPages.EarthPathTable, t.Title);
        Assert.Equal(new[] { "Bus", "Resistance (ohm)", "Soil zone", "Soil resistivity (ohm.m)", "Under" }, t.Columns);
        Assert.Equal(new[] { new[] { bus1, "0.5", "ov.soil", "100", "substation 'ov.sub'" } }, Cells(t));
        Assert.Equal("As written in " + UiOverlayFixture.FileA + " /records/10.", t.Note);

        // The links are the generic ones (parent, the bus, the soil zone): each selects its node.
        Assert.Equal(new[]
        {
            new OverviewRow("ov.sub", "parent  Substation", "substation", ov.Find("ov.sub")!.Key),
            new OverviewRow("bus 1", "reference 'bus':  " + bus1, "model", AssetKey.Substation(1)),
            new OverviewRow("record 'ov.soil'", "reference 'soil':  Soil zone 'ov.soil'", "record", ov.Find("ov.soil")!.Key),
        }, path.Rows);
        foreach (OverviewRow row in path.Rows.ToList()) AssertOpens(nav, row);
    }

    [Fact]
    public void The_earth_wire_page_shows_the_line_it_rides_its_count_bonding_and_conductor_and_says_the_per_span_resistance_is_not_carried()
    {
        using var fx = new UiOverlayFixture();
        (LiveLoadResult loaded, var frames) = Run(fx.ManifestPath);
        AssetNavigatorModel nav = Navigator(fx.ManifestPath, loaded.Overlay, frames);
        OverlayIndex ov = nav.Index!.Overlay!;
        GridModel m = nav.Index.Model;
        int li = Enumerable.Range(0, m.Branches.Count).Single(i => !m.Branches[i].IsTransformer && m.Branches[i].Name == "L2-3 B6 boundary");
        string line = "line #" + li + "  " + m.Branches[li].From + " -> " + m.Branches[li].To;

        AssetOverviewPage ew = Page(nav, "ov.ew");
        AssertCarried(ew);
        Assert.Equal(new[]
        {
            new OverviewFact("Rides line", line + "  (through line route 'ov.route')"),
            new OverviewFact("Earth wires", "1"),
            new OverviewFact("Bonded at both ends", "true"),
            new OverviewFact("Conductor", "ov.zebra (Zebra)"),
            new OverviewFact("Earth resistance per span", "not carried: the earthWire kind has no per-span earth resistance parameter"),
            new OverviewFact("Zero sequence", "the line's authored r0Pu and x0Pu stay what the fault studies use; this record does not change them"),
        }, ew.Facts.Skip(8));   // after the generic eight (six, then count and bondedAtBothEnds)
        OverviewTable t = Assert.Single(ew.Tables);
        Assert.Equal(OverlayKindPages.EarthWireTable, t.Title);
        Assert.Equal(new[] { "Line", "Route", "Earth wires (count)", "Bonded at both ends", "Conductor", "Cross-section (mm2)", "DC resistance (ohm/km)" }, t.Columns);
        Assert.Equal(new[] { new[] { line, "ov.route", "1", "true", "ov.zebra (Zebra)", "428.9", "0.0674" } }, Cells(t));

        // The kind's own link: the line, reached through the route (the earthWire kind has no line reference itself).
        OverviewRow rides = ew.Rows[^1];
        Assert.Equal(new OverviewRow("line 'L2-3 B6 boundary'", "rides this line, through line route 'ov.route':  " + line, "model", AssetKey.Branch(m, li)), rides);
        Assert.Equal(AssetKeyKind.Circuit, rides.Target!.Kind);
        Assert.Equal(new[] { "ov.route", "record 'ov.zebra'", "line 'L2-3 B6 boundary'" }, ew.Rows.Select(r => r.Name));
        foreach (OverviewRow row in ew.Rows.ToList()) AssertOpens(nav, row);
    }

    [Fact]
    public void The_substation_page_has_an_earthing_summary_of_its_earth_paths_and_their_soil_zones()
    {
        using var fx = new UiOverlayFixture();
        (LiveLoadResult loaded, var frames) = Run(fx.ManifestPath);
        AssetNavigatorModel nav = Navigator(fx.ManifestPath, loaded.Overlay, frames);
        OverlayIndex ov = nav.Index!.Overlay!;
        GridModel m = nav.Index.Model;
        string bus1 = "bus 1 '" + m.Buses.First(b => b.Id == 1).Name + "'";

        AssetOverviewPage sub = Page(nav, "ov.sub");
        AssertCarried(sub);
        Assert.Equal(new OverviewFact("Earthing", "1 earth path, 1 soil zone"), sub.Facts[^1]);
        Assert.Single(sub.Facts, f => f.Label == "Earthing");
        OverviewTable t = Assert.Single(sub.Tables);
        Assert.Equal(OverlayKindPages.EarthingTable, t.Title);
        Assert.Equal(new[] { "Earth path", "Bus", "Resistance (ohm)", "Soil zone", "Soil resistivity (ohm.m)" }, t.Columns);
        Assert.Equal(new[] { new[] { "ov.earth", bus1, "0.5", "ov.soil", "100" } }, Cells(t));
        Assert.Equal("The earth path records under this substation, in overlay order.", t.Note);
        OverviewRow zone = sub.Rows[^1];
        Assert.Equal(new OverviewRow("ov.soil", "earthing: soil zone of 'ov.earth'", "soilZone", ov.Find("ov.soil")!.Key), zone);
        AssertOpens(nav, zone);
        // The earth path itself is one of the substation's child rows (the generic part), and opens its record.
        AssertOpens(nav, sub.Rows.Single(r => r.Name == "ov.earth" && r.Detail == "child  Earth path"));
    }

    [Fact]
    public void A_substation_with_no_earth_path_and_a_soil_zone_with_none_say_so_and_absent_values_read_absent()
    {
        // File A without its earth path; the soil zone without its depth (uniform soil).
        string a = string.Join("\n", UiOverlayFixture.OverlayA.Split('\n').Where(l => !l.Contains("\"id\": \"ov.earth\"", StringComparison.Ordinal)));
        Assert.NotEqual(UiOverlayFixture.OverlayA, a);
        using var fx = new UiOverlayFixture(withOverlay: false);
        UiOverlayFixture.AddOverlay(fx.ManifestPath, a, UiOverlayFixture.OverlayB.Replace(", \"depthM\": 2", "", StringComparison.Ordinal));
        (LiveLoadResult loaded, var frames) = Run(fx.ManifestPath);
        AssetNavigatorModel nav = Navigator(fx.ManifestPath, loaded.Overlay, frames);

        AssetOverviewPage sub = Page(nav, "ov.sub");
        Assert.Equal(new OverviewFact("Earthing", "no earth path under this substation"), sub.Facts[^1]);
        OverviewTable t = Assert.Single(sub.Tables);
        Assert.Empty(t.Rows);
        Assert.Equal("No earth path record sits under this substation.", t.Note);

        AssetOverviewPage soil = Page(nav, "ov.soil");
        Assert.Contains(new OverviewFact("Upper layer depth", "absent (uniform soil)"), soil.Facts);
        Assert.Contains(new OverviewFact("Earth paths in this zone", "0"), soil.Facts);
        Assert.Equal(new[] { "Upper layer depth", OverlayKindPages.Absent, "m" }, Table(soil, OverlayKindPages.SoilTable).Rows[1].Cells);
        OverviewTable none = Table(soil, OverlayKindPages.SoilPathsTable);
        Assert.Empty(none.Rows);
        Assert.Equal("No earth path of the overlay points at this soil zone.", none.Note);
        Assert.Empty(soil.Rows);
    }

    [Fact]
    public void A_table_whose_row_does_not_match_its_columns_is_refused_loudly()
    {
        var ex = Assert.Throws<ArgumentException>(() => OverviewTable.Of("T", new[] { "a", "b" }, new[] { new[] { "1" } }));
        Assert.Contains("table 'T': a row has 1 cells for 2 columns.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void In_Author_mode_the_earthing_values_edit_through_the_existing_field_grid_one_undo_step_each()
    {
        using var fx = new UiOverlayFixture();
        CaseAuthoring a = CaseAuthoring.Open(AssetCaseSource.V2(fx.ManifestPath), Gate(), Array.Empty<string>());
        CaseRef Ov(string id) => a.Collections().Where(c => c.Role == Core.IO.Json.CaseEditing.CaseRoles.Overlay).SelectMany(a.Records)
            .Select(r => r.Ref).First(r => a.OverlayIdentity(r).Id == id);

        int depth = a.UndoDepth;
        FieldGridModel soil = a.FieldGrid(Ov("ov.soil"));
        Assert.True(soil.IsOverlay);
        AuthoringResult r1 = soil.Edit("params.resistivityOhmM", "250");
        Assert.True(r1.Ok && r1.Changed, r1.Message);
        Assert.Equal(depth + 1, a.UndoDepth);
        AuthoringResult r2 = a.FieldGrid(Ov("ov.earth")).Edit("params.resistanceOhm", "0.25");
        Assert.True(r2.Ok && r2.Changed, r2.Message);
        Assert.Equal(depth + 2, a.UndoDepth);
        AuthoringResult r3 = a.FieldGrid(Ov("ov.ew")).Edit("params.count", "2");
        Assert.True(r3.Ok && r3.Changed, r3.Message);
        Assert.Equal(depth + 3, a.UndoDepth);

        // The session as it is now carries the new values (the {v, unit} form keeps its unit; the plain form stays plain).
        IReadOnlyList<AssetOverlayRecord> now = a.OverlayRecordsNow();
        Assert.Equal("250", OverlayKindPages.Written(now.Single(x => x.Id == "ov.soil"), "resistivityOhmM"));
        Assert.Equal("0.25", OverlayKindPages.Written(now.Single(x => x.Id == "ov.earth"), "resistanceOhm"));
        Assert.Equal("2", OverlayKindPages.Written(now.Single(x => x.Id == "ov.ew"), "count"));
        // A negative resistance is refused by the kind spec's minimum, nothing changed.
        AuthoringResult bad = a.FieldGrid(Ov("ov.earth")).Edit("params.resistanceOhm", "-1");
        Assert.False(bad.Ok);
        Assert.Equal(depth + 3, a.UndoDepth);
        a.Undo();
        a.Undo();
        a.Undo();
        Assert.Equal(depth, a.UndoDepth);
        Assert.Equal("100", OverlayKindPages.Written(a.OverlayRecordsNow().Single(x => x.Id == "ov.soil"), "resistivityOhmM"));
    }
}
