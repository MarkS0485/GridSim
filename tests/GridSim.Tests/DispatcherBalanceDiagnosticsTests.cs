using GridSim.Core.Dispatch;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R076 (October 2026 review): the Dispatcher promised to sanity-check that the slack stays within its capacity but
/// never did. <see cref="Dispatcher.BalanceDetailed"/> now reports a marginal target outside [Pmin, Pmax] and the slack-bus
/// count, without changing the balanced model (no silent clamp).
/// </summary>
public class DispatcherBalanceDiagnosticsTests
{
    private static GridModel Model(double demand, IReadOnlyList<Generator> gens, bool secondSlack = false) => new GridModel
    {
        Name = "dispatch-diag",
        BaseMva = 100,
        Buses = new[]
        {
            new Bus(1, BusType.Slack, BaseKv: 400),
            new Bus(2, BusType.Pq, Pd: demand, BaseKv: 400),
            new Bus(3, secondSlack ? BusType.Slack : BusType.Pq, BaseKv: 400),
        },
        Generators = gens,
        Branches = new[] { new Branch(1, 2, 0.01, 0.1, Name: "L1-2"), new Branch(2, 3, 0.01, 0.1, Name: "L2-3") },
    };

    [Fact]
    public void Detailed_balance_returns_exactly_the_model_balance_returns()
    {
        var m = Model(1000, new[] { new Generator(Bus: 1, Pg: 300, Pmax: 2000), new Generator(Bus: 2, Pg: 200) });
        GridModel plain = Dispatcher.Balance(m, 0.02);
        Dispatcher.BalanceResult detailed = Dispatcher.BalanceDetailed(m, 0.02);
        Assert.Equal(plain.Generators.Select(g => BitConverter.DoubleToInt64Bits(g.Pg)),
                     detailed.Model.Generators.Select(g => BitConverter.DoubleToInt64Bits(g.Pg)));
        Assert.Equal(1000 * 1.02 - 200, detailed.SlackTargetMw);
        Assert.Null(detailed.SlackCapacityNote);
        Assert.Equal(1, detailed.SlackBusCount);
    }

    [Fact]
    public void A_target_above_pmax_is_reported_not_clamped()
    {
        var m = Model(1000, new[] { new Generator(Bus: 1, Pg: 0, Pmax: 500), new Generator(Bus: 2, Pg: 100) });
        Dispatcher.BalanceResult r = Dispatcher.BalanceDetailed(m, 0.0);
        Assert.Equal(900, r.SlackTargetMw);
        Assert.Equal(900, r.Model.Generators[0].Pg);                 // the model is not clamped
        Assert.Contains("exceeds the marginal plant's Pmax 500.0 MW", r.SlackCapacityNote);
        Assert.Contains("short by 400.0 MW", r.SlackCapacityNote);
    }

    [Fact]
    public void A_target_below_pmin_is_reported()
    {
        var m = Model(100, new[] { new Generator(Bus: 1, Pg: 0, Pmin: 300), new Generator(Bus: 2, Pg: 50) });
        Dispatcher.BalanceResult r = Dispatcher.BalanceDetailed(m, 0.0);
        Assert.Contains("below the marginal plant's Pmin 300.0 MW", r.SlackCapacityNote);
    }

    [Fact]
    public void A_slack_bus_without_a_generator_and_a_second_slack_bus_are_reported()
    {
        var m = Model(100, new[] { new Generator(Bus: 2, Pg: 50) }, secondSlack: true);
        Dispatcher.BalanceResult r = Dispatcher.BalanceDetailed(m, 0.0);
        Assert.True(double.IsNaN(r.SlackTargetMw));
        Assert.Contains("has no generator", r.SlackCapacityNote);
        Assert.Equal(2, r.SlackBusCount);
    }
}
