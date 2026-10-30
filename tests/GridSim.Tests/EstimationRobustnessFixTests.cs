using GridSim.Core.Estimation;
using GridSim.Core.Estimation.Dse;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// WS6 estimation fixes: R256 (zero / negative dt in the dynamic estimators), R257 (parallel-circuit
/// active-set rows in the Feasible KKT), R258 (observability island sweep through open branches) and
/// R260 (SchweppeGm leverage computed with the declared weights).
/// </summary>
public class EstimationRobustnessFixTests
{
    // ------------------------------------------------------------------ R256

    [Fact]
    public void Random_walk_with_zero_dt_adds_no_process_noise()
    {
        var model = new RandomWalkProcessModel(angleCount: 2);
        var x = new double[] { 0.1, 0.2, 1.0 };
        var f = new double[3, 3];
        var q = new double[3, 3];
        model.Predict(x, 0.0, f, q);
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                Assert.Equal(i == j ? 1.0 : 0.0, f[i, j]);
                Assert.Equal(0.0, q[i, j]);   // used to be a full second of drift
            }
    }

    [Fact]
    public void Random_walk_with_a_positive_dt_is_unchanged()
    {
        var model = new RandomWalkProcessModel(angleCount: 1, angleProcessStdPerSqrtSec: 0.05, vmProcessStdPerSqrtSec: 0.01);
        var f = new double[2, 2];
        var q = new double[2, 2];
        model.Predict(new double[2], 2.5, f, q);
        Assert.Equal(0.05 * 0.05 * 2.5, q[0, 0]);
        Assert.Equal(0.01 * 0.01 * 2.5, q[1, 1]);
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(-1e-9)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Random_walk_rejects_an_out_of_order_or_non_finite_dt(double dt)
    {
        var model = new RandomWalkProcessModel(angleCount: 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => model.Predict(new double[2], dt, new double[2, 2], new double[2, 2]));
    }

    [Fact]
    public void Frequency_filter_with_zero_dt_is_a_pure_update()
    {
        // Two filters see the same history; one also sees a duplicate of the last sample at dt = 0. With zero
        // process noise and F = I the duplicate is a plain second observation, so the frequency stays put
        // to within the measurement and never drifts by a second of RoCoF.
        var a = new FrequencyRocofFilter(initialFreqHz: 50.0);
        FrequencyRocofEstimate e = null!;
        for (int k = 1; k <= 50; k++) e = a.Step(50.0 - 0.1 * k * 0.02, sigmaHz: 0.001, dtSeconds: 0.02);
        double before = e.FreqHz;
        FrequencyRocofEstimate dup = a.Step(50.0 - 0.1 * 50 * 0.02, sigmaHz: 0.001, dtSeconds: 0.0);
        Assert.True(Math.Abs(dup.FreqHz - before) < 1e-3, $"duplicate sample moved the frequency from {before} to {dup.FreqHz}");
    }

    [Theory]
    [InlineData(-0.02)]
    [InlineData(double.NaN)]
    public void Frequency_filter_rejects_an_out_of_order_or_non_finite_dt(double dt)
    {
        var a = new FrequencyRocofFilter(initialFreqHz: 50.0);
        Assert.Throws<ArgumentOutOfRangeException>(() => a.Step(50.0, 0.001, dt));
    }

    // ------------------------------------------------------------------ R257

    /// <summary>Case 14 with a second, identical circuit in parallel with branch from-to (both named so flow
    /// measurements can address each), its solved truth, an exact full measurement set and its model.</summary>
    private static (GridModel Model, PowerFlowResult Truth, MeasurementModel Mm, double[] W) Case14WithParallel(int from, int to)
    {
        GridModel m = IeeeCases.Case14();
        var branches = new List<Branch>();
        foreach (Branch b in m.Branches)
        {
            if (b.From == from && b.To == to)
            {
                branches.Add(b with { Name = $"{from}-{to} a" });
                branches.Add(b with { Name = $"{from}-{to} b" });
            }
            else branches.Add(b);
        }
        GridModel par = m.CopyWith(branches: branches);
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(par);
        Assert.True(truth.Converged);
        // Sigmas widened 50x: with the standard sigmas the free gain reaches ~1e7 and ANY active inequality row
        // (even a single one, parallel circuits or not) falls under the solver's relative pivot floor - a
        // separate, pre-existing KKT scaling defect recorded in NOTES-FOR-MAIN (R257 section). Wide sigmas keep
        // the gain small so this test isolates the parallel-circuit duplicate rows.
        MeasurementSet exact = WlsEstimatorTests.ExactFullSet(par, truth);
        var mm = new MeasurementModel(par, exact with { Measurements = exact.Measurements.Select(x => x with { Sigma = x.Sigma * 50.0 }).ToList() });
        var w = new double[mm.MeasurementCount];
        for (int r = 0; r < w.Length; r++) w[r] = 1.0 / (mm.SigmaPu[r] * mm.SigmaPu[r]);
        return (par, truth, mm, w);
    }

    private static (double[] Vm, double[] Va) TruthState(PowerFlowResult truth) =>
        (truth.Vm.ToArray(), truth.VaDeg.Select(d => d * Math.PI / 180.0).ToArray());

    [Fact]
    public void Feasible_step_enforces_one_angle_row_across_parallel_circuits()
    {
        // Both parallel circuits used to add an identical +1/-1 row when the angle cap was hit: an exactly
        // singular KKT, TrySolve false, and the estimate reported as Unobservable. One row per bus pair now.
        // Case 14 bus 8 hangs off bus 7 through branch 7-8 alone, so doubling 7-8 and evaluating at a flat state
        // with only bus 8 rotated activates the angle cap on exactly the parallel pair and nothing else
        // (several active rows around a loop would be a different, genuine dependency).
        (GridModel m, PowerFlowResult truth, MeasurementModel mm, double[] w) = Case14WithParallel(7, 8);
        int i2 = truth.BusIds.ToList().IndexOf(7), i4 = truth.BusIds.ToList().IndexOf(8);
        double[] vm = Enumerable.Repeat(1.0, m.BusCount).ToArray();
        double[] va = new double[m.BusCount];
        va[i4] = -0.3;
        double diff = va[i2] - va[i4];
        double cap = 0.1;

        var step = new FeasibleWlsStep(new DenseLuSolver(),
            new FeasibilityConstraints { AngleDiffMaxRad = cap, EnforceGeneratorQ = false });
        step.Begin(m, mm, w);
        var dx = new double[mm.StateSize];
        Assert.True(step.TrySolve(vm, va, dx), "the KKT with the parallel pair's angle constraint must be solvable");

        // The single linearised row holds the difference exactly on the cap: (theta1 + d1) - (theta5 + d5) = cap.
        double d2 = mm.AngleColumn(i2) >= 0 ? dx[mm.AngleColumn(i2)] : 0.0;
        double d4 = mm.AngleColumn(i4) >= 0 ? dx[mm.AngleColumn(i4)] : 0.0;
        Assert.Equal(Math.Sign(diff) * cap, diff + d2 - d4, 9);
    }

    [Fact]
    public void Feasible_step_enforces_one_thermal_row_per_parallel_group()
    {
        // Identical parallel circuits over rating used to add two identical S^2 rows: a singular KKT.
        (GridModel m, PowerFlowResult truth, MeasurementModel mm, double[] w) = Case14WithParallel(2, 4);
        static double S(LineFlow lf) => Math.Sqrt(lf.PFromMw * lf.PFromMw + lf.QFromMvar * lf.QFromMvar);
        double sTrue = truth.LineFlows.Where(l => l.From == 2 && l.To == 4).Max(S);
        GridModel tight = m.CopyWith(branches: m.Branches
            .Select(b => b.From == 2 && b.To == 4 ? b with { RateAMva = sTrue * 0.7 } : b).ToList());
        (double[] vm, double[] va) = TruthState(truth);

        var step = new FeasibleWlsStep(new DenseLuSolver(),
            new FeasibilityConstraints { EnforceBranchRatings = true, EnforceGeneratorQ = false });
        step.Begin(tight, mm, w);
        var dx = new double[mm.StateSize];
        Assert.True(step.TrySolve(vm, va, dx), "the KKT with the parallel pair's thermal constraint must be solvable");
        Assert.All(dx, v => Assert.True(double.IsFinite(v)));
    }

    [Fact]
    public void A_degenerate_constraint_block_with_observable_measurements_is_infeasible_not_unobservable()
    {
        // Two identical hard zero-injection rows at bus 7 make the KKT constraint block rank-deficient while
        // the full telemetry still observes every state. That is a constraint degeneracy, reported as
        // Infeasible with an observable report - not the old false "Unobservable".
        GridModel m = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { EnforceQLimits = true });
        MeasurementSet full = WlsEstimatorTests.ExactFullSet(m, truth);
        var ms = full.Measurements.ToList();
        ms.Add(new Measurement(MeasurementKind.PInjMw, 0.0, 1.0, Bus: 7, Source: "virtual-zero-injection"));
        ms.Add(new Measurement(MeasurementKind.PInjMw, 0.0, 1.0, Bus: 7, Source: "virtual-zero-injection"));
        EstimationResult est = new WlsStateEstimator().Estimate(m, full with { Measurements = ms },
            new EstimationOptions { Method = EstimationMethod.Feasible });
        Assert.Equal(EstimationStatus.Infeasible, est.Status);
        Assert.False(est.Converged);
        Assert.True(est.Observability.Observable);
    }

    // ------------------------------------------------------------------ R258

    [Fact]
    public void An_island_isolated_by_an_open_branch_is_reported_unmeasured()
    {
        // Case 14 bus 8 hangs off bus 7 through branch 7-8 alone. Open it and leave bus 8 unmeasured: the
        // island sweep used to join bus 8 to the main component through the OPEN branch.
        GridModel m = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(m);
        MeasurementSet full = WlsEstimatorTests.ExactFullSet(m, truth);
        var ms = full.Measurements.Where(x => x.Bus != 8 && x.From != 8 && x.To != 8).ToList();
        MeasurementSet set = full with { Measurements = ms };

        GridModel open = m.CopyWith(branches: m.Branches
            .Select(b => (b.From == 7 && b.To == 8) || (b.From == 8 && b.To == 7) ? b with { InService = false } : b).ToList());
        ObservabilityReport openReport = ObservabilityAnalysis.AnalyzeTopology(open, new MeasurementModel(open, set));
        Assert.False(openReport.Observable);
        Assert.Equal(new[] { 8 }, openReport.UnmeasuredIslandBuses);

        // Control: with the branch closed, bus 8 is part of the measured main component.
        ObservabilityReport closedReport = ObservabilityAnalysis.AnalyzeTopology(m, new MeasurementModel(m, set));
        Assert.Empty(closedReport.UnmeasuredIslandBuses);
    }

    // ------------------------------------------------------------------ R260

    [Fact]
    public void SchweppeGm_converges_to_the_huber_fixed_point_on_declared_weight_leverage()
    {
        // At the SchweppeGm solution x*, the IRLS weights must be Huber factors of the leverage-adjusted
        // residual computed with the DECLARED weights: f_r = min(1, k / r_S,r). So one Gauss-Newton step
        // from x* with weights w * f must be (near) zero - x* is that weighted problem's fixed point. With
        // the R260 defect the factors were computed against the already down-weighted weights and settled
        // at (k sigma/|r|)^(2/3), so this step was a sizeable fraction of the plain-WLS step.
        GridModel model = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet clean = WlsEstimatorTests.ExactFullSet(model, truth);
        var corrupted = clean.Measurements.ToList();
        int bad = corrupted.FindIndex(x => x.Kind == MeasurementKind.PFlowMw);
        corrupted[bad] = corrupted[bad] with { Value = corrupted[bad].Value + 25.0 };   // ~16 sigma gross error
        MeasurementSet set = clean with { Measurements = corrupted };

        var opt = new EstimationOptions { Method = EstimationMethod.SchweppeGm, DetectBadData = false };
        EstimationResult gm = new WlsStateEstimator().Estimate(model, set, opt);
        Assert.Equal(EstimationStatus.Converged, gm.Status);

        var mm = new MeasurementModel(model, set);
        int m = mm.MeasurementCount, s = mm.StateSize;
        var w = new double[m];
        for (int r = 0; r < m; r++) w[r] = 1.0 / (mm.SigmaPu[r] * mm.SigmaPu[r]);
        double[] vm = gm.Vm.ToArray();
        double[] va = gm.VaDeg.Select(d => d * Math.PI / 180.0).ToArray();

        var h = new double[m];
        mm.EvaluateH(vm, va, h);
        var res = new double[m];
        for (int r = 0; r < m; r++) res[r] = mm.ZPu[r] - h[r];
        var declared = new DenseWlsStep(new DenseLuSolver());
        declared.Begin(model, mm, w);
        (double[] rn, bool[] crit) = declared.NormalizedResiduals(vm, va, res);
        var wf = new double[m];
        for (int r = 0; r < m; r++)
        {
            double fac = crit[r] || !double.IsFinite(rn[r]) ? 1.0 : rn[r] <= opt.HuberK ? 1.0 : opt.HuberK / rn[r];
            wf[r] = w[r] * fac;
        }
        Assert.True(wf[bad] < w[bad], "the gross error must be down-weighted");

        double MaxStep(double[] weights)
        {
            var step = new DenseWlsStep(new DenseLuSolver());
            step.Begin(model, mm, weights);
            var dx = new double[s];
            Assert.True(step.TrySolve(vm, va, dx));
            return dx.Max(Math.Abs);
        }
        double huberStep = MaxStep(wf);
        double wlsStep = MaxStep(w);
        Assert.True(huberStep <= 1e-2 * wlsStep,
            $"not at the declared-weight Huber fixed point: step {huberStep:E3} vs plain-WLS step {wlsStep:E3}");
    }
}
