using GridSim.Core.Dispatch;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

public class DispatcherTests
{
    // A minimal well-formed grid: one slack bus, one load bus, and the branch between them.
    // Generators are supplied by the caller so each test can vary the slack-bus population.
    private static GridModel Model(double demand, IReadOnlyList<Generator> gens) => new GridModel
    {
        Name = "dispatch-test",
        BaseMva = 100,
        Buses = new[]
        {
            new Bus(1, BusType.Slack, BaseKv: 400),
            new Bus(2, BusType.Pq, Pd: demand, BaseKv: 400),
        },
        Generators = gens,
        Branches = new[] { new Branch(1, 2, 0.01, 0.1, Name: "L1-2") },
    };

    private static double TotalGen(GridModel m) => m.Generators.Sum(g => g.Pg);

    [Fact]
    public void Two_generators_on_the_slack_bus_do_not_double_count()
    {
        const double demand = 1000, loss = 0.02;
        // Two generators on the slack bus (1) plus one dispatched generator elsewhere (2).
        var m = Model(demand, new[]
        {
            new Generator(Bus: 1, Pg: 300, Name: "slack-marginal"),
            new Generator(Bus: 1, Pg: 250, Name: "slack-base"),   // extra plant on the slack bus
            new Generator(Bus: 2, Pg: 200, Name: "wind"),
        });

        var balanced = Dispatcher.Balance(m, loss);

        // Total generation must meet demand + losses exactly, counted once  -  the second
        // slack-bus generator must NOT be added on top of the balancing quantity.
        Assert.Equal(demand * (1 + loss), TotalGen(balanced), 6);

        // The non-marginal generators keep their dispatched output untouched.
        Assert.Equal(250, balanced.Generators[1].Pg, 6);
        Assert.Equal(200, balanced.Generators[2].Pg, 6);

        // The designated marginal plant absorbs the balance: target - fixed generation.
        Assert.Equal(demand * (1 + loss) - 250 - 200, balanced.Generators[0].Pg, 6);
    }

    [Fact]
    public void Three_generators_on_the_slack_bus_still_balance_once()
    {
        const double demand = 500, loss = 0.03;
        var m = Model(demand, new[]
        {
            new Generator(Bus: 1, Pg: 100, Name: "g1"),
            new Generator(Bus: 1, Pg: 120, Name: "g2"),
            new Generator(Bus: 1, Pg: 80, Name: "g3"),
        });

        var balanced = Dispatcher.Balance(m, loss);

        Assert.Equal(demand * (1 + loss), TotalGen(balanced), 6);
    }

    [Fact]
    public void Single_slack_generator_case_is_unchanged()
    {
        const double demand = 1000, loss = 0.02;
        var m = Model(demand, new[]
        {
            new Generator(Bus: 1, Pg: 999, Name: "slack"),   // arbitrary pre-value
            new Generator(Bus: 2, Pg: 400, Name: "nuclear"),
        });

        var balanced = Dispatcher.Balance(m, loss);

        // Slack generator set to demand+losses minus the single non-slack generator.
        Assert.Equal(demand * (1 + loss) - 400, balanced.Generators[0].Pg, 6);
        Assert.Equal(400, balanced.Generators[1].Pg, 6);     // untouched
        Assert.Equal(demand * (1 + loss), TotalGen(balanced), 6);
    }
}
