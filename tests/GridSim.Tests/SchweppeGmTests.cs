using System;
using GridSim.Core.Diagnostics;
using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The Schweppe-Huber GM estimator: it judges each measurement against its leverage-adjusted (normalized)
/// residual, so a gross error on a high-leverage measurement - which plain WLS follows and plain Huber masks -
/// is down-weighted. On clean data it reduces to WLS (recovers truth to machine precision). It is a
/// measurement-only static filter, so it takes no Dynamics->Estimation review-gate entry.
/// </summary>
public class SchweppeGmTests
{
    private static (GridModel Model, PowerFlowResult Truth) Fixture()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        return (model, truth);
    }

    [Fact]
    public void SchweppeGm_reduces_to_wls_on_clean_data()
    {
        (GridModel model, PowerFlowResult truth) = Fixture();
        EstimationResult est = new WlsStateEstimator().Estimate(
            model, WlsEstimatorTests.ExactFullSet(model, truth),
            new EstimationOptions { Method = EstimationMethod.SchweppeGm });

        Assert.Equal(EstimationStatus.Converged, est.Status);
        for (int i = 0; i < model.BusCount; i++)
        {
            Assert.True(Math.Abs(est.Vm[i] - truth.Vm[i]) < 1e-9, $"bus {truth.BusIds[i]} Vm");
            Assert.True(Math.Abs(est.VaDeg[i] - truth.VaDeg[i]) < 1e-8, $"bus {truth.BusIds[i]} Va");
        }
    }

    [Fact]
    public void SchweppeGm_is_more_robust_than_plain_wls_to_a_gross_error()
    {
        (GridModel model, PowerFlowResult truth) = Fixture();
        MeasurementSet clean = WlsEstimatorTests.ExactFullSet(model, truth);

        // Corrupt the first voltage measurement with a large gross error.
        var corrupted = new System.Collections.Generic.List<Measurement>(clean.Measurements);
        int bad = -1;
        for (int i = 0; i < corrupted.Count; i++)
            if (corrupted[i].Kind == MeasurementKind.VmPu) { bad = i; break; }
        Assert.True(bad >= 0);
        int badBusId = corrupted[bad].Bus;
        corrupted[bad] = corrupted[bad] with { Value = corrupted[bad].Value + 0.30 };
        var set = new MeasurementSet { Case = model.Name, BaseMva = model.BaseMva, Measurements = corrupted };

        // Disable bad-data removal so we isolate the robust WEIGHTING behaviour (not removal).
        var noRemoval = new EstimationOptions { DetectBadData = false };
        EstimationResult wls = new WlsStateEstimator().Estimate(model, set, noRemoval);
        EstimationResult gm = new WlsStateEstimator().Estimate(model, set,
            noRemoval with { Method = EstimationMethod.SchweppeGm });

        int busRow = -1;
        for (int i = 0; i < truth.BusIds.Count; i++) if (truth.BusIds[i] == badBusId) { busRow = i; break; }
        Assert.True(busRow >= 0);

        double wlsErr = Math.Abs(wls.Vm[busRow] - truth.Vm[busRow]);
        double gmErr = Math.Abs(gm.Vm[busRow] - truth.Vm[busRow]);
        Assert.True(gmErr < wlsErr,
            $"SchweppeGm should be closer to truth at the corrupted bus: GM {gmErr} vs WLS {wlsErr}");
    }

    [Fact]
    public void SchweppeGm_is_a_static_filter_not_dynamics_fused()
    {
        // Measurement-only: unconditionally permitted, and NOT registered in the review gate.
        RestrictionReview.AssertPermitted(nameof(EstimationMethod.SchweppeGm), usesDynamicsModel: false);
        Assert.False(RestrictionReview.IsReviewed(nameof(EstimationMethod.SchweppeGm)));
    }
}
