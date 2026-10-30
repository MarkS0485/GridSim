using GridSim.Core.Estimation;
using Xunit;

namespace GridSim.Tests;

/// <summary>Projection statistics: a leverage point (a Jacobian row far from the cloud in state space) gets a
/// large PS and a small leverage weight, while the well-placed rows sit near 1 - the masking-immune leverage
/// the Schweppe-Huber GM estimator uses.</summary>
public class ProjectionStatisticsTests
{
    [Fact]
    public void A_leverage_point_gets_a_high_projection_statistic()
    {
        // A genuinely TIGHT cluster of rows, plus one row far out in state space (a leverage point).
        var points = new[]
        {
            new[] { 1.00, 1.00 }, new[] { 1.02, 0.99 }, new[] { 0.98, 1.01 }, new[] { 1.01, 1.02 },
            new[] { 0.99, 0.98 }, new[] { 1.00, 1.01 }, new[] { 0.99, 1.00 }, new[] { 1.01, 0.99 },
            new[] { 40.0, 40.0 }, // leverage point
        };

        double[] ps = ProjectionStatistics.Compute(points);
        int leverage = points.Length - 1;
        for (int i = 0; i < leverage; i++)
            Assert.True(ps[i] < ps[leverage], $"cluster row {i} PS {ps[i]} should be below leverage PS {ps[leverage]}");

        // With a cutoff between them, the leverage point is down-weighted and the cluster is not.
        double cutoff = 5.0;
        Assert.Equal(1.0, ProjectionStatistics.LeverageWeight(ps[0], cutoff));
        Assert.True(ProjectionStatistics.LeverageWeight(ps[leverage], cutoff) < 0.5);
    }
}
