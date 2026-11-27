using System.Text;
using GridSim.Core.Journal;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 (2.1.122): exit-code claims in <c>breaking-change validate</c>, and the scope of B28.
/// <para>
/// A re-run that newly exits 1 or 2 used to be CANNOT-REPLAY before any claim was looked at, so a breaking change that
/// claims such a refusal (B9 and B11 claim 0 to 1; B28 claims 0 to 1 or 2) could never explain one. Validate now asks the
/// candidates' <c>(exit code)</c> claims first (<see cref="BreakingChangeMap.ExitClaims"/>). A claimed refusal is CHANGED
/// on the single field <c>(exit code)</c>, which the claim check then explains; the recorded outputs are not compared,
/// since the claim is that the run is now refused.
/// </para>
/// <para>
/// B28 (2.1.120, an RMS run refused when a first-step initialiser meets a disturbance at t = 0) is API only: no CLI verb
/// starts a run with a disturbance in force at t = 0 (the stability fault and events come at 0.1 s or after the first
/// step; dynamics-study refuses an onset or event time of 0). Its scope names no verb, so it explains no journal job, and
/// its claim still judges a refusal correctly if one is handed to it.
/// </para>
/// </summary>
public sealed class Rc5ReviewFix2120BreakingChangeTests : IDisposable
{
    private readonly string _dir = JournalTestSupport.NewDir("bc2120");

    public void Dispose() => JournalTestSupport.Delete(_dir);

    private static readonly BreakingChangeMap Map = BreakingChangeMap.Default;

    private const string Build = "2.1.122";

    private static readonly string[] StabilityArgv = ["stability", "net.raw", "--rms", "--dyn-data", "net.dyr", "--events", "fault:4@0.1,clear:4@0.15"];

    [Fact]
    public void B28_is_api_only_and_explains_no_cli_job()
    {
        BreakingChange b = Map.Entries.Single(x => x.Id == "B28");
        Assert.Empty(b.Verbs);
        Assert.Empty(b.Options);
        Assert.Empty(b.InputFormats);
        Assert.StartsWith("API only:", b.Condition, StringComparison.Ordinal);
        // A stabiliser on electrical power is refused (since 2.1.122 nothing that reads only the terminal voltage is),
        // and one on speed still runs.
        Assert.Contains("a stabiliser on electrical power (IEEEST", b.Condition, StringComparison.Ordinal);
        Assert.DoesNotContain("terminal voltage", b.Condition, StringComparison.Ordinal);
        Assert.Contains("a stabiliser on rotor speed alone, with or without the IEEEST voltage cut-off, still runs", b.Condition, StringComparison.Ordinal);
        Assert.DoesNotContain("B28", Map.Explain("2.1.119", Build, "stability", StabilityArgv, ["/w/net.raw", "/w/net.dyr"]));
        Assert.DoesNotContain("B28", Map.Explain("2.1.119", Build, "dynamics-study", ["dynamics-study", "s.json", "--out", "r.json"], []));
        // The claim itself still covers result to refused, with either refusal code.
        Assert.Equal(new[] { "B28" }, Map.ExitClaims(["B28"], 0, 2));
        Assert.Equal(new[] { "B28" }, Map.ExitClaims(["B28"], 0, 1));
        Assert.Empty(Map.ExitClaims(["B28"], 0, 3));
        Assert.Empty(Map.ExitClaims(["B28"], 3, 2));
    }

    /// <summary>A job whose re-run is refused under a claiming change is CHANGED on (exit code) alone and EXPLAINED by it,
    /// never CANNOT-REPLAY. It is still CHANGED, and ReplayReport.ExitCode gives 3 for any CHANGED outcome that is not
    /// UNBOUNDED or REVIEWED, so validate's exit code is 3.</summary>
    [Fact]
    public void A_b28_transition_job_is_explained_by_b28_not_cannot_replay()
    {
        var o = new ReplayOutcome { Seq = 3, RecordedUtc = "2026-11-21T00:00:00.0000000Z", Verb = "stability", RecordedVersion = "2.1.119", RecordedExit = 0, ReplayExit = 2 };
        IReadOnlyList<string> claimed = Map.ExitClaims(["B28"], 0, 2);
        BreakingChangeMap.MarkClaimedRefusal(o, 0, 2, claimed, "error: stability: IEEEX1 on machine 0 finishes its initialisation on the first step's terminal measurements");
        Assert.Equal(ReplayStatus.Changed, o.Status);
        FieldDifference f = Assert.Single(o.Fields);
        Assert.Equal(("(run)", "(exit code)", "exit", "0->2"), (f.File, f.Field, f.Kind, f.Transition));
        UnboundedChanges.Judge(Map, o, ["B28"], "stability", StabilityArgv, []);
        Assert.Equal(ClaimVerdict.Explained, o.Verdict);
        Assert.Equal(new[] { "B28" }, o.ExplainedBy);
        Assert.Equal(ClaimVerdict.Explained, f.Verdict);
        Assert.False(o.Flagged);
        Assert.Equal(3, ReplayReport.ExitCode([o]));
        Assert.StartsWith("exit 0 -> 2, a refusal claimed by B28; outputs not compared: error: stability:", o.Reason, StringComparison.Ordinal);
        string text = ReplayReport.ConsoleText([o], Build);
        Assert.Contains("seq 3 stability (2.1.119): CHANGED, 1 field(s), worst (run) (exit code) 0 of 53 bits agree, EXPLAINED by B28", text, StringComparison.Ordinal);
        Assert.DoesNotContain(ReplayStatus.CannotReplay + ":", text.Split('\n')[1], StringComparison.Ordinal);
    }

    /// <summary>The same ordering through a real CLI scope: a g74 job recorded before B9 (2.1.098), whose re-run is now
    /// refused with exit 1, has B9 among its candidates, and B9 claims 0 to 1, so it is explained. A pf job refused with
    /// exit 2 has no candidate that claims it (B2 claims 0 to 3 only), so it stays CANNOT-REPLAY.</summary>
    [Fact]
    public void A_claimed_refusal_on_a_cli_scope_is_explained_and_an_unclaimed_one_is_not()
    {
        IReadOnlyList<string> g74 = Map.Explain("2.1.090", Build, "g74", ["g74", "study.json", "--json", "g.json"], ["/w/study.json"]);
        Assert.Contains("B9", g74);
        Assert.Equal(new[] { "B9" }, Map.ExitClaims(g74, 0, 1));
        var o = new ReplayOutcome { Seq = 1, Verb = "g74", RecordedVersion = "2.1.090", RecordedExit = 0, ReplayExit = 1 };
        BreakingChangeMap.MarkClaimedRefusal(o, 0, 1, Map.ExitClaims(g74, 0, 1), "");
        UnboundedChanges.Judge(Map, o, g74, "g74", ["g74", "study.json", "--json", "g.json"], []);
        Assert.Equal(ClaimVerdict.Explained, o.Verdict);
        Assert.Equal(new[] { "B9" }, o.ExplainedBy);
        Assert.Equal("exit 0 -> 1, a refusal claimed by B9; outputs not compared", o.Reason);

        IReadOnlyList<string> pf = Map.Explain("2.1.080", Build, "pf", ["pf", "case14", "--json", "pf.json"], []);
        Assert.Empty(Map.ExitClaims(pf, 0, 2));
        Assert.Empty(Map.ExitClaims(pf, 0, 1));
        // An unbounded change is never asked (B3 and B27 claim a change of model on every field, not a refusal).
        Assert.Empty(Map.ExitClaims(["B3", "B27"], 0, 2));
    }

    /// <summary>End to end: a pf job whose recorded command line now carries a flag this build refuses (exit 2). No
    /// breaking change claims that, so the entry is CANNOT-REPLAY, and the reason no longer says the command line was not
    /// accepted (exit 2 is also how some verbs report a refused run): it says "usage error or refused run" and quotes the
    /// re-run's first error line.</summary>
    [Fact]
    public void The_cli_reports_an_unclaimed_exit_2_without_claiming_the_command_line_was_refused()
    {
        string work = Path.Combine(_dir, "work");
        Directory.CreateDirectory(work);
        string journal = Path.Combine(_dir, "j", "journal.jsonl");
        CliProcess.Result run = JournalTestSupport.Run(work, "--journal", journal, "pf", "case14", "--json", "pf.json");
        Assert.True(run.ExitCode == 0, run.ToString());
        JournalEntry e = JournalJson.Parse(File.ReadAllLines(journal)[0], out _)!;
        e.Version = "2.1.080";
        e.Argv.Add("--no-such-flag-2120");
        File.WriteAllText(journal, JournalJson.Line(e) + "\n", new UTF8Encoding(false));

        CliProcess.Result v = JournalTestSupport.Run(_dir, "breaking-change", "validate", "--journal", journal);
        Assert.True(v.ExitCode == 1, v.ToString());
        Assert.Contains("CANNOT-REPLAY: replay exit 2 (usage error or refused run): ", v.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("no longer accepted", v.StdOut, StringComparison.Ordinal);
    }

    /// <summary>
    /// End to end, the job the review asked for: a real stability run with a DYR holding ESST3A (B27's condition: both
    /// <c>--dyr-extended</c> and <c>--allow-unmodelled</c>), journaled, with its RMS event export as the result.
    /// <para>
    /// Updated in 2.1.127, which makes stability re-runnable (ReplayPolicy.DynamicsVerbs). This test pinned the 2.1.122
    /// state, "CANNOT-REPLAY: not replayable: not yet classified", which that package was asked to end. The job now
    /// re-runs. Nothing in it moved (it was recorded on this build, only its version label set back), so it is IDENTICAL
    /// once the export's run header lines and run-metadata.json host and build fields are left out (volatile-fields.json),
    /// validate exits 0, and <c>--accept B27</c> still records nothing because no job moved. The UNBOUNDED, accept, REVIEWED
    /// and re-flag path on a real stability job is in Rc5DynamicsReplayTests.
    /// </para>
    /// </summary>
    [Fact]
    public void A_journaled_stability_job_with_a_b27_dyr_is_replayed_and_identical()
    {
        string work = Path.Combine(_dir, "work");
        Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(work, "case14.raw"), Rc5Wp19BitIdentityTests.Case14Raw());
        const string g = Rc5Wp19BitIdentityTests.Genrou;
        File.WriteAllText(Path.Combine(work, "case14.dyr"),
              "     1 'GENROU' 1 " + g + " /\n"
            + "     1 'ESST1A' 1  1  1  0.02  0.1 -0.1  1.0  10.0  0.0  0.0  210.0  0.02  6.43 -6.0  6.43 -6.0  0.038  0.0  1.0  4.54  6.0 /\n"
            + "     2 'GENROU' 1 " + g + " /\n"
            + "     2 'EXST1' 1  0.0  0.2 -0.2  1.0  10.0  200.0  0.02  7.0 -6.4  0.05  0.02  1.0 /\n"
            + "     3 'GENROU' 1 " + g + " /\n"
            + "     3 'SCRX' 1  0.1  10.0  200.0  0.05  0.0  5.0  0  10.0 /\n"
            + "     6 'GENROU' 1 " + g + " /\n"
            + "     6 'ESST3A' 1  0.0  0.2 -0.2  7.93  1.0  6.67  20.0  0.0  10.0 -10.0  1.0  6.15  0.0  6.9  0.2  0.081  5.8  20.0  0.4  1.0  0.0 /\n"
            + "     8 'GENCLS' 1  5.0  0.0 /\n");
        string journal = Path.Combine(_dir, "j", "journal.jsonl");
        CliProcess.Result run = JournalTestSupport.Run(work, "--journal", journal, "stability", "case14.raw", "--rms", "--dyn-data", "case14.dyr",
            "--dyr-extended", "--allow-unmodelled", "--events", "fault:4@0.1,clear:4@0.15", "--events-out", "ev");
        Assert.True(run.ExitCode == 0, run.ToString());
        JournalEntry e = JournalJson.Parse(File.ReadAllLines(journal)[0], out _)!;
        Assert.Equal(3, e.Outputs.Count);
        Assert.Contains(e.Tokens, t => t.Kind == "input" && t.Raw == "case14.dyr");
        e.Version = "2.1.080";   // B3 and B27 in the job's window
        File.WriteAllText(journal, JournalJson.Line(e) + "\n", new UTF8Encoding(false));

        CliProcess.Result v = JournalTestSupport.Run(_dir, "breaking-change", "validate", "--journal", journal);
        Assert.True(v.ExitCode == 0, v.ToString());
        Assert.Contains("seq 1 stability (2.1.080): IDENTICAL", v.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("not yet classified", v.StdOut, StringComparison.Ordinal);
        CliProcess.Result accept = JournalTestSupport.Run(_dir, "breaking-change", "validate", "--journal", journal, "--accept", "B27");
        Assert.Contains("--accept B27: no job moved under B27 in this run; nothing recorded.", accept.StdErr, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_dir, "j", "journal.accept.json")));
    }
}
