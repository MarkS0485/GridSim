using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;

namespace GridSim.Tests;

/// <summary>
/// Small deterministic models + a solve helper shared by the web anti-drift / regression suites. Kept in-code
/// (not a bundled case) so the fleet composition is explicit: the anti-drift tests need a KNOWN fossil +
/// converter mix to prove the carbon / largest-loss projections consume the shared Core presenters.
/// </summary>
internal static class WebProjectionFixtures
{
    /// <summary>A two-bus fleet: a synchronous CCGT, a smaller synchronous Coal unit, and a large converter wind
    /// farm. All at the slack bus, feeding one PQ load - enough to solve and to exercise every fuel class. The
    /// wind farm is deliberately the BIGGEST unit by MW. Before B4 N039 (2026-10) it was excluded from the largest
    /// loss (converters were not counted) and the 1,500 MW CCGT was the credible loss; since N039 every single infeed
    /// counts (SQSS v2.10 para 5.1.2: "a single power park module"), so the 2,000 MW wind farm is.</summary>
    public static GridModel MixedFleet() => new()
    {
        BaseMva = 100,
        Buses = new[]
        {
            new Bus(1, BusType.Slack, BaseKv: 400),
            new Bus(2, BusType.Pq, Pd: 2500, BaseKv: 400),
        },
        Branches = new[] { new Branch(1, 2, 0.01, 0.1) },
        Generators = new[]
        {
            new Generator(1, 1500, MBase: 1800, H: 5, Type: "CCGT", Name: "Big CCGT"),
            new Generator(1, 900,  MBase: 1000, H: 4, Type: "Coal", Name: "Coal A"),
            new Generator(1, 2000, MBase: 2200, H: 0, Type: "wind-offshore", Name: "Wind farm"),
        },
    };

    /// <summary>A single-fuel Coal fleet: the whole-grid carbon intensity must collapse to that fuel's factor
    /// regardless of the exact solved MW (the numerator/denominator cancel), giving a hard deterministic value.</summary>
    public static GridModel CoalOnlyFleet() => new()
    {
        BaseMva = 100,
        Buses = new[]
        {
            new Bus(1, BusType.Slack, BaseKv: 400),
            new Bus(2, BusType.Pq, Pd: 1000, BaseKv: 400),
        },
        Branches = new[] { new Branch(1, 2, 0.01, 0.1) },
        Generators = new[] { new Generator(1, 1000, MBase: 1200, H: 4, Type: "Coal", Name: "Coal A") },
    };

    /// <summary>Solve to steady state and wrap the regulated model in a <see cref="SimFrame"/> (freq 50 Hz),
    /// mirroring how the web session builds a frame before projecting it.</summary>
    public static SimFrame Solve(GridModel model)
    {
        var solver = new SteadyStateSolver();
        PowerFlowResult r = solver.Solve(model);
        GridModel solved = solver.RegulatedModel;
        double gen = solved.Generators.Sum(g => g.Pg);
        double demand = solved.Buses.Sum(b => b.Pd);
        return new SimFrame(solved, r, Hour: 0, Tick: 0, FrequencyHz: 50, GenerationMw: gen, DemandMw: demand);
    }
}
