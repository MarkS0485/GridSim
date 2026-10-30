using System;
using GridSim.Core.Simulation;
using GridSim.Core.Simulation.Health;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The availability health probe (NCSC CAF B5): it folds a resident run's own counters against a
/// <see cref="ServiceLevelObjective"/> and reports whether the objective was met. A clean run is healthy; a low
/// converged fraction, a high rejected-too-fresh fraction, or an over-budget window latency each breach; a run
/// that processed nothing is neutral (healthy); and the verdict is a pure function of its inputs (deterministic).
/// </summary>
public sealed class HealthProbeTests
{
    private static RealtimeRunSummary Summary(int processed, int converged, int rejectedFresh, int failed = 0) =>
        new(processed, converged, rejectedFresh, failed, FirstStateTimeUtc: null, LastStateTimeUtc: null);

    [Fact]
    public void A_fully_converged_run_is_healthy()
    {
        var probe = new HealthProbe();
        HealthReport report = probe.Check(Summary(processed: 100, converged: 100, rejectedFresh: 0));

        Assert.True(report.Healthy);
        Assert.Empty(report.BreachedObjectives);
        Assert.Equal(1.0, report.ConvergedFraction);
        Assert.Equal(0.0, report.RejectedFreshFraction);
    }

    [Fact]
    public void A_low_converged_fraction_breaches_the_objective()
    {
        var probe = new HealthProbe();   // default MinConvergedFraction 0.99
        HealthReport report = probe.Check(Summary(processed: 100, converged: 90, rejectedFresh: 0));

        Assert.False(report.Healthy);
        Assert.Single(report.BreachedObjectives);
        Assert.Equal(0.90, report.ConvergedFraction, 12);
    }

    [Fact]
    public void A_high_rejected_fresh_fraction_breaches_the_objective()
    {
        var probe = new HealthProbe();   // default MaxRejectedFreshFraction 0.01
        HealthReport report = probe.Check(Summary(processed: 100, converged: 100, rejectedFresh: 20));

        Assert.False(report.Healthy);
        Assert.Contains(report.BreachedObjectives, b => b.Contains("rejected-too-fresh", StringComparison.Ordinal));
        Assert.Equal(0.20, report.RejectedFreshFraction, 12);
    }

    [Fact]
    public void A_worst_window_latency_beyond_the_objective_breaches()
    {
        var probe = new HealthProbe();   // default MaxWindowLatency 1 s
        TimeSpan tooSlow = ServiceLevelObjective.Default.MaxWindowLatency + TimeSpan.FromSeconds(1);

        HealthReport report = probe.Check(Summary(processed: 100, converged: 100, rejectedFresh: 0), tooSlow);

        Assert.False(report.Healthy);
        Assert.Contains(report.BreachedObjectives, b => b.Contains("window latency", StringComparison.Ordinal));
        Assert.Equal(tooSlow, report.WorstWindowLatency);
    }

    [Fact]
    public void A_latency_within_the_objective_does_not_breach()
    {
        var probe = new HealthProbe();
        TimeSpan fast = TimeSpan.FromMilliseconds(10);
        HealthReport report = probe.Check(Summary(processed: 100, converged: 100, rejectedFresh: 0), fast);

        Assert.True(report.Healthy);
        Assert.Empty(report.BreachedObjectives);
    }

    [Fact]
    public void A_run_that_processed_nothing_is_healthy_with_neutral_fractions()
    {
        var probe = new HealthProbe();
        HealthReport report = probe.Check(Summary(processed: 0, converged: 0, rejectedFresh: 0));

        Assert.True(report.Healthy);
        Assert.Empty(report.BreachedObjectives);
        Assert.Equal(1.0, report.ConvergedFraction);
        Assert.Equal(0.0, report.RejectedFreshFraction);
    }

    [Fact]
    public void The_verdict_is_deterministic_for_the_same_input()
    {
        var probe = new HealthProbe();
        RealtimeRunSummary summary = Summary(processed: 100, converged: 95, rejectedFresh: 3);
        TimeSpan latency = TimeSpan.FromMilliseconds(500);

        HealthReport a = probe.Check(summary, latency);
        HealthReport b = probe.Check(summary, latency);

        // Compare component by component: the record's list member is reference-compared by the default equality,
        // so determinism is asserted on the values the probe actually computes.
        Assert.Equal(a.Healthy, b.Healthy);
        Assert.Equal(a.ConvergedFraction, b.ConvergedFraction);
        Assert.Equal(a.RejectedFreshFraction, b.RejectedFreshFraction);
        Assert.Equal(a.WorstWindowLatency, b.WorstWindowLatency);
        Assert.Equal(a.BreachedObjectives, b.BreachedObjectives);
    }
}
