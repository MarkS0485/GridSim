using System.Diagnostics;
using System.Text;
using System.Text.Json;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Lossless;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Tests.V2;
using Xunit;
using Xunit.Abstractions;
using static GridSim.Tests.Editor.EditorTestSupport;

namespace GridSim.Tests.Editor;

/// <summary>
/// Slice E3 of the case editor (SCHEMA-EDITOR-PLAN.md section 10): the case session engine (catalogue, operations,
/// reference index, undo / redo, licence at open). Proofs P1-P6 in memory (the session's bytes are written by a
/// test-only helper here; the real save is slice E4), the OLTC mirror, the bus count, integrity refusals and cascades,
/// and the gb-full shared-name rule.
/// </summary>
public class CaseSessionTests
{
    private readonly ITestOutputHelper _out;

    public CaseSessionTests(ITestOutputHelper output) => _out = output;

    /// <summary>A licence gate with no bus limit and a far-future expiry (Entitlement.IsExpired reads UtcNow).</summary>
    internal static LicenceGate Unlimited() => new(new Entitlement("test@example.invalid", "Test", LicenceTier.NetworkOperator, 0,
        new DateOnly(2026, 1, 1), new DateOnly(2999, 12, 31), "test-unlimited"));

    /// <summary>A licence gate with a bus limit and a far-future expiry.</summary>
    internal static LicenceGate Limited(int buses) => new(new Entitlement("test@example.invalid", "Test", LicenceTier.Personal, buses,
        new DateOnly(2026, 1, 1), new DateOnly(2999, 12, 31), "test-limited"));

    /// <summary>TEST-ONLY direct write of the session's dirty documents (the real save gate is slice E4).</summary>
    internal static void WriteCurrent(CaseSession s)
    {
        foreach (CaseDocument d in s.DirtyDocuments)
        {
            if (d.Current is null) File.Delete(d.Path);
            else File.WriteAllBytes(d.Path, d.Current.ToArray());
        }
    }

    private static int Gen(CaseSession s) => s.Doc("generation.json").Id;

    // =================================================================================================================
    // Open (P1 in memory), bus count, OLTC mirror, pre-flight
    // =================================================================================================================

    [Theory]
    [MemberData(nameof(BundledCases), MemberType = typeof(EditorTestSupport))]
    public void P1_opening_a_bundled_case_holds_every_file_byte_for_byte_and_nothing_is_dirty(string relative)
    {
        string dir = CaseDir(relative);
        CaseSession s = CaseSession.OpenV1(dir, Limited(1));   // bundled: the gate never counts it
        Assert.True(s.IsBundled);
        Assert.False(s.IsDirty);
        Assert.False(s.CanUndo);
        foreach (CaseDocument d in s.Documents)
        {
            Assert.Equal(File.Exists(d.Path), d.Exists);
            if (d.Exists) Assert.True(File.ReadAllBytes(d.Path).AsSpan().SequenceEqual(d.Text.Bytes), d.FileName);
        }
        // Files the loader does not read are listed and never part of the case (P10's premise).
        var expectedOthers = Directory.EnumerateFiles(dir).Select(p => Path.GetFileName(p))
            .Where(f => !V1Projector.LoaderFiles.Contains(f)).OrderBy(f => f, StringComparer.Ordinal).ToList();
        Assert.Equal(expectedOthers, s.OtherFiles);
        Assert.Empty(s.LeftoverFiles);
    }

    [Theory]
    [MemberData(nameof(BundledCases), MemberType = typeof(EditorTestSupport))]
    public void The_bus_count_equals_the_loaders(string relative)
    {
        CaseSession s = CaseSession.OpenV1(CaseDir(relative), Unlimited());
        Assert.Equal(JsonGridLoader.LoadDirectory(CaseDir(relative), dispatch: false).BusCount, s.BusCount);
    }

    [Theory]
    [MemberData(nameof(BundledCases), MemberType = typeof(EditorTestSupport))]
    public void Preflight_finds_no_problem_in_a_bundled_case(string relative)
    {
        CaseSession s = CaseSession.OpenV1(CaseDir(relative), Unlimited());
        Assert.Null(s.Preflight.LoaderError);
        Assert.Equal(0, s.Preflight.SchemaErrorCount);
    }

    [Theory]
    [MemberData(nameof(BundledCases), MemberType = typeof(EditorTestSupport))]
    public void The_OLTC_mirror_equals_the_solvers_mapping(string relative)
    {
        // The editor's file-level rule must assign every OLTC record to exactly the branch the solver drives (N046),
        // or a rename cascade could move a tap changer to another transformer.
        CaseSession s = CaseSession.OpenV1(CaseDir(relative), Unlimited());
        GridModel model = JsonGridLoader.LoadDirectory(CaseDir(relative), dispatch: false);
        int[] map = SteadyStateSolver.MapOltcRecordsToBranches(model);
        ReferenceIndex ix = ReferenceIndex.Build(s);
        Assert.Equal(model.Oltcs.Count, ix.Oltcs.Count);
        int lines = s.Doc("transmission.json").Exists ? s.Doc("transmission.json").Text.Root.Elements.Count : 0;
        for (int k = 0; k < map.Length; k++)
        {
            RecordRef? expected = map[k] < 0 ? null
                : map[k] < lines ? new RecordRef(s.Doc("transmission.json").Id, "", map[k])
                : new RecordRef(s.Doc("transformer.json").Id, "", map[k] - lines);
            Assert.True(expected == ix.OltcBranch[k], $"data/{relative}: OLTC {k}: solver {expected}, editor {ix.OltcBranch[k]}");
        }
    }

    // =================================================================================================================
    // Licence at open
    // =================================================================================================================

    [Fact]
    public void A_user_case_over_the_bus_limit_is_refused_at_open_and_one_within_it_opens()
    {
        using var copy = new TempCopy(CaseDir("uk"));   // outside data/, so not a Bundled Network
        CaseSession ok = CaseSession.OpenV1(copy.Dir, Limited(20));
        Assert.False(ok.IsBundled);
        Assert.Equal(20, ok.BusCount);
        var ex = Assert.Throws<CaseLicenceDeniedException>(() => CaseSession.OpenV1(copy.Dir, Limited(19)));
        Assert.Contains("20 buses", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_folder_without_buses_json_is_not_a_case()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-editor-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var ex = Assert.Throws<InvalidDataException>(() => CaseSession.OpenV1(dir, Unlimited()));
            Assert.Contains("buses.json", ex.Message, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_malformed_case_file_is_refused_at_open_naming_it()
    {
        using var copy = new TempCopy(CaseDir("uk"));
        File.WriteAllText(Path.Combine(copy.Dir, "demand.json"), "[ { \"bus\": 1, } ");
        var ex = Assert.Throws<InvalidDataException>(() => CaseSession.OpenV1(copy.Dir, Unlimited()));
        Assert.StartsWith("demand.json", ex.Message, StringComparison.Ordinal);
    }

    // =================================================================================================================
    // P2, P3, P4, P5: edits
    // =================================================================================================================

    [Fact]
    public void P2_edit_then_undo_is_clean_and_redo_repeats_exactly()
    {
        using var copy = new TempCopy(CaseDir("uk"));
        CaseSession s = CaseSession.OpenV1(copy.Dir, Unlimited());
        byte[] original = s.Doc("generation.json").Text.ToArray();

        OpResult r = CaseOps.SetField(s, Gen(s), "/0", "capacityMva", "1400");
        Assert.True(r.Ok && r.Changed, r.Message);
        Assert.Contains("1300 -> 1400", r.Message, StringComparison.Ordinal);
        Assert.True(s.IsDirty);
        byte[] edited = s.Doc("generation.json").Text.ToArray();

        s.Undo();
        Assert.False(s.IsDirty);
        Assert.Equal(original, s.Doc("generation.json").Text.ToArray());

        s.Redo();
        Assert.Equal(edited, s.Doc("generation.json").Text.ToArray());
        s.Undo();
        Assert.False(s.IsDirty);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.0")]
    [InlineData("0.00")]
    [InlineData("0e5")]
    public void P2_typing_the_same_value_is_no_change(string typed)
    {
        // uk generation #1 (North Scotland Onshore Wind) has "inertiaH": 0.0.
        CaseSession s = CaseSession.OpenV1(CaseDir("uk"), Unlimited());
        OpResult r = CaseOps.SetField(s, Gen(s), "/1", "inertiaH", typed);
        Assert.True(r.Ok);
        Assert.False(r.Changed, r.Message);
        Assert.False(s.CanUndo);
        Assert.False(s.IsDirty);
    }

    [Fact]
    public void P3_a_one_field_edit_changes_one_contiguous_range_from_old_token_to_new()
    {
        CaseSession s = CaseSession.OpenV1(CaseDir("uk"), Unlimited());
        var before = s.Documents.ToDictionary(d => d.FileName, d => d.Current?.ToArray(), StringComparer.Ordinal);
        Assert.True(CaseOps.SetField(s, Gen(s), "/17", "capacityMva", "1234.5").Changed);

        foreach (CaseDocument d in s.Documents)
        {
            byte[]? a = before[d.FileName], b = d.Current?.ToArray();
            if (d.FileName != "generation.json") { Assert.Equal(a, b); continue; }
            int prefix = 0;
            while (prefix < a!.Length && prefix < b!.Length && a[prefix] == b[prefix]) prefix++;
            int suffix = 0;
            while (suffix < a.Length - prefix && suffix < b!.Length - prefix && a[^(suffix + 1)] == b[^(suffix + 1)]) suffix++;
            string oldRange = Encoding.UTF8.GetString(a, prefix, a.Length - prefix - suffix);
            string newRange = Encoding.UTF8.GetString(b!, prefix, b!.Length - prefix - suffix);
            // The differing range is inside the one number token (common leading digits may be shared).
            Assert.Matches("^[0-9.]*$", oldRange);
            Assert.Matches("^[0-9.]*$", newRange);
            JsonSourceNode tok = d.Text.Root.Elements[17].FindMember("capacityMva", StringComparer.OrdinalIgnoreCase)!.Value;
            Assert.Equal("1234.5", d.Text.RawText(tok));
            Assert.True(prefix >= tok.Start && b.Length - suffix <= tok.End);
        }
    }

    [Fact]
    public void P4_a_one_field_edit_is_exactly_one_DTO_difference()
    {
        using var copy = new TempCopy(CaseDir("uk"));
        V1Case before = V1Projector.ReadV1(copy.Dir);
        CaseSession s = CaseSession.OpenV1(copy.Dir, Unlimited());
        Assert.True(CaseOps.SetField(s, Gen(s), "/17", "capacityMva", "1234.5").Changed);
        WriteCurrent(s);
        List<string> diffs = V1Projector.Differences(before, V1Projector.ReadV1(copy.Dir));
        Assert.Single(diffs);
        Assert.StartsWith("$.Generators[17].CapacityMva:", diffs[0], StringComparison.Ordinal);
    }

    [Fact]
    public void P5_unedited_cases_build_bit_identical_models_and_one_to_one_fields_differ_in_exactly_that_field()
    {
        // Edited, dispatch off, three fields that map 1:1 into the model.
        Check("uk", "buses.json", "/2", "vm", "1.03", "$.Buses[2].Vm");
        Check("uk", "transformer.json", "/0", "tapRatio", "1.05", null);      // transformers follow the lines in Branches
        Check("gb-synthetic", "transmission.json", "/0", "ratingMva", "6000", "$.Branches[0].RateAMva");

        void Check(string relative, string file, string pointer, string field, string typed, string? expectedPath)
        {
            using var copy = new TempCopy(CaseDir(relative));
            GridModel before = JsonGridLoader.LoadDirectory(copy.Dir, dispatch: false);
            CaseSession s = CaseSession.OpenV1(copy.Dir, Unlimited());
            OpResult r = CaseOps.SetField(s, s.Doc(file).Id, pointer, field, typed);
            Assert.True(r.Changed, r.Message);
            WriteCurrent(s);
            GridModel after = JsonGridLoader.LoadDirectory(copy.Dir, dispatch: false);
            List<string> diffs = ModelBits.Differences(before, after);
            Assert.True(diffs.Count == 1, $"{relative}/{file}{pointer} {field}: {string.Join(Environment.NewLine, diffs)}");
            if (expectedPath is not null) Assert.StartsWith(expectedPath + ":", diffs[0], StringComparison.Ordinal);
            else Assert.EndsWith(".TapRatio: a 1 (bits 3FF0000000000000), b 1.05 (bits 3FF0CCCCCCCCCCCD)", diffs[0], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void P5_an_unedited_save_path_builds_a_bit_identical_dispatched_model()
    {
        using var copy = new TempCopy(CaseDir("uk"));
        GridModel before = JsonGridLoader.LoadDirectory(copy.Dir, dispatch: true);
        CaseSession s = CaseSession.OpenV1(copy.Dir, Unlimited());
        CaseOps.SetField(s, Gen(s), "/0", "capacityMva", "1400");
        s.Undo();
        WriteCurrent(s);   // nothing is dirty, so nothing is written
        Assert.Empty(ModelBits.Differences(before, JsonGridLoader.LoadDirectory(copy.Dir, dispatch: true)));
    }

    // =================================================================================================================
    // P6: every record rewritten by the fresh-record writer loses nothing
    // =================================================================================================================

    [Theory]
    [MemberData(nameof(BundledCases), MemberType = typeof(EditorTestSupport))]
    public void P6_every_record_rewritten_fresh_gives_identical_DTOs_and_a_bit_identical_model(string relative)
    {
        using var copy = new TempCopy(CaseDir(relative));
        foreach (CaseFileSpec spec in CaseFileSpec.V1Files)
        {
            string path = Path.Combine(copy.Dir, spec.FileName);
            if (!File.Exists(path)) continue;
            JsonSourceText t = JsonSourceText.Load(path, StringComparer.OrdinalIgnoreCase);
            IEnumerable<JsonSourceNode> records = spec.IsArray ? t.Root.Elements : new[] { t.Root };
            var edits = new List<JsonTextEdit>();
            foreach (JsonSourceNode rec in records)
            {
                // The same writer AddRecord uses: every scalar re-written by JsonLiteral, laid out in the record's style.
                JsonFresh fresh = JsonFresh.FromElement(t.ElementOf(rec));
                JsonContainerStyle? style = rec.IsContainer ? JsonLayout.Detect(t, rec) : null;
                string text = JsonLayout.Format(fresh, style, JsonLayout.LineIndentOf(t, rec.Start), t.Newline);
                edits.Add(JsonTextEdit.ReplaceText(rec.Start, rec.Length, text));
            }
            File.WriteAllBytes(path, t.Apply(edits).Text.ToArray());
        }

        List<string> dto = V1Projector.Differences(V1Projector.ReadV1(CaseDir(relative)), V1Projector.ReadV1(copy.Dir));
        Assert.True(dto.Count == 0, $"data/{relative}: {string.Join(Environment.NewLine, dto)}");
        List<string> bits = ModelBits.Differences(JsonGridLoader.LoadDirectory(CaseDir(relative), dispatch: true), JsonGridLoader.LoadDirectory(copy.Dir, dispatch: true));
        Assert.True(bits.Count == 0, $"data/{relative}: {string.Join(Environment.NewLine, bits)}");
    }

    // =================================================================================================================
    // Typed input and references
    // =================================================================================================================

    [Theory]
    [InlineData("capacityMva", "12,5")]
    [InlineData("capacityMva", "NaN")]
    [InlineData("capacityMva", "-1")]          // schema minimum 0
    [InlineData("bus", "1.5")]
    [InlineData("bus", "999")]                 // no such bus
    [InlineData("role", "swing")]              // not a generator role
    [InlineData("type", "Hydro")]              // pattern: lower case
    [InlineData("nope", "1")]                  // no such field
    public void Bad_input_is_refused_and_changes_nothing(string field, string typed)
    {
        CaseSession s = CaseSession.OpenV1(CaseDir("uk"), Unlimited());
        OpResult r = CaseOps.SetField(s, Gen(s), "/0", field, typed);
        Assert.False(r.Ok, r.Message);
        Assert.False(string.IsNullOrWhiteSpace(r.Message));
        Assert.False(s.IsDirty);
        Assert.False(s.CanUndo);
    }

    [Fact]
    public void Enum_input_is_matched_case_insensitively_and_written_canonically()
    {
        CaseSession s = CaseSession.OpenV1(CaseDir("uk"), Unlimited());
        OpResult r = CaseOps.SetField(s, Gen(s), "/0", "role", "PQ");
        Assert.True(r.Changed, r.Message);
        Assert.Equal("\"pq\"", s.Doc("generation.json").Text.RawText(s.Doc("generation.json").Text.Resolve("/0/role")!));
    }

    [Fact]
    public void An_absent_field_is_inserted_in_schema_order_and_reset_removes_it()
    {
        CaseSession s = CaseSession.OpenV1(CaseDir("uk"), Unlimited());
        // uk generation #0 has name, bus, type, capacityMva, capacityFactor, voltageSetpoint, inertiaH: qMinMvar goes
        // after voltageSetpoint (its schema predecessor present), before inertiaH.
        Assert.True(CaseOps.SetField(s, Gen(s), "/0", "qMinMvar", "-650").Changed);
        JsonSourceNode rec = s.Doc("generation.json").Text.Resolve("/0")!;
        var names = rec.Members.Select(m => m.Name).ToList();
        Assert.Equal(names.IndexOf("voltageSetpoint") + 1, names.IndexOf("qMinMvar"));
        Assert.True(CaseOps.ResetField(s, Gen(s), "/0", "qMinMvar").Changed);
        Assert.False(s.IsDirty);
        Assert.False(CaseOps.ResetField(s, Gen(s), "/0", "bus").Ok);   // required
    }

    // =================================================================================================================
    // Integrity: delete, keys, cascades
    // =================================================================================================================

    [Fact]
    public void A_referenced_bus_cannot_be_deleted_or_re_keyed_by_SetField()
    {
        CaseSession s = CaseSession.OpenV1(CaseDir("uk"), Unlimited());
        int buses = s.Doc("buses.json").Id;
        OpResult del = CaseOps.DeleteRecord(s, new RecordRef(buses, "", 0));
        Assert.False(del.Ok);
        Assert.Contains("refer to", del.Message, StringComparison.Ordinal);
        Assert.Contains("generation.json", del.Message, StringComparison.Ordinal);
        OpResult set = CaseOps.SetField(s, buses, "/0", "id", "500");
        Assert.False(set.Ok);
        Assert.Contains("Rename (R)", set.Message, StringComparison.Ordinal);
        Assert.False(CaseOps.SetField(s, buses, "/0", "id", "2").Ok);   // collides with bus 2
        Assert.False(s.IsDirty);
    }

    [Fact]
    public void Renaming_a_bus_id_cascades_to_every_reference_in_one_undo_step()
    {
        using var copy = new TempCopy(CaseDir("uk"));
        CaseSession s = CaseSession.OpenV1(copy.Dir, Unlimited());
        var original = s.Documents.ToDictionary(d => d.FileName, d => d.Current?.ToArray(), StringComparer.Ordinal);
        int refs = ReferenceIndex.Build(s).BusReferrers(1).Count;
        Assert.True(refs > 0);

        OpResult preview = CaseOps.RenameKey(s, new RecordRef(s.Doc("buses.json").Id, "", 0), "1001", dryRun: true);
        Assert.False(preview.Changed);
        Assert.Contains($"{refs} reference(s)", preview.Message, StringComparison.Ordinal);
        Assert.False(s.IsDirty);

        OpResult r = CaseOps.RenameKey(s, new RecordRef(s.Doc("buses.json").Id, "", 0), "1001");
        Assert.True(r.Changed, r.Message);
        ReferenceIndex ix = ReferenceIndex.Build(s);
        Assert.Empty(ix.BusReferrers(1));
        Assert.Equal(refs, ix.BusReferrers(1001).Count);
        Assert.Equal(1, s.UndoDepth);

        // The renamed case still loads, and only bus ids / references moved.
        WriteCurrent(s);
        GridModel m = JsonGridLoader.LoadDirectory(copy.Dir, dispatch: false);
        Assert.Contains(m.Buses, b => b.Id == 1001);
        Assert.DoesNotContain(m.Buses, b => b.Id == 1);

        s.Undo();
        foreach (CaseDocument d in s.Documents) Assert.Equal(original[d.FileName], d.Current?.ToArray());
    }

    [Fact]
    public void Renaming_a_transformer_takes_its_OLTC_with_it()
    {
        CaseSession s = CaseSession.OpenV1(CaseDir("uk"), Unlimited());
        var t0 = new RecordRef(s.Doc("transformer.json").Id, "", 0);
        OpResult r = CaseOps.RenameKey(s, t0, "T1 renamed");
        Assert.True(r.Changed, r.Message);
        Assert.Equal("T1 renamed", s.Doc("onloadtapchanger.json").Text.Resolve("/0/transformer")!.StringValue);
        ReferenceIndex ix = ReferenceIndex.Build(s);
        Assert.Equal(t0, ix.OltcBranch[0]);
        Assert.False(CaseOps.RenameKey(s, t0, "T2 GSP S Scotland").Ok);   // collides
    }

    [Fact]
    public void A_shared_gb_full_transformer_name_renames_only_that_record_and_is_refused_once_a_boundary_refers_to_it()
    {
        using var copy = new TempCopy(CaseDir("gb-full"));
        // Moved 2026-10 (N190, Ruling R1, Mark delegated 2026-10-04): the bundled gb-full no longer carries duplicated
        // names (its 58 shared SGT names were made unique, names only). Before: the test took the first shared name of
        // the bundled data as it stood. After: it restores one of the former duplicates in its temporary copy (the
        // second Barking row gets its old name back), so the editor rule it pins is exercised exactly as before.
        string tx = Path.Combine(copy.Dir, "transformer.json");
        string text = File.ReadAllText(tx);
        const string twin = "\"SGT BARKC1 LPN:Barking x2/3@3595#2\"";
        Assert.Equal(1, text.Split(twin).Length - 1);
        File.WriteAllText(tx, text.Replace(twin, "\"SGT BARKC1 LPN:Barking x2/3@3595\"", StringComparison.Ordinal));
        CaseSession s = CaseSession.OpenV1(copy.Dir, Unlimited());
        ReferenceIndex ix = ReferenceIndex.Build(s);
        KeyValuePair<string, List<RecordRef>> shared = ix.Branches.Where(kv => kv.Value.Count > 1)
            .OrderBy(kv => kv.Key, StringComparer.Ordinal).First();
        RecordRef first = shared.Value[0], second = shared.Value[1];

        // No boundary refers to it: the rename touches that one record; the other keeps the shared name.
        OpResult ok = CaseOps.RenameKey(s, first, shared.Key + " (A)");
        Assert.True(ok.Changed, ok.Message);
        Assert.Equal(shared.Key, s.Documents[second.Document].Text.Resolve(second.Pointer + "/name")!.StringValue);
        s.Undo();

        // A boundary naming the shared branch makes the rename ambiguous: refused.
        OpResult add = CaseOps.AddRecord(s, s.Doc("boundaries.json").Id, "", -1, new[]
        {
            new KeyValuePair<string, string>("name", "Test boundary"),
            new KeyValuePair<string, string>("limitMw", "100"),
            new KeyValuePair<string, string>("branches", "[{\"branch\": " + JsonSerializer.Serialize(shared.Key) + ", \"direction\": 1}]"),
        });
        Assert.True(add.Changed, add.Message);
        OpResult refused = CaseOps.RenameKey(s, first, shared.Key + " (A)");
        Assert.False(refused.Ok);
        Assert.Contains("ambiguous", refused.Message, StringComparison.Ordinal);
    }

    // =================================================================================================================
    // Add / Duplicate / Delete
    // =================================================================================================================

    [Fact]
    public void Add_appends_a_valid_record_in_the_neighbours_style_and_refuses_bad_ones()
    {
        CaseSession s = CaseSession.OpenV1(CaseDir("uk"), Unlimited());
        int n = s.Doc("generation.json").Text.Root.Elements.Count;
        OpResult r = CaseOps.AddRecord(s, Gen(s), "", -1, new[]
        {
            new KeyValuePair<string, string>("bus", "3"),
            new KeyValuePair<string, string>("name", "New & Shiny"),
            new KeyValuePair<string, string>("type", "solar"),
            new KeyValuePair<string, string>("capacityMva", "50"),
        });
        Assert.True(r.Changed, r.Message);
        Assert.Equal(new RecordRef(Gen(s), "", n), r.Select);
        JsonSourceText t = s.Doc("generation.json").Text;
        Assert.Equal("{ \"name\": \"New & Shiny\", \"bus\": 3, \"type\": \"solar\", \"capacityMva\": 50 }", t.RawText(t.Root.Elements[n]));

        Assert.False(CaseOps.AddRecord(s, Gen(s), "", -1, new[] { new KeyValuePair<string, string>("name", "No bus") }).Ok);
        Assert.False(CaseOps.AddRecord(s, Gen(s), "", -1, new[] { new KeyValuePair<string, string>("bus", "4242") }).Ok);
        Assert.Equal(1, s.UndoDepth);
    }

    [Fact]
    public void Adding_to_an_absent_file_creates_it_and_undo_removes_it_again()
    {
        CaseSession s = CaseSession.OpenV1(CaseDir("uk"), Unlimited());
        CaseDocument classes = s.Doc("asset-classes.json");
        Assert.False(classes.Exists);
        OpResult r = CaseOps.AddRecord(s, classes.Id, "", 0, new[]
        {
            new KeyValuePair<string, string>("name", "gfl-test"),
            new KeyValuePair<string, string>("kind", "gridFollowingInverter"),
        });
        Assert.True(r.Changed, r.Message);
        Assert.True(classes.Exists && classes.IsDirty);
        Assert.True(JsonElement.DeepEquals(Element("[{\"name\":\"gfl-test\",\"kind\":\"gridFollowingInverter\"}]"), classes.Text.ToElement()));
        s.Undo();
        Assert.False(classes.Exists);
        Assert.False(s.IsDirty);
    }

    [Fact]
    public void Duplicating_a_bus_gives_the_copy_the_next_free_id()
    {
        CaseSession s = CaseSession.OpenV1(CaseDir("uk"), Unlimited());
        int max = ReferenceIndex.Build(s).MaxBusId;
        OpResult r = CaseOps.DuplicateRecord(s, new RecordRef(s.Doc("buses.json").Id, "", 0));
        Assert.True(r.Changed, r.Message);
        JsonSourceText t = s.Doc("buses.json").Text;
        Assert.Equal(max + 1, (int)t.Resolve("/1/id")!.IntegerValue!.Value);
        Assert.Equal(t.Resolve("/0/name")!.StringValue, t.Resolve("/1/name")!.StringValue);
        Assert.Equal(1, s.UndoDepth);   // the copy and its new id are one step
    }

    [Fact]
    public void Duplicating_a_generator_copies_its_bytes_and_delete_removes_it()
    {
        CaseSession s = CaseSession.OpenV1(CaseDir("uk"), Unlimited());
        byte[] original = s.Doc("generation.json").Text.ToArray();
        JsonSourceText t0 = s.Doc("generation.json").Text;
        string raw = t0.RawText(t0.Root.Elements[0]);
        OpResult dup = CaseOps.DuplicateRecord(s, new RecordRef(Gen(s), "", 0));
        Assert.True(dup.Changed, dup.Message);
        JsonSourceText t1 = s.Doc("generation.json").Text;
        Assert.Equal(raw, t1.RawText(t1.Root.Elements[1]));
        OpResult del = CaseOps.DeleteRecord(s, new RecordRef(Gen(s), "", 1));
        Assert.True(del.Changed, del.Message);
        Assert.Equal(original, s.Doc("generation.json").Text.ToArray());
        Assert.False(s.IsDirty);
    }

    // =================================================================================================================
    // Stations, normalisation, transactions, history limit
    // =================================================================================================================

    [Fact]
    public void The_station_hierarchy_is_deterministic_and_covers_every_unit_once()
    {
        CaseSession s = CaseSession.OpenV1(CaseDir("gb-full"), Unlimited());
        IReadOnlyList<FuelGroup> groups = StationIndex.Build(s);
        Assert.Equal(groups.Select(g => g.Type).OrderBy(t => t, StringComparer.Ordinal), groups.Select(g => g.Type));
        var all = groups.SelectMany(g => g.Stations).SelectMany(st => st.Units).ToList();
        Assert.Equal(s.Doc("generation.json").Text.Root.Elements.Count, all.Count);
        Assert.Equal(all.Count, all.Distinct().Count());
        foreach (FuelGroup g in groups)
            foreach (Station st in g.Stations)
                Assert.Equal(st.Units.OrderBy(u => u), st.Units);
        Assert.Contains(groups.SelectMany(g => g.Stations), st => st.Units.Count > 1);   // gb-full shares names
        Assert.Equal(groups.Select(g => g.Stations.Count), StationIndex.Build(s).Select(g => g.Stations.Count));
    }

    [Fact]
    public void Renaming_a_station_renames_every_unit_of_it()
    {
        CaseSession s = CaseSession.OpenV1(CaseDir("gb-full"), Unlimited());
        Station st = StationIndex.Build(s).SelectMany(g => g.Stations).Where(x => x.Units.Count > 1 && x.Name is not null).OrderBy(x => x.Name, StringComparer.Ordinal).First();
        OpResult r = CaseOps.RenameStation(s, st, "Renamed station");
        Assert.True(r.Changed, r.Message);
        foreach (int u in st.Units) Assert.Equal("Renamed station", s.Doc("generation.json").Text.Resolve("/" + u + "/name")!.StringValue);
        Assert.Equal(1, s.UndoDepth);
    }

    [Fact]
    public void Normalise_fixes_non_canonical_key_spelling_in_one_step()
    {
        using var copy = new TempCopy(CaseDir("uk"));
        string gen = Path.Combine(copy.Dir, "generation.json");
        File.WriteAllText(gen, File.ReadAllText(gen).Replace("\"capacityMva\"", "\"CapacityMVA\"", StringComparison.Ordinal));
        CaseSession s = CaseSession.OpenV1(copy.Dir, Unlimited());
        Assert.Null(s.Preflight.LoaderError);                 // the loader is case-insensitive
        Assert.True(s.Preflight.SchemaErrorCount > 0);        // the strict schema is not (Q1)
        OpResult r = CaseOps.NormaliseKeys(s);
        Assert.True(r.Changed, r.Message);
        Assert.Empty(BundledSchemas.V1Validate.Validate(s.Doc("generation.json").Text.ToElement(), "generation.schema.json"));
        Assert.False(CaseOps.NormaliseKeys(s).Changed);
    }

    [Fact]
    public void An_uncommitted_transaction_rolls_back_exactly()
    {
        CaseSession s = CaseSession.OpenV1(CaseDir("uk"), Unlimited());
        CaseDocument d = s.Doc("generation.json");
        byte[] original = d.Text.ToArray();
        using (CaseTransaction tx = s.Begin("abandoned"))
        {
            tx.Patch(d, JsonTextOps.RemoveElement(d.Text, d.Text.Root, 0));
            tx.Patch(d, JsonTextOps.RemoveElement(d.Text, d.Text.Root, 0));
            Assert.True(d.IsDirty);
        }
        Assert.Equal(original, d.Text.ToArray());
        Assert.False(s.CanUndo);
    }

    [Fact]
    public void The_undo_history_holds_at_most_1000_levels()
    {
        CaseSession s = CaseSession.OpenV1(CaseDir("uk"), Unlimited());
        for (int i = 0; i < CaseSession.UndoLimit + 5; i++)
            Assert.True(CaseOps.SetField(s, Gen(s), "/0", "capacityMva", (2000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture)).Changed);
        Assert.Equal(CaseSession.UndoLimit, s.UndoDepth);
        while (s.CanUndo) s.Undo();
        // Five levels fell off the bottom, so the oldest reachable state is the fifth edit, not the original.
        Assert.Equal("2004", s.Doc("generation.json").Text.RawText(s.Doc("generation.json").Text.Resolve("/0/capacityMva")!));
    }

    [Fact]
    public void Reparse_time_of_one_edit_on_the_largest_bundled_file_is_measured()
    {
        // SCHEMA-EDITOR-PLAN.md risk R4: one re-parse of gb-full generation.json (about 1.5 MB) per operation. This
        // records the time (see the test output) and fails only if it is pathologically slow.
        CaseSession s = CaseSession.OpenV1(CaseDir("gb-full"), Unlimited());
        var sw = Stopwatch.StartNew();
        const int edits = 5;
        for (int i = 0; i < edits; i++)
            Assert.True(CaseOps.SetField(s, Gen(s), "/100", "capacityMva", (10 + i).ToString(System.Globalization.CultureInfo.InvariantCulture)).Changed);
        sw.Stop();
        double perEdit = sw.Elapsed.TotalMilliseconds / edits;
        _out.WriteLine($"gb-full generation.json: {perEdit:F1} ms per SetField (re-parse, reference index, record validation).");
        Assert.True(perEdit < 30_000, $"one edit took {perEdit:F0} ms");
    }
}
