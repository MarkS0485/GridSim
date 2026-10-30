using System.Linq;
using GridSim.Core.Analysis;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The ranked watch-list (<see cref="WatchList"/>): the scattered precursors aggregated into one short,
/// worst-first list scored by consequence x proximity. A heavily-loaded big circuit outranks a mildly-loaded
/// small one; the list is capped to the top N; and a supplied protection assessment sharpens a thermal item
/// with its time-to-trip.
/// </summary>
public class WatchListTests
{
    // BIG: a high-flow line loaded ~135% of a 100 MVA rating. SMALL: a lower-flow line ~90% loaded.
    private static GridModel Model() => new()
    {
        BaseMva = 100,
        Name = "watch",
        Buses = new[]
        {
            new Bus(1, BusType.Slack, Vm: 1.0, BaseKv: 132),
            new Bus(2, BusType.Pq, Pd: 130, Qd: 45, BaseKv: 132),
            new Bus(3, BusType.Pq, Pd: 88, Qd: 22, BaseKv: 132),
        },
        Generators = new[] { new Generator(1, 0, Vg: 1.0, Qmin: -400, Qmax: 400) },
        Branches = new[]
        {
            new Branch(1, 2, 0.005, 0.04, 0.0, RateAMva: 100, Name: "BIG"),
            new Branch(1, 3, 0.005, 0.04, 0.0, RateAMva: 100, Name: "SMALL"),
        },
    };

    private static PowerFlowResult Solve(GridModel m) => new NewtonRaphsonPowerFlow().Solve(m);

    [Fact]
    public void Ranks_the_worst_developing_condition_first()
    {
        GridModel model = Model();
        PowerFlowResult r = Solve(model);
        WatchListReport w = WatchList.Assess(model, r);

        Assert.NotNull(w.Top);
        Assert.Equal(WatchCategory.ThermalOverload, w.Top!.Category);
        Assert.Equal("BIG", w.Top.Location);

        // Scores are consequence x proximity, sorted worst-first.
        for (int i = 1; i < w.Items.Count; i++)
            Assert.True(w.Items[i - 1].Score >= w.Items[i].Score, "watch-list must be ranked worst-first");

        double big = w.Items.First(x => x.Location == "BIG").Score;
        double small = w.Items.First(x => x.Location == "SMALL").Score;
        Assert.True(big > small, $"the heavily-loaded big circuit ({big:F3}) should outrank the small one ({small:F3})");

        foreach (WatchItem it in w.Items)
        {
            Assert.InRange(it.Consequence, 0, 1);
            Assert.InRange(it.Proximity, 0, 1);
            Assert.Equal(it.Consequence * it.Proximity, it.Score, 12);
        }
    }

    [Fact]
    public void Caps_the_list_to_the_top_n()
    {
        GridModel model = Model();
        PowerFlowResult r = Solve(model);
        WatchListReport w = WatchList.Assess(model, r, new WatchListOptions { TopN = 1 });
        Assert.Single(w.Items);
        Assert.Equal("BIG", w.Items[0].Location);
    }

    [Fact]
    public void A_clear_system_has_an_empty_or_short_list()
    {
        // Lightly loaded, healthy voltages: nothing pressing.
        var model = new GridModel
        {
            BaseMva = 100,
            Name = "clear",
            Buses = new[] { new Bus(1, BusType.Slack, Vm: 1.0), new Bus(2, BusType.Pq, Pd: 10, Qd: 3) },
            Generators = new[] { new Generator(1, 0, Vg: 1.0, Qmin: -100, Qmax: 100) },
            Branches = new[] { new Branch(1, 2, 0.01, 0.05, 0.0, RateAMva: 200, Name: "L") },
        };
        WatchListReport w = WatchList.Assess(model, Solve(model));
        Assert.DoesNotContain(w.Items, it => it.Category == WatchCategory.ThermalOverload);
    }

    [Fact]
    public void A_protection_assessment_adds_the_thermal_lead_time()
    {
        GridModel model = Model();
        PowerFlowResult r = Solve(model);
        ProtectionReport prot = ProtectionAssessment.Assess(model, r);
        WatchListReport w = WatchList.Assess(model, r, protection: prot);

        WatchItem big = w.Items.First(x => x.Location == "BIG");
        Assert.NotNull(big.LeadTimeSeconds); // the thermal replica's time-to-trip
        Assert.True(big.LeadTimeSeconds > 0);
        Assert.Contains("trips in", big.Description);
    }
}
