using System.Text;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Lossless;
using GridSim.Core.Model;
using GridSim.Tests.V2;
using Xunit;
using static GridSim.Tests.Editor.CaseSessionTests;
using static GridSim.Tests.Editor.EditorTestSupport;

namespace GridSim.Tests.Editor;

/// <summary>
/// Slice E4 of the case editor (SCHEMA-EDITOR-PLAN.md section 10): the save gate and the committer. Proofs P1-P5 end
/// to end through the real save, P10 (files the case does not read are never touched), the refusals (loader, licence,
/// external change, new warnings), the rollback of a failed multi-file commit, and check mode.
/// </summary>
public class SaveGateTests
{
    private static int Gen(CaseSession s) => s.Doc("generation.json").Id;

    private static void AssertUnchanged(Dictionary<string, (byte[] Bytes, DateTime Mtime)> before, TempCopy copy, params string[] except)
    {
        Dictionary<string, (byte[] Bytes, DateTime Mtime)> now = copy.Snapshot();
        Assert.Equal(before.Keys.OrderBy(k => k, StringComparer.Ordinal), now.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach ((string f, (byte[] bytes, DateTime mtime)) in before)
        {
            if (except.Contains(f)) continue;
            Assert.True(bytes.AsSpan().SequenceEqual(now[f].Bytes), $"{f}: bytes changed");
            Assert.True(mtime == now[f].Mtime, $"{f}: last-write time changed");
        }
    }

    // =================================================================================================================
    // P1, P2: nothing to save writes nothing
    // =================================================================================================================

    [Theory]
    [MemberData(nameof(BundledCases), MemberType = typeof(EditorTestSupport))]
    public void P1_open_and_save_writes_nothing(string relative)
    {
        using var copy = new TempCopy(CaseDir(relative));
        var before = copy.Snapshot();
        CaseSession s = CaseSession.OpenV1(copy.Dir, Unlimited());
        SaveReport r = SaveGate.Run(s);
        Assert.Equal(SaveOutcome.NothingToSave, r.Outcome);
        Assert.Equal("No changes to save.", r.Headline);
        AssertUnchanged(before, copy);
    }

    [Fact]
    public void P2_edit_then_undo_then_save_writes_nothing()
    {
        using var copy = new TempCopy(CaseDir("uk"));
        var before = copy.Snapshot();
        CaseSession s = CaseSession.OpenV1(copy.Dir, Unlimited());
        Assert.True(CaseOps.SetField(s, Gen(s), "/3", "capacityMva", "999").Changed);
        s.Undo();
        Assert.Equal(SaveOutcome.NothingToSave, SaveGate.Run(s).Outcome);
        AssertUnchanged(before, copy);
    }

    // =================================================================================================================
    // P3, P4, P5 end to end
    // =================================================================================================================

    [Fact]
    public void P3_P4_a_saved_one_field_edit_is_one_token_one_DTO_difference_and_touches_no_other_file()
    {
        using var copy = new TempCopy(CaseDir("uk"));
        var before = copy.Snapshot();
        V1Case dtoBefore = V1Projector.ReadV1(copy.Dir);
        CaseSession s = CaseSession.OpenV1(copy.Dir, Unlimited());
        Assert.True(CaseOps.SetField(s, Gen(s), "/17", "capacityMva", "1234.5").Changed);

        SaveReport r = SaveGate.Run(s);
        Assert.True(r.Saved, string.Join(Environment.NewLine, r.Lines()));
        Assert.Equal(new[] { "generation.json" }, r.FilesWritten);
        Assert.Contains(r.Notes, n => n.Contains("1234.5", StringComparison.Ordinal));
        Assert.False(s.IsDirty);
        Assert.True(s.CanUndo);   // the history survives a save

        AssertUnchanged(before, copy, "generation.json");
        byte[] a = before["generation.json"].Bytes, b = File.ReadAllBytes(Path.Combine(copy.Dir, "generation.json"));
        int prefix = 0;
        while (prefix < Math.Min(a.Length, b.Length) && a[prefix] == b[prefix]) prefix++;
        int suffix = 0;
        while (suffix < Math.Min(a.Length, b.Length) - prefix && a[^(suffix + 1)] == b[^(suffix + 1)]) suffix++;
        Assert.Matches("^[0-9.]*$", Encoding.UTF8.GetString(b, prefix, b.Length - prefix - suffix));

        List<string> diffs = V1Projector.Differences(dtoBefore, V1Projector.ReadV1(copy.Dir));
        Assert.Single(diffs);
        Assert.StartsWith("$.Generators[17].CapacityMva:", diffs[0], StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(copy.Dir, "*" + CaseSession.NewFileSuffix));
    }

    [Fact]
    public void P5_a_saved_bus_voltage_edit_moves_exactly_that_model_field()
    {
        using var copy = new TempCopy(CaseDir("uk"));
        GridModel before = JsonGridLoader.LoadDirectory(copy.Dir, dispatch: false);
        CaseSession s = CaseSession.OpenV1(copy.Dir, Unlimited());
        Assert.True(CaseOps.SetField(s, s.Doc("buses.json").Id, "/4", "vm", "0.98").Changed);
        Assert.True(SaveGate.Run(s).Saved);
        List<string> diffs = ModelBits.Differences(before, JsonGridLoader.LoadDirectory(copy.Dir, dispatch: false));
        Assert.Single(diffs);
        Assert.StartsWith("$.Buses[4].Vm:", diffs[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Saving_again_after_undo_writes_the_original_bytes_back()
    {
        using var copy = new TempCopy(CaseDir("uk"));
        byte[] original = File.ReadAllBytes(Path.Combine(copy.Dir, "generation.json"));
        CaseSession s = CaseSession.OpenV1(copy.Dir, Unlimited());
        CaseOps.SetField(s, Gen(s), "/0", "capacityMva", "1400");
        Assert.True(SaveGate.Run(s).Saved);
        s.Undo();
        Assert.True(s.IsDirty);   // the baseline moved to the saved state
        Assert.True(SaveGate.Run(s).Saved);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(copy.Dir, "generation.json")));
    }

    // =================================================================================================================
    // P10: files the case does not read
    // =================================================================================================================

    [Fact]
    public void P10_intl_metadata_files_stay_byte_identical_through_an_edit_and_save()
    {
        using var copy = new TempCopy(CaseDir("intl/france"));
        var before = copy.Snapshot();
        Assert.Contains("diagnostics.json", before.Keys);
        Assert.Contains("MODEL_CARD.md", before.Keys);
        Assert.Contains("ATTRIBUTION.txt", before.Keys);
        CaseSession s = CaseSession.OpenV1(copy.Dir, Unlimited());
        Assert.Contains("diagnostics.json", s.OtherFiles);
        Assert.True(CaseOps.SetField(s, Gen(s), "/0", "capacityMva", "321").Changed);
        Assert.True(SaveGate.Run(s).Saved);
        AssertUnchanged(before, copy, "generation.json");
    }

    // =================================================================================================================
    // Refusals: disk unchanged
    // =================================================================================================================

    [Fact]
    public void A_change_the_loader_refuses_is_not_saved_and_the_disk_is_unchanged()
    {
        using var copy = new TempCopy(CaseDir("uk"));
        var before = copy.Snapshot();
        CaseSession s = CaseSession.OpenV1(copy.Dir, Unlimited());
        // Remove the only slack: the record is still schema-valid, but the loader refuses a case with no slack bus.
        JsonSourceText gen = s.Doc("generation.json").Text;
        int slack = Enumerable.Range(0, gen.Root.Elements.Count).Single(i => RecordRead.Str(gen.Root.Elements[i], "role", StringComparer.OrdinalIgnoreCase) == "slack");
        Assert.True(CaseOps.SetField(s, Gen(s), "/" + slack, "role", "pv").Changed);

        SaveReport r = SaveGate.Run(s);
        Assert.Equal(SaveOutcome.Refused, r.Outcome);
        Assert.StartsWith("NOT SAVED: 1 error(s)", r.Headline, StringComparison.Ordinal);
        Assert.Contains(r.Errors, e => e.Message.Contains("slack", StringComparison.OrdinalIgnoreCase));
        Assert.True(s.IsDirty);
        AssertUnchanged(before, copy);
    }

    [Fact]
    public void New_model_warnings_need_an_explicit_yes_and_are_then_accepted()
    {
        using var copy = new TempCopy(CaseDir("uk"));
        var before = copy.Snapshot();
        CaseSession s = CaseSession.OpenV1(copy.Dir, Unlimited());
        // A second slack generator: the model is valid but warns "2 slack (reference) buses".
        JsonSourceText gen = s.Doc("generation.json").Text;
        int other = Enumerable.Range(0, gen.Root.Elements.Count).First(i => RecordRead.Str(gen.Root.Elements[i], "role", StringComparer.OrdinalIgnoreCase) != "slack");
        Assert.True(CaseOps.SetField(s, Gen(s), "/" + other, "role", "slack").Changed);

        SaveReport ask = SaveGate.Run(s);
        Assert.Equal(SaveOutcome.NeedsConfirmation, ask.Outcome);
        Assert.Contains(ask.NewWarnings, w => w.Contains("slack", StringComparison.Ordinal));
        AssertUnchanged(before, copy);

        Assert.True(SaveGate.Run(s, new SaveOptions(ConfirmNewWarnings: true)).Saved);
        // The warning is now part of the accepted set: the next save does not ask again.
        Assert.True(CaseOps.SetField(s, Gen(s), "/0", "capacityMva", "1301").Changed);
        Assert.Equal(SaveOutcome.Saved, SaveGate.Run(s).Outcome);
    }

    [Fact]
    public void A_file_changed_on_disk_since_open_refuses_the_save()
    {
        using var copy = new TempCopy(CaseDir("uk"));
        CaseSession s = CaseSession.OpenV1(copy.Dir, Unlimited());
        Assert.True(CaseOps.SetField(s, Gen(s), "/0", "capacityMva", "1400").Changed);
        string path = Path.Combine(copy.Dir, "generation.json");
        byte[] external = Encoding.UTF8.GetBytes(File.ReadAllText(path).Replace("1300", "1350", StringComparison.Ordinal));
        File.WriteAllBytes(path, external);

        SaveReport r = SaveGate.Run(s);
        Assert.Equal(SaveOutcome.ChangedOnDisk, r.Outcome);
        Assert.Contains("generation.json", r.Headline, StringComparison.Ordinal);
        Assert.Equal(external, File.ReadAllBytes(path));   // the other writer's content survives
    }

    [Fact]
    public void Adding_a_bus_over_the_licence_limit_is_refused_at_save()
    {
        using var copy = new TempCopy(CaseDir("uk"));
        var before = copy.Snapshot();
        CaseSession s = CaseSession.OpenV1(copy.Dir, Limited(20));
        Assert.True(CaseOps.DuplicateRecord(s, new RecordRef(s.Doc("buses.json").Id, "", 0)).Changed);
        Assert.Equal(21, s.BusCount);
        SaveReport r = SaveGate.Run(s);
        Assert.Equal(SaveOutcome.Refused, r.Outcome);
        Assert.Contains(r.Errors, e => e.Message.StartsWith("licence:", StringComparison.Ordinal));
        AssertUnchanged(before, copy);
    }

    // =================================================================================================================
    // Commit failures roll back
    // =================================================================================================================

    [Fact]
    public void A_failure_part_way_through_the_commit_restores_every_replaced_file()
    {
        using var copy = new TempCopy(CaseDir("uk"));
        var before = copy.Snapshot();
        CaseSession s = CaseSession.OpenV1(copy.Dir, Unlimited());
        Assert.True(CaseOps.SetField(s, Gen(s), "/0", "capacityMva", "1400").Changed);
        Assert.True(CaseOps.SetField(s, s.Doc("demand.json").Id, "/0", "pMw", "650").Changed);

        int calls = 0;
        SaveReport r = SaveGate.Run(s, new SaveOptions(BeforeReplace: f =>
        {
            if (++calls == 2) throw new IOException($"simulated failure before replacing {f}");
        }));
        Assert.Equal(SaveOutcome.CommitFailed, r.Outcome);
        Assert.StartsWith("NOT SAVED", r.Headline, StringComparison.Ordinal);
        Assert.Contains("restored from memory", r.Headline, StringComparison.Ordinal);
        foreach ((string f, (byte[] bytes, _)) in before)
            Assert.True(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(copy.Dir, f))), $"{f} not restored");
        Assert.Empty(Directory.EnumerateFiles(copy.Dir, "*" + CaseSession.NewFileSuffix));
        Assert.True(s.IsDirty);

        // The edits are still in memory and a normal save then succeeds.
        Assert.True(SaveGate.Run(s).Saved);
        Assert.Equal(SaveOutcome.NothingToSave, SaveGate.Run(s).Outcome);
    }

    [Fact]
    public void A_file_created_by_an_edit_is_written_and_loads()
    {
        using var copy = new TempCopy(CaseDir("uk"));
        File.Delete(Path.Combine(copy.Dir, "demand.json"));
        CaseSession s = CaseSession.OpenV1(copy.Dir, Unlimited());
        Assert.False(s.Doc("demand.json").Exists);
        Assert.True(CaseOps.AddRecord(s, s.Doc("demand.json").Id, "", 0, new[]
        {
            new KeyValuePair<string, string>("name", "Test load"),
            new KeyValuePair<string, string>("bus", "101"),
            new KeyValuePair<string, string>("pMw", "100"),
        }).Changed);
        // A case opened with no demand at all may warn differently once it has one; that is not what this proves.
        SaveReport r = SaveGate.Run(s, new SaveOptions(ConfirmNewWarnings: true));
        Assert.True(r.Saved, string.Join(Environment.NewLine, r.Lines()));
        GridModel m = JsonGridLoader.LoadDirectory(copy.Dir, dispatch: false);
        Assert.Equal(100.0, m.Buses.Single(b => b.Id == 101).Pd);
    }

    [Fact]
    public void A_leftover_new_file_is_reported_at_open_and_in_the_save_report()
    {
        using var copy = new TempCopy(CaseDir("uk"));
        File.WriteAllText(Path.Combine(copy.Dir, "generation.json" + CaseSession.NewFileSuffix), "[]");
        CaseSession s = CaseSession.OpenV1(copy.Dir, Unlimited());
        Assert.Equal(new[] { "generation.json" + CaseSession.NewFileSuffix }, s.LeftoverFiles);
        Assert.DoesNotContain("generation.json" + CaseSession.NewFileSuffix, s.OtherFiles);
        Assert.Contains(SaveGate.Run(s).Notes, n => n.Contains("interrupted save", StringComparison.Ordinal));
    }

    // =================================================================================================================
    // Check mode
    // =================================================================================================================

    [Theory]
    [MemberData(nameof(BundledCases), MemberType = typeof(EditorTestSupport))]
    public void Check_passes_on_every_bundled_case_and_writes_nothing(string relative)
    {
        using var copy = new TempCopy(CaseDir(relative));
        var before = copy.Snapshot();
        SaveReport r = SaveGate.Run(CaseSession.OpenV1(copy.Dir, Unlimited()), new SaveOptions(CheckOnly: true));
        Assert.True(r.Outcome == SaveOutcome.CheckPassed, string.Join(Environment.NewLine, r.Lines()));
        AssertUnchanged(before, copy);
    }

    [Fact]
    public void Check_reports_a_non_canonical_key_and_a_dirty_file_with_it_cannot_be_saved()
    {
        using var copy = new TempCopy(CaseDir("uk"));
        string gen = Path.Combine(copy.Dir, "generation.json");
        File.WriteAllText(gen, File.ReadAllText(gen).Replace("\"inertiaH\"", "\"InertiaH\"", StringComparison.Ordinal));
        CaseSession s = CaseSession.OpenV1(copy.Dir, Unlimited());
        SaveReport check = SaveGate.Run(s, new SaveOptions(CheckOnly: true));
        Assert.Equal(SaveOutcome.CheckFailed, check.Outcome);
        Assert.Contains(check.Errors, e => e.File == "generation.json" && e.Pointer.EndsWith("/InertiaH", StringComparison.Ordinal));

        // Q1 strict: once generation.json is dirty, its non-canonical keys refuse the save until normalised.
        Assert.True(CaseOps.SetField(s, Gen(s), "/0", "capacityMva", "1400").Changed);
        Assert.Equal(SaveOutcome.Refused, SaveGate.Run(s).Outcome);
        Assert.True(CaseOps.NormaliseKeys(s).Changed);
        Assert.True(SaveGate.Run(s).Saved);
    }
}
