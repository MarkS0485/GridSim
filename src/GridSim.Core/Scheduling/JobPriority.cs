namespace GridSim.Core.Scheduling;

/// <summary>
/// Which tier a post-solve analysis job runs in within a cycle.
/// </summary>
public enum JobPriority
{
    /// <summary>
    /// Must complete every cycle. The security-critical analyses (key contingencies, harmonics) are High:
    /// they are guaranteed the compute budget and are
    /// never cancelled at the deadline, so an operator always has a current view even when a hard
    /// case or a compute stutter makes the cycle run long. (The base power solve is not a job: it runs before the
    /// fan-out, R096.)
    /// </summary>
    High,

    /// <summary>
    /// Deferrable / best-effort. Runs only in the budget left after the High tier, and unfinished
    /// Low work rotates into later cycles rather than blocking the window. Anything essential-but-not-
    /// -this-instant (rolling N-1 coverage, RoCoF screening, conformance) is Low.
    /// </summary>
    Low,
}
