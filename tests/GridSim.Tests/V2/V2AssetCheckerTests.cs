using System.Text.Json;
using GridSim.Core.IO.Json.Overlay;
using GridSim.Core.IO.Json.Schema;
using Xunit;
using static GridSim.Tests.V2.V2TestSupport;

namespace GridSim.Tests.V2;

/// <summary>
/// Phase 5a (2026-10-20): <see cref="V2AssetChecker"/>, the gate an asset overlay (gridsim-assets/2) passes before the
/// loader carries it. Every rule is shown to bite, per kind and per rule, and each test asserts the EXACT pointer and
/// message (so a message that loses its record id or field fails), and that every problem is listed together, never
/// only the first. The overlay kinds are the real files under schemas/v2/kinds/overlay and the schemas the real
/// schemas/v2 set, so these tests also pin the shipped kind contracts.
/// </summary>
public sealed class V2AssetCheckerTests
{
    private static readonly Lazy<SchemaValidator> Validator = new(() => new SchemaValidator(RealSchemas()));

    private static readonly Lazy<OverlayKindSet> Kinds =
        new(() => OverlayKindSet.LoadDirectory(Path.Combine(KindsDir, OverlayKindSet.FolderName), RealSchemas()));

    /// <summary>The case the references resolve against: two buses, two generators, a demand, a transformer, a line
    /// named L and two lines that share the name Dup (so a reference to Dup is ambiguous).</summary>
    private const string Network = """
        {
          "schema": "gridsim-network/2",
          "buses": [ { "id": 1, "baseKv": 400, "type": "slack" }, { "id": 2, "baseKv": 132 } ],
          "generators": [ { "name": "G", "bus": 1 }, { "name": "G2", "bus": 2 } ],
          "demands": [ { "name": "D", "bus": 2, "pMw": 10 } ],
          "lines": [
            { "name": "L", "from": 1, "to": 2, "rPu": 0, "xPu": 0.1 },
            { "name": "Dup", "from": 1, "to": 2, "rPu": 0, "xPu": 0.1 },
            { "name": "Dup", "from": 1, "to": 2, "rPu": 0, "xPu": 0.1 }
          ],
          "transformers": [ { "name": "T", "from": 1, "to": 2, "rPu": 0, "xPu": 0.1 } ]
        }
        """;

    /// <summary>Parse with comments and trailing commas allowed, as the v2 reader does, but WITH repeated member names
    /// kept: the v2 file reader refuses those outright (A_repeated_member_name_never_reaches_the_checker_from_a_file),
    /// and the checker refuses them again for an element parsed by anything else; this lets that second check be seen.</summary>
    private static readonly JsonDocumentOptions TestOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static JsonElement Root(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json, TestOptions);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void A_repeated_member_name_never_reaches_the_checker_from_a_file()
    {
        // The shared v2 reader (JsonSchemaSubset.ReaderOptions, used by ParseFile and so by the loader and validator)
        // refuses a repeated member name before any check runs.
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse("""{ "a": 1, "a": 2 }""", JsonSchemaSubset.ReaderOptions));
    }

    private static AssetOverlayTargets Targets() => AssetOverlayTargets.FromNetworks(new[] { Root(Network) });

    /// <summary>An overlay document holding the given records.</summary>
    private static string Doc(params string[] records)
        => "{ \"schema\": \"gridsim-assets/2\", \"records\": [ " + string.Join(", ", records) + " ] }";

    /// <summary>Check documents named a.assets.json, b.assets.json, ... against the case above (or no case).</summary>
    private static IReadOnlyList<SchemaError> Check(bool withCase, params string[] docs)
        => V2AssetChecker.Check(docs.Select((d, i) => ((char)('a' + i) + ".assets.json", Root(d))).ToList(),
            Validator.Value, Kinds.Value, withCase ? Targets() : null);

    private static IReadOnlyList<SchemaError> Check(params string[] docs) => Check(true, docs);

    /// <summary>"pointer => message" per error, in order, for exact comparison.</summary>
    private static string[] Lines(IEnumerable<SchemaError> errors) => errors.Select(e => e.Pointer + " => " + e.Message).ToArray();

    // A complete, valid overlay: every kind, every reference type, nested parents (a child before its parent).
    private static readonly string[] ValidRecords =
    {
        """{ "id": "bb1", "kind": "busbarSection", "parent": "sub", "refs": { "bus": { "bus": 1 } }, "params": { "label": "Main 1", "ratedCurrentA": { "v": 4000, "unit": "A" } } }""",
        """{ "id": "sub", "kind": "substation", "refs": {}, "params": { "name": "Site", "latitudeDeg": 51.5, "longitudeDeg": -0.1 }, "prov": { "tag": "Synthetic", "note": "test" }, "note": "a note" }""",
        """{ "id": "bb2", "kind": "busbarSection", "parent": "sub", "refs": { "bus": { "bus": 2 } }, "params": {} }""",
        """{ "id": "cb", "kind": "switchingDevice", "parent": "sub", "refs": { "from": { "record": "bb1" }, "to": { "record": "bb2" }, "line": { "line": "L" }, "transformer": { "transformer": "T" }, "generator": { "generator": "G" }, "demand": { "demand": "D" } }, "params": { "device": "breaker", "normalOpen": false } }""",
        """{ "id": "soil", "kind": "soilZone", "refs": {}, "params": { "resistivityOhmM": 100 } }""",
        """{ "id": "earth", "kind": "earthPath", "parent": "sub", "refs": { "bus": { "bus": 1 }, "soil": { "record": "soil" } }, "params": { "resistanceOhm": 0.5 } }""",
        """{ "id": "t3", "kind": "transformer3W", "parent": "sub", "refs": { "starBus": { "bus": 2 }, "hv": { "transformer": "T" }, "mv": { "transformer": "T" }, "lv": { "transformer": "T" } }, "params": { "baseMva": 100 } }""",
        """{ "id": "dc", "kind": "hvdcLink", "refs": { "sendingConverter": { "generator": "G" }, "receivingConverter": { "generator": "G2" } }, "params": { "technology": "vsc" } }""",
        """{ "id": "zebra", "kind": "conductorType", "refs": {}, "params": { "crossSectionMm2": 428.9, "diameterMm": 28.62 } }""",
        """{ "id": "l6", "kind": "towerType", "refs": {}, "params": { "heightM": 50, "circuits": 2 } }""",
        """{ "id": "l6.a", "kind": "conductorPosition", "parent": "l6", "refs": {}, "params": { "phase": "a", "xM": -6.5, "yM": 30 } }""",
        """{ "id": "route", "kind": "lineRoute", "refs": { "line": { "line": "L" } }, "params": {} }""",
        """{ "id": "span1", "kind": "span", "parent": "route", "refs": { "conductor": { "record": "zebra" }, "tower": { "record": "l6" } }, "params": { "lengthM": 350 } }""",
        """{ "id": "ew", "kind": "earthWire", "parent": "route", "refs": { "conductor": { "record": "zebra" } }, "params": { "count": 1 } }""",
    };

    [Fact]
    public void A_complete_overlay_of_every_kind_and_reference_type_passes()
    {
        IReadOnlyList<SchemaError> errors = Check(Doc(ValidRecords));
        Assert.True(errors.Count == 0, Dump(errors));
        // The fixture really does use every overlay kind (so a kind cannot quietly drop out of this check).
        var kinds = ValidRecords.Select(r => Root(r).GetProperty("kind").GetString()!).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(Kinds.Value.Kinds.Select(k => k.Kind).OrderBy(k => k, StringComparer.Ordinal), kinds.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void The_same_overlay_split_over_two_files_passes_with_parents_and_refs_across_files()
    {
        IReadOnlyList<SchemaError> errors = Check(Doc(ValidRecords.Take(7).ToArray()), Doc(ValidRecords.Skip(7).ToArray()));
        Assert.True(errors.Count == 0, Dump(errors));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Per kind: each kind's own required parameters, required roles and parent rule
    // ------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("""{ "id": "x", "kind": "substation", "parent": "y", "refs": {}, "params": {} }""",
        "/records/0/parent => record 'x': a 'substation' record sits at the top level and takes no parent, but parent 'y' is given.")]
    [InlineData("""{ "id": "x", "kind": "busbarSection", "refs": {}, "params": {} }""",
        "/records/0 => record 'x': a 'busbarSection' record must have a parent (of kind substation), and it has none.",
        "/records/0/refs => record 'x': a 'busbarSection' record requires role 'bus' (a bus), which is missing.")]
    [InlineData("""{ "id": "x", "kind": "switchingDevice", "refs": {}, "params": {} }""",
        "/records/0/params => record 'x': kind 'switchingDevice' requires parameter 'device', which is missing.",
        "/records/0/params => record 'x': kind 'switchingDevice' requires parameter 'normalOpen', which is missing.",
        "/records/0 => record 'x': a 'switchingDevice' record must have a parent (of kind substation), and it has none.",
        "/records/0/refs => record 'x': a 'switchingDevice' record requires role 'from' (a record), which is missing.")]
    [InlineData("""{ "id": "x", "kind": "conductorType", "refs": {}, "params": {} }""",
        "/records/0/params => record 'x': kind 'conductorType' requires parameter 'crossSectionMm2', which is missing.")]
    [InlineData("""{ "id": "x", "kind": "towerType", "refs": {}, "params": { "heightM": -1 } }""",
        "/records/0/params/heightM => record 'x': -1 m is below the minimum 0 for 'heightM'.")]
    [InlineData("""{ "id": "x", "kind": "conductorPosition", "refs": {}, "params": {} }""",
        "/records/0/params => record 'x': kind 'conductorPosition' requires parameter 'phase', which is missing.",
        "/records/0/params => record 'x': kind 'conductorPosition' requires parameter 'xM', which is missing.",
        "/records/0/params => record 'x': kind 'conductorPosition' requires parameter 'yM', which is missing.",
        "/records/0 => record 'x': a 'conductorPosition' record must have a parent (of kind towerType), and it has none.")]
    [InlineData("""{ "id": "x", "kind": "lineRoute", "refs": {}, "params": {} }""",
        "/records/0/refs => record 'x': a 'lineRoute' record requires role 'line' (a line), which is missing.")]
    [InlineData("""{ "id": "x", "kind": "span", "refs": {}, "params": {} }""",
        "/records/0/params => record 'x': kind 'span' requires parameter 'lengthM', which is missing.",
        "/records/0 => record 'x': a 'span' record must have a parent (of kind lineRoute), and it has none.")]
    [InlineData("""{ "id": "x", "kind": "earthWire", "refs": {}, "params": {} }""",
        "/records/0/params => record 'x': kind 'earthWire' requires parameter 'count', which is missing.",
        "/records/0 => record 'x': a 'earthWire' record must have a parent (of kind lineRoute), and it has none.")]
    [InlineData("""{ "id": "x", "kind": "soilZone", "refs": {}, "params": {} }""",
        "/records/0/params => record 'x': kind 'soilZone' requires parameter 'resistivityOhmM', which is missing.")]
    [InlineData("""{ "id": "x", "kind": "earthPath", "refs": {}, "params": {} }""",
        "/records/0/params => record 'x': kind 'earthPath' requires parameter 'resistanceOhm', which is missing.",
        "/records/0/refs => record 'x': a 'earthPath' record requires role 'bus' (a bus), which is missing.")]
    [InlineData("""{ "id": "x", "kind": "transformer3W", "refs": {}, "params": {} }""",
        "/records/0/params => record 'x': kind 'transformer3W' requires parameter 'baseMva', which is missing.",
        "/records/0/refs => record 'x': a 'transformer3W' record requires role 'starBus' (a bus), which is missing.",
        "/records/0/refs => record 'x': a 'transformer3W' record requires role 'hv' (a transformer), which is missing.",
        "/records/0/refs => record 'x': a 'transformer3W' record requires role 'mv' (a transformer), which is missing.",
        "/records/0/refs => record 'x': a 'transformer3W' record requires role 'lv' (a transformer), which is missing.")]
    [InlineData("""{ "id": "x", "kind": "hvdcLink", "refs": {}, "params": {} }""",
        "/records/0/refs => record 'x': a 'hvdcLink' record requires role 'sendingConverter' (a generator), which is missing.")]
    public void Each_kind_refuses_a_record_that_breaks_its_contract_with_every_problem_listed(string record, params string[] expected)
        => Assert.Equal(expected, Lines(Check(Doc(record))));

    [Fact]
    public void The_per_kind_theory_covers_every_overlay_kind()
    {
        // The theory above has one row per kind; this keeps it that way when a kind is added.
        var rows = typeof(V2AssetCheckerTests).GetMethod(nameof(Each_kind_refuses_a_record_that_breaks_its_contract_with_every_problem_listed))!
            .GetCustomAttributes(typeof(InlineDataAttribute), false).Cast<InlineDataAttribute>()
            .Select(a => Root((string)a.GetData(null!).Single()[0]).GetProperty("kind").GetString()!)
            .OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Assert.Equal(Kinds.Value.Kinds.Select(k => k.Kind).ToArray(), rows);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Per rule: kind, params, units
    // ------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("oltc")]   // a template kind is not an overlay kind: the two sets never mix
    [InlineData("pylon")]
    public void An_unknown_kind_is_refused_naming_the_overlay_kinds(string kind)
        => Assert.Equal(new[]
            {
                $"/records/0/kind => record 'x': '{kind}' is not an asset overlay kind (known: busbarSection, conductorPosition, conductorType, earthPath, earthWire, hvdcLink, lineRoute, soilZone, span, substation, switchingDevice, towerType, transformer3W).",
            },
            Lines(Check(Doc($$"""{ "id": "x", "kind": "{{kind}}", "refs": {}, "params": {} }"""))));

    [Theory]
    [InlineData("""{ "resistivityOhmM": "high" }""", "/records/0/params/resistivityOhmM => record 'x': value \"high\" is not a finite number.")]
    [InlineData("""{ "resistivityOhmM": -5 }""", "/records/0/params/resistivityOhmM => record 'x': -5 ohm.m is below the minimum 0 for 'resistivityOhmM'.")]
    [InlineData("""{ "resistivityOhmM": 100, "colour": "brown" }""", "/records/0/params/colour => record 'x': unknown parameter 'colour' for kind 'soilZone' (declared: resistivityOhmM, depthM).")]
    [InlineData("""{ "resistivityOhmM": { "v": 100, "unit": "ohm" } }""", "/records/0/params/resistivityOhmM/unit => record 'x': unit 'ohm' does not match the kind's unit 'ohm.m' for 'resistivityOhmM' (no conversion is done; write the spec's unit).")]
    [InlineData("""{ "resistivityOhmM": 100, "depthM": 1, "depthM": 2 }""", "/records/0/params/depthM => record 'x': parameter 'depthM' is written more than once (the reader would keep only one of them).")]
    public void A_bad_parameter_is_refused_with_its_record_and_field(string parameters, string expected)
        => Assert.Equal(new[] { expected }, Lines(Check(Doc($$"""{ "id": "x", "kind": "soilZone", "refs": {}, "params": {{parameters}} }"""))));

    [Fact]
    public void An_enum_value_and_a_unit_on_a_unitless_parameter_are_refused()
        => Assert.Equal(new[]
            {
                "/records/0/params/technology/unit => record 'dc': parameter 'technology' is a enum and takes no unit, but 'kV' is written.",
                "/records/0/params/ratedMw => record 'dc': value true is not a finite number.",
            },
            Lines(Check(Doc("""{ "id": "dc", "kind": "hvdcLink", "refs": { "sendingConverter": { "generator": "G" } }, "params": { "technology": { "v": "vsc", "unit": "kV" }, "ratedMw": true } }"""))));

    [Fact]
    public void A_switching_device_type_outside_the_enum_is_refused()
        => Assert.Equal(new[] { "/records/2/params/device => record 'x': value \"fuse\" is not one of the allowed values: breaker, disconnector, earthSwitch." },
            Lines(Check(Doc(
                """{ "id": "s", "kind": "substation", "refs": {}, "params": {} }""",
                """{ "id": "b", "kind": "busbarSection", "parent": "s", "refs": { "bus": { "bus": 1 } }, "params": {} }""",
                """{ "id": "x", "kind": "switchingDevice", "parent": "s", "refs": { "from": { "record": "b" } }, "params": { "device": "fuse", "normalOpen": true } }"""))));

    // ------------------------------------------------------------------------------------------------------------
    // Per rule: ids and parents
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_duplicate_id_is_refused_within_a_file_and_across_files_naming_the_first()
    {
        string soil = """{ "id": "s", "kind": "soilZone", "refs": {}, "params": { "resistivityOhmM": 1 } }""";
        IReadOnlyList<SchemaError> errors = Check(Doc(soil, soil), Doc(soil));
        Assert.Equal(new[]
        {
            "/records/1/id => record 's': the id is declared twice (first in a.assets.json at /records/0); an id must be unique across every overlay file of the case.",
            "/records/0/id => record 's': the id is declared twice (first in a.assets.json at /records/0); an id must be unique across every overlay file of the case.",
        }, Lines(errors));
        Assert.Equal(new[] { "a.assets.json", "b.assets.json" }, errors.Select(e => e.File).ToArray());
    }

    [Fact]
    public void A_parent_that_does_not_exist_or_is_of_the_wrong_kind_is_refused()
        => Assert.Equal(new[]
            {
                "/records/0/parent => record 'a': parent 'nowhere' is not a record of the overlay.",
                "/records/2/parent => record 'c': the parent of a 'busbarSection' record must be of kind substation, but 'soil' is a 'soilZone'.",
            },
            Lines(Check(Doc(
                """{ "id": "a", "kind": "busbarSection", "parent": "nowhere", "refs": { "bus": { "bus": 1 } }, "params": {} }""",
                """{ "id": "soil", "kind": "soilZone", "refs": {}, "params": { "resistivityOhmM": 1 } }""",
                """{ "id": "c", "kind": "busbarSection", "parent": "soil", "refs": { "bus": { "bus": 1 } }, "params": {} }"""))));

    [Fact]
    public void A_record_that_is_its_own_parent_is_refused_as_a_loop()
        => Assert.Equal(new[]
            {
                "/records/0/parent => record 'p': the parent of a 'conductorPosition' record must be of kind towerType, but 'p' is a 'conductorPosition'.",
                "/records/0/parent => record 'p': the parent chain loops (p -> p); a record cannot sit, directly or through others, under itself.",
            },
            Lines(Check(Doc("""{ "id": "p", "kind": "conductorPosition", "parent": "p", "refs": {}, "params": { "phase": "a", "xM": 0, "yM": 1 } }"""))));

    [Fact]
    public void A_parent_loop_through_several_records_is_reported_once_naming_every_record_on_it()
    {
        // b -> c -> d -> b, entered from a (which hangs off the loop but is not on it).
        string Rec(string id, string parent) => $$"""{ "id": "{{id}}", "kind": "busbarSection", "parent": "{{parent}}", "refs": { "bus": { "bus": 1 } }, "params": {} }""";
        IReadOnlyList<SchemaError> errors = Check(Doc(Rec("a", "b"), Rec("b", "c"), Rec("c", "d"), Rec("d", "b")));
        Assert.Equal(new[]
        {
            "/records/0/parent => record 'a': the parent of a 'busbarSection' record must be of kind substation, but 'b' is a 'busbarSection'.",
            "/records/1/parent => record 'b': the parent of a 'busbarSection' record must be of kind substation, but 'c' is a 'busbarSection'.",
            "/records/2/parent => record 'c': the parent of a 'busbarSection' record must be of kind substation, but 'd' is a 'busbarSection'.",
            "/records/3/parent => record 'd': the parent of a 'busbarSection' record must be of kind substation, but 'b' is a 'busbarSection'.",
            "/records/1/parent => record 'b': the parent chain loops (b -> c -> d -> b); a record cannot sit, directly or through others, under itself.",
        }, Lines(errors));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Per rule: references
    // ------------------------------------------------------------------------------------------------------------

    private static string Device(string refs)
        => Doc("""{ "id": "s", "kind": "substation", "refs": {}, "params": {} }""",
               """{ "id": "b", "kind": "busbarSection", "parent": "s", "refs": { "bus": { "bus": 1 } }, "params": {} }""",
               """{ "id": "soil", "kind": "soilZone", "refs": {}, "params": { "resistivityOhmM": 1 } }""",
               $$"""{ "id": "x", "kind": "switchingDevice", "parent": "s", "refs": {{refs}}, "params": { "device": "breaker", "normalOpen": false } }""");

    [Theory]
    [InlineData("""{ "from": { "record": "b" }, "line": { "line": "Nope" } }""", "/records/3/refs/line/line => record 'x': role 'line' points at line 'Nope', which is not a line of the case.")]
    [InlineData("""{ "from": { "record": "b" }, "line": { "line": "Dup" } }""", "/records/3/refs/line/line => record 'x': role 'line' points at line 'Dup', which is ambiguous: 2 lines of the case carry that name.")]
    [InlineData("""{ "from": { "record": "b" }, "transformer": { "transformer": "L" } }""", "/records/3/refs/transformer/transformer => record 'x': role 'transformer' points at transformer 'L', which is not a transformer of the case.")]
    [InlineData("""{ "from": { "record": "b" }, "generator": { "generator": "D" } }""", "/records/3/refs/generator/generator => record 'x': role 'generator' points at generator 'D', which is not a generator of the case.")]
    [InlineData("""{ "from": { "record": "b" }, "demand": { "demand": "G" } }""", "/records/3/refs/demand/demand => record 'x': role 'demand' points at demand 'G', which is not a demand of the case.")]
    [InlineData("""{ "from": { "record": "nowhere" } }""", "/records/3/refs/from/record => record 'x': role 'from' points at record 'nowhere', which is not a record of the overlay.")]
    [InlineData("""{ "from": { "record": "x" } }""", "/records/3/refs/from/record => record 'x': role 'from' points at the record itself.")]
    [InlineData("""{ "from": { "record": "soil" } }""", "/records/3/refs/from/record => record 'x': role 'from' must point at a record of kind busbarSection, but 'soil' is a 'soilZone'.")]
    [InlineData("""{ "from": { "bus": 1 } }""", "/records/3/refs/from => record 'x': role 'from' of a 'switchingDevice' record points at a record, but a bus is written.")]
    [InlineData("""{ "from": { "record": "b" }, "bay": { "line": "L" } }""", "/records/3/refs/bay => record 'x': kind 'switchingDevice' has no role 'bay' (declared: from, to, line, transformer, generator, demand).")]
    [InlineData("""{ "line": { "line": "L" } }""", "/records/3/refs => record 'x': a 'switchingDevice' record requires role 'from' (a record), which is missing.")]
    [InlineData("""{ "from": { "record": "b" }, "line": { "line": "L" }, "line": { "line": "L" } }""", "/records/3/refs/line => record 'x': role 'line' is written more than once (the reader would keep only one of them).")]
    public void A_bad_reference_is_refused_with_its_record_and_role(string refs, string expected)
        => Assert.Equal(new[] { expected }, Lines(Check(Device(refs))));

    [Theory]
    [InlineData("3", "/records/0/refs/bus/bus => record 'e': role 'bus' points at bus 3, which is not a bus of the case.")]
    [InlineData("1e30", "/records/0/refs/bus/bus => record 'e': role 'bus' gives bus id 1e30, which is not a whole number a bus id can be.")]
    public void A_bus_reference_must_name_a_bus_of_the_case(string bus, string expected)
        => Assert.Equal(new[] { expected },
            Lines(Check(Doc($$"""{ "id": "e", "kind": "earthPath", "refs": { "bus": { "bus": {{bus}} } }, "params": { "resistanceOhm": 1 } }"""))));

    [Fact]
    public void A_fractional_bus_id_is_refused_by_the_schema_naming_the_record()
    {
        IReadOnlyList<SchemaError> errors = Check(Doc("""{ "id": "e", "kind": "earthPath", "refs": { "bus": { "bus": 1.5 } }, "params": { "resistanceOhm": 1 } }"""));
        Assert.NotEmpty(errors);
        Assert.All(errors, e => Assert.StartsWith("/records/0/refs/bus", e.Pointer, StringComparison.Ordinal));
        Assert.All(errors, e => Assert.StartsWith("record 'e': ", e.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void A_bus_id_written_as_a_whole_number_with_a_fraction_part_resolves()
    {
        // The schema's integer admits 1.0 (as everywhere in v2): it is bus 1.
        IReadOnlyList<SchemaError> errors = Check(Doc("""{ "id": "e", "kind": "earthPath", "refs": { "bus": { "bus": 1.0 } }, "params": { "resistanceOhm": 1 } }"""));
        Assert.True(errors.Count == 0, Dump(errors));
    }

    [Fact]
    public void A_role_on_a_kind_that_declares_none_is_refused()
        => Assert.Equal(new[] { "/records/0/refs/bus => record 's': kind 'substation' declares no roles, so role 'bus' cannot be given." },
            Lines(Check(Doc("""{ "id": "s", "kind": "substation", "refs": { "bus": { "bus": 1 } }, "params": {} }"""))));

    [Fact]
    public void Without_a_case_only_references_between_records_are_resolved()
    {
        // A lone overlay file (the validate verb given no manifest): references to network elements cannot be resolved,
        // so they are not reported; a reference to a missing record still is.
        IReadOnlyList<SchemaError> errors = Check(false, Device("""{ "from": { "record": "nowhere" }, "line": { "line": "Nope" }, "demand": { "demand": "G" } }"""));
        Assert.Equal(new[] { "/records/3/refs/from/record => record 'x': role 'from' points at record 'nowhere', which is not a record of the overlay." }, Lines(errors));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Every problem together; the schema layer first
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Every_problem_in_every_file_is_listed_together()
    {
        IReadOnlyList<SchemaError> errors = Check(
            Doc("""{ "id": "a", "kind": "soilZone", "refs": {}, "params": { "resistivityOhmM": { "v": 1, "unit": "m" } } }""",
                """{ "id": "b", "kind": "pylon", "refs": {}, "params": {} }""",
                """{ "id": "c", "kind": "lineRoute", "refs": { "line": { "line": "Nope" } }, "params": {} }"""),
            Doc("""{ "id": "a", "kind": "span", "parent": "c", "refs": { "conductor": { "record": "c" } }, "params": { "lengthM": 1 } }""",
                """{ "id": "e", "kind": "earthPath", "refs": { "bus": { "bus": 9 } }, "params": { "resistanceOhm": 1 } }"""));
        Assert.Equal(new[]
        {
            "b.assets.json /records/0/id => record 'a': the id is declared twice (first in a.assets.json at /records/0); an id must be unique across every overlay file of the case.",
            "a.assets.json /records/0/params/resistivityOhmM/unit => record 'a': unit 'm' does not match the kind's unit 'ohm.m' for 'resistivityOhmM' (no conversion is done; write the spec's unit).",
            "a.assets.json /records/1/kind => record 'b': 'pylon' is not an asset overlay kind (known: busbarSection, conductorPosition, conductorType, earthPath, earthWire, hvdcLink, lineRoute, soilZone, span, substation, switchingDevice, towerType, transformer3W).",
            "a.assets.json /records/2/refs/line/line => record 'c': role 'line' points at line 'Nope', which is not a line of the case.",
            "b.assets.json /records/0/refs/conductor/record => record 'a': role 'conductor' must point at a record of kind conductorType, but 'c' is a 'lineRoute'.",
            "b.assets.json /records/1/refs/bus/bus => record 'e': role 'bus' points at bus 9, which is not a bus of the case.",
        }, errors.Select(e => e.File + " " + e.Pointer + " => " + e.Message).ToArray());
    }

    [Fact]
    public void A_schema_problem_is_reported_with_the_record_id_and_the_semantic_layer_waits_for_the_shape()
    {
        // Record x has no params (schema); record y's unknown kind (semantic) is not reported until the shape is fixed.
        IReadOnlyList<SchemaError> errors = Check(Doc(
            """{ "id": "x", "kind": "soilZone", "refs": {} }""",
            """{ "id": "y", "kind": "pylon", "refs": {}, "params": {} }"""));
        Assert.NotEmpty(errors);
        Assert.All(errors, e => Assert.StartsWith("/records/0", e.Pointer, StringComparison.Ordinal));
        Assert.All(errors, e => Assert.StartsWith("record 'x': ", e.Message, StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Message.Contains("params", StringComparison.Ordinal));
        Assert.DoesNotContain(errors, e => e.Message.Contains("pylon", StringComparison.Ordinal));
    }
}
