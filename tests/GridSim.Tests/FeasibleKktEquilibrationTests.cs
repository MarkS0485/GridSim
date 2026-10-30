using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R257 follow-up (ruling Q12, 2026-10-02): the Feasible KKT row-equilibrates its constraint block and the
/// angle-difference rows carry a hysteresis band. These tests pin:
///   - with the STANDARD sigmas (free gain ~1e7), case14 at its true state with one active angle row is
///     solvable (it was declared singular: the constraint pivot fell under the relative pivot floor), and the
///     linearised row lands the difference exactly on the cap;
///   - the angle hysteresis never pushes an under-limit difference up: a difference inside the band keeps its
///     row with a zero right-hand side, so the step holds it where it is;
///   - case57 and case118 with synthetic telemetry no longer fail at iteration 0 on the generator-Q rows.
/// </summary>
public class FeasibleKktEquilibrationTests
{
    private static (GridModel M, PowerFlowResult Truth, MeasurementModel Mm, double[] W) Case14Exact()
    {
        GridModel m = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { EnforceQLimits = true });
        Assert.True(truth.Converged);
        MeasurementSet set = WlsEstimatorTests.ExactFullSet(m, truth);   // standard sigmas: gain ~1e7
        var mm = new MeasurementModel(m, set);
        var w = new double[mm.MeasurementCount];
        for (int r = 0; r < w.Length; r++) w[r] = 1.0 / (mm.SigmaPu[r] * mm.SigmaPu[r]);
        return (m, truth, mm, w);
    }

    private static (double[] Vm, double[] Va) TruthState(PowerFlowResult truth) =>
        (truth.Vm.ToArray(), truth.VaDeg.Select(d => d * Math.PI / 180.0).ToArray());

    [Fact]
    public void One_active_angle_row_at_the_true_state_is_solvable_with_standard_sigmas()
    {
        (GridModel m, PowerFlowResult truth, MeasurementModel mm, double[] w) = Case14Exact();
        (double[] vm, double[] va) = TruthState(truth);

        // Pick the branch with the largest true angle difference and cap just below it, above every other
        // branch's difference and its hysteresis trigger, so exactly ONE angle row is active.
        List<int> ids = truth.BusIds.ToList();
        int bestF = -1, bestT = -1;
        double best = 0.0, second = 0.0;
        foreach (Branch br in m.Branches)
        {
            int f = ids.IndexOf(br.From), t = ids.IndexOf(br.To);
            double a = Math.Abs(va[f] - va[t]);
            if (a > best) { second = best; best = a; bestF = f; bestT = t; }
            else if (a > second) second = a;
        }
        double cap = 0.5 * (best + second);
        Assert.True(second < cap * (1.0 - FeasibleWlsStep.AngleHysteresisFraction), "need a single active angle row");
        int iF = bestF, iT = bestT;
        double diff = va[iF] - va[iT];

        var step = new FeasibleWlsStep(new GridSim.Core.PowerFlow.DenseLuSolver(),
            new FeasibilityConstraints { AngleDiffMaxRad = cap, EnforceGeneratorQ = false });
        step.Begin(m, mm, w);
        var dx = new double[mm.StateSize];
        Assert.True(step.TrySolve(vm, va, dx), "the equilibrated KKT with active angle rows must be solvable");
        Assert.All(dx, v => Assert.True(double.IsFinite(v)));

        // The single active row lands its difference exactly on the cap after the linear step.
        double dF = mm.AngleColumn(iF) >= 0 ? dx[mm.AngleColumn(iF)] : 0.0;
        double dT = mm.AngleColumn(iT) >= 0 ? dx[mm.AngleColumn(iT)] : 0.0;
        Assert.Equal(Math.Sign(diff) * cap, diff + dF - dT, 9);
    }

    [Fact]
    public void A_difference_inside_the_hysteresis_band_is_held_not_pushed_onto_the_limit()
    {
        (GridModel m, PowerFlowResult truth, MeasurementModel mm, double[] w) = Case14Exact();
        // Flat voltages, bus 8 rotated so the 7-8 difference sits inside the band: under the limit by a quarter of
        // the band. Every other branch's difference is zero, far below the trigger.
        double cap = 0.1;
        double band = cap * FeasibleWlsStep.AngleHysteresisFraction;
        int i7 = truth.BusIds.ToList().IndexOf(7), i8 = truth.BusIds.ToList().IndexOf(8);
        double[] vm = Enumerable.Repeat(1.0, m.BusCount).ToArray();
        double[] va = new double[m.BusCount];
        va[i8] = -(cap - 0.25 * band);
        double diff = va[i7] - va[i8];
        Assert.True(diff < cap && diff > cap - band);

        var step = new FeasibleWlsStep(new GridSim.Core.PowerFlow.DenseLuSolver(),
            new FeasibilityConstraints { AngleDiffMaxRad = cap, EnforceGeneratorQ = false });
        step.Begin(m, mm, w);
        var dx = new double[mm.StateSize];
        Assert.True(step.TrySolve(vm, va, dx));
        double d7 = mm.AngleColumn(i7) >= 0 ? dx[mm.AngleColumn(i7)] : 0.0;
        double d8 = mm.AngleColumn(i8) >= 0 ? dx[mm.AngleColumn(i8)] : 0.0;
        // Held: the row's right-hand side is clamped to zero, so the linear step does not change the difference
        // (and in particular does not push it up onto the cap).
        Assert.Equal(0.0, d7 - d8, 9);
    }

    [Theory]
    [InlineData("57", 0.0)]
    [InlineData("57", 1.0)]
    [InlineData("118", 0.0)]
    [InlineData("118", 1.0)]
    public void Synthetic_telemetry_no_longer_fails_at_the_first_iteration(string name, double noise)
    {
        GridModel m = name == "57" ? IeeeCases.Case57() : IeeeCases.Case118();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { EnforceQLimits = true });
        MeasurementSet set = SyntheticMeasurements.Sample(m, truth, new SyntheticMeasurementOptions { NoiseScale = noise });
        EstimationResult est = new WlsStateEstimator().Estimate(m, set,
            new EstimationOptions { Method = EstimationMethod.Feasible });
        // Before the equilibration the KKT was declared singular at iteration 0 (Infeasible, 0 iterations) in all
        // four cases.
        //
        // Moved 2026-10 (N043, authorised by Mark's best-judgement ruling): this pinned case118 at noise 1 at
        // MaxIterations, the defect itself (the generator-Q active set had no hysteresis and cycled between a
        // constrained and an unconstrained step: 30 iterations, J 877.8811728901087). With the N043 band and
        // multiplier-sign release it converges in 8 iterations (J 877.8811650453016), so all four cases now
        // converge. Before: expected = (118, 1) ? MaxIterations : Converged. After: Converged in every case.
        Assert.True(est.Iterations > 0, $"case{name}: Feasible stopped at iteration {est.Iterations} ({est.Status})");
        Assert.Equal(EstimationStatus.Converged, est.Status);
    }
}
