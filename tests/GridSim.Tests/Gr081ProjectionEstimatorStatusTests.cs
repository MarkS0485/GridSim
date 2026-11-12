using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-081: EstimationResult.ToPowerFlowResult put the estimator's state-update norm in MaxMismatch with nothing saying
/// so, and the power-flow status had no Unobservable or Infeasible value (an unobservable estimate read NonFinite or
/// MaxIterations). The projection now carries EstimatorStatus and StateUpdateNorm under their own names. Status,
/// Converged and MaxMismatch are unchanged, so these tests also pin the old fields exactly.
/// </summary>
public class Gr081ProjectionEstimatorStatusTests
{
    [Fact]
    public void Gr081_a_converged_estimate_projects_its_status_and_update_norm_by_name()
    {
        GridModel m = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { EnforceQLimits = true });
        MeasurementSet set = SyntheticMeasurements.Sample(m, truth, new SyntheticMeasurementOptions { NoiseScale = 0.0 });
        EstimationResult est = new WlsStateEstimator().Estimate(m, set);
        Assert.Equal(EstimationStatus.Converged, est.Status);

        PowerFlowResult projected = est.ToPowerFlowResult(m);

        Assert.Equal(EstimationStatus.Converged, projected.EstimatorStatus);
        Assert.Equal(est.MaxStateUpdate, projected.StateUpdateNorm);
        // The old slot is unchanged, bit for bit: it still holds the same state-update norm.
        Assert.Equal(est.MaxStateUpdate, projected.MaxMismatch);
        Assert.True(projected.Converged);
        Assert.Equal(PowerFlowStatus.Converged, projected.Status);
        Assert.Equal(est.Iterations, projected.Iterations);
    }

    [Fact]
    public void Gr081_an_unobservable_estimate_projects_as_unobservable_not_only_as_a_power_flow_status()
    {
        // Voltage magnitudes only: no angle information, so case9 is unobservable (as ObservabilityTests pins).
        GridModel m = IeeeCases.Case9();
        var ms = m.Buses.Select(b => new Measurement(MeasurementKind.VmPu, 1.0, 0.004, Bus: b.Id)).ToArray();
        var set = new MeasurementSet { Case = m.Name, BaseMva = m.BaseMva, Measurements = ms };
        EstimationResult est = new WlsStateEstimator().Estimate(m, set);
        Assert.Equal(EstimationStatus.Unobservable, est.Status);

        PowerFlowResult projected = est.ToPowerFlowResult(m);

        Assert.Equal(EstimationStatus.Unobservable, projected.EstimatorStatus);
        Assert.False(projected.Converged);
        Assert.NotEqual(PowerFlowStatus.Converged, projected.Status);
        Assert.Equal(est.MaxStateUpdate, projected.StateUpdateNorm);
    }

    [Fact]
    public void Gr081_a_power_flow_solve_carries_no_estimator_fields()
    {
        GridModel m = IeeeCases.Case14();
        PowerFlowResult dense = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions());
        PowerFlowResult sparse = new SparseNewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions());
        Assert.Null(dense.EstimatorStatus);
        Assert.Null(dense.StateUpdateNorm);
        Assert.Null(sparse.EstimatorStatus);
        Assert.Null(sparse.StateUpdateNorm);
    }
}
