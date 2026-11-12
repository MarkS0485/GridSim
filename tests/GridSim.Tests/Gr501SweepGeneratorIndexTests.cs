using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-501: the sweep's result assembly scanned the whole generator fleet for every bus (SpecGenP / SpecGenQ and the PV
/// test), O(buses x units). The units are now indexed by bus once. The sums keep their order (each bus's units in model
/// order, from 0.0, exactly what Enumerable.Sum did), so every number is bit-for-bit unchanged; the existing sweep pins
/// (case33bw, CIGRE MV, the weak-mesh test) hold that, and these check the indexing itself.
/// </summary>
public class Gr501SweepGeneratorIndexTests
{
    private static string Source()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, "GridSim.slnx not found");
        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "GridSim.Core", "PowerFlow", "BackwardForwardSweep.cs"));
    }

    [Fact]
    public void Gr501_result_assembly_no_longer_scans_the_fleet_per_bus()
    {
        string src = Source();
        Assert.DoesNotContain("m.Generators.Where(g => index.TryGetValue(g.Bus, out int gi) && gi == busIdx)", src);
        Assert.DoesNotContain("model.Generators.Any(g => index.TryGetValue(g.Bus, out int gi) && gi == i)", src);
        Assert.Contains("GeneratorsByBus(model, index, n)", src);
    }

    [Fact]
    public void Gr501_co_located_units_are_summed_in_model_order_on_a_radial_feeder()
    {
        // A three-bus radial feeder with two PQ units on bus 3 (10 MW + 5 MW) and a load of 20 MW there. The sweep's
        // net injection at bus 3 must meet the specified 15 - 20 = -5 MW (the sum of BOTH units), within its tolerance.
        var m = new GridModel
        {
            BaseMva = 100,
            Buses = new[]
            {
                new Bus(1, BusType.Slack, BaseKv: 11), new Bus(2, BusType.Pq, Pd: 1, BaseKv: 11),
                new Bus(3, BusType.Pq, Pd: 20, Qd: 2, BaseKv: 11),
            },
            Branches = new[] { new Branch(1, 2, 0.01, 0.02), new Branch(2, 3, 0.01, 0.02) },
            Generators = new[] { new Generator(1, 0), new Generator(3, 10.0), new Generator(3, 5.0) },
        };
        PowerFlowResult r = new BackwardForwardSweep().Solve(m);
        Assert.True(r.Converged, $"sweep status {r.Status}");
        int i3 = r.BusIds.ToList().IndexOf(3);
        Assert.True(Math.Abs(r.PInjMw[i3] - (10.0 + 5.0 - 20.0)) < 1e-4, $"bus 3 P {r.PInjMw[i3]:F8} MW");
        Assert.True(Math.Abs(r.QInjMvar[i3] - (0.0 - 2.0)) < 1e-4, $"bus 3 Q {r.QInjMvar[i3]:F8} MVAr");
    }
}
