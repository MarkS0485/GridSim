using System.Text.Json;
using GridSim.Core.IO.Json.Overlay;
using GridSim.Core.IO.Json.Schema;
using GridSim.Core.Model;
using GridSim.Core.Model.Substations;
using GridSim.Core.PowerFlow;
using Xunit;
using static GridSim.Tests.Substations.SubstationFixtures;
using static GridSim.Tests.V2.V2TestSupport;

namespace GridSim.Tests.Substations;

/// <summary>RC4 R4-11 part A: the native file (identity round trip, strict reader) and the overlay path (a v2 case whose
/// substation records today only warn now projects to an exact bus split).</summary>
public sealed class SubstationJsonAndOverlayTests
{
    // ------------------------------------------------------------------------------------------------- JSON

    [Fact]
    public void Write_read_write_returns_the_same_bytes_and_the_same_projection()
    {
        foreach (SubstationModel m in new[] { DoubleBusbar(), BreakerAndAHalf(), Ring(), TwoFeed() })
        {
            string once = SubstationJson.Write(m);
            SubstationModel back = SubstationJson.Read(once);
            Assert.Equal(once, SubstationJson.Write(back));
            Assert.Equal(SubstationProjection.ProjectNormal(m).GroupingText(), SubstationProjection.ProjectNormal(back).GroupingText());
            Assert.Equal(SubstationProjection.ProjectNormal(m).Model.Buses, SubstationProjection.ProjectNormal(back).Model.Buses);
            Assert.Equal(m.Terminals(), back.Terminals());
        }
    }

    [Fact]
    public void The_writer_uses_the_overlay_vocabulary()
    {
        string json = SubstationJson.Write(TwoFeed());
        Assert.Contains("\"device\": \"breaker\"", json, StringComparison.Ordinal);
        Assert.Contains("\"normalOpen\": false", json, StringComparison.Ordinal);
        Assert.Contains("\"format\": \"gridsim-substations/1\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void The_reader_is_strict_and_names_the_path()
    {
        string good = SubstationJson.Write(Ring());
        InvalidDataException e1 = Assert.Throws<InvalidDataException>(() => SubstationJson.Read(good.Replace("\"normalOpen\"", "\"normalopen\"")));
        Assert.Contains("unknown member", e1.Message, StringComparison.Ordinal);
        InvalidDataException e2 = Assert.Throws<InvalidDataException>(() => SubstationJson.Read(good.Replace("\"breaker\"", "\"fuse\"")));
        Assert.Contains("device", e2.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => SubstationJson.Read("{ \"format\": \"other/1\" }"));
        Assert.Throws<InvalidDataException>(() => SubstationJson.Read("not json"));
    }

    // ----------------------------------------------------------------------------------------------- overlay

    private static readonly Lazy<SchemaValidator> Validator = new(() => new SchemaValidator(RealSchemas()));

    private static readonly Lazy<OverlayKindSet> Kinds =
        new(() => OverlayKindSet.LoadDirectory(Path.Combine(KindsDir, OverlayKindSet.FolderName), RealSchemas()));

    private const string Network = """
        {
          "schema": "gridsim-network/2",
          "buses": [ { "id": 1, "baseKv": 400, "type": "slack" }, { "id": 2, "baseKv": 400 }, { "id": 3, "baseKv": 400 } ],
          "generators": [ { "name": "G", "bus": 1 } ],
          "demands": [ { "name": "D", "bus": 3, "pMw": 50 } ],
          "lines": [ { "name": "L", "from": 1, "to": 2, "rPu": 0, "xPu": 0.1 }, { "name": "M", "from": 2, "to": 3, "rPu": 0, "xPu": 0.1 },
                     { "name": "N", "from": 1, "to": 3, "rPu": 0, "xPu": 0.2 } ]
        }
        """;

    private static JsonElement Root(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>The case as the overlay's bus-branch model: 1 (slack, G) -L- 2 -M- 3 (50 MW), and 1 -N- 3.</summary>
    private static GridModel Base() => new()
    {
        BaseMva = 100.0,
        Name = "overlay-case",
        Buses = new[]
        {
            new Bus(1, BusType.Slack, BaseKv: 400), new Bus(2, BusType.Pq, BaseKv: 400), new Bus(3, BusType.Pq, 50, 0, BaseKv: 400),
        },
        Generators = new[] { new Generator(1, 0, Name: "G") },
        Branches = new[] { new Branch(1, 2, 0, 0.1, Name: "L"), new Branch(2, 3, 0, 0.1, Name: "M"), new Branch(1, 3, 0, 0.2, Name: "N") },
    };

    private const string Sub = """{ "id": "sub", "kind": "substation", "refs": {}, "params": { "name": "Site" } }""";
    private const string Bb1 = """{ "id": "bb1", "kind": "busbarSection", "parent": "sub", "refs": { "bus": { "bus": 2 } }, "params": {} }""";
    private const string Bb2 = """{ "id": "bb2", "kind": "busbarSection", "parent": "sub", "refs": { "bus": { "bus": 2 } }, "params": {} }""";

    private static string Coupler(bool open) =>
        $$"""{ "id": "cpl", "kind": "switchingDevice", "parent": "sub", "refs": { "from": { "record": "bb1" }, "to": { "record": "bb2" } }, "params": { "device": "breaker", "normalOpen": {{(open ? "true" : "false")}} } }""";

    private static string Bay(string id, string section, string line, bool open = false) =>
        $$"""{ "id": "{{id}}", "kind": "switchingDevice", "parent": "sub", "refs": { "from": { "record": "{{section}}" }, "line": { "line": "{{line}}" } }, "params": { "device": "breaker", "normalOpen": {{(open ? "true" : "false")}} } }""";

    private static CaseAssetOverlay Overlay(params string[] records)
    {
        var docs = new[] { ("site.assets.json", Root("{ \"schema\": \"gridsim-assets/2\", \"records\": [ " + string.Join(", ", records) + " ] }")) };
        IReadOnlyList<SchemaError> errors = V2AssetChecker.Check(docs, Validator.Value, Kinds.Value, AssetOverlayTargets.FromNetworks(new[] { Root(Network) }));
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors.Select(e => e.Pointer + " => " + e.Message)));
        return CaseAssetOverlay.FromCheckedDocuments(docs);
    }

    [Fact]
    public void An_overlay_that_only_warned_now_projects_to_an_exact_split_and_the_warning_is_superseded()
    {
        // Bus 2 is one authored bus, but its sections bb1 (line L) and bb2 (line M) are separated by the OPEN coupler.
        CaseAssetOverlay ov = Overlay(Sub, Bb1, Bb2, Coupler(open: true), Bay("bayL", "bb1", "L"), Bay("bayM", "bb2", "M"));

        // Today's behaviour, unchanged: one implied-split warning, the case solves as written.
        SubstationTopologyWarning warning = Assert.Single(SubstationTopology.WarningsOf(ov));
        Assert.Equal(TopologyWarningKind.ImpliedSplit, warning.Kind);

        // The exact mapping: bb1 keeps bus 2 (with L), bb2 becomes the new bus 4 (with M).
        SubstationOverlayProjectionResult p = SubstationOverlayProjection.Project(Base(), ov.Records);
        OverlayBusSplit split = Assert.Single(p.Splits);
        Assert.Equal(("sub", 2L, 4), (split.Substation, split.AuthoredBus, split.NewBusId));
        Assert.Equal(new[] { "bb2" }, split.Sections);
        Assert.Equal(new[] { warning.Message }, p.SupersededWarnings.Select(w => w.Message));
        Assert.Equal(4, p.Model.Buses.Count);
        Branch m = p.Model.Branches.Single(b => b.Name == "M");
        Assert.Equal((4, 3), (m.From, m.To));
        Branch l = p.Model.Branches.Single(b => b.Name == "L");
        Assert.Equal((1, 2), (l.From, l.To));

        // It solves, and equals the same network written out by hand (bus 2 a stub on L, bus 4 on M).
        var hand = new GridModel
        {
            BaseMva = 100.0,
            Buses = new[]
            {
                new Bus(1, BusType.Slack, BaseKv: 400), new Bus(2, BusType.Pq, BaseKv: 400), new Bus(3, BusType.Pq, 50, 0, BaseKv: 400),
                new Bus(4, BusType.Pq, BaseKv: 400),
            },
            Generators = new[] { new Generator(1, 0, Name: "G") },
            Branches = new[] { new Branch(1, 2, 0, 0.1, Name: "L"), new Branch(4, 3, 0, 0.1, Name: "M"), new Branch(1, 3, 0, 0.2, Name: "N") },
        };
        PowerFlowResult a = new NewtonRaphsonPowerFlow().Solve(p.Model);
        PowerFlowResult b = new NewtonRaphsonPowerFlow().Solve(hand);
        Assert.True(a.Converged && b.Converged);
        Assert.Equal(b.Vm, a.Vm);
        Assert.Equal(b.VaDeg, a.VaDeg);
        Assert.Equal(b.LineFlows, a.LineFlows);
        // All of the 50 MW arrives over N alone (L and M are dead ends), a closed form: the case as written would split it.
        Assert.Equal(50.0, a.LineFlows.Single(f => f.Name == "N").PFromMw, 6);
        PowerFlowResult asWritten = new NewtonRaphsonPowerFlow().Solve(Base());
        Assert.True(asWritten.LineFlows.Single(f => f.Name == "N").PFromMw < 49.0);
    }

    [Fact]
    public void Closing_the_coupler_by_override_gives_no_split_and_the_authored_model_back()
    {
        CaseAssetOverlay ov = Overlay(Sub, Bb1, Bb2, Coupler(open: true), Bay("bayL", "bb1", "L"), Bay("bayM", "bb2", "M"));
        SubstationOverlayProjectionResult p = SubstationOverlayProjection.Project(Base(), ov.Records,
            new Dictionary<string, bool> { ["cpl"] = false });
        Assert.Empty(p.Splits);
        Assert.Empty(p.SupersededWarnings);
        GridModel b = Base();
        Assert.Equal(b.Buses, p.Model.Buses);
        Assert.Equal(b.Branches, p.Model.Branches);
        Assert.Equal(b.AllGenerators, p.Model.AllGenerators);
    }

    [Fact]
    public void An_open_bay_takes_the_circuit_out_of_service_and_an_unknown_device_is_refused()
    {
        CaseAssetOverlay ov = Overlay(Sub, Bb1, Bb2, Coupler(open: false), Bay("bayL", "bb1", "L"), Bay("bayM", "bb2", "M", open: true));
        SubstationOverlayProjectionResult p = SubstationOverlayProjection.Project(Base(), ov.Records);
        Assert.False(p.Model.Branches.Single(x => x.Name == "M").InService);
        Assert.Contains(p.Dispositions, d => d.Contains("every bay is open", StringComparison.Ordinal));
        Assert.Throws<SubstationTopologyException>(() =>
            SubstationOverlayProjection.Project(Base(), ov.Records, new Dictionary<string, bool> { ["nope"] = true }));
    }
}
