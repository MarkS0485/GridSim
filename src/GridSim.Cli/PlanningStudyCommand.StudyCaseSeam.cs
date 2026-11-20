using System.Text.Json;
using GridSim.Core.Analysis.PlanningStudies;
using GridSim.Core.Model;

namespace GridSim.Cli;

// RC4 R4-01: the seam through which the study-case verb reuses the RC3 planning-study machinery (strict study and
// operation parsing, master identity, per-job result folders with verified reuse, atomic new-file writing) without
// duplicating it. Nothing here changes what planning-study does.
internal static partial class PlanningStudyCommand
{
    /// <summary>One job's outcome as the study-case verb needs it.</summary>
    internal sealed record SeamOutcome(PlanningJob Job, string State, string? Status, string? Outcome, string? Reason,
        string? Result, string? ResultSha256, bool Reused, string? ReuseRefusal, double? ElapsedMs);

    /// <summary>Run <paramref name="jobs"/> (each already numbered) into <c>jobs/&lt;folder&gt;</c> under
    /// <paramref name="outDir"/> with the planning-study reuse rules, calling <paramref name="runner"/> for each job.</summary>
    internal static SeamOutcome[] SeamRunJobs(IReadOnlyList<PlanningJob> jobs, string outDir, PlanningMaster master,
        string build, int parallel, int? maxJobs, Func<PlanningJob, PlanningJobResult> runner, CancellationToken cancel)
    {
        int limit = maxJobs ?? int.MaxValue;
        var outcomes = new SeamOutcome[jobs.Count];
        Parallel.For(0, jobs.Count, new ParallelOptions { MaxDegreeOfParallelism = parallel }, i =>
        {
            PlanningJob job = jobs[i];
            if (i >= limit || cancel.IsCancellationRequested)
            {
                outcomes[i] = new SeamOutcome(job, "NOT ATTEMPTED", null, null, null, null, null, false, null, null);
                return;
            }
            (JobRecord r, JobTiming t) = Execute(job, outDir, master, build, runner);
            outcomes[i] = new SeamOutcome(job, r.State, r.Status, r.Outcome, r.Reason, r.Result, r.ResultSha256,
                t.Reused, t.ReuseRefusal, t.ElapsedMs);
        });
        return outcomes;
    }

    internal static PlanningStudyDefinition SeamStudy(JsonElement study, string at) => Study(study, at);
    internal static PlanningOperation SeamOperation(JsonElement operation, string at) => Operation(operation, at);
    internal static string SeamCaseSha256(GridModel model) => CaseSha256(model);
    internal static List<PlanningInputFile> SeamInputs(string path, string root) => Inputs(path, root);
    internal static string SeamBuild() => BuildFingerprint();
    internal static void SeamWriteNew(string path, byte[] bytes) => WriteNew(path, bytes);
    internal static void SeamWriteNew(string path, string text) => WriteNew(path, text);
    internal static string SeamJson(object value) => JsonSerializer.Serialize(value, OutputJson) + "\n";
}
