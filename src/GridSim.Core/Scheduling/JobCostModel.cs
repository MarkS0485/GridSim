using System.Collections.Concurrent;

namespace GridSim.Core.Scheduling;

/// <summary>
/// Tracks each job's measured cost (core-ms) as an exponentially-weighted moving average, so admission
/// control learns real runtimes instead of trusting the static seed. Keyed by <see cref="AnalysisJob.Id"/>,
/// so a rotating N-1 shard carries its own history across cycles. Thread-safe.
/// </summary>
public sealed class JobCostModel
{
    private readonly ConcurrentDictionary<string, double> _ewma = new(StringComparer.Ordinal);
    private readonly double _alpha;

    /// <param name="alpha">Smoothing factor in (0,1]; higher reacts faster to the latest observation.</param>
    public JobCostModel(double alpha = 0.3)
    {
        // GR-865: NaN-safe (a NaN alpha passed the old test and made every estimate NaN, so admission was unbounded).
        if (!(alpha > 0 && alpha <= 1)) throw new ArgumentOutOfRangeException(nameof(alpha), alpha, "alpha must be in (0,1].");
        _alpha = alpha;
    }

    /// <summary>Best current estimate of the job's cost, core-ms - the EWMA if seen, else its seed.</summary>
    public double Estimate(AnalysisJob job) =>
        _ewma.TryGetValue(job.Id, out double v) ? v : job.SeedCoreMs;

    /// <summary>Fold a measured runtime into the job's EWMA.</summary>
    public void Observe(string id, double coreMs)
    {
        // GR-865: a measured cost is a finite number of core-ms, 0 or above; a NaN would poison the job's estimate for good.
        if (!(coreMs >= 0) || double.IsInfinity(coreMs))
            throw new ArgumentOutOfRangeException(nameof(coreMs), coreMs, "A measured job cost must be a finite number of core-ms, 0 or above.");
        _ewma.AddOrUpdate(id, coreMs, (_, old) => _alpha * coreMs + (1 - _alpha) * old);
    }
}
