using System.Text.Json;
using GridSim.Core.IO.Json.Overlay;
using GridSim.Core.IO.Json.Schema;
using Xunit;
using static GridSim.Tests.V2.V2TestSupport;

namespace GridSim.Tests.V2;

/// <summary>IO-22: a transformer bayed from a section at each voltage does not join those sections.</summary>
public sealed class OverlayTransformerBayTests
{
    private static readonly Lazy<SchemaValidator> Validator = new(() => new SchemaValidator(RealSchemas()));
    private static readonly Lazy<OverlayKindSet> Kinds =
        new(() => OverlayKindSet.LoadDirectory(Path.Combine(KindsDir, OverlayKindSet.FolderName), RealSchemas()));

    private const string Network = """
        {
          "schema": "gridsim-network/2",
          "buses": [ { "id": 1, "baseKv": 400, "type": "slack" }, { "id": 2, "baseKv": 400 }, { "id": 3, "baseKv": 132 } ],
          "generators": [ { "name": "G", "bus": 1 } ],
          "demands": [ { "name": "D", "bus": 3, "pMw": 10 } ],
          "lines": [ { "name": "L", "from": 1, "to": 2, "rPu": 0, "xPu": 0.1 }, { "name": "M", "from": 2, "to": 3, "rPu": 0, "xPu": 0.1 } ],
          "transformers": [ { "name": "T", "from": 2, "to": 3, "rPu": 0, "xPu": 0.1 }, { "name": "U", "from": 2, "to": 3, "rPu": 0, "xPu": 0.1 } ]
        }
        """;

    private const string Sub = """{ "id": "sub", "kind": "substation", "refs": {}, "params": { "name": "Site" } }""";

    private static JsonElement Root(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static string Section(string id, int bus) =>
        $$"""{ "id": "{{id}}", "kind": "busbarSection", "parent": "sub", "refs": { "bus": { "bus": {{bus}} } }, "params": {} }""";

    private static string Coupler(string id, string from, string to, bool open) =>
        $$"""{ "id": "{{id}}", "kind": "switchingDevice", "parent": "sub", "refs": { "from": { "record": "{{from}}" }, "to": { "record": "{{to}}" } }, "params": { "device": "breaker", "normalOpen": {{(open ? "true" : "false")}} } }""";

    private static string Bay(string id, string from, string name) =>
        $$"""{ "id": "{{id}}", "kind": "switchingDevice", "parent": "sub", "refs": { "from": { "record": "{{from}}" }, "transformer": { "transformer": "{{name}}" } }, "params": { "device": "breaker", "normalOpen": false } }""";

    private static SubstationTopology Build(params string[] records)
    {
        var docs = new[] { ("site.assets.json", Root("{ \"schema\": \"gridsim-assets/2\", \"records\": [ " + string.Join(", ", records) + " ] }")) };
        IReadOnlyList<SchemaError> errors = V2AssetChecker.Check(docs, Validator.Value, Kinds.Value,
            AssetOverlayTargets.FromNetworks(new[] { Root(Network) }));
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors.Select(e => e.Message)));
        return SubstationTopology.All(CaseAssetOverlay.FromCheckedDocuments(docs).Records).Single();
    }

    [Fact]
    public void A_transformer_bayed_at_both_voltages_is_not_an_implied_coupling()
    {
        SubstationTopology t = Build(Sub, Section("bbHV", 2), Section("bbLV", 3), Bay("cbH", "bbHV", "T"), Bay("cbL", "bbLV", "T"));

        Assert.Empty(t.Warnings);
        Assert.Equal(2, t.Groups.Count);
    }

    [Fact]
    public void Two_hv_sections_split_by_an_open_coupler_still_warn_when_each_feeds_a_transformer_to_one_lv_section()
    {
        SubstationTopology t = Build(Sub, Section("bbA", 2), Section("bbB", 2), Section("bbLV", 3), Coupler("cpl", "bbA", "bbB", open: true),
            Bay("cbA", "bbA", "T"), Bay("cbB", "bbB", "U"), Bay("cbL1", "bbLV", "T"), Bay("cbL2", "bbLV", "U"));

        Assert.Contains(t.Warnings, w => w.Kind == TopologyWarningKind.ImpliedSplit);
    }
}
