using System.Text.Json;
using GridSim.Core.Diagnostics;
using GridSim.Core.IO.Json.Schema;
using Xunit;
using static GridSim.Tests.V2.V2TestSupport;

namespace GridSim.Tests.V2;

/// <summary>
/// The real schemas/v2 tree is well formed: every file parses, every schema uses only the supported keywords,
/// every $ref resolves, and every kind file loads. Written to pass whatever set of kind files is present (the
/// slice 3 kind specs land separately), so nothing here names a kind other than 'site'.
/// </summary>
public class V2SchemaWellFormedTests
{
    [Fact]
    public void Every_file_under_schemas_v2_parses()
    {
        string[] files = Directory.EnumerateFiles(SchemasDir, "*.json", SearchOption.AllDirectories).ToArray();
        Assert.NotEmpty(files);
        foreach (string f in files)
        {
            using JsonDocument doc = JsonSchemaSubset.ParseFile(f);   // throws naming the file
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        }
    }

    [Fact]
    public void Every_file_under_schemas_v2_is_either_a_schema_or_a_kind_spec()
    {
        // A stray file (say a typo'd 'common.schema.jsn' or a kind spec at the top level) would be silently skipped
        // by both loaders. Refuse it here instead.
        foreach (string f in Directory.EnumerateFiles(SchemasDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(SchemasDir, f).Replace('\\', '/');
            bool schema = !rel.Contains('/', StringComparison.Ordinal) && rel.EndsWith(JsonSchemaSubset.SchemaFileSuffix, StringComparison.Ordinal);
            bool kind = rel.StartsWith("kinds/", StringComparison.Ordinal) && rel.Count(c => c == '/') == 1 &&
                        rel.EndsWith(KindSpecRegistry.KindFileSuffix, StringComparison.Ordinal);
            // Test changed 2026-10-20 (Phase 5a): the asset overlay kind specs live one level down, in exactly
            // kinds/overlay/<kind>.kind.json (loaded apart from the template kinds by OverlayKindSet). Before: only
            // top-level schemas and kinds/<kind>.kind.json were admitted. After: those, plus kind specs in that one
            // folder; any other subfolder or file name is still refused, and AssetOverlayKindTests pins that folder's
            // exact contents.
            bool overlayKind = rel.StartsWith("kinds/overlay/", StringComparison.Ordinal) && rel.Count(c => c == '/') == 2 &&
                               rel.EndsWith(KindSpecRegistry.KindFileSuffix, StringComparison.Ordinal);
            Assert.True(schema || kind || overlayKind, $"unexpected file under schemas/v2: {rel}");
        }
    }

    [Fact]
    public void The_schema_set_loads_with_only_supported_keywords_and_every_ref_resolved()
    {
        // LoadDirectory refuses unsupported keywords, unresolved refs and in-place ref cycles, so loading is the test.
        JsonSchemaSubset set = RealSchemas();
        Assert.Contains("common.schema.json", set.Files);
        Assert.Contains("library.schema.json", set.Files);
        Assert.Contains("kind.schema.json", set.Files);
        Assert.True(set.RefCount > 0);
    }

    [Fact]
    public void Every_schema_file_declares_the_2020_12_dialect_and_its_own_id()
    {
        foreach (string f in Directory.EnumerateFiles(SchemasDir, "*" + JsonSchemaSubset.SchemaFileSuffix))
        {
            using JsonDocument doc = JsonSchemaSubset.ParseFile(f);
            Assert.Equal(JsonSchemaSubset.Draft202012, doc.RootElement.GetProperty("$schema").GetString());
            Assert.Equal(Path.GetFileName(f), doc.RootElement.GetProperty("$id").GetString());
        }
    }

    [Fact]
    public void Every_kind_file_present_loads_and_the_site_kind_is_among_them()
    {
        KindSpecRegistry kinds = KindSpecRegistry.LoadDirectory(KindsDir, RealSchemas());
        int files = Directory.EnumerateFiles(KindsDir, "*" + KindSpecRegistry.KindFileSuffix).Count();
        Assert.Equal(files, kinds.Kinds.Count);
        Assert.True(kinds.Contains("site"));
    }

    [Fact]
    public void Every_file_written_by_this_work_is_plain_ascii()
    {
        // House rule: ASCII only. Applies to the whole schemas/v2 tree (and the library data, checked here too).
        var files = Directory.EnumerateFiles(SchemasDir, "*.json", SearchOption.AllDirectories).ToList();
        if (Directory.Exists(LibraryDir)) files.AddRange(Directory.EnumerateFiles(LibraryDir, "*.json", SearchOption.AllDirectories));
        foreach (string f in files)
        {
            byte[] bytes = File.ReadAllBytes(f);
            int bad = Array.FindIndex(bytes, b => b > 0x7E || (b < 0x20 && b != (byte)'\n' && b != (byte)'\r' && b != (byte)'\t'));
            Assert.True(bad < 0, $"{f}: non-ASCII or control byte at offset {bad}");
        }
    }

    // ----- common.schema.json contents -----

    [Fact]
    public void The_unit_vocabulary_is_exactly_the_closed_plan_list()
    {
        string[] plan = { "1", "pu@system", "pu@machine", "pu@transformer", "s", "Hz", "Hz/s", "MW", "MVA", "Mvar", "kV", "km",
                          "ohm/km", "uS/km", "%", "MWh", "MW/min", "MW/Hz/MW", "degC", "deg",
                          // Moved 2026-10 (Q24, authorised): five units appended for the seven former unit-gap
                          // keys. Before: the twenty units above. After: those twenty plus these five.
                          "MW.s/Hz", "MW/Hz", "MW/s/MW", "V", "min",
                          // Test changed 2026-10-20 (Phase 5a): six units appended for the asset overlay kinds
                          // (schemas/v2/kinds/overlay): metres, millimetres, square millimetres, amperes, ohms and ohm
                          // metres. Before: the twenty-five units above. After: those twenty-five plus these six, in
                          // this order. The assertion is unchanged (the exact list, in order).
                          "m", "mm", "mm2", "A", "ohm", "ohm.m" };
        using JsonDocument doc = JsonSchemaSubset.ParseFile(Path.Combine(SchemasDir, "common.schema.json"));
        string[] units = doc.RootElement.GetProperty("$defs").GetProperty("unit").GetProperty("enum")
            .EnumerateArray().Select(u => u.GetString()!).ToArray();
        Assert.Equal(plan, units);
    }

    [Fact]
    public void The_provenance_tags_are_exactly_the_ProvenanceTag_names()
    {
        using JsonDocument doc = JsonSchemaSubset.ParseFile(Path.Combine(SchemasDir, "common.schema.json"));
        string[] tags = doc.RootElement.GetProperty("$defs").GetProperty("provenanceTag").GetProperty("enum")
            .EnumerateArray().Select(u => u.GetString()!).ToArray();
        Assert.Equal(Enum.GetNames<ProvenanceTag>(), tags);
    }

    private static IReadOnlyList<SchemaError> AgainstDef(string def, string json)
    {
        // Validate against one $defs entry of the real common.schema.json through a one-line wrapper schema.
        string commonText = File.ReadAllText(Path.Combine(SchemasDir, "common.schema.json"));
        JsonSchemaSubset set = Schemas(("common.schema.json", commonText),
                                       ("probe.schema.json", "{ \"$ref\": \"common.schema.json#/$defs/" + def + "\" }"));
        return Validate(set, "probe.schema.json", json);
    }

    [Theory]
    [InlineData("""{ "tag": "CuratedPlaceholder", "note": "from a datasheet [VERIFY]" }""", true)]
    [InlineData("""{ "tag": "CuratedPlaceholder", "note": "from a datasheet" }""", false)]
    [InlineData("""{ "tag": "CuratedPlaceholder", "note": "[verify]" }""", false)]
    [InlineData("""{ "tag": "Recorded", "note": "NESO published setting" }""", true)]
    [InlineData("""{ "tag": "recorded", "note": "case matters in v2" }""", false)]
    [InlineData("""{ "tag": "Recorded" }""", false)]
    [InlineData("""{ "tag": "Recorded", "note": "n", "source": "x" }""", false)]
    public void Provenance_carries_the_VERIFY_rule_for_curated_placeholders(string json, bool ok)
        => Assert.Equal(ok, AgainstDef("provenance", json).Count == 0);

    [Theory]
    [InlineData("4.5", true)]
    [InlineData("""{ "v": 4.5 }""", true)]
    [InlineData("""{ "v": 4.5, "unit": "s", "prov": { "tag": "Synthetic", "note": "n" } }""", true)]
    [InlineData("""{ "v": 4.5, "unit": "seconds" }""", false)]
    [InlineData("""{ "unit": "s" }""", false)]
    [InlineData("""{ "v": "4.5" }""", false)]
    [InlineData("""{ "v": 4.5, "u": "s" }""", false)]
    [InlineData("null", false)]
    public void Quantity_is_a_number_or_v_unit_prov(string json, bool ok)
        => Assert.Equal(ok, AgainstDef("quantity", json).Count == 0);

    [Theory]
    [InlineData("\"gridsim.exciter.sexs@1\"", true)]
    [InlineData("\"gridsim.placeholder.site.Nuclear-Sub@12\"", true)]
    [InlineData("\"gridsim.exciter.sexs\"", false)]
    [InlineData("\"gridsim.exciter.sexs@0\"", false)]
    [InlineData("\"gridsim.exciter.sexs@01\"", false)]
    [InlineData("\"other.exciter@1\"", false)]
    [InlineData("\"gridsim..x@1\"", false)]
    [InlineData("\"gridsim.x@1\\n\"", false)]
    [InlineData("\"gridsim.x y@1\"", false)]
    public void Template_ref_is_id_at_rev(string json, bool ok)
        => Assert.Equal(ok, AgainstDef("templateRef", json).Count == 0);

    [Theory]
    [InlineData("\"active\"", true)]
    [InlineData("\"placeholder\"", true)]
    [InlineData("\"Active\"", false)]
    [InlineData("\"draft\"", false)]
    public void Status_is_active_or_placeholder(string json, bool ok)
        => Assert.Equal(ok, AgainstDef("status", json).Count == 0);
}
