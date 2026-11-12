using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-622: a bus of the model that the prefault result does not carry used to take its stored case-file voltage, so
/// a dead bus (and any machine on it) became a source in the operating-point fault study. Such a bus is now
/// de-energised: 0 V, no load, no machine contribution; a fault ON it is refused by name. Both the balanced and the
/// unbalanced studies.
/// </summary>
public class Gr622DeEnergisedPrefaultBusTests
{
    private const int DeadBus = 100;

    // case9 plus bus 100 carrying a load and a copy of one of case9's synchronous machines; the prefault result is
    // case9's own solve, which has no bus 100.
    private static (GridModel Extended, GridModel Case9, PowerFlowResult Prefault) Setup()
    {
        GridModel c9 = IeeeCases.Case9();
        PowerFlowResult pre = new NewtonRaphsonPowerFlow().Solve(c9);
        Assert.True(pre.Converged);
        Bus dead = new(DeadBus, BusType.Pq, Pd: 20, Qd: 5, BaseKv: c9.Buses[0].BaseKv, Vm: 1.02, VaDeg: 3.0);
        GridModel ext = c9.CopyWith(
            buses: c9.Buses.Append(dead).ToList(),
            generators: c9.Generators.Append(c9.Generators[1] with { Bus = DeadBus }).ToList());
        Assert.DoesNotContain(DeadBus, pre.BusIds);
        return (ext, c9, pre);
    }

    [Fact]
    public void Gr622_a_bus_missing_from_the_prefault_result_is_de_energised_in_the_balanced_study()
    {
        (GridModel ext, GridModel c9, PowerFlowResult pre) = Setup();
        int faultBus = c9.Buses[4].Id;

        FaultResult withDead = FaultAnalysis.ThreePhase(ext, pre, faultBus);
        FaultResult reference = FaultAnalysis.ThreePhase(c9, pre, faultBus);

        int at = withDead.State.BusIds.ToList().IndexOf(DeadBus);
        Assert.Equal(0.0, withDead.State.Vm[at]);   // was its stored 1.02 pu, held up by its own machine
        // The dead, unconnected bus adds nothing to the connected network's fault.
        Assert.True(Math.Abs(withDead.FaultCurrentPu - reference.FaultCurrentPu) <= 1e-12 * reference.FaultCurrentPu,
            $"fault current {withDead.FaultCurrentPu:R} vs {reference.FaultCurrentPu:R} without the dead bus");
    }

    [Fact]
    public void Gr622_a_fault_on_a_de_energised_bus_is_refused_by_name()
    {
        (GridModel ext, _, PowerFlowResult pre) = Setup();
        var ex = Assert.Throws<InvalidOperationException>(() => FaultAnalysis.ThreePhase(ext, pre, DeadBus));
        Assert.Contains($"Fault bus {DeadBus} is not in the prefault result", ex.Message, StringComparison.Ordinal);
        var ex2 = Assert.Throws<InvalidOperationException>(() => FaultAnalysis.Unbalanced(ext, pre, DeadBus, FaultType.LineToLine));
        Assert.Contains($"Fault bus {DeadBus} is not in the prefault result", ex2.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Gr622_the_unbalanced_study_also_leaves_the_de_energised_bus_at_zero()
    {
        (GridModel ext, GridModel c9, PowerFlowResult pre) = Setup();
        int faultBus = c9.Buses[4].Id;
        UnbalancedFaultResult r = FaultAnalysis.Unbalanced(ext, pre, faultBus, FaultType.LineToLine);
        int at = r.BusIds.ToList().IndexOf(DeadBus);
        Assert.Equal(System.Numerics.Complex.Zero, r.V1[at]);
        Assert.Equal(System.Numerics.Complex.Zero, r.V2[at]);
    }
}
