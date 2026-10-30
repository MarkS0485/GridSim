using System.Collections.Generic;
using GridSim.Core.Estimation;
using GridSim.Core.Estimation.Security;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The FDIA screen's protected-anchor check. A perfectly crafted attack that lands on another feasible, in-limit
/// operating point is invisible to the envelope and physics checks, but it cannot move a trusted (PMU-anchored)
/// measurement it did not forge. When the estimated state disagrees with a protected anchor by more than that
/// anchor's own noise, the screen flags it (<see cref="FdiaScreenReport.AnchorInconsistent"/>). A clean estimate
/// screened with correct anchors is not flagged; an anchor whose value is deliberately offset far from the state
/// is flagged. The existing FdiaScreenTests and FdiaCloakedAttackTests stay untouched.
/// </summary>
public class FdiaProtectedAnchorTests
{
    // Built exactly as FdiaScreenTests.CleanEstimate: case9, Newton-Raphson truth, a zero-noise WLS estimate that
    // recovers it and passes chi-square.
    private static (GridModel Model, EstimationResult Estimate) CleanEstimate()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        EstimationResult est = new WlsStateEstimator().Estimate(model, WlsEstimatorTests.ExactFullSet(model, truth));
        return (model, est);
    }

    // PMU voltage and angle anchors taken from the estimated state itself: a correct feed reproduces the state, so
    // every anchor residual z - h(x_hat) is zero to machine precision.
    private static List<Measurement> CorrectAnchors(EstimationResult est)
    {
        var anchors = new List<Measurement>();
        for (int i = 0; i < est.BusIds.Count; i++)
        {
            int id = est.BusIds[i];
            anchors.Add(new Measurement(MeasurementKind.VmPu, est.Vm[i], 0.004, Bus: id, Source: "pmu"));
            anchors.Add(new Measurement(MeasurementKind.VaDeg, est.VaDeg[i], 0.01, Bus: id, Source: "pmu"));
        }
        return anchors;
    }

    [Fact]
    public void A_clean_estimate_with_correct_protected_anchors_is_not_flagged()
    {
        (GridModel model, EstimationResult est) = CleanEstimate();

        FdiaScreenReport report = FdiaScreen.Screen(model, est, protectedAnchors: CorrectAnchors(est));

        Assert.True(report.ChiSquarePassed);
        Assert.False(report.AnchorInconsistent);
        Assert.False(report.SuspectedFdia);
        Assert.Empty(report.ProtectedAnchorViolations!);
    }

    [Fact]
    public void An_anchor_offset_far_from_the_state_sets_anchor_inconsistent_and_flags_the_screen()
    {
        (GridModel model, EstimationResult est) = CleanEstimate();

        // A trusted PMU row at bus 0 reads 0.2 pu away from the estimated voltage: 50 sigma on a 0.004 pu meter, far
        // beyond the three-sigma significance margin. The attacker could not have moved this anchor, so the state it
        // produced no longer reproduces the anchor - the feasible-landing signature.
        int id = est.BusIds[0];
        var offsetAnchor = new Measurement(MeasurementKind.VmPu, est.Vm[0] + 0.2, 0.004, Bus: id, Source: "pmu");

        FdiaScreenReport report = FdiaScreen.Screen(model, est, protectedAnchors: new[] { offsetAnchor });

        Assert.True(report.ChiSquarePassed);          // measurement-consistent...
        Assert.True(report.AnchorInconsistent);       // ...yet it disagrees with the trusted anchor...
        Assert.True(report.SuspectedFdia);            // ...so the screen flags a possible feasible-landing attack.
        Assert.NotNull(report.ProtectedAnchorViolations);
        Assert.NotEmpty(report.ProtectedAnchorViolations!);
    }

    [Fact]
    public void Without_anchors_the_screen_behaves_exactly_as_before()
    {
        (GridModel model, EstimationResult est) = CleanEstimate();

        FdiaScreenReport withNone = FdiaScreen.Screen(model, est);
        FdiaScreenReport withNull = FdiaScreen.Screen(model, est, protectedAnchors: null);

        Assert.False(withNone.AnchorInconsistent);
        Assert.False(withNone.SuspectedFdia);
        Assert.False(withNull.AnchorInconsistent);
        Assert.False(withNull.SuspectedFdia);
    }
}
