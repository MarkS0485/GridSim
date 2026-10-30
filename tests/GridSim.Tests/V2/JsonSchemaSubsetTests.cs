using GridSim.Core.IO.Json.Schema;
using Xunit;
using static GridSim.Tests.V2.V2TestSupport;

namespace GridSim.Tests.V2;

/// <summary>
/// Load-time behaviour of the hand-rolled schema subset: the supported keyword list, loud refusal of anything
/// outside it (naming file and JSON pointer), keyword shape checks, $ref resolution (local and cross-file), and
/// $ref cycles that never consume input. All schemas here are hand-built.
/// </summary>
public class JsonSchemaSubsetTests
{
    private static InvalidDataException LoadFails(params (string File, string Text)[] files)
        => Assert.Throws<InvalidDataException>(() => Schemas(files));

    [Fact]
    public void The_supported_keyword_list_is_exactly_the_plan_subset()
    {
        // Pinned against the dream-of-json-ification plan: widening the subset is a deliberate act, not a drift.
        string[] plan = "$schema $id $ref $defs type properties required additionalProperties items enum const minimum maximum exclusiveMinimum minItems pattern oneOf anyOf if then else title description"
            .Split(' ');
        Assert.Equal(plan, JsonSchemaSubset.SupportedKeywords);
    }

    [Fact]
    public void Every_supported_keyword_loads_together_in_one_schema()
    {
        JsonSchemaSubset set = Schemas(("all.schema.json", """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "$id": "all.schema.json",
              "title": "t", "description": "d",
              "$defs": { "n": { "type": "number", "minimum": 0, "maximum": 9, "exclusiveMinimum": -1 } },
              "type": "object",
              "required": ["a"],
              "additionalProperties": false,
              "properties": {
                "a": { "$ref": "#/$defs/n" },
                "b": { "type": "array", "minItems": 1, "items": { "type": "string", "pattern": "^x" } },
                "c": { "enum": [1, "two", null] },
                "d": { "const": true },
                "e": { "oneOf": [ { "type": "string" }, { "type": "integer" } ] },
                "f": { "anyOf": [ { "type": "string" }, { "type": "null" } ] },
                "g": { "if": { "type": "string" }, "then": { "pattern": "y" }, "else": { "type": "number" } }
              }
            }
            """));
        Assert.Equal(new[] { "all.schema.json" }, set.Files);
        Assert.Equal(1, set.RefCount);
    }

    [Theory]
    [InlineData("format")]
    [InlineData("allOf")]
    [InlineData("not")]
    [InlineData("patternProperties")]
    [InlineData("minLength")]
    [InlineData("maxItems")]
    [InlineData("exclusiveMaximum")]
    [InlineData("$anchor")]
    [InlineData("default")]
    [InlineData("examples")]
    public void An_unsupported_keyword_is_refused_naming_file_and_pointer(string keyword)
    {
        InvalidDataException ex = LoadFails(("x.schema.json",
            "{ \"type\": \"object\", \"properties\": { \"name\": { \"type\": \"string\", \"" + keyword + "\": 1 } } }"));
        Assert.Contains("x.schema.json", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'/properties/name/" + keyword + "'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("unsupported schema keyword '" + keyword + "'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_property_named_like_an_unsupported_keyword_is_fine()
    {
        // 'format' here is a member NAME the instance may carry, not a keyword of the schema.
        Schemas(("x.schema.json", """{ "properties": { "format": { "type": "string" }, "not": { "type": "number" } } }"""));
    }

    [Fact]
    public void Unsupported_keywords_inside_defs_items_and_combinators_are_all_found_at_once()
    {
        InvalidDataException ex = LoadFails(("x.schema.json", """
            {
              "$defs": { "a": { "minLength": 1 } },
              "items": { "maxItems": 2 },
              "anyOf": [ { "type": "string" }, { "format": "date" } ],
              "if": { "uniqueItems": true }, "then": {}
            }
            """));
        Assert.Contains("'/$defs/a/minLength'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'/items/maxItems'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'/anyOf/1/format'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'/if/uniqueItems'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("4 problem(s)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pointer_token_with_slash_or_tilde_is_escaped_in_the_message()
    {
        InvalidDataException ex = LoadFails(("x.schema.json", """{ "properties": { "a/b~c": { "format": "x" } } }"""));
        Assert.Contains("'/properties/a~1b~0c/format'", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "type": "float" }""", "unknown type name 'float'")]
    [InlineData("""{ "type": [] }""", "non-empty array of type names")]
    [InlineData("""{ "type": ["string", "string"] }""", "listed twice")]
    [InlineData("""{ "required": "a" }""", "'required' must be an array of strings")]
    [InlineData("""{ "required": ["a", "a"] }""", "lists 'a' twice")]
    [InlineData("""{ "minItems": -1 }""", "non-negative integer")]
    [InlineData("""{ "minItems": 1.5 }""", "non-negative integer")]
    [InlineData("""{ "minimum": "0" }""", "'minimum' must be a finite number")]
    [InlineData("""{ "maximum": null }""", "'maximum' must be a finite number")]
    [InlineData("""{ "exclusiveMinimum": true }""", "'exclusiveMinimum' must be a finite number")]
    [InlineData("""{ "pattern": "([" }""", "not a valid regular expression")]
    [InlineData("""{ "pattern": 3 }""", "'pattern' must be a string")]
    [InlineData("""{ "enum": [] }""", "non-empty array")]
    [InlineData("""{ "enum": [1, 1.0] }""", "same value twice")]
    [InlineData("""{ "oneOf": [] }""", "non-empty array of schemas")]
    [InlineData("""{ "anyOf": {} }""", "non-empty array of schemas")]
    [InlineData("""{ "then": {} }""", "without a sibling 'if'")]
    [InlineData("""{ "else": {} }""", "without a sibling 'if'")]
    [InlineData("""{ "items": [ {} ] }""", "a schema must be a JSON object")]
    [InlineData("""{ "properties": { "a": true } }""", "a schema must be a JSON object")]
    [InlineData("""{ "properties": [] }""", "'properties' must be an object of schemas")]
    [InlineData("""{ "title": 1 }""", "'title' must be a string")]
    [InlineData("""{ "description": [] }""", "'description' must be a string")]
    [InlineData("""{ "$ref": "" }""", "'$ref' must be a non-empty string")]
    [InlineData("""{ "$schema": "http://json-schema.org/draft-07/schema#" }""", "'$schema' must be")]
    [InlineData("""{ "$id": "other.schema.json" }""", "must equal the file name")]
    [InlineData("""{ "$defs": { "a": { "$id": "x.schema.json" } } }""", "only allowed at a file root")]
    [InlineData("""{ "$defs": { "a": { "$schema": "https://json-schema.org/draft/2020-12/schema" } } }""", "only allowed at a file root")]
    [InlineData("""[]""", "a schema must be a JSON object")]
    public void A_badly_shaped_keyword_is_refused(string schema, string expected)
    {
        InvalidDataException ex = LoadFails(("x.schema.json", schema));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.Contains("x.schema.json", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Additional_properties_accepts_a_boolean_or_a_schema()
    {
        Schemas(("a.schema.json", """{ "additionalProperties": false }"""));
        Schemas(("b.schema.json", """{ "additionalProperties": true }"""));
        Schemas(("c.schema.json", """{ "additionalProperties": { "type": "number" } }"""));
    }

    [Fact]
    public void Comments_and_trailing_commas_are_accepted_like_v1()
    {
        Schemas(("x.schema.json", """
            {
              // a comment, as JsonGridLoader allows in a v1 case file
              "type": "object", /* and a block comment */
              "required": ["a",],
            }
            """));
    }

    [Fact]
    public void A_duplicate_member_name_is_refused()
    {
        // In a tree read the later duplicate would silently win; v2 refuses it outright.
        InvalidDataException ex = LoadFails(("x.schema.json", """{ "type": "string", "type": "number" }"""));
        Assert.Contains("x.schema.json", ex.Message, StringComparison.Ordinal);
        Assert.Contains("not valid JSON", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_json_is_refused_naming_the_file()
    {
        InvalidDataException ex = LoadFails(("broken.schema.json", "{ \"type\": "));
        Assert.Contains("broken.schema.json: not valid JSON", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_name_with_a_path_separator_is_refused()
    {
        InvalidDataException ex = LoadFails(("sub/x.schema.json", "{}"));
        Assert.Contains("plain file name", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_source_set_is_refused()
        => Assert.Throws<InvalidDataException>(() => JsonSchemaSubset.FromSources(new Dictionary<string, string>()));

    [Fact]
    public void A_missing_directory_is_refused()
        => Assert.Throws<DirectoryNotFoundException>(() => JsonSchemaSubset.LoadDirectory(Path.Combine(Path.GetTempPath(), "gridsim-no-such-schema-dir-" + Guid.NewGuid())));

    // ----- $ref resolution -----

    [Fact]
    public void Local_and_cross_file_refs_resolve()
    {
        JsonSchemaSubset set = Schemas(
            ("common.schema.json", """{ "$defs": { "quantity": { "type": "number" }, "unit": { "enum": ["MW"] } } }"""),
            ("main.schema.json", """
                {
                  "$defs": { "local": { "type": "string" } },
                  "properties": {
                    "a": { "$ref": "#/$defs/local" },
                    "b": { "$ref": "common.schema.json#/$defs/quantity" },
                    "c": { "$ref": "common.schema.json#/$defs/unit" },
                    "d": { "$ref": "common.schema.json" },
                    "e": { "$ref": "#" }
                  }
                }
                """));
        Assert.Equal(5, set.RefCount);
        Assert.Equal(new[] { "common.schema.json", "main.schema.json" }, set.Files);
    }

    [Theory]
    [InlineData("""{ "$ref": "#/$defs/missing" }""", "no schema at '/$defs/missing'")]
    [InlineData("""{ "$ref": "other.schema.json#/$defs/x" }""", "names file 'other.schema.json'")]
    [InlineData("""{ "$ref": "#anchor" }""", "anchors are not supported")]
    [InlineData("""{ "$defs": { "e": { "enum": [ { "type": "string" } ] } }, "$ref": "#/$defs/e/enum/0" }""", "does not resolve to a schema")]
    [InlineData("""{ "properties": { "a": { "type": "string" } }, "$ref": "#/properties" }""", "does not resolve to a schema")]
    public void A_ref_that_does_not_land_on_a_schema_is_refused(string schema, string expected)
    {
        InvalidDataException ex = LoadFails(("x.schema.json", schema));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.Contains("x.schema.json at", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_ref_into_another_file_with_a_missing_def_is_refused_naming_both_files()
    {
        InvalidDataException ex = LoadFails(
            ("common.schema.json", """{ "$defs": { "a": {} } }"""),
            ("main.schema.json", """{ "properties": { "q": { "$ref": "common.schema.json#/$defs/b" } } }"""));
        Assert.Contains("main.schema.json at '/properties/q'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("in 'common.schema.json'", ex.Message, StringComparison.Ordinal);
    }

    // ----- $ref cycles -----

    [Fact]
    public void A_schema_that_refers_to_itself_in_place_is_a_cycle()
    {
        InvalidDataException ex = LoadFails(("x.schema.json", """{ "$ref": "#" }"""));
        Assert.Contains("$ref cycle that never consumes input", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_two_def_cycle_is_refused()
    {
        InvalidDataException ex = LoadFails(("x.schema.json", """
            { "$defs": { "a": { "$ref": "#/$defs/b" }, "b": { "$ref": "#/$defs/a" } } }
            """));
        Assert.Contains("$ref cycle", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'/$defs/a'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'/$defs/b'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cycle_across_files_is_refused()
    {
        InvalidDataException ex = LoadFails(
            ("a.schema.json", """{ "$defs": { "x": { "$ref": "b.schema.json#/$defs/y" } } }"""),
            ("b.schema.json", """{ "$defs": { "y": { "$ref": "a.schema.json#/$defs/x" } } }"""));
        Assert.Contains("$ref cycle", ex.Message, StringComparison.Ordinal);
        Assert.Contains("a.schema.json at '/$defs/x'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("b.schema.json at '/$defs/y'", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "$defs": { "a": { "anyOf": [ { "type": "null" }, { "$ref": "#/$defs/a" } ] } } }""")]
    [InlineData("""{ "$defs": { "a": { "oneOf": [ { "$ref": "#/$defs/a" } ] } } }""")]
    [InlineData("""{ "$defs": { "a": { "if": { "$ref": "#/$defs/a" } } } }""")]
    [InlineData("""{ "$defs": { "a": { "if": {}, "then": { "$ref": "#/$defs/a" } } } }""")]
    [InlineData("""{ "$defs": { "a": { "if": {}, "else": { "$ref": "#/$defs/a" } } } }""")]
    public void A_cycle_through_a_combinator_or_conditional_is_refused(string schema)
    {
        InvalidDataException ex = LoadFails(("x.schema.json", schema));
        Assert.Contains("$ref cycle that never consumes input", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_recursive_tree_through_properties_or_items_is_not_a_cycle()
    {
        // Each recursion steps into a child value, so validation is bounded by the document's depth.
        JsonSchemaSubset set = Schemas(("tree.schema.json", """
            {
              "$defs": {
                "node": {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["v"],
                  "properties": {
                    "v": { "type": "number" },
                    "children": { "type": "array", "items": { "$ref": "#/$defs/node" } },
                    "next": { "$ref": "#/$defs/node" }
                  }
                },
                "bag": { "additionalProperties": { "$ref": "#/$defs/bag" } }
              },
              "$ref": "#/$defs/node"
            }
            """));
        Assert.Empty(Validate(set, "tree.schema.json", """{ "v": 1, "children": [ { "v": 2 }, { "v": 3, "next": { "v": 4 } } ] }"""));
        Assert.Single(Validate(set, "tree.schema.json", """{ "v": 1, "children": [ { "v": 2, "next": { "v": "x" } } ] }"""));
    }

    [Theory]
    [InlineData("^a$", "^a\\z")]
    [InlineData("a\\$", "a\\$")]
    [InlineData("[$]", "[$]")]
    [InlineData("[\\]$]x$", "[\\]$]x\\z")]
    [InlineData("^(a|b)$|^c$", "^(a|b)\\z|^c\\z")]
    public void Dollar_anchors_are_rewritten_to_absolute_end_outside_classes(string pattern, string expected)
        => Assert.Equal(expected, JsonSchemaSubset.ToDotNetAnchors(pattern));
}
