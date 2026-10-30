using GridSim.Core.Analysis;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// DC PTDF/LODF. Pins the exact hand-computable values on an equal-reactance triangle, the reference
/// conventions (slack column zero, LODF diagonal -1), the defining LODF identity against a re-solve
/// with the branch removed, and the islanding guard on a radial line.
/// </summary>
public class DistributionFactorsTests
{
    // Triangle: buses 1(slack),2,3; lines 1-2, 2-3, 1-3, all X=1. Branch order is [1-2, 2-3, 1-3].
    private static GridModel Triangle(bool line12InService = true) => new()
    {
        BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pq, Pd: 0), new Bus(3, BusType.Pq, Pd: 0) },
        Generators = new[] { new Generator(1, Pg: 0) },
        Branches = new[]
        {
            new Branch(1, 2, 0.0, 1.0) { InService = line12InService },
            new Branch(2, 3, 0.0, 1.0),
            new Branch(1, 3, 0.0, 1.0),
        },
    };

    [Fact]
    public void PTDF_matches_the_analytic_triangle_and_the_slack_column_is_zero()
    {
        DistributionFactors df = DistributionFactors.Build(Triangle());

        // +1 at bus 2 (withdraw at slack 1) splits 2/3 down the direct line, 1/3 the long way.
        double[] col = df.PtdfColumn(2);
        Assert.Equal(-2.0 / 3.0, col[0], 9); // line 1-2
        Assert.Equal(1.0 / 3.0, col[1], 9);  // line 2-3
        Assert.Equal(-1.0 / 3.0, col[2], 9); // line 1-3

        // The slack column is identically zero.
        foreach (double v in df.PtdfColumn(1)) Assert.Equal(0.0, v, 12);
    }

    [Fact]
    public void LODF_diagonal_is_minus_one_and_satisfies_the_re_solve_identity()
    {
        GridModel m = Triangle();
        DistributionFactors df = DistributionFactors.Build(m);
        var inj = new Dictionary<int, double> { [2] = 100.0 }; // 100 MW at bus 2, slack absorbs

        double[] f = df.DcFlows(inj, m.BaseMva);
        double[] lodf = df.LodfColumn(0); // outage line 1-2
        Assert.Equal(-1.0, lodf[0], 12);

        // Defining identity: post-outage flow == flow with line 1-2 physically removed (both DC).
        DistributionFactors dfOut = DistributionFactors.Build(Triangle(line12InService: false));
        double[] fOut = dfOut.DcFlows(inj, m.BaseMva);
        for (int l = 0; l < f.Length; l++)
            Assert.Equal(fOut[l], f[l] + lodf[l] * f[0], 6);
    }

    [Fact]
    public void A_radial_bridge_outage_is_flagged_as_islanding()
    {
        // Two buses, one line: outaging it disconnects the load - LODF undefined.
        var m = new GridModel
        {
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pq, Pd: 50) },
            Generators = new[] { new Generator(1, Pg: 0) },
            Branches = new[] { new Branch(1, 2, 0.0, 0.1) },
        };
        DistributionFactors df = DistributionFactors.Build(m);
        Assert.True(df.OutageIslands(0));
        Assert.All(df.LodfColumn(0), v => Assert.True(double.IsNaN(v)));
    }
}
