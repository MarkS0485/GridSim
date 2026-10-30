using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Web.Projection;
using GridSim.Web.Sessions;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R017: the map trip (web right-click / T key, and the WPF twin) must remove the largest unit that is GENERATING
/// now, by live Pg, and never a power-flow-only device. Before the fix the web map sized the trip from the topology
/// fleet's NAMEPLATE (MBase), so on gb-full the T key tripped the 266,425 MVA national slack balancer, and a
/// right-click on a bus with no fleet record fell back to that same system maximum.
/// </summary>
public class InfeedSelectionTests
{
    private static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>A small model built so that nameplate and live output disagree: the slack balancer has the
    /// biggest nameplate AND (after a solve) real output, a voltage-control device has a big nameplate at 0 MW,
    /// and two real units tie on output at bus 3 so the tie-break is visible.</summary>
    private static GridModel Mixed() => new()
    {
        BaseMva = 100,
        Buses = new[]
        {
            new Bus(1, BusType.Slack, BaseKv: 400, Name: "Slack bus"),
            new Bus(2, BusType.Pq, Pd: 900, BaseKv: 400, Name: "Load bus"),
            new Bus(3, BusType.Pv, BaseKv: 400, Name: "Plant bus"),
            new Bus(4, BusType.Pq, BaseKv: 400, Name: "Empty bus"),
        },
        Branches = new[] { new Branch(1, 2, 0.01, 0.1), new Branch(2, 3, 0.01, 0.1), new Branch(2, 4, 0.01, 0.1) },
        Generators = new[]
        {
            new Generator(1, 5000, MBase: 266_425, Type: "grid", Name: "National slack", PowerFlowOnly: true),
            new Generator(4, 0, MBase: 2_880, Type: "grid", Name: "Voltage control", PowerFlowOnly: true),
            new Generator(3, 400, MBase: 450, H: 5, Type: "CCGT", Name: "Unit A"),
            new Generator(3, 400, MBase: 900, H: 5, Type: "CCGT", Name: "Unit B"),
            new Generator(3, 150, MBase: 200, H: 4, Type: "OCGT", Name: "Unit C"),
        },
    };

    [Fact]
    public void Largest_online_uses_live_output_and_skips_power_flow_only_devices()
    {
        (Generator Unit, int Index)? pick = InfeedSelection.LargestOnline(Mixed());

        // The slack (5,000 MW output, 266,425 MVA nameplate) and the voltage-control unit are excluded; Unit A and
        // Unit B tie at 400 MW and model order wins, so Unit A (index 2) is chosen although Unit B has the bigger
        // nameplate. Exact equality: this is a selection, not a computation.
        Assert.NotNull(pick);
        Assert.Equal("Unit A", pick.Value.Unit.Name);
        Assert.Equal(2, pick.Value.Index);
        Assert.Equal(400.0, pick.Value.Unit.Pg);
    }

    [Fact]
    public void Largest_online_at_a_bus_with_no_generation_is_null_not_a_fallback()
    {
        // Bus 4 carries only a 0 MW power-flow-only device: there is nothing to trip there, and the answer must
        // say so (null) rather than fall back to the system-wide largest as the old web map did.
        Assert.Null(InfeedSelection.LargestOnline(Mixed(), busId: 4));
        Assert.Null(InfeedSelection.LargestOnline(Mixed(), busId: 2));   // a load bus, no units at all
        Assert.Null(InfeedSelection.LargestOnline(Mixed(), busId: 1));   // only the slack balancer
    }

    [Fact]
    public void Largest_online_ignores_non_finite_and_non_positive_output()
    {
        var m = new GridModel
        {
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 400) },
            Branches = Array.Empty<Branch>(),
            Generators = new[]
            {
                new Generator(1, double.NaN, Type: "CCGT", Name: "Corrupt"),
                new Generator(1, -50, Type: "battery", Name: "Charging"),
                new Generator(1, 0, Type: "CCGT", Name: "Off"),
            },
        };
        Assert.Null(InfeedSelection.LargestOnline(m));
    }

    [Fact]
    public void Gb_full_trip_is_a_real_generating_unit_not_the_266_GW_slack()
    {
        // The R017 failure scenario on the real case: load gb-full, solve it, and pick the system-wide trip.
        GridModel model = JsonGridLoader.LoadDirectory(Path.Combine(DataDir, "gb-full"));
        var solver = new SteadyStateSolver();
        solver.Solve(model);
        GridModel solved = solver.RegulatedModel;

        // What the old web T key used: the largest topology-fleet Mw, i.e. nameplate MBase.
        TopologyDto topo = FrameProjector.BuildTopology("gb-full", "gb-full", solved, replay: false, frameCount: 0);
        double oldTripMw = topo.Fleet.Max(g => g.Mw);
        Assert.Equal(266_424.9878610732, oldTripMw);           // the slack balancer's nameplate, as the finding says

        (Generator Unit, int Index)? pick = InfeedSelection.LargestOnline(solved);
        Assert.NotNull(pick);
        Generator unit = pick.Value.Unit;
        Assert.False(unit.PowerFlowOnly);
        // It is the largest live output among physical units, computed independently here.
        double expected = solved.Generators.Where(g => !g.PowerFlowOnly && g.Pg > 0).Max(g => g.Pg);
        Assert.Equal(expected, unit.Pg);
        Assert.Equal("Drax Power Station biomass", unit.Name);
        // The slack is flagged so the map skips it too (fleet marker scale and fly-out capacity row).
        Assert.Contains(topo.Fleet, f => f.PowerFlowOnly && f.Mw == 266_424.9878610732);
    }

    [Fact]
    public async Task Live_session_trip_reports_the_unit_it_removed()
    {
        // IEEE 9-bus held steady: the session picks from its latest solved frame by the same Core rule.
        GridModel case9 = IeeeCases.Case9();
        SimSession s = SimSession.CreateLive("t", "case9", "IEEE 9", case9, holdSteady: true);
        try
        {
            Assert.True(s.CanTrip);
            SimSession.TripOutcome all = s.TripLargestInfeed(null);
            Assert.True(all.Tripped, all.Message);
            Assert.True(all.LostMw > 0);
            Assert.StartsWith("TRIP ", all.Message);

            // A load-only bus has nothing to trip: the outcome says so and nothing is injected.
            int loadBus = case9.Buses.First(b => b.Pd > 0 && case9.Generators.All(g => g.Bus != b.Id)).Id;
            SimSession.TripOutcome none = s.TripLargestInfeed(loadBus);
            Assert.False(none.Tripped);
            Assert.StartsWith("No generation to trip at ", none.Message);
            Assert.Equal(0.0, none.LostMw);
        }
        finally
        {
            await s.DisposeAsync();
        }
    }
}
