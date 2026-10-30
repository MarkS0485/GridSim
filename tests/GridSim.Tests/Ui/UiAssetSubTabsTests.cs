using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using GridSim.Core.Analysis;
using GridSim.Core.Assets;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Tests.Editor;
using GridSim.Ui.Assets.Index;
using GridSim.Ui.Assets.Keys;
using GridSim.Ui.Assets.Navigation;
using GridSim.Ui.Assets.Overviews;
using GridSim.Ui.Assets.Pages;
using GridSim.Ui.Dashboard;
using GridSim.Ui.Hosting;
using GridSim.Ui.Models;
using Xunit;

namespace GridSim.Tests.Ui;

/// <summary>
/// UI programme ASSETS B2 (Run mode, read-only): the explorer tree over an AssetIndex with the new class nodes (plan S8),
/// the PROPERTIES sub-tab (the case record's literals and the schema's field specs, exactly, with the solver's value
/// beside a field under its caption), the CONNECTIONS sub-tab (the model adjacency and the case editor's ReferenceIndex,
/// exactly), the navigator's sub-tab rules (shown only with content; nothing new until the shell declares the source, so
/// the A3 behaviour is unchanged), and the new OVERVIEW pages' content computed here independently. New pages with no WPF
/// counterpart: exact tests instead of a parity oracle.
/// </summary>
public sealed class UiAssetSubTabsTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static long Bits(double v) => BitConverter.DoubleToInt64Bits(v);

    private static LicenceGate Gate() => CaseSessionTests.Unlimited();

    // ---------------------------------------------------------------- models and frames

    private static GridModel Load(string relative) => JsonGridLoader.LoadDirectory(EditorTestSupport.CaseDir(relative));

    private static AssetIndex IndexOf(string relative, GridModel? m = null) =>
        AssetIndex.Build(m ?? Load(relative), AssetCaseSource.V1(EditorTestSupport.CaseDir(relative)), Gate());

    private static ModelSource SourceOf(string name)
    {
        if (name == "case9")
        {
            string file = Path.Combine(AppContext.BaseDirectory, "data", "matpower", "lib", "case9.m");
            Assert.True(File.Exists(file), $"bundled case not found: {file}");
            return new ModelSource("MATPOWER  case9", Path.GetDirectoryName(file)!, file);
        }
        string dir = Path.Combine(AppContext.BaseDirectory, "data", name);
        Assert.True(File.Exists(Path.Combine(dir, "system.json")), $"bundled case not found: {dir}");
        return new ModelSource(name, dir);
    }

    private static readonly Dictionary<string, (SimFrame Frame, string Clock)[]> FrameCache = new(StringComparer.Ordinal);

    // Two solved frames of a bundled case, from the live host as the app runs it (cached: each case is solved once).
    private static (SimFrame Frame, string Clock)[] Frames(string name)
    {
        lock (FrameCache)
        {
            if (FrameCache.TryGetValue(name, out var cached)) return cached;
            var host = new SimHost();
            var dash = new DashboardModel();
            LiveLoadResult loaded = host.LoadLive(SourceOf(name), SolverKind.Auto, new LicenceGate(Entitlement.FreeDefault));
            Assert.True(loaded.Loaded, loaded.Refusal);
            var frames = new (SimFrame, string)[2];
            for (int i = 0; i < frames.Length; i++)
            {
                HostFrame hf = Assert.IsType<HostFrame>(host.ProduceNext());
                dash.Update(hf.Frame!, hf.Boundaries, hf.SolveMs);
                frames[i] = (hf.Frame!, dash.Clock);
            }
            FrameCache[name] = frames;
            return frames;
        }
    }

    // The navigator of the app, declared a source and shown, fed the frames.
    private static AssetNavigatorModel Navigator(string name, AssetCaseSource? source = null)
    {
        var nav = new AssetNavigatorModel();
        nav.SetSource(source ?? AssetCaseSource.From(SourceOf(name)), Gate());
        foreach (var (f, c) in Frames(name)) nav.Update(f, c);
        return nav;
    }

    // A temporary copy of a case with some units out of service (never the slack unit).
    private static string OutOfServiceCopy(string relative)
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-assets-b2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var opts = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        foreach (string f in Directory.EnumerateFiles(EditorTestSupport.CaseDir(relative), "*.json"))
        {
            JsonNode node = JsonNode.Parse(File.ReadAllText(f), documentOptions: opts)!;
            if (Path.GetFileName(f) == "generation.json")
                for (int k = 0; k < node.AsArray().Count; k++)
                {
                    JsonObject o = node[k]!.AsObject();
                    bool slack = o.TryGetPropertyValue("role", out JsonNode? r) && string.Equals((string?)r, "slack", StringComparison.OrdinalIgnoreCase);
                    if (!slack && k % 4 == 2) o["inService"] = false;
                }
            File.WriteAllText(Path.Combine(dir, Path.GetFileName(f)), node.ToJsonString());
        }
        return dir;
    }

    private static List<AssetTreeNode> Flatten(AssetTreeNode n)
    {
        var all = new List<AssetTreeNode> { n };
        foreach (AssetTreeNode c in n.Children) all.AddRange(Flatten(c));
        return all;
    }

    // ================================================================ the tree

    [Theory]
    [InlineData("uk", new[] { "DEMANDS", "BOUNDARIES" })]
    [InlineData("gb-synthetic", new[] { "DEMANDS", "PROTECTIONS", "ASSET CLASSES" })]
    [InlineData("gb-full", new[] { "DEMANDS", "BOUNDARIES", "ASSET CLASSES" })]
    public void The_index_tree_is_the_A3_tree_then_the_new_class_nodes_that_have_members(string relative, string[] classes)
    {
        GridModel m = Load(relative);
        AssetIndex ix = IndexOf(relative, m);
        Assert.True(ix.HasCase, ix.CaseNote);
        AssetTree a3 = AssetTree.Build(m);
        AssetTree t = ix.BuildTree();

        // The A3 tree, untouched: the same nodes (id, label, detail, page, key) in the same order, first.
        List<AssetTreeNode> old = a3.PreOrder().ToList();
        List<AssetTreeNode> now = t.PreOrder().Take(old.Count).ToList();
        Assert.Equal(old.Select(n => (n.Id, n.Label, n.Detail, n.Page, n.Key)), now.Select(n => (n.Id, n.Label, n.Detail, n.Page, n.Key)));
        Assert.Equal(a3.Roots.Select(r => r.Id), t.Roots.Take(8).Select(r => r.Id));

        // Then the new classes, in plan order, each only with members.
        Assert.Equal(classes, t.Roots.Skip(8).Select(r => r.Label));
        Assert.All(t.Roots.Skip(8), r => { Assert.Equal(AssetNodeRole.Class, r.Role); Assert.NotEmpty(r.Children); });
        AssetTreeNode? Root(string label) => t.Roots.FirstOrDefault(r => r.Label == label);

        // DEMANDS: every demand record once, in case order, keyed by its record.
        AssetTreeNode demands = Root(AssetTree.DemandsLabel)!;
        Assert.Equal(ix.Demands.Select(AssetIndex.DemandKey), demands.Children.Select(n => n.Key));
        Assert.Equal(ix.Demands.Count.ToString(Inv), demands.Detail);
        Assert.Equal(ix.Demands.Select(d => d.Name ?? "(unnamed)"), demands.Children.Select(n => n.Label));
        Assert.All(demands.Children, n => Assert.Equal(AssetPageKind.Demand, n.Page.Kind));
        // BOUNDARIES / PROTECTIONS: the model's, in order.
        if (Root(AssetTree.BoundariesLabel) is { } b)
            Assert.Equal(m.Boundaries.Select(x => x.Name), b.Children.Select(n => n.Label));
        else Assert.Empty(m.Boundaries);
        if (Root(AssetTree.ProtectionsLabel) is { } p)
            Assert.Equal(m.Protections!.Instances.Select(x => x.Name), p.Children.Select(n => n.Label));
        else Assert.Null(m.Protections);
        // ASSET CLASSES: the classes in library order, then the bindings group.
        if (Root(AssetTree.AssetClassesLabel) is { } c)
        {
            var units = c.Children.Where(n => n.Role == AssetNodeRole.Unit).ToList();
            Assert.Equal(m.AssetDynamics!.Library.Classes.Select(x => x.Name), units.Select(n => n.Label));
            AssetTreeNode g = Assert.Single(c.Children, n => n.Role == AssetNodeRole.Group);
            Assert.Same(c.Children[^1], g);
            Assert.Equal(AssetTree.BindingsLabel, g.Label);
            Assert.Equal(m.AssetDynamics.Bindings.Rules.Select(r => r.MatchType), g.Children.Select(n => n.Label));
        }
        Assert.Null(Root(AssetTree.OutOfServiceLabel));   // no bundled case has an out-of-service unit

        // Every node exactly once by key, found again by its key; every unit's map place is a bus of the model.
        List<AssetTreeNode> all = t.PreOrder().ToList();
        Assert.Equal(all.Count, all.Select(n => n.Key).Distinct().Count());
        Assert.All(all, n => Assert.Same(n, t.NodeFor(n.Key)));
        Assert.All(all, n => Assert.Same(n, t.ById(n.Id)));
    }

    [Fact]
    public void Out_of_service_units_get_their_class_node_in_fleet_order()
    {
        string dir = OutOfServiceCopy("uk");
        try
        {
            GridModel m = JsonGridLoader.LoadDirectory(dir);
            AssetIndex ix = AssetIndex.Build(m, AssetCaseSource.V1(dir), Gate());
            Assert.True(ix.HasCase, ix.CaseNote);
            AssetTreeNode oos = Assert.Single(ix.BuildTree().Roots, r => r.Label == AssetTree.OutOfServiceLabel);
            var expected = Enumerable.Range(0, m.AllGenerators.Count).Where(k => !m.AllGenerators[k].InService).ToList();
            Assert.NotEmpty(expected);
            Assert.Equal(expected.Select(k => AssetKey.OutOfService(m, k)), oos.Children.Select(n => n.Key));
            Assert.Equal(expected.Count.ToString(Inv), oos.Detail);
            // Each one's record is the generation.json record at its fleet position, its PROPERTIES say "inService": false.
            foreach (int k in expected)
            {
                CaseRef r = ix.RecordOf(AssetKey.OutOfService(m, k))!;
                Assert.Equal(("generation.json", k), (r.FileName, r.Index));
                PropertyRow row = Assert.Single(ix.Properties(AssetKey.OutOfService(m, k), null)!.Rows, x => x.Field == "inService");
                Assert.Equal("false", row.Literal);
                Assert.Equal(ix.FleetKey(k), AssetKey.OutOfService(m, k));
            }
            // The in-service units keep their A3 keys and records through the fleet.
            for (int i = 0; i < m.Generators.Count; i++)
                Assert.Equal(ix.Map!.GeneratorRecord(i), ix.RecordOf(AssetKey.Unit(m, i, AssetIndex.IsLink(m.Generators[i]))));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_source_that_is_not_a_case_file_has_no_records_and_says_why()
    {
        GridModel m = Frames("case9")[0].Frame.Model;
        foreach ((AssetCaseSource s, string why) in new[]
                 {
                     (AssetCaseSource.From(SourceOf("case9")), "PROPERTIES is not shown: this model was read from a MATPOWER .m file, not a GridSim case file, so it has no case records."),
                     (new AssetCaseSource(AssetSourceKind.Replay, "x"), "PROPERTIES is not shown: this is a replay of an exported run folder, not a GridSim case file, so it has no case records."),
                     (new AssetCaseSource(AssetSourceKind.Bridge, "ws://x"), "PROPERTIES is not shown: this model is streamed by the GDA bridge, not read from a GridSim case file, so it has no case records."),
                 })
        {
            AssetIndex ix = AssetIndex.Build(m, s, Gate());
            Assert.False(ix.HasCase);
            Assert.Equal(why, ix.CaseNote);
            Assert.Empty(ix.Demands);
            Assert.Null(ix.Properties(AssetKey.Substation(m.Buses[0].Id), null));
            Assert.Equal(8, ix.BuildTree().Roots.Count);   // case9 has no boundary, protection, class or out-of-service unit
            Assert.True(ix.Connections(AssetKey.Substation(m.Buses[0].Id)).Any);   // the model adjacency still is there
        }
        Assert.Equal(AssetSourceKind.Matpower, AssetCaseSource.From(SourceOf("case9")).Kind);
        Assert.Equal(AssetSourceKind.V1Folder, AssetCaseSource.From(new ModelSource("x", "d")).Kind);
        Assert.Equal(AssetSourceKind.Replay, AssetCaseSource.From(new ModelSource("x", "d", ReplayDir: "r")).Kind);
        Assert.Equal(AssetSourceKind.Bridge, AssetCaseSource.From(new ModelSource("x", "", Bridge: "ws://b")).Kind);
        Assert.Same(AssetCaseSource.Undeclared, AssetCaseSource.From(null));
    }

    // ================================================================ PROPERTIES

    // The record at position index of a v1 file, read independently of the editing engine (System.Text.Json).
    private static JsonElement RecordOf(string relative, string file, int index)
    {
        var opts = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        using JsonDocument d = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(EditorTestSupport.CaseDir(relative), file)), opts);
        return d.RootElement[index].Clone();
    }

    // The rows PROPERTIES must show for one v1 record: one per schema field in schema order (the catalogue's FieldSpec),
    // the literal being the record's own text of the member (matched ignoring case, as the v1 loader matches keys).
    private static List<(string Field, string Label, string Unit, string Help, string Kind, bool Required, bool Present, string Literal, string Absent)>
        ExpectedRows(string relative, CaseRef r)
    {
        RecordShape shape = FieldCatalogue.V1RecordShape(CaseFileSpec.ForFile(r.FileName)!);
        JsonElement rec = RecordOf(relative, r.FileName, r.Index);
        var rows = new List<(string, string, string, string, string, bool, bool, string, string)>();
        foreach (FieldSpec f in shape.Fields)
        {
            JsonProperty? p = rec.EnumerateObject().Where(x => string.Equals(x.Name, f.Name, StringComparison.OrdinalIgnoreCase)).Cast<JsonProperty?>().FirstOrDefault();
            string label = f.Title, unit = "";
            int open = f.Title.LastIndexOf(" (", StringComparison.Ordinal);
            if (f.Title.EndsWith(')') && open > 0) { label = f.Title[..open]; unit = f.Title[(open + 2)..^1]; }
            rows.Add((f.Name, label, unit, f.Description, f.Kind.ToString(), f.Required, p is not null, p?.Value.GetRawText() ?? "", f.AbsentText));
        }
        Assert.Equal(rec.EnumerateObject().Count(), rows.Count(x => x.Item7));   // every member of the record is a schema field
        return rows;
    }

    private static void AssertRows(string relative, AssetProperties p)
    {
        var expected = ExpectedRows(relative, p.Record);
        Assert.Equal(expected, p.Rows.Select(r => (r.Field, r.Label, r.Unit, r.Help, r.Kind, r.Required, r.Present, r.Literal, r.Absent)));
        Assert.All(p.Rows, r => Assert.Equal(r.Present ? "as written" : "absent: " + r.Absent, r.State));
        Assert.Equal(FieldCatalogue.V1RecordShape(CaseFileSpec.ForFile(p.Record.FileName)!).Title, p.Shape);
    }

    [Fact]
    public void PROPERTIES_rows_are_the_records_literals_and_the_field_specs_exactly_with_the_solved_value_labelled()
    {
        var (frame, _) = Frames("uk")[0];
        GridModel m = frame.Model;
        AssetIndex ix = AssetIndex.Build(m, AssetCaseSource.From(SourceOf("uk")), Gate());
        Assert.True(ix.HasCase, ix.CaseNote);
        string Num(double v) => AssetIndex.Num(v);
        Assert.Equal("1.25", Num(1.25));
        Assert.Equal(("Capacity", "MVA"), PropertyRow.SplitTitle("Capacity (MVA)"));
        Assert.Equal(("Name", ""), PropertyRow.SplitTitle("Name"));

        // ---- a circuit: r, x, b and the ratings beside the loader's per-circuit equivalent, labelled with the count ----
        int ci = Enumerable.Range(0, m.Branches.Count).First(i => !m.Branches[i].IsTransformer);
        AssetProperties line = ix.Properties(AssetKey.Branch(m, ci), frame)!;
        AssertRows("uk", line);
        JsonElement lr = RecordOf("uk", "transmission.json", line.Record.Index);
        int circuits = lr.TryGetProperty("circuits", out JsonElement ce) ? ce.GetInt32() : 1;
        string cap = $"as solved: per-circuit equivalent of {circuits} circuit" + (circuits == 1 ? "" : "s");
        Branch br = m.Branches[ci];
        var solved = line.Rows.Where(r => r.HasSolved).ToDictionary(r => r.Field, r => (r.SolvedCaption, r.SolvedValue));
        Assert.Equal((cap, Num(br.R)), solved["rPu"]);
        Assert.Equal((cap, Num(br.X)), solved["xPu"]);
        Assert.Equal((cap, Num(br.B)), solved["bPu"]);
        Assert.Equal((cap, Num(br.RateAMva)), solved["ratingMva"]);
        Assert.Equal(("as solved", Num(br.TapRatio)), solved["tapRatio"]);
        Assert.Equal(("", ""), line.Rows.Where(r => r.Field is "name" or "from" or "to").Select(r => (r.SolvedCaption, r.SolvedValue)).Distinct().Single());
        string name = lr.TryGetProperty("name", out JsonElement ne) ? " '" + ne.GetString() + "'" : "";
        Assert.Equal("transmission.json #" + line.Record.Index.ToString(Inv) + name, line.Heading);
        Assert.Equal("Solved values from tick " + frame.Tick.ToString(Inv) + ".", line.SolvedNote);

        // ---- a transformer ----
        int ti = Enumerable.Range(0, m.Branches.Count).First(i => m.Branches[i].IsTransformer);
        AssetProperties tx = ix.Properties(AssetKey.Branch(m, ti), frame)!;
        AssertRows("uk", tx);
        Assert.Equal(("as solved", Num(m.Branches[ti].X)), tx.Rows.Single(r => r.Field == "xPu") is var x ? (x.SolvedCaption, x.SolvedValue) : default);

        // ---- a generator: the dispatched output, the limits as the solver holds them ----
        Generator g = m.Generators[0];
        AssetProperties gen = ix.Properties(AssetKey.Unit(m, 0, AssetIndex.IsLink(g)), frame)!;
        AssertRows("uk", gen);
        Assert.Equal(("generation.json", ix.Map!.GeneratorRecord(0).Index), (gen.Record.FileName, gen.Record.Index));
        var gs = gen.Rows.Where(r => r.HasSolved).ToDictionary(r => r.Field, r => (r.SolvedCaption, r.SolvedValue));
        Assert.Equal(("as solved", Num(g.Pg)), gs["outputMw"]);
        Assert.Equal(("as solved", Num(g.MBase)), gs["capacityMva"]);
        Assert.Equal(("as solved", Num(g.Qmax)), gs["qMaxMvar"]);

        // ---- a bus: the solved voltage beside the written start value ----
        Bus bus = m.Buses[0];
        AssetProperties bp = ix.Properties(AssetKey.Substation(bus.Id), frame)!;
        AssertRows("uk", bp);
        int at = frame.Result.BusIds.ToList().IndexOf(bus.Id);
        Assert.Equal(("as solved", Num(frame.Result.Vm[at])), bp.Rows.Single(r => r.Field == "vm") is var v ? (v.SolvedCaption, v.SolvedValue) : default);
        Assert.Equal(("as solved", Num(frame.Result.VaDeg[at])), bp.Rows.Single(r => r.Field == "vaDeg") is var va ? (va.SolvedCaption, va.SolvedValue) : default);

        // ---- a demand record: the bus total it folds into, labelled as the bus's ----
        DemandRecord d = ix.Demands[0];
        AssetProperties dp = ix.Properties(AssetIndex.DemandKey(d), frame)!;
        AssertRows("uk", dp);
        Bus db = m.Buses.First(x => x.Id == d.Bus);
        string dcap = "bus " + d.Bus.ToString(Inv) + " total as solved (every demand record on the bus)";
        Assert.Equal((dcap, Num(db.Pd)), dp.Rows.Single(r => r.Field == "pMw") is var pr ? (pr.SolvedCaption, pr.SolvedValue) : default);

        // ---- a tap changer and a boundary: records with no solved column ----
        AssetProperties op = ix.Properties(AssetKey.TapChanger(m, 0), frame)!;
        AssertRows("uk", op);
        Assert.DoesNotContain(op.Rows, r => r.HasSolved);
        AssetProperties bdp = ix.Properties(ix.BoundaryKey(0), frame)!;
        AssertRows("uk", bdp);
        Assert.Equal(("boundaries.json", 0), (bdp.Record.FileName, bdp.Record.Index));

        // ---- without a frame: the same rows, no solved column ----
        AssetProperties noFrame = ix.Properties(AssetKey.Branch(m, ci), null)!;
        Assert.Equal(line.Rows.Select(r => r with { SolvedCaption = "", SolvedValue = "" }), noFrame.Rows);
        Assert.Equal("No solved frame yet: the solved column fills in when the model is solved.", noFrame.SolvedNote);

        // ---- what has no record: the landing page, a class, a group, an asset not in the model ----
        Assert.Null(ix.Properties(AssetKey.Home, frame));
        Assert.Null(ix.Properties(AssetKey.ForClass("class:GeneratorFleet"), frame));
        Assert.Null(ix.Properties(AssetKey.Substation(int.MinValue), frame));
        Assert.Null(ix.Properties(AssetKey.ForModel(AssetKeyKind.Generator, ModelRef.Unit(0, g.Bus, "not this unit")), frame));
    }

    [Fact]
    public void PROPERTIES_of_protections_classes_and_bindings_are_their_records()
    {
        GridModel m = Load("gb-synthetic");
        AssetIndex ix = IndexOf("gb-synthetic", m);
        Assert.True(ix.HasCase, ix.CaseNote);
        AssertRows("gb-synthetic", ix.Properties(ix.ProtectionKey(0), null)!);
        AssertRows("gb-synthetic", ix.Properties(ix.ClassKey(0), null)!);
        AssertRows("gb-synthetic", ix.Properties(ix.BindingKey(0), null)!);
        Assert.Equal(("protections.json", 0), (ix.RecordOf(ix.ProtectionKey(0))!.FileName, ix.RecordOf(ix.ProtectionKey(0))!.Index));
        Assert.Equal(("asset-classes.json", 1), (ix.RecordOf(ix.ClassKey(1))!.FileName, ix.RecordOf(ix.ClassKey(1))!.Index));
        Assert.Equal(("asset-bindings.json", 1), (ix.RecordOf(ix.BindingKey(1))!.FileName, ix.RecordOf(ix.BindingKey(1))!.Index));
    }

    // ================================================================ CONNECTIONS

    [Fact]
    public void CONNECTIONS_list_the_model_adjacency_and_the_ReferenceIndex_where_used_list_exactly()
    {
        GridModel m = Load("uk");
        AssetIndex ix = IndexOf("uk", m);
        ReferenceIndex refs = ReferenceIndex.Build(CaseSession.OpenV1(EditorTestSupport.CaseDir("uk"), Gate()));
        int[] driven = SteadyStateSolver.MapOltcRecordsToBranches(m);

        // ---- a bus: its branches (model order, from end then to end), units, tap changers, demand records ----
        Bus bus = m.Buses.OrderByDescending(b => m.Branches.Count(x => x.From == b.Id || x.To == b.Id)).First();
        int id = bus.Id;
        var expected = new List<(string Relation, AssetKey Target)>();
        for (int i = 0; i < m.Branches.Count; i++)
        {
            string what = m.Branches[i].IsTransformer ? "transformer" : "circuit";
            if (m.Branches[i].From == id) expected.Add((what + " (from end)", AssetKey.Branch(m, i)));
            if (m.Branches[i].To == id) expected.Add((what + " (to end)", AssetKey.Branch(m, i)));
        }
        for (int i = 0; i < m.Generators.Count; i++)
            if (m.Generators[i].Bus == id)
                expected.Add((AssetIndex.IsLink(m.Generators[i]) ? "interconnector on this bus" : "unit on this bus", AssetKey.Unit(m, i, AssetIndex.IsLink(m.Generators[i]))));
        for (int k = 0; k < m.Oltcs.Count; k++)
            if (m.Oltcs[k].ControlsBus == id) expected.Add(("tap changer regulating this bus", AssetKey.TapChanger(m, k)));
        foreach (DemandRecord d in ix.Demands)
            if (d.Bus == id) expected.Add(("demand record folded into this bus", AssetIndex.DemandKey(d)));
        AssetConnections c = ix.Connections(AssetKey.Substation(id));
        Assert.Equal(expected, c.Model.Select(r => (r.Relation, r.Target!)));
        Assert.All(c.Model, r => Assert.Equal(AssetConnections.ModelSection, r.Section));
        // Where used: exactly the editor's referrers of the bus id, in its order.
        Assert.Equal(refs.BusReferrers(id).Select(r => (r.Field, r.What)), c.WhereUsed.Select(r => (r.Relation, r.Label)));
        Assert.All(c.WhereUsed, r => Assert.Equal(AssetConnections.WhereUsedSection, r.Section));
        Assert.True(c.WhereUsed.Count > 0);

        // ---- a transformer driven by a tap changer: its ends, the tap changer, the boundaries crossing it ----
        int k0 = Enumerable.Range(0, driven.Length).First(k => driven[k] >= 0);
        int ti = driven[k0];
        Branch tx = m.Branches[ti];
        AssetConnections tc = ix.Connections(AssetKey.Branch(m, ti));
        var tExpected = new List<(string, AssetKey?)> { ("from bus", AssetKey.Substation(tx.From)), ("to bus", AssetKey.Substation(tx.To)) };
        for (int k = 0; k < driven.Length; k++) if (driven[k] == ti) tExpected.Add(("tap changer driving this transformer", AssetKey.TapChanger(m, k)));
        for (int k = 0; k < m.Boundaries.Count; k++)
            foreach (BoundaryBranch bb in m.Boundaries[k].Branches)
                if (bb.Branch == tx.Name) tExpected.Add(("boundary crossing this branch", ix.BoundaryKey(k)));
        Assert.Equal(tExpected, tc.Model.Select(r => (r.Relation, r.Target)));
        Assert.Equal(refs.BranchReferrers(tx.Name!).Select(r => (r.Field, r.What)), tc.WhereUsed.Select(r => (r.Relation, r.Label)));

        // ---- a boundary: its branches, each a link to its branch ----
        Boundary bd = m.Boundaries[0];
        AssetConnections bc = ix.Connections(ix.BoundaryKey(0));
        Assert.Equal(bd.Branches.Select(x => AssetKey.Branch(m, m.Branches.ToList().FindIndex(y => y.Name == x.Branch))), bc.Model.Select(r => r.Target!));
        Assert.Equal(bd.Branches.Select(x => "direction " + (x.Direction > 0 ? "+" : "-") + Math.Abs(x.Direction).ToString(Inv)), bc.Model.Select(r => r.Detail));

        // ---- nothing for the tree's own nodes ----
        Assert.False(ix.Connections(AssetKey.Home).Any);
        Assert.False(ix.Connections(AssetKey.ForClass("class:SubstationFleet")).Any);
        Assert.False(ix.Connections(AssetKey.Substation(int.MinValue)).Any);
    }

    [Theory]
    [InlineData("uk")]
    [InlineData("gb-synthetic")]
    public void Every_CONNECTIONS_link_of_every_node_selects_a_node_of_the_tree(string relative)
    {
        GridModel m = Load(relative);
        AssetIndex ix = IndexOf(relative, m);
        AssetTree t = ix.BuildTree();
        int links = 0, used = 0;
        foreach (AssetTreeNode n in t.PreOrder())
        {
            AssetConnections c = ix.Connections(n.Key);
            foreach (ConnectionRow r in c.Model.Concat(c.WhereUsed))
            {
                if (r.Target is null) continue;   // a name the model does not carry (none in a bundled case)
                Assert.True(t.NodeFor(r.Target) is not null, $"{n}: {r.Relation} '{r.Label}' names {r.Target}, which no node stands for");
                links++;
            }
            used += c.WhereUsed.Count;
            Assert.All(c.Model.Concat(c.WhereUsed), r => Assert.NotNull(r.Target));
        }
        Assert.True(links > 0 && used > 0);
        if (relative == "gb-synthetic")
        {
            // An asset class: its bindings, protections and the units naming it; the where-used list is the editor's.
            ReferenceIndex refs = ReferenceIndex.Build(CaseSession.OpenV1(EditorTestSupport.CaseDir(relative), Gate()));
            for (int ci = 0; ci < ix.Classes.Count; ci++)
            {
                AssetConnections cc = ix.Connections(ix.ClassKey(ci));
                Assert.Equal(refs.ClassReferrers(ix.Classes[ci].Name).Select(r => (r.Field, r.What)), cc.WhereUsed.Select(r => (r.Relation, r.Label)));
                Assert.Equal(ix.Bindings.Select((b, k) => (b, k)).Where(x => string.Equals(x.b.ClassName, ix.Classes[ci].Name, StringComparison.OrdinalIgnoreCase))
                    .Select(x => ix.BindingKey(x.k)), cc.Model.Where(r => r.Relation == "binding rule using this class").Select(r => r.Target!));
            }
        }
    }

    // ================================================================ the navigator's sub-tabs

    [Fact]
    public void Until_the_source_is_declared_the_navigator_is_the_A3_navigator()
    {
        var nav = new AssetNavigatorModel();
        foreach (var (f, c) in Frames("uk")) nav.Update(f, c);
        Assert.Equal(8, nav.Roots.Count);
        Assert.Null(nav.Index);
        Assert.Same(AssetCaseSource.Undeclared, nav.Source);
        foreach (AssetTreeNode n in nav.Roots.SelectMany(Flatten).Where(n => n.Role == AssetNodeRole.Unit).Take(20))
        {
            nav.Select(n);
            Assert.Equal(new[] { "OVERVIEW" }, nav.SubTabs);
            Assert.Null(nav.Properties);
            Assert.False(nav.Connections.Any);
            Assert.Equal("", nav.SubTabNote);
        }
    }

    [Fact]
    public void A_case_file_offers_PROPERTIES_and_CONNECTIONS_on_its_assets_and_only_OVERVIEW_on_the_tree_nodes()
    {
        var frames = Frames("uk");
        AssetNavigatorModel nav = Navigator("uk");
        AssetIndex ix = Assert.IsType<AssetIndex>(nav.Index);
        Assert.True(ix.HasCase, ix.CaseNote);
        Assert.Equal(new[] { "DEMANDS", "BOUNDARIES" }, nav.Roots.Skip(8).Select(r => r.Label));
        Assert.Equal("", nav.SubTabNote);
        Assert.False(nav.HasSubTabNote);

        // Every asset of the A3 classes and the new ones: OVERVIEW, PROPERTIES, CONNECTIONS (each has a record and a link).
        foreach (AssetTreeNode cls in nav.Roots.Skip(1))
        {
            nav.Select(cls);
            Assert.Equal(new[] { "OVERVIEW" }, nav.SubTabs);
            AssetTreeNode? unit = Flatten(cls).FirstOrDefault(n => n.Role == AssetNodeRole.Unit);
            if (unit is null) continue;   // GENERATOR PHASORS has no children
            nav.Select(unit);
            Assert.Equal(new[] { "OVERVIEW", "PROPERTIES", "CONNECTIONS" }, nav.SubTabs);
            Assert.Equal(ix.Connections(unit.Key).Model, nav.Connections.Model);
            Assert.Equal(ix.Connections(unit.Key).WhereUsed, nav.Connections.WhereUsed);
        }
        nav.Select(nav.Tree.Home);
        Assert.Equal(new[] { "OVERVIEW" }, nav.SubTabs);
        Assert.Throws<ArgumentException>(() => nav.SelectedSubTab = "PROPERTIES");

        // PROPERTIES: the index's rows for the node, with the last frame's solved values; they follow the frames.
        AssetTreeNode line = Flatten(nav.Roots[5]).First(n => n.Role == AssetNodeRole.Unit);
        nav.Select(line);
        Assert.True(nav.IsOverviewShown);
        nav.SelectedSubTab = "PROPERTIES";
        Assert.True(nav.IsPropertiesShown);
        Assert.False(nav.IsOverviewShown);
        Assert.Equal(ix.Properties(line.Key, frames[^1].Frame)!.Rows, nav.Properties!.Rows);
        nav.Update(frames[0].Frame, frames[0].Clock);
        Assert.Equal(ix.Properties(line.Key, frames[0].Frame)!.Rows, nav.Properties!.Rows);
        // A node without that sub-tab puts the view back on OVERVIEW.
        nav.Select(nav.Roots[5]);
        Assert.Equal("OVERVIEW", nav.SelectedSubTab);
        Assert.Null(nav.Properties);

        // CONNECTIONS: a link selects its node and opens its page.
        nav.Select(line);
        nav.SelectedSubTab = "CONNECTIONS";
        Assert.True(nav.IsConnectionsShown);
        ConnectionRow from = nav.Connections.Model[0];
        Assert.Equal("from bus", from.Relation);
        Assert.True(nav.OpenConnection(from));
        Assert.Equal(from.Target, nav.Selected!.Key);
        Assert.Equal(AssetPageKey.Substation(from.Target!.Model!.BusA), nav.Explorer.KeyOf(nav.Current));
        Assert.True(nav.GoBack());
        Assert.Same(line, nav.Selected);
        Assert.False(nav.OpenKey(AssetKey.Substation(int.MinValue)));
        Assert.False(nav.OpenConnection(new ConnectionRow("s", "r", "l", "d", null)));
    }

    [Fact]
    public void A_source_that_is_not_a_case_file_offers_no_PROPERTIES_and_says_why()
    {
        AssetNavigatorModel nav = Navigator("case9");
        Assert.Equal("PROPERTIES is not shown: this model was read from a MATPOWER .m file, not a GridSim case file, so it has no case records.", nav.SubTabNote);
        Assert.True(nav.HasSubTabNote);
        Assert.Equal(8, nav.Roots.Count);
        AssetTreeNode bus = Flatten(nav.Roots[1]).First(n => n.Role == AssetNodeRole.Unit);
        nav.Select(bus);
        Assert.Equal(new[] { "OVERVIEW", "CONNECTIONS" }, nav.SubTabs);
        Assert.Null(nav.Properties);
    }

    [Fact]
    public void The_index_is_built_only_once_the_tab_is_shown()
    {
        var nav = new AssetNavigatorModel { Active = false };
        nav.SetSource(AssetCaseSource.From(SourceOf("uk")), Gate());
        foreach (var (f, c) in Frames("uk")) nav.Update(f, c);
        Assert.Null(nav.Index);
        Assert.Equal(8, nav.Roots.Count);
        nav.Active = true;
        Assert.NotNull(nav.Index);
        Assert.Equal(10, nav.Roots.Count);
        // A new source drops the index (and its nodes) until the next frame of the new model.
        nav.SetSource(AssetCaseSource.From(SourceOf("case9")), Gate());
        Assert.Null(nav.Index);
        Assert.Equal(8, nav.Roots.Count);
    }

    // ================================================================ the new OVERVIEW pages

    [Fact]
    public void Selecting_a_new_class_node_opens_its_overview_page_and_its_rows_select_their_nodes()
    {
        var frames = Frames("uk");
        AssetNavigatorModel nav = Navigator("uk");
        AssetIndex ix = nav.Index!;
        foreach (AssetTreeNode cls in nav.Roots.Skip(8))
        {
            nav.Select(cls);
            AssetOverviewPage page = Assert.IsType<AssetOverviewPage>(nav.Current);
            Assert.Equal(cls.Page, page.Key);
            Assert.Equal(cls.Page, nav.Explorer.KeyOf(page));
            Assert.Same(cls, nav.Selected);
            Assert.Equal(cls.Path, nav.Crumb);
            AssertSameContent(OverviewBuilder.Build(cls.Page, ix, frames[^1].Frame), page.Content);
            // A row: selects its member and opens the member's page.
            OverviewRow row = page.Rows[0];
            Assert.True(nav.OpenKey(row.Target!));
            AssetOverviewPage unit = Assert.IsType<AssetOverviewPage>(nav.Current);
            Assert.Equal(row.Target, nav.Selected!.Key);
            Assert.Equal(nav.Selected.Page, unit.Key);
            // Back returns the very class page.
            Assert.True(nav.GoBack());
            Assert.Same(page, nav.Current);
        }
        // A demand page sits on the map at its bus.
        AssetTreeNode d = nav.Roots.Single(r => r.Label == AssetTree.DemandsLabel).Children[0];
        nav.Select(d);
        Assert.Equal(new AssetMapTarget(ix.Demands[0].Bus, null), nav.MapTarget);
        // OPEN INVERTER FLEET asks the shell.
        Assert.False(nav.OpenInverterFleet());
        int asked = 0;
        nav.InverterFleetRequested += (_, _) => asked++;
        Assert.True(nav.OpenInverterFleet());
        Assert.Equal(1, asked);
    }

    private static void AssertSameContent(OverviewContent e, OverviewContent a)
    {
        Assert.Equal((e.Eyebrow, e.Title, e.Sub, e.RowsHeader, e.RowsNote, e.Note, e.LinksToInverterFleet),
            (a.Eyebrow, a.Title, a.Sub, a.RowsHeader, a.RowsNote, a.Note, a.LinksToInverterFleet));
        Assert.Equal(e.Tiles, a.Tiles);
        Assert.Equal(e.Facts, a.Facts);
        Assert.Equal(e.Rows, a.Rows);
    }

    private static string Mw(double v) => v.ToString("F1", Inv) + " MW";

    [Fact]
    public void The_DEMANDS_and_BOUNDARIES_pages_say_exactly_what_the_records_and_the_frame_say()
    {
        var (frame, _) = Frames("uk")[0];
        GridModel m = frame.Model;
        AssetIndex ix = AssetIndex.Build(m, AssetCaseSource.From(SourceOf("uk")), Gate());
        List<DemandDto> dtos = JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(SourceOf("uk").Dir, "demand.json")), JsonGridLoader.Json.ListDemandDto)!;

        // ---- DEMANDS ----
        OverviewContent dc = OverviewBuilder.Build(AssetPageKey.OverviewClass(AssetPageKind.DemandFleet), ix, frame);
        var buses = dtos.Select(x => x.Bus).Distinct().ToList();
        double authored = 0;
        foreach (DemandDto x in dtos) authored += x.PMw;
        double solved = 0;
        foreach (int b in buses) solved += m.Buses.First(x => x.Id == b).Pd;
        Assert.Equal(new[]
        {
            new OverviewTile("RECORDS", dtos.Count.ToString("N0", Inv), "demand records in the case file"),
            new OverviewTile("AUTHORED P", Mw(authored), "the records' pMw as written"),
            new OverviewTile("BUSES", buses.Count.ToString("N0", Inv), "carrying demand records"),
            new OverviewTile("SOLVED BUS DEMAND", Mw(solved), "those buses' Pd as solved, this frame"),
        }, dc.Tiles);
        Assert.Equal(("ASSET CLASS", "DEMANDS", "RECORDS"), (dc.Eyebrow, dc.Title, dc.RowsHeader));
        Assert.Equal(Math.Min(dtos.Count, OverviewBuilder.RowCap), dc.Rows.Count);
        Assert.Equal(dtos.Count > OverviewBuilder.RowCap ? $"showing {OverviewBuilder.RowCap} of {dtos.Count:N0}" : "", dc.RowsNote);
        for (int k = 0; k < dc.Rows.Count; k++)
        {
            Assert.Equal(dtos[k].Name ?? "(unnamed)", dc.Rows[k].Name);
            Assert.Equal(Mw(dtos[k].PMw), dc.Rows[k].Value);
            Assert.Equal(AssetIndex.DemandKey(ix.Demands[k]), dc.Rows[k].Target);
        }
        // One demand record's page.
        DemandRecord d = ix.Demands[0];
        OverviewContent du = OverviewBuilder.Build(new AssetPageKey(AssetPageKind.Demand, 0, d.Bus, 0, d.Name), ix, frame);
        Bus db = m.Buses.First(x => x.Id == d.Bus);
        Assert.Equal(Mw(dtos[0].PMw), du.Facts.Single(f => f.Label == "Authored P").Value);
        Assert.Equal("bus " + d.Bus.ToString(Inv) + ": Pd " + Mw(db.Pd) + ", Qd " + db.Qd.ToString("F1", Inv) + " Mvar as solved",
            du.Facts.Single(f => f.Label == "Folds into").Value);
        Assert.Equal(dtos.Count(x => x.Bus == d.Bus).ToString("N0", Inv), du.Facts.Single(f => f.Label == "Records on this bus").Value);
        Assert.Equal(AssetKey.Substation(d.Bus), Assert.Single(du.Rows).Target);

        // ---- BOUNDARIES: the Core boundary analysis of this frame ----
        IReadOnlyList<BoundaryFlow> flows = BoundaryAnalysis.Evaluate(m, frame.Result);
        OverviewContent bc = OverviewBuilder.Build(AssetPageKey.OverviewClass(AssetPageKind.BoundaryFleet), ix, frame);
        Assert.Equal(m.Boundaries.Count.ToString("N0", Inv), bc.Tiles[0].Value);
        Assert.Equal(Mw(m.Boundaries.Sum(b => b.LimitMw)), bc.Tiles[1].Value);
        Assert.Equal(flows.Count(f => f.Overloaded).ToString("N0", Inv), bc.Tiles[2].Value);
        Assert.Equal(flows.Select(f => Mw(f.FlowMw)), bc.Rows.Select(r => r.Value));
        Assert.Equal(m.Boundaries.Select((b, k) => ix.BoundaryKey(k)), bc.Rows.Select(r => r.Target!));
        OverviewContent bu = OverviewBuilder.Build(new AssetPageKey(AssetPageKind.Boundary, 0, 0, 0, m.Boundaries[0].Name), ix, frame);
        Assert.Equal(Mw(flows[0].FlowMw), bu.Facts.Single(f => f.Label == "Flow").Value);
        Assert.Equal(m.Boundaries[0].Branches.Select(x => x.Branch), bu.Rows.Select(r => r.Name));
        // Before any frame: no flow.
        Assert.Equal(OverviewBuilder.NoValue, OverviewBuilder.Build(AssetPageKey.OverviewClass(AssetPageKind.BoundaryFleet), ix, null).Tiles[2].Value);
        // A page for a boundary no longer in the model says so.
        Assert.Equal("this boundary is not in the current model",
            OverviewBuilder.Build(new AssetPageKey(AssetPageKind.Boundary, 0, 0, 0, "no such boundary"), ix, frame).Sub);
        Assert.Throws<ArgumentOutOfRangeException>(() => OverviewBuilder.Build(AssetPageKey.Home, ix, frame));
    }

    [Fact]
    public void The_PROTECTIONS_ASSET_CLASSES_and_OUT_OF_SERVICE_pages_say_exactly_what_the_bundles_say()
    {
        GridModel m = Load("gb-synthetic");
        AssetIndex ix = IndexOf("gb-synthetic", m);
        var ps = m.Protections!.Instances;
        OverviewContent pc = OverviewBuilder.Build(AssetPageKey.OverviewClass(AssetPageKind.ProtectionFleet), ix, null);
        Assert.Equal(new[]
        {
            new OverviewTile("PROTECTIONS", ps.Count.ToString("N0", Inv), "placed protective devices"),
            new OverviewTile("CAPACITY", Mw(ps.Sum(p => p.CapacityMw)), "the devices' authored capacity"),
        }, pc.Tiles);
        Assert.Equal(ps.Take(OverviewBuilder.RowCap).Select(p => (p.Name, Mw(p.CapacityMw))), pc.Rows.Select(r => (r.Name, r.Value)));
        OverviewContent pu = OverviewBuilder.Build(new AssetPageKey(AssetPageKind.Protection, 0, ps[0].MonitorBus, 0, ps[0].Name), ix, null);
        Assert.Equal(ps[0].ClassName, pu.Facts.Single(f => f.Label == "Class").Value);
        Assert.Equal(AssetKey.Substation(ps[0].MonitorBus), pu.Rows[0].Target);

        AssetDynamicsModel dyn = m.AssetDynamics!;
        List<AssetClass> classes = dyn.Library.Classes.ToList();
        OverviewContent cc = OverviewBuilder.Build(AssetPageKey.OverviewClass(AssetPageKind.AssetClassFleet), ix, null);
        Assert.Equal(classes.Count.ToString("N0", Inv), cc.Tiles[0].Value);
        Assert.Equal(dyn.Bindings.Rules.Count.ToString("N0", Inv), cc.Tiles[1].Value);
        Assert.Equal(classes.Select(c => c.Name).Concat(dyn.Bindings.Rules.Select(r => r.MatchType)), cc.Rows.Select(r => r.Name));
        Assert.True(cc.LinksToInverterFleet);
        AssetClass c0 = classes[0];
        OverviewContent cu = OverviewBuilder.Build(new AssetPageKey(AssetPageKind.AssetClass, 0, 0, 0, c0.Name), ix, null);
        Assert.Equal(c0.Params.Select(p => (p.Key, c0.ProvenanceFor(p.Key).Tag.ToString(), AssetIndex.Num(p.Value))),
            cu.Rows.Select(r => (r.Name, r.Detail, r.Value)));
        int bound = m.AllGenerators.Count(g => string.Equals(dyn.Bindings.Resolve(g.DynamicsClass, g.Type), c0.Name, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(bound.ToString("N0", Inv), cu.Facts.Single(f => f.Label == "Units bound to it").Value);
        AssetBinding b0 = dyn.Bindings.Rules[0];
        OverviewContent bu = OverviewBuilder.Build(new AssetPageKey(AssetPageKind.AssetBinding, 0, 0, 0, b0.MatchType), ix, null);
        Assert.Equal(ix.ClassKey(ix.ClassIndexOf(b0.ClassName)), Assert.Single(bu.Rows).Target);

        string dir = OutOfServiceCopy("uk");
        try
        {
            GridModel om = JsonGridLoader.LoadDirectory(dir);
            AssetIndex oix = AssetIndex.Build(om, AssetCaseSource.V1(dir), Gate());
            var off = om.AllGenerators.Where(g => !g.InService).ToList();
            OverviewContent oc = OverviewBuilder.Build(AssetPageKey.OverviewClass(AssetPageKind.OutOfServiceFleet), oix, null);
            Assert.Equal(off.Count.ToString("N0", Inv), oc.Tiles[0].Value);
            Assert.Equal(off.Sum(g => g.MBase).ToString("F0", Inv) + " MVA", oc.Tiles[1].Value);
            Assert.Equal(off.Take(OverviewBuilder.RowCap).Select(g => g.Name ?? $"unit @ bus {g.Bus}"), oc.Rows.Select(r => r.Name));
            int k = om.AllGenerators.ToList().FindIndex(g => !g.InService);
            OverviewContent ou = OverviewBuilder.Build(new AssetPageKey(AssetPageKind.OutOfServiceUnit, k, om.AllGenerators[k].Bus, 0, om.AllGenerators[k].Name), oix, null);
            Assert.Equal("no: kept in the case, never solved", ou.Facts.Single(f => f.Label == "In service").Value);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
