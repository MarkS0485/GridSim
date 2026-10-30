using System.Text.Json;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Overlay;
using GridSim.Core.IO.Json.Schema;
using Xunit;
using static GridSim.Tests.V2.V2TestSupport;

namespace GridSim.Tests.V2;

/// <summary>
/// Phase 5a (2026-10-20): the asset overlay schemas. assets.schema.json (gridsim-assets/2) on valid and invalid
/// documents, each invalid one failing at the expected pointer for the expected reason; the case manifest's optional
/// "assets" member (with and without it); the overlay block of kind.schema.json; the new document tag; and the
/// <c>validate</c> engine (<see cref="V2DocumentValidator"/>) pulling a manifest's overlay files in and resolving them
/// against its network files.
/// </summary>
public sealed class AssetOverlaySchemaTests
{
    private static readonly Lazy<SchemaValidator> Validator = new(() => new SchemaValidator(RealSchemas()));

    private static IReadOnlyList<SchemaError> Validate(string schemaFile, string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json, JsonSchemaSubset.ReaderOptions);
        return Validator.Value.Validate(doc.RootElement.Clone(), schemaFile);
    }

    private const string GoodRecord = """{ "id": "sub.1", "kind": "substation", "parent": "x", "refs": { "a": { "bus": 1 }, "b": { "line": "L" }, "c": { "transformer": "T" }, "d": { "generator": "G" }, "e": { "demand": "D" }, "f": { "record": "r-2:x" } }, "params": { "n": 1.5, "s": "text", "b": true, "o": { "v": 2, "unit": "m", "prov": { "tag": "Recorded", "note": "survey" } } }, "prov": { "tag": "CuratedPlaceholder", "note": "from a drawing [VERIFY]" }, "note": "free text" }""";

    private static string Doc(string record) => "{ \"schema\": \"gridsim-assets/2\", \"description\": \"d\", \"records\": [ " + record + " ] }";

    [Fact]
    public void The_schema_set_and_the_embedded_set_include_the_assets_schema()
    {
        Assert.Contains(V2AssetChecker.SchemaFile, RealSchemas().Files);
        Assert.Contains(V2AssetChecker.SchemaFile, BundledSchemas.V2().Files);
        Assert.Equal("assets.schema.json", V2AssetChecker.SchemaFile);
        Assert.Equal("gridsim-assets/2", V2AssetChecker.SchemaTag);
    }

    [Fact]
    public void The_validator_knows_the_new_document_tag_and_still_knows_every_old_one()
    {
        Assert.Contains("gridsim-assets/2", V2DocumentValidator.KnownTags);
        foreach (string old in new[] { "gridsim-case/2", "gridsim-network/2", "gridsim-library/2", "gridsim-kind/2", "gridsim-dynamics/2", "gridsim-dist3ph/2" })
            Assert.Contains(old, V2DocumentValidator.KnownTags);
        Assert.Equal(7, V2DocumentValidator.KnownTags.Count);
    }

    [Fact]
    public void A_record_using_every_member_and_every_reference_type_is_schema_valid()
    {
        // The schema alone: the kind, roles and parent are the checker's business, not the schema's.
        IReadOnlyList<SchemaError> errors = Validate("assets.schema.json", Doc(GoodRecord));
        Assert.True(errors.Count == 0, Dump(errors));
        Assert.Empty(Validate("assets.schema.json", """{ "schema": "gridsim-assets/2", "records": [] }"""));
    }

    [Theory]
    [InlineData("""{ "schema": "gridsim-assets/2" }""", "", "records")]
    [InlineData("""{ "schema": "gridsim-assets/3", "records": [] }""", "/schema", "gridsim-assets/2")]
    [InlineData("""{ "schema": "gridsim-assets/2", "records": [], "notes": "x" }""", "/notes", "unknown member")]
    [InlineData("""{ "schema": "gridsim-assets/2", "records": [ { "kind": "span", "refs": {}, "params": {} } ] }""", "/records/0", "id")]
    [InlineData("""{ "schema": "gridsim-assets/2", "records": [ { "id": "x", "refs": {}, "params": {} } ] }""", "/records/0", "kind")]
    [InlineData("""{ "schema": "gridsim-assets/2", "records": [ { "id": "x", "kind": "span", "params": {} } ] }""", "/records/0", "refs")]
    [InlineData("""{ "schema": "gridsim-assets/2", "records": [ { "id": "x", "kind": "span", "refs": {} } ] }""", "/records/0", "params")]
    [InlineData("""{ "schema": "gridsim-assets/2", "records": [ { "id": "x", "kind": "span", "refs": {}, "params": {}, "children": [] } ] }""", "/records/0/children", "unknown member")]
    [InlineData("""{ "schema": "gridsim-assets/2", "records": [ { "id": "-x", "kind": "span", "refs": {}, "params": {} } ] }""", "/records/0/id", "pattern")]
    [InlineData("""{ "schema": "gridsim-assets/2", "records": [ { "id": "x y", "kind": "span", "refs": {}, "params": {} } ] }""", "/records/0/id", "pattern")]
    [InlineData("""{ "schema": "gridsim-assets/2", "records": [ { "id": "x", "kind": "Span", "refs": {}, "params": {} } ] }""", "/records/0/kind", "pattern")]
    [InlineData("""{ "schema": "gridsim-assets/2", "records": [ { "id": "x", "kind": "span", "parent": "", "refs": {}, "params": {} } ] }""", "/records/0/parent", "pattern")]
    [InlineData("""{ "schema": "gridsim-assets/2", "records": [ { "id": "x", "kind": "span", "refs": { "a": { "bus": 1, "line": "L" } }, "params": {} } ] }""", "/records/0/refs/a", "")]
    [InlineData("""{ "schema": "gridsim-assets/2", "records": [ { "id": "x", "kind": "span", "refs": { "a": { "pylon": "P" } }, "params": {} } ] }""", "/records/0/refs/a", "")]
    [InlineData("""{ "schema": "gridsim-assets/2", "records": [ { "id": "x", "kind": "span", "refs": { "a": { "bus": "1" } }, "params": {} } ] }""", "/records/0/refs/a", "")]
    [InlineData("""{ "schema": "gridsim-assets/2", "records": [ { "id": "x", "kind": "span", "refs": { "a": { "line": " " } }, "params": {} } ] }""", "/records/0/refs/a", "")]
    [InlineData("""{ "schema": "gridsim-assets/2", "records": [ { "id": "x", "kind": "span", "refs": { "a": "L" }, "params": {} } ] }""", "/records/0/refs/a", "")]
    [InlineData("""{ "schema": "gridsim-assets/2", "records": [ { "id": "x", "kind": "span", "refs": {}, "params": { "p": [1, 2] } } ] }""", "/records/0/params/p", "")]
    [InlineData("""{ "schema": "gridsim-assets/2", "records": [ { "id": "x", "kind": "span", "refs": {}, "params": { "p": null } } ] }""", "/records/0/params/p", "")]
    [InlineData("""{ "schema": "gridsim-assets/2", "records": [ { "id": "x", "kind": "span", "refs": {}, "params": { "p": { "v": 1, "unit": "metres" } } } ] }""", "/records/0/params/p", "")]
    [InlineData("""{ "schema": "gridsim-assets/2", "records": [ { "id": "x", "kind": "span", "refs": {}, "params": {}, "prov": { "tag": "CuratedPlaceholder", "note": "guess" } } ] }""", "/records/0/prov/note", "pattern")]
    public void An_invalid_overlay_document_fails_at_the_expected_place(string json, string pointer, string fragment)
    {
        IReadOnlyList<SchemaError> errors = Validate("assets.schema.json", json);
        Assert.True(errors.Any(e => e.Pointer == pointer && e.Message.Contains(fragment, StringComparison.Ordinal)),
            $"expected an error at '{pointer}' containing '{fragment}', got:{Environment.NewLine}{Dump(errors)}");
    }

    // ----- the case manifest -----

    private const string Manifest = """{ "schema": "gridsim-case/2", "name": "n", "system": { "baseMva": 100, "frequencyHz": 50 }, "network": ["network.json"] }""";

    [Fact]
    public void A_manifest_with_and_without_assets_is_schema_valid()
    {
        Assert.Empty(Validate("case.schema.json", Manifest));
        Assert.Empty(Validate("case.schema.json", Manifest[..^1] + """, "assets": ["site.assets.json", "more/lines.assets.json"] }"""));
    }

    [Theory]
    [InlineData("""[]""", "/assets", "")]
    [InlineData("""["/abs.assets.json"]""", "/assets/0", "pattern")]
    [InlineData("""["a\\b.json"]""", "/assets/0", "pattern")]
    [InlineData("""["site.assets.txt"]""", "/assets/0", "pattern")]
    [InlineData("\"site.assets.json\"", "/assets", "array")]
    public void A_bad_assets_member_fails_at_the_expected_place(string assets, string pointer, string fragment)
    {
        IReadOnlyList<SchemaError> errors = Validate("case.schema.json", Manifest[..^1] + ", \"assets\": " + assets + " }");
        Assert.True(errors.Any(e => e.Pointer == pointer && e.Message.Contains(fragment, StringComparison.Ordinal)),
            $"expected an error at '{pointer}' containing '{fragment}', got:{Environment.NewLine}{Dump(errors)}");
    }

    // ----- the overlay block of a kind spec -----

    private const string Kind = """{ "schema": "gridsim-kind/2", "kind": "k", "title": "K", "description": "d", "params": {}, "overlay": OVERLAY }""";

    [Fact]
    public void A_kind_with_and_without_an_overlay_block_is_schema_valid()
    {
        Assert.Empty(Validate("kind.schema.json", Kind.Replace(", \"overlay\": OVERLAY", "", StringComparison.Ordinal)));
        Assert.Empty(Validate("kind.schema.json", Kind.Replace("OVERLAY",
            """{ "refs": { "a": { "target": "record", "required": true, "recordKinds": ["k"], "description": "x" }, "b": { "target": "bus", "required": false, "description": "y" } }, "parentKinds": ["k"], "parentRequired": true }""", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("""{ "parentKinds": [] }""", "/overlay", "refs")]
    [InlineData("""{ "refs": {} }""", "/overlay", "parentKinds")]
    [InlineData("""{ "refs": {}, "parentKinds": [], "children": [] }""", "/overlay/children", "unknown member")]
    [InlineData("""{ "refs": { "a": { "target": "pylon", "required": true, "description": "x" } }, "parentKinds": [] }""", "/overlay/refs/a/target", "")]
    [InlineData("""{ "refs": { "a": { "target": "bus", "description": "x" } }, "parentKinds": [] }""", "/overlay/refs/a", "required")]
    [InlineData("""{ "refs": { "a": { "target": "record", "required": true, "recordKinds": [], "description": "x" } }, "parentKinds": [] }""", "/overlay/refs/a/recordKinds", "")]
    [InlineData("""{ "refs": {}, "parentKinds": ["Bad"] }""", "/overlay/parentKinds/0", "pattern")]
    public void A_bad_overlay_block_fails_at_the_expected_place(string overlay, string pointer, string fragment)
    {
        IReadOnlyList<SchemaError> errors = Validate("kind.schema.json", Kind.Replace("OVERLAY", overlay, StringComparison.Ordinal));
        Assert.True(errors.Any(e => e.Pointer == pointer && e.Message.Contains(fragment, StringComparison.Ordinal)),
            $"expected an error at '{pointer}' containing '{fragment}', got:{Environment.NewLine}{Dump(errors)}");
    }

    // ----- the validate engine -----

    private const string Network = """
        { "schema": "gridsim-network/2",
          "buses": [ { "id": 1, "baseKv": 400, "type": "slack" }, { "id": 2, "baseKv": 132 } ],
          "lines": [ { "name": "L", "from": 1, "to": 2, "rPu": 0, "xPu": 0.1 } ] }
        """;

    private const string GoodOverlay = """
        { "schema": "gridsim-assets/2", "records": [
          { "id": "route", "kind": "lineRoute", "refs": { "line": { "line": "L" } }, "params": {} },
          { "id": "e", "kind": "earthPath", "refs": { "bus": { "bus": 2 } }, "params": { "resistanceOhm": 1 } }
        ] }
        """;

    /// <summary>A case folder holding the manifest (with the given assets member), its network and overlay files.</summary>
    private static string CaseFolder(string folder, string assetsMember, params (string Path, string Text)[] files)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "case.json"), Manifest[..^1] + assetsMember + " }");
        File.WriteAllText(Path.Combine(folder, "network.json"), Network);
        foreach ((string p, string t) in files)
        {
            string full = Path.Combine(folder, p.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, t);
        }
        return Path.Combine(folder, "case.json");
    }

    private static string NewFolder() => Path.Combine(Path.GetTempPath(), "gridsim-p5a-" + Guid.NewGuid().ToString("N"));

    private static void Delete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void A_manifest_pulls_its_overlay_files_in_and_they_are_valid_against_its_network()
    {
        string dir = NewFolder();
        try
        {
            string manifest = CaseFolder(dir, ", \"assets\": [\"o/site.assets.json\"]", ("o/site.assets.json", GoodOverlay));
            IReadOnlyList<V2DocumentReport> reports = V2DocumentValidator.ValidateFiles(new[] { manifest }, SchemasDir);
            Assert.Equal(3, reports.Count);   // the manifest, its network file, its overlay file
            Assert.All(reports, r => Assert.True(r.Valid, r.Path + ": " + Dump(r.Errors)));
            Assert.Equal(new[] { "gridsim-case/2", "gridsim-network/2", "gridsim-assets/2" }, reports.Select(r => r.SchemaTag).ToArray());
            Assert.Equal(Path.Combine(dir, "o", "site.assets.json"), reports[2].Path);
        }
        finally { Delete(dir); }
    }

    [Fact]
    public void A_manifest_without_assets_reports_exactly_as_before()
    {
        string dir = NewFolder();
        try
        {
            string manifest = CaseFolder(dir, "", ("o/site.assets.json", GoodOverlay));   // an overlay file nobody lists
            IReadOnlyList<V2DocumentReport> reports = V2DocumentValidator.ValidateFiles(new[] { manifest }, SchemasDir);
            Assert.Equal(new[] { "gridsim-case/2", "gridsim-network/2" }, reports.Select(r => r.SchemaTag).ToArray());
            Assert.All(reports, r => Assert.True(r.Valid, r.Path + ": " + Dump(r.Errors)));
        }
        finally { Delete(dir); }
    }

    [Fact]
    public void An_overlay_reference_the_network_does_not_have_is_an_error_on_the_overlay_file()
    {
        string dir = NewFolder();
        try
        {
            string bad = GoodOverlay.Replace("\"line\": \"L\"", "\"line\": \"M\"", StringComparison.Ordinal);
            string manifest = CaseFolder(dir, ", \"assets\": [\"site.assets.json\"]", ("site.assets.json", bad));
            IReadOnlyList<V2DocumentReport> reports = V2DocumentValidator.ValidateFiles(new[] { manifest }, SchemasDir);
            Assert.True(reports[0].Valid && reports[1].Valid);
            Assert.Equal(new[] { "/records/0/refs/line/line: record 'route': role 'line' points at line 'M', which is not a line of the case." },
                reports[2].Errors.Select(e => e.ToString()).ToArray());
        }
        finally { Delete(dir); }
    }

    [Fact]
    public void An_overlay_file_given_with_its_manifest_is_resolved_against_that_manifest_and_reported_once()
    {
        string dir = NewFolder();
        try
        {
            string bad = GoodOverlay.Replace("\"bus\": 2", "\"bus\": 5", StringComparison.Ordinal);
            string manifest = CaseFolder(dir, ", \"assets\": [\"site.assets.json\"]", ("site.assets.json", bad));
            string overlay = Path.Combine(dir, "site.assets.json");
            IReadOnlyList<V2DocumentReport> reports = V2DocumentValidator.ValidateFiles(new[] { overlay, manifest }, SchemasDir);
            Assert.Equal(new[] { overlay, manifest, Path.Combine(dir, "network.json") }, reports.Select(r => r.Path).ToArray());
            Assert.Equal(new[] { "/records/1/refs/bus/bus: record 'e': role 'bus' points at bus 5, which is not a bus of the case." },
                reports[0].Errors.Select(e => e.ToString()).ToArray());
        }
        finally { Delete(dir); }
    }

    [Fact]
    public void A_lone_overlay_file_is_checked_without_a_case()
    {
        string dir = NewFolder();
        try
        {
            Directory.CreateDirectory(dir);
            string overlay = Path.Combine(dir, "lone.assets.json");
            File.WriteAllText(overlay, GoodOverlay.Replace("\"bus\": 2", "\"bus\": 5", StringComparison.Ordinal)
                .Replace("\"kind\": \"earthPath\"", "\"kind\": \"earthPath\", \"parent\": \"ghost\"", StringComparison.Ordinal));
            V2DocumentReport r = V2DocumentValidator.ValidateFiles(new[] { overlay }, SchemasDir).Single();
            // Bus 5 cannot be judged without a case; the missing parent can.
            Assert.Equal("gridsim-assets/2", r.SchemaTag);
            Assert.Equal(new[] { "/records/1/parent: record 'e': parent 'ghost' is not a record of the overlay." }, r.Errors.Select(e => e.ToString()).ToArray());
        }
        finally { Delete(dir); }
    }

    [Fact]
    public void A_missing_outside_or_doubly_listed_overlay_file_is_an_error_on_the_manifest()
    {
        string dir = NewFolder();
        try
        {
            string manifest = CaseFolder(Path.Combine(dir, "case"),
                ", \"assets\": [\"site.assets.json\", \"missing.assets.json\", \"../outside.assets.json\", \"site.assets.json\"]",
                ("site.assets.json", GoodOverlay));
            File.WriteAllText(Path.Combine(dir, "outside.assets.json"), "{ \"secret\": \"must not be quoted\" }");
            IReadOnlyList<V2DocumentReport> reports = V2DocumentValidator.ValidateFiles(new[] { manifest }, SchemasDir);
            string[] errors = reports[0].Errors.Select(e => e.Pointer + " " + e.Message).ToArray();
            Assert.Equal(3, errors.Length);
            Assert.StartsWith("/assets/1 asset overlay file 'missing.assets.json' does not exist", errors[0], StringComparison.Ordinal);
            Assert.StartsWith("/assets/2 asset overlay file '../outside.assets.json' lies outside the case folder", errors[1], StringComparison.Ordinal);
            Assert.Equal("/assets/3 asset overlay file 'site.assets.json' is listed more than once.", errors[2]);
            Assert.DoesNotContain(reports, r => r.Errors.Any(e => e.Message.Contains("must not be quoted", StringComparison.Ordinal)));
            Assert.All(reports.Skip(1), r => Assert.True(r.Valid, r.Path + ": " + Dump(r.Errors)));   // the listed file itself is fine
        }
        finally { Delete(dir); }
    }
}
