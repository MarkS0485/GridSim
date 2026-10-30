using System.Text.Json;
using GridSim.Core.IO.Json.Schema;
using Xunit;
using static GridSim.Tests.V2.V2TestSupport;

namespace GridSim.Tests.V2;

/// <summary>
/// The placeholder rules of the v2 template library, on fixtures under tests/GridSim.Tests/fixtures/v2/ (never on
/// the shipped unmodelled-sites.json, which must stay strippable): a placeholder with a non-null value fails, an
/// active template with a null value fails, and a placeholder used as a controller target fails. Each failing
/// fixture has a passing positive control, so a check that simply refused everything could not pass.
///
/// The site fixtures use the real schemas/v2/kinds/site.kind.json. The controller fixtures use the minimal
/// stand-in kinds in fixtures/v2/kinds, so they do not depend on the real machine and exciter kind specs.
/// </summary>
public class V2PlaceholderRuleTests
{
    private static IReadOnlyList<SchemaError> CheckFixture(string fixture, string kindsDir)
    {
        JsonSchemaSubset schemas = RealSchemas();
        KindSpecRegistry kinds = KindSpecRegistry.LoadDirectory(kindsDir, schemas);
        JsonElement root = ReadRoot(Path.Combine(FixturesDir, fixture));
        return V2LibraryChecker.Check(new[] { (fixture, root) }, new SchemaValidator(schemas), kinds);
    }

    private static IReadOnlyList<SchemaError> CheckSiteFixture(string fixture) => CheckFixture(fixture, KindsDir);

    private static IReadOnlyList<SchemaError> CheckControllerFixture(string fixture)
        => CheckFixture(fixture, Path.Combine(FixturesDir, "kinds"));

    [Fact]
    public void The_valid_site_fixture_passes()
    {
        IReadOnlyList<SchemaError> errors = CheckSiteFixture("valid-sites.library.json");
        Assert.True(errors.Count == 0, Dump(errors));
    }

    [Fact]
    public void A_placeholder_with_a_non_null_value_fails()
    {
        IReadOnlyList<SchemaError> errors = CheckSiteFixture("placeholder-with-value.library.json");
        Assert.NotEmpty(errors);
        // Caught by the schema's if/then on status, at the offending value, and named with the file.
        Assert.Contains(errors, e => e.Pointer == "/templates/0/params/generation" && e.File == "placeholder-with-value.library.json");
        Assert.Contains(errors, e => e.Message.Contains("expected type null", StringComparison.Ordinal));
    }

    [Fact]
    public void An_active_template_with_a_null_value_fails()
    {
        IReadOnlyList<SchemaError> errors = CheckSiteFixture("active-with-null.library.json");
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Pointer == "/templates/0/params/demand" && e.File == "active-with-null.library.json");
    }

    [Fact]
    public void A_placeholder_controller_slot_must_be_null_too()
    {
        JsonSchemaSubset schemas = RealSchemas();
        using JsonDocument doc = JsonDocument.Parse("""
            { "schema": "gridsim-library/2", "templates": [ {
                "id": "gridsim.fixture.m", "rev": 1, "kind": "synchronousMachine", "status": "placeholder", "title": "M",
                "params": { "h": null }, "controllers": { "exciter": "gridsim.fixture.x@1" },
                "provenance": { "_default": { "tag": "CuratedPlaceholder", "note": "[VERIFY]" } } } ] }
            """);
        IReadOnlyList<SchemaError> errors = new SchemaValidator(schemas).Validate(doc.RootElement, V2LibraryChecker.LibrarySchemaFile);
        Assert.Contains(errors, e => e.Pointer == "/templates/0/controllers/exciter");
    }

    [Fact]
    public void An_active_controller_target_passes()
    {
        IReadOnlyList<SchemaError> errors = CheckControllerFixture("active-controller-target.library.json");
        Assert.True(errors.Count == 0, Dump(errors));
    }

    [Fact]
    public void A_placeholder_used_as_a_controller_target_fails()
    {
        IReadOnlyList<SchemaError> errors = CheckControllerFixture("placeholder-controller-target.library.json");
        SchemaError e = Assert.Single(errors);
        Assert.Equal("/templates/0/controllers/exciter", e.Pointer);
        Assert.Equal("placeholder-controller-target.library.json", e.File);
        Assert.Contains("can never be a controller target", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_placeholder_controller_target_in_another_file_fails_too()
    {
        // The rule spans files: split the failing fixture's two templates across two documents.
        JsonSchemaSubset schemas = RealSchemas();
        KindSpecRegistry kinds = KindSpecRegistry.LoadDirectory(Path.Combine(FixturesDir, "kinds"), schemas);
        JsonElement both = ReadRoot(Path.Combine(FixturesDir, "placeholder-controller-target.library.json"));
        JsonElement machine = Single(both.GetProperty("templates")[0]);
        JsonElement exciter = Single(both.GetProperty("templates")[1]);
        IReadOnlyList<SchemaError> errors = V2LibraryChecker.Check(
            new[] { ("machines.json", machine), ("exciters.json", exciter) }, new SchemaValidator(schemas), kinds);
        SchemaError e = Assert.Single(errors);
        Assert.Equal("machines.json", e.File);
        Assert.Contains("exciters.json", e.Message, StringComparison.Ordinal);
    }

    private static JsonElement Single(JsonElement template)
    {
        using JsonDocument d = JsonDocument.Parse("{ \"schema\": \"gridsim-library/2\", \"templates\": [" + template.GetRawText() + "] }");
        return d.RootElement.Clone();
    }

    [Fact]
    public void A_controller_ref_that_resolves_nowhere_fails()
    {
        IReadOnlyList<SchemaError> errors = CheckWithControllerKinds("""
            { "id": "gridsim.fixture.machine", "rev": 1, "kind": "synchronousMachine", "status": "active", "title": "M",
              "params": { "h": 4 }, "controllers": { "exciter": "gridsim.fixture.nowhere@1" },
              "provenance": { "_default": { "tag": "Synthetic", "note": "n" } } }
            """);
        Assert.Contains("does not resolve to any template", Assert.Single(errors).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_controller_slot_pointing_at_the_wrong_kind_fails()
    {
        IReadOnlyList<SchemaError> errors = CheckWithControllerKinds("""
            { "id": "gridsim.fixture.machine", "rev": 1, "kind": "synchronousMachine", "status": "active", "title": "M",
              "params": { "h": 4 }, "controllers": { "governor": "gridsim.fixture.other@1" },
              "provenance": { "_default": { "tag": "Synthetic", "note": "n" } } },
            { "id": "gridsim.fixture.other", "rev": 1, "kind": "synchronousMachine", "status": "active", "title": "O",
              "params": { "h": 2 }, "provenance": { "_default": { "tag": "Synthetic", "note": "n" } } }
            """);
        Assert.Contains("must reference a 'governor' template", Assert.Single(errors).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_duplicate_id_at_rev_fails()
    {
        IReadOnlyList<SchemaError> errors = CheckWithControllerKinds("""
            { "id": "gridsim.fixture.machine", "rev": 1, "kind": "synchronousMachine", "status": "active", "title": "M",
              "params": { "h": 4 }, "provenance": { "_default": { "tag": "Synthetic", "note": "n" } } },
            { "id": "gridsim.fixture.machine", "rev": 1.0, "kind": "synchronousMachine", "status": "active", "title": "M again",
              "params": { "h": 5 }, "provenance": { "_default": { "tag": "Synthetic", "note": "n" } } }
            """);
        SchemaError e = Assert.Single(errors);
        Assert.Equal("/templates/1/id", e.Pointer);
        Assert.Contains("gridsim.fixture.machine@1 is declared twice", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_provenance_key_that_is_not_a_parameter_fails()
    {
        IReadOnlyList<SchemaError> errors = CheckWithControllerKinds("""
            { "id": "gridsim.fixture.machine", "rev": 1, "kind": "synchronousMachine", "status": "active", "title": "M",
              "params": { "h": 4 },
              "provenance": { "_default": { "tag": "Synthetic", "note": "n" }, "H": { "tag": "Recorded", "note": "typo'd key" } } }
            """);
        SchemaError e = Assert.Single(errors);
        Assert.Equal("/templates/0/provenance/H", e.Pointer);
    }

    [Fact]
    public void A_template_of_an_unknown_kind_fails()
    {
        IReadOnlyList<SchemaError> errors = CheckWithControllerKinds("""
            { "id": "gridsim.fixture.ghost", "rev": 1, "kind": "ghost", "status": "active", "title": "G",
              "params": {}, "provenance": { "_default": { "tag": "Synthetic", "note": "n" } } }
            """);
        Assert.Contains("unknown kind 'ghost'", Assert.Single(errors).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Schema_errors_are_reported_before_semantic_checks_run()
    {
        // A wrong document tag is a shape error; the (otherwise failing) semantic layer is not run on top of it.
        IReadOnlyList<SchemaError> errors = CheckWithControllerKinds("""
            { "id": "gridsim.fixture.ghost", "rev": 1, "kind": "ghost", "status": "active", "title": "G",
              "params": {}, "provenance": { "_default": { "tag": "Synthetic", "note": "n" } } }
            """, tag: "gridsim-library/1");
        Assert.Equal("/schema", Assert.Single(errors).Pointer);
    }

    [Fact]
    public void Validate_files_or_throw_names_each_file()
    {
        JsonSchemaSubset schemas = RealSchemas();
        KindSpecRegistry kinds = KindSpecRegistry.LoadDirectory(KindsDir, schemas);
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => V2LibraryChecker.ValidateFilesOrThrow(
            new[] { Path.Combine(FixturesDir, "valid-sites.library.json"), Path.Combine(FixturesDir, "active-with-null.library.json") },
            new SchemaValidator(schemas), kinds));
        Assert.Contains("active-with-null.library.json: /templates/0/params/demand", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("valid-sites.library.json:", ex.Message, StringComparison.Ordinal);
    }

    private static IReadOnlyList<SchemaError> CheckWithControllerKinds(string templates, string tag = "gridsim-library/2")
    {
        JsonSchemaSubset schemas = RealSchemas();
        KindSpecRegistry kinds = KindSpecRegistry.LoadDirectory(Path.Combine(FixturesDir, "kinds"), schemas);
        using JsonDocument doc = JsonDocument.Parse("{ \"schema\": \"" + tag + "\", \"templates\": [" + templates + "] }");
        return V2LibraryChecker.Check(new[] { ("inline.json", doc.RootElement.Clone()) }, new SchemaValidator(schemas), kinds);
    }
}
