using System.Globalization;
using System.Text;

namespace GridSim.Core.Journal;

/// <summary>The outcome statuses of one journal entry in <c>breaking-change validate</c>.</summary>
public static class ReplayStatus
{
    public const string Identical = "IDENTICAL";
    public const string Changed = "CHANGED";
    public const string CannotReplay = "CANNOT-REPLAY";
    /// <summary>Recorded by this very build (same version and commit): by determinism it cannot change, so it is not
    /// re-run.</summary>
    public const string SkippedSameBuild = "SKIPPED-SAME-BUILD";
}

/// <summary>The outcome of one journal entry: a pure function of the entry, its input bytes, this build and the
/// re-run's outputs. It holds nothing about scheduling (no start order, wall time or scratch path).</summary>
public sealed class ReplayOutcome
{
    public long Seq { get; init; }
    public string RecordedVersion { get; init; } = "";
    /// <summary>When the entry was recorded (with <see cref="Seq"/>, the job's identity for <c>--accept</c>).</summary>
    public string RecordedUtc { get; init; } = "";
    public string Verb { get; init; } = "";
    /// <summary>The run's input files, by file name, joined with ';'.</summary>
    public string Inputs { get; init; } = "";
    public string Status { get; set; } = ReplayStatus.CannotReplay;
    public string? Reason { get; set; }
    public int? RecordedExit { get; init; }
    public int? ReplayExit { get; set; }
    public List<FieldDifference> Fields { get; } = new();
    public IReadOnlyList<string> ExplainedBy { get; set; } = Array.Empty<string>();

    /// <summary>For a CHANGED entry, the claim check over its fields (<see cref="BreakingChangeMap.Judge"/>): EXPLAINED
    /// only when every field is within the claims of breaking changes that can explain it; else the worst of
    /// UNEXPLAINED, EXCEEDS-CLAIM and AMBIGUOUS. Empty for the other statuses.</summary>
    public string Verdict { get; set; } = "";

    /// <summary>CHANGED with no breaking change to explain it: a likely bug, to be reported.</summary>
    public bool Unexplained => Status == ReplayStatus.Changed && Verdict is "" or ClaimVerdict.Unexplained;

    /// <summary>CHANGED and neither EXPLAINED nor REVIEWED (unexplained, beyond its claim, ambiguous, or unbounded and not
    /// yet reviewed): flagged, never counted as explained.</summary>
    public bool Flagged => Status == ReplayStatus.Changed && Verdict is not (ClaimVerdict.Explained or ClaimVerdict.Reviewed);

    /// <summary>The worst field: fewest agreeing bits, then most ulps, then file and field name.</summary>
    public FieldDifference? Worst => Fields
        .OrderBy(f => f.MinAgreeingBits)
        .ThenByDescending(f => f.MaxUlps ?? ulong.MaxValue)
        .ThenBy(f => f.File, StringComparer.Ordinal)
        .ThenBy(f => f.Field, StringComparer.Ordinal)
        .FirstOrDefault();
}

/// <summary>Exit code and console text of a validate run, from its outcomes in sequence order.</summary>
public static class ReplayReport
{
    /// <summary>The exit code that means "review needed": the only results that moved did so under a breaking change
    /// with no bound (UNBOUNDED), and nothing else is wrong.</summary>
    public const int ReviewNeededExit = 4;

    /// <summary>1 when anything could not be re-run (an input error, the batch rule); else 3 when anything changed (a
    /// verdict) other than UNBOUNDED or REVIEWED; else 4 (<see cref="ReviewNeededExit"/>) when anything is UNBOUNDED; else
    /// 0. A REVIEWED entry counts as nothing wrong.</summary>
    public static int ExitCode(IReadOnlyList<ReplayOutcome> outcomes)
    {
        if (outcomes.Any(o => o.Status == ReplayStatus.CannotReplay)) return 1;
        if (outcomes.Any(o => o.Status == ReplayStatus.Changed && o.Verdict is not (ClaimVerdict.Unbounded or ClaimVerdict.Reviewed))) return 3;
        if (outcomes.Any(o => o.Status == ReplayStatus.Changed && o.Verdict == ClaimVerdict.Unbounded)) return ReviewNeededExit;
        return 0;
    }

    /// <summary>A number in round-trip form, or "" for none.</summary>
    public static string Number(double? d) => d is double x
        ? (double.IsNaN(x) ? "NaN" : double.IsPositiveInfinity(x) ? "Infinity" : double.IsNegativeInfinity(x) ? "-Infinity" : x.ToString("R", CultureInfo.InvariantCulture))
        : "";

    /// <summary>The console text: the counts line (unexplained first), then one line per entry in sequence order.</summary>
    public static string ConsoleText(IReadOnlyList<ReplayOutcome> outcomes, string buildVersion)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        int changed = outcomes.Count(o => o.Status == ReplayStatus.Changed);
        int unexplained = outcomes.Count(o => o.Unexplained);
        int exceeds = outcomes.Count(o => o.Status == ReplayStatus.Changed && o.Verdict == ClaimVerdict.ExceedsClaim);
        int ambiguous = outcomes.Count(o => o.Status == ReplayStatus.Changed && o.Verdict == ClaimVerdict.Ambiguous);
        int unbounded = outcomes.Count(o => o.Status == ReplayStatus.Changed && o.Verdict == ClaimVerdict.Unbounded);
        int reviewed = outcomes.Count(o => o.Status == ReplayStatus.Changed && o.Verdict == ClaimVerdict.Reviewed);
        sb.Append(string.Create(inv,
            $"breaking-change validate on {buildVersion}: unexplained={unexplained} exceeds-claim={exceeds} ambiguous={ambiguous} identical={outcomes.Count(o => o.Status == ReplayStatus.Identical)} changed={changed} cannot-replay={outcomes.Count(o => o.Status == ReplayStatus.CannotReplay)} skipped-same-build={outcomes.Count(o => o.Status == ReplayStatus.SkippedSameBuild)} unbounded={unbounded} reviewed={reviewed}\n"));
        foreach (ReplayOutcome o in outcomes)
        {
            sb.Append(string.Create(inv, $"  seq {o.Seq} {o.Verb} ({o.RecordedVersion}): {o.Status}"));
            if (o.Status == ReplayStatus.Changed)
            {
                FieldDifference? w = o.Worst;
                if (w is not null)
                    sb.Append(string.Create(inv, $", {o.Fields.Count} field(s), worst {w.File} {w.Field} {w.MinAgreeingBits} of 53 bits agree"));
                sb.Append(o.Verdict switch
                {
                    ClaimVerdict.Explained => ", EXPLAINED by " + string.Join("; ", o.ExplainedBy),
                    ClaimVerdict.ExceedsClaim => ", EXCEEDS-CLAIM of " + string.Join("; ", o.ExplainedBy) + ": " + FirstFlagged(o),
                    ClaimVerdict.Ambiguous => ", AMBIGUOUS between " + string.Join("; ", o.ExplainedBy) + ": " + FirstFlagged(o),
                    ClaimVerdict.Unbounded => ", UNBOUNDED (" + string.Join("; ", UnboundedIds(o)) + "), review needed: " + MovedFields(o),
                    ClaimVerdict.Reviewed => ", REVIEWED (" + string.Join("; ", UnboundedIds(o)) + "): " + MovedFields(o),
                    _ => ", UNEXPLAINED (likely a bug: please report it)",
                });
            }
            if (o.Reason is not null) sb.Append(": ").Append(o.Reason);
            sb.Append('\n');
        }
        if (unexplained > 0)
            sb.Append(string.Create(inv, $"  {unexplained} changed result(s) are explained by no breaking change: this is likely a bug; please report it with the entry.\n"));
        if (exceeds + ambiguous > 0)
            sb.Append(string.Create(inv, $"  {exceeds + ambiguous} changed result(s) moved beyond, or cannot be matched to, what their breaking changes claim: a second change may be hiding there; please report it with the entry.\n"));
        if (unbounded > 0)
        {
            string ids = string.Join(", ", outcomes.Where(o => o.Status == ReplayStatus.Changed && o.Verdict == ClaimVerdict.Unbounded)
                .SelectMany(UnboundedIds).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal));
            sb.Append(string.Create(inv, $"  {unbounded} changed result(s) moved under {ids}, a breaking change with no bound: the move is expected, but no number can check it. Look at the fields listed, and when you are content run validate again with --accept {ids}. Those jobs then show as REVIEWED until one moves further or a new job moves.\n"));
        }
        return sb.ToString();
    }

    // The unbounded changes an UNBOUNDED or REVIEWED entry's fields rest on.
    private static IEnumerable<string> UnboundedIds(ReplayOutcome o) => o.Fields
        .Where(f => f.Verdict is ClaimVerdict.Unbounded or ClaimVerdict.Reviewed)
        .SelectMany(f => f.ExplainedBy.Split(';', StringSplitOptions.RemoveEmptyEntries)).Distinct(StringComparer.Ordinal);

    // Every moved field of an entry with its size, in field order.
    private static string MovedFields(ReplayOutcome o) => string.Create(CultureInfo.InvariantCulture, $"{o.Fields.Count} field(s) moved: ")
        + string.Join("; ", o.Fields.Select(f => f.File + " " + f.Field + " " + (f.Observed.Length > 0 ? f.Observed : f.Kind)));

    private static string FirstFlagged(ReplayOutcome o) =>
        o.Fields.FirstOrDefault(f => f.Verdict == o.Verdict) is { } f ? $"{f.File} {f.Field} observed {f.Observed}, claimed {f.Claimed}" : "";
}

/// <summary>One job of the validate scheduler: its position in the caller's sequence-ordered selection (carried for the
/// caller; the scheduler's result array follows the job list) and what the journal recorded about its size.</summary>
public sealed record ReplayJob(int Position, long Seq, long ElapsedMs, long PeakWorkingSetBytes);

/// <summary>
/// Runs re-run jobs side by side. Start order and memory admission are scheduling only: each result is stored at its
/// job's index in the job list (which the caller builds in sequence order), and the report is built from that array
/// after every job has finished, so the report is the same whatever the parallelism and whatever order jobs finish in.
/// </summary>
public static class ReplayScheduler
{
    /// <summary>The smallest memory estimate of a job (256 MiB).</summary>
    public const long MinEstimateBytes = 256L * 1024 * 1024;

    /// <summary>A job's memory estimate: max(256 MiB, 1.25 x its recorded peak working set).</summary>
    public static long Estimate(long peakWorkingSetBytes) => Math.Max(MinEstimateBytes, peakWorkingSetBytes + peakWorkingSetBytes / 4);

    /// <summary>The automatic parallelism: min(processors, floor(0.8 x installed / 256 MiB), selected), at least 1.</summary>
    public static int AutoParallel(int processors, long installedBytes, int selected)
    {
        long byMemory = (long)(0.8 * installedBytes) / MinEstimateBytes;
        long n = Math.Min(Math.Min(processors, byMemory), selected);
        return (int)Math.Max(1, n);
    }

    /// <summary>The start order: longest recorded run first, ties by sequence number.</summary>
    public static IReadOnlyList<int> StartOrder(IReadOnlyList<ReplayJob> jobs) =>
        Enumerable.Range(0, jobs.Count).OrderByDescending(i => jobs[i].ElapsedMs).ThenBy(i => jobs[i].Seq).ToArray();

    /// <summary>
    /// Run every job with at most <paramref name="parallel"/> at once and the sum of running estimates within
    /// <paramref name="budgetBytes"/> (a job larger than the budget runs alone). Each job whose result
    /// <paramref name="retrySerially"/> flags (a crash, a kill for time or memory) is then run once more alone, and only
    /// that result is kept. <paramref name="done"/> is told of each completion (in completion order, for progress only).
    /// The result array is aligned with <paramref name="jobs"/>.
    /// </summary>
    public static TResult[] Run<TResult>(IReadOnlyList<ReplayJob> jobs, int parallel, long budgetBytes,
        Func<ReplayJob, TResult> run, Func<TResult, bool> retrySerially, Action<ReplayJob, int, int>? done = null)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(run);
        if (parallel < 1) throw new ArgumentOutOfRangeException(nameof(parallel));
        var results = new TResult[jobs.Count];
        IReadOnlyList<int> order = StartOrder(jobs);
        var gate = new object();
        int next = 0, running = 0, finished = 0;
        long used = 0;
        Exception? failure = null;
        var tasks = new List<Task>();
        lock (gate)
        {
            while (next < order.Count || running > 0)
            {
                if (failure is null && next < order.Count)
                {
                    int index = order[next];
                    ReplayJob job = jobs[index];
                    long est = Estimate(job.PeakWorkingSetBytes);
                    if (running < parallel && (running == 0 || used + est <= budgetBytes))
                    {
                        next++;
                        running++;
                        used += est;
                        tasks.Add(Task.Factory.StartNew(() =>
                        {
                            TResult? r = default;
                            Exception? ex = null;
                            try { r = run(job); }
                            catch (Exception e) { ex = e; }
                            lock (gate)
                            {
                                try
                                {
                                    if (ex is not null) failure ??= ex;
                                    else results[index] = r!;
                                    finished++;
                                    done?.Invoke(job, finished, jobs.Count);
                                }
                                catch (Exception e) { failure ??= e; }
                                finally
                                {
                                    running--;
                                    used -= est;
                                    Monitor.PulseAll(gate);
                                }
                            }
                        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));
                        continue;
                    }
                }
                else if (failure is not null && running == 0) break;
                Monitor.Wait(gate);
            }
        }
        Task.WaitAll(tasks.ToArray());
        if (failure is not null) throw new InvalidOperationException("a re-run job failed: " + failure.Message, failure);
        for (int i = 0; i < jobs.Count; i++)
            if (retrySerially(results[i])) results[i] = run(jobs[i]);
        return results;
    }
}
