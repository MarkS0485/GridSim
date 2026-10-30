using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Lossless;
using GridSim.Core.Layout;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Tests.Editor;
using GridSim.Ui.Assets.Editing;
using GridSim.Ui.Assets.Index;
using GridSim.Ui.Assets.Keys;
using GridSim.Ui.Commands;
using GridSim.Ui.Schematic;
using GridSim.Ui.Shell;
using Xunit;

namespace GridSim.Tests.Ui;

/// <summary>
/// UI programme Phase 4.2: map editing in AUTHOR mode, the UI-neutral half (MapEditing, AuthorMap, the joined undo step
/// of CaseAuthoring, MapAuthorCommands), on temporary copies only (no test writes into data/). Pinned: the coordinate
/// convention (x = world x, y = 1 - world y, six decimals, shortest round trip) against the map's own projection; a
/// placed bus is exactly the engine's Add record with those coordinates (the bytes of buses.json change only by the new
/// record, every other file is untouched), one undo step, undo gives the original bytes back; Connect adds a line or a
/// transformer record whose from and to are the two buses (v1: no other field asked; v2: name, rPu and xPu asked); a
/// drag changes exactly the x and y value tokens of that bus in one undo step; a case with no layout draws the run
/// pipeline's own automatic layout, bus for bus, and its first placed or moved bus writes that layout into the other
/// buses in the same undo step; a delete with referrers is refused with the engine's exact text and changes nothing;
/// the map and the tree name the same records (the selection sync); the commands are Author and desktop only, with
/// their gestures and the map element menu, and clash with nothing.
/// </summary>
public sealed class UiMapEditingTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static LicenceGate Gate() => CaseSessionTests.Unlimited();

    private static readonly IReadOnlyList<string> NoRoots = Array.Empty<string>();

    private static CaseAuthoring OpenV1(string dir) => CaseAuthoring.Open(AssetCaseSource.V1(dir), Gate(), NoRoots);

    private static CaseAuthoring OpenV2(string manifest) => CaseAuthoring.Open(AssetCaseSource.V2(manifest), Gate(), NoRoots);

    private static Dictionary<string, byte[]> Files(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetRelativePath(dir, p), File.ReadAllBytes, StringComparer.Ordinal);

    // The session's current bytes of every document (null: absent), by file name.
    private static Dictionary<string, byte[]?> SessionBytes(CaseAuthoring a) =>
        a.Session.Documents.ToDictionary(d => d.FileName, d => d.Exists ? d.Text.ToArray() : null, StringComparer.Ordinal);

    private static void AssertSameBytes(Dictionary<string, byte[]?> expected, CaseAuthoring a)
    {
        Dictionary<string, byte[]?> now = SessionBytes(a);
        Assert.Equal(expected.Keys.OrderBy(k => k, StringComparer.Ordinal), now.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach ((string file, byte[]? bytes) in expected)
        {
            if (bytes is null) Assert.Null(now[file]);
            else Assert.True(now[file] is byte[] n && n.AsSpan().SequenceEqual(bytes), file + " differs");
        }
    }

    private static AuthoringRecordSet Set(CaseAuthoring a, string role) => a.Collections().First(c => c.Role == role);

    private static CaseRef Rec(CaseAuthoring a, string role, int index) => a.Records(Set(a, role))[index].Ref;

    private static KeyValuePair<string, string> Kv(string k, string v) => new(k, v);

    // The original bytes with the value tokens of members <members> of the object at <pointer> replaced by <literals>.
    private static byte[] Spliced(byte[] original, string name, StringComparer cmp, string pointer, params (string Member, string Literal)[] edits)
    {
        JsonSourceText t = JsonSourceText.Parse(original, name, cmp);
        JsonSourceNode obj = t.Resolve(pointer)!;
        var parts = edits.Select(e => (Node: obj.FindMember(e.Member, cmp)!.Value, e.Literal)).OrderByDescending(p => p.Node.Start).ToList();
        byte[] result = original;
        foreach ((JsonSourceNode node, string literal) in parts)
        {
            byte[] lit = Encoding.UTF8.GetBytes(literal);
            var next = new byte[result.Length - node.Length + lit.Length];
            result.AsSpan(0, node.Start).CopyTo(next);
            lit.CopyTo(next.AsSpan(node.Start));
            result.AsSpan(node.Start + node.Length).CopyTo(next.AsSpan(node.Start + lit.Length));
            result = next;
        }
        return result;
    }

    // data/uk with every bus's "x" and "y" members removed (a case with no layout of its own), in a temporary copy.
    private static EditorTestSupport.TempCopy NoLayoutUk()
    {
        var temp = new EditorTestSupport.TempCopy(EditorTestSupport.CaseDir("uk"));
        string buses = Path.Combine(temp.Dir, "buses.json");
        string text = File.ReadAllText(buses);
        string stripped = Regex.Replace(text, @",\s*""x"":\s*[-0-9.eE]+,\s*""y"":\s*[-0-9.eE]+", "");
        Assert.NotEqual(text, stripped);
        Assert.DoesNotContain("\"x\"", stripped, StringComparison.Ordinal);
        File.WriteAllText(buses, stripped);
        return temp;
    }

    // =================================================================================================================
    // The coordinate convention
    // =================================================================================================================

    [Fact]
    public void Case_coordinates_are_world_x_and_one_minus_world_y_rounded_to_six_places_in_shortest_form()
    {
        Assert.Equal(6, MapEditing.CoordinateDecimals);
        Assert.Equal((0.25, 0.6), MapEditing.CaseCoordinates(0.25, 0.4));
        Assert.Equal((0.123457, 0.75), MapEditing.CaseCoordinates(0.1234567, 0.25));
        Assert.Equal((-0.5, 1.25), MapEditing.CaseCoordinates(-0.5, -0.25));   // never clamped to the 0..1 box
        (double x0, double y0) = MapEditing.CaseCoordinates(-0.0000001, 1.0);
        Assert.Equal(0.0, x0);
        Assert.False(double.IsNegative(x0));   // a negative zero is written as 0, never "-0"
        Assert.False(double.IsNegative(y0));
        Assert.Equal("0", MapEditing.Format(-0.0000001));
        Assert.Equal("0.4", MapEditing.Format(0.4));
        Assert.Equal("0.123457", MapEditing.Format(0.1234565000001));
        Assert.Equal("-0.125", MapEditing.Format(-0.125));
        Assert.Throws<ArgumentOutOfRangeException>(() => MapEditing.CaseCoordinates(double.NaN, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => MapEditing.CaseCoordinates(0, double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => MapEditing.Format(double.NegativeInfinity));

        // The map's own projection takes the written point back to the world point it came from.
        using var temp = new EditorTestSupport.TempCopy(EditorTestSupport.CaseDir("uk"));
        CaseAuthoring a = OpenV1(temp.Dir);
        AuthorMap map = MapEditing.Build(a);
        var view = new SchematicView();
        view.Load(map.Model);
        for (int i = 0; i < map.Buses.Count; i++)
        {
            (double x, double y) = MapEditing.CaseCoordinates(view.Nodes[i].Wx, view.Nodes[i].Wy);
            Assert.Equal(map.Buses[i].X, x);   // data/uk's coordinates have at most two decimals: exact
            Assert.Equal(map.Buses[i].Y, y);
        }
    }

    // =================================================================================================================
    // The Author map
    // =================================================================================================================

    [Fact]
    public void The_Author_map_is_the_cases_bus_line_and_transformer_records_in_order_with_their_references()
    {
        using var temp = new EditorTestSupport.TempCopy(EditorTestSupport.CaseDir("uk"));
        CaseAuthoring a = OpenV1(temp.Dir);
        AuthorMap map = MapEditing.Build(a);
        GridModel loaded = JsonGridLoader.LoadDirectory(temp.Dir);

        Assert.False(map.AutoLayout);
        Assert.Empty(map.NotDrawn);
        Assert.Empty(map.Model.Generators);   // nothing but the drawing: no generation, no demand, no solved state
        Assert.Equal(loaded.Buses.Select(b => (b.Id, b.Name, b.BaseKv, b.X, b.Y)), map.Model.Buses.Select(b => (b.Id, b.Name, b.BaseKv, b.X, b.Y)));
        // Bus types as the loader derives them (the slack from the generator roles).
        Assert.Equal(loaded.Buses.Select(b => b.Type == BusType.Slack), map.Model.Buses.Select(b => b.Type == BusType.Slack));
        // The branches in the loader's order (transmission.json, then transformer.json), with their ends and names.
        Assert.Equal(loaded.Branches.Select(b => (b.From, b.To, b.Name, b.IsTransformer)), map.Model.Branches.Select(b => (b.From, b.To, b.Name, b.IsTransformer)));

        // Each map element is its record, both ways (the selection sync names records by these).
        IReadOnlyList<AuthoringRecord> busRecs = a.Records(Set(a, CaseRoles.Buses));
        Assert.Equal(busRecs.Count, map.Buses.Count);
        for (int i = 0; i < busRecs.Count; i++)
        {
            Assert.Equal(busRecs[i].Ref, map.Buses[i].Record);
            Assert.Same(map.Buses[i], map.BusOf(busRecs[i].Ref));
            Assert.Same(map.Buses[i], map.Bus(map.Buses[i].Id));
            Assert.Null(map.BranchIndexOf(busRecs[i].Ref));
        }
        List<CaseRef> branchRecs = a.Records(Set(a, CaseRoles.Lines)).Concat(a.Records(Set(a, CaseRoles.Transformers))).Select(r => r.Ref).ToList();
        Assert.Equal(branchRecs, map.Branches.Select(b => b.Record));
        for (int i = 0; i < branchRecs.Count; i++)
        {
            Assert.Equal(i, map.BranchIndexOf(branchRecs[i]));
            Assert.Null(map.BusOf(branchRecs[i]));
        }
        Assert.Null(map.Bus(424242));
        Assert.Null(map.BusOf(Rec(a, CaseRoles.Generators, 0)));
        Assert.Null(map.BranchIndexOf(Rec(a, CaseRoles.Generators, 0)));
    }

    [Fact]
    public void A_case_with_no_layout_draws_the_run_pipelines_automatic_layout_bus_for_bus()
    {
        using EditorTestSupport.TempCopy temp = NoLayoutUk();
        CaseAuthoring a = OpenV1(temp.Dir);
        AuthorMap map = MapEditing.Build(a);
        Assert.True(map.AutoLayout);
        GridModel run = GraphLayout.EnsureLayout(JsonGridLoader.LoadDirectory(temp.Dir));
        Assert.False(GraphLayout.HasNoLayout(run));
        Assert.Equal(run.Buses.Count, map.Buses.Count);
        for (int i = 0; i < run.Buses.Count; i++)
        {
            // Bit for bit: the same layout of the same graph from the same slack.
            Assert.Equal(BitConverter.DoubleToInt64Bits(run.Buses[i].X), BitConverter.DoubleToInt64Bits(map.Buses[i].X));
            Assert.Equal(BitConverter.DoubleToInt64Bits(run.Buses[i].Y), BitConverter.DoubleToInt64Bits(map.Model.Buses[i].Y));
        }
    }

    // =================================================================================================================
    // Place a bus
    // =================================================================================================================

    [Fact]
    public void Placing_a_bus_is_the_engines_add_record_with_the_clicked_coordinates_in_one_undo_step_and_undo_restores_the_bytes()
    {
        using var temp = new EditorTestSupport.TempCopy(EditorTestSupport.CaseDir("uk"));
        using var twin = new EditorTestSupport.TempCopy(EditorTestSupport.CaseDir("uk"));
        Dictionary<string, byte[]> onDisk = Files(temp.Dir);
        CaseAuthoring a = OpenV1(temp.Dir);
        Dictionary<string, byte[]?> before = SessionBytes(a);
        int depth = a.UndoDepth;
        KeyValuePair<string, string>[] values = { Kv("id", "9001"), Kv("baseKv", "132") };
        Assert.Equal(new[] { "id", "baseKv" }, MapEditing.PlaceBusFields(a).Select(f => f.Field));

        AuthoringResult r = MapEditing.PlaceBus(a, values, 0.25, 0.4);
        Assert.True(r.Ok, r.Message);
        Assert.True(r.Changed);
        Assert.Equal(depth + 1, a.UndoDepth);   // one undo step

        // Exactly the engine's Add record with x = 0.25 and y = 1 - 0.4 = 0.6, on a twin of the case.
        CaseAuthoring b = OpenV1(twin.Dir);
        AuthoringResult rb = RecordActions.Add(b, Set(b, CaseRoles.Buses), values.Append(Kv("x", "0.25")).Append(Kv("y", "0.6")).ToList());
        Assert.True(rb.Ok, rb.Message);
        Assert.Equal("Placed at x 0.25, y 0.6: " + rb.Message, r.Message);
        Assert.Equal(rb.Select, r.Select);   // the new record is selected
        AssertSameBytes(SessionBytes(b), a);

        // Lossless: buses.json changes only by the new record after the last one; every other file is as it was.
        byte[] old = before["buses.json"]!, now = a.Session.Doc("buses.json").Text.ToArray();
        JsonSourceText oldText = JsonSourceText.Parse(old, "buses.json", a.Session.Doc("buses.json").KeyComparer);
        int lastEnd = oldText.Root.Elements[^1].Start + oldText.Root.Elements[^1].Length;
        Assert.True(now.AsSpan(0, lastEnd).SequenceEqual(old.AsSpan(0, lastEnd)));
        Assert.True(now.AsSpan(now.Length - (old.Length - lastEnd)).SequenceEqual(old.AsSpan(lastEnd)));
        foreach ((string file, byte[]? bytes) in before.Where(kv => kv.Key != "buses.json"))
            Assert.True(bytes is null ? !a.Session.Doc(file).Exists : a.Session.Doc(file).Text.ToArray().AsSpan().SequenceEqual(bytes), file);
        JsonObject added = JsonNode.Parse(now)!.AsArray()[^1]!.AsObject();
        Assert.Equal(new[] { "id", "baseKv", "x", "y" }, added.Select(kv => kv.Key));
        Assert.Equal("{\"id\":9001,\"baseKv\":132,\"x\":0.25,\"y\":0.6}", added.ToJsonString());

        // The map draws it at the clicked world point.
        AuthorMap map = MapEditing.Build(a);
        AuthorMapBus placed = map.Bus(9001)!;
        Assert.Equal(r.Select, placed.Record);
        var view = new SchematicView();
        view.Load(map.Model);
        SchematicNode node = view.Nodes[view.IdToIndex[9001]];
        Assert.Equal(0.25, node.Wx);
        Assert.Equal(1.0 - 0.6, node.Wy);

        // Undo: the original bytes; redo: the placed bytes again.
        Dictionary<string, byte[]?> placedBytes = SessionBytes(a);
        Assert.True(a.Undo().Ok);
        AssertSameBytes(before, a);
        Assert.False(a.IsDirty);
        Assert.True(a.Redo().Ok);
        AssertSameBytes(placedBytes, a);

        // The loader refuses an island with no slack, so the new bus is connected (a line from bus 101) before the save.
        // Saved, the loader reads the bus where it was placed; nothing else on disk changed.
        Assert.True(MapEditing.Connect(a, MapBranchKind.Line, 101, 9001, new[] { Kv("xPu", "0.1") }).Ok);
        AuthoringReport saved = a.Save();
        Assert.True(saved.Saved, string.Join(Environment.NewLine, saved.Lines()));
        Bus loaded = JsonGridLoader.LoadDirectory(temp.Dir).Buses.Single(x => x.Id == 9001);
        Assert.Equal((0.25, 0.6, 132.0), (loaded.X, loaded.Y, loaded.BaseKv));
        foreach ((string file, byte[] bytes) in onDisk.Where(kv => kv.Key is not ("buses.json" or "transmission.json")))
            Assert.True(File.ReadAllBytes(Path.Combine(temp.Dir, file)).AsSpan().SequenceEqual(bytes), file);
    }

    [Fact]
    public void A_refused_place_changes_nothing_and_says_why()
    {
        using var temp = new EditorTestSupport.TempCopy(EditorTestSupport.CaseDir("uk"));
        using var twin = new EditorTestSupport.TempCopy(EditorTestSupport.CaseDir("uk"));
        CaseAuthoring a = OpenV1(temp.Dir);
        Dictionary<string, byte[]?> before = SessionBytes(a);

        // A colliding id: the engine's own refusal, word for word (as Add record refuses it), nothing changed.
        CaseAuthoring b = OpenV1(twin.Dir);
        AuthoringResult expected = RecordActions.Add(b, Set(b, CaseRoles.Buses), new[] { Kv("id", "1"), Kv("baseKv", "132"), Kv("x", "0.5"), Kv("y", "0.5") });
        Assert.False(expected.Ok);
        AuthoringResult r = MapEditing.PlaceBus(a, new[] { Kv("id", "1"), Kv("baseKv", "132") }, 0.5, 0.5);
        Assert.False(r.Ok);
        Assert.Equal(expected.Message, r.Message);
        // A missing required field (base kV).
        AuthoringResult missing = MapEditing.PlaceBus(a, new[] { Kv("id", "9002") }, 0.5, 0.5);
        Assert.False(missing.Ok);
        Assert.StartsWith("A new bus needs: ", missing.Message, StringComparison.Ordinal);
        // x or y typed into the prompt: they are the clicked point.
        AuthoringResult typed = MapEditing.PlaceBus(a, new[] { Kv("id", "9003"), Kv("baseKv", "11"), Kv("x", "0.1") }, 0.5, 0.5);
        Assert.Equal("Place bus refused: x and y are the clicked point, so they are not typed in the prompt.", typed.Message);
        AssertSameBytes(before, a);
        Assert.Equal(0, a.UndoDepth);
        Assert.False(a.IsDirty);
    }

    [Fact]
    public void Placing_a_bus_in_a_case_with_no_layout_writes_the_automatic_layout_of_the_other_buses_in_the_same_undo_step()
    {
        using EditorTestSupport.TempCopy temp = NoLayoutUk();
        CaseAuthoring a = OpenV1(temp.Dir);
        AuthorMap shown = MapEditing.Build(a);
        Assert.True(shown.AutoLayout);
        Dictionary<string, byte[]?> before = SessionBytes(a);
        int revision = (int)a.Revision;

        AuthoringResult r = MapEditing.PlaceBus(a, new[] { Kv("id", "9001"), Kv("baseKv", "132") }, 0.25, 0.4);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(1, a.UndoDepth);   // ONE undo step for the add and the layout
        Assert.Equal(revision + 1, a.Revision);   // one change, one Changed event
        Assert.Contains("The case had no layout coordinates, so the map's automatic layout of the other 20 bus(es) was written", r.Message, StringComparison.Ordinal);
        Assert.Equal(Set(a, CaseRoles.Buses).Count - 1, r.Select!.Index);   // the new bus is selected

        // Every old bus now holds the position the map showed (rounded to six places), the new one the clicked point;
        // the JSON is the old JSON with x and y added to each bus (nothing else changed), and every other file is as it was.
        JsonArray now = JsonNode.Parse(a.Session.Doc("buses.json").Text.ToArray())!.AsArray();
        JsonArray old = JsonNode.Parse(before["buses.json"]!)!.AsArray();
        Assert.Equal(old.Count + 1, now.Count);
        for (int i = 0; i < old.Count; i++)
        {
            JsonObject o = now[i]!.AsObject();
            Assert.Equal(MapEditing.Format(shown.Buses[i].X), o["x"]!.ToJsonString());
            Assert.Equal(MapEditing.Format(shown.Buses[i].Y), o["y"]!.ToJsonString());
            JsonObject without = (JsonObject)o.DeepClone();
            without.Remove("x");
            without.Remove("y");
            Assert.True(JsonNode.DeepEquals(old[i], without), "bus " + i.ToString(Inv) + " changed beyond x and y");
        }
        Assert.Equal("{\"id\":9001,\"baseKv\":132,\"x\":0.25,\"y\":0.6}", now[^1]!.ToJsonString());
        foreach ((string file, byte[]? bytes) in before.Where(kv => kv.Key != "buses.json"))
            Assert.True(bytes is null ? !a.Session.Doc(file).Exists : a.Session.Doc(file).Text.ToArray().AsSpan().SequenceEqual(bytes), file);

        // The map is unchanged for the old buses (to six places) and has the new one where it was clicked.
        AuthorMap after = MapEditing.Build(a);
        Assert.False(after.AutoLayout);
        for (int i = 0; i < shown.Buses.Count; i++)
        {
            Assert.Equal(shown.Buses[i].X, after.Buses[i].X, 6);
            Assert.Equal(shown.Buses[i].Y, after.Buses[i].Y, 6);
        }

        // One undo gives the original bytes back exactly; redo gives the placed bytes back exactly.
        Dictionary<string, byte[]?> placed = SessionBytes(a);
        Assert.True(a.Undo().Ok);
        AssertSameBytes(before, a);
        Assert.False(a.CanUndo);
        Assert.True(a.Redo().Ok);
        AssertSameBytes(placed, a);
    }

    // =================================================================================================================
    // Connect
    // =================================================================================================================

    [Fact]
    public void Connect_adds_a_line_or_a_transformer_record_from_the_first_bus_to_the_second_in_one_undo_step()
    {
        using var temp = new EditorTestSupport.TempCopy(EditorTestSupport.CaseDir("uk"));
        CaseAuthoring a = OpenV1(temp.Dir);
        Dictionary<string, byte[]?> before = SessionBytes(a);
        // v1: only from and to are required, and they are the two clicks: nothing more is asked.
        Assert.Empty(MapEditing.ConnectFields(a, MapBranchKind.Line, 1));
        Assert.Empty(MapEditing.ConnectFields(a, MapBranchKind.Transformer, 3));
        int lines = Set(a, CaseRoles.Lines).Count, txs = Set(a, CaseRoles.Transformers).Count, branches = MapEditing.Build(a).Branches.Count;

        AuthoringResult line = MapEditing.Connect(a, MapBranchKind.Line, 1, 3, Array.Empty<KeyValuePair<string, string>>());
        Assert.True(line.Ok, line.Message);
        Assert.Equal(1, a.UndoDepth);
        Assert.StartsWith("Connected bus 1 to bus 3 with a new line: Added line ", line.Message, StringComparison.Ordinal);
        Assert.Equal(lines + 1, Set(a, CaseRoles.Lines).Count);
        Assert.Equal("{\"from\":1,\"to\":3}", JsonNode.Parse(a.Session.Doc("transmission.json").Text.ToArray())!.AsArray()[^1]!.ToJsonString());
        AuthoringResult tx = MapEditing.Connect(a, MapBranchKind.Transformer, 3, 103, Array.Empty<KeyValuePair<string, string>>());
        Assert.True(tx.Ok, tx.Message);
        Assert.Equal(2, a.UndoDepth);
        Assert.Equal(txs + 1, Set(a, CaseRoles.Transformers).Count);
        Assert.Equal("{\"from\":3,\"to\":103}", JsonNode.Parse(a.Session.Doc("transformer.json").Text.ToArray())!.AsArray()[^1]!.ToJsonString());

        // The map draws both, as their records, and the new records are what the results select.
        AuthorMap map = MapEditing.Build(a);
        Assert.Equal(branches + 2, map.Branches.Count);
        AuthorMapBranch newLine = map.Branches[map.BranchIndexOf(line.Select!)!.Value];
        Assert.Equal((1, 3, false), (newLine.From, newLine.To, newLine.IsTransformer));
        AuthorMapBranch newTx = map.Branches[map.BranchIndexOf(tx.Select!)!.Value];
        Assert.Equal((3, 103, true), (newTx.From, newTx.To, newTx.IsTransformer));

        // Refusals change nothing: the same bus twice, a bus the case does not have, from or to typed in.
        Dictionary<string, byte[]?> connected = SessionBytes(a);
        Assert.Equal("Connect refused: a line joins two different buses; both clicks were on bus 2.",
            MapEditing.Connect(a, MapBranchKind.Line, 2, 2, Array.Empty<KeyValuePair<string, string>>()).Message);
        Assert.Equal("Connect refused: bus 424242 is not a bus record of this case.",
            MapEditing.Connect(a, MapBranchKind.Transformer, 1, 424242, Array.Empty<KeyValuePair<string, string>>()).Message);
        Assert.Equal("Connect refused: from and to are the clicked buses, so they are not typed in the prompt.",
            MapEditing.Connect(a, MapBranchKind.Line, 1, 2, new[] { Kv("to", "5") }).Message);
        AssertSameBytes(connected, a);
        Assert.Equal(2, a.UndoDepth);

        // Undo both: the original bytes.
        Assert.True(a.Undo().Ok);
        Assert.True(a.Undo().Ok);
        AssertSameBytes(before, a);
    }

    [Fact]
    public void Connect_on_a_v2_case_asks_for_name_rPu_and_xPu_and_the_engine_refuses_a_missing_one()
    {
        using var f = new UiOverlayFixture(withOverlay: false);
        CaseAuthoring a = OpenV2(f.ManifestPath);
        Assert.Equal(new[] { "name", "rPu", "xPu" }, MapEditing.ConnectFields(a, MapBranchKind.Line, 1).Select(x => x.Field));
        Assert.Equal(new[] { "name", "rPu", "xPu" }, MapEditing.ConnectFields(a, MapBranchKind.Transformer, 1).Select(x => x.Field));
        Dictionary<string, byte[]?> before = SessionBytes(a);

        AuthoringResult missing = MapEditing.Connect(a, MapBranchKind.Line, 1, 3, new[] { Kv("name", "L-new"), Kv("rPu", "0.01") });
        Assert.False(missing.Ok);
        Assert.StartsWith("A new line needs: ", missing.Message, StringComparison.Ordinal);
        AssertSameBytes(before, a);

        AuthoringResult ok = MapEditing.Connect(a, MapBranchKind.Transformer, 3, 103, new[] { Kv("name", "T-new"), Kv("rPu", "0.002"), Kv("xPu", "0.06") });
        Assert.True(ok.Ok, ok.Message);
        Assert.Equal(1, a.UndoDepth);
        CaseRef made = ok.Select!;
        Assert.Equal(CaseRoles.Transformers, a.RoleOf(made));
        JsonObject rec = JsonNode.Parse(a.RawText(made))!.AsObject();
        Assert.Equal("{\"name\":\"T-new\",\"from\":3,\"to\":103,\"rPu\":0.002,\"xPu\":0.06}", rec.ToJsonString());
        Assert.True(a.Check().Passed, string.Join(Environment.NewLine, a.LastCheck!.Lines()));
    }

    // =================================================================================================================
    // Drag
    // =================================================================================================================

    [Fact]
    public void Dragging_a_bus_changes_exactly_its_x_and_y_tokens_in_one_undo_step()
    {
        using var temp = new EditorTestSupport.TempCopy(EditorTestSupport.CaseDir("uk"));
        CaseAuthoring a = OpenV1(temp.Dir);
        Dictionary<string, byte[]?> before = SessionBytes(a);
        CaseRef bus3 = MapEditing.Build(a).Bus(3)!.Record;

        AuthoringResult r = MapEditing.MoveBus(a, 3, 0.5, 0.75);
        Assert.True(r.Ok, r.Message);
        Assert.Equal("Moved bus 3 'North England 400kV' to x 0.5, y 0.25. (Edit > Undo reverses it.)", r.Message);
        Assert.Equal(bus3, r.Select);
        Assert.Equal(1, a.UndoDepth);
        Assert.Equal("move bus 3 'North England 400kV' to x 0.5, y 0.25", a.UndoDescription);

        // Exactly the two value tokens of bus 3 change; every other byte of every file is as it was.
        CaseDocument d = a.Session.Doc("buses.json");
        byte[] expected = Spliced(before["buses.json"]!, "buses.json", d.KeyComparer, bus3.Pointer, ("x", "0.5"), ("y", "0.25"));
        Assert.True(d.Text.ToArray().AsSpan().SequenceEqual(expected));
        foreach ((string file, byte[]? bytes) in before.Where(kv => kv.Key != "buses.json"))
            Assert.True(bytes is null ? !a.Session.Doc(file).Exists : a.Session.Doc(file).Text.ToArray().AsSpan().SequenceEqual(bytes), file);
        Assert.Equal((0.5, 0.25), (MapEditing.Build(a).Bus(3)!.X, MapEditing.Build(a).Bus(3)!.Y));

        // Dropped where it already is: nothing to do, no undo step.
        AuthoringResult same = MapEditing.MoveBus(a, 3, 0.5, 0.75);
        Assert.True(same.Ok);
        Assert.False(same.Changed);
        Assert.Equal(1, a.UndoDepth);
        // A bus the case does not have.
        Assert.Equal("Move refused: bus 424242 is not a bus record of this case.", MapEditing.MoveBus(a, 424242, 0.1, 0.1).Message);

        // Undo: the original bytes; redo: the moved bytes.
        Assert.True(a.Undo().Ok);
        AssertSameBytes(before, a);
        Assert.True(a.Redo().Ok);
        Assert.True(a.Session.Doc("buses.json").Text.ToArray().AsSpan().SequenceEqual(expected));
    }

    [Fact]
    public void Dragging_a_bus_of_a_case_with_no_layout_inserts_x_and_y_into_every_bus_in_one_undo_step()
    {
        using EditorTestSupport.TempCopy temp = NoLayoutUk();
        CaseAuthoring a = OpenV1(temp.Dir);
        AuthorMap shown = MapEditing.Build(a);
        Dictionary<string, byte[]?> before = SessionBytes(a);

        AuthoringResult r = MapEditing.MoveBus(a, 3, 0.5, 0.75);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(1, a.UndoDepth);
        Assert.Contains("The case had no layout coordinates, so the map's automatic layout of the other 19 bus(es) was written", r.Message, StringComparison.Ordinal);
        JsonArray now = JsonNode.Parse(a.Session.Doc("buses.json").Text.ToArray())!.AsArray();
        JsonArray old = JsonNode.Parse(before["buses.json"]!)!.AsArray();
        for (int i = 0; i < old.Count; i++)
        {
            JsonObject o = now[i]!.AsObject();
            // Inserted at the schema's place: after baseKv, x before y.
            List<string> keys = o.Select(kv => kv.Key).ToList();
            Assert.Equal(keys.IndexOf("baseKv") + 1, keys.IndexOf("x"));
            Assert.Equal(keys.IndexOf("x") + 1, keys.IndexOf("y"));
            bool moved = shown.Buses[i].Id == 3;
            Assert.Equal(moved ? "0.5" : MapEditing.Format(shown.Buses[i].X), o["x"]!.ToJsonString());
            Assert.Equal(moved ? "0.25" : MapEditing.Format(shown.Buses[i].Y), o["y"]!.ToJsonString());
            JsonObject without = (JsonObject)o.DeepClone();
            without.Remove("x");
            without.Remove("y");
            Assert.True(JsonNode.DeepEquals(old[i], without));
        }
        Assert.True(a.Check().Passed, string.Join(Environment.NewLine, a.LastCheck!.Lines()));
        Assert.True(a.Undo().Ok);
        AssertSameBytes(before, a);
    }

    // =================================================================================================================
    // Delete, and the joined undo step
    // =================================================================================================================

    [Fact]
    public void Deleting_a_bus_with_referrers_is_refused_with_the_engines_exact_text_and_changes_nothing()
    {
        using var temp = new EditorTestSupport.TempCopy(EditorTestSupport.CaseDir("uk"));
        CaseAuthoring a = OpenV1(temp.Dir);
        var author = new AssetAuthorModel(a);
        Dictionary<string, byte[]?> before = SessionBytes(a);
        CaseRef bus1 = MapEditing.Build(a).Bus(1)!.Record;

        // The engine's refusal text, made here from its parts: the referrers of bus 1 and how a refusal lists them.
        CaseSession s = a.Session;
        CaseDocument d = s.Documents[bus1.Document];
        IReadOnlyList<ReferenceIndex.Referrer> refs = CaseOps.ReferrersOf(s, bus1.ToRecordRef());
        Assert.NotEmpty(refs);
        string what = RecordRead.Describe(d, d.Text.Resolve(bus1.Pointer)!, bus1.Index);
        string expected = $"Delete refused: {refs.Count} record(s) refer to {what}: {CaseOps.ListReferrers(refs)}";

        Assert.True(author.SelectRecord(bus1));
        AuthoringResult r = author.DeleteSelected();   // what the map's Delete runs
        Assert.False(r.Ok);
        Assert.Equal(expected, r.Message);
        Assert.Equal(expected, author.Status);
        Assert.True(author.StatusIsError);
        AssertSameBytes(before, a);
        Assert.Equal(0, a.UndoDepth);
        Assert.Same(author.NodeOf(bus1), author.Selected);   // the selection stays on the bus

        // A placed bus nothing refers to deletes, in one undo step, and undo puts it back.
        AuthoringResult placed = author.Report(MapEditing.PlaceBus(a, new[] { Kv("id", "9001"), Kv("baseKv", "132") }, 0.3, 0.3));
        Assert.True(placed.Ok, placed.Message);
        Assert.Equal(placed.Select, author.Selected!.Record);   // Report: the selection follows the new record
        Dictionary<string, byte[]?> withBus = SessionBytes(a);
        AuthoringResult del = author.DeleteSelected();
        Assert.True(del.Ok, del.Message);
        Assert.Null(MapEditing.Build(a).Bus(9001));
        Assert.Equal(2, a.UndoDepth);
        Assert.True(a.Undo().Ok);
        AssertSameBytes(withBus, a);
    }

    [Fact]
    public void The_joined_undo_step_undoes_the_earlier_steps_when_a_later_one_is_refused()
    {
        using var temp = new EditorTestSupport.TempCopy(EditorTestSupport.CaseDir("uk"));
        CaseAuthoring a = OpenV1(temp.Dir);
        Dictionary<string, byte[]?> before = SessionBytes(a);
        AuthoringRecordSet buses = Set(a, CaseRoles.Buses);
        Func<CaseSession, OpResult> add = s => CaseOps.AddRecord(s, buses.Document, buses.ArrayPointer, int.MaxValue, new[] { Kv("id", "9001"), Kv("baseKv", "132") });

        // The second add collides with the first: refused, the first undone, the bytes as before.
        AuthoringResult r = a.ApplyAsOne("two adds", new[] { add, add });
        Assert.False(r.Ok);
        // Test changed 2026-10-24 (agreed by Mark): the refused group now puts the undo and redo history back exactly
        // as it was before the group, so its undone step is no longer left on the redo list (a Redo would have replayed
        // half of a refused group). The history was empty before the group, so it is empty after it.
        Assert.EndsWith("The 1 step(s) already applied for 'two adds' were undone, so the case and its undo and redo history are as before.", r.Message, StringComparison.Ordinal);
        AssertSameBytes(before, a);
        Assert.False(a.CanUndo);
        Assert.False(a.CanRedo);   // the history as before the group: nothing to redo

        // Two steps that both succeed are one undo level, described as asked; one undo restores the bytes.
        Func<CaseSession, OpResult> add2 = s => CaseOps.AddRecord(s, buses.Document, buses.ArrayPointer, int.MaxValue, new[] { Kv("id", "9002"), Kv("baseKv", "11") });
        AuthoringResult both = a.ApplyAsOne("two good adds", new[] { add, add2 });
        Assert.True(both.Ok, both.Message);
        Assert.Equal(1, a.UndoDepth);
        Assert.Equal("two good adds", a.UndoDescription);
        Assert.Equal(buses.Count, both.Select!.Index);   // the first operation's record
        Assert.Equal(buses.Count + 2, Set(a, CaseRoles.Buses).Count);
        Assert.True(a.Undo().Ok);
        AssertSameBytes(before, a);
    }

    // =================================================================================================================
    // The commands
    // =================================================================================================================

    private sealed class NoMapActions : IMapAuthorActions, IMapActions
    {
        public AssetAuthorModel? Author => null;
        public MapEditTool Tool { get; set; } = MapEditTool.Select;
        public bool HasMapSelection => false;
        public Task ChooseToolAsync(MapEditTool tool) => Task.CompletedTask;
        public Task DeleteSelectedAsync() => Task.CompletedTask;
        public Task ShowInAssetsAsync() => Task.CompletedTask;
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
        public int? ContextBusId { get; set; }
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
    public void The_map_commands_are_Author_and_desktop_only_with_their_keys_menu_and_map_element_menu_and_clash_with_nothing()
    {
        var r = new CommandRegistry();
        var actions = new NoMapActions();
        // The Run map's keys first (F, M, C, B, G, D, R, A, T, Escape in Run mode), then the Author map's: no clash.
        MapCommands.Register(r, actions);
        IReadOnlyList<AppCommand> map = MapAuthorCommands.Register(r, actions);
        Assert.Equal(new[] { MapAuthorCommands.SelectTool, MapAuthorCommands.PlaceBus, MapAuthorCommands.Connect, MapAuthorCommands.Delete, MapAuthorCommands.ShowInAssets },
            map.Select(c => c.Id));
        Assert.All(map, c => Assert.Equal(CommandModes.Author, c.Modes));
        Assert.All(map, c => Assert.Equal(CommandPlatforms.Desktop, c.Platforms));
        Assert.All(map, c => Assert.False(c.IsEnabled));   // nothing to act on while not authoring

        // Author mode: the Map menu and its keys; Escape is the select tool there (it clears the selection in Run mode).
        MenuNode menu = Assert.Single(r.MenuTree(CommandModes.Author, CommandPlatforms.Desktop));
        Assert.Equal("Map", menu.Title);
        Assert.Equal(new[] { "Select tool", "Place bus", "Connect", "Delete" }, menu.Children.Select(n => n.Title));
        IReadOnlyDictionary<string, AppCommand> keys = r.Gestures(CommandModes.Author, CommandPlatforms.Desktop);
        Assert.Equal(new[] { "Delete", "Escape", "L", "P" }, keys.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(MapAuthorCommands.SelectTool, keys["Escape"].Id);
        Assert.Equal(MapAuthorCommands.PlaceBus, keys["P"].Id);
        Assert.Equal(MapAuthorCommands.Connect, keys["L"].Id);
        Assert.Equal(MapAuthorCommands.Delete, keys["Delete"].Id);
        Assert.Equal(MapCommands.ClearSelection, r.Gestures(CommandModes.Run, CommandPlatforms.Desktop)["Escape"].Id);
        // The tools show which is chosen.
        Assert.True(map[0].IsChecked!());
        actions.Tool = MapEditTool.Connect;
        Assert.Equal(new[] { false, false, true }, map.Take(3).Select(c => c.IsChecked!()));

        // The map element's context menu (Author mode only); the tree's record kinds are not offered these.
        Assert.Equal(new[] { "Delete", "Show in Assets" }, r.ForTarget(CommandTargets.MapElement, CommandModes.Author, CommandPlatforms.Desktop).Select(c => c.Title));
        Assert.Empty(r.ForTarget(CommandTargets.MapElement, CommandModes.Run, CommandPlatforms.Desktop));
        foreach (CommandTargets t in new[] { CommandTargets.Bus, CommandTargets.Branch, CommandTargets.Transformer })
            Assert.DoesNotContain(r.ForTarget(t, CommandModes.Author, CommandPlatforms.Desktop), c => map.Contains(c));
        // Mobile has no Author mode: none of these is offered there.
        Assert.Empty(r.MenuTree(CommandModes.Author, CommandPlatforms.Mobile));
        Assert.DoesNotContain(r.Commands, c => c.IsOfferedIn(CommandModes.Author, CommandPlatforms.Mobile));
    }
}
