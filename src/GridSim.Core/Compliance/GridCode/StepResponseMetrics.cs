using System;
using System.Collections.Generic;

namespace GridSim.Core.Compliance.GridCode;

/// <summary>
/// The measurements of a step response read from a sampled trace y(t_k), the step applied at <c>StepSeconds</c> from the
/// level <c>Initial</c> towards <c>Final</c>. Every time is measured from the step.
/// <list type="bullet">
/// <item>The change is <c>Final - Initial</c>; a fraction f of it is reached at the level <c>Initial + f (Final - Initial)</c>.</item>
/// <item>A crossing time is the first sample interval [t_k, t_k+1] at or after the step where y reaches the level, placed by
/// linear interpolation between the two samples (<c>t_k + (L - y_k) (t_k+1 - t_k) / (y_k+1 - y_k)</c>); a sample exactly on
/// the level is its own crossing. Null when the trace never reaches it.</item>
/// <item>Overshoot = (peak - Final) / |Final - Initial|, the peak taken in the direction of the change over the samples at or
/// after the step (0 or below when the trace never passes Final).</item>
/// <item>Settling = the last sample time outside the band |y - Centre| &lt;= Band, or 0 when no sample after the step lies
/// outside it. When the last sample of the trace is itself outside the band, the trace has not settled: null.</item>
/// </list>
/// Pure arithmetic on the samples given; nothing is fitted.
/// </summary>
public static class StepResponseMetrics
{
    /// <summary>The first time (s from the step) the trace reaches <c>Initial + fraction (Final - Initial)</c>, by linear
    /// interpolation between samples; null when it never does.</summary>
    public static double? CrossingSeconds(IReadOnlyList<double> times, IReadOnlyList<double> y, double stepSeconds, double initial, double final, double fraction)
    {
        Check(times, y);
        double change = final - initial;
        if (change == 0.0) throw new ArgumentException("the step has no change (Final = Initial).", nameof(final));
        double level = initial + fraction * change;
        double sign = change > 0.0 ? 1.0 : -1.0;
        int first = FirstAtOrAfter(times, stepSeconds);
        if (first < 0) return null;
        if (sign * (y[first] - level) >= 0.0) return times[first] - stepSeconds;
        for (int k = first; k + 1 < times.Count; k++)
        {
            double a = sign * (y[k] - level), b = sign * (y[k + 1] - level);
            if (a < 0.0 && b >= 0.0)
            {
                double t = times[k] + (level - y[k]) * (times[k + 1] - times[k]) / (y[k + 1] - y[k]);
                return t - stepSeconds;
            }
        }
        return null;
    }

    /// <summary>Overshoot (peak - Final) / |Final - Initial| over the samples at or after the step, the peak taken in the
    /// direction of the change. Null when no sample lies at or after the step.</summary>
    public static double? Overshoot(IReadOnlyList<double> times, IReadOnlyList<double> y, double stepSeconds, double initial, double final)
    {
        Check(times, y);
        double change = final - initial;
        if (change == 0.0) throw new ArgumentException("the step has no change (Final = Initial).", nameof(final));
        double sign = change > 0.0 ? 1.0 : -1.0;
        int first = FirstAtOrAfter(times, stepSeconds);
        if (first < 0) return null;
        double peak = double.NegativeInfinity;
        for (int k = first; k < times.Count; k++) peak = Math.Max(peak, sign * y[k]);
        return (peak - sign * final) / Math.Abs(change);
    }

    /// <summary>The settling time (s from the step): the time of the first sample from which every later sample lies within
    /// |y - centre| &lt;= band, that is the sample after the last one outside it (2.1.126: the last outside sample itself
    /// under-read the settling time by up to one step); 0 when no sample at or after the step lies outside; null when the
    /// last sample is outside the band (the trace has not settled within the run).</summary>
    public static double? SettlingSeconds(IReadOnlyList<double> times, IReadOnlyList<double> y, double stepSeconds, double centre, double band)
    {
        Check(times, y);
        if (!(band >= 0.0) || !double.IsFinite(band)) throw new ArgumentOutOfRangeException(nameof(band), band, "the band must be finite and 0 or more.");
        int first = FirstAtOrAfter(times, stepSeconds);
        if (first < 0) return null;
        if (!(Math.Abs(y[^1] - centre) <= band)) return null;
        int lastOut = -1;
        for (int k = first; k < times.Count; k++)
            if (!(Math.Abs(y[k] - centre) <= band)) lastOut = k;
        // The last sample is inside the band, so a sample after the last outside one exists.
        return lastOut < 0 ? 0.0 : times[lastOut + 1] - stepSeconds;
    }

    /// <summary>The peak-to-peak spread (max - min) of the samples with t - step in [from, to].</summary>
    public static double? PeakToPeak(IReadOnlyList<double> times, IReadOnlyList<double> y, double stepSeconds, double fromSeconds, double toSeconds)
    {
        Check(times, y);
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        for (int k = 0; k < times.Count; k++)
        {
            double t = times[k] - stepSeconds;
            if (t < fromSeconds || t > toSeconds) continue;
            lo = Math.Min(lo, y[k]); hi = Math.Max(hi, y[k]);
        }
        return double.IsPositiveInfinity(lo) ? null : hi - lo;
    }

    /// <summary>The peak-to-peak magnitude of the oscillations in the samples with t - step in [from, to]: max - min over the
    /// samples from the first turning point in that window (a sample where the trace changes direction, strictly) to the end
    /// of the window; 0 when the trace does not turn there (a monotone approach is not an oscillation). Null when the window
    /// holds fewer than three samples.</summary>
    public static double? OscillationPeakToPeak(IReadOnlyList<double> times, IReadOnlyList<double> y, double stepSeconds, double fromSeconds, double toSeconds)
    {
        Check(times, y);
        var idx = new List<int>();
        for (int k = 0; k < times.Count; k++)
        {
            double t = times[k] - stepSeconds;
            if (t >= fromSeconds && t <= toSeconds) idx.Add(k);
        }
        if (idx.Count < 3) return null;
        int turn = -1;
        for (int i = 1; i + 1 < idx.Count; i++)
        {
            double a = y[idx[i]] - y[idx[i - 1]], b = y[idx[i + 1]] - y[idx[i]];
            if ((a > 0.0 && b < 0.0) || (a < 0.0 && b > 0.0)) { turn = i; break; }
        }
        if (turn < 0) return 0.0;
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        for (int i = turn; i < idx.Count; i++) { lo = Math.Min(lo, y[idx[i]]); hi = Math.Max(hi, y[idx[i]]); }
        return hi - lo;
    }

    private static int FirstAtOrAfter(IReadOnlyList<double> times, double stepSeconds)
    {
        for (int k = 0; k < times.Count; k++) if (times[k] >= stepSeconds) return k;
        return -1;
    }

    private static void Check(IReadOnlyList<double> times, IReadOnlyList<double> y)
    {
        ArgumentNullException.ThrowIfNull(times);
        ArgumentNullException.ThrowIfNull(y);
        if (times.Count != y.Count) throw new ArgumentException("times and values must have the same length.");
        if (times.Count == 0) throw new ArgumentException("the trace is empty.");
        for (int k = 1; k < times.Count; k++)
            if (!(times[k] > times[k - 1])) throw new ArgumentException("the sample times must increase strictly.");
    }
}
