using System;
using GridSim.Core.Diagnostics;
using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>The orthogonal (QR) estimator method: it solves the weighted least squares without forming the
/// normal equations, and recovers the truth to machine precision - the same solution as WLS on well-conditioned
/// data, via a numerically-robust path. A measurement-only static filter (no review gate).</summary>
public class QrEstimatorTests
{
    [Fact]
    public void Qr_method_recovers_truth_and_matches_wls()
    {
        GridModel model = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet set = WlsEstimatorTests.ExactFullSet(model, truth);

        EstimationResult qr = new WlsStateEstimator().Estimate(model, set, new EstimationOptions { Method = EstimationMethod.Qr });
        EstimationResult wls = new WlsStateEstimator().Estimate(model, set, new EstimationOptions { Method = EstimationMethod.Wls });

        Assert.Equal(EstimationStatus.Converged, qr.Status);
        for (int i = 0; i < model.BusCount; i++)
        {
            Assert.True(Math.Abs(qr.Vm[i] - truth.Vm[i]) < 1e-9, $"bus {truth.BusIds[i]} Vm");
            Assert.True(Math.Abs(qr.VaDeg[i] - truth.VaDeg[i]) < 1e-8, $"bus {truth.BusIds[i]} Va");
            Assert.True(Math.Abs(qr.Vm[i] - wls.Vm[i]) < 1e-8, $"QR vs WLS Vm at bus {truth.BusIds[i]}");
        }
    }

    [Fact]
    public void Qr_is_a_static_filter_not_dynamics_fused()
    {
        RestrictionReview.AssertPermitted(nameof(EstimationMethod.Qr), usesDynamicsModel: false);
        Assert.False(RestrictionReview.IsReviewed(nameof(EstimationMethod.Qr)));
    }
}
