using System.Text;
using System.Text.Json;
using Xunit;

namespace GridSim.Tests.V2;

/// <summary>
/// Phase 1 slice 3 (dream-of-json-ification): every per-kind parameter contract in <c>schemas/v2/kinds/</c> has
/// EXACTLY the fixed <c>gridsim-kind/2</c> format, checked here independently of the schema validator another slice
/// builds. The checker itself is proven to bite: each kind of malformation (an unknown member, a number without a
/// unit, an enum without values, a unit outside the closed vocabulary, and so on) is fed in and must be reported.
/// </summary>
public sealed class V2KindSpecStructureTests
{
    // ------------------------------------------------------------------------------ the real files

    [Fact]
    public void The_kinds_folder_holds_the_fifteen_parameter_kinds_and_the_site_kind_and_nothing_unexpected()
    {
        // Renamed 2026-10 (DOC-17 D-DOC-222): was The_kinds_folder_holds_the_fifteen_slice3_kinds_and_nothing_unexpected.
        // The body is unchanged apart from the renamed lists; site is now required (below) rather than merely tolerated.
        string dir = KindFileSet.KindsDirectory();
        Assert.True(Directory.Exists(dir), $"kinds folder missing: {dir}");

        // Every parameter kind must be present ...
        foreach (string kind in KindFileSet.ParameterKinds)
            Assert.True(File.Exists(KindFileSet.PathOf(kind)), $"missing kind file for '{kind}'");
        // ... and every slot-only kind (site) too (added 2026-10, DOC-17 D-DOC-222: a stricter check, never a looser one) ...
        foreach (string kind in KindFileSet.SlotOnlyKinds)
            Assert.True(File.Exists(KindFileSet.PathOf(kind)), $"missing kind file for '{kind}'");

        // ... and nothing else may be there.
        var known = KindFileSet.ParameterKinds.Concat(KindFileSet.SlotOnlyKinds).ToHashSet(StringComparer.Ordinal);
        foreach (string file in Directory.GetFiles(dir))
        {
            string name = Path.GetFileName(file);
            Assert.True(name.EndsWith(".kind.json", StringComparison.Ordinal), $"unexpected file in the kinds folder: {name}");
            Assert.Contains(name[..^".kind.json".Length], known);
        }
    }

    [Theory]
    [MemberData(nameof(KindFileSet.KindNames), MemberType = typeof(KindFileSet))]
    public void Each_kind_file_has_exactly_the_fixed_format(string kind)
    {
        IReadOnlyList<string> errors = KindFileSet.ShapeErrors(KindFileSet.Load(kind), kind);
        Assert.True(errors.Count == 0, $"{kind}.kind.json departs from the fixed format:\n  " + string.Join("\n  ", errors));
    }

    [Theory]
    [MemberData(nameof(KindFileSet.KindNames), MemberType = typeof(KindFileSet))]
    public void Each_kind_file_is_plain_ascii(string kind)
    {
        // House rule: ASCII only (no smart quotes, no en or em dashes) in every file written.
        byte[] bytes = File.ReadAllBytes(KindFileSet.PathOf(kind));
        int bad = Array.FindIndex(bytes, b => b > 0x7F);
        Assert.True(bad < 0, $"{kind}.kind.json has a non-ASCII byte at offset {bad}");
    }

    // D-DOC-222 (DOC-17): the two per-kind checks above ran over the fifteen parameter kinds only, so site.kind.json
    // was never checked for format or ASCII. These run the same checks over the slot-only kinds (added tests; the
    // existing theories keep their rows exactly).
    [Theory]
    [InlineData("site")]
    public void Each_slot_only_kind_file_has_exactly_the_fixed_format_and_is_plain_ascii(string kind)
    {
        Assert.Contains(kind, KindFileSet.SlotOnlyKinds);
        IReadOnlyList<string> errors = KindFileSet.ShapeErrors(KindFileSet.Load(kind), kind);
        Assert.True(errors.Count == 0, $"{kind}.kind.json departs from the fixed format:\n  " + string.Join("\n  ", errors));
        byte[] bytes = File.ReadAllBytes(KindFileSet.PathOf(kind));
        int bad = Array.FindIndex(bytes, b => b > 0x7F);
        Assert.True(bad < 0, $"{kind}.kind.json has a non-ASCII byte at offset {bad}");
    }

    [Fact]
    public void The_slot_only_list_is_exactly_the_kind_files_that_are_not_parameter_kinds()
    {
        // So a new slot-only kind cannot slip in unchecked: AllKindNames must cover every file in the folder.
        var files = Directory.GetFiles(KindFileSet.KindsDirectory(), "*.kind.json")
            .Select(f => Path.GetFileName(f)[..^".kind.json".Length]).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        var all = ((IEnumerable<object[]>)KindFileSet.AllKindNames()).Select(row => (string)row[0]).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Assert.Equal(files, all);
        Assert.Equal(new[] { "site" }, KindFileSet.SlotOnlyKinds);
    }

    [Fact]
    public void Every_unit_used_is_in_the_closed_vocabulary_and_the_vocabulary_has_twenty_five_units()
    {
        // Pins the vocabulary this test enforces to the plan's list, so it cannot quietly grow here.
        // Moved 2026-10 (Q24, authorised): 20 -> 25, the five units Q24 added (MW.s/Hz, MW/Hz, MW/s/MW, V, min).
        // The method name moved with it (was ..._has_twenty_units).
        Assert.Equal(25, KindFileSet.UnitVocabulary.Length);
        Assert.Equal(KindFileSet.UnitVocabulary.Length, KindFileSet.UnitVocabulary.Distinct(StringComparer.Ordinal).Count());
        foreach ((string kind, JsonElement root) in KindFileSet.LoadAll())
            foreach (JsonProperty p in root.GetProperty("params").EnumerateObject())
                if (p.Value.TryGetProperty("unit", out JsonElement u))
                    Assert.True(KindFileSet.UnitVocabulary.Contains(u.GetString(), StringComparer.Ordinal), $"{kind}.{p.Name}: unit {u}");
    }

    [Fact]
    public void No_parameter_name_is_declared_twice_within_a_kind_and_names_are_lower_camel_case()
    {
        foreach ((string kind, JsonElement root) in KindFileSet.LoadAll())
        {
            IReadOnlyList<string> names = KindFileSet.ParamNames(root);
            Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
            foreach (string n in names)
                Assert.True(char.IsAsciiLetterLower(n[0]) && n.All(char.IsAsciiLetterOrDigit), $"{kind}.{n}: not lowerCamelCase ASCII");
        }
    }

    // ------------------------------------------------------------------------------ the checker bites

    /// <summary>A minimal, correct kind document. Each negative case below breaks exactly one rule of it.</summary>
    private const string Good = """
        {
          "schema": "gridsim-kind/2",
          "kind": "demo",
          "title": "Demo",
          "description": "A minimal valid kind.",
          "params": {
            "x": { "type": "number", "unit": "s", "min": 0, "max": 10, "required": false, "markGated": true,
                   "description": "A number.",
                   "codeDefaults": [ { "consumer": "Somewhere", "ref": "member:GridSim.Core:T.X", "value": 1.5 } ] },
            "e": { "type": "enum", "values": ["A", "B"], "required": true, "description": "An enum.", "codeDefaults": [] },
            "b": { "type": "boolean", "required": false, "description": "A flag.",
                   "codeDefaults": [ { "consumer": "Elsewhere", "ref": "param:GridSim.Core:T:.ctor:b", "value": false } ] }
          }
        }
        """;

    private static IReadOnlyList<string> Check(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json, KindFileSet.ReadOptions);
        return KindFileSet.ShapeErrors(doc.RootElement, "demo");
    }

    [Fact]
    public void The_minimal_document_is_accepted()
    {
        IReadOnlyList<string> errors = Check(Good);
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    [Theory]
    // top level
    [InlineData("\"params\": {", "\"extra\": 1, \"params\": {", "unknown member")]
    [InlineData("\"schema\": \"gridsim-kind/2\"", "\"schema\": \"gridsim-kind/1\"", "/schema")]
    [InlineData("\"kind\": \"demo\"", "\"kind\": \"other\"", "/kind")]
    [InlineData("\"title\": \"Demo\",", "", "/title: required member is missing")]
    // parameter members
    [InlineData("\"markGated\": true,", "\"markGated\": true, \"default\": 3,", "unknown member")]
    [InlineData("\"unit\": \"s\", ", "", "must declare a unit")]
    [InlineData("\"unit\": \"s\"", "\"unit\": \"seconds\"", "not in the closed unit vocabulary")]
    [InlineData("\"unit\": \"s\"", "\"unit\": \"Hz/S\"", "not in the closed unit vocabulary")]
    [InlineData("\"values\": [\"A\", \"B\"], ", "", "must list its values")]
    [InlineData("\"values\": [\"A\", \"B\"]", "\"values\": []", "non-empty array")]
    [InlineData("\"values\": [\"A\", \"B\"]", "\"values\": [\"A\", \"A\"]", "duplicate value")]
    [InlineData("\"type\": \"boolean\",", "\"type\": \"boolean\", \"unit\": \"1\",", "only number and integer parameters carry a unit")]
    [InlineData("\"type\": \"boolean\",", "\"type\": \"boolean\", \"values\": [\"A\"],", "only enum parameters carry values")]
    [InlineData("\"type\": \"enum\",", "\"type\": \"enum\", \"min\": 0,", "only number and integer parameters carry min")]
    [InlineData("\"min\": 0, \"max\": 10", "\"min\": 10, \"max\": 0", "exceeds max")]
    [InlineData("\"type\": \"number\"", "\"type\": \"float\"", "/type: must be one of")]
    [InlineData("\"required\": true,", "", "/required: required member is missing")]
    [InlineData("\"required\": true,", "\"required\": \"yes\",", "must be true or false")]
    [InlineData("\"description\": \"A flag.\",", "", "/description: required member is missing")]
    [InlineData(", \"codeDefaults\": [] }", " }", "/codeDefaults: required member is missing")]
    // code defaults
    [InlineData("\"consumer\": \"Somewhere\",", "\"consumer\": \"Somewhere\", \"note\": \"x\",", "unknown member")]
    [InlineData("\"consumer\": \"Somewhere\", ", "", "/consumer: required member is missing")]
    [InlineData("\"ref\": \"member:GridSim.Core:T.X\"", "\"ref\": \"GridSim.Core:T.X\"", "must start with one of")]
    [InlineData("\"value\": 1.5", "\"value\": \"1.5\"", "must be a number")]
    [InlineData("\"value\": 1.5", "\"value\": 11", "above the declared max")]
    [InlineData("\"value\": false", "\"value\": 0", "must be true or false")]
    [InlineData("\"codeDefaults\": [] }", "\"codeDefaults\": [ { \"consumer\": \"c\", \"ref\": \"source:a:b\", \"value\": \"C\" } ] }", "must be one of the enum values")]
    [InlineData("\"value\": 1.5 } ]", "\"value\": 1.5 }, { \"consumer\": \"Again\", \"ref\": \"member:GridSim.Core:T.X\", \"value\": 1.5 } ]", "appears twice")]
    // duplicates that System.Text.Json would otherwise let through silently
    [InlineData("\"title\": \"Demo\",", "\"title\": \"Demo\", \"title\": \"Again\",", "appears more than once")]
    public void Each_malformation_is_reported(string find, string replace, string expectedFragment)
    {
        Assert.Contains(find, Good, StringComparison.Ordinal);   // the case really edits the good document
        string bad = ReplaceFirst(Good, find, replace);
        Assert.NotEqual(Good, bad);
        IReadOnlyList<string> errors = Check(bad);
        Assert.True(errors.Any(e => e.Contains(expectedFragment, StringComparison.Ordinal)),
            $"expected an error containing \"{expectedFragment}\"; got:\n  " + string.Join("\n  ", errors));
    }

    [Fact]
    public void Infinity_is_accepted_only_as_the_two_named_strings_on_a_number()
    {
        string inf = ReplaceFirst(Good, "\"value\": 1.5", "\"value\": \"+Infinity\"");
        Assert.Empty(Check(inf));
        string neg = ReplaceFirst(Good, "\"value\": 1.5", "\"value\": \"-Infinity\"");
        Assert.Empty(Check(neg));
        string word = ReplaceFirst(Good, "\"value\": 1.5", "\"value\": \"Infinity\"");
        Assert.Contains(Check(word), e => e.Contains("must be a number", StringComparison.Ordinal));
    }

    [Fact]
    public void A_non_object_document_is_reported_not_thrown()
    {
        Assert.NotEmpty(Check("[]"));
        Assert.NotEmpty(Check("{ \"schema\": \"gridsim-kind/2\", \"kind\": \"demo\", \"title\": \"t\", \"description\": \"d\", \"params\": [] }"));
        Assert.NotEmpty(Check("{ \"schema\": \"gridsim-kind/2\", \"kind\": \"demo\", \"title\": \"t\", \"description\": \"d\", \"params\": {} }"));
    }

    private static string ReplaceFirst(string text, string find, string replace)
    {
        int i = text.IndexOf(find, StringComparison.Ordinal);
        return i < 0 ? text : new StringBuilder(text).Remove(i, find.Length).Insert(i, replace).ToString();
    }
}
