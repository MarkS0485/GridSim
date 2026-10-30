using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The phase-angle feed: robust parsing (ISO-8601 UTC keeps its colons), frequency time-projection, the
/// slack re-referencing that makes the result invariant to a common-mode system angle, and the headline
/// physics claim - genuine per-bus angle measurements sharpen the estimate's angle profile.
/// </summary>
public class PhaseAngleFeedTests
{
    [Fact]
    public void Parse_keeps_the_iso_utc_colons_intact()
    {
        IReadOnlyList<PhaseAngleObs> obs = PhaseAngleFeed.Parse("12:5.3:2026-05-01T20:00:00Z:49.98");
        PhaseAngleObs o = Assert.Single(obs);
        Assert.Equal(12, o.Bus);
        Assert.Equal(5.3, o.AngleDeg, 9);
        Assert.Equal(49.98, o.FreqHz, 9);
        Assert.Equal(new DateTime(2026, 5, 1, 20, 0, 0, DateTimeKind.Utc), o.Utc);
    }

    [Fact]
    public void Parse_accepts_space_and_comma_separated_tuples()
    {
        var obs = PhaseAngleFeed.Parse("1:0:2026-05-01T20:00:00Z:50 2:3.5:2026-05-01T20:00:00Z:50,3:-2:2026-05-01T20:00:00Z:50");
        Assert.Equal(3, obs.Count);
        Assert.Equal(new[] { 1, 2, 3 }, obs.Select(o => o.Bus));
    }

    [Fact]
    public void Malformed_tuple_fails_loud()
        => Assert.Throws<FormatException>(() => PhaseAngleFeed.Parse("notabus:1:2026-05-01T20:00:00Z:50"));

    [Fact]
    public void Frequency_projects_the_angle_36_degrees_per_0_1Hz_second()
    {
        // A bus at +0.1 Hz for 1 s advances 36° (GDA's own PHASEANGLE_INFERED invariant).
        GridModel m = IeeeCases.Case14();
        var t0 = new DateTime(2026, 5, 1, 20, 0, 0, DateTimeKind.Utc);
        int bus = m.Buses.First(b => b.Type != BusType.Slack).Id;
        var obs = new[] { new PhaseAngleObs(bus, 0.0, t0, 50.1) };

        (IReadOnlyList<Measurement> rows, _) = PhaseAngleFeed.ToMeasurements(
            m, obs, new PhaseAngleOptions { Mode = PhaseAngleReference.AlreadyReferenced, TargetUtc = t0.AddSeconds(1) });

        Assert.Equal(36.0, Assert.Single(rows).Value, 6);
    }

    [Fact]
    public void Auto_reference_cancels_a_common_mode_system_angle()
    {
        // The physics: injecting an absolute feed = truth angle + a common system phase theta_sys on EVERY
        // bus (incl. slack) must, after slack re-referencing, reduce to the truth angles - theta_sys cancels.
        GridModel m = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(m);
        var t0 = new DateTime(2026, 5, 1, 20, 0, 0, DateTimeKind.Utc);
        const double thetaSys = 137.5;   // arbitrary large common-mode offset

        var obs = new List<PhaseAngleObs>();
        for (int i = 0; i < truth.BusIds.Count; i++)
            obs.Add(new PhaseAngleObs(truth.BusIds[i], truth.VaDeg[i] + thetaSys, t0, 50.0)); // f=50 => no drift

        (IReadOnlyList<Measurement> rows, _) = PhaseAngleFeed.ToMeasurements(
            m, obs, new PhaseAngleOptions { Mode = PhaseAngleReference.AutoReference, TargetUtc = t0 });

        // Each re-referenced row equals the bus's true slack-referenced angle: theta_sys is gone.
        var truthById = truth.BusIds.Select((id, i) => (id, va: truth.VaDeg[i])).ToDictionary(x => x.id, x => x.va);
        foreach (Measurement row in rows)
            Assert.Equal(truthById[row.Bus], row.Value, 6);
    }

    [Fact]
    public void Genuine_per_bus_angles_sharpen_the_estimate()
    {
        // Baseline: a noisy P/Q/Vm set (angles observed only indirectly through P). Adding real per-bus
        // angle rows must materially cut the angle error - the estimator-improvement claim, physically.
        GridModel m = IeeeCases.Case30();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(truth.Converged);

        MeasurementSet baseSet = SyntheticMeasurements.Sample(
            m, truth, new SyntheticMeasurementOptions { Seed = 20260725, NoiseScale = 3.0 });

        EstimationResult estA = new WlsStateEstimator().Estimate(m, baseSet);
        Assert.True(estA.Converged);
        double rmseA = AngleRmse(estA, truth);

        // Inject the true per-bus angles (already slack-referenced; f=50 so no projection) at a tight sigma.
        var t0 = new DateTime(2026, 5, 1, 20, 0, 0, DateTimeKind.Utc);
        var obs = new List<PhaseAngleObs>();
        for (int i = 0; i < truth.BusIds.Count; i++)
            obs.Add(new PhaseAngleObs(truth.BusIds[i], truth.VaDeg[i], t0, 50.0));
        (IReadOnlyList<Measurement> angleRows, _) = PhaseAngleFeed.ToMeasurements(
            m, obs, new PhaseAngleOptions { Mode = PhaseAngleReference.Raw, SigmaDeg = 0.1 });

        MeasurementSet withAngles = baseSet with { Measurements = baseSet.Measurements.Concat(angleRows).ToList() };
        EstimationResult estB = new WlsStateEstimator().Estimate(m, withAngles);
        Assert.True(estB.Converged);
        double rmseB = AngleRmse(estB, truth);

        Assert.True(rmseA > 0.05, $"baseline angle error too small to be a fair test: {rmseA:F4} deg");
        Assert.True(rmseB < rmseA * 0.5, $"angle RMSE did not improve: baseline {rmseA:F4} deg -> with angles {rmseB:F4} deg");
    }

    private static double AngleRmse(EstimationResult est, PowerFlowResult truth)
    {
        var truthById = truth.BusIds.Select((id, i) => (id, va: truth.VaDeg[i])).ToDictionary(x => x.id, x => x.va);
        double s = 0;
        int n = 0;
        for (int i = 0; i < est.BusIds.Count; i++)
        {
            double d = est.VaDeg[i] - truthById[est.BusIds[i]];
            s += d * d;
            n++;
        }
        return Math.Sqrt(s / n);
    }
}
