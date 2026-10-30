using GridSim.Core.Dynamics;
using GridSim.Core.Frequency;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The inverse-swing-equation tools: estimate inertia from RoCoF, score a response against
/// the GB limits, and size a TSGB device to keep a loss inside those limits.
/// </summary>
public class InertiaAndLimitsTests
{
    [Fact]
    public void Inertia_estimate_round_trips_the_simulator()
    {
        const double eMwS = 100_000;      // 100 GVAs spinning
        const double lossMw = 1000;
        FrequencyResponse r = FrequencyDynamics.Simulate(
            eMwS, 30_000, new[] { new FrequencyEvent(1.0, -lossMw) });

        // The first post-event RoCoF is governor/damping-free, so the estimate is exact.
        double est = InertiaEstimator.EstimateInertiaMwS(r, lossMw);
        Assert.True(Math.Abs(est - eMwS) < 1.0, $"estimated {est:F1} MWs, expected ~{eMwS}");
    }

    [Fact]
    public void Predict_and_estimate_are_consistent()
    {
        double rocof = InertiaEstimator.PredictRocof(100_000, -1000);   // -0.25 Hz/s
        Assert.Equal(-0.25, rocof, 6);
        Assert.Equal(100_000, InertiaEstimator.EstimateInertiaMwS(1000, rocof), 3);
        Assert.Equal(100.0, InertiaEstimator.EstimateInertiaGvaS(1000, rocof), 6);
    }

    [Fact]
    public void Estimate_h_seconds_on_a_base()
    {
        // 100 GVAs on a 200 GVA fleet base => H ~= 0.5 s aggregate (toy numbers).
        double h = InertiaEstimator.EstimateHSeconds(1000, -0.25, 200_000);
        Assert.Equal(0.5, h, 6);
    }

    [Fact]
    public void Mild_trip_stays_within_limits()
    {
        FrequencyResponse r = FrequencyDynamics.Simulate(
            200_000, 30_000, new[] { new FrequencyEvent(1.0, -500) });
        FrequencyAssessment a = FrequencyLimits.Assess(r);
        Assert.False(a.RocofExceedsLimit);
        Assert.False(a.BelowStatutory);
        Assert.False(a.LfddAtRisk);
    }

    [Fact]
    public void Severe_low_inertia_trip_breaches_rocof()
    {
        FrequencyResponse r = FrequencyDynamics.Simulate(
            80_000, 30_000, new[] { new FrequencyEvent(1.0, -1800) });
        FrequencyAssessment a = FrequencyLimits.Assess(r);
        Assert.True(a.RocofExceedsLimit, $"RoCoF {a.InitialRocofHzPerS:F3} should exceed 0.125 Hz/s");
    }

    [Fact]
    public void Sizer_produces_a_device_that_meets_both_targets()
    {
        const double systemE = 80_000, demand = 30_000, loss = 1320;
        const double targetRocof = 0.125, targetNadir = 49.5;

        TsgbSizing s = TsgbSizer.SizeForTargets(systemE, demand, loss, targetRocof, targetNadir);

        Assert.True(s.SyntheticInertiaMwS > 0, "a low-inertia system needs synthetic inertia");
        Assert.True(Math.Abs(s.AchievedRocofHzPerS) <= targetRocof + 1e-6,
            $"achieved RoCoF {s.AchievedRocofHzPerS:F3} should meet {targetRocof}");
        Assert.True(s.AchievedNadirHz >= targetNadir - 0.05,
            $"achieved nadir {s.AchievedNadirHz:F3} should meet {targetNadir}");
    }

    [Fact]
    public void Trace_assessment_flags_a_dip_and_high_rocof()
    {
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        double[] hz = { 50.0, 50.0, 50.0, 50.0, 50.0, 49.4, 49.4, 49.45, 49.6, 49.8, 50.0 };
        var samples = new List<FrequencySample>();
        for (int i = 0; i < hz.Length; i++) samples.Add(new FrequencySample(t0.AddSeconds(i), hz[i]));

        FrequencyTraceReport rep = FrequencyLimits.AssessTrace(samples);

        Assert.True(rep.RocofLimitExceeded);            // 0.6 Hz/s step  0.125
        Assert.True(rep.SamplesBelowStatutory >= 2);    // 49.4/49.45 < 49.5
        Assert.True(rep.SamplesBelowOperational >= 4);  // several < 49.8
        Assert.False(rep.LfddReached);                  // never <= 48.8
        Assert.Equal(49.4, rep.MinHz, 3);
    }

    [Fact]
    public void Sizer_asks_for_almost_nothing_when_already_secure()
    {
        // Huge inertia, tiny loss: already inside both limits, so the device should be ~0.
        TsgbSizing s = TsgbSizer.SizeForTargets(400_000, 30_000, 300, 0.125, 49.5);
        Assert.Equal(0.0, s.SyntheticInertiaMwS, 1);          // RoCoF floor already met
        Assert.True(s.FastResponseMw < 50, $"FFR {s.FastResponseMw:F1} MW should be negligible");
        Assert.True(Math.Abs(s.AchievedRocofHzPerS) <= 0.125 + 1e-6);
        Assert.True(s.AchievedNadirHz >= 49.5 - 0.05);
    }

    [Fact]
    public void Steady_trace_reports_no_breaches()
    {
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var samples = new List<FrequencySample>();
        for (int i = 0; i < 60; i++) samples.Add(new FrequencySample(t0.AddSeconds(i), 50.0 + (i % 2 == 0 ? 0.01 : -0.01)));

        FrequencyTraceReport rep = FrequencyLimits.AssessTrace(samples);
        Assert.False(rep.RocofLimitExceeded);
        Assert.Equal(0, rep.SamplesBelowOperational);
        Assert.Equal(0, rep.SamplesBelowStatutory);
        Assert.False(rep.LfddReached);
    }

    [Fact]
    public void Rocof_screening_ranks_worst_loss_first()
    {
        var conts = new[]
        {
            new Contingency("small unit", 300, 5_000),
            new Contingency("big unit", 1800, 8_000),
        };
        IReadOnlyList<RocofScreeningResult> res = RocofScreening.Screen(100_000, 30_000, conts);

        Assert.Equal("big unit", res[0].Name);   // largest loss + lowest remaining inertia = worst RoCoF
        Assert.True(Math.Abs(res[0].InitialRocofHzPerS) > Math.Abs(res[1].InitialRocofHzPerS));
        Assert.True(res[0].RocofBreach);          // 1800 MW on ~92 GVAs => well over 0.125 Hz/s
    }
}
