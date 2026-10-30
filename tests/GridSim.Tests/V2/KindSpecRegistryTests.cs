using System.Text.Json;
using GridSim.Core.IO.Json.Schema;
using Xunit;
using static GridSim.Tests.V2.V2TestSupport;

namespace GridSim.Tests.V2;

/// <summary>
/// The kind spec registry: loading kind files in the fixed gridsim-kind/2 format (validated against the real
/// schemas/v2/kind.schema.json, plus the semantic rules the schema subset cannot express), and checking a
/// template's params against a kind (unknown key, unit, range, required, null rules, value types). Kind files here
/// are hand-built test kinds, independent of the real kind specs.
/// </summary>
public class KindSpecRegistryTests
{
    private const string TestKind = """
        {
          "schema": "gridsim-kind/2",
          "kind": "testKind",
          "title": "Test kind",
          "description": "A kind with one parameter of every type.",
          "params": {
            "hS":      { "type": "number",  "unit": "s", "min": 0, "max": 20, "required": true, "description": "Inertia.",
                         "codeDefaults": [ { "consumer": "FuelMap nuclear", "ref": "opaque-ref-1", "value": 5.0 },
                                           { "consumer": "SyntheticGb nuclear", "ref": "opaque-ref-2", "value": 4.0 } ] },
            "taps":    { "type": "integer", "unit": "1", "min": 1, "required": false, "description": "Tap count.", "markGated": true },
            "chem":    { "type": "enum", "values": ["LFP", "NMC"], "required": false, "description": "Chemistry.",
                         "codeDefaults": [ { "consumer": "preset", "ref": "r", "value": "LFP" } ] },
            "label":   { "type": "string", "required": false, "description": "A label." },
            "enabled": { "type": "boolean", "required": false, "description": "On or off.", "codeDefaults": [] }
          }
        }
        """;

    private static KindSpecRegistry Registry(params (string File, string Text)[] kinds)
        => KindSpecRegistry.FromSources(kinds.ToDictionary(k => k.File, k => k.Text, StringComparer.Ordinal), RealSchemas());

    private static KindSpecRegistry TestRegistry() => Registry(("testKind.kind.json", TestKind));

    private static InvalidDataException LoadFails(string text, string file = "testKind.kind.json")
        => Assert.Throws<InvalidDataException>(() => Registry((file, text)));

    private static IReadOnlyList<SchemaError> Check(string paramsJson, bool placeholder = false, string kind = "testKind")
    {
        using JsonDocument doc = JsonDocument.Parse(paramsJson);
        return TestRegistry().CheckParams(kind, doc.RootElement.Clone(), placeholder, "/templates/0/params");
    }

    // ----- loading -----

    [Fact]
    public void A_well_formed_kind_loads_with_every_field()
    {
        KindSpecRegistry r = TestRegistry();
        KindSpec k = r.Get("testKind");
        Assert.Equal("Test kind", k.Title);
        Assert.Equal("testKind.kind.json", k.SourceFile);
        Assert.Equal(5, k.Params.Count);

        KindParamSpec h = k.Params["hS"];
        Assert.Equal(KindParamType.Number, h.Type);
        Assert.Equal("s", h.Unit);
        Assert.Equal(0.0, h.Min);
        Assert.Equal(20.0, h.Max);
        Assert.True(h.Required);
        Assert.False(h.MarkGated);
        Assert.Equal(2, h.CodeDefaults.Count);
        Assert.Equal("FuelMap nuclear", h.CodeDefaults[0].Consumer);
        Assert.Equal("opaque-ref-1", h.CodeDefaults[0].Ref);
        Assert.Equal(5.0, h.CodeDefaults[0].Value.GetDouble());
        Assert.Equal(4.0, h.CodeDefaults[1].Value.GetDouble());   // disagreeing consumers kept apart, never merged

        Assert.True(k.Params["taps"].MarkGated);
        Assert.Equal(KindParamType.Integer, k.Params["taps"].Type);
        Assert.Equal(new[] { "LFP", "NMC" }, k.Params["chem"].Values);
        Assert.Equal(KindParamType.String, k.Params["label"].Type);
        Assert.Equal(KindParamType.Boolean, k.Params["enabled"].Type);
        Assert.Null(k.Params["label"].Unit);
    }

    [Fact]
    public void An_unknown_kind_is_a_loud_error()
    {
        KindSpecRegistry r = TestRegistry();
        Assert.False(r.Contains("nope"));
        Assert.Null(r.Find("nope"));
        Assert.Contains("Unknown kind 'nope'", Assert.Throws<InvalidDataException>(() => r.Get("nope")).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"kind\": \"testKind\",", "\"kind\": \"testKind\", \"extra\": 1,", "/extra")]
    [InlineData("\"required\": true, \"description\": \"Inertia.\",", "\"required\": true, \"description\": \"Inertia.\", \"default\": 5,", "/params/hS/default")]
    [InlineData("\"consumer\": \"preset\",", "\"consumer\": \"preset\", \"note\": \"x\",", "/params/chem/codeDefaults/0/note")]
    public void An_unknown_member_anywhere_in_a_kind_file_is_refused(string from, string to, string pointer)
    {
        InvalidDataException ex = LoadFails(TestKind.Replace(from, to, StringComparison.Ordinal));
        Assert.Contains("testKind.kind.json: " + pointer, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"schema\": \"gridsim-kind/2\"", "\"schema\": \"gridsim-kind/1\"", "must be exactly")]
    [InlineData("\"unit\": \"s\"", "\"unit\": \"sec\"", "not one of the allowed values")]
    [InlineData("\"type\": \"boolean\"", "\"type\": \"bool\"", "not one of the allowed values")]
    [InlineData("\"values\": [\"LFP\", \"NMC\"]", "\"values\": []", "at least 1 required")]
    [InlineData("\"kind\": \"testKind\"", "\"kind\": \"Test-Kind\"", "does not match the pattern")]
    [InlineData("\"value\": \"LFP\"", "\"value\": null", "expected type number or string or boolean")]
    public void A_kind_file_breaking_the_schema_is_refused(string from, string to, string expected)
    {
        InvalidDataException ex = LoadFails(TestKind.Replace(from, to, StringComparison.Ordinal));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"type\": \"number\",  \"unit\": \"s\",", "\"type\": \"number\",", "must declare its unit")]
    [InlineData("\"type\": \"integer\", \"unit\": \"1\",", "\"type\": \"integer\",", "must declare its unit")]
    [InlineData("\"type\": \"string\",", "\"type\": \"string\", \"unit\": \"MW\",", "only number and integer parameters may declare a unit")]
    [InlineData("\"type\": \"string\",", "\"type\": \"string\", \"max\": 3,", "may declare min or max")]
    [InlineData("\"min\": 0, \"max\": 20,", "\"min\": 21, \"max\": 20,", "min 21 is above max 20")]
    [InlineData("\"values\": [\"LFP\", \"NMC\"],", "", "an enum parameter must list its values")]
    [InlineData("\"type\": \"string\",", "\"type\": \"string\", \"values\": [\"a\"],", "only an enum parameter may list values")]
    [InlineData("[\"LFP\", \"NMC\"]", "[\"LFP\", \"LFP\"]", "values lists the same value twice")]
    [InlineData("\"value\": 5.0", "\"value\": \"5\"", "is not a finite number")]
    [InlineData("\"value\": \"LFP\"", "\"value\": \"NaS\"", "is not one of the allowed values")]
    public void A_kind_file_breaking_a_semantic_rule_is_refused(string from, string to, string expected)
    {
        string text = TestKind.Replace(from, to, StringComparison.Ordinal);
        Assert.NotEqual(TestKind, text);   // the substitution must actually have happened
        InvalidDataException ex = LoadFails(text);
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_file_must_be_named_after_its_kind()
    {
        InvalidDataException ex = LoadFails(TestKind, "otherName.kind.json");
        Assert.Contains("must then be named 'testKind.kind.json'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_kind_file_problem_is_reported_together()
    {
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => Registry(
            ("a.kind.json", TestKind.Replace("\"kind\": \"testKind\",", "\"kind\": \"testKind\", \"x\": 1,", StringComparison.Ordinal)),
            ("b.kind.json", "{ not json")));
        Assert.Contains("a.kind.json: /x", ex.Message, StringComparison.Ordinal);
        Assert.Contains("b.kind.json: not valid JSON", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_schema_set_without_the_kind_schema_is_refused()
    {
        JsonSchemaSubset bare = Schemas(("x.schema.json", "{}"));
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() =>
            KindSpecRegistry.FromSources(new Dictionary<string, string> { ["testKind.kind.json"] = TestKind }, bare));
        Assert.Contains("kind.schema.json", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_kinds_directory_is_refused()
        => Assert.Throws<DirectoryNotFoundException>(() =>
            KindSpecRegistry.LoadDirectory(Path.Combine(Path.GetTempPath(), "gridsim-no-kinds-" + Guid.NewGuid()), RealSchemas()));

    // ----- params against a kind -----

    [Fact]
    public void A_complete_active_params_object_passes()
    {
        IReadOnlyList<SchemaError> errors = Check("""
            { "hS": 4.0, "taps": 17, "chem": "NMC", "label": "x", "enabled": false }
            """);
        Assert.True(errors.Count == 0, Dump(errors));
    }

    [Fact]
    public void The_object_form_with_the_exact_unit_passes()
    {
        IReadOnlyList<SchemaError> errors = Check("""
            { "hS": { "v": 20, "unit": "s", "prov": { "tag": "Recorded", "note": "n" } }, "taps": { "v": 2.0 }, "chem": { "v": "LFP" } }
            """);
        Assert.True(errors.Count == 0, Dump(errors));
    }

    [Fact]
    public void An_unknown_parameter_is_an_error()
    {
        SchemaError e = Assert.Single(Check("""{ "hS": 4, "hs": 4 }"""));
        Assert.Equal("/templates/0/params/hs", e.Pointer);
        Assert.Contains("unknown parameter 'hs'", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_required_parameter_is_an_error_for_active_and_placeholder()
    {
        Assert.Contains("requires parameter 'hS'", Assert.Single(Check("{}")).Message, StringComparison.Ordinal);
        Assert.Contains("requires parameter 'hS'", Assert.Single(Check("{}", placeholder: true)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ms")]
    [InlineData("S")]
    [InlineData("pu@machine")]
    public void A_unit_that_differs_from_the_spec_is_an_error(string unit)
    {
        SchemaError e = Assert.Single(Check("{ \"hS\": { \"v\": 4, \"unit\": \"" + unit + "\" } }"));
        Assert.Equal("/templates/0/params/hS/unit", e.Pointer);
        Assert.Contains("does not match the kind's unit 's'", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_unit_on_a_parameter_without_one_is_an_error()
        => Assert.Contains("takes no unit", Assert.Single(Check("""{ "hS": 1, "label": { "v": "x", "unit": "MW" } }""")).Message, StringComparison.Ordinal);

    [Theory]
    [InlineData("""{ "hS": -0.1 }""", "below the minimum 0")]
    [InlineData("""{ "hS": 20.5 }""", "above the maximum 20")]
    [InlineData("""{ "hS": { "v": 21 } }""", "above the maximum 20")]
    [InlineData("""{ "hS": 1, "taps": 0 }""", "below the minimum 1")]
    public void Out_of_range_is_an_error(string json, string expected)
        => Assert.Contains(expected, Assert.Single(Check(json)).Message, StringComparison.Ordinal);

    [Fact]
    public void Range_bounds_are_inclusive()
    {
        Assert.Empty(Check("""{ "hS": 0 }"""));
        Assert.Empty(Check("""{ "hS": 20 }"""));
    }

    [Theory]
    [InlineData("""{ "hS": "4" }""", "is not a finite number")]
    [InlineData("""{ "hS": true }""", "is not a finite number")]
    [InlineData("""{ "hS": 1, "taps": 2.5 }""", "is not a whole number")]
    [InlineData("""{ "hS": 1, "chem": "lfp" }""", "is not one of the allowed values")]
    [InlineData("""{ "hS": 1, "chem": 1 }""", "is not one of the allowed values")]
    [InlineData("""{ "hS": 1, "label": 7 }""", "is not a string")]
    [InlineData("""{ "hS": 1, "enabled": "true" }""", "is not true or false")]
    [InlineData("""{ "hS": 1, "enabled": { "v": 1 } }""", "is not true or false")]
    public void A_value_of_the_wrong_type_is_an_error(string json, string expected)
        => Assert.Contains(expected, Assert.Single(Check(json)).Message, StringComparison.Ordinal);

    [Fact]
    public void Placeholder_requires_every_value_null()
    {
        Assert.Empty(Check("""{ "hS": null, "chem": null }""", placeholder: true));
        IReadOnlyList<SchemaError> errors = Check("""{ "hS": null, "chem": "LFP", "enabled": false }""", placeholder: true);
        Assert.Equal(new[] { "/templates/0/params/chem", "/templates/0/params/enabled" }, errors.Select(e => e.Pointer));
        Assert.All(errors, e => Assert.Contains("every value null", e.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void Active_refuses_any_null()
    {
        SchemaError e = Assert.Single(Check("""{ "hS": null }"""));
        Assert.Contains("may not have a null value", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Params_of_an_unknown_kind_are_an_error()
        => Assert.Contains("unknown kind 'ghost'", Assert.Single(Check("{}", kind: "ghost")).Message, StringComparison.Ordinal);

    [Fact]
    public void Every_params_problem_is_reported_not_just_the_first()
    {
        IReadOnlyList<SchemaError> errors = Check("""{ "taps": 0.5, "chem": "X", "bogus": 1, "label": null }""");
        // missing hS, taps not whole, chem not allowed, bogus unknown, label null in an active template
        Assert.True(errors.Count == 5, Dump(errors));
    }
}
