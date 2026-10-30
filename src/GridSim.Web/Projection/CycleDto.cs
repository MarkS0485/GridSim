using GridSim.Core.Analysis;
using GridSim.Core.Scheduling;

namespace GridSim.Web.Projection;

// ---- CYCLE (prioritised parallel analysis cycle telemetry) ----

/// <summary>One job's outcome in the latest cycle, for the CYCLE tab's job table.</summary>
public sealed record CycleJobDto(string Name, string Priority, string Outcome, double CoreMs, bool FromStaleFrame);

/// <summary>
/// Telemetry for the most recently completed analysis cycle: how the single-core base solve and the
/// parallel fan-out spent the compute budget, whether the High tier's guarantee held, how far N-1
/// coverage has rotated, and the worst contingency currently seen.
/// </summary>
public sealed record CycleDto(
    long CycleIndex,
    double BaseSolveMs,
    double WallMs,
    int HighTotal,
    int HighCompleted,
    int LowAdmitted,
    int LowCompleted,
    int LowDeferred,
    int LowCancelled,
    double BudgetTotalCoreMs,
    double BudgetSpentCoreMs,
    double UtilizationPercent,
    int BacklogDepth,
    bool HighTierMet,
    int WorkerBudgetNote,       // workers implied by budget/window, for the header readout
    int NMinus1ShardsCovered,
    int NMinus1ShardsTotal,
    string? WorstContingency,
    int WorstContingencyViolations,
    bool WorstContingencyIslanded,
    IReadOnlyList<CycleJobDto> Jobs);

/// <summary>Projects a <see cref="CycleResult"/> into the browser DTO, folding in the N-1 shard coverage (shards completed at
/// least once since the session's cycle service started: cumulative, not a rolling window; D-DOC-144).</summary>
public static class CycleProjection
{
    public static CycleDto Project(CycleResult r, int shardsCovered, int shardsTotal, int workers)
    {
        var jobs = new List<CycleJobDto>(r.Results.Count);
        string? worst = null;
        int worstViol = 0;
        bool worstIslanded = false;

        foreach (JobResult jr in r.Results)
        {
            jobs.Add(new CycleJobDto(jr.Name, jr.Priority.ToString(), jr.Outcome.ToString(), jr.CoreMs, jr.FromStaleFrame));

            // The N-1 jobs carry a list of ContingencyResult; surface the single worst outage seen this cycle.
            if (jr.Payload is IReadOnlyList<ContingencyResult> contingencies)
                foreach (ContingencyResult c in contingencies)
                {
                    if (!c.Converged)
                    {
                        // An islanding outage outranks any thermal/voltage violation.
                        if (!worstIslanded) { worst = c.Outage; worstIslanded = true; worstViol = c.Violations.Count; }
                    }
                    else if (!worstIslanded && c.Violations.Count > worstViol)
                    {
                        worst = c.Outage; worstViol = c.Violations.Count;
                    }
                }
        }

        // Job table: High first, then Low; within a tier, most-expensive first (what dominates the window).
        jobs.Sort((a, b) =>
        {
            int p = string.CompareOrdinal(a.Priority, b.Priority); // "High" < "Low" ordinally
            return p != 0 ? p : b.CoreMs.CompareTo(a.CoreMs);
        });

        return new CycleDto(
            CycleIndex: r.CycleIndex,
            BaseSolveMs: r.BaseSolveMs,
            WallMs: r.WallMs,
            HighTotal: r.HighTotal,
            HighCompleted: r.HighCompleted,
            LowAdmitted: r.LowAdmitted,
            LowCompleted: r.LowCompleted,
            LowDeferred: r.LowDeferred,
            LowCancelled: r.LowCancelled,
            BudgetTotalCoreMs: r.BudgetTotalCoreMs,
            BudgetSpentCoreMs: r.BudgetSpentCoreMs,
            UtilizationPercent: r.Utilization * 100.0,
            BacklogDepth: r.BacklogDepth,
            HighTierMet: r.HighTierMet,
            WorkerBudgetNote: workers,
            NMinus1ShardsCovered: shardsCovered,
            NMinus1ShardsTotal: shardsTotal,
            WorstContingency: worst,
            WorstContingencyViolations: worstViol,
            WorstContingencyIslanded: worstIslanded,
            Jobs: jobs);
    }
}
