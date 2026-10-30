using GridSim.Core.Dynamics;
using GridSim.Core.Frequency;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R081: the over-frequency limits (operational 50.2 Hz, statutory 50.5 Hz) were declared and never assessed, so a loss
/// of demand or of an exporting interconnector (a positive event) scored "within limits". FrequencyResponse now carries
/// the run's peak, and Assess / AssessTrace report the high side with the same strict comparison as the low side. Every
/// run that stays at or below 50.2 Hz (every under-frequency study) keeps its old summary text.
/// </summary>
public sealed partial class B1SwingKernelTests
{
    [Fact]
    public void R081_the_peak_is_the_highest_point_on_the_trajectory()
    {
        FrequencyResponse up = FrequencyDynamics.Simulate(200_000, 30_000, new[] { new FrequencyEvent(1.0, +1320.0) });
        Assert.Equal(up.Points.Max(p => p.Hz), up.PeakHz);
        Assert.True(up.PeakHz > 50.0);
        FrequencyResponse down = FrequencyDynamics.Simulate(200_000, 30_000, new[] { new FrequencyEvent(1.0, -1320.0) });
        Assert.Equal(50.0, down.PeakHz);   // a loss from nominal never rises above the start
        Assert.Equal(49.0, new FrequencyResponse(0, 49.0, 0, 49.0, Array.Empty<FrequencyPoint>()).PeakHz);   // no points: the nadir
    }

    [Fact]
    public void R081_a_loss_of_demand_that_crosses_the_statutory_high_edge_is_no_longer_within_limits()
    {
        // 5 GW of demand lost on a 50 GW.s system: the frequency rises well past 50.5 Hz.
        FrequencyResponse r = FrequencyDynamics.Simulate(50_000, 30_000, new[] { new FrequencyEvent(1.0, +5000.0) });
        Assert.True(r.PeakHz > FrequencyLimits.StatutoryHighHz, $"peak {r.PeakHz:R}");
        FrequencyAssessment a = FrequencyLimits.Assess(r);
        Assert.True(a.AboveStatutory);
        Assert.True(a.AboveOperational);
        Assert.False(a.BelowStatutory);
        Assert.Equal(r.PeakHz, a.PeakHz);
        Assert.Equal("ABOVE STATUTORY (50.5 Hz)", a.Summary);
    }

    [Fact]
    public void R081_a_small_rise_breaches_only_the_operational_band()
    {
        // A rise past 50.2 Hz but short of 50.5 Hz, at a RoCoF inside every limit: +500 MW on 200 GW.s and 30 GW of
        // demand. Initial RoCoF 50 / (2 x 200 000) x 500 = 0.0625 Hz/s (under the legacy 0.125); the droop-and-damping
        // steady state is 500 / (300 + 6000 / 2.5) = 0.185 Hz, and the 8 s governor lag overshoots it (x 1.71), so the peak
        // is about 50.32 Hz.
        // Moved 2026-10 (C2, final batch; B1 R081, Ruling R1 and Mark's go on the final-batch moves 2026-10-10): the
        // test's own setup. At +800 MW the overshoot (x 1.71 of the 0.296 Hz steady state) peaks at 50.50767428801575 Hz,
        // past 50.5, so the run is not the 'operational only' case the test names; the model is symmetric (-800 MW gives a
        // nadir of 49.49232571198425 = 100 - peak) and settles at 50.2963 = 800 / 2700, so the code is right. Before ->
        // after: +800.0 MW -> +500.0 MW (assertions unchanged).
        FrequencyResponse r = FrequencyDynamics.Simulate(200_000, 30_000, new[] { new FrequencyEvent(1.0, +500.0) },
            new DynamicsConfig { DurationSeconds = 60 });
        Assert.InRange(r.PeakHz, FrequencyLimits.OperationalHighHz, FrequencyLimits.StatutoryHighHz);
        FrequencyAssessment a = FrequencyLimits.Assess(r);
        Assert.True(a.AboveOperational);
        Assert.False(a.AboveStatutory);
        Assert.False(a.RocofExceedsLegacyLimit);
        Assert.Equal("above operational (50.2 Hz)", a.Summary);
    }

    [Fact]
    public void R081_under_frequency_runs_keep_their_old_summary_and_flags()
    {
        foreach (double loss in new[] { 200.0, 1320.0, 1800.0, 6000.0 })
            foreach (double e in new[] { 50_000.0, 200_000.0 })
            {
                FrequencyResponse r = FrequencyDynamics.Simulate(e, 30_000, new[] { new FrequencyEvent(1.0, -loss) });
                FrequencyAssessment a = FrequencyLimits.Assess(r);
                Assert.False(a.AboveOperational);
                Assert.False(a.AboveStatutory);
                // The pre-R081 summary chain, verbatim.
                bool rocofLegacy = FrequencyLimits.BreachesRocof(Math.Abs(r.InitialRocofHzPerS), FrequencyLimits.RocofLimitLegacyHzPerS);
                bool rocofActive = FrequencyLimits.BreachesRocof(Math.Abs(r.InitialRocofHzPerS), FrequencyLimits.ActiveRocofLimitHzPerS);
                string old =
                    r.NadirHz <= FrequencyLimits.LfddFirstStageHz ? "LFDD RISK  -  demand disconnection"
                    : r.NadirHz < FrequencyLimits.StatutoryLowHz ? "BELOW STATUTORY (49.5 Hz)"
                    : rocofActive ? $"RoCoF OVER {FrequencyLimits.ActiveRocofLimitHzPerS:0.###} Hz/s"
                    : rocofLegacy ? $"RoCoF OVER legacy {FrequencyLimits.RocofLimitLegacyHzPerS:0.###} Hz/s"
                    : r.NadirHz < FrequencyLimits.OperationalLowHz ? "below operational (49.8 Hz)"
                    : "within limits";
                Assert.Equal(old, a.Summary);
            }
    }

    [Fact]
    public void R081_a_peak_exactly_on_a_band_edge_is_inside_the_band()
    {
        var pts = new[] { new FrequencyPoint(DateTime.UnixEpoch, 50.0, 0.0), new FrequencyPoint(DateTime.UnixEpoch.AddSeconds(1), 50.5, 0.0) };
        FrequencyAssessment a = FrequencyLimits.Assess(new FrequencyResponse(0.0, 50.0, 0.0, 50.5, pts));
        Assert.True(a.AboveOperational);
        Assert.False(a.AboveStatutory);   // strict, as the low side: 49.5 exactly is not below statutory
    }

    [Fact]
    public void R081_the_trace_scan_counts_samples_above_the_high_edges()
    {
        DateTime t0 = new(2024, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        double[] hz = { 50.0, 50.1, 50.2, 50.25, 50.6, 50.55, 50.3, 50.0 };
        var samples = hz.Select((f, i) => new FrequencySample(t0.AddSeconds(i), f)).ToList();
        FrequencyTraceReport rep = FrequencyLimits.AssessTrace(samples);
        Assert.Equal(4, rep.SamplesAboveOperational);   // 50.25, 50.6, 50.55, 50.3 (50.2 itself is inside)
        Assert.Equal(2, rep.SamplesAboveStatutory);     // 50.6, 50.55
        Assert.Equal(0, rep.SamplesBelowOperational);
    }
}
