using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-623 follow-up: the thermal (49) and overcurrent elements (50 instantaneous, 51 time) read a branch whose two ends have collapsed as
/// carrying 0 pu, a confident "not operating" on a branch that may be carrying fault current. Like the undercurrent
/// element (37), they now give such a CurrentUnknown branch no verdict; a branch with a healthy end is unchanged.
/// </summary>
public class Gr623OvercurrentCollapsedBranchTests
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

    // Buses 2 and 3 collapsed to 0 pu: branch 2-3 is CurrentUnknown, branch 1-2 keeps its healthy end at bus 1.
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

    public static TheoryData<string> Elements => new() { "49", "50", "51" };

    private static IProtectionRelay Element(string device) => device switch
    {
        "49" => new ThermalOverloadRelay(),
        "50" => new InstantaneousOvercurrentRelay(),
        _ => new TimeOvercurrentRelay(),
    };

    [Theory]
    [MemberData(nameof(Elements))]
    public void Gr623_an_overcurrent_element_gives_a_current_unknown_branch_no_verdict(string device)
    {
        var ctx = new ProtectionContext(ThreeBus(), Collapsed(), new ProtectionSettings(), null);
        Assert.True(ctx.CurrentUnknown[1]);
        List<RelayVerdict> verdicts = Element(device).Evaluate(ctx).ToList();
        Assert.DoesNotContain(verdicts, v => v.LocationKind == ProtectionLocation.Branch && v.LocationId == 1);
    }

    [Theory]
    [MemberData(nameof(Elements))]
    public void Gr623_a_branch_with_a_healthy_end_still_gets_its_overcurrent_verdict(string device)
    {
        var ctx = new ProtectionContext(ThreeBus(), Collapsed(), new ProtectionSettings(), null);
        Assert.False(ctx.CurrentUnknown[0]);
        List<RelayVerdict> verdicts = Element(device).Evaluate(ctx).ToList();
        // Branch 1-2 carries 0.5 pu against a 1.0 pu rating: not operating, and the only candidate left, so it is the
        // closest non-operating verdict the element reports.
        RelayVerdict v = Assert.Single(verdicts);
        Assert.Equal(ProtectionLocation.Branch, v.LocationKind);
        Assert.Equal(0, v.LocationId);
        Assert.False(v.Operate);
    }
}
