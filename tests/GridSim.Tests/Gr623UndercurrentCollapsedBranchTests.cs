using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-623: the protection context derives each branch-end current as S/|V| and writes 0 when that end's voltage has
/// collapsed. With BOTH ends collapsed the branch read 0 pu, so the undercurrent element (37) operated on a branch that
/// may be carrying fault current. Such a branch is now flagged CurrentUnknown and 37 gives it no verdict.
/// </summary>
public class Gr623UndercurrentCollapsedBranchTests
{
    private static GridModel ThreeBus() => new()
    {
        BaseMva = 100,
        Buses = new[]
        {
            new Bus(1, BusType.Slack, BaseKv: 132), new Bus(2, BusType.Pq, Pd: 10, BaseKv: 132),
            new Bus(3, BusType.Pq, Pd: 10, BaseKv: 132),
        },
        Branches = new[] { new Branch(1, 2, 0.01, 0.1, RateAMva: 100), new Branch(2, 3, 0.01, 0.1, RateAMva: 100) },
        Generators = new[] { new Generator(1, 20) },
    };

    /// <summary>Buses 2 and 3 collapsed to 0 pu; branch 2-3 still reports a 50 MVA flow at both ends.</summary>
    private static PowerFlowResult Collapsed() => new()
    {
        Converged = true,
        Iterations = 1,
        MaxMismatch = 0.0,
        BusIds = new[] { 1, 2, 3 },
        Vm = new[] { 1.0, 0.0, 0.0 },
        VaDeg = new double[3],
        PInjMw = new double[3],
        QInjMvar = new double[3],
        TotalLossMw = 0.0,
        TotalLossMvar = 0.0,
        LineFlows = new[]
        {
            new LineFlow(1, 2, 30, 40, -30, -40, 0, 0, RatingMva: 100),
            new LineFlow(2, 3, 30, 40, -30, -40, 0, 0, RatingMva: 100),
        },
    };

    [Fact]
    public void Gr623_a_branch_with_both_ends_collapsed_is_current_unknown_and_gets_no_undercurrent_verdict()
    {
        var ctx = new ProtectionContext(ThreeBus(), Collapsed(), new ProtectionSettings(), null);
        Assert.False(ctx.CurrentUnknown[0]);   // bus 1 is healthy: the current is derived there
        Assert.True(ctx.CurrentUnknown[1]);    // both ends at 0 pu

        List<RelayVerdict> verdicts = new UnderCurrentRelay().Evaluate(ctx).ToList();
        Assert.DoesNotContain(verdicts, v => v.LocationKind == ProtectionLocation.Branch && v.LocationId == 1);
    }

    [Fact]
    public void Gr623_one_collapsed_end_still_takes_the_current_from_the_healthy_end()
    {
        var ctx = new ProtectionContext(ThreeBus(), Collapsed(), new ProtectionSettings(), null);
        // Branch 1-2: |S| = 50 MVA = 0.5 pu at 1.0 pu on the healthy end.
        Assert.Equal(0.5, Math.Max(ctx.IFromPu[0], ctx.IToPu[0]), 12);
    }
}
