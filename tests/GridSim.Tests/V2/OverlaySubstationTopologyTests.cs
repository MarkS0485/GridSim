using System.Text.Json;
using GridSim.Core.IO.Json.Overlay;
using GridSim.Core.IO.Json.Schema;
using Xunit;
using static GridSim.Tests.V2.V2TestSupport;

namespace GridSim.Tests.V2;

/// <summary>
/// Phase 5c (2026-10-20): <see cref="SubstationTopology"/>, the projection of one substation record's children (busbar
/// sections, switching devices, bays, the nodes under the normal switch states) and the implied split / coupling
/// WARNING, and <see cref="V2AssetChecker.Warnings"/>, the separate warning list beside the checker's errors. Every
/// overlay here first passes <see cref="V2AssetChecker.Check"/> with NO error (a warning never becomes an error), against
/// the real schemas and kind specs and a small case; the warning texts are asserted exactly, and a consistent topology
/// is shown to give none.
/// </summary>
public sealed class OverlaySubstationTopologyTests
{
    private static readonly Lazy<SchemaValidator> Validator = new(() => new SchemaValidator(RealSchemas()));

    private static readonly Lazy<OverlayKindSet> Kinds =
        new(() => OverlayKindSet.LoadDirectory(Path.Combine(KindsDir, OverlayKindSet.FolderName), RealSchemas()));

    /// <summary>The case: three buses, a line L and M, a transformer T, a generator G and a demand D.</summary>
    private const string Network = """
        {
          "schema": "gridsim-network/2",
          "buses": [ { "id": 1, "baseKv": 400, "type": "slack" }, { "id": 2, "baseKv": 400 }, { "id": 3, "baseKv": 132 } ],
          "generators": [ { "name": "G", "bus": 1 } ],
          "demands": [ { "name": "D", "bus": 3, "pMw": 10 } ],
          "lines": [ { "name": "L", "from": 1, "to": 2, "rPu": 0, "xPu": 0.1 }, { "name": "M", "from": 2, "to": 3, "rPu": 0, "xPu": 0.1 } ],
          "transformers": [ { "name": "T", "from": 2, "to": 3, "rPu": 0, "xPu": 0.1 } ]
        }
        """;

    private static JsonElement Root(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static AssetOverlayTargets Targets() => AssetOverlayTargets.FromNetworks(new[] { Root(Network) });

    private const string Sub = """{ "id": "sub", "kind": "substation", "refs": {}, "params": { "name": "Site" } }""";

    private static string Section(string id, int bus, string parent = "sub") =>
        $$"""{ "id": "{{id}}", "kind": "busbarSection", "parent": "{{parent}}", "refs": { "bus": { "bus": {{bus}} } }, "params": {} }""";

    private static string Coupler(string id, string from, string to, bool open, string device = "breaker") =>
        $$"""{ "id": "{{id}}", "kind": "switchingDevice", "parent": "sub", "refs": { "from": { "record": "{{from}}" }, "to": { "record": "{{to}}" } }, "params": { "device": "{{device}}", "normalOpen": {{(open ? "true" : "false")}} } }""";

    private static string Bay(string id, string from, string role, string name, bool open, string device = "breaker") =>
        $$"""{ "id": "{{id}}", "kind": "switchingDevice", "parent": "sub", "refs": { "from": { "record": "{{from}}" }, "{{role}}": { "{{role}}": "{{name}}" } }, "params": { "device": "{{device}}", "normalOpen": {{(open ? "true" : "false")}} } }""";

    private static string Lone(string id, string from, string device, bool open) =>
        $$"""{ "id": "{{id}}", "kind": "switchingDevice", "parent": "sub", "refs": { "from": { "record": "{{from}}" } }, "params": { "device": "{{device}}", "normalOpen": {{(open ? "true" : "false")}} } }""";

    private static IReadOnlyList<(string Name, JsonElement Root)> Docs(params string[] records) =>
        new[] { ("site.assets.json", Root("{ \"schema\": \"gridsim-assets/2\", \"records\": [ " + string.Join(", ", records) + " ] }")) };

    // The overlay, checked with NO error first (so every warning below is on an overlay the loader accepts), then read.
    private static (SubstationTopology Topology, CaseAssetOverlay Overlay, IReadOnlyList<(string, JsonElement)> Docs) Build(params string[] records)
    {
        IReadOnlyList<(string Name, JsonElement Root)> docs = Docs(records);
        IReadOnlyList<SchemaError> errors = V2AssetChecker.Check(docs, Validator.Value, Kinds.Value, Targets());
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors.Select(e => e.Pointer + " => " + e.Message)));
        CaseAssetOverlay ov = CaseAssetOverlay.FromCheckedDocuments(docs);
        return (SubstationTopology.All(ov.Records).Single(), ov, docs);
    }

    private const string Tail = " This is a warning, not an error: the case still loads and solves exactly as written.";

    [Fact]
    public void A_consistent_topology_two_sections_of_one_bus_joined_by_a_closed_coupler_gives_one_node_and_no_warning()
    {
        (SubstationTopology t, CaseAssetOverlay ov, var docs) = Build(Sub, Section("bb1", 1), Section("bb2", 1),
            Coupler("cpl", "bb1", "bb2", open: false), Bay("cbl", "bb1", "line", "L", open: false));

        Assert.Equal("sub", t.Substation);
        Assert.Equal(("site.assets.json", "/records/0"), (t.Document, t.Pointer));
        Assert.Equal(new[] { new TopologySection("bb1", 1, 0), new TopologySection("bb2", 1, 0) }, t.Sections);
        Assert.Equal(new[]
        {
            new TopologyDevice("cpl", TopologyDeviceType.Breaker, false, "bb1", "bb2", null, TopologyDeviceRole.Coupler),
            new TopologyDevice("cbl", TopologyDeviceType.Breaker, false, "bb1", null, new TopologyFeeder(AssetRefTarget.Line, "L"), TopologyDeviceRole.Feeder),
        }, t.Devices);
        TopologyBay bay = Assert.Single(t.Bays);
        Assert.Equal(("cbl", "bb1", "line 'L'", true), (bay.Id, bay.Section, bay.Feeder!.Display, bay.Closed));
        Assert.Equal(new[] { "cbl" }, bay.Devices);
        TopologyGroup g = Assert.Single(t.Groups);
        Assert.Equal(new[] { "bb1", "bb2" }, g.Sections);
        Assert.Equal(new long[] { 1 }, g.Buses);
        Assert.Empty(t.Warnings);
        Assert.Empty(SubstationTopology.WarningsOf(ov));
        Assert.Empty(V2AssetChecker.Warnings(docs, Validator.Value, Kinds.Value, Targets()));
    }

    [Fact]
    public void A_split_one_bus_whose_sections_an_open_coupler_separates_warns_with_the_exact_text()
    {
        (SubstationTopology t, CaseAssetOverlay ov, var docs) = Build(Sub, Section("bb1", 1), Section("bb2", 1), Coupler("cpl", "bb1", "bb2", open: true));

        Assert.Equal(new[] { 0, 1 }, t.Sections.Select(s => s.Group));
        Assert.Equal(2, t.Groups.Count);
        SubstationTopologyWarning w = Assert.Single(t.Warnings);
        Assert.Equal(TopologyWarningKind.ImpliedSplit, w.Kind);
        Assert.Equal(("sub", "site.assets.json", "/records/0"), (w.Substation, w.Document, w.Pointer));
        Assert.Equal(new[] { "bb1", "bb2" }, w.Sections);
        Assert.Equal(new[] { "cpl" }, w.Devices);
        Assert.Equal("substation 'sub': busbar sections 'bb1' and 'bb2' all collapse to bus 1, but under the normal switch states they fall into "
            + "2 separate groups (['bb1'] and ['bb2']); the open device 'cpl' lies between them. The switch states imply a bus split the authored "
            + "buses do not show." + Tail, w.Message);

        // The checker: the same warning in its separate list, and no error (the case still loads).
        Assert.Empty(V2AssetChecker.Check(docs, Validator.Value, Kinds.Value, Targets()));
        // (The warning holds lists, which a record compares by reference: compared here member by member.)
        static string Flat(SubstationTopologyWarning x) =>
            $"{x.Kind}|{x.Substation}|{x.Document}|{x.Pointer}|{string.Join(",", x.Sections)}|{string.Join(",", x.Devices)}|{x.Message}";
        Assert.Equal(new[] { Flat(w) }, V2AssetChecker.Warnings(docs, Validator.Value, Kinds.Value, Targets()).Select(Flat));
        Assert.Equal(new[] { Flat(w) }, SubstationTopology.WarningsOf(ov).Select(Flat));
    }

    [Fact]
    public void A_coupling_sections_of_different_buses_joined_by_a_closed_coupler_warns_with_the_exact_text()
    {
        (SubstationTopology t, _, _) = Build(Sub, Section("bb1", 1), Section("bb2", 2), Coupler("cpl", "bb1", "bb2", open: false));

        TopologyGroup g = Assert.Single(t.Groups);
        Assert.Equal(new long[] { 1, 2 }, g.Buses);
        SubstationTopologyWarning w = Assert.Single(t.Warnings);
        Assert.Equal(TopologyWarningKind.ImpliedCoupling, w.Kind);
        Assert.Equal(new[] { "bb1", "bb2" }, w.Sections);
        Assert.Equal(new[] { "cpl" }, w.Devices);
        Assert.Equal("substation 'sub': busbar sections 'bb1' (bus 1) and 'bb2' (bus 2) collapse to different buses, but under the normal switch "
            + "states the closed device 'cpl' joins them into one node. The switch states imply a coupling the authored case does not model." + Tail, w.Message);

        // The same two sections with the coupler OPEN: two nodes, two buses, consistent: no warning.
        (SubstationTopology open, _, _) = Build(Sub, Section("bb1", 1), Section("bb2", 2), Coupler("cpl", "bb1", "bb2", open: true));
        Assert.Equal(2, open.Groups.Count);
        Assert.Empty(open.Warnings);
    }

    [Fact]
    public void An_earth_switch_never_joins_two_sections_even_when_closed_and_is_a_bay_of_its_own()
    {
        (SubstationTopology t, _, _) = Build(Sub, Section("bb1", 1), Section("bb2", 1),
            Coupler("es", "bb1", "bb2", open: false, device: "earthSwitch"), Lone("es2", "bb2", "earthSwitch", open: true));

        Assert.Equal(new[] { TopologyDeviceRole.Earth, TopologyDeviceRole.Earth }, t.Devices.Select(d => d.Role));
        Assert.Equal(new[] { TopologyDeviceType.EarthSwitch, TopologyDeviceType.EarthSwitch }, t.Devices.Select(d => d.Device));
        Assert.Null(t.Devices[0].To);   // an earth switch closes onto earth: its "to" joins nothing
        Assert.Equal(new[] { ("es", "bb1"), ("es2", "bb2") }, t.Bays.Select(b => (b.Id, b.Section)));
        Assert.All(t.Bays, b => Assert.Null(b.Feeder));
        Assert.Equal(2, t.Groups.Count);
        SubstationTopologyWarning w = Assert.Single(t.Warnings);
        Assert.Empty(w.Devices);
        Assert.Equal("substation 'sub': busbar sections 'bb1' and 'bb2' all collapse to bus 1, but under the normal switch states they fall into "
            + "2 separate groups (['bb1'] and ['bb2']); no switching device joins them. The switch states imply a bus split the authored buses do "
            + "not show." + Tail, w.Message);
    }

    [Fact]
    public void A_multi_bay_double_busbar_joins_its_sections_through_a_circuit_reached_from_both_and_splits_when_a_selector_opens()
    {
        // Line L is selected onto both sections: bb1 through a disconnector and a breaker in series (one bay), bb2 through
        // a disconnector; a transformer, generator and demand bay; a spare disconnector. With both selectors closed, the
        // two sections are one node through L.
        string[] records =
        {
            Sub, Section("bb1", 1), Section("bb2", 1),
            Bay("ds1", "bb1", "line", "L", open: false, device: "disconnector"),
            Bay("cb1", "bb1", "line", "L", open: false),
            Bay("ds2", "bb2", "line", "L", open: false, device: "disconnector"),
            Bay("cbt", "bb1", "transformer", "T", open: false),
            Bay("cbg", "bb2", "generator", "G", open: false),
            Bay("cbd", "bb2", "demand", "D", open: true),
            Lone("sp", "bb1", "disconnector", open: true),
        };
        (SubstationTopology t, _, _) = Build(records);
        Assert.Equal(new[]
        {
            ("ds1", "bb1", "line 'L'", 2, true),
            ("ds2", "bb2", "line 'L'", 1, true),
            ("cbt", "bb1", "transformer 'T'", 1, true),
            ("cbg", "bb2", "generator 'G'", 1, true),
            ("cbd", "bb2", "demand 'D'", 1, false),
            ("sp", "bb1", "", 1, false),
        }, t.Bays.Select(b => (b.Id, b.Section, b.Feeder?.Display ?? "", b.Devices.Count, b.Closed)));
        Assert.Equal(new[] { "ds1", "cb1" }, t.Bays[0].Devices);
        Assert.Equal(TopologyDeviceRole.Spare, t.Device("sp")!.Role);
        Assert.Equal(new[] { "bb1", "bb2" }, Assert.Single(t.Groups).Sections);
        Assert.Empty(t.Warnings);

        // Open the bb2 selector: the sections fall apart, and the open selector is the device between them.
        records[5] = Bay("ds2", "bb2", "line", "L", open: true, device: "disconnector");
        (SubstationTopology split, _, _) = Build(records);
        Assert.Equal(2, split.Groups.Count);
        SubstationTopologyWarning w = Assert.Single(split.Warnings);
        Assert.Equal(new[] { "ds2" }, w.Devices);
        Assert.Equal("substation 'sub': busbar sections 'bb1' and 'bb2' all collapse to bus 1, but under the normal switch states they fall into "
            + "2 separate groups (['bb1'] and ['bb2']); the open device 'ds2' lies between them. The switch states imply a bus split the authored "
            + "buses do not show." + Tail, w.Message);

        // Selected onto sections of DIFFERENT buses through closed bays: a coupling, naming every device of those bays.
        records[2] = Section("bb2", 2);
        records[5] = Bay("ds2", "bb2", "line", "L", open: false, device: "disconnector");
        (SubstationTopology coupled, _, _) = Build(records);
        SubstationTopologyWarning c = Assert.Single(coupled.Warnings);
        Assert.Equal(TopologyWarningKind.ImpliedCoupling, c.Kind);
        Assert.Equal(new[] { "ds1", "cb1", "ds2" }, c.Devices);
        Assert.Equal("substation 'sub': busbar sections 'bb1' (bus 1) and 'bb2' (bus 2) collapse to different buses, but under the normal switch "
            + "states the closed devices 'ds1', 'cb1' and 'ds2' join them into one node. The switch states imply a coupling the authored case "
            + "does not model." + Tail, c.Message);
    }

    [Fact]
    public void A_split_and_a_coupling_in_one_substation_give_both_warnings_split_first()
    {
        (SubstationTopology t, _, _) = Build(Sub, Section("bb1", 1), Section("bb2", 1), Section("bb3", 2),
            Coupler("c12", "bb1", "bb2", open: true), Coupler("c23", "bb2", "bb3", open: false));
        Assert.Equal(new[] { new[] { "bb1" }, new[] { "bb2", "bb3" } }, t.Groups.Select(g => g.Sections.ToArray()));
        Assert.Equal(new[]
        {
            "substation 'sub': busbar sections 'bb1' and 'bb2' all collapse to bus 1, but under the normal switch states they fall into 2 separate "
            + "groups (['bb1'] and ['bb2']); the open device 'c12' lies between them. The switch states imply a bus split the authored buses do "
            + "not show." + Tail,
            "substation 'sub': busbar sections 'bb2' (bus 1) and 'bb3' (bus 2) collapse to different buses, but under the normal switch states the "
            + "closed device 'c23' joins them into one node. The switch states imply a coupling the authored case does not model." + Tail,
        }, t.Warnings.Select(w => w.Message));
        Assert.Equal(new[] { TopologyWarningKind.ImpliedSplit, TopologyWarningKind.ImpliedCoupling }, t.Warnings.Select(w => w.Kind));
    }

    [Fact]
    public void The_checker_errors_are_exactly_what_they_were_and_a_set_with_errors_has_no_warnings()
    {
        // A split overlay plus one bad record: Check lists exactly the bad record's problem (the split is not an error),
        // and Warnings gives nothing for a set that fails Check.
        string bad = """{ "id": "bad", "kind": "busbarSection", "parent": "sub", "refs": { "bus": { "bus": 99 } }, "params": {} }""";
        IReadOnlyList<(string Name, JsonElement Root)> docs = Docs(Sub, Section("bb1", 1), Section("bb2", 1), Coupler("cpl", "bb1", "bb2", open: true), bad);
        IReadOnlyList<SchemaError> errors = V2AssetChecker.Check(docs, Validator.Value, Kinds.Value, Targets());
        SchemaError e = Assert.Single(errors);
        Assert.Equal(("/records/4/refs/bus/bus", "record 'bad': role 'bus' points at bus 99, which is not a bus of the case."), (e.Pointer, e.Message));
        Assert.Empty(V2AssetChecker.Warnings(docs, Validator.Value, Kinds.Value, Targets()));
    }

    [Fact]
    public void Devices_outside_the_substation_and_records_of_other_kinds_are_placed_as_the_rules_say()
    {
        // A second substation's section; a device of 'sub' whose from is that other section (unplaced); a coupler whose
        // "to" is the other substation's section (not a coupler here: a spare bay on its own section).
        string sub2 = """{ "id": "sub2", "kind": "substation", "refs": {}, "params": {} }""";
        IReadOnlyList<(string Name, JsonElement Root)> docs = Docs(Sub, sub2, Section("bb1", 1), Section("x1", 1, parent: "sub2"),
            Lone("away", "x1", "breaker", open: false), Coupler("cross", "bb1", "x1", open: false));
        Assert.Empty(V2AssetChecker.Check(docs, Validator.Value, Kinds.Value, Targets()));
        IReadOnlyList<SubstationTopology> all = SubstationTopology.All(CaseAssetOverlay.FromCheckedDocuments(docs).Records);
        Assert.Equal(new[] { "sub", "sub2" }, all.Select(t => t.Substation));
        SubstationTopology t = all[0];
        Assert.Equal(new[] { "bb1" }, t.Sections.Select(s => s.Id));
        Assert.Equal(new[] { ("away", TopologyDeviceRole.Unplaced), ("cross", TopologyDeviceRole.Spare) }, t.Devices.Select(d => (d.Id, d.Role)));
        Assert.Equal(new[] { "cross" }, t.Bays.Select(b => b.Id));
        Assert.Empty(t.Warnings);
        Assert.Empty(all[1].Devices);   // the devices are children of 'sub', not of 'sub2'
        Assert.Throws<ArgumentException>(() => SubstationTopology.Build(CaseAssetOverlay.FromCheckedDocuments(docs).Records[2], Array.Empty<AssetOverlayRecord>()));
    }

    [Fact]
    public void The_projection_is_deterministic_the_same_records_give_equal_values_every_time()
    {
        string[] records = { Sub, Section("bb1", 1), Section("bb2", 1), Section("bb3", 2), Coupler("c12", "bb1", "bb2", open: true), Coupler("c23", "bb2", "bb3", open: false) };
        (SubstationTopology a, _, _) = Build(records);
        (SubstationTopology b, _, _) = Build(records);
        Assert.Equal(a.Sections, b.Sections);
        Assert.Equal(a.Devices, b.Devices);
        Assert.Equal(a.Bays.Select(x => (x.Id, x.Section, x.Feeder, string.Join(",", x.Devices), x.Closed)), b.Bays.Select(x => (x.Id, x.Section, x.Feeder, string.Join(",", x.Devices), x.Closed)));
        Assert.Equal(a.Groups.Select(g => string.Join(",", g.Sections) + "|" + string.Join(",", g.Buses)), b.Groups.Select(g => string.Join(",", g.Sections) + "|" + string.Join(",", g.Buses)));
        Assert.Equal(a.Warnings.Select(w => w.Message), b.Warnings.Select(w => w.Message));
    }
}
