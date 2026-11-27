using System.Text.Json;
using GridSim.Core.Journal;
using Xunit;
using static GridSim.Tests.Rc5DynamicsReplaySupport;

namespace GridSim.Tests;

/// <summary>
/// RC5 (2.1.127), T7 extended to the dynamics verbs: the volatile-field census. The fields left out of the comparison are
/// derived from the result schemas (volatile-fields.json: the run-metadata sidecar's host, build and time fields, the CSV
/// run header lines, the dynamics-study provenance.engine); this census checks that the list is complete for the verbs
/// 2.1.127 makes re-runnable. Each job is run once with a journal in one folder, then re-run by <c>validate</c> itself, on
/// the same build, in another folder (the scratch folder), later. Every entry must be IDENTICAL. A field the list misses
/// (a time, a host name, a build stamp) shows as CHANGED and fails the census, naming the verb and the field.
/// </summary>
public sealed class BreakingChangeDynamicsCensusTests : IDisposable
{
    private readonly string _dir = JournalTestSupport.NewDir("dyncensus");

    public void Dispose() => JournalTestSupport.Delete(_dir);

    /// <summary>The corpus: every re-run dynamics verb, and each output kind it has (verb, job, allowed exit codes).</summary>
    private static List<(string Verb, string[] Job, int[] Exits)> Corpus() =>
    [
        ("stability", ["stability", "case9", "--export", "classical.json"], [0]),
        ("stability", ["stability", "case9", "--rms", "--events", "fault:7@0.1,clear:7@0.183", "--events-out", "ev-plain"], [0]),
        ("stability", SequenceFaultJob("ev-seq"), [0]),
        ("stability", Case14StabilityJob("ev-dyr"), [0]),
        ("dynamics-study", ["dynamics-study", Path.Combine("study", "study.json"), "--out", "study-result.json"], [0, 3]),
        // SEC-27 (2.1.156): a plug-in job (--model-plugins) is never re-run (a re-run never loads code), so it left the census;
        // Sec27 in BugHunt1125SecurityTests and the plug-in replay tests pin that it is CANNOT-REPLAY.
        ("long-term", ["long-term", "case9", "lt.json", "--out", "lt.csv"], [0, 3]),
        ("trip", ["trip", "case9", "--export", "trip.csv"], [0, 3]),
        ("modal", ["modal", "case9", "--export", "modal.json"], [0, 1]),
    ];

    [Fact]
    public void T7_dynamics_census_every_re_run_dynamics_job_is_identical()
    {
        string work = Path.Combine(_dir, "runs");
        WriteCase14(work);
        WriteStudy(Path.Combine(work, "study"));
        File.WriteAllText(Path.Combine(work, "lt.json"), LongTermCase9);
        string journal = Path.Combine(_dir, "journal", "journal.jsonl");
        List<(string Verb, string[] Job, int[] Exits)> corpus = Corpus();
        foreach ((string _, string[] job, int[] exits) in corpus) Record(work, journal, job, exits);
        foreach (string line in File.ReadAllLines(journal))
            Assert.Equal("safe", JournalJson.Parse(line, out _)!.ReplayClass);
        JournalTestSupport.FromAnotherBuild(journal);

        string report = Path.Combine(_dir, "validate.json");
        CliProcess.Result v = JournalTestSupport.Run(_dir, "breaking-change", "validate", "--journal", journal, "--parallel", "4", "--json", report);
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(report));
        var failures = new List<string>();
        foreach (JsonElement e in doc.RootElement.GetProperty("tables").GetProperty("entries").EnumerateArray())
            if (e.GetProperty("status").GetString() != ReplayStatus.Identical)
                failures.Add($"seq {e.GetProperty("seq").GetInt64()} {e.GetProperty("verb").GetString()}: {e.GetProperty("status").GetString()} "
                    + $"{e.GetProperty("reason").GetString()} worst field {e.GetProperty("worst_field").GetString()}");
        foreach (JsonElement f in doc.RootElement.GetProperty("tables").GetProperty("field_differences").EnumerateArray())
            failures.Add($"  seq {f.GetProperty("seq").GetInt64()} {f.GetProperty("file").GetString()} {f.GetProperty("field").GetString()} ({f.GetProperty("kind").GetString()})");
        Assert.True(failures.Count == 0, "the volatile-field list is incomplete or a dynamics verb is not deterministic:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
        Assert.True(v.ExitCode == 0, v.ToString());
        Assert.Equal(corpus.Count, doc.RootElement.GetProperty("summary").GetProperty("identical").GetInt64());
    }

    /// <summary>The census covers every verb <c>validate</c> re-runs as a dynamics verb, and dynamics-study.</summary>
    [Fact]
    public void The_dynamics_census_covers_every_re_run_dynamics_verb()
    {
        var covered = Corpus().Select(c => c.Verb).ToHashSet(StringComparer.Ordinal);
        foreach (string v in ReplayPolicy.DynamicsVerbs.Append("dynamics-study"))
            Assert.True(covered.Contains(v), $"{v} is re-run by validate but not in the dynamics census");
    }
}
