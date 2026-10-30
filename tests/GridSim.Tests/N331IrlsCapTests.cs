using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N331 (1.95.249): Huber and Schweppe-GM get their own IRLS outer cap (50) because their weights converge linearly and
/// the old cap of 10 stopped clean case14 estimates short of the weight tolerance. LAV keeps the cap of 10.
/// </summary>
public class N331IrlsCapTests
{
    private static (GridModel Model, MeasurementSet Set) Case14(int seed)
    {
        GridModel m = IeeeCases.Case14();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(r.Converged);
        return (m, SyntheticMeasurements.Sample(m, r, new SyntheticMeasurementOptions { Seed = seed }));
    }

    [Fact]
    public void The_m_estimator_cap_is_50_and_lav_keeps_10()
    {
        Assert.Equal(50, WlsStateEstimator.IrlsMaxOuterIterationsMEstimator);
        Assert.Equal(10, WlsStateEstimator.IrlsMaxOuterIterations);
    }

    [Theory]
    [InlineData(EstimationMethod.SchweppeGm, 3)]   // hit the old cap of 10 (largest weight change 0.00255 at pass 10)
    [InlineData(EstimationMethod.Huber, 1)]        // hit the old cap of 10 (0.00156 at pass 10)
    public void Clean_case14_sets_that_hit_the_old_cap_now_settle(EstimationMethod method, int seed)
    {
        (GridModel m, MeasurementSet set) = Case14(seed);
        EstimationResult e = new WlsStateEstimator().Estimate(m, set, new EstimationOptions { Method = method });
        Assert.Equal(EstimationStatus.Converged, e.Status);
        Assert.False(e.IrlsCapReached);
        Assert.True(e.ChiSquarePassed);
    }
}
