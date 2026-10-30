using System.Linq;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Fault injection (<see cref="FaultAnalysis"/>) and the protection cascade (<see cref="ProtectionCascade"/>):
/// a balanced three-phase fault collapses the bus voltage and drives a large fault current that the
/// protection overlay's current/voltage/distance/differential elements operate on; and a single overload
/// propagates through the network as trips chain.
/// </summary>
public class FaultAndCascadeTests
{
    private static PowerFlowResult Solve(GridModel m) => new NewtonRaphsonPowerFlow().Solve(m);

    private static GridModel WithBranches(GridModel m, System.Collections.Generic.IReadOnlyList<Branch> branches) => new()
    {
        BaseMva = m.BaseMva, Buses = m.Buses, Generators = m.Generators, Branches = branches,
        Oltcs = m.Oltcs, Boundaries = m.Boundaries, FrequencyHz = m.FrequencyHz, Name = m.Name,
        AssetDynamics = m.AssetDynamics, Protections = m.Protections,
    };

    [Fact]
    public void A_bolted_three_phase_fault_collapses_the_bus_and_draws_a_large_current()
    {
        GridModel m = IeeeCases.Case14();
        PowerFlowResult pre = Solve(m);

        FaultResult f = FaultAnalysis.ThreePhase(m, pre, faultBusId: 4, faultReactancePu: 0.0);

        int idx = System.Array.IndexOf(f.State.BusIds.ToArray(), 4);
        Assert.True(f.State.Vm[idx] < 0.05, $"a bolted fault should collapse the bus voltage, got {f.State.Vm[idx]}");
        Assert.True(f.FaultCurrentPu > 1.0, $"fault current should be large, got {f.FaultCurrentPu}");
        Assert.True(f.DifferentialCurrentPuByBus.TryGetValue(4, out double d) && d > 1.0);
    }

    [Fact]
    public void A_higher_fault_impedance_limits_the_fault_current()
    {
        GridModel m = IeeeCases.Case14();
        PowerFlowResult pre = Solve(m);

        double bolted = FaultAnalysis.ThreePhase(m, pre, 4, 0.0).FaultCurrentPu;
        double resistive = FaultAnalysis.ThreePhase(m, pre, 4, 0.5).FaultCurrentPu;

        Assert.True(resistive < bolted, $"a fault impedance must reduce the fault current: {resistive} vs {bolted}");
        // And the bus no longer fully collapses.
        FaultResult f = FaultAnalysis.ThreePhase(m, pre, 4, 0.5);
        int idx = System.Array.IndexOf(f.State.BusIds.ToArray(), 4);
        Assert.True(f.State.Vm[idx] > 0.05, "an impedance fault leaves some voltage on the bus");
    }

    [Fact]
    public void The_faulted_state_operates_the_differential_and_distance_and_undervoltage_elements()
    {
        GridModel m = IeeeCases.Case14();
        PowerFlowResult pre = Solve(m);

        (FaultResult fault, ProtectionReport rep) = ProtectionAssessment.AssessThreePhaseFault(m, pre, 4);

        // 87 fires at the faulted bus with the fault current as its operating quantity.
        RelayVerdict diff = rep.Operating.First(v => v.Device == "87");
        Assert.Equal(ProtectionLocation.Bus, diff.LocationKind);
        Assert.Equal(4, diff.LocationId);
        Assert.True(diff.Quantity > 1.0);
        // The depressed network trips undervoltage, and a distance zone picks up on a faulted-branch relay.
        Assert.Contains(rep.Operating, v => v.Device == "27");
        Assert.Contains(rep.Operating, v => v.Device.StartsWith("21", System.StringComparison.Ordinal));
    }

    [Fact]
    public void An_unstressed_case_does_not_cascade()
    {
        GridModel m = IeeeCases.Case14();
        CascadeResult cas = ProtectionCascade.Simulate(m, Solve(m));
        Assert.False(cas.Cascaded);
    }

    [Fact]
    public void A_branch_rated_below_its_flow_starts_a_cascade_that_opens_that_branch_first()
    {
        GridModel m = IeeeCases.Case14();
        PowerFlowResult probe = Solve(m);
        LineFlow busiest = probe.LineFlows.OrderByDescending(
            l => l.PFromMw * l.PFromMw + l.QFromMvar * l.QFromMvar).First();
        double sMva = System.Math.Sqrt(busiest.PFromMw * busiest.PFromMw + busiest.QFromMvar * busiest.QFromMvar);

        var branches = m.Branches.Select(br =>
            br.From == busiest.From && br.To == busiest.To ? br with { RateAMva = 0.4 * sMva } : br).ToList();
        GridModel stressed = WithBranches(m, branches);

        CascadeResult cas = ProtectionCascade.Simulate(stressed, Solve(stressed));

        Assert.True(cas.Cascaded, "an overloaded branch should start a cascade");
        CascadeStep first = cas.Steps[0];
        Assert.Equal(busiest.From, first.From);
        Assert.Equal(busiest.To, first.To);
        Assert.True(first.Device is "49" or "50" or "51" || first.Device.StartsWith("21", System.StringComparison.Ordinal));
    }
}
