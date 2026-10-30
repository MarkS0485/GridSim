namespace GridSim.Core.Simulation.Health;

/// <summary>The outcome of checking a run's counters against a <see cref="ServiceLevelObjective"/>.</summary>
/// <param name="Healthy">True when no objective was breached.</param>
/// <param name="BreachedObjectives">A one-line description of each breached objective (empty when healthy).</param>
/// <param name="ConvergedFraction">The observed converged/processed fraction (1.0 when nothing was processed).</param>
/// <param name="RejectedFreshFraction">The observed rejected-too-fresh/processed fraction (0.0 when none processed).</param>
/// <param name="WorstWindowLatency">The worst per-window latency observed, or null when latency was not measured.</param>
public sealed record HealthReport(
    bool Healthy,
    IReadOnlyList<string> BreachedObjectives,
    double ConvergedFraction,
    double RejectedFreshFraction,
    TimeSpan? WorstWindowLatency);

/// <summary>
/// Folds a resident estimator's own run counters (<see cref="RealtimeRunSummary"/>) and, optionally, the worst
/// per-window latency against a <see cref="ServiceLevelObjective"/> to produce a <see cref="HealthReport"/> (NCSC
/// CAF B5). A breach is the signal the hosting layer turns into an <c>AvailabilityDegraded</c> incident.
///
/// <para><b>Deterministic.</b> The verdict is a pure function of the counters passed in; it reads no wall clock, so
/// the same run produces the same report under replay. The service that calls it supplies the latency it measured
/// against window arrival (never against real time), keeping the restriction invariant intact.</para>
/// </summary>
public sealed class HealthProbe
{
    private readonly ServiceLevelObjective _slo;

    /// <summary>Create a probe for a given objective (validated up front).</summary>
    public HealthProbe(ServiceLevelObjective? slo = null)
    {
        _slo = slo ?? ServiceLevelObjective.Default;
        _slo.Validate();
    }

    /// <summary>The objective this probe checks against.</summary>
    public ServiceLevelObjective Objective => _slo;

    /// <summary>
    /// Check a run summary (and an optional worst-observed window latency) against the objective.
    /// </summary>
    /// <param name="summary">The engine's own run counters.</param>
    /// <param name="worstWindowLatency">The worst time to process one window after arrival, or null when the caller
    /// did not measure latency (then the latency objective is not checked).</param>
    public HealthReport Check(RealtimeRunSummary summary, TimeSpan? worstWindowLatency = null)
    {
        ArgumentNullException.ThrowIfNull(summary);
        int processed = summary.WindowsProcessed;

        // With nothing processed the fractions are neutral; whether that is healthy depends on why (see below).
        double convergedFraction = processed > 0 ? (double)summary.WindowsConverged / processed : 1.0;
        double rejectedFreshFraction = processed > 0 ? (double)summary.WindowsRejectedFresh / processed : 0.0;

        var breaches = new List<string>();
        if (processed > 0 && convergedFraction < _slo.MinConvergedFraction)
            breaches.Add($"converged fraction {convergedFraction:P1} is below the objective {_slo.MinConvergedFraction:P1}");
        if (processed > 0 && rejectedFreshFraction > _slo.MaxRejectedFreshFraction)
            breaches.Add($"rejected-too-fresh fraction {rejectedFreshFraction:P1} exceeds the objective {_slo.MaxRejectedFreshFraction:P1}");
        // Windows that never reached the estimator are evidence too. A run in which NOTHING was processed because every
        // window was rejected as too fresh or failed (malformed) used to report healthy with neutral fractions; and
        // failed windows were never read at all. Failed windows count against the converged objective: the usable
        // fraction is converged / (processed + failed).
        int failed = summary.WindowsFailed, fresh = summary.WindowsRejectedFresh;
        if (processed == 0 && (fresh > 0 || failed > 0))
            breaches.Add($"no window was processed ({fresh} rejected as too fresh, {failed} failed)");
        else if (processed > 0 && failed > 0)
        {
            double usable = (double)summary.WindowsConverged / (processed + failed);
            if (usable < _slo.MinConvergedFraction)
                breaches.Add($"{failed} window(s) failed; converged / (processed + failed) {usable:P1} is below the objective {_slo.MinConvergedFraction:P1}");
        }
        if (worstWindowLatency is TimeSpan latency && latency > _slo.MaxWindowLatency)
            breaches.Add($"worst window latency {latency.TotalMilliseconds:0} ms exceeds the objective {_slo.MaxWindowLatency.TotalMilliseconds:0} ms");

        return new HealthReport(breaches.Count == 0, breaches, convergedFraction, rejectedFreshFraction, worstWindowLatency);
    }
}
