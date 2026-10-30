using System.Numerics;
using GridSim.Core.Analysis;
using GridSim.Core.Distribution;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Workstream 4 (October 2026 review), the study-level half of the convergence contract: R027 (a diverged
/// subtransient run reported in synchronism), R223 (AC-OPF converged when its loop never settled), R226
/// (continuation at the nose), R234 (bolted fault at the source returned NaN), R261 (a chi-square test that never
/// ran recorded as PASS).
/// </summary>
public class ConvergenceContractStudyTests
{
    private static Complex[,] Tie(double xTot)
    {
        Complex y = Complex.One / new Complex(0.0, xTot);
        return new[,] { { y, -y }, { -y, y } };
    }

    private static MultiMachineSubtransient.Machine Flat() => new(
        Xd: 0.3, Xdp: 0.3, Xdpp: 0.3, Xq: 0.3, Xqp: 0.3, Xqpp: 0.3,
        Tdop: 6.0, Tdopp: 0.04, Tqop: 0.4, Tqopp: 0.06);

    [Fact]
    public void A_diverged_subtransient_run_is_never_in_synchronism()
    {
        // NaN mechanical power drives every angle to NaN. NaN made both the min/max and the spread test false, so the
        // run used to report StayedInSynchronism = true (R027).
        var eq0 = new[] { 1.10, 1.00 };
        MultiMachineSubtransient.Result sub = MultiMachineSubtransient.Simulate(
            new[] { Flat(), Flat() }, new[] { 4.0, 5.0 }, new[] { 0.0, 0.0 }, new[] { double.NaN, double.NaN }, efd: eq0,
            new[] { 0.2, 0.0 }, eqp0: eq0, edp0: new[] { 0.0, 0.0 }, eqpp0: eq0, edpp0: new[] { 0.0, 0.0 },
            Tie(0.5), 50.0, stepSeconds: 0.005, durationSeconds: 0.1);
        Assert.False(sub.StayedInSynchronism);
    }

    [Fact]
    public void Ac_opf_that_cannot_settle_is_not_converged()
    {
        // One iteration cannot settle the loss loop (the first pass always moves the losses from 0), so the result
        // must not be reported converged. It used to return Converged = true whenever the loop ran out (R223).
        AcOpfResult r = AcOptimalPowerFlow.Solve(IeeeCases.Case9(), maxIterations: 1);
        Assert.False(r.Converged);
        AcOpfResult ok = AcOptimalPowerFlow.Solve(IeeeCases.Case9());
        Assert.True(ok.Converged);
    }

    [Fact]
    public void Continuation_reaches_the_nose_without_an_exception()
    {
        // R226: the singular Jacobian at the nose used to throw out of MaxLoadability. Since 1.95.001 the canonical
        // Newton step reports a singular Jacobian as non-convergence, which the continuation loop backs off from.
        double lambda = ContinuationPowerFlow.MaxLoadability(IeeeCases.Case9());
        Assert.True(double.IsFinite(lambda) && lambda > 1.0, $"max loadability {lambda}");
    }

    [Fact]
    public void A_bolted_fault_at_the_source_fails_loudly_instead_of_returning_nan()
    {
        // R234: zero source impedance (infinite bus) and a bolted fault at the source gave 0/0 -> NaN currents.
        var m = new MultiPhaseModel(
            new[] { new PhaseBus(0, true, MultiPhaseModel.BalancedNominal(1.0)), new PhaseBus(1, false) },
            new[] { new PhaseBranch(0, 1, PhaseImpedance.Diagonal(new Complex(0.01, 0.05))) },
            Array.Empty<PhaseLoad>());
        var ex = Assert.Throws<ArgumentException>(() => SequenceFaultStudy.SingleLineToGround(m, 0));   // bus 0 is the source
        Assert.Contains("zero loop impedance", ex.Message);
    }

    [Fact]
    public void A_chi_square_test_that_did_not_run_is_not_recorded_as_pass()
    {
        // R261: with bad-data detection off the test never runs; ChiSquarePassed stays (legacy) true but the new
        // tri-state says NotRun, and the audit trail prints not-run (n/a before N208), not PASS.
        GridModel m = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(m);
        MeasurementSet set = SyntheticMeasurements.Sample(m, truth);
        EstimationResult off = new WlsStateEstimator().Estimate(m, set, new EstimationOptions { DetectBadData = false });
        Assert.Equal(ChiSquareOutcome.NotRun, off.ChiSquareTest);
        // Moved 2026-10 (N208, Ruling R1 follow-up, Mark delegated 2026-10-04): a chi-square test that did not run is
        // recorded as "not run" in so many words. Before: "chi2=n/a". After: "chi2=not-run" (one token, since the audit
        // line is key=value fields). The intent of the test (never PASS for a test that did not run) is unchanged.
        Assert.Contains("chi2=not-run", EstimationAuditTrail.Record(new GridSim.Core.Diagnostics.AuditLog(), DateTime.UnixEpoch, null, off).Summary);

        EstimationResult on = new WlsStateEstimator().Estimate(m, set, new EstimationOptions { DetectBadData = true });
        Assert.NotEqual(ChiSquareOutcome.NotRun, on.ChiSquareTest);
        Assert.Equal(on.ChiSquarePassed, on.ChiSquareTest == ChiSquareOutcome.Passed);
    }
}
