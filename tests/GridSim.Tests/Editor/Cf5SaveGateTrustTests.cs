using System.Text.Json;
using GridSim.Core.Diagnostics;
using GridSim.Core.Events;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.Replay;
using GridSim.Tests.V2;
using Xunit;
using static GridSim.Tests.Editor.CaseSessionTests;

namespace GridSim.Tests.Editor;

/// <summary>
/// CF5 (N327, Q32 recommendation accepted by main): the editor's v2 save refuses a case the v2 loader refuses, as the v1
/// gate refuses what JsonGridLoader refuses; part 2, the provenance trust rules of technical ch. 8 enforced in code (an
/// output tag is never an input: at the save gate and at the replay entry; trust only moves down).
/// </summary>
public class Cf5SaveGateTrustTests
{
    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Dir = Path.Combine(Path.GetTempPath(), "gridsim-editor-cf5-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
        }

        public string Dir { get; }

        public string Write(string name, string text)
        {
            string path = Path.Combine(Dir, name.Replace('/', Path.DirectorySeparatorChar));
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

    private const string Manifest = "{\n  \"schema\": \"gridsim-case/2\",\n  \"name\": \"Two buses\",\n  \"system\": { \"baseMva\": 100, \"frequencyHz\": 50 },\n  \"network\": [\"a.network.json\"]\n}\n";
    private const string Buses = "    { \"id\": 1, \"baseKv\": 400, \"type\": \"slack\" },\n    { \"id\": 2, \"baseKv\": 400 }\n";
    private const string Injections = "  \"generators\": [\n    { \"name\": \"G\", \"bus\": 1, \"capacityMva\": 500, \"outputMw\": 0 }\n  ],\n"
        + "  \"demands\": [\n    { \"name\": \"D\", \"bus\": 2, \"pMw\": 50, \"qMvar\": 10 }\n  ]";

    private static string Connected(TempDir t)
    {
        t.Write("a.network.json", "{\n  \"schema\": \"gridsim-network/2\",\n  \"buses\": [\n" + Buses + "  ],\n" + Injections + ",\n"
            + "  \"lines\": [\n    { \"name\": \"L1-2\", \"from\": 1, \"to\": 2, \"rPu\": 0.002, \"xPu\": 0.02, \"bPu\": 0.01, \"ratingMva\": 500 }\n  ]\n}\n");
        return t.Write("two.case.json", Manifest);
    }

    private static string Islanded(TempDir t)
    {
        t.Write("a.network.json", "{\n  \"schema\": \"gridsim-network/2\",\n  \"buses\": [\n" + Buses + "  ],\n" + Injections + "\n}\n");
        return t.Write("two.case.json", Manifest);
    }

    // ------------------------------------------------------------------ part 1: the loader's refusal refuses the save

    [Fact]
    public void A_v2_case_the_loader_refuses_is_not_saved_and_the_disk_is_unchanged()
    {
        using var t = new TempDir();
        string manifest = Islanded(t);
        CaseSession s = CaseSession.OpenV2(manifest, Unlimited());
        int a = s.Doc("a.network.json").Id;
        Assert.True(CaseOps.SetField(s, a, "/demands/0", "pMw", "60").Changed);
        string before = File.ReadAllText(Path.Combine(t.Dir, "a.network.json"));
        SaveReport r = SaveGate.Run(s);
        Assert.Equal(SaveOutcome.Refused, r.Outcome);
        Assert.Contains(r.Errors, e => e.Message.StartsWith("the v2 loader refuses the case", StringComparison.Ordinal));
        Assert.Contains(r.Notes, n => n.StartsWith("WARNING: the v2 loader refuses the case", StringComparison.Ordinal));
        Assert.Equal(before, File.ReadAllText(Path.Combine(t.Dir, "a.network.json")));
        Assert.DoesNotContain(r.Errors, e => e.Message.Contains("gridsim-edit-", StringComparison.Ordinal));   // no staged path shown
    }

    [Fact]
    public void A_v2_check_of_a_case_the_loader_refuses_fails()
    {
        using var t = new TempDir();
        CaseSession s = CaseSession.OpenV2(Islanded(t), Unlimited());
        Assert.Equal(SaveOutcome.CheckFailed, SaveGate.Run(s, new SaveOptions(CheckOnly: true)).Outcome);
    }

    [Fact]
    public void A_v2_case_the_loader_reads_still_saves()
    {
        using var t = new TempDir();
        CaseSession s = CaseSession.OpenV2(Connected(t), Unlimited());
        int a = s.Doc("a.network.json").Id;
        Assert.True(CaseOps.SetField(s, a, "/demands/0", "pMw", "60").Changed);
        SaveReport r = SaveGate.Run(s);
        Assert.True(r.Saved, string.Join(Environment.NewLine, r.Lines()));
        Assert.Contains(r.Notes, n => n.StartsWith("the v2 loader reads the case", StringComparison.Ordinal));
    }

    [Fact]
    public void The_suggested_fix_for_the_moved_editor_test_gives_its_two_file_case_a_line_and_saves()
    {
        // The V2EditorTests case (buses in a.network.json, a generator and a demand in b.network.json) with a line: the
        // loader reads it, so the reference / rename test can keep asserting Saved (END-PASS move, CF5 N327).
        using var t = new TempDir();
        t.Write("a.network.json", "{\n  \"schema\": \"gridsim-network/2\",\n  \"buses\": [\n    { \"id\": 1, \"baseKv\": 400, \"type\": \"slack\" },\n    { \"id\": 2, \"baseKv\": 400 }\n  ],\n"
            + "  \"lines\": [\n    { \"name\": \"L1-2\", \"from\": 1, \"to\": 2, \"rPu\": 0.002, \"xPu\": 0.02 }\n  ]\n}\n");
        t.Write("b.network.json", "{\n  \"schema\": \"gridsim-network/2\",\n  \"generators\": [\n    { \"name\": \"G\", \"bus\": 2, \"capacityMva\": 100 }\n  ],\n  \"demands\": [\n    { \"name\": \"D\", \"bus\": 1, \"pMw\": 50 }\n  ]\n}\n");
        string manifest = t.Write("two.case.json", "{\n  \"schema\": \"gridsim-case/2\",\n  \"name\": \"Two files\",\n  \"system\": { \"baseMva\": 100, \"frequencyHz\": 50 },\n  \"network\": [\"a.network.json\", \"b.network.json\"]\n}\n");
        CaseSession s = CaseSession.OpenV2(manifest, Unlimited());
        int a = s.Doc("a.network.json").Id;
        OpResult ren = CaseOps.RenameKey(s, new RecordRef(a, "/buses", 1), "20");
        Assert.True(ren.Changed, ren.Message);
        SaveReport r = SaveGate.Run(s);
        Assert.True(r.Saved, string.Join(Environment.NewLine, r.Lines()));
    }

    // ------------------------------------------------------------------ part 2: the trust rules in code

    [Fact]
    public void An_output_tag_written_in_a_case_file_refuses_the_save()
    {
        using var t = new TempDir();
        string manifestText = File.ReadAllText(Path.Combine(V2TestSupport.FixturesDir, "cases", "good.case.json"))
            .Replace("\"tag\": \"CuratedPlaceholder\", \"note\": \"fixture values [VERIFY]\"", "\"tag\": \"Counterfactual\", \"note\": \"fixture values [VERIFY]\"", StringComparison.Ordinal);
        Assert.Contains("\"Counterfactual\"", manifestText, StringComparison.Ordinal);
        string manifest = t.Write("good.case.json", manifestText);
        t.Copy(Path.Combine(V2TestSupport.FixturesDir, "networks", "good.network.json"), "good.network.json");
        t.Write("extra/more.network.json", "{ \"schema\": \"gridsim-network/2\" }");
        CaseSession s = CaseSession.OpenV2(manifest, Unlimited());
        SaveReport check = SaveGate.Run(s, new SaveOptions(CheckOnly: true));
        Assert.Equal(SaveOutcome.CheckFailed, check.Outcome);
        Assert.Contains(check.Errors, e => e.File == "good.case.json" && e.Message.Contains("is an output tag", StringComparison.Ordinal));
    }

    [Fact]
    public void Tags_are_found_under_prov_and_provenance_maps_and_only_output_tags_are_flagged()
    {
        JsonElement root = JsonDocument.Parse("""
            {
              "a": { "v": 1, "prov": { "tag": "Recorded" } },
              "b": { "provenance": { "_default": { "tag": "CuratedPlaceholder" }, "x": { "tag": "UnvalidatedAtScale" } } },
              "c": [ { "prov": { "tag": "Counterfactual", "note": "n" } } ],
              "d": { "tag": "Counterfactual" }
            }
            """).RootElement;
        var tags = ProvenanceTrust.TagsIn(root);
        Assert.Equal(new[] { "/a/prov/tag", "/b/provenance/_default/tag", "/b/provenance/x/tag", "/c/0/prov/tag" }, tags.Select(x => x.Pointer));
        var outputs = ProvenanceTrust.OutputTagsIn(root);
        Assert.Equal(new[] { ("/b/provenance/x/tag", "UnvalidatedAtScale"), ("/c/0/prov/tag", "Counterfactual") }, outputs);
    }

    [Fact]
    public void The_trust_order_ranks_input_tags_and_refuses_to_rank_output_tags()
    {
        Assert.True(ProvenanceTrust.Rank(ProvenanceTag.Recorded) > ProvenanceTrust.Rank(ProvenanceTag.DerivedFromRecorded));
        Assert.True(ProvenanceTrust.Rank(ProvenanceTag.DerivedFromRecorded) > ProvenanceTrust.Rank(ProvenanceTag.Synthetic));
        Assert.Equal(ProvenanceTrust.Rank(ProvenanceTag.Synthetic), ProvenanceTrust.Rank(ProvenanceTag.Interpolated));
        Assert.True(ProvenanceTrust.Rank(ProvenanceTag.Synthetic) > ProvenanceTrust.Rank(ProvenanceTag.CuratedPlaceholder));
        Assert.Throws<ArgumentException>(() => ProvenanceTrust.Rank(ProvenanceTag.Counterfactual));
        Assert.Throws<ArgumentException>(() => ProvenanceTrust.Rank(ProvenanceTag.UnvalidatedAtScale));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProvenanceTrust.Rank((ProvenanceTag)999));
    }

    [Fact]
    public void Trust_only_moves_down()
    {
        ProvenanceTrust.RequireNotRaised(new[] { ProvenanceTag.Recorded, ProvenanceTag.DerivedFromRecorded }, ProvenanceTag.DerivedFromRecorded, "x");
        ProvenanceTrust.RequireNotRaised(new[] { ProvenanceTag.Recorded }, ProvenanceTag.CuratedPlaceholder, "x");
        ProvenanceTrust.RequireNotRaised(new[] { ProvenanceTag.Synthetic }, ProvenanceTag.Counterfactual, "x");   // labelling an output
        var raised = Assert.Throws<InvalidDataException>(() => ProvenanceTrust.RequireNotRaised(new[] { ProvenanceTag.Recorded, ProvenanceTag.DerivedFromRecorded }, ProvenanceTag.Recorded, "headline"));
        Assert.Contains("trust only moves down", raised.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => ProvenanceTrust.RequireNotRaised(new[] { ProvenanceTag.Counterfactual }, ProvenanceTag.CuratedPlaceholder, "x"));
        Assert.Throws<ArgumentException>(() => ProvenanceTrust.RequireNotRaised(Array.Empty<ProvenanceTag>(), ProvenanceTag.Recorded, "x"));
    }

    [Fact]
    public void A_counterfactual_is_never_a_replay_input()
    {
        GridEvent ev = EventCatalog.Builtin()[0];
        ReplayValidator.RequireReplayInputs(ev);   // every built-in event replays as before
        var losses = ev.Losses.ToList();
        losses[0] = losses[0] with { Provenance = ProvenanceTag.Counterfactual };
        GridEvent fed = ev with { Losses = losses };
        var ex = Assert.Throws<InvalidDataException>(() => ReplayValidator.Validate(fed));
        Assert.Contains("never an input to another replay", ex.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => ReplayValidator.Validate(ev with { Outcome = ev.Outcome with { Provenance = ProvenanceTag.UnvalidatedAtScale } }));
        Assert.Throws<InvalidDataException>(() => ReplayValidator.Validate(ev with { Provenance = ProvenanceTag.Counterfactual }));
    }

    [Fact]
    public void Every_built_in_event_passes_the_replay_input_rule()
    {
        foreach (GridEvent ev in EventCatalog.Builtin()) ReplayValidator.RequireReplayInputs(ev);
    }
}
