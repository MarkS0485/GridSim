using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>The sparse (above-threshold) estimator backend must agree with the dense one -
/// same single Jacobian evaluation path, same statistics, same answers. Forced sparse via
/// <see cref="EstimationOptions.SparseBusThreshold"/> = 0 on the standard cases.</summary>
public class SparseEstimatorTests
{
    private static readonly EstimationOptions Sparse = new() { SparseBusThreshold = 0 };

    [Fact]
    public void Sparse_backend_recovers_exact_truth_to_machine_precision()
    {
        GridModel model = IeeeCases.Case118();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet set = SyntheticMeasurements.Sample(model, truth, new SyntheticMeasurementOptions { NoiseScale = 0 });

        EstimationResult est = new WlsStateEstimator().Estimate(model, set, Sparse);
        Assert.Equal(EstimationStatus.Converged, est.Status);
        for (int i = 0; i < model.BusCount; i++)
        {
            Assert.True(Math.Abs(est.Vm[i] - truth.Vm[i]) < 1e-9, $"Vm[{i}] off by {est.Vm[i] - truth.Vm[i]}");
            Assert.True(Math.Abs(est.VaDeg[i] - truth.VaDeg[i]) < 1e-8);
        }
    }

    [Fact]
    public void Sparse_and_dense_backends_agree_on_a_noisy_set()
    {
        GridModel model = IeeeCases.Case118();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet set = SyntheticMeasurements.Sample(model, truth);

        EstimationResult dense = new WlsStateEstimator().Estimate(model, set);
        EstimationResult sparse = new WlsStateEstimator().Estimate(model, set, Sparse);

        Assert.Equal(EstimationStatus.Converged, dense.Status);
        Assert.Equal(EstimationStatus.Converged, sparse.Status);
        for (int i = 0; i < model.BusCount; i++)
        {
            Assert.True(Math.Abs(dense.Vm[i] - sparse.Vm[i]) < 1e-8,
                $"Vm[{i}]: dense {dense.Vm[i]} vs sparse {sparse.Vm[i]}");
            Assert.True(Math.Abs(dense.VaDeg[i] - sparse.VaDeg[i]) < 1e-7);
        }
        Assert.True(Math.Abs(dense.ObjectiveJ - sparse.ObjectiveJ) < 1e-6 * Math.Max(1, dense.ObjectiveJ));
        Assert.Equal(dense.ChiSquarePassed, sparse.ChiSquarePassed);
        // Within the diagnosis size cap the sparse backend now runs the same numerical rank proof as the
        // dense one, so it agrees on the rank (an observable case is full rank) - no longer -1.
        Assert.Equal(dense.Observability.StateSize, sparse.Observability.StateSize);
        Assert.Equal(dense.Observability.Rank, sparse.Observability.Rank);
        Assert.Equal(dense.Observability.StateSize, sparse.Observability.Rank);
    }

    [Fact]
    public void Sparse_backend_identifies_and_removes_the_same_gross_error()
    {
        GridModel model = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet set = WlsEstimatorTests.ExactFullSet(model, truth);

        var ms = set.Measurements.ToList();
        int victim = ms.FindIndex(m => m.Kind == MeasurementKind.PFlowMw);
        Measurement bad = ms[victim] with { Value = ms[victim].Value + 20 * ms[victim].Sigma };
        ms[victim] = bad;
        set = set with { Measurements = ms };

        EstimationResult dense = new WlsStateEstimator().Estimate(model, set);
        EstimationResult sparse = new WlsStateEstimator().Estimate(model, set, Sparse);

        Assert.Equal(bad, Assert.Single(dense.Removed).Measurement);
        Assert.Equal(bad, Assert.Single(sparse.Removed).Measurement);
        Assert.True(sparse.ChiSquarePassed);
    }

    [Fact]
    public void Neighbour_injection_evaluation_is_bit_identical_to_the_solver_path()
    {
        // MeasurementModel evaluates injections over the electrical neighbourhood only; the
        // skipped terms are exact structural zeros (adding 0.0 is exact), so h must equal the
        // full-scan solver injections BIT FOR BIT - this test pins the no-copy-drift claim.
        GridModel model = IeeeCases.Case118();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet set = SyntheticMeasurements.Sample(model, truth, new SyntheticMeasurementOptions { NoiseScale = 0 });

        var mm = new MeasurementModel(model, set);
        int n = model.BusCount;
        var vm = new double[n];
        var va = new double[n];
        for (int i = 0; i < n; i++)
        {
            vm[i] = truth.Vm[i] * (1.0 + 0.01 * Math.Sin(i + 1)); // off-solution: nothing degenerate
            va[i] = truth.VaDeg[i] * Math.PI / 180.0 + 0.005 * Math.Cos(2 * i + 1);
        }

        var h = new double[mm.MeasurementCount];
        mm.EvaluateH(vm, va, h); // neighbour path

        YBus ybus = YBus.Build(model);
        var g = new double[n, n];
        var b = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) { g[i, j] = ybus.Y[i, j].Real; b[i, j] = ybus.Y[i, j].Imaginary; }
        var p = new double[n];
        var q = new double[n];
        NewtonRaphsonPowerFlow.ComputeInjections(n, g, b, vm, va, p, q); // full-scan solver path

        for (int r = 0; r < mm.MeasurementCount; r++)
        {
            Measurement m = mm.Measurements[r];
            if (m.Kind == MeasurementKind.PInjMw)
                Assert.Equal(p[ybus.IndexOf(m.Bus)], h[r]); // bitwise
            else if (m.Kind == MeasurementKind.QInjMvar)
                Assert.Equal(q[ybus.IndexOf(m.Bus)], h[r]);
        }
    }

    [Fact]
    public void Lean_residuals_skips_covariance_on_a_consistent_fit()
    {
        GridModel model = IeeeCases.Case118();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet set = SyntheticMeasurements.Sample(model, truth);

        EstimationResult lean = new WlsStateEstimator().Estimate(
            model, set, new EstimationOptions { SparseBusThreshold = 0, LeanResiduals = true });
        Assert.True(lean.ChiSquarePassed);
        Assert.All(lean.Residuals, r => Assert.True(double.IsNaN(r.NormalizedResidual)));

        // A gross error still triggers the covariance stage and removal, lean or not.
        var ms = set.Measurements.ToList();
        int victim = ms.FindIndex(m => m.Kind == MeasurementKind.PFlowMw);
        ms[victim] = ms[victim] with { Value = ms[victim].Value + 20 * ms[victim].Sigma };
        EstimationResult bad = new WlsStateEstimator().Estimate(
            model, set with { Measurements = ms },
            new EstimationOptions { SparseBusThreshold = 0, LeanResiduals = true });
        Assert.Single(bad.Removed);
    }

    [Fact]
    public void Measured_injection_prior_recovers_the_truth_operating_point()
    {
        // The triad's solve stage: with exact measured injections, the measured-injection
        // power flow must land on the same operating point the measurements came from, and
        // the estimator warm-started from it converges immediately.
        GridModel model = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet set = SyntheticMeasurements.Sample(model, truth, new SyntheticMeasurementOptions { NoiseScale = 0 });

        PowerFlowResult prior = MeasuredInjectionPrior.Solve(model, set, new NewtonRaphsonPowerFlow());
        Assert.True(prior.Converged);
        for (int i = 0; i < model.BusCount; i++)
            Assert.True(Math.Abs(prior.Vm[i] - truth.Vm[i]) < 0.02,
                $"prior Vm[{i}] {prior.Vm[i]} far from truth {truth.Vm[i]}"); // PV setpoint 1.01 vs truth profile: near, not exact

        MeasurementSet primed = MeasuredInjectionPrior.ApplyVoltagePrior(set, prior);
        EstimationResult est = new WlsStateEstimator().Estimate(
            model, primed, new EstimationOptions { WarmStart = prior });
        Assert.Equal(EstimationStatus.Converged, est.Status);
        for (int i = 0; i < model.BusCount; i++)
            Assert.True(Math.Abs(est.Vm[i] - truth.Vm[i]) < 1e-8); // telemetry pulls the prior to the exact truth
    }

    [Fact]
    public void Measured_injection_prior_keeps_every_unit_at_the_slack_bus()
    {
        // GR-527: a second unit at the slack bus (listed last, Vg 1.02). The prior used to keep only the first unit, so
        // its slack held the first unit's 1.06; it now keeps both and holds the engines' set point (the last unit's).
        GridModel c14 = IeeeCases.Case14();
        int slack = c14.Buses.First(b => b.Type == BusType.Slack).Id;
        GridModel model = c14.CopyWith(generators: c14.Generators.Append(new Generator(slack, 0, Vg: 1.02)).ToArray());
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet set = SyntheticMeasurements.Sample(model, truth, new SyntheticMeasurementOptions { NoiseScale = 0 });
        PowerFlowResult prior = MeasuredInjectionPrior.Solve(model, set, new NewtonRaphsonPowerFlow());
        Assert.True(prior.Converged);
        int si = prior.BusIds.ToList().IndexOf(slack);
        Assert.Equal(truth.Vm[si], prior.Vm[si]);
        Assert.Equal(1.02, prior.Vm[si]);
    }

    [Fact]
    public void Huber_matches_wls_on_clean_data_and_shrugs_off_multiple_gross_errors()
    {
        GridModel model = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet clean = SyntheticMeasurements.Sample(model, truth);

        EstimationResult wls = new WlsStateEstimator().Estimate(model, clean);
        EstimationResult huber = new WlsStateEstimator().Estimate(
            model, clean, new EstimationOptions { Method = EstimationMethod.Huber });
        Assert.Equal(EstimationStatus.Converged, huber.Status);
        for (int i = 0; i < model.BusCount; i++)
            Assert.True(Math.Abs(huber.Vm[i] - wls.Vm[i]) < 1e-3,
                $"clean-data Huber strayed from WLS at bus {i}: {huber.Vm[i]} vs {wls.Vm[i]}");

        // THREE simultaneous gross errors - the scenario sequential LNR handles slowly and
        // robust reweighting is built for. No removals; the errors are down-weighted away.
        var ms = clean.Measurements.ToList();
        int hit = 0;
        for (int i = 0; i < ms.Count && hit < 3; i++)
            if (ms[i].Kind == MeasurementKind.PFlowMw)
            {
                ms[i] = ms[i] with { Value = ms[i].Value + 25 * ms[i].Sigma };
                hit++;
            }
        MeasurementSet corrupted = clean with { Measurements = ms };

        EstimationResult robust = new WlsStateEstimator().Estimate(
            model, corrupted, new EstimationOptions { Method = EstimationMethod.Huber });
        Assert.Equal(EstimationStatus.Converged, robust.Status);
        Assert.Empty(robust.Removed);
        for (int i = 0; i < model.BusCount; i++)
            Assert.True(Math.Abs(robust.Vm[i] - truth.Vm[i]) < 5e-3,
                $"Huber failed to reject 3 gross errors at bus {i}: {robust.Vm[i]} vs truth {truth.Vm[i]}");
    }

    [Fact]
    public void Lav_follows_the_trustworthy_majority()
    {
        GridModel model = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet set = SyntheticMeasurements.Sample(model, truth, new SyntheticMeasurementOptions { NoiseScale = 0 });
        var ms = set.Measurements.ToList();
        int victim = ms.FindIndex(m => m.Kind == MeasurementKind.PInjMw);
        ms[victim] = ms[victim] with { Value = ms[victim].Value + 30 * ms[victim].Sigma };

        EstimationResult lav = new WlsStateEstimator().Estimate(
            model, set with { Measurements = ms },
            new EstimationOptions { Method = EstimationMethod.Lav });
        Assert.Equal(EstimationStatus.Converged, lav.Status);
        for (int i = 0; i < model.BusCount; i++)
            Assert.True(Math.Abs(lav.Vm[i] - truth.Vm[i]) < 1e-3,
                $"LAV pulled toward the outlier at bus {i}");
    }

    [Fact]
    // GR-406: renamed from Constrained_method_honours_zero_injections_exactly: the name said exactly; the assertion is 1e-5. Tightening waits for a measured value.
    public void Constrained_method_honours_zero_injections_within_1e_5()
    {
        // case14 bus 7 is a pure switching node (no load, no generation). Replace its soft
        // injection measurements with virtual-zero-injection rows: the constrained method must
        // satisfy them EXACTLY (KKT equality), where soft WLS only gets within sigma.
        GridModel model = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet set = SyntheticMeasurements.Sample(model, truth);

        var ms = set.Measurements
            .Where(m => !(m.Bus == 7 && m.Kind is MeasurementKind.PInjMw or MeasurementKind.QInjMvar))
            .ToList();
        ms.Add(new Measurement(MeasurementKind.PInjMw, 0.0, 10.0, Bus: 7, Source: "virtual-zero-injection"));
        ms.Add(new Measurement(MeasurementKind.QInjMvar, 0.0, 20.0, Bus: 7, Source: "virtual-zero-injection"));
        MeasurementSet withVirtuals = set with { Measurements = ms };

        EstimationResult soft = new WlsStateEstimator().Estimate(model, withVirtuals);
        EstimationResult hard = new WlsStateEstimator().Estimate(
            model, withVirtuals, new EstimationOptions { Method = EstimationMethod.Constrained });

        Assert.Equal(EstimationStatus.Converged, hard.Status);
        // The constraint is satisfied exactly (linearized residual at convergence ~ solver tol).
        foreach (MeasurementResidual r in hard.Residuals)
            if (string.Equals(r.Measurement.Source, "virtual-zero-injection", StringComparison.Ordinal))
            {
                Assert.True(Math.Abs(r.Residual) < 1e-5,
                    $"constraint violated by {r.Residual} MW/MVAr");
                Assert.True(r.IsCritical); // a network fact is never removable telemetry
            }

        // The states agree with the soft solution closely (the virtuals were already near-binding).
        for (int i = 0; i < model.BusCount; i++)
            Assert.True(Math.Abs(hard.Vm[i] - soft.Vm[i]) < 5e-3,
                $"constrained strayed from soft WLS at bus {i}");

        // A gross error elsewhere is still identified and removed - and never a constraint row.
        var bad = withVirtuals.Measurements.ToList();
        int victim = bad.FindIndex(m => m.Kind == MeasurementKind.PFlowMw);
        bad[victim] = bad[victim] with { Value = bad[victim].Value + 20 * bad[victim].Sigma };
        EstimationResult est = new WlsStateEstimator().Estimate(
            model, withVirtuals with { Measurements = bad },
            new EstimationOptions { Method = EstimationMethod.Constrained });
        Assert.Equal(EstimationStatus.Converged, est.Status);
        Assert.All(est.Removed, rem =>
            Assert.NotEqual("virtual-zero-injection", rem.Measurement.Source));
        Assert.Single(est.Removed);
    }

    [Fact]
    public void Sparse_backend_reports_unmeasured_islands()
    {
        var model = new GridModel
        {
            BaseMva = 100,
            Name = "two-islands",
            Buses = new[]
            {
                new Bus(1, BusType.Slack),
                new Bus(2, BusType.Pq, Pd: 40),
                new Bus(3, BusType.Slack),
                new Bus(4, BusType.Pq, Pd: 25),
            },
            Generators = new[] { new Generator(1, 40), new Generator(3, 25) },
            Branches = new[]
            {
                new Branch(1, 2, 0.01, 0.1),
                new Branch(3, 4, 0.01, 0.1),
            },
        };
        var set = new MeasurementSet
        {
            Case = model.Name,
            Measurements = new[]
            {
                new Measurement(MeasurementKind.VmPu, 1.0, 0.004, Bus: 1),
                new Measurement(MeasurementKind.PInjMw, -40, 1.0, Bus: 2),
                new Measurement(MeasurementKind.QInjMvar, 0, 1.0, Bus: 2),
            },
        };

        EstimationResult est = new WlsStateEstimator().Estimate(model, set, Sparse);
        Assert.Equal(EstimationStatus.Unobservable, est.Status);
        Assert.Equal(new[] { 3, 4 }, est.Observability.UnmeasuredIslandBuses);
    }
}
