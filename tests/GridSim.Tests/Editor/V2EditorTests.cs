using System.Text;
using System.Text.Json;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Lossless;
using GridSim.Core.IO.Json.Schema;
using GridSim.SchemaEditor.Ui;
using GridSim.Tests.V2;
using Xunit;
using static GridSim.Tests.Editor.CaseSessionTests;
using static GridSim.Tests.Editor.EditorTestSupport;

namespace GridSim.Tests.Editor;

/// <summary>
/// Slice E7 of the case editor (SCHEMA-EDITOR-PLAN.md section 10): v2 documents. Opening a manifest or a single
/// network file, the merged sections, path containment, cross-file reference integrity, the v2 save gate (schemas plus
/// semantic checks; no production v2 loader yet), and the plan's comparison: the same edit made in a v1 case and in its
/// v2 projection gives the same DTOs once the v2 case is projected back.
/// </summary>
public class V2EditorTests
{
    private static string Fixture(string relative) => Path.Combine(V2TestSupport.FixturesDir, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>A fresh temporary folder, deleted when disposed.</summary>
    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Dir = Path.Combine(Path.GetTempPath(), "gridsim-editor-v2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
        }

        public string Dir { get; }

        public string Write(string relative, string text)
        {
            string path = Path.Combine(Dir, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            return path;
        }

        public string Copy(string source, string relative)
        {
            string path = Path.Combine(Dir, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(source, path);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void A_single_network_file_opens_with_its_sections_and_no_problems()
    {
        using var t = new TempDir();
        string net = t.Copy(Fixture("networks/good.network.json"), "good.network.json");
        CaseSession s = CaseSession.OpenV2(net, Unlimited());
        Assert.Equal(CaseFormat.V2Network, s.Format);
        Assert.Single(s.Documents);
        Assert.Equal(2, s.BusCount);
        Assert.Equal(CaseRoles.V2Network, s.Collections.Select(c => c.Role));
        Assert.Equal(0, s.Preflight.SchemaErrorCount);
        Assert.Null(s.Preflight.LoaderError);
        Assert.Empty(s.Preflight.ModelWarnings);
        Assert.Equal(SaveOutcome.CheckPassed, SaveGate.Run(s, new SaveOptions(CheckOnly: true)).Outcome);
    }

    [Fact]
    public void A_manifest_opens_with_every_listed_network_file_and_a_missing_one_is_reported()
    {
        using var t = new TempDir();
        string manifest = t.Copy(Fixture("cases/good.case.json"), "good.case.json");
        t.Copy(Fixture("networks/good.network.json"), "good.network.json");

        CaseSession missing = CaseSession.OpenV2(manifest, Unlimited());
        Assert.Equal(CaseFormat.V2Case, missing.Format);
        Assert.Equal(3, missing.Documents.Count);
        Assert.False(missing.Documents[2].Exists);
        Assert.Contains("does not exist", missing.Preflight.LoaderError, StringComparison.Ordinal);
        Assert.Equal(SaveOutcome.CheckFailed, SaveGate.Run(missing, new SaveOptions(CheckOnly: true)).Outcome);

        t.Write("extra/more.network.json", "{ \"schema\": \"gridsim-network/2\" }");
        CaseSession s = CaseSession.OpenV2(manifest, Unlimited());
        SaveReport check = SaveGate.Run(s, new SaveOptions(CheckOnly: true));
        Assert.True(check.Outcome == SaveOutcome.CheckPassed, string.Join(Environment.NewLine, check.Lines()));
        // Moved 2026-10 (DOC-17 D-DOC-162, Ruling R1, Mark delegated 2026-10-04): the note was stale; V2CaseLoader has
        // existed since N065, and what is true is that the save gate does not run it. Before: the note contained "no
        // production v2 loader yet (roadmap P2)". After: it contains "the v2 loader is not run on save". The check is
        // as strict as before (the note must be there, saying what a v2 save does and does not prove).
        Assert.Contains(check.Notes, n => n.Contains("the v2 loader is not run on save", StringComparison.Ordinal));
        Assert.DoesNotContain(check.Notes, n => n.Contains("no production v2 loader", StringComparison.Ordinal));
        Assert.Equal(2, s.BusCount);
    }

    [Theory]
    [InlineData("[\"../outside.network.json\"]", "outside the case folder")]
    [InlineData("[\"a.network.json\", \"a.network.json\"]", "listed twice")]
    public void Network_files_outside_the_folder_or_listed_twice_are_refused(string network, string expected)
    {
        using var t = new TempDir();
        string manifest = t.Write("sub/c.case.json", "{ \"schema\": \"gridsim-case/2\", \"name\": \"c\", \"system\": { \"baseMva\": 100, \"frequencyHz\": 50 }, \"network\": " + network + " }");
        var ex = Assert.Throws<InvalidDataException>(() => CaseSession.OpenV2(manifest, Unlimited()));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_v2_field_edit_saves_exactly_one_token_and_stays_schema_valid()
    {
        using var t = new TempDir();
        string net = t.Copy(Fixture("networks/good.network.json"), "good.network.json");
        string before = File.ReadAllText(net);
        CaseSession s = CaseSession.OpenV2(net, Unlimited());
        OpResult r = CaseOps.SetField(s, 0, "/generators/1", "capacityMva", "150");
        Assert.True(r.Changed, r.Message);
        Assert.Equal(new RecordRef(0, "/generators", 1), r.Select);
        SaveReport saved = SaveGate.Run(s);
        Assert.True(saved.Saved, string.Join(Environment.NewLine, saved.Lines()));
        string after = File.ReadAllText(net);
        int at = before.IndexOf("\"capacityMva\": 100", StringComparison.Ordinal);
        Assert.Equal(before[..at] + "\"capacityMva\": 150" + before[(at + "\"capacityMva\": 100".Length)..], after);
        Assert.Empty(BundledSchemas.V2Validate.Validate(JsonSourceText.Load(net, StringComparer.Ordinal).ToElement(), CaseSession.V2NetworkSchema));
    }

    /// <summary>A two-file case: buses in a.network.json, a generator and a demand on them in b.network.json.</summary>
    private static string TwoFileCase(TempDir t)
    {
        t.Write("a.network.json", "{\n  \"schema\": \"gridsim-network/2\",\n  \"buses\": [\n    { \"id\": 1, \"baseKv\": 400, \"type\": \"slack\" },\n    { \"id\": 2, \"baseKv\": 400 }\n  ]\n}\n");
        // Moved 2026-10 (END-PASS CF5 row 1; see References_resolve_across_network_files_and_a_bus_rename_cascades_into_the_other_file):
        // b.network.json gains one line L1-2 so bus 2 is no longer an island with no slack. In b, not a, so a.network.json
        // still lacks /lines for Adding_to_a_section_a_file_lacks_inserts_the_array_member.
        t.Write("b.network.json", "{\n  \"schema\": \"gridsim-network/2\",\n  \"generators\": [\n    { \"name\": \"G\", \"bus\": 2, \"capacityMva\": 100 }\n  ],\n  \"demands\": [\n    { \"name\": \"D\", \"bus\": 1, \"pMw\": 50 }\n  ],\n  \"lines\": [\n    { \"name\": \"L1-2\", \"from\": 1, \"to\": 2, \"rPu\": 0.002, \"xPu\": 0.02 }\n  ]\n}\n");
        return t.Write("two.case.json", "{\n  \"schema\": \"gridsim-case/2\",\n  \"name\": \"Two files\",\n  \"system\": { \"baseMva\": 100, \"frequencyHz\": 50 },\n  \"network\": [\"a.network.json\", \"b.network.json\"]\n}\n");
    }

    [Fact]
    public void References_resolve_across_network_files_and_a_bus_rename_cascades_into_the_other_file()
    {
        using var t = new TempDir();
        CaseSession s = CaseSession.OpenV2(TwoFileCase(t), Unlimited());
        Assert.Equal(2, s.BusCount);
        int a = s.Doc("a.network.json").Id, b = s.Doc("b.network.json").Id;

        Assert.False(CaseOps.SetField(s, b, "/generators/0", "bus", "3").Ok);              // no bus 3 in any file
        Assert.True(CaseOps.SetField(s, b, "/generators/0", "bus", "1").Changed);          // bus 1 lives in a.network.json
        s.Undo();
        OpResult del = CaseOps.DeleteRecord(s, new RecordRef(a, "/buses", 1));
        Assert.False(del.Ok);
        Assert.Contains("b.network.json", del.Message, StringComparison.Ordinal);

        OpResult ren = CaseOps.RenameKey(s, new RecordRef(a, "/buses", 1), "20");
        Assert.True(ren.Changed, ren.Message);
        Assert.Equal(20, RecordRead.Int(s.Doc("b.network.json").Text.Resolve("/generators/0")!, "bus", StringComparer.Ordinal));
        Assert.Equal(new[] { "a.network.json", "b.network.json" }, s.DirtyDocuments.Select(d => d.FileName));
        Assert.Equal(1, s.UndoDepth);
        // Moved 2026-10 (END-PASS CF5 row 1, end pass; N327 / Q32 and D-DOC-082, the save gate runs the v2 loader, which
        // refuses an island with no slack, Ruling R1 and Mark's go on the end-pass moves 2026-10-11): TwoFileCase's bus 2
        // had no branch to the slack, so the loader now refuses the save (observed: Assert.True failed, Saved false,
        // 1.95.273-275 runs). This test is about references across files; saving a valid two-file case still exists, so
        // the fixture is made valid instead of re-pinning the refusal. Before -> after: b.network.json gains
        // `"lines": [{ "name": "L1-2", "from": 1, "to": 2, "rPu": 0.002, "xPu": 0.02 }]` (the rename cascades into its
        // "to" as well); this assertion unchanged (Saved true: derived from Cf5SaveGateTrustTests' suggested-fix test,
        // green in those runs with the line in a.network.json; with it in b.network.json not observed).
        Assert.True(SaveGate.Run(s).Saved);
    }

    [Fact]
    public void Duplicate_bus_ids_across_files_refuse_the_save()
    {
        using var t = new TempDir();
        CaseSession s = CaseSession.OpenV2(TwoFileCase(t), Unlimited());
        int b = s.Doc("b.network.json").Id;
        // Add a bus 2 to b.network.json by raw text (the editor itself refuses the collision; this proves the gate).
        Assert.False(CaseOps.AddRecord(s, b, "/buses", 0, new[] { new KeyValuePair<string, string>("id", "2"), new KeyValuePair<string, string>("baseKv", "132") }).Ok);
        CaseDocument bd = s.Doc("b.network.json");
        using (CaseTransaction tx = s.Begin("test: duplicate bus id"))
        {
            tx.Patch(bd, JsonTextOps.InsertMember(bd.Text, bd.Text.Root, 1, "buses", JsonFresh.FromElement(Element("[{\"id\": 2, \"baseKv\": 132}]"))));
            tx.Commit();
        }
        SaveReport r = SaveGate.Run(s);
        Assert.Equal(SaveOutcome.Refused, r.Outcome);
        Assert.Contains(r.Errors, e => e.Message.Contains("unique across every network file", StringComparison.Ordinal));
    }

    [Fact]
    public void Adding_to_a_section_a_file_lacks_inserts_the_array_member()
    {
        using var t = new TempDir();
        CaseSession s = CaseSession.OpenV2(TwoFileCase(t), Unlimited());
        int a = s.Doc("a.network.json").Id;
        // Test changed 2026-10-19: END-PASS CF5 row 1 moved a line named L1-2 into TwoFileCase's b.network.json, and
        // branch names are unique across every network file (CaseOps.KeyCollision), so adding a second "L1-2" is
        // correctly refused. The new line takes a fresh name; the test is about inserting the missing /lines member.
        OpResult r = CaseOps.AddRecord(s, a, "/lines", 0, new[]
        {
            new KeyValuePair<string, string>("name", "L1-2b"),
            new KeyValuePair<string, string>("from", "1"),
            new KeyValuePair<string, string>("to", "2"),
            new KeyValuePair<string, string>("rPu", "0.01"),
            new KeyValuePair<string, string>("xPu", "0.1"),
        });
        Assert.True(r.Changed, r.Message);
        Assert.Empty(BundledSchemas.V2Validate.Validate(s.Doc("a.network.json").Text.ToElement(), CaseSession.V2NetworkSchema));
        Assert.Equal(new RecordRef(a, "/lines", 0), r.Select);
    }

    [Fact]
    public void The_same_edit_in_v1_and_in_its_v2_projection_gives_the_same_case_back()
    {
        // The plan's E7 proof, on gb-synthetic (whose projection the v2 tests already validate).
        using var v1 = new TempCopy(CaseDir("gb-synthetic"));
        using var t = new TempDir();
        V2Projection p = V1Projector.Project(V1Projector.ReadV1(v1.Dir));
        string manifest = Path.Combine(t.Dir, "gb-synthetic.case.json");
        File.WriteAllBytes(manifest, p.CaseJson);
        File.WriteAllBytes(Path.Combine(t.Dir, V1Projector.NetworkFileName), p.NetworkJson);

        CaseSession s1 = CaseSession.OpenV1(v1.Dir, Unlimited());
        Assert.True(CaseOps.SetField(s1, s1.Doc("generation.json").Id, "/17", "capacityMva", "1234.5").Changed);
        Assert.True(SaveGate.Run(s1).Saved);

        CaseSession s2 = CaseSession.OpenV2(manifest, Unlimited());
        Assert.Equal(s1.BusCount, s2.BusCount);
        OpResult r = CaseOps.SetField(s2, s2.Doc(V1Projector.NetworkFileName).Id, "/generators/17", "capacityMva", "1234.5");
        Assert.True(r.Changed, r.Message);
        SaveReport saved = SaveGate.Run(s2);
        Assert.True(saved.Saved, string.Join(Environment.NewLine, saved.Lines()));

        JsonElement caseRoot = JsonSourceText.Load(manifest, StringComparer.Ordinal).ToElement();
        JsonElement networkRoot = JsonSourceText.Load(Path.Combine(t.Dir, V1Projector.NetworkFileName), StringComparer.Ordinal).ToElement();
        List<string> diffs = V1Projector.Differences(V1Projector.ReadV1(v1.Dir), V1Projector.Back(caseRoot, networkRoot));
        Assert.True(diffs.Count == 0, string.Join(Environment.NewLine, diffs));
    }

    [Fact]
    public void The_editor_browses_a_v2_network_and_saves_an_edit()
    {
        using var t = new TempDir();
        string net = t.Copy(Fixture("networks/good.network.json"), "good.network.json");
        var d = new EditorDriver(CaseSession.OpenV2(net, Unlimited()), "good.network.json");
        Assert.Contains("good.network.json (v2)  2 buses", d.Screen().RowText(0), StringComparison.Ordinal);
        d.Select("Generation");
        Assert.Contains("good.network.json", d.CurrentRow, StringComparison.Ordinal);
        d.Keys(ConsoleKey.Enter);
        Assert.Equal(2, d.Editor.Top.Visible.Count);
        d.Select("Battery").Keys(ConsoleKey.Enter);
        Assert.Contains("generators #1 'Battery'", d.Screen().RowText(1), StringComparison.Ordinal);
        d.Select("capacityMva").Keys(ConsoleKey.Enter, "150", ConsoleKey.Enter, ConsoleKey.F10);
        Assert.StartsWith("Saved 1 file(s): good.network.json.", d.Editor.Message, StringComparison.Ordinal);
        Assert.Contains("\"capacityMva\": 150", File.ReadAllText(net), StringComparison.Ordinal);
    }

    [Fact]
    public void Check_on_a_v2_file_from_the_command_line()
    {
        using var t = new TempDir();
        string net = t.Copy(Fixture("networks/good.network.json"), "good.network.json");
        string bad = t.Copy(Fixture("networks/bad-bus-type.network.json"), "bad.network.json");
        using var o = new StringWriter();
        using var e = new StringWriter();
        Assert.Equal(0, GridSim.SchemaEditor.EntryPoint.Run(new[] { net, "--check" }, _ => Unlimited(), o, e));
        Assert.Equal(3, GridSim.SchemaEditor.EntryPoint.Run(new[] { bad, "--check" }, _ => Unlimited(), o, e));
    }
}
