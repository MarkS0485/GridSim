using System.Text.Json;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Overlay;
using GridSim.Core.IO.Json.Schema;
using Xunit;
using static GridSim.Tests.V2.V2TestSupport;

namespace GridSim.Tests.V2;

/// <summary>
/// Phase 5a (2026-10-20): the asset overlay kind specs (schemas/v2/kinds/overlay, loaded by <see cref="OverlayKindSet"/>).
/// The folder holds exactly the thirteen overlay kinds and nothing else; each one's reference roles, parent rule and
/// parameter units are pinned; the set never overlaps the template kinds; the six new units are each used; the
/// embedded copies equal the files; and the loader's own rules (the ones the schema subset cannot express) bite.
/// </summary>
public sealed class AssetOverlayKindTests
{
    private static string OverlayDir => Path.Combine(KindsDir, OverlayKindSet.FolderName);

    private static OverlayKindSet Load() => OverlayKindSet.LoadDirectory(OverlayDir, RealSchemas());

    /// <summary>
    /// Every overlay kind with its parent rule and roles, in one line each: "kind: parent [kinds](required|optional) |
    /// role->target[recordKinds](required|optional) ...". Pinned so a contract change is a deliberate edit here.
    /// </summary>
    private static readonly string[] Contracts =
    {
        "busbarSection: parent [substation] required | bus->bus required",
        "conductorPosition: parent [towerType] required |",
        "conductorType: top level |",
        "earthPath: parent [substation] optional | bus->bus required, soil->record[soilZone] optional",
        "earthWire: parent [lineRoute] required | conductor->record[conductorType] optional",
        "hvdcLink: top level | sendingConverter->generator required, receivingConverter->generator optional",
        "lineRoute: top level | line->line required",
        "soilZone: top level |",
        "span: parent [lineRoute] required | conductor->record[conductorType] optional, tower->record[towerType] optional",
        "substation: top level |",
        "switchingDevice: parent [substation] required | from->record[busbarSection] required, to->record[busbarSection] optional, line->line optional, transformer->transformer optional, generator->generator optional, demand->demand optional",
        "towerType: top level |",
        "transformer3W: parent [substation] optional | starBus->bus required, hv->transformer required, mv->transformer required, lv->transformer required",
    };

    private static string Contract(OverlayKindSpec k)
    {
        string parent = k.ParentKinds.Count == 0 ? "top level"
            : $"parent [{string.Join(",", k.ParentKinds)}] {(k.ParentRequired ? "required" : "optional")}";
        string refs = string.Join(", ", k.Refs.Values.Select(r =>
            $"{r.Role}->{OverlayKindSet.TargetWord(r.Target)}{(r.RecordKinds.Count > 0 ? "[" + string.Join(",", r.RecordKinds) + "]" : "")} {(r.Required ? "required" : "optional")}"));
        return $"{k.Kind}: {parent} |{(refs.Length > 0 ? " " + refs : "")}";
    }

    [Fact]
    public void The_overlay_folder_holds_exactly_the_thirteen_overlay_kinds_and_nothing_else()
    {
        Assert.True(Directory.Exists(OverlayDir), $"overlay kinds folder missing: {OverlayDir}");
        Assert.Empty(Directory.GetDirectories(OverlayDir));
        string[] files = Directory.GetFiles(OverlayDir).Select(f => Path.GetFileName(f)!).OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[]
        {
            "busbarSection.kind.json", "conductorPosition.kind.json", "conductorType.kind.json", "earthPath.kind.json",
            "earthWire.kind.json", "hvdcLink.kind.json", "lineRoute.kind.json", "soilZone.kind.json", "span.kind.json",
            "substation.kind.json", "switchingDevice.kind.json", "towerType.kind.json", "transformer3W.kind.json",
        }, files);
    }

    [Fact]
    public void Every_overlay_kind_loads_with_exactly_its_pinned_roles_and_parent_rule()
        => Assert.Equal(Contracts, Load().Kinds.Select(Contract).ToArray());

    [Fact]
    public void Every_overlay_kind_has_its_parameter_contract_and_every_numeric_parameter_a_unit_of_the_vocabulary()
    {
        OverlayKindSet set = Load();
        using JsonDocument common = JsonSchemaSubset.ParseFile(Path.Combine(SchemasDir, "common.schema.json"));
        var vocabulary = common.RootElement.GetProperty("$defs").GetProperty("unit").GetProperty("enum").EnumerateArray()
            .Select(u => u.GetString()!).ToHashSet(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (OverlayKindSpec k in set.Kinds)
        {
            KindSpec spec = set.Parameters.Get(k.Kind);
            Assert.NotEmpty(spec.Params);
            Assert.Contains("(asset overlay)", spec.Title, StringComparison.Ordinal);
            Assert.Contains("never used by the solver", spec.Description, StringComparison.Ordinal);
            foreach (KindParamSpec p in spec.Params.Values)
            {
                Assert.Empty(p.CodeDefaults);   // no C# consumer reads an overlay parameter
                Assert.False(p.MarkGated);
                if (p.Unit is not null) { Assert.Contains(p.Unit, vocabulary); used.Add(p.Unit); }
            }
        }
        // The six units Phase 5a added are each used by at least one overlay kind (none was added speculatively).
        foreach (string unit in new[] { "m", "mm", "mm2", "A", "ohm", "ohm.m" })
            Assert.Contains(unit, used);
    }

    [Fact]
    public void The_unit_vocabulary_is_the_twenty_five_template_units_then_the_six_overlay_units()
    {
        // Ties the schema's list to KindFileSet's (the template kinds' checker), which stays at the twenty-five it
        // checks the template kind files against: the six overlay units are appended after them, in this order.
        using JsonDocument common = JsonSchemaSubset.ParseFile(Path.Combine(SchemasDir, "common.schema.json"));
        string[] units = common.RootElement.GetProperty("$defs").GetProperty("unit").GetProperty("enum").EnumerateArray().Select(u => u.GetString()!).ToArray();
        Assert.Equal(KindFileSet.UnitVocabulary.Concat(new[] { "m", "mm", "mm2", "A", "ohm", "ohm.m" }).ToArray(), units);
    }

    [Fact]
    public void The_overlay_kinds_and_the_template_kinds_never_overlap()
    {
        KindSpecRegistry templates = KindSpecRegistry.LoadDirectory(KindsDir, RealSchemas());
        OverlayKindSet overlay = Load();
        Assert.Empty(templates.Kinds.Select(k => k.Kind).Intersect(overlay.Kinds.Select(k => k.Kind), StringComparer.Ordinal));
        // The template kinds are untouched: none carries an overlay block, and their registry loads the sixteen files as before.
        foreach (string f in Directory.GetFiles(KindsDir, "*" + KindSpecRegistry.KindFileSuffix))
        {
            using JsonDocument d = JsonSchemaSubset.ParseFile(f);
            Assert.False(d.RootElement.TryGetProperty("overlay", out _), $"{Path.GetFileName(f)} must not be an overlay kind");
        }
        Assert.Equal(16, templates.Kinds.Count);
    }

    [Fact]
    public void Every_overlay_kind_file_is_plain_ascii()
    {
        foreach (string f in Directory.GetFiles(OverlayDir))
        {
            byte[] bytes = File.ReadAllBytes(f);
            int bad = Array.FindIndex(bytes, b => b > 0x7E || (b < 0x20 && b != (byte)'\n' && b != (byte)'\r' && b != (byte)'\t'));
            Assert.True(bad < 0, $"{f}: non-ASCII or control byte at offset {bad}");
        }
    }

    [Fact]
    public void The_embedded_overlay_kinds_equal_the_files_and_the_schema_folder_writer_lays_them_out_where_the_loader_reads()
    {
        IReadOnlyDictionary<string, string> embedded = BundledSchemas.Sources(BundledSchemas.V2OverlayKindsPrefix);
        string[] onDisk = Directory.GetFiles(OverlayDir).Select(f => Path.GetFileName(f)!).OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Assert.Equal(onDisk, embedded.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        foreach (string f in onDisk)
            Assert.True(File.ReadAllBytes(Path.Combine(OverlayDir, f)).AsSpan().SequenceEqual(BundledSchemas.Bytes(BundledSchemas.V2OverlayKindsPrefix + f)), f);

        string dir = Path.Combine(Path.GetTempPath(), "gridsim-p5a-schemas-" + Guid.NewGuid().ToString("N"));
        try
        {
            BundledSchemas.WriteV2SchemaFolder(dir);
            string written = Path.Combine(dir, "kinds", OverlayKindSet.FolderName);
            Assert.Equal(onDisk, Directory.GetFiles(written).Select(f => Path.GetFileName(f)!).OrderBy(f => f, StringComparer.Ordinal).ToArray());
            foreach (string f in onDisk)
                Assert.True(File.ReadAllBytes(Path.Combine(OverlayDir, f)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(written, f))), f);
            Assert.True(File.Exists(Path.Combine(dir, "assets.schema.json")));
            // And the written folder loads (the layout the save gate hands the v2 loader).
            Assert.Equal(13, OverlayKindSet.LoadDirectory(written, JsonSchemaSubset.LoadDirectory(dir)).Kinds.Count);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // The loader's own rules
    // ------------------------------------------------------------------------------------------------------------

    private static string KindText(string kind, string overlay)
        => $$"""{ "schema": "gridsim-kind/2", "kind": "{{kind}}", "title": "T", "description": "d", "params": {}{{(overlay.Length == 0 ? "" : ", \"overlay\": " + overlay)}} }""";

    private static string LoadFails(params (string Kind, string Overlay)[] kinds)
        => Assert.Throws<InvalidDataException>(() => OverlayKindSet.FromSources(
            kinds.ToDictionary(k => k.Kind + ".kind.json", k => KindText(k.Kind, k.Overlay), StringComparer.Ordinal), RealSchemas())).Message;

    [Theory]
    [InlineData("", "a.kind.json: an asset overlay kind must have an \"overlay\" block (refs and parentKinds); kind 'a' has none.")]
    [InlineData("""{ "refs": { "r": { "target": "record", "required": true, "description": "x" } }, "parentKinds": [] }""", "a.kind.json: /overlay/refs/r: a record target must list its recordKinds (the overlay kinds it may point at).")]
    [InlineData("""{ "refs": { "r": { "target": "bus", "required": true, "recordKinds": ["a"], "description": "x" } }, "parentKinds": [] }""", "a.kind.json: /overlay/refs/r: only a record target may list recordKinds (this role points at a bus).")]
    [InlineData("""{ "refs": { "r": { "target": "record", "required": true, "recordKinds": ["oltc"], "description": "x" } }, "parentKinds": [] }""", "a.kind.json: /overlay/refs/r/recordKinds: 'oltc' is not an asset overlay kind (known: a).")]
    [InlineData("""{ "refs": {}, "parentKinds": ["nope"] }""", "a.kind.json: /overlay/parentKinds: 'nope' is not an asset overlay kind (known: a).")]
    [InlineData("""{ "refs": {}, "parentKinds": ["a", "a"] }""", "a.kind.json: /overlay/parentKinds: kind 'a' is listed more than once.")]
    [InlineData("""{ "refs": {}, "parentKinds": [], "parentRequired": true }""", "a.kind.json: /overlay/parentRequired: a kind with no parentKinds sits at the top level, so it cannot require a parent.")]
    [InlineData("""{ "refs": { "Bus": { "target": "bus", "required": true, "description": "x" } }, "parentKinds": [] }""", "a.kind.json: /overlay/refs/Bus: role name 'Bus' must be lowerCamelCase ASCII (a lower-case letter, then letters and digits).")]
    public void A_bad_overlay_kind_is_refused_with_its_file_and_place(string overlay, string expected)
    {
        string message = LoadFails(("a", overlay));
        Assert.Contains("Invalid v2 asset overlay kind spec(s) (1 problem(s)):", message, StringComparison.Ordinal);
        Assert.Contains(expected, message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_role_declared_twice_is_refused_by_the_shared_kind_reader()
    {
        // The v2 reader refuses a repeated member name, so the parameter-contract layer (KindSpecRegistry) stops the file
        // before its overlay block is read.
        string message = LoadFails(("a", """{ "refs": { "r": { "target": "bus", "required": true, "description": "x" }, "r": { "target": "line", "required": true, "description": "y" } }, "parentKinds": [] }"""));
        Assert.Contains("Invalid v2 kind spec(s) (1 problem(s)):", message, StringComparison.Ordinal);
        Assert.Contains("a.kind.json: not valid JSON:", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_problem_across_every_overlay_kind_file_is_listed_together()
    {
        string message = LoadFails(("a", ""), ("b", """{ "refs": {}, "parentKinds": ["c"], "parentRequired": false }"""));
        Assert.Contains("(2 problem(s))", message, StringComparison.Ordinal);
        Assert.Contains("a.kind.json: an asset overlay kind must have", message, StringComparison.Ordinal);
        Assert.Contains("b.kind.json: /overlay/parentKinds: 'c' is not an asset overlay kind (known: b).", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_overlay_folder_is_refused_naming_it()
    {
        string missing = Path.Combine(Path.GetTempPath(), "gridsim-p5a-none-" + Guid.NewGuid().ToString("N"));
        var ex = Assert.Throws<DirectoryNotFoundException>(() => OverlayKindSet.LoadDirectory(missing, RealSchemas()));
        Assert.Contains(missing, ex.Message, StringComparison.Ordinal);
    }
}
