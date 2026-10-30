namespace GridSim.Core.Scheduling;

/// <summary>
/// Per-cycle telemetry: what the base solve cost, how the fan-out spent the budget, and how the Low
/// tier split into done / deferred / cancelled. This is what the CLI prints and the web "CYCLE" panel binds.
/// </summary>
public sealed record CycleResult(
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
    int BacklogDepth,
    bool FromStaleFrame,
    IReadOnlyList<JobResult> Results)
{
    /// <summary>Fraction of the compute budget spent this cycle (0..1+).</summary>
    public double Utilization => BudgetTotalCoreMs > 0 ? BudgetSpentCoreMs / BudgetTotalCoreMs : 0.0;

    /// <summary>True when every High job completed - the invariant the design guarantees.</summary>
    public bool HighTierMet => HighCompleted == HighTotal;
}
