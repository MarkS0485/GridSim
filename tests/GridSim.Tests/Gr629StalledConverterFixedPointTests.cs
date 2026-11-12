using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-629: the test gap the review named was "an assessment on a converter case that cannot settle" (the breaker
/// duration half is covered by Iec60909ShortFaultThermalTests). The operating-point fault study's converter current
/// fixed point is stopped unsettled on purpose (FaultAnalysis.ThreePhaseCapped with a one-pass cap): the result must say
/// so (ConvertersConverged false, the faulted state MaxIterations and not Converged), and the protection assessment,
/// which AssessThreePhaseFault runs on exactly that state, must refuse it rather than grade relays on it.
/// </summary>
public class Gr629StalledConverterFixedPointTests
{
    // Synchronous source at bus 1, a 0.1 pu line to bus 2, a 100 MVA battery converter exporting 50 MW at bus 2.
    private static GridModel Radial() => new()
    {
        BaseMva = 100.0,
        Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 132.0), new Bus(2, BusType.Pv, BaseKv: 132.0) },
        Generators = new[]
        {
            new Generator(Bus: 1, Pg: 0.0, MBase: 100.0, Name: "grid", Type: "ccgt"),
            new Generator(Bus: 2, Pg: 50.0, MBase: 100.0, Name: "unit", Type: "battery"),
        },
        Branches = new[] { new Branch(1, 2, R: 0.0, X: 0.10) },
    };

    private static PowerFlowResult Flat(GridModel m) => new()
    {
        Converged = true, Iterations = 0, MaxMismatch = 0,
        BusIds = m.Buses.Select(b => b.Id).ToArray(),
        Vm = m.Buses.Select(_ => 1.0).ToArray(),
        VaDeg = m.Buses.Select(_ => 0.0).ToArray(),
        PInjMw = new double[m.BusCount], QInjMvar = new double[m.BusCount],
        TotalLossMw = 0, TotalLossMvar = 0, LineFlows = Array.Empty<LineFlow>(),
    };

    [Fact]
    public void Gr629_a_converter_fixed_point_stopped_unsettled_gives_a_non_converged_fault_state()
    {
        GridModel m = Radial();
        FaultResult r = FaultAnalysis.ThreePhaseCapped(m, Flat(m), faultBusId: 2, faultReactancePu: 0.0,
            FaultAnalysis.DefaultSubtransientXdPu, FaultAnalysis.DefaultConverterReactiveGain, converterMaxIterations: 1);

        Assert.False(r.ConvertersConverged);
        Assert.False(r.State.Converged);
        Assert.Equal(PowerFlowStatus.MaxIterations, r.State.Status);
        Assert.Single(r.Converters!);
    }

    [Fact]
    public void Gr629_the_protection_assessment_refuses_the_unsettled_fault_state()
    {
        GridModel m = Radial();
        FaultResult r = FaultAnalysis.ThreePhaseCapped(m, Flat(m), faultBusId: 2, faultReactancePu: 0.0,
            FaultAnalysis.DefaultSubtransientXdPu, FaultAnalysis.DefaultConverterReactiveGain, converterMaxIterations: 1);
        // What AssessThreePhaseFault does with the study's state, step for step.
        Assert.ThrowsAny<InvalidOperationException>(() =>
            ProtectionAssessment.Assess(m, r.State, null, null, r.DifferentialCurrentPuByBus));
    }

    [Fact]
    public void Gr629_the_default_cap_settles_the_same_case_and_matches_the_public_entry_bit_for_bit()
    {
        GridModel m = Radial();
        FaultResult capped = FaultAnalysis.ThreePhaseCapped(m, Flat(m), faultBusId: 2, faultReactancePu: 0.0,
            FaultAnalysis.DefaultSubtransientXdPu, FaultAnalysis.DefaultConverterReactiveGain,
            FaultAnalysis.ConverterFixedPointMaxIterations);
        FaultResult open = FaultAnalysis.ThreePhase(m, Flat(m), faultBusId: 2);
        Assert.True(open.ConvertersConverged);
        Assert.Equal(open.FaultCurrentPu, capped.FaultCurrentPu);
        Assert.Equal(open.State.Vm, capped.State.Vm);
        Assert.Equal(1000, FaultAnalysis.ConverterFixedPointMaxIterations);
        Assert.Throws<ArgumentOutOfRangeException>(() => FaultAnalysis.ThreePhaseCapped(m, Flat(m), 2, 0.0,
            FaultAnalysis.DefaultSubtransientXdPu, FaultAnalysis.DefaultConverterReactiveGain, converterMaxIterations: 0));
    }
}
