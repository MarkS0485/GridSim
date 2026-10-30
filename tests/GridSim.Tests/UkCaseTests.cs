using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

public class UkCaseTests
{
    private static string UkDir => Path.Combine(AppContext.BaseDirectory, "data", "uk");

    private static GridModel Load() => JsonGridLoader.LoadDirectory(UkDir);

    [Fact]
    public void Loads_the_expected_shape()
    {
        var m = Load();
        Assert.Equal(20, m.BusCount);                 // 10 zones x (400 kV + 132 kV)
        Assert.Equal(29, m.Generators.Count);         // plants + the 10 named interconnectors
        Assert.Equal(24, m.Branches.Count);           // 14 lines + 10 transformers
        Assert.Equal(10, m.Oltcs.Count);
        Assert.Equal(1, m.Buses.Count(b => b.Type == BusType.Slack));
    }

    [Fact]
    public void Reaches_a_stable_steady_state()
    {
        var m = Load();
        var solver = new SteadyStateSolver();
        var r = solver.Solve(m);

        Assert.True(r.Converged, $"did not converge (mismatch {r.MaxMismatch:E2})");
        Assert.True(r.MaxMismatch < 1e-6);
    }

    [Fact]
    public void All_voltages_are_within_the_statutory_band()
    {
        var r = new SteadyStateSolver().Solve(Load());
        Assert.All(r.Vm, v => Assert.InRange(v, 0.94, 1.06));
    }

    [Fact]
    public void Oltc_holds_the_132kv_demand_buses_near_target()
    {
        var m = Load();
        var r = new SteadyStateSolver().Solve(m);
        var vmById = new Dictionary<int, double>();
        for (int i = 0; i < r.BusIds.Count; i++) vmById[r.BusIds[i]] = r.Vm[i];

        // Every 132 kV demand bus (id 101..110) should sit close to 1.0 p.u.
        foreach (Bus b in m.Buses.Where(b => b.BaseKv < 200))
            Assert.InRange(vmById[b.Id], 0.97, 1.03);
    }

    [Fact]
    public void Generation_balances_demand_plus_losses()
    {
        var m = Load();
        var r = new SteadyStateSolver().Solve(m);
        double demand = m.Buses.Sum(b => b.Pd);
        double netInjection = r.PInjMw.Sum(); // = losses
        Assert.Equal(r.TotalLossMw, netInjection, 3);
        Assert.True(r.TotalLossMw > 0 && r.TotalLossMw < 0.05 * demand, $"losses {r.TotalLossMw:F1} MW look wrong");
    }

    [Fact]
    public void Slack_dispatch_stays_within_capacity()
    {
        var m = Load();
        var r = new SteadyStateSolver().Solve(m);
        int slackIdx = m.Buses.ToList().FindIndex(b => b.Type == BusType.Slack);
        double slackBusId = m.Buses[slackIdx].Id;
        double slackCap = m.Generators.Where(g => g.Bus == slackBusId).Sum(g => g.MBase);
        Assert.InRange(r.PInjMw[slackIdx], 0, slackCap);
    }

    [Fact]
    public void Named_interconnectors_are_present_and_converter_connected()
    {
        var m = Load();
        var links = m.Generators.Where(g => g.Type == "interconnector").ToList();

        string[] expected = { "Moyle", "North Sea Link", "EWIC", "Viking Link", "IFA",
                              "ElecLink", "NemoLink", "BritNed", "IFA2", "Greenlink" };
        Assert.Equal(expected.OrderBy(n => n), links.Select(l => l.Name).OrderBy(n => n));

        Assert.All(links, l => Assert.Equal(0, l.H));            // no rotating mass
        Assert.Contains(links, l => l.Pg < 0);                    // Moyle exports in the snapshot
        Assert.All(links, l => Assert.True(Math.Abs(l.Pg) <= l.Pmax, $"{l.Name} beyond capacity"));
    }

    [Fact]
    public void System_inertia_is_reported()
    {
        var m = Load();
        Assert.True(m.SystemInertiaGvaS > 100);  // synchronous-heavy snapshot
        Assert.True(m.SystemInertiaGvaS < 300);
    }
}
