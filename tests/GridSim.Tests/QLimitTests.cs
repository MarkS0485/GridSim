using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

public class QLimitTests
{
    private static GridModel Case9WithGen2Qmax(double qmax)
    {
        var b = IeeeCases.Case9();
        var gens = b.Generators.Select(g => g.Bus == 2 ? g with { Qmax = qmax, Qmin = -300 } : g).ToList();
        return new GridModel
        {
            Name = b.Name, BaseMva = b.BaseMva, FrequencyHz = b.FrequencyHz,
            Buses = b.Buses, Generators = gens, Branches = b.Branches,
        };
    }

    [Fact]
    public void Pv_bus_holds_voltage_when_within_q_limits()
    {
        // Generous limit: bus 2 stays PV at its 1.025 setpoint (the published case9 result).
        var r = new NewtonRaphsonPowerFlow().Solve(Case9WithGen2Qmax(300));
        Assert.True(r.Converged);
        Assert.Equal(1.025, r.Vm[1], 3);
    }

    [Fact]
    public void Pv_bus_switches_to_pq_and_pins_q_at_the_limit()
    {
        // Bus 2 normally supplies ~+6.5 MVAr; cap it at 0 and it must release voltage.
        var r = new NewtonRaphsonPowerFlow().Solve(Case9WithGen2Qmax(0.0));
        Assert.True(r.Converged);
        Assert.True(r.Vm[1] < 1.025, $"expected voltage to drop below setpoint, got {r.Vm[1]:F4}");
        Assert.Equal(0.0, r.QInjMvar[1], 1); // reactive pinned at the 0 MVAr limit
    }

    [Fact]
    public void Disabling_enforcement_lets_q_exceed_the_limit()
    {
        var opt = new PowerFlowOptions { EnforceQLimits = false };
        var r = new NewtonRaphsonPowerFlow().Solve(Case9WithGen2Qmax(0.0), opt);
        Assert.True(r.Converged);
        Assert.Equal(1.025, r.Vm[1], 3);       // still held
        Assert.True(r.QInjMvar[1] > 1.0);      // limit ignored
    }
}
