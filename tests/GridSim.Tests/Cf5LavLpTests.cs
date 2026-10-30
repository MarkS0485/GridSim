using System;
using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// CF5 (N331): least absolute values solved exactly. <see cref="LavLinearProgram"/> on problems whose L1 optimum is known
/// by hand, cross-checked against the project's general LP solver (<see cref="BoundedSimplex"/>) on a real linearised
/// estimation problem, and <see cref="EstimationMethod.LavLp"/> end to end (convergence without any IRLS cap, the
/// clean-data objective the probe measured, a gross error rejected exactly). <see cref="EstimationMethod.Lav"/> (IRLS)
/// is untouched: its own tests pin it.
/// </summary>
public class Cf5LavLpTests
{
    private static IReadOnlyList<(int Col, double Val)>[] Rows(params (int, double)[][] rows) =>
        rows.Select(r => (IReadOnlyList<(int Col, double Val)>)r.ToList()).ToArray();

    // ------------------------------------------------------------------ hand references

    [Fact]
    public void One_state_with_equal_weights_is_the_median_and_its_objective()
    {
        // min |1 - a| + |2 - a| + |3 - a| + |10 - a|: every a in [2, 3] is optimal with objective 10; a vertex is 2 or 3.
        var rows = Rows(new[] { (0, 1.0) }, new[] { (0, 1.0) }, new[] { (0, 1.0) }, new[] { (0, 1.0) });
        LavLpSolution sol = LavLinearProgram.Solve(rows, new[] { 1.0, 2.0, 3.0, 10.0 }, new[] { 1.0, 1.0, 1.0, 1.0 }, 1);
        Assert.Equal(LavLpStatus.Optimal, sol.Status);
        Assert.True(sol.Dx[0] == 2.0 || sol.Dx[0] == 3.0, $"a = {sol.Dx[0]:R}, expected a vertex 2 or 3");
        Assert.Equal(10.0, sol.Objective);
    }

    [Fact]
    public void One_state_with_unequal_weights_is_the_weighted_median()
    {
        // Weights 1, 1, 1, 5: the heavy row holds more than half the total weight (5 of 8), so a = 10 exactly,
        // objective 9 + 8 + 7 + 0 = 24.
        var rows = Rows(new[] { (0, 1.0) }, new[] { (0, 1.0) }, new[] { (0, 1.0) }, new[] { (0, 1.0) });
        LavLpSolution sol = LavLinearProgram.Solve(rows, new[] { 1.0, 2.0, 3.0, 10.0 }, new[] { 1.0, 1.0, 1.0, 5.0 }, 1);
        Assert.Equal(LavLpStatus.Optimal, sol.Status);
        Assert.Equal(10.0, sol.Dx[0]);
        Assert.Equal(24.0, sol.Objective);
    }

    [Fact]
    public void A_line_through_four_collinear_points_ignores_the_outlier_exactly()
    {
        // y = a + b t at t = 0..4 with y = 0, 1, 2, 3, 100. The L1 fit passes through the four collinear points
        // (a = 0, b = 1) and pays the whole outlier: objective 100 - 4 = 96. WLS would tilt the line towards 100.
        var rows = Rows(
            new[] { (0, 1.0) },
            new[] { (0, 1.0), (1, 1.0) },
            new[] { (0, 1.0), (1, 2.0) },
            new[] { (0, 1.0), (1, 3.0) },
            new[] { (0, 1.0), (1, 4.0) });
        LavLpSolution sol = LavLinearProgram.Solve(rows, new[] { 0.0, 1.0, 2.0, 3.0, 100.0 }, new[] { 1.0, 1.0, 1.0, 1.0, 1.0 }, 2);
        Assert.Equal(LavLpStatus.Optimal, sol.Status);
        Assert.True(Math.Abs(sol.Dx[0]) <= 1e-14, $"a = {sol.Dx[0]:R}");
        Assert.True(Math.Abs(sol.Dx[1] - 1.0) <= 1e-14, $"b = {sol.Dx[1]:R}");
        Assert.True(Math.Abs(sol.Objective - 96.0) <= 1e-12, $"objective {sol.Objective:R}");
        Assert.DoesNotContain(4, sol.Basis);   // the outlier is never interpolated
    }

    [Fact]
    public void The_solve_is_deterministic_bit_for_bit_and_a_warm_start_reaches_the_same_optimum()
    {
        var rows = Rows(
            new[] { (0, 1.0) }, new[] { (0, 1.0), (1, 1.0) }, new[] { (0, 1.0), (1, 2.0) },
            new[] { (0, 1.0), (1, 3.0) }, new[] { (0, 1.0), (1, 4.0) }, new[] { (1, 1.0) });
        double[] r = { 0.1, 1.3, 1.9, 3.2, 3.8, 0.7 };
        double[] w = { 1.0, 2.0, 1.0, 3.0, 1.0, 0.5 };
        LavLpSolution a = LavLinearProgram.Solve(rows, r, w, 2);
        LavLpSolution b = LavLinearProgram.Solve(rows, (double[])r.Clone(), (double[])w.Clone(), 2);
        Assert.Equal(LavLpStatus.Optimal, a.Status);
        Assert.Equal(BitConverter.DoubleToInt64Bits(a.Objective), BitConverter.DoubleToInt64Bits(b.Objective));
        for (int j = 0; j < 2; j++) Assert.Equal(BitConverter.DoubleToInt64Bits(a.Dx[j]), BitConverter.DoubleToInt64Bits(b.Dx[j]));
        Assert.Equal(a.Basis, b.Basis);
        // Warm-started from its own optimum: no move, same vertex.
        LavLpSolution warm = LavLinearProgram.Solve(rows, r, w, 2, a.Basis);
        Assert.Equal(0, warm.Pivots);
        Assert.Equal(a.Objective, warm.Objective);
    }

    [Fact]
    public void The_solve_refuses_malformed_input_loudly()
    {
        var one = Rows(new[] { (0, 1.0) }, new[] { (0, 1.0) });
        Assert.Throws<ArgumentNullException>(() => LavLinearProgram.Solve(null!, new[] { 1.0, 2.0 }, new[] { 1.0, 1.0 }, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => LavLinearProgram.Solve(one, new[] { 1.0, 2.0 }, new[] { 1.0, 1.0 }, 0));
        Assert.Throws<ArgumentException>(() => LavLinearProgram.Solve(one, new[] { 1.0 }, new[] { 1.0, 1.0 }, 1));
        Assert.Throws<ArgumentException>(() => LavLinearProgram.Solve(one, new[] { 1.0, 2.0 }, new[] { 1.0, 0.0 }, 1));
        Assert.Throws<ArgumentException>(() => LavLinearProgram.Solve(one, new[] { 1.0, double.NaN }, new[] { 1.0, 1.0 }, 1));
        Assert.Throws<ArgumentException>(() => LavLinearProgram.Solve(Rows(new[] { (1, 1.0) }, new[] { (0, 1.0) }), new[] { 1.0, 2.0 }, new[] { 1.0, 1.0 }, 1));
        Assert.Throws<ArgumentException>(() => LavLinearProgram.Solve(Rows(new[] { (0, 1.0), (0, 2.0) }, new[] { (0, 1.0) }), new[] { 1.0, 2.0 }, new[] { 1.0, 1.0 }, 1));
        Assert.Throws<ArgumentException>(() => LavLinearProgram.Solve(Rows(new[] { (0, 1.0) }), new[] { 1.0 }, new[] { 1.0 }, 2));
    }

    [Fact]
    public void Rows_that_cannot_determine_the_state_are_reported_singular_not_solved()
    {
        // Two states, three rows that all measure only the first: no basis exists.
        var rows = Rows(new[] { (0, 1.0) }, new[] { (0, 2.0) }, new[] { (0, 3.0) });
        LavLpSolution sol = LavLinearProgram.Solve(rows, new[] { 1.0, 2.0, 3.0 }, new[] { 1.0, 1.0, 1.0 }, 2);
        Assert.Equal(LavLpStatus.Singular, sol.Status);
    }

    // ------------------------------------------------------------------ cross-check against BoundedSimplex

    [Fact]
    public void On_a_real_linearised_estimation_problem_the_l1_simplex_and_the_general_lp_agree()
    {
        // case14, seed 1, at flat start (a far-from-optimal linearisation, so the LP is not trivial). The same LP posed to
        // BoundedSimplex in standard form: columns dx+ (s), dx- (s), e+ (m), e- (m); H dx+ - H dx- + e+ - e- = r;
        // minimise w.(e+ + e-) with the costs scaled to at most 1. Two independent solvers, one optimum.
        GridModel model = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet set = SyntheticMeasurements.Sample(model, truth, new SyntheticMeasurementOptions { Seed = 1 });
        var mm = new MeasurementModel(model, set);
        int m = mm.MeasurementCount, s = mm.StateSize, n = model.BusCount;
        double[] vm = Enumerable.Repeat(1.0, n).ToArray();
        double[] va = new double[n];
        var h = new double[m];
        mm.EvaluateH(vm, va, h);
        var jac = new SparseJacobianRows(m);
        mm.EvaluateJacobian(vm, va, jac);
        double[] r = Enumerable.Range(0, m).Select(i => mm.ZPu[i] - h[i]).ToArray();
        double[] w = Enumerable.Range(0, m).Select(i => 1.0 / mm.SigmaPu[i]).ToArray();

        LavLpSolution mine = LavLinearProgram.Solve(jac.Rows, r, w, s);
        Assert.Equal(LavLpStatus.Optimal, mine.Status);

        int cols = 2 * s + 2 * m;
        var a = new double[m, cols];
        var c = new double[cols];
        var lo = new double[cols];
        var hi = new double[cols];
        Array.Fill(hi, double.PositiveInfinity);
        double box = 10.0;   // closes the zero-cost ray (dx+ and dx- raised together); far above this step
        for (int j = 0; j < 2 * s; j++) hi[j] = box;
        double wMax = w.Max();
        for (int i = 0; i < m; i++)
        {
            foreach ((int col, double val) in jac.Rows[i]) { a[i, col] = val; a[i, s + col] = -val; }
            a[i, 2 * s + i] = 1.0;
            a[i, 2 * s + m + i] = -1.0;
            c[2 * s + i] = w[i] / wMax;
            c[2 * s + m + i] = w[i] / wMax;
        }
        Assert.True(mine.Dx.All(x => Math.Abs(x) < box), "the L1 step must sit inside the reference LP's box for the comparison to hold");
        (LpStatus status, double[] x, double objective) = BoundedSimplex.Solve(a, r, c, lo, hi,
            pricingTieMargin: BoundedSimplex.NearTiePricingMargin);
        Assert.Equal(LpStatus.Optimal, status);
        double reference = objective * wMax;
        Assert.True(Math.Abs(mine.Objective - reference) <= 1e-8 * reference,
            $"L1 simplex objective {mine.Objective:R}, BoundedSimplex {reference:R}");
        // The objective at the reference point, recomputed, equals the reference objective (sanity of the comparison).
        double[] dxRef = Enumerable.Range(0, s).Select(j => x[j] - x[s + j]).ToArray();
        Assert.True(Math.Abs(LavLinearProgram.Objective(jac.Rows, r, w, dxRef) - reference) <= 1e-8 * reference);
    }

    // ------------------------------------------------------------------ the estimator method

    private static (GridModel Model, PowerFlowResult Truth, MeasurementSet Set) Case14(int seed)
    {
        GridModel model = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(truth.Converged);
        return (model, truth, SyntheticMeasurements.Sample(model, truth, new SyntheticMeasurementOptions { Seed = seed }));
    }

    [Theory]
    // J at the converged LAV-LP state, as `estimate case14 --synth --seed N --method lav-lp` printed it in the CF5 probe
    // (2026-10-11, Debug build); the trial's BoundedSimplex-based successive LP printed the same three values.
    [InlineData(1, 111.66)]
    [InlineData(2, 87.62)]
    [InlineData(3, 89.79)]
    public void Lav_lp_converges_on_clean_case14_with_no_irls_cap(int seed, double jPrinted)
    {
        (GridModel model, _, MeasurementSet set) = Case14(seed);
        EstimationResult lp = new WlsStateEstimator().Estimate(model, set, new EstimationOptions { Method = EstimationMethod.LavLp });
        Assert.Equal(EstimationStatus.Converged, lp.Status);
        Assert.False(lp.IrlsCapReached);
        Assert.True(lp.ChiSquarePassed);
        Assert.InRange(lp.ObjectiveJ, jPrinted - 0.005, jPrinted + 0.005);
    }

    [Fact]
    public void Lav_lp_is_deterministic_bit_for_bit()
    {
        (GridModel model, _, MeasurementSet set) = Case14(2);
        var opt = new EstimationOptions { Method = EstimationMethod.LavLp };
        EstimationResult a = new WlsStateEstimator().Estimate(model, set, opt);
        EstimationResult b = new WlsStateEstimator().Estimate(model, set, opt);
        for (int i = 0; i < model.BusCount; i++)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.Vm[i]), BitConverter.DoubleToInt64Bits(b.Vm[i]));
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.VaDeg[i]), BitConverter.DoubleToInt64Bits(b.VaDeg[i]));
        }
    }

    [Fact]
    public void Lav_lp_rejects_a_gross_error_exactly_on_an_exact_set()
    {
        // The Q13 / N064 set-up (exact case14 set, one P injection pushed 30 sigma). IRLS LAV hits its cap here. The exact
        // LAV optimum interpolates s good rows and every other good row is consistent with them, so the state is the
        // truth to round-off and the bad row carries the whole 30 sigma.
        (GridModel model, PowerFlowResult truth, _) = Case14(1);
        MeasurementSet exact = SyntheticMeasurements.Sample(model, truth, new SyntheticMeasurementOptions { NoiseScale = 0 });
        var ms = exact.Measurements.ToList();
        int victim = ms.FindIndex(x => x.Kind == MeasurementKind.PInjMw);
        ms[victim] = ms[victim] with { Value = ms[victim].Value + 30 * ms[victim].Sigma };
        MeasurementSet bad = exact with { Measurements = ms };

        EstimationResult lp = new WlsStateEstimator().Estimate(model, bad, new EstimationOptions { Method = EstimationMethod.LavLp });
        Assert.Equal(EstimationStatus.Converged, lp.Status);
        Assert.False(lp.IrlsCapReached);
        for (int i = 0; i < model.BusCount; i++)
        {
            Assert.True(Math.Abs(lp.Vm[i] - truth.Vm[i]) < 1e-6, $"bus {i}: Vm {lp.Vm[i]:R} vs truth {truth.Vm[i]:R}");
            Assert.True(Math.Abs(lp.VaDeg[i] - truth.VaDeg[i]) < 1e-4, $"bus {i}: Va {lp.VaDeg[i]:R} vs truth {truth.VaDeg[i]:R}");
        }
        // WLS without bad-data removal smears the error over the state; LAV-LP does not.
        EstimationResult wls = new WlsStateEstimator().Estimate(model, bad, new EstimationOptions { DetectBadData = false });
        double wlsDev = Enumerable.Range(0, model.BusCount).Max(i => Math.Abs(wls.Vm[i] - truth.Vm[i]) + Math.Abs(wls.VaDeg[i] - truth.VaDeg[i]));
        double lpDev = Enumerable.Range(0, model.BusCount).Max(i => Math.Abs(lp.Vm[i] - truth.Vm[i]) + Math.Abs(lp.VaDeg[i] - truth.VaDeg[i]));
        Assert.True(lpDev < wlsDev, $"LAV-LP deviation {lpDev:R} should be below WLS's {wlsDev:R}");
    }

    [Fact]
    public void Hti_refuses_lav_lp_like_the_other_robust_methods()
    {
        (GridModel model, _, MeasurementSet set) = Case14(1);
        Assert.Throws<NotSupportedException>(() =>
            HtiBadDataEstimation.Estimate(new WlsStateEstimator(), model, set, new EstimationOptions { Method = EstimationMethod.LavLp }));
    }

    [Fact]
    public void Irls_lav_is_documented_as_approximate_in_code_and_output()
    {
        string root = RepoRoot();
        string core = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src", "GridSim.Core", "Estimation", "WlsStateEstimator.cs"));
        Assert.Contains("APPROXIMATE (N331, CF5)", core);
        string cli = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src", "GridSim.Cli", "Program.cs"));
        // Moved 2026-10 (END-PASS CF6 row 4, end pass; N356, `--method lav` is the exact LP form and the IRLS form is
        // `--method lav-irls`, Ruling R1 and Mark's go on the end-pass moves 2026-10-11): the IRLS form is still documented
        // as approximate in the output, under its new name, so the check follows it. Before -> after: "NOTE: --method lav
        // is LAV by IRLS, an approximation" -> "NOTE: --method lav-irls is LAV by IRLS, an approximation" (observed: the old
        // text not found, 1.95.273-275 runs; the new text read from Program.cs, the source this test reads).
        Assert.Contains("NOTE: --method lav-irls is LAV by IRLS, an approximation", cli);
        Assert.Contains("\"lav-lp\" => EstimationMethod.LavLp", cli);
    }

    private static string RepoRoot()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("GridSim.slnx not found above the test binaries.");
    }
}
