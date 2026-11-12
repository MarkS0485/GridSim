using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-518: the Feasible method's hard backstop (a voltage pushed below FeasibilityConstraints.VmFloor is set to the floor)
/// acted without a word. Every clamp is now counted on the result (VmFloorClamps) and the buses named
/// (VmFloorClampedBusIds). Reporting only: the estimate itself is unchanged.
/// </summary>
public class Gr518VmFloorClampReportTests
{
    private static (GridModel Model, MeasurementSet Set) Case14()
    {
        GridModel m = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { EnforceQLimits = true });
        return (m, SyntheticMeasurements.Sample(m, truth, new SyntheticMeasurementOptions { NoiseScale = 0.0 }));
    }

    [Fact]
    public void Gr518_a_floor_above_the_true_voltages_is_reported_as_clamps_on_named_buses()
    {
        // A floor at 1.04 pu sits above several case14 voltages (and above a 1.0 pu start), so the backstop must act.
        (GridModel m, MeasurementSet set) = Case14();
        var feas = new FeasibilityConstraints { VmFloor = 1.04, VmMin = 1.05, VmMax = 1.3 };
        EstimationResult est = new WlsStateEstimator().Estimate(m, set,
            new EstimationOptions { Method = EstimationMethod.Feasible, Feasibility = feas });

        Assert.True(est.VmFloorClamps > 0, "the backstop clamped nothing, so the test does not reach the clamp path");
        Assert.NotEmpty(est.VmFloorClampedBusIds);
        var ids = m.Buses.Select(b => b.Id).ToHashSet();
        Assert.All(est.VmFloorClampedBusIds, id => Assert.Contains(id, ids));
        Assert.Equal(est.VmFloorClampedBusIds.Distinct().Count(), est.VmFloorClampedBusIds.Count);
        Assert.True(est.VmFloorClamps >= est.VmFloorClampedBusIds.Count);
    }

    [Theory]
    [InlineData(EstimationMethod.Feasible)]
    [InlineData(EstimationMethod.Wls)]
    public void Gr518_no_clamp_reads_zero_and_an_empty_list(EstimationMethod method)
    {
        (GridModel m, MeasurementSet set) = Case14();
        EstimationResult est = new WlsStateEstimator().Estimate(m, set, new EstimationOptions { Method = method });
        Assert.Equal(0, est.VmFloorClamps);
        Assert.Empty(est.VmFloorClampedBusIds);
    }
}
