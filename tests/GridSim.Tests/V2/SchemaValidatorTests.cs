using System.Text.Json;
using GridSim.Core.IO.Json.Schema;
using Xunit;
using static GridSim.Tests.V2.V2TestSupport;

namespace GridSim.Tests.V2;

/// <summary>
/// Validation semantics of every supported keyword, each with a passing and a failing case, on hand-built schemas;
/// plus the all-errors-with-pointers contract and the fail-loud helper.
/// </summary>
public class SchemaValidatorTests
{
    private static void Passes(string schema, string json)
    {
        IReadOnlyList<SchemaError> errors = ValidateOne(schema, json);
        Assert.True(errors.Count == 0, $"expected {json} to pass {schema}, got:{Environment.NewLine}{Dump(errors)}");
    }

    private static SchemaError FailsOnce(string schema, string json)
    {
        IReadOnlyList<SchemaError> errors = ValidateOne(schema, json);
        Assert.True(errors.Count == 1, $"expected exactly one error for {json} against {schema}, got {errors.Count}:{Environment.NewLine}{Dump(errors)}");
        return errors[0];
    }

    // ----- type -----

    [Theory]
    [InlineData("object", "{}")]
    [InlineData("array", "[]")]
    [InlineData("string", "\"s\"")]
    [InlineData("number", "1.5")]
    [InlineData("number", "2")]
    [InlineData("integer", "2")]
    [InlineData("integer", "2.0")]
    [InlineData("integer", "-3e2")]
    [InlineData("boolean", "true")]
    [InlineData("boolean", "false")]
    [InlineData("null", "null")]
    public void Type_accepts_its_own_values(string type, string json) => Passes("{ \"type\": \"" + type + "\" }", json);

    [Theory]
    [InlineData("object", "[]")]
    [InlineData("array", "{}")]
    [InlineData("string", "1")]
    [InlineData("number", "\"1\"")]
    [InlineData("integer", "2.5")]
    [InlineData("boolean", "0")]
    [InlineData("null", "false")]
    public void Type_refuses_other_values(string type, string json)
    {
        SchemaError e = FailsOnce("{ \"type\": \"" + type + "\" }", json);
        Assert.Equal("", e.Pointer);
        Assert.Contains("expected type " + type, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_type_union_accepts_any_member()
    {
        Passes("""{ "type": ["number", "null"] }""", "null");
        Passes("""{ "type": ["number", "null"] }""", "3");
        Assert.Contains("number or null", FailsOnce("""{ "type": ["number", "null"] }""", "\"x\"").Message, StringComparison.Ordinal);
    }

    // ----- properties / required / additionalProperties -----

    [Fact]
    public void Properties_validate_each_named_member_at_its_pointer()
    {
        const string s = """{ "properties": { "a": { "type": "number" }, "b/c": { "type": "string" } } }""";
        Passes(s, """{ "a": 1, "b/c": "x", "other": true }""");
        SchemaError e = FailsOnce(s, """{ "a": 1, "b/c": 2 }""");
        Assert.Equal("/b~1c", e.Pointer);
    }

    [Fact]
    public void Properties_ignore_a_non_object()
        => Passes("""{ "properties": { "a": { "type": "number" } }, "required": ["a"] }""", "[1]");

    [Fact]
    public void Required_reports_each_missing_member()
    {
        const string s = """{ "required": ["a", "b"] }""";
        Passes(s, """{ "a": null, "b": 0 }""");
        IReadOnlyList<SchemaError> errors = ValidateOne(s, "{}");
        Assert.Equal(2, errors.Count);
        Assert.Contains("missing required member 'a'", errors[0].Message, StringComparison.Ordinal);
        Assert.Contains("missing required member 'b'", errors[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Additional_properties_false_refuses_each_unknown_member_at_its_pointer()
    {
        const string s = """{ "properties": { "a": {} }, "additionalProperties": false }""";
        Passes(s, """{ "a": 1 }""");
        IReadOnlyList<SchemaError> errors = ValidateOne(s, """{ "a": 1, "x": 2, "y": 3 }""");
        Assert.Equal(new[] { "/x", "/y" }, errors.Select(e => e.Pointer));
        Assert.Contains("unknown member 'x'", errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Additional_properties_schema_applies_to_members_not_in_properties()
    {
        const string s = """{ "properties": { "a": { "type": "string" } }, "additionalProperties": { "type": "number" } }""";
        Passes(s, """{ "a": "s", "b": 1, "c": 2 }""");
        Assert.Equal("/c", FailsOnce(s, """{ "a": "s", "b": 1, "c": "no" }""").Pointer);
    }

    [Fact]
    public void Additional_properties_true_allows_anything()
        => Passes("""{ "properties": { "a": {} }, "additionalProperties": true }""", """{ "z": [1] }""");

    [Fact]
    public void Additional_properties_does_not_see_properties_of_a_sibling_ref()
    {
        // JSON Schema 2020-12: additionalProperties looks only at 'properties' in its own schema object.
        JsonSchemaSubset set = Schemas(("s.schema.json", """
            { "$defs": { "base": { "properties": { "a": {} } } }, "$ref": "#/$defs/base", "additionalProperties": false }
            """));
        Assert.Equal("/a", Assert.Single(Validate(set, "s.schema.json", """{ "a": 1 }""")).Pointer);
    }

    // ----- items / minItems -----

    [Fact]
    public void Items_validates_each_element_at_its_index()
    {
        const string s = """{ "items": { "type": "integer" } }""";
        Passes(s, "[1, 2, 3]");
        IReadOnlyList<SchemaError> errors = ValidateOne(s, """[1, "two", 3, 4.5]""");
        Assert.Equal(new[] { "/1", "/3" }, errors.Select(e => e.Pointer));
    }

    [Fact]
    public void Min_items_counts_elements()
    {
        Passes("""{ "minItems": 2 }""", "[1, 2]");
        Assert.Contains("at least 2", FailsOnce("""{ "minItems": 2 }""", "[1]").Message, StringComparison.Ordinal);
        Passes("""{ "minItems": 2 }""", "\"not an array\"");
    }

    // ----- enum / const -----

    [Fact]
    public void Enum_matches_by_json_value()
    {
        const string s = """{ "enum": ["a", 1, null, { "k": [true] }] }""";
        Passes(s, "\"a\"");
        Passes(s, "1.0");
        Passes(s, "null");
        Passes(s, """{ "k": [true] }""");
        Assert.Contains("not one of the allowed values", FailsOnce(s, "\"b\"").Message, StringComparison.Ordinal);
        FailsOnce(s, """{ "k": [false] }""");
        FailsOnce(s, "\"A\"");   // case-sensitive
    }

    [Fact]
    public void Const_matches_exactly_one_value()
    {
        Passes("""{ "const": "gridsim-library/2" }""", "\"gridsim-library/2\"");
        Assert.Contains("must be exactly", FailsOnce("""{ "const": "gridsim-library/2" }""", "\"gridsim-library/1\"").Message, StringComparison.Ordinal);
        FailsOnce("""{ "const": false }""", "0");
    }

    // ----- minimum / maximum / exclusiveMinimum -----

    [Fact]
    public void Minimum_is_inclusive()
    {
        Passes("""{ "minimum": 0 }""", "0");
        Assert.Contains("below the minimum", FailsOnce("""{ "minimum": 0 }""", "-0.001").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Maximum_is_inclusive()
    {
        Passes("""{ "maximum": 1.5 }""", "1.5");
        Assert.Contains("above the maximum", FailsOnce("""{ "maximum": 1.5 }""", "1.5000001").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Exclusive_minimum_refuses_the_bound_itself()
    {
        Passes("""{ "exclusiveMinimum": 0 }""", "1e-300");
        Assert.Contains("strictly greater than 0", FailsOnce("""{ "exclusiveMinimum": 0 }""", "0").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Numeric_bounds_ignore_a_non_number()
        => Passes("""{ "minimum": 5, "maximum": 6, "exclusiveMinimum": 5 }""", "\"text\"");

    [Fact]
    public void A_number_too_large_for_a_double_is_refused()
        => Assert.Contains("outside the range of a double", FailsOnce("""{ "minimum": 0 }""", "1e400").Message, StringComparison.Ordinal);

    // ----- pattern -----

    [Fact]
    public void Pattern_is_an_unanchored_search()
    {
        Passes("""{ "pattern": "VERIFY" }""", "\"approximated [VERIFY] from literature\"");
        Assert.Contains("does not match the pattern", FailsOnce("""{ "pattern": "VERIFY" }""", "\"verify\"").Message, StringComparison.Ordinal);
        Passes("""{ "pattern": "^x" }""", "42");   // a non-string is left to 'type'
    }

    [Fact]
    public void An_anchored_pattern_refuses_a_trailing_newline()
    {
        // ECMA-262 '$' is the very end; .NET's would also match before a final newline. The subset uses the ECMA meaning.
        Passes("""{ "pattern": "^gridsim\\.[a-z]+$" }""", "\"gridsim.abc\"");
        FailsOnce("""{ "pattern": "^gridsim\\.[a-z]+$" }""", "\"gridsim.abc\\n\"");
    }

    // ----- oneOf / anyOf -----

    [Fact]
    public void One_of_needs_exactly_one_match()
    {
        const string s = """{ "oneOf": [ { "type": "integer" }, { "minimum": 10 } ] }""";
        Passes(s, "3");      // integer only
        Passes(s, "10.5");   // minimum only
        SchemaError both = FailsOnce(s, "12");
        Assert.Contains("matches 2 oneOf alternatives (0, 1)", both.Message, StringComparison.Ordinal);
        IReadOnlyList<SchemaError> none = ValidateOne(s, "2.5");
        Assert.Contains("matches none of the 2 oneOf alternatives", none[0].Message, StringComparison.Ordinal);
        Assert.Contains(none, e => e.Message.StartsWith("[oneOf alternative 0]", StringComparison.Ordinal));
        Assert.Contains(none, e => e.Message.StartsWith("[oneOf alternative 1]", StringComparison.Ordinal));
    }

    [Fact]
    public void Any_of_needs_at_least_one_match()
    {
        const string s = """{ "anyOf": [ { "type": "null" }, { "type": "number", "minimum": 0 } ] }""";
        Passes(s, "null");
        Passes(s, "4");
        IReadOnlyList<SchemaError> errors = ValidateOne(s, "-1");
        Assert.Contains("matches none of the 2 anyOf alternatives", errors[0].Message, StringComparison.Ordinal);
        Assert.Contains(errors, e => e.Message.Contains("[anyOf alternative 1]", StringComparison.Ordinal) && e.Message.Contains("below the minimum", StringComparison.Ordinal));
    }

    // ----- if / then / else -----

    [Fact]
    public void If_chooses_then_or_else_and_reports_nothing_itself()
    {
        const string s = """
            {
              "if": { "required": ["kind"], "properties": { "kind": { "const": "placeholder" } } },
              "then": { "properties": { "v": { "type": "null" } } },
              "else": { "properties": { "v": { "type": "number" } } }
            }
            """;
        Passes(s, """{ "kind": "placeholder", "v": null }""");
        Passes(s, """{ "kind": "active", "v": 1 }""");
        Passes(s, """{ "v": 2 }""");
        Assert.Equal("/v", FailsOnce(s, """{ "kind": "placeholder", "v": 1 }""").Pointer);
        Assert.Equal("/v", FailsOnce(s, """{ "kind": "active", "v": null }""").Pointer);
    }

    [Fact]
    public void If_without_else_applies_nothing_when_it_fails()
    {
        const string s = """{ "if": { "type": "string" }, "then": { "pattern": "^a" } }""";
        Passes(s, "\"abc\"");
        Passes(s, "7");
        FailsOnce(s, "\"xyz\"");
    }

    // ----- $ref, local and cross-file -----

    [Fact]
    public void A_local_ref_validates_against_its_target()
    {
        const string s = """{ "$defs": { "pos": { "type": "number", "minimum": 0 } }, "properties": { "p": { "$ref": "#/$defs/pos" } } }""";
        Passes(s, """{ "p": 3 }""");
        Assert.Equal("/p", FailsOnce(s, """{ "p": -3 }""").Pointer);
    }

    [Fact]
    public void A_cross_file_ref_resolves_further_local_refs_in_the_target_file()
    {
        // common's '#/$defs/unit' must resolve inside common.schema.json, not inside the referring file.
        JsonSchemaSubset set = Schemas(
            ("common.schema.json", """
                { "$defs": {
                    "unit": { "enum": ["MW", "Mvar"] },
                    "quantity": { "type": "object", "required": ["v"], "properties": { "v": { "type": "number" }, "unit": { "$ref": "#/$defs/unit" } } } } }
                """),
            ("main.schema.json", """
                { "$defs": { "unit": { "enum": ["something else"] } }, "properties": { "q": { "$ref": "common.schema.json#/$defs/quantity" } } }
                """));
        Assert.Empty(Validate(set, "main.schema.json", """{ "q": { "v": 1, "unit": "MW" } }"""));
        SchemaError e = Assert.Single(Validate(set, "main.schema.json", """{ "q": { "v": 1, "unit": "kV" } }"""));
        Assert.Equal("/q/unit", e.Pointer);
    }

    [Fact]
    public void Title_and_description_have_no_effect()
        => Passes("""{ "title": "t", "description": "d" }""", """{ "anything": [1, "x", null] }""");

    [Fact]
    public void Validating_against_a_file_not_in_the_set_fails_loud()
    {
        JsonSchemaSubset set = Schemas(("a.schema.json", "{}"));
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => Validate(set, "b.schema.json", "{}"));
        Assert.Contains("b.schema.json", ex.Message, StringComparison.Ordinal);
    }

    // ----- every error, with pointers -----

    [Fact]
    public void Every_error_is_returned_with_its_pointer_not_just_the_first()
    {
        const string s = """
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["schema", "items"],
              "properties": {
                "schema": { "const": "x/2" },
                "items": { "type": "array", "minItems": 1, "items": {
                    "type": "object", "additionalProperties": false, "required": ["id"],
                    "properties": { "id": { "type": "string", "pattern": "^g\\." }, "n": { "type": "integer", "minimum": 1 } } } }
              }
            }
            """;
        const string doc = """
            {
              "schema": "x/1",
              "items": [ { "id": "g.ok", "n": 1 }, { "id": "bad", "n": 0 }, { "n": 2.5, "extra": true } ],
              "stray": 1
            }
            """;
        IReadOnlyList<SchemaError> errors = ValidateOne(s, doc);
        string all = Dump(errors);
        Assert.Contains(errors, e => e.Pointer == "/schema");
        Assert.Contains(errors, e => e.Pointer == "/items/1/id");
        Assert.Contains(errors, e => e.Pointer == "/items/1/n");
        Assert.Contains(errors, e => e.Pointer == "/items/2" && e.Message.Contains("missing required member 'id'", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Pointer == "/items/2/n");
        Assert.Contains(errors, e => e.Pointer == "/items/2/extra");
        Assert.Contains(errors, e => e.Pointer == "/stray");
        Assert.True(errors.Count == 7, all);
    }

    [Fact]
    public void Validate_or_throw_names_the_document_and_lists_every_error()
    {
        JsonSchemaSubset set = Schemas(("s.schema.json", """{ "required": ["a", "b"], "additionalProperties": false, "properties": { "a": {}, "b": {} } }"""));
        using JsonDocument doc = JsonDocument.Parse("""{ "c": 1 }""");
        InvalidDataException ex = Assert.Throws<InvalidDataException>(
            () => new SchemaValidator(set).ValidateOrThrow(doc.RootElement, "s.schema.json", "my-library.json"));
        Assert.StartsWith("my-library.json: 3 validation error(s):", ex.Message, StringComparison.Ordinal);
        Assert.Contains("(root): missing required member 'a'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("(root): missing required member 'b'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("/c: unknown member 'c'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_or_throw_is_silent_when_clean()
    {
        JsonSchemaSubset set = Schemas(("s.schema.json", """{ "type": "object" }"""));
        using JsonDocument doc = JsonDocument.Parse("{}");
        new SchemaValidator(set).ValidateOrThrow(doc.RootElement, "s.schema.json", "x.json");
    }

    [Fact]
    public void Parse_file_names_the_file_on_bad_json()
    {
        string path = Path.Combine(Path.GetTempPath(), "gridsim-v2-bad-" + Guid.NewGuid() + ".json");
        File.WriteAllText(path, "{ \"a\": 1, \"a\": 2 }");
        try
        {
            InvalidDataException ex = Assert.Throws<InvalidDataException>(() => JsonSchemaSubset.ParseFile(path));
            Assert.StartsWith(Path.GetFileName(path) + ":", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
