namespace GridSim.Core.Frequency;

/// <summary>One measured system-frequency reading (e.g. a 1-second NESO sample).</summary>
public readonly record struct FrequencySample(DateTime Time, double Hz);

/// <summary>
/// A frequency point on the fine (e.g. 20 ms) timeline, with the rate of change of
/// frequency that produced it. RoCoF is in Hz/s  -  the headline stability number:
/// the steeper it is after a disturbance, the less inertia is holding the grid up.
/// </summary>
/// <remarks>Any NaN in <see cref="Hz"/> or <see cref="RocofHzPerS"/> (a diverged run) is stored as the one canonical
/// NaN (<see cref="GridSim.Core.Numerics.DetMath.CanonicalNaN"/>), so its bytes do not depend on the JIT tier.</remarks>
public readonly record struct FrequencyPoint(DateTime Time, double Hz, double RocofHzPerS)
{
    /// <summary>Frequency (Hz); a NaN is stored canonical.</summary>
    public double Hz { get; init => field = GridSim.Core.Numerics.DetMath.CanonicalNaN(value); } = GridSim.Core.Numerics.DetMath.CanonicalNaN(Hz);

    /// <summary>RoCoF (Hz/s); a NaN is stored canonical.</summary>
    public double RocofHzPerS { get; init => field = GridSim.Core.Numerics.DetMath.CanonicalNaN(value); } = GridSim.Core.Numerics.DetMath.CanonicalNaN(RocofHzPerS);
}

/// <summary>Summary of a frequency trace.</summary>
public readonly record struct FrequencyStats(
    int Count,
    double MinHz, double MaxHz, double MeanHz,
    double MaxAbsRocofHzPerS, DateTime MaxRocofAt);
