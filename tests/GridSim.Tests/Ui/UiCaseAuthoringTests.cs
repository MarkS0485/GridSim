using System.Globalization;
using System.Text;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Lossless;
using GridSim.Core.Licensing;
using GridSim.Tests.Editor;
using GridSim.Tests.V2;
using GridSim.Ui.Assets.Editing;
using GridSim.Ui.Assets.Index;
using GridSim.Ui.Assets.Keys;
using Xunit;

namespace GridSim.Tests.Ui;

/// <summary>
/// UI programme Phase 4.1: the public CaseAuthoring wrapper over the case editor's engine, on temporary copies only (no
/// test writes into data/ or any bundled case). Pinned: edit, undo, redo, Check and Save round trips on a v1 case folder
/// and a v2 case, with the saved bytes compared byte for byte with what the engine's lossless layer says (only the edited
/// value token changes, every other byte of every file is as before); undo to the original gives the original bytes; the
/// undo limit is the engine's; a bundled case refuses Save and its Save As writes a new case of the user's own while the
/// bundled files and their content hashes stay exactly as shipped; Save As never overwrites and never writes a bundled
/// location; a refused delete lists every referrer; a rename cascades in one undo step; Add refuses a missing field or a
/// dangling reference; a case with an asset overlay checks and saves with it, and one whose overlay fails the overlay
/// checker is refused, writing nothing (changed 2026-10-20, Phase 5b: until then any overlay was refused outright);
/// MATPOWER, a replay and the bridge have no Author mode, with the reason (plan Q9).
/// </summary>
public sealed class UiCaseAuthoringTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static LicenceGate Gate() => CaseSessionTests.Unlimited();

    // No real data root: a temporary copy is never bundled, whatever the test does.
    private static readonly IReadOnlyList<string> NoRoots = Array.Empty<string>();

    private static CaseAuthoring OpenV1(string dir, IReadOnlyList<string>? roots = null, string? users = null)
        => CaseAuthoring.Open(AssetCaseSource.V1(dir), Gate(), roots ?? NoRoots, users);

    private static CaseAuthoring OpenV2(string manifest, IReadOnlyList<string>? roots = null)
        => CaseAuthoring.Open(AssetCaseSource.V2(manifest), Gate(), roots ?? NoRoots);

    private static Dictionary<string, byte[]> Files(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetRelativePath(dir, p), File.ReadAllBytes, StringComparer.Ordinal);

    // The engine's expectation for a one-token edit: the original bytes with the value token of member
    // <member> of the object at <pointer> replaced by <literal>, nothing else changed.
    private static byte[] Spliced(byte[] original, string name, StringComparer cmp, string pointer, string member, string literal)
    {
        JsonSourceText t = JsonSourceText.Parse(original, name, cmp);
        JsonSourceNode value = t.Resolve(pointer)!.FindMember(member, cmp)!.Value;
        byte[] lit = Encoding.UTF8.GetBytes(literal);
        var result = new byte[original.Length - value.Length + lit.Length];
        original.AsSpan(0, value.Start).CopyTo(result);
        lit.CopyTo(result.AsSpan(value.Start));
        original.AsSpan(value.Start + value.Length).CopyTo(result.AsSpan(value.Start + lit.Length));
        return result;
    }

    private static AuthoringRecordSet Set(CaseAuthoring a, string role) => a.Collections().First(c => c.Role == role);

    private static CaseRef Rec(CaseAuthoring a, string role, int index) => a.Records(Set(a, role))[index].Ref;

    // =================================================================================================================
    // Round trips
    // =================================================================================================================

    [Fact]
    public void V1_edit_undo_redo_check_save_round_trips_and_only_the_edited_token_changes()
    {
        using var temp = new EditorTestSupport.TempCopy(EditorTestSupport.CaseDir("uk"));
        Dictionary<string, byte[]> before = Files(temp.Dir);
        CaseAuthoring a = OpenV1(temp.Dir);
        Assert.Equal(AuthoringFormat.V1Folder, a.Format);
        Assert.False(a.IsBundled);
        Assert.False(a.IsDirty);
        Assert.False(a.HasOverlay);
        Assert.Equal(CaseSession.UndoLimit, CaseAuthoring.UndoLimit);

        CaseRef line0 = Rec(a, CaseRoles.Lines, 0);
        Assert.Equal("transmission.json", line0.FileName);
        FieldGridModel grid = a.FieldGrid(line0);
        FieldRow r = grid.Row("rPu");
        Assert.Equal(FieldState.Set, r.State);
        Assert.Equal("0.020", r.Literal);   // the literal exactly as written (not re-formatted)
        long rev = a.Revision;
        int changed = 0;
        a.Changed += (_, _) => changed++;

        // Edit: one undo step, the file dirty, nothing on disk.
        AuthoringResult ok = grid.Edit("rPu", "0.025");
        Assert.True(ok.Ok, ok.Message);
        Assert.True(ok.Changed);
        Assert.True(a.IsDirty);
        Assert.Equal(new[] { "transmission.json" }, a.DirtyFiles);
        Assert.Equal(1, a.UndoDepth);
        Assert.Equal(rev + 1, a.Revision);
        Assert.Equal(1, changed);
        Assert.Equal(before, Files(temp.Dir));
        Assert.Equal("0.025", a.FieldGrid(line0).Row("rPu").Literal);

        // The same value again is no change and no undo step.
        AuthoringResult same = a.FieldGrid(line0).Edit("rPu", "0.0250");
        Assert.True(same.Ok);
        Assert.False(same.Changed);
        Assert.Equal(1, a.UndoDepth);

        // An invalid literal is refused with the reason, and changes nothing.
        AuthoringResult bad = a.FieldGrid(line0).Edit("rPu", "1e");
        Assert.False(bad.Ok);
        Assert.StartsWith(a.FieldGrid(line0).Row("rPu").Label, bad.Message, StringComparison.Ordinal);
        Assert.Equal(1, a.UndoDepth);
        AuthoringResult badInt = a.FieldGrid(line0).Edit("circuits", "2.5");
        Assert.False(badInt.Ok, badInt.Message);

        // Undo puts the bytes back exactly (the document is clean again); redo re-applies.
        AuthoringResult u = a.Undo();
        Assert.True(u.Ok, u.Message);
        Assert.False(a.IsDirty);
        Assert.True(a.Session.Doc("transmission.json").Text.Bytes.SequenceEqual(before["transmission.json"]));
        Assert.True(a.Redo().Ok);
        Assert.True(a.IsDirty);
        Assert.False(a.Redo().Ok);   // nothing more to redo: refused with the reason
        Assert.Equal("Nothing to redo.", a.Redo().Message);

        // Check: the gate's check, writing nothing.
        AuthoringReport check = a.Check();
        Assert.Equal(AuthoringOutcome.CheckPassed, check.Outcome);
        Assert.Same(check, a.LastCheck);
        Assert.Equal(a.Revision, a.LastCheckRevision);
        Assert.Equal(before, Files(temp.Dir));

        // Save: only transmission.json is written, and it differs from the original only in that one token.
        AuthoringReport saved = a.Save();
        Assert.True(saved.Saved, string.Join(Environment.NewLine, saved.Lines()));
        Assert.Equal(new[] { "transmission.json" }, saved.FilesWritten);
        Assert.False(a.IsDirty);
        Assert.True(a.SavedThisSession);
        Dictionary<string, byte[]> after = Files(temp.Dir);
        Assert.Equal(before.Keys.OrderBy(k => k, StringComparer.Ordinal), after.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (string f in before.Keys.Where(k => k != "transmission.json")) Assert.Equal(before[f], after[f]);
        Assert.Equal(Spliced(before["transmission.json"], "transmission.json", StringComparer.OrdinalIgnoreCase, "/0", "rPu", "0.025"),
            after["transmission.json"]);
        // A second save has nothing to save.
        Assert.Equal(AuthoringOutcome.NothingToSave, a.Save().Outcome);
    }

    [Fact]
    public void V2_edit_undo_redo_check_save_round_trips_and_only_the_edited_token_changes()
    {
        using var f = new UiOverlayFixture(withOverlay: false);
        Dictionary<string, byte[]> before = Files(f.CaseDir);
        CaseAuthoring a = OpenV2(f.ManifestPath);
        Assert.Equal(AuthoringFormat.V2Case, a.Format);
        Assert.False(a.HasOverlay);
        Assert.Null(a.OverlayRefusal);

        CaseRef line0 = Rec(a, CaseRoles.Lines, 0);
        Assert.Equal(V1Projector.NetworkFileName, line0.FileName);
        Assert.Equal("/lines", line0.ArrayPointer);
        string old = a.FieldGrid(line0).Row("rPu").Literal;
        Assert.NotEqual("0.025", old);

        Assert.True(a.FieldGrid(line0).Edit("rPu", "0.025").Ok);
        Assert.Equal(new[] { V1Projector.NetworkFileName }, a.DirtyFiles);
        Assert.True(a.Undo().Ok);
        Assert.False(a.IsDirty);
        Assert.True(a.Session.Doc(V1Projector.NetworkFileName).Text.Bytes.SequenceEqual(before[V1Projector.NetworkFileName]));
        Assert.True(a.Redo().Ok);

        AuthoringReport check = a.Check();
        Assert.Equal(AuthoringOutcome.CheckPassed, check.Outcome);
        AuthoringReport saved = a.Save();
        Assert.True(saved.Saved, string.Join(Environment.NewLine, saved.Lines()));
        Dictionary<string, byte[]> after = Files(f.CaseDir);
        foreach (string k in before.Keys.Where(k => k != V1Projector.NetworkFileName)) Assert.Equal(before[k], after[k]);
        Assert.Equal(Spliced(before[V1Projector.NetworkFileName], V1Projector.NetworkFileName, StringComparer.Ordinal, "/lines/0", "rPu", "0.025"),
            after[V1Projector.NetworkFileName]);
        // The saved case is the run pipeline's v2 source again (the manifest).
        Assert.Equal(Path.GetFullPath(f.ManifestPath), a.RunSource("x").V2Case);
    }

    [Fact]
    public void The_undo_history_holds_the_engines_limit_and_no_more()
    {
        using var temp = new EditorTestSupport.TempCopy(EditorTestSupport.CaseDir("uk"));
        CaseAuthoring a = OpenV1(temp.Dir);
        CaseRef bus = Rec(a, CaseRoles.Buses, 0);
        for (int i = 0; i <= CaseAuthoring.UndoLimit; i++)
            Assert.True(a.FieldGrid(bus).Edit("x", (i % 2 == 0 ? 0.5 : 0.25).ToString(Inv)).Changed);
        Assert.Equal(CaseAuthoring.UndoLimit, a.UndoDepth);
        int undone = 0;
        while (a.CanUndo) { Assert.True(a.Undo().Ok); undone++; }
        Assert.Equal(CaseAuthoring.UndoLimit, undone);
        Assert.Equal("Nothing to undo.", a.Undo().Message);
    }

    // =================================================================================================================
    // Bundled cases: Save As only
    // =================================================================================================================

    [Fact]
    public void A_bundled_case_refuses_Save_and_Save_As_writes_the_users_copy_leaving_the_bundled_files_and_hashes_unchanged()
    {
        string uk = EditorTestSupport.CaseDir("uk");
        Assert.True(BundledNetworks.IsBundledPath(uk), "data/uk must be a bundled case here");
        Dictionary<string, byte[]> shipped = Files(uk);
        Dictionary<string, string> hashes = Directory.EnumerateFiles(uk).ToDictionary(p => p, BundledNetworks.ContentHash, StringComparer.Ordinal);
        string users = Path.Combine(Path.GetTempPath(), "gridsim-authoring-users-" + Guid.NewGuid().ToString("N"));
        try
        {
            // The real data roots: the location rule makes the case bundled.
            CaseAuthoring a = CaseAuthoring.Open(AssetCaseSource.V1(uk), Gate(), bundledRoots: null, userCasesFolder: users);
            Assert.True(a.IsBundled);
            Assert.Equal(Path.Combine(users, "uk-copy"), a.SuggestSaveAsFolder());
            CaseRef line0 = Rec(a, CaseRoles.Lines, 0);
            Assert.True(a.FieldGrid(line0).Edit("rPu", "0.025").Ok);

            AuthoringReport refused = a.Save();
            Assert.Equal(AuthoringOutcome.Refused, refused.Outcome);
            Assert.StartsWith("NOT SAVED: ", refused.Headline, StringComparison.Ordinal);
            Assert.Contains("bundled case", refused.Headline, StringComparison.Ordinal);
            Assert.Contains("Save As", refused.Headline, StringComparison.Ordinal);
            Assert.Empty(refused.FilesWritten);
            Assert.True(a.IsDirty);   // the edit is kept in memory
            Assert.Equal(shipped, Files(uk));

            string target = a.SuggestSaveAsFolder();
            AuthoringReport copy = a.SaveAs(target);
            Assert.True(copy.Saved, string.Join(Environment.NewLine, copy.Lines()));
            // Every file of the case is in the copy: the edited one with only its token changed, the rest byte for byte.
            Dictionary<string, byte[]> written = Files(target);
            Assert.Equal(shipped.Keys.OrderBy(k => k, StringComparer.Ordinal), written.Keys.OrderBy(k => k, StringComparer.Ordinal));
            foreach (string k in shipped.Keys.Where(k => k != "transmission.json")) Assert.Equal(shipped[k], written[k]);
            Assert.Equal(Spliced(shipped["transmission.json"], "transmission.json", StringComparer.OrdinalIgnoreCase, "/0", "rPu", "0.025"),
                written["transmission.json"]);
            // The authoring now edits the copy: clean, not bundled, Save works in place there.
            Assert.Equal(Path.GetFullPath(target), a.RootPath);
            Assert.False(a.IsBundled);
            Assert.False(a.IsDirty);
            Assert.True(a.SavedThisSession);
            Assert.True(a.FieldGrid(Rec(a, CaseRoles.Lines, 0)).Edit("xPu", "0.21").Ok);
            Assert.True(a.Save().Saved);

            // The bundled case is exactly as shipped: bytes, content hashes and the bundled verdict.
            Assert.Equal(shipped, Files(uk));
            foreach ((string path, string hash) in hashes) Assert.Equal(hash, BundledNetworks.ContentHash(path));
            Assert.True(BundledNetworks.IsBundledPath(uk));
        }
        finally
        {
            if (Directory.Exists(users)) Directory.Delete(users, recursive: true);
        }
    }

    [Fact]
    public void Save_As_never_overwrites_never_writes_a_bundled_location_and_never_its_own_folder()
    {
        // A fake data root holding a copy of data/uk at <root>/uk: bundled by location, so nothing near data/ is touched.
        string root = Path.Combine(Path.GetTempPath(), "gridsim-authoring-root-" + Guid.NewGuid().ToString("N"));
        string other = Path.Combine(Path.GetTempPath(), "gridsim-authoring-other-" + Guid.NewGuid().ToString("N"));
        try
        {
            string uk = Path.Combine(root, "uk");
            Directory.CreateDirectory(uk);
            foreach (string f in Directory.EnumerateFiles(EditorTestSupport.CaseDir("uk"))) File.Copy(f, Path.Combine(uk, Path.GetFileName(f)));
            Dictionary<string, byte[]> shipped = Files(uk);
            CaseAuthoring a = OpenV1(uk, new[] { root });
            Assert.True(a.IsBundled);
            Assert.True(a.FieldGrid(Rec(a, CaseRoles.Buses, 0)).Edit("x", "0.5").Ok);

            Assert.Equal(AuthoringOutcome.Refused, a.Save().Outcome);
            // Into a bundled folder (the case's own, another bundled folder): refused before anything is created.
            foreach (string t in new[] { Path.Combine(uk, "inner"), Path.Combine(root, "gb-full", "x"), uk })
            {
                AuthoringReport r = a.SaveAs(t);
                Assert.Equal(AuthoringOutcome.Refused, r.Outcome);
                Assert.StartsWith("NOT SAVED: ", r.Headline, StringComparison.Ordinal);
                Assert.False(Directory.Exists(t) && t != uk, t + " was created");
            }
            // A folder that is not empty: refused, its file untouched.
            Directory.CreateDirectory(other);
            File.WriteAllText(Path.Combine(other, "keep.txt"), "keep");
            AuthoringReport full = a.SaveAs(other);
            Assert.Equal(AuthoringOutcome.Refused, full.Outcome);
            Assert.Contains("not empty", full.Headline, StringComparison.Ordinal);
            Assert.Equal(new[] { "keep.txt" }, Directory.EnumerateFiles(other).Select(Path.GetFileName));
            Assert.Equal(shipped, Files(uk));
            Assert.True(a.IsDirty);
            Assert.Equal(Path.GetFullPath(uk), a.RootPath);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (Directory.Exists(other)) Directory.Delete(other, recursive: true);
        }
    }

    // =================================================================================================================
    // Record actions
    // =================================================================================================================

    [Fact]
    public void A_refused_delete_lists_every_referrer_and_changes_nothing()
    {
        using var temp = new EditorTestSupport.TempCopy(EditorTestSupport.CaseDir("uk"));
        CaseAuthoring a = OpenV1(temp.Dir);
        CaseRef bus1 = Rec(a, CaseRoles.Buses, 0);
        IReadOnlyList<WhereUsedRow> used = RecordActions.WhereUsed(a, bus1);
        Assert.NotEmpty(used);
        Assert.True(used.Count <= CaseOps.ReferrersShown, "the uk case has fewer referrers of bus 1 than the refusal lists");
        // The where-used list is the engine's, exactly.
        IReadOnlyList<ReferenceIndex.Referrer> engine = CaseOps.ReferrersOf(a.Session, bus1.ToRecordRef());
        Assert.Equal(engine.Select(r => (r.Field, r.What)), used.Select(r => (r.Field, r.What)));

        AuthoringResult r = RecordActions.Delete(a, bus1);
        Assert.False(r.Ok);
        Assert.StartsWith("Delete refused: " + used.Count.ToString(Inv) + " record(s) refer to", r.Message, StringComparison.Ordinal);
        foreach (WhereUsedRow u in used) Assert.Contains(u.What + " (" + u.Field + ")", r.Message, StringComparison.Ordinal);
        Assert.False(a.IsDirty);
        Assert.False(a.CanUndo);
    }

    [Fact]
    public void Rename_cascades_in_one_undo_step_and_duplicate_add_and_delete_go_through_the_engine()
    {
        using var temp = new EditorTestSupport.TempCopy(EditorTestSupport.CaseDir("uk"));
        Dictionary<string, byte[]> before = Files(temp.Dir);
        CaseAuthoring a = OpenV1(temp.Dir);
        CaseRef bus1 = Rec(a, CaseRoles.Buses, 0);
        Assert.Equal("id", RecordActions.KeyField(a, bus1));
        int refs = RecordActions.WhereUsed(a, bus1).Count;

        AuthoringResult dry = RecordActions.Rename(a, bus1, "1001", dryRun: true);
        Assert.True(dry.Ok);
        Assert.False(dry.Changed);
        Assert.StartsWith("Rename id 1 -> 1001: " + refs.ToString(Inv) + " reference(s) follow", dry.Message, StringComparison.Ordinal);
        Assert.False(a.IsDirty);

        AuthoringResult ren = RecordActions.Rename(a, bus1, "1001");
        Assert.True(ren.Ok, ren.Message);
        Assert.Equal(1, a.UndoDepth);   // one step across every file
        Assert.Equal(refs, RecordActions.WhereUsed(a, Rec(a, CaseRoles.Buses, 0)).Count);   // every reference followed
        Assert.True(a.Undo().Ok);
        foreach (CaseDocument d in a.Session.Documents.Where(d => d.Exists))
            Assert.True(d.Text.Bytes.SequenceEqual(before[d.FileName]), d.FileName + " is not back to its original bytes");

        // Duplicate a line, then undo: back to the original bytes.
        AuthoringResult dup = RecordActions.Duplicate(a, Rec(a, CaseRoles.Lines, 0));
        Assert.True(dup.Ok, dup.Message);
        Assert.Equal(Rec(a, CaseRoles.Lines, 1), dup.Select);
        Assert.True(a.Undo().Ok);
        Assert.False(a.IsDirty);

        // Add a demand: refused without its required bus, refused for a bus that does not exist, done for a real one.
        AuthoringRecordSet demands = Set(a, CaseRoles.Demands);
        Assert.Equal(new[] { "bus" }, RecordActions.RequiredFields(a, demands).Select(f => f.Field));
        AuthoringResult missing = RecordActions.Add(a, demands, Array.Empty<KeyValuePair<string, string>>());
        Assert.False(missing.Ok);
        Assert.Contains("needs:", missing.Message, StringComparison.Ordinal);
        AuthoringResult dangling = RecordActions.Add(a, demands, new[] { new KeyValuePair<string, string>("bus", "9999") });
        Assert.False(dangling.Ok);
        Assert.Contains("bus 9999 does not exist", dangling.Message, StringComparison.Ordinal);
        int count = demands.Count;
        AuthoringResult added = RecordActions.Add(a, demands, new[] { new KeyValuePair<string, string>("bus", "1") });
        Assert.True(added.Ok, added.Message);
        Assert.Equal(count + 1, Set(a, CaseRoles.Demands).Count);
        Assert.Equal(count, added.Select!.Index);
        // The new record can be deleted (nothing refers to a demand), and reset to absent works on a written field.
        Assert.False(RecordActions.ResetToAbsent(a, added.Select, "bus").Ok);   // required: refused
        Assert.True(RecordActions.Delete(a, added.Select).Ok);
        Assert.Equal(count, Set(a, CaseRoles.Demands).Count);
        Assert.True(RecordActions.ResetToAbsent(a, Rec(a, CaseRoles.Demands, 0), "powerFactor").Ok);
        Assert.Equal(FieldState.Absent, a.FieldGrid(Rec(a, CaseRoles.Demands, 0)).Row("powerFactor").State);
    }

    // =================================================================================================================
    // The asset overlay: checked and saved with the case (Phase 5b)
    // =================================================================================================================

    // Test changed 2026-10-20 (Phase 5b): this test pinned the Phase 4.1 refusal of Check, Save and Save As for a case
    // with an asset overlay ("fail loud until overlay editing", A_case_with_an_asset_overlay_refuses_Check_Save_and_Save_As_
    // with_a_clear_message_and_writes_nothing). Phase 5b lifts that refusal by design: the engine opens the overlay files
    // as session documents, the save gate checks them (schema, V2AssetChecker, the v2 loader on a staged copy holding
    // them) and the committer writes them. The replacement assertions are at least as strong: the same edit now Checks
    // and Saves, with exactly the edited token changed and every other byte of every file (the overlay files included)
    // as before; Save As copies every file, the overlay files byte for byte; and a case whose overlay FAILS the checker is
    // still refused by Check, Save and Save As, listing each of its problems, writing nothing and keeping the edit.
    [Fact]
    public void A_case_with_an_asset_overlay_checks_and_saves_with_it_and_a_failing_overlay_is_still_refused_writing_nothing()
    {
        using var f = new UiOverlayFixture(withOverlay: true);
        Dictionary<string, byte[]> before = Files(f.CaseDir);
        CaseAuthoring a = OpenV2(f.ManifestPath);
        Assert.True(a.HasOverlay);
        Assert.Equal(new[] { UiOverlayFixture.FileA, UiOverlayFixture.FileB }, a.OverlayFiles);
        Assert.Null(a.OverlayRefusal);
        CaseRef line = Rec(a, CaseRoles.Lines, 0);
        Assert.True(a.FieldGrid(line).Edit("rPu", "0.025").Ok);

        AuthoringReport check = a.Check();
        Assert.Equal(AuthoringOutcome.CheckPassed, check.Outcome);
        Assert.Empty(check.Errors);
        AuthoringReport save = a.Save();
        Assert.Equal(AuthoringOutcome.Saved, save.Outcome);
        Assert.Equal(new[] { V1Projector.NetworkFileName }, save.FilesWritten);
        Dictionary<string, byte[]> after = Files(f.CaseDir);
        Assert.Equal(Spliced(before[V1Projector.NetworkFileName], V1Projector.NetworkFileName, StringComparer.Ordinal, line.Pointer, "rPu", "0.025"),
            after[V1Projector.NetworkFileName]);
        Assert.Equal(before.Keys.OrderBy(k => k, StringComparer.Ordinal), after.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (string k in before.Keys.Where(k => k != V1Projector.NetworkFileName)) Assert.Equal(before[k], after[k]);
        Assert.False(a.IsDirty);

        string target = Path.Combine(Path.GetTempPath(), "gridsim-authoring-overlay-" + Guid.NewGuid().ToString("N"));
        try
        {
            AuthoringReport copy = a.SaveAs(target);
            Assert.Equal(AuthoringOutcome.Saved, copy.Outcome);
            Assert.Equal(after, Files(target));   // every file, the overlay files among them, byte for byte
        }
        finally
        {
            try { if (Directory.Exists(target)) Directory.Delete(target, recursive: true); } catch (IOException) { }
        }

        // An overlay that fails the checker is still refused by Check, Save and Save As, every problem listed.
        using var bad = new UiOverlayFixture(withOverlay: true, overlayA: OverlayEditingTests.BadOverlayA());
        Dictionary<string, byte[]> badBefore = Files(bad.CaseDir);
        CaseAuthoring b = OpenV2(bad.ManifestPath);
        Assert.Null(b.OverlayRefusal);
        Assert.True(b.FieldGrid(Rec(b, CaseRoles.Lines, 0)).Edit("rPu", "0.025").Ok);
        string[] problems =
        {
            "record 'ov.bb2': role 'bus' points at bus 99999, which is not a bus of the case.",
            "record 'ov.cbl': value \"fuse\" is not one of the allowed values: breaker, disconnector, earthSwitch.",
            "record 'ov.ds': a 'switchingDevice' record requires role 'from' (a record), which is missing.",
        };
        AuthoringReport badSave = b.Save();
        Assert.Equal(AuthoringOutcome.Refused, badSave.Outcome);
        Assert.Equal("NOT SAVED: 3 error(s). The files on disk are unchanged.", badSave.Headline);
        Assert.Equal(problems, badSave.Errors.Select(e => e.Message).OrderBy(m => m, StringComparer.Ordinal));
        Assert.All(badSave.Errors, e => Assert.Equal("site.assets.json", e.File));
        AuthoringReport badCheck = b.Check();
        Assert.Equal(AuthoringOutcome.CheckFailed, badCheck.Outcome);
        Assert.Equal(problems, badCheck.Errors.Select(e => e.Message).OrderBy(m => m, StringComparer.Ordinal));
        string badTarget = Path.Combine(Path.GetTempPath(), "gridsim-authoring-overlay-" + Guid.NewGuid().ToString("N"));
        AuthoringReport badCopy = b.SaveAs(badTarget);
        Assert.Equal(AuthoringOutcome.Refused, badCopy.Outcome);
        Assert.False(Directory.Exists(badTarget));
        Assert.Equal(badBefore, Files(bad.CaseDir));
        Assert.True(b.IsDirty);
    }

    // =================================================================================================================
    // Plan Q9: no Author mode for sources that are not case files
    // =================================================================================================================

    [Theory]
    [InlineData(AssetSourceKind.Matpower, "MATPOWER .m file")]
    [InlineData(AssetSourceKind.Replay, "replay of an exported run folder")]
    [InlineData(AssetSourceKind.Bridge, "streamed by the GDA bridge")]
    [InlineData(AssetSourceKind.Undeclared, "no case is loaded yet")]
    public void Sources_that_are_not_case_files_have_no_Author_mode_and_say_why(AssetSourceKind kind, string why)
    {
        var source = new AssetCaseSource(kind, kind == AssetSourceKind.Undeclared ? null : "somewhere");
        string reason = Assert.IsType<string>(CaseAuthoring.UnavailableReason(source));
        Assert.StartsWith("AUTHOR is not available: ", reason, StringComparison.Ordinal);
        Assert.Contains(why, reason, StringComparison.Ordinal);
        CaseAuthoringException ex = Assert.Throws<CaseAuthoringException>(() => CaseAuthoring.Open(source, Gate()));
        Assert.Equal(reason, ex.Message);
        Assert.Null(CaseAuthoring.UnavailableReason(AssetCaseSource.V1("x")));
        Assert.Null(CaseAuthoring.UnavailableReason(AssetCaseSource.V2("x.case.json")));
    }

    [Fact]
    public void A_case_that_cannot_be_opened_is_refused_with_the_engines_reason()
    {
        string missing = Path.Combine(Path.GetTempPath(), "gridsim-authoring-missing-" + Guid.NewGuid().ToString("N"));
        CaseAuthoringException ex = Assert.Throws<CaseAuthoringException>(() => OpenV1(missing));
        Assert.StartsWith("AUTHOR could not open the case " + missing + ": ", ex.Message, StringComparison.Ordinal);
    }
}
