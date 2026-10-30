using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

public class BadDataTests
{
    [Fact]
    public void Chi_square_quantiles_match_table_values_within_one_percent()
    {
        // Standard table values at p = 0.95.
        Assert.True(Math.Abs(ChiSquare.Quantile(10, 0.95) - 18.307) / 18.307 < 0.01);
        Assert.True(Math.Abs(ChiSquare.Quantile(50, 0.95) - 67.505) / 67.505 < 0.01);
        Assert.True(Math.Abs(ChiSquare.Quantile(100, 0.95) - 124.342) / 124.342 < 0.01);
        Assert.True(double.IsNaN(ChiSquare.Quantile(0, 0.95)));
    }

    [Fact] // GR-539: one and two degrees of freedom use the exact closed forms, not Wilson-Hilferty
    public void Chi_square_quantiles_at_one_and_two_dof_are_exact()
    {
        // Table values: chi-square(1, 0.95) = 3.841458820694124 (= 1.959963984540054^2), chi-square(2, 0.95) =
        // -2 ln 0.05 = 5.991464547107979, chi-square(2, 0.99) = -2 ln 0.01 = 9.210340371976182. Wilson-Hilferty gave
        // 3.7468 and 5.9369 at p = 0.95 (2.5 % and 0.9 % low).
        Assert.True(Math.Abs(ChiSquare.Quantile(1, 0.95) - 3.841458820694124) / 3.841458820694124 < 1e-8);
        Assert.True(Math.Abs(ChiSquare.Quantile(2, 0.95) - 5.991464547107979) / 5.991464547107979 < 1e-13);
        Assert.True(Math.Abs(ChiSquare.Quantile(2, 0.99) - 9.210340371976182) / 9.210340371976182 < 1e-13);
        Assert.True(Math.Abs(ChiSquare.Quantile(1, 0.99) - 6.634896601021214) / 6.634896601021214 < 1e-8);
    }

    [Fact]
    public void A_single_gross_error_is_identified_and_removed()
    {
        GridModel model = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet set = WlsEstimatorTests.ExactFullSet(model, truth);

        // Corrupt one branch-flow measurement by +20sigma.
        var ms = set.Measurements.ToList();
        int victim = ms.FindIndex(m => m.Kind == MeasurementKind.PFlowMw);
        Measurement bad = ms[victim] with { Value = ms[victim].Value + 20 * ms[victim].Sigma };
        ms[victim] = bad;
        set = set with { Measurements = ms };

        EstimationResult est = new WlsStateEstimator().Estimate(model, set);

        Assert.Equal(EstimationStatus.Converged, est.Status);
        Assert.True(est.ChiSquarePassed, $"J = {est.ObjectiveJ} should pass after removal (threshold {est.ChiSquareThreshold})");
        BadDataRemoval removal = Assert.Single(est.Removed);
        Assert.Equal(bad, removal.Measurement);
        Assert.True(removal.NormalizedResidual > 3.0);

        // With the gross error gone, the estimate is back at the truth.
        for (int i = 0; i < model.BusCount; i++)
        {
            Assert.True(Math.Abs(est.Vm[i] - truth.Vm[i]) < 1e-8, $"Vm[{i}] off by {est.Vm[i] - truth.Vm[i]}");
            Assert.True(Math.Abs(est.VaDeg[i] - truth.VaDeg[i]) < 1e-7);
        }
    }

    [Fact]
    public void A_clean_set_passes_chi_square_with_no_removals()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        EstimationResult est = new WlsStateEstimator().Estimate(model, WlsEstimatorTests.ExactFullSet(model, truth));

        Assert.True(est.ChiSquarePassed);
        Assert.Empty(est.Removed);
        Assert.True(est.DegreesOfFreedom > 0);
        Assert.False(double.IsNaN(est.ChiSquareThreshold));
        // Normalized residuals were computed for the diagnostic table.
        Assert.Contains(est.Residuals, r => double.IsFinite(r.NormalizedResidual));
    }

    private static (GridModel Model, MeasurementSet Set, Measurement CriticalFlow) RadialTailFixture(double flowCorruptionSigma)
    {
        // 1(slack) -- 2 -- 3: the head of the feeder is redundantly measured, but bus 3 is
        // observed by exactly two measurements (P and Q flow on 2-3) pinning its two states -
        // both are critical: no redundancy stands behind them.
        var model = new GridModel
        {
            BaseMva = 100,
            Name = "radial-tail",
            Buses = new[]
            {
                new Bus(1, BusType.Slack),
                new Bus(2, BusType.Pq, Pd: 20),
                new Bus(3, BusType.Pq, Pd: 10),
            },
            Generators = new[] { new Generator(1, 30) },
            Branches = new[]
            {
                new Branch(1, 2, 0.01, 0.1),
                new Branch(2, 3, 0.01, 0.1),
            },
        };
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(truth.Converged);

        int i2 = truth.BusIds.ToList().IndexOf(2);
        LineFlow f12 = truth.LineFlows[0];
        LineFlow f23 = truth.LineFlows[1];
        Measurement criticalFlow = new(
            MeasurementKind.PFlowMw, f23.PFromMw + flowCorruptionSigma * 1.0, 1.0, From: 2, To: 3);
        var set = new MeasurementSet
        {
            Case = model.Name,
            Measurements = new[]
            {
                // Redundant head: voltages, slack-side flow both ends, bus-2 injection.
                new Measurement(MeasurementKind.VmPu, truth.Vm[0], 0.004, Bus: 1),
                new Measurement(MeasurementKind.VmPu, truth.Vm[i2], 0.004, Bus: 2),
                new Measurement(MeasurementKind.PFlowMw, f12.PFromMw, 1.0, From: 1, To: 2),
                new Measurement(MeasurementKind.QFlowMvar, f12.QFromMvar, 1.0, From: 1, To: 2),
                new Measurement(MeasurementKind.PFlowMw, f12.PToMw, 1.0, From: 1, To: 2, End: BranchEnd.To),
                new Measurement(MeasurementKind.QFlowMvar, f12.QToMvar, 1.0, From: 1, To: 2, End: BranchEnd.To),
                // NOTE: no bus-2 injection measurements - a neighbour's injection would observe
                // bus 3 too and quietly give the tail redundancy.
                // Minimal tail: exactly two observations of bus 3's two states.
                criticalFlow,
                new Measurement(MeasurementKind.QFlowMvar, f23.QFromMvar, 1.0, From: 2, To: 3),
            },
        };
        return (model, set, criticalFlow);
    }

    [Fact]
    public void Critical_measurements_are_flagged_and_never_removed()
    {
        (GridModel model, MeasurementSet set, Measurement criticalFlow) = RadialTailFixture(0);

        EstimationResult est = new WlsStateEstimator().Estimate(model, set);
        Assert.Equal(EstimationStatus.Converged, est.Status);
        Assert.Empty(est.Removed);

        MeasurementResidual tail = est.Residuals.Single(r => r.Measurement == criticalFlow);
        Assert.True(tail.IsCritical);
        Assert.True(double.IsNaN(tail.NormalizedResidual)); // no redundancy to normalise against
    }

    [Fact]
    public void A_gross_error_on_a_critical_measurement_is_undetectable_and_stays_flagged()
    {
        // The textbook trap the criticality flag exists for: corrupt the only observer of a
        // state and the error is absorbed into the estimate - chi-square stays quiet. The honest output
        // is the IsCritical flag on the residual table, never a removal.
        (GridModel model, MeasurementSet set, Measurement criticalFlow) = RadialTailFixture(20);

        EstimationResult est = new WlsStateEstimator().Estimate(model, set);
        Assert.Equal(EstimationStatus.Converged, est.Status);
        Assert.True(est.ChiSquarePassed);
        Assert.Empty(est.Removed);
        MeasurementResidual tail = est.Residuals.Single(r => r.Measurement == criticalFlow);
        Assert.True(tail.IsCritical);
        Assert.True(Math.Abs(tail.Residual) < 1e-6); // structurally near-zero residual
    }

    [Fact]
    public void A_removed_measurement_is_backfilled_from_the_prior_when_opted_in()
    {
        GridModel model = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet set = WlsEstimatorTests.ExactFullSet(model, truth);

        var ms = set.Measurements.ToList();
        int victim = ms.FindIndex(m => m.Kind == MeasurementKind.PInjMw);
        Measurement bad = ms[victim] with { Value = ms[victim].Value + 20 * ms[victim].Sigma };
        ms[victim] = bad;
        set = set with { Measurements = ms };

        // Warm-start from the truth: the prior a --prior=solve pipeline supplies. The backfill
        // pseudo is then the prior's view of the removed quantity - anchored, honestly loose.
        var opt = new EstimationOptions
        {
            FlatStart = false,
            WarmStart = truth,
            BackfillRemovedFromPrior = true,
        };
        EstimationResult est = new WlsStateEstimator().Estimate(model, set, opt);

        Assert.Equal(EstimationStatus.Converged, est.Status);
        Assert.True(est.ChiSquarePassed);
        BadDataRemoval removal = Assert.Single(est.Removed);
        Assert.Equal(bad, removal.Measurement);

        // The replacement participates in the final fit: same kind+bus, the loud source tag,
        // a 3x-inflated sigma, and a value near the prior's (= truth's) injection.
        MeasurementResidual backfill = Assert.Single(
            est.Residuals, r => r.Measurement.Source == WlsStateEstimator.BackfillSource);
        Assert.Equal(bad.Kind, backfill.Measurement.Kind);
        Assert.Equal(bad.Bus, backfill.Measurement.Bus);
        Assert.True(backfill.Measurement.Sigma >= 3.0 * bad.Sigma - 1e-9);
        double truthInj = truth.PInjMw[truth.BusIds.ToList().IndexOf(bad.Bus)];
        Assert.True(Math.Abs(backfill.Measurement.Value - truthInj) < 1.0,
            $"backfill value {backfill.Measurement.Value} should sit near the prior injection {truthInj}");

        // And the estimate itself stays at the truth.
        for (int i = 0; i < model.BusCount; i++)
            Assert.True(Math.Abs(est.Vm[i] - truth.Vm[i]) < 1e-6);
    }

    [Fact]
    public void Reactive_removals_are_not_backfilled()
    {
        GridModel model = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet set = WlsEstimatorTests.ExactFullSet(model, truth);

        var ms = set.Measurements.ToList();
        int victim = ms.FindIndex(m => m.Kind == MeasurementKind.QInjMvar);
        ms[victim] = ms[victim] with { Value = ms[victim].Value + 20 * ms[victim].Sigma };
        set = set with { Measurements = ms };

        var opt = new EstimationOptions { FlatStart = false, WarmStart = truth, BackfillRemovedFromPrior = true };
        EstimationResult est = new WlsStateEstimator().Estimate(model, set, opt);

        // The defective Q row is removed and simply excluded - a VAr-blind layer's Q rows are
        // pseudos, and the prior embeds them, so backfilling would re-inject the defect.
        Assert.Equal(EstimationStatus.Converged, est.Status);
        Assert.Single(est.Removed);
        Assert.DoesNotContain(est.Residuals, r => r.Measurement.Source == WlsStateEstimator.BackfillSource);
    }

    [Fact]
    public void Backfill_from_the_flat_start_is_refused()
    {
        // GR-531: with FlatStart and no WarmStart the "prior" would be the flat start; it used to run anyway.
        GridModel model = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet set = WlsEstimatorTests.ExactFullSet(model, truth);
        var ex = Assert.Throws<ArgumentException>(() =>
            new WlsStateEstimator().Estimate(model, set, new EstimationOptions { BackfillRemovedFromPrior = true }));
        Assert.Contains("flat start", ex.Message, StringComparison.Ordinal);
    }
}
