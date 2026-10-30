namespace GridSim.Core.Simulation.Health;

/// <summary>
/// A stated availability objective for a resident GridSim estimator service (NCSC CAF B5, "resilient networks and
/// systems": maintain availability to a stated objective and evidence it). The <see cref="HealthProbe"/> folds the
/// engine's own run counters against these thresholds, so "the service met its objective" is a checked fact, not
/// an assertion.
///
/// <para><b>Restriction aware.</b> <see cref="MaxWindowLatency"/> is measured against window ARRIVAL, never
/// against real time, so meeting even a tight latency objective can never imply nowcasting - the data processed is
/// still at least a horizon old. It bounds how promptly the service keeps up with the replay/permitted-age stream
/// it is fed, not how fresh that stream is.</para>
/// </summary>
/// <param name="MinConvergedFraction">The least acceptable converged/processed fraction over the window (0..1).</param>
/// <param name="MaxRejectedFreshFraction">The most acceptable rejected-too-fresh/processed fraction (0..1); a high
/// rate suggests a mis-timed or hostile feed.</param>
/// <param name="MaxWindowLatency">The most acceptable time to process one window after it arrives.</param>
public sealed record ServiceLevelObjective(
    double MinConvergedFraction,
    double MaxRejectedFreshFraction,
    TimeSpan MaxWindowLatency)
{
    /// <summary>A conservative default: at least 99% of processed windows converge, at most 1% are rejected as
    /// too fresh, and a window is processed within one second of arrival.</summary>
    public static ServiceLevelObjective Default { get; } =
        new(MinConvergedFraction: 0.99, MaxRejectedFreshFraction: 0.01, MaxWindowLatency: TimeSpan.FromSeconds(1));

    /// <summary>Validate the thresholds (fractions in 0..1, a non-negative latency). Fails loud.</summary>
    public void Validate()
    {
        if (!(MinConvergedFraction >= 0.0 && MinConvergedFraction <= 1.0))
            throw new ArgumentOutOfRangeException(nameof(MinConvergedFraction), MinConvergedFraction, "must be in 0..1.");
        if (!(MaxRejectedFreshFraction >= 0.0 && MaxRejectedFreshFraction <= 1.0))
            throw new ArgumentOutOfRangeException(nameof(MaxRejectedFreshFraction), MaxRejectedFreshFraction, "must be in 0..1.");
        if (MaxWindowLatency < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(MaxWindowLatency), MaxWindowLatency, "must be non-negative.");
    }
}
