using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Overlay;
using GridSim.Core.IO.Json.Schema;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Tests.Editor;
using GridSim.Ui.Assets.Index;
using GridSim.Ui.Assets.Keys;
using GridSim.Ui.Assets.Navigation;
using GridSim.Ui.Assets.Overlay;
using GridSim.Ui.Assets.Overviews;
using GridSim.Ui.Assets.Pages;
using GridSim.Ui.Dashboard;
using GridSim.Ui.Diagrams;
using GridSim.Ui.Hosting;
using GridSim.Ui.Models;
using GridSim.Ui.Presentation;
using Xunit;

namespace GridSim.Tests.Ui;

/// <summary>
/// UI programme ASSETS slice 5-view (Run mode, read-only): the asset overlay in the ASSETS tab, headless.
/// <list type="bullet">
/// <item>the app's run pipeline loads a JSON v2 case through the v2 loader (SimHost), carrying its overlay beside the model,
/// refusing a bad case or a bad overlay loudly ("Load failed: ..."), with frames bit-identical with and without the
/// overlay;</item>
/// <item>the OverlayIndex: the parent / child tree, the by-kind view, every reference resolved to the AssetKey of the tree
/// node it names, the model assets each record attaches to, and the checker's problems placed on their records;</item>
/// <item>the tree: OVERLAY by kind (canonical nodes) and the records nested under the model assets they attach to
/// (attachment nodes, each with a key of its own), every key one node;</item>
/// <item>the navigator: OVERLAY on a model asset, PROPERTIES (as written) and CHECK on a record, every overlay page saying
/// "carried, not used by the solver", links selecting the right node, the substation diagram;</item>
/// <item>the overlay never changes any displayed model value: every A2/B2 page, PROPERTIES and CONNECTIONS identical with
/// and without the overlay on the same case and frames.</item>
/// </list>
/// </summary>
public sealed class UiAssetOverlayTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static LicenceGate Gate() => CaseSessionTests.Unlimited();

    private static long Bits(double v) => BitConverter.DoubleToInt64Bits(v);

    // Two solved frames of a v2 case, from the live host exactly as the app runs it.
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

    // The app's navigator, declared the v2 source (with the overlay the load carried) and fed the frames, shown.
    private static AssetNavigatorModel Navigator(string manifest, CaseAssetOverlay? overlay, (SimFrame Frame, string Clock)[] frames,
        IReadOnlyList<SchemaError>? problems = null)
    {
        var nav = new AssetNavigatorModel { Active = true };
        nav.SetSource(AssetCaseSource.From(ModelCatalog.V2CaseSource(manifest)), Gate(), overlay, problems);
        foreach (var (f, c) in frames) nav.Update(f, c);
        Assert.NotNull(nav.Index);
        return nav;
    }

    private static List<AssetTreeNode> All(AssetTree t) => t.PreOrder().ToList();

    private static bool IsOverlayNode(AssetTreeNode n) =>
        n.Key.Kind == AssetKeyKind.OverlayRecord || n.Id.StartsWith("group:overlay:", StringComparison.Ordinal)
        || n.Id == "class:" + AssetPageKind.OverlayFleet;

    // ================================================================ the v2 load path (SimHost)

    [Fact]
    public void The_app_loads_a_v2_case_through_the_v2_loader_and_carries_its_overlay_beside_the_model()
    {
        using var fx = new UiOverlayFixture();
        ModelSource src = ModelCatalog.V2CaseSource(fx.ManifestPath);
        Assert.Equal(Path.GetFullPath(fx.ManifestPath), src.V2Case);
        Assert.Equal(Path.GetFullPath(fx.CaseDir).TrimEnd(Path.DirectorySeparatorChar), src.Dir.TrimEnd(Path.DirectorySeparatorChar));
        Assert.Equal("v2  uk.case.json", src.Label);
        Assert.Equal(Path.GetFullPath(fx.ManifestPath), src.Key);
        AssetCaseSource acs = AssetCaseSource.From(src);
        Assert.Equal(AssetSourceKind.V2Case, acs.Kind);
        Assert.Equal(src.V2Case, acs.Path);
        Assert.True(acs.IsCaseFile);

        (LiveLoadResult loaded, _) = Run(fx.ManifestPath);
        CaseAssetOverlay ov = Assert.IsType<CaseAssetOverlay>(loaded.Overlay);
        Assert.Equal(new[] { UiOverlayFixture.FileA, UiOverlayFixture.FileB }, ov.Documents);
        Assert.Equal(UiOverlayFixture.RecordCount, ov.Records.Count);
        Assert.Contains($"overlay: {UiOverlayFixture.RecordCount} records carried, not used by the solver", loaded.Notes);

        // The model is the v2 loader's (the same loader the CLI runs, against the repository's schemas), to the bit.
        GridModel direct = V2CaseLoader.Load(fx.ManifestPath, V2.V2TestSupport.SchemasDir, EditorTestSupport.DataDir);
        GridModel viaHost = loaded.Model!;
        Assert.Equal(direct.Buses.Select(b => (b.Id, b.Name, Bits(b.BaseKv), Bits(b.Pd), Bits(b.Qd))), viaHost.Buses.Select(b => (b.Id, b.Name, Bits(b.BaseKv), Bits(b.Pd), Bits(b.Qd))));
        Assert.Equal(direct.Branches.Select(b => (b.From, b.To, b.Name, Bits(b.R), Bits(b.X), Bits(b.B))), viaHost.Branches.Select(b => (b.From, b.To, b.Name, Bits(b.R), Bits(b.X), Bits(b.B))));
        Assert.Equal(direct.AllGenerators.Select(g => (g.Bus, g.Name, Bits(g.Pg), Bits(g.MBase))), viaHost.AllGenerators.Select(g => (g.Bus, g.Name, Bits(g.Pg), Bits(g.MBase))));

        // A v1 folder and a MATPOWER file carry no overlay and no notes, as before.
        LiveLoadResult v1 = new SimHost().LoadLive(new ModelSource("uk", Path.Combine(EditorTestSupport.DataDir, "uk")), SolverKind.Auto, new LicenceGate(Entitlement.FreeDefault));
        Assert.True(v1.Loaded, v1.Refusal);
        Assert.Null(v1.Overlay);
        Assert.Empty(v1.Notes);
    }

    [Fact]
    public void Frames_are_bit_identical_with_and_without_the_overlay()
    {
        using var plain = new UiOverlayFixture(withOverlay: false);
        using var overlaid = new UiOverlayFixture();
        (LiveLoadResult a, var fa) = Run(plain.ManifestPath);
        (LiveLoadResult b, var fb) = Run(overlaid.ManifestPath);
        Assert.Null(a.Overlay);
        Assert.NotNull(b.Overlay);
        Assert.Equal(a.Notes.Append($"overlay: {UiOverlayFixture.RecordCount} records carried, not used by the solver"), b.Notes);
        Assert.Equal(a.ActiveSolver, b.ActiveSolver);
        for (int i = 0; i < fa.Length; i++)
        {
            SimFrame x = fa[i].Frame, y = fb[i].Frame;
            Assert.Equal(x.Result.Converged, y.Result.Converged);
            Assert.Equal(x.Result.BusIds, y.Result.BusIds);
            Assert.Equal(x.Result.Vm.Select(Bits), y.Result.Vm.Select(Bits));
            Assert.Equal(x.Result.VaDeg.Select(Bits), y.Result.VaDeg.Select(Bits));
            Assert.Equal(Bits(x.FrequencyHz), Bits(y.FrequencyHz));
            Assert.Equal(Bits(x.GenerationMw), Bits(y.GenerationMw));
            Assert.Equal(Bits(x.DemandMw), Bits(y.DemandMw));
            Assert.Equal(x.Model.Generators.Select(g => Bits(g.Pg)), y.Model.Generators.Select(g => Bits(g.Pg)));
            Assert.Equal(fa[i].Clock, fb[i].Clock);
        }
    }

    [Fact]
    public void A_v2_case_the_loader_refuses_is_refused_loudly_and_nothing_loads()
    {
        // An overlay that fails its checker: the whole case is refused, naming every problem (the loader's fail-loud rule).
        string bad = UiOverlayFixture.OverlayA.Replace("\"resistanceOhm\": { \"v\": 0.5, \"unit\": \"ohm\" }", "\"resistanceOhm\": -1", StringComparison.Ordinal)
            .Replace("\"line\": { \"line\": \"L1-2 N-S Scotland\" }", "\"line\": { \"line\": \"No such line\" }", StringComparison.Ordinal);
        Assert.NotEqual(UiOverlayFixture.OverlayA, bad);
        using var fx = new UiOverlayFixture(overlayA: bad);
        var host = new SimHost();
        LiveLoadResult r = host.LoadLive(ModelCatalog.V2CaseSource(fx.ManifestPath), SolverKind.Auto, new LicenceGate(Entitlement.FreeDefault));
        Assert.False(r.Loaded);
        Assert.Null(r.Model);
        Assert.Null(r.Overlay);
        Assert.StartsWith("Load failed: ", r.Refusal, StringComparison.Ordinal);
        Assert.Contains("record 'ov.earth': -1 ohm is below the minimum 0 for 'resistanceOhm'.", r.Refusal, StringComparison.Ordinal);
        Assert.Contains("record 'ov.cbl': role 'line' points at line 'No such line', which is not a line of the case.", r.Refusal, StringComparison.Ordinal);
        Assert.False(host.HasLiveSolver);
        Assert.Null(host.ProduceNext());

        // A file that is not a v2 case manifest is refused too.
        string notACase = Path.Combine(fx.CaseDir, "network.json");
        LiveLoadResult n = host.LoadLive(ModelCatalog.V2CaseSource(notACase), SolverKind.Auto, new LicenceGate(Entitlement.FreeDefault));
        Assert.False(n.Loaded);
        Assert.Contains("not a JSON v2 case manifest", n.Refusal, StringComparison.Ordinal);
        Assert.False(host.HasLiveSolver);
    }

    // ================================================================ the overlay index

    [Fact]
    public void The_overlay_index_holds_the_parent_child_tree_the_by_kind_view_and_every_reference_resolved_to_its_key()
    {
        using var fx = new UiOverlayFixture();
        (LiveLoadResult loaded, var frames) = Run(fx.ManifestPath);
        AssetNavigatorModel nav = Navigator(fx.ManifestPath, loaded.Overlay, frames);
        AssetIndex ix = nav.Index!;
        Assert.True(ix.HasCase, ix.CaseNote);
        OverlayIndex ov = Assert.IsType<OverlayIndex>(ix.Overlay);
        GridModel m = ix.Model;

        // Every record, in overlay order (file A then file B), each with its canonical key and page.
        string[] ids = { "ov.bb1", "ov.sub", "ov.bb2", "ov.cpl", "ov.cbl", "ov.cbt", "ov.cbg", "ov.cbd", "ov.ds", "ov.es", "ov.earth", "ov.t3w", "ov.hvdc",
                         "ov.zebra", "ov.l6", "ov.l6.a", "ov.route", "ov.span1", "ov.ew", "ov.soil" };
        Assert.Equal(ids, ov.Entries.Select(e => e.Id));
        for (int i = 0; i < ids.Length; i++)
        {
            OverlayEntry e = ov.Entries[i];
            Assert.Equal(i, e.Ordinal);
            Assert.Equal(AssetKey.ForOverlay(new OverlayRef(ids[i])), e.Key);
            Assert.Equal(new AssetPageKey(AssetPageKind.OverlayRecord, i, 0, 0, ids[i]), e.Page);
            Assert.Same(e, ov.Find(ids[i]));
            Assert.Same(e, ov.EntryOf(e.Key));
            Assert.Same(e, ov.EntryOf(AssetKey.ForOverlay(new OverlayRef(ids[i], "bus:1/overlay:x"))));   // any node of it
            Assert.Empty(e.Problems);
        }
        Assert.Null(ov.Find("nowhere"));
        Assert.Null(ov.EntryOf(AssetKey.Substation(1)));

        // The parent / child tree: children in overlay order (the busbar section written before its parent included).
        Assert.Equal(new[] { "ov.bb1", "ov.bb2", "ov.cpl", "ov.cbl", "ov.cbt", "ov.cbg", "ov.cbd", "ov.ds", "ov.es", "ov.earth", "ov.t3w" },
            ov.Find("ov.sub")!.Children.Select(c => c.Id));
        Assert.Same(ov.Find("ov.sub"), ov.Find("ov.bb1")!.Parent);
        Assert.Equal(new[] { "ov.span1", "ov.ew" }, ov.Find("ov.route")!.Children.Select(c => c.Id));
        Assert.Equal(new[] { "ov.l6.a" }, ov.Find("ov.l6")!.Children.Select(c => c.Id));
        Assert.Null(ov.Find("ov.sub")!.Parent);
        Assert.Equal("Substation", ov.Find("ov.sub")!.KindTitle);
        Assert.Equal("Busbar section", ov.Find("ov.bb1")!.KindTitle);

        // The by-kind view: all thirteen kinds, ordinal by name, records in overlay order.
        Assert.Equal(new[] { "busbarSection", "conductorPosition", "conductorType", "earthPath", "earthWire", "hvdcLink", "lineRoute",
                             "soilZone", "span", "substation", "switchingDevice", "towerType", "transformer3W" }, ov.ByKind.Select(g => g.Kind));
        Assert.Equal(new[] { "ov.cpl", "ov.cbl", "ov.cbt", "ov.cbg", "ov.cbd", "ov.ds", "ov.es" },
            ov.ByKind.Single(g => g.Kind == "switchingDevice").Records.Select(e => e.Id));
        Assert.Equal(ov.Entries.Count, ov.ByKind.Sum(g => g.Records.Count));

        // References: each to the key of the tree node it names, with the words it shows.
        int Branch(string name) => Enumerable.Range(0, m.Branches.Count).Single(i => m.Branches[i].Name == name);
        int Gen(string name) => Enumerable.Range(0, m.Generators.Count).Single(i => m.Generators[i].Name == name);
        DemandRecord northScotland = ix.Demands.Single(d => d.Name == "North Scotland");
        OverlayRefLink R(string id, string role) => ov.Find(id)!.Refs.Single(r => r.Role == role);
        Assert.Equal(AssetKey.Substation(1), R("ov.bb1", "bus").Target);
        Assert.Equal("bus 1", R("ov.bb1", "bus").Written);
        Assert.Equal("bus 1 '" + m.Buses.First(b => b.Id == 1).Name + "'", R("ov.bb1", "bus").Resolved);
        Assert.Equal(AssetKey.Substation(101), R("ov.bb2", "bus").Target);
        Assert.Equal(AssetKey.Branch(m, Branch("L1-2 N-S Scotland")), R("ov.cbl", "line").Target);
        Assert.Equal(AssetKeyKind.Circuit, R("ov.cbl", "line").Target!.Kind);
        Assert.Equal(AssetKey.Branch(m, Branch("T1 GSP N Scotland")), R("ov.cbt", "transformer").Target);
        Assert.Equal(AssetKeyKind.Transformer, R("ov.cbt", "transformer").Target!.Kind);
        Assert.Equal(AssetKey.Unit(m, Gen("North Scotland Hydro"), isLink: false), R("ov.cbg", "generator").Target);
        Assert.Equal(AssetKey.Unit(m, Gen("Moyle"), isLink: true), R("ov.hvdc", "sendingConverter").Target);
        Assert.Equal(AssetKeyKind.Interconnector, R("ov.hvdc", "sendingConverter").Target!.Kind);
        Assert.Equal(AssetKey.Unit(m, Gen("Torness Nuclear"), isLink: false), R("ov.hvdc", "receivingConverter").Target);
        Assert.Equal(AssetIndex.DemandKey(northScotland), R("ov.cbd", "demand").Target);
        Assert.Equal(ov.Find("ov.bb1")!.Key, R("ov.cpl", "from").Target);
        Assert.Equal(ov.Find("ov.soil")!.Key, R("ov.earth", "soil").Target);
        Assert.False(R("ov.earth", "soil").IsModelAsset);
        Assert.True(R("ov.earth", "bus").IsModelAsset);
        Assert.Equal("Soil zone 'ov.soil'", R("ov.earth", "soil").Resolved);
        Assert.Equal(AssetKey.Substation(102), R("ov.t3w", "starBus").Target);
        Assert.Equal(AssetKey.Branch(m, Branch("L2-3 B6 boundary")), R("ov.route", "line").Target);
        // Every reference of the fixture resolves (the checker resolved them against the network file; the index against
        // the running model and its case agrees), and every model target is a node of the tree.
        AssetTree tree = nav.Tree;
        foreach (OverlayEntry e in ov.Entries)
            foreach (OverlayRefLink r in e.Refs)
            {
                Assert.True(r.Target is not null, $"{e.Id} role {r.Role} did not resolve: {r.Resolved}");
                Assert.NotNull(tree.NodeFor(r.Target));
            }
    }

    [Fact]
    public void Each_record_attaches_to_the_model_assets_it_points_at_and_a_substation_to_its_sections_buses()
    {
        using var fx = new UiOverlayFixture();
        (LiveLoadResult loaded, var frames) = Run(fx.ManifestPath);
        AssetIndex ix = Navigator(fx.ManifestPath, loaded.Overlay, frames).Index!;
        OverlayIndex ov = ix.Overlay!;
        GridModel m = ix.Model;
        AssetKey Line(string name) => AssetKey.Branch(m, Enumerable.Range(0, m.Branches.Count).Single(i => m.Branches[i].Name == name));
        AssetKey Gen(string name, bool link = false) => AssetKey.Unit(m, Enumerable.Range(0, m.Generators.Count).Single(i => m.Generators[i].Name == name), link);

        // Own references.
        Assert.Equal(new[] { new OverlayAttachment(AssetKey.Substation(1), "role 'bus' points here") }, ov.Find("ov.bb1")!.Attachments);
        Assert.Equal(new[] { Line("L1-2 N-S Scotland") }, ov.Find("ov.cbl")!.Attachments.Select(a => a.Asset));
        Assert.Equal(new[] { AssetKey.Substation(102), Line("T2 GSP S Scotland"), Line("T3 GSP N England"), Line("T4 GSP Yorkshire") },
            ov.Find("ov.t3w")!.Attachments.Select(a => a.Asset));
        Assert.Equal(new[] { Gen("Moyle", link: true), Gen("Torness Nuclear") }, ov.Find("ov.hvdc")!.Attachments.Select(a => a.Asset));
        // A record with no model reference of its own: the buses its descendants point at (the substation); none at all
        // for the catalogue items and the soil zone.
        Assert.Equal(new[]
        {
            new OverlayAttachment(AssetKey.Substation(1), "holds busbarSection 'ov.bb1', whose role 'bus' points here"),
            new OverlayAttachment(AssetKey.Substation(101), "holds busbarSection 'ov.bb2', whose role 'bus' points here"),
            new OverlayAttachment(AssetKey.Substation(102), "holds transformer3W 'ov.t3w', whose role 'starBus' points here"),
        }, ov.Find("ov.sub")!.Attachments);
        foreach (string id in new[] { "ov.zebra", "ov.l6", "ov.l6.a", "ov.soil", "ov.cpl", "ov.ds", "ov.es", "ov.span1", "ov.ew" })
            Assert.Empty(ov.Find(id)!.Attachments);

        // Per asset: every attached record in overlay order, and the roots the tree nests under it.
        Assert.Equal(new[] { "ov.bb1", "ov.sub", "ov.earth" }, ov.AttachedTo(AssetKey.Substation(1)).Select(e => e.Id));
        Assert.Equal(new[] { "ov.sub" }, ov.RootsUnder(AssetKey.Substation(1)).Select(e => e.Id));
        Assert.Equal(new[] { "ov.sub", "ov.bb2" }, ov.AttachedTo(AssetKey.Substation(101)).Select(e => e.Id));
        Assert.Equal(new[] { "ov.sub" }, ov.RootsUnder(AssetKey.Substation(101)).Select(e => e.Id));
        Assert.Equal(new[] { "ov.cbl" }, ov.RootsUnder(Line("L1-2 N-S Scotland")).Select(e => e.Id));
        Assert.Equal(new[] { "ov.route" }, ov.RootsUnder(Line("L2-3 B6 boundary")).Select(e => e.Id));
        Assert.Equal(new[] { "ov.cbd" }, ov.RootsUnder(AssetIndex.DemandKey(ix.Demands.Single(d => d.Name == "North Scotland"))).Select(e => e.Id));
        Assert.Empty(ov.AttachedTo(AssetKey.Substation(4)));
        Assert.Empty(ov.RootsUnder(AssetKey.Substation(4)));
    }

    [Fact]
    public void The_checker_problems_are_placed_on_their_records_and_shown_on_CHECK()
    {
        // A schema-valid overlay with record-level problems (a parameter out of range, an unresolved reference, an unknown
        // role): the checker's own results, placed per record by its file and pointer.
        string bad = UiOverlayFixture.OverlayA
            .Replace("\"resistanceOhm\": { \"v\": 0.5, \"unit\": \"ohm\" }", "\"resistanceOhm\": -1", StringComparison.Ordinal)
            .Replace("\"line\": { \"line\": \"L1-2 N-S Scotland\" }", "\"line\": { \"line\": \"No such line\" }", StringComparison.Ordinal);
        using var good = new UiOverlayFixture();
        using var fx = new UiOverlayFixture(overlayA: bad);
        IReadOnlyList<(string Name, JsonElement Root)> docs = fx.OverlayDocuments();
        var targets = AssetOverlayTargets.FromNetworks(new[] { V2.V2TestSupport.ReadRoot(fx.NetworkPath) });
        IReadOnlyList<SchemaError> problems = V2AssetChecker.Check(docs, BundledSchemas.V2Validate, OverlayIndex.Kinds, targets);
        Assert.Equal(2, problems.Count);
        CaseAssetOverlay overlay = CaseAssetOverlay.FromCheckedDocuments(docs);

        // The model and frames of the good case (same network), the bad overlay and its problems handed to the tab.
        (_, var frames) = Run(good.ManifestPath);
        AssetNavigatorModel nav = Navigator(good.ManifestPath, overlay, frames, problems);
        OverlayIndex ov = nav.Index!.Overlay!;
        Assert.Equal(new[] { problems.Single(p => p.Pointer.StartsWith("/records/10/", StringComparison.Ordinal)) }, ov.Find("ov.earth")!.Problems);
        Assert.Equal(new[] { problems.Single(p => p.Pointer.StartsWith("/records/4/", StringComparison.Ordinal)) }, ov.Find("ov.cbl")!.Problems);
        Assert.All(ov.Entries.Where(e => e.Id is not ("ov.earth" or "ov.cbl")), e => Assert.Empty(e.Problems));
        Assert.Empty(ov.UnplacedProblems);
        // The unresolved line: no target, and words that say so.
        OverlayRefLink line = ov.Find("ov.cbl")!.Refs.Single(r => r.Role == "line");
        Assert.Null(line.Target);
        Assert.Equal("not a line of the running model", line.Resolved);

        // CHECK on the record with problems (canonical node and an attachment node alike), hidden on one without.
        nav.Select(nav.Tree.NodeFor(ov.Find("ov.earth")!.Key)!);
        Assert.Equal(new[] { "OVERVIEW", "PROPERTIES", "CHECK" }, nav.SubTabs);
        OverlayCheck check = Assert.IsType<OverlayCheck>(nav.OverlayCheck);
        Assert.Equal("1 problem in " + UiOverlayFixture.FileA + " (the overlay checker, V2AssetChecker)", check.Heading);
        Assert.Equal(OverlayIndex.SolverNote, check.Note);
        Assert.Equal(ov.Find("ov.earth")!.Problems.Select(p => new OverlayCheckRow(p.Pointer, p.Message)), check.Rows);
        nav.SelectedSubTab = "CHECK";
        Assert.True(nav.IsCheckShown);
        AssetTreeNode attached = nav.Tree.PreOrder().First(n => n.IsAttachment && n.Key.Overlay!.RecordId == "ov.earth");
        nav.Select(attached);
        Assert.Same(check, nav.OverlayCheck);   // the same content: the navigator keeps the object
        Assert.Equal("CHECK", nav.SelectedSubTab);
        nav.Select(nav.Tree.NodeFor(ov.Find("ov.soil")!.Key)!);
        Assert.Equal(new[] { "OVERVIEW", "PROPERTIES" }, nav.SubTabs);
        Assert.Null(nav.OverlayCheck);
        Assert.Equal("OVERVIEW", nav.SelectedSubTab);
        // The OVERLAY class page counts the problems.
        nav.Select(nav.Roots.Single(r => r.Label == AssetTree.OverlayLabel));
        Assert.Contains(Assert.IsType<AssetOverviewPage>(nav.Current).Tiles, t => t.Label == "PROBLEMS" && t.Value == "2");
    }

    // ================================================================ the tree

    [Fact]
    public void The_tree_shows_the_overlay_by_kind_and_nested_under_the_model_assets_every_key_one_node()
    {
        using var fx = new UiOverlayFixture();
        (LiveLoadResult loaded, var frames) = Run(fx.ManifestPath);
        AssetNavigatorModel nav = Navigator(fx.ManifestPath, loaded.Overlay, frames);
        OverlayIndex ov = nav.Index!.Overlay!;
        AssetTree t = nav.Tree;

        // OVERLAY is the last class node: one group per kind (ordinal by name), its records (canonical nodes) in order.
        AssetTreeNode cls = nav.Roots[^1];
        Assert.Equal(AssetTree.OverlayLabel, cls.Label);
        Assert.Equal(AssetNodeRole.Class, cls.Role);
        Assert.Equal(OverlayIndex.FleetPage, cls.Page);
        Assert.Equal(UiOverlayFixture.RecordCount.ToString(Inv), cls.Detail);
        Assert.Equal(ov.ByKind.Select(g => (g.Title, g.Records.Count.ToString(Inv))), cls.Children.Select(k => (k.Label, k.Detail)));
        Assert.All(cls.Children, k => Assert.Equal(AssetNodeRole.Group, k.Role));
        foreach ((AssetTreeNode kindNode, OverlayKindGroup g) in cls.Children.Zip(ov.ByKind))
        {
            Assert.Equal(OverlayIndex.KindPage(g.Kind), kindNode.Page);
            Assert.Equal(g.Records.Select(e => (e.Key, e.Page, e.Id, e.KindTitle)), kindNode.Children.Select(n => (n.Key, n.Page, n.Label, n.Detail)));
            Assert.All(kindNode.Children, n => Assert.False(n.IsAttachment));
            Assert.All(kindNode.Children, n => Assert.Empty(n.Children));
        }
        foreach (OverlayEntry e in ov.Entries) Assert.Same(t.NodeFor(e.Key), t.NodeFor(e.Page));

        // Under bus 1: the substation with its whole subtree (attachment nodes, the section written before its parent
        // under its parent, not again on its own).
        AssetTreeNode bus1 = t.Substation(1)!;
        AssetTreeNode sub = Assert.Single(bus1.Children);
        Assert.Equal("bus:1/overlay:ov.sub", sub.Id);
        Assert.True(sub.IsAttachment);
        Assert.Equal(AssetKey.ForOverlay(new OverlayRef("ov.sub", "bus:1/overlay:ov.sub")), sub.Key);
        Assert.Equal(ov.Find("ov.sub")!.Page, sub.Page);
        Assert.Equal(new[] { "ov.bb1", "ov.bb2", "ov.cpl", "ov.cbl", "ov.cbt", "ov.cbg", "ov.cbd", "ov.ds", "ov.es", "ov.earth", "ov.t3w" },
            sub.Children.Select(c => c.Label));
        Assert.Equal("bus:1/overlay:ov.sub/overlay:ov.bb1", sub.Children[0].Id);
        Assert.Equal(new[] { "ov.sub" }, t.Substation(101)!.Children.Select(c => c.Label));
        Assert.Equal(new[] { "ov.sub" }, t.Substation(102)!.Children.Select(c => c.Label));
        Assert.Empty(t.Substation(4)!.Children);
        GridModel m = nav.Index.Model;
        AssetTreeNode route = Assert.Single(t.NodeFor(AssetKey.Branch(m, Enumerable.Range(0, m.Branches.Count).Single(i => m.Branches[i].Name == "L2-3 B6 boundary")))!.Children);
        Assert.Equal("ov.route", route.Label);
        Assert.Equal(new[] { "ov.span1", "ov.ew" }, route.Children.Select(c => c.Label));
        AssetTreeNode moyle = t.NodeFor(ov.Find("ov.hvdc")!.Refs[0].Target)!;
        Assert.StartsWith("ic:", moyle.Id, StringComparison.Ordinal);
        Assert.Equal(new[] { "ov.hvdc" }, moyle.Children.Select(c => c.Label));
        AssetTreeNode demand = t.NodeFor(ov.Find("ov.cbd")!.Refs.Single(r => r.Role == "demand").Target)!;
        Assert.Equal(new[] { "ov.cbd" }, demand.Children.Select(c => c.Label));

        // Every node has a key no other node has, and NodeFor gives that node back; attachment keys name their record.
        List<AssetTreeNode> all = All(t);
        Assert.Equal(all.Count, all.Select(n => n.Key).Distinct().Count());
        Assert.Equal(all.Count, all.Select(n => n.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(all, n => Assert.Same(n, t.NodeFor(n.Key)));
        foreach (AssetTreeNode n in all.Where(n => n.IsAttachment))
        {
            Assert.Equal(n.Id, n.Key.Overlay!.At);
            Assert.Equal(ov.Find(n.Key.Overlay.RecordId)!.Page, n.Page);
            Assert.Equal(AssetNodeRole.Unit, n.Role);
        }
        Assert.Equal("OverlayRecord ov.sub @ bus:1/overlay:ov.sub", sub.Key.Display);
        Assert.Equal("OverlayRecord ov.sub", ov.Find("ov.sub")!.Key.Display);

        // Search lists a record once (its canonical node), not again for every asset it attaches to.
        nav.SearchText = "ov.sub";
        Assert.Equal(new[] { t.NodeFor(ov.Find("ov.sub")!.Key) }, nav.SearchHits);
        nav.SearchText = "";
    }

    [Fact]
    public void Without_an_overlay_the_tab_has_no_overlay_node_and_no_overlay_sub_tab()
    {
        using var fx = new UiOverlayFixture(withOverlay: false);
        (LiveLoadResult loaded, var frames) = Run(fx.ManifestPath);
        Assert.Null(loaded.Overlay);
        AssetNavigatorModel nav = Navigator(fx.ManifestPath, null, frames);
        Assert.Null(nav.Index!.Overlay);
        Assert.DoesNotContain(nav.Roots, r => r.Label == AssetTree.OverlayLabel);
        Assert.DoesNotContain(All(nav.Tree), n => n.Key.Kind == AssetKeyKind.OverlayRecord);
        nav.Select(nav.Tree.Substation(1)!);
        Assert.DoesNotContain("OVERLAY", nav.SubTabs);
        Assert.Null(nav.OverlayList);
    }

    // ================================================================ the navigator: sub-tabs, pages, links

    [Fact]
    public void A_model_asset_lists_its_overlay_records_on_OVERLAY_and_each_row_selects_its_record()
    {
        using var fx = new UiOverlayFixture();
        (LiveLoadResult loaded, var frames) = Run(fx.ManifestPath);
        AssetNavigatorModel nav = Navigator(fx.ManifestPath, loaded.Overlay, frames);
        OverlayIndex ov = nav.Index!.Overlay!;

        nav.Select(nav.Tree.Substation(1)!);
        Assert.Equal(new[] { "OVERVIEW", "PROPERTIES", "CONNECTIONS", "OVERLAY" }, nav.SubTabs);
        AssetOverlayList list = Assert.IsType<AssetOverlayList>(nav.OverlayList);
        Assert.Equal("3 overlay records attached to Substation bus 1", list.Heading);
        Assert.Equal(OverlayIndex.SolverNote, list.Note);
        Assert.Equal(new[]
        {
            new ConnectionRow(AssetOverlayList.Section, "role 'bus' points here", "ov.bb1", "Busbar section", ov.Find("ov.bb1")!.Key),
            new ConnectionRow(AssetOverlayList.Section, "holds busbarSection 'ov.bb1', whose role 'bus' points here", "ov.sub", "Substation", ov.Find("ov.sub")!.Key),
            new ConnectionRow(AssetOverlayList.Section, "role 'bus' points here", "ov.earth", "Earth path", ov.Find("ov.earth")!.Key),
        }, list.Rows);
        nav.SelectedSubTab = "OVERLAY";
        Assert.True(nav.IsOverlayShown);
        Assert.False(nav.IsOverviewShown);
        foreach (ConnectionRow row in list.Rows.ToList())
        {
            nav.Select(nav.Tree.Substation(1)!);
            Assert.True(nav.OpenConnection(row));
            Assert.Equal(row.Target, nav.Selected!.Key);
            Assert.False(nav.Selected.IsAttachment);
            AssetOverviewPage page = Assert.IsType<AssetOverviewPage>(nav.Current);
            Assert.Equal(row.Label, page.Title);
            Assert.Equal("OVERVIEW", nav.SelectedSubTab);   // a record has no OVERLAY sub-tab: back to its overview
        }

        // An asset no record attaches to has no OVERLAY sub-tab.
        nav.Select(nav.Tree.Substation(4)!);
        Assert.DoesNotContain("OVERLAY", nav.SubTabs);
        Assert.Null(nav.OverlayList);
    }

    [Fact]
    public void An_overlay_record_shows_its_overview_and_its_properties_as_written_and_its_links_select_the_right_node()
    {
        using var fx = new UiOverlayFixture();
        (LiveLoadResult loaded, var frames) = Run(fx.ManifestPath);
        AssetNavigatorModel nav = Navigator(fx.ManifestPath, loaded.Overlay, frames);
        OverlayIndex ov = nav.Index!.Overlay!;
        GridModel m = nav.Index.Model;

        // OVERVIEW of the busbar section: kind, where written, parent, params with units and provenance, links.
        nav.Select(nav.Tree.NodeFor(ov.Find("ov.bb1")!.Key)!);
        Assert.Equal(new[] { "OVERVIEW", "PROPERTIES" }, nav.SubTabs);
        AssetOverviewPage bb1 = Assert.IsType<AssetOverviewPage>(nav.Current);
        Assert.Equal("OVERLAY RECORD", bb1.Eyebrow);
        Assert.Equal("ov.bb1", bb1.Title);
        Assert.Equal("Busbar section  -  carried, not used by the solver", bb1.Sub);
        Assert.Equal(OverlayIndex.SolverNote, bb1.Note);
        Assert.Equal(new[]
        {
            new OverviewFact("Kind", "busbarSection  (Busbar section)"),
            new OverviewFact("Written in", UiOverlayFixture.FileA + "  /records/0"),
            new OverviewFact("Parent", "substation 'ov.sub'"),
            new OverviewFact("Children", "0"),
            new OverviewFact("Provenance", "none given"),
            new OverviewFact("Note", "none"),
            new OverviewFact("Parameter label", "Main 400"),
            new OverviewFact("Parameter ratedCurrentA", "4000 A  (provenance Synthetic: test fixture)"),
        }, bb1.Facts);
        Assert.Equal(new[]
        {
            new OverviewRow("ov.sub", "parent  Substation", "substation", ov.Find("ov.sub")!.Key),
            new OverviewRow("bus 1", "reference 'bus':  bus 1 '" + m.Buses.First(b => b.Id == 1).Name + "'", "model", AssetKey.Substation(1)),
        }, bb1.Rows);
        Assert.False(bb1.HasDiagram);

        // PROPERTIES: the record as written, member by member, read-only, units and help from the kind spec.
        nav.SelectedSubTab = "PROPERTIES";
        Assert.Null(nav.Properties);   // no case record: the overlay record's own content instead
        OverlayRecordProperties p = Assert.IsType<OverlayRecordProperties>(nav.OverlayProperties);
        Assert.Equal("ov.bb1  (" + UiOverlayFixture.FileA + " /records/0)", p.Heading);
        Assert.Equal("busbarSection: Busbar section", p.Kind);
        Assert.Equal(OverlayIndex.SolverNote, p.Note);
        Assert.Equal(new[] { "id", "kind", "parent", "refs/bus", "params/label", "params/ratedCurrentA", "prov", "note" }, p.Rows.Select(r => r.Field));
        Assert.Equal(new[] { "\"ov.bb1\"", "\"busbarSection\"", "\"ov.sub\"", "{ \"bus\": 1 }", "\"Main 400\"",
                             "{ \"v\": 4000, \"unit\": \"A\", \"prov\": { \"tag\": \"Synthetic\", \"note\": \"test fixture\" } }", "", "" },
            p.Rows.Select(r => r.Literal));
        Assert.Equal(new[] { true, true, true, true, true, true, false, false }, p.Rows.Select(r => r.Present));
        PropertyRow rated = p.Rows.Single(r => r.Field == "params/ratedCurrentA");
        Assert.Equal(("A", "Number", false, "as written"), (rated.Unit, rated.Kind, rated.Required, rated.State));
        Assert.Equal("Busbar continuous current rating, amperes.", rated.Help);
        Assert.Equal("absent: none", p.Rows.Single(r => r.Field == "prov").State);
        Assert.All(p.Rows, r => Assert.False(r.HasSolved));   // the solver has no value for an overlay record

        // The parent link selects the substation's canonical node; its bus link the bus; back returns.
        nav.SelectedSubTab = "OVERVIEW";
        Assert.True(nav.OpenKey(bb1.Rows[0].Target!));
        Assert.Same(nav.Tree.NodeFor(ov.Find("ov.sub")!.Key), nav.Selected);
        AssetOverviewPage sub = Assert.IsType<AssetOverviewPage>(nav.Current);
        Assert.Equal("ov.sub", sub.Title);
        Assert.True(sub.HasDiagram);
        Assert.Contains(new OverviewFact("Note", "a substation note"), sub.Facts);
        Assert.Contains(new OverviewFact("Provenance", "Synthetic: test fixture"), sub.Facts);
        Assert.Contains(new OverviewFact("Parameter latitudeDeg", "57.5 deg"), sub.Facts);
        Assert.Equal(11, sub.Rows.Count(r => r.Detail.StartsWith("child  ", StringComparison.Ordinal)));
        Assert.Equal(new[] { AssetKey.Substation(1), AssetKey.Substation(101), AssetKey.Substation(102) },
            sub.Rows.Where(r => r.Detail.StartsWith("attached: ", StringComparison.Ordinal)).Select(r => r.Target));
        nav.GoBack();
        Assert.Same(bb1, nav.Current);
        Assert.True(nav.OpenKey(bb1.Rows[1].Target!));
        Assert.Same(nav.Tree.Substation(1), nav.Selected);
        Assert.IsType<SubstationDetailModel<PlantRowModel, ConnectionRowModel>>(nav.Current);

        // A bay device's line reference opens the circuit page.
        nav.Select(nav.Tree.NodeFor(ov.Find("ov.cbl")!.Key)!);
        OverviewRow lineRow = Assert.IsType<AssetOverviewPage>(nav.Current).Rows.Single(r => r.Name == "line 'L1-2 N-S Scotland'");
        Assert.True(nav.OpenKey(lineRow.Target!));
        Assert.Equal(AssetKeyKind.Circuit, nav.Selected!.Key.Kind);
        Assert.IsType<CircuitDetailModel<CircuitStatusRowModel>>(nav.Current);

        // An attachment node shows the same record's page and content, under the asset it was reached from.
        AssetTreeNode under = nav.Tree.Substation(1)!.Children[0].Children[0];
        Assert.Equal("ov.bb1", under.Label);
        nav.Select(under);
        Assert.Same(under, nav.Selected);
        AssetOverviewPage again = Assert.IsType<AssetOverviewPage>(nav.Current);
        Assert.Equal((bb1.Eyebrow, bb1.Title, bb1.Sub, bb1.Note), (again.Eyebrow, again.Title, again.Sub, again.Note));
        Assert.Equal(bb1.Facts, again.Facts);
        Assert.Equal(bb1.Rows, again.Rows);
        Assert.Equal("SUBSTATIONS > 400 kV > " + nav.Tree.Substation(1)!.Label + " > ov.sub > ov.bb1", nav.Crumb);
        Assert.Equal(new[] { "OVERVIEW", "PROPERTIES" }, nav.SubTabs);
    }

    [Fact]
    public void Every_overlay_page_says_carried_not_used_by_the_solver()
    {
        using var fx = new UiOverlayFixture();
        (LiveLoadResult loaded, var frames) = Run(fx.ManifestPath);
        AssetNavigatorModel nav = Navigator(fx.ManifestPath, loaded.Overlay, frames);
        List<AssetTreeNode> overlayNodes = All(nav.Tree).Where(IsOverlayNode).ToList();
        Assert.True(overlayNodes.Count > UiOverlayFixture.RecordCount + 13, $"only {overlayNodes.Count} overlay nodes");
        foreach (AssetTreeNode n in overlayNodes)
        {
            nav.Select(n);
            AssetOverviewPage page = Assert.IsType<AssetOverviewPage>(nav.Current);
            Assert.Contains(OverlayIndex.NotUsedBySolver, page.Sub, StringComparison.Ordinal);
            Assert.Equal(OverlayIndex.SolverNote, page.Note);
            Assert.StartsWith("Carried, not used by the solver", page.Note, StringComparison.Ordinal);
            if (n.Key.Kind == AssetKeyKind.OverlayRecord)
                Assert.Equal(OverlayIndex.SolverNote, nav.OverlayProperties!.Note);
        }
        foreach (AssetKey asset in nav.Index!.Overlay!.AttachedAssets)
        {
            Assert.True(nav.OpenKey(asset));
            Assert.Equal(OverlayIndex.SolverNote, nav.OverlayList!.Note);
            Assert.Contains("OVERLAY", nav.SubTabs);
        }
        // The class and kind pages: tiles, rows and facts exactly.
        nav.Select(nav.Roots.Single(r => r.Label == AssetTree.OverlayLabel));
        AssetOverviewPage fleet = Assert.IsType<AssetOverviewPage>(nav.Current);
        Assert.Equal(("ASSET OVERLAY", "OVERLAY", "the asset overlay of the case  -  carried, not used by the solver"), (fleet.Eyebrow, fleet.Title, fleet.Sub));
        Assert.Equal(new[] { ("RECORDS", "20"), ("KINDS", "13"), ("FILES", "2"), ("ATTACHED", "11") }, fleet.Tiles.Select(t => (t.Label, t.Value)));
        Assert.Equal(new[] { new OverviewFact("File 1", UiOverlayFixture.FileA), new OverviewFact("File 2", UiOverlayFixture.FileB) }, fleet.Facts);
        Assert.Equal(nav.Index.Overlay.ByKind.SelectMany(g => g.Records).Select(e => (e.Id, e.Key)), fleet.Rows.Select(r => (r.Name, r.Target!)));
        nav.Select(nav.Roots[^1].Children.Single(k => k.Label == "Switching device"));
        AssetOverviewPage kind = Assert.IsType<AssetOverviewPage>(nav.Current);
        Assert.Equal(("OVERLAY KIND", "Switching device", "switchingDevice  -  carried, not used by the solver"), (kind.Eyebrow, kind.Title, kind.Sub));
        Assert.Equal(7, kind.Rows.Count);
        Assert.Equal("under substation 'ov.sub'  in " + UiOverlayFixture.FileA, kind.Rows[0].Detail);
    }

    [Fact]
    public void The_substation_record_draws_its_sections_and_switchgear_and_a_click_opens_what_a_symbol_stands_for()
    {
        using var fx = new UiOverlayFixture();
        (LiveLoadResult loaded, var frames) = Run(fx.ManifestPath);
        AssetNavigatorModel nav = Navigator(fx.ManifestPath, loaded.Overlay, frames);
        OverlayIndex ov = nav.Index!.Overlay!;
        GridModel m = nav.Index.Model;
        nav.Select(nav.Tree.NodeFor(ov.Find("ov.sub")!.Key)!);
        AssetOverviewPage page = Assert.IsType<AssetOverviewPage>(nav.Current);
        DiagramModel d = Assert.IsType<DiagramModel>(AssetDiagrams.For(page));

        // Two sections, a coupler between them, the bays with what they switch, the normally open devices drawn open.
        Assert.Equal(DiagramSymbolKind.BusSection, d.Symbol("ov.bb1").Kind);
        Assert.Equal(DiagramSymbolKind.BusSection, d.Symbol("ov.bb2").Kind);
        Assert.Equal("ov.bb1  (bus 1)", d.Symbol("ov.bb1").Label);
        Assert.Equal(DiagramSymbolKind.CircuitBreaker, d.Symbol("ov.cpl").Kind);
        // Test retargeted 2026-10-20 (Phase 5c): the coupler between the two neighbouring sections is now drawn in line in
        // the gap between them (the bus section breaker position), so its caption "ov.cpl  coupler" is a free label
        // centred exactly under the symbol (its own label would sit over the next section). Was: the symbol's label.
        Assert.Equal("", d.Symbol("ov.cpl").Label);
        Assert.Equal(DiagramRotation.ThreeQuarter, d.Symbol("ov.cpl").Rotation);
        DiagramLabel caption = Assert.Single(d.Labels, l => l.Text == "ov.cpl  coupler");
        Assert.Equal((d.Symbol("ov.cpl").Position.X, OverlaySubstationDiagram.BarY + OverlaySubstationDiagram.InlineCaptionDrop, DiagramLabelAlign.Centre),
            (caption.Anchor.X, caption.Anchor.Y, caption.Align));
        Assert.Equal(DiagramSymbolKind.LineEnd, d.Symbol("ov.cbl/END").Kind);
        Assert.Equal(DiagramSymbolKind.Transformer2W, d.Symbol("ov.cbt/END").Kind);
        Assert.Equal(DiagramSymbolKind.Generator, d.Symbol("ov.cbg/END").Kind);
        Assert.Equal(DiagramSymbolKind.Load, d.Symbol("ov.cbd/END").Kind);
        Assert.Equal(DiagramSymbolKind.Disconnector, d.Symbol("ov.ds").Kind);
        Assert.Equal(DiagramSwitchState.Open, d.Symbol("ov.ds").State.Switch);
        Assert.Equal(DiagramSymbolKind.EarthSwitch, d.Symbol("ov.es").Kind);
        Assert.Equal(DiagramSwitchState.Open, d.Symbol("ov.es").State.Switch);
        Assert.Equal(DiagramSwitchState.Closed, d.Symbol("ov.cbl").State.Switch);
        // Test retargeted 2026-10-20 (Phase 5c): the diagram now takes, read-only, the solved voltage of the bus each
        // section collapses to (Run mode; no switching is simulated). Was: nothing drawn energised. Now, exactly: the page
        // keeps the last frame's voltages to the bit; every symbol is energised exactly when the bus of its section (a
        // device's or far end's: its bay's section) has a solved voltage above zero; both buses are live in this solved
        // case, so every conductor is; and with no frame (the diagram built without voltages) nothing is, as before.
        IReadOnlyDictionary<long, double> vm = Assert.IsAssignableFrom<IReadOnlyDictionary<long, double>>(page.SolvedVoltages);
        PowerFlowResult last = frames[^1].Frame.Result;
        Assert.Equal(last.BusIds.Count, vm.Count);
        for (int i = 0; i < last.BusIds.Count; i++) Assert.Equal(Bits(last.Vm[i]), Bits(vm[last.BusIds[i]]));
        bool Live(long bus) => vm.TryGetValue(bus, out double v) && double.IsFinite(v) && v > 0;
        SubstationTopology topo = ov.Find("ov.sub")!.Topology!;
        TopologySection SectionOf(string symbolId)
        {
            string id = OverlaySubstationDiagram.RecordIdOf(symbolId);
            return topo.Section(id) ?? topo.Section(topo.Device(id)!.From!)!;
        }
        Assert.True(Live(1));
        Assert.True(Live(101));
        Assert.All(d.Symbols, s => Assert.Equal(Live(SectionOf(s.Id).Bus!.Value), s.State.Energised));
        Assert.All(d.Connections, c => Assert.True(c.State.Energised));
        DiagramModel cold = OverlaySubstationDiagram.Build(ov.Find("ov.sub"))!;
        Assert.All(cold.Symbols, s => Assert.False(s.State.Energised));   // as written, no frame: nothing shown live
        Assert.All(cold.Connections, c => Assert.False(c.State.Energised));
        Assert.Equal(12, d.Connections.Count);   // 6 bay devices to their section, 4 ends, the coupler's two sides
        Assert.Equal(13, d.Symbols.Count);   // 2 sections, 7 devices (the coupler among them), 4 bay ends
        Assert.Contains(d.Labels, l => l.Text.Contains(OverlayIndex.NotUsedBySolver, StringComparison.Ordinal));
        Assert.Null(AssetDiagrams.For(new AssetOverviewPage(ov.Find("ov.bb1")!.Page, nav.Index)));

        // A click: a section opens its record; a bay end opens the asset the device switches.
        Assert.True(nav.OpenFromDiagram("ov.bb2"));
        Assert.Same(nav.Tree.NodeFor(ov.Find("ov.bb2")!.Key), nav.Selected);
        nav.Select(nav.Tree.NodeFor(ov.Find("ov.sub")!.Key)!);
        Assert.True(nav.OpenFromDiagram("ov.cbt/END"));
        Assert.Equal(AssetKey.Branch(m, Enumerable.Range(0, m.Branches.Count).Single(i => m.Branches[i].Name == "T1 GSP N Scotland")), nav.Selected!.Key);
        nav.Select(nav.Tree.NodeFor(ov.Find("ov.sub")!.Key)!);
        Assert.False(nav.OpenFromDiagram("nothing"));
        Assert.False(nav.OpenFromDiagram("ov.zebra"));   // a record the diagram does not draw
    }

    // ================================================================ the overlay changes no displayed model value

    [Fact]
    public void Every_A2_and_B2_page_and_sub_tab_is_identical_with_and_without_the_overlay()
    {
        using var plain = new UiOverlayFixture(withOverlay: false);
        using var overlaid = new UiOverlayFixture();
        (_, var fa) = Run(plain.ManifestPath);
        (LiveLoadResult b, var fb) = Run(overlaid.ManifestPath);
        AssetNavigatorModel without = Navigator(plain.ManifestPath, null, fa);
        AssetNavigatorModel with = Navigator(overlaid.ManifestPath, b.Overlay, fb);

        // The tree: the model's nodes are the same nodes, in the same order, with the same texts and pages; the overlay
        // only adds nodes (the OVERLAY class and the attachment nodes under the assets).
        List<AssetTreeNode> a = All(without.Tree), c = All(with.Tree).Where(n => !IsOverlayNode(n)).ToList();
        Assert.Equal(a.Select(n => (n.Id, n.Label, n.Detail, n.Kind, n.Page, n.Key, n.Role, n.MapTarget)),
                     c.Select(n => (n.Id, n.Label, n.Detail, n.Kind, n.Page, n.Key, n.Role, n.MapTarget)));
        Assert.Equal(without.Roots.Select(r => r.Label).Append(AssetTree.OverlayLabel), with.Roots.Select(r => r.Label));

        // Every model node: the same page values, PROPERTIES, CONNECTIONS and sub-tabs (OVERLAY added where records attach).
        int pages = 0;
        foreach (AssetTreeNode n in a)
        {
            AssetTreeNode twin = with.Tree.ById(n.Id)!;
            without.Select(n);
            with.Select(twin);
            AssertSameValues(without.Current, with.Current, n.Id, 0);
            Assert.Equal(without.Current.GetType(), with.Current.GetType());
            Assert.Equal(without.SubTabs, with.SubTabs.Where(s => s != "OVERLAY"));
            Assert.Equal(with.Index!.Overlay!.AttachedTo(twin.Key).Count > 0, with.SubTabs.Contains("OVERLAY"));
            Assert.Equal(without.Crumb, with.Crumb);
            Assert.Equal(without.MapTarget, with.MapTarget);
            Assert.Equal(without.Connections.Model, with.Connections.Model);
            Assert.Equal(without.Connections.WhereUsed.Select(r => (r.Relation, r.Label, r.Target)), with.Connections.WhereUsed.Select(r => (r.Relation, r.Label, r.Target)));
            if (without.SubTabs.Contains("PROPERTIES"))
            {
                without.SelectedSubTab = "PROPERTIES";
                with.SelectedSubTab = "PROPERTIES";
                Assert.Equal(without.Properties!.Rows, with.Properties!.Rows);
                Assert.Equal(without.Properties.Heading, with.Properties.Heading);
                Assert.Null(with.OverlayProperties);
                without.SelectedSubTab = "OVERVIEW";
                with.SelectedSubTab = "OVERVIEW";
            }
            pages++;
        }
        Assert.True(pages > 100, $"only {pages} model nodes compared");
    }

    // Every public value of two pages (or rows) equal: strings ordinal, doubles by their bits, lists item by item. The
    // indexes, models and explorer behind a page are the inputs, not its values, so they are not walked.
    private static void AssertSameValues(object? expected, object? actual, string at, int depth)
    {
        Assert.True(depth < 7, $"{at}: too deep");
        if (expected is null || actual is null) { Assert.True(expected is null && actual is null, $"{at}: one side null"); return; }
        Type t = expected.GetType();
        if (t == typeof(double))
        {
            Assert.True(Bits((double)expected) == Bits((double)actual), $"{at}: {expected} vs {actual}");
            return;
        }
        if (t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(UiColour) || t == typeof(DateTime))
        {
            Assert.True(Equals(expected, actual), $"{at}: '{expected}' vs '{actual}'");
            return;
        }
        if (expected is Delegate || expected is AssetIndex || expected is GridModel || expected is AssetExplorerModel
            || expected is OverlayIndex || expected is SimFrame || expected is PowerFlowResult) return;
        if (expected is IEnumerable eList && actual is IEnumerable aList)
        {
            object?[] e = eList.Cast<object?>().ToArray(), x = aList.Cast<object?>().ToArray();
            Assert.True(e.Length == x.Length, $"{at}: {x.Length} items, expected {e.Length}");
            for (int i = 0; i < e.Length; i++) AssertSameValues(e[i], x[i], $"{at}[{i}]", depth + 1);
            return;
        }
        foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0 || typeof(Delegate).IsAssignableFrom(p.PropertyType)) continue;
            AssertSameValues(p.GetValue(expected), p.GetValue(actual), $"{at}.{p.Name}", depth + 1);
        }
    }
}
