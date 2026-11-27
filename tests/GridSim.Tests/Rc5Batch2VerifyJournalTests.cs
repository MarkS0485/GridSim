using System.Text;
using System.Text.Json;
using GridSim.Core.Journal;
using Xunit;
using static GridSim.Tests.Rc5DynamicsReplaySupport;

namespace GridSim.Tests;

/// <summary>
/// RC5 batch-2 verify (2.1.130), the 2.1.127 review's items on <c>breaking-change validate</c>:
/// <list type="bullet">
/// <item>a plug-in assembly is never taken from the snapshot store, even when a tampered journal marks it as kept and a
/// planted store object holds its exact bytes;</item>
/// <item>a DYR input that cannot be read where the re-run will read it makes the job CANNOT-REPLAY
/// (<c>input unreadable</c>), shown with a stub reader;</item>
/// <item>a study's curves folder is recorded by its file list, so a curve file added to a folder that was empty at
/// recording is seen, and an absolute curves folder is re-listed where it is;</item>
/// <item>a dynamics-study job with model assignments replays IDENTICAL: its result names the assignments file by its file
/// name, and the re-run's file name maps to the same placeholder.</item>
/// </list>
/// </summary>
public sealed class Rc5Batch2VerifyJournalTests : IDisposable
{
    private readonly string _dir = JournalTestSupport.NewDir("b2verify");

    public void Dispose() => JournalTestSupport.Delete(_dir);

    private string P(params string[] parts) => Path.Combine(new[] { _dir }.Concat(parts).ToArray());

    [Fact]
    public void A_tampered_journal_never_supplies_a_plugin_assembly_from_the_store()
    {
        string work = P("plugin");
        WriteStudy(work);
        WritePlugin(work);
        string[] job = ["dynamics-study", "study.json", "--model-plugins", "plugins.json", "--model-assignments", "assign.json", "--out", "result.json"];
        string journal = P("jp", "journal.jsonl");
        RecordWithSnapshot(work, journal, job);
        string dll = Path.Combine(work, "sample.dll");
        byte[] dllBytes = File.ReadAllBytes(dll);
        string sha = Sha(dll);

        // The tampering: the assembly's case file is marked as kept, and the store holds an object with its exact bytes.
        JournalEntry e = JournalJson.Parse(File.ReadAllLines(journal)[0], out string? error) ?? throw new InvalidDataException(error);
        JournalCaseFile assembly = Assert.Single(e.CaseFiles, c => c.Label.StartsWith(DynamicsInputs.PluginAssemblyLabel, StringComparison.Ordinal));
        Assert.True(assembly.Snapshot != true);
        assembly.Snapshot = true;
        File.WriteAllText(journal, JournalJson.Line(e) + "\n", new UTF8Encoding(false));
        string objects = JournalSnapshot.ObjectsDir(journal);
        Assert.Null(JournalSnapshot.Put(objects, sha, dllBytes));
        Assert.True(File.Exists(JournalSnapshot.ObjectPath(objects, sha)));
        JournalTestSupport.FromAnotherBuild(journal);

        // The assembly is gone from where the configuration names it. The store object would pass the pin check (same
        // bytes). SEC-27 (2.1.156): a job that names --model-plugins is never re-run at all (a re-run never loads code), so
        // it is CANNOT-REPLAY by the replay policy before any input or store object is looked at, and no re-run starts. (The
        // pin used to read "plug-in assembly missing", the label rule's refusal, which ran because plug-in jobs were
        // re-runnable; ruling 4 of the 2026-11-25 bug hunt makes them never re-runnable, so that reason is no longer reached.)
        File.Delete(dll);
        string report = P("tampered.json");
        CliProcess.Result r = Validate(_dir, journal, "--json", report);
        Assert.True(r.ExitCode == 1, r.ToString());
        Assert.EndsWith(": CANNOT-REPLAY: not replayable: --model-plugins loads plug-in code, and a re-run never loads code", Line(r, 1), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, Table(report, "entries")[0].GetProperty("replay_exit").ValueKind);
    }

    [Fact]
    public void A_dyr_input_that_cannot_be_read_is_cannot_replay_input_unreadable()
    {
        var into = new List<UnboundedChanges.DyrInput>();
        static byte[]? Denied(string path, out string? problem) { problem = "access denied"; return null; }
        Assert.Equal("input unreadable: /rec/net.dyr (access denied)", UnboundedChanges.AddDyr("/rec/net.dyr", "/replay/net.dyr", true, Denied, into));
        Assert.Equal("input unreadable: /rec/s/case.dyr (access denied)", UnboundedChanges.AddDyr("/rec/s/case.dyr", "/replay/s/case.dyr", false, Denied, into));
        Assert.Empty(into);

        // Readable: a DYR is added under its recorded file name with the bytes read; a JSON --dyn-data file is not a DYR.
        byte[] dyr = Encoding.ASCII.GetBytes("1 'GENCLS' 1 5.0 0.0 /\n");
        byte[]? Dyr(string path, out string? problem) { problem = null; return path == "/replay/net.dyr" ? dyr : Encoding.ASCII.GetBytes(" \n{ \"x\": 1 }"); }
        Assert.Null(UnboundedChanges.AddDyr("/rec/net.dyr", "/replay/net.dyr", true, Dyr, into));
        Assert.Null(UnboundedChanges.AddDyr("/rec/net.json", "/replay/net.json", true, Dyr, into));
        UnboundedChanges.DyrInput only = Assert.Single(into);
        Assert.Equal(("net.dyr", (string?)null), (only.Name, only.Problem));
        Assert.Equal(dyr, only.Bytes);
    }

    [Fact]
    public void A_curves_folder_is_recorded_by_its_file_list_and_an_absolute_one_is_relisted()
    {
        string work = P("curves");
        WriteStudy(work);
        string folder = P("abs-curves");
        Directory.CreateDirectory(folder);   // empty when the run is recorded
        string study = Path.Combine(work, "study.json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(study))!;
        node["adaptive"] = new System.Text.Json.Nodes.JsonObject { ["curves"] = folder };
        File.WriteAllText(study, node.ToJsonString());
        string journal = P("jc", "journal.jsonl");
        // The job's own outcome does not matter here (the curves check comes before any re-run): only its journal line.
        JournalTestSupport.Run(work, "--journal", journal, "dynamics-study", "study.json", "--out", "result.json");
        Assert.Equal(1, JournalTestSupport.Lines(journal));
        JournalEntry e = JournalJson.Parse(File.ReadAllLines(journal)[0], out string? error) ?? throw new InvalidDataException(error);
        JournalCaseFile rec = Assert.Single(e.CaseFiles, c => c.Label == DynamicsInputs.CurvesFolderLabel);
        Assert.Equal((folder, folder), (rec.Path, rec.Abs));
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Array.Empty<byte>())), rec.Sha256);
        Assert.True(rec.Snapshot != true);
        JournalTestSupport.FromAnotherBuild(journal);

        // A curve file added later to the (absolute) folder: the folder no longer holds exactly the recorded files.
        File.WriteAllText(Path.Combine(folder, "late.json"), "{}");
        CliProcess.Result r = Validate(_dir, journal);
        Assert.True(r.ExitCode == 1, r.ToString());
        Assert.EndsWith($": CANNOT-REPLAY: input changed: {folder} (the curves folder no longer holds exactly the recorded files)", Line(r, 1), StringComparison.Ordinal);
    }

    [Fact]
    public void A_dynamics_study_with_model_assignments_replays_identical()
    {
        // SEC-27 (2.1.156): a plug-in job is never re-run, so the assignments here bind the shipped TGOV1 (a declarative model,
        // the same parameters the plug-in sample took) and the job names no --model-plugins. What is pinned is unchanged: the
        // result names the assignments file by its file name, and the re-run maps it to the same placeholder.
        string work = P("assign");
        WriteStudy(work);
        File.WriteAllText(Path.Combine(work, "assign.json"), JsonSerializer.Serialize(new
        {
            schema = "gridsim-model-assignments/1",
            userModels = new[] { new { machine = new { bus = 1, id = "1" }, model = "TGOV1",
                parameters = new Dictionary<string, double> { ["R"] = 0.05, ["T1"] = 0.49, ["T2"] = 2.1, ["T3"] = 7.0, ["Dt"] = 0.0, ["Vmin"] = 0.0, ["Vmax"] = 2.5 } } },
        }));
        string journal = P("ja", "journal.jsonl");
        Record(work, journal, ["dynamics-study", "study.json", "--model-assignments", "assign.json", "--out", "result.json"], 0, 3);
        using (JsonDocument d = JsonDocument.Parse(File.ReadAllText(Path.Combine(work, "result.json"))))
            Assert.Equal("assign.json", d.RootElement.GetProperty("provenance").GetProperty("modelAssignments").GetProperty("file").GetString());
        JournalTestSupport.FromAnotherBuild(journal);
        CliProcess.Result r = Validate(_dir, journal);
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.EndsWith(": IDENTICAL", Line(r, 1), StringComparison.Ordinal);
    }
}
