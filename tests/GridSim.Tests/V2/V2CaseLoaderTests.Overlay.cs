using System.Text;
using System.Text.Json;
using GridSim.Core.Dynamics;
using GridSim.Core.Frequency;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Json.Overlay;
using GridSim.Core.IO.Json.Schema;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;
using static GridSim.Tests.V2.V2TestSupport;

namespace GridSim.Tests.V2;

/// <summary>
/// Phase 5a (2026-10-20), the asset overlay PIN: an overlay is carried beside the model and never used by the solver.
/// For every pinned v2 case (each bundled case through its v2 projection, the N234 sequence-data case with a line added,
/// and the import / binding / override fixture with a transformer added) a FULL overlay is built from the case's own elements, using
/// every overlay kind (13), every reference type (bus, line, transformer, generator, demand, record), every new unit,
/// both parameter forms, a record's own provenance and note, children under three parent kinds, a child written before its
/// parent and parents and references across two overlay files. Then, with and without the overlay:
/// <list type="bullet">
/// <item>the GridModel is equal in every field, doubles by their bits (the same reflection comparer the v1 / v2 bit
/// identity pins use, so nothing can be skipped by omission);</item>
/// <item>the Newton power flow is equal to the bit;</item>
/// <item>a short asset-aware frequency run (5 s, a loss of 5% of demand at 1 s) is equal in every field, to the bit;</item>
/// <item>the notes are the same plus exactly one: "overlay: 17 records carried, not used by the solver";</item>
/// <item>without the overlay the result carries none (Overlay null), exactly as before Phase 5a.</item>
/// </list>
/// </summary>
public sealed partial class V2CaseLoaderTests
{
    /// <summary>The overlay's two files, as the manifest lists them (one in a subfolder).</summary>
    private const string OverlayFileA = "assets/site.assets.json";
    private const string OverlayFileB = "lines.assets.json";

    /// <summary>The records the full overlay holds (see <see cref="FullOverlay"/>).</summary>
    private const int OverlayRecordCount = 17;

    private static readonly string OverlayNote = $"overlay: {OverlayRecordCount} records carried, not used by the solver";

    [Theory]
    [MemberData(nameof(BundledCases))]
    public void Overlay_pin_a_bundled_case_with_a_full_overlay_gives_the_same_model_solve_and_dynamics_bit_for_bit(string relativeDir)
    {
        string dir = Path.Combine(DataRoot, relativeDir);
        using var plain = new TempFolder();
        using var overlaid = new TempFolder();
        string plainManifest = WriteProjection(dir, plain.Path);
        string overlaidManifest = WriteProjection(dir, overlaid.Path);
        AddOverlay(overlaidManifest);

        AssertOverlayChangesNothing(
            V2CaseLoader.LoadWithNotes(plainManifest, SchemasDir, DataRoot),
            V2CaseLoader.LoadWithNotes(overlaidManifest, SchemasDir, DataRoot),
            "data/" + relativeDir);
    }

    /// <summary>The N234 earth-fault case (every sequence field present) with a third bus and a line added, so the
    /// overlay's line route and line bay have a line to point at (the original has none).</summary>
    private static string N234NetworkWithLine()
        => N234Lf(N234Network)
            .Replace(
                "{ \"id\": 2, \"name\": \"LV\", \"baseKv\": 132 }",
                "{ \"id\": 2, \"name\": \"LV\", \"baseKv\": 132 },\n    { \"id\": 3, \"name\": \"LV2\", \"baseKv\": 132 }", StringComparison.Ordinal)
            .Replace(
                "\"transformers\": [",
                "\"lines\": [ { \"name\": \"L2-3\", \"from\": 2, \"to\": 3, \"rPu\": 0.01, \"xPu\": 0.05, \"ratingMva\": 200 } ],\n  \"transformers\": [", StringComparison.Ordinal);

    [Fact]
    public void Overlay_pin_the_sequence_data_case_with_a_full_overlay_gives_the_same_model_solve_and_dynamics_bit_for_bit()
    {
        string network = N234NetworkWithLine();
        Assert.Contains("L2-3", network, StringComparison.Ordinal);   // the text edits above took
        Assert.Contains("LV2", network, StringComparison.Ordinal);
        Assert.Contains("windings", network, StringComparison.Ordinal);
        using var a = new Fixture(N234Manifest, network);
        using var b = new Fixture(N234Manifest, network);
        AddOverlay(b.ManifestPath);
        AssertOverlayChangesNothing(a.Load(), b.Load(), "N234 earth-fault case");
    }

    /// <summary>The import fixture's network (imports, a binding rule and an override make an asset fleet) with a third
    /// bus and a transformer added, so every overlay reference type has a target.</summary>
    private static string ImportNetworkWithTransformer()
        => Network.Replace(
                "{ \"id\": 2, \"name\": \"South\", \"baseKv\": 400 }",
                "{ \"id\": 2, \"name\": \"South\", \"baseKv\": 400 },\n    { \"id\": 3, \"name\": \"South LV\", \"baseKv\": 132 }", StringComparison.Ordinal)
            .Replace(
                "\"lines\": [",
                "\"transformers\": [ { \"name\": \"T2-3\", \"from\": 2, \"to\": 3, \"rPu\": 0.001, \"xPu\": 0.04, \"ratingMva\": 500 } ],\n  \"lines\": [", StringComparison.Ordinal);

    [Fact]
    public void Overlay_pin_the_import_and_binding_case_with_a_full_overlay_gives_the_same_model_solve_and_dynamics_bit_for_bit()
    {
        string network = ImportNetworkWithTransformer();
        Assert.Contains("T2-3", network, StringComparison.Ordinal);   // the text edits above took
        Assert.Contains("South LV", network, StringComparison.Ordinal);
        using var a = new Fixture(network: network);
        using var b = new Fixture(network: network);
        AddOverlay(b.ManifestPath);
        V2CaseLoadResult plain = a.Load(), overlaid = b.Load();
        Assert.NotNull(plain.Model.AssetDynamics);   // the dynamics run below really has an asset fleet to drive
        AssertOverlayChangesNothing(plain, overlaid, "import fixture");
    }

    [Fact]
    public void Overlay_the_carried_overlay_holds_every_record_as_written()
    {
        using var f = new Fixture(network: ImportNetworkWithTransformer());
        AddOverlay(f.ManifestPath);
        CaseAssetOverlay o = f.Load().Overlay!;

        Assert.Equal(new[] { OverlayFileA, OverlayFileB }, o.Documents);
        Assert.Equal(OverlayRecordCount, o.Records.Count);
        // Document order, file by file: the child busbar section was written before its substation.
        Assert.Equal("ov.bb1", o.Records[0].Id);
        Assert.Equal("ov.sub", o.Records[1].Id);
        Assert.Equal(new[] { "ov.bb1", "ov.bb2", "ov.cb", "ov.ds", "ov.es", "ov.earth", "ov.t3w" }, o.ChildrenOf("ov.sub").Select(r => r.Id).ToArray());
        Assert.Equal(new[] { "ov.l6.a", "ov.l6.e" }, o.ChildrenOf("ov.l6").Select(r => r.Id).ToArray());

        AssetOverlayRecord cb = o.Find("ov.cb")!;
        Assert.Equal("switchingDevice", cb.Kind);
        Assert.Equal(OverlayFileA, cb.Document);
        Assert.Equal("/records/3", cb.Pointer);
        Assert.Equal(new[] { "from", "to", "line", "transformer", "generator", "demand" }, cb.Refs.Select(r => r.Role).ToArray());
        Assert.Equal(new[] { "record 'ov.bb1'", "record 'ov.bb2'", "line 'L1-2'", "transformer 'T2-3'", "generator 'Slack'", "demand 'Load'" },
            cb.Refs.Select(r => r.Display).ToArray());
        Assert.Equal("breaker", cb.Params.GetProperty("device").GetString());

        AssetOverlayRecord sub = o.Find("ov.sub")!;
        Assert.Null(sub.Parent);
        Assert.Equal("a substation note", sub.Note);
        Assert.Equal("Synthetic", sub.Prov!.Value.GetProperty("tag").GetString());
        Assert.Equal(1, o.Find("ov.bb1")!.Refs.Single().Bus);
        Assert.Equal(4000.0, o.Find("ov.bb1")!.Params.GetProperty("ratedCurrentA").GetProperty("v").GetDouble());
        Assert.Equal("ov.sub", o.Find("ov.earth")!.Parent);
        Assert.Equal("record 'ov.soil'", o.Find("ov.earth")!.Refs.Single(r => r.Role == "soil").Display);
        Assert.Null(o.Find("nowhere"));

        // Every kind and every reference type is in the pinned overlay (so the pins above exercise them all).
        Assert.Equal(13, o.Records.Select(r => r.Kind).Distinct().Count());
        Assert.Equal(Enum.GetValues<AssetRefTarget>().OrderBy(t => t), o.Records.SelectMany(r => r.Refs).Select(r => r.Target).Distinct().OrderBy(t => t));
    }

    [Fact]
    public void Overlay_a_case_without_assets_carries_none_and_adds_no_note()
    {
        using var f = new Fixture(N234Manifest, N234Lf(N234Network));
        V2CaseLoadResult r = f.Load();
        Assert.Null(r.Overlay);
        Assert.Empty(r.Notes);
    }

    [Fact]
    public void Overlay_a_bad_overlay_refuses_the_case_listing_every_problem()
    {
        using var f = new Fixture(N234Manifest, N234Lf(N234Network));
        AddAssetsMember(f.ManifestPath, "bad.assets.json");
        File.WriteAllText(Path.Combine(f.CaseDir, "bad.assets.json"), """
            { "schema": "gridsim-assets/2", "records": [
              { "id": "x", "kind": "earthPath", "refs": { "bus": { "bus": 7 } }, "params": { "resistanceOhm": -1 } },
              { "id": "y", "kind": "lineRoute", "refs": { "line": { "line": "T" } }, "params": {} }
            ] }
            """);
        string why = f.Refusal();
        Assert.Contains("JSON v2 case refused at the schema checks: 3 problem(s):", why, StringComparison.Ordinal);
        Assert.Contains("bad.assets.json: /records/0/params/resistanceOhm: record 'x': -1 ohm is below the minimum 0 for 'resistanceOhm'.", why, StringComparison.Ordinal);
        Assert.Contains("bad.assets.json: /records/0/refs/bus/bus: record 'x': role 'bus' points at bus 7, which is not a bus of the case.", why, StringComparison.Ordinal);
        Assert.Contains("bad.assets.json: /records/1/refs/line/line: record 'y': role 'line' points at line 'T', which is not a line of the case.", why, StringComparison.Ordinal);
    }

    [Fact]
    public void Overlay_a_missing_or_doubly_listed_overlay_file_refuses_the_case()
    {
        using var f = new Fixture(network: ImportNetworkWithTransformer());
        AddOverlay(f.ManifestPath);
        AddAssetsMember(f.ManifestPath, "missing.assets.json", OverlayFileB);
        string why = f.Refusal();
        Assert.Contains("/assets/2: asset overlay file 'missing.assets.json' does not exist", why, StringComparison.Ordinal);
        Assert.Contains("/assets/3: asset overlay file 'lines.assets.json' is listed more than once.", why, StringComparison.Ordinal);
    }

    [Fact]
    public void Overlay_a_file_outside_the_case_folder_is_refused_before_anything_is_read()
    {
        using var f = new Fixture(N234Manifest, N234Lf(N234Network));
        AddAssetsMember(f.ManifestPath, "../outside.assets.json");
        string why = f.Refusal();
        Assert.Contains("refused at the asset overlay file list", why, StringComparison.Ordinal);
        Assert.Contains("/assets/0: asset overlay file '../outside.assets.json' lies outside the case folder", why, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>The pin itself: model, solve, dynamics, notes and the carried overlay.</summary>
    private static void AssertOverlayChangesNothing(V2CaseLoadResult plain, V2CaseLoadResult overlaid, string label)
    {
        // Without an overlay nothing is carried (the pre-5a result); with one, all of it is.
        Assert.Null(plain.Overlay);
        Assert.NotNull(overlaid.Overlay);
        Assert.Equal(OverlayRecordCount, overlaid.Overlay!.Records.Count);
        Assert.Equal(plain.Notes.Append(OverlayNote).ToArray(), overlaid.Notes.ToArray());

        // The model: every field, doubles by their bits.
        List<string> diffs = DeepDifferences(plain.Model, overlaid.Model);
        Assert.True(diffs.Count == 0, $"{label}: the model with the overlay differs from the model without it:{Environment.NewLine}{string.Join(Environment.NewLine, diffs)}");

        // The power flow: the default Newton engine with its default options on each model.
        PowerFlowResult a = SolverSelector.CreateNewton(plain.Model).Solve(plain.Model);
        PowerFlowResult b = SolverSelector.CreateNewton(overlaid.Model).Solve(overlaid.Model);
        AssertSameSolve(a, b, label);

        // A short asset-aware frequency run at the model's own nominal: a loss of 5% of demand at 1 s, 5 s simulated.
        AssetFrequencyResult da = ShortDynamics(plain.Model), db = ShortDynamics(overlaid.Model);
        Assert.NotEmpty(da.Response.Points);   // the run really produced a trace to compare
        // Every field but the trace through the reflection comparer; the trace (whose points carry a DateTime the
        // comparer does not know, and which it rightly refuses to skip) point by point below, to the bit and the tick.
        List<string> ddiffs = DeepDifferences(da with { Response = null! }, db with { Response = null! });
        Assert.True(ddiffs.Count == 0, $"{label}: the frequency run with the overlay differs:{Environment.NewLine}{string.Join(Environment.NewLine, ddiffs)}");
        FrequencyResponse ra = da.Response, rb = db.Response;
        Assert.Equal(Bits(ra.InitialRocofHzPerS), Bits(rb.InitialRocofHzPerS));
        Assert.Equal(Bits(ra.NadirHz), Bits(rb.NadirHz));
        Assert.Equal(Bits(ra.NadirSeconds), Bits(rb.NadirSeconds));
        Assert.Equal(Bits(ra.SettleHz), Bits(rb.SettleHz));
        Assert.Equal(ra.Points.Count, rb.Points.Count);
        for (int i = 0; i < ra.Points.Count; i++)
        {
            FrequencyPoint pa = ra.Points[i], pb = rb.Points[i];
            Assert.True(pa.Time.Ticks == pb.Time.Ticks && pa.Time.Kind == pb.Time.Kind && Bits(pa.Hz) == Bits(pb.Hz) && Bits(pa.RocofHzPerS) == Bits(pb.RocofHzPerS),
                $"{label}: frequency point {i} differs: {pa} vs {pb}");
        }
    }

    private static AssetFrequencyResult ShortDynamics(GridModel m)
    {
        double demandMw = Math.Max(100.0, m.Buses.Sum(b => b.Pd));
        var cfg = new DynamicsConfig { NominalHz = m.FrequencyHz, EnableAssetDynamics = true, DurationSeconds = 5.0 };
        return AssetFrequencyDynamics.Simulate(m, demandMw, new[] { new FrequencyEvent(1.0, -0.05 * demandMw) }, cfg);
    }

    /// <summary>Add a full overlay to the case whose manifest is at <paramref name="manifestPath"/>: the two overlay files
    /// written beside it, built from its own network file(s), and the manifest's "assets" member.</summary>
    private static void AddOverlay(string manifestPath)
    {
        string caseDir = Path.GetDirectoryName(manifestPath)!;
        var networks = new List<JsonElement>();
        using (JsonDocument m = JsonSchemaSubset.ParseFile(manifestPath))
            foreach (JsonElement n in m.RootElement.GetProperty("network").EnumerateArray())
                networks.Add(ReadRoot(Path.Combine(caseDir, n.GetString()!)));
        (string a, string b) = FullOverlay(networks);
        Directory.CreateDirectory(Path.Combine(caseDir, "assets"));
        File.WriteAllText(Path.Combine(caseDir, OverlayFileA.Replace('/', Path.DirectorySeparatorChar)), a);
        File.WriteAllText(Path.Combine(caseDir, OverlayFileB), b);
        AddAssetsMember(manifestPath, OverlayFileA, OverlayFileB);
    }

    /// <summary>Rewrite a manifest with these paths appended to its "assets" member (created when absent); every other
    /// member is copied unchanged.</summary>
    private static void AddAssetsMember(string manifestPath, params string[] paths)
    {
        using JsonDocument m = JsonSchemaSubset.ParseFile(manifestPath);
        var existing = m.RootElement.TryGetProperty("assets", out JsonElement a) ? a.EnumerateArray().Select(x => x.GetString()!).ToList() : new List<string>();
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            foreach (JsonProperty p in m.RootElement.EnumerateObject())
                if (p.Name != "assets") p.WriteTo(w);
            w.WriteStartArray("assets");
            foreach (string s in existing.Concat(paths)) w.WriteStringValue(s);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        File.WriteAllBytes(manifestPath, ms.ToArray());
    }

    /// <summary>
    /// The full overlay for a case, as two files. Targets are taken from the case: its first two buses, a line, up to
    /// three transformers, two generators and a demand, each one whose name no other element of its type shares (a
    /// shared name is ambiguous and would be refused). Seventeen records: every overlay kind, every reference type.
    /// </summary>
    private static (string A, string B) FullOverlay(IReadOnlyList<JsonElement> networks)
    {
        List<long> buses = networks.SelectMany(n => n.TryGetProperty("buses", out JsonElement bs) ? bs.EnumerateArray() : Enumerable.Empty<JsonElement>())
            .Select(b => (long)b.GetProperty("id").GetDouble()).Take(2).ToList();
        Assert.NotEmpty(buses);
        long b1 = buses[0], b2 = buses[^1];
        List<string> Unique(string member, int take)
        {
            List<string> names = networks.SelectMany(n => n.TryGetProperty(member, out JsonElement xs) ? xs.EnumerateArray() : Enumerable.Empty<JsonElement>())
                .Select(x => x.GetProperty("name").GetString()!).ToList();
            List<string> once = names.GroupBy(x => x, StringComparer.Ordinal).Where(g => g.Count() == 1).Select(g => g.Key).Take(take).ToList();
            Assert.True(once.Count > 0, $"the case has no {member} with a name of its own, so the full overlay cannot point at one.");
            while (once.Count < take) once.Add(once[^1]);
            return once;
        }
        string line = Unique("lines", 1)[0];
        List<string> tx = Unique("transformers", 3);
        List<string> gens = Unique("generators", 2);
        string demand = Unique("demands", 1)[0];
        static string Q(string s) => "\"" + JsonEncodedText.Encode(s) + "\"";
        static string N(long x) => x.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // File A: the substation and what sits in it (its first record is a child written before its parent), the
        // HVDC link; its earth path points at a soil zone in file B.
        var a = new StringBuilder();
        a.Append("""{ "schema": "gridsim-assets/2", "description": "Phase 5a pin overlay, file A.", "records": [""").Append('\n');
        a.Append($$"""  { "id": "ov.bb1", "kind": "busbarSection", "parent": "ov.sub", "refs": { "bus": { "bus": {{N(b1)}} } }, "params": { "label": "Main 1", "ratedCurrentA": { "v": 4000, "unit": "A", "prov": { "tag": "Synthetic", "note": "pin fixture" } } } },""").Append('\n');
        a.Append("""  { "id": "ov.sub", "kind": "substation", "refs": {}, "params": { "name": "Pin site", "operator": "Test", "latitudeDeg": 51.5, "longitudeDeg": -0.1 }, "prov": { "tag": "Synthetic", "note": "pin fixture" }, "note": "a substation note" },""").Append('\n');
        a.Append($$"""  { "id": "ov.bb2", "kind": "busbarSection", "parent": "ov.sub", "refs": { "bus": { "bus": {{N(b2)}} } }, "params": {} },""").Append('\n');
        a.Append($$"""  { "id": "ov.cb", "kind": "switchingDevice", "parent": "ov.sub", "refs": { "from": { "record": "ov.bb1" }, "to": { "record": "ov.bb2" }, "line": { "line": {{Q(line)}} }, "transformer": { "transformer": {{Q(tx[0])}} }, "generator": { "generator": {{Q(gens[0])}} }, "demand": { "demand": {{Q(demand)}} } }, "params": { "device": "breaker", "normalOpen": false, "ratedCurrentA": 4000, "breakingCurrentA": 63000 } },""").Append('\n');
        a.Append("""  { "id": "ov.ds", "kind": "switchingDevice", "parent": "ov.sub", "refs": { "from": { "record": "ov.bb1" } }, "params": { "device": "disconnector", "normalOpen": true } },""").Append('\n');
        a.Append("""  { "id": "ov.es", "kind": "switchingDevice", "parent": "ov.sub", "refs": { "from": { "record": "ov.bb2" } }, "params": { "device": "earthSwitch", "normalOpen": true } },""").Append('\n');
        a.Append($$"""  { "id": "ov.earth", "kind": "earthPath", "parent": "ov.sub", "refs": { "bus": { "bus": {{N(b1)}} }, "soil": { "record": "ov.soil" } }, "params": { "resistanceOhm": { "v": 0.5, "unit": "ohm" } } },""").Append('\n');
        a.Append($$"""  { "id": "ov.t3w", "kind": "transformer3W", "parent": "ov.sub", "refs": { "starBus": { "bus": {{N(b2)}} }, "hv": { "transformer": {{Q(tx[0])}} }, "mv": { "transformer": {{Q(tx[1])}} }, "lv": { "transformer": {{Q(tx[2])}} } }, "params": { "baseMva": 100, "hvKv": 400, "mvKv": 132, "lvKv": 13, "r12Pu": 0.002, "x12Pu": 0.1, "r13Pu": 0.003, "x13Pu": 0.2, "r23Pu": 0.002, "x23Pu": 0.15 } },""").Append('\n');
        a.Append($$"""  { "id": "ov.hvdc", "kind": "hvdcLink", "refs": { "sendingConverter": { "generator": {{Q(gens[0])}} }, "receivingConverter": { "generator": {{Q(gens[1])}} } }, "params": { "technology": "vsc", "ratedMw": 1000, "dcVoltageKv": 320, "cableLengthKm": 100 }, "note": "over the authored generators" }""").Append('\n');
        a.Append("] }\n");

        // File B: catalogue items, the route of the line with its span and earth wire, and the soil zone.
        var b = new StringBuilder();
        b.Append("""{ "schema": "gridsim-assets/2", "records": [""").Append('\n');
        b.Append("""  { "id": "ov.zebra", "kind": "conductorType", "refs": {}, "params": { "code": "Zebra", "material": "acsr", "crossSectionMm2": { "v": 428.9, "unit": "mm2" }, "diameterMm": { "v": 28.62, "unit": "mm" }, "dcResistanceOhmPerKm": 0.0674, "ratedCurrentA": 900 } },""").Append('\n');
        b.Append("""  { "id": "ov.l6", "kind": "towerType", "refs": {}, "params": { "code": "L6", "heightM": 50, "circuits": 2 } },""").Append('\n');
        b.Append("""  { "id": "ov.l6.a", "kind": "conductorPosition", "parent": "ov.l6", "refs": {}, "params": { "phase": "a", "circuit": 1, "xM": -6.5, "yM": { "v": 30, "unit": "m" } } },""").Append('\n');
        b.Append("""  { "id": "ov.l6.e", "kind": "conductorPosition", "parent": "ov.l6", "refs": {}, "params": { "phase": "earth", "xM": 0, "yM": 50 } },""").Append('\n');
        b.Append($$"""  { "id": "ov.route", "kind": "lineRoute", "refs": { "line": { "line": {{Q(line)}} } }, "params": { "name": "Pin route", "routeLengthKm": 1.2 } },""").Append('\n');
        b.Append("""  { "id": "ov.span1", "kind": "span", "parent": "ov.route", "refs": { "conductor": { "record": "ov.zebra" }, "tower": { "record": "ov.l6" } }, "params": { "lengthM": 350, "chainageM": 0, "sagM": 9.5, "latitudeDeg": 51.5, "longitudeDeg": -0.1 } },""").Append('\n');
        b.Append("""  { "id": "ov.ew", "kind": "earthWire", "parent": "ov.route", "refs": { "conductor": { "record": "ov.zebra" } }, "params": { "count": 1, "bondedAtBothEnds": true } },""").Append('\n');
        b.Append("""  { "id": "ov.soil", "kind": "soilZone", "refs": {}, "params": { "resistivityOhmM": { "v": 100, "unit": "ohm.m" }, "depthM": 2 } }""").Append('\n');
        b.Append("] }\n");
        return (a.ToString(), b.ToString());
    }
}
