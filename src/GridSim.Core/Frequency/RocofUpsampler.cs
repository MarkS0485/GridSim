namespace GridSim.Core.Frequency;

/// <summary>
/// Refines a coarse frequency trace to a fine timeline. Between two consecutive
/// samples it computes the rate of change of frequency,
/// <c>RoCoF = (f2 - f1) / deltat</c>, and fills the gap by linear interpolation  - 
/// turning NESO's 1-second data into 50 sub-samples at 20 ms each
/// (<c>f(t) = f1 + RoCoFt</c>). That 20 ms cadence is the resolution the
/// frequency-dynamics model and a fast frequency-response controller want to see.
/// </summary>
public static class RocofUpsampler
{
    /// <summary>
    /// Upsample to <paramref name="subSamplesPerInterval"/> points per source
    /// interval (50 -> 20 ms steps for 1-second input), carrying the interval's RoCoF.
    /// </summary>
    public static IEnumerable<FrequencyPoint> Upsample(IEnumerable<FrequencySample> samples, int subSamplesPerInterval = 50)
    {
        int sub = Math.Max(1, subSamplesPerInterval);
        using IEnumerator<FrequencySample> e = samples.GetEnumerator();
        if (!e.MoveNext()) yield break;

        FrequencySample prev = e.Current;
        while (e.MoveNext())
        {
            FrequencySample cur = e.Current;
            double dt = (cur.Time - prev.Time).TotalSeconds;
            RequireNotBackwards(prev.Time, cur.Time);
            if (dt <= 0) { prev = cur; continue; }   // a repeated time stamp: no interval to fill

            double rocof = (cur.Hz - prev.Hz) / dt;
            double step = dt / sub;
            for (int k = 0; k < sub; k++)
            {
                double tOff = k * step;
                yield return new FrequencyPoint(prev.Time.AddSeconds(tOff), prev.Hz + rocof * tOff, rocof);
            }
            prev = cur;
        }

        yield return new FrequencyPoint(prev.Time, prev.Hz, 0.0); // closing point
    }

    /// <summary>Per-second RoCoF (Hz/s) between consecutive samples, paired with the later sample's time.</summary>
    public static IEnumerable<(DateTime Time, double RocofHzPerS)> Rocof(IEnumerable<FrequencySample> samples)
    {
        using IEnumerator<FrequencySample> e = samples.GetEnumerator();
        if (!e.MoveNext()) yield break;
        FrequencySample prev = e.Current;
        while (e.MoveNext())
        {
            FrequencySample cur = e.Current;
            double dt = (cur.Time - prev.Time).TotalSeconds;
            RequireNotBackwards(prev.Time, cur.Time);
            if (dt > 0) yield return (cur.Time, (cur.Hz - prev.Hz) / dt);
            prev = cur;
        }
    }

    /// <summary>
    /// GR-572: a measured trace must run forwards in time. A sample stamped before its predecessor used to be stepped
    /// over without a word, so a trace spliced out of order (two files concatenated, a clock reset) scored as if it were
    /// whole; it is now refused, naming both time stamps. A repeated time stamp (zero interval) is still skipped: it has
    /// no interval to take a rate over and is common in recorded data. A gap between samples is still interpolated
    /// linearly (technical ch. 36): check a trace's cadence before scoring it.
    /// </summary>
    /// <exception cref="InvalidDataException">The later sample is stamped before the earlier one.</exception>
    public static void RequireNotBackwards(DateTime previous, DateTime current)
    {
        if (current < previous)
            throw new InvalidDataException(
                $"frequency trace: the sample at {current.ToString("O", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"follows one at {previous.ToString("O", System.Globalization.CultureInfo.InvariantCulture)}, earlier in time; " +
                "a trace must be in time order (sort it, or split it at the clock reset).");
    }

    public static FrequencyStats Summarize(IEnumerable<FrequencySample> samples)
    {
        int count = 0;
        double min = double.MaxValue, max = double.MinValue, sum = 0;
        double maxAbsRocof = 0;
        DateTime maxRocofAt = default;
        FrequencySample prev = default;
        bool havePrev = false;

        foreach (FrequencySample s in samples)
        {
            count++;
            sum += s.Hz;
            if (s.Hz < min) min = s.Hz;
            if (s.Hz > max) max = s.Hz;

            if (havePrev)
            {
                double dt = (s.Time - prev.Time).TotalSeconds;
                RequireNotBackwards(prev.Time, s.Time);
                if (dt > 0)
                {
                    double r = Math.Abs((s.Hz - prev.Hz) / dt);
                    if (r > maxAbsRocof) { maxAbsRocof = r; maxRocofAt = s.Time; }
                }
            }
            prev = s;
            havePrev = true;
        }

        return count == 0
            ? new FrequencyStats(0, 0, 0, 0, 0, default)
            : new FrequencyStats(count, min, max, sum / count, maxAbsRocof, maxRocofAt);
    }
}
