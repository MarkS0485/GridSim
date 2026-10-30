using System;
using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Estimation;
using GridSim.Core.Estimation.Security;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The FDIA screen against a genuinely residual-cloaked attack, and its behaviour when conventional bad-data detection
/// has already failed.
///
/// <para>A residual-cloaked attack on AC state estimation replaces the true measurements z = h(x) with z' = h(x + c):
/// the measurements of a different, fully self-consistent operating point. (The linear form a = H c is the first-order
/// version of the same thing.) The estimator then converges to x + c with exactly the same residual as the honest fit,
/// so the chi-square and largest-normalised-residual tests cannot see it. Because the attacked state is self-consistent,
/// its projected power flow satisfies every conservation law by construction; the screen catches it through the
/// operational envelope instead (here, a bus voltage driven far outside the band), which is the sub-class of attacks the
/// screen is designed to flag.</para>
/// </summary>
public class FdiaCloakedAttackTests
{
    /// <summary>The attacked voltage: 0.10 pu above the default 1.10 pu band. The bus's own voltage meter has a sigma of
    /// 0.004 pu (<see cref="WlsEstimatorTests.ExactFullSet"/>), so the breach is 25 sigma, far beyond the screen's
    /// three-sigma significance rule.</summary>
    private const double AttackedVmPu = 1.20;

    /// <summary>
    /// The complete, self-consistent power flow at a given state (voltages and angles), built exactly as
    /// <see cref="EstimationResult.ToPowerFlowResult"/> builds the projection of an estimate: injections from the
    /// network admittance at that state, then the standard result assembly (which adds the branch flows).
    /// </summary>
    private static PowerFlowResult SelfConsistentFlowAt(GridModel model, double[] vmByBus, double[] vaDegByBus)
    {
        int n = model.BusCount;
        var index = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) index[model.Buses[i].Id] = i;
        ComplexCsr y = SparseNewtonRaphsonPowerFlow.BuildY(model, index);

        var vm = new double[n];
        var va = new double[n];
        for (int i = 0; i < n; i++)
        {
            vm[i] = vmByBus[i];
            va[i] = vaDegByBus[i] * Math.PI / 180.0;
        }

        var p = new double[n];
        var q = new double[n];
        SparseNewtonRaphsonPowerFlow.ComputeInjections(y, vm, va, p, q);
        return NewtonShell.AssembleResult(model, n, vm, va, p, q, model.BaseMva,
            converged: true, totalIters: 0, maxMis: 0.0, id => index[id]);
    }

    /// <summary>The honest state x (Newton-Raphson on case9) and the attacked state x + c, in model bus order.</summary>
    private static (GridModel Model, PowerFlowResult Truth, PowerFlowResult Attacked, int AttackedBusId) CleanAndAttacked()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(truth.Converged);

        // Read the honest state into model bus order (the result's own order is keyed by bus id).
        int n = model.BusCount;
        var position = new Dictionary<int, int>(n);
        for (int i = 0; i < truth.BusIds.Count; i++) position[truth.BusIds[i]] = i;
        var vm = new double[n];
        var vaDeg = new double[n];
        for (int i = 0; i < n; i++)
        {
            int at = position[model.Buses[i].Id];
            vm[i] = truth.Vm[at];
            vaDeg[i] = truth.VaDeg[at];
        }

        // The attack vector c: raise the voltage of the first load (PQ) bus, leave every angle and every other voltage
        // alone. Any state is a valid target; the attacker then publishes that state's measurements.
        int attackedIndex = Array.FindIndex(model.Buses.ToArray(), b => b.Type == BusType.Pq);
        Assert.True(attackedIndex >= 0, "case9 must have a load bus to attack");
        vm[attackedIndex] = AttackedVmPu;

        PowerFlowResult attacked = SelfConsistentFlowAt(model, vm, vaDeg);
        return (model, truth, attacked, model.Buses[attackedIndex].Id);
    }

    [Fact]
    public void A_residual_cloaked_attack_leaves_the_fit_unchanged_and_is_flagged_by_the_envelope()
    {
        (GridModel model, PowerFlowResult truth, PowerFlowResult attacked, int attackedBusId) = CleanAndAttacked();

        // Estimate from the honest measurements and from the attacker's measurements.
        EstimationResult honest = new WlsStateEstimator().Estimate(model, WlsEstimatorTests.ExactFullSet(model, truth));
        EstimationResult cloaked = new WlsStateEstimator().Estimate(model, WlsEstimatorTests.ExactFullSet(model, attacked));
        Assert.Equal(EstimationStatus.Converged, honest.Status);
        Assert.Equal(EstimationStatus.Converged, cloaked.Status);

        // The estimator recovers the attacked state, exactly as it recovers an honest one (the same 1e-10 bound the
        // estimator's own recovery test uses).
        var attackedVm = new Dictionary<int, double>();
        for (int i = 0; i < attacked.BusIds.Count; i++) attackedVm[attacked.BusIds[i]] = attacked.Vm[i];
        for (int i = 0; i < cloaked.BusIds.Count; i++)
            Assert.True(Math.Abs(cloaked.Vm[i] - attackedVm[cloaked.BusIds[i]]) < 1e-10,
                $"bus {cloaked.BusIds[i]}: estimated Vm {cloaked.Vm[i]:R} vs attacked state {attackedVm[cloaked.BusIds[i]]:R}");
        int at = cloaked.BusIds.ToList().IndexOf(attackedBusId);
        Assert.True(Math.Abs(cloaked.Vm[at] - AttackedVmPu) < 1e-10);

        // The cloak: both fits are exact, so the weighted residual is round-off in both. Conventional bad-data
        // detection therefore passes the attacked estimate just as it passes the honest one.
        Assert.True(honest.ObjectiveJ < 1e-16, $"honest J = {honest.ObjectiveJ:R}");
        Assert.True(cloaked.ObjectiveJ < 1e-16, $"cloaked J = {cloaked.ObjectiveJ:R}");
        Assert.Equal(ChiSquareOutcome.Passed, honest.ChiSquareTest);
        Assert.Equal(ChiSquareOutcome.Passed, cloaked.ChiSquareTest);
        Assert.Empty(cloaked.Removed);

        // The screen: the honest estimate raises nothing; the cloaked one is measurement-consistent yet 25 sigma outside
        // the voltage band at the attacked bus, which is the FDIA signature.
        Assert.False(FdiaScreen.Screen(model, honest).SuspectedFdia);
        FdiaScreenReport report = FdiaScreen.Screen(model, cloaked);
        Assert.True(report.ChiSquarePassed);
        Assert.True(report.SuspectedFdia, report.Summary);
        Assert.Contains(report.SignificantLimitViolations, v => v.Value > 1.1 && Math.Abs(v.Value - AttackedVmPu) < 1e-9);
    }

    [Fact]
    public void A_chi_square_failure_is_reported_as_conventional_bad_data_not_as_a_cloaked_attack()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet exact = WlsEstimatorTests.ExactFullSet(model, truth);

        // One gross error: the first branch-flow meter reads 100 sigma high. The full set is highly redundant, so this
        // meter is not critical and its error shows in the residual. Removal is switched off so the failure is reported
        // as found, not repaired.
        var corrupted = new List<Measurement>(exact.Measurements);
        int flow = corrupted.FindIndex(m => m.Kind == MeasurementKind.PFlowMw);
        Assert.True(flow >= 0);
        corrupted[flow] = corrupted[flow] with { Value = corrupted[flow].Value + 100.0 * corrupted[flow].Sigma };
        MeasurementSet set = new() { Case = exact.Case, BaseMva = exact.BaseMva, Measurements = corrupted };

        EstimationResult est = new WlsStateEstimator().Estimate(model, set,
            new EstimationOptions { DetectBadData = true, MaxBadDataRemovals = 0 });
        Assert.True(est.Converged);
        Assert.Equal(ChiSquareOutcome.Failed, est.ChiSquareTest);

        FdiaScreenReport report = FdiaScreen.Screen(model, est);
        Assert.False(report.ChiSquarePassed);
        Assert.False(report.SuspectedFdia);
        Assert.Contains("conventional bad-data detection applies", report.Summary);
    }

    [Fact]
    public void The_screen_refuses_an_estimate_whose_chi_square_test_did_not_run()
    {
        // With detection off the measurement-consistency half of the screen has no verdict; the screen says so rather
        // than reading "not run" as a pass.
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        EstimationResult est = new WlsStateEstimator().Estimate(model, WlsEstimatorTests.ExactFullSet(model, truth),
            new EstimationOptions { DetectBadData = false });
        Assert.Equal(ChiSquareOutcome.NotRun, est.ChiSquareTest);
        Assert.Throws<InvalidOperationException>(() => FdiaScreen.Screen(model, est));
    }
}
