using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-501 (second loop): the sweep's PV-bus setup in Solve also scanned the whole generator fleet for every PV bus to
/// total each bus's nominal Q and its Q limits. It now reads the per-bus unit index built once. The totals add the same
/// units in the same order from 0, so every number is bit-for-bit unchanged (the existing sweep pins hold that); these
/// check the indexing itself.
/// </summary>
public class Gr501SweepPvSetupIndexTests
{
    private static string Source()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, "GridSim.slnx not found");
        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "GridSim.Core", "PowerFlow", "BackwardForwardSweep.cs"));
    }

    [Fact]
    public void Gr501_the_pv_setup_no_longer_scans_the_fleet_per_bus()
    {
        string src = Source();
        Assert.DoesNotContain("if (!index.TryGetValue(g.Bus, out int gi) || gi != i) continue;", src);
        Assert.Contains("List<Generator>?[] unitsAt = GeneratorsByBus(model, index, n);", src);
    }

    [Fact]
    public void Gr501_two_co_located_units_regulate_a_pv_bus_with_their_summed_limits()
    {
        // A radial feeder with a PV bus at the end holding 1.0 pu through two units whose Q limits add up. The sweep must
        // hold the set-point (the summed limits are wide enough) and report the bus at it.
        var m = new GridModel
        {
            BaseMva = 100,
            Buses = new[]
            {
                new Bus(1, BusType.Slack, BaseKv: 11), new Bus(2, BusType.Pq, Pd: 1, BaseKv: 11),
                new Bus(3, BusType.Pv, Pd: 5, Qd: 2, BaseKv: 11),
            },
            Branches = new[] { new Branch(1, 2, 0.01, 0.02), new Branch(2, 3, 0.01, 0.02) },
            Generators = new[]
            {
                new Generator(1, 0),
                new Generator(3, 2.0, Vg: 1.0, Qmin: -3.0, Qmax: 3.0),
                new Generator(3, 2.0, Vg: 1.0, Qmin: -3.0, Qmax: 3.0),
            },
        };
        PowerFlowResult r = new BackwardForwardSweep().Solve(m, new PowerFlowOptions { EnforceQLimits = true });
        Assert.True(r.Converged, $"sweep status {r.Status}");
        int i3 = r.BusIds.ToList().IndexOf(3);
        Assert.True(Math.Abs(r.Vm[i3] - 1.0) < 1e-4, $"PV bus 3 at {r.Vm[i3]:F8} pu, set-point 1.0");
    }
}
