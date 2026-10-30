namespace GridSim.Core.Scheduling;

/// <summary>
/// Configuration for the cycle engine. This is the config home the codebase did not have for any
/// thread-count / window / budget knob; the CLI verb and the web host both bind onto it.
/// </summary>
public sealed record CycleOptions
{
    /// <summary>Wall-clock window per cycle, milliseconds. The hard deadline; the compute budget is
    /// <see cref="Workers"/> × this. Configurable - a control room may run 1000 ms; a soak test may shorten it.</summary>
    public double WindowMs { get; init; } = 1000.0;

    /// <summary>
    /// Fan-out width for the post-solve job pool. Defaults to <c>ProcessorCount - 1</c>, reserving a core
    /// for the single-threaded base solve and orchestration. The compute budget is <c>Workers × WindowMs</c>
    /// core-ms, so this is exactly the "budget scales with core count" knob.
    /// </summary>
    public int Workers { get; init; } = Math.Max(1, Environment.ProcessorCount - 1);

    /// <summary>
    /// RESERVED, NOT IMPLEMENTED: the intended opt-in to drain cycle N-1's Low backlog against the previous frame
    /// (marked <see cref="CycleContext.FromStaleFrame"/>) while cycle N's base solve runs. Nothing implements the
    /// pipelined drain yet, so <see cref="CycleRunner"/> refuses <c>true</c> (R096, October 2026 review: the option
    /// and the CLI's <c>cycle --pipeline</c> flag were accepted and silently ignored).
    /// </summary>
    public bool PipelineBacklog { get; init; } = false;

    /// <summary>EWMA smoothing for the <see cref="JobCostModel"/>.</summary>
    public double CostEwmaAlpha { get; init; } = 0.3;

    /// <summary>Number of Low-tier N-1 shards to split the non-critical branch set into (rotation granularity).</summary>
    public int NMinus1Shards { get; init; } = 8;

    /// <summary>
    /// A branch counts toward the High-tier "hot" N-1 set (screened every cycle) once its loading reaches
    /// this percent of rating. The rest of the branches are screened by the rotating Low shards.
    /// </summary>
    public double HotBranchLoadingPercent { get; init; } = 80.0;

    /// <summary>Above this bus count the harmonics job is skipped (mirrors the on-demand study's gate).</summary>
    public int HarmonicsBusLimit { get; init; } = 1500;
}
