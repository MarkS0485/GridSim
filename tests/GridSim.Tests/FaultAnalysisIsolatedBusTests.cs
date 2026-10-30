using System;
using System.Linq;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-135: an isolated bus (no branch, load, shunt or machine) made the operating-point fault admittance singular, so a
/// fault anywhere failed. It is now grounded through a unit admittance, decoupled from the rest, so a fault elsewhere
/// gives the connected network's answer; a fault on the isolated bus itself is refused by name.
/// </summary>
public class FaultAnalysisIsolatedBusTests
{
    private static (GridModel Plain, GridModel WithIsolated, PowerFlowResult Pf) Cases()
    {
        GridModel c = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(c);
        Assert.True(pf.Converged);
        var withIsolated = new GridModel
        {
            BaseMva = c.BaseMva, Branches = c.Branches, Generators = c.Generators, Name = c.Name,
            Buses = c.Buses.Concat(new[] { new Bus(99, BusType.Pq, BaseKv: 230) }).ToList(),
        };
        return (c, withIsolated, pf);
    }

    [Fact]
    public void A_fault_elsewhere_ignores_an_isolated_bus()
    {
        (GridModel plain, GridModel withIsolated, PowerFlowResult pf) = Cases();
        FaultResult a = FaultAnalysis.ThreePhase(plain, pf, faultBusId: 5);
        FaultResult b = FaultAnalysis.ThreePhase(withIsolated, pf, faultBusId: 5);
        Assert.Equal(a.FaultCurrentPu, b.FaultCurrentPu, 12);
        Assert.Equal(a.TheveninZPu, b.TheveninZPu, 12);
        for (int i = 0; i < a.State.Vm.Length; i++) Assert.Equal(a.State.Vm[i], b.State.Vm[i], 12);
    }

    [Fact]
    public void A_fault_on_the_isolated_bus_is_refused_by_name()
    {
        (_, GridModel withIsolated, PowerFlowResult pf) = Cases();
        var ex = Assert.Throws<InvalidOperationException>(() => FaultAnalysis.ThreePhase(withIsolated, pf, faultBusId: 99));
        Assert.Contains("Fault bus 99 is isolated", ex.Message, StringComparison.Ordinal);
    }
}
