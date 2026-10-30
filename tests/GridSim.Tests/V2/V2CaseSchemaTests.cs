using System.Text.Json;
using GridSim.Core.IO.Json.Schema;
using GridSim.Core.Model;
using Xunit;
using static GridSim.Tests.V2.V2TestSupport;

namespace GridSim.Tests.V2;

/// <summary>
/// The v2 case manifest (case.schema.json, gridsim-case/2) and network file (network.schema.json,
/// gridsim-network/2) schemas, on hand-written fixtures under tests/GridSim.Tests/fixtures/v2/cases and
/// fixtures/v2/networks. Each invalid fixture is good.*.json with exactly one thing wrong, so a failure here names
/// one rule. Each invalid fixture must fail with an error at the expected JSON pointer, not merely fail somewhere:
/// a fixture that fails for an unintended reason would otherwise hide the rule it was written to test.
/// </summary>
public class V2CaseSchemaTests
{
    private static readonly Lazy<SchemaValidator> Validator = new(() => new SchemaValidator(RealSchemas()));

    private static IReadOnlyList<SchemaError> ValidateFixture(string folder, string file, string schema)
    {
        using JsonDocument doc = JsonSchemaSubset.ParseFile(Path.Combine(FixturesDir, folder, file));
        return Validator.Value.Validate(doc.RootElement, schema);
    }

    // ----- case manifest -----

    [Fact]
    public void The_schema_set_includes_the_case_and_network_schemas()
    {
        JsonSchemaSubset set = RealSchemas();
        Assert.Contains("case.schema.json", set.Files);
        Assert.Contains("network.schema.json", set.Files);
    }

    [Fact]
    public void A_complete_good_case_passes()
    {
        IReadOnlyList<SchemaError> errors = ValidateFixture("cases", "good.case.json", "case.schema.json");
        Assert.True(errors.Count == 0, Dump(errors));
    }

    [Theory]
    [InlineData("override-without-reason.case.json", "/overrides/0", "reason")]
    [InlineData("override-without-shadow.case.json", "/overrides/0", "shadow")]
    [InlineData("override-shadow-not-explicit.case.json", "/overrides/0/shadow", "explicit")]
    [InlineData("override-blank-reason.case.json", "/overrides/0/reason", "pattern")]
    [InlineData("unknown-member.case.json", "/notes", "unknown member")]
    [InlineData("unknown-member-in-system.case.json", "/system/baseKV", "unknown member")]
    [InlineData("unknown-member-in-override.case.json", "/overrides/0/silent", "unknown member")]
    [InlineData("wrong-schema-version.case.json", "/schema", "gridsim-case/2")]
    [InlineData("missing-system.case.json", "", "system")]
    [InlineData("unpinned-binding.case.json", "/bindings/1/template", "pattern")]
    [InlineData("bad-import-pattern.case.json", "/imports/0/templates/2", "pattern")]
    [InlineData("absolute-network-path.case.json", "/network/1", "pattern")]
    public void An_invalid_case_fails_at_the_expected_place(string file, string pointer, string messageFragment)
    {
        IReadOnlyList<SchemaError> errors = ValidateFixture("cases", file, "case.schema.json");
        Assert.True(errors.Count > 0, $"{file} should fail case.schema.json but passed.");
        Assert.True(errors.Any(e => e.Pointer == pointer && e.Message.Contains(messageFragment, StringComparison.Ordinal)),
            $"{file}: expected an error at '{pointer}' mentioning '{messageFragment}'; got:{Environment.NewLine}{Dump(errors)}");
    }

    [Fact]
    public void Every_case_fixture_is_named_in_a_test()
    {
        // A fixture added later without a test row would never run; refuse that.
        string[] named =
        {
            "good.case.json", "override-without-reason.case.json", "override-without-shadow.case.json",
            "override-shadow-not-explicit.case.json", "override-blank-reason.case.json", "unknown-member.case.json",
            "unknown-member-in-system.case.json", "unknown-member-in-override.case.json", "wrong-schema-version.case.json",
            "missing-system.case.json", "unpinned-binding.case.json", "bad-import-pattern.case.json",
            "absolute-network-path.case.json",
        };
        string[] present = Directory.EnumerateFiles(Path.Combine(FixturesDir, "cases")).Select(Path.GetFileName).Select(f => f!).ToArray();
        Assert.Equal(named.OrderBy(f => f, StringComparer.Ordinal), present.OrderBy(f => f, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("""{ "template": "gridsim.x@1", "params": { "h": 4 }, "reason": "r", "shadow": "explicit" }""", true)]
    [InlineData("""{ "template": "gridsim.x@1", "params": { "h": null }, "reason": "r", "shadow": "explicit" }""", false)]
    [InlineData("""{ "template": "gridsim.x@1", "params": { "h": 4 }, "reason": "r", "shadow": true }""", false)]
    [InlineData("""{ "template": "gridsim.x@1", "params": { "h": 4 }, "reason": "", "shadow": "explicit" }""", false)]
    [InlineData("""{ "template": "gridsim.x", "params": { "h": 4 }, "reason": "r", "shadow": "explicit" }""", false)]
    [InlineData("""{ "params": { "h": 4 }, "reason": "r", "shadow": "explicit" }""", false)]
    public void An_override_needs_a_pinned_template_present_values_a_reason_and_explicit_shadow(string json, bool ok)
    {
        string text = """{ "schema": "gridsim-case/2", "name": "n", "system": { "baseMva": 100, "frequencyHz": 50 }, "network": ["n.json"], "overrides": [ """
                      + json + " ] }";
        using JsonDocument doc = JsonDocument.Parse(text, JsonSchemaSubset.ReaderOptions);
        IReadOnlyList<SchemaError> errors = Validator.Value.Validate(doc.RootElement, "case.schema.json");
        Assert.True(ok == (errors.Count == 0), Dump(errors));
    }

    [Theory]
    [InlineData("\"gridsim.*\"", true)]
    [InlineData("\"gridsim.exciter.*\"", true)]
    [InlineData("\"gridsim.exciter.sexs\"", true)]
    [InlineData("\"gridsim.exciter.sexs@2\"", true)]
    [InlineData("\"gridsim.exciter.sexs.*\"", true)]
    [InlineData("\"gridsim.\"", false)]
    [InlineData("\"gridsim.exciter*\"", false)]
    [InlineData("\"gridsim.exciter.*@1\"", false)]
    [InlineData("\"gridsim.exciter.sexs@0\"", false)]
    [InlineData("\"*\"", false)]
    public void An_import_pattern_is_a_ref_an_id_or_a_dotted_prefix(string pattern, bool ok)
    {
        string text = """{ "schema": "gridsim-case/2", "name": "n", "system": { "baseMva": 100, "frequencyHz": 50 }, "network": ["n.json"], "imports": [ { "library": "lib/a.json", "templates": [ """
                      + pattern + " ] } ] }";
        using JsonDocument doc = JsonDocument.Parse(text, JsonSchemaSubset.ReaderOptions);
        IReadOnlyList<SchemaError> errors = Validator.Value.Validate(doc.RootElement, "case.schema.json");
        Assert.True(ok == (errors.Count == 0), Dump(errors));
    }

    // ----- network file -----

    [Fact]
    public void A_good_network_using_every_member_passes()
    {
        IReadOnlyList<SchemaError> errors = ValidateFixture("networks", "good.network.json", "network.schema.json");
        Assert.True(errors.Count == 0, Dump(errors));
    }

    [Theory]
    [InlineData("v1-flat-converter-member.network.json", "/generators/1/converterInterfaced", "unknown member")]
    [InlineData("v1-zero-sequence-spelling.network.json", "/lines/0/rPu0", "unknown member")]
    [InlineData("v1-dynamics-class.network.json", "/transformers/0/dynamicsClass", "unknown member")]
    [InlineData("unknown-winding.network.json", "/transformers/0/windings/to", "not one of the allowed values")]
    [InlineData("generator-without-name.network.json", "/generators/1", "name")]
    [InlineData("protection-class-not-template.network.json", "/protections/0/class", "unknown member")]
    [InlineData("wrong-schema.network.json", "/schema", "gridsim-network/2")]
    [InlineData("bad-bus-type.network.json", "/buses/0/type", "not one of the allowed values")]
    [InlineData("power-factor-zero.network.json", "/demands/1/powerFactor", "strictly greater")]
    public void An_invalid_network_fails_at_the_expected_place(string file, string pointer, string messageFragment)
    {
        IReadOnlyList<SchemaError> errors = ValidateFixture("networks", file, "network.schema.json");
        Assert.True(errors.Count > 0, $"{file} should fail network.schema.json but passed.");
        Assert.True(errors.Any(e => e.Pointer == pointer && e.Message.Contains(messageFragment, StringComparison.Ordinal)),
            $"{file}: expected an error at '{pointer}' mentioning '{messageFragment}'; got:{Environment.NewLine}{Dump(errors)}");
    }

    [Fact]
    public void Every_network_fixture_is_named_in_a_test()
    {
        string[] named =
        {
            "good.network.json", "v1-flat-converter-member.network.json", "v1-zero-sequence-spelling.network.json",
            "v1-dynamics-class.network.json", "unknown-winding.network.json", "generator-without-name.network.json",
            "protection-class-not-template.network.json", "wrong-schema.network.json", "bad-bus-type.network.json",
            "power-factor-zero.network.json",
        };
        string[] present = Directory.EnumerateFiles(Path.Combine(FixturesDir, "networks")).Select(Path.GetFileName).Select(f => f!).ToArray();
        Assert.Equal(named.OrderBy(f => f, StringComparer.Ordinal), present.OrderBy(f => f, StringComparer.Ordinal));
    }

    [Fact]
    public void The_winding_connections_are_exactly_the_WindingConnection_names()
    {
        // network.schema.json must spell winding connections as GridModel does, so Phase 5 can wire them through
        // without a translation table.
        using JsonDocument doc = JsonSchemaSubset.ParseFile(Path.Combine(SchemasDir, "network.schema.json"));
        string[] names = doc.RootElement.GetProperty("$defs").GetProperty("windingConnection").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(Enum.GetNames<WindingConnection>(), names);
    }

    [Fact]
    public void A_network_with_no_elements_passes_and_one_without_schema_fails()
    {
        using JsonDocument empty = JsonDocument.Parse("""{ "schema": "gridsim-network/2" }""", JsonSchemaSubset.ReaderOptions);
        Assert.Empty(Validator.Value.Validate(empty.RootElement, "network.schema.json"));
        using JsonDocument none = JsonDocument.Parse("""{ "buses": [] }""", JsonSchemaSubset.ReaderOptions);
        Assert.Contains(Validator.Value.Validate(none.RootElement, "network.schema.json"), e => e.Message.Contains("'schema'", StringComparison.Ordinal));
    }
}
