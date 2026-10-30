using System.Threading;

namespace GridSim.Core.Scheduling;

/// <summary>
/// The compute budget for one cycle, in <b>core-milliseconds</b>. With <c>W</c> workers and a wall-clock
/// window of <c>windowMs</c>, a cycle owns <c>W × windowMs</c> of compute (28 cores × 1000 ms =
/// 28,000 core-ms). The window is the wall-clock deadline; this aggregate is the currency admission
/// control spends - it is what lets the budget scale with core count while the window stays fixed.
/// Thread-safe: workers record their spend concurrently as they finish jobs.
/// </summary>
public sealed class CycleBudget
{
    private double _spent;

    public CycleBudget(int workers, double windowMs)
    {
        TotalCoreMs = Math.Max(0, workers) * Math.Max(0, windowMs);
    }

    /// <summary>Total compute this cycle may spend, core-ms (<c>workers × windowMs</c>).</summary>
    public double TotalCoreMs { get; }

    /// <summary>Compute spent so far, core-ms (sum of measured job runtimes).</summary>
    public double Spent => Volatile.Read(ref _spent);

    /// <summary>Compute left, core-ms (never negative).</summary>
    public double Remaining => Math.Max(0.0, TotalCoreMs - Spent);

    /// <summary>Fraction of the budget spent (0..1+; can exceed 1 if High work overran the estimate).</summary>
    public double Utilization => TotalCoreMs > 0 ? Spent / TotalCoreMs : 0.0;

    /// <summary>Record <paramref name="coreMs"/> of spend (lock-free CAS add on the double accumulator).</summary>
    public void Record(double coreMs)
    {
        // GR-860: a spend is a finite number of core-ms, 0 or above. A NaN made the accumulator NaN, and then the CAS loop
        // below spun for ever (NaN != NaN, so the comparison never matched); the comparison is also on the bits now.
        if (!(coreMs >= 0) || double.IsInfinity(coreMs))
            throw new ArgumentOutOfRangeException(nameof(coreMs), coreMs, "A recorded spend must be a finite number of core-ms, 0 or above.");
        double init, updated;
        do
        {
            init = Volatile.Read(ref _spent);
            updated = init + coreMs;
        }
        while (BitConverter.DoubleToInt64Bits(Interlocked.CompareExchange(ref _spent, updated, init)) != BitConverter.DoubleToInt64Bits(init));
    }

    /// <summary>Would admitting an estimated <paramref name="estCoreMs"/> more still fit the budget?</summary>
    public bool CanAdmit(double estCoreMs) => Spent + estCoreMs <= TotalCoreMs;
}
